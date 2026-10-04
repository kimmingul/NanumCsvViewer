using System.Diagnostics;

namespace NanumCsvViewer.Stats
{
    public enum AutoMlMetric { Accuracy, MacroF1, Rmse, RSquared }

    public enum AutoMlSkip { LogisticNeedsBinary, SvmTooLarge }

    /// <summary>SVM 후보의 해법. ExactSmo는 libsvm SMO(탐색 표본 800행 이하), LinearDcd는 선형 쌍대 좌표 강하(one-vs-rest).</summary>
    public enum AutoMlSvmMode { None, ExactSmo, LinearDcd }

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
        /// <summary>DCD SVM 에폭당 작업 상한(행×특성×클래스). 테스트가 표본추출 경로를 작은 데이터로 확인하려고 줄인다.</summary>
        internal long SvmWorkPerEpoch { get; init; } = AutoMl.SvmDcdWorkPerEpoch;
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

    /// <summary>
    /// 다항(소프트맥스) 로지스틱 회귀. 계수는 [클래스, 1 + 특성] 이고 0열이 절편(규제 없음)이다.
    /// 목적함수 = (1/n)·Σ 교차엔트로피 + (1/(2·C·n))·‖W‖² 로 sklearn LogisticRegression(multinomial, L2, C)의
    /// 목적(C·Σ손실 + ½‖W‖²)을 n·C로 나눈 것과 최소점이 같다. 학습 행에 없는 클래스는 확률 0(예측 불가)이다.
    /// 입력은 호출자가 스케일한 특성이어야 한다.
    /// </summary>
    public sealed class MultinomialLogisticModel
    {
        public const string ModelTypeName = "MultinomialLogistic";

        /// <summary>[ClassCount, 1 + 특성]. 0열 절편. 학습 행에 없는 클래스의 행은 0.</summary>
        public required double[,] Coefficients { get; init; }
        public required bool[] ClassPresent { get; init; }
        public required double C { get; init; }
        public required bool Converged { get; init; }
        public required int Iterations { get; init; }
        public required int RowsFit { get; init; }
        public int ClassCount => Coefficients.GetLength(0);
        public int FeatureCount => Coefficients.GetLength(1) - 1;

        /// <summary>행별 클래스 확률 [n, ClassCount]. 학습에 없는 클래스는 0.</summary>
        public double[,] PredictProbabilities(double[,] x, CancellationToken cancellation = default)
        {
            int n = x.GetLength(0), p = x.GetLength(1), k = ClassCount;
            if (p != FeatureCount) throw new ArgumentException("Feature count must match the fitted model.", nameof(x));
            var prob = new double[n, k];
            var z = new double[k];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                double max = double.NegativeInfinity;
                for (int c = 0; c < k; c++)
                {
                    if (!ClassPresent[c]) { z[c] = double.NegativeInfinity; continue; }
                    double s = Coefficients[c, 0];
                    for (int j = 0; j < p; j++) s += Coefficients[c, j + 1] * x[i, j];
                    z[c] = s;
                    if (s > max) max = s;
                }
                double sum = 0;
                for (int c = 0; c < k; c++)
                {
                    double e = ClassPresent[c] ? Math.Exp(z[c] - max) : 0;
                    z[c] = e;
                    sum += e;
                }
                for (int c = 0; c < k; c++) prob[i, c] = z[c] / sum;
            }
            return prob;
        }

        /// <summary>확률 최대 클래스. 동점은 작은 클래스 번호.</summary>
        public int[] PredictClasses(double[,] x, CancellationToken cancellation = default)
        {
            var prob = PredictProbabilities(x, cancellation);
            int n = prob.GetLength(0), k = prob.GetLength(1);
            var cls = new int[n];
            for (int i = 0; i < n; i++)
            {
                int best = 0;
                for (int c = 1; c < k; c++)
                    if (prob[i, c] > prob[i, best]) best = c;
                cls[i] = best;
            }
            return cls;
        }
    }

    public static class MultinomialLogistic
    {
        public const int MaxIterations = 500;
        const int ChunkRows = 1024;

        public static MultinomialLogisticModel Fit(double[,] x, int[] y, int classCount, double c = 1.0, CancellationToken cancellation = default)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (y.Length != n) throw new ArgumentException("Label length must match rows.", nameof(y));
            if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            if (!(c > 0) || double.IsInfinity(c)) throw new DesignMatrixException("C must be positive and finite.");
            var counts = new int[classCount];
            for (int i = 0; i < n; i++)
            {
                if ((uint)y[i] >= (uint)classCount) throw new DesignMatrixException("A class index is outside 0..K-1.");
                counts[y[i]]++;
            }
            var present = new bool[classCount];
            var compact = new int[classCount];
            int k = 0;
            for (int cl = 0; cl < classCount; cl++)
            {
                present[cl] = counts[cl] > 0;
                compact[cl] = present[cl] ? k++ : -1;
            }
            if (k < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            var yc = new int[n];
            for (int i = 0; i < n; i++) yc[i] = compact[y[i]];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++)
                    if (!double.IsFinite(x[i, j])) throw new DesignMatrixException("Features must be finite.");

            int stride = p + 1;
            int dim = k * stride;
            double lambda = 1.0 / (c * n);
            var start = new double[dim];
            alglib.minlbfgscreate(dim, Math.Min(10, dim), start, out var state);
            alglib.minlbfgssetcond(state, 1e-9, 0, 0, MaxIterations);
            int iterations = 0;
            while (alglib.minlbfgsiteration(state))
            {
                var inner = state.innerobj;
                if (inner.needfg || inner.needf)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var g = inner.needfg ? inner.g : new double[dim];
                    inner.f = Objective(x, yc, n, p, k, lambda, inner.x, g, cancellation);
                    iterations++;
                }
            }
            alglib.minlbfgsresults(state, out var sol, out var rep);
            if (rep.terminationtype < 0 || !sol.All(double.IsFinite))
                throw new DesignMatrixException("Multinomial logistic regression diverged.");
            var coef = new double[classCount, stride];
            for (int cl = 0; cl < classCount; cl++)
            {
                if (!present[cl]) continue;
                for (int j = 0; j < stride; j++) coef[cl, j] = sol[compact[cl] * stride + j];
            }
            return new MultinomialLogisticModel
            {
                Coefficients = coef,
                ClassPresent = present,
                C = c,
                Converged = rep.terminationtype != 5,
                Iterations = rep.iterationscount,
                RowsFit = n,
            };
        }

        static double Objective(double[,] x, int[] y, int n, int p, int k, double lambda, double[] theta, double[] grad, CancellationToken ct)
        {
            int stride = p + 1, dim = k * stride;
            int chunks = (n + ChunkRows - 1) / ChunkRows;
            var lossParts = new double[chunks];
            var gradParts = new double[chunks][];
            void Run(int ch)
            {
                int lo = ch * ChunkRows, hi = Math.Min(n, lo + ChunkRows);
                var g = new double[dim];
                var z = new double[k];
                double loss = 0;
                for (int i = lo; i < hi; i++)
                {
                    double max = double.NegativeInfinity;
                    for (int c = 0; c < k; c++)
                    {
                        int o = c * stride;
                        double s = theta[o];
                        for (int j = 0; j < p; j++) s += theta[o + 1 + j] * x[i, j];
                        z[c] = s;
                        if (s > max) max = s;
                    }
                    double sum = 0;
                    for (int c = 0; c < k; c++) sum += Math.Exp(z[c] - max);
                    double lse = max + Math.Log(sum);
                    loss += lse - z[y[i]];
                    for (int c = 0; c < k; c++)
                    {
                        double r = Math.Exp(z[c] - lse) - (c == y[i] ? 1 : 0);
                        int o = c * stride;
                        g[o] += r;
                        for (int j = 0; j < p; j++) g[o + 1 + j] += r * x[i, j];
                    }
                }
                lossParts[ch] = loss;
                gradParts[ch] = g;
            }
            if (chunks >= 4)
                Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, Run);
            else
                for (int ch = 0; ch < chunks; ch++) Run(ch);
            double total = 0;
            Array.Clear(grad, 0, dim);
            for (int ch = 0; ch < chunks; ch++)
            {
                total += lossParts[ch];
                var g = gradParts[ch];
                for (int d = 0; d < dim; d++) grad[d] += g[d];
            }
            double f = total / n;
            for (int d = 0; d < dim; d++) grad[d] /= n;
            for (int c = 0; c < k; c++)
                for (int j = 1; j < stride; j++)
                {
                    double w = theta[c * stride + j];
                    f += 0.5 * lambda * w * w;
                    grad[c * stride + j] += lambda * w;
                }
            return f;
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
        /// <summary>실제로 탐색한 후보 이름(계획된 격자, 실행 순서). 시간 예산으로 건너뛴 것도 포함한다.</summary>
        public required IReadOnlyList<string> Grid { get; init; }
        public required AutoMlSvmMode SvmMode { get; init; }
        /// <summary>SVM 한 번의 적합에 쓰는 학습 행 상한(시드 고정 층화 표본). 상한이 없으면 null.</summary>
        public int? SvmFitRowCap { get; init; }
        /// <summary>다항 로지스틱 승자가 L-BFGS 반복 상한 전에 기울기 허용오차에 닿았는지. 해당 없으면 null.</summary>
        public bool? BestConverged { get; init; }
    }

    /// <summary>
    /// 기존 학습기 작은 격자 탐색. 홀드아웃 시험 행은 탐색에 쓰지 않고, 층화 k-겹은 학습 분할(표본)에서만 돈다.
    /// 최고 설정을 학습 분할에 다시 적합해 시험 지표를 내고, 저장 모형은 사용 행 전체(상한이면 시드 고정 표본)로 다시 적합한다.
    /// </summary>
    public static class AutoMl
    {
        public const int SvmSearchRowCap = 800;
        public const int SvmExactMaxFeatures = 40;
        /// <summary>DCD SVM: 한 번의 적합에서 에폭당 곱셈-덧셈 수(행 × 특성 × 클래스)의 상한. 넘으면 행을 시드 고정 층화 표본으로 줄인다.</summary>
        public const long SvmDcdWorkPerEpoch = 10_000_000;
        public const int SvmDcdMaxRows = 100_000;
        public const int SvmDcdMinRows = 200;
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
            var skipped = new List<AutoMlSkip>();
            var specs = Specs(regression, classCount, search.Length, x.GetLength(1), skipped, opt.SvmWorkPerEpoch);
            if (!regression && classCount != 2) skipped.Add(AutoMlSkip.LogisticNeedsBinary);

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
                        var foldRows = regression ? split.Train : CapRows(split.Train, searchY!, spec.FitRowCap, opt.Seed);
                        var fitted = spec.Fit(searchX, searchY, searchReg, foldRows, classCount, opt.Seed, cancellation);
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
            int fitCap = EffectiveCap(bestSpec, opt.RefitRowCap);
            if (train.Length > fitCap)
            {
                evalSampled = true;
                evalRows = regression
                    ? Map(train, RowSample.Random(train.Length, fitCap, opt.Seed, out _))
                    : Map(train, RowSample.Stratified(LabelsOf(labels!, train), fitCap, opt.Seed, out _));
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
            if (n > fitCap)
            {
                bundleSampled = true;
                bundleRows = regression
                    ? RowSample.Random(n, fitCap, opt.Seed, out _)
                    : RowSample.Stratified(labels!, fitCap, opt.Seed, out _);
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
                Grid = specs.Select(s => s.Name).ToArray(),
                SvmMode = specs.Any(s => s.Name == "SVM-linear") ? AutoMlSvmMode.ExactSmo
                    : specs.Any(s => s.Name == "SVM-linear-dcd") ? AutoMlSvmMode.LinearDcd : AutoMlSvmMode.None,
                SvmFitRowCap = specs.FirstOrDefault(s => s.Name == "SVM-linear-dcd")?.FitRowCap,
                BestConverged = evalFit.Converged is null && bundle.Converged is null ? null : evalFit.Converged != false && bundle.Converged != false,
                ElapsedSeconds = clock.Elapsed.TotalSeconds,
            };
        }

        sealed class Spec
        {
            public required string Name { get; init; }
            public required string ModelType { get; init; }
            public bool Scale { get; init; }
            public bool UnboundedRefit { get; init; }
            /// <summary>한 번의 적합에 쓰는 행 상한(교차검증 겹·시험 지표 모형·저장 모형 공통). 넘으면 시드 고정 층화 표본.</summary>
            public int FitRowCap { get; init; } = int.MaxValue;
            public required Func<double[,], int[]?, double[]?, int[], int, int, CancellationToken, Fitted> Fit { get; init; }
        }

        sealed class Fitted
        {
            public required object Engine { get; init; }
            public bool? Converged { get; init; }
            public FeatureScaler? Scaler { get; init; }
            public required Func<double[,], CancellationToken, int[]> PredictClass { get; init; }
            public required Func<double[,], CancellationToken, double[]> PredictValue { get; init; }
        }

        static List<Spec> Specs(bool regression, int classCount, int searchRows, int features, List<AutoMlSkip> skipped, long svmWork)
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
            if (classCount == 2)
                list.Add(Linear(logistic: true));
            else
            {
                list.Add(Multinomial(1.0));
                list.Add(Multinomial(0.1));
            }
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
            var (svmMode, svmCap) = PlanSvm(searchRows, features, classCount, svmWork);
            if (svmMode == AutoMlSvmMode.ExactSmo) list.Add(Svm());
            else if (svmMode == AutoMlSvmMode.LinearDcd) list.Add(SvmDcd(svmCap));
            else skipped.Add(AutoMlSkip.SvmTooLarge);
            return list;
        }

        /// <summary>
        /// SVM 후보 결정. 탐색 표본이 작으면 정확한 SMO. 아니면 DCD이고 한 번의 적합 행 상한은
        /// min(100,000, 10,000,000 / (특성 × 클래스)). 200행도 못 넣으면 None(생략).
        /// </summary>
        internal static (AutoMlSvmMode Mode, int RowCap) PlanSvm(int searchRows, int features, int classCount, long work = SvmDcdWorkPerEpoch)
        {
            if (searchRows <= SvmSearchRowCap && features <= SvmExactMaxFeatures) return (AutoMlSvmMode.ExactSmo, int.MaxValue);
            long perRow = Math.Max(1L, (long)features * classCount);
            int cap = (int)Math.Min(SvmDcdMaxRows, work / perRow);
            return cap < SvmDcdMinRows ? (AutoMlSvmMode.None, 0) : (AutoMlSvmMode.LinearDcd, cap);
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

        /// <summary>선형 SVM, 쌍대 좌표 강하(one-vs-rest, 정규화된 절편). 한 번의 적합은 rowCap 행까지, 넘으면 시드 고정 층화 표본.</summary>
        static Spec SvmDcd(int rowCap) => new()
        {
            Name = "SVM-linear-dcd",
            ModelType = ModelTypes.Svm,
            Scale = true,
            FitRowCap = rowCap,
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
                    // 훈련 행이 이 값을 넘으면 SVM이 커널 행렬 없는 DCD 경로를 쓴다. 항상 넘도록 n-1.
                    MaxTrainingRows = Math.Max(sx.GetLength(0) - 1, 2),
                };
                var model = SupportVectorMachine.Fit(sx, TakeInt(labels!, rows), classCount, opt, ct);
                return Predictor(model, scaler, (z, token) => model.Predict(scaler.Transform(z), token), null);
            },
        };

        /// <summary>L2 규제 다항 로지스틱(C는 sklearn과 같은 역규제 강도). 행 수 상한은 재적합 상한을 따른다.</summary>
        static Spec Multinomial(double c) => new()
        {
            Name = "Multinomial-C" + c.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture),
            ModelType = MultinomialLogisticModel.ModelTypeName,
            Scale = true,
            Fit = (x, labels, _, rows, classCount, _, ct) =>
            {
                var raw = AdaBoost.TakeRows(x, rows);
                var scaler = FeatureScaler.Fit(raw, ScalingMethod.ZScore);
                var sx = scaler.Transform(raw);
                var model = MultinomialLogistic.Fit(sx, TakeInt(labels!, rows), classCount, c, ct);
                return new Fitted
                {
                    Engine = model,
                    Scaler = scaler,
                    Converged = model.Converged,
                    PredictClass = (z, token) => model.PredictClasses(scaler.Transform(z), token),
                    PredictValue = (_, _) => throw new InvalidOperationException("This configuration is not a regressor."),
                };
            },
        };

        static int EffectiveCap(Spec spec, int refitCap)
            => Math.Min(spec.UnboundedRefit ? int.MaxValue : refitCap, spec.FitRowCap);

        /// <summary>rows가 cap을 넘으면 시드 고정 층화 표본(원 행 번호, 오름차순). labels는 rows와 같은 좌표계(탐색 행렬 안의 위치).</summary>
        internal static int[] CapRows(int[] rows, int[] labels, int cap, int seed)
        {
            if (rows.Length <= cap) return rows;
            return Map(rows, RowSample.Stratified(LabelsOf(labels, rows), cap, seed, out _));
        }

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
