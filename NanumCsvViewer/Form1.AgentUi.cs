using System.IO;
using System.Text.Json;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Tools;

namespace NanumCsvViewer
{
    // v2: 오른쪽에 도킹되는 AI 에이전트 채팅 패널(omp RPC 호스트). 설계: docs/AGENT_INTEGRATION_PLAN.md
    public partial class Form1
    {
        private SplitContainer? _agentSplit;
        private AgentChatPanel? _agentPanel;
        private ChatController? _agentController;
        private ToolStripButton? _agentButton;
        private ToolStripMenuItem? _agentPanelMenu;
        private bool _syncingAgentToggle;

        // Form1.Features.BuildFeatureMenus에서 호출.
        private void BuildAgentFeatures()
        {
            // 기존 본문(outerSplit)을 왼쪽에, 채팅 패널을 오른쪽에 두는 분할. 패널은 처음 열 때 만든다.
            _agentSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel2,
                Panel2Collapsed = true,
            };
            int index = Controls.GetChildIndex(outerSplit);
            Controls.Remove(outerSplit);
            _agentSplit.Panel1.Controls.Add(outerSplit);
            Controls.Add(_agentSplit);
            Controls.SetChildIndex(_agentSplit, index);

            _agentPanelMenu = MakeCmd("view.ai", (_, _) => SetAgentPanelVisible(!AgentPanelVisible));
            BuildWorkspaceFileFeatures();   // File ▸ 작업 공간 열기·저장·최근 목록 (BuildFeatureMenus가 에이전트와 함께 한 번 호출한다)
            LocalizeAgentUi();
        }

        private bool AgentPanelVisible => _agentSplit is { Panel2Collapsed: false };

        private void LocalizeAgentUi()
        {
            string lang = Loc.CurrentLanguage == "ko" ? "ko" : "en";
            _agentPanel?.SetLanguage(lang);
            if (_agentController is not null) _agentController.Options = AgentOptions();
        }

        /// <summary>
        /// 에이전트가 실제로 따르는 옵션 = 앱 설정에 작업 공간 파일의 설정을 합친 것. 승인 모드·데이터 정책·로컬 Python은 둘 중 <b>더 엄격한 쪽</b>이다
        /// (받은 작업 공간 파일이 앱 설정을 풀 수 없다). omp 추가 인자의 승인 모드 고정은 여전히 이긴다.
        /// </summary>
        private AgentHostOptions AgentOptions() => WorkspaceAgentPolicy.Apply(AppAgentOptions(), _wfAgent);

        private void SetAgentPanelVisible(bool visible) => SetAgentPanelVisible(visible, focus: true);

        /// <summary>AI 패널을 보이거나 숨긴다. focus=false면 채팅 입력창으로 포커스를 옮기지 않는다(시작·작업 공간 레이아웃 복원).</summary>
        private void SetAgentPanelVisible(bool visible, bool focus)
        {
            if (_agentSplit is null) return;
            _syncingAgentToggle = true;
            try
            {
                if (_agentButton is not null) _agentButton.Checked = visible;
                if (_agentPanelMenu is not null) _agentPanelMenu.Checked = visible;
            }
            finally { _syncingAgentToggle = false; }

            if (!visible)
            {
                _agentPrewarmTimer?.Stop();
                _agentSplit.Panel2Collapsed = true;
                RaisePanelChanged(PanelKind.Agent);
                return;
            }
            EnsureAgentPanel();
            _placingAgentSplitter = true;
            try { _agentSplit.Panel2Collapsed = false; }
            finally { _placingAgentSplitter = false; }
            // 최소 크기는 분할이 실제 크기를 가진 뒤에만 지정할 수 있다(생성 시 지정하면 SplitterDistance 범위 오류).
            // 저장 폭은 96 DPI 기준 논리 단위. 고해상도 화면에서도 같은 체감 폭이 되도록 장치 픽셀로 바꾼다.
            int total = _agentSplit.Width;
            int min = LogicalToDeviceUnits(320);
            int width = Math.Clamp(LogicalToDeviceUnits(AgentWidthLogical), min, Math.Max(min, total - LogicalToDeviceUnits(300)));
            int distance = total - width - _agentSplit.SplitterWidth;
            if (distance > _agentSplit.Panel1MinSize)
            {
                _placingAgentSplitter = true;
                try { _agentSplit.SplitterDistance = distance; }
                finally { _placingAgentSplitter = false; }
                if (_agentSplit.Panel2MinSize < min && total - _agentSplit.SplitterDistance - _agentSplit.SplitterWidth >= min)
                    _agentSplit.Panel2MinSize = min;
            }
            if (focus) _agentPanel!.FocusInput();
            RaisePanelChanged(PanelKind.Agent);
            KickAgentPrewarm();
        }

        /// <summary>AI 패널 폭(논리 단위). 작업 공간 레이아웃이 정한 값이 있으면 그것, 아니면 앱 설정.</summary>
        private int AgentWidthLogical => _agentWidthOverride > 0 ? _agentWidthOverride : _settings.AgentPanelWidth;
        private int _agentWidthOverride;

        /// <summary>
        /// 질문을 AI 채팅에 보낸다(컨텍스트 메뉴 "AI에게 묻기"): 패널을 열고(포커스는 입력창으로) 메시지를 보낸다.
        /// omp가 아직 시작 전이면 이 메시지로 시작한다. 이미 작업 중이면 채팅 규칙대로 대기열에 들어간다.
        /// </summary>
        internal void AskAgent(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt) || _agentSplit is null) return;
            SetAgentPanelVisible(true);
            _agentController?.Submit(prompt);
        }

        /// <summary>폼에 직접 붙은 본문(Fill) 컨트롤. 에이전트 분할이 있으면 그것, 없으면 outerSplit. 띠·패널의 z-순서 기준.</summary>
        private Control MainContent => _agentSplit ?? (Control)outerSplit;
        private bool _placingAgentSplitter;

        private void EnsureAgentPanel()
        {
            if (_agentPanel is not null) return;
            _agentPanel = new AgentChatPanel { Dock = DockStyle.Fill };
            _agentSplit!.Panel2.Controls.Add(_agentPanel);
            _agentSplit.SplitterMoved += (_, _) =>
            {
                if (!AgentPanelVisible || _placingAgentSplitter) return;
                int logical = (int)Math.Round(_agentSplit.Panel2.Width * 96.0 / DeviceDpi);
                if (logical < 320) return; // 접힘·배치 중간값은 저장하지 않는다
                _settings.AgentPanelWidth = logical;
                _agentWidthOverride = 0;   // 사용자가 직접 끈 폭이 이제 기준이다
                _settings.Save();
            };
            _agentPanel.ApplyTheme(_theme == AppTheme.Dark, Font);
            _agentPanel.SetLanguage(Loc.CurrentLanguage == "ko" ? "ko" : "en");

            var options = AgentOptions();
            _agentController = new ChatController(_agentPanel, new CsvHostTools(this, AgentOptions), options);
            _agentController.PageMessageUnhandled += OnAgentPageMessage;
            _agentController.StatusChanged += _ => { };
            // 채팅 승인 선택: 작업 공간 파일이 열려 있으면 "이 작업 공간 / 앱 기본값"을 묻고 알맞은 곳에 저장한다.
            _agentController.ApprovalModeApplier = ApplyApprovalFromChat;
            _agentController.ApprovalNoticeShown += () => { _settings.AgentApprovalNoticeShown = true; _settings.Save(); };
            _agentController.PythonEnvNoticeShown += () => { _settings.AgentPythonEnvNoticeShown = true; _settings.Save(); };
            _agentController.SetWorkspaceContext(BuildAgentWorkspaceContext());
            // omp는 바로 시작하지 않는다: 패널이 보이는 채로 앱이 한가해지면 곧(KickAgentPrewarm) 메시지 없이 시작해 모델·생각·승인 선택이 첫 메시지 전에
            // 준비되고, 패널이 숨겨져 있으면 첫 메시지(또는 패널을 열 때)까지 미룬다. 작업 공간 파일이 열려 있으면 그 작업 공간의
            // 대화를 이어 가고(없거나 사라졌으면 새 대화 + 알림), 파일이 없으면 새 대화. 작업 폴더는 시작하는 순간의 열린 파일 기준.
            _agentController.StartOnFirstUse(AgentWorkingDirectory, _wfFilePath is null ? null : WorkspaceConversationOf(_wfAgent));
            PostAgentContext();
        }

        // ---- omp 미리 시작 -----------------------------------------------------------------------------------------

        private const int AgentPrewarmDelayMs = 1500;
        private System.Windows.Forms.Timer? _agentPrewarmTimer;

        /// <summary>
        /// 패널이 보이고 omp가 아직 미뤄져 있으면 짧은 지연 뒤 미리 시작할 차례를 잡는다(이미 잡혀 있으면 그대로). 시작 배치·패널 열기·작업 공간 열기/닫기/전환·
        /// 파일 열기(모두 <see cref="PostAgentContext"/>를 거친다) 뒤에 부른다. 실제 시작은 <see cref="TryPrewarmAgent"/>가 판단한다.
        /// </summary>
        private void KickAgentPrewarm()
        {
            if (IsDisposed || _closing || !AgentPanelVisible || _agentController is not { IsStartDeferred: true }) return;
            _agentPrewarmTimer ??= CreateAgentPrewarmTimer();
            if (!_agentPrewarmTimer.Enabled) _agentPrewarmTimer.Start();
        }

        private System.Windows.Forms.Timer CreateAgentPrewarmTimer()
        {
            var timer = new System.Windows.Forms.Timer { Interval = AgentPrewarmDelayMs };
            timer.Tick += (_, _) => TryPrewarmAgent();
            return timer;
        }

        /// <summary>
        /// 미리 시작 판단(타이머 한 번). 패널이 숨겨졌거나 이미 시작했으면 아무것도 안 한다. 작업 공간 열기·저장·닫기, 파일 열기·전환, 편집·필터 같은 앞 작업이
        /// 도는 중이면 다시 잡는다(백그라운드 인덱싱은 기다리지 않는다). 로컬 Python이 켜져 있는데 아직 작업 공간 파일도 데이터 파일도 없으면 분석 폴더가
        /// 정해지지 않았으므로 지금 시작하면 파일을 여는 순간 한 번 더 다시 시작해야 한다: 그때(PostAgentContext → Kick)까지 미룬다. 시작했으면 true.
        /// </summary>
        internal bool TryPrewarmAgent()
        {
            _agentPrewarmTimer?.Stop();
            if (IsDisposed || _closing || !AgentPanelVisible || _agentController is not { IsStartDeferred: true } controller) return false;
            if (_wfBusy || _openGate.CurrentCount == 0 || ForegroundWorkRunning()) { KickAgentPrewarm(); return false; }
            if (AgentAnalysisFolderUndecided()) return false;
            return controller.StartDeferredNow();
        }

        private bool AgentAnalysisFolderUndecided()
        {
            if (!AgentOptions().AllowLocalPython) return false;
            var context = BuildAgentWorkspaceContext();
            return context.WorkspaceFile is null && context.FirstDataFile is null;
        }

        /// <summary>omp의 작업 폴더: 열린 파일의 폴더, 없으면 문서 폴더.</summary>
        private string AgentWorkingDirectory()
        {
            string? dir = _currentPath is null ? null : Path.GetDirectoryName(_currentPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        /// <summary>에이전트에 알릴 작업 공간 상태: 작업 공간 파일(저장·연 적이 있으면)과 열린 탭 전부(왼쪽→오른쪽)·활성 탭.</summary>
        private AgentWorkspaceContext BuildAgentWorkspaceContext()
        {
            var entries = new List<AgentTableEntry>(_tabs.Count);
            foreach (var tab in _tabs)
            {
                string kind = tab.Kind switch
                {
                    TabKind.Sheet => AgentTableEntry.KindSheet,
                    TabKind.View => AgentTableEntry.KindView,
                    TabKind.Result => AgentTableEntry.KindResult,
                    _ => AgentTableEntry.KindFile,
                };
                bool hasFile = tab.Kind is TabKind.File or TabKind.Sheet;
                entries.Add(new AgentTableEntry(tab.DisplayName, hasFile ? tab.Path : null, kind, ReferenceEquals(tab, _t)));
            }
            return new AgentWorkspaceContext(WorkspaceFilePath, entries, _wfAgent.Notes);
        }

        /// <summary>작업 공간 단위의 고정 분석 폴더(만들지 않음). 로컬 Python이 꺼져 있거나 에이전트가 아직 없으면 null.</summary>
        string? ICsvAgentHost.AnalysisFolder => _agentController?.AnalysisFolder;

        // 작업 공간 상태(작업 공간 파일·열린 탭 목록)를 컨트롤러에 알리고 채팅 입력창 위 파일 칩(활성 탭 이름)을 갱신한다.
        // LoadDocument(시트 전환 포함)·탭 전환·탭 열기/닫기·작업 공간 저장/열기·편집 변경 때 부른다. 컨트롤러는 분석 폴더를 작업 공간 파일(있으면)
        // 또는 세션에서 처음 연 데이터 파일로 정하고, 탭·시트가 바뀌어도 omp를 다시 시작하지 않는다. csv.* 도구는 호스트가 활성 탭을 보므로 현재 탭에 작용한다.
        private void PostAgentContext()
        {
            _agentController?.SetWorkspaceContext(BuildAgentWorkspaceContext());
            KickAgentPrewarm();
            if (_agentPanel is null) return;
            int edits = _doc is null || _doc.Edits.IsEmpty ? 0 : 1;
            string displayName = _t?.DisplayName ?? (_currentPath is null ? "" : Path.GetFileName(_currentPath));
            string path = _t?.Path ?? _currentPath ?? "";
            _agentPanel.Post(JsonSerializer.Serialize(new
            {
                t = "context",
                file = displayName,
                path,
                edits = HasUnsavedEdits ? Math.Max(edits, 1) : 0,
            }));
        }

        private void OnAgentPageMessage(JsonElement msg)
        {
            string t = msg.TryGetProperty("t", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() ?? "" : "";
            switch (t)
            {
                case "settings":
                    ShowAgentSettings();
                    break;
                case "listColumns":
                    var names = _doc is null ? Array.Empty<string>() : AdvHeaders();
                    _agentPanel?.Post(JsonSerializer.Serialize(new { t = "files", items = names }));
                    break;
                case "openFile":
                case "openImage":
                    string path = msg.TryGetProperty("path", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() ?? "" : "";
                    OpenFileFromAgent(path);
                    break;
                // setApproval는 컨트롤러가 처리한다(yolo 확인·재시작). context 등은 이 호스트에서 쓰지 않는다.
            }
        }

        /// <summary>
        /// 채팅에서 연 파일 경로를 실제 파일로. 절대 경로는 그대로, 상대 경로는 분석 결과 폴더(로컬 Python이 켜진 때) → 열린 파일의 폴더 순.
        /// 없으면 null.
        /// </summary>
        private string? ResolveAgentFile(string path)
        {
            if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
            var bases = new List<string>();
            if (_agentController?.OutputFolder is { } output) bases.Add(output);
            bases.Add(AgentWorkingDirectory());
            foreach (string dir in bases)
            {
                string candidate = Path.Combine(dir, path);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private void ShowViewerResult(ViewerShowResult result)
        {
            if (!result.Ok) MessageBox.Show(this, result.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OpenFileFromAgent(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string? full = ResolveAgentFile(path);
            if (full == null)
            {
                MessageBox.Show(this, LT($"File not found: {path}", $"파일을 찾을 수 없습니다: {path}"), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 보고서·그림·pdf는 데이터 파일이 아니라 보기 창으로 연다.
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is ".md" or ".markdown") { ShowViewerResult(MarkdownViewerForm.ShowFile(this, full, _palette)); return; }
            if (AgentWorkspace.IsImageFile(full) || ext == ".pdf") { ShowViewerResult(ImageViewerForm.ShowFile(this, full, _palette)); return; }
            if (string.Equals(Path.GetFullPath(full), _currentPath is null ? null : Path.GetFullPath(_currentPath), StringComparison.OrdinalIgnoreCase))
                return;
            _ = OpenFileAsync(full);
        }

        /// <summary>로컬 Python 분석을 켤 때 한 번 보이는 알림. 취소하면 켜지 않는다.</summary>
        private bool ConfirmLocalPython()
        {
            bool summaryOnly = !Enum.TryParse<AgentDataPolicy>(_settings.AgentDataPolicy, out var p) || p == AgentDataPolicy.SummaryOnly;
            string text = LT(
                "Local Python analysis lets the AI agent export the current view to a file in the analysis folder (<workspace name>_분석결과 next to the workspace file, or <first file name>_분석결과 next to the first data file you opened; it stays the same when you switch tabs) and run Python on it on this PC.\n\n" +
                "Everything a script prints is read by the AI model: the data policy only controls what the app itself sends." +
                (summaryOnly ? " With 'Summary only' the agent is instructed to print aggregates only, but this cannot be fully enforced for code the agent writes." : "") +
                "\n\nThe first Python run of each conversation asks for your approval. Turn it on?",
                "로컬 Python 분석을 켜면 AI 에이전트가 현재 보기를 분석 폴더(작업 공간 파일 옆의 <작업 공간 이름>_분석결과, 없으면 처음 연 데이터 파일 옆의 <파일 이름>_분석결과 — 탭을 바꿔도 그대로)에 파일로 내보내 이 PC에서 Python으로 분석할 수 있습니다.\n\n" +
                "스크립트가 출력하는 모든 내용은 AI 모델이 읽습니다. 데이터 정책은 앱이 직접 보내는 내용만 제어합니다." +
                (summaryOnly ? " '요약만'이면 집계만 출력하라고 지시하지만, 에이전트가 쓰는 코드에는 완전히 강제할 수 없습니다." : "") +
                "\n\n대화마다 첫 Python 실행은 승인을 묻습니다. 켤까요?");
            return MessageBox.Show(this, text, LT("Local Python analysis", "로컬 Python 분석"),
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
        }

        /// <summary>승인 모드를 설정에 저장한다. 직접 고른 것이므로 기본 모드 안내는 더 보이지 않는다.</summary>
        private void SaveAgentApprovalMode(AgentApprovalMode mode)
        {
            _settings.AgentApprovalMode = AgentApprovalPolicy.ToOmp(mode);
            _settings.AgentApprovalNoticeShown = true;
            _settings.Save();
        }

        /// <summary>채팅 ⚙·슬래시 /settings가 부르는 진입점: 설정 대화 상자를 AI 쪽으로 연다.</summary>
        private void ShowAgentSettings() => ShowSettings("ai");

        /// <summary>설정 대화 상자 AI 쪽의 입력값.</summary>
        internal readonly record struct AgentSettingsInput(bool WorkspaceScope, AgentApprovalMode Mode, AgentDataPolicy Policy, bool Python,
            bool ApprovalEditable, int MaxRows, string OmpPath, string ExtraArgs);

        /// <summary>
        /// 설정 대화 상자 AI 쪽의 적용(확인·적용). 작업 공간 범위면 작업 공간 파일에 적고(앱 기본값과 같고 아직 값이 없으면 적지 않음 — 앱 설정을 따름),
        /// 앱 범위면 앱 설정에 적는다. 승인 모드·데이터 공유·로컬 Python은 둘 중 더 엄격한 쪽이 이기므로 풀리지 않은 값은 알린다.
        /// </summary>
        internal void ApplyAgentSettings(AgentSettingsInput v)
        {
            bool workspaceScope = v.WorkspaceScope && _wfFilePath is not null;
            var wantMode = v.Mode;
            var wantPolicy = v.Policy;
            bool wantPython = v.Python;
            bool approvalEditable = v.ApprovalEditable && AgentApprovalPolicy.ForcedByArgs(v.ExtraArgs) is null;
            var app = AppAgentOptions();

            if (workspaceScope)
            {
                // 작업 공간 파일에 적는다. 앱 기본값과 같고 아직 값이 없으면 따로 적지 않는다(앱 설정을 따름). 켜기 확인(로컬 Python)은 필요 없다 — 더 엄격한 쪽이 이기므로 풀지 못한다.
                if (approvalEditable)
                {
                    bool had = WorkspaceAgentPolicy.ParseApproval(_wfAgent) is not null;
                    if (wantMode == app.ApprovalMode && !had) _wfAgent.ApprovalMode = null;
                    else if (wantMode != (WorkspaceAgentPolicy.ParseApproval(_wfAgent) ?? app.ApprovalMode) && wantMode == AgentApprovalMode.Yolo && !ConfirmYolo()) { }
                    else { _wfAgent.ApprovalMode = AgentApprovalPolicy.ToOmp(wantMode); _settings.AgentApprovalNoticeShown = true; }
                }
                _wfAgent.DataPolicy = wantPolicy == app.DataPolicy && WorkspaceAgentPolicy.ParseDataPolicy(_wfAgent) is null ? null : wantPolicy.ToString();
                _wfAgent.AllowLocalPython = wantPython == app.AllowLocalPython && _wfAgent.AllowLocalPython is null ? null : wantPython;
            }
            else
            {
                if (wantPython && !_settings.AgentAllowLocalPython && !ConfirmLocalPython()) wantPython = false;
                if (approvalEditable && wantMode != app.ApprovalMode && (wantMode != AgentApprovalMode.Yolo || ConfirmYolo()))
                    SaveAgentApprovalMode(wantMode);
                _settings.AgentDataPolicy = wantPolicy.ToString();
                _settings.AgentAllowLocalPython = wantPython;
            }

            string oldPath = _settings.AgentOmpPath ?? "", oldArgs = _settings.AgentExtraArgs ?? "";
            _settings.AgentMaxRows = Math.Clamp(v.MaxRows, 1, 5000);
            _settings.AgentOmpPath = v.OmpPath.Trim();
            _settings.AgentExtraArgs = v.ExtraArgs.Trim();
            _settings.Save();
            ExplainNotApplied(approvalEditable ? wantMode : null, wantPolicy, wantPython);
            if (_agentController is null) return;
            _agentController.Options = AgentOptions();
            if (oldPath != _settings.AgentOmpPath || oldArgs != _settings.AgentExtraArgs)
                _ = _agentController.RestartAsync();
        }

        /// <summary>
        /// 설정 대화 상자의 분석 스킬 구역 적용: 설정에 이미 쓴 스킬 선택을 저장하고 에이전트 옵션에 반영한다. 로컬 Python이 켜져 있고 선택이 바뀌었으면
        /// 컨트롤러가 쉬는 대로 같은 대화로 다시 시작한다(실행 중인 omp는 시작 때만 스킬을 읽는다).
        /// </summary>
        internal void ApplyAgentSkillSettings()
        {
            _settings.Save();
            if (_agentController is not null) _agentController.Options = AgentOptions();
        }

        // 테마 변경(ApplyTheme)에서 호출.
        private void ApplyAgentTheme() => _agentPanel?.ApplyTheme(_theme == AppTheme.Dark, Font);

        // 종료(OnFormClosing)에서 호출.
        private void ShutdownAgent()
        {
            _agentPrewarmTimer?.Dispose();
            _agentPrewarmTimer = null;
            _agentController?.Dispose();
            _agentController = null;
        }
    }
}
