using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>`omp usage --json`에서 한 제공자의 요금제 한도를 사용량 패널이 읽는 모양(plan, limits[window, windowLabel, group, used, resetsAt, status])으로 줄인다.</summary>
    internal static class UsageReport
    {
        /// <summary>제공자 보고서가 없거나 JSON이 아니면 null.</summary>
        public static JsonObject? Limits(string? usageJson, string provider)
        {
            if (string.IsNullOrWhiteSpace(usageJson) || provider.Length == 0) return null;
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(usageJson);
                root = doc.RootElement.Clone();
            }
            catch (JsonException) { return null; }

            foreach (var report in root.Child("reports").Items())
            {
                if (!string.Equals(report.Str("provider"), provider, StringComparison.OrdinalIgnoreCase)) continue;
                string plan = report.Child("metadata").Str("planType");
                var rows = new JsonArray();
                var seen = new HashSet<string>();
                foreach (var limit in report.Child("limits").Items())
                {
                    var window = limit.Child("window");
                    var amount = limit.Child("amount");
                    if (!amount.IsObject()) continue;
                    string key = limit.Str("label") + "|" + window.Str("id") + "|" + window.Str("resetsAt");
                    if (!seen.Add(key)) continue;
                    double used = amount.Num("usedFraction");
                    rows.Add(new JsonObject
                    {
                        ["window"] = window.Str("id"),
                        ["windowLabel"] = window.Str("label"),
                        ["group"] = Group(limit),
                        ["used"] = double.IsNaN(used) ? 0 : used,
                        ["resetsAt"] = window.Int("resetsAt"),
                        ["status"] = limit.Str("status"),
                    });
                }
                return new JsonObject { ["plan"] = Capitalize(plan), ["limits"] = rows };
            }
            return null;
        }

        /// <summary>Anthropic은 한도를 창 이름으로("Claude 5 Hour"), 다른 제공자는 모델 묶음으로 부른다. tier는 그 모델들로 좁힌다.</summary>
        private static string Group(JsonElement limit)
        {
            string tier = limit.Child("scope").Str("tier");
            if (tier.Length > 0) return Capitalize(tier);
            string label = limit.Str("label");
            string windowLabel = limit.Child("window").Str("label");
            if (windowLabel.Length > 0 && label.Contains(windowLabel, StringComparison.OrdinalIgnoreCase)) return "";
            return label;
        }

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
