using System.Diagnostics;
using System.IO;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    // v3: 작업 공간 파일(.ncvws) 저장·열기·최근 목록, 닫기·열기 전 저장 확인. 형식·경로 규칙은 Workspace/WorkspaceFile.cs.
    // 데이터는 저장하지 않는다: 원본 경로·옵션, 뷰 정의, 열린 탭 순서·활성 탭, 탐색기 표시 여부만. 탭별 필터·정렬·서식은 경로로 저장된 보기 저장소가 이미 가지고 있다.
    public partial class Form1
    {
        private string? _wfFilePath;
        private string _wfSavedSignature = "";
        /// <summary>작업 공간 파일에 있었지만 이번에 만들지 못한 뷰 정의(원본 누락·SQL 오류). 다시 저장할 때 잃지 않도록 그대로 보관한다.</summary>
        private readonly List<WorkspaceFileView> _wfCarriedViews = new();
        private bool _wfBusy;
        private bool _wfCloseDecided;
        private ToolStripMenuItem? _wfNewMenu, _wfOpenMenu, _wfSaveMenu, _wfSaveAsMenu, _wfCloseMenu, _wfRecentMenu;

        /// <summary>저장 확인(예/아니오/취소) 대화 상자를 대신하는 이음매(테스트용). null이면 실제 MessageBox.</summary>
        internal Func<string, DialogResult>? WorkspaceSavePrompt;

        /// <summary>지금 작업 공간 파일(.ncvws)의 전체 경로. 저장하거나 연 적이 없으면 null(제목 없는 작업 공간).</summary>
        internal string? WorkspaceFilePath => _wfFilePath;

        /// <summary>작업 공간을 열거나 저장하는 중인가.</summary>
        internal bool IsWorkspaceFileBusy => _wfBusy;

        // Form1.AgentUi.BuildAgentFeatures에서 호출. 항목만 만든다(파일 메뉴 조립은 Ui/Form1.MainMenu.cs).
        private void BuildWorkspaceFileFeatures()
        {
            _wfNewMenu = MakeCmd("file.newWorkspace", async (_, _) => await NewWorkspaceAsync());
            _wfOpenMenu = MakeCmd("file.openWorkspace", async (_, _) => await OpenWorkspaceDialogAsync());
            _wfSaveMenu = MakeCmd("file.saveWorkspace", (_, _) => SaveWorkspace(saveAs: false)); // 작업 공간 파일이 없으면 다른 이름으로 저장 대화상자
            _wfSaveAsMenu = MakeItem("Save Workspace As…", "작업 공간을 다른 이름으로 저장…", (_, _) => SaveWorkspace(saveAs: true));
            _wfCloseMenu = MakeCmd("file.closeWorkspace", (_, _) => CloseWorkspace());
            _wfRecentMenu = MakeItem("Recent Workspaces", "최근 작업 공간", (_, _) => { });
            _wfRecentMenu.DropDownItems.Add(new ToolStripMenuItem { Enabled = false });
            _wfRecentMenu.DropDownOpening += (_, _) => FillRecentWorkspaceMenu();

            // 열기 대화 상자에서 .ncvws도 고를 수 있게(고르면 OpenFileCoreAsync가 작업 공간으로 연다).
            openFileDialog1.Filter = openFileDialog1.Filter
                .Replace("*.sqlite3|CSV", "*.sqlite3;*" + WorkspaceFile.Extension + "|CSV")
                + "|" + LT("Workspace (*.ncvws)|*.ncvws", "작업 공간 (*.ncvws)|*.ncvws");

            FormClosing += OnWorkspaceFormClosing;
        }

        private void FillRecentWorkspaceMenu()
        {
            if (_wfRecentMenu is null) return;
            _wfRecentMenu.DropDownItems.Clear();
            var recent = _settings.RecentWorkspaces ?? new List<string>();
            if (recent.Count == 0)
            {
                _wfRecentMenu.DropDownItems.Add(new ToolStripMenuItem(LT("(none)", "(없음)")) { Enabled = false });
                return;
            }
            foreach (string path in recent.ToArray())
            {
                var item = new ToolStripMenuItem(path) { ToolTipText = path };
                item.Click += async (_, _) => await OpenWorkspaceFileAsync(path);
                _wfRecentMenu.DropDownItems.Add(item);
            }
        }

        // ---- 닫기 전 확인 ------------------------------------------------------------------------------------

        private void OnWorkspaceFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (e.Cancel || _wfCloseDecided) return;
            // 종료가 이미 정해진 경우(Windows 종료·작업 관리자·다른 프로세스의 WM_CLOSE)에는 묻지 않는다. 두 번째 닫기 시도(비동기 정리 뒤)에도 다시 묻지 않도록 결정을 기억한다.
            // 패널 배치만 파일에 조용히 반영한다(파일의 다른 내용은 건드리지 않는다).
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) { SaveWorkspaceLayoutOnClose(); _wfCloseDecided = true; return; }
            bool hadChanges = WorkspaceNeedsSave();
            if (!ConfirmWorkspaceSaved()) { e.Cancel = true; return; }
            if (!hadChanges) SaveWorkspaceLayoutOnClose();   // 내용 변경이 없었으면 패널 배치만 파일에 반영(저장 확인은 띄우지 않는다)
            _wfCloseDecided = true;
        }

        /// <summary>
        /// 저장하지 않은 작업 공간 변경이 있으면 저장할지 묻는다(예 = 저장, 아니오 = 버림, 취소 = false). 변경이 없으면 true.
        /// 변경 = 작업 공간 파일이 있으면 저장본과 다름, 없으면(제목 없음) 뷰 정의가 있음(뷰는 작업 공간 파일에만 남는다).
        /// </summary>
        internal bool ConfirmWorkspaceSaved()
        {
            if (!WorkspaceNeedsSave()) return true;
            string text = _wfFilePath is null
                ? LT("This workspace has views or notes that exist only in this session. Save it as a workspace file?",
                     "이 작업 공간에는 이번 실행에만 있는 뷰나 메모가 있습니다. 작업 공간 파일로 저장할까요?")
                : LT($"Save changes to the workspace '{Path.GetFileName(_wfFilePath)}'?", $"작업 공간 '{Path.GetFileName(_wfFilePath)}'의 변경 사항을 저장할까요?");
            var answer = WorkspaceSavePrompt?.Invoke(text) ?? MessageBox.Show(this, text, LT("Workspace", "작업 공간"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            return answer != DialogResult.Yes || SaveWorkspace(saveAs: false);
        }

        private bool WorkspaceNeedsSave()
        {
            if (_wfFilePath is not null)
                return WorkspaceFile.Signature(CaptureWorkspaceModel(_wfFilePath)) != _wfSavedSignature;
            return ExistingWorkspace is { } ws && ws.Views.Count > 0 || !string.IsNullOrWhiteSpace(_wfAgent.Notes);
        }

        /// <summary>파일 ▸ 작업 공간 닫기를 쓸 수 있는가: 작업 공간 파일이 열려 있거나 작업 공간에 원본·뷰가 있다.</summary>
        internal bool CanCloseWorkspace() =>
            !_wfBusy && (_wfFilePath is not null || ExistingWorkspace is { } ws && (ws.Sources.Count > 0 || ws.Views.Count > 0));

        private void UpdateCloseWorkspaceMenu()
        {
            if (_wfCloseMenu is not null) _wfCloseMenu.Enabled = CanCloseWorkspace();
        }

        /// <summary>
        /// 작업 공간을 닫는다: 저장 확인(취소하면 아무것도 바꾸지 않음) → 파일이 있으면 패널 배치만 조용히 반영(앱 종료와 같게) → 모든 탭 닫기(저장 안 한 편집은 묻는다) →
        /// 원본·뷰 지우기 → 작업 공간 파일·제목·에이전트 상태 비우기 → 앱 시작 레이아웃 적용 → 최근 목록 갱신. 닫았으면 true.
        /// </summary>
        internal bool CloseWorkspace()
        {
            if (_wfBusy || _closing || IsDisposed || !CanCloseWorkspace()) return false;
            bool hadChanges = WorkspaceNeedsSave();
            if (!ConfirmWorkspaceSaved()) return false;
            if (!hadChanges) SaveWorkspaceLayoutOnClose();
            string? closed = _wfFilePath;
            if (!CloseAllTabs(askUnsaved: true)) return false;

            var problems = ResetWorkspaceSession(keepTitle: false);
            SwitchAgentToOpenedWorkspace();   // 파일 없는 작업 공간으로: 대화는 아직 시작 전이면 그대로 미루고, 떠 있으면 새 대화로
            if (closed is not null) RememberRecentWorkspace(closed);
            statusLabel.Text = closed is null
                ? LT("Workspace closed.", "작업 공간을 닫았습니다.")
                : LT("Workspace closed: ", "작업 공간을 닫았습니다: ") + closed;
            if (problems.Count > 0) statusLabel.Text += LT($" ({problems.Count} item(s) could not be removed)", $" (지우지 못한 항목 {problems.Count}개)");
            PostAgentContext();
            UpdateCloseWorkspaceMenu();
            return true;
        }

        // ---- 저장 ----------------------------------------------------------------------------------------

        /// <summary>현재 상태에서 작업 공간 파일 모델을 만든다(작업 공간이 없으면 탭만). 엔진은 만들지 않는다.</summary>
        private WorkspaceFileModel CaptureWorkspaceModel(string workspacePath)
        {
            var sources = new List<WorkspaceCaptureSource>();
            var views = new List<WorkspaceFileView>();
            if (ExistingWorkspace is { } ws)
            {
                foreach (var s in ws.Sources)
                {
                    if (string.IsNullOrWhiteSpace(s.Path)) continue;   // 경로를 모르는 원본(복원할 수 없음)
                    if (s.Kind == WorkspaceSourceKind.Csv && s.Tables.Count > 0)
                    {
                        var o = s.Tables[0].Options;
                        sources.Add(new WorkspaceCaptureSource(WorkspaceFileSource.KindCsv, s.Name, s.Path, o.EncodingName, o.Delimiter, o.HasHeader, null));
                    }
                    else if (s.Kind == WorkspaceSourceKind.Database)
                    {
                        sources.Add(new WorkspaceCaptureSource(WorkspaceFileSource.KindDatabase, s.Name, s.Path, null, null, true,
                            s.Tables.Select(t => t.Name).ToList()));
                    }
                }
                foreach (var v in ws.Views) views.Add(WorkspaceFileView.From(v.Name, v.Sql, v.IncludeUnsavedEdits, v.Provenance));
            }

            var tabs = new List<WorkspaceCaptureTab>();
            int active = -1;
            foreach (var t in _tabs)
            {
                WorkspaceCaptureTab? c = t.Kind switch
                {
                    TabKind.File => new WorkspaceCaptureTab(WorkspaceFileTab.KindFile, t.Path, null, null),
                    TabKind.Sheet => new WorkspaceCaptureTab(WorkspaceFileTab.KindSheet, t.Path, t.SheetName, null),
                    TabKind.View when !string.IsNullOrEmpty(t.ViewName) => new WorkspaceCaptureTab(WorkspaceFileTab.KindView, null, null, t.ViewName),
                    _ => null,   // 질의 결과는 임시 파일이라 저장하지 않는다
                };
                if (c is null) continue;
                if (ReferenceEquals(t, _t)) active = tabs.Count;
                tabs.Add(c);
            }
            return WorkspaceFile.Capture(workspacePath, sources, views, _wfCarriedViews, tabs, active, WorkspaceDockVisible, CaptureAgentInfo(),
                LayoutReady ? CaptureLayout() : null);
        }

        private string SuggestWorkspaceFolder()
        {
            string? dir = _tabs.Where(t => t.Kind is TabKind.File or TabKind.Sheet).Select(t => Path.GetDirectoryName(t.Path)).FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));
            return dir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private string SuggestWorkspaceName()
        {
            string? first = _tabs.Where(t => t.Kind is TabKind.File or TabKind.Sheet).Select(t => Path.GetFileNameWithoutExtension(t.Path)).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            return (first ?? LT("workspace", "작업 공간")) + WorkspaceFile.Extension;
        }

        /// <summary>작업 공간 파일로 저장한다. 파일이 아직 없거나 saveAs이면 이름을 묻는다. 저장하면 true(취소·실패는 false).</summary>
        internal bool SaveWorkspace(bool saveAs)
        {
            if (_wfBusy) return false;
            string? target = _wfFilePath;
            if (saveAs || target is null)
            {
                using var dlg = new SaveFileDialog
                {
                    Title = LT("Save Workspace", "작업 공간 저장"),
                    Filter = LT("Workspace (*.ncvws)|*.ncvws|All files (*.*)|*.*", "작업 공간 (*.ncvws)|*.ncvws|모든 파일 (*.*)|*.*"),
                    DefaultExt = "ncvws",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = target is null ? SuggestWorkspaceName() : Path.GetFileName(target),
                    InitialDirectory = target is null ? SuggestWorkspaceFolder() : Path.GetDirectoryName(target) ?? SuggestWorkspaceFolder(),
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                target = Path.GetFullPath(dlg.FileName);
            }

            return WriteWorkspaceFile(target, freshConversation: false);
        }

        /// <summary>
        /// 지금 상태를 target에 쓰고 그 파일을 현재 작업 공간 파일로 삼는다(원본 경로 보호 포함, 실패하면 안내 후 false). freshConversation이면 지금 떠 있는 대화를
        /// 파일에 연결하지 않는다(새 작업 공간은 새 대화로 시작).
        /// </summary>
        private bool WriteWorkspaceFile(string target, bool freshConversation)
        {
            // 원본 파일은 어떤 경우에도 쓰지 않는다: 작업 공간 파일이 열려 있거나 등록된 원본과 같은 경로면 거절.
            if (IsWorkspaceSourcePath(target))
            {
                MessageBox.Show(this, LT("That name is used by a file that is open in the workspace; choose another name. Source files are never overwritten.",
                    "작업 공간에 열려 있는 파일과 같은 이름입니다. 다른 이름을 고르세요. 원본 파일은 덮어쓰지 않습니다."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            WorkspaceFileModel model;
            try
            {
                model = CaptureWorkspaceModel(target);
                if (freshConversation && model.Agent is not null) model.Agent.Session = null;
                WorkspaceFile.Save(target, model);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                MessageBox.Show(this, LT("Could not save the workspace: ", "작업 공간을 저장하지 못했습니다: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            _wfFilePath = target;
            _wfAgent = model.Agent?.Clone() ?? new WorkspaceFileAgent();   // 저장된 대화 연결이 이제 "읽어 둔" 연결이다
            _wfSavedSignature = WorkspaceFile.Signature(model);
            if (model.Layout is not null) _wfAppliedLayout = model.Layout.Clone();
            RememberRecentWorkspace(target);
            statusLabel.Text = LT("Workspace saved: ", "작업 공간을 저장했습니다: ") + target;
            PostAgentContext();   // 작업 공간 파일이 생기면 에이전트의 분석 폴더가 그 옆으로 바뀐다
            return true;
        }

        /// <summary>
        /// 탭이 모두 닫힌 뒤(또는 새 작업 공간처럼 탭을 남기는 경우) 작업 공간 상태를 비운다: 파일 연결·에이전트 제한/메모/대화 연결·보관 중인 뷰·저장 서명 버리기 →
        /// 원본·뷰 지우기 → 제목(keepTitle이 아니면)·앱 시작 레이아웃. 지우지 못한 항목 목록을 돌려준다.
        /// </summary>
        private List<string> ResetWorkspaceSession(bool keepTitle)
        {
            var problems = new List<string>();
            _wfFilePath = null;
            _wfAgent = new WorkspaceFileAgent();   // 닫은 작업 공간의 제한·메모·대화 연결을 버린다
            _wfCarriedViews.Clear();
            _wfSavedSignature = "";
            _wfAppliedLayout = null;
            ClearWorkspaceState(problems);
            if (!keepTitle) Text = ProgramName;
            if (LayoutReady) ApplyLayout(StartupLayout());
            return problems;
        }

        private bool IsWorkspaceSourcePath(string full)
        {
            if (IsOpenInAnyTab(full)) return true;
            if (ExistingWorkspace is { } ws)
                foreach (var s in ws.Sources)
                    if (SamePath(s.Path, full) || s.Tables.Any(t => SamePath(t.FilePath, full))) return true;
            return false;
        }

        private void RememberRecentWorkspace(string path)
        {
            _settings.AddRecentWorkspace(path);
            _settings.Save();
        }

        // ---- 열기 ----------------------------------------------------------------------------------------

        private async Task OpenWorkspaceDialogAsync()
        {
            using var dlg = new OpenFileDialog
            {
                Title = LT("Open Workspace", "작업 공간 열기"),
                Filter = LT("Workspace (*.ncvws)|*.ncvws|All files (*.*)|*.*", "작업 공간 (*.ncvws)|*.ncvws|모든 파일 (*.*)|*.*"),
                CheckFileExists = true,
            };
            string? recentDir = _settings.RecentWorkspaces?.Select(Path.GetDirectoryName).FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));
            if (recentDir is not null) dlg.InitialDirectory = recentDir;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            await OpenWorkspaceFileAsync(dlg.FileName);
        }

        /// <summary>
        /// 작업 공간 파일을 연다: 저장 확인 → 파일 읽기(형식·버전 검사) → 누락 파일 찾기/건너뛰기 → 현재 탭·원본·뷰 정리 → 원본 등록 → 탭 열기(저장된 순서) →
        /// 뷰 만들기(결과는 탭을 열 때 계산) → 활성 탭·탐색기 복원. 취소하면 그 시점까지 연 것만 남고 작업 공간 파일은 연결되지 않는다. 열었으면 true.
        /// </summary>
        internal async Task<bool> OpenWorkspaceFileAsync(string path)
        {
            if (_wfBusy || _closing || IsDisposed) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (ArgumentException) { return false; }
            if (!File.Exists(full))
            {
                _settings.RemoveRecentWorkspace(full);
                _settings.Save();
                MessageBox.Show(this, LT("File not found: ", "파일을 찾을 수 없습니다: ") + full, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            WorkspaceFileModel model;
            try { model = WorkspaceFile.Load(full); }
            catch (WorkspaceFileException ex)
            {
                MessageBox.Show(this, ex.Message + "\n\n" + full, LT("Open Workspace", "작업 공간 열기"), MessageBoxButtons.OK,
                    ex.Error == WorkspaceFileError.TooNew ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, LT("Could not read the workspace file: ", "작업 공간 파일을 읽지 못했습니다: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!ConfirmWorkspaceSaved()) return false;
            string dir = Path.GetDirectoryName(full) ?? "";

            // 원본 파일 찾기: 상대 경로 → 절대 경로. 없으면 사용자가 찾거나 건너뛴다.
            var resolved = new string?[model.Sources.Count];
            var missing = new List<WorkspaceMissingDialog.Item>();
            var missingIndex = new List<int>();
            for (int i = 0; i < model.Sources.Count; i++)
            {
                var s = model.Sources[i];
                resolved[i] = WorkspaceFile.Resolve(s, dir);
                if (resolved[i] is not null) continue;
                string last = WorkspaceFile.Candidates(s, dir).LastOrDefault() ?? s.AbsolutePath ?? s.Path ?? "";
                missing.Add(new WorkspaceMissingDialog.Item(string.IsNullOrWhiteSpace(s.Name) ? Path.GetFileName(last) : s.Name, last, Path.GetExtension(last)));
                missingIndex.Add(i);
            }
            if (missing.Count > 0)
            {
                using var dlg = new WorkspaceMissingDialog(_palette, Path.GetFileNameWithoutExtension(full), missing, dir);
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                for (int k = 0; k < missing.Count; k++)
                    if (missing[k].Located is { } located) resolved[missingIndex[k]] = Path.GetFullPath(located);
            }

            if (!CloseAllTabs(askUnsaved: true)) return false;

            _wfBusy = true;
            Enabled = false;
            using var progress = new WorkspaceProgressForm(_palette, LT("Opening workspace", "작업 공간 여는 중"));
            progress.Show(this);
            var problems = new List<string>();
            bool cancelled = false;
            try
            {
                _wfFilePath = null;
                _wfAgent = new WorkspaceFileAgent();   // 이전 작업 공간의 제한·메모를 버린다
                _wfCarriedViews.Clear();
                progress.Report(LT("Clearing the current workspace…", "현재 작업 공간을 정리하는 중…"));
                ClearWorkspaceState(problems);
                await RestoreWorkspaceAsync(model, resolved, dir, progress, problems, progress.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                _wfBusy = false;
                Enabled = true;
            }
            progress.Close();
            Activate();

            if (cancelled)
            {
                statusLabel.Text = LT("Opening the workspace was cancelled; what was already opened stays open.", "작업 공간 열기를 취소했습니다. 이미 연 것은 그대로 남습니다.");
                if (_agentController is not null) _agentController.Options = AgentOptions();
                PostAgentContext();
                return false;
            }

            _wfFilePath = full;
            _wfAgent = model.Agent?.Clone() ?? new WorkspaceFileAgent();
            SwitchAgentToOpenedWorkspace();   // 이 작업 공간의 설정·메모·대화로(이어 가거나 새 대화)
            _wfSavedSignature = WorkspaceFile.Signature(CaptureWorkspaceModel(full));
            RememberRecentWorkspace(full);
            statusLabel.Text = LT("Workspace opened: ", "작업 공간을 열었습니다: ") + full;
            PostAgentContext();
            if (problems.Count > 0) ShowWorkspaceProblems(Path.GetFileName(full), problems);
            return true;
        }

        /// <summary>현재 작업 공간의 뷰·원본을 모두 지운다(원본 파일은 건드리지 않는다). 지우지 못한 것은 problems에 남긴다.</summary>
        private void ClearWorkspaceState(List<string> problems)
        {
            if (ExistingWorkspace is not { } ws) return;
            foreach (var v in ws.Views.Reverse().ToArray())
            {
                try { ws.RemoveView(v, cascade: true); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or WorkspaceQueryException) { problems.Add(v.Name + ": " + ex.Message); }
            }
            foreach (var s in ws.Sources.ToArray())
            {
                try { ws.Remove(s, cascade: true); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or WorkspaceQueryException) { problems.Add(s.Name + ": " + ex.Message); }
            }
        }

        private async Task RestoreWorkspaceAsync(WorkspaceFileModel model, string?[] resolved, string dir, WorkspaceProgressForm progress,
            List<string> problems, CancellationToken ct)
        {
            // 엔진이 필요한가(원본 또는 뷰가 있다). 쓸 수 없으면 탭만 열고 이유를 알린다.
            DataWorkspace? ws = null;
            if (resolved.Any(p => p is not null) || model.Views.Count > 0)
            {
                ws = Workspace;
                if (ws is null)
                    problems.Add(LT("The SQL engine is not available" + (string.IsNullOrEmpty(WorkspaceUnavailableReason) ? "" : ": " + WorkspaceUnavailableReason) + ". Files were opened as tabs; views were kept in the workspace file but not created.",
                        "SQL 엔진을 쓸 수 없습니다" + (string.IsNullOrEmpty(WorkspaceUnavailableReason) ? "" : ": " + WorkspaceUnavailableReason) + ". 파일은 탭으로만 열었고 뷰는 작업 공간 파일에 남겨 두었지만 만들지 않았습니다."));
            }

            // 1) CSV 원본: 저장된 인코딩·구분자로 직접 등록한다(탭을 여는 쪽의 자동 감지보다 저장된 선택이 우선).
            if (ws is not null)
            {
                for (int i = 0; i < model.Sources.Count; i++)
                {
                    var s = model.Sources[i];
                    if (resolved[i] is not { } p || s.Kind != WorkspaceFileSource.KindCsv) continue;
                    ct.ThrowIfCancellationRequested();
                    string label = Path.GetFileName(p);
                    progress.Report(LT("Reading ", "읽는 중: ") + label);
                    try
                    {
                        var options = new CsvSourceOptions
                        {
                            EncodingName = string.IsNullOrWhiteSpace(s.Encoding) ? null : s.Encoding,
                            Delimiter = string.IsNullOrEmpty(s.Delimiter) ? null : s.Delimiter[0],
                            HasHeader = s.HasHeader,
                        };
                        var source = await ws.AddCsvAsync(p, options, new Progress<int>(pct => progress.Report(LT("Reading ", "읽는 중: ") + label + $"  {pct}%")), ct);
                        TryRestoreName(ws, source, s.Name, problems);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { problems.Add($"{label}: {ex.Message}"); }
                }
            }

            // 2) 파일·시트 탭(저장된 순서). 탭을 열면 작업 공간에 등록된다(같은 경로면 위에서 만든 원본을 그대로 쓴다). 워크북 원본은 여기서 DB 원본이 된다.
            var opened = new DocumentTab?[model.Tabs.Count];
            for (int k = 0; k < model.Tabs.Count; k++)
            {
                var t = model.Tabs[k];
                if (t.Kind == WorkspaceFileTab.KindView || t.Source is not { } si || resolved[si] is not { } p) continue;
                ct.ThrowIfCancellationRequested();
                progress.Report(LT("Opening ", "여는 중: ") + Path.GetFileName(p));
                var tab = await OpenFileTabAsync(p);
                if (tab is null) { problems.Add(LT("Could not open ", "열지 못했습니다: ") + p); continue; }
                opened[k] = tab;
                if (ws is not null)
                {
                    try { await RegisterTabAsync(tab, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { problems.Add(Path.GetFileName(p) + ": " + ex.Message); }
                }
                if (t.Kind == WorkspaceFileTab.KindSheet && !string.IsNullOrEmpty(t.Sheet))
                    await SelectSheetAsync(tab, t.Sheet, problems, ct);
            }

            // 3) 탭이 없는 DB 원본(엑셀·SAS·SPSS·SQLite)과 이름 되돌리기, 시트 목록 달라짐 알림.
            if (ws is not null)
            {
                for (int i = 0; i < model.Sources.Count; i++)
                {
                    var s = model.Sources[i];
                    if (resolved[i] is not { } p || s.Kind != WorkspaceFileSource.KindDatabase) continue;
                    ct.ThrowIfCancellationRequested();
                    var live = ws.Sources.FirstOrDefault(x => x.Kind == WorkspaceSourceKind.Database && SamePath(x.Path, p));
                    if (live is null)
                    {
                        progress.Report(LT("Reading ", "읽는 중: ") + Path.GetFileName(p));
                        try { live = (await AddSourcesAsync(new[] { p }, ct)).FirstOrDefault(x => x.Kind == WorkspaceSourceKind.Database); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { problems.Add(Path.GetFileName(p) + ": " + ex.Message); }
                    }
                    if (live is null) continue;
                    TryRestoreName(ws, live, s.Name, problems);
                    if (s.Tables is { Count: > 0 })
                    {
                        var gone = s.Tables.Where(n => !live.Tables.Any(t => string.Equals(t.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (gone.Count > 0)
                            problems.Add(LT($"{Path.GetFileName(p)}: sheet(s) no longer in the file: {string.Join(", ", gone)} (views using them will not work).",
                                $"{Path.GetFileName(p)}: 파일에서 사라진 시트: {string.Join(", ", gone)} (이를 쓰는 뷰는 동작하지 않습니다)."));
                    }
                }
            }

            // 4) 뷰: 정의만 만든다 — 결과는 탭을 열 때 계산하므로 그때까지 "오래된" 상태. 만들지 못한 뷰는 그대로 보관해 다시 저장할 때 잃지 않는다.
            foreach (var v in model.Views)
            {
                if (ws is null) { _wfCarriedViews.Add(v); continue; }
                try { ws.CreateView(v.Name, v.Sql, v.IncludeUnsavedEdits, v.ToProvenance()); }
                catch (Exception ex) when (ex is WorkspaceQueryException or ArgumentException or InvalidOperationException)
                {
                    problems.Add(LT($"View '{v.Name}' could not be restored (kept in the workspace file): ", $"뷰 '{v.Name}'을(를) 복원하지 못했습니다(작업 공간 파일에는 남겨 둡니다): ") + ex.Message);
                    _wfCarriedViews.Add(v);
                }
            }

            // 5) 뷰 탭: 결과가 없으면 여기서 계산한다(취소할 수 있다).
            for (int k = 0; k < model.Tabs.Count; k++)
            {
                var t = model.Tabs[k];
                if (t.Kind != WorkspaceFileTab.KindView || ws is null) continue;
                var view = ws.Views.FirstOrDefault(v => string.Equals(v.Name, t.View, StringComparison.OrdinalIgnoreCase));
                if (view is null) continue;
                ct.ThrowIfCancellationRequested();
                progress.Report(LT("Computing view ", "뷰 계산 중: ") + view.Name);
                try { opened[k] = await OpenRelationTabAsync(view, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { problems.Add(LT($"View '{view.Name}': ", $"뷰 '{view.Name}': ") + ex.Message); }
            }

            // 6) 탭 순서·활성 탭·탐색기.
            var order = new List<DocumentTab>();
            DocumentTab? active = null;
            for (int k = 0; k < opened.Length; k++)
            {
                if (opened[k] is not { IsClosed: false } tab || order.Contains(tab)) continue;
                order.Add(tab);
                if (k == model.ActiveTab) active = tab;
            }
            for (int k = 0; k < order.Count; k++) MoveTab(order[k], k);
            if (active is not null) await ActivateTabAsync(active);
            else if (order.Count > 0 && ActiveTab is null) await ActivateTabAsync(order[0]);
            ApplyWorkspaceLayout(model);   // 저장된 패널 배치(없는 옛 파일은 앱 기본)
        }

        private void TryRestoreName(DataWorkspace ws, WorkspaceSource source, string savedName, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(savedName) || string.Equals(source.Name, savedName, StringComparison.Ordinal)) return;
            try { ws.Rename(source, savedName); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or WorkspaceQueryException)
            {
                problems.Add(LT($"'{source.Name}' could not be renamed back to '{savedName}' (views using that name may not work): ",
                    $"'{source.Name}'의 이름을 '{savedName}'(으)로 되돌리지 못했습니다(그 이름을 쓰는 뷰는 동작하지 않을 수 있음): ") + ex.Message);
            }
        }

        /// <summary>워크북 탭을 저장된 시트로 바꾼다(탭을 활성화하고 시트 전환이 끝나기를 기다린다).</summary>
        private async Task SelectSheetAsync(DocumentTab tab, string sheet, List<string> problems, CancellationToken ct)
        {
            var wb = tab.Workbook;
            if (wb is null) return;
            int idx = -1;
            for (int i = 0; i < wb.SheetNames.Count; i++)
                if (string.Equals(wb.SheetNames[i], sheet, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0)
            {
                problems.Add(LT($"{tab.Title}: sheet '{sheet}' no longer exists.", $"{tab.Title}: 시트 '{sheet}'이(가) 더는 없습니다."));
                return;
            }
            await ActivateTabAsync(tab);
            if (_currentSheetIndex == idx) return;
            if (!await WaitForAsync(() => !_busy, 30000, ct)) return;
            SwitchSheet(idx);
            if (!await WaitForAsync(() => !_busy && _currentSheetIndex == idx, 30000, ct))
                problems.Add(LT($"{tab.Title}: could not switch to sheet '{sheet}'.", $"{tab.Title}: 시트 '{sheet}'(으)로 바꾸지 못했습니다."));
        }

        private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                await Task.Delay(30, ct);
            }
            return true;
        }

        private void ShowWorkspaceProblems(string name, List<string> problems)
        {
            const int max = 15;
            string list = string.Join("\n", problems.Take(max).Select(p => "• " + p));
            if (problems.Count > max) list += "\n" + LT($"… and {problems.Count - max} more.", $"… 외 {problems.Count - max}건.");
            MessageBox.Show(this, LT($"The workspace '{name}' was opened, with problems:\n\n", $"작업 공간 '{name}'을(를) 열었지만 문제가 있었습니다:\n\n") + list,
                LT("Open Workspace", "작업 공간 열기"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
