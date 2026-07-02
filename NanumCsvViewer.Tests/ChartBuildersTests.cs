using NanumCsvViewer.Charting;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 차트 빌더(이슈 #19): 행 → PlotModel 변환·통계 배지·대용량 모드 전환·상한 정책 검증.
    public class ChartBuildersTests
    {
        private static List<string[]> Rows(params string[][] rows) => rows.ToList();

        private static List<string[]> NumericRows(IEnumerable<double> values)
            => values.Select(v => new[] { v.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList();

        // ---- 히스토그램 ----

        [Fact]
        public void Histogram_builds_bars_overlays_and_sw_badge()
        {
            var rows = NumericRows(new double[] { 2, 3, 3, 4, 4, 4, 5, 5, 6, 8 });
            var m = ChartBuilders.Histogram(rows, 0, "v", new ChartBuilders.HistogramOptions(Bins: 5))!;

            var bars = m.Series.Single(s => s.Kind == PlotSeriesKind.Bars);
            Assert.Equal(5, bars.BarHeight.Length);
            Assert.Equal(10, bars.BarHeight.Sum()); // 전 빈 합 = n

            Assert.Contains(m.Series, s => s.Name == "Normal" && s.Kind == PlotSeriesKind.Line);
            Assert.Contains(m.Series, s => s.Name == "KDE" && s.Kind == PlotSeriesKind.Line);
            Assert.Contains(m.Badges, b => b.Label.StartsWith("SW W="));       // n≤5000 → Shapiro-Wilk 배지
            Assert.Contains(m.ReferenceLines, r => r.Label.StartsWith("mean"));
            Assert.True(m.AllowZoom);
        }

        [Fact]
        public void Histogram_null_when_no_numeric_values()
        {
            Assert.Null(ChartBuilders.Histogram(Rows(new[] { "a" }, new[] { "b" }), 0, "v", new ChartBuilders.HistogramOptions()));
        }

        [Fact]
        public void Histogram_large_n_swaps_sw_badge_for_skewness()
        {
            var values = new List<double>();
            var rnd = new Random(42);
            for (int i = 0; i < 6000; i++) values.Add(rnd.NextDouble() * 10);
            var m = ChartBuilders.Histogram(NumericRows(values), 0, "v", new ChartBuilders.HistogramOptions())!;
            Assert.DoesNotContain(m.Badges, b => b.Label.StartsWith("SW"));    // Royston 신뢰범위 초과
            Assert.Contains(m.Badges, b => b.Label.StartsWith("skew="));
        }

        // ---- 박스플롯 ----

        [Fact]
        public void Boxplot_three_groups_gets_anova_badge()
        {
            var rows = new List<string[]>();
            foreach (double v in new double[] { 23, 25, 18, 29, 22 }) rows.Add(new[] { "a", v.ToString() });
            foreach (double v in new double[] { 31, 28, 35, 30, 33 }) rows.Add(new[] { "b", v.ToString() });
            foreach (double v in new double[] { 45, 42, 39, 48, 44 }) rows.Add(new[] { "c", v.ToString() });
            var m = ChartBuilders.BoxPlot(rows, 1, "값", 0, "그룹")!;

            var boxes = m.Series.Single().Boxes;
            Assert.Equal(3, boxes.Count);
            Assert.Contains(m.Badges, b => b.Label.StartsWith("ANOVA F=44.48")); // scipy: F=44.4756
        }

        [Fact]
        public void Boxplot_two_groups_gets_welch_badge()
        {
            var rows = new List<string[]>();
            foreach (double v in new double[] { 1, 2, 3, 4, 5 }) rows.Add(new[] { "a", v.ToString() });
            foreach (double v in new double[] { 21, 22, 23, 24, 25 }) rows.Add(new[] { "b", v.ToString() });
            var m = ChartBuilders.BoxPlot(rows, 1, "값", 0, "그룹")!;
            Assert.Contains(m.Badges, b => b.Label.StartsWith("t="));
        }

        [Fact]
        public void Boxplot_caps_groups_and_reports_omitted()
        {
            var rows = new List<string[]>();
            for (int g = 0; g < 60; g++)
                for (int i = 0; i < 3; i++)
                    rows.Add(new[] { $"g{g:00}", (g + i).ToString() });
            var m = ChartBuilders.BoxPlot(rows, 1, "값", 0, "그룹", maxGroups: 50)!;
            Assert.Equal(50, m.Series.Single().Boxes.Count);
            Assert.Contains(m.Badges, b => b.Label.Contains("omitted"));
        }

        // ---- 산점도 ----

        [Fact]
        public void Scatter_small_n_uses_points_with_regression_and_r_badge()
        {
            var rows = new List<string[]>();
            var xs = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var ys = new double[] { 2.1, 3.9, 6.2, 8.1, 9.8, 12.2, 13.9, 16.1, 18.0, 20.2 };
            for (int i = 0; i < xs.Length; i++) rows.Add(new[] { xs[i].ToString(), ys[i].ToString() });
            var m = ChartBuilders.Scatter(rows, 0, "x", 1, "y", new ChartBuilders.ScatterOptions())!;

            Assert.Contains(m.Series, s => s.Kind == PlotSeriesKind.Points);
            Assert.Contains(m.Series, s => s.Name == "OLS");
            Assert.Contains(m.Badges, b => b.Label.StartsWith("r=1.000") || b.Label.StartsWith("r=0.999")); // scipy r=0.99973
            Assert.Contains(m.Badges, b => b.Label.Contains("R²=0.999"));
        }

        [Fact]
        public void Scatter_large_n_switches_to_density_with_full_stats()
        {
            var rows = new List<string[]>();
            var rnd = new Random(7);
            for (int i = 0; i < 1200; i++)
            {
                double x = rnd.NextDouble();
                rows.Add(new[] { x.ToString("0.####"), (2 * x + rnd.NextDouble() * 0.1).ToString("0.####") });
            }
            var m = ChartBuilders.Scatter(rows, 0, "x", 1, "y", new ChartBuilders.ScatterOptions(DensityThreshold: 1000))!;
            Assert.Contains(m.Series, s => s.Kind == PlotSeriesKind.Density);
            Assert.Contains(m.Badges, b => b.Label.StartsWith("r="));           // 통계는 여전히 전수
            Assert.Contains("밀도", m.RenderNote);
            Assert.Contains("1,200", m.RenderNote);                              // 전수 N 명시
        }

        // ---- 상관 히트맵 ----

        [Fact]
        public void Heatmap_builds_symmetric_matrix_with_axis_names()
        {
            var rows = new List<string[]>();
            for (int i = 1; i <= 5; i++)
                rows.Add(new[] { i.ToString(), (2 * i).ToString(), (6 - i).ToString() });
            var m = ChartBuilders.CorrelationHeatmap(rows, new[] { 0, 1, 2 }, new[] { "a", "b", "c" })!;
            var cells = m.Series.Single().Cells!;
            Assert.Equal(1.0, cells[0, 1], 6);
            Assert.Equal(-1.0, cells[0, 2], 6);
            Assert.Equal(new[] { "a", "b", "c" }, m.XAxis.Categories);
            Assert.NotNull(m.Series.Single().CellsP);
        }

        [Fact]
        public void Heatmap_needs_two_columns()
        {
            var rows = Rows(new[] { "1" }, new[] { "2" });
            Assert.Null(ChartBuilders.CorrelationHeatmap(rows, new[] { 0 }, new[] { "a" }));
        }

        // ---- Q-Q ----

        [Fact]
        public void Qq_builds_points_reference_line_and_sw_badge()
        {
            var rows = NumericRows(new double[] { 148, 154, 158, 160, 161, 162, 166, 170, 182, 195, 236 });
            var m = ChartBuilders.QqPlot(rows, 0, "v")!;
            Assert.Contains(m.Series, s => s.Kind == PlotSeriesKind.Points && s.Xs.Length == 11);
            Assert.Contains(m.Series, s => s.Name == "Normal ref" && s.Dashed);
            Assert.Contains(m.Badges, b => b.Label.StartsWith("SW W=0.789")); // scipy W=0.7888
        }

        [Fact]
        public void Qq_guards_constant_column()
        {
            Assert.Null(ChartBuilders.QqPlot(NumericRows(new double[] { 5, 5, 5, 5 }), 0, "v"));
        }

        // ---- 시계열 ----

        [Fact]
        public void Timeseries_bins_by_month_with_moving_average()
        {
            var rows = new List<string[]>();
            for (int mth = 1; mth <= 6; mth++)
                for (int i = 0; i < mth; i++)         // 1월 1건, 2월 2건 … 6월 6건
                    rows.Add(new[] { $"2024-{mth:00}-10" });
            var m = ChartBuilders.TimeSeries(rows, 0, "날짜", null,
                new ChartBuilders.TimeSeriesOptions(DateBinPeriod.Month, MovingAverageWindow: 3))!;

            var main = m.Series.First(s => !s.Name.StartsWith("MA"));
            Assert.Equal(new double[] { 1, 2, 3, 4, 5, 6 }, main.Ys);
            var ma = m.Series.Single(s => s.Name == "MA(3)");
            Assert.Equal(new double[] { 1, 1.5, 2, 3, 4, 5 }, ma.Ys);           // 수작업 참조값
            Assert.Equal(6, m.XAxis.TickLabels!.Count);
        }

        // ---- 파레토 ----

        [Fact]
        public void Pareto_orders_by_count_with_cumulative_percent()
        {
            var rows = new List<string[]>();
            for (int i = 0; i < 50; i++) rows.Add(new[] { "A" });
            for (int i = 0; i < 30; i++) rows.Add(new[] { "B" });
            for (int i = 0; i < 20; i++) rows.Add(new[] { "C" });
            var m = ChartBuilders.Pareto(rows, 0, "cat")!;

            Assert.Equal(new[] { "A", "B", "C" }, m.XAxis.Categories);
            var cum = m.Series.Single(s => s.UseSecondaryAxis);
            Assert.Equal(new double[] { 50, 80, 100 }, cum.Ys);
            Assert.NotNull(m.Y2Axis);
        }

        [Fact]
        public void Pareto_aggregates_tail_into_other()
        {
            var rows = new List<string[]>();
            for (int v = 0; v < 60; v++) rows.Add(new[] { $"v{v:00}" });
            var m = ChartBuilders.Pareto(rows, 0, "cat", topN: 50)!;
            Assert.Equal(51, m.XAxis.Categories.Count);                          // 상위 50 + (기타)
            Assert.Equal("(기타)", m.XAxis.Categories[^1]);
            Assert.Contains(m.Badges, b => b.Label.Contains("기타"));
        }
    }
}
