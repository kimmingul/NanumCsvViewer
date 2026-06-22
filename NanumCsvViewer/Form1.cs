using System.Diagnostics;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    public partial class Form1 : Form
    {
        private const string ProgramName = "Nanum CSV Viewer";

        private VirtualCsvDocument? _doc;
        private CancellationTokenSource? _indexCts;
        private CancellationTokenSource? _opCts;
        private CancellationTokenSource? _findCts;
        // 진행 중 비동기 작업 추적: 새 파일을 열기 전에 취소하고 완료까지 대기(옛 문서 안전 해제).
        private Task? _indexTask;
        private Task? _opTask;
        private Task? _findTask;
        private readonly System.Windows.Forms.Timer _rowCountTimer;
        private readonly System.Windows.Forms.Timer _detailTimer;

        private bool _suppressEncodingEvent;
        private bool _busy;
        private bool _indexing;
        private bool _userResizedRowHeader;   // 사용자가 행번호 칸 폭을 직접 조절하면 자동 조정 중단
        private bool _settingRowHeaderWidth;   // 프로그램이 폭을 설정하는 중(사용자 조작과 구분)

        // 뷰 상태 — 다중 조건 필터(모두 AND) : 텍스트 조건 1개 + 셀값 조건 N개
        private Func<string[], bool>? _textCondition;
        private string _textConditionDesc = "";
        private readonly List<(string desc, Func<string[], bool> pred)> _valueConditions = new();
        private int _sortColumn = -1;
        private bool _sortAscending = true;

        private bool HasAnyFilter => _textCondition is not null || _valueConditions.Count > 0;

        // 멀티라인 셀 행 높이 계산용
        private int _singleLineHeight = 22;
        private int _lineHeight = 18;
        private const int MaxCellLines = 6;

        public Form1()
        {
            InitializeComponent();
            Text = ProgramName;

            encodingCombo.Items.AddRange(EncodingDetector.SelectableNames);
            BuildEncodingMenu();
            ApplyIcons();

            // 셀 내 줄바꿈을 여러 줄로 표시
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _lineHeight = grid.Font.Height + 2;
            _singleLineHeight = Math.Max(grid.RowTemplate.Height, _lineHeight + 6);

            try { splitContainer1.SplitterDistance = 26; } catch { /* 초기 크기에 따라 무시 */ }

            _rowCountTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _rowCountTimer.Tick += (_, _) => RefreshRowCount();

            // 상세 패널 갱신 디바운스(빠른 셀 이동 시 RichTextBox 재구성 폭주 방지)
            _detailTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _detailTimer.Tick += (_, _) => { _detailTimer.Stop(); UpdateDetailPanel(); };

            UpdateFeatureState();
            RefreshSignal();
            statusLabel.Text = "파일을 여세요 (File ▸ Open).";
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
        }

        private static void SetButtonImage(ToolStripButton btn, Image img)
        {
            btn.Image = img;
            btn.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            btn.ImageScaling = ToolStripItemImageScaling.None;
        }

        // View ▸ 인코딩 하위 메뉴를 SelectableNames로 채움(콤보와 동일 목록).
        private void BuildEncodingMenu()
        {
            foreach (string name in EncodingDetector.SelectableNames)
            {
                var item = new ToolStripMenuItem(name) { Tag = name };
                item.Click += OnEncodingMenuItemClick;
                encodingMenuItem.DropDownItems.Add(item);
            }
        }

        private void OnEncodingMenuItemClick(object? sender, EventArgs e)
        {
            if (_doc is null || sender is not ToolStripMenuItem item || item.Tag is not string name) return;
            int idx = encodingCombo.Items.IndexOf(name);
            if (idx >= 0) encodingCombo.SelectedIndex = idx; // OnEncodingChanged가 실제 변경을 수행
        }

        // 현재 인코딩에 해당하는 하위 메뉴 항목에 체크 표시.
        private void SyncEncodingMenu(string name)
        {
            foreach (ToolStripItem it in encodingMenuItem.DropDownItems)
                if (it is ToolStripMenuItem mi)
                    mi.Checked = mi.Tag is string n && n == name;
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
                var old = _doc;
                _doc = null;
                old?.Dispose();

                ResetView();
                _userResizedRowHeader = false; // 새 파일에서는 자동 폭 조정 재개
                _doc = VirtualCsvDocument.Open(path);

                BuildColumns(_doc.Header);
                SetEncodingComboSelection(_doc.EncodingName);

                grid.RowCount = 0;
                Text = $"{ProgramName}  -  {Path.GetFileName(path)}";

                StartIndexing();
                UpdateFeatureState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "열기 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                statusLabel.Text = "열기 실패.";
            }
        }

        private void BuildColumns(string[] header)
        {
            cellAddressLabel.Text = "";
            cellValueTextBox.Text = "";
            grid.Columns.Clear();
            filterColumnCombo.Items.Clear();
            filterColumnCombo.Items.Add("(모든 컬럼)");

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
            statusLabel.Text = "불러오는 중...";
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
                MessageBox.Show(ex.Message, "인덱싱 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnIndexProgress(IndexProgress p)
        {
            int pct = Math.Clamp(p.Percent, 0, 100);
            progressBar.Value = pct;
            progressLabel.Text = pct + "%";
            statusLabel.Text = $"불러오는 중...  {p.RowsSoFar:N0} 행 인덱싱  ({FormatBytes(p.BytesProcessed)} / {FormatBytes(p.FileLength)})";
        }

        private void OnIndexingComplete(long ms)
        {
            progressBar.Visible = false;
            progressLabel.Visible = false;
            UpdateFeatureState();

            if (_doc is null) return;
            string mode = _doc.InMemory ? "RAM" : "디스크";
            string trunc = _doc.RowCountTruncated ? "  [행 수 한계 초과로 일부만 표시]" : "";
            statusLabel.Text =
                $"준비 완료 · {_doc.DataRowsAvailable:N0} 행 · {_doc.ColumnCount} 열 · {_doc.EncodingName} · 구분자 '{_doc.Delimiter}' · {mode} 모드 · {ms:N0} ms · 필터/정렬 준비됨{trunc}";
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
                e.Value = e.ColumnIndex < row.Length ? row[e.ColumnIndex] : string.Empty;
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
            TextRenderer.DrawText(e.Graphics, s, font, bounds, SystemColors.ControlText,
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
                cellAddressLabel.Text = $"R{_doc.GetSourceRowNumber(r):N0} · {colName}";
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
                detailHeaderLabel.Text = "행 상세";
                detailRichText.Clear();
                return;
            }

            string[] row;
            try { row = _doc.GetDisplayRow(r); }
            catch (Exception ex) { Debug.WriteLine($"[DetailPanel] {ex}"); return; }

            detailHeaderLabel.Text = $"행 상세 — R{_doc.GetSourceRowNumber(r):N0}";
            _detailBoldFont ??= new Font(detailRichText.Font, FontStyle.Bold);

            detailRichText.SuspendLayout();
            detailRichText.Clear();
            int cols = _doc.ColumnCount;
            for (int c = 0; c < cols; c++)
            {
                string name = c < grid.Columns.Count ? grid.Columns[c].HeaderText : $"Column{c + 1}";
                string val = c < row.Length ? row[c] : string.Empty;
                val = val.Replace("\r\n", "\n").Replace("\r", "\n");

                detailRichText.SelectionFont = _detailBoldFont;
                detailRichText.SelectionColor = Color.FromArgb(0, 70, 140);
                detailRichText.AppendText(name + "\n");

                detailRichText.SelectionFont = detailRichText.Font;
                detailRichText.SelectionColor = Color.Black;
                detailRichText.AppendText(val + "\n\n");
            }
            detailRichText.SelectionStart = 0;
            detailRichText.ScrollToCaret();
            detailRichText.ResumeLayout();
        }

        // ---------------------------------------------------------------- Encoding

        private void SetEncodingComboSelection(string name)
        {
            _suppressEncodingEvent = true;
            int idx = encodingCombo.Items.IndexOf(name);
            encodingCombo.SelectedIndex = idx >= 0 ? idx : 0;
            _suppressEncodingEvent = false;
            SyncEncodingMenu(encodingCombo.SelectedItem as string ?? "");
        }

        private void OnEncodingChanged(object? sender, EventArgs e)
        {
            if (_suppressEncodingEvent || _doc is null) return;
            if (encodingCombo.SelectedItem is not string name) return;

            _doc.ChangeEncoding(name);
            SyncEncodingMenu(name);
            BuildColumns(_doc.Header);
            ResetViewMapOnly();
            grid.RowCount = 0;
            RefreshRowCount();
            grid.Invalidate();
            statusLabel.Text = $"인코딩 변경: {name}";
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

            int total = doc.DisplayRowCount;
            if (total == 0) return;
            int start = (grid.CurrentCell?.RowIndex ?? -1) + 1;

            _findCts?.Cancel();
            _findCts = new CancellationTokenSource();
            var ct = _findCts.Token;

            SetBusy(true);
            statusLabel.Text = $"'{term}' 검색 중...";
            int found;
            try
            {
                found = await Task.Run(() => SearchForward(doc, term, start, total, ct), ct);
            }
            catch (OperationCanceledException) { return; }
            finally { SetBusy(false); }

            if (doc != _doc) return;              // 검색 중 다른 파일이 열렸으면 결과 무시

            if (found >= 0)
            {
                grid.CurrentCell = grid.Rows[found].Cells[Math.Max(0, grid.CurrentCell?.ColumnIndex ?? 0)];
                grid.FirstDisplayedScrollingRowIndex = found;
                statusLabel.Text = $"'{term}' 발견: {found + 1:N0} 행";
            }
            else
            {
                statusLabel.Text = $"'{term}' 을(를) 찾을 수 없습니다.";
            }
        }

        private static int SearchForward(VirtualCsvDocument doc, string term, int start, int total, CancellationToken ct)
        {
            // start..total-1 후 0..start-1 (랩어라운드)
            for (int i = start; i < total; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                if (RowContains(doc, i, term)) return i;
            }
            for (int i = 0; i < start && i < total; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                if (RowContains(doc, i, term)) return i;
            }
            return -1;
        }

        private static bool RowContains(VirtualCsvDocument doc, int viewRow, string term)
        {
            string[] row = doc.GetDisplayRow(viewRow);
            foreach (string f in row)
                if (f.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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
                await RebuildFilterAsync("필터 갱신 중...");        // 조건 제거 → 넓어짐 → 전체 재스캔
                return;
            }

            int sel = filterColumnCombo.SelectedIndex;   // 0 = 모든 컬럼
            int col = sel - 1;
            var pred = BuildContainsPredicate(term, col);
            string colName = col < 0 ? "전체" : (col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"열{col + 1}");
            _textCondition = pred;
            _textConditionDesc = $"{colName}⊇\"{Trunc(term)}\"";

            if (hadText)
            {
                // 기존 텍스트 조건 교체 → 결과가 넓어질 수 있어 전체 재스캔
                await RebuildFilterAsync("필터 적용 중...");
            }
            else
            {
                // 첫 텍스트 조건 = 순수 AND 추가(좁힘) → 증분(현재 뷰만)
                await RunViewOpAsync(p => _doc.FilterWithinViewAsync(pred, p, _opCts!.Token), "필터 적용 중...");
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
                statusLabel.Text = "먼저 셀을 선택하세요.";
                return;
            }
            int viewRow = cell.RowIndex, col = cell.ColumnIndex;
            string[] row;
            try { row = _doc.GetDisplayRow(viewRow); } catch { return; }
            string value = col < row.Length ? row[col] : "";
            string colName = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"열{col + 1}";

            int capCol = col;
            string capVal = value;
            Func<string[], bool> pred = r => capCol < r.Length && string.Equals(r[capCol], capVal, StringComparison.Ordinal);
            _valueConditions.Add(($"{colName}=\"{Trunc(value)}\"", pred));

            // 증분: 현재 뷰만 새 조건으로 좁힘(전체 재스캔 안 함). 정렬 순서 유지.
            await RunViewOpAsync(p => _doc.FilterWithinViewAsync(pred, p, _opCts!.Token), "셀값 필터 적용 중...");
            UpdateFilterStatus();
        }

        // 모든 활성 조건(텍스트 + 셀값들)을 AND로 합쳐 뷰를 다시 구성. 필터 변경 시 정렬은 초기화.
        private async Task RebuildFilterAsync(string busyText)
        {
            if (_doc is null) return;
            _sortColumn = -1;
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
            var text = _textCondition;
            var preds = _valueConditions.Select(v => v.pred).ToArray();
            return row =>
            {
                if (text is not null && !text(row)) return false;
                for (int i = 0; i < preds.Length; i++)
                    if (!preds[i](row)) return false;
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
            if (!HasAnyFilter)
            {
                statusLabel.Text = $"필터 없음 · {_doc.DataRowsAvailable:N0} 행";
                return;
            }
            var parts = new List<string>();
            if (_textCondition is not null) parts.Add(_textConditionDesc);
            parts.AddRange(_valueConditions.Select(v => v.desc));
            statusLabel.Text = $"필터({parts.Count}): {string.Join(" AND ", parts)}  →  {_doc.DisplayRowCount:N0} / {_doc.DataRowsAvailable:N0} 행";
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
            _sortColumn = -1;
            ClearSortGlyphs();
            _doc.ClearView();
            grid.RowCount = 0;
            RefreshRowCount();
            grid.Invalidate();
            statusLabel.Text = $"필터 해제 · {_doc.DataRowsAvailable:N0} 행";
        }

        // ---------------------------------------------------------------- Sort (Phase 3)

        private void OnColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || e.ColumnIndex < 0) return;

            if (_sortColumn == e.ColumnIndex) _sortAscending = !_sortAscending;
            else { _sortColumn = e.ColumnIndex; _sortAscending = true; }

            _ = SortAsync();
        }

        // Edit/툴바 ▸ 오름·내림차순 정렬: 현재 셀의 열(없으면 첫 열)을 기준으로 정렬.
        private void OnSortAscMenuClick(object? sender, EventArgs e) => SortCurrentColumn(true);
        private void OnSortDescMenuClick(object? sender, EventArgs e) => SortCurrentColumn(false);

        private void SortCurrentColumn(bool ascending)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            int col = grid.CurrentCell?.ColumnIndex ?? -1;
            if (col < 0) col = grid.Columns.Count > 0 ? 0 : -1;
            if (col < 0) return;
            _sortColumn = col;
            _sortAscending = ascending;
            _ = SortAsync();
        }

        private async Task SortAsync()
        {
            if (_doc is null) return;
            // 정렬은 현재 뷰(필터 결과 또는 전체)를 기준으로 동작.
            // 필터가 적용된 상태에서 정렬하려면 먼저 필터 뷰가 구성돼 있어야 하므로,
            // 필터가 있는데 정렬 기준이 바뀌면 SortAsync가 현재 _viewMap을 재정렬한다.
            await RunViewOpAsync(p => _doc.SortAsync(_sortColumn, _sortAscending, p, _opCts!.Token), "정렬 중...");

            ClearSortGlyphs();
            if (_sortColumn >= 0 && _sortColumn < grid.Columns.Count)
                grid.Columns[_sortColumn].HeaderCell.SortGlyphDirection = _sortAscending ? SortOrder.Ascending : SortOrder.Descending;
            statusLabel.Text = $"정렬: {grid.Columns[_sortColumn].HeaderText} {(_sortAscending ? "오름차순" : "내림차순")} · {_doc.DisplayRowCount:N0} 행";
        }

        private void OnClearSortClick(object? sender, EventArgs e)
        {
            if (_doc is null || _busy) return;
            _sortColumn = -1;
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
                statusLabel.Text = "정렬 해제.";
            }
        }

        private void ClearSortGlyphs()
        {
            foreach (DataGridViewColumn c in grid.Columns)
                c.HeaderCell.SortGlyphDirection = SortOrder.None;
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

            encodingCombo.Enabled = open && !_busy;
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

            RefreshSignal();
        }

        // 상태바 우측 신호등(단일 점): 대기/로딩/작업중/준비완료
        private void RefreshSignal()
        {
            Color c; string t;
            if (_doc is null) { c = Color.Gray; t = "대기"; }
            else if (_indexing) { c = Color.Goldenrod; t = "불러오는 중"; }
            else if (_busy) { c = Color.DarkOrange; t = "작업 중"; }
            else { c = Color.ForestGreen; t = "준비완료"; }
            signalLabel.ForeColor = c;
            signalLabel.Text = "● " + t;
        }

        private void ResetView()
        {
            _textCondition = null;
            _textConditionDesc = "";
            _valueConditions.Clear();
            _sortColumn = -1;
            ClearSortGlyphs();
        }

        private void ResetViewMapOnly()
        {
            _textCondition = null;
            _textConditionDesc = "";
            _valueConditions.Clear();
            _sortColumn = -1;
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
            _doc?.Dispose();
            _detailBoldFont?.Dispose();
            _detailTimer?.Dispose();
            _rowCountTimer?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
