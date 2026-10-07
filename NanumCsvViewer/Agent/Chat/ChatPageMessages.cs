using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>host → 채팅 페이지 메시지(JSON 한 객체, 필드 "t"가 종류). RAD Agent 페이지와 같은 형식.</summary>
    internal static class ChatPageMessages
    {
        private static string Build(string t, Action<JsonObject>? fill = null)
        {
            var o = new JsonObject { ["t"] = t };
            fill?.Invoke(o);
            return RpcProtocol.Serialize(o);
        }

        /// <summary>
        /// 사용자 말풍선. omp에 보낸 글에 첨부 머리말이 있으면(<see cref="AttachmentContext"/>) 말풍선에는 사용자가 쓴 글과 파일 칩(attachments)만 싣는다.
        /// </summary>
        public static string User(string text, long ts) => Build("user", o => { FillUser(o, text); o["ts"] = ts; });

        /// <summary>실행 중에 보낸 메시지: queue는 'steer'(다음 단계에 읽힘) 또는 'followUp'(턴 뒤). sent는 omp에 보낸 원문(머리말 포함).</summary>
        public static string QueuedUser(string text, string queue, string sent, long ts) =>
            Build("user", o => { FillUser(o, text); o["queue"] = queue; o["sent"] = sent; o["ts"] = ts; });

        private static void FillUser(JsonObject o, string text)
        {
            if (AttachmentContext.TryParse(text, out var items, out string visible))
            {
                o["text"] = visible;
                o["attachments"] = AttachmentArray(items);
            }
            else o["text"] = text;
        }

        private static JsonArray AttachmentArray(IEnumerable<ChatAttachment> items)
        {
            var arr = new JsonArray();
            foreach (var a in items)
            {
                var tables = new JsonArray();
                foreach (string t in a.Tables) tables.Add(t);
                arr.Add(new JsonObject { ["name"] = a.Name, ["path"] = a.Path, ["label"] = "📎 " + a.Name, ["tables"] = tables });
            }
            return arr;
        }

        /// <summary>+ 메뉴/끌어놓기로 작업 공간에 올린 파일들: 입력창이 칩으로 보여 준다(다음 전송에 머리말로 붙는다).</summary>
        public static string Attached(IReadOnlyList<ChatAttachment> items) => Build("attached", o => o["items"] = AttachmentArray(items));

        public static string Queue(IReadOnlyList<string> steering, IReadOnlyList<string> followUp, bool fromState) =>
            Build("queue", o =>
            {
                o["steering"] = ToArray(steering);
                o["followUp"] = ToArray(followUp);
                o["state"] = fromState;
            });

        public static string QueueRemoved(string sent, string queue, bool removed) =>
            Build("queueRemoved", o => { o["sent"] = sent; o["queue"] = queue; o["removed"] = removed; });

        public static string Delta(string text) => Build("assistantDelta", o => o["text"] = text);
        public static string AssistantEnd() => Build("assistantEnd");
        public static string ThinkingDelta(string text) => Build("thinkingDelta", o => o["text"] = text);
        public static string Thinking(string text) => Build("thinking", o => o["text"] = text);
        public static string ThinkingEnd() => Build("thinkingEnd");

        public static string ToolInputDelta(string id, string name, string text) =>
            Build("toolInputDelta", o => { o["id"] = id; o["name"] = name; o["text"] = text; });

        public static string ToolStart(string id, string name, string detail, string input) =>
            Build("toolStart", o => { o["id"] = id; o["name"] = name; o["detail"] = detail; o["input"] = input; });

        public static string ToolUpdate(string id, string text) =>
            Build("toolUpdate", o => { o["id"] = id; o["text"] = text; });

        public static string ToolEnd(string id, bool ok, long ms, string result) =>
            Build("toolEnd", o => { o["id"] = id; o["ok"] = ok; o["ms"] = ms; o["result"] = result; });

        public static string Subagent(string id, string agent, string description, string intent, string status, int tools) =>
            Build("subagent", o =>
            {
                o["id"] = id; o["agent"] = agent; o["description"] = description;
                o["intent"] = intent; o["status"] = status; o["tools"] = tools;
            });

        public static string Todos(IReadOnlyList<TodoItem> items) =>
            Build("todos", o =>
            {
                var arr = new JsonArray();
                foreach (var t in items)
                    arr.Add(new JsonObject { ["phase"] = t.Phase, ["content"] = t.Content, ["status"] = t.Status });
                o["items"] = arr;
            });

        public static string Notice(string level, string text) =>
            Build("notice", o => { o["level"] = level; o["text"] = text; });

        public static string LinkNotice(string level, string text, string linkText, string url) =>
            Build("notice", o => { o["level"] = level; o["text"] = text; o["linkText"] = linkText; o["url"] = url; });

        /// <summary>
        /// 버튼(링크) 여러 개가 달린 알림. 각 action의 url은 openUrl로 호스트에 간다(앱 내부 주소 nanumcsv://…).
        /// checkboxLabel이 있으면 체크 상자(기본 꺼짐)를 보이고, 켜 두면 CarriesPathOption인 버튼의 주소 끝에 "&amp;path=1"을 붙여 보낸다.
        /// </summary>
        public static string ActionNotice(string level, string text, IReadOnlyList<ChatNoticeAction> actions, string? checkboxLabel = null) =>
            Build("notice", o =>
            {
                o["level"] = level; o["text"] = text;
                var arr = new JsonArray();
                foreach (var a in actions)
                    arr.Add(new JsonObject { ["text"] = a.Text, ["url"] = a.Url, ["pathOption"] = a.CarriesPathOption });
                o["actions"] = arr;
                if (!string.IsNullOrEmpty(checkboxLabel)) o["checkbox"] = checkboxLabel;
            });

        /// <summary>채팅 줄에 그림 썸네일 한 개. url은 결과 폴더 가상 호스트의 주소, path는 열 때 호스트로 돌려보내는 전체 경로.</summary>
        public static string Image(string url, string name, string path, string? caption) =>
            Build("image", o => { o["url"] = url; o["name"] = name; o["path"] = path; o["caption"] = caption ?? ""; });

        /// <summary>답변 마크다운의 상대 경로 그림(![제목](a.png))을 풀 주소 기준. 빈 값이면 그림을 글자로만 보인다.</summary>
        public static string ImageBase(string url) => Build("imageBase", o => o["url"] = url);

        public static string ModelNotice(string level, string text, params string[] models) =>
            Build("notice", o =>
            {
                o["level"] = level; o["text"] = text;
                var arr = new JsonArray();
                foreach (var m in models) if (!string.IsNullOrEmpty(m)) arr.Add(m);
                o["models"] = arr;
            });

        /// <summary>이제부터 답하는 모델("provider/model").</summary>
        public static string Model(string selector) => Build("model", o => o["model"] = selector);

        /// <summary>턴 종료: startedAt/endedAt은 1970 기준 ms(UTC), stopped는 사용자가 멈춘 경우.</summary>
        public static string TurnEnd(long startedAt, long endedAt, bool stopped) =>
            Build("turnEnd", o =>
            {
                if (startedAt <= 0 || endedAt < startedAt) return;
                o["started"] = startedAt; o["ended"] = endedAt; o["stopped"] = stopped;
            });

        public static string Clear() => Build("clear");
        public static string FocusInput() => Build("focusInput");
        public static string SetInput(string text) => Build("setInput", o => o["text"] = text);
        public static string Submitted(string id, bool ok) => Build("submitted", o => { o["id"] = id; o["ok"] = ok; });

        public static string Status(ChatStatus s) =>
            Build("status", o =>
            {
                o["state"] = s.State;
                o["error"] = s.Error;
                o["connected"] = s.Connected;
                o["ready"] = s.Ready;
                o["busy"] = s.Busy;
                o["shell"] = s.Shell;
                o["activity"] = s.Activity;
                o["model"] = s.Model;
                o["thinking"] = s.Thinking;
                o["context"] = s.Context;
                o["title"] = s.Title;
                o["project"] = s.Project;
                o["cwd"] = s.Cwd;
                o["pid"] = s.Pid;
                o["approval"] = s.Approval;
                o["approvalLocked"] = s.ApprovalLocked;
                o["workspaceLimit"] = s.WorkspaceLimit;
                o["workspaceLimitLabel"] = s.WorkspaceLimitLabel;
            });

        /// <summary>recent: omp의 사용 기록에서 고른 "최근 사용" 모델(models 안의 것만, 최근 순). 기록을 못 읽으면 빈 배열.</summary>
        public static string Catalog(IReadOnlyList<string> models, IReadOnlyList<string> levels, IReadOnlyDictionary<string, string> providers, IReadOnlyList<string>? recent = null) =>
            Build("catalog", o =>
            {
                o["models"] = ToArray(models);
                o["recent"] = ToArray(recent ?? Array.Empty<string>());
                o["levels"] = ToArray(levels);
                var p = new JsonObject();
                foreach (var kv in providers) p[kv.Key] = kv.Value;
                o["providers"] = p;
            });

        public static string Commands(IReadOnlyList<SlashCommandInfo> items) =>
            Build("commands", o =>
            {
                var arr = new JsonArray();
                foreach (var c in items)
                    arr.Add(new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["hint"] = c.Hint });
                o["items"] = arr;
            });

        /// <summary>approval 카드. lines의 접두 "+ " / "- " / "  "는 add/del/same, 그 밖은 text.</summary>
        public static string Approval(string id, string target, string summary, IReadOnlyList<string> lines)
        {
            const int MaxLines = 400;
            return Build("approval", o =>
            {
                o["id"] = id;
                o["target"] = target;
                o["summary"] = summary;
                var arr = new JsonArray();
                int shown = Math.Min(lines.Count, MaxLines);
                for (int i = 0; i < shown; i++)
                {
                    string line = lines[i];
                    string k, t;
                    if (line.StartsWith("+ ", StringComparison.Ordinal)) { k = "add"; t = line[2..]; }
                    else if (line.StartsWith("- ", StringComparison.Ordinal)) { k = "del"; t = line[2..]; }
                    else if (line.StartsWith("  ", StringComparison.Ordinal)) { k = "same"; t = line[2..]; }
                    else { k = "text"; t = line; }
                    arr.Add(new JsonObject { ["k"] = k, ["t"] = t });
                }
                if (lines.Count > shown)
                    arr.Add(new JsonObject { ["k"] = "text", ["t"] = $"… +{lines.Count - shown}" });
                o["lines"] = arr;
            });
        }

        public static string ApprovalResult(string id, bool ok) => Build("approvalResult", o => { o["id"] = id; o["ok"] = ok; });

        /// <summary>다시 불러온 대화. 마지막 답변(다음이 사용자 메시지이거나 끝)에는 턴 시작·종료 시각을 붙인다.</summary>
        public static string History(IReadOnlyList<HistoryItem> items) =>
            Build("history", o =>
            {
                var arr = new JsonArray();
                long turnStart = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    var j = new JsonObject { ["role"] = it.Role, ["text"] = it.Text };
                    if (it.Role == "user" && AttachmentContext.TryParse(it.Text, out var attached, out string visible))
                    {
                        j["text"] = visible;
                        j["attachments"] = AttachmentArray(attached);
                    }
                    if (it.Role == "user")
                    {
                        j["ts"] = it.Timestamp;
                        turnStart = it.Timestamp;
                    }
                    else
                    {
                        if (it.Model.Length > 0) j["model"] = it.Model;
                        bool lastOfTurn = i + 1 == items.Count || items[i + 1].Role == "user";
                        if (lastOfTurn && turnStart > 0 && it.CompletedAt >= turnStart)
                        {
                            j["started"] = turnStart;
                            j["ended"] = it.CompletedAt;
                            j["stopped"] = it.Stopped;
                        }
                    }
                    arr.Add(j);
                }
                o["items"] = arr;
            });

        /// <summary>사용량 패널: stats = get_session_stats 데이터(컨텍스트·토큰·비용), limits = 요금제 한도(UsageReport.Limits), provider = 표시 이름.</summary>
        public static string Usage(JsonNode? stats, string provider, bool loading, string error, JsonNode? limits) =>
            Build("usage", o =>
            {
                if (stats != null) o["stats"] = stats.DeepClone();
                o["provider"] = provider;
                o["loading"] = loading;
                o["error"] = error;
                if (limits != null) o["limits"] = limits.DeepClone();
            });

        public static string Sheet(string title, string markdown) => Build("sheet", o => { o["title"] = title; o["text"] = markdown; });

        private static JsonArray ToArray(IReadOnlyList<string> items)
        {
            var arr = new JsonArray();
            foreach (var s in items) arr.Add(s);
            return arr;
        }
    }

    /// <summary><see cref="ChatPageMessages.ActionNotice"/>의 버튼 한 개.</summary>
    internal sealed record ChatNoticeAction(string Text, string Url, bool CarriesPathOption = false);

    internal sealed record TodoItem(string Phase, string Content, string Status);

    internal sealed record SlashCommandInfo(string Name, string Description, string Hint);

    /// <summary>status 메시지 내용. Context: 사용률 %(알 수 없으면 -1).</summary>
    internal sealed record ChatStatus
    {
        public string State { get; init; } = "";
        public bool Error { get; init; }
        public bool Connected { get; init; }
        /// <summary>메시지를 보낼 수 있다: 연결됐거나, omp를 첫 메시지 때 시작하도록 미뤄 둔 상태(보내면 그때 시작).</summary>
        public bool Ready { get; init; }
        public bool Busy { get; init; }
        public bool Shell { get; init; }
        public string Activity { get; init; } = "";
        public string Model { get; init; } = "";
        public string Thinking { get; init; } = "";
        public double Context { get; init; } = -1;
        public string Title { get; init; } = "";
        public string Project { get; init; } = "";
        public string Cwd { get; init; } = "";
        public int Pid { get; init; }
        /// <summary>승인 모드 omp 값(always-ask | write | yolo). 페이지의 승인 선택이 이 값을 보여 준다(빈 값이면 선택을 숨긴다).</summary>
        public string Approval { get; init; } = "";
        /// <summary>비어 있지 않으면 승인 모드가 omp 추가 인자로 고정됨(드롭다운 비활성, 이 문구를 툴팁으로).</summary>
        public string ApprovalLocked { get; init; } = "";
        /// <summary>비어 있지 않으면 작업 공간 파일의 설정이 앱 설정을 조였다(채팅 바닥줄에 자물쇠 표시, 이 문구를 툴팁으로).</summary>
        public string WorkspaceLimit { get; init; } = "";
        /// <summary>자물쇠 표시의 짧은 글.</summary>
        public string WorkspaceLimitLabel { get; init; } = "";
    }
}
