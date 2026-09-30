namespace NanumCsvViewer
{
    /// <summary>
    /// 명령줄 인수에서 시작 시 열 파일 경로를 찾는다(탐색기 "연결 프로그램"/더블클릭 → <c>"exe" "%1"</c>).
    /// </summary>
    internal static class StartupArgs
    {
        /// <summary>
        /// 존재하는 파일 경로(절대 경로)를 반환하고, 없으면 null.
        /// 우선 각 인수를 그대로 시도하고, 실패하면 인수를 공백으로 이어 붙여 다시 시도한다 —
        /// 연결 명령이 <c>%1</c>을 따옴표로 감싸지 않아 공백 포함 경로가 여러 인수로 쪼개진 경우 복구.
        /// </summary>
        public static string? ResolveFilePath(IReadOnlyList<string> args, string? baseDirectory = null)
        {
            foreach (string arg in args)
            {
                if (TryResolve(arg, baseDirectory) is { } path) return path;
            }
            return args.Count > 1 ? TryResolve(string.Join(' ', args), baseDirectory) : null;
        }

        private static string? TryResolve(string arg, string? baseDirectory)
        {
            string candidate = arg.Trim().Trim('"');
            if (candidate.Length == 0) return null;
            if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                candidate = uri.LocalPath;
            }
            try
            {
                string full = baseDirectory is null || Path.IsPathRooted(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(candidate, baseDirectory);
                return File.Exists(full) ? full : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }
}
