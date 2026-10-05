using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    // 실제 Form1(보이지 않는 창)에서 작업 공간 계약: 지연 등록·멱등, 워크북(DB) 등록, 뷰 탭 열기·새로 고침(탭 교체), 결과 탭(읽기 전용),
    // 파일로 저장(보호된 경로 거부·BOM·원문 보존), 저장 안 한 편집 스냅숏, 탐색기 트리, SQL 편집기, 취소, 엔진 없음 처리.
    [Collection("SavedViewStore")]
    public class WorkspaceUiTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_wsui_" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _paths = new();
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        private static readonly CancellationToken NoCancel = CancellationToken.None;

        static WorkspaceUiTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public WorkspaceUiTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            foreach (string p in _paths)
                try { if (EditJournal.KeyFor(p) is { } key) EditJournal.Delete(EditJournal.DefaultDirectory, key); } catch { }
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string MakeCsv(string name, string content, Encoding? enc = null)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, enc ?? new UTF8Encoding(false));
            _paths.Add(path);
            return path;
        }

        private const string CsvA = "id,name,score\n1,kim,30\n2,lee,10\n3,park,20\n4,choi,40\n";
        private const string CsvB = "id,city\n1,seoul\n2,busan\n3,daegu\n";

        private static T Get<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;
        private static object? Invoke(Form1 f, string method, params object[] args) => typeof(Form1).GetMethod(method, Inst)!.Invoke(f, args);

        private static void Pump(Task task)
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(90)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            Assert.True(task.IsCompleted, "task did not complete");
            task.GetAwaiter().GetResult();
        }

        private static T Await<T>(Task<T> task) { Pump(task); return task.Result; }

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 30)
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(2); }
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            Assert.True(condition(), "timed out waiting for: " + what);
        }

        private void OnForm(Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Form1? form = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    form = new Form1(new AppSettings());
                    _ = form.Handle;
                    body(form);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { if (form is not null) typeof(Form1).GetMethod("DisposeWorkspaceUi", Inst)!.Invoke(form, null); } catch { }
                    try { form?.Dispose(); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(240)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static DocumentTab Open(Form1 f, string path)
        {
            var tab = Await(f.OpenFileTabAsync(path));
            Assert.NotNull(tab);
            WaitIdle(f, tab!);
            return tab!;
        }

        private static void WaitIdle(Form1 f, DocumentTab tab)
            => PumpUntil(() => tab.Document is { IndexingComplete: true } && !tab.IsIndexing && !Get<bool>(f, "_busy"), "tab indexing to finish");

        private static WorkspaceTable Table(Form1 f, string name)
            => f.Workspace!.Sources.SelectMany(s => s.Tables).Single(t => t.Name == name);

        private static string[] Lines(string path) => File.ReadAllLines(path);

        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void Tabs_stay_out_of_the_engine_until_registered_and_registration_is_idempotent_and_reuses_the_tab_encoding()
        {
            string path = MakeCsv("명단.csv", "이름,나이\n김철수,30\n이영희,25\n박민수,41\n", Encoding.GetEncoding(949));
            OnForm(form =>
            {
                int changes = 0;
                form.WorkspaceChanged += () => changes++;
                var tab = Open(form, path);

                // 탭을 열기만 해서는 엔진도 변환 사본도 만들지 않는다.
                Assert.Null(form.ExistingWorkspace);
                Assert.False(form.IsTabRegistered(tab));

                var src = Await(form.RegisterTabAsync(tab, NoCancel));
                Assert.NotNull(src);
                Assert.NotNull(form.ExistingWorkspace);
                var table = src!.Tables[0];
                Assert.Equal(tab.Document!.EncodingName, table.Options.EncodingName); // 탭이 알아낸 인코딩을 그대로 쓴다
                Assert.Equal(tab.Document.Delimiter, table.Options.Delimiter);
                Assert.True(form.IsTabRegistered(tab));
                Assert.True(changes > 0);

                // 멱등: 같은 탭·같은 경로로 다시 불러도 같은 원본.
                Assert.Same(src, Await(form.RegisterTabAsync(tab, NoCancel)));
                Assert.Same(src, Await(form.AddSourcesAsync(new[] { path }, NoCancel)).Single());
                Assert.Single(form.Workspace!.Sources);

                var preview = Await(form.Workspace.PreviewAsync("SELECT 이름 FROM 명단 WHERE 나이 > 28 ORDER BY 나이", 10));
                Assert.Equal(new[] { "김철수", "박민수" }, preview.Rows.Select(r => r[0]).ToArray());
            });
        }

        [Fact]
        public void Workbook_tab_registers_a_private_copy_so_the_tables_survive_closing_the_tab_and_sheets_open_from_the_explorer()
        {
            string db = Path.Combine(_dir, "book.db");
            _paths.Add(db);
            var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db, Pooling = false };
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE alpha (id INTEGER, name TEXT); INSERT INTO alpha VALUES (1,'a'),(2,'b'),(3,'c');" +
                                  "CREATE TABLE beta (k INTEGER); INSERT INTO beta VALUES (10),(20);";
                cmd.ExecuteNonQuery();
            }
            OnForm(form =>
            {
                var tab = Open(form, db);
                Assert.Equal(TabKind.Sheet, tab.Kind);
                var src = Await(form.RegisterTabAsync(tab, NoCancel))!;
                Assert.Equal(WorkspaceSourceKind.Database, src.Kind);
                Assert.Equal(new[] { "alpha", "beta" }, src.Tables.Select(t => t.Name).ToArray());
                Assert.Same(src, Await(form.RegisterTabAsync(tab, NoCancel)));

                // 탭을 닫아도(임시 CSV가 지워져도) 표는 그대로 질의된다.
                Assert.True(form.CloseTab(tab, askUnsaved: false));
                PumpUntil(() => tab.DisposeCompletion.IsCompleted, "tab resources released");
                Assert.Equal("3", Await(form.Workspace!.PreviewAsync($"SELECT count(*) FROM {src.Tables[0].SqlReference}", 5)).Rows[0][0]);

                // 표를 열면 워크북 탭이 열리고 그 시트로 전환된다.
                var beta = src.Tables[1];
                var opened = Await(form.OpenRelationTabAsync(beta, NoCancel))!;
                Assert.Equal(TabKind.Sheet, opened.Kind);
                WaitIdle(form, opened);
                Assert.Equal("beta", opened.SheetName);
                var again = Await(form.OpenRelationTabAsync(src.Tables[0], NoCancel))!;
                Assert.Same(opened, again);
                Assert.Equal("alpha", again.SheetName);
            });
        }

        [Fact]
        public void Adding_a_workbook_without_a_tab_imports_it_and_dropping_a_source_releases_the_copy()
        {
            string db = Path.Combine(_dir, "solo.db");
            _paths.Add(db);
            var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db, Pooling = false };
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (7),(8);";
                cmd.ExecuteNonQuery();
            }
            OnForm(form =>
            {
                var src = Await(form.AddSourcesAsync(new[] { db }, NoCancel)).Single();
                Assert.Equal(WorkspaceSourceKind.Database, src.Kind);
                Assert.Equal(Path.GetFullPath(db), src.Path);
                Assert.Empty(form.Tabs); // 탭을 만들지 않는다
                Assert.Equal("2", Await(form.Workspace!.PreviewAsync($"SELECT count(*) FROM {src.Tables[0].SqlReference}", 5)).Rows[0][0]);
                var holds = Get<System.Collections.IDictionary>(form, "_workbookHolds");
                Assert.Equal(1, holds.Count);
                Assert.True(form.RemoveConfirmed(src, cascade: false));
                PumpUntil(() => holds.Count == 0, "workbook copy released");
            });
        }

        [Fact]
        public void View_tabs_open_read_only_are_reused_and_swap_to_a_fresh_document_when_the_source_changes()
        {
            string a = MakeCsv("a.csv", CsvA);
            OnForm(form =>
            {
                Await(form.AddSourcesAsync(new[] { a }, NoCancel));
                var ws = form.Workspace!;
                var view = ws.CreateView("big", "SELECT * FROM a WHERE score > 15 ORDER BY id");
                var tab = Await(form.OpenRelationTabAsync(view, NoCancel))!;
                WaitIdle(form, tab);
                Assert.Equal(TabKind.View, tab.Kind);
                Assert.True(tab.IsReadOnly);
                Assert.Equal("big", tab.ViewName);
                Assert.Equal(view.ResultPath, tab.Path);
                Assert.Equal(3, tab.Document!.DataRowsAvailable);
                Assert.Throws<InvalidOperationException>(() => form.RequireEditableTab());

                // 같은 뷰를 다시 열면 같은 탭.
                Assert.Same(tab, Await(form.OpenRelationTabAsync(view, NoCancel)));
                Assert.Same(tab, form.FindViewTab(view));

                // 원본이 바뀌면 오래된 것으로 표시되고, 다시 열면 새 문서로 교체된다(탭 수·자리는 그대로).
                File.WriteAllText(a, CsvA + "5,jung,50\n", new UTF8Encoding(false));
                Assert.True(ws.IsStale(view));
                int count = form.Tabs.Count, index = form.Tabs.ToList().IndexOf(tab);
                var fresh = Await(form.OpenRelationTabAsync(view, NoCancel))!;
                Assert.NotSame(tab, fresh);
                Assert.True(tab.IsClosed);
                Assert.Equal(count, form.Tabs.Count);
                Assert.Equal(index, form.Tabs.ToList().IndexOf(fresh));
                WaitIdle(form, fresh);
                Assert.Equal(4, fresh.Document!.DataRowsAvailable);
                Assert.False(ws.IsStale(view));

                // 명시적 새로 고침은 항상 다시 계산한다.
                string before = fresh.Path;
                var again = Await(form.RefreshViewTabAsync(fresh, force: true, NoCancel))!;
                Assert.NotSame(fresh, again);
                Assert.NotEqual(before, again.Path);
                Assert.Equal(view.ResultPath, again.Path);
                Assert.Same(view, form.ViewOfTab(again));
            });
        }

        [Fact]
        public void Query_result_tabs_are_read_only_and_failures_open_nothing()
        {
            string a = MakeCsv("a.csv", CsvA);
            OnForm(form =>
            {
                Await(form.AddSourcesAsync(new[] { a }, NoCancel));
                var result = Await(form.OpenQueryResultTabAsync("SELECT name, score FROM a ORDER BY score DESC", "Query 1", NoCancel));
                WaitIdle(form, result);
                Assert.Equal(TabKind.Result, result.Kind);
                Assert.Equal("Query 1", result.Title);
                Assert.True(result.IsReadOnly);
                Assert.Null(result.ViewName);
                Assert.Equal(4, result.Document!.DataRowsAvailable);
                Assert.Equal(new[] { "choi", "40" }, result.Document.GetDataRow(0));
                Assert.Throws<InvalidOperationException>(() => form.RequireEditableTab());

                int tabs = form.Tabs.Count;
                Assert.ThrowsAny<Exception>(() => Await(form.OpenQueryResultTabAsync("DROP TABLE a", "Query 2", NoCancel)));
                Assert.ThrowsAny<Exception>(() => Await(form.OpenQueryResultTabAsync("SELECT * FROM missing_table", "Query 3", NoCancel)));
                Assert.Equal(tabs, form.Tabs.Count);
                Assert.Equal("Query 1", form.NextQueryTitle()); // 제목 번호는 호출자가 NextQueryTitle로 매긴다
                Assert.Equal("Query 2", form.NextQueryTitle());

                // 결과 탭을 닫으면 임시 CSV도 지워진다.
                string file = result.Path;
                form.CloseTab(result, askUnsaved: false);
                PumpUntil(() => !File.Exists(file), "result file removed");
            });
        }

        [Fact]
        public void Save_as_file_preserves_raw_text_writes_a_bom_and_never_overwrites_a_source()
        {
            string a = MakeCsv("a.csv", "id,code,amt\n1,001,\"1,000\"\n2,002,2\n");
            string other = MakeCsv("other.csv", "x\n1\n");
            OnForm(form =>
            {
                var tab = Open(form, a);
                var src = Await(form.RegisterTabAsync(tab, NoCancel))!;
                var table = src.Tables[0];

                // 표는 형 변환 전의 원문 그대로(선행 0·천 단위 쉼표 보존), CSV는 UTF-8 BOM.
                string csv = Path.Combine(_dir, "out.csv");
                Assert.Equal(Path.GetFullPath(csv), Await(form.SaveRelationAsAsync(table, csv, NoCancel)));
                byte[] bytes = File.ReadAllBytes(csv);
                Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
                Assert.Equal(new[] { "id,code,amt", "1,001,\"1,000\"", "2,002,2" }, File.ReadAllLines(csv, Encoding.UTF8));

                // 엑셀.
                string xlsx = Path.Combine(_dir, "out.xlsx");
                Await(form.SaveRelationAsAsync(table, xlsx, NoCancel));
                Assert.Equal((byte)'P', File.ReadAllBytes(xlsx)[0]); // zip(OOXML)

                // 뷰 결과도 저장된다.
                var view = form.Workspace!.CreateView("only1", "SELECT id, code FROM a WHERE id = 1");
                string viewCsv = Path.Combine(_dir, "view.csv");
                Await(form.SaveRelationAsAsync(view, viewCsv, NoCancel));
                Assert.Equal(new[] { "id,code", "1,001" }, File.ReadAllLines(viewCsv, Encoding.UTF8));

                // 보호: 열린 탭 파일·작업 공간 원본·뷰 결과 파일·잘못된 확장자는 거부하고 원본은 그대로.
                string original = File.ReadAllText(a);
                Assert.Throws<InvalidOperationException>(() => Await(form.SaveRelationAsAsync(table, a, NoCancel)));
                Assert.Throws<InvalidOperationException>(() => Await(form.SaveRelationAsAsync(table, Path.Combine(_dir, "A.CSV"), NoCancel)));
                Assert.Throws<InvalidOperationException>(() => Await(form.SaveRelationAsAsync(table, view.ResultPath!, NoCancel)));
                Assert.Throws<ArgumentException>(() => Await(form.SaveRelationAsAsync(table, Path.Combine(_dir, "x.txt"), NoCancel)));
                Assert.Equal(original, File.ReadAllText(a));

                // 등록만 했고 탭은 안 연 원본도 보호된다.
                var src2 = Await(form.AddSourcesAsync(new[] { other }, NoCancel)).Single();
                Assert.Throws<InvalidOperationException>(() => Await(form.SaveRelationAsAsync(src2.Tables[0], other, NoCancel)));
                Assert.Equal("x\n1\n", File.ReadAllText(other));

                // 관련 없는 기존 파일은 덮어쓴다.
                File.WriteAllText(csv, "stale");
                Await(form.SaveRelationAsAsync(table, csv, NoCancel));
                Assert.Equal("id,code,amt", File.ReadAllLines(csv, Encoding.UTF8)[0]);
            });
        }

        [Fact]
        public void Unsaved_edits_reach_a_view_only_when_it_asks_for_them()
        {
            string a = MakeCsv("a.csv", CsvA);
            OnForm(form =>
            {
                var tab = Open(form, a);
                Await(form.RegisterTabAsync(tab, NoCancel));
                Assert.False(form.AnyTabHasUnsavedEdits());
                Invoke(form, "CommitCellEdit", 0, 2, "99");
                Assert.True(tab.HasUnsavedEdits);
                Assert.True(form.AnyTabHasUnsavedEdits());

                var ws = form.Workspace!;
                var saved = ws.CreateView("saved_basis", "SELECT * FROM a ORDER BY id", includeUnsavedEdits: false);
                var edited = ws.CreateView("edited_basis", "SELECT * FROM a ORDER BY id", includeUnsavedEdits: true);
                Await(ws.MaterializeViewAsync(saved));
                Await(ws.MaterializeViewAsync(edited));
                Assert.Equal("1,kim,30", Lines(saved.ResultPath!)[1]);
                Assert.Equal("1,kim,99", Lines(edited.ResultPath!)[1]);
                Assert.Equal(CsvA, File.ReadAllText(a).Replace("\r\n", "\n")); // 원본 파일은 그대로
                Assert.Same(tab, form.FindTabForTable(Table(form, "a")));

                // 편집이 없는 표는 스냅숏을 만들지 않는다(저장된 파일 기준).
                string b = MakeCsv("b.csv", CsvB);
                var srcB = Await(form.AddSourcesAsync(new[] { b }, NoCancel)).Single();
                Assert.Null(form.CreateEditSnapshot(srcB.Tables[0], NoCancel));
            });
        }

        [Fact]
        public void Explorer_lists_open_files_then_sources_views_and_results_with_columns_counts_and_stale_markers()
        {
            string a = MakeCsv("a.csv", CsvA);
            string b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                var tabA = Open(form, a);
                form.SetWorkspaceExplorerVisible(true);
                Assert.True(form.WorkspaceDockVisible);
                var explorer = form.Explorer!;
                explorer.RefreshTree();
                Assert.Null(form.ExistingWorkspace); // 탐색기를 펼쳐도 엔진을 만들지 않는다

                var rows = explorer.Snapshot().ToList();
                Assert.Contains(rows, r => r.Info.Key == "grp:open");
                Assert.Contains(rows, r => r.Info.Kind == WorkspaceExplorer.NodeKind.Tab && r.Text == "a.csv");
                Assert.DoesNotContain(rows, r => r.Info.Key == "grp:src");

                // 올리면 "열린 파일"에서 "원본"으로 옮겨 가고 컬럼에 타입이 붙는다.
                Await(form.RegisterTabAsync(tabA, NoCancel));
                Await(form.AddSourcesAsync(new[] { b }, NoCancel));
                explorer.RefreshTree();
                rows = explorer.Snapshot().ToList();
                Assert.DoesNotContain(rows, r => r.Info.Key == "grp:open");
                Assert.Contains(rows, r => r.Info.Key == "grp:src");
                Assert.True(explorer.Select(i => i.Kind == WorkspaceExplorer.NodeKind.Column && i.Item is WorkspaceColumn { Name: "score", Type: ColumnValueType.Integer, IsConverted: true }));
                Assert.Contains(explorer.Snapshot(), r => r.Info.Item is WorkspaceColumn { Name: "city" });

                // 행 수는 표를 고르면 백그라운드에서 센다.
                var tableA = Table(form, "a");
                Assert.True(explorer.Select(i => ReferenceEquals(i.Item, tableA)));
                PumpUntil(() => explorer.KnownRowCount(tableA) == 4, "row count of a");
                Assert.StartsWith("4", explorer.DetailText(explorer.SelectedInfo!));

                // 뷰: 정상 → 행 수, 원본 변경 → ⚠, 깨짐 → 오류 표시.
                var ws = form.Workspace!;
                var view = ws.CreateView("v1", "SELECT * FROM b");
                var broken = ws.CreateView("v2", "SELECT city FROM b");
                Await(form.OpenRelationTabAsync(view, NoCancel));
                explorer.RefreshTree();
                var node = explorer.Snapshot().Single(r => ReferenceEquals(r.Info.Item, view)).Info;
                Assert.StartsWith("3", explorer.DetailText(node));
                File.WriteAllText(b, CsvB + "4,ulsan\n", new UTF8Encoding(false));
                explorer.RefreshTree(); // 상태 표시는 스냅숏이라 다시 읽어야 ⚠가 보인다
                Assert.Contains("⚠", explorer.DetailText(node));
                File.WriteAllText(b, "zzz\n1\n", new UTF8Encoding(false));
                Pump(form.RefreshSourceAsync(Table(form, "b").Source, NoCancel));
                Assert.Contains("⚠", explorer.DetailText(explorer.Snapshot().Single(r => ReferenceEquals(r.Info.Item, broken)).Info));

                // 질의 결과 그룹.
                var result = Await(form.OpenQueryResultTabAsync("SELECT 1 AS x", "Query 1", NoCancel));
                explorer.RefreshTree();
                Assert.Contains(explorer.Snapshot(), r => r.Info.Kind == WorkspaceExplorer.NodeKind.Result && ReferenceEquals(r.Info.Item, result));
            });
        }

        [Fact]
        public void Adding_files_and_folders_reports_each_failure_and_keeps_the_good_files()
        {
            string a = MakeCsv("a.csv", CsvA);
            Directory.CreateDirectory(Path.Combine(_dir, "folder"));
            string good = MakeCsv(Path.Combine("folder", "good.csv"), CsvB);
            string empty = MakeCsv(Path.Combine("folder", "empty.csv"), "");
            MakeCsv(Path.Combine("folder", "readme.md"), "not data");
            OnForm(form =>
            {
                var messages = new List<string>();
                form.WorkspaceMessageSink = messages.Add;
                var added = Await(form.AddFilesUiAsync(new[] { a, Path.Combine(_dir, "folder") }));
                Assert.Equal(new[] { "a", "good" }, added.Select(s => s.Name).OrderBy(n => n).ToArray());
                var message = Assert.Single(messages);
                Assert.Contains("empty.csv", message);
                Assert.DoesNotContain("readme", message);
                Assert.True(form.WorkspaceDockVisible); // 작업이 도는 동안 취소할 수 있게 탐색기를 펼친다
                Assert.False(form.WorkspaceOperationRunning);
            });
        }

        [Fact]
        public void A_running_workspace_operation_can_be_cancelled_and_only_one_runs_at_a_time()
        {
            OnForm(form =>
            {
                var task = form.RunWorkspaceUiAsync("long", ct => Task.Delay(Timeout.Infinite, ct));
                Assert.True(form.WorkspaceOperationRunning);
                Assert.Equal("long", form.Explorer!.StatusText);
                Assert.True(form.Explorer.IsBusyShown);
                Assert.False(Await(form.RunWorkspaceUiAsync("second", _ => Task.CompletedTask))); // 동시에 하나만
                form.CancelWorkspaceOperation();
                Assert.False(Await(task));
                Assert.False(form.WorkspaceOperationRunning);
                Assert.False(form.Explorer.IsBusyShown);

                var messages = new List<string>();
                form.WorkspaceMessageSink = messages.Add;
                Assert.False(Await(form.RunWorkspaceUiAsync("boom", _ => throw new InvalidOperationException("kaboom"))));
                Assert.Equal(new[] { "kaboom" }, messages);
                Assert.True(Await(form.RunWorkspaceUiAsync("ok", _ => Task.CompletedTask)));
            });
        }

        [Fact]
        public void The_sql_editor_window_runs_into_numbered_result_tabs_and_saves_views()
        {
            string a = MakeCsv("a.csv", CsvA);
            string b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                Open(form, a);
                Open(form, b);
                var editor = Await(form.NewQueryAsync("SELECT a.name, b.city FROM a JOIN b ON a.id = b.id ORDER BY a.id", null));
                Assert.NotNull(editor);
                Assert.Same(editor, Assert.Single(form.SqlEditors));
                Assert.NotNull(form.ExistingWorkspace); // 열려 있던 탭이 표로 올라갔다
                Assert.Equal(2, form.Workspace!.Sources.Count);

                var info = Await(editor!.Panel.RunAsync());
                Assert.NotNull(info);
                Assert.Equal(3, info!.RowCount);
                var result = form.ActiveTab!;
                Assert.Equal(TabKind.Result, result.Kind);
                Assert.Equal("Query 1", result.Title);
                WaitIdle(form, result);
                Assert.Equal(new[] { "kim", "seoul" }, result.Document!.GetDataRow(0));

                Assert.NotNull(Await(editor.Panel.RunAsync()));
                Assert.Equal("Query 2", form.ActiveTab!.Title);

                // 오류는 편집기에 남고 탭은 늘지 않는다.
                int tabs = form.Tabs.Count;
                editor.Panel.Sql = "SELECT * FROM nope";
                Assert.Null(Await(editor.Panel.RunAsync()));
                Assert.Contains("nope", editor.Panel.LastError, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(tabs, form.Tabs.Count);

                // 뷰로 저장 → View 탭.
                var messages = new List<string>();
                form.WorkspaceMessageSink = messages.Add;
                Assert.Null(Await(form.CreateAndOpenViewAsync("bad name", "SELECT 1", false)));
                Assert.Single(messages);
                var view = Await(form.CreateAndOpenViewAsync("joined", "SELECT a.name, b.city FROM a JOIN b ON a.id = b.id", false))!;
                Assert.NotNull(view);
                Assert.Equal(TabKind.View, form.ActiveTab!.Kind);
                Assert.Equal("joined", form.ActiveTab.ViewName);

                // 뷰 편집: SQL과 이름을 바꾸면 탭이 새 결과로 교체된다.
                var oldTab = form.ActiveTab;
                Assert.True(Await(form.UpdateViewAndOpenAsync(view, "joined2", "SELECT a.name FROM a JOIN b ON a.id = b.id WHERE a.id = 1", false)));
                Assert.Equal("joined2", view.Name);
                Assert.True(oldTab.IsClosed);
                Assert.Equal("joined2", form.ActiveTab!.Title);
                WaitIdle(form, form.ActiveTab);
                Assert.Equal(1, form.ActiveTab.Document!.DataRowsAvailable);

                // 이름 검사.
                var ws = form.Workspace;
                Assert.Null(form.ValidateViewName(ws, "fresh_name"));
                Assert.NotNull(form.ValidateViewName(ws, "a"));         // 이미 쓰는 이름
                Assert.NotNull(form.ValidateViewName(ws, "has space")); // 규칙 위반
                Assert.NotNull(form.ValidateViewName(ws, ""));
                Assert.Null(form.ValidateViewName(ws, "joined2", current: "joined2"));

                editor.Close();
                editor.Dispose();
                Assert.Empty(form.SqlEditors);
            });
        }

        [Fact]
        public void Removing_a_source_needs_cascade_when_views_use_it_and_closes_their_tabs()
        {
            string a = MakeCsv("a.csv", CsvA);
            OnForm(form =>
            {
                var src = Await(form.AddSourcesAsync(new[] { a }, NoCancel)).Single();
                var view = form.Workspace!.CreateView("dep", "SELECT * FROM a");
                var tab = Await(form.OpenRelationTabAsync(view, NoCancel))!;
                var messages = new List<string>();
                form.WorkspaceMessageSink = messages.Add;

                Assert.False(form.RemoveConfirmed(src, cascade: false));
                Assert.Single(messages);
                Assert.Contains("dep", messages[0]);
                Assert.False(tab.IsClosed);
                Assert.False(form.RenameTo(src, "renamed")); // 뷰가 쓰는 원본은 이름을 바꿀 수 없다
                Assert.Equal(2, messages.Count);

                Assert.True(form.RemoveConfirmed(src, cascade: true));
                Assert.True(tab.IsClosed);
                Assert.Empty(form.Workspace.Sources);
                Assert.Empty(form.Workspace.Views);
            });
        }

        [Fact]
        public void Source_tabs_of_a_view_open_every_file_it_reads_and_tab_menus_follow_the_tab_kind()
        {
            string a = MakeCsv("a.csv", CsvA);
            string b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                Await(form.AddSourcesAsync(new[] { a, b }, NoCancel));
                Assert.Empty(form.Tabs);
                var inner = form.Workspace!.CreateView("inner_v", "SELECT a.id, a.name, b.city FROM a JOIN b ON a.id = b.id");
                var outer = form.Workspace.CreateView("outer_v", "SELECT * FROM inner_v WHERE name <> 'kim'");
                Assert.Equal(2, Await(form.OpenSourceTabsOfViewAsync(outer, NoCancel)));
                Assert.NotNull(form.FindTab(a));
                Assert.NotNull(form.FindTab(b));
                Assert.Equal(2, form.Tabs.Count);
                Assert.Equal(2, Await(form.OpenSourceTabsOfViewAsync(inner, NoCancel))); // 이미 열려 있으면 그 탭을 쓴다
                Assert.Equal(2, form.Tabs.Count);

                var viewTab = Await(form.OpenRelationTabAsync(outer, NoCancel))!;
                using var menu = new System.Windows.Forms.ContextMenuStrip();
                form.AddWorkspaceTabMenuItems(menu, viewTab);
                Assert.Contains(menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>(), i => i.Text is "Refresh View" or "뷰 새로 고침");
                using var fileMenu = new System.Windows.Forms.ContextMenuStrip();
                form.AddWorkspaceTabMenuItems(fileMenu, form.FindTab(a)!);
                Assert.Contains(fileMenu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>(), i => i.Text is "Add to Workspace" or "작업 공간에 추가" && !i.Enabled); // 이미 올라가 있다
            });
        }

        [Fact]
        public void Menus_exist_and_switch_off_with_the_reason_when_the_engine_is_unavailable()
        {
            OnForm(form =>
            {
                var menu = form.WorkspaceMenu;
                Assert.NotNull(menu);
                Assert.Contains(menu.DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>(), i => i.ShortcutKeys == (System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.Q));

                // 엔진이 정상이면 항목이 켜져 있다.
                Invoke(form, "UpdateWorkspaceMenuState");
                Assert.True(Get<System.Windows.Forms.ToolStripMenuItem>(form, "_wsNewQueryMenu").Enabled);

                // 엔진을 쓸 수 없다고 하면(네이티브 DLL 없음 시나리오) 엔진이 필요한 항목이 꺼지고 툴팁에 이유가 나온다.
                typeof(Form1).GetField("_workspaceUnavailable", Inst)!.SetValue(form, "native library missing");
                Assert.Null(form.Workspace);
                Assert.Equal("native library missing", form.WorkspaceUnavailableReason);
                Invoke(form, "UpdateWorkspaceMenuState");
                foreach (string name in new[] { "_wsNewQueryMenu", "_wsAddFilesMenu", "_wsJoinMenu", "_wsAppendMenu", "_wsCompareMenu", "_wsGroupMenu" })
                {
                    var item = Get<System.Windows.Forms.ToolStripMenuItem>(form, name);
                    Assert.False(item.Enabled, name);
                    Assert.Contains("native library missing", item.ToolTipText);
                }
                var ex = Assert.Throws<InvalidOperationException>(() => Await(form.AddSourcesAsync(new[] { Path.Combine(_dir, "x.csv") }, NoCancel)));
                Assert.Contains("native library missing", ex.Message);
                form.SetWorkspaceExplorerVisible(true); // 탐색기는 이유를 안내 띠로 보여 준다
                Assert.Contains(form.Explorer!.Controls.OfType<System.Windows.Forms.Label>(), l => l.Name == "workspaceBanner" && l.Text.Contains("native library missing"));
            });
        }

        // ---------------------------------------------------------------- 탐색기 클릭 = 탭 고르기 · 탭 → 트리 동기화

        private const int WmKeyDown = 0x100, WmKeyUp = 0x101, VkDown = 0x28, VkUp = 0x26;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private string MakeDb(string name)
        {
            string db = Path.Combine(_dir, name);
            _paths.Add(db);
            var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = db, Pooling = false };
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE alpha (id INTEGER, name TEXT); INSERT INTO alpha VALUES (1,'a'),(2,'b'),(3,'c');" +
                              "CREATE TABLE beta (k INTEGER); INSERT INTO beta VALUES (10),(20);";
            cmd.ExecuteNonQuery();
            return db;
        }

        private static bool IsTable(WorkspaceExplorer.NodeInfo i, string name) => i.Kind == WorkspaceExplorer.NodeKind.Table && i.Item is WorkspaceTable t && t.Name == name;
        private static bool IsColumn(WorkspaceExplorer.NodeInfo i, string name) => i.Kind == WorkspaceExplorer.NodeKind.Column && i.Item is WorkspaceColumn c && c.Name == name;
        private static string Selected(WorkspaceExplorer ex) => ex.SelectedInfo?.Key ?? "";
        private static System.Windows.Forms.DataGridView GridOf(Form1 f) => Get<System.Windows.Forms.DataGridView>(f, "grid");
        private static string Status(Form1 f) => Get<System.Windows.Forms.ToolStripStatusLabel>(f, "statusLabel").Text;

        private static WorkspaceExplorer ShowExplorer(Form1 form)
        {
            form.SetWorkspaceExplorerVisible(true);
            var ex = form.Explorer!;
            ex.RefreshTree();
            return ex;
        }

        [Fact]
        public void One_click_on_a_node_with_an_open_tab_activates_it_like_the_tab_strip_and_the_active_ones_node_does_nothing()
        {
            string a = MakeCsv("a.csv", CsvA);
            string b = MakeCsv("b.csv", CsvB);
            string c = MakeCsv("c.csv", "x\n1\n");
            OnForm(form =>
            {
                var tabA = Open(form, a);
                var tabB = Open(form, b);
                var tabC = Open(form, c);                                   // 올리지 않은 파일: "열린 파일" 그룹의 탭 노드
                Await(form.AddSourcesAsync(new[] { a, b }, NoCancel));
                var result = Await(form.OpenQueryResultTabAsync("SELECT 1 AS x", "Query 1", NoCancel));
                WaitIdle(form, result);
                var view = form.Workspace!.CreateView("v1", "SELECT * FROM b");
                var viewTab = Await(form.OpenRelationTabAsync(view, NoCancel))!;
                var ex = ShowExplorer(form);

                Pump(ex.ClickAsync(i => IsTable(i, "a")));
                Assert.Same(tabA, form.ActiveTab);                          // 표 노드 → 파일 탭
                Pump(ex.ClickAsync(i => i.Kind == WorkspaceExplorer.NodeKind.Tab && ReferenceEquals(i.Item, tabC)));
                Assert.Same(tabC, form.ActiveTab);                          // 올리지 않은 파일의 탭 노드
                Pump(ex.ClickAsync(i => i.Kind == WorkspaceExplorer.NodeKind.Result && ReferenceEquals(i.Item, result)));
                Assert.Same(result, form.ActiveTab);                        // 질의 결과 노드
                Pump(ex.ClickAsync(i => i.Kind == WorkspaceExplorer.NodeKind.View && ReferenceEquals(i.Item, view)));
                Assert.Same(viewTab, form.ActiveTab);                       // 뷰 노드 → 뷰 탭(다시 계산하지 않는다)
                Assert.Equal(5, form.Tabs.Count);

                int changes = 0, clicks = form.ExplorerClickCount;
                form.TabsChanged += () => changes++;
                Pump(ex.ClickAsync(i => i.Kind == WorkspaceExplorer.NodeKind.View && ReferenceEquals(i.Item, view)));
                Assert.Equal(0, changes);                                   // 이미 활성인 탭의 노드는 아무것도 바꾸지 않는다
                Assert.Same(viewTab, form.ActiveTab);
                Assert.Equal(clicks + 1, form.ExplorerClickCount);
                Assert.False(form.WorkspaceOperationRunning);
            });
        }

        [Fact]
        public void One_click_on_a_table_that_is_not_open_yet_opens_its_tab()
        {
            string a = MakeCsv("a.csv", CsvA);
            OnForm(form =>
            {
                Await(form.AddSourcesAsync(new[] { a }, NoCancel));
                var ex = ShowExplorer(form);
                Assert.Empty(form.Tabs);

                Pump(ex.ClickAsync(i => IsTable(i, "a")));
                var tab = Assert.Single(form.Tabs);
                Assert.Same(tab, form.ActiveTab);
                Assert.Equal(Path.GetFullPath(a), tab.Path);
                Assert.False(form.WorkspaceOperationRunning);               // 열기가 끝나면 진행 표시도 걷힌다
                Assert.Equal("tbl:" + Table(form, "a").SqlReference, Selected(ex));
            });
        }

        [Fact]
        public void One_click_on_a_workbook_sheet_activates_the_workbook_tab_and_switches_to_that_sheet_and_reopens_a_closed_workbook()
        {
            string db = MakeDb("book.db");
            OnForm(form =>
            {
                var tab = Open(form, db);
                var src = Await(form.RegisterTabAsync(tab, NoCancel))!;
                var other = Open(form, MakeCsv("other.csv", CsvB));         // 워크북 탭을 비활성으로
                var ex = ShowExplorer(form);
                Assert.Same(other, form.ActiveTab);

                Pump(ex.ClickAsync(i => IsTable(i, "beta")));
                Assert.Same(tab, form.ActiveTab);
                Assert.Equal("beta", tab.SheetName);
                Pump(ex.ClickAsync(i => IsTable(i, "alpha")));
                Assert.Equal("alpha", tab.SheetName);
                Assert.Single(form.Tabs, t => t.Kind == TabKind.Sheet);     // 새 탭을 만들지 않는다
                Assert.Equal("tbl:" + src.Tables[0].SqlReference, Selected(ex));

                int clicks = form.Tabs.Count;
                Pump(ex.ClickAsync(i => i.Kind == WorkspaceExplorer.NodeKind.Source));
                Assert.Equal(clicks, form.Tabs.Count);                      // DB 원본 노드는 열지 않는다(펼치기·선택만)

                // 탭을 닫은 통합 문서: 표를 누르면 다시 열고 그 시트로.
                Assert.True(form.CloseTab(tab, askUnsaved: false));
                PumpUntil(() => tab.DisposeCompletion.IsCompleted, "tab resources released");
                Pump(ex.ClickAsync(i => IsTable(i, "beta")));
                var reopened = Assert.Single(form.Tabs, t => t.Kind == TabKind.Sheet);
                Assert.NotSame(tab, reopened);
                Assert.Same(reopened, form.ActiveTab);
                WaitIdle(form, reopened);
                Assert.Equal("beta", reopened.SheetName);
            });
        }

        [Fact]
        public void One_click_on_a_column_activates_its_tab_and_selects_that_column_in_the_first_displayed_row_without_touching_hidden_columns()
        {
            string a = MakeCsv("a.csv", CsvA);
            string b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                var tabA = Open(form, a);
                var tabB = Open(form, b);
                Await(form.AddSourcesAsync(new[] { a, b }, NoCancel));
                var ex = ShowExplorer(form);
                Assert.Same(tabB, form.ActiveTab);
                var grid = GridOf(form);

                Pump(ex.ClickAsync(i => IsColumn(i, "score")));
                Assert.Same(tabA, form.ActiveTab);
                Assert.Equal(2, grid.CurrentCell.ColumnIndex);
                Assert.Equal(0, grid.CurrentCell.RowIndex);
                Assert.Equal(grid.FirstDisplayedScrollingRowIndex, grid.CurrentCell.RowIndex);
                Assert.StartsWith("col:" + Table(form, "a").SqlReference + ":", Selected(ex));   // 컬럼 노드가 선택된 채 남는다(되돌려 놓지 않는다)

                // 숨긴 컬럼: 탭은 활성화하되 숨김을 풀지도, 선택을 옮기지도 않고 상태 줄로 알린다.
                Pump(ex.ClickAsync(i => IsColumn(i, "city")));
                Assert.Same(tabB, form.ActiveTab);
                Assert.Equal(1, GridOf(form).CurrentCell.ColumnIndex);
                Pump(ex.ClickAsync(i => IsColumn(i, "id") && ReferenceEquals(i.Item, Table(form, "a").Columns[0])));
                Assert.Same(tabA, form.ActiveTab);
                grid.Columns[1].Visible = false;
                Get<HashSet<int>>(form, "_hiddenColumns").Add(1);
                var before = grid.CurrentCell;
                Pump(ex.ClickAsync(i => IsColumn(i, "name")));
                Assert.Same(tabA, form.ActiveTab);
                Assert.False(grid.Columns[1].Visible);
                Assert.Same(before, grid.CurrentCell);
                Assert.Contains("name", Status(form));
                Assert.NotEqual("", Status(form));                          // 안내 문구(언어 설정에 따라 영어·한국어)

                // 아직 열지 않은 표의 컬럼: 열고 나서 고른다.
                Assert.True(form.CloseTab(tabB, askUnsaved: false));
                PumpUntil(() => tabB.DisposeCompletion.IsCompleted, "tab resources released");
                Pump(ex.ClickAsync(i => IsColumn(i, "city")));
                Assert.Equal(Path.GetFullPath(b), form.ActiveTab!.Path);
                Assert.Equal(1, GridOf(form).CurrentCell.ColumnIndex);
            });
        }

        [Fact]
        public void Arrow_keys_and_programmatic_selection_only_move_the_selection_and_never_open_anything()
        {
            string a = MakeCsv("a.csv", CsvA);
            string b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                Await(form.AddSourcesAsync(new[] { a, b }, NoCancel));
                var ex = ShowExplorer(form);
                Assert.True(ex.Select(i => IsTable(i, "a")));
                string first = Selected(ex);

                var handle = ex.TreeHandle;
                SendMessage(handle, WmKeyDown, (IntPtr)VkDown, IntPtr.Zero);
                SendMessage(handle, WmKeyUp, (IntPtr)VkDown, IntPtr.Zero);
                Assert.NotEqual(first, Selected(ex));                       // 키가 트리에 닿아 선택이 옮겨 갔다
                SendMessage(handle, WmKeyDown, (IntPtr)VkDown, IntPtr.Zero);
                SendMessage(handle, WmKeyDown, (IntPtr)VkUp, IntPtr.Zero);
                Pump(Task.Delay(50));

                Assert.Empty(form.Tabs);
                Assert.Equal(0, form.ExplorerClickCount);
                Assert.False(form.WorkspaceOperationRunning);

                // Enter는 연다.
                Assert.True(ex.Select(i => IsTable(i, "b")));
                SendMessage(handle, WmKeyDown, (IntPtr)0x0D, IntPtr.Zero);
                PumpUntil(() => form.Tabs.Count == 1 && !form.ExplorerClickRunning, "Enter opens the table");
                Assert.Equal(Path.GetFullPath(b), form.ActiveTab!.Path);
            });
        }

        [Fact]
        public void Switching_tabs_or_sheets_selects_the_matching_node_without_clicking_it()
        {
            string a = MakeCsv("a.csv", CsvA);
            string b = MakeCsv("b.csv", CsvB);
            string c = MakeCsv("c.csv", "x\n1\n");
            string db = MakeDb("book.db");
            OnForm(form =>
            {
                var tabA = Open(form, a);
                var tabB = Open(form, b);
                var tabC = Open(form, c);
                var tabDb = Open(form, db);
                Await(form.AddSourcesAsync(new[] { a, b, db }, NoCancel));
                var result = Await(form.OpenQueryResultTabAsync("SELECT 1 AS x", "Query 1", NoCancel));
                WaitIdle(form, result);
                var ex = ShowExplorer(form);
                int clicks = form.ExplorerClickCount;
                string Key(string table) => "tbl:" + Table(form, table).SqlReference;

                form.ActivateTab(tabA);
                Assert.Equal(Key("a"), Selected(ex));
                form.ActivateTab(tabB);
                Assert.Equal(Key("b"), Selected(ex));
                form.ActivateTab(tabC);
                Assert.Equal("tab:" + tabC.Id, Selected(ex));               // 올리지 않은 파일은 자기 탭 노드
                form.ActivateTab(result);
                Assert.Equal("res:" + result.Id, Selected(ex));
                form.ActivateTab(tabDb);
                Assert.Equal(Key("alpha"), Selected(ex));
                Pump(form.SwitchSheetAsync(1));                             // 시트 전환(탭은 그대로)도 따라간다
                Assert.Equal(Key("beta"), Selected(ex));
                Pump(form.SwitchSheetAsync(0));
                Assert.Equal(Key("alpha"), Selected(ex));

                // 탭 → 트리는 클릭이 아니다: 아무 클릭 동작도, 열린 탭 변화도 없다.
                Assert.Equal(clicks, form.ExplorerClickCount);
                Assert.Equal(5, form.Tabs.Count);

                // 사용자가 다른 노드를 눌러 탭이 바뀌어도 되돌아 흔들리지 않는다(되먹임 없음).
                int changes = 0;
                form.TabsChanged += () => changes++;
                Pump(ex.ClickAsync(i => IsTable(i, "b")));
                Assert.Same(tabB, form.ActiveTab);
                Assert.Equal(Key("b"), Selected(ex));
                Assert.Equal(1, changes);
                Assert.Equal(clicks + 1, form.ExplorerClickCount);

                // 탭이 닫히고 트리가 다시 만들어진 뒤에도 선택은 활성 탭의 노드다.
                Assert.True(form.CloseTab(tabC, askUnsaved: false));
                form.ActivateTab(tabA);
                ex.RefreshTree();
                Assert.Equal(Key("a"), Selected(ex));
            });
        }

        [Fact]
        public void A_newer_click_supersedes_one_still_in_progress_and_the_stale_result_never_activates()
        {
            string b = MakeCsv("b.csv", CsvB);
            string db = MakeDb("book.db");
            OnForm(form =>
            {
                Await(form.AddSourcesAsync(new[] { db, b }, NoCancel));
                var ex = ShowExplorer(form);
                Assert.Empty(form.Tabs);

                // 워크북 가져오기는 취소할 수 없다: 뒤 클릭이 오면 그 결과(탭은 열리더라도)는 활성화하지 않는다.
                var first = ex.ClickAsync(i => IsTable(i, "alpha"));
                Assert.False(first.IsCompleted);
                var second = ex.ClickAsync(i => IsTable(i, "b"));
                Pump(Task.WhenAll(first, second));
                Assert.Equal(Path.GetFullPath(b), form.ActiveTab!.Path);
                Assert.Equal(2, form.ExplorerClickCount);
                Assert.False(form.WorkspaceOperationRunning);
                Assert.DoesNotContain(form.Tabs, t => t.IsActive && t.Kind == TabKind.Sheet);

                // 취소할 수 있는 뷰 계산은 취소된다(뷰 탭이 생기지 않는다).
                var view = form.Workspace!.CreateView("heavy", "SELECT sum(sqrt(i * j)) AS s FROM range(200000) x(i), range(200000) y(j)");
                ex.RefreshTree();
                var slow = ex.ClickAsync(i => i.Kind == WorkspaceExplorer.NodeKind.View && ReferenceEquals(i.Item, view));
                Assert.False(slow.IsCompleted);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var other = ex.ClickAsync(i => IsTable(i, "alpha"));
                Pump(Task.WhenAll(slow, other));
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the heavy view was not cancelled");
                Assert.Null(form.FindViewTab(view));
                Assert.Equal(TabKind.Sheet, form.ActiveTab!.Kind);          // 뒤 클릭(시트)의 탭이 남는다
                Assert.False(form.WorkspaceOperationRunning);
            });
        }

        [Fact]
        public void Double_click_is_the_same_as_one_click_and_never_opens_a_table_twice()
        {
            string db = MakeDb("book.db");
            OnForm(form =>
            {
                Await(form.AddSourcesAsync(new[] { db }, NoCancel));
                var ex = ShowExplorer(form);

                var first = ex.ClickAsync(i => IsTable(i, "beta"));
                var second = ex.ClickAsync(i => IsTable(i, "beta"));        // 더블클릭의 둘째 알림
                Pump(Task.WhenAll(first, second));
                var tab = Assert.Single(form.Tabs);
                Assert.Same(tab, form.ActiveTab);
                WaitIdle(form, tab);
                Assert.Equal("beta", tab.SheetName);
                Assert.Equal(1, form.ExplorerClickCount);                  // 둘째는 첫째에 합류했다

                Pump(ex.ClickAsync(i => IsTable(i, "beta")));               // 끝난 뒤의 클릭은 활성 탭이라 아무 일도 없다
                Assert.Single(form.Tabs);
                Assert.False(form.WorkspaceOperationRunning);
            });
        }
    }
}
