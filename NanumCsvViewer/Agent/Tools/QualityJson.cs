using System.Text.Json.Nodes;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>품질 스캔 결과를 모델에게 줄 JSON으로. 원시 셀 값(예시 값·위장결측 후보 값·상수 값)은 includeValues일 때만 담는다.</summary>
    internal static class QualityJson
    {
        public static string KindName(QualityCheckKind kind)
        {
            string s = kind.ToString();
            var sb = new System.Text.StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append('_');
                sb.Append(char.ToLowerInvariant(s[i]));
            }
            return sb.ToString();
        }

        public static JsonObject Build(QualityReport report, QualitySeverity minSeverity, int maxFindings, bool includeValues)
        {
            var matching = report.Findings
                .Where(f => f.Severity >= minSeverity)
                .OrderByDescending(f => f.Severity).ThenBy(f => f.Column).ThenBy(f => f.Kind)
                .ToList();

            var findings = new JsonArray();
            foreach (var f in matching.Take(maxFindings))
            {
                var o = new JsonObject
                {
                    ["check"] = KindName(f.Kind),
                    ["severity"] = f.Severity.ToString().ToLowerInvariant(),
                    ["dimension"] = f.Dimension.ToString().ToLowerInvariant(),
                    ["column"] = f.Column >= 0 && f.ColumnName.Length > 0 ? f.ColumnName : null,
                    ["violations"] = f.ViolationCount,
                    ["evaluated_rows"] = f.EvaluatedRows,
                };
                if (f.SkippedRows > 0) o["skipped_rows"] = f.SkippedRows;
                if (f.Approximate) o["approximate"] = true;
                if (f.FenceLow is { } lo) o["fence_low"] = ToolJson.Num(lo);
                if (f.FenceHigh is { } hi) o["fence_high"] = ToolJson.Num(hi);
                if (f.Examples.Count > 0)
                    o["example_rows"] = new JsonArray(f.Examples.Take(5).Select(e => (JsonNode?)JsonValue.Create(e.SourceRow)).ToArray());
                if (f.Label is { Length: > 0 } label && (f.Kind != QualityCheckKind.ConstantColumn || includeValues))
                    o["label"] = ToolJson.Clip(label, 80);
                if (includeValues)
                {
                    if (f.Examples.Count > 0)
                        o["examples"] = new JsonArray(f.Examples.Take(5)
                            .Select(e => (JsonNode?)new JsonObject { ["row"] = e.SourceRow, ["value"] = ToolJson.Clip(e.Value, 60) }).ToArray());
                    if (f.Breakdown.Count > 0)
                        o["breakdown"] = new JsonArray(f.Breakdown.Take(10)
                            .Select(b => (JsonNode?)new JsonObject { ["value"] = ToolJson.Clip(b.Value, 40), ["count"] = b.Count }).ToArray());
                }
                else if (f.Breakdown.Count > 0)
                {
                    o["breakdown_values_hidden"] = f.Breakdown.Count;
                }
                findings.Add(o);
            }

            var json = new JsonObject
            {
                ["rows_scanned"] = report.RowsScanned,
                ["scanned_fully"] = report.ScannedFully,
                ["elapsed_seconds"] = ToolJson.Num(report.ElapsedSeconds),
                ["counts"] = new JsonObject
                {
                    ["critical"] = report.CountBySeverity(QualitySeverity.Critical),
                    ["warning"] = report.CountBySeverity(QualitySeverity.Warning),
                    ["info"] = report.CountBySeverity(QualitySeverity.Info),
                },
                ["findings"] = findings,
            };
            if (report.DuplicateRowCheckSkipped) json["duplicate_row_check_skipped"] = true;
            if (matching.Count > maxFindings)
                json["truncated"] = $"Listed {maxFindings} of {matching.Count} findings at or above the requested severity (most severe first).";
            if (!includeValues)
                json["values_hidden"] = "Example cell values are not included because the data policy is SummaryOnly; row numbers are given (see example_rows).";
            return json;
        }
    }
}
