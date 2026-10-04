using System.Text.Json;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    // 승인 카드(IAgentApprovals)와 omp extension_ui_request.
    public sealed partial class ChatController
    {
        private static readonly string[] ApproveWords = { "approve", "allow", "accept", "yes", "승인", "허용" };
        private static readonly string[] DenyWords = { "deny", "reject", "decline", "no", "거부", "거절" };

        private readonly Dictionary<string, TaskCompletionSource<bool>> _approvals = new();
        /// <summary>omp extension_ui_request id → 승인 카드 id(omp가 cancel을 보내면 카드를 닫기 위함).</summary>
        private readonly Dictionary<string, string> _approvalByUiId = new();
        private int _approvalCounter;
        private string _lastExtensionStatus = "";
        /// <summary>Python eval 승인 기억(대화마다 한 번). 새 대화·세션 전환·(재)시작에서 지운다.</summary>
        private readonly EvalApprovalMemory _evalApproval = new();

        // ---- IAgentApprovals -----------------------------------------------------------------------------------

        /// <summary>채팅에 승인 카드를 띄우고 사용자의 답을 기다린다. 거부·중지·자식 종료·취소·창 닫힘이면 false.</summary>
        public Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation) =>
            ApproveCore(target, summary, lines, cancellation, null);

        private async Task<bool> ApproveCore(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation, string? ompUiId)
        {
            if (_disposed || _client == null || cancellation.IsCancellationRequested) return false;
            string id = "approval-" + (++_approvalCounter);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _approvals[id] = tcs;
            if (ompUiId != null) _approvalByUiId[ompUiId] = id;
            _page.Post(ChatPageMessages.Approval(id, target, summary, lines));

            var ui = SynchronizationContext.Current;
            CancellationTokenRegistration registration = default;
            if (cancellation.CanBeCanceled)
            {
                registration = cancellation.Register(() =>
                {
                    if (ui != null && SynchronizationContext.Current != ui) ui.Post(_ => ResolveApproval(id, false), null);
                    else ResolveApproval(id, false);
                });
            }
            try { return await tcs.Task; }
            finally
            {
                registration.Dispose();
                if (ompUiId != null) _approvalByUiId.Remove(ompUiId);
            }
        }

        /// <summary>카드에 답을 준다(한 번만). 이미 닫힌 카드는 무시.</summary>
        private void ResolveApproval(string id, bool ok)
        {
            if (!_approvals.Remove(id, out var tcs)) return;
            tcs.TrySetResult(ok);
            _page.Post(ChatPageMessages.ApprovalResult(id, ok));
        }

        /// <summary>중지·자식 종료·창 닫힘: 열린 모든 카드를 거절한다.</summary>
        private void RefuseAllApprovals()
        {
            foreach (string id in _approvals.Keys.ToArray()) ResolveApproval(id, false);
        }

        // ---- extension_ui_request ------------------------------------------------------------------------------

        private void HandleUiRequest(OmpRpcClient client, JsonElement frame)
        {
            string id = frame.Str("id"), method = frame.Str("method");
            if (id.Length == 0) return;
            string title = frame.Str("title");
            string message = frame.Str("message");
            if (message.Length == 0) message = frame.Str("instructions");

            switch (method)
            {
                case "notify":
                    {
                        string level = frame.Str("notifyType") switch { "warning" or "warn" => "warn", "error" => "error", _ => "omp" };
                        if (message.Length > 0) _stream.Emit(ChatPageMessages.Notice(level, message));
                        break;
                    }
                case "setStatus":
                    {
                        string text = frame.Str("statusText");
                        if (text.Length == 0) text = message;
                        if (text.Length > 0 && text != _lastExtensionStatus)
                            _stream.Emit(ChatPageMessages.Notice("omp", text));
                        _lastExtensionStatus = text;
                        break;
                    }
                case "open_url":
                    {
                        string url = frame.Str("url");
                        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            Dialogs.OpenUrl(url);
                            string text = (message + " " + url).Trim();
                            _stream.Emit(ChatPageMessages.Notice("info", T("Finish signing in in your browser: ", "브라우저에서 로그인을 마치세요: ") + text));
                        }
                        break;
                    }
                case "set_editor_text":
                    _page.Post(ChatPageMessages.SetInput(frame.Str("text")));
                    break;
                case "cancel":
                    {
                        string target = frame.Str("targetId");
                        if (target.Length > 0 && _approvalByUiId.TryGetValue(target, out string? card)) ResolveApproval(card, false);
                        break;
                    }
                case "confirm":
                    AnswerWithDialog(client, frame, id, () =>
                        RpcProtocol.UiConfirmed(id, Dialogs.Confirm(title.Length > 0 ? title : "omp", message)));
                    break;
                case "input":
                case "editor":
                    AnswerWithDialog(client, frame, id, () =>
                    {
                        string prompt = message.Length > 0 ? message : title;
                        string initial = frame.Str("prefill");
                        if (initial.Length == 0) initial = frame.Str("placeholder");
                        string? value = Dialogs.Input(title, prompt, method == "editor" ? initial : "", method == "editor");
                        return value == null ? RpcProtocol.UiCancelled(id) : RpcProtocol.UiValue(id, value);
                    });
                    break;
                case "select":
                    HandleSelect(client, frame, id, title, message);
                    break;
            }
        }

        /// <summary>모달을 띄워 답을 만들고, 그동안 자식이 바뀌었으면 답하지 않는다. 창을 못 띄우면 기본 답.</summary>
        private void AnswerWithDialog(OmpRpcClient client, JsonElement request, string id, Func<string> ask)
        {
            string reply;
            try { reply = ask(); }
            catch (Exception ex)
            {
                _log.Note("dialog failed: " + ex.Message);
                reply = DefaultUiReply(request) ?? RpcProtocol.UiCancelled(id);
            }
            if (ReferenceEquals(client, _client)) client.Send(reply);
        }

        private async void HandleSelect(OmpRpcClient client, JsonElement frame, string id, string title, string message)
        {
            try
            {
                var options = frame.Child("options").Strings();
                string? approve = OptionStarting(options, ApproveWords);
                string? deny = OptionStarting(options, DenyWords);

                if (title.TrimStart().StartsWith("Allow tool", StringComparison.OrdinalIgnoreCase) && approve != null && deny != null)
                {
                    string[] titleLines = title.Split('\n');
                    string first = titleLines[0].Trim();
                    string second = titleLines.Length > 1 ? titleLines[1].Trim() : "";
                    // csv.* 도구는 CsvHostTools가 편집·저장을 직접 묻는다: omp의 중복 승인 없이 통과.
                    if (first.StartsWith("Allow tool: csv.", StringComparison.OrdinalIgnoreCase) ||
                        second.StartsWith("Path: xd://csv.", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ReferenceEquals(client, _client)) client.Send(RpcProtocol.UiValue(id, approve));
                        return;
                    }
                    var lines = titleLines.Skip(1).ToList();
                    if (message.Length > 0) lines.AddRange(message.Split('\n'));
                    // omp 승인 본문은 diff가 아니라 원문(예: 쓸 파일 내용)이다. 카드가 "+ "/"- "로 시작하는 줄을 추가/삭제로 색칠하지 않도록
                    // 문맥 접두("  ")를 붙여 그대로 보이게 한다(마크다운 목록 "- …"이 빨간 삭제 줄로 보이던 문제).
                    var cardLines = lines.Select(l => l.TrimEnd('\r'))
                        .Select(l => l.StartsWith("+ ", StringComparison.Ordinal) || l.StartsWith("- ", StringComparison.Ordinal) ? "  " + l : l)
                        .ToList();

                    // Python 실행(omp eval, language python)은 대화마다 한 번만 묻는다: 한 번 승인하면 이 대화의 이후 질문은 자동 승인.
                    if (OmpApprovalPrompt.IsPythonEval(title))
                    {
                        if (_evalApproval.Approved)
                        {
                            if (!_evalApproval.NoticePosted)
                            {
                                _evalApproval.MarkNoticePosted();
                                _stream.Emit(ChatPageMessages.Notice("info", T("Python run approved (this conversation)", "Python 실행 승인됨(이 대화)")));
                            }
                            if (ReferenceEquals(client, _client)) client.Send(RpcProtocol.UiValue(id, approve));
                            return;
                        }
                        bool pyOk = await ApproveCore(first, T("Approving also allows later Python runs in this conversation without asking.",
                            "승인하면 이 대화의 이후 Python 실행은 다시 묻지 않습니다."), cardLines, CancellationToken.None, id);
                        if (pyOk && ReferenceEquals(client, _client)) _evalApproval.Remember();
                        if (ReferenceEquals(client, _client)) client.Send(RpcProtocol.UiValue(id, pyOk ? approve : deny));
                        return;
                    }

                    bool ok = await ApproveCore(first, "", cardLines, CancellationToken.None, id);
                    if (ReferenceEquals(client, _client)) client.Send(RpcProtocol.UiValue(id, ok ? approve : deny));
                    return;
                }

                string? choice = options.Count == 0 ? null : Dialogs.Select(title.Length > 0 ? title : "omp", options);
                if (ReferenceEquals(client, _client))
                    client.Send(choice == null ? RpcProtocol.UiCancelled(id) : RpcProtocol.UiValue(id, choice));
            }
            catch (Exception ex)
            {
                _log.Note("select failed: " + ex.Message);
                if (ReferenceEquals(client, _client)) client.Send(DefaultUiReply(frame) ?? RpcProtocol.UiCancelled(id));
            }
        }

        private static string? OptionStarting(IReadOnlyList<string> options, string[] words)
        {
            foreach (string option in options)
            {
                string o = option.Trim().ToLowerInvariant();
                foreach (string w in words)
                    if (o.StartsWith(w, StringComparison.Ordinal)) return option;
            }
            return null;
        }

        /// <summary>
        /// 대화 상자를 쓸 수 없을 때의 기본 답: input/editor는 취소, select는 첫 옵션(없으면 취소), confirm은 확인. 그 밖은 답 없음.
        /// </summary>
        internal static string? DefaultUiReply(JsonElement request)
        {
            string id = request.Str("id");
            if (id.Length == 0) return null;
            switch (request.Str("method"))
            {
                case "input":
                case "editor":
                    return RpcProtocol.UiCancelled(id);
                case "select":
                    {
                        var options = request.Child("options").Strings();
                        return options.Count > 0 ? RpcProtocol.UiValue(id, options[0]) : RpcProtocol.UiCancelled(id);
                    }
                case "confirm":
                    return RpcProtocol.UiConfirmed(id, true);
                default:
                    return null;
            }
        }
    }
}
