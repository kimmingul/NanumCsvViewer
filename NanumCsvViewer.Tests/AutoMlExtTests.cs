using System.Diagnostics;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// AutoML 확장: 다항 로지스틱 후보(sklearn LogisticRegression multinomial lbfgs 기준, tol=1e-12)와
    /// 큰 예산에서의 SVM(DCD) 후보. 참조 계수·확률은 sklearn 1.7.2로 생성해 하드코딩했다.
    /// </summary>
    public class AutoMlExtTests
    {
        static readonly double[] RefX = { 0.3322, 0.2471, -1.0238, 2.6175, 1.6548, -1.2511, -0.1897, 0.1010, 1.1743, -2.1854, 2.2408, 1.2042, -0.3405, 0.7983, 1.7040, 2.1531, 1.4076, 0.9799, 1.9347, 1.1352, 0.8300, 1.4211, 3.3723, -1.4124, 1.9244, -0.3147, -0.5330, -0.5053, 1.9670, 1.5310, -2.6929, -0.2540, 1.9538, 0.5726, 0.8439, -0.0761, -1.0754, 1.7236, 2.4204, -0.0342, -0.7518, 1.2052, -0.2248, -0.5351, -0.2044, -0.7068, -0.1959, -0.6314, 0.6931, 0.0808, 0.8055, -0.4186, 0.0979, 0.0700, 1.9220, 1.9592, 3.1262, -2.3030, 1.2880, 1.8995, 2.2651, 2.1965, 0.0872, -0.5983, 2.7503, 1.1658, -0.7882, 2.6369, 1.5682, -0.0217, 2.9617, 1.0788, -0.2352, 2.4420, -1.3722, 1.5161, 0.7582, 1.0187, 0.3260, 1.5430, -1.7015, 1.4252, -0.2610, -1.3556, 0.4445, 0.6095, 2.2264, 1.0247, 1.5677, 0.0009, 0.7587, 0.6173, -1.4361, -0.5903, 0.3469, 0.4632, 2.4133, 0.0503, -0.6436, 0.3322, 1.2391, -0.3704, -1.2314, 0.8105, 1.7321, 0.7151, -1.0991, 0.2560, -0.9669, 1.8538, 2.8251, -1.5606, 2.0789, 1.2988, 0.9177, 2.7179, -1.6882, -2.9187, -0.0091, -0.3516, -0.4595, 1.9464, 2.9707, -0.3525, -0.3043, 0.1601, -1.1722, -0.9749, -0.5233, 0.3428, 0.9066, 0.7092, -0.0835, 1.4890, 1.1692 };
        static readonly int[] RefY = { 1, 1, 0, 2, 0, 1, 1, 1, 0, 1, 2, 1, 2, 0, 0, 0, 0, 0, 2, 2, 1, 2, 2, 2, 1, 2, 0, 0, 2, 1, 0, 0, 1, 1, 0, 1, 2, 2, 1, 0, 2, 1, 0, 2, 2 };
        static readonly double[] Coef_1_0 = { -0.2796640984, -0.8243234945, -0.5418894060, 0.5497676523, 0.2009049360, -0.4829620240, -0.2701035539, 0.6234185585, 1.0248514300 };
        static readonly double[] Icpt_1_0 = { 1.2629946467, 0.1906098046, -1.4536044514 };
        static readonly double[] Prob_1_0 = { 0.6386614241, 0.3491602334, 0.0121783425, 0.0613213852, 0.9322540535, 0.0064245614, 0.5469643262, 0.1900718263, 0.2629638475, 0.0797080630, 0.0475431623, 0.8727487748, 0.2142384061, 0.1385366905, 0.6472249035, 0.0796239458, 0.7289710464, 0.1914050079 };
        static readonly int[] Pred_1_0 = { 0, 1, 0, 2, 2, 1, 1, 1, 1, 2, 0, 1, 2, 0, 0, 0, 0, 0, 2, 2, 1, 2, 2, 2, 1, 1, 1, 0, 2, 1, 1, 0, 1, 1, 2, 0, 2, 2, 1, 0, 2, 0, 0, 1, 2 };
        static readonly double[] Coef_0_1 = { -0.1520585834, -0.4660399488, -0.2453723729, 0.3628612042, 0.1431869845, -0.2918433869, -0.2108026208, 0.3228529643, 0.5372157599 };
        static readonly double[] Icpt_0_1 = { 0.6803000825, 0.0104750845, -0.6907751670 };
        static readonly double[] Prob_0_1 = { 0.5330219340, 0.3946151278, 0.0723629382, 0.1423820876, 0.8146677507, 0.0429501617, 0.4620624307, 0.2159735054, 0.3219640638, 0.1679748860, 0.1033249726, 0.7287001415, 0.2865436705, 0.1849147706, 0.5285415590, 0.1679188057, 0.5865982916, 0.2454829027 };
        static readonly int[] Pred_0_1 = { 0, 1, 0, 2, 2, 1, 1, 1, 1, 2, 2, 1, 2, 0, 0, 0, 0, 0, 2, 2, 1, 2, 2, 2, 1, 1, 1, 0, 2, 1, 1, 0, 1, 1, 2, 0, 2, 2, 1, 0, 2, 0, 0, 1, 2 };
        static readonly double[] Coef_10_0 = { -0.3348714982, -0.9499310124, -0.7119160474, 0.5791801155, 0.1956909229, -0.5812359761, -0.2443086174, 0.7542400896, 1.2931520235 };
        static readonly double[] Icpt_10_0 = { 1.5216957518, 0.3546333461, -1.8763290979 };
        static readonly double[] Prob_10_0 = { 0.6682872665, 0.3272081734, 0.0045045601, 0.0492308725, 0.9479198482, 0.0028492793, 0.5701824736, 0.1953248100, 0.2344927164, 0.0640249294, 0.0412279450, 0.8947471256, 0.1798058658, 0.1278438501, 0.6923502841, 0.0592005216, 0.7518548713, 0.1889446071 };
        static readonly int[] Pred_10_0 = { 0, 1, 0, 2, 2, 1, 1, 1, 1, 2, 0, 1, 2, 0, 0, 0, 0, 0, 2, 2, 1, 2, 2, 2, 1, 1, 1, 0, 2, 1, 1, 0, 1, 1, 2, 0, 2, 2, 1, 0, 2, 0, 0, 1, 2 };

        static double[,] RefMatrix()
        {
            var x = new double[RefY.Length, 3];
            for (int i = 0; i < RefY.Length; i++)
                for (int j = 0; j < 3; j++) x[i, j] = RefX[i * 3 + j];
            return x;
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(0.1)]
        [InlineData(10.0)]
        public void Multinomial_matches_sklearn_lbfgs_reference(double c)
        {
            var (coef, icpt, prob, pred) = c switch
            {
                1.0 => (Coef_1_0, Icpt_1_0, Prob_1_0, Pred_1_0),
                0.1 => (Coef_0_1, Icpt_0_1, Prob_0_1, Pred_0_1),
                _ => (Coef_10_0, Icpt_10_0, Prob_10_0, Pred_10_0),
            };
            var x = RefMatrix();
            var model = MultinomialLogistic.Fit(x, RefY, 3, c);
            Assert.True(model.Converged);
            for (int k = 0; k < 3; k++)
            {
                Near(icpt[k], model.Coefficients[k, 0]);
                for (int j = 0; j < 3; j++)
                    Near(coef[k * 3 + j], model.Coefficients[k, j + 1]);
            }
            var p = model.PredictProbabilities(x);
            for (int i = 0; i < 6; i++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                {
                    Near(prob[i * 3 + k], p[i, k]);
                    sum += p[i, k];
                }
                Assert.Equal(1.0, sum, 12);
            }
            Assert.Equal(pred, model.PredictClasses(x));
        }

        [Fact]
        public void Multinomial_gives_absent_classes_zero_probability()
        {
            var x = RefMatrix();
            var y = RefY.Select(v => v == 2 ? 3 : v).ToArray(); // class 2 has no rows among 4 classes
            var model = MultinomialLogistic.Fit(x, y, 4, 1.0);
            Assert.Equal(new[] { true, true, false, true }, model.ClassPresent);
            var p = model.PredictProbabilities(x);
            for (int i = 0; i < p.GetLength(0); i++)
            {
                Assert.Equal(0.0, p[i, 2]);
                Assert.Equal(1.0, p[i, 0] + p[i, 1] + p[i, 3], 12);
            }
            Assert.DoesNotContain(2, model.PredictClasses(x));
            // 존재하는 3개 클래스에서의 적합은 같은 데이터의 3클래스 적합과 같다.
            var direct = MultinomialLogistic.Fit(x, RefY, 3, 1.0);
            for (int j = 0; j < 4; j++)
            {
                Assert.Equal(direct.Coefficients[0, j], model.Coefficients[0, j], 9);
                Assert.Equal(direct.Coefficients[1, j], model.Coefficients[1, j], 9);
                Assert.Equal(direct.Coefficients[2, j], model.Coefficients[3, j], 9);
            }
        }

        [Fact]
        public void Multinomial_rejects_invalid_input()
        {
            var x = RefMatrix();
            Assert.Throws<DesignMatrixException>(() => MultinomialLogistic.Fit(x, new int[RefY.Length], 3, 1.0));
            Assert.Throws<DesignMatrixException>(() => MultinomialLogistic.Fit(x, RefY, 3, 0.0));
            Assert.Throws<DesignMatrixException>(() => MultinomialLogistic.Fit(x, RefY, 3, double.PositiveInfinity));
            Assert.Throws<DesignMatrixException>(() => MultinomialLogistic.Fit(x, RefY.Select(v => v + 1).ToArray(), 3, 1.0));
            var bad = RefMatrix();
            bad[3, 1] = double.NaN;
            Assert.Throws<DesignMatrixException>(() => MultinomialLogistic.Fit(bad, RefY, 3, 1.0));
        }

        [Fact]
        public void Multinomial_fit_is_bitwise_deterministic_across_parallel_chunks()
        {
            var (x, y) = Blobs(7000, 4, 5, seed: 3, sep: 1.0);
            var a = MultinomialLogistic.Fit(x, y, 4, 1.0);
            var b = MultinomialLogistic.Fit(x, y, 4, 1.0);
            Assert.Equal(a.Iterations, b.Iterations);
            for (int k = 0; k < 4; k++)
                for (int j = 0; j < 6; j++)
                    Assert.Equal(a.Coefficients[k, j], b.Coefficients[k, j]);
        }

        [Fact]
        public void Multinomial_fit_honours_cancellation()
        {
            var (x, y) = Blobs(120_000, 5, 20, seed: 4, sep: 0.4);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(150);
            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => MultinomialLogistic.Fit(x, y, 5, 1.0, cts.Token));
            Assert.True(sw.Elapsed.TotalSeconds < 20, $"cancellation took {sw.Elapsed.TotalSeconds:F1}s");
        }

        [Fact]
        public void AutoMl_three_classes_lists_multinomial_and_replaces_binary_logistic()
        {
            var (x, y) = Blobs(150, 3, 2, seed: 5, sep: 2.5);
            var report = AutoMl.Search(x, y, 3, Opt(seed: 2, budget: 0));
            Assert.Equal(new[] { "Multinomial-C1.0", "Multinomial-C0.1" }, report.Grid.Take(2));
            Assert.DoesNotContain("Logistic", report.Grid);
            Assert.Contains(AutoMlSkip.LogisticNeedsBinary, report.Skipped);
            Assert.Contains("SVM-linear", report.Grid);
            Assert.Equal(AutoMlSvmMode.ExactSmo, report.SvmMode);
            Assert.Null(report.SvmFitRowCap);
        }

        [Fact]
        public void AutoMl_binary_target_keeps_binary_logistic_and_has_no_multinomial()
        {
            var (x, y) = Blobs(120, 2, 2, seed: 6, sep: 2.0);
            var report = AutoMl.Search(x, y, 2, Opt(seed: 2, budget: 0));
            Assert.Equal("Logistic", report.Grid[0]);
            Assert.DoesNotContain(report.Grid, g => g.StartsWith("Multinomial", StringComparison.Ordinal));
            Assert.DoesNotContain(AutoMlSkip.LogisticNeedsBinary, report.Skipped);
        }

        [Fact]
        public void AutoMl_multinomial_winner_is_refit_saveable_and_matches_a_direct_fit()
        {
            var (x, y) = Blobs(210, 3, 3, seed: 7, sep: 1.2);
            var report = AutoMl.Search(x, y, 3, Opt(seed: 3, budget: 0)); // 시간 예산 0: 첫 설정(Multinomial-C1.0)만
            Assert.Equal("Multinomial-C1.0", report.BestName);
            Assert.Equal(MultinomialLogisticModel.ModelTypeName, report.BestModelType);
            var features = Enumerable.Range(0, 3).Select(j => new ModelFeature("f" + j, VariableKind.Numeric, null)).ToArray();
            var saved = new ModelBundle
            {
                ModelType = report.BestModelType, Task = ModelTask.Classification, Features = features, Scaler = report.BundleScaler,
                Target = "y", ClassNames = new[] { "a", "b", "c" }, Engine = report.BundleEngine, TrainingRows = report.BundleRows,
            };
            var loaded = ModelStore.Load(ModelStore.Serialize(saved, "1.20.0", DateTime.UtcNow)).Model;
            var before = ModelStore.PredictEncoded(saved, x);
            var after = ModelStore.PredictEncoded(loaded, x);
            Assert.Equal(before.ClassIndex, after.ClassIndex);
            Assert.Equal(before.Probability, after.Probability);
            Assert.True(report.BestConverged);
            var bundle = Assert.IsType<MultinomialLogisticModel>(report.BundleEngine);
            Assert.Equal(report.BundleRows, bundle.RowsFit);
            var eval = Assert.IsType<MultinomialLogisticModel>(report.EvaluationEngine);
            Assert.Equal(report.EvaluationRows.Length, eval.RowsFit);
            Assert.Empty(report.EvaluationRows.Intersect(report.TestRows));

            var raw = AdaBoost.TakeRows(x, report.EvaluationRows);
            var sx = report.EvaluationScaler!.Transform(raw);
            var labels = report.EvaluationRows.Select(r => y[r]).ToArray();
            var direct = MultinomialLogistic.Fit(sx, labels, 3, 1.0);
            for (int k = 0; k < 3; k++)
                for (int j = 0; j < 4; j++)
                    Assert.Equal(direct.Coefficients[k, j], eval.Coefficients[k, j], 12);
            Assert.NotNull(report.TestClassification);
            Assert.True(report.TestScore > report.BaselineScore);
        }

        [Fact]
        public void AutoMl_selection_is_deterministic()
        {
            var (x, y) = Blobs(240, 3, 2, seed: 8, sep: 1.8);
            var opt = Opt(seed: 4, budget: 120);
            var a = AutoMl.Search(x, y, 3, opt);
            var b = AutoMl.Search(x, y, 3, opt);
            Assert.Equal(a.Grid, b.Grid);
            Assert.Equal(a.BestName, b.BestName);
            Assert.Equal(a.Leaderboard.Select(t => t.Name), b.Leaderboard.Select(t => t.Name));
            for (int i = 0; i < a.Leaderboard.Count; i++)
            {
                Assert.Equal(a.Leaderboard[i].Mean, b.Leaderboard[i].Mean, 12);
                Assert.Equal(a.Leaderboard[i].Sd, b.Leaderboard[i].Sd, 12);
            }
            Assert.Contains(a.Leaderboard, t => t.Name == "Multinomial-C1.0");
            Assert.Contains(a.Leaderboard, t => t.Name == "Multinomial-C0.1");
            Assert.Equal(a.BundleEngine is MultinomialLogisticModel, a.BestModelType == MultinomialLogisticModel.ModelTypeName);
            Assert.Equal(a.BestName.StartsWith("Multinomial", StringComparison.Ordinal), a.BestConverged is not null);
            Assert.Empty(a.Failures);
        }

        [Theory]
        [InlineData(800, 40, 3, AutoMlSvmMode.ExactSmo, int.MaxValue)]
        [InlineData(801, 5, 3, AutoMlSvmMode.LinearDcd, 100_000)]
        [InlineData(100, 41, 2, AutoMlSvmMode.LinearDcd, 100_000)]
        [InlineData(5000, 2000, 3, AutoMlSvmMode.LinearDcd, 1666)]
        [InlineData(5000, 1000, 10, AutoMlSvmMode.LinearDcd, 1000)]
        [InlineData(5000, 100_000, 3, AutoMlSvmMode.None, 0)]
        public void Svm_plan_follows_the_documented_rule(int rows, int features, int classes, AutoMlSvmMode mode, int cap)
        {
            var plan = AutoMl.PlanSvm(rows, features, classes);
            Assert.Equal(mode, plan.Mode);
            Assert.Equal(cap, plan.RowCap);
        }

        [Fact]
        public void Svm_competes_on_search_samples_above_the_exact_limit()
        {
            var (x, y) = Blobs(1300, 2, 2, seed: 9, sep: 2.5);
            var report = AutoMl.Search(x, y, 2, Opt(seed: 1, budget: 300, searchRows: 900));
            Assert.True(report.SearchRows.Length > AutoMl.SvmSearchRowCap);
            Assert.Equal(AutoMlSvmMode.LinearDcd, report.SvmMode);
            Assert.Equal(AutoMl.SvmDcdMaxRows, report.SvmFitRowCap);
            Assert.DoesNotContain("SVM-linear", report.Grid);
            Assert.DoesNotContain(AutoMlSkip.SvmTooLarge, report.Skipped);
            var svm = Assert.Single(report.Leaderboard, t => t.Name == "SVM-linear-dcd");
            Assert.Equal(ModelTypes.Svm, svm.ModelType);
            Assert.True(svm.Mean > 0.9, $"separable blobs, SVM CV accuracy {svm.Mean}");
            Assert.Empty(report.Failures);
        }

        [Fact]
        public void Svm_fit_rows_are_a_seeded_stratified_sample_when_the_work_cap_binds()
        {
            var (x, y) = Blobs(1300, 2, 2, seed: 10, sep: 2.5);
            var opt = Opt(seed: 6, budget: 300, searchRows: 900) with { SvmWorkPerEpoch = 2 * 2 * 300 };
            var a = AutoMl.Search(x, y, 2, opt);
            var b = AutoMl.Search(x, y, 2, opt);
            Assert.Equal(300, a.SvmFitRowCap);
            var ta = a.Leaderboard.Single(t => t.Name == "SVM-linear-dcd");
            var tb = b.Leaderboard.Single(t => t.Name == "SVM-linear-dcd");
            Assert.Equal(ta.Mean, tb.Mean, 12);
            Assert.Equal(ta.Sd, tb.Sd, 12);
            Assert.True(ta.Mean > 0.85);
            if (a.BestName == "SVM-linear-dcd")
            {
                Assert.True(a.EvaluationSampled);
                Assert.Equal(300, a.EvaluationRows.Length);
                Assert.True(a.BundleSampled);
                Assert.Equal(300, a.BundleRows);
                Assert.False(a.BundleUsesAllRows);
                Assert.Empty(a.EvaluationRows.Intersect(a.TestRows));
            }
        }

        [Fact]
        public void Svm_is_skipped_and_listed_when_even_the_minimum_rows_exceed_the_work_cap()
        {
            var (x, y) = Blobs(1200, 2, 2, seed: 11, sep: 2.0);
            var opt = Opt(seed: 1, budget: 0, searchRows: 900) with { SvmWorkPerEpoch = 100 };
            var report = AutoMl.Search(x, y, 2, opt);
            Assert.Equal(AutoMlSvmMode.None, report.SvmMode);
            Assert.Null(report.SvmFitRowCap);
            Assert.Contains(AutoMlSkip.SvmTooLarge, report.Skipped);
            Assert.DoesNotContain(report.Grid, g => g.StartsWith("SVM", StringComparison.Ordinal));
        }

        [Fact]
        public void CapRows_is_a_sorted_seeded_stratified_subset()
        {
            var labels = Enumerable.Range(0, 400).Select(i => i % 4 == 0 ? 1 : 0).ToArray();
            var rows = Enumerable.Range(0, 400).Where(i => i % 2 == 0).ToArray(); // 200 rows
            Assert.Same(rows, AutoMl.CapRows(rows, labels, 200, 1));
            var a = AutoMl.CapRows(rows, labels, 40, 7);
            var b = AutoMl.CapRows(rows, labels, 40, 7);
            Assert.Equal(a, b);
            Assert.Equal(40, a.Length);
            Assert.Equal(a.OrderBy(v => v), a);
            Assert.Subset(rows.ToHashSet(), a.ToHashSet());
            Assert.Equal(40, a.Distinct().Count());
            int ones = a.Count(r => labels[r] == 1);
            Assert.InRange(ones, 19, 21); // 모든 짝수 행의 절반이 클래스 1 → 층화 유지
            Assert.NotEqual(a, AutoMl.CapRows(rows, labels, 40, 8));
        }

        [Fact]
        public void AutoMl_honours_cancellation_before_and_during_the_search()
        {
            var (x, y) = Blobs(300, 3, 2, seed: 12, sep: 1.5);
            using var pre = new CancellationTokenSource();
            pre.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => AutoMl.Search(x, y, 3, Opt(seed: 1, budget: 120), pre.Token));

            var (bx, by) = Blobs(60_000, 4, 30, seed: 13, sep: 0.3);
            using var during = new CancellationTokenSource();
            during.CancelAfter(300);
            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => AutoMl.Search(bx, by, 4,
                Opt(seed: 1, budget: 600, searchRows: 40_000), during.Token));
            Assert.True(sw.Elapsed.TotalSeconds < 30, $"cancellation took {sw.Elapsed.TotalSeconds:F1}s");
        }

        static void Near(double expected, double actual, double tol = 1e-6)
            => Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected} actual {actual}");

        static AutoMlOptions Opt(int seed, double budget, int searchRows = 2_000) => new()
        {
            Metric = AutoMlMetric.Accuracy,
            Folds = 3,
            Seed = seed,
            TestFraction = 0.25,
            TimeBudgetSeconds = budget,
            SearchRowBudget = searchRows,
        };

        static (double[,] X, int[] Y) Blobs(int n, int k, int p, int seed, double sep)
        {
            var rng = new Random(seed);
            var x = new double[n, p];
            var y = new int[n];
            for (int i = 0; i < n; i++)
            {
                int c = i % k;
                y[i] = c;
                for (int j = 0; j < p; j++)
                {
                    double center = ((c * 7 + j * 3) % (k + 2)) * sep * 0.5 * (j % 2 == 0 ? 1 : -1);
                    double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                    x[i, j] = center + Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                }
            }
            return (x, y);
        }
    }
}
