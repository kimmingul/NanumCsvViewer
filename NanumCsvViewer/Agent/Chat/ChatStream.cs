namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>
    /// 에이전트 이벤트를 채팅 페이지 메시지로 바꾼다(텍스트·생각·도구·하위 에이전트·알림). 실시간 델타는 화면에만 보내고,
    /// 기록(transcript)에는 블록이 끝날 때 한 메시지씩만 남겨 다시 붙은 화면이 간결하게 재생되게 한다.
    /// </summary>
    internal sealed class ChatStream
    {
        private const int MaxTranscript = 4000;
        private const int MaxInputChars = 20000;
        private const int MaxUpdateChars = 8000;
        private const int UpdateIntervalMs = 250;
        private const int MaxResultChars = 20000;

        private readonly Action<string> _post;
        private readonly IChatClock _clock;
        private readonly Func<bool> _koreanFn;
        private bool _korean => _koreanFn();
        private readonly List<string> _transcript = new();
        private readonly Dictionary<string, string> _inputs = new();
        private readonly Dictionary<string, long> _starts = new();
        private readonly Dictionary<string, long> _lastUpdate = new();
        private string _text = "", _thinking = "", _todos = "", _model = "";

        public ChatStream(Action<string> post, IChatClock clock, Func<bool> korean)
        {
            _post = post;
            _clock = clock;
            _koreanFn = korean;
        }

        /// <summary>기록에 남기고 화면에도 보낸다.</summary>
        public void Emit(string json)
        {
            Flush();
            _transcript.Add(json);
            if (_transcript.Count > MaxTranscript) _transcript.RemoveRange(0, _transcript.Count - MaxTranscript);
            _post(json);
        }

        /// <summary>화면에만 보낸다(기록 없음).</summary>
        public void PostOnly(string json) => _post(json);

        /// <summary>끝나지 않은 텍스트/생각을 기록에 한 메시지로 넣는다(화면에는 이미 있음).</summary>
        private void Flush()
        {
            if (_thinking.Length > 0) _transcript.Add(ChatPageMessages.Thinking(_thinking));
            if (_text.Length > 0) _transcript.Add(ChatPageMessages.Delta(_text));
            _thinking = "";
            _text = "";
        }

        public void Clear()
        {
            _transcript.Clear();
            _inputs.Clear();
            _starts.Clear();
            _lastUpdate.Clear();
            _text = _thinking = _todos = _model = "";
        }

        /// <summary>새로 붙은 화면이 따라잡도록 기록 + 끝나지 않은 블록.</summary>
        public IReadOnlyList<string> Replay()
        {
            var list = new List<string>(_transcript);
            if (_thinking.Length > 0) list.Add(ChatPageMessages.ThinkingDelta(_thinking));
            if (_text.Length > 0) list.Add(ChatPageMessages.Delta(_text));
            return list;
        }

        /// <summary>할 일 목록이 바뀌었을 때만 보낸다.</summary>
        public void ShowTodos(IReadOnlyList<TodoItem> todos)
        {
            string json = ChatPageMessages.Todos(todos);
            if (json != _todos && (_todos.Length > 0 || todos.Count > 0)) Emit(json);
            _todos = json;
        }

        /// <summary>화면용 메시지를 보냈으면 true. false면 세션이 직접 처리할 이벤트.</summary>
        public bool Apply(AgentEvent ev)
        {
            switch (ev.Kind)
            {
                case AgentEventKind.TextDelta:
                    _text += ev.Text;
                    _post(ChatPageMessages.Delta(ev.Text));
                    return true;
                case AgentEventKind.TextEnd:
                    Emit(ChatPageMessages.AssistantEnd());
                    return true;
                case AgentEventKind.Thinking:
                    if (ev.Text.Length > 0)
                    {
                        _thinking += ev.Text;
                        _post(ChatPageMessages.ThinkingDelta(ev.Text));
                    }
                    return true;
                case AgentEventKind.ThinkingEnd:
                    Emit(ChatPageMessages.ThinkingEnd());
                    return true;
                case AgentEventKind.ToolCallDelta:
                    if (ev.ToolId.Length > 0 && ev.Text.Length > 0)
                    {
                        _inputs.TryGetValue(ev.ToolId, out string? input);
                        input ??= "";
                        if (input.Length < MaxInputChars) _inputs[ev.ToolId] = input + ev.Text;
                        _post(ChatPageMessages.ToolInputDelta(ev.ToolId, ev.ToolName, ev.Text));
                    }
                    return true;
                case AgentEventKind.ToolStart:
                    {
                        _inputs.Remove(ev.ToolId, out string? input);
                        _starts[ev.ToolId] = _clock.TickMs;
                        Emit(ChatPageMessages.ToolStart(ev.ToolId, ev.ToolName, ev.Detail, input ?? ""));
                        return true;
                    }
                case AgentEventKind.ToolUpdate:
                    {
                        // bash는 매 업데이트마다 지금까지의 출력 전체를 보낸다: 간격을 두고 꼬리만 보여 준다.
                        long now = _clock.TickMs;
                        if (_lastUpdate.TryGetValue(ev.ToolId, out long last) && now - last < UpdateIntervalMs) return true;
                        _lastUpdate[ev.ToolId] = now;
                        _post(ChatPageMessages.ToolUpdate(ev.ToolId, Tail(ev.Text, MaxUpdateChars)));
                        return true;
                    }
                case AgentEventKind.ToolEnd:
                    {
                        long now = _clock.TickMs;
                        long started = _starts.TryGetValue(ev.ToolId, out long s) ? s : now;
                        _starts.Remove(ev.ToolId);
                        _lastUpdate.Remove(ev.ToolId);
                        Emit(ChatPageMessages.ToolEnd(ev.ToolId, !ev.IsError, now - started,
                            AgentEventParser.ToolResultPreview(ev.Text, MaxResultChars)));
                        return true;
                    }
                case AgentEventKind.Subagent:
                    {
                        string json = ChatPageMessages.Subagent(ev.ToolId, ev.ToolName, ev.Detail, ev.Text, ev.Level, ev.Count);
                        if (ev.Level == "running") _post(json); else Emit(json);
                        return true;
                    }
                case AgentEventKind.Notice:
                    // omp가 시작마다 알리는 xd:// 장치 목록 알림은 채팅이 이미 안다. 일반 info는 숨길 수 있게 'omp' 수준으로.
                    if (!ev.Text.StartsWith("xd://: mounted", StringComparison.Ordinal))
                        Emit(ChatPageMessages.Notice(ev.Level == "info" ? "omp" : ev.Level, ev.Text));
                    return true;
                case AgentEventKind.Error:
                    Emit(ChatPageMessages.Notice("error", ev.Text));
                    return true;
                case AgentEventKind.CommandOutput:
                    if (ev.Text.Trim().Length > 0) Emit(ChatPageMessages.Notice("output", ev.Text));
                    return true;
                case AgentEventKind.CompactionStart:
                    Emit(ChatPageMessages.Notice("info", _korean ? "대화를 압축하는 중입니다…" : "Compacting the conversation…"));
                    return true;
                case AgentEventKind.RetryStart:
                    Emit(ChatPageMessages.Notice("retry", ev.Text));
                    return true;
                case AgentEventKind.Fallback:
                    Emit(ChatPageMessages.ModelNotice("retry", ev.Text, ev.Detail, ev.ToolName));
                    return true;
                case AgentEventKind.RetryEnd:
                    Emit(ChatPageMessages.Notice(ev.IsError ? "error" : "retry", ev.Text));
                    return true;
                case AgentEventKind.Model:
                    if (ev.Detail.Length > 0 && ev.ToolName + "/" + ev.Detail != _model)
                    {
                        _model = ev.ToolName + "/" + ev.Detail;
                        Emit(ChatPageMessages.Model(_model));
                    }
                    return true;
                default:
                    return false;
            }
        }

        private static string Tail(string text, int max) =>
            text.Length <= max ? text : "…" + text[^max..];
    }
}
