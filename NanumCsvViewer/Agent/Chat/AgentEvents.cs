using System.Text;
using System.Text.Json;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Chat
{
    internal enum AgentEventKind
    {
        None, AgentStart, AgentEnd, Thinking, ThinkingEnd, TextDelta, TextEnd, ToolCallStart, ToolCallDelta,
        ToolStart, ToolUpdate, ToolEnd, Notice, CompactionStart, CompactionEnd, RetryStart, RetryEnd, Fallback,
        Subagent, Error,
        /// <summary>agent 턴 없이 끝난 prompt(로컬 슬래시 명령): agent_end가 오지 않는다.</summary>
        PromptLocal,
        /// <summary>내장 슬래시 명령이 출력한 텍스트(ANSI 제거).</summary>
        CommandOutput,
        /// <summary>어시스턴트 메시지 시작: ToolName=provider, Detail=모델 id.</summary>
        Model,
    }

    /// <summary>
    /// 채팅이 보여 줄 만큼만 줄인 에이전트 이벤트. Subagent: ToolId=id, ToolName=agent, Detail=설명, Text=마지막 의도,
    /// Level=상태, Count=도구 호출 수. Fallback: ToolName=to, Detail=from.
    /// </summary>
    internal sealed class AgentEvent
    {
        public AgentEventKind Kind;
        public string Text = "", ToolId = "", ToolName = "", Detail = "", Level = "";
        public int Count;
        public bool IsError, IsTerminal;
    }

    internal static class AgentEventParser
    {
        public static AgentEvent Parse(JsonElement root, bool korean)
        {
            var ev = new AgentEvent();
            string type = root.Str("type");
            switch (type)
            {
                case "agent_start":
                    ev.Kind = AgentEventKind.AgentStart;
                    break;
                case "agent_end":
                    ev.Kind = AgentEventKind.AgentEnd;
                    ev.IsTerminal = root.Bool("isTerminal") != false;
                    break;
                case "message_update":
                    ReadMessageUpdate(root, ev);
                    break;
                case "message_start":
                    {
                        var msg = root.Child("message");
                        if (msg.IsObject() && msg.Str("role") == "assistant")
                        {
                            ev.Kind = AgentEventKind.Model;
                            ev.ToolName = msg.Str("provider");
                            ev.Detail = msg.Str("model");
                        }
                        break;
                    }
                case "tool_execution_start":
                case "tool_execution_update":
                case "tool_execution_end":
                    ReadTool(root, type, ev);
                    break;
                case "subagent_lifecycle":
                case "subagent_progress":
                    ReadSubagent(root, ev);
                    break;
                case "notice":
                    ev.Kind = AgentEventKind.Notice;
                    ev.Text = root.Str("message");
                    ev.Level = root.Str("level");
                    if (ev.Level.Length == 0) ev.Level = "info";
                    break;
                case "auto_compaction_start":
                    ev.Kind = AgentEventKind.CompactionStart;
                    break;
                case "auto_compaction_end":
                    ev.Kind = AgentEventKind.CompactionEnd;
                    break;
                case "auto_retry_start":
                case "auto_retry_end":
                case "retry_fallback_applied":
                case "retry_fallback_succeeded":
                    ReadRetry(root, type, ev, korean);
                    break;
                case "prompt_result":
                    if (root.Bool("agentInvoked") == false) ev.Kind = AgentEventKind.PromptLocal;
                    break;
                case "command_output":
                    ev.Kind = AgentEventKind.CommandOutput;
                    ev.Text = StripAnsi(root.Str("text"));
                    break;
                case "extension_error":
                    ev.Kind = AgentEventKind.Error;
                    ev.Text = root.Str("error");
                    break;
                case "response":
                    if (root.Bool("success") == true)
                    {
                        if (root.Str("command") == "prompt" && root.Child("data").Bool("agentInvoked") == false)
                            ev.Kind = AgentEventKind.PromptLocal;
                    }
                    else if (root.Bool("success") == false)
                    {
                        ev.Kind = AgentEventKind.Error;
                        string cmd = root.Str("command"), err = root.Str("error");
                        ev.Text = cmd.Length > 0 ? $"{cmd}: {err}" : err;
                        ev.ToolName = cmd;
                    }
                    break;
            }
            return ev;
        }

        private static void ReadMessageUpdate(JsonElement root, AgentEvent ev)
        {
            var stream = root.Child("assistantMessageEvent");
            switch (stream.Str("type"))
            {
                case "text_delta":
                    ev.Kind = AgentEventKind.TextDelta;
                    ev.Text = stream.Str("delta");
                    break;
                case "text_end":
                    ev.Kind = AgentEventKind.TextEnd;
                    break;
                case "thinking_start":
                case "thinking_delta":
                    ev.Kind = AgentEventKind.Thinking;
                    ev.Text = stream.Str("delta");
                    break;
                case "thinking_end":
                    ev.Kind = AgentEventKind.ThinkingEnd;
                    break;
                case "toolcall_start":
                case "toolcall_delta":
                    ev.Kind = stream.Str("type") == "toolcall_start" ? AgentEventKind.ToolCallStart : AgentEventKind.ToolCallDelta;
                    var part = StreamPart(stream);
                    ev.ToolName = part.Str("name");
                    ev.ToolId = part.Str("id");
                    ev.Text = stream.Str("delta");
                    break;
            }
        }

        /// <summary>스트림 이벤트가 가리키는 tool call 콘텐츠 조각: toolCall, 없으면 partial.content[contentIndex].</summary>
        private static JsonElement StreamPart(JsonElement stream)
        {
            var call = stream.Child("toolCall");
            if (call.IsObject()) return call;
            var content = stream.Child("partial").Child("content");
            if (!content.IsArray()) return default;
            int len = content.GetArrayLength();
            if (len == 0) return default;
            int idx = (int)stream.Int("contentIndex", 0);
            if (idx < 0 || idx >= len) idx = 0;
            var item = content[idx];
            return item.IsObject() ? item : default;
        }

        private static void ReadTool(JsonElement root, string type, AgentEvent ev)
        {
            var args = root.Child("args");
            string raw = root.Str("toolName");
            ev.ToolId = root.Str("toolCallId");
            ev.ToolName = ResolveToolName(raw, args);
            if (type == "tool_execution_start")
            {
                ev.Kind = AgentEventKind.ToolStart;
                ev.Detail = ToolDetail(root, args, raw, ev.ToolName);
            }
            else if (type == "tool_execution_update")
            {
                ev.Kind = AgentEventKind.ToolUpdate;
                ev.Text = root.Child("partialResult").Child("content").ContentText();
            }
            else
            {
                ev.Kind = AgentEventKind.ToolEnd;
                ev.IsError = root.Bool("isError") == true;
                var result = root.Child("result");
                ev.Text = result.IsObject() ? result.Child("content").ContentText() : root.Str("result");
            }
        }

        /// <summary>write/read가 'xd://csv.x'를 가리키면 도구 이름은 'csv.x'.</summary>
        private static string ResolveToolName(string toolName, JsonElement args)
        {
            if (toolName is "write" or "read")
            {
                string path = args.Str("path");
                if (path.StartsWith("xd://", StringComparison.Ordinal)) return path["xd://".Length..];
            }
            return toolName;
        }

        private static string ToolDetail(JsonElement root, JsonElement args, string raw, string resolved)
        {
            string d = root.Str("intent");
            if (d.Length == 0 && args.IsObject())
            {
                d = args.Str("intent");
                if (d.Length == 0) d = args.Str("i");
                if (d.Length == 0)
                {
                    if (raw == "bash") d = args.Str("command");
                    else if (raw is "read" or "write" or "edit") d = args.Str("path");
                    else if (resolved.StartsWith("csv.", StringComparison.Ordinal)) d = args.GetRawText();
                }
            }
            d = CollapseWhitespace(d);
            return d.Length > 160 ? d[..159] + "…" : d;
        }

        private static void ReadSubagent(JsonElement root, AgentEvent ev)
        {
            var payload = root.Child("payload");
            if (!payload.IsObject()) return;
            var p = payload.Child("progress");
            if (!p.IsObject()) p = payload;
            ev.ToolId = p.Str("id");
            ev.ToolName = p.Str("agent");
            ev.Detail = CollapseWhitespace(p.Str("description"));
            ev.Text = CollapseWhitespace(p.Str("lastIntent"));
            ev.Level = p.Str("status");
            ev.Count = (int)p.Int("toolCount", 0);
            ev.Kind = ev.ToolId.Length == 0 ? AgentEventKind.None : AgentEventKind.Subagent;
        }

        private static void ReadRetry(JsonElement root, string type, AgentEvent ev, bool korean)
        {
            switch (type)
            {
                case "auto_retry_start":
                    ev.Kind = AgentEventKind.RetryStart;
                    long sec = (root.Int("delayMs") + 999) / 1000;
                    ev.Text = korean
                        ? $"재시도 {root.Int("attempt")}/{root.Int("maxAttempts")} ({sec}초 뒤): {CollapseWhitespace(root.Str("errorMessage"))}"
                        : $"Retry {root.Int("attempt")}/{root.Int("maxAttempts")} in {sec}s: {CollapseWhitespace(root.Str("errorMessage"))}";
                    break;
                case "auto_retry_end":
                    ev.Kind = AgentEventKind.RetryEnd;
                    ev.IsError = root.Bool("success") == false;
                    ev.Text = ev.IsError
                        ? (korean ? "재시도 실패: " : "Retry failed: ") + CollapseWhitespace(root.Str("finalError"))
                        : (korean ? $"{root.Int("attempt")}번째 재시도에서 성공했습니다." : $"Succeeded on retry {root.Int("attempt")}.");
                    break;
                case "retry_fallback_applied":
                    ev.Kind = AgentEventKind.Fallback;
                    ev.Detail = root.Str("from");
                    ev.ToolName = root.Str("to");
                    ev.Text = korean ? $"모델 전환: {ev.Detail} → {ev.ToolName}" : $"Switched model: {ev.Detail} → {ev.ToolName}";
                    break;
                default:
                    ev.Kind = AgentEventKind.Fallback;
                    ev.Detail = root.Str("model");
                    ev.Text = korean ? $"{ev.Detail} 모델로 성공했습니다." : $"Succeeded with {ev.Detail}.";
                    break;
            }
        }

        public static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>터미널 이스케이프(색·커서) 제거.</summary>
        public static string StripAnsi(string text)
        {
            if (text.IndexOf('\u001b') < 0) return text;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    i += 2;
                    while (i < text.Length && !(text[i] >= '@' && text[i] <= '~')) i++;
                    continue;
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        /// <summary>도구 결과 미리보기: maxChars 초과 시 앞부분 + 생략 표시.</summary>
        public static string ToolResultPreview(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;
            return text[..maxChars] + $"\n… ({text.Length - maxChars:N0} more chars)";
        }
    }
}
