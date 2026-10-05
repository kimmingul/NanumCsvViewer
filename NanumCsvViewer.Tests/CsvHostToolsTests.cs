using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // csv.* host tool: 가짜 호스트(메모리 표)로 인자 검증·데이터 정책·승인·편집·저장·기록을, 실제 VirtualCsvDocument로
    // "필터 → 분석" 경로의 숫자를 검증한다. 네트워크·모델 호출 없음.
    // 실제 Form1을 만드는 테스트가 있으므로 다른 Form1 테스트와 직렬화한다(WinForms KeysConverter 초기화 경쟁·SavedViewStore 경로 공유).
    [Collection("SavedViewStore")]
    public sealed partial class CsvHostToolsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "csvtools-" + Guid.NewGuid().ToString("N"));

        public CsvHostToolsTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* 무시 */ }
        }

        // ------------------------------------------------------------------ 테스트 도구

        private sealed class MemoryLog : IAgentToolLog
        {
            public readonly List<(string Tool, JsonNode? Args, bool Ok)> Entries = new();
            public void Append(string tool, JsonNode? args, bool ok, long milliseconds, string? error) => Entries.Add((tool, args?.DeepClone(), ok));
        }

        private sealed class FakeApprovals(bool answer) : IAgentApprovals
        {
            public readonly List<(string Target, string Summary, IReadOnlyList<string> Lines, ApprovalKind Kind)> Calls = new();
            public Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation, ApprovalKind kind = ApprovalKind.RowSharing)
            {
                Calls.Add((target, summary, lines, kind));
                return Task.FromResult(answer);
            }
        }

        private class HostStub : ICsvAgentHost
        {
            public virtual AgentDocumentInfo? GetInfo() => throw new NotSupportedException();
            public virtual Task<AgentRowsPage> GetRowsAsync(long firstViewRow, int count, IReadOnlyList<int> columns, CancellationToken c) => throw new NotSupportedException();
            public virtual Task<T> RunOnViewRowsAsync<T>(string description, Func<AgentViewData, CancellationToken, T> work, CancellationToken c) => throw new NotSupportedException();
            public virtual void ShowAnalysisWindow(AgentAnalysisOutcome outcome) => throw new NotSupportedException();
            public virtual Task<AgentViewChange> SetFilterAsync(string expression, bool replace, CancellationToken c) => throw new NotSupportedException();
            public virtual Task<AgentViewChange> ClearFilterAsync(CancellationToken c) => throw new NotSupportedException();
            public virtual Task<AgentViewChange> SortAsync(IReadOnlyList<SortKey> keys, CancellationToken c) => throw new NotSupportedException();
            public virtual Task<AgentGotoResult> GotoAsync(long? sourceRow, int? column, CancellationToken c) => throw new NotSupportedException();
            public virtual Task<QualityReport> RunQualityScanAsync(CancellationToken c) => throw new NotSupportedException();
            public virtual Task<AgentRegexScan> RegexCountAsync(Regex regex, IReadOnlyList<int> columns, int maxSamples, CancellationToken c) => throw new NotSupportedException();
            public virtual Task<AgentRegexPlan> PlanRegexReplaceAsync(Regex regex, string replacement, IReadOnlyList<int> columns, int maxChanges, CancellationToken c) => throw new NotSupportedException();
            public virtual IReadOnlyList<AgentCellState> GetCellStates(IReadOnlyList<(long SourceRow, int Column)> cells) => throw new NotSupportedException();
            public virtual AgentEditResult ApplyEdits(IReadOnlyList<AgentCellEdit> edits, string description) => throw new NotSupportedException();
            public virtual AgentUndoResult UndoAgentEdit() => throw new NotSupportedException();
            public virtual Task<AgentSaveResult> SaveEditsAsAsync(string fullPath, CancellationToken c) => throw new NotSupportedException();
            public virtual Task<AgentRowNumbers> GetViewRowNumbersAsync(int cap, CancellationToken c) => throw new NotSupportedException();
            public virtual AgentStructureResult InsertRows(long rowNumber, int count, string description) => throw new NotSupportedException();
            public virtual AgentStructureResult DeleteRows(IReadOnlyList<long> rowNumbers, string description) => throw new NotSupportedException();
            public virtual AgentColumnChange AddColumn(string name, string? fill, string description) => throw new NotSupportedException();
            public virtual AgentColumnChange InsertColumn(string name, int position, string? fill, string description) => throw new NotSupportedException();
            public virtual AgentColumnChange MoveColumn(int from, int to, string description) => throw new NotSupportedException();
            public virtual ConditionalFormatUndoResult? UndoConditionalFormat() => throw new NotSupportedException();
            public virtual AgentColumnChange DeleteColumn(int column, string description) => throw new NotSupportedException();
            public virtual IReadOnlyDictionary<string, string> ConditionalFormatProblems() => throw new NotSupportedException();
            public virtual IReadOnlyList<ConditionalFormatRule> ListConditionalFormats() => throw new NotSupportedException();
            public virtual ConditionalFormatRule AddConditionalFormat(ConditionalFormatRule draft) => throw new NotSupportedException();
            public virtual bool RemoveConditionalFormat(string id) => throw new NotSupportedException();
            public virtual int ClearConditionalFormats() => throw new NotSupportedException();
            public virtual Task<ConditionalFormatCount> CountConditionalFormatAsync(string? id, ConditionalFormatRule? draft, CancellationToken c) => throw new NotSupportedException();
            public virtual ViewerShowResult ShowMarkdown(string fullPath) => throw new NotSupportedException();
            public virtual ViewerShowResult ShowImage(string fullPath) => throw new NotSupportedException();
            public virtual bool PostInlineImage(string fullPath, string? caption) => throw new NotSupportedException();
        }

        /// <summary>메모리 표 호스트: 필터·정렬·편집(한 단계씩 쌓이는 이력)·저장 경로를 흉내 낸다.</summary>
        private sealed class FakeHost : HostStub
        {
            public string[] Headers = { "id", "age", "city", "group", "score" };
            public ColumnValueType[] Types =
            {
                ColumnValueType.Integer, ColumnValueType.Integer, ColumnValueType.Categorical, ColumnValueType.Categorical, ColumnValueType.Float,
            };
            public List<string[]> Rows = new()
            {
                new[] { "1", "25", "Seoul", "A", "1.5" },
                new[] { "2", "31", "Busan", "B", "2.5" },
                new[] { "3", "47", "SECRET_TOWN", "A", "3.5" },
                new[] { "4", "52", "Seoul", "B", "4.5" },
                new[] { "5", "38", "Busan", "A", "" },
            };
            public readonly Dictionary<(int Row, int Col), string> Overlay = new();
            public readonly List<(string? Description, Dictionary<(int, int), string?> Before)> Steps = new();
            public List<int> View = new();
            public readonly List<string> Expressions = new();
            public List<AgentFilterInfo> ExtraFilters = new();
            public List<SortKey> SortKeys = new();
            public bool NoDoc, Indexing, Busy;
            public string? Directory;
            public string FileName = "people.csv";
            public bool SheetMode;
            public readonly List<string> Calls = new();
            public string? SavedPath;
            public readonly List<AgentAnalysisOutcome> Shown = new();
            public QualityReport? Report;
            public long ForcedRegexTimeouts, ForcedFilterTimeouts;
            public bool ForceTruncatedPlan;

            public FakeHost() => View = Enumerable.Range(0, Rows.Count).ToList();

            public string[] Current(int i)
            {
                var row = (string[])Rows[i].Clone();
                foreach (var kv in Overlay) if (kv.Key.Row == i) row[kv.Key.Col] = kv.Value;
                return row;
            }

            private AgentEditState State()
            {
                string? top = Steps.Count > 0 ? Steps[^1].Description : null;
                return new AgentEditState(Overlay.Count, 0, DeletedRowCount, AddedRowCount, Steps.Count > 0, Steps.Count > 0, top,
                    top is not null && top.StartsWith(AgentEditTag.Prefix, StringComparison.Ordinal), SheetMode, AddedColumnCount, DeletedColumnCount, ColumnsReordered);
            }

            public override AgentDocumentInfo? GetInfo()
            {
                if (NoDoc) return null;
                var filters = Expressions.Select(e => new AgentFilterInfo(AgentFilterKind.Expression, e)).Concat(ExtraFilters).ToList();
                return new AgentDocumentInfo(
                    FileName, null, Array.Empty<string>(), "UTF-8", ",", 1234, !Indexing, Indexing ? 40 : 100, Busy,
                    Rows.Count, View.Count, false,
                    Headers.Select((h, i) => new AgentColumn(i, h, Types[i])).ToList(),
                    filters, false,
                    SortKeys.Select(k => new AgentSortInfo(Headers[k.Column], k.Ascending)).ToList(),
                    Array.Empty<string>(), State(), new AgentCursor(null, null),
                    Directory, Directory is null ? Array.Empty<string>() : new[] { Path.Combine(Directory, FileName) });
            }

            public override Task<AgentRowsPage> GetRowsAsync(long firstViewRow, int count, IReadOnlyList<int> columns, CancellationToken c)
            {
                Calls.Add($"rows:{firstViewRow}:{count}");
                var rows = new List<AgentRow>();
                for (long i = firstViewRow; i < firstViewRow + count && i < View.Count; i++)
                {
                    var r = Current(View[(int)i]);
                    rows.Add(new AgentRow(View[(int)i] + 1, columns.Select(col => r[col]).ToArray()));
                }
                return Task.FromResult(new AgentRowsPage(firstViewRow, rows, View.Count));
            }

            // 엔진(RegexReplace)을 그대로 써서 현재 뷰를 스캔한다. 시간 초과는 테스트가 강제로 얹는다(실제 250ms 대기 없이 보고 경로만 검증).
            public override Task<AgentRegexScan> RegexCountAsync(Regex regex, IReadOnlyList<int> columns, int maxSamples, CancellationToken c)
            {
                Calls.Add("regex_count");
                var s = RegexReplace.Count(i => Current(View[(int)i]), Enumerable.Range(0, View.Count).Select(i => (long)i), columns, regex, maxSamples, c);
                return Task.FromResult(new AgentRegexScan(s.RowsScanned, s.CellsMatched, s.RowsMatched, s.CellsTimedOut + ForcedRegexTimeouts,
                    s.Samples.Select(x => new AgentRegexSample(View[(int)x.DataRow] + 1, x.Column, x.Value)).ToList()));
            }

            public override Task<AgentRegexPlan> PlanRegexReplaceAsync(Regex regex, string replacement, IReadOnlyList<int> columns, int maxChanges, CancellationToken c)
            {
                Calls.Add("regex_plan");
                var p = RegexReplace.Plan(i => Current(View[(int)i]), Enumerable.Range(0, View.Count).Select(i => (long)i), columns, regex, replacement, maxChanges, c);
                return Task.FromResult(new AgentRegexPlan(
                    p.Changes.Select(x => new AgentRegexChange(View[(int)x.DataRow] + 1, x.Column, x.OldValue, x.NewValue)).ToList(),
                    p.RowsScanned, p.CellsMatched, p.CellsTimedOut + ForcedRegexTimeouts, p.Truncated || ForceTruncatedPlan));
            }

            public override Task<T> RunOnViewRowsAsync<T>(string description, Func<AgentViewData, CancellationToken, T> work, CancellationToken c)
            {
                var data = new AgentViewData(View.Select(Current).ToList(), Headers, Types);
                return Task.Run(() => work(data, c), c);
            }

            public override void ShowAnalysisWindow(AgentAnalysisOutcome outcome) => Shown.Add(outcome);

            public override Task<AgentViewChange> SetFilterAsync(string expression, bool replace, CancellationToken c)
            {
                Calls.Add("filter:" + (replace ? "replace" : "and") + ":" + expression);
                var pred = AdvancedFilterExpression.Compile(expression, Headers).Predicate;
                if (replace) { Expressions.Clear(); ExtraFilters.Clear(); View = Enumerable.Range(0, Rows.Count).Where(i => pred(Current(i))).ToList(); }
                else View = View.Where(i => pred(Current(i))).ToList();
                Expressions.Add(expression);
                return Task.FromResult(new AgentViewChange(View.Count, Rows.Count, ForcedFilterTimeouts));
            }

            public override Task<AgentViewChange> ClearFilterAsync(CancellationToken c)
            {
                Calls.Add("clear");
                Expressions.Clear();
                ExtraFilters.Clear();
                SortKeys.Clear();
                View = Enumerable.Range(0, Rows.Count).ToList();
                return Task.FromResult(new AgentViewChange(View.Count, Rows.Count));
            }

            public override Task<AgentViewChange> SortAsync(IReadOnlyList<SortKey> keys, CancellationToken c)
            {
                Calls.Add("sort:" + string.Join(",", keys.Select(k => $"{k.Column}{(k.Ascending ? "+" : "-")}")));
                SortKeys = keys.ToList();
                return Task.FromResult(new AgentViewChange(View.Count, Rows.Count));
            }

            public override Task<AgentGotoResult> GotoAsync(long? sourceRow, int? column, CancellationToken c)
            {
                Calls.Add($"goto:{sourceRow}:{column}");
                return Task.FromResult(new AgentGotoResult(sourceRow ?? 1, sourceRow ?? 1, Headers[column ?? 0]));
            }

            public override Task<QualityReport> RunQualityScanAsync(CancellationToken c)
            {
                Calls.Add("quality");
                return Task.FromResult(Report!);
            }

            public override IReadOnlyList<AgentCellState> GetCellStates(IReadOnlyList<(long SourceRow, int Column)> cells)
                => cells.Select(t => t.SourceRow >= 1 && t.SourceRow <= Rows.Count
                    ? new AgentCellState(true, Current((int)t.SourceRow - 1)[t.Column])
                    : new AgentCellState(false, "")).ToList();

            public override AgentEditResult ApplyEdits(IReadOnlyList<AgentCellEdit> edits, string description)
            {
                var before = new Dictionary<(int, int), string?>();
                int changed = 0, unchanged = 0;
                foreach (var e in edits)
                {
                    var key = ((int)e.SourceRow - 1, e.Column);
                    if (Current(key.Item1)[e.Column] == e.Value) { unchanged++; continue; }
                    before[key] = Overlay.TryGetValue(key, out var old) ? old : null;
                    Overlay[key] = e.Value;
                    changed++;
                }
                if (before.Count > 0) Steps.Add((description, before));
                return new AgentEditResult(changed, unchanged, edits.Count, State());
            }

            public void UserEdit(int row, int col, string value)
            {
                var key = (row, col);
                Steps.Add((null, new Dictionary<(int, int), string?> { [key] = Overlay.TryGetValue(key, out var o) ? o : null }));
                Overlay[key] = value;
            }

            public override AgentUndoResult UndoAgentEdit()
            {
                if (Steps.Count == 0) throw new AgentToolException("There is nothing to undo.");
                var (desc, before) = Steps[^1];
                if (desc is null || !desc.StartsWith(AgentEditTag.Prefix, StringComparison.Ordinal))
                    throw new AgentToolException("The latest edit step was not made by the agent.");
                if (Snapshots.Remove(Steps.Count - 1, out var snap))
                {
                    Rows = snap.Rows; Headers = snap.Headers; Types = snap.Types;
                    View = Enumerable.Range(0, Rows.Count).ToList();
                }
                foreach (var (key, old) in before) { if (old is null) Overlay.Remove(key); else Overlay[key] = old; }
                Steps.RemoveAt(Steps.Count - 1);
                return new AgentUndoResult(desc[AgentEditTag.Prefix.Length..], State());
            }

            public override Task<AgentSaveResult> SaveEditsAsAsync(string fullPath, CancellationToken c)
            {
                SavedPath = fullPath;
                return Task.FromResult(new AgentSaveResult(fullPath, $"{Overlay.Count} cell(s)"));
            }

            // ---- 구조 편집: 한 단계 = 표 스냅샷(되돌리기용). Steps에는 빈 사전 + 설명만 쌓는다.
            public readonly Dictionary<int, (List<string[]> Rows, string[] Headers, ColumnValueType[] Types)> Snapshots = new();
            public int AddedRowCount, DeletedRowCount, AddedColumnCount, DeletedColumnCount;
            public bool NoStructure;

            private void Step(string description)
            {
                Snapshots[Steps.Count] = (Rows.Select(r => (string[])r.Clone()).ToList(), (string[])Headers.Clone(), (ColumnValueType[])Types.Clone());
                Steps.Add((description, new Dictionary<(int, int), string?>()));
            }

            private AgentStructureResult Done(long first, int count)
            {
                View = Enumerable.Range(0, Rows.Count).ToList();
                return new AgentStructureResult(first, count, Rows.Count, State());
            }

            public override Task<AgentRowNumbers> GetViewRowNumbersAsync(int cap, CancellationToken c)
                => Task.FromResult(new AgentRowNumbers(View.Take(cap).Select(i => (long)i + 1).ToList(), View.Count > cap));

            public override AgentStructureResult InsertRows(long rowNumber, int count, string description)
            {
                Calls.Add($"insert:{rowNumber}:{count}");
                Step(description);
                for (int i = 0; i < count; i++) Rows.Insert((int)rowNumber - 1 + i, new string[Headers.Length].Select(_ => "").ToArray());
                AddedRowCount += count;
                return Done(rowNumber, count);
            }

            public override AgentStructureResult DeleteRows(IReadOnlyList<long> rowNumbers, string description)
            {
                Calls.Add("delete:" + string.Join(",", rowNumbers));
                Step(description);
                foreach (long r in rowNumbers.OrderByDescending(x => x)) Rows.RemoveAt((int)r - 1);
                DeletedRowCount += rowNumbers.Count;
                return Done(0, rowNumbers.Count);
            }

            public override AgentColumnChange AddColumn(string name, string? fill, string description)
            {
                Calls.Add($"addcol:{name}:{fill}");
                Step(description);
                Headers = Headers.Append(name).ToArray();
                Types = Types.Append(ColumnValueType.String).ToArray();
                for (int i = 0; i < Rows.Count; i++) Rows[i] = Rows[i].Append(fill ?? "").ToArray();
                AddedColumnCount++;
                return new AgentColumnChange(Headers.Length - 1, name, Headers.Length, State());
            }

            public bool ColumnsReordered;

            public override AgentColumnChange InsertColumn(string name, int position, string? fill, string description)
            {
                Calls.Add($"inscol:{name}:{position}:{fill}");
                Step(description);
                Headers = Headers.Take(position).Append(name).Concat(Headers.Skip(position)).ToArray();
                Types = Types.Take(position).Append(ColumnValueType.String).Concat(Types.Skip(position)).ToArray();
                for (int i = 0; i < Rows.Count; i++) Rows[i] = Rows[i].Take(position).Append(fill ?? "").Concat(Rows[i].Skip(position)).ToArray();
                AddedColumnCount++;
                return new AgentColumnChange(position, name, Headers.Length, State());
            }

            public override AgentColumnChange MoveColumn(int from, int to, string description)
            {
                Calls.Add($"movecol:{from}:{to}");
                Step(description);
                string name = Headers[from];
                var type = Types[from];
                Headers = Headers.Where((_, i) => i != from).ToArray();
                Types = Types.Where((_, i) => i != from).ToArray();
                var h = Headers.ToList(); h.Insert(to, name); Headers = h.ToArray();
                var t = Types.ToList(); t.Insert(to, type); Types = t.ToArray();
                for (int i = 0; i < Rows.Count; i++)
                {
                    var cells = Rows[i].ToList();
                    string v = cells[from]; cells.RemoveAt(from); cells.Insert(to, v);
                    Rows[i] = cells.ToArray();
                }
                ColumnsReordered = true;
                return new AgentColumnChange(to, name, Headers.Length, State());
            }

            public override AgentColumnChange DeleteColumn(int column, string description)
            {
                Calls.Add($"delcol:{column}");
                Step(description);
                string name = Headers[column];
                Headers = Headers.Where((_, i) => i != column).ToArray();
                Types = Types.Where((_, i) => i != column).ToArray();
                for (int i = 0; i < Rows.Count; i++) Rows[i] = Rows[i].Where((_, k) => k != column).ToArray();
                DeletedColumnCount++;
                return new AgentColumnChange(column, name, Headers.Length, State());
            }

            // ---- 조건부 서식
            public readonly List<ConditionalFormatRule> Rules = new();
            private int _nextRule = 1;
            public bool FailCount;

            public readonly Dictionary<string, string> RuleProblems = new();
            public override IReadOnlyDictionary<string, string> ConditionalFormatProblems() => RuleProblems;
            public override IReadOnlyList<ConditionalFormatRule> ListConditionalFormats() => Rules.ToList();

            public override ConditionalFormatRule AddConditionalFormat(ConditionalFormatRule draft)
            {
                var rule = draft with { Id = "cf" + _nextRule++ };
                RuleHistory.Add(("add " + rule.Id, Rules.ToList()));
                Rules.Add(rule);
                return rule;
            }

            public override bool RemoveConditionalFormat(string id)
            {
                var before = Rules.ToList();
                bool removed = Rules.RemoveAll(r => r.Id == id) > 0;
                if (removed) RuleHistory.Add(("remove " + id, before));
                return removed;
            }

            public override int ClearConditionalFormats()
            {
                int n = Rules.Count;
                if (n > 0) RuleHistory.Add(("clear", Rules.ToList()));
                Rules.Clear();
                return n;
            }

            // 서식 되돌리기: 변경 직전 규칙 집합을 쌓는다(Add/Remove/Clear는 아래 훅에서 기록).
            public readonly List<(string Description, List<ConditionalFormatRule> Before)> RuleHistory = new();
            public override ConditionalFormatUndoResult? UndoConditionalFormat()
            {
                if (RuleHistory.Count == 0) return null;
                var (desc, before) = RuleHistory[^1];
                RuleHistory.RemoveAt(RuleHistory.Count - 1);
                Rules.Clear();
                Rules.AddRange(before);
                return new ConditionalFormatUndoResult(desc, Rules.Count);
            }

            public override Task<ConditionalFormatCount> CountConditionalFormatAsync(string? id, ConditionalFormatRule? draft, CancellationToken c)
            {
                if (FailCount) throw new AgentToolException("The viewer is busy.");
                var rule = draft ?? Rules.First(r => r.Id == id);
                if (rule.Kind == ConditionalFormatKind.ColorScale)
                {
                    int col = Array.IndexOf(Headers, rule.Column);
                    long numeric = View.Count(i => double.TryParse(Current(i)[col], out _));
                    return Task.FromResult(new ConditionalFormatCount(View.Count, numeric, 0));
                }
                var pred = AdvancedFilterExpression.Compile(rule.Expression, Headers).Predicate;
                long hit = View.Count(i => pred(Current(i)));
                return Task.FromResult(new ConditionalFormatCount(View.Count, hit, 0));
            }

            // ---- 뷰어
            public readonly List<string> ShownMarkdown = new(), ShownImages = new();
            public readonly List<(string Path, string? Caption)> InlinePosts = new();
            public string? ViewerFailure;
            public bool InlineOk = true;

            public override ViewerShowResult ShowMarkdown(string fullPath)
            {
                if (ViewerFailure is not null) return new ViewerShowResult(false, ViewerFailure);
                ShownMarkdown.Add(fullPath);
                return new ViewerShowResult(true, "opened");
            }

            public override ViewerShowResult ShowImage(string fullPath)
            {
                if (ViewerFailure is not null) return new ViewerShowResult(false, ViewerFailure);
                ShownImages.Add(fullPath);
                return new ViewerShowResult(true, "opened");
            }

            public override bool PostInlineImage(string fullPath, string? caption)
            {
                InlinePosts.Add((fullPath, caption));
                return InlineOk;
            }
        }

        private static CsvHostTools Tools(ICsvAgentHost host, AgentDataPolicy policy = AgentDataPolicy.SummaryOnly, int maxRows = 200,
            IAgentToolLog? log = null, string language = "en", bool python = false)
            => new(host, () => new AgentHostOptions(Language: language, DataPolicy: policy, MaxRowsPerRequest: maxRows, AllowLocalPython: python), log ?? new MemoryLog());

        private static Task<HostToolResult> Call(CsvHostTools tools, string tool, string argsJson, IAgentApprovals? approvals = null)
        {
            var args = JsonDocument.Parse(argsJson).RootElement.Clone();
            return tools.ExecuteAsync(new HostToolCall("host_1", "call_1", tool, args), approvals ?? new FakeApprovals(true), CancellationToken.None);
        }

        private static JsonNode Payload(HostToolResult r)
        {
            int nl = r.Text.IndexOf('\n');
            return JsonNode.Parse(nl < 0 ? r.Text : r.Text[(nl + 1)..])!;
        }

        // ------------------------------------------------------------------ 정의

        [Fact]
        public void Definitions_CoverThePlanAndAreStrictAndShort()
        {
            var defs = Tools(new FakeHost()).Definitions;
            var expected = new[]
            {
                "csv.info", "csv.column_stats", "csv.get_rows", "csv.set_filter", "csv.clear_filter", "csv.sort", "csv.goto",
                "csv.run_analysis", "csv.quality_scan", "csv.edit_cells", "csv.undo", "csv.save_edits_as", "csv.regex_count", "csv.regex_replace",
                "csv.insert_rows", "csv.delete_rows", "csv.add_column", "csv.move_column", "csv.delete_column",
                "csv.format_add", "csv.format_list", "csv.format_remove", "csv.format_clear", "csv.format_undo",
                "csv.export_view", "csv.show_markdown", "csv.show_image",
                "ws.list_tables", "ws.describe", "ws.add_source", "ws.query", "ws.check_join", "ws.create_view", "ws.append", "ws.compare", "ws.group",
                "ws.materialize", "ws.open", "ws.switch", "ws.notes", "ws.set_notes",
            };
            Assert.Equal(expected.OrderBy(x => x), defs.Select(d => d.Name).OrderBy(x => x));

            foreach (var d in defs)
            {
                Assert.True(d.Description.Length <= 420, $"{d.Name} description is {d.Description.Length} chars");
                var schema = JsonNode.Parse(d.ParametersSchema)!.AsObject();
                Assert.Equal("object", (string?)schema["type"]);
                AssertStrict(d.Name, schema);
            }
        }

        private static void AssertStrict(string where, JsonNode node)
        {
            if (node is JsonObject o)
            {
                if (o["type"]?.ToString() == "object")
                    Assert.True(o["additionalProperties"] is JsonValue v && v.GetValue<bool>() == false, $"{where}: object without additionalProperties:false");
                foreach (var kv in o) if (kv.Value is not null) AssertStrict(where + "/" + kv.Key, kv.Value);
            }
            else if (node is JsonArray a)
                foreach (var item in a) if (item is not null) AssertStrict(where + "[]", item);
        }

        // ------------------------------------------------------------------ 상태 오류

        [Fact]
        public async Task NoDocument_BusyAndIndexing_AreErrorsNotExceptions()
        {
            var host = new FakeHost { NoDoc = true };
            var tools = Tools(host);
            Assert.True((await Call(tools, "csv.get_rows", "{}")).IsError);
            var info = await Call(tools, "csv.info", "{}");
            Assert.False(info.IsError);
            Assert.False((bool)Payload(info)["open"]!);

            host.NoDoc = false;
            host.Busy = true;
            var busy = await Call(tools, "csv.set_filter", """{"expression":"age > 30"}""");
            Assert.True(busy.IsError);
            Assert.Contains("busy", busy.Text);

            host.Busy = false;
            host.Indexing = true;
            var indexing = await Call(tools, "csv.sort", """{"keys":[{"column":"age"}]}""");
            Assert.True(indexing.IsError);
            Assert.Contains("indexed", indexing.Text);
            Assert.DoesNotContain(host.Calls, c => c.StartsWith("sort", StringComparison.Ordinal));
        }

        [Fact]
        public async Task UnknownTool_And_BadArguments_AreErrors()
        {
            var tools = Tools(new FakeHost());
            Assert.True((await Call(tools, "csv.nope", "{}")).IsError);
            var bad = await Call(tools, "csv.goto", """{"row":"abc"}""");
            Assert.True(bad.IsError);
            Assert.Contains("'row'", bad.Text);
            Assert.True((await Call(tools, "csv.goto", "{}")).IsError);
        }

        // ------------------------------------------------------------------ info

        [Fact]
        public async Task Info_ReportsSchemaAndRedactsCellValuedFiltersUnderSummaryOnly()
        {
            var host = new FakeHost();
            host.Expressions.Add("age > 30");
            host.ExtraFilters.Add(new AgentFilterInfo(AgentFilterKind.CellValue, "city = SECRET_TOWN"));

            var summary = await Call(Tools(host), "csv.info", "{}");
            Assert.False(summary.IsError);
            var json = Payload(summary);
            Assert.Equal(5, json["columns"]!.AsArray().Count);
            Assert.Equal("Integer", (string?)json["columns"]![1]!["type"]);
            Assert.Equal("SummaryOnly", (string?)json["data_policy"]);
            Assert.Contains("age > 30", summary.Text);
            Assert.DoesNotContain("SECRET_TOWN", summary.Text);

            var allowed = await Call(Tools(host, AgentDataPolicy.RowsAllowed), "csv.info", "{}");
            Assert.Contains("SECRET_TOWN", allowed.Text);
        }

        // ------------------------------------------------------------------ get_rows 정책

        [Fact]
        public async Task GetRows_SummaryOnly_IsRefusedWithGuidanceAndTouchesNothing()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var r = await Call(Tools(host), "csv.get_rows", "{}", approvals);
            Assert.True(r.IsError);
            Assert.Contains("SummaryOnly", r.Text);
            Assert.Contains("settings", r.Text);
            Assert.Empty(host.Calls);
            Assert.Empty(approvals.Calls);
        }

        [Fact]
        public async Task GetRows_RowsWithApproval_ShowsColumnsAndCount_AndHonorsDenial()
        {
            var host = new FakeHost();
            var denied = new FakeApprovals(false);
            var r = await Call(Tools(host, AgentDataPolicy.RowsWithApproval), "csv.get_rows", """{"count":3,"columns":["id","city"]}""", denied);
            Assert.True(r.IsError);
            Assert.Empty(host.Calls);
            var card = Assert.Single(denied.Calls);
            Assert.Contains("3 row(s) × 2 column(s)", card.Summary);
            Assert.Equal(ApprovalKind.RowSharing, card.Kind);
            Assert.Contains("  · city", card.Lines);

            var ok = await Call(Tools(host, AgentDataPolicy.RowsWithApproval), "csv.get_rows", """{"count":3,"columns":["id","city"]}""", new FakeApprovals(true));
            Assert.False(ok.IsError);
            var rows = Payload(ok)["rows"]!.AsArray();
            Assert.Equal(3, rows.Count);
            Assert.Equal(1, (int)rows[0]![0]!);
            Assert.Equal("Seoul", (string?)rows[0]![2]);
        }

        [Fact]
        public async Task GetRows_RowsAllowed_CapsAtMaxRowsAndSaysSo()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(false);
            var r = await Call(Tools(host, AgentDataPolicy.RowsAllowed, maxRows: 2), "csv.get_rows", """{"count":10}""", approvals);
            Assert.False(r.IsError);
            Assert.Empty(approvals.Calls);
            var json = Payload(r);
            Assert.Equal(2, json["returned"]!.GetValue<int>());
            Assert.Contains("Requested 10", json["notes"]![0]!.ToString());
        }

        [Fact]
        public async Task GetRows_FromBeyondEnd_IsAnError()
        {
            var r = await Call(Tools(new FakeHost(), AgentDataPolicy.RowsAllowed), "csv.get_rows", """{"from":99}""");
            Assert.True(r.IsError);
            Assert.Contains("5", r.Text);
        }

        // ------------------------------------------------------------------ column_stats

        [Fact]
        public async Task ColumnStats_SummaryOnly_GivesAggregatesAndOmitsTopValues()
        {
            var host = new FakeHost();
            var r = await Call(Tools(host), "csv.column_stats", """{"top_values":3}""");
            Assert.False(r.IsError);
            Assert.DoesNotContain("SECRET_TOWN", r.Text);
            var json = Payload(r);
            var cols = json["columns"]!.AsArray();
            var age = cols.First(c => (string?)c!["name"] == "age")!;
            Assert.Equal(5, age["rows"]!.GetValue<int>());
            Assert.Equal(25, age["numeric"]!["min"]!.GetValue<double>());
            Assert.Equal(52, age["numeric"]!["max"]!.GetValue<double>());
            Assert.Equal(38.6, age["numeric"]!["mean"]!.GetValue<double>(), 6);
            var score = cols.First(c => (string?)c!["name"] == "score")!;
            Assert.Equal(1, score["missing"]!.GetValue<int>());
            var city = cols.First(c => (string?)c!["name"] == "city")!;
            Assert.Equal(3, city["distinct"]!.GetValue<int>());
            Assert.Null(city["top_values"]);
            Assert.Contains("SummaryOnly", json["notes"]![0]!.ToString());
        }

        [Fact]
        public async Task ColumnStats_TopValues_RowsAllowedIncludes_RowsWithApprovalAsksFirst()
        {
            var host = new FakeHost();
            var allowed = await Call(Tools(host, AgentDataPolicy.RowsAllowed), "csv.column_stats", """{"columns":["city"],"top_values":2}""");
            var top = Payload(allowed)["columns"]![0]!["top_values"]!.AsArray();
            Assert.Equal(2, top.Count);
            Assert.Equal(2, top[0]!["count"]!.GetValue<int>()); // Seoul ×2 와 Busan ×2: 값 오름차순 동률 → Busan 먼저

            var ap = new FakeApprovals(false);
            var denied = await Call(Tools(host, AgentDataPolicy.RowsWithApproval), "csv.column_stats", """{"columns":["city"],"top_values":2}""", ap);
            Assert.True(denied.IsError);
            Assert.Single(ap.Calls);

            var plain = new FakeApprovals(false);
            var aggregates = await Call(Tools(host, AgentDataPolicy.RowsWithApproval), "csv.column_stats", """{"columns":["city"]}""", plain);
            Assert.False(aggregates.IsError);
            Assert.Empty(plain.Calls);
        }

        [Fact]
        public async Task ColumnStats_UnknownColumn_ListsAvailableOnes()
        {
            var r = await Call(Tools(new FakeHost()), "csv.column_stats", """{"columns":["agee"]}""");
            Assert.True(r.IsError);
            Assert.Contains("Unknown column 'agee'", r.Text);
            Assert.Contains("age", r.Text);
        }

        // ------------------------------------------------------------------ 필터 · 정렬 · 이동

        [Fact]
        public async Task SetFilter_AppliesReplaceAndNarrowing_AndExplainsSyntaxErrors()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            var first = await Call(tools, "csv.set_filter", """{"expression":"age > 30"}""");
            Assert.False(first.IsError);
            Assert.Equal(4, Payload(first)["view_rows"]!.GetValue<int>());

            var narrowed = await Call(tools, "csv.set_filter", """{"expression":"age < 50","mode":"and"}""");
            Assert.Equal(3, Payload(narrowed)["view_rows"]!.GetValue<int>());
            Assert.Contains("filter:and:age < 50", host.Calls);

            var replaced = await Call(tools, "csv.set_filter", """{"expression":"age < 30"}""");
            Assert.Equal(1, Payload(replaced)["view_rows"]!.GetValue<int>());
            Assert.Contains("filter:replace:age < 30", host.Calls);

            int callsBefore = host.Calls.Count;
            var bad = await Call(tools, "csv.set_filter", """{"expression":"nosuchcolumn = 1"}""");
            Assert.True(bad.IsError);
            Assert.Contains("Syntax:", bad.Text);
            Assert.Equal(callsBefore, host.Calls.Count);
        }

        [Fact]
        public async Task SetFilter_EmptyResult_IsReportedAsAWarning()
        {
            var r = await Call(Tools(new FakeHost()), "csv.set_filter", """{"expression":"age > 999"}""");
            Assert.False(r.IsError);
            Assert.Equal(0, Payload(r)["view_rows"]!.GetValue<int>());
            Assert.Contains("No row matches", Payload(r)["warnings"]![0]!.ToString());
        }

        [Fact]
        public async Task Sort_ResolvesColumnsRejectsDuplicatesAndClears()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            var ok = await Call(tools, "csv.sort", """{"keys":[{"column":"Group"},{"column":"score","order":"desc"}]}""");
            Assert.False(ok.IsError);
            Assert.Contains("sort:3+,4-", host.Calls);

            var dup = await Call(tools, "csv.sort", """{"keys":[{"column":"age"},{"column":"age","order":"desc"}]}""");
            Assert.True(dup.IsError);

            var clear = await Call(tools, "csv.sort", """{"keys":[]}""");
            Assert.False(clear.IsError);
            Assert.Contains("sort:", host.Calls);
            Assert.True((await Call(tools, "csv.sort", """{"keys":[{"column":"age","order":"sideways"}]}""")).IsError);
        }

        [Fact]
        public async Task Goto_NeedsRowOrColumn_AndPassesBoth()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            Assert.True((await Call(tools, "csv.goto", "{}")).IsError);
            var r = await Call(tools, "csv.goto", """{"row":4,"column":"city"}""");
            Assert.False(r.IsError);
            Assert.Contains("goto:4:2", host.Calls);
        }

        [Fact]
        public async Task ClearFilter_CallsHost()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            await Call(tools, "csv.set_filter", """{"expression":"age > 30"}""");
            var r = await Call(tools, "csv.clear_filter", "{}");
            Assert.False(r.IsError);
            Assert.Equal(5, Payload(r)["view_rows"]!.GetValue<int>());
        }

        // ------------------------------------------------------------------ 정규식 도구 · 새 필터 문법

        private sealed class CallbackApprovals(Action onAsk) : IAgentApprovals
        {
            public Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation, ApprovalKind kind = ApprovalKind.RowSharing)
            {
                onAsk();
                return Task.FromResult(true);
            }
        }

        [Fact]
        public async Task SetFilter_NewGrammar_ReachesTheHost_AndTimeoutsAreWarned()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            Assert.Equal(2, Payload(await Call(tools, "csv.set_filter", """{"expression":"city !matches \"^S\""}"""))["view_rows"]!.GetValue<int>());
            Assert.Equal(1, Payload(await Call(tools, "csv.set_filter", """{"expression":"* matches \"secret\""}"""))["view_rows"]!.GetValue<int>());
            Assert.Equal(3, Payload(await Call(tools, "csv.set_filter", """{"expression":"NOT (city = \"Seoul\")"}"""))["view_rows"]!.GetValue<int>());
            Assert.Equal(1, Payload(await Call(tools, "csv.set_filter", """{"expression":"city matches_cs \"^S\"","mode":"and"}"""))["view_rows"]!.GetValue<int>());

            host.ForcedFilterTimeouts = 4;
            var timedOut = await Call(tools, "csv.set_filter", """{"expression":"city matches \"x\""}""");
            Assert.False(timedOut.IsError);
            var json = Payload(timedOut);
            Assert.Equal(4, json["regex_cells_timed_out"]!.GetValue<int>());
            Assert.Contains(json["warnings"]!.AsArray(), w => w!.ToString().Contains("time limit"));
        }

        [Fact]
        public async Task SetFilter_InvalidRegex_IsAnErrorWithoutTouchingTheView()
        {
            var host = new FakeHost();
            var r = await Call(Tools(host), "csv.set_filter", """{"expression":"city matches \"(\""}""");
            Assert.True(r.IsError);
            Assert.Contains("Syntax:", r.Text);
            Assert.DoesNotContain(host.Calls, c => c.StartsWith("filter", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RegexCount_SummaryOnly_ReportsCountsAndRowNumbersButNoValues()
        {
            var approvals = new FakeApprovals(true);
            var r = await Call(Tools(new FakeHost()), "csv.regex_count", """{"pattern":"^S","columns":["city"]}""", approvals);
            Assert.False(r.IsError, r.Text);
            var json = Payload(r);
            Assert.Equal(5, json["rows_scanned"]!.GetValue<int>());
            Assert.Equal(3, json["cells_matched"]!.GetValue<int>());
            Assert.Equal(3, json["rows_matched"]!.GetValue<int>());
            Assert.Equal(0, json["cells_timed_out"]!.GetValue<int>());
            var examples = json["examples"]!.AsArray();
            Assert.Equal(new[] { 1, 3, 4 }, examples.Select(e => e!["row"]!.GetValue<int>()).ToArray());
            Assert.All(examples, e => Assert.Null(e!["value"]));
            Assert.DoesNotContain("SECRET_TOWN", r.Text);
            Assert.Empty(approvals.Calls);
        }

        [Fact]
        public async Task RegexCount_RowsWithApproval_ShowsTheValuesOnTheCardBeforeSharingThem()
        {
            var tools = Tools(new FakeHost(), AgentDataPolicy.RowsWithApproval);

            var yes = new FakeApprovals(true);
            var shared = await Call(tools, "csv.regex_count", """{"pattern":"^S","columns":["city"]}""", yes);
            var card = Assert.Single(yes.Calls);
            Assert.Contains("+ 3 · city: SECRET_TOWN", card.Lines);
            Assert.Equal(ApprovalKind.RowSharing, card.Kind);
            Assert.Equal("SECRET_TOWN", (string?)Payload(shared)["examples"]![1]!["value"]);

            var no = new FakeApprovals(false);
            var declined = await Call(tools, "csv.regex_count", """{"pattern":"^S","columns":["city"]}""", no);
            Assert.False(declined.IsError);
            Assert.Single(no.Calls);
            Assert.DoesNotContain("SECRET_TOWN", declined.Text);
            Assert.Equal(3, Payload(declined)["cells_matched"]!.GetValue<int>());
            Assert.Equal(3, Payload(declined)["examples"]!.AsArray().Count);
            Assert.Contains("declined", declined.Text);

            var none = new FakeApprovals(true);
            await Call(tools, "csv.regex_count", """{"pattern":"^S","columns":["city"],"max_samples":0}""", none);
            Assert.Empty(none.Calls);   // 공유할 값이 없으면 묻지 않는다
        }

        [Fact]
        public async Task RegexCount_RowsAllowed_ListsCappedSamples_AndValidatesArguments()
        {
            var tools = Tools(new FakeHost(), AgentDataPolicy.RowsAllowed);
            var approvals = new FakeApprovals(true);
            var r = await Call(tools, "csv.regex_count", """{"pattern":"^S","columns":["city"],"max_samples":2}""", approvals);
            var json = Payload(r);
            Assert.Equal(3, json["cells_matched"]!.GetValue<int>());
            Assert.Equal(2, json["examples"]!.AsArray().Count);
            Assert.Equal("Seoul", (string?)json["examples"]![0]!["value"]);
            Assert.Empty(approvals.Calls);

            Assert.True((await Call(tools, "csv.regex_count", """{"pattern":"x","max_samples":21}""")).IsError);
            Assert.True((await Call(tools, "csv.regex_count", """{"pattern":""}""")).IsError);
            Assert.True((await Call(tools, "csv.regex_count", """{"columns":["city"]}""")).IsError);
            Assert.True((await Call(tools, "csv.regex_count", """{"pattern":"x","columns":["nope"]}""")).IsError);
        }

        [Fact]
        public async Task RegexCount_ScansTheCurrentViewAndAllColumnsByDefault_AndHonoursCase()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            await Call(tools, "csv.set_filter", """{"expression":"age > 30"}""");   // 행 2~5
            var view = Payload(await Call(tools, "csv.regex_count", """{"pattern":"^S","columns":["city"]}"""));
            Assert.Equal(4, view["rows_scanned"]!.GetValue<int>());
            Assert.Equal(2, view["cells_matched"]!.GetValue<int>());

            await Call(tools, "csv.clear_filter", "{}");
            var all = Payload(await Call(tools, "csv.regex_count", """{"pattern":"^bus"}"""));
            Assert.Equal(5, all["columns_scanned"]!.GetValue<int>());
            Assert.Equal(2, all["cells_matched"]!.GetValue<int>());

            Assert.Equal(3, Payload(await Call(tools, "csv.regex_count", """{"pattern":"^s","columns":["city"]}"""))["cells_matched"]!.GetValue<int>());
            Assert.Equal(0, Payload(await Call(tools, "csv.regex_count", """{"pattern":"^s","columns":["city"],"case_sensitive":true}"""))["cells_matched"]!.GetValue<int>());
        }

        [Fact]
        public async Task RegexCount_TimedOutCellsAreReportedNotSwallowed()
        {
            var host = new FakeHost { ForcedRegexTimeouts = 7 };
            var r = await Call(Tools(host), "csv.regex_count", """{"pattern":"^S","columns":["city"]}""");
            Assert.False(r.IsError);
            var json = Payload(r);
            Assert.Equal(7, json["cells_timed_out"]!.GetValue<int>());
            Assert.Contains("NOT evaluated", json["warning"]!.ToString());
            Assert.Contains("7 cell(s) timed out", r.Text);
        }

        [Fact]
        public async Task RegexTools_InvalidPattern_IsAnEnglishErrorAndNothingRuns()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            var count = await Call(tools, "csv.regex_count", """{"pattern":"(unclosed"}""");
            Assert.True(count.IsError);
            Assert.StartsWith("Invalid regular expression", count.Text);
            var replace = await Call(tools, "csv.regex_replace", """{"pattern":"[a-","replacement":"x","columns":["city"]}""");
            Assert.True(replace.IsError);
            Assert.StartsWith("Invalid regular expression", replace.Text);
            Assert.DoesNotContain(host.Calls, c => c.StartsWith("regex", StringComparison.Ordinal));
        }

        [Fact]
        public async Task RegexReplace_ShowsCardRegardlessOfPolicy_AppliesAsOneTaggedUndoStep_AndUndoReverts()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);     // SummaryOnly: 카드는 로컬이라 값을 그대로 보여 준다
            var r = await Call(tools, "csv.regex_replace", """{"pattern":"(?<c>Seoul|Busan)","replacement":"${c}-KR","columns":["city"]}""", approvals);
            Assert.False(r.IsError, r.Text);

            var card = Assert.Single(approvals.Calls);
            Assert.Contains("- Seoul", card.Lines);
            Assert.Contains("+ Seoul-KR", card.Lines);
            Assert.Contains("-4 +4", card.Summary);
            Assert.Contains("rows 1–5", card.Summary);
            Assert.DoesNotContain(card.Lines, l => l.Contains("SECRET_TOWN"));   // 바뀌지 않는 셀은 카드에 없다

            var json = Payload(r);
            Assert.Equal(4, json["changed_cells"]!.GetValue<int>());
            Assert.Equal(0, json["cells_timed_out"]!.GetValue<int>());
            Assert.Equal(1, json["undo_steps_added"]!.GetValue<int>());
            Assert.Null(json["examples"]);                    // SummaryOnly: 개수만
            Assert.DoesNotContain("-KR", r.Text);

            var step = Assert.Single(host.Steps);
            Assert.StartsWith(AgentEditTag.Prefix, step.Description);
            Assert.Equal(4, step.Before.Count);
            Assert.Equal("Seoul-KR", host.Current(0)[2]);
            Assert.Equal("SECRET_TOWN", host.Current(2)[2]);

            var undone = await Call(tools, "csv.undo", "{}");
            Assert.False(undone.IsError);
            Assert.Equal("Seoul", host.Current(0)[2]);
            Assert.Equal("Busan", host.Current(4)[2]);
            Assert.Empty(host.Steps);
        }

        [Fact]
        public async Task RegexReplace_ResultExamples_OnlyWhenRowsAllowed()
        {
            foreach (var (policy, expectExamples) in new[]
            {
                (AgentDataPolicy.SummaryOnly, false), (AgentDataPolicy.RowsWithApproval, false), (AgentDataPolicy.RowsAllowed, true),
            })
            {
                var r = await Call(Tools(new FakeHost(), policy), "csv.regex_replace", """{"pattern":"^Busan$","replacement":"Pusan","columns":["city"]}""");
                Assert.False(r.IsError);
                Assert.Equal(expectExamples, Payload(r)["examples"] is not null);
                Assert.Equal(expectExamples, r.Text.Contains("Pusan"));
            }
        }

        [Fact]
        public async Task RegexReplace_Denied_ChangesNothing()
        {
            var host = new FakeHost();
            var r = await Call(Tools(host), "csv.regex_replace", """{"pattern":"Seoul","replacement":"X","columns":["city"]}""", new FakeApprovals(false));
            Assert.True(r.IsError);
            Assert.Contains("did not approve", r.Text);
            Assert.Empty(host.Steps);
            Assert.Equal("Seoul", host.Current(0)[2]);
        }

        [Fact]
        public async Task RegexReplace_DollarSyntax_AndEmptyReplacement_FollowDotNetRules()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            await Call(tools, "csv.regex_replace", """{"pattern":"^Busan$","replacement":"$$","columns":["city"]}""");
            Assert.Equal("$", host.Current(1)[2]);
            await Call(tools, "csv.regex_replace", """{"pattern":"eoul","replacement":"","columns":["city"]}""");
            Assert.Equal("S", host.Current(0)[2]);
            Assert.Equal(2, host.Steps.Count);
        }

        [Fact]
        public async Task RegexReplace_NothingToChange_NeedsNoApproval_AndStillReportsTimeouts()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);
            var none = await Call(tools, "csv.regex_replace", """{"pattern":"zzz","replacement":"y","columns":["city"]}""", approvals);
            Assert.False(none.IsError);
            Assert.Contains("no cell matches", none.Text);
            var same = await Call(tools, "csv.regex_replace", """{"pattern":"^Seoul$","replacement":"Seoul","columns":["city"]}""", approvals);
            Assert.Contains("same text", same.Text);
            Assert.Equal(2, Payload(same)["cells_matched"]!.GetValue<int>());
            Assert.Empty(approvals.Calls);
            Assert.Empty(host.Steps);

            host.ForcedRegexTimeouts = 2;
            var timed = await Call(tools, "csv.regex_replace", """{"pattern":"zzz","replacement":"y","columns":["city"]}""", approvals);
            Assert.Equal(2, Payload(timed)["cells_timed_out"]!.GetValue<int>());
            Assert.Contains("NOT evaluated", Payload(timed)["warning"]!.ToString());
        }

        [Fact]
        public async Task RegexReplace_TimeoutsAppearOnTheCardAndInTheResult()
        {
            var host = new FakeHost { ForcedRegexTimeouts = 3 };
            var approvals = new FakeApprovals(true);
            var r = await Call(Tools(host), "csv.regex_replace", """{"pattern":"^Busan$","replacement":"Pusan","columns":["city"]}""", approvals);
            Assert.False(r.IsError);
            Assert.Contains(Assert.Single(approvals.Calls).Lines, l => l.Contains("3 cell(s) timed out and are NOT changed"));
            var json = Payload(r);
            Assert.Equal(3, json["cells_timed_out"]!.GetValue<int>());
            Assert.NotNull(json["warning"]);
            Assert.Contains("3 cell(s) timed out", r.Text);
        }

        [Fact]
        public async Task RegexReplace_RequiresColumnsAndReplacement_BeforeAnythingRuns()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);
            Assert.True((await Call(tools, "csv.regex_replace", """{"pattern":"a","replacement":"b"}""", approvals)).IsError);
            Assert.True((await Call(tools, "csv.regex_replace", """{"pattern":"a","replacement":"b","columns":[]}""", approvals)).IsError);
            Assert.True((await Call(tools, "csv.regex_replace", """{"pattern":"a","columns":["city"]}""", approvals)).IsError);
            Assert.True((await Call(tools, "csv.regex_replace", """{"pattern":"a","replacement":"b","columns":["nope"]}""", approvals)).IsError);
            Assert.Empty(approvals.Calls);
            Assert.DoesNotContain("regex_plan", host.Calls);
        }

        [Fact]
        public async Task RegexReplace_CardShowsFirst15Changes_AndAllAreAppliedInOneStep()
        {
            var host = new FakeHost();
            host.Rows = Enumerable.Range(1, 100).Select(i => new[] { i.ToString(), "30", "x" + i, "A", "1" }).ToList();
            host.View = Enumerable.Range(0, 100).ToList();
            var approvals = new FakeApprovals(true);
            var r = await Call(Tools(host), "csv.regex_replace", """{"pattern":"^x","replacement":"y","columns":["city"]}""", approvals);
            Assert.False(r.IsError, r.Text);

            var card = Assert.Single(approvals.Calls);
            Assert.Equal(15, card.Lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
            Assert.Equal(15, card.Lines.Count(l => l.StartsWith("+ ", StringComparison.Ordinal)));
            Assert.Contains("  … and 85 more cell(s) not shown", card.Lines);
            Assert.Contains("-100 +100", card.Summary);
            Assert.Equal(100, Payload(r)["changed_cells"]!.GetValue<int>());
            Assert.Single(host.Steps);
            Assert.Equal(10, Payload(r)["example_rows"]!.AsArray().Count);
            Assert.Equal("y100", host.Current(99)[2]);
        }

        [Fact]
        public async Task RegexReplace_TooManyChanges_IsRefusedHonestly()
        {
            var host = new FakeHost { ForceTruncatedPlan = true };
            var approvals = new FakeApprovals(true);
            var r = await Call(Tools(host), "csv.regex_replace", """{"pattern":"Seoul","replacement":"X","columns":["city"]}""", approvals);
            Assert.True(r.IsError);
            Assert.Contains("Refused", r.Text);
            Assert.Contains("nothing was changed", r.Text);
            Assert.Empty(approvals.Calls);
            Assert.Empty(host.Steps);
        }

        [Fact]
        public async Task RegexReplace_CellsEditedWhileWaitingForApproval_AreNotOverwritten()
        {
            var host = new FakeHost();
            var approvals = new CallbackApprovals(() => host.UserEdit(0, 2, "Jeju"));
            var r = await Call(Tools(host), "csv.regex_replace", """{"pattern":"Seoul","replacement":"X","columns":["city"]}""", approvals);
            Assert.True(r.IsError);
            Assert.Contains("changed while waiting", r.Text);
            Assert.Equal("Jeju", host.Current(0)[2]);
            Assert.Equal("Seoul", host.Current(3)[2]);
            Assert.Single(host.Steps);    // 사용자의 편집 1단계뿐
        }

        // 실제 Form1 호스트: 필터된 뷰만 스캔하고, 행 번호는 행 머리글 번호이며, 적용은 AI 태그가 붙은 되돌리기 1단계다.
        [Fact]
        public void RealHost_RegexScanAndReplace_FollowTheFilteredViewAndUndoInOneStep()
        {
            string path = Path.Combine(_dir, "people.csv");
            File.WriteAllText(path, "id,name\n1,Kim A\n2,Lee B\n3,Kim C\n4,Park D\n5,kim E\n", new UTF8Encoding(false));
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var doc = VirtualCsvDocument.Open(path);
                    Pump(doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None));
                    Pump(doc.ApplyFilterAsync(row => row[0] != "2", null, CancellationToken.None));   // 2번 행(Lee B)을 뷰에서 뺀다
                    using var form = new Form1(new AppSettings());
                    _ = form.Handle;
                    typeof(Form1).GetField("_doc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(form, doc);
                    ICsvAgentHost host = form;

                    var regex = RegexSafety.Compile("^kim ");
                    var scan = Await(host.RegexCountAsync(regex, new[] { 1 }, 5, CancellationToken.None));
                    Assert.Equal(4, scan.RowsScanned);
                    Assert.Equal(3, scan.CellsMatched);
                    Assert.Equal(new long[] { 1, 3, 5 }, scan.Samples.Select(s => s.SourceRow).ToArray());
                    Assert.Equal(0, scan.CellsTimedOut);

                    var plan = Await(host.PlanRegexReplaceAsync(regex, "Dr. ", new[] { 1 }, 100, CancellationToken.None));
                    Assert.Equal(new long[] { 1, 3, 5 }, plan.Changes.Select(c => c.SourceRow).ToArray());
                    Assert.Equal("Kim A", plan.Changes[0].Old);
                    Assert.Equal("Dr. A", plan.Changes[0].New);
                    Assert.False(plan.Truncated);
                    Assert.Equal("Kim A", doc.GetDataRow(0)[1]);        // 계획만으로는 아무것도 바뀌지 않는다

                    var result = host.ApplyEdits(plan.Changes.Select(c => new AgentCellEdit(c.SourceRow, c.Column, c.New)).ToList(), AgentEditTag.Prefix + "regex");
                    Assert.Equal(3, result.Changed);
                    Assert.Equal("Dr. A", doc.GetDataRow(0)[1]);
                    Assert.Equal("Lee B", doc.GetDataRow(1)[1]);

                    // 편집이 반영된 현재 값을 다시 스캔한다.
                    var after = Await(host.RegexCountAsync(RegexSafety.Compile("^Dr\\. "), new[] { 1 }, 5, CancellationToken.None));
                    Assert.Equal(3, after.CellsMatched);

                    var undone = host.UndoAgentEdit();
                    Assert.Equal("regex", undone.Description);
                    Assert.Equal("Kim A", doc.GetDataRow(0)[1]);
                    Assert.Equal("kim E", doc.GetDataRow(4)[1]);
                    Assert.False(doc.Edits.CanUndo);     // 한 단계뿐이었다
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            static void Pump(Task task)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
                Assert.True(task.IsCompleted);
                task.GetAwaiter().GetResult();
            }

            static T Await<T>(Task<T> task)
            {
                Pump(task);
                return task.Result;
            }
        }

        // ------------------------------------------------------------------ 편집 · 되돌리기

        [Fact]
        public async Task EditCells_ShowsOldNewCard_AppliesAsOneUndoStep_WithoutSheetMode()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var r = await Call(Tools(host), "csv.edit_cells",
                """{"edits":[{"row":2,"column":"city","value":"Daegu"},{"row":4,"column":"age","value":"53"},{"row":1,"column":"group","value":"A"}]}""", approvals);
            Assert.False(r.IsError);

            var card = Assert.Single(approvals.Calls);
            Assert.Equal(ApprovalKind.DataEdit, card.Kind);
            Assert.Contains("- Busan", card.Lines);
            Assert.Contains("+ Daegu", card.Lines);
            Assert.Contains("- 52", card.Lines);
            Assert.Contains("+ 53", card.Lines);
            Assert.DoesNotContain(card.Lines, l => l.Contains("Row 1 "));   // 같은 값은 카드에서 제외

            var json = Payload(r);
            Assert.Equal(2, json["changed_cells"]!.GetValue<int>());
            Assert.Equal(1, json["undo_steps_added"]!.GetValue<int>());
            Assert.Contains("sheet edit mode", r.Text);
            Assert.False(json["edits"]!["sheet_edit_mode"]!.GetValue<bool>());
            Assert.Single(host.Steps);
            Assert.StartsWith(AgentEditTag.Prefix, host.Steps[0].Description);
            Assert.Equal("Daegu", host.Current(1)[2]);
        }

        [Fact]
        public async Task EditCells_Denied_ChangesNothing()
        {
            var host = new FakeHost();
            var r = await Call(Tools(host), "csv.edit_cells", """{"edits":[{"row":2,"column":"city","value":"Daegu"}]}""", new FakeApprovals(false));
            Assert.True(r.IsError);
            Assert.Contains("did not approve", r.Text);
            Assert.Empty(host.Steps);
            Assert.Equal("Busan", host.Current(1)[2]);
        }

        [Fact]
        public async Task EditCells_NoChangeNeedsNoApproval_AndBadInputIsRejectedBeforeAsking()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);
            var same = await Call(tools, "csv.edit_cells", """{"edits":[{"row":2,"column":"city","value":"Busan"}]}""", approvals);
            Assert.False(same.IsError);
            Assert.Empty(approvals.Calls);

            Assert.True((await Call(tools, "csv.edit_cells", """{"edits":[{"row":99,"column":"city","value":"x"}]}""", approvals)).IsError);
            Assert.True((await Call(tools, "csv.edit_cells", """{"edits":[{"row":1,"column":"nope","value":"x"}]}""", approvals)).IsError);
            Assert.True((await Call(tools, "csv.edit_cells", """{"edits":[{"row":1,"column":"city","value":"x"},{"row":1,"column":"city","value":"y"}]}""", approvals)).IsError);
            Assert.True((await Call(tools, "csv.edit_cells", """{"edits":[{"row":1,"column":"city"}]}""", approvals)).IsError);
            Assert.True((await Call(tools, "csv.edit_cells", """{"edits":[]}""", approvals)).IsError);
            Assert.Empty(approvals.Calls);
            Assert.Empty(host.Steps);
        }

        [Fact]
        public async Task EditCells_NumberValueIsStoredAsGivenText_AndEmptyClears()
        {
            var host = new FakeHost();
            await Call(Tools(host), "csv.edit_cells", """{"edits":[{"row":1,"column":"id","value":"001"},{"row":2,"column":"score","value":""},{"row":3,"column":"age","value":48}]}""");
            Assert.Equal("001", host.Current(0)[0]);
            Assert.Equal("", host.Current(1)[4]);
            Assert.Equal("48", host.Current(2)[1]);
        }

        [Fact]
        public async Task Undo_RevertsAgentStep_ButNeverTheUsersOwnEdit()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            await Call(tools, "csv.edit_cells", """{"edits":[{"row":2,"column":"city","value":"Daegu"}]}""");
            var undone = await Call(tools, "csv.undo", "{}");
            Assert.False(undone.IsError);
            Assert.Equal("Busan", host.Current(1)[2]);

            host.UserEdit(0, 2, "Jeju");
            var refused = await Call(tools, "csv.undo", "{}");
            Assert.True(refused.IsError);
            Assert.Equal("Jeju", host.Current(0)[2]);
        }

        [Fact]
        public void EditCard_CapsLinesAndCountsTheRest()
        {
            var changes = Enumerable.Range(1, 500).Select(i => new EditCard.Change(i, "c", "old" + i, "new" + i)).ToList();
            var lines = EditCard.Lines(changes, korean: false);
            Assert.True(lines.Count <= EditCard.MaxLines + 1);
            Assert.Contains("more cell(s) not shown", lines[^1]);
            Assert.Equal("- old1", lines[1]);
            Assert.Equal("+ new1", lines[2]);

            var multi = EditCard.Lines(new[] { new EditCard.Change(1, "c", "", "a\nb") }, korean: false);
            Assert.Equal("- (empty)", multi[1]);
            Assert.Equal("+ a⏎b", multi[2]);
        }

        // ------------------------------------------------------------------ 저장

        private FakeHost HostWithEdits()
        {
            var host = new FakeHost { Directory = _dir };
            host.UserEdit(0, 2, "Jeju");
            return host;
        }

        [Fact]
        public async Task Save_AlwaysAsks_ResolvesRelativePath_AndRefusesTheSourceFile()
        {
            var host = HostWithEdits();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host, AgentDataPolicy.RowsAllowed);

            var refused = await Call(tools, "csv.save_edits_as", """{"path":"people.csv"}""", approvals);
            Assert.True(refused.IsError);
            Assert.Contains("source file", refused.Text);
            Assert.Null(host.SavedPath);

            var absoluteSource = await Call(tools, "csv.save_edits_as", JsonSerializer.Serialize(new { path = Path.Combine(_dir, "PEOPLE.CSV"), overwrite = true }), approvals);
            Assert.True(absoluteSource.IsError);
            Assert.Empty(approvals.Calls);

            var ok = await Call(tools, "csv.save_edits_as", """{"path":"people.edited.csv"}""", approvals);
            Assert.False(ok.IsError);
            string expected = Path.Combine(_dir, "people.edited.csv");
            Assert.Equal(expected, host.SavedPath);
            var card = Assert.Single(approvals.Calls);
            Assert.Contains("+ " + expected, card.Lines);
            Assert.Equal(ApprovalKind.FileSave, card.Kind);
        }

        [Fact]
        public async Task Save_DeniedWritesNothing_ExistingNeedsOverwriteAndWarns()
        {
            var host = HostWithEdits();
            var tools = Tools(host);
            var denied = await Call(tools, "csv.save_edits_as", """{"path":"a.csv"}""", new FakeApprovals(false));
            Assert.True(denied.IsError);
            Assert.Null(host.SavedPath);

            string existing = Path.Combine(_dir, "taken.csv");
            File.WriteAllText(existing, "x");
            var approvals = new FakeApprovals(true);
            var noFlag = await Call(tools, "csv.save_edits_as", """{"path":"taken.csv"}""", approvals);
            Assert.True(noFlag.IsError);
            Assert.Contains("overwrite", noFlag.Text);
            Assert.Empty(approvals.Calls);

            var withFlag = await Call(tools, "csv.save_edits_as", """{"path":"taken.csv","overwrite":true}""", approvals);
            Assert.False(withFlag.IsError);
            Assert.Contains(Assert.Single(approvals.Calls).Lines, l => l.Contains("REPLACED"));
        }

        [Fact]
        public async Task Save_RejectsOddExtensionsMissingFoldersAndNoEdits()
        {
            var host = HostWithEdits();
            var tools = Tools(host);
            Assert.True((await Call(tools, "csv.save_edits_as", """{"path":"run.exe"}""")).IsError);
            Assert.True((await Call(tools, "csv.save_edits_as", """{"path":"nofolder\\out.csv"}""")).IsError);

            var clean = new FakeHost { Directory = _dir };
            var none = await Call(Tools(clean), "csv.save_edits_as", """{"path":"out.csv"}""");
            Assert.True(none.IsError);
            Assert.Contains("no edits", none.Text);
        }

        [Fact]
        public void SavePolicy_RelativeNeedsFolder_AndCanonicalizesSourceComparison()
        {
            Assert.Throws<AgentToolException>(() => AgentSavePolicy.Resolve("out.csv", null, Array.Empty<string>(), false));
            string source = Path.Combine(_dir, "data.csv");
            var same = Assert.Throws<AgentToolException>(() => AgentSavePolicy.Resolve(Path.Combine(_dir, "sub", "..", "data.csv"), _dir, new[] { source }, true));
            Assert.Contains("source file", same.Message);
            Assert.Equal(Path.Combine(_dir, "out.xlsx"), AgentSavePolicy.Resolve("out.xlsx", _dir, new[] { source }, false));
        }

        // ------------------------------------------------------------------ 품질

        private static QualityReport SampleReport() => new()
        {
            RowsScanned = 1000,
            ScannedFully = true,
            ElapsedSeconds = 0.5,
            Columns = Array.Empty<QualityColumnProfile>(),
            Findings = new[]
            {
                new QualityFinding
                {
                    Kind = QualityCheckKind.DisguisedMissing, Dimension = QualityDimension.Completeness, Severity = QualitySeverity.Warning,
                    Column = 1, ColumnName = "age", ViolationCount = 12, EvaluatedRows = 1000,
                    Examples = new[] { new QualityExample(7, "SECRET_999") },
                    Breakdown = new[] { new ValueCount("SECRET_999", 12) },
                },
                new QualityFinding
                {
                    Kind = QualityCheckKind.ConstantColumn, Dimension = QualityDimension.Plausibility, Severity = QualitySeverity.Info,
                    Column = 3, ColumnName = "group", ViolationCount = 1000, EvaluatedRows = 1000, Label = "SECRET_CONST",
                },
                new QualityFinding
                {
                    Kind = QualityCheckKind.EmptyColumn, Dimension = QualityDimension.Completeness, Severity = QualitySeverity.Critical,
                    Column = 4, ColumnName = "score", ViolationCount = 1000, EvaluatedRows = 1000,
                },
            },
        };

        [Fact]
        public async Task QualityScan_SummaryOnly_WithholdsValuesButKeepsRowNumbers()
        {
            var host = new FakeHost { Report = SampleReport() };
            var r = await Call(Tools(host), "csv.quality_scan", "{}");
            Assert.False(r.IsError);
            Assert.DoesNotContain("SECRET", r.Text);
            var findings = Payload(r)["findings"]!.AsArray();
            Assert.Equal(3, findings.Count);
            Assert.Equal("empty_column", (string?)findings[0]!["check"]);          // 심각도 내림차순
            Assert.Equal("critical", (string?)findings[0]!["severity"]);
            var disguised = findings.First(f => (string?)f!["check"] == "disguised_missing")!;
            Assert.Equal(7, disguised["example_rows"]![0]!.GetValue<int>());
            Assert.Equal(1, disguised["breakdown_values_hidden"]!.GetValue<int>());

            var allowed = await Call(Tools(host, AgentDataPolicy.RowsAllowed), "csv.quality_scan", """{"min_severity":"warning"}""");
            Assert.Contains("SECRET_999", allowed.Text);
            Assert.Equal(2, Payload(allowed)["findings"]!.AsArray().Count);
        }

        [Fact]
        public async Task QualityScan_MaxFindings_TruncatesHonestly()
        {
            var host = new FakeHost { Report = SampleReport() };
            var r = await Call(Tools(host), "csv.quality_scan", """{"max_findings":1}""");
            var json = Payload(r);
            Assert.Single(json["findings"]!.AsArray());
            Assert.Contains("Listed 1 of 3", json["truncated"]!.ToString());
        }

        // ------------------------------------------------------------------ 기록

        [Fact]
        public async Task ToolLog_RecordsToolArgsOutcome_WithoutCellValues()
        {
            string logPath = Path.Combine(_dir, "agent", "tool-log.jsonl");
            var host = new FakeHost();
            var tools = Tools(host, log: new AgentToolLog(logPath));
            await Call(tools, "csv.edit_cells", """{"edits":[{"row":2,"column":"city","value":"Daegu-SECRET"}]}""");
            await Call(tools, "csv.set_filter", """{"expression":"age > 30"}""");
            await Call(tools, "csv.nope", "{}");

            var lines = File.ReadAllLines(logPath);
            Assert.Equal(3, lines.Length);
            Assert.DoesNotContain("Daegu-SECRET", string.Join("\n", lines));
            var edit = JsonNode.Parse(lines[0])!;
            Assert.Equal("csv.edit_cells", (string?)edit["tool"]);
            Assert.True(edit["ok"]!.GetValue<bool>());
            Assert.Equal(1, edit["args"]!["edits"]!.GetValue<int>());
            Assert.Equal("city", (string?)edit["args"]!["columns"]![0]);
            Assert.True(edit["ms"]!.GetValue<long>() >= 0);
            Assert.Equal("age > 30", (string?)JsonNode.Parse(lines[1])!["args"]!["expression"]);
            Assert.False(JsonNode.Parse(lines[2])!["ok"]!.GetValue<bool>());
        }

        [Fact]
        public void ToolLog_FailureNeverThrows()
        {
            string blocker = Path.Combine(_dir, "afile");
            File.WriteAllText(blocker, "x");
            var log = new AgentToolLog(Path.Combine(blocker, "sub", "log.jsonl")); // 폴더 자리에 파일이 있다
            log.Append("csv.info", null, true, 1, null);
        }

        // ------------------------------------------------------------------ 컬럼 이름

        [Fact]
        public void ColumnNames_ExactThenCaseInsensitive_AndAmbiguityIsAnError()
        {
            var headers = new[] { "Age", "age2", "AGE" };
            Assert.Equal(0, ColumnNames.Resolve(headers, "Age"));
            Assert.Equal(1, ColumnNames.Resolve(headers, "AGE2"));
            Assert.Throws<AgentToolException>(() => ColumnNames.Resolve(headers, "nosuch"));
            var amb = Assert.Throws<AgentToolException>(() => ColumnNames.Resolve(new[] { "ab", "AB" }, "Ab"));
            Assert.Contains("ambiguous", amb.Message);
        }

        // ------------------------------------------------------------------ 통계 계산(실제 문서)

        private async Task<VirtualCsvDocument> OpenCsv(string name, string csv)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            var doc = VirtualCsvDocument.Open(path);
            await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            return doc;
        }

        /// <summary>실제 문서의 현재 뷰를 쓰는 호스트: 필터를 문서에 적용하고 분석 입력으로 뷰 스냅샷을 준다.</summary>
        private sealed class DocHost(VirtualCsvDocument doc, ColumnValueType[] types) : HostStub
        {
            public readonly List<AgentAnalysisOutcome> Shown = new();

            public override AgentDocumentInfo? GetInfo() => new(
                "data.csv", null, Array.Empty<string>(), "UTF-8", ",", doc.FileLength, true, 100, false,
                doc.DataRowsAvailable, doc.DisplayRowCount, false,
                doc.Header.Select((h, i) => new AgentColumn(i, h, types[i])).ToList(),
                Array.Empty<AgentFilterInfo>(), false, Array.Empty<AgentSortInfo>(), Array.Empty<string>(),
                AgentEditState.None, new AgentCursor(null, null), null, Array.Empty<string>());

            public override async Task<AgentViewChange> SetFilterAsync(string expression, bool replace, CancellationToken c)
            {
                var pred = AdvancedFilterExpression.Compile(expression, doc.Header).Predicate;
                if (replace) await doc.ApplyFilterAsync(pred, null, c); else await doc.FilterWithinViewAsync(pred, null, c);
                return new AgentViewChange(doc.DisplayRowCount, doc.DataRowsAvailable);
            }

            public override Task<T> RunOnViewRowsAsync<T>(string description, Func<AgentViewData, CancellationToken, T> work, CancellationToken c)
            {
                var data = new AgentViewData(doc.SnapshotViewRows(), doc.Header, types);
                return Task.Run(() => work(data, c), c);
            }

            public override void ShowAnalysisWindow(AgentAnalysisOutcome outcome) => Shown.Add(outcome);
        }

        private static string Num(double v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

        private static string LinearCsv()
        {
            // y = 2 + 3 x + 5·[g = B] + 작은 결정적 잡음
            var sb = new StringBuilder("x,g,y\n");
            for (int i = 1; i <= 60; i++)
            {
                string g = i % 2 == 0 ? "B" : "A";
                double noise = ((i * 37) % 11 - 5) * 0.01;
                sb.Append(i).Append(',').Append(g).Append(',').Append(Num(2 + 3 * i + (g == "B" ? 5 : 0) + noise)).Append('\n');
            }
            return sb.ToString();
        }

        private static readonly ColumnValueType[] XgyTypes = { ColumnValueType.Integer, ColumnValueType.Categorical, ColumnValueType.Float };

        [Fact]
        public async Task RunAnalysis_Glm_ReturnsCoefficientsAnovaFit_OnTheFilteredView()
        {
            using var doc = await OpenCsv("lin.csv", LinearCsv());
            var host = new DocHost(doc, XgyTypes);
            var tools = Tools(host);

            var filter = await Call(tools, "csv.set_filter", """{"expression":"x > 20"}""");
            Assert.Equal(40, Payload(filter)["view_rows"]!.GetValue<int>());

            var r = await Call(tools, "csv.run_analysis", """{"kind":"glm","formula":"y ~ x + C(g)"}""");
            Assert.False(r.IsError, r.Text);
            var json = Payload(r);
            Assert.Equal("glm", (string?)json["kind"]);
            Assert.Equal(40, json["rows_in_view"]!.GetValue<int>());
            Assert.Equal(40, json["n_used"]!.GetValue<int>());
            Assert.Equal(0, json["n_dropped"]!.GetValue<int>());

            var coef = json["coefficients"]!.AsArray();
            double Get(string term, string field) => coef.First(c => (string?)c!["term"] == term)![field]!.GetValue<double>();
            Assert.Equal(3.0, Get("x", "estimate"), 2);
            Assert.Equal(5.0, Get("g[T.B]", "estimate"), 1);
            Assert.True(Get("x", "p") < 1e-6);
            Assert.True(Get("x", "ci_low") < Get("x", "estimate") && Get("x", "ci_high") > Get("x", "estimate"));
            Assert.True(json["fit"]!["r2"]!.GetValue<double>() > 0.999);
            Assert.Equal(2, json["anova_type2"]!.AsArray().Count(a => (string?)a!["term"] is "x" or "g"));
            Assert.Single(host.Shown);                       // 결과 창을 사용자에게도 연다
            Assert.True(Get("x", "estimate") is > 2.9 and < 3.1);
            Assert.NotNull(host.Shown[0].Linear);

            var quiet = await Call(tools, "csv.run_analysis", """{"kind":"glm","formula":"y ~ x","show_window":false}""");
            Assert.False(quiet.IsError);
            Assert.Single(host.Shown);
        }

        [Fact]
        public async Task RunAnalysis_ReportsDroppedRows_ForMissingValues()
        {
            var sb = new StringBuilder("x,g,y\n");
            for (int i = 1; i <= 30; i++)
                sb.Append(i).Append(',').Append(i % 2 == 0 ? "B" : "A").Append(',').Append(i % 10 == 0 ? "" : Num(1 + 2 * i + (i % 3) * 0.1)).Append('\n');
            using var doc = await OpenCsv("miss.csv", sb.ToString());
            var r = await Call(Tools(new DocHost(doc, XgyTypes)), "csv.run_analysis", """{"kind":"glm","formula":"y ~ x"}""");
            var json = Payload(r);
            Assert.Equal(30, json["rows_in_view"]!.GetValue<int>());
            Assert.Equal(27, json["n_used"]!.GetValue<int>());
            Assert.Equal(3, json["n_dropped"]!.GetValue<int>());
        }

        [Fact]
        public async Task RunAnalysis_UserInputProblems_AreErrorsWithTheReason()
        {
            using var doc = await OpenCsv("lin2.csv", LinearCsv());
            var tools = Tools(new DocHost(doc, XgyTypes));
            var missingColumn = await Call(tools, "csv.run_analysis", """{"kind":"glm","formula":"y ~ nosuch"}""");
            Assert.True(missingColumn.IsError);
            Assert.Contains("nosuch", missingColumn.Text);
            var syntax = await Call(tools, "csv.run_analysis", """{"kind":"glm","formula":"y ~ ~ x"}""");
            Assert.True(syntax.IsError);
            Assert.True((await Call(tools, "csv.run_analysis", """{"kind":"glm"}""")).IsError);
            Assert.True((await Call(tools, "csv.run_analysis", """{"kind":"ancova","dependent":"y"}""")).IsError);
            Assert.True((await Call(tools, "csv.run_analysis", """{"kind":"bogus"}""")).IsError);
            var nonNumericResponse = await Call(tools, "csv.run_analysis", """{"kind":"glm","formula":"g ~ x"}""");
            Assert.True(nonNumericResponse.IsError);
        }

        [Fact]
        public async Task RunAnalysis_Describe_GroupBy_CountsAndMeans()
        {
            using var doc = await OpenCsv("desc.csv", LinearCsv());
            var r = await Call(Tools(new DocHost(doc, XgyTypes)), "csv.run_analysis", """{"kind":"describe","columns":["x"],"group_by":"g"}""");
            Assert.False(r.IsError, r.Text);
            var groups = Payload(r)["groups"]!.AsArray();
            Assert.Equal(2, groups.Count);
            var a = groups.First(g => (string?)g!["group"] == "A")!;
            Assert.Equal(30, a["rows"]!.GetValue<int>());
            var x = a["columns"]![0]!;
            Assert.Equal(30, x["n"]!.GetValue<int>());
            Assert.Equal(30.0, x["mean"]!.GetValue<double>(), 6);   // 1,3,…,59 의 평균
            Assert.Equal(1, x["min"]!.GetValue<double>());
            Assert.Equal(59, x["max"]!.GetValue<double>());
        }

        [Fact]
        public async Task RunAnalysis_Ancova_AdjustedMeansAndPairwise()
        {
            var sb = new StringBuilder("g,cov,y\n");
            string[] levels = { "A", "B", "C" };
            double[] shift = { 0, 1.5, 3 };
            for (int i = 0; i < 90; i++)
            {
                int k = i % 3;
                double cov = 10 + (i * 7 % 13) * 0.5;
                double noise = ((i * 29) % 7 - 3) * 0.05;
                sb.Append(levels[k]).Append(',').Append(Num(cov)).Append(',').Append(Num(1 + 2 * cov + shift[k] + noise)).Append('\n');
            }
            using var doc = await OpenCsv("anc.csv", sb.ToString());
            var types = new[] { ColumnValueType.Categorical, ColumnValueType.Float, ColumnValueType.Float };
            var r = await Call(Tools(new DocHost(doc, types)), "csv.run_analysis",
                """{"kind":"ancova","dependent":"y","factors":["g"],"covariates":["cov"]}""");
            Assert.False(r.IsError, r.Text);
            var json = Payload(r);
            var factor = json["adjusted_means"]![0]!;
            Assert.Equal("g", (string?)factor["factor"]);
            Assert.Equal(3, factor["levels"]!.AsArray().Count);
            Assert.Equal(3, factor["pairwise"]!["comparisons"]!.GetValue<int>());
            var ba = factor["pairwise"]!["rows"]!.AsArray().First(p => (string?)p!["a"] == "A" && (string?)p["b"] == "B")!;
            Assert.Equal(1.5, ba["b_minus_a"]!.GetValue<double>(), 1);
            Assert.NotNull(json["slopes_homogeneity"]);
            Assert.NotNull(json["covariate_means"]!["cov"]);
            Assert.True(json["fit"]!["r2"]!.GetValue<double>() > 0.99);
        }

        [Fact]
        public async Task RunAnalysis_Logistic_OddsRatioAndClassification()
        {
            var sb = new StringBuilder("x,out\n");
            uint seed = 12345;
            double Next() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (double)(1u << 24); }
            for (int i = 0; i < 300; i++)
            {
                double x = Next() + Next() + Next() + Next() - 2;
                double p = 1 / (1 + Math.Exp(-(-0.3 + 1.5 * x)));
                sb.Append(Num(x)).Append(',').Append(Next() < p ? 1 : 0).Append('\n');
            }
            using var doc = await OpenCsv("logit.csv", sb.ToString());
            var types = new[] { ColumnValueType.Float, ColumnValueType.Integer };
            var r = await Call(Tools(new DocHost(doc, types)), "csv.run_analysis", """{"kind":"logistic","formula":"out ~ x"}""");
            Assert.False(r.IsError, r.Text);
            var json = Payload(r);
            Assert.True(json["fit"]!["converged"]!.GetValue<bool>());
            var x1 = json["coefficients"]!.AsArray().First(c => (string?)c!["term"] == "x")!;
            Assert.True(x1["odds_ratio"]!.GetValue<double>() > 2);
            Assert.True(x1["or_ci_low"]!.GetValue<double>() < x1["odds_ratio"]!.GetValue<double>());
            Assert.True(x1["p"]!.GetValue<double>() < 0.001);
            Assert.True(json["classification"]!["auc"]!.GetValue<double>() > 0.7);
            Assert.Equal("binomial", (string?)json["family"]);
            Assert.Equal("1", (string?)json["response_event_level"]);
        }

        [Fact]
        public async Task RunAnalysis_Glzm_PoissonLog_RejectsInvalidLinkAndFits()
        {
            var sb = new StringBuilder("x,n\n");
            for (int i = 0; i <= 40; i++)
            {
                double x = i * 0.05;
                sb.Append(Num(x)).Append(',').Append(Num(Math.Exp(2 + 0.3 * x) * (1 + ((i * 13) % 5 - 2) * 0.01))).Append('\n');
            }
            using var doc = await OpenCsv("pois.csv", sb.ToString());
            var tools = Tools(new DocHost(doc, new[] { ColumnValueType.Float, ColumnValueType.Integer }));
            var r = await Call(tools, "csv.run_analysis", """{"kind":"glzm","formula":"n ~ x","family":"poisson"}""");
            Assert.False(r.IsError, r.Text);
            var json = Payload(r);
            Assert.Equal("poisson", (string?)json["family"]);
            Assert.Equal("log", (string?)json["link"]);
            Assert.True(json["fit"]!["converged"]!.GetValue<bool>());
            var x1 = json["coefficients"]!.AsArray().First(c => (string?)c!["term"] == "x")!;
            Assert.Equal(0.3, x1["estimate"]!.GetValue<double>(), 1);

            var bad = await Call(tools, "csv.run_analysis", """{"kind":"glzm","formula":"n ~ x","family":"poisson","link":"logit"}""");
            Assert.True(bad.IsError);
            Assert.Contains("not valid", bad.Text);
        }

        // ------------------------------------------------------------------ 컬럼 통계 계산기

        [Fact]
        public async Task ColumnStatsCalculator_OnRealView_MatchesHandComputation()
        {
            using var doc = await OpenCsv("stats.csv", "a,b\n1,x\n2,y\n,x\n4,x\nzz,y\n");
            var data = new AgentViewData(doc.SnapshotViewRows(), doc.Header, new[] { ColumnValueType.Integer, ColumnValueType.Categorical });
            var stats = ColumnStatsCalculator.Compute(data, new[] { 0, 1 }, topN: 1, CancellationToken.None);

            var a = stats[0];
            Assert.Equal(5, a.Rows);
            Assert.Equal(1, a.Missing);
            Assert.Equal(1, a.NonNumeric);                  // "zz"
            Assert.Equal(3, a.Numeric!.Count);
            Assert.Equal(7.0 / 3, a.Numeric.Mean, 9);
            Assert.Equal(2, a.Numeric.Median);

            var b = stats[1];
            Assert.Equal(2, b.Distinct);
            Assert.Equal(("x", 3L), b.TopValues![0]);
        }

        [Fact]
        public async Task ColumnStatsCalculator_Cancellation_Stops()
        {
            var sb = new StringBuilder("a\n");
            for (int i = 0; i < 100_000; i++) sb.Append(i).Append('\n');
            using var doc = await OpenCsv("big.csv", sb.ToString());
            var data = new AgentViewData(doc.SnapshotViewRows(), doc.Header, new[] { ColumnValueType.Integer });
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => ColumnStatsCalculator.Compute(data, new[] { 0 }, 0, cts.Token));
        }

        [Fact]
        public async Task Cancellation_ReportsAsErrorResult()
        {
            var host = new FakeHost();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var tools = Tools(host);
            var r = await tools.ExecuteAsync(new HostToolCall("h", "t", "csv.info", JsonDocument.Parse("{}").RootElement.Clone()), new FakeApprovals(true), cts.Token);
            Assert.True(r.IsError);
            Assert.Contains("Cancelled", r.Text);
        }

        [Fact]
        public async Task ParallelCalls_AreSerialized()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            var calls = Enumerable.Range(0, 8).Select(i => Call(tools, "csv.set_filter", $$"""{"expression":"age > {{20 + i}}"}""")).ToArray();
            var results = await Task.WhenAll(calls);
            Assert.All(results, r => Assert.False(r.IsError, r.Text));
            Assert.Equal(8, host.Calls.Count(c => c.StartsWith("filter:", StringComparison.Ordinal)));
        }
    }
}
