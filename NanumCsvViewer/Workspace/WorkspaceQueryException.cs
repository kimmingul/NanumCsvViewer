using System.Text;

namespace NanumCsvViewer.Workspace
{
    public enum QueryErrorKind
    {
        Other,
        /// <summary>SQL 문법 오류.</summary>
        Syntax,
        /// <summary>없는 표·컬럼·함수 이름(Catalog/Binder).</summary>
        Name,
        /// <summary>형 변환·값 오류.</summary>
        Conversion,
        /// <summary>메모리 상한 초과.</summary>
        OutOfMemory,
        /// <summary>파일 읽기·쓰기·CSV 파싱 오류.</summary>
        Io,
        /// <summary>SELECT 한 문장이 아님.</summary>
        NotSelect,
    }

    /// <summary>엔진 오류 하나(DuckDB 오류를 읽기 쉬운 메시지로 바꾼 것). 줄·열은 1부터이며 사용자가 쓴 SQL 기준(알 수 있을 때만).</summary>
    public sealed class WorkspaceQueryException : Exception
    {
        public QueryErrorKind Kind { get; }
        public int? Line { get; }
        public int? Column { get; }
        /// <summary>DuckDB가 낸 원문 메시지(없으면 null).</summary>
        public string? EngineMessage { get; }

        public WorkspaceQueryException(string message, QueryErrorKind kind = QueryErrorKind.Other, int? line = null, int? column = null,
            string? engineMessage = null, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
            Line = line;
            Column = column;
            EngineMessage = engineMessage;
        }

        internal static string Where(int? line, int? col)
            => line is null ? string.Empty
                : col is null ? ViewerSupport.LT($" (line {line})", $" ({line}번째 줄)")
                : ViewerSupport.LT($" (line {line}, column {col})", $" ({line}번째 줄 {col}번째 글자)");

        /// <summary>
        /// DuckDB 예외를 변환한다. <paramref name="executedSql"/>은 실제로 실행한 문장(래핑 포함), <paramref name="prefixLength"/>는
        /// 그 첫 줄에서 사용자 SQL 앞에 붙인 글자 수(줄·열을 사용자 SQL 기준으로 되돌리는 데 쓴다).
        /// </summary>
        internal static WorkspaceQueryException From(Exception ex, string? executedSql = null, int prefixLength = 0)
        {
            if (ex is WorkspaceQueryException already) return already;
            string raw = ex.Message ?? string.Empty;
            QueryErrorKind kind = Classify(raw);

            // "LINE n: <발췌>\n   ^" 블록을 떼어 내고 줄·열을 계산.
            string body = raw;
            int? line = null, col = null;
            var m = SqlNames.LineMarker.Match(raw);
            if (m.Success)
            {
                body = raw[..m.Index].TrimEnd();
                string[] tail = raw[m.Index..].Split('\n');
                line = int.Parse(m.Groups[1].Value);
                if (tail.Length >= 2)
                {
                    string excerpt = tail[0].TrimEnd('\r');
                    string caretLine = tail[1].TrimEnd('\r');
                    int caret = caretLine.IndexOf('^');
                    if (caret >= 0 && excerpt.Length >= m.Length)
                    {
                        int headerLen = m.Length + 1; // "LINE n: "
                        string core = excerpt.Length > headerLen ? excerpt[headerLen..] : string.Empty;
                        int lead = core.StartsWith("...", StringComparison.Ordinal) ? 3 : 0;
                        string coreText = core.Substring(lead).TrimEnd('.');
                        int inLine = Math.Max(0, caret - headerLen - lead);
                        int start = 0;
                        if (executedSql is not null)
                        {
                            string[] lines = executedSql.Split('\n');
                            if (line.Value - 1 < lines.Length && coreText.Length > 0)
                            {
                                int at = lines[line.Value - 1].TrimEnd('\r').IndexOf(coreText, StringComparison.Ordinal);
                                if (at >= 0) start = at;
                            }
                        }
                        col = start + inLine + 1;
                        if (line == 1 && col > prefixLength) col -= prefixLength;
                    }
                }
            }

            string friendly = StripErrorPrefix(body);
            return new WorkspaceQueryException(friendly + Where(line, col), kind, line, col, raw, ex);
        }

        private static string StripErrorPrefix(string message)
        {
            // "Catalog Error: ..." 같은 앞머리는 Kind로 옮겨 가므로 떼되, 의미 없는 "Invalid Error:" 등은 그대로 두지 않는다.
            int idx = message.IndexOf(" Error: ", StringComparison.Ordinal);
            if (idx > 0 && idx < 40 && !message.AsSpan(0, idx).Contains('\n'))
                return message[(idx + " Error: ".Length)..].Trim();
            return message.Trim();
        }

        private static QueryErrorKind Classify(string raw)
        {
            string head = raw.Length > 60 ? raw[..60] : raw;
            if (head.StartsWith("Parser Error", StringComparison.OrdinalIgnoreCase)) return QueryErrorKind.Syntax;
            if (head.StartsWith("Catalog Error", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("Binder Error", StringComparison.OrdinalIgnoreCase)) return QueryErrorKind.Name;
            if (head.StartsWith("Conversion Error", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("Invalid Input Error", StringComparison.OrdinalIgnoreCase)) return QueryErrorKind.Conversion;
            if (head.StartsWith("Out of Memory", StringComparison.OrdinalIgnoreCase)) return QueryErrorKind.OutOfMemory;
            if (head.StartsWith("IO Error", StringComparison.OrdinalIgnoreCase)) return QueryErrorKind.Io;
            return QueryErrorKind.Other;
        }

        /// <summary>
        /// json_serialize_sql이 알려 주는 문자(코드 포인트) 오프셋 → 1부터 시작하는 (줄, 열). 열은 UTF-16 글자 단위라 편집기 글자 위치와 같다.
        /// (바이트 오프셋이 아니다 — 한글 SQL로 확인함.)
        /// </summary>
        internal static (int Line, int Column) LineColumnFromOffset(string sql, long codePointOffset)
        {
            int line = 1, col = 1;
            long seen = 0;
            for (int i = 0; i < sql.Length && seen < codePointOffset; i++)
            {
                char c = sql[i];
                if (c == '\n') { line++; col = 1; }
                else if (c != '\r') col++;
                if (!char.IsHighSurrogate(c)) seen++;
            }
            return (line, col);
        }
    }

    /// <summary>DuckDB 네이티브 라이브러리를 읽지 못해 엔진을 쓸 수 없을 때(예: 지원하지 않는 아키텍처).</summary>
    public sealed class WorkspaceEngineUnavailableException : Exception
    {
        public WorkspaceEngineUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
