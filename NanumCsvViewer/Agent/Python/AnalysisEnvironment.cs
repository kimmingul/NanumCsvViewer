using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NanumCsvViewer.Agent.Python
{
    internal enum AnalysisEnvState
    {
        /// <summary>만들어지지 않았다.</summary>
        Missing,
        /// <summary>venv와 마커가 있고 python.exe가 있다.</summary>
        Ready,
        /// <summary>폴더·마커가 어긋나 있다(다음 설치가 지우고 다시 만든다).</summary>
        Broken,
    }

    internal enum AnalysisGroupState
    {
        NotInstalled,
        Installed,
        /// <summary>설치되어 있지만 이 앱 버전의 고정 목록과 다르다(업데이트 필요).</summary>
        Outdated,
        /// <summary>마커에는 있으나 lock에 고정된 패키지가 없다.</summary>
        Damaged,
    }

    internal enum AnalysisFailure
    {
        None,
        NoPython,
        UnsupportedPlatform,
        UnknownGroup,
        Busy,
        Offline,
        /// <summary>--only-binary라서 이 Python·플랫폼용 wheel이 없는 패키지가 있다(소스 빌드는 하지 않는다).</summary>
        NoWheel,
        Cancelled,
        VenvFailed,
        PipFailed,
        VerifyFailed,
        Io,
    }

    /// <summary>Message는 사람이 읽는 영어 한 줄(모델·로그용). 화면 문구는 <see cref="AnalysisMessages"/>가 Failure로 만든다.</summary>
    internal sealed record AnalysisResult(bool Ok, string Message, AnalysisFailure Failure = AnalysisFailure.None, string? Package = null)
    {
        public static AnalysisResult Success(string message) => new(true, message);
        public static AnalysisResult Fail(AnalysisFailure kind, string message, string? package = null) => new(false, message, kind, package);
    }

    /// <summary>진행 알림: Step은 venv·install·verify·lock·remove, Message는 영어 한 줄(pip의 한 줄이 그대로 올 수 있다).</summary>
    internal sealed record AnalysisProgress(string Step, string Message);

    internal sealed record AnalysisGroupStatus(AnalysisGroup Group, AnalysisGroupState State, IReadOnlyDictionary<string, string> Versions);

    /// <summary>프로세스 없이 파일만 읽은 환경 상태.</summary>
    internal sealed record AnalysisEnvInfo(
        AnalysisEnvState State, string Root, string PythonPath, Version? PythonVersion, string? BasePython,
        IReadOnlyList<AnalysisGroupStatus> Groups, IReadOnlyDictionary<string, string> Packages)
    {
        public bool IsReady => State == AnalysisEnvState.Ready;

        public AnalysisGroupStatus? Group(string name) =>
            Groups.FirstOrDefault(g => string.Equals(g.Group.Name, name, StringComparison.OrdinalIgnoreCase));

        public bool Has(string group) => Group(group) is { State: AnalysisGroupState.Installed or AnalysisGroupState.Outdated };

        public IEnumerable<string> InstalledGroupNames =>
            Groups.Where(g => g.State is AnalysisGroupState.Installed or AnalysisGroupState.Outdated).Select(g => g.Group.Name);
    }

    /// <summary>
    /// 앱이 관리하는 Python 분석 환경: <c>%LOCALAPPDATA%\NanumCsvViewer\python-analysis\venv</c>. 사용자의 Python·패키지와 분리되며
    /// 묶음(core·stats·clinical·ml)을 고정 버전으로 설치·업데이트·제거한다. pip는 항상 <c>--only-binary=:all: --require-virtualenv</c>
    /// (소스 빌드·시스템 site-packages 없음)이고 venv는 --system-site-packages 없이 만든다. 설치 뒤 import를 확인하고
    /// <c>venv\requirements.lock</c>(pip freeze)과 <c>env.json</c>(묶음별 지문)을 쓴다. 다른 앱 창과는 root\.lock 파일로 직렬화한다.
    /// </summary>
    internal sealed class AnalysisEnvironment
    {
        public const int SchemaVersion = 1;

        private static readonly TimeSpan VenvTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan PipTimeout = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(30);

        private static readonly Lazy<AnalysisEnvironment> s_default = new(() => new AnalysisEnvironment(
            DefaultRoot, SystemProcessRunner.Instance, new PythonToolsEnvironment(PythonToolsEnvironment.DefaultRoot, SystemProcessRunner.Instance).MarkerPath));

        /// <summary>앱 전체가 공유하는 환경(설정 창·에이전트 도구가 같은 작업 상태를 본다).</summary>
        public static AnalysisEnvironment Default => s_default.Value;

        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NanumCsvViewer", "python-analysis");

        private readonly IProcessRunner _runner;
        private readonly string? _toolsMarkerPath;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private string _activity = "";

        /// <param name="toolsMarkerPath">python-tools의 tools.json(있으면 거기 기록된 기반 Python을 먼저 시도). 테스트는 null.</param>
        public AnalysisEnvironment(string root, IProcessRunner runner, string? toolsMarkerPath = null)
        {
            Root = root;
            _runner = runner;
            _toolsMarkerPath = toolsMarkerPath;
        }

        public string Root { get; }
        public string VenvDir => Path.Combine(Root, "venv");
        public string ScriptsDir => Path.Combine(VenvDir, "Scripts");
        public string PythonPath => Path.Combine(ScriptsDir, "python.exe");
        public string MarkerPath => Path.Combine(Root, "env.json");
        public string LockFilePath => Path.Combine(VenvDir, AnalysisLock.FileName);
        private string ProcessLockPath => Path.Combine(Root, ".lock");

        /// <summary>설치·제거가 진행 중인가(이 프로세스 안).</summary>
        public bool IsBusy { get { lock (_gate) return _activity.Length > 0; } }

        /// <summary>진행 중인 작업의 가장 최근 한 줄(없으면 빈 문자열).</summary>
        public string Activity { get { lock (_gate) return _activity; } }

        /// <summary>작업 시작·진행·끝에 아무 스레드에서나 불린다(UI는 Invoke로 옮겨 쓴다).</summary>
        public event Action<AnalysisProgress?>? Changed;

        /// <summary>진행 중인 설치·제거를 취소한다.</summary>
        public void Cancel()
        {
            lock (_gate) _cts?.Cancel();
        }

        // ---- 명령줄 ----------------------------------------------------------------------------------------------

        /// <summary>기반 Python으로 venv 만들기. --system-site-packages 없음(기본)이라 시스템 패키지가 섞이지 않는다.</summary>
        public static IReadOnlyList<string> VenvArguments(string venvDir) => new[] { "-m", "venv", venvDir };

        /// <summary>
        /// venv의 python으로 고정 패키지 설치. 항상 wheel만(--only-binary=:all:), venv 안에만(--require-virtualenv: 가상 환경 밖이면 pip가 거부).
        /// </summary>
        public static IReadOnlyList<string> PipInstallArguments(IEnumerable<string> specs)
        {
            var args = new List<string>
            {
                "-m", "pip", "install", "--only-binary=:all:", "--require-virtualenv",
                "--disable-pip-version-check", "--no-input", "--no-warn-script-location",
            };
            args.AddRange(specs);
            return args;
        }

        public static IReadOnlyList<string> PipFreezeArguments() => new[] { "-m", "pip", "freeze", "--disable-pip-version-check" };

        public static IReadOnlyList<string> PipUninstallArguments(IEnumerable<string> names)
        {
            var args = new List<string> { "-m", "pip", "uninstall", "--yes", "--require-virtualenv", "--disable-pip-version-check" };
            args.AddRange(names);
            return args;
        }

        // ---- 상태(파일만) ----------------------------------------------------------------------------------------

        public AnalysisEnvInfo Inspect()
        {
            var empty = Array.Empty<AnalysisGroupStatus>();
            var none = new Dictionary<string, string>();
            if (!File.Exists(MarkerPath))
            {
                return new AnalysisEnvInfo(Directory.Exists(VenvDir) ? AnalysisEnvState.Broken : AnalysisEnvState.Missing, Root, PythonPath, null, null, empty, none);
            }

            JsonNode? node;
            try { node = JsonNode.Parse(File.ReadAllText(MarkerPath)); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new AnalysisEnvInfo(AnalysisEnvState.Broken, Root, PythonPath, null, null, empty, none);
            }
            if (node is not JsonObject marker || (int?)marker["schema"] != SchemaVersion || !File.Exists(PythonPath))
                return new AnalysisEnvInfo(AnalysisEnvState.Broken, Root, PythonPath, null, null, empty, none);

            Version.TryParse((string?)marker["pythonVersion"], out var py);
            py ??= new Version(3, 10);
            var packages = ReadLock();
            var groupNode = marker["groups"] as JsonObject;
            var statuses = new List<AnalysisGroupStatus>();
            foreach (var g in AnalysisGroups.All)
            {
                var versions = new Dictionary<string, string>();
                var state = AnalysisGroupState.NotInstalled;
                if (groupNode?[g.Name] is JsonObject entry)
                {
                    state = (string?)entry["fingerprint"] == g.Fingerprint(py) ? AnalysisGroupState.Installed : AnalysisGroupState.Outdated;
                    foreach (var pin in g.PinsFor(py))
                    {
                        if (packages.TryGetValue(pin.Key, out string? v)) versions[pin.Name] = v;
                        else state = AnalysisGroupState.Damaged;
                    }
                }
                statuses.Add(new AnalysisGroupStatus(g, state, versions));
            }
            return new AnalysisEnvInfo(AnalysisEnvState.Ready, Root, PythonPath, py, (string?)marker["basePython"], statuses, packages);
        }

        public IReadOnlyDictionary<string, string> ReadLock()
        {
            try { return AnalysisLock.Parse(File.Exists(LockFilePath) ? File.ReadAllText(LockFilePath) : null); }
            catch (IOException) { return new Dictionary<string, string>(); }
        }

        /// <summary>venv 폴더 전체 크기(바이트). 큰 폴더라 백그라운드에서 부른다. 없으면 0.</summary>
        public long GetSizeBytes()
        {
            if (!Directory.Exists(VenvDir)) return 0;
            long total = 0;
            try
            {
                foreach (var f in new DirectoryInfo(VenvDir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    total += f.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return total;
        }

        // ---- 기반 Python 찾기 -------------------------------------------------------------------------------------

        /// <summary>
        /// venv를 만들 기반 인터프리터: 이미 만든 환경의 기반 → python-tools 환경이 쓴 기반 → PATH(python·py -3·python3). 모두 3.10+ 필요.
        /// 찾지 못하면 Interpreter가 null이거나 IsSupported가 아니며 Problem에 영어 이유.
        /// </summary>
        public async Task<PythonLocateResult> FindBasePythonAsync(CancellationToken ct)
        {
            var remembered = new List<string>();
            AddMarkerBase(MarkerPath, remembered);
            if (_toolsMarkerPath != null) AddMarkerBase(_toolsMarkerPath, remembered);
            foreach (string exe in remembered.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(exe) || exe.StartsWith(VenvDir, StringComparison.OrdinalIgnoreCase)) continue;
                var info = await PythonLocator.InspectAsync(exe, Array.Empty<string>(), "base", _runner, Root, ct).ConfigureAwait(false);
                if (info is { IsSupported: true }) return new PythonLocateResult(info, null);
            }
            Directory.CreateDirectory(Root);
            return await PythonLocator.LocateAsync(null, Root, _runner, ct).ConfigureAwait(false);
        }

        private static void AddMarkerBase(string markerPath, List<string> into)
        {
            try
            {
                if (!File.Exists(markerPath)) return;
                if (JsonNode.Parse(File.ReadAllText(markerPath)) is JsonObject o && (string?)o["basePython"] is { Length: > 0 } p) into.Add(p);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }

        // ---- 설치·업데이트 ----------------------------------------------------------------------------------------

        /// <summary>
        /// 요청한 묶음(+core, +이미 설치된 묶음)을 고정 버전으로 설치·업데이트한다. 환경이 없거나 손상되면 먼저 (다시) 만든다.
        /// 이미 모두 최신이면 pip를 부르지 않는다. 실패는 예외 대신 결과로. progress는 백그라운드 스레드에서 불린다.
        /// </summary>
        public async Task<AnalysisResult> InstallAsync(IEnumerable<string> groups, Action<AnalysisProgress>? progress, CancellationToken ct)
        {
            var wanted = AnalysisGroups.Resolve(groups, out var unknown);
            if (unknown.Count > 0)
                return AnalysisResult.Fail(AnalysisFailure.UnknownGroup, $"Unknown package group(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", AnalysisGroups.Names)}.");

            return await RunExclusiveAsync("install", progress, ct, async (report, token) =>
            {
                var before = Inspect();
                if (before.IsReady && wanted.All(g => before.Group(g.Name)?.State == AnalysisGroupState.Installed))
                    return AnalysisResult.Success("Already installed.");

                Version pyVersion;
                string? basePath;
                if (before.IsReady && before.PythonVersion != null)
                {
                    pyVersion = before.PythonVersion;
                    basePath = before.BasePython;
                }
                else
                {
                    var created = await CreateVenvAsync(report, token).ConfigureAwait(false);
                    if (!created.Result.Ok) return created.Result;
                    pyVersion = created.Interpreter!.Version;
                    basePath = created.Interpreter.Path;
                }

                // 이미 설치된 묶음도 같이 넘겨 한 번에 풀게 한다(새 묶음이 기존 패키지를 깨뜨리지 않게).
                var current = Inspect();
                var all = AnalysisGroups.All
                    .Where(g => wanted.Any(w => w.Name == g.Name) || current.Has(g.Name))
                    .ToArray();
                var pins = all.SelectMany(g => g.PinsFor(pyVersion)).ToArray();

                string names = string.Join(", ", wanted.Select(w => w.Name));
                report(new AnalysisProgress("install", $"Installing {names} ({pins.Length} packages)…"));
                var pip = await _runner.RunAsync(PythonPath, PipInstallArguments(pins.Select(p => p.Spec)), Root, PipTimeout,
                    line => report(new AnalysisProgress("install", line)), token).ConfigureAwait(false);
                if (!pip.Ok) return FailPip("pip install", pip, token);

                report(new AnalysisProgress("verify", "Checking that the packages import…"));
                var verified = await VerifyImportsAsync(pins, token).ConfigureAwait(false);
                if (!verified.Ok) return verified;

                var frozen = await WriteLockAsync(pyVersion, report, token).ConfigureAwait(false);
                if (!frozen.Ok) return frozen;

                WriteMarker(pyVersion, basePath, all.Select(g => g.Name));
                return AnalysisResult.Success($"Installed {names}.");
            }).ConfigureAwait(false);
        }

        private async Task<(AnalysisResult Result, PythonInterpreter? Interpreter)> CreateVenvAsync(Action<AnalysisProgress> report, CancellationToken ct)
        {
            report(new AnalysisProgress("venv", "Looking for Python 3.10 or newer…"));
            var located = await FindBasePythonAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return (AnalysisResult.Fail(AnalysisFailure.Cancelled, "Cancelled."), null);
            if (!located.Found)
                return (AnalysisResult.Fail(AnalysisFailure.NoPython, located.Problem ?? "No Python interpreter was found."), null);
            var basePython = located.Interpreter!;

            string platform = await ProbePlatformAsync(basePython, ct).ConfigureAwait(false);
            if (platform.Contains("arm64", StringComparison.OrdinalIgnoreCase) || platform.Equals("win32", StringComparison.OrdinalIgnoreCase))
                return (AnalysisResult.Fail(AnalysisFailure.UnsupportedPlatform,
                    $"{basePython.Path} is a {platform} Python. The pinned data-science wheels (pandas, numpy, scipy, scikit-learn…) are published for 64-bit Intel/AMD Windows (win-amd64) only; " +
                    "there are no prebuilt win-arm64/win32 wheels and the app does not build from source. Install a 64-bit x64 Python 3.10-3.13."), null);

            try
            {
                Directory.CreateDirectory(Root);
                if (Directory.Exists(VenvDir)) Directory.Delete(VenvDir, true);
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            }
            catch (Exception ex) { return (AnalysisResult.Fail(AnalysisFailure.Io, $"Cannot reset {Root}: {ex.Message}"), null); }

            report(new AnalysisProgress("venv", $"Creating the environment with Python {basePython.Version.Major}.{basePython.Version.Minor}…"));
            var venv = await _runner.RunAsync(basePython.Path, VenvArguments(VenvDir), Root, VenvTimeout, null, ct).ConfigureAwait(false);
            if (!venv.Ok)
                return (venv.Error == "cancelled"
                    ? AnalysisResult.Fail(AnalysisFailure.Cancelled, "Cancelled.")
                    : AnalysisResult.Fail(AnalysisFailure.VenvFailed, Describe("python -m venv failed", venv)), null);
            if (!File.Exists(PythonPath))
                return (AnalysisResult.Fail(AnalysisFailure.VenvFailed, $"python -m venv finished but {PythonPath} does not exist."), null);
            if (IncludesSystemSitePackages())
                return (AnalysisResult.Fail(AnalysisFailure.VenvFailed, "The new environment would use the system site-packages; refusing to install into it."), null);
            return (AnalysisResult.Success("created"), basePython);
        }

        private async Task<string> ProbePlatformAsync(PythonInterpreter python, CancellationToken ct)
        {
            var r = await _runner.RunAsync(python.Path, new[] { "-c", "import sysconfig;print(sysconfig.get_platform())" }, Root, QuickTimeout, null, ct).ConfigureAwait(false);
            return r.Ok ? (r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "") : "";
        }

        private bool IncludesSystemSitePackages()
        {
            string cfg = Path.Combine(VenvDir, "pyvenv.cfg");
            if (!File.Exists(cfg)) return false;
            foreach (string raw in File.ReadLines(cfg))
            {
                int eq = raw.IndexOf('=');
                if (eq > 0 && raw[..eq].Trim().Equals("include-system-site-packages", StringComparison.OrdinalIgnoreCase))
                    return raw[(eq + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        // import 확인 대상 모듈(배포 이름과 다른 것만 표로).
        private static readonly Dictionary<string, string> ImportNames = new(StringComparer.Ordinal)
        {
            ["scikit-learn"] = "sklearn", ["scikit-survival"] = "sksurv", ["imbalanced-learn"] = "imblearn", ["umap-learn"] = "umap",
            ["scikit-posthocs"] = "scikit_posthocs", ["pyyaml"] = "yaml", ["pillow"] = "PIL",
        };

        internal static string ImportName(RequirementPin pin) =>
            ImportNames.TryGetValue(pin.Key, out string? n) ? n : pin.Key.Replace('-', '_');

        private async Task<AnalysisResult> VerifyImportsAsync(IEnumerable<RequirementPin> pins, CancellationToken ct)
        {
            const string script =
                "import importlib,sys\n" +
                "bad=[]\n" +
                "for m in sys.argv[1:]:\n" +
                "    try: importlib.import_module(m)\n" +
                "    except BaseException as e: bad.append(m+': '+type(e).__name__+': '+str(e)[:300].replace(chr(10),' '))\n" +
                "print('\\n'.join('IMPORT-FAILED '+b for b in bad))\n" +
                "sys.exit(1 if bad else 0)\n";
            var args = new List<string> { "-c", script };
            args.AddRange(pins.Select(ImportName).Distinct());
            var r = await _runner.RunAsync(PythonPath, args, Root, VerifyTimeout, null, ct).ConfigureAwait(false);
            if (r.Error == "cancelled") return AnalysisResult.Fail(AnalysisFailure.Cancelled, "Cancelled.");
            if (r.Ok) return AnalysisResult.Success("verified");
            string detail = string.Join(" | ", r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.StartsWith("IMPORT-FAILED", StringComparison.Ordinal)).Take(4));
            if (detail.Length == 0) detail = Describe("import check failed", r);
            return AnalysisResult.Fail(AnalysisFailure.VerifyFailed,
                detail + " (If a DLL failed to load, install the Microsoft Visual C++ Redistributable 2015-2022 x64.)");
        }

        private async Task<AnalysisResult> WriteLockAsync(Version python, Action<AnalysisProgress> report, CancellationToken ct)
        {
            report(new AnalysisProgress("lock", "Recording installed versions…"));
            var r = await _runner.RunAsync(PythonPath, PipFreezeArguments(), Root, VerifyTimeout, null, ct).ConfigureAwait(false);
            if (!r.Ok) return AnalysisResult.Fail(AnalysisFailure.PipFailed, Describe("pip freeze failed", r));
            try
            {
                File.WriteAllText(LockFilePath, AnalysisLock.Format(r.Output, python, DateTime.UtcNow), new UTF8Encoding(false));
                return AnalysisResult.Success("locked");
            }
            catch (Exception ex) { return AnalysisResult.Fail(AnalysisFailure.Io, $"Cannot write {LockFilePath}: {ex.Message}"); }
        }

        private void WriteMarker(Version python, string? basePython, IEnumerable<string> groupNames)
        {
            var groups = new JsonObject();
            foreach (string n in groupNames)
            {
                var g = AnalysisGroups.Get(n)!;
                groups[g.Name] = new JsonObject { ["fingerprint"] = g.Fingerprint(python), ["installedUtc"] = DateTime.UtcNow.ToString("o") };
            }
            var marker = new JsonObject
            {
                ["schema"] = SchemaVersion,
                ["basePython"] = basePython ?? ReadBasePythonFromCfg(),
                ["pythonVersion"] = python.ToString(),
                ["groups"] = groups,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
            };
            File.WriteAllText(MarkerPath, marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }

        /// <summary>venv\pyvenv.cfg의 home(기반 인터프리터 폴더)로 기반 python.exe를 추정한다.</summary>
        private string? ReadBasePythonFromCfg()
        {
            try
            {
                string cfg = Path.Combine(VenvDir, "pyvenv.cfg");
                if (!File.Exists(cfg)) return null;
                foreach (string raw in File.ReadLines(cfg))
                {
                    int eq = raw.IndexOf('=');
                    if (eq > 0 && raw[..eq].Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
                        return Path.Combine(raw[(eq + 1)..].Trim(), "python.exe");
                }
            }
            catch (IOException) { }
            return null;
        }

        // ---- 제거 ------------------------------------------------------------------------------------------------

        /// <summary>
        /// 묶음 하나를 제거한다: 남는 묶음의 고정 패키지에서 닿지 않는 패키지(그 묶음의 것과 그 의존 패키지 중 다른 묶음이 쓰지 않는 것)를 pip uninstall.
        /// core를 제거하면 환경 전체를 지운다(다른 묶음이 core 위에 있다).
        /// </summary>
        public async Task<AnalysisResult> RemoveGroupAsync(string group, Action<AnalysisProgress>? progress, CancellationToken ct)
        {
            var g = AnalysisGroups.Get(group);
            if (g == null) return AnalysisResult.Fail(AnalysisFailure.UnknownGroup, $"Unknown package group '{group}'. Valid: {string.Join(", ", AnalysisGroups.Names)}.");
            if (g.Name == AnalysisGroups.Core) return await RemoveAllAsync(progress, ct).ConfigureAwait(false);

            return await RunExclusiveAsync("remove", progress, ct, async (report, token) =>
            {
                var info = Inspect();
                if (!info.IsReady) return AnalysisResult.Success("Nothing to remove.");
                if (!info.Has(g.Name)) return AnalysisResult.Success($"{g.Name} is not installed.");
                var py = info.PythonVersion ?? new Version(3, 10);

                var keepGroups = info.InstalledGroupNames.Where(n => n != g.Name).Select(n => AnalysisGroups.Get(n)!).ToArray();
                var roots = keepGroups.SelectMany(k => k.PinsFor(py)).Select(p => p.Key).Distinct().ToArray();

                report(new AnalysisProgress("remove", $"Finding packages only {g.Name} needs…"));
                var args = new List<string> { "-c", OrphanScript };
                args.AddRange(roots);
                var probe = await _runner.RunAsync(PythonPath, args, Root, VerifyTimeout, null, token).ConfigureAwait(false);
                if (!probe.Ok) return FailPip("dependency scan", probe, token);
                var orphans = probe.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => l.StartsWith("ORPHAN:", StringComparison.Ordinal)).Select(l => l[7..].Trim()).Where(l => l.Length > 0).ToArray();

                if (orphans.Length > 0)
                {
                    report(new AnalysisProgress("remove", $"Removing {orphans.Length} packages…"));
                    var un = await _runner.RunAsync(PythonPath, PipUninstallArguments(orphans), Root, PipTimeout,
                        line => report(new AnalysisProgress("remove", line)), token).ConfigureAwait(false);
                    if (!un.Ok) return FailPip("pip uninstall", un, token);
                }

                var frozen = await WriteLockAsync(py, report, token).ConfigureAwait(false);
                if (!frozen.Ok) return frozen;
                WriteMarker(py, info.BasePython, keepGroups.Select(k => k.Name));
                return AnalysisResult.Success($"Removed {g.Name} ({orphans.Length} packages).");
            }).ConfigureAwait(false);
        }

        /// <summary>환경 폴더 전체(venv·마커)를 지운다.</summary>
        public async Task<AnalysisResult> RemoveAllAsync(Action<AnalysisProgress>? progress, CancellationToken ct) =>
            await RunExclusiveAsync("remove", progress, ct, (report, _) =>
            {
                report(new AnalysisProgress("remove", "Deleting the analysis environment…"));
                try
                {
                    if (Directory.Exists(VenvDir)) Directory.Delete(VenvDir, true);
                    if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
                    return Task.FromResult(AnalysisResult.Success("Removed the analysis environment."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Task.FromResult(AnalysisResult.Fail(AnalysisFailure.Io, $"Cannot delete {VenvDir}: {ex.Message} (close programs that use it, e.g. a running agent)"));
                }
            }).ConfigureAwait(false);

        /// <summary>
        /// 인자로 받은 루트 패키지에서 닿는 배포(importlib.metadata의 Requires-Dist, extra 제외)를 빼고 남는 것을 "ORPHAN:이름"으로 출력한다.
        /// pip·setuptools·wheel은 늘 남긴다. 마커는 평가하지 않는다(더 많이 남기는 쪽 = 안전).
        /// </summary>
        internal const string OrphanScript =
            "import re,sys\n" +
            "from importlib import metadata\n" +
            "norm=lambda n: re.sub(r'[-_.]+','-',n).lower()\n" +
            "dists={}\n" +
            "for d in metadata.distributions():\n" +
            "    n=d.metadata['Name']\n" +
            "    if n: dists[norm(n)]=d\n" +
            "keep={'pip','setuptools','wheel'}\n" +
            "stack=[norm(a) for a in sys.argv[1:]]\n" +
            "while stack:\n" +
            "    n=stack.pop()\n" +
            "    if n in keep: continue\n" +
            "    keep.add(n)\n" +
            "    d=dists.get(n)\n" +
            "    if d is None: continue\n" +
            "    for r in (d.requires or []):\n" +
            "        if 'extra ==' in r or 'extra==' in r: continue\n" +
            "        m=re.match(r'[A-Za-z0-9][A-Za-z0-9._-]*',r)\n" +
            "        if m: stack.append(norm(m.group(0)))\n" +
            "for n,d in sorted(dists.items()):\n" +
            "    if n not in keep: print('ORPHAN:'+d.metadata['Name'])\n";

        // ---- 공통 ------------------------------------------------------------------------------------------------

        private async Task<AnalysisResult> RunExclusiveAsync(string what, Action<AnalysisProgress>? progress, CancellationToken ct,
            Func<Action<AnalysisProgress>, CancellationToken, Task<AnalysisResult>> work)
        {
            CancellationTokenSource linked;
            lock (_gate)
            {
                if (_activity.Length > 0)
                    return AnalysisResult.Fail(AnalysisFailure.Busy, "Another analysis-environment operation is already running; wait for it to finish.");
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _cts = linked;
                _activity = what;
            }

            void Report(AnalysisProgress p)
            {
                lock (_gate) _activity = p.Message;
                try { progress?.Invoke(p); } catch { /* 진행 표시 실패는 작업에 영향 없음 */ }
                try { Changed?.Invoke(p); } catch { }
            }

            try
            {
                try { Directory.CreateDirectory(Root); }
                catch (Exception ex) { return AnalysisResult.Fail(AnalysisFailure.Io, $"Cannot create {Root}: {ex.Message}"); }

                using var processLock = await AcquireProcessLockAsync(linked.Token).ConfigureAwait(false);
                if (processLock == null)
                    return AnalysisResult.Fail(AnalysisFailure.Busy, "Another Nanum CSV Viewer window is changing the Python analysis environment; try again in a few minutes.");
                return await work(Report, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return AnalysisResult.Fail(AnalysisFailure.Cancelled, "Cancelled.");
            }
            finally
            {
                lock (_gate) { _activity = ""; _cts = null; }
                linked.Dispose();
                try { Changed?.Invoke(null); } catch { }
            }
        }

        private async Task<FileStream?> AcquireProcessLockAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (true)
            {
                try { return new FileStream(ProcessLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException)
                {
                    if (DateTime.UtcNow > deadline) return null;
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) { return null; }
            }
        }

        private static AnalysisResult FailPip(string what, ProcessResult r, CancellationToken ct)
        {
            if (r.Error == "cancelled" || ct.IsCancellationRequested)
                return AnalysisResult.Fail(AnalysisFailure.Cancelled, "Cancelled. Already downloaded files stay in pip's cache; run it again to continue.");
            string output = r.Output;
            string? package = null;
            var m = System.Text.RegularExpressions.Regex.Match(output, @"No matching distribution found for ([^\s]+)");
            if (m.Success) package = m.Groups[1].Value;
            if (LooksLikeNetworkError(output))
                return AnalysisResult.Fail(AnalysisFailure.Offline, Describe(what + " could not reach the Python package index", r));
            if (m.Success || output.Contains("Could not find a version that satisfies", StringComparison.OrdinalIgnoreCase))
                return AnalysisResult.Fail(AnalysisFailure.NoWheel,
                    Describe($"{what}: no prebuilt wheel for this Python/platform (the app never builds from source)", r), package);
            return AnalysisResult.Fail(AnalysisFailure.PipFailed, Describe(what + " failed", r));
        }

        internal static bool LooksLikeNetworkError(string output) =>
            output.Contains("Failed to establish a new connection", StringComparison.OrdinalIgnoreCase)
            || output.Contains("NewConnectionError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Max retries exceeded", StringComparison.OrdinalIgnoreCase)
            || output.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Connection aborted", StringComparison.OrdinalIgnoreCase)
            || output.Contains("ConnectionError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("ProxyError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("SSLError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("ReadTimeoutError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Read timed out", StringComparison.OrdinalIgnoreCase);

        private static string Describe(string what, ProcessResult r)
        {
            string tail = PythonToolsEnvironment.LastLines(r.Output, 6);
            string detail = r.Error ?? $"exit code {r.ExitCode}";
            return $"{what} ({detail}).{(tail.Length > 0 ? " " + tail : "")}";
        }
    }
}
