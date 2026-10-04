using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NanumCsvViewer.Csv
{
    public enum CsvSearchMode
    {
        Contains,
        Regex,
        Fuzzy
    }

    /// <summary>잘못된 정규식 등 검색 쿼리 구성 실패.</summary>
    public sealed class CsvSearchException : Exception
    {
        public CsvSearchException(string message) : base(message) { }
    }

    /// <summary>검색 조건: 텍스트 + 모드 + (선택) 컬럼 스코프.</summary>
    public sealed class CsvSearchQuery
    {
        public string Text { get; }
        public CsvSearchMode Mode { get; }
        public int? Column { get; }

        // 정규식 모드에서 한 번만 컴파일한 결과(검증 겸용). 매처가 그대로 재사용한다.
        internal Regex? CompiledRegex { get; }

        /// <exception cref="CsvSearchException">정규식 모드에서 패턴이 비었거나 잘못됨.</exception>
        public CsvSearchQuery(string text, CsvSearchMode mode, int? column)
        {
            Text = text;
            Mode = mode;
            Column = column;
            if (mode == CsvSearchMode.Regex)
            {
                try { CompiledRegex = RegexSafety.Compile(text); }
                catch (RegexPatternException ex) { throw new CsvSearchException(ex.Message); }
            }
        }

        /// <summary>
        /// 사용자 입력을 모드로 라우팅: <c>/pattern/</c> 또는 <c>regex:</c> → Regex,
        /// <c>fuzzy:</c> → Fuzzy, 그 외 → Contains. 빈 문자열이면 null.
        /// </summary>
        public static CsvSearchQuery? FromUserInput(string input, int? column)
        {
            string trimmed = input.Trim();
            if (trimmed.Length == 0) return null;

            if (trimmed.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                return new CsvSearchQuery(trimmed.Substring("regex:".Length).Trim(), CsvSearchMode.Regex, column);
            if (trimmed.StartsWith("fuzzy:", StringComparison.OrdinalIgnoreCase))
                return new CsvSearchQuery(trimmed.Substring("fuzzy:".Length).Trim(), CsvSearchMode.Fuzzy, column);
            if (trimmed.Length >= 2 && trimmed[0] == '/' && trimmed[^1] == '/')
                return new CsvSearchQuery(trimmed.Substring(1, trimmed.Length - 2), CsvSearchMode.Regex, column);

            return new CsvSearchQuery(trimmed, CsvSearchMode.Contains, column);
        }
    }

    /// <summary>
    /// 컴파일된 검색기. 정규식은 한 번만 컴파일하여 셀마다 재사용.
    /// 셀 하나가 정규식 시간 제한(<see cref="RegexSafety.MatchTimeout"/>)을 넘기면 그 셀은 불일치로 처리하되
    /// <see cref="Timeouts"/>에 센다 — 호출부가 검색이 끝난 뒤 사용자에게 알린다.
    /// </summary>
    public sealed class CsvSearchMatcher
    {
        private readonly CsvSearchQuery _query;
        private readonly Regex? _regex;

        /// <summary>정규식 시간 초과 셀 수(정규식 모드에서만 증가).</summary>
        public RegexTimeoutCounter Timeouts { get; } = new();

        public CsvSearchMatcher(CsvSearchQuery query)
        {
            _query = query;
            if (query.Mode == CsvSearchMode.Regex)
                _regex = query.CompiledRegex ?? RegexSafety.Compile(query.Text);
        }

        /// <summary>
        /// 행에서 첫 일치 컬럼·값을 반환(없으면 null). 컬럼 스코프가 있으면 그 컬럼만.
        /// ct는 셀 사이에서 확인한다(정규식이 셀마다 시간 제한까지 걸려도 취소가 한 셀 안에 반응).
        /// </summary>
        public (int Column, string Value)? FirstMatch(string[] row, CancellationToken ct = default)
        {
            if (_query.Column is int scoped)
            {
                if (scoped < 0 || scoped >= row.Length) return null;
                if (_regex is not null) ct.ThrowIfCancellationRequested();
                return Matches(row[scoped]) ? (scoped, row[scoped]) : null;
            }
            for (int c = 0; c < row.Length; c++)
            {
                if (_regex is not null) ct.ThrowIfCancellationRequested();
                if (Matches(row[c])) return (c, row[c]);
            }
            return null;
        }

        private bool Matches(string value)
        {
            switch (_query.Mode)
            {
                case CsvSearchMode.Contains:
                    return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                        value, _query.Text,
                        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
                case CsvSearchMode.Regex:
                    return _regex is not null && RegexSafety.IsMatch(_regex, value, Timeouts);
                case CsvSearchMode.Fuzzy:
                    return FuzzyContains(value, _query.Text);
                default:
                    return false;
            }
        }

        /// <summary>순서 보존 부분일치: query 문자들이 value에 순서대로 등장하면 일치.</summary>
        private static bool FuzzyContains(string value, string query)
        {
            string needle = Normalize(query);
            if (needle.Length == 0) return true;
            int n = 0;
            foreach (char c in Normalize(value))
            {
                if (c == needle[n])
                {
                    n++;
                    if (n == needle.Length) return true;
                }
            }
            return false;
        }

        private static string Normalize(string value)
        {
            // 대소문자·분음 무시: 소문자화 + 분음 제거 후 공백 트림.
            string lowered = value.Trim().ToLowerInvariant();
            var sb = new StringBuilder(lowered.Length);
            foreach (char c in lowered.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
