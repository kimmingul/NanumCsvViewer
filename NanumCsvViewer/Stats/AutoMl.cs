using System.Diagnostics;

namespace NanumCsvViewer.Stats
{
    public enum AutoMlMetric { Accuracy, MacroF1, Rmse, RSquared }

    public enum AutoMlSkip { LogisticNeedsBinary, SvmTooLarge }

    /// <summary>
    /// 학습 분할 안에서만 탐색한다. 시험 행은 탐색·교차검증에 넣지 않는다.
    /// 시간 예산 0은 첫 설정만 적합하고 다음 설정은 시작하지 않는다.
    /// </summary>
    public sealed record AutoMlOptions
    {
        public AutoMlMetric Metric { get; init; } = AutoMlMetric.Accuracy;
        public int Folds { get; init; } = 3;
        public int Seed { get; init; } = 1;
        public double TestFraction { get; init; } = 0.3;
        /// <summary>초. 0이면 첫 설정만.</summary>
        public double TimeBudgetSeconds { get; init; } = 30;
        /// <summary>탐색에 쓰는 학습 행 상한. 넘으면 시드 고정 표본.</summary>
        public int SearchRowBudget { get; init; } = 2_000;
        /// <summary>시험 지표용 재적합·저장 모형의 행 상한. 선형·로지스틱은 상한을 쓰지 않는다.</summary>
        public int RefitRowCap { get; init; } = 40_000;
    }

    public sealed class AutoMlTrial
    {
        public required string Name { get; init; }
        public required string ModelType { get; init; }
        public required double Mean { get; init; }
        public required double Sd { get; init; }
        public required double Seconds { get; init; }
        public required int Order { get; init; }
    }

    public sealed class AutoMlFailure
    {
        public required string Name { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>절편 포함 선형·로지스틱. 계수의 NaN(별칭)은 0으로 둔다.</summary>
    public sealed class LinearScoreModel
    {
        public required double[] Coefficients { get; init; }
        public required bool Logistic { get; init; }

        public double[] PredictValues(double[,] x, CancellationToken cancellation = default)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (Coefficients.Length != p + 1)
                throw new ArgumentException("Coefficient length must be features + intercept.", nameof(x));
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                double s = Coef(0);
                for (int j = 0; j < p; j++) s += Coef(j + 1) * x[i, j];
                y[i] = Logistic ? Sigmoid(s) : s;
            }
            return y;
        }

        public int[] PredictClasses(double[,] x, CancellationToken cancellation = default)
        {
            var p = PredictValues(x, cancellation);
            var c = new int[p.Length];
            for (int i = 0; i < p.Length; i++) c[i] = p[i] >= GeneralizedLinearModel.LogisticThreshold ? 1 : 0;
            return c;
        }

        double Coef(int j) => double.IsFinite(Coefficients[j]) ? Coefficients[j] : 0;

        static double Sigmoid(double z)
        {
            if (z >= 0)
            {
                double e = Math.Exp(-z);
                return 1.0 / (1.0 + e);
            }
            double ep = Math.Exp(z);
            return ep / (1.0 + ep);
        }
    }



    public sealed class AutoMlReport
    {
        public required bool Regression { get; init; }
        public required AutoMlMetric Metric { get; init; }
        public required bool HigherIsBetter { get; init; }
        public required IReadOnlyList<AutoMlTrial> Leaderboard { get; init; }
        public required IReadOnlyList<AutoMlFailure> Failures { get; init; }
        public required IReadOnlyList<AutoMlSkip> Skipped { get; init; }
        public required string BestName { get; init; }
        public required string BestModelType { get; init; }
        /// <summary>시험 지표를 낸 모형. 학습 분할(또는 그 상한 표본)만 적합.</summary>
        public required object EvaluationEngine { get; init; }
        public FeatureScaler? EvaluationScaler { get; init; }
        /// <summary>저장용. 사용 행 전체(또는 상한 표본)로 다시 적합. 시험 지표 모형이 아니다.</summary>
        public required object BundleEngine { get; init; }
        public FeatureScaler? BundleScaler { get; init; }
        public required int BundleRows { get; init; }
        public required bool BundleSampled { get; init; }
        public required bool BundleUsesAllRows { get; init; }
        /// <summary>원본 행렬 인덱스. 탐색에 쓴 행. 시험 행과 겹치지 않는다.</summary>
        public required int[] SearchRows { get; init; }
        public required int[] TestRows { get; init; }
        public required int[] TrainRows { get; init; }
        /// <summary>시험 지표 모형을 적합한 행. 시험 행과 겹치지 않는다.</summary>
        public required int[] EvaluationRows { get; init; }
        public required bool SearchSampled { get; init; }
        public required bool EvaluationSampled { get; init; }
        public required bool BudgetStopped { get; init; }
        public required int ConfigsTried { get; init; }
        public required int ConfigsPlanned { get; init; }
        public required double TestScore { get; init; }
        public required double BaselineScore { get; init; }
        public ClassificationMetrics? TestClassification { get; init; }
        public RegressionMetrics? TestRegression { get; init; }
        public required int Folds { get; init; }
        public required int Seed { get; init; }
        public required double TestFraction { get; init; }
        public required double TimeBudgetSeconds { get; init; }
        public required int SearchRowBudget { get; init; }
        public required double ElapsedSeconds { get; init; }
    }

    /// <summary>
    /// 기존 학습기 작은 격자 탐색. 홀드아웃 시험 행은 탐색에 쓰지 않고, 층화 k-겹은 학습 분할(표본)에서만 돈다.
    /// 최고 설정을 학습 분할에 다시 적합해 시험 지표를 내고, 저장 모형은 사용 행 전체(상한이면 시드 고정 표본)로 다시 적합한다.
    /// </summary>
    public static class AutoMl
    {
        public const int SvmSearchRowCap = 800;
        public const int DefaultSearchRows = 2_000;
        public const int DefaultRefitCap = 40_000;

        public static AutoMlReport Search(double[,] x, int[] labels, int classCount, AutoMlOptions? options = null, CancellationToken cancellation = default)
        {
            var opt = options ?? new AutoMlOptions();
            Validate(x, opt, regression: false);
            if (labels.Length != x.GetLength(0)) throw new ArgumentException("Label length must match rows.", nameof(labels));
            if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            if (opt.Metric is AutoMlMetric.Rmse or AutoMlMetric.RSquared)
                throw new DesignMatrixException("Accuracy or macro F1 is the classification metric. RMSE and R² are for a numeric target.");
            return Run(x, labels, null, classCount, false, opt, cancellation);
        }

        public static AutoMlReport Search(double[,] x, double[] y, AutoMlOptions? options = null, CancellationToken cancellation = default)
        {
            var opt = options ?? new AutoMlOptions { Metric = AutoMlMetric.Rmse };
            Validate(x, opt, regression: true);
            if (y.Length != x.GetLength(0)) throw new ArgumentException("Target length must match rows.", nameof(y));
            if (opt.Metric is AutoMlMetric.Accuracy or AutoMlMetric.MacroF1)
                throw new DesignMatrixException("RMSE or R² is the regression metric. Accuracy and macro F1 are for a class target.");
            for (int i = 0; i < y.Length; i++)
                if (!double.IsFinite(y[i]))
                    throw new DesignMatrixException("Regression target has a non-finite value.");
            return Run(x, null, y, 0, true, opt, cancellation);
        }

        static AutoMlReport Run(double[,] x, int[]? labels, double[]? y, int classCount, bool regression, AutoMlOptions opt, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            var dummy = labels ?? new int[n];
            var outer = ClassifierEvaluation.Holdout(dummy, opt.TestFraction, opt.Seed, stratified: !regression);
            var train = outer.Train;
            var test = outer.Test;
            bool searchSampled = false;
            int[] search = train;
            if (train.Length > opt.SearchRowBudget)
            {
                searchSampled = true;
                search = regression
                    ? Map(train, RowSample.Random(train.Length, opt.SearchRowBudget, opt.Seed, out _))
                    : Map(train, RowSample.Stratified(LabelsOf(labels!, train), opt.SearchRowBudget, opt.Seed, out _));
            }
            var searchX = AdaBoost.TakeRows(x, search);
            int[]? searchY = regression ? null : LabelsOf(labels!, search);
            double[]? searchReg = regression ? ValuesOf(y!, search) : null;
            var specs = Specs(regression, classCount, search.Length, x.GetLength(1));
            var skipped = new List<AutoMlSkip>();
            if (!regression && classCount != 2) skipped.Add(AutoMlSkip.LogisticNeedsBinary);
            if (!regression && search.Length > SvmSearchRowCap) skipped.Add(AutoMlSkip.SvmTooLarge);

            var clock = Stopwatch.StartNew();
            var successes = new List<AutoMlTrial>();
            var failures = new List<AutoMlFailure>();
            int tried = 0;
            bool budgetStopped = false;
            Spec? bestSpec = null;
            double bestScore = double.NaN;
            for (int s = 0; s < specs.Count; s++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (tried > 0 && clock.Elapsed.TotalSeconds >= opt.TimeBudgetSeconds)
                {
                    budgetStopped = true;
                    break;
                }
                var spec = specs[s];
                tried++;
                var sw = Stopwatch.StartNew();
                try
                {
                    var folds = regression
                        ? ClassifierEvaluation.KFold(new int[search.Length], opt.Folds, opt.Seed, stratified: false)
                        : ClassifierEvaluation.KFold(searchY!, opt.Folds, opt.Seed, stratified: true);
                    var scores = new double[folds.Count];
                    for (int f = 0; f < folds.Count; f++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var split = folds[f];
                        if (split.Train.Length == 0 || split.Test.Length == 0)
                            throw new DesignMatrixException("A cross-validation fold is empty. Use fewer folds or more rows.");
                        var fitted = spec.Fit(searchX, searchY, searchReg, split.Train, classCount, opt.Seed, cancellation);
                        if (regression)
                        {
                            var actual = TakeDouble(searchReg!, split.Test);
                            var pred = fitted.PredictValue(AdaBoost.TakeRows(searchX, split.Test), cancellation);
                            scores[f] = RegScore(opt.Metric, actual, pred);
                        }
                        else
                        {
                            var actual = TakeInt(searchY!, split.Test);
                            var pred = fitted.PredictClass(AdaBoost.TakeRows(searchX, split.Test), cancellation);
                            scores[f] = ClassScore(opt.Metric, actual, pred, classCount);
                        }
                    }
                    double mean = 0;
                    for (int f = 0; f < scores.Length; f++) mean += scores[f];
                    mean /= scores.Length;
                    double var = 0;
                    for (int f = 0; f < scores.Length; f++)
                    {
                        double d = scores[f] - mean;
                        var += d * d;
                    }
                    double sd = scores.Length > 1 ? Math.Sqrt(var / (scores.Length - 1)) : 0;
                    sw.Stop();
                    successes.Add(new AutoMlTrial
                    {
                        Name = spec.Name,
                        ModelType = spec.ModelType,
                        Mean = mean,
                        Sd = sd,
                        Seconds = sw.Elapsed.TotalSeconds,
                        Order = s,
                    });
                    if (bestSpec is null || Better(mean, bestScore, opt.Metric))
                    {
                        bestSpec = spec;
                        bestScore = mean;
                    }
                }
                catch (Exception ex) when (ex is DesignMatrixException or ArgumentException or InvalidOperationException)
                {
                    sw.Stop();
                    failures.Add(new AutoMlFailure { Name = spec.Name, Message = ex.Message });
                }
            }
            if (bestSpec is null)
                throw new DesignMatrixException(failures.Count == 0
                    ? "AutoML did not fit any configuration."
                    : "Every AutoML configuration failed. " + failures[0].Message);

            bool higher = opt.Metric != AutoMlMetric.Rmse;
            successes.Sort((a, b) =>
            {
                int c = higher ? b.Mean.CompareTo(a.Mean) : a.Mean.CompareTo(b.Mean);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Name, b.Name);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
            bestSpec = specs.First(s => s.Name == successes[0].Name);
            bool evalSampled = false;
            int[] evalRows = train;
            if (!bestSpec.UnboundedRefit && train.Length > opt.RefitRowCap)
            {
                evalSampled = true;
                evalRows = regression
                    ? Map(train, RowSample.Random(train.Length, opt.RefitRowCap, opt.Seed, out _))
                    : Map(train, RowSample.Stratified(LabelsOf(labels!, train), opt.RefitRowCap, opt.Seed, out _));
            }
            var evalFit = bestSpec.Fit(x, labels, y, evalRows, classCount, opt.Seed, cancellation);
            double testScore;
            double baseline;
            ClassificationMetrics? testCls = null;
            RegressionMetrics? testReg = null;
            var testX = AdaBoost.TakeRows(x, test);
            if (regression)
            {
                var actual = ValuesOf(y!, test);
                var pred = evalFit.PredictValue(testX, cancellation);
                testReg = RegressionMetrics.From(actual, pred);
                testScore = opt.Metric == AutoMlMetric.RSquared ? testReg.RSquared : testReg.Rmse;
                var trainY = ValuesOf(y!, evalRows);
                double mean = 0;
                for (int i = 0; i < trainY.Length; i++) mean += trainY[i];
                mean /= trainY.Length;
                var basePred = new double[actual.Length];
                for (int i = 0; i < basePred.Length; i++) basePred[i] = mean;
                var baseM = RegressionMetrics.From(actual, basePred);
                baseline = opt.Metric == AutoMlMetric.RSquared ? baseM.RSquared : baseM.Rmse;
            }
            else
            {
                var actual = LabelsOf(labels!, test);
                var pred = evalFit.PredictClass(testX, cancellation);
                testCls = ClassifierEvaluation.Metrics(actual, pred, classCount);
                testScore = opt.Metric == AutoMlMetric.MacroF1 ? testCls.MacroF1 : testCls.Accuracy;
                var trainLab = LabelsOf(labels!, evalRows);
                int majority = Majority(trainLab, classCount);
                var basePred = new int[actual.Length];
                for (int i = 0; i < basePred.Length; i++) basePred[i] = majority;
                var baseM = ClassifierEvaluation.Metrics(actual, basePred, classCount);
                baseline = opt.Metric == AutoMlMetric.MacroF1 ? baseM.MacroF1 : baseM.Accuracy;
            }

            bool bundleSampled = false;
            int[] bundleRows = DecisionTree.Identity(n);
            if (!bestSpec.UnboundedRefit && n > opt.RefitRowCap)
            {
                bundleSampled = true;
                bundleRows = regression
                    ? RowSample.Random(n, opt.RefitRowCap, opt.Seed, out _)
                    : RowSample.Stratified(labels!, opt.RefitRowCap, opt.Seed, out _);
            }
            var bundle = bestSpec.Fit(x, labels, y, bundleRows, classCount, opt.Seed, cancellation);
            clock.Stop();
            return new AutoMlReport
            {
                Regression = regression,
                Metric = opt.Metric,
                HigherIsBetter = higher,
                Leaderboard = successes,
                Failures = failures,
                Skipped = skipped,
                BestName = bestSpec.Name,
                BestModelType = bestSpec.ModelType,
                EvaluationEngine = evalFit.Engine,
                EvaluationScaler = evalFit.Scaler,
                BundleEngine = bundle.Engine,
                BundleScaler = bundle.Scaler,
                BundleRows = bundleRows.Length,
                BundleSampled = bundleSampled,
                BundleUsesAllRows = !bundleSampled,
                SearchRows = search,
                TestRows = test,
                TrainRows = train,
                EvaluationRows = evalRows,
                SearchSampled = searchSampled,
                EvaluationSampled = evalSampled,
                BudgetStopped = budgetStopped,
                ConfigsTried = tried,
                ConfigsPlanned = specs.Count,
                TestScore = testScore,
                BaselineScore = baseline,
                TestClassification = testCls,
                TestRegression = testReg,
                Folds = opt.Folds,
                Seed = opt.Seed,
                TestFraction = opt.TestFraction,
                TimeBudgetSeconds = opt.TimeBudgetSeconds,
                SearchRowBudget = opt.SearchRowBudget,
                ElapsedSeconds = clock.Elapsed.TotalSeconds,
            };
        }

        sealed class Spec
        {
            public required string Name { get; init; }
            public required string ModelType { get; init; }
            public bool Scale { get; init; }
            public bool UnboundedRefit { get; init; }
            public required Func<double[,], int[]?, double[]?, int[], int, int, CancellationToken, Fitted> Fit { get; init; }
        }

        sealed class Fitted
        {
            public required object Engine { get; init; }
            public FeatureScaler? Scaler { get; init; }
            public required Func<double[,], CancellationToken, int[]> PredictClass { get; init; }
            public required Func<double[,], CancellationToken, double[]> PredictValue { get; init; }
        }

        static List<Spec> Specs(bool regression, int classCount, int searchRows, int features)
        {
            var list = new List<Spec>();
            if (regression)
            {
                list.Add(Linear(logistic: false));
                list.Add(Tree(depth: 2, regression: true));
                list.Add(Tree(depth: 4, regression: true));
                list.Add(Forest(regression: true));
                list.Add(Boost(regression: true));
                list.Add(Boosted(regression: true, loss: AdaBoostLoss.Linear, rate: 1));
                list.Add(Boosted(regression: true, loss: AdaBoostLoss.Square, rate: 0.8));
                return list;
            }
            if (classCount == 2) list.Add(Linear(logistic: true));
            list.Add(Bayes());
            list.Add(Lda());
            list.Add(Knn(3));
            list.Add(Knn(5));
            list.Add(Tree(2, false));
            list.Add(Tree(4, false));
            list.Add(Forest(false));
            list.Add(Boost(false));
            list.Add(Boosted(false, AdaBoostLoss.Linear, 1));
            list.Add(Boosted(false, AdaBoostLoss.Linear, 0.5));
            if (searchRows <= SvmSearchRowCap && features <= 40) list.Add(Svm());
            return list;
        }

        static Spec Linear(bool logistic) => new()
        {
            Name = logistic ? "Logistic" : "Linear",
            ModelType = logistic ? ModelTypes.Logistic : ModelTypes.LinearModel,
            Scale = true,
            UnboundedRefit = true,
            Fit = (x, labels, y, rows, classCount, _, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var scaler = FeatureScaler.Fit(raw, ScalingMethod.ZScore);
                var sx = scaler.Transform(raw);
                int n = sx.GetLength(0), p = sx.GetLength(1);
                var design = new double[n, p + 1];
                for (int i = 0; i < n; i++)
                {
                    design[i, 0] = 1;
                    for (int j = 0; j < p; j++) design[i, j + 1] = sx[i, j];
                }
                double[] beta;
                if (logistic)
                {
                    var yy = new double[n];
                    for (int i = 0; i < n; i++) yy[i] = labels![rows[i]];
                    var fit = GeneralizedLinearModel.Fit(design, yy, GlmFamily.Binomial, GlmLink.Logit, hasIntercept: true, cancellation: ct);
                    beta = (double[])fit.Coefficients.Clone();
                    for (int j = 0; j < fit.Aliased.Length && j < beta.Length; j++)
                        if (fit.Aliased[j]) beta[j] = 0;
                }
                else
                {
                    var yy = new double[n];
                    for (int i = 0; i < n; i++) yy[i] = y![rows[i]];
                    var fit = LeastSquares.Fit(design, yy, cancellation: ct);
                    beta = (double[])fit.Beta.Clone();
                    for (int j = 0; j < fit.Aliased.Length && j < beta.Length; j++)
                        if (fit.Aliased[j]) beta[j] = 0;
                }
                var model = new LinearScoreModel { Coefficients = beta, Logistic = logistic };
                return Predictor(model, scaler, logistic
                    ? (z, token) => model.PredictClasses(scaler.Transform(z), token)
                    : null,
                    (z, token) => model.PredictValues(scaler.Transform(z), token));
            },
        };

        static Spec Bayes() => new()
        {
            Name = "NaiveBayes",
            ModelType = ModelTypes.NaiveBayes,
            Scale = true,
            Fit = (x, labels, _, rows, classCount, _, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var scaler = FeatureScaler.Fit(raw, ScalingMethod.ZScore);
                var sx = scaler.Transform(raw);
                int p = sx.GetLength(1);
                var names = new string[p];
                var src = new int[p];
                for (int j = 0; j < p; j++) { names[j] = "x" + j.ToString(System.Globalization.CultureInfo.InvariantCulture); src[j] = j; }
                var groups = FeatureGroups.FromMatrix(src, names);
                var yy = TakeInt(labels!, rows);
                var model = NaiveBayesModel.Fit(sx, yy, classCount, groups, cancellation: ct);
                return Predictor(model, scaler, (z, token) => model.Predict(scaler.Transform(z), token), null);
            },
        };

        static Spec Lda() => new()
        {
            Name = "LDA",
            ModelType = ModelTypes.Lda,
            Scale = true,
            Fit = (x, labels, _, rows, classCount, _, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var scaler = FeatureScaler.Fit(raw, ScalingMethod.ZScore);
                var sx = scaler.Transform(raw);
                var model = LinearDiscriminant.Fit(sx, TakeInt(labels!, rows), classCount, cancellation: ct);
                return Predictor(model, scaler, (z, token) => model.Predict(scaler.Transform(z)), null);
            },
        };

        static Spec Knn(int k) => new()
        {
            Name = "KNN-" + k.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ModelType = ModelTypes.Knn,
            Scale = true,
            Fit = (x, labels, _, rows, classCount, _, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var scaler = FeatureScaler.Fit(raw, ScalingMethod.ZScore);
                var sx = scaler.Transform(raw);
                var yy = TakeInt(labels!, rows);
                if (k > yy.Length) throw new DesignMatrixException($"k ({k}) must be between 1 and the training row count ({yy.Length}).");
                var model = new KnnModel { TrainX = sx, Labels = yy, ClassCount = classCount, Neighbors = k };
                return Predictor(model, scaler, (z, token) => model.Predict(scaler.Transform(z), token), null);
            },
        };

        static Spec Tree(int depth, bool regression) => new()
        {
            Name = regression ? "Tree-d" + depth : "Tree-d" + depth,
            ModelType = ModelTypes.DecisionTree,
            Fit = (x, labels, y, rows, classCount, seed, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var tree = new DecisionTreeOptions
                {
                    MaxDepth = depth,
                    Criterion = regression ? TreeCriterion.Mse : TreeCriterion.Gini,
                    SplitMode = TreeSplitMode.Exact,
                    Seed = seed,
                };
                if (regression)
                {
                    var model = DecisionTree.FitRegression(raw, TakeDouble(y!, rows), tree, ct);
                    return Predictor(model, null, null, (z, token) => model.PredictValue(z, token));
                }
                var cls = DecisionTree.FitClassification(raw, TakeInt(labels!, rows), classCount, tree, ct);
                return Predictor(cls, null, (z, token) => cls.Predict(z, token), null);
            },
        };

        static Spec Forest(bool regression) => new()
        {
            Name = "RandomForest",
            ModelType = ModelTypes.RandomForest,
            Fit = (x, labels, y, rows, classCount, seed, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var opt = new RandomForestOptions
                {
                    Trees = 12,
                    MaxDepth = 4,
                    Seed = seed,
                    SplitMode = TreeSplitMode.Exact,
                    MaxTrainingRows = Math.Max(raw.GetLength(0), 2),
                    Criterion = regression ? TreeCriterion.Mse : TreeCriterion.Gini,
                };
                if (regression)
                {
                    var model = RandomForest.FitRegression(raw, TakeDouble(y!, rows), opt, ct);
                    return Predictor(model, null, null, (z, token) => model.PredictValue(z, token));
                }
                var cls = RandomForest.FitClassification(raw, TakeInt(labels!, rows), classCount, opt, ct);
                return Predictor(cls, null, (z, token) => cls.Predict(z, token), null);
            },
        };

        static Spec Boost(bool regression) => new()
        {
            Name = "GradientBoosting",
            ModelType = ModelTypes.GradientBoosting,
            Fit = (x, labels, y, rows, classCount, seed, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var opt = new GradientBoostingOptions
                {
                    MaxIterations = 20,
                    LearningRate = 0.1,
                    MaxDepth = 3,
                    MaxLeafNodes = 8,
                    MinSamplesLeaf = 2,
                    EarlyStopping = false,
                    Seed = seed,
                };
                if (regression)
                {
                    var model = GradientBoosting.Fit(raw, TakeDouble(y!, rows), opt, ct);
                    return Predictor(model, null, null, (z, token) => model.PredictValues(z, token));
                }
                var cls = GradientBoosting.Fit(raw, TakeInt(labels!, rows), classCount, opt, ct);
                return Predictor(cls, null, (z, token) => cls.PredictClasses(z, token), null);
            },
        };

        static Spec Boosted(bool regression, AdaBoostLoss loss, double rate) => new()
        {
            Name = regression
                ? "AdaBoost-" + loss.ToString()
                : "AdaBoost-lr" + rate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            ModelType = AdaBoost.ModelTypeName,
            Fit = (x, labels, y, rows, classCount, seed, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var opt = new AdaBoostOptions
                {
                    Estimators = 12,
                    LearningRate = rate,
                    MaxDepth = regression ? 2 : 1,
                    Seed = seed,
                    Loss = loss,
                    MaxTrainingRows = Math.Max(raw.GetLength(0), 2),
                };
                if (regression)
                {
                    var model = AdaBoost.FitRegression(raw, TakeDouble(y!, rows), opt, ct);
                    return Predictor(model, null, null, (z, token) => model.PredictValue(z, token));
                }
                var cls = AdaBoost.FitClassification(raw, TakeInt(labels!, rows), classCount, opt, ct);
                return Predictor(cls, null, (z, token) => cls.Predict(z, token), null);
            },
        };

        static Spec Svm() => new()
        {
            Name = "SVM-linear",
            ModelType = ModelTypes.Svm,
            Scale = true,
            Fit = (x, labels, _, rows, classCount, seed, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var scaler = FeatureScaler.Fit(raw, ScalingMethod.ZScore);
                var sx = scaler.Transform(raw);
                var opt = new SvmOptions
                {
                    Kernel = SvmKernel.Linear,
                    C = 1,
                    Seed = seed,
                    MaxIterations = 200,
                    LinearEpochs = 12,
                    MaxTrainingRows = Math.Max(sx.GetLength(0), 2),
                };
                var model = SupportVectorMachine.Fit(sx, TakeInt(labels!, rows), classCount, opt, ct);
                return Predictor(model, scaler, (z, token) => model.Predict(scaler.Transform(z), token), null);
            },
        };

        static Fitted Predictor(object engine, FeatureScaler? scaler, Func<double[,], CancellationToken, int[]>? cls, Func<double[,], CancellationToken, double[]>? val)
            => new()
            {
                Engine = engine,
                Scaler = scaler,
                PredictClass = cls ?? ((_, _) => throw new InvalidOperationException("This configuration is not a classifier.")),
                PredictValue = val ?? ((_, _) => throw new InvalidOperationException("This configuration is not a regressor.")),
            };

        static bool Better(double candidate, double best, AutoMlMetric metric)
            => metric == AutoMlMetric.Rmse ? candidate < best : candidate > best;

        static double ClassScore(AutoMlMetric metric, int[] actual, int[] pred, int classCount)
        {
            var m = ClassifierEvaluation.Metrics(actual, pred, classCount);
            return metric == AutoMlMetric.MacroF1 ? m.MacroF1 : m.Accuracy;
        }

        static double RegScore(AutoMlMetric metric, double[] actual, double[] pred)
        {
            var m = RegressionMetrics.From(actual, pred);
            return metric == AutoMlMetric.RSquared ? m.RSquared : m.Rmse;
        }

        static int Majority(int[] labels, int classCount)
        {
            var counts = new int[classCount];
            for (int i = 0; i < labels.Length; i++) counts[labels[i]]++;
            int best = 0;
            for (int c = 1; c < classCount; c++)
                if (counts[c] > counts[best]) best = c;
            return best;
        }

        static void Validate(double[,] x, AutoMlOptions opt, bool regression)
        {
            int n = x.GetLength(0);
            if (x.GetLength(1) < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 4) throw new DesignMatrixException("Need at least 4 complete rows.");
            if (opt.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the search is reproducible.");
            if (!(opt.TestFraction > 0 && opt.TestFraction < 1))
                throw new DesignMatrixException("Test fraction must be between 0 and 1 (exclusive).");
            if (opt.Folds < 2) throw new DesignMatrixException("k-fold needs at least 2 folds.");
            if (opt.SearchRowBudget < 4) throw new DesignMatrixException("The search row budget must be at least 4.");
            if (opt.RefitRowCap < 4) throw new DesignMatrixException("The refit row cap must be at least 4.");
            if (opt.TimeBudgetSeconds < 0) throw new DesignMatrixException("Time budget cannot be negative.");
            _ = regression;
        }

        static int[] Map(int[] outer, int[] local)
        {
            var a = new int[local.Length];
            for (int i = 0; i < local.Length; i++) a[i] = outer[local[i]];
            Array.Sort(a);
            return a;
        }

        static int[] LabelsOf(int[] labels, int[] rows)
        {
            var a = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) a[i] = labels[rows[i]];
            return a;
        }

        static double[] ValuesOf(double[] y, int[] rows)
        {
            var a = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) a[i] = y[rows[i]];
            return a;
        }

        static int[] TakeInt(int[] y, int[] rows) => LabelsOf(y, rows);
        static double[] TakeDouble(double[] y, int[] rows) => ValuesOf(y, rows);
    }
}
