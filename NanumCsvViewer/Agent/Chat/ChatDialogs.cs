using System.Diagnostics;

namespace NanumCsvViewer.Agent.Chat
{
    /// <summary>채팅 컨트롤러가 모달/OS 기능이 필요할 때 쓰는 경계(테스트에서 가짜로 대체). UI 스레드에서 호출.</summary>
    internal interface IChatDialogs
    {
        /// <summary>확인/취소 질문. true=확인.</summary>
        bool Confirm(string title, string message);
        /// <summary>목록에서 하나 고르기. 취소하면 null.</summary>
        string? Select(string title, IReadOnlyList<string> options);
        /// <summary>한 줄(또는 여러 줄) 입력. 취소하면 null.</summary>
        string? Input(string title, string prompt, string initial, bool multiline);
        /// <summary>HTML 내보내기 경로 선택. 취소하면 null.</summary>
        string? PickExportPath(string suggestedFileName);
        /// <summary>파일 여러 개 고르기(filter는 OpenFileDialog 형식). 취소하면 null.</summary>
        IReadOnlyList<string>? PickFiles(string title, string filter);
        /// <summary>폴더 하나 고르기. 취소하면 null.</summary>
        string? PickFolder(string description);
        /// <summary>http/https 주소를 기본 브라우저로 연다(그 밖의 스킴은 무시).</summary>
        void OpenUrl(string url);
        void SetClipboard(string text);
    }

    internal sealed class WinFormsChatDialogs : IChatDialogs
    {
        private readonly Func<bool> _korean;

        public WinFormsChatDialogs(Func<bool> korean) => _korean = korean;

        private string T(string en, string ko) => _korean() ? ko : en;

        public bool Confirm(string title, string message) =>
            MessageBox.Show(Form.ActiveForm, message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

        public string? Select(string title, IReadOnlyList<string> options)
        {
            if (options.Count == 0) return null;
            using var form = NewForm(title, 460, 380);
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var o in options) list.Items.Add(o);
            list.SelectedIndex = 0;
            var buttons = Buttons(form, out var ok);
            list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) ok.PerformClick(); };
            form.Controls.Add(list);
            form.Controls.Add(buttons);
            list.BringToFront();
            return form.ShowDialog(Form.ActiveForm) == DialogResult.OK && list.SelectedIndex >= 0
                ? (string)list.SelectedItem!
                : null;
        }

        public string? Input(string title, string prompt, string initial, bool multiline)
        {
            using var form = NewForm(title, 520, multiline ? 360 : 170);
            var label = new Label { Text = prompt, Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(0, 0, 0, 6) };
            var box = new TextBox
            {
                Text = initial,
                Multiline = multiline,
                AcceptsReturn = multiline,
                ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
                Dock = multiline ? DockStyle.Fill : DockStyle.Top,
            };
            var buttons = Buttons(form, out _);
            form.Controls.Add(box);
            form.Controls.Add(label);
            form.Controls.Add(buttons);
            if (multiline) box.BringToFront();
            form.Shown += (_, _) => { box.Focus(); box.SelectAll(); };
            return form.ShowDialog(Form.ActiveForm) == DialogResult.OK ? box.Text : null;
        }

        public string? PickExportPath(string suggestedFileName)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "HTML (*.html)|*.html",
                DefaultExt = "html",
                FileName = suggestedFileName,
                OverwritePrompt = true,
            };
            return dlg.ShowDialog(Form.ActiveForm) == DialogResult.OK ? dlg.FileName : null;
        }

        public IReadOnlyList<string>? PickFiles(string title, string filter)
        {
            using var dlg = new OpenFileDialog { Multiselect = true, Title = title, Filter = filter, CheckFileExists = true };
            return dlg.ShowDialog(Form.ActiveForm) == DialogResult.OK ? dlg.FileNames : null;
        }

        public string? PickFolder(string description)
        {
            using var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
            return dlg.ShowDialog(Form.ActiveForm) == DialogResult.OK ? dlg.SelectedPath : null;
        }

        public void OpenUrl(string url)
        {
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* 브라우저를 못 열어도 채팅은 계속 */ }
        }

        public void SetClipboard(string text)
        {
            try { Clipboard.SetText(text); }
            catch { /* 클립보드 점유 중 */ }
        }

        private Form NewForm(string title, int w, int h) => new()
        {
            Text = title,
            ClientSize = new Size(w, h),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Font = SystemFonts.MessageBoxFont,
            Padding = new Padding(12),
        };

        private Panel Buttons(Form form, out Button ok)
        {
            ok = new Button { Text = T("OK", "확인"), DialogResult = DialogResult.OK, Width = 88, Height = 28 };
            var cancel = new Button { Text = T("Cancel", "취소"), DialogResult = DialogResult.Cancel, Width = 88, Height = 28 };
            var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 42, Padding = new Padding(0, 8, 0, 0) };
            panel.Controls.Add(cancel);
            panel.Controls.Add(ok);
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            return panel;
        }
    }
}
