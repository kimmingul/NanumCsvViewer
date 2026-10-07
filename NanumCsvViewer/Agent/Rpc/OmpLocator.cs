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

    /// <summary>
    /// 짧은 omp 실행의 결과. StartError가 있으면 프로세스를 시작하지 못했다(차단·형식 불일치·접근 거부 등: StartErrorCode는 Win32 오류 코드).
    /// </summary>
    internal sealed record OmpRunOutcome(string? Output, int? ExitCode, bool TimedOut, int? StartErrorCode, string? StartError)
    {
        public bool Started => StartError == null;

        public static OmpRunOutcome Ran(string output, int exitCode = 0) => new(output, exitCode, false, null, null);
    }

    /// <summary>omp 짧은 실행(`--version`, `usage --json` 등). 모델 호출 없음. 찾기는 <see cref="OmpDiscovery"/>.</summary>
    internal static class OmpLocator
    {
        public const string ExeName = "omp.exe";

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
            var o = await RunDetailedAsync(exePath, args, workingDirectory, timeout, cancellation, throwIfCancelled: false).ConfigureAwait(false);
            if (!o.Started || o.TimedOut || o.ExitCode == null) return null;
            return requireSuccess && o.ExitCode != 0 ? null : o.Output;
        }

        /// <summary>`omp --version`의 자세한 결과(시작 실패와 시간 초과를 구분). 호출자가 취소하면 OperationCanceledException.</summary>
        public static Task<OmpRunOutcome> RunVersionOutcomeAsync(string exePath, TimeSpan timeout, CancellationToken cancellation = default) =>
            RunDetailedAsync(exePath, new[] { "--version" }, null, timeout, cancellation, throwIfCancelled: true);

        private static async Task<OmpRunOutcome> RunDetailedAsync(string exePath, IReadOnlyList<string> args, string? workingDirectory,
            TimeSpan timeout, CancellationToken cancellation, bool throwIfCancelled)
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
            OmpPathEnvironment.Apply(psi);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(timeout);
            Process? p = null;
            try
            {
                p = Process.Start(psi);
                if (p == null) return new OmpRunOutcome(null, null, false, null, "process did not start");
                p.StandardInput.Close();
                Task<string> err = p.StandardError.ReadToEndAsync(cts.Token);
                string output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                try { await err.ConfigureAwait(false); } catch { }
                return new OmpRunOutcome(output, p.ExitCode, false, null, null);
            }
            catch (System.ComponentModel.Win32Exception ex) when (p == null)
            {
                return new OmpRunOutcome(null, null, false, ex.NativeErrorCode, ex.Message);
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                try { if (p is { HasExited: false }) p.Kill(true); } catch { }
                if (ex is OperationCanceledException && cancellation.IsCancellationRequested && throwIfCancelled) throw;
                bool timedOut = ex is OperationCanceledException && !cancellation.IsCancellationRequested;
                return new OmpRunOutcome(null, null, timedOut, null, timedOut ? null : ex.Message);
            }
            finally { p?.Dispose(); }
        }
    }
}
