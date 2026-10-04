using System.Diagnostics;
using System.Text;

namespace NanumCsvViewer.Agent.Rpc
{
    /// <summary>omp 자식 프로세스 실행 정보.</summary>
    internal sealed record OmpLaunchInfo(string ExePath, IReadOnlyList<string> Arguments, string WorkingDirectory, string StderrLogPath);

    /// <summary>omp 자식 프로세스(테스트는 메모리 스트림 가짜로 대체).</summary>
    internal interface IOmpProcess : IDisposable
    {
        /// <summary>omp stdin(UTF-8 바이트를 직접 쓴다).</summary>
        Stream StandardInput { get; }
        /// <summary>omp stdout(JSONL).</summary>
        Stream StandardOutput { get; }
        int? ProcessId { get; }
        /// <summary>stderr의 마지막 비어 있지 않은 줄(종료 원인 안내용).</summary>
        string? LastErrorLine { get; }
        /// <summary>프로세스가 끝나면 종료 코드로 완료.</summary>
        Task<int> Exited { get; }
        /// <summary>프로세스 트리를 즉시 종료.</summary>
        void Kill();
    }

    internal interface IOmpProcessFactory
    {
        IOmpProcess Start(OmpLaunchInfo info);
    }

    /// <summary>omp.exe를 실제로 띄운다: 창 없음, stdin/stdout 리디렉션, stderr는 파일로.</summary>
    internal sealed class OmpProcessFactory : IOmpProcessFactory
    {
        public IOmpProcess Start(OmpLaunchInfo info)
        {
            var psi = new ProcessStartInfo(info.ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = info.WorkingDirectory,
            };
            foreach (string a in info.Arguments) psi.ArgumentList.Add(a);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("omp did not start");
            return new RealProcess(process, info.StderrLogPath);
        }

        private sealed class RealProcess : IOmpProcess
        {
            private readonly Process _process;
            private volatile string? _lastError;

            public RealProcess(Process process, string stderrLogPath)
            {
                _process = process;
                Exited = WaitAsync();
                _ = Task.Run(() => PumpStderr(stderrLogPath));
            }

            public Stream StandardInput => _process.StandardInput.BaseStream;
            public Stream StandardOutput => _process.StandardOutput.BaseStream;
            public int? ProcessId { get { try { return _process.Id; } catch { return null; } } }
            public string? LastErrorLine => _lastError;
            public Task<int> Exited { get; }

            private async Task<int> WaitAsync()
            {
                try
                {
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                    return _process.ExitCode;
                }
                catch { return -1; }
            }

            private async Task PumpStderr(string path)
            {
                FileStream? file = null;
                try
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch { file = null; }
                    var reader = _process.StandardError;
                    string? line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.Trim().Length > 0) _lastError = line.Trim();
                        if (file != null)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                            await file.WriteAsync(bytes).ConfigureAwait(false);
                            await file.FlushAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch { /* stderr 기록 실패는 무시 */ }
                finally { file?.Dispose(); }
            }

            public void Kill()
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                catch { /* 이미 종료 */ }
            }

            public void Dispose()
            {
                Kill();
                try { _process.Dispose(); } catch { }
            }
        }
    }

    /// <summary>omp 명령줄·임시 파일 경로 구성.</summary>
    internal static class OmpLaunch
    {
        /// <summary>%TEMP%\NanumCsvViewer</summary>
        public static string TempDirectory => Path.Combine(Path.GetTempPath(), "NanumCsvViewer");

        /// <summary>
        /// 호스트 도구를 시스템 프롬프트에 인라인 문서화하고(csv.* → xd://csv.*), omp 자체 도구(bash·write 등)의 승인을 켠다.
        /// omp 기본값(yolo)은 아무것도 묻지 않는다. always-ask는 읽기 외 호출을 extension_ui_request로 묻고, 앱은 그것을 채팅 승인 카드로 보여 준다.
        /// 사용자가 ExtraArgs에 --approval-mode를 주면 실행 인자가 이 설정보다 우선한다.
        /// </summary>
        public const string HostConfigJson = "{\"tools\":{\"xdevInlineDevices\":[\"csv.*\"],\"approvalMode\":\"always-ask\"}}";

        public static string StderrLogPath(string tag) => Path.Combine(TempDirectory, $"omp.stderr-p{tag}.log");
        public static string HostConfigPath(string tag) => Path.Combine(TempDirectory, $"omp-host-p{tag}.yml");
        public static string GuidePath(string tag) => Path.Combine(TempDirectory, $"agent-guide-p{tag}.md");

        /// <summary>--mode rpc-ui --cwd &lt;dir&gt; --config &lt;yml&gt; [--append-system-prompt &lt;guide&gt;] [extra...]</summary>
        public static List<string> BuildArguments(string workingDirectory, string? hostConfigPath, string? guidePath, string? extraArgs)
        {
            var args = new List<string> { "--mode", "rpc-ui", "--cwd", workingDirectory };
            if (!string.IsNullOrEmpty(hostConfigPath)) { args.Add("--config"); args.Add(hostConfigPath); }
            if (!string.IsNullOrEmpty(guidePath)) { args.Add("--append-system-prompt"); args.Add(guidePath); }
            args.AddRange(SplitArguments(extraArgs));
            return args;
        }

        /// <summary>공백으로 나누되 큰따옴표 안은 한 인자로 본다(따옴표는 제거).</summary>
        public static List<string> SplitArguments(string? text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var sb = new StringBuilder();
            bool inQuote = false, has = false;
            foreach (char c in text)
            {
                if (c == '"') { inQuote = !inQuote; has = true; }
                else if (!inQuote && char.IsWhiteSpace(c))
                {
                    if (has) { result.Add(sb.ToString()); sb.Clear(); has = false; }
                }
                else { sb.Append(c); has = true; }
            }
            if (has) result.Add(sb.ToString());
            return result;
        }

        /// <summary>임베디드 가이드(AgentGuide.md)를 읽는다. 리소스가 없으면 null.</summary>
        public static string? ReadGuideResource()
        {
            var asm = typeof(OmpLaunch).Assembly;
            string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("AgentGuide.md", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        /// <summary>임시 폴더에 host.yml과 가이드를 쓰고 경로를 돌려준다(가이드 없으면 null).</summary>
        public static (string HostConfig, string? Guide) WriteSupportFiles(string tag, string? guideText, string language)
        {
            Directory.CreateDirectory(TempDirectory);
            PruneStaleFiles();
            string host = HostConfigPath(tag);
            File.WriteAllText(host, HostConfigJson, new UTF8Encoding(false));
            string? guide = null;
            if (!string.IsNullOrEmpty(guideText))
            {
                guide = GuidePath(tag);
                string tail = language == "en"
                    ? "\n\nThe app UI language is English (en)."
                    : "\n\n앱 화면 언어는 한국어(ko)입니다.";
                File.WriteAllText(guide, guideText.TrimEnd() + tail + "\n", new UTF8Encoding(false));
            }
            return (host, guide);
        }

        private static int s_pruned;

        /// <summary>프로세스당 한 번: 비정상 종료로 남은 7일 지난 임시 파일(host.yml·가이드·stderr 로그)을 지운다.</summary>
        private static void PruneStaleFiles()
        {
            if (Interlocked.Exchange(ref s_pruned, 1) != 0) return;
            try
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
                foreach (string pattern in new[] { "omp-host-p*.yml", "agent-guide-p*.md", "omp.stderr-p*.log" })
                    foreach (string file in Directory.EnumerateFiles(TempDirectory, pattern))
                    {
                        try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
                    }
            }
            catch { /* 정리 실패는 무시 */ }
        }
    }
}
