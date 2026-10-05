using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 작업 공간 메모 편집: 자료 설명·핵심 관계·분석 목표를 자유롭게 적는다. 작업 공간 파일에 저장되고 AI 에이전트에게는 자료로 전달된다
    /// (에이전트가 규칙을 바꾸는 지시로 읽지 않는다). 글자 수 제한은 <see cref="WorkspaceFileAgent.MaxNotesChars"/>.
    /// </summary>
    internal sealed class WorkspaceNotesDialog : Form
    {
        private readonly TextBox _text;
        private readonly Label _count;

        public string Value => _text.Text;

        private static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        public WorkspaceNotesDialog(string workspaceName, string initial, ThemePalette palette)
        {
            Text = LT("Workspace Notes", "작업 공간 메모") + " — " + workspaceName;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            Padding = new Padding(12);
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(420, 300);

            var hint = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 58, ForeColor = palette.Text, Name = "notesHint",
                Text = LT("Describe the data, key relations between the tables and your analysis goals. The notes are saved with the workspace file and shown to the AI agent as background information about the data (never as instructions that change its rules).",
                          "자료 설명, 표 사이의 핵심 관계, 분석 목표를 적어 두세요. 메모는 작업 공간 파일에 저장되고 AI 에이전트에게 자료에 대한 배경 정보로 전달됩니다(에이전트의 규칙을 바꾸는 지시로는 쓰이지 않습니다)."),
            };
            _text = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = false, ScrollBars = ScrollBars.Vertical, WordWrap = true,
                BackColor = palette.Surface, ForeColor = palette.Text, BorderStyle = BorderStyle.FixedSingle, Name = "notesText",
                MaxLength = WorkspaceFileAgent.MaxNotesChars, Text = (initial ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
            _count = new Label { AutoSize = true, ForeColor = palette.Text, Name = "notesCount", Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 8, 12, 0) };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            var cancel = new Button { Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(88, 28), Name = "notesCancel" };
            var ok = new Button { Text = LT("OK", "확인"), DialogResult = DialogResult.OK, Size = new Size(88, 28), Name = "notesOk" };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(_count);
            AcceptButton = null;   // Enter는 줄바꿈이다
            CancelButton = cancel;

            Controls.Add(_text);
            Controls.Add(hint);
            Controls.Add(buttons);
            _text.TextChanged += (_, _) => UpdateCount();
            UpdateCount();
            Shown += (_, _) => { _text.Focus(); _text.SelectionStart = _text.TextLength; };
        }

        private void UpdateCount() =>
            _count.Text = $"{_text.TextLength:N0} / {WorkspaceFileAgent.MaxNotesChars:N0}";
    }
}
