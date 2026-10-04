using System.Diagnostics;
using System.Text;

namespace NanumCsvViewer.Agent.Python
{
    /// <summary>짧은 외부 프로그램 실행 결과. 시작하지 못했거나 시간이 지났으면 ExitCode=-1, Error에 이유.</summary>
    internal sealed record ProcessResult(int ExitCode, string Output, string? Error = null)
    {
        public bool Ok => ExitCode == 0 && Error == null;
    }

    /// <summary>Python 환경 준비·점검용 프로세스 실행(테스트에서 가짜로 교체).</summary>
    internal interface IProcessRunner
    {
        /// <summary>표준 출력+오류를 줄 단위로 onLine에 흘리며 실행한다. 창 없음, stdin 닫음. 호출 스레드를 막지 않는다.</summary>
        Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout,
            Action<string>? onLine, CancellationToken cancellation);
    }

    internal sealed class SystemProcessRunner : IProcessRunner
    {
        public static readonly SystemProcessRunner Instance = new();

        public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout,
            Action<string>? onLine, CancellationToken cancellation)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)) psi.WorkingDirectory = workingDirectory;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(timeout);
            var sb = new StringBuilder();
            object gate = new();
            void Line(string? text)
            {
                if (text == null) return;
                lock (gate) sb.AppendLine(text);
                try { onLine?.Invoke(text); } catch { /* 진행 표시 실패는 실행에 영향 없음 */ }
            }

            Process? p = null;
            try
            {
                p = Process.Start(psi);
                if (p == null) return new ProcessResult(-1, "", "process did not start");
                p.StandardInput.Close();
                var outTask = PumpAsync(p.StandardOutput, Line, cts.Token);
                var errTask = PumpAsync(p.StandardError, Line, cts.Token);
                await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                string text;
                lock (gate) text = sb.ToString();
                return new ProcessResult(p.ExitCode, text);
            }
            catch (OperationCanceledException)
            {
                try { if (p is { HasExited: false }) p.Kill(true); } catch { }
                string text;
                lock (gate) text = sb.ToString();
                return new ProcessResult(-1, text, cancellation.IsCancellationRequested ? "cancelled" : "timed out");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                try { if (p is { HasExited: false }) p.Kill(true); } catch { }
                return new ProcessResult(-1, "", ex.Message);
            }
            finally { p?.Dispose(); }
        }

        private static async Task PumpAsync(StreamReader reader, Action<string?> sink, CancellationToken ct)
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) sink(line);
        }
    }
}
