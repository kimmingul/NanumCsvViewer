using System.Globalization;

namespace NanumCsvViewer.Stats
{
    // 일원 반복측정 분산분석(넓은 형식: 행 = 피험자, 열 = 조건). F·df·p는 statsmodels AnovaRM과 같다.
    // SS는 상수 이동에 불변이므로 첫 관측값으로 중심화한 한 패스 공식(뷰는 한 번만 순회).
    //
    // Mauchly·GG·HF는 교과서 공식(정규직교 Helmert 대비). 조건이 2개면 구형성은 자명하다.
    //   Σ = Cᵀ S C,  W = det(Σ) / (tr(Σ)/p)^p,  p = k−1
    //   d = 1 − (2p² + p + 2) / (6p(n−1)),  χ² = −(n−1)·d·ln(W),  df = p(p+1)/2 − 1
    //   ε_GG = tr(Σ)² / (p · ‖Σ‖²_F)
    //   ε_HF = (n·p·ε_GG − 2) / (p·(n−1 − p·ε_GG)),  1 초과(또는 분모 ≤ 0)면 1로 자른다.

    /// <summary>조건(반복 측정 열)의 평균·표본 표준편차.</summary>
    public sealed record RmCondition(string Name, double Mean, double StandardDeviation);

    /// <summary>
    /// 일원 반복측정 ANOVA. 구형성이 자명(k=2)하거나 공분산이 특이이면 Mauchly 통계량은 NaN.
    /// HuynhFeldt는 1로 자른 값이고, HuynhFeldtCapped가 그 사실을 알린다. 추정이 음수면 HuynhFeldtDefined가 false.
    /// </summary>
    public sealed record RmAnovaResult(
        int N,
        int K,
        double SsSubjects,
        double SsConditions,
        double SsError,
        double MeanSquareConditions,
        double MeanSquareError,
        double F,
        double P,
        double PartialEtaSquared,
        bool SphericityTrivial,
        bool MauchlyDefined,
        double MauchlyW,
        double MauchlyChiSquare,
        double MauchlyDf,
        double MauchlyP,
        double GreenhouseGeisser,
        double GgDfNum,
        double GgDfDen,
        double GgP,
        bool HuynhFeldtDefined,
        double HuynhFeldt,
        bool HuynhFeldtCapped,
        double HfDfNum,
        double HfDfDen,
        double HfP,
        IReadOnlyList<RmCondition> Conditions,
        long RowsRead,
        long RowsUsed,
        long RowsDropped);

    /// <summary>일원 반복측정 분산분석. 목록별 삭제(한 열이라도 결측이면 그 행 제외).</summary>
    public static class RepeatedMeasuresAnova
    {
        /// <summary>조건 수 상한. k=32·수백만 행의 공분산 누적(O(nk²))이 수십 초를 넘지 않게 한다.</summary>
        public const int MaxConditions = 32;

        private const int CancelEvery = 4096;

        public static RmAnovaResult Fit(IReadOnlyList<IReadOnlyList<double>> subjects, CancellationToken cancellation = default)
        {
            if (subjects is null) throw new ArgumentNullException(nameof(subjects));
            if (subjects.Count < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            int k = subjects[0].Count;
            if (k < 2) throw new DesignMatrixException("Repeated-measures ANOVA needs at least 2 conditions.");
            if (k > MaxConditions)
                throw new DesignMatrixException($"Repeated-measures ANOVA accepts at most {MaxConditions} conditions.");
            var names = Enumerable.Range(1, k).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
            return FitCore(subjects.Select(s =>
            {
                if (s.Count != k) throw new ArgumentException("Every subject must have the same number of conditions.", nameof(subjects));
                return s as double[] ?? s.ToArray();
            }), k, names, subjects.Count, subjects.Count, 0, cancellation);
        }

        public static RmAnovaResult Fit(
            IEnumerable<string[]> rows, IReadOnlyList<int> columns, IReadOnlyList<string> names, CancellationToken cancellation = default)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            if (columns is null) throw new ArgumentNullException(nameof(columns));
            int k = columns.Count;
            if (k < 2) throw new DesignMatrixException("Repeated-measures ANOVA needs at least 2 conditions.");
            if (k > MaxConditions)
                throw new DesignMatrixException($"Repeated-measures ANOVA accepts at most {MaxConditions} conditions.");

            var sum = new double[k];
            var cross = new double[k, k];
            double grand = 0, sumRowSq = 0, shift = double.NaN;
            var buf = new double[k];
            long read = 0, used = 0, dropped = 0;
            foreach (var row in rows)
            {
                if ((++read & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                bool ok = true;
                for (int j = 0; j < k; j++)
                {
                    if (!TryCell(row, columns[j], out buf[j])) { ok = false; break; }
                }
                if (!ok) { dropped++; continue; }
                if (double.IsNaN(shift)) shift = buf[0];
                Accumulate(buf, shift, sum, cross, ref grand, ref sumRowSq);
                used++;
            }
            if (used < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            return Finish((int)used, k, names, sum, cross, grand, sumRowSq, shift, read, used, dropped);
        }

        private static RmAnovaResult FitCore(
            IEnumerable<double[]> subjects, int k, IReadOnlyList<string> names,
            long read, long used, long dropped, CancellationToken ct)
        {
            var sum = new double[k];
            var cross = new double[k, k];
            double grand = 0, sumRowSq = 0, shift = double.NaN;
            int n = 0;
            foreach (var row in subjects)
            {
                if ((n & (CancelEvery - 1)) == 0) ct.ThrowIfCancellationRequested();
                if (double.IsNaN(shift)) shift = row[0];
                Accumulate(row, shift, sum, cross, ref grand, ref sumRowSq);
                n++;
            }
            return Finish(n, k, names, sum, cross, grand, sumRowSq, shift, read, used, dropped);
        }

        private static void Accumulate(double[] row, double shift, double[] sum, double[,] cross, ref double grand, ref double sumRowSq)
        {
            int k = sum.Length;
            double rowSum = 0;
            for (int j = 0; j < k; j++)
            {
                double v = row[j] - shift;
                sum[j] += v;
                rowSum += v;
                for (int l = 0; l <= j; l++)
                    cross[j, l] += v * (row[l] - shift);
            }
            grand += rowSum;
            sumRowSq += rowSum * rowSum;
        }

        private static RmAnovaResult Finish(
            int n, int k, IReadOnlyList<string> names, double[] sum, double[,] cross,
            double grand, double sumRowSq, double shift, long read, long used, long dropped)
        {
            double cf = grand * grand / (n * (double)k);
            double sumSq = 0, ssCondRaw = 0;
            var conds = new RmCondition[k];
            for (int j = 0; j < k; j++)
            {
                sumSq += cross[j, j];
                ssCondRaw += sum[j] * sum[j];
                double mean = sum[j] / n + shift;
                double var = (cross[j, j] - sum[j] * sum[j] / n) / (n - 1.0);
                if (var < 0 && var > -1e-8) var = 0;
                string name = names is not null && j < names.Count ? names[j] : (j + 1).ToString(CultureInfo.InvariantCulture);
                conds[j] = new RmCondition(name, mean, Math.Sqrt(Math.Max(0, var)));
            }
            double ssTotal = sumSq - cf;
            double ssSubjects = sumRowSq / k - cf;
            double ssConditions = ssCondRaw / n - cf;
            double ssError = ssTotal - ssSubjects - ssConditions;
            // 한 패스 공식의 자리수 소멸. 정수 자료에서는 0이고, 부동소수에서는 아주 작은 음수가 나올 수 있다.
            if (ssError < 0 && ssError > -1e-8 * Math.Max(1, Math.Abs(ssTotal))) ssError = 0;
            if (ssConditions < 0 && ssConditions > -1e-8 * Math.Max(1, Math.Abs(ssTotal))) ssConditions = 0;

            double dfC = k - 1.0;
            double dfE = (n - 1.0) * dfC;
            double msC = ssConditions / dfC;
            double msE = dfE > 0 ? ssError / dfE : double.NaN;
            double f, p;
            if (!(ssError > 0))
            {
                // 잔차가 0. 조건 제곱합도 0이면 변동이 없어 F를 정의하지 않고, 조건만 다르면 F=∞·p=0.
                if (!(ssConditions > 0)) { f = double.NaN; p = double.NaN; }
                else { f = double.PositiveInfinity; p = 0; }
            }
            else
            {
                f = msC / msE;
                p = Dist.FUpper(f, dfC, dfE);
            }
            double eta = ssConditions + ssError > 0 ? ssConditions / (ssConditions + ssError) : double.NaN;

            if (k == 2)
            {
                return new RmAnovaResult(
                    n, k, ssSubjects, ssConditions, ssError, msC, msE, f, p, eta,
                    true, false, double.NaN, double.NaN, double.NaN, double.NaN,
                    1, dfC, dfE, p, true, 1, false, dfC, dfE, p, conds, read, used, dropped);
            }

            var cov = SampleCovariance(n, k, sum, cross);
            int pDim = k - 1;
            var sigma = ContrastCovariance(cov, Helmert(k));
            double trace = 0, frobenius = 0;
            for (int a = 0; a < pDim; a++)
            {
                trace += sigma[a, a];
                for (int b = 0; b < pDim; b++) frobenius += sigma[a, b] * sigma[a, b];
            }

            bool ggDefined = trace > 0 && frobenius > 0;
            double epsGg = ggDefined ? trace * trace / (pDim * frobenius) : double.NaN;
            // 수치 오차로 1을 아주 조금 넘는 경우는 1로 둔다. GG의 이론 상한은 1.
            if (epsGg > 1) epsGg = 1;
            double ggDf1 = epsGg * dfC, ggDf2 = epsGg * dfE;
            double pGg = ggDefined && !double.IsNaN(f) ? Dist.FUpper(f, ggDf1, ggDf2) : double.NaN;

            bool hfDefined = false;
            bool hfCapped = false;
            double epsHf = double.NaN;
            if (ggDefined)
            {
                double num = n * (double)pDim * epsGg - 2;
                double den = pDim * (n - 1.0 - pDim * epsGg);
                if (!(den > 1e-12) || num / den > 1)
                {
                    // 분모가 0 이하이거나 추정이 1을 넘으면 Lecoutre처럼 1로 자른다.
                    epsHf = 1;
                    hfDefined = true;
                    hfCapped = true;
                }
                else if (num / den > 0)
                {
                    epsHf = num / den;
                    hfDefined = true;
                }
            }
            double hfDf1 = hfDefined ? epsHf * dfC : double.NaN;
            double hfDf2 = hfDefined ? epsHf * dfE : double.NaN;
            double pHf = hfDefined && !double.IsNaN(f) ? Dist.FUpper(f, hfDf1, hfDf2) : double.NaN;

            bool mauchly = false;
            double w = double.NaN, chi = double.NaN, dfW = pDim * (pDim + 1) / 2.0 - 1, pW = double.NaN;
            if (trace > 0 && dfW > 0)
            {
                double det = Determinant(sigma);
                double meanEig = trace / pDim;
                w = det / Math.Pow(meanEig, pDim);
                // 특이 공분산은 det이 0 또는 수치 오차로 음수. ln(W)를 정의하지 않는다.
                if (w > 0 && double.IsFinite(w))
                {
                    double d = 1 - (2.0 * pDim * pDim + pDim + 2) / (6.0 * pDim * (n - 1));
                    chi = -(n - 1.0) * d * Math.Log(w);
                    pW = Dist.ChiSquareUpper(chi, dfW);
                    mauchly = double.IsFinite(chi);
                }
            }

            return new RmAnovaResult(
                n, k, ssSubjects, ssConditions, ssError, msC, msE, f, p, eta,
                false, mauchly, mauchly ? w : double.NaN, mauchly ? chi : double.NaN, dfW, mauchly ? pW : double.NaN,
                epsGg, ggDf1, ggDf2, pGg, hfDefined, epsHf, hfCapped, hfDf1, hfDf2, pHf,
                conds, read, used, dropped);
        }

        private static double[,] SampleCovariance(int n, int k, double[] sum, double[,] cross)
        {
            var s = new double[k, k];
            double dn = n;
            for (int a = 0; a < k; a++)
            {
                for (int b = 0; b <= a; b++)
                {
                    double c = (cross[a, b] - sum[a] * sum[b] / dn) / (n - 1.0);
                    s[a, b] = c;
                    s[b, a] = c;
                }
            }
            return s;
        }

        /// <summary>정규직교 Helmert 대비(k × (k−1)). 열은 상수 벡터와 직교하고 단위 길이.</summary>
        private static double[,] Helmert(int k)
        {
            var c = new double[k, k - 1];
            for (int j = 1; j < k; j++)
            {
                for (int i = 0; i < j; i++) c[i, j - 1] = 1;
                c[j, j - 1] = -j;
                double norm = 0;
                for (int i = 0; i < k; i++) norm += c[i, j - 1] * c[i, j - 1];
                norm = Math.Sqrt(norm);
                for (int i = 0; i < k; i++) c[i, j - 1] /= norm;
            }
            return c;
        }

        private static double[,] ContrastCovariance(double[,] cov, double[,] contrast)
        {
            int k = cov.GetLength(0);
            int p = contrast.GetLength(1);
            var tmp = new double[k, p];
            for (int i = 0; i < k; i++)
                for (int j = 0; j < p; j++)
                {
                    double s = 0;
                    for (int t = 0; t < k; t++) s += cov[i, t] * contrast[t, j];
                    tmp[i, j] = s;
                }
            var sigma = new double[p, p];
            for (int a = 0; a < p; a++)
                for (int b = 0; b < p; b++)
                {
                    double s = 0;
                    for (int t = 0; t < k; t++) s += contrast[t, a] * tmp[t, b];
                    sigma[a, b] = s;
                }
            return sigma;
        }

        /// <summary>부분 피벗 LU 행렬식. 대칭이 아니어도 되고, 피벗이 0이면 0을 돌려준다.</summary>
        private static double Determinant(double[,] matrix)
        {
            int p = matrix.GetLength(0);
            var a = (double[,])matrix.Clone();
            double det = 1;
            double scale = 0;
            for (int i = 0; i < p; i++)
                for (int j = 0; j < p; j++) scale = Math.Max(scale, Math.Abs(a[i, j]));
            if (scale == 0) return 0;
            for (int col = 0; col < p; col++)
            {
                int pivot = col;
                double best = Math.Abs(a[col, col]);
                for (int r = col + 1; r < p; r++)
                {
                    double v = Math.Abs(a[r, col]);
                    if (v > best) { best = v; pivot = r; }
                }
                if (best <= 1e-14 * scale) return 0;
                if (pivot != col)
                {
                    for (int c = col; c < p; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                    det = -det;
                }
                det *= a[col, col];
                for (int r = col + 1; r < p; r++)
                {
                    double f = a[r, col] / a[col, col];
                    for (int c = col + 1; c < p; c++) a[r, c] -= f * a[col, c];
                }
            }
            return det;
        }

        private static bool TryCell(string[] row, int col, out double value)
        {
            value = 0;
            return row is not null && (uint)col < (uint)row.Length && StatValue.TryNumber(row[col], out value);
        }
    }
}
