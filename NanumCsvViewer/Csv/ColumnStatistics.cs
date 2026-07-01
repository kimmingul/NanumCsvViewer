using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>컬럼 값의 추론 타입.</summary>
    public enum ColumnValueType
    {
        Integer,
        Float,
        Currency,
        Percent,
        Scientific,
        Date,
        DateTime,
        Time,
        Boolean,
        Categorical,
        Ordinal,
        Identifier,
        String,
        Empty
    }

    public static class ColumnValueTypeExtensions
    {
        /// <summary>헤더 태그·UI 표시용 이름.</summary>
        public static string DisplayName(this ColumnValueType type) => type switch
        {
            ColumnValueType.Integer => "Integer",
            ColumnValueType.Float => "Float",
            ColumnValueType.Currency => "Currency",
            ColumnValueType.Percent => "Percent",
            ColumnValueType.Scientific => "Scientific",
            ColumnValueType.Date => "Date",
            ColumnValueType.DateTime => "DateTime",
            ColumnValueType.Time => "Time",
            ColumnValueType.Boolean => "Boolean",
            ColumnValueType.Categorical => "Categorical",
            ColumnValueType.Ordinal => "Ordinal",
            ColumnValueType.Identifier => "Identifier",
            ColumnValueType.String => "String",
            ColumnValueType.Empty => "Empty",
            _ => "String"
        };

        /// <summary>날짜 성분이 있는 타입(Date 또는 DateTime). 날짜 필터·날짜 그룹핑 대상 판정용.</summary>
        public static bool HasDateComponent(this ColumnValueType type)
            => type is ColumnValueType.Date or ColumnValueType.DateTime;

        /// <summary>수치로 다뤄야 하는 타입(통계·정렬·수치필터·분포분석 대상).</summary>
        public static bool IsNumeric(this ColumnValueType type)
            => type is ColumnValueType.Integer or ColumnValueType.Float
                    or ColumnValueType.Currency or ColumnValueType.Percent or ColumnValueType.Scientific;

        /// <summary>범주로 다뤄야 하는 타입(헤더 값 필터 대상). 순서형 포함.</summary>
        public static bool IsCategorical(this ColumnValueType type)
            => type is ColumnValueType.Categorical or ColumnValueType.Ordinal;
    }

    /// <summary>통화·퍼센트 등 숫자에 붙는 표시 단위를 파싱·정규화하는 헬퍼.</summary>
    public static class NumericAffix
    {
        // 인식하는 통화 기호: 원·달러·엔/위안·유로·파운드(+ 전각 ￥, 위안 元).
        private static readonly char[] CurrencyChars = { '₩', '$', '¥', '￥', '元', '€', '£' };

        public static bool IsCurrencyChar(char c) => Array.IndexOf(CurrencyChars, c) >= 0;

        /// <summary>선행/후행 통화 기호와 천단위 콤마를 제거해 숫자로 파싱. hadSymbol=기호가 있었는지.</summary>
        public static bool TryParseCurrency(string value, out double number, out char symbol, out bool hadSymbol)
        {
            number = 0; symbol = '\0'; hadSymbol = false;
            string s = value.Trim();
            if (s.Length == 0) return false;
            if (IsCurrencyChar(s[0])) { symbol = s[0]; hadSymbol = true; s = s[1..].Trim(); }
            else if (IsCurrencyChar(s[^1])) { symbol = s[^1]; hadSymbol = true; s = s[..^1].Trim(); }
            s = s.Replace(",", "");
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>후행 '%'와 콤마를 제거해 숫자로 파싱. hadPercent=% 기호가 있었는지.</summary>
        public static bool TryParsePercent(string value, out double number, out bool hadPercent)
        {
            number = 0; hadPercent = false;
            string s = value.Trim();
            if (s.EndsWith("%")) { hadPercent = true; s = s[..^1].Trim(); }
            s = s.Replace(",", "");
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>일반 숫자 → 통화 → 퍼센트 순으로 시도하는 통합 숫자 추출(필터·정렬·통계 공용).</summary>
        public static bool TryParseNumber(string value, out double number)
        {
            if (double.TryParse(value.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return true;
            if (TryParseCurrency(value, out number, out _, out bool hadSym) && hadSym) return true;
            if (TryParsePercent(value, out number, out bool hadPct) && hadPct) return true;
            number = 0;
            return false;
        }
    }

    /// <summary>통화·퍼센트 컬럼의 그리드 표시 스킨. 선언된 원시 숫자에 기호/%를 입힌다(표시 전용).</summary>
    public static class CellDisplay
    {
        public static string Format(ColumnValueType type, char? currencySymbol, bool percentIsFraction, string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            switch (type)
            {
                case ColumnValueType.Currency:
                    // 기호가 알려졌고 셀이 원시 숫자일 때만 기호+천단위로 표시. 이미 기호가 붙은 텍스트는 그대로.
                    if (currencySymbol is char sym &&
                        double.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double cv))
                        return sym + cv.ToString("#,0.################", CultureInfo.InvariantCulture);
                    return raw;
                case ColumnValueType.Percent:
                    if (raw.TrimEnd().EndsWith("%")) return raw; // 이미 %가 붙은 추론 텍스트
                    if (double.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double pv))
                    {
                        double shown = percentIsFraction ? Math.Round(pv * 100, 10) : pv; // ×100의 부동소수 노이즈 제거
                        return shown.ToString("0.################", CultureInfo.InvariantCulture) + "%";
                    }
                    return raw;
                default:
                    return raw;
            }
        }
    }

    /// <summary>SAS·SPSS가 파일에 명시한 컬럼 타입 힌트(추론 대신 사용). 통화 기호·퍼센트 표기 방식 포함.</summary>
    public sealed record ColumnTypeHint(
        ColumnValueType Type,
        char? CurrencySymbol = null,
        bool PercentIsFraction = false);

    public readonly record struct NumericColumnSummary(
        double Min, double Max, double Mean, double Median, double StandardDeviation);

    public readonly record struct TopValue(string Value, int Count);

    public sealed record ColumnSummary
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public ColumnValueType InferredType { get; init; }
        public int NullCount { get; init; }
        public int NonNullCount { get; init; }
        public int UniqueCount { get; init; }
        public NumericColumnSummary? Numeric { get; init; }
        public IReadOnlyList<TopValue> TopValues { get; init; } = Array.Empty<TopValue>();
        /// <summary>통화 타입일 때 표시할 기호(선언 힌트가 지정). null이면 셀 텍스트를 그대로 표시.</summary>
        public char? CurrencySymbol { get; init; }
        /// <summary>퍼센트 타입일 때 밑값이 분수(0.25=25%)인지. true면 표시 시 ×100.</summary>
        public bool PercentIsFraction { get; init; }
    }

    public sealed class ColumnStatisticsReport
    {
        public int RowSampleCount { get; init; }
        public IReadOnlyList<ColumnSummary> Columns { get; init; } = Array.Empty<ColumnSummary>();
    }

    /// <summary>표본 행으로 컬럼별 추론 타입과 요약 통계를 계산. macOS ColumnStatisticsBuilder 이식.</summary>
    public static class ColumnStatisticsBuilder
    {
        private static readonly HashSet<string> NullTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "", "na", "n/a", "null", "nil", "missing"
        };

        private static readonly HashSet<string> BooleanTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "yes", "no", "y", "n", "0", "1"
        };

        private static readonly HashSet<string> IdHeaderTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "id", "no", "code", "key", "num", "number", "uuid", "guid", "seq"
        };

        private static readonly string[] IdHeaderSubstrings = { "번호", "코드", "식별", "일련" };

        // 헤더명이 식별자 컬럼을 암시하는지(영문 토큰 또는 한국어 부분문자열).
        private static bool HeaderSuggestsIdentifier(string name)
        {
            foreach (var sub in IdHeaderSubstrings)
                if (name.Contains(sub)) return true;
            foreach (var token in TokenizeAlphanumeric(name.ToLowerInvariant()))
                if (IdHeaderTokens.Contains(token)) return true;
            return false;
        }

        private static IEnumerable<string> TokenizeAlphanumeric(string s)
        {
            int start = -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetterOrDigit(s[i])) { if (start < 0) start = i; }
                else if (start >= 0) { yield return s[start..i]; start = -1; }
            }
            if (start >= 0) yield return s[start..];
        }

        public static ColumnStatisticsReport Summarize(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
        {
            int columnCount = headers.Count;
            var columns = new ColumnSummary[columnCount];
            for (int c = 0; c < columnCount; c++)
                columns[c] = SummarizeColumn(c, headers[c], rows);
            return new ColumnStatisticsReport { RowSampleCount = rows.Count, Columns = columns };
        }

        /// <summary>단일 컬럼만 빠르게 추론(헤더 태그 초기 표시용).</summary>
        public static ColumnSummary SummarizeColumn(int index, string name, IReadOnlyList<string[]> rows)
        {
            int nullCount = 0;
            var frequencies = new Dictionary<string, int>();
            var values = new List<string>();
            var numericValues = new List<double>();
            bool integerCompatible = true;
            bool floatCompatible = true;
            bool booleanCompatible = true;
            bool temporalCompatible = true;
            bool temporalHasDate = false;
            bool temporalHasTime = false;
            // 통화·퍼센트·지수 감지: 모든 비어있지 않은 값이 기호를 가질 때만 성립(기호 없는 값이 하나라도
            // 섞이면 오분류 방지). *HasBare = 기호 없는 순수 숫자가 섞였는지.
            bool currencyCompatible = true, sawCurrency = false, currencyHasBare = false;
            bool percentCompatible = true, sawPercent = false, percentHasBare = false;
            bool sawScientific = false;
            // 헤더가 날짜를 암시하지 않아도 컴팩트 숫자 날짜(yyyyMMdd 등)를 날짜로 인정한다.
            const bool allowCompactNumericDates = true;
            // 식별자(수량이 아닌 코드) 감지: 선행 0이 있는 숫자 또는 헤더 키워드(id·번호·코드…).
            bool hasLeadingZero = false;
            bool headerSuggestsId = HeaderSuggestsIdentifier(name);

            foreach (var row in rows)
            {
                string raw = index < row.Length ? row[index] : string.Empty;
                string value = raw.Trim();
                if (IsNull(value)) { nullCount++; continue; }

                values.Add(value);
                frequencies[value] = frequencies.TryGetValue(value, out int f) ? f + 1 : 1;

                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
                {
                    numericValues.Add(number);
                    bool hasExp = value.IndexOf('e', StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hasExp) sawScientific = true;
                    if (Math.Truncate(number) != number || value.Contains('.') || hasExp)
                        integerCompatible = false;
                }
                else
                {
                    integerCompatible = false;
                    floatCompatible = false;
                }

                // 통화/퍼센트: 기호가 붙어 plain 파싱이 실패한 값의 숫자 크기를 통계에 반영(기호가 있을 때만 add해 중복 방지).
                if (currencyCompatible)
                {
                    if (NumericAffix.TryParseCurrency(value, out double cnum, out _, out bool hadSym))
                    { if (hadSym) { sawCurrency = true; numericValues.Add(cnum); } else currencyHasBare = true; }
                    else currencyCompatible = false;
                }
                if (percentCompatible)
                {
                    if (NumericAffix.TryParsePercent(value, out double pnum, out bool hadPct))
                    { if (hadPct) { sawPercent = true; numericValues.Add(pnum); } else percentHasBare = true; }
                    else percentCompatible = false;
                }

                if (temporalCompatible)
                {
                    var temporal = CsvDateParser.ParseDetailed(value, allowCompactNumericDates);
                    if (temporal is null)
                        temporalCompatible = false;
                    else
                    {
                        if (temporal.Value.Kind != TemporalKind.Time) temporalHasDate = true;
                        if (temporal.Value.Kind != TemporalKind.Date) temporalHasTime = true;
                    }
                }
                if (booleanCompatible && !BooleanTokens.Contains(value))
                    booleanCompatible = false;
                if (!hasLeadingZero && value.Length > 1 && value[0] == '0' && value.All(char.IsAsciiDigit))
                    hasLeadingZero = true;
            }

            int nonNullCount = values.Count;
            ColumnValueType inferredType;
            if (nonNullCount == 0)
                inferredType = ColumnValueType.Empty;
            else if (booleanCompatible)
                inferredType = ColumnValueType.Boolean;
            else if (temporalCompatible)
                inferredType = (temporalHasDate, temporalHasTime) switch
                {
                    (true, true) => ColumnValueType.DateTime,
                    (false, true) => ColumnValueType.Time,
                    _ => ColumnValueType.Date,
                };
            else if (percentCompatible && sawPercent && !percentHasBare)
                inferredType = ColumnValueType.Percent;
            else if (currencyCompatible && sawCurrency && !currencyHasBare)
                inferredType = ColumnValueType.Currency;
            else if (integerCompatible && (hasLeadingZero || headerSuggestsId))
                inferredType = ColumnValueType.Identifier;
            else if (floatCompatible && sawScientific)
                inferredType = ColumnValueType.Scientific;
            else if (integerCompatible)
                inferredType = ColumnValueType.Integer;
            else if (floatCompatible)
                inferredType = ColumnValueType.Float;
            else if (frequencies.Count <= Math.Max(20, nonNullCount / 2))
                inferredType = ColumnValueType.Categorical;
            else
                inferredType = ColumnValueType.String;

            var topValues = frequencies
                .Select(kv => new TopValue(kv.Key, kv.Value))
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Value, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToArray();

            return new ColumnSummary
            {
                Index = index,
                Name = name,
                InferredType = inferredType,
                NullCount = nullCount,
                NonNullCount = nonNullCount,
                UniqueCount = frequencies.Count,
                // 식별자는 수량이 아니므로 평균/표준편차 등 수치 통계를 붙이지 않는다.
                Numeric = (inferredType == ColumnValueType.Identifier || numericValues.Count == 0)
                    ? null : SummarizeNumbers(numericValues),
                TopValues = topValues
            };
        }

        private static bool IsNull(string value) => NullTokens.Contains(value);

        private static NumericColumnSummary SummarizeNumbers(List<double> values)
        {
            var sorted = values.ToArray();
            Array.Sort(sorted);
            double sum = 0;
            foreach (double v in sorted) sum += v;
            double mean = sum / sorted.Length;
            double median = sorted.Length % 2 == 0
                ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
                : sorted[sorted.Length / 2];
            double variance = 0;
            foreach (double v in sorted) variance += (v - mean) * (v - mean);
            variance /= sorted.Length;
            return new NumericColumnSummary(sorted[0], sorted[^1], mean, median, Math.Sqrt(variance));
        }
    }
}
