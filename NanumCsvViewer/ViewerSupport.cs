using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NanumCsvViewer
{
    /// <summary>MarkdownViewerForm/ImageViewerForm.ShowFile의 결과. Ok=false면 Message가 사용자·모델에게 보일 이유.</summary>
    public sealed record ViewerShowResult(bool Ok, string Message);

    /// <summary>두 뷰어 창이 함께 쓰는 작은 도구들.</summary>
    internal static class ViewerSupport
    {
        public static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        /// <summary>팔레트가 어두운 쪽인가(WebView2 테마 변수 선택용).</summary>
        public static bool IsDark(ThemePalette palette) => ReferenceEquals(palette, ThemePalette.Dark);

        /// <summary>탐색기에서 파일을 선택해 보여 준다(없으면 폴더만). 실패는 false.</summary>
        public static bool RevealInExplorer(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { "/select," + full } });
                else
                    Process.Start(new ProcessStartInfo(Path.GetDirectoryName(full) ?? full) { UseShellExecute = true });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>시스템 기본 프로그램으로 연다. 실패는 false.</summary>
        public static bool OpenWithShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>다른 프로그램(에이전트의 Python)이 쓰는 중이어도 읽는다. 최대 maxBytes까지만(넘으면 null).</summary>
        public static byte[]? ReadShared(string path, long maxBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxBytes) return null;
            using var ms = new MemoryStream((int)fs.Length);
            fs.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>UTF-8(BOM 있으면 BOM 제거). UTF-16 BOM이면 UTF-16으로.</summary>
        public static string DecodeText(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }

        private static readonly Regex ImageRefRe = new(@"!\[[^\]]*\]\(([^)\s]+)\)", RegexOptions.Compiled);

        /// <summary>
        /// 마크다운이 참조하는 그림의 상대 경로(슬래시로 정규화, 중복 제거). 웹 주소·절대 경로·".." 포함·그림이 아닌 확장자는 뺀다
        /// (페이지가 실제로 그리는 규칙과 같다).
        /// </summary>
        public static IReadOnlyList<string> ImageRefs(string markdown)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (Match m in ImageRefRe.Matches(markdown))
            {
                string p;
                try { p = Uri.UnescapeDataString(m.Groups[1].Value); }
                catch (UriFormatException) { continue; }
                p = p.Replace('\\', '/');
                if (Regex.IsMatch(p, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:") || p.StartsWith('/')) continue;
                var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
                if (parts.Length == 0 || parts.Contains("..")) continue;
                if (!Agent.AgentWorkspace.IsImageFile(parts[^1])) continue;
                string rel = string.Join('/', parts);
                if (seen.Add(rel)) result.Add(rel);
            }
            return result;
        }

        /// <summary>
        /// 마크다운을 다른 폴더로 저장할 때 참조 그림도 같은 상대 경로로 복사한다(이미 있으면 덮어쓰지 않는다).
        /// 복사한 수와 원본이 없어 못 한 수를 돌려준다.
        /// </summary>
        public static (int Copied, int Missing) CopyReferencedImages(string markdown, string sourceDir, string targetDir)
        {
            if (string.Equals(Path.GetFullPath(sourceDir).TrimEnd('\\'), Path.GetFullPath(targetDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return (0, 0);
            int copied = 0, missing = 0;
            foreach (string rel in ImageRefs(markdown))
            {
                string from = Path.Combine(sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string to = Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(from)) { missing++; continue; }
                if (File.Exists(to)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to);
                    copied++;
                }
                catch (Exception) { missing++; }
            }
            return (copied, missing);
        }
    }

    /// <summary>파일 변경을 한 번으로 모아(디바운스) UI 스레드에서 알린다. 에이전트가 파일을 쓰는 도중의 연속 이벤트를 흡수한다.</summary>
    internal sealed class DebouncedFileWatcher : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Func<string, bool> _relevant;

        public event Action? Changed;

        /// <param name="relevant">변경된 전체 경로가 관심 대상인가.</param>
        public DebouncedFileWatcher(string directory, Func<string, bool> relevant, int debounceMs = 400)
        {
            _relevant = relevant;
            _timer = new System.Windows.Forms.Timer { Interval = debounceMs };
            _timer.Tick += (_, _) => { _timer.Stop(); Changed?.Invoke(); };
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = false,
            };
            _watcher.Changed += (_, e) => Poke(e.FullPath);
            _watcher.Created += (_, e) => Poke(e.FullPath);
            _watcher.Renamed += (_, e) => Poke(e.FullPath);
            SynchronizationContext? ui = SynchronizationContext.Current;
            _ui = ui;
        }

        private readonly SynchronizationContext? _ui;

        public void Start()
        {
            try { _watcher.EnableRaisingEvents = true; } catch (Exception) { /* 폴더가 사라짐 등: 자동 새로고침만 꺼진다 */ }
        }

        private void Poke(string path)
        {
            if (!_relevant(path)) return;
            // FileSystemWatcher는 스레드 풀에서 부른다: 타이머는 UI 스레드에서만 만진다.
            if (_ui != null) _ui.Post(_ => { _timer.Stop(); _timer.Start(); }, null);
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
