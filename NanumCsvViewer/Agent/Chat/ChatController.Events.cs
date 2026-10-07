using System.Text.Json;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    // omp → 페이지: 이벤트·상태·카탈로그·명령 목록.
    public sealed partial class ChatController
    {
        private string _model = "", _thinking = "", _sessionName = "", _sessionFile = "";
        private double _context = -1;
        private bool _shellRunning;
        private bool _stateInFlight, _stateAgain;
        private List<string> _modelList = new();
        private List<string> _levels = new();
        private Dictionary<string, string> _providers = new();
        private List<SlashCommandInfo> _ompCommands = new();

        private static readonly string[] DefaultLevels = { "off", "minimal", "low", "medium", "high", "xhigh" };

        private void OnFrame(OmpRpcClient client, JsonElement frame)
        {
            if (_disposed || !ReferenceEquals(client, _client)) return;
            switch (frame.Str("type"))
            {
                case "host_tool_call":
                    _dispatcher?.HandleCall(frame);
                    return;
                case "host_tool_cancel":
                    _dispatcher?.HandleCancel(frame);
                    return;
                case "extension_ui_request":
                    HandleUiRequest(client, frame);
                    return;
                case "available_commands_update":
                    ApplyCommands(frame.Child("commands"));
                    return;
                case "queue_update":
                    PostQueue(frame, fromState: false);
                    return;
                case "prompt_result":
                    HandlePromptResult(frame);
                    return;
                case "session_info_update":
                    RequestState();
                    return;
                case "session_settled":
                    return;
            }

            var ev = AgentEventParser.Parse(frame, Korean);
            if (ev.Kind != AgentEventKind.None) OnAgentEvent(ev);
        }

        private void OnAgentEvent(AgentEvent ev)
        {
            _activity.Apply(ev);
            _stream.Apply(ev);

            switch (ev.Kind)
            {
                case AgentEventKind.AgentEnd when ev.IsTerminal:
                    EndTurn(stopped: false);
                    break;
                case AgentEventKind.ToolEnd when ev.ToolName.Contains("todo", StringComparison.OrdinalIgnoreCase):
                    RequestState();
                    break;
                case AgentEventKind.Model when ev.Detail.Length > 0:
                    _model = ev.ToolName + "/" + ev.Detail;
                    break;
            }
            RefreshStatus();
        }

        private void HandlePromptResult(JsonElement frame)
        {
            if (frame.Str("status") == "error")
            {
                var error = frame.Child("error");
                string message = error.IsObject() ? error.Str("message") : error.ValueKind == JsonValueKind.String ? error.GetString() ?? "" : "";
                _stream.Emit(ChatPageMessages.Notice("error", message.Length > 0 ? message : T("The request failed.", "요청이 실패했습니다.")));
            }
            if (frame.Bool("agentInvoked") == false)
            {
                _activity.Apply(new AgentEvent { Kind = AgentEventKind.PromptLocal });
                RefreshStatus();
            }
        }

        /// <summary>턴 종료: 열려 있던 턴이면 시각을 담은 turnEnd를 보내고 활동을 비운 뒤 상태(할 일·컨텍스트·대기열)를 다시 읽는다.</summary>
        private void EndTurn(bool stopped)
        {
            bool stop = stopped || _activity.StopRequested;
            bool open = _activity.CloseTurn(out long startedAt);
            _activity.Reset();
            _abortAt = null;
            if (open) _stream.Emit(ChatPageMessages.TurnEnd(startedAt, _clock.UnixMs, stop));
            if (_connected) RequestState();
            RefreshStatus();
            RunPendingWorkspaceRestart();
        }

        private void OnHostToolFinished(HostToolCall call, HostToolResult? result) => RefreshStatus();

        // ---- 상태 ----------------------------------------------------------------------------------------------

        private void RequestState()
        {
            if (_stateInFlight) { _stateAgain = true; return; }
            _stateInFlight = true;
            bool sent = Ask("get_state", null,
                data => { _stateInFlight = false; ApplyState(data); FlushStateAgain(); },
                _ => { _stateInFlight = false; FlushStateAgain(); });
            if (!sent) _stateInFlight = false;
        }

        private void FlushStateAgain()
        {
            if (!_stateAgain) return;
            _stateAgain = false;
            RequestState();
        }

        private void ApplyState(JsonElement data)
        {
            var model = data.Child("model");
            string provider = model.Str("provider"), id = model.Str("id");
            if (id.Length > 0) _model = provider.Length > 0 ? provider + "/" + id : id;
            _thinking = data.Str("thinkingLevel", _thinking);
            _sessionFile = data.Str("sessionFile", _sessionFile);
            _evalApproval.ObserveSessionFile(_sessionFile);
            _sessionName = data.Str("sessionName");
            var usage = data.Child("contextUsage");
            double percent = usage.Num("percent");
            _context = double.IsNaN(percent) ? -1 : percent;

            var todos = new List<TodoItem>();
            foreach (var phase in data.Child("todoPhases").Items())
            {
                string phaseName = phase.Str("name");
                foreach (var task in phase.Child("tasks").Items())
                    todos.Add(new TodoItem(phaseName, task.Str("content"), task.Str("status")));
            }
            _stream.ShowTodos(todos);

            var queued = data.Child("queuedMessages");
            if (queued.Child("steering").IsArray() && queued.Child("followUp").IsArray())
                PostQueue(queued, fromState: true);
            RefreshStatus();
        }

        private void PostQueue(JsonElement source, bool fromState) =>
            _page.Post(ChatPageMessages.Queue(source.Child("steering").Strings(), source.Child("followUp").Strings(), fromState));

        // ---- 카탈로그·명령 -------------------------------------------------------------------------------------

        private void RequestCommands() =>
            Ask("get_available_commands", null, data => ApplyCommands(data.Child("commands")));

        private void RequestModels() =>
            Ask("get_available_models", null, data =>
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var m in data.Child("models").Items())
                {
                    string provider = m.Str("provider"), id = m.Str("id");
                    if (provider.Length > 0 && id.Length > 0) set.Add(provider + "/" + id);
                }
                _modelList = set.ToList();
                PostCatalog();
                RefreshModelUsage();
                NoticeIfNoModels();
            });

        /// <summary>
        /// 연결됐는데 쓸 수 있는 모델이 하나도 없으면(로그인 전·자격 증명 만료) 채팅에 알리고 AI 설정 도우미의 로그인 단계로 가는 버튼을 단다.
        /// 모델이 생기면 다시 알릴 수 있게 표시를 지운다.
        /// </summary>
        private void NoticeIfNoModels()
        {
            if (_modelList.Count > 0) { _noModelsNoticed = false; return; }
            if (_noModelsNoticed) return;
            _noModelsNoticed = true;
            _stream.Emit(ChatPageMessages.LinkNotice("warn", NoModelsNoticeText(Korean), T("Sign in…", "로그인…"), SetupLoginUrl));
        }

        private bool _noModelsNoticed;

        /// <summary>채팅 알림의 버튼이 AI 설정 도우미(로그인 단계)를 열게 하는 앱 내부 주소.</summary>
        internal const string SetupLoginUrl = "nanumcsv://setup/login";

        internal static string NoModelsNoticeText(bool korean) => korean
            ? "사용할 수 있는 AI 모델이 없습니다. 모델 제공자에 로그인하거나 API 키를 설정해야 합니다."
            : "No AI model is available yet. Sign in to a model provider or set an API key.";

        private void RequestLevels() =>
            Ask("get_available_thinking_levels", null, data =>
            {
                var levels = data.Child("levels").Strings();
                _levels = levels.Count > 0 ? levels : DefaultLevels.ToList();
                PostCatalog();
            });

        private void RequestProviders() =>
            Ask("get_login_providers", null, data =>
            {
                var map = new Dictionary<string, string>();
                foreach (var p in data.Child("providers").Items())
                {
                    if (p.Bool("available") == false) continue;
                    string id = p.Str("id");
                    if (id.Length > 0) map[id] = p.Str("name", id);
                }
                _providers = map;
                PostCatalog();
            }, _ => { /* 로그인 제공자 이름은 보기 좋게 쓰는 용도뿐 */ });

        private void PostCatalog()
        {
            _postedRecent = OmpModelUsage.Recent(_usageItems, _modelList);
            _page.Post(ChatPageMessages.Catalog(_modelList, _levels.Count > 0 ? _levels : DefaultLevels.ToList(), _providers, _postedRecent));
        }

        private void ApplyCommands(JsonElement commands)
        {
            var list = new List<SlashCommandInfo>();
            foreach (var c in commands.Items())
            {
                string name = c.Str("name");
                if (name.StartsWith('/')) name = name[1..];
                if (name.Length == 0) continue;
                var input = c.Child("input");
                string hint = input.IsObject() ? input.Str("hint") : input.ValueKind == JsonValueKind.String ? input.GetString() ?? "" : c.Str("hint");
                list.Add(new SlashCommandInfo(name, c.Str("description"), hint));
            }
            _ompCommands = list;
            PostCommands();
        }

        private void PostCommands()
        {
            var all = new List<SlashCommandInfo>(_ompCommands);
            foreach (var local in SlashRoutes.LocalCommands(Korean))
                if (!all.Any(c => string.Equals(c.Name, local.Name, StringComparison.OrdinalIgnoreCase)))
                    all.Add(local);
            _page.Post(ChatPageMessages.Commands(all));
        }

        private IReadOnlyCollection<string> OmpCommandNames => _ompCommands.Select(c => c.Name).ToArray();
    }
}
