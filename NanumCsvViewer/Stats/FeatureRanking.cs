using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    // 단변량 필터 순위(이슈 #27). 수치 목표 → Pearson r·F-회귀(sklearn f_regression, center=True,
    // force_finite=True). 범주 목표 → 수치 특성은 ANOVA F(sklearn f_classif), 범주 특성은 분할표
    // 독립성 χ²(scipy.stats.chi2_contingency, correction=False). sklearn chi2(원-핫 카운트)와 다르다.
    // q값은 Benjamini–Hochberg(statsmodels multipletests method='fdr_bh'). 상호작용·중복은 보지 않는다.

    public enum RankingTest { PearsonF, AnovaF, ChiSquare }

    public enum RankNote { None, Constant, PerfectCorrelation, PerfectSeparation, LowExpected, SingleLevel }

    /// <summary>한 특성의 단변량 점수. Score는 Pearson r, ANOVA F, 또는 χ².</summary>
    public readonly record struct UnivariateScore(
        double Score,
        double Statistic,
        double PValue,
        int Df1,
        int Df2,
        RankNote Note,
        double MinExpected);

    public sealed class RankedFeature
    {
        public required string Name { get; init; }
        public required int ColumnIndex { get; init; }
        public required RankingTest Test { get; init; }
        public required double Score { get; init; }
        public required double Statistic { get; init; }
        public required double PValue { get; init; }
        /// <summary>정의된 p값만으로 계산한 BH q. 정의되지 않은 검정은 NaN.</summary>
        public required double QValue { get; init; }
        public required int Df1 { get; init; }
        public required int Df2 { get; init; }
        public required RankNote Note { get; init; }
        public required double MinExpected { get; init; }
    }

    public sealed class FeatureRankingResult
    {
        /// <summary>p 오름차순, 같으면 |Score| 내림차순, 그래도 같으면 컬럼 인덱스.</summary>
        public required IReadOnlyList<RankedFeature> Ranked { get; init; }
        public required long RowsRead { get; init; }
        public required long RowsUsed { get; init; }
        public required long RowsDropped { get; init; }
        public required TargetKind TargetKind { get; init; }
        public required string TargetName { get; init; }
        /// <summary>q값 가족에 넣은(p가 유한인) 검정 수.</summary>
        public required int TestsInFamily { get; init; }
        /// <summary>수치 목표라 점수에서 뺀 범주 특성.</summary>
        public required IReadOnlyList<string> SkippedCategorical { get; init; }
    }

    public static class FeatureRanking
    {
        /// <summary>
        /// Benjamini–Hochberg q값. 정렬은 p 오름차순, q_i = min(1, min_{j≥i} p_(j)·m/(j+1)).
        /// 같은 p는 순서가 달라도 q가 같다(우측 누적 최솟값). NaN p는 q도 NaN이며 가족 크기에는 포함된다 —
        /// 정의된 p만 넣으려면 호출 전에 걸러야 한다.
        /// </summary>
        public static double[] BenjaminiHochberg(IReadOnlyList<double> pValues)
        {
            int m = pValues.Count;
            var q = new double[m];
            if (m == 0) return q;
            var order = new int[m];
            for (int i = 0; i < m; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                double pa = pValues[a], pb = pValues[b];
                bool na = double.IsNaN(pa), nb = double.IsNaN(pb);
                if (na || nb) return na.CompareTo(nb);
                return pa.CompareTo(pb);
            });

            var raw = new double[m];
            for (int i = 0; i < m; i++)
            {
                double p = pValues[order[i]];
                raw[i] = double.IsNaN(p) ? double.NaN : p * m / (i + 1);
            }
            double acc = double.PositiveInfinity;
            for (int i = m - 1; i >= 0; i--)
            {
                if (double.IsNaN(raw[i])) { q[order[i]] = double.NaN; continue; }
                acc = Math.Min(acc, raw[i]);
                q[order[i]] = acc > 1 ? 1 : acc;
            }
            return q;
        }

        /// <summary>Pearson r과 F-회귀. 상수 특성·목표는 sklearn force_finite처럼 F=0, p=1, r=0.</summary>
        public static UnivariateScore PearsonF(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
        {
            int n = y.Length;
            if (x.Length != n) throw new ArgumentException("Length mismatch.", nameof(x));
            if (n < 3) throw new DesignMatrixException("F-regression needs at least 3 complete rows.");

            double meanX = 0, meanY = 0;
            for (int i = 0; i < n; i++) { meanX += x[i]; meanY += y[i]; }
            meanX /= n; meanY /= n;
            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - meanX, dy = y[i] - meanY;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }
            if (!(sxx > 0) || !(syy > 0))
                return new UnivariateScore(0, 0, 1, 1, n - 2, RankNote.Constant, double.NaN);

            double r = sxy / Math.Sqrt(sxx * syy);
            if (r > 1) r = 1;
            else if (r < -1) r = -1;
            double r2 = r * r;
            if (r2 >= 1 - 1e-15)
                return new UnivariateScore(r, double.PositiveInfinity, 0, 1, n - 2, RankNote.PerfectCorrelation, double.NaN);
            double f = r2 / (1 - r2) * (n - 2);
            return new UnivariateScore(r, f, Dist.FUpper(f, 1, n - 2), 1, n - 2, RankNote.None, double.NaN);
        }

        /// <summary>클래스별 일원분산분석 F(sklearn f_classif / f_oneway). 상수 특성은 F·p = NaN.</summary>
        public static UnivariateScore AnovaF(ReadOnlySpan<double> x, ReadOnlySpan<int> groups, int classCount)
        {
            int n = x.Length;
            if (groups.Length != n) throw new ArgumentException("Length mismatch.", nameof(groups));
            if (classCount < 2) throw new DesignMatrixException("ANOVA needs at least 2 classes.");
            var count = new int[classCount];
            var sum = new double[classCount];
            double ssAll = 0, sumAll = 0;
            for (int i = 0; i < n; i++)
            {
                int g = groups[i];
                if (g < 0 || g >= classCount) throw new ArgumentException("A group index is outside 0..K-1.", nameof(groups));
                count[g]++;
                sum[g] += x[i];
                sumAll += x[i];
                ssAll += x[i] * x[i];
            }
            for (int k = 0; k < classCount; k++)
                if (count[k] == 0) throw new DesignMatrixException("A class has no rows for ANOVA.");
            int dfb = classCount - 1;
            int dfw = n - classCount;
            if (dfw < 1) throw new DesignMatrixException("ANOVA needs more rows than classes.");

            double ssb = -sumAll * sumAll / n;
            for (int k = 0; k < classCount; k++) ssb += sum[k] * sum[k] / count[k];
            double sstot = ssAll - sumAll * sumAll / n;
            double ssw = sstot - ssb;
            double scale = Math.Max(1, Math.Abs(sstot));
            if (ssb < 0 && ssb > -1e-10 * scale) ssb = 0;
            if (ssw < 0 && ssw > -1e-10 * scale) ssw = 0;
            double msb = ssb / dfb;
            double msw = ssw / dfw;
            if (msw == 0)
            {
                if (msb == 0) return new UnivariateScore(double.NaN, double.NaN, double.NaN, dfb, dfw, RankNote.Constant, double.NaN);
                return new UnivariateScore(double.PositiveInfinity, double.PositiveInfinity, 0, dfb, dfw, RankNote.PerfectSeparation, double.NaN);
            }
            double f = msb / msw;
            return new UnivariateScore(f, f, Dist.FUpper(f, dfb, dfw), dfb, dfw, RankNote.None, double.NaN);
        }

        /// <summary>
        /// 분할표 독립성 χ²(Yates 보정 없음). df = (행−1)(열−1). 기대빈도 &lt; 5이면 LowExpected.
        /// 빈 주변합은 기대빈도 0으로 나눗셈이 깨지므로 허용하지 않는다.
        /// </summary>
        public static UnivariateScore ChiSquare(int[,] table)
        {
            int rows = table.GetLength(0), cols = table.GetLength(1);
            if (rows < 1 || cols < 1) throw new ArgumentException("Table is empty.", nameof(table));
            long n = 0;
            var rowSum = new long[rows];
            var colSum = new long[cols];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                {
                    int v = table[i, j];
                    if (v < 0) throw new ArgumentException("Counts must be non-negative.", nameof(table));
                    rowSum[i] += v;
                    colSum[j] += v;
                    n += v;
                }
            for (int i = 0; i < rows; i++)
                if (rowSum[i] == 0) throw new ArgumentException("A table row sums to 0.", nameof(table));
            for (int j = 0; j < cols; j++)
                if (colSum[j] == 0) throw new ArgumentException("A table column sums to 0.", nameof(table));

            int df = (rows - 1) * (cols - 1);
            if (df <= 0)
                return new UnivariateScore(0, 0, 1, Math.Max(df, 0), 0, RankNote.SingleLevel, double.NaN);

            double chi = 0, minExp = double.PositiveInfinity;
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                {
                    double exp = (double)rowSum[i] * colSum[j] / n;
                    if (exp < minExp) minExp = exp;
                    double d = table[i, j] - exp;
                    chi += d * d / exp;
                }
            var note = minExp < 5 ? RankNote.LowExpected : RankNote.None;
            return new UnivariateScore(chi, chi, Dist.ChiSquareUpper(chi, df), df, 0, note, minExp);
        }

        /// <summary>
        /// 뷰를 한 번 순회해 목록별 삭제한 뒤 특성별로 점수를 매긴다. 수치 목표의 범주 특성은 건너뛴다.
        /// </summary>
        public static FeatureRankingResult Rank(
            IReadOnlyList<string[]> rows,
            IReadOnlyList<string> headers,
            IReadOnlyList<int> featureColumns,
            Func<int, VariableKind> kindOf,
            int targetColumn,
            TargetKind targetKind,
            FeatureMatrixOptions? options = null,
            CancellationToken cancellation = default)
        {
            options ??= new FeatureMatrixOptions();
            if (featureColumns.Count == 0) throw new DesignMatrixException("Select at least one feature column.");
            if (targetKind is not (TargetKind.Numeric or TargetKind.Categorical))
                throw new ArgumentException("Target kind must be numeric or categorical.", nameof(targetKind));
            if (targetColumn < 0 || targetColumn >= headers.Count)
                throw new DesignMatrixException("The target column is not in the table.");
            if (featureColumns.Contains(targetColumn))
                throw new DesignMatrixException("The target column cannot also be a feature.");

            int f = featureColumns.Count;
            var kinds = new VariableKind[f];
            for (int k = 0; k < f; k++) kinds[k] = kindOf(featureColumns[k]);
            var num = new List<double>?[f];
            var codes = new List<int>?[f];
            var levelMaps = new Dictionary<string, int>?[f];
            var levelNames = new List<string>?[f];
            for (int k = 0; k < f; k++)
            {
                if (kinds[k] == VariableKind.Numeric) num[k] = new List<double>();
                else
                {
                    codes[k] = new List<int>();
                    levelMaps[k] = new Dictionary<string, int>(StringComparer.Ordinal);
                    levelNames[k] = new List<string>();
                }
            }
            var yNum = new List<double>();
            var yCodes = new List<int>();
            var yMap = new Dictionary<string, int>(StringComparer.Ordinal);
            var yNames = new List<string>();
            long dropped = 0;
            long bytesPerRow = 8L + kinds.Sum(k => k == VariableKind.Numeric ? 8L : 4L);
            int used = 0;

            for (int r = 0; r < rows.Count; r++)
            {
                if ((r & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var row = rows[r];
                if (!TryRead(row, featureColumns, kinds, targetColumn, targetKind,
                        out var nums, out var cats, out double tv, out string? tl))
                {
                    dropped++;
                    continue;
                }
                if ((long)(used + 1) * bytesPerRow > options.MemoryBudgetBytes) throw new AnalysisMemoryLimitException();
                for (int k = 0; k < f; k++)
                {
                    if (kinds[k] == VariableKind.Numeric) { num[k]!.Add(nums![k]); continue; }
                    string label = cats![k];
                    if (!levelMaps[k]!.TryGetValue(label, out int code))
                    {
                        code = levelMaps[k]!.Count;
                        if (code >= options.MaxLevels)
                            throw new DesignMatrixException($"'{headers[featureColumns[k]]}' has more than {options.MaxLevels:N0} levels.");
                        levelMaps[k]![label] = code;
                        levelNames[k]!.Add(label);
                    }
                    codes[k]!.Add(code);
                }
                if (targetKind == TargetKind.Numeric) yNum.Add(tv);
                else
                {
                    if (!yMap.TryGetValue(tl!, out int code))
                    {
                        code = yMap.Count;
                        if (code >= options.MaxLevels)
                            throw new DesignMatrixException($"Target '{headers[targetColumn]}' has more than {options.MaxLevels:N0} classes.");
                        yMap[tl!] = code;
                        yNames.Add(tl!);
                    }
                    yCodes.Add(code);
                }
                used++;
            }
            cancellation.ThrowIfCancellationRequested();
            if (used == 0)
                throw new DesignMatrixException("No complete rows: every row has a missing or non-numeric value in the selected columns.");

            var skipped = new List<string>();
            var pending = new List<(int Col, string Name, RankingTest Test, UnivariateScore Score)>();

            if (targetKind == TargetKind.Numeric)
            {
                var y = yNum.ToArray();
                if (PearsonF(y, y).Note == RankNote.Constant)
                    throw new DesignMatrixException("The numeric target does not vary in the complete rows.");
                bool any = false;
                for (int k = 0; k < f; k++)
                {
                    if (kinds[k] != VariableKind.Numeric)
                    {
                        skipped.Add(headers[featureColumns[k]]);
                        continue;
                    }
                    any = true;
                    pending.Add((featureColumns[k], headers[featureColumns[k]], RankingTest.PearsonF, PearsonF(num[k]!.ToArray(), y)));
                }
                if (!any)
                    throw new DesignMatrixException("A numeric target is scored with Pearson/F-regression, which needs at least one numeric feature. Categorical features are not scored for a numeric target.");
            }
            else
            {
                var sortedClasses = StatValue.SortLevels(yNames);
                if (sortedClasses.Count < 2)
                    throw new DesignMatrixException("The target has only one class in the complete rows.");
                var classPos = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < sortedClasses.Count; i++) classPos[sortedClasses[i]] = i;
                var classOfCode = yNames.Select(l => classPos[l]).ToArray();
                var groups = yCodes.Select(c => classOfCode[c]).ToArray();
                int kClasses = sortedClasses.Count;

                bool any = false;
                for (int k = 0; k < f; k++)
                {
                    string name = headers[featureColumns[k]];
                    if (kinds[k] == VariableKind.Numeric)
                    {
                        any = true;
                        pending.Add((featureColumns[k], name, RankingTest.AnovaF, AnovaF(num[k]!.ToArray(), groups, kClasses)));
                        continue;
                    }
                    var sortedLevels = StatValue.SortLevels(levelNames[k]!);
                    if (sortedLevels.Count < 2)
                    {
                        any = true;
                        pending.Add((featureColumns[k], name, RankingTest.ChiSquare,
                            new UnivariateScore(0, 0, 1, 0, 0, RankNote.SingleLevel, double.NaN)));
                        continue;
                    }
                    var levelPos = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < sortedLevels.Count; i++) levelPos[sortedLevels[i]] = i;
                    var levelOfCode = levelNames[k]!.Select(l => levelPos[l]).ToArray();
                    var table = new int[sortedLevels.Count, kClasses];
                    var colCodes = codes[k]!;
                    for (int i = 0; i < used; i++) table[levelOfCode[colCodes[i]], groups[i]]++;
                    any = true;
                    pending.Add((featureColumns[k], name, RankingTest.ChiSquare, ChiSquare(table)));
                }
                if (!any) throw new DesignMatrixException("Select at least one feature column.");
            }

            var finite = new List<int>();
            for (int i = 0; i < pending.Count; i++)
                if (!double.IsNaN(pending[i].Score.PValue)) finite.Add(i);
            var pFinite = finite.Select(i => pending[i].Score.PValue).ToArray();
            var qFinite = BenjaminiHochberg(pFinite);
            var qOf = new double[pending.Count];
            for (int i = 0; i < qOf.Length; i++) qOf[i] = double.NaN;
            for (int i = 0; i < finite.Count; i++) qOf[finite[i]] = qFinite[i];

            var ranked = new List<RankedFeature>(pending.Count);
            for (int i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                ranked.Add(new RankedFeature
                {
                    Name = item.Name,
                    ColumnIndex = item.Col,
                    Test = item.Test,
                    Score = item.Score.Score,
                    Statistic = item.Score.Statistic,
                    PValue = item.Score.PValue,
                    QValue = qOf[i],
                    Df1 = item.Score.Df1,
                    Df2 = item.Score.Df2,
                    Note = item.Score.Note,
                    MinExpected = item.Score.MinExpected,
                });
            }
            ranked.Sort(CompareRank);

            return new FeatureRankingResult
            {
                Ranked = ranked,
                RowsRead = rows.Count,
                RowsUsed = used,
                RowsDropped = dropped,
                TargetKind = targetKind,
                TargetName = headers[targetColumn],
                TestsInFamily = finite.Count,
                SkippedCategorical = skipped,
            };
        }

        private static int CompareRank(RankedFeature a, RankedFeature b)
        {
            int c = CompareP(a.PValue, b.PValue);
            if (c != 0) return c;
            c = CompareAbsDesc(a.Score, b.Score);
            if (c != 0) return c;
            return a.ColumnIndex.CompareTo(b.ColumnIndex);
        }

        private static int CompareP(double a, double b)
        {
            bool na = double.IsNaN(a), nb = double.IsNaN(b);
            if (na || nb) return na.CompareTo(nb);
            return a.CompareTo(b);
        }

        private static int CompareAbsDesc(double a, double b)
        {
            bool na = double.IsNaN(a), nb = double.IsNaN(b);
            if (na || nb) return na.CompareTo(nb);
            return Math.Abs(b).CompareTo(Math.Abs(a));
        }

        private static bool TryRead(
            string[] row,
            IReadOnlyList<int> featureColumns,
            VariableKind[] kinds,
            int targetColumn,
            TargetKind targetKind,
            out double[]? nums,
            out string[]? cats,
            out double targetValue,
            out string? targetLabel)
        {
            nums = new double[featureColumns.Count];
            cats = new string[featureColumns.Count];
            targetValue = 0;
            targetLabel = null;
            for (int k = 0; k < featureColumns.Count; k++)
            {
                int c = featureColumns[k];
                if (c >= row.Length) return false;
                if (kinds[k] == VariableKind.Numeric)
                {
                    if (!StatValue.TryNumber(row[c], out nums[k])) return false;
                }
                else if (StatValue.IsMissing(row[c])) return false;
                else cats[k] = row[c].Trim();
            }
            if (targetColumn >= row.Length) return false;
            if (targetKind == TargetKind.Numeric) return StatValue.TryNumber(row[targetColumn], out targetValue);
            if (StatValue.IsMissing(row[targetColumn])) return false;
            targetLabel = row[targetColumn].Trim();
            return true;
        }
    }
}
