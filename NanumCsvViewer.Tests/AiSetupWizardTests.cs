using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Rpc;
using NanumCsvViewer.Agent.Setup;

namespace NanumCsvViewer.Tests
{
    // AI 설정 도우미: 단계 상태 기계(가짜 보고서), 자동으로 여는 규칙, 진단 정보 가리기, 모델이 없을 때의 채팅 알림,
    // omp 로그인 RPC(가짜 omp), 화면(가짜 환경) 동작.
    internal static class SetupFakes
    {
        public static readonly OmpVersion Version = new(18, 7, 0);

        public static WebViewReport WebView(WebViewProblem problem = WebViewProblem.None, string? runtime = "149.0.4022.98",
            IReadOnlyList<CompatLayerEntry>? layers = null, int? hresult = null) =>
            new(runtime, @"C:\Users\tester\AppData\Local\NanumCsvViewer\WebView2", "PerMonitorV2", 150, layers ?? Array.Empty<CompatLayerEntry>(),
                hresult, hresult is null ? null : "failed", @"C:\Users\tester\AppData\Local\NanumCsvViewer\agent\webview2-init.log", problem);

        public static CompatLayerEntry Layer(string hive = "HKCU", string exe = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\149.0.4022.98\msedgewebview2.exe",
            string flags = "HIGHDPIAWARE", bool removable = true) => new(hive, exe, flags, true, removable);

        public static OmpDiscoveryResult OmpOk(string exe = @"C:\fake\omp.exe") =>
            new(new[] { new OmpLocation("PATH", exe, OmpLocationStatus.Found) }, exe, Array.Empty<OmpCandidate>(), Version, OmpProblemKind.None, "");

        public static OmpDiscoveryResult OmpMissing(params OmpCandidate[] candidates) =>
            new(new[] { new OmpLocation("PATH (omp.exe/omp.cmd)", null, OmpLocationStatus.Missing), new OmpLocation(@"%LOCALAPPDATA%\omp\omp.exe", @"C:\x\omp.exe", OmpLocationStatus.Missing) },
                null, candidates, null, OmpProblemKind.NotFound, "");

        public static AccountSnapshot Account(int models = 3, params LoginProvider[] providers) =>
            new(true, 2, 1234, null, providers.Length > 0 ? providers : new[] { new LoginProvider("anthropic", "Anthropic", models > 0) }, models);
    }

    internal sealed class FakeAccountProbe : IOmpAccountProbe
    {
        public Queue<AccountSnapshot> Snapshots { get; } = new();
        public Func<string, LoginResult> OnLogin { get; set; } = _ => new LoginResult(true, "Signed in.", false);
        /// <summary>널이 아니면 OnLogin 대신 쓴다(주소·입력 칸을 거치는 로그인).</summary>
        public Func<string, LoginUi, CancellationToken, Task<LoginResult>>? OnLoginAsync { get; set; }
        public List<string> LoggedIn { get; } = new();
        public int Inspections { get; private set; }
        public bool Disposed { get; private set; }

        public Task<AccountSnapshot> InspectAsync(string exePath, CancellationToken ct)
        {
            Inspections++;
            var snapshot = Snapshots.Count > 1 ? Snapshots.Dequeue() : Snapshots.Peek();
            return Task.FromResult(snapshot);
        }

        public Task<LoginResult> LoginAsync(string exePath, string providerId, LoginUi ui, CancellationToken ct)
        {
            LoggedIn.Add(providerId);
            return OnLoginAsync is { } custom ? custom(providerId, ui, ct) : Task.FromResult(OnLogin(providerId));
        }

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeSetupOps : AiSetupOps
    {
        public bool ConfirmAnswer { get; set; } = true;
        public string? PickedOmp { get; set; }
        public string? SavePath { get; set; }
        public List<string> Urls { get; } = new();
        public List<string> Folders { get; } = new();
        public List<string> Confirms { get; } = new();
        public List<string> Messages { get; } = new();
        public List<(string Exe, string? Provider)> Terminals { get; } = new();
        public List<(CompatLayerEntry Entry, string Backup)> Removed { get; } = new();
        public List<(string Src, bool AddToPath)> Installed { get; } = new();
        public int Downloads { get; private set; }
        public bool DownloadAddToPath { get; private set; }
        public string? Clipboard { get; private set; }
        public Func<CompatLayerEntry, RemovalResult> OnRemove { get; set; } = e => new RemovalResult(true, "removed", "backup.reg");
        public Func<string, OmpInstallResult> OnInstall { get; set; } = s => new OmpInstallResult(true, @"C:\fake\omp.exe", "installed", OmpInstallError.None, SetupFakes.Version);
        public Dictionary<string, string> Files { get; } = new();

        public override void OpenUrl(string url) => Urls.Add(url);
        public override void OpenFolder(string path) => Folders.Add(path);
        public override void OpenTerminalLogin(string exe, string? providerId) => Terminals.Add((exe, providerId));
        public override bool Confirm(string title, string message, bool yesNo = false) { Confirms.Add(message); return ConfirmAnswer; }
        public override void ShowMessage(string title, string message, bool error = false) => Messages.Add(message);
        public override string? PickOmpFile() => PickedOmp;
        public override string DefaultBackupPath() => @"C:\backup\appcompat.reg";
        public override RemovalResult RemoveCompatLayer(CompatLayerEntry entry, string backupPath) { Removed.Add((entry, backupPath)); return OnRemove(entry); }
        public override Task<OmpInstallResult> InstallFromFileAsync(string source, bool addToPath, CancellationToken ct) { Installed.Add((source, addToPath)); return Task.FromResult(OnInstall(source)); }
        public override Task<OmpInstallResult> DownloadAndInstallAsync(IProgress<OmpDownloadProgress> progress, bool addToPath, CancellationToken ct)
        {
            Downloads++;
            DownloadAddToPath = addToPath;
            progress.Report(new OmpDownloadProgress(5 << 20, 10 << 20, "download"));
            return Task.FromResult(OnInstall("download"));
        }
        public override string OmpTargetPath => @"C:\Users\tester\AppData\Local\omp\omp.exe";
        public override void SetClipboard(string text) => Clipboard = text;
        public override string? PickSavePath(string suggestedFileName) => SavePath;
        public override void WriteFile(string path, string text) => Files[path] = text;
    }

    // ================================================================== 단계 상태 기계

    public class AiSetupStateMachineTests
    {
        private static AiSetupRunner Runner(Func<WebViewReport> web, Func<OmpDiscoveryResult> omp, Func<FakeAccountProbe> probe, bool ko = false) =>
            new(new AiSetupServices { CollectWebView = web, DiscoverOmp = _ => Task.FromResult(omp()), CreateProbe = probe }, ko);

        private static FakeAccountProbe Probe(AccountSnapshot snapshot)
        {
            var p = new FakeAccountProbe();
            p.Snapshots.Enqueue(snapshot);
            return p;
        }

        [Fact]
        public async Task A_healthy_pc_turns_all_four_steps_green_with_no_fix_buttons()
        {
            using var runner = Runner(() => SetupFakes.WebView(), () => SetupFakes.OmpOk(), () => Probe(SetupFakes.Account(3)));
            await runner.RunAllAsync();

            Assert.True(runner.AllOk);
            Assert.All(runner.Steps, s => { Assert.Equal(SetupState.Ok, s.State); Assert.Empty(s.Fixes); });
            Assert.Null(runner.FirstProblem);
            Assert.Contains("18.7.0", runner.Step(SetupStepId.Omp).Summary);
            Assert.Contains("3", runner.Step(SetupStepId.Login).Summary);
        }

        [Fact]
        public async Task A_missing_omp_blocks_login_and_connection_and_offers_browse_download_and_each_candidate()
        {
            FakeAccountProbe? created = null;
            var candidate = new OmpCandidate(@"C:\Users\x\Downloads\omp-windows-x64.exe", "Downloads folder");
            using var runner = Runner(() => SetupFakes.WebView(), () => SetupFakes.OmpMissing(candidate), () => created = Probe(SetupFakes.Account()));
            await runner.RunAllAsync();

            var omp = runner.Step(SetupStepId.Omp);
            Assert.Equal(SetupState.Fail, omp.State);
            Assert.Equal(new[] { SetupFixKind.InstallCandidate, SetupFixKind.BrowseOmp, SetupFixKind.DownloadOmp }, omp.Fixes.Select(f => f.Kind).ToArray());
            Assert.Same(candidate, omp.Fixes[0].Arg);
            Assert.Contains("PATH (omp.exe/omp.cmd)", omp.Detail);   // 확인한 위치가 그대로 보인다
            Assert.Equal(SetupState.Blocked, runner.Step(SetupStepId.Login).State);
            Assert.Equal(SetupState.Blocked, runner.Step(SetupStepId.Connection).State);
            Assert.Null(created);                                     // omp가 없으면 프로세스를 띄워 보지 않는다
            Assert.Equal(SetupStepId.Omp, runner.FirstProblem);
        }

        [Fact]
        public async Task Fixing_omp_and_rechecking_moves_the_blocked_steps_forward()
        {
            var found = SetupFakes.OmpMissing();
            using var runner = Runner(() => SetupFakes.WebView(), () => found, () => Probe(SetupFakes.Account(2)));
            await runner.RunAllAsync();
            Assert.Equal(SetupState.Blocked, runner.Step(SetupStepId.Login).State);

            found = SetupFakes.OmpOk();
            await runner.RunOmpAsync();

            Assert.True(runner.AllOk);
        }

        [Fact]
        public async Task A_forced_dpi_layer_on_the_webview_exe_is_a_failure_with_a_remove_button_only_for_user_entries()
        {
            var layers = new[] { SetupFakes.Layer(), SetupFakes.Layer("HKLM64", removable: false) };
            using var runner = Runner(() => SetupFakes.WebView(WebViewProblem.CompatLayer, layers: layers, hresult: unchecked((int)0x8007139F)),
                () => SetupFakes.OmpOk(), () => Probe(SetupFakes.Account()), ko: true);
            await runner.RunWebViewAsync();

            var step = runner.Step(SetupStepId.WebView);
            Assert.Equal(SetupState.Fail, step.State);
            var remove = Assert.Single(step.Fixes, f => f.Kind == SetupFixKind.RemoveCompatLayer);
            Assert.True(((CompatLayerEntry)remove.Arg!).Removable);
            Assert.Contains("HKLM64", step.Detail);
            Assert.Contains("관리자", step.Detail);                     // HKLM은 안내만
            Assert.DoesNotContain(step.Fixes, f => f.Kind == SetupFixKind.InstallWebView);   // 런타임 재설치는 권하지 않는다
        }

        [Fact]
        public async Task A_dpi_layer_without_a_failure_is_only_a_warning()
        {
            using var runner = Runner(() => SetupFakes.WebView(layers: new[] { SetupFakes.Layer() }), () => SetupFakes.OmpOk(), () => Probe(SetupFakes.Account()));
            await runner.RunWebViewAsync();

            Assert.Equal(SetupState.Warn, runner.Step(SetupStepId.WebView).State);
            Assert.Contains(runner.Step(SetupStepId.WebView).Fixes, f => f.Kind == SetupFixKind.RemoveCompatLayer);
        }

        [Theory]
        [InlineData("RuntimeMissing", "InstallWebView")]
        [InlineData("BinaryOrArch", "InstallWebView")]
        [InlineData("StateMismatch", "RetryWebView")]
        [InlineData("AccessDenied", "RetryWebView")]
        [InlineData("Other", "OpenWebViewLog")]
        public async Task Each_webview_problem_offers_its_own_fix_and_the_runtime_link_only_when_it_applies(string problemName, string expectedName)
        {
            var problem = Enum.Parse<WebViewProblem>(problemName);
            var expected = Enum.Parse<SetupFixKind>(expectedName);
            string? runtime = problem == WebViewProblem.RuntimeMissing ? null : "149.0";
            using var runner = Runner(() => SetupFakes.WebView(problem, runtime, hresult: 5), () => SetupFakes.OmpOk(), () => Probe(SetupFakes.Account()));
            await runner.RunWebViewAsync();

            var step = runner.Step(SetupStepId.WebView);
            Assert.Equal(SetupState.Fail, step.State);
            Assert.Contains(step.Fixes, f => f.Kind == expected);
            bool link = step.Fixes.Any(f => f.Kind == SetupFixKind.InstallWebView);
            Assert.Equal(problem is WebViewProblem.RuntimeMissing or WebViewProblem.BinaryOrArch, link);
        }

        [Fact]
        public async Task No_models_fails_the_login_step_with_a_terminal_fix_and_leaves_the_connection_step_a_warning()
        {
            using var runner = Runner(() => SetupFakes.WebView(), () => SetupFakes.OmpOk(), () => Probe(SetupFakes.Account(0, new LoginProvider("anthropic", "Anthropic", false))));
            await runner.RunAllAsync();

            var login = runner.Step(SetupStepId.Login);
            Assert.Equal(SetupState.Fail, login.State);
            Assert.Contains(login.Fixes, f => f.Kind == SetupFixKind.OpenTerminalLogin);
            Assert.Contains("ANTHROPIC_API_KEY", login.Detail);       // API 키 방식 안내
            Assert.Equal(SetupState.Warn, runner.Step(SetupStepId.Connection).State);
            Assert.Equal(SetupStepId.Login, runner.FirstProblem);
        }

        [Fact]
        public async Task A_probe_that_cannot_start_omp_fails_the_connection_and_blocks_the_login_step()
        {
            using var runner = Runner(() => SetupFakes.WebView(), () => SetupFakes.OmpOk(), () => Probe(AccountSnapshot.Failed("boom")));
            await runner.RunAllAsync();

            Assert.Equal(SetupState.Blocked, runner.Step(SetupStepId.Login).State);
            var conn = runner.Step(SetupStepId.Connection);
            Assert.Equal(SetupState.Fail, conn.State);
            Assert.Equal("boom", conn.Detail);
            Assert.Contains(conn.Fixes, f => f.Kind == SetupFixKind.Recheck);
        }

        [Fact]
        public async Task A_throwing_collector_or_probe_becomes_a_failed_step_not_an_exception()
        {
            using var runner = new AiSetupRunner(new AiSetupServices
            {
                CollectWebView = () => throw new InvalidOperationException("registry gone"),
                DiscoverOmp = _ => Task.FromResult(SetupFakes.OmpOk()),
                CreateProbe = () => new ThrowingProbe(),
            }, false);
            await runner.RunAllAsync();

            Assert.Equal(SetupState.Fail, runner.Step(SetupStepId.WebView).State);
            Assert.Equal("registry gone", runner.Step(SetupStepId.WebView).Detail);
            Assert.Equal(SetupState.Fail, runner.Step(SetupStepId.Connection).State);
        }

        private sealed class ThrowingProbe : IOmpAccountProbe
        {
            public Task<AccountSnapshot> InspectAsync(string exePath, CancellationToken ct) => throw new IOException("pipe broke");
            public Task<LoginResult> LoginAsync(string exePath, string providerId, LoginUi ui, CancellationToken ct) => throw new NotSupportedException();
            public void Dispose() { }
        }

        [Fact]
        public async Task Steps_report_checking_before_their_final_state_and_login_runs_again_without_rediscovering_omp()
        {
            int discoveries = 0;
            var probe = Probe(SetupFakes.Account(0, new LoginProvider("anthropic", "Anthropic", false)));
            probe.Snapshots.Enqueue(SetupFakes.Account(1, new LoginProvider("anthropic", "Anthropic", true)));
            using var runner = new AiSetupRunner(new AiSetupServices
            {
                CollectWebView = () => SetupFakes.WebView(),
                DiscoverOmp = _ => { discoveries++; return Task.FromResult(SetupFakes.OmpOk()); },
                CreateProbe = () => probe,
            }, false);
            var seen = new List<(SetupStepId, SetupState)>();
            runner.StepChanged += s => seen.Add((s.Id, s.State));

            await runner.RunAllAsync();
            Assert.True(seen.IndexOf((SetupStepId.Login, SetupState.Checking)) < seen.IndexOf((SetupStepId.Login, SetupState.Fail)));
            Assert.False(runner.AllOk);

            await runner.RunAccountAsync();   // 로그인 직후
            Assert.True(runner.AllOk);
            Assert.Equal(1, discoveries);
            Assert.Equal(2, probe.Inspections);
        }

        [Fact]
        public async Task Too_old_and_unrunnable_omp_get_download_and_browse_but_no_misleading_missing_text()
        {
            var tooOld = new OmpDiscoveryResult(Array.Empty<OmpLocation>(), @"C:\fake\omp.exe", Array.Empty<OmpCandidate>(), new OmpVersion(17, 0, 0), OmpProblemKind.TooOld, "");
            using var runner = Runner(() => SetupFakes.WebView(), () => tooOld, () => Probe(SetupFakes.Account()), ko: false);
            await runner.RunAllAsync();

            var omp = runner.Step(SetupStepId.Omp);
            Assert.Equal(SetupState.Fail, omp.State);
            Assert.Contains("too old", omp.Summary);
            Assert.Contains(omp.Fixes, f => f.Kind == SetupFixKind.DownloadOmp && f.Label.Contains("latest"));
            Assert.DoesNotContain("not found", omp.Summary);
        }
    }

    // ================================================================== 자동으로 여는 규칙

    public class AiSetupAutoShowTests
    {
        [Fact]
        public void The_assistant_shows_once_per_app_version_and_again_after_an_update()
        {
            var s = new AppSettings();
            Assert.True(AiSetupPolicy.ShouldAutoShow(s, "3.4.0", shownThisSession: false));

            AiSetupPolicy.MarkShown(s, "3.4.0");
            Assert.False(AiSetupPolicy.ShouldAutoShow(s, "3.4.0", false));
            Assert.True(AiSetupPolicy.ShouldAutoShow(s, "3.4.1", false));
        }

        [Fact]
        public void Dont_show_again_wins_over_a_new_version_and_can_be_undone()
        {
            var s = new AppSettings();
            AiSetupPolicy.SetDismissed(s, true);
            Assert.False(AiSetupPolicy.ShouldAutoShow(s, "9.9.9", false));

            AiSetupPolicy.SetDismissed(s, false);
            Assert.True(AiSetupPolicy.ShouldAutoShow(s, "9.9.9", false));
        }

        [Fact]
        public void A_second_failure_in_the_same_session_never_reopens_it()
        {
            Assert.False(AiSetupPolicy.ShouldAutoShow(new AppSettings(), "3.4.0", shownThisSession: true));
        }

        [Fact]
        public void The_shown_version_and_dismissal_survive_a_settings_round_trip()
        {
            var s = new AppSettings();
            AiSetupPolicy.MarkShown(s, "3.4.0");
            AiSetupPolicy.SetDismissed(s, true);
            s.Save();
            var back = AppSettings.Load();
            Assert.Equal("3.4.0", back.AiSetupShownVersion);
            Assert.True(back.AiSetupDisabled);
            new AppSettings().Save();   // 임시 설정 폴더를 되돌린다
        }
    }

    // ================================================================== 진단 정보 가리기

    public class AiDiagnosticsRedactionTests
    {
        private static DiagnosticRedactor Redactor() => new(@"C:\Users\우현지", "우현지");

        [Theory]
        [InlineData("key sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 end", "sk-ant-api03")]
        [InlineData("OPENAI_API_KEY=sk-proj-1234567890abcdefghij", "sk-proj")]
        [InlineData("Authorization: Bearer abcdef0123456789abcdef.tail", "abcdef0123456789")]
        [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV", "eyJhbGci")]
        [InlineData("{\"access_token\":\"ya29.A0ARrdaM-secretvalue\",\"x\":1}", "secretvalue")]
        [InlineData("https://auth.example.com/authorize?client_id=x&code_challenge=AAAAbbbb1111&state=zzzz9999", "AAAAbbbb1111")]
        [InlineData("omp --api-key hunter2hunter2 --model a/b", "hunter2")]
        [InlineData("--api-key=hunter2hunter2", "hunter2")]
        [InlineData("password: correct-horse-battery", "correct-horse")]
        [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdef")]
        [InlineData("AIzaSyA1234567890abcdefghijklmnopqrstuvw", "AIzaSyA1234")]
        [InlineData("blob a1B2c3D4e5F6g7H8i9J0k1L2m3N4o5P6q7R8s9T0 tail", "a1B2c3D4e5F6")]
        public void API_keys_tokens_and_authorization_values_are_masked(string line, string secret)
        {
            string result = Redactor().Redact(line);
            Assert.DoesNotContain(secret, result);
            Assert.Contains(DiagnosticRedactor.Mark, result);
        }

        [Fact]
        public void The_user_name_in_paths_becomes_the_user_profile_variable_in_every_spelling()
        {
            var r = Redactor();
            Assert.Equal(@"%USERPROFILE%\AppData\Local\omp\omp.exe", r.Redact(@"C:\Users\우현지\AppData\Local\omp\omp.exe"));
            Assert.Equal(@"%USERPROFILE%\Downloads", r.Redact(@"c:\users\우현지\Downloads"));                         // 대소문자
            Assert.Equal("exe=%USERPROFILE%\\\\AppData", r.Redact("exe=C:\\\\Users\\\\우현지\\\\AppData"));          // JSON 이스케이프
            Assert.Equal("%USERPROFILE%/Documents", r.Redact("C:/Users/우현지/Documents"));
        }

        [Fact]
        public void Other_users_folders_and_a_bare_user_name_are_masked_too()
        {
            var r = Redactor();
            Assert.Equal(@"D:\Users\<user>\data.csv", r.Redact(@"D:\Users\someone else\data.csv"));
            Assert.Equal("logged in as <user>.", r.Redact("logged in as 우현지."));
            Assert.Equal("우현지s stays", r.Redact("우현지s stays"));   // 낱말의 일부는 건드리지 않는다
        }

        [Fact]
        public void Ordinary_diagnostic_text_is_left_alone()
        {
            const string text = "WebView2 runtime : 149.0.4022.98\nmax_tokens=4096\nProblem : CompatLayer\n  [HKCU] C:\\Program Files (x86)\\Microsoft\\EdgeWebView\\msedgewebview2.exe = HIGHDPIAWARE <DPI>\nversion: 18.7.0 (minimum 18.4.4)";
            Assert.Equal(text, new DiagnosticRedactor("", "").Redact(text));
        }

        [Fact]
        public void The_built_report_has_every_section_masks_settings_and_never_leaks_the_profile_path()
        {
            var input = new AiDiagnosticsInput("3.4.0", "Windows 11 (build 26200)", "X64", "X64", ".NET 10", "ko-KR", 144,
                SetupFakes.WebView().ToDiagnosticText(), "omp discovery\n  executable: C:\\Users\\우현지\\AppData\\Local\\omp\\omp.exe", "State  : Ready\nRoot   : C:\\Users\\우현지\\AppData\\Local\\NanumCsvViewer\\python-analysis",
                new[]
                {
                    KeyValuePair.Create("AgentOmpPath", @"C:\Users\우현지\Downloads\omp.exe"),
                    KeyValuePair.Create("AgentExtraArgs", "--api-key sk-live-ABCDEFGHIJKLMNOP1234 --model x"),
                },
                "10:00 init failed 0x8007139F", "10:00:00.000 # omp rpc start exe=C:\\Users\\우현지\\omp.exe");
            string text = AiDiagnostics.Build(input, Redactor());

            foreach (string section in new[] { "App version      : 3.4.0", "Windows 11 (build 26200)", "Architecture     : OS X64", "Window DPI       : 144 (150%)",
                "-- WebView2 --", "-- omp --", "-- Python analysis environment --", "-- Agent settings --", "-- webview2-init.log (tail) --", "-- rpc.log (tail" })
                Assert.Contains(section, text);
            Assert.DoesNotContain("우현지", text);
            Assert.DoesNotContain("sk-live", text);
            Assert.Contains(@"AgentOmpPath = %USERPROFILE%\Downloads\omp.exe", text);
            Assert.Contains("0x8007139F", text);
        }

        [Fact]
        public void The_rpc_log_summary_keeps_frame_kinds_and_errors_but_drops_message_and_tool_contents()
        {
            var raw = new[]
            {
                "10:00:00.001 # NanumCsvViewer 3.4.0 omp 18.7.0 cwd=C:\\Users\\x",
                "10:00:00.002 > {\"id\":\"req-1\",\"type\":\"prompt\",\"message\":\"SECRET-CELL-VALUE 90210\"}",
                "10:00:00.003 < {\"type\":\"message_update\",\"delta\":\"SECRET-ANSWER\"}",
                "10:00:00.004 < {\"type\":\"agent_start\"}",
                "10:00:00.005 < {\"type\":\"response\",\"command\":\"get_available_models\",\"success\":false,\"error\":\"Model not found: x/y\"}",
                "10:00:00.006 < {\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"text\":\"SECRET-TEXT\"}}",
                "10:00:00.007 < {\"type\":\"host_tool_call\",\"toolName\":\"csv.rows\",\"arguments\":{\"v\":\"SECRET-ROW\"}}",
                "10:00:00.008 < not json at all SECRET-RAW",
                "10:00:00.010 < {\"type\":\"message_end\",\"message\":{\"content\":\"SECRET-BIG…[70000 chars]",
                "10:00:00.011 < {\"type\":\"message_update\",\"delta\":\"SECRET-BIG-DELTA…[70000 chars]",
                "10:00:00.009 ! not sent: frame is 2000000 bytes",
            };
            string joined = string.Join("\n", AiDiagnostics.SummarizeRpcLog(raw, 50));

            Assert.DoesNotContain("SECRET", joined);
            Assert.Contains("< agent_start", joined);
            Assert.Contains("response get_available_models FAILED error=Model not found: x/y", joined);
            Assert.Contains("response prompt ok", joined);
            Assert.Contains("host_tool_call csv.rows", joined);
            Assert.Contains("> command prompt", joined);
            Assert.Contains("# NanumCsvViewer 3.4.0", joined);
            Assert.Contains("! not sent", joined);
            Assert.DoesNotContain("message_update", joined);
            Assert.Contains("message_end (truncated frame)", joined);
            Assert.Contains("(unparsed frame)", joined);
        }

        [Fact]
        public void The_summary_and_file_tail_keep_only_the_last_lines()
        {
            var raw = Enumerable.Range(0, 100).Select(i => $"10:00:00.{i:000} < {{\"type\":\"evt{i}\"}}").ToList();
            var summary = AiDiagnostics.SummarizeRpcLog(raw, 5);
            Assert.Equal(5, summary.Count);
            Assert.EndsWith("evt99", summary[^1]);

            string file = Path.Combine(Path.GetTempPath(), "ncv-tail-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                File.WriteAllLines(file, Enumerable.Range(0, 1000).Select(i => "line " + i));
                using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // 다른 프로세스가 쓰는 중이어도 읽는다
                Assert.Equal(new[] { "line 997", "line 998", "line 999" }, AiDiagnostics.TailLines(file, 3));
            }
            finally { File.Delete(file); }
            Assert.Empty(AiDiagnostics.TailLines(file, 3));
        }
    }

    // ================================================================== 채팅 알림: 모델이 없을 때

    public class AiSetupEmptyModelsNoticeTests
    {
        private static void EmptyModels(FakeOmpProcess p) =>
            p.Handlers["get_available_models"] = cmd => FakeOmpProcess.Ok(cmd.Str("id"), "get_available_models", new JsonObject { ["models"] = new JsonArray() });

        [Fact]
        public async Task A_connected_agent_with_no_models_posts_a_sign_in_notice_that_opens_the_wizard_at_the_login_step()
        {
            using var rig = new ControllerRig(configure: EmptyModels);
            AgentSetupReason? reason = null;
            rig.OnUi(() => rig.Controller.SetupNeeded += (r, _) => reason = r);
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("url") == ChatController.SetupLoginUrl));

            var notice = rig.Page.Parsed("notice").Single(n => n.Str("url") == ChatController.SetupLoginUrl);
            Assert.Equal("warn", notice.Str("level"));
            Assert.Equal(ChatController.NoModelsNoticeText(false), notice.Str("text"));
            Assert.Equal("Sign in…", notice.Str("linkText"));

            rig.FromPage("{\"t\":\"openUrl\",\"url\":\"" + ChatController.SetupLoginUrl + "\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => reason) == AgentSetupReason.Login);
        }

        [Fact]
        public async Task The_notice_is_in_korean_for_a_korean_ui_and_repeats_only_after_models_appeared_in_between()
        {
            using var rig = new ControllerRig(new AgentHostOptions(Language: "ko", AppVersion: "1.2.3"), EmptyModels);
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("url") == ChatController.SetupLoginUrl));
            Assert.Equal(ChatController.NoModelsNoticeText(true), rig.Page.Parsed("notice").Single(n => n.Str("url") == ChatController.SetupLoginUrl).Str("text"));

            // 다시 모델을 물어도(로그인 뒤 새로 고침) 같은 알림을 또 쌓지 않는다.
            rig.FromPage("{\"t\":\"runCommand\",\"command\":\"/login\"}");
            await Task.Delay(150);
            Assert.Single(rig.Page.Parsed("notice"), n => n.Str("url") == ChatController.SetupLoginUrl);
        }

        [Fact]
        public async Task With_models_available_no_sign_in_notice_appears()
        {
            using var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.WaitForCountAsync("catalog", 2);
            await Task.Delay(100);

            Assert.DoesNotContain(rig.Page.Parsed("notice"), n => n.Str("url") == ChatController.SetupLoginUrl);
        }
    }

    // ================================================================== 로그인 RPC (가짜 omp)

    public class OmpAccountProbeTests
    {
        private static string Frame(JsonObject o) => o.ToJsonString();

        [Fact]
        public async Task Inspect_lists_providers_and_unique_models_and_starts_omp_in_plain_rpc_mode_without_a_session()
        {
            var factory = new FakeOmpFactory();
            using var ui = new TestUi();
            using var probe = new OmpRpcAccountProbe(factory, () => false);

            AccountSnapshot? snapshot = null;
            await ui.InvokeAsync(async () => snapshot = await probe.InspectAsync("C:\\fake\\omp.exe", CancellationToken.None));

            Assert.True(snapshot!.HandshakeOk);
            Assert.Equal(2, snapshot.ModelCount);   // 가짜 omp 기본 응답: 중복 제거 뒤 2개
            Assert.Equal("anthropic", Assert.Single(snapshot.Providers).Id);
            Assert.True(snapshot.Providers[0].Authenticated);
            Assert.Equal(2, snapshot.ProtocolVersion);
            var args = factory.Last.Launch.Arguments;
            Assert.Equal(new[] { "--mode", "rpc" }, args.Take(2));
            Assert.Contains("--no-session", args);
            Assert.DoesNotContain("rpc-ui", args);
            // 모델에는 아무것도 보내지 않는다.
            Assert.DoesNotContain(factory.Last.ReceivedSnapshot(), f => f.Str("type") is "prompt" or "steer" or "follow_up");
        }

        [Fact]
        public async Task A_start_failure_becomes_a_failed_snapshot_with_omps_last_stderr_line()
        {
            var factory = new FakeOmpFactory { Configure = p => { p.AutoReady = false; p.Crash(1, "bad cpu"); } };
            using var ui = new TestUi();
            using var probe = new OmpRpcAccountProbe(factory, () => false);

            AccountSnapshot? snapshot = null;
            await ui.InvokeAsync(async () => snapshot = await probe.InspectAsync("C:\\fake\\omp.exe", CancellationToken.None));

            Assert.False(snapshot!.HandshakeOk);
            Assert.False(string.IsNullOrEmpty(snapshot.Error));
        }

        [Fact]
        public async Task Login_forwards_the_authorization_url_and_progress_answers_input_prompts_and_marks_the_next_inspect_stale()
        {
            var factory = new FakeOmpFactory();
            factory.Configure = p => p.Handlers["login"] = cmd =>
            {
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-1", ["method"] = "open_url", ["url"] = "https://auth.example.com/o?x=1", ["instructions"] = "Approve access" }));
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-2", ["method"] = "notify", ["message"] = "Waiting for the browser" }));
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-3", ["method"] = "input", ["title"] = "Code", ["message"] = "Paste the code" }));
                return FakeOmpProcess.Ok(cmd.Str("id"), "login", new JsonObject());
            };
            using var ui = new TestUi();
            using var probe = new OmpRpcAccountProbe(factory, () => false);
            var urls = new List<(string, string?)>();
            var progress = new List<string>();
            var inputs = new List<(string, string)>();
            var loginUi = new LoginUi((u, i) => urls.Add((u, i)), progress.Add, (t, p, c) => { inputs.Add((t, p)); return Task.FromResult<string?>("the-code"); });

            LoginResult? result = null;
            await ui.InvokeAsync(async () => result = await probe.LoginAsync("C:\\fake\\omp.exe", "anthropic", loginUi, CancellationToken.None));

            Assert.True(result!.Ok);
            Assert.Equal(("https://auth.example.com/o?x=1", "Approve access"), Assert.Single(urls));
            Assert.Contains("Waiting for the browser", progress);
            Assert.Equal(("Code", "Paste the code"), Assert.Single(inputs));
            var login = factory.Last.ReceivedSnapshot().Single(f => f.Str("type") == "login");
            Assert.Equal("anthropic", login.Str("providerId"));
            var answer = await factory.Last.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "ui-3");
            Assert.Equal("the-code", answer.Str("value"));

            // 로그인 뒤 첫 검사는 새 프로세스에서 새 자격 증명을 읽는다.
            AccountSnapshot? after = null;
            await ui.InvokeAsync(async () => after = await probe.InspectAsync("C:\\fake\\omp.exe", CancellationToken.None));
            Assert.True(after!.HandshakeOk);
            Assert.Equal(2, factory.Processes.Count);
        }

        [Fact]
        public async Task Non_http_urls_are_never_opened_and_a_cancelled_input_answers_cancelled()
        {
            var factory = new FakeOmpFactory();
            factory.Configure = p => p.Handlers["login"] = cmd =>
            {
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-1", ["method"] = "open_url", ["url"] = "file:///C:/Windows/System32/calc.exe" }));
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-2", ["method"] = "input", ["title"] = "Code" }));
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-3", ["method"] = "confirm", ["title"] = "Sure?" }));
                return FakeOmpProcess.Ok(cmd.Str("id"), "login", new JsonObject());
            };
            using var ui = new TestUi();
            using var probe = new OmpRpcAccountProbe(factory, () => false);
            var urls = new List<string>();

            await ui.InvokeAsync(async () => await probe.LoginAsync("C:\\fake\\omp.exe", "x", new LoginUi((u, _) => urls.Add(u), _ => { }, (_, _, _) => Task.FromResult<string?>(null)), CancellationToken.None));

            Assert.Empty(urls);
            var cancelled = await factory.Last.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "ui-2");
            Assert.True(cancelled.Bool("cancelled"));
            var confirm = await factory.Last.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "ui-3");
            Assert.True(confirm.Bool("cancelled"));
        }

        [Fact]
        public async Task A_secret_prompt_rejection_asks_for_the_terminal_and_other_failures_do_not()
        {
            var factory = new FakeOmpFactory();
            factory.Configure = p =>
            {
                p.Handlers["login"] = cmd => cmd.Str("providerId") == "openrouter"
                    ? FakeOmpProcess.Fail(cmd.Str("id"), "login", "Provider asked for a secret input; use the terminal UI.")
                    : FakeOmpProcess.Fail(cmd.Str("id"), "login", "Access denied by the user");
            };
            using var ui = new TestUi();
            using var probe = new OmpRpcAccountProbe(factory, () => true);
            var noop = new LoginUi((_, _) => { }, _ => { }, (_, _, _) => Task.FromResult<string?>(null));

            LoginResult? secret = null, denied = null;
            await ui.InvokeAsync(async () =>
            {
                secret = await probe.LoginAsync("C:\\fake\\omp.exe", "openrouter", noop, CancellationToken.None);
                denied = await probe.LoginAsync("C:\\fake\\omp.exe", "anthropic", noop, CancellationToken.None);
            });

            Assert.False(secret!.Ok);
            Assert.True(secret.NeedsTerminal);
            Assert.Contains("omp login", secret.Message);
            Assert.False(denied!.Ok);
            Assert.False(denied.NeedsTerminal);
            Assert.Equal("Access denied by the user", denied.Message);
        }

        [Fact]
        public async Task Cancelling_a_pending_login_stops_the_probe_process_and_throws()
        {
            var factory = new FakeOmpFactory();
            factory.Configure = p => p.Handlers["login"] = _ => "";   // 응답 없음: 사용자가 브라우저에서 끝내기를 기다리는 중
            using var ui = new TestUi();
            using var probe = new OmpRpcAccountProbe(factory, () => false);
            using var cts = new CancellationTokenSource();

            Task? pending = null;
            ui.Invoke(() => pending = probe.LoginAsync("C:\\fake\\omp.exe", "anthropic", new LoginUi((_, _) => { }, _ => { }, (_, _, _) => Task.FromResult<string?>(null)), cts.Token));
            await OmpRpcClientTests.WaitUntilAsync(() => factory.Processes.Count > 0);
            await factory.Last.WaitForTypeAsync("login");
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending!);
            await OmpRpcClientTests.WaitUntilAsync(() => factory.Last.Killed);
        }

        [Fact]
        public async Task An_input_prompt_that_omp_cancels_or_that_outlives_the_login_is_closed_without_answering_it()
        {
            var factory = new FakeOmpFactory();
            factory.Configure = p => p.Handlers["login"] = cmd =>
            {
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-1", ["method"] = "input", ["title"] = "Code", ["message"] = "Paste the code" }));
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-9", ["method"] = "cancel", ["targetId"] = "ui-1" }));
                p.Emit(Frame(new JsonObject { ["type"] = "extension_ui_request", ["id"] = "ui-2", ["method"] = "input", ["title"] = "Code", ["message"] = "Again" }));
                return FakeOmpProcess.Ok(cmd.Str("id"), "login", new JsonObject());   // 두 번째 입력 칸은 열린 채 로그인이 끝난다
            };
            using var ui = new TestUi();
            using var probe = new OmpRpcAccountProbe(factory, () => false);
            var closed = new List<string>();
            var loginUi = new LoginUi((_, _) => { }, _ => { }, async (title, prompt, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { lock (closed) closed.Add(prompt); }
                return "late-answer";   // 토큰이 취소된 뒤의 값은 보내지 않는다
            });

            LoginResult? result = null;
            await ui.InvokeAsync(async () => result = await probe.LoginAsync("C:\\fake\\omp.exe", "anthropic", loginUi, CancellationToken.None));
            await OmpRpcClientTests.WaitUntilAsync(() => { lock (closed) return closed.Count == 2; });
            await Task.Delay(100);

            Assert.True(result!.Ok);
            Assert.Equal(new[] { "Paste the code", "Again" }, closed.OrderBy(x => x == "Again").ToArray());
            Assert.DoesNotContain(factory.Last.ReceivedSnapshot(), f => f.Str("type") == "extension_ui_response");
        }
    }

    // ================================================================== 도우미 창 (가짜 환경, 화면 없이 보이지 않게 띄움)

    public class AiSetupWizardFormTests
    {
        private sealed class Rig
        {
            public FakeSetupOps Ops { get; } = new();
            public AppSettings Settings { get; } = new();
            public int Saves;
            public Func<WebViewReport> Web = () => SetupFakes.WebView();
            public Func<OmpDiscoveryResult> Omp = () => SetupFakes.OmpOk();
            public FakeAccountProbe Probe { get; } = new();
            public string Diagnostics = "DIAG-TEXT\nline2";
            public List<string> Events { get; } = new();
            public AiSetupWizard Wizard = null!;

            public Rig(AccountSnapshot? account = null, bool ko = false, SetupStepId? focus = null, Action<AppSettings>? seed = null)
            {
                seed?.Invoke(Settings);
                Probe.Snapshots.Enqueue(account ?? SetupFakes.Account(3));
                var services = new AiSetupServices { CollectWebView = () => Web(), DiscoverOmp = _ => Task.FromResult(Omp()), CreateProbe = () => Probe };
                Wizard = new AiSetupWizard(new AiSetupRunner(services, ko), Ops, Settings, ThemePalette.Light, ko,
                    _ => Task.FromResult(Diagnostics), s => Saves++, focus)
                {
                    Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), WebViewRetryWait = TimeSpan.Zero,
                };
                Wizard.OmpPathChosen += p => Events.Add("path:" + p);
                Wizard.OmpInstalled += p => Events.Add("installed:" + p);
                Wizard.CredentialsChanged += () => Events.Add("credentials");
                Wizard.WebViewRetryRequested += () => Events.Add("webview-retry");
            }

            public AiSetupWizard.StepView View(SetupStepId id) => Wizard.Views[(int)id];
            public Button Fix(SetupStepId id, SetupFixKind kind) => View(id).FixButtons.Single(b => b.Text == View(id).Current.Fixes.Single(f => f.Kind == kind).Label);
        }

        private static void OnSta(Action body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    body();
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 20)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { Application.DoEvents(); Thread.Sleep(2); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());   // DoEvents가 컨텍스트를 걷어 가므로 다시 건다
            Assert.True(condition(), "timed out waiting for: " + what);
        }

        private static void Settled(Rig rig) =>
            PumpUntil(() => !rig.Wizard.Busy && rig.Wizard.Views.All(v => v.Current.State is not (SetupState.Pending or SetupState.Checking)), "all steps settled");

        private static void Idle(Rig rig) => PumpUntil(() => !rig.Wizard.Busy, "wizard idle");

        private static void Open(Rig rig)
        {
            rig.Wizard.Show();
            Settled(rig);
        }

        [Fact]
        public void The_login_shows_a_non_modal_input_box_for_the_pasted_code_and_a_button_to_copy_the_address()
        {
            OnSta(() =>
            {
                var rig = new Rig(SetupFakes.Account(0, new LoginProvider("openrouter", "OpenRouter", false)));
                string? pasted = null;
                rig.Probe.OnLoginAsync = async (id, ui, ct) =>
                {
                    ui.OpenUrl("https://openrouter.ai/auth?x=1", "Approve access");
                    pasted = await ui.AskInput("Code", "Paste the authorization code", ct);
                    return new LoginResult(pasted != null, "Signed in.", false);
                };
                using var wizard = rig.Wizard;
                Open(rig);
                var login = rig.View(SetupStepId.Login);
                Assert.False(login.InputVisible);

                login.ProviderButtons.Single().PerformClick();
                PumpUntil(() => login.InputVisible, "input box shown");

                Assert.Equal("https://openrouter.ai/auth?x=1", Assert.Single(rig.Ops.Urls));   // 브라우저는 먼저 열린다
                Assert.True(login.CopyUrlButton.Visible);
                login.CopyUrlButton.PerformClick();
                Assert.Equal("https://openrouter.ai/auth?x=1", rig.Ops.Clipboard);

                login.InputOk.PerformClick();                      // 빈 칸은 제출되지 않는다
                Assert.True(login.InputVisible);
                login.InputBox.Text = "  the-code  ";
                login.InputOk.PerformClick();
                Settled(rig);

                Assert.Equal("the-code", pasted);
                Assert.False(login.InputVisible);
                Assert.Contains("credentials", rig.Events);
            });
        }

        [Fact]
        public void Opening_on_a_healthy_pc_shows_four_green_ticks_and_no_fix_buttons()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                Open(rig);

                foreach (var id in Enum.GetValues<SetupStepId>())
                {
                    Assert.Equal("\u2714", rig.View(id).IconText);
                    Assert.Empty(rig.View(id).FixButtons);
                }
                Assert.Equal("Everything is ready.", wizard.StatusText);
                Assert.Equal(1, rig.Probe.Inspections);   // 로그인·연결 두 단계가 omp를 한 번만 띄운다
                Assert.True(rig.View(SetupStepId.Login).ProviderButtons.Count == 1);   // 로그인 제공자 목록은 항상 보인다
            });
        }

        [Fact]
        public void Browse_saves_the_chosen_path_through_the_event_and_rechecks_the_rest_of_the_steps()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                var current = SetupFakes.OmpMissing();
                rig.Omp = () => current;
                Open(rig);
                Assert.Equal("\u2716", rig.View(SetupStepId.Omp).IconText);
                Assert.Equal("\u2013", rig.View(SetupStepId.Login).IconText);   // 막힘

                rig.Ops.PickedOmp = @"D:\tools\omp.exe";
                wizard.OmpPathChosen += _ => current = SetupFakes.OmpOk(@"D:\tools\omp.exe");   // 호스트가 설정에 저장한 효과
                rig.Fix(SetupStepId.Omp, SetupFixKind.BrowseOmp).PerformClick();
                Settled(rig);

                Assert.Contains(@"path:D:\tools\omp.exe", rig.Events);
                Assert.Equal("\u2714", rig.View(SetupStepId.Omp).IconText);
                Assert.Equal("\u2714", rig.View(SetupStepId.Connection).IconText);
            });
        }

        [Fact]
        public void Cancelling_the_file_dialog_changes_nothing()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                rig.Omp = () => SetupFakes.OmpMissing();
                Open(rig);

                rig.Ops.PickedOmp = null;
                rig.Fix(SetupStepId.Omp, SetupFixKind.BrowseOmp).PerformClick();
                Idle(rig);

                Assert.Empty(rig.Events);
            });
        }

        [Fact]
        public void Installing_a_found_candidate_copies_it_after_confirmation_honouring_the_path_checkbox_then_rechecks()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                var candidate = new OmpCandidate(@"C:\Users\tester\Downloads\omp-windows-x64.exe", "Downloads folder");
                var current = SetupFakes.OmpMissing(candidate);
                rig.Omp = () => current;
                Open(rig);
                Assert.True(rig.View(SetupStepId.Omp).AddToPath.Visible);
                rig.View(SetupStepId.Omp).AddToPath.Checked = true;
                rig.Ops.OnInstall = s => { current = SetupFakes.OmpOk(); return new OmpInstallResult(true, @"C:\fake\omp.exe", "installed", OmpInstallError.None, SetupFakes.Version); };

                rig.Fix(SetupStepId.Omp, SetupFixKind.InstallCandidate).PerformClick();
                Settled(rig);

                Assert.Equal((candidate.Path, true), Assert.Single(rig.Ops.Installed));
                Assert.Contains(candidate.Path, rig.Ops.Confirms.Single());
                Assert.Contains(@"installed:C:\fake\omp.exe", rig.Events);
                Assert.True(wizard.Views.All(v => v.Current.State == SetupState.Ok));
            });
        }

        [Fact]
        public void Declining_the_install_confirmation_and_a_failed_install_leave_omp_unfixed()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                var candidate = new OmpCandidate(@"C:\Users\tester\Downloads\omp-windows-x64.exe", "Downloads folder");
                rig.Omp = () => SetupFakes.OmpMissing(candidate);
                Open(rig);

                rig.Ops.ConfirmAnswer = false;
                rig.Fix(SetupStepId.Omp, SetupFixKind.InstallCandidate).PerformClick();
                Idle(rig);
                Assert.Empty(rig.Ops.Installed);

                rig.Ops.ConfirmAnswer = true;
                rig.Ops.OnInstall = _ => new OmpInstallResult(false, null, "The file is in use.", OmpInstallError.InUse);
                rig.Fix(SetupStepId.Omp, SetupFixKind.InstallCandidate).PerformClick();
                Idle(rig);

                Assert.Single(rig.Ops.Installed);
                Assert.Contains("The file is in use.", rig.Ops.Messages);
                Assert.DoesNotContain(rig.Events, e => e.StartsWith("installed"));
                Assert.Equal(SetupState.Fail, rig.View(SetupStepId.Omp).Current.State);
            });
        }

        [Fact]
        public void Download_asks_first_then_installs_and_rechecks()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                var current = SetupFakes.OmpMissing();
                rig.Omp = () => current;
                Open(rig);
                rig.Ops.OnInstall = _ => { current = SetupFakes.OmpOk(); return new OmpInstallResult(true, @"C:\fake\omp.exe", "installed", OmpInstallError.None, SetupFakes.Version); };

                rig.Ops.ConfirmAnswer = false;
                rig.Fix(SetupStepId.Omp, SetupFixKind.DownloadOmp).PerformClick();
                Idle(rig);
                Assert.Equal(0, rig.Ops.Downloads);   // 확인 없이는 내려받지 않는다

                rig.Ops.ConfirmAnswer = true;
                rig.Fix(SetupStepId.Omp, SetupFixKind.DownloadOmp).PerformClick();
                Settled(rig);

                Assert.Equal(1, rig.Ops.Downloads);
                Assert.False(rig.Ops.DownloadAddToPath);
                Assert.True(wizard.Views.All(v => v.Current.State == SetupState.Ok));
            });
        }

        [Fact]
        public void Removing_a_compat_layer_confirms_names_the_backup_removes_only_that_entry_and_retries_the_chat_screen()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                var layer = SetupFakes.Layer();
                bool removed = false;
                rig.Web = () => removed ? SetupFakes.WebView() : SetupFakes.WebView(WebViewProblem.CompatLayer, layers: new[] { layer }, hresult: unchecked((int)0x8007139F));
                rig.Ops.OnRemove = e => { removed = true; return new RemovalResult(true, "removed", @"C:\backup\appcompat.reg"); };
                Open(rig);
                Assert.Equal("\u2716", rig.View(SetupStepId.WebView).IconText);

                rig.Fix(SetupStepId.WebView, SetupFixKind.RemoveCompatLayer).PerformClick();
                Settled(rig);

                var (entry, backup) = Assert.Single(rig.Ops.Removed);
                Assert.Same(layer, entry);
                Assert.Equal(@"C:\backup\appcompat.reg", backup);
                string question = rig.Ops.Confirms.Single();
                Assert.Contains(layer.ExePath, question);
                Assert.Contains(@"C:\backup\appcompat.reg", question);   // 백업 위치를 먼저 알린다
                Assert.Contains("webview-retry", rig.Events);
                Assert.Equal("\u2714", rig.View(SetupStepId.WebView).IconText);
            });
        }

        [Fact]
        public void A_declined_removal_touches_nothing_and_a_failed_removal_reports_the_error()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                rig.Web = () => SetupFakes.WebView(WebViewProblem.CompatLayer, layers: new[] { SetupFakes.Layer() }, hresult: 5);
                Open(rig);

                rig.Ops.ConfirmAnswer = false;
                rig.Fix(SetupStepId.WebView, SetupFixKind.RemoveCompatLayer).PerformClick();
                Idle(rig);
                Assert.Empty(rig.Ops.Removed);

                rig.Ops.ConfirmAnswer = true;
                rig.Ops.OnRemove = _ => new RemovalResult(false, "Access denied", null);
                rig.Fix(SetupStepId.WebView, SetupFixKind.RemoveCompatLayer).PerformClick();
                Settled(rig);

                Assert.Contains("Access denied", rig.Ops.Messages);
                Assert.DoesNotContain("webview-retry", rig.Events);
                Assert.Equal(SetupState.Fail, rig.View(SetupStepId.WebView).Current.State);
            });
        }

        [Fact]
        public void The_runtime_missing_fix_opens_the_install_page_and_the_log_fix_opens_the_log_folder()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                rig.Web = () => SetupFakes.WebView(WebViewProblem.RuntimeMissing, runtime: null);
                Open(rig);

                rig.Fix(SetupStepId.WebView, SetupFixKind.InstallWebView).PerformClick();
                Assert.Equal(WebViewDiagnostics.RuntimeInstallUrl, Assert.Single(rig.Ops.Urls));

                rig.Web = () => SetupFakes.WebView(WebViewProblem.Other, hresult: 1);
                rig.Fix(SetupStepId.WebView, SetupFixKind.RetryWebView).PerformClick();
                Settled(rig);
                rig.Fix(SetupStepId.WebView, SetupFixKind.OpenWebViewLog).PerformClick();
                Assert.Equal(Path.GetDirectoryName(WebViewDiagnostics.LogPath), Assert.Single(rig.Ops.Folders));
            });
        }

        [Fact]
        public void Signing_in_runs_the_login_then_tells_the_host_and_rechecks_so_the_models_turn_green()
        {
            OnSta(() =>
            {
                var rig = new Rig(SetupFakes.Account(0, new LoginProvider("anthropic", "Anthropic", false), new LoginProvider("openai-codex", "ChatGPT", false)));
                rig.Probe.Snapshots.Enqueue(SetupFakes.Account(4, new LoginProvider("anthropic", "Anthropic", true), new LoginProvider("openai-codex", "ChatGPT", false)));
                using var wizard = rig.Wizard;
                Open(rig);
                Assert.Equal("\u2716", rig.View(SetupStepId.Login).IconText);
                var buttons = rig.View(SetupStepId.Login).ProviderButtons;
                Assert.Equal(2, buttons.Count);

                buttons[0].PerformClick();
                Settled(rig);
                PumpUntil(() => rig.View(SetupStepId.Login).LoginMessageText.Length > 0, "login result shown");

                Assert.Equal("anthropic", Assert.Single(rig.Probe.LoggedIn));
                Assert.Contains("credentials", rig.Events);
                Assert.Equal("\u2714", rig.View(SetupStepId.Login).IconText);
                Assert.Contains("4", rig.View(SetupStepId.Login).SummaryText);
                Assert.Equal("Signed in.", rig.View(SetupStepId.Login).LoginMessageText);
            });
        }

        [Fact]
        public void A_provider_that_needs_an_api_key_explains_it_and_offers_the_terminal_for_that_provider()
        {
            OnSta(() =>
            {
                var rig = new Rig(SetupFakes.Account(0, new LoginProvider("openrouter", "OpenRouter", false)));
                rig.Probe.OnLogin = _ => new LoginResult(false, "Needs a secret; use `omp login`.", NeedsTerminal: true);
                using var wizard = rig.Wizard;
                Open(rig);

                rig.View(SetupStepId.Login).ProviderButtons.Single().PerformClick();
                Settled(rig);
                PumpUntil(() => rig.View(SetupStepId.Login).LoginMessageText.Length > 0, "login result shown");

                Assert.DoesNotContain("credentials", rig.Events);
                Assert.Contains("omp login", rig.View(SetupStepId.Login).LoginMessageText);
                Assert.True(rig.View(SetupStepId.Login).TerminalButtonVisible);

                rig.View(SetupStepId.Login).TerminalButton.PerformClick();
                Assert.Equal((@"C:\fake\omp.exe", "openrouter"), Assert.Single(rig.Ops.Terminals));
            });
        }

        [Fact]
        public void The_terminal_fix_on_the_login_step_runs_omp_login_for_the_found_executable()
        {
            OnSta(() =>
            {
                var rig = new Rig(SetupFakes.Account(0, new LoginProvider("anthropic", "Anthropic", false)));
                using var wizard = rig.Wizard;
                Open(rig);

                rig.Fix(SetupStepId.Login, SetupFixKind.OpenTerminalLogin).PerformClick();

                Assert.Equal((@"C:\fake\omp.exe", (string?)null), Assert.Single(rig.Ops.Terminals));
            });
        }

        [Fact]
        public void Dont_show_again_is_saved_immediately_and_the_checkbox_starts_from_the_setting()
        {
            OnSta(() =>
            {
                var rig = new Rig(seed: s => s.AiSetupDisabled = true);
                var again = new Rig();
                using var wizard = rig.Wizard;
                using var other = again.Wizard;
                var box = wizard.DontShowAgain;
                Assert.True(box.Checked);

                var box2 = other.DontShowAgain;
                Assert.False(box2.Checked);
                box2.Checked = true;
                Assert.True(again.Settings.AiSetupDisabled);
                Assert.Equal(1, again.Saves);
            });
        }

        [Fact]
        public void Copy_diagnostics_puts_the_report_on_the_clipboard_and_save_as_writes_the_same_text()
        {
            OnSta(() =>
            {
                var rig = new Rig();
                using var wizard = rig.Wizard;
                Open(rig);

                // 아래 두 호출은 버튼이 부르는 같은 경로다.
                var t = wizard.CopyDiagnosticsAsync();
                PumpUntil(() => t.IsCompleted, "copy finished");
                Assert.Equal("DIAG-TEXT\nline2", rig.Ops.Clipboard);
                Assert.Contains("2 lines", wizard.StatusText);

                rig.Ops.SavePath = @"C:\out\diag.txt";
                var s = wizard.SaveDiagnosticsAsync();
                PumpUntil(() => s.IsCompleted, "save finished");
                Assert.Equal("DIAG-TEXT\nline2", rig.Ops.Files[@"C:\out\diag.txt"]);
            });
        }

        [Fact]
        public void The_wizard_follows_the_ui_language_and_keeps_every_label_bilingual()
        {
            OnSta(() =>
            {
                var ko = new Rig(ko: true);
                var en = new Rig(ko: false);
                using var wk = ko.Wizard;
                using var we = en.Wizard;
                ko.Omp = () => SetupFakes.OmpMissing();
                en.Omp = () => SetupFakes.OmpMissing();
                Open(ko);
                Open(en);

                Assert.Equal("AI 환경 설정 도우미", wk.Text);
                Assert.Equal("AI Setup Assistant", we.Text);
                Assert.Contains("찾아보기…", ko.View(SetupStepId.Omp).FixButtons.Select(b => b.Text));
                Assert.Contains("Browse…", en.View(SetupStepId.Omp).FixButtons.Select(b => b.Text));
            });
        }
    }

    // ================================================================== Form1: 메뉴·자동으로 열기·진단 정보 복사

    [Collection("SavedViewStore")]
    public class AiSetupFormHostTests
    {
        private static void OnForm(Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
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

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 30)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { Application.DoEvents(); Thread.Sleep(2); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Assert.True(condition(), "timed out waiting for: " + what);
        }

        private static IEnumerable<ToolStripMenuItem> Items(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is not ToolStripMenuItem m) continue;
                yield return m;
                foreach (var child in Items(m.DropDownItems)) yield return child;
            }
        }

        private static void UseFakes(Form1 form, out FakeSetupOps ops)
        {
            ops = new FakeSetupOps();
            var probe = new FakeAccountProbe();
            probe.Snapshots.Enqueue(SetupFakes.Account(2));
            form.AiSetupOpsOverride = ops;
            form.AiSetupServicesOverride = new AiSetupServices
            {
                CollectWebView = () => SetupFakes.WebView(),
                DiscoverOmp = _ => Task.FromResult(SetupFakes.OmpOk()),
                CreateProbe = () => probe,
            };
        }

        [Fact]
        public void The_tools_and_help_menus_have_the_assistant_and_the_diagnostics_item_and_the_tools_item_opens_the_wizard_once()
        {
            OnForm(form =>
            {
                UseFakes(form, out _);
                var tools = form.TopLevelMenus.Single(m => m.Name == "toolsMenu");
                var help = form.TopLevelMenus.Single(m => m.Name == "helpMenu");
                var setup = Items(tools.DropDownItems).Single(i => i.Text is "AI Setup Assistant…" or "AI 환경 설정 도우미…");
                Assert.Single(Items(help.DropDownItems), i => i.Text is "Copy AI Diagnostics" or "AI 환경 진단 정보 복사");

                setup.PerformClick();
                var first = form.AiSetupWindow;
                Assert.NotNull(first);
                setup.PerformClick();
                Assert.Same(first, form.AiSetupWindow);   // 두 번째 클릭은 같은 창을 앞으로
                first!.Close();
                Assert.Null(form.AiSetupWindow);
            });
        }

        [Fact]
        public void Auto_show_opens_once_per_version_records_it_and_respects_dont_show_again()
        {
            OnForm(form =>
            {
                UseFakes(form, out _);
                var settings = form.AppSettingsRef;
                settings.AiSetupShownVersion = "";
                settings.AiSetupDisabled = false;

                Assert.True(form.TryAutoShowAiSetup(SetupStepId.Omp, requireVisible: false));
                Assert.Equal(AppInfo.Version, settings.AiSetupShownVersion);
                PumpUntil(() => form.AiSetupWindow is not null, "wizard opened");
                form.AiSetupWindow!.Close();

                Assert.False(form.TryAutoShowAiSetup(SetupStepId.WebView, requireVisible: false));   // 같은 실행·같은 버전: 다시 열지 않는다
                Assert.Null(form.AiSetupWindow);

                settings.AiSetupDisabled = true;
                Assert.False(form.TryAutoShowAiSetup(SetupStepId.Omp, requireVisible: false));
                Assert.False(form.TryAutoShowAiSetup(SetupStepId.Omp));   // 보이지 않는 창은 기본으로 열지 않는다
            });
        }

        [Fact]
        public void The_ai_settings_page_has_an_assistant_button_that_opens_it_modally_and_refreshes_the_omp_path_box()
        {
            OnForm(form =>
            {
                UseFakes(form, out _);
                using var dlg = new SettingsDialog(form, "ai");
                dlg.Show(form);
                string label = Loc.CurrentLanguage == "ko" ? "AI 환경 설정 도우미…" : "AI Setup Assistant…";
                var button = FindControls(dlg).OfType<Button>().Single(b => b.Text == label);

                bool seen = false, modal = false;
                var timer = new System.Windows.Forms.Timer { Interval = 80 };
                timer.Tick += (_, _) =>
                {
                    if (form.AiSetupWindow is not { Visible: true } wizard) return;
                    timer.Stop();
                    seen = true;
                    modal = wizard.Modal;
                    form.AppSettingsRef.AgentOmpPath = @"D:\picked\omp.exe";   // 도우미 안에서 찾아보기로 바뀐 것처럼
                    wizard.Close();
                };
                timer.Start();
                try { button.PerformClick(); }
                finally { timer.Stop(); timer.Dispose(); }

                Assert.True(seen);
                Assert.True(modal);   // 설정 대화 상자 위에서는 모달(모달 안에서 모덜리스 창은 쓸 수 없다)
                var pathBox = FindControls(dlg).OfType<TextBox>().Single(t => t.Text == @"D:\picked\omp.exe");
                Assert.NotNull(pathBox);
                dlg.Close();
            });
        }

        private static IEnumerable<Control> FindControls(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var child in FindControls(c)) yield return child;
            }
        }

        [Fact]
        public void The_chat_notice_button_opens_the_wizard_at_the_login_step_even_when_auto_show_is_off()
        {
            OnForm(form =>
            {
                UseFakes(form, out _);
                form.AppSettingsRef.AiSetupDisabled = true;
                var method = typeof(Form1).GetMethod("OnAgentSetupNeeded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

                method.Invoke(form, new object[] { AgentSetupReason.Login, "" });
                PumpUntil(() => form.AiSetupWindow is not null, "wizard opened at login");
                form.AiSetupWindow!.Close();
            });
        }

        [Fact]
        public void Copy_diagnostics_copies_a_masked_report_and_offers_to_save_it()
        {
            OnForm(form =>
            {
                UseFakes(form, out var ops);
                ops.ConfirmAnswer = true;
                ops.SavePath = @"C:\out\diag.txt";

                var task = form.CopyAiDiagnosticsAsync();
                PumpUntil(() => task.IsCompleted, "diagnostics collected", 60);
                string text = task.Result;

                Assert.Equal(text, ops.Clipboard);
                Assert.Equal(text, ops.Files[@"C:\out\diag.txt"]);
                Assert.Contains("== Nanum CSV Viewer AI diagnostics ==", text);
                Assert.Contains("App version      : " + AppInfo.Version, text);
                Assert.Contains("-- WebView2 --", text);
                Assert.Contains("-- omp --", text);
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"Users\" + Environment.UserName, text, StringComparison.OrdinalIgnoreCase);

                ops.ConfirmAnswer = false;
                ops.Files.Clear();
                var again = form.CopyAiDiagnosticsAsync();
                PumpUntil(() => again.IsCompleted, "second copy", 60);
                Assert.Empty(ops.Files);   // 저장을 거절하면 파일을 쓰지 않는다
            });
        }
    }
}
