using System.IO;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>에이전트 설정(승인 모드·데이터 정책·로컬 Python)을 어디에 적용할까: 이 작업 공간(파일에 저장) 또는 앱 기본값.</summary>
    internal enum AgentSettingScope { Workspace, App }

    // v3.1: 작업 공간 파일(.ncvws v2)의 에이전트 정보 — 작업 공간별 대화(omp 세션)·제한 설정(앱 설정보다 엄격한 쪽이 이김)·메모.
    // 규칙은 Agent/WorkspaceAgentPolicy.cs, 대화 이어가기는 Agent/Chat/ChatController.Conversation.cs, 파일 형식은 Workspace/WorkspaceFile.cs.
    public partial class Form1
    {
        /// <summary>열려 있는 작업 공간의 에이전트 정보. 작업 공간 파일을 열면 파일의 것으로 바뀌고, 저장하면 파일에 쓰인다.</summary>
        private WorkspaceFileAgent _wfAgent = new();
        private ToolStripMenuItem? _wsNotesMenu, _wsNewConversationMenu;

        // 테스트가 대화 상자 없이 흐름을 검증하게 하는 이음매. null이면 실제 대화 상자를 띄운다.
        internal Func<string, string?>? WorkspaceNotesEditor;
        internal Func<string, AgentSettingScope?>? AgentScopeChooser;

        /// <summary>열려 있는 작업 공간의 에이전트 정보(읽기용 복사본이 아님 — 테스트·상태 점검용).</summary>
        internal WorkspaceFileAgent WorkspaceAgentInfo => _wfAgent;

        internal string WorkspaceNotes => _wfAgent.Notes ?? "";

        // ---- 메뉴 --------------------------------------------------------------------------------------------

        // BuildWorkspaceFileFeatures에서 호출(작업 공간 메뉴는 이미 만들어져 있다).
        private void BuildWorkspaceAgentMenu()
        {
            _wsNotesMenu = MakeItem("Workspace Notes…", "작업 공간 메모…", (_, _) => EditWorkspaceNotesCommand());
            _wsNewConversationMenu = MakeItem("Start New Conversation for This Workspace", "이 작업 공간의 새 대화 시작",
                async (_, _) => await StartNewWorkspaceConversationAsync());
            _wsMenu!.DropDownItems.Add(new ToolStripSeparator());
            _wsMenu.DropDownItems.Add(_wsNotesMenu);
            _wsMenu.DropDownItems.Add(_wsNewConversationMenu);
            _wsMenu.DropDownOpening += (_, _) =>
            {
                _wsNewConversationMenu.Enabled = _wfFilePath is not null;
                _wsNewConversationMenu.ToolTipText = _wfFilePath is null
                    ? LT("Save the workspace as a file first; a conversation is linked to a workspace file.", "먼저 작업 공간을 파일로 저장하세요. 대화는 작업 공간 파일에 연결됩니다.")
                    : "";
            };
        }

        // ---- 메모 --------------------------------------------------------------------------------------------

        /// <summary>메모를 바꾼다(글자 수 제한 적용, 비면 지움). 에이전트 맥락에 반영되고 작업 공간 파일을 저장할 때 함께 저장된다.</summary>
        internal void SetWorkspaceNotes(string? notes)
        {
            string? clean = string.IsNullOrWhiteSpace(notes) ? null : notes.Replace("\r\n", "\n").TrimEnd();
            if (clean is { Length: > WorkspaceFileAgent.MaxNotesChars }) clean = clean[..WorkspaceFileAgent.MaxNotesChars];
            if (string.Equals(clean, _wfAgent.Notes, StringComparison.Ordinal)) return;
            _wfAgent.Notes = clean;
            PostAgentContext();   // 다음에 (다시) 시작하는 omp 가이드에 새 메모가 실린다
        }

        /// <summary>작업 공간 ▸ 메모…</summary>
        internal void EditWorkspaceNotesCommand()
        {
            string? result;
            if (WorkspaceNotesEditor is { } editor) result = editor(WorkspaceNotes);
            else
            {
                string name = _wfFilePath is null ? LT("(unsaved workspace)", "(저장 안 한 작업 공간)") : Path.GetFileName(_wfFilePath);
                using var dlg = new WorkspaceNotesDialog(name, WorkspaceNotes, _palette);
                result = dlg.ShowDialog(this) == DialogResult.OK ? dlg.Value : null;
            }
            if (result is null) return;
            SetWorkspaceNotes(result);
            statusLabel.Text = LT("Workspace notes updated. Save the workspace to keep them.", "작업 공간 메모를 바꿨습니다. 작업 공간을 저장하면 파일에 남습니다.");
        }

        string IWorkspaceAgentHost.WorkspaceNotes { get { AgentAssertUi(); return WorkspaceNotes; } }

        void IWorkspaceAgentHost.SetWorkspaceNotes(string notes)
        {
            AgentAssertUi();
            SetWorkspaceNotes(notes);
        }

        // ---- 대화 --------------------------------------------------------------------------------------------

        private static WorkspaceConversation WorkspaceConversationOf(WorkspaceFileAgent? agent) =>
            agent?.Session is { } s ? new WorkspaceConversation(s.File, s.Id) : WorkspaceConversation.None;

        /// <summary>
        /// 저장할 에이전트 정보: 읽어 둔 정보에 지금 대화를 얹는다. 대화는 세션 파일이 실제로 있을 때만 적는다(아직 한 마디도 안 한 대화나
        /// 사라진 옛 연결은 적지 않는다). 컨트롤러가 아직 대화를 모르면(시작 전·omp 없음) 읽어 둔 연결을 그대로 둔다.
        /// </summary>
        private WorkspaceFileAgent CaptureAgentInfo()
        {
            var info = _wfAgent.Clone();
            WorkspaceConversation? current = _agentController is { SessionFile.Length: > 0 } c
                ? WorkspaceConversation.FromFile(c.SessionFile)
                : WorkspaceConversationOf(info);
            var (path, _) = ChatController.ResolveConversation(current, null);
            info.Session = path is null ? null : new WorkspaceFileSession { Id = SessionCatalog.IdOf(path), File = path };
            return info;
        }

        /// <summary>
        /// 작업 공간 파일을 열었다: 에이전트가 떠 있으면 그 작업 공간의 설정·메모·대화로 바꾼다(저장된 대화가 있으면 이어 가고, 없거나 사라졌으면 새 대화).
        /// 아직 안 떴으면 패널을 처음 열 때 같은 대화로 시작한다.
        /// </summary>
        private void SwitchAgentToOpenedWorkspace()
        {
            if (_agentController is not { } controller) return;
            _ = controller.SwitchWorkspaceAsync(AgentOptions(), BuildAgentWorkspaceContext(), WorkspaceConversationOf(_wfAgent));
        }

        /// <summary>작업 공간 ▸ 이 작업 공간의 새 대화 시작. 저장된 대화 연결을 새 대화로 바꾼다(작업 공간을 저장하면 파일에 반영).</summary>
        internal async Task StartNewWorkspaceConversationAsync()
        {
            if (_wfFilePath is null) return;
            string text = LT("Start a new conversation for this workspace? The current conversation stays saved in omp's session list, but the workspace will point to the new one once you save it.",
                             "이 작업 공간의 새 대화를 시작할까요? 지금 대화는 omp 세션 목록에 그대로 남지만, 작업 공간을 저장하면 새 대화가 이 작업 공간에 연결됩니다.");
            if (!ConfirmWorkspace(text)) return;
            _wfAgent.Session = null;
            if (_agentController is { } controller) await controller.StartNewConversationAsync();
            statusLabel.Text = LT("Started a new conversation for this workspace.", "이 작업 공간의 새 대화를 시작했습니다.");
        }

        // ---- 설정 ------------------------------------------------------------------------------------------------

        /// <summary>앱 설정만으로 만든 에이전트 옵션(작업 공간 제한을 합치기 전).</summary>
        private AgentHostOptions AppAgentOptions() => new(
            OmpPath: string.IsNullOrWhiteSpace(_settings.AgentOmpPath) ? null : _settings.AgentOmpPath,
            ExtraArgs: string.IsNullOrWhiteSpace(_settings.AgentExtraArgs) ? null : _settings.AgentExtraArgs,
            Language: Loc.CurrentLanguage == "ko" ? "ko" : "en",
            DataPolicy: Enum.TryParse<AgentDataPolicy>(_settings.AgentDataPolicy, out var p) ? p : AgentDataPolicy.SummaryOnly,
            MaxRowsPerRequest: Math.Clamp(_settings.AgentMaxRows, 1, 5000),
            AppVersion: AppInfo.Version,
            AllowLocalPython: _settings.AgentAllowLocalPython,
            ApprovalMode: AgentApprovalPolicy.Parse(_settings.AgentApprovalMode),
            ApprovalNoticePending: !_settings.AgentApprovalNoticeShown,
            SkillsEnabled: _settings.AgentSkillsEnabled,
            SkillCategoriesOff: _settings.AgentSkillCategoriesOff ?? "",
            SkillsOff: _settings.AgentSkillsOff ?? "",
            UseManagedPython: _settings.AgentUseManagedPython,
            PythonEnvNoticePending: !_settings.AgentPythonEnvNoticeShown,
            ModelUsageAlertedVersion: _settings.AgentModelUsageAlertVersion ?? "");

        /// <summary>설정 대화 상자가 보여 줄 값: 작업 공간 범위면 작업 공간에 적힌 값(없으면 앱 값), 앱 범위면 앱 값.</summary>
        internal (AgentApprovalMode Mode, AgentDataPolicy Policy, bool Python) ScopeBaseline(bool workspaceScope)
        {
            var app = AppAgentOptions();
            if (!workspaceScope) return (app.ApprovalMode, app.DataPolicy, app.AllowLocalPython);
            return (WorkspaceAgentPolicy.ParseApproval(_wfAgent) ?? app.ApprovalMode,
                    WorkspaceAgentPolicy.ParseDataPolicy(_wfAgent) ?? app.DataPolicy,
                    _wfAgent.AllowLocalPython ?? app.AllowLocalPython);
        }

        private AgentSettingScope? ChooseAgentScope(string settingText)
        {
            if (AgentScopeChooser is { } chooser) return chooser(settingText);
            string name = _wfFilePath is null ? "" : Path.GetFileName(_wfFilePath);
            var forWorkspace = new TaskDialogCommandLinkButton(LT("This workspace", "이 작업 공간"),
                LT($"Saved in {name} (when you save the workspace). A workspace can only make the app default stricter.",
                   $"{name}에 저장됩니다(작업 공간을 저장할 때). 작업 공간은 앱 기본값을 더 엄격하게만 바꿀 수 있습니다."));
            var forApp = new TaskDialogCommandLinkButton(LT("App default", "앱 기본값"),
                LT("Applies to every workspace and when no workspace is open.", "모든 작업 공간과 작업 공간이 없을 때 적용됩니다."));
            var page = new TaskDialogPage
            {
                Caption = LT("AI agent setting", "AI 에이전트 설정"),
                Heading = LT("Where should this setting apply?", "이 설정을 어디에 적용할까요?"),
                Text = settingText,
                Icon = TaskDialogIcon.Information,
                AllowCancel = true,
                Buttons = { forWorkspace, forApp, TaskDialogButton.Cancel },
                DefaultButton = forWorkspace,
            };
            var result = TaskDialog.ShowDialog(this, page);
            return result == forWorkspace ? AgentSettingScope.Workspace : result == forApp ? AgentSettingScope.App : null;
        }

        /// <summary>
        /// 채팅 승인 선택에서 모드를 골랐다(컨트롤러가 모두 허용 확인까지 마친 뒤 부른다). 작업 공간 파일이 열려 있으면 "이 작업 공간 / 앱 기본값"을 묻고
        /// 고른 곳에 저장한다(null = 취소). 열려 있지 않으면 앱 설정. 돌려주는 옵션은 작업 공간 제한을 합친 것이다.
        /// </summary>
        private AgentHostOptions? ApplyApprovalFromChat(AgentApprovalMode mode)
        {
            var scope = AgentSettingScope.App;
            if (_wfFilePath is not null)
            {
                bool ko = Loc.CurrentLanguage == "ko";
                if (ChooseAgentScope(LT("Approval mode: ", "승인 모드: ") + ApprovalTexts.Label(mode, ko)) is not { } chosen) return null;
                scope = chosen;
            }
            if (scope == AgentSettingScope.Workspace)
            {
                _wfAgent.ApprovalMode = AgentApprovalPolicy.ToOmp(mode);
                _settings.AgentApprovalNoticeShown = true;
                _settings.Save();
            }
            else SaveAgentApprovalMode(mode);
            return AgentOptions();
        }

        private bool ConfirmYolo()
        {
            bool ko = Loc.CurrentLanguage == "ko";
            return MessageBox.Show(this, ApprovalTexts.YoloConfirm(ko), ApprovalTexts.YoloTitle(ko), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }

        /// <summary>고른 값 중 작업 공간/앱 설정이 더 엄격해서 적용되지 않은 것을 알린다(더 엄격한 쪽이 이긴다).</summary>
        private void ExplainNotApplied(AgentApprovalMode? mode, AgentDataPolicy policy, bool python)
        {
            var eff = AgentOptions();
            var lines = new List<string>();
            if (mode is { } m && eff.ApprovalMode != m && eff.ForcedApproval is null)
                lines.Add(LT($"Approval mode: {ApprovalTexts.Label(eff.ApprovalMode, false)} stays", $"승인 모드: {ApprovalTexts.Label(eff.ApprovalMode, true)} 유지"));
            if (eff.DataPolicy != policy)
                lines.Add(LT($"Data sharing: {WorkspaceAgentPolicy.PolicyLabel(eff.DataPolicy, false)} stays", $"데이터 공유: {WorkspaceAgentPolicy.PolicyLabel(eff.DataPolicy, true)} 유지"));
            if (eff.AllowLocalPython != python)
                lines.Add(python ? LT("Local Python analysis stays off", "로컬 Python 분석은 꺼진 채로 유지") : LT("Local Python analysis stays on", "로컬 Python 분석은 켜진 채로 유지"));
            if (lines.Count == 0) return;
            MessageBox.Show(this,
                LT("Not applied: the app default or the workspace setting is stricter, and the stricter one always applies.\n\n",
                   "적용되지 않음: 앱 기본값 또는 작업 공간 설정이 더 엄격하며 더 엄격한 쪽이 항상 적용됩니다.\n\n") + string.Join("\n", lines.Select(l => "• " + l)) + "\n\n" +
                LT("To loosen a setting, change the app default (and remove the stricter value from the workspace).",
                   "풀려면 앱 기본값을 바꾸세요(그리고 작업 공간에 적힌 더 엄격한 값을 지우세요)."),
                LT("AI agent setting", "AI 에이전트 설정"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
