using System.Collections;
using System.Diagnostics;
using System.Globalization;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests;

/// <summary>
/// The basic analyses snapshot only the columns they declare. Every case runs the analysis on the full-width rows and on
/// the projected rows (all other cells null) and requires an identical report: a column that is read but not declared
/// either throws (null cell) or changes the result, because every column of the test data carries different content.
/// </summary>
public class AnalysisProjectionTests
{
    private const int Width = 8;
    private static readonly string[] Labels = Enumerable.Range(0, Width).Select(i => $"col{i}").ToArray();

    // 0 group (A/B/C) · 1 numeric with gaps · 2 numeric · 3 date · 4 numeric · 5 group (x/y) · 6 text · 7 numeric
    private static List<string[]> Data()
    {
        var rng = new Random(7);
        var inv = CultureInfo.InvariantCulture;
        string[] groups = { "A", "B", "C" };
        var rows = new List<string[]>();
        for (int i = 0; i < 240; i++)
        {
            string g = groups[i % 3];
            double v1 = 10 + (i * 7) % 23 + (g == "B" ? 5 : 0) + rng.NextDouble();
            var row = new[]
            {
                g,
                i % 29 == 0 ? "NA" : v1.ToString("F2", inv),
                (v1 * 0.5 + rng.NextDouble() * 4).ToString("F3", inv),
                new DateTime(2024, 1, 1).AddDays(i * 3).ToString("yyyy-MM-dd", inv),
                (i % 17 + rng.NextDouble()).ToString("F2", inv),
                i % 5 < 2 ? "x" : "y",
                "junk-" + rng.Next(),
                rng.Next(1000).ToString(inv),
            };
            rows.Add(i is 50 or 111 ? row[..3] : row); // ragged rows keep their own length
        }
        return rows;
    }

    private static AnalysisReport? Run(List<string[]> rows, Action<AnalysisWork> compute, bool sourceRows)
    {
        var withNumbers = sourceRows ? rows.Select((r, i) => (r, (long)i + 1)).ToList() : null;
        var work = new AnalysisWork(rows, Labels, default, withNumbers);
        compute(work);
        return work.Report;
    }

    private static void AssertProjectionIsLossless(IReadOnlyCollection<int> columns, Action<AnalysisWork> compute, bool sourceRows = false)
    {
        var full = Data();
        var projected = AnalysisSnapshot.Collect(full, default, columns).Rows;

        for (int r = 0; r < full.Count; r++)
        {
            Assert.Equal(full[r].Length, projected[r].Length);
            for (int c = 0; c < full[r].Length; c++)
                Assert.Equal(columns.Contains(c) ? full[r][c] : null, projected[r][c]);
        }

        var expected = Run(full, compute, sourceRows);
        var actual = Run(projected, compute, sourceRows);
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Title, actual!.Title);
        Assert.Equal(expected.Body, actual.Body);
        Assert.Equal(expected.Chart, actual.Chart);
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.False(string.IsNullOrWhiteSpace(expected.Body));
    }

    [Fact]
    public void Distribution_reads_only_its_column() =>
        AssertProjectionIsLossless(BasicAnalyses.DistributionColumns(1), w => BasicAnalyses.Distribution(w, 1, 6));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Date_histogram_reads_only_the_date_and_optional_value_column(int valueSelection)
    {
        var columns = BasicAnalyses.DateHistogramColumns(3, valueSelection);
        Assert.Equal(valueSelection == 0 ? new[] { 3 } : new[] { 3, 4 }, columns);
        AssertProjectionIsLossless(columns, w => BasicAnalyses.DateHistogram(w, 3, valueSelection, 2));
    }

    [Fact]
    public void Duplicates_read_only_the_key_columns() =>
        AssertProjectionIsLossless(BasicAnalyses.DuplicatesColumns(new[] { 0, 5 }),
            w => BasicAnalyses.Duplicates(w, new[] { 0, 5 }), sourceRows: true);

    [Fact]
    public void Group_by_reads_only_group_and_value_columns()
    {
        var funcs = new[] { AggregationFunction.Count, AggregationFunction.Sum, AggregationFunction.Mean, AggregationFunction.Median };
        var columns = BasicAnalyses.GroupByColumns(new[] { 0, 5 }, 1);
        Assert.Equal(new[] { 0, 5, 1 }, columns);
        AssertProjectionIsLossless(columns, w => BasicAnalyses.GroupBy(w, new[] { 0, 5 }, 1, funcs));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Correlation_reads_only_x_and_y(int method) =>
        AssertProjectionIsLossless(BasicAnalyses.CorrelationColumns(1, 2), w => BasicAnalyses.Correlation(w, 1, 2, method));

    [Fact]
    public void Independent_t_test_reads_only_value_and_group() =>
        AssertProjectionIsLossless(BasicAnalyses.IndependentTTestColumns(1, 0), w => BasicAnalyses.IndependentTTest(w, 1, 0));

    [Fact]
    public void Paired_t_test_reads_only_both_columns() =>
        AssertProjectionIsLossless(BasicAnalyses.PairedTTestColumns(1, 2), w => BasicAnalyses.PairedTTest(w, 1, 2));

    [Fact]
    public void Chi_square_reads_only_row_and_column_variables() =>
        AssertProjectionIsLossless(BasicAnalyses.ChiSquareColumns(0, 5), w => BasicAnalyses.ChiSquare(w, 0, 5));

    [Fact]
    public void Descriptives_read_only_the_selected_columns()
    {
        var cols = new[] { 1, 2, 4 };
        AssertProjectionIsLossless(BasicAnalyses.DescriptivesColumns(cols), w => BasicAnalyses.Descriptives(w, cols));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(5, 1)]
    [InlineData(6, 3)] // includes the ragged rows, which count as empty values
    public void Frequency_reads_only_its_column(int column, int limit) =>
        AssertProjectionIsLossless(BasicAnalyses.FrequencyColumns(column), w => BasicAnalyses.Frequency(w, column, limit));

    [Fact]
    public void One_way_anova_reads_only_value_and_group() =>
        AssertProjectionIsLossless(BasicAnalyses.OneWayAnovaColumns(4, 0), w => BasicAnalyses.OneWayAnova(w, 4, 0));

    [Fact]
    public void Normality_reads_only_its_column() =>
        AssertProjectionIsLossless(BasicAnalyses.NormalityColumns(2), w => BasicAnalyses.Normality(w, 2));

    [Fact]
    public void Reading_an_unprojected_column_is_detectable()
    {
        // Guards the harness itself: an analysis that needs column 1 but only projects column 2 must NOT pass.
        var full = Data();
        var projected = AnalysisSnapshot.Collect(full, default, new[] { 2 }).Rows;
        Assert.ThrowsAny<Exception>(() =>
        {
            var expected = Run(full, w => BasicAnalyses.Distribution(w, 1, 6), false);
            var actual = Run(projected, w => BasicAnalyses.Distribution(w, 1, 6), false);
            Assert.Equal(expected!.Body, actual!.Body);
        });
    }

    // ------------------------------------------------------------------ memory estimate

    [Fact]
    public void Estimate_counts_only_projected_cells()
    {
        string[] Row(int unprojectedLength, int projectedLength)
        {
            var row = Enumerable.Repeat(new string('u', unprojectedLength), 60).ToArray();
            row[3] = new string('p', projectedLength);
            return row;
        }

        long Estimate(string[] row, params int[] keep) => AnalysisSnapshot.EstimateRowBytes(row, keep);

        long baseline = Estimate(Row(10, 10), 3);
        Assert.Equal(baseline, Estimate(Row(5_000, 10), 3));            // unprojected text is free
        Assert.Equal(baseline + 2 * 90, Estimate(Row(10, 100), 3));      // projected text costs 2 bytes per char
        Assert.True(Estimate(Row(1_000, 1_000), 3) < AnalysisSnapshot.EstimateRowBytes(Row(1_000, 1_000), null) / 20);
        Assert.Equal(Estimate(Row(10, 10), 3) + 32 + 2 * 10, Estimate(Row(10, 10), 3, 4));
        Assert.Equal(48 + 8 * 60, Estimate(Row(10, 10)));                // nothing kept: only the array
    }

    [Fact]
    public void Projection_fits_a_budget_the_full_copy_exceeds()
    {
        var rows = Enumerable.Range(0, 100).Select(i => Enumerable.Range(0, 50).Select(c => new string('v', 100)).ToArray()).ToArray();
        const long budget = 300_000; // full ≈ 100 × 50 × 240 B = 1.2 MB; one column ≈ 100 × (48 + 400 + 232) B
        Assert.Throws<AnalysisMemoryLimitException>(() => AnalysisSnapshot.Collect(rows, default, memoryBudgetBytes: budget));
        Assert.Throws<AnalysisMemoryLimitException>(() => AnalysisSnapshot.Collect(rows, default, Enumerable.Range(0, 50).ToArray(), budget));
        var snapshot = AnalysisSnapshot.Collect(rows, default, new[] { 7 }, budget);
        Assert.Equal(100, snapshot.Rows.Count);
        Assert.All(snapshot.Rows, r => Assert.Equal(50, r.Length));
        Assert.All(snapshot.Rows, r => Assert.Single(r, cell => cell is not null));
        Assert.All(snapshot.Rows, r => Assert.NotNull(r[7]));
        Assert.Throws<AnalysisMemoryLimitException>(() => AnalysisSnapshot.Collect(rows, default, new[] { 7 }, 20_000));
    }

    [Fact]
    public void Projection_ignores_column_indexes_beyond_a_short_row_and_rejects_negatives()
    {
        var rows = new[] { new[] { "a", "b" }, new[] { "c" } };
        var snapshot = AnalysisSnapshot.Collect(rows, default, new[] { 1, 1, 5 });
        Assert.Equal(new string?[] { null, "b" }, snapshot.Rows[0]);
        Assert.Equal(new string?[] { null }, snapshot.Rows[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisSnapshot.Collect(rows, default, new[] { -1 }));
    }

    [Theory]
    [InlineData(1L << 30, 512L << 20)]         // 1 GB machine → floor
    [InlineData(8L << 30, 2L << 30)]           // 25 %
    [InlineData(16L << 30, 4L << 30)]
    [InlineData(128L << 30, 4L << 30)]         // ceiling
    public void Budget_is_a_quarter_of_available_memory_between_512_MB_and_4_GB(long available, long expected)
        => Assert.Equal(expected, AnalysisMemoryBudget.ForAvailableMemory(available));

    [Fact]
    public void Current_budget_is_within_bounds() =>
        Assert.InRange(AnalysisMemoryBudget.Current, AnalysisMemoryBudget.MinimumBytes, AnalysisMemoryBudget.MaximumBytes);

    // ------------------------------------------------------------------ wide file (the reported failure)

    private sealed class WideRows(int count, int width) : IReadOnlyList<string[]>
    {
        private static readonly string[] Pool = Enumerable.Range(0, 1000).Select(i => "value" + i.ToString("D5")).ToArray();
        public int Count => count;
        public string[] this[int index]
        {
            get
            {
                var row = new string[width];
                for (int c = 0; c < width; c++) row[c] = Pool[(index + c * 31) % Pool.Length];
                row[1] = (index % 977).ToString(CultureInfo.InvariantCulture);
                row[2] = index % 3 == 0 ? "A" : "B";
                return row;
            }
        }
        public IEnumerator<string[]> GetEnumerator() { for (int i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Wide_file_150k_rows_by_60_columns_snapshots_only_the_needed_columns()
    {
        var source = new WideRows(150_000, 60);
        const long oldFixedBudget = 128L * 1024 * 1024;

        // What the reported file did: every column of every row against a fixed 128 MB.
        Assert.Throws<AnalysisMemoryLimitException>(() => AnalysisSnapshot.Collect(source, default, memoryBudgetBytes: oldFixedBudget));

        var sw = Stopwatch.StartNew();
        var snapshot = AnalysisSnapshot.Collect(source, default, new[] { 1, 2 });
        Assert.Equal(150_000, snapshot.Rows.Count);

        var work = new AnalysisWork(snapshot.Rows, Enumerable.Range(0, 60).Select(i => $"c{i}").ToArray(), default);
        BasicAnalyses.Descriptives(work, new[] { 1 });
        Assert.Contains("N (valid)    150,000", work.Report!.Body.Replace('\u00a0', ' '));
        BasicAnalyses.Frequency(work, 2, 10);
        var frequency = work.Report!.Body;
        Assert.True(frequency.Contains("unique 2") || frequency.Contains("고유값 2"), frequency);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }
}
