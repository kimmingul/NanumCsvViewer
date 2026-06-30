using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>컬럼 값의 추론 타입.</summary>
    public enum ColumnValueType
    {
        Integer,
        Float,
        Date,
        DateTime,
        Time,
        Boolean,
        Categorical,
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
            ColumnValueType.Date => "Date",
            ColumnValueType.DateTime => "DateTime",
            ColumnValueType.Time => "Time",
            ColumnValueType.Boolean => "Boolean",
            ColumnValueType.Categorical => "Categorical",
            ColumnValueType.Identifier => "Identifier",
            ColumnValueType.String => "String",
            ColumnValueType.Empty => "Empty",
            _ => "String"
        };

        /// <summary>날짜 성분이 있는 타입(Date 또는 DateTime). 날짜 필터·날짜 그룹핑 대상 판정용.</summary>
        public static bool HasDateComponent(this ColumnValueType type)
            => type is ColumnValueType.Date or ColumnValueType.DateTime;
    }

    public readonly record struct NumericColumnSummary(
        double Min, double Max, double Mean, double Median, double StandardDeviation);

    public readonly record struct TopValue(string Value, int Count);

    public sealed class ColumnSummary
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public ColumnValueType InferredType { get; init; }
        public int NullCount { get; init; }
        public int NonNullCount { get; init; }
        public int UniqueCount { get; init; }
        public NumericColumnSummary? Numeric { get; init; }
        public IReadOnlyList<TopValue> TopValues { get; init; } = Array.Empty<TopValue>();
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
                    if (Math.Truncate(number) != number || value.Contains('.') ||
                        value.IndexOf('e', StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        integerCompatible = false;
                    }
                }
                else
                {
                    integerCompatible = false;
                    floatCompatible = false;
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
            else if (integerCompatible && (hasLeadingZero || headerSuggestsId))
                inferredType = ColumnValueType.Identifier;
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
