namespace NanumCsvViewer.Csv
{
    public enum CorrelationMethod
    {
        Pearson,
        Spearman
    }

    public readonly record struct CorrelationResult(
        CorrelationMethod Method, double Coefficient, double PValue, int SampleSize, string Interpretation);

    public readonly record struct IndependentTTestResult(
        string GroupA, string GroupB, double MeanA, double MeanB,
        double TStatistic, double DegreesOfFreedom, double PValue,
        double ConfidenceIntervalLow, double ConfidenceIntervalHigh,
        double EffectSize, string Interpretation);

    public readonly record struct PairedTTestResult(
        double MeanDifference, double TStatistic, double DegreesOfFreedom, double PValue,
        double ConfidenceIntervalLow, double ConfidenceIntervalHigh,
        double EffectSize, string Interpretation);

    public sealed class ChiSquareResult
    {
        public double Statistic { get; init; }
        public int DegreesOfFreedom { get; init; }
        public double PValue { get; init; }
        public IReadOnlyList<string> RowLabels { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ColumnLabels { get; init; } = Array.Empty<string>();
        public IReadOnlyList<double[]> Observed { get; init; } = Array.Empty<double[]>();
        public string Interpretation { get; init; } = string.Empty;
    }

    /// <summary>
    /// 기술/추론 통계. p값은 정규근사가 아닌 Student-t(정규화 불완전 베타)·카이제곱(정규화 불완전 감마)으로 계산.
    /// macOS CsvStatistics 이식. 경계조건 정확도는 단위테스트로 macOS 결과와 대조.
    /// </summary>
    public static class CsvStatistics
    {
        public static CorrelationResult Correlation(IReadOnlyList<(double X, double Y)> pairs, CorrelationMethod method)
        {
            var clean = pairs.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
            List<(double X, double Y)> transformed;
            if (method == CorrelationMethod.Spearman)
            {
                var rx = Ranks(clean.Select(p => p.X).ToList());
                var ry = Ranks(clean.Select(p => p.Y).ToList());
                transformed = new List<(double, double)>(clean.Count);
                for (int i = 0; i < clean.Count; i++) transformed.Add((rx[i], ry[i]));
            }
            else
            {
                transformed = clean;
            }

            double r = Pearson(transformed);
            int n = transformed.Count;
            double t = n > 2 && Math.Abs(r) < 1
                ? Math.Abs(r) * Math.Sqrt((n - 2) / Math.Max(1e-12, 1 - r * r))
                : double.PositiveInfinity;
            double p = n > 2 ? TwoSidedStudentTPValue(t, n - 2) : 1;
            return new CorrelationResult(method, r, p, n, Interpretation(p));
        }

        public static IndependentTTestResult IndependentTTest(string groupA, IReadOnlyList<double> a, string groupB, IReadOnlyList<double> b)
        {
            double meanA = Mean(a);
            double meanB = Mean(b);
            double varA = SampleVariance(a);
            double varB = SampleVariance(b);
            double nA = a.Count;
            double nB = b.Count;
            double standardError = Math.Sqrt(varA / Math.Max(1, nA) + varB / Math.Max(1, nB));
            double diff = meanA - meanB;
            double t = standardError == 0 ? 0 : diff / standardError;
            double numerator = Math.Pow(varA / Math.Max(1, nA) + varB / Math.Max(1, nB), 2);
            double denominator = Math.Pow(varA / Math.Max(1, nA), 2) / Math.Max(1, nA - 1)
                               + Math.Pow(varB / Math.Max(1, nB), 2) / Math.Max(1, nB - 1);
            double df = denominator == 0 ? Math.Max(1, nA + nB - 2) : numerator / denominator;
            double p = TwoSidedStudentTPValue(Math.Abs(t), df);
            double critical = StudentTCriticalTwoSided(0.05, df);
            double ciLow = diff - critical * standardError;
            double ciHigh = diff + critical * standardError;
            double pooled = Math.Sqrt(((nA - 1) * varA + (nB - 1) * varB) / Math.Max(1, nA + nB - 2));
            double effect = pooled == 0 ? 0 : diff / pooled;
            return new IndependentTTestResult(groupA, groupB, meanA, meanB, t, df, p, ciLow, ciHigh, effect, Interpretation(p));
        }

        public static PairedTTestResult PairedTTest(IReadOnlyList<double> before, IReadOnlyList<double> after)
        {
            int count = Math.Min(before.Count, after.Count);
            var differences = new double[count];
            for (int i = 0; i < count; i++) differences[i] = after[i] - before[i];
            double meanDiff = Mean(differences);
            double variance = SampleVariance(differences);
            double n = differences.Length;
            double standardError = Math.Sqrt(variance / Math.Max(1, n));
            double t = standardError == 0 ? 0 : meanDiff / standardError;
            double df = Math.Max(0, n - 1);
            double p = TwoSidedStudentTPValue(Math.Abs(t), df);
            double critical = StudentTCriticalTwoSided(0.05, df);
            double ciLow = meanDiff - critical * standardError;
            double ciHigh = meanDiff + critical * standardError;
            double sd = Math.Sqrt(variance);
            return new PairedTTestResult(meanDiff, t, df, p, ciLow, ciHigh, sd == 0 ? 0 : meanDiff / sd, Interpretation(p));
        }

        public static ChiSquareResult ChiSquare(IReadOnlyList<(string Row, string Column)> rows)
        {
            var rowLabels = rows.Select(r => r.Row).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var columnLabels = rows.Select(r => r.Column).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var observed = new double[rowLabels.Count][];
            for (int r = 0; r < rowLabels.Count; r++) observed[r] = new double[columnLabels.Count];

            foreach (var (rowVal, colVal) in rows)
            {
                int r = rowLabels.IndexOf(rowVal);
                int c = columnLabels.IndexOf(colVal);
                if (r >= 0 && c >= 0) observed[r][c] += 1;
            }

            var rowTotals = observed.Select(row => row.Sum()).ToArray();
            var columnTotals = new double[columnLabels.Count];
            for (int c = 0; c < columnLabels.Count; c++)
                for (int r = 0; r < rowLabels.Count; r++)
                    columnTotals[c] += observed[r][c];
            double total = rowTotals.Sum();

            double statistic = 0;
            if (total > 0)
            {
                for (int r = 0; r < rowLabels.Count; r++)
                {
                    for (int c = 0; c < columnLabels.Count; c++)
                    {
                        double expected = rowTotals[r] * columnTotals[c] / total;
                        if (expected > 0)
                            statistic += Math.Pow(observed[r][c] - expected, 2) / expected;
                    }
                }
            }
            int df = Math.Max(0, (rowLabels.Count - 1) * (columnLabels.Count - 1));
            double p = ChiSquareSurvival(statistic, df);
            return new ChiSquareResult
            {
                Statistic = statistic,
                DegreesOfFreedom = df,
                PValue = p,
                RowLabels = rowLabels,
                ColumnLabels = columnLabels,
                Observed = observed,
                Interpretation = Interpretation(p)
            };
        }

        // ---- 분포 함수 (검증용 공개 래퍼) ----

        /// <summary>자유도 df인 Student-t 양측 꼬리확률 P(|T| &gt; t). 검증·일반 용도 공개.</summary>
        public static double StudentTTwoSidedPValue(double t, double degreesOfFreedom)
            => TwoSidedStudentTPValue(t, degreesOfFreedom);

        /// <summary>자유도 df인 카이제곱 상측 꼬리확률 P(X &gt; statistic).</summary>
        public static double ChiSquareUpperTailProbability(double statistic, int degreesOfFreedom)
            => ChiSquareSurvival(statistic, degreesOfFreedom);

        // ---- 내부 통계 헬퍼 ----

        private static double Pearson(IReadOnlyList<(double X, double Y)> pairs)
        {
            if (pairs.Count <= 1) return 0;
            var xs = pairs.Select(p => p.X).ToArray();
            var ys = pairs.Select(p => p.Y).ToArray();
            double mx = Mean(xs);
            double my = Mean(ys);
            double numerator = 0, dx = 0, dy = 0;
            for (int i = 0; i < xs.Length; i++)
            {
                numerator += (xs[i] - mx) * (ys[i] - my);
                dx += (xs[i] - mx) * (xs[i] - mx);
                dy += (ys[i] - my) * (ys[i] - my);
            }
            double denominator = Math.Sqrt(dx * dy);
            return denominator == 0 ? 0 : numerator / denominator;
        }

        private static double[] Ranks(IReadOnlyList<double> values)
        {
            var sorted = values.Select((v, i) => (Value: v, Offset: i))
                .OrderBy(t => t.Value).ToArray();
            var output = new double[values.Count];
            int i2 = 0;
            while (i2 < sorted.Length)
            {
                int j = i2;
                while (j + 1 < sorted.Length && sorted[j + 1].Value == sorted[i2].Value) j++;
                double rank = ((i2 + 1) + (j + 1)) / 2.0;
                for (int k = i2; k <= j; k++) output[sorted[k].Offset] = rank;
                i2 = j + 1;
            }
            return output;
        }

        public static double Mean(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return 0;
            double sum = 0;
            foreach (double v in values) sum += v;
            return sum / values.Count;
        }

        private static double SampleVariance(IReadOnlyList<double> values)
        {
            if (values.Count <= 1) return 0;
            double m = Mean(values);
            double sum = 0;
            foreach (double v in values) sum += (v - m) * (v - m);
            return sum / (values.Count - 1);
        }

        private static double TwoSidedStudentTPValue(double t, double degreesOfFreedom)
        {
            if (!double.IsFinite(t) || degreesOfFreedom <= 0)
                return double.IsInfinity(t) ? 0 : 1;
            double x = degreesOfFreedom / (degreesOfFreedom + t * t);
            return ClampedProbability(RegularizedIncompleteBeta(degreesOfFreedom / 2, 0.5, x));
        }

        private static double StudentTCriticalTwoSided(double alpha, double degreesOfFreedom)
        {
            if (degreesOfFreedom <= 0) return 0;
            double low = 0, high = 1;
            while (TwoSidedStudentTPValue(high, degreesOfFreedom) > alpha && high < 1_000_000) high *= 2;
            for (int i = 0; i < 80; i++)
            {
                double mid = (low + high) / 2;
                if (TwoSidedStudentTPValue(mid, degreesOfFreedom) > alpha) low = mid;
                else high = mid;
            }
            return high;
        }

        private static double ChiSquareSurvival(double statistic, int degreesOfFreedom)
        {
            if (degreesOfFreedom <= 0) return 1;
            return ClampedProbability(RegularizedGammaQ(degreesOfFreedom / 2.0, Math.Max(0, statistic) / 2));
        }

        private static double RegularizedIncompleteBeta(double a, double b, double x)
        {
            if (a <= 0 || b <= 0) return double.NaN;
            if (x <= 0) return 0;
            if (x >= 1) return 1;

            double logTerm = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x);
            double bt = Math.Exp(logTerm);
            if (x < (a + 1) / (a + b + 2))
                return bt * BetaContinuedFraction(a, b, x) / a;
            return 1 - bt * BetaContinuedFraction(b, a, 1 - x) / b;
        }

        private static double BetaContinuedFraction(double a, double b, double x)
        {
            const int maxIterations = 200;
            const double epsilon = 3e-14;
            const double tiny = 1e-300;
            double qab = a + b;
            double qap = a + 1;
            double qam = a - 1;
            double c = 1;
            double d = 1 - qab * x / qap;
            if (Math.Abs(d) < tiny) d = tiny;
            d = 1 / d;
            double h = d;

            for (int m = 1; m <= maxIterations; m++)
            {
                int m2 = 2 * m;
                double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
                d = 1 + aa * d;
                if (Math.Abs(d) < tiny) d = tiny;
                c = 1 + aa / c;
                if (Math.Abs(c) < tiny) c = tiny;
                d = 1 / d;
                h *= d * c;

                aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
                d = 1 + aa * d;
                if (Math.Abs(d) < tiny) d = tiny;
                c = 1 + aa / c;
                if (Math.Abs(c) < tiny) c = tiny;
                d = 1 / d;
                double delta = d * c;
                h *= delta;
                if (Math.Abs(delta - 1) < epsilon) break;
            }
            return h;
        }

        private static double RegularizedGammaQ(double a, double x)
        {
            if (a <= 0) return double.NaN;
            if (x <= 0) return 1;
            if (x < a + 1) return 1 - RegularizedGammaPSeries(a, x);
            return RegularizedGammaQContinuedFraction(a, x);
        }

        private static double RegularizedGammaPSeries(double a, double x)
        {
            const int maxIterations = 1000;
            const double epsilon = 1e-14;
            double sum = 1 / a;
            double delta = sum;
            double ap = a;
            for (int i = 0; i < maxIterations; i++)
            {
                ap += 1;
                delta *= x / ap;
                sum += delta;
                if (Math.Abs(delta) < Math.Abs(sum) * epsilon) break;
            }
            return ClampedProbability(sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a)));
        }

        private static double RegularizedGammaQContinuedFraction(double a, double x)
        {
            const int maxIterations = 1000;
            const double epsilon = 1e-14;
            const double tiny = 1e-300;
            double b = x + 1 - a;
            double c = 1 / tiny;
            double d = 1 / Math.Max(Math.Abs(b), tiny);
            if (Math.Abs(b) < tiny) d = 1 / tiny;
            double h = d;
            for (int i = 1; i <= maxIterations; i++)
            {
                double an = -i * (i - a);
                b += 2;
                d = an * d + b;
                if (Math.Abs(d) < tiny) d = tiny;
                c = b + an / c;
                if (Math.Abs(c) < tiny) c = tiny;
                d = 1 / d;
                double delta = d * c;
                h *= delta;
                if (Math.Abs(delta - 1) < epsilon) break;
            }
            return ClampedProbability(Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h);
        }

        private static double ClampedProbability(double value)
        {
            if (!double.IsFinite(value))
                return double.IsNaN(value) ? 1 : (value < 0 ? 0 : 1);
            return Math.Max(0, Math.Min(1, value));
        }

        private static string Interpretation(double pValue)
            => pValue < 0.05 ? "통계적으로 유의함 (p < 0.05)" : "통계적으로 유의하지 않음 (p >= 0.05)";

        // Lanczos 근사 log-gamma (.NET에 lgamma 내장 부재).
        private static readonly double[] LanczosCoefficients =
        {
            676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7
        };

        private static double LogGamma(double x)
        {
            if (x < 0.5)
            {
                // 반사 공식: Γ(x)Γ(1-x) = π / sin(πx)
                return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
            }
            x -= 1;
            double a = 0.99999999999980993;
            double tt = x + 7.5;
            for (int i = 0; i < LanczosCoefficients.Length; i++)
                a += LanczosCoefficients[i] / (x + i + 1);
            return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(tt) - tt + Math.Log(a);
        }
    }
}
