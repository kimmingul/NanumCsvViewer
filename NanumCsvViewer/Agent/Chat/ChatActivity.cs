namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>시간 원천(테스트에서 교체). TickMs는 단조 시계, UnixMs는 1970 기준 UTC ms.</summary>
    internal interface IChatClock
    {
        long TickMs { get; }
        long UnixMs { get; }
    }

    internal sealed class SystemChatClock : IChatClock
    {
        public static readonly SystemChatClock Instance = new();
        public long TickMs => Environment.TickCount64;
        public long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    internal enum ActivityState { Idle, Waiting, Thinking, Writing, PreparingTool, RunningTool, Compacting, Retrying }

    /// <summary>에이전트가 지금 무엇을 하는지(RPC 이벤트에서 유도). Busy면 입력창이 '실행 중' 모드가 된다.</summary>
    internal sealed class ChatActivity
    {
        private readonly IChatClock _clock;
        private readonly Func<bool> _koreanFn;
        private bool _korean => _koreanFn();
        private string _toolName = "";
        private int _runningTools;
        private long _startedAt;
        private bool _turnOpen;

        public ChatActivity(IChatClock clock, Func<bool> korean)
        {
            _clock = clock;
            _koreanFn = korean;
        }

        public ActivityState State { get; private set; } = ActivityState.Idle;
        public bool StopRequested { get; private set; }
        /// <summary>현재 턴이 시작된 단조 시계 값(같은 턴 식별용).</summary>
        public long StartTick { get; private set; }
        public bool Busy => State != ActivityState.Idle;

        private void Start()
        {
            StartTick = _clock.TickMs;
            _startedAt = _clock.UnixMs;
            StopRequested = false;
            _turnOpen = true;
            State = ActivityState.Waiting;
        }

        public void PromptSent()
        {
            if (State == ActivityState.Idle) Start();
        }

        public void NoteStopRequested()
        {
            if (State != ActivityState.Idle) StopRequested = true;
        }

        /// <summary>prompt가 거절되었거나 보내지 못했다: 턴을 시작하지 않은 것으로 되돌린다.</summary>
        public void AbandonTurn()
        {
            Reset();
            _turnOpen = false;
        }

        /// <summary>시작된 턴마다 한 번만 true와 시작 시각(ms, 1970 UTC)을 준다.</summary>
        public bool CloseTurn(out long startedAt)
        {
            bool open = _turnOpen;
            startedAt = _startedAt;
            _turnOpen = false;
            return open;
        }

        public void Reset()
        {
            State = ActivityState.Idle;
            _toolName = "";
            _runningTools = 0;
        }

        public void Apply(AgentEvent ev)
        {
            switch (ev.Kind)
            {
                case AgentEventKind.AgentStart:
                    if (State == ActivityState.Idle) Start();
                    break;
                case AgentEventKind.AgentEnd:
                    if (ev.IsTerminal) Reset();
                    break;
                case AgentEventKind.Thinking:
                    if (_runningTools == 0) State = ActivityState.Thinking;
                    break;
                case AgentEventKind.TextDelta:
                    if (_runningTools == 0) State = ActivityState.Writing;
                    break;
                case AgentEventKind.ToolCallStart:
                    _toolName = ev.ToolName;
                    State = ActivityState.PreparingTool;
                    break;
                case AgentEventKind.ToolStart:
                    _runningTools++;
                    _toolName = ev.ToolName;
                    State = ActivityState.RunningTool;
                    break;
                case AgentEventKind.ToolEnd:
                    if (_runningTools > 0) _runningTools--;
                    if (_runningTools == 0) State = ActivityState.Waiting;
                    break;
                case AgentEventKind.CompactionStart:
                    State = ActivityState.Compacting;
                    break;
                case AgentEventKind.RetryStart:
                    State = ActivityState.Retrying;
                    break;
                case AgentEventKind.CompactionEnd:
                case AgentEventKind.RetryEnd:
                    if (State != ActivityState.Idle) State = ActivityState.Waiting;
                    break;
                case AgentEventKind.Error:
                case AgentEventKind.PromptLocal:
                    // 거절된 prompt나 omp가 직접 처리한 슬래시 명령은 턴을 시작하지 않는다.
                    if (State == ActivityState.Waiting) { Reset(); _turnOpen = false; }
                    break;
            }
        }

        public int ElapsedSeconds => State == ActivityState.Idle ? 0 : (int)((_clock.TickMs - StartTick) / 1000);

        /// <summary>작업 줄 문구. 예: "도구 실행 중: csv.sort · 4초"</summary>
        public string Text
        {
            get
            {
                string s = State switch
                {
                    ActivityState.Waiting => _korean ? "응답 기다리는 중" : "Waiting for the model",
                    ActivityState.Thinking => _korean ? "생각하는 중" : "Thinking",
                    ActivityState.Writing => _korean ? "답변 작성 중" : "Writing the answer",
                    ActivityState.PreparingTool => (_korean ? "도구 준비 중: " : "Preparing tool: ") + _toolName,
                    ActivityState.RunningTool => (_korean ? "도구 실행 중: " : "Running tool: ") + _toolName,
                    ActivityState.Compacting => _korean ? "대화 압축 중" : "Compacting the conversation",
                    ActivityState.Retrying => _korean ? "재시도 대기 중" : "Waiting to retry",
                    _ => _korean ? "대기" : "Idle",
                };
                return State == ActivityState.Idle ? s : $"{s} · {ElapsedSeconds}{(_korean ? "초" : "s")}";
            }
        }
    }
}
