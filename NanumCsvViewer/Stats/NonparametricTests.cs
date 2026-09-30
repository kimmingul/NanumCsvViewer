using System.Globalization;
using System.Numerics;

namespace NanumCsvViewer.Stats
{
    // 비모수 검정(이슈 #27). 순위 통계는 scipy 1.15와 같은 정의로 계산한다.
    // ALGLIB Mann-Whitney/Wilcoxon은 p를 [0.0001, 0.25]로 자르고 소표본을 거부해 scipy와 맞지 않으므로
    // 직접 구현하고, 정규·이항 꼬리만 ALGLIB(Dist·binomialdistribution)를 쓴다.
    //
    // Mann-Whitney 점근: U₁ = R₁ − n₁(n₁+1)/2, z = (max(U₁,U₂) − μ − ½) / σ, p = min(1, 2·Φ̄(z)).
    //   σ² = n₁n₂/12 · ((N+1) − Σ(t³−t)/(N(N−1))), scipy mannwhitneyu(method='asymptotic', use_continuity=True).
    // Wilcoxon: 영 차이 제거(zero_method='wilcox'). 양측 통계량 W = min(T⁺, T⁻).
    //   n≤50·동점 없음 → 정확 분포, 그 외 → 동점·연속 보정 정규근사(method='asymptotic', correction=True).
    // 부호 검정: p=0.5 양측 이항은 scipy.stats.binomtest와 같이 2·F(min(k, n−k)) (상한 1).

    /// <summary>Mann-Whitney U. U1은 그룹 1의 scipy 통계량. 분산 0(전부 동점)이면 Z·p는 NaN.</summary>
    public sealed record MannWhitneyResult(
        double U1,
        double U2,
        double Z,
        double PAsymptotic,
        double PExact,
        bool ExactComputed,
        bool HasTies,
        bool VarianceZero,
        double RankBiserial,
        int N1,
        int N2,
        double Median1,
        double Median2,
        string Group1,
        string Group2,
        long OtherGroupsExcluded,
        long RowsRead,
        long RowsUsed,
        long RowsDropped);

    /// <summary>Wilcoxon 부호순위. W는 양측 scipy 통계량 min(T⁺, T⁻). Z는 T⁺ 방향의 연속보정 표준화 통계량.</summary>
    public sealed record WilcoxonResult(
        double W,
        double TPlus,
        double TMinus,
        double Z,
        double EffectSizeR,
        double P,
        bool UsedExact,
        bool HasTies,
        int N,
        int ZerosDropped,
        long RowsRead,
        long RowsUsed,
        long RowsDropped);

    /// <summary>부호 검정. Positive/Negative는 영이 아닌 차이(또는 중앙값과의 차이) 개수. P는 양측 정확 이항.</summary>
    public sealed record SignTestResult(
        int Positive,
        int Negative,
        int Zeros,
        double P,
        bool Paired,
        double HypothesizedMedian,
        long RowsRead,
        long RowsUsed,
        long RowsDropped);

    /// <summary>Kruskal-Wallis 그룹 요약. MeanRank는 전체 평균 순위.</summary>
    public sealed record KruskalGroup(string Name, int N, double Median, double MeanRank);

    /// <summary>Dunn 사후 비교. Z = (R̄ᵢ − R̄ⱼ) / se, PBonferroni = min(1, P · 비교 수).</summary>
    public sealed record DunnPair(int IndexI, int IndexJ, double Z, double P, double PBonferroni);

    /// <summary>Kruskal-Wallis H(동점 보정)와 Dunn 사후 비교 전체.</summary>
    public sealed record KruskalWallisResult(
        double H,
        int Df,
        double P,
        double EpsilonSquared,
        bool SmallSample,
        IReadOnlyList<KruskalGroup> Groups,
        IReadOnlyList<DunnPair> Dunn,
        long Comparisons,
        long RowsRead,
        long RowsUsed,
        long RowsDropped);

    /// <summary>Friedman 조건(열)의 순위합·평균 순위.</summary>
    public sealed record FriedmanCondition(string Name, double RankSum, double MeanRank);

    /// <summary>Friedman χ²(동점 보정)와 Kendall W = χ² / (n(k−1)).</summary>
    public sealed record FriedmanResult(
        double ChiSquare,
        int Df,
        double P,
        double KendallsW,
        bool HasTies,
        bool SmallSample,
        int N,
        int K,
        IReadOnlyList<FriedmanCondition> Conditions,
        long RowsRead,
        long RowsUsed,
        long RowsDropped);

    /// <summary>Mann-Whitney·Wilcoxon·부호·Kruskal-Wallis·Friedman. 행 스캐너는 뷰를 한 번만 순회한다.</summary>
    public static class NonparametricTests
    {
        /// <summary>정확 Mann-Whitney를 계산하는 최대 n₁·n₂. 이보다 크면 점근 p만 보고한다.</summary>
        public const int MannWhitneyExactProductLimit = 2000;
        /// <summary>Dunn 사후비교를 계산하는 최대 그룹 수. 넘으면 H 검정만 보고한다(k(k−1)/2 쌍 폭증 방지).</summary>
        public const int MaxDunnGroups = 100;

        /// <summary>Wilcoxon 정확 분포 상한(scipy와 동일). 초과하거나 동점이 있으면 정규근사.</summary>
        public const int WilcoxonExactMaxN = 50;

        /// <summary>Friedman 조건 수 상한. 행마다 O(k log k)라 넓은 선택도 가능하지만 결과 표를 읽기 위한 한계.</summary>
        public const int MaxFriedmanColumns = 32;

        private const int CancelEvery = 4096;

        public static MannWhitneyResult MannWhitney(IReadOnlyList<double> group1, IReadOnlyList<double> group2, CancellationToken cancellation = default)
            => MannWhitneyCore(group1, group2, "1", "2", 0, group1.Count + group2.Count, group1.Count + group2.Count, 0, cancellation);

        /// <summary>
        /// 값 열·그룹 열을 한 번 순회한다. 그룹 이름이 둘 다 비어 있으면 완전한 그룹이 정확히 두 개일 때만 비교하고,
        /// 셋 이상이면 이름을 지정하라는 예외를 던진다. 지정한 이름은 정확 일치 후 대소문자 무시 유일 일치.
        /// </summary>
        public static MannWhitneyResult MannWhitney(
            IEnumerable<string[]> rows, int valueColumn, int groupColumn,
            string? group1, string? group2, CancellationToken cancellation = default)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            bool named = !string.IsNullOrEmpty(group1) || !string.IsNullOrEmpty(group2);
            if (named && (string.IsNullOrEmpty(group1) || string.IsNullOrEmpty(group2)))
                throw new DesignMatrixException("Enter both group names, or leave both blank to compare the only two groups.");
            if (named && string.Equals(group1, group2, StringComparison.Ordinal))
                throw new DesignMatrixException("The two group names must be different.");

            var lists = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            var extraNames = new List<string>();
            long read = 0, used = 0, dropped = 0, other = 0;
            foreach (var row in rows)
            {
                if ((++read & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                if (!TryCell(row, valueColumn, out double v) || !TryLabel(row, groupColumn, out string label))
                {
                    dropped++;
                    continue;
                }
                if (named)
                {
                    if (!LabelEquals(label, group1!) && !LabelEquals(label, group2!))
                    {
                        other++;
                        dropped++;
                        continue;
                    }
                }
                else if (!lists.ContainsKey(label) && lists.Count >= 3)
                {
                    if (extraNames.Count < 12) extraNames.Add(label);
                    other++;
                    dropped++;
                    continue;
                }
                if (!lists.TryGetValue(label, out var list))
                {
                    list = new List<double>();
                    lists[label] = list;
                }
                list.Add(v);
                used++;
            }

            if (!named)
            {
                if (lists.Count + extraNames.Count < 2)
                    throw new DesignMatrixException("Mann-Whitney needs two groups. Only one group has numeric values.");
                if (lists.Count != 2 || extraNames.Count > 0)
                {
                    var names = StatValue.SortLevels(lists.Keys.Concat(extraNames));
                    string shown = string.Join(", ", names.Take(12));
                    if (names.Count > 12) shown += ", …";
                    throw new DesignMatrixException(
                        $"Mann-Whitney needs two groups, but {lists.Count + extraNames.Count} were found ({shown}). Enter the two group names to compare.");
                }
                var ordered = StatValue.SortLevels(lists.Keys);
                return MannWhitneyCore(lists[ordered[0]], lists[ordered[1]], ordered[0], ordered[1], other, read, used, dropped, cancellation);
            }

            string? a = MatchLabel(lists.Keys, group1!);
            string? b = MatchLabel(lists.Keys, group2!);
            if (a is null || b is null)
            {
                string present = lists.Count == 0 ? "(none)" : string.Join(", ", StatValue.SortLevels(lists.Keys).Take(12));
                string missing = a is null ? group1! : group2!;
                throw new DesignMatrixException($"Group '{missing}' was not found. Groups with numeric values: {present}.");
            }
            if (ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal))
                throw new DesignMatrixException("The two group names refer to the same group.");
            return MannWhitneyCore(lists[a], lists[b], a, b, other, read, used, dropped, cancellation);
        }

        public static WilcoxonResult WilcoxonSignedRank(IReadOnlyList<double> x, IReadOnlyList<double> y, CancellationToken cancellation = default)
        {
            if (x is null) throw new ArgumentNullException(nameof(x));
            if (y is null) throw new ArgumentNullException(nameof(y));
            if (x.Count != y.Count) throw new ArgumentException("Paired samples must have the same length.", nameof(y));
            var diffs = new List<double>(x.Count);
            for (int i = 0; i < x.Count; i++)
            {
                if ((i & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                diffs.Add(x[i] - y[i]);
            }
            return WilcoxonCore(diffs, x.Count, x.Count, 0, cancellation);
        }

        public static WilcoxonResult WilcoxonSignedRank(IEnumerable<string[]> rows, int columnX, int columnY, CancellationToken cancellation = default)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            var diffs = new List<double>();
            long read = 0, dropped = 0;
            foreach (var row in rows)
            {
                if ((++read & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                if (!TryCell(row, columnX, out double a) || !TryCell(row, columnY, out double b))
                {
                    dropped++;
                    continue;
                }
                diffs.Add(a - b);
            }
            if (diffs.Count < 2)
                throw new DesignMatrixException("Need at least 2 complete rows.");
            return WilcoxonCore(diffs, read, diffs.Count, dropped, cancellation);
        }

        public static SignTestResult SignTestPaired(IReadOnlyList<double> x, IReadOnlyList<double> y, CancellationToken cancellation = default)
        {
            if (x is null) throw new ArgumentNullException(nameof(x));
            if (y is null) throw new ArgumentNullException(nameof(y));
            if (x.Count != y.Count) throw new ArgumentException("Paired samples must have the same length.", nameof(y));
            int pos = 0, neg = 0, zeros = 0;
            for (int i = 0; i < x.Count; i++)
            {
                if ((i & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                double d = x[i] - y[i];
                if (d > 0) pos++;
                else if (d < 0) neg++;
                else zeros++;
            }
            return SignCore(pos, neg, zeros, true, double.NaN, x.Count, x.Count, 0);
        }

        public static SignTestResult SignTestPaired(IEnumerable<string[]> rows, int columnX, int columnY, CancellationToken cancellation = default)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            int pos = 0, neg = 0, zeros = 0;
            long read = 0, used = 0, dropped = 0;
            foreach (var row in rows)
            {
                if ((++read & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                if (!TryCell(row, columnX, out double a) || !TryCell(row, columnY, out double b))
                {
                    dropped++;
                    continue;
                }
                used++;
                double d = a - b;
                if (d > 0) pos++;
                else if (d < 0) neg++;
                else zeros++;
            }
            if (used < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            return SignCore(pos, neg, zeros, true, double.NaN, read, used, dropped);
        }

        public static SignTestResult SignTestMedian(IReadOnlyList<double> values, double median, CancellationToken cancellation = default)
        {
            if (values is null) throw new ArgumentNullException(nameof(values));
            if (!double.IsFinite(median)) throw new DesignMatrixException("The hypothesized median must be a finite number.");
            int pos = 0, neg = 0, zeros = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if ((i & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                double d = values[i] - median;
                if (d > 0) pos++;
                else if (d < 0) neg++;
                else zeros++;
            }
            return SignCore(pos, neg, zeros, false, median, values.Count, values.Count, 0);
        }

        public static SignTestResult SignTestMedian(IEnumerable<string[]> rows, int column, double median, CancellationToken cancellation = default)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            if (!double.IsFinite(median)) throw new DesignMatrixException("The hypothesized median must be a finite number.");
            int pos = 0, neg = 0, zeros = 0;
            long read = 0, used = 0, dropped = 0;
            foreach (var row in rows)
            {
                if ((++read & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                if (!TryCell(row, column, out double v))
                {
                    dropped++;
                    continue;
                }
                used++;
                double d = v - median;
                if (d > 0) pos++;
                else if (d < 0) neg++;
                else zeros++;
            }
            if (used < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            return SignCore(pos, neg, zeros, false, median, read, used, dropped);
        }

        public static KruskalWallisResult KruskalWallis(IReadOnlyList<IReadOnlyList<double>> groups, CancellationToken cancellation = default)
        {
            if (groups is null) throw new ArgumentNullException(nameof(groups));
            if (groups.Count < 2) throw new DesignMatrixException("Kruskal-Wallis needs at least 2 groups.");
            var named = new List<(string Name, List<double> Values)>(groups.Count);
            long n = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                if (groups[g] is null || groups[g].Count == 0)
                    throw new DesignMatrixException("Each group needs at least one observation.");
                named.Add(((g + 1).ToString(CultureInfo.InvariantCulture), groups[g] as List<double> ?? groups[g].ToList()));
                n += groups[g].Count;
            }
            return KruskalCore(named, n, n, 0, cancellation);
        }

        public static KruskalWallisResult KruskalWallis(IEnumerable<string[]> rows, int valueColumn, int groupColumn, CancellationToken cancellation = default)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            var lists = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            long read = 0, used = 0, dropped = 0;
            foreach (var row in rows)
            {
                if ((++read & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                if (!TryCell(row, valueColumn, out double v) || !TryLabel(row, groupColumn, out string label))
                {
                    dropped++;
                    continue;
                }
                if (!lists.TryGetValue(label, out var list))
                {
                    list = new List<double>();
                    lists[label] = list;
                }
                list.Add(v);
                used++;
            }
            if (used < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            if (lists.Count < 2) throw new DesignMatrixException("Kruskal-Wallis needs at least 2 groups with numeric values.");
            var ordered = StatValue.SortLevels(lists.Keys);
            var named = ordered.Select(name => (name, lists[name])).ToList();
            return KruskalCore(named, read, used, dropped, cancellation);
        }

        public static FriedmanResult Friedman(IReadOnlyList<IReadOnlyList<double>> blocks, CancellationToken cancellation = default)
        {
            if (blocks is null) throw new ArgumentNullException(nameof(blocks));
            if (blocks.Count < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            int k = blocks[0].Count;
            if (k < 3) throw new DesignMatrixException("Friedman needs at least 3 conditions.");
            if (k > MaxFriedmanColumns)
                throw new DesignMatrixException($"Friedman accepts at most {MaxFriedmanColumns} conditions.");
            var rankSum = new double[k];
            double tieSum = 0;
            var idx = new int[k];
            for (int i = 0; i < blocks.Count; i++)
            {
                if ((i & (CancelEvery - 1)) == 0) cancellation.ThrowIfCancellationRequested();
                if (blocks[i].Count != k) throw new ArgumentException("Every block must have the same number of conditions.", nameof(blocks));
                var buf = blocks[i] as double[] ?? blocks[i].ToArray();
                AccumulateFriedmanRow(buf, idx, rankSum, ref tieSum);
            }
            var names = Enumerable.Range(1, k).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
            return FinishFriedman(rankSum, tieSum, blocks.Count, names, blocks.Count, blocks.Count, 0);
        }

        public static FriedmanResult Friedman(
            IEnumerable<string[]> rows, IReadOnlyList<int> columns, IReadOnlyList<string> names, CancellationToken cancellation = default)
        {
            if (rows is null) throw new ArgumentNullException(nameof(rows));
            if (columns is null) throw new ArgumentNullException(nameof(columns));
            int k = columns.Count;
            if (k < 3) throw new DesignMatrixException("Friedman needs at least 3 conditions.");
            if (k > MaxFriedmanColumns)
                throw new DesignMatrixException($"Friedman accepts at most {MaxFriedmanColumns} conditions.");
            var rankSum = new double[k];
            double tieSum = 0;
            var buf = new double[k];
            var idx = new int[k];
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
                used++;
                AccumulateFriedmanRow(buf, idx, rankSum, ref tieSum);
            }
            if (used < 2) throw new DesignMatrixException("Need at least 2 complete rows.");
            return FinishFriedman(rankSum, tieSum, (int)used, names, read, used, dropped);
        }

        // ---------------------------------------------------------------- 핵심 계산

        private static MannWhitneyResult MannWhitneyCore(
            IReadOnlyList<double> g1, IReadOnlyList<double> g2, string name1, string name2,
            long other, long read, long used, long dropped, CancellationToken ct)
        {
            int n1 = g1.Count, n2 = g2.Count;
            if (n1 < 1 || n2 < 1) throw new DesignMatrixException("Each group needs at least one numeric value.");
            int n = n1 + n2;
            var values = new double[n];
            var from1 = new bool[n];
            for (int i = 0; i < n1; i++) { values[i] = g1[i]; from1[i] = true; }
            for (int i = 0; i < n2; i++) values[n1 + i] = g2[i];
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

            double rankSum1 = 0, tieSum = 0;
            int iRun = 0;
            while (iRun < n)
            {
                if ((iRun & (CancelEvery - 1)) == 0) ct.ThrowIfCancellationRequested();
                int j = iRun + 1;
                while (j < n && values[order[j]] == values[order[iRun]]) j++;
                int t = j - iRun;
                double rank = (iRun + 1 + j) / 2.0;
                if (t > 1) tieSum += (double)t * t * t - t;
                for (int slot = iRun; slot < j; slot++)
                    if (from1[order[slot]]) rankSum1 += rank;
                iRun = j;
            }

            double u1 = rankSum1 - n1 * (n1 + 1.0) / 2.0;
            double u2 = (double)n1 * n2 - u1;
            double mu = (double)n1 * n2 / 2.0;
            double sigma = Math.Sqrt((double)n1 * n2 / 12.0 * ((n + 1.0) - tieSum / (n * (n - 1.0))));
            bool ties = tieSum > 0;
            bool varianceZero = !(sigma > 0);
            double z = double.NaN, pAsym = double.NaN;
            if (!varianceZero)
            {
                // scipy는 항상 더 큰 U에서 0.5를 빼 SF를 쓴다. U가 평균에 붙으면 z가 음수가 되고 p는 1로 잘린다.
                z = (Math.Max(u1, u2) - mu - 0.5) / sigma;
                pAsym = Math.Clamp(2 * Dist.NormalCdf(-z), 0, 1);
            }

            double pExact = double.NaN;
            bool exact = !ties && (double)n1 * n2 <= MannWhitneyExactProductLimit;
            if (exact)
            {
                long uMin = (long)Math.Round(Math.Min(u1, u2));
                pExact = MannWhitneyExactP(uMin, n1, n2, ct);
            }

            return new MannWhitneyResult(
                u1, u2, z, pAsym, pExact, exact, ties, varianceZero,
                mu == 0 ? double.NaN : 2 * u1 / (n1 * (double)n2) - 1,
                n1, n2, Median(g1), Median(g2), name1, name2, other, read, used, dropped);
        }

        private static WilcoxonResult WilcoxonCore(List<double> diffs, long read, long used, long dropped, CancellationToken ct)
        {
            int zeros = 0;
            var nz = new List<double>(diffs.Count);
            for (int i = 0; i < diffs.Count; i++)
            {
                if (diffs[i] == 0) zeros++;
                else nz.Add(diffs[i]);
            }
            if (nz.Count < 1)
                throw new DesignMatrixException("All paired differences are zero; the Wilcoxon signed-rank test is undefined.");

            int n = nz.Count;
            var abs = new double[n];
            var positive = new bool[n];
            for (int i = 0; i < n; i++)
            {
                abs[i] = Math.Abs(nz[i]);
                positive[i] = nz[i] > 0;
            }
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => abs[a].CompareTo(abs[b]));

            var ranks = new double[n];
            double tieSum = 0;
            int iRun = 0;
            while (iRun < n)
            {
                if ((iRun & (CancelEvery - 1)) == 0) ct.ThrowIfCancellationRequested();
                int j = iRun + 1;
                while (j < n && abs[order[j]] == abs[order[iRun]]) j++;
                int t = j - iRun;
                double rank = (iRun + 1 + j) / 2.0;
                if (t > 1) tieSum += (double)t * t * t - t;
                for (int slot = iRun; slot < j; slot++) ranks[order[slot]] = rank;
                iRun = j;
            }

            double tPlus = 0, tMinus = 0;
            for (int i = 0; i < n; i++)
            {
                if (positive[i]) tPlus += ranks[i];
                else tMinus += ranks[i];
            }
            bool ties = tieSum > 0;
            double mean = n * (n + 1.0) / 4.0;
            double se = Math.Sqrt((n * (n + 1.0) * (2.0 * n + 1.0) - tieSum / 2.0) / 24.0);
            double z0 = (tPlus - mean) / se;
            double sign = z0 > 0 ? 1 : z0 < 0 ? -1 : 0;
            double z = z0 - sign * 0.5 / se;
            bool exact = n <= WilcoxonExactMaxN && !ties;
            double p = exact
                ? WilcoxonExactP(n, (int)Math.Round(tPlus))
                : Math.Clamp(Dist.NormalTwoSided(z), 0, 1);
            return new WilcoxonResult(
                Math.Min(tPlus, tMinus), tPlus, tMinus, z, z / Math.Sqrt(n), p, exact, ties, n, zeros, read, used, dropped);
        }

        private static SignTestResult SignCore(int pos, int neg, int zeros, bool paired, double median, long read, long used, long dropped)
        {
            int n = pos + neg;
            if (n < 1)
                throw new DesignMatrixException(paired
                    ? "All paired differences are zero; the sign test is undefined."
                    : "Every value equals the hypothesized median; the sign test is undefined.");
            return new SignTestResult(pos, neg, zeros, BinomialTwoSidedHalf(pos, n), paired, median, read, used, dropped);
        }

        private static KruskalWallisResult KruskalCore(
            List<(string Name, List<double> Values)> groups, long read, long used, long dropped, CancellationToken ct)
        {
            int k = groups.Count;
            int n = 0;
            for (int g = 0; g < k; g++) n += groups[g].Values.Count;
            if (n < 2) throw new DesignMatrixException("Need at least 2 complete rows.");

            var values = new double[n];
            var groupOf = new int[n];
            int cursor = 0;
            for (int g = 0; g < k; g++)
            {
                var src = groups[g].Values;
                for (int i = 0; i < src.Count; i++)
                {
                    values[cursor] = src[i];
                    groupOf[cursor] = g;
                    cursor++;
                }
            }
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

            var rankSum = new double[k];
            double tieSum = 0;
            int iRun = 0;
            while (iRun < n)
            {
                if ((iRun & (CancelEvery - 1)) == 0) ct.ThrowIfCancellationRequested();
                int j = iRun + 1;
                while (j < n && values[order[j]] == values[order[iRun]]) j++;
                int t = j - iRun;
                double rank = (iRun + 1 + j) / 2.0;
                if (t > 1) tieSum += (double)t * t * t - t;
                for (int slot = iRun; slot < j; slot++) rankSum[groupOf[order[slot]]] += rank;
                iRun = j;
            }

            // scipy tiecorrect = 1 − Σ(t³−t)/(N³−N). 0이면 모든 값이 같다.
            double tieFactor = 1.0 - tieSum / (n * (n * (double)n - 1.0));
            if (!(tieFactor > 0))
                throw new DesignMatrixException("All values are identical; the Kruskal-Wallis test is undefined.");

            double ssbn = 0;
            bool small = false;
            var summaries = new KruskalGroup[k];
            for (int g = 0; g < k; g++)
            {
                int ng = groups[g].Values.Count;
                if (ng < 5) small = true;
                ssbn += rankSum[g] * rankSum[g] / ng;
                summaries[g] = new KruskalGroup(groups[g].Name, ng, Median(groups[g].Values), rankSum[g] / ng);
            }
            double h = (12.0 / (n * (n + 1.0)) * ssbn - 3.0 * (n + 1.0)) / tieFactor;
            int df = k - 1;
            double p = Dist.ChiSquareUpper(h, df);
            double eps2 = h * (n + 1.0) / (n * (double)n - 1.0);

            // Dunn (1964): se² = (N(N+1)/12 − Σ(t³−t)/(12(N−1))) · (1/nᵢ + 1/nⱼ). 동점 보정은 MWU 분산과 같은 항.
            // 그룹이 너무 많으면(ID 컬럼을 그룹으로 고른 경우 등) k(k−1)/2 쌍을 만들지 않는다 — 메모리 폭주 방지.
            long comparisons = (long)k * (k - 1) / 2;
            double baseVar = n * (n + 1.0) / 12.0 - tieSum / (12.0 * (n - 1.0));
            var pairs = new List<DunnPair>(k <= MaxDunnGroups ? (int)comparisons : 0);
            if (baseVar > 0 && k <= MaxDunnGroups)
            {
                for (int a = 0; a < k; a++)
                {
                    for (int b = a + 1; b < k; b++)
                    {
                        ct.ThrowIfCancellationRequested();
                        double se = Math.Sqrt(baseVar * (1.0 / summaries[a].N + 1.0 / summaries[b].N));
                        double z = (summaries[a].MeanRank - summaries[b].MeanRank) / se;
                        double raw = Dist.NormalTwoSided(z);
                        pairs.Add(new DunnPair(a, b, z, raw, Math.Min(1, raw * comparisons)));
                    }
                }
            }
            return new KruskalWallisResult(h, df, p, eps2, small, summaries, pairs, comparisons, read, used, dropped);
        }

        private static void AccumulateFriedmanRow(double[] values, int[] idx, double[] rankSum, ref double tieSum)
        {
            int k = values.Length;
            for (int j = 0; j < k; j++) idx[j] = j;
            for (int a = 1; a < k; a++)
            {
                int v = idx[a];
                double key = values[v];
                int b = a - 1;
                while (b >= 0 && values[idx[b]] > key)
                {
                    idx[b + 1] = idx[b];
                    b--;
                }
                idx[b + 1] = v;
            }
            int i = 0;
            while (i < k)
            {
                int j = i + 1;
                while (j < k && values[idx[j]] == values[idx[i]]) j++;
                int t = j - i;
                double rank = (i + 1 + j) / 2.0;
                if (t > 1) tieSum += (double)t * t * t - t;
                for (int slot = i; slot < j; slot++) rankSum[idx[slot]] += rank;
                i = j;
            }
        }

        private static FriedmanResult FinishFriedman(
            double[] rankSum, double tieSum, int n, IReadOnlyList<string> names, long read, long used, long dropped)
        {
            int k = rankSum.Length;
            double c = 1.0 - tieSum / (k * (k * (double)k - 1.0) * n);
            if (!(c > 1e-12))
                throw new DesignMatrixException("Every block is tied; the Friedman test is undefined.");
            double ssbn = 0;
            for (int j = 0; j < k; j++) ssbn += rankSum[j] * rankSum[j];
            double chi = (12.0 / (k * (double)n * (k + 1.0)) * ssbn - 3.0 * n * (k + 1.0)) / c;
            int df = k - 1;
            var conds = new FriedmanCondition[k];
            for (int j = 0; j < k; j++)
            {
                string name = names is not null && j < names.Count ? names[j] : (j + 1).ToString(CultureInfo.InvariantCulture);
                conds[j] = new FriedmanCondition(name, rankSum[j], rankSum[j] / n);
            }
            return new FriedmanResult(
                chi, df, Dist.ChiSquareUpper(chi, df), chi / (n * (double)df), tieSum > 0, n <= 10,
                n, k, conds, read, used, dropped);
        }

        // scipy _MWU.build_u_freqs_array. 계수는 정수로 정확히 나누어떨어지므로 BigInteger를 쓴다.
        private static double MannWhitneyExactP(long uMin, int n1, int n2, CancellationToken ct)
        {
            int m = (int)uMin;
            int a = Math.Min(n1, n2);
            int b = Math.Max(n1, n2);
            var sigma = new int[m + 1];
            for (int d = 1; d <= a; d++)
                for (int idx = d; idx <= m; idx += d) sigma[idx] += d;
            for (int d = b + 1; d <= b + a; d++)
                for (int idx = d; idx <= m; idx += d) sigma[idx] -= d;

            var conf = new BigInteger[m + 1];
            conf[0] = BigInteger.One;
            for (int u = 1; u <= m; u++)
            {
                if ((u & 31) == 0) ct.ThrowIfCancellationRequested();
                BigInteger s = BigInteger.Zero;
                for (int j = 0; j < u; j++)
                {
                    int coef = sigma[u - j];
                    if (coef == 0) continue;
                    s += coef > 0 ? conf[j] * coef : -(conf[j] * -coef);
                }
                conf[u] = s / u;
            }
            BigInteger cdf = BigInteger.Zero;
            for (int i = 0; i <= m; i++) cdf += conf[i];
            BigInteger total = BinomialCoefficient(a + b, a);
            // 2·cdf/total 을 12자리로 환원. 소표본 이항계수는 이 자리에서 scipy와 일치한다.
            const long scale = 1_000_000_000_000L;
            double p = (double)(cdf * (2 * scale) / total) / scale;
            return Math.Clamp(p, 0, 1);
        }

        private static BigInteger BinomialCoefficient(int n, int k)
        {
            if (k > n - k) k = n - k;
            BigInteger r = BigInteger.One;
            for (int i = 1; i <= k; i++)
            {
                r *= n - k + i;
                r /= i;
            }
            return r;
        }

        // 부호 순위 T⁺의 영가설 분포. n≤50이면 부분집합 개수가 2^53 안에 있어 double 정수로 정확하다.
        private static double WilcoxonExactP(int n, int tPlus)
        {
            int max = n * (n + 1) / 2;
            if (tPlus < 0) tPlus = 0;
            if (tPlus > max) tPlus = max;
            var counts = new double[1];
            counts[0] = 1;
            for (int k = 1; k <= n; k++)
            {
                var next = new double[k * (k + 1) / 2 + 1];
                for (int s = 0; s < counts.Length; s++)
                {
                    next[s] += counts[s];
                    next[s + k] += counts[s];
                }
                counts = next;
            }
            double cdf = 0, sf = 0;
            for (int s = 0; s <= tPlus; s++) cdf += counts[s];
            for (int s = tPlus; s < counts.Length; s++) sf += counts[s];
            return Math.Clamp(2 * Math.Min(cdf, sf) / Math.ScaleB(1, n), 0, 1);
        }

        /// <summary>p=0.5 양측 이항검정. scipy.stats.binomtest와 같이 2·CDF(min(k, n−k)), 상한 1.</summary>
        public static double BinomialTwoSidedHalf(int k, int n)
        {
            if (n < 1 || k < 0 || k > n) return double.NaN;
            int m = Math.Min(k, n - k);
            return Math.Clamp(2 * alglib.binomialdistribution(m, n, 0.5), 0, 1);
        }

        private static double Median(IReadOnlyList<double> values)
        {
            int n = values.Count;
            if (n == 0) return double.NaN;
            var a = new double[n];
            for (int i = 0; i < n; i++) a[i] = values[i];
            Array.Sort(a);
            int mid = n / 2;
            return (n & 1) == 1 ? a[mid] : (a[mid - 1] + a[mid]) / 2.0;
        }

        private static bool TryCell(string[] row, int col, out double value)
        {
            value = 0;
            return row is not null && (uint)col < (uint)row.Length && StatValue.TryNumber(row[col], out value);
        }

        private static bool TryLabel(string[] row, int col, out string label)
        {
            label = "";
            if (row is null || (uint)col >= (uint)row.Length || StatValue.IsMissing(row[col])) return false;
            label = row[col].Trim();
            return label.Length > 0 && !StatValue.IsMissing(label);
        }

        private static bool LabelEquals(string label, string wanted)
            => string.Equals(label, wanted, StringComparison.Ordinal)
               || string.Equals(label, wanted, StringComparison.OrdinalIgnoreCase);

        private static string? MatchLabel(IEnumerable<string> names, string wanted)
        {
            string? ignore = null;
            foreach (var name in names)
            {
                if (string.Equals(name, wanted, StringComparison.Ordinal)) return name;
                if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (ignore is not null) return null;
                ignore = name;
            }
            return ignore;
        }
    }
}
