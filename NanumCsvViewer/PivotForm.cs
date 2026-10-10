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
        private readonly IReadOnlyList<string[]> _rows;
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
        private Measure[] _resultMeasures = Array.Empty<Measure>();
        private NumericUpDown _rowPage = null!, _columnPage = null!;
        private Label _pageInfo = null!;
        private Label _chartPageInfo = null!;
        private readonly ToolTip _filterTip = new();
        private Button _cancelRun = null!;
        private Label _operationStatus = null!;
        private bool _paging;
        private CancellationTokenSource? _runCancellation;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<Task> _readerTasks = new();
        private bool _stoppingReaders;
        internal Task ReaderCompletion => Task.WhenAll(_readerTasks.ToArray());

        internal void CancelReaders()
        {
            _stoppingReaders = true;
            _runCancellation?.Cancel();
            if (!IsDisposed) _lifetime.Cancel();
        }

        private void TrackReader(Task reader)
        {
            _readerTasks.RemoveAll(task => task.IsCompleted);
            _readerTasks.Add(reader);
        }
        private const int RowsPerPage = 100;
        private int ColumnsPerPage => Math.Max(1, 40 / Math.Max(1, _results.Count));

        private static readonly Color RowAccent = Color.FromArgb(46, 111, 176);
        private static readonly Color ColAccent = Color.FromArgb(27, 158, 119);
        private static readonly Color ValAccent = Color.FromArgb(217, 95, 2);
        private static readonly Color FilAccent = Color.FromArgb(123, 94, 167);
        private Color ChipBg => _palette.AltRow;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        private readonly Form1? _host;

        /// <param name="host">예산 초과 안내에서 설정 대화 상자를 열 때 쓰는 주 창(null이면 안내만).</param>
        public PivotForm(string[] headers, ColumnSummary[] summaries, IReadOnlyList<string[]> rows, ThemePalette palette, AppTheme theme, Form1? host = null)
        {
            _host = host;
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
            _cancelRun = new Button { Text = LT("Cancel calculation", "계산 취소"), Dock = DockStyle.Bottom, Height = 30, Visible = false };
            _cancelRun.Click += (_, _) => _runCancellation?.Cancel();
            Controls.Add(_cancelRun);
            _operationStatus = new Label { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8),
                ForeColor = _palette.Text, BackColor = _palette.Surface, Visible = false };
            Controls.Add(_operationStatus);
            FormClosing += (_, _) => CancelReaders();
            Disposed += (_, _) => { _runCancellation?.Cancel(); _lifetime.Cancel(); _lifetime.Dispose(); _filterTip.Dispose(); };
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
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            };
            tabTable.Controls.Add(_resultGrid);
            var pages = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, AutoScroll = true };
            _rowPage = new NumericUpDown { Minimum = 1, Maximum = 1, Value = 1, Width = 75 };
            _columnPage = new NumericUpDown { Minimum = 1, Maximum = 1, Value = 1, Width = 75 };
            _pageInfo = new Label { AutoSize = true };
            pages.Controls.Add(new Label { Text = LT("Row page", "행 페이지"), AutoSize = true });
            pages.Controls.Add(_rowPage);
            pages.Controls.Add(new Label { Text = LT("Column page", "열 페이지"), AutoSize = true });
            pages.Controls.Add(_columnPage);
            pages.Controls.Add(_pageInfo);
            tabTable.Controls.Add(pages);
            _rowPage.ValueChanged += (_, _) => { if (!_paging) { RenderTable(); RenderChart(); } };
            _columnPage.ValueChanged += (_, _) => { if (!_paging) { RenderTable(); RenderChart(); } };

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
            _chartPageInfo = new Label { Dock = DockStyle.Bottom, Height = 38, ForeColor = _palette.Text };
            tabChart.Controls.Add(_chartPageInfo);

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
                vc.DropDownStyle = ComboBoxStyle.DropDown;
                vc.Text = f.Value;
                vc.TextChanged += (_, _) => f.Value = vc.Text;
                _filterTip.SetToolTip(vc, LT("Type an exact value, or choose a suggestion (up to 500). Empty = no filter; null = missing values.",
                    "값을 직접 입력하거나 추천 값(최대 500개)을 선택하세요. 공란은 필터 없음, null은 결측값입니다."));
                _ = LoadFilterValuesAsync(vc, f.Col, f.Period);
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

        private async Task LoadFilterValuesAsync(ComboBox combo, int col, DateBinPeriod? period)
        {
            if (_stoppingReaders || IsDisposed) return;
            var dict = period is DateBinPeriod p ? new Dictionary<int, DateBinPeriod> { { col, p } } : new();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            void Cancel(object? sender, EventArgs e) => cancellation.Cancel();
            combo.Disposed += Cancel;
            try
            {
                var reader = Task.Run(() =>
                {
                    var candidates = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < _rows.Count && candidates.Count < 500; i++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        string value = CsvAnalytics.PivotKeyValue(_rows[i], col, dict);
                        if (value.Length <= 300) candidates.Add(value);
                    }
                    return candidates.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
                }, cancellation.Token);
                TrackReader(reader);
                var values = await reader;
                if (combo.IsDisposed || IsDisposed || cancellation.IsCancellationRequested) return;
                combo.Items.AddRange(values);
                // Suggestions are bounded; arbitrary exact values remain editable.
                combo.AccessibleDescription = LT("Suggestions only. Type any exact value.", "추천 목록입니다. 원하는 값을 직접 입력할 수 있습니다.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!combo.IsDisposed) combo.AccessibleDescription = ex.Message;
            }
            finally { combo.Disposed -= Cancel; }
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
            if (_measures.Count >= 32)
            {
                MessageBox.Show(this, LT("Use up to 32 measures per pivot. Open another pivot for additional measures.",
                    "피벗 하나에 측정값을 최대 32개 사용할 수 있습니다. 추가 측정값은 별도 피벗에서 확인하세요."), Text);
                return;
            }
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
        private List<PivotFilter> Filters() => _filters.Select(f => new PivotFilter(f.Col, f.Value.Length == 0 ? null : f.Value)).ToList();

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
            if (_stoppingReaders || IsDisposed) return;
            if (_runCancellation is not null) return;
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
            _operationStatus.Text = "";
            _operationStatus.Visible = false;
            using var cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            foreach (Control control in Controls) if (control != _cancelRun) control.Enabled = false;
            _cancelRun.Visible = true;
            try
            {
                // 예산 읽기는 남길 메모리 정책에서 예외를 던질 수 있어 try 안에서 읽는다. 측정값 수로 나눈 몫이 0이어도 남는 양이 없는 것이다.
                long memoryBudget = AnalysisMemoryBudget.Current / (measures.Length * 4L);
                if (memoryBudget <= 0) throw new AnalysisMemoryLimitException();
                var reader = Task.Run(() =>
                {
                    var list = new List<PivotTableResult>(measures.Length);
                    var rowTotals = new Dictionary<int, PivotTableResult>();
                    var colTotals = new Dictionary<int, PivotTableResult>();
                    var grandTotals = new Dictionary<int, double>();
                    foreach (var (field, func) in measures)
                    {
                        int m = list.Count;
                        list.Add(CsvAnalytics.PivotTable(_rows, rowDims, colDims, field, func, rowNames, filters, groupings, cancellation.Token, memoryBudget));
                        rowTotals[m] = colDims.Length == 0 ? list[m] : CsvAnalytics.PivotTable(_rows, rowDims, Array.Empty<int>(), field, func, rowNames, filters, groupings, cancellation.Token, memoryBudget);
                        colTotals[m] = rowDims.Length == 0 ? list[m] : CsvAnalytics.PivotTable(_rows, Array.Empty<int>(), colDims, field, func, null, filters, groupings, cancellation.Token, memoryBudget);
                        grandTotals[m] = CsvAnalytics.PivotTable(_rows, Array.Empty<int>(), Array.Empty<int>(), field, func, null, filters, groupings, cancellation.Token, memoryBudget).Value(Array.Empty<string>(), Array.Empty<string>());
                    }
                    return (list, rowTotals, colTotals, grandTotals);
                });
                TrackReader(reader);
                var results = await reader;
                if (IsDisposed || cancellation.IsCancellationRequested) return;
                _results = results.list;
                _resultMeasures = measures.Select(m => new Measure { Field = m.Field, Func = m.Func }).ToArray();
                ClearTotalCaches();
                foreach (var pair in results.rowTotals) _rowTotalCache.Add(pair.Key, pair.Value);
                foreach (var pair in results.colTotals) _colTotalCache.Add(pair.Key, pair.Value);
                foreach (var pair in results.grandTotals) _grandCache.Add(pair.Key, pair.Value);
                _paging = true;
                _rowPage.Value = _columnPage.Value = 1;
                _rowPage.Maximum = Math.Max(1, (_results[0].RowKeys.Count + RowsPerPage - 1) / RowsPerPage);
                _columnPage.Maximum = Math.Max(1, (_results[0].ColumnKeys.Count + ColumnsPerPage - 1) / ColumnsPerPage);
                _paging = false;
                RenderTable();
                UpdateMeasureCombo();
                RenderChart();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is PivotMemoryLimitException or AnalysisMemoryLimitException)
            {
                string message = LT("This pivot exceeds the memory budget. Filter the rows, group dates, or reduce dimensions/measures. The previous result is preserved.",
                    "피벗의 예상 메모리가 예산을 초과했습니다. 행 필터·날짜 그룹을 적용하거나 차원·측정값을 줄여 주세요. 이전 결과는 유지됩니다.");
                ShowOperationError(message);
                // 컨트롤을 다시 켠 뒤(finally 다음) 안내한다. 이 대화 상자 위에서 설정을 연다.
                if (_host is not null && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) _host.ShowMemoryBudgetExceeded(this, message, Text); });
            }
            catch (Exception ex)
            {
                ShowOperationError(ex.Message);
            }
            finally
            {
                _runCancellation = null;
                if (!IsDisposed)
                {
                    Cursor = Cursors.Default;
                    _cancelRun.Visible = false;
                    foreach (Control control in Controls) control.Enabled = true;
                }
            }
        }

        private void ShowOperationError(string message)
        {
            if (IsDisposed) return;
            _operationStatus.Text = message;
            _operationStatus.Visible = true;
        }

        private void InitializeComponent()
        {

        }

        private void RenderTable()
        {
            _resultGrid.SuspendLayout();
            try
            {
                _resultGrid.Columns.Clear();
                _resultGrid.Rows.Clear();

                if (_results.Count == 0) return;
                var first = _results[0];
                var rowKeys = first.RowKeys.Skip(((int)_rowPage.Value - 1) * RowsPerPage).Take(RowsPerPage).ToArray();
                var colKeys = first.ColumnKeys.Skip(((int)_columnPage.Value - 1) * ColumnsPerPage).Take(ColumnsPerPage).ToArray();
                _pageInfo.Text = LT($"{first.RowKeys.Count:N0} rows / {first.ColumnKeys.Count:N0} column groups. Chart: current page.", $"전체 {first.RowKeys.Count:N0}행 / {first.ColumnKeys.Count:N0}열 그룹. 차트: 현재 페이지.");
                bool hasCols = first.ColumnColumns.Count > 0;

                foreach (var name in first.RowColumnNames)
                    _resultGrid.Columns.Add("d_" + _resultGrid.Columns.Count, name);

                var valueColIndex = new List<(int measure, string[]? colKey, bool total)>();
                if (hasCols)
                {
                    foreach (var ck in colKeys)
                        for (int m = 0; m < _results.Count; m++)
                        {
                            _resultGrid.Columns.Add("v" + _resultGrid.Columns.Count, $"{string.Join(" | ", ck)} · {MeasureLabel(_resultMeasures[m])}");
                            valueColIndex.Add((m, ck, false));
                        }
                    for (int m = 0; m < _results.Count; m++)
                    {
                        _resultGrid.Columns.Add("t" + _resultGrid.Columns.Count, $"{LT("Total", "합계")} · {MeasureLabel(_resultMeasures[m])}");
                        valueColIndex.Add((m, null, true));
                    }
                }
                else
                {
                    for (int m = 0; m < _results.Count; m++)
                    {
                        _resultGrid.Columns.Add("v" + _resultGrid.Columns.Count, MeasureLabel(_resultMeasures[m]));
                        valueColIndex.Add((m, Array.Empty<string>(), false));
                    }
                }

                int cap = rowKeys.Length;
                for (int ri = 0; ri < cap; ri++)
                {
                    var rk = rowKeys[ri];
                    var cells = new List<object>();
                    cells.AddRange(rk.Cast<object>());
                    foreach (var (m, ck, total) in valueColIndex)
                        cells.Add(Fmt(total ? RowTotal(m, rk) : _results[m].Value(rk, ck!)));
                    _resultGrid.Rows.Add(cells.ToArray());
                }

                if (first.RowColumns.Count > 0)
                {
                    var totalCells = new List<object>();
                    for (int d = 0; d < first.RowColumns.Count; d++) totalCells.Add(d == 0 ? LT("Total", "합계") : "");
                    foreach (var (m, ck, total) in valueColIndex)
                        totalCells.Add(Fmt(total ? GrandTotal(m) : ColTotal(m, ck!)));
                    if (_resultGrid.Columns.Count == totalCells.Count)
                        _resultGrid.Rows.Add(totalCells.ToArray());
                }

            }
            finally { _resultGrid.ResumeLayout(); }
        }

        private readonly Dictionary<int, PivotTableResult> _rowTotalCache = new();
        private readonly Dictionary<int, PivotTableResult> _colTotalCache = new();
        private readonly Dictionary<int, double> _grandCache = new();

        private double RowTotal(int m, string[] rk) => _rowTotalCache[m].Value(rk, Array.Empty<string>());
        private double ColTotal(int m, string[] ck) => _colTotalCache[m].Value(Array.Empty<string>(), ck);
        private double GrandTotal(int m) => _grandCache[m];

        private void ClearTotalCaches() { _rowTotalCache.Clear(); _colTotalCache.Clear(); _grandCache.Clear(); }

        private static string Fmt(double v)
            => v == Math.Truncate(v) && Math.Abs(v) < 1e15 ? v.ToString("#,##0", CultureInfo.InvariantCulture) : v.ToString("#,##0.###", CultureInfo.InvariantCulture);

        private void UpdateMeasureCombo()
        {
            _measureCombo.Items.Clear();
            foreach (var m in _resultMeasures) _measureCombo.Items.Add(MeasureLabel(m));
            if (_measureCombo.Items.Count > 0) _measureCombo.SelectedIndex = 0;
        }

        private void RenderChart()
        {
            if (_results.Count == 0) { _chart.SetData(Array.Empty<string>(), new(), _palette, "", ""); return; }
            int mi = Math.Max(0, _measureCombo.SelectedIndex);
            if (mi >= _results.Count) mi = 0;
            var pivot = _results[mi];
            _chartPageInfo.Text = LT($"Chart: row page {_rowPage.Value}/{_rowPage.Maximum}, column page {_columnPage.Value}/{_columnPage.Maximum}. Change pages in the Table tab.",
                $"차트: 행 {_rowPage.Value}/{_rowPage.Maximum}페이지, 열 {_columnPage.Value}/{_columnPage.Maximum}페이지. 표 탭에서 페이지를 변경하세요.");
            bool hasCols = pivot.ColumnColumns.Count > 0;
            var chartRows = pivot.RowKeys.Skip(((int)_rowPage.Value - 1) * RowsPerPage).Take(RowsPerPage).ToArray();
            var chartColumns = pivot.ColumnKeys.Skip(((int)_columnPage.Value - 1) * ColumnsPerPage).Take(ColumnsPerPage).ToArray();
            string[] categories;
            var series = new List<ChartSeries>();
            string xTitle, yTitle = MeasureLabel(_resultMeasures[mi]);

            if (pivot.RowColumns.Count == 0)
            {
                if (!hasCols)
                {
                    categories = new[] { LT("Total", "합계") };
                    series.Add(new ChartSeries(yTitle, new[] { pivot.Value(Array.Empty<string>(), Array.Empty<string>()) }));
                }
                else
                {
                    categories = chartColumns.Select(ck => string.Join(" | ", ck)).ToArray();
                    series.Add(new ChartSeries(yTitle, chartColumns.Select(ck => pivot.Value(Array.Empty<string>(), ck)).ToArray()));
                }
                xTitle = hasCols ? LT("Columns", "열") : LT("Metric", "지표");
            }
            else
            {
                categories = chartRows.Select(rk => string.Join(" | ", rk)).ToArray();
                var colKeys = hasCols ? chartColumns : new[] { Array.Empty<string>() };
                foreach (var ck in colKeys)
                {
                    string name = ck.Length == 0 ? yTitle : string.Join(" | ", ck);
                    series.Add(new ChartSeries(name, chartRows.Select(rk => pivot.Value(rk, ck)).ToArray()));
                }
                xTitle = string.Join(" | ", pivot.RowColumnNames);
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
