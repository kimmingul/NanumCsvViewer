using System.IO;
using System.Text.Json;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer
{
    // v2: 오른쪽에 도킹되는 AI 에이전트 채팅 패널(omp RPC 호스트). 설계: docs/AGENT_INTEGRATION_PLAN.md
    public partial class Form1
    {
        private SplitContainer? _agentSplit;
        private AgentChatPanel? _agentPanel;
        private ChatController? _agentController;
        private ToolStripButton? _agentButton;
        private ToolStripMenuItem? _agentPanelMenu, _agentSettingsMenu;
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

            _agentPanelMenu = MakeItem("AI Agent Panel", "AI 에이전트 패널", (_, _) => SetAgentPanelVisible(!AgentPanelVisible));
            _agentPanelMenu.ShortcutKeys = Keys.Control | Keys.Shift | Keys.A;
            _agentSettingsMenu = MakeItem("AI Agent Settings…", "AI 에이전트 설정…", (_, _) => ShowAgentSettings());
            viewToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            viewToolStripMenuItem.DropDownItems.Add(_agentPanelMenu);
            viewToolStripMenuItem.DropDownItems.Add(_agentSettingsMenu);

            _agentButton = new ToolStripButton
            {
                Text = "✦ AI",
                CheckOnClick = true,
                Alignment = ToolStripItemAlignment.Right,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                Overflow = ToolStripItemOverflow.Never,
            };
            _agentButton.CheckedChanged += (_, _) =>
            {
                if (!_syncingAgentToggle) SetAgentPanelVisible(_agentButton.Checked);
            };
            toolStrip1.Items.Add(_agentButton);
            LocalizeAgentUi();
        }

        private bool AgentPanelVisible => _agentSplit is { Panel2Collapsed: false };

        private void LocalizeAgentUi()
        {
            if (_agentButton is not null)
                _agentButton.ToolTipText = LT("AI agent panel (Ctrl+Shift+A)", "AI 에이전트 패널 (Ctrl+Shift+A)");
            string lang = Loc.CurrentLanguage == "ko" ? "ko" : "en";
            _agentPanel?.SetLanguage(lang);
            if (_agentController is not null) _agentController.Options = AgentOptions();
        }

        private AgentHostOptions AgentOptions() => new(
            OmpPath: string.IsNullOrWhiteSpace(_settings.AgentOmpPath) ? null : _settings.AgentOmpPath,
            ExtraArgs: string.IsNullOrWhiteSpace(_settings.AgentExtraArgs) ? null : _settings.AgentExtraArgs,
            Language: Loc.CurrentLanguage == "ko" ? "ko" : "en",
            DataPolicy: Enum.TryParse<AgentDataPolicy>(_settings.AgentDataPolicy, out var p) ? p : AgentDataPolicy.SummaryOnly,
            MaxRowsPerRequest: Math.Clamp(_settings.AgentMaxRows, 1, 5000),
            AppVersion: AppInfo.Version,
            AllowLocalPython: _settings.AgentAllowLocalPython,
            ApprovalMode: AgentApprovalPolicy.Parse(_settings.AgentApprovalMode),
            ApprovalNoticePending: !_settings.AgentApprovalNoticeShown);

        private void SetAgentPanelVisible(bool visible)
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
                _agentSplit.Panel2Collapsed = true;
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
            int width = Math.Clamp(LogicalToDeviceUnits(_settings.AgentPanelWidth), min, Math.Max(min, total - LogicalToDeviceUnits(300)));
            int distance = total - width - _agentSplit.SplitterWidth;
            if (distance > _agentSplit.Panel1MinSize)
            {
                _placingAgentSplitter = true;
                try { _agentSplit.SplitterDistance = distance; }
                finally { _placingAgentSplitter = false; }
                if (_agentSplit.Panel2MinSize < min && total - _agentSplit.SplitterDistance - _agentSplit.SplitterWidth >= min)
                    _agentSplit.Panel2MinSize = min;
            }
            _agentPanel!.FocusInput();
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
                _settings.Save();
            };
            _agentPanel.ApplyTheme(_theme == AppTheme.Dark, Font);
            _agentPanel.SetLanguage(Loc.CurrentLanguage == "ko" ? "ko" : "en");

            var options = AgentOptions();
            _agentController = new ChatController(_agentPanel, new CsvHostTools(this, AgentOptions), options);
            _agentController.PageMessageUnhandled += OnAgentPageMessage;
            _agentController.StatusChanged += _ => { };
            _agentController.ApprovalModeChanged += mode => SaveAgentApprovalMode(mode);
            _agentController.ApprovalNoticeShown += () => { _settings.AgentApprovalNoticeShown = true; _settings.Save(); };
            _agentController.SetDataFile(_currentPath);
            _ = _agentController.StartAsync(AgentWorkingDirectory());
            PostAgentContext();
        }

        /// <summary>omp의 작업 폴더: 열린 파일의 폴더, 없으면 문서 폴더.</summary>
        private string AgentWorkingDirectory()
        {
            string? dir = _currentPath is null ? null : Path.GetDirectoryName(_currentPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // LoadDocument·편집 변경 시: 채팅 입력창 위 파일 칩.
        private void PostAgentContext()
        {
            _agentController?.SetDataFile(_currentPath);
            if (_agentPanel is null) return;
            int edits = _doc is null || _doc.Edits.IsEmpty ? 0 : 1;
            _agentPanel.Post(JsonSerializer.Serialize(new
            {
                t = "context",
                file = _currentPath is null ? "" : Path.GetFileName(_currentPath),
                path = _currentPath ?? "",
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
                "Local Python analysis lets the AI agent export the current view to a file in the analysis folder (<file name>_분석결과, next to the data file) and run Python on it on this PC.\n\n" +
                "Everything a script prints is read by the AI model: the data policy only controls what the app itself sends." +
                (summaryOnly ? " With 'Summary only' the agent is instructed to print aggregates only, but this cannot be fully enforced for code the agent writes." : "") +
                "\n\nThe first Python run of each conversation asks for your approval. Turn it on?",
                "로컬 Python 분석을 켜면 AI 에이전트가 현재 보기를 분석 폴더(데이터 파일 옆의 <파일 이름>_분석결과)에 파일로 내보내 이 PC에서 Python으로 분석할 수 있습니다.\n\n" +
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

        /// <summary>
        /// 설정 대화 상자에서 고른 승인 모드를 적용한다. 바뀌었고 모두 허용(yolo)이면 확인 대화 상자를 먼저 띄우고 취소하면 이전 모드를 유지한다.
        /// 에이전트가 떠 있으면 컨트롤러가(확인·재시작·저장) 맡고, 없으면 설정만 저장한다.
        /// </summary>
        private void ApplyApprovalChoice(AgentApprovalMode mode)
        {
            if (mode == AgentApprovalPolicy.Parse(_settings.AgentApprovalMode)) return;
            if (_agentController is not null) { _agentController.TrySetApprovalMode(mode); return; }
            bool ko = Loc.CurrentLanguage == "ko";
            if (mode == AgentApprovalMode.Yolo && MessageBox.Show(this, ApprovalTexts.YoloConfirm(ko), ApprovalTexts.YoloTitle(ko),
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            SaveAgentApprovalMode(mode);
        }

        private void ShowAgentSettings()
        {
            using var dlg = new ParamDialog(LT("AI Agent Settings", "AI 에이전트 설정"), _palette);
            var policy = dlg.AddCombo(LT("Data sharing with the AI", "AI와의 데이터 공유"), new[]
            {
                LT("Summary only (no raw rows)", "요약만 (원시 행 보내지 않음)"),
                LT("Rows with approval", "행 값 — 요청마다 승인"),
                LT("Rows allowed (up to the limit)", "행 값 — 승인 없이(상한까지)"),
            }, (int)AgentOptions().DataPolicy);
            var maxRows = dlg.AddNumeric(LT("Row limit per request", "요청당 행 상한"), 1, 5000, Math.Clamp(_settings.AgentMaxRows, 1, 5000));
            var ompPath = dlg.AddText(LT("omp path (blank = auto)", "omp 경로 (비우면 자동)"), _settings.AgentOmpPath ?? "");
            var extra = dlg.AddText(LT("Extra omp arguments", "omp 추가 인자"), _settings.AgentExtraArgs ?? "");
            bool ko = Loc.CurrentLanguage == "ko";
            var approval = dlg.AddCombo(LT("Approval mode", "승인 모드"),
                Enum.GetValues<AgentApprovalMode>().Select(m => ApprovalTexts.Label(m, ko)).ToArray(),
                (int)AgentApprovalPolicy.Parse(_settings.AgentApprovalMode));
            // omp 추가 인자(--approval-mode·--yolo·--auto-approve)가 모드를 고정하면 선택을 막고 이유를 보여 준다.
            if (AgentApprovalPolicy.ForcedByArgs(_settings.AgentExtraArgs) is { } forced)
            {
                approval.SelectedIndex = (int)forced.Mode;
                approval.Enabled = false;
                dlg.AddNote(ApprovalTexts.Locked(forced.Flag, ko));
            }
            var localPython = dlg.AddCheckedList(LT("Local Python analysis", "로컬 Python 분석"),
                new[] { LT("Allow local Python analysis", "로컬 Python 분석 허용") }, 1);
            localPython.CheckOnClick = true;
            localPython.SetItemChecked(0, _settings.AgentAllowLocalPython);
            dlg.AddNote(LT(
                "When on, the agent may export the current view to a file in the analysis folder (<file name>_분석결과, next to the data file) and run Python on it (omp's eval tool, needs Python 3.10+). Everything a script prints is read by the AI model; with 'Summary only' the agent is told to print aggregates only, but that cannot be fully enforced for code it writes. The first Python run of each conversation asks for your approval.",
                "켜면 에이전트가 현재 보기를 분석 폴더(데이터 파일 옆의 <파일 이름>_분석결과)에 파일로 내보내 Python(omp eval 도구, Python 3.10 이상 필요)으로 분석할 수 있습니다. 스크립트가 출력하는 모든 내용은 AI 모델이 읽습니다. '요약만'이면 집계만 출력하라고 지시하지만, 에이전트가 쓰는 코드에는 완전히 강제할 수 없습니다. 대화마다 첫 Python 실행은 승인을 묻습니다."));
            dlg.AddNote(LT(
                "Approval mode: 'Always ask' asks for every write or run, in the app and in omp. 'Auto-approve edits' lets undoable data edits run without a card and lets omp write files, but still asks for Python/shell runs, saving to a new file and sharing raw rows. 'Allow everything' also skips those (raw-row sharing still follows the data sharing setting). Changing it restarts the agent on the same conversation.",
                "승인 모드: '항상 묻기'는 앱과 omp 모두 쓰기·실행마다 묻습니다. '편집 자동 승인'은 되돌릴 수 있는 데이터 편집을 카드 없이 실행하고 omp의 파일 쓰기도 허용하지만 Python·셸 실행, 새 파일 저장, 원시 행 공유는 묻습니다. '모두 허용'은 그것들도 묻지 않습니다(원시 행 공유는 데이터 공유 설정을 따름). 바꾸면 같은 대화로 에이전트를 다시 시작합니다."));
            dlg.AddNote(LT(
                "The agent is omp (oh-my-pi), which uses the models you configured in omp. 'Summary only' sends the schema, aggregates and analysis results, never raw cell values. Edits are stored in an undoable overlay; the original file is never written.",
                "에이전트는 omp(oh-my-pi)이며 omp에 설정한 모델을 씁니다. '요약만'은 스키마·집계·분석 결과만 보내고 원시 셀 값은 보내지 않습니다. 편집은 되돌릴 수 있는 덮개에 쌓이고 원본 파일은 쓰지 않습니다."));
            if (!dlg.ShowOk(this)) return;

            bool wantPython = localPython.GetItemChecked(0);
            if (wantPython && !_settings.AgentAllowLocalPython && !ConfirmLocalPython()) wantPython = false;
            if (approval.Enabled && AgentApprovalPolicy.ForcedByArgs(extra.Text) is null)
                ApplyApprovalChoice((AgentApprovalMode)Math.Clamp(approval.SelectedIndex, 0, 2));

            string oldPath = _settings.AgentOmpPath ?? "", oldArgs = _settings.AgentExtraArgs ?? "";
            _settings.AgentDataPolicy = ((AgentDataPolicy)Math.Clamp(policy.SelectedIndex, 0, 2)).ToString();
            _settings.AgentMaxRows = (int)maxRows.Value;
            _settings.AgentOmpPath = ompPath.Text.Trim();
            _settings.AgentExtraArgs = extra.Text.Trim();
            _settings.AgentAllowLocalPython = wantPython;
            _settings.Save();
            if (_agentController is null) return;
            _agentController.Options = AgentOptions();
            if (oldPath != _settings.AgentOmpPath || oldArgs != _settings.AgentExtraArgs)
                _ = _agentController.RestartAsync();
        }

        // 테마 변경(ApplyTheme)에서 호출.
        private void ApplyAgentTheme() => _agentPanel?.ApplyTheme(_theme == AppTheme.Dark, Font);

        // 종료(OnFormClosing)에서 호출.
        private void ShutdownAgent()
        {
            _agentController?.Dispose();
            _agentController = null;
        }
    }
}
