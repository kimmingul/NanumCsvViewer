using System.Drawing;
using System.Windows.Forms;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    /// <summary>
    /// 헤더 필터 팝오버. 범주형 컬럼은 검색+체크박스 목록, 날짜 컬럼은 날짜 범위. macOS ColumnFilterPopover 대응.
    /// 경계 없는 모달 폼으로 헤더 아래에 표시한다.
    /// </summary>
    internal sealed class ColumnFilterPopup : Form
    {
        private readonly ThemePalette _palette;
        private TableLayoutPanel _root = null!;

        // 결과(범주형)
        public List<string> SelectedValues { get; private set; } = new();
        public bool IncludeBlanks { get; private set; }
        public bool SelectAll { get; private set; }
        // 결과(날짜)
        public DateTime? RangeStart { get; private set; }
        public DateTime? RangeEnd { get; private set; }

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

        public ColumnFilterPopup(string columnName, DateTime? start, DateTime? end, ThemePalette palette)
        {
            _palette = palette;
            InitChrome(columnName);
            BuildDate(start, end);
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

        // ---- 날짜 ----

        private void BuildDate(DateTime? start, DateTime? end)
        {
            var panel = new Panel { BackColor = _palette.Surface };
            var fromUse = new CheckBox { Text = LT("From", "시작"), Location = new Point(8, 12), AutoSize = true, Checked = start is not null, ForeColor = _palette.Text };
            var fromPicker = new DateTimePicker { Format = DateTimePickerFormat.Short, Location = new Point(78, 8), Width = 160, Value = start ?? DateTime.Today };
            var toUse = new CheckBox { Text = LT("To", "끝"), Location = new Point(8, 48), AutoSize = true, Checked = end is not null, ForeColor = _palette.Text };
            var toPicker = new DateTimePicker { Format = DateTimePickerFormat.Short, Location = new Point(78, 44), Width = 160, Value = end ?? DateTime.Today };
            // 날짜를 바꾸면 해당 경계 체크박스를 자동으로 켠다.
            fromPicker.ValueChanged += (_, _) => fromUse.Checked = true;
            toPicker.ValueChanged += (_, _) => toUse.Checked = true;
            panel.Controls.AddRange(new Control[] { fromUse, fromPicker, toUse, toPicker });
            AddRow(panel, SizeType.Absolute, 82);

            AddRow(ButtonRow(out var ok, LT("Apply", "적용")), SizeType.AutoSize);
            Height = 24 + 82 + 44;

            ok.Click += (_, _) =>
            {
                RangeStart = fromUse.Checked ? fromPicker.Value.Date : null;
                RangeEnd = toUse.Checked ? toPicker.Value.Date : null;
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
