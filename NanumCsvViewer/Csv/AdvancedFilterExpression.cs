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
    ///
    /// 확장(이슈 #26): <c>[컬럼명]</c> 대괄호 참조를 양변에 쓸 수 있고, 우변이 컬럼 참조면
    /// 교차 컬럼 비교가 된다(예: <c>[end_date] &gt;= [start_date]</c>). 교차 컬럼 비교는
    /// 수치 → 시간(날짜 인식) → 문자열 순으로 판정하며, 어느 한쪽이 빈 값이면 항상 거짓이다.
    /// 리터럴 비교(기존 문법)의 의미는 변경하지 않는다(저장된 뷰 하위 호환).
    /// </summary>
    public static class AdvancedFilterExpression
    {
        /// <param name="blankNeverMatchesOrdering">
        /// true면 순서 비교(&lt; &lt;= &gt; &gt;=)에서 좌변 셀이 결측(빈 값·널 토큰)일 때 항상 거짓.
        /// 데이터 품질 규칙 실행 전용 — 결측 행이 <c>age &lt; 0</c> 같은 규칙에 문자열 폴백으로
        /// 잘못 매칭돼 위반으로 이중 계산되는 것을 막는다(결측은 결측 검사가 담당). 필터 경로는 false로
        /// 두어 저장된 뷰 하위 호환을 유지한다.</param>
        public static CompiledAdvancedFilter Compile(string expression, IReadOnlyList<string> headers,
            bool blankNeverMatchesOrdering = false)
        {
            var tokens = Tokenize(expression);
            if (tokens.Count == 0)
                throw new AdvancedFilterExpressionException("필터 식이 비어 있습니다.");
            var parser = new Parser(tokens, headers, blankNeverMatchesOrdering);
            var predicate = parser.ParseExpression();
            if (!parser.IsAtEnd)
                throw new AdvancedFilterExpressionException($"예상치 못한 토큰 '{parser.CurrentToken}'");
            return new CompiledAdvancedFilter(expression, predicate);
        }

        /// <summary>
        /// 대괄호 없는 우변이 헤더명과 정확히 일치하면서 비교 연산자를 쓰는지 감지(규칙 입력 실수 경고용).
        /// 예: <c>end_date &lt; start_date</c> — 사용자는 컬럼 비교를 의도했지만 리터럴 문자열 비교가 된다.
        /// </summary>
        public static bool LooksLikeUnbracketedColumnComparison(string expression, IReadOnlyList<string> headers)
        {
            List<string> tokens;
            try { tokens = Tokenize(expression); } catch { return false; }
            var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i + 1 < tokens.Count; i++)
            {
                if (tokens[i] is "<" or "<=" or ">" or ">=" or "=" or "==" or "!=")
                {
                    string rhs = tokens[i + 1];
                    bool quoted = rhs.Length >= 2 && rhs[0] == '"';
                    bool bracketed = rhs.Length >= 2 && rhs[0] == '[';
                    if (!quoted && !bracketed && headerSet.Contains(rhs)) return true;
                }
            }
            return false;
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

                if (c == '[')
                {
                    // '['는 토큰 시작 위치에서 닫는 ']'가 있을 때만 컬럼 참조. 그 외에는 리터럴 문자로
                    // 취급해 기존 문법 하위 호환을 지킨다: 헤더명 속 '['(weight[kg]), 값 속 '['(code = [A12]),
                    // 미닫힘 '['(note contains [draft) 모두 예전처럼 동작.
                    if (current.Length > 0) { current.Append(c); i++; continue; } // 토큰 중간 → 리터럴(헤더명 일부)
                    int close = expression.IndexOf(']', i + 1);
                    if (close < 0) { current.Append(c); i++; continue; }           // 미닫힘 → 리터럴
                    string inner = expression.Substring(i + 1, close - i - 1).Trim();
                    tokens.Add("[" + inner + "]");
                    i = close + 1;
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
            private readonly bool _blankNeverMatchesOrdering;
            private int _position;

            public Parser(List<string> tokens, IReadOnlyList<string> headers, bool blankNeverMatchesOrdering)
            {
                _tokens = tokens;
                _headers = headers;
                _blankNeverMatchesOrdering = blankNeverMatchesOrdering;
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
                int column = ColumnIndex(Unbracket(columnName));
                string op = Consume("연산자가 필요합니다.");
                string valueToken = Consume("비교 값이 필요합니다.");

                // 우변이 [컬럼] 참조이고 그 이름이 실제 헤더면 교차 컬럼 비교. 이름이 컬럼이 아니면(예: 'code = [A12]')
                // 리터럴 경로로 폴백해 기존 문법 하위 호환을 유지한다(값 = "[A12]").
                if (IsColumnRef(valueToken) && TryColumnIndex(Unbracket(valueToken), out int rightCol))
                    return BuildColumnComparison(column, rightCol, op);

                string value = Unquote(valueToken);
                bool guardBlank = _blankNeverMatchesOrdering;

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
                            // 규칙 실행 시 결측 셀은 순서 비교 대상 제외(문자열 폴백으로 인한 위반 오계산 방지).
                            if (guardBlank && IsMissingCell(row[column])) return false;
                            return CompareValues(row[column], value, op);
                        };
                    default:
                        throw new AdvancedFilterExpressionException($"지원하지 않는 연산자 '{op}'");
                }
            }

            private static bool IsMissingCell(string raw)
            {
                string v = raw.Trim();
                return v.Length == 0 || ColumnStatisticsBuilder.IsNullToken(v);
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
                => TryColumnIndex(name, out int idx)
                    ? idx
                    : throw new AdvancedFilterExpressionException($"알 수 없는 컬럼: {name}");

            private bool TryColumnIndex(string name, out int index)
            {
                for (int i = 0; i < _headers.Count; i++)
                    if (string.Equals(_headers[i], name, StringComparison.OrdinalIgnoreCase))
                    { index = i; return true; }

                if (name.StartsWith("column", StringComparison.OrdinalIgnoreCase))
                {
                    string digits = new string(name.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out int number) && number - 1 >= 0 && number - 1 < _headers.Count)
                    { index = number - 1; return true; }
                }
                index = -1;
                return false;
            }

            private static string Unquote(string token)
            {
                if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
                    return token.Substring(1, token.Length - 2);
                return token;
            }

            private static bool IsColumnRef(string token)
                => token.Length >= 2 && token[0] == '[' && token[^1] == ']';

            private static string Unbracket(string token)
                => IsColumnRef(token) ? token.Substring(1, token.Length - 2) : token;

            private static string CellOf(string[] row, int col)
                => col >= 0 && col < row.Length ? row[col].Trim() : string.Empty;

            /// <summary>교차 컬럼 술어. 빈 값이 끼면 항상 거짓(결측은 결측 검사가 담당).</summary>
            private static Func<string[], bool> BuildColumnComparison(int left, int right, string op)
            {
                if (string.Equals(op, "contains", StringComparison.OrdinalIgnoreCase))
                    return row =>
                    {
                        string l = CellOf(row, left), r = CellOf(row, right);
                        if (l.Length == 0 || r.Length == 0) return false;
                        return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                            l, r, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
                    };

                return op switch
                {
                    "==" or "=" => row => CompareCells(CellOf(row, left), CellOf(row, right)) == 0,
                    "!=" => row => CompareCells(CellOf(row, left), CellOf(row, right)) is int c0 && c0 != 0,
                    ">" => row => CompareCells(CellOf(row, left), CellOf(row, right)) > 0,
                    ">=" => row => CompareCells(CellOf(row, left), CellOf(row, right)) >= 0,
                    "<" => row => CompareCells(CellOf(row, left), CellOf(row, right)) < 0,
                    "<=" => row => CompareCells(CellOf(row, left), CellOf(row, right)) <= 0,
                    _ => throw new AdvancedFilterExpressionException($"지원하지 않는 연산자 '{op}'"),
                };
            }

            /// <summary>
            /// 교차 컬럼 셀 비교. 빈 값은 null(비교 불가). 순서는 <b>시간 → 수치 → 문자열</b>:
            /// 시간을 수치보다 먼저 시도해 <c>2024.11</c>(yyyy.MM)이 <c>2024.5</c>보다 작다고 판정되는
            /// 오류를 막는다. 시간은 Kind를 반영해(둘 다 Time이면 TimeOfDay, 둘 다 Date/DateTime이면 값)
            /// 비교하고, Time과 Date/DateTime이 섞이면 비교 불가(null)로 둔다(벽시계 의존 제거 — 결정론).
            /// </summary>
            private static int? CompareCells(string l, string r)
            {
                if (l.Length == 0 || r.Length == 0) return null;

                var lt = CsvDateParser.ParseDetailed(l, true);
                var rt = CsvDateParser.ParseDetailed(r, true);
                if (lt is { } lv && rt is { } rv)
                {
                    bool lTime = lv.Kind == TemporalKind.Time;
                    bool rTime = rv.Kind == TemporalKind.Time;
                    if (lTime && rTime) return lv.Value.TimeOfDay.CompareTo(rv.Value.TimeOfDay);
                    if (!lTime && !rTime) return lv.Value.CompareTo(rv.Value);
                    return null; // 시각 전용 vs 날짜/일시 — 의미상 비교 불가(오늘 날짜 주입 회피)
                }

                if (NumericAffix.TryParseNumber(l, out double ln) && NumericAffix.TryParseNumber(r, out double rn))
                    return ln.CompareTo(rn);
                return string.Compare(l, r, StringComparison.OrdinalIgnoreCase);
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
