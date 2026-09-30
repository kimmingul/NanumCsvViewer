using System.Drawing;
using System.Windows.Forms;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    /// <summary>
    /// 모형식 편집기(이슈 #27). 왼쪽 컬럼 목록을 더블클릭하면 인용된 이름을 커서 위치에 넣고,
    /// 연산자 버튼(~ + : * C())으로 식을 조립한다. 입력마다 파싱·컬럼 해석을 검증해 오류를 즉시 보여 주고
    /// 유효할 때만 OK를 허용한다. 모형별 옵션(분포·연결 함수 등)은 AddOption으로 아래에 붙인다.
    /// </summary>
    internal sealed class FormulaDialog : Form
    {
        private readonly TextBox _formula;
        private readonly Label _status;
        private readonly TableLayoutPanel _options;
        private readonly Button _ok;
        private readonly IReadOnlyList<string> _headers;
        private readonly ThemePalette _palette;
        private readonly Func<ModelFormula, string?>? _extraValidation;

        /// <summary>검증된 식(OK 이후 유효).</summary>
        public ModelFormula? Parsed { get; private set; }
        public string FormulaString => _formula.Text.Trim();

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        /// <param name="headers">식 해석에 쓰는 순수 컬럼 이름(AdvHeaders).</param>
        /// <param name="columnLabels">목록 표시용(타입 배지 포함 가능). headers와 같은 순서·개수.</param>
        /// <param name="extraValidation">추가 검증(예: 응답이 수치여야 함). 문제면 메시지, 아니면 null.</param>
        public FormulaDialog(string title, IReadOnlyList<string> headers, IReadOnlyList<string> columnLabels,
            ThemePalette palette, string initialFormula = "", string? note = null,
            Func<ModelFormula, string?>? extraValidation = null)
        {
            _headers = headers;
            _palette = palette;
            _extraValidation = extraValidation;
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(820, 470);
            MinimumSize = new Size(640, 400);
            Padding = new Padding(12);

            var columns = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 250,
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                HorizontalScrollbar = true,
            };
            foreach (var label in columnLabels) columns.Items.Add(label);
            columns.DoubleClick += (_, _) =>
            {
                if (columns.SelectedIndex >= 0) Insert(FormulaText.QuoteName(_headers[columns.SelectedIndex]));
            };

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0) };

            var help = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 58,
                ForeColor = palette.Text,
                Text = note ?? LT(
                    "response ~ term + term …   a:b interaction · a*b = a + b + a:b · C(x) forces categorical · - 1 removes the intercept.\nDouble-click a column to insert it. Names with spaces are written as [name].",
                    "응답 ~ 항 + 항 …   a:b 상호작용 · a*b = a + b + a:b · C(x) 범주로 강제 · - 1 절편 제거.\n컬럼을 더블클릭하면 식에 들어갑니다. 공백이 있는 이름은 [이름]으로 씁니다."),
            };

            _formula = new TextBox
            {
                Dock = DockStyle.Top,
                Multiline = true,
                Height = 64,
                Font = new Font(FontFamily.GenericMonospace, 10f),
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Text = initialFormula,
                ScrollBars = ScrollBars.Vertical,
            };
            _formula.TextChanged += (_, _) => Revalidate();

            var ops = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            foreach (var (text, insert) in new[] { ("~", " ~ "), ("+", " + "), (":", ":"), ("*", " * "), ("C( )", "C()"), ("- 1", " - 1") })
            {
                var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(44, 26), FlatStyle = FlatStyle.Flat, BackColor = palette.Surface, ForeColor = palette.Text };
                b.FlatAppearance.BorderColor = palette.Border;
                b.Click += (_, _) =>
                {
                    Insert(insert);
                    if (insert == "C()") { _formula.SelectionStart -= 1; _formula.Focus(); }
                };
                ops.Controls.Add(b);
            }

            _status = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(0, 6, 0, 0) };

            _options = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            };
            _options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            _options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(88, 28) };
            _ok = new Button { Text = "OK", Size = new Size(88, 28) };
            _ok.Click += (_, _) => { if (Revalidate()) { DialogResult = DialogResult.OK; Close(); } };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            AcceptButton = _ok;
            CancelButton = cancel;

            // Dock=Top은 나중에 추가한 것이 위로 — 역순으로 추가.
            right.Controls.Add(_options);
            right.Controls.Add(_status);
            right.Controls.Add(ops);
            right.Controls.Add(_formula);
            right.Controls.Add(help);
            Controls.Add(right);
            Controls.Add(columns);
            Controls.Add(buttons);

            Revalidate();
        }

        /// <summary>모형 옵션 콤보(분포·연결 함수 등)를 식 아래에 추가.</summary>
        public ComboBox AddOption(string label, IEnumerable<string> items, int selected = 0)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 260,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
                Margin = new Padding(0, 4, 0, 4),
            };
            foreach (var it in items) combo.Items.Add(it);
            if (combo.Items.Count > 0) combo.SelectedIndex = Math.Clamp(selected, 0, combo.Items.Count - 1);
            combo.SelectedIndexChanged += (_, _) => Revalidate();
            AddOptionRow(label, combo);
            return combo;
        }

        /// <summary>자유 입력 옵션(예: 이벤트 수준).</summary>
        public TextBox AddTextOption(string label, string value = "")
        {
            var tb = new TextBox
            {
                Width = 260,
                Text = value,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 4, 0, 4),
            };
            AddOptionRow(label, tb);
            return tb;
        }

        private void AddOptionRow(string label, Control input)
        {
            int row = _options.RowCount;
            _options.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false,
                Width = 142,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = _palette.Text,
            }, 0, row);
            _options.Controls.Add(input, 1, row);
            _options.RowCount = row + 1;
        }

        public bool ShowOk(IWin32Window owner) => ShowDialog(owner) == DialogResult.OK && Parsed is not null;

        private void Insert(string text)
        {
            int at = _formula.SelectionStart;
            _formula.Text = _formula.Text.Remove(at, _formula.SelectionLength).Insert(at, text);
            _formula.SelectionStart = at + text.Length;
            _formula.SelectionLength = 0;
            _formula.Focus();
        }

        private bool Revalidate()
        {
            Parsed = null;
            string text = FormulaString;
            string? error = null;
            ModelFormula? parsed = null;
            if (text.Length == 0) error = LT("Enter a formula, e.g. y ~ x1 + C(group).", "식을 입력하세요. 예: y ~ x1 + C(group)");
            else
            {
                try
                {
                    parsed = ModelFormula.Parse(text);
                    StatValue.ResolveColumn(_headers, parsed.Response);
                    foreach (var v in parsed.PredictorVariables) StatValue.ResolveColumn(_headers, v);
                    error = _extraValidation?.Invoke(parsed);
                }
                catch (FormulaParseException ex) { error = ex.Message; }
                catch (DesignMatrixException ex) { error = ex.Message; }
            }
            _status.ForeColor = error is null ? _palette.Text : Color.FromArgb(200, 60, 60);
            _status.Text = error ?? "✓ " + parsed!;
            _ok.Enabled = error is null;
            if (error is null) Parsed = parsed;
            return error is null;
        }
    }
}
