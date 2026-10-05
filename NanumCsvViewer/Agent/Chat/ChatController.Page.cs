using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    // 페이지 → omp: 입력창·상단 바·메뉴 명령.
    public sealed partial class ChatController
    {
        private void OnPageMessage(JsonElement msg)
        {
            if (_disposed || !msg.IsObject()) return;
            switch (msg.Str("t"))
            {
                case "ready":
                    OnPageReady();
                    break;
                case "submit":
                    {
                        bool ok = Submit(msg.Str("text"), msg.Bool("followUp") == true);
                        _page.Post(ChatPageMessages.Submitted(msg.Str("id"), ok));
                        break;
                    }
                case "abort":
                    StopTurn();
                    break;
                case "newSession":
                    StartNewSession();
                    break;
                case "sessions":
                    PickSession();
                    break;
                case "export":
                    ExportConversation();
                    break;
                case "setModel":
                    SetModel(msg.Str("value"));
                    break;
                case "setThinking":
                    SetThinking(msg.Str("value"));
                    break;
                case "setApproval":
                    // 알 수 없는 값은 무시하고 선택을 현재 모드로 되돌린다(기본 모드로 조용히 넘어가지 않는다).
                    if (AgentApprovalPolicy.TryParse(msg.Str("value"), out var wanted)) TrySetApprovalMode(wanted);
                    else RefreshStatus(force: true);
                    break;
                case "cancelQueued":
                    CancelQueued(msg.Str("sent"), msg.Str("queue"));
                    break;
                case "runCommand":
                    Submit(msg.Str("text"), false);
                    break;
                case "abortRetry":
                    _client?.SendCommand("abort_retry");
                    break;
                case "subagentLog":
                    ShowSubagentLog(msg.Str("id"));
                    break;
                case "usage":
                    RequestUsage();
                    break;
                case "approval":
                    ResolveApproval(msg.Str("id"), msg.Bool("ok") == true);
                    break;
                case "copy":
                    Dialogs.SetClipboard(msg.Str("text"));
                    break;
                case "openUrl":
                    Dialogs.OpenUrl(msg.Str("url"));
                    break;
                default:
                    PageMessageUnhandled?.Invoke(msg);
                    break;
            }
        }

        /// <summary>페이지가 (다시) 준비되었다. 두 번째부터는 새로 만든 화면이므로 기록을 재생한다.</summary>
        private void OnPageReady()
        {
            _pageReadyCount++;
            if (_pageReadyCount > 1)
            {
                _page.Post(ChatPageMessages.Clear());
                foreach (string json in _stream.Replay()) _page.Post(json);
            }
            if (_imageBase.Length > 0) _page.Post(ChatPageMessages.ImageBase(_imageBase));
            PostCatalog();
            PostCommands();
            RefreshStatus(force: true);
        }

        // ---- 보내기 --------------------------------------------------------------------------------------------

        /// <summary>
        /// 입력창 제출. 대기 중이면 prompt, 작업 중이면 steer(다음 단계에 읽힘) 또는 followUp(턴 뒤). 슬래시 명령은 앱이 처리하거나 omp로 넘긴다.
        /// 보냈으면 true(페이지가 입력창을 비운다).
        /// </summary>
        public bool Submit(string text, bool followUp = false)
        {
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return false;
            if (IsStartDeferred) return StartDeferredWith(trimmed, followUp);   // 첫 사용: omp를 지금 시작하고 연결되면 이 메시지를 보낸다
            if (_client == null || !_connected)
            {
                _stream.Emit(ChatPageMessages.Notice("warn", T("The agent is not connected.", "에이전트가 연결되어 있지 않습니다.")));
                return false;
            }
            if (trimmed.StartsWith('!') && trimmed.Length > 1) return RunShell(trimmed[1..].Trim());

            var route = SlashRoutes.Route(trimmed, OmpCommandNames);
            switch (route.Kind)
            {
                case SlashKind.Prompt:
                    if (_shellRunning) return Busy();
                    return _activity.Busy ? QueueMessage(trimmed, followUp) : SendPrompt(trimmed);
                case SlashKind.PassThrough:
                    if (IsBusy) return Busy();
                    return SendPrompt(trimmed);
                case SlashKind.Abort:
                    StopTurn();
                    return true;
                case SlashKind.Queue:
                    if (route.Args.Length == 0)
                    {
                        _stream.Emit(ChatPageMessages.Notice("warn", T("Usage: /queue <message>", "사용법: /queue <메시지>")));
                        return false;
                    }
                    return IsBusy ? QueueMessage(route.Args, followUp: true) : SendPrompt(route.Args);
                case SlashKind.Copy:
                    CopyLastAnswer();
                    return true;
                case SlashKind.Version:
                    _stream.Emit(ChatPageMessages.Notice("info",
                        $"Nanum CSV Viewer {_options.AppVersion} · omp {(_ompVersion?.ToString() ?? "?")}".Trim()));
                    return true;
                case SlashKind.Settings:
                    PageMessageUnhandled?.Invoke(Synthetic("{\"t\":\"settings\"}"));
                    return true;
                case SlashKind.TerminalOnly:
                    _stream.Emit(ChatPageMessages.Notice("warn",
                        T($"/{route.Name} only works in the omp terminal.", $"/{route.Name} 명령은 omp 터미널에서만 쓸 수 있습니다.")));
                    return false;
            }

            // 아래는 작업 중에는 실행할 수 없는 명령
            if (IsBusy) return Busy();
            switch (route.Kind)
            {
                case SlashKind.NewSession:
                case SlashKind.Clear:
                    return StartNewSession();
                case SlashKind.Restart:
                    _ = RestartSessionAsync();
                    return true;
                case SlashKind.ListModels:
                    ChooseModel();
                    return true;
                case SlashKind.SetModel:
                    SetModel(route.Arg1 + "/" + route.Arg2);
                    return true;
                case SlashKind.Fast:
                    SetFast(route.Arg1);
                    return true;
                case SlashKind.Thinking:
                    if (route.Arg1.Length > 0) SetThinking(route.Arg1); else ChooseThinking();
                    return true;
                case SlashKind.Login:
                    Login(route.Args);
                    return true;
            }
            return false;
        }

        private bool Busy()
        {
            _stream.Emit(ChatPageMessages.Notice("warn", T("The agent is busy. Stop it or wait until it finishes.", "에이전트가 작업 중입니다. 중지하거나 끝날 때까지 기다리세요.")));
            return false;
        }

        private bool SendPrompt(string message)
        {
            var client = _client;
            if (client == null || !client.TryRequest(id => RpcProtocol.Message("prompt", id, message), out var task))
            {
                _stream.Emit(ChatPageMessages.Notice("error", T("Could not send the message to omp.", "메시지를 omp로 보내지 못했습니다.")));
                return false;
            }
            _stream.Emit(ChatPageMessages.User(message, _clock.UnixMs));
            // 이 턴에서 에이전트가 만드는 뷰의 출처에 적는다(슬래시 명령은 사용자 요청이 아니다).
            if (!message.StartsWith('/')) _lastUserRequest = message;
            _activity.PromptSent();
            Track(client, task, "prompt",
                data =>
                {
                    if (data.Bool("agentInvoked") == false)
                    {
                        _activity.Apply(new AgentEvent { Kind = AgentEventKind.PromptLocal });
                        RefreshStatus();
                    }
                },
                error =>
                {
                    _activity.AbandonTurn();
                    _stream.Emit(ChatPageMessages.Notice("error", "prompt: " + error));
                    RefreshStatus();
                });
            RefreshStatus();
            return true;
        }

        private bool QueueMessage(string text, bool followUp)
        {
            var client = _client;
            string type = followUp ? "follow_up" : "steer";
            if (client == null || !client.TryRequest(id => RpcProtocol.Message(type, id, text), out var task))
            {
                _stream.Emit(ChatPageMessages.Notice("error", T("Could not send the message to omp.", "메시지를 omp로 보내지 못했습니다.")));
                return false;
            }
            _stream.Emit(ChatPageMessages.QueuedUser(text, followUp ? "followUp" : "steer", text, _clock.UnixMs));
            Track(client, task, type, null, error => _stream.Emit(ChatPageMessages.Notice("error", $"{type}: {error}")));
            return true;
        }

        private bool RunShell(string command)
        {
            if (command.Length == 0) return false;
            if (IsBusy) return Busy();
            _shellRunning = true;
            _stream.Emit(ChatPageMessages.User("!" + command, _clock.UnixMs));
            RefreshStatus();
            bool sent = Ask("bash", o => o["command"] = command,
                data =>
                {
                    _shellRunning = false;
                    var sb = new StringBuilder(data.Str("output").TrimEnd());
                    if (data.Bool("cancelled") == true) sb.Append('\n').Append(T("(cancelled)", "(취소됨)"));
                    else if (data.Int("exitCode") != 0) sb.Append('\n').Append(T($"(exit code {data.Int("exitCode")})", $"(종료 코드 {data.Int("exitCode")})"));
                    if (data.Bool("truncated") == true) sb.Append('\n').Append(T("(output truncated)", "(출력이 잘렸습니다)"));
                    if (sb.ToString().Trim().Length > 0) _stream.Emit(ChatPageMessages.Notice("output", sb.ToString().Trim()));
                    RefreshStatus();
                },
                error =>
                {
                    _shellRunning = false;
                    _stream.Emit(ChatPageMessages.Notice("error", "bash: " + error));
                    RefreshStatus();
                });
            if (!sent) { _shellRunning = false; RefreshStatus(); }
            return sent;
        }

        // ---- 세션·모델·생각 ------------------------------------------------------------------------------------

        private bool StartNewSession()
        {
            if (_client == null || !_connected) return false;
            if (IsBusy) return Busy();
            if (!Dialogs.Confirm(T("New conversation", "새 대화"),
                    T("Start a new conversation? The current one stays saved.", "새 대화를 시작할까요? 지금 대화는 저장되어 있습니다.")))
                return false;
            return Ask("new_session", null, data =>
            {
                if (data.Bool("cancelled") == true) return;
                _evalApproval.Reset();
                _stream.Clear();
                _page.Post(ChatPageMessages.Clear());
                RequestState();
            });
        }

        private void SetModel(string selector)
        {
            int slash = selector.IndexOf('/');
            if (slash <= 0 || slash >= selector.Length - 1) return;
            if (IsBusy) { Busy(); return; }
            string provider = selector[..slash], modelId = selector[(slash + 1)..];
            Ask("set_model", o => { o["provider"] = provider; o["modelId"] = modelId; }, _ =>
            {
                // 생각 수준 목록은 모델에 따라 달라진다.
                RequestLevels();
                RequestState();
            });
        }

        private void SetThinking(string level)
        {
            if (level.Length == 0) return;
            if (IsBusy) { Busy(); return; }
            Ask("set_thinking_level", o => o["level"] = level, _ => RequestState());
        }

        private void ChooseModel() =>
            Ask("get_available_models", null, data =>
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var m in data.Child("models").Items())
                    if (m.Str("provider").Length > 0 && m.Str("id").Length > 0) set.Add(m.Str("provider") + "/" + m.Str("id"));
                string? choice = Dialogs.Select(T("Choose a model", "모델 선택"), set.ToList());
                if (choice != null) SetModel(choice);
            });

        private void ChooseThinking()
        {
            string? choice = Dialogs.Select(T("Thinking level", "생각 수준"), _levels.Count > 0 ? _levels : DefaultLevels.ToList());
            if (choice != null) SetThinking(choice);
        }

        private void SetFast(string arg)
        {
            if (arg.Length == 0)
            {
                var options = new List<string> { "on", "off" };
                if (_ompCommands.Any(c => c.Name == "fast" && (c.Description.Contains("ultra", StringComparison.OrdinalIgnoreCase) || c.Hint.Contains("ultra", StringComparison.OrdinalIgnoreCase))))
                    options.Insert(1, "ultra");
                string? choice = Dialogs.Select(T("Fast mode", "빠른 모드"), options);
                if (choice == null) return;
                arg = choice;
            }
            if (arg == "ultra") { SendPrompt("/fast ultra"); return; }
            bool on = arg == "on";
            Ask("set_fast_mode", o => o["enabled"] = on, _ => _stream.Emit(ChatPageMessages.Notice("info", T(on ? "Fast mode on." : "Fast mode off.", on ? "빠른 모드를 켰습니다." : "빠른 모드를 껐습니다."))));
        }

        private void CopyLastAnswer() =>
            Ask("get_last_assistant_text", null, data =>
            {
                string text = data.ValueKind == JsonValueKind.String ? data.GetString() ?? "" : data.Str("text");
                if (text.Length == 0)
                {
                    _stream.Emit(ChatPageMessages.Notice("info", T("There is no answer to copy yet.", "복사할 답변이 아직 없습니다.")));
                    return;
                }
                Dialogs.SetClipboard(text);
                _stream.Emit(ChatPageMessages.Notice("info", T("Copied the last answer.", "마지막 답변을 복사했습니다.")));
            });

        private void Login(string arg)
        {
            Ask("get_login_providers", null, data =>
            {
                var names = new List<string>();
                var ids = new Dictionary<string, string>();
                foreach (var p in data.Child("providers").Items())
                {
                    if (p.Bool("available") == false) continue;
                    string id = p.Str("id");
                    if (id.Length == 0) continue;
                    string label = p.Str("name", id) + (p.Bool("authenticated") == true ? " ✓" : "");
                    names.Add(label);
                    ids[label] = id;
                }
                string? providerId = null;
                if (arg.Length > 0)
                    providerId = ids.Values.FirstOrDefault(i => string.Equals(i, arg, StringComparison.OrdinalIgnoreCase));
                if (providerId == null)
                {
                    string? choice = Dialogs.Select(T("Sign in to a provider", "로그인할 제공자"), names);
                    if (choice == null) return;
                    providerId = ids[choice];
                }
                Ask("login", o => o["providerId"] = providerId,
                    _ =>
                    {
                        _stream.Emit(ChatPageMessages.Notice("info", T("Signed in.", "로그인했습니다.")));
                        RequestModels();
                        RequestProviders();
                    },
                    error => _stream.Emit(ChatPageMessages.Notice("error", "login: " + error)));
            });
        }

        private void CancelQueued(string sent, string queue)
        {
            string ompQueue = queue == "followUp" ? "followUp" : "steering";
            var client = _client;
            if (client == null || !_connected || !client.TryRequest(id => RpcProtocol.RemoveQueued(id, sent, ompQueue), out var task))
            {
                _page.Post(ChatPageMessages.QueueRemoved(sent, queue, false));
                return;
            }
            Track(client, task, "remove_queued_message",
                data =>
                {
                    bool removed = data.Bool("removed") == true;
                    _page.Post(ChatPageMessages.QueueRemoved(sent, queue, removed));
                    if (!removed)
                        _stream.Emit(ChatPageMessages.Notice("info", T("The agent already read that message.", "에이전트가 이미 그 메시지를 읽었습니다.")));
                },
                error =>
                {
                    _page.Post(ChatPageMessages.QueueRemoved(sent, queue, false));
                    _stream.Emit(ChatPageMessages.Notice("error", "remove_queued_message: " + error));
                });
        }

        private void ExportConversation()
        {
            if (_client == null || !_connected) return;
            string? path = Dialogs.PickExportPath($"NanumCsvViewer-agent-{DateTime.Now:yyyyMMdd-HHmm}.html");
            if (path == null) return;
            Ask("export_html", o => o["outputPath"] = path,
                _ => _stream.Emit(ChatPageMessages.Notice("info", T("Saved: ", "저장했습니다: ") + path)));
        }

        // ---- 하위 에이전트 기록 · 세션 목록 --------------------------------------------------------------------

        private void ShowSubagentLog(string id)
        {
            if (id.Length == 0) return;
            Ask("get_subagent_messages", o => o["subagentId"] = id, data =>
            {
                var sb = new StringBuilder();
                foreach (var m in data.Child("messages").Items())
                {
                    string text = m.Child("content").ContentText();
                    if (text.Length == 0) continue;
                    sb.Append("**").Append(m.Str("role", "message")).Append("**\n\n").Append(text).Append("\n\n");
                }
                if (sb.Length == 0) sb.Append(T("No messages yet.", "아직 메시지가 없습니다."));
                _page.Post(ChatPageMessages.Sheet(T("Subagent ", "하위 에이전트 ") + id, sb.ToString()));
            });
        }

        /// <summary>
        /// 세션 선택 창: omp가 이 작업 폴더에 저장한 대화를 최근 순으로, 맨 위에 "새 대화". 고르면 switch_session 뒤 기록을 다시 불러온다.
        /// </summary>
        private void PickSession()
        {
            if (_client == null || !_connected || IsBusy) return;
            string newLabel = T("+ New conversation", "+ 새 대화");
            string currentMark = T("  (current)", "  (현재)");
            var labels = new List<string> { newLabel };
            var paths = new Dictionary<string, string>();
            string? dir = SessionCatalog.FindDirectory(_sessionFile, _workDir, _svc.SessionRoot);
            if (dir != null)
            {
                foreach (var s in SessionCatalog.List(dir, _sessionFile))
                {
                    bool isCurrent = string.Equals(s.Path, _sessionFile, StringComparison.OrdinalIgnoreCase);
                    string label = SessionCatalog.Label(s, isCurrent, currentMark);
                    if (paths.ContainsKey(label)) label += " · " + Path.GetFileNameWithoutExtension(s.Path);
                    labels.Add(label);
                    paths[label] = s.Path;
                }
            }
            string? choice = Dialogs.Select(T("Conversations", "대화 목록"), labels);
            if (choice == null) return;
            if (choice == newLabel) { StartNewSession(); return; }
            string path = paths[choice];
            if (string.Equals(path, _sessionFile, StringComparison.OrdinalIgnoreCase)) return;
            Ask("switch_session", o => o["sessionPath"] = path, data =>
            {
                if (data.Bool("cancelled") == true) return;
                _evalApproval.Reset();
                _stream.Clear();
                _page.Post(ChatPageMessages.Clear());
                RequestState();
                LoadHistory();
            });
        }

        /// <summary>get_messages_page를 끝까지 읽어 history 메시지로 보낸다(세션 전환 뒤 화면 복원).</summary>
        private void LoadHistory(string? cursor = null, List<HistoryItem>? items = null, Action? done = null)
        {
            items ??= new List<HistoryItem>();
            void Finish()
            {
                _stream.Emit(ChatPageMessages.History(items));
                done?.Invoke();   // history 메시지가 화면을 새로 그리므로 그 뒤에 보여야 하는 알림은 여기서
            }
            Ask("get_messages_page", o => { o["limit"] = 256; if (cursor != null) o["cursor"] = cursor; },
                data =>
                {
                    HistoryItem.Append(items, data.Child("messages"));
                    string next = data.Str("nextCursor");
                    if (next.Length > 0 && items.Count < 4000) LoadHistory(next, items, done);
                    else Finish();
                },
                _ => Finish());
        }

        private static JsonElement Synthetic(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }

    /// <summary>다시 불러온 대화의 한 메시지(사용자/어시스턴트). Timestamp/CompletedAt은 1970 기준 ms, 0이면 모름.</summary>
    internal sealed class HistoryItem
    {
        public string Role = "", Text = "", Model = "";
        public long Timestamp, CompletedAt;
        public bool Stopped;

        /// <summary>messages 배열의 user/assistant 메시지를 이어 붙인다. 글 없는 어시스턴트 메시지(도구 호출만)는 앞 답변의 종료 시각으로 합친다.</summary>
        public static void Append(List<HistoryItem> items, JsonElement messages)
        {
            foreach (var m in messages.Items())
            {
                string role = m.Str("role");
                if (role != "user" && role != "assistant") continue;
                string text = m.Child("content").ContentText();
                long ts = m.Int("timestamp");
                bool stopped = m.Str("stopReason") == "aborted";
                if (text.Length == 0 && role == "assistant" && items.Count > 0 && items[^1].Role == "assistant")
                {
                    items[^1].CompletedAt = m.Int("completedAt", ts);
                    items[^1].Stopped = stopped;
                    continue;
                }
                if (text.Length == 0 && role == "user") continue;
                items.Add(new HistoryItem
                {
                    Role = role,
                    Text = text,
                    Timestamp = ts,
                    CompletedAt = role == "assistant" ? m.Int("completedAt", ts) : 0,
                    Stopped = stopped,
                    Model = role == "assistant" && m.Str("model").Length > 0 ? m.Str("provider") + "/" + m.Str("model") : "",
                });
            }
        }
    }
}
