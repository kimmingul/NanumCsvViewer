using System.Diagnostics;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    public partial class Form1 : Form
    {
        private const string ProgramName = "Nanum CSV Viewer";

        private readonly AppSettings _settings;
        private AppTheme _theme = AppTheme.Light;
        private ThemePalette _palette = ThemePalette.Light;

        private VirtualCsvDocument? _doc;
        private string? _currentPath;
        private CancellationTokenSource? _indexCts;
        private CancellationTokenSource? _opCts;
        private CancellationTokenSource? _findCts;
        // 진행 중 비동기 작업 추적: 새 파일을 열기 전에 취소하고 완료까지 대기(옛 문서 안전 해제).
        private Task? _indexTask;
        private Task? _opTask;
        private Task? _findTask;
        private readonly System.Windows.Forms.Timer _rowCountTimer;
        private readonly System.Windows.Forms.Timer _detailTimer;

        private bool _busy;
        private bool _indexing;
        private bool _userResizedRowHeader;   // 사용자가 행번호 칸 폭을 직접 조절하면 자동 조정 중단
        private bool _settingRowHeaderWidth;   // 프로그램이 폭을 설정하는 중(사용자 조작과 구분)

        // 뷰 상태 — 다중 조건 필터(모두 AND) : 텍스트 조건 1개 + 셀값 조건 N개
        private Func<string[], bool>? _textCondition;
        private string _textConditionDesc = "";
        private readonly List<(string desc, Func<string[], bool> pred)> _valueConditions = new();
        // 다중 컬럼 정렬: 순서가 우선순위(앞이 1차). 헤더 클릭=단일 교체, Shift+클릭=차수 추가.
        private readonly List<SortKey> _sortKeys = new();

        private bool HasAnyFilter => _textCondition is not null || _valueConditions.Count > 0 || !_columnFilters.IsEmpty;

        // 멀티라인 셀 행 높이 계산용
        private int _singleLineHeight = 22;
        private int _lineHeight = 18;
        private const int MaxCellLines = 6;

        public Form1(AppSettings settings)
        {
            _settings = settings;
            InitializeComponent();
            Text = ProgramName;

            // 창/작업표시줄 아이콘: exe에 박힌 앱 아이콘(app.ico) 사용
            try { if (Environment.ProcessPath is { } p) Icon = System.Drawing.Icon.ExtractAssociatedIcon(p); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Icon] {ex}"); }

            BuildEncodingMenu();
            BuildLanguageMenu();
            BuildFeatureMenus();
            ApplyIcons();
            ApplyLocalization();

            // 셀 내 줄바꿈을 여러 줄로 표시
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _lineHeight = grid.Font.Height + 2;
            _singleLineHeight = Math.Max(grid.RowTemplate.Height, _lineHeight + 6);

            // 헤더에 컬럼명 + 타입 배지를 한 줄로 그릴 공간 확보
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 30;

            try { splitContainer1.SplitterDistance = 26; } catch { /* 초기 크기에 따라 무시 */ }

            _rowCountTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _rowCountTimer.Tick += (_, _) => RefreshRowCount();

            // 상세 패널 갱신 디바운스(빠른 셀 이동 시 RichTextBox 재구성 폭주 방지)
            _detailTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _detailTimer.Tick += (_, _) => { _detailTimer.Stop(); UpdateDetailPanel(); };

            // 테마: 저장값(Light/Dark) 우선, 없으면 Windows 시스템 테마 따름.
            _theme = _settings.Theme switch
            {
                "Dark" => AppTheme.Dark,
                "Light" => AppTheme.Light,
                _ => ThemeManager.DetectSystem(),
            };
            ApplyTheme(_theme);

            UpdateFeatureState();
            RefreshSignal();
            statusLabel.Text = Loc.T("Status_OpenPrompt");
        }

        // ---------------------------------------------------------------- UI 설정(아이콘 · 인코딩 메뉴)

        private void ApplyIcons()
        {
            // 메뉴 아이콘
            openToolStripMenuItem.Image = UiIcons.Open();
            quitToolStripMenuItem.Image = UiIcons.Quit();
            findMenuItem.Image = UiIcons.Find();
            findNextMenuItem.Image = UiIcons.FindNext();
            applyFilterMenuItem.Image = UiIcons.Filter();
            editFilterByCellMenuItem.Image = UiIcons.FilterByCell();
            clearFilterToolStripMenuItem.Image = UiIcons.ClearFilter();
            sortAscMenuItem.Image = UiIcons.SortAscending();
            sortDescMenuItem.Image = UiIcons.SortDescending();
            clearSortToolStripMenuItem.Image = UiIcons.ClearSort();
            encodingMenuItem.Image = UiIcons.Encoding();
            detailPanelMenuItem.Image = UiIcons.DetailPanel();
            aboutToolStripMenuItem.Image = UiIcons.About();
            filterByCellMenuItem.Image = UiIcons.FilterByCell();

            // 툴바 버튼(이미지 + 텍스트)
            SetButtonImage(openToolStripButton, UiIcons.Open());
            SetButtonImage(findNextButton, UiIcons.FindNext());
            SetButtonImage(filterByCellButton, UiIcons.FilterByCell());
            SetButtonImage(applyFilterButton, UiIcons.Filter());
            SetButtonImage(clearFilterButton, UiIcons.ClearFilter());
            SetButtonImage(sortAscButton, UiIcons.SortAscending());
            SetButtonImage(sortDescButton, UiIcons.SortDescending());
            SetButtonImage(clearSortButton, UiIcons.ClearSort());
            SetButtonImage(detailToggleButton, UiIcons.DetailPanel());

            encodingStatusButton.Image = UiIcons.Encoding();
            encodingStatusButton.ImageScaling = ToolStripItemImageScaling.None;
        }

        // 아이콘 이미지만 코드에서 제공하고, 표시 방식(DisplayStyle: Image/Text/ImageAndText)은
        // Designer가 단일 소스로 결정하게 둔다(런타임이 Designer 설정을 덮어쓰지 않도록).
        private static void SetButtonImage(ToolStripButton btn, Image img)
        {
            btn.Image = img;
            btn.ImageScaling = ToolStripItemImageScaling.None;
        }

        // View 메뉴와 상태바 인코딩 드롭다운을 같은 목록(SelectableNames)으로 채움.
        private void BuildEncodingMenu()
        {
            foreach (string name in EncodingDetector.SelectableNames)
            {
                var menuItem = new ToolStripMenuItem(name) { Tag = name };
                menuItem.Click += OnEncodingPick;
                encodingMenuItem.DropDownItems.Add(menuItem);

                var ddItem = new ToolStripMenuItem(name) { Tag = name };
                ddItem.Click += OnEncodingPick;
                encodingStatusButton.DropDownItems.Add(ddItem);
            }
        }

        // 상태바 드롭다운·View 메뉴 공통 진입점.
        private void OnEncodingPick(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem item && item.Tag is string name) ChangeEncodingTo(name);
        }

        // ---------------------------------------------------------------- Localization (i18n)

        // View ▸ Language 하위 메뉴 구성(English / 한국어). OS 자동 감지는 시작값.
        private void BuildLanguageMenu()
        {
            AddLang("en", "Lang_English");
            AddLang("ko", "Lang_Korean");

            void AddLang(string code, string key)
            {
                var item = new ToolStripMenuItem { Tag = code };
                item.Click += OnLanguagePick;
                languageMenuItem.DropDownItems.Add(item);
            }
        }

        private void OnLanguagePick(object? sender, EventArgs e)
        {
            if (sender is not ToolStripMenuItem item || item.Tag is not string code) return;
            if (code == Loc.CurrentLanguage) return;
            _settings.Language = code;
            _settings.Save();
            Loc.Apply(code);
            ApplyLocalization();         // 모든 정적 텍스트 즉시 갱신
            RefreshDynamicTexts();       // 상태바 등 동적 텍스트 갱신
        }

        // 메뉴/툴바/툴팁/컨텍스트 등 모든 정적 UI 텍스트를 현재 언어로 설정(코드가 단일 소스).
        private void ApplyLocalization()
        {
            LocalizeFeatureMenus();
            // 메뉴
            fileToolStripMenuItem.Text = Loc.T("Menu_File");
            openToolStripMenuItem.Text = Loc.T("Menu_Open");
            quitToolStripMenuItem.Text = Loc.T("Menu_Quit");
            editToolStripMenuItem.Text = Loc.T("Menu_Edit");
            findMenuItem.Text = Loc.T("Menu_Find");
            findNextMenuItem.Text = Loc.T("Menu_FindNext");
            applyFilterMenuItem.Text = Loc.T("Menu_ApplyFilter");
            editFilterByCellMenuItem.Text = Loc.T("Menu_FilterByCell");
            clearFilterToolStripMenuItem.Text = Loc.T("Menu_ClearFilter");
            sortAscMenuItem.Text = Loc.T("Menu_SortAsc");
            sortDescMenuItem.Text = Loc.T("Menu_SortDesc");
            clearSortToolStripMenuItem.Text = Loc.T("Menu_ClearSort");
            viewToolStripMenuItem.Text = Loc.T("Menu_View");
            encodingMenuItem.Text = Loc.T("Menu_Encoding");
            detailPanelMenuItem.Text = Loc.T("Menu_DetailPanel");
            languageMenuItem.Text = Loc.T("Menu_Language");
            helpToolStripMenuItem.Text = Loc.T("Menu_Help");
            usageMenuItem.Text = Loc.T("Menu_Usage");
            aboutToolStripMenuItem.Text = Loc.T("Menu_About");

            // 언어 하위 항목(라벨 + 체크)
            foreach (ToolStripItem it in languageMenuItem.DropDownItems)
                if (it is ToolStripMenuItem mi && mi.Tag is string code)
                {
                    mi.Text = Loc.T(code == "ko" ? "Lang_Korean" : "Lang_English");
                    mi.Checked = code == Loc.CurrentLanguage;
                }

            // 툴바
            openToolStripButton.Text = Loc.T("Tb_Open");
            findLabel.Text = Loc.T("Tb_FindLabel");
            findNextButton.Text = Loc.T("Tb_FindNext");
            filterByCellButton.Text = Loc.T("Tb_FilterByCell");
            filterByCellButton.ToolTipText = Loc.T("Tip_FilterByCell");
            filterColumnLabel.Text = Loc.T("Tb_FilterLabel");
            applyFilterButton.Text = Loc.T("Tb_Apply");
            clearFilterButton.Text = Loc.T("Tb_Clear");
            sortAscButton.ToolTipText = Loc.T("Tip_SortAsc");
            sortDescButton.ToolTipText = Loc.T("Tip_SortDesc");
            clearSortButton.Text = Loc.T("Menu_ClearSort");
            clearSortButton.ToolTipText = Loc.T("Tip_ClearSort");
            detailToggleButton.Text = Loc.T("Tb_Details");
            detailToggleButton.ToolTipText = Loc.T("Tip_Detail");
            themeToggleButton.ToolTipText = Loc.T("Tip_Theme");
            encodingStatusButton.ToolTipText = Loc.T("Tip_Encoding");

            // 컨텍스트 메뉴
            filterByCellMenuItem.Text = Loc.T("Ctx_FilterByCell");

            // 필터 컬럼 콤보 첫 항목(열려 있는 경우)
            if (filterColumnCombo.Items.Count > 0) filterColumnCombo.Items[0] = Loc.T("Combo_AllColumns");
        }

        // 언어 변경 시 동적 텍스트(상태바·신호등) 갱신.
        private void RefreshDynamicTexts()
        {
            RefreshSignal();
            if (_doc is null) { statusLabel.Text = Loc.T("Status_OpenPrompt"); return; }
            if (HasAnyFilter || _sortKeys.Count > 0) UpdateFilterStatus();
            else statusLabel.Text = Loc.F("Status_NoFilterFmt", _doc.DataRowsAvailable.ToString("N0"), FormatBytes(_doc.FileLength));
        }

        // ---------------------------------------------------------------- Theme (light/dark)

        private void ApplyTheme(AppTheme theme)
        {
            _theme = theme;
            _palette = ThemeManager.Apply(this, theme);
            // 토글 버튼 아이콘: 현재 다크면 해(라이트로 전환), 라이트면 달(다크로 전환)
            themeToggleButton.Image = theme == AppTheme.Dark ? UiIcons.Sun() : UiIcons.Moon();
            if (_doc is not null) { _detailTimer.Stop(); UpdateDetailPanel(); } // 상세 패널 색 갱신
            grid.Invalidate();
        }

        private void OnThemeToggleClick(object? sender, EventArgs e)
        {
            var next = _theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
            ApplyTheme(next);
            _settings.Theme = next == AppTheme.Dark ? "Dark" : "Light";
            _settings.Save();
        }

        // ---------------------------------------------------------------- Help

        private void OnUsageClick(object? sender, EventArgs e)
        {
            using var usage = new UsageForm(_palette);
            usage.ShowDialog(this);
        }

        // ---------------------------------------------------------------- Open

        private async void OnOpenClick(object? sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog(this) != DialogResult.OK) return;
            await OpenFileAsync(openFileDialog1.FileName);
        }

        private async Task OpenFileAsync(string path)
        {
            try
            {
                // 진행 중인 인덱싱/필터/정렬/검색을 취소하고 완료까지 기다린 뒤에야 옛 문서를 해제한다.
                // (검색 스레드가 해제된 _doc/디스크 핸들을 참조해 NRE/ObjectDisposedException 나는 것을 방지)
                await CancelAndDrainAsync();
                DeleteCurrentIndexIfRequested(); // 이전 파일을 닫기 전 캐시 정리(설정 시)
                var old = _doc;
                _doc = null;
                old?.Dispose();

                ResetView();
                _hiddenColumns.Clear();
                _userResizedRowHeader = false; // 새 파일에서는 자동 폭 조정 재개
                _doc = VirtualCsvDocument.Open(path);
                _currentPath = path;

                BuildColumns(_doc.Header);
                SyncEncodingUi(_doc.EncodingName);

                grid.RowCount = 0;
                Text = $"{ProgramName}  -  {Path.GetFileName(path)}";

                StartIndexing();
                UpdateFeatureState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.T("Title_OpenFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                statusLabel.Text = Loc.T("Status_OpenFailed");
            }
        }

        private void BuildColumns(string[] header)
        {
            cellAddressLabel.Text = "";
            cellValueTextBox.Text = "";
            grid.Columns.Clear();
            filterColumnCombo.Items.Clear();
            filterColumnCombo.Items.Add(Loc.T("Combo_AllColumns"));

            for (int i = 0; i < header.Length; i++)
            {
                string name = string.IsNullOrEmpty(header[i]) ? $"Column{i + 1}" : header[i];
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = name,
                    Name = "col" + i,
                    SortMode = DataGridViewColumnSortMode.Programmatic,
                    Width = 130,
                    Resizable = DataGridViewTriState.True,
                };
                grid.Columns.Add(col);
                filterColumnCombo.Items.Add(name);
            }
            filterColumnCombo.SelectedIndex = 0;
        }

        // ---------------------------------------------------------------- Indexing

        private void StartIndexing()
        {
            _indexCts = new CancellationTokenSource();
            _indexing = true;
            progressBar.Visible = true;
            progressBar.Value = 0;
            progressLabel.Visible = true;
            progressLabel.Text = "0%";
            statusLabel.Text = Loc.T("Status_Loading");
            _rowCountTimer.Start();
            UpdateFeatureState();

            var progress = new Progress<IndexProgress>(OnIndexProgress);
            _indexTask = RunIndexingAsync(progress);
        }

        private async Task RunIndexingAsync(IProgress<IndexProgress> progress)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await _doc!.RunIndexingAsync(progress, _indexCts!.Token);
                sw.Stop();
                _rowCountTimer.Stop();
                _indexing = false;
                RefreshRowCount();
                OnIndexingComplete(sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _rowCountTimer.Stop();
                _indexing = false;
                UpdateFeatureState();
            }
            catch (Exception ex)
            {
                _rowCountTimer.Stop();
                _indexing = false;
                progressBar.Visible = false;
                progressLabel.Visible = false;
                UpdateFeatureState();
                MessageBox.Show(ex.Message, Loc.T("Title_IndexError"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnIndexProgress(IndexProgress p)
        {
            int pct = Math.Clamp(p.Percent, 0, 100);
            progressBar.Value = pct;
            progressLabel.Text = pct + "%";
            statusLabel.Text = Loc.F("Status_LoadingProgressFmt", p.RowsSoFar.ToString("N0"), FormatBytes(p.BytesProcessed), FormatBytes(p.FileLength));
        }

        private void OnIndexingComplete(long ms)
        {
            progressBar.Visible = false;
            progressLabel.Visible = false;
            _lastIndexMs = ms;
            ComputeColumnTypeTags();
            UpdateFeatureState();

            if (_doc is null) return;
            string mode = _doc.InMemory ? Loc.T("Mode_Ram") : Loc.T("Mode_Disk");
            string trunc = _doc.RowCountTruncated ? Loc.T("Status_Truncated") : "";
            statusLabel.Text = Loc.F("Status_ReadyFmt",
                _doc.DataRowsAvailable.ToString("N0"), FormatBytes(_doc.FileLength), _doc.ColumnCount,
                _doc.Delimiter, mode, ms.ToString("N0"), trunc);
            RebuildFilterChips();
        }

        private void RefreshRowCount()
        {
            if (_doc is null) { grid.RowCount = 0; return; }
            int count = _doc.DisplayRowCount;
            if (grid.RowCount != count)
            {
                try { grid.RowCount = count; }
                catch { /* 스크롤 위치 조정 중 일시적 예외 무시 */ }
            }
            UpdateRowHeaderWidth();
        }

        private void UpdateRowHeaderWidth()
        {
            // 사용자가 직접 폭을 조절했으면 더 이상 자동 조정하지 않음(사용자 설정 보존)
            if (_doc is null || _userResizedRowHeader) return;
            // 행번호 자릿수에 맞춰 넉넉히(예: 100,000=6자리도 잘 보이게). 사용자는 경계를 끌어 조절 가능.
            int digits = Math.Max(3, _doc.DataRowsAvailable.ToString().Length);
            int w = Math.Max(64, 22 + digits * 9);
            if (grid.RowHeadersWidth != w)
            {
                _settingRowHeaderWidth = true;
                try { grid.RowHeadersWidth = w; } catch { }
                _settingRowHeaderWidth = false;
            }
        }

        private void OnRowHeadersWidthChanged(object? sender, EventArgs e)
        {
            // 프로그램이 설정한 경우가 아니라면(=사용자가 드래그) 자동 조정 중단
            if (!_settingRowHeaderWidth) _userResizedRowHeader = true;
        }

        // ---------------------------------------------------------------- Virtual mode

        private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (_doc is null || e.RowIndex < 0) return;
            if (e.RowIndex >= _doc.DisplayRowCount) return;
            try
            {
                string[] row = _doc.GetDisplayRow(e.RowIndex);
                // 그리드 셀은 길이 제한 미리보기로 표시(긴 XML/CLOB의 비싼 텍스트 레이아웃 방지).
                // 전체 값은 셀 값 표시줄·상세 패널(F4)에서 그대로 본다.
                e.Value = e.ColumnIndex < row.Length ? PreviewCell(row[e.ColumnIndex]) : string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CellValueNeeded] r={e.RowIndex} c={e.ColumnIndex}: {ex}");
                e.Value = string.Empty;
            }
        }

        // 행 헤더에 원본 파일 행번호 표시(보이는 행만 호출됨)
        private void OnRowPostPaint(object? sender, DataGridViewRowPostPaintEventArgs e)
        {
            if (_doc is null || e.RowIndex < 0 || e.RowIndex >= _doc.DisplayRowCount) return;
            string s;
            try { s = _doc.GetSourceRowNumber(e.RowIndex).ToString(); }
            catch { return; }
            var bounds = new Rectangle(e.RowBounds.Left + 2, e.RowBounds.Top, grid.RowHeadersWidth - 6, e.RowBounds.Height);
            var font = grid.RowHeadersDefaultCellStyle.Font ?? grid.Font;
            TextRenderer.DrawText(e.Graphics, s, font, bounds, _palette.HeaderText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // 셀 내 줄바꿈 개수에 따라 행 높이 계산(최대 MaxCellLines줄). 가상 모드라 보이는 행만 호출.
        private void OnRowHeightInfoNeeded(object? sender, DataGridViewRowHeightInfoNeededEventArgs e)
        {
            if (_doc is null || e.RowIndex < 0 || e.RowIndex >= _doc.DisplayRowCount) return;

            // 프리징 방지: DataGridView가 스크롤/선택 계산을 위해 광범위한 행 높이를 질의할 때
            // 화면 밖 행까지 파싱하면 수백만 건 파싱 폭주가 발생한다.
            // → 뷰포트 인근 행만 파싱하고, 그 밖은 캐시된 것만(없으면 기본 1줄 높이) 사용한다.
            int lines = 1;
            string[]? row = null;
            try
            {
                int first = grid.FirstDisplayedScrollingRowIndex;
                if (first >= 0)
                {
                    int disp = grid.DisplayedRowCount(false);
                    bool near = e.RowIndex >= first - 60 && e.RowIndex <= first + disp + 60;
                    if (near) row = _doc.GetDisplayRow(e.RowIndex);
                }
                if (row is null && _doc.TryGetCachedDisplayRow(e.RowIndex, out var cached)) row = cached;
            }
            catch { }

            if (row is not null)
            {
                foreach (string f in row)
                {
                    int c = 1;
                    foreach (char ch in f) if (ch == '\n') c++;
                    if (c > lines) lines = c;
                }
                if (lines > MaxCellLines) lines = MaxCellLines;
            }

            e.Height = lines <= 1 ? _singleLineHeight : _singleLineHeight + (lines - 1) * _lineHeight;
            e.MinimumHeight = 3;
        }

        // 선택 셀 값 표시줄 갱신
        private void OnCurrentCellChanged(object? sender, EventArgs e)
        {
            if (_doc is null || grid.CurrentCell is null)
            {
                cellAddressLabel.Text = "";
                cellValueTextBox.Text = "";
                return;
            }
            int r = grid.CurrentCell.RowIndex, c = grid.CurrentCell.ColumnIndex;
            if (r < 0 || c < 0 || r >= _doc.DisplayRowCount) return;
            try
            {
                string[] row = _doc.GetDisplayRow(r);
                string val = c < row.Length ? row[c] : "";
                // TextBox 멀티라인은 CRLF를 줄바꿈으로 인식
                cellValueTextBox.Text = val.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                string colName = c < grid.Columns.Count ? grid.Columns[c].HeaderText : "";
                cellAddressLabel.Text = Loc.F("CellAddr_Fmt", _doc.GetSourceRowNumber(r).ToString("N0"), colName);
            }
            catch (Exception ex) { Debug.WriteLine($"[CurrentCellChanged] {ex}"); }

            if (!outerSplit.Panel2Collapsed) { _detailTimer.Stop(); _detailTimer.Start(); }
        }

        // ---------------------------------------------------------------- Detail panel (선택 행 전체)

        private bool _syncingDetailToggle;
        private Font? _detailBoldFont;
        private bool _detailEverShown;

        private void OnDetailToggleChanged(object? sender, EventArgs e)
        {
            if (_syncingDetailToggle) return;
            SetDetailPanelVisible(detailToggleButton.Checked);
        }

        private void OnDetailMenuChanged(object? sender, EventArgs e)
        {
            if (_syncingDetailToggle) return;
            SetDetailPanelVisible(detailPanelMenuItem.Checked);
        }

        private void SetDetailPanelVisible(bool visible)
        {
            _syncingDetailToggle = true;
            detailToggleButton.Checked = visible;
            detailPanelMenuItem.Checked = visible;
            _syncingDetailToggle = false;

            if (visible)
            {
                outerSplit.Panel2Collapsed = false;
                if (!_detailEverShown)
                {
                    _detailEverShown = true;
                    try
                    {
                        int want = outerSplit.Width - 360; // 우측 패널 ~360px
                        int min = outerSplit.Panel1MinSize;
                        int max = outerSplit.Width - outerSplit.Panel2MinSize - outerSplit.SplitterWidth;
                        if (max > min) outerSplit.SplitterDistance = Math.Clamp(want, min, max);
                    }
                    catch { }
                }
                UpdateDetailPanel();
            }
            else
            {
                outerSplit.Panel2Collapsed = true;
            }
        }

        private void UpdateDetailPanel()
        {
            if (_doc is null || outerSplit.Panel2Collapsed) return;
            int r = grid.CurrentCell?.RowIndex ?? -1;
            if (r < 0 || r >= _doc.DisplayRowCount)
            {
                detailHeaderLabel.Text = Loc.T("Detail_Header");
                detailRichText.Clear();
                return;
            }

            string[] row;
            try { row = _doc.GetDisplayRow(r); }
            catch (Exception ex) { Debug.WriteLine($"[DetailPanel] {ex}"); return; }

            detailHeaderLabel.Text = Loc.F("Detail_HeaderFmt", _doc.GetSourceRowNumber(r).ToString("N0"));
            _detailBoldFont ??= new Font(detailRichText.Font, FontStyle.Bold);

            detailRichText.SuspendLayout();
            detailRichText.Clear();
            int cols = _doc.ColumnCount;
            for (int c = 0; c < cols; c++)
            {
                string name = c < grid.Columns.Count ? grid.Columns[c].HeaderText : Loc.F("Column_Fmt", c + 1);
                string val = c < row.Length ? row[c] : string.Empty;
                val = val.Replace("\r\n", "\n").Replace("\r", "\n");

                detailRichText.SelectionFont = _detailBoldFont;
                detailRichText.SelectionColor = _palette.Accent;   // 테마에 맞춘 컬럼명 색
                detailRichText.AppendText(name + "\n");

                detailRichText.SelectionFont = detailRichText.Font;
                detailRichText.SelectionColor = _palette.Text;     // 다크에서도 보이도록 테마 텍스트색
                detailRichText.AppendText(val + "\n\n");
            }
            detailRichText.SelectionStart = 0;
            detailRichText.ScrollToCaret();
            detailRichText.ResumeLayout();
        }

        // ---------------------------------------------------------------- Encoding

        // 인코딩 적용(드롭다운·메뉴 공통 경로). 현재와 같으면 무시.
        private void ChangeEncodingTo(string name)
        {
            if (_doc is null || string.Equals(name, _doc.EncodingName, StringComparison.Ordinal)) return;
            _doc.ChangeEncoding(name);
            SyncEncodingUi(name);
            BuildColumns(_doc.Header);
            ResetViewMapOnly();
            grid.RowCount = 0;
            RefreshRowCount();
            grid.Invalidate();
            statusLabel.Text = Loc.F("Status_EncodingChangedFmt", name);
        }

        // 상태바 버튼 텍스트 + 양쪽(메뉴/드롭다운) 체크 표시를 현재 인코딩으로 동기화.
        private void SyncEncodingUi(string name)
        {
            encodingStatusButton.Text = name;
            SyncChecks(encodingMenuItem.DropDownItems, name);
            SyncChecks(encodingStatusButton.DropDownItems, name);

            static void SyncChecks(ToolStripItemCollection items, string current)
            {
                foreach (ToolStripItem it in items)
                    if (it is ToolStripMenuItem mi)
                        mi.Checked = mi.Tag is string n && n == current;
            }
        }

        // ---------------------------------------------------------------- Find (streaming)

        private void OnFindKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = FindNextAsync(); }
        }

        private void OnFindNextClick(object? sender, EventArgs e) => _ = FindNextAsync();

        // Edit ▸ 찾기... (Ctrl+F): 검색 입력란으로 포커스 이동 + 전체 선택
        private void OnFindMenuClick(object? sender, EventArgs e)
        {
            if (!findTextBox.Enabled) return;
            findTextBox.Focus();
            findTextBox.SelectAll();
        }

        private async Task FindNextAsync()
        {
            if (_doc is null || _busy) return;
            var task = FindNextCoreAsync();
            _findTask = task;
            await task;
        }

        private async Task FindNextCoreAsync()
        {
            var doc = _doc;                       // 로컬 캡처: 검색 도중 파일이 바뀌어도 안전
            if (doc is null) return;
            string term = findTextBox.Text;
            if (string.IsNullOrEmpty(term)) return;

            // 입력을 검색 모드로 라우팅: /pat/·regex: → 정규식, fuzzy: → 퍼지, 그 외 → contains
            CsvSearchMatcher matcher;
            try
            {
                var query = CsvSearchQuery.FromUserInput(term, null);
                if (query is null) return;
                matcher = new CsvSearchMatcher(query);
            }
            catch (CsvSearchException ex)
            {
                statusLabel.Text = ex.Message;
                return;
            }

            int total = doc.DisplayRowCount;
            if (total == 0) return;
            int start = (grid.CurrentCell?.RowIndex ?? -1) + 1;

            _findCts?.Cancel();
            _findCts = new CancellationTokenSource();
            var ct = _findCts.Token;

            SetBusy(true);
            statusLabel.Text = Loc.F("Status_SearchingFmt", term);
            int found;
            try
            {
                found = await Task.Run(() => SearchForward(doc, matcher, start, total, ct), ct);
            }
            catch (OperationCanceledException) { return; }
            finally { SetBusy(false); }

            if (doc != _doc) return;              // 검색 중 다른 파일이 열렸으면 결과 무시

            if (found >= 0)
            {
                grid.CurrentCell = grid.Rows[found].Cells[Math.Max(0, grid.CurrentCell?.ColumnIndex ?? 0)];
                grid.FirstDisplayedScrollingRowIndex = found;
                statusLabel.Text = Loc.F("Status_FoundFmt", term, (found + 1).ToString("N0"));
            }
            else
            {
                statusLabel.Text = Loc.F("Status_NotFoundFmt", term);
            }
        }

        private static int SearchForward(VirtualCsvDocument doc, CsvSearchMatcher matcher, int start, int total, CancellationToken ct)
        {
            // start..total-1 후 0..start-1 (랩어라운드)
            for (int i = start; i < total; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                if (matcher.FirstMatch(doc.GetDisplayRow(i)) is not null) return i;
            }
            for (int i = 0; i < start && i < total; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                if (matcher.FirstMatch(doc.GetDisplayRow(i)) is not null) return i;
            }
            return -1;
        }

        // ---------------------------------------------------------------- Filter (Phase 3)

        private void OnFilterKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = ApplyTextFilterAsync(); }
        }

        private void OnApplyFilterClick(object? sender, EventArgs e) => _ = ApplyTextFilterAsync();

        // 텍스트 필터 = 조건 1개 슬롯(적용 시 교체, 빈 값이면 해제). 셀값 조건들과 AND로 합쳐짐.
        private async Task ApplyTextFilterAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            string term = filterTextBox.Text;
            bool hadText = _textCondition is not null;

            if (string.IsNullOrEmpty(term))
            {
                if (!hadText) { UpdateFilterStatus(); return; }   // 변화 없음
                _textCondition = null;
                _textConditionDesc = "";
                await RebuildFilterAsync(Loc.T("Status_FilterUpdating"));        // 조건 제거 → 넓어짐 → 전체 재스캔
                return;
            }

            int sel = filterColumnCombo.SelectedIndex;   // 0 = 모든 컬럼
            int col = sel - 1;
            var pred = BuildContainsPredicate(term, col);
            string colName = col < 0 ? Loc.T("Filter_All") : (col < grid.Columns.Count ? grid.Columns[col].HeaderText : Loc.F("ColShort_Fmt", col + 1));
            _textCondition = pred;
            _textConditionDesc = Loc.F("Filter_ContainsFmt", colName, Trunc(term));

            if (hadText)
            {
                // 기존 텍스트 조건 교체 → 결과가 넓어질 수 있어 전체 재스캔
                await RebuildFilterAsync(Loc.T("Status_FilterApplying"));
            }
            else
            {
                // 첫 텍스트 조건 = 순수 AND 추가(좁힘) → 증분(현재 뷰만)
                await RunViewOpAsync(p => _doc.FilterWithinViewAsync(pred, p, _opCts!.Token), Loc.T("Status_FilterApplying"));
                UpdateFilterStatus();
            }
        }

        // 셀값으로 필터(AND 누적): 선택 셀의 열 = 그 값(정확 일치) 조건을 추가.
        private void OnFilterByCellClick(object? sender, EventArgs e) => _ = FilterBySelectedCellAsync();

        private async Task FilterBySelectedCellAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var cell = grid.CurrentCell;
            if (cell is null || cell.RowIndex < 0 || cell.ColumnIndex < 0)
            {
                statusLabel.Text = Loc.T("Status_SelectCellFirst");
                return;
            }
            int viewRow = cell.RowIndex, col = cell.ColumnIndex;
            string[] row;
            try { row = _doc.GetDisplayRow(viewRow); } catch { return; }
            string value = col < row.Length ? row[col] : "";
            string colName = col < grid.Columns.Count ? grid.Columns[col].HeaderText : Loc.F("ColShort_Fmt", col + 1);

            int capCol = col;
            string capVal = value;
            Func<string[], bool> pred = r => capCol < r.Length && string.Equals(r[capCol], capVal, StringComparison.Ordinal);
            _valueConditions.Add((Loc.F("Filter_EqualsFmt", colName, Trunc(value)), pred));

            // 증분: 현재 뷰만 새 조건으로 좁힘(전체 재스캔 안 함). 정렬 순서 유지.
            await RunViewOpAsync(p => _doc.FilterWithinViewAsync(pred, p, _opCts!.Token), Loc.T("Status_CellFilterApplying"));
            UpdateFilterStatus();
        }

        // 모든 활성 조건(텍스트 + 셀값들)을 AND로 합쳐 뷰를 다시 구성. 필터 변경 시 정렬은 초기화.
        private async Task RebuildFilterAsync(string busyText)
        {
            if (_doc is null) return;
            _sortKeys.Clear();
            ClearSortGlyphs();

            if (!HasAnyFilter)
            {
                _doc.ClearView();
                grid.RowCount = 0;
                RefreshRowCount();
                grid.Invalidate();
                UpdateFilterStatus();
                return;
            }

            var combined = BuildCombinedPredicate();
            await RunViewOpAsync(p => _doc.ApplyFilterAsync(combined, p, _opCts!.Token), busyText);
            UpdateFilterStatus();
        }

        private Func<string[], bool> BuildCombinedPredicate()
        {
            // 모든 활성 조건을 개별 술어로 평탄화 → AND(모두) 또는 OR(하나라도)로 결합.
            var preds = new List<Func<string[], bool>>();
            if (_textCondition is not null) preds.Add(_textCondition);
            preds.AddRange(_valueConditions.Select(v => v.pred));
            preds.AddRange(_columnFilters.IndividualPredicates());
            var arr = preds.ToArray();
            bool any = _filterMatchAny;
            return row =>
            {
                if (arr.Length == 0) return true;
                if (any)
                {
                    foreach (var p in arr) if (p(row)) return true;
                    return false;
                }
                foreach (var p in arr) if (!p(row)) return false;
                return true;
            };
        }

        private static Func<string[], bool> BuildContainsPredicate(string term, int col)
        {
            if (col < 0)
                return row =>
                {
                    foreach (string f in row)
                        if (f.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                };
            return row => col < row.Length && row[col].Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateFilterStatus()
        {
            if (_doc is null) return;
            RebuildFilterChips();
            string size = FormatBytes(_doc.FileLength);
            if (!HasAnyFilter)
            {
                statusLabel.Text = Loc.F("Status_NoFilterFmt", _doc.DataRowsAvailable.ToString("N0"), size);
                return;
            }
            var parts = new List<string>();
            if (_textCondition is not null) parts.Add(_textConditionDesc);
            parts.AddRange(_valueConditions.Select(v => v.desc));
            parts.AddRange(_columnFilters.Descriptions(_doc.Header));
            statusLabel.Text = Loc.F("Status_FilterFmt", parts.Count, string.Join(Loc.T("Filter_And"), parts),
                _doc.DisplayRowCount.ToString("N0"), _doc.DataRowsAvailable.ToString("N0"), size);
        }

        private static string Trunc(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 20 ? s : s.Substring(0, 20) + "…";
        }

        private void OnGridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
        {
            // 우클릭 시 해당 셀을 현재 셀로 선택(컨텍스트 메뉴의 '이 셀 값으로 필터'가 그 셀에 적용되도록)
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.ColumnIndex >= 0)
            {
                try { grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex]; } catch { }
            }
        }

        private void OnClearFilterClick(object? sender, EventArgs e)
        {
            if (_doc is null) return;
            _textCondition = null;
            _textConditionDesc = "";
            _valueConditions.Clear();
            _columnFilters.Clear();
            _sortKeys.Clear();
            ClearSortGlyphs();
            _doc.ClearView();
            grid.RowCount = 0;
            RefreshRowCount();
            grid.Invalidate();
            statusLabel.Text = Loc.F("Status_FilterClearedFmt", _doc.DataRowsAvailable.ToString("N0"), FormatBytes(_doc.FileLength));
            RebuildFilterChips();
        }

        // ---------------------------------------------------------------- Sort (Phase 3)

        private void OnColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || e.ColumnIndex < 0) return;
            // 우측 깔때기 영역(약 18px) 클릭은 정렬 대신 필터 팝오버. 그 외는 정렬.
            if (e.Button == MouseButtons.Left && IsFilterableColumn(e.ColumnIndex) &&
                e.X >= grid.Columns[e.ColumnIndex].Width - 18)
            {
                OpenColumnFilter(e.ColumnIndex);
                return;
            }
            bool additive = (ModifierKeys & Keys.Shift) == Keys.Shift;
            ToggleSortColumn(e.ColumnIndex, additive);
        }

        // 헤더 클릭=단일 기준으로 교체(같은 단일 컬럼 재클릭이면 방향 토글),
        // Shift+클릭=정렬 차수로 추가(이미 포함된 컬럼이면 방향 토글).
        private void ToggleSortColumn(int col, bool additive)
        {
            int existing = _sortKeys.FindIndex(s => s.Column == col);
            if (additive)
            {
                if (existing >= 0) _sortKeys[existing] = new SortKey(col, !_sortKeys[existing].Ascending);
                else _sortKeys.Add(new SortKey(col, true));
            }
            else
            {
                bool asc = (existing == 0 && _sortKeys.Count == 1) ? !_sortKeys[0].Ascending : true;
                _sortKeys.Clear();
                _sortKeys.Add(new SortKey(col, asc));
            }
            _ = SortAsync();
        }

        // Edit/툴바 ▸ 오름·내림차순 정렬: 현재 셀의 열(없으면 첫 열)을 단일 기준으로 정렬.
        private void OnSortAscMenuClick(object? sender, EventArgs e) => SortCurrentColumn(true);
        private void OnSortDescMenuClick(object? sender, EventArgs e) => SortCurrentColumn(false);

        private void SortCurrentColumn(bool ascending)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            int col = grid.CurrentCell?.ColumnIndex ?? -1;
            if (col < 0) col = grid.Columns.Count > 0 ? 0 : -1;
            if (col < 0) return;
            _sortKeys.Clear();
            _sortKeys.Add(new SortKey(col, ascending));
            _ = SortAsync();
        }

        private async Task SortAsync()
        {
            if (_doc is null || _sortKeys.Count == 0) return;
            // 정렬은 현재 뷰(필터 결과 또는 전체)를 기준으로 동작. 다중 키는 목록 순서가 우선순위.
            var keys = _sortKeys.ToArray();
            await RunViewOpAsync(p => _doc.SortAsync(keys, p, _opCts!.Token), Loc.T("Status_Sorting"));

            UpdateSortGlyphs();
            statusLabel.Text = Loc.F("Status_SortFmt", DescribeSort(), _doc.DisplayRowCount.ToString("N0"));
        }

        private string DescribeSort()
        {
            var parts = new List<string>(_sortKeys.Count);
            foreach (var s in _sortKeys)
            {
                string name = s.Column < grid.Columns.Count ? grid.Columns[s.Column].HeaderText : Loc.F("ColShort_Fmt", s.Column + 1);
                parts.Add($"{name} {(s.Ascending ? "▲" : "▼")}");
            }
            return string.Join(" → ", parts);
        }

        private void OnClearSortClick(object? sender, EventArgs e)
        {
            if (_doc is null || _busy) return;
            _sortKeys.Clear();
            ClearSortGlyphs();
            // 정렬만 해제: 필터가 있으면 결과를 파일 순서로 즉시 복원(재필터 없음), 없으면 전체 보기.
            if (HasAnyFilter)
            {
                _doc.ResetViewOrder();
                grid.Invalidate();
                UpdateFilterStatus();
            }
            else
            {
                _doc.ClearView();
                grid.RowCount = 0;
                RefreshRowCount();
                grid.Invalidate();
                statusLabel.Text = Loc.T("Status_SortCleared");
            }
        }

        private void ClearSortGlyphs()
        {
            foreach (DataGridViewColumn c in grid.Columns)
                c.HeaderCell.SortGlyphDirection = SortOrder.None;
            grid.Invalidate(); // 우선순위 배지 다시 그리기
        }

        // 정렬 컬럼마다 화살표 표시. 다중 정렬이면 우선순위 배지는 OnGridCellPainting에서 그림.
        private void UpdateSortGlyphs()
        {
            foreach (DataGridViewColumn c in grid.Columns)
                c.HeaderCell.SortGlyphDirection = SortOrder.None;
            foreach (var s in _sortKeys)
                if (s.Column >= 0 && s.Column < grid.Columns.Count)
                    grid.Columns[s.Column].HeaderCell.SortGlyphDirection =
                        s.Ascending ? SortOrder.Ascending : SortOrder.Descending;
            grid.Invalidate();
        }

        // 컬럼 헤더를 직접 그린다: 컬럼명(상단) + 추론 타입 배지(하단) + 정렬 화살표·다중정렬 우선순위(우상단).
        // HeaderText는 손대지 않아 컬럼명 조회(필터/상세/주소)는 그대로 유지된다.
        private void OnGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.Graphics is null || e.RowIndex != -1 || e.ColumnIndex < 0) return;
            var g = e.Graphics;
            var b = e.CellBounds;

            e.PaintBackground(b, true); // 테마 배경(+선택)

            var font = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
            string text = e.FormattedValue?.ToString() ?? grid.Columns[e.ColumnIndex].HeaderText;

            // 우측에 정렬 화살표(+우선순위) + 필터 깔때기 공간 확보
            int idx = _sortKeys.FindIndex(s => s.Column == e.ColumnIndex);
            bool filterable = IsFilterableColumn(e.ColumnIndex);
            bool filterActive = filterable && _columnFilters.HasFilterFor(e.ColumnIndex);
            int funnelW = filterable ? 18 : 0;
            int rightReserved = funnelW + (idx >= 0 ? (_sortKeys.Count > 1 ? 28 : 16) : 2);

            // 배지는 컬럼명 바로 오른쪽에 인라인 배치(보기 설정으로 끌 수 있음)
            bool hasBadge = _showTypeBadges && e.ColumnIndex < _columnSummaries.Length;
            int badgeW = hasBadge ? MeasureBadgeWidth(g, _columnSummaries[e.ColumnIndex].InferredType) : 0;
            int gap = hasBadge ? 6 : 0;

            int nameLeft = b.Left + 6;
            int maxNameW = b.Width - 6 - rightReserved - badgeW - gap - 2;
            // 측정·그리기의 패딩을 일치시켜야 불필요한 생략부호(…)가 생기지 않음.
            int nameSize = TextRenderer.MeasureText(g, text, font).Width;
            int nameW = Math.Max(0, Math.Min(nameSize, maxNameW));

            var textRect = new Rectangle(nameLeft, b.Top, nameW + 2, b.Height);
            TextRenderer.DrawText(g, text, font, textRect, _palette.HeaderText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (hasBadge)
            {
                int badgeX = Math.Min(nameLeft + nameW + gap, b.Right - rightReserved - badgeW);
                if (badgeX >= nameLeft)
                    DrawTypeBadge(g, new Point(badgeX, b.Top + (b.Height - 16) / 2), _columnSummaries[e.ColumnIndex].InferredType);
            }

            if (filterable)
                DrawFunnel(g, new Rectangle(b.Right - 16, b.Top + (b.Height - 10) / 2, 11, 10), filterActive);

            if (idx >= 0)
            {
                DrawSortArrow(g, new Rectangle(b.Right - 16 - funnelW, b.Top + (b.Height - 9) / 2, 10, 9), _sortKeys[idx].Ascending);
                if (_sortKeys.Count > 1)
                {
                    using var pf = new Font(grid.Font.FontFamily, 6.5f, FontStyle.Bold);
                    TextRenderer.DrawText(g, (idx + 1).ToString(), pf,
                        new Point(b.Right - 28 - funnelW, b.Top + 3), _palette.Accent, TextFormatFlags.NoPadding);
                }
            }

            using (var pen = new Pen(_palette.Border))
                g.DrawLine(pen, b.Right - 1, b.Top, b.Right - 1, b.Bottom - 1);

            e.Handled = true;
        }

        // ---------------------------------------------------------------- Shared op runner

        private Task RunViewOpAsync(Func<IProgress<int>, Task> op, string busyText)
        {
            var task = RunViewOpCoreAsync(op, busyText);
            _opTask = task; // 새 파일 열기 전 드레인 대상으로 추적
            return task;
        }

        private async Task RunViewOpCoreAsync(Func<IProgress<int>, Task> op, string busyText)
        {
            _opCts?.Cancel();
            _opCts = new CancellationTokenSource();
            SetBusy(true);
            statusLabel.Text = busyText;
            progressBar.Visible = true;
            progressBar.Value = 0;
            progressLabel.Visible = true;
            progressLabel.Text = "0%";
            var prog = new Progress<int>(p =>
            {
                progressBar.Value = Math.Clamp(p, 0, 100);
                progressLabel.Text = Math.Clamp(p, 0, 100) + "%";
            });
            try
            {
                await op(prog);
            }
            catch (OperationCanceledException) { }
            finally
            {
                progressBar.Visible = false;
                progressLabel.Visible = false;
                SetBusy(false);
                grid.RowCount = 0;
                RefreshRowCount();
                grid.Invalidate();
            }
        }

        // ---------------------------------------------------------------- Misc

        private void OnAboutClick(object? sender, EventArgs e)
        {
            using var about = new About { StartPosition = FormStartPosition.CenterParent };
            about.ShowDialog(this);
        }

        private void OnQuitClick(object? sender, EventArgs e) => Close();

        private void SetBusy(bool busy)
        {
            _busy = busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            UpdateFeatureState();
        }

        private void UpdateFeatureState()
        {
            bool open = _doc is not null;
            bool ready = open && _doc!.IndexingComplete && !_busy;

            encodingStatusButton.Enabled = open && !_busy;
            encodingMenuItem.Enabled = open && !_busy;
            findTextBox.Enabled = open && !_busy;
            findNextButton.Enabled = open && !_busy;
            findMenuItem.Enabled = open && !_busy;
            findNextMenuItem.Enabled = open && !_busy;

            filterColumnCombo.Enabled = ready;
            filterTextBox.Enabled = ready;
            applyFilterButton.Enabled = ready;
            clearFilterButton.Enabled = ready;
            filterByCellButton.Enabled = ready;
            filterByCellMenuItem.Enabled = ready;
            applyFilterMenuItem.Enabled = ready;
            editFilterByCellMenuItem.Enabled = ready;
            clearFilterToolStripMenuItem.Enabled = ready;
            clearSortToolStripMenuItem.Enabled = ready;
            sortAscMenuItem.Enabled = ready;
            sortDescMenuItem.Enabled = ready;
            sortAscButton.Enabled = ready;
            sortDescButton.Enabled = ready;
            clearSortButton.Enabled = ready;

            UpdateFeatureMenuState();
            RefreshSignal();
        }

        // 상태바 우측 신호등(단일 점): 대기/로딩/작업중/준비완료
        private void RefreshSignal()
        {
            Color c; string t;
            if (_doc is null) { c = Color.Gray; t = Loc.T("Signal_Idle"); }
            else if (_indexing) { c = Color.Goldenrod; t = Loc.T("Signal_Loading"); }
            else if (_busy) { c = Color.DarkOrange; t = Loc.T("Signal_Busy"); }
            else { c = Color.ForestGreen; t = Loc.T("Signal_Ready"); }
            signalLabel.ForeColor = c;
            signalLabel.Text = "● " + t;
        }

        private void ResetView()
        {
            _textCondition = null;
            _textConditionDesc = "";
            _valueConditions.Clear();
            _columnFilters.Clear();
            _sortKeys.Clear();
            ClearSortGlyphs();
        }

        private void ResetViewMapOnly()
        {
            _textCondition = null;
            _textConditionDesc = "";
            _valueConditions.Clear();
            _columnFilters.Clear();
            _sortKeys.Clear();
            ClearSortGlyphs();
            _doc?.ClearView();
        }

        private void CancelAll()
        {
            _rowCountTimer.Stop();
            _indexCts?.Cancel();
            _opCts?.Cancel();
            _findCts?.Cancel();
        }

        // 모든 백그라운드 작업을 취소하고 완료될 때까지 대기. 옛 문서를 Dispose하기 전에 호출해야
        // 백그라운드 스레드가 해제된 리소스를 건드리지 않는다.
        private async Task CancelAndDrainAsync()
        {
            CancelAll();
            var tasks = new List<Task>(3);
            if (_indexTask is not null) tasks.Add(_indexTask);
            if (_opTask is not null) tasks.Add(_opTask);
            if (_findTask is not null) tasks.Add(_findTask);
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks); }
                catch { /* 취소/오류는 각 메서드가 자체 처리하므로 여기선 무시 */ }
            }
            _indexTask = _opTask = _findTask = null;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.#} {units[u]}";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 셀 값 표시줄을 한 줄 높이로 컴팩트하게 시작(레이아웃 확정 후 설정).
            try
            {
                int want = _singleLineHeight + 4;
                if (want >= splitContainer1.Panel1MinSize &&
                    want <= splitContainer1.Height - splitContainer1.Panel2MinSize - splitContainer1.SplitterWidth)
                {
                    splitContainer1.SplitterDistance = want;
                }
            }
            catch { }

            // 상세 패널을 기본으로 표시
            SetDetailPanelVisible(true);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CancelAll();
            DeleteCurrentIndexIfRequested(); // 종료 시 현재 파일 캐시 정리(설정 시)
            _doc?.Dispose();
            CleanupTempImports();
            _detailBoldFont?.Dispose();
            _detailTimer?.Dispose();
            _rowCountTimer?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
