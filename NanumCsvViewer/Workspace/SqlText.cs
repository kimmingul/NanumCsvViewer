namespace NanumCsvViewer.Workspace
{
    public enum SqlTokenKind { Whitespace, Comment, String, QuotedIdentifier, Number, Word, Punctuation }

    public readonly record struct SqlToken(SqlTokenKind Kind, int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>SQL 텍스트 도우미: 가벼운 토큰 분리(구문 색·자동완성 문맥용)와 문장 끝 정리. DuckDB 문법을 검증하지는 않는다.</summary>
    public static class SqlText
    {
        /// <summary>문자열('…')·따옴표 식별자("…")·줄 주석(--)·블록 주석(/* */)을 인식해 토큰으로 나눈다. 닫히지 않은 것은 끝까지 하나의 토큰.</summary>
        public static List<SqlToken> Tokenize(string sql)
        {
            var tokens = new List<SqlToken>();
            int i = 0, n = sql.Length;
            while (i < n)
            {
                char c = sql[i];
                int start = i;
                if (char.IsWhiteSpace(c))
                {
                    while (i < n && char.IsWhiteSpace(sql[i])) i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Whitespace, start, i - start));
                }
                else if (c == '-' && i + 1 < n && sql[i + 1] == '-')
                {
                    while (i < n && sql[i] != '\n') i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Comment, start, i - start));
                }
                else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
                {
                    int depth = 1; i += 2; // DuckDB는 중첩 블록 주석을 지원
                    while (i < n && depth > 0)
                    {
                        if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') { depth++; i += 2; }
                        else if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    }
                    tokens.Add(new SqlToken(SqlTokenKind.Comment, start, i - start));
                }
                else if (c == '\'' || c == '"')
                {
                    i++;
                    while (i < n)
                    {
                        if (sql[i] == c) { if (i + 1 < n && sql[i + 1] == c) { i += 2; continue; } i++; break; }
                        i++;
                    }
                    tokens.Add(new SqlToken(c == '\'' ? SqlTokenKind.String : SqlTokenKind.QuotedIdentifier, start, i - start));
                }
                else if (char.IsDigit(c))
                {
                    while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.' || sql[i] == '_')) i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Number, start, i - start));
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Word, start, i - start));
                }
                else
                {
                    i++;
                    tokens.Add(new SqlToken(SqlTokenKind.Punctuation, start, 1));
                }
            }
            return tokens;
        }

        /// <summary>
        /// 문장 끝의 세미콜론(과 그 뒤 공백·주석)을 떼어 한 문장 본문만 돌려준다. 끝이 줄 주석이면 뒤에 붙이는 ")" 가 주석에 먹히지 않도록
        /// 호출 쪽에서 줄바꿈을 넣어야 한다(<see cref="Wrap"/>). 문장 사이의 세미콜론(여러 문장)은 건드리지 않는다.
        /// </summary>
        public static string StripTerminator(string sql)
        {
            var tokens = Tokenize(sql);
            int end = sql.Length;
            for (int t = tokens.Count - 1; t >= 0; t--)
            {
                var tok = tokens[t];
                if (tok.Kind is SqlTokenKind.Whitespace or SqlTokenKind.Comment) continue;
                if (tok.Kind == SqlTokenKind.Punctuation && sql[tok.Start] == ';') { end = tok.Start; continue; }
                break;
            }
            return sql[..end].Trim();
        }

        /// <summary>본문을 "접두 + 본문 + 줄바꿈 + 접미"로 감싼다(본문 끝 줄 주석 대비). 접두는 본문과 같은 줄에 두어 줄 번호를 보존한다.</summary>
        public static string Wrap(string prefix, string body, string suffix) => prefix + body + "\n" + suffix;
    }
}
