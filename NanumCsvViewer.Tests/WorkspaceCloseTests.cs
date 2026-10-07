using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    /// <summary>파일 ▸ 작업 공간 닫기: 저장 확인(저장/저장 안 함/취소), 탭·원본·뷰 비우기, 앱 시작 배치로 복귀, 에이전트 컨텍스트 초기화.</summary>
    [Collection("SavedViewStore")]
    public class WorkspaceCloseTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_wsclose_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        private static readonly CancellationToken NoCancel = CancellationToken.None;

        static WorkspaceCloseTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public WorkspaceCloseTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private const string CsvA = "id,name,score\n1,kim,30\n2,lee,10\n3,park,20\n";

        private string MakeCsv(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private static void Pump(Task task)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(90)) { Application.DoEvents(); Thread.Sleep(1); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Assert.True(task.IsCompleted, "task did not complete");
            task.GetAwaiter().GetResult();
        }

        private static T Await<T>(Task<T> task) { Pump(task); return task.Result; }

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 30)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { Application.DoEvents(); Thread.Sleep(2); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
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
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    form = new Form1(settings);
                    _ = form.Handle;
                    form.ClientSize = new Size(1200, 700);
                    form.LayoutReady = true;
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

        private static DocumentTab Open(Form1 f, string path)
        {
            var tab = Await(f.OpenFileTabAsync(path));
            Assert.NotNull(tab);
            PumpUntil(() => tab!.Document is { IndexingComplete: true } && !tab.IsIndexing, "indexing");
            return tab!;
        }

        /// <summary>CSV 하나를 연 작업 공간을 저장해 둔 상태(패널은 custom)로 만든다.</summary>
        private void OpenSavedWorkspace(Form1 form, string ws, PanelLayout layout)
        {
            var tab = Open(form, MakeCsv("a.csv", CsvA));
            Await(form.RegisterTabAsync(tab, NoCancel));
            form.ApplyLayout(layout);
            SetField(form, "_wfFilePath", ws);
            Assert.True((bool)Call(form, "SaveWorkspace", false)!);
        }

        private static PanelLayout Custom() => new() { Agent = false, Detail = true, Facets = true, Explorer = true, Findings = false, CellBar = false };

        [Fact]
        public void Cancelling_the_save_prompt_leaves_the_workspace_untouched()
        {
            string ws = Path.Combine(_dir, "p.ncvws");
            OnForm(new AppSettings(), form =>
            {
                OpenSavedWorkspace(form, ws, Custom());
                form.SetWorkspaceNotes("unsaved note");
                int asked = 0;
                form.WorkspaceSavePrompt = _ => { asked++; return DialogResult.Cancel; };

                Assert.False(form.CloseWorkspace());

                Assert.Equal(1, asked);
                Assert.Equal(Path.GetFullPath(ws), form.WorkspaceFilePath);
                Assert.Single(form.Tabs);
                Assert.NotEmpty(form.Workspace!.Sources);
                Assert.Equal("unsaved note", form.WorkspaceNotes);
                Assert.True(form.IsPanelVisible(PanelKind.Detail));
                Assert.True(form.IsPanelVisible(PanelKind.Explorer));
                Assert.Null(WorkspaceFile.Load(ws).Agent?.Notes);
            });
        }

        [Fact]
        public void Saving_at_the_prompt_writes_the_changes_then_closes_and_clears_everything()
        {
            string ws = Path.Combine(_dir, "s.ncvws");
            var settings = new AppSettings { StartupPanels = new PanelLayout { Agent = true, Detail = false } };
            OnForm(settings, form =>
            {
                OpenSavedWorkspace(form, ws, Custom());
                form.SetWorkspaceNotes("keep me");
                form.WorkspaceSavePrompt = _ => DialogResult.Yes;

                Assert.True(form.CloseWorkspace());

                Assert.Equal("keep me", WorkspaceFile.Load(ws).Agent!.Notes);
                Assert.Null(form.WorkspaceFilePath);
                Assert.Empty(form.Tabs);
                Assert.Empty(form.Workspace!.Sources);
                Assert.Empty(form.Workspace.Views);
                Assert.Equal("", form.WorkspaceNotes);
                Assert.Equal("Nanum CSV Viewer", form.Text);
                Assert.False(form.CanCloseWorkspace());
                Assert.Contains(Path.GetFullPath(ws), settings.RecentWorkspaces!);
            });
        }

        [Fact]
        public void Declining_the_save_prompt_discards_the_changes_but_still_closes()
        {
            string ws = Path.Combine(_dir, "d.ncvws");
            OnForm(new AppSettings(), form =>
            {
                OpenSavedWorkspace(form, ws, Custom());
                form.SetWorkspaceNotes("throw away");
                form.WorkspaceSavePrompt = _ => DialogResult.No;

                Assert.True(form.CloseWorkspace());

                Assert.Null(WorkspaceFile.Load(ws).Agent?.Notes);
                Assert.Null(form.WorkspaceFilePath);
                Assert.Empty(form.Tabs);
                Assert.Empty(form.Workspace!.Sources);
            });
        }

        [Fact]
        public void Closing_without_changes_does_not_prompt_keeps_the_layout_in_the_file_and_returns_the_screen_to_the_app_layout()
        {
            string ws = Path.Combine(_dir, "l.ncvws");
            var startup = new PanelLayout { Agent = false, Detail = false, CellBar = true };
            OnForm(new AppSettings { StartupPanels = startup }, form =>
            {
                OpenSavedWorkspace(form, ws, Custom());
                form.SetPanelVisible(PanelKind.Facets, false);               // 저장 뒤 패널만 바꿨다
                form.WorkspaceSavePrompt = _ => throw new Xunit.Sdk.XunitException("must not prompt for a layout-only change");

                Assert.True(form.CloseWorkspace());

                Assert.False(WorkspaceFile.Load(ws).Layout!.Facets);          // 앱 종료처럼 배치만 파일에 반영
                Assert.True(WorkspaceFile.Load(ws).Layout!.Detail);
                var now = form.CaptureLayout();
                Assert.True(startup.SameVisibility(now), $"layout is not the app startup layout: detail={now.Detail} explorer={now.Explorer} agent={now.Agent} cellbar={now.CellBar}");
            });
        }

        [Fact]
        public void Closing_resets_the_agent_workspace_context_and_keeps_a_not_yet_started_agent_deferred()
        {
            string ws = Path.Combine(_dir, "g.ncvws");
            OnForm(new AppSettings { StartupPanels = new PanelLayout { Agent = false } }, form =>   // 시작 배치에 AI가 켜져 있으면 닫을 때 진짜 패널(WebView2)이 만들어진다
            {
                OpenSavedWorkspace(form, ws, Custom());
                form.SetWorkspaceNotes("goal: churn");
                Assert.True((bool)Call(form, "SaveWorkspace", false)!);

                var factory = new FakeOmpFactory();
                using var controller = new ChatController(new FakePage(), new FakeTools(), new AgentHostOptions(Language: "en", AppVersion: "1.2.3"),
                    new ChatControllerServices
                    {
                        ProcessFactory = factory,
                        Clock = new ManualClock(),
                        Dialogs = new FakeDialogs(),
                        Log = new CapturingLog(),
                        Ui = SynchronizationContext.Current,
                        AutoTick = false,
                        DiscoverOmp = (_, _, _) => Task.FromResult(TestOmp.Result("C:\\fake\\omp.exe", "omp/18.4.4")),
                        ReadGuide = () => "# guide",
                        LocalPython = new FakePythonSetup(),
                        ReadModelUsage = _ => OmpModelUsage.Result.Fail(OmpModelUsage.Status.NoDatabase, "test"),
                    });
                controller.StartOnFirstUse(() => _dir);
                controller.SetWorkspaceContext((AgentWorkspaceContext)Call(form, "BuildAgentWorkspaceContext")!);
                SetField(form, "_agentController", controller);
                Assert.Equal(Path.GetFullPath(ws), Context(controller).WorkspaceFile);
                Assert.Equal("goal: churn", Context(controller).Notes);

                Assert.True(form.CloseWorkspace());

                var ctx = Context(controller);
                Assert.Null(form.WorkspaceFilePath);
                Assert.Null(ctx.WorkspaceFile);
                Assert.Empty(ctx.Tables);
                Assert.True(string.IsNullOrEmpty(ctx.Notes));
                Assert.True(controller.IsStartDeferred);                      // 첫 메시지 전에는 omp를 시작하지 않는다
                Assert.Empty(factory.Processes);
                SetField(form, "_agentController", null);
            });
        }

        private static AgentWorkspaceContext Context(ChatController c) =>
            (AgentWorkspaceContext)typeof(ChatController).GetField("_workspaceContext", Inst)!.GetValue(c)!;

        [Fact]
        public void The_menu_item_exists_has_no_shortcut_and_is_enabled_only_while_a_workspace_is_open()
        {
            string ws = Path.Combine(_dir, "m.ncvws");
            OnForm(new AppSettings(), form =>
            {
                var item = Walk(form.MenuStripForTests().Items).Single(i => (i.Tag as string) == "file.closeWorkspace");
                Assert.Equal(Keys.None, item.ShortcutKeys);
                Assert.Equal("", CommandShortcuts.Get("file.closeWorkspace").KeyText);
                Assert.False(form.CanCloseWorkspace());

                OpenSavedWorkspace(form, ws, Custom());
                Assert.True(form.CanCloseWorkspace());
                form.WorkspaceSavePrompt = _ => DialogResult.No;
                Assert.True(form.CloseWorkspace());
                Assert.False(form.CanCloseWorkspace());
                Assert.False(form.CloseWorkspace());                          // 열린 것이 없으면 아무 일도 하지 않는다
            });
        }

        private static IEnumerable<ToolStripMenuItem> Walk(ToolStripItemCollection items)
        {
            foreach (var i in items.OfType<ToolStripMenuItem>())
            {
                yield return i;
                foreach (var child in Walk(i.DropDownItems)) yield return child;
            }
        }
    }
}
