using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class CsvAnalyticsTests
    {
        [Fact]
        public void Percentile_interpolates()
        {
            double[] sorted = { 1, 2, 3, 4 };
            Assert.Equal(2.5, CsvAnalytics.Percentile(sorted, 0.5), 6);
            Assert.Equal(1.75, CsvAnalytics.Percentile(sorted, 0.25), 6);
        }

        [Fact]
        public void Find_duplicates_groups_by_key()
        {
            var rows = new List<(string[], long)>
            {
                (new[] { "a", "1" }, 1),
                (new[] { "b", "2" }, 2),
                (new[] { "a", "1" }, 3),
                (new[] { "a", "1" }, 4),
            };
            var dups = CsvAnalytics.FindDuplicates(rows, new[] { 0, 1 });
            Assert.Single(dups);
            Assert.Equal(new long[] { 1, 3, 4 }, dups[0].SourceRows);
        }

        [Fact]
        public void GroupBy_aggregates()
        {
            var rows = new List<string[]>
            {
                new[] { "A", "10" },
                new[] { "A", "20" },
                new[] { "B", "100" },
            };
            var result = CsvAnalytics.GroupBy(rows, new[] { 0 }, 1,
                new[] { AggregationFunction.Sum, AggregationFunction.Mean, AggregationFunction.Count });
            var groupA = result.Rows.First(r => r.Key[0] == "A");
            Assert.Equal(30, groupA.Values[AggregationFunction.Sum], 6);
            Assert.Equal(15, groupA.Values[AggregationFunction.Mean], 6);
            Assert.Equal(2, groupA.Values[AggregationFunction.Count], 6);
        }

        [Fact]
        public void Numeric_distribution_stats()
        {
            var values = new double[] { 1, 2, 3, 4, 5 };
            var dist = CsvAnalytics.NumericDistributionOf(values, 0, 5);
            Assert.Equal(5, dist.Count);
            Assert.Equal(1, dist.Min);
            Assert.Equal(5, dist.Max);
            Assert.Equal(3, dist.Mean, 6);
            Assert.Equal(3, dist.Median, 6);
            Assert.Equal(5, dist.Bins.Sum(b => b.Count));
        }

        [Fact]
        public void Date_histogram_by_month()
        {
            var rows = new List<string[]>
            {
                new[] { "2024-01-05", "10" },
                new[] { "2024-01-20", "20" },
                new[] { "2024-02-10", "30" },
            };
            var hist = CsvAnalytics.DateHistogramOf(rows, 0, 1, DateBinPeriod.Month);
            Assert.Equal(2, hist.Bins.Count);
            var jan = hist.Bins.First(b => b.Label == "2024-01");
            Assert.Equal(2, jan.Count);
            Assert.Equal(30, jan.Sum);
            Assert.Equal(15, jan.Average);
        }

        [Fact]
        public void Pivot_table_sum()
        {
            var rows = new List<string[]>
            {
                new[] { "East", "Q1", "100" },
                new[] { "East", "Q2", "200" },
                new[] { "West", "Q1", "50" },
            };
            var pivot = CsvAnalytics.PivotTable(rows, new[] { 0 }, new[] { 1 }, 2, AggregationFunction.Sum);
            Assert.Equal(2, pivot.RowKeys.Count);     // East, West
            Assert.Equal(2, pivot.ColumnKeys.Count);  // Q1, Q2
            Assert.Equal(100, pivot.Value(new[] { "East" }, new[] { "Q1" }), 6);
            Assert.Equal(200, pivot.Value(new[] { "East" }, new[] { "Q2" }), 6);
            Assert.Equal(0, pivot.Value(new[] { "West" }, new[] { "Q2" }), 6);
        }

        [Fact]
        public void Pivot_null_grouping()
        {
            var rows = new List<string[]>
            {
                new[] { "", "5" },
                new[] { "n/a", "5" },
                new[] { "A", "5" },
            };
            var pivot = CsvAnalytics.PivotTable(rows, new[] { 0 }, Array.Empty<int>(), 1, AggregationFunction.Count);
            // "" 와 "n/a" 는 모두 "null" 그룹으로 합쳐짐 → 행키 2개(null, A)
            Assert.Equal(2, pivot.RowKeys.Count);
        }
    }
}
