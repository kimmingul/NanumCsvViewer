namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>omp의 도구 승인 질문("Allow tool: eval\nLanguage: python\nCode:\n...")을 읽는다.</summary>
    internal static class OmpApprovalPrompt
    {
        /// <summary>제목 첫 줄이 "Allow tool: &lt;이름&gt;"이면 이름을 돌려준다.</summary>
        public static bool TryGetTool(string title, out string tool)
        {
            tool = "";
            string first = (title ?? "").Split('\n')[0].Trim();
            const string prefix = "Allow tool:";
            if (!first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            tool = first[prefix.Length..].Trim();
            return tool.Length > 0;
        }

        /// <summary>omp `eval` 도구의 Python 셀 승인 질문인가(JavaScript 셀은 해당 없음).</summary>
        public static bool IsPythonEval(string title)
        {
            if (!TryGetTool(title, out string tool) || !string.Equals(tool, "eval", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (string line in title.Split('\n').Skip(1))
            {
                string t = line.Trim();
                if (!t.StartsWith("Language:", StringComparison.OrdinalIgnoreCase)) continue;
                string lang = t["Language:".Length..].Trim();
                return lang.Equals("python", StringComparison.OrdinalIgnoreCase) || lang.Equals("py", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
    }

    /// <summary>
    /// "대화마다 한 번" Python 실행 승인 기억. 사용자가 승인 카드를 승인하면 이 대화의 이후 Python eval 질문은 자동 승인한다.
    /// 새 대화·세션 전환·omp 재시작·세션 파일이 바뀌면 지워져 다시 묻는다. UI 스레드 전용.
    /// </summary>
    internal sealed class EvalApprovalMemory
    {
        private string _sessionFile = "";

        public bool Approved { get; private set; }
        /// <summary>이번 승인 이후 "자동 승인됨" 알림을 이미 냈는가(한 번만 낸다).</summary>
        public bool NoticePosted { get; private set; }

        /// <summary>사용자가 승인했다.</summary>
        public void Remember() { Approved = true; NoticePosted = false; }

        public void MarkNoticePosted() => NoticePosted = true;

        /// <summary>새 대화·세션 전환·재시작.</summary>
        public void Reset()
        {
            Approved = false;
            NoticePosted = false;
        }

        /// <summary>get_state가 알려 준 세션 파일. 이미 알던 파일에서 다른 파일로 바뀌면(외부 전환) 기억을 지운다.</summary>
        public void ObserveSessionFile(string? sessionFile)
        {
            string next = sessionFile ?? "";
            if (next.Length == 0) return;
            if (_sessionFile.Length > 0 && !string.Equals(_sessionFile, next, StringComparison.OrdinalIgnoreCase)) Reset();
            _sessionFile = next;
        }
    }
}
