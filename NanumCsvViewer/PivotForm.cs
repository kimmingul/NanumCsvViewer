using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    /// <summary>
    /// 엑셀식 피벗 빌더: 필드를 행/열/값/필터에 배정하고 표·차트로 결과를 본다.
    /// 각 칩(chip)에 인라인 컨트롤을 둔다 — Date 차원은 날짜 주기 콤보, 값은 (타입별) 집계 함수 콤보.
    /// 집계 엔진은 <see cref="CsvAnalytics.PivotTable"/>(이미 이식·테스트됨)를 사용.
    /// </summary>
    internal sealed class PivotForm : Form
    {
        private sealed class DimItem { public int Field; public DateBinPeriod? Period; }
        private sealed class Measure { public int Field; public AggregationFunction Func; }
        private sealed class FilterItem { public int Col; public string Value = ""; public DateBinPeriod? Period; }

        private readonly string[] _headers;
        private readonly ColumnSummary[] _summaries;
        private readonly List<string[]> _rows;
        private readonly ThemePalette _palette;

        private readonly List<DimItem> _rowDims = new();
        private readonly List<DimItem> _colDims = new();
        private readonly List<Measure> _measures = new();
        private readonly List<FilterItem> _filters = new();

        private ListBox _fieldsList = null!;
        private TableLayoutPanel _rowsTable = null!, _colsTable = null!, _valuesTable = null!, _filtersTable = null!;
        private ComboBox _chartTypeCombo = null!, _measureCombo = null!;
        private DataGridView _resultGrid = null!;
        private ChartControl _chart = null!;
        private TabControl _resultTabs = null!;

        private List<PivotTableResult> _results = new();

        private static readonly Color RowAccent = Color.FromArgb(46, 111, 176);
        private static readonly Color ColAccent = Color.FromArgb(27, 158, 119);
        private static readonly Color ValAccent = Color.FromArgb(217, 95, 2);
        private static readonly Color FilAccent = Color.FromArgb(123, 94, 167);
        private Color ChipBg => _palette.AltRow;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        public PivotForm(string[] headers, ColumnSummary[] summaries, List<string[]> rows, ThemePalette palette, AppTheme theme)
        {
            _headers = headers;
            _summaries = summaries;
            _rows = rows;
            _palette = palette;

            Text = LT("Pivot Builder", "피벗 빌더");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(1160, 750);
            MinimumSize = new Size(880, 640);
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

            ThemeManager.Apply(this, theme); // 폼 배경 + 전역 툴스트립 렌더러
            BuildUi();
            StyleResultGrid();
        }

        /// <summary>차트 탭을 선택한 채로 연다(피벗 ▸ 피벗차트 메뉴용).</summary>
        public void SelectChartTab()
        {
            try { if (_resultTabs.TabPages.Count > 1) _resultTabs.SelectedIndex = 1; } catch { }
        }

        // ---------------------------------------------------------------- UI 구성

        private void BuildUi()
        {
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 58, SplitterWidth = 6 };
            Controls.Add(split);

            // ── 빌더(좌) ──
            var b = split.Panel1;
            b.BackColor = _palette.Window;
            b.AutoScroll = true;
            b.Padding = new Padding(12);

            var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = _palette.Window };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            b.Controls.Add(stack);

            void AddRow(Control c, int height)
            {
                int r = stack.RowCount;
                c.Dock = DockStyle.Fill;
                c.Margin = new Padding(0, 0, 0, 10);
                stack.Controls.Add(c, 0, r);
                stack.RowStyles.Add(new RowStyle(height < 0 ? SizeType.AutoSize : SizeType.Absolute, height < 0 ? 0 : height + 10));
                stack.RowCount = r + 1;
            }

            AddRow(new Label { Text = LT("Select a field, then add it to an area below.", "필드를 선택한 뒤 아래 영역에 배정하세요."), AutoSize = true, ForeColor = _palette.Text }, -1);

            // 필드 카드
            _fieldsList = new ListBox { BorderStyle = BorderStyle.None, IntegralHeight = false, BackColor = _palette.Surface, ForeColor = _palette.Text };
            foreach (var lbl in FieldLabels()) _fieldsList.Items.Add(lbl);
            _fieldsList.DoubleClick += (_, _) => AddSelectedTo(_rowDims, RefreshRows);
            AddRow(BuildCard(LT("Fields", "필드"), _palette.Accent, _fieldsList), 140);

            // 배정 버튼 행
            var assign = new FlowLayoutPanel { WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = _palette.Window };
            assign.Controls.Add(AssignButton(LT("＋ Rows", "＋ 행"), RowAccent, (_, _) => AddSelectedTo(_rowDims, RefreshRows)));
            assign.Controls.Add(AssignButton(LT("＋ Cols", "＋ 열"), ColAccent, (_, _) => AddSelectedTo(_colDims, RefreshCols)));
            assign.Controls.Add(AssignButton(LT("＋ Values", "＋ 값"), ValAccent, (_, _) => AddSelectedValue()));
            assign.Controls.Add(AssignButton(LT("＋ Filters", "＋ 필터"), FilAccent, (_, _) => AddSelectedFilter()));
            AddRow(assign, 36);

            // 영역 카드(칩 호스트)
            (Panel content, TableLayoutPanel table) MakeZone()
            {
                var content = new Panel { BackColor = _palette.Surface, AutoScroll = true };
                var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = _palette.Surface };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                content.Controls.Add(table);
                return (content, table);
            }

            var rowsZone = MakeZone(); _rowsTable = rowsZone.table;
            AddRow(BuildCard(LT("Rows", "행"), RowAccent, rowsZone.content), 92);
            var colsZone = MakeZone(); _colsTable = colsZone.table;
            AddRow(BuildCard(LT("Columns", "열"), ColAccent, colsZone.content), 92);
            var valuesZone = MakeZone(); _valuesTable = valuesZone.table;
            AddRow(BuildCard(LT("Values", "값"), ValAccent, valuesZone.content), 110);
            var filtersZone = MakeZone(); _filtersTable = filtersZone.table;
            AddRow(BuildCard(LT("Filters", "필터"), FilAccent, filtersZone.content), 92);

            // 푸터: 새로고침
            var footer = new FlowLayoutPanel { WrapContents = false, BackColor = _palette.Window };
            var refresh = AssignButton(LT("▶ Run", "▶ 실행"), _palette.Accent, async (_, _) => await RefreshResultAsync());
            refresh.BackColor = _palette.Accent; refresh.ForeColor = Color.White; refresh.FlatAppearance.BorderSize = 0;
            refresh.Width = 150; refresh.Height = 32;
            footer.Controls.Add(refresh);
            AddRow(footer, 40);

            // ── 결과(우) ──
            var rp = split.Panel2;
            rp.BackColor = _palette.Window;
            _resultTabs = new TabControl { Dock = DockStyle.Fill };
            var tabTable = new TabPage(LT("Table", "표")) { BackColor = _palette.Window };
            _resultGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                BorderStyle = BorderStyle.None,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            };
            tabTable.Controls.Add(_resultGrid);

            var tabChart = new TabPage(LT("Chart", "차트")) { BackColor = _palette.Window };
            var chartTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 7, 6, 4), BackColor = _palette.Window };
            _chartTypeCombo = MakeCombo(130);
            _chartTypeCombo.Items.AddRange(new object[] { LT("Bar", "막대"), LT("Grouped", "묶은 막대"), LT("Stacked", "누적 막대"), LT("Line", "꺾은선") });
            _chartTypeCombo.SelectedIndex = 0;
            _chartTypeCombo.SelectedIndexChanged += (_, _) => { _chart.Kind = (PivotChartKind)_chartTypeCombo.SelectedIndex; };
            _measureCombo = MakeCombo(230);
            _measureCombo.SelectedIndexChanged += (_, _) => RenderChart();
            chartTop.Controls.Add(new Label { Text = LT("Type", "종류"), AutoSize = true, Padding = new Padding(2, 8, 2, 0), ForeColor = _palette.Text });
            chartTop.Controls.Add(_chartTypeCombo);
            chartTop.Controls.Add(new Label { Text = LT("Measure", "측정값"), AutoSize = true, Padding = new Padding(14, 8, 2, 0), ForeColor = _palette.Text });
            chartTop.Controls.Add(_measureCombo);
            _chart = new ChartControl { Dock = DockStyle.Fill, BackColor = _palette.Surface };
            tabChart.Controls.Add(_chart);
            tabChart.Controls.Add(chartTop);

            _resultTabs.TabPages.Add(tabTable);
            _resultTabs.TabPages.Add(tabChart);
            rp.Controls.Add(_resultTabs);
        }

        // ---------------------------------------------------------------- 카드·버튼·콤보 헬퍼

        private ComboBox MakeCombo(int width = 0)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                DrawMode = DrawMode.OwnerDrawFixed,
                BackColor = _palette.Surface,
                ForeColor = _palette.Text,
            };
            c.DrawItem += (s, e) =>
            {
                var cb = (ComboBox)s!;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var bg = new SolidBrush(sel ? _palette.SelectionBg : _palette.Surface))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                if (e.Index >= 0)
                    TextRenderer.DrawText(e.Graphics, cb.Items[e.Index]?.ToString() ?? "", cb.Font, e.Bounds,
                        sel ? _palette.SelectionText : _palette.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            if (width > 0) c.Width = width;
            return c;
        }

        private Button AssignButton(string text, Color accent, EventHandler onClick)
        {
            var btn = new Button { Text = text, FlatStyle = FlatStyle.Flat, AutoSize = false, Width = 100, Height = 30, Margin = new Padding(0, 0, 5, 0), ForeColor = accent, BackColor = _palette.Surface, Font = new Font(Font, FontStyle.Bold) };
            btn.FlatAppearance.BorderColor = accent;
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.MouseOverBackColor = _palette.MenuHighlight;
            btn.Cursor = Cursors.Hand;
            btn.Click += onClick;
            return btn;
        }

        private Button ChipButton(string text, EventHandler onClick)
        {
            var btn = new Button { Text = text, Size = new Size(22, 20), FlatStyle = FlatStyle.Flat, Margin = new Padding(1, 4, 1, 0), ForeColor = _palette.Text, BackColor = ChipBg, TabStop = false };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = _palette.MenuHighlight;
            btn.Cursor = Cursors.Hand;
            btn.Click += onClick;
            return btn;
        }

        private Panel BuildCard(string title, Color accent, Control content)
        {
            var card = new Panel { BackColor = _palette.Surface };
            card.Paint += (s, e) =>
            {
                using var p = new Pen(_palette.Border);
                e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
            };

            var contentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 2, 8, 8), BackColor = _palette.Surface };
            content.Dock = DockStyle.Fill;
            contentHost.Controls.Add(content);

            var header = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = _palette.Surface };
            header.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = accent, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) });

            var accentBar = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = accent };

            card.Controls.Add(contentHost);
            card.Controls.Add(header);
            card.Controls.Add(accentBar);
            return card;
        }

        // ---------------------------------------------------------------- 칩(chip) 렌더링

        private FlowLayoutPanel ChipRightBar()
            => new() { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = ChipBg };

        private void AddChip(TableLayoutPanel table, string name, Color accent, FlowLayoutPanel right)
        {
            var chip = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4), BackColor = ChipBg };
            chip.Paint += (s, e) =>
            {
                using var p = new Pen(_palette.Border);
                e.Graphics.DrawRectangle(p, 0, 0, chip.Width - 1, chip.Height - 1);
                using var ab = new SolidBrush(accent);
                e.Graphics.FillRectangle(ab, 0, 0, 3, chip.Height);
            };
            var nameLbl = new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), ForeColor = _palette.Text, BackColor = ChipBg, AutoEllipsis = true };
            right.BackColor = ChipBg;
            chip.Controls.Add(nameLbl);
            chip.Controls.Add(right);

            int r = table.RowCount;
            table.Controls.Add(chip, 0, r);
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            table.RowCount = r + 1;
        }

        private static void ClearTable(TableLayoutPanel t)
        {
            t.SuspendLayout();
            var ctrls = t.Controls.Cast<Control>().ToArray();
            t.Controls.Clear();
            foreach (var c in ctrls) c.Dispose();
            t.RowStyles.Clear();
            t.RowCount = 0;
            t.ResumeLayout();
        }

        private ComboBox PeriodCombo(DateBinPeriod? current)
        {
            var c = MakeCombo(86);
            c.Margin = new Padding(2, 4, 2, 0);
            c.Items.AddRange(new object[] { LT("(raw)", "(원본)"), LT("Day", "일"), LT("Week", "주"), LT("Month", "월"), LT("Year", "년") });
            c.SelectedIndex = PeriodToIndex(current);
            return c;
        }

        private void RefreshDims(TableLayoutPanel table, List<DimItem> dims, Color accent, Action refresh)
        {
            ClearTable(table);
            for (int i = 0; i < dims.Count; i++)
            {
                int idx = i;
                var d = dims[i];
                var right = ChipRightBar();
                right.Controls.Add(ChipButton("✕", (_, _) => { dims.RemoveAt(idx); refresh(); }));
                right.Controls.Add(ChipButton("▾", (_, _) => { if (Swap(dims, idx, idx + 1)) refresh(); }));
                right.Controls.Add(ChipButton("▴", (_, _) => { if (Swap(dims, idx, idx - 1)) refresh(); }));
                if (IsDate(d.Field))
                {
                    var pc = PeriodCombo(d.Period);
                    pc.SelectedIndexChanged += (s, _) => d.Period = IndexToPeriod(pc.SelectedIndex);
                    right.Controls.Add(pc);
                }
                AddChip(table, ColName(d.Field), accent, right);
            }
        }

        private void RefreshValues()
        {
            ClearTable(_valuesTable);
            for (int i = 0; i < _measures.Count; i++)
            {
                int idx = i;
                var m = _measures[i];
                var right = ChipRightBar();
                right.Controls.Add(ChipButton("✕", (_, _) => { _measures.RemoveAt(idx); RefreshValues(); }));
                right.Controls.Add(ChipButton("▾", (_, _) => { if (Swap(_measures, idx, idx + 1)) RefreshValues(); }));
                right.Controls.Add(ChipButton("▴", (_, _) => { if (Swap(_measures, idx, idx - 1)) RefreshValues(); }));

                var allowed = AllowedFunctions(m.Field);
                var fc = MakeCombo(108);
                fc.Margin = new Padding(2, 4, 2, 0);
                foreach (var f in allowed) fc.Items.Add(f.DisplayName());
                int sel = Array.IndexOf(allowed, m.Func);
                if (sel < 0) { sel = 0; m.Func = allowed[0]; }
                fc.SelectedIndex = sel;
                fc.SelectedIndexChanged += (s, _) => m.Func = allowed[fc.SelectedIndex];
                right.Controls.Add(fc);

                AddChip(_valuesTable, ColName(m.Field), ValAccent, right);
            }
        }

        private void RefreshFilters()
        {
            ClearTable(_filtersTable);
            for (int i = 0; i < _filters.Count; i++)
            {
                int idx = i;
                var f = _filters[i];
                var right = ChipRightBar();
                right.Controls.Add(ChipButton("✕", (_, _) => { _filters.RemoveAt(idx); RefreshFilters(); }));

                var vc = MakeCombo(130);
                vc.Margin = new Padding(2, 4, 2, 0);
                foreach (var v in FilterValues(f.Col, f.Period)) vc.Items.Add(v);
                int vsel = vc.Items.IndexOf(f.Value);
                if (vsel < 0 && vc.Items.Count > 0) { vsel = 0; f.Value = vc.Items[0]?.ToString() ?? ""; }
                if (vsel >= 0) vc.SelectedIndex = vsel;
                vc.SelectedIndexChanged += (s, _) => f.Value = vc.SelectedItem?.ToString() ?? "";
                right.Controls.Add(vc);

                if (IsDate(f.Col))
                {
                    var pc = PeriodCombo(f.Period);
                    pc.SelectedIndexChanged += (s, _) => { f.Period = IndexToPeriod(pc.SelectedIndex); f.Value = ""; RefreshFilters(); };
                    right.Controls.Add(pc);
                }
                AddChip(_filtersTable, ColName(f.Col), FilAccent, right);
            }
        }

        private void RefreshRows() => RefreshDims(_rowsTable, _rowDims, RowAccent, RefreshRows);
        private void RefreshCols() => RefreshDims(_colsTable, _colDims, ColAccent, RefreshCols);

        private static int PeriodToIndex(DateBinPeriod? p) => p switch
        {
            DateBinPeriod.Day => 1,
            DateBinPeriod.Week => 2,
            DateBinPeriod.Month => 3,
            DateBinPeriod.Year => 4,
            _ => 0
        };

        private static DateBinPeriod? IndexToPeriod(int i) => i switch
        {
            1 => DateBinPeriod.Day,
            2 => DateBinPeriod.Week,
            3 => DateBinPeriod.Month,
            4 => DateBinPeriod.Year,
            _ => null
        };

        private AggregationFunction[] AllowedFunctions(int field)
            => IsNumeric(field)
                ? Enum.GetValues<AggregationFunction>()
                : new[] { AggregationFunction.Count, AggregationFunction.UniqueCount };

        private string[] FilterValues(int col, DateBinPeriod? period)
        {
            var dict = period is DateBinPeriod p ? new Dictionary<int, DateBinPeriod> { { col, p } } : new();
            return _rows.Select(r => CsvAnalytics.PivotKeyValue(r, col, dict))
                .Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
        }

        // ---------------------------------------------------------------- 필드 배정

        private string[] FieldLabels()
        {
            var labels = new string[_headers.Length];
            for (int i = 0; i < _headers.Length; i++) labels[i] = ColLabel(i);
            return labels;
        }

        private string ColName(int i) => i >= 0 && i < _headers.Length && _headers[i].Length > 0 ? _headers[i] : $"Column{i + 1}";
        private string ColLabel(int i) => i < _summaries.Length ? $"{ColName(i)}  [{_summaries[i].InferredType.DisplayName()}]" : ColName(i);
        private bool IsDate(int i) => i < _summaries.Length && _summaries[i].InferredType.HasDateComponent();
        private bool IsNumeric(int i) => i < _summaries.Length && (_summaries[i].InferredType == ColumnValueType.Integer || _summaries[i].InferredType == ColumnValueType.Float);

        private void AddSelectedTo(List<DimItem> dims, Action refresh)
        {
            int f = _fieldsList.SelectedIndex;
            if (f < 0 || dims.Any(d => d.Field == f)) return;
            dims.Add(new DimItem { Field = f, Period = IsDate(f) ? DateBinPeriod.Month : null });
            refresh();
        }

        private void AddSelectedValue()
        {
            int f = _fieldsList.SelectedIndex;
            if (f < 0) return;
            _measures.Add(new Measure { Field = f, Func = IsNumeric(f) ? AggregationFunction.Sum : AggregationFunction.Count });
            RefreshValues();
        }

        private void AddSelectedFilter()
        {
            int f = _fieldsList.SelectedIndex;
            if (f < 0) return;
            _filters.Add(new FilterItem { Col = f, Period = IsDate(f) ? DateBinPeriod.Month : null });
            RefreshFilters();
        }

        private static bool Swap<T>(List<T> list, int i, int j)
        {
            if (j < 0 || j >= list.Count) return false;
            (list[i], list[j]) = (list[j], list[i]);
            return true;
        }

        private string MeasureLabel(Measure m) => $"{m.Func.DisplayName()}({ColName(m.Field)})";

        // ---------------------------------------------------------------- 계산

        private int[] RowFields() => _rowDims.Select(d => d.Field).ToArray();
        private int[] ColFields() => _colDims.Select(d => d.Field).ToArray();
        private List<PivotFilter> Filters() => _filters.Select(f => new PivotFilter(f.Col, f.Value)).ToList();

        private Dictionary<int, DateBinPeriod> DateGroupings()
        {
            var dict = new Dictionary<int, DateBinPeriod>();
            foreach (var d in _rowDims.Concat(_colDims))
                if (d.Period is DateBinPeriod p) dict[d.Field] = p;
            foreach (var f in _filters)
                if (f.Period is DateBinPeriod p) dict[f.Col] = p;
            return dict;
        }

        private async Task RefreshResultAsync()
        {
            if (_measures.Count == 0)
            {
                MessageBox.Show(LT("Add at least one Value (measure).", "값(측정값)을 하나 이상 추가하세요."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var rowDims = RowFields();
            var colDims = ColFields();
            var measures = _measures.Select(m => (m.Field, m.Func)).ToArray();
            var filters = Filters();
            var groupings = DateGroupings();
            var rowNames = rowDims.Select(ColName).ToArray();

            Cursor = Cursors.WaitCursor;
            try
            {
                var results = await Task.Run(() =>
                {
                    var list = new List<PivotTableResult>(measures.Length);
                    foreach (var (field, func) in measures)
                        list.Add(CsvAnalytics.PivotTable(_rows, rowDims, colDims, field, func, rowNames, filters, groupings));
                    return list;
                });
                _results = results;
                ClearTotalCaches();
                RenderTable();
                UpdateMeasureCombo();
                RenderChart();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void InitializeComponent()
        {

        }

        private void RenderTable()
        {
            _resultGrid.SuspendLayout();
            _resultGrid.Columns.Clear();
            _resultGrid.Rows.Clear();

            if (_results.Count == 0) { _resultGrid.ResumeLayout(); return; }
            var first = _results[0];
            var rowKeys = first.RowKeys;
            var colKeys = first.ColumnKeys;
            bool hasCols = _colDims.Count > 0;

            foreach (var name in first.RowColumnNames)
                _resultGrid.Columns.Add("d_" + _resultGrid.Columns.Count, name);

            var valueColIndex = new List<(int measure, string[]? colKey, bool total)>();
            if (hasCols)
            {
                foreach (var ck in colKeys)
                    for (int m = 0; m < _results.Count; m++)
                    {
                        _resultGrid.Columns.Add("v" + _resultGrid.Columns.Count, $"{string.Join(" | ", ck)} · {MeasureLabel(_measures[m])}");
                        valueColIndex.Add((m, ck, false));
                    }
                for (int m = 0; m < _results.Count; m++)
                {
                    _resultGrid.Columns.Add("t" + _resultGrid.Columns.Count, $"{LT("Total", "합계")} · {MeasureLabel(_measures[m])}");
                    valueColIndex.Add((m, null, true));
                }
            }
            else
            {
                for (int m = 0; m < _results.Count; m++)
                {
                    _resultGrid.Columns.Add("v" + _resultGrid.Columns.Count, MeasureLabel(_measures[m]));
                    valueColIndex.Add((m, Array.Empty<string>(), false));
                }
            }

            int cap = Math.Min(rowKeys.Count, 5000);
            for (int ri = 0; ri < cap; ri++)
            {
                var rk = rowKeys[ri];
                var cells = new List<object>();
                cells.AddRange(rk.Cast<object>());
                foreach (var (m, ck, total) in valueColIndex)
                    cells.Add(Fmt(total ? RowTotal(m, rk) : _results[m].Value(rk, ck!)));
                _resultGrid.Rows.Add(cells.ToArray());
            }

            if (_rowDims.Count > 0)
            {
                var totalCells = new List<object>();
                for (int d = 0; d < _rowDims.Count; d++) totalCells.Add(d == 0 ? LT("Total", "합계") : "");
                foreach (var (m, ck, total) in valueColIndex)
                    totalCells.Add(Fmt(total ? GrandTotal(m) : ColTotal(m, ck!)));
                if (_resultGrid.Columns.Count == totalCells.Count)
                    _resultGrid.Rows.Add(totalCells.ToArray());
            }

            _resultGrid.ResumeLayout();
        }

        private readonly Dictionary<int, PivotTableResult> _rowTotalCache = new();
        private readonly Dictionary<int, PivotTableResult> _colTotalCache = new();
        private readonly Dictionary<int, double> _grandCache = new();

        private double RowTotal(int m, string[] rk)
        {
            if (!_rowTotalCache.TryGetValue(m, out var pt))
            {
                pt = CsvAnalytics.PivotTable(_rows, RowFields(), Array.Empty<int>(), _measures[m].Field, _measures[m].Func,
                    RowFields().Select(ColName).ToArray(), Filters(), DateGroupings());
                _rowTotalCache[m] = pt;
            }
            return pt.Value(rk, Array.Empty<string>());
        }

        private double ColTotal(int m, string[] ck)
        {
            if (!_colTotalCache.TryGetValue(m, out var pt))
            {
                pt = CsvAnalytics.PivotTable(_rows, Array.Empty<int>(), ColFields(), _measures[m].Field, _measures[m].Func,
                    Array.Empty<string>(), Filters(), DateGroupings());
                _colTotalCache[m] = pt;
            }
            return pt.Value(Array.Empty<string>(), ck);
        }

        private double GrandTotal(int m)
        {
            if (!_grandCache.TryGetValue(m, out double v))
            {
                var pt = CsvAnalytics.PivotTable(_rows, Array.Empty<int>(), Array.Empty<int>(), _measures[m].Field, _measures[m].Func,
                    Array.Empty<string>(), Filters(), DateGroupings());
                v = pt.Value(Array.Empty<string>(), Array.Empty<string>());
                _grandCache[m] = v;
            }
            return v;
        }

        private void ClearTotalCaches() { _rowTotalCache.Clear(); _colTotalCache.Clear(); _grandCache.Clear(); }

        private static string Fmt(double v)
            => v == Math.Truncate(v) && Math.Abs(v) < 1e15 ? v.ToString("#,##0", CultureInfo.InvariantCulture) : v.ToString("#,##0.###", CultureInfo.InvariantCulture);

        private void UpdateMeasureCombo()
        {
            _measureCombo.Items.Clear();
            foreach (var m in _measures) _measureCombo.Items.Add(MeasureLabel(m));
            if (_measureCombo.Items.Count > 0) _measureCombo.SelectedIndex = 0;
        }

        private void RenderChart()
        {
            if (_results.Count == 0) { _chart.SetData(Array.Empty<string>(), new(), _palette, "", ""); return; }
            int mi = Math.Max(0, _measureCombo.SelectedIndex);
            if (mi >= _results.Count) mi = 0;
            var pivot = _results[mi];
            bool hasCols = _colDims.Count > 0;
            string[] categories;
            var series = new List<ChartSeries>();
            string xTitle, yTitle = MeasureLabel(_measures[mi]);

            if (_rowDims.Count == 0)
            {
                if (!hasCols)
                {
                    categories = new[] { LT("Total", "합계") };
                    series.Add(new ChartSeries(yTitle, new[] { pivot.Value(Array.Empty<string>(), Array.Empty<string>()) }));
                }
                else
                {
                    categories = pivot.ColumnKeys.Select(ck => string.Join(" | ", ck)).ToArray();
                    series.Add(new ChartSeries(yTitle, pivot.ColumnKeys.Select(ck => pivot.Value(Array.Empty<string>(), ck)).ToArray()));
                }
                xTitle = hasCols ? LT("Columns", "열") : LT("Metric", "지표");
            }
            else
            {
                categories = pivot.RowKeys.Select(rk => string.Join(" | ", rk)).ToArray();
                var colKeys = hasCols ? pivot.ColumnKeys : new List<string[]> { Array.Empty<string>() };
                foreach (var ck in colKeys)
                {
                    string name = ck.Length == 0 ? yTitle : string.Join(" | ", ck);
                    series.Add(new ChartSeries(name, pivot.RowKeys.Select(rk => pivot.Value(rk, ck)).ToArray()));
                }
                xTitle = string.Join(" | ", _rowDims.Select(d => ColName(d.Field)));
            }

            if (LooksTemporal(categories) && _chartTypeCombo.SelectedIndex == 0)
                _chartTypeCombo.SelectedIndex = 3; // Line
            _chart.Kind = (PivotChartKind)_chartTypeCombo.SelectedIndex;
            _chart.SetData(categories, series, _palette, xTitle, yTitle);
        }

        private static bool LooksTemporal(string[] categories)
        {
            var nonNull = categories.Where(c => c.Length > 0 && c != "null").ToArray();
            if (nonNull.Length < 2) return false;
            return nonNull.All(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"^\d{4}(-\d{2}){0,2}$|^\d{4}-W\d{2}$"));
        }

        private void StyleResultGrid()
        {
            var g = _resultGrid; var p = _palette;
            g.EnableHeadersVisualStyles = false;
            g.BackgroundColor = p.GridBg;
            g.GridColor = p.Border;
            g.DefaultCellStyle.BackColor = p.GridBg;
            g.DefaultCellStyle.ForeColor = p.Text;
            g.DefaultCellStyle.SelectionBackColor = p.SelectionBg;
            g.DefaultCellStyle.SelectionForeColor = p.SelectionText;
            g.DefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
            g.AlternatingRowsDefaultCellStyle.BackColor = p.AltRow;
            g.AlternatingRowsDefaultCellStyle.ForeColor = p.Text;
            g.ColumnHeadersDefaultCellStyle.BackColor = p.HeaderBg;
            g.ColumnHeadersDefaultCellStyle.ForeColor = p.HeaderText;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.HeaderBg;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 2, 4, 2);
            g.RowTemplate.Height = 24;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ClearTotalCaches();
            base.OnFormClosed(e);
        }
    }
}
