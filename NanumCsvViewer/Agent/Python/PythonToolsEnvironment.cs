using System.Text;
using System.Text.Json;

namespace NanumCsvViewer.Agent.Python
{
    internal enum PythonToolsState
    {
        /// <summary>마커·실행 파일 모두 현재 고정 버전과 일치.</summary>
        Ready,
        /// <summary>아직 만들지 않음.</summary>
        Missing,
        /// <summary>마커의 스키마·도구 버전이 이 앱의 고정 버전과 다름(다시 만든다).</summary>
        Outdated,
        /// <summary>마커는 있는데 venv·실행 파일이 없거나 손상(다시 만든다).</summary>
        Broken,
    }

    /// <summary>Ok면 두 서버 실행 파일의 절대 경로. 실패면 Message에 이유, Offline이면 네트워크 문제로 보인다.</summary>
    internal sealed record PythonToolsResult(bool Ok, string Message, string? LangServer = null, string? Ruff = null, bool Offline = false, bool Installed = false);

    /// <summary>
    /// 에이전트가 쓰는 Python 코드 진단(basedpyright·ruff)을 위한 앱 관리 도구 환경:
    /// <c>%LOCALAPPDATA%\NanumCsvViewer\python-tools\venv</c>. 분석용 Python(사용자의 인터프리터·패키지)과 분리되어 있고
    /// 버전은 고정된다. 한 번만 만들고(`python -m venv` + `pip install`), 마커(tools.json)가 다르거나 파일이 손상되면 다시 만든다.
    /// 다른 앱 인스턴스와 동시에 만들지 않도록 root\.lock 파일로 직렬화한다.
    /// </summary>
    internal sealed class PythonToolsEnvironment
    {
        /// <summary>마커 형식·설치 방식을 바꾸면 올린다.</summary>
        public const int SchemaVersion = 1;
        public const string BasedPyrightVersion = "1.40.2";
        public const string RuffVersion = "0.16.10";

        private static readonly TimeSpan VenvTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan PipTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(60);

        private readonly IProcessRunner _runner;

        public PythonToolsEnvironment(string root, IProcessRunner runner)
        {
            Root = root;
            _runner = runner;
        }

        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NanumCsvViewer", "python-tools");

        public string Root { get; }
        public string VenvDir => Path.Combine(Root, "venv");
        public string ScriptsDir => Path.Combine(VenvDir, "Scripts");
        public string VenvPython => Path.Combine(ScriptsDir, "python.exe");
        public string LangServerPath => Path.Combine(ScriptsDir, "basedpyright-langserver.exe");
        public string BasedPyrightCliPath => Path.Combine(ScriptsDir, "basedpyright.exe");
        public string RuffPath => Path.Combine(ScriptsDir, "ruff.exe");
        public string MarkerPath => Path.Combine(Root, "tools.json");
        private string LockPath => Path.Combine(Root, ".lock");

        /// <summary>`python -m venv &lt;venvDir&gt;` 인자.</summary>
        public static IReadOnlyList<string> VenvArguments(string venvDir) => new[] { "-m", "venv", venvDir };

        /// <summary>venv의 python으로 고정 버전 두 도구를 설치하는 인자.</summary>
        public static IReadOnlyList<string> PipInstallArguments() => new[]
        {
            "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--no-warn-script-location",
            "basedpyright==" + BasedPyrightVersion, "ruff==" + RuffVersion,
        };

        /// <summary>프로세스를 띄우지 않고 파일만 보고 상태를 판단한다.</summary>
        public PythonToolsState Inspect()
        {
            if (!File.Exists(MarkerPath)) return Directory.Exists(VenvDir) ? PythonToolsState.Broken : PythonToolsState.Missing;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(MarkerPath));
                var r = doc.RootElement;
                bool same = r.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt32() == SchemaVersion &&
                            r.TryGetProperty("basedpyright", out var b) && b.GetString() == BasedPyrightVersion &&
                            r.TryGetProperty("ruff", out var u) && u.GetString() == RuffVersion;
                if (!same) return PythonToolsState.Outdated;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return PythonToolsState.Broken; }
            return File.Exists(VenvPython) && File.Exists(LangServerPath) && File.Exists(RuffPath) ? PythonToolsState.Ready : PythonToolsState.Broken;
        }

        private PythonToolsResult Ready(bool installed) => new(true, "ready", LangServerPath, RuffPath, Installed: installed);

        /// <summary>
        /// 없으면 만들고, 낡았거나 손상되면 지우고 다시 만든다. progress는 사람이 읽는 영어 진행 문구(백그라운드 스레드에서 불린다).
        /// 실패는 예외 대신 결과로 돌려주며 마커를 쓰지 않으므로 다음 호출이 다시 시도(복구)한다.
        /// </summary>
        public async Task<PythonToolsResult> EnsureAsync(PythonInterpreter basePython, Action<string>? progress, CancellationToken ct)
        {
            if (Inspect() == PythonToolsState.Ready) return Ready(false);
            try { Directory.CreateDirectory(Root); }
            catch (Exception ex) { return new PythonToolsResult(false, "Cannot create " + Root + ": " + ex.Message); }

            using var lockStream = await AcquireLockAsync(ct).ConfigureAwait(false);
            if (lockStream == null) return new PythonToolsResult(false, "Another Nanum CSV Viewer window is still preparing the Python tools; try again in a few minutes.");

            var state = Inspect();                 // 기다리는 동안 다른 인스턴스가 끝냈을 수 있다.
            if (state == PythonToolsState.Ready) return Ready(false);
            if (state != PythonToolsState.Missing)
            {
                progress?.Invoke("Repairing the Python tools environment…");
                if (!TryDeleteEnvironment(out string? why)) return new PythonToolsResult(false, "Cannot remove the old Python tools environment: " + why);
            }

            progress?.Invoke("Creating the Python tools environment (one-time)…");
            var venv = await _runner.RunAsync(basePython.Path, VenvArguments(VenvDir), Root, VenvTimeout, null, ct).ConfigureAwait(false);
            if (!venv.Ok) return Fail("python -m venv failed", venv);

            progress?.Invoke($"Installing basedpyright {BasedPyrightVersion} and ruff {RuffVersion} (one-time download)…");
            var pip = await _runner.RunAsync(VenvPython, PipInstallArguments(), Root, PipTimeout, null, ct).ConfigureAwait(false);
            if (!pip.Ok) return Fail("pip install failed", pip);

            var ruff = await _runner.RunAsync(RuffPath, new[] { "--version" }, Root, VerifyTimeout, null, ct).ConfigureAwait(false);
            if (!ruff.Ok || !ruff.Output.Contains("ruff", StringComparison.OrdinalIgnoreCase)) return Fail("ruff does not run", ruff);
            var pyright = await _runner.RunAsync(BasedPyrightCliPath, new[] { "--version" }, Root, VerifyTimeout, null, ct).ConfigureAwait(false);
            if (!pyright.Ok || !pyright.Output.Contains("basedpyright", StringComparison.OrdinalIgnoreCase)) return Fail("basedpyright does not run", pyright);
            if (!File.Exists(LangServerPath)) return new PythonToolsResult(false, "basedpyright-langserver.exe was not created in " + ScriptsDir);

            try
            {
                File.WriteAllText(MarkerPath, JsonSerializer.Serialize(new
                {
                    schema = SchemaVersion,
                    basedpyright = BasedPyrightVersion,
                    ruff = RuffVersion,
                    basePython = basePython.Path,
                    pythonVersion = basePython.Version.ToString(),
                    createdUtc = DateTime.UtcNow.ToString("o"),
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch (Exception ex) { return new PythonToolsResult(false, "Cannot write " + MarkerPath + ": " + ex.Message); }
            return Ready(true);
        }

        private static PythonToolsResult Fail(string what, ProcessResult r)
        {
            string tail = LastLines(r.Output, 6);
            string detail = r.Error != null ? r.Error : $"exit code {r.ExitCode}";
            bool offline = LooksOffline(r.Output);
            string msg = $"{what} ({detail}).{(tail.Length > 0 ? " " + tail : "")}";
            return new PythonToolsResult(false, msg, Offline: offline);
        }

        /// <summary>pip 출력이 네트워크(오프라인·프록시·DNS) 문제를 가리키는가.</summary>
        internal static bool LooksOffline(string output) =>
            output.Contains("Failed to establish a new connection", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Could not find a version that satisfies", StringComparison.OrdinalIgnoreCase)
            || output.Contains("No matching distribution", StringComparison.OrdinalIgnoreCase)
            || output.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Connection aborted", StringComparison.OrdinalIgnoreCase)
            || output.Contains("ProxyError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("SSLError", StringComparison.OrdinalIgnoreCase)
            || output.Contains("timed out", StringComparison.OrdinalIgnoreCase);

        internal static string LastLines(string text, int count)
        {
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string joined = string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - count)));
            return joined.Length > 600 ? joined[^600..] : joined;
        }

        private bool TryDeleteEnvironment(out string? why)
        {
            why = null;
            try
            {
                if (Directory.Exists(VenvDir)) Directory.Delete(VenvDir, true);
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
        }

        /// <summary>root\.lock을 독점으로 연다. 다른 프로세스가 쥐고 있으면 최대 10분 기다린다(취소 가능). 못 열면 null.</summary>
        private async Task<FileStream?> AcquireLockAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (true)
            {
                try
                {
                    return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow > deadline) return null;
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) { return null; }
            }
        }
    }
}
