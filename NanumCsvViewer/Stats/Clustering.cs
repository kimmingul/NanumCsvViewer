
namespace NanumCsvViewer.Stats
{
    /// <summary>K-means 옵션. 시드는 1 이상이어야 실행마다 같은 결과가 나온다.</summary>
    public sealed record KMeansOptions
    {
        public int K { get; init; } = 3;
        /// <summary>재시작 횟수. 가장 작은 에너지(WSS)를 고른다.</summary>
        public int Restarts { get; init; } = 5;
        /// <summary>재시작당 최대 Lloyd 반복. 0이면 안전 상한(<see cref="KMeansClustering.DefaultMaxIterations"/>). 상한에 닿으면 미수렴으로 보고한다.</summary>
        public int MaxIterations { get; init; }
        public int Seed { get; init; } = 1;
        public ScalingMethod Scaling { get; init; } = ScalingMethod.ZScore;
        /// <summary>k = 2..K 의 WSS(엘보)를 같이 계산한다.</summary>
        public bool Elbow { get; init; }
        /// <summary>실루엣을 계산할 최대 행 수. 넘으면 시드 고정 표본(근사).</summary>
        public int SilhouetteSampleSize { get; init; } = 5000;
        /// <summary>이 행 수를 넘으면 시드 고정 표본(이 크기)에서 초기 중심을 찾고 전체 행 Lloyd로 정제한다.</summary>
        public int FullDataLimit { get; init; } = 200_000;
    }

    /// <summary>한 군집. 중심은 원래 단위, WSS는 알고리즘이 최적한 스케일 공간.</summary>
    public sealed record KMeansCluster(int Index, int Size, double[] CenterOriginal, double WithinSs);

    /// <summary>엘보 한 점. 실패한 k는 Succeeded=false, TotalWss=NaN.</summary>
    public sealed record ElbowPoint(int K, double TotalWss, bool Succeeded);

    /// <summary>K-means 결과. 배정은 0..K-1(시드에 대해 결정적).</summary>
    public sealed class KMeansResult
    {
        public required int[] Assignment { get; init; }
        public required KMeansCluster[] Clusters { get; init; }
        /// <summary>스케일 공간의 총 군집 내 제곱합(배정된 점과 그 평균의 거리).</summary>
        public required double TotalWss { get; init; }
        public required double BetweenSs { get; init; }
        public required double TotalSs { get; init; }
        /// <summary>BSS/TSS. TSS가 0이면 NaN.</summary>
        public required double BetweenTotalRatio { get; init; }
        /// <summary>1 = 가장 좋은 재시작이 배정 불변(Lloyd 고정점)으로 수렴, 2 = 그 재시작이 반복 상한에 도달해 미수렴(배정은 그대로 보고). 서로 다른 점이 k개 미만이면 예외.</summary>
        public required int TerminationType { get; init; }
        /// <summary>모든 재시작의 Lloyd 반복 합계.</summary>
        public required int Iterations { get; init; }
        /// <summary>수렴한 재시작 수(전체 Restarts 중). 표본 초기화이면 표본 단계의 값.</summary>
        public required int ConvergedRestarts { get; init; }
        /// <summary>선택된 배정이 수렴한 재시작에서 나왔는지(TerminationType == 1).</summary>
        public bool Converged => TerminationType == 1;
        /// <summary>0이면 전체 행에서 초기화·Lloyd를 돌린다. 양수면 이 행 수의 시드 고정 표본에서 초기 중심을 찾고 전체 행 Lloyd로 정제.</summary>
        public int InitializationSampleRows { get; init; }
        public int RefinementIterations { get; init; }
        /// <summary>전체 행 Lloyd 정제가 최대 반복 안에 배정 불변으로 수렴했는지(표본 초기화일 때만 의미).</summary>
        public bool RefinementConverged { get; init; } = true;
        /// <summary>엘보 표를 계산한 표본 행 수(0이면 전체).</summary>
        public int ElbowSampleRows { get; init; }
        public required int Seed { get; init; }
        public required int Restarts { get; init; }
        public required ScalingMethod Scaling { get; init; }
        public required IReadOnlyList<string> FeatureNames { get; init; }
        /// <summary>유클리드 실루엣(스케일 공간). 정의되지 않으면 NaN.</summary>
        public required double Silhouette { get; init; }
        public required int SilhouetteRows { get; init; }
        public required bool SilhouetteSampled { get; init; }
        public required bool SilhouetteDefined { get; init; }
        /// <summary>최종 평균에 대해 배정이 최근접 군집과 일치하는지(Lloyd 고정점).</summary>
        public required bool LloydFixedPoint { get; init; }
        public required int EmptyClusters { get; init; }
        public IReadOnlyList<ElbowPoint>? Elbow { get; init; }
    }

    /// <summary>
    /// K-means(이슈 #27). 자체 구현: 탐욕 k-means++ 초기화 · 재시작 · 청크 병렬 Lloyd(유클리드, 시드 고정, 취소 가능).
    /// 배정 후 중심·WSS는 그 배정의 평균으로 다시 계산한다 — sklearn inertia(같은 배정)와 같은 정의.
    /// 보고용 중심은 아핀 스케일을 되돌려 원래 단위다.
    /// </summary>
    public static class KMeansClustering
    {
        public static KMeansResult Fit(
            double[,] x,
            KMeansOptions? options = null,
            IReadOnlyList<string>? featureNames = null,
            CancellationToken cancellation = default)
        {
            options ??= new KMeansOptions();
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p < 1) throw new DesignMatrixException("Select at least one feature column.");
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows for K-means.");
            if (options.K < 2) throw new DesignMatrixException("k must be at least 2.");
            if (options.K > n) throw new DesignMatrixException($"k ({options.K}) exceeds the number of complete rows ({n}).");
            if (options.Restarts < 1) throw new DesignMatrixException("Restarts must be at least 1.");
            if (options.MaxIterations < 0) throw new DesignMatrixException("Max iterations cannot be negative.");
            if (options.Seed < 1) throw new DesignMatrixException("Seed must be a positive integer so the run is reproducible.");
            if (options.SilhouetteSampleSize < 2) throw new DesignMatrixException("Silhouette sample size must be at least 2.");
            if (options.FullDataLimit < options.K) throw new DesignMatrixException("The initialization sample must have at least k rows.");
            var names = featureNames ?? DefaultNames(p);
            if (names.Count != p) throw new ArgumentException("Feature name count must match columns.", nameof(featureNames));
            RequireFinite(x, cancellation);

            var scaler = FeatureScaler.Fit(x, options.Scaling);
            var z = scaler.Transform(x);
            cancellation.ThrowIfCancellationRequested();

            // 큰 데이터: 시드 고정 표본에서 k-means++·재시작·Lloyd로 시작 중심을 찾고, 전체 행에서 Lloyd 반복(배정 ↔ 평균)으로 정제한다.
            int[] assignment;
            int iterations, terminationType, convergedRestarts;
            int initRows = 0, refineIterations = 0;
            bool refineConverged = true;
            double[,] elbowData = z;
            if (n > options.FullDataLimit)
            {
                var sampleIdx = SampleRows(n, options.FullDataLimit, options.Seed);
                var zs = Rows(z, sampleIdx);
                var init = Run(zs, options.K, options.Restarts, options.MaxIterations, options.Seed, cancellation);
                if (init.Termination < 0)
                    throw new DesignMatrixException(FailureMessage(init.Termination, options.K));
                (assignment, refineIterations, refineConverged) = Refine(z, init.Centers, options.K, cancellation);
                iterations = init.Iterations;
                terminationType = init.Termination;
                convergedRestarts = init.ConvergedRestarts;
                initRows = sampleIdx.Length;
                elbowData = zs;
            }
            else
            {
                var run = Run(z, options.K, options.Restarts, options.MaxIterations, options.Seed, cancellation);
                if (run.Termination < 0)
                    throw new DesignMatrixException(FailureMessage(run.Termination, options.K));
                assignment = run.Assignment;
                iterations = run.Iterations;
                terminationType = run.Termination;
                convergedRestarts = run.ConvergedRestarts;
            }

            var stats = Summarize(z, assignment, options.K, scaler, cancellation);
            IReadOnlyList<ElbowPoint>? elbow = null;
            if (options.Elbow)
                elbow = ElbowTable(elbowData, options, initRows > 0 ? Inertia(elbowData, Nearest(elbowData, stats, options.K), options.K, cancellation) : stats.TotalWss, cancellation);

            var (sil, silRows, sampled, defined) = Silhouette(z, assignment, options.K, options.Seed, options.SilhouetteSampleSize, cancellation);

            return new KMeansResult
            {
                Assignment = assignment,
                Clusters = stats.Clusters,
                TotalWss = stats.TotalWss,
                BetweenSs = stats.BetweenSs,
                TotalSs = stats.TotalSs,
                BetweenTotalRatio = stats.Ratio,
                TerminationType = terminationType,
                ConvergedRestarts = convergedRestarts,
                Iterations = iterations,
                InitializationSampleRows = initRows,
                RefinementIterations = refineIterations,
                RefinementConverged = refineConverged,
                ElbowSampleRows = options.Elbow && initRows > 0 ? initRows : 0,
                Seed = options.Seed,
                Restarts = options.Restarts,
                Scaling = options.Scaling,
                FeatureNames = names,
                Silhouette = sil,
                SilhouetteRows = silRows,
                SilhouetteSampled = sampled,
                SilhouetteDefined = defined,
                LloydFixedPoint = stats.FixedPoint,
                EmptyClusters = stats.Empty,
                Elbow = elbow,
            };
        }

        // Centers: 배정 평균(스케일 공간, 평탄 k*p). Termination: 1 = 가장 좋은 재시작이 배정 불변으로 수렴, 2 = 그 재시작이 반복 상한에 도달(배정은 보고하되 미수렴),
        // -3 = 서로 다른 점이 k개 미만(배정 없음).
        private readonly record struct RunResult(int[] Assignment, double[] Centers, int Termination, int Iterations, int ConvergedRestarts);

        // 재시작마다 k-means++ 초기화 → Lloyd. 재시작은 같은 시드 난수열을 순서대로 쓰므로 재시작 수를 늘려도 앞선 재시작은 그대로다.
        // 가장 작은 WSS(배정 평균 기준)를 고르고, 같으면 먼저 나온 재시작. 취소는 청크마다 확인한다.
        private static RunResult Run(double[,] z, int k, int restarts, int maxIts, int seed, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int n = z.GetLength(0), p = z.GetLength(1);
            var plan = new Plan(n, p, k);
            var workspace = new Workspace(plan);
            var rng = new Random(seed);
            int limit = maxIts == 0 ? DefaultMaxIterations : maxIts;
            int[]? bestAssign = null;
            double[]? bestCenters = null;
            double bestInertia = double.PositiveInfinity;
            bool bestConverged = false;
            int totalIterations = 0, convergedRestarts = 0;
            var assignment = new int[n];
            for (int r = 0; r < restarts; r++)
            {
                var centers = Seeding(z, plan, rng, cancellation);
                if (centers is null) return new RunResult(Array.Empty<int>(), Array.Empty<double>(), -3, totalIterations, convergedRestarts);
                var o = Lloyd(z, workspace, centers, assignment, limit, cancellation);
                if (o.Outcome == LloydOutcome.TooFewDistinct)
                    return new RunResult(Array.Empty<int>(), Array.Empty<double>(), -3, totalIterations + o.Iterations, convergedRestarts);
                totalIterations += o.Iterations;
                bool converged = o.Outcome == LloydOutcome.Converged;
                if (converged) convergedRestarts++;
                double inertia = converged ? o.Inertia : Inertia(z, assignment, k, cancellation);
                if (inertia < bestInertia)
                {
                    bestInertia = inertia;
                    bestConverged = converged;
                    bestCenters = centers;
                    var spare = bestAssign;
                    bestAssign = assignment;
                    assignment = spare ?? new int[n];
                }
            }
            return new RunResult(bestAssign!, bestCenters!, bestConverged ? 1 : 2, totalIterations, convergedRestarts);
        }

        private readonly record struct Summary(KMeansCluster[] Clusters, double TotalWss, double BetweenSs, double TotalSs, double Ratio, bool FixedPoint, int Empty, double[,] ScaledCenters);

        // WSS는 배정된 행의 평균(스케일 공간)에 대한 제곱거리 합. 중심의 원래 단위는 아핀 역변환.
        private static Summary Summarize(double[,] z, int[] assignment, int k, FeatureScaler scaler, CancellationToken cancellation)
        {
            int n = z.GetLength(0), p = z.GetLength(1);
            var count = new int[k];
            var sum = new double[k, p];
            var grand = new double[p];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int c = assignment[i];
                count[c]++;
                for (int j = 0; j < p; j++)
                {
                    double v = z[i, j];
                    sum[c, j] += v;
                    grand[j] += v;
                }
            }
            var mean = new double[k, p];
            for (int c = 0; c < k; c++)
            {
                if (count[c] == 0) continue;
                double inv = 1.0 / count[c];
                for (int j = 0; j < p; j++) mean[c, j] = sum[c, j] * inv;
            }
            for (int j = 0; j < p; j++) grand[j] /= n;

            var within = new double[k];
            double wss = 0, tss = 0;
            bool fixedPoint = true;
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int own = assignment[i];
                double ownD = 0, best = double.PositiveInfinity;
                int nearest = 0;
                for (int c = 0; c < k; c++)
                {
                    if (count[c] == 0) continue;
                    double d2 = 0;
                    for (int j = 0; j < p; j++)
                    {
                        double d = z[i, j] - mean[c, j];
                        d2 += d * d;
                    }
                    if (c == own) ownD = d2;
                    if (d2 < best) { best = d2; nearest = c; }
                }
                if (nearest != own) fixedPoint = false;
                within[own] += ownD;
                wss += ownD;
                double td = 0;
                for (int j = 0; j < p; j++)
                {
                    double d = z[i, j] - grand[j];
                    td += d * d;
                }
                tss += td;
            }

            var clusters = new KMeansCluster[k];
            int empty = 0;
            for (int c = 0; c < k; c++)
            {
                var center = new double[p];
                if (count[c] == 0)
                {
                    empty++;
                    for (int j = 0; j < p; j++) center[j] = double.NaN;
                }
                else
                {
                    for (int j = 0; j < p; j++)
                        center[j] = mean[c, j] * scaler.Scale[j] + scaler.Center[j];
                }
                clusters[c] = new KMeansCluster(c, count[c], center, within[c]);
            }
            double bss = tss - wss;
            double ratio = tss == 0 ? double.NaN : bss / tss;
            return new Summary(clusters, wss, bss, tss, ratio, fixedPoint, empty, mean);
        }

        // 엘보의 요청 k는 본 적합을 재사용한다(같은 시드·재시작이면 다시 돌려도 같음).
        private static List<ElbowPoint> ElbowTable(double[,] z, KMeansOptions options, double requestedWss, CancellationToken cancellation)
        {
            var rows = new List<ElbowPoint>();
            for (int kk = 2; kk <= options.K; kk++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (kk == options.K)
                {
                    rows.Add(new ElbowPoint(kk, requestedWss, true));
                    continue;
                }
                var run = Run(z, kk, options.Restarts, options.MaxIterations, options.Seed, cancellation);
                if (run.Termination < 0)
                {
                    rows.Add(new ElbowPoint(kk, double.NaN, false));
                    continue;
                }
                rows.Add(new ElbowPoint(kk, Inertia(z, run.Assignment, kk, cancellation), true));
            }
            return rows;
        }

        // 엘보 비교용. 배정 평균에 대한 제곱거리 합만 구한다(역변환 없음).
        private static double Inertia(double[,] z, int[] assignment, int k, CancellationToken cancellation)
        {
            int n = z.GetLength(0), p = z.GetLength(1);
            var count = new int[k];
            var sum = new double[k, p];
            for (int i = 0; i < n; i++)
            {
                int c = assignment[i];
                count[c]++;
                for (int j = 0; j < p; j++) sum[c, j] += z[i, j];
            }
            var mean = new double[k, p];
            for (int c = 0; c < k; c++)
            {
                if (count[c] == 0) continue;
                double inv = 1.0 / count[c];
                for (int j = 0; j < p; j++) mean[c, j] = sum[c, j] * inv;
            }
            double wss = 0;
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int c = assignment[i];
                if (count[c] == 0) continue;
                for (int j = 0; j < p; j++)
                {
                    double d = z[i, j] - mean[c, j];
                    wss += d * d;
                }
            }
            return wss;
        }

        // 실루엣은 유클리드 거리(제곱 아님). n이 표본 한도를 넘으면 시드 고정 비복원 추출 — 근사라고 표시한다.
        private static (double Score, int Rows, bool Sampled, bool Defined) Silhouette(
            double[,] z, int[] assignment, int k, int seed, int sampleSize, CancellationToken cancellation)
        {
            int n = z.GetLength(0);
            bool sampled = n > sampleSize;
            var rows = SampleRows(n, Math.Min(n, sampleSize), seed);
            int m = rows.Length;
            var labels = new int[m];
            var freq = new int[k];
            for (int i = 0; i < m; i++)
            {
                labels[i] = assignment[rows[i]];
                freq[labels[i]]++;
            }
            int present = 0;
            for (int c = 0; c < k; c++) if (freq[c] > 0) present++;
            // sklearn 정의: 2 ≤ 군집 수 ≤ 표본 수 − 1. 모든 행이 제 군집이면 실루엣이 정의되지 않는다.
            if (present < 2 || m < 3 || present >= m)
                return (double.NaN, m, sampled, false);

            var sum = new double[m, k];
            int p = z.GetLength(1);
            for (int a = 0; a < m; a++)
            {
                if ((a & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int ia = rows[a];
                for (int b = a + 1; b < m; b++)
                {
                    int ib = rows[b];
                    double d2 = 0;
                    for (int j = 0; j < p; j++)
                    {
                        double d = z[ia, j] - z[ib, j];
                        d2 += d * d;
                    }
                    double dist = Math.Sqrt(d2);
                    sum[a, labels[b]] += dist;
                    sum[b, labels[a]] += dist;
                }
            }

            double total = 0;
            for (int i = 0; i < m; i++)
            {
                int own = labels[i];
                if (freq[own] <= 1) continue; // 크기 1 군집은 0 (sklearn nan_to_num)
                double aMean = sum[i, own] / (freq[own] - 1);
                double bMean = double.PositiveInfinity;
                for (int c = 0; c < k; c++)
                {
                    if (c == own || freq[c] == 0) continue;
                    double mean = sum[i, c] / freq[c];
                    if (mean < bMean) bMean = mean;
                }
                double denom = Math.Max(aMean, bMean);
                total += denom == 0 ? 0 : (bMean - aMean) / denom;
            }
            return (total / m, m, sampled, true);
        }

        // 부분 Fisher-Yates. 점수 자체는 순서에 무관하지만 순회를 고정하려고 정렬한다.
        private static int[] SampleRows(int n, int take, int seed)
        {
            if (take >= n)
            {
                var all = new int[n];
                for (int i = 0; i < n; i++) all[i] = i;
                return all;
            }
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            var rng = new Random(seed);
            for (int i = 0; i < take; i++)
            {
                int j = i + rng.Next(n - i);
                (idx[i], idx[j]) = (idx[j], idx[i]);
            }
            var sample = new int[take];
            Array.Copy(idx, sample, take);
            Array.Sort(sample);
            return sample;
        }

        private static string FailureMessage(int termination, int k) => termination switch
        {
            -3 => $"K-means failed: fewer than {k} distinct points (or k is invalid for this data). No partition is reported.",
            _ => $"K-means failed (termination {termination}). No partition is reported.",
        };

        private static void RequireFinite(double[,] x, CancellationToken ct)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++)
                    if (!double.IsFinite(x[i, j]))
                        throw new DesignMatrixException("K-means needs finite feature values (found NaN or infinity).");
            }
        }

        private static string[] DefaultNames(int p)
        {
            var names = new string[p];
            for (int j = 0; j < p; j++) names[j] = "X" + (j + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return names;
        }

        /// <summary>전체 행 Lloyd 정제 최대 반복.</summary>
        public const int MaxRefineIterations = 50;

        private static double[,] Rows(double[,] z, int[] rows)
        {
            int p = z.GetLength(1);
            var r = new double[rows.Length, p];
            for (int i = 0; i < rows.Length; i++) for (int j = 0; j < p; j++) r[i, j] = z[rows[i], j];
            return r;
        }

        // 전체 행 Lloyd 정제: 표본에서 찾은 중심(스케일 공간, 평탄 k*p)에서 시작. 반환 반복·수렴은 같은 Lloyd 루프의 결과다.
        internal static (int[] Assignment, int Iterations, bool Converged) Refine(double[,] z, double[] startCenters, int k, CancellationToken ct)
        {
            int n = z.GetLength(0), p = z.GetLength(1);
            var workspace = new Workspace(new Plan(n, p, k));
            var centers = (double[])startCenters.Clone();
            var assignment = new int[n];
            var o = Lloyd(z, workspace, centers, assignment, MaxRefineIterations, ct);
            if (o.Outcome == LloydOutcome.TooFewDistinct)
                throw new DesignMatrixException(FailureMessage(-3, k));
            return (assignment, o.Iterations, o.Outcome == LloydOutcome.Converged);
        }

        // ===== 자체 K-means(k-means++ · 재시작 · 병렬 Lloyd) =====
        // 모든 병렬 합산은 n·k·p에만 의존하는 고정 크기 청크로 나누고 청크 순서대로 합쳐, 코어 수와 무관하게 결과가 같다.
        // 취소는 청크마다(수 ms) 확인하므로 반복 중간에도 즉시 빠져나온다 — 백그라운드에 남는 작업이 없다.

        /// <summary>0(제한 없음)을 요청했을 때 쓰는 재시작당 Lloyd 반복 안전 상한. 도달하면 수렴하지 못했다고 보고한다.</summary>
        public const int DefaultMaxIterations = 1000;

        private sealed class Plan
        {
            public readonly int N, P, K, ChunkRows, Chunks;
            public Plan(int n, int p, int k)
            {
                N = n; P = p; K = k;
                long maxChunks = Math.Clamp(4_000_000L / ((long)k * p + k + 2), 16, 4096);
                ChunkRows = (int)Math.Max(2048, (n + maxChunks - 1) / maxChunks);
                Chunks = (n + ChunkRows - 1) / ChunkRows;
            }
            public (int From, int To) Range(int chunk) => (chunk * ChunkRows, Math.Min(N, (chunk + 1) * ChunkRows));
        }

        private sealed class Workspace
        {
            public readonly Plan Plan;
            public readonly double[] Sums;
            public readonly int[] Counts;
            public readonly int[] Changed;
            public readonly double[] InertiaPart;
            public readonly double[] Dist;
            public Workspace(Plan plan)
            {
                Plan = plan;
                Sums = new double[(long)plan.Chunks * plan.K * plan.P];
                Counts = new int[plan.Chunks * plan.K];
                Changed = new int[plan.Chunks];
                InertiaPart = new double[plan.Chunks];
                Dist = new double[plan.N];
            }
        }

        private enum LloydOutcome { Converged, IterationLimit, TooFewDistinct }

        // double[,]는 행 우선 연속 메모리이므로 평탄한 span으로 본다(경계 검사 없이 z[i*p + j]).
        private static ReadOnlySpan<double> Flat(double[,] a)
            => System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
                ref System.Runtime.CompilerServices.Unsafe.As<byte, double>(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference((Array)a)),
                a.Length);

        private static double Dist2(ReadOnlySpan<double> a, int ao, ReadOnlySpan<double> b, int bo, int p)
        {
            double s = 0;
            for (int j = 0; j < p; j++)
            {
                double d = a[ao + j] - b[bo + j];
                s += d * d;
            }
            return s;
        }

        /// <summary>테스트 전용: 이 스레드에서 호출한 K-means가 실행한 청크 본문 수를 센다(취소 뒤 백그라운드 작업이 남지 않는지 확인).</summary>
        [ThreadStatic] internal static long[]? ChunkProbe;

        private static void ForChunks(int chunks, CancellationToken ct, Action<int> body)
        {
            ct.ThrowIfCancellationRequested();
            var probe = ChunkProbe;
            if (probe is null)
                Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, body);
            else
                Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, ch =>
                {
                    Interlocked.Increment(ref probe[0]);
                    body(ch);
                });
            ct.ThrowIfCancellationRequested();
        }

        private static double SumInOrder(double[] a, int offset, int stride, int count)
        {
            double s = 0;
            for (int i = 0; i < count; i++) s += a[offset + i * stride];
            return s;
        }

        // 가중(D²) 추첨: 청크 합으로 청크를 고르고 그 안에서 행을 고른다. 가중이 0인 행은 뽑지 않는다.
        private static int PickWeighted(double[] minD2, double[] chunkSum, Plan plan, double target)
        {
            for (int ch = 0; ch < plan.Chunks; ch++)
            {
                if (ch < plan.Chunks - 1 && target >= chunkSum[ch]) { target -= chunkSum[ch]; continue; }
                var (from, to) = plan.Range(ch);
                for (int i = from; i < to; i++)
                {
                    double d = minD2[i];
                    if (d <= 0) continue;
                    target -= d;
                    if (target < 0) return i;
                }
            }
            for (int i = minD2.Length - 1; i >= 0; i--) if (minD2[i] > 0) return i;
            return -1;
        }

        // 탐욕 k-means++(sklearn과 같은 2+ln k 후보): 후보 각각이 총 잠재력(최소 제곱거리 합)을 얼마나 줄이는지 보고 가장 좋은 후보를 쓴다.
        // 서로 다른 점이 k개 미만이면 null.
        private static double[]? Seeding(double[,] z, Plan plan, Random rng, CancellationToken ct)
        {
            int n = plan.N, p = plan.P, k = plan.K, chunks = plan.Chunks;
            var centers = new double[k * p];
            var minD2 = new double[n];
            int trials = 2 + (int)Math.Log(k);
            int first = rng.Next(n);
            Flat(z).Slice(first * p, p).CopyTo(centers);
            var chunkSum = new double[chunks];
            ForChunks(chunks, ct, ch =>
            {
                var zf = Flat(z);
                var (from, to) = plan.Range(ch);
                double s = 0;
                for (int i = from; i < to; i++)
                {
                    double d = Dist2(zf, i * p, centers, 0, p);
                    minD2[i] = d;
                    s += d;
                }
                chunkSum[ch] = s;
            });
            double potential = SumInOrder(chunkSum, 0, 1, chunks);
            var cand = new int[trials];
            var candPot = new double[chunks * trials];
            for (int c = 1; c < k; c++)
            {
                if (!(potential > 0)) return null;
                for (int t = 0; t < trials; t++)
                {
                    cand[t] = PickWeighted(minD2, chunkSum, plan, rng.NextDouble() * potential);
                    if (cand[t] < 0) return null;
                }
                ForChunks(chunks, ct, ch =>
                {
                    var zf = Flat(z);
                    var (from, to) = plan.Range(ch);
                    var local = new double[trials];
                    for (int i = from; i < to; i++)
                    {
                        double m = minD2[i];
                        for (int t = 0; t < trials; t++)
                            local[t] += Math.Min(m, Dist2(zf, i * p, zf, cand[t] * p, p));
                    }
                    for (int t = 0; t < trials; t++) candPot[ch * trials + t] = local[t];
                });
                int bestT = 0;
                double bestPot = double.PositiveInfinity;
                for (int t = 0; t < trials; t++)
                {
                    double s = SumInOrder(candPot, t, trials, chunks);
                    if (s < bestPot) { bestPot = s; bestT = t; }
                }
                int chosen = cand[bestT];
                Flat(z).Slice(chosen * p, p).CopyTo(centers.AsSpan(c * p, p));
                int cc = c;
                ForChunks(chunks, ct, ch =>
                {
                    var zf = Flat(z);
                    var (from, to) = plan.Range(ch);
                    double s = 0;
                    for (int i = from; i < to; i++)
                    {
                        double d = Dist2(zf, i * p, centers, cc * p, p);
                        if (d < minD2[i]) minD2[i] = d;
                        s += minD2[i];
                    }
                    chunkSum[ch] = s;
                });
                potential = SumInOrder(chunkSum, 0, 1, chunks);
            }
            return centers;
        }

        // 빈 군집 처리(sklearn과 같은 방식): 자기 중심에서 가장 먼 행을 빈 군집의 새 중심으로 옮긴다.
        // 그런 행이 없으면(모든 행이 자기 중심과 같음) 서로 다른 점이 k개 미만이라 false.
        private static bool Relocate(double[,] z, Workspace w, int[] assignment, double[] total, int[] cnt, CancellationToken ct)
        {
            int n = w.Plan.N, p = w.Plan.P, k = w.Plan.K;
            var zf = Flat(z);
            for (int c = 0; c < k; c++)
            {
                if (cnt[c] != 0) continue;
                int far;
                while (true)
                {
                    double best = 0;
                    far = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if ((i & 8191) == 0) ct.ThrowIfCancellationRequested();
                        if (w.Dist[i] > best) { best = w.Dist[i]; far = i; }
                    }
                    if (far < 0) return false;
                    if (cnt[assignment[far]] > 1) break;
                    w.Dist[far] = 0;
                }
                int old = assignment[far];
                for (int j = 0; j < p; j++)
                {
                    double v = zf[far * p + j];
                    total[old * p + j] -= v;
                    total[c * p + j] = v;
                }
                cnt[old]--;
                cnt[c] = 1;
                assignment[far] = c;
                w.Dist[far] = 0;
            }
            return true;
        }

        // Lloyd: 최근접 중심 배정(청크 병렬, 같은 패스에서 군집 합계 누적) → 평균으로 중심 갱신. 배정이 하나도 안 바뀌면 수렴.
        // 동점은 낮은 군집 번호. centers는 입력(시작 중심)이자 출력(마지막 배정의 평균). 수렴하면 Inertia는 그 배정의 WSS.
        private static (LloydOutcome Outcome, int Iterations, double Inertia) Lloyd(
            double[,] z, Workspace w, double[] centers, int[] assignment, int maxIts, CancellationToken ct)
        {
            var plan = w.Plan;
            int p = plan.P, k = plan.K, chunks = plan.Chunks, kp = k * p;
            Array.Fill(assignment, -1);
            var total = new double[kp];
            var cnt = new int[k];
            for (int it = 1; it <= maxIts; it++)
            {
                ForChunks(chunks, ct, ch =>
                {
                    var zf = Flat(z);
                    var (from, to) = plan.Range(ch);
                    long sOff = (long)ch * kp;
                    int cOff = ch * k;
                    Array.Clear(w.Sums, (int)sOff, kp);
                    Array.Clear(w.Counts, cOff, k);
                    int changed = 0;
                    double inertia = 0;
                    for (int i = from; i < to; i++)
                    {
                        int best = 0;
                        double bd = double.PositiveInfinity;
                        for (int c = 0; c < k; c++)
                        {
                            double d = Dist2(zf, i * p, centers, c * p, p);
                            if (d < bd) { bd = d; best = c; }
                        }
                        if (assignment[i] != best) { assignment[i] = best; changed++; }
                        w.Dist[i] = bd;
                        inertia += bd;
                        w.Counts[cOff + best]++;
                        int so = (int)sOff + best * p;
                        for (int j = 0; j < p; j++) w.Sums[so + j] += zf[i * p + j];
                    }
                    w.Changed[ch] = changed;
                    w.InertiaPart[ch] = inertia;
                });
                int changedTotal = 0;
                for (int ch = 0; ch < chunks; ch++) changedTotal += w.Changed[ch];
                if (changedTotal == 0)
                    return (LloydOutcome.Converged, it, SumInOrder(w.InertiaPart, 0, 1, chunks));

                Array.Clear(total);
                Array.Clear(cnt);
                for (int ch = 0; ch < chunks; ch++)
                {
                    int so = ch * kp;
                    for (int e = 0; e < kp; e++) total[e] += w.Sums[so + e];
                    for (int c = 0; c < k; c++) cnt[c] += w.Counts[ch * k + c];
                }
                if (!Relocate(z, w, assignment, total, cnt, ct))
                    return (LloydOutcome.TooFewDistinct, it, double.NaN);
                for (int c = 0; c < k; c++)
                {
                    double inv = 1.0 / cnt[c];
                    for (int j = 0; j < p; j++) centers[c * p + j] = total[c * p + j] * inv;
                }
            }
            return (LloydOutcome.IterationLimit, maxIts, double.NaN);
        }

        // 엘보 기준점(요청 k): 표본에서 최종 중심에 가장 가까운 군집으로 배정한 WSS — 다른 k와 같은 표본으로 비교.
        private static int[] Nearest(double[,] z, Summary stats, int k)
        {
            int n = z.GetLength(0), p = z.GetLength(1);
            var result = new int[n];
            for (int i = 0; i < n; i++)
            {
                double bestD = double.PositiveInfinity;
                for (int c = 0; c < k; c++)
                {
                    if (stats.Clusters[c].Size == 0) continue;
                    double d2 = 0;
                    for (int j = 0; j < p; j++) { double d = z[i, j] - stats.ScaledCenters[c, j]; d2 += d * d; }
                    if (d2 < bestD) { bestD = d2; result[i] = c; }
                }
            }
            return result;
        }
    }
}
