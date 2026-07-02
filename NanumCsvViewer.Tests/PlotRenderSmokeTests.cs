using System.Drawing;
using NanumCsvViewer.Charting;

namespace NanumCsvViewer.Tests
{
    // 렌더러 스모크(이슈 #19): 7종 차트 모델을 오프스크린 비트맵으로 렌더 — 예외 없음 + 실제 픽셀이 그려짐.
    // (GDI+ 픽셀 정확성은 검증 불가 영역이므로 '경로 통과 + 비어 있지 않음'만 잠근다.)
    public class PlotRenderSmokeTests
    {
        private static void AssertRenders(PlotModel? model, ThemePalette? palette = null)
        {
            Assert.NotNull(model);
            using var control = new PlotControl { Size = new Size(640, 480) };
            control.SetModel(model, palette ?? ThemePalette.Light);
            using var bmp = control.RenderBitmap(1);
            Assert.True(bmp.Width >= 640 && bmp.Height >= 480);

            // 배경(Surface) 이외 픽셀이 존재해야 함 — 8픽셀 간격 샘플링
            var surface = (palette ?? ThemePalette.Light).Surface;
            bool nonBackground = false;
            for (int y = 0; y < bmp.Height && !nonBackground; y += 8)
                for (int x = 0; x < bmp.Width; x += 8)
                    if (bmp.GetPixel(x, y).ToArgb() != surface.ToArgb()) { nonBackground = true; break; }
            Assert.True(nonBackground, "렌더 결과가 빈 화면");
        }

        private static List<string[]> NumericRows(IEnumerable<double> values)
            => values.Select(v => new[] { v.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToList();

        [Fact]
        public void Histogram_renders()
            => AssertRenders(ChartBuilders.Histogram(
                NumericRows(Enumerable.Range(0, 500).Select(i => Math.Sin(i) * 3 + 10.0)),
                0, "v", new ChartBuilders.HistogramOptions()));

        [Fact]
        public void Histogram_renders_dark_theme()
            => AssertRenders(ChartBuilders.Histogram(
                NumericRows(Enumerable.Range(0, 200).Select(i => (double)(i % 17))),
                0, "v", new ChartBuilders.HistogramOptions()), ThemePalette.Dark);

        [Fact]
        public void Boxplot_renders()
        {
            var rows = new List<string[]>();
            var rnd = new Random(3);
            foreach (string g in new[] { "a", "b", "c" })
                for (int i = 0; i < 40; i++)
                    rows.Add(new[] { g, (rnd.NextDouble() * 10 + g[0]).ToString("0.###") });
            AssertRenders(ChartBuilders.BoxPlot(rows, 1, "값", 0, "그룹"));
        }

        [Fact]
        public void Scatter_points_mode_renders()
        {
            var rows = new List<string[]>();
            var rnd = new Random(5);
            for (int i = 0; i < 300; i++)
            {
                double x = rnd.NextDouble() * 10;
                rows.Add(new[] { x.ToString("0.###"), (2 * x + rnd.NextDouble()).ToString("0.###") });
            }
            AssertRenders(ChartBuilders.Scatter(rows, 0, "x", 1, "y", new ChartBuilders.ScatterOptions()));
        }

        [Fact]
        public void Scatter_density_mode_renders()
        {
            var rows = new List<string[]>();
            var rnd = new Random(11);
            for (int i = 0; i < 3000; i++)
            {
                double x = rnd.NextDouble();
                rows.Add(new[] { x.ToString("0.####"), (x + rnd.NextDouble() * 0.2).ToString("0.####") });
            }
            AssertRenders(ChartBuilders.Scatter(rows, 0, "x", 1, "y",
                new ChartBuilders.ScatterOptions(DensityThreshold: 1000))); // 밀도 격자 렌더 경로
        }

        [Fact]
        public void Correlation_heatmap_renders()
        {
            var rows = new List<string[]>();
            var rnd = new Random(9);
            for (int i = 0; i < 100; i++)
            {
                double a = rnd.NextDouble();
                rows.Add(new[] { a.ToString("0.###"), (a * 2).ToString("0.###"), rnd.NextDouble().ToString("0.###") });
            }
            AssertRenders(ChartBuilders.CorrelationHeatmap(rows, new[] { 0, 1, 2 }, new[] { "a", "b", "c" }));
        }

        [Fact]
        public void Qq_plot_renders()
            => AssertRenders(ChartBuilders.QqPlot(
                NumericRows(Enumerable.Range(1, 80).Select(i => (double)i * i)), 0, "v"));

        [Fact]
        public void Timeseries_renders()
        {
            var rows = new List<string[]>();
            for (int mth = 1; mth <= 12; mth++)
                for (int i = 0; i <= mth; i++)
                    rows.Add(new[] { $"2024-{mth:00}-15" });
            AssertRenders(ChartBuilders.TimeSeries(rows, 0, "날짜", null,
                new ChartBuilders.TimeSeriesOptions(NanumCsvViewer.Csv.DateBinPeriod.Month, MovingAverageWindow: 3)));
        }

        [Fact]
        public void Pareto_renders()
        {
            var rows = new List<string[]>();
            foreach (var (v, n) in new[] { ("A", 40), ("B", 25), ("C", 15), ("D", 10) })
                for (int i = 0; i < n; i++) rows.Add(new[] { v });
            AssertRenders(ChartBuilders.Pareto(rows, 0, "cat"));
        }
    }
}
