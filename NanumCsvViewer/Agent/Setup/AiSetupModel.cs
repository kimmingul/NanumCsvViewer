using System.Text;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Setup
{
    /// <summary>AI 설정 도우미의 네 단계. 값이 곧 화면 순서다.</summary>
    internal enum SetupStepId { WebView = 0, Omp = 1, Login = 2, Connection = 3 }

    internal enum SetupState
    {
        /// <summary>아직 검사하지 않음.</summary>
        Pending,
        Checking,
        Ok,
        /// <summary>동작은 하지만 문제가 될 수 있음.</summary>
        Warn,
        Fail,
        /// <summary>앞 단계가 해결돼야 검사할 수 있음.</summary>
        Blocked,
    }

    internal enum SetupFixKind
    {
        InstallWebView,
        RemoveCompatLayer,
        RetryWebView,
        OpenWebViewLog,
        BrowseOmp,
        InstallCandidate,
        DownloadOmp,
        OpenTerminalLogin,
        Recheck,
    }

    /// <param name="Arg">RemoveCompatLayer는 <see cref="CompatLayerEntry"/>, InstallCandidate는 <see cref="OmpCandidate"/>.</param>
    internal sealed record SetupFix(SetupFixKind Kind, string Label, object? Arg = null, string? Tooltip = null);

    internal sealed record SetupStep(SetupStepId Id, SetupState State, string Summary, string? Detail, IReadOnlyList<SetupFix> Fixes)
    {
        public bool IsProblem => State is SetupState.Fail or SetupState.Warn;
        public static IReadOnlyList<SetupFix> NoFixes { get; } = Array.Empty<SetupFix>();
    }

    internal sealed record LoginProvider(string Id, string Name, bool Authenticated);

    /// <summary>omp를 짧은 RPC 프로세스로 띄워 본 결과(모델 호출 없음).</summary>
    internal sealed record AccountSnapshot(bool HandshakeOk, int ProtocolVersion, int ElapsedMs, string? Error,
        IReadOnlyList<LoginProvider> Providers, int ModelCount)
    {
        public bool HasModels => ModelCount > 0;
        public static AccountSnapshot Failed(string error, int elapsedMs = 0) =>
            new(false, 0, elapsedMs, error, Array.Empty<LoginProvider>(), 0);
    }

    internal sealed record LoginResult(bool Ok, string Message, bool NeedsTerminal);

    /// <summary>단계 평가(순수 함수). 화면·테스트가 같은 규칙을 쓴다.</summary>
    internal static class AiSetupEvaluator
    {
        private static string T(bool ko, string en, string kr) => ko ? kr : en;

        // ---- 1. WebView2 ----------------------------------------------------------------------------------------

        public static SetupStep EvaluateWebView(WebViewReport r, bool ko)
        {
            var fixes = new List<SetupFix>();
            var dpiLayers = r.CompatLayers.Where(l => l.DpiRelated).ToList();
            bool layerCause = r.Problem == WebViewProblem.CompatLayer;

            if (r.RuntimeVersion is null || r.Problem == WebViewProblem.RuntimeMissing)
            {
                fixes.Add(new SetupFix(SetupFixKind.InstallWebView, T(ko, "Open the runtime download page", "런타임 설치 페이지 열기"), WebViewDiagnostics.RuntimeInstallUrl));
                fixes.Add(new SetupFix(SetupFixKind.RetryWebView, T(ko, "Retry", "다시 시도")));
                return new SetupStep(SetupStepId.WebView, SetupState.Fail,
                    T(ko, "The Microsoft Edge WebView2 runtime is not installed.", "Microsoft Edge WebView2 런타임이 이 PC에 설치되어 있지 않습니다."),
                    T(ko, "The AI chat screen needs the Evergreen runtime. Install it, then run the check again.", "AI 채팅 화면에는 Evergreen 런타임이 필요합니다. 설치한 뒤 다시 검사하세요."), fixes);
            }

            if (dpiLayers.Count > 0)
            {
                foreach (var l in dpiLayers.Where(l => l.Removable))
                    fixes.Add(new SetupFix(SetupFixKind.RemoveCompatLayer,
                        T(ko, "Remove the DPI compatibility setting", "DPI 호환성 설정 해제") + (dpiLayers.Count > 1 ? " (" + Path.GetFileName(l.ExePath) + ")" : ""),
                        l, l.ExePath + " = " + l.Flags));
                var sb = new StringBuilder();
                sb.Append(T(ko,
                    "A Windows compatibility setting forces the DPI mode of an app the chat screen depends on. This can stop the chat screen from starting (error 0x8007139F).",
                    "Windows 호환성 설정이 채팅 화면이 쓰는 프로그램의 DPI 모드를 강제로 바꿔 놓았습니다. 채팅 화면이 시작되지 않을 수 있습니다(오류 0x8007139F)."));
                foreach (var l in dpiLayers)
                    sb.Append("\n  [").Append(l.Hive).Append("] ").Append(l.ExePath).Append(" = ").Append(l.Flags);
                if (dpiLayers.Any(l => !l.Removable))
                    sb.Append('\n').Append(T(ko,
                        "Entries under HKLM need administrator rights: open the file's Properties > Compatibility > Change high DPI settings and clear the override.",
                        "HKLM 항목은 관리자 권한이 필요합니다. 해당 파일의 속성 > 호환성 > 높은 DPI 설정 변경에서 재정의를 해제하세요."));
                if (r.Problem != WebViewProblem.None) fixes.Add(new SetupFix(SetupFixKind.RetryWebView, T(ko, "Retry", "다시 시도")));
                return new SetupStep(SetupStepId.WebView, layerCause ? SetupState.Fail : SetupState.Warn,
                    layerCause
                        ? T(ko, "The chat screen could not start because of a DPI compatibility setting.", "DPI 호환성 설정 때문에 채팅 화면을 시작하지 못했습니다.")
                        : T(ko, "A DPI compatibility setting was found; it can break the chat screen.", "DPI 호환성 설정이 있습니다. 채팅 화면에 문제가 생길 수 있습니다."),
                    sb.ToString(), fixes);
            }

            string last = r.LastInitHResult is { } hr ? $"0x{hr:X8} {r.LastInitMessage}" : "";
            switch (r.Problem)
            {
                case WebViewProblem.CompatLayer:   // 레이어는 이미 없는데 마지막 오류만 남음: 다시 시도하면 지워진다
                case WebViewProblem.StateMismatch:
                    fixes.Add(new SetupFix(SetupFixKind.RetryWebView, T(ko, "Retry", "다시 시도")));
                    fixes.Add(new SetupFix(SetupFixKind.OpenWebViewLog, T(ko, "Open the log folder", "로그 폴더 열기")));
                    return new SetupStep(SetupStepId.WebView, SetupState.Fail,
                        T(ko, "The chat screen is in an invalid state (0x8007139F). The runtime is installed, so reinstalling it will not help.", "채팅 화면이 올바른 상태가 아닙니다(0x8007139F). 런타임은 설치되어 있으니 다시 설치해도 소용없습니다."),
                        T(ko, "Another process may be using the same data folder with different settings. Close other Nanum CSV Viewer windows, restart the app, then retry.\n", "같은 데이터 폴더를 다른 설정으로 쓰는 프로세스가 있을 수 있습니다. 다른 Nanum CSV Viewer 창을 닫고 앱을 다시 시작한 뒤 다시 시도하세요.\n") + last, fixes);
                case WebViewProblem.AccessDenied:
                    fixes.Add(new SetupFix(SetupFixKind.RetryWebView, T(ko, "Retry", "다시 시도")));
                    fixes.Add(new SetupFix(SetupFixKind.OpenWebViewLog, T(ko, "Open the log folder", "로그 폴더 열기")));
                    return new SetupStep(SetupStepId.WebView, SetupState.Fail,
                        T(ko, "The app cannot write to its WebView2 data folder.", "앱이 WebView2 데이터 폴더에 쓸 수 없습니다."),
                        T(ko, "Check the permissions of: ", "다음 폴더의 권한을 확인하세요: ") + r.UserDataFolder, fixes);
                case WebViewProblem.BinaryOrArch:
                    fixes.Add(new SetupFix(SetupFixKind.InstallWebView, T(ko, "Open the runtime download page", "런타임 설치 페이지 열기"), WebViewDiagnostics.RuntimeInstallUrl));
                    fixes.Add(new SetupFix(SetupFixKind.RetryWebView, T(ko, "Retry", "다시 시도")));
                    return new SetupStep(SetupStepId.WebView, SetupState.Fail,
                        T(ko, "The WebView2 runtime files are damaged or do not match this PC.", "WebView2 런타임 파일이 손상되었거나 이 PC와 맞지 않습니다."),
                        T(ko, "Reinstall the runtime.\n", "런타임을 다시 설치하세요.\n") + last, fixes);
                case WebViewProblem.Other:
                    fixes.Add(new SetupFix(SetupFixKind.RetryWebView, T(ko, "Retry", "다시 시도")));
                    fixes.Add(new SetupFix(SetupFixKind.OpenWebViewLog, T(ko, "Open the log folder", "로그 폴더 열기")));
                    return new SetupStep(SetupStepId.WebView, SetupState.Fail,
                        T(ko, "The chat screen could not start.", "채팅 화면을 시작하지 못했습니다."),
                        last + "\n" + T(ko, "Log: ", "로그: ") + r.LogPath, fixes);
            }

            return new SetupStep(SetupStepId.WebView, SetupState.Ok,
                T(ko, $"WebView2 runtime {r.RuntimeVersion} is installed.", $"WebView2 런타임이 설치되어 있습니다 (버전 {r.RuntimeVersion})."),
                T(ko, $"App DPI mode: {r.DpiAwareness} (system scale {r.DpiScalePercent}%). No DPI compatibility settings found.",
                      $"앱 DPI 모드: {r.DpiAwareness} (시스템 배율 {r.DpiScalePercent}%). DPI 호환성 설정은 없습니다."), SetupStep.NoFixes);
        }

        // ---- 2. omp ---------------------------------------------------------------------------------------------

        public static SetupStep EvaluateOmp(OmpDiscoveryResult r, bool ko)
        {
            if (r.IsOk)
            {
                return new SetupStep(SetupStepId.Omp, SetupState.Ok,
                    T(ko, $"omp {r.Version} was found.", $"omp를 찾았습니다 (버전 {r.Version})."), r.ChosenExe, SetupStep.NoFixes);
            }

            var fixes = new List<SetupFix>();
            foreach (var c in r.Candidates)
            {
                fixes.Add(new SetupFix(SetupFixKind.InstallCandidate,
                    T(ko, $"Install this copy ({c.Reason})", $"이 파일 설치(복사) — {c.Reason}"), c, c.Path));
            }
            fixes.Add(new SetupFix(SetupFixKind.BrowseOmp, T(ko, "Browse…", "찾아보기…")));
            fixes.Add(new SetupFix(SetupFixKind.DownloadOmp,
                r.Problem == OmpProblemKind.TooOld ? T(ko, "Download the latest omp", "최신 omp 내려받기") : T(ko, "Download omp", "omp 내려받기")));

            string summary = r.Problem switch
            {
                OmpProblemKind.ConfiguredPathMissing => T(ko, "The omp path set in the settings does not exist.", "설정에 지정한 omp 경로에 파일이 없습니다."),
                OmpProblemKind.UnsupportedWrapper => T(ko, "Only a script wrapper (.ps1) of omp was found.", "omp의 스크립트 래퍼(.ps1)만 찾았습니다."),
                OmpProblemKind.NotRunnable => T(ko, "omp was found but cannot be run.", "omp를 찾았지만 실행할 수 없습니다."),
                OmpProblemKind.TooOld => T(ko, $"omp {r.Version} is too old; {OmpVersion.Minimum} or newer is required.", $"omp {r.Version}은(는) 너무 오래되었습니다. {OmpVersion.Minimum} 이상이 필요합니다."),
                OmpProblemKind.UnknownVersion => T(ko, "omp was found but its version could not be read.", "omp를 찾았지만 버전을 읽을 수 없습니다."),
                _ => T(ko, "omp (oh-my-pi) was not found.", "omp(oh-my-pi)를 찾을 수 없습니다."),
            };
            string detail = r.Problem switch
            {
                OmpProblemKind.NotFound or OmpProblemKind.ConfiguredPathMissing or OmpProblemKind.UnsupportedWrapper =>
                    (string.IsNullOrEmpty(r.ProblemDetail) ? "" : r.ProblemDetail + "\n") + OmpDiscovery.CheckedText(r, ko),
                _ => (r.ChosenExe ?? "") + (string.IsNullOrEmpty(r.ProblemDetail) ? "" : " — " + r.ProblemDetail),
            };
            return new SetupStep(SetupStepId.Omp, SetupState.Fail, summary, detail, fixes);
        }

        // ---- 3·4. 로그인과 연결 ---------------------------------------------------------------------------------

        public static SetupStep EvaluateLogin(AccountSnapshot? a, bool ompOk, bool ko)
        {
            if (!ompOk)
                return new SetupStep(SetupStepId.Login, SetupState.Blocked,
                    T(ko, "Fix the omp step first; then the sign-in state can be checked.", "먼저 omp 단계를 해결하면 로그인 상태를 확인할 수 있습니다."), null, SetupStep.NoFixes);
            if (a is null)
                return new SetupStep(SetupStepId.Login, SetupState.Pending, T(ko, "Not checked yet.", "아직 검사하지 않았습니다."), null, SetupStep.NoFixes);
            if (!a.HandshakeOk)
                return new SetupStep(SetupStepId.Login, SetupState.Blocked,
                    T(ko, "omp did not start, so the sign-in state is unknown.", "omp가 시작되지 않아 로그인 상태를 알 수 없습니다."), a.Error, SetupStep.NoFixes);

            var signedIn = a.Providers.Where(p => p.Authenticated).Select(p => p.Name).ToList();
            if (a.HasModels)
                return new SetupStep(SetupStepId.Login, SetupState.Ok,
                    T(ko, $"{a.ModelCount} model(s) are available.", $"사용할 수 있는 모델이 {a.ModelCount}개 있습니다."),
                    signedIn.Count > 0 ? T(ko, "Signed in: ", "로그인됨: ") + string.Join(", ", signedIn) : T(ko, "Models come from an API key or environment variable.", "API 키/환경 변수로 모델을 쓰고 있습니다."),
                    SetupStep.NoFixes);

            return new SetupStep(SetupStepId.Login, SetupState.Fail,
                T(ko, "No AI model is available. Sign in to a model provider or set an API key.", "사용할 수 있는 AI 모델이 없습니다. 모델 제공자에 로그인하거나 API 키를 설정하세요."),
                T(ko,
                    "Pick a provider below and press Sign in; the authorization page opens in your browser. API-key providers cannot be signed in from here: set the provider's environment variable (for example ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY) and restart the app, or run `omp login` in a terminal.",
                    "아래에서 제공자를 골라 [로그인]을 누르면 브라우저에 인증 페이지가 열립니다. API 키 방식 제공자는 여기서 로그인할 수 없습니다. 제공자의 환경 변수(예: ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY)를 설정하고 앱을 다시 시작하거나, 터미널에서 `omp login`을 실행하세요."),
                new[] { new SetupFix(SetupFixKind.OpenTerminalLogin, T(ko, "Open a terminal to sign in (omp login)", "터미널에서 로그인 (omp login)")) });
        }

        public static SetupStep EvaluateConnection(AccountSnapshot? a, bool ompOk, bool ko)
        {
            if (!ompOk)
                return new SetupStep(SetupStepId.Connection, SetupState.Blocked,
                    T(ko, "Fix the omp step first; then the connection can be tested.", "먼저 omp 단계를 해결하면 연결을 시험할 수 있습니다."), null, SetupStep.NoFixes);
            if (a is null)
                return new SetupStep(SetupStepId.Connection, SetupState.Pending, T(ko, "Not checked yet.", "아직 검사하지 않았습니다."), null, SetupStep.NoFixes);
            if (!a.HandshakeOk)
                return new SetupStep(SetupStepId.Connection, SetupState.Fail,
                    T(ko, "omp did not answer.", "omp가 응답하지 않았습니다."), a.Error,
                    new[] { new SetupFix(SetupFixKind.Recheck, T(ko, "Test again", "다시 시험")) });
            string detail = T(ko, $"Handshake in {a.ElapsedMs} ms (protocol v{a.ProtocolVersion}). No prompt was sent to any model.",
                $"핸드셰이크 {a.ElapsedMs}ms (프로토콜 v{a.ProtocolVersion}). 모델에는 어떤 메시지도 보내지 않았습니다.");
            if (!a.HasModels)
                return new SetupStep(SetupStepId.Connection, SetupState.Warn,
                    T(ko, "Connected, but no model is listed yet (see the sign-in step).", "연결은 되었지만 모델이 아직 없습니다(로그인 단계 참고)."), detail, SetupStep.NoFixes);
            return new SetupStep(SetupStepId.Connection, SetupState.Ok,
                T(ko, $"Connected to omp; {a.ModelCount} model(s) listed.", $"omp에 연결했습니다. 모델 {a.ModelCount}개가 보입니다."), detail, SetupStep.NoFixes);
        }
    }

    /// <summary>러너가 쓰는 외부 의존(테스트에서 교체).</summary>
    internal sealed class AiSetupServices
    {
        public Func<WebViewReport> CollectWebView { get; init; } = WebViewDiagnostics.Collect;
        public required Func<CancellationToken, Task<OmpDiscoveryResult>> DiscoverOmp { get; init; }
        public Func<IOmpAccountProbe> CreateProbe { get; init; } = () => new OmpRpcAccountProbe();
    }

    /// <summary>
    /// 네 단계의 상태 기계. WebView2 → omp → (omp가 됐을 때만) 로그인·연결. 단계가 바뀔 때마다 <see cref="StepChanged"/>(UI 스레드)를 올린다.
    /// 로그인·연결은 omp를 한 번 띄워 얻은 같은 스냅샷에서 평가한다. 모델에는 메시지를 보내지 않는다.
    /// </summary>
    internal sealed class AiSetupRunner : IDisposable
    {
        private readonly AiSetupServices _svc;
        private readonly bool _ko;
        private readonly SetupStep[] _steps;
        private IOmpAccountProbe? _probe;
        private string? _probeExe;

        public AiSetupRunner(AiSetupServices services, bool korean)
        {
            _svc = services;
            _ko = korean;
            _steps = Enum.GetValues<SetupStepId>().Select(id => new SetupStep(id, SetupState.Pending, "", null, SetupStep.NoFixes)).ToArray();
        }

        public IReadOnlyList<SetupStep> Steps => _steps;
        public SetupStep Step(SetupStepId id) => _steps[(int)id];
        public event Action<SetupStep>? StepChanged;

        public WebViewReport? WebView { get; private set; }
        public OmpDiscoveryResult? Omp { get; private set; }
        public AccountSnapshot? Account { get; private set; }

        public bool Running { get; private set; }
        public bool AllOk => _steps.All(s => s.State == SetupState.Ok);

        /// <summary>처음 문제가 있는(Fail·Warn) 단계. 없으면 null.</summary>
        public SetupStepId? FirstProblem => _steps.Where(s => s.IsProblem).Select(s => (SetupStepId?)s.Id).FirstOrDefault();

        /// <summary>로그인 단계에서 [로그인]을 누를 때 쓰는 연결(검사 뒤에도 살아 있음). 없으면 null.</summary>
        public IOmpAccountProbe? Probe => _probe;

        private void Set(SetupStep step)
        {
            _steps[(int)step.Id] = step;
            StepChanged?.Invoke(step);
        }

        private void Mark(SetupStepId id, SetupState state, string summary = "")
        {
            var old = _steps[(int)id];
            Set(old with { State = state, Summary = summary.Length > 0 ? summary : old.Summary, Fixes = SetupStep.NoFixes });
        }

        /// <summary>네 단계 모두.</summary>
        public async Task RunAllAsync(CancellationToken ct = default)
        {
            Running = true;
            try
            {
                await RunWebViewAsync();
                await RunOmpAsync(ct);
            }
            finally { Running = false; }
        }

        public Task RunWebViewAsync()
        {
            Mark(SetupStepId.WebView, SetupState.Checking, T("Checking…", "검사하는 중…"));
            try { WebView = _svc.CollectWebView(); }
            catch (Exception ex)
            {
                Set(new SetupStep(SetupStepId.WebView, SetupState.Fail, T("The WebView2 check failed.", "WebView2 검사에 실패했습니다."), ex.Message,
                    new[] { new SetupFix(SetupFixKind.Recheck, T("Check again", "다시 검사")) }));
                return Task.CompletedTask;
            }
            Set(AiSetupEvaluator.EvaluateWebView(WebView, _ko));
            return Task.CompletedTask;
        }

        /// <summary>omp 찾기 → 로그인·연결. omp를 고친 뒤에 부른다.</summary>
        public async Task RunOmpAsync(CancellationToken ct = default)
        {
            Mark(SetupStepId.Omp, SetupState.Checking, T("Checking…", "검사하는 중…"));
            Mark(SetupStepId.Login, SetupState.Pending);
            Mark(SetupStepId.Connection, SetupState.Pending);
            Account = null;
            try { Omp = await _svc.DiscoverOmp(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Omp = null;
                Set(new SetupStep(SetupStepId.Omp, SetupState.Fail, T("The omp check failed.", "omp 검사에 실패했습니다."), ex.Message,
                    new[] { new SetupFix(SetupFixKind.Recheck, T("Check again", "다시 검사")) }));
                Set(AiSetupEvaluator.EvaluateLogin(null, false, _ko));
                Set(AiSetupEvaluator.EvaluateConnection(null, false, _ko));
                return;
            }
            Set(AiSetupEvaluator.EvaluateOmp(Omp, _ko));
            await RunAccountAsync(ct);
        }

        /// <summary>로그인·연결만 다시(로그인 직후).</summary>
        public async Task RunAccountAsync(CancellationToken ct = default)
        {
            bool ompOk = Omp is { IsOk: true };
            if (!ompOk)
            {
                Account = null;
                Set(AiSetupEvaluator.EvaluateLogin(null, false, _ko));
                Set(AiSetupEvaluator.EvaluateConnection(null, false, _ko));
                return;
            }
            Mark(SetupStepId.Login, SetupState.Checking, T("Checking…", "검사하는 중…"));
            Mark(SetupStepId.Connection, SetupState.Checking, T("Checking…", "검사하는 중…"));
            string exe = Omp!.ChosenExe!;
            if (_probe is null || !string.Equals(_probeExe, exe, StringComparison.OrdinalIgnoreCase))
            {
                _probe?.Dispose();
                _probe = _svc.CreateProbe();
                _probeExe = exe;
            }
            AccountSnapshot snapshot;
            try { snapshot = await _probe.InspectAsync(exe, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { snapshot = AccountSnapshot.Failed(ex.Message); }
            Account = snapshot;
            Set(AiSetupEvaluator.EvaluateLogin(snapshot, true, _ko));
            Set(AiSetupEvaluator.EvaluateConnection(snapshot, true, _ko));
        }

        private string T(string en, string ko) => _ko ? ko : en;

        public void Dispose()
        {
            _probe?.Dispose();
            _probe = null;
        }
    }

    /// <summary>
    /// 자동으로 여는 규칙: 앱 버전마다 한 번(열 때 기록). '다시 표시하지 않기'를 누르면 이후 자동으로는 열지 않는다.
    /// 메뉴·설정에서 직접 여는 것은 이 규칙과 무관하다.
    /// </summary>
    internal static class AiSetupPolicy
    {
        public static bool ShouldAutoShow(AppSettings settings, string appVersion, bool shownThisSession) =>
            !shownThisSession && !settings.AiSetupDisabled && !string.Equals(settings.AiSetupShownVersion, appVersion, StringComparison.Ordinal);

        public static void MarkShown(AppSettings settings, string appVersion) => settings.AiSetupShownVersion = appVersion;

        public static void SetDismissed(AppSettings settings, bool dismissed) => settings.AiSetupDisabled = dismissed;
    }
}
