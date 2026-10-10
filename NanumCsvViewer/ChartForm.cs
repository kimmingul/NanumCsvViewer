using NanumCsvViewer.Charting;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    internal enum ChartKind { Histogram, BoxPlot, Scatter, CorrelationHeatmap, QqPlot, TimeSeries, Pareto }

    /// <summary>차트 빌더가 쓰는 데이터·환경 컨텍스트. Form1이 만들어 넘긴다(뷰 스냅샷 공유 — 복제 없음).</summary>
    internal sealed class ChartContext
    {
        public required List<string[]> Rows { get; set; }
        public required string[] ColumnNames { get; init; }
        public required ColumnSummary[] Summaries { get; init; }
        public required ThemePalette Palette { get; init; }
        /// <summary>현재(필터·정렬) 뷰를 다시 수집(새로고침 버튼).</summary>
        public Func<Task<AnalysisSnapshot?>>? RefreshRowsAsync { get; init; }
        /// <summary>새 차트 창 열기(드릴다운) — Form1이 생성·수명 관리. 자기참조 클로저라 생성 후 주입.</summary>
        public Action<ChartKind, int[]?>? OpenChart { get; set; }
        /// <summary>예산 초과 안내에서 설정의 분석 메모리 상한 쪽을 연다(Form1이 주입).</summary>
        public Action? OpenMemorySettings { get; init; }
    }

    /// <summary>
    /// 시각화 차트 빌더(이슈 #19). 모델리스·다중 인스턴스 — 차트 종류 전환이 1클릭인 탐색형 창.
    /// 좌측: 종류 + 필드 슬롯(종류별 선언 테이블) + 옵션. 우측: 통계 배지 스트립 + PlotControl.
    /// 설계는 Fable5·Codex·Grok 3자 논쟁 합의안(.omc/artifacts/viz-debate/).
    /// </summary>
    internal sealed class ChartForm : Form
    {
        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        // 종류별 필드 슬롯 선언(라벨·타입 필터·선택 여부) — if/else 7벌 대신 데이터 주도(논쟁 합의).
        private enum SlotFilter { Numeric, Date, Any }
        private sealed record SlotSpec(string En, string Ko, SlotFilter Filter, bool Optional = false);

        private static readonly Dictionary<ChartKind, SlotSpec[]> Slots = new()
        {
            [ChartKind.Histogram] = new[] { new SlotSpec("Value", "값", SlotFilter.Numeric) },
            [ChartKind.BoxPlot] = new[]
            {
                new SlotSpec("Value", "값", SlotFilter.Numeric),
                new SlotSpec("Group (optional)", "그룹(선택)", SlotFilter.Any, Optional: true),
            },
            [ChartKind.Scatter] = new[]
            {
                new SlotSpec("X", "X", SlotFilter.Numeric),
                new SlotSpec("Y", "Y", SlotFilter.Numeric),
            },
            [ChartKind.CorrelationHeatmap] = Array.Empty<SlotSpec>(), // 다중 선택(체크리스트) 특례
            [ChartKind.QqPlot] = new[] { new SlotSpec("Value", "값", SlotFilter.Numeric) },
            [ChartKind.TimeSeries] = new[]
            {
                new SlotSpec("Date", "날짜", SlotFilter.Date),
                new SlotSpec("Value (optional, sum)", "값(선택, 합계)", SlotFilter.Numeric, Optional: true),
            },
            [ChartKind.Pareto] = new[] { new SlotSpec("Column", "컬럼", SlotFilter.Any) },
        };

        private readonly ChartContext _ctx;
        private ChartKind _kind;

        private readonly ListBox _kindList = new();
        private readonly Panel _slotPanel = new();
        private readonly List<ComboBox> _slotCombos = new();
        private readonly CheckedListBox _heatmapColumns = new();
        private readonly Panel _optionsPanel = new();
        private readonly FlowLayoutPanel _badgeStrip = new();
        private readonly PlotControl _plot = new();
        private int[] _heatmapColIndexes = Array.Empty<int>(); // 드릴다운 역참조용

        // 옵션 컨트롤(종류별 표시 전환)
        private readonly NumericUpDown _binsInput = new() { Minimum = 0, Maximum = 100, Value = 0 };
        private readonly CheckBox _normalCheck = new() { Checked = true };
        private readonly CheckBox _kdeCheck = new() { Checked = true };
        private readonly CheckBox _regressionCheck = new() { Checked = true };
        private readonly ComboBox _periodCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown _maInput = new() { Minimum = 0, Maximum = 365, Value = 3 };

        private bool _building; // 컨트롤 재구성 중 이벤트 억제

        public ChartForm(ChartContext ctx, ChartKind kind, int[]? presetCols = null)
        {
            _ctx = ctx;
            _kind = kind;

            Text = LT("Chart Builder", "차트 빌더");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(980, 640);
            MinimumSize = new Size(720, 480);
            BackColor = ctx.Palette.Window;
            ForeColor = ctx.Palette.Text;
            KeyPreview = true;
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            BuildLayout();
            // 초기 선택은 핸들러를 억제하고 반영 — BuildLayout이 이미 _kind 기준으로 슬롯/옵션을 구성했으므로
            // 핸들러가 돌면 프리셋 적용 전에 기본 컬럼으로 전체 빌드가 한 번 낭비된다(2M행 이중 블로킹 방지).
            _building = true;
            _kindList.SelectedIndex = (int)kind;
            _building = false;
            Text = LT("Chart Builder", "차트 빌더") + " — " + _kindList.SelectedItem;
            ApplyPreset(presetCols);
            RebuildModel();
        }

        // ---------------------------------------------------------------- 레이아웃

        private static readonly (string En, string Ko)[] KindNames =
        {
            ("Histogram · Density", "히스토그램·밀도"),
            ("Box Plot (groups)", "박스플롯(그룹)"),
            ("Scatter · Regression", "산점도·회귀"),
            ("Correlation Heatmap", "상관 히트맵"),
            ("Q-Q Plot", "Q-Q 플롯"),
            ("Time Series", "시계열"),
            ("Pareto", "파레토"),
        };

        private void BuildLayout()
        {
            var p = _ctx.Palette;

            var left = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = p.Window, Padding = new Padding(8) };
            Controls.Add(left);

            // 우측: 배지 스트립(상단) + 플롯(중앙) + 버튼(하단)
            var right = new Panel { Dock = DockStyle.Fill, BackColor = p.Window };
            Controls.Add(right);
            right.BringToFront();

            _badgeStrip.Dock = DockStyle.Top;
            _badgeStrip.Height = 30;
            _badgeStrip.BackColor = p.Window;
            _badgeStrip.Padding = new Padding(4, 3, 4, 0);
            right.Controls.Add(_badgeStrip);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4, 3, 8, 3) };
            right.Controls.Add(bottom);
            bottom.Controls.Add(MakeButton(LT("Copy", "복사"), (_, _) => CopyToClipboard()));
            bottom.Controls.Add(MakeButton(LT("Save PNG…", "PNG 저장…"), (_, _) => SavePng()));
            if (_ctx.RefreshRowsAsync is not null)
                bottom.Controls.Add(MakeButton(LT("Refresh from View", "현재 뷰로 새로고침"), (_, _) => RefreshFromView()));

            _plot.Dock = DockStyle.Fill;
            _plot.BackColor = p.Surface;
            _plot.HeatCellClicked += OnHeatCellClicked;
            right.Controls.Add(_plot);
            _plot.BringToFront();

            // 좌측 구성: 종류 리스트 → 슬롯 → 옵션
            var kindLabel = MakeCaption(LT("Chart Type", "차트 종류"));
            kindLabel.Dock = DockStyle.Top;
            _kindList.Dock = DockStyle.Top;
            _kindList.Height = 128;
            _kindList.BorderStyle = BorderStyle.FixedSingle;
            _kindList.BackColor = p.Surface;
            _kindList.ForeColor = p.Text;
            foreach (var (en, ko) in KindNames) _kindList.Items.Add(LT(en, ko));
            _kindList.SelectedIndexChanged += (_, _) =>
            {
                if (_building || _kindList.SelectedIndex < 0) return;
                _kind = (ChartKind)_kindList.SelectedIndex;
                Text = LT("Chart Builder", "차트 빌더") + " — " + _kindList.SelectedItem;
                BuildSlots(preserve: true);
                BuildOptions();
                RebuildModel();
            };

            _slotPanel.Dock = DockStyle.Top;
            _slotPanel.AutoSize = false;
            _slotPanel.Height = 200;
            _optionsPanel.Dock = DockStyle.Fill;

            left.Controls.Add(_optionsPanel);
            left.Controls.Add(_slotPanel);
            left.Controls.Add(_kindList);
            left.Controls.Add(kindLabel);

            // 옵션 공통 준비
            foreach (string period in new[] { LT("Day", "일"), LT("Week", "주"), LT("Month", "월"), LT("Year", "년") })
                _periodCombo.Items.Add(period);
            _periodCombo.SelectedIndex = 2;
            foreach (Control c in new Control[] { _binsInput, _maInput }) StyleInput(c);
            StyleInput(_periodCombo);
            _binsInput.ValueChanged += OnOptionChanged;
            _normalCheck.CheckedChanged += OnOptionChanged;
            _kdeCheck.CheckedChanged += OnOptionChanged;
            _regressionCheck.CheckedChanged += OnOptionChanged;
            _periodCombo.SelectedIndexChanged += OnOptionChanged;
            _maInput.ValueChanged += OnOptionChanged;

            _heatmapColumns.BorderStyle = BorderStyle.FixedSingle;
            _heatmapColumns.CheckOnClick = true;
            _heatmapColumns.BackColor = p.Surface;
            _heatmapColumns.ForeColor = p.Text;
            _heatmapColumns.ItemCheck += (_, _) => BeginInvoke(RebuildModel);

            BuildSlots(preserve: false);
            BuildOptions();
        }

        private void OnOptionChanged(object? sender, EventArgs e)
        {
            if (!_building) RebuildModel();
        }

        private Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = _ctx.Palette.Surface,
                ForeColor = _ctx.Palette.Text,
            };
            b.FlatAppearance.BorderColor = _ctx.Palette.Border;
            b.Click += onClick;
            return b;
        }

        private Label MakeCaption(string text) => new()
        {
            Text = text,
            AutoSize = false,
            Height = 20,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = _ctx.Palette.Text,
        };

        private void StyleInput(Control c)
        {
            c.BackColor = _ctx.Palette.Surface;
            c.ForeColor = _ctx.Palette.Text;
        }

        // ---------------------------------------------------------------- 슬롯(종류별 컬럼 선택)

        private void BuildSlots(bool preserve)
        {
            _building = true;
            var previous = _slotCombos.Select(c => c.SelectedIndex >= 0 ? ColIndexOf(c) : -1).ToArray();
            _slotPanel.Controls.Clear();
            _slotCombos.Clear();

            if (_kind == ChartKind.CorrelationHeatmap)
            {
                // 특례: 수치 컬럼 다중 선택
                var caption = MakeCaption(LT("Numeric columns", "수치 컬럼(2개 이상)"));
                caption.Dock = DockStyle.Top;
                _heatmapColumns.Items.Clear();
                for (int c = 0; c < _ctx.ColumnNames.Length; c++)
                {
                    if (!IsNumericCol(c)) continue;
                    int idx = _heatmapColumns.Items.Add(new ColItem(c, _ctx.ColumnNames[c]));
                    if (_heatmapColumns.Items.Count <= 8) _heatmapColumns.SetItemChecked(idx, true);
                }
                _heatmapColumns.Dock = DockStyle.Fill;
                _slotPanel.Controls.Add(_heatmapColumns);
                _slotPanel.Controls.Add(caption);
                _building = false;
                return;
            }

            // Dock=Top은 나중에 추가된 것이 위로 가므로 역순으로 쌓는다.
            var specs = Slots[_kind];
            foreach (var spec in specs.Reverse())
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
                StyleInput(combo);
                if (spec.Optional) combo.Items.Add(new ColItem(-1, LT("(none)", "(없음)")));
                for (int c = 0; c < _ctx.ColumnNames.Length; c++)
                    if (MatchesFilter(c, spec.Filter))
                        combo.Items.Add(new ColItem(c, _ctx.ColumnNames[c]));
                if (combo.Items.Count > 0) combo.SelectedIndex = spec.Optional && combo.Items.Count > 1 ? 1 : 0;
                combo.SelectedIndexChanged += (_, _) => { if (!_building) RebuildModel(); };

                var caption = MakeCaption(LT(spec.En, spec.Ko));
                caption.Dock = DockStyle.Top;
                _slotPanel.Controls.Add(combo);
                _slotPanel.Controls.Add(caption);
                _slotCombos.Insert(0, combo);
            }

            // 이전 선택 보존(같은 컬럼이 새 슬롯 필터에도 맞으면 유지 — 종류 전환 1클릭 UX)
            if (preserve)
            {
                for (int i = 0; i < _slotCombos.Count && i < previous.Length; i++)
                {
                    if (previous[i] < 0) continue;
                    SelectCol(_slotCombos[i], previous[i]);
                }
            }
            _building = false;
        }

        private sealed record ColItem(int Index, string Name)
        {
            public override string ToString() => Name;
        }

        private static int ColIndexOf(ComboBox combo)
            => combo.SelectedItem is ColItem it ? it.Index : -1;

        private static void SelectCol(ComboBox combo, int colIndex)
        {
            for (int i = 0; i < combo.Items.Count; i++)
                if (combo.Items[i] is ColItem it && it.Index == colIndex) { combo.SelectedIndex = i; return; }
        }

        private bool IsNumericCol(int c)
            => c < _ctx.Summaries.Length ? _ctx.Summaries[c].InferredType.IsNumeric() : true;

        private bool MatchesFilter(int c, SlotFilter filter)
        {
            if (c >= _ctx.Summaries.Length) return true; // 요약 없으면 전부 허용
            var t = _ctx.Summaries[c].InferredType;
            return filter switch
            {
                SlotFilter.Numeric => t.IsNumeric(),
                SlotFilter.Date => t.HasDateComponent(),
                _ => t != ColumnValueType.Empty,
            };
        }

        private void ApplyPreset(int[]? presetCols)
        {
            if (presetCols is null) return;
            _building = true;
            if (_kind == ChartKind.CorrelationHeatmap)
            {
                for (int i = 0; i < _heatmapColumns.Items.Count; i++)
                    _heatmapColumns.SetItemChecked(i,
                        _heatmapColumns.Items[i] is ColItem it && presetCols.Contains(it.Index));
            }
            else
            {
                for (int i = 0; i < presetCols.Length && i < _slotCombos.Count; i++)
                    SelectCol(_slotCombos[i], presetCols[i]);
            }
            _building = false;
        }

        // ---------------------------------------------------------------- 옵션(종류별)

        private void BuildOptions()
        {
            _building = true;
            _optionsPanel.Controls.Clear();
            var rows = new List<(string Caption, Control Input)>();
            switch (_kind)
            {
                case ChartKind.Histogram:
                    _normalCheck.Text = LT("Normal curve", "정규곡선");
                    _kdeCheck.Text = LT("KDE density", "KDE 밀도곡선");
                    rows.Add((LT("Bins (0=auto)", "구간 수(0=자동)"), _binsInput));
                    rows.Add(("", _normalCheck));
                    rows.Add(("", _kdeCheck));
                    break;
                case ChartKind.Scatter:
                    _regressionCheck.Text = LT("Regression line", "회귀선");
                    rows.Add(("", _regressionCheck));
                    break;
                case ChartKind.TimeSeries:
                    rows.Add((LT("Period", "주기"), _periodCombo));
                    rows.Add((LT("Moving avg (0=off)", "이동평균(0=끔)"), _maInput));
                    break;
            }
            foreach (var (caption, input) in Enumerable.Reverse(rows))
            {
                input.Dock = DockStyle.Top;
                if (input is CheckBox cb) cb.ForeColor = _ctx.Palette.Text;
                _optionsPanel.Controls.Add(input);
                if (caption.Length > 0)
                {
                    var lab = MakeCaption(caption);
                    lab.Dock = DockStyle.Top;
                    _optionsPanel.Controls.Add(lab);
                }
            }
            _building = false;
        }

        // ---------------------------------------------------------------- 모델 구축

        private Func<PlotModel?>? _pendingModel;
        private Task? _modelTask;

        private void RebuildModel()
        {
            if (_building || IsDisposed) return;
            _pendingModel = CaptureModelBuilder();
            if (_modelTask is { IsCompleted: false }) return;
            _modelTask = BuildModelsAsync();
        }

        private async Task BuildModelsAsync()
        {
            Cursor = Cursors.WaitCursor;
            _plot.Enabled = false;
            try
            {
                while (_pendingModel is { } build && !IsDisposed)
                {
                    _pendingModel = null;
                    try
                    {
                        var model = await Task.Run(build);
                        if (IsDisposed || _pendingModel is not null) continue;
                        _plot.SetModel(model, _ctx.Palette);
                        RebuildBadges(model);
                    }
                    catch (Exception ex)
                    {
                        if (IsDisposed || _pendingModel is not null) continue;
                        _plot.SetModel(null, _ctx.Palette);
                        RebuildBadges(null);
                        bool overBudget = ex is AnalysisMemoryLimitException;
                        _badgeStrip.Controls.Add(new Label { AutoSize = true,
                            Text = overBudget
                                ? LT("This chart exceeds the calculation or display budget. Filter rows or select fewer columns (up to 128 for a heatmap). In Settings, raise the cap or reduce the memory to keep free.",
                                    "차트의 계산·표시 예산을 초과했습니다. 행 필터를 적용하거나 컬럼 수를 줄이세요(히트맵 최대 128개). 설정에서 상한을 올리거나 남길 메모리를 줄일 수 있습니다.")
                                : ex.Message, ForeColor = _ctx.Palette.Text });
                        if (overBudget && _ctx.OpenMemorySettings is { } openSettings)
                        {
                            var link = new LinkLabel { AutoSize = true, Text = LT("Open Settings", "설정 열기"),
                                LinkColor = _ctx.Palette.Text, ActiveLinkColor = _ctx.Palette.Text };
                            link.LinkClicked += (_, _) => openSettings();
                            _badgeStrip.Controls.Add(link);
                        }
                    }
                }
            }
            finally
            {
                _pendingModel = null;
                if (!IsDisposed) { Cursor = Cursors.Default; _plot.Enabled = true; }
            }
        }

        private Func<PlotModel?> CaptureModelBuilder()
        {
            var rows = _ctx.Rows;
            switch (_kind)
            {
                case ChartKind.Histogram:
                {
                    int c = SlotCol(0);
                    if (c < 0) return () => null;
                    int bins = (int)_binsInput.Value;
                    bool normal = _normalCheck.Checked, kde = _kdeCheck.Checked;
                    return () => ChartBuilders.Histogram(rows, c, Name0(c),
                        new ChartBuilders.HistogramOptions(bins == 0 ? null : bins, normal, kde));
                }
                case ChartKind.BoxPlot:
                {
                    int v = SlotCol(0), g = SlotCol(1);
                    if (v < 0) return () => null;
                    return () => ChartBuilders.BoxPlot(rows, v, Name0(v), g, g >= 0 ? Name0(g) : "");
                }
                case ChartKind.Scatter:
                {
                    int x = SlotCol(0), y = SlotCol(1);
                    if (x < 0 || y < 0) return () => null;
                    bool regression = _regressionCheck.Checked;
                    return () => ChartBuilders.Scatter(rows, x, Name0(x), y, Name0(y),
                        new ChartBuilders.ScatterOptions(regression));
                }
                case ChartKind.CorrelationHeatmap:
                {
                    var cols = new List<int>();
                    var names = new List<string>();
                    foreach (var item in _heatmapColumns.CheckedItems)
                        if (item is ColItem it) { cols.Add(it.Index); names.Add(it.Name); }
                    _heatmapColIndexes = cols.ToArray();
                    return () => ChartBuilders.CorrelationHeatmap(rows, cols, names);
                }
                case ChartKind.QqPlot:
                {
                    int c = SlotCol(0);
                    return () => c < 0 ? null : ChartBuilders.QqPlot(rows, c, Name0(c));
                }
                case ChartKind.TimeSeries:
                {
                    int d = SlotCol(0), v = SlotCol(1);
                    if (d < 0) return () => null;
                    var period = (DateBinPeriod)_periodCombo.SelectedIndex;
                    int movingAverage = (int)_maInput.Value;
                    return () => ChartBuilders.TimeSeries(rows, d, Name0(d), v >= 0 ? Name0(v) : null,
                        new ChartBuilders.TimeSeriesOptions(period,
                            movingAverage, v >= 0 ? v : null));
                }
                case ChartKind.Pareto:
                {
                    int c = SlotCol(0);
                    return () => c < 0 ? null : ChartBuilders.Pareto(rows, c, Name0(c));
                }
                default:
                    return () => null;
            }
        }

        private int SlotCol(int slot) => slot < _slotCombos.Count ? ColIndexOf(_slotCombos[slot]) : -1;
        private string Name0(int c) => c >= 0 && c < _ctx.ColumnNames.Length ? _ctx.ColumnNames[c] : $"Col{c + 1}";

        private void RebuildBadges(PlotModel? model)
        {
            var previous = _badgeStrip.Controls.Cast<Control>().ToArray();
            _badgeStrip.Controls.Clear();
            foreach (var control in previous) control.Dispose();
            if (model is null) return;
            foreach (var badge in model.Badges)
            {
                var chip = new Label
                {
                    Text = badge.Label,
                    AutoSize = true,
                    Padding = new Padding(7, 4, 7, 4),
                    Margin = new Padding(3, 0, 3, 0),
                    BackColor = _ctx.Palette.Surface,
                    ForeColor = _ctx.Palette.Text,
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand,
                };
                string detail = badge.Detail;
                string title = badge.Label;
                chip.Click += (_, _) =>
                {
                    using var form = new ResultForm(title, detail, _ctx.Palette);
                    form.ShowDialog(this);
                };
                _badgeStrip.Controls.Add(chip);
            }
        }

        // ---------------------------------------------------------------- 액션

        private void OnHeatCellClicked(int row, int col)
        {
            if (row < 0 || col < 0 || row >= _heatmapColIndexes.Length || col >= _heatmapColIndexes.Length) return;
            // 셀(행=Y, 열=X) → 해당 두 컬럼 산점도 드릴다운(설계 합의 #4-5)
            _ctx.OpenChart?.Invoke(ChartKind.Scatter, new[] { _heatmapColIndexes[col], _heatmapColIndexes[row] });
        }

        private bool _refreshingRows;

        private async void RefreshFromView()
        {
            if (_ctx.RefreshRowsAsync is null || _refreshingRows || IsDisposed) return;
            _refreshingRows = true;
            Cursor = Cursors.WaitCursor;
            try
            {
                var snapshot = await _ctx.RefreshRowsAsync();
                if (IsDisposed || snapshot is null) return;
                _ctx.Rows = snapshot.Rows;
                RebuildModel();
            }
            catch (Exception ex)
            {
                if (!IsDisposed) MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _refreshingRows = false;
                if (!IsDisposed) Cursor = Cursors.Default;
            }
        }

        private void SavePng()
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png",
                FileName = "chart.png",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            using var bmp = _plot.RenderBitmap();
            bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
        }

        private void CopyToClipboard()
        {
            using var bmp = _plot.RenderBitmap();
            Clipboard.SetImage(bmp);
        }
    }
}
