using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NanumCsvViewer.Agent.Rpc
{
    /// <summary>omp `--mode rpc-ui` JSONL 프레임 작성기/해석기(프로토콜 v1, omp가 제안하면 v2). 참고: omp://rpc.md</summary>
    internal static class RpcProtocol
    {
        /// <summary>물리 한 줄(v1·v2 공통) 상한.</summary>
        public const int MaxFrameBytes = 1 << 20;
        /// <summary>v2 rpc_chunk로 복원한 논리 프레임 상한.</summary>
        public const int MaxReassembledFrameBytes = 64 << 20;

        /// <summary>한국어를 \uXXXX로 늘리지 않는 직렬화 옵션(stdin은 UTF-8 JSONL).</summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Serialize(JsonNode node) => node.ToJsonString(Options);

        /// <summary>{"id":..,"type":..,...}. fill이 나머지 필드를 채운다.</summary>
        public static string Command(string type, string? id = null, Action<JsonObject>? fill = null)
        {
            var obj = new JsonObject();
            if (id != null) obj["id"] = id;
            obj["type"] = type;
            fill?.Invoke(obj);
            return Serialize(obj);
        }

        /// <summary>ready 프레임이 제안한 프로토콜 중 쓸 것: v2가 있으면 2, v1만 있으면 1, 목록이 없으면 1(구버전), 둘 다 없으면 0.</summary>
        public static int ChooseProtocol(JsonElement ready)
        {
            if (!ready.TryGetProperty("supportedProtocolVersions", out var list) || list.ValueKind != JsonValueKind.Array)
                return 1;
            bool v1 = false, v2 = false;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int v)) continue;
                v1 |= v == 1;
                v2 |= v == 2;
            }
            return v2 ? 2 : v1 ? 1 : 0;
        }

        public static string Negotiate(string id, int version) =>
            Command("negotiate_protocol", id, o => o["protocolVersion"] = version);

        public static string SetHostTools(string id, IReadOnlyList<HostToolDefinition> tools) =>
            Command("set_host_tools", id, o =>
            {
                var arr = new JsonArray();
                foreach (var t in tools)
                {
                    JsonNode? schema;
                    try { schema = JsonNode.Parse(t.ParametersSchema); }
                    catch (JsonException) { schema = null; }
                    arr.Add(new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = schema ?? new JsonObject { ["type"] = "object" },
                    });
                }
                o["tools"] = arr;
            });

        /// <summary>prompt / steer / follow_up 공통: message와 선택적 images.</summary>
        public static string Message(string type, string id, string message, JsonArray? images = null) =>
            Command(type, id, o =>
            {
                o["message"] = message;
                if (images is { Count: > 0 }) o["images"] = images;
            });

        public static string SetModel(string id, string provider, string modelId) =>
            Command("set_model", id, o => { o["provider"] = provider; o["modelId"] = modelId; });

        public static string SetThinking(string id, string level) =>
            Command("set_thinking_level", id, o => o["level"] = level);

        public static string RemoveQueued(string id, string message, string queue) =>
            Command("remove_queued_message", id, o => { o["message"] = message; o["queue"] = queue; });

        public static string SwitchSession(string id, string sessionPath) =>
            Command("switch_session", id, o => o["sessionPath"] = sessionPath);

        public static string ExportHtml(string id, string outputPath) =>
            Command("export_html", id, o => o["outputPath"] = outputPath);

        /// <summary>host_tool_result. ImagePngBase64가 있으면 image 파트를 text 뒤에 붙인다.</summary>
        public static string HostToolResult(string id, HostToolResult result)
        {
            var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result.Text } };
            if (!string.IsNullOrEmpty(result.ImagePngBase64))
                content.Add(new JsonObject { ["type"] = "image", ["data"] = result.ImagePngBase64, ["mimeType"] = "image/png" });
            var obj = new JsonObject { ["type"] = "host_tool_result", ["id"] = id };
            if (result.IsError) obj["isError"] = true;
            obj["result"] = new JsonObject { ["content"] = content };
            return Serialize(obj);
        }

        public static string UiValue(string id, string value) =>
            Serialize(new JsonObject { ["type"] = "extension_ui_response", ["id"] = id, ["value"] = value });

        public static string UiConfirmed(string id, bool confirmed) =>
            Serialize(new JsonObject { ["type"] = "extension_ui_response", ["id"] = id, ["confirmed"] = confirmed });

        public static string UiCancelled(string id) =>
            Serialize(new JsonObject { ["type"] = "extension_ui_response", ["id"] = id, ["cancelled"] = true });

        public static int Utf8Length(string s) => Encoding.UTF8.GetByteCount(s);
    }

    /// <summary>JsonElement 읽기 보조(없으면 기본값).</summary>
    internal static class RpcJson
    {
        public static string Str(this JsonElement e, string name, string fallback = "")
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? fallback;
                if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.GetRawText();
            }
            return fallback;
        }

        public static long Int(this JsonElement e, string name, long fallback = 0)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n))
                return n;
            return fallback;
        }

        public static double Num(this JsonElement e, string name, double fallback = double.NaN)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d))
                return d;
            return fallback;
        }

        /// <summary>true/false 값이 명시된 경우에만 값, 아니면 null.</summary>
        public static bool? Bool(this JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return null;
        }

        public static JsonElement Child(this JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)) return v;
            return default;
        }

        public static bool IsObject(this JsonElement e) => e.ValueKind == JsonValueKind.Object;
        public static bool IsArray(this JsonElement e) => e.ValueKind == JsonValueKind.Array;

        public static IEnumerable<JsonElement> Items(this JsonElement e) =>
            e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();

        public static List<string> Strings(this JsonElement e)
        {
            var list = new List<string>();
            foreach (var item in e.Items())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
            return list;
        }

        /// <summary>content(문자열 또는 [{type:"text",text}] 배열)에서 텍스트를 이어 붙인다.</summary>
        public static string ContentText(this JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (content.ValueKind != JsonValueKind.Array) return "";
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String) { Append(sb, part.GetString()); continue; }
                if (part.ValueKind != JsonValueKind.Object) continue;
                string type = part.Str("type");
                if (type is "" or "text") Append(sb, part.Str("text"));
            }
            return sb.ToString();

            static void Append(StringBuilder sb, string? s)
            {
                if (string.IsNullOrEmpty(s)) return;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(s);
            }
        }
    }
}
