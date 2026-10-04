using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>도구 결과 JSON 작성 도우미. 한글은 이스케이프하지 않고, NaN/∞는 null, 숫자는 유효숫자 6자리로 줄여 토큰을 아낀다.</summary>
    internal static class ToolJson
    {
        private static readonly JsonSerializerOptions Compact = new()
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>유한한 수만 유효숫자 6자리로 반올림해 노드로. 그 외(NaN·∞)는 null.</summary>
        public static JsonNode? Num(double v)
        {
            if (!double.IsFinite(v)) return null;
            return JsonValue.Create(Sig(v));
        }

        public static JsonNode? Num(double? v) => v is { } d ? Num(d) : null;

        public static double Sig(double v)
        {
            if (v == 0 || !double.IsFinite(v)) return v;
            return double.Parse(v.ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        public static JsonNode? Str(string? s) => s is null ? null : JsonValue.Create(s);

        public static JsonArray Strings(IEnumerable<string> items)
        {
            var a = new JsonArray();
            foreach (string s in items) a.Add(JsonValue.Create(s));
            return a;
        }

        public static string Serialize(JsonNode node) => node.ToJsonString(Compact);

        /// <summary>셀 값 한 칸을 모델에게 보낼 때의 길이 제한. 잘리면 끝에 …을 붙인다.</summary>
        public static string Clip(string s, int max)
            => s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";

        /// <summary>승인 카드·로그용 한 줄 표시: 줄바꿈을 눈에 보이게 바꾸고 길이를 자른다.</summary>
        public static string OneLine(string s, int max)
        {
            string t = s.Replace("\r\n", "⏎").Replace("\n", "⏎").Replace("\r", "⏎").Replace("\t", "→");
            return Clip(t, max);
        }
    }
}
