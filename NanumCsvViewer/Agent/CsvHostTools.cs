using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Agent
{
    /// <summary>
    /// csv.* host tool 실행기. 인자 검증·데이터 정책·승인 카드·결과 JSON·기록을 맡고, 실제 창 조작은 ICsvAgentHost에 위임한다.
    ///
    /// 데이터 정책(AgentHostOptions.DataPolicy):
    ///  - SummaryOnly(기본): csv.get_rows 거절. 다른 도구 결과에는 원시 셀 값이 없다 — 컬럼 이름·타입·개수·수치 집계(평균·분위수·최소/최대)와
    ///    분석 결과(수준 이름 포함)만. column_stats의 상위 빈도 값은 생략, quality_scan의 예시 값·위장결측 후보 값·상수 값도 생략(행 번호만).
    ///  - RowsWithApproval: csv.get_rows와 top_values가 있는 column_stats는 요청마다 승인 카드. quality_scan 예시 값은 생략.
    ///  - RowsAllowed: csv.get_rows 승인 없이 MaxRowsPerRequest까지. top_values·품질 예시 값도 포함.
    /// 편집·저장은 정책과 무관하게 항상 승인 카드.
    /// </summary>
    public sealed class CsvHostTools : ICsvToolExecutor
    {
        private const int MaxResultChars = 60_000;
        private const int RowsOutputBudgetChars = 30_000;
        private const int MaxCellChars = 200;
        private const int MaxEditsPerCall = 500;
        private const int MaxEditValueChars = 32_768;

        private readonly ICsvAgentHost _host;
        private readonly Func<AgentHostOptions> _options;
        private readonly IAgentToolLog _log;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public CsvHostTools(ICsvAgentHost host, Func<AgentHostOptions> options)
            : this(host, options, AgentToolLog.Default) { }

        public CsvHostTools(ICsvAgentHost host, Func<AgentHostOptions> options, IAgentToolLog log)
        {
            _host = host;
            _options = options;
            _log = log;
        }

        public IReadOnlyList<HostToolDefinition> Definitions => ToolDefinitions.All;

        public async Task<HostToolResult> ExecuteAsync(HostToolCall call, IAgentApprovals approvals, CancellationToken cancellation)
        {
            var sw = Stopwatch.StartNew();
            HostToolResult result;
            try
            {
                // 도구는 한 번에 하나씩: 모델이 병렬 호출해도 창 상태(필터·정렬·편집)가 섞이지 않는다.
                await _gate.WaitAsync(cancellation);
                try
                {
                    result = await DispatchAsync(call, approvals, cancellation);
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException)
            {
                result = HostToolResult.Error("Cancelled: the user stopped the turn or closed the window before the tool finished.");
            }
            catch (AgentToolException ex)
            {
                result = HostToolResult.Error(ex.Message);
            }
            catch (Exception ex) when (IsUserInputError(ex))
            {
                result = HostToolResult.Error(ex.Message);
            }
            catch (Exception ex)
            {
                result = HostToolResult.Error($"Unexpected error in {call.ToolName}: {ex.GetType().Name}: {ex.Message}");
            }

            sw.Stop();
            _log.Append(call.ToolName, SafeLogArgs(call), !result.IsError, sw.ElapsedMilliseconds, result.IsError ? result.Text : null);
            return result;
        }

        /// <summary>통계 엔진이 사용자(모델) 입력 문제로 던지는 예외 — 메시지를 그대로 알려 고치게 한다.</summary>
        private static bool IsUserInputError(Exception ex)
            => ex is DesignMatrixException or AnalysisMemoryLimitException or FormulaParseException or AdvancedFilterExpressionException;

        private async Task<HostToolResult> DispatchAsync(HostToolCall call, IAgentApprovals approvals, CancellationToken ct)
        {
            var args = ToolArgs.From(call.Arguments);
            return call.ToolName switch
            {
                ToolDefinitions.Info => Info(),
                ToolDefinitions.ColumnStats => await ColumnStatsAsync(args, approvals, ct),
                ToolDefinitions.GetRows => await GetRowsAsync(args, approvals, ct),
                ToolDefinitions.SetFilter => await SetFilterAsync(args, ct),
                ToolDefinitions.ClearFilter => await ClearFilterAsync(ct),
                ToolDefinitions.Sort => await SortAsync(args, ct),
                ToolDefinitions.Goto => await GotoAsync(args, ct),
                ToolDefinitions.RunAnalysis => await RunAnalysisAsync(args, ct),
                ToolDefinitions.QualityScan => await QualityScanAsync(args, ct),
                ToolDefinitions.EditCells => await EditCellsAsync(args, approvals, ct),
                ToolDefinitions.Undo => Undo(),
                ToolDefinitions.SaveEditsAs => await SaveEditsAsAsync(args, approvals, ct),
                _ => HostToolResult.Error($"Unknown tool '{call.ToolName}'. Available: {string.Join(", ", ToolDefinitions.All.Select(d => d.Name))}."),
            };
        }

        private AgentHostOptions Options() => _options();
        private bool Korean => string.Equals(Options().Language, "ko", StringComparison.OrdinalIgnoreCase);
        private string L(string en, string ko) => Korean ? ko : en;

        private AgentDocumentInfo RequireReady()
        {
            var info = _host.GetInfo() ?? throw new AgentToolException("No file is open in Nanum CSV Viewer. Ask the user to open one.");
            if (!info.IndexingComplete)
                throw new AgentToolException($"The file is still being indexed ({info.IndexingPercent}%). Retry in a few seconds.");
            if (info.Busy)
                throw new AgentToolException("The viewer is busy with another operation (filter, sort, analysis or save). Retry in a moment.");
            return info;
        }

        private static HostToolResult Reply(string summary, JsonObject json)
        {
            string text = summary + "\n" + ToolJson.Serialize(json);
            if (text.Length > MaxResultChars)
                text = text[..MaxResultChars] + $"\n[truncated: output exceeded {MaxResultChars:N0} characters]";
            return HostToolResult.Ok(text);
        }

        private static string[] Names(AgentDocumentInfo info) => info.Columns.Select(c => c.Name).ToArray();

        // ------------------------------------------------------------------ csv.info

        private HostToolResult Info()
        {
            var info = _host.GetInfo();
            var options = Options();
            if (info is null)
                return Reply("No file is open.", new JsonObject { ["open"] = false, ["data_policy"] = options.DataPolicy.ToString() });

            bool sharesValues = options.DataPolicy != AgentDataPolicy.SummaryOnly;
            var columns = new JsonArray();
            foreach (var c in info.Columns)
                columns.Add(new JsonObject { ["name"] = c.Name, ["type"] = c.Type.DisplayName() });

            var filters = new JsonArray();
            foreach (var f in info.Filters)
            {
                var o = new JsonObject { ["kind"] = FilterKindName(f.Kind) };
                if (f.Column is not null) o["column"] = f.Column;
                // 셀값·검색어·컬럼 필터의 설명에는 셀 값이 들어 있다. 식 필터는 모델/사용자가 쓴 식이므로 그대로.
                if (f.Kind == AgentFilterKind.Expression || sharesValues) o["text"] = f.Description;
                filters.Add(o);
            }
            var sort = new JsonArray();
            foreach (var s in info.Sort) sort.Add(new JsonObject { ["column"] = s.Column, ["order"] = s.Ascending ? "asc" : "desc" });

            var json = new JsonObject
            {
                ["file"] = info.FileName,
                ["sheet"] = info.SheetName,
                ["encoding"] = info.Encoding,
                ["delimiter"] = info.Delimiter,
                ["file_bytes"] = info.FileBytes,
                ["indexing_complete"] = info.IndexingComplete,
                ["busy"] = info.Busy,
                ["total_rows"] = info.TotalRows,
                ["view_rows"] = info.ViewRows,
                ["columns"] = columns,
                ["filters"] = filters,
                ["filter_combination"] = info.FilterMatchAny ? "any (OR)" : "all (AND)",
                ["sort"] = sort,
                ["edits"] = EditStateJson(info.Edits),
                ["data_policy"] = options.DataPolicy.ToString(),
                ["max_rows_per_request"] = options.MaxRowsPerRequest,
            };
            if (info.SheetNames.Count > 1) json["sheets"] = ToolJson.Strings(info.SheetNames);
            if (info.HiddenColumns.Count > 0) json["hidden_columns"] = ToolJson.Strings(info.HiddenColumns);
            if (info.RowCountTruncated) json["row_count_truncated"] = true;
            if (info.Cursor.SourceRow is { } r) json["cursor"] = new JsonObject { ["row"] = r, ["column"] = info.Cursor.Column };
            if (!info.IndexingComplete) json["indexing_percent"] = info.IndexingPercent;

            string headline = info.IndexingComplete
                ? $"{info.FileName}: {info.ViewRows:N0} of {info.TotalRows:N0} rows in view, {info.Columns.Count} columns."
                : $"{info.FileName}: still indexing ({info.IndexingPercent}%).";
            return Reply(headline, json);
        }

        private static string FilterKindName(AgentFilterKind kind) => kind switch
        {
            AgentFilterKind.Expression => "expression",
            AgentFilterKind.TextSearch => "text_search",
            AgentFilterKind.CellValue => "cell_value",
            _ => "column_filter",
        };

        private static JsonObject EditStateJson(AgentEditState e) => new()
        {
            ["edited_cells"] = e.Cells,
            ["renamed_columns"] = e.RenamedColumns,
            ["deleted_rows"] = e.DeletedRows,
            ["added_rows"] = e.AddedRows,
            ["unsaved"] = e.Unsaved,
            ["can_undo"] = e.CanUndo,
            ["agent_can_undo"] = e.AgentCanUndo,
            ["sheet_edit_mode"] = e.SheetEditMode,
        };

        // ------------------------------------------------------------------ csv.column_stats

        private async Task<HostToolResult> ColumnStatsAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            var info = RequireReady();
            var names = Names(info);
            var requested = args.OptStringArray("columns", 40);
            List<int> cols = requested is { Count: > 0 }
                ? ColumnNames.ResolveMany(names, requested, "columns")
                : Enumerable.Range(0, Math.Min(names.Length, 40)).ToList();
            int topN = (int)(args.OptInt("top_values", 0, 20) ?? 0);

            var policy = Options().DataPolicy;
            var notes = new JsonArray();
            if (topN > 0)
            {
                if (policy == AgentDataPolicy.SummaryOnly)
                {
                    topN = 0;
                    notes.Add("top_values omitted: the data policy is SummaryOnly (raw cell values are not shared). Counts and numeric summaries are included.");
                }
                else if (policy == AgentDataPolicy.RowsWithApproval)
                {
                    var lines = new List<string> { L("  Columns:", "  컬럼:") };
                    lines.AddRange(cols.Take(40).Select(c => "  · " + names[c]));
                    bool ok = await approvals.ApproveAsync(
                        L("Share most frequent values with the AI", "AI에게 최빈값 공유"),
                        L($"Up to {topN} most frequent values for {cols.Count} column(s) of the current view", $"현재 뷰 {cols.Count}개 컬럼의 상위 {topN}개 빈도 값"),
                        lines, ct);
                    if (!ok) throw new AgentToolException("The user declined to share the most frequent values. Retry without top_values for aggregates only.");
                }
            }
            bool includeTop = topN > 0;

            var stats = await _host.RunOnViewRowsAsync(
                "Column statistics",
                (data, token) => ColumnStatsCalculator.Compute(data, cols, topN, token),
                ct);

            var json = new JsonObject
            {
                ["scope"] = "current view",
                ["view_rows"] = info.ViewRows,
                ["columns"] = ColumnStatsCalculator.ToJson(stats, includeTop),
            };
            if (notes.Count > 0) json["notes"] = notes;
            if (requested is not { Count: > 0 } && names.Length > 40)
                json["columns_truncated"] = $"Only the first 40 of {names.Length} columns are shown; pass 'columns' for others.";
            return Reply($"Statistics for {stats.Count} column(s) over {info.ViewRows:N0} view rows.", json);
        }

        // ------------------------------------------------------------------ csv.get_rows

        private async Task<HostToolResult> GetRowsAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            var options = Options();
            if (options.DataPolicy == AgentDataPolicy.SummaryOnly)
                throw new AgentToolException(
                    "Refused: the data policy is SummaryOnly, so raw rows are not shared with the AI. " +
                    "Tell the user they can allow it in the AI agent settings (data sharing: 'rows with approval' or 'rows allowed'). " +
                    "Meanwhile use csv.info, csv.column_stats, csv.run_analysis or csv.quality_scan, which return aggregates only.");

            var info = RequireReady();
            var names = Names(info);
            long from = args.OptInt("from", 1, long.MaxValue) ?? 1;
            long want = args.OptInt("count", 1, 1_000_000) ?? 20;
            var requested = args.OptStringArray("columns", 60);
            List<int> cols = requested is { Count: > 0 }
                ? ColumnNames.ResolveMany(names, requested, "columns")
                : Enumerable.Range(0, Math.Min(names.Length, 60)).ToList();

            if (info.ViewRows == 0) throw new AgentToolException("The current view has no rows (the filter matches nothing).");
            if (from > info.ViewRows) throw new AgentToolException($"'from' is beyond the end: the current view has {info.ViewRows:N0} rows.");

            int cap = Math.Clamp(options.MaxRowsPerRequest, 1, 5000);
            long n = Math.Min(Math.Min(want, cap), info.ViewRows - from + 1);

            if (options.DataPolicy == AgentDataPolicy.RowsWithApproval)
            {
                var lines = new List<string> { L("  Columns:", "  컬럼:") };
                lines.AddRange(cols.Take(60).Select(c => "  · " + names[c]));
                if (info.Filters.Count > 0) lines.Add(L("  (a filter is active; only the rows in the current view)", "  (필터가 적용되어 현재 뷰의 행만 해당)"));
                bool ok = await approvals.ApproveAsync(
                    L("Share rows with the AI", "AI에게 행 데이터 공유"),
                    L($"{n:N0} row(s) × {cols.Count} column(s): view rows {from:N0}–{from + n - 1:N0} of {info.ViewRows:N0}",
                      $"{n:N0}행 × {cols.Count}개 컬럼: 현재 뷰 {from:N0}–{from + n - 1:N0}행 (전체 {info.ViewRows:N0})"),
                    lines, ct);
                if (!ok) throw new AgentToolException("The user declined to share rows. Use aggregate tools (csv.column_stats, csv.run_analysis) instead.");
            }

            var page = await _host.GetRowsAsync(from - 1, (int)n, cols, ct);

            var rows = new JsonArray();
            int chars = 0;
            int included = 0;
            bool budgetHit = false;
            foreach (var row in page.Rows)
            {
                var arr = new JsonArray { JsonValue.Create(row.SourceRow) };
                int rowChars = 8;
                foreach (string v in row.Values)
                {
                    string cell = ToolJson.Clip(v, MaxCellChars);
                    rowChars += cell.Length + 3;
                    arr.Add(cell);
                }
                if (chars + rowChars > RowsOutputBudgetChars && included > 0) { budgetHit = true; break; }
                chars += rowChars;
                rows.Add(arr);
                included++;
            }

            var json = new JsonObject
            {
                ["columns"] = ToolJson.Strings(cols.Select(c => names[c])),
                ["row_format"] = "[row_number_in_row_header, ...values in the order of columns]",
                ["from_view_row"] = from,
                ["returned"] = included,
                ["view_rows"] = info.ViewRows,
                ["rows"] = rows,
            };
            var notes = new JsonArray();
            if (want > n) notes.Add($"Requested {want:N0} rows; returned at most {n:N0} (policy cap {cap:N0} and the end of the view).");
            if (budgetHit) notes.Add($"Output was cut after {included} row(s) to stay under {RowsOutputBudgetChars:N0} characters; continue with from={from + included}.");
            if (page.Rows.Any(r => r.Values.Any(v => v.Length > MaxCellChars))) notes.Add($"Cell values longer than {MaxCellChars} characters are clipped with …");
            if (requested is not { Count: > 0 } && names.Length > 60) notes.Add($"Only the first 60 of {names.Length} columns are included; pass 'columns' for others.");
            if (notes.Count > 0) json["notes"] = notes;
            return Reply($"{included} row(s) of the current view starting at view row {from:N0}.", json);
        }

        // ------------------------------------------------------------------ csv.set_filter / clear_filter

        private async Task<HostToolResult> SetFilterAsync(ToolArgs args, CancellationToken ct)
        {
            string expression = args.ReqString("expression").Trim();
            string mode = args.OptEnum("mode", "replace", "and") ?? "replace";
            if (expression.Length > 4000) throw new AgentToolException("The expression is too long (max 4000 characters).");
            var info = RequireReady();
            var names = Names(info);

            var warnings = new JsonArray();
            try
            {
                AdvancedFilterExpression.Compile(expression, names);
                if (AdvancedFilterExpression.LooksLikeUnbracketedColumnComparison(expression, names))
                    warnings.Add("The right side matches a column name but is not in [brackets], so it is compared as literal text. Use [col_a] >= [col_b] to compare two columns.");
            }
            catch (AdvancedFilterExpressionException ex)
            {
                throw new AgentToolException(
                    "Invalid filter expression: " + ex.Message + " " +
                    "Syntax: <column> <op> <value>; op is = != < <= > >= contains startswith endswith; combine with AND / OR and parentheses (no NOT); " +
                    "put text values in double quotes; a right side like [other_column] compares two columns. " +
                    "Columns: " + string.Join(", ", names.Take(30)) + (names.Length > 30 ? ", …" : ""));
            }

            var change = await _host.SetFilterAsync(expression, replace: mode == "replace", ct);
            var json = new JsonObject
            {
                ["expression"] = expression,
                ["mode"] = mode,
                ["view_rows"] = change.ViewRows,
                ["total_rows"] = change.TotalRows,
            };
            if (change.ViewRows == 0) warnings.Add("No row matches. Check column names and value spelling (csv.column_stats top_values, if the data policy allows).");
            if (warnings.Count > 0) json["warnings"] = warnings;
            return Reply($"Filter applied: {change.ViewRows:N0} of {change.TotalRows:N0} rows match. The grid shows them now.", json);
        }

        private async Task<HostToolResult> ClearFilterAsync(CancellationToken ct)
        {
            RequireReady();
            var change = await _host.ClearFilterAsync(ct);
            return Reply($"Filters and sort cleared: all {change.TotalRows:N0} rows are shown.",
                new JsonObject { ["view_rows"] = change.ViewRows, ["total_rows"] = change.TotalRows });
        }

        // ------------------------------------------------------------------ csv.sort

        private async Task<HostToolResult> SortAsync(ToolArgs args, CancellationToken ct)
        {
            var keysArg = args.OptObjectArray("keys", 5) ?? throw new AgentToolException("'keys' is required (an empty array clears the sort).");
            var info = RequireReady();
            var names = Names(info);
            var keys = new List<SortKey>();
            foreach (var k in keysArg)
            {
                int col = ColumnNames.Resolve(names, k.ReqString("column"), "keys[].column");
                if (keys.Any(x => x.Column == col)) throw new AgentToolException($"Column '{names[col]}' appears twice in 'keys'.");
                string order = k.OptEnum("order", "asc", "desc") ?? "asc";
                keys.Add(new SortKey(col, order == "asc"));
            }

            var change = await _host.SortAsync(keys, ct);
            var sort = new JsonArray();
            foreach (var k in keys) sort.Add(new JsonObject { ["column"] = names[k.Column], ["order"] = k.Ascending ? "asc" : "desc" });
            string summary = keys.Count == 0
                ? "Sort cleared."
                : "Sorted by " + string.Join(" → ", keys.Select(k => $"{names[k.Column]} {(k.Ascending ? "asc" : "desc")}")) + ".";
            return Reply(summary, new JsonObject { ["sort"] = sort, ["view_rows"] = change.ViewRows });
        }

        // ------------------------------------------------------------------ csv.goto

        private async Task<HostToolResult> GotoAsync(ToolArgs args, CancellationToken ct)
        {
            long? row = args.OptInt("row", 1, long.MaxValue);
            string? colName = args.OptString("column");
            if (row is null && string.IsNullOrWhiteSpace(colName)) throw new AgentToolException("Give 'row' and/or 'column'.");
            var info = RequireReady();
            int? col = string.IsNullOrWhiteSpace(colName) ? null : ColumnNames.Resolve(Names(info), colName, "column");

            var r = await _host.GotoAsync(row, col, ct);
            return Reply($"Cursor moved to row {r.SourceRow:N0}, column {r.Column}.",
                new JsonObject { ["row"] = r.SourceRow, ["view_row"] = r.ViewRow, ["column"] = r.Column });
        }

        // ------------------------------------------------------------------ csv.run_analysis

        private async Task<HostToolResult> RunAnalysisAsync(ToolArgs args, CancellationToken ct)
        {
            string kindText = args.OptEnum("kind", "describe", "glm", "ancova", "glzm", "logistic")
                ?? throw new AgentToolException("'kind' is required: describe, glm, ancova, glzm or logistic.");
            var kind = Enum.Parse<AgentAnalysisKind>(kindText, ignoreCase: true);

            var family = args.OptEnum("family", "gaussian", "binomial", "poisson", "gamma") is { } f
                ? Enum.Parse<GlmFamily>(f, ignoreCase: true) : GlmFamily.Gaussian;
            GlmLink? link = args.OptEnum("link", "identity", "log", "inverse", "logit", "probit", "cloglog", "sqrt") switch
            {
                null => null,
                "cloglog" => GlmLink.CLogLog,
                { } l => Enum.Parse<GlmLink>(l, ignoreCase: true),
            };

            var request = new AgentAnalysisRequest
            {
                Kind = kind,
                Formula = args.OptString("formula"),
                Columns = args.OptStringArray("columns", 40),
                GroupBy = args.OptString("group_by"),
                Dependent = args.OptString("dependent"),
                Factors = args.OptStringArray("factors", 6),
                Covariates = args.OptStringArray("covariates", 20),
                Interactions = args.OptEnum("interactions", "none", "two_way", "all")?.ToLowerInvariant() ?? "none",
                Family = family,
                Link = link,
                EventLevel = args.OptString("event_level"),
                Offset = args.OptString("offset"),
                Exposure = args.OptString("exposure"),
                VarianceWeights = args.OptString("var_weights"),
                FrequencyWeights = args.OptString("freq_weights"),
                Trials = args.OptString("trials"),
                ShowWindow = args.OptBool("show_window") ?? true,
            };
            if (kind is AgentAnalysisKind.Glm or AgentAnalysisKind.Glzm or AgentAnalysisKind.Logistic && string.IsNullOrWhiteSpace(request.Formula))
                throw new AgentToolException($"'formula' is required for {kindText}, e.g. \"y ~ x1 + C(group)\".");
            if (kind == AgentAnalysisKind.Ancova &&
                (string.IsNullOrWhiteSpace(request.Dependent) || request.Factors is not { Count: > 0 } || request.Covariates is not { Count: > 0 }))
                throw new AgentToolException("ancova needs 'dependent', 'factors' (categorical) and 'covariates' (numeric).");

            var info = RequireReady();
            var outcome = await _host.RunOnViewRowsAsync(
                "Analysis: " + kindText,
                (data, token) => AgentAnalysis.Run(request, data, token),
                ct);
            if (request.ShowWindow) _host.ShowAnalysisWindow(outcome);

            outcome.Json["scope"] = $"current view ({info.ViewRows:N0} rows)";
            outcome.Json["result_window"] = request.ShowWindow ? "opened for the user" : "not opened";
            return Reply($"{outcome.Title} finished.", outcome.Json);
        }

        // ------------------------------------------------------------------ csv.quality_scan

        private async Task<HostToolResult> QualityScanAsync(ToolArgs args, CancellationToken ct)
        {
            var min = (args.OptEnum("min_severity", "info", "warning", "critical") ?? "info").ToLowerInvariant() switch
            {
                "critical" => QualitySeverity.Critical,
                "warning" => QualitySeverity.Warning,
                _ => QualitySeverity.Info,
            };
            int max = (int)(args.OptInt("max_findings", 1, 100) ?? 40);
            RequireReady();

            var report = await _host.RunQualityScanAsync(ct);
            bool includeValues = Options().DataPolicy == AgentDataPolicy.RowsAllowed;
            var json = QualityJson.Build(report, min, max, includeValues);
            if (!includeValues && Options().DataPolicy == AgentDataPolicy.RowsWithApproval)
                json["values_hidden"] = "Example cell values are not included in scan results; row numbers are given (example_rows). Use csv.get_rows (user approval) to read specific rows.";
            return Reply(
                $"Quality scan: {report.CountBySeverity(QualitySeverity.Critical)} critical, {report.CountBySeverity(QualitySeverity.Warning)} warning, {report.CountBySeverity(QualitySeverity.Info)} info over {report.RowsScanned:N0} rows. Shown in the quality panel.",
                json);
        }

        // ------------------------------------------------------------------ csv.edit_cells

        private async Task<HostToolResult> EditCellsAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            var items = args.OptObjectArray("edits", MaxEditsPerCall) ?? throw new AgentToolException("'edits' is required.");
            if (items.Count == 0) throw new AgentToolException("'edits' is empty.");
            var info = RequireReady();
            var names = Names(info);

            var edits = new List<AgentCellEdit>(items.Count);
            var seen = new HashSet<(long, int)>();
            foreach (var item in items)
            {
                long row = item.ReqInt("row", 1, long.MaxValue);
                int col = ColumnNames.Resolve(names, item.ReqString("column"), "edits[].column");
                if (!item.HasScalar("value")) throw new AgentToolException("Every edit needs a 'value' (use \"\" to clear the cell).");
                string value = item.OptScalarAsString("value") ?? "";
                if (row > info.TotalRows) throw new AgentToolException($"Row {row:N0} does not exist (the table has {info.TotalRows:N0} rows).");
                if (value.Length > MaxEditValueChars) throw new AgentToolException($"A value is too long (max {MaxEditValueChars:N0} characters).");
                if (!seen.Add((row, col))) throw new AgentToolException($"Row {row:N0} column '{names[col]}' is edited twice in one call.");
                edits.Add(new AgentCellEdit(row, col, value));
            }

            var states = _host.GetCellStates(edits.Select(e => (e.SourceRow, e.Column)).ToList());
            var changes = new List<EditCard.Change>();
            for (int i = 0; i < edits.Count; i++)
            {
                if (!states[i].Exists) throw new AgentToolException($"Row {edits[i].SourceRow:N0} does not exist.");
                if (!string.Equals(states[i].Current, edits[i].Value, StringComparison.Ordinal))
                    changes.Add(new EditCard.Change(edits[i].SourceRow, names[edits[i].Column], states[i].Current, edits[i].Value));
            }
            if (changes.Count == 0)
                return Reply("No change: every cell already has the given value.", new JsonObject { ["changed_cells"] = 0, ["unchanged_cells"] = edits.Count });

            long lo = changes.Min(c => c.SourceRow), hi = changes.Max(c => c.SourceRow);
            bool approved = await approvals.ApproveAsync(
                L($"Edit {changes.Count:N0} cell(s) in {info.FileName}", $"{info.FileName}의 셀 {changes.Count:N0}개 편집"),
                L($"-{changes.Count:N0} +{changes.Count:N0} · rows {lo:N0}–{hi:N0} · one undo step (Ctrl+Z); the original file is not changed",
                  $"-{changes.Count:N0} +{changes.Count:N0} · {lo:N0}–{hi:N0}행 · 되돌리기 1단계(Ctrl+Z), 원본 파일은 바뀌지 않음"),
                EditCard.Lines(changes, Korean), ct);
            if (!approved) throw new AgentToolException("The user did not approve the edit. Nothing was changed.");

            RequireReady(); // 승인을 기다리는 동안 사용자가 다른 작업을 시작했을 수 있다
            var result = _host.ApplyEdits(edits, AgentEditTag.Prefix + L($"edit {changes.Count:N0} cell(s)", $"셀 {changes.Count:N0}개 편집"));
            var json = new JsonObject
            {
                ["changed_cells"] = result.Changed,
                ["unchanged_cells"] = result.Unchanged,
                ["undo_steps_added"] = 1,
                ["edits"] = EditStateJson(result.State),
                ["note"] = "Applied through the edit overlay as one undo step; sheet edit mode was not needed. The source file is unchanged and the edits are unsaved until csv.save_edits_as. csv.undo reverts this step.",
            };
            return Reply($"Edited {result.Changed:N0} cell(s) as one undo step (overlay only; original file untouched, unsaved).", json);
        }

        // ------------------------------------------------------------------ csv.undo

        private HostToolResult Undo()
        {
            RequireReady();
            var r = _host.UndoAgentEdit();
            return Reply($"Undid: {r.Description}.", new JsonObject { ["undone"] = r.Description, ["edits"] = EditStateJson(r.State) });
        }

        // ------------------------------------------------------------------ csv.save_edits_as

        private async Task<HostToolResult> SaveEditsAsAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            string requested = args.ReqString("path");
            bool overwrite = args.OptBool("overwrite") ?? false;
            var info = RequireReady();
            var e = info.Edits;
            if (e.Cells + e.RenamedColumns + e.DeletedRows + e.AddedRows == 0)
                throw new AgentToolException("There are no edits to save.");

            string full = AgentSavePolicy.Resolve(requested, info.Directory, info.ProtectedPaths, overwrite);
            bool exists = File.Exists(full);

            var parts = new List<string>();
            if (e.Cells > 0) parts.Add(L($"{e.Cells:N0} cell(s)", $"셀 {e.Cells:N0}개"));
            if (e.RenamedColumns > 0) parts.Add(L($"{e.RenamedColumns:N0} renamed column(s)", $"컬럼 이름 {e.RenamedColumns:N0}개"));
            if (e.DeletedRows > 0) parts.Add(L($"{e.DeletedRows:N0} deleted row(s)", $"삭제 행 {e.DeletedRows:N0}개"));
            if (e.AddedRows > 0) parts.Add(L($"{e.AddedRows:N0} added row(s)", $"추가 행 {e.AddedRows:N0}개"));
            string summary = string.Join(", ", parts);

            var lines = new List<string>
            {
                "+ " + full,
                L("  Includes: ", "  포함: ") + summary,
                L("  The source file stays unchanged.", "  원본 파일은 바뀌지 않습니다."),
            };
            if (exists) lines.Add("- " + L("An existing file with this name will be REPLACED.", "같은 이름의 기존 파일을 덮어씁니다."));

            bool approved = await approvals.ApproveAsync(
                L($"Save edits to {Path.GetFileName(full)}", $"편집 내용을 {Path.GetFileName(full)}에 저장"),
                L($"Write {summary} to a new file", $"{summary}을(를) 새 파일에 저장"),
                lines, ct);
            if (!approved) throw new AgentToolException("The user did not approve saving. Nothing was written.");

            RequireReady();
            full = AgentSavePolicy.Resolve(requested, info.Directory, info.ProtectedPaths, overwrite); // 승인 대기 중 바뀐 상태 재검증
            var saved = await _host.SaveEditsAsAsync(full, ct);
            return Reply($"Saved to {saved.FullPath}.", new JsonObject { ["saved_to"] = saved.FullPath, ["includes"] = saved.Summary, ["source_file_unchanged"] = true });
        }

        // ------------------------------------------------------------------ 기록

        /// <summary>기록용 인자: 셀 값은 빼고 행·컬럼만 남긴다. 파싱에 실패해도 도구 실행을 막지 않는다.</summary>
        private static JsonNode? SafeLogArgs(HostToolCall call)
        {
            try
            {
                if (call.Arguments.ValueKind != JsonValueKind.Object) return null;
                if (call.ToolName == ToolDefinitions.EditCells)
                {
                    var rows = new SortedSet<long>();
                    var cols = new SortedSet<string>(StringComparer.Ordinal);
                    int count = 0;
                    if (call.Arguments.TryGetProperty("edits", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var e in arr.EnumerateArray())
                        {
                            count++;
                            if (e.ValueKind != JsonValueKind.Object) continue;
                            if (e.TryGetProperty("row", out var r) && r.TryGetInt64(out long rn)) rows.Add(rn);
                            if (e.TryGetProperty("column", out var c) && c.ValueKind == JsonValueKind.String) cols.Add(c.GetString() ?? "");
                        }
                    return new JsonObject
                    {
                        ["edits"] = count,
                        ["rows"] = new JsonArray(rows.Take(20).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                        ["columns"] = new JsonArray(cols.Take(20).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                    };
                }
                var node = JsonNode.Parse(call.Arguments.GetRawText());
                if (node is JsonObject o && o.ContainsKey("event_level")) o["event_level"] = "<set>"; // 범주 값이므로 기록하지 않는다
                return node;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
