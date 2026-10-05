using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Python;

namespace NanumCsvViewer.Agent
{
    // 작업 공간 단위 대화(v3.1): 작업 공간 파일(.ncvws)이 가리키는 omp 세션을 이어 가거나, 없으면 새 대화로 시작한다.
    // omp 세션 = ~/.omp/agent/sessions/<작업 폴더>/<시각>_<id>.jsonl. get_state.sessionFile이 지금 세션이고, 명령줄 --resume <파일>이 이어받는다
    // (작업 폴더가 달라도 된다 — RPC switch_session은 다른 폴더의 세션을 거절하므로 쓰지 않는다).
    public sealed partial class ChatController
    {
        /// <summary>대화를 이어받은 직후 omp 기록을 불러와 화면을 복원해야 하는가(연결이 끝나면 한 번).</summary>
        private bool _historyOnConnect;
        private string _lastUserRequest = "";

        /// <summary>지금 대화의 omp 세션 파일(아직 모르면 빈 문자열). 대화를 바꾸면 omp가 알려 주기 전에도 바로 새 값이다.</summary>
        public string SessionFile => _sessionFile;

        /// <summary>지금 대화의 세션 id(파일 이름에서). 모르면 null.</summary>
        public string? SessionId => SessionCatalog.IdOf(_sessionFile);

        /// <summary>이번 턴을 시작한 사용자 메시지(도구가 뷰 출처에 적는다). 슬래시 명령은 제외. 아직 없으면 null.</summary>
        public string? CurrentUserRequest => _lastUserRequest.Length == 0 ? null : _lastUserRequest;

        /// <summary>
        /// 작업 공간 파일이 가리키는 대화를 찾는다: 저장된 파일 경로 → 그 id로 세션 저장소 전체 검색. 저장된 대화가 없으면 (null, false),
        /// 있었는데 못 찾으면 (null, true).
        /// </summary>
        internal static (string? Path, bool Missing) ResolveConversation(WorkspaceConversation? conversation, string? sessionRoot)
        {
            if (conversation is null || conversation.IsEmpty) return (null, false);
            string? file = conversation.SessionFile;
            if (!string.IsNullOrWhiteSpace(file) && Path.IsPathRooted(file) && File.Exists(file)) return (file, false);
            string? id = conversation.SessionId ?? SessionCatalog.IdOf(file);
            if (SessionCatalog.FindById(id, sessionRoot) is { } found) return (found, false);
            return (null, true);
        }

        /// <summary>
        /// 대화를 바꿀 준비: 화면·승인 기록을 비우고, 이어받을 세션이 있으면 그 경로를, 없으면 null(새 대화)을 돌려준다.
        /// 이어받으면 연결이 끝난 뒤 기록을 불러오고, 저장된 대화가 있었는데 사라졌으면 채팅에 알린다.
        /// </summary>
        private string? BeginConversation(WorkspaceConversation? conversation)
        {
            var (path, missing) = ResolveConversation(conversation, _svc.SessionRoot);
            _stream.Clear();
            _page.Post(ChatPageMessages.Clear());
            _evalApproval.Reset();
            _lastUserRequest = "";
            _sessionFile = path ?? "";
            _sessionName = "";
            _historyOnConnect = path != null;
            // 이어받을 때의 안내는 기록을 다시 그린 뒤에 나온다(연결 처리 쪽). 사라진 경우의 경고는 바로.
            if (path == null && missing)
                _stream.Emit(ChatPageMessages.Notice("warn", T(
                    "The conversation saved with this workspace no longer exists (the session file is gone or was made on another PC). Starting a new conversation.",
                    "이 작업 공간에 저장된 이전 대화를 찾을 수 없습니다(세션 파일이 없어졌거나 다른 PC에서 만든 것입니다). 새 대화로 시작합니다.")));
            return path;
        }

        /// <summary>처음 시작하면서 작업 공간의 대화를 이어 간다(없으면 새 대화). conversation이 null이면 <see cref="StartAsync(string, CancellationToken)"/>와 같다.</summary>
        public Task StartAsync(string workingDirectory, WorkspaceConversation? conversation, CancellationToken cancellation = default)
        {
            if (conversation is null) return StartCoreAsync(workingDirectory, null, cancellation);
            string? resume = BeginConversation(conversation);
            return StartCoreAsync(workingDirectory, resume, cancellation, resumeViaCli: resume != null);
        }

        /// <summary>
        /// 작업 공간이 바뀌었다(다른 작업 공간 파일 열기): 설정·작업 공간 상태를 바꾸고 그 작업 공간의 대화로 omp를 다시 시작한다.
        /// 작업 중이면 중단한다. 저장된 대화가 없거나 사라졌으면 새 대화(사라졌다면 알림). 분석 폴더도 새 작업 공간 기준으로 바뀐다.
        /// </summary>
        public Task SwitchWorkspaceAsync(AgentHostOptions options, AgentWorkspaceContext context, WorkspaceConversation? conversation)
        {
            if (_disposed) return Task.CompletedTask;
            _options = options;   // 이 아래에서 곧바로 다시 시작하므로 Options 설정자의 낡음 검사(이중 재시작)는 건너뛴다
            ApplyWorkspaceContext(context);
            return RestartOnConversationAsync(conversation);
        }

        /// <summary>
        /// 이 작업 공간의 새 대화를 시작한다: 저장된 대화 연결은 새 대화로 바뀐다(작업 공간을 저장하면 파일에 반영된다).
        /// omp를 같은 작업 공간·설정으로 새 세션에서 다시 시작한다. 작업 중이면 중단한다.
        /// </summary>
        public Task StartNewConversationAsync() =>
            RestartOnConversationAsync(WorkspaceConversation.None,
                T("Started a new conversation for this workspace. Save the workspace to keep this link; the previous conversation stays in omp's session list.",
                  "이 작업 공간의 새 대화를 시작했습니다. 작업 공간을 저장하면 이 연결이 파일에 남습니다. 이전 대화는 omp 세션 목록에 그대로 있습니다."));

        private Task RestartOnConversationAsync(WorkspaceConversation? conversation, string? notice = null)
        {
            if (_disposed) return Task.CompletedTask;
            if (_activity.Busy) { _activity.NoteStopRequested(); TearDown(force: true); EndTurn(stopped: true); }
            string? resume = BeginConversation(conversation);
            if (notice != null) _stream.Emit(ChatPageMessages.Notice("info", notice));
            RefreshStatus(force: true);
            return StartCoreAsync(_baseDir, resume, default, resumeViaCli: true);
        }
    }
}
