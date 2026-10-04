using System.Diagnostics;
using System.Text;

namespace NanumCsvViewer.Agent.Rpc
{
    /// <summary>omp 버전(major.minor.patch). "omp/18.4.4"의 마지막 '/' 뒤를 읽는다.</summary>
    internal readonly record struct OmpVersion(int Major, int Minor, int Patch) : IComparable<OmpVersion>
    {
        /// <summary>이 앱이 검증한 최소 omp 버전(--mode rpc-ui, queue_update, 호스트 도구 v2).</summary>
        public static readonly OmpVersion Minimum = new(18, 4, 4);

        public int CompareTo(OmpVersion other)
        {
            int c = Major.CompareTo(other.Major);
            if (c != 0) return c;
            c = Minor.CompareTo(other.Minor);
            return c != 0 ? c : Patch.CompareTo(other.Patch);
        }

        public static bool operator <(OmpVersion a, OmpVersion b) => a.CompareTo(b) < 0;
        public static bool operator >(OmpVersion a, OmpVersion b) => a.CompareTo(b) > 0;
        public static bool operator <=(OmpVersion a, OmpVersion b) => a.CompareTo(b) <= 0;
        public static bool operator >=(OmpVersion a, OmpVersion b) => a.CompareTo(b) >= 0;

        public override string ToString() => $"{Major}.{Minor}.{Patch}";

        /// <summary>"omp/18.4.4", "18.4.4", "v18.4.4-beta.1" 등. 숫자로 시작하지 않으면 실패.</summary>
        public static bool TryParse(string? text, out OmpVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) s = s[..nl];
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s[(slash + 1)..];
            s = s.Trim().TrimStart('v', 'V');
            if (s.Length == 0 || !char.IsAsciiDigit(s[0])) return false;

            int[] parts = new int[3];
            int pos = 0;
            for (int i = 0; i < 3; i++)
            {
                int start = pos;
                while (pos < s.Length && char.IsAsciiDigit(s[pos])) pos++;
                if (pos == start)
                {
                    if (i == 0) return false;
                    break; // "18.4"처럼 짧으면 나머지는 0
                }
                if (!int.TryParse(s.AsSpan(start, pos - start), out parts[i])) return false;
                if (pos < s.Length && s[pos] == '.') pos++; else break;
            }
            version = new OmpVersion(parts[0], parts[1], parts[2]);
            return true;
        }
    }

    internal enum OmpStatus { Ok, NotFound, TooOld, UnknownVersion }

    internal sealed record OmpProbeResult(OmpStatus Status, string? Path, OmpVersion? Version, string Message)
    {
        public bool IsOk => Status == OmpStatus.Ok;
    }

    /// <summary>omp 실행 파일 찾기와 버전 확인. 모델 호출 없음.</summary>
    internal static class OmpLocator
    {
        public const string ExeName = "omp.exe";

        /// <summary>
        /// 설정 경로(있고 파일이 존재하면) → PATH의 omp.exe → %LOCALAPPDATA%\omp\omp.exe.
        /// getEnv/fileExists는 테스트용 주입점.
        /// </summary>
        public static string? FindExecutable(string? configured, Func<string, string?>? getEnv = null, Func<string, bool>? fileExists = null)
        {
            getEnv ??= Environment.GetEnvironmentVariable;
            fileExists ??= File.Exists;

            if (!string.IsNullOrWhiteSpace(configured))
            {
                string path = configured.Trim().Trim('"');
                if (fileExists(path)) return path;
            }

            string? pathVar = getEnv("PATH");
            if (!string.IsNullOrEmpty(pathVar))
            {
                foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate;
                    try { candidate = Path.Combine(dir.Trim().Trim('"'), ExeName); }
                    catch (ArgumentException) { continue; }
                    if (fileExists(candidate)) return candidate;
                }
            }

            string? local = getEnv("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
            {
                string candidate = Path.Combine(local, "omp", ExeName);
                if (fileExists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>`omp --version`을 실행해 첫 줄을 돌려준다(실패·시간 초과는 null).</summary>
        public static async Task<string?> RunVersionAsync(string exePath, TimeSpan timeout, CancellationToken cancellation = default)
        {
            string? output = await RunAsync(exePath, new[] { "--version" }, null, timeout, requireSuccess: false, cancellation).ConfigureAwait(false);
            return output?.Split('\n')[0].Trim();
        }

        /// <summary>
        /// omp를 짧은 별도 프로세스로 실행해 표준 출력 전체를 돌려준다(창 없음, stdin 닫음). 시작 실패·시간 초과는 null,
        /// requireSuccess면 종료 코드가 0이 아닐 때도 null. 호출 스레드를 막지 않는다.
        /// </summary>
        public static async Task<string?> RunAsync(string exePath, IReadOnlyList<string> args, string? workingDirectory,
            TimeSpan timeout, bool requireSuccess = true, CancellationToken cancellation = default)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)) psi.WorkingDirectory = workingDirectory;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(timeout);
            Process? p = null;
            try
            {
                p = Process.Start(psi);
                if (p == null) return null;
                p.StandardInput.Close();
                Task<string> err = p.StandardError.ReadToEndAsync(cts.Token);
                string output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                try { await err.ConfigureAwait(false); } catch { }
                return requireSuccess && p.ExitCode != 0 ? null : output;
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                try { if (p is { HasExited: false }) p.Kill(true); } catch { }
                return null;
            }
            finally { p?.Dispose(); }
        }

        /// <summary>찾기 + 버전 확인(≥ 18.4.4). 앱 시작/패널 열기 때 한 번.</summary>
        public static async Task<OmpProbeResult> ProbeAsync(string? configured, bool korean,
            Func<string, CancellationToken, Task<string?>>? runVersion = null,
            Func<string, string?>? getEnv = null, Func<string, bool>? fileExists = null,
            CancellationToken cancellation = default)
        {
            string? exe = FindExecutable(configured, getEnv, fileExists);
            if (exe == null)
                return new OmpProbeResult(OmpStatus.NotFound, null, null,
                    korean ? "omp(oh-my-pi)를 찾을 수 없습니다. PATH 또는 %LOCALAPPDATA%\\omp\\omp.exe에 설치하거나 설정에서 경로를 지정하세요."
                           : "omp (oh-my-pi) was not found. Install it on PATH or at %LOCALAPPDATA%\\omp\\omp.exe, or set its path in the settings.");

            runVersion ??= (p, ct) => RunVersionAsync(p, TimeSpan.FromSeconds(10), ct);
            string? text = await runVersion(exe, cancellation).ConfigureAwait(false);
            if (!OmpVersion.TryParse(text, out var version))
                return new OmpProbeResult(OmpStatus.UnknownVersion, exe, null,
                    korean ? $"omp 버전을 확인할 수 없습니다: {exe} ({text ?? "응답 없음"})" : $"Cannot determine the omp version of {exe} ({text ?? "no answer"}).");

            if (version < OmpVersion.Minimum)
                return new OmpProbeResult(OmpStatus.TooOld, exe, version,
                    korean ? $"omp {version}은(는) 너무 오래되었습니다. {OmpVersion.Minimum} 이상이 필요합니다."
                           : $"omp {version} is too old; {OmpVersion.Minimum} or newer is required.");

            return new OmpProbeResult(OmpStatus.Ok, exe, version, $"omp {version}");
        }
    }
}
