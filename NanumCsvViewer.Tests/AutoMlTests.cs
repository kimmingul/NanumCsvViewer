using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 기준값: sklearn 1.7.2 AdaBoostClassifier(algorithm='SAMME') / AdaBoostRegressor.
    // 분류 기본 학습기는 DecisionTreeClassifier(max_depth=1 또는 2). 회귀는 DecisionTreeRegressor(max_depth=1).
    // 동점이 없는 분할에서 예측·가중·오차가 맞아야 한다. 어긋나면 허용오차를 풀지 않고 구현을 고친다.
    public class AutoMlTests
    {
        const double Tol = 1e-8;

        static readonly double[,] XorX = Mat(new[]
        {
            0.0, 0.1, 0.2, 0.0, 0.1, 0.3, 0.05, 1.2, 0.3, 0.9, 0.15, 1.1,
            1.2, 0.1, 0.9, 0.2, 1.1, 0.0, 1.0, 1.1, 1.3, 0.8, 0.8, 1.0,
            0.25, 0.2, 0.4, 1.3, 1.4, 0.15, 1.15, 1.25, -0.1, 0.4, 0.35, 0.85,
            1.5, 0.35, 0.95, 1.4,
        }, 2);
        static readonly int[] XorY = { 0, 0, 0, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 0, 0, 1, 1, 0 };

        static readonly double[,] RegX = Mat(new[]
        {
            0.2, 0.1, 0.4, 0.8, 1.1, 0.2, 0.3, 1.4, 1.6, 0.5, 0.7, 1.1,
            1.8, 1.3, 0.5, 0.4, 1.2, 1.7, 0.9, 0.6, 1.4, 0.9, 0.1, 1.2,
            1.9, 0.3, 0.6, 1.6, 1.3, 0.7, 0.8, 0.2, 1.7, 1.5, 0.35, 0.95,
            1.05, 1.25, 0.55, 1.55,
        }, 2);
        static readonly double[] RegY =
        {
            0.2, 1.5, 0.4, 2.2, 1.1, 2.8, 0.7, 1.9, 3.1, 0.9,
            2.4, 1.3, 0.5, 2.6, 1.8, 0.3, 3.4, 1.6, 2.0, 0.8,
        };

        [Fact]
        public void Samme_stumps_match_sklearn()
        {
            var model = AdaBoost.FitClassification(XorX, XorY, 2, new AdaBoostOptions
            {
                Estimators = 8,
                LearningRate = 0.9,
                MaxDepth = 1,
                Seed = 4,
            });
            Assert.Equal(8, model.EstimatorsUsed);
            Assert.Equal(new[] { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0 }, model.Predict(XorX));
            Near(new[]
            {
                0.3649185972973484, 0.5966032167787925, 0.4274073334308697, 0.35982166758240186,
                0.30585811283578185, 0.3119623439005352, 0.3425425293229602, 0.4703695825741859,
            }, model.EstimatorWeights);
            Near(new[]
            {
                0.39999999999999986, 0.3400901594643177, 0.38345783745093437, 0.40135994779572953,
                0.4158477732267787, 0.41420113042248374, 0.4059815147683983, 0.3722367871182846,
            }, model.EstimatorErrors);
            Near(new[] { 0.901882694057279, 0.09811730594272101 }, model.Importances);
            Assert.Equal(8, model.Stumps.Count);
            Assert.Empty(model.RegressionTrees);
            Assert.Equal(0.9, model.LearningRate);
            Assert.Equal(2, model.ClassCount);
            Assert.True(model.Stumps[0].Feature >= 0);
        }

        [Fact]
        public void Samme_depth2_matches_sklearn()
        {
            var model = AdaBoost.FitClassification(XorX, XorY, 2, new AdaBoostOptions
            {
                Estimators = 5,
                LearningRate = 0.6,
                MaxDepth = 2,
                Seed = 4,
            });
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0 }, model.Predict(XorX));
            Near(new[] { 0.2432790648648989, 0.46536520513444374, 0.27545167246510105, 0.21692357297596324, 0.21062853777838225 }, model.EstimatorWeights);
            Near(new[] { 0.39999999999999986, 0.3152670879005026, 0.38720264379332975, 0.4105869987889732, 0.413128412864051 }, model.EstimatorErrors);
            Near(new[] { 0.8792349547417232, 0.12076504525827694 }, model.Importances);
        }

        [Fact]
        public void Samme_multiclass_matches_sklearn()
        {
            var x = Mat(new[]
            {
                0.1, 0.2, 0.2, 0.1, 0.15, 0.4, 0.3, 0.25,
                1.2, 0.2, 1.4, 0.1, 1.1, 0.5, 1.3, 0.3,
                0.2, 1.3, 0.1, 1.5, 0.4, 1.2, 0.25, 1.4,
                0.8, 0.7, 1.0, 0.9, 0.6, 1.0, 0.9, 0.4,
            }, 2);
            var y = new[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 1, 2, 1 };
            var model = AdaBoost.FitClassification(x, y, 3, new AdaBoostOptions
            {
                Estimators = 6,
                LearningRate = 1,
                MaxDepth = 1,
                Seed = 3,
            });
            Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 0, 1, 2, 1 }, model.Predict(x));
            Near(new[] { 1.4816045409242156, 2.4159137783010487, 3.2580965380214817, 2.9586910018736416, 3.030823593365261, 3.0109997456847797 }, model.EstimatorWeights);
            Near(new[] { 0.3125, 0.15151515151515152, 0.07142857142857145, 0.09401709401709404, 0.08805031446540881, 0.0896551724137931 }, model.EstimatorErrors);
            Near(new[] { 0.2748365953461607, 0.7251634046538392 }, model.Importances);
        }

        [Theory]
        [InlineData(AdaBoostLoss.Linear)]
        [InlineData(AdaBoostLoss.Square)]
        [InlineData(AdaBoostLoss.Exponential)]
        public void R2_matches_sklearn(AdaBoostLoss loss)
        {
            var model = AdaBoost.FitRegression(RegX, RegY, new AdaBoostOptions
            {
                Estimators = 6,
                LearningRate = 0.8,
                MaxDepth = 1,
                Seed = 1,
                Loss = loss,
            });
            Assert.Equal(6, model.EstimatorsUsed);
            Assert.Equal(6, model.RegressionTrees.Count);
            Assert.Empty(model.Stumps);
            var (pred, weights, errors, imp) = loss switch
            {
                AdaBoostLoss.Square => (new[]
                {
                    1.5000000000000002, 1.5000000000000002, 1.5000000000000002, 1.5000000000000002, 1.535294117647059,
                    1.5000000000000002, 1.535294117647059, 1.5000000000000002, 2.9333333333333336, 1.5000000000000002,
                    1.535294117647059, 1.5000000000000002, 1.5000000000000002, 2.9333333333333336, 1.535294117647059,
                    1.5000000000000002, 2.725, 1.5000000000000002, 1.535294117647059, 1.588235294117647,
                }, new[] { 1.0751349623483113, 0.821288903293873, 0.28005769599259883, 1.5652190310487524, 0.9621029566661549, 0.5466117527920401 },
                new[] { 0.20686636790538715, 0.26374163476043366, 0.413364932282042, 0.12384374602964458, 0.231007916248503, 0.335533047332449 },
                new[] { 0.28735149938382315, 0.7126485006161769 }),
                AdaBoostLoss.Exponential => (new[]
                {
                    1.0538461538461537, 1.535294117647059, 1.0538461538461537, 1.769230769230769, 1.26875,
                    1.535294117647059, 1.535294117647059, 1.26875, 2.725, 1.26875,
                    1.535294117647059, 1.535294117647059, 1.0538461538461537, 2.725, 1.26875,
                    1.0538461538461537, 1.769230769230769, 1.535294117647059, 1.535294117647059, 1.769230769230769,
                }, new[] { 0.7146853827481777, 0.7674533498458382, 0.4951597554684547, 0.6444787307161537, 0.7431899036664933, 0.5688910236443846 },
                new[] { 0.29041760254956916, 0.2770150263169978, 0.3500203647244998, 0.3088292393963625, 0.2831301983872891, 0.32935278234846505 },
                new[] { 0.0, 1.0 }),
                _ => (new[]
                {
                    1.0666666666666667, 1.26875, 1.0666666666666667, 1.7384615384615383, 1.26875,
                    1.26875, 1.26875, 1.26875, 2.725, 1.26875,
                    1.26875, 1.26875, 1.0666666666666667, 2.725, 1.26875,
                    1.0666666666666667, 1.7384615384615383, 1.26875, 1.26875, 1.7384615384615383,
                }, new[] { 0.410006734080165, 0.4955155696900983, 0.07670543690210468, 0.266382764115621, 0.630828579072129, 0.24128701174290934 },
                new[] { 0.37460567823343854, 0.3499191840891669, 0.476047898099542, 0.41751609611049667, 0.312483164344354, 0.4251642544691689 },
                new[] { 0.0, 1.0 }),
            };
            Near(pred, model.PredictValue(RegX));
            Near(weights, model.EstimatorWeights);
            Near(errors, model.EstimatorErrors);
            Near(imp, model.Importances);
        }

        [Fact]
        public void Min_samples_leaf_blocks_stump_splits()
        {
            var x = new double[100, 1];
            var y = new double[100];
            var labels = new int[100];
            for (int i = 0; i < 100; i++)
            {
                x[i, 0] = i;
                y[i] = i;
                labels[i] = i < 50 ? 0 : 1;
            }
            var reg = AdaBoost.FitRegression(x, y, new AdaBoostOptions
            {
                Estimators = 1,
                MaxDepth = 1,
                Seed = 1,
                MinSamplesLeaf = 100,
            });
            Assert.Equal(-1, reg.RegressionTrees[0].Root.Feature);
            Assert.Equal(1, reg.RegressionTrees[0].LeafCount);
            // 분할이 없으면 모든 행이 한 잎 값(R2 부트스트랩 표본의 평균)으로 예측된다.
            var pred = reg.PredictValue(x);
            for (int i = 1; i < pred.Length; i++)
                Assert.Equal(pred[0], pred[i]);

            var cls = AdaBoost.FitClassification(x, labels, 2, new AdaBoostOptions
            {
                Estimators = 1,
                MaxDepth = 1,
                Seed = 1,
                MinSamplesLeaf = 100,
            });
            Assert.Equal(-1, cls.Stumps[0].Feature);
            Assert.Null(cls.Stumps[0].Left);
            Assert.Null(cls.Stumps[0].Right);
        }
        [Fact]
        public void Row_cap_is_labelled()
        {
            var model = AdaBoost.FitClassification(XorX, XorY, 2, new AdaBoostOptions
            {
                Estimators = 3,
                MaxDepth = 1,
                Seed = 2,
                MaxTrainingRows = 8,
            });
            Assert.True(model.Sampled);
            Assert.Equal(8, model.RowsFit);
            Assert.Equal(XorY.Length, model.SourceRows);
        }

        [Fact]
        public void AutoMl_is_deterministic_and_orders_the_leaderboard()
        {
            var (x, y) = Cloud(48, seed: 7);
            var opt = new AutoMlOptions
            {
                Metric = AutoMlMetric.Accuracy,
                Folds = 3,
                Seed = 5,
                TestFraction = 0.25,
                TimeBudgetSeconds = 120,
                SearchRowBudget = 100,
            };
            var a = AutoMl.Search(x, y, 2, opt);
            var b = AutoMl.Search(x, y, 2, opt);
            Assert.Equal(a.BestName, b.BestName);
            Assert.Equal(a.Leaderboard.Count, b.Leaderboard.Count);
            Assert.Equal(a.SearchRows, b.SearchRows);
            Assert.Equal(a.TestRows, b.TestRows);
            for (int i = 0; i < a.Leaderboard.Count; i++)
            {
                Assert.Equal(a.Leaderboard[i].Name, b.Leaderboard[i].Name);
                Assert.Equal(a.Leaderboard[i].Mean, b.Leaderboard[i].Mean, 12);
                Assert.Equal(a.Leaderboard[i].Sd, b.Leaderboard[i].Sd, 12);
                if (i > 0)
                    Assert.True(a.Leaderboard[i - 1].Mean + 1e-12 >= a.Leaderboard[i].Mean);
            }
            Assert.Equal(a.Leaderboard[0].Name, a.BestName);
            Assert.False(a.BudgetStopped);
            Assert.True(a.ConfigsTried == a.ConfigsPlanned);
        }

        [Fact]
        public void AutoMl_does_not_use_test_rows_in_search()
        {
            var (x, y) = Cloud(40, seed: 3);
            var opt = new AutoMlOptions
            {
                Metric = AutoMlMetric.MacroF1,
                Folds = 3,
                Seed = 9,
                TestFraction = 0.25,
                TimeBudgetSeconds = 120,
                SearchRowBudget = 100,
            };
            var first = AutoMl.Search(x, y, 2, opt);
            Assert.Empty(first.SearchRows.Intersect(first.TestRows));
            Assert.Empty(first.EvaluationRows.Intersect(first.TestRows));
            Assert.Empty(first.TrainRows.Intersect(first.TestRows));
            Assert.Superset(first.TrainRows.ToHashSet(), first.SearchRows.ToHashSet());

            var mutated = (double[,])x.Clone();
            foreach (int row in first.TestRows)
                for (int j = 0; j < mutated.GetLength(1); j++)
                    mutated[row, j] = 999;
            var second = AutoMl.Search(mutated, y, 2, opt);
            Assert.Equal(first.BestName, second.BestName);
            Assert.Equal(first.Leaderboard.Select(t => t.Name), second.Leaderboard.Select(t => t.Name));
            for (int i = 0; i < first.Leaderboard.Count; i++)
                Assert.Equal(first.Leaderboard[i].Mean, second.Leaderboard[i].Mean, 12);
        }

        [Fact]
        public void AutoMl_respects_time_and_row_budgets()
        {
            var (x, y) = Cloud(40, seed: 1);
            var tight = AutoMl.Search(x, y, 2, new AutoMlOptions
            {
                Metric = AutoMlMetric.Accuracy,
                Folds = 2,
                Seed = 1,
                TestFraction = 0.25,
                TimeBudgetSeconds = 0,
                SearchRowBudget = 100,
            });
            Assert.True(tight.BudgetStopped);
            Assert.Equal(1, tight.ConfigsTried);
            Assert.Single(tight.Leaderboard);
            Assert.True(tight.ConfigsPlanned > 1);

            var sampled = AutoMl.Search(x, y, 2, new AutoMlOptions
            {
                Metric = AutoMlMetric.Accuracy,
                Folds = 2,
                Seed = 4,
                TestFraction = 0.25,
                TimeBudgetSeconds = 120,
                SearchRowBudget = 8,
            });
            Assert.True(sampled.SearchSampled);
            Assert.True(sampled.SearchRows.Length <= 8);
            Assert.Empty(sampled.SearchRows.Intersect(sampled.TestRows));
        }

        [Fact]
        public void AutoMl_regression_leaderboard_is_ordered_by_rmse()
        {
            var x = new double[30, 1];
            var y = new double[30];
            for (int i = 0; i < 30; i++)
            {
                x[i, 0] = i;
                y[i] = 0.5 * i + (i % 3) * 0.1;
            }
            var report = AutoMl.Search(x, y, new AutoMlOptions
            {
                Metric = AutoMlMetric.Rmse,
                Folds = 3,
                Seed = 2,
                TestFraction = 0.2,
                TimeBudgetSeconds = 120,
                SearchRowBudget = 100,
            });
            Assert.True(report.Regression);
            Assert.False(report.HigherIsBetter);
            for (int i = 1; i < report.Leaderboard.Count; i++)
                Assert.True(report.Leaderboard[i - 1].Mean <= report.Leaderboard[i].Mean + 1e-12);
            Assert.DoesNotContain(report.SearchRows, report.TestRows.Contains);
            Assert.True(report.BundleUsesAllRows);
        }

        static (double[,] X, int[] Y) Cloud(int n, int seed)
        {
            var rng = new Random(seed);
            var x = new double[n, 2];
            var y = new int[n];
            for (int i = 0; i < n; i++)
            {
                y[i] = i < n / 2 ? 0 : 1;
                double shift = y[i] == 0 ? 0 : 1.4;
                x[i, 0] = shift + rng.NextDouble();
                x[i, 1] = rng.NextDouble() * 0.4 + y[i] * 0.2;
            }
            return (x, y);
        }

        static double[,] Mat(double[] flat, int p)
        {
            int n = flat.Length / p;
            var x = new double[n, p];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++)
                    x[i, j] = flat[i * p + j];
            return x;
        }

        static void Near(double[] expected, double[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.True(Math.Abs(expected[i] - actual[i]) <= Tol, $"[{i}] expected {expected[i]} actual {actual[i]}");
        }
    }
}
