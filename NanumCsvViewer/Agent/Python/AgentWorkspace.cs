using System.Text;

namespace NanumCsvViewer.Agent
{
    /// <summary>
    /// 로컬 Python 분석의 작업 공간 규칙. 열린 파일마다 결과 폴더 <c>&lt;파일 폴더&gt;\&lt;파일 이름(확장자 제외)&gt;_분석결과</c>를 쓰고,
    /// 파일이 없으면 <c>문서\NanumCsvViewer\분석결과</c>를 쓴다. omp는 이 폴더를 작업 폴더(--cwd)로 시작하고,
    /// 에이전트가 쓴 스크립트·표·그림·보고서가 여기에 쌓인다. 원본 파일은 건드리지 않는다.
    /// </summary>
    public static class AgentWorkspace
    {
        /// <summary>결과 폴더 이름 접미사.</summary>
        public const string OutputSuffix = "_분석결과";

        /// <summary>열린 파일이 없을 때 문서 폴더 아래에 쓰는 폴더 이름(NanumCsvViewer\분석결과).</summary>
        public const string FallbackLeaf = "분석결과";

        /// <summary>결과 폴더의 전체 경로(만들지 않는다). 파일이 없거나 경로가 올바르지 않으면 문서 폴더 쪽 기본 폴더.</summary>
        public static string ComputeOutputFolder(string? openFilePath)
        {
            string? full = TryFullPath(openFilePath);
            if (full != null)
            {
                string? dir = Path.GetDirectoryName(full);
                string name = Path.GetFileNameWithoutExtension(full);
                if (!string.IsNullOrEmpty(dir) && name.Length > 0)
                    return Path.Combine(dir, name + OutputSuffix);
            }
            return FallbackFolder();
        }

        /// <summary>결과 폴더를 필요할 때 만들고(이미 있으면 그대로) 전체 경로를 돌려준다. 만들 수 없으면(읽기 전용 위치 등) 기본 폴더로 되돌린다.</summary>
        public static string OutputFolderFor(string? openFilePath)
        {
            string folder = ComputeOutputFolder(openFilePath);
            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception) when (!SamePath(folder, FallbackFolder()))
            {
                string fallback = FallbackFolder();
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        /// <summary>path가 folder 안(하위 포함, folder 자체는 제외)에 있는가. 대소문자 무시, ".." 정규화 후 비교.</summary>
        public static bool IsInside(string folder, string path)
        {
            string? root = TryFullPath(folder), target = TryFullPath(path);
            if (root == null || target == null) return false;
            root = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return target.Length > root.Length && target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 모델이 준 경로(결과 폴더 기준 상대 경로 또는 절대 경로)를 전체 경로로 바꾸고, 결과 폴더 안일 때만 돌려준다. 밖이면 null.
        /// 파일 존재 여부는 보지 않는다.
        /// </summary>
        public static string? ResolveInside(string folder, string relativeOrAbsolute)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolute)) return null;
            string? root = TryFullPath(folder);
            if (root == null) return null;
            string text = relativeOrAbsolute.Trim().Trim('"');
            string? full = TryFullPath(Path.IsPathRooted(text) ? text : Path.Combine(root, text));
            return full != null && IsInside(root, full) ? full : null;
        }

        /// <summary>folder 기준 상대 경로를 URL 경로("a/b%20c.png")로. 밖에 있으면 null.</summary>
        public static string? ToUrlPath(string folder, string path)
        {
            if (!IsInside(folder, path)) return null;
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string rel = Path.GetFullPath(path)[(root.Length + 1)..];
            var sb = new StringBuilder();
            foreach (string part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(Uri.EscapeDataString(part));
            }
            return sb.ToString();
        }

        /// <summary>파일 확장자가 채팅·뷰어가 그림으로 보여 주는 종류인가(png·jpg·jpeg·gif·bmp·webp·svg).</summary>
        public static bool IsImageFile(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg";

        /// <summary>문서\NanumCsvViewer\분석결과.</summary>
        public static string FallbackFolder() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NanumCsvViewer", FallbackLeaf);

        private static bool SamePath(string a, string b) =>
            string.Equals(TryFullPath(a), TryFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static string? TryFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path); }
            catch (Exception) { return null; }
        }
    }
}
