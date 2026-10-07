using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// 작업 공간 파일(.ncvws)의 패널 배치: 저장·복원, 옛 파일, 저장 확인과의 관계, 그리고 패널 표시 API(Form1)가 실제로 화면을 바꾸는지.
    /// </summary>
    [Collection("SavedViewStore")]
    public class WorkspaceLayoutTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_wslayout_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        static WorkspaceLayoutTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public WorkspaceLayoutTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private const string CsvA = "id,name,score\n1,kim,30\n2,lee,10\n3,park,20\n";

        private string MakeCsv(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        // ---- 파일 형식(순수 함수) ----------------------------------------------------------------------------

        private static PanelLayout Custom() => new()
        {
            Agent = false, Detail = true, Facets = true, Explorer = true, Findings = false, CellBar = false,
            DetailWidth = 410, ExplorerWidth = 300, AgentWidth = 520,
        };

        private static WorkspaceFileModel Capture(string ws, PanelLayout? layout) =>
            WorkspaceFile.Capture(ws, Array.Empty<WorkspaceCaptureSource>(), Array.Empty<WorkspaceFileView>(), Array.Empty<WorkspaceFileView>(),
                Array.Empty<WorkspaceCaptureTab>(), -1, layout?.Explorer ?? false, null, layout);

        [Fact]
        public void The_layout_round_trips_through_the_file()
        {
            string ws = Path.Combine(_dir, "a.ncvws");
            WorkspaceFile.Save(ws, Capture(ws, Custom()));

            var loaded = WorkspaceFile.Load(ws);
            Assert.Equal(WorkspaceFile.CurrentVersion, loaded.Version);
            var l = Assert.IsType<PanelLayout>(loaded.Layout);
            Assert.Equal((false, true, true, true, false, false), (l.Agent, l.Detail, l.Facets, l.Explorer, l.Findings, l.CellBar));
            Assert.Equal((410, 300, 520), (l.DetailWidth, l.ExplorerWidth, l.AgentWidth));
        }

        [Fact]
        public void The_layout_stays_inside_format_version_2_so_older_readers_still_open_the_file()
        {
            Assert.Equal(2, WorkspaceFile.CurrentVersion);
            string ws = Path.Combine(_dir, "v2.ncvws");
            WorkspaceFile.Save(ws, Capture(ws, Custom()));
            Assert.Contains("\"version\": 2", File.ReadAllText(ws));
            Assert.Contains("\"layout\"", File.ReadAllText(ws));

            var parsed = WorkspaceFile.Parse("""{ "format": "ncvws", "version": 2, "layout": { "agent": false, "detail": true, "detailWidth": 400 } }""");
            Assert.Equal(2, parsed.Version);
            Assert.True(parsed.Layout!.Detail);
            Assert.False(parsed.Layout.Agent);
            Assert.Equal(400, parsed.Layout.DetailWidth);
        }

        [Fact]
        public void Files_written_before_the_layout_section_load_without_one()
        {
            var v1 = WorkspaceFile.Parse("""{ "format": "ncvws", "version": 1, "tabs": [], "activeTab": -1, "explorerVisible": true }""");
            var v2 = WorkspaceFile.Parse("""{ "format": "ncvws", "version": 2, "tabs": [], "activeTab": -1, "explorerVisible": false }""");
            Assert.Null(v1.Layout);
            Assert.Null(v2.Layout);
            Assert.True(v1.ExplorerVisible);
        }

        [Fact]
        public void Hand_edited_widths_are_pulled_into_range_and_zero_means_unspecified()
        {
            var m = WorkspaceFile.Parse("""{ "format": "ncvws", "version": 2, "layout": { "detailWidth": 99999, "explorerWidth": 5, "agentWidth": -40, "detail": true } }""");
            Assert.Equal(PanelLayout.MaxWidth, m.Layout!.DetailWidth);
            Assert.Equal(PanelLayout.MinWidth, m.Layout.ExplorerWidth);
            Assert.Equal(0, m.Layout.AgentWidth);
            Assert.True(m.Layout.Detail);
            Assert.True(m.Layout.Agent);       // 적히지 않은 값은 레이아웃의 기본값
            Assert.True(m.Layout.CellBar);
        }

        [Fact]
        public void Layout_changes_are_not_unsaved_workspace_changes()
        {
            string ws = Path.Combine(_dir, "b.ncvws");
            Assert.Equal(WorkspaceFile.Signature(Capture(ws, new PanelLayout())), WorkspaceFile.Signature(Capture(ws, Custom())));
        }

        [Fact]
        public void Layouts_match_when_panels_are_the_same_and_visible_widths_are_within_rounding()
        {
            var a = Custom();
            var b = Custom();
            b.DetailWidth += 2;                       // DPI 반올림 오차
            Assert.True(Form1.LayoutsMatch(a, b));
            b.DetailWidth += 20;                      // 사용자가 폭을 바꿨다
            Assert.False(Form1.LayoutsMatch(a, b));

            var hidden = Custom();
            hidden.Agent = false; hidden.AgentWidth = 900;   // 안 보이는 패널의 폭은 보지 않는다
            Assert.True(Form1.LayoutsMatch(a, hidden));

            var other = Custom();
            other.Facets = false;
            Assert.False(Form1.LayoutsMatch(a, other));
        }

        // ---- Form1 (보이지 않는 창) --------------------------------------------------------------------------

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

        private static void OnForm(AppSettings settings, Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Form1? form = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    form = new Form1(settings);
                    _ = form.Handle;
                    form.ClientSize = new Size(1200, 700);
                    form.LayoutReady = true;   // 보이지 않는 창에서는 시작 레이아웃이 적용되지 않으므로 적용된 것으로 본다(AI 패널은 쓰지 않는다)
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

        private static object? Call(Form1 f, string method, params object[] args) => typeof(Form1).GetMethod(method, Inst)!.Invoke(f, args);
        private static void SetField(Form1 f, string field, object? value) => typeof(Form1).GetField(field, Inst)!.SetValue(f, value);
        private static T GetField<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;

        private static void AssertLayout(PanelLayout want, PanelLayout got)
        {
            Assert.True(want.SameVisibility(got), $"visibility differs: want {Describe(want)} got {Describe(got)}");
            if (want.Detail && want.DetailWidth > 0) Assert.InRange(got.DetailWidth, want.DetailWidth - 2, want.DetailWidth + 2);
            if (want.Explorer && want.ExplorerWidth > 0) Assert.InRange(got.ExplorerWidth, want.ExplorerWidth - 2, want.ExplorerWidth + 2);
        }

        private static string Describe(PanelLayout l) => $"agent={l.Agent} detail={l.Detail} facets={l.Facets} explorer={l.Explorer} findings={l.Findings} cellbar={l.CellBar}";

        [Fact]
        public void Applying_a_layout_changes_the_screen_and_capturing_reads_it_back_and_every_change_is_announced_once()
        {
            OnForm(new AppSettings(), form =>
            {
                var events = new List<(PanelKind, bool)>();
                form.PanelVisibilityChanged += (k, v) => events.Add((k, v));

                var want = Custom();
                form.ApplyLayout(want);
                AssertLayout(want, form.CaptureLayout());
                Assert.True(form.IsPanelVisible(PanelKind.Detail));
                Assert.True(form.IsPanelVisible(PanelKind.Explorer));
                Assert.False(form.IsPanelVisible(PanelKind.CellBar));
                Assert.False(form.IsPanelVisible(PanelKind.Agent));

                Assert.Contains((PanelKind.Detail, true), events);
                Assert.Contains((PanelKind.Explorer, true), events);
                Assert.Contains((PanelKind.CellBar, false), events);
                Assert.Contains((PanelKind.Facets, true), events);

                // 같은 레이아웃을 다시 적용해도 알림은 더 나가지 않는다(바뀐 것이 없다).
                events.Clear();
                form.ApplyLayout(want);
                Assert.Empty(events);

                // 하나만 바꾸면 그것만 알린다.
                form.SetPanelVisible(PanelKind.Detail, false);
                Assert.Equal(new[] { (PanelKind.Detail, false) }, events);
                Assert.False(form.CaptureLayout().Detail);
            });
        }

        [Fact]
        public void The_facets_panel_that_is_switched_on_without_a_document_appears_when_one_opens()
        {
            string a = MakeCsv("a.csv", CsvA);
            // Control.Visible는 창이 보여야 의미가 있으므로 이 테스트만 투명한 창을 실제로 띄운다.
            OnForm(new AppSettings { StartupPanels = new PanelLayout { Agent = false } }, form =>
            {
                form.LayoutReady = false;                                     // OnShown이 시작 레이아웃을 적용하면 true가 된다
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                form.Show();
                PumpUntil(() => form.LayoutReady, "startup layout applied on show");
                form.SetPanelVisible(PanelKind.Facets, true);
                Assert.True(form.IsPanelVisible(PanelKind.Facets));          // 켜 둔 상태는 기억한다
                var panel = GetField<Control?>(form, "_facetsPanel");
                Assert.NotNull(panel);
                Assert.False(panel!.Visible);                                // 문서가 없으니 빈 패널을 보이지 않는다

                var tab = Await(form.OpenFileTabAsync(a));
                Assert.NotNull(tab);
                PumpUntil(() => tab!.Document is { IndexingComplete: true } && !tab.IsIndexing, "indexing");
                Assert.NotNull(form.CurrentDocument);
                PumpUntil(() => panel.Visible, "facets panel shown for the open document");

                form.SetPanelVisible(PanelKind.Facets, false);
                Assert.False(panel.Visible);
                Assert.False(form.IsPanelVisible(PanelKind.Facets));
            });
        }

        [Fact]
        public void Startup_layout_is_the_settings_choice_or_the_last_state_when_remembering()
        {
            var settings = new AppSettings();
            OnForm(settings, form =>
            {
                var d = form.StartupLayout();
                Assert.Equal((true, false, false, true, false, true), (d.Agent, d.Detail, d.Facets, d.Explorer, d.Findings, d.CellBar));   // AI·탐색기·셀 줄

                settings.StartupPanels = new PanelLayout { Agent = false, Detail = true };
                Assert.True(form.StartupLayout().Detail);
                Assert.False(form.StartupLayout().Agent);

                settings.RememberLastPanels = true;                          // 저장된 마지막 상태가 아직 없으면 설정을 쓴다
                Assert.True(form.StartupLayout().Detail);
                settings.LastPanels = new PanelLayout { Agent = false, Detail = false, Facets = true, DetailWidth = 500 };
                var last = form.StartupLayout();
                Assert.True(last.Facets);
                Assert.False(last.Detail);
                Assert.Equal(0, last.DetailWidth);                           // 폭은 앱 설정의 폭을 따른다
            });
        }

        [Fact]
        public void Saving_a_workspace_stores_the_layout_and_opening_it_restores_that_layout_over_whatever_is_showing()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "proj.ncvws");

            var saved = Custom();
            OnForm(new AppSettings(), form =>
            {
                Await(form.OpenFileTabAsync(a));
                form.ApplyLayout(saved);
                SetField(form, "_wfFilePath", ws);
                Assert.True((bool)Call(form, "SaveWorkspace", false)!);
            });

            var onDisk = WorkspaceFile.Load(ws);
            Assert.NotNull(onDisk.Layout);
            AssertLayout(saved, onDisk.Layout!);

            // 다른 배치로 떠 있는 창에서 연다: 저장된 배치로 바뀐다.
            OnForm(new AppSettings(), form =>
            {
                form.ApplyLayout(new PanelLayout { Agent = false, Detail = false, CellBar = true });
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                AssertLayout(saved, form.CaptureLayout());
            });
        }

        [Fact]
        public void A_workspace_without_a_layout_section_gets_the_app_defaults_not_the_previous_workspace_layout()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "old.ncvws");
            File.WriteAllText(ws, $$"""
                { "format": "ncvws", "version": 2,
                  "sources": [ { "kind": "csv", "name": "a", "path": "a.csv", "absolutePath": {{System.Text.Json.JsonSerializer.Serialize(a)}}, "hasHeader": true } ],
                  "views": [], "tabs": [], "activeTab": -1, "explorerVisible": false }
                """);
            var settings = new AppSettings { StartupPanels = new PanelLayout { Agent = false, Detail = true, CellBar = true } };
            OnForm(settings, form =>
            {
                form.ApplyLayout(Custom());                                   // 앞 작업 공간에서 쓰던 배치
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                AssertLayout(settings.StartupPanels, form.CaptureLayout());   // 앱 기본으로 돌아간다
            });
        }

        [Fact]
        public void An_old_file_that_had_the_explorer_open_still_opens_it()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "old2.ncvws");
            File.WriteAllText(ws, $$"""
                { "format": "ncvws", "version": 2,
                  "sources": [ { "kind": "csv", "name": "a", "path": "a.csv", "absolutePath": {{System.Text.Json.JsonSerializer.Serialize(a)}}, "hasHeader": true } ],
                  "views": [], "tabs": [], "activeTab": -1, "explorerVisible": true }
                """);
            OnForm(new AppSettings { StartupPanels = new PanelLayout { Agent = false } }, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                Assert.True(form.IsPanelVisible(PanelKind.Explorer));
            });
        }

        [Fact]
        public void Closing_saves_a_changed_layout_into_the_file_without_a_save_prompt_and_keeps_everything_else_in_it()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "close.ncvws");
            var first = new PanelLayout { Agent = false, Detail = true, DetailWidth = 380 };
            OnForm(new AppSettings(), form =>
            {
                Await(form.OpenFileTabAsync(a));
                form.ApplyLayout(first);
                SetField(form, "_wfFilePath", ws);
                Assert.True((bool)Call(form, "SaveWorkspace", false)!);
            });
            var before = WorkspaceFile.Load(ws);

            OnForm(new AppSettings(), form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                Assert.True(form.IsPanelVisible(PanelKind.Detail));
                form.SetPanelVisible(PanelKind.Detail, false);                // 사용자가 패널을 바꾼다
                form.SetPanelVisible(PanelKind.CellBar, false);

                Assert.True(form.ConfirmWorkspaceSaved());                    // 배치만 바뀐 것은 저장 확인을 띄우지 않는다(띄우면 이 호출이 멈춘다)
                Call(form, "SaveWorkspaceLayoutOnClose");
            });

            var after = WorkspaceFile.Load(ws);
            Assert.False(after.Layout!.Detail);
            Assert.False(after.Layout.CellBar);
            Assert.Equal(before.Sources.Count, after.Sources.Count);
            Assert.Equal(before.Tabs.Count, after.Tabs.Count);
            Assert.Equal(before.Version, after.Version);
        }

        [Fact]
        public void Closing_without_a_layout_change_does_not_rewrite_the_workspace_file()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "same.ncvws");
            OnForm(new AppSettings(), form =>
            {
                Await(form.OpenFileTabAsync(a));
                form.ApplyLayout(new PanelLayout { Agent = false, Detail = true, DetailWidth = 380 });
                SetField(form, "_wfFilePath", ws);
                Assert.True((bool)Call(form, "SaveWorkspace", false)!);
            });
            var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(ws, stamp);

            OnForm(new AppSettings(), form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                Call(form, "SaveWorkspaceLayoutOnClose");
            });
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(ws));
        }

        [Fact]
        public void Session_state_is_remembered_only_for_the_app_when_no_workspace_file_owns_the_layout()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "own.ncvws");
            File.WriteAllText(ws, """{ "format": "ncvws", "version": 2, "layout": { "agent": false, "detail": true } }""");

            var plain = new AppSettings();
            OnForm(plain, form =>
            {
                form.ApplyLayout(new PanelLayout { Agent = false, Facets = true });
                Call(form, "SaveSessionState");
            });
            Assert.True(plain.LastPanels!.Facets);
            Assert.Null(plain.LastWorkspace);

            var withWorkspace = new AppSettings { LastPanels = new PanelLayout { Agent = false, Explorer = true } };
            OnForm(withWorkspace, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                form.SetPanelVisible(PanelKind.Facets, true);
                Call(form, "SaveSessionState");
            });
            Assert.True(withWorkspace.LastPanels!.Explorer);              // 작업 공간의 배치로 앱의 마지막 상태를 덮어쓰지 않는다
            Assert.False(withWorkspace.LastPanels.Facets);
            Assert.Equal(Path.GetFullPath(ws), withWorkspace.LastWorkspace);
        }
    }
}
