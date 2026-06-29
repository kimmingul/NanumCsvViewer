using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class ColumnStatisticsTests
    {
        private static ColumnSummary Summarize(string header, params string[] values)
        {
            var rows = values.Select(v => new[] { v }).ToList();
            return ColumnStatisticsBuilder.SummarizeColumn(0, header, rows);
        }

        [Fact]
        public void Infers_integer()
        {
            Assert.Equal(ColumnValueType.Integer, Summarize("n", "2", "3", "4", "100").InferredType);
        }

        [Fact]
        public void Infers_float()
        {
            Assert.Equal(ColumnValueType.Float, Summarize("x", "1.5", "2.0", "3.25").InferredType);
        }

        [Fact]
        public void Infers_boolean()
        {
            Assert.Equal(ColumnValueType.Boolean, Summarize("flag", "true", "false", "yes", "no").InferredType);
        }

        [Fact]
        public void Infers_date_with_date_header()
        {
            Assert.Equal(ColumnValueType.Date, Summarize("date", "2024-01-01", "2024-02-01", "2024-03-01").InferredType);
        }

        [Fact]
        public void Infers_categorical_for_few_distinct_strings()
        {
            Assert.Equal(ColumnValueType.Categorical, Summarize("city", "서울", "부산", "서울", "대구", "부산").InferredType);
        }

        [Fact]
        public void Counts_nulls_and_uniques()
        {
            var s = Summarize("c", "a", "", "a", "b", "n/a");
            Assert.Equal(2, s.NullCount);       // "" 와 "n/a"
            Assert.Equal(3, s.NonNullCount);    // a, a, b
            Assert.Equal(2, s.UniqueCount);     // a, b
        }

        [Fact]
        public void Numeric_summary_values()
        {
            var s = Summarize("n", "1", "2", "3", "4");
            Assert.NotNull(s.Numeric);
            Assert.Equal(1, s.Numeric!.Value.Min);
            Assert.Equal(4, s.Numeric.Value.Max);
            Assert.Equal(2.5, s.Numeric.Value.Mean, 6);
            Assert.Equal(2.5, s.Numeric.Value.Median, 6);
        }

        [Fact]
        public void Empty_column_is_empty_type()
        {
            Assert.Equal(ColumnValueType.Empty, Summarize("e", "", "n/a", "null").InferredType);
        }
    }
}
