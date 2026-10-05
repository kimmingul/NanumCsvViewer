namespace NanumCsvViewer
{
    // 메뉴 막대의 단일 구성처. 10개 최상위 메뉴: 파일 · 편집 · 보기 · 데이터 · 작업 공간 · 통계 · 시각화 · 품질 · 도구 · 도움말.
    //
    // 항목 자체(핸들러·활성 상태)는 기능별 부분 파일의 Build*가 만들어 필드에 둔다. 이 파일은 그 항목들을 한 곳에서 메뉴 트리로
    // 조립해, 메뉴 구조를 바꾸려면 여기만 보면 되게 한다. 이름·단축키는 CommandShortcuts 표가 단일 소스다.
    // 모든 항목은 RegisterLabel/MakeCmd로 등록되므로 언어를 바꾸면 LocalizeFeatureMenus가 한 번에 다시 라벨링한다.
    public partial class Form1
    {
        private ToolStripMenuItem? _fileMenu, _editMenu, _viewMenu, _dataMenu, _statsMenu, _toolsMenu, _helpMenu;
        private ToolStripMenuItem? _openMenu, _quitMenu, _closeOthersMenu, _copyMenu, _findMenu, _findNextMenu, _applyFilterMenu, _filterByCellMenu,
            _clearFilterMenu, _sortAscMenu, _sortDescMenu, _clearSortMenu, _rowsMenu, _colsMenu, _columnTypeMenu, _facetsMenu, _cellBarMenu,
            _viewFindingsMenu, _themeMenu, _settingsMenu, _usageMenu, _shortcutsMenu, _aboutMenu;

        // 디자이너 대신 코드에서 만드는 항목. 생성자 초반(BuildEncodingMenu·BuildLanguageMenu)부터 쓰이므로 필드 초기화로 만든다.
        private readonly ToolStripMenuItem encodingMenuItem = new() { Name = "encodingMenuItem" };
        private readonly ToolStripMenuItem languageMenuItem = new() { Name = "languageMenuItem" };
        private readonly ToolStripMenuItem detailPanelMenuItem = new() { Name = "detailPanelMenuItem", CheckOnClick = true };

        /// <summary>메뉴 막대에 있는 최상위 메뉴(왼쪽→오른쪽). 테스트·구조 점검용.</summary>
        internal IReadOnlyList<ToolStripMenuItem> TopLevelMenus => menuStrip1.Items.OfType<ToolStripMenuItem>().ToList();

        private static ToolStripSeparator Sep() => new();

        private ToolStripMenuItem Top(string name, string en, string ko)
        {
            var m = new ToolStripMenuItem { Name = name };
            RegisterLabel(m, en, ko);
            return m;
        }

        /// <summary>
        /// 단축키 표에 있는 명령의 메뉴 항목. 이름(영/한)·단축키 표시는 CommandShortcuts에서 오고, Tag/Name은 명령 Id다.
        /// 표에서 DisplayOnly가 아닌 키는 항목에 바인딩되어 메뉴가 닫혀 있어도 동작하고, DisplayOnly는 표시만 한다
        /// (ProcessCmdKey·그리드 키 처리기가 직접 받는 키).
        /// </summary>
        private ToolStripMenuItem MakeCmd(string id, EventHandler handler)
        {
            var e = CommandShortcuts.Get(id);
            // 메뉴 글자의 & 는 단축 글자 표시이므로 글자 그대로 보이려면 이중으로 쓴다.
            var item = MakeItem(e.En.Replace("&", "&&"), e.Ko.Replace("&", "&&"), handler);
            item.Name = id;
            item.Tag = id;
            item.ShortcutKeyDisplayString = e.KeyText;
            if (!e.DisplayOnly && e.Keys != Keys.None) item.ShortcutKeys = e.Keys;
            return item;
        }

        private static void Fill(ToolStripMenuItem parent, params ToolStripItem?[] items)
        {
            foreach (var item in items)
                if (item is not null) parent.DropDownItems.Add(item);
        }

        private ToolStripMenuItem Sub(string en, string ko, params ToolStripItem?[] items)
        {
            var m = new ToolStripMenuItem();
            RegisterLabel(m, en, ko);
            Fill(m, items);
            return m;
        }

        // Build*가 모두 끝난 뒤(생성자) 한 번 호출.
        private void ComposeMainMenu()
        {
            _fileMenu = Top("fileMenu", "File", "파일");
            _editMenu = Top("editMenu", "Edit", "편집");
            _viewMenu = Top("viewMenu", "View", "보기");
            _dataMenu = Top("dataMenu", "Data", "데이터");
            _statsMenu = _advMenu!; // 통계 = 기본 분석 + 고급 통계(BuildAdvancedStatsMenu가 만든 하위 메뉴들)
            _toolsMenu = Top("toolsMenu", "Tools", "도구");
            _helpMenu = Top("helpMenu", "Help", "도움말");

            // ---- 파일
            _openMenu = MakeCmd("file.open", OnOpenClick);
            _quitMenu = MakeCmd("file.quit", OnQuitClick);
            _closeOthersMenu = MakeItem("Close Other Tabs", "다른 탭 닫기", (_, _) => { if (_t is { } t) CloseOtherTabs(t); });
            Fill(_fileMenu,
                _openMenu, _clipboardOpenMenu, Sep(),
                _closeTabMenu, _closeOthersMenu, _closeAllTabsMenu, Sep(),
                _wfNewMenu, _wfOpenMenu, _wfRecentMenu, _wfSaveMenu, _wfSaveAsMenu, _wfCloseMenu, Sep(),
                _exportMenu, Sep(),
                _quitMenu);
            _fileMenu.DropDownOpening += (_, _) => UpdateCloseWorkspaceMenu();

            // ---- 편집
            _copyMenu = MakeCmd("edit.copy", (_, _) => CopySelectedCells());
            _rowsMenu = Sub("Rows", "행", _insertAboveMenu, _insertBelowMenu, _deleteRowsMenu, Sep(), _copyRowMenu);
            _colsMenu = Sub("Columns", "컬럼", _renameColumnMenu, _insertColumnMenu, _moveColumnLeftMenu, _moveColumnRightMenu, _deleteColumnMenu,
                Sep(), _extractColumnMenu, _copyColMenu);
            Fill(_editMenu,
                _undoMenu, _redoMenu, Sep(),
                _editCellMenu, _editSheetMenu, Sep(),
                _copyMenu, _pasteMenu, _clearCellsMenu, Sep(),
                _regexReplaceMenu, Sep(),
                _rowsMenu, _colsMenu, Sep(),
                _revertCellMenu, _saveEditsMenu, _discardEditsMenu);

            // ---- 데이터
            _findMenu = MakeCmd("data.find", OnFindMenuClick);
            _findNextMenu = MakeCmd("data.findNext", OnFindNextClick);
            _applyFilterMenu = MakeItem("Apply Filter", "필터 적용", OnApplyFilterClick);
            _filterByCellMenu = MakeCmd("data.filterByCell", OnFilterByCellClick);
            _clearFilterMenu = MakeCmd("data.clearFilter", OnClearFilterClick);
            _sortAscMenu = MakeItem("Sort Ascending", "오름차순 정렬", OnSortAscMenuClick);
            _sortDescMenu = MakeItem("Sort Descending", "내림차순 정렬", OnSortDescMenuClick);
            _clearSortMenu = MakeCmd("data.clearSort", OnClearSortClick);
            _columnTypeMenu = Sub("Column Type", "컬럼 타입");
            _columnTypeMenu.DropDownOpening += (_, _) => FillColumnTypeMenu(_columnTypeMenu);
            Fill(_dataMenu,
                _findMenu, _findNextMenu, Sep(),
                _applyFilterMenu, _filterByCellMenu, _advFilterMenu, _clearFilterMenu, Sep(),
                _sortAscMenu, _sortDescMenu, _clearSortMenu, Sep(),
                _gotoRowMenu, Sep(),
                _columnsMenu, _columnTypeMenu, encodingMenuItem, Sep(),
                _saveViewMenu, _restoreViewMenu, Sep(),
                _pivotTableMenu);
            RegisterLabel(encodingMenuItem, "Encoding", "인코딩");

            // ---- 보기
            _cellBarMenu = MakeItem("Cell Value Bar", "셀 값 표시줄", (_, _) => SetPanelVisible(PanelKind.CellBar, !IsPanelVisible(PanelKind.CellBar)));
            _viewFindingsMenu = MakeItem("Findings Panel", "검사 결과 패널", (_, _) => ToggleQualityPanel());
            detailPanelMenuItem.Name = "view.detail";
            detailPanelMenuItem.Tag = "view.detail";
            detailPanelMenuItem.ShortcutKeys = CommandShortcuts.KeysOf("view.detail");
            detailPanelMenuItem.ShortcutKeyDisplayString = CommandShortcuts.Get("view.detail").KeyText;
            detailPanelMenuItem.CheckedChanged += OnDetailMenuChanged;
            RegisterLabel(detailPanelMenuItem, CommandShortcuts.En("view.detail"), CommandShortcuts.Ko("view.detail"));
            _themeMenu = Sub("Theme", "테마", ThemeItem("", "System", "시스템"), ThemeItem("Light", "Light", "밝게"), ThemeItem("Dark", "Dark", "어둡게"));
            _themeMenu.DropDownOpening += (_, _) => SyncLanguageThemeMenus();
            languageMenuItem.DropDownOpening += (_, _) => SyncLanguageThemeMenus();
            RegisterLabel(languageMenuItem, "Language", "언어");
            Fill(_viewMenu,
                detailPanelMenuItem, _agentPanelMenu, _viewExplorerMenu, _facetsMenu, _viewFindingsMenu, _cellBarMenu, Sep(),
                _nextTabMenu, _prevTabMenu, Sep(),
                _showBadgesMenu, _fieldLabelsMenu, Sep(),
                _cfMenu, _cfUndoMenu, _cfRedoMenu, Sep(),
                _themeMenu, languageMenuItem);
            _viewMenu.DropDownOpening += (_, _) => SyncPanelToggles();

            // ---- 도구
            _settingsMenu = MakeCmd("tools.settings", (_, _) => ShowSettings());
            _pythonBundleMenu = MakeCmd("tools.exportPythonBundle", (_, _) => ExportPythonBundle());
            Fill(_toolsMenu, _settingsMenu, Sep(), _pythonBundleMenu, Sep(), _perfMenu, _indexCacheMenu);

            // ---- 도움말
            _usageMenu = MakeCmd("help.usage", OnUsageClick);
            _shortcutsMenu = MakeItem("Keyboard Shortcuts…", "단축키…", (_, _) => ShowSettings("shortcuts"));
            _aboutMenu = MakeItem("About", "정보", OnAboutClick);
            Fill(_helpMenu, _usageMenu, _shortcutsMenu, Sep(), _aboutMenu);

            menuStrip1.Items.AddRange(new ToolStripItem[]
            {
                _fileMenu, _editMenu, _viewMenu, _dataMenu, _wsMenu!, _statsMenu, _vizMenu!, _qualityMenu!, _toolsMenu, _helpMenu,
            });

            // 패널 표시가 바뀌면(사용자 조작 · 시작 레이아웃 · 작업 공간 복원) 메뉴 체크와 툴바 토글을 같이 맞춘다.
            PanelVisibilityChanged += (_, _) => SyncPanelToggles();
        }

        // 단일 상태 소스에서 패널 토글 항목(메뉴 체크·툴바 눌림)을 맞춘다. View 메뉴를 열 때와 패널 표시가 바뀔 때 호출.
        private void SyncPanelToggles()
        {
            _syncingPanelToggles = true;
            try
            {
                bool detail = IsPanelVisible(PanelKind.Detail), agent = IsPanelVisible(PanelKind.Agent), explorer = IsPanelVisible(PanelKind.Explorer);
                if (detailPanelMenuItem.Checked != detail) detailPanelMenuItem.Checked = detail;
                if (detailToggleButton.Checked != detail) detailToggleButton.Checked = detail;
                if (_agentPanelMenu is not null) _agentPanelMenu.Checked = agent;
                if (_agentButton is not null && _agentButton.Checked != agent) _agentButton.Checked = agent;
                if (_viewExplorerMenu is not null) _viewExplorerMenu.Checked = explorer;
                if (_wsToolButton is not null) _wsToolButton.Checked = explorer;
                bool facets = IsPanelVisible(PanelKind.Facets);
                if (_facetsMenu is not null) _facetsMenu.Checked = facets;
                if (_facetsButton is not null && _facetsButton.Checked != facets) _facetsButton.Checked = facets;
                bool findings = IsPanelVisible(PanelKind.Findings);
                if (_viewFindingsMenu is not null) _viewFindingsMenu.Checked = findings;
                if (_qualityPanelMenu is not null) _qualityPanelMenu.Checked = findings;
                if (_cellBarMenu is not null) _cellBarMenu.Checked = IsPanelVisible(PanelKind.CellBar);
            }
            finally { _syncingPanelToggles = false; }
        }

        private bool _syncingPanelToggles;

        // 문서 상태에 따른 메뉴·툴바 활성. Form1.UpdateFeatureState가 호출한다.
        private void UpdateChromeEnabledState(bool open, bool ready)
        {
            bool free = open && !_busy;
            encodingStatusButton.Enabled = free;
            encodingMenuItem.Enabled = free;
            findTextBox.Enabled = free;
            findNextButton.Enabled = free;
            _findMenu!.Enabled = free;
            _findNextMenu!.Enabled = free;

            filterColumnCombo.Enabled = ready;
            filterTextBox.Enabled = ready;
            applyFilterButton.Enabled = ready;
            clearFilterButton.Enabled = ready;
            _applyFilterMenu!.Enabled = ready;
            _filterByCellMenu!.Enabled = ready;
            _clearFilterMenu!.Enabled = ready;
            _clearSortMenu!.Enabled = ready;
            _sortAscMenu!.Enabled = ready;
            _sortDescMenu!.Enabled = ready;
            sortAscButton.Enabled = ready;
            sortDescButton.Enabled = ready;
            clearSortButton.Enabled = ready;
            if (_columnTypeMenu is not null) _columnTypeMenu.Enabled = ready;
        }

        // 데이터 ▸ 컬럼 타입: 현재 셀의 컬럼 기준으로 열릴 때마다 채운다(헤더 우클릭 메뉴와 같은 항목).
        private void FillColumnTypeMenu(ToolStripMenuItem parent)
        {
            parent.DropDownItems.Clear();
            int col = grid.CurrentCell?.ColumnIndex ?? -1;
            if (_doc is null || col < 0 || col >= _columnSummaries.Length || !_doc.IndexingComplete || _busy)
            {
                parent.DropDownItems.Add(new ToolStripMenuItem(LT("(select a cell first)", "(먼저 셀을 선택하세요)")) { Enabled = false });
                return;
            }
            foreach (ToolStripItem item in BuildTypeItems(col).ToArray()) parent.DropDownItems.Add(item);
        }

        private ToolStripMenuItem ThemeItem(string setting, string en, string ko)
        {
            var item = MakeItem(en, ko, (_, _) => ApplyThemeSetting(setting));
            item.Tag = setting;
            return item;
        }

        // 보기 ▸ 테마·언어의 체크와 언어 항목 글자를 현재 설정에 맞춘다(언어를 바꾸거나 메뉴를 열 때).
        private void SyncLanguageThemeMenus()
        {
            string language = _settings.Language is "en" or "ko" ? _settings.Language : "auto";
            foreach (ToolStripItem it in languageMenuItem.DropDownItems)
                if (it is ToolStripMenuItem { Tag: string code } mi)
                {
                    mi.Text = code switch { "ko" => Loc.T("Lang_Korean"), "en" => Loc.T("Lang_English"), _ => LT("Auto (follow Windows)", "자동 (Windows 언어)") };
                    mi.Checked = code == language;
                }
            if (_themeMenu is null) return;
            string theme = _settings.Theme is "Light" or "Dark" ? _settings.Theme : "";
            foreach (ToolStripItem it in _themeMenu.DropDownItems)
                if (it is ToolStripMenuItem { Tag: string code } mi) mi.Checked = code == theme;
        }
    }
}
