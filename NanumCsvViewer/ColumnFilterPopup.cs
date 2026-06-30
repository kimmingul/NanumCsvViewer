using System.Globalization;
using System.Drawing;
using System.Windows.Forms;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    /// <summary>
    /// 헤더 필터 팝오버(타입별 모드): 범주/불리언=검색+체크박스, 숫자=범위(min~max),
    /// 시간계열=정밀도별 범위(날짜/일시/시각), 문자열·식별자=텍스트 술어(포함/일치/정규식/빈값 등).
    /// 경계 없는 모달 폼으로 헤더 아래에 표시한다. macOS ColumnFilterPopover 대응.
    /// </summary>
    internal sealed class ColumnFilterPopup : Form
    {
        private readonly ThemePalette _palette;
        private TableLayoutPanel _root = null!;

        // 결과(범주형)
        public List<string> SelectedValues { get; private set; } = new();
        public bool IncludeBlanks { get; private set; }
        public bool SelectAll { get; private set; }
        // 결과(시간 범위)
        public DateTime? RangeStart { get; private set; }
        public DateTime? RangeEnd { get; private set; }
        // 결과(숫자 범위)
        public double? NumMin { get; private set; }
        public double? NumMax { get; private set; }
        // 결과(텍스트 술어)
        public TextFilterOp TextOp { get; private set; }
        public string TextValue { get; private set; } = "";
        public bool TextCaseSensitive { get; private set; }

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        // 범주형 상태
        private readonly List<(string Value, int Count)> _distinct = new();
        private readonly List<string> _visibleValues = new();
        private readonly Dictionary<string, bool> _checkState = new(StringComparer.Ordinal);
        private CheckedListBox? _list;
        private string BlankLabel => LT("(Blank)", "(빈 값)");

        public ColumnFilterPopup(string columnName, IReadOnlyList<(string Value, int Count)> distinct, SelectedValuesFilter? current, ThemePalette palette)
        {
            _palette = palette;
            _distinct.AddRange(distinct);
            InitChrome(columnName);

            bool noFilter = current is null;
            var set = current is null ? null : new HashSet<string>(current.Values, StringComparer.Ordinal);
            foreach (var (val, _) in _distinct)
                _checkState[val] = noFilter || (val.Length == 0 ? current!.IncludeBlanks : set!.Contains(val));

            BuildCategorical();
        }

        public ColumnFilterPopup(string columnName, DateTime? start, DateTime? end, TemporalFilterKind kind, ThemePalette palette)
        {
            _palette = palette;
            InitChrome(columnName);
            BuildTemporal(start, end, kind);
        }

        public ColumnFilterPopup(string columnName, double? min, double? max, ThemePalette palette)
        {
            _palette = palette;
            InitChrome(columnName);
            BuildNumeric(min, max);
        }

        public ColumnFilterPopup(string columnName, TextFilter? current, ThemePalette palette)
        {
            _palette = palette;
            InitChrome(columnName);
            BuildText(current);
        }

        private void InitChrome(string columnName)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = _palette.Surface;
            ForeColor = _palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            Width = 262;
            Padding = new Padding(1);
            Paint += (_, e) => { using var p = new Pen(_palette.Border); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };

            _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = _palette.Surface };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(_root);

            AddRow(new Label
            {
                Text = columnName,
                ForeColor = _palette.Accent,
                Font = new Font(Font, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Height = 24,
            }, SizeType.Absolute, 24);
        }

        private void AddRow(Control c, SizeType type, int size = 0)
        {
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0);
            int r = _root.RowCount;
            _root.Controls.Add(c, 0, r);
            _root.RowStyles.Add(new RowStyle(type, size));
            _root.RowCount = r + 1;
        }

        // ---- 범주형 ----

        private void BuildCategorical()
        {
            var search = new TextBox { BackColor = _palette.Surface, ForeColor = _palette.Text, BorderStyle = BorderStyle.FixedSingle };
            search.TextChanged += (_, _) => Populate(search.Text);
            AddRow(search, SizeType.Absolute, 26);

            _list = new CheckedListBox
            {
                CheckOnClick = true,
                BorderStyle = BorderStyle.None,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
                IntegralHeight = false,
            };
            _list.ItemCheck += (s, e) =>
            {
                if (e.Index >= 0 && e.Index < _visibleValues.Count)
                    _checkState[_visibleValues[e.Index]] = e.NewValue == CheckState.Checked;
            };
            AddRow(_list, SizeType.Percent, 100);

            var selectRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = _palette.Surface, Padding = new Padding(6, 2, 6, 2) };
            selectRow.Controls.Add(FlatButton(LT("Select All", "전체 선택"), (_, _) => SetAllChecks(true)));
            selectRow.Controls.Add(FlatButton(LT("Clear", "해제"), (_, _) => SetAllChecks(false)));
            AddRow(selectRow, SizeType.AutoSize);

            AddRow(ButtonRow(out var ok, LT("Apply", "적용")), SizeType.AutoSize);
            Height = 360;
            Populate("");

            ok.Click += (_, _) =>
            {
                SelectAll = _distinct.All(d => _checkState.TryGetValue(d.Value, out bool b) && b);
                SelectedValues = _checkState.Where(kv => kv.Value && kv.Key.Length > 0).Select(kv => kv.Key).ToList();
                IncludeBlanks = _checkState.TryGetValue("", out bool blank) && blank;
                DialogResult = DialogResult.OK;
            };
        }

        private void Populate(string search)
        {
            if (_list is null) return;
            _list.BeginUpdate();
            _list.Items.Clear();
            _visibleValues.Clear();
            foreach (var (val, count) in _distinct)
            {
                string label = val.Length == 0 ? BlankLabel : val;
                if (search.Length > 0 && label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int idx = _list.Items.Add(count > 0 ? $"{label}  ({count:N0})" : label);
                _visibleValues.Add(val);
                _list.SetItemChecked(idx, _checkState.TryGetValue(val, out bool b) && b);
            }
            _list.EndUpdate();
        }

        private void SetAllChecks(bool value)
        {
            foreach (var v in _visibleValues) _checkState[v] = value;
            for (int i = 0; i < _list!.Items.Count; i++) _list.SetItemChecked(i, value);
        }

        // ---- 시간 범위(날짜 / 일시 / 시각) ----

        private void BuildTemporal(DateTime? start, DateTime? end, TemporalFilterKind kind)
        {
            DateTimePicker MakePicker(int y, DateTime? val)
            {
                var p = new DateTimePicker { Location = new Point(78, y), Width = 160, Value = val ?? DateTime.Today };
                switch (kind)
                {
                    case TemporalFilterKind.DateTime:
                        p.Format = DateTimePickerFormat.Custom; p.CustomFormat = "yyyy-MM-dd HH:mm:ss"; break;
                    case TemporalFilterKind.Time:
                        p.Format = DateTimePickerFormat.Time; p.ShowUpDown = true; break;
                    default:
                        p.Format = DateTimePickerFormat.Short; break;
                }
                return p;
            }

            var panel = new Panel { BackColor = _palette.Surface };
            var fromUse = new CheckBox { Text = LT("From", "시작"), Location = new Point(8, 12), AutoSize = true, Checked = start is not null, ForeColor = _palette.Text };
            var fromPicker = MakePicker(8, start);
            var toUse = new CheckBox { Text = LT("To", "끝"), Location = new Point(8, 48), AutoSize = true, Checked = end is not null, ForeColor = _palette.Text };
            var toPicker = MakePicker(44, end);
            // 값을 바꾸면 해당 경계 체크박스를 자동으로 켠다.
            fromPicker.ValueChanged += (_, _) => fromUse.Checked = true;
            toPicker.ValueChanged += (_, _) => toUse.Checked = true;
            panel.Controls.AddRange(new Control[] { fromUse, fromPicker, toUse, toPicker });
            AddRow(panel, SizeType.Absolute, 82);

            AddRow(ButtonRow(out var ok, LT("Apply", "적용")), SizeType.AutoSize);
            Height = 24 + 82 + 44;

            ok.Click += (_, _) =>
            {
                // Date는 시각을 버리고, DateTime/Time은 시각을 보존한다.
                RangeStart = fromUse.Checked ? (kind == TemporalFilterKind.Date ? fromPicker.Value.Date : fromPicker.Value) : null;
                RangeEnd = toUse.Checked ? (kind == TemporalFilterKind.Date ? toPicker.Value.Date : toPicker.Value) : null;
                DialogResult = DialogResult.OK;
            };
        }

        // ---- 숫자 범위 ----

        private void BuildNumeric(double? min, double? max)
        {
            var panel = new Panel { BackColor = _palette.Surface };
            panel.Controls.Add(new Label { Text = LT("Min", "최소"), Location = new Point(8, 14), AutoSize = true, ForeColor = _palette.Text });
            var minBox = new TextBox { Location = new Point(78, 10), Width = 160, BackColor = _palette.Surface, ForeColor = _palette.Text, BorderStyle = BorderStyle.FixedSingle, Text = min?.ToString(CultureInfo.InvariantCulture) ?? "" };
            panel.Controls.Add(new Label { Text = LT("Max", "최대"), Location = new Point(8, 50), AutoSize = true, ForeColor = _palette.Text });
            var maxBox = new TextBox { Location = new Point(78, 46), Width = 160, BackColor = _palette.Surface, ForeColor = _palette.Text, BorderStyle = BorderStyle.FixedSingle, Text = max?.ToString(CultureInfo.InvariantCulture) ?? "" };
            panel.Controls.Add(minBox);
            panel.Controls.Add(maxBox);
            AddRow(panel, SizeType.Absolute, 82);

            AddRow(ButtonRow(out var ok, LT("Apply", "적용")), SizeType.AutoSize);
            Height = 24 + 82 + 44;

            ok.Click += (_, _) =>
            {
                NumMin = ParseNullable(minBox.Text);
                NumMax = ParseNullable(maxBox.Text);
                DialogResult = DialogResult.OK;
            };
        }

        private static double? ParseNullable(string s)
            => double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) ? d : null;

        // ---- 텍스트 술어(문자열 / 식별자) ----

        private void BuildText(TextFilter? current)
        {
            var ops = new (TextFilterOp Op, string Label)[]
            {
                (TextFilterOp.Contains, LT("contains", "포함")),
                (TextFilterOp.Equals, LT("equals", "일치")),
                (TextFilterOp.StartsWith, LT("starts with", "~로 시작")),
                (TextFilterOp.EndsWith, LT("ends with", "~로 끝남")),
                (TextFilterOp.Regex, LT("regex", "정규식")),
                (TextFilterOp.IsBlank, LT("is blank", "빈 값")),
                (TextFilterOp.IsNotBlank, LT("is not blank", "비어있지 않음")),
            };

            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(8, 8), Width = 230, BackColor = _palette.Surface, ForeColor = _palette.Text };
            foreach (var o in ops) combo.Items.Add(o.Label);
            int sel = current is null ? 0 : Array.FindIndex(ops, o => o.Op == current.Op);
            combo.SelectedIndex = sel < 0 ? 0 : sel;

            var valueBox = new TextBox { Location = new Point(8, 40), Width = 230, BackColor = _palette.Surface, ForeColor = _palette.Text, BorderStyle = BorderStyle.FixedSingle, Text = current?.Value ?? "" };
            var caseChk = new CheckBox { Text = LT("Case sensitive", "대소문자 구분"), Location = new Point(8, 72), AutoSize = true, ForeColor = _palette.Text, Checked = current?.CaseSensitive ?? false };

            void SyncEnabled()
            {
                var op = ops[combo.SelectedIndex].Op;
                valueBox.Enabled = op is not (TextFilterOp.IsBlank or TextFilterOp.IsNotBlank);
            }
            combo.SelectedIndexChanged += (_, _) => SyncEnabled();
            SyncEnabled();

            var panel = new Panel { BackColor = _palette.Surface };
            panel.Controls.AddRange(new Control[] { combo, valueBox, caseChk });
            AddRow(panel, SizeType.Absolute, 100);

            AddRow(ButtonRow(out var ok, LT("Apply", "적용")), SizeType.AutoSize);
            Height = 24 + 100 + 44;

            ok.Click += (_, _) =>
            {
                TextOp = ops[combo.SelectedIndex].Op;
                TextValue = valueBox.Text;
                TextCaseSensitive = caseChk.Checked;
                DialogResult = DialogResult.OK;
            };
        }

        // ---- 공통 ----

        private FlowLayoutPanel ButtonRow(out Button ok, string applyText)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Padding = new Padding(6), BackColor = _palette.Surface };
            ok = new Button { Text = applyText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(72, 26) };
            var cancel = new Button { Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(72, 26) };
            row.Controls.Add(ok);
            row.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            return row;
        }

        private Button FlatButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, AutoSize = true, MinimumSize = new Size(80, 24), ForeColor = _palette.Text, BackColor = _palette.Surface, Margin = new Padding(0, 0, 6, 0) };
            b.FlatAppearance.BorderColor = _palette.Border;
            b.Click += onClick;
            return b;
        }

        public bool ShowAt(IWin32Window owner, Point screenPt)
        {
            var screen = Screen.FromPoint(screenPt).WorkingArea;
            int x = Math.Min(screenPt.X, screen.Right - Width - 4);
            int y = Math.Min(screenPt.Y, screen.Bottom - Height - 4);
            Location = new Point(Math.Max(screen.Left, x), Math.Max(screen.Top, y));
            return ShowDialog(owner) == DialogResult.OK;
        }
    }
}
