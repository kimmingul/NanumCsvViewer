using System.Text.Json.Nodes;

namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>에이전트가 실행한 도구 기록(도구·인자·성공 여부·소요 ms). 셀 값은 기록하지 않는다.</summary>
    public interface IAgentToolLog
    {
        void Append(string tool, JsonNode? args, bool ok, long milliseconds, string? error);
    }

    /// <summary>
    /// %LOCALAPPDATA%\NanumCsvViewer\agent\tool-log.jsonl 에 한 줄 JSON으로 덧붙인다. 5 MiB를 넘으면 한 세대만 회전한다.
    /// 기록 실패(권한·디스크)는 도구 실행을 막지 않는다.
    /// </summary>
    public sealed class AgentToolLog : IAgentToolLog
    {
        public const long MaxBytes = 5L * 1024 * 1024;

        private readonly string _path;
        private readonly object _gate = new();

        public AgentToolLog(string path) => _path = path;

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NanumCsvViewer", "agent", "tool-log.jsonl");

        public static AgentToolLog Default { get; } = new(DefaultPath);

        public string FilePath => _path;

        public void Append(string tool, JsonNode? args, bool ok, long milliseconds, string? error)
        {
            try
            {
                var entry = new JsonObject
                {
                    ["ts"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
                    ["tool"] = tool,
                    ["args"] = args?.DeepClone(),
                    ["ok"] = ok,
                    ["ms"] = milliseconds,
                };
                if (error is not null) entry["error"] = ToolJson.Clip(error, 300);
                string line = ToolJson.Serialize(entry) + "\n";
                lock (_gate)
                {
                    string? dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var info = new FileInfo(_path);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = _path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_path, old);
                    }
                    File.AppendAllText(_path, line, new System.Text.UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // 기록은 부가 기능이다. 실패해도 도구 결과에는 영향이 없다.
            }
        }
    }
}
