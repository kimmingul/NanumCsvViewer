namespace NanumCsvViewer.Stats
{
    /// <summary>
    /// (가중) 최소제곱 적합 결과. 별칭(선형 종속) 열은 R처럼 계수 NaN으로 표시한다.
    /// XtWXInverse = (XᵀWX)⁻¹ — 별칭 열의 행·열은 NaN, 공분산 = 분산추정 × 이 행렬.
    /// WeightedRss = Σ wᵢ(yᵢ − xᵢβ)².
    /// </summary>
    public sealed record LeastSquaresFit(
        double[] Beta,
        double[,] XtWXInverse,
        bool[] Aliased,
        int Rank,
        double WeightedRss);

    /// <summary>
    /// 정규방정식 기반 (가중) 최소제곱. n이 크고 p가 작은(수백 이하) 표 데이터 회귀에 맞춘 구조:
    /// XᵀWX·XᵀWy를 행 파티션 병렬로 누적(O(np²))하고, 대각 허용오차 피벗 촐레스키로 별칭 열을 찾은 뒤
    /// 남은 블록을 ALGLIB spdmatrixcholeskyinverse로 역행렬화한다. GLM·IRLS(GLzM) 공용.
    /// 상수 열(절편)이 있으면 나머지 열과 y를 가중 평균으로 중심화해 풀고 원래 모수로 되돌린다 —
    /// 별칭 판정이 예측변수의 원점(예: 1,000,000 근처 값)에 좌우되지 않고 정규방정식의 조건수도 줄어든다.
    /// </summary>
    public static class LeastSquares
    {
        /// <summary>상대 대각 허용오차: 앞선 열로 설명되고 남은 분산 비율이 이보다 작으면 별칭.</summary>
        public const double AliasTolerance = 1e-10;

        /// <param name="columns">x의 일부 열만 쓸 때 그 인덱스(복사 없이 부분모형 적합 — Type II 등). 결과 배열은 이 순서.</param>
        public static LeastSquaresFit Fit(double[,] x, double[] y, double[]? weights = null, CancellationToken cancellation = default,
            IReadOnlyList<int>? columns = null)
        {
            int n = x.GetLength(0);
            int[] cols = columns?.ToArray() ?? Enumerable.Range(0, x.GetLength(1)).ToArray();
            int p = cols.Length;
            if (y.Length != n) throw new ArgumentException("Row count mismatch.", nameof(y));
            if (weights is not null && weights.Length != n) throw new ArgumentException("Row count mismatch.", nameof(weights));

            // 가중 평균·상수 열 판정(O(np)). 첫 번째 0이 아닌 상수 열을 절편으로 보고 나머지를 중심화한다.
            var (means, constant, sumW, yMean) = Moments(x, cols, y, weights, cancellation);
            int c0 = -1;
            for (int j = 0; j < p && sumW > 0; j++)
                if (constant[j] && x[FirstWeightedRow(weights, n), cols[j]] != 0) { c0 = j; break; }

            // 작업 열: 절편을 뺀 열(중심화) 또는 전체(중심화 없음).
            int[] work = c0 < 0 ? Enumerable.Range(0, p).ToArray() : Enumerable.Range(0, p).Where(j => j != c0).ToArray();
            int q = work.Length;
            var workCols = work.Select(j => cols[j]).ToArray();
            var centers = c0 < 0 ? new double[q] : work.Select(j => means[j]).ToArray();
            double yCenter = c0 < 0 ? 0 : yMean;
            var (xtwx, xtwy) = Accumulate(x, workCols, centers, y, yCenter, weights, cancellation);

            // 피벗 없는 순차 촐레스키 + 허용오차: 열 j의 잔여 대각이 원래(중심화) 대각 대비 작으면 별칭으로 표시하고 건너뛴다.
            var aliased = new bool[p];
            var kept = new List<int>(q);
            var l = new double[q, q];
            for (int j = 0; j < q; j++)
            {
                double orig = xtwx[j, j];
                double d = orig;
                for (int k = 0; k < kept.Count; k++) { int kk = kept[k]; d -= l[j, kk] * l[j, kk]; }
                if (!(orig > 0) || d <= AliasTolerance * orig)
                {
                    aliased[work[j]] = true;
                    continue;
                }
                double ljj = Math.Sqrt(d);
                l[j, j] = ljj;
                for (int i = j + 1; i < q; i++)
                {
                    double s = xtwx[i, j];
                    for (int k = 0; k < kept.Count; k++) { int kk = kept[k]; s -= l[i, kk] * l[j, kk]; }
                    l[i, j] = s / ljj;
                }
                kept.Add(j);
            }

            int r = kept.Count;
            var beta = Enumerable.Repeat(double.NaN, p).ToArray();
            var inverse = new double[p, p];
            for (int i = 0; i < p; i++) for (int j = 0; j < p; j++) inverse[i, j] = double.NaN;
            if (r == 0 && c0 < 0) return new LeastSquaresFit(beta, inverse, aliased, 0, double.NaN);

            // 남은 블록 A_r의 역행렬(ALGLIB) → β = A_r⁻¹ b_r
            var a = new double[r, r];
            if (r > 0)
            {
                for (int i = 0; i < r; i++) for (int j = 0; j < r; j++) a[i, j] = xtwx[kept[i], kept[j]];
                if (!alglib.spdmatrixcholesky(a, r, false))
                    throw new InvalidOperationException("The model matrix is numerically singular.");
                alglib.spdmatrixcholeskyinverse(a, r, false, out _);
                // ALGLIB는 하삼각만 채운다 → 대칭 복원
                for (int i = 0; i < r; i++) for (int j = i + 1; j < r; j++) a[i, j] = a[j, i];
            }
            for (int i = 0; i < r; i++)
            {
                double s = 0;
                for (int j = 0; j < r; j++) s += a[i, j] * xtwy[kept[j]];
                beta[work[kept[i]]] = s;
                for (int j = 0; j < r; j++) inverse[work[kept[i]], work[kept[j]]] = a[i, j];
            }

            if (c0 >= 0)
            {
                // X = [c·1, X̃ + 1mᵀ] = Z·T, Z = [1, X̃] (X̃ 가중 중심화 → ZᵀWZ 블록 대각).
                // (XᵀWX)⁻¹ = T⁻¹ diag(1/Σw, A⁻¹) T⁻ᵀ,  T⁻¹ = [[1/c, −mᵀ/c], [0, I]].
                double cval = x[FirstWeightedRow(weights, n), cols[c0]];
                var m = kept.Select(k => centers[k]).ToArray();
                double b0 = yMean;
                for (int i = 0; i < r; i++) b0 -= beta[work[kept[i]]] * m[i];
                beta[c0] = b0 / cval;
                double quad = 0;
                for (int j = 0; j < r; j++)
                {
                    double ma = 0;
                    for (int i = 0; i < r; i++) ma += m[i] * a[i, j];
                    inverse[c0, work[kept[j]]] = inverse[work[kept[j]], c0] = -ma / cval;
                    quad += ma * m[j];
                }
                inverse[c0, c0] = (1.0 / sumW + quad) / (cval * cval);
                r++;
            }

            double rss = WeightedRss(x, cols, y, weights, beta, aliased, cancellation);
            return new LeastSquaresFit(beta, inverse, aliased, r, rss);
        }

        private static int FirstWeightedRow(double[]? w, int n)
        {
            if (w is null) return 0;
            for (int i = 0; i < n; i++) if (w[i] != 0) return i;
            return 0;
        }

        private static (double[] Means, bool[] Constant, double SumW, double YMean) Moments(
            double[,] x, int[] cols, double[] y, double[]? w, CancellationToken ct)
        {
            int n = x.GetLength(0), p = cols.Length;
            var sum = new double[p];
            var first = new double[p];
            var constant = Enumerable.Repeat(true, p).ToArray();
            double sumW = 0, sumY = 0;
            bool seen = false;
            for (int i = 0; i < n; i++)
            {
                if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
                double wi = w?[i] ?? 1.0;
                if (wi == 0) continue;
                sumW += wi;
                sumY += wi * y[i];
                for (int j = 0; j < p; j++)
                {
                    double v = x[i, cols[j]];
                    sum[j] += wi * v;
                    if (!seen) first[j] = v;
                    else if (constant[j] && v != first[j]) constant[j] = false;
                }
                seen = true;
            }
            var means = new double[p];
            if (sumW > 0) for (int j = 0; j < p; j++) means[j] = constant[j] ? first[j] : sum[j] / sumW;
            return (means, constant, sumW, sumW > 0 ? sumY / sumW : 0);
        }

        /// <summary>적합값 xᵢβ (별칭 열 제외).</summary>
        public static double[] Predict(double[,] x, double[] beta)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            var fitted = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int j = 0; j < p; j++) if (!double.IsNaN(beta[j])) s += x[i, j] * beta[j];
                fitted[i] = s;
            }
            return fitted;
        }

        private static double WeightedRss(double[,] x, int[] cols, double[] y, double[]? w, double[] beta, bool[] aliased, CancellationToken ct)
        {
            int n = x.GetLength(0), p = cols.Length;
            double rss = 0;
            for (int i = 0; i < n; i++)
            {
                if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
                double f = 0;
                for (int j = 0; j < p; j++) if (!aliased[j]) f += x[i, cols[j]] * beta[j];
                double e = y[i] - f;
                rss += (w?[i] ?? 1.0) * e * e;
            }
            return rss;
        }

        private static (double[,] XtWX, double[] XtWy) Accumulate(double[,] x, int[] cols, double[] centers, double[] y, double yCenter,
            double[]? w, CancellationToken ct)
        {
            int n = x.GetLength(0), p = cols.Length;
            int parts = Math.Clamp(n / 50_000, 1, Environment.ProcessorCount);
            var partials = new (double[,] A, double[] B)[parts];
            Parallel.For(0, parts, new ParallelOptions { CancellationToken = ct }, part =>
            {
                int from = (int)((long)n * part / parts), to = (int)((long)n * (part + 1) / parts);
                var a = new double[p, p];
                var b = new double[p];
                var row = new double[p];
                for (int i = from; i < to; i++)
                {
                    if (((i - from) & 16383) == 0) ct.ThrowIfCancellationRequested();
                    double wi = w?[i] ?? 1.0;
                    if (wi == 0) continue;
                    for (int j = 0; j < p; j++) row[j] = x[i, cols[j]] - centers[j];
                    double wy = wi * (y[i] - yCenter);
                    for (int j = 0; j < p; j++)
                    {
                        double wxj = wi * row[j];
                        if (wxj == 0) continue;
                        b[j] += row[j] * wy;
                        for (int k = 0; k <= j; k++) a[j, k] += wxj * row[k];
                    }
                }
                partials[part] = (a, b);
            });
            var xtwx = new double[p, p];
            var xtwy = new double[p];
            foreach (var (a, b) in partials)
            {
                for (int j = 0; j < p; j++)
                {
                    xtwy[j] += b[j];
                    for (int k = 0; k <= j; k++) xtwx[j, k] += a[j, k];
                }
            }
            for (int j = 0; j < p; j++) for (int k = 0; k < j; k++) xtwx[k, j] = xtwx[j, k];
            return (xtwx, xtwy);
        }
    }

    /// <summary>분포 함수 래퍼(ALGLIB). 잘못된 자유도·NaN은 NaN을 돌려준다(ALGLIB 예외를 UI로 새지 않게).</summary>
    public static class Dist
    {
        /// <summary>Student t 양측 p = P(|T| ≥ |t|).</summary>
        public static double TTwoSided(double t, double df)
        {
            if (double.IsNaN(t) || !(df > 0)) return double.NaN;
            if (double.IsInfinity(t)) return 0;
            int k = (int)Math.Round(df);
            // ALGLIB studenttdistribution은 정수 자유도. 비정수(Welch 등)는 불완전 베타로 계산.
            if (Math.Abs(df - k) < 1e-12 && k >= 1)
                return Math.Clamp(2 * alglib.studenttdistribution(k, -Math.Abs(t)), 0, 1);
            double xb = df / (df + t * t);
            return Math.Clamp(alglib.incompletebeta(df / 2, 0.5, xb), 0, 1);
        }

        /// <summary>Student t 분위수(양측 신뢰구간용 상측 1−α/2).</summary>
        public static double TQuantile(double p, double df)
        {
            if (!(df > 0) || !(p > 0 && p < 1)) return double.NaN;
            int k = (int)Math.Round(df);
            if (Math.Abs(df - k) < 1e-12 && k >= 1) return alglib.invstudenttdistribution(k, p);
            // 비정수 자유도: 이분법
            double lo = -1e3, hi = 1e3;
            for (int it = 0; it < 200; it++)
            {
                double mid = (lo + hi) / 2;
                double cdf = mid < 0 ? TTwoSided(mid, df) / 2 : 1 - TTwoSided(mid, df) / 2;
                if (cdf < p) lo = mid; else hi = mid;
            }
            return (lo + hi) / 2;
        }

        /// <summary>F 상측 p = P(F ≥ f).</summary>
        public static double FUpper(double f, double df1, double df2)
        {
            if (double.IsNaN(f) || !(df1 > 0) || !(df2 > 0)) return double.NaN;
            if (f <= 0) return 1;
            if (double.IsPositiveInfinity(f)) return 0;
            // 비정수 자유도(GG 보정 등)도 지원: P(F ≥ f) = I_{df2/(df2+df1 f)}(df2/2, df1/2)
            return Math.Clamp(alglib.incompletebeta(df2 / 2, df1 / 2, df2 / (df2 + df1 * f)), 0, 1);
        }

        /// <summary>카이제곱 상측 p = P(X ≥ x).</summary>
        public static double ChiSquareUpper(double x, double df)
        {
            if (double.IsNaN(x) || !(df > 0)) return double.NaN;
            if (x <= 0) return 1;
            if (double.IsPositiveInfinity(x)) return 0;
            return Math.Clamp(alglib.incompletegammac(df / 2, x / 2), 0, 1);
        }

        /// <summary>표준정규 양측 p = 2·P(Z ≥ |z|).</summary>
        public static double NormalTwoSided(double z)
            => double.IsNaN(z) ? double.NaN : Math.Clamp(2 * alglib.normaldistribution(-Math.Abs(z)), 0, 1);

        public static double NormalCdf(double z) => alglib.normaldistribution(z);

        public static double NormalQuantile(double p)
            => p > 0 && p < 1 ? alglib.invnormaldistribution(p) : double.NaN;
    }
}
