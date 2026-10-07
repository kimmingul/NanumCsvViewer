using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Python;

namespace NanumCsvViewer.Agent
{
    /// <summary>채팅 페이지가 결과 폴더의 파일을 보여 줄 수 있게 가상 호스트를 연결하는 패널 쪽 기능(IChatPage 구현이 선택적으로 제공).</summary>
    internal interface IChatOutputHost
    {
        /// <summary>결과 폴더를 https://nanumcsv-out.local/ 에 연결한다(null이면 해제). 이 폴더 밖의 파일은 페이지에 노출되지 않는다.</summary>
        void SetOutputFolder(string? folder);
    }

    // 로컬 Python 분석: 작업 폴더(결과 폴더), 인터프리터·도구 환경 준비, 가이드 절, 채팅의 그림.
    public sealed partial class ChatController
    {
        /// <summary>결과 폴더 가상 호스트. 페이지의 그림 주소가 이 이름으로 시작한다.</summary>
        internal const string OutputHost = "nanumcsv-out.local";

        private string _baseDir = "";
        private string? _firstDataFile;
        private string? _workspaceFile;
        private AgentWorkspaceContext _workspaceContext = AgentWorkspaceContext.Empty;
        private string _outputFolder = "";
        private PythonInterpreter? _pyInterpreter;
        private LspStatus _lspStatus = LspStatus.Unavailable;
        private AgentDataPolicy _launchedPolicy;
        private CancellationTokenSource? _pythonCts;
        /// <summary>omp는 시작할 때만 lsp.json을 읽는다(실제 omp 18.4.4로 확인). 이번 실행이 시작된 뒤에 설정이 생겼으면 다시 시작해야 반영된다.</summary>
        private bool _lspRestartPending;
        /// <summary>이번 omp 실행이 host.yml의 python.interpreter로 받은 관리 환경 python.exe(쓰지 않았으면 null).</summary>
        private string? _managedPython;

        /// <summary>로컬 Python 분석이 켜져 있고 omp가 결과 폴더에서 실행 중일 때 그 폴더. 아니면 null.</summary>
        public string? OutputFolder => _options.AllowLocalPython && _outputFolder.Length > 0 ? _outputFolder : null;

        /// <summary>
        /// 지금 분석 폴더(만들지 않는다): 로컬 Python이 켜져 있으면 omp가 쓰는(또는 다음에 쓸) 작업 공간 고정 폴더, 꺼져 있으면 null.
        /// csv.export_view 등이 데이터를 내보낼 곳이며, 탭·시트를 바꿔도 달라지지 않는다.
        /// </summary>
        public string? AnalysisFolder
        {
            get
            {
                if (!_options.AllowLocalPython) return null;
                string desired = DesiredWorkDir();
                // 폴더를 만들 수 없어 대체 폴더로 시작했다면 omp가 실제로 쓰는 폴더를 알려 준다.
                return _workDir.Length > 0 && string.Equals(desired, _launchedDesired, StringComparison.OrdinalIgnoreCase) ? _workDir : desired;
            }
        }

        /// <summary>
        /// 작업 공간 상태(작업 공간 파일·열린 탭)를 알린다. 분석 폴더는 작업 공간 파일(있으면), 없으면 이 세션에서 처음 연 데이터 파일로 정해지고
        /// 그 뒤로 탭·시트 전환, 탭 열기·닫기로는 바뀌지 않으므로 omp를 다시 시작하지 않는다. 폴더가 달라지는 때(처음 데이터 파일이 열렸을 때,
        /// 작업 공간 파일이 저장·열렸을 때)만 에이전트가 쉬는 대로(작업 중이면 턴이 끝난 뒤) 같은 대화로 다시 시작한다. 로컬 Python이 꺼져 있으면 재시작 없음.
        /// </summary>
        public void SetWorkspaceContext(AgentWorkspaceContext context)
        {
            ApplyWorkspaceContext(context);
            RunPendingWorkspaceRestart();
        }

        private void ApplyWorkspaceContext(AgentWorkspaceContext context)
        {
            _workspaceContext = context ?? AgentWorkspaceContext.Empty;
            _firstDataFile ??= _workspaceContext.FirstDataFile;
            _workspaceFile = string.IsNullOrWhiteSpace(_workspaceContext.WorkspaceFile) ? null : _workspaceContext.WorkspaceFile;
        }

        /// <summary>omp가 지금 써야 할 작업 폴더(폴더를 만들지 않는다).</summary>
        private string DesiredWorkDir() =>
            _options.AllowLocalPython
                ? AgentWorkspace.StableOutputFolder(_workspaceFile, _firstDataFile)
                : Directory.Exists(_baseDir) ? _baseDir : Environment.CurrentDirectory;

        /// <summary>마지막 시작 때 계산한 원하는 작업 폴더(폴더를 못 만들어 대체 폴더를 쓴 경우에도 같은 값이라 재시작을 되풀이하지 않는다).</summary>
        private string _launchedDesired = "";

        private bool FolderStale() =>
            !string.Equals(DesiredWorkDir(), _launchedDesired, StringComparison.OrdinalIgnoreCase);

        /// <summary>실행 중인 omp의 작업 폴더·Python 가이드·진단 설정·승인 모드(host.yml)가 지금 설정과 다른가.</summary>
        private bool WorkspaceStale() =>
            FolderStale() || ApprovalStale() || SkillsStale() || ManagedPythonStale() || (_options.AllowLocalPython && (_options.DataPolicy != _launchedPolicy || _lspRestartPending));

        /// <summary>쉬는 중(연결됨, 작업 없음)이고 작업 공간이 낡았으면 같은 대화로 다시 시작한다. 작업 중이면 EndTurn이 다시 부른다.</summary>
        private void RunPendingWorkspaceRestart()
        {
            if (_disposed || !_connected || IsBusy || _client == null || !WorkspaceStale()) return;
            string text = FolderStale() && _options.AllowLocalPython
                ? T("Switching the analysis folder; restarting the agent on the same conversation…", "분석 결과 폴더를 바꾸기 위해 같은 대화로 에이전트를 다시 시작합니다…")
                : ApprovalStale()
                    ? T($"Applying the new approval mode ({AgentApprovalPolicy.ToOmp(_options.ApprovalMode)}); restarting the agent on the same conversation…",
                        $"새 승인 모드({AgentApprovalPolicy.ToOmp(_options.ApprovalMode)})를 반영하기 위해 같은 대화로 에이전트를 다시 시작합니다…")
                    : SkillsStale() && _options.AllowLocalPython
                        ? T("Applying the analysis-skill settings; restarting the agent on the same conversation…", "분석 스킬 설정을 반영하기 위해 같은 대화로 에이전트를 다시 시작합니다…")
                    : ManagedPythonStale()
                        ? T("Switching Python analysis to the managed environment; restarting the agent on the same conversation…", "Python 분석 환경을 바꾸기 위해 같은 대화로 에이전트를 다시 시작합니다…")
                    : !_options.AllowLocalPython
                        ? T("Restarting the agent on the same conversation…", "같은 대화로 에이전트를 다시 시작합니다…")
                        : _lspRestartPending
                            ? T("Restarting the agent on the same conversation so it picks up the Python code diagnostics…", "Python 코드 진단을 반영하기 위해 같은 대화로 에이전트를 다시 시작합니다…")
                            : T("Restarting the agent on the same conversation to apply the new data policy…", "새 데이터 정책을 반영하기 위해 같은 대화로 에이전트를 다시 시작합니다…");
            _stream.Emit(ChatPageMessages.Notice("info", text));
            _ = RestartSessionAsync();
        }

        // ---- 시작 때 준비 --------------------------------------------------------------------------------------

        /// <summary>
        /// 로컬 Python이 켜져 있을 때 omp 시작 전에: 결과 폴더 연결, 인터프리터 찾기, 도구 환경 점검(준비되어 있으면 lsp.json 기록, 아니면
        /// 백그라운드 준비). 가이드의 Python 절을 돌려준다. 꺼져 있으면 null. 실패해도 예외 없이 알림으로만 알리고 나머지는 계속된다.
        /// </summary>
        private async Task<string?> PreparePythonAsync(string? ompExe, int launch, CancellationToken cancellation)
        {
            _pyInterpreter = null;
            _managedPython = null;
            _lspStatus = LspStatus.Unavailable;
            _lspRestartPending = false;
            _launchedPolicy = _options.DataPolicy;
            if (!_options.AllowLocalPython)
            {
                _outputFolder = "";
                (_page as IChatOutputHost)?.SetOutputFolder(null);
                SetImageBase("");
                return null;
            }

            _outputFolder = _workDir;
            (_page as IChatOutputHost)?.SetOutputFolder(_workDir);
            SetImageBase("https://" + OutputHost + "/");

            IReadOnlyList<string> packages = Array.Empty<string>();
            PythonLocateResult located;
            try { located = await _svc.LocalPython.LocateAsync(ompExe, _workDir, cancellation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { located = new PythonLocateResult(null, ex.Message); }
            if (launch != _launchId || _disposed) return null;

            // 앱 관리 분석 환경(core 설치됨)이 있고 사용자가 허용했으면 eval의 Python은 그것이다(host.yml의 python.interpreter). 없으면 지금까지처럼 사용자 Python.
            var managed = DesiredManagedEnvironment();
            PythonInterpreter? effective = located.Found ? located.Interpreter : null;
            if (managed != null)
            {
                _managedPython = managed.PythonPath;
                effective = new PythonInterpreter(managed.PythonPath, managed.PythonVersion ?? new Version(3, 10), "managed");
            }

            if (effective != null)
            {
                _pyInterpreter = effective;
                try { packages = await _svc.LocalPython.PackagesAsync(effective, cancellation); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Note("package probe failed: " + ex.Message); }
                if (launch != _launchId || _disposed) return null;
                PrepareLanguageTools(effective, launch);
            }
            else
            {
                PostPythonMissing(located);
            }
            if (managed == null && effective != null) PostManagedEnvironmentNotice();     // Python이 아예 없으면 위의 "Python 없음" 안내만
            string pythonGuide = PythonGuide.Build(new PythonGuideContext(_workspaceContext, _workDir, managed != null ? effective : located.Interpreter, packages, _lspStatus, _options.DataPolicy, _skills?.Names));
            string envGuide = AnalysisMessages.BuildGuide(_svc.LocalPython.InspectManaged(), _options.UseManagedPython, managed == null ? effective?.Path : null);
            return pythonGuide + "\n\n" + envGuide;
        }

        /// <summary>사용할 관리 환경(허용·core 설치됨·실행 가능). 아니면 null.</summary>
        private AnalysisEnvInfo? DesiredManagedEnvironment()
        {
            if (!_options.AllowLocalPython || !_options.UseManagedPython) return null;
            try
            {
                return _svc.LocalPython.InspectManaged() is { IsReady: true } info && info.Has(AnalysisGroups.Core) ? info : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>omp가 지금 쓰는 Python(host.yml)이 사용자 설정·환경 상태가 정하는 값과 다른가(환경을 만들었거나 지웠거나 토글을 바꿨다).</summary>
        private bool ManagedPythonStale() =>
            _options.AllowLocalPython && !string.Equals(DesiredManagedEnvironment()?.PythonPath, _managedPython, StringComparison.OrdinalIgnoreCase);

        /// <summary>관리 환경 없이 로컬 Python을 처음 쓸 때 한 번만, 설정에서 만들 수 있다고 알린다.</summary>
        private void PostManagedEnvironmentNotice()
        {
            if (!_options.PythonEnvNoticePending || !_options.UseManagedPython) return;
            _options = _options with { PythonEnvNoticePending = false };
            _stream.Emit(ChatPageMessages.LinkNotice("info",
                T("Python analysis uses your own Python. Settings ▸ AI ▸ Python environment can create a managed environment with tested package versions (pandas, scipy, statsmodels, scikit-learn, lifelines …) so analyses are reproducible.",
                  "Python 분석은 사용자의 Python을 쓰고 있습니다. 설정 ▸ AI ▸ Python 환경에서 검증된 버전의 패키지(pandas·scipy·statsmodels·scikit-learn·lifelines 등)가 든 관리 환경을 만들 수 있어 분석을 재현할 수 있습니다."),
                T("Open Python environment settings", "Python 환경 설정 열기"), SettingsUrl));
            PythonEnvNoticeShown?.Invoke();
        }

        /// <summary>채팅 알림의 버튼이 설정 창을 열게 하는 앱 내부 주소(openUrl로 오면 settings 메시지로 바꿔 호스트에 넘긴다).</summary>
        internal const string SettingsUrl = "nanumcsv://settings/ai";

        /// <summary>관리 환경 없이 쓴다는 한 번짜리 안내를 채팅에 보였을 때(호스트가 '보였음'을 설정에 저장한다).</summary>
        public event Action? PythonEnvNoticeShown;

        /// <summary>앱 내부 주소면 처리하고 true. 그 밖의 주소는 호출자가 연다.</summary>
        private bool TryHandleAppUrl(string? url)
        {
            if (TryHandleOmpUrl(url)) return true;
            if (!string.Equals(url, SettingsUrl, StringComparison.OrdinalIgnoreCase)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse("{\"t\":\"settings\"}");
            PageMessageUnhandled?.Invoke(doc.RootElement.Clone());
            return true;
        }

        /// <summary>호스트 도구(py.ensure_packages 등)가 올린 진행 알림을 채팅에 보인다(UI 스레드).</summary>
        private void OnToolNotice(string level, string text)
        {
            if (!_disposed) _stream.Emit(ChatPageMessages.Notice(level, text));
        }

        /// <summary>답변 마크다운의 상대 경로 그림을 풀 주소. 바뀔 때만 보낸다(페이지가 다시 열리면 OnPageReady가 다시 보낸다).</summary>
        private string _imageBase = "";

        private void SetImageBase(string url)
        {
            if (url == _imageBase) return;
            _imageBase = url;
            _page.Post(ChatPageMessages.ImageBase(url));
        }

        private void PostPythonMissing(PythonLocateResult located)
        {
            string reason = located.Problem ?? "";
            string en = reason.Length > 0 ? " (" + reason + ")" : "";
            _stream.Emit(ChatPageMessages.LinkNotice("warn",
                T("Local Python analysis is on, but no usable Python was found" + en +
                  ". Install Python 3.10 or newer (tick \"Add python.exe to PATH\"), then type /restart. Everything else keeps working.",
                  "로컬 Python 분석이 켜져 있지만 사용할 수 있는 Python을 찾지 못했습니다" + en +
                  ". Python 3.10 이상을 설치(\"Add python.exe to PATH\" 선택)한 뒤 /restart 를 입력하세요. 나머지 기능은 그대로 동작합니다."),
                T("Download Python", "Python 내려받기"), "https://www.python.org/downloads/"));
        }

        /// <summary>도구 환경이 준비되어 있으면 lsp.json을 쓰고 Ready, 아니면 백그라운드에서 만든다(Preparing).</summary>
        private void PrepareLanguageTools(PythonInterpreter python, int launch)
        {
            string folder = _workDir;
            var ready = _svc.LocalPython.ReadyTools();
            if (ready != null)
            {
                _lspStatus = TryWriteConfig(folder, ready, python) ? LspStatus.Ready : LspStatus.Unavailable;
                return;
            }
            _lspStatus = LspStatus.Preparing;
            _pythonCts ??= new CancellationTokenSource();
            _ = ProvisionToolsAsync(python, folder, launch, SynchronizationContext.Current ?? _svc.Ui, _pythonCts.Token);
        }

        private bool TryWriteConfig(string folder, PythonToolsResult tools, PythonInterpreter python)
        {
            try
            {
                var written = _svc.LocalPython.WriteConfig(folder, tools, python);
                if (written.LspKeptUserFile)
                    _stream.Emit(ChatPageMessages.Notice("info", T("Kept the existing .omp\\lsp.json of the analysis folder (not created by this app).", "분석 폴더의 기존 .omp\\lsp.json은 이 앱이 만든 것이 아니어서 그대로 두었습니다.")));
                return true;
            }
            catch (Exception ex)
            {
                _log.Note("lsp config failed: " + ex);
                _stream.Emit(ChatPageMessages.Notice("warn", T("Could not write the Python code-diagnostics settings: ", "Python 코드 진단 설정을 쓰지 못했습니다: ") + ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 백그라운드(UI 스레드가 아님)에서 도구 환경(venv + basedpyright + ruff)을 만든다. 진행·결과는 채팅 알림.
        /// 끝났을 때 omp가 아직 같은 결과 폴더로 떠 있으면 설정을 쓰고 쉬는 대로 다시 시작해 반영한다.
        /// </summary>
        private async Task ProvisionToolsAsync(PythonInterpreter python, string folder, int launch, SynchronizationContext? ui, CancellationToken ct)
        {
            void Say(string level, string text)
            {
                if (_disposed) return;
                if (ui != null) ui.Post(_ => { if (!_disposed) _stream.Emit(ChatPageMessages.Notice(level, text)); }, null);
            }

            Say("info", T("Setting up Python code diagnostics (basedpyright + ruff). One-time, needs the internet; the agent keeps working meanwhile.",
                          "Python 코드 진단(basedpyright + ruff)을 준비합니다. 처음 한 번만, 인터넷이 필요하며 그동안에도 에이전트는 계속 쓸 수 있습니다."));
            PythonToolsResult result;
            try
            {
                result = await Task.Run(() => _svc.LocalPython.EnsureToolsAsync(python, msg => Say("info", msg), ct), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { result = new PythonToolsResult(false, ex.Message); }

            if (ui == null) { OnToolsProvisioned(result, python, folder, launch); return; }
            ui.Post(_ => OnToolsProvisioned(result, python, folder, launch), null);
        }

        private void OnToolsProvisioned(PythonToolsResult result, PythonInterpreter python, string folder, int launch)
        {
            if (_disposed) return;
            if (!result.Ok)
            {
                string how = result.Offline
                    ? T(" It needs the internet once (pip download); connect, then type /restart to retry.", " 처음 한 번 인터넷(pip 다운로드)이 필요합니다. 연결한 뒤 /restart 로 다시 시도하세요.")
                    : T(" Type /restart to retry (the app repairs the environment).", " /restart 로 다시 시도하면 앱이 환경을 복구합니다.");
                _stream.Emit(ChatPageMessages.Notice("warn", T("Python code diagnostics could not be prepared: ", "Python 코드 진단을 준비하지 못했습니다: ") + result.Message + how));
                return;
            }
            // 폴더가 그새 바뀌었어도 설정은 써 둔다(그 폴더로 다시 열 때 바로 준비 상태).
            if (!TryWriteConfig(folder, result, python)) return;
            bool current = launch == _launchId && _options.AllowLocalPython && string.Equals(folder, _workDir, StringComparison.OrdinalIgnoreCase);
            if (!current) return;
            _lspStatus = LspStatus.Ready;
            _lspRestartPending = true;
            _stream.Emit(ChatPageMessages.Notice("info", result.Installed
                ? T("Python code diagnostics are ready (basedpyright + ruff).", "Python 코드 진단이 준비되었습니다(basedpyright + ruff).")
                : T("Python code diagnostics are ready.", "Python 코드 진단 준비 완료.")));
            RunPendingWorkspaceRestart();
        }

        // ---- 채팅의 그림 ---------------------------------------------------------------------------------------

        /// <summary>
        /// 결과 폴더 안의 그림 파일을 채팅 줄에 썸네일로 보인다(클릭하면 이미지 뷰어). 로컬 Python이 꺼져 있거나 파일이 없거나 그림이 아니거나
        /// 결과 폴더 밖이면 false.
        /// </summary>
        public bool PostImage(string path, string? caption = null)
        {
            string? folder = OutputFolder;
            if (_disposed || folder == null) return false;
            string? full = AgentWorkspace.ResolveInside(folder, path);
            if (full == null || !File.Exists(full) || !AgentWorkspace.IsImageFile(full)) return false;
            string? url = AgentWorkspace.ToUrlPath(folder, full);
            if (url == null) return false;
            _stream.Emit(ChatPageMessages.Image("https://" + OutputHost + "/" + url, Path.GetFileName(full), full, caption));
            return true;
        }
    }
}
