using System.Globalization;
using System.Text.Json;

namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>
    /// host_tool_call 인자 읽기. 잘못된 형식은 모델이 스스로 고칠 수 있게 어떤 인자가 왜 틀렸는지 적은 AgentToolException으로 알린다.
    /// 모르는 키(omp가 붙이는 의도 문구 등)는 무시한다 — 스키마가 이미 additionalProperties:false로 안내한다.
    /// </summary>
    internal sealed class ToolArgs
    {
        private readonly JsonElement _obj;
        private readonly bool _has;

        private ToolArgs(JsonElement obj, bool has) { _obj = obj; _has = has; }

        public static ToolArgs From(JsonElement arguments)
        {
            if (arguments.ValueKind == JsonValueKind.Object) return new ToolArgs(arguments, true);
            if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return new ToolArgs(default, false);
            throw new AgentToolException("Arguments must be a JSON object.");
        }

        private bool TryGet(string name, out JsonElement value)
        {
            value = default;
            if (!_has || !_obj.TryGetProperty(name, out value)) return false;
            return value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined;
        }

        public bool Has(string name) => TryGet(name, out _);

        public string? OptString(string name)
        {
            if (!TryGet(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.String) throw Bad(name, "a string");
            return v.GetString();
        }

        public string ReqString(string name)
        {
            string? s = OptString(name);
            if (string.IsNullOrWhiteSpace(s)) throw new AgentToolException($"'{name}' is required.");
            return s;
        }

        public long? OptInt(string name, long min, long max)
        {
            if (!TryGet(name, out var v)) return null;
            long n;
            if (v.ValueKind == JsonValueKind.Number)
            {
                if (!v.TryGetInt64(out n))
                {
                    double d = v.GetDouble();
                    if (d != Math.Floor(d) || !double.IsFinite(d) || Math.Abs(d) > 9e15) throw Bad(name, "an integer");
                    n = (long)d;
                }
            }
            else if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { }
            else throw Bad(name, "an integer");
            if (n < min || n > max) throw new AgentToolException($"'{name}' must be between {min} and {max} (got {n}).");
            return n;
        }

        public long ReqInt(string name, long min, long max)
            => OptInt(name, min, max) ?? throw new AgentToolException($"'{name}' is required.");

        public bool? OptBool(string name)
        {
            if (!TryGet(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Bad(name, "true or false"),
            };
        }

        /// <summary>허용 값(대소문자 무시) 중 하나. 없으면 null.</summary>
        public string? OptEnum(string name, params string[] allowed)
        {
            string? s = OptString(name);
            if (s is null) return null;
            string? hit = allowed.FirstOrDefault(a => string.Equals(a, s.Trim(), StringComparison.OrdinalIgnoreCase));
            return hit ?? throw new AgentToolException($"'{name}' must be one of: {string.Join(", ", allowed)} (got '{s}').");
        }

        public IReadOnlyList<string>? OptStringArray(string name, int maxItems)
        {
            if (!TryGet(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array) throw Bad(name, "an array of strings");
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Bad(name, "an array of strings");
                list.Add(item.GetString() ?? "");
            }
            if (list.Count > maxItems) throw new AgentToolException($"'{name}' has {list.Count} items; at most {maxItems} are allowed.");
            return list;
        }

        public IReadOnlyList<ToolArgs>? OptObjectArray(string name, int maxItems)
        {
            if (!TryGet(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array) throw Bad(name, "an array of objects");
            var list = new List<ToolArgs>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Bad(name, "an array of objects");
                list.Add(new ToolArgs(item, true));
            }
            if (list.Count > maxItems) throw new AgentToolException($"'{name}' has {list.Count} items; at most {maxItems} are allowed. Split the request.");
            return list;
        }

        /// <summary>값이 문자열이거나 수·불리언이면 문자열로(편집 값은 모델이 숫자를 그대로 줄 수 있다). 객체·배열은 오류.</summary>
        public string? OptScalarAsString(string name)
        {
            if (!TryGet(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => throw Bad(name, "a string"),
            };
        }

        /// <summary>키가 있으면(빈 문자열 포함) 값을 돌려주고, 없으면 null. 빈 문자열이 유효한 값(셀 비우기)인 곳에서 쓴다.</summary>
        public bool HasScalar(string name) => TryGet(name, out _);

        private static AgentToolException Bad(string name, string expected)
            => new($"'{name}' must be {expected}.");
    }
}
