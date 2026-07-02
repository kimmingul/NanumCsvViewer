using NanumCsvViewer.Charting;

namespace NanumCsvViewer.Tests
{
    // 시각화 계산 엔진(이슈 #19). 참조값은 scipy 1.17.1 / numpy 2.4.4 (명시 공식) 대조.
    public class PlotMathTests
    {
        // ---- OLS 회귀 ----

        [Fact]
        public void Ols_matches_scipy_linregress()
        {
            // scipy: slope=2.0054545, intercept=0.02, r=0.9997250, r²=0.9994501
            var xs = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var ys = new double[] { 2.1, 3.9, 6.2, 8.1, 9.8, 12.2, 13.9, 16.1, 18.0, 20.2 };
            var f = PlotMath.OlsFit(xs, ys)!;
            Assert.Equal(2.005454545454546, f.Slope, 9);
            Assert.Equal(0.02, f.Intercept, 5);
            Assert.Equal(0.999725032, f.R, 6);
            Assert.Equal(0.999450139, f.RSquared, 6);
            Assert.Equal(10, f.Count);
        }

        [Fact]
        public void Ols_negative_slope()
        {
            // scipy: slope=-0.8, intercept=5.4, r=-0.8
            var f = PlotMath.OlsFit(new double[] { 1, 2, 3, 4, 5 }, new double[] { 5, 3, 4, 1, 2 })!;
            Assert.Equal(-0.8, f.Slope, 6);
            Assert.Equal(5.4, f.Intercept, 6);
            Assert.Equal(-0.8, f.R, 6);
        }

        [Fact]
        public void Ols_guards_degenerate_input()
        {
            Assert.Null(PlotMath.OlsFit(new double[] { 1 }, new double[] { 2 }));
            Assert.Null(PlotMath.OlsFit(new double[] { 3, 3, 3 }, new double[] { 1, 2, 3 })); // x 분산 0
            // NaN 쌍은 건너뛰고 유효 쌍으로만 적합
            var f = PlotMath.OlsFit(new[] { 1.0, double.NaN, 2, 3 }, new[] { 2.0, 9, 4, 6 })!;
            Assert.Equal(2.0, f.Slope, 6);
            Assert.Equal(3, f.Count);
        }

        // ---- KDE ----

        [Fact]
        public void Silverman_bandwidth_matches_textbook_formula()
        {
            // 명시 공식: 0.9·min(sd=1.7126977, IQR/1.34=1.3059701)·10^(-0.2) = 0.7416103
            var d = new double[] { 2, 3, 3, 4, 4, 4, 5, 5, 6, 8 };
            Assert.Equal(0.741610, PlotMath.SilvermanBandwidth(d), 5);
        }

        [Fact]
        public void Exact_kde_matches_manual_gaussian_sum()
        {
            // 명시 공식 수작업 계산(파이썬): f(2)=0.1014211, f(4)=0.2509071, f(6)=0.1028383
            var d = new double[] { 2, 3, 3, 4, 4, 4, 5, 5, 6, 8 };
            double h = 0.7416103116091823;
            Assert.Equal(0.101421, PlotMath.KdeAt(2.0, d, h), 5);
            Assert.Equal(0.250907, PlotMath.KdeAt(4.0, d, h), 5);
            Assert.Equal(0.102838, PlotMath.KdeAt(6.0, d, h), 5);
        }

        [Fact]
        public void Binned_kde_approximates_exact_within_tolerance()
        {
            var d = new double[] { 2, 3, 3, 4, 4, 4, 5, 5, 6, 8 };
            double h = PlotMath.SilvermanBandwidth(d);
            var curve = PlotMath.BinnedKdeCurve(d, h, gridPoints: 256, bins: 512);
            Assert.Equal(256, curve.X.Length);
            // 곡선 전 구간에서 exact 대비 상대오차 2% 이내(피크 기준)
            double peak = 0;
            foreach (double x in curve.X) peak = Math.Max(peak, PlotMath.KdeAt(x, d, h));
            for (int i = 0; i < curve.X.Length; i++)
            {
                double exact = PlotMath.KdeAt(curve.X[i], d, h);
                Assert.True(Math.Abs(curve.Density[i] - exact) <= 0.02 * peak,
                    $"x={curve.X[i]}: binned={curve.Density[i]}, exact={exact}");
            }
            // 밀도 적분 ≈ 1 (사다리꼴)
            double integral = 0;
            for (int i = 1; i < curve.X.Length; i++)
                integral += (curve.Density[i] + curve.Density[i - 1]) / 2 * (curve.X[i] - curve.X[i - 1]);
            Assert.Equal(1.0, integral, 2);
        }

        [Fact]
        public void Normal_pdf_known_values()
        {
            // scipy: norm.pdf(3,5,2)=0.12098536, pdf(5,5,2)=0.19947114
            Assert.Equal(0.1209853623, PlotMath.NormalPdf(3, 5, 2), 8);
            Assert.Equal(0.1994711402, PlotMath.NormalPdf(5, 5, 2), 8);
            Assert.Equal(0.1209853623, PlotMath.NormalPdf(7, 5, 2), 8);
        }

        // ---- Q-Q ----

        [Fact]
        public void Qq_positions_use_blom_and_normal_quantile()
        {
            // n=5, (i-0.375)/(5.25): z = ∓1.1797611, ∓0.4972006, 0
            var sorted = new double[] { 10, 20, 30, 40, 50 };
            var qq = PlotMath.QqNormalPoints(sorted);
            Assert.Equal(5, qq.Theoretical.Length);
            Assert.Equal(-1.179761, qq.Theoretical[0], 5);
            Assert.Equal(-0.497201, qq.Theoretical[1], 5);
            Assert.Equal(0.0, qq.Theoretical[2], 6);
            Assert.Equal(0.497201, qq.Theoretical[3], 5);
            Assert.Equal(1.179761, qq.Theoretical[4], 5);
            Assert.Equal(sorted, qq.Sample);
        }

        [Fact]
        public void Qq_thinning_is_deterministic_and_keeps_tails()
        {
            var sorted = new double[10_000];
            for (int i = 0; i < sorted.Length; i++) sorted[i] = i;
            var qq = PlotMath.QqNormalPoints(sorted, maxPoints: 100);
            Assert.Equal(100, qq.Sample.Length);
            Assert.Equal(0, qq.Sample[0]);        // 최소값 보존
            Assert.Equal(9999, qq.Sample[^1]);    // 최대값 보존(꼬리)
            var again = PlotMath.QqNormalPoints(sorted, maxPoints: 100);
            Assert.Equal(qq.Sample, again.Sample); // 결정적(무작위 아님)
        }

        // ---- 박스플롯 ----

        [Fact]
        public void Box_stats_match_reference()
        {
            // numpy: Q1=3.5, med=6, Q3=8.5, 수염=[1,10], 이상치=[25]
            var b = PlotMath.ComputeBoxStats(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 25 })!;
            Assert.Equal(3.5, b.Q1, 6);
            Assert.Equal(6.0, b.Median, 6);
            Assert.Equal(8.5, b.Q3, 6);
            Assert.Equal(1.0, b.WhiskerLow, 6);
            Assert.Equal(10.0, b.WhiskerHigh, 6);
            Assert.Equal(new double[] { 25 }, b.Outliers);
            Assert.Equal(11, b.Count);
        }

        [Fact]
        public void Box_stats_guards()
        {
            Assert.Null(PlotMath.ComputeBoxStats(Array.Empty<double>()));
            var constant = PlotMath.ComputeBoxStats(new double[] { 7, 7, 7 })!;
            Assert.Equal(7, constant.Median, 6);
            Assert.Empty(constant.Outliers);
        }

        // ---- 빈 수 ----

        [Fact]
        public void Freedman_diaconis_matches_manual()
        {
            // iqr=1.75, width=2·1.75·10^(-1/3)=1.6245561, range=9 → ceil(5.54)=6
            var d = new double[] { 1, 2, 2, 3, 3, 3, 4, 4, 5, 10 };
            Assert.Equal(6, PlotMath.FreedmanDiaconisBins(d));
        }

        [Fact]
        public void Freedman_diaconis_clamps_and_falls_back()
        {
            Assert.Equal(5, PlotMath.FreedmanDiaconisBins(new double[] { 1, 2 }));            // 하한 클램프
            var constant = new double[] { 5, 5, 5, 5, 5, 5, 5, 5 };
            Assert.InRange(PlotMath.FreedmanDiaconisBins(constant), 5, 100);                  // IQR 0 → Sturges 폴백
        }

        // ---- 상관 행렬 ----

        [Fact]
        public void Pearson_matrix_matches_numpy_corrcoef()
        {
            // col0=1..5, col1=2·col0, col2=[5,3,4,1,2], col3=상수
            // numpy: r01=1, r02=-0.8, r12=-0.8, 상수열은 NaN
            var cols = new List<double[]>
            {
                new double[] { 1, 2, 3, 4, 5 },
                new double[] { 2, 4, 6, 8, 10 },
                new double[] { 5, 3, 4, 1, 2 },
                new double[] { 1, 1, 1, 1, 1 },
            };
            var m = PlotMath.PearsonMatrix(cols);
            Assert.Equal(1.0, m.R[0, 1], 6);
            Assert.Equal(-0.8, m.R[0, 2], 6);
            Assert.Equal(-0.8, m.R[1, 2], 6);
            Assert.Equal(1.0, m.R[0, 0], 6);
            Assert.True(double.IsNaN(m.R[0, 3])); // 상수 컬럼 가드
            Assert.True(double.IsNaN(m.R[3, 3]) || m.R[3, 3] == 1); // 대각 정의(관측 존재 시 1)
            Assert.Equal(m.R[2, 0], m.R[0, 2], 10); // 대칭
            Assert.Equal(5, m.N[0, 1]);
        }

        [Fact]
        public void Pearson_matrix_pairwise_complete_skips_nan()
        {
            var cols = new List<double[]>
            {
                new double[] { 1, 2, 3, double.NaN, 5 },
                new double[] { 2, 4, 6, 8, 10 },
            };
            var m = PlotMath.PearsonMatrix(cols);
            Assert.Equal(4, m.N[0, 1]);       // NaN 행 제외
            Assert.Equal(1.0, m.R[0, 1], 6);  // 나머지는 완전 비례
        }

        [Fact]
        public void Pearson_matrix_p_matches_correlation_test()
        {
            // 단일 쌍 p는 기존 Correlation()과 일치해야 함(같은 t 변환·분포)
            var xs = new double[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var ys = new double[] { 2.1, 3.9, 6.2, 8.1, 9.8, 12.2, 13.9, 16.1 };
            var pairs = xs.Zip(ys).Select(t => (t.First, t.Second)).ToList();
            var reference = NanumCsvViewer.Csv.CsvStatistics.Correlation(pairs, NanumCsvViewer.Csv.CorrelationMethod.Pearson);
            var m = PlotMath.PearsonMatrix(new List<double[]> { xs, ys });
            Assert.Equal(reference.Coefficient, m.R[0, 1], 6);
            Assert.Equal(reference.PValue, m.P[0, 1], 6);
        }

        // ---- 이동평균 ----

        [Fact]
        public void Trailing_moving_average_matches_manual()
        {
            // 수작업: [1, 1.5, 2, 3, 4, 5]
            var ma = PlotMath.TrailingMovingAverage(new double[] { 1, 2, 3, 4, 5, 6 }, 3);
            Assert.Equal(new double[] { 1, 1.5, 2, 3, 4, 5 }, ma);
        }

        // ---- 밀도 격자 ----

        [Fact]
        public void Density_grid_counts_all_points_exactly()
        {
            // 무작위 샘플이 아닌 전수 카운트임을 검증(설계 원칙)
            var xs = new List<double>();
            var ys = new List<double>();
            for (int i = 0; i < 1000; i++) { xs.Add(i % 10); ys.Add(i % 7); }
            var g = PlotMath.ComputeDensityGrid(xs, ys, cols: 10, rows: 7)!;
            Assert.Equal(1000, g.Total);
            long sum = 0;
            for (int r = 0; r < 7; r++) for (int c = 0; c < 10; c++) sum += g.Counts[r, c];
            Assert.Equal(1000, sum);
            Assert.True(g.MaxCount >= 14); // 1000/(10·7)≈14.3 균등 분포
        }

        [Fact]
        public void Density_grid_rebins_to_zoom_range()
        {
            var xs = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var ys = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var g = PlotMath.ComputeDensityGrid(xs, ys, 4, 4, xMin: 3, xMax: 6, yMin: 3, yMax: 6)!;
            Assert.Equal(4, g.Total); // 줌 범위 [3,6]에는 3,4,5,6만
        }

        [Fact]
        public void Density_grid_null_when_empty()
        {
            Assert.Null(PlotMath.ComputeDensityGrid(Array.Empty<double>(), Array.Empty<double>(), 4, 4));
        }

        // ---- 파레토 ----

        [Fact]
        public void Pareto_cumulative_percent()
        {
            var cum = PlotMath.ParetoCumulativePercent(new[] { 50, 30, 20 });
            Assert.Equal(50.0, cum[0], 6);
            Assert.Equal(80.0, cum[1], 6);
            Assert.Equal(100.0, cum[2], 6);
        }
    }
}
