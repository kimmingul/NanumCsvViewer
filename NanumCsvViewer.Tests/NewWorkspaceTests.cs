using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    /// <summary>파일 ▸ 새 작업 공간…: 각 확인 단계에서 취소하면 아무것도 바뀌지 않고, 만들면 새 .ncvws가 바로 현재 작업 공간이 된다.</summary>
    [Collection("SavedViewStore")]
    public class NewWorkspaceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_wsnew_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        private static readonly CancellationToken NoCancel = CancellationToken.None;

        static NewWorkspaceTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public NewWorkspaceTests() => Directory.CreateDirectory(_dir);
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
        private DocumentTab OpenSavedWorkspace(Form1 form, string ws)
        {
            var tab = Open(form, MakeCsv("a.csv", CsvA));
            Await(form.RegisterTabAsync(tab, NoCancel));
            form.ApplyLayout(new PanelLayout { Agent = false, Detail = true, Facets = true, Explorer = true, Findings = false, CellBar = false });
            SetField(form, "_wfFilePath", ws);
            Assert.True((bool)Call(form, "SaveWorkspace", false)!);
            return tab;
        }

        private static NewWorkspaceRequest Req(string folder, string name, bool include = false) => new(folder, name, include);

        /// <summary>대화 상자 이음매: 주어진 값들을 차례로 돌려주고(마지막 뒤엔 취소) 호출 횟수를 센다.</summary>
        private sealed class Prompts
        {
            private readonly Queue<NewWorkspaceRequest?> _answers;
            public int Calls;
            public bool? OpenFilesAvailable;
            public NewWorkspaceRequest? FirstInitial;
            public Prompts(params NewWorkspaceRequest?[] answers) => _answers = new Queue<NewWorkspaceRequest?>(answers);
            public NewWorkspaceRequest? Next(NewWorkspaceRequest initial, bool openFiles)
            {
                Calls++;
                OpenFilesAvailable ??= openFiles;
                FirstInitial ??= initial;
                return _answers.Count > 0 ? _answers.Dequeue() : null;
            }
        }

        private static PanelLayout StartupOf(Form1 f) => f.StartupLayout();

        [Fact]
        public void Cancelling_the_save_prompt_stops_before_any_dialog_and_changes_nothing()
        {
            string ws = Path.Combine(_dir, "p.ncvws");
            OnForm(new AppSettings(), form =>
            {
                OpenSavedWorkspace(form, ws);
                form.SetWorkspaceNotes("unsaved note");
                var prompts = new Prompts(Req(_dir, "new"));
                form.NewWorkspacePrompt = prompts.Next;
                int asked = 0;
                form.WorkspaceSavePrompt = _ => { asked++; return DialogResult.Cancel; };

                Assert.False(Pump2(form.NewWorkspaceAsync()));

                Assert.Equal(1, asked);
                Assert.Equal(0, prompts.Calls);
                Assert.Equal(Path.GetFullPath(ws), form.WorkspaceFilePath);
                Assert.Single(form.Tabs);
                Assert.NotEmpty(form.Workspace!.Sources);
                Assert.Equal("unsaved note", form.WorkspaceNotes);
                Assert.False(File.Exists(Path.Combine(_dir, "new.ncvws")));
            });
        }

        [Fact]
        public void Cancelling_the_dialog_after_saving_at_the_prompt_keeps_the_saved_workspace_open()
        {
            string ws = Path.Combine(_dir, "q.ncvws");
            OnForm(new AppSettings(), form =>
            {
                OpenSavedWorkspace(form, ws);
                form.SetWorkspaceNotes("keep me");
                form.WorkspaceSavePrompt = _ => DialogResult.Yes;
                var prompts = new Prompts();   // 곧바로 취소
                form.NewWorkspacePrompt = prompts.Next;

                Assert.False(Pump2(form.NewWorkspaceAsync()));

                Assert.Equal(1, prompts.Calls);
                Assert.Equal(Path.GetFullPath(ws), form.WorkspaceFilePath);
                Assert.Single(form.Tabs);
                Assert.Equal("keep me", WorkspaceFile.Load(ws).Agent?.Notes);   // '저장'을 고른 변경은 파일에 남는다
                Assert.True(form.IsPanelVisible(PanelKind.Detail));
                Assert.True(form.IsPanelVisible(PanelKind.Explorer));
            });
        }

        [Fact]
        public void Declining_the_overwrite_returns_to_the_dialog_and_cancelling_there_leaves_the_existing_file_alone()
        {
            string ws = Path.Combine(_dir, "o.ncvws");
            string existing = Path.Combine(_dir, "taken.ncvws");
            File.WriteAllText(existing, "precious");
            OnForm(new AppSettings(), form =>
            {
                OpenSavedWorkspace(form, ws);
                var asked = new List<string>();
                form.WorkspaceOverwriteConfirm = p => { asked.Add(p); return false; };
                var prompts = new Prompts(Req(_dir, "taken"));   // 두 번째 호출은 취소
                form.NewWorkspacePrompt = prompts.Next;

                Assert.False(Pump2(form.NewWorkspaceAsync()));

                Assert.Equal(2, prompts.Calls);
                Assert.Equal(new[] { existing }, asked);
                Assert.Equal("precious", File.ReadAllText(existing));
                Assert.Equal(Path.GetFullPath(ws), form.WorkspaceFilePath);
                Assert.Single(form.Tabs);
            });
        }

        [Fact]
        public void Cancelling_the_unsaved_cell_edit_confirmation_closes_nothing_and_writes_no_file()
        {
            string ws = Path.Combine(_dir, "e.ncvws");
            OnForm(new AppSettings(), form =>
            {
                var tab = OpenSavedWorkspace(form, ws);
                Call(form, "CommitCellEdit", 0, 1, "EDITED");
                Assert.True(tab.HasUnsavedEdits);
                IReadOnlyList<DocumentTab>? shown = null;
                form.UnsavedTabsConfirm = list => { shown = list; return false; };
                form.WorkspaceSavePrompt = _ => DialogResult.No;
                form.NewWorkspacePrompt = new Prompts(Req(_dir, "never")).Next;

                Assert.False(Pump2(form.NewWorkspaceAsync()));

                Assert.Equal(new[] { tab }, shown);
                Assert.Single(form.Tabs);
                Assert.True(tab.HasUnsavedEdits);
                Assert.Equal(Path.GetFullPath(ws), form.WorkspaceFilePath);
                Assert.False(File.Exists(Path.Combine(_dir, "never.ncvws")));
            });
        }

        [Fact]
        public void An_empty_new_workspace_closes_everything_writes_the_file_and_becomes_current()
        {
            string ws = Path.Combine(_dir, "old.ncvws");
            string folder = Path.Combine(_dir, "target");
            Directory.CreateDirectory(folder);
            var settings = new AppSettings { StartupPanels = new PanelLayout { Agent = false, Detail = false, Explorer = false } };
            OnForm(settings, form =>
            {
                OpenSavedWorkspace(form, ws);
                form.SetWorkspaceNotes("old notes");
                var asked = new List<string>();
                form.WorkspaceSavePrompt = t => { asked.Add(t); return DialogResult.No; };
                var prompts = new Prompts(Req(folder, "fresh"));
                form.NewWorkspacePrompt = prompts.Next;

                Assert.True(Pump2(form.NewWorkspaceAsync()));

                string target = Path.Combine(folder, "fresh.ncvws");
                Assert.Single(asked);
                Assert.True(prompts.OpenFilesAvailable);
                Assert.Equal(Path.GetFullPath(target), form.WorkspaceFilePath);
                Assert.Empty(form.Tabs);
                Assert.Empty(form.Workspace!.Sources);
                Assert.Empty(form.Workspace.Views);
                Assert.True(string.IsNullOrEmpty(form.WorkspaceNotes));
                Assert.Equal(WorkspaceFile.Extension, Path.GetExtension(target));
                var model = WorkspaceFile.Load(target);
                Assert.Empty(model.Sources);
                Assert.Empty(model.Tabs);
                Assert.Null(model.Agent?.Session);
                Assert.Equal(StartupOf(form).Detail, form.IsPanelVisible(PanelKind.Detail));
                Assert.Equal(StartupOf(form).Explorer, form.IsPanelVisible(PanelKind.Explorer));
                Assert.Equal(Path.GetFullPath(target), settings.RecentWorkspaces![0]);
                Assert.False(form.IsWorkspaceFileBusy);
                Assert.False((bool)Call(form, "WorkspaceNeedsSave")!);                 // 방금 쓴 파일이라 바로 '변경 있음'이 아니다
                Assert.Contains("fresh.ncvws", form.Controls.Find("statusStrip1", true).OfType<StatusStrip>().Single().Items.OfType<ToolStripStatusLabel>().Select(l => l.Text).Aggregate("", (a, b) => a + b));
                Assert.Null(WorkspaceFile.Load(ws).Agent?.Notes);                      // 저장 안 함 = 이전 파일은 그대로(메모가 새로 들어가지 않았다)
            });
        }

        [Fact]
        public void Including_open_files_keeps_the_tabs_and_registers_them_as_sources()
        {
            string ws = Path.Combine(_dir, "old2.ncvws");
            OnForm(new AppSettings(), form =>
            {
                var tab = OpenSavedWorkspace(form, ws);
                form.Workspace!.CreateView("v1", "SELECT * FROM a");   // 이전 작업 공간의 뷰는 새 작업 공간으로 넘어가지 않는다
                form.WorkspaceSavePrompt = _ => DialogResult.No;
                var prompts = new Prompts(Req(_dir, "withfiles", include: true));
                form.NewWorkspacePrompt = prompts.Next;

                Assert.True(Pump2(form.NewWorkspaceAsync()));

                string target = Path.Combine(_dir, "withfiles.ncvws");
                Assert.Single(form.Tabs);
                Assert.Same(tab, form.Tabs[0]);
                Assert.False(tab.IsClosed);
                Assert.Single(form.Workspace!.Sources);
                Assert.Empty(form.Workspace.Views);
                var model = WorkspaceFile.Load(target);
                Assert.Single(model.Sources);
                Assert.Single(model.Tabs);
                Assert.Equal(Path.GetFullPath(target), form.WorkspaceFilePath);
                Assert.False((bool)Call(form, "WorkspaceNeedsSave")!);
            });
        }

        [Fact]
        public void Confirming_the_overwrite_replaces_the_existing_workspace_file()
        {
            string existing = Path.Combine(_dir, "again.ncvws");
            OnForm(new AppSettings(), form =>
            {
                form.ApplyLayout(new PanelLayout { Agent = false, Detail = false, Explorer = false });
                Open(form, MakeCsv("b.csv", CsvA));
                File.WriteAllText(existing, "not a workspace");
                var asked = new List<string>();
                form.WorkspaceOverwriteConfirm = p => { asked.Add(p); return true; };
                form.NewWorkspacePrompt = new Prompts(Req(_dir, "again")).Next;

                Assert.True(Pump2(form.NewWorkspaceAsync()));

                Assert.Equal(new[] { existing }, asked);
                Assert.Empty(WorkspaceFile.Load(existing).Tabs);                         // 이제 올바른 빈 작업 공간 파일
                Assert.Equal(Path.GetFullPath(existing), form.WorkspaceFilePath);
                Assert.Empty(form.Tabs);
            });
        }

        [Fact]
        public void Switches_the_agent_to_the_new_workspace_file_with_a_new_conversation()
        {
            string ws = Path.Combine(_dir, "ag.ncvws");
            string folder = Path.Combine(_dir, "agent_target");
            Directory.CreateDirectory(folder);
            OnForm(new AppSettings { StartupPanels = new PanelLayout { Agent = false } }, form =>
            {
                OpenSavedWorkspace(form, ws);
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
                        LocateOmp = _ => "C:\\fake\\omp.exe",
                        RunVersion = (_, _) => Task.FromResult<string?>("omp/18.4.4"),
                        ReadGuide = () => "# guide",
                        LocalPython = new FakePythonSetup(),
                        ReadModelUsage = _ => OmpModelUsage.Result.Fail(OmpModelUsage.Status.NoDatabase, "test"),
                    });
                controller.StartOnFirstUse(() => _dir);
                controller.SetWorkspaceContext((AgentWorkspaceContext)Call(form, "BuildAgentWorkspaceContext")!);
                SetField(form, "_agentController", controller);
                Assert.Equal(Path.GetFullPath(ws), Context(controller).WorkspaceFile);

                form.NewWorkspacePrompt = new Prompts(Req(folder, "brand new")).Next;
                Assert.True(Pump2(form.NewWorkspaceAsync()));

                string target = Path.GetFullPath(Path.Combine(folder, "brand new.ncvws"));
                var ctx = Context(controller);
                Assert.Equal(target, ctx.WorkspaceFile);
                Assert.Empty(ctx.Tables);
                Assert.True(string.IsNullOrEmpty(ctx.Notes));
                Assert.True(controller.IsStartDeferred);                                   // 첫 메시지 전에는 omp를 시작하지 않는다(작업 공간 전환과 동일)
                Assert.Empty(factory.Processes);
                Assert.Equal(Path.Combine(folder, "brand new" + AgentWorkspace.OutputSuffix), Call(form, "CurrentAnalysisFolder"));
                Assert.Null(WorkspaceFile.Load(target).Agent?.Session);                    // 이전 대화를 새 작업 공간에 연결하지 않는다
                SetField(form, "_agentController", null);
            });
        }

        private static AgentWorkspaceContext Context(ChatController c) =>
            (AgentWorkspaceContext)typeof(ChatController).GetField("_workspaceContext", Inst)!.GetValue(c)!;

        private static bool Pump2(Task<bool> task) => Await(task);

        [Fact]
        public void The_menu_puts_New_Workspace_first_in_the_workspace_group_with_Ctrl_Shift_N()
        {
            OnForm(new AppSettings(), form =>
            {
                var file = form.MenuStripForTests().Items.OfType<ToolStripMenuItem>().First(i => i.DropDownItems.OfType<ToolStripMenuItem>().Any(d => (d.Tag as string) == "file.openWorkspace"));
                var order = file.DropDownItems.OfType<ToolStripMenuItem>().Select(i => i.Tag as string ?? i.Text).ToList();
                int n = order.IndexOf("file.newWorkspace");
                Assert.True(n >= 0);
                Assert.Equal("file.openWorkspace", order[n + 1]);
                // 새 작업 공간 · 열기 · 최근 · 저장 · 다른 이름으로 저장 · 닫기 — 사이에 다른 항목이 끼지 않는다.
                var group = file.DropDownItems.Cast<ToolStripItem>().SkipWhile(i => (i.Tag as string) != "file.newWorkspace").TakeWhile(i => i is not ToolStripSeparator).ToList();
                Assert.Equal(6, group.Count);
                Assert.Equal("file.newWorkspace", group[0].Tag);
                Assert.Equal("file.openWorkspace", group[1].Tag);
                Assert.Equal("file.saveWorkspace", group[3].Tag);
                Assert.Equal("file.closeWorkspace", group[5].Tag);
                Assert.Equal(Keys.Control | Keys.Shift | Keys.N, group[0] is ToolStripMenuItem m ? m.ShortcutKeys : Keys.None);
                Assert.Equal("Ctrl+Shift+N", CommandShortcuts.Get("file.newWorkspace").KeyText);
            });
        }

        [Fact]
        public void Ctrl_Shift_N_is_bound_to_exactly_one_command()
        {
            var owners = CommandShortcuts.AllBindings().Where(b => b.Keys == (Keys.Control | Keys.Shift | Keys.N)).Select(b => b.Id).ToList();
            Assert.Equal(new[] { "file.newWorkspace" }, owners);
        }

        [Theory]
        [InlineData("", "name", false)]
        [InlineData("<dir>", "", false)]
        [InlineData("<dir>", "   ", false)]
        [InlineData("<dir>", "a/b", false)]
        [InlineData("<dir>", "bad?name", false)]
        [InlineData("<dir>", "trailing.", false)]
        [InlineData("<dir>", "CON", false)]
        [InlineData("<dir>", "com1.backup", false)]
        [InlineData("<dir>", ".ncvws", false)]
        [InlineData("<dir>\\missing", "name", false)]
        [InlineData("<dir>", "Sales 2026", true)]
        [InlineData("<dir>", "분석 작업", true)]
        [InlineData("<dir>", "typed.ncvws", true)]
        public void Names_and_locations_are_validated(string folder, string name, bool ok)
        {
            string f = folder.Replace("<dir>", _dir);
            Assert.Equal(ok, NewWorkspaceRequest.Validate(f, name) is null);
        }

        [Fact]
        public void The_preview_paths_follow_the_agents_analysis_folder_rule()
        {
            var req = new NewWorkspaceRequest(_dir, NewWorkspaceRequest.NormalizeName("  Sales.ncvws "), false);
            Assert.Equal("Sales", req.Name);
            Assert.Equal(Path.Combine(_dir, "Sales.ncvws"), req.FilePath);
            Assert.Equal(Path.Combine(_dir, "Sales_분석결과"), req.AnalysisFolder);
            Assert.Equal(AgentWorkspace.StableOutputFolder(req.FilePath, null), req.AnalysisFolder);
        }

        [Fact]
        public void The_dialog_previews_the_paths_and_defaults_the_include_option_off()
        {
            OnForm(new AppSettings(), form =>
            {
                using var dlg = new NewWorkspaceDialog(form.PaletteRef, new NewWorkspaceRequest(_dir, "Plan", false), openFilesAvailable: true);
                _ = dlg.Handle;
                Assert.False(dlg.Controls.Find("includeOpenFiles", true).OfType<CheckBox>().Single().Checked);
                Assert.Equal(Path.Combine(_dir, "Plan.ncvws"), dlg.Controls.Find("filePreview", true).Single().Text);
                Assert.Equal(Path.Combine(_dir, "Plan_분석결과"), dlg.Controls.Find("analysisPreview", true).Single().Text);
                var name = dlg.Controls.Find("nameText", true).OfType<TextBox>().Single();
                name.Text = "Other";
                Assert.Equal(Path.Combine(_dir, "Other_분석결과"), dlg.Controls.Find("analysisPreview", true).Single().Text);
                Assert.True(dlg.Controls.Find("createButton", true).Single().Enabled);
                name.Text = "bad/name";
                Assert.False(dlg.Controls.Find("createButton", true).Single().Enabled);
                Assert.NotEqual("", dlg.Controls.Find("problemLabel", true).Single().Text);
                using var none = new NewWorkspaceDialog(form.PaletteRef, new NewWorkspaceRequest(_dir, "Plan", true), openFilesAvailable: false);
                var include = none.Controls.Find("includeOpenFiles", true).OfType<CheckBox>().Single();
                Assert.False(include.Enabled);
                Assert.False(include.Checked);
            });
        }
    }
}
