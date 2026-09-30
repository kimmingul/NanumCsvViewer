using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 기준값은 throwaway Python(sklearn 1.7.2 HistGradientBoosting*)으로 생성하고 여기에 고정했다.
    // 설정이 같으면 분할 동점이 없는 작은 자료에서 예측이 거의 일치해야 한다.
    // 허용오차 1e-6: 기울기를 float32로 합산하는 sklearn과 잎 값 공식이 같을 때의 잔차.
    // 더 어긋나면 알고리즘 불일치이므로 오차를 느슨하게 풀지 않고 구현을 고친다.
    public class GradientBoostingTests
    {
        private const double Tol = 1e-6;

        // sklearn.ensemble.HistGradientBoostingRegressor(
        //   loss='squared_error', learning_rate=0.1, max_iter=10, max_leaf_nodes=6,
        //   max_depth=3, min_samples_leaf=3, l2_regularization=0, max_bins=255,
        //   early_stopping=False).predict(X)
        private static readonly double[] RegPred =
        {
            1.2486292567518014, 1.2486292567518014, 1.2486292567518014, 1.2486292567518014, 1.2486292567518014,
            1.2486292567518014, 1.2486292567518014, 1.2486292567518014, 1.2486292567518014, 2.222700682116879,
            2.222700682116879, 2.222700682116879, 2.222700682116879, 2.222700682116879, 2.222700682116879,
            2.222700682116879, 2.222700682116879, 2.222700682116879, 3.276289791934301, 3.276289791934301,
            3.276289791934301, 3.276289791934301, 3.276289791934301, 3.276289791934301, 3.276289791934301,
            3.276289791934301, 4.511917627012066, 4.511917627012066, 4.511917627012066, 4.511917627012066,
            4.511917627012066, 4.511917627012066, 4.511917627012066, 4.511917627012066, 4.511917627012066,
            5.522153987212325, 5.522153987212325, 5.522153987212325, 5.522153987212325, 5.522153987212325,
            5.522153987212325, 5.522153987212325, 5.522153987212325, 6.752426297913969, 6.752426297913969,
            6.752426297913969, 6.752426297913969, 6.752426297913969, 6.752426297913969, 6.752426297913969,
            6.752426297913969, 6.752426297913969, 7.811423124986535, 7.811423124986535, 7.811423124986535,
            7.811423124986535, 7.811423124986535, 7.811423124986535, 7.811423124986535, 7.811423124986535,
        };

        // 같은 X,y. max_bins=4, learning_rate=0.2, max_iter=5, max_leaf_nodes=4, max_depth=None,
        // min_samples_leaf=8, l2_regularization=1. 분위 경계는 sklearn _find_binning_thresholds와 같다
        // (특성0: 1.45, 2.95, 4.45).
        private static readonly double[] QuantilePred =
        {
            1.8950220113827114, 1.8950220113827114, 1.8950220113827114, 1.8950220113827114, 1.8950220113827114,
            1.6232052114393023, 1.6232052114393023, 1.6232052114393023, 1.6232052114393023, 1.8950220113827114,
            1.8950220113827114, 1.8950220113827114, 1.8950220113827114, 1.6232052114393023, 1.6232052114393023,
            3.1747379594083336, 3.1747379594083336, 3.1747379594083336, 3.446554759351743, 3.446554759351743,
            3.446554759351743, 3.446554759351743, 3.1747379594083336, 3.1747379594083336, 3.1747379594083336,
            3.1747379594083336, 3.446554759351743, 3.446554759351743, 3.446554759351743, 3.446554759351743,
            5.343447559409671, 5.343447559409671, 5.343447559409671, 5.343447559409671, 5.343447559409671,
            5.623443351473127, 5.623443351473127, 5.623443351473127, 5.623443351473127, 5.343447559409671,
            5.343447559409671, 5.343447559409671, 5.343447559409671, 5.623443351473127, 5.623443351473127,
            7.287814847912107, 7.287814847912107, 7.287814847912107, 7.007819055848651, 7.007819055848651,
            7.007819055848651, 7.007819055848651, 7.287814847912107, 7.287814847912107, 7.287814847912107,
            7.287814847912107, 7.007819055848651, 7.007819055848651, 7.007819055848651, 7.007819055848651,
        };

        [Fact]
        public void Regression_predictions_match_sklearn_hist_gradient_boosting()
        {
            var (x, y) = RegData();
            var model = GradientBoosting.Fit(x, y, new GradientBoostingOptions
            {
                LearningRate = 0.1,
                MaxIterations = 10,
                MaxLeafNodes = 6,
                MaxDepth = 3,
                MinSamplesLeaf = 3,
                L2Regularization = 0,
                MaxBins = 255,
                EarlyStopping = false,
                Seed = 1,
            });
            Assert.Equal(10, model.IterationsUsed);
            Assert.False(model.EarlyStopped);
            var pred = model.PredictValues(x);
            AssertClose(RegPred, pred, Tol);
        }

        [Fact]
        public void Quantile_bins_and_l2_match_sklearn()
        {
            var (x, y) = RegData();
            var model = GradientBoosting.Fit(x, y, new GradientBoostingOptions
            {
                LearningRate = 0.2,
                MaxIterations = 5,
                MaxLeafNodes = 4,
                MaxDepth = 0,
                MinSamplesLeaf = 8,
                L2Regularization = 1,
                MaxBins = 4,
                EarlyStopping = false,
                Seed = 1,
            });
            Assert.Equal(new[] { 4, 4 }, model.BinsPerFeature);
            AssertClose(QuantilePred, model.PredictValues(x), Tol);
        }

        [Fact]
        public void Training_loss_does_not_increase_and_fit_is_deterministic()
        {
            var (x, y) = RegData();
            var opt = new GradientBoostingOptions
            {
                LearningRate = 0.1, MaxIterations = 12, MaxLeafNodes = 8, MaxDepth = 3,
                MinSamplesLeaf = 2, EarlyStopping = false, Seed = 3,
            };
            var a = GradientBoosting.Fit(x, y, opt);
            var b = GradientBoosting.Fit(x, y, opt);
            AssertClose(a.PredictValues(x), b.PredictValues(x), 0);
            Assert.Equal(a.TrainLoss, b.TrainLoss);
            Assert.True(a.TrainLoss[0] > a.TrainLoss[^1]);
            for (int i = 1; i < a.TrainLoss.Length; i++)
                Assert.True(a.TrainLoss[i] <= a.TrainLoss[i - 1] + 1e-9, $"loss rose at {i}: {a.TrainLoss[i - 1]} -> {a.TrainLoss[i]}");
        }

        [Fact]
        public void Constant_feature_has_zero_gain()
        {
            var (x0, y) = RegData();
            var x = new double[60, 3];
            for (int i = 0; i < 60; i++)
            {
                x[i, 0] = x0[i, 0];
                x[i, 1] = x0[i, 1];
                x[i, 2] = 4;
            }
            var model = GradientBoosting.Fit(x, y, new GradientBoostingOptions
            {
                MaxIterations = 8, MaxLeafNodes = 6, MaxDepth = 3, MinSamplesLeaf = 3, EarlyStopping = false,
            });
            Assert.Equal(0, model.FeatureImportance[2]);
            Assert.True(model.FeatureImportance[0] > 0);
        }

        [Fact]
        public void Binary_predictions_match_sklearn()
        {
            var (x, y) = BinaryData();
            var model = GradientBoosting.Fit(x, y, 2, new GradientBoostingOptions
            {
                LearningRate = 0.1, MaxIterations = 15, MaxLeafNodes = 8, MaxDepth = 3,
                MinSamplesLeaf = 4, L2Regularization = 0.1, MaxBins = 255, EarlyStopping = false, Seed = 1,
            });
            // sklearn HistGradientBoostingClassifier(...).predict — accuracy 1.0 on this set.
            int[] expected =
            {
                0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1,
                0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1,
                0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
                0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1,
            };
            Assert.Equal(expected, model.PredictClasses(x));
        }

        [Fact]
        public void Multiclass_predictions_match_sklearn()
        {
            var (x, y) = MultiData();
            var model = GradientBoosting.Fit(x, y, 3, new GradientBoostingOptions
            {
                LearningRate = 0.15, MaxIterations = 8, MaxLeafNodes = 6, MaxDepth = 2,
                MinSamplesLeaf = 3, L2Regularization = 0, MaxBins = 255, EarlyStopping = false, Seed = 2,
            });
            var pred = model.PredictClasses(x);
            for (int i = 0; i < y.Length; i++) Assert.Equal(y[i], pred[i]);
            Assert.Equal(8, model.IterationsUsed);
        }

        [Fact]
        public void Early_stopping_halts_and_is_deterministic()
        {
            var (x, y) = RegData();
            var opt = new GradientBoostingOptions
            {
                LearningRate = 0.3, MaxIterations = 80, MaxLeafNodes = 4, MaxDepth = 2, MinSamplesLeaf = 5,
                EarlyStopping = true, ValidationFraction = 0.25, IterationsNoChange = 3,
                EarlyStoppingTolerance = 1e-6, Seed = 11,
            };
            var a = GradientBoosting.Fit(x, y, opt);
            var b = GradientBoosting.Fit(x, y, opt);
            Assert.True(a.EarlyStopped);
            Assert.True(a.IterationsUsed < opt.MaxIterations);
            Assert.True(a.IterationsUsed >= opt.IterationsNoChange);
            Assert.NotNull(a.ValidationLoss);
            Assert.Equal(a.IterationsUsed + 1, a.ValidationLoss!.Length);
            Assert.Equal(a.IterationsUsed, b.IterationsUsed);
            AssertClose(a.PredictValues(x), b.PredictValues(x), 0);
            Assert.True(a.BestValidationIndex >= 0 && a.BestValidationIndex < a.ValidationLoss.Length);
        }

        [Fact]
        public void Holdout_regression_beats_intercept_baseline()
        {
            var (x, y) = RegData();
            var report = GradientBoosting.Evaluate(x, y, new GradientBoostingOptions
            {
                LearningRate = 0.1, MaxIterations = 15, MaxLeafNodes = 8, MaxDepth = 3, MinSamplesLeaf = 3,
                EarlyStopping = false, Scheme = EvalScheme.Holdout, TestFraction = 0.3, Seed = 4,
            });
            Assert.NotNull(report.Regression);
            Assert.NotNull(report.RegressionBaseline);
            Assert.True(report.Regression!.Rmse < report.RegressionBaseline!.Rmse);
            Assert.NotNull(report.TrainLoss);
            Assert.Equal(16, report.TrainLoss!.Length);
            Assert.False(report.ImportanceAveragedAcrossFolds);
            double sum = report.FeatureImportance.Sum();
            Assert.True(Math.Abs(sum - 1) < 1e-12);
        }

        [Fact]
        public void Rejects_bad_options_without_a_partial_fit()
        {
            var (x, y) = RegData();
            Assert.Throws<DesignMatrixException>(() => GradientBoosting.Fit(x, y, new GradientBoostingOptions { MaxBins = 1 }));
            Assert.Throws<DesignMatrixException>(() => GradientBoosting.Fit(x, y, new GradientBoostingOptions { LearningRate = 0 }));
            Assert.Throws<DesignMatrixException>(() => GradientBoosting.Fit(x, y, new GradientBoostingOptions { Seed = 0 }));
            var labels = new int[60];
            Assert.Throws<DesignMatrixException>(() => GradientBoosting.Fit(x, labels, 1));
        }

        [Fact]
        public void Large_n_fit_finishes_and_is_deterministic()
        {
            const int n = 80_000;
            const int p = 6;
            var x = new double[n, p];
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < p; j++) x[i, j] = ((i * (j + 3)) % 97) / 10.0 + (j == 0 ? i % 11 : 0);
                y[i] = 0.4 * x[i, 0] - 0.2 * x[i, 1] + (i % 5) * 0.01;
            }
            var opt = new GradientBoostingOptions
            {
                LearningRate = 0.1, MaxIterations = 12, MaxLeafNodes = 8, MaxDepth = 4,
                MinSamplesLeaf = 20, MaxBins = 64, EarlyStopping = false, Seed = 5,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var model = GradientBoosting.Fit(x, y, opt);
            sw.Stop();
            Assert.True(sw.Elapsed.TotalSeconds < 20, $"elapsed {sw.Elapsed.TotalSeconds:0.00}s");
            Assert.Equal(12, model.IterationsUsed);
            Assert.True(model.TrainLoss[^1] < model.TrainLoss[0]);
            var again = GradientBoosting.Fit(x, y, opt);
            Assert.Equal(model.TrainLoss[^1], again.TrainLoss[^1]);
            Assert.Equal(model.FeatureImportance, again.FeatureImportance);
        }

        private static (double[,] X, double[] Y) RegData()
        {
            var x = new double[60, 2];
            var y = new double[60];
            for (int i = 0; i < 60; i++)
            {
                x[i, 0] = i * 0.1;
                x[i, 1] = ((i * 7) % 60) * 0.05;
                y[i] = 2 * x[i, 0] - x[i, 1] + 0.01 * ((i % 5) - 2);
            }
            return (x, y);
        }

        private static (double[,] X, int[] Y) BinaryData()
        {
            var x = new double[80, 3];
            var y = new int[80];
            for (int i = 0; i < 80; i++)
            {
                x[i, 0] = (i % 10) * 0.3;
                x[i, 1] = (i / 10) * 0.4;
                x[i, 2] = ((i * 3) % 7) * 0.2;
                y[i] = x[i, 0] + 0.5 * x[i, 1] > 2.0 ? 1 : 0;
            }
            return (x, y);
        }

        private static (double[,] X, int[] Y) MultiData()
        {
            var x = new double[90, 2];
            var y = new int[90];
            for (int i = 0; i < 90; i++)
            {
                x[i, 0] = (i % 9) * 0.5 + (i % 3) * 0.15;
                x[i, 1] = (i / 9) * 0.35 - (i % 3) * 0.2;
                y[i] = i % 3;
            }
            return (x, y);
        }

        private static void AssertClose(double[] expected, double[] actual, double tol)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                double d = Math.Abs(expected[i] - actual[i]);
                Assert.True(d <= tol, $"row {i}: expected {expected[i]}, actual {actual[i]}, diff {d}");
            }
        }
    }
}
