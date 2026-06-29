using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>컬럼 값의 추론 타입.</summary>
    public enum ColumnValueType
    {
        Integer,
        Float,
        Date,
        Boolean,
        Categorical,
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
            ColumnValueType.Boolean => "Boolean",
            ColumnValueType.Categorical => "Categorical",
            ColumnValueType.String => "String",
            ColumnValueType.Empty => "Empty",
            _ => "String"
        };
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
            bool dateCompatible = true;
            bool booleanCompatible = true;
            bool allowCompactNumericDates = CsvDateParser.HeaderSuggestsDate(name);

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

                if (dateCompatible && CsvDateParser.Parse(value, allowCompactNumericDates) is null)
                    dateCompatible = false;
                if (booleanCompatible && !BooleanTokens.Contains(value))
                    booleanCompatible = false;
            }

            int nonNullCount = values.Count;
            ColumnValueType inferredType;
            if (nonNullCount == 0)
                inferredType = ColumnValueType.Empty;
            else if (booleanCompatible)
                inferredType = ColumnValueType.Boolean;
            else if (dateCompatible && ((!integerCompatible && !floatCompatible) || allowCompactNumericDates))
                inferredType = ColumnValueType.Date;
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
                Numeric = numericValues.Count == 0 ? null : SummarizeNumbers(numericValues),
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
