using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Python;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    /// <summary>ChatController의 외부 의존(테스트에서 교체). 기본값이 실제 환경이다.</summary>
    internal sealed class ChatControllerServices
    {
        public IOmpProcessFactory ProcessFactory { get; init; } = new OmpProcessFactory();
        public IChatClock Clock { get; init; } = SystemChatClock.Instance;
        /// <summary>null이면 WinForms 모달 구현.</summary>
        public IChatDialogs? Dialogs { get; init; }
        /// <summary>null이면 %TEMP%\NanumCsvViewer\rpc.log.</summary>
        public IRpcLog? Log { get; init; }
        /// <summary>null이면 StartAsync를 부른 스레드의 SynchronizationContext.</summary>
        public SynchronizationContext? Ui { get; init; }
        /// <summary>true면 250ms WinForms 타이머가 Tick()을 부른다. 테스트는 직접 Tick()을 부른다.</summary>
        public bool AutoTick { get; init; } = true;
        /// <summary>omp 찾기·검증: (설정 경로, 캐시된 검증 값, 취소) → 결과. 테스트는 가짜로 교체한다.</summary>
        public Func<string?, string?, CancellationToken, Task<OmpDiscoveryResult>> DiscoverOmp { get; init; } =
            (configured, cache, ct) => OmpDiscovery.DiscoverAsync(configured, cache, null, ct);
        /// <summary>채팅 알림의 '이 파일로 설치': (원본, PATH 추가, 취소) → 결과.</summary>
        public Func<string, bool, CancellationToken, Task<OmpInstallResult>> InstallOmpFromFile { get; init; } =
            (src, addToPath, ct) => OmpInstaller.InstallFromFileAsync(src, addToPath, ct);
        /// <summary>채팅 알림의 '다운로드하여 설치'.</summary>
        public Func<IProgress<OmpDownloadProgress>, bool, CancellationToken, Task<OmpInstallResult>> DownloadOmp { get; init; } =
            (progress, addToPath, ct) => OmpInstaller.DownloadAndInstallAsync(progress, ct, addToPath);
        public Func<string?> ReadGuide { get; init; } = OmpLaunch.ReadGuideResource;
        /// <summary>`omp usage --json` 같은 짧은 CLI 호출: (실행 파일, 인자, 작업 폴더) → 표준 출력, 실패는 null. 10초 제한. UI 스레드를 막지 않는다.</summary>
        public Func<string, IReadOnlyList<string>, string, CancellationToken, Task<string?>> RunOmpCli { get; init; } =
            (exe, args, cwd, ct) => OmpLocator.RunAsync(exe, args, cwd, TimeSpan.FromSeconds(10), true, ct);
        /// <summary>omp 세션 저장소 루트(null이면 ~/.omp/agent/sessions).</summary>
        public string? SessionRoot { get; init; }
        /// <summary>로컬 Python 분석 준비(인터프리터 찾기·도구 환경). 테스트는 가짜로 교체한다.</summary>
        internal IPythonSetup LocalPython { get; init; } = new PythonSetup();
        /// <summary>분석 스킬을 풀 루트(null이면 %LOCALAPPDATA%\NanumCsvViewer\skills). 테스트는 임시 폴더로 바꾼다.</summary>
        public string? SkillRoot { get; init; }
        /// <summary>omp의 에이전트 폴더(agent.db 위치). null이면 omp 추가 인자의 --profile·환경 변수(OMP_PROFILE, PI_CODING_AGENT_DIR …)에서 찾는다.</summary>
        public string? AgentDirectory { get; init; }
        /// <summary>omp 사용 기록(모델별 마지막 사용) 읽기. UI 스레드 밖에서 부른다. 테스트는 가짜로 교체한다.</summary>
        internal Func<string, OmpModelUsage.Result> ReadModelUsage { get; init; } = dir => OmpModelUsage.Read(dir);
    }

    /// <summary>채팅이 첫 설정(마법사)을 필요로 하는 이유.</summary>
    public enum AgentSetupReason { WebView, Omp, Login }

    /// <summary>
    /// omp 이벤트 → 채팅 페이지 메시지, 페이지 명령 → omp RPC. omp 자식 프로세스의 수명(시작·중지·강제 재시작·종료 복구)과
    /// host tool 호출, 승인 카드, extension_ui_request를 맡는다. 모든 멤버는 UI 스레드에서 호출한다.
    /// </summary>
    public sealed partial class ChatController : IAgentApprovals, IDisposable
    {
        /// <summary>중지(abort) 뒤 이 시간 안에 턴이 끝나지 않으면 자식을 죽이고 같은 세션으로 다시 시작한다.</summary>
        internal const int StopGraceMs = 5000;
        /// <summary>자식이 스스로 죽었을 때 자동 재시작 최소 간격.</summary>
        internal const int RecoverGapMs = 60000;

        private readonly IChatPage _page;
        private readonly ICsvToolExecutor _tools;
        private readonly ChatControllerServices _svc;
        private readonly IChatClock _clock;
        private readonly IRpcLog _log;
        private readonly bool _ownsLog;
        private readonly ChatStream _stream;
        private readonly ChatActivity _activity;
        private IChatDialogs? _dialogs;
        private AgentHostOptions _options;
        private System.Windows.Forms.Timer? _timer;

        private OmpRpcClient? _client;
        private HostToolDispatcher? _dispatcher;
        private int _launchId;
        private string _workDir = "";
        private bool _connected;
        private bool _disposed;
        private string _statusText = "";
        private bool _statusError;
        private ChatStatus? _lastStatus;
        private string _lastSummary = "";
        private long? _lastRecoverTick;
        private long? _abortAt;
        private long _abortTurn;
        private OmpVersion? _ompVersion;
        private string? _ompExe;
        private int _pageReadyCount;
        private static int s_instances;
        /// <summary>임시 파일 이름 접미사: 첫 인스턴스는 프로세스 id, 이후는 id-순번(같은 프로세스의 인스턴스끼리 충돌 방지).</summary>
        private readonly string _supportTag = NextSupportTag();

        private static string NextSupportTag()
        {
            int n = Interlocked.Increment(ref s_instances);
            return n == 1 ? Environment.ProcessId.ToString() : $"{Environment.ProcessId}-{n}";
        }

        public ChatController(IChatPage page, ICsvToolExecutor tools, AgentHostOptions options)
            : this(page, tools, options, new ChatControllerServices())
        {
        }

        internal ChatController(IChatPage page, ICsvToolExecutor tools, AgentHostOptions options, ChatControllerServices services)
        {
            _page = page;
            _tools = tools;
            _options = options;
            _svc = services;
            _clock = services.Clock;
            _dialogs = services.Dialogs;
            _ownsLog = services.Log == null;
            _log = services.Log ?? new FileRpcLog();
            _stream = new ChatStream(_page.Post, _clock, () => Korean);
            _activity = new ChatActivity(_clock, () => Korean);
            _page.Received += OnPageMessage;
            if (_tools is IToolNotices notices) notices.Notice += OnToolNotice;
        }

        /// <summary>omp가 떠 있고 호스트 도구까지 등록된 상태(프롬프트를 보낼 수 있음).</summary>
        public bool IsRunning => _client != null && _connected;

        /// <summary>에이전트가 작업 중이거나 omp 셸 명령이 실행 중.</summary>
        public bool IsBusy => _activity.Busy || _shellRunning;

        /// <summary>상태 문구가 바뀔 때(UI 스레드): 연결 중/오류/작업 중 활동/연결됨.</summary>
        public event Action<string>? StatusChanged;

        /// <summary>
        /// AI 설정이 필요할 때(UI 스레드): omp를 찾지 못했거나 실행할 수 없음(Omp), 모델 로그인이 없음(Login).
        /// 두 번째 인자는 진단 설명(없으면 빈 문자열). 호스트가 설정 마법사를 열 수 있다.
        /// </summary>
        public event Action<AgentSetupReason, string>? SetupNeeded;

        /// <summary>채팅 알림의 버튼이 omp 경로를 골랐을 때(UI 스레드): 호스트가 AgentOmpPath로 저장한다.</summary>
        public event Action<string>? OmpPathPicked;

        /// <summary>확인한 omp 경로·버전·파일 지문(UI 스레드): 호스트가 설정에 캐시해 다음 시작의 --version 실행을 줄인다.</summary>
        public event Action<string>? OmpVerifiedChanged;

        /// <summary>
        /// 컨트롤러가 처리하지 않는 페이지 메시지(settings, openFile, listColumns, setApproval …). 호스트(Form1)가 처리한다.
        /// </summary>
        public event Action<JsonElement>? PageMessageUnhandled;

        /// <summary>설정(언어·데이터 정책·omp 경로)이 바뀌면 새 값을 넘긴다. omp 경로·인자는 다음 (재)시작부터 반영.</summary>
        public AgentHostOptions Options
        {
            get => _options;
            set
            {
                bool languageChanged = _options.IsKorean != value.IsKorean;
                var previousLimits = _options.Limits;
                _options = value;
                if (languageChanged) PostCommands();
                // 작업 공간 설정이 앱 설정을 새로 조였으면(작업 공간 열기·설정 변경) 채팅에 한 번 알린다.
                if (value.Limits.Any && value.Limits != previousLimits)
                    _stream.Emit(ChatPageMessages.Notice("info", WorkspaceAgentPolicy.LimitTooltip(value, Korean)));
                RefreshStatus(force: true);
                // 로컬 Python 켜기/끄기·결과 폴더·데이터 정책이 바뀌면 쉬는 대로(작업 중이면 턴 뒤) 같은 대화로 다시 시작한다.
                RunPendingWorkspaceRestart();
            }
        }

        private bool Korean => _options.IsKorean;
        private string T(string en, string ko) => Korean ? ko : en;
        private IChatDialogs Dialogs => _dialogs ??= new WinFormsChatDialogs(() => Korean);

        // ---- 시작/중지 -----------------------------------------------------------------------------------------

        /// <summary>omp를 찾고(<see cref="OmpDiscovery"/>: 설정 → PATH → %LOCALAPPDATA%\omp) 버전을 확인한 뒤 자식을 띄워 핸드셰이크한다. 실패는 예외 대신 상태 표시줄/알림(찾아보기·설치·다운로드·다시 시도 버튼 포함)으로 보고한다.</summary>
        public Task StartAsync(string workingDirectory, CancellationToken cancellation = default) =>
            StartCoreAsync(workingDirectory, null, cancellation);

        /// <summary>자식을 끄고(사용자 승인 카드는 거절, 실행 중 도구는 취소) 상태를 '연결 안 됨'으로 둔다.</summary>
        public void Stop()
        {
            if (_disposed) return;
            TearDown(force: false);
            if (_activity.Busy) EndTurn(stopped: true);
            _shellRunning = false;
            StopTimer();
            _prewarming = false;
            SetStatus(T("Not connected", "연결 안 됨"), false);
        }

        /// <summary>같은 세션으로 omp를 다시 시작(설정 변경 등). 작업 중이면 먼저 중단된다.</summary>
        public Task RestartAsync() => RestartSessionAsync();

        public void Dispose()
        {
            if (_disposed) return;
            try { _pythonCts?.Cancel(); } catch { }
            try { _attachCts?.Cancel(); } catch { }
            try { _ompActionCts?.Cancel(); } catch { }
            TearDown(force: true);
            _disposed = true;
            _page.Received -= OnPageMessage;
            if (_tools is IToolNotices unsub) unsub.Notice -= OnToolNotice;
            StopTimer();
            SupportFiles.Cleanup(_supportTag);
            if (_ownsLog) (_log as IDisposable)?.Dispose();
        }

        private async Task StartCoreAsync(string workingDirectory, string? resumeSession, CancellationToken cancellation, bool resumeViaCli = false)
        {
            if (_disposed) return;
            TearDown(force: true);
            int launch = ++_launchId;
            if (!string.IsNullOrEmpty(workingDirectory)) _baseDir = workingDirectory;
            _workDir = _options.AllowLocalPython
                ? AgentWorkspace.StableOutputFolderFor(_workspaceFile, _firstDataFile)
                : Directory.Exists(_baseDir) ? _baseDir : Environment.CurrentDirectory;
            _launchedDesired = DesiredWorkDir();
            _evalApproval.Reset();
            SetStatus(T("Connecting to omp…", "omp에 연결하는 중…"), false);
            EnsureTimer();

            try
            {
                var found = await _svc.DiscoverOmp(_options.OmpPath, _options.OmpVerified, cancellation);
                if (launch != _launchId || _disposed) return;
                _lastDiscovery = found;
                string? exe = _ompExe = found.ChosenExe;
                if (!found.IsOk || exe == null)
                {
                    FailOmp(found);
                    return;
                }
                _ompVersion = found.Version;
                if (found.VerifiedCache != null && found.VerifiedCache != _options.OmpVerified)
                {
                    _options = _options with { OmpVerified = found.VerifiedCache };
                    OmpVerifiedChanged?.Invoke(found.VerifiedCache);
                }

                await PrepareSkillsAsync(exe, launch, cancellation);
                if (launch != _launchId || _disposed) return;
                string? pythonSection = await PreparePythonAsync(exe, launch, cancellation);
                if (launch != _launchId || _disposed) return;
                _launchedApproval = _options.ApprovalMode;
                // 작업 공간 메모는 (다시) 시작할 때마다 가이드에 실린다 — 이어받은 대화에서도 같다.
                string? notesSection = WorkspaceNotesGuide.Build(_workspaceContext.Notes, Korean);
                string? guideExtra = string.Join("\n\n", new[] { pythonSection, notesSection }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var (hostConfig, guide) = SupportFiles.Write(_supportTag, _svc.ReadGuide(), _options.Language, guideExtra.Length == 0 ? null : guideExtra, _launchedApproval, _managedPython);
                // 작업 폴더가 바뀐 재시작은 omp가 RPC switch_session을 거절한다(다른 cwd의 세션). 명령줄 --resume은 폴더가 달라도 이어 간다.
                string? cliResume = resumeViaCli && !string.IsNullOrEmpty(resumeSession) ? resumeSession : null;
                if (cliResume != null) resumeSession = null;
                var args = OmpLaunch.BuildArguments(_workDir, hostConfig, guide, _options.ExtraArgs, cliResume, _skillsOverlay);
                var info = new OmpLaunchInfo(exe, args, _workDir, OmpLaunch.StderrLogPath(_supportTag));

                var client = new OmpRpcClient(_svc.ProcessFactory, _svc.Ui, _log);
                client.Frame += json => OnFrame(client, json);
                client.Exited += exit => OnClientExited(client, exit);
                client.Diagnostic += text => { if (ReferenceEquals(client, _client)) _stream.Emit(ChatPageMessages.Notice("warn", text)); };
                _client = client;
                _dispatcher = new HostToolDispatcher(_tools, this, client.Send, _log, Korean);
                _dispatcher.Finished += OnHostToolFinished;
                _log.Header($"NanumCsvViewer {_options.AppVersion} omp {_ompVersion?.ToString() ?? "?"} cwd={_workDir}");

                await client.StartAsync(info, cancellation);
                if (launch != _launchId || _disposed) return;
                await HandshakeAsync(client, launch, resumeSession);
            }
            catch (OperationCanceledException)
            {
                if (launch == _launchId) Fail(T("Connection cancelled.", "연결이 취소되었습니다."));
            }
            catch (Exception ex)
            {
                if (launch == _launchId) Fail(T("Cannot start omp: ", "omp를 시작할 수 없습니다: ") + ex.Message);
            }
        }

        /// <summary>계획 §5 순서: ready/negotiate(클라이언트) → set_host_tools → get_state, get_available_commands → get_available_models, get_available_thinking_levels.</summary>
        private async Task HandshakeAsync(OmpRpcClient client, int launch, string? resumeSession)
        {
            client.TryRequest(id => RpcProtocol.SetHostTools(id, _tools.Definitions), out var toolsTask);
            RequestState();
            RequestCommands();
            RequestModels();
            RequestLevels();
            RequestProviders();

            JsonElement reply = await toolsTask;
            if (launch != _launchId || !ReferenceEquals(client, _client)) return;
            if (reply.Bool("success") != true)
            {
                Fail(T("omp rejected the CSV tools: ", "omp가 CSV 도구 등록을 거절했습니다: ") + reply.Str("error"));
                return;
            }
            _statusError = false;
            if (!string.IsNullOrEmpty(resumeSession))
            {
                // 이어받기가 끝나기 전에 입력을 받으면 진행 중인 턴 때문에 omp가 전환을 취소한다(실제 omp로 확인): 끝난 뒤에 연결됨으로 본다.
                client.TryRequest(id => RpcProtocol.SwitchSession(id, resumeSession), out var switchTask);
                JsonElement switched = await switchTask;
                if (launch != _launchId || !ReferenceEquals(client, _client)) return;
                if (switched.Bool("success") != true)
                    _stream.Emit(ChatPageMessages.Notice("warn", T("Could not resume the previous session: ", "이전 세션을 이어 가지 못했습니다: ") + switched.Str("error")));
                else if (switched.Child("data").Bool("cancelled") == true)
                    _stream.Emit(ChatPageMessages.Notice("warn", T("omp did not resume the previous session; this is a new conversation.", "omp가 이전 세션을 이어 가지 않았습니다. 새 대화로 시작합니다.")));
                RequestState();
            }
            _connected = true;
            _prewarming = false;
            if (_historyOnConnect)
            {
                // 작업 공간의 대화를 이어받았다: 화면은 비어 있으니 omp에 저장된 기록을 다시 불러온다.
                _historyOnConnect = false;
                RequestState();
                LoadHistory(done: () => _stream.Emit(ChatPageMessages.Notice("info",
                    T("Resumed this workspace's previous conversation.", "이 작업 공간의 이전 대화를 이어 갔습니다."))));
            }
            SetStatus("", false);
            RefreshStatus(force: true);
            PostApprovalNoticeOnce();
            FlushPendingSubmit();   // 첫 메시지로 시작한 경우: 이제 그 메시지를 보낸다(그러면 작업 중이 되어 아래 재시작은 턴 뒤로 미뤄진다)
            RunPendingWorkspaceRestart();
        }

        private void Fail(string message, string? noticeText = null)
        {
            _connected = false;
            _prewarming = false;
            DropPendingSubmit();
            TearDown(force: true);
            SetStatus(message, true);
            _stream.Emit(ChatPageMessages.Notice("error", noticeText ?? message));
        }

        /// <summary>대기 중인 승인·도구를 정리하고 자식을 끈다(세대가 바뀌어 늦은 콜백은 버려진다).</summary>
        private void TearDown(bool force)
        {
            _launchId++;
            _stateInFlight = false;
            _stateAgain = false;
            _shellRunning = false;
            RefuseAllApprovals();
            _dispatcher?.CancelAll();
            _abortAt = null;
            _connected = false;
            var client = _client;
            _client = null;
            _dispatcher = null;
            client?.Stop(force);
        }

        private async Task RestartSessionAsync()
        {
            if (IsStartDeferred) return;   // 아직 시작한 적이 없다: 설정은 첫 메시지 때 반영된다
            string? resume = _sessionFile;
            bool otherFolder = FolderStale();   // 새 작업 폴더로 가는 재시작: 세션은 명령줄로 이어받는다
            if (_activity.Busy) { _activity.NoteStopRequested(); TearDown(force: true); EndTurn(stopped: true); }
            try { await StartCoreAsync(_baseDir, string.IsNullOrEmpty(resume) ? null : resume, default, otherFolder); }
            catch (Exception ex) { _log.Note("restart failed: " + ex); }
        }

        // ---- 중지 / 종료 복구 ----------------------------------------------------------------------------------

        /// <summary>
        /// 중지 버튼: 승인 카드를 거절하고 abort를 보낸다. 같은 턴이 5초 안에 끝나지 않거나 한 번 더 누르면
        /// 자식을 죽이고 같은 세션으로 다시 시작한다(제공자 요청에 걸린 자식은 abort에도 답하지 않는다).
        /// </summary>
        public void StopTurn()
        {
            var client = _client;
            RefuseAllApprovals();
            _activity.NoteStopRequested();
            if (_shellRunning)
            {
                client?.SendCommand("abort_bash");
                return;
            }
            if (client == null || !_connected) return;
            if (!_activity.Busy)
            {
                client.SendCommand("abort");
                return;
            }
            if (_abortAt != null && _activity.StartTick == _abortTurn)
            {
                ForceStop();
                return;
            }
            client.SendCommand("abort");
            _stream.Emit(ChatPageMessages.Notice("info", T("Stopping…", "중지하는 중…")));
            _abortAt = _clock.TickMs;
            _abortTurn = _activity.StartTick;
        }

        private void ForceStop()
        {
            _abortAt = null;
            string? resume = _sessionFile;
            _stream.Emit(ChatPageMessages.Notice("warn", T("omp did not stop in time; restarting it on the same session.", "omp가 제때 멈추지 않아 같은 세션으로 다시 시작합니다.")));
            _activity.NoteStopRequested();
            TearDown(force: true);
            EndTurn(stopped: true);
            _ = ResumeAsync(resume);
        }

        private async Task ResumeAsync(string? resume)
        {
            try { await StartCoreAsync(_baseDir, string.IsNullOrEmpty(resume) ? null : resume, default); }
            catch (Exception ex) { _log.Note("resume failed: " + ex); }
        }

        private void OnClientExited(OmpRpcClient client, OmpExitInfo info)
        {
            if (_disposed || !ReferenceEquals(client, _client)) return;
            bool wasConnected = _connected;
            string? resume = _sessionFile;
            TearDown(force: true);
            if (_activity.Busy) { _activity.NoteStopRequested(); EndTurn(stopped: true); }

            string reason = string.IsNullOrWhiteSpace(info.LastErrorLine) ? "" : " " + info.LastErrorLine!.Trim();
            long now = _clock.TickMs;
            if (wasConnected && (_lastRecoverTick == null || now - _lastRecoverTick.Value >= RecoverGapMs))
            {
                _lastRecoverTick = now;
                _stream.Emit(ChatPageMessages.Notice("warn", T($"omp exited.{reason} Restarting on the same session…", $"omp가 종료되었습니다.{reason} 같은 세션으로 다시 시작합니다…")));
                SetStatus(T("Restarting omp…", "omp를 다시 시작하는 중…"), false);
                _ = ResumeAsync(resume);
            }
            else
            {
                string msg = T($"omp exited.{reason}", $"omp가 종료되었습니다.{reason}");
                SetStatus(msg, true);
                _stream.Emit(ChatPageMessages.Notice("error", msg));
                DropPendingSubmit();
            }
        }

        /// <summary>주기 처리(250 ms): 중지 유예 감시와 작업 시간 표시 갱신. 테스트/호스트가 직접 부를 수 있다.</summary>
        public void Tick()
        {
            if (_disposed) return;
            if (_abortAt is long at)
            {
                if (!_activity.Busy || _activity.StartTick != _abortTurn) _abortAt = null;
                else if (_clock.TickMs - at >= StopGraceMs) ForceStop();
            }
            RefreshStatus();
        }

        private void EnsureTimer()
        {
            if (!_svc.AutoTick || _timer != null) return;
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
        }

        // ---- 상태 표시 -----------------------------------------------------------------------------------------

        private void SetStatus(string text, bool error)
        {
            _statusText = text;
            _statusError = error;
            RefreshStatus(force: true);
        }

        /// <summary>status 메시지는 내용이 바뀐 때만 보낸다(작업 시간 초 단위 갱신 포함).</summary>
        private void RefreshStatus(bool force = false)
        {
            if (_disposed) return;
            var s = new ChatStatus
            {
                State = _statusText,
                Error = _statusError,
                Connected = _connected,
                Ready = _connected || IsStartDeferred || _prewarming,
                Busy = _activity.Busy || _shellRunning,
                Shell = _shellRunning,
                Activity = _activity.Busy ? _activity.Text : "",
                Model = _model,
                Thinking = _thinking,
                Context = _context,
                Title = _sessionName,
                Project = ProjectName,
                Cwd = _workDir,
                Pid = _client?.ProcessId ?? 0,
                Approval = AgentApprovalPolicy.ToOmp(_options.EffectiveApprovalMode),
                ApprovalLocked = _options.ForcedApproval is { } forced ? ApprovalTexts.Locked(forced.Flag, Korean) : "",
                WorkspaceLimit = WorkspaceAgentPolicy.LimitTooltip(_options, Korean),
                WorkspaceLimitLabel = _options.Limits.Any ? WorkspaceAgentPolicy.LimitLabel(Korean) : "",
            };
            if (!force && s == _lastStatus) return;
            _lastStatus = s;
            _page.Post(ChatPageMessages.Status(s));

            string summary = _statusError || !_connected ? _statusText
                : s.Busy ? s.Activity
                : T("Connected", "연결됨") + (_ompVersion != null ? $" · omp {_ompVersion}" : "");
            if (summary != _lastSummary)
            {
                _lastSummary = summary;
                StatusChanged?.Invoke(summary);
            }
        }

        private string ProjectName
        {
            get
            {
                string dir = _workDir.TrimEnd('\\', '/');
                string name = Path.GetFileName(dir);
                return name.Length > 0 ? name : dir;
            }
        }

        // ---- 보조 ----------------------------------------------------------------------------------------------

        /// <summary>RPC 요청을 보내고, 같은 클라이언트가 살아 있는 동안 응답을 UI 컨텍스트에서 처리한다. 보내지 못하면 false.</summary>
        private bool Ask(string type, Action<JsonObject>? fill, Action<JsonElement>? onOk = null, Action<string>? onFail = null)
        {
            var client = _client;
            if (client == null || !client.TryRequest(type, fill, out var task)) return false;
            Track(client, task, type, onOk, onFail);
            return true;
        }

        private async void Track(OmpRpcClient client, Task<JsonElement> task, string type, Action<JsonElement>? onOk, Action<string>? onFail)
        {
            try
            {
                JsonElement reply = await task;
                if (!ReferenceEquals(client, _client)) return;
                if (reply.Bool("success") == true) onOk?.Invoke(reply.Child("data"));
                else
                {
                    string err = reply.Str("error");
                    if (onFail != null) onFail(err);
                    else _stream.Emit(ChatPageMessages.Notice("error", $"{type}: {err}"));
                }
            }
            catch (OperationCanceledException) { }
            catch (OmpExitedException) { }
            catch (Exception ex) { _log.Note($"{type} handler failed: {ex}"); }
        }
    }

    /// <summary>임시 폴더의 host.yml·가이드 파일 관리.</summary>
    internal static class SupportFiles
    {
        public static (string HostConfig, string? Guide) Write(string tag, string? guide, string language, string? extraSection = null,
            AgentApprovalMode approvalMode = AgentApprovalPolicy.Default, string? pythonInterpreter = null) =>
            OmpLaunch.WriteSupportFiles(tag, guide, language, extraSection, approvalMode, pythonInterpreter);

        public static void Cleanup(string tag)
        {
            foreach (string path in new[] { OmpLaunch.HostConfigPath(tag), OmpLaunch.SkillsOverlayPath(tag), OmpLaunch.GuidePath(tag) })
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
