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

    /// <summary>단일 수치 컬럼의 기술통계 묶음. 왜도·첨도는 Excel·SPSS와 같은 보정(adjusted) 공식.</summary>
    public sealed record DescriptiveStatisticsResult
    {
        public int Count { get; init; }
        public double Sum { get; init; }
        public double Mean { get; init; }
        /// <summary>표본 표준편차(n-1).</summary>
        public double StandardDeviation { get; init; }
        public double StandardError { get; init; }
        /// <summary>평균의 95% 신뢰구간(Student-t 기반).</summary>
        public double ConfidenceIntervalLow { get; init; }
        public double ConfidenceIntervalHigh { get; init; }
        public double Min { get; init; }
        public double Q1 { get; init; }
        public double Median { get; init; }
        public double Q3 { get; init; }
        public double Max { get; init; }
        public double Range { get; init; }
        public double InterquartileRange { get; init; }
        /// <summary>보정 왜도(G1). n&lt;3 또는 표준편차 0이면 NaN.</summary>
        public double Skewness { get; init; }
        /// <summary>보정 초과 첨도(G2, 정규분포=0). n&lt;4 또는 표준편차 0이면 NaN.</summary>
        public double ExcessKurtosis { get; init; }
        /// <summary>변동계수(SD/Mean). 평균 0이면 NaN.</summary>
        public double CoefficientOfVariation { get; init; }
        /// <summary>최빈값들(빈도 2 이상인 최대 빈도 값, 오름차순). 모두 고유하면 비어 있음.</summary>
        public IReadOnlyList<double> Modes { get; init; } = Array.Empty<double>();
        public int ModeFrequency { get; init; }
    }

    public readonly record struct FrequencyEntry(string Value, int Count, double Percent, double CumulativePercent);

    public sealed record FrequencyTableResult
    {
        public int TotalCount { get; init; }
        public int UniqueCount { get; init; }
        /// <summary>빈도 내림차순(동률은 값 오름차순) 정렬.</summary>
        public IReadOnlyList<FrequencyEntry> Entries { get; init; } = Array.Empty<FrequencyEntry>();
    }

    public sealed record AnovaGroup(string Name, int Count, double Mean, double StandardDeviation);

    public sealed record OneWayAnovaResult
    {
        public IReadOnlyList<AnovaGroup> Groups { get; init; } = Array.Empty<AnovaGroup>();
        public double SumOfSquaresBetween { get; init; }
        public double SumOfSquaresWithin { get; init; }
        public double FStatistic { get; init; }
        public int DfBetween { get; init; }
        public int DfWithin { get; init; }
        public double PValue { get; init; }
        /// <summary>효과 크기 η² = SSB/SST.</summary>
        public double EtaSquared { get; init; }
        public string Interpretation { get; init; } = string.Empty;
    }

    public sealed record ShapiroWilkResult(double W, double PValue, int SampleSize, string Interpretation);

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

        // ---- 기술통계 (이슈 #17) ----

        /// <summary>수치 목록의 기술통계. 값이 없으면 null.</summary>
        public static DescriptiveStatisticsResult? Describe(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return null;
            var sorted = values.ToArray();
            Array.Sort(sorted);
            int n = sorted.Length;
            double sum = 0;
            foreach (double v in sorted) sum += v;
            double mean = sum / n;
            double variance = SampleVariance(sorted);
            double sd = Math.Sqrt(variance);
            double se = sd / Math.Sqrt(n);
            double critical = n > 1 ? StudentTCriticalTwoSided(0.05, n - 1) : 0;

            // 보정 왜도 G1 · 보정 초과 첨도 G2 (Excel SKEW/KURT, SPSS와 동일 공식).
            double skew = double.NaN, kurt = double.NaN;
            if (sd > 0)
            {
                double s3 = 0, s4 = 0;
                foreach (double v in sorted)
                {
                    double z = (v - mean) / sd;
                    s3 += z * z * z;
                    s4 += z * z * z * z;
                }
                if (n >= 3) skew = n / ((double)(n - 1) * (n - 2)) * s3;
                if (n >= 4) kurt = (double)n * (n + 1) / ((n - 1.0) * (n - 2) * (n - 3)) * s4
                                   - 3.0 * (n - 1) * (n - 1) / ((n - 2.0) * (n - 3));
            }

            // 최빈값: 빈도 2 이상인 최대 빈도 값들만(전부 고유하면 무의미하므로 생략).
            var freq = new Dictionary<double, int>();
            foreach (double v in sorted) freq[v] = freq.TryGetValue(v, out int f) ? f + 1 : 1;
            int maxFreq = 0;
            foreach (int f in freq.Values) if (f > maxFreq) maxFreq = f;
            var modes = maxFreq >= 2
                ? freq.Where(kv => kv.Value == maxFreq).Select(kv => kv.Key).OrderBy(v => v).ToArray()
                : Array.Empty<double>();

            double q1 = CsvAnalytics.Percentile(sorted, 0.25);
            double q3 = CsvAnalytics.Percentile(sorted, 0.75);
            return new DescriptiveStatisticsResult
            {
                Count = n,
                Sum = sum,
                Mean = mean,
                StandardDeviation = sd,
                StandardError = se,
                ConfidenceIntervalLow = mean - critical * se,
                ConfidenceIntervalHigh = mean + critical * se,
                Min = sorted[0],
                Q1 = q1,
                Median = CsvAnalytics.Percentile(sorted, 0.5),
                Q3 = q3,
                Max = sorted[^1],
                Range = sorted[^1] - sorted[0],
                InterquartileRange = q3 - q1,
                Skewness = skew,
                ExcessKurtosis = kurt,
                CoefficientOfVariation = mean == 0 ? double.NaN : sd / mean,
                Modes = modes,
                ModeFrequency = modes.Length > 0 ? maxFreq : 0,
            };
        }

        /// <summary>값(트림 후) 빈도표. 빈도 내림차순, 동률은 값 오름차순. 백분율·누적백분율 포함.</summary>
        public static FrequencyTableResult FrequencyTable(IReadOnlyList<string> values)
        {
            var freq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string raw in values)
            {
                string v = raw.Trim();
                freq[v] = freq.TryGetValue(v, out int f) ? f + 1 : 1;
            }
            int total = values.Count;
            var entries = new List<FrequencyEntry>(freq.Count);
            double cumulative = 0;
            foreach (var kv in freq.OrderByDescending(kv => kv.Value)
                                   .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                double percent = total == 0 ? 0 : 100.0 * kv.Value / total;
                cumulative += percent;
                entries.Add(new FrequencyEntry(kv.Key, kv.Value, percent, Math.Min(100, cumulative)));
            }
            return new FrequencyTableResult { TotalCount = total, UniqueCount = freq.Count, Entries = entries };
        }

        /// <summary>일원배치 분산분석. 그룹이 2개 미만이거나 잔차 자유도가 없으면 null.</summary>
        public static OneWayAnovaResult? OneWayAnova(IReadOnlyList<(string Group, double Value)> observations)
        {
            var byGroup = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            foreach (var (g, v) in observations)
            {
                if (!double.IsFinite(v)) continue;
                if (!byGroup.TryGetValue(g, out var list)) { list = new List<double>(); byGroup[g] = list; }
                list.Add(v);
            }
            int k = byGroup.Count;
            int total = byGroup.Values.Sum(g => g.Count);
            if (k < 2 || total - k < 1) return null;

            double grandMean = byGroup.Values.SelectMany(g => g).Sum() / total;
            double ssb = 0, ssw = 0;
            var groups = new List<AnovaGroup>(k);
            foreach (var kv in byGroup.OrderByDescending(kv => kv.Value.Count)
                                      .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                double mean = Mean(kv.Value);
                ssb += kv.Value.Count * (mean - grandMean) * (mean - grandMean);
                foreach (double v in kv.Value) ssw += (v - mean) * (v - mean);
                groups.Add(new AnovaGroup(kv.Key, kv.Value.Count, mean, Math.Sqrt(SampleVariance(kv.Value))));
            }

            int dfB = k - 1, dfW = total - k;
            double msw = ssw / dfW;
            double f, p;
            if (msw == 0)
            {
                // 그룹 내 변동 0: 그룹 간 차이가 있으면 완전 분리(F=∞, p=0), 없으면 무차이(F=0, p=1).
                f = ssb == 0 ? 0 : double.PositiveInfinity;
                p = ssb == 0 ? 1 : 0;
            }
            else
            {
                f = (ssb / dfB) / msw;
                p = FSurvival(f, dfB, dfW);
            }
            double sst = ssb + ssw;
            return new OneWayAnovaResult
            {
                Groups = groups,
                SumOfSquaresBetween = ssb,
                SumOfSquaresWithin = ssw,
                FStatistic = f,
                DfBetween = dfB,
                DfWithin = dfW,
                PValue = p,
                EtaSquared = sst == 0 ? 0 : ssb / sst,
                Interpretation = Interpretation(p),
            };
        }

        /// <summary>
        /// Shapiro-Wilk 정규성 검정 (Royston 1995, AS R94 — R·scipy와 동일 알고리즘).
        /// n&lt;3 또는 모든 값이 동일하면 null. p값 근사는 n≤5000에서 신뢰 가능.
        /// </summary>
        public static ShapiroWilkResult? ShapiroWilk(IReadOnlyList<double> values)
        {
            int n = values.Count;
            if (n < 3) return null;
            var x = values.ToArray();
            Array.Sort(x);
            if (x[^1] - x[0] <= 0) return null; // 상수 컬럼: W 미정의

            // 기대 정규 순서통계량 m과 가중치 a (Royston 다항 보정).
            var m = new double[n];
            for (int i = 0; i < n; i++) m[i] = NormalQuantile((i + 1 - 0.375) / (n + 0.25));
            double ssm = 0;
            foreach (double v in m) ssm += v * v;

            var a = new double[n];
            double rsn = 1 / Math.Sqrt(n);
            if (n == 3)
            {
                a[0] = -Math.Sqrt(0.5);
                a[2] = Math.Sqrt(0.5);
            }
            else
            {
                double an = m[n - 1] / Math.Sqrt(ssm)
                    + rsn * (0.221157 + rsn * (-0.147981 + rsn * (-2.071190 + rsn * (4.434685 + rsn * -2.706056))));
                if (n > 5)
                {
                    double an1 = m[n - 2] / Math.Sqrt(ssm)
                        + rsn * (0.042981 + rsn * (-0.293762 + rsn * (-1.752461 + rsn * (5.682633 + rsn * -3.582633))));
                    double phi = (ssm - 2 * m[n - 1] * m[n - 1] - 2 * m[n - 2] * m[n - 2])
                               / (1 - 2 * an * an - 2 * an1 * an1);
                    double sqrtPhi = Math.Sqrt(phi);
                    for (int i = 2; i < n - 2; i++) a[i] = m[i] / sqrtPhi;
                    a[n - 1] = an; a[n - 2] = an1; a[0] = -an; a[1] = -an1;
                }
                else
                {
                    double phi = (ssm - 2 * m[n - 1] * m[n - 1]) / (1 - 2 * an * an);
                    double sqrtPhi = Math.Sqrt(phi);
                    for (int i = 1; i < n - 1; i++) a[i] = m[i] / sqrtPhi;
                    a[n - 1] = an; a[0] = -an;
                }
            }

            double meanX = Mean(x);
            double numerator = 0, denominator = 0;
            for (int i = 0; i < n; i++)
            {
                numerator += a[i] * x[i];
                denominator += (x[i] - meanX) * (x[i] - meanX);
            }
            double w = Math.Min(1, numerator * numerator / denominator);

            double p;
            if (n == 3)
            {
                p = ClampedProbability(6.0 / Math.PI * (Math.Asin(Math.Sqrt(w)) - Math.Asin(Math.Sqrt(0.75))));
            }
            else if (w >= 1)
            {
                p = 1;
            }
            else
            {
                double z;
                if (n <= 11)
                {
                    double gamma = 0.459 * n - 2.273;
                    double logArg = gamma - Math.Log(1 - w);
                    if (logArg <= 0) { z = double.PositiveInfinity; } // W가 근사 하한 밖 → 극단적 비정규
                    else
                    {
                        double wt = -Math.Log(logArg);
                        double mu = 0.5440 + n * (-0.39978 + n * (0.025054 + n * -0.0006714));
                        double sigma = Math.Exp(1.3822 + n * (-0.77857 + n * (0.062767 + n * -0.0020322)));
                        z = (wt - mu) / sigma;
                    }
                }
                else
                {
                    double u = Math.Log(n);
                    double wt = Math.Log(1 - w);
                    double mu = -1.5861 + u * (-0.31082 + u * (-0.083751 + u * 0.0038915));
                    double sigma = Math.Exp(-0.4803 + u * (-0.082676 + u * 0.0030302));
                    z = (wt - mu) / sigma;
                }
                p = NormalUpperTail(z);
            }
            return new ShapiroWilkResult(w, p, n, InterpretNormality(p));
        }

        private static string InterpretNormality(double pValue)
            => pValue < 0.05 ? "정규분포가 아니라고 볼 수 있음 (p < 0.05)" : "정규성을 기각할 수 없음 (p >= 0.05)";

        // ---- 분포 함수 (검증용 공개 래퍼) ----

        /// <summary>자유도 df인 Student-t 양측 꼬리확률 P(|T| &gt; t). 검증·일반 용도 공개.</summary>
        public static double StudentTTwoSidedPValue(double t, double degreesOfFreedom)
            => TwoSidedStudentTPValue(t, degreesOfFreedom);

        /// <summary>자유도 (df1, df2)인 F분포 상측 꼬리확률 P(F &gt; f).</summary>
        public static double FDistributionUpperTailProbability(double f, int df1, int df2)
            => FSurvival(f, df1, df2);

        /// <summary>표준정규 분위수 Φ⁻¹(p) (Acklam 근사, 상대오차 ~1e-9).</summary>
        public static double StandardNormalQuantile(double p) => NormalQuantile(p);

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

        // F분포 생존함수: P(F > f) = I_x(d2/2, d1/2), x = d2/(d2 + d1·f). 기존 불완전 베타 재사용.
        private static double FSurvival(double f, int df1, int df2)
        {
            if (df1 <= 0 || df2 <= 0) return 1;
            if (double.IsPositiveInfinity(f)) return 0;
            if (!double.IsFinite(f) || f <= 0) return 1;
            double x = df2 / (df2 + df1 * f);
            return ClampedProbability(RegularizedIncompleteBeta(df2 / 2.0, df1 / 2.0, x));
        }

        // 표준정규 상측 꼬리확률: P(Z > z) = ½·P(χ²₁ > z²) (z ≥ 0). χ² 생존함수 재사용.
        private static double NormalUpperTail(double z)
        {
            if (double.IsNaN(z)) return 1;
            if (double.IsPositiveInfinity(z)) return 0;
            if (double.IsNegativeInfinity(z)) return 1;
            double half = 0.5 * ClampedProbability(RegularizedGammaQ(0.5, z * z / 2));
            return z >= 0 ? half : 1 - half;
        }

        // 표준정규 분위수 Φ⁻¹(p): Acklam 근사(상대오차 ~1.15e-9). Shapiro-Wilk 기대 순서통계량용.
        private static double NormalQuantile(double p)
        {
            if (p <= 0) return double.NegativeInfinity;
            if (p >= 1) return double.PositiveInfinity;

            // 유리 다항 근사 계수 (P. J. Acklam, 2003).
            ReadOnlySpan<double> ca = stackalloc double[]
            {
                -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00
            };
            ReadOnlySpan<double> cb = stackalloc double[]
            {
                -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                6.680131188771972e+01, -1.328068155288572e+01
            };
            ReadOnlySpan<double> cc = stackalloc double[]
            {
                -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00
            };
            ReadOnlySpan<double> cd = stackalloc double[]
            {
                7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00
            };
            const double pLow = 0.02425;

            if (p < pLow)
            {
                double q = Math.Sqrt(-2 * Math.Log(p));
                return (((((cc[0] * q + cc[1]) * q + cc[2]) * q + cc[3]) * q + cc[4]) * q + cc[5])
                     / ((((cd[0] * q + cd[1]) * q + cd[2]) * q + cd[3]) * q + 1);
            }
            if (p > 1 - pLow)
            {
                double q = Math.Sqrt(-2 * Math.Log(1 - p));
                return -(((((cc[0] * q + cc[1]) * q + cc[2]) * q + cc[3]) * q + cc[4]) * q + cc[5])
                      / ((((cd[0] * q + cd[1]) * q + cd[2]) * q + cd[3]) * q + 1);
            }
            {
                double q = p - 0.5;
                double r = q * q;
                return (((((ca[0] * r + ca[1]) * r + ca[2]) * r + ca[3]) * r + ca[4]) * r + ca[5]) * q
                     / (((((cb[0] * r + cb[1]) * r + cb[2]) * r + cb[3]) * r + cb[4]) * r + 1);
            }
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
