using NanumCsvViewer.Agent.Chat;

namespace NanumCsvViewer.Agent
{
    /// <summary>승인 모드 관련 문구(채팅 알림·확인 대화 상자·설정 대화 상자가 같은 글을 쓴다).</summary>
    internal static class ApprovalTexts
    {
        public static string Label(AgentApprovalMode mode, bool korean) => mode switch
        {
            AgentApprovalMode.AlwaysAsk => korean ? "항상 묻기 (always-ask)" : "Always ask (always-ask)",
            AgentApprovalMode.Write => korean ? "편집 자동 승인 (write)" : "Auto-approve edits (write)",
            _ => korean ? "모두 허용 (yolo)" : "Allow everything (yolo)",
        };

        public static string YoloTitle(bool korean) => korean ? "모두 허용(yolo)" : "Allow everything (yolo)";

        public static string YoloConfirm(bool korean) => korean
            ? "Python·셸 명령과 파일 쓰기가 묻지 않고 이 PC에서 실행됩니다. 에이전트가 쓴 코드가 사용자 권한으로 바로 실행되며, " +
              "데이터 편집과 새 파일 저장 승인 카드도 나오지 않습니다(편집은 Ctrl+Z로 되돌릴 수 있습니다).\n\n모두 허용 모드로 바꿀까요?"
            : "Python and shell commands and file writes will run on this PC without asking. Code the agent writes runs immediately with your permissions, " +
              "and data-edit and save-as approval cards are no longer shown (edits can still be undone with Ctrl+Z).\n\nSwitch to allow-everything mode?";

        public static string DefaultNotice(bool korean) => korean
            ? "승인 모드: 모두 허용(yolo). Python·셸 명령과 파일 쓰기가 묻지 않고 이 PC에서 실행됩니다. " +
              "입력창 아래 승인 선택 또는 보기 ▸ AI 에이전트 설정…에서 '항상 묻기'나 '편집 자동 승인'으로 바꿀 수 있습니다."
            : "Approval mode: allow everything (yolo). Python and shell commands and file writes run on this PC without asking. " +
              "Change it to 'Always ask' or 'Auto-approve edits' with the selector under the message box or in View ▸ AI agent settings….";

        /// <summary>추가 인자로 고정됐을 때 드롭다운 툴팁·알림 문구.</summary>
        public static string Locked(string flag, bool korean) => korean
            ? $"승인 모드가 omp 추가 인자 '{flag}'로 고정되어 바꿀 수 없습니다. 바꾸려면 보기 ▸ AI 에이전트 설정…의 'omp 추가 인자'에서 이 인자를 지우세요."
            : $"The approval mode is fixed by the extra omp argument '{flag}'. To change it, remove that argument in View ▸ AI Agent Settings… ▸ 'Extra omp arguments'.";

        public static string AutoApproved(string target, AgentApprovalMode mode, bool korean) => korean
            ? $"자동 승인: {target} (모드: {AgentApprovalPolicy.ToOmp(mode)})"
            : $"Auto-approved: {target} (mode: {AgentApprovalPolicy.ToOmp(mode)})";
    }

    // 승인 모드: 앱 카드 정책, 모드 변경(확인·재시작), 기본 모드 안내.
    public sealed partial class ChatController
    {
        /// <summary>마지막으로 띄운 omp의 host.yml에 쓴 모드. 설정이 이와 다르면 쉬는 대로 같은 대화로 다시 시작한다.</summary>
        private AgentApprovalMode _launchedApproval = AgentApprovalPolicy.Default;

        /// <summary>현재 승인 모드.</summary>
        public AgentApprovalMode ApprovalMode => _options.ApprovalMode;

        /// <summary>사용자가 채팅 선택으로 모드를 바꿔 적용됐을 때(호스트가 설정에 저장한다).</summary>
        public event Action<AgentApprovalMode>? ApprovalModeChanged;

        /// <summary>기본 모드 안내를 채팅에 보였을 때(호스트가 '보였음'을 설정에 저장한다).</summary>
        public event Action? ApprovalNoticeShown;

        private bool ApprovalStale() => _options.ApprovalMode != _launchedApproval;

        /// <summary>
        /// 모드를 바꾼다. 모두 허용(yolo)로 바꿀 때는 확인 대화 상자를 띄우고 취소하면 이전 모드를 유지한다(false).
        /// 바뀌면 omp는 쉬는 대로(작업 중이면 턴 뒤) 같은 대화로 다시 시작해 host.yml의 tools.approvalMode를 반영한다.
        /// 앱 카드 정책은 즉시 바뀐다.
        /// </summary>
        public bool TrySetApprovalMode(AgentApprovalMode mode)
        {
            if (_disposed) return false;
            if (_options.ForcedApproval is { } forced)
            {
                // omp 추가 인자가 모드를 고정했다: 선택을 바꾸지 않고 이유를 알린다.
                _stream.Emit(ChatPageMessages.Notice("warn", ApprovalTexts.Locked(forced.Flag, Korean)));
                RefreshStatus(force: true);
                return false;
            }
            if (mode == _options.ApprovalMode) { RefreshStatus(force: true); return true; }
            if (mode == AgentApprovalMode.Yolo && !Dialogs.Confirm(ApprovalTexts.YoloTitle(Korean), ApprovalTexts.YoloConfirm(Korean)))
            {
                RefreshStatus(force: true);   // 페이지의 선택을 이전 모드로 되돌린다
                return false;
            }
            Options = _options with { ApprovalMode = mode, ApprovalNoticePending = false };
            ApprovalModeChanged?.Invoke(mode);
            return true;
        }

        /// <summary>첫 연결 때 한 번: 기본 모드(명시적으로 고르지 않음) 안내. 막는 대화 상자 없이 채팅 알림.</summary>
        private void PostApprovalNoticeOnce()
        {
            if (!_options.ApprovalNoticePending) return;
            _options = _options with { ApprovalNoticePending = false };
            _stream.Emit(ChatPageMessages.Notice("info", ApprovalTexts.DefaultNotice(Korean)));
            ApprovalNoticeShown?.Invoke();
        }

        /// <summary>앱 카드가 현재 모드에서 자동 승인되는 종류면 카드 없이 알림만 남기고 true.</summary>
        private bool TryAutoApprove(string target, ApprovalKind kind)
        {
            var mode = _options.EffectiveApprovalMode;
            if (!AgentApprovalPolicy.AutoApproves(mode, kind)) return false;
            string text = ApprovalTexts.AutoApproved(target, mode, Korean);
            _log.Note(text);
            _stream.Emit(ChatPageMessages.Notice("info", text));
            return true;
        }
    }
}
