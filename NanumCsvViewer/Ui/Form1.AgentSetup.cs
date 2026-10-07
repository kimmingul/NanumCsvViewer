using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Rpc;
using NanumCsvViewer.Agent.Setup;

namespace NanumCsvViewer
{
    // AI 설정 도우미(Agent/Setup)와 진단 정보 복사의 호스트 쪽: 메뉴 진입점, 자동으로 여는 규칙, 도우미가 고친 결과(설정 저장·에이전트 다시 시작·
    // 채팅 화면 다시 시도)의 반영. 단계 규칙·화면은 Agent/Setup/*.cs.
    public partial class Form1
    {
        private ToolStripMenuItem? _aiSetupMenu, _aiDiagMenu;
        private AiSetupWizard? _aiSetup;
        private bool _aiSetupAutoShown;

        /// <summary>테스트·스크린샷용: 널이 아니면 도우미가 실제 환경 대신 이 의존을 쓴다.</summary>
        internal AiSetupServices? AiSetupServicesOverride;
        internal AiSetupOps? AiSetupOpsOverride;

        /// <summary>열려 있는 AI 설정 도우미(없으면 null).</summary>
        internal AiSetupWizard? AiSetupWindow => _aiSetup is { IsDisposed: false } w ? w : null;

        private static bool KoreanUi => Loc.CurrentLanguage == "ko";

        private Task<OmpDiscoveryResult> DiscoverOmp(CancellationToken ct) => OmpDiscovery.DiscoverAsync(_settings, ct);

        /// <summary>
        /// 도우미를 연다(이미 열려 있으면 앞으로). modal이면 owner 위에 모달로(설정 대화 상자에서 부를 때), 아니면 창 하나로 띄워 두고 계속 쓸 수 있게 한다.
        /// focus 단계가 있으면 첫 검사가 끝난 뒤 그 단계로 스크롤한다.
        /// </summary>
        internal AiSetupWizard ShowAiSetupAssistant(SetupStepId? focus = null, bool modal = false, IWin32Window? owner = null)
        {
            if (AiSetupWindow is { } open)
            {
                if (!modal) { open.Activate(); return open; }
                open.Close();
            }
            bool ko = KoreanUi;
            var services = AiSetupServicesOverride ?? new AiSetupServices { DiscoverOmp = DiscoverOmp };
            var ops = AiSetupOpsOverride ?? new AiSetupOps();
            var runner = new AiSetupRunner(services, ko);
            var wizard = new AiSetupWizard(runner, ops, _settings, _palette, ko,
                ct => AiDiagnostics.CollectAsync(_settings, DeviceDpi, DiscoverOmp, ct), s => s.Save(), focus);
            wizard.OmpPathChosen += OnAiSetupOmpPath;
            wizard.OmpInstalled += OnAiSetupOmpInstalled;
            wizard.CredentialsChanged += () => { if (_agentController is not null) _ = _agentController.RestartAsync(); };
            wizard.WebViewRetryRequested += () => _agentPanel?.RetryInit();
            wizard.FormClosed += (_, _) => { if (ReferenceEquals(_aiSetup, wizard)) _aiSetup = null; };
            _aiSetup = wizard;
            if (modal) wizard.ShowDialog(owner ?? this);
            else wizard.Show(this);
            return wizard;
        }

        /// <summary>찾아보기로 고른 omp를 설정에 저장하고 다음 시작부터(실행 중이면 지금) 쓰게 한다.</summary>
        private void OnAiSetupOmpPath(string path)
        {
            _settings.AgentOmpPath = path;
            _settings.Save();
            ApplyOmpChange();
        }

        /// <summary>omp를 설치했다. 설정에 적어 둔 경로가 비어 있는 파일이면 설치한 경로로 바꾼다(설정이 설치본을 가리지 않게).</summary>
        private void OnAiSetupOmpInstalled(string? installed)
        {
            string configured = _settings.AgentOmpPath ?? "";
            if (installed is not null && configured.Length > 0 && !File.Exists(configured))
            {
                _settings.AgentOmpPath = installed;
                _settings.Save();
            }
            ApplyOmpChange();
        }

        private void ApplyOmpChange()
        {
            if (_agentController is null) return;
            _agentController.Options = AgentOptions();
            _ = _agentController.RestartAsync();
        }

        // ---- 자동으로 열기 ----------------------------------------------------------------------------------------

        private void WireAgentSetup()
        {
            if (_agentController is not null) _agentController.SetupNeeded += OnAgentSetupNeeded;
            WebViewDiagnostics.InitFailed += OnWebViewInitFailed;
        }

        private void UnwireAgentSetup()
        {
            if (_agentController is not null) _agentController.SetupNeeded -= OnAgentSetupNeeded;
            WebViewDiagnostics.InitFailed -= OnWebViewInitFailed;
        }

        private void OnWebViewInitFailed(string _) => TryAutoShowAiSetup(SetupStepId.WebView);

        private void OnAgentSetupNeeded(AgentSetupReason reason, string _)
        {
            switch (reason)
            {
                case AgentSetupReason.Login:   // 채팅 알림의 [로그인…] 버튼: 사용자가 직접 누른 것이라 자동 규칙과 무관
                    if (!IsDisposed && !_closing) BeginInvoke(() => { if (!IsDisposed) ShowAiSetupAssistant(SetupStepId.Login); });
                    break;
                case AgentSetupReason.Omp:
                    TryAutoShowAiSetup(SetupStepId.Omp);
                    break;
                default:
                    TryAutoShowAiSetup(SetupStepId.WebView);
                    break;
            }
        }

        /// <summary>
        /// AI 패널이 시작하지 못했을 때 도우미를 자동으로 연다: 앱 버전마다 한 번, '다시 표시하지 않기'를 누르지 않았을 때만.
        /// 보이지 않는 창(requireVisible)에서는 열지 않는다. 열었으면 true.
        /// </summary>
        internal bool TryAutoShowAiSetup(SetupStepId step, bool requireVisible = true)
        {
            if (IsDisposed || _closing || (requireVisible && (!IsHandleCreated || !Visible))) return false;
            if (!AiSetupPolicy.ShouldAutoShow(_settings, AppInfo.Version, _aiSetupAutoShown)) return false;
            _aiSetupAutoShown = true;
            AiSetupPolicy.MarkShown(_settings, AppInfo.Version);
            _settings.Save();
            if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed && !_closing) ShowAiSetupAssistant(step); });
            return true;
        }

        // ---- 진단 정보 복사 ---------------------------------------------------------------------------------------

        /// <summary>도움말 ▸ AI 환경 진단 정보 복사: 클립보드에 복사하고 파일로도 저장할지 묻는다.</summary>
        internal async Task<string> CopyAiDiagnosticsAsync()
        {
            var ops = AiSetupOpsOverride ?? new AiSetupOps();
            ops.Owner = this;
            string text;
            try
            {
                Cursor = Cursors.WaitCursor;
                text = await AiDiagnostics.CollectAsync(_settings, DeviceDpi, DiscoverOmp);
            }
            catch (Exception ex)
            {
                ops.ShowMessage(KoreanUi ? "AI 환경 진단 정보 복사" : "Copy AI Diagnostics", ex.Message, error: true);
                return "";
            }
            finally { Cursor = Cursors.Default; }
            ops.SetClipboard(text);
            int lines = text.Count(c => c == '\n') + 1;
            bool ko = KoreanUi;
            string message = ko
                ? $"진단 정보를 클립보드에 복사했습니다({lines}줄). 사용자 이름·비밀 값(API 키·토큰)은 가렸고 데이터 내용은 포함되지 않습니다.\n\n파일로도 저장할까요?"
                : $"Copied the diagnostics to the clipboard ({lines} lines). Your user name and secrets (API keys, tokens) are masked; no data contents are included.\n\nSave it to a file as well?";
            if (ops.Confirm(ko ? "AI 환경 진단 정보 복사" : "Copy AI Diagnostics", message, yesNo: true)
                && ops.PickSavePath($"NanumCsvViewer-ai-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt") is { } path)
            {
                try { ops.WriteFile(path, text); }
                catch (Exception ex) { ops.ShowMessage(ko ? "AI 환경 진단 정보 복사" : "Copy AI Diagnostics", ex.Message, error: true); }
            }
            return text;
        }
    }
}
