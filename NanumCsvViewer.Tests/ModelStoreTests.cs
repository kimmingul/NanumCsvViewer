using System.Globalization;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// 저장·로드 후 예측이 비트 단위로 같은지, 학습에 없던 범주 수준은 점수하지 않는지,
    /// ONNX protobuf 구조와 onnxruntime 1.20.1 기록값(아래 주석의 생성 스크립트)을 고정한다.
    /// </summary>
    public class ModelStoreTests
    {
        static readonly DateTime Created = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        // 수치 y, x, 범주 g(a/b), 클래스 cls(no/yes). g의 학습 수준은 a, b뿐.
        static (string[] Headers, List<string[]> Rows) Table(int n = 24)
        {
            var rows = new List<string[]>();
            for (int i = 0; i < n; i++)
            {
                string g = i % 2 == 0 ? "a" : "b";
                double x = i * 0.5;
                double y = 1.25 + 1.5 * x + (g == "b" ? 0.75 : 0);
                string cls = x + (g == "b" ? 2 : 0) >= 6 ? "yes" : "no";
                rows.Add(new[]
                {
                    y.ToString("G17", CultureInfo.InvariantCulture),
                    x.ToString("G17", CultureInfo.InvariantCulture),
                    g,
                    cls,
                });
            }
            return (new[] { "y", "x", "g", "cls" }, rows);
        }

        static Func<int, VariableKind> Kind(params int[] categorical)
        {
            var set = new HashSet<int>(categorical);
            return c => set.Contains(c) ? VariableKind.Categorical : VariableKind.Numeric;
        }

        static ModelBundle Trip(ModelBundle bundle)
        {
            var loaded = ModelStore.Load(ModelStore.Serialize(bundle, "1.18.0", Created));
            Assert.Equal("1.18.0", loaded.AppVersion);
            Assert.Equal(Created, loaded.CreatedAt.ToUniversalTime());
            Assert.Equal(bundle.ModelType, loaded.Model.ModelType);
            Assert.Equal(bundle.Features.Count, loaded.Model.Features.Count);
            return loaded.Model;
        }

        static void Same(EncodedPredictions a, EncodedPredictions b)
        {
            Assert.Equal(a.ClassIndex, b.ClassIndex);
            if (a.Value is null) Assert.Null(b.Value);
            else
            {
                Assert.NotNull(b.Value);
                Assert.Equal(a.Value.Length, b.Value!.Length);
                for (int i = 0; i < a.Value.Length; i++)
                    Assert.Equal(BitConverter.DoubleToInt64Bits(a.Value[i]), BitConverter.DoubleToInt64Bits(b.Value[i]));
            }
        }

        [Fact]
        public void Loader_rejects_a_different_format_version()
        {
            var (headers, rows) = Table(12);
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x + C(g)"), Kind(2));
            var bundle = ModelBundle.FromFormula(ModelTypes.LinearModel, ModelTask.Regression, dm, LinearModel.Fit(dm), "n");
            string json = ModelStore.Serialize(bundle, "1.18.0", Created).Replace("\"version\":1", "\"version\":9", StringComparison.Ordinal);
            var ex = Assert.Throws<ModelStoreException>(() => ModelStore.Load(json));
            Assert.Contains("version 9", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Formula_encoding_matches_the_design_matrix_and_rejects_unseen_levels()
        {
            var (headers, rows) = Table();
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x + C(g)"), Kind(2));
            var bundle = ModelBundle.FromFormula(ModelTypes.LinearModel, ModelTask.Regression, dm, LinearModel.Fit(dm), "ols");
            var binding = ModelStore.Bind(bundle, headers);
            Assert.True(binding.Ready);
            var encoded = new double[dm.ColumnCount];
            for (int i = 0; i < dm.RowCount; i++)
            {
                Assert.True(ModelStore.EncodeRow(binding, rows[dm.ViewRows[i]], encoded, out _));
                for (int j = 0; j < dm.ColumnCount; j++)
                    Assert.Equal(dm.X[i, j], encoded[j]);
            }
            var unseen = ModelStore.Score(bundle, binding, new[] { "1", "0.5", "c", "no" });
            Assert.False(unseen.Scorable);
            Assert.Contains("unseen level", unseen.Reason, StringComparison.Ordinal);
            Assert.True(ModelStore.Score(bundle, binding, new[] { "1", "0.5", "a", "no" }).Scorable);
            var missing = ModelStore.Bind(bundle, new[] { "y", "x" });
            Assert.False(missing.Ready);
            Assert.Contains("g", missing.MissingColumns);
            var loaded = Trip(bundle);
            Same(ModelStore.PredictEncoded(bundle, dm.X), ModelStore.PredictEncoded(loaded, dm.X));
            Assert.IsType<LinearModelFit>(loaded.Engine);
        }

        [Fact]
        public void Logistic_round_trip_keeps_class_and_probability()
        {
            var (headers, rows) = Table();
            var formula = ModelFormula.Parse("cls ~ x + C(g)");
            var dm = DesignMatrixBuilder.Build(rows, headers, formula, Kind(2, 3),
                new DesignMatrixOptions { Response = ResponseKind.Binary });
            var fit = GeneralizedLinearModel.Fit(dm, GlmFamily.Binomial, GlmLink.Logit, logisticExtras: true);
            var bundle = ModelBundle.FromFormula(ModelTypes.Logistic, ModelTask.Classification, dm, fit, "logit");
            var loaded = Trip(bundle);
            Same(ModelStore.PredictEncoded(bundle, dm.X), ModelStore.PredictEncoded(loaded, dm.X));
            var pred = ModelStore.PredictEncoded(loaded, dm.X);
            Assert.NotNull(pred.ClassIndex);
            Assert.Contains(pred.ClassIndex!, v => v is 0 or 1);
            Assert.NotNull(pred.Probability);
            Assert.Equal(2, pred.Probability!.GetLength(1));
        }

        [Fact]
        public void Knn_naive_bayes_and_lda_round_trip()
        {
            var (headers, rows) = Table();
            var kind = Kind(2, 3);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, kind, 3, TargetKind.Categorical);
            var knn = new KnnModel
            {
                TrainX = fm.X,
                Labels = fm.ClassLabels!,
                ClassCount = fm.ClassNames!.Count,
                Neighbors = 3,
            };
            var knnBundle = ModelBundle.FromFeatures(ModelTypes.Knn, ModelTask.Classification, fm, headers, kind, "cls", knn, null, fm.RowCount, "knn");
            var knnLoaded = Trip(knnBundle);
            Same(ModelStore.PredictEncoded(knnBundle, fm.X), ModelStore.PredictEncoded(knnLoaded, fm.X));
            Assert.Equal(knn.Predict(fm.X), ModelStore.PredictEncoded(knnLoaded, fm.X).ClassIndex);

            var groups = FeatureGroups.FromMatrix(fm.SourceColumns, fm.FeatureNames);
            var scaler = FeatureScaler.Fit(fm.X, ScalingMethod.ZScore);
            var bayes = NaiveBayesModel.Fit(scaler.Transform(fm.X), fm.ClassLabels!, fm.ClassNames!.Count, groups);
            var bayesBundle = ModelBundle.FromFeatures(ModelTypes.NaiveBayes, ModelTask.Classification, fm, headers, kind, "cls", bayes, scaler, fm.RowCount, "nb");
            var bayesLoaded = Trip(bayesBundle);
            Same(ModelStore.PredictEncoded(bayesBundle, fm.X), ModelStore.PredictEncoded(bayesLoaded, fm.X));

            var lda = LinearDiscriminant.Fit(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, fm.FeatureNames, fm.ClassNames);
            var ldaBundle = ModelBundle.FromFeatures(ModelTypes.Lda, ModelTask.Classification, fm, headers, kind, "cls", lda, null, fm.RowCount, "lda");
            var ldaLoaded = Trip(ldaBundle);
            Same(ModelStore.PredictEncoded(ldaBundle, fm.X), ModelStore.PredictEncoded(ldaLoaded, fm.X));
            Assert.Equal(lda.Predict(fm.X), Assert.IsType<LinearDiscriminantModel>(ldaLoaded.Engine).Predict(fm.X));
        }

        [Fact]
        public void Trees_forest_svm_and_boosting_round_trip()
        {
            var (headers, rows) = Table();
            var kind = Kind(2, 3);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, kind, 3, TargetKind.Categorical);
            var reg = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, kind, 0, TargetKind.Numeric);
            var treeOpt = new DecisionTreeOptions { MaxDepth = 3, MinSamplesLeaf = 1, Seed = 1 };
            var tree = DecisionTree.FitClassification(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, treeOpt);
            var treeBundle = ModelBundle.FromFeatures(ModelTypes.DecisionTree, ModelTask.Classification, fm, headers, kind, "cls", tree, null, fm.RowCount, "tree");
            var treeLoaded = Trip(treeBundle);
            Same(ModelStore.PredictEncoded(treeBundle, fm.X), ModelStore.PredictEncoded(treeLoaded, fm.X));
            Assert.Equal(tree.Predict(fm.X), Assert.IsType<DecisionTreeModel>(treeLoaded.Engine).Predict(fm.X));

            var regTree = DecisionTree.FitRegression(reg.X, reg.NumericTarget!, treeOpt with { Criterion = TreeCriterion.Mse });
            var regBundle = ModelBundle.FromFeatures(ModelTypes.DecisionTree, ModelTask.Regression, reg, headers, kind, "y", regTree, null, reg.RowCount, "tree-reg");
            Same(ModelStore.PredictEncoded(regBundle, reg.X), ModelStore.PredictEncoded(Trip(regBundle), reg.X));

            var forestOpt = new RandomForestOptions { Trees = 4, MaxDepth = 3, Seed = 1, MinSamplesLeaf = 1 };
            var forest = RandomForest.FitClassification(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, forestOpt);
            var forestBundle = ModelBundle.FromFeatures(ModelTypes.RandomForest, ModelTask.Classification, fm, headers, kind, "cls", forest, null, fm.RowCount, "rf");
            var forestLoaded = Trip(forestBundle);
            Same(ModelStore.PredictEncoded(forestBundle, fm.X), ModelStore.PredictEncoded(forestLoaded, fm.X));
            Assert.Equal(forest.Predict(fm.X), Assert.IsType<RandomForestModel>(forestLoaded.Engine).Predict(fm.X));

            var svm = SupportVectorMachine.Fit(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, new SvmOptions { Kernel = SvmKernel.Linear, C = 1, MaxIterations = 200 });
            var svmBundle = ModelBundle.FromFeatures(ModelTypes.Svm, ModelTask.Classification, fm, headers, kind, "cls", svm, null, fm.RowCount, "svm");
            var svmLoaded = Trip(svmBundle);
            Same(ModelStore.PredictEncoded(svmBundle, fm.X), ModelStore.PredictEncoded(svmLoaded, fm.X));
            Assert.Equal(svm.Predict(fm.X), Assert.IsType<SvmModel>(svmLoaded.Engine).Predict(fm.X));

            var gbOpt = new GradientBoostingOptions { MaxIterations = 6, MaxLeafNodes = 4, MinSamplesLeaf = 1, MaxBins = 8, EarlyStopping = false, Seed = 1 };
            var gb = GradientBoosting.Fit(reg.X, reg.NumericTarget!, gbOpt);
            var gbBundle = ModelBundle.FromFeatures(ModelTypes.GradientBoosting, ModelTask.Regression, reg, headers, kind, "y", gb, null, reg.RowCount, "gb");
            Same(ModelStore.PredictEncoded(gbBundle, reg.X), ModelStore.PredictEncoded(Trip(gbBundle), reg.X));
            var gbCls = GradientBoosting.Fit(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, gbOpt);
            var gbClsBundle = ModelBundle.FromFeatures(ModelTypes.GradientBoosting, ModelTask.Classification, fm, headers, kind, "cls", gbCls, null, fm.RowCount, "gb-cls");
            var gbLoaded = Trip(gbClsBundle);
            Same(ModelStore.PredictEncoded(gbClsBundle, fm.X), ModelStore.PredictEncoded(gbLoaded, fm.X));
            Assert.Equal(gbCls.PredictClasses(fm.X), Assert.IsType<GradientBoostingModel>(gbLoaded.Engine).PredictClasses(fm.X));
        }

        [Fact]
        public void Onnx_refuses_models_without_a_fixed_graph()
        {
            var (headers, rows) = Table(12);
            var kind = Kind(2, 3);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, kind, 3, TargetKind.Categorical);
            var knn = ModelBundle.FromFeatures(ModelTypes.Knn, ModelTask.Classification, fm, headers, kind, "cls",
                new KnnModel { TrainX = fm.X, Labels = fm.ClassLabels!, ClassCount = 2, Neighbors = 1 }, null, fm.RowCount, "knn");
            Assert.False(OnnxExport.TryExport(knn, out _, out var reason));
            Assert.Contains("not exportable to ONNX", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Onnx_graph_uses_documented_ir_and_matches_recorded_runtime_outputs()
        {
            var linear = LinearFixture();
            var linOnnx = OnnxExport.Export(linear);
            var linInfo = OnnxExport.Inspect(linOnnx.Model);
            Assert.Equal(OnnxExport.IrVersion, linInfo.IrVersion);
            Assert.Contains(linInfo.Opsets, o => o.Domain == "ai.onnx.ml" && o.Version == OnnxExport.MlOpset);
            Assert.Contains(linInfo.Opsets, o => o.Domain == "" && o.Version == OnnxExport.OnnxOpset);
            Assert.Contains("LinearRegressor", linInfo.NodeOps);
            Assert.Contains("ai.onnx.ml", linInfo.NodeDomains);
            Assert.Contains("prediction", linInfo.Outputs);
            Assert.Contains("features", linOnnx.SidecarJson, StringComparison.Ordinal);

            var logistic = LogisticFixture();
            var logOnnx = OnnxExport.Export(logistic);
            var logInfo = OnnxExport.Inspect(logOnnx.Model);
            Assert.Contains("MatMul", logInfo.NodeOps);
            Assert.Contains("Sigmoid", logInfo.NodeOps);
            Assert.Contains("GreaterOrEqual", logInfo.NodeOps);
            Assert.Contains("label", logInfo.Outputs);

            var tree = TreeFixture();
            var treeInfo = OnnxExport.Inspect(OnnxExport.Export(tree).Model);
            Assert.Contains("TreeEnsembleClassifier", treeInfo.NodeOps);
            Assert.Contains("ai.onnx.ml", treeInfo.NodeDomains);

            var forest = ForestFixture();
            var forestInfo = OnnxExport.Inspect(OnnxExport.Export(forest).Model);
            Assert.Contains("TreeEnsembleRegressor", forestInfo.NodeOps);

            var boosting = BoostFixture();
            var boostInfo = OnnxExport.Inspect(OnnxExport.Export(boosting).Model);
            Assert.Contains("TreeEnsembleRegressor", boostInfo.NodeOps);

            Assert.Contains("ArgMax", treeInfo.NodeOps);
            // onnxruntime 1.20.1 CPU. sess.run(None, {"features": X.astype("float32")}). 허용 오차 1e-5.
            CloseRecorded(ModelStore.PredictEncoded(linear, linear.Engine is LinearModelFit ? DesignOf(linear) : throw new InvalidOperationException()).Value!, LinearOnnx);
            CloseRecorded(ModelStore.PredictEncoded(logistic, DesignOf(logistic)).Value!, LogisticOnnxProbability);
            CloseRecorded(ClassOf(tree), TreeOnnxLabel);
            CloseRecorded(ModelStore.PredictEncoded(forest, FeatureOf(forest)).Value!, ForestOnnx);
            CloseRecorded(ModelStore.PredictEncoded(boosting, FeatureOf(boosting)).Value!, BoostOnnx);
        }

        // onnxruntime 1.20.1, providers=["CPUExecutionProvider"], input name "features".
        static readonly double[] LinearOnnx =
        {
            1.25, 2.75, 2.75, 4.25, 4.25, 5.75, 5.75, 7.25, 7.25, 8.75, 8.75, 10.25,
            10.25, 11.75, 11.75, 13.25, 13.25, 14.75, 14.75, 16.25, 16.25, 17.75, 17.75, 19.25,
        };
        static readonly double[] LogisticOnnxProbability =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        };
        static readonly int[] TreeOnnxLabel =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        };
        static readonly double[] ForestOnnx =
        {
            3.8333332538604736, 3.8333332538604736, 3.8333332538604736, 3.8333332538604736,
            3.8333332538604736, 3.8333332538604736, 3.8333332538604736, 7.083333492279053,
            8.694443702697754, 8.694443702697754, 8.694443702697754, 8.694443702697754,
            8.694443702697754, 12.541666984558105, 12.541666984558105, 12.541666984558105,
            12.541666984558105, 14.05302906036377, 15.88636302947998, 15.88636302947998,
            15.88636302947998, 17.886362075805664, 17.886362075805664, 17.886362075805664,
        };
        static readonly double[] BoostOnnx =
        {
            8.27495002746582, 8.27495002746582, 8.27495002746582, 8.27495002746582,
            8.27495002746582, 8.27495002746582, 9.129949569702148, 9.129949569702148,
            9.129949569702148, 9.129949569702148, 9.129949569702148, 9.129949569702148,
            11.370050430297852, 11.370050430297852, 11.370050430297852, 11.370050430297852,
            11.370050430297852, 11.370050430297852, 12.22504997253418, 12.22504997253418,
            12.22504997253418, 12.22504997253418, 12.22504997253418, 12.22504997253418,
        };

        static void CloseRecorded(double[] actual, double[] recorded)
        {
            Assert.Equal(recorded.Length, actual.Length);
            for (int i = 0; i < actual.Length; i++)
                Assert.InRange(Math.Abs(actual[i] - recorded[i]), 0, 1e-5);
        }

        static void CloseRecorded(int[] actual, int[] recorded)
        {
            Assert.Equal(recorded, actual);
        }

        static int[] ClassOf(ModelBundle bundle)
        {
            var pred = ModelStore.PredictEncoded(bundle, FeatureOf(bundle));
            return pred.ClassIndex ?? throw new InvalidOperationException("no class");
        }

        static double[,] DesignOf(ModelBundle bundle)
        {
            var (headers, rows) = Table(24);
            var formula = ModelFormula.Parse(bundle.Formula!);
            bool binary = bundle.Task == ModelTask.Classification;
            var dm = DesignMatrixBuilder.Build(rows, headers, formula, Kind(2, 3),
                new DesignMatrixOptions { Response = binary ? ResponseKind.Binary : ResponseKind.Numeric });
            return dm.X;
        }

        static double[,] FeatureOf(ModelBundle bundle)
        {
            var (headers, rows) = Table(24);
            int[] cols = bundle.Features.Count == 1 ? new[] { 1 } : new[] { 1, 2 };
            int target = bundle.Task == ModelTask.Classification ? 3 : 0;
            var fm = FeatureMatrixBuilder.Build(rows, headers, cols, Kind(2, 3), target,
                bundle.Task == ModelTask.Classification ? TargetKind.Categorical : TargetKind.Numeric);
            return fm.X;
        }

        static ModelBundle LinearFixture()
        {
            var (headers, rows) = Table(24);
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x + C(g)"), Kind(2, 3));
            return ModelBundle.FromFormula(ModelTypes.LinearModel, ModelTask.Regression, dm, LinearModel.Fit(dm), "ols");
        }

        static ModelBundle LogisticFixture()
        {
            var (headers, rows) = Table(24);
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("cls ~ x + C(g)"), Kind(2, 3),
                new DesignMatrixOptions { Response = ResponseKind.Binary });
            var fit = GeneralizedLinearModel.Fit(dm, GlmFamily.Binomial, GlmLink.Logit);
            return ModelBundle.FromFormula(ModelTypes.Logistic, ModelTask.Classification, dm, fit, "logit");
        }

        static ModelBundle TreeFixture()
        {
            var (headers, rows) = Table(24);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, Kind(2, 3), 3, TargetKind.Categorical);
            var tree = DecisionTree.FitClassification(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, new DecisionTreeOptions { MaxDepth = 2, MinSamplesLeaf = 1 });
            return ModelBundle.FromFeatures(ModelTypes.DecisionTree, ModelTask.Classification, fm, headers, Kind(2, 3), "cls", tree, null, fm.RowCount, "tree");
        }

        static ModelBundle ForestFixture()
        {
            var (headers, rows) = Table(24);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1 }, Kind(2, 3), 0, TargetKind.Numeric);
            var forest = RandomForest.FitRegression(fm.X, fm.NumericTarget!, new RandomForestOptions { Trees = 3, MaxDepth = 2, Seed = 1, Criterion = TreeCriterion.Mse });
            return ModelBundle.FromFeatures(ModelTypes.RandomForest, ModelTask.Regression, fm, headers, Kind(2, 3), "y", forest, null, fm.RowCount, "rf");
        }

        static ModelBundle BoostFixture()
        {
            var (headers, rows) = Table(24);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1 }, Kind(2, 3), 0, TargetKind.Numeric);
            var gb = GradientBoosting.Fit(fm.X, fm.NumericTarget!, new GradientBoostingOptions
            {
                MaxIterations = 4, MaxLeafNodes = 3, MinSamplesLeaf = 1, MaxBins = 4, EarlyStopping = false, Seed = 1,
            });
            return ModelBundle.FromFeatures(ModelTypes.GradientBoosting, ModelTask.Regression, fm, headers, Kind(2, 3), "y", gb, null, fm.RowCount, "gb");
        }

        [Fact]
        public void AdaBoost_and_linear_score_round_trip()
        {
            var (headers, rows) = Table();
            var kind = Kind(2, 3);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, kind, 3, TargetKind.Categorical);
            var ada = AdaBoost.FitClassification(fm.X, fm.ClassLabels!, fm.ClassNames!.Count,
                new AdaBoostOptions { Estimators = 8, MaxDepth = 1, Seed = 1, LearningRate = 0.8 });
            var adaBundle = ModelBundle.FromFeatures(ModelTypes.AdaBoost, ModelTask.Classification, fm, headers, kind, "cls", ada, null, ada.RowsFit, "ada");
            var loaded = Trip(adaBundle);
            Same(ModelStore.PredictEncoded(adaBundle, fm.X), ModelStore.PredictEncoded(loaded, fm.X));
            Assert.Equal(ada.Predict(fm.X), Assert.IsType<AdaBoostModel>(loaded.Engine).Predict(fm.X));

            var reg = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, kind, 0, TargetKind.Numeric);
            var adaReg = AdaBoost.FitRegression(reg.X, reg.NumericTarget!, new AdaBoostOptions { Estimators = 6, MaxDepth = 2, Seed = 1, Loss = AdaBoostLoss.Linear });
            var regBundle = ModelBundle.FromFeatures(ModelTypes.AdaBoost, ModelTask.Regression, reg, headers, kind, "y", adaReg, null, adaReg.RowsFit, "ada-reg");
            Same(ModelStore.PredictEncoded(regBundle, reg.X), ModelStore.PredictEncoded(Trip(regBundle), reg.X));
            Assert.False(OnnxExport.TryExport(regBundle, out _, out var refused));
            Assert.Contains("not exportable to ONNX", refused, StringComparison.Ordinal);
            Assert.Contains("median", refused, StringComparison.OrdinalIgnoreCase);

            var xs = FeatureMatrixBuilder.Build(rows, headers, new[] { 1 }, kind, 0, TargetKind.Numeric);
            var score = new LinearScoreModel { Coefficients = new[] { 0.25, 1.5 }, Logistic = false };
            var scoreBundle = ModelBundle.FromFeatures(ModelTypes.LinearModel, ModelTask.Regression, xs, headers, kind, "y", score, null, xs.RowCount, "score");
            var scoreLoaded = Trip(scoreBundle);
            Same(ModelStore.PredictEncoded(scoreBundle, xs.X), ModelStore.PredictEncoded(scoreLoaded, xs.X));
            Assert.IsType<LinearScoreModel>(scoreLoaded.Engine);

            var logit = new LinearScoreModel { Coefficients = new[] { -0.4, 0.8 }, Logistic = true };
            var logitBundle = ModelBundle.FromFeatures(ModelTypes.Logistic, ModelTask.Classification, xs, headers, kind, "y", logit, null, xs.RowCount, "logit-score");
            var logitLoaded = Trip(logitBundle);
            Same(ModelStore.PredictEncoded(logitBundle, xs.X), ModelStore.PredictEncoded(logitLoaded, xs.X));
        }

        // onnxruntime 1.20.1 CPU, sess.run(None, {"features": X.astype("float32")}) on the exported graphs.
        [Fact]
        public void Onnx_adaboost_and_linear_score_match_recorded_runtime_outputs()
        {
            var (headers, rows) = Table(16);
            var kind = Kind(2, 3);
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1 }, kind, 3, TargetKind.Categorical);
            var ada = AdaBoost.FitClassification(fm.X, fm.ClassLabels!, fm.ClassNames!.Count,
                new AdaBoostOptions { Estimators = 6, MaxDepth = 1, Seed = 1, LearningRate = 1 });
            var adaBundle = ModelBundle.FromFeatures(ModelTypes.AdaBoost, ModelTask.Classification, fm, headers, kind, "cls", ada, null, ada.RowsFit, "ada");
            var adaInfo = OnnxExport.Inspect(OnnxExport.Export(adaBundle).Model);
            Assert.Contains("TreeEnsembleClassifier", adaInfo.NodeOps);
            Assert.Contains("scores", adaInfo.Outputs);          // SAMME 점수는 확률이라 부르지 않는다
            Assert.DoesNotContain("probabilities", adaInfo.Outputs);
            CloseRecorded(ModelStore.PredictEncoded(adaBundle, fm.X).ClassIndex!, AdaOnnxLabel);

            var xs = FeatureMatrixBuilder.Build(rows, headers, new[] { 1 }, kind, 0, TargetKind.Numeric);
            var score = new LinearScoreModel { Coefficients = new[] { 0.25, 1.5 }, Logistic = false };
            var scoreBundle = ModelBundle.FromFeatures(ModelTypes.LinearModel, ModelTask.Regression, xs, headers, kind, "y", score, null, xs.RowCount, "score");
            CloseRecorded(ModelStore.PredictEncoded(scoreBundle, xs.X).Value!, ScoreOnnx);

            var logit = new LinearScoreModel { Coefficients = new[] { -0.4, 0.8 }, Logistic = true };
            var logitBundle = ModelBundle.FromFeatures(ModelTypes.Logistic, ModelTask.Classification, xs, headers, kind, "y", logit, null, xs.RowCount, "logit-score");
            var logitPred = ModelStore.PredictEncoded(logitBundle, xs.X);
            CloseRecorded(logitPred.ClassIndex!, LogitOnnxLabel);
            var p1 = new double[logitPred.Count];
            for (int i = 0; i < p1.Length; i++) p1[i] = logitPred.Probability![i, 1];
            CloseRecorded(p1, LogitOnnxProbability);
        }

        static readonly int[] AdaOnnxLabel = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1 };
        static readonly double[] ScoreOnnx = { 0.25, 1.0, 1.75, 2.5, 3.25, 4.0, 4.75, 5.5, 6.25, 7.0, 7.75, 8.5, 9.25, 10.0, 10.75, 11.5 };
        static readonly int[] LogitOnnxLabel = { 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        static readonly double[] LogitOnnxProbability =
        {
            0.40131235122680664, 0.5, 0.5986876487731934, 0.6899744868278503, 0.7685247659683228, 0.8320183753967285,
            0.8807970881462097, 0.9168273210525513, 0.9426758289337158, 0.960834264755249, 0.9734029769897461,
            0.9820137619972229, 0.987871527671814, 0.9918375015258789, 0.994513750076294, 0.9963157176971436,
        };

        [Fact]
        public void Loader_rejects_oversized_or_inconsistent_payloads_without_allocating_them()
        {
            var ex = Assert.Throws<ModelStoreException>(() => ModelStore.RequireFileSize(ModelStore.MaxPayloadBytes + 1));
            Assert.Contains("not loaded", ex.Message, StringComparison.OrdinalIgnoreCase);
            ModelStore.RequireFileSize(32);

            string json = """
                {"format":"nanum-model","version":1,"appVersion":"1.18.0","createdAt":"2026-09-30T00:00:00.0000000Z","modelType":"Lda","task":"Classification","target":"y","features":[{"column":"x","kind":"Numeric"}],"engine":{"type":"Lda","lda":{"classCount":2,"featureCount":1,"meansRows":536870913,"meansCols":1,"classMeans":"AAAAAAAAAAA=","priors":"AAAAAAAAAAAAAAAAAAAAAA=="}}}
                """;
            var huge = Assert.Throws<ModelStoreException>(() => ModelStore.Load(json));
            Assert.Contains("not loaded", huge.Message, StringComparison.OrdinalIgnoreCase);

            string negative = json.Replace("\"meansRows\":536870913", "\"meansRows\":-1", StringComparison.Ordinal);
            var neg = Assert.Throws<ModelStoreException>(() => ModelStore.Load(negative));
            Assert.Contains("not loaded", neg.Message, StringComparison.OrdinalIgnoreCase);
        }

    }
}
