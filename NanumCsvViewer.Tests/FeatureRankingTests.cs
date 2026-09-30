using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 단변량 필터를 sklearn·scipy·statsmodels와 대조한다.
    // f_regression / r_regression, f_classif, chi2_contingency(correction=False),
    // multipletests(method='fdr_bh').
    public class FeatureRankingTests
    {
        private static readonly double[,] X = new double[,]
        {
            { 2.1, 1.0, 0.4, 8.0 },
            { 2.4, 1.2, 0.3, 7.5 },
            { 2.0, 0.9, 0.5, 8.2 },
            { 5.1, 3.2, 1.1, 2.0 },
            { 5.4, 3.0, 1.4, 1.8 },
            { 4.8, 3.5, 1.0, 2.3 },
            { 1.1, 4.2, 2.5, 5.0 },
            { 1.4, 4.0, 2.8, 4.6 },
            { 0.9, 4.5, 2.2, 5.4 },
            { 3.3, 2.1, 3.6, 3.1 },
            { 3.8, 1.8, 3.2, 3.5 },
            { 3.0, 2.4, 3.9, 2.8 },
        };

        // sklearn f_regression(X, y) / r_regression(X, y), y = [3.2, 3.0, 3.5, 8.1, 8.4, 7.6, 4.2, 4.0, 4.5, 6.1, 6.4, 5.8]
        private static readonly double[] Y = { 3.2, 3.0, 3.5, 8.1, 8.4, 7.6, 4.2, 4.0, 4.5, 6.1, 6.4, 5.8 };
        private static readonly double[] PearsonR = { 0.8890727740558568, 0.2989896809109193, 0.18933044105182645, -0.9199638956869061 };
        private static readonly double[] PearsonF = { 37.721398102939894, 0.9817079033455434, 0.37178725079552616, 55.07602186655059 };
        private static readonly double[] PearsonP = { 0.00010949913610700922, 0.3451458428595983, 0.555632363349756, 2.258651456160415e-05 };

        // sklearn f_classif(X, labels), labels = [0,0,0,1,1,1,2,2,2,1,2,0]
        private static readonly int[] Labels = { 0, 0, 0, 1, 1, 1, 2, 2, 2, 1, 2, 0 };
        private static readonly double[] AnovaFStat = { 9.42350907519449, 6.754750175932451, 1.268685454227199, 7.412326961107419 };
        private static readonly double[] AnovaP = { 0.006202791055825252, 0.016160133532416607, 0.3270472081619622, 0.012516173910344906 };

        [Fact]
        public void Pearson_F_matches_sklearn_f_regression()
        {
            int n = Y.Length, p = X.GetLength(1);
            for (int j = 0; j < p; j++)
            {
                var col = new double[n];
                for (int i = 0; i < n; i++) col[i] = X[i, j];
                var score = FeatureRanking.PearsonF(col, Y);
                Close(PearsonR[j], score.Score);
                Close(PearsonF[j], score.Statistic);
                Close(PearsonP[j], score.PValue, 1e-6, 1e-10);
                Assert.Equal(RankNote.None, score.Note);
                Assert.Equal(1, score.Df1);
                Assert.Equal(n - 2, score.Df2);
            }
        }

        [Fact]
        public void Constant_feature_follows_sklearn_force_finite_for_regression_and_nan_for_anova()
        {
            var col = new double[Y.Length];
            for (int i = 0; i < col.Length; i++) col[i] = 5;
            var reg = FeatureRanking.PearsonF(col, Y);
            Assert.Equal(0, reg.Score);
            Assert.Equal(0, reg.Statistic);
            Assert.Equal(1, reg.PValue);
            Assert.Equal(RankNote.Constant, reg.Note);

            var anova = FeatureRanking.AnovaF(col, Labels, 3);
            Assert.True(double.IsNaN(anova.Statistic));
            Assert.True(double.IsNaN(anova.PValue));
            Assert.Equal(RankNote.Constant, anova.Note);
        }

        [Fact]
        public void Anova_F_matches_sklearn_f_classif()
        {
            int n = Labels.Length, p = X.GetLength(1);
            for (int j = 0; j < p; j++)
            {
                var col = new double[n];
                for (int i = 0; i < n; i++) col[i] = X[i, j];
                var score = FeatureRanking.AnovaF(col, Labels, 3);
                Close(AnovaFStat[j], score.Statistic);
                Close(AnovaP[j], score.PValue, 1e-6, 1e-10);
                Assert.Equal(2, score.Df1);
                Assert.Equal(n - 3, score.Df2);
            }
        }

        [Fact]
        public void Chi_square_matches_scipy_contingency_without_yates()
        {
            // scipy.stats.chi2_contingency(table, correction=False)
            var table = new[,] { { 10, 2, 3 }, { 1, 8, 4 }, { 5, 1, 9 } };
            var score = FeatureRanking.ChiSquare(table);
            Close(19.583041958041953, score.Statistic, 1e-12, 1e-12);
            Close(0.0006035025767408919, score.PValue, 1e-6, 1e-10);
            Assert.Equal(4, score.Df1);
            Assert.Equal(RankNote.LowExpected, score.Note); // 최소 기대빈도 3.33 < 5
            Assert.True(score.MinExpected < 5);

            // 2×2. correction=False → 9.663…, Yates(correction=True) → 7.635… 와 달라야 한다.
            var two = new[,] { { 12, 3 }, { 5, 14 } };
            var plain = FeatureRanking.ChiSquare(two);
            Close(9.663157894736843, plain.Statistic, 1e-12, 1e-12);
            Close(0.0018800017694650532, plain.PValue, 1e-6, 1e-10);
            Assert.Equal(1, plain.Df1);
            Assert.Equal(RankNote.None, plain.Note);
            Assert.NotEqual(7.635087719298245, plain.Statistic);
        }

        [Fact]
        public void Benjamini_Hochberg_matches_statsmodels_fdr_bh_including_monotonicity()
        {
            // statsmodels.stats.multitest.multipletests(p, method='fdr_bh')
            var p = new[] { 0.001, 0.04, 0.03, 0.20, 0.80, 0.04, 0.0001, 1.0 };
            var q = FeatureRanking.BenjaminiHochberg(p);
            Close(new[] { 0.004, 0.064, 0.064, 0.26666666666666666, 0.9142857142857144, 0.064, 0.0008, 1.0 }, q, 1e-12, 1e-12);

            // raw BH는 [0.04, 0.08, 0.0533, 0.20]. 두 번째는 오른쪽 누적 최솟값으로 0.0533까지 내려간다.
            var q2 = FeatureRanking.BenjaminiHochberg(new[] { 0.01, 0.04, 0.04, 0.20 });
            Close(new[] { 0.04, 0.05333333333333334, 0.05333333333333334, 0.2 }, q2, 1e-12, 1e-12);
        }

        [Fact]
        public void Rank_numeric_target_drops_incomplete_rows_and_orders_by_p()
        {
            // 완전한 6행의 f_regression. 마지막 행은 x1 결측이라 제외.
            var rows = new[]
            {
                new[] { "2.1", "1.0", "3.2" },
                new[] { "2.4", "1.2", "3.0" },
                new[] { "5.1", "3.2", "8.1" },
                new[] { "5.4", "3.0", "8.4" },
                new[] { "1.1", "4.2", "4.2" },
                new[] { "1.4", "4.0", "4.0" },
                new[] { "", "1.0", "9.0" },
            };
            var result = FeatureRanking.Rank(rows, new[] { "x1", "x2", "y" }, new[] { 0, 1 }, _ => VariableKind.Numeric, 2, TargetKind.Numeric);
            Assert.Equal(7, result.RowsRead);
            Assert.Equal(6, result.RowsUsed);
            Assert.Equal(1, result.RowsDropped);
            Assert.Equal(new[] { 0, 1 }, result.Ranked.Select(f => f.ColumnIndex).ToArray());
            Close(0.9048681174421341, result.Ranked[0].Score);
            Close(18.073387497320347, result.Ranked[0].Statistic);
            Close(0.0131446372787276, result.Ranked[0].PValue, 1e-6, 1e-10);
            Close(0.0262892745574551, result.Ranked[0].QValue, 1e-6, 1e-10);
            Close(0.4805823280593035, result.Ranked[1].QValue, 1e-6, 1e-10);
            // |r|이 더 작은 특성이 p가 더 작으면 p가 이긴다 — 이 자료는 p와 |r|이 같은 방향이라,
            // 순위 키가 p임을 q가 아닌 컬럼 순서로 확인한다(x1이 x2보다 앞).
            Assert.True(result.Ranked[0].PValue < result.Ranked[1].PValue);
        }

        [Fact]
        public void Rank_categorical_target_uses_anova_and_contingency_chi_square()
        {
            // 완전한 8행. size의 f_classif, color×class의 chi2_contingency(correction=False).
            // 결측 2행은 제외. 순위는 p: size(0.031) 다음 color(0.717).
            var rows = new[]
            {
                new[] { "red", "1.0", "a" },
                new[] { "red", "1.2", "a" },
                new[] { "blue", "3.0", "b" },
                new[] { "blue", "3.1", "b" },
                new[] { "red", "2.0", "b" },
                new[] { "blue", "1.5", "a" },
                new[] { "green", "2.5", "a" },
                new[] { "green", "4.0", "b" },
                new[] { "", "1.0", "a" },
                new[] { "red", "", "a" },
            };
            var result = FeatureRanking.Rank(rows, new[] { "color", "size", "klass" }, new[] { 0, 1 },
                c => c == 1 ? VariableKind.Numeric : VariableKind.Categorical, 2, TargetKind.Categorical);
            Assert.Equal(8, result.RowsUsed);
            Assert.Equal(2, result.RowsDropped);
            Assert.Equal(RankingTest.AnovaF, result.Ranked[0].Test);
            Assert.Equal(1, result.Ranked[0].ColumnIndex);
            Close(7.82247191011235, result.Ranked[0].Statistic);
            Close(0.03129284176775848, result.Ranked[0].PValue, 1e-6, 1e-10);
            Close(0.062585683535517, result.Ranked[0].QValue, 1e-6, 1e-10);

            Assert.Equal(RankingTest.ChiSquare, result.Ranked[1].Test);
            Close(0.6666666666666666, result.Ranked[1].Statistic, 1e-12, 1e-12);
            Close(0.7165313105737892, result.Ranked[1].PValue, 1e-6, 1e-10);
            Close(0.7165313105737892, result.Ranked[1].QValue, 1e-6, 1e-10);
            Assert.Equal(2, result.Ranked[1].Df1);
            Assert.Equal(RankNote.LowExpected, result.Ranked[1].Note);
        }

        [Fact]
        public void Rank_rejects_target_used_as_feature_and_empty_complete_rows()
        {
            var rows = new[] { new[] { "1", "2" }, new[] { "", "3" } };
            var headers = new[] { "x", "y" };
            var ex = Assert.Throws<DesignMatrixException>(() =>
                FeatureRanking.Rank(rows, headers, new[] { 0, 1 }, _ => VariableKind.Numeric, 1, TargetKind.Numeric));
            Assert.Contains("cannot also be a feature", ex.Message);

            var empty = Assert.Throws<DesignMatrixException>(() =>
                FeatureRanking.Rank(new[] { new[] { "", "1" } }, headers, new[] { 0 }, _ => VariableKind.Numeric, 1, TargetKind.Numeric));
            Assert.Contains("No complete rows", empty.Message);
        }

        [Fact]
        public void Rank_orders_tied_p_by_absolute_score_then_column()
        {
            // 같은 |r|(따라서 같은 p)인 두 특성은 컬럼 인덱스로 안정 정렬된다.
            // 세 번째는 |r|이 더 작아도 p가 더 작으면 앞에 오지 못한다 — 여기선 복사본이라 p가 같다.
            var rows = new[]
            {
                new[] { "1", "1", "10", "0" },
                new[] { "2", "2", "0", "1" },
                new[] { "3", "3", "1", "2" },
                new[] { "4", "4", "0", "3" },
                new[] { "5", "5", "1", "4" },
            };
            var result = FeatureRanking.Rank(rows, new[] { "copyA", "copyB", "weak", "y" }, new[] { 0, 1, 2 },
                _ => VariableKind.Numeric, 3, TargetKind.Numeric);
            Assert.Equal(0, result.Ranked[0].ColumnIndex);
            Assert.Equal(1, result.Ranked[1].ColumnIndex);
            Assert.Equal(result.Ranked[0].PValue, result.Ranked[1].PValue);
            Assert.Equal(Math.Abs(result.Ranked[0].Score), Math.Abs(result.Ranked[1].Score));
            Assert.True(result.Ranked[0].PValue < result.Ranked[2].PValue);
        }

        private static void Close(double[] expected, double[] actual, double rel, double abs)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Close(expected[i], actual[i], rel, abs);
        }

        private static void Close(double expected, double actual, double rel = 1e-8, double abs = 1e-8)
        {
            double tol = Math.Max(abs, rel * Math.Max(1, Math.Max(Math.Abs(expected), Math.Abs(actual))));
            // 절대 하한도 둔다. p값처럼 작은 수는 abs로 본다.
            tol = Math.Max(tol, abs);
            Assert.True(Math.Abs(expected - actual) <= Math.Max(abs, rel * Math.Max(Math.Abs(expected), Math.Abs(actual))),
                $"expected {expected}, actual {actual}");
        }
    }
}
