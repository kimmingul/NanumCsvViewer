using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 실제 Form1(보이지 않는 창)에서 다중 문서 탭: 탭마다 독립된 필터·정렬·숨김 열·편집, 전환 시 문서를 다시 열지 않음,
    // 같은 경로는 기존 탭 활성화, 닫기·종료 시 저장 안 한 편집 확인, 에이전트 도구는 활성 탭 대상, 읽기 전용 탭의 편집 거부.
    [Collection("SavedViewStore")]
    public class TabsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_tabs_" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _paths = new();
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        public TabsTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            // 탭 전환이 남긴 복구 저널이 사용자 폴더에 쌓이지 않게 정리
            foreach (string p in _paths)
                try { if (EditJournal.KeyFor(p) is { } key) EditJournal.Delete(EditJournal.DefaultDirectory, key); } catch { }
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string MakeCsv(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            _paths.Add(path);
            return path;
        }

        private static T Get<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;
        private static object? Invoke(Form1 f, string method, params object[] args) => typeof(Form1).GetMethod(method, Inst)!.Invoke(f, args);
        private static System.Windows.Forms.DataGridView GridOf(Form1 f) => f.Controls.Find("grid", true).OfType<System.Windows.Forms.DataGridView>().Single();
        private static string[] Headers(Form1 f) => GridOf(f).Columns.Cast<System.Windows.Forms.DataGridViewColumn>().Select(c => c.HeaderText).ToArray();

        private static void Pump(Task task)
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(60)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
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
            Assert.True(thread.Join(TimeSpan.FromSeconds(180)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // 파일을 새 탭으로 열고(활성화) 인덱싱이 끝날 때까지 기다린다.
        private static DocumentTab Open(Form1 f, string path)
        {
            var tab = Await(f.OpenFileTabAsync(path));
            Assert.NotNull(tab);
            WaitIdle(f, tab!);
            return tab!;
        }

        private static void WaitIdle(Form1 f, DocumentTab tab)
            => PumpUntil(() => tab.Document is { IndexingComplete: true } && !tab.IsIndexing && !Get<bool>(f, "_busy"), "tab indexing to finish");

        private static void Switch(Form1 f, DocumentTab tab)
        {
            Pump(f.ActivateTabAsync(tab));
            Assert.Same(tab, f.ActiveTab);
        }

        private const string CsvA = "id,name,score\n1,kim,30\n2,lee,10\n3,park,20\n4,choi,40\n";
        private const string CsvB = "x,y\n10,p\n20,q\n30,r\n";

        [Fact]
        public void Opening_files_adds_tabs_and_the_same_path_activates_the_existing_tab()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                int changes = 0;
                form.TabsChanged += () => changes++;
                Assert.Empty(form.Tabs);
                Assert.Null(form.ActiveTab);

                var tabA = Open(form, a);
                var tabB = Open(form, b);
                Assert.Equal(new[] { tabA, tabB }, form.Tabs);
                Assert.Same(tabB, form.ActiveTab);
                Assert.Equal(TabKind.File, tabA.Kind);
                Assert.Equal("a.csv", tabA.Title);
                Assert.Equal(Path.GetFullPath(a), tabA.Path);
                Assert.False(tabA.IsReadOnly);
                Assert.True(changes >= 4); // 추가×2 + 전환×2 이상

                // 같은 경로(대소문자 달라도)는 새 탭 없이 기존 탭을 활성화한다.
                var again = Await(form.OpenFileTabAsync(a.ToUpperInvariant().Replace(_dir.ToUpperInvariant(), _dir)));
                Assert.Same(tabA, again);
                Assert.Equal(2, form.Tabs.Count);
                Assert.Same(tabA, form.ActiveTab);
                Assert.Equal(new[] { "id", "name", "score" }, Headers(form));

                // 목록 순서를 끌어서 바꿀 수 있다.
                Invoke(form, "MoveTab", tabB, 0);
                Assert.Equal(new[] { tabB, tabA }, form.Tabs);
            });
        }

        [Fact]
        public void Tabs_keep_independent_filters_sort_hidden_columns_selection_and_edits_and_switching_does_not_reload()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                var grid = GridOf(form);
                var filterBox = Get<System.Windows.Forms.ToolStripTextBox>(form, "filterTextBox");

                var tabA = Open(form, a);
                var docA = tabA.Document!;
                filterBox.Text = "i";                                          // kim, choi
                Pump((Task)Invoke(form, "ApplyTextFilterAsync")!);
                Get<List<SortKey>>(form, "_sortKeys").Add(new SortKey(2, false)); // score ↓ → choi, kim
                Pump((Task)Invoke(form, "SortAsync")!);
                grid.Columns[0].Visible = false;
                Get<HashSet<int>>(form, "_hiddenColumns").Add(0);
                Invoke(form, "CommitCellEdit", 0, 1, "CHOI!");                    // 보이는 첫 행(choi)의 name 편집
                grid.CurrentCell = grid[1, 1];                                    // kim 행의 name
                Assert.Equal(2, docA.DisplayRowCount);
                Assert.Equal("CHOI!", docA.GetDisplayRow(0)[1]);
                Assert.True(tabA.HasUnsavedEdits);

                var tabB = Open(form, b);
                var docB = tabB.Document!;
                Assert.NotSame(docA, docB);
                // 새 탭은 깨끗하다: 필터·정렬·숨김·편집 없음, 그리드는 B의 컬럼과 행.
                Assert.Equal(new[] { "x", "y" }, Headers(form));
                Assert.Equal(3, grid.RowCount);
                Assert.Empty(Get<List<SortKey>>(form, "_sortKeys"));
                Assert.Empty(Get<HashSet<int>>(form, "_hiddenColumns"));
                Assert.Null(Get<Func<string[], bool>?>(form, "_textCondition"));
                Assert.Equal("", filterBox.Text);
                Assert.False(tabB.HasUnsavedEdits);
                Assert.Same(docB, Get<VirtualCsvDocument>(form, "_doc"));

                Get<List<SortKey>>(form, "_sortKeys").Add(new SortKey(0, false));
                Pump((Task)Invoke(form, "SortAsync")!);
                Assert.Equal("30", docB.GetDisplayRow(0)[0]);

                // A로 돌아오면 A의 상태가 그대로이고(같은 문서 인스턴스·같은 덮개) B의 정렬은 B에 남는다.
                Switch(form, tabA);
                Assert.Same(docA, Get<VirtualCsvDocument>(form, "_doc"));
                Assert.Same(docA, tabA.Document);
                Assert.True(docA.IndexingComplete);
                Assert.Equal(new[] { "id", "name", "score" }, Headers(form));
                Assert.Equal(2, docA.DisplayRowCount);
                Assert.Equal(2, grid.RowCount);
                Assert.Equal("CHOI!", docA.GetDisplayRow(0)[1]);
                Assert.True(tabA.HasUnsavedEdits);
                Assert.Equal(new[] { new SortKey(2, false) }, Get<List<SortKey>>(form, "_sortKeys"));
                Assert.Equal(new[] { 0 }, Get<HashSet<int>>(form, "_hiddenColumns").ToArray());
                Assert.False(grid.Columns[0].Visible);
                Assert.Equal("i", filterBox.Text);
                Assert.NotNull(Get<Func<string[], bool>?>(form, "_textCondition"));
                Assert.Equal((1, 1), (grid.CurrentCell!.RowIndex, grid.CurrentCell.ColumnIndex));

                Switch(form, tabB);
                Assert.Same(docB, tabB.Document);
                Assert.Equal("30", docB.GetDisplayRow(0)[0]);
                Assert.Equal(new[] { new SortKey(0, false) }, Get<List<SortKey>>(form, "_sortKeys"));
                Assert.Empty(Get<HashSet<int>>(form, "_hiddenColumns"));
                Assert.False(tabB.HasUnsavedEdits);
                Assert.True(tabA.HasUnsavedEdits); // 비활성 탭의 미저장 편집도 보인다
            });
        }

        [Fact]
        public void Conditional_format_rules_and_history_are_per_tab()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                var tabA = Open(form, a);
                var rule = new ConditionalFormatRule(Guid.NewGuid().ToString("N"), "big scores", true, ConditionalFormatKind.Expression, "score > 15",
                    ConditionalFormatTarget.Cell, "score", "yellow", null, false, null, null, null);
                Invoke(form, "SetFormatRules", new object[] { new[] { rule }.AsEnumerable(), false, "test" });
                Assert.Single(Get<List<ConditionalFormatRule>>(form, "_cfRules"));
                Assert.True(Get<ConditionalFormatHistory>(form, "_cfHistory").CanUndo);

                var tabB = Open(form, b);
                Assert.Empty(Get<List<ConditionalFormatRule>>(form, "_cfRules"));
                Assert.False(Get<ConditionalFormatHistory>(form, "_cfHistory").CanUndo);

                Switch(form, tabA);
                Assert.Single(Get<List<ConditionalFormatRule>>(form, "_cfRules"));
                Assert.True(Get<ConditionalFormatHistory>(form, "_cfHistory").CanUndo);
            });
        }

        [Fact]
        public void Closing_a_tab_with_unsaved_edits_asks_and_respects_the_answer()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB), c = MakeCsv("c.csv", "k\n1\n2\n");
            OnForm(form =>
            {
                var tabA = Open(form, a);
                var tabB = Open(form, b);
                var tabC = Open(form, c);
                Switch(form, tabA);
                Invoke(form, "CommitCellEdit", 0, 1, "EDITED");
                Assert.True(tabA.HasUnsavedEdits);

                var asked = new List<IReadOnlyList<DocumentTab>>();
                bool answer = false;
                form.UnsavedTabsConfirm = list => { asked.Add(list); return answer; };

                // 아니오: 탭이 그대로 열려 있고 편집도 남는다.
                Assert.False(form.CloseTab(tabA, askUnsaved: true));
                Assert.Equal(new[] { tabA, tabB, tabC }, form.Tabs);
                Assert.True(tabA.HasUnsavedEdits);
                Assert.Equal(new[] { tabA }, Assert.Single(asked));

                // 깨끗한 탭은 묻지 않고 닫힌다.
                Assert.True(form.CloseTab(tabC, askUnsaved: true));
                Assert.Single(asked);
                Assert.Equal(new[] { tabA, tabB }, form.Tabs);
                Pump(tabC.DisposeCompletion);
                Assert.True(tabC.IsClosed);

                // 예: 활성 탭이 닫히고 오른쪽 이웃이 활성화된다. 문서는 해제된다.
                answer = true;
                var docA = tabA.Document!;
                Assert.True(form.CloseTab(tabA, askUnsaved: true));
                Assert.Equal(2, asked.Count);
                Assert.Equal(new[] { tabB }, form.Tabs);
                Assert.Same(tabB, form.ActiveTab);
                Assert.Same(Get<VirtualCsvDocument>(form, "_doc"), tabB.Document);
                Assert.Equal(new[] { "x", "y" }, Headers(form));
                Pump(tabA.DisposeCompletion);
                Assert.Null(tabA.Document);
                Assert.NotNull(docA);

                // 마지막 탭을 닫으면 빈 화면.
                Assert.True(form.CloseTab(tabB, askUnsaved: false));
                Assert.Empty(form.Tabs);
                Assert.Null(form.ActiveTab);
                Assert.Null(Get<VirtualCsvDocument?>(form, "_doc"));
                Assert.Empty(GridOf(form).Columns);
            });
        }

        [Fact]
        public void Closing_the_window_asks_once_listing_every_dirty_tab()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB), c = MakeCsv("c.csv", "k\n1\n2\n");
            OnForm(form =>
            {
                var tabA = Open(form, a);
                Invoke(form, "CommitCellEdit", 0, 1, "A!");
                var tabB = Open(form, b);
                Invoke(form, "CommitCellEdit", 1, 1, "B!");
                var tabC = Open(form, c);
                Assert.True(tabA.HasUnsavedEdits && tabB.HasUnsavedEdits && !tabC.HasUnsavedEdits);

                var asked = new List<DocumentTab[]>();
                form.UnsavedTabsConfirm = list => { asked.Add(list.ToArray()); return false; };
                form.Close(); // 취소(아니오) → 창은 그대로 열려 있다
                Assert.False(form.IsDisposed);
                Assert.Equal(new[] { tabA, tabB }, Assert.Single(asked));
                Assert.Equal(3, form.Tabs.Count);

                // 그 사이 백그라운드 탭에 가려진 편집도 전환 뒤 그대로
                Switch(form, tabA);
                Assert.Equal("A!", tabA.Document!.GetDisplayRow(0)[1]);
                Switch(form, tabB);
                Assert.Equal("B!", tabB.Document!.GetDisplayRow(1)[1]);
            });
        }

        [Fact]
        public void Agent_host_and_tools_act_on_the_active_tab()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                var tabA = Open(form, a);
                var tabB = Open(form, b);
                var host = (ICsvAgentHost)form;
                var tools = new CsvHostTools(host, () => new AgentHostOptions(Language: "en", DataPolicy: AgentDataPolicy.SummaryOnly, MaxRowsPerRequest: 200, AllowLocalPython: false));
                HostToolResult Run(string tool, string args)
                {
                    var call = new HostToolCall("host_1", "call_1", tool, JsonDocument.Parse(args).RootElement.Clone());
                    var task = tools.ExecuteAsync(call, new AlwaysApprove(), CancellationToken.None);
                    Pump(task);
                    return task.Result;
                }

                Assert.Equal("b.csv", host.GetInfo()!.FileName);
                Assert.Equal(3, host.GetInfo()!.TotalRows);
                var edit = Run("csv.edit_cells", """{"edits":[{"row":2,"column":"y","value":"AI"}]}""");
                Assert.False(edit.IsError, edit.Text);
                Assert.Equal("AI", tabB.Document!.GetDataRow(1)[1]);
                Assert.True(tabA.Document!.Edits.IsEmpty);

                Switch(form, tabA);
                Assert.Equal("a.csv", host.GetInfo()!.FileName);
                Assert.Equal(4, host.GetInfo()!.TotalRows);
                var info = Run("csv.info", "{}");
                Assert.False(info.IsError, info.Text);
                Assert.Contains("a.csv", info.Text);
                Assert.DoesNotContain("b.csv", info.Text);
                var edit2 = Run("csv.edit_cells", """{"edits":[{"row":1,"column":"name","value":"AI-A"}]}""");
                Assert.False(edit2.IsError, edit2.Text);
                Assert.Equal("AI-A", tabA.Document!.GetDataRow(0)[1]);
                Assert.Equal("AI", tabB.Document!.GetDataRow(1)[1]);
                Assert.Equal("q", tabB.Document!.GetOriginalRow(1)[1]);
            });
        }

        private sealed class AlwaysApprove : IAgentApprovals
        {
            public Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation, ApprovalKind kind = ApprovalKind.RowSharing)
                => Task.FromResult(true);
        }

        [Fact]
        public void A_generated_read_only_tab_is_listed_activated_and_refuses_ui_edits()
        {
            string v = MakeCsv("view.csv", CsvA);
            string a = MakeCsv("a.csv", CsvB);
            OnForm(form =>
            {
                var plain = Open(form, a);
                var view = form.OpenGeneratedTab(v, "Top scores", TabKind.View, "top_scores", readOnly: true);
                WaitIdle(form, view);
                Assert.Same(view, form.ActiveTab);
                Assert.Equal(new[] { plain, view }, form.Tabs);
                Assert.True(view.IsReadOnly);
                Assert.Equal(TabKind.View, view.Kind);
                Assert.Equal("top_scores", view.ViewName);
                Assert.Equal("Top scores", view.DisplayName);
                Assert.Equal(Path.GetFullPath(v), view.Path);
                var doc = view.Document!;
                Assert.Equal(4, doc.DataRowsAvailable);

                // 셀 편집·시트 편집 모드는 거부되고 덮개는 비어 있다.
                Invoke(form, "CommitCellEdit", 0, 1, "nope");
                Assert.True(doc.Edits.IsEmpty);
                Invoke(form, "SetSheetEditing", true, false);
                Assert.False(Get<bool>(form, "_sheetEditing"));
                Assert.False((bool)typeof(Form1).GetProperty("EditsReady", Inst)!.GetValue(form)!);
                Assert.False(view.HasUnsavedEdits);

                // 일반 탭으로 돌아가면 편집 준비 상태가 된다.
                Switch(form, plain);
                Assert.True((bool)typeof(Form1).GetProperty("EditsReady", Inst)!.GetValue(form)!);
            });
        }

        [Fact]
        public void Read_only_tab_blocks_agent_structure_edits_and_plain_tabs_stay_editable()
        {
            string v = MakeCsv("view.csv", CsvA);
            string a = MakeCsv("a.csv", CsvB);
            OnForm(form =>
            {
                var plain = Open(form, a);
                var view = form.OpenGeneratedTab(v, "Result 1", TabKind.Result, null, readOnly: true);
                WaitIdle(form, view);
                var doc = view.Document!;

                var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() => Invoke(form, "AgentInsertRows", 1L, 1, "AI: insert"));
                Assert.IsType<InvalidOperationException>(ex.InnerException);
                Assert.False(string.IsNullOrWhiteSpace(ex.InnerException!.Message));
                Assert.Throws<InvalidOperationException>(() => form.RequireEditableTab());
                Assert.True(doc.Edits.IsEmpty);
                Assert.Equal(4, doc.DataRowsAvailable);

                // 같은 폼의 일반 탭은 그대로 편집된다.
                Switch(form, plain);
                form.RequireEditableTab();
                Invoke(form, "CommitCellEdit", 0, 1, "ok");
                Assert.True(plain.HasUnsavedEdits);
                Assert.True(view.Document!.Edits.IsEmpty);
            });
        }

        [Fact]
        public void A_tab_that_finishes_indexing_in_the_background_is_finalized_when_activated()
        {
            var sb = new StringBuilder("id,name,score\n");
            for (int i = 1; i <= 300_000; i++) sb.Append(i).Append(",n").Append(i % 97).Append(',').Append(i % 1000).Append('\n');
            string big = MakeCsv("big.csv", sb.ToString());
            string small = MakeCsv("small.csv", CsvB);
            OnForm(form =>
            {
                var bigTab = Await(form.OpenFileTabAsync(big))!;
                var smallTab = Open(form, small);    // 곧바로 다른 탭으로: 큰 파일은 백그라운드에서 계속 인덱싱한다
                Assert.Same(smallTab, form.ActiveTab);
                PumpUntil(() => bigTab.Document is { IndexingComplete: true } && !bigTab.IsIndexing, "background indexing");
                Assert.Equal(300_000, bigTab.Document!.DataRowsAvailable);
                Assert.Equal(3, bigTab.InferredColumnTypes.Count);   // 활성화 전에도 컬럼 타입을 알 수 있다
                Assert.DoesNotContain(ColumnValueType.Empty, bigTab.InferredColumnTypes);

                Switch(form, bigTab);
                var grid = GridOf(form);
                PumpUntil(() => grid.RowCount == 300_000 && !Get<bool>(form, "_indexing"), "big tab grid");
                Assert.Equal(new[] { "id", "name", "score" }, Headers(form));
                Assert.Equal(3, Get<ColumnSummary[]>(form, "_columnSummaries").Length);
                Assert.False(bigTab.IsIndexing);
                Assert.Equal(-1, bigTab.IndexingPercent);
            });
        }

        [Fact]
        public void A_workbook_is_one_tab_with_sheet_switching_inside_and_reopening_activates_it()
        {
            string xlsx = Path.Combine(_dir, "book.xlsx");
            CreateTwoSheetXlsx(xlsx);
            string csv = MakeCsv("a.csv", CsvA);
            OnForm(form =>
            {
                var book = Await(form.OpenFileTabAsync(xlsx))!;
                WaitIdle(form, book);
                Assert.Equal(TabKind.Sheet, book.Kind);
                Assert.Equal("book.xlsx", book.Title);
                Assert.Equal("Alpha", book.SheetName);
                Assert.Equal("book.xlsx [Alpha]", book.DisplayName);
                Assert.Equal(new[] { "name", "age" }, Headers(form));

                var plain = Open(form, csv);
                Switch(form, book);
                Assert.Equal(new[] { "name", "age" }, Headers(form));
                Invoke(form, "SwitchSheet", 1);
                PumpUntil(() => book.SheetName == "Beta" && book.Document is { IndexingComplete: true } && !Get<bool>(form, "_busy") && !Get<bool>(form, "_indexing"), "sheet switch");
                Assert.Equal(new[] { "city" }, Headers(form));
                Assert.Equal("book.xlsx [Beta]", book.DisplayName);
                Assert.Equal(2, form.Tabs.Count);

                Switch(form, plain);
                var again = Await(form.OpenFileTabAsync(xlsx));
                Assert.Same(book, again);
                Assert.Same(book, form.ActiveTab);
                Assert.Equal(new[] { "city" }, Headers(form));   // 시트 선택도 탭에 남아 있다
                Assert.True(form.CloseTab(book, askUnsaved: false));
                Pump(book.DisposeCompletion);
            });
        }

        [Fact]
        public void Tab_strip_gestures_activate_close_with_middle_click_and_reorder_by_dragging()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB), c = MakeCsv("c.csv", "k\n1\n2\n");
            OnForm(form =>
            {
                form.Size = new System.Drawing.Size(900, 600);
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                var tabA = Open(form, a);
                var tabB = Open(form, b);
                var tabC = Open(form, c);
                var strip = Get<TabStrip>(form, "tabStrip");
                var shown = typeof(TabStrip).GetMethod("Shown", Inst)!;
                System.Drawing.Rectangle Rect(int i) => (System.Drawing.Rectangle)shown.Invoke(strip, new object[] { i })!;
                System.Drawing.Point Center(int i) => new(Rect(i).Left + 20, Rect(i).Top + Rect(i).Height / 2); // 닫기 단추 왼쪽
                void Mouse(string handler, System.Windows.Forms.MouseButtons button, System.Drawing.Point p)
                    => typeof(System.Windows.Forms.Control).GetMethod(handler, Inst)!.Invoke(strip, new object[] { new System.Windows.Forms.MouseEventArgs(button, 1, p.X, p.Y, 0) });

                // 왼쪽 클릭 = 활성화
                Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, Center(0));
                Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Left, Center(0));
                Assert.Same(tabA, form.ActiveTab);

                // 끌어서 순서 바꾸기: A를 C 뒤로
                var from = Center(0);
                Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, from);
                var to = new System.Drawing.Point(Rect(2).Right - 4, from.Y);
                Mouse("OnMouseMove", System.Windows.Forms.MouseButtons.Left, to);
                Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Left, to);
                Assert.Equal(new[] { tabB, tabC, tabA }, form.Tabs);
                Assert.Same(tabA, form.ActiveTab);

                // 가운데 클릭 = 닫기(깨끗한 탭은 묻지 않는다)
                Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Middle, Center(1));
                Assert.Equal(new[] { tabB, tabA }, form.Tabs);
                Assert.True(tabC.IsClosed);

                // 닫기 단추(×) 클릭, 저장 안 한 편집이 있으면 확인
                Invoke(form, "CommitCellEdit", 0, 1, "X");
                var asked = 0;
                form.UnsavedTabsConfirm = _ => { asked++; return false; };
                var close = new System.Drawing.Point(Rect(1).Right - 12, Rect(1).Top + Rect(1).Height / 2);
                Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, close);
                Assert.Equal(1, asked);
                Assert.Equal(new[] { tabB, tabA }, form.Tabs);
            });
        }


        [Fact]
        public void Switching_away_from_a_dirty_tab_writes_its_recovery_journal_and_discarding_removes_it()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB);
            OnForm(form =>
            {
                string keyA = EditJournal.KeyFor(a)!;
                EditJournal.Delete(EditJournal.DefaultDirectory, keyA);
                var tabA = Open(form, a);
                Invoke(form, "CommitCellEdit", 0, 1, "KEEP");
                Assert.False(EditJournal.Exists(EditJournal.DefaultDirectory, keyA)); // 타이머가 돌기 전
                Open(form, b);                                                         // 전환 = 동기 저널 기록
                Assert.True(EditJournal.Exists(EditJournal.DefaultDirectory, keyA));

                form.UnsavedTabsConfirm = _ => true;
                Assert.True(form.CloseTab(tabA, askUnsaved: true));                   // 버리기 확인 → 저널도 지운다
                Assert.False(EditJournal.Exists(EditJournal.DefaultDirectory, keyA));
            });
        }

        [Fact]
        public void Close_all_and_close_others_ask_once_for_the_dirty_tabs_and_close_nothing_on_no()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB), c = MakeCsv("c.csv", "k\n1\n2\n");
            OnForm(form =>
            {
                var tabA = Open(form, a);
                Invoke(form, "CommitCellEdit", 0, 1, "A!");
                var tabB = Open(form, b);
                Invoke(form, "CommitCellEdit", 0, 1, "B!");
                var tabC = Open(form, c);

                var asked = new List<DocumentTab[]>();
                bool answer = false;
                form.UnsavedTabsConfirm = list => { asked.Add(list.ToArray()); return answer; };

                Assert.False(form.CloseAllTabs(askUnsaved: true));
                Assert.Equal(new[] { tabA, tabB }, Assert.Single(asked));
                Assert.Equal(3, form.Tabs.Count);

                Assert.False((bool)Invoke(form, "CloseOtherTabs", tabC)!);          // C만 남기려면 A·B의 편집을 버려야 한다
                Assert.Equal(2, asked.Count);
                Assert.Equal(new[] { tabA, tabB }, asked[1]);
                Assert.Equal(3, form.Tabs.Count);

                answer = true;
                Assert.True((bool)Invoke(form, "CloseOtherTabs", tabC)!);
                Assert.Equal(new[] { tabC }, form.Tabs);
                Assert.Same(tabC, form.ActiveTab);
                Assert.True(tabA.IsClosed && tabB.IsClosed);

                Assert.True(form.CloseAllTabs(askUnsaved: true));                    // 깨끗하므로 더 묻지 않는다
                Assert.Equal(3, asked.Count);
                Assert.Empty(form.Tabs);
                Assert.Null(form.ActiveTab);
                Pump(Task.WhenAll(tabA.DisposeCompletion, tabB.DisposeCompletion, tabC.DisposeCompletion));
            });
        }

        [Fact]
        public void Opening_several_files_at_once_activates_only_the_last_and_skips_files_already_open()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB), c = MakeCsv("c.csv", "k\n1\n2\n");
            OnForm(form =>
            {
                var tabA = Open(form, a);
                int activations = 0;
                form.TabsChanged += () => { if (form.ActiveTab is not null && !ReferenceEquals(form.ActiveTab, tabA)) activations++; };
                Pump(form.OpenFilesAsync(new[] { b, a, c }));                          // a는 이미 열려 있다
                Assert.Equal(3, form.Tabs.Count);
                Assert.Equal(new[] { "a.csv", "b.csv", "c.csv" }, form.Tabs.Select(t => t.Title));
                Assert.Equal("c.csv", form.ActiveTab!.Title);
                Assert.Equal(new[] { "k" }, Headers(form));
                Assert.True(activations >= 1);
                Pump(form.OpenFilesAsync(new[] { Path.Combine(_dir, "missing.csv") }.Where(_ => false))); // 빈 목록은 아무것도 하지 않는다
                Assert.Equal(3, form.Tabs.Count);
            });
        }

        [Fact]
        public void On_a_shown_form_the_tab_strip_sits_under_the_toolbar_and_the_dock_host_beside_the_content()
        {
            string a = MakeCsv("a.csv", CsvA);
            OnForm(form =>
            {
                form.Size = new System.Drawing.Size(900, 600);
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                var menu = Get<System.Windows.Forms.MenuStrip>(form, "menuStrip1");
                var tool = Get<System.Windows.Forms.ToolStrip>(form, "toolStrip1");
                var status = Get<System.Windows.Forms.StatusStrip>(form, "statusStrip1");
                var strip = Get<TabStrip>(form, "tabStrip");
                var grid = GridOf(form);

                var tab = Open(form, a);
                System.Windows.Forms.Application.DoEvents();
                Assert.True(strip.Visible);
                Assert.Equal(0, menu.Top);
                Assert.True(tool.Top >= menu.Bottom);
                Assert.True(strip.Top >= tool.Bottom, $"tab strip {strip.Top} must be below toolbar bottom {tool.Bottom}");
                Assert.Equal(form.ClientSize.Width, strip.Width);
                Assert.True(grid.PointToScreen(System.Drawing.Point.Empty).Y > strip.PointToScreen(System.Drawing.Point.Empty).Y + strip.Height - 1);
                Assert.Equal(status.Width, form.ClientSize.Width);

                // 왼쪽 도킹 영역을 펼치면 상태바는 전체 폭을 유지하고 본문이 오른쪽으로 밀린다.
                int gridLeftBefore = grid.PointToScreen(System.Drawing.Point.Empty).X;
                form.WorkspaceDockVisible = true;
                System.Windows.Forms.Application.DoEvents();
                Assert.True(form.WorkspaceDockHost.Visible);
                Assert.Equal(form.ClientSize.Width, status.Width);
                Assert.Equal(form.ClientSize.Width, strip.Width);
                Assert.True(form.WorkspaceDockHost.Top >= strip.Bottom);
                Assert.True(grid.PointToScreen(System.Drawing.Point.Empty).X >= gridLeftBefore + form.WorkspaceDockHost.Width);
                form.WorkspaceDockVisible = false;
                Assert.False(tab.IsClosed);
            });
        }

        [Fact]
        public void Cycling_tabs_wraps_and_the_dock_host_is_available_but_collapsed()
        {
            string a = MakeCsv("a.csv", CsvA), b = MakeCsv("b.csv", CsvB), c = MakeCsv("c.csv", "k\n1\n");
            OnForm(form =>
            {
                Assert.NotNull(form.WorkspaceDockHost);
                Assert.False(form.WorkspaceDockVisible);
                Assert.Empty(form.WorkspaceDockHost.Controls);
                form.WorkspaceDockVisible = true;
                Assert.True(form.WorkspaceDockVisible);
                form.WorkspaceDockVisible = false;

                var tabA = Open(form, a);
                var tabB = Open(form, b);
                var tabC = Open(form, c);
                var strip = Get<TabStrip>(form, "tabStrip");
                Assert.Equal(3, strip.Tabs.Count);
                Assert.Equal(new[] { tabA, tabB, tabC }, strip.Tabs);
                Assert.Same(tabC, strip.Active);

                Invoke(form, "CycleTab", +1);
                Assert.Same(tabA, form.ActiveTab);
                Invoke(form, "CycleTab", -1);
                Assert.Same(tabC, form.ActiveTab);
                Assert.Same(tabC, strip.Active);
            });
        }

        // 최소 OOXML(xlsx) 두 시트짜리 워크북.
        private static void CreateTwoSheetXlsx(string path)
        {
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            void W(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                using var s = entry.Open();
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
            const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            W("[Content_Types].xml", Xml +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");
            W("_rels/.rels", Xml +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            W("xl/workbook.xml", Xml +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Alpha\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Beta\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            W("xl/_rels/workbook.xml.rels", Xml +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                "</Relationships>");
            W("xl/worksheets/sheet1.xml", Xml +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>name</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>age</t></is></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Kim</t></is></c><c r=\"B2\"><v>30</v></c></row>" +
                "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Lee</t></is></c><c r=\"B3\"><v>25</v></c></row>" +
                "</sheetData></worksheet>");
            W("xl/worksheets/sheet2.xml", Xml +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>city</t></is></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Seoul</t></is></c></row>" +
                "</sheetData></worksheet>");
        }
    }
}
