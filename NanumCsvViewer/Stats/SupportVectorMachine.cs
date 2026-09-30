namespace NanumCsvViewer.Stats
{
    public enum SvmKernel { Linear, Rbf }

    public enum SvmGammaMode { Scale, Auto, Value }

    public sealed record SvmOptions
    {
        public SvmKernel Kernel { get; init; } = SvmKernel.Rbf;
        public double C { get; init; } = 1;
        public SvmGammaMode GammaMode { get; init; } = SvmGammaMode.Scale;
        public double Gamma { get; init; } = 0.1;
        public double Tolerance { get; init; } = 1e-3;
        /// <summary>SMO 반복 상한. 넘기면 수렴하지 않았다고 표시하고 그때의 α를 쓴다.</summary>
        public int MaxIterations { get; init; } = 10_000;
        public int Seed { get; init; } = 1;
        /// <summary>커널 SMO 학습 행 상한. 넘으면 시드 고정 층화 표본. 선형은 상한을 넘으면 전 행 쌍대좌표강하.</summary>
        public int MaxTrainingRows { get; init; } = SupportVectorMachine.DefaultMaxTrainingRows;
        /// <summary>평가로 점수를 내는 시험 행 상한. 넘으면 시드 고정 층화 표본만 예측하고 두 크기를 적는다. 0 이하면 기본 상한.</summary>
        public int MaxEvaluationRows { get; init; } = SupportVectorMachine.DefaultMaxEvaluationRows;
        public int LinearEpochs { get; init; } = 25;
    }

    /// <summary>
    /// 분류 SVM. 작은 데이터는 libsvm C-SVC SMO(WSS-2, shrinking 없음, Q는 float로 캐스팅)로
    /// sklearn SVC(shrinking=False)에 맞춘다. 이진 결정함수는 classes_[1] 쪽이 양수.
    /// 다중 클래스는 one-vs-one 투표. RBF는 O(n²)라 학습 행을 상한으로 자르고 그 사실을 적는다.
    /// 선형이 상한을 넘으면 커널 행렬 없이 L2-정규 힌지 SVM(쌍대 좌표 강하, 절편은 정규화된 상수 특성)을
    /// one-vs-rest로 푼다. 그 결정값은 sklearn SVC와 같지 않다.
    /// </summary>
    public static class SupportVectorMachine
    {
        public const int DefaultMaxTrainingRows = 20_000;
        const double Tau = 1e-12;

        public static SvmModel Fit(double[,] x, int[] y, int classCount, SvmOptions? options = null, CancellationToken cancellation = default)
        {
            var opt = options ?? new SvmOptions();
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (y.Length != n) throw new ArgumentException("Label length must match rows.", nameof(y));
            if (classCount < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            if (!(opt.C > 0) || double.IsNaN(opt.C)) throw new DesignMatrixException("C must be positive.");
            if (!(opt.Tolerance > 0)) throw new DesignMatrixException("Tolerance must be positive.");
            if (opt.MaxIterations < 1) throw new DesignMatrixException("Max iterations must be at least 1.");
            if (opt.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so sampling is reproducible.");
            if (opt.MaxTrainingRows < 2) throw new DesignMatrixException("The training row cap must be at least 2.");
            if (opt.LinearEpochs < 1) throw new DesignMatrixException("Linear epochs must be at least 1.");
            var counts = new int[classCount];
            for (int i = 0; i < n; i++)
            {
                if ((uint)y[i] >= (uint)classCount) throw new DesignMatrixException("A class index is outside 0..K-1.");
                counts[y[i]]++;
            }
            int present = 0;
            for (int c = 0; c < classCount; c++) if (counts[c] > 0) present++;
            if (present < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
            cancellation.ThrowIfCancellationRequested();

            bool linearLarge = opt.Kernel == SvmKernel.Linear && n > opt.MaxTrainingRows;
            if (linearLarge)
                return FitLinearPrimal(x, y, classCount, opt, n, cancellation);

            int[] index = RowSample.Stratified(y, opt.MaxTrainingRows, opt.Seed, out bool sampled);
            var trainX = index.Length == n ? x : Slice(x, index, cancellation);
            var trainY = index.Length == n ? y : Take(y, index);
            double gamma = opt.Kernel == SvmKernel.Rbf ? ResolveGamma(opt.GammaMode, opt.Gamma, trainX) : 0;
            var model = FitSmo(trainX, trainY, classCount, opt, gamma, cancellation);
            model.Sampled = sampled;
            model.RowsPresented = n;
            model.RowsUsed = trainX.GetLength(0);
            return model;
        }

        public const int DefaultMaxEvaluationRows = 100_000;

        public static SvmClassificationResult Evaluate(
            double[,] x, int[] y, int classCount, ClassifierOptions eval, SvmOptions? svm = null,
            CancellationToken cancellation = default)
        {
            var opt = svm ?? new SvmOptions();
            int cap = opt.MaxEvaluationRows <= 0 ? DefaultMaxEvaluationRows : opt.MaxEvaluationRows;
            if (cap < 1) throw new DesignMatrixException("The evaluation row cap must be at least 1.");
            ClassifierCommon.Validate(x, y, classCount, eval, neighbors: false);
            return eval.Scheme == EvalScheme.Holdout
                ? EvaluateHoldout(x, y, classCount, eval, opt, cap, cancellation)
                : EvaluateKFold(x, y, classCount, eval, opt, cap, cancellation);
        }

        static SvmClassificationResult EvaluateHoldout(
            double[,] x, int[] y, int classCount, ClassifierOptions eval, SvmOptions opt, int cap, CancellationToken cancellation)
        {
            var split = ClassifierEvaluation.Holdout(y, eval.TestFraction, eval.Seed, stratified: true);
            var scored = CapRows(y, split.Test, cap, eval.Seed, out bool sampled);
            var scaler = FeatureScaler.Fit(x, eval.Scaling, split.Train);
            var trainX = ClassifierCommon.Extract(x, split.Train, scaler, null, cancellation);
            var model = Fit(trainX, Take(y, split.Train), classCount, opt, cancellation);
            var testX = ClassifierCommon.Extract(x, scored, scaler, null, cancellation);
            var pred = model.Predict(testX, cancellation);
            var actual = Take(y, scored);
            return Finish(eval, model, ClassifierEvaluation.Metrics(actual, pred, classCount),
                Majority(y, scored, classCount), split.Train.Length, split.Test.Length, scored.Length, sampled, false, scaler, pred, actual);
        }

        static SvmClassificationResult EvaluateKFold(
            double[,] x, int[] y, int classCount, ClassifierOptions eval, SvmOptions opt, int cap, CancellationToken cancellation)
        {
            int n = y.Length;
            var folds = ClassifierEvaluation.KFold(y, eval.Folds, eval.Seed, stratified: true);
            var scoredAll = CapRows(y, DecisionTree.Identity(n), cap, eval.Seed, out bool sampled);
            var inScored = new bool[n];
            for (int i = 0; i < scoredAll.Length; i++) inScored[scoredAll[i]] = true;
            var cm = new long[classCount, classCount];
            int scored = 0;
            foreach (var split in folds)
            {
                cancellation.ThrowIfCancellationRequested();
                var test = new List<int>();
                for (int i = 0; i < split.Test.Length; i++)
                    if (inScored[split.Test[i]]) test.Add(split.Test[i]);
                if (test.Count == 0 || split.Train.Length == 0) continue;
                var testRows = test.ToArray();
                var scaler = FeatureScaler.Fit(x, eval.Scaling, split.Train);
                var trainX = ClassifierCommon.Extract(x, split.Train, scaler, null, cancellation);
                var model = Fit(trainX, Take(y, split.Train), classCount, opt, cancellation);
                var pred = model.Predict(ClassifierCommon.Extract(x, testRows, scaler, null, cancellation), cancellation);
                for (int i = 0; i < testRows.Length; i++) cm[y[testRows[i]], pred[i]]++;
                scored += testRows.Length;
            }
            if (scored == 0) throw new DesignMatrixException("The evaluation sample is empty. Raise the evaluation row cap or use fewer folds.");
            var displayScaler = FeatureScaler.Fit(x, eval.Scaling);
            var display = Fit(ClassifierCommon.Extract(x, DecisionTree.Identity(n), displayScaler, null, cancellation), y, classCount, opt, cancellation);
            return Finish(eval, display, ClassifierEvaluation.FromConfusion(cm), Majority(y, scoredAll, classCount),
                n, n, scored, sampled, true, null, null, null);
        }

        static SvmClassificationResult Finish(
            ClassifierOptions eval, SvmModel display, ClassificationMetrics metrics, double baseline,
            int trainRows, int presented, int scored, bool sampled, bool allRows,
            FeatureScaler? scaler, int[]? pred, int[]? actual)
            => new()
            {
                Evaluation = new ClassifierEvalResult
                {
                    Metrics = metrics,
                    MajorityBaseline = baseline,
                    Scheme = eval.Scheme,
                    Seed = eval.Seed,
                    TestFraction = eval.TestFraction,
                    Folds = eval.Folds,
                    Scaling = eval.Scaling,
                    TrainRows = trainRows,
                    TestRows = scored,
                    HoldoutScaler = scaler,
                    HoldoutPredicted = pred,
                    HoldoutActual = actual,
                },
                Display = display,
                DisplayUsesAllRows = allRows,
                EvaluationRowsPresented = presented,
                EvaluationRowsScored = scored,
                EvaluationSampled = sampled,
            };

        /// <summary>시험 행이 상한을 넘으면 시드 고정 층화 표본만 남긴다. 반환 인덱스는 원 행 번호.</summary>
        internal static int[] CapRows(int[] labels, int[] rows, int cap, int seed, out bool sampled)
        {
            if (rows.Length <= cap)
            {
                sampled = false;
                return rows;
            }
            var sub = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) sub[i] = labels[rows[i]];
            var local = RowSample.Stratified(sub, cap, seed, out sampled);
            var chosen = new int[local.Length];
            for (int i = 0; i < local.Length; i++) chosen[i] = rows[local[i]];
            return chosen;
        }

        static double Majority(int[] labels, int[] rows, int classCount)
        {
            var counts = new long[classCount];
            for (int i = 0; i < rows.Length; i++) counts[labels[rows[i]]]++;
            long max = 0;
            for (int c = 0; c < classCount; c++) if (counts[c] > max) max = counts[c];
            return rows.Length == 0 ? double.NaN : (double)max / rows.Length;
        }

        /// <summary>sklearn gamma='scale' / 'auto'. scale은 전체 원소 모분산(ddof=0).</summary>
        public static double ResolveGamma(SvmGammaMode mode, double value, double[,] x)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (mode == SvmGammaMode.Auto) return p == 0 ? 1 : 1.0 / p;
            if (mode == SvmGammaMode.Value)
            {
                if (!(value > 0) || double.IsNaN(value)) throw new DesignMatrixException("Gamma must be positive.");
                return value;
            }
            long count = (long)n * p;
            if (count == 0 || p == 0) return 1;
            // 중심화 2-pass. 원시 2차 모멘트는 [1e9, 1e9+1]에서 분산이 0으로 붕괴한다.
            double mean = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++)
                    mean += x[i, j];
            mean /= count;
            double m2 = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++)
            {
                double d = x[i, j] - mean;
                m2 += d * d;
            }
            double var = m2 / count;
            if (var < 0) var = 0;
            return var == 0 ? 1.0 : 1.0 / (p * var);
        }

        static SvmModel FitSmo(double[,] x, int[] y, int classCount, SvmOptions opt, double gamma, CancellationToken cancellation)
        {
            var pairs = new List<BinarySvm>();
            bool converged = true;
            int iterations = 0;
            for (int i = 0; i < classCount; i++)
            {
                for (int j = i + 1; j < classCount; j++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var rows = new List<int>();
                    var pm = new List<sbyte>();
                    for (int r = 0; r < y.Length; r++)
                    {
                        if (y[r] == i) { rows.Add(r); pm.Add(1); }
                        else if (y[r] == j) { rows.Add(r); pm.Add(-1); }
                    }
                    if (rows.Count < 2 || !pm.Contains(1) || !pm.Contains(-1)) continue;
                    var solved = Solve(x, rows.ToArray(), pm.ToArray(), opt.C, opt.Kernel, gamma, opt.Tolerance, opt.MaxIterations, cancellation);
                    pairs.Add(solved);
                    iterations += solved.Iterations;
                    if (!solved.Converged) converged = false;
                }
            }
            if (pairs.Count == 0) throw new DesignMatrixException("SVM could not form a binary subproblem.");
            var support = new int[classCount];
            var marked = new bool[y.Length];
            foreach (var pair in pairs)
            {
                for (int s = 0; s < pair.Rows.Length; s++)
                {
                    if (Math.Abs(pair.Alpha[s]) <= 0) continue;
                    int row = pair.Rows[s];
                    if (!marked[row])
                    {
                        marked[row] = true;
                        support[y[row]]++;
                    }
                }
            }
            return new SvmModel
            {
                X = x,
                Labels = y,
                ClassCount = classCount,
                Kernel = opt.Kernel,
                Gamma = gamma,
                C = opt.C,
                Pairs = pairs.ToArray(),
                SupportPerClass = support,
                Converged = converged,
                Iterations = iterations,
                Solver = "SMO",
                Multiclass = classCount == 2 ? "binary" : "one-vs-one",
                Sampled = false,
                RowsPresented = y.Length,
                RowsUsed = y.Length,
            };
        }

        /// <summary>libsvm Solver, shrinking 없음. α는 라그랑주 승수. 결정값 = Σ α_i y_i K - ρ (양수 → 쌍의 +1 클래스).</summary>
        static BinarySvm Solve(double[,] x, int[] rows, sbyte[] y, double c, SvmKernel kernel, double gamma,
            double eps, int maxIter, CancellationToken cancellation)
        {
            int n = rows.Length, p = x.GetLength(1);
            var alpha = new double[n];
            var g = new double[n];
            var qd = new double[n];
            var norm = new double[n];
            for (int i = 0; i < n; i++)
            {
                g[i] = -1;
                norm[i] = Dot(x, rows[i], rows[i], p);
                qd[i] = kernel == SvmKernel.Linear ? norm[i] : 1.0;
            }
            var status = new byte[n]; // 0 free, 1 lower, 2 upper
            for (int i = 0; i < n; i++) status[i] = 1;
            var qi = new float[n];
            var qj = new float[n];
            int iter = 0;
            bool converged = false;
            while (iter < maxIter)
            {
                if ((iter & 31) == 0) cancellation.ThrowIfCancellationRequested();
                if (!Select(y, g, status, qd, qi, rows, x, kernel, gamma, norm, eps, n, p, out int i, out int j))
                {
                    converged = true;
                    break;
                }
                iter++;
                FillQ(qi, i, y, rows, x, kernel, gamma, norm, n, p);
                FillQ(qj, j, y, rows, x, kernel, gamma, norm, n, p);
                double oldI = alpha[i], oldJ = alpha[j];
                if (y[i] != y[j])
                {
                    double quad = qd[i] + qd[j] + 2 * qi[j];
                    if (quad <= 0) quad = Tau;
                    double delta = (-g[i] - g[j]) / quad;
                    double diff = alpha[i] - alpha[j];
                    alpha[i] += delta;
                    alpha[j] += delta;
                    if (diff > 0)
                    {
                        if (alpha[j] < 0) { alpha[j] = 0; alpha[i] = diff; }
                    }
                    else if (alpha[i] < 0) { alpha[i] = 0; alpha[j] = -diff; }
                    if (diff > c - c)
                    {
                        if (alpha[i] > c) { alpha[i] = c; alpha[j] = c - diff; }
                    }
                    else if (alpha[j] > c) { alpha[j] = c; alpha[i] = c + diff; }
                }
                else
                {
                    double quad = qd[i] + qd[j] - 2 * qi[j];
                    if (quad <= 0) quad = Tau;
                    double delta = (g[i] - g[j]) / quad;
                    double sum = alpha[i] + alpha[j];
                    alpha[i] -= delta;
                    alpha[j] += delta;
                    if (sum > c)
                    {
                        if (alpha[i] > c) { alpha[i] = c; alpha[j] = sum - c; }
                    }
                    else if (alpha[j] < 0) { alpha[j] = 0; alpha[i] = sum; }
                    if (sum > c)
                    {
                        if (alpha[j] > c) { alpha[j] = c; alpha[i] = sum - c; }
                    }
                    else if (alpha[i] < 0) { alpha[i] = 0; alpha[j] = sum; }
                }
                double dI = alpha[i] - oldI, dJ = alpha[j] - oldJ;
                for (int k = 0; k < n; k++) g[k] += qi[k] * dI + qj[k] * dJ;
                status[i] = Status(alpha[i], c);
                status[j] = Status(alpha[j], c);
            }
            double rho = Rho(y, g, status, n);
            return new BinarySvm(rows, y, alpha, rho, iter, converged);
        }

        static bool Select(sbyte[] y, double[] g, byte[] status, double[] qd, float[] qi,
            int[] rows, double[,] x, SvmKernel kernel, double gamma, double[] norm, double eps,
            int n, int p, out int outI, out int outJ)
        {
            outI = -1;
            outJ = -1;
            double gmax = double.NegativeInfinity, gmax2 = double.NegativeInfinity;
            int gmaxIdx = -1;
            for (int t = 0; t < n; t++)
            {
                if (y[t] == 1)
                {
                    if (status[t] != 2 && -g[t] >= gmax) { gmax = -g[t]; gmaxIdx = t; }
                }
                else if (status[t] != 1 && g[t] >= gmax) { gmax = g[t]; gmaxIdx = t; }
            }
            int i = gmaxIdx;
            if (i >= 0) FillQ(qi, i, y, rows, x, kernel, gamma, norm, n, p);
            int gminIdx = -1;
            double objMin = double.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (y[j] == 1)
                {
                    if (status[j] == 1) continue;
                    double grad = gmax + g[j];
                    if (g[j] >= gmax2) gmax2 = g[j];
                    if (grad > 0 && i >= 0)
                    {
                        double quad = qd[i] + qd[j] - 2.0 * y[i] * qi[j];
                        double obj = -(grad * grad) / (quad > 0 ? quad : Tau);
                        if (obj <= objMin) { objMin = obj; gminIdx = j; }
                    }
                }
                else
                {
                    if (status[j] == 2) continue;
                    double grad = gmax - g[j];
                    if (-g[j] >= gmax2) gmax2 = -g[j];
                    if (grad > 0 && i >= 0)
                    {
                        double quad = qd[i] + qd[j] + 2.0 * y[i] * qi[j];
                        double obj = -(grad * grad) / (quad > 0 ? quad : Tau);
                        if (obj <= objMin) { objMin = obj; gminIdx = j; }
                    }
                }
            }
            if (gmax + gmax2 < eps || gminIdx < 0) return false;
            outI = gmaxIdx;
            outJ = gminIdx;
            return true;
        }

        static void FillQ(float[] q, int i, sbyte[] y, int[] rows, double[,] x, SvmKernel kernel, double gamma, double[] norm, int n, int p)
        {
            for (int j = 0; j < n; j++)
            {
                double k = kernel == SvmKernel.Linear
                    ? Dot(x, rows[i], rows[j], p)
                    : Rbf(norm[i], norm[j], Dot(x, rows[i], rows[j], p), gamma);
                q[j] = (float)(y[i] * y[j] * k);
            }
        }

        static double Rho(sbyte[] y, double[] g, byte[] status, int n)
        {
            double ub = double.PositiveInfinity, lb = double.NegativeInfinity, sum = 0;
            int free = 0;
            for (int i = 0; i < n; i++)
            {
                double yg = y[i] * g[i];
                if (status[i] == 2)
                {
                    if (y[i] == -1) ub = Math.Min(ub, yg);
                    else lb = Math.Max(lb, yg);
                }
                else if (status[i] == 1)
                {
                    if (y[i] == 1) ub = Math.Min(ub, yg);
                    else lb = Math.Max(lb, yg);
                }
                else { free++; sum += yg; }
            }
            if (free > 0) return sum / free;
            if (double.IsInfinity(ub) || double.IsInfinity(lb)) return 0;
            return (ub + lb) / 2;
        }

        static byte Status(double alpha, double c) => alpha >= c ? (byte)2 : alpha <= 0 ? (byte)1 : (byte)0;

        static SvmModel FitLinearPrimal(double[,] x, int[] y, int classCount, SvmOptions opt, int n, CancellationToken cancellation)
        {
            int p = x.GetLength(1);
            var weights = new double[classCount][];
            var bias = new double[classCount];
            var rng = new Random(opt.Seed);
            for (int c = 0; c < classCount; c++)
            {
                cancellation.ThrowIfCancellationRequested();
                var pm = new sbyte[n];
                int pos = 0;
                for (int i = 0; i < n; i++)
                {
                    pm[i] = (sbyte)(y[i] == c ? 1 : -1);
                    if (pm[i] == 1) pos++;
                }
                if (pos == 0 || pos == n) continue;
                var (w, b) = CoordinateDescent(x, pm, opt.C, opt.LinearEpochs, opt.Tolerance, rng, cancellation);
                weights[c] = w;
                bias[c] = b;
            }
            return new SvmModel
            {
                X = x,
                Labels = y,
                ClassCount = classCount,
                Kernel = SvmKernel.Linear,
                Gamma = 0,
                C = opt.C,
                Pairs = Array.Empty<BinarySvm>(),
                LinearWeights = weights,
                LinearBias = bias,
                SupportPerClass = new int[classCount],
                Converged = true,
                Iterations = opt.LinearEpochs,
                Solver = "DCD",
                Multiclass = "one-vs-rest",
                Sampled = false,
                RowsPresented = n,
                RowsUsed = n,
            };
        }

        /// <summary>편향은 상수 특성 1로 넣어 같이 L2 정규화한다. sklearn SVC의 비정규 절편과 다르다.</summary>
        static (double[] W, double B) CoordinateDescent(double[,] x, sbyte[] y, double c, int epochs, double tol, Random rng, CancellationToken cancellation)
        {
            int n = y.Length, p = x.GetLength(1);
            var w = new double[p];
            double b = 0;
            var alpha = new double[n];
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            var qii = new double[n];
            for (int i = 0; i < n; i++) qii[i] = Dot(x, i, i, p) + 1;
            for (int epoch = 0; epoch < epochs; epoch++)
            {
                cancellation.ThrowIfCancellationRequested();
                Shuffle(order, rng);
                double maxPg = 0;
                for (int t = 0; t < n; t++)
                {
                    int i = order[t];
                    double dot = b;
                    int row = i;
                    for (int f = 0; f < p; f++) dot += w[f] * x[row, f];
                    double g = y[i] * dot - 1;
                    double pg = alpha[i] <= 0 ? Math.Min(g, 0) : alpha[i] >= c ? Math.Max(g, 0) : g;
                    if (Math.Abs(pg) > maxPg) maxPg = Math.Abs(pg);
                    if (qii[i] <= 0) continue;
                    double next = Math.Clamp(alpha[i] - g / qii[i], 0, c);
                    double d = next - alpha[i];
                    if (d == 0) continue;
                    double scale = d * y[i];
                    for (int f = 0; f < p; f++) w[f] += scale * x[row, f];
                    b += scale;
                    alpha[i] = next;
                }
                if (maxPg < tol) break;
            }
            return (w, b);
        }

        static void Shuffle(int[] a, Random rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        static double Dot(double[,] x, int i, int j, int p)
        {
            double s = 0;
            for (int f = 0; f < p; f++) s += x[i, f] * x[j, f];
            return s;
        }

        static double Rbf(double ni, double nj, double dot, double gamma)
        {
            double dist = ni + nj - 2 * dot;
            if (dist < 0) dist = 0;
            return Math.Exp(-gamma * dist);
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

        static int[] Take(int[] y, int[] rows)
        {
            var a = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) a[i] = y[rows[i]];
            return a;
        }
    }

    sealed class BinarySvm
    {
        public BinarySvm(int[] rows, sbyte[] y, double[] alpha, double rho, int iterations, bool converged)
        {
            Rows = rows;
            Y = y;
            Alpha = alpha;
            Rho = rho;
            Iterations = iterations;
            Converged = converged;
        }
        public int[] Rows { get; }
        public sbyte[] Y { get; }
        public double[] Alpha { get; }
        public double Rho { get; }
        public int Iterations { get; }
        public bool Converged { get; }
    }

    public sealed class SvmModel
    {
        internal double[,] X { get; init; } = null!;
        internal int[] Labels { get; init; } = null!;
        public required int ClassCount { get; init; }
        public required SvmKernel Kernel { get; init; }
        public required double Gamma { get; init; }
        public required double C { get; init; }
        internal BinarySvm[] Pairs { get; init; } = Array.Empty<BinarySvm>();
        internal double[][]? LinearWeights { get; init; }
        internal double[]? LinearBias { get; init; }
        public required int[] SupportPerClass { get; init; }
        public int SupportVectorCount { get { int s = 0; for (int i = 0; i < SupportPerClass.Length; i++) s += SupportPerClass[i]; return s; } }
        public required bool Converged { get; init; }
        public required int Iterations { get; init; }
        public required string Solver { get; init; }
        public required string Multiclass { get; init; }
        public required bool Sampled { get; set; }
        public required int RowsPresented { get; set; }
        public required int RowsUsed { get; set; }

        public int[] Predict(double[,] x, CancellationToken cancellation = default)
        {
            int n = x.GetLength(0);
            var pred = new int[n];
            if (n == 0) return pred;
            int p = x.GetLength(1);
            var pack = Pack(p);
            RunRows(n, cancellation, () => new ScoreBuf(p, ClassCount), (i, buf) => pred[i] = PredictPacked(pack, x, i, buf));
            return pred;
        }

        /// <summary>이진 SMO만. 양수는 더 큰 클래스 인덱스(sklearn decision_function).</summary>
        public double[] DecisionFunction(double[,] x, CancellationToken cancellation = default)
        {
            if (ClassCount != 2 || Solver != "SMO" || Pairs.Length != 1)
                throw new InvalidOperationException("DecisionFunction matches sklearn only for a binary SMO model.");
            int n = x.GetLength(0);
            var d = new double[n];
            int p = x.GetLength(1);
            var pack = Pack(p);
            // 쌍은 클래스 0 = +1. sklearn은 classes_[1]이 양수이므로 부호를 뒤집는다.
            RunRows(n, cancellation, () => new ScoreBuf(p, 1), (i, buf) => d[i] = -ScorePacked(pack[0], x, i, buf.Query));
            return d;
        }

        /// <summary>행마다 독립. 스레드 로컬 버퍼만 쓰고 결과는 인덱스에 직접 기록한다.</summary>
        static void RunRows(int n, CancellationToken cancellation, Func<ScoreBuf> local, Action<int, ScoreBuf> body)
        {
            if (n < 512)
            {
                var buf = local();
                for (int i = 0; i < n; i++)
                {
                    if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                    body(i, buf);
                }
                return;
            }
            var po = new ParallelOptions { CancellationToken = cancellation };
            try
            {
                Parallel.For(0, n, po, local, (i, _, buf) =>
                {
                    body(i, buf);
                    return buf;
                }, _ => { });
            }
            catch (AggregateException ex)
            {
                foreach (var inner in ex.Flatten().InnerExceptions)
                    if (inner is OperationCanceledException) throw inner;
                throw;
            }
        }

        PairPack[] Pack(int p)
        {
            if (Solver == "DCD")
                return Array.Empty<PairPack>();
            var packs = new PairPack[Pairs.Length];
            for (int t = 0; t < Pairs.Length; t++)
            {
                var pair = Pairs[t];
                int count = 0;
                for (int s = 0; s < pair.Alpha.Length; s++)
                    if (pair.Alpha[s] != 0) count++;
                var sv = new double[count * p];
                var coeff = new double[count];
                var norm = new double[count];
                int w = 0;
                int pos = Labels[pair.Rows[0]], neg = pos;
                for (int s = 0; s < pair.Rows.Length; s++)
                {
                    if (pair.Y[s] > 0) pos = Labels[pair.Rows[s]];
                    else neg = Labels[pair.Rows[s]];
                    if (pair.Alpha[s] == 0) continue;
                    double nrm = 0;
                    int row = pair.Rows[s];
                    for (int f = 0; f < p; f++)
                    {
                        double v = X[row, f];
                        sv[w * p + f] = v;
                        nrm += v * v;
                    }
                    coeff[w] = pair.Alpha[s] * pair.Y[s];
                    norm[w] = nrm;
                    w++;
                }
                packs[t] = new PairPack(sv, coeff, norm, pair.Rho, pos, neg, w, p, Kernel == SvmKernel.Rbf, Gamma);
            }
            return packs;
        }

        int PredictPacked(PairPack[] packs, double[,] x, int row, ScoreBuf buf)
        {
            if (Solver == "DCD") return PredictLinear(x, row);
            var votes = buf.Votes;
            Array.Clear(votes, 0, votes.Length);
            for (int t = 0; t < packs.Length; t++)
            {
                double s = ScorePacked(packs[t], x, row, buf.Query);
                if (s > 0) votes[packs[t].Pos]++;
                else votes[packs[t].Neg]++;
            }
            int best = 0;
            for (int c = 1; c < votes.Length; c++)
                if (votes[c] > votes[best]) best = c;
            return best;
        }

        static double ScorePacked(PairPack pair, double[,] x, int row, double[] query)
        {
            int p = pair.P;
            double qn = 0;
            for (int f = 0; f < p; f++)
            {
                double v = x[row, f];
                query[f] = v;
                qn += v * v;
            }
            double sum = 0;
            var sv = pair.Sv;
            var coeff = pair.Coeff;
            if (pair.Rbf)
            {
                var norm = pair.Norm;
                double gamma = pair.Gamma;
                for (int s = 0; s < pair.Count; s++)
                {
                    int b = s * p;
                    double dot = 0;
                    for (int f = 0; f < p; f++) dot += query[f] * sv[b + f];
                    double dist = qn + norm[s] - 2 * dot;
                    if (dist < 0) dist = 0;
                    sum += coeff[s] * Math.Exp(-gamma * dist);
                }
            }
            else
            {
                for (int s = 0; s < pair.Count; s++)
                {
                    int b = s * p;
                    double dot = 0;
                    for (int f = 0; f < p; f++) dot += query[f] * sv[b + f];
                    sum += coeff[s] * dot;
                }
            }
            return sum - pair.Rho;
        }

        int PredictLinear(double[,] x, int row)
        {
            var w = LinearWeights!;
            var b = LinearBias!;
            int best = -1;
            double bestScore = double.NegativeInfinity;
            for (int c = 0; c < ClassCount; c++)
            {
                if (w[c] == null) continue;
                double s = b[c];
                for (int f = 0; f < w[c].Length; f++) s += w[c][f] * x[row, f];
                if (s > bestScore) { bestScore = s; best = c; }
            }
            return best < 0 ? 0 : best;
        }

        sealed class ScoreBuf
        {
            public ScoreBuf(int p, int classes)
            {
                Query = new double[p];
                Votes = new int[classes];
            }
            public double[] Query { get; }
            public int[] Votes { get; }
        }

        sealed class PairPack
        {
            public PairPack(double[] sv, double[] coeff, double[] norm, double rho, int pos, int neg, int count, int p, bool rbf, double gamma)
            {
                Sv = sv; Coeff = coeff; Norm = norm; Rho = rho; Pos = pos; Neg = neg; Count = count; P = p; Rbf = rbf; Gamma = gamma;
            }
            public double[] Sv { get; }
            public double[] Coeff { get; }
            public double[] Norm { get; }
            public double Rho { get; }
            public int Pos { get; }
            public int Neg { get; }
            public int Count { get; }
            public int P { get; }
            public bool Rbf { get; }
            public double Gamma { get; }
        }
    }

    public sealed class SvmClassificationResult
    {
        public required ClassifierEvalResult Evaluation { get; init; }
        public required SvmModel Display { get; init; }
        public required bool DisplayUsesAllRows { get; init; }
        /// <summary>평가 분할의 시험 행(홀드아웃) 또는 전체 행(k-겹). 표본이 아니면 점수 행과 같다.</summary>
        public int EvaluationRowsPresented { get; init; }
        /// <summary>실제로 예측해 지표에 넣은 행 수.</summary>
        public int EvaluationRowsScored { get; init; }
        public bool EvaluationSampled { get; init; }
    }
}
