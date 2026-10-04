using System.Text.Json.Nodes;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Agent.Tools
{
    internal sealed record NumericColumnStat(
        long Count, double Mean, double StandardDeviation, double Min, double Q1, double Median, double Q3, double Max);

    internal sealed record ColumnStatResult(
        AgentColumn Column,
        long Rows,
        long Missing,
        long Distinct,
        bool DistinctIsLowerBound,
        long NonNumeric,
        NumericColumnStat? Numeric,
        IReadOnlyList<(string Value, long Count)>? TopValues,
        bool TopApproximate);

    /// <summary>
    /// 현재 뷰 행에서 컬럼별 집계(결측·고유값·수치 요약·상위 빈도)를 계산한다. 순수 함수 — UI 없음.
    /// 수치 컬럼은 정확한 분위수를 위해 값을 모으므로 값 수 예산 안에서 컬럼을 묶어 여러 패스로 나눠 읽는다.
    /// </summary>
    internal static class ColumnStatsCalculator
    {
        /// <summary>컬럼당 고유값 추적 상한. 넘으면 고유값 수는 하한(≥)이고 상위 빈도는 근사다.</summary>
        public const int MaxDistinctTracked = 50_000;

        /// <summary>한 패스에서 모으는 수치 값 총량(≈160 MB).</summary>
        public const long NumericValueBudget = 20_000_000;

        public static List<ColumnStatResult> Compute(AgentViewData data, IReadOnlyList<int> columns, int topN, CancellationToken cancellation)
        {
            var rows = data.Rows;
            long n = rows.Count;
            var results = new Dictionary<int, ColumnStatResult>();

            int numericPerPass = (int)Math.Clamp(NumericValueBudget / Math.Max(1, n), 1, int.MaxValue);
            var numericCols = columns.Where(c => data.Types[c].IsNumeric()).ToList();
            var otherCols = columns.Where(c => !data.Types[c].IsNumeric()).ToList();

            var batches = new List<List<int>>();
            for (int i = 0; i < numericCols.Count; i += numericPerPass)
                batches.Add(numericCols.GetRange(i, Math.Min(numericPerPass, numericCols.Count - i)));
            if (batches.Count == 0) batches.Add(new List<int>());
            batches[0].AddRange(otherCols);

            foreach (var batch in batches)
            {
                if (batch.Count == 0) continue;
                foreach (var r in OnePass(data, batch, topN, cancellation)) results[r.Column.Index] = r;
            }
            return columns.Select(c => results[c]).ToList();
        }

        private sealed class Acc
        {
            public int Col;
            public bool Numeric;
            public long Missing, NonNumeric;
            public List<double>? Values;
            public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);
            public bool Capped;
        }

        private static IEnumerable<ColumnStatResult> OnePass(AgentViewData data, List<int> cols, int topN, CancellationToken ct)
        {
            var accs = cols.Select(c => new Acc { Col = c, Numeric = data.Types[c].IsNumeric() }).ToArray();
            foreach (var a in accs) if (a.Numeric) a.Values = new List<double>();

            long rowCount = 0;
            foreach (var row in data.Rows)
            {
                if ((rowCount & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                rowCount++;
                foreach (var a in accs)
                {
                    string v = a.Col < row.Length ? row[a.Col] : "";
                    if (StatValue.IsMissing(v)) { a.Missing++; continue; }
                    if (a.Numeric)
                    {
                        if (NumericAffix.TryParseNumber(v, out double d) && double.IsFinite(d)) a.Values!.Add(d);
                        else a.NonNumeric++;
                    }
                    string key = v.Trim();
                    if (a.Counts.TryGetValue(key, out long c)) a.Counts[key] = c + 1;
                    else if (a.Counts.Count < MaxDistinctTracked) a.Counts[key] = 1;
                    else a.Capped = true;
                }
            }

            foreach (var a in accs)
            {
                NumericColumnStat? num = null;
                if (a.Numeric && a.Values!.Count > 0 && CsvStatistics.Describe(a.Values) is { } d)
                    num = new NumericColumnStat(d.Count, d.Mean, d.StandardDeviation, d.Min, d.Q1, d.Median, d.Q3, d.Max);

                IReadOnlyList<(string, long)>? top = null;
                if (topN > 0)
                    top = a.Counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                        .Take(topN).Select(kv => (kv.Key, kv.Value)).ToList();

                yield return new ColumnStatResult(
                    new AgentColumn(a.Col, data.Headers[a.Col], data.Types[a.Col]),
                    rowCount, a.Missing, a.Counts.Count, a.Capped, a.NonNumeric, num, top, a.Capped);
            }
        }

        /// <summary>
        /// 결과 JSON. includeTop=false면 상위 빈도(원시 셀 값)를 넣지 않는다 — 수치 요약은 집계이므로 항상 포함한다.
        /// </summary>
        public static JsonArray ToJson(IReadOnlyList<ColumnStatResult> stats, bool includeTop, int valueClip = 60)
        {
            var arr = new JsonArray();
            foreach (var s in stats)
            {
                var o = new JsonObject
                {
                    ["name"] = s.Column.Name,
                    ["type"] = s.Column.Type.DisplayName(),
                    ["rows"] = s.Rows,
                    ["missing"] = s.Missing,
                    ["distinct"] = s.Distinct,
                };
                if (s.DistinctIsLowerBound) o["distinct_is_lower_bound"] = true;
                if (s.Column.IsNumeric)
                {
                    o["non_numeric"] = s.NonNumeric;
                    if (s.Numeric is { } n)
                        o["numeric"] = new JsonObject
                        {
                            ["n"] = n.Count,
                            ["mean"] = ToolJson.Num(n.Mean),
                            ["sd"] = ToolJson.Num(n.StandardDeviation),
                            ["min"] = ToolJson.Num(n.Min),
                            ["q1"] = ToolJson.Num(n.Q1),
                            ["median"] = ToolJson.Num(n.Median),
                            ["q3"] = ToolJson.Num(n.Q3),
                            ["max"] = ToolJson.Num(n.Max),
                        };
                }
                if (includeTop && s.TopValues is { Count: > 0 } top)
                {
                    var t = new JsonArray();
                    foreach (var (value, count) in top)
                        t.Add(new JsonObject { ["value"] = ToolJson.Clip(value, valueClip), ["count"] = count });
                    o["top_values"] = t;
                    if (s.TopApproximate) o["top_values_approximate"] = true;
                }
                arr.Add(o);
            }
            return arr;
        }
    }
}
