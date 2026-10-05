using NanumCsvViewer.Agent.Chat;

namespace NanumCsvViewer.Agent
{
    // 지연 시작(v3.2): AI 패널이 시작할 때부터 떠 있어도 omp 자식 프로세스는 바로 만들지 않는다. 시작은 첫 메시지를 보낼 때,
    // 또는 호스트가 앱이 한가해진 뒤 <see cref="StartDeferredNow"/>로 미리 데우면 그때 한다. 패널이 숨겨져 있으면 호스트는 데우지 않아
    // 패널만 열었다 닫는 사람에게는 프로세스·버전 확인·네트워크 연결 비용이 없다.
    public sealed partial class ChatController
    {
        private (Func<string> WorkingDirectory, string? Resume, bool ViaCli)? _deferred;
        private (string Text, bool FollowUp)? _pendingSubmit;
        /// <summary>메시지 없이 미리 시작한 omp가 아직 연결 중이다. 이 동안 들어온 첫 메시지는 연결이 끝나면 보낸다.</summary>
        private bool _prewarming;

        /// <summary>시작을 첫 메시지로 미뤄 둔 상태(자식 프로세스 없음).</summary>
        public bool IsStartDeferred => _deferred != null && _client == null && !_disposed;

        /// <summary>
        /// <see cref="StartAsync(string, WorkspaceConversation?, CancellationToken)"/>와 같되 지금은 아무것도 시작하지 않는다.
        /// 입력창에서 첫 메시지(또는 슬래시 명령)를 보내면 그때 omp를 시작하고 그 메시지를 보낸다.
        /// 이어받을 대화는 지금 정해 화면에 알린다. 작업 폴더는 시작하는 순간의 값을 쓴다.
        /// </summary>
        public void StartOnFirstUse(Func<string> workingDirectory, WorkspaceConversation? conversation = null)
        {
            if (_disposed) return;
            string? resume = conversation is null ? null : BeginConversation(conversation);
            _deferred = (workingDirectory, resume, resume != null);
            SetStatus(T("Ready — omp starts with your first message", "준비됨 — omp는 첫 메시지를 보낼 때 시작합니다"), false);
        }

        /// <summary>
        /// 미뤄 둔 시작을 메시지 없이 지금 한다(모델·생각·승인 선택이 첫 메시지 전에 준비되도록). 미뤄 둔 상태가 아니면(이미 시작했거나
        /// 시작 중, 또는 시작한 적 없음) 아무것도 하지 않고 false. 작업 폴더는 <see cref="StartOnFirstUse"/>가 받은 함수로 지금 구한다.
        /// </summary>
        public bool StartDeferredNow()
        {
            if (!IsStartDeferred) return false;
            _prewarming = true;
            LaunchDeferred();
            return true;
        }

        // 미뤄 둔 시작을 지금 하고 메시지는 연결이 끝난 뒤 보낸다(페이지는 보냈다고 알고 입력창을 비운다).
        private bool StartDeferredWith(string text, bool followUp)
        {
            _pendingSubmit = (text, followUp);
            LaunchDeferred();
            return true;
        }

        private void LaunchDeferred()
        {
            var d = _deferred!.Value;
            _deferred = null;
            string dir = "";
            try { dir = d.WorkingDirectory(); } catch { /* 작업 폴더를 못 구하면 StartCoreAsync가 기본 폴더를 쓴다 */ }
            _ = StartDeferredCoreAsync(dir, d.Resume, d.ViaCli);
        }

        private async Task StartDeferredCoreAsync(string dir, string? resume, bool viaCli)
        {
            try { await StartCoreAsync(dir, resume, default, viaCli); }
            catch (Exception ex) { _log.Note("deferred start failed: " + ex); Fail(T("Cannot start omp: ", "omp를 시작할 수 없습니다: ") + ex.Message); }
        }

        private void FlushPendingSubmit()
        {
            if (_pendingSubmit is not { } p) return;
            _pendingSubmit = null;
            Submit(p.Text, p.FollowUp);
        }

        private void DropPendingSubmit()
        {
            if (_pendingSubmit == null) return;
            _pendingSubmit = null;
            _stream.Emit(ChatPageMessages.Notice("warn",
                T("Your message was not sent because the agent could not start.", "에이전트를 시작하지 못해 메시지를 보내지 못했습니다.")));
        }
    }
}
