using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent.Python;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Setup
{
    /// <summary>진단 텍스트에서 사용자 이름·경로와 비밀 값(토큰·API 키)을 가린다. 패턴 기반이라 모르는 형식은 못 잡을 수 있다.</summary>
    internal sealed class DiagnosticRedactor
    {
        public const string Mark = "<redacted>";

        private static readonly Regex[] Opaque =
        {
            new(@"\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{12,}", RegexOptions.Compiled),                         // OpenAI·Anthropic(sk-ant-…) 형식
            new(@"\bBearer\s+[A-Za-z0-9._~+/=\-]{12,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*", RegexOptions.Compiled), // JWT
            new(@"\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled),
            new(@"\bAIza[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled),                                  // Google API 키
            new(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled),
            new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),
        };

        // "name: value" / "name=value" / "name":"value" 에서 이름에 비밀을 뜻하는 낱말이 있으면 값을 가린다(max_tokens 같은 복수형은 제외).
        private static readonly Regex NamedSecret = new(
            @"(?<name>(?<![A-Za-z0-9])[A-Za-z0-9_\-]*(?:api[_-]?key|token(?!s)|secret|password|passwd|authorization|credential|verifier|auth[_-]?code)[A-Za-z0-9_\-]*[""']?\s*[:=]\s*|(?<![A-Za-z0-9_\-])key[""']?\s*[:=]\s*)(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;&}""']+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex QuerySecret = new(
            @"([?&](?:code|state|code_challenge|code_verifier|access_token|refresh_token|id_token|token|key|api_key|apikey|client_secret|signature|sig)=)[^&\s""'<>]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CliKey = new(@"(--api-key(?:=|\s+))\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 40자 이상 이어진 불투명 문자열(영문·숫자 모두 포함)은 키로 보고 가린다.
        private static readonly Regex LongToken = new(@"(?<![A-Za-z0-9_\-+/=])[A-Za-z0-9_\-+/=]{40,}(?![A-Za-z0-9_\-+/=])", RegexOptions.Compiled);

        private static readonly Regex UsersFolder = new(@"(?<drive>[A-Za-z]:)(?<sep>[\\/]{1,2})Users(?<sep2>[\\/]{1,2})(?<name>[^\\/\r\n""<>|:*?]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string _profile;
        private readonly Regex? _profileRx;
        private readonly Regex? _userRx;

        public DiagnosticRedactor(string userProfile, string userName)
        {
            _profile = userProfile;
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                // 같은 경로의 표기: 역슬래시·슬래시·JSON 이스케이프(\\)
                string core = Regex.Escape(userProfile.TrimEnd('\\', '/')).Replace(@"\\", @"[\\/]{1,2}");
                _profileRx = new Regex(core, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 2)
                _userRx = new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(userName) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        public static DiagnosticRedactor ForCurrentUser() =>
            new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);

        public string Redact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = text;
            foreach (var rx in Opaque) s = rx.Replace(s, Mark);
            s = CliKey.Replace(s, "$1" + Mark);
            s = QuerySecret.Replace(s, "$1" + Mark);
            s = NamedSecret.Replace(s, m =>
            {
                string v = m.Groups["value"].Value;
                if (v.Contains(Mark, StringComparison.Ordinal)) return m.Value;
                string quote = v.Length > 0 && (v[0] == '"' || v[0] == '\'') ? v[0].ToString() : "";
                return m.Groups["name"].Value + quote + Mark + quote;
            });
            s = LongToken.Replace(s, m => m.Value.Any(char.IsLetter) && m.Value.Any(char.IsDigit) ? Mark : m.Value);

            if (_profileRx is not null) s = _profileRx.Replace(s, "%USERPROFILE%");
            s = UsersFolder.Replace(s, m => m.Groups["name"].Value == "%USERPROFILE%" ? m.Value : m.Groups["drive"].Value + m.Groups["sep"].Value + "Users" + m.Groups["sep2"].Value + "<user>");
            if (_userRx is not null) s = _userRx.Replace(s, "<user>");
            return s;
        }
    }

    /// <summary>"AI 환경 진단 정보 복사"의 본문. 데이터 내용(셀 값·질문·답변)과 비밀은 넣지 않는다.</summary>
    internal sealed record AiDiagnosticsInput(
        string AppVersion, string OsDescription, string OsArchitecture, string ProcessArchitecture, string DotNetVersion, string Culture,
        int WindowDpi, string WebViewText, string OmpText, string PythonEnvText,
        IReadOnlyList<KeyValuePair<string, string>> Settings, string WebViewLogTail, string RpcLogTail);

    internal static class AiDiagnostics
    {
        public const int DefaultTailLines = 60;

        public static string Build(AiDiagnosticsInput i, DiagnosticRedactor redactor)
        {
            var sb = new StringBuilder();
            sb.AppendLine("== Nanum CSV Viewer AI diagnostics ==");
            sb.AppendLine("Generated        : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("App version      : " + i.AppVersion);
            sb.AppendLine("OS               : " + i.OsDescription);
            sb.AppendLine($"Architecture     : OS {i.OsArchitecture}, process {i.ProcessArchitecture}");
            sb.AppendLine(".NET             : " + i.DotNetVersion);
            sb.AppendLine("UI culture       : " + i.Culture);
            sb.AppendLine($"Window DPI       : {i.WindowDpi} ({i.WindowDpi * 100 / 96}%)");
            sb.AppendLine();
            sb.AppendLine("-- WebView2 --");
            sb.AppendLine(i.WebViewText.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("-- omp --");
            sb.AppendLine(i.OmpText.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("-- Python analysis environment --");
            sb.AppendLine(i.PythonEnvText.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("-- Agent settings --");
            foreach (var kv in i.Settings) sb.AppendLine($"{kv.Key} = {kv.Value}");
            sb.AppendLine();
            sb.AppendLine("-- webview2-init.log (tail) --");
            sb.AppendLine(string.IsNullOrWhiteSpace(i.WebViewLogTail) ? "(empty)" : i.WebViewLogTail.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("-- rpc.log (tail; frame types only, no message contents) --");
            sb.AppendLine(string.IsNullOrWhiteSpace(i.RpcLogTail) ? "(empty)" : i.RpcLogTail.TrimEnd());
            return redactor.Redact(sb.ToString());
        }

        // ---- 수집 -----------------------------------------------------------------------------------------------

        public static async Task<string> CollectAsync(AppSettings settings, int windowDpi, Func<CancellationToken, Task<OmpDiscoveryResult>> discoverOmp,
            CancellationToken ct = default)
        {
            string webView;
            try { webView = WebViewDiagnostics.Collect().ToDiagnosticText(); }
            catch (Exception ex) { webView = "(failed: " + ex.Message + ")"; }
            string omp;
            try { omp = (await discoverOmp(ct)).ToDiagnosticText(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { omp = "(failed: " + ex.Message + ")"; }

            var input = new AiDiagnosticsInput(
                AppInfo.Version,
                RuntimeInformation.OSDescription + " (build " + Environment.OSVersion.Version.Build + ")",
                RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription, System.Globalization.CultureInfo.CurrentUICulture.Name,
                windowDpi, webView, omp, DescribePythonEnv(),
                AgentSettingsLines(settings),
                string.Join("\n", TailLines(WebViewDiagnostics.LogPath, DefaultTailLines)),
                string.Join("\n", SummarizeRpcLog(TailLines(Path.Combine(OmpLaunch.TempDirectory, "rpc.log"), 400), DefaultTailLines)));
            return Build(input, DiagnosticRedactor.ForCurrentUser());
        }

        public static IReadOnlyList<KeyValuePair<string, string>> AgentSettingsLines(AppSettings s) => new[]
        {
            KeyValuePair.Create("AgentOmpPath", string.IsNullOrWhiteSpace(s.AgentOmpPath) ? "(auto)" : s.AgentOmpPath!),
            KeyValuePair.Create("AgentExtraArgs", string.IsNullOrWhiteSpace(s.AgentExtraArgs) ? "(none)" : s.AgentExtraArgs!),
            KeyValuePair.Create("AgentDataPolicy", s.AgentDataPolicy),
            KeyValuePair.Create("AgentMaxRows", s.AgentMaxRows.ToString()),
            KeyValuePair.Create("AgentApprovalMode", s.AgentApprovalMode),
            KeyValuePair.Create("AgentAllowLocalPython", s.AgentAllowLocalPython.ToString()),
            KeyValuePair.Create("AgentUseManagedPython", s.AgentUseManagedPython.ToString()),
            KeyValuePair.Create("AgentSkillsEnabled", s.AgentSkillsEnabled.ToString()),
            KeyValuePair.Create("Language", s.Language),
            KeyValuePair.Create("Theme", string.IsNullOrEmpty(s.Theme) ? "(system)" : s.Theme),
            KeyValuePair.Create("AiSetupShownVersion", string.IsNullOrEmpty(s.AiSetupShownVersion) ? "(never)" : s.AiSetupShownVersion),
            KeyValuePair.Create("AiSetupDisabled", s.AiSetupDisabled.ToString()),
        };

        public static string DescribePythonEnv()
        {
            try
            {
                var info = AnalysisEnvironment.Default.Inspect();
                var sb = new StringBuilder();
                sb.AppendLine("State  : " + info.State);
                sb.AppendLine("Root   : " + info.Root);
                if (info.PythonVersion is not null) sb.AppendLine("Python : " + info.PythonVersion + (info.BasePython is null ? "" : " (base " + info.BasePython + ")"));
                sb.Append("Groups : " + (info.Groups.Count == 0 ? "(none)" : string.Join(", ", info.Groups.Select(g => g.Group.Name + "=" + g.State))));
                return sb.ToString();
            }
            catch (Exception ex) { return "(failed: " + ex.Message + ")"; }
        }

        /// <summary>파일 끝의 count줄(공유 쓰기 중인 로그도 읽는다). 없거나 못 읽으면 빈 목록. 큰 파일은 끝 256KB만 본다.</summary>
        public static IReadOnlyList<string> TailLines(string path, int count)
        {
            try
            {
                if (!File.Exists(path)) return Array.Empty<string>();
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int window = 256 * 1024;
                bool partial = fs.Length > window;
                if (partial) fs.Seek(-window, SeekOrigin.End);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string text = reader.ReadToEnd();
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                if (partial && lines.Count > 0) lines.RemoveAt(0);   // 잘린 첫 줄
                while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                return lines.Count <= count ? lines : lines.GetRange(lines.Count - count, count);
            }
            catch (Exception) { return Array.Empty<string>(); }
        }

        private static readonly HashSet<string> NoisyEvents = new(StringComparer.Ordinal) { "message_update", "tool_execution_update", "host_tool_update", "btw_delta" };
        private static readonly Regex FrameTypePattern = new(@"^\{\s*""type""\s*:\s*""([A-Za-z_]{1,40})""", RegexOptions.Compiled);

        /// <summary>
        /// rpc.log("HH:mm:ss.fff &lt; {json}")를 프레임 종류만 남긴 줄로 줄인다. 프레임 본문(질문·답변·도구 결과·데이터)은 버린다.
        /// 응답은 명령·성공 여부·오류 문구, 이벤트는 종류. 머리(#)·메모(!) 줄은 그대로.
        /// </summary>
        public static IReadOnlyList<string> SummarizeRpcLog(IEnumerable<string> rawLines, int max)
        {
            var result = new List<string>();
            foreach (string line in rawLines)
            {
                if (line.Length < 14) continue;
                string stamp = line[..12];
                char dir = line.Length > 13 ? line[13] : ' ';
                string body = line.Length > 15 ? line[15..] : "";
                if (dir is '#' or '!') { result.Add(stamp + " " + dir + " " + Clip(body, 400)); continue; }
                if (dir is not ('<' or '>')) continue;
                string summary;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    string type = root.Str("type", "?");
                    if (NoisyEvents.Contains(type)) continue;
                    summary = type == "response"
                        ? $"response {root.Str("command", "?")} {(root.Bool("success") == true ? "ok" : "FAILED")}" + (root.Bool("success") == true ? "" : " error=" + Clip(root.Str("error"), 300))
                        : type == "extension_ui_request" ? "extension_ui_request " + root.Str("method", "?")
                        : type == "host_tool_call" ? "host_tool_call " + root.Str("toolName", root.Str("name", "?"))
                        : dir == '>' && root.Str("command").Length == 0 ? "command " + type
                        : type;
                }
                catch (JsonException)
                {
                    // 너무 커서 잘린 프레임: 앞머리의 type만 건진다(본문은 쓰지 않는다).
                    var m = FrameTypePattern.Match(body);
                    string type = m.Success ? m.Groups[1].Value : "";
                    if (NoisyEvents.Contains(type)) continue;
                    summary = type.Length > 0 ? type + " (truncated frame)" : "(unparsed frame)";
                }
                result.Add($"{stamp} {dir} {summary}");
            }
            return result.Count <= max ? result : result.GetRange(result.Count - max, max);
        }

        private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    }
}
