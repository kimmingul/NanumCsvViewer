namespace NanumCsvViewer.Stats
{
    public enum EvalScheme { Holdout, KFold }

    /// <summary>분류기 평가 옵션. 스케일러는 항상 학습 행으로만 적합한다.</summary>
    public sealed record ClassifierOptions
    {
        public EvalScheme Scheme { get; init; } = EvalScheme.Holdout;
        public double TestFraction { get; init; } = 0.3;
        public int Folds { get; init; } = 5;
        public int Seed { get; init; } = 1;
        public ScalingMethod Scaling { get; init; } = ScalingMethod.ZScore;
        /// <summary>KNN 이웃 수. 나이브 베이즈는 쓰지 않는다.</summary>
        public int Neighbors { get; init; } = 5;
    }

    /// <summary>홀드아웃 또는 k-겹 평가. 홀드아웃이면 학습 스케일러와 예측을 돌려준다(누수 검증용).</summary>
    public sealed class ClassifierEvalResult
    {
        public required ClassificationMetrics Metrics { get; init; }
        /// <summary>평가 집합에서 최다 클래스 비율. 홀드아웃은 시험 행, k-겹은 전체 행.</summary>
        public required double MajorityBaseline { get; init; }
        public required EvalScheme Scheme { get; init; }
        public required int Seed { get; init; }
        public required double TestFraction { get; init; }
        public required int Folds { get; init; }
        public required ScalingMethod Scaling { get; init; }
        public required int TrainRows { get; init; }
        public required int TestRows { get; init; }
        public FeatureScaler? HoldoutScaler { get; init; }
        public int[]? HoldoutPredicted { get; init; }
        public int[]? HoldoutActual { get; init; }
    }

    /// <summary>특성 열 묶음. 범주는 원-핫 열 전부(수준 순서 = Columns 순서).</summary>
    public sealed record FeatureGroup(int SourceColumn, int[] Columns, bool Categorical);

    /// <summary>FeatureMatrix 원-핫에서 수치/범주 묶음을 복원한다. 같은 원본 컬럼이 2열 이상이면 범주.</summary>
    public static class FeatureGroups
    {
        public static IReadOnlyList<FeatureGroup> FromMatrix(IReadOnlyList<int> sourceColumns, IReadOnlyList<string> featureNames)
        {
            if (sourceColumns.Count != featureNames.Count)
                throw new ArgumentException("Source column count must match feature names.", nameof(featureNames));
            if (sourceColumns.Count == 0)
                throw new DesignMatrixException("Select at least one feature column.");
            int n = sourceColumns.Count;
            var used = new bool[n];
            var groups = new List<FeatureGroup>();
            for (int i = 0; i < n; i++)
            {
                if (used[i]) continue;
                int src = sourceColumns[i];
                var cols = new List<int>();
                for (int j = i; j < n; j++)
                {
                    if (sourceColumns[j] != src) continue;
                    cols.Add(j);
                    used[j] = true;
                }
                // 1수준 원-핫(열 하나, 이름에 '=')은 상수 수치로 둔다. 연속 컬럼 이름에 '='가 있어도 값을 버리지 않기 위함.
                groups.Add(new FeatureGroup(src, cols.ToArray(), cols.Count > 1));
            }
            return groups;
        }
    }

    /// <summary>
    /// K-최근접 이웃 분류. 예측은 ALGLIB kd-tree(eps=0, 유클리드)만 사용한다 — 행 쌍 거리 행렬을 만들지 않는다.
    /// </summary>
    public static class KnnClassifier
    {
        public static int[] Predict(
            double[,] trainX, int[] trainLabels, int classCount, double[,] testX, int k,
            CancellationToken cancellation = default)
        {
            int n = trainX.GetLength(0);
            int p = trainX.GetLength(1);
            int m = testX.GetLength(0);
            if (p < 1 || testX.GetLength(1) != p)
                throw new ArgumentException("Train and test feature counts must match.");
            if (trainLabels.Length != n)
                throw new ArgumentException("Training label length must match training rows.", nameof(trainLabels));
            if (n < 1) throw new DesignMatrixException("The training split is empty.");
            if (k < 1 || k > n)
                throw new DesignMatrixException($"k ({k}) must be between 1 and the training row count ({n}).");
            if (classCount < 2)
                throw new DesignMatrixException("The target has only one class in the complete rows.");

            var xy = new double[n, p + 1];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int y = trainLabels[i];
                if ((uint)y >= (uint)classCount)
                    throw new ArgumentException("A training label is outside 0..classCount-1.", nameof(trainLabels));
                for (int j = 0; j < p; j++) xy[i, j] = trainX[i, j];
                xy[i, p] = y;
            }

            alglib.knnmodel model;
            try
            {
                alglib.knnbuildercreate(out var builder);
                alglib.knnbuildersetdatasetcls(builder, xy, n, p, classCount);
                alglib.knnbuildersetnorm(builder, 2);
                alglib.knnbuilderbuildknnmodel(builder, k, 0.0, out model, out _);
            }
            catch (alglib.alglibexception ex)
            {
                throw new DesignMatrixException(string.IsNullOrWhiteSpace(ex.msg) ? "KNN model build failed." : ex.msg);
            }

            // 스레드별 ALGLIB 버퍼(knncreatebuffer + knntsprocess)로 병렬 질의. knnclassify와 같은 규칙:
            // 클래스 확률의 첫 최댓값(동률이면 작은 인덱스).
            var pred = new int[m];
            int parts = Math.Clamp(m / 2048, 1, Environment.ProcessorCount);
            Parallel.For(0, parts, new ParallelOptions { CancellationToken = cancellation }, part =>
            {
                int from = (int)((long)m * part / parts), to = (int)((long)m * (part + 1) / parts);
                alglib.knncreatebuffer(model, out var buffer);
                var row = new double[p];
                var probs = new double[classCount];
                for (int i = from; i < to; i++)
                {
                    if (((i - from) & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    for (int j = 0; j < p; j++) row[j] = testX[i, j];
                    alglib.knntsprocess(model, buffer, row, ref probs);
                    int cls = 0;
                    for (int c = 1; c < probs.Length; c++) if (probs[c] > probs[cls]) cls = c;
                    pred[i] = cls;
                }
            });
            return pred;
        }

        public static ClassifierEvalResult Evaluate(
            double[,] x, int[] labels, int classCount, ClassifierOptions? options = null,
            CancellationToken cancellation = default)
        {
            options ??= new ClassifierOptions();
            int n = x.GetLength(0);
            ClassifierCommon.Validate(x, labels, classCount, options, neighbors: true);
            return ClassifierCommon.Evaluate(x, labels, classCount, options, scaleColumns: null, cancellation, (trainX, trainY, testX, _) =>
                Predict(trainX, trainY, classCount, testX, options.Neighbors, cancellation));
        }
    }

    /// <summary>범주 특성 하나. Counts는 평활 전, LogProb는 Laplace 평활 후 로그확률.</summary>
    public sealed class CategoricalFeature
    {
        public int SourceColumn { get; }
        public int[] Columns { get; }
        public long[,] Counts { get; }
        public double[,] LogProb { get; }

        internal CategoricalFeature(int source, int[] columns, long[,] counts, double[,] logProb)
        {
            SourceColumn = source;
            Columns = columns;
            Counts = counts;
            LogProb = logProb;
        }
    }

    /// <summary>
    /// 가우시안·범주 나이브 베이즈. 수치 특성은 sklearn GaussianNB(var_smoothing = 1e-9 × 최대 특성 분산, ddof=0),
    /// 범주 특성은 sklearn CategoricalNB(α = 1, 수준 수 = 원-핫 열 수)와 같은 로그우도.
    /// </summary>
    public sealed class NaiveBayesModel
    {
        public const double DefaultVarSmoothing = 1e-9;
        public const double DefaultAlpha = 1.0;

        public int ClassCount { get; }
        public int[] ClassCounts { get; }
        public double[] Prior { get; }
        public double[] LogPrior { get; }
        public double Epsilon { get; }
        public double VarSmoothing { get; }
        public double Alpha { get; }
        /// <summary>수치 특성의 열 인덱스(적합에 넘긴 행렬 기준).</summary>
        public int[] NumericColumns { get; }
        /// <summary>클래스×수치특성 평균. 적합 공간(스케일 적용 후).</summary>
        public double[,] Mean { get; }
        /// <summary>클래스×수치특성 분산. 모분산(ddof=0)에 epsilon을 더한 값.</summary>
        public double[,] Variance { get; }
        public CategoricalFeature[] Categorical { get; }

        private NaiveBayesModel(
            int classCount, int[] classCounts, double[] prior, double[] logPrior,
            double epsilon, double varSmoothing, double alpha,
            int[] numericColumns, double[,] mean, double[,] variance, CategoricalFeature[] categorical)
        {
            ClassCount = classCount;
            ClassCounts = classCounts;
            Prior = prior;
            LogPrior = logPrior;
            Epsilon = epsilon;
            VarSmoothing = varSmoothing;
            Alpha = alpha;
            NumericColumns = numericColumns;
            Mean = mean;
            Variance = variance;
            Categorical = categorical;
        }

        public static NaiveBayesModel Fit(
            double[,] x, int[] labels, int classCount, IReadOnlyList<FeatureGroup> groups,
            double varSmoothing = DefaultVarSmoothing, double alpha = DefaultAlpha,
            CancellationToken cancellation = default)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (labels.Length != n) throw new ArgumentException("Label length must match rows.", nameof(labels));
            if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            if (n < 1) throw new DesignMatrixException("The training split is empty.");
            if (varSmoothing < 0) throw new ArgumentException("var_smoothing cannot be negative.", nameof(varSmoothing));
            if (!(alpha > 0)) throw new ArgumentException("Laplace alpha must be positive.", nameof(alpha));

            var numeric = new List<int>();
            var categoricalGroups = new List<FeatureGroup>();
            foreach (var g in groups)
            {
                if (g.Columns.Length == 0) throw new ArgumentException("A feature group has no columns.", nameof(groups));
                foreach (int col in g.Columns)
                    if ((uint)col >= (uint)p) throw new ArgumentException("Feature column is out of range.", nameof(groups));
                if (g.Categorical) categoricalGroups.Add(g);
                else foreach (int col in g.Columns) numeric.Add(col);
            }

            double epsilon = 0;
            if (numeric.Count > 0)
            {
                double maxVar = 0;
                for (int j = 0; j < numeric.Count; j++)
                {
                    if ((j & 15) == 0) cancellation.ThrowIfCancellationRequested();
                    double v = PopulationVariance(x, numeric[j], cancellation);
                    if (v > maxVar) maxVar = v;
                }
                epsilon = varSmoothing * maxVar;
            }

            var counts = new int[classCount];
            var sum = new double[classCount, numeric.Count];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int y = labels[i];
                if ((uint)y >= (uint)classCount)
                    throw new ArgumentException("A label is outside 0..classCount-1.", nameof(labels));
                counts[y]++;
                for (int j = 0; j < numeric.Count; j++) sum[y, j] += x[i, numeric[j]];
            }

            var mean = new double[classCount, Math.Max(numeric.Count, 1)];
            if (numeric.Count == 0) mean = new double[classCount, 0];
            for (int c = 0; c < classCount; c++)
            {
                if (counts[c] == 0) continue;
                for (int j = 0; j < numeric.Count; j++) mean[c, j] = sum[c, j] / counts[c];
            }

            var ss = new double[classCount, numeric.Count];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int y = labels[i];
                for (int j = 0; j < numeric.Count; j++)
                {
                    double d = x[i, numeric[j]] - mean[y, j];
                    ss[y, j] += d * d;
                }
            }
            var variance = new double[classCount, numeric.Count];
            for (int c = 0; c < classCount; c++)
                for (int j = 0; j < numeric.Count; j++)
                    variance[c, j] = (counts[c] == 0 ? 0 : ss[c, j] / counts[c]) + epsilon;

            var cat = new CategoricalFeature[categoricalGroups.Count];
            for (int g = 0; g < categoricalGroups.Count; g++)
            {
                cancellation.ThrowIfCancellationRequested();
                var group = categoricalGroups[g];
                int levels = group.Columns.Length;
                var raw = new long[classCount, levels];
                for (int i = 0; i < n; i++)
                {
                    int level = ArgMax(x, i, group.Columns);
                    raw[labels[i], level]++;
                }
                var logProb = new double[classCount, levels];
                for (int c = 0; c < classCount; c++)
                {
                    double denom = counts[c] + alpha * levels;
                    for (int l = 0; l < levels; l++)
                        logProb[c, l] = Math.Log(raw[c, l] + alpha) - Math.Log(denom);
                }
                cat[g] = new CategoricalFeature(group.SourceColumn, group.Columns, raw, logProb);
            }

            var prior = new double[classCount];
            var logPrior = new double[classCount];
            for (int c = 0; c < classCount; c++)
            {
                prior[c] = (double)counts[c] / n;
                logPrior[c] = counts[c] == 0 ? double.NegativeInfinity : Math.Log(counts[c]) - Math.Log(n);
            }
            return new NaiveBayesModel(classCount, counts, prior, logPrior, epsilon, varSmoothing, alpha,
                numeric.ToArray(), mean, variance, cat);
        }

        /// <summary>스케일이 있으면 수치 평균을 원래 단위로 되돌린다.</summary>
        public double MeanInOriginalUnits(int classIndex, int numericIndex, FeatureScaler? scaler)
        {
            double m = Mean[classIndex, numericIndex];
            if (scaler is null || scaler.Method == ScalingMethod.None) return m;
            int col = NumericColumns[numericIndex];
            return m * scaler.Scale[col] + scaler.Center[col];
        }

        public double NumericLogLikelihood(double[,] x, int row, int classIndex)
        {
            if (ClassCounts[classIndex] == 0) return double.NegativeInfinity;
            double s = 0;
            for (int j = 0; j < NumericColumns.Length; j++)
            {
                double v = Variance[classIndex, j];
                double d = x[row, NumericColumns[j]] - Mean[classIndex, j];
                if (!(v > 0))
                {
                    if (d != 0) return double.NegativeInfinity;
                    continue;
                }
                s += -0.5 * Math.Log(2.0 * Math.PI * v) - 0.5 * d * d / v;
            }
            return s;
        }

        public double CategoricalLogLikelihood(double[,] x, int row, int classIndex)
        {
            if (ClassCounts[classIndex] == 0) return double.NegativeInfinity;
            double s = 0;
            foreach (var f in Categorical)
                s += f.LogProb[classIndex, ArgMax(x, row, f.Columns)];
            return s;
        }

        /// <summary>로그 사전확률 + 수치 로그우도 + 범주 로그우도. 정규화하지 않는다.</summary>
        public double[] JointLogProbability(double[,] x, int row)
        {
            var joint = new double[ClassCount];
            for (int c = 0; c < ClassCount; c++)
            {
                if (ClassCounts[c] == 0) { joint[c] = double.NegativeInfinity; continue; }
                joint[c] = LogPrior[c] + NumericLogLikelihood(x, row, c) + CategoricalLogLikelihood(x, row, c);
            }
            return joint;
        }

        public double[] PredictProba(double[,] x, int row)
        {
            var joint = JointLogProbability(x, row);
            double max = double.NegativeInfinity;
            for (int c = 0; c < joint.Length; c++) if (joint[c] > max) max = joint[c];
            var proba = new double[joint.Length];
            if (double.IsNegativeInfinity(max))
            {
                for (int c = 0; c < proba.Length; c++) proba[c] = double.NaN;
                return proba;
            }
            double sum = 0;
            for (int c = 0; c < joint.Length; c++)
            {
                proba[c] = Math.Exp(joint[c] - max);
                sum += proba[c];
            }
            if (sum == 0)
            {
                for (int c = 0; c < proba.Length; c++) proba[c] = double.NaN;
                return proba;
            }
            for (int c = 0; c < proba.Length; c++) proba[c] /= sum;
            return proba;
        }

        public int Predict(double[,] x, int row)
        {
            var joint = JointLogProbability(x, row);
            int best = 0;
            for (int c = 1; c < joint.Length; c++)
                if (joint[c] > joint[best]) best = c;
            return best;
        }

        public int[] Predict(double[,] x, CancellationToken cancellation = default)
        {
            int n = x.GetLength(0);
            var pred = new int[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                pred[i] = Predict(x, i);
            }
            return pred;
        }

        private static double PopulationVariance(double[,] x, int col, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            if (n == 0) return 0;
            double mean = 0;
            for (int i = 0; i < n; i++) mean += x[i, col];
            mean /= n;
            double ss = 0;
            for (int i = 0; i < n; i++)
            {
                if ((i & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                double d = x[i, col] - mean;
                ss += d * d;
            }
            return ss / n;
        }

        internal static int ArgMax(double[,] x, int row, int[] columns)
        {
            int best = 0;
            double bestV = x[row, columns[0]];
            for (int i = 1; i < columns.Length; i++)
            {
                double v = x[row, columns[i]];
                if (v > bestV) { bestV = v; best = i; }
            }
            return best;
        }
    }

    public sealed class NaiveBayesEvalResult
    {
        public required ClassifierEvalResult Evaluation { get; init; }
        /// <summary>홀드아웃이면 학습 분할 적합, k-겹이면 전체 행 기술 적합(지표와 다른 적합임을 표시).</summary>
        public required NaiveBayesModel Parameters { get; init; }
        public required bool ParametersUseAllRows { get; init; }
        public FeatureScaler? ParameterScaler { get; init; }
    }

    public static class NaiveBayesClassifier
    {
        public static NaiveBayesEvalResult Evaluate(
            double[,] x, int[] labels, int classCount, IReadOnlyList<FeatureGroup> groups,
            ClassifierOptions? options = null, CancellationToken cancellation = default)
        {
            options ??= new ClassifierOptions();
            ClassifierCommon.Validate(x, labels, classCount, options, neighbors: false);
            int p = x.GetLength(1);
            var mask = NumericMask(groups, p);

            NaiveBayesModel? holdoutModel = null;
            FeatureScaler? holdoutScaler = null;
            var eval = ClassifierCommon.Evaluate(x, labels, classCount, options, mask, cancellation, (trainX, trainY, testX, scaler) =>
            {
                var model = NaiveBayesModel.Fit(trainX, trainY, classCount, groups, cancellation: cancellation);
                if (options.Scheme == EvalScheme.Holdout && holdoutModel is null)
                {
                    holdoutModel = model;
                    holdoutScaler = scaler;
                }
                return model.Predict(testX, cancellation);
            });

            NaiveBayesModel parameters;
            FeatureScaler? parameterScaler;
            bool allRows;
            if (options.Scheme == EvalScheme.Holdout)
            {
                parameters = holdoutModel ?? throw new DesignMatrixException("Naive Bayes produced no training fit.");
                parameterScaler = holdoutScaler?.WithIdentityOutside(mask);
                allRows = false;
            }
            else
            {
                parameterScaler = FeatureScaler.Fit(x, options.Scaling);
                var all = ClassifierCommon.Extract(x, AllRows(x.GetLength(0)), parameterScaler, mask, cancellation);
                parameters = NaiveBayesModel.Fit(all, labels, classCount, groups, cancellation: cancellation);
                allRows = true;
            }
            return new NaiveBayesEvalResult
            {
                Evaluation = eval,
                Parameters = parameters,
                ParametersUseAllRows = allRows,
                ParameterScaler = parameterScaler?.WithIdentityOutside(mask),
            };
        }

        /// <summary>수치 특성 열만 true. 범주(원-핫) 열은 스케일하지 않는다.</summary>
        public static bool[] NumericMask(IReadOnlyList<FeatureGroup> groups, int p)
        {
            var mask = new bool[p];
            foreach (var g in groups)
                if (!g.Categorical)
                    foreach (int col in g.Columns) mask[col] = true;
            return mask;
        }

        private static int[] AllRows(int n)
        {
            var rows = new int[n];
            for (int i = 0; i < n; i++) rows[i] = i;
            return rows;
        }
    }

    /// <summary>홀드아웃·k-겹 공통. predict는 이미 스케일된 학습/시험 행렬을 받는다.</summary>
    internal static class ClassifierCommon
    {
        internal static void Validate(double[,] x, int[] labels, int classCount, ClassifierOptions options, bool neighbors)
        {
            int n = x.GetLength(0);
            if (x.GetLength(1) < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            if (labels.Length != n) throw new ArgumentException("Label length must match rows.", nameof(labels));
            if (options.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the split is reproducible.");
            if (neighbors && options.Neighbors < 1) throw new DesignMatrixException("k must be at least 1.");
            if (options.Scheme == EvalScheme.Holdout)
            {
                if (!(options.TestFraction > 0 && options.TestFraction < 1))
                    throw new DesignMatrixException("Test fraction must be between 0 and 1 (exclusive).");
            }
            else
            {
                if (options.Folds < 2) throw new DesignMatrixException("k-fold needs at least 2 folds.");
                if (n < options.Folds)
                    throw new DesignMatrixException($"Need at least {options.Folds} complete rows for {options.Folds}-fold cross-validation.");
            }
            for (int i = 0; i < n; i++)
                if ((uint)labels[i] >= (uint)classCount)
                    throw new ArgumentException("A label is outside 0..classCount-1.", nameof(labels));
        }

        internal static ClassifierEvalResult Evaluate(
            double[,] x, int[] labels, int classCount, ClassifierOptions options, bool[]? scaleColumns,
            CancellationToken cancellation, Func<double[,], int[], double[,], FeatureScaler, int[]> predict)
        {
            int n = x.GetLength(0);
            if (options.Scheme == EvalScheme.Holdout)
            {
                var split = ClassifierEvaluation.Holdout(labels, options.TestFraction, options.Seed, stratified: true);
                var scaler = FeatureScaler.Fit(x, options.Scaling, split.Train);
                var trainX = Extract(x, split.Train, scaler, scaleColumns, cancellation);
                var testX = Extract(x, split.Test, scaler, scaleColumns, cancellation);
                var trainY = Take(labels, split.Train);
                var pred = predict(trainX, trainY, testX, scaler);
                var actual = Take(labels, split.Test);
                if (pred.Length != actual.Length) throw new InvalidOperationException("Prediction length mismatch.");
                return new ClassifierEvalResult
                {
                    Metrics = ClassifierEvaluation.Metrics(actual, pred, classCount),
                    MajorityBaseline = Majority(labels, split.Test, classCount),
                    Scheme = options.Scheme,
                    Seed = options.Seed,
                    TestFraction = options.TestFraction,
                    Folds = options.Folds,
                    Scaling = options.Scaling,
                    TrainRows = split.Train.Length,
                    TestRows = split.Test.Length,
                    HoldoutScaler = scaler,
                    HoldoutPredicted = pred,
                    HoldoutActual = actual,
                };
            }

            var folds = ClassifierEvaluation.KFold(labels, options.Folds, options.Seed, stratified: true);
            var cm = new long[classCount, classCount];
            foreach (var split in folds)
            {
                cancellation.ThrowIfCancellationRequested();
                if (split.Test.Length == 0 || split.Train.Length == 0)
                    throw new DesignMatrixException("A cross-validation fold is empty. Use fewer folds or more rows.");
                var scaler = FeatureScaler.Fit(x, options.Scaling, split.Train);
                var trainX = Extract(x, split.Train, scaler, scaleColumns, cancellation);
                var testX = Extract(x, split.Test, scaler, scaleColumns, cancellation);
                var pred = predict(trainX, Take(labels, split.Train), testX, scaler);
                if (pred.Length != split.Test.Length) throw new InvalidOperationException("Prediction length mismatch.");
                for (int i = 0; i < split.Test.Length; i++)
                    cm[labels[split.Test[i]], pred[i]]++;
            }
            return new ClassifierEvalResult
            {
                Metrics = ClassifierEvaluation.FromConfusion(cm),
                MajorityBaseline = Majority(labels, null, classCount),
                Scheme = options.Scheme,
                Seed = options.Seed,
                TestFraction = options.TestFraction,
                Folds = options.Folds,
                Scaling = options.Scaling,
                TrainRows = n,
                TestRows = n,
            };
        }

        internal static double[,] Extract(double[,] x, int[] rows, FeatureScaler scaler, bool[]? scaleColumns, CancellationToken cancellation)
        {
            int p = x.GetLength(1);
            var m = new double[rows.Length, p];
            bool scale = scaler.Method != ScalingMethod.None;
            for (int i = 0; i < rows.Length; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int r = rows[i];
                for (int j = 0; j < p; j++)
                {
                    double v = x[r, j];
                    if (scale && (scaleColumns == null || scaleColumns[j]))
                        v = (v - scaler.Center[j]) / scaler.Scale[j];
                    m[i, j] = v;
                }
            }
            return m;
        }

        private static int[] Take(int[] labels, int[] rows)
        {
            var y = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) y[i] = labels[rows[i]];
            return y;
        }

        private static double Majority(int[] labels, int[]? subset, int classCount)
        {
            var counts = new long[classCount];
            int n = subset?.Length ?? labels.Length;
            if (subset == null)
                for (int i = 0; i < labels.Length; i++) counts[labels[i]]++;
            else
                for (int i = 0; i < subset.Length; i++) counts[labels[subset[i]]]++;
            long max = 0;
            for (int c = 0; c < classCount; c++) if (counts[c] > max) max = counts[c];
            return n == 0 ? double.NaN : (double)max / n;
        }
    }
}
