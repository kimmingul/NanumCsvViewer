using System.Collections.ObjectModel;
using System.Diagnostics;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    // 다중 문서 탭(v3). 설계:
    //  · 활성 탭의 상태는 기존대로 Form1 필드(_doc, _sortKeys, _hiddenColumns …)에 올라가 있다 — 그래서 필터·정렬·편집·에이전트 도구가
    //    "현재 탭"에 자동으로 작용한다. 탭을 바꿀 때 필드를 DocumentTab으로 내려 저장(CaptureActiveTab)하고 다음 탭 것을 올린다(RestoreTab).
    //    컬렉션(정렬 키·숨김 열·필터 …)은 탭이 소유한 인스턴스를 필드가 가리키는 방식이라 복사가 없다.
    //  · 문서는 다시 열지 않는다(VirtualCsvDocument·덮개·뷰 맵이 탭에 그대로). 백그라운드 탭은 인덱싱을 계속한다.
    //  · 필터·정렬·분석 같은 "현재 문서 대상 작업"이 도는 중에는 전환을 그 작업이 끝날 때까지 기다린다(작업은 취소하지 않는다).
    //  · 워크북(엑셀·SAS·SPSS·SQLite)은 파일 하나가 탭 하나이고, 시트·테이블 전환은 기존 화면 아래 시트 탭이 맡는다(TabKind.Sheet).
    public partial class Form1
    {
        private readonly List<DocumentTab> _tabs = new();
        private ReadOnlyCollection<DocumentTab>? _tabsView;
        private DocumentTab? _t;                              // 활성 탭(필드에 올라와 있는 탭). 없으면 null
        private readonly SemaphoreSlim _openGate = new(1, 1); // 연속 열기(드롭·다중 선택) 직렬화
        private long _activationCounter;
        private ContextMenuStrip? _tabMenu;
        private ToolStripMenuItem? _closeTabMenu, _closeAllTabsMenu, _nextTabMenu, _prevTabMenu;

        /// <summary>열린 문서 탭(왼쪽→오른쪽 순서). 읽기 전용 실시간 뷰.</summary>
        internal IReadOnlyList<DocumentTab> Tabs => _tabsView ??= _tabs.AsReadOnly();

        /// <summary>지금 보이는 탭. 열린 문서가 없으면 null.</summary>
        internal DocumentTab? ActiveTab => _t;

        /// <summary>탭이 추가·닫힘·전환·이동·이름 변경되면 UI 스레드에서 발생.</summary>
        internal event Action? TabsChanged;

        /// <summary>왼쪽 도킹 영역(작업 공간 탐색기용, 처음엔 비어 있고 접혀 있다). 보이게 하려면 <see cref="WorkspaceDockVisible"/>.</summary>
        internal Panel WorkspaceDockHost => workspaceDockHost;

        /// <summary>왼쪽 도킹 영역과 분할 막대를 함께 펼치거나 접는다.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool WorkspaceDockVisible
        {
            get => _workspaceDockVisible;
            set
            {
                _workspaceDockVisible = value;
                workspaceDockHost.Visible = value;
                workspaceSplitter.Visible = value;
                ApplyDockOrder();
            }
        }
        private bool _workspaceDockVisible;
        private bool _tabStripShown;

        // 도킹은 컨트롤 인덱스가 큰 것부터 처리된다. 보이지 않던 도킹 컨트롤은 폼이 표시될 때 목록 끝(=가장 먼저 도킹)으로 밀려나므로,
        // 보이게 만든 뒤마다 순서를 바로잡는다: 메뉴 > 툴바 > 탭 띠 > 상태바 > 왼쪽 도킹 영역 > 분할 막대 > (칩·품질 띠) > 본문.
        private void ApplyDockOrder()
        {
            var order = new Control[] { menuStrip1, toolStrip1, tabStrip, statusStrip1, workspaceDockHost, workspaceSplitter };
            SuspendLayout();
            try
            {
                for (int k = 0; k < order.Length; k++)
                    Controls.SetChildIndex(order[k], Controls.Count - 1 - k);
            }
            finally { ResumeLayout(true); }
        }

        // 테스트가 대화 상자 없이 확인 흐름을 검증하게 하는 이음매. null이면 실제 MessageBox를 띄운다.
        internal Func<IReadOnlyList<DocumentTab>, bool>? UnsavedTabsConfirm;

        // DocumentTab의 "활성이면 실시간" 읽기용.
        internal VirtualCsvDocument? CurrentDocument => _doc;
        internal Import.WorkbookSession? CurrentWorkbook => _workbook;
        internal bool CurrentIsIndexing => _indexing;
        internal ColumnSummary[] CurrentColumnSummaries => _columnSummaries;
        internal string? CurrentDocumentPath => _currentPath;

        private bool ActiveTabReadOnly => _t?.IsReadOnly == true;

        // ---------------------------------------------------------------- 초기화 · 메뉴

        private void BuildTabFeatures()
        {
            tabStrip.TabActivateRequested += tab => { ActivateTab(tab); grid.Focus(); };
            tabStrip.TabCloseRequested += tab => CloseTab(tab, true);
            tabStrip.TabContextRequested += ShowTabMenu;
            tabStrip.TabMoveRequested += MoveTab;
            tabStrip.AllowDrop = true;
            tabStrip.DragEnter += OnFeatureDragEnter;
            tabStrip.DragDrop += OnFeatureDragDrop;

            int openIdx = fileToolStripMenuItem.DropDownItems.IndexOf(openToolStripMenuItem);
            _closeTabMenu = MakeItem("Close Tab", "탭 닫기", (_, _) => { if (_t is { } t) CloseTab(t, true); });
            _closeTabMenu.ShortcutKeys = Keys.Control | Keys.W;
            _closeAllTabsMenu = MakeItem("Close All Tabs", "모든 탭 닫기", (_, _) => CloseAllTabs(true));
            fileToolStripMenuItem.DropDownItems.Insert(openIdx + 1, _closeAllTabsMenu);
            fileToolStripMenuItem.DropDownItems.Insert(openIdx + 1, _closeTabMenu);

            _nextTabMenu = MakeItem("Next Tab", "다음 탭", (_, _) => CycleTab(+1));
            _nextTabMenu.ShortcutKeyDisplayString = "Ctrl+Tab";
            _prevTabMenu = MakeItem("Previous Tab", "이전 탭", (_, _) => CycleTab(-1));
            _prevTabMenu.ShortcutKeyDisplayString = "Ctrl+Shift+Tab";
            viewToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            viewToolStripMenuItem.DropDownItems.Add(_nextTabMenu);
            viewToolStripMenuItem.DropDownItems.Add(_prevTabMenu);
        }

        private void UpdateTabMenuState()
        {
            bool any = _tabs.Count > 0;
            if (_closeTabMenu is not null) _closeTabMenu.Enabled = any;
            if (_closeAllTabsMenu is not null) _closeAllTabsMenu.Enabled = any;
            bool many = _tabs.Count > 1;
            if (_nextTabMenu is not null) _nextTabMenu.Enabled = many;
            if (_prevTabMenu is not null) _prevTabMenu.Enabled = many;
            tabStrip.Invalidate();
        }

        private void NotifyTabsChanged()
        {
            tabStrip.SetTabs(_tabs, _t);
            bool show = _tabs.Count > 0;
            if (show != _tabStripShown)
            {
                _tabStripShown = show;
                tabStrip.Visible = show;
                ApplyDockOrder();
            }
            UpdateTabMenuState();
            TabsChanged?.Invoke();
        }

        private void CycleTab(int step)
        {
            if (_tabs.Count < 2) return;
            int i = _t is null ? -1 : _tabs.IndexOf(_t);
            int next = ((i < 0 ? 0 : i) + step + _tabs.Count) % _tabs.Count;
            ActivateTab(_tabs[next]);
            grid.Focus();
        }

        private void MoveTab(DocumentTab tab, int toIndex)
        {
            int from = _tabs.IndexOf(tab);
            if (from < 0) return;
            toIndex = Math.Clamp(toIndex, 0, _tabs.Count - 1);
            if (toIndex == from) return;
            _tabs.RemoveAt(from);
            _tabs.Insert(toIndex, tab);
            NotifyTabsChanged();
        }

        /// <summary>열린 모든 탭이 읽는 파일의 전체 경로(원본·워크북 원본·임시 CSV). 어떤 저장도 이 경로를 덮어쓰면 안 된다.</summary>
        internal IReadOnlyList<string> OpenSourcePaths()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? p)
            {
                if (string.IsNullOrEmpty(p)) return;
                try { set.Add(Path.GetFullPath(p)); } catch { /* 잘못된 경로는 무시 */ }
            }
            foreach (var tab in _tabs)
            {
                Add(tab.Path);
                Add(tab.DocumentPath);
                Add(tab.Workbook?.SourcePath);
            }
            return set.ToArray();
        }

        private bool IsOpenInAnyTab(string fullPath)
            => OpenSourcePaths().Contains(fullPath, StringComparer.OrdinalIgnoreCase);

        /// <summary>경로(전체 경로)로 이미 열린 파일 탭을 찾는다. 뷰·결과 탭은 대상이 아니다.</summary>
        internal DocumentTab? FindTab(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch { return null; }
            return _tabs.FirstOrDefault(t => t.Kind is TabKind.File or TabKind.Sheet
                                             && string.Equals(t.Path, full, StringComparison.OrdinalIgnoreCase));
        }

        // ---------------------------------------------------------------- 열기

        // 기존 호출부(시작 인수·클립보드·에이전트·편집 저장 후 열기)가 쓰는 이름. 이제 새 탭을 연다(같은 경로면 그 탭을 활성화).
        private async Task OpenFileAsync(string path) => await OpenFileCoreAsync(path, activate: true);

        /// <summary>파일을 새 탭으로 연다. 같은 경로가 이미 열려 있으면 그 탭을 활성화해 돌려준다. 실패하면 안내하고 null.</summary>
        internal Task<DocumentTab?> OpenFileTabAsync(string path) => OpenFileCoreAsync(path, activate: true);

        /// <summary>여러 파일을 차례로 열고 마지막(이미 열려 있던 것 포함) 탭만 활성화한다. 파일마다 실패는 따로 안내한다.</summary>
        internal async Task OpenFilesAsync(IEnumerable<string> paths)
        {
            DocumentTab? last = null;
            foreach (string path in paths)
                last = await OpenFileCoreAsync(path, activate: false) ?? last;
            if (last is not null && !last.IsClosed) await ActivateTabAsync(last);
        }

        private async Task<DocumentTab?> OpenFileCoreAsync(string path, bool activate)
        {
            if (_closing || IsDisposed) return null;
            // 작업 공간 파일(.ncvws)은 탭이 아니라 작업 공간으로 연다. _openGate를 잡기 전에 분기(그 경로가 OpenFileTabAsync를 다시 부른다).
            if (WorkspaceFile.IsWorkspaceFile(path)) { await OpenWorkspaceFileAsync(path); return null; }
            await _openGate.WaitAsync();
            Import.WorkbookSession? wb = null;
            try
            {
                if (_closing || IsDisposed) return null;
                string full = Path.GetFullPath(path);
                if (FindTab(full) is { } existing)
                {
                    if (activate) await ActivateTabAsync(existing);
                    return existing;
                }

                // 엑셀/SAS/SPSS/SQLite: 시트별 임시 CSV로 변환(UI 스레드 밖). 그동안 화면은 그대로 쓸 수 있다.
                if (Import.TabularImporter.IsImportable(full))
                {
                    statusLabel.Text = LT("Importing…", "불러오는 중…");
                    wb = await Task.Run(() => Import.WorkbookSession.Create(full, _settings.ShowFieldLabels));
                    if (_closing || IsDisposed) return null;
                }
                var tab = CreateFileTab(full, wb);
                wb = null; // 소유권은 탭으로
                if (activate) await ActivateTabAsync(tab);
                return tab;
            }
            catch (Exception ex)
            {
                wb?.Dispose();
                if (!IsDisposed)
                {
                    MessageBox.Show(this, ex.Message, Loc.T("Title_OpenFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    statusLabel.Text = Loc.T("Status_OpenFailed");
                }
                return null;
            }
            finally { _openGate.Release(); }
        }

        // 문서를 열고 탭을 만들어 목록에 넣는다(활성화하지 않음 — 인덱싱은 백그라운드에서 시작). 실패하면 아무것도 바뀌지 않는다.
        private DocumentTab CreateFileTab(string fullPath, Import.WorkbookSession? wb)
        {
            string docPath = wb is null ? fullPath : wb.CsvPath(0);
            var doc = VirtualCsvDocument.Open(docPath);
            string fileName = Path.GetFileName(fullPath);
            var tab = new DocumentTab(this, wb is null ? TabKind.File : TabKind.Sheet, fullPath, fileName, readOnly: false, viewName: null)
            {
                Doc = doc,
                DocPath = docPath,
                Wb = wb,
                SheetIndex = 0,
                SheetName = wb?.SheetNames[0],
                WindowTitle = wb is null ? fileName : $"{fileName}  [{wb.SheetNames[0]}]",
            };
            _tabs.Add(tab);
            StartIndexing(tab, doc);
            NotifyTabsChanged();
            return tab;
        }

        /// <summary>
        /// 만들어 낸 CSV(질의 결과·뷰 테이블)를 새 탭으로 연다. readOnly면 편집·구조 변경·붙여넣기가 모두 거부된다.
        /// 탭은 바로 활성화를 요청한다(현재 탭의 필터·정렬 작업이 도는 중이면 끝난 뒤). UI 스레드에서 호출한다.
        /// </summary>
        internal DocumentTab OpenGeneratedTab(string csvPath, string title, TabKind kind, string? viewName, bool readOnly)
        {
            string full = Path.GetFullPath(csvPath);
            var doc = VirtualCsvDocument.Open(full);
            var tab = new DocumentTab(this, kind, full, title, readOnly, viewName)
            {
                Doc = doc,
                DocPath = full,
                WindowTitle = title,
            };
            _tabs.Add(tab);
            StartIndexing(tab, doc);
            NotifyTabsChanged();
            ActivateTab(tab);
            return tab;
        }

        // ---------------------------------------------------------------- 전환

        /// <summary>탭을 활성화한다. 현재 문서에서 필터·정렬·분석이 도는 중이면 끝난 뒤에 전환한다.</summary>
        internal void ActivateTab(DocumentTab tab)
        {
            if (tab is null || tab.IsClosed || _closing || IsDisposed || !_tabs.Contains(tab) || ReferenceEquals(tab, _t)) return;
            if (ForegroundWorkRunning()) { _ = ActivateTabAsync(tab); return; }
            SwitchTo(tab);
        }

        /// <summary>전환이 끝나면 완료되는 <see cref="ActivateTab"/>.</summary>
        internal async Task ActivateTabAsync(DocumentTab tab)
        {
            if (tab is null || tab.IsClosed || _closing || IsDisposed || !_tabs.Contains(tab) || ReferenceEquals(tab, _t)) return;
            if (ForegroundWorkRunning())
            {
                statusLabel.Text = LT("Waiting for the running operation to finish before switching tabs…", "실행 중인 작업이 끝나면 탭을 전환합니다…");
                await WaitForForegroundIdleAsync();
            }
            if (tab.IsClosed || _closing || IsDisposed || !_tabs.Contains(tab) || ReferenceEquals(tab, _t)) return;
            SwitchTo(tab);
        }

        private void SwitchTo(DocumentTab tab)
        {
            CaptureActiveTab(flushJournal: true);
            RestoreTab(tab);
            NotifyTabsChanged();
        }

        // 현재 문서를 대상으로 도는 작업(필터·정렬·분석·품질 스캔·패싯·저장 …)이 있는가. 인덱싱은 포함하지 않는다(백그라운드로 계속).
        private bool ForegroundWorkRunning()
            => _busy || _drainDepth > 0 || _reimporting
               || Pending(_opTask) || Pending(_findTask) || Pending(_analysisTask)
               || Pending(_qualityTask) || Pending(_facetTask) || Pending(_pivotDrainTask);

        private static bool Pending(Task? t) => t is { IsCompleted: false };

        private async Task WaitForForegroundIdleAsync()
        {
            while (ForegroundWorkRunning() && !_closing && !IsDisposed) await Task.Delay(30);
            // 작업을 await하던 호출부의 후속 코드(상태 갱신 등)가 먼저 끝나도록 메시지 큐를 한 번 비운다.
            await Task.Yield();
            await Task.Delay(1);
        }

        // ---------------------------------------------------------------- 활성 탭 내리기 / 올리기

        private void CaptureActiveTab(bool flushJournal)
        {
            var t = _t;
            if (t is null) return;

            // 진행 중인 셀 편집·시트 편집 모드는 정리(편집 내용 자체는 문서의 덮개에 남는다).
            if (grid.IsCurrentCellInEditMode) { try { grid.EndEdit(); } catch { } }
            if (_sheetEditing) SetSheetEditing(false, silent: true);
            _pendingHeaderSort?.Stop();
            t.SettlePending = _settleTimer.Enabled;
            _settleTimer.Stop();
            if (flushJournal && _doc is not null) FlushJournal(synchronous: true);
            _journalTimer.Stop();
            _cfScaleCts?.Cancel();
            _cfViewKey = (-1, -1, -1);   // 돌아오면 색상 눈금 범위를 다시 계산한다

            // 화면 상태
            t.ColumnWidths = grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToArray();
            t.CurrentRow = grid.CurrentCell?.RowIndex ?? -1;
            t.CurrentColumn = grid.CurrentCell?.ColumnIndex ?? -1;
            t.FirstRow = grid.RowCount > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
            t.HorizontalScroll = grid.HorizontalScrollingOffset;
            t.FilterBoxText = filterTextBox.Text;
            t.FilterColumnIndex = filterColumnCombo.SelectedIndex;
            t.StatusText = statusLabel.Text;

            // 필드 → 탭
            t.Doc = _doc; t.DocPath = _currentPath; t.Wb = _workbook; t.SheetIndex = _currentSheetIndex;
            t.LastIndexMs = _lastIndexMs; t.Summaries = _columnSummaries;
            t.TextCondition = _textCondition; t.TextConditionDesc = _textConditionDesc; t.FilterMatchAny = _filterMatchAny;
            t.UserResizedRowHeader = _userResizedRowHeader;
            t.IndexCts = _indexCts; t.IndexTask = _indexTask; t.Indexing = _indexing;
            t.CfStyler = _cfStyler; t.CfDoc = _cfDoc; t.CfSeenEditsVersion = _cfSeenEditsVersion; t.CfSeenHeaderVersion = _cfSeenHeaderVersion;
            t.CfViewKey = _cfViewKey; t.CfReportedTimeouts = _cfReportedTimeouts; t.CfFailureShown = _cfFailureShown;
            t.QualityReport = _qualityReport; t.QualityFindingsDoc = _qualityFindingsDoc;
            t.RecoveryOfferedFor = _recoveryOfferedFor;
            // 컬렉션(SortKeys·ValueConditions·ColumnFilters·HiddenColumns·ManualTypeOverrides·CfRules·CfHistory·QualityFindings·ChartForms·SurvivalPlots)은
            // 탭이 소유한 인스턴스를 필드가 가리키고 있었으므로 옮길 것이 없다.

            HideTabWindows(t);
            ResetTabFields();
            _t = null;
        }

        // 필드를 "열린 문서 없음" 상태로(탭 소유 컬렉션은 새 빈 인스턴스 — 다음 탭을 올리기 전까지 아무도 쓰지 않는다).
        private void ResetTabFields()
        {
            _doc = null; _currentPath = null; _workbook = null; _currentSheetIndex = 0; _lastIndexMs = 0;
            _columnSummaries = Array.Empty<ColumnSummary>();
            _textCondition = null; _textConditionDesc = ""; _filterMatchAny = false;
            _userResizedRowHeader = false;
            _indexCts = null; _indexTask = null; _indexing = false;
            _cfStyler = new ConditionalFormatStyler(ConditionalFormatSet.Empty); _cfDoc = null;
            _cfSeenEditsVersion = _cfSeenHeaderVersion = -1; _cfViewKey = (-1, -1, -1); _cfReportedTimeouts = 0; _cfFailureShown = 0;
            _qualityReport = null; _qualityFindingsDoc = null;
            _recoveryOfferedFor = null;
            _pendingSelectRowId = -1;
            _sortKeys = new(); _valueConditions = new(); _columnFilters = new(); _hiddenColumns = new(); _manualTypeOverrides = new();
            _cfRules = new(); _cfHistory = new(); _qualityFindings = new(); _chartForms = new(); _survivalPlots = new();
        }

        private void AdoptTabCollections(DocumentTab t)
        {
            _sortKeys = t.SortKeys; _valueConditions = t.ValueConditions; _columnFilters = t.ColumnFilters;
            _hiddenColumns = t.HiddenColumns; _manualTypeOverrides = t.ManualTypeOverrides;
            _cfRules = t.CfRules; _cfHistory = t.CfHistory; _qualityFindings = t.QualityFindings;
            _chartForms = t.ChartForms; _survivalPlots = t.SurvivalPlots;
        }

        private void RestoreTab(DocumentTab tab)
        {
            _t = tab;
            tab.ActivationStamp = ++_activationCounter;
            var doc = tab.Doc;
            if (doc is null) { ShowEmptyUi(); return; }

            // 탭 → 필드
            _doc = doc; _currentPath = tab.DocPath; _workbook = tab.Wb; _currentSheetIndex = tab.SheetIndex;
            _lastIndexMs = tab.LastIndexMs; _columnSummaries = tab.Summaries;
            _textCondition = tab.TextCondition; _textConditionDesc = tab.TextConditionDesc; _filterMatchAny = tab.FilterMatchAny;
            _userResizedRowHeader = tab.UserResizedRowHeader;
            _indexCts = tab.IndexCts; _indexTask = tab.IndexTask; _indexing = tab.Indexing;
            _cfStyler = tab.CfStyler; _cfDoc = tab.CfDoc; _cfSeenEditsVersion = tab.CfSeenEditsVersion; _cfSeenHeaderVersion = tab.CfSeenHeaderVersion;
            _cfViewKey = tab.CfViewKey; _cfReportedTimeouts = tab.CfReportedTimeouts; _cfFailureShown = tab.CfFailureShown;
            _qualityReport = tab.QualityReport; _qualityFindingsDoc = tab.QualityFindingsDoc;
            _recoveryOfferedFor = tab.RecoveryOfferedFor;
            AdoptTabCollections(tab);
            _editTitleSuffix = "";

            grid.SuspendLayout();
            try
            {
                grid.RowCount = 0;
                BuildColumns(doc.Header);
                // 컬럼 삭제·삽입·이동 편집이 있는 문서: 그리드 컬럼 이름은 물리 번호("col"+번호)여야 편집 동기화(SyncGridColumnsWithHeader)가 맞는다.
                int[] physical = doc.Edits.VisiblePhysicalColumns(doc.RawColumnCount);
                if (physical.Length == grid.Columns.Count)
                    for (int i = 0; i < physical.Length; i++) grid.Columns[i].Name = "col" + physical[i];
                if (tab.ColumnWidths is { } widths)
                    for (int i = 0; i < widths.Length && i < grid.Columns.Count; i++) grid.Columns[i].Width = widths[i];
                foreach (int c in _hiddenColumns)
                    if (c >= 0 && c < grid.Columns.Count) grid.Columns[c].Visible = false;
                ApplyColumnTooltips();
                UpdateSortGlyphs();
                if (tab.FilterColumnIndex > 0 && tab.FilterColumnIndex < filterColumnCombo.Items.Count)
                    filterColumnCombo.SelectedIndex = tab.FilterColumnIndex;
                filterTextBox.Text = tab.FilterBoxText;
                SyncEncodingUi(doc.EncodingName);
                Text = $"{ProgramName}  -  {tab.WindowTitle}";
                if (_workbook is not null) { BuildSheetTabs(_workbook); HighlightSheetTab(_currentSheetIndex); }
                else HideSheetTabs();
                UpdateFieldLabelsMenu();
            }
            finally { grid.ResumeLayout(); }

            // 인덱싱 진행 표시
            if (_indexing)
            {
                progressBar.Visible = true;
                progressLabel.Visible = true;
                if (tab.LastProgress is { } p) OnIndexProgress(p);
                else { progressBar.Value = 0; progressLabel.Text = "0%"; statusLabel.Text = Loc.T("Status_Loading"); }
                _rowCountTimer.Start();
            }
            else
            {
                progressBar.Visible = false;
                progressLabel.Visible = false;
                _rowCountTimer.Stop();
            }
            RefreshRowCount();
            RestoreGridPosition(tab);

            // 품질 패널: 이 탭의 발견만 보인다.
            if (_qualityFindings.Count > 0) ShowQualityFindings();
            else if (_qualityPanel is not null) { _qualityPanel.ShowFindings(Array.Empty<Csv.DataQuality.QualityFinding>(), ""); SetQualityPanelVisible(false); }

            ShowTabWindows(tab);
            UpdateFeatureState();

            if (tab.NeedsIndexFinalize)
            {
                tab.NeedsIndexFinalize = false;
                OnIndexingComplete(tab.LastIndexMs);
                if (_facetsVisible) BuildFacets();
            }
            else
            {
                RebuildFilterChips();
                if (!_indexing)
                {
                    if (!HasAnyFilter && _sortKeys.Count > 0)
                    {
                        statusLabel.Text = Loc.F("Status_SortFmt", DescribeSort(), doc.DisplayRowCount.ToString("N0"));
                        if (_facetsVisible) BuildFacets();
                    }
                    else UpdateFilterStatus(); // 필터 상태 문구(+ 패싯 재계산)
                }
            }
            if (tab.SettlePending) { tab.SettlePending = false; _settleTimer.Interval = 60; _settleTimer.Start(); }
            _detailTimer.Stop();
            UpdateDetailPanel();
            PostAgentContext();
            grid.Invalidate();
        }

        private void RestoreGridPosition(DocumentTab tab)
        {
            try
            {
                if (tab.CurrentRow >= 0 && tab.CurrentRow < grid.RowCount && tab.CurrentColumn >= 0 && tab.CurrentColumn < grid.Columns.Count
                    && grid.Columns[tab.CurrentColumn].Visible)
                    grid.CurrentCell = grid[tab.CurrentColumn, tab.CurrentRow];
                else OnCurrentCellChanged(grid, EventArgs.Empty);
                if (tab.FirstRow >= 0 && tab.FirstRow < grid.RowCount) grid.FirstDisplayedScrollingRowIndex = tab.FirstRow;
                if (tab.HorizontalScroll > 0) grid.HorizontalScrollingOffset = tab.HorizontalScroll;
            }
            catch (Exception ex) { Debug.WriteLine($"[Tabs] restore position: {ex.Message}"); }
        }

        // 열린 문서가 하나도 없을 때의 화면.
        private void ShowEmptyUi()
        {
            _rowCountTimer.Stop();
            progressBar.Visible = false;
            progressLabel.Visible = false;
            grid.RowCount = 0;
            grid.Columns.Clear();
            filterColumnCombo.Items.Clear();
            filterTextBox.Text = "";
            cellAddressBox.Text = "";
            cellValueTextBox.Text = "";
            detailRichText.Clear();
            detailHeaderLabel.Text = Loc.T("Detail_Header");
            _editTitleSuffix = "";
            Text = ProgramName;
            HideSheetTabs();
            if (_qualityPanel is not null) { _qualityPanel.ShowFindings(Array.Empty<Csv.DataQuality.QualityFinding>(), ""); SetQualityPanelVisible(false); }
            RebuildFilterChips();
            UpdateFeatureState();
            statusLabel.Text = Loc.T("Status_OpenPrompt");
            PostAgentContext();
            UpdateFieldLabelsMenu();
        }

        // 이 탭이 연 비모달 창(차트·고급 분석 결과·생존 곡선)은 탭을 떠나면 숨기고 돌아오면 다시 보인다.
        private void HideTabWindows(DocumentTab t)
        {
            t.HiddenWindows.Clear();
            foreach (var f in OwnedForms.ToArray())
            {
                if (f.IsDisposed || !f.Visible) continue;
                if (f is AdvancedResultForm or ChartForm || t.SurvivalPlots.Contains(f))
                {
                    t.HiddenWindows.Add(f);
                    f.Hide();
                }
            }
        }

        private void ShowTabWindows(DocumentTab t)
        {
            foreach (var f in t.HiddenWindows.ToArray())
                if (!f.IsDisposed) { try { f.Show(this); } catch (Exception ex) { Debug.WriteLine($"[Tabs] show window: {ex.Message}"); } }
            t.HiddenWindows.Clear();
        }

        private static void CloseTabWindows(DocumentTab t)
        {
            foreach (var f in t.HiddenWindows.ToArray())
                if (!f.IsDisposed) { try { f.Close(); } catch { } }
            t.HiddenWindows.Clear();
        }

        // ---------------------------------------------------------------- 닫기

        /// <summary>
        /// 탭을 닫는다. askUnsaved면 저장하지 않은 편집이 있을 때 확인하고(아니오면 false), 아니면 확인 없이 닫는다
        /// (그 경우 복구 저널은 남겨 다음에 복구할 수 있다). 활성 탭에서 작업이 도는 중이면 끝난 뒤 닫는다.
        /// 문서 해제는 인덱싱 스레드가 멈춘 뒤 이뤄진다(<see cref="DocumentTab"/>의 DisposeCompletion).
        /// </summary>
        internal bool CloseTab(DocumentTab tab, bool askUnsaved)
        {
            if (tab is null || tab.IsClosed || !_tabs.Contains(tab)) return false;
            bool discarded = false;
            if (askUnsaved && tab.HasUnsavedEdits)
            {
                if (!ConfirmDiscardTabs(new[] { tab })) return false;
                discarded = true;
            }
            CloseConfirmed(tab, flushJournal: !discarded);
            return true;
        }

        private void CloseConfirmed(DocumentTab tab, bool flushJournal)
        {
            if (!ReferenceEquals(tab, _t))
            {
                _tabs.Remove(tab);
                BeginDisposeTab(tab);
                NotifyTabsChanged();
                return;
            }
            if (ForegroundWorkRunning()) { _ = CloseActiveAfterIdleAsync(tab, flushJournal); return; }
            CloseActiveNow(tab, flushJournal);
        }

        private async Task CloseActiveAfterIdleAsync(DocumentTab tab, bool flushJournal)
        {
            statusLabel.Text = LT("Waiting for the running operation to finish before closing the tab…", "실행 중인 작업이 끝나면 탭을 닫습니다…");
            await WaitForForegroundIdleAsync();
            if (tab.IsClosed || _closing || IsDisposed || !_tabs.Contains(tab)) return;
            if (!ReferenceEquals(tab, _t)) { CloseConfirmed(tab, flushJournal); return; }
            CloseActiveNow(tab, flushJournal);
        }

        private void CloseActiveNow(DocumentTab tab, bool flushJournal)
        {
            int idx = _tabs.IndexOf(tab);
            CaptureActiveTab(flushJournal);
            _tabs.Remove(tab);
            // 오른쪽 이웃, 없으면 왼쪽 이웃(브라우저와 같은 규칙)
            DocumentTab? next = _tabs.Count == 0 ? null : _tabs[Math.Min(idx, _tabs.Count - 1)];
            BeginDisposeTab(tab);
            if (next is not null) RestoreTab(next); else ShowEmptyUi();
            NotifyTabsChanged();
        }

        // 탭이 가진 문서·워크북을 해제한다. 인덱싱 스레드가 멈춘 뒤에 해제(옛 문서를 읽는 스레드가 없게).
        private void BeginDisposeTab(DocumentTab tab)
        {
            tab.IsClosed = true;
            CloseTabWindows(tab);
            tab.IndexCts?.Cancel();
            tab.DisposeCompletion = ReleaseTabResourcesAsync(tab.Doc, tab.Wb, tab.DocPath, tab.IndexTask);
        }

        private async Task ReleaseTabResourcesAsync(VirtualCsvDocument? doc, Import.WorkbookSession? wb, string? docPath, Task? indexTask)
        {
            if (indexTask is not null)
            {
                try { await indexTask; } catch { /* 취소·오류는 인덱싱 쪽에서 이미 처리 */ }
            }
            try { if (_settings.DeleteIndexOnClose && docPath is not null) IndexCache.DeleteFor(docPath); } catch { }
            try { doc?.Dispose(); } catch { }
            try { wb?.Dispose(); } catch { }
        }

        /// <summary>모든 탭을 닫는다(저장 안 한 편집이 있는 탭들을 한 번에 묻는다). 취소하면 아무것도 닫지 않고 false.</summary>
        internal bool CloseAllTabs(bool askUnsaved)
        {
            var all = _tabs.ToArray();
            var dirty = all.Where(t => t.HasUnsavedEdits).ToList();
            bool discarded = false;
            if (askUnsaved && dirty.Count > 0)
            {
                if (!ConfirmDiscardTabs(dirty)) return false;
                discarded = true;
            }
            foreach (var tab in all.Where(t => !ReferenceEquals(t, _t))) CloseConfirmed(tab, flushJournal: !discarded);
            if (_t is { } active) CloseConfirmed(active, flushJournal: !discarded);
            return true;
        }

        private bool CloseOtherTabs(DocumentTab keep)
        {
            var others = _tabs.Where(t => !ReferenceEquals(t, keep)).ToArray();
            var dirty = others.Where(t => t.HasUnsavedEdits).ToList();
            if (dirty.Count > 0 && !ConfirmDiscardTabs(dirty)) return false;
            ActivateTab(keep);
            foreach (var tab in others) CloseConfirmed(tab, flushJournal: !dirty.Contains(tab));
            return true;
        }

        /// <summary>저장하지 않은 편집이 있는 탭들을 나열하며 버릴지 묻는다. 목록이 비면 true. 예 = 복구 저널도 지운다.</summary>
        private bool ConfirmDiscardTabs(IReadOnlyList<DocumentTab> dirty)
        {
            if (dirty.Count == 0) return true;
            bool ok;
            if (UnsavedTabsConfirm is { } hook) ok = hook(dirty);
            else
            {
                var lines = dirty.Select(t => $"  • {t.DisplayName} ({EditSummary(t.Document?.Edits)})");
                string list = string.Join("\n", lines);
                string text = dirty.Count == 1
                    ? LT($"There are unsaved edits in '{dirty[0].DisplayName}' ({EditSummary(dirty[0].Document?.Edits)}). Discard them?\n(Use Edit ▸ Save Edits As… to keep them.)",
                         $"'{dirty[0].DisplayName}'에 저장하지 않은 편집이 있습니다({EditSummary(dirty[0].Document?.Edits)}). 버릴까요?\n(보존하려면 편집 ▸ 편집 내용 저장…을 먼저 사용하세요.)")
                    : LT($"{dirty.Count} tabs have unsaved edits:\n{list}\n\nDiscard them?\n(Use Edit ▸ Save Edits As… to keep them.)",
                         $"저장하지 않은 편집이 있는 탭이 {dirty.Count}개 있습니다:\n{list}\n\n모두 버릴까요?\n(보존하려면 편집 ▸ 편집 내용 저장…을 먼저 사용하세요.)");
                ok = MessageBox.Show(this, text, ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            if (!ok) return false;
            foreach (var tab in dirty) DeleteJournalFor(tab);
            return true;
        }

        private void DeleteJournalFor(DocumentTab tab)
        {
            if (ReferenceEquals(tab, _t)) { DeleteJournal(); return; }
            string? src = tab.Wb?.SourcePath ?? tab.DocPath;
            string? key = src is null ? null : EditJournal.KeyFor(src, tab.Wb is null ? 0 : tab.SheetIndex);
            if (key is null) return;
            try { EditJournal.Delete(EditJournal.DefaultDirectory, key); } catch (Exception ex) { Debug.WriteLine($"[EditJournal] {ex.Message}"); }
        }

        // 앱 종료: 모든 탭의 인덱싱을 멈추고 끝날 때까지 기다린다(활성 탭 것은 CancelAndDrainAsync가 처리).
        private async Task DrainBackgroundTabsAsync()
        {
            var tasks = new List<Task>();
            foreach (var tab in _tabs)
            {
                if (ReferenceEquals(tab, _t)) continue;
                tab.IndexCts?.Cancel();
                if (tab.IndexTask is not null) tasks.Add(tab.IndexTask);
            }
            if (tasks.Count == 0) return;
            try { await Task.WhenAll(tasks); } catch { }
        }

        // 폼이 닫힐 때: 모든 탭의 문서·워크북 해제와 인덱스 캐시 정리.
        private void DisposeAllTabs()
        {
            var docs = new HashSet<VirtualCsvDocument>();
            foreach (var tab in _tabs.ToArray())
            {
                CloseTabWindows(tab);
                tab.IndexCts?.Cancel();
                var doc = tab.Document;
                string? docPath = tab.DocumentPath;
                var wb = tab.Workbook;
                try { if (_settings.DeleteIndexOnClose && docPath is not null) IndexCache.DeleteFor(docPath); } catch { }
                if (doc is not null) { docs.Add(doc); try { doc.Dispose(); } catch { } }
                try { wb?.Dispose(); } catch { }
                tab.IsClosed = true;
            }
            _tabs.Clear();
            if (_doc is not null && !docs.Contains(_doc)) { try { _doc.Dispose(); } catch { } } // 탭 없이 문서만 올린 경우(테스트)
        }

        // ---------------------------------------------------------------- 탭 오른쪽 클릭 메뉴

        private void ShowTabMenu(DocumentTab tab, Point screenPoint)
        {
            _tabMenu?.Dispose();
            var menu = new ContextMenuStrip();
            menu.Items.Add(LT("Close", "닫기"), null, (_, _) => CloseTab(tab, true));
            menu.Items.Add(LT("Close Others", "다른 탭 닫기"), null, (_, _) => CloseOtherTabs(tab)).Enabled = _tabs.Count > 1;
            menu.Items.Add(LT("Close All", "모두 닫기"), null, (_, _) => CloseAllTabs(true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(LT("Copy Path", "경로 복사"), null, (_, _) =>
            {
                try { Clipboard.SetText(tab.Path); }
                catch (Exception ex) { Debug.WriteLine($"[Tabs] clipboard: {ex.Message}"); }
            });
            var openFolder = menu.Items.Add(LT("Open Folder", "폴더 열기"), null, (_, _) => OpenTabFolder(tab));
            openFolder.Enabled = File.Exists(tab.Path);
            AddWorkspaceTabMenuItems(menu, tab);
            _tabMenu = menu;
            menu.Show(screenPoint);
        }

        private static void OpenTabFolder(DocumentTab tab)
        {
            try
            {
                if (!File.Exists(tab.Path)) return;
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{tab.Path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Debug.WriteLine($"[Tabs] open folder: {ex.Message}"); }
        }

        // ---------------------------------------------------------------- 읽기 전용 탭 / 에이전트

        /// <summary>활성 탭이 읽기 전용(뷰 테이블·질의 결과)이면 편집 시도를 거부한다. 에이전트 편집 도구도 이걸 부른다.</summary>
        internal void RequireEditableTab()
        {
            if (ActiveTabReadOnly)
                throw new InvalidOperationException(LT("This tab is read-only (a view table or query result). Edits are not allowed.",
                    "이 탭은 읽기 전용입니다(뷰 테이블 또는 질의 결과). 편집할 수 없습니다."));
        }
    }
}
