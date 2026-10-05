using System.IO;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    // 파일 ▸ 새 작업 공간…: 저장 확인 → 위치·이름 대화 상자 → (덮어쓰기 확인) → 탭 정리 → 새 .ncvws를 바로 쓰고 현재 작업 공간으로 삼는다.
    // 닫기 로직(Form1.WorkspaceFile.cs의 ResetWorkspaceSession)과 저장 로직(WriteWorkspaceFile)을 그대로 쓴다.
    public partial class Form1
    {
        /// <summary>새 작업 공간 대화 상자를 대신하는 이음매(테스트용). (초기값, 열린 파일이 있는가) → 고른 값, 취소면 null. null이면 실제 대화 상자.</summary>
        internal Func<NewWorkspaceRequest, bool, NewWorkspaceRequest?>? NewWorkspacePrompt;

        /// <summary>같은 이름 파일을 덮어쓸지 묻는 확인을 대신하는 이음매(테스트용). 인자는 파일 경로. null이면 실제 MessageBox.</summary>
        internal Func<string, bool>? WorkspaceOverwriteConfirm;

        private static bool IsDataTab(DocumentTab t) => t.Kind is TabKind.File or TabKind.Sheet;

        private string DefaultNewWorkspaceFolder() =>
            _settings.RecentWorkspaces?.Select(Path.GetDirectoryName).FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        private static string UniqueWorkspaceName(string folder)
        {
            string baseName = ViewerSupport.LT("Workspace", "작업 공간");
            string name = baseName;
            for (int i = 2; File.Exists(Path.Combine(folder, name + WorkspaceFile.Extension)); i++) name = $"{baseName} {i}";
            return name;
        }

        /// <summary>
        /// 새 작업 공간을 만든다. 저장 확인·덮어쓰기 확인·저장 안 한 편집 확인에서 취소하면 아무것도 바꾸지 않고 false.
        /// "지금 열린 파일 포함"이 꺼져 있으면 모든 탭과 현재 작업 공간 상태를 닫기와 똑같이 비우고, 켜져 있으면 파일·시트 탭을 남겨 원본으로 등록한다(뷰·질의 결과 탭은 닫는다).
        /// 새 파일을 바로 쓰고 현재 작업 공간 파일·상태 표시줄·최근 목록·에이전트(새 대화 + 새 분석 폴더)를 맞춘다.
        /// </summary>
        internal async Task<bool> NewWorkspaceAsync()
        {
            if (_wfBusy || _closing || IsDisposed) return false;
            bool hadChanges = WorkspaceNeedsSave();
            if (!ConfirmWorkspaceSaved()) return false;

            bool openFiles = _tabs.Any(IsDataTab);
            string folder = DefaultNewWorkspaceFolder();
            var initial = new NewWorkspaceRequest(folder, UniqueWorkspaceName(folder), false);
            NewWorkspaceRequest req;
            while (true)
            {
                NewWorkspaceRequest? picked;
                if (NewWorkspacePrompt is { } hook) picked = hook(initial, openFiles);
                else
                {
                    using var dlg = new NewWorkspaceDialog(_palette, initial, openFiles);
                    picked = dlg.ShowDialog(this) == DialogResult.OK ? dlg.Result : null;
                }
                if (picked is null) return false;
                req = picked;
                initial = req;
                if (IsWorkspaceSourcePath(req.FilePath))
                {
                    MessageBox.Show(this, LT("That name is used by a file that is open in the workspace; choose another name. Source files are never overwritten.",
                        "작업 공간에 열려 있는 파일과 같은 이름입니다. 다른 이름을 고르세요. 원본 파일은 덮어쓰지 않습니다."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    continue;
                }
                if (File.Exists(req.FilePath) && !ConfirmWorkspaceOverwrite(req.FilePath)) continue;   // 아니오 → 대화 상자로 돌아가 다른 이름을 고른다
                break;
            }

            // 닫을 탭: 포함이 꺼져 있으면 전부, 켜져 있으면 파일·시트가 아닌 탭(뷰·질의 결과).
            var toClose = _tabs.Where(t => !req.IncludeOpenFiles || !IsDataTab(t)).ToArray();
            var dirty = toClose.Where(t => t.HasUnsavedEdits).ToList();
            if (!ConfirmDiscardTabs(dirty)) return false;

            if (!hadChanges) SaveWorkspaceLayoutOnClose();   // 닫는 작업 공간의 패널 배치만 조용히 반영(닫기와 같게)

            // 파일을 쓸 수 있는지 먼저 확인한다(빈 새 작업 공간을 실제로 쓴다): 실패하면 아무것도 닫지 않은 채 중단.
            var startup = StartupLayout();
            try
            {
                WorkspaceFile.Save(req.FilePath, WorkspaceFile.Capture(req.FilePath, Array.Empty<WorkspaceCaptureSource>(), Array.Empty<WorkspaceFileView>(),
                    Array.Empty<WorkspaceFileView>(), Array.Empty<WorkspaceCaptureTab>(), -1, startup.Explorer, null, startup));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                MessageBox.Show(this, LT("Could not create the workspace: ", "작업 공간을 만들지 못했습니다: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // 탭 닫기(저장 안 한 편집은 위에서 이미 확인했다). 활성 탭은 마지막에 닫는다(CloseAllTabs와 같은 순서).
            foreach (var tab in toClose.Where(t => !ReferenceEquals(t, _t))) CloseConfirmed(tab, flushJournal: !dirty.Contains(tab));
            if (toClose.FirstOrDefault(t => ReferenceEquals(t, _t)) is { } active) CloseConfirmed(active, flushJournal: !dirty.Contains(active));

            var problems = ResetWorkspaceSession(keepTitle: _tabs.Count > 0);

            if (req.IncludeOpenFiles && _tabs.Any(IsDataTab))
            {
                _wfBusy = true;
                try
                {
                    if (await RegisterOpenTabsAsync(CancellationToken.None) is { } failure)
                        problems.Add(LT("Some open files could not be added as sources: ", "열린 파일 중 일부를 원본으로 등록하지 못했습니다: ") + failure.Message);
                }
                finally { _wfBusy = false; }
                if (IsDisposed) return false;
            }

            if (!WriteWorkspaceFile(req.FilePath, freshConversation: true)) return false;
            SwitchAgentToOpenedWorkspace();   // 새 작업 공간 파일·분석 폴더로, 대화는 새 대화로(떠 있지 않으면 첫 사용 때)
            statusLabel.Text = LT("Workspace created: ", "새 작업 공간을 만들었습니다: ") + req.FilePath;
            UpdateCloseWorkspaceMenu();
            if (problems.Count > 0)
                MessageBox.Show(this, string.Join("\n", problems.Take(10).Select(p => "• " + p)), LT("New Workspace", "새 작업 공간"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        private bool ConfirmWorkspaceOverwrite(string path)
        {
            if (WorkspaceOverwriteConfirm is { } hook) return hook(path);
            return MessageBox.Show(this,
                LT($"'{path}' already exists. Replace it with a new workspace?", $"'{path}'이(가) 이미 있습니다. 새 작업 공간으로 덮어쓸까요?"),
                LT("New Workspace", "새 작업 공간"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
    }
}
