using System.Globalization;
using System.Text;
using System.Text.Json;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>omp가 저장한 세션 하나(.jsonl). Title은 omp가 붙인 이름, 없으면 첫 사용자 메시지.</summary>
    internal sealed record SessionInfo(string Path, string Title, DateTime Modified, bool HasMessages);

    /// <summary>
    /// 현재 작업 폴더의 omp 세션 목록. omp는 세션을 ~/.omp/agent/sessions/--C--folder--/ 아래에 저장한다(폴더 이름 = 작업 폴더의
    /// ':' '\' '/'를 '-'로 바꾸고 앞뒤에 "--"). get_state.sessionFile의 폴더가 알려져 있으면 그것이 가장 정확하다.
    /// </summary>
    internal static class SessionCatalog
    {
        /// <summary>작업 폴더 → omp 세션 폴더 이름. 예: C:\tmp → --C--tmp--</summary>
        public static string DirectoryNameFor(string cwd)
        {
            string trimmed = cwd.TrimEnd('\\', '/');
            var sb = new StringBuilder("--");
            foreach (char c in trimmed) sb.Append(c is ':' or '\\' or '/' ? '-' : c);
            return sb.Append("--").ToString();
        }

        public static string DefaultRoot() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp", "agent", "sessions");

        /// <summary>sessionFile의 폴더, 없으면 root\&lt;cwd 인코딩&gt;. 폴더가 없으면 null.</summary>
        public static string? FindDirectory(string? sessionFile, string cwd, string? root = null)
        {
            if (!string.IsNullOrEmpty(sessionFile))
            {
                string? dir = Path.GetDirectoryName(sessionFile);
                if (dir != null && Directory.Exists(dir)) return dir;
            }
            if (cwd.Length == 0) return null;
            string candidate = Path.Combine(root ?? DefaultRoot(), DirectoryNameFor(cwd));
            return Directory.Exists(candidate) ? candidate : null;
        }

        /// <summary>최근 순으로 최대 max개. 읽을 수 없는 파일은 건너뛴다. 메시지가 하나도 없는 빈 세션은 현재 세션(current)일 때만 남긴다.</summary>
        public static List<SessionInfo> List(string directory, string? current = null, int max = 50)
        {
            var result = new List<SessionInfo>();
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(directory).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).ToList(); }
            catch (IOException) { return result; }
            catch (UnauthorizedAccessException) { return result; }

            foreach (var file in files)
            {
                if (result.Count >= max) break;
                var info = Read(file);
                bool isCurrent = current != null && string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase);
                if (!info.HasMessages && !isCurrent) continue;
                result.Add(info);
            }
            return result;
        }

        /// <summary>앞부분(최대 200줄)에서 title 줄과 첫 사용자 메시지를 찾는다.</summary>
        public static SessionInfo Read(FileInfo file)
        {
            string title = "", firstUser = "";
            try
            {
                using var reader = new StreamReader(file.FullName, new UTF8Encoding(false), true, 4096);
                for (int i = 0; i < 200; i++)
                {
                    string? line = reader.ReadLine();
                    if (line == null) break;
                    if (line.Length > 262144) continue;
                    bool isTitle = line.StartsWith("{\"type\":\"title\"", StringComparison.Ordinal);
                    if (!isTitle && (firstUser.Length > 0 || !line.Contains("\"user\"", StringComparison.Ordinal))) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (isTitle) { title = AgentEventParser.CollapseWhitespace(root.Str("title")); continue; }
                        var msg = root.Child("message");
                        if (msg.IsObject() && msg.Str("role") == "user")
                            firstUser = AgentEventParser.CollapseWhitespace(msg.Child("content").ContentText());
                    }
                    catch (JsonException) { }
                    if (title.Length > 0 && firstUser.Length > 0) break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            string label = title.Length > 0 ? title : firstUser;
            if (label.Length == 0) label = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            return new SessionInfo(file.FullName, Shorten(label, 60), file.LastWriteTime, firstUser.Length > 0 || title.Length > 0);
        }

        /// <summary>목록 한 줄: "2026-09-30 14:02  제목". 같은 줄이 겹치면 파일 이름을 덧붙여 구분한다.</summary>
        public static string Label(SessionInfo s, bool isCurrent, string currentMark) =>
            $"{s.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}  {s.Title}{(isCurrent ? currentMark : "")}";

        private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
