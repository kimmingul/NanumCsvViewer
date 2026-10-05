namespace NanumCsvViewer.Agent.Python
{
    /// <summary>ChatController가 로컬 Python 분석을 켤 때 쓰는 준비 단계(테스트에서 가짜로 교체).</summary>
    internal interface IPythonSetup
    {
        /// <summary>omp eval이 쓸 인터프리터 찾기(몇 초). 실패는 결과의 Problem.</summary>
        Task<PythonLocateResult> LocateAsync(string? ompExe, string workingDirectory, CancellationToken ct);
        /// <summary>분석 패키지 중 설치된 것.</summary>
        Task<IReadOnlyList<string>> PackagesAsync(PythonInterpreter python, CancellationToken ct);
        /// <summary>도구 환경이 이미 준비되어 있으면(프로세스 없이 파일만 확인) 그 결과, 아니면 null.</summary>
        PythonToolsResult? ReadyTools();
        /// <summary>도구 환경을 만들거나 고친다(오래 걸릴 수 있음: 백그라운드 스레드).</summary>
        Task<PythonToolsResult> EnsureToolsAsync(PythonInterpreter python, Action<string>? progress, CancellationToken ct);
        /// <summary>결과 폴더에 lsp.json·pyproject.toml을 쓴다.</summary>
        PythonLspConfig.WriteResult WriteConfig(string outputFolder, PythonToolsResult tools, PythonInterpreter python);
        /// <summary>앱 관리 분석 환경 상태(파일만 읽음). 관리 환경을 모르는 구현(테스트 가짜)은 null.</summary>
        AnalysisEnvInfo? InspectManaged() => null;
    }

    internal sealed class PythonSetup : IPythonSetup
    {
        private readonly IProcessRunner _runner;
        private readonly PythonToolsEnvironment _tools;

        public PythonSetup() : this(SystemProcessRunner.Instance, PythonToolsEnvironment.DefaultRoot) { }

        public PythonSetup(IProcessRunner runner, string toolsRoot)
        {
            _runner = runner;
            _tools = new PythonToolsEnvironment(toolsRoot, runner);
        }

        public PythonToolsEnvironment Tools => _tools;

        public Task<PythonLocateResult> LocateAsync(string? ompExe, string workingDirectory, CancellationToken ct) =>
            PythonLocator.LocateAsync(ompExe, workingDirectory, _runner, ct);

        public Task<IReadOnlyList<string>> PackagesAsync(PythonInterpreter python, CancellationToken ct) =>
            PythonLocator.InstalledPackagesAsync(python, _runner, ct);

        public PythonToolsResult? ReadyTools() =>
            _tools.Inspect() == PythonToolsState.Ready
                ? new PythonToolsResult(true, "ready", _tools.LangServerPath, _tools.RuffPath)
                : null;

        public Task<PythonToolsResult> EnsureToolsAsync(PythonInterpreter python, Action<string>? progress, CancellationToken ct) =>
            _tools.EnsureAsync(python, progress, ct);

        public PythonLspConfig.WriteResult WriteConfig(string outputFolder, PythonToolsResult tools, PythonInterpreter python) =>
            PythonLspConfig.Write(outputFolder, tools.LangServer!, tools.Ruff!, python.Path);

        public AnalysisEnvInfo? InspectManaged() => AnalysisEnvironment.Default.Inspect();
    }
}
