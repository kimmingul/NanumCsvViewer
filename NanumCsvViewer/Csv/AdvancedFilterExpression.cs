using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>표현식 필터 컴파일 결과: 원본 식 + 행 술어.</summary>
    public sealed class CompiledAdvancedFilter
    {
        public string Expression { get; }
        public Func<string[], bool> Predicate { get; }

        public CompiledAdvancedFilter(string expression, Func<string[], bool> predicate)
        {
            Expression = expression;
            Predicate = predicate;
        }
    }

    public sealed class AdvancedFilterExpressionException : Exception
    {
        public AdvancedFilterExpressionException(string message) : base(message) { }
    }

    /// <summary>
    /// 표현식 필터: <c>AND</c>/<c>OR</c>/괄호, 비교 <c>== = != &lt; &lt;= &gt; &gt;= contains</c>.
    /// 컬럼은 헤더명(대소문자 무시) 또는 <c>Column&lt;N&gt;</c>(1-based)으로 참조.
    /// 비교는 양쪽이 숫자면 수치, 아니면 문화권 무시 문자열 비교. macOS AdvancedFilterExpression 이식.
    /// </summary>
    public static class AdvancedFilterExpression
    {
        public static CompiledAdvancedFilter Compile(string expression, IReadOnlyList<string> headers)
        {
            var tokens = Tokenize(expression);
            if (tokens.Count == 0)
                throw new AdvancedFilterExpressionException("필터 식이 비어 있습니다.");
            var parser = new Parser(tokens, headers);
            var predicate = parser.ParseExpression();
            if (!parser.IsAtEnd)
                throw new AdvancedFilterExpressionException($"예상치 못한 토큰 '{parser.CurrentToken}'");
            return new CompiledAdvancedFilter(expression, predicate);
        }

        private static List<string> Tokenize(string expression)
        {
            var tokens = new List<string>();
            var current = new System.Text.StringBuilder();

            void Flush()
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }

            int i = 0, n = expression.Length;
            while (i < n)
            {
                char c = expression[i];
                if (char.IsWhiteSpace(c)) { Flush(); i++; continue; }

                if (c == '"')
                {
                    Flush();
                    i++;
                    var value = new System.Text.StringBuilder();
                    while (i < n)
                    {
                        char next = expression[i];
                        if (next == '"') { i++; break; }
                        if (next == '\\')
                        {
                            i++;
                            if (i < n) { value.Append(expression[i]); i++; }
                        }
                        else { value.Append(next); i++; }
                    }
                    tokens.Add("\"" + value + "\"");
                    continue;
                }

                if (c == '(' || c == ')')
                {
                    Flush();
                    tokens.Add(c.ToString());
                    i++;
                    continue;
                }

                if (c == '=' || c == '!' || c == '<' || c == '>')
                {
                    Flush();
                    string op = c.ToString();
                    i++;
                    if (i < n)
                    {
                        if (expression[i] == '=') { op += "="; i++; }
                    }
                    tokens.Add(op);
                    continue;
                }

                current.Append(c);
                i++;
            }
            Flush();
            return tokens;
        }

        private sealed class Parser
        {
            private readonly List<string> _tokens;
            private readonly IReadOnlyList<string> _headers;
            private int _position;

            public Parser(List<string> tokens, IReadOnlyList<string> headers)
            {
                _tokens = tokens;
                _headers = headers;
            }

            public bool IsAtEnd => _position >= _tokens.Count;
            public string CurrentToken => IsAtEnd ? "" : _tokens[_position];

            public Func<string[], bool> ParseExpression() => ParseOr();

            private Func<string[], bool> ParseOr()
            {
                var lhs = ParseAnd();
                while (MatchKeyword("OR"))
                {
                    var rhs = ParseAnd();
                    var prev = lhs;
                    lhs = row => prev(row) || rhs(row);
                }
                return lhs;
            }

            private Func<string[], bool> ParseAnd()
            {
                var lhs = ParsePrimary();
                while (MatchKeyword("AND"))
                {
                    var rhs = ParsePrimary();
                    var prev = lhs;
                    lhs = row => prev(row) && rhs(row);
                }
                return lhs;
            }

            private Func<string[], bool> ParsePrimary()
            {
                if (Match("("))
                {
                    var predicate = ParseExpression();
                    if (!Match(")"))
                        throw new AdvancedFilterExpressionException("닫는 괄호가 없습니다.");
                    return predicate;
                }
                return ParseComparison();
            }

            private Func<string[], bool> ParseComparison()
            {
                string columnName = Consume("컬럼명이 필요합니다.");
                int column = ColumnIndex(columnName);
                string op = Consume("연산자가 필요합니다.");
                string value = Unquote(Consume("비교 값이 필요합니다."));

                switch (op.ToLowerInvariant())
                {
                    case "contains":
                        return row =>
                        {
                            if (column >= row.Length) return false;
                            return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                                row[column], value,
                                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
                        };
                    case "==":
                    case "=":
                        return row => column < row.Length && row[column] == value;
                    case "!=":
                        return row => column >= row.Length || row[column] != value;
                    case ">":
                    case ">=":
                    case "<":
                    case "<=":
                        return row =>
                        {
                            if (column >= row.Length) return false;
                            return CompareValues(row[column], value, op);
                        };
                    default:
                        throw new AdvancedFilterExpressionException($"지원하지 않는 연산자 '{op}'");
                }
            }

            private string Consume(string message)
            {
                if (IsAtEnd) throw new AdvancedFilterExpressionException(message);
                return _tokens[_position++];
            }

            private bool Match(string token)
            {
                if (IsAtEnd || _tokens[_position] != token) return false;
                _position++;
                return true;
            }

            private bool MatchKeyword(string keyword)
            {
                if (IsAtEnd || !string.Equals(_tokens[_position], keyword, StringComparison.OrdinalIgnoreCase))
                    return false;
                _position++;
                return true;
            }

            private int ColumnIndex(string name)
            {
                for (int i = 0; i < _headers.Count; i++)
                    if (string.Equals(_headers[i], name, StringComparison.OrdinalIgnoreCase))
                        return i;

                if (name.StartsWith("column", StringComparison.OrdinalIgnoreCase))
                {
                    string digits = new string(name.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int number) && number - 1 >= 0 && number - 1 < _headers.Count)
                        return number - 1;
                }
                throw new AdvancedFilterExpressionException($"알 수 없는 컬럼: {name}");
            }

            private static string Unquote(string token)
            {
                if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
                    return token.Substring(1, token.Length - 2);
                return token;
            }

            private static bool CompareValues(string lhs, string rhs, string op)
            {
                int comparison;
                if (double.TryParse(lhs.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double left) &&
                    double.TryParse(rhs.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double right))
                {
                    comparison = left.CompareTo(right);
                }
                else
                {
                    comparison = string.Compare(lhs, rhs, StringComparison.OrdinalIgnoreCase);
                }

                return op switch
                {
                    ">" => comparison > 0,
                    ">=" => comparison >= 0,
                    "<" => comparison < 0,
                    "<=" => comparison <= 0,
                    _ => false
                };
            }
        }
    }
}
