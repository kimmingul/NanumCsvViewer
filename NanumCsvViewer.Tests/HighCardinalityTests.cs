using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Charting;

namespace NanumCsvViewer.Tests;

// 실제 Form1을 만드는 테스트가 있으므로 다른 Form1 테스트와 직렬화한다(WinForms KeysConverter 초기화 경쟁).
[Collection("SavedViewStore")]
public class HighCardinalityTests
{
    [Fact]
    public void Analysis_snapshot_is_complete_or_fails_without_returning_a_prefix()
    {
        var rows = new[] { new[] { "first" }, new[] { "last" } };
        Assert.Equal(rows, AnalysisSnapshot.Collect(rows, default).Rows);
        Assert.Throws<AnalysisMemoryLimitException>(() => AnalysisSnapshot.Collect(rows, default, memoryBudgetBytes: 100));
        Assert.Throws<IOException>(() => AnalysisSnapshot.Collect(new FailingRows(), default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => AnalysisSnapshot.Collect(rows, cancellation.Token));
    }

    [Fact]
    public void Analysis_snapshot_does_not_silently_stop_at_two_million_rows()
    {
        var source = Enumerable.Repeat(Array.Empty<string>(), 2_000_001).ToArray();
        Assert.Equal(2_000_001, AnalysisSnapshot.Collect(source, default).Rows.Count);
    }

    private sealed class FailingRows : IReadOnlyList<string[]>
    {
        public int Count => 3;
        public string[] this[int index] => index == 1 ? throw new IOException("Read failure") : new[] { "row" };
        public IEnumerator<string[]> GetEnumerator() => Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DeferredRows(Func<string[]> read) : IReadOnlyList<string[]>
    {
        public int Count => 1;
        public string[] this[int index] => index == 0 ? read() : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<string[]> GetEnumerator() { yield return this[0]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class GeneratedRows : IReadOnlyList<string[]>
    {
        public int Count { get; set; } = 3;
        public string[] this[int index] => new[] { index.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        public IEnumerator<string[]> GetEnumerator() => Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(AggregationFunction.Median, 1)]
    [InlineData(AggregationFunction.UniqueCount, 3)]
    public void Multi_measure_pivot_recovers_from_real_memory_budget_failure(AggregationFunction function, double expected) => OnSta(() =>
    {
        var rows = new GeneratedRows();
        using var pivot = new PivotForm(new[] { "value" }, Array.Empty<ColumnSummary>(), rows, ThemePalette.Light, AppTheme.Light);
        for (int i = 0; i < 32; i++)
        {
            AddConfiguration(pivot, "_measures", 0);
            var measure = Field<IList>(pivot, "_measures")[i]!;
            measure.GetType().GetField("Func")!.SetValue(measure, function);
        }
        PumpUntil((Task)Invoke(pivot, "RefreshResultAsync")!);
        var previous = Field<List<PivotTableResult>>(pivot, "_results");
        Assert.Equal(32, previous.Count);
        Assert.All(previous, r => Assert.Equal(expected, r.Value(Array.Empty<string>(), Array.Empty<string>())));
        rows.Count = 100_000; // Each measure receives 2MiB; retained exact values exceed it.
        PumpUntil((Task)Invoke(pivot, "RefreshResultAsync")!);
        Assert.Same(previous, Field<List<PivotTableResult>>(pivot, "_results"));
        Assert.NotEmpty(Field<Label>(pivot, "_operationStatus").Text);
        Assert.True(Field<DataGridView>(pivot, "_resultGrid").Enabled);
        rows.Count = 3;
        for (int run = 0; run < 12; run++)
        {
            PumpUntil((Task)Invoke(pivot, "RefreshResultAsync")!);
            Assert.Empty(Field<Label>(pivot, "_operationStatus").Text);
            var results = Field<List<PivotTableResult>>(pivot, "_results");
            Assert.All(results, r => Assert.Equal(expected, r.Value(Array.Empty<string>(), Array.Empty<string>())));
            Assert.Equal(32, Field<Dictionary<int, double>>(pivot, "_grandCache").Count);
            Assert.Single(Field<List<Task>>(pivot, "_readerTasks"));
        }
    });

    [Fact]
    public void Sparse_chi_square_matches_dense_reference_including_empty_cells()
    {
        var random = new Random(917);
        for (int trial = 0; trial < 30; trial++)
        {
            var pairs = new List<(string Row, string Column)>();
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 7; c++)
                    for (int i = 0, n = random.Next(8); i < n; i++) pairs.Add(($"r{r}", $"c{c}"));
            var result = CsvStatistics.ChiSquare(pairs);
            double expectedStatistic = 0;
            long smallCells = 0;
            double minimumExpected = double.PositiveInfinity;
            for (int r = 0; r < result.RowLabels.Count; r++)
                for (int c = 0; c < result.ColumnLabels.Count; c++)
                {
                    double observed = pairs.Count(p => p.Row == result.RowLabels[r] && p.Column == result.ColumnLabels[c]);
                    double expected = pairs.Count(p => p.Row == result.RowLabels[r]) *
                        (double)pairs.Count(p => p.Column == result.ColumnLabels[c]) / pairs.Count;
                    expectedStatistic += Math.Pow(observed - expected, 2) / expected;
                    if (expected < 5) smallCells++;
                    minimumExpected = Math.Min(minimumExpected, expected);
                    Assert.Equal(observed, result.Observed[r][c]);
                }
            Assert.Equal(expectedStatistic, result.Statistic, 8);
            Assert.Equal(smallCells, result.ExpectedCellsBelowFive);
            Assert.Equal(minimumExpected, result.MinimumExpectedCount, 8);
        }
    }

    [Fact]
    public void Chi_square_supports_large_sparse_tables_without_df_overflow()
    {
        const int size = 100_000;
        var pairs = Enumerable.Range(0, size).Select(i => ($"r{i:D6}", $"c{i:D6}")).ToArray();
        var result = CsvStatistics.ChiSquare(pairs);
        Assert.Equal((long)(size - 1) * (size - 1), result.DegreesOfFreedom);
        Assert.InRange(Math.Abs(result.Statistic - size * (double)(size - 1)), 0, 0.1);
        Assert.Equal(1, result.Observed[size - 1][size - 1]);
        Assert.Equal(0, result.Observed[0][size - 1]);
        Assert.False(result.HasReliableApproximation);
        Assert.Equal((long)size * size, result.ExpectedCellsBelowFive);
        Assert.Throws<AnalysisMemoryLimitException>(() => CsvStatistics.ChiSquare(pairs, memoryBudgetBytes: 1024));
    }

    [Fact]
    public void Heatmap_rejects_unrenderable_dimensions_before_allocating_matrix()
    {
        Assert.Throws<AnalysisMemoryLimitException>(() => ChartBuilders.CorrelationHeatmap(new(),
            Enumerable.Range(0, 129).ToArray(), Enumerable.Repeat("column", 129).ToArray()));
    }

    [Fact]
    public void Pivot_keys_do_not_merge_embedded_separators()
    {
        var rows = new[] { new[] { "a\u001fb", "c", "1" }, new[] { "a", "b\u001fc", "2" } };
        var result = CsvAnalytics.PivotTable(rows, new[] { 0, 1 }, Array.Empty<int>(), 2, AggregationFunction.Sum);
        Assert.Equal(2, result.RowKeys.Count);
        Assert.Equal(1, result.Value(rows[0][..2], Array.Empty<string>()));
        Assert.Equal(2, result.Value(rows[1][..2], Array.Empty<string>()));
        Assert.Equal(2, CsvAnalytics.GroupBy(rows, new[] { 0, 1 }, 2, new[] { AggregationFunction.Sum }).Rows.Count);
        Assert.Empty(CsvAnalytics.FindDuplicates(rows.Select((r, i) => (r, (long)i + 1)).ToArray(), new[] { 0, 1 }));
        Assert.NotEqual(new PivotCellKey(new[] { "a\u001eb" }, new[] { "c" }),
            new PivotCellKey(new[] { "a" }, new[] { "b\u001ec" }));
    }

    [Theory]
    [InlineData(AggregationFunction.Count, 5)]
    [InlineData(AggregationFunction.Sum, 8)]
    [InlineData(AggregationFunction.Mean, 2)]
    [InlineData(AggregationFunction.Min, 1)]
    [InlineData(AggregationFunction.Max, 3)]
    [InlineData(AggregationFunction.Median, 2)]
    [InlineData(AggregationFunction.UniqueCount, 4)]
    [InlineData(AggregationFunction.StandardDeviation, 0.7071067811865476)]
    public void Streaming_aggregates_preserve_exact_semantics(AggregationFunction function, double expected)
    {
        var rows = new[] { "1", "2", "3", "2", "missing" }.Select(v => new[] { v }).ToArray();
        var result = CsvAnalytics.PivotTable(rows, Array.Empty<int>(), Array.Empty<int>(), 0, function);
        Assert.Equal(expected, result.Value(Array.Empty<string>(), Array.Empty<string>()), 10);
        var grouped = CsvAnalytics.GroupBy(rows, Array.Empty<int>(), 0, new[] { function });
        Assert.Equal(expected, grouped.Rows.Single().Values[function], 10);
    }

    [Fact]
    public void Memory_limit_and_cancellation_do_not_publish_partial_pivots()
    {
        var rows = Enumerable.Range(0, 10000).Select(i => new[] { i.ToString() }).ToArray();
        Assert.Throws<PivotMemoryLimitException>(() => CsvAnalytics.PivotTable(rows, new[] { 0 },
            Array.Empty<int>(), 0, AggregationFunction.Count, memoryBudgetBytes: 4096));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CsvAnalytics.PivotTable(Array.Empty<string[]>(),
            Array.Empty<int>(), Array.Empty<int>(), 0, AggregationFunction.Count, cancellation: cts.Token));
    }

    [Fact]
    public void Streaming_max_preserves_existing_NaN_semantics()
    {
        var rows = new[] { new[] { "NaN" }, new[] { "3" }, new[] { "NaN" }, new[] { "2" } };
        Assert.Equal(3, CsvAnalytics.PivotTable(rows, Array.Empty<int>(), Array.Empty<int>(), 0,
            AggregationFunction.Max).Value(Array.Empty<string>(), Array.Empty<string>()));
        Assert.Equal(3, CsvAnalytics.GroupBy(rows, Array.Empty<int>(), 0,
            new[] { AggregationFunction.Max }).Rows.Single().Values[AggregationFunction.Max]);
    }

    [Fact]
    public void Many_rows_in_one_count_cell_need_no_retained_values()
    {
        var rows = Enumerable.Repeat(new[] { "100" }, 1_000_000).ToArray();
        var result = CsvAnalytics.PivotTable(rows, Array.Empty<int>(), Array.Empty<int>(), 0,
            AggregationFunction.Count, memoryBudgetBytes: 4096);
        Assert.Equal(1_000_000, result.Value(Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void Filter_pages_preserve_selection_and_search_all_categories() => OnSta(() =>
    {
        var values = Enumerable.Range(0, 100_000).Select(i => ($"category{i:D6}", 1)).ToArray();
        using var popup = new ColumnFilterPopup("category", values, null, ThemePalette.Light);
        var list = Field<CheckedListBox>(popup, "_list");
        var page = Field<NumericUpDown>(popup, "_page");
        Assert.Equal(200, list.Items.Count);
        Assert.Equal(500, page.Maximum);
        list.SetItemChecked(0, false);
        page.Value = page.Maximum;
        Assert.Contains("category099999", list.Items[^1]!.ToString());
        page.Value = 1;
        Assert.False(list.GetItemChecked(0));
        Invoke(popup, "Populate", "category099999");
        Assert.Single(list.Items.Cast<object>());
        Invoke(popup, "SetAllChecks", false);
        var state = Field<Dictionary<string, bool>>(popup, "_checkState");
        Assert.False(state["category099999"]);
        Assert.True(state["category000001"]);
    });

    [Fact]
    public void Sparse_pivot_with_twenty_thousand_categories_renders_only_current_page() => OnSta(() =>
    {
        var rows = Enumerable.Range(0, 20_000).Select(i => new[] { $"r{i:D5}", $"c{i:D5}", "2" }).ToArray();
        using var form = new PivotForm(new[] { "row", "column", "value" }, Array.Empty<ColumnSummary>(), rows,
            ThemePalette.Light, AppTheme.Light);
        AddConfiguration(form, "_rowDims", 0);
        AddConfiguration(form, "_colDims", 1);
        AddConfiguration(form, "_measures", 2);
        var task = (Task)Invoke(form, "RefreshResultAsync")!;
        PumpUntil(task);
        var results = Field<List<PivotTableResult>>(form, "_results");
        Assert.Equal(20_000, results.Single().Values.Count);
        var grid = Field<DataGridView>(form, "_resultGrid");
        Assert.Equal(101, grid.RowCount);
        Assert.Equal(42, grid.ColumnCount);
        var rowPage = Field<NumericUpDown>(form, "_rowPage");
        var columnPage = Field<NumericUpDown>(form, "_columnPage");
        rowPage.Value = rowPage.Maximum;
        columnPage.Value = columnPage.Maximum;
        Assert.Equal("r19999", grid[0, 99].Value);
        Assert.Equal("1", grid[40, 99].Value); // Count for the last observed cell.
        Assert.Equal("20,000", grid[41, 100].Value); // Total covers all pages.
        var secondRun = (Task)Invoke(form, "RefreshResultAsync")!;
        var cancellation = Field<CancellationTokenSource>(form, "_runCancellation");
        var duplicateRun = (Task)Invoke(form, "RefreshResultAsync")!;
        Assert.True(duplicateRun.IsCompleted);
        Assert.Same(cancellation, Field<CancellationTokenSource>(form, "_runCancellation"));
        cancellation.Cancel();
        PumpUntil(secondRun);
        Assert.Same(results, Field<List<PivotTableResult>>(form, "_results"));
        Assert.True(grid.Enabled);
    });

    private static void AddConfiguration(object form, string name, int column)
    {
        var list = Field<IList>(form, name);
        var item = Activator.CreateInstance(list.GetType().GetGenericArguments()[0])!;
        item.GetType().GetField("Field")!.SetValue(item, column);
        list.Add(item);
    }

    [Fact]
    public void Closing_main_window_waits_for_cancelled_reader_before_releasing_document() => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"nanum-close-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "value\nlast read\n");
        try
        {
            using var doc = VirtualCsvDocument.Open(path);
            PumpUntil(doc.RunIndexingAsync(new Progress<IndexProgress>(), default));
            using var form = new Form1(new AppSettings());
            _ = form.Handle;
            SetField(form, "_doc", doc);
            using var cancellation = new CancellationTokenSource();
            SetField(form, "_facetCts", cancellation);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(async () =>
            {
                await release.Task;
                Assert.Equal("last read", doc.GetDataRowUncached(0)[0]);
            });
            SetField(form, "_facetTask", reader);
            bool closedAfterRead = false;
            form.FormClosed += (_, _) => closedAfterRead = reader.IsCompletedSuccessfully;
            try
            {
                form.Close();
                Application.DoEvents();
                Assert.True(cancellation.IsCancellationRequested);
                Assert.False(form.IsDisposed);
                var shutdown = Field<Task>(form, "_shutdownTask");
                form.Close();
                Assert.Same(shutdown, Field<Task>(form, "_shutdownTask"));
                release.SetResult();
                PumpUntil(shutdown);
                reader.GetAwaiter().GetResult();
                Assert.True(form.IsDisposed);
                Assert.True(closedAfterRead);
            }
            finally { release.TrySetResult(); }
        }
        finally { File.Delete(path); }
    });

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    [Fact]
    public void Facets_discard_completed_stale_results_after_successive_filter_changes() => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"nanum-facets-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, new[] { "category" }.Concat(Enumerable.Range(0, 50_000).Select(i => $"cat{i:D5}")));
        try
        {
            using var doc = VirtualCsvDocument.Open(path);
            PumpUntil(doc.RunIndexingAsync(new Progress<IndexProgress>(), default));
            using var form = new Form1(new AppSettings());
            _ = form.Handle;
            SetField(form, "_doc", doc);
            SetField(form, "_columnSummaries", new[] { new ColumnSummary { Index = 0, InferredType = ColumnValueType.Categorical } });
            Invoke(form, "EnsureFacetsPanel");
            SetField(form, "_facetsVisible", true);
            var panel = Field<FlowLayoutPanel>(form, "_facetsPanel");
            for (int change = 0; change < 8; change++)
            {
                doc.ClearView();
                Invoke(form, "BuildFacets");
                // Finish computation without pumping the UI, so its old result
                // is queued but has not been applied when the view changes.
                Assert.True(Field<Task>(form, "_facetTask").Wait(TimeSpan.FromSeconds(5)));
                string target = $"cat{49_999 - change:D5}";
                doc.ApplyFilterAsync(row => row[0] == target, null, default).GetAwaiter().GetResult();
                Invoke(form, "UpdateFilterStatus");
                Assert.True(Field<Task>(form, "_facetTask").Wait(TimeSpan.FromSeconds(5)));
                PumpUntilUi(() => Field<CancellationTokenSource?>(form, "_facetCts") is null);
                var facet = Assert.Single(panel.Controls.OfType<FacetView>());
                var displayed = Assert.Single(Field<(string Label, int Count, Action OnClick)[]>(facet, "_rows"));
                Assert.Equal(target, displayed.Label);
                Assert.Equal(1, displayed.Count);
                Assert.Null(Field<CancellationTokenSource?>(form, "_facetCts"));
                Assert.True(panel.Enabled);
            }
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void Main_close_drains_active_pivot_reads_before_document_disposal() => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"nanum-pivot-close-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "value\n1\n");
        try
        {
            using var doc = VirtualCsvDocument.Open(path);
            PumpUntil(doc.RunIndexingAsync(new Progress<IndexProgress>(), default));
            using var form = new Form1(new AppSettings());
            _ = form.Handle;
            SetField(form, "_doc", doc);
            using var entered = new ManualResetEventSlim();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new DeferredRows(() =>
            {
                entered.Set();
                release.Task.GetAwaiter().GetResult();
                return doc.GetDataRowUncached(0);
            });
            using var pivot = new PivotForm(new[] { "value" }, Array.Empty<ColumnSummary>(), source, ThemePalette.Light, AppTheme.Light);
            SetField(form, "_pivotForm", pivot);
            AddConfiguration(pivot, "_measures", 0);
            var run = (Task)Invoke(pivot, "RefreshResultAsync")!;
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                form.Close();
                Application.DoEvents();
                Assert.False(form.IsDisposed);
                Assert.False(pivot.ReaderCompletion.IsCompleted);
                release.SetResult();
                PumpUntil(Field<Task>(form, "_shutdownTask"));
                PumpUntil(run);
                Assert.True(pivot.ReaderCompletion.IsCompleted);
                Assert.True(form.IsDisposed);
            }
            finally { release.TrySetResult(); }
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void General_analysis_uses_worker_snapshot_and_preserves_source_rows() => OnSta(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"nanum-analysis-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "value,other\nkeep1,x\nskip,y\nkeep2,z\n");
        try
        {
            using var doc = VirtualCsvDocument.Open(path);
            PumpUntil(doc.RunIndexingAsync(new Progress<IndexProgress>(), default));
            PumpUntil(doc.ApplyFilterAsync(row => row[0] != "skip", null, default));
            using var form = new Form1(new AppSettings());
            _ = form.Handle;
            SetField(form, "_doc", doc);
            using var entered = new ManualResetEventSlim();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int uiThread = Environment.CurrentManagedThreadId, workerThread = uiThread;
            long[]? sourceNumbers = null;
            string[]? values = null;
            string?[]? unprojected = null;
            Action<AnalysisWork> compute = work =>
            {
                workerThread = Environment.CurrentManagedThreadId;
                sourceNumbers = work.SourceRows.Select(r => r.SourceRow).ToArray();
                values = work.Rows.Select(r => r[0]).ToArray();
                unprojected = work.Rows.Select(r => r[1]).ToArray();
                entered.Set();
                release.Task.GetAwaiter().GetResult();
                work.Cancellation.ThrowIfCancellationRequested();
            };
            var task = (Task)Invoke(form, "RunAnalysisAsync", new[] { 0 }, compute, true)!;
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                bool heartbeat = false;
                form.BeginInvoke(() => heartbeat = true);
                Application.DoEvents();
                Assert.True(heartbeat);
                Assert.True(Field<bool>(form, "_busy"));
                var drain = (Task)Invoke(form, "CancelAndDrainAsync")!;
                Invoke(form, "EnsureFacetsPanel");
                SetField(form, "_facetsVisible", true);
                Invoke(form, "BuildFacets");
                Assert.Null(Field<CancellationTokenSource?>(form, "_facetCts"));
                release.SetResult();
                PumpUntil(task);
                PumpUntil(drain);
                Assert.NotEqual(uiThread, workerThread);
                Assert.Equal(new long[] { 1, 3 }, sourceNumbers);
                Assert.Equal(new[] { "keep1", "keep2" }, values);
                Assert.Equal(new string?[] { null, null }, unprojected); // only column 0 was requested
                Assert.False(Field<bool>(form, "_busy"));
            }
            finally { release.TrySetResult(); }
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void Chart_worker_keeps_UI_responsive_and_applies_latest_request_after_failure() => OnSta(() =>
    {
        var context = new ChartContext { Rows = new() { new[] { "1" }, new[] { "2" } },
            ColumnNames = new[] { "value" }, Summaries = Array.Empty<ColumnSummary>(), Palette = ThemePalette.Light };
        using var form = new ChartForm(context, ChartKind.Histogram, new[] { 0 });
        _ = form.Handle;
        PumpUntil(Field<Task>(form, "_modelTask"));
        using var entered = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<PlotModel?> blocked = () =>
        {
            entered.Set();
            release.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("Superseded calculation failed");
        };
        SetField(form, "_pendingModel", blocked);
        var task = (Task)Invoke(form, "BuildModelsAsync")!;
        SetField(form, "_modelTask", task);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            bool heartbeat = false;
            form.BeginInvoke(() => heartbeat = true);
            Application.DoEvents();
            Assert.True(heartbeat);
            Invoke(form, "RebuildModel");
            context.Rows = Enumerable.Range(1, 7).Select(i => new[] { i.ToString() }).ToList();
            Invoke(form, "RebuildModel");
            Assert.Same(task, Field<Task>(form, "_modelTask"));
            release.SetResult();
            PumpUntil(task);
            Assert.Equal("N=7", Field<PlotControl>(form, "_plot").Model!.RenderNote);
            Assert.True(Field<PlotControl>(form, "_plot").Enabled);
        }
        finally { release.TrySetResult(); }
    });

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void PumpUntil(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "Pivot worker did not complete in 30 seconds");
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntilUi(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Assert.True(completed(), "UI continuation did not finish");
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "UI test did not complete in 45 seconds");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
