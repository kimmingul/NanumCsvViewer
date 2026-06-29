using System.Globalization;

namespace NanumCsvViewer.Csv
{
    public enum AggregationFunction
    {
        Count,
        Sum,
        Mean,
        Median,
        Min,
        Max,
        UniqueCount,
        StandardDeviation
    }

    public static class AggregationFunctionExtensions
    {
        public static string DisplayName(this AggregationFunction f) => f switch
        {
            AggregationFunction.Count => "Count",
            AggregationFunction.Sum => "Sum",
            AggregationFunction.Mean => "Mean",
            AggregationFunction.Median => "Median",
            AggregationFunction.Min => "Min",
            AggregationFunction.Max => "Max",
            AggregationFunction.UniqueCount => "Unique Count",
            AggregationFunction.StandardDeviation => "Std",
            _ => f.ToString()
        };
    }

    public enum DateBinPeriod
    {
        Day,
        Week,
        Month,
        Year
    }

    public readonly record struct DuplicateGroup(IReadOnlyList<string> Key, IReadOnlyList<long> SourceRows);

    public sealed class GroupByRow
    {
        public IReadOnlyList<string> Key { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<AggregationFunction, double> Values { get; init; }
            = new Dictionary<AggregationFunction, double>();
    }

    public sealed class GroupByResult
    {
        public IReadOnlyList<int> GroupColumns { get; init; } = Array.Empty<int>();
        public int ValueColumn { get; init; }
        public IReadOnlyList<AggregationFunction> Functions { get; init; } = Array.Empty<AggregationFunction>();
        public IReadOnlyList<GroupByRow> Rows { get; init; } = Array.Empty<GroupByRow>();
    }

    public readonly record struct HistogramBin(double LowerBound, double UpperBound, int Count);

    public sealed class NumericDistribution
    {
        public int Column { get; init; }
        public int Count { get; init; }
        public double Min { get; init; }
        public double Max { get; init; }
        public double Mean { get; init; }
        public double Median { get; init; }
        public double Q1 { get; init; }
        public double Q3 { get; init; }
        public double StandardDeviation { get; init; }
        public IReadOnlyList<HistogramBin> Bins { get; init; } = Array.Empty<HistogramBin>();
    }

    public readonly record struct DateHistogramBin(string Label, int Count, double? Sum, double? Average);

    public sealed class DateHistogram
    {
        public int DateColumn { get; init; }
        public int? ValueColumn { get; init; }
        public DateBinPeriod Period { get; init; }
        public IReadOnlyList<DateHistogramBin> Bins { get; init; } = Array.Empty<DateHistogramBin>();
    }

    public readonly record struct PivotFilter(int Column, string? SelectedValue);

    /// <summary>피벗 셀 키: 행키 + 열키 조합. 구조적 동등성으로 딕셔너리 키 사용.</summary>
    public sealed class PivotCellKey : IEquatable<PivotCellKey>
    {
        public IReadOnlyList<string> Row { get; }
        public IReadOnlyList<string> Column { get; }
        private readonly string _composite;

        public PivotCellKey(IReadOnlyList<string> row, IReadOnlyList<string> column)
        {
            Row = row;
            Column = column;
            _composite = string.Join('', row) + "" + string.Join('', column);
        }

        public bool Equals(PivotCellKey? other) => other is not null && _composite == other._composite;
        public override bool Equals(object? obj) => Equals(obj as PivotCellKey);
        public override int GetHashCode() => _composite.GetHashCode();
    }

    public sealed class PivotTableResult
    {
        public IReadOnlyList<int> RowColumns { get; init; } = Array.Empty<int>();
        public IReadOnlyList<string> RowColumnNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<int> ColumnColumns { get; init; } = Array.Empty<int>();
        public int ValueColumn { get; init; }
        public AggregationFunction Function { get; init; }
        public IReadOnlyList<string[]> RowKeys { get; init; } = Array.Empty<string[]>();
        public IReadOnlyList<string[]> ColumnKeys { get; init; } = Array.Empty<string[]>();
        public IReadOnlyDictionary<PivotCellKey, double> Values { get; init; }
            = new Dictionary<PivotCellKey, double>();

        public double Value(IReadOnlyList<string> row, IReadOnlyList<string> column)
            => Values.TryGetValue(new PivotCellKey(row, column), out double v) ? v : 0;
    }

    /// <summary>분석/집계 엔진. macOS CsvAnalytics 이식. 행 입력은 호출자가 현재 뷰에서 공급.</summary>
    public static class CsvAnalytics
    {
        private static readonly HashSet<string> NullTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "", "na", "n/a", "null", "nil", "missing"
        };

        public static IReadOnlyList<DuplicateGroup> FindDuplicates(
            IReadOnlyList<(string[] Fields, long SourceRow)> rows, IReadOnlyList<int> columns)
        {
            var groups = new Dictionary<string, (List<string> Key, List<long> Rows)>();
            foreach (var row in rows)
            {
                var keyParts = columns.Select(c => c < row.Fields.Length ? row.Fields[c] : "").ToList();
                string composite = string.Join('', keyParts);
                if (!groups.TryGetValue(composite, out var entry))
                {
                    entry = (keyParts, new List<long>());
                    groups[composite] = entry;
                }
                entry.Rows.Add(row.SourceRow);
            }

            return groups.Values
                .Where(g => g.Rows.Count > 1)
                .Select(g =>
                {
                    g.Rows.Sort();
                    return new DuplicateGroup(g.Key, g.Rows);
                })
                .OrderBy(g => g.SourceRows.Count > 0 ? g.SourceRows[0] : 0)
                .ThenBy(g => string.Join('', g.Key), StringComparer.Ordinal)
                .ToList();
        }

        public static GroupByResult GroupBy(
            IReadOnlyList<string[]> rows, IReadOnlyList<int> groupColumns, int valueColumn,
            IReadOnlyList<AggregationFunction> functions)
        {
            var groups = new Dictionary<string, (List<string> Key, List<string> Values)>();
            foreach (var row in rows)
            {
                var keyParts = groupColumns.Select(c => c < row.Length ? row[c] : "").ToList();
                string value = valueColumn < row.Length ? row[valueColumn] : "";
                string composite = string.Join('', keyParts);
                if (!groups.TryGetValue(composite, out var entry))
                {
                    entry = (keyParts, new List<string>());
                    groups[composite] = entry;
                }
                entry.Values.Add(value);
            }

            var resultRows = groups.Values.Select(g =>
            {
                var numbers = ParseNumbers(g.Values);
                var output = new Dictionary<AggregationFunction, double>();
                foreach (var fn in functions)
                    output[fn] = Aggregate(fn, g.Values, numbers);
                return new GroupByRow { Key = g.Key, Values = output };
            })
            .OrderBy(r => string.Join('', r.Key), StringComparer.OrdinalIgnoreCase)
            .ToList();

            return new GroupByResult
            {
                GroupColumns = groupColumns.ToArray(),
                ValueColumn = valueColumn,
                Functions = functions.ToArray(),
                Rows = resultRows
            };
        }

        public static NumericDistribution NumericDistributionOf(IReadOnlyList<double> values, int column, int binCount)
        {
            var sorted = values.ToArray();
            Array.Sort(sorted);
            int count = sorted.Length;
            double minValue = count == 0 ? 0 : sorted[0];
            double maxValue = count == 0 ? 0 : sorted[^1];
            double mean = count == 0 ? 0 : sorted.Sum() / count;
            double std = 0;
            if (count > 0)
            {
                double acc = 0;
                foreach (double v in sorted) acc += (v - mean) * (v - mean);
                std = Math.Sqrt(acc / count);
            }
            var bins = Histogram(sorted, minValue, maxValue, Math.Max(1, binCount));
            return new NumericDistribution
            {
                Column = column,
                Count = count,
                Min = minValue,
                Max = maxValue,
                Mean = mean,
                Median = Percentile(sorted, 0.5),
                Q1 = Percentile(sorted, 0.25),
                Q3 = Percentile(sorted, 0.75),
                StandardDeviation = std,
                Bins = bins
            };
        }

        public static DateHistogram DateHistogramOf(
            IReadOnlyList<string[]> rows, int dateColumn, int? valueColumn, DateBinPeriod period)
        {
            var buckets = new Dictionary<string, (int Count, double Sum)>();
            foreach (var row in rows)
            {
                if (dateColumn >= row.Length) continue;
                var date = CsvDateParser.Parse(row[dateColumn], allowCompactNumeric: true);
                if (date is null) continue;
                string label = DateLabel(date.Value, period);
                double value = 0;
                if (valueColumn is int vc && vc < row.Length &&
                    double.TryParse(row[vc].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    value = parsed;
                }
                var current = buckets.TryGetValue(label, out var b) ? b : (0, 0.0);
                buckets[label] = (current.Item1 + 1, current.Item2 + value);
            }

            bool hasValue = valueColumn is not null;
            var bins = buckets.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(label =>
            {
                var bucket = buckets[label];
                return new DateHistogramBin(
                    label,
                    bucket.Count,
                    hasValue ? bucket.Sum : (double?)null,
                    hasValue && bucket.Count > 0 ? bucket.Sum / bucket.Count : (double?)null);
            }).ToList();

            return new DateHistogram { DateColumn = dateColumn, ValueColumn = valueColumn, Period = period, Bins = bins };
        }

        public static PivotTableResult PivotTable(
            IReadOnlyList<string[]> rows,
            IReadOnlyList<int> rowColumns,
            IReadOnlyList<int> columnColumns,
            int valueColumn,
            AggregationFunction function,
            IReadOnlyList<string>? rowColumnNames = null,
            IReadOnlyList<PivotFilter>? filters = null,
            IReadOnlyDictionary<int, DateBinPeriod>? dateGroupings = null,
            CancellationToken cancellation = default)
        {
            dateGroupings ??= new Dictionary<int, DateBinPeriod>();
            var activeFilters = (filters ?? Array.Empty<PivotFilter>()).Where(f => f.SelectedValue is not null).ToList();

            var raw = new Dictionary<PivotCellKey, List<string>>();
            var rowKeySet = new HashSet<string>();
            var rowKeyList = new List<string[]>();
            var columnKeySet = new HashSet<string>();
            var columnKeyList = new List<string[]>();

            int index = 0;
            foreach (var row in rows)
            {
                if ((index++ & 0x3FFF) == 0) cancellation.ThrowIfCancellationRequested();
                if (!PivotRowMatches(row, activeFilters, dateGroupings)) continue;

                var rowKey = rowColumns.Select(c => PivotKeyValue(row, c, dateGroupings)).ToArray();
                var columnKey = columnColumns.Select(c => PivotKeyValue(row, c, dateGroupings)).ToArray();
                string value = valueColumn < row.Length ? row[valueColumn] : "";

                if (rowKeySet.Add(string.Join('', rowKey))) rowKeyList.Add(rowKey);
                if (columnKeySet.Add(string.Join('', columnKey))) columnKeyList.Add(columnKey);

                var cellKey = new PivotCellKey(rowKey, columnKey);
                if (!raw.TryGetValue(cellKey, out var list)) { list = new List<string>(); raw[cellKey] = list; }
                list.Add(value);
            }

            rowKeyList.Sort((a, b) => string.CompareOrdinal(string.Join('', a), string.Join('', b)));
            columnKeyList.Sort((a, b) => string.CompareOrdinal(string.Join('', a), string.Join('', b)));

            var values = new Dictionary<PivotCellKey, double>();
            int i = 0;
            foreach (var kv in raw)
            {
                if ((i++ & 0x3FFF) == 0) cancellation.ThrowIfCancellationRequested();
                var numbers = ParseNumbers(kv.Value);
                values[kv.Key] = Aggregate(function, kv.Value, numbers);
            }

            return new PivotTableResult
            {
                RowColumns = rowColumns.ToArray(),
                RowColumnNames = (rowColumnNames ?? Array.Empty<string>()).ToArray(),
                ColumnColumns = columnColumns.ToArray(),
                ValueColumn = valueColumn,
                Function = function,
                RowKeys = rowKeyList,
                ColumnKeys = columnKeyList,
                Values = values
            };
        }

        public static string PivotKeyValue(string[] row, int column, IReadOnlyDictionary<int, DateBinPeriod> dateGroupings)
        {
            string raw = column < row.Length ? row[column] : "";
            string trimmed = raw.Trim();
            if (IsNull(trimmed)) return "null";
            if (dateGroupings.TryGetValue(column, out var period))
            {
                var date = CsvDateParser.Parse(raw, allowCompactNumeric: true);
                if (date is not null) return DateLabel(date.Value, period);
            }
            return raw;
        }

        private static bool PivotRowMatches(
            string[] row, IReadOnlyList<PivotFilter> filters, IReadOnlyDictionary<int, DateBinPeriod> dateGroupings)
        {
            foreach (var filter in filters)
            {
                if (filter.SelectedValue is null) continue;
                if (PivotKeyValue(row, filter.Column, dateGroupings) != filter.SelectedValue) return false;
            }
            return true;
        }

        private static bool IsNull(string value) => NullTokens.Contains(value);

        private static List<double> ParseNumbers(IReadOnlyList<string> values)
        {
            var numbers = new List<double>(values.Count);
            foreach (var v in values)
                if (double.TryParse(v.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    numbers.Add(d);
            return numbers;
        }

        private static double Aggregate(AggregationFunction function, IReadOnlyList<string> rawValues, List<double> numbers)
        {
            switch (function)
            {
                case AggregationFunction.Count:
                    return rawValues.Count;
                case AggregationFunction.Sum:
                    return numbers.Sum();
                case AggregationFunction.Mean:
                    return numbers.Count == 0 ? 0 : numbers.Sum() / numbers.Count;
                case AggregationFunction.Median:
                {
                    var sorted = numbers.ToArray();
                    Array.Sort(sorted);
                    return Percentile(sorted, 0.5);
                }
                case AggregationFunction.Min:
                    return numbers.Count == 0 ? 0 : numbers.Min();
                case AggregationFunction.Max:
                    return numbers.Count == 0 ? 0 : numbers.Max();
                case AggregationFunction.UniqueCount:
                    return new HashSet<string>(rawValues).Count;
                case AggregationFunction.StandardDeviation:
                {
                    if (numbers.Count == 0) return 0;
                    double mean = numbers.Sum() / numbers.Count;
                    double acc = 0;
                    foreach (double v in numbers) acc += (v - mean) * (v - mean);
                    return Math.Sqrt(acc / numbers.Count);
                }
                default:
                    return 0;
            }
        }

        public static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            if (sorted.Length == 1) return sorted[0];
            double position = p * (sorted.Length - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper) return sorted[lower];
            double fraction = position - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
        }

        private static List<HistogramBin> Histogram(double[] values, double minValue, double maxValue, int binCount)
        {
            var result = new List<HistogramBin>();
            if (values.Length == 0) return result;
            if (minValue == maxValue)
            {
                result.Add(new HistogramBin(minValue, maxValue, values.Length));
                return result;
            }
            double width = (maxValue - minValue) / binCount;
            var counts = new int[binCount];
            foreach (double v in values)
            {
                int idx = Math.Min(binCount - 1, Math.Max(0, (int)((v - minValue) / width)));
                counts[idx]++;
            }
            for (int i = 0; i < binCount; i++)
            {
                double lower = minValue + i * width;
                result.Add(new HistogramBin(lower, i == binCount - 1 ? maxValue : lower + width, counts[i]));
            }
            return result;
        }

        private static string DateLabel(DateTime date, DateBinPeriod period)
        {
            switch (period)
            {
                case DateBinPeriod.Day:
                    return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case DateBinPeriod.Week:
                    int week = System.Globalization.ISOWeek.GetWeekOfYear(date);
                    int weekYear = System.Globalization.ISOWeek.GetYear(date);
                    return $"{weekYear:D4}-W{week:D2}";
                case DateBinPeriod.Month:
                    return date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                case DateBinPeriod.Year:
                    return date.ToString("yyyy", CultureInfo.InvariantCulture);
                default:
                    return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }
    }
}
