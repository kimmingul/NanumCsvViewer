using System.Globalization;
using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer;

/// <summary>
/// The worker-side bodies of the basic analyses (Analysis ▸ …). Each analysis declares the columns it reads
/// (<c>…Columns</c>); <c>Form1.RunAnalysisAsync</c> snapshots only those columns, so an analysis MUST NOT read
/// any other cell of <see cref="AnalysisWork.Rows"/> (the rest are null).
/// </summary>
internal static class BasicAnalyses
{
    private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

    // 접사 인식 파서(NumericAffix)로 통일 — 타입 시스템(Currency/Percent=numeric)·컬럼 필터·차트 빌더와
    // 같은 기준. 통화 컬럼에서 "결과창은 값 없음인데 차트로 보기는 전수 집계"가 되는 모순 방지(이슈 #19 리뷰).
    internal static List<double> NumericColumn(IEnumerable<string[]> rows, int col)
    {
        var values = new List<double>();
        foreach (var row in rows)
            if (col < row.Length && NumericAffix.TryParseNumber(row[col], out double d) && double.IsFinite(d))
                values.Add(d);
        return values;
    }

    // ------------------------------------------------------------ 수치 분포

    internal static int[] DistributionColumns(int column) => new[] { column };

    internal static void Distribution(AnalysisWork work, int colSelection, int bins)
    {
        var rows = work.Rows;
        var values = NumericColumn(rows, colSelection);
        if (values.Count == 0) { work.SetResult(LT("Numeric Distribution", "수치 분포"), LT("No numeric values.", "수치 값이 없습니다.")); return; }

        var d = CsvAnalytics.NumericDistributionOf(values, colSelection, bins);
        var sb = new StringBuilder();
        sb.AppendLine(work.ColumnLabel(colSelection));
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"count  {d.Count:N0}");
        sb.AppendLine($"min    {d.Min:G6}");
        sb.AppendLine($"q1     {d.Q1:G6}");
        sb.AppendLine($"median {d.Median:G6}");
        sb.AppendLine($"mean   {d.Mean:G6}");
        sb.AppendLine($"q3     {d.Q3:G6}");
        sb.AppendLine($"max    {d.Max:G6}");
        sb.AppendLine($"std    {d.StandardDeviation:G6}");
        sb.AppendLine();
        sb.AppendLine(LT("Histogram", "히스토그램") + ":");
        int maxCount = d.Bins.Count > 0 ? d.Bins.Max(b => b.Count) : 0;
        foreach (var b in d.Bins)
        {
            int barLen = maxCount > 0 ? b.Count * 30 / maxCount : 0;
            sb.AppendLine($"[{b.LowerBound,10:G5} – {b.UpperBound,10:G5}) {b.Count,8:N0} {new string('█', barLen)}");
        }
        work.SetChartResult(LT("Numeric Distribution", "수치 분포"), sb.ToString(),
            ChartKind.Histogram, new[] { colSelection });
    }

    // ------------------------------------------------------------ 날짜 히스토그램

    /// <param name="valueColSelection">콤보 선택 인덱스: 0 = (없음), n ≥ 1 = 컬럼 n-1.</param>
    internal static int[] DateHistogramColumns(int dateColSelection, int valueColSelection)
        => valueColSelection == 0 ? new[] { dateColSelection } : new[] { dateColSelection, valueColSelection - 1 };

    internal static void DateHistogram(AnalysisWork work, int dateColSelection, int valueColSelection, int periodSelection)
    {
        var rows = work.Rows;
        int? vc = valueColSelection == 0 ? null : valueColSelection - 1;
        var p = (DateBinPeriod)periodSelection;
        var hist = CsvAnalytics.DateHistogramOf(rows, dateColSelection, vc, p);
        var sb = new StringBuilder();
        sb.AppendLine($"{work.ColumnLabel(dateColSelection)} · {p}");
        sb.AppendLine(new string('─', 40));
        foreach (var b in hist.Bins)
        {
            sb.Append($"{b.Label,-12} {b.Count,8:N0}");
            if (b.Sum is double s) sb.Append($"  sum={s:G6}  avg={b.Average:G6}");
            sb.AppendLine();
        }
        work.SetResult(LT("Date Histogram", "날짜 히스토그램"), sb.ToString());
    }

    // ------------------------------------------------------------ 중복 찾기 (work.SourceRows 사용)

    internal static int[] DuplicatesColumns(IReadOnlyList<int> keys) => keys.ToArray();

    internal static void Duplicates(AnalysisWork work, IReadOnlyList<int> keys)
    {
        var rows = work.SourceRows;
        var dups = CsvAnalytics.FindDuplicates(rows, keys, work.Cancellation);
        var sb = new StringBuilder();
        sb.AppendLine(LT($"Duplicate groups: {dups.Count:N0}", $"중복 그룹: {dups.Count:N0}"));
        sb.AppendLine(new string('─', 40));
        foreach (var g in dups.Take(1000))
            sb.AppendLine($"{string.Join(" | ", g.Key)}  ×{g.SourceRows.Count}  → " +
                LT("rows ", "행 ") + string.Join(", ", g.SourceRows.Take(20)) + (g.SourceRows.Count > 20 ? " …" : ""));
        if (dups.Count > 1000) sb.AppendLine("…");
        work.SetResult(LT("Find Duplicates", "중복 찾기"), sb.ToString());
    }

    // ------------------------------------------------------------ 그룹별 집계

    internal static int[] GroupByColumns(IReadOnlyList<int> groups, int valueColSelection)
        => groups.Append(valueColSelection).Distinct().ToArray();

    internal static void GroupBy(AnalysisWork work, IReadOnlyList<int> groups, int valueColSelection,
        IReadOnlyList<AggregationFunction> funcs)
    {
        var rows = work.Rows;
        var result = CsvAnalytics.GroupBy(rows, groups, valueColSelection, funcs, work.Cancellation);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(" | ", groups.Select(work.ColumnLabel)) + "  →  " + string.Join(", ", funcs.Select(f => f.DisplayName())));
        sb.AppendLine(new string('─', 50));
        foreach (var r in result.Rows.Take(5000))
            sb.AppendLine($"{string.Join(" | ", r.Key),-30}  " +
                string.Join("  ", funcs.Select(f => $"{f.DisplayName()}={r.Values[f]:G6}")));
        if (result.Rows.Count > 5000) sb.AppendLine("…");
        work.SetResult(LT("Group By", "그룹별 집계"), sb.ToString());
    }

    // ------------------------------------------------------------ 상관분석

    internal static int[] CorrelationColumns(int x, int y) => new[] { x, y };

    internal static void Correlation(AnalysisWork work, int xSelection, int ySelection, int methodSelection)
    {
        var rows = work.Rows;
        var pairs = new List<(double, double)>();
        foreach (var row in rows)
            if (xSelection < row.Length && ySelection < row.Length &&
                NumericAffix.TryParseNumber(row[xSelection], out double xv) &&
                NumericAffix.TryParseNumber(row[ySelection], out double yv))
                pairs.Add((xv, yv));

        var r = CsvStatistics.Correlation(pairs, (CorrelationMethod)methodSelection);
        var sb = new StringBuilder();
        sb.AppendLine($"{work.ColumnLabel(xSelection)}  vs  {work.ColumnLabel(ySelection)}");
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"method       {r.Method}");
        sb.AppendLine($"coefficient  {r.Coefficient:0.0000}");
        sb.AppendLine($"p-value      {r.PValue:0.0000}");
        sb.AppendLine($"sample size  {r.SampleSize:N0}");
        sb.AppendLine($"→ {r.Interpretation}");
        work.SetChartResult(LT("Correlation", "상관분석"), sb.ToString(),
            ChartKind.Scatter, new[] { xSelection, ySelection });
    }

    // ------------------------------------------------------------ 독립표본 t검정

    internal static int[] IndependentTTestColumns(int valueCol, int groupCol) => new[] { valueCol, groupCol };

    internal static void IndependentTTest(AnalysisWork work, int valueColSelection, int groupColSelection)
    {
        var rows = work.Rows;
        var groups = new Dictionary<string, List<double>>();
        int vc = valueColSelection, gc = groupColSelection;
        foreach (var row in rows)
        {
            if (vc >= row.Length || gc >= row.Length) continue;
            if (!NumericAffix.TryParseNumber(row[vc], out double v)) continue;
            string g = row[gc];
            if (!groups.TryGetValue(g, out var listv)) { listv = new List<double>(); groups[g] = listv; }
            listv.Add(v);
        }
        var top = groups.OrderByDescending(kv => kv.Value.Count).Take(2).ToList();
        if (top.Count < 2) { work.SetResult(LT("Independent t-test", "독립표본 t검정"), LT("Need at least 2 groups.", "그룹이 2개 이상 필요합니다.")); return; }

        var r = CsvStatistics.IndependentTTest(top[0].Key, top[0].Value, top[1].Key, top[1].Value);
        var sb = new StringBuilder();
        sb.AppendLine($"{work.ColumnLabel(vc)} by {work.ColumnLabel(gc)}");
        if (groups.Count > 2)
            sb.AppendLine(LT($"Using the two largest groups; {groups.Count - 2:N0} other groups are excluded.",
                $"빈도 상위 두 그룹을 비교합니다. 나머지 {groups.Count - 2:N0}개 그룹은 제외됩니다."));
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"group A      {r.GroupA} (mean {r.MeanA:G6}, n {top[0].Value.Count})");
        sb.AppendLine($"group B      {r.GroupB} (mean {r.MeanB:G6}, n {top[1].Value.Count})");
        sb.AppendLine($"t            {r.TStatistic:0.0000}");
        sb.AppendLine($"df           {r.DegreesOfFreedom:0.00}");
        sb.AppendLine($"p-value      {r.PValue:0.0000}");
        sb.AppendLine($"95% CI       [{r.ConfidenceIntervalLow:G6}, {r.ConfidenceIntervalHigh:G6}]");
        sb.AppendLine($"Cohen's d    {r.EffectSize:0.0000}");
        sb.AppendLine($"→ {r.Interpretation}");
        work.SetChartResult(LT("Independent t-test", "독립표본 t검정"), sb.ToString(),
            ChartKind.BoxPlot, new[] { vc, gc });
    }

    // ------------------------------------------------------------ 대응표본 t검정

    internal static int[] PairedTTestColumns(int before, int after) => new[] { before, after };

    internal static void PairedTTest(AnalysisWork work, int beforeSelection, int afterSelection)
    {
        var rows = work.Rows;
        var b = new List<double>(); var a = new List<double>();
        foreach (var row in rows)
        {
            if (beforeSelection >= row.Length || afterSelection >= row.Length) continue;
            if (NumericAffix.TryParseNumber(row[beforeSelection], out double bv) &&
                NumericAffix.TryParseNumber(row[afterSelection], out double av))
            { b.Add(bv); a.Add(av); }
        }
        if (b.Count < 2) { work.SetResult(LT("Paired t-test", "대응표본 t검정"), LT("Need at least 2 paired values.", "쌍을 이룬 값이 2개 이상 필요합니다.")); return; }

        var r = CsvStatistics.PairedTTest(b, a);
        var sb = new StringBuilder();
        sb.AppendLine($"{work.ColumnLabel(beforeSelection)} → {work.ColumnLabel(afterSelection)}");
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"mean diff    {r.MeanDifference:G6}");
        sb.AppendLine($"t            {r.TStatistic:0.0000}");
        sb.AppendLine($"df           {r.DegreesOfFreedom:0.00}");
        sb.AppendLine($"p-value      {r.PValue:0.0000}");
        sb.AppendLine($"95% CI       [{r.ConfidenceIntervalLow:G6}, {r.ConfidenceIntervalHigh:G6}]");
        sb.AppendLine($"→ {r.Interpretation}");
        work.SetResult(LT("Paired t-test", "대응표본 t검정"), sb.ToString());
    }

    // ------------------------------------------------------------ 카이제곱

    internal static int[] ChiSquareColumns(int rowCol, int colCol) => new[] { rowCol, colCol };

    internal static void ChiSquare(AnalysisWork work, int rowColSelection, int colColSelection)
    {
        var rows = work.Rows;
        var pairs = new List<(string, string)>();
        foreach (var row in rows)
            if (rowColSelection < row.Length && colColSelection < row.Length)
                pairs.Add((row[rowColSelection], row[colColSelection]));

        var r = CsvStatistics.ChiSquare(pairs, work.Cancellation);
        var sb = new StringBuilder();
        sb.AppendLine($"{work.ColumnLabel(rowColSelection)} × {work.ColumnLabel(colColSelection)}");
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"χ²           {r.Statistic:0.0000}");
        sb.AppendLine($"df           {r.DegreesOfFreedom}");
        sb.AppendLine($"min expected {r.MinimumExpectedCount:G6}");
        sb.AppendLine($"expected <5  {r.ExpectedCellsBelowFive:N0}");
        if (r.HasReliableApproximation) sb.AppendLine($"p-value      {r.PValue:0.0000}");
        sb.AppendLine(r.HasReliableApproximation ? $"→ {r.Interpretation}" :
            LT("Expected counts are too sparse (or there is only one category). The approximate p-value and significance interpretation are not reported.",
                "기대빈도가 너무 작거나 범주가 하나뿐입니다. 근사 p값과 유의성 해석은 표시하지 않습니다."));
        work.SetResult(LT("Chi-square", "카이제곱 검정"), sb.ToString());
    }

    // ------------------------------------------------------------ 기술통계

    internal static int[] DescriptivesColumns(IReadOnlyList<int> cols) => cols.Distinct().ToArray();

    internal static void Descriptives(AnalysisWork work, IReadOnlyList<int> cols)
    {
        var rows = work.Rows;
        var sb = new StringBuilder();
        foreach (int c in cols)
        {
            var values = NumericColumn(rows, c);
            sb.AppendLine(work.ColumnLabel(c));
            sb.AppendLine(new string('─', 44));
            var d = CsvStatistics.Describe(values);
            if (d is null)
            {
                sb.AppendLine(LT("No numeric values.", "수치 값이 없습니다."));
                sb.AppendLine();
                continue;
            }
            int missing = rows.Count - d.Count;
            sb.AppendLine($"N (valid)    {d.Count:N0}");
            sb.AppendLine(LT($"missing      {missing:N0}", $"결측/비수치   {missing:N0}"));
            sb.AppendLine($"sum          {d.Sum:G6}");
            sb.AppendLine($"mean         {d.Mean:G6}");
            sb.AppendLine($"sd           {d.StandardDeviation:G6}");
            sb.AppendLine($"se           {d.StandardError:G6}");
            sb.AppendLine($"95% CI       [{d.ConfidenceIntervalLow:G6}, {d.ConfidenceIntervalHigh:G6}]");
            sb.AppendLine($"min          {d.Min:G6}");
            sb.AppendLine($"q1           {d.Q1:G6}");
            sb.AppendLine($"median       {d.Median:G6}");
            sb.AppendLine($"q3           {d.Q3:G6}");
            sb.AppendLine($"max          {d.Max:G6}");
            sb.AppendLine($"range        {d.Range:G6}");
            sb.AppendLine($"IQR          {d.InterquartileRange:G6}");
            if (d.Modes.Count > 0)
                sb.AppendLine(LT("mode         ", "최빈값        ").TrimEnd() + "  " +
                    string.Join(", ", d.Modes.Take(3).Select(m => m.ToString("G6", CultureInfo.InvariantCulture))) +
                    (d.Modes.Count > 3 ? " …" : "") + $"  (×{d.ModeFrequency})");
            if (!double.IsNaN(d.Skewness)) sb.AppendLine($"skewness     {d.Skewness:0.0000}");
            if (!double.IsNaN(d.ExcessKurtosis)) sb.AppendLine($"kurtosis     {d.ExcessKurtosis:0.0000}");
            if (!double.IsNaN(d.CoefficientOfVariation)) sb.AppendLine($"CV           {d.CoefficientOfVariation:0.0000}");
            sb.AppendLine();
        }
        work.SetChartResult(LT("Descriptive Statistics", "기술통계"), sb.ToString(),
            ChartKind.Histogram, new[] { cols[0] });
    }

    // ------------------------------------------------------------ 빈도분석

    internal static int[] FrequencyColumns(int column) => new[] { column };

    internal static void Frequency(AnalysisWork work, int colSelection, int limit)
    {
        var rows = work.Rows;
        int c = colSelection;
        var values = new List<string>(rows.Count);
        foreach (var row in rows) values.Add(c < row.Length ? row[c] : string.Empty);

        var t = CsvStatistics.FrequencyTable(values);
        var sb = new StringBuilder();
        sb.AppendLine(work.ColumnLabel(c));
        sb.AppendLine(LT($"total {t.TotalCount:N0} · unique {t.UniqueCount:N0}", $"전체 {t.TotalCount:N0} · 고유값 {t.UniqueCount:N0}"));
        sb.AppendLine(new string('─', 56));
        sb.AppendLine(LT($"{"value",-24} {"count",8} {"%",8} {"cum%",8}", $"{"값",-24} {"빈도",8} {"%",8} {"누적%",8}"));
        foreach (var e in t.Entries.Take(limit))
        {
            string label = e.Value.Length == 0 ? LT("(empty)", "(빈값)") : e.Value;
            if (label.Length > 24) label = label[..23] + "…";
            sb.AppendLine($"{label,-24} {e.Count,8:N0} {e.Percent,7:0.00}% {e.CumulativePercent,7:0.00}%");
        }
        if (t.Entries.Count > limit)
            sb.AppendLine(LT($"… {t.Entries.Count - limit:N0} more values", $"… 외 {t.Entries.Count - limit:N0}개 값"));
        work.SetChartResult(LT("Frequency Table", "빈도분석"), sb.ToString(),
            ChartKind.Pareto, new[] { c });
    }

    // ------------------------------------------------------------ 일원배치 분산분석

    internal static int[] OneWayAnovaColumns(int valueCol, int groupCol) => new[] { valueCol, groupCol };

    internal static void OneWayAnova(AnalysisWork work, int valueColSelection, int groupColSelection)
    {
        var rows = work.Rows;
        int vc = valueColSelection, gc = groupColSelection;
        var obs = new List<(string, double)>();
        foreach (var row in rows)
            if (vc < row.Length && gc < row.Length &&
                NumericAffix.TryParseNumber(row[vc], out double v))
                obs.Add((row[gc].Trim(), v));

        var r = CsvStatistics.OneWayAnova(obs);
        if (r is null) { work.SetResult(LT("One-way ANOVA", "일원배치 분산분석"), LT("Need at least 2 groups with numeric values.", "수치 값을 가진 그룹이 2개 이상 필요합니다.")); return; }

        var sb = new StringBuilder();
        sb.AppendLine($"{work.ColumnLabel(vc)} by {work.ColumnLabel(gc)}");
        sb.AppendLine(new string('─', 44));
        foreach (var g in r.Groups.Take(30))
            sb.AppendLine($"  {g.Name,-16} n={g.Count,-6:N0} mean={g.Mean:G6}  sd={g.StandardDeviation:G6}");
        if (r.Groups.Count > 30) sb.AppendLine($"  … +{r.Groups.Count - 30:N0}");
        sb.AppendLine();
        sb.AppendLine($"F            {r.FStatistic:0.0000}");
        sb.AppendLine($"df           {r.DfBetween}, {r.DfWithin}");
        sb.AppendLine($"p-value      {r.PValue:0.0000}");
        sb.AppendLine($"η² (eta²)    {r.EtaSquared:0.0000}");
        sb.AppendLine($"→ {r.Interpretation}");
        work.SetChartResult(LT("One-way ANOVA", "일원배치 분산분석"), sb.ToString(),
            ChartKind.BoxPlot, new[] { vc, gc });
    }

    // ------------------------------------------------------------ 정규성 검정

    internal static int[] NormalityColumns(int column) => new[] { column };

    internal static void Normality(AnalysisWork work, int colSelection)
    {
        var rows = work.Rows;
        var values = NumericColumn(rows, colSelection);
        // Royston p값 근사는 n≤5000에서 검증됨 → 초과 시 앞 5,000개만 사용하고 표기.
        const int swCap = 5000;
        bool capped = values.Count > swCap;
        if (capped) values = values.Take(swCap).ToList();

        var r = CsvStatistics.ShapiroWilk(values);
        if (r is null)
        {
            work.SetResult(LT("Normality Test", "정규성 검정"),
                LT("Need at least 3 distinct numeric values.", "서로 다른 수치 값이 3개 이상 필요합니다."));
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine(work.ColumnLabel(colSelection));
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"n            {r.SampleSize:N0}" + (capped ? LT("  (first 5,000)", "  (처음 5,000개)") : ""));
        sb.AppendLine($"W            {r.W:0.0000}");
        sb.AppendLine($"p-value      {r.PValue:0.0000}");
        sb.AppendLine($"→ {r.Interpretation}");
        work.SetChartResult(LT("Normality Test (Shapiro-Wilk)", "정규성 검정(Shapiro-Wilk)"), sb.ToString(),
            ChartKind.QqPlot, new[] { colSelection });
    }
}
