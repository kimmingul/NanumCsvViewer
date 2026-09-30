using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 결정트리·랜덤 포레스트·SVM. 기준값은 throwaway Python(sklearn 1.7.2)으로 생성하고 여기에 고정했다.
    public class TreesSvmTests
    {
        // DecisionTreeClassifier(criterion='gini'|'entropy', max_depth=3, random_state=0..2).
        // 세 시드에서 트리 구조가 같았다(최적 분할이 유일).
        private static double[,] ClassX() => new double[,]
        {
            { 0, 0 }, { 0, 1 }, { 1, 0 }, { 1, 1 },
            { 0, 5 }, { 0, 6 }, { 1, 5 }, { 1, 6 },
            { 8, 0 }, { 8, 1 }, { 9, 0 }, { 9, 1 },
            { 8, 5 }, { 9, 6 }, { 8, 6 }, { 9, 5 },
        };

        private static int[] ClassY() => new[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2 };

        // DecisionTreeRegressor(max_depth=3). random_state 0, 1, 7에서 예측·중요도가 같았다.
        private static double[,] RegX() => new double[,]
        {
            { 0, 0 }, { 0, 1 }, { 1, 0 }, { 1, 1 },
            { 0, 8 }, { 1, 8 }, { 0, 9 }, { 1, 9 },
            { 10, 0 }, { 11, 0 }, { 10, 1 }, { 11, 1 },
        };

        private static double[] RegY() => new[] { 0, 0.2, 0.1, 0.0, 5, 5.2, 4.8, 5.1, 20, 21, 19, 20.5 };

        // sklearn SVC(kernel=..., C=1, shrinking=False, tol=1e-3)
        private static double[,] SvmX() => new double[,]
        {
            { 1.0, 2.0 }, { 1.2, 1.8 }, { 0.8, 2.2 }, { 1.5, 1.5 }, { 2.0, 2.5 },
            { 5.0, 5.0 }, { 5.2, 4.6 }, { 4.7, 5.3 }, { 5.5, 5.1 }, { 4.4, 4.8 },
            { 3.2, 1.0 }, { 1.0, 3.5 },
        };

        private static int[] SvmY() => new[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 1 };

        [Fact]
        public void Gini_tree_matches_sklearn_predictions_and_importances()
        {
            var model = DecisionTree.FitClassification(ClassX(), ClassY(), 3, new DecisionTreeOptions
            {
                Criterion = TreeCriterion.Gini,
                MaxDepth = 3,
                SplitMode = TreeSplitMode.Exact,
            });
            Assert.Equal(ClassY(), model.Predict(ClassX()));
            Close(0.6, model.Importances[0], 1e-9);
            Close(0.4, model.Importances[1], 1e-9);
            Assert.False(model.ApproximateSplits);
            Assert.True(model.Depth >= 1);
        }

        [Fact]
        public void Entropy_importances_match_sklearn()
        {
            var model = DecisionTree.FitClassification(ClassX(), ClassY(), 3, new DecisionTreeOptions
            {
                Criterion = TreeCriterion.Entropy,
                MaxDepth = 3,
                SplitMode = TreeSplitMode.Exact,
            });
            Assert.Equal(ClassY(), model.Predict(ClassX()));
            Close(0.6666666666666666, model.Importances[0], 1e-9);
            Close(0.3333333333333333, model.Importances[1], 1e-9);
        }

        [Fact]
        public void Regression_tree_matches_sklearn_predictions_and_importances()
        {
            var model = DecisionTree.FitRegression(RegX(), RegY(), new DecisionTreeOptions
            {
                Criterion = TreeCriterion.Mse,
                MaxDepth = 3,
                SplitMode = TreeSplitMode.Exact,
            });
            double[] expected =
            {
                0.1, 0.1, 0.05, 0.05, 4.9, 5.15, 4.9, 5.15, 20.0, 21.0, 19.0, 20.5,
            };
            var pred = model.PredictValue(RegX());
            for (int i = 0; i < expected.Length; i++) Close(expected[i], pred[i], 1e-9);
            Close(0.9432760563353453, model.Importances[0], 1e-8);
            Close(0.05672394366465474, model.Importances[1], 1e-8);
        }

        [Fact]
        public void Tie_break_prefers_the_lower_feature_index()
        {
            var x = new double[,] { { 0, 0 }, { 0, 0 }, { 5, 5 }, { 5, 5 } };
            var y = new[] { 0, 0, 1, 1 };
            var model = DecisionTree.FitClassification(x, y, 2, new DecisionTreeOptions { SplitMode = TreeSplitMode.Exact, MaxDepth = 2 });
            Assert.Equal(0, model.Root.Feature);
            Close(1, model.Importances[0], 1e-12);
            Close(0, model.Importances[1], 1e-12);
        }

        [Fact]
        public void Max_depth_stops_the_tree()
        {
            var model = DecisionTree.FitClassification(ClassX(), ClassY(), 3, new DecisionTreeOptions
            {
                MaxDepth = 1,
                SplitMode = TreeSplitMode.Exact,
            });
            Assert.Equal(1, model.Depth);
            Assert.Equal(2, model.LeafCount);
        }

        [Fact]
        public void Binned_mode_is_labelled_and_keeps_discrete_partitions()
        {
            var model = DecisionTree.FitClassification(ClassX(), ClassY(), 3, new DecisionTreeOptions
            {
                SplitMode = TreeSplitMode.Binned,
                Bins = 8,
                MaxDepth = 3,
            });
            Assert.True(model.ApproximateSplits);
            Assert.Equal(ClassY(), model.Predict(ClassX()));
        }

        [Fact]
        public void Forest_is_deterministic_and_separates_blobs()
        {
            var (x, y) = Blobs();
            var opt = new RandomForestOptions
            {
                Trees = 15,
                MaxDepth = 6,
                Seed = 3,
                SplitMode = TreeSplitMode.Exact,
                MaxTrainingRows = 500,
            };
            var a = RandomForest.FitClassification(x, y, 3, opt);
            var b = RandomForest.FitClassification(x, y, 3, opt);
            Assert.Equal(a.Predict(x), b.Predict(x));
            int correct = 0;
            var pred = a.Predict(x);
            for (int i = 0; i < y.Length; i++) if (pred[i] == y[i]) correct++;
            Assert.Equal(y.Length, correct);
            Assert.True(a.OobRows > 0);
            Assert.InRange(a.OobError, 0, 0.25);
            Assert.Equal("MDI", RandomForest.ImportanceMethod);
            double sum = 0;
            for (int i = 0; i < a.Importances.Length; i++) sum += a.Importances[i];
            Close(1, sum, 1e-9);
        }

        [Fact]
        public void Forest_row_cap_is_labelled()
        {
            var (x, y) = Blobs();
            var model = RandomForest.FitClassification(x, y, 3, new RandomForestOptions
            {
                Trees = 4,
                MaxDepth = 4,
                Seed = 2,
                MaxTrainingRows = 30,
            });
            Assert.True(model.Sampled);
            Assert.Equal(30, model.TrainingRows);
            Assert.Equal(y.Length, model.SourceRows);
        }

        [Fact]
        public void Different_forest_seeds_are_not_required_to_match()
        {
            var (x, y) = Blobs();
            var a = RandomForest.FitClassification(x, y, 3, new RandomForestOptions { Trees = 5, Seed = 1, MaxDepth = 4, SplitMode = TreeSplitMode.Exact });
            var b = RandomForest.FitClassification(x, y, 3, new RandomForestOptions { Trees = 5, Seed = 1, MaxDepth = 4, SplitMode = TreeSplitMode.Exact });
            Assert.Equal(a.Predict(x), b.Predict(x));
        }

        [Fact]
        public void Linear_smo_matches_sklearn_svc()
        {
            // SVC(kernel='linear', C=1, shrinking=False, tol=1e-3).decision_function / n_support_
            var model = SupportVectorMachine.Fit(SvmX(), SvmY(), 2, new SvmOptions
            {
                Kernel = SvmKernel.Linear,
                C = 1,
                MaxTrainingRows = 100,
            });
            Assert.Equal("SMO", model.Solver);
            Assert.Equal(new[] { 2, 2 }, model.SupportPerClass);
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 1 }, model.Predict(SvmX()));
            double[] dec =
            {
                -1.3363057808591758, -1.6726115089945264, -1.0000000527238218, -2.177070101197554,
                -0.9445859747540934, 1.6127390747490473, 1.0000002203761191, 2.1171976669520802,
                1.6012741331234004, 1.5159237542047919, -3.3770700329225667, 0.7369426659226637,
            };
            var actual = model.DecisionFunction(SvmX());
            for (int i = 0; i < dec.Length; i++) Close(dec[i], actual[i], 1e-4);
        }

        [Fact]
        public void Rbf_smo_matches_sklearn_svc()
        {
            // SVC(kernel='rbf', C=1, gamma='scale', shrinking=False, tol=1e-3)
            var model = SupportVectorMachine.Fit(SvmX(), SvmY(), 2, new SvmOptions
            {
                Kernel = SvmKernel.Rbf,
                C = 1,
                GammaMode = SvmGammaMode.Scale,
                MaxTrainingRows = 100,
            });
            Close(0.17566116912267005, model.Gamma, 1e-12);
            Assert.Equal(new[] { 3, 3 }, model.SupportPerClass);
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0 }, model.Predict(SvmX()));
            double[] dec =
            {
                -1.1140215751490914, -1.2009003753746825, -0.9997937639724281, -1.270709744112288,
                -0.9997508084874189, 1.0616975097606882, 1.0038155208355155, 1.0473402409076833,
                0.9997407988241105, 0.9995973815562684, -0.9997937525033905, -0.31325856904420424,
            };
            var actual = model.DecisionFunction(SvmX());
            for (int i = 0; i < dec.Length; i++) Close(dec[i], actual[i], 1e-4);
        }

        [Fact]
        public void Multiclass_rbf_matches_sklearn_predictions_and_support_counts()
        {
            // SVC(kernel='rbf', C=1, gamma='scale', shrinking=False).predict / n_support_
            var x = new double[,]
            {
                { 0.0, 0.0 }, { 0.2, 0.1 }, { 0.1, 0.3 },
                { 3.0, 0.0 }, { 3.2, 0.2 }, { 2.8, 0.1 },
                { 0.0, 3.0 }, { 0.2, 3.1 }, { 0.1, 2.7 },
                { 1.5, 1.5 }, { 1.6, 1.4 },
            };
            var y = new[] { 0, 0, 0, 1, 1, 1, 2, 2, 2, 0, 1 };
            var model = SupportVectorMachine.Fit(x, y, 3, new SvmOptions
            {
                Kernel = SvmKernel.Rbf,
                GammaMode = SvmGammaMode.Scale,
                MaxTrainingRows = 100,
            });
            Assert.Equal("one-vs-one", model.Multiclass);
            Assert.Equal(y, model.Predict(x));
            Assert.Equal(new[] { 4, 4, 2 }, model.SupportPerClass);
            Close(0.3272525659576195, model.Gamma, 1e-12);
        }

        [Fact]
        public void Kernel_row_cap_is_a_labelled_stratified_sample()
        {
            var (x, y) = Blobs();
            var model = SupportVectorMachine.Fit(x, y, 3, new SvmOptions
            {
                Kernel = SvmKernel.Rbf,
                MaxTrainingRows = 24,
                Seed = 5,
                MaxIterations = 200,
            });
            Assert.True(model.Sampled);
            Assert.Equal(24, model.RowsUsed);
            Assert.Equal(y.Length, model.RowsPresented);
            Assert.Equal("SMO", model.Solver);
        }

        [Fact]
        public void Linear_above_the_cap_uses_full_data_coordinate_descent()
        {
            var model = SupportVectorMachine.Fit(SvmX(), SvmY(), 2, new SvmOptions
            {
                Kernel = SvmKernel.Linear,
                MaxTrainingRows = 6,
                LinearEpochs = 40,
                Seed = 1,
            });
            Assert.Equal("DCD", model.Solver);
            Assert.False(model.Sampled);
            Assert.Equal(SvmY().Length, model.RowsUsed);
            Assert.Equal("one-vs-rest", model.Multiclass);
            var pred = model.Predict(SvmX());
            int correct = 0;
            var y = SvmY();
            for (int i = 0; i < y.Length; i++) if (pred[i] == y[i]) correct++;
            Assert.True(correct >= 10);
        }

        [Fact]
        public void Gamma_scale_uses_centered_variance_on_large_offsets()
        {
            // np.var([1e9, 1e9+1], ddof=0) = 0.25, p = 1 → gamma = 1 / 0.25 = 4.
            // A raw second-moment formula cancels and returns 1.
            var x = new double[,] { { 1e9 }, { 1e9 + 1 } };
            Close(4, SupportVectorMachine.ResolveGamma(SvmGammaMode.Scale, 0, x), 1e-6);
        }

        [Fact]
        public void Kfold_display_model_is_fit_after_a_full_data_scaler()
        {
            var x = SvmX();
            var y = SvmY();
            var run = SupportVectorMachine.Evaluate(x, y, 2, new ClassifierOptions
            {
                Scheme = EvalScheme.KFold,
                Folds = 3,
                Seed = 1,
                Scaling = ScalingMethod.ZScore,
                TestFraction = 0.3,
            }, new SvmOptions
            {
                Kernel = SvmKernel.Rbf,
                GammaMode = SvmGammaMode.Scale,
                MaxTrainingRows = 100,
            });
            Assert.True(run.DisplayUsesAllRows);
            var scaler = FeatureScaler.Fit(x, ScalingMethod.ZScore);
            var rows = new int[x.GetLength(0)];
            for (int i = 0; i < rows.Length; i++) rows[i] = i;
            var scaled = ClassifierCommon.Extract(x, rows, scaler, null, default);
            double expected = SupportVectorMachine.ResolveGamma(SvmGammaMode.Scale, 0, scaled);
            double raw = SupportVectorMachine.ResolveGamma(SvmGammaMode.Scale, 0, x);
            Close(expected, run.Display.Gamma, 1e-12);
            Assert.True(Math.Abs(raw - run.Display.Gamma) > 1e-6);
        }

        [Fact]
        public void Evaluation_cap_labels_scored_and_presented_sizes()
        {
            var (x, y) = Blobs();
            var eval = new ClassifierOptions
            {
                Scheme = EvalScheme.Holdout,
                TestFraction = 0.5,
                Seed = 4,
                Scaling = ScalingMethod.None,
            };
            var opt = new SvmOptions
            {
                Kernel = SvmKernel.Linear,
                MaxTrainingRows = 100,
                MaxEvaluationRows = 8,
                MaxIterations = 300,
                Seed = 4,
            };
            var run = SupportVectorMachine.Evaluate(x, y, 3, eval, opt);
            var again = SupportVectorMachine.Evaluate(x, y, 3, eval, opt);
            Assert.True(run.EvaluationSampled);
            Assert.Equal(8, run.EvaluationRowsScored);
            Assert.True(run.EvaluationRowsPresented > run.EvaluationRowsScored);
            Assert.Equal(run.EvaluationRowsScored, (int)run.Evaluation.Metrics.Total);
            Assert.Equal(run.EvaluationRowsScored, run.Evaluation.TestRows);
            Assert.Equal(run.EvaluationRowsPresented, again.EvaluationRowsPresented);
            Assert.Equal(run.Evaluation.HoldoutPredicted, again.Evaluation.HoldoutPredicted);

            var full = SupportVectorMachine.Evaluate(x, y, 3, eval, opt with { MaxEvaluationRows = 10_000 });
            Assert.False(full.EvaluationSampled);
            Assert.Equal(full.EvaluationRowsPresented, full.EvaluationRowsScored);
        }

        private static (double[,] X, int[] Y) Blobs()
        {
            var x = new double[60, 2];
            var y = new int[60];
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < 20; i++)
                {
                    int r = c * 20 + i;
                    x[r, 0] = c == 1 ? 8 : 0;
                    x[r, 1] = c == 2 ? 8 : 0;
                    x[r, 0] += (i % 5) * 0.1;
                    x[r, 1] += (i / 5) * 0.1;
                    y[r] = c;
                }
            }
            return (x, y);
        }

        private static void Close(double expected, double actual, double tol)
        {
            Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");
        }
    }
}
