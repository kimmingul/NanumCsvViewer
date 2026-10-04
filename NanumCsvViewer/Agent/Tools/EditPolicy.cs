namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>에이전트가 만든 편집 단계를 되돌리기 이력에서 알아보는 표지(되돌리기 메뉴에도 그대로 보인다).</summary>
    public static class AgentEditTag
    {
        public const string Prefix = "AI: ";
    }
}

namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>에이전트 저장 경로 정책: 원본 덮어쓰기 금지, 허용 확장자, 기존 파일은 overwrite 명시 시에만.</summary>
    internal static class AgentSavePolicy
    {
        public static readonly string[] Extensions = { ".csv", ".tsv", ".txt", ".xlsx" };

        /// <summary>검증을 통과한 전체 경로를 돌려준다. 거절 사유는 모델이 고칠 수 있게 AgentToolException으로.</summary>
        public static string Resolve(string requested, string? sourceDirectory, IEnumerable<string> protectedPaths, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(requested)) throw new AgentToolException("'path' is required.");
            string full;
            try
            {
                string p = requested.Trim();
                if (!Path.IsPathRooted(p))
                {
                    if (string.IsNullOrEmpty(sourceDirectory))
                        throw new AgentToolException("Give an absolute path: the source file's folder is unknown.");
                    p = Path.Combine(sourceDirectory, p);
                }
                full = Path.GetFullPath(p);
            }
            catch (AgentToolException) { throw; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new AgentToolException($"Invalid path: {ex.Message}");
            }

            string ext = Path.GetExtension(full);
            if (!Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                throw new AgentToolException($"Unsupported extension '{ext}'. Use one of: {string.Join(", ", Extensions)}.");

            foreach (string protectedPath in protectedPaths)
                if (SameFile(full, protectedPath))
                    throw new AgentToolException("Refused: that is the source file. Edits are never written over the original; choose a different file name.");

            if (Directory.Exists(full)) throw new AgentToolException("That path is an existing folder.");
            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                throw new AgentToolException($"The folder does not exist: {dir}");
            if (File.Exists(full) && !overwrite)
                throw new AgentToolException("That file already exists. Choose another name, or pass overwrite:true to replace it (the user must approve).");
            return full;
        }

        public static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                string fa = Canonical(a), fb = Canonical(b);
                return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        private static string Canonical(string path)
        {
            string full = Path.GetFullPath(path);
            try
            {
                // 심볼릭 링크/정션으로 원본을 가리키는 경로도 같은 파일로 본다.
                var info = new FileInfo(full);
                if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) return Path.GetFullPath(target.FullName);
            }
            catch (IOException) { }
            return full;
        }
    }

    /// <summary>편집 승인 카드 본문. 접두 "- "/"+ "/"  "는 채팅 카드에서 삭제/추가/문맥으로 색칠된다.</summary>
    internal static class EditCard
    {
        public const int MaxLines = 300;
        public const int ValueWidth = 120;

        public sealed record Change(long SourceRow, string Column, string Old, string New);

        /// <summary>변경마다 "  행 N · 컬럼" / "- 이전" / "+ 이후" 3줄. 상한을 넘으면 나머지는 개수로만 알린다.</summary>
        public static IReadOnlyList<string> Lines(IReadOnlyList<Change> changes, bool korean)
        {
            var lines = new List<string>();
            int shown = 0;
            foreach (var c in changes)
            {
                if (lines.Count + 3 > MaxLines) break;
                lines.Add(korean ? $"  {c.SourceRow:N0}행 · {c.Column}" : $"  Row {c.SourceRow:N0} · {c.Column}");
                lines.Add("- " + Show(c.Old, korean));
                lines.Add("+ " + Show(c.New, korean));
                shown++;
            }
            if (shown < changes.Count)
                lines.Add(korean ? $"  … 외 {changes.Count - shown:N0}개 셀(표시 생략)" : $"  … and {changes.Count - shown:N0} more cell(s) not shown");
            return lines;
        }

        private static string Show(string v, bool korean)
            => v.Length == 0 ? (korean ? "(빈 값)" : "(empty)") : ToolJson.OneLine(v, ValueWidth);
    }
}
