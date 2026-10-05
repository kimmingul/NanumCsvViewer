namespace NanumCsvViewer
{
    // 도구 모음의 단일 구성처. 왼쪽 → 오른쪽:
    //   [열기][작업 공간 저장] | [되돌리기][다시 실행] | 찾기 상자 | 필터 [컬럼][값] | 정렬 ▲ ▼ ✕ | [셀 편집][시트 편집] | [조건부 서식]
    //   …오른쪽 끝: [탐색기][패싯][행 상세][AI] | [설정]
    // 아이콘은 모두 Windows 아이콘 글꼴 글리프(IconGlyphs)로 장치 DPI·테마 글자색에 맞춰 그린다.
    // 툴팁은 "이름 (단축키)" — 단축키 표(CommandShortcuts)에서 온다.
    public partial class Form1
    {
        private ToolStripButton? _saveWsButton, _undoButton, _redoButton, _cfButton, _settingsButton, _facetsButton;
        private readonly List<(ToolStripItem Item, string TextEn, string TextKo, string? CommandId)> _toolbarLabels = new();

        private ToolStripButton IconButton(string name, string glyph, string en, string ko, string? commandId, EventHandler onClick,
            bool right = false, bool checkOnClick = false)
        {
            var b = new ToolStripButton
            {
                Name = name,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                CheckOnClick = checkOnClick,
                Overflow = ToolStripItemOverflow.AsNeeded,
                Alignment = right ? ToolStripItemAlignment.Right : ToolStripItemAlignment.Left,
            };
            b.Click += onClick;
            ToolbarIcon(b, glyph, en, ko, commandId);
            return b;
        }

        /// <summary>기존 항목(디자이너가 만든 버튼)에 글리프 아이콘과 툴팁 라벨을 단다.</summary>
        private void ToolbarIcon(ToolStripItem item, string glyph, string en, string ko, string? commandId)
        {
            item.ImageScaling = ToolStripItemImageScaling.None;
            item.Padding = new Padding(2);
            _toolbarLabels.Add((item, en, ko, commandId));
            IconGlyphs.Attach(item, glyph, _palette.Text, DeviceDpi);
        }

        private void ComposeToolbar()
        {
            toolStrip1.ImageScalingSize = new Size(IconGlyphs.PixelSize(DeviceDpi), IconGlyphs.PixelSize(DeviceDpi));
            toolStrip1.AutoSize = true;

            var open = openToolStripButton;
            ToolbarIcon(open, IconGlyphs.Open, "Open…", "열기…", "file.open");
            _saveWsButton = IconButton("saveWorkspaceButton", IconGlyphs.Save, "Save Workspace", "작업 공간 저장", "file.saveWorkspace",
                (_, _) => SaveWorkspace(saveAs: false));
            _undoButton = IconButton("undoButton", IconGlyphs.Undo, "Undo", "되돌리기", "edit.undo", (_, _) => UndoEdit());
            _redoButton = IconButton("redoButton", IconGlyphs.Redo, "Redo", "다시 실행", "edit.redo", (_, _) => RedoEdit());

            ToolbarIcon(findNextButton, IconGlyphs.FindNext, "Find Next", "다음 찾기", "data.findNext");
            findLabel.DisplayStyle = ToolStripItemDisplayStyle.Image;
            ToolbarIcon(findLabel, IconGlyphs.Find, "Find", "찾기", null);
            filterColumnLabel.DisplayStyle = ToolStripItemDisplayStyle.Image;
            ToolbarIcon(filterColumnLabel, IconGlyphs.Filter, "Filter", "필터", null);
            ToolbarIcon(applyFilterButton, IconGlyphs.Filter, "Apply Filter (Enter)", "필터 적용 (Enter)", null);
            ToolbarIcon(clearFilterButton, IconGlyphs.Clear, "Clear Filter", "필터 해제", "data.clearFilter");
            ToolbarIcon(sortAscButton, IconGlyphs.SortAsc, "Sort Ascending", "오름차순 정렬", null);
            ToolbarIcon(sortDescButton, IconGlyphs.SortDesc, "Sort Descending", "내림차순 정렬", null);
            ToolbarIcon(clearSortButton, IconGlyphs.Clear, "Clear Sort", "정렬 해제", "data.clearSort");

            _editCellButton = IconButton("editCellButton", IconGlyphs.Edit, "Edit Cell…", "셀 편집…", "edit.cell", (_, _) => EditCurrentCell());
            _editSheetButton = IconButton("editSheetButton", IconGlyphs.Sheet, "Sheet Edit Mode", "시트 편집 모드", "edit.sheet",
                (_, _) => SetSheetEditing(!_sheetEditing));
            _cfButton = IconButton("cfButton", IconGlyphs.Palette, "Conditional Formatting…", "조건부 서식…", null, (_, _) => ShowConditionalFormatManager());

            // 우측 정렬 항목은 나중에 추가한 것이 왼쪽에 놓인다: 설정 → 구분선 → AI → 행 상세 → 탐색기 순으로 추가하면
            // 화면에서는 [탐색기][패싯][행 상세][AI] | [설정].
            _settingsButton = IconButton("settingsButton", IconGlyphs.Settings, "Settings…", "설정…", "tools.settings", (_, _) => ShowSettings(), right: true);
            _agentButton = IconButton("agentToggleButton", IconGlyphs.Chat, "AI Agent Panel", "AI 에이전트 패널", "view.ai", (_, _) => { }, right: true, checkOnClick: true);
            _agentButton.CheckedChanged += (_, _) =>
            {
                if (!_syncingAgentToggle && !_syncingPanelToggles) SetAgentPanelVisible(_agentButton.Checked);
            };
            detailToggleButton.Alignment = ToolStripItemAlignment.Right;
            ToolbarIcon(detailToggleButton, IconGlyphs.Detail, CommandShortcuts.En("view.detail"), CommandShortcuts.Ko("view.detail"), "view.detail");
            _facetsButton = IconButton("facetsToggleButton", IconGlyphs.Facets, CommandShortcuts.En("view.facets"), CommandShortcuts.Ko("view.facets"), "view.facets",
                (_, _) => { }, right: true, checkOnClick: true);
            _facetsButton.CheckedChanged += (_, _) =>
            {
                if (!_syncingPanelToggles) SetFacetsVisible(_facetsButton.Checked);
            };
            _wsToolButton = IconButton("workspaceToolButton", IconGlyphs.Explorer, "Workspace Explorer", "작업 공간 탐색기", "view.explorer",
                (_, _) => SetWorkspaceExplorerVisible(!WorkspaceDockVisible), right: true);

            toolStrip1.Items.Clear();
            toolStrip1.Items.AddRange(new ToolStripItem[]
            {
                open, _saveWsButton, Sep(),
                _undoButton, _redoButton, Sep(),
                findLabel, findTextBox, findNextButton, Sep(),
                filterColumnLabel, filterColumnCombo, filterTextBox, applyFilterButton, clearFilterButton, Sep(),
                sortAscButton, sortDescButton, clearSortButton, Sep(),
                _editCellButton, _editSheetButton, Sep(),
                _cfButton,
                _settingsButton, new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right },
                _agentButton, detailToggleButton, _facetsButton, _wsToolButton,
            });
        }

        // 툴바의 모든 글리프를 현재 테마 글자색·DPI로 다시 그린다(ApplyTheme·DPI 변경에서 호출).
        private void RefreshToolbarIcons()
        {
            int px = IconGlyphs.PixelSize(DeviceDpi);
            toolStrip1.ImageScalingSize = new Size(px, px);
            IconGlyphs.Refresh(_palette.Text, DeviceDpi);
            var old = encodingStatusButton.Image;
            encodingStatusButton.ImageScaling = ToolStripItemImageScaling.None;
            encodingStatusButton.Image = IconGlyphs.Render(IconGlyphs.Globe, _palette.Text, DeviceDpi);
            old?.Dispose();
        }

        // 언어 전환: 툴바 글자와 툴팁("이름 (단축키)")을 새 언어로.
        private void LocalizeToolbar()
        {
            foreach (var (item, en, ko, id) in _toolbarLabels)
            {
                string name = LT(en, ko);
                item.Text = name.TrimEnd('…');
                item.ToolTipText = id is null ? name : CommandShortcuts.Tip(name, id);
            }
            findLabel.Text = LT("Find", "찾기");
            filterColumnLabel.Text = LT("Filter", "필터");
            findTextBox.ToolTipText = LT("Find in the data — Enter or F3 jumps to the next match", "데이터에서 찾기 — Enter 또는 F3으로 다음 일치 항목으로 이동");
            findTextBox.TextBox.PlaceholderText = LT("Find…", "찾기…");
            filterColumnCombo.ToolTipText = LT("Column to filter (or all columns)", "필터할 컬럼(또는 모든 컬럼)");
            filterTextBox.ToolTipText = LT("Filter text — Enter applies it to the selected column", "필터 값 — Enter로 선택한 컬럼에 적용");
            filterTextBox.TextBox.PlaceholderText = LT("Filter text…", "필터 값…");
            encodingStatusButton.ToolTipText = LT("Text encoding (click to change)", "텍스트 인코딩 (클릭하여 변경)");
        }
    }
}
