
namespace NanumCsvViewer.Stats
{
    /// <summary>K-means 옵션. 시드는 1 이상이어야 실행마다 같은 결과가 나온다(ALGLIB 규약).</summary>
    public sealed record KMeansOptions
    {
        public int K { get; init; } = 3;
        /// <summary>재시작 횟수. 가장 작은 에너지(WSS)를 고른다.</summary>
        public int Restarts { get; init; } = 5;
        /// <summary>재시작당 최대 Lloyd 반복. 0이면 제한 없음.</summary>
        public int MaxIterations { get; init; }
        public int Seed { get; init; } = 1;
        public ScalingMethod Scaling { get; init; } = ScalingMethod.ZScore;
        /// <summary>k = 2..K 의 WSS(엘보)를 같이 계산한다.</summary>
        public bool Elbow { get; init; }
        /// <summary>실루엣을 계산할 최대 행 수. 넘으면 시드 고정 표본(근사).</summary>
        public int SilhouetteSampleSize { get; init; } = 5000;
        /// <summary>이 행 수를 넘으면 시드 고정 표본(이 크기)으로 ALGLIB 초기 중심을 찾고 전체 행 Lloyd로 정제한다.</summary>
        public int FullDataLimit { get; init; } = 200_000;
    }

    /// <summary>한 군집. 중심은 원래 단위, WSS는 알고리즘이 최적한 스케일 공간.</summary>
    public sealed record KMeansCluster(int Index, int Size, double[] CenterOriginal, double WithinSs);

    /// <summary>엘보 한 점. 실패한 k는 Succeeded=false, TotalWss=NaN.</summary>
    public sealed record ElbowPoint(int K, double TotalWss, bool Succeeded);

    /// <summary>K-means 결과. 배정은 0..K-1(ALGLIB 인덱스, 시드에 대해 결정적).</summary>
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
        public required int TerminationType { get; init; }
        public required int Iterations { get; init; }
        /// <summary>0이면 전체 행으로 ALGLIB 실행. 양수면 이 행 수의 시드 고정 표본으로 초기 중심을 찾고 전체 행 Lloyd로 정제.</summary>
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
    /// K-means(이슈 #27). ALGLIB clusterizer(유클리드, k-means++, 시드 고정)로 배정하고
    /// 중심·WSS는 그 배정의 평균으로 다시 계산한다 — sklearn inertia(같은 배정)와 같은 정의.
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

            var scaler = FeatureScaler.Fit(x, options.Scaling);
            var z = scaler.Transform(x);
            cancellation.ThrowIfCancellationRequested();

            // 큰 데이터: ALGLIB(k-means++·재시작)는 시드 고정 표본으로 초기 중심을 찾고, 전체 행에서
            // Lloyd 반복(배정 ↔ 평균)으로 정제한다. 전체 행 ALGLIB 실행은 수백만 행에서 수 분이 걸린다.
            int[] assignment;
            int iterations;
            int initRows = 0, refineIterations = 0;
            bool refineConverged = true;
            double[,] elbowData = z;
            if (n > options.FullDataLimit)
            {
                var sampleIdx = SampleRows(n, options.FullDataLimit, options.Seed);
                var zs = Rows(z, sampleIdx);
                var init = Run(zs, options.K, options.Restarts, options.MaxIterations, options.Seed, cancellation);
                if (init.Termination != 1)
                    throw new DesignMatrixException(FailureMessage(init.Termination, options.K));
                var centers = Means(zs, init.Assignment, options.K);
                (assignment, refineIterations, refineConverged) = Refine(z, centers, options.K, cancellation);
                iterations = init.Iterations;
                initRows = sampleIdx.Length;
                elbowData = zs;
            }
            else
            {
                var run = Run(z, options.K, options.Restarts, options.MaxIterations, options.Seed, cancellation);
                if (run.Termination != 1)
                    throw new DesignMatrixException(FailureMessage(run.Termination, options.K));
                assignment = run.Assignment;
                iterations = run.Iterations;
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
                TerminationType = 1,
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

        private readonly record struct RunResult(int[] Assignment, int Termination, int Iterations);

        // ALGLIB: 생성 → 점 설정(유클리드=2) → 재시작/반복 제한 → k-means++ → 시드 → 실행.
        // 종료코드가 양수가 아니면 배정을 결과로 쓰지 않는다.
        private static RunResult Run(double[,] z, int k, int restarts, int maxIts, int seed, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            int n = z.GetLength(0), p = z.GetLength(1);
            alglib.kmeansreport rep;
            // ALGLIB 호출 자체는 중간에 멈출 수 없다. 별도 작업에서 실행하고, 취소되면 호출 쪽은 즉시 빠져나온다.
            // 포기된 작업은 (초기화 표본 크기로 제한된) 계산을 마친 뒤 결과를 버린다.
            var work = Task.Run(() =>
            {
                alglib.clusterizercreate(out var state);
                alglib.clusterizersetpoints(state, z, n, p, 2);
                alglib.clusterizersetkmeanslimits(state, restarts, maxIts);
                alglib.clusterizersetkmeansinit(state, 2);
                alglib.clusterizersetseed(state, seed);
                alglib.clusterizerrunkmeans(state, k, out alglib.kmeansreport r);
                return r;
            });
            try
            {
                work.Wait(cancellation);
                rep = work.Result;
            }
            catch (OperationCanceledException)
            {
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
            catch (AggregateException agg) when (agg.InnerException is alglib.alglibexception ex)
            {
                throw new DesignMatrixException(string.IsNullOrWhiteSpace(ex.msg) ? "K-means failed." : ex.msg);
            }
            cancellation.ThrowIfCancellationRequested();
            if (rep.terminationtype != 1)
                return new RunResult(Array.Empty<int>(), rep.terminationtype, rep.iterationscount);
            if (rep.cidx is null || rep.cidx.Length < n)
                throw new DesignMatrixException("K-means returned no assignments.");
            var assignment = new int[n];
            Array.Copy(rep.cidx, assignment, n);
            for (int i = 0; i < n; i++)
                if ((uint)assignment[i] >= (uint)k)
                    throw new DesignMatrixException("K-means returned an incomplete assignment.");
            return new RunResult(assignment, rep.terminationtype, rep.iterationscount);
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
                if (run.Termination != 1)
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
            -5 => "K-means failed: Euclidean distance is required. No partition is reported.",
            -1 => "K-means failed: invalid k, restarts, or dimensions. No partition is reported.",
            _ => $"K-means failed (ALGLIB termination {termination}). No partition is reported.",
        };

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

        private static double[,] Means(double[,] z, int[] assignment, int k)
        {
            int n = z.GetLength(0), p = z.GetLength(1);
            var sum = new double[k, p];
            var count = new int[k];
            for (int i = 0; i < n; i++)
            {
                int c = assignment[i];
                count[c]++;
                for (int j = 0; j < p; j++) sum[c, j] += z[i, j];
            }
            for (int c = 0; c < k; c++)
                for (int j = 0; j < p; j++) sum[c, j] = count[c] == 0 ? double.NaN : sum[c, j] / count[c];
            return sum;
        }

        // 표준 Lloyd: 최근접 중심 배정(병렬) → 배정 평균으로 중심 갱신, 배정이 바뀌지 않으면 수렴.
        // 빈 군집은 이전 중심을 유지한다(요약에서 빈 군집으로 보고).
        private static (int[] Assignment, int Iterations, bool Converged) Refine(double[,] z, double[,] centers, int k, CancellationToken ct)
        {
            int n = z.GetLength(0), p = z.GetLength(1);
            var assignment = new int[n];
            for (int i = 0; i < n; i++) assignment[i] = -1;
            for (int it = 1; it <= MaxRefineIterations; it++)
            {
                ct.ThrowIfCancellationRequested();
                int changed = 0;
                var c0 = centers;
                Parallel.For(0, Math.Max(1, Environment.ProcessorCount), new ParallelOptions { CancellationToken = ct }, () => 0, (part, _, local) =>
                {
                    int parts = Math.Max(1, Environment.ProcessorCount);
                    int from = (int)((long)n * part / parts), to = (int)((long)n * (part + 1) / parts);
                    for (int i = from; i < to; i++)
                    {
                        int best = 0;
                        double bestD = double.PositiveInfinity;
                        for (int c = 0; c < k; c++)
                        {
                            if (double.IsNaN(c0[c, 0])) continue;
                            double d2 = 0;
                            for (int j = 0; j < p; j++) { double d = z[i, j] - c0[c, j]; d2 += d * d; }
                            if (d2 < bestD) { bestD = d2; best = c; }
                        }
                        if (assignment[i] != best) { assignment[i] = best; local++; }
                    }
                    return local;
                }, local => Interlocked.Add(ref changed, local));
                if (changed == 0) return (assignment, it, true);
                var next = Means(z, assignment, k);
                for (int c = 0; c < k; c++)
                    if (double.IsNaN(next[c, 0])) for (int j = 0; j < p; j++) next[c, j] = centers[c, j];
                centers = next;
            }
            return (assignment, MaxRefineIterations, false);
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
