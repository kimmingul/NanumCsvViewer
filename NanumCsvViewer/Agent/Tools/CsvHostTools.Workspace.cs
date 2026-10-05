using System.Globalization;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Agent
{
    // ws.* 도구: 여러 파일(표)·뷰·DuckDB 질의. 창 조작은 IWorkspaceAgentHost, 계산은 DataWorkspace가 한다.
    //
    // 데이터 정책(ws.query): SummaryOnly = 행 수·컬럼 이름/형·비어 있지 않은 수·고유 값 수·수치 컬럼의 최소/최대/평균만(셀 값 없음).
    // RowsWithApproval = 승인 카드 뒤에 상한까지의 행, RowsAllowed = 상한까지의 행. open_tab:true는 정책과 무관하게 사용자에게 결과 탭을 열어 준다(모델에는 정책대로).
    // ws.describe의 변환 실패 예시 값은 SummaryOnly에서 생략. 뷰 만들기·덧붙이기·비교·그룹은 DataEdit 승인(write·yolo에서 자동), 실체화는 FileSave 승인.
    public sealed partial class CsvHostTools
    {
        private const int WsMaxCountedTables = 30;
        private const long WsCountRowsMaxBytes = 64L * 1024 * 1024;
        private const int WsDefaultProfileColumns = 30;
        private static readonly string[] WsSourceExtensions =
            { ".csv", ".tsv", ".txt", ".xlsx", ".xlsm", ".xls", ".sas7bdat", ".sav", ".db", ".sqlite", ".sqlite3" };

        private async Task<HostToolResult> WorkspaceToolAsync(string tool, ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            if (_host is not IWorkspaceAgentHost wh)
                throw new AgentToolException("The workspace is not available in this window. Use the csv.* tools on the open file.");
            var ws = wh.Workspace ?? throw new AgentToolException(
                "The workspace (DuckDB query engine) is not available: " + (wh.WorkspaceUnavailableReason ?? "unknown reason") + ". Use the csv.* tools on the open file.");
            await wh.EnsureTabsRegisteredAsync(ct);

            try
            {
                return tool switch
                {
                    ToolDefinitions.WsListTables => await WsListTablesAsync(args, wh, ws, ct),
                    ToolDefinitions.WsDescribe => await WsDescribeAsync(args, ws, ct),
                    ToolDefinitions.WsAddSource => await WsAddSourceAsync(args, wh, ws, ct),
                    ToolDefinitions.WsQuery => await WsQueryAsync(args, wh, ws, approvals, ct),
                    ToolDefinitions.WsCheckJoin => await WsCheckJoinAsync(args, ws, ct),
                    ToolDefinitions.WsCreateView => await WsCreateViewAsync(args, wh, ws, approvals, ct),
                    ToolDefinitions.WsAppend => await WsAppendAsync(args, wh, ws, approvals, ct),
                    ToolDefinitions.WsCompare => await WsCompareAsync(args, wh, ws, approvals, ct),
                    ToolDefinitions.WsGroup => await WsGroupAsync(args, wh, ws, approvals, ct),
                    ToolDefinitions.WsMaterialize => await WsMaterializeAsync(args, wh, ws, approvals, ct),
                    ToolDefinitions.WsOpen => await WsOpenAsync(args, wh, ws, ct),
                    ToolDefinitions.WsSwitch => await WsSwitchAsync(args, wh, ct),
                    _ => HostToolResult.Error($"Unknown tool '{tool}'."),
                };
            }
            catch (WorkspaceQueryException ex) { throw new AgentToolException(ex.Message); }
            catch (ArgumentException ex) { throw new AgentToolException(ex.Message); }
            catch (InvalidOperationException ex) { throw new AgentToolException(ex.Message); }
        }

        // ------------------------------------------------------------------ 이름 확인

        private static IEnumerable<string> WsRelationNames(DataWorkspace ws)
            => ws.Sources.SelectMany(s => s.Tables.Select(t => t.DisplayName)).Concat(ws.Views.Select(v => v.Name));

        private static IWorkspaceRelation WsResolve(DataWorkspace ws, string name, string param)
        {
            string n = name.Trim();
            if (n.Length >= 2 && n[0] == '"' && n[^1] == '"') n = n[1..^1].Replace("\"\"", "\"");
            var rel = ws.FindRelation(n);
            if (rel is null && n.StartsWith("main.", StringComparison.OrdinalIgnoreCase)) rel = ws.FindRelation(n[5..]);
            if (rel is not null) return rel;
            var names = WsRelationNames(ws).Take(60).ToList();
            throw new AgentToolException(
                $"'{param}': no table or view named '{name}'. " +
                (names.Count == 0 ? "The workspace is empty; open files or use ws.add_source first." : "Available: " + string.Join(", ", names) + "."));
        }

        private static string WsColumn(IWorkspaceRelation rel, string name, string param)
        {
            string n = name.Trim();
            var exact = rel.Columns.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.Ordinal));
            if (exact is not null) return exact.Name;
            var loose = rel.Columns.Where(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)).ToList();
            if (loose.Count == 1) return loose[0].Name;
            throw new AgentToolException(
                $"'{param}': {rel.DisplayName} has no column '{name}'. Columns: " + string.Join(", ", rel.Columns.Select(c => c.Name).Take(80)) + ".");
        }

        private static JsonArray WsColumnsJson(IEnumerable<WorkspaceColumn> columns)
        {
            var a = new JsonArray();
            foreach (var c in columns) a.Add(new JsonObject { ["name"] = c.Name, ["type"] = c.Type.DisplayName(), ["sql_type"] = c.SqlType });
            return a;
        }

        private static JsonNode? WsScalar(string? s)
        {
            if (s is null) return null;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return JsonValue.Create(l);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return ToolJson.Num(d);
            return JsonValue.Create(s);
        }

        private static long WsLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : 0;

        private static bool WsIsNumeric(ColumnValueType t) => t is ColumnValueType.Integer or ColumnValueType.Float or ColumnValueType.Scientific;

        private static List<JoinKey> WsKeys(ToolArgs args, IWorkspaceRelation left, IWorkspaceRelation right)
        {
            var items = args.OptObjectArray("keys", 5);
            if (items is null || items.Count == 0) throw new AgentToolException("'keys' is required: at least one {left, right} column pair.");
            var keys = new List<JoinKey>();
            foreach (var k in items)
                keys.Add(new JoinKey(WsColumn(left, k.ReqString("left"), "keys.left"), WsColumn(right, k.ReqString("right"), "keys.right")));
            return keys;
        }

        // ------------------------------------------------------------------ ws.list_tables

        private async Task<HostToolResult> WsListTablesAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, CancellationToken ct)
        {
            bool count = args.OptBool("count_rows") ?? true;
            var sources = new JsonArray();
            int counted = 0;
            foreach (var s in ws.Sources)
            {
                var tables = new JsonArray();
                foreach (var t in s.Tables)
                {
                    var o = new JsonObject
                    {
                        ["name"] = t.DisplayName,
                        ["sql"] = t.SqlReference,
                        ["raw_text_sql"] = t.RawSqlReference,
                        ["columns"] = WsColumnsJson(t.Columns),
                    };
                    if (count && counted < WsMaxCountedTables && t.Length <= WsCountRowsMaxBytes)
                    {
                        counted++;
                        try { o["rows"] = await ws.CountRowsAsync(t, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { o["rows"] = null; }
                    }
                    tables.Add(o);
                }
                var so = new JsonObject
                {
                    ["name"] = s.Name,
                    ["kind"] = s.Kind == WorkspaceSourceKind.Csv ? "csv" : "database",
                    ["file"] = string.IsNullOrEmpty(s.Path) ? null : Path.GetFileName(s.Path),
                    ["tables"] = tables,
                };
                bool changed = false;
                try { changed = ws.IsSourceChanged(s); } catch (Exception) { }
                if (changed) so["file_changed_since_loaded"] = true;
                sources.Add(so);
            }

            var views = new JsonArray();
            foreach (var v in ws.Views)
            {
                var vo = new JsonObject
                {
                    ["name"] = v.Name,
                    ["sql"] = v.Sql,
                    ["depends_on"] = ToolJson.Strings(v.Dependencies),
                    ["columns"] = WsColumnsJson(v.Columns),
                    ["include_unsaved_edits"] = v.IncludeUnsavedEdits,
                    ["computed"] = v.HasResult,
                    ["stale"] = ws.IsStale(v),
                };
                if (v.ResultRowCount is { } rc) vo["rows"] = rc;
                if (v.Error is { Length: > 0 } err) vo["error"] = err;
                views.Add(vo);
            }

            var tabs = new JsonArray();
            string? active = null;
            foreach (var t in wh.GetTabs())
            {
                if (t.IsActive) active = t.Name;
                var to = new JsonObject { ["name"] = t.Name, ["kind"] = t.Kind, ["active"] = t.IsActive, ["read_only"] = t.IsReadOnly, ["unsaved_edits"] = t.HasUnsavedEdits };
                if (t.RelationName is not null) to["table_or_view"] = t.RelationName;
                tabs.Add(to);
            }

            var json = new JsonObject
            {
                ["sources"] = sources,
                ["views"] = views,
                ["tabs"] = tabs,
                ["active_tab"] = active,
                ["notes"] = new JsonArray(
                    "Quote names with \"…\" in SQL (always for Korean/space/digit-leading names). Typed columns come from the table; identifier/id columns stay VARCHAR (CAST before numeric comparison). <table>__raw has every column as original text.",
                    "csv.* tools act on the active tab only; use ws.open / ws.switch to change it."),
            };
            int tableCount = ws.Sources.Sum(s => s.Tables.Count);
            return Reply($"{ws.Sources.Count} source(s) with {tableCount} table(s), {ws.Views.Count} view(s), {tabs.Count} open tab(s).", json);
        }

        // ------------------------------------------------------------------ ws.describe

        private async Task<HostToolResult> WsDescribeAsync(ToolArgs args, DataWorkspace ws, CancellationToken ct)
        {
            var rel = WsResolve(ws, args.ReqString("name"), "name");
            var requested = args.OptStringArray("columns", 40);
            var profileCols = requested is { Count: > 0 }
                ? requested.Select(c => WsColumn(rel, c, "columns")).Distinct(StringComparer.Ordinal).ToList()
                : rel.Columns.Take(WsDefaultProfileColumns).Select(c => c.Name).ToList();
            bool sharesValues = Options().DataPolicy != AgentDataPolicy.SummaryOnly;

            long rows = await ws.CountRowsAsync(rel, ct);
            var json = new JsonObject
            {
                ["name"] = rel.DisplayName,
                ["kind"] = rel is WorkspaceTable ? "table" : "view",
                ["sql"] = rel.SqlReference,
                ["rows"] = rows,
                ["columns"] = WsColumnsJson(rel.Columns),
            };
            if (rel is WorkspaceView { Error: { Length: > 0 } viewError }) json["error"] = viewError;
            if (rel is WorkspaceTable tbl)
            {
                json["raw_text_sql"] = tbl.RawSqlReference;
                var reports = await ws.CheckTypedColumnsAsync(tbl, ct);
                var casts = new JsonArray();
                foreach (var r in reports)
                {
                    var o = new JsonObject
                    {
                        ["column"] = r.Column,
                        ["type"] = r.Type.DisplayName(),
                        ["non_empty"] = r.NonEmptyCount,
                        ["cast_failures"] = r.FailureCount,
                        ["failure_rate"] = r.NonEmptyCount == 0 ? 0 : ToolJson.Num((double)r.FailureCount / r.NonEmptyCount),
                    };
                    if (r.FailureCount > 0 && sharesValues && r.FailureExamples.Count > 0)
                        o["failure_examples"] = ToolJson.Strings(r.FailureExamples.Select(e => ToolJson.Clip(e, 40)));
                    casts.Add(o);
                }
                json["typed_columns_checked"] = reports.Count;
                json["cast_failures"] = casts;
                int bad = reports.Count(r => r.FailureCount > 0);
                json["cast_summary"] = bad == 0
                    ? "Every non-empty value converted to its detected type."
                    : $"{bad} column(s) have values that did not convert (they are NULL in the typed column; the original text is in the __raw table).";
                if (!sharesValues && bad > 0) json["notes"] = new JsonArray("Failing example values omitted: the data policy is SummaryOnly.");
            }

            var profile = new JsonArray();
            var keyCandidates = new JsonArray();
            if (profileCols.Count > 0 && rows > 0)
            {
                var parts = new List<string> { "count(*)" };
                foreach (string c in profileCols)
                {
                    string q = SqlNames.Quote(c);
                    parts.Add($"count({q})");
                    parts.Add($"count(DISTINCT {q})");
                }
                var pv = await ws.PreviewAsync($"SELECT {string.Join(", ", parts)} FROM {rel.SqlReference}", 1, ct);
                var row = pv.Rows[0];
                long total = WsLong(row[0]);
                for (int i = 0; i < profileCols.Count; i++)
                {
                    long nonNull = WsLong(row[1 + 2 * i]), distinct = WsLong(row[2 + 2 * i]);
                    long nulls = total - nonNull;
                    profile.Add(new JsonObject
                    {
                        ["column"] = profileCols[i],
                        ["non_null"] = nonNull,
                        ["nulls"] = nulls,
                        ["distinct"] = distinct,
                    });
                    if (nonNull == 0) continue;
                    if (distinct == total && nulls == 0)
                        keyCandidates.Add(new JsonObject { ["column"] = profileCols[i], ["uniqueness"] = "unique", ["nulls"] = 0 });
                    else if (nonNull >= 20 && (double)distinct / nonNull >= 0.95)
                        keyCandidates.Add(new JsonObject
                        {
                            ["column"] = profileCols[i],
                            ["uniqueness"] = "near_unique",
                            ["nulls"] = nulls,
                            ["rows_sharing_a_value"] = nonNull - distinct,
                        });
                }
            }
            json["profile"] = profile;
            json["key_candidates"] = keyCandidates;
            if (rel.Columns.Count > profileCols.Count && requested is not { Count: > 0 })
                json["profile_truncated"] = $"Only the first {profileCols.Count} of {rel.Columns.Count} columns are profiled; pass 'columns' for others.";
            return Reply($"{rel.DisplayName}: {rows:N0} rows, {rel.Columns.Count} columns.", json);
        }

        // ------------------------------------------------------------------ ws.add_source

        private async Task<HostToolResult> WsAddSourceAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, CancellationToken ct)
        {
            var requested = args.OptStringArray("paths", 20);
            if (requested is not { Count: > 0 }) throw new AgentToolException("'paths' is required: at least one file path.");
            var info = _host.GetInfo();
            string? baseDir = info?.Directory;
            if (string.IsNullOrEmpty(baseDir) || (wh.GetTabs().FirstOrDefault(t => t.IsActive)?.IsReadOnly ?? false)) baseDir = _host.AnalysisFolder;

            var fulls = new List<string>();
            foreach (string raw in requested)
            {
                string full;
                try
                {
                    string p = raw.Trim();
                    if (!Path.IsPathRooted(p))
                    {
                        if (string.IsNullOrEmpty(baseDir)) throw new AgentToolException($"'{raw}': give an absolute path (no folder to resolve relative paths against).");
                        p = Path.Combine(baseDir, p);
                    }
                    full = Path.GetFullPath(p);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    throw new AgentToolException($"Invalid path '{raw}': {ex.Message}");
                }
                string ext = Path.GetExtension(full);
                if (!WsSourceExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    throw new AgentToolException($"Refused '{Path.GetFileName(full)}': only data files can be added ({string.Join(" ", WsSourceExtensions)}).");
                if (Directory.Exists(full)) throw new AgentToolException($"'{raw}' is a folder; pass data files.");
                if (!File.Exists(full)) throw new AgentToolException($"File not found: {full}");
                if (!fulls.Contains(full, StringComparer.OrdinalIgnoreCase)) fulls.Add(full);
            }

            var already = new List<WorkspaceSource>();
            var toAdd = new List<string>();
            foreach (string f in fulls)
            {
                var existing = ws.Sources.FirstOrDefault(s => AgentSavePolicy.SameFile(s.Path, f));
                if (existing is not null) already.Add(existing); else toAdd.Add(f);
            }
            var added = toAdd.Count > 0 ? await wh.AddSourcesAsync(toAdd, ct) : Array.Empty<WorkspaceSource>();

            var list = new JsonArray();
            foreach (var s in already.Concat(added))
            {
                var tables = new JsonArray();
                foreach (var t in s.Tables) tables.Add(new JsonObject { ["name"] = t.DisplayName, ["sql"] = t.SqlReference, ["columns"] = WsColumnsJson(t.Columns) });
                list.Add(new JsonObject { ["name"] = s.Name, ["file"] = Path.GetFileName(s.Path), ["already_in_workspace"] = already.Contains(s), ["tables"] = tables });
            }
            return Reply($"{added.Count} source(s) added, {already.Count} already in the workspace. Files are read-only inputs and are never modified.", new JsonObject { ["sources"] = list });
        }

        // ------------------------------------------------------------------ ws.query

        private static async Task<long> WsCountQueryAsync(DataWorkspace ws, string body, CancellationToken ct)
        {
            var pv = await ws.PreviewAsync(SqlText.Wrap("SELECT count(*) FROM (", body, ") AS __q"), 1, ct);
            return WsLong(pv.Rows[0][0]);
        }

        private async Task<HostToolResult> WsQueryAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, IAgentApprovals approvals, CancellationToken ct)
        {
            string sql = args.ReqString("sql");
            bool openTab = args.OptBool("open_tab") ?? false;
            string title = args.OptString("title") is { Length: > 0 } t ? t : "query";
            var options = Options();
            int cap = (int)Math.Min(args.OptInt("max_rows", 1, 5000) ?? 20, Math.Clamp(options.MaxRowsPerRequest, 1, 5000));

            var analysis = ws.Analyze(sql);
            if (!analysis.IsValid)
                throw new AgentToolException(analysis.Error + WorkspaceQueryException.Where(analysis.Line, analysis.Column));
            string body = SqlText.StripTerminator(sql);

            var json = new JsonObject();
            var notes = new JsonArray();
            string tabNote = "";
            if (openTab)
            {
                var tab = await wh.OpenQueryResultAsync(sql, title, ct);
                json["opened_tab"] = tab.Name;
                json["active_tab"] = tab.Name;
                tabNote = $" Opened as tab '{tab.Name}' (csv.* tools now act on it).";
            }

            bool rowsMode = options.DataPolicy != AgentDataPolicy.SummaryOnly;
            QueryPreview? preview = null;
            if (rowsMode)
            {
                if (options.DataPolicy == AgentDataPolicy.RowsWithApproval)
                {
                    var cols = (await ws.PreviewAsync(sql, 1, ct)).Columns;
                    var lines = new List<string> { L("  SQL:", "  SQL:") };
                    lines.AddRange(sql.Split('\n').Take(12).Select(l => "  " + ToolJson.OneLine(l.TrimEnd(), 160)));
                    lines.Add(L("  Columns:", "  컬럼:"));
                    lines.AddRange(cols.Take(60).Select(c => "  · " + c.Name));
                    bool ok = await approvals.ApproveAsync(
                        L("Share query result rows with the AI", "AI에게 질의 결과 행 공유"),
                        L($"Up to {cap:N0} row(s) × {cols.Count} column(s) of a query result", $"질의 결과 최대 {cap:N0}행 × {cols.Count}개 컬럼"),
                        lines, ct, ApprovalKind.RowSharing);
                    if (!ok)
                    {
                        rowsMode = false;
                        notes.Add("The user declined to share rows; only aggregates are included.");
                    }
                }
                if (rowsMode) preview = await ws.PreviewAsync(sql, cap, ct);
            }

            if (preview is not null)
            {
                var cols = new JsonArray();
                foreach (var c in preview.Columns) cols.Add(new JsonObject { ["name"] = c.Name, ["type"] = c.SqlType });
                var rows = new JsonArray();
                int chars = 0, included = 0;
                bool budgetHit = false;
                foreach (var r in preview.Rows)
                {
                    var arr = new JsonArray();
                    foreach (string? cell in r)
                    {
                        string? s = cell is null ? null : ToolJson.Clip(cell, MaxCellChars);
                        chars += (s?.Length ?? 4) + 3;
                        arr.Add(s is null ? null : JsonValue.Create(s));
                    }
                    if (chars > RowsOutputBudgetChars && included > 0) { budgetHit = true; break; }
                    rows.Add(arr);
                    included++;
                }
                long total = preview.Truncated ? await WsCountQueryAsync(ws, body, ct) : preview.Rows.Count;
                json["row_count"] = total;
                json["columns"] = cols;
                json["rows"] = rows;
                json["rows_returned"] = included;
                if (preview.Truncated || budgetHit)
                    json["truncated"] = $"Showing {included:N0} of {total:N0} rows (cap {cap:N0}). Add LIMIT/aggregation or open_tab:true to show the user everything.";
                if (notes.Count > 0) json["notes"] = notes;
                return Reply($"Query returned {total:N0} row(s), {preview.Columns.Count} column(s); showing {included:N0}.{tabNote}", json);
            }

            // SummaryOnly(또는 공유 거절): 집계만.
            var described = await ws.PreviewAsync(sql, 1, ct);
            var columns = described.Columns;
            var parts = new List<string> { "count(*)" };
            for (int i = 0; i < columns.Count; i++)
            {
                parts.Add($"count(c{i})");
                parts.Add($"count(DISTINCT c{i})");
                if (WsIsNumeric(columns[i].Type)) { parts.Add($"min(c{i})"); parts.Add($"max(c{i})"); parts.Add($"avg(c{i})"); }
            }
            string alias = string.Join(", ", Enumerable.Range(0, columns.Count).Select(i => "c" + i));
            string agg = SqlText.Wrap($"SELECT {string.Join(", ", parts)} FROM (", body, $") AS __q({alias})");
            var res = await ws.PreviewAsync(agg, 1, ct);
            var vals = res.Rows[0];
            long rowCount = WsLong(vals[0]);
            int p = 1;
            var colJson = new JsonArray();
            for (int i = 0; i < columns.Count; i++)
            {
                var o = new JsonObject
                {
                    ["name"] = columns[i].Name,
                    ["type"] = columns[i].SqlType,
                    ["non_null"] = WsLong(vals[p++]),
                    ["distinct"] = WsLong(vals[p++]),
                };
                if (WsIsNumeric(columns[i].Type))
                {
                    o["min"] = WsScalar(vals[p++]);
                    o["max"] = WsScalar(vals[p++]);
                    o["mean"] = WsScalar(vals[p++]);
                }
                colJson.Add(o);
            }
            json["row_count"] = rowCount;
            json["columns"] = colJson;
            if (options.DataPolicy == AgentDataPolicy.SummaryOnly)
                notes.Add("Rows omitted: the data policy is SummaryOnly (raw cell values are not shared). Use aggregates in SQL (count, sum, avg, GROUP BY) or open_tab:true to show the user the rows.");
            json["notes"] = notes;
            return Reply($"Query returns {rowCount:N0} row(s), {columns.Count} column(s) (aggregates only).{tabNote}", json);
        }

        // ------------------------------------------------------------------ ws.check_join

        private async Task<HostToolResult> WsCheckJoinAsync(ToolArgs args, DataWorkspace ws, CancellationToken ct)
        {
            var left = WsResolve(ws, args.ReqString("left"), "left");
            var right = WsResolve(ws, args.ReqString("right"), "right");
            var keys = WsKeys(args, left, right);
            var kind = (args.OptEnum("kind", "inner", "left", "right", "full") ?? "inner").ToLowerInvariant() switch
            {
                "left" => JoinKind.Left,
                "right" => JoinKind.Right,
                "full" => JoinKind.Full,
                _ => JoinKind.Inner,
            };
            var spec = new JoinSpec(left, right, keys, kind);
            var d = await ws.CheckJoinAsync(spec, ct);

            var warnings = new JsonArray();
            foreach (string w in d.KeyTypeWarnings) warnings.Add(w);
            if (d.LeftRows > 0 && d.RightRows > 0 && d.MatchedKeys == 0)
                warnings.Add("NO key matches at all: the columns are probably wrong, or the values differ in format (leading zeros, spaces, case, type).");
            else if (d.MatchedKeys > 0)
            {
                double leftMatch = d.LeftDistinctKeys == 0 ? 0 : (double)d.MatchedKeys / d.LeftDistinctKeys;
                double rightMatch = d.RightDistinctKeys == 0 ? 0 : (double)d.MatchedKeys / d.RightDistinctKeys;
                if (leftMatch < 0.5) warnings.Add($"Only {leftMatch:P0} of the left keys have a match on the right.");
                if (rightMatch < 0.5) warnings.Add($"Only {rightMatch:P0} of the right keys have a match on the left.");
            }
            if (d.Cardinality == JoinCardinality.ManyToMany)
                warnings.Add("Many-to-many join: duplicate keys on BOTH sides multiply rows. Deduplicate or aggregate one side first.");
            long biggest = Math.Max(d.LeftRows, d.RightRows);
            if (biggest > 0 && d.ExpectedRows > 2 * biggest)
                warnings.Add($"The result would have {d.ExpectedRows:N0} rows, {(double)d.ExpectedRows / biggest:0.#}× the larger input.");
            if (d.LeftNullKeyRows > 0 || d.RightNullKeyRows > 0)
                warnings.Add("Rows with an empty/NULL key never match (left " + d.LeftNullKeyRows.ToString("N0") + ", right " + d.RightNullKeyRows.ToString("N0") + ").");

            var json = new JsonObject
            {
                ["left"] = left.DisplayName,
                ["right"] = right.DisplayName,
                ["kind"] = kind.ToString().ToLowerInvariant(),
                ["left_rows"] = d.LeftRows,
                ["right_rows"] = d.RightRows,
                ["left_null_key_rows"] = d.LeftNullKeyRows,
                ["right_null_key_rows"] = d.RightNullKeyRows,
                ["left_distinct_keys"] = d.LeftDistinctKeys,
                ["right_distinct_keys"] = d.RightDistinctKeys,
                ["matched_keys"] = d.MatchedKeys,
                ["left_only_keys"] = d.LeftOnlyKeys,
                ["right_only_keys"] = d.RightOnlyKeys,
                ["left_duplicate_keys"] = d.LeftDuplicateKeys,
                ["right_duplicate_keys"] = d.RightDuplicateKeys,
                ["left_max_rows_per_key"] = d.LeftMaxKeyRows,
                ["right_max_rows_per_key"] = d.RightMaxKeyRows,
                ["inner_join_rows"] = d.MatchedRowsInner,
                ["expected_rows"] = d.ExpectedRows,
                ["growth_vs_left"] = d.LeftRows == 0 ? null : ToolJson.Num((double)d.ExpectedRows / d.LeftRows),
                ["growth_vs_right"] = d.RightRows == 0 ? null : ToolJson.Num((double)d.ExpectedRows / d.RightRows),
                ["cardinality"] = d.Cardinality switch
                {
                    JoinCardinality.OneToOne => "one-to-one",
                    JoinCardinality.OneToMany => "one-to-many",
                    JoinCardinality.ManyToOne => "many-to-one",
                    _ => "many-to-many",
                },
                ["warnings"] = warnings,
                ["join_sql"] = JoinSql.Build(spec),
            };
            return Reply($"{kind} join: {d.ExpectedRows:N0} expected rows ({d.MatchedKeys:N0} matching keys, {d.Cardinality}); {warnings.Count} warning(s).", json);
        }

        // ------------------------------------------------------------------ 뷰 만들기 공통

        private async Task<HostToolResult> WsCreateViewCoreAsync(DataWorkspace ws, IWorkspaceAgentHost wh, string name, string sql, bool includeEdits,
            bool open, bool replace, string what, JsonObject extra, IReadOnlyList<string> cardNotes, IAgentApprovals approvals, CancellationToken ct)
        {
            string clean = SqlNames.Sanitize(name, "view");
            if (!string.Equals(clean, name.Trim(), StringComparison.Ordinal))
                throw new AgentToolException($"'{name}' is not a valid view name. Use letters, digits and underscores only (try '{clean}').");
            var existing = ws.Views.FirstOrDefault(v => string.Equals(v.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !replace)
                throw new AgentToolException($"A view named '{existing.Name}' already exists. Choose another name or pass replace:true to redefine it.");
            if (existing is null && WsRelationNames(ws).Any(n => string.Equals(n, clean, StringComparison.OrdinalIgnoreCase)))
                throw new AgentToolException($"The name '{clean}' is already used by a table. Choose another name.");

            var analysis = ws.Analyze(sql);
            if (!analysis.IsValid)
                throw new AgentToolException(analysis.Error + WorkspaceQueryException.Where(analysis.Line, analysis.Column));

            var lines = new List<string>
            {
                "+ " + (existing is null ? L("view ", "뷰 ") : L("REDEFINE view ", "뷰 재정의 ")) + clean,
                L("  SQL:", "  SQL:"),
            };
            lines.AddRange(sql.Split('\n').Take(14).Select(l => "  " + ToolJson.OneLine(l.TrimEnd(), 160)));
            lines.AddRange(cardNotes.Select(n => "  " + n));
            lines.Add(includeEdits
                ? L("  Uses the unsaved edits of the open files.", "  열린 파일의 저장 안 한 편집을 반영합니다.")
                : L("  Uses the saved files.", "  저장된 파일 기준입니다."));
            lines.Add(L("  Original files are never changed; the view can be removed again.", "  원본 파일은 바뀌지 않으며 뷰는 다시 지울 수 있습니다."));
            bool approved = await approvals.ApproveAsync(
                L($"Create view {clean}", $"뷰 {clean} 만들기"),
                what, lines, ct, ApprovalKind.DataEdit);
            if (!approved) throw new AgentToolException("The user did not approve creating the view. Nothing was created.");

            WorkspaceView view;
            if (existing is not null)
            {
                ws.UpdateView(existing, sql, includeEdits);
                view = existing;
            }
            else view = ws.CreateView(clean, sql, includeEdits);

            string openNote = "";
            if (open)
            {
                var tab = await wh.OpenRelationAsync(view, ct);
                if (tab is null) extra["open_failed"] = "The view could not be opened as a tab (see ws.list_tables for its error).";
                else
                {
                    extra["opened_tab"] = tab.Name;
                    extra["active_tab"] = tab.Name;
                    openNote = $" Opened as read-only tab '{tab.Name}'; csv.* tools now act on it.";
                }
            }

            extra["view"] = view.Name;
            extra["sql_reference"] = view.SqlReference;
            extra["columns"] = WsColumnsJson(view.Columns);
            if (view.ResultRowCount is { } rc) extra["rows"] = rc;
            if (view.Error is { Length: > 0 } err) extra["error"] = err;
            extra["include_unsaved_edits"] = includeEdits;
            return Reply($"{(existing is null ? "Created" : "Redefined")} view {view.Name} ({view.Columns.Count} columns).{openNote}", extra);
        }

        // ------------------------------------------------------------------ ws.create_view

        private Task<HostToolResult> WsCreateViewAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, IAgentApprovals approvals, CancellationToken ct)
            => WsCreateViewCoreAsync(ws, wh, args.ReqString("name"), args.ReqString("sql"), args.OptBool("include_unsaved_edits") ?? false,
                args.OptBool("open") ?? false, args.OptBool("replace") ?? false,
                L("Create a derived table (view) from a SELECT query", "SELECT 질의로 파생 표(뷰) 만들기"),
                new JsonObject(), Array.Empty<string>(), approvals, ct);

        // ------------------------------------------------------------------ ws.append

        private Task<HostToolResult> WsAppendAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, IAgentApprovals approvals, CancellationToken ct)
        {
            var names = args.OptStringArray("tables", 10);
            if (names is not { Count: >= 2 }) throw new AgentToolException("'tables' needs at least 2 table names.");
            var rels = names.Select(n => WsResolve(ws, n, "tables")).ToList();
            var mode = string.Equals(args.OptEnum("mode", "by_name", "by_position"), "by_position", StringComparison.OrdinalIgnoreCase)
                ? UnionMode.ByPosition : UnionMode.ByName;
            string? sourceColumn = args.OptString("source_column");
            if (string.IsNullOrWhiteSpace(sourceColumn)) sourceColumn = null;
            var spec = new AppendSpec(rels, null, sourceColumn, mode);
            var plan = WizardSql.PlanAppend(spec);
            string sql = WizardSql.AppendSql(spec);

            var extra = new JsonObject();
            var warnings = plan.Warnings;
            if (warnings.Count > 0) extra["warnings"] = ToolJson.Strings(warnings);
            extra["sql"] = sql;
            var card = warnings.Take(6).Select(w => "! " + ToolJson.OneLine(w, 160)).ToList();
            return WsCreateViewCoreAsync(ws, wh, args.ReqString("name"), sql, args.OptBool("include_unsaved_edits") ?? false,
                args.OptBool("open") ?? false, args.OptBool("replace") ?? false,
                L($"Stack {rels.Count} tables into one view", $"표 {rels.Count}개를 하나의 뷰로 이어 붙이기"),
                extra, card, approvals, ct);
        }

        // ------------------------------------------------------------------ ws.compare

        private async Task<HostToolResult> WsCompareAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, IAgentApprovals approvals, CancellationToken ct)
        {
            var left = WsResolve(ws, args.ReqString("left"), "left");
            var right = WsResolve(ws, args.ReqString("right"), "right");
            var keys = WsKeys(args, left, right);
            var colNames = args.OptStringArray("columns", 60);
            List<JoinKey>? compareCols = null;
            if (colNames is { Count: > 0 })
            {
                compareCols = new List<JoinKey>();
                foreach (string c in colNames)
                    compareCols.Add(new JoinKey(WsColumn(left, c, "columns"), WsColumn(right, c, "columns")));
            }
            var spec = new CompareSpec(left, right, keys, compareCols,
                args.OptBool("ignore_case") ?? false, args.OptBool("trim_whitespace") ?? false,
                args.OptBool("include_unchanged") ?? false, args.OptBool("long") ?? false);
            string sql = WizardSql.CompareSql(spec);

            var extra = new JsonObject { ["sql"] = sql };
            var card = new List<string>();
            var summary = await ws.PreviewAsync(WizardSql.CompareSummarySql(spec), 1, ct);
            if (summary.Rows.Count > 0)
            {
                var counts = new JsonObject();
                for (int i = 0; i < summary.Columns.Count; i++)
                {
                    counts[summary.Columns[i].Name] = WsScalar(summary.Rows[0][i]);
                    card.Add($"{summary.Columns[i].Name}: {summary.Rows[0][i]}");
                }
                extra["summary_counts"] = counts;
            }
            return await WsCreateViewCoreAsync(ws, wh, args.ReqString("name"), sql, false,
                args.OptBool("open") ?? false, args.OptBool("replace") ?? false,
                L($"Compare {left.DisplayName} with {right.DisplayName}", $"{left.DisplayName}과(와) {right.DisplayName} 비교"),
                extra, card, approvals, ct);
        }

        // ------------------------------------------------------------------ ws.group

        private Task<HostToolResult> WsGroupAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, IAgentApprovals approvals, CancellationToken ct)
        {
            var table = WsResolve(ws, args.ReqString("table"), "table");
            var groupBy = (args.OptStringArray("group_by", 10) ?? Array.Empty<string>()).Select(c => WsColumn(table, c, "group_by")).ToList();
            var items = args.OptObjectArray("aggregates", 20);
            if (items is not { Count: > 0 }) throw new AgentToolException("'aggregates' is required: at least one {function, column?, alias?}.");
            var aggs = new List<GroupAggregate>();
            foreach (var it in items)
            {
                string fn = it.ReqString("function").ToLowerInvariant();
                GroupFunction f = fn switch
                {
                    "count" => GroupFunction.Count,
                    "sum" => GroupFunction.Sum,
                    "avg" => GroupFunction.Avg,
                    "min" => GroupFunction.Min,
                    "max" => GroupFunction.Max,
                    "count_distinct" => GroupFunction.CountDistinct,
                    "median" => GroupFunction.Median,
                    _ => throw new AgentToolException($"Unknown aggregate function '{fn}'. Use count, sum, avg, min, max, count_distinct or median."),
                };
                string? col = it.OptString("column");
                if (string.IsNullOrWhiteSpace(col))
                {
                    if (f != GroupFunction.Count) throw new AgentToolException($"'{fn}' needs a 'column' (only count may omit it).");
                    col = null;
                }
                else col = WsColumn(table, col, "aggregates.column");
                string? alias = it.OptString("alias");
                aggs.Add(new GroupAggregate(f, col, string.IsNullOrWhiteSpace(alias) ? null : alias));
            }
            string sql = WizardSql.GroupSql(new GroupSpec(table, groupBy, aggs));
            return WsCreateViewCoreAsync(ws, wh, args.ReqString("name"), sql, false,
                args.OptBool("open") ?? false, args.OptBool("replace") ?? false,
                L($"Group {table.DisplayName} and aggregate", $"{table.DisplayName} 그룹 집계"),
                new JsonObject { ["sql"] = sql }, Array.Empty<string>(), approvals, ct);
        }

        // ------------------------------------------------------------------ ws.materialize

        private async Task<HostToolResult> WsMaterializeAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, IAgentApprovals approvals, CancellationToken ct)
        {
            var rel = WsResolve(ws, args.ReqString("name"), "name");
            string requested = args.ReqString("path");
            bool overwrite = args.OptBool("overwrite") ?? false;

            var info = _host.GetInfo();
            bool activeReadOnly = wh.GetTabs().FirstOrDefault(t => t.IsActive)?.IsReadOnly ?? false;
            string? baseDir = activeReadOnly || string.IsNullOrEmpty(info?.Directory) ? _host.AnalysisFolder : info!.Directory;
            if (!Path.IsPathRooted(requested) && !string.IsNullOrEmpty(baseDir)) Directory.CreateDirectory(baseDir);

            string full = AgentSavePolicy.Resolve(requested, baseDir, WsProtectedPaths(wh, ws, info), overwrite);
            bool exists = File.Exists(full);

            var lines = new List<string>
            {
                "+ " + full,
                L("  Content: ", "  내용: ") + rel.DisplayName + $" ({rel.Columns.Count} " + L("columns", "컬럼") + ")",
                L("  Original files stay unchanged.", "  원본 파일은 바뀌지 않습니다."),
            };
            if (exists) lines.Add("- " + L("An existing file with this name will be REPLACED.", "같은 이름의 기존 파일을 덮어씁니다."));
            bool approved = await approvals.ApproveAsync(
                L($"Save {rel.DisplayName} to {Path.GetFileName(full)}", $"{rel.DisplayName}을(를) {Path.GetFileName(full)}에 저장"),
                L("Write the table/view result to a new file", "표·뷰 결과를 새 파일에 저장"),
                lines, ct, ApprovalKind.FileSave);
            if (!approved) throw new AgentToolException("The user did not approve saving. Nothing was written.");

            full = AgentSavePolicy.Resolve(requested, baseDir, WsProtectedPaths(wh, ws, _host.GetInfo()), overwrite); // 승인 대기 중 바뀐 상태 재검증
            string saved = await wh.SaveRelationAsAsync(rel, full, ct);
            return Reply($"Saved {rel.DisplayName} to {saved}.", new JsonObject { ["saved_to"] = saved, ["name"] = rel.DisplayName, ["source_files_unchanged"] = true });
        }

        private static IEnumerable<string> WsProtectedPaths(IWorkspaceAgentHost wh, DataWorkspace ws, AgentDocumentInfo? info)
        {
            var set = new List<string>(wh.OpenSourcePaths());
            if (info is not null) set.AddRange(info.ProtectedPaths);
            foreach (var s in ws.Sources)
            {
                if (!string.IsNullOrEmpty(s.Path)) set.Add(s.Path);
                foreach (var t in s.Tables)
                {
                    if (!string.IsNullOrEmpty(t.FilePath)) set.Add(t.FilePath);
                    if (!string.IsNullOrEmpty(t.ReadPath)) set.Add(t.ReadPath);
                }
            }
            return set;
        }

        // ------------------------------------------------------------------ ws.open / ws.switch

        private HostToolResult WsTabReply(string verb, AgentTabInfo tab)
        {
            var json = new JsonObject
            {
                ["tab"] = tab.Name,
                ["kind"] = tab.Kind,
                ["read_only"] = tab.IsReadOnly,
            };
            var info = _host.GetInfo();
            string headline = $"{verb} '{tab.Name}'.";
            if (info is not null)
            {
                json["indexing_complete"] = info.IndexingComplete;
                json["total_rows"] = info.TotalRows;
                json["columns"] = new JsonArray(info.Columns.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["type"] = c.Type.DisplayName() }).ToArray());
                if (!info.IndexingComplete) json["note"] = "Still indexing; csv.* tools work when it finishes.";
                headline += $" {info.TotalRows:N0} rows, {info.Columns.Count} columns; csv.* tools now act on it.";
            }
            return Reply(headline, json);
        }

        private async Task<HostToolResult> WsOpenAsync(ToolArgs args, IWorkspaceAgentHost wh, DataWorkspace ws, CancellationToken ct)
        {
            var rel = WsResolve(ws, args.ReqString("name"), "name");
            var tab = await wh.OpenRelationAsync(rel, ct)
                      ?? throw new AgentToolException($"'{rel.DisplayName}' could not be opened as a tab" +
                                                      (rel is WorkspaceView { Error: { Length: > 0 } e } ? ": " + e : "."));
            return WsTabReply("Opened", tab);
        }

        private async Task<HostToolResult> WsSwitchAsync(ToolArgs args, IWorkspaceAgentHost wh, CancellationToken ct)
        {
            string name = args.ReqString("name");
            var tab = await wh.SwitchTabAsync(name, ct);
            if (tab is null)
                throw new AgentToolException($"No open tab named '{name}'. Open tabs: " + string.Join(", ", wh.GetTabs().Select(t => t.Name)) + ". Use ws.open to open a table or view.");
            return WsTabReply("Switched to", tab);
        }
    }
}
