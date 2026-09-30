using System.Text;

namespace NanumCsvViewer.Stats
{
    // 고급 통계(이슈 #27) 공용 — R/statsmodels 스타일 모형식. WinForms 의존 없음.
    //
    // 문법:  응답 ~ 항 (+ 항)*
    //   변수   : 식별자(문자·숫자·_·.) | [공백 포함 이름] | `백틱 이름` | C(변수) — C()는 범주로 강제
    //   a:b    : 상호작용(곱)
    //   a*b    : a + b + a:b (a*b*c 는 모든 하위 조합)
    //   - 1, + 0 : 절편 제거 / + 1 : 절편 명시
    // 괄호 묶음·거듭제곱·변환 함수(log 등)는 지원하지 않으며 위치와 함께 오류를 낸다.

    /// <summary>모형 항: 변수 1개면 주효과, 2개 이상이면 상호작용. 변수 순서는 식에 적힌 순서.</summary>
    public sealed record FormulaTerm(IReadOnlyList<string> Variables)
    {
        public string Name => string.Join(":", Variables);
        public int Order => Variables.Count;

        public bool Equals(FormulaTerm? other)
            => other is not null && Variables.Count == other.Variables.Count
               && Variables.OrderBy(v => v, StringComparer.Ordinal)
                   .SequenceEqual(other.Variables.OrderBy(v => v, StringComparer.Ordinal), StringComparer.Ordinal);

        public override int GetHashCode()
        {
            var h = new HashCode();
            foreach (var v in Variables.OrderBy(v => v, StringComparer.Ordinal)) h.Add(v, StringComparer.Ordinal);
            return h.ToHashCode();
        }

        /// <summary>이 항이 other의 모든 변수를 포함하는지(주변성 판정: other ⊂ this).</summary>
        public bool Contains(FormulaTerm other)
            => other.Variables.All(v => Variables.Contains(v, StringComparer.Ordinal));
    }

    public sealed record ModelFormula(
        string Response,
        IReadOnlyList<FormulaTerm> Terms,
        bool Intercept,
        IReadOnlySet<string> ForcedCategorical)
    {
        /// <summary>식에 등장하는 모든 설명 변수(중복 없음, 등장 순).</summary>
        public IReadOnlyList<string> PredictorVariables
            => Terms.SelectMany(t => t.Variables).Distinct(StringComparer.Ordinal).ToArray();

        /// <summary>식 문자열로 되돌린다(공백·특수문자 이름은 [ ]로 인용).</summary>
        public override string ToString()
        {
            string Q(string v)
            {
                string name = IsBareIdentifier(v) ? v : "[" + v + "]";
                return ForcedCategorical.Contains(v) ? $"C({name})" : name;
            }
            var rhs = Terms.Select(t => string.Join(":", t.Variables.Select(Q))).ToList();
            if (!Intercept) rhs.Add("0");
            if (rhs.Count == 0) rhs.Add("1");
            return $"{Q(Response)} ~ {string.Join(" + ", rhs)}";
        }

        internal static bool IsBareIdentifier(string s)
            => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_')
               && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');

        public static ModelFormula Parse(string text) => new FormulaParser(text).Parse();
    }

    public sealed class FormulaParseException : FormatException
    {
        /// <summary>오류 위치(0-based 문자 인덱스).</summary>
        public int Position { get; }
        public FormulaParseException(string message, int position)
            : base($"{message} (position {position + 1})") => Position = position;
    }

    internal sealed class FormulaParser
    {
        private readonly string _s;
        private int _i;
        private readonly HashSet<string> _categorical = new(StringComparer.Ordinal);

        public FormulaParser(string text) => _s = text ?? "";

        public ModelFormula Parse()
        {
            SkipWs();
            if (Eof) throw Error("Formula is empty.");
            string response = ReadVariable();
            SkipWs();
            if (Eof || _s[_i] != '~') throw Error("Expected '~' after the response.");
            _i++;

            bool intercept = true;
            var terms = new List<FormulaTerm>();
            bool expectTerm = true;
            int sign = +1;
            while (true)
            {
                SkipWs();
                if (Eof)
                {
                    if (expectTerm) throw Error("Expected a term.");
                    break;
                }
                if (!expectTerm)
                {
                    char op = _s[_i];
                    if (op == '+') sign = +1;
                    else if (op == '-') sign = -1;
                    else throw Error($"Unexpected '{op}'. Use + to add terms.");
                    _i++;
                    expectTerm = true;
                    continue;
                }

                if (char.IsDigit(_s[_i]))
                {
                    int start = _i;
                    while (!Eof && char.IsDigit(_s[_i])) _i++;
                    string num = _s[start.._i];
                    if (num == "1") intercept = sign > 0;
                    else if (num == "0") { if (sign < 0) throw Error("'- 0' is not meaningful.", start); intercept = false; }
                    else throw Error("Only 0 or 1 may appear as numbers (intercept control).", start);
                }
                else
                {
                    var expanded = ReadProduct();
                    if (sign < 0)
                    {
                        foreach (var t in expanded) terms.RemoveAll(x => x.Equals(t));
                    }
                    else
                    {
                        foreach (var t in expanded)
                            if (!terms.Contains(t)) terms.Add(t);
                    }
                }
                sign = +1;
                expectTerm = false;
            }

            // R처럼 차수(주효과 → 2차 상호작용 …) 순, 같은 차수는 등장 순.
            var ordered = terms.Select((t, idx) => (t, idx)).OrderBy(x => x.t.Order).ThenBy(x => x.idx)
                .Select(x => x.t).ToList();
            if (ordered.Any(t => t.Variables.Contains(response, StringComparer.Ordinal)))
                throw new FormulaParseException($"The response '{response}' cannot also be a predictor.", 0);
            return new ModelFormula(response, ordered, intercept, _categorical);
        }

        // a*b:c*d … — ':'는 곱보다 강하게 묶인다(R과 동일).
        private List<FormulaTerm> ReadProduct()
        {
            var factors = new List<List<string>> { ReadInteraction() };
            while (true)
            {
                SkipWs();
                if (Eof || _s[_i] != '*') break;
                _i++;
                SkipWs();
                factors.Add(ReadInteraction());
            }
            if (factors.Count == 1) return new List<FormulaTerm> { new(factors[0]) };

            // 모든 비어 있지 않은 부분집합(곱 전개). 변수 중복은 합친다.
            var result = new List<FormulaTerm>();
            int n = factors.Count;
            if (n > 10) throw Error("Too many '*' factors.");
            var subsets = Enumerable.Range(1, (1 << n) - 1)
                .OrderBy(m => System.Numerics.BitOperations.PopCount((uint)m)).ThenBy(m => m);
            foreach (int mask in subsets)
            {
                var vars = new List<string>();
                for (int k = 0; k < n; k++)
                    if ((mask & (1 << k)) != 0)
                        foreach (var v in factors[k]) if (!vars.Contains(v, StringComparer.Ordinal)) vars.Add(v);
                var term = new FormulaTerm(vars);
                if (!result.Contains(term)) result.Add(term);
            }
            return result;
        }

        private List<string> ReadInteraction()
        {
            var vars = new List<string> { ReadVariable() };
            while (true)
            {
                SkipWs();
                if (Eof || _s[_i] != ':') break;
                _i++;
                SkipWs();
                string v = ReadVariable();
                if (!vars.Contains(v, StringComparer.Ordinal)) vars.Add(v);
            }
            return vars;
        }

        private string ReadVariable()
        {
            SkipWs();
            if (Eof) throw Error("Expected a variable name.");
            int start = _i;
            char c = _s[_i];
            if (c == '(') throw Error("Parentheses are not supported; write the terms out (e.g. a + b + a:b).");
            if (c == '[') return ReadQuoted('[', ']');
            if (c == '`') return ReadQuoted('`', '`');
            if (!(char.IsLetter(c) || c == '_')) throw Error($"Unexpected '{c}'.");
            while (!Eof && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '_' or '.')) _i++;
            string name = _s[start.._i];

            SkipWs();
            if (!Eof && _s[_i] == '(')
            {
                if (name is "C" or "c")
                {
                    _i++;
                    string inner = ReadVariable();
                    SkipWs();
                    if (Eof || _s[_i] != ')') throw Error("Expected ')' to close C(…).");
                    _i++;
                    _categorical.Add(inner);
                    return inner;
                }
                throw Error($"Function '{name}(…)' is not supported. Only C(column) is available.", start);
            }
            return name;
        }

        private string ReadQuoted(char open, char close)
        {
            int start = _i;
            _i++; // open
            int end = _s.IndexOf(close, _i);
            if (end < 0) throw Error($"Missing closing '{close}'.", start);
            string name = _s[_i..end];
            _i = end + 1;
            if (name.Length == 0) throw Error("Empty column name.", start);
            return name;
        }

        private bool Eof => _i >= _s.Length;
        private void SkipWs() { while (!Eof && char.IsWhiteSpace(_s[_i])) _i++; }
        private FormulaParseException Error(string message, int? at = null) => new(message, at ?? _i);
    }

    /// <summary>식 편집기 보조: 컬럼 이름을 식에 넣을 형태로 인용한다.</summary>
    public static class FormulaText
    {
        public static string QuoteName(string name)
            => ModelFormula.IsBareIdentifier(name) && name is not ("C" or "c") ? name : "[" + name.Replace("]", "") + "]";

        /// <summary>응답과 설명 변수 목록으로 주효과 식을 만든다(범주 변수는 C()로 감쌀지 선택).</summary>
        public static string MainEffects(string response, IEnumerable<string> predictors, Func<string, bool>? wrapCategorical = null)
        {
            var sb = new StringBuilder(QuoteName(response)).Append(" ~ ");
            var parts = predictors.Select(p => wrapCategorical?.Invoke(p) == true ? $"C({QuoteName(p)})" : QuoteName(p)).ToList();
            sb.Append(parts.Count == 0 ? "1" : string.Join(" + ", parts));
            return sb.ToString();
        }
    }
}
