using System.Diagnostics;
using System.Globalization;

namespace NanumCsvViewer.Stats
{
    /// <summary>회귀 AdaBoost.R2 손실. 분류(SAMME)는 쓰지 않는다.</summary>
    public enum AdaBoostLoss { Linear, Square, Exponential }

    /// <summary>
    /// AdaBoost 옵션. 분류 기본 학습기는 깊이 1 스텀프(sklearn AdaBoostClassifier), 회귀 기본은 깊이 3
    /// (sklearn AdaBoostRegressor). MaxDepth가 0이면 그 기본값을 쓴다.
    /// </summary>
    public sealed record AdaBoostOptions
    {
        public int Estimators { get; init; } = 50;
        public double LearningRate { get; init; } = 1;
        /// <summary>0이면 분류 1, 회귀 3.</summary>
        public int MaxDepth { get; init; }
        public int Seed { get; init; } = 1;
        public AdaBoostLoss Loss { get; init; } = AdaBoostLoss.Linear;
        /// <summary>분류 스텀프의 불순도. 회귀는 항상 MSE.</summary>
        public TreeCriterion Criterion { get; init; } = TreeCriterion.Gini;
        public int MinSamplesLeaf { get; init; } = 1;
        /// <summary>이 행 수를 넘으면 시드 고정 표본만 학습한다. 0 이하면 기본 상한.</summary>
        public int MaxTrainingRows { get; init; } = AdaBoost.DefaultMaxTrainingRows;
        /// <summary>0 이하면 시간 제한 없음. 첫 추정기는 항상 적합하고, 그 다음부터 경과 시간이 예산을 넘으면 멈춘다.</summary>
        public double TimeBudgetSeconds { get; init; }
    }

    /// <summary>
    /// 분류는 sklearn AdaBoostClassifier(algorithm='SAMME', 가중 얕은 트리). 회귀는 AdaBoost.R2
    /// (가중 부트스트랩 + 비가중 CART, 예측은 가중 중앙값). 분류 동점은 특성 인덱스가 작은 쪽 —
    /// sklearn은 특성을 무작위 순서로 봐서 동점일 때만 다를 수 있다.
    /// </summary>
    public sealed record AdaBoostModel
    {
        public bool Regression { get; init; }
        public int ClassCount { get; init; }
        public int FeatureCount { get; init; }
        public int EstimatorsUsed { get; init; }
        public int EstimatorsRequested { get; init; }
        public double LearningRate { get; init; }
        public int MaxDepth { get; init; }
        public int Seed { get; init; }
        public AdaBoostLoss Loss { get; init; }
        public TreeCriterion Criterion { get; init; }
        /// <summary>쓴 추정기만. sklearn의 꼬리 0 패딩은 없다.</summary>
        public required double[] EstimatorWeights { get; init; }
        public required double[] EstimatorErrors { get; init; }
        /// <summary>추정기 가중 평균 불순도 감소. 합이 1이거나, 분할이 없으면 0.</summary>
        public required double[] Importances { get; init; }
        public bool Sampled { get; init; }
        public int RowsFit { get; init; }
        public int SourceRows { get; init; }
        public bool StoppedEarly { get; init; }
        /// <summary>perfect | worse-than-random | time-budget | weight-overflow | null.</summary>
        public string? StopReason { get; init; }
        public bool TimeBudgetHit { get; init; }

        /// <summary>분류 스텀프/얕은 트리. 회귀 모형이면 빈 목록. 저장·재적용용 읽기 전용 스냅샷.</summary>
        public IReadOnlyList<AdaBoostStump> Stumps { get; init; } = Array.Empty<AdaBoostStump>();
        /// <summary>회귀 기본 학습기(비가중 CART). 분류 모형이면 빈 목록.</summary>
        public IReadOnlyList<DecisionTreeModel> RegressionTrees { get; init; } = Array.Empty<DecisionTreeModel>();

        public int[] Predict(double[,] x, CancellationToken cancellation = default)
        {
            if (Regression) throw new InvalidOperationException("This AdaBoost model is a regressor.");
            EnsureColumns(x);
            int n = x.GetLength(0);
            var pred = new int[n];
            if (EstimatorsUsed == 0) return pred;
            var trees = Stumps;
            var w = EstimatorWeights;
            double wSum = 0;
            for (int m = 0; m < EstimatorsUsed; m++) wSum += w[m];
            int k = ClassCount;
            if (k < 2 || wSum == 0 && EstimatorsUsed == 1)
            {
                for (int i = 0; i < n; i++) pred[i] = trees[0].PredictRow(x, i);
                return pred;
            }
            double neg = -1.0 / (k - 1);
            // 행 범위를 병렬로 처리(행별 독립이라 스레드 수와 무관하게 결과 동일). 다중 클래스 점수 버퍼는 범위당 1개.
            int parts = Math.Clamp(n / 4096, 1, Environment.ProcessorCount);
            Parallel.For(0, parts, new ParallelOptions { CancellationToken = cancellation }, part =>
            {
                int from = (int)((long)n * part / parts), to = (int)((long)n * (part + 1) / parts);
                var score = new double[k];
                for (int i = from; i < to; i++)
                {
                    if (((i - from) & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    if (k == 2)
                    {
                        double s2 = 0;
                        for (int m = 0; m < EstimatorsUsed; m++)
                        {
                            int c = trees[m].PredictRow(x, i);
                            // SAMME 이진: 클래스 1이면 +w, 클래스 0이면 -w. 동점(0)은 클래스 0.
                            s2 += c == 1 ? w[m] : -w[m];
                        }
                        pred[i] = s2 > 0 ? 1 : 0;
                    }
                    else
                    {
                        Array.Clear(score);
                        for (int m = 0; m < EstimatorsUsed; m++)
                        {
                            int c = trees[m].PredictRow(x, i);
                            for (int t = 0; t < k; t++)
                                score[t] += t == c ? w[m] : neg * w[m];
                        }
                        int best = 0;
                        for (int t = 1; t < k; t++)
                            if (score[t] > score[best]) best = t;
                        pred[i] = best;
                    }
                }
            });
            return pred;
        }

        public double[] PredictValue(double[,] x, CancellationToken cancellation = default)
        {
            if (!Regression) throw new InvalidOperationException("This AdaBoost model is a classifier.");
            EnsureColumns(x);
            int n = x.GetLength(0);
            var dest = new double[n];
            var trees = RegressionTrees;
            if (EstimatorsUsed == 0) return dest;
            var w = EstimatorWeights;
            var cols = new double[EstimatorsUsed][];
            for (int m = 0; m < EstimatorsUsed; m++)
                cols[m] = trees[m].PredictValue(x, cancellation);
            var order = new int[EstimatorsUsed];
            var values = new double[EstimatorsUsed];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                for (int m = 0; m < EstimatorsUsed; m++)
                {
                    order[m] = m;
                    values[m] = cols[m][i];
                }
                Array.Sort(values, order, 0, EstimatorsUsed);
                double total = 0;
                for (int m = 0; m < EstimatorsUsed; m++) total += w[m];
                double half = 0.5 * total;
                double cdf = 0;
                int pick = order[0];
                for (int m = 0; m < EstimatorsUsed; m++)
                {
                    cdf += w[order[m]];
                    if (cdf >= half || m == EstimatorsUsed - 1)
                    {
                        pick = order[m];
                        break;
                    }
                }
                dest[i] = cols[pick][i];
            }
            return dest;
        }

        void EnsureColumns(double[,] x)
        {
            if (x.GetLength(1) != FeatureCount)
                throw new ArgumentException("Feature count does not match the fitted model.", nameof(x));
        }
    }
    /// <summary>
    /// 분류 AdaBoost의 읽기 전용 스텀프. Feature &lt; 0이면 잎. 왼쪽은 값 &lt;= Threshold, 결측·초과는 오른쪽.
    /// Importances는 루트에만 채워진 트리별 정규화 중요도다.
    /// </summary>
    public sealed class AdaBoostStump
    {
        public int Feature { get; init; } = -1;
        public double Threshold { get; init; }
        public int Prediction { get; init; }
        public AdaBoostStump? Left { get; init; }
        public AdaBoostStump? Right { get; init; }
        public double[] Importances { get; init; } = Array.Empty<double>();

        public int PredictRow(double[,] x, int row)
        {
            var node = this;
            while (node.Feature >= 0)
            {
                double v = x[row, node.Feature];
                var next = double.IsNaN(v) || v > node.Threshold ? node.Right : node.Left;
                if (next is null) break;
                node = next;
            }
            return node.Prediction;
        }

        internal static AdaBoostStump From(WeightedStumpTree tree) => new()
        {
            Feature = tree.Feature,
            Threshold = tree.Threshold,
            Prediction = tree.Prediction,
            Left = tree.Left is null ? null : From(tree.Left),
            Right = tree.Right is null ? null : From(tree.Right),
            Importances = tree.Importances,
        };
    }


    public sealed class AdaBoostClassificationResult
    {
        public required ClassifierEvalResult Evaluation { get; init; }
        public required AdaBoostModel Model { get; init; }
        public required bool ModelUsesAllRows { get; init; }
    }

    public sealed class AdaBoostRegressionResult
    {
        public required RegressionMetrics Metrics { get; init; }
        public required double BaselineRmse { get; init; }
        public required EvalScheme Scheme { get; init; }
        public required int Seed { get; init; }
        public required double TestFraction { get; init; }
        public required int Folds { get; init; }
        public required int TrainRows { get; init; }
        public required int TestRows { get; init; }
        public required AdaBoostModel Model { get; init; }
        public required bool ModelUsesAllRows { get; init; }
    }

    public static class AdaBoost
    {
        public const int DefaultMaxTrainingRows = 40_000;
        /// <summary>ModelBundle.ModelType. ModelTypes에 상수가 아직 없어 저장 어댑터는 이 문자열을 쓴다.</summary>
        public const string ModelTypeName = "AdaBoost";

        /// <summary>np.finfo(float64).eps. sklearn이 표본 가중을 이 값으로 자른다.</summary>
        public const double MachineEps = 2.2204460492503131e-16;

        public static AdaBoostModel FitClassification(
            double[,] x, int[] y, int classCount, AdaBoostOptions? options = null, CancellationToken cancellation = default)
        {
            var opt = Validate(options ?? new AdaBoostOptions(), regression: false);
            int n = x.GetLength(0), p = x.GetLength(1);
            if (y.Length != n) throw new ArgumentException("Label length must match rows.", nameof(y));
            if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            for (int i = 0; i < n; i++)
                if ((uint)y[i] >= (uint)classCount)
                    throw new DesignMatrixException("A class index is outside 0..K-1.");
            int depth = opt.MaxDepth <= 0 ? 1 : opt.MaxDepth;
            var (rows, sampled) = TakeTrain(n, y, opt, regression: false, cancellation);
            var fitX = rows.Length == n ? x : TakeRows(x, rows);
            var fitY = rows.Length == n ? y : TakeInt(y, rows);
            var model = FitSamme(fitX, fitY, classCount, p, depth, opt, cancellation);
            return model with { Sampled = sampled, RowsFit = fitX.GetLength(0), SourceRows = n };
        }

        public static AdaBoostModel FitRegression(
            double[,] x, double[] y, AdaBoostOptions? options = null, CancellationToken cancellation = default)
        {
            var opt = Validate(options ?? new AdaBoostOptions(), regression: true);
            int n = x.GetLength(0), p = x.GetLength(1);
            if (y.Length != n) throw new ArgumentException("Target length must match rows.", nameof(y));
            for (int i = 0; i < n; i++)
                if (!double.IsFinite(y[i]))
                    throw new DesignMatrixException("Regression target has a non-finite value.");
            int depth = opt.MaxDepth <= 0 ? 3 : opt.MaxDepth;
            var (rows, sampled) = TakeTrain(n, null, opt, regression: true, cancellation);
            var fitX = rows.Length == n ? x : TakeRows(x, rows);
            var fitY = rows.Length == n ? y : TakeDouble(y, rows);
            var model = FitR2(fitX, fitY, p, depth, opt, cancellation);
            return model with { Sampled = sampled, RowsFit = fitX.GetLength(0), SourceRows = n };
        }

        public static AdaBoostClassificationResult EvaluateClassification(
            double[,] x, int[] y, int classCount, ClassifierOptions eval, AdaBoostOptions? options = null,
            CancellationToken cancellation = default)
        {
            var opt = options ?? new AdaBoostOptions();
            var evalOpt = eval with { Scaling = ScalingMethod.None };
            ClassifierCommon.Validate(x, y, classCount, evalOpt, neighbors: false);
            AdaBoostModel? captured = null;
            var result = ClassifierCommon.Evaluate(x, y, classCount, evalOpt, scaleColumns: null, cancellation,
                (trainX, trainY, testX, _) =>
                {
                    var fit = FitClassification(trainX, trainY, classCount, opt, cancellation);
                    captured = fit;
                    return fit.Predict(testX, cancellation);
                });
            bool allRows = evalOpt.Scheme != EvalScheme.Holdout;
            var display = allRows
                ? FitClassification(x, y, classCount, opt, cancellation)
                : captured ?? throw new DesignMatrixException("AdaBoost produced no training fit.");
            return new AdaBoostClassificationResult { Evaluation = result, Model = display, ModelUsesAllRows = allRows };
        }

        public static AdaBoostRegressionResult EvaluateRegression(
            double[,] x, double[] y, ClassifierOptions eval, AdaBoostOptions? options = null,
            CancellationToken cancellation = default)
        {
            var opt = options ?? new AdaBoostOptions();
            int n = x.GetLength(0);
            if (y.Length != n) throw new ArgumentException("Target length must match rows.", nameof(y));
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (eval.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the split is reproducible.");
            var dummy = new int[n];
            AdaBoostModel? captured = null;
            if (eval.Scheme == EvalScheme.Holdout)
            {
                if (!(eval.TestFraction > 0 && eval.TestFraction < 1))
                    throw new DesignMatrixException("Test fraction must be between 0 and 1 (exclusive).");
                var split = ClassifierEvaluation.Holdout(dummy, eval.TestFraction, eval.Seed, stratified: false);
                var (pred, actual, baseline) = RegressSplit(x, y, split, opt, cancellation, ref captured);
                return new AdaBoostRegressionResult
                {
                    Metrics = RegressionMetrics.From(actual, pred),
                    BaselineRmse = baseline,
                    Scheme = eval.Scheme,
                    Seed = eval.Seed,
                    TestFraction = eval.TestFraction,
                    Folds = eval.Folds,
                    TrainRows = split.Train.Length,
                    TestRows = split.Test.Length,
                    Model = captured!,
                    ModelUsesAllRows = false,
                };
            }
            if (eval.Folds < 2) throw new DesignMatrixException("k-fold needs at least 2 folds.");
            if (n < eval.Folds)
                throw new DesignMatrixException($"Need at least {eval.Folds} complete rows for {eval.Folds}-fold cross-validation.");
            var folds = ClassifierEvaluation.KFold(dummy, eval.Folds, eval.Seed, stratified: false);
            var allActual = new double[n];
            var allPred = new double[n];
            double baselineSse = 0;
            foreach (var split in folds)
            {
                cancellation.ThrowIfCancellationRequested();
                if (split.Test.Length == 0 || split.Train.Length == 0)
                    throw new DesignMatrixException("A cross-validation fold is empty. Use fewer folds or more rows.");
                var (pred, actual, baseline) = RegressSplit(x, y, split, opt, cancellation, ref captured);
                for (int i = 0; i < split.Test.Length; i++)
                {
                    allActual[split.Test[i]] = actual[i];
                    allPred[split.Test[i]] = pred[i];
                }
                baselineSse += baseline * baseline * split.Test.Length;
            }
            return new AdaBoostRegressionResult
            {
                Metrics = RegressionMetrics.From(allActual, allPred),
                BaselineRmse = Math.Sqrt(baselineSse / n),
                Scheme = eval.Scheme,
                Seed = eval.Seed,
                TestFraction = eval.TestFraction,
                Folds = eval.Folds,
                TrainRows = n,
                TestRows = n,
                Model = FitRegression(x, y, opt, cancellation),
                ModelUsesAllRows = true,
            };
        }

        static (double[] Pred, double[] Actual, double Baseline) RegressSplit(
            double[,] x, double[] y, DataSplit split, AdaBoostOptions opt, CancellationToken cancellation,
            ref AdaBoostModel? captured)
        {
            var trainX = TakeRows(x, split.Train);
            var testX = TakeRows(x, split.Test);
            var trainY = TakeDouble(y, split.Train);
            var fit = FitRegression(trainX, trainY, opt, cancellation);
            captured = fit;
            var pred = fit.PredictValue(testX, cancellation);
            var actual = TakeDouble(y, split.Test);
            double mean = 0;
            for (int i = 0; i < trainY.Length; i++) mean += trainY[i];
            mean /= trainY.Length;
            double sse = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                double e = actual[i] - mean;
                sse += e * e;
            }
            return (pred, actual, Math.Sqrt(sse / actual.Length));
        }

        static AdaBoostModel FitSamme(double[,] x, int[] y, int classCount, int p, int depth, AdaBoostOptions opt, CancellationToken cancellation)
        {
            int n = y.Length;
            var weight = new double[n];
            double inv = 1.0 / n;
            for (int i = 0; i < n; i++) weight[i] = inv;
            var trees = new List<WeightedStumpTree>();
            var alphas = new List<double>();
            var errors = new List<double>();
            var clock = Stopwatch.StartNew();
            bool stopped = false, timeHit = false;
            string? reason = null;
            double worse = 1.0 - 1.0 / classCount;
            for (int m = 0; m < opt.Estimators; m++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (m > 0 && opt.TimeBudgetSeconds > 0 && clock.Elapsed.TotalSeconds >= opt.TimeBudgetSeconds)
                {
                    stopped = true;
                    timeHit = true;
                    reason = "time-budget";
                    break;
                }
                Clip(weight);
                var tree = WeightedStumpTree.Fit(x, y, weight, classCount, depth, opt.Criterion, opt.MinSamplesLeaf, cancellation);
                var pred = tree.Predict(x, cancellation);
                double errNum = 0, errDen = 0;
                for (int i = 0; i < n; i++)
                {
                    errDen += weight[i];
                    if (pred[i] != y[i]) errNum += weight[i];
                }
                double err = errDen == 0 ? 1 : errNum / errDen;
                if (err <= 0)
                {
                    trees.Add(tree);
                    alphas.Add(1);
                    errors.Add(0);
                    stopped = true;
                    reason = "perfect";
                    break;
                }
                if (err >= worse)
                {
                    if (trees.Count == 0)
                        throw new DesignMatrixException("AdaBoost base learner is worse than random. The ensemble cannot be fit.");
                    stopped = true;
                    reason = "worse-than-random";
                    break;
                }
                double alpha = opt.LearningRate * (Math.Log((1 - err) / err) + Math.Log(classCount - 1));
                trees.Add(tree);
                alphas.Add(alpha);
                errors.Add(err);
                if (m == opt.Estimators - 1) break;
                for (int i = 0; i < n; i++)
                {
                    if (!(weight[i] > 0)) continue;
                    double incorrect = pred[i] != y[i] ? 1 : 0;
                    weight[i] = Math.Exp(Math.Log(weight[i]) + alpha * incorrect);
                }
                if (!Normalize(weight))
                {
                    stopped = true;
                    reason = "weight-overflow";
                    break;
                }
            }
            if (trees.Count == 0)
                throw new DesignMatrixException("AdaBoost stopped before any estimator was kept.");
            return Finish(false, classCount, p, depth, opt, trees.Count, alphas, errors, ImportancesOf(trees, alphas, p), stopped, reason, timeHit, trees.ToArray(), null);
        }

        static DecisionTreeModel FitMseStump(double[,] x, double[] y, int treeSeed, int minLeaf, CancellationToken cancellation)
        {
            int n = y.Length, p = x.GetLength(1);
            var treeRng = new NumpyRandom(treeSeed);
            uint state = (uint)treeRng.Randint(int.MaxValue);
            var features = new int[p];
            for (int i = 0; i < p; i++) features[i] = i;
            int fEnd = p, nFound = 0, nTotalConst = 0, nDrawnConst = 0, visited = 0;
            double bestProxy = double.NegativeInfinity;
            int bestFeature = -1, bestLeft = 0;
            double bestThreshold = 0, bestLeftSum = 0, bestLeftSq = 0, bestRightSum = 0, bestRightSq = 0;
            var vals = new double[n];
            var ord = new int[n];
            if (n >= 2 * minLeaf)
            {
                while (fEnd > nTotalConst && visited < p)
                {
                cancellation.ThrowIfCancellationRequested();
                visited++;
                int span = fEnd - nFound - nDrawnConst;
                int fDraw = nDrawnConst + (int)(OurRand(ref state) % (uint)span);
                int fIndex = fDraw + nFound;
                int feature = features[fIndex];
                for (int i = 0; i < n; i++) { ord[i] = i; vals[i] = x[i, feature]; }
                Array.Sort(vals, ord, 0, n);
                if ((float)vals[n - 1] <= (float)vals[0] + 1e-7f)
                {
                    (features[fIndex], features[nTotalConst]) = (features[nTotalConst], features[fIndex]);
                    nFound++;
                    nTotalConst++;
                    continue;
                }
                fEnd--;
                (features[fEnd], features[fIndex]) = (features[fIndex], features[fEnd]);
                double featSum = 0, featSq = 0;
                for (int i = 0; i < n; i++)
                {
                    double v = y[ord[i]];
                    featSum += v;
                    featSq += v * v;
                }
                int leftN = 0;
                double leftSum = 0, leftSq = 0;
                int s = 0;
                while (s < n)
                {
                    int groupEnd = s;
                    while (groupEnd + 1 < n && (float)vals[groupEnd + 1] <= (float)vals[groupEnd] + 1e-7f)
                        groupEnd++;
                    for (int t = s; t <= groupEnd; t++)
                    {
                        double v = y[ord[t]];
                        leftSum += v;
                        leftSq += v * v;
                        leftN++;
                    }
                    s = groupEnd + 1;
                    if (s >= n) break;
                    int rightN = n - leftN;
                    if (leftN < minLeaf || rightN < minLeaf) continue;
                    double rightSum = featSum - leftSum;
                    double proxy = leftSum * leftSum / leftN + rightSum * rightSum / rightN;
                    // 두 특성의 MSE 대리가 float 잡음(약 1e-14)만 다를 때 sklearn은 먼저 본 특성을 유지한다.
                    if (double.IsNegativeInfinity(bestProxy) || proxy > bestProxy + 1e-12)
                    {
                        bestProxy = proxy;
                        float lo = (float)vals[s - 1];
                        float hi = (float)vals[s];
                        double thr = lo / 2.0 + hi / 2.0;
                        if (thr == hi || double.IsInfinity(thr)) thr = lo;
                        bestFeature = feature;
                        bestThreshold = thr;
                        bestLeft = leftN;
                        bestLeftSum = leftSum;
                        bestLeftSq = leftSq;
                        bestRightSum = rightSum;
                        bestRightSq = featSq - leftSq;
                    }
                }
                }
            }

            double totalSum = 0, totalSq = 0;
            for (int i = 0; i < n; i++) { totalSum += y[i]; totalSq += y[i] * y[i]; }
            double mean = n == 0 ? 0 : totalSum / n;
            double parentImp = n == 0 ? 0 : totalSq / n - mean * mean;
            if (bestFeature < 0)
            {
                return new DecisionTreeModel
                {
                    Root = new Node { Feature = -1, Count = n, Value = mean, Impurity = parentImp },
                    FeatureCount = p,
                    ClassCount = 0,
                    Regression = true,
                    Criterion = TreeCriterion.Mse,
                    Importances = new double[p],
                    Depth = 0,
                    LeafCount = 1,
                    NodeCount = 1,
                };
            }
            int rightCount = n - bestLeft;
            double leftMean = bestLeftSum / bestLeft;
            double rightMean = bestRightSum / rightCount;
            double leftImp = bestLeftSq / bestLeft - leftMean * leftMean;
            double rightImp = bestRightSq / rightCount - rightMean * rightMean;
            var importances = new double[p];
            importances[bestFeature] = 1;
            return new DecisionTreeModel
            {
                Root = new Node
                {
                    Feature = bestFeature,
                    Threshold = bestThreshold,
                    Count = n,
                    Impurity = parentImp,
                    Value = mean,
                    Left = new Node { Feature = -1, Count = bestLeft, Value = leftMean, Impurity = leftImp },
                    Right = new Node { Feature = -1, Count = rightCount, Value = rightMean, Impurity = rightImp },
                },
                FeatureCount = p,
                ClassCount = 0,
                Regression = true,
                Criterion = TreeCriterion.Mse,
                Importances = importances,
                Depth = 1,
                LeafCount = 2,
                NodeCount = 3,
            };
        }

        static uint OurRand(ref uint seed)
        {
            if (seed == 0) seed = 1;
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            return seed % 2147483648u;
        }

        static AdaBoostModel FitR2(double[,] x, double[] y, int p, int depth, AdaBoostOptions opt, CancellationToken cancellation)
        {
            int n = y.Length;
            var weight = new double[n];
            double inv = 1.0 / n;
            for (int i = 0; i < n; i++) weight[i] = inv;
            var trees = new List<DecisionTreeModel>();
            var alphas = new List<double>();
            var errors = new List<double>();
            var rng = new NumpyRandom(opt.Seed);
            var treeOpt = new DecisionTreeOptions
            {
                Criterion = TreeCriterion.Mse,
                MaxDepth = depth,
                MinSamplesSplit = 2,
                MinSamplesLeaf = Math.Max(1, opt.MinSamplesLeaf),
                SplitMode = TreeSplitMode.Exact,
                Seed = 1,
            };
            var clock = Stopwatch.StartNew();
            bool stopped = false, timeHit = false;
            string? reason = null;
            var loss = new double[n];
            for (int m = 0; m < opt.Estimators; m++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (m > 0 && opt.TimeBudgetSeconds > 0 && clock.Elapsed.TotalSeconds >= opt.TimeBudgetSeconds)
                {
                    stopped = true;
                    timeHit = true;
                    reason = "time-budget";
                    break;
                }
                Clip(weight);
                // sklearn _set_random_states가 트리 random_state용 randint를 하나 소비한 뒤 choice를 호출한다.
                int treeSeed = rng.Randint(int.MaxValue);
                var boot = rng.Choice(weight);
                var bx = TakeRows(x, boot);
                var by = TakeDouble(y, boot);
                var tree = depth == 1
                    ? FitMseStump(bx, by, treeSeed, Math.Max(1, opt.MinSamplesLeaf), cancellation)
                    : DecisionTree.FitRegression(bx, by, treeOpt, cancellation);
                var pred = tree.PredictValue(x, cancellation);
                double errMax = 0;
                bool any = false;
                for (int i = 0; i < n; i++)
                {
                    if (!(weight[i] > 0)) continue;
                    any = true;
                    double e = Math.Abs(pred[i] - y[i]);
                    if (e > errMax) errMax = e;
                }
                if (!any) throw new DesignMatrixException("AdaBoost sample weights are all zero.");
                double err = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!(weight[i] > 0)) { loss[i] = 0; continue; }
                    double e = errMax == 0 ? 0 : Math.Abs(pred[i] - y[i]) / errMax;
                    if (opt.Loss == AdaBoostLoss.Square) e *= e;
                    else if (opt.Loss == AdaBoostLoss.Exponential) e = 1 - Math.Exp(-e);
                    loss[i] = e;
                    err += weight[i] * e;
                }
                if (err <= 0)
                {
                    trees.Add(tree);
                    alphas.Add(1);
                    errors.Add(0);
                    stopped = true;
                    reason = "perfect";
                    break;
                }
                if (err >= 0.5)
                {
                    if (trees.Count == 0)
                    {
                        trees.Add(tree);
                        alphas.Add(0);
                        errors.Add(err);
                    }
                    stopped = true;
                    reason = "worse-than-random";
                    break;
                }
                double beta = err / (1 - err);
                double alpha = opt.LearningRate * Math.Log(1 / beta);
                trees.Add(tree);
                alphas.Add(alpha);
                errors.Add(err);
                if (m == opt.Estimators - 1) break;
                for (int i = 0; i < n; i++)
                    if (weight[i] > 0)
                        weight[i] *= Math.Pow(beta, (1 - loss[i]) * opt.LearningRate);
                if (!Normalize(weight))
                {
                    stopped = true;
                    reason = "weight-overflow";
                    break;
                }
            }
            if (trees.Count == 0)
                throw new DesignMatrixException("AdaBoost stopped before any estimator was kept.");
            var importances = new double[p];
            double wSum = 0;
            for (int m = 0; m < trees.Count; m++) wSum += alphas[m];
            if (wSum > 0)
            {
                for (int m = 0; m < trees.Count; m++)
                {
                    var ti = trees[m].Importances;
                    for (int j = 0; j < p && j < ti.Length; j++) importances[j] += alphas[m] * ti[j];
                }
                for (int j = 0; j < p; j++) importances[j] /= wSum;
            }
            return Finish(true, 0, p, depth, opt, trees.Count, alphas, errors, importances, stopped, reason, timeHit, null, trees.ToArray());
        }

        static AdaBoostModel Finish(
            bool regression, int classCount, int p, int depth, AdaBoostOptions opt, int used,
            List<double> alphas, List<double> errors, double[] importances, bool stopped, string? reason, bool timeHit,
            WeightedStumpTree[]? classTrees, DecisionTreeModel[]? regTrees)
        {
            var stumps = classTrees is null ? Array.Empty<AdaBoostStump>() : Array.ConvertAll(classTrees, AdaBoostStump.From);
            return new AdaBoostModel
            {
                Regression = regression,
                ClassCount = classCount,
                FeatureCount = p,
                EstimatorsUsed = used,
                EstimatorsRequested = opt.Estimators,
                LearningRate = opt.LearningRate,
                MaxDepth = depth,
                Seed = opt.Seed,
                Loss = opt.Loss,
                Criterion = regression ? TreeCriterion.Mse : opt.Criterion,
                EstimatorWeights = alphas.ToArray(),
                EstimatorErrors = errors.ToArray(),
                Importances = importances,
                StoppedEarly = stopped || used < opt.Estimators,
                StopReason = reason,
                TimeBudgetHit = timeHit,
                Stumps = stumps,
                RegressionTrees = regTrees ?? Array.Empty<DecisionTreeModel>(),
            };
        }

        static double[] ImportancesOf(List<WeightedStumpTree> trees, List<double> alphas, int p)
        {
            var imp = new double[p];
            double wSum = 0;
            for (int m = 0; m < trees.Count; m++) wSum += alphas[m];
            if (wSum <= 0) return imp;
            for (int m = 0; m < trees.Count; m++)
            {
                var ti = trees[m].Importances;
                for (int j = 0; j < p; j++) imp[j] += alphas[m] * ti[j];
            }
            for (int j = 0; j < p; j++) imp[j] /= wSum;
            return imp;
        }

        static void Clip(double[] weight)
        {
            for (int i = 0; i < weight.Length; i++)
                if (weight[i] > 0 && weight[i] < MachineEps) weight[i] = MachineEps;
        }

        static bool Normalize(double[] weight)
        {
            double sum = 0;
            for (int i = 0; i < weight.Length; i++) sum += weight[i];
            if (!double.IsFinite(sum) || sum <= 0) return false;
            for (int i = 0; i < weight.Length; i++) weight[i] /= sum;
            return true;
        }

        static (int[] Rows, bool Sampled) TakeTrain(int n, int[]? labels, AdaBoostOptions opt, bool regression, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int cap = opt.MaxTrainingRows <= 0 ? DefaultMaxTrainingRows : opt.MaxTrainingRows;
            if (cap < 2) throw new DesignMatrixException("The training row cap must be at least 2.");
            if (n <= cap) return (DecisionTree.Identity(n), false);
            if (regression)
                return (RowSample.Random(n, cap, opt.Seed, out bool sampled), sampled);
            return (RowSample.Stratified(labels!, cap, opt.Seed, out bool sampledC), sampledC);
        }

        static AdaBoostOptions Validate(AdaBoostOptions opt, bool regression)
        {
            if (opt.Estimators < 1) throw new DesignMatrixException("n_estimators must be at least 1.");
            if (!(opt.LearningRate > 0) || double.IsNaN(opt.LearningRate))
                throw new DesignMatrixException("Learning rate must be a finite number greater than 0.");
            if (opt.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so sampling is reproducible.");
            if (opt.MaxDepth < 0) throw new DesignMatrixException("Max depth cannot be negative.");
            if (opt.MinSamplesLeaf < 1) throw new DesignMatrixException("min_samples_leaf must be at least 1.");
            if (!regression && opt.Criterion == TreeCriterion.Mse)
                throw new DesignMatrixException("MSE is a regression criterion. Use Gini or entropy for classification.");
            if (opt.TimeBudgetSeconds < 0) throw new DesignMatrixException("Time budget cannot be negative.");
            return opt;
        }

        internal static double[,] TakeRows(double[,] x, int[] rows)
        {
            int p = x.GetLength(1);
            var a = new double[rows.Length, p];
            for (int i = 0; i < rows.Length; i++)
                for (int j = 0; j < p; j++)
                    a[i, j] = x[rows[i], j];
            return a;
        }

        static int[] TakeInt(int[] y, int[] rows)
        {
            var a = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) a[i] = y[rows[i]];
            return a;
        }

        static double[] TakeDouble(double[] y, int[] rows)
        {
            var a = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) a[i] = y[rows[i]];
            return a;
        }
    }

    /// <summary>표본 가중 CART. 깊이 제한 얕은 트리. sklearn DecisionTree(splitter='best')의 가중 지니/엔트로피와 같은 중간점 규칙.</summary>
    internal sealed class WeightedStumpTree
    {
        const float FeatureThreshold = 1e-7f;
        const double ImpurityEpsilon = 2.220446049250313e-15;

        public int Feature = -1;
        public double Threshold;
        public int Prediction;
        public WeightedStumpTree? Left;
        public WeightedStumpTree? Right;
        public double[] Importances = Array.Empty<double>();

        public static WeightedStumpTree Fit(
            double[,] x, int[] y, double[] w, int classCount, int maxDepth, TreeCriterion criterion, int minLeaf,
            CancellationToken cancellation)
        {
            int n = y.Length, p = x.GetLength(1);
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            var imp = new double[p];
            var root = Build(x, y, w, idx, 0, n, 0, maxDepth, classCount, criterion, minLeaf, imp, cancellation);
            double sum = 0;
            for (int j = 0; j < p; j++) sum += imp[j];
            if (sum > 0)
                for (int j = 0; j < p; j++) imp[j] /= sum;
            root.Importances = imp;
            return root;
        }

        public int PredictRow(double[,] x, int row)
        {
            var node = this;
            while (node.Feature >= 0)
            {
                double v = x[row, node.Feature];
                node = (double.IsNaN(v) || v > node.Threshold ? node.Right : node.Left) ?? node;
                if (node.Feature < 0) break;
            }
            return node.Prediction;
        }

        public int[] Predict(double[,] x, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            var pred = new int[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                pred[i] = PredictRow(x, i);
            }
            return pred;
        }

        static WeightedStumpTree Build(
            double[,] x, int[] y, double[] w, int[] idx, int start, int end, int depth, int maxDepth,
            int classCount, TreeCriterion criterion, int minLeaf, double[] importances, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int nNode = end - start;
            var total = new double[classCount];
            double sumW = 0;
            var counts = new int[classCount];
            for (int i = start; i < end; i++)
            {
                int row = idx[i];
                double wi = w[row];
                sumW += wi;
                total[y[row]] += wi;
                counts[y[row]]++;
            }
            int pred = ArgMax(total);
            var node = new WeightedStumpTree { Feature = -1, Prediction = pred };
            if (depth >= maxDepth || nNode < 2 || nNode < 2 * minLeaf || sumW <= 0) return node;
            double parentImp = Impurity(total, sumW, criterion);
            if (parentImp <= ImpurityEpsilon) return node;
            if (!TrySplit(x, y, w, idx, start, end, classCount, criterion, minLeaf, parentImp, sumW, out int feature, out double threshold, out int leftCount, out double leftW, out double leftImp, out double rightW, out double rightImp, cancellation))
                return node;
            importances[feature] += sumW * parentImp - leftW * leftImp - rightW * rightImp;
            Array.Sort(idx, start, nNode, Comparer<int>.Create((a, b) =>
            {
                int cmp = x[a, feature].CompareTo(x[b, feature]);
                return cmp != 0 ? cmp : a.CompareTo(b);
            }));
            int mid = start + leftCount;
            if (mid <= start || mid >= end) return node;
            node.Feature = feature;
            node.Threshold = threshold;
            node.Left = Build(x, y, w, idx, start, mid, depth + 1, maxDepth, classCount, criterion, minLeaf, importances, cancellation);
            node.Right = Build(x, y, w, idx, mid, end, depth + 1, maxDepth, classCount, criterion, minLeaf, importances, cancellation);
            return node;
        }

        static bool TrySplit(
            double[,] x, int[] y, double[] w, int[] idx, int start, int end, int classCount, TreeCriterion criterion,
            int minLeaf, double parentImp, double parentW, out int feature, out double threshold, out int leftCount,
            out double leftW, out double leftImp, out double rightW, out double rightImp, CancellationToken cancellation)
        {
            feature = -1;
            threshold = 0;
            leftCount = 0;
            leftW = leftImp = rightW = rightImp = 0;
            int nNode = end - start;
            int p = x.GetLength(1);
            var vals = new double[nNode];
            var ord = new int[nNode];
            var leftCounts = new double[classCount];
            double bestProxy = double.NegativeInfinity;
            double[]? suffix = null;
            bool found = false;
            for (int f = 0; f < p; f++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < nNode; i++)
                {
                    ord[i] = i;
                    vals[i] = x[idx[start + i], f];
                }
                Array.Sort(vals, ord, 0, nNode);
                if ((float)vals[nNode - 1] <= (float)vals[0] + FeatureThreshold) continue;
                Array.Clear(leftCounts, 0, classCount);
                // 큰 노드: 오른쪽 가중 도수를 역방향 누적합으로 한 번에 만든다(임계값마다 재합산하면 O(n²)).
                // 작은 노드는 기존 순서 합산을 유지해 sklearn 참조와 같은 반올림을 보존한다.
                bool fast = nNode > FastSplitNodeSize;
                if (fast)
                {
                    suffix ??= new double[(nNode + 1) * classCount];
                    Array.Clear(suffix, 0, (nNode + 1) * classCount);
                    for (int t = nNode - 1; t >= 0; t--)
                    {
                        int row = idx[start + ord[t]];
                        Array.Copy(suffix, (t + 1) * classCount, suffix, t * classCount, classCount);
                        suffix[t * classCount + y[row]] += w[row];
                    }
                }
                int leftN = 0;
                double lw = 0;
                int s = 0;
                while (s < nNode)
                {
                    int groupEnd = s;
                    while (groupEnd + 1 < nNode && (float)vals[groupEnd + 1] <= (float)vals[groupEnd] + FeatureThreshold)
                        groupEnd++;
                    for (int t = s; t <= groupEnd; t++)
                    {
                        int row = idx[start + ord[t]];
                        leftCounts[y[row]] += w[row];
                        lw += w[row];
                        leftN++;
                    }
                    s = groupEnd + 1;
                    if (s >= nNode) break;
                    int rightN = nNode - leftN;
                    if (leftN < minLeaf || rightN < minLeaf) continue;
                    double rw = parentW - lw;
                    if (lw <= 0 || rw <= 0) continue;
                    double lImp = Impurity(leftCounts, lw, criterion);
                    // 오른쪽 가중 도수는 부모에서 왼쪽을 뺀다.
                    double rImp = fast
                        ? Impurity(new ReadOnlySpan<double>(suffix, s * classCount, classCount), rw, criterion)
                        : RightImpurity(y, w, idx, start, ord, nNode, classCount, leftCounts, rw, criterion, s);
                    double proxy = -(lw * lImp + rw * rImp);
                    if (proxy > bestProxy)
                    {
                        bestProxy = proxy;
                        float lo = (float)vals[s - 1];
                        float hi = (float)vals[s];
                        double thr = lo / 2.0 + hi / 2.0;
                        if (thr == hi || double.IsInfinity(thr)) thr = lo;
                        feature = f;
                        threshold = thr;
                        leftCount = leftN;
                        leftW = lw;
                        leftImp = lImp;
                        rightW = rw;
                        rightImp = rImp;
                        found = true;
                        _ = parentImp;
                    }
                }
            }
            return found;
        }

        static double RightImpurity(int[] y, double[] w, int[] idx, int start, int[] ord, int nNode, int classCount, double[] leftCounts, double rightW, TreeCriterion criterion, int from)
        {
            var right = new double[classCount];
            for (int t = from; t < nNode; t++)
            {
                int row = idx[start + ord[t]];
                right[y[row]] += w[row];
            }
            // leftCounts는 누적이라 오른쪽을 다시 더하는 편이 부모-왼쪽보다 반올림이 같다. 쓰지 않은 인자는 호출 형태를 고정한다.
            _ = leftCounts;
            _ = w;
            return Impurity(right, rightW, criterion);
        }

        /// <summary>이 크기를 넘는 노드는 역방향 누적합으로 분할 후보를 평가한다.</summary>
        const int FastSplitNodeSize = 2048;

        static double Impurity(double[] counts, double sum, TreeCriterion criterion)
            => Impurity(new ReadOnlySpan<double>(counts), sum, criterion);

        static double Impurity(ReadOnlySpan<double> counts, double sum, TreeCriterion criterion)
        {
            if (sum <= 0) return 0;
            if (criterion == TreeCriterion.Entropy)
            {
                double h = 0;
                for (int k = 0; k < counts.Length; k++)
                {
                    if (counts[k] <= 0) continue;
                    double pk = counts[k] / sum;
                    h -= pk * Math.Log(pk);
                }
                return h;
            }
            double s = 0;
            for (int k = 0; k < counts.Length; k++)
            {
                double pk = counts[k] / sum;
                s += pk * pk;
            }
            return 1 - s;
        }

        static int ArgMax(double[] counts)
        {
            int best = 0;
            for (int k = 1; k < counts.Length; k++)
                if (counts[k] > counts[best]) best = k;
            return best;
        }
    }

    /// <summary>numpy.random.RandomState (MT19937)의 randint·random·choice(replace, p)만. AdaBoost.R2 부트스트랩이 sklearn과 같게.</summary>
    internal sealed class NumpyRandom
    {
        const int N = 624;
        const int M = 397;
        const uint MatrixA = 0x9908b0df;
        const uint Upper = 0x80000000;
        const uint Lower = 0x7fffffff;
        readonly uint[] _mt = new uint[N];
        int _pos;

        public NumpyRandom(int seed)
        {
            uint s = (uint)seed;
            for (int pos = 0; pos < N; pos++)
            {
                _mt[pos] = s;
                s = 1812433253u * (s ^ (s >> 30)) + (uint)(pos + 1);
            }
            _pos = N;
        }

        public uint NextUInt()
        {
            if (_pos >= N)
            {
                Twist();
                _pos = 0;
            }
            uint y = _mt[_pos++];
            y ^= y >> 11;
            y ^= (y << 7) & 0x9d2c5680;
            y ^= (y << 15) & 0xefc60000;
            y ^= y >> 18;
            return y;
        }

        public double NextDouble()
        {
            uint a = NextUInt() >> 5;
            uint b = NextUInt() >> 6;
            return (a * 67108864.0 + b) / 9007199254740992.0;
        }

        public int Randint(int high)
        {
            uint max = (uint)(high - 1);
            uint mask = max;
            mask |= mask >> 1;
            mask |= mask >> 2;
            mask |= mask >> 4;
            mask |= mask >> 8;
            mask |= mask >> 16;
            uint value;
            do value = NextUInt() & mask;
            while (value > max);
            return (int)value;
        }

        public int[] Choice(double[] p)
        {
            int n = p.Length;
            var cdf = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += p[i];
                cdf[i] = sum;
            }
            double norm = cdf[n - 1];
            if (!(norm > 0)) throw new DesignMatrixException("AdaBoost sample weights are all zero.");
            var idx = new int[n];
            for (int i = 0; i < n; i++)
            {
                double u = NextDouble();
                int lo = 0, hi = n;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cdf[mid] / norm > u) hi = mid;
                    else lo = mid + 1;
                }
                idx[i] = lo >= n ? n - 1 : lo;
            }
            return idx;
        }

        void Twist()
        {
            for (int i = 0; i < N - M; i++)
            {
                uint y = (_mt[i] & Upper) | (_mt[i + 1] & Lower);
                _mt[i] = _mt[i + M] ^ (y >> 1) ^ ((y & 1) == 1 ? MatrixA : 0);
            }
            for (int i = N - M; i < N - 1; i++)
            {
                uint y = (_mt[i] & Upper) | (_mt[i + 1] & Lower);
                _mt[i] = _mt[i + (M - N)] ^ (y >> 1) ^ ((y & 1) == 1 ? MatrixA : 0);
            }
            uint last = (_mt[N - 1] & Upper) | (_mt[0] & Lower);
            _mt[N - 1] = _mt[M - 1] ^ (last >> 1) ^ ((last & 1) == 1 ? MatrixA : 0);
        }
    }
}
