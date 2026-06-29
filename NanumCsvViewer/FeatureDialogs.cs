using System.Drawing;
using System.Windows.Forms;

namespace NanumCsvViewer
{
    /// <summary>
    /// 라벨+입력 행을 동적으로 쌓아 매개변수를 받는 재사용 다이얼로그(분석/통계/필터 등).
    /// 콘텐츠 너비에 맞춰 폼을 수동 사이징하여 입력 컨트롤이 잘리지 않게 한다.
    /// </summary>
    internal sealed class ParamDialog : Form
    {
        private readonly TableLayoutPanel _table;
        private readonly ThemePalette _palette;
        private const int Pad = 16;
        private const int LabelW = 160;
        private const int InputW = 280;
        private bool _finished;

        public ParamDialog(string title, ThemePalette palette)
        {
            _palette = palette;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

            // 2열(라벨 | 입력) 표. AutoSize로 콘텐츠에 맞춰 자라되, 너비는 Dock에 종속되지 않음.
            _table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(Pad, Pad),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
                Padding = new Padding(0),
            };
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelW));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(_table);
        }

        private T AddRow<T>(string label, T input) where T : Control
        {
            int row = _table.RowCount;
            var lbl = new Label
            {
                Text = label,
                AutoSize = false,
                Width = LabelW - 8,
                Height = Math.Max(24, input.Height),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = _palette.Text,
                Margin = new Padding(0, 4, 8, 4),
            };
            input.Margin = new Padding(0, 4, 0, 4);
            _table.Controls.Add(lbl, 0, row);
            _table.Controls.Add(input, 1, row);
            _table.RowCount = row + 1;
            return input;
        }

        public ComboBox AddCombo(string label, IEnumerable<string> items, int selected = 0)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = InputW,
                DropDownWidth = InputW + 80, // 긴 컬럼명도 드롭다운에서 잘 보이게
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
            };
            foreach (var it in items) combo.Items.Add(it);
            if (combo.Items.Count > 0) combo.SelectedIndex = Math.Clamp(selected, 0, combo.Items.Count - 1);
            return AddRow(label, combo);
        }

        public CheckedListBox AddCheckedList(string label, IEnumerable<string> items, int visibleRows = 6)
        {
            var list = new CheckedListBox
            {
                Width = InputW,
                Height = Math.Max(2, visibleRows) * 20 + 6,
                CheckOnClick = true,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
            };
            foreach (var it in items) list.Items.Add(it);
            return AddRow(label, list);
        }

        public NumericUpDown AddNumeric(string label, int min, int max, int value)
        {
            var num = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                Width = 110,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
            };
            return AddRow(label, num);
        }

        public TextBox AddText(string label, string value = "")
        {
            var tb = new TextBox
            {
                Text = value,
                Width = InputW,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
            return AddRow(label, tb);
        }

        public void AddNote(string text)
        {
            int row = _table.RowCount;
            var lbl = new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(LabelW + InputW, 0),
                ForeColor = _palette.Text,
                Margin = new Padding(0, 2, 0, 8),
            };
            _table.Controls.Add(lbl, 0, row);
            _table.SetColumnSpan(lbl, 2);
            _table.RowCount = row + 1;
        }

        public bool ShowOk(IWin32Window owner)
        {
            EnsureButtons();
            return ShowDialog(owner) == DialogResult.OK;
        }

        private void EnsureButtons()
        {
            if (_finished) return;
            _finished = true;

            _table.PerformLayout();
            Size content = _table.PreferredSize;
            int width = content.Width + Pad * 2;
            int btnY = Pad + content.Height + 14;

            // 먼저 클라이언트 크기를 확정한 뒤, 그 너비에 맞춰 버튼을 우측 정렬(잘림 방지).
            ClientSize = new Size(width, btnY + 28 + Pad);

            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(88, 28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(88, 28) };
            cancel.Location = new Point(ClientSize.Width - Pad - cancel.Width, btnY);
            ok.Location = new Point(cancel.Left - 8 - ok.Width, btnY);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    /// <summary>분석/통계 결과를 고정폭 텍스트로 표시(복사 가능). 큰 표도 스크롤로 확인.</summary>
    internal sealed class ResultForm : Form
    {
        public ResultForm(string title, string body, ThemePalette palette)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(640, 480);
            BackColor = palette.Window;
            ForeColor = palette.Text;

            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9.5f),
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.None,
                Text = body.Replace("\n", "\r\n"),
            };

            var bottom = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(6),
            };
            var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 26) };
            var copy = new Button { Text = "Copy", AutoSize = true, MinimumSize = new Size(80, 26) };
            copy.Click += (_, _) => { try { Clipboard.SetText(body); } catch { } };
            bottom.Controls.Add(close);
            bottom.Controls.Add(copy);

            Controls.Add(text);
            Controls.Add(bottom);
            AcceptButton = close;
        }
    }
}
