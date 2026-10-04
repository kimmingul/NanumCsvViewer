namespace NanumCsvViewer
{
    /// <summary>
    /// 단일 셀 편집 대화상자. 전체 원본 값(미리보기 잘림·통화/퍼센트 표시 스킨 없이)을 일반 텍스트로 편집하고,
    /// 값을 어떤 형식으로도 해석하지 않는다 — 001은 001 그대로 돌려준다.
    /// </summary>
    internal sealed class CellEditDialog : Form
    {
        private readonly TextBox _text;

        /// <summary>OK 시 편집된 텍스트(줄바꿈은 호출자가 정규화).</summary>
        public string Value => _text.Text;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        public CellEditDialog(string columnName, long sourceRow, string current, string original, bool edited, ThemePalette palette)
        {
            Text = LT("Edit Cell", "셀 편집");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(520, 320);
            MinimumSize = new Size(380, 240);
            Padding = new Padding(12);

            var header = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 44,
                ForeColor = palette.Text,
                Text = LT($"Column: {columnName}   ·   Source row: {sourceRow:N0}\r\nText is stored exactly as typed (leading zeros such as 001 are kept). The original file is not changed.",
                          $"컬럼: {columnName}   ·   원본 행: {sourceRow:N0}\r\n입력한 텍스트 그대로 저장합니다(001 같은 선행 0 유지). 원본 파일은 바뀌지 않습니다."),
            };

            _text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 10f),
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Text = current.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"),
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            var cancel = new Button { Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(88, 28) };
            var ok = new Button { Text = LT("Apply", "적용"), DialogResult = DialogResult.OK, Size = new Size(88, 28) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            if (edited)
            {
                var restore = new Button { Text = LT("Restore Original", "원래 값으로"), AutoSize = true, MinimumSize = new Size(110, 28) };
                restore.Click += (_, _) => { _text.Text = original.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"); _text.Focus(); };
                buttons.Controls.Add(restore);
            }
            CancelButton = cancel;

            Controls.Add(_text);
            Controls.Add(header);
            Controls.Add(buttons);
            Shown += (_, _) => { _text.Focus(); _text.SelectAll(); };
        }
    }
}
