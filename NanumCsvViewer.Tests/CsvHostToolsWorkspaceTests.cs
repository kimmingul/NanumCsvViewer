using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    // ws.* 에이전트 도구: 가짜 호스트(진짜 DataWorkspace) 위의 정책·승인·정직한 숫자, 그리고 진짜 Form1에서 두 CSV를 열어 조인 뷰를 만드는 흐름.
    [Collection("SavedViewStore")]
    public class CsvHostToolsWorkspaceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_wstools_" + Guid.NewGuid().ToString("N"));

        public CsvHostToolsWorkspaceTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string MakeCsv(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private const string Customers = "id,name,city\n001,kim,Seoul\n002,lee,Busan\n003,park,Seoul\n";
        private const string Orders = "order_id,cust_id,amount\n1,001,100\n2,001,50\n3,002,70\n4,009,10\n";

        // ---------------------------------------------------------------- 가짜 호스트

        private sealed class Approvals(bool answer) : IAgentApprovals
        {
            public readonly List<(string Target, IReadOnlyList<string> Lines, ApprovalKind Kind)> Calls = new();
            /// <summary>이번 턴을 시작한 사용자 메시지(에이전트 뷰의 출처에 적힌다).</summary>
            public string? Request { get; set; }
            public string? CurrentUserRequest => Request;
            public Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation, ApprovalKind kind = ApprovalKind.RowSharing)
            {
                Calls.Add((target, lines, kind));
                return Task.FromResult(answer);
            }
        }

        private class PlainHost : ICsvAgentHost
        {
            public virtual AgentDocumentInfo? GetInfo() => null;
            public Task<AgentRowsPage> GetRowsAsync(long firstViewRow, int count, IReadOnlyList<int> columns, CancellationToken c) => throw new NotSupportedException();
            public Task<T> RunOnViewRowsAsync<T>(string description, Func<AgentViewData, CancellationToken, T> work, CancellationToken c) => throw new NotSupportedException();
            public void ShowAnalysisWindow(AgentAnalysisOutcome outcome) => throw new NotSupportedException();
            public Task<AgentViewChange> SetFilterAsync(string expression, bool replace, CancellationToken c) => throw new NotSupportedException();
            public Task<AgentViewChange> ClearFilterAsync(CancellationToken c) => throw new NotSupportedException();
            public Task<AgentViewChange> SortAsync(IReadOnlyList<SortKey> keys, CancellationToken c) => throw new NotSupportedException();
            public Task<AgentGotoResult> GotoAsync(long? sourceRow, int? column, CancellationToken c) => throw new NotSupportedException();
            public Task<QualityReport> RunQualityScanAsync(CancellationToken c) => throw new NotSupportedException();
            public Task<AgentRegexScan> RegexCountAsync(Regex regex, IReadOnlyList<int> columns, int maxSamples, CancellationToken c) => throw new NotSupportedException();
            public Task<AgentRegexPlan> PlanRegexReplaceAsync(Regex regex, string replacement, IReadOnlyList<int> columns, int maxChanges, CancellationToken c) => throw new NotSupportedException();
            public IReadOnlyList<AgentCellState> GetCellStates(IReadOnlyList<(long SourceRow, int Column)> cells) => throw new NotSupportedException();
            public AgentEditResult ApplyEdits(IReadOnlyList<AgentCellEdit> edits, string description) => throw new NotSupportedException();
            public AgentUndoResult UndoAgentEdit() => throw new NotSupportedException();
            public Task<AgentSaveResult> SaveEditsAsAsync(string fullPath, CancellationToken c) => throw new NotSupportedException();
            public Task<AgentRowNumbers> GetViewRowNumbersAsync(int cap, CancellationToken c) => throw new NotSupportedException();
            public AgentStructureResult InsertRows(long rowNumber, int count, string description) => throw new NotSupportedException();
            public AgentStructureResult DeleteRows(IReadOnlyList<long> rowNumbers, string description) => throw new NotSupportedException();
            public AgentColumnChange AddColumn(string name, string? fill, string description) => throw new NotSupportedException();
            public AgentColumnChange InsertColumn(string name, int position, string? fill, string description) => throw new NotSupportedException();
            public AgentColumnChange MoveColumn(int from, int to, string description) => throw new NotSupportedException();
            public AgentColumnChange DeleteColumn(int column, string description) => throw new NotSupportedException();
            public IReadOnlyDictionary<string, string> ConditionalFormatProblems() => throw new NotSupportedException();
            public IReadOnlyList<ConditionalFormatRule> ListConditionalFormats() => throw new NotSupportedException();
            public ConditionalFormatRule AddConditionalFormat(ConditionalFormatRule draft) => throw new NotSupportedException();
            public bool RemoveConditionalFormat(string id) => throw new NotSupportedException();
            public int ClearConditionalFormats() => throw new NotSupportedException();
            public ConditionalFormatUndoResult? UndoConditionalFormat() => throw new NotSupportedException();
            public Task<ConditionalFormatCount> CountConditionalFormatAsync(string? id, ConditionalFormatRule? draft, CancellationToken c) => throw new NotSupportedException();
            public ViewerShowResult ShowMarkdown(string fullPath) => throw new NotSupportedException();
            public ViewerShowResult ShowImage(string fullPath) => throw new NotSupportedException();
            public bool PostInlineImage(string fullPath, string? caption) => throw new NotSupportedException();
        }

        private sealed class WsHost : PlainHost, IWorkspaceAgentHost
        {
            private readonly string _dir;
            private string _active;
            public DataWorkspace? Workspace { get; set; } = new DataWorkspace();
            public string? WorkspaceUnavailableReason { get; set; }
            public List<string> OpenPaths { get; } = new();
            public List<string> OpenedRelations { get; } = new();
            public List<string> QueryTabs { get; } = new();
            public List<string> Tabs { get; } = new();

            public WsHost(string dir, string activeTab) { _dir = dir; _active = activeTab; Tabs.Add(activeTab); }

            public override AgentDocumentInfo? GetInfo() => new AgentDocumentInfo("customers.csv", null, Array.Empty<string>(), "UTF-8", ",", 10, true, 100, false, 3, 3, false,
                new[] { new AgentColumn(0, "id", ColumnValueType.Identifier) }, Array.Empty<AgentFilterInfo>(), false, Array.Empty<AgentSortInfo>(), Array.Empty<string>(),
                new AgentEditState(0, 0, 0, 0, false, false, null, false, false), new AgentCursor(null, null), _dir, OpenPaths.ToArray());

            public Task EnsureTabsRegisteredAsync(CancellationToken ct) => Task.CompletedTask;
            public IReadOnlyList<AgentTabInfo> GetTabs() => Tabs.Select(t => new AgentTabInfo(t, "file", t == _active, false, false, null)).ToList();
            public IReadOnlyList<string> OpenSourcePaths() => OpenPaths;
            public async Task<IReadOnlyList<WorkspaceSource>> AddSourcesAsync(IReadOnlyList<string> paths, CancellationToken ct)
            {
                var list = new List<WorkspaceSource>();
                foreach (string p in paths) list.Add(await Workspace!.AddCsvAsync(p, null, null, ct));
                return list;
            }

            public async Task<AgentTabInfo?> OpenRelationAsync(IWorkspaceRelation relation, CancellationToken ct)
            {
                OpenedRelations.Add(relation.DisplayName);
                if (relation is WorkspaceView v) await Workspace!.MaterializeViewAsync(v, null, ct);
                Tabs.Add(relation.DisplayName);
                _active = relation.DisplayName;
                return new AgentTabInfo(relation.DisplayName, relation is WorkspaceView ? "view" : "file", true, relation is WorkspaceView, false, relation.DisplayName);
            }

            public Task<AgentTabInfo> OpenQueryResultAsync(string sql, string title, CancellationToken ct)
            {
                QueryTabs.Add(sql);
                Tabs.Add(title);
                _active = title;
                return Task.FromResult(new AgentTabInfo(title, "result", true, true, false, null));
            }

            public async Task<string> SaveRelationAsAsync(IWorkspaceRelation relation, string path, CancellationToken ct)
            {
                await Workspace!.RunToCsvAsync("SELECT * FROM " + relation.SqlReference, path, null, ct);
                return path;
            }

            public Task<AgentTabInfo?> SwitchTabAsync(string name, CancellationToken ct)
            {
                if (!Tabs.Contains(name)) return Task.FromResult<AgentTabInfo?>(null);
                _active = name;
                return Task.FromResult<AgentTabInfo?>(new AgentTabInfo(name, "file", true, false, false, null));
            }

            public string WorkspaceNotes { get; set; } = "";
            public void SetWorkspaceNotes(string notes) => WorkspaceNotes = notes;
        }

        private static readonly AgentHostOptions Summary = new(Language: "en", DataPolicy: AgentDataPolicy.SummaryOnly, MaxRowsPerRequest: 50, AllowLocalPython: false);

        private static AgentHostOptions Policy(AgentDataPolicy p) => new(Language: "en", DataPolicy: p, MaxRowsPerRequest: 50, AllowLocalPython: false);

        private (WsHost Host, CsvHostTools Tools, string Customers, string Orders) Setup(AgentHostOptions? options = null)
        {
            string c = MakeCsv("customers.csv", Customers), o = MakeCsv("orders.csv", Orders);
            var host = new WsHost(_dir, "customers.csv");
            host.OpenPaths.Add(c);
            host.Workspace!.AddCsv(c);
            host.Workspace.AddCsv(o);
            var opts = options ?? Summary;
            return (host, new CsvHostTools(host, () => opts), c, o);
        }

        private static HostToolResult Run(CsvHostTools tools, string tool, string args, IAgentApprovals? approvals = null)
        {
            var call = new HostToolCall("host_1", "call_1", tool, JsonDocument.Parse(args).RootElement.Clone());
            return tools.ExecuteAsync(call, approvals ?? new Approvals(true), CancellationToken.None).GetAwaiter().GetResult();
        }

        private static JsonObject Json(HostToolResult r)
        {
            Assert.False(r.IsError, r.Text);
            int nl = r.Text.IndexOf('\n');
            return JsonNode.Parse(r.Text[(nl + 1)..])!.AsObject();
        }

        // ---------------------------------------------------------------- 목록·설명

        [Fact]
        public void List_tables_shows_sources_views_tabs_and_row_counts()
        {
            var (host, tools, _, _) = Setup();
            host.Workspace!.CreateView("big_orders", "SELECT * FROM orders WHERE CAST(amount AS INTEGER) > 60");
            var j = Json(Run(tools, "ws.list_tables", "{}"));
            var sources = j["sources"]!.AsArray();
            Assert.Equal(new[] { "customers", "orders" }, sources.Select(s => (string)s!["name"]!).ToArray());
            var orders = sources[1]!["tables"]![0]!;
            Assert.Equal(4, (long)orders["rows"]!);
            Assert.Contains(orders["columns"]!.AsArray(), c => (string)c!["name"]! == "amount");
            var view = j["views"]![0]!;
            Assert.Equal("big_orders", (string)view["name"]!);
            Assert.True((bool)view["stale"]! || !(bool)view["computed"]!);
            Assert.Equal("customers.csv", (string)j["active_tab"]!);
        }

        [Fact]
        public void Describe_gives_distinct_counts_and_key_candidates_without_values()
        {
            var (_, tools, _, _) = Setup();
            string text = Run(tools, "ws.describe", """{"name":"customers"}""").Text;
            var j = Json(Run(tools, "ws.describe", """{"name":"customers"}"""));
            Assert.Equal(3, (long)j["rows"]!);
            var id = j["profile"]!.AsArray().Single(p => (string)p!["column"]! == "id")!;
            Assert.Equal(3, (long)id["distinct"]!);
            Assert.Contains(j["key_candidates"]!.AsArray(), k => (string)k!["column"]! == "id" && (string)k["uniqueness"]! == "unique");
            Assert.DoesNotContain(j["key_candidates"]!.AsArray(), k => (string)k!["column"]! == "city");
            Assert.DoesNotContain("kim", text);
            Assert.DoesNotContain("Seoul", text);
        }

        [Fact]
        public void Describe_unknown_name_lists_available_relations()
        {
            var (_, tools, _, _) = Setup();
            var r = Run(tools, "ws.describe", """{"name":"nope"}""");
            Assert.True(r.IsError);
            Assert.Contains("customers", r.Text);
            Assert.Contains("orders", r.Text);
        }

        // ---------------------------------------------------------------- 조인 진단

        [Fact]
        public void Check_join_reports_honest_numbers_and_warnings()
        {
            var (_, tools, _, _) = Setup();
            var j = Json(Run(tools, "ws.check_join", """{"left":"customers","right":"orders","keys":[{"left":"id","right":"cust_id"}]}"""));
            Assert.Equal(3, (long)j["left_rows"]!);
            Assert.Equal(4, (long)j["right_rows"]!);
            Assert.Equal(2, (long)j["matched_keys"]!);
            Assert.Equal(1, (long)j["left_only_keys"]!);   // 003
            Assert.Equal(1, (long)j["right_only_keys"]!);  // 009
            Assert.Equal(3, (long)j["expected_rows"]!);    // 001×2 + 002×1
            Assert.Equal("one-to-many", (string)j["cardinality"]!);
            Assert.Contains("JOIN", (string)j["join_sql"]!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Check_join_with_no_matching_keys_warns()
        {
            var (_, tools, _, _) = Setup();
            var j = Json(Run(tools, "ws.check_join", """{"left":"customers","right":"orders","keys":[{"left":"name","right":"cust_id"}]}"""));
            Assert.Equal(0, (long)j["matched_keys"]!);
            Assert.Contains(j["warnings"]!.AsArray(), w => ((string)w!).Contains("NO key matches"));
        }

        [Fact]
        public void Check_join_rejects_unknown_columns()
        {
            var (_, tools, _, _) = Setup();
            var r = Run(tools, "ws.check_join", """{"left":"customers","right":"orders","keys":[{"left":"idd","right":"cust_id"}]}""");
            Assert.True(r.IsError);
            Assert.Contains("no column", r.Text);
        }

        // ---------------------------------------------------------------- 뷰 만들기

        [Fact]
        public void Create_view_asks_a_DataEdit_approval_creates_the_view_and_opens_it()
        {
            var (host, tools, _, _) = Setup();
            var ap = new Approvals(true);
            var j = Json(Run(tools, "ws.create_view",
                """{"name":"cust_orders","sql":"SELECT c.name, o.amount FROM customers c JOIN orders o ON c.id = o.cust_id","open":true}""", ap));
            Assert.Single(ap.Calls);
            Assert.Equal(ApprovalKind.DataEdit, ap.Calls[0].Kind);
            Assert.Contains(ap.Calls[0].Lines, l => l.Contains("JOIN"));
            Assert.NotNull(host.Workspace!.FindRelation("cust_orders"));
            Assert.Equal("cust_orders", (string)j["opened_tab"]!);
            Assert.Equal(new[] { "cust_orders" }, host.OpenedRelations);
            Assert.Equal(3, (long)j["rows"]!);
        }

        [Fact]
        public void Create_view_declined_creates_nothing()
        {
            var (host, tools, _, _) = Setup();
            var r = Run(tools, "ws.create_view", """{"name":"v1","sql":"SELECT * FROM orders"}""", new Approvals(false));
            Assert.True(r.IsError);
            Assert.Empty(host.Workspace!.Views);
        }

        [Fact]
        public void Create_view_refuses_non_select_and_duplicates_before_asking()
        {
            var (host, tools, _, _) = Setup();
            var ap = new Approvals(true);
            var bad = Run(tools, "ws.create_view", """{"name":"v1","sql":"DROP TABLE orders"}""", ap);
            Assert.True(bad.IsError);
            Assert.Empty(ap.Calls);
            Assert.NotNull(host.Workspace!.FindRelation("orders"));

            Assert.False(Run(tools, "ws.create_view", """{"name":"v1","sql":"SELECT * FROM orders"}""", ap).IsError);
            var dup = Run(tools, "ws.create_view", """{"name":"v1","sql":"SELECT * FROM customers"}""", ap);
            Assert.True(dup.IsError);
            Assert.Contains("already exists", dup.Text);
            var replaced = Run(tools, "ws.create_view", """{"name":"v1","sql":"SELECT * FROM customers","replace":true}""", ap);
            Assert.False(replaced.IsError, replaced.Text);
            Assert.Contains("customers", host.Workspace.Views.Single().Sql);
        }

        [Fact]
        public void Create_view_rejects_unusable_names()
        {
            var (_, tools, _, _) = Setup();
            var r = Run(tools, "ws.create_view", """{"name":"a b;c","sql":"SELECT 1"}""");
            Assert.True(r.IsError);
            Assert.Contains("not a valid view name", r.Text);
        }

        [Fact]
        public void Append_stacks_tables_and_group_aggregates()
        {
            var (host, tools, _, _) = Setup();
            MakeCsv("customers2.csv", "id,name,city\n004,choi,Daegu\n");
            Run(tools, "ws.add_source", """{"paths":["customers2.csv"]}""");
            var a = Json(Run(tools, "ws.append", """{"name":"all_customers","tables":["customers","customers2"],"source_column":"src","open":true}"""));
            Assert.Equal(4, (long)a["rows"]!);
            Assert.Contains(a["columns"]!.AsArray(), c => (string)c!["name"]! == "src");

            var g = Json(Run(tools, "ws.group",
                """{"name":"by_city","table":"all_customers","group_by":["city"],"aggregates":[{"function":"count","alias":"n"}],"open":true}"""));
            Assert.Equal(3, (long)g["rows"]!); // Seoul, Busan, Daegu
            Assert.NotNull(host.Workspace!.FindRelation("by_city"));
        }

        [Fact]
        public void Group_rejects_a_missing_column_for_non_count()
        {
            var (_, tools, _, _) = Setup();
            var r = Run(tools, "ws.group", """{"name":"g","table":"orders","group_by":["cust_id"],"aggregates":[{"function":"sum"}]}""");
            Assert.True(r.IsError);
            Assert.Contains("needs a 'column'", r.Text);
        }

        [Fact]
        public void Compare_returns_counts_and_creates_a_view()
        {
            var (host, tools, _, _) = Setup();
            MakeCsv("customers_new.csv", "id,name,city\n001,kim,Seoul\n002,lee,Incheon\n005,new,Ulsan\n");
            Run(tools, "ws.add_source", """{"paths":["customers_new.csv"]}""");
            var j = Json(Run(tools, "ws.compare", """{"name":"diff","left":"customers","right":"customers_new","keys":[{"left":"id","right":"id"}]}"""));
            Assert.NotNull(j["summary_counts"]);
            Assert.NotNull(host.Workspace!.FindRelation("diff"));
            Assert.Contains("added", j["summary_counts"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- 질의와 데이터 정책

        private const string JoinSelect = "SELECT c.name, o.amount FROM customers c JOIN orders o ON c.id = o.cust_id ORDER BY o.order_id";

        [Fact]
        public void Query_under_SummaryOnly_returns_counts_and_numeric_aggregates_but_no_values()
        {
            var (_, tools, _, _) = Setup(Summary);
            var r = Run(tools, "ws.query", "{\"sql\":\"" + JoinSelect + "\"}");
            var j = Json(r);
            Assert.Equal(3, (long)j["row_count"]!);
            Assert.Null(j["rows"]);
            var amount = j["columns"]!.AsArray().Single(c => (string)c!["name"]! == "amount")!;
            Assert.Equal(50, (double)amount["min"]!);
            Assert.Equal(100, (double)amount["max"]!);
            Assert.Contains("SummaryOnly", r.Text);
            Assert.DoesNotContain("kim", r.Text);
            Assert.DoesNotContain("lee", r.Text);
        }

        [Fact]
        public void Query_with_RowsAllowed_returns_capped_rows_and_reports_truncation()
        {
            var (_, tools, _, _) = Setup(Policy(AgentDataPolicy.RowsAllowed));
            var j = Json(Run(tools, "ws.query", "{\"sql\":\"" + JoinSelect + "\",\"max_rows\":2}"));
            Assert.Equal(3, (long)j["row_count"]!);
            Assert.Equal(2, j["rows"]!.AsArray().Count);
            Assert.Equal("kim", (string)j["rows"]![0]![0]!);
            Assert.NotNull(j["truncated"]);
        }

        [Fact]
        public void Query_with_RowsWithApproval_asks_first_and_falls_back_to_aggregates_when_declined()
        {
            var (_, tools, _, _) = Setup(Policy(AgentDataPolicy.RowsWithApproval));
            var no = new Approvals(false);
            var declined = Run(tools, "ws.query", "{\"sql\":\"" + JoinSelect + "\"}", no);
            Assert.Single(no.Calls);
            Assert.Equal(ApprovalKind.RowSharing, no.Calls[0].Kind);
            Assert.DoesNotContain("kim", declined.Text);
            Assert.Equal(3, (long)Json(declined)["row_count"]!);

            var yes = new Approvals(true);
            var granted = Json(Run(tools, "ws.query", "{\"sql\":\"" + JoinSelect + "\"}", yes));
            Assert.Single(yes.Calls);
            Assert.Equal("kim", (string)granted["rows"]![0]![0]!);
        }

        [Fact]
        public void Query_open_tab_shows_the_result_to_the_user_but_the_model_still_gets_only_the_policy()
        {
            var (host, tools, _, _) = Setup(Summary);
            var r = Run(tools, "ws.query", "{\"sql\":\"" + JoinSelect + "\",\"open_tab\":true,\"title\":\"joined\"}");
            var j = Json(r);
            Assert.Single(host.QueryTabs);
            Assert.Equal("joined", (string)j["opened_tab"]!);
            Assert.DoesNotContain("kim", r.Text);
        }

        [Fact]
        public void Query_errors_are_reported_with_position_and_nothing_is_run_for_non_select()
        {
            var (_, tools, _, _) = Setup();
            var r = Run(tools, "ws.query", """{"sql":"SELECT FROM"}""");
            Assert.True(r.IsError);
            var drop = Run(tools, "ws.query", """{"sql":"DROP TABLE orders"}""");
            Assert.True(drop.IsError);
            Assert.Contains("SELECT", drop.Text);
        }

        // ---------------------------------------------------------------- 파일: 추가·저장

        [Fact]
        public void Add_source_refuses_executables_missing_files_and_folders_and_resolves_relative_paths()
        {
            var (host, tools, _, _) = Setup();
            string exe = MakeCsv("tool.exe", "MZ");
            Assert.Contains("only data files", Run(tools, "ws.add_source", "{\"paths\":[" + JsonSerializer.Serialize(exe) + "]}").Text);
            Assert.Contains("not found", Run(tools, "ws.add_source", """{"paths":["missing.csv"]}""").Text);
            Assert.True(Run(tools, "ws.add_source", "{\"paths\":[" + JsonSerializer.Serialize(_dir) + "]}").IsError);

            MakeCsv("extra.csv", "k,v\n1,a\n");
            var j = Json(Run(tools, "ws.add_source", """{"paths":["extra.csv"]}"""));
            Assert.NotNull(host.Workspace!.FindRelation("extra"));
            Assert.False((bool)j["sources"]![0]!["already_in_workspace"]!);
            var again = Json(Run(tools, "ws.add_source", """{"paths":["extra.csv"]}"""));
            Assert.True((bool)again["sources"]![0]!["already_in_workspace"]!);
            Assert.Single(host.Workspace.Sources.Where(s => s.Name == "extra"));
        }

        [Fact]
        public void Materialize_needs_FileSave_approval_and_never_overwrites_a_source()
        {
            var (_, tools, customersPath, ordersPath) = Setup();
            var ap = new Approvals(true);

            var overSource = Run(tools, "ws.materialize", "{\"name\":\"customers\",\"path\":" + JsonSerializer.Serialize(customersPath) + ",\"overwrite\":true}", ap);
            Assert.True(overSource.IsError);
            Assert.Contains("source file", overSource.Text);
            var overRegistered = Run(tools, "ws.materialize", "{\"name\":\"orders\",\"path\":" + JsonSerializer.Serialize(ordersPath) + ",\"overwrite\":true}", ap);
            Assert.True(overRegistered.IsError);
            Assert.Empty(ap.Calls);
            Assert.Equal(Orders, File.ReadAllText(ordersPath));

            var ok = Json(Run(tools, "ws.materialize", """{"name":"orders","path":"orders_copy.csv"}""", ap));
            Assert.Single(ap.Calls);
            Assert.Equal(ApprovalKind.FileSave, ap.Calls[0].Kind);
            string saved = (string)ok["saved_to"]!;
            Assert.True(File.Exists(saved));
            Assert.Equal(5, File.ReadAllLines(saved).Length);

            Assert.True(Run(tools, "ws.materialize", "{\"name\":\"orders\",\"path\":\"orders_copy.csv\"}", new Approvals(true)).IsError); // exists, no overwrite
            Assert.True(Run(tools, "ws.materialize", "{\"name\":\"orders\",\"path\":\"x.csv\"}", new Approvals(false)).IsError);
            Assert.False(File.Exists(Path.Combine(_dir, "x.csv")));
        }

        // ---------------------------------------------------------------- 열기·전환·가용성

        [Fact]
        public void Open_and_switch_change_the_active_tab()
        {
            var (host, tools, _, _) = Setup();
            var opened = Json(Run(tools, "ws.open", """{"name":"orders"}"""));
            Assert.Equal("orders", (string)opened["tab"]!);
            var sw = Json(Run(tools, "ws.switch", """{"name":"customers.csv"}"""));
            Assert.Equal("customers.csv", (string)sw["tab"]!);
            var missing = Run(tools, "ws.switch", """{"name":"nope"}""");
            Assert.True(missing.IsError);
            Assert.Contains("customers.csv", missing.Text);
            Assert.Equal(new[] { "orders" }, host.OpenedRelations);
        }

        [Fact]
        public void Workspace_tools_explain_when_the_engine_or_host_is_unavailable()
        {
            var (host, tools, _, _) = Setup();
            host.Workspace = null;
            host.WorkspaceUnavailableReason = "native library missing";
            var r = Run(tools, "ws.list_tables", "{}");
            Assert.True(r.IsError);
            Assert.Contains("native library missing", r.Text);

            var plain = new CsvHostTools(new PlainHost(), () => Summary);
            var p = Run(plain, "ws.list_tables", "{}");
            Assert.True(p.IsError);
            Assert.Contains("not available", p.Text);
        }

        [Fact]
        public void Ws_tools_are_registered_with_strict_schemas()
        {
            var names = ToolDefinitions.All.Select(d => d.Name).ToList();
            foreach (string n in new[] { "ws.list_tables", "ws.describe", "ws.add_source", "ws.query", "ws.check_join", "ws.create_view", "ws.append", "ws.compare", "ws.group", "ws.materialize", "ws.open", "ws.switch" })
                Assert.Contains(n, names);
            Assert.Equal(names.Count, names.Distinct().Count());
            foreach (var d in ToolDefinitions.All.Where(d => d.Name.StartsWith("ws.")))
                Assert.Contains("\"additionalProperties\":false", d.ParametersSchema.Replace(" ", ""));
        }

        // ---------------------------------------------------------------- 진짜 Form1

        private static void OnForm(Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    using var form = new Form1(new AppSettings());
                    _ = form.Handle;
                    body(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(240)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void Pump(Task task)
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(90)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            Assert.True(task.IsCompleted, "task did not complete");
            task.GetAwaiter().GetResult();
        }

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 60)
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(2); }
            Assert.True(condition(), "timed out waiting for: " + what);
        }

        private static DocumentTab OpenTab(Form1 f, string path)
        {
            var task = f.OpenFileTabAsync(path);
            Pump(task);
            var tab = task.Result!;
            PumpUntil(() => tab.Document is { IndexingComplete: true } && !tab.IsIndexing, "indexing");
            return tab;
        }

        [Fact]
        public void Real_form_opens_two_csvs_creates_a_joined_view_and_protects_every_open_source()
        {
            string people = MakeCsv("people.csv", "id,name\n1,kim\n2,lee\n3,park\n");
            string scores = MakeCsv("scores.csv", "pid,score\n1,90\n2,80\n2,70\n9,10\n");
            OnForm(form =>
            {
                var tabPeople = OpenTab(form, people);
                var tabScores = OpenTab(form, scores);
                var host = (ICsvAgentHost)form;
                var tools = new CsvHostTools(host, () => Policy(AgentDataPolicy.RowsAllowed));
                HostToolResult RunUi(string tool, string args)
                {
                    // 앞선 DoEvents 대기가 동기화 컨텍스트를 기본값으로 되돌릴 수 있다. 도구의 await 연속이 UI 스레드로 돌아오도록 다시 설치한다.
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    var task = tools.ExecuteAsync(new HostToolCall("h", "c", tool, JsonDocument.Parse(args).RootElement.Clone()), new Approvals(true), CancellationToken.None);
                    Pump(task);
                    return task.Result;
                }

                // 목록: 열려 있던 두 파일이 모두 표로 보인다.
                var list = Json(RunUi("ws.list_tables", "{}"));
                Assert.Equal(new[] { "people", "scores" }, list["sources"]!.AsArray().Select(s => (string)s!["name"]!).OrderBy(n => n).ToArray());
                Assert.Equal(2, list["tabs"]!.AsArray().Count);

                // 키 진단 → 뷰 만들기 + 열기
                var check = Json(RunUi("ws.check_join", """{"left":"people","right":"scores","keys":[{"left":"id","right":"pid"}]}"""));
                Assert.Equal(3, (long)check["expected_rows"]!);
                var created = Json(RunUi("ws.create_view",
                    """{"name":"ranked","sql":"SELECT p.name, s.score FROM people p JOIN scores s ON CAST(p.id AS BIGINT) = CAST(s.pid AS BIGINT)","open":true}"""));
                Assert.Equal("ranked", (string)created["view"]!);
                Assert.Equal(TabKind.View, form.ActiveTab!.Kind);
                Assert.True(form.ActiveTab.IsReadOnly);
                PumpUntil(() => form.ActiveTab!.Document is { IndexingComplete: true }, "view indexing");
                Assert.Equal(3, host.GetInfo()!.TotalRows);

                // 읽기 전용 뷰 탭: 에이전트 편집 거부(승인 전에 거절되거나 적용에서 거절 — 어느 쪽이든 문서는 그대로).
                var edit = RunUi("csv.edit_cells", """{"edits":[{"row":1,"column":"score","value":"0"}]}""");
                Assert.True(edit.IsError, edit.Text);
                Assert.True(form.ActiveTab.Document!.Edits.IsEmpty);

                // csv.* 는 활성 탭(뷰)을 읽는다.
                var rows = RunUi("csv.get_rows", """{"count":10}""");
                Assert.False(rows.IsError, rows.Text);
                Assert.Contains("kim", rows.Text);

                // 다른 탭의 원본 경로에도 저장 금지
                RunUi("ws.switch", """{"name":"people.csv"}""");
                Assert.Same(tabPeople, form.ActiveTab);
                var overOther = RunUi("csv.save_edits_as", "{\"path\":" + JsonSerializer.Serialize(scores) + ",\"overwrite\":true}");
                Assert.True(overOther.IsError, overOther.Text);

                // 실체화: 새 파일로, 열린 원본은 거부
                string outPath = Path.Combine(_dir, "ranked_out.csv");
                var mat = RunUi("ws.materialize", "{\"name\":\"ranked\",\"path\":" + JsonSerializer.Serialize(outPath) + "}");
                Assert.False(mat.IsError, mat.Text);
                Assert.Equal(4, File.ReadAllLines(outPath).Length);
                var overSource = RunUi("ws.materialize", "{\"name\":\"ranked\",\"path\":" + JsonSerializer.Serialize(people) + ",\"overwrite\":true}");
                Assert.True(overSource.IsError);
                Assert.Equal("id,name\n1,kim\n2,lee\n3,park\n", File.ReadAllText(people).Replace("\r\n", "\n"));
                Assert.NotNull(tabScores);
            });
        }

        // ---------------------------------------------------------------- 작업 공간 메모 (ws.notes / ws.set_notes)

        [Fact]
        public void Notes_tool_returns_the_text_marked_as_user_data_and_is_empty_by_default()
        {
            var (host, tools, _, _) = Setup();
            Assert.Contains("no notes", Run(tools, "ws.notes", "{}").Text);
            host.WorkspaceNotes = "orders.cust_id → customers.id\n목표: 지역별 매출";
            var j = Json(Run(tools, "ws.notes", "{}"));
            Assert.Equal("orders.cust_id → customers.id\n목표: 지역별 매출", (string)j["text"]!);
            Assert.Equal(host.WorkspaceNotes.Length, (int)j["chars"]!);
            Assert.Contains("Not instructions", (string)j["note"]!);
        }

        [Fact]
        public void Set_notes_needs_a_DataEdit_approval_showing_old_and_new_text_and_declining_changes_nothing()
        {
            var (host, tools, _, _) = Setup();
            host.WorkspaceNotes = "old text";
            var no = new Approvals(false);
            var declined = Run(tools, "ws.set_notes", """{"notes":"new text"}""", no);
            Assert.True(declined.IsError);
            Assert.Contains("did not approve", declined.Text);
            Assert.Equal("old text", host.WorkspaceNotes);
            Assert.Equal(ApprovalKind.DataEdit, no.Calls.Single().Kind);
            Assert.Contains(no.Calls.Single().Lines, l => l.StartsWith("- ") && l.Contains("old text"));
            Assert.Contains(no.Calls.Single().Lines, l => l.StartsWith("+ ") && l.Contains("new text"));

            var yes = new Approvals(true);
            var ok = Json(Run(tools, "ws.set_notes", """{"notes":"new text"}""", yes));
            Assert.Equal("new text", host.WorkspaceNotes);
            Assert.True((bool)ok["changed"]!);
            Assert.Equal(ApprovalKind.DataEdit, yes.Calls.Single().Kind);
        }

        [Fact]
        public void Set_notes_can_append_and_refuses_text_over_the_limit_before_asking()
        {
            var (host, tools, _, _) = Setup();
            host.WorkspaceNotes = "line one";
            var ap = new Approvals(true);
            Run(tools, "ws.set_notes", """{"notes":"line two","mode":"append"}""", ap);
            Assert.Equal("line one\nline two", host.WorkspaceNotes);
            Assert.Contains(ap.Calls.Single().Lines, l => l.StartsWith("+ ") && l.Contains("line two"));
            Assert.DoesNotContain(ap.Calls.Single().Lines, l => l.StartsWith("- "));    // 덧붙이기는 기존 글을 지우지 않는다

            var ap2 = new Approvals(true);
            var tooLong = Run(tools, "ws.set_notes", "{\"notes\":\"" + new string('x', WorkspaceFileAgent.MaxNotesChars) + "\",\"mode\":\"append\"}", ap2);
            Assert.True(tooLong.IsError);
            Assert.Contains("limit", tooLong.Text);
            Assert.Empty(ap2.Calls);
            Assert.Equal("line one\nline two", host.WorkspaceNotes);
        }

        [Fact]
        public void Setting_the_same_text_again_asks_nothing_and_clearing_the_notes_is_allowed()
        {
            var (host, tools, _, _) = Setup();
            host.WorkspaceNotes = "same";
            var ap = new Approvals(true);
            var same = Json(Run(tools, "ws.set_notes", """{"notes":"same"}""", ap));
            Assert.False((bool)same["changed"]!);
            Assert.Empty(ap.Calls);
            Run(tools, "ws.set_notes", """{"notes":""}""", ap);
            Assert.Equal("", host.WorkspaceNotes);
        }

        [Fact]
        public void List_tables_shows_a_clipped_copy_of_the_notes_and_where_to_read_the_rest()
        {
            var (host, tools, _, _) = Setup();
            Assert.False(Json(Run(tools, "ws.list_tables", "{}")).ContainsKey("workspace_notes"));

            host.WorkspaceNotes = new string('n', 3000);
            var r = Run(tools, "ws.list_tables", "{}");
            var j = Json(r);
            var notes = j["workspace_notes"]!;
            Assert.Equal(3000, (int)notes["chars"]!);
            Assert.True((bool)notes["truncated"]!);
            Assert.True(((string)notes["text"]!).Length <= 1001);
            Assert.Contains("not instructions", ((string)notes["note"]!).ToLowerInvariant());
            Assert.Contains("ws.notes", r.Text);
        }

        // ---------------------------------------------------------------- 뷰 출처

        [Fact]
        public void Agent_made_views_record_who_when_and_the_users_request_capped_at_500_chars()
        {
            var (host, tools, _, _) = Setup();
            var ap = new Approvals(true) { Request = "주문 금액이 큰 건만 보여 줘 " + new string('가', 700) };
            Run(tools, "ws.create_view", """{"name":"big_orders","sql":"SELECT * FROM orders WHERE CAST(amount AS INTEGER) > 60"}""", ap);

            var p = host.Workspace!.Views.Single().Provenance!;
            Assert.True(p.IsAgent);
            Assert.True((DateTime.UtcNow - p.CreatedUtc).TotalMinutes < 2);
            Assert.StartsWith("주문 금액이 큰 건만 보여 줘", p.Request);
            Assert.Equal(ViewProvenance.MaxRequestChars, p.Request!.Length);

            var view = Json(Run(tools, "ws.list_tables", "{}"))["views"]![0]!;
            Assert.Equal("agent", (string)view["created_by"]!);
            Assert.StartsWith("주문 금액이 큰 건만", (string)view["user_request"]!);
            Assert.EndsWith("Z", (string)view["created_utc"]!);
        }

        [Theory]
        [InlineData("ws.append", """{"name":"v","tables":["customers","orders"]}""")]
        [InlineData("ws.compare", """{"name":"v","left":"customers","right":"customers","keys":[{"left":"id","right":"id"}]}""")]
        [InlineData("ws.group", """{"name":"v","table":"orders","group_by":["cust_id"],"aggregates":[{"function":"count"}]}""")]
        public void Every_view_creating_tool_records_the_agent_as_the_author(string tool, string args)
        {
            var (host, tools, _, _) = Setup();
            var r = Run(tools, tool, args, new Approvals(true) { Request = "please" });
            Assert.False(r.IsError, r.Text);
            var p = host.Workspace!.Views.Single().Provenance!;
            Assert.True(p.IsAgent);
            Assert.Equal("please", p.Request);
        }

        [Fact]
        public void Redefining_a_view_makes_the_agent_its_latest_author_and_a_missing_request_is_just_absent()
        {
            var (host, tools, _, _) = Setup();
            var view = host.Workspace!.CreateView("v", "SELECT * FROM orders", false, ViewProvenance.User());
            Run(tools, "ws.create_view", """{"name":"v","sql":"SELECT * FROM customers","replace":true}""", new Approvals(true));
            Assert.True(view.Provenance!.IsAgent);
            Assert.Null(view.Provenance.Request);          // 호스트가 요청을 모르면 적지 않는다(지어내지 않는다)
        }
    }
}
