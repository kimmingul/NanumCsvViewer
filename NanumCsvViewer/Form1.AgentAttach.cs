using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    // 채팅 입력창의 + 메뉴·끌어놓기: 고른 파일을 작업 공간 원본으로 올린다(AddSourcesAsync). 탭은 열지 않는다.
    public partial class Form1
    {
        internal IChatAttachHost CreateAgentAttachHost() => new AgentAttachHost(this);

        private sealed class AgentAttachHost : IChatAttachHost
        {
            private readonly Form1 _form;

            public AgentAttachHost(Form1 form) => _form = form;

            public string? UnavailableReason => _form.WorkspaceUnavailableReason;

            public async Task<ChatAttachOutcome> AddAsync(IReadOnlyList<string> files, CancellationToken cancellation)
            {
                var added = new List<ChatAttachment>();
                var failed = new List<(string File, string Error)>();
                if (_form.IsDisposed || _form._closing) return new ChatAttachOutcome(added, failed);
                if (_form.WorkspaceOperationRunning)
                    return new ChatAttachOutcome(added, failed,
                        LT("another workspace operation is still running — wait or cancel it first.", "다른 작업 공간 작업이 실행 중입니다. 끝나길 기다리거나 먼저 취소하세요."));

                // 진행 표시·취소·오류 안내는 탐색기에서 파일을 추가할 때와 같은 경로(RunWorkspaceUiAsync). 탐색기 패널을 새로 펼치지는 않는다.
                await _form.RunWorkspaceUiAsync(LT("Adding files…", "파일 추가 중…"), async ct =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellation);
                    foreach (string file in files)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        try
                        {
                            foreach (var src in await _form.AddSourcesAsync(new[] { file }, linked.Token))
                                added.Add(Describe(src));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { failed.Add((file, ErrorText(ex))); }
                    }
                }, showExplorer: false);
                if (added.Count > 0 && !_form.IsDisposed)
                    _form.statusLabel.Text = LT($"Added {added.Count} source(s) to the workspace.", $"원본 {added.Count}개를 작업 공간에 추가했습니다.");
                return new ChatAttachOutcome(added, failed);
            }

            private static ChatAttachment Describe(WorkspaceSource src)
                => new(src.Path.Length > 0 ? Path.GetFileName(src.Path) : src.Name, src.Path, src.Tables.Select(t => t.DisplayName).ToList());
        }
    }
}
