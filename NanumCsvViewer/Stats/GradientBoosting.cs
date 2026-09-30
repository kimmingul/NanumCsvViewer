using System.Buffers;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    /// <summary>
    /// 히스토그램 그래디언트 부스팅(sklearn HistGradientBoosting / LightGBM·XGBoost hist 계열).
    /// 제곱오차 회귀, 이진 로그손실, 다항 소프트맥스(대각 헤시안 뉴턴 부스팅). 잎 우선(best-first) 성장.
    /// AdaBoost·CatBoost·NGBoost는 제공하지 않는다.
    /// </summary>
    public enum BoostingTask { Regression, Binary, Multiclass }

    public enum BoostingLoss { HalfSquaredError, HalfBinomial, HalfMultinomial }

    /// <summary>
    /// 적합·평가 옵션. 0인 최대 깊이·최대 잎 수는 제한 없음(sklearn의 None).
    /// 조기 종료는 검증 비율을 시드로 떼어 손실로 판단하며, 최고 반복으로 되돌리지 않는다(sklearn과 같음).
    /// </summary>
    public sealed record GradientBoostingOptions
    {
        public int MaxIterations { get; init; } = 100;
        public double LearningRate { get; init; } = 0.1;
        /// <summary>0이면 깊이 제한 없음.</summary>
        public int MaxDepth { get; init; }
        /// <summary>0이면 잎 수 제한 없음. 지정하면 2 이상.</summary>
        public int MaxLeafNodes { get; init; } = 31;
        public int MinSamplesLeaf { get; init; } = 20;
        public double L2Regularization { get; init; }
        /// <summary>비결측 값의 최대 구간 수(sklearn max_bins). 결측 구간은 따로 하나 예약한다.</summary>
        public int MaxBins { get; init; } = 255;
        public bool EarlyStopping { get; init; }
        public double ValidationFraction { get; init; } = 0.1;
        public int IterationsNoChange { get; init; } = 10;
        public double EarlyStoppingTolerance { get; init; } = 1e-7;
        public int Seed { get; init; } = 1;
        public EvalScheme Scheme { get; init; } = EvalScheme.Holdout;
        public double TestFraction { get; init; } = 0.3;
        public int Folds { get; init; } = 5;
        public long MemoryBudgetBytes { get; init; } = 1024L * 1024 * 1024;
    }

    /// <summary>한 번 적합된 히스토그램 부스팅 모델. 예측은 학습에 쓴 분위 경계로 구간을 다시 매긴다.</summary>
    public sealed class GradientBoostingModel
    {
        internal const double AlmostInf = 1e300;
        internal const double MinHessianToSplit = 1e-3;
        internal const int BinEdgeSubsample = 200_000;
        private const double ValueEps = 1e-15;

        private readonly double[][] _thresholds;
        private readonly PredNode[][][] _trees;
        private readonly int _missingBin;

        internal GradientBoostingModel(
            BoostingTask task, BoostingLoss loss, int classCount, int featureCount,
            double[] baseline, PredNode[][][] trees, double[][] thresholds, int[] binsPerFeature,
            double[] gain, int iterationsUsed, int maxIterations, bool earlyStopped,
            double[] trainLoss, double[]? validationLoss, int bestValidationIndex,
            bool binEdgesSubsampled, int binEdgeRows, int rowsFit, int rowsValidation,
            GradientBoostingOptions options)
        {
            Task = task;
            Loss = loss;
            ClassCount = classCount;
            FeatureCount = featureCount;
            Baseline = baseline;
            _trees = trees;
            _thresholds = thresholds;
            _missingBin = options.MaxBins;
            BinsPerFeature = binsPerFeature;
            Gain = gain;
            double total = 0;
            for (int j = 0; j < gain.Length; j++) total += gain[j];
            TotalGain = total;
            FeatureImportance = new double[gain.Length];
            if (total > 0)
                for (int j = 0; j < gain.Length; j++) FeatureImportance[j] = gain[j] / total;
            IterationsUsed = iterationsUsed;
            MaxIterations = maxIterations;
            EarlyStopped = earlyStopped;
            TrainLoss = trainLoss;
            ValidationLoss = validationLoss;
            BestValidationIndex = bestValidationIndex;
            BinEdgesSubsampled = binEdgesSubsampled;
            BinEdgeRows = binEdgeRows;
            RowsFit = rowsFit;
            RowsValidation = rowsValidation;
            LearningRate = options.LearningRate;
            MaxDepth = options.MaxDepth;
            MaxLeafNodes = options.MaxLeafNodes;
            MinSamplesLeaf = options.MinSamplesLeaf;
            L2Regularization = options.L2Regularization;
            MaxBins = options.MaxBins;
            Seed = options.Seed;
            EarlyStopping = options.EarlyStopping;
            ValidationFraction = options.ValidationFraction;
            IterationsNoChange = options.IterationsNoChange;
            EarlyStoppingTolerance = options.EarlyStoppingTolerance;
        }

        public BoostingTask Task { get; }
        public BoostingLoss Loss { get; }
        public int ClassCount { get; }
        public int FeatureCount { get; }
        /// <summary>회귀·이진은 길이 1, 다항은 클래스 수. 링크 공간의 절편.</summary>
        public double[] Baseline { get; }
        /// <summary>분할 이득 합(정규화 전). 특성 순서 = 입력 열 순서.</summary>
        public double[] Gain { get; }
        public double TotalGain { get; }
        /// <summary>이득 기준 중요도. 합이 1이거나(분할이 있으면) 전부 0.</summary>
        public double[] FeatureImportance { get; }
        public int[] BinsPerFeature { get; }
        public int IterationsUsed { get; }
        public int MaxIterations { get; }
        public bool EarlyStopped { get; }
        /// <summary>길이 = 반복+1. [0]은 첫 트리 전 절편 손실(평균 반제곱오차 또는 평균 로그손실).</summary>
        public double[] TrainLoss { get; }
        public double[]? ValidationLoss { get; }
        /// <summary>검증 손실이 가장 낮은 인덱스(0 = 절편). 조기 종료가 아니면 -1. 동점이면 앞쪽.</summary>
        public int BestValidationIndex { get; }
        public bool BinEdgesSubsampled { get; }
        public int BinEdgeRows { get; }
        public int RowsFit { get; }
        public int RowsValidation { get; }
        public double LearningRate { get; }
        public int MaxDepth { get; }
        public int MaxLeafNodes { get; }
        public int MinSamplesLeaf { get; }
        public double L2Regularization { get; }
        public int MaxBins { get; }
        public int Seed { get; }
        public bool EarlyStopping { get; }
        public double ValidationFraction { get; }
        public int IterationsNoChange { get; }
        public double EarlyStoppingTolerance { get; }

        public double[] PredictValues(double[,] x, CancellationToken cancellation = default)
            => PredictValues(x, null, cancellation);

        public double[] PredictValues(double[,] x, int[]? rows, CancellationToken cancellation = default)
        {
            if (Task != BoostingTask.Regression)
                throw new InvalidOperationException("PredictValues is for a regression model.");
            int n = rows?.Length ?? x.GetLength(0);
            RequireColumns(x);
            var dest = new double[n];
            var cols = DenseColumns(x, rows, n, cancellation);
            var trees = _trees[0];
            double baseline = Baseline[0];
            void Score(int i)
            {
                double s = baseline;
                for (int t = 0; t < trees.Length; t++) s += WalkDense(trees[t], cols, i);
                dest[i] = s;
            }
            if (n >= 4096) Parallel.For(0, n, new ParallelOptions { CancellationToken = cancellation }, Score);
            else for (int i = 0; i < n; i++) Score(i);
            return dest;
        }

        public int[] PredictClasses(double[,] x, CancellationToken cancellation = default)
            => PredictClasses(x, null, cancellation);

        public int[] PredictClasses(double[,] x, int[]? rows, CancellationToken cancellation = default)
        {
            if (Task == BoostingTask.Regression)
                throw new InvalidOperationException("PredictClasses is for a classification model.");
            int n = rows?.Length ?? x.GetLength(0);
            RequireColumns(x);
            var dest = new int[n];
            var cols = DenseColumns(x, rows, n, cancellation);
            if (Task == BoostingTask.Binary)
            {
                var trees = _trees[0];
                double baseline = Baseline[0];
                void Score(int i)
                {
                    double s = baseline;
                    for (int t = 0; t < trees.Length; t++) s += WalkDense(trees[t], cols, i);
                    dest[i] = s > 0 ? 1 : 0;
                }
                if (n >= 4096) Parallel.For(0, n, new ParallelOptions { CancellationToken = cancellation }, Score);
                else for (int i = 0; i < n; i++) Score(i);
                return dest;
            }
            int k = ClassCount;
            var baselineV = Baseline;
            var treesByClass = _trees;
            void ScoreMulti(int i)
            {
                int best = 0;
                double bestV = double.NegativeInfinity;
                for (int c = 0; c < k; c++)
                {
                    double s = baselineV[c];
                    var trees = treesByClass[c];
                    for (int t = 0; t < trees.Length; t++) s += WalkDense(trees[t], cols, i);
                    if (s > bestV) { bestV = s; best = c; }
                }
                dest[i] = best;
            }
            if (n >= 4096) Parallel.For(0, n, new ParallelOptions { CancellationToken = cancellation }, ScoreMulti);
            else for (int i = 0; i < n; i++) ScoreMulti(i);
            return dest;
        }

        private void RequireColumns(double[,] x)
        {
            if (x.GetLength(1) != FeatureCount)
                throw new ArgumentException("Feature count does not match the fitted model.", nameof(x));
        }

        private double[][] DenseColumns(double[,] x, int[]? rows, int n, CancellationToken cancellation)
        {
            int p = FeatureCount;
            var cols = new double[p][];
            for (int f = 0; f < p; f++) cols[f] = new double[n];
            void Fill(int i)
            {
                int row = rows == null ? i : rows[i];
                for (int f = 0; f < p; f++) cols[f][i] = x[row, f];
            }
            if (n >= 8192) Parallel.For(0, n, new ParallelOptions { CancellationToken = cancellation }, Fill);
            else for (int i = 0; i < n; i++) Fill(i);
            return cols;
        }

        private static double WalkDense(PredNode[] tree, double[][] cols, int i)
        {
            int node = 0;
            while (true)
            {
                var nd = tree[node];
                if (nd.Leaf) return nd.Value;
                double v = cols[nd.Feature][i];
                if (double.IsNaN(v))
                    node = nd.MissingLeft ? nd.Left : nd.Right;
                else
                    node = v <= nd.Threshold ? nd.Left : nd.Right;
            }
        }

        internal readonly struct PredNode
        {
            public bool Leaf { get; init; }
            public int Feature { get; init; }
            public double Threshold { get; init; }
            public double Value { get; init; }
            public int Left { get; init; }
            public int Right { get; init; }
            public bool MissingLeft { get; init; }
            public double Gain { get; init; }
        }
    }

    /// <summary>홀드아웃 또는 k-겹 평가. 조기 종료 검증은 바깥 학습 분할 안에서만 뗀다.</summary>
    public sealed class GradientBoostingReport
    {
        public required BoostingTask Task { get; init; }
        public required BoostingLoss Loss { get; init; }
        public required EvalScheme Scheme { get; init; }
        public required int Seed { get; init; }
        public required double TestFraction { get; init; }
        public required int Folds { get; init; }
        public required int TrainRows { get; init; }
        public required int TestRows { get; init; }
        public required RegressionMetrics? Regression { get; init; }
        public required RegressionMetrics? RegressionBaseline { get; init; }
        public required ClassificationMetrics? Classification { get; init; }
        public required double MajorityBaseline { get; init; }
        public required double[] FeatureImportance { get; init; }
        public required double TotalGain { get; init; }
        public required bool ImportanceAveragedAcrossFolds { get; init; }
        public required int[] FoldIterations { get; init; }
        public required bool[] FoldEarlyStopped { get; init; }
        public required double[] FoldFinalTrainLoss { get; init; }
        public required double[]? FoldFinalValidationLoss { get; init; }
        public required bool EarlyStopped { get; init; }
        public required double[]? TrainLoss { get; init; }
        public required double[]? ValidationLoss { get; init; }
        public required bool LossCurveIsFoldMean { get; init; }
        public required int BestValidationIndex { get; init; }
        public required int RowsFit { get; init; }
        public required int RowsValidation { get; init; }
        public required bool BinEdgesSubsampled { get; init; }
        public required int BinEdgeRows { get; init; }
        public required int[] BinsPerFeature { get; init; }
        public required double[] Baseline { get; init; }
        public required int IterationsUsed { get; init; }
        public required int MaxIterations { get; init; }
        public required double LearningRate { get; init; }
        public required int MaxDepth { get; init; }
        public required int MaxLeafNodes { get; init; }
        public required int MinSamplesLeaf { get; init; }
        public required double L2Regularization { get; init; }
        public required int MaxBins { get; init; }
        public required bool EarlyStopping { get; init; }
        public required double ValidationFraction { get; init; }
        public required int IterationsNoChange { get; init; }
        public required double EarlyStoppingTolerance { get; init; }
    }

    public static class GradientBoosting
    {
        public static GradientBoostingModel Fit(double[,] x, double[] y, GradientBoostingOptions? options = null, CancellationToken cancellation = default)
            => Fit(x, y, options, null, cancellation);

        public static GradientBoostingModel Fit(double[,] x, double[] y, GradientBoostingOptions? options, int[]? rows, CancellationToken cancellation = default)
        {
            options ??= new GradientBoostingOptions();
            int nAll = x.GetLength(0);
            if (y.Length != nAll) throw new ArgumentException("Target length must match rows.", nameof(y));
            var index = IndexOf(x, rows);
            ValidateCommon(x, index.Length, options, classification: false, classCount: 1);
            for (int i = 0; i < index.Length; i++)
                if (!double.IsFinite(y[index[i]]))
                    throw new DesignMatrixException("The numeric target must be finite.");
            return FitCore(x, index, y, null, 1, options, cancellation);
        }

        public static GradientBoostingModel Fit(double[,] x, int[] labels, int classCount, GradientBoostingOptions? options = null, CancellationToken cancellation = default)
            => Fit(x, labels, classCount, options, null, cancellation);

        public static GradientBoostingModel Fit(double[,] x, int[] labels, int classCount, GradientBoostingOptions? options, int[]? rows, CancellationToken cancellation = default)
        {
            options ??= new GradientBoostingOptions();
            int nAll = x.GetLength(0);
            if (labels.Length != nAll) throw new ArgumentException("Label length must match rows.", nameof(labels));
            var index = IndexOf(x, rows);
            ValidateCommon(x, index.Length, options, classification: true, classCount);
            for (int i = 0; i < index.Length; i++)
                if ((uint)labels[index[i]] >= (uint)classCount)
                    throw new DesignMatrixException("A class index is outside 0..K-1.");
            return FitCore(x, index, null, labels, classCount, options, cancellation);
        }

        public static GradientBoostingReport Evaluate(double[,] x, double[] y, GradientBoostingOptions? options = null, CancellationToken cancellation = default)
        {
            options ??= new GradientBoostingOptions();
            int n = x.GetLength(0);
            if (y.Length != n) throw new ArgumentException("Target length must match rows.", nameof(y));
            ValidateEval(n, options, classification: false);
            var dummy = new int[n];
            if (options.Scheme == EvalScheme.Holdout)
            {
                var split = ClassifierEvaluation.Holdout(dummy, options.TestFraction, options.Seed, stratified: false);
                var model = Fit(x, y, options, split.Train, cancellation);
                var pred = model.PredictValues(x, split.Test, cancellation);
                var actual = Take(y, split.Test);
                var baseline = Constant(model.Baseline[0], pred.Length);
                return ReportFrom(model, options, split.Train.Length, split.Test.Length,
                    RegressionMetrics.From(actual, pred), RegressionMetrics.From(actual, baseline),
                    null, double.NaN, false, new[] { model }, pred, null);
            }
            var folds = ClassifierEvaluation.KFold(dummy, options.Folds, options.Seed, stratified: false);
            return EvaluateRegressionFolds(x, y, options, folds, cancellation);
        }

        public static GradientBoostingReport Evaluate(double[,] x, int[] labels, int classCount, GradientBoostingOptions? options = null, CancellationToken cancellation = default)
        {
            options ??= new GradientBoostingOptions();
            int n = x.GetLength(0);
            if (labels.Length != n) throw new ArgumentException("Label length must match rows.", nameof(labels));
            if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            ValidateEval(n, options, classification: true);
            for (int i = 0; i < n; i++)
                if ((uint)labels[i] >= (uint)classCount)
                    throw new DesignMatrixException("A class index is outside 0..K-1.");
            if (options.Scheme == EvalScheme.Holdout)
            {
                var split = ClassifierEvaluation.Holdout(labels, options.TestFraction, options.Seed, stratified: true);
                var model = Fit(x, labels, classCount, options, split.Train, cancellation);
                var pred = model.PredictClasses(x, split.Test, cancellation);
                var actual = Take(labels, split.Test);
                return ReportFrom(model, options, split.Train.Length, split.Test.Length,
                    null, null, ClassifierEvaluation.Metrics(actual, pred, classCount),
                    Majority(labels, split.Test, classCount), false, new[] { model }, null, pred);
            }
            var folds = ClassifierEvaluation.KFold(labels, options.Folds, options.Seed, stratified: true);
            return EvaluateClassFolds(x, labels, classCount, options, folds, cancellation);
        }

        private static GradientBoostingModel FitCore(
            double[,] x, int[] rows, double[]? yReg, int[]? yClass, int classCount,
            GradientBoostingOptions options, CancellationToken cancellation)
        {
            bool regression = yReg != null;
            int p = x.GetLength(1);
            var (grow, val) = SplitEarly(rows, yClass, classCount, regression, options);
            int nGrow = grow.Length;
            int classes = regression || classCount == 2 ? 1 : classCount;
            long bytes = (long)nGrow * p + (long)nGrow * classes * 8L * (regression ? 2 : 3) + (long)nGrow * 4L;
            if (nGrow > GradientBoostingModel.BinEdgeSubsample) bytes += (long)nGrow * 4L;
            if (bytes > options.MemoryBudgetBytes) throw new AnalysisMemoryLimitException();

            var (thresholds, binsNonMissing, bins, subsampled, edgeRows) = Bin(x, grow, options, cancellation);
            var yGrow = regression ? Take(yReg!, grow) : null;
            var labelGrow = regression ? null : Take(yClass!, grow);
            double[] baseline = regression
                ? new[] { Mean(yGrow!) }
                : classCount == 2
                    ? new[] { Logit(Mean01(labelGrow!)) }
                    : MulticlassBaseline(labelGrow!, classCount);

            var raw = new double[classes][];
            for (int c = 0; c < classes; c++)
            {
                raw[c] = new double[nGrow];
                double b = baseline[regression || classCount == 2 ? 0 : c];
                for (int i = 0; i < nGrow; i++) raw[c][i] = b;
            }
            double[][]? valRaw = null;
            double[]? yVal = null;
            int[]? labelVal = null;
            if (val.Length > 0)
            {
                valRaw = new double[classes][];
                for (int c = 0; c < classes; c++)
                {
                    valRaw[c] = new double[val.Length];
                    double b = baseline[regression || classCount == 2 ? 0 : c];
                    for (int i = 0; i < val.Length; i++) valRaw[c][i] = b;
                }
                yVal = regression ? Take(yReg!, val) : null;
                labelVal = regression ? null : Take(yClass!, val);
            }

            var trainLoss = new List<double> { LossOf(regression, classCount, yGrow, labelGrow, raw) };
            List<double>? valLoss = valRaw == null ? null : new List<double> { LossOf(regression, classCount, yVal, labelVal, valRaw, x, val) };
            var trees = new List<GradientBoostingModel.PredNode[]>[classes];
            for (int c = 0; c < classes; c++) trees[c] = new List<GradientBoostingModel.PredNode[]>();
            var gain = new double[p];
            bool stopped = false;
            var grad = new double[classes][];
            var hess = regression ? null : new double[classes][];
            for (int c = 0; c < classes; c++)
            {
                grad[c] = new double[nGrow];
                if (hess != null) hess[c] = new double[nGrow];
            }

            for (int iter = 0; iter < options.MaxIterations; iter++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (regression)
                    FillResidual(raw[0], yGrow!, grad[0]);
                else if (classCount == 2)
                    BinaryGradHess(labelGrow!, raw[0], grad[0], hess![0]);
                else
                    MultiGradHess(labelGrow!, raw, grad, hess!);

                for (int c = 0; c < classes; c++)
                {
                    var tree = GrowTree(bins, binsNonMissing, thresholds, grad[c], hess?[c], raw[c], options, gain, cancellation);
                    trees[c].Add(tree);
                    if (valRaw != null) ApplyValidation(tree, valRaw[c], x, val);
                }
                trainLoss.Add(LossOf(regression, classCount, yGrow, labelGrow, raw));
                if (valLoss != null) valLoss.Add(LossOf(regression, classCount, yVal, labelVal, valRaw!, x, val));
                if (options.EarlyStopping && ShouldStop(valLoss!, options.IterationsNoChange, options.EarlyStoppingTolerance))
                {
                    stopped = true;
                    break;
                }
            }

            int best = -1;
            if (valLoss != null)
            {
                best = 0;
                for (int i = 1; i < valLoss.Count; i++)
                    if (valLoss[i] < valLoss[best]) best = i;
            }
            var packed = new GradientBoostingModel.PredNode[classes][][];
            for (int c = 0; c < classes; c++) packed[c] = trees[c].ToArray();
            var task = regression ? BoostingTask.Regression : classCount == 2 ? BoostingTask.Binary : BoostingTask.Multiclass;
            var loss = regression ? BoostingLoss.HalfSquaredError : classCount == 2 ? BoostingLoss.HalfBinomial : BoostingLoss.HalfMultinomial;
            return new GradientBoostingModel(
                task, loss, regression ? 1 : classCount, p, baseline, packed, thresholds, binsNonMissing,
                gain, trainLoss.Count - 1, options.MaxIterations, stopped,
                trainLoss.ToArray(), valLoss?.ToArray(), best,
                subsampled, edgeRows, nGrow, val.Length, options);
        }

        private static bool ShouldStop(List<double> losses, int noChange, double tol)
        {
            int reference = noChange + 1;
            if (losses.Count < reference) return false;
            double bar = losses[^reference] - tol;
            for (int i = losses.Count - noChange; i < losses.Count; i++)
                if (losses[i] < bar) return false;
            return true;
        }

        private static (int[] Grow, int[] Val) SplitEarly(int[] rows, int[]? labels, int classCount, bool regression, GradientBoostingOptions options)
        {
            if (!options.EarlyStopping) return (rows, Array.Empty<int>());
            int n = rows.Length;
            DataSplit split;
            try
            {
                if (regression)
                    split = RandomSplit(n, options.ValidationFraction, options.Seed);
                else
                {
                    var local = new int[n];
                    for (int i = 0; i < n; i++) local[i] = labels![rows[i]];
                    split = ClassifierEvaluation.Holdout(local, options.ValidationFraction, options.Seed, stratified: true);
                }
            }
            catch (InvalidOperationException)
            {
                throw new DesignMatrixException("The early-stopping validation split is empty. Lower the validation fraction or turn early stopping off.");
            }
            if (split.Train.Length < 2)
                throw new DesignMatrixException("Early stopping left fewer than 2 training rows. Lower the validation fraction or turn early stopping off.");
            return (Map(rows, split.Train), Map(rows, split.ValOrTest()));
        }

        private static int[] Map(int[] rows, int[] local)
        {
            var r = new int[local.Length];
            for (int i = 0; i < local.Length; i++) r[i] = rows[local[i]];
            return r;
        }

        private static DataSplit RandomSplit(int n, double fraction, int seed)
        {
            if (!(fraction > 0 && fraction < 1)) throw new ArgumentOutOfRangeException(nameof(fraction));
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Shuffle(idx, new Random(seed));
            int nTest = (int)Math.Round(n * fraction);
            nTest = Math.Min(Math.Max(nTest, 1), n - 1);
            var test = new int[nTest];
            var train = new int[n - nTest];
            Array.Copy(idx, test, nTest);
            Array.Copy(idx, nTest, train, 0, train.Length);
            Array.Sort(test);
            Array.Sort(train);
            if (test.Length == 0) throw new InvalidOperationException("empty");
            return new DataSplit(train, test);
        }

        private static void Shuffle(int[] a, Random rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        private static (double[][] Thresholds, int[] BinsNonMissing, byte[][] Bins, bool Subsampled, int EdgeRows) Bin(
            double[,] x, int[] rows, GradientBoostingOptions options, CancellationToken cancellation)
        {
            int n = rows.Length, p = x.GetLength(1);
            int[] edge = rows;
            bool sub = false;
            if (n > GradientBoostingModel.BinEdgeSubsample)
            {
                edge = Sample(rows, GradientBoostingModel.BinEdgeSubsample, options.Seed);
                sub = true;
            }
            var thresholds = new double[p][];
            var nonMissing = new int[p];
            var bins = new byte[p][];
            var col = new double[edge.Length];
            for (int f = 0; f < p; f++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < edge.Length; i++)
                {
                    double v = x[edge[i], f];
                    if (!double.IsFinite(v)) throw new DesignMatrixException("Feature values must be finite.");
                    col[i] = v;
                }
                var sorted = (double[])col.Clone();
                Array.Sort(sorted);
                thresholds[f] = ThresholdsOf(sorted, options.MaxBins);
                nonMissing[f] = thresholds[f].Length + 1;
                var mapped = new byte[n];
                var thr = thresholds[f];
                if (n >= 8192)
                {
                    int bad = 0;
                    Parallel.For(0, n, i =>
                    {
                        double v = x[rows[i], f];
                        if (!double.IsFinite(v)) Interlocked.Exchange(ref bad, 1);
                        else mapped[i] = MapBin(v, thr);
                    });
                    if (bad != 0) throw new DesignMatrixException("Feature values must be finite.");
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        double v = x[rows[i], f];
                        if (!double.IsFinite(v)) throw new DesignMatrixException("Feature values must be finite.");
                        mapped[i] = MapBin(v, thr);
                    }
                }
                bins[f] = mapped;
            }
            return (thresholds, nonMissing, bins, sub, edge.Length);
        }

        private static int[] Sample(int[] rows, int take, int seed)
        {
            var idx = (int[])rows.Clone();
            var rng = new Random(seed);
            for (int i = 0; i < take; i++)
            {
                int j = rng.Next(i, idx.Length);
                (idx[i], idx[j]) = (idx[j], idx[i]);
            }
            var chosen = new int[take];
            Array.Copy(idx, chosen, take);
            return chosen;
        }

        internal static double[] ThresholdsOf(double[] sorted, int maxBins)
        {
            int n = sorted.Length;
            if (n == 0) return Array.Empty<double>();
            int unique = 1;
            for (int i = 1; i < n; i++) if (sorted[i] != sorted[i - 1]) unique++;
            if (unique <= maxBins)
            {
                if (unique == 1) return Array.Empty<double>();
                var mid = new double[unique - 1];
                int u = 0;
                double prev = sorted[0];
                for (int i = 1; i < n && u < mid.Length; i++)
                {
                    if (sorted[i] == prev) continue;
                    double m = (prev + sorted[i]) * 0.5;
                    if (m > GradientBoostingModel.AlmostInf) m = GradientBoostingModel.AlmostInf;
                    mid[u++] = m;
                    prev = sorted[i];
                }
                return mid;
            }
            var thr = new double[maxBins - 1];
            for (int i = 0; i < thr.Length; i++)
            {
                double q = (i + 1) / (double)maxBins;
                double pos = (n - 1) * q;
                double fl = Math.Floor(pos);
                double cl = Math.Ceiling(pos);
                double m = fl == cl ? sorted[(int)fl] : (sorted[(int)fl] + sorted[(int)cl]) * 0.5;
                if (m > GradientBoostingModel.AlmostInf) m = GradientBoostingModel.AlmostInf;
                thr[i] = m;
            }
            return thr;
        }

        internal static byte MapBin(double x, double[] thresholds)
        {
            int left = 0, right = thresholds.Length;
            while (left < right)
            {
                int middle = left + (right - left - 1) / 2;
                if (x <= thresholds[middle]) right = middle;
                else left = middle + 1;
            }
            return (byte)left;
        }

        private const int ParallelMin = 32_768;
        private const int HistChunk = 65_536;

        private static GradientBoostingModel.PredNode[] GrowTree(
            byte[][] bins, int[] binsNonMissing, double[][] thresholds,
            double[] grad, double[]? hess, double[] trainRaw, GradientBoostingOptions options, double[] gain,
            CancellationToken cancellation)
        {
            int n = grad.Length, p = bins.Length;
            bool constantH = hess == null;
            var part = ArrayPool<int>.Shared.Rent(n);
            try
            {
            for (int i = 0; i < n; i++) part[i] = i;
            var rootHist = BuildHist(bins, part, 0, n, grad, hess, cancellation);
            double sumG = 0;
            int sumC = 0;
            double sumH = 0;
            for (int b = 0; b < options.MaxBins + 1; b++)
            {
                sumG += rootHist.SumG[0][b];
                sumC += rootHist.Count[0][b];
                if (!constantH) sumH += rootHist.SumH![0][b];
            }
            if (constantH) sumH = n;
            var root = new Node
            {
                Start = 0, Stop = n, Depth = 0, SumG = sumG, SumH = sumH, Value = 0, Hist = rootHist,
            };
            var leaves = new List<Node>();
            var heap = new GainHeap();
            if (n < 2 * options.MinSamplesLeaf || sumH < GradientBoostingModel.MinHessianToSplit)
                leaves.Add(root);
            else
                Consider(root, binsNonMissing, grad, hess, options, heap, leaves);

            int maxLeaves = options.MaxLeafNodes;
            while (heap.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var node = heap.Pop();
                var split = node.Split;
                int cut = Partition(part, node.Start, node.Stop, bins[split.Feature], split.Bin);
                if (cut - node.Start != split.NLeft)
                    throw new InvalidOperationException("Split partition does not match histogram counts.");
                var left = new Node
                {
                    Start = node.Start, Stop = cut, Depth = node.Depth + 1,
                    SumG = split.SumGL, SumH = split.SumHL, Value = split.ValueL,
                };
                var right = new Node
                {
                    Start = cut, Stop = node.Stop, Depth = node.Depth + 1,
                    SumG = split.SumGR, SumH = split.SumHR, Value = split.ValueR,
                };
                node.Left = left;
                node.Right = right;
                node.Feature = split.Feature;
                node.Bin = split.Bin;
                node.Gain = split.Gain;
                node.MissingLeft = left.Count > right.Count;
                gain[split.Feature] += split.Gain;

                int leafCount = leaves.Count + heap.Count + 2;
                if (maxLeaves > 0 && leafCount == maxLeaves)
                {
                    left.Final = true; leaves.Add(left);
                    right.Final = true; leaves.Add(right);
                    while (heap.Count > 0) { var pending = heap.Pop(); pending.Final = true; leaves.Add(pending); }
                    node.Hist = null;
                    break;
                }
                if (options.MaxDepth > 0 && left.Depth == options.MaxDepth)
                {
                    left.Final = true; leaves.Add(left);
                    right.Final = true; leaves.Add(right);
                    node.Hist = null;
                    continue;
                }
                if (left.Count < options.MinSamplesLeaf * 2) { left.Final = true; leaves.Add(left); }
                if (right.Count < options.MinSamplesLeaf * 2) { right.Final = true; leaves.Add(right); }
                if (!left.Final || !right.Final)
                {
                    Node small = left.Count < right.Count ? left : right;
                    Node large = small == left ? right : left;
                    small.Hist = BuildHist(bins, part, small.Start, small.Stop, grad, hess, cancellation);
                    large.Hist = Subtract(node.Hist!, small.Hist);
                    node.Hist = null;
                    if (!left.Final) Consider(left, binsNonMissing, grad, hess, options, heap, leaves);
                    if (!right.Final) Consider(right, binsNonMissing, grad, hess, options, heap, leaves);
                    if (left.Final) left.Hist = null;
                    if (right.Final) right.Hist = null;
                }
                else node.Hist = null;
            }
            double lr = options.LearningRate;
            for (int i = 0; i < leaves.Count; i++) leaves[i].Value *= lr;
            AddLeafValues(trainRaw, part, leaves);
            return Flatten(root, thresholds);
            }
            finally
            {
                ArrayPool<int>.Shared.Return(part);
            }
        }

        private static void Consider(Node node, int[] binsNonMissing, double[] grad, double[]? hess,
            GradientBoostingOptions options, GainHeap heap, List<Node> leaves)
        {
            var split = BestSplit(node, binsNonMissing, options);
            if (split.Gain <= 0)
            {
                node.Hist = null;
                leaves.Add(node);
                return;
            }
            node.Split = split;
            node.Gain = split.Gain;
            heap.Push(node);
        }

        private static Split BestSplit(Node node, int[] binsNonMissing, GradientBoostingOptions options)
        {
            var hist = node.Hist!;
            int p = hist.SumG.Length;
            bool constantH = hist.SumH == null;
            double parentLoss = node.Value * node.SumG;
            double l2 = options.L2Regularization;
            var best = new Split { Gain = -1, Feature = 0 };
            for (int f = 0; f < p; f++)
            {
                int end = binsNonMissing[f] - 1;
                if (end <= 0) continue;
                double gL = 0, hL = 0;
                int nL = 0;
                var sg = hist.SumG[f];
                var ct = hist.Count[f];
                var sh = hist.SumH?[f];
                for (int b = 0; b < end; b++)
                {
                    nL += ct[b];
                    int nR = node.Count - nL;
                    gL += sg[b];
                    hL += constantH ? ct[b] : sh![b];
                    if (nL < options.MinSamplesLeaf) continue;
                    if (nR < options.MinSamplesLeaf) break;
                    double hR = node.SumH - hL;
                    if (hL < GradientBoostingModel.MinHessianToSplit) continue;
                    if (hR < GradientBoostingModel.MinHessianToSplit) break;
                    double gR = node.SumG - gL;
                    double vL = NodeValue(gL, hL, l2);
                    double vR = NodeValue(gR, hR, l2);
                    double gain = parentLoss - vL * gL - vR * gR;
                    if (gain > best.Gain && gain > 0)
                    {
                        best = new Split
                        {
                            Gain = gain, Feature = f, Bin = b, NLeft = nL,
                            SumGL = gL, SumHL = hL, SumGR = gR, SumHR = hR,
                            ValueL = vL, ValueR = vR,
                        };
                    }
                }
            }
            return best;
        }

        private const double ValueEps = 1e-15;

        private static double NodeValue(double sumG, double sumH, double l2)
            => -sumG / (sumH + l2 + ValueEps);

        private static int Partition(int[] idx, int start, int stop, byte[] featureBins, int splitBin)
        {
            int lo = start;
            for (int i = start; i < stop; i++)
            {
                if (featureBins[idx[i]] <= splitBin)
                {
                    (idx[lo], idx[i]) = (idx[i], idx[lo]);
                    lo++;
                }
            }
            return lo;
        }

        private static Hist BuildHist(byte[][] bins, int[] part, int start, int stop, double[] grad, double[]? hess, CancellationToken cancellation)
        {
            int p = bins.Length;
            var hist = Hist.New(p, 256, hess != null);
            int n = stop - start;
            if (n >= ParallelMin)
                AccumulateChunked(bins, part, start, stop, grad, hess, hist, cancellation);
            else if (p >= 2 && n >= 4096)
                Parallel.For(0, p, new ParallelOptions { CancellationToken = cancellation }, f => Accumulate(bins[f], part, start, stop, grad, hess, hist.SumG[f], hist.SumH?[f], hist.Count[f]));
            else
            {
                for (int f = 0; f < p; f++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Accumulate(bins[f], part, start, stop, grad, hess, hist.SumG[f], hist.SumH?[f], hist.Count[f]);
                }
            }
            return hist;
        }

        // 청크 크기는 코어 수와 무관. 부분 합을 청크 번호 순으로 더해 결정적이다.
        private static void AccumulateChunked(byte[][] bins, int[] part, int start, int stop, double[] grad, double[]? hess, Hist hist, CancellationToken cancellation)
        {
            int n = stop - start;
            int chunks = (n + HistChunk - 1) / HistChunk;
            int p = bins.Length;
            int nBins = 256;
            bool withH = hess != null;
            var partialG = new double[chunks][][];
            var partialC = new int[chunks][][];
            var partialH = withH ? new double[chunks][][] : null;
            for (int c = 0; c < chunks; c++)
            {
                partialG[c] = new double[p][];
                partialC[c] = new int[p][];
                if (withH) partialH![c] = new double[p][];
                for (int f = 0; f < p; f++)
                {
                    partialG[c][f] = new double[nBins];
                    partialC[c][f] = new int[nBins];
                    if (withH) partialH![c][f] = new double[nBins];
                }
            }
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellation }, c =>
            {
                int a = start + c * HistChunk;
                int b = Math.Min(stop, a + HistChunk);
                for (int f = 0; f < p; f++)
                    Accumulate(bins[f], part, a, b, grad, hess, partialG[c][f], partialH?[c][f], partialC[c][f]);
            });
            for (int c = 0; c < chunks; c++)
                for (int f = 0; f < p; f++)
                {
                    var dg = hist.SumG[f];
                    var sg = partialG[c][f];
                    var dc = hist.Count[f];
                    var sc = partialC[c][f];
                    for (int bin = 0; bin < nBins; bin++)
                    {
                        dg[bin] += sg[bin];
                        dc[bin] += sc[bin];
                    }
                    if (!withH) continue;
                    var dh = hist.SumH![f];
                    var sh = partialH![c][f];
                    for (int bin = 0; bin < nBins; bin++) dh[bin] += sh[bin];
                }
        }

        private static void Accumulate(byte[] featureBins, int[] part, int start, int stop, double[] grad, double[]? hess, double[] sumG, double[]? sumH, int[] count)
        {
            if (hess == null)
            {
                for (int i = start; i < stop; i++)
                {
                    int row = part[i];
                    int b = featureBins[row];
                    sumG[b] += (float)grad[row];
                    count[b]++;
                }
            }
            else
            {
                for (int i = start; i < stop; i++)
                {
                    int row = part[i];
                    int b = featureBins[row];
                    sumG[b] += (float)grad[row];
                    sumH![b] += (float)hess[row];
                    count[b]++;
                }
            }
        }

        private static Hist Subtract(Hist parent, Hist small)
        {
            int p = parent.SumG.Length;
            int bins = parent.SumG[0].Length;
            for (int f = 0; f < p; f++)
            {
                var pg = parent.SumG[f];
                var sg = small.SumG[f];
                var pc = parent.Count[f];
                var sc = small.Count[f];
                for (int b = 0; b < bins; b++)
                {
                    pg[b] -= sg[b];
                    pc[b] -= sc[b];
                }
                if (parent.SumH != null)
                {
                    var ph = parent.SumH[f];
                    var sh = small.SumH![f];
                    for (int b = 0; b < bins; b++) ph[b] -= sh[b];
                }
            }
            return parent;
        }

        private static GradientBoostingModel.PredNode[] Flatten(Node root, double[][] thresholds)
        {
            var list = new List<GradientBoostingModel.PredNode>();
            void Walk(Node n)
            {
                int idx = list.Count;
                list.Add(default);
                int left = 0, right = 0;
                if (n.Left != null)
                {
                    left = list.Count;
                    Walk(n.Left);
                    right = list.Count;
                    Walk(n.Right!);
                }
                bool leaf = n.Left == null;
                list[idx] = new GradientBoostingModel.PredNode
                {
                    Leaf = leaf,
                    Feature = n.Feature,
                    Threshold = leaf ? 0 : thresholds[n.Feature][n.Bin],
                    Value = n.Value,
                    Left = left,
                    Right = right,
                    MissingLeft = n.MissingLeft,
                    Gain = leaf ? 0 : n.Gain,
                };
            }
            Walk(root);
            return list.ToArray();
        }

        private static void AddLeafValues(double[] raw, int[] part, List<Node> leaves)
        {
            for (int leaf = 0; leaf < leaves.Count; leaf++)
            {
                var node = leaves[leaf];
                double v = node.Value;
                if (v == 0) continue;
                int a = node.Start, b = node.Stop;
                int n = b - a;
                if (n >= ParallelMin)
                {
                    int chunks = (n + HistChunk - 1) / HistChunk;
                    Parallel.For(0, chunks, c =>
                    {
                        int from = a + c * HistChunk;
                        int to = Math.Min(b, from + HistChunk);
                        for (int i = from; i < to; i++) raw[part[i]] += v;
                    });
                }
                else
                {
                    for (int i = a; i < b; i++) raw[part[i]] += v;
                }
            }
        }

        private static void ApplyValidation(GradientBoostingModel.PredNode[] tree, double[] raw, double[,] x, int[] rows)
        {
            int n = rows.Length;
            if (n >= ParallelMin)
            {
                Parallel.For(0, n, i => raw[i] += WalkRaw(tree, x, rows[i]));
                return;
            }
            for (int i = 0; i < n; i++) raw[i] += WalkRaw(tree, x, rows[i]);
        }

        private static double WalkRaw(GradientBoostingModel.PredNode[] tree, double[,] x, int row)
        {
            int i = 0;
            while (true)
            {
                var node = tree[i];
                if (node.Leaf) return node.Value;
                double v = x[row, node.Feature];
                i = v <= node.Threshold ? node.Left : node.Right;
            }
        }

        private static void FillResidual(double[] raw, double[] y, double[] grad)
        {
            int n = raw.Length;
            if (n >= ParallelMin)
                Parallel.For(0, n, i => grad[i] = raw[i] - y[i]);
            else
                for (int i = 0; i < n; i++) grad[i] = raw[i] - y[i];
        }

        private static void BinaryGradHess(int[] y, double[] raw, double[] grad, double[] hess)
        {
            int n = y.Length;
            void One(int i)
            {
                double r = raw[i];
                double yv = y[i];
                if (r > -37)
                {
                    double e = Math.Exp(-r);
                    double den = 1 + e;
                    grad[i] = ((1 - yv) - yv * e) / den;
                    hess[i] = e / (den * den);
                }
                else
                {
                    double e = Math.Exp(r);
                    grad[i] = e - yv;
                    hess[i] = e;
                }
            }
            if (n >= ParallelMin) Parallel.For(0, n, One);
            else for (int i = 0; i < n; i++) One(i);
        }

        private static void MultiGradHess(int[] y, double[][] raw, double[][] grad, double[][] hess)
        {
            int n = y.Length, k = raw.Length;
            void One(int i, double[] p)
            {
                double max = raw[0][i];
                for (int c = 1; c < k; c++) if (raw[c][i] > max) max = raw[c][i];
                double sum = 0;
                for (int c = 0; c < k; c++)
                {
                    p[c] = Math.Exp(raw[c][i] - max);
                    sum += p[c];
                }
                int yi = y[i];
                for (int c = 0; c < k; c++)
                {
                    double pc = p[c] / sum;
                    grad[c][i] = pc - (c == yi ? 1 : 0);
                    hess[c][i] = pc * (1 - pc);
                }
            }
            if (n >= ParallelMin)
                Parallel.For(0, n, () => new double[k], (i, _, p) => { One(i, p); return p; }, _ => { });
            else
            {
                var p = new double[k];
                for (int i = 0; i < n; i++) One(i, p);
            }
        }

        private static double LossOf(bool regression, int classCount, double[]? yReg, int[]? yClass, double[][] raw, double[,]? x = null, int[]? rows = null)
        {
            if (regression) return MeanHalfSquared(yReg!, raw[0]);
            if (classCount == 2) return MeanHalfBinomial(yClass!, raw[0]);
            return MeanMulti(yClass!, raw);
        }

        private static double MeanHalfSquared(double[] y, double[] pred)
        {
            double s = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double d = pred[i] - y[i];
                s += 0.5 * d * d;
            }
            return s / y.Length;
        }

        private static double MeanHalfBinomial(int[] y, double[] raw)
        {
            double s = 0;
            for (int i = 0; i < y.Length; i++) s += Log1pExp(raw[i]) - y[i] * raw[i];
            return s / y.Length;
        }

        private static double MeanMulti(int[] y, double[][] raw)
        {
            int n = y.Length, k = raw.Length;
            double Sum(int a, int b)
            {
                double s = 0;
                for (int i = a; i < b; i++)
                {
                    double max = raw[0][i];
                    for (int c = 1; c < k; c++) if (raw[c][i] > max) max = raw[c][i];
                    double sum = 0;
                    for (int c = 0; c < k; c++) sum += Math.Exp(raw[c][i] - max);
                    s += Math.Log(sum) + max - raw[y[i]][i];
                }
                return s;
            }
            return Reduce(n, Sum) / n;
        }

        private static double Reduce(int n, Func<int, int, double> sumRange)
        {
            if (n < ParallelMin) return sumRange(0, n);
            int chunks = (n + HistChunk - 1) / HistChunk;
            var parts = new double[chunks];
            Parallel.For(0, chunks, c =>
            {
                int a = c * HistChunk;
                parts[c] = sumRange(a, Math.Min(n, a + HistChunk));
            });
            double s = 0;
            for (int c = 0; c < chunks; c++) s += parts[c];
            return s;
        }

        private static double Log1pExp(double x)
        {
            if (x <= -37) return Math.Exp(x);
            if (x <= 18) return Math.Log(1 + Math.Exp(x));
            if (x <= 33.231948866385) return x + Math.Log(1 + Math.Exp(-x));
            return x;
        }

        private static double Mean(double[] y)
        {
            double s = 0;
            for (int i = 0; i < y.Length; i++) s += y[i];
            return s / y.Length;
        }

        private static double Mean01(int[] y)
        {
            double s = 0;
            for (int i = 0; i < y.Length; i++) s += y[i];
            return s / y.Length;
        }

        private static double Logit(double p)
        {
            double eps = 10 * double.Epsilon;
            p = Math.Clamp(p, eps, 1 - eps);
            return Math.Log(p / (1 - p));
        }

        private static double[] MulticlassBaseline(int[] y, int k)
        {
            var count = new int[k];
            for (int i = 0; i < y.Length; i++) count[y[i]]++;
            double eps = double.Epsilon;
            var logP = new double[k];
            double meanLog = 0;
            for (int c = 0; c < k; c++)
            {
                double p = Math.Clamp(count[c] / (double)y.Length, eps, 1 - eps);
                logP[c] = Math.Log(p);
                meanLog += logP[c];
            }
            meanLog /= k;
            var b = new double[k];
            for (int c = 0; c < k; c++) b[c] = logP[c] - meanLog;
            return b;
        }

        private static void ValidateCommon(double[,] x, int n, GradientBoostingOptions o, bool classification, int classCount)
        {
            int p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (o.MaxIterations < 1) throw new DesignMatrixException("Max iterations must be at least 1.");
            if (!(o.LearningRate > 0) || !double.IsFinite(o.LearningRate))
                throw new DesignMatrixException("Learning rate must be a finite number greater than 0.");
            if (o.MaxLeafNodes != 0 && o.MaxLeafNodes < 2)
                throw new DesignMatrixException("Max leaf nodes must be 0 (no limit) or at least 2.");
            if (o.MaxDepth < 0) throw new DesignMatrixException("Max depth cannot be negative. Use 0 for no limit.");
            if (o.MinSamplesLeaf < 1) throw new DesignMatrixException("Min samples per leaf must be at least 1.");
            if (o.L2Regularization < 0 || !double.IsFinite(o.L2Regularization))
                throw new DesignMatrixException("L2 regularization must be a finite number ≥ 0.");
            if (o.MaxBins < 2 || o.MaxBins > 255) throw new DesignMatrixException("Max bins must be between 2 and 255.");
            if (o.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the run is reproducible.");
            if (o.EarlyStopping)
            {
                if (!(o.ValidationFraction > 0 && o.ValidationFraction < 1))
                    throw new DesignMatrixException("Validation fraction must be between 0 and 1 (exclusive).");
                if (o.IterationsNoChange < 1) throw new DesignMatrixException("Iterations with no change must be at least 1.");
                if (!(o.EarlyStoppingTolerance >= 0) || !double.IsFinite(o.EarlyStoppingTolerance))
                    throw new DesignMatrixException("Early-stopping tolerance must be a finite number ≥ 0.");
            }
            if (classification && classCount < 2)
                throw new DesignMatrixException("The target has only one class in the complete rows.");
        }

        private static void ValidateEval(int n, GradientBoostingOptions o, bool classification)
        {
            if (o.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the split is reproducible.");
            if (o.Scheme == EvalScheme.Holdout)
            {
                if (!(o.TestFraction > 0 && o.TestFraction < 1))
                    throw new DesignMatrixException("Test fraction must be between 0 and 1 (exclusive).");
            }
            else
            {
                if (o.Folds < 2) throw new DesignMatrixException("k-fold needs at least 2 folds.");
                if (n < o.Folds)
                    throw new DesignMatrixException($"Need at least {o.Folds} complete rows for {o.Folds}-fold cross-validation.");
            }
        }

        private static int[] IndexOf(double[,] x, int[]? rows)
        {
            int n = x.GetLength(0);
            if (rows == null)
            {
                var all = new int[n];
                for (int i = 0; i < n; i++) all[i] = i;
                return all;
            }
            for (int i = 0; i < rows.Length; i++)
                if ((uint)rows[i] >= (uint)n) throw new ArgumentOutOfRangeException(nameof(rows));
            return rows;
        }

        private static double[] Take(double[] src, int[] rows)
        {
            var y = new double[rows.Length];
            for (int i = 0; i < rows.Length; i++) y[i] = src[rows[i]];
            return y;
        }

        private static int[] Take(int[] src, int[] rows)
        {
            var y = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) y[i] = src[rows[i]];
            return y;
        }

        private static double[] Constant(double v, int n)
        {
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = v;
            return a;
        }

        private static double Majority(int[] labels, int[] subset, int classCount)
        {
            var counts = new long[classCount];
            for (int i = 0; i < subset.Length; i++) counts[labels[subset[i]]]++;
            long max = 0;
            for (int c = 0; c < classCount; c++) if (counts[c] > max) max = counts[c];
            return subset.Length == 0 ? double.NaN : (double)max / subset.Length;
        }

        private static GradientBoostingReport EvaluateRegressionFolds(double[,] x, double[] y, GradientBoostingOptions options, IReadOnlyList<DataSplit> folds, CancellationToken cancellation)
        {
            int n = y.Length;
            var pred = new double[n];
            var baseline = new double[n];
            var models = new GradientBoostingModel[folds.Count];
            for (int f = 0; f < folds.Count; f++)
            {
                cancellation.ThrowIfCancellationRequested();
                var split = folds[f];
                if (split.Train.Length < 2 || split.Test.Length == 0)
                    throw new DesignMatrixException("A cross-validation fold is empty. Use fewer folds or more rows.");
                models[f] = Fit(x, y, options, split.Train, cancellation);
                var p = models[f].PredictValues(x, split.Test, cancellation);
                for (int i = 0; i < split.Test.Length; i++)
                {
                    pred[split.Test[i]] = p[i];
                    baseline[split.Test[i]] = models[f].Baseline[0];
                }
            }
            return ReportFrom(models[0], options, n, n, RegressionMetrics.From(y, pred), RegressionMetrics.From(y, baseline),
                null, double.NaN, true, models, null, null);
        }

        private static GradientBoostingReport EvaluateClassFolds(double[,] x, int[] labels, int classCount, GradientBoostingOptions options, IReadOnlyList<DataSplit> folds, CancellationToken cancellation)
        {
            int n = labels.Length;
            var pred = new int[n];
            var models = new GradientBoostingModel[folds.Count];
            for (int f = 0; f < folds.Count; f++)
            {
                cancellation.ThrowIfCancellationRequested();
                var split = folds[f];
                if (split.Train.Length < 2 || split.Test.Length == 0)
                    throw new DesignMatrixException("A cross-validation fold is empty. Use fewer folds or more rows.");
                models[f] = Fit(x, labels, classCount, options, split.Train, cancellation);
                var p = models[f].PredictClasses(x, split.Test, cancellation);
                for (int i = 0; i < split.Test.Length; i++) pred[split.Test[i]] = p[i];
            }
            return ReportFrom(models[0], options, n, n, null, null,
                ClassifierEvaluation.Metrics(labels, pred, classCount), Majority(labels, Enumerable.Range(0, n).ToArray(), classCount),
                true, models, null, pred);
        }

        private static GradientBoostingReport ReportFrom(
            GradientBoostingModel sample, GradientBoostingOptions options, int trainRows, int testRows,
            RegressionMetrics? regression, RegressionMetrics? regressionBaseline,
            ClassificationMetrics? classification, double majority, bool folds,
            GradientBoostingModel[] models, double[]? holdoutPred, int[]? holdoutClass)
        {
            var gain = new double[sample.FeatureCount];
            if (folds)
            {
                for (int m = 0; m < models.Length; m++)
                    for (int j = 0; j < gain.Length; j++) gain[j] += models[m].Gain[j];
            }
            else
                gain = sample.Gain;
            double total = 0;
            for (int j = 0; j < gain.Length; j++) total += gain[j];
            var imp = new double[gain.Length];
            if (total > 0)
                for (int j = 0; j < gain.Length; j++) imp[j] = gain[j] / total;

            var foldIter = new int[models.Length];
            var foldStop = new bool[models.Length];
            var foldTrain = new double[models.Length];
            double[]? foldVal = models[0].ValidationLoss == null ? null : new double[models.Length];
            bool sameLen = true;
            bool anyStop = false;
            for (int i = 0; i < models.Length; i++)
            {
                foldIter[i] = models[i].IterationsUsed;
                foldStop[i] = models[i].EarlyStopped;
                anyStop |= models[i].EarlyStopped;
                foldTrain[i] = models[i].TrainLoss[^1];
                if (foldVal != null) foldVal[i] = models[i].ValidationLoss![^1];
                if (models[i].TrainLoss.Length != models[0].TrainLoss.Length) sameLen = false;
                if (models[i].BinEdgesSubsampled != models[0].BinEdgesSubsampled) { }
            }
            double[]? curve = null;
            double[]? vcurve = null;
            bool meanCurve = false;
            if (!folds)
            {
                curve = sample.TrainLoss;
                vcurve = sample.ValidationLoss;
            }
            else if (sameLen)
            {
                meanCurve = true;
                curve = new double[models[0].TrainLoss.Length];
                for (int m = 0; m < models.Length; m++)
                    for (int i = 0; i < curve.Length; i++) curve[i] += models[m].TrainLoss[i];
                for (int i = 0; i < curve.Length; i++) curve[i] /= models.Length;
                var val0 = models[0].ValidationLoss;
                if (val0 != null && models.All(m => m.ValidationLoss != null && m.ValidationLoss.Length == val0.Length))
                {
                    vcurve = new double[val0.Length];
                    for (int m = 0; m < models.Length; m++)
                        for (int i = 0; i < vcurve.Length; i++) vcurve[i] += models[m].ValidationLoss![i];
                    for (int i = 0; i < vcurve.Length; i++) vcurve[i] /= models.Length;
                }
            }
            int rowsFit = 0, rowsVal = 0;
            for (int i = 0; i < models.Length; i++) { rowsFit += models[i].RowsFit; rowsVal += models[i].RowsValidation; }
            if (!folds) { rowsFit = sample.RowsFit; rowsVal = sample.RowsValidation; }
            return new GradientBoostingReport
            {
                Task = sample.Task,
                Loss = sample.Loss,
                Scheme = options.Scheme,
                Seed = options.Seed,
                TestFraction = options.TestFraction,
                Folds = options.Folds,
                TrainRows = trainRows,
                TestRows = testRows,
                Regression = regression,
                RegressionBaseline = regressionBaseline,
                Classification = classification,
                MajorityBaseline = majority,
                FeatureImportance = imp,
                TotalGain = total,
                ImportanceAveragedAcrossFolds = folds,
                FoldIterations = foldIter,
                FoldEarlyStopped = foldStop,
                FoldFinalTrainLoss = foldTrain,
                FoldFinalValidationLoss = foldVal,
                EarlyStopped = anyStop,
                TrainLoss = curve,
                ValidationLoss = vcurve,
                LossCurveIsFoldMean = meanCurve,
                BestValidationIndex = folds ? -1 : sample.BestValidationIndex,
                RowsFit = rowsFit,
                RowsValidation = rowsVal,
                BinEdgesSubsampled = models.Any(m => m.BinEdgesSubsampled),
                BinEdgeRows = sample.BinEdgeRows,
                BinsPerFeature = sample.BinsPerFeature,
                Baseline = sample.Baseline,
                IterationsUsed = folds ? foldIter.Max() : sample.IterationsUsed,
                MaxIterations = sample.MaxIterations,
                LearningRate = sample.LearningRate,
                MaxDepth = sample.MaxDepth,
                MaxLeafNodes = sample.MaxLeafNodes,
                MinSamplesLeaf = sample.MinSamplesLeaf,
                L2Regularization = sample.L2Regularization,
                MaxBins = sample.MaxBins,
                EarlyStopping = sample.EarlyStopping,
                ValidationFraction = sample.ValidationFraction,
                IterationsNoChange = sample.IterationsNoChange,
                EarlyStoppingTolerance = sample.EarlyStoppingTolerance,
            };
        }

        private sealed class Node
        {
            public int Start, Stop, Depth, Feature, Bin;
            public double SumG, SumH, Value, Gain;
            public bool MissingLeft, Final;
            public Split Split;
            public Hist? Hist;
            public Node? Left, Right;
            public int Count => Stop - Start;
        }

        private struct Split
        {
            public double Gain;
            public int Feature, Bin, NLeft;
            public double SumGL, SumHL, SumGR, SumHR, ValueL, ValueR;
        }

        private sealed class Hist
        {
            public required double[][] SumG { get; init; }
            public double[][]? SumH { get; init; }
            public required int[][] Count { get; init; }

            public static Hist New(int features, int bins, bool withHess)
            {
                var sg = new double[features][];
                var ct = new int[features][];
                double[][]? sh = withHess ? new double[features][] : null;
                for (int f = 0; f < features; f++)
                {
                    sg[f] = new double[bins];
                    ct[f] = new int[bins];
                    if (sh != null) sh[f] = new double[bins];
                }
                return new Hist { SumG = sg, SumH = sh, Count = ct };
            }
        }

        /// <summary>높은 이득이 먼저 나온다. 동점에서는 heapq와 같이 어느 쪽도 작지 않다.</summary>
        private sealed class GainHeap
        {
            private readonly List<Node> _h = new();
            public int Count => _h.Count;
            public void Push(Node node)
            {
                _h.Add(node);
                SiftDown(_h.Count - 1);
            }
            public Node Pop()
            {
                var top = _h[0];
                var last = _h[^1];
                _h.RemoveAt(_h.Count - 1);
                if (_h.Count > 0)
                {
                    _h[0] = last;
                    SiftUp(0);
                }
                return top;
            }
            private static bool Less(Node a, Node b) => a.Gain > b.Gain;
            private void SiftDown(int pos)
            {
                var item = _h[pos];
                while (pos > 0)
                {
                    int parent = (pos - 1) >> 1;
                    if (!Less(item, _h[parent])) break;
                    _h[pos] = _h[parent];
                    pos = parent;
                }
                _h[pos] = item;
            }
            private void SiftUp(int pos)
            {
                int start = pos;
                var item = _h[pos];
                int child = 2 * pos + 1;
                while (child < _h.Count)
                {
                    int right = child + 1;
                    if (right < _h.Count && !Less(_h[child], _h[right])) child = right;
                    _h[pos] = _h[child];
                    pos = child;
                    child = 2 * pos + 1;
                }
                _h[pos] = item;
                SiftDownFrom(start, pos);
            }
            private void SiftDownFrom(int start, int pos)
            {
                var item = _h[pos];
                while (pos > start)
                {
                    int parent = (pos - 1) >> 1;
                    if (!Less(item, _h[parent])) break;
                    _h[pos] = _h[parent];
                    pos = parent;
                }
                _h[pos] = item;
            }
        }
    }

    internal static class DataSplitEarly
    {
        public static int[] ValOrTest(this DataSplit split) => split.Test;
    }
}
