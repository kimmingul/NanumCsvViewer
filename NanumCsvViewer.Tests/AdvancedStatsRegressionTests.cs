using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 고급 통계 코드 리뷰에서 찾은 결함의 회귀 테스트(이슈 #27).
    public class AdvancedStatsRegressionTests
    {
        private static VariableKind Kind(int c) => c == 2 ? VariableKind.Categorical : VariableKind.Numeric;

        [Fact]
        public void Large_predictor_offset_is_not_mistaken_for_aliasing()
        {
            // x가 1,000,000 근처여도 절편과 구별되는 정상 예측변수다: y = x − 1,000,000 을 정확히 적합해야 한다.
            var x = new double[,] { { 1, 1_000_000 }, { 1, 1_000_001 }, { 1, 1_000_002 }, { 1, 1_000_003 } };
            var y = new double[] { 0, 1, 2, 3 };
            var fit = LeastSquares.Fit(x, y);
            Assert.Equal(new[] { false, false }, fit.Aliased);
            Assert.Equal(2, fit.Rank);
            Assert.Equal(1.0, fit.Beta[1], 9);
            Assert.Equal(-1_000_000.0, fit.Beta[0], 3);
            Assert.True(fit.WeightedRss < 1e-12);
        }

        [Fact]
        public void Duplicate_intercept_column_is_aliased_after_centering()
        {
            var x = new double[,] { { 1, 2, 0 }, { 1, 2, 1 }, { 1, 2, 2 }, { 1, 2, 4 } };
            var fit = LeastSquares.Fit(x, new double[] { 1, 3, 5, 9 });
            Assert.Equal(new[] { false, true, false }, fit.Aliased);
            Assert.Equal(1, fit.Beta[0], 9);
            Assert.Equal(2, fit.Beta[2], 9);
        }

        [Fact]
        public void Interaction_without_its_marginal_term_is_rejected_not_refit()
        {
            var rows = new List<string[]>
            {
                new[] { "2", "1", "A" }, new[] { "4", "2", "A" }, new[] { "6", "3", "A" },
                new[] { "3", "1", "B" }, new[] { "6", "2", "B" }, new[] { "9", "3", "B" },
            };
            var headers = new[] { "y", "x", "g" };
            var ex = Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x:C(g)"), Kind));
            Assert.Contains("x*g", ex.Message);

            // x 주효과가 있으면 patsy와 같은 처치 코딩: [Intercept, x, x:g[T.B]]
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x + x:C(g)"), Kind);
            Assert.Equal(new[] { "Intercept", "x", "x:g[T.B]" }, dm.ColumnNames);
            var fit = LeastSquares.Fit(dm.X, dm.Y);
            Assert.Equal(2, fit.Beta[1], 9); // A의 기울기
            Assert.Equal(1, fit.Beta[2], 9); // B − A
        }

        [Fact]
        public void Categorical_interaction_width_is_checked_before_expansion()
        {
            var rows = Enumerable.Range(0, 60)
                .Select(i => new[] { i.ToString(), (i % 30).ToString(), $"g{i % 40}" })
                .ToList();
            var headers = new[] { "y", "a", "g" };
            var ex = Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ C(a)*C(g)"), Kind,
                    new DesignMatrixOptions { MaxColumns = 200 }));
            Assert.Contains("200", ex.Message);
        }

        [Fact]
        public void Auc_does_not_overflow_with_many_positive_rows()
        {
            int n = 100_000;
            var y = new double[n];
            for (int i = 0; i < n; i += 2) y[i] = 1;
            var score = new double[n]; // 전부 동점 → AUC 0.5
            Assert.Equal(0.5, GeneralizedLinearModel.RocAuc(y, score), 12);
        }

        [Fact]
        public void Small_unit_predictor_with_finite_mle_is_not_flagged_as_separation()
        {
            // x = 0 에서 1/4, x = 0.001 에서 3/4 양성 — 유한 MLE(기울기 2000·ln 3), 분리 아님.
            var x = new double[8, 2];
            var y = new double[8];
            for (int i = 0; i < 8; i++) { x[i, 0] = 1; x[i, 1] = i < 4 ? 0 : 0.001; }
            y[0] = 1; y[4] = 1; y[5] = 1; y[6] = 1;
            var fit = GeneralizedLinearModel.Fit(x, y, GlmFamily.Binomial, GlmLink.Logit);
            Assert.DoesNotContain(GlmDiagnostic.Separation, fit.Diagnostics);
            Assert.Equal(2000 * Math.Log(3), fit.Coefficients[1], 4);
        }

        [Fact]
        public void Kruskal_wallis_with_many_groups_skips_dunn_pairs()
        {
            var groups = Enumerable.Range(0, 150).Select(g => (IReadOnlyList<double>)new double[] { g, g + 0.5 }).ToList();
            var r = NonparametricTests.KruskalWallis(groups);
            Assert.Empty(r.Dunn);
            Assert.Equal(150L * 149 / 2, r.Comparisons);
            Assert.False(double.IsNaN(r.H));
        }

        [Fact]
        public void Silhouette_is_undefined_when_every_row_is_its_own_cluster()
        {
            var x = new double[,] { { 0 }, { 5 }, { 10 } };
            var r = KMeansClustering.Fit(x, new KMeansOptions { K = 3, Scaling = ScalingMethod.None });
            Assert.False(r.SilhouetteDefined);
            Assert.True(double.IsNaN(r.Silhouette));
        }

        [Fact]
        public void Large_kmeans_uses_sample_initialization_and_full_lloyd_refinement()
        {
            // 두 개의 떨어진 덩어리 600행. 초기화 표본 100행 → 전체 행 Lloyd로 정확한 분할에 수렴.
            int n = 600;
            var x = new double[n, 2];
            var rng = new Random(5);
            for (int i = 0; i < n; i++)
            {
                double c = i < 300 ? 0 : 20;
                x[i, 0] = c + rng.NextDouble();
                x[i, 1] = c + rng.NextDouble();
            }
            var r = KMeansClustering.Fit(x, new KMeansOptions { K = 2, Scaling = ScalingMethod.None, FullDataLimit = 100, Seed = 3 });
            Assert.Equal(100, r.InitializationSampleRows);
            Assert.True(r.RefinementConverged);
            Assert.True(r.LloydFixedPoint);
            Assert.Equal(new[] { 300, 300 }, r.Clusters.Select(c => c.Size).OrderBy(s => s));
            Assert.All(Enumerable.Range(0, 300), i => Assert.Equal(r.Assignment[0], r.Assignment[i]));
            Assert.All(Enumerable.Range(300, 300), i => Assert.Equal(r.Assignment[300], r.Assignment[i]));
        }
    }
}
