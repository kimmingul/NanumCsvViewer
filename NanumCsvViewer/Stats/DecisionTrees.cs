using System.Globalization;
using System.Text;

namespace NanumCsvViewer.Stats
{
    public enum TreeCriterion { Gini, Entropy, Mse }

    /// <summary>Exact는 정렬 분할(sklearn CART와 같은 규칙). Binned는 분위 구간. Auto는 행 수에 따라 고른다.</summary>
    public enum TreeSplitMode { Auto, Exact, Binned }

    /// <summary>포레스트가 분할마다 뽑는 특성 수. Auto는 분류 sqrt(p), 회귀는 전부(sklearn 1.1+).</summary>
    public enum ForestFeatureMode { Auto, Sqrt, Log2, All }

    public sealed record DecisionTreeOptions
    {
        public TreeCriterion Criterion { get; init; } = TreeCriterion.Gini;
        /// <summary>0 이하면 제한 없음. 루트 깊이 0. 이 깊이의 노드는 분할하지 않는다.</summary>
        public int MaxDepth { get; init; } = 8;
        public int MinSamplesSplit { get; init; } = 2;
        public int MinSamplesLeaf { get; init; } = 1;
        public double MinImpurityDecrease { get; init; } = 0;
        public TreeSplitMode SplitMode { get; init; } = TreeSplitMode.Auto;
        /// <summary>구간 분할의 최대 구간 수. 256을 넘기면 256으로 자른다(byte 저장).</summary>
        public int Bins { get; init; } = 256;
        /// <summary>0이면 모든 특성. 그보다 작으면 시드로 뽑고 인덱스 오름차순으로 본다(동점은 작은 인덱스).</summary>
        public int MaxFeatures { get; init; } = 0;
        public int Seed { get; init; } = 1;
    }

    public sealed record RandomForestOptions
    {
        public int Trees { get; init; } = 50;
        public int MaxDepth { get; init; } = 12;
        public int MinSamplesSplit { get; init; } = 2;
        public int MinSamplesLeaf { get; init; } = 1;
        public ForestFeatureMode FeatureMode { get; init; } = ForestFeatureMode.Auto;
        public int Seed { get; init; } = 1;
        /// <summary>이 행 수를 넘으면 시드 고정 표본으로만 학습한다. 0 이하면 기본 상한.</summary>
        public int MaxTrainingRows { get; init; } = RandomForest.DefaultMaxTrainingRows;
        public TreeSplitMode SplitMode { get; init; } = TreeSplitMode.Auto;
        public int Bins { get; init; } = 64;
        public TreeCriterion Criterion { get; init; } = TreeCriterion.Gini;
    }

    /// <summary>
    /// CART. 정확 분할은 sklearn DecisionTree(splitter='best', 균등 가중)와 같은 불순도·중간점·
    /// FEATURE_THRESHOLD(1e-7, float32)를 쓴다. 동점은 특성 인덱스가 작은 쪽, 그다음 왼쪽 임계값
    /// (sklearn은 특성을 무작위 순서로 봐서 동점일 때만 다를 수 있다).
    /// 구간 분할은 분위 경계의 근사이며 <see cref="DecisionTreeModel.ApproximateSplits"/>로 표시한다.
    /// </summary>
    public static class DecisionTree
    {
        public const int AutoBinRowThreshold = 80_000;
        public const int DefaultBins = 256;
        const float FeatureThreshold = 1e-7f;
        const double ImpurityEpsilon = 2.220446049250313e-15; // 10 * double.Epsilon, sklearn EPSILON

        public static DecisionTreeModel FitClassification(
            double[,] x, int[] y, int classCount, DecisionTreeOptions? options = null, CancellationToken cancellation = default)
            => FitCore(x, y, null, classCount, false, options ?? new DecisionTreeOptions { Criterion = TreeCriterion.Gini }, null, null, cancellation);

        public static DecisionTreeModel FitRegression(
            double[,] x, double[] y, DecisionTreeOptions? options = null, CancellationToken cancellation = default)
            => FitCore(x, null, y, 0, true, options ?? new DecisionTreeOptions { Criterion = TreeCriterion.Mse }, null, null, cancellation);

        internal static DecisionTreeModel FitCore(
            double[,] x, int[]? yClass, double[]? yValue, int classCount, bool regression,
            DecisionTreeOptions options, int[]? sampleIndex, Random? rng, CancellationToken cancellation,
            ColumnBins? sharedBins = null)
        {
            options = Validate(x, yClass, yValue, classCount, regression, options);
            int n = x.GetLength(0), p = x.GetLength(1);
            var samples = sampleIndex ?? Identity(n);
            int nSamples = samples.Length;
            if (nSamples < 1) throw new DesignMatrixException("The training split is empty.");
            cancellation.ThrowIfCancellationRequested();

            bool binned = ResolveBinned(options, nSamples);
            int binCap = Math.Clamp(options.Bins, 2, 256);
            ColumnBins? bins = null;
            if (binned)
            {
                bins = sharedBins ?? BuildBins(x, samples, p, binCap, cancellation);
                binned = true;
            }

            var model = new Builder(x, yClass, yValue, classCount, regression, options, samples, rng, bins, cancellation).Build();
            model.ApproximateSplits = binned;
            model.BinCount = binned ? binCap : 0;
            return model;
        }

        public static TreeClassificationResult EvaluateClassification(
            double[,] x, int[] y, int classCount, ClassifierOptions eval, DecisionTreeOptions? tree = null,
            CancellationToken cancellation = default)
        {
            var opt = tree ?? new DecisionTreeOptions();
            ClassifierCommon.Validate(x, y, classCount, eval, neighbors: false);
            DecisionTreeModel? captured = null;
            var result = ClassifierCommon.Evaluate(x, y, classCount, eval, scaleColumns: null, cancellation,
                (trainX, trainY, testX, _) =>
                {
                    var fit = FitClassification(trainX, trainY, classCount, opt, cancellation);
                    captured = fit;
                    return fit.Predict(testX, cancellation);
                });
            bool allRows = eval.Scheme != EvalScheme.Holdout;
            var display = allRows
                ? FitClassification(x, y, classCount, opt, cancellation)
                : captured ?? throw new DesignMatrixException("Decision tree produced no training fit.");
            return new TreeClassificationResult { Evaluation = result, Display = display, DisplayUsesAllRows = allRows };
        }

        public static TreeRegressionResult EvaluateRegression(
            double[,] x, double[] y, ClassifierOptions eval, DecisionTreeOptions? tree = null,
            CancellationToken cancellation = default)
        {
            var opt = tree ?? new DecisionTreeOptions { Criterion = TreeCriterion.Mse };
            ValidateRegressionEval(x, y, eval);
            int n = x.GetLength(0);
            var dummy = new int[n];
            DecisionTreeModel? captured = null;
            if (eval.Scheme == EvalScheme.Holdout)
            {
                var split = ClassifierEvaluation.Holdout(dummy, eval.TestFraction, eval.Seed, stratified: false);
                var (pred, actual, baseline) = RegressSplit(x, y, split, opt, cancellation, ref captured);
                return new TreeRegressionResult
                {
                    Metrics = RegressionMetrics.From(actual, pred),
                    BaselineRmse = baseline,
                    Scheme = eval.Scheme,
                    Seed = eval.Seed,
                    TestFraction = eval.TestFraction,
                    Folds = eval.Folds,
                    TrainRows = split.Train.Length,
                    TestRows = split.Test.Length,
                    Display = captured!,
                    DisplayUsesAllRows = false,
                };
            }

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
            return new TreeRegressionResult
            {
                Metrics = RegressionMetrics.From(allActual, allPred),
                BaselineRmse = Math.Sqrt(baselineSse / n),
                Scheme = eval.Scheme,
                Seed = eval.Seed,
                TestFraction = eval.TestFraction,
                Folds = eval.Folds,
                TrainRows = n,
                TestRows = n,
                Display = FitRegression(x, y, opt, cancellation),
                DisplayUsesAllRows = true,
            };
        }

        static (double[] Pred, double[] Actual, double BaselineRmse) RegressSplit(
            double[,] x, double[] y, DataSplit split, DecisionTreeOptions opt, CancellationToken cancellation,
            ref DecisionTreeModel? captured)
        {
            var trainX = ClassifierCommon.Extract(x, split.Train, FeatureScaler.Fit(x, ScalingMethod.None, split.Train), null, cancellation);
            var testX = ClassifierCommon.Extract(x, split.Test, FeatureScaler.Fit(x, ScalingMethod.None), null, cancellation);
            var trainY = Take(y, split.Train);
            var fit = FitRegression(trainX, trainY, opt, cancellation);
            captured = fit;
            var pred = fit.PredictValue(testX, cancellation);
            var actual = Take(y, split.Test);
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

        internal static bool ResolveBinned(DecisionTreeOptions options, int rows) => options.SplitMode switch
        {
            TreeSplitMode.Exact => false,
            TreeSplitMode.Binned => true,
            _ => rows > AutoBinRowThreshold,
        };

        static DecisionTreeOptions Validate(double[,] x, int[]? yClass, double[]? yValue, int classCount, bool regression, DecisionTreeOptions options)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 1) throw new DesignMatrixException("Need at least 1 complete row.");
            if (options.MinSamplesSplit < 2) throw new DesignMatrixException("min_samples_split must be at least 2.");
            if (options.MinSamplesLeaf < 1) throw new DesignMatrixException("min_samples_leaf must be at least 1.");
            if (options.Bins < 2) throw new DesignMatrixException("Bin count must be at least 2.");
            if (options.MaxFeatures < 0) throw new DesignMatrixException("max_features cannot be negative.");
            if (regression)
            {
                if (yValue is null || yValue.Length != n) throw new ArgumentException("Regression target length must match rows.", nameof(yValue));
                if (options.Criterion != TreeCriterion.Mse)
                    options = options with { Criterion = TreeCriterion.Mse };
            }
            else
            {
                if (yClass is null || yClass.Length != n) throw new ArgumentException("Label length must match rows.", nameof(yClass));
                if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
                if (options.Criterion == TreeCriterion.Mse)
                    throw new DesignMatrixException("MSE is a regression criterion. Use Gini or entropy for classification.");
                for (int i = 0; i < n; i++)
                    if ((uint)yClass[i] >= (uint)classCount)
                        throw new DesignMatrixException("A class index is outside 0..K-1.");
            }
            return options;
        }

        static void ValidateRegressionEval(double[,] x, double[] y, ClassifierOptions eval)
        {
            int n = x.GetLength(0);
            if (x.GetLength(1) < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (y.Length != n) throw new ArgumentException("Target length must match rows.", nameof(y));
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (eval.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the split is reproducible.");
            if (eval.Scheme == EvalScheme.Holdout)
            {
                if (!(eval.TestFraction > 0 && eval.TestFraction < 1))
                    throw new DesignMatrixException("Test fraction must be between 0 and 1 (exclusive).");
            }
            else
            {
                if (eval.Folds < 2) throw new DesignMatrixException("k-fold needs at least 2 folds.");
                if (n < eval.Folds)
                    throw new DesignMatrixException($"Need at least {eval.Folds} complete rows for {eval.Folds}-fold cross-validation.");
            }
        }

        internal static int[] Identity(int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = i;
            return a;
        }

        internal static double[] Take(double[] y, int[] rows)
        {
            var a = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) a[i] = y[rows[i]];
            return a;
        }

        internal static ColumnBins BuildBins(double[,] x, int[] samples, int p, int maxBins, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            var edges = new double[p][];
            var ids = new byte[n * p];
            var col = new double[samples.Length];
            for (int f = 0; f < p; f++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < samples.Length; i++) col[i] = x[samples[i], f];
                Array.Sort(col);
                edges[f] = QuantileEdges(col, maxBins);
                for (int r = 0; r < n; r++)
                {
                    if ((r & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                    ids[r * p + f] = edges[f].Length == 0 ? (byte)0 : (byte)BinOf(x[r, f], edges[f]);
                }
            }
            return new ColumnBins(ids, edges, p);
        }

        internal static double[] QuantileEdges(double[] sorted, int maxBins)
        {
            int n = sorted.Length;
            if (n == 0) return Array.Empty<double>();
            int distinct = 1;
            for (int i = 1; i < n; i++) if (sorted[i] > sorted[i - 1]) distinct++;
            if (distinct <= maxBins)
            {
                var unique = new double[distinct];
                unique[0] = sorted[0];
                int w = 1;
                for (int i = 1; i < n; i++)
                    if (sorted[i] > sorted[i - 1]) unique[w++] = sorted[i];
                return unique;
            }
            var edges = new List<double>(maxBins);
            for (int b = 1; b <= maxBins; b++)
            {
                int idx = (int)((long)b * (n - 1) / maxBins);
                double v = sorted[idx];
                if (edges.Count == 0 || v > edges[^1]) edges.Add(v);
            }
            if (edges[^1] < sorted[n - 1]) edges.Add(sorted[n - 1]);
            return edges.ToArray();
        }

        internal static int BinOf(double value, double[] edges)
        {
            int lo = 0, hi = edges.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (value <= edges[mid]) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }

        sealed class Builder
        {
            readonly double[,] _x;
            readonly int[]? _yClass;
            readonly double[]? _yValue;
            readonly int _classes;
            readonly bool _regression;
            readonly DecisionTreeOptions _opt;
            readonly int[] _samples;
            readonly Random? _rng;
            readonly ColumnBins? _bins;
            readonly CancellationToken _ct;
            readonly int _p;
            readonly int _nTotal;
            readonly double[] _vals;
            readonly int[] _ord;

            readonly int _maxDepth;

            public Builder(double[,] x, int[]? yClass, double[]? yValue, int classCount, bool regression,
                DecisionTreeOptions options, int[] samples, Random? rng, ColumnBins? bins, CancellationToken cancellation)
            {
                _x = x;
                _yClass = yClass;
                _yValue = yValue;
                _classes = classCount;
                _regression = regression;
                _opt = options;
                _samples = samples;
                _rng = rng;
                _bins = bins;
                _ct = cancellation;
                _p = x.GetLength(1);
                _nTotal = samples.Length;
                _vals = new double[_nTotal];
                _ord = new int[_nTotal];

                _maxDepth = options.MaxDepth <= 0 ? int.MaxValue : options.MaxDepth;
            }

            public DecisionTreeModel Build()
            {
                var nodes = new List<Node>();
                var stack = new Stack<Job>();
                stack.Push(new Job(0, _nTotal, 0, null, true));
                Node? root = null;
                int leafCount = 0, depthSeen = 0;
                while (stack.Count > 0)
                {
                    var job = stack.Pop();
                    _ct.ThrowIfCancellationRequested();
                    int nNode = job.End - job.Start;
                    var node = MakeNode(job.Start, nNode);
                    nodes.Add(node);
                    if (job.Parent == null) root = node;
                    else if (job.IsLeft) job.Parent.Left = node;
                    else job.Parent.Right = node;
                    if (job.Depth > depthSeen) depthSeen = job.Depth;

                    bool stop = job.Depth >= _maxDepth
                        || nNode < _opt.MinSamplesSplit
                        || nNode < 2 * _opt.MinSamplesLeaf
                        || node.Impurity <= ImpurityEpsilon;
                    if (stop)
                    {
                        leafCount++;
                        continue;
                    }
                    var features = FeaturesForSplit();
                    if (!TrySplit(job.Start, job.End, node, features, out int leftCount, out var choice))
                    {
                        leafCount++;
                        continue;
                    }
                    if (choice.Improvement + ImpurityEpsilon < _opt.MinImpurityDecrease)
                    {
                        leafCount++;
                        continue;
                    }
                    int pos = Partition(job.Start, job.End, choice, leftCount);
                    if (pos <= job.Start || pos >= job.End)
                    {
                        leafCount++;
                        continue;
                    }
                    node.Feature = choice.Feature;
                    node.Threshold = choice.Threshold;
                    stack.Push(new Job(pos, job.End, job.Depth + 1, node, false));
                    stack.Push(new Job(job.Start, pos, job.Depth + 1, node, true));
                }

                var importances = Importances(nodes);
                return new DecisionTreeModel
                {
                    Root = root ?? throw new InvalidOperationException("Tree has no root."),
                    FeatureCount = _p,
                    ClassCount = _regression ? 0 : _classes,
                    Regression = _regression,
                    Criterion = _opt.Criterion,
                    Importances = importances,
                    Depth = depthSeen,
                    LeafCount = leafCount,
                    NodeCount = nodes.Count,
                };
            }

            int[] FeaturesForSplit()
            {
                int m = _opt.MaxFeatures <= 0 || _opt.MaxFeatures >= _p ? _p : _opt.MaxFeatures;
                if (m >= _p)
                {
                    var all = new int[_p];
                    for (int i = 0; i < _p; i++) all[i] = i;
                    return all;
                }
                var rng = _rng ?? new Random(_opt.Seed);
                var idx = new int[_p];
                for (int i = 0; i < _p; i++) idx[i] = i;
                for (int i = 0; i < m; i++)
                {
                    int j = i + rng.Next(_p - i);
                    (idx[i], idx[j]) = (idx[j], idx[i]);
                }
                var chosen = new int[m];
                Array.Copy(idx, chosen, m);
                Array.Sort(chosen);
                return chosen;
            }

            Node MakeNode(int start, int nNode)
            {
                var node = new Node { Feature = -1, Count = nNode };
                if (_regression)
                {
                    double sum = 0, sumSq = 0;
                    for (int i = 0; i < nNode; i++)
                    {
                        double v = _yValue![_samples[start + i]];
                        sum += v;
                        sumSq += v * v;
                    }
                    double mean = sum / nNode;
                    node.Value = mean;
                    node.Impurity = sumSq / nNode - mean * mean;
                    node.Prediction = 0;
                }
                else
                {
                    var counts = new int[_classes];
                    for (int i = 0; i < nNode; i++) counts[_yClass![_samples[start + i]]]++;
                    node.Impurity = ImpurityFromCounts(counts, nNode);
                    node.Prediction = ArgMax(counts);
                    node.Value = node.Prediction;
                    node.ClassCounts = counts;
                }
                return node;
            }

            bool TrySplit(int start, int end, Node node, int[] features, out int leftCount, out SplitChoice choice)
            {
                leftCount = 0;
                choice = null!;
                double bestProxy = double.NegativeInfinity;
                SplitChoice? best = null;
                int bestN = 0;
                int nNode = end - start;
                if (_bins != null)
                {
                    if (!TrySplitBinned(start, end, node, features, ref bestProxy, ref best, ref bestN))
                        return false;
                }
                else if (!TrySplitExact(start, end, node, features, nNode, ref bestProxy, ref best, ref bestN))
                    return false;
                if (best == null) return false;
                leftCount = bestN;
                choice = best;
                return true;
            }

            bool TrySplitExact(int start, int end, Node node, int[] features, int nNode,
                ref double bestProxy, ref SplitChoice? best, ref int bestN)
            {
                int[]? total = _regression ? null : CountClasses(start, end);
                var leftCounts = _regression ? null : new int[_classes];
                int visited = 0;
                foreach (int f in features)
                {
                    _ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < nNode; i++)
                    {
                        _ord[i] = i;
                        _vals[i] = _x[_samples[start + i], f];
                    }
                    Array.Sort(_vals, _ord, 0, nNode);
                    if ((float)_vals[nNode - 1] <= (float)_vals[0] + FeatureThreshold) continue;
                    if (leftCounts != null) Array.Clear(leftCounts, 0, _classes);
                    // 정렬 한 번 뒤 왼쪽 합·제곱합만 누적한다. 임계값마다 노드를 다시 훑지 않는다.
                    double featSum = 0, featSq = 0;
                    if (_regression)
                    {
                        for (int i = 0; i < nNode; i++)
                        {
                            double v = _yValue![_samples[start + _ord[i]]];
                            featSum += v;
                            featSq += v * v;
                        }
                    }
                    int leftN = 0;
                    double leftSum = 0, leftSq = 0;
                    int p = 0;
                    while (p < nNode)
                    {
                        if ((visited++ & 8191) == 0) _ct.ThrowIfCancellationRequested();
                        int groupEnd = p;
                        while (groupEnd + 1 < nNode && (float)_vals[groupEnd + 1] <= (float)_vals[groupEnd] + FeatureThreshold)
                            groupEnd++;
                        for (int t = p; t <= groupEnd; t++)
                        {
                            int row = _samples[start + _ord[t]];
                            if (_regression)
                            {
                                double v = _yValue![row];
                                leftSum += v;
                                leftSq += v * v;
                            }
                            else leftCounts![_yClass![row]]++;
                            leftN++;
                        }
                        p = groupEnd + 1;
                        if (p >= nNode) break;
                        int rightN = nNode - leftN;
                        if (leftN < _opt.MinSamplesLeaf || rightN < _opt.MinSamplesLeaf) continue;
                        double leftImp, rightImp, proxy;
                        if (_regression)
                        {
                            double rightSum = featSum - leftSum;
                            double rightSq = featSq - leftSq;
                            proxy = leftSum * leftSum / leftN + rightSum * rightSum / rightN;
                            leftImp = Mse(leftSum, leftSq, leftN);
                            rightImp = Mse(rightSum, rightSq, rightN);
                        }
                        else
                        {
                            leftImp = ImpurityFromCounts(leftCounts!, leftN);
                            rightImp = ImpurityFromCounts(Subtract(total!, leftCounts!), rightN);
                            proxy = -rightN * rightImp - leftN * leftImp;
                        }
                        if (proxy > bestProxy)
                        {
                            bestProxy = proxy;
                            float lo = (float)_vals[p - 1];
                            float hi = (float)_vals[p];
                            double threshold = lo / 2.0 + hi / 2.0;
                            if (threshold == hi || double.IsInfinity(threshold)) threshold = lo;
                            double improvement = Improvement(node.Impurity, leftImp, rightImp, nNode, leftN, rightN);
                            best = new SplitChoice(f, threshold, leftImp, rightImp, improvement, true);
                            bestN = leftN;
                        }
                    }
                }
                return best != null;
            }

            bool TrySplitBinned(int start, int end, Node node, int[] features,
                ref double bestProxy, ref SplitChoice? best, ref int bestN)
            {
                var bins = _bins!;
                int nNode = end - start;
                int maxBins = 0;
                foreach (int f in features) maxBins = Math.Max(maxBins, bins.Edges[f].Length);
                if (maxBins < 2) return false;
                int[]? total = _regression ? null : CountClasses(start, end);
                double totalSum = 0, totalSq = 0;
                if (_regression)
                {
                    for (int i = start; i < end; i++)
                    {
                        double v = _yValue![_samples[i]];
                        totalSum += v;
                        totalSq += v * v;
                    }
                }
                var hist = _regression ? null : new int[features.Length, maxBins, _classes];
                var hCount = _regression ? new int[features.Length, maxBins] : null;
                var hSum = _regression ? new double[features.Length, maxBins] : null;
                var hSq = _regression ? new double[features.Length, maxBins] : null;
                var featPos = new int[_p];
                Array.Fill(featPos, -1);
                for (int i = 0; i < features.Length; i++) featPos[features[i]] = i;
                for (int s = start; s < end; s++)
                {
                    if (((s - start) & 8191) == 0) _ct.ThrowIfCancellationRequested();
                    int row = _samples[s];
                    int baseIx = row * bins.P;
                    if (_regression)
                    {
                        double v = _yValue![row];
                        for (int f = 0; f < _p; f++)
                        {
                            int fp = featPos[f];
                            if (fp < 0) continue;
                            int b = bins.Ids[baseIx + f];
                            hCount![fp, b]++;
                            hSum![fp, b] += v;
                            hSq![fp, b] += v * v;
                        }
                    }
                    else
                    {
                        int yi = _yClass![row];
                        for (int f = 0; f < _p; f++)
                        {
                            int fp = featPos[f];
                            if (fp < 0) continue;
                            hist![fp, bins.Ids[baseIx + f], yi]++;
                        }
                    }
                }
                var leftCounts = _regression ? null : new int[_classes];
                for (int fi = 0; fi < features.Length; fi++)
                {
                    int f = features[fi];
                    var edges = bins.Edges[f];
                    if (edges.Length < 2) continue;
                    if (leftCounts != null) Array.Clear(leftCounts, 0, _classes);
                    int leftN = 0;
                    double leftSum = 0, leftSq = 0;
                    for (int b = 0; b < edges.Length - 1; b++)
                    {
                        int inBin = 0;
                        if (_regression)
                        {
                            inBin = hCount![fi, b];
                            leftSum += hSum![fi, b];
                            leftSq += hSq![fi, b];
                        }
                        else
                        {
                            for (int c = 0; c < _classes; c++)
                            {
                                int add = hist![fi, b, c];
                                leftCounts![c] += add;
                                inBin += add;
                            }
                        }
                        leftN += inBin;
                        if (inBin == 0) continue;
                        int rightN = nNode - leftN;
                        if (leftN < _opt.MinSamplesLeaf || rightN < _opt.MinSamplesLeaf) continue;
                        double leftImp, rightImp, proxy;
                        if (_regression)
                        {
                            double rightSum = totalSum - leftSum;
                            proxy = leftSum * leftSum / leftN + rightSum * rightSum / rightN;
                            leftImp = leftSq / leftN - (leftSum / leftN) * (leftSum / leftN);
                            double rightSq = totalSq - leftSq;
                            rightImp = rightSq / rightN - (rightSum / rightN) * (rightSum / rightN);
                        }
                        else
                        {
                            leftImp = ImpurityFromCounts(leftCounts!, leftN);
                            rightImp = ImpurityFromCounts(Subtract(total!, leftCounts!), rightN);
                            proxy = -rightN * rightImp - leftN * leftImp;
                        }
                        if (proxy > bestProxy)
                        {
                            bestProxy = proxy;
                            double improvement = Improvement(node.Impurity, leftImp, rightImp, nNode, leftN, rightN);
                            best = new SplitChoice(f, edges[b], leftImp, rightImp, improvement, false);
                            bestN = leftN;
                        }
                    }
                }
                return best != null;
            }
            int Partition(int start, int end, SplitChoice choice, int leftCount)
            {
                if (!choice.ByMembership)
                {
                    int w = start;
                    for (int t = start; t < end; t++)
                    {
                        if (_x[_samples[t], choice.Feature] <= choice.Threshold)
                        {
                            (_samples[w], _samples[t]) = (_samples[t], _samples[w]);
                            w++;
                        }
                    }
                    return w;
                }
                // 최적 위치만 기억했다가 이긴 특성을 한 번 더 정렬해 자른다. 후보마다 왼쪽 인덱스를 복사하지 않는다.
                Array.Sort(_samples, start, end - start, Comparer<int>.Create((a, b) =>
                {
                    int cmp = _x[a, choice.Feature].CompareTo(_x[b, choice.Feature]);
                    return cmp != 0 ? cmp : a.CompareTo(b);
                }));
                return start + leftCount;
            }
            int[] CountClasses(int start, int end)
            {
                var counts = new int[_classes];
                for (int i = start; i < end; i++) counts[_yClass![_samples[i]]]++;
                return counts;
            }




            static double Mse(double sum, double sumSq, int n) => sumSq / n - (sum / n) * (sum / n);

            double ImpurityFromCounts(int[] counts, int n)
            {
                if (n <= 0) return 0;
                if (_opt.Criterion == TreeCriterion.Entropy)
                {
                    double h = 0;
                    for (int c = 0; c < counts.Length; c++)
                    {
                        if (counts[c] == 0) continue;
                        double p = counts[c] / (double)n;
                        h -= p * Math.Log(p);
                    }
                    return h;
                }
                double sq = 0;
                for (int c = 0; c < counts.Length; c++) sq += (double)counts[c] * counts[c];
                return 1.0 - sq / ((double)n * n);
            }

            double Improvement(double parent, double left, double right, int nNode, int nLeft, int nRight)
                => (nNode / (double)_nTotal) * (parent - (nRight / (double)nNode) * right - (nLeft / (double)nNode) * left);

            static int[] Subtract(int[] total, int[] left)
            {
                var r = new int[total.Length];
                for (int i = 0; i < total.Length; i++) r[i] = total[i] - left[i];
                return r;
            }

            static int ArgMax(int[] counts)
            {
                int best = 0;
                for (int i = 1; i < counts.Length; i++)
                    if (counts[i] > counts[best]) best = i;
                return best;
            }

            double[] Importances(List<Node> nodes)
            {
                var imp = new double[_p];
                foreach (var node in nodes)
                {
                    if (node.Feature < 0 || node.Left == null || node.Right == null) continue;
                    imp[node.Feature] += node.Count * node.Impurity
                        - node.Left.Count * node.Left.Impurity
                        - node.Right.Count * node.Right.Impurity;
                }
                double sum = 0;
                for (int i = 0; i < _p; i++) sum += imp[i];
                if (sum > 0)
                    for (int i = 0; i < _p; i++) imp[i] /= sum;
                return imp;
            }

            readonly record struct Job(int Start, int End, int Depth, Node? Parent, bool IsLeft);
            sealed record SplitChoice(int Feature, double Threshold, double LeftImpurity, double RightImpurity, double Improvement, bool ByMembership);
        }
    }

    internal sealed class ColumnBins
    {
        public ColumnBins(byte[] ids, double[][] edges, int p)
        {
            Ids = ids;
            Edges = edges;
            P = p;
        }
        public byte[] Ids { get; }
        public double[][] Edges { get; }
        public int P { get; }
    }

    public sealed class Node
    {
        public int Feature = -1;
        public double Threshold;
        public double Impurity;
        public int Count;
        public int Prediction;
        public double Value;
        public int[]? ClassCounts;
        public Node? Left;
        public Node? Right;
    }

    public sealed class DecisionTreeModel
    {
        public required Node Root { get; init; }
        public required int FeatureCount { get; init; }
        public required int ClassCount { get; init; }
        public required bool Regression { get; init; }
        public required TreeCriterion Criterion { get; init; }
        /// <summary>정규화된 불순도 감소(MDI). 합이 1이거나, 분할이 없으면 0.</summary>
        public required double[] Importances { get; init; }
        public required int Depth { get; init; }
        public required int LeafCount { get; init; }
        public required int NodeCount { get; init; }
        public bool ApproximateSplits { get; internal set; }
        public int BinCount { get; internal set; }

        public int[] Predict(double[,] x, CancellationToken cancellation = default)
        {
            EnsureShape(x, regression: false);
            int n = x.GetLength(0);
            var pred = new int[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                pred[i] = PredictClass(x, i);
            }
            return pred;
        }

        public double[] PredictValue(double[,] x, CancellationToken cancellation = default)
        {
            EnsureShape(x, regression: true);
            int n = x.GetLength(0);
            var pred = new double[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                pred[i] = PredictNode(x, i).Value;
            }
            return pred;
        }

        public int PredictClass(double[,] x, int row) => PredictNode(x, row).Prediction;
        internal double ValueAt(double[,] x, int row) => PredictNode(x, row).Value;


        Node PredictNode(double[,] x, int row)
        {
            var node = Root;
            while (node.Feature >= 0)
            {
                double v = x[row, node.Feature];
                node = (double.IsNaN(v) || v > node.Threshold ? node.Right : node.Left) ?? node;
                if (node.Feature < 0) break;
            }
            return node;
        }

        void EnsureShape(double[,] x, bool regression)
        {
            if (regression != Regression) throw new InvalidOperationException(regression ? "This tree is not a regressor." : "This tree is not a classifier.");
            if (x.GetLength(1) != FeatureCount) throw new ArgumentException("Feature count does not match the fitted tree.", nameof(x));
        }

        public string Format(IReadOnlyList<string> featureNames, IReadOnlyList<string>? classNames, int maxDepth)
        {
            var sb = new StringBuilder();
            Write(sb, Root, featureNames, classNames, 0, maxDepth);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, Node node, IReadOnlyList<string> names, IReadOnlyList<string>? classes, int depth, int maxDepth)
        {
            string pad = new string(' ', depth * 2);
            if (node.Feature < 0 || depth >= maxDepth && node.Feature >= 0 && depth > 0 && maxDepth >= 0 && depth >= maxDepth)
            {
                if (node.Feature >= 0 && depth >= maxDepth)
                {
                    sb.Append(pad).AppendLine("…");
                    return;
                }
            }
            if (node.Feature < 0 || node.Left == null || node.Right == null)
            {
                sb.Append(pad);
                if (classes != null && node.Prediction >= 0 && node.Prediction < classes.Count)
                    sb.Append(classes[node.Prediction]);
                else
                    sb.Append(node.Value.ToString("G6", CultureInfo.InvariantCulture));
                sb.Append(" (n=").Append(node.Count.ToString(CultureInfo.InvariantCulture));
                sb.Append(", impurity=").Append(node.Impurity.ToString("G6", CultureInfo.InvariantCulture)).AppendLine(")");
                return;
            }
            if (depth >= maxDepth)
            {
                sb.Append(pad).AppendLine("…");
                return;
            }
            string fname = node.Feature < names.Count ? names[node.Feature] : "x" + node.Feature.ToString(CultureInfo.InvariantCulture);
            sb.Append(pad).Append(fname).Append(" <= ").Append(node.Threshold.ToString("G6", CultureInfo.InvariantCulture));
            sb.Append(" (n=").Append(node.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(")");
            Write(sb, node.Left, names, classes, depth + 1, maxDepth);
            Write(sb, node.Right, names, classes, depth + 1, maxDepth);
        }
    }

    public sealed class TreeClassificationResult
    {
        public required ClassifierEvalResult Evaluation { get; init; }
        public required DecisionTreeModel Display { get; init; }
        public required bool DisplayUsesAllRows { get; init; }
    }

    public sealed class TreeRegressionResult
    {
        public required RegressionMetrics Metrics { get; init; }
        /// <summary>평가 행에 학습 평균을 예측했을 때의 RMSE.</summary>
        public required double BaselineRmse { get; init; }
        public required EvalScheme Scheme { get; init; }
        public required int Seed { get; init; }
        public required double TestFraction { get; init; }
        public required int Folds { get; init; }
        public required int TrainRows { get; init; }
        public required int TestRows { get; init; }
        public required DecisionTreeModel Display { get; init; }
        public required bool DisplayUsesAllRows { get; init; }
    }


    public sealed class ForestClassificationResult
    {
        public required ClassifierEvalResult Evaluation { get; init; }
        public required RandomForestModel Model { get; init; }
        public required bool ModelUsesAllRows { get; init; }
    }

    public sealed class ForestRegressionResult
    {
        public required RegressionMetrics Metrics { get; init; }
        public required double BaselineRmse { get; init; }
        public required EvalScheme Scheme { get; init; }
        public required int Seed { get; init; }
        public required double TestFraction { get; init; }
        public required int Folds { get; init; }
        public required int TrainRows { get; init; }
        public required int TestRows { get; init; }
        public required RandomForestModel Model { get; init; }
        public required bool ModelUsesAllRows { get; init; }
    }

    /// <summary>
    /// 배깅 CART. 중요도는 트리별 정규화 불순도 감소(MDI)의 평균이며 순열 중요도가 아니다.
    /// OOB는 부트스트랩에 한 번도 안 들어간 행의 투표(분류) 또는 평균(회귀)이다.
    /// 학습 행이 상한을 넘으면 시드 고정 표본만 쓰고, 그 사실을 결과에 적는다.
    /// </summary>
    public static class RandomForest
    {
        public const int DefaultMaxTrainingRows = 80_000;
        public const int AutoBinRowThreshold = 12_000;
        public const string ImportanceMethod = "MDI";

        public static RandomForestModel FitClassification(
            double[,] x, int[] y, int classCount, RandomForestOptions? options = null, CancellationToken cancellation = default)
            => Fit(x, y, null, classCount, false, options ?? new RandomForestOptions(), cancellation);

        public static RandomForestModel FitRegression(
            double[,] x, double[] y, RandomForestOptions? options = null, CancellationToken cancellation = default)
            => Fit(x, null, y, 0, true, options ?? new RandomForestOptions { Criterion = TreeCriterion.Mse }, cancellation);

        public static ForestClassificationResult EvaluateClassification(
            double[,] x, int[] y, int classCount, ClassifierOptions eval, RandomForestOptions? forest = null,
            CancellationToken cancellation = default)
        {
            var opt = forest ?? new RandomForestOptions();
            ClassifierCommon.Validate(x, y, classCount, eval, neighbors: false);
            RandomForestModel? captured = null;
            var result = ClassifierCommon.Evaluate(x, y, classCount, eval, null, cancellation,
                (trainX, trainY, testX, _) =>
                {
                    var fit = FitClassification(trainX, trainY, classCount, opt, cancellation);
                    captured = fit;
                    return fit.Predict(testX, cancellation);
                });
            bool allRows = eval.Scheme != EvalScheme.Holdout;
            var display = allRows ? FitClassification(x, y, classCount, opt, cancellation) : captured!;
            return new ForestClassificationResult { Evaluation = result, Model = display, ModelUsesAllRows = allRows };
        }

        public static ForestRegressionResult EvaluateRegression(
            double[,] x, double[] y, ClassifierOptions eval, RandomForestOptions? forest = null,
            CancellationToken cancellation = default)
        {
            var opt = forest ?? new RandomForestOptions { Criterion = TreeCriterion.Mse };
            int n = x.GetLength(0);
            if (y.Length != n) throw new ArgumentException("Target length must match rows.", nameof(y));
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (eval.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the split is reproducible.");
            var dummy = new int[n];
            if (eval.Scheme == EvalScheme.Holdout)
            {
                var split = ClassifierEvaluation.Holdout(dummy, eval.TestFraction, eval.Seed, stratified: false);
                var (pred, actual, baseline, model) = ForestRegressSplit(x, y, split, opt, cancellation);
                return new ForestRegressionResult
                {
                    Metrics = RegressionMetrics.From(actual, pred),
                    BaselineRmse = baseline,
                    Scheme = eval.Scheme,
                    Seed = eval.Seed,
                    TestFraction = eval.TestFraction,
                    Folds = eval.Folds,
                    TrainRows = split.Train.Length,
                    TestRows = split.Test.Length,
                    Model = model,
                    ModelUsesAllRows = false,
                };
            }
            if (eval.Folds < 2) throw new DesignMatrixException("k-fold needs at least 2 folds.");
            if (n < eval.Folds) throw new DesignMatrixException($"Need at least {eval.Folds} complete rows for {eval.Folds}-fold cross-validation.");
            var folds = ClassifierEvaluation.KFold(dummy, eval.Folds, eval.Seed, stratified: false);
            var allActual = new double[n];
            var allPred = new double[n];
            double baselineSse = 0;
            foreach (var split in folds)
            {
                cancellation.ThrowIfCancellationRequested();
                var (pred, actual, baseline, _) = ForestRegressSplit(x, y, split, opt, cancellation);
                for (int i = 0; i < split.Test.Length; i++)
                {
                    allActual[split.Test[i]] = actual[i];
                    allPred[split.Test[i]] = pred[i];
                }
                baselineSse += baseline * baseline * split.Test.Length;
            }
            return new ForestRegressionResult
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

        static (double[] Pred, double[] Actual, double Baseline, RandomForestModel Model) ForestRegressSplit(
            double[,] x, double[] y, DataSplit split, RandomForestOptions opt, CancellationToken cancellation)
        {
            var trainX = ClassifierCommon.Extract(x, split.Train, FeatureScaler.Fit(x, ScalingMethod.None, split.Train), null, cancellation);
            var testX = ClassifierCommon.Extract(x, split.Test, FeatureScaler.Fit(x, ScalingMethod.None), null, cancellation);
            var trainY = DecisionTree.Take(y, split.Train);
            var fit = FitRegression(trainX, trainY, opt, cancellation);
            var pred = fit.PredictValue(testX, cancellation);
            var actual = DecisionTree.Take(y, split.Test);
            double mean = 0;
            for (int i = 0; i < trainY.Length; i++) mean += trainY[i];
            mean /= Math.Max(1, trainY.Length);
            double sse = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                double e = actual[i] - mean;
                sse += e * e;
            }
            return (pred, actual, actual.Length == 0 ? double.NaN : Math.Sqrt(sse / actual.Length), fit);
        }

        static RandomForestModel Fit(
            double[,] x, int[]? yClass, double[]? yValue, int classCount, bool regression,
            RandomForestOptions options, CancellationToken cancellation)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (options.Trees < 1) throw new DesignMatrixException("The forest needs at least 1 tree.");
            if (options.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the forest is reproducible.");
            if (options.MinSamplesSplit < 2) throw new DesignMatrixException("min_samples_split must be at least 2.");
            if (options.MinSamplesLeaf < 1) throw new DesignMatrixException("min_samples_leaf must be at least 1.");
            int cap = options.MaxTrainingRows <= 0 ? DefaultMaxTrainingRows : options.MaxTrainingRows;
            if (cap < 2) throw new DesignMatrixException("The training row cap must be at least 2.");

            int[] index;
            bool sampled;
            if (regression)
            {
                if (yValue is null || yValue.Length != n) throw new ArgumentException("Target length must match rows.", nameof(yValue));
                index = RowSample.Random(n, cap, options.Seed, out sampled);
            }
            else
            {
                if (yClass is null || yClass.Length != n) throw new ArgumentException("Label length must match rows.", nameof(yClass));
                if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
                index = RowSample.Stratified(yClass, cap, options.Seed, out sampled);
            }
            var trainX = index.Length == n ? x : Slice(x, index, cancellation);
            int[]? trainY = regression ? null : TakeInt(yClass!, index);
            double[]? trainV = regression ? DecisionTree.Take(yValue!, index) : null;
            int nTrain = trainX.GetLength(0);

            int maxFeatures = ResolveFeatures(options.FeatureMode, p, regression);
            bool binned = options.SplitMode switch
            {
                TreeSplitMode.Exact => false,
                TreeSplitMode.Binned => true,
                _ => nTrain > AutoBinRowThreshold,
            };
            int binCap = Math.Clamp(options.Bins, 2, 256);
            var treeOpt = new DecisionTreeOptions
            {
                Criterion = regression ? TreeCriterion.Mse : options.Criterion == TreeCriterion.Mse ? TreeCriterion.Gini : options.Criterion,
                MaxDepth = options.MaxDepth,
                MinSamplesSplit = options.MinSamplesSplit,
                MinSamplesLeaf = options.MinSamplesLeaf,
                SplitMode = binned ? TreeSplitMode.Binned : TreeSplitMode.Exact,
                Bins = binCap,
                MaxFeatures = maxFeatures,
                Seed = options.Seed,
            };
            ColumnBins? bins = binned
                ? DecisionTree.BuildBins(trainX, DecisionTree.Identity(nTrain), p, binCap, cancellation)
                : null;

            var trees = new DecisionTreeModel[options.Trees];
            var inBag = new bool[options.Trees][];
            var po = new ParallelOptions { CancellationToken = cancellation };
            try
            {
                Parallel.For(0, options.Trees, po, t =>
                {
                    var rng = new Random(MixSeed(options.Seed, t));
                    var bag = new int[nTrain];
                    var seen = new bool[nTrain];
                    for (int i = 0; i < nTrain; i++)
                    {
                        bag[i] = rng.Next(nTrain);
                        seen[bag[i]] = true;
                    }
                    trees[t] = DecisionTree.FitCore(trainX, trainY, trainV, classCount, regression, treeOpt, bag, rng, cancellation, bins);
                    inBag[t] = seen;
                });
            }
            catch (AggregateException ex)
            {
                foreach (var inner in ex.Flatten().InnerExceptions)
                    if (inner is OperationCanceledException or DesignMatrixException) throw inner;
                throw;
            }

            var importances = new double[p];
            for (int t = 0; t < trees.Length; t++)
                for (int f = 0; f < p; f++) importances[f] += trees[t].Importances[f];
            for (int f = 0; f < p; f++) importances[f] /= trees.Length;

            double oobScore = double.NaN, oobError = double.NaN;
            int oobRows = 0;
            if (regression)
                (oobScore, oobError, oobRows) = OobRegression(trainX, trainV!, trees, inBag, cancellation);
            else
                (oobScore, oobError, oobRows) = OobClassification(trainX, trainY!, classCount, trees, inBag, cancellation);

            return new RandomForestModel
            {
                Trees = trees,
                FeatureCount = p,
                ClassCount = regression ? 0 : classCount,
                Regression = regression,
                Importances = importances,
                OobScore = oobScore,
                OobError = oobError,
                OobRows = oobRows,
                TrainingRows = nTrain,
                SourceRows = n,
                Sampled = sampled,
                ApproximateSplits = binned,
                BinCount = binned ? binCap : 0,
                Seed = options.Seed,
                MaxFeatures = maxFeatures,
                TreeCount = trees.Length,
            };
        }

        static (double Score, double Error, int Rows) OobClassification(
            double[,] x, int[] y, int classCount, DecisionTreeModel[] trees, bool[][] inBag, CancellationToken cancellation)
        {
            int n = y.Length;
            var votes = new int[n * classCount];
            var seen = new int[n];
            for (int t = 0; t < trees.Length; t++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < n; i++)
                {
                    if (inBag[t][i]) continue;
                    votes[i * classCount + trees[t].PredictClass(x, i)]++;
                    seen[i]++;
                }
            }
            int used = 0, correct = 0;
            for (int i = 0; i < n; i++)
            {
                if (seen[i] == 0) continue;
                used++;
                int best = 0;
                for (int c = 1; c < classCount; c++)
                    if (votes[i * classCount + c] > votes[i * classCount + best]) best = c;
                if (best == y[i]) correct++;
            }
            if (used == 0) return (double.NaN, double.NaN, 0);
            double acc = (double)correct / used;
            return (acc, 1 - acc, used);
        }

        static (double Score, double Error, int Rows) OobRegression(
            double[,] x, double[] y, DecisionTreeModel[] trees, bool[][] inBag, CancellationToken cancellation)
        {
            int n = y.Length;
            var sum = new double[n];
            var seen = new int[n];
            for (int t = 0; t < trees.Length; t++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < n; i++)
                {
                    if (inBag[t][i]) continue;
                    sum[i] += trees[t].ValueAt(x, i);
                    seen[i]++;
                }
            }
            var actual = new List<double>();
            var pred = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (seen[i] == 0) continue;
                actual.Add(y[i]);
                pred.Add(sum[i] / seen[i]);
            }
            if (actual.Count == 0) return (double.NaN, double.NaN, 0);
            var metrics = RegressionMetrics.From(actual, pred);
            return (metrics.RSquared, metrics.Rmse, actual.Count);
        }


        internal static int ResolveFeatures(ForestFeatureMode mode, int p, bool regression) => mode switch
        {
            ForestFeatureMode.All => p,
            ForestFeatureMode.Sqrt => Math.Max(1, (int)Math.Sqrt(p)),
            ForestFeatureMode.Log2 => Math.Max(1, (int)Math.Log2(p)),
            _ => regression ? p : Math.Max(1, (int)Math.Sqrt(p)),
        };

        internal static int MixSeed(int seed, int tree)
        {
            unchecked
            {
                uint x = (uint)seed;
                x ^= (uint)tree * 0x9E3779B9u;
                x *= 0x85EBCA6Bu;
                x ^= x >> 13;
                x *= 0xC2B2AE35u;
                x ^= x >> 16;
                int s = (int)(x & 0x7FFFFFFF);
                return s == 0 ? 1 : s;
            }
        }

        static int[] TakeInt(int[] y, int[] rows)
        {
            var a = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) a[i] = y[rows[i]];
            return a;
        }

        static double[,] Slice(double[,] x, int[] rows, CancellationToken cancellation)
        {
            int p = x.GetLength(1);
            var m = new double[rows.Length, p];
            for (int i = 0; i < rows.Length; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++) m[i, j] = x[rows[i], j];
            }
            return m;
        }
    }

    public sealed class RandomForestModel
    {
        public required DecisionTreeModel[] Trees { get; init; }
        public required int FeatureCount { get; init; }
        public required int ClassCount { get; init; }
        public required bool Regression { get; init; }
        public required double[] Importances { get; init; }
        /// <summary>분류는 OOB 정확도, 회귀는 OOB R². 해당 행이 없으면 NaN.</summary>
        public required double OobScore { get; init; }
        /// <summary>분류는 OOB 오분류율, 회귀는 OOB RMSE.</summary>
        public required double OobError { get; init; }
        public required int OobRows { get; init; }
        public required int TrainingRows { get; init; }
        public required int SourceRows { get; init; }
        public required bool Sampled { get; init; }
        public required bool ApproximateSplits { get; init; }
        public required int BinCount { get; init; }
        public required int Seed { get; init; }
        public required int MaxFeatures { get; init; }
        public required int TreeCount { get; init; }

        public int[] Predict(double[,] x, CancellationToken cancellation = default)
        {
            if (Regression) throw new InvalidOperationException("This forest is a regressor.");
            if (x.GetLength(1) != FeatureCount) throw new ArgumentException("Feature count does not match.", nameof(x));
            int n = x.GetLength(0);
            var pred = new int[n];
            var votes = new int[ClassCount];
            for (int i = 0; i < n; i++)
            {
                if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                Array.Clear(votes, 0, votes.Length);
                for (int t = 0; t < Trees.Length; t++) votes[Trees[t].PredictClass(x, i)]++;
                int best = 0;
                for (int c = 1; c < votes.Length; c++)
                    if (votes[c] > votes[best]) best = c;
                pred[i] = best;
            }
            return pred;
        }

        public double[] PredictValue(double[,] x, CancellationToken cancellation = default)
        {
            if (!Regression) throw new InvalidOperationException("This forest is a classifier.");
            if (x.GetLength(1) != FeatureCount) throw new ArgumentException("Feature count does not match.", nameof(x));
            int n = x.GetLength(0);
            var pred = new double[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                double sum = 0;
                for (int t = 0; t < Trees.Length; t++) sum += Trees[t].ValueAt(x, i);
                pred[i] = sum / Trees.Length;
            }
            return pred;
        }

    }

    /// <summary>시드 고정 행 표본. 분류는 층화, 회귀는 단순 무작위. 결과는 원래 순서.</summary>
    internal static class RowSample
    {
        public static int[] Stratified(int[] labels, int maxRows, int seed, out bool sampled)
        {
            int n = labels.Length;
            if (n <= maxRows)
            {
                sampled = false;
                return DecisionTree.Identity(n);
            }
            sampled = true;
            int k = 0;
            for (int i = 0; i < n; i++) if (labels[i] + 1 > k) k = labels[i] + 1;
            var groups = new List<int>[k];
            for (int c = 0; c < k; c++) groups[c] = new List<int>();
            for (int i = 0; i < n; i++) groups[labels[i]].Add(i);
            int present = 0;
            for (int c = 0; c < k; c++) if (groups[c].Count > 0) present++;
            if (maxRows < present)
                throw new DesignMatrixException($"The training row cap ({maxRows}) is smaller than the number of classes ({present}).");

            var quota = new int[k];
            var frac = new double[k];
            int assigned = 0;
            for (int c = 0; c < k; c++)
            {
                if (groups[c].Count == 0) continue;
                double exact = (double)maxRows * groups[c].Count / n;
                quota[c] = Math.Min(groups[c].Count, Math.Max(1, (int)Math.Floor(exact)));
                frac[c] = exact - Math.Floor(exact);
                assigned += quota[c];
            }
            while (assigned > maxRows)
            {
                int best = -1;
                for (int c = 0; c < k; c++)
                    if (quota[c] > 1 && (best < 0 || quota[c] > quota[best])) best = c;
                if (best < 0) break;
                quota[best]--;
                assigned--;
            }
            var order = new List<int>();
            for (int c = 0; c < k; c++) if (groups[c].Count > 0) order.Add(c);
            order.Sort((a, b) => frac[b].CompareTo(frac[a]) != 0 ? frac[b].CompareTo(frac[a]) : a.CompareTo(b));
            int guard = 0;
            while (assigned < maxRows && guard++ < k + 2)
            {
                bool any = false;
                foreach (int c in order)
                {
                    if (assigned >= maxRows) break;
                    if (quota[c] < groups[c].Count) { quota[c]++; assigned++; any = true; }
                }
                if (!any) break;
            }
            var rng = new Random(seed);
            var chosen = new List<int>(assigned);
            for (int c = 0; c < k; c++)
            {
                if (quota[c] == 0) continue;
                var idx = groups[c].ToArray();
                Shuffle(idx, rng);
                for (int i = 0; i < quota[c]; i++) chosen.Add(idx[i]);
            }
            chosen.Sort();
            return chosen.ToArray();
        }

        public static int[] Random(int n, int maxRows, int seed, out bool sampled)
        {
            if (n <= maxRows)
            {
                sampled = false;
                return DecisionTree.Identity(n);
            }
            sampled = true;
            var idx = DecisionTree.Identity(n);
            Shuffle(idx, new Random(seed));
            var chosen = new int[maxRows];
            Array.Copy(idx, chosen, maxRows);
            Array.Sort(chosen);
            return chosen;
        }

        static void Shuffle(int[] a, Random rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }
    }
}
