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
        private readonly System.Windows.Forms.Timer _rowCountTimer;

        private bool _suppressEncodingEvent;
        private bool _busy;

        // 뷰 상태(필터/정렬 합성)
        private Func<string[], bool>? _activeFilter;
        private int _sortColumn = -1;
        private bool _sortAscending = true;

        // 멀티라인 셀 행 높이 계산용
        private int _singleLineHeight = 22;
        private int _lineHeight = 18;
        private const int MaxCellLines = 6;

        public Form1()
        {
            InitializeComponent();
            Text = ProgramName;

            encodingCombo.Items.AddRange(EncodingDetector.SelectableNames);

            // 셀 내 줄바꿈을 여러 줄로 표시
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _lineHeight = grid.Font.Height + 2;
            _singleLineHeight = Math.Max(grid.RowTemplate.Height, _lineHeight + 6);

            try { splitContainer1.SplitterDistance = 26; } catch { /* 초기 크기에 따라 무시 */ }

            _rowCountTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _rowCountTimer.Tick += (_, _) => RefreshRowCount();

            UpdateFeatureState();
            statusLabel.Text = "파일을 여세요 (File ▸ Open).";
        }

        // ---------------------------------------------------------------- Open

        private void OnOpenClick(object? sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog(this) != DialogResult.OK) return;
            OpenFile(openFileDialog1.FileName);
        }

        private void OpenFile(string path)
        {
            try
            {
                CancelAll();
                var old = _doc;
                _doc = null;
                old?.Dispose();

                ResetView();
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
            progressBar.Visible = true;
            progressBar.Value = 0;
            progressLabel.Visible = true;
            progressLabel.Text = "0%";
            statusLabel.Text = "불러오는 중...";
            _rowCountTimer.Start();

            var progress = new Progress<IndexProgress>(OnIndexProgress);
            _ = RunIndexingAsync(progress);
        }

        private async Task RunIndexingAsync(IProgress<IndexProgress> progress)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await _doc!.RunIndexingAsync(progress, _indexCts!.Token);
                sw.Stop();
                _rowCountTimer.Stop();
                RefreshRowCount();
                OnIndexingComplete(sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _rowCountTimer.Stop();
            }
            catch (Exception ex)
            {
                _rowCountTimer.Stop();
                progressBar.Visible = false;
                progressLabel.Visible = false;
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
            if (_doc is null) return;
            // 콤마 없는 자릿수 기준, 폭을 종전의 약 절반으로
            int digits = Math.Max(2, _doc.DataRowsAvailable.ToString().Length);
            int w = Math.Max(28, 10 + digits * 4);
            if (grid.RowHeadersWidth != w)
            {
                try { grid.RowHeadersWidth = w; } catch { }
            }
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
            catch
            {
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
            int lines = 1;
            try
            {
                foreach (string f in _doc.GetDisplayRow(e.RowIndex))
                {
                    int c = 1;
                    foreach (char ch in f) if (ch == '\n') c++;
                    if (c > lines) lines = c;
                }
            }
            catch { }
            if (lines > MaxCellLines) lines = MaxCellLines;
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
            catch { }

            if (!outerSplit.Panel2Collapsed) UpdateDetailPanel();
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
            catch { return; }

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
        }

        private void OnEncodingChanged(object? sender, EventArgs e)
        {
            if (_suppressEncodingEvent || _doc is null) return;
            if (encodingCombo.SelectedItem is not string name) return;

            _doc.ChangeEncoding(name);
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

        private async Task FindNextAsync()
        {
            if (_doc is null || _busy) return;
            string term = findTextBox.Text;
            if (string.IsNullOrEmpty(term)) return;

            int total = _doc.DisplayRowCount;
            if (total == 0) return;
            int start = (grid.CurrentCell?.RowIndex ?? -1) + 1;

            SetBusy(true);
            statusLabel.Text = $"'{term}' 검색 중...";
            int found = await Task.Run(() => SearchForward(term, start, total));
            SetBusy(false);

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

        private int SearchForward(string term, int start, int total)
        {
            // start..total-1 후 0..start-1 (랩어라운드)
            for (int i = start; i < total; i++)
                if (RowContains(i, term)) return i;
            for (int i = 0; i < start && i < total; i++)
                if (RowContains(i, term)) return i;
            return -1;
        }

        private bool RowContains(int viewRow, string term)
        {
            string[] row = _doc!.GetDisplayRow(viewRow);
            foreach (string f in row)
                if (f.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- Filter (Phase 3)

        private void OnFilterKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = ApplyFilterAsync(); }
        }

        private void OnApplyFilterClick(object? sender, EventArgs e) => _ = ApplyFilterAsync();

        private async Task ApplyFilterAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;

            string term = filterTextBox.Text;
            if (string.IsNullOrEmpty(term))
            {
                OnClearFilterClick(this, EventArgs.Empty);
                return;
            }

            int sel = filterColumnCombo.SelectedIndex;       // 0 = 모든 컬럼
            int col = sel - 1;                                // 특정 컬럼 인덱스(-1이면 전체)
            _activeFilter = BuildPredicate(term, col);

            // 새 필터는 정렬을 초기화
            _sortColumn = -1;
            ClearSortGlyphs();

            await RunViewOpAsync(p => _doc.ApplyFilterAsync(_activeFilter!, p, _opCts!.Token), "필터 적용 중...");
            statusLabel.Text = $"필터 적용: {_doc.DisplayRowCount:N0} / {_doc.DataRowsAvailable:N0} 행";
        }

        private static Func<string[], bool> BuildPredicate(string term, int col)
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

        private void OnClearFilterClick(object? sender, EventArgs e)
        {
            if (_doc is null) return;
            _activeFilter = null;
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
            // 정렬만 해제: 필터가 있으면 필터 뷰를 다시 구성, 없으면 전체 보기.
            if (_activeFilter is not null)
            {
                _ = RunViewOpAsync(p => _doc.ApplyFilterAsync(_activeFilter!, p, _opCts!.Token), "정렬 해제 중...");
            }
            else
            {
                _doc.ClearView();
                grid.RowCount = 0;
                RefreshRowCount();
                grid.Invalidate();
            }
            statusLabel.Text = "정렬 해제.";
        }

        private void ClearSortGlyphs()
        {
            foreach (DataGridViewColumn c in grid.Columns)
                c.HeaderCell.SortGlyphDirection = SortOrder.None;
        }

        // ---------------------------------------------------------------- Shared op runner

        private async Task RunViewOpAsync(Func<IProgress<int>, Task> op, string busyText)
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
            findTextBox.Enabled = open && !_busy;
            findNextButton.Enabled = open && !_busy;

            filterColumnCombo.Enabled = ready;
            filterTextBox.Enabled = ready;
            applyFilterButton.Enabled = ready;
            clearFilterButton.Enabled = ready;
            clearFilterToolStripMenuItem.Enabled = ready;
            clearSortToolStripMenuItem.Enabled = ready;
        }

        private void ResetView()
        {
            _activeFilter = null;
            _sortColumn = -1;
            ClearSortGlyphs();
        }

        private void ResetViewMapOnly()
        {
            _activeFilter = null;
            _sortColumn = -1;
            ClearSortGlyphs();
            _doc?.ClearView();
        }

        private void CancelAll()
        {
            _rowCountTimer.Stop();
            _indexCts?.Cancel();
            _opCts?.Cancel();
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
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CancelAll();
            _doc?.Dispose();
            _detailBoldFont?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
