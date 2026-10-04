using System.Text.Json;

namespace NanumCsvViewer.Agent.Python
{
    /// <summary>분석에 쓰는 Python 인터프리터(omp eval이 쓰는 것). Source: "omp"(omp setup python --check) 또는 PATH에서 찾은 이름.</summary>
    internal sealed record PythonInterpreter(string Path, Version Version, string Source)
    {
        public const int MinMajor = 3, MinMinor = 10;
        public bool IsSupported => Version.Major > MinMajor || (Version.Major == MinMajor && Version.Minor >= MinMinor);
    }

    /// <summary>찾기 결과. Interpreter가 null이면 Problem에 이유(사용자에게 보일 영어 한 줄).</summary>
    internal sealed record PythonLocateResult(PythonInterpreter? Interpreter, string? Problem)
    {
        public bool Found => Interpreter is { IsSupported: true };
    }

    /// <summary>
    /// omp eval이 쓸 Python을 찾는다: 먼저 `omp setup python --check --json`(작업 폴더 기준: &lt;cwd&gt;\.venv, 관리 venv, PATH 순),
    /// 없으면 PATH의 python·py 런처. 모델 호출 없음, 실패는 예외 대신 Problem으로 보고한다.
    /// </summary>
    internal static class PythonLocator
    {
        private const string InfoScript = "import sys;print(sys.executable);print('%d.%d.%d'%sys.version_info[:3])";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        /// <summary>`omp setup python --check --json` 출력 → 인터프리터 경로(사용할 수 없으면 null). 앞뒤에 다른 글이 있어도 첫 JSON 객체를 읽는다.</summary>
        public static string? ParseOmpCheck(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            int start = output.IndexOf('{'), end = output.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            try
            {
                using var doc = JsonDocument.Parse(output[start..(end + 1)]);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (!root.TryGetProperty("available", out var avail) || avail.ValueKind != JsonValueKind.True) return null;
                return root.TryGetProperty("pythonPath", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
                    ? p.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>"C:\\py\\python.exe\n3.10.6" 형태의 InfoScript 출력 → 인터프리터.</summary>
        public static PythonInterpreter? ParseInfo(string? output, string source)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) return null;
            string path = lines[^2], version = lines[^1];
            return Version.TryParse(version, out var v) && Path.IsPathRooted(path) ? new PythonInterpreter(path, v, source) : null;
        }

        public static async Task<PythonLocateResult> LocateAsync(string? ompExe, string workingDirectory, IProcessRunner runner, CancellationToken ct)
        {
            string? problem = null;
            if (!string.IsNullOrEmpty(ompExe))
            {
                var check = await runner.RunAsync(ompExe, new[] { "setup", "python", "--check", "--json" }, workingDirectory, Timeout, null, ct).ConfigureAwait(false);
                string? path = ParseOmpCheck(check.Output);
                if (path != null)
                {
                    var info = await InspectAsync(path, Array.Empty<string>(), "omp", runner, workingDirectory, ct).ConfigureAwait(false);
                    if (info != null)
                    {
                        return info.IsSupported
                            ? new PythonLocateResult(info, null)
                            : new PythonLocateResult(info, $"Python {info.Version} found at {info.Path}, but Python {PythonInterpreter.MinMajor}.{PythonInterpreter.MinMinor}+ is required.");
                    }
                    problem = $"omp reported {path}, but it did not run.";
                }
            }

            PythonInterpreter? tooOld = null;
            foreach (var (exe, pre, label) in new (string, string[], string)[]
                     { ("python", Array.Empty<string>(), "python"), ("py", new[] { "-3" }, "py -3"), ("python3", Array.Empty<string>(), "python3") })
            {
                var info = await InspectAsync(exe, pre, label, runner, workingDirectory, ct).ConfigureAwait(false);
                if (info == null) continue;
                if (info.IsSupported) return new PythonLocateResult(info, null);
                tooOld ??= info;
            }
            if (tooOld != null)
                return new PythonLocateResult(tooOld, $"Python {tooOld.Version} found at {tooOld.Path}, but Python {PythonInterpreter.MinMajor}.{PythonInterpreter.MinMinor}+ is required.");
            return new PythonLocateResult(null, problem ?? "No Python interpreter was found on PATH.");
        }

        private static async Task<PythonInterpreter?> InspectAsync(string exe, string[] pre, string source, IProcessRunner runner, string cwd, CancellationToken ct)
        {
            var args = pre.Concat(new[] { "-c", InfoScript }).ToArray();
            var r = await runner.RunAsync(exe, args, cwd, Timeout, null, ct).ConfigureAwait(false);
            return r.Ok ? ParseInfo(r.Output, source) : null;
        }

        private static readonly string[] ProbedPackages = { "pandas", "numpy", "matplotlib", "scipy", "statsmodels", "seaborn", "sklearn" };

        /// <summary>분석에 흔히 쓰는 패키지 중 설치된 것(이름순 고정). 확인하지 못하면 빈 목록.</summary>
        public static async Task<IReadOnlyList<string>> InstalledPackagesAsync(PythonInterpreter python, IProcessRunner runner, CancellationToken ct)
        {
            string code = "import importlib.util as u;print(' '.join(m for m in " +
                          "(" + string.Join(",", ProbedPackages.Select(p => $"'{p}'")) + ",) if u.find_spec(m)))";
            var r = await runner.RunAsync(python.Path, new[] { "-c", code }, null, Timeout, null, ct).ConfigureAwait(false);
            if (!r.Ok) return Array.Empty<string>();
            string line = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => n == "sklearn" ? "scikit-learn" : n).ToArray();
        }
    }
}
