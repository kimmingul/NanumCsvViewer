using System.Text;
using System.Text.RegularExpressions;

namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>채팅에 붙인 파일(작업 공간 원본) 하나: 표시용 파일 이름·원본 경로·작업 공간 표 이름(DB 원본은 여러 개, "스키마.표").</summary>
    internal sealed record ChatAttachment(string Name, string Path, IReadOnlyList<string> Tables);

    /// <summary>
    /// 첨부한 파일을 omp에 알리는 머리말. 파일 내용은 들어가지 않고 파일·표 이름만 있다(데이터 정책은 그대로):
    /// <c>[Attached workspace tables: orders (orders.csv), Sheet1 (book.xlsx)] Use ws.* tools to inspect them.</c> + 빈 줄 + 사용자 글.
    /// 같은 머리말을 <see cref="TryParse"/>가 다시 읽는다 — 말풍선(칩으로)·다시 불러온 기록·세션 목록·뷰 출처가 머리말 대신 사용자가 쓴 글만 보이게 한다.
    /// 작업 공간의 표 이름은 문자·숫자·밑줄(스키마는 점으로 이어짐)이라 공백이 없고, 그래서 "표 (파일)"을 첫 " ("에서 나눌 수 있다.
    /// </summary>
    internal static class AttachmentContext
    {
        private const string Open = "[Attached workspace tables: ";
        private const string Close = "] Use ws.* tools to inspect them.";
        /// <summary>머리말에 적는 표 이름의 최대 개수(큰 DB 파일이 프롬프트를 부풀리지 않게). 넘치면 ", +N more".</summary>
        internal const int MaxListed = 40;
        /// <summary>한 번에 붙일 수 있는 파일 수.</summary>
        internal const int MaxAttachments = 50;

        // "표 (파일)" 하나: 파일 이름은 ")" 다음이 ", " 또는 끝인 첫 자리에서 끝난다.
        private static readonly Regex Entry = new(@"\G(\S+) \((.+?)\)(?=, |$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MoreTail = new(@"\G\+\d+ more$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>머리말 + 글. 붙인 것이 없으면 글 그대로.</summary>
        public static string Compose(IReadOnlyList<ChatAttachment>? items, string text)
        {
            var entries = new List<string>();
            int total = 0;
            if (items != null)
            {
                foreach (var a in items)
                    foreach (string table in a.Tables)
                    {
                        total++;
                        if (entries.Count < MaxListed) entries.Add($"{table} ({a.Name})");
                    }
            }
            if (total == 0) return text;
            var sb = new StringBuilder(Open).Append(string.Join(", ", entries));
            if (total > entries.Count) sb.Append(", +").Append(total - entries.Count).Append(" more");
            return sb.Append(Close).Append("\n\n").Append(text).ToString();
        }

        /// <summary>
        /// 머리말이 있으면 파일별 칩(이름·표; 경로는 알 수 없어 빈 문자열)과 머리말을 뺀 글을 돌려준다.
        /// 머리말이 없거나 읽을 수 없으면 false이고 글은 그대로(읽을 수 없는 머리말은 글자 그대로 보인다).
        /// </summary>
        public static bool TryParse(string full, out IReadOnlyList<ChatAttachment> items, out string text)
        {
            items = Array.Empty<ChatAttachment>();
            text = full;
            if (!full.StartsWith(Open, StringComparison.Ordinal)) return false;
            int close = full.IndexOf(Close, Open.Length, StringComparison.Ordinal);
            if (close < 0) return false;
            string inner = full[Open.Length..close];

            var files = new List<(string Name, List<string> Tables)>();
            int pos = 0;
            while (pos < inner.Length)
            {
                if (MoreTail.Match(inner, pos).Success) break;
                var m = Entry.Match(inner, pos);
                if (!m.Success) return false;
                string table = m.Groups[1].Value, file = m.Groups[2].Value;
                int at = files.FindIndex(f => f.Name == file);
                if (at < 0) { files.Add((file, new List<string>())); at = files.Count - 1; }
                files[at].Tables.Add(table);
                pos = m.Index + m.Length;
                if (pos < inner.Length) pos += 2;   // ", "
            }
            if (files.Count == 0) return false;

            items = files.Select(f => new ChatAttachment(f.Name, "", f.Tables)).ToList();
            string rest = full[(close + Close.Length)..];
            text = rest.StartsWith("\n\n", StringComparison.Ordinal) ? rest[2..] : rest.TrimStart('\r', '\n');
            return true;
        }

        /// <summary>사용자 글만(머리말 제거). 머리말이 없거나 읽을 수 없으면 그대로.</summary>
        public static string StripContext(string full) => TryParse(full, out _, out string text) ? text : full;
    }
}
