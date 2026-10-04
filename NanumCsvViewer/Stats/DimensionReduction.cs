using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    // 차원축소(이슈 #27). PCA는 ALGLIB pcabuildbasis(전체 SVD)로 표본 고유값(s²/(n−1), sklearn
    // explained_variance_)을 구하고, LDA 분류는 sklearn LinearDiscriminantAnalysis(solver='svd')와
    // 같은 합동 급내 공분산·학습 빈도 사전확률 공식이다. 판별 방향은 ALGLIB fisherldan.

    /// <summary>PCA 분산 행렬. 상관은 표본 표준편차(ddof=1)로 표준화한 뒤의 공분산(=상관행렬).</summary>
    public enum PcaScale { Correlation, Covariance }

    /// <summary>주성분분석 결과. 고유값은 내림차순, 적재량 열은 성분(최대 |적재량|이 양이 되도록 부호 고정).</summary>
    public sealed class PcaResult
    {
        public required PcaScale Scale { get; init; }
        /// <summary>길이 p. 표본 분산(ddof=1). 상수 특성은 0.</summary>
        public required double[] Eigenvalues { get; init; }
        public required double[] ExplainedRatio { get; init; }
        public required double[] CumulativeRatio { get; init; }
        /// <summary>p×m 적재량. 열 = 성분.</summary>
        public required double[,] Loadings { get; init; }
        public required int RequestedComponents { get; init; }
        public required IReadOnlyList<string> FeatureNames { get; init; }
        /// <summary>고유값 &gt; 1 인 성분 수(Kaiser. 상관행렬에서 정의).</summary>
        public required int KaiserCount { get; init; }
        public required double MeanEigenvalue { get; init; }
        public required int AboveMeanCount { get; init; }
        public required int ConstantFeatures { get; init; }
        public required int RowCount { get; init; }
        /// <summary>ALGLIB pcabuildbasis(전체 기저). 잘린 부분공간 반복이 아니다.</summary>
        public bool Exact => true;

        public int FeatureCount => Eigenvalues.Length;
        public int ComponentCount => Loadings.GetLength(1);
    }

    public static class PrincipalComponents
    {
        /// <summary>
        /// 주성분분석. <paramref name="components"/>가 1 미만이면 min(5, p). p를 넘으면 p로 자른다.
        /// 상관 척도는 각 열을 표본 표준편차(ddof=1)로 나눈 뒤 공분산을 고유분해한다 — 고유값은 상관행렬의 고유값.
        /// </summary>
        public static PcaResult Fit(
            double[,] x,
            IReadOnlyList<string> featureNames,
            PcaScale scale,
            int components,
            CancellationToken cancellation = default)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (featureNames.Count != p) throw new ArgumentException("Feature name count must match columns.", nameof(featureNames));
            if (n < 2) throw new DesignMatrixException("PCA needs at least 2 complete rows.");
            RequireFinite(x, cancellation);

            int constant = CountConstantColumns(x, cancellation);
            double[,] input = x;
            if (scale == PcaScale.Correlation)
            {
                cancellation.ThrowIfCancellationRequested();
                // ddof=1. 상수 열은 중심화 후 0 (FeatureScaler가 표준편차 0을 1로 두어 (x-mean)/1 = 0).
                input = FeatureScaler.Fit(x, ScalingMethod.ZScore).Transform(x);
            }

            cancellation.ThrowIfCancellationRequested();
            double[] s2;
            double[,] v;
            try
            {
                (s2, v) = CovarianceBasis(input, n, p, cancellation);
            }
            catch (alglib.alglibexception ex)
            {
                throw new DesignMatrixException(string.IsNullOrEmpty(ex.msg) ? "PCA failed." : ex.msg);
            }
            if (s2.Length != p || v.GetLength(0) != p || v.GetLength(1) != p)
                throw new InvalidOperationException("PCA returned an unexpected basis shape.");

            int m = components < 1 ? Math.Min(5, p) : Math.Min(components, p);
            var loadings = new double[p, m];
            for (int c = 0; c < m; c++)
            {
                FlipColumnSign(v, c);
                for (int j = 0; j < p; j++) loadings[j, c] = v[j, c];
            }

            double total = 0;
            for (int i = 0; i < p; i++)
            {
                if (s2[i] < 0 && s2[i] > -1e-12) s2[i] = 0; // 수치 잡음
                total += s2[i];
            }
            var ratio = new double[p];
            var cumulative = new double[p];
            double running = 0;
            int kaiser = 0, aboveMean = 0;
            double mean = p == 0 ? double.NaN : total / p;
            for (int i = 0; i < p; i++)
            {
                ratio[i] = total > 0 ? s2[i] / total : double.NaN;
                running += double.IsNaN(ratio[i]) ? 0 : ratio[i];
                cumulative[i] = total > 0 ? running : double.NaN;
                if (s2[i] > 1) kaiser++;
                if (s2[i] > mean) aboveMean++;
            }

            return new PcaResult
            {
                Scale = scale,
                Eigenvalues = s2,
                ExplainedRatio = ratio,
                CumulativeRatio = cumulative,
                Loadings = loadings,
                RequestedComponents = components < 1 ? m : components,
                FeatureNames = featureNames,
                KaiserCount = kaiser,
                MeanEigenvalue = mean,
                AboveMeanCount = aboveMean,
                ConstantFeatures = constant,
                RowCount = n,
            };
        }

        /// <summary>
        /// 중심화 공분산(ddof=1)을 행 범위 병렬·취소 가능한 두 번의 패스로 만든 뒤 p×p 대칭 고유분해(ALGLIB smatrixevd)로 푼다.
        /// n·p² 비용이 전부 취소 가능한 구간이고, 취소 불가한 고유분해는 p³(p×p)뿐이다.
        /// 고유값은 내림차순, 열은 고유벡터(부호는 호출 쪽 FlipColumnSign이 정한다).
        /// </summary>
        internal static (double[] Variances, double[,] Vectors) CovarianceBasis(double[,] x, int n, int p, CancellationToken cancellation)
        {
            var mean = new double[p];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++) mean[j] += x[i, j];
            }
            for (int j = 0; j < p; j++) mean[j] /= n;

            const int Chunk = 8192;
            int chunks = (n + Chunk - 1) / Chunk;
            var partial = new double[chunks][];
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellation }, c =>
            {
                var acc = new double[p * p];
                var row = new double[p];
                int lo = c * Chunk, hi = Math.Min(n, lo + Chunk);
                for (int i = lo; i < hi; i++)
                {
                    for (int j = 0; j < p; j++) row[j] = x[i, j] - mean[j];
                    for (int a = 0; a < p; a++)
                    {
                        double ra = row[a];
                        int off = a * p;
                        for (int b = a; b < p; b++) acc[off + b] += ra * row[b];
                    }
                }
                partial[c] = acc;
            });
            cancellation.ThrowIfCancellationRequested();

            var cov = new double[p, p];
            double denom = n - 1;
            for (int a = 0; a < p; a++)
                for (int b = a; b < p; b++)
                {
                    double s = 0;
                    for (int c = 0; c < chunks; c++) s += partial[c][a * p + b];
                    cov[a, b] = s / denom;
                }

            if (!alglib.smatrixevd(cov, p, 1, true, out double[] d, out double[,] z))
                throw new DesignMatrixException("PCA eigen decomposition did not converge.");
            var s2 = new double[p];
            var v = new double[p, p];
            for (int k = 0; k < p; k++)
            {
                int src = p - 1 - k; // ALGLIB는 오름차순
                s2[k] = d[src];
                for (int j = 0; j < p; j++) v[j, k] = z[j, src];
            }
            return (s2, v);
        }

        private static int CountConstantColumns(double[,] x, CancellationToken ct)
        {
            int n = x.GetLength(0), p = x.GetLength(1), constant = 0;
            for (int j = 0; j < p; j++)
            {
                if ((j & 63) == 0) ct.ThrowIfCancellationRequested();
                double v0 = x[0, j];
                bool same = true;
                for (int i = 1; i < n && same; i++)
                    if (x[i, j] != v0) same = false;
                if (same) constant++;
            }
            return constant;
        }

        internal static void FlipColumnSign(double[,] m, int col)
        {
            int rows = m.GetLength(0);
            int maxI = 0;
            double maxAbs = -1;
            for (int i = 0; i < rows; i++)
            {
                double a = Math.Abs(m[i, col]);
                if (a > maxAbs) { maxAbs = a; maxI = i; }
            }
            // sklearn svd_flip(u_based_decision=False): 각 성분에서 |적재량| 최대 원소가 양이 되게.
            if (m[maxI, col] < 0)
                for (int i = 0; i < rows; i++) m[i, col] = -m[i, col];
        }

        private static void RequireFinite(double[,] x, CancellationToken ct)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++)
                    if (!double.IsFinite(x[i, j]))
                        throw new DesignMatrixException("Feature values must be finite.");
            }
        }
    }

    public enum LdaSplitKind { Holdout, KFold }

    /// <summary>
    /// 선형판별 적합. 예측·확률은 sklearn LinearDiscriminantAnalysis(solver='svd', tol=1e-4)와 같다
    /// (합동 급내 공분산의 유사역, 사전확률 = 학습 빈도). 계수 행렬은 클래스별 점수(이진으로 접지 않음)라
    /// sklearn의 이진 coef_ 차이와는 다르고, 확률·예측은 같다. 판별 방향은 fisherldan 단위벡터.
    /// </summary>
    public sealed class LinearDiscriminantModel
    {
        public required int ClassCount { get; init; }
        public required int FeatureCount { get; init; }
        public required IReadOnlyList<string> ClassNames { get; init; }
        public required IReadOnlyList<string> FeatureNames { get; init; }
        public required double[] Priors { get; init; }
        public required int[] ClassCounts { get; init; }
        /// <summary>K×p 클래스 평균.</summary>
        public required double[,] ClassMeans { get; init; }
        /// <summary>sklearn explained_variance_ratio_ (svd 솔버, 최대 min(K−1, p)개).</summary>
        public required double[] ExplainedVarianceRatio { get; init; }
        /// <summary>p×d 판별 계수. fisherldan이면 단위 노름, 실패 시 SVD 스케일링(단위 노름 아님).</summary>
        public required double[,] Directions { get; init; }
        public required bool UsedFisherDirections { get; init; }
        /// <summary>급내 중심화 행렬 SVD에서 tol보다 큰 특이값 개수.</summary>
        public required int WithinRank { get; init; }
        public required double Tolerance { get; init; }
        /// <summary>K×p. 행 점수 = x·coef + intercept. argmax가 예측.</summary>
        public required double[,] Coef { get; init; }
        public required double[] Intercept { get; init; }

        public bool Singular => WithinRank < FeatureCount;
        public int DirectionCount => Directions.GetLength(1);

        public int[] Predict(double[,] x, IReadOnlyList<int>? rows = null)
        {
            int n = rows?.Count ?? x.GetLength(0);
            var pred = new int[n];
            for (int i = 0; i < n; i++) pred[i] = ArgMax(Scores(x, rows?[i] ?? i));
            return pred;
        }

        public double[,] PredictProba(double[,] x, IReadOnlyList<int>? rows = null)
        {
            int n = rows?.Count ?? x.GetLength(0);
            int k = ClassCount;
            var proba = new double[n, k];
            for (int i = 0; i < n; i++)
            {
                var scores = Scores(x, rows?[i] ?? i);
                double max = scores.Max();
                double sum = 0;
                for (int c = 0; c < k; c++)
                {
                    double e = Math.Exp(scores[c] - max);
                    proba[i, c] = e;
                    sum += e;
                }
                for (int c = 0; c < k; c++) proba[i, c] /= sum;
            }
            return proba;
        }

        private double[] Scores(double[,] x, int row)
        {
            int k = ClassCount, p = FeatureCount;
            var scores = new double[k];
            for (int c = 0; c < k; c++)
            {
                double s = Intercept[c];
                for (int j = 0; j < p; j++) s += x[row, j] * Coef[c, j];
                scores[c] = s;
            }
            return scores;
        }

        private static int ArgMax(double[] scores)
        {
            int best = 0;
            for (int c = 1; c < scores.Length; c++)
                if (scores[c] > scores[best]) best = c;
            return best;
        }
    }

    public sealed class LdaEvaluation
    {
        public required ClassificationMetrics Metrics { get; init; }
        /// <summary>각 분할의 학습 최빈 클래스를 검증 행에 적용한 정확도.</summary>
        public required double MajorityBaselineAccuracy { get; init; }
        public required int OverallMajorityClass { get; init; }
        public required double OverallMajorityProportion { get; init; }
        public required int SplitCount { get; init; }
        public required bool Stratified { get; init; }
        public required int Seed { get; init; }
        /// <summary>홀드아웃 검증 비율. k-겹이면 0.</summary>
        public required double TestFraction { get; init; }
        public required bool AnySingular { get; init; }
    }

    public static class LinearDiscriminant
    {
        /// <summary>sklearn LinearDiscriminantAnalysis tol 기본값.</summary>
        public const double DefaultTolerance = 1e-4;

        public static LinearDiscriminantModel Fit(
            double[,] x,
            int[] labels,
            int classCount,
            IReadOnlyList<string>? featureNames = null,
            IReadOnlyList<string>? classNames = null,
            bool computeDirections = true,
            double tolerance = DefaultTolerance,
            CancellationToken cancellation = default)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (labels.Length != n) throw new ArgumentException("Label length must match rows.", nameof(labels));
            if (classCount < 2) throw new DesignMatrixException("LDA needs at least 2 classes.");
            if (n <= classCount) throw new DesignMatrixException("The number of samples must be greater than the number of classes.");
            if (!(tolerance > 0)) throw new ArgumentOutOfRangeException(nameof(tolerance));
            RequireFinite(x, cancellation);

            featureNames ??= DefaultNames(p, "x");
            classNames ??= DefaultNames(classCount, "c");
            if (featureNames.Count != p) throw new ArgumentException("Feature name count must match columns.", nameof(featureNames));
            if (classNames.Count != classCount) throw new ArgumentException("Class name count must match classes.", nameof(classNames));

            var counts = new int[classCount];
            var means = new double[classCount, p];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int c = labels[i];
                if (c < 0 || c >= classCount) throw new DesignMatrixException("A class index is outside 0..K-1.");
                counts[c]++;
                for (int j = 0; j < p; j++) means[c, j] += x[i, j];
            }
            for (int c = 0; c < classCount; c++)
            {
                if (counts[c] == 0) throw new DesignMatrixException($"Class {c} has no training rows.");
                for (int j = 0; j < p; j++) means[c, j] /= counts[c];
            }

            var priors = new double[classCount];
            var xbar = new double[p];
            for (int c = 0; c < classCount; c++)
            {
                priors[c] = (double)counts[c] / n;
                for (int j = 0; j < p; j++) xbar[j] += priors[c] * means[c, j];
            }

            // 급내 중심화. 클래스 순으로 이어 붙이면 sklearn _solve_svd와 같은 행 순서다(특이벡터 부호만 다를 수 있음).
            var offsets = new int[classCount + 1];
            for (int c = 0; c < classCount; c++) offsets[c + 1] = offsets[c] + counts[c];
            var cursor = (int[])offsets.Clone();
            var xc = new double[n, p];
            for (int i = 0; i < n; i++)
            {
                int dest = cursor[labels[i]]++;
                int c = labels[i];
                for (int j = 0; j < p; j++) xc[dest, j] = x[i, j] - means[c, j];
            }

            var std = new double[p];
            for (int j = 0; j < p; j++)
            {
                double ss = 0;
                for (int i = 0; i < n; i++) ss += xc[i, j] * xc[i, j];
                std[j] = Math.Sqrt(ss / n); // np.std, ddof=0. 급내 중심이라 평균은 0.
                if (std[j] == 0) std[j] = 1;
            }
            double fac = 1.0 / (n - classCount);
            double sfac = Math.Sqrt(fac);
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++) xc[i, j] = sfac * xc[i, j] / std[j];
            }

            var (s, vt) = Svd(xc, n, p);
            int rank = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] > tolerance) rank++;
            if (rank == 0)
                throw new DesignMatrixException("Within-class covariance has rank 0: features do not vary within classes, so LDA cannot form a discriminant.");

            var scalings = new double[p, rank];
            for (int r = 0; r < rank; r++)
                for (int j = 0; j < p; j++)
                    scalings[j, r] = vt[r, j] / std[j] / s[r];

            double facb = 1.0 / (classCount - 1);
            var between = new double[classCount, rank];
            for (int c = 0; c < classCount; c++)
            {
                double scale = Math.Sqrt(n * priors[c] * facb);
                for (int r = 0; r < rank; r++)
                {
                    double dot = 0;
                    for (int j = 0; j < p; j++) dot += (means[c, j] - xbar[j]) * scalings[j, r];
                    between[c, r] = scale * dot;
                }
            }

            var (s2, vt2) = Svd(between, classCount, rank);
            if (s2.Length == 0 || !(s2[0] > 0))
                throw new DesignMatrixException("Between-class scatter has no positive component; classes are not separated in the within-class subspace.");

            double sumSq = 0;
            for (int i = 0; i < s2.Length; i++) sumSq += s2[i] * s2[i];
            int maxComp = Math.Min(classCount - 1, p);
            int ratioLen = Math.Min(maxComp, s2.Length);
            var ratio = new double[ratioLen];
            for (int i = 0; i < ratioLen; i++)
                ratio[i] = sumSq > 0 ? s2[i] * s2[i] / sumSq : double.NaN;

            double cut = tolerance * s2[0];
            int rank2 = 0;
            for (int i = 0; i < s2.Length; i++)
                if (s2[i] > cut) rank2++;
            if (rank2 == 0)
                throw new DesignMatrixException("Between-class scatter is below the SVD tolerance; LDA cannot form a discriminant.");

            var scalingsFinal = new double[p, rank2];
            for (int j = 0; j < p; j++)
                for (int c = 0; c < rank2; c++)
                {
                    double dot = 0;
                    for (int r = 0; r < rank; r++) dot += scalings[j, r] * vt2[c, r];
                    scalingsFinal[j, c] = dot;
                }

            var coefClass = new double[classCount, rank2];
            for (int k = 0; k < classCount; k++)
                for (int c = 0; c < rank2; c++)
                {
                    double dot = 0;
                    for (int j = 0; j < p; j++) dot += (means[k, j] - xbar[j]) * scalingsFinal[j, c];
                    coefClass[k, c] = dot;
                }

            var intercept = new double[classCount];
            var coef = new double[classCount, p];
            for (int k = 0; k < classCount; k++)
            {
                double ss = 0;
                for (int c = 0; c < rank2; c++) ss += coefClass[k, c] * coefClass[k, c];
                intercept[k] = -0.5 * ss + Math.Log(priors[k]);
                for (int j = 0; j < p; j++)
                {
                    double dot = 0;
                    for (int c = 0; c < rank2; c++) dot += coefClass[k, c] * scalingsFinal[j, c];
                    coef[k, j] = dot;
                }
                double adj = 0;
                for (int j = 0; j < p; j++) adj += xbar[j] * coef[k, j];
                intercept[k] -= adj;
            }

            bool usedFisher = false;
            double[,] directions;
            if (computeDirections)
            {
                cancellation.ThrowIfCancellationRequested();
                directions = TryFisherDirections(x, labels, classCount, out usedFisher);
                if (!usedFisher)
                {
                    directions = new double[p, rank2];
                    for (int c = 0; c < rank2; c++)
                    {
                        for (int j = 0; j < p; j++) directions[j, c] = scalingsFinal[j, c];
                        PrincipalComponents.FlipColumnSign(directions, c);
                    }
                }
            }
            else directions = new double[p, 0];

            return new LinearDiscriminantModel
            {
                ClassCount = classCount,
                FeatureCount = p,
                ClassNames = classNames,
                FeatureNames = featureNames,
                Priors = priors,
                ClassCounts = counts,
                ClassMeans = means,
                ExplainedVarianceRatio = ratio,
                Directions = directions,
                UsedFisherDirections = usedFisher,
                WithinRank = rank,
                Tolerance = tolerance,
                Coef = coef,
                Intercept = intercept,
            };
        }

        /// <summary>
        /// 홀드아웃 또는 k-겹으로 적합·예측해 혼동행렬을 합산한다. 각 분할은 학습 행만으로 적합한다(누수 없음).
        /// 학습 분할에 클래스가 2개 미만이면 부분 결과 없이 예외.
        /// </summary>
        public static LdaEvaluation Evaluate(
            double[,] x,
            int[] labels,
            int classCount,
            IReadOnlyList<string> classNames,
            LdaSplitKind split,
            int folds,
            double testFraction,
            int seed,
            bool stratified,
            double tolerance = DefaultTolerance,
            CancellationToken cancellation = default)
        {
            if (classNames.Count != classCount) throw new ArgumentException("Class name count must match classes.", nameof(classNames));
            IReadOnlyList<DataSplit> splits = split == LdaSplitKind.Holdout
                ? new[] { ClassifierEvaluation.Holdout(labels, testFraction, seed, stratified) }
                : ClassifierEvaluation.KFold(labels, folds, seed, stratified);

            var cm = new long[classCount, classCount];
            long baselineCorrect = 0;
            bool anySingular = false;
            foreach (var part in splits)
            {
                cancellation.ThrowIfCancellationRequested();
                var trainCounts = new int[classCount];
                foreach (int i in part.Train) trainCounts[labels[i]]++;
                int present = 0, majority = 0;
                for (int c = 0; c < classCount; c++)
                {
                    if (trainCounts[c] > 0) present++;
                    if (trainCounts[c] > trainCounts[majority]) majority = c;
                }
                if (present < 2)
                    throw new DesignMatrixException("A training split has fewer than 2 classes. Use fewer folds, stratification, or more rows per class.");

                int[] localToGlobal = new int[present];
                var globalToLocal = new int[classCount];
                int local = 0;
                for (int c = 0; c < classCount; c++)
                {
                    if (trainCounts[c] == 0) { globalToLocal[c] = -1; continue; }
                    globalToLocal[c] = local;
                    localToGlobal[local] = c;
                    local++;
                }

                var (xt, yt) = Slice(x, labels, part.Train, globalToLocal);
                var model = Fit(xt, yt, present, null, null, computeDirections: false, tolerance, cancellation);
                if (model.Singular) anySingular = true;
                var pred = model.Predict(x, part.Test);
                for (int t = 0; t < part.Test.Length; t++)
                {
                    int actual = labels[part.Test[t]];
                    int predicted = localToGlobal[pred[t]];
                    cm[actual, predicted]++;
                    if (actual == majority) baselineCorrect++;
                }
            }

            var overall = new int[classCount];
            for (int i = 0; i < labels.Length; i++) overall[labels[i]]++;
            int overallMajority = 0;
            for (int c = 1; c < classCount; c++)
                if (overall[c] > overall[overallMajority]) overallMajority = c;
            long tested = 0;
            for (int a = 0; a < classCount; a++)
                for (int b = 0; b < classCount; b++) tested += cm[a, b];

            return new LdaEvaluation
            {
                Metrics = ClassifierEvaluation.FromConfusion(cm),
                MajorityBaselineAccuracy = tested == 0 ? double.NaN : (double)baselineCorrect / tested,
                OverallMajorityClass = overallMajority,
                OverallMajorityProportion = labels.Length == 0 ? double.NaN : (double)overall[overallMajority] / labels.Length,
                SplitCount = splits.Count,
                Stratified = stratified,
                Seed = seed,
                TestFraction = split == LdaSplitKind.Holdout ? testFraction : 0,
                AnySingular = anySingular,
            };
        }

        private static (double[,] x, int[] y) Slice(double[,] source, int[] labels, int[] rows, int[] globalToLocal)
        {
            int n = rows.Length, p = source.GetLength(1);
            var x = new double[n, p];
            var y = new int[n];
            for (int i = 0; i < n; i++)
            {
                int r = rows[i];
                for (int j = 0; j < p; j++) x[i, j] = source[r, j];
                y[i] = globalToLocal[labels[r]];
            }
            return (x, y);
        }

        private static double[,] TryFisherDirections(double[,] x, int[] labels, int classCount, out bool used)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            int d = Math.Min(p, classCount - 1);
            var xy = new double[n, p + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < p; j++) xy[i, j] = x[i, j];
                xy[i, p] = labels[i];
            }
            try
            {
                alglib.fisherldan(xy, n, p, classCount, out double[,] w);
                if (w.GetLength(0) != p || w.GetLength(1) < d)
                {
                    used = false;
                    return new double[p, 0];
                }
                var directions = new double[p, d];
                for (int c = 0; c < d; c++)
                {
                    for (int j = 0; j < p; j++) directions[j, c] = w[j, c];
                    PrincipalComponents.FlipColumnSign(directions, c);
                }
                used = true;
                return directions;
            }
            catch (alglib.alglibexception)
            {
                used = false;
                return new double[p, 0];
            }
        }

        private static (double[] s, double[,] vt) Svd(double[,] a, int m, int n)
        {
            if (!alglib.rmatrixsvd(a, m, n, 0, 1, 2, out double[] w, out _, out double[,] vt))
                throw new InvalidOperationException("SVD failed while fitting LDA.");
            return (w, vt);
        }

        private static void RequireFinite(double[,] x, CancellationToken ct)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++)
                    if (!double.IsFinite(x[i, j]))
                        throw new DesignMatrixException("Feature values must be finite.");
            }
        }

        private static string[] DefaultNames(int n, string prefix)
        {
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = prefix + i;
            return names;
        }
    }
}
