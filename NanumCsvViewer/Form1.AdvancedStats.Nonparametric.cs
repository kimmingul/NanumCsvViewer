using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    // 비모수 검정·반복측정 ANOVA(이슈 #27). 대화상자에서 열을 고르고, 뷰를 한 번 순회한 결과를 고정폭 텍스트로 보인다.
    public partial class Form1
    {
        private const int DunnDisplayCap = 30;

        private async void AdvMannWhitney()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            int vc, gc;
            string groupA, groupB;
            using (var dlg = new ParamDialog(LT("Mann-Whitney U", "Mann-Whitney U"), _palette))
            {
                var valueCol = dlg.AddCombo(LT("Value column", "값 컬럼"), ColumnLabels(), FirstNumericColumn());
                var groupCol = dlg.AddCombo(LT("Group column", "그룹 컬럼"), ColumnLabels(), AdvDefaultGroupColumn());
                var a = dlg.AddText(LT("Group A (optional)", "그룹 A (선택)"), "");
                var b = dlg.AddText(LT("Group B (optional)", "그룹 B (선택)"), "");
                dlg.AddNote(LT(
                    "Leave both names blank when the column has exactly two groups. Otherwise enter the two levels to compare. U is for group A (or the first sorted level).",
                    "그룹이 정확히 둘이면 이름을 비워 두세요. 셋 이상이면 비교할 두 수준을 입력하세요. U는 그룹 A(또는 정렬상 첫 수준)의 통계량입니다."));
                if (!dlg.ShowOk(this)) return;
                vc = valueCol.SelectedIndex;
                gc = groupCol.SelectedIndex;
                groupA = a.Text.Trim();
                groupB = b.Text.Trim();
            }
            if (vc < 0 || gc < 0)
            {
                ShowResult(LT("Mann-Whitney U", "Mann-Whitney U"), LT("Select a value column and a group column.", "값 컬럼과 그룹 컬럼을 선택하세요."));
                return;
            }
            if (vc == gc)
            {
                ShowResult(LT("Mann-Whitney U", "Mann-Whitney U"), LT("The value column and the group column must be different.", "값 컬럼과 그룹 컬럼은 달라야 합니다."));
                return;
            }
            string title = LT("Mann-Whitney U", "Mann-Whitney U");
            await RunAdvancedAsync(title, input =>
            {
                var r = NonparametricTests.MannWhitney(input.Rows, vc, gc,
                    groupA.Length == 0 ? null : groupA, groupB.Length == 0 ? null : groupB, input.Cancellation);
                var sb = new StringBuilder();
                sb.AppendLine(AdvScope(r.RowsRead, r.RowsUsed, r.RowsDropped));
                sb.AppendLine();
                sb.AppendLine($"{input.Headers[vc]}  {LT("by", "기준")}  {input.Headers[gc]}");
                sb.AppendLine(LT(
                    "Asymptotic normal approximation with tie correction and continuity correction (scipy.stats.mannwhitneyu, method='asymptotic', use_continuity=True).",
                    "동점 보정·연속 보정을 넣은 정규근사 (scipy.stats.mannwhitneyu, method='asymptotic', use_continuity=True)."));
                if (r.OtherGroupsExcluded > 0)
                    sb.AppendLine(LT(
                        $"Other groups excluded: {r.OtherGroupsExcluded:N0} rows.",
                        $"다른 그룹으로 제외: {r.OtherGroupsExcluded:N0}행."));
                sb.AppendLine();
                var groups = new TextTable(LT("Group", "그룹"), "n", LT("Median", "중앙값"));
                groups.AddRow(Clip(r.Group1), StatFormat.Int(r.N1), StatFormat.G(r.Median1));
                groups.AddRow(Clip(r.Group2), StatFormat.Int(r.N2), StatFormat.G(r.Median2));
                sb.Append(groups.Render());
                sb.AppendLine();
                var stats = new TextTable(LT("Statistic", "통계량"), LT("Value", "값"));
                stats.AddRow("U (" + Clip(r.Group1) + ")", StatFormat.G(r.U1));
                stats.AddRow("U (" + Clip(r.Group2) + ")", StatFormat.G(r.U2));
                stats.AddRow("z", StatFormat.G(r.Z));
                stats.AddRow(LT("p (two-sided)", "p (양측)"), PStars(r.PAsymptotic));
                if (r.ExactComputed) stats.AddRow(LT("exact p", "정확 p"), PStars(r.PExact));
                stats.AddRow(LT("rank-biserial r", "순위양분 r"), StatFormat.G(r.RankBiserial));
                sb.Append(stats.Render());
                sb.AppendLine();
                sb.AppendLine(LT(
                    "z = (max(U1, U2) − n1·n2/2 − 0.5) / σ. Rank-biserial r = 2·U1/(n1·n2) − 1 (positive: group A tends to be larger).",
                    "z = (max(U1, U2) − n1·n2/2 − 0.5) / σ. 순위양분 r = 2·U1/(n1·n2) − 1 (양수면 그룹 A가 더 큰 경향)."));
                if (r.VarianceZero)
                    sb.AppendLine(LT(
                        "Every observation is tied, so the asymptotic variance is 0. z and p are not reported.",
                        "관측값이 모두 동점이라 점근 분산이 0입니다. z와 p는 보고하지 않습니다."));
                else if (r.ExactComputed)
                    sb.AppendLine(LT(
                        $"Exact p: scipy method='exact' (no ties, n1·n2 = {r.N1 * (long)r.N2} ≤ {NonparametricTests.MannWhitneyExactProductLimit}). Prefer this p when both samples are small.",
                        $"정확 p: scipy method='exact' (동점 없음, n1·n2 = {r.N1 * (long)r.N2} ≤ {NonparametricTests.MannWhitneyExactProductLimit}). 표본이 작으면 이 p를 우선하세요."));
                else if (r.HasTies)
                    sb.AppendLine(LT(
                        "Exact p was not computed: ties are present, and the no-tie exact distribution does not apply.",
                        "정확 p는 계산하지 않았습니다. 동점이 있어 무동점 정확분포를 쓸 수 없습니다."));
                else
                    sb.AppendLine(LT(
                        $"Exact p was not computed: n1·n2 = {r.N1 * (long)r.N2} exceeds {NonparametricTests.MannWhitneyExactProductLimit}.",
                        $"정확 p는 계산하지 않았습니다. n1·n2 = {r.N1 * (long)r.N2} 가 한도 {NonparametricTests.MannWhitneyExactProductLimit} 를 넘습니다."));
                if (!r.ExactComputed && (r.N1 < 5 || r.N2 < 5) && !r.VarianceZero)
                    sb.AppendLine(LT(
                        "Warning: a sample has fewer than 5 observations. The normal approximation is rough.",
                        "경고: 한 집단의 관측이 5개 미만입니다. 정규근사는 거칠 수 있습니다."));
                return sb.ToString();
            });
        }

        private async void AdvWilcoxon()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            int c1, c2;
            using (var dlg = new ParamDialog(LT("Wilcoxon Signed-Rank", "Wilcoxon 부호순위"), _palette))
            {
                var x = dlg.AddCombo(LT("Column 1", "컬럼 1"), ColumnLabels(), FirstNumericColumn());
                var y = dlg.AddCombo(LT("Column 2", "컬럼 2"), ColumnLabels(), Math.Min(FirstNumericColumn() + 1, Math.Max(0, _doc.ColumnCount - 1)));
                dlg.AddNote(LT(
                    "Paired columns. Zero differences are dropped (scipy zero_method='wilcox') and are not counted as missing.",
                    "대응 컬럼입니다. 차이가 0인 쌍은 제외합니다(scipy zero_method='wilcox'). 결측으로 세지 않습니다."));
                if (!dlg.ShowOk(this)) return;
                c1 = x.SelectedIndex;
                c2 = y.SelectedIndex;
            }
            if (!RequireDistinctPair(c1, c2, LT("Wilcoxon Signed-Rank", "Wilcoxon 부호순위"))) return;
            string title = LT("Wilcoxon Signed-Rank", "Wilcoxon 부호순위");
            await RunAdvancedAsync(title, input =>
            {
                var r = NonparametricTests.WilcoxonSignedRank(input.Rows, c1, c2, input.Cancellation);
                var sb = new StringBuilder();
                sb.AppendLine(AdvScope(r.RowsRead, r.RowsUsed, r.RowsDropped));
                sb.AppendLine();
                sb.AppendLine($"{input.Headers[c1]}  −  {input.Headers[c2]}");
                sb.AppendLine(r.UsedExact
                    ? LT($"Method: exact (n = {r.N} ≤ {NonparametricTests.WilcoxonExactMaxN}, no ties). scipy.stats.wilcoxon(method='exact', zero_method='wilcox').",
                         $"방법: 정확분포 (n = {r.N} ≤ {NonparametricTests.WilcoxonExactMaxN}, 동점 없음). scipy.stats.wilcoxon(method='exact', zero_method='wilcox').")
                    : LT($"Method: normal approximation with tie correction and continuity correction. scipy.stats.wilcoxon(method='asymptotic', correction=True, zero_method='wilcox'). {(r.HasTies ? "Ties are present, so the exact distribution does not apply." : $"n = {r.N} exceeds {NonparametricTests.WilcoxonExactMaxN}.")}",
                         $"방법: 동점·연속 보정을 넣은 정규근사. scipy.stats.wilcoxon(method='asymptotic', correction=True, zero_method='wilcox'). {(r.HasTies ? "동점이 있어 정확분포를 쓰지 않습니다." : $"n = {r.N} 이(가) {NonparametricTests.WilcoxonExactMaxN} 을 넘습니다.")}"));
                sb.AppendLine(LT(
                    $"Zero differences dropped: {r.ZerosDropped:N0}. Non-zero pairs used: {r.N:N0}.",
                    $"차이가 0이라 제외: {r.ZerosDropped:N0}쌍. 사용한 비영 쌍: {r.N:N0}."));
                sb.AppendLine();
                var stats = new TextTable(LT("Statistic", "통계량"), LT("Value", "값"));
                stats.AddRow("W", StatFormat.G(r.W));
                stats.AddRow("T+", StatFormat.G(r.TPlus));
                stats.AddRow("T−", StatFormat.G(r.TMinus));
                stats.AddRow("z", StatFormat.G(r.Z));
                stats.AddRow(LT("p (two-sided)", "p (양측)"), PStars(r.P));
                stats.AddRow(LT("effect size r", "효과크기 r"), StatFormat.G(r.EffectSizeR));
                sb.Append(stats.Render());
                sb.AppendLine();
                sb.AppendLine(LT(
                    "W = min(T+, T−) is the two-sided statistic. z is the continuity-corrected normal statistic in the T+ direction. r = z / √n (n = non-zero pairs); the sign follows T+.",
                    "W = min(T+, T−) 는 양측 통계량입니다. z는 T+ 방향의 연속보정 정규 통계량입니다. r = z / √n (n = 비영 쌍)이며 부호는 T+를 따릅니다."));
                if (r.UsedExact)
                    sb.AppendLine(LT(
                        "The exact p-value does not use z. r still uses the normal z so the effect size is defined the same way.",
                        "정확 p는 z를 쓰지 않습니다. 효과크기 r은 같은 정의를 쓰도록 정규 z로 계산합니다."));
                return sb.ToString();
            });
        }

        private async void AdvSignTest()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            int mode, c1, c2;
            string medianText;
            using (var dlg = new ParamDialog(LT("Sign Test", "부호 검정"), _palette))
            {
                var kind = dlg.AddCombo(LT("Test", "검정"), new[]
                {
                    LT("Two paired columns", "대응 두 컬럼"),
                    LT("One column vs a value", "한 컬럼 대 기준값"),
                });
                var x = dlg.AddCombo(LT("Column 1", "컬럼 1"), ColumnLabels(), FirstNumericColumn());
                var y = dlg.AddCombo(LT("Column 2 (paired)", "컬럼 2 (대응)"), ColumnLabels(), Math.Min(FirstNumericColumn() + 1, Math.Max(0, _doc.ColumnCount - 1)));
                var median = dlg.AddText(LT("Hypothesized median", "기준 중앙값"), "0");
                dlg.AddNote(LT(
                    "Paired mode ignores the median box. One-sample mode ignores column 2. Ties to zero (or to the median) are dropped. p is the two-sided exact binomial test (scipy.stats.binomtest, p = 0.5).",
                    "대응 모드는 중앙값 칸을 무시합니다. 일표본 모드는 컬럼 2를 무시합니다. 0(또는 기준 중앙값)과 같은 값은 제외합니다. p는 양측 정확 이항검정입니다(scipy.stats.binomtest, p = 0.5)."));
                if (!dlg.ShowOk(this)) return;
                mode = kind.SelectedIndex;
                c1 = x.SelectedIndex;
                c2 = y.SelectedIndex;
                medianText = median.Text.Trim();
            }
            bool paired = mode == 0;
            double medianValue = 0;
            if (paired)
            {
                if (!RequireDistinctPair(c1, c2, LT("Sign Test", "부호 검정"))) return;
            }
            else
            {
                if (c1 < 0)
                {
                    ShowResult(LT("Sign Test", "부호 검정"), LT("Select a column.", "컬럼을 선택하세요."));
                    return;
                }
                if (!StatValue.TryNumber(medianText, out medianValue))
                {
                    ShowResult(LT("Sign Test", "부호 검정"), LT("Enter a numeric hypothesized median.", "기준 중앙값을 숫자로 입력하세요."));
                    return;
                }
            }
            string title = LT("Sign Test", "부호 검정");
            await RunAdvancedAsync(title, input =>
            {
                var r = paired
                    ? NonparametricTests.SignTestPaired(input.Rows, c1, c2, input.Cancellation)
                    : NonparametricTests.SignTestMedian(input.Rows, c1, medianValue, input.Cancellation);
                var sb = new StringBuilder();
                sb.AppendLine(AdvScope(r.RowsRead, r.RowsUsed, r.RowsDropped));
                sb.AppendLine();
                sb.AppendLine(paired
                    ? $"{input.Headers[c1]}  −  {input.Headers[c2]}"
                    : LT($"{input.Headers[c1]}  vs median {StatFormat.G(r.HypothesizedMedian)}",
                         $"{input.Headers[c1]}  대 중앙값 {StatFormat.G(r.HypothesizedMedian)}"));
                sb.AppendLine(LT(
                    "Exact two-sided binomial test, H0: P(positive) = 0.5 (scipy.stats.binomtest).",
                    "양측 정확 이항검정, H0: P(양수) = 0.5 (scipy.stats.binomtest)."));
                sb.AppendLine();
                var stats = new TextTable(LT("Statistic", "통계량"), LT("Value", "값"));
                stats.AddRow(LT("positive", "양수"), StatFormat.Int(r.Positive));
                stats.AddRow(LT("negative", "음수"), StatFormat.Int(r.Negative));
                stats.AddRow(LT("zeros dropped", "0 제외"), StatFormat.Int(r.Zeros));
                stats.AddRow("n", StatFormat.Int(r.Positive + r.Negative));
                stats.AddRow(LT("p (two-sided)", "p (양측)"), PStars(r.P));
                sb.Append(stats.Render());
                return sb.ToString();
            });
        }

        private async void AdvKruskalWallis()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            int vc, gc;
            using (var dlg = new ParamDialog(LT("Kruskal-Wallis H", "Kruskal-Wallis H"), _palette))
            {
                var valueCol = dlg.AddCombo(LT("Value column", "값 컬럼"), ColumnLabels(), FirstNumericColumn());
                var groupCol = dlg.AddCombo(LT("Group column", "그룹 컬럼"), ColumnLabels(), AdvDefaultGroupColumn());
                dlg.AddNote(LT(
                    "Two or more groups. H uses the tie correction. Dunn post-hoc z uses the same tie correction; p is Bonferroni-adjusted over every pair, even when the table is truncated.",
                    "그룹이 둘 이상이어야 합니다. H는 동점 보정됩니다. Dunn 사후 z도 같은 동점 보정을 쓰며, 표를 잘라 보여도 Bonferroni는 모든 쌍에 대해 보정합니다."));
                if (!dlg.ShowOk(this)) return;
                vc = valueCol.SelectedIndex;
                gc = groupCol.SelectedIndex;
            }
            if (vc < 0 || gc < 0)
            {
                ShowResult(LT("Kruskal-Wallis H", "Kruskal-Wallis H"), LT("Select a value column and a group column.", "값 컬럼과 그룹 컬럼을 선택하세요."));
                return;
            }
            if (vc == gc)
            {
                ShowResult(LT("Kruskal-Wallis H", "Kruskal-Wallis H"), LT("The value column and the group column must be different.", "값 컬럼과 그룹 컬럼은 달라야 합니다."));
                return;
            }
            string title = LT("Kruskal-Wallis H", "Kruskal-Wallis H");
            await RunAdvancedAsync(title, input =>
            {
                var r = NonparametricTests.KruskalWallis(input.Rows, vc, gc, input.Cancellation);
                var sb = new StringBuilder();
                sb.AppendLine(AdvScope(r.RowsRead, r.RowsUsed, r.RowsDropped));
                sb.AppendLine();
                sb.AppendLine($"{input.Headers[vc]}  {LT("by", "기준")}  {input.Headers[gc]}");
                sb.AppendLine(LT(
                    "Kruskal-Wallis H with tie correction (scipy.stats.kruskal). ε² = H·(N+1)/(N²−1).",
                    "동점 보정 Kruskal-Wallis H (scipy.stats.kruskal). ε² = H·(N+1)/(N²−1)."));
                sb.AppendLine();
                var groups = new TextTable(LT("Group", "그룹"), "n", LT("Median", "중앙값"), LT("Mean rank", "평균 순위"));
                foreach (var g in r.Groups)
                    groups.AddRow(Clip(g.Name), StatFormat.Int(g.N), StatFormat.G(g.Median), StatFormat.F(g.MeanRank, 3));
                sb.Append(groups.Render());
                sb.AppendLine();
                var stats = new TextTable(LT("Statistic", "통계량"), LT("Value", "값"));
                stats.AddRow("H", StatFormat.G(r.H));
                stats.AddRow("df", StatFormat.Int(r.Df));
                stats.AddRow("p", PStars(r.P));
                stats.AddRow("ε²", StatFormat.G(r.EpsilonSquared));
                sb.Append(stats.Render());
                if (r.SmallSample)
                    sb.AppendLine(LT(
                        "Warning: a group has fewer than 5 observations. The chi-square approximation may be unreliable.",
                        "경고: 관측이 5개 미만인 그룹이 있습니다. 카이제곱 근사는 불안정할 수 있습니다."));
                sb.AppendLine();
                sb.AppendLine(LT(
                    $"Dunn post-hoc (tie-corrected z, two-sided normal p, Bonferroni × {r.Comparisons}).",
                    $"Dunn 사후비교 (동점 보정 z, 양측 정규 p, Bonferroni × {r.Comparisons})."));
                if (r.Dunn.Count == 0)
                {
                    sb.AppendLine(r.Groups.Count > NonparametricTests.MaxDunnGroups
                        ? LT($"Skipped: more than {NonparametricTests.MaxDunnGroups} groups ({r.Groups.Count:N0}). Check that the group column is not an identifier.",
                             $"생략: 그룹이 {NonparametricTests.MaxDunnGroups}개를 넘습니다({r.Groups.Count:N0}개). 그룹 컬럼이 식별자가 아닌지 확인하세요.")
                        : LT("Pairwise standard errors are undefined (no rank variance).", "쌍별 표준오차를 정의할 수 없습니다(순위 분산 없음)."));
                }
                else
                {
                    var ordered = r.Dunn
                        .OrderBy(d => d.PBonferroni)
                        .ThenByDescending(d => Math.Abs(d.Z))
                        .ThenBy(d => d.IndexI)
                        .ThenBy(d => d.IndexJ)
                        .ToList();
                    int show = Math.Min(DunnDisplayCap, ordered.Count);
                    var pairs = new TextTable(LT("Group i", "그룹 i"), LT("Group j", "그룹 j"), "z", "p", LT("p Bonf.", "p Bonf."));
                    for (int i = 0; i < show; i++)
                    {
                        var d = ordered[i];
                        pairs.AddRow(Clip(r.Groups[d.IndexI].Name), Clip(r.Groups[d.IndexJ].Name),
                            StatFormat.G(d.Z), StatFormat.P(d.P), StatFormat.P(d.PBonferroni));
                    }
                    sb.Append(pairs.Render());
                    if (ordered.Count > show)
                        sb.AppendLine(LT(
                            $"Showing {show} of {ordered.Count} pairs (smallest adjusted p). Bonferroni still uses all {r.Comparisons} comparisons.",
                            $"{ordered.Count}쌍 중 조정 p가 작은 {show}쌍만 표시합니다. Bonferroni는 {r.Comparisons}개 비교 전체를 사용합니다."));
                }
                return sb.ToString();
            });
        }

        private async void AdvFriedman()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var cols = PickRelatedColumns(LT("Friedman", "Friedman"), 3, NonparametricTests.MaxFriedmanColumns, LT(
                "Select 3 or more numeric columns (one row = one block). Incomplete rows are dropped. At most 32 columns.",
                "수치 컬럼을 3개 이상 선택하세요(한 행 = 한 블록). 결측 행은 제외됩니다. 최대 32열."));
            if (cols is null) return;
            string title = LT("Friedman", "Friedman");
            var names = cols.Select(c => AdvHeaders()[c]).ToArray();
            await RunAdvancedAsync(title, input =>
            {
                var r = NonparametricTests.Friedman(input.Rows, cols, names, input.Cancellation);
                var sb = new StringBuilder();
                sb.AppendLine(AdvScope(r.RowsRead, r.RowsUsed, r.RowsDropped));
                sb.AppendLine();
                sb.AppendLine(LT(
                    $"Friedman test, {r.K} conditions, listwise complete rows (scipy.stats.friedmanchisquare, tie correction).",
                    $"Friedman 검정, 조건 {r.K}개, 목록별 완전 행 (scipy.stats.friedmanchisquare, 동점 보정)."));
                sb.AppendLine();
                var conds = new TextTable(LT("Condition", "조건"), LT("Rank sum", "순위합"), LT("Mean rank", "평균 순위"));
                foreach (var c in r.Conditions)
                    conds.AddRow(Clip(c.Name), StatFormat.F(c.RankSum, 2), StatFormat.F(c.MeanRank, 3));
                sb.Append(conds.Render());
                sb.AppendLine();
                var stats = new TextTable(LT("Statistic", "통계량"), LT("Value", "값"));
                stats.AddRow("χ²", StatFormat.G(r.ChiSquare));
                stats.AddRow("df", StatFormat.Int(r.Df));
                stats.AddRow("p", PStars(r.P));
                stats.AddRow(LT("Kendall's W", "Kendall W"), StatFormat.G(r.KendallsW));
                stats.AddRow("n", StatFormat.Int(r.N));
                sb.Append(stats.Render());
                sb.AppendLine();
                sb.AppendLine(LT(
                    "W = χ² / (n·(k−1)), using the tie-corrected Friedman χ².",
                    "W = χ² / (n·(k−1)). χ²는 동점 보정된 Friedman 통계량입니다."));
                if (r.HasTies)
                    sb.AppendLine(LT("Ties within blocks were corrected.", "블록 안 동점을 보정했습니다."));
                if (r.SmallSample)
                    sb.AppendLine(LT(
                        "Warning: n ≤ 10. The chi-square approximation is more reliable for larger n.",
                        "경고: n ≤ 10. 카이제곱 근사는 n이 클수록 안정적입니다."));
                return sb.ToString();
            });
        }

        private async void AdvRmAnova()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var cols = PickRelatedColumns(LT("Repeated-Measures ANOVA", "반복측정 분산분석"), 2, RepeatedMeasuresAnova.MaxConditions, LT(
                "Wide format: one row = one subject, each column = one condition. Select 2 or more numeric columns (3 or more for a sphericity test). At most 32 columns.",
                "넓은 형식: 한 행 = 한 피험자, 각 컬럼 = 한 조건. 수치 컬럼을 2개 이상 선택하세요(구형성 검정은 3개 이상). 최대 32열."));
            if (cols is null) return;
            string title = LT("Repeated-Measures ANOVA", "반복측정 분산분석");
            var names = cols.Select(c => AdvHeaders()[c]).ToArray();
            await RunAdvancedAsync(title, input =>
            {
                var r = RepeatedMeasuresAnova.Fit(input.Rows, cols, names, input.Cancellation);
                var sb = new StringBuilder();
                sb.AppendLine(AdvScope(r.RowsRead, r.RowsUsed, r.RowsDropped));
                sb.AppendLine();
                sb.AppendLine(LT(
                    $"One-way repeated-measures ANOVA, {r.K} conditions, n = {r.N} (listwise). F matches statsmodels AnovaRM.",
                    $"일원 반복측정 분산분석, 조건 {r.K}개, n = {r.N} (목록별 삭제). F는 statsmodels AnovaRM과 같습니다."));
                sb.AppendLine();
                var conds = new TextTable(LT("Condition", "조건"), LT("Mean", "평균"), "SD");
                foreach (var c in r.Conditions)
                    conds.AddRow(Clip(c.Name), StatFormat.G(c.Mean), StatFormat.G(c.StandardDeviation));
                sb.Append(conds.Render());
                sb.AppendLine();
                var anova = new TextTable(LT("Source", "요인"), "SS", "df", "MS", "F", "p");
                anova.AddRow(LT("Subjects", "피험자"), StatFormat.G(r.SsSubjects), StatFormat.Int(r.N - 1), "—", "—", "—");
                anova.AddRow(LT("Conditions", "조건"), StatFormat.G(r.SsConditions), StatFormat.G(r.K - 1),
                    StatFormat.G(r.MeanSquareConditions), StatFormat.G(r.F), PStars(r.P));
                anova.AddRow(LT("Error", "오차"), StatFormat.G(r.SsError), StatFormat.G((r.N - 1.0) * (r.K - 1)),
                    StatFormat.G(r.MeanSquareError), "—", "—");
                sb.Append(anova.Render());
                sb.AppendLine(LT($"partial η² = {StatFormat.G(r.PartialEtaSquared)}   (SS_conditions / (SS_conditions + SS_error))",
                    $"부분 η² = {StatFormat.G(r.PartialEtaSquared)}   (SS_조건 / (SS_조건 + SS_오차))"));
                if (double.IsNaN(r.F))
                    sb.AppendLine(LT(
                        "F is undefined: there is no variation among conditions or residuals.",
                        "F를 정의할 수 없습니다. 조건과 잔차에 변동이 없습니다."));
                else if (double.IsPositiveInfinity(r.F))
                    sb.AppendLine(LT(
                        "Residual sum of squares is 0 and conditions differ, so F is infinite and p is 0.",
                        "잔차 제곱합이 0이고 조건 평균이 달라 F는 무한대, p는 0입니다."));
                sb.AppendLine();
                if (r.SphericityTrivial)
                {
                    sb.AppendLine(LT(
                        "Sphericity is trivially satisfied (2 conditions, one contrast). Mauchly's test is not applicable. Greenhouse-Geisser and Huynh-Feldt ε are 1, so the corrected p equals the uncorrected p.",
                        "조건이 2개(대비 1개)라 구형성은 자명하게 성립합니다. Mauchly 검정은 해당 없습니다. Greenhouse-Geisser·Huynh-Feldt ε은 1이며, 보정 p는 보정 전 p와 같습니다."));
                    return sb.ToString();
                }
                sb.AppendLine(LT("Sphericity (orthonormal Helmert contrasts of the sample covariance).",
                    "구형성 (표본 공분산의 정규직교 Helmert 대비)."));
                var sph = new TextTable(LT("Test", "검정"), "ε / W", "χ²", "df", "p", LT("df num", "분자 df"), LT("df den", "분모 df"));
                if (r.MauchlyDefined)
                    sph.AddRow("Mauchly", StatFormat.G(r.MauchlyW), StatFormat.G(r.MauchlyChiSquare),
                        StatFormat.G(r.MauchlyDf), StatFormat.P(r.MauchlyP), "—", "—");
                else
                    sph.AddRow("Mauchly", "—", "—", StatFormat.G(r.MauchlyDf), "—", "—", "—");
                sph.AddRow("GG", StatFormat.G(r.GreenhouseGeisser), "—", "—", PStars(r.GgP),
                    StatFormat.G(r.GgDfNum), StatFormat.G(r.GgDfDen));
                sph.AddRow(r.HuynhFeldtCapped ? "HF*" : "HF",
                    StatFormat.G(r.HuynhFeldt), "—", "—", PStars(r.HfP),
                    StatFormat.G(r.HfDfNum), StatFormat.G(r.HfDfDen));
                sb.Append(sph.Render());
                if (!r.MauchlyDefined)
                    sb.AppendLine(LT(
                        "Mauchly's W is not reported: the contrast covariance is singular (need more subjects than conditions, or the measures are linearly dependent).",
                        "Mauchly W는 보고하지 않습니다. 대비 공분산이 특이합니다(피험자가 조건 수보다 적거나 측정이 선형 종속)."));
                if (r.HuynhFeldtCapped)
                    sb.AppendLine(LT(
                        "HF* : Huynh-Feldt ε was above 1 (or the denominator was not positive) and is capped at 1.",
                        "HF* : Huynh-Feldt ε이 1을 넘었거나(분모가 양이 아님) 1로 잘랐습니다."));
                else if (!r.HuynhFeldtDefined)
                    sb.AppendLine(LT(
                        "Huynh-Feldt ε is not reported: the estimate was not positive for this sample size. Use Greenhouse-Geisser.",
                        "Huynh-Feldt ε은 보고하지 않습니다. 이 표본 크기에서 추정이 양이 아닙니다. Greenhouse-Geisser를 사용하세요."));
                sb.AppendLine(LT(
                    "ε_GG = tr(Σ)² / ((k−1)·‖Σ‖²). ε_HF = (n(k−1)ε_GG − 2) / ((k−1)(n − 1 − (k−1)ε_GG)), capped at 1.",
                    "ε_GG = tr(Σ)² / ((k−1)·‖Σ‖²). ε_HF = (n(k−1)ε_GG − 2) / ((k−1)(n − 1 − (k−1)ε_GG)), 상한 1."));
                return sb.ToString();
            });
        }

        private bool RequireDistinctPair(int c1, int c2, string title)
        {
            if (c1 < 0 || c2 < 0)
            {
                ShowResult(title, LT("Select two columns.", "컬럼을 둘 선택하세요."));
                return false;
            }
            if (c1 == c2)
            {
                ShowResult(title, LT("The two columns must be different.", "두 컬럼은 달라야 합니다."));
                return false;
            }
            return true;
        }

        /// <summary>관련 측정 열을 고른다. 취소·선택 부족이면 null.</summary>
        private int[]? PickRelatedColumns(string title, int minimum, int maximum, string note)
        {
            using var dlg = new ParamDialog(title, _palette);
            var list = dlg.AddCheckedList(LT("Columns", "컬럼"), ColumnLabels(), Math.Min(10, Math.Max(4, _doc!.ColumnCount)));
            int numeric = 0;
            for (int c = 0; c < _columnSummaries.Length && c < list.Items.Count; c++)
                if (IsNumericColumn(c)) numeric++;
            if (numeric >= minimum && numeric <= 8)
            {
                for (int c = 0; c < list.Items.Count; c++)
                    if (IsNumericColumn(c)) list.SetItemChecked(c, true);
            }
            dlg.AddNote(note);
            if (!dlg.ShowOk(this)) return null;
            var cols = CheckedIndexes(list);
            if (cols.Count < minimum)
            {
                ShowResult(title, LT($"Select at least {minimum} columns.", $"컬럼을 {minimum}개 이상 선택하세요."));
                return null;
            }
            if (cols.Count > maximum)
            {
                ShowResult(title, LT($"Select at most {maximum} columns.", $"컬럼은 최대 {maximum}개까지 선택할 수 있습니다."));
                return null;
            }
            var nonNumeric = cols.Where(c => !IsNumericColumn(c)).ToList();
            if (nonNumeric.Count > 0)
            {
                ShowResult(title, LT(
                    "Every selected column must be numeric. Change the column type or uncheck non-numeric columns.",
                    "선택한 컬럼은 모두 수치형이어야 합니다. 타입을 바꾸거나 비수치 컬럼의 체크를 해제하세요."));
                return null;
            }
            return cols.ToArray();
        }

        private static string PStars(double p) => StatFormat.P(p) + StatFormat.Stars(p);

        private static string Clip(string name)
        {
            if (string.IsNullOrEmpty(name)) return LT("(blank)", "(빈값)");
            return name.Length <= 18 ? name : name[..17] + "…";
        }
    }
}
