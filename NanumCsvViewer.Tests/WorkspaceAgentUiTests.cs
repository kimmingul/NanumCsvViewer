using System.Text;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    // 진짜 Form1(보이지 않는 창)에서: 저장 → 다시 열기로 메모·제한 설정·뷰 출처가 돌아오는지, 앱 설정이 더 엄격하면 작업 공간이 풀지 못하는지,
    // 설정 범위(이 작업 공간/앱 기본값) 선택, 탐색기의 에이전트 배지·툴팁.
    [Collection("SavedViewStore")]
    public class WorkspaceAgentUiTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_wsagent_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        private static readonly CancellationToken NoCancel = CancellationToken.None;

        static WorkspaceAgentUiTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public WorkspaceAgentUiTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string MakeCsv(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        private const string CsvA = "id,name,score\n1,kim,30\n2,lee,10\n3,park,20\n";

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
            PumpUntil(() => tab!.Document is { IndexingComplete: true } && !tab.IsIndexing, "indexing");
            return tab!;
        }

        private static object? Call(Form1 f, string method, params object[] args) => typeof(Form1).GetMethod(method, Inst)!.Invoke(f, args);
        private static void SetField(Form1 f, string field, object? value) => typeof(Form1).GetField(field, Inst)!.SetValue(f, value);
        private static AgentHostOptions Options(Form1 f) => (AgentHostOptions)Call(f, "AgentOptions")!;

        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void Saving_and_reopening_a_workspace_restores_notes_restrictions_and_view_provenance_and_the_explorer_shows_the_agent_badge()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "proj.ncvws");

            // 1) 만든다: 사용자 뷰·마법사 뷰·에이전트 뷰 + 메모 + 엄격한 설정, 그리고 저장.
            OnForm(new AppSettings(), form =>
            {
                var tab = Open(form, a);
                Await(form.RegisterTabAsync(tab, NoCancel));
                Assert.NotNull(Await(form.CreateAndOpenViewAsync("mine", "SELECT * FROM a", false)));
                Assert.NotNull(Await(form.CreateAndOpenViewAsync("joined", "SELECT name FROM a", false, ViewProvenance.Wizard("Join"))));
                form.Workspace!.CreateView("by_ai", "SELECT name, score FROM a WHERE score > 15", false, ViewProvenance.Agent("점수가 15 넘는 사람만 보여 줘"));
                form.SetWorkspaceNotes("a.id는 고유 번호\n목표: 점수 분포");
                form.WorkspaceAgentInfo.ApprovalMode = "always-ask";
                form.WorkspaceAgentInfo.DataPolicy = "SummaryOnly";
                form.WorkspaceAgentInfo.AllowLocalPython = false;
                SetField(form, "_wfFilePath", ws);
                Assert.True((bool)Call(form, "SaveWorkspace", false)!);
            });

            var saved = WorkspaceFile.Load(ws);
            Assert.Equal(WorkspaceFile.CurrentVersion, saved.Version);
            Assert.Equal("a.id는 고유 번호\n목표: 점수 분포", saved.Agent!.Notes);
            Assert.Equal(("always-ask", "SummaryOnly", false), (saved.Agent.ApprovalMode, saved.Agent.DataPolicy, saved.Agent.AllowLocalPython));
            Assert.Equal(new[] { "user", "wizard:join", "agent" }, saved.Views.OrderBy(v => v.Name == "mine" ? 0 : v.Name == "joined" ? 1 : 2).Select(v => v.CreatedBy));
            Assert.Equal("점수가 15 넘는 사람만 보여 줘", saved.Views.Single(v => v.Name == "by_ai").Request);
            Assert.All(saved.Views, v => Assert.NotNull(v.CreatedUtc));

            // 2) 느슨한 앱 설정의 다른 창에서 연다: 작업 공간의 엄격한 값이 효력을 가지고 표시가 뜬다.
            var loose = new AppSettings { AgentApprovalMode = "yolo", AgentDataPolicy = "RowsAllowed", AgentAllowLocalPython = true };
            OnForm(loose, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                Assert.Equal("a.id는 고유 번호\n목표: 점수 분포", form.WorkspaceNotes);

                var eff = Options(form);
                Assert.Equal((AgentApprovalMode.AlwaysAsk, AgentDataPolicy.SummaryOnly, false), (eff.ApprovalMode, eff.DataPolicy, eff.AllowLocalPython));
                Assert.True(eff.Limits is { Approval: true, DataPolicy: true, LocalPython: true });
                Assert.Equal("yolo", loose.AgentApprovalMode);                      // 앱 설정 자체는 그대로다

                var views = form.Workspace!.Views.ToDictionary(v => v.Name);
                Assert.True(views["by_ai"].Provenance!.IsAgent);
                Assert.Equal("점수가 15 넘는 사람만 보여 줘", views["by_ai"].Provenance!.Request);
                Assert.Equal("join", views["joined"].Provenance!.WizardName);
                Assert.Equal("user", views["mine"].Provenance!.CreatedBy);

                // 에이전트 배지(✦)와 툴팁(요청 + 시각), 사용자·마법사 뷰는 일반 표시.
                form.SetWorkspaceExplorerVisible(true);
                var explorer = form.Explorer!;
                explorer.RefreshTree();
                var rows = explorer.Snapshot().Where(r => r.Info.Kind == WorkspaceExplorer.NodeKind.View).ToDictionary(r => r.Text, r => r.Info);
                Assert.Equal(WorkspaceExplorer.AgentGlyph, WorkspaceExplorer.GlyphText(rows["by_ai"]));
                Assert.NotEqual(WorkspaceExplorer.AgentGlyph, WorkspaceExplorer.GlyphText(rows["mine"]));
                Assert.NotEqual(WorkspaceExplorer.AgentGlyph, WorkspaceExplorer.GlyphText(rows["joined"]));
                string tip = WorkspaceExplorer.TooltipText(rows["by_ai"]);
                Assert.Contains("점수가 15 넘는 사람만 보여 줘", tip);
                Assert.Contains("SELECT name, score FROM a", tip);
                Assert.Contains(saved.Views.Single(v => v.Name == "by_ai").CreatedUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), tip);
                Assert.DoesNotContain("Request", WorkspaceExplorer.TooltipText(rows["mine"]));
            });
        }

        [Fact]
        public void A_v1_workspace_file_opens_with_unknown_provenance_no_restrictions_and_no_badge()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "old.ncvws");
            File.WriteAllText(ws, $$"""
                { "format": "ncvws", "version": 1,
                  "sources": [ { "kind": "csv", "name": "a", "path": "a.csv", "absolutePath": {{System.Text.Json.JsonSerializer.Serialize(a)}}, "hasHeader": true } ],
                  "views": [ { "name": "v", "sql": "SELECT * FROM a" } ], "tabs": [], "activeTab": -1 }
                """);
            OnForm(new AppSettings { AgentDataPolicy = "RowsWithApproval" }, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                Assert.Null(form.Workspace!.Views.Single().Provenance);
                Assert.Equal("", form.WorkspaceNotes);
                var eff = Options(form);
                Assert.False(eff.Limits.Any);
                Assert.Equal(AgentDataPolicy.RowsWithApproval, eff.DataPolicy);

                form.SetWorkspaceExplorerVisible(true);
                form.Explorer!.RefreshTree();
                var node = form.Explorer.Snapshot().Single(r => r.Info.Kind == WorkspaceExplorer.NodeKind.View).Info;
                Assert.Equal("◈", WorkspaceExplorer.GlyphText(node));
                Assert.Equal("", WorkspaceExplorer.TooltipText(node));
            });
        }

        [Fact]
        public void Opening_another_workspace_replaces_the_previous_ones_restrictions_and_notes()
        {
            string a = MakeCsv("a.csv", CsvA);
            string strict = Path.Combine(_dir, "strict.ncvws"), plain = Path.Combine(_dir, "plain.ncvws");
            WorkspaceFile.Save(strict, new WorkspaceFileModel { Agent = new WorkspaceFileAgent { DataPolicy = "SummaryOnly", Notes = "strict notes" } });
            WorkspaceFile.Save(plain, new WorkspaceFileModel());
            OnForm(new AppSettings { AgentDataPolicy = "RowsAllowed" }, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(strict)));
                Assert.Equal(AgentDataPolicy.SummaryOnly, Options(form).DataPolicy);
                Assert.Equal("strict notes", form.WorkspaceNotes);

                Assert.True(Await(form.OpenWorkspaceFileAsync(plain)));
                Assert.Equal(AgentDataPolicy.RowsAllowed, Options(form).DataPolicy);   // 이전 작업 공간의 제한이 남지 않는다
                Assert.Equal("", form.WorkspaceNotes);
            });
        }

        [Fact]
        public void Even_a_workspace_file_that_says_yolo_and_rows_allowed_cannot_loosen_strict_app_settings()
        {
            string ws = Path.Combine(_dir, "loose.ncvws");
            WorkspaceFile.Save(ws, new WorkspaceFileModel { Agent = new WorkspaceFileAgent { ApprovalMode = "yolo", DataPolicy = "RowsAllowed", AllowLocalPython = true } });
            var strictApp = new AppSettings { AgentApprovalMode = "always-ask", AgentDataPolicy = "SummaryOnly", AgentAllowLocalPython = false };
            OnForm(strictApp, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                var eff = Options(form);
                Assert.Equal((AgentApprovalMode.AlwaysAsk, AgentDataPolicy.SummaryOnly, false), (eff.ApprovalMode, eff.DataPolicy, eff.AllowLocalPython));
                Assert.False(eff.Limits.Any);
            });
        }

        [Fact]
        public void The_extra_args_lock_beats_a_workspace_restriction_for_the_approval_mode_only()
        {
            string ws = Path.Combine(_dir, "strict.ncvws");
            WorkspaceFile.Save(ws, new WorkspaceFileModel { Agent = new WorkspaceFileAgent { ApprovalMode = "always-ask", DataPolicy = "SummaryOnly" } });
            var app = new AppSettings { AgentExtraArgs = "--yolo", AgentApprovalMode = "write", AgentDataPolicy = "RowsAllowed" };
            OnForm(app, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                var eff = Options(form);
                Assert.Equal(AgentApprovalMode.Yolo, eff.EffectiveApprovalMode);        // 잠금이 이긴다
                Assert.Equal(AgentDataPolicy.SummaryOnly, eff.DataPolicy);              // 나머지는 작업 공간이 조인다
                Assert.False(eff.Limits.Approval);
            });
        }

        // ---- 설정 범위 선택 --------------------------------------------------------------------------------------

        [Fact]
        public void Choosing_this_workspace_for_a_dropdown_change_stores_it_in_the_workspace_and_leaves_the_app_setting_alone()
        {
            string ws = Path.Combine(_dir, "w.ncvws");
            WorkspaceFile.Save(ws, new WorkspaceFileModel());
            var app = new AppSettings { AgentApprovalMode = "yolo" };
            OnForm(app, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                string? asked = null;
                form.AgentScopeChooser = text => { asked = text; return AgentSettingScope.Workspace; };

                var next = (AgentHostOptions?)Call(form, "ApplyApprovalFromChat", AgentApprovalMode.AlwaysAsk);
                Assert.NotNull(asked);
                Assert.Equal("always-ask", form.WorkspaceAgentInfo.ApprovalMode);
                Assert.Equal("yolo", app.AgentApprovalMode);
                Assert.Equal(AgentApprovalMode.AlwaysAsk, next!.ApprovalMode);
                Assert.True(next.Limits.Approval);
                Assert.True((bool)Call(form, "WorkspaceNeedsSave")!);                       // 저장 안 한 변경으로 보인다
            });
        }

        [Fact]
        public void Choosing_app_default_changes_the_app_setting_and_cancel_changes_nothing()
        {
            string ws = Path.Combine(_dir, "w.ncvws");
            WorkspaceFile.Save(ws, new WorkspaceFileModel());
            var app = new AppSettings { AgentApprovalMode = "yolo" };
            OnForm(app, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                form.AgentScopeChooser = _ => null;
                Assert.Null(Call(form, "ApplyApprovalFromChat", AgentApprovalMode.Write));
                Assert.Equal("yolo", app.AgentApprovalMode);
                Assert.Null(form.WorkspaceAgentInfo.ApprovalMode);

                form.AgentScopeChooser = _ => AgentSettingScope.App;
                var next = (AgentHostOptions?)Call(form, "ApplyApprovalFromChat", AgentApprovalMode.Write);
                Assert.Equal("write", app.AgentApprovalMode);
                Assert.Null(form.WorkspaceAgentInfo.ApprovalMode);
                Assert.Equal(AgentApprovalMode.Write, next!.ApprovalMode);
            });
        }

        [Fact]
        public void Without_a_workspace_file_the_dropdown_asks_nothing_and_saves_to_the_app_as_before()
        {
            var app = new AppSettings { AgentApprovalMode = "yolo" };
            OnForm(app, form =>
            {
                bool asked = false;
                form.AgentScopeChooser = _ => { asked = true; return AgentSettingScope.Workspace; };
                var next = (AgentHostOptions?)Call(form, "ApplyApprovalFromChat", AgentApprovalMode.AlwaysAsk);
                Assert.False(asked);
                Assert.Equal("always-ask", app.AgentApprovalMode);
                Assert.Equal(AgentApprovalMode.AlwaysAsk, next!.ApprovalMode);
            });
        }

        [Fact]
        public void A_looser_choice_for_this_workspace_is_stored_but_changes_nothing_while_the_app_is_stricter()
        {
            string ws = Path.Combine(_dir, "w.ncvws");
            WorkspaceFile.Save(ws, new WorkspaceFileModel());
            OnForm(new AppSettings { AgentApprovalMode = "always-ask" }, form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                form.AgentScopeChooser = _ => AgentSettingScope.Workspace;
                var next = (AgentHostOptions?)Call(form, "ApplyApprovalFromChat", AgentApprovalMode.Yolo);
                Assert.Equal("yolo", form.WorkspaceAgentInfo.ApprovalMode);
                Assert.Equal(AgentApprovalMode.AlwaysAsk, next!.ApprovalMode);              // 더 엄격한 앱 설정이 이긴다
            });
        }

        // ---- 메모 · 대화 ------------------------------------------------------------------------------------------

        [Fact]
        public void The_notes_command_edits_through_the_dialog_seam_trims_to_the_limit_and_clears_with_blank_text()
        {
            OnForm(new AppSettings(), form =>
            {
                form.WorkspaceNotesEditor = current => current + "첫 줄";
                form.EditWorkspaceNotesCommand();
                Assert.Equal("첫 줄", form.WorkspaceNotes);

                form.WorkspaceNotesEditor = _ => new string('x', WorkspaceFileAgent.MaxNotesChars + 100);
                form.EditWorkspaceNotesCommand();
                Assert.Equal(WorkspaceFileAgent.MaxNotesChars, form.WorkspaceNotes.Length);

                form.WorkspaceNotesEditor = _ => null;                                      // 취소는 그대로
                form.EditWorkspaceNotesCommand();
                Assert.Equal(WorkspaceFileAgent.MaxNotesChars, form.WorkspaceNotes.Length);

                form.WorkspaceNotesEditor = _ => "   \r\n ";
                form.EditWorkspaceNotesCommand();
                Assert.Equal("", form.WorkspaceNotes);
                Assert.Null(form.WorkspaceAgentInfo.Notes);
            });
        }

        [Fact]
        public void A_stored_conversation_is_kept_on_open_and_only_a_session_file_that_exists_is_saved_back()
        {
            string a = MakeCsv("a.csv", CsvA);
            string ws = Path.Combine(_dir, "chat.ncvws");
            string live = Path.Combine(_dir, "2026-01-01T00-00-00-000Z_live-1.jsonl");
            File.WriteAllText(live, "{}\n");
            WorkspaceFile.Save(ws, new WorkspaceFileModel { Agent = new WorkspaceFileAgent { Session = new WorkspaceFileSession { Id = "live-1", File = live } } });
            OnForm(new AppSettings(), form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                Assert.Equal(live, form.WorkspaceAgentInfo.Session!.File);
                Assert.True((bool)Call(form, "SaveWorkspace", false)!);
                Assert.Equal(live, WorkspaceFile.Load(ws).Agent!.Session!.File);          // 패널을 안 열었어도 읽어 둔 연결을 잃지 않는다

                File.Delete(live);                                                       // 세션이 사라지면 죽은 연결은 저장하지 않는다
                Assert.True((bool)Call(form, "SaveWorkspace", false)!);
                Assert.Null(WorkspaceFile.Load(ws).Agent?.Session);
            });
        }

        [Fact]
        public void Starting_a_new_conversation_drops_the_stored_link_after_confirmation()
        {
            string ws = Path.Combine(_dir, "chat.ncvws");
            string live = Path.Combine(_dir, "2026-01-01T00-00-00-000Z_live-2.jsonl");
            File.WriteAllText(live, "{}\n");
            WorkspaceFile.Save(ws, new WorkspaceFileModel { Agent = new WorkspaceFileAgent { Session = new WorkspaceFileSession { Id = "live-2", File = live } } });
            OnForm(new AppSettings(), form =>
            {
                Assert.True(Await(form.OpenWorkspaceFileAsync(ws)));
                form.WorkspaceConfirmSink = _ => false;
                Pump(form.StartNewWorkspaceConversationAsync());
                Assert.NotNull(form.WorkspaceAgentInfo.Session);                            // 취소하면 그대로

                form.WorkspaceConfirmSink = _ => true;
                Pump(form.StartNewWorkspaceConversationAsync());
                Assert.Null(form.WorkspaceAgentInfo.Session);
                Assert.True((bool)Call(form, "WorkspaceNeedsSave")!);                       // 연결이 바뀌었으니 저장 대상
            });
        }
    }
}
