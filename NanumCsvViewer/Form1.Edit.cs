using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // ---------------------------------------------------------------- 편집 (보기 전용이 기본)
    //
    // 기본은 읽기 전용 뷰어다. 데이터가 함부로 바뀌지 않도록 편집은 별도 버튼으로만 켠다:
    //  - 셀 편집: 선택한 셀 1개를 대화상자에서 고치면 바로 보기 모드로 돌아온다.
    //  - 시트 편집 모드: 켜 두는 동안 여러 셀을 인라인 편집·붙여넣기·지우기, 컬럼 이름 변경, 행 삽입/삭제를 한다. 끄면 보기 모드.
    // 편집은 원본 파일에 쓰지 않고 덮개(CellEdits)에만 쌓이며, "편집 내용 저장…"으로 새 파일에 저장한다.
    // 값은 항상 문자열 그대로 다룬다(숫자 변환 없음) — 001의 선행 0이 사라지지 않는다.
    // 모든 편집은 되돌리기/다시 실행(Ctrl+Z / Ctrl+Y) 대상이다. 보조 기능은 Form1.Edit.Ops.cs.
    public partial class Form1
    {
        private ToolStripMenuItem? _editCellMenu, _editSheetMenu, _saveEditsMenu, _revertCellMenu, _discardEditsMenu;
        private ToolStripMenuItem? _undoMenu, _redoMenu, _pasteMenu, _clearCellsMenu, _renameColumnMenu,
            _insertAboveMenu, _insertBelowMenu, _deleteRowsMenu, _regexReplaceMenu, _extractColumnMenu,
            _insertColumnMenu, _deleteColumnMenu, _moveColumnLeftMenu, _moveColumnRightMenu;
        private readonly List<ToolStripItem> _editContextItems = new();
        private ToolStripButton? _editCellButton, _editSheetButton;
        private bool _sheetEditing;
        private bool _editWired;
        private string _editTitleSuffix = "";
        private CellEdits? _watchedEdits;
        private long _seenStructureVersion, _seenHeaderVersion;

        private bool HasUnsavedEdits => _doc is not null && !_doc.Edits.IsEmpty && _doc.Edits.IsDirty;

        private bool EditsReady => _doc is not null && _doc.IndexingComplete && !_busy && !ActiveTabReadOnly;

        private void BuildEditFeatures()
        {
            editToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _undoMenu = MakeItem("Undo", "되돌리기", (_, _) => UndoEdit());
            _undoMenu.ShortcutKeyDisplayString = "Ctrl+Z";
            _redoMenu = MakeItem("Redo", "다시 실행", (_, _) => RedoEdit());
            _redoMenu.ShortcutKeyDisplayString = "Ctrl+Y";
            _editCellMenu = MakeItem("Edit Cell…", "셀 편집…", (_, _) => EditCurrentCell());
            _editCellMenu.ShortcutKeys = Keys.F2;
            _editSheetMenu = MakeItem("Sheet Edit Mode", "시트 편집 모드", (_, _) => SetSheetEditing(!_sheetEditing));
            _editSheetMenu.ShortcutKeys = Keys.Control | Keys.Shift | Keys.E;
            _pasteMenu = MakeItem("Paste Cells", "셀 붙여넣기", (_, _) => PasteFromClipboard());
            _pasteMenu.ShortcutKeyDisplayString = "Ctrl+V";
            _clearCellsMenu = MakeItem("Clear Selected Cells", "선택한 셀 지우기", (_, _) => ClearSelectedCells());
            _clearCellsMenu.ShortcutKeyDisplayString = "Del";
            _regexReplaceMenu = MakeItem("Find && Replace (regex)…", "찾아 바꾸기 (정규식)…", async (_, _) => await RegexReplaceAsync());
            _regexReplaceMenu.ShortcutKeys = Keys.Control | Keys.H;
            _extractColumnMenu = MakeItem("Extract to New Column (regex)…", "정규식으로 새 컬럼에 추출…", async (_, _) => await ExtractColumnAsync());
            _renameColumnMenu = MakeItem("Rename Column…", "컬럼 이름 변경…", (_, _) => RenameCurrentColumn());
            _insertColumnMenu = MakeItem("Insert Column…", "컬럼 삽입…", (_, _) => InsertColumnFromUi());
            _deleteColumnMenu = MakeItem("Delete Column", "컬럼 삭제", (_, _) => { if (grid.CurrentCell is { ColumnIndex: >= 0 } c) DeleteColumnFromUi(c.ColumnIndex); });
            _moveColumnLeftMenu = MakeItem("Move Column Left", "컬럼 왼쪽으로 이동", (_, _) => MoveCurrentColumn(-1));
            _moveColumnRightMenu = MakeItem("Move Column Right", "컬럼 오른쪽으로 이동", (_, _) => MoveCurrentColumn(+1));
            _insertAboveMenu = MakeItem("Insert Row Above", "위에 행 삽입", (_, _) => InsertRow(above: true));
            _insertBelowMenu = MakeItem("Insert Row Below", "아래에 행 삽입", (_, _) => InsertRow(above: false));
            _deleteRowsMenu = MakeItem("Delete Selected Rows", "선택한 행 삭제", (_, _) => DeleteSelectedRows());
            _revertCellMenu = MakeItem("Revert This Cell", "이 셀 편집 되돌리기", (_, _) => RevertCurrentCell());
            _saveEditsMenu = MakeItem("Save Edits As…", "편집 내용 저장…", async (_, _) => await SaveEditsAsync());
            _discardEditsMenu = MakeItem("Discard All Edits", "모든 편집 버리기", (_, _) => DiscardAllEdits());
            foreach (var m in new ToolStripItem?[]
                     {
                         _undoMenu, _redoMenu, new ToolStripSeparator(),
                         _editCellMenu, _editSheetMenu, _pasteMenu, _clearCellsMenu, _regexReplaceMenu, _extractColumnMenu, _renameColumnMenu,
                         _insertColumnMenu, _moveColumnLeftMenu, _moveColumnRightMenu, _deleteColumnMenu,
                         _insertAboveMenu, _insertBelowMenu, _deleteRowsMenu, new ToolStripSeparator(),
                         _revertCellMenu, _saveEditsMenu, _discardEditsMenu,
                     })
                editToolStripMenuItem.DropDownItems.Add(m!);

            // 셀 우클릭 메뉴(시트 편집 모드에서만 보임)
            gridContextMenu.Items.Add(new ToolStripSeparator());
            _editContextItems.Add(gridContextMenu.Items[^1]);
            foreach (var (en, ko, act) in new (string, string, Action)[]
                     {
                         ("Paste Cells", "셀 붙여넣기", PasteFromClipboard),
                         ("Clear Selected Cells", "선택한 셀 지우기", ClearSelectedCells),
                         ("Insert Row Above", "위에 행 삽입", () => InsertRow(above: true)),
                         ("Insert Row Below", "아래에 행 삽입", () => InsertRow(above: false)),
                         ("Delete Selected Rows", "선택한 행 삭제", DeleteSelectedRows),
                         ("Find && Replace (regex)…", "찾아 바꾸기 (정규식)…", () => _ = RegexReplaceAsync()),
                         ("Extract to New Column (regex)…", "정규식으로 새 컬럼에 추출…", () => _ = ExtractColumnAsync()),
                         ("Insert Column…", "컬럼 삽입…", InsertColumnFromUi),
                         ("Move Column Left", "컬럼 왼쪽으로 이동", () => MoveCurrentColumn(-1)),
                         ("Move Column Right", "컬럼 오른쪽으로 이동", () => MoveCurrentColumn(+1)),
                         ("Delete Column", "컬럼 삭제", () => { if (grid.CurrentCell is { ColumnIndex: >= 0 } c) DeleteColumnFromUi(c.ColumnIndex); }),
                     })
            {
                var item = MakeItem(en, ko, (_, _) => act());
                gridContextMenu.Items.Add(item);
                _editContextItems.Add(item);
            }

            // 별도 편집 버튼(툴바). 보기 모드에서는 둘 다 꺼져 있다.
            _editCellButton = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text, Name = "editCellButton",
                Alignment = ToolStripItemAlignment.Right, Overflow = ToolStripItemOverflow.Never,
            };
            _editCellButton.Click += (_, _) => EditCurrentCell();
            _editSheetButton = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = false, Name = "editSheetButton",
                Alignment = ToolStripItemAlignment.Right, Overflow = ToolStripItemOverflow.Never,
            };
            _editSheetButton.Click += (_, _) => SetSheetEditing(!_sheetEditing);
            // 우측 정렬은 나중에 추가한 것이 왼쪽에 놓인다: [셀][시트] 순서로 보이게 시트를 먼저 추가.
            toolStrip1.Items.Add(_editSheetButton);
            toolStrip1.Items.Add(_editCellButton);

            _settleTimer.Tick += async (_, _) => await SettleAfterEditAsync();
            _journalTimer.Tick += (_, _) => FlushJournal();
            WireAddressBox();
            BuildFormatFeatures();
            Shown += (_, _) => grid.CellContextMenuStripNeeded += OnEditHeaderMenuNeeded; // 타입 메뉴(Features)가 만든 뒤에 항목을 덧붙이려면 그 핸들러보다 늦게 연결해야 한다

            if (_editWired) return;
            _editWired = true;
            grid.CellFormatting += OnEditCellFormatting;
            grid.CellValuePushed += OnEditCellValuePushed;
            grid.EditingControlShowing += OnEditControlShowing;
            grid.CellDoubleClick += OnEditCellDoubleClick;
            grid.ColumnHeaderMouseDoubleClick += OnEditHeaderDoubleClick;
            grid.MouseDown += OnColumnDragMouseDown;
            grid.MouseMove += OnColumnDragMouseMove;
            grid.MouseUp += OnColumnDragMouseUp;
            grid.KeyDown += OnColumnDragKeyDown;
            grid.Paint += OnColumnDragPaint;
        }

        private void LocalizeEditButtons()
        {
            if (_editCellButton is not null)
            {
                _editCellButton.Text = LT("✎ Cell", "✎ 셀");
                _editCellButton.ToolTipText = LT("Edit Cell — edit the selected cell; returns to view mode right after (F2)", "셀 편집 — 선택한 셀 하나를 편집하고 바로 보기 모드로 돌아옵니다 (F2)");
            }
            if (_editSheetButton is not null)
            {
                _editSheetButton.Text = _sheetEditing ? LT("✎ Sheet ON", "✎ 시트 ON") : LT("✎ Sheet", "✎ 시트");
                _editSheetButton.ToolTipText = LT("Sheet Edit Mode — turn on/off editing of many cells, paste, rename columns, insert/delete rows (off = view mode)", "시트 편집 모드 — 여러 셀 편집·붙여넣기·컬럼 이름 변경·행 삽입/삭제를 켜고 끕니다(끄면 보기 모드)");
                _editSheetButton.Checked = _sheetEditing;
            }
            RefreshHistoryMenuText();
        }

        // Form1.UpdateFeatureMenuState()에서 호출.
        private void UpdateEditState()
        {
            bool ready = EditsReady;
            bool hasCell = ready && grid.CurrentCell is { RowIndex: >= 0, ColumnIndex: >= 0 };
            WatchEdits();
            if (_editCellMenu is not null) _editCellMenu.Enabled = hasCell && !_sheetEditing;
            if (_editCellButton is not null) _editCellButton.Enabled = hasCell && !_sheetEditing;
            if (_editSheetMenu is not null) { _editSheetMenu.Enabled = ready; _editSheetMenu.Checked = _sheetEditing; }
            if (_editSheetButton is not null) _editSheetButton.Enabled = ready;
            bool sheet = ready && _sheetEditing;
            bool structure = sheet && _doc!.CanEditStructure;
            if (_pasteMenu is not null) _pasteMenu.Enabled = sheet && hasCell;
            if (_clearCellsMenu is not null) _clearCellsMenu.Enabled = sheet && hasCell;
            // 정규식 바꾸기·추출은 보기 모드에서도 눌러 볼 수 있다: 거부 이유를 상태 표시줄에 알린다(붙여넣기와 같음).
            if (_regexReplaceMenu is not null) _regexReplaceMenu.Enabled = ready;
            if (_extractColumnMenu is not null) _extractColumnMenu.Enabled = ready;
            if (_renameColumnMenu is not null) _renameColumnMenu.Enabled = sheet && hasCell;
            if (_insertColumnMenu is not null) _insertColumnMenu.Enabled = sheet;
            int curCol = hasCell ? grid.CurrentCell!.ColumnIndex : -1;
            if (_moveColumnLeftMenu is not null) _moveColumnLeftMenu.Enabled = sheet && hasCell && AdjacentVisibleColumn(curCol, -1) >= 0;
            if (_moveColumnRightMenu is not null) _moveColumnRightMenu.Enabled = sheet && hasCell && AdjacentVisibleColumn(curCol, +1) >= 0;
            if (_deleteColumnMenu is not null) _deleteColumnMenu.Enabled = sheet && hasCell && _doc!.ColumnCount > 1;
            UpdateFormatState();
            if (_insertAboveMenu is not null) _insertAboveMenu.Enabled = structure;
            if (_insertBelowMenu is not null) _insertBelowMenu.Enabled = structure;
            if (_deleteRowsMenu is not null) _deleteRowsMenu.Enabled = structure && hasCell;
            foreach (var item in _editContextItems) { item.Visible = _sheetEditing; item.Enabled = sheet; }
            UpdateEditStateMenusOnly();
            LocalizeEditButtons();
            UpdateEditTitle();
            OfferRecoveryWhenReady();
        }

        private void UpdateEditStateMenusOnly()
        {
            bool ready = EditsReady;
            bool any = _doc is not null && !_doc.Edits.IsEmpty;
            if (_saveEditsMenu is not null) _saveEditsMenu.Enabled = ready && any;
            if (_discardEditsMenu is not null) _discardEditsMenu.Enabled = ready && any;
            if (_revertCellMenu is not null)
                _revertCellMenu.Enabled = ready && grid.CurrentCell is { RowIndex: >= 0 } && CurrentCellIsEdited();
            if (_undoMenu is not null) _undoMenu.Enabled = ready && _doc!.Edits.CanUndo;
            if (_redoMenu is not null) _redoMenu.Enabled = ready && _doc!.Edits.CanRedo;
            RefreshHistoryMenuText();
        }

        private void RefreshHistoryMenuText()
        {
            var edits = _doc?.Edits;
            if (_undoMenu is not null)
                _undoMenu.Text = edits?.UndoDescription is { Length: > 0 } u ? LT("Undo: ", "되돌리기: ") + u : LT("Undo", "되돌리기");
            if (_redoMenu is not null)
                _redoMenu.Text = edits?.RedoDescription is { Length: > 0 } r ? LT("Redo: ", "다시 실행: ") + r : LT("Redo", "다시 실행");
        }

        // 문서마다 덮개가 새로 생기므로 변경 알림을 문서에 맞춰 다시 건다.
        private void WatchEdits()
        {
            var edits = _doc?.Edits;
            if (ReferenceEquals(edits, _watchedEdits)) return;
            if (_watchedEdits is not null) _watchedEdits.Changed -= OnEditsChanged;
            _watchedEdits = edits;
            _settleTimer.Stop();
            _journalTimer.Stop();
            if (edits is not null)
            {
                edits.Changed += OnEditsChanged;
                _seenStructureVersion = edits.StructureVersion;
                _seenHeaderVersion = edits.HeaderVersion;
            }
            ResetJournalForDocument();
        }

        private void UpdateEditTitle()
        {
            tabStrip.Invalidate(); // 탭의 "저장 안 한 편집" 점
            string suffix = (_sheetEditing ? LT("   ✎ SHEET EDIT MODE", "   ✎ 시트 편집 모드") : "")
                          + (HasUnsavedEdits ? "   *" + LT("edited", "편집됨") : "");
            if (suffix == _editTitleSuffix) return;
            if (_editTitleSuffix.Length > 0 && Text.EndsWith(_editTitleSuffix, StringComparison.Ordinal))
                Text = Text[..^_editTitleSuffix.Length];
            _editTitleSuffix = suffix;
            if (suffix.Length > 0) Text += suffix;
        }

        // LoadDocument에서 호출: 새 문서는 항상 보기 모드로 시작한다.
        private void ResetEditUi()
        {
            if (_sheetEditing) SetSheetEditing(false, silent: true);
            if (_editTitleSuffix.Length > 0 && Text.EndsWith(_editTitleSuffix, StringComparison.Ordinal))
                Text = Text[..^_editTitleSuffix.Length];
            _editTitleSuffix = "";
            _pendingSelectRowId = -1;
        }

        /// <summary>저장하지 않은 편집이 있으면 버릴지 묻는다. false면 호출자는 동작을 중단한다. 버리면 복구 저널도 지운다.</summary>
        private bool ConfirmDiscardEdits()
        {
            if (!HasUnsavedEdits) return true;
            var r = MessageBox.Show(this,
                LT($"There are unsaved edits ({EditSummary()}). Discard them?\n(Use Edit ▸ Save Edits As… to keep them.)",
                   $"저장하지 않은 편집이 있습니다({EditSummary()}). 버릴까요?\n(보존하려면 편집 ▸ 편집 내용 저장…을 먼저 사용하세요.)"),
                ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return false;
            DeleteJournal();
            return true;
        }

        /// <summary>현재 편집 요약(예: "3 cells, 1 column name, 2 deleted rows").</summary>
        private string EditSummary() => EditSummary(_doc?.Edits);

        private static string EditSummary(CellEdits? e)
        {
            if (e is null) return "";
            var parts = new List<string>();
            if (e.Count > 0) parts.Add(LT($"{e.Count:N0} cell(s)", $"셀 {e.Count:N0}개"));
            if (e.HeaderEditCount > 0) parts.Add(LT($"{e.HeaderEditCount:N0} column name(s)", $"컬럼 이름 {e.HeaderEditCount:N0}개"));
            if (e.DeletedCount > 0) parts.Add(LT($"{e.DeletedCount:N0} deleted row(s)", $"삭제 행 {e.DeletedCount:N0}개"));
            if (e.AddedCount > 0) parts.Add(LT($"{e.AddedCount:N0} added row(s)", $"추가 행 {e.AddedCount:N0}개"));
            if (e.AppendedColumnCount > 0) parts.Add(LT($"{e.AppendedColumnCount:N0} new column(s)", $"새 컬럼 {e.AppendedColumnCount:N0}개"));
            if (e.DeletedColumnCount > 0) parts.Add(LT($"{e.DeletedColumnCount:N0} deleted column(s)", $"삭제 컬럼 {e.DeletedColumnCount:N0}개"));
            if (e.HasColumnOrder) parts.Add(LT("columns reordered", "컬럼 순서 변경"));
            return string.Join(LT(", ", ", "), parts);
        }

        // ---------------------------------------------------------------- 단일 셀 편집

        private void EditCurrentCell()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || _sheetEditing || ActiveTabReadOnly) return;
            if (grid.CurrentCell is not { RowIndex: >= 0, ColumnIndex: >= 0 } cell) return;
            int viewRow = cell.RowIndex, col = cell.ColumnIndex;
            int rowId = _doc.GetRowId(viewRow);
            if (rowId < 0) return;

            string[] row = _doc.GetDisplayRow(viewRow);
            string current = col < row.Length ? row[col] : "";
            var originalRow = _doc.GetOriginalRow(rowId);
            string original = col < originalRow.Length ? originalRow[col] : "";
            string colName = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";
            long rowNumber = _doc.GetSourceRowNumber(viewRow);

            using var dlg = new CellEditDialog(colName, rowNumber, current, original, _doc.Edits.Contains(rowId, col), _palette);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            CommitCellEdit(viewRow, col, dlg.Value);
            // 단일 셀 편집은 적용 즉시 보기 모드 — 따로 켜 둔 상태가 없다.
            statusLabel.Text = LT($"Cell edited (row {rowNumber:N0}, {colName}). Back in view mode — save with Edit ▸ Save Edits As….",
                                  $"셀을 편집했습니다(행 {rowNumber:N0}, {colName}). 보기 모드로 돌아왔습니다 — 편집 ▸ 편집 내용 저장…으로 저장하세요.");
        }

        /// <summary>텍스트를 그대로 덮개에 기록(한 단계). 줄바꿈은 원래 값의 스타일(CRLF/LF)에 맞춘다.</summary>
        private void CommitCellEdit(int viewRow, int col, string text)
        {
            if (_doc is null || ActiveTabReadOnly) return;
            int rowId = _doc.GetRowId(viewRow);
            if (rowId < 0) return;
            var originalRow = _doc.GetOriginalRow(rowId);
            string original = col < originalRow.Length ? originalRow[col] : "";
            text = MatchNewlineStyle(text, original);
            _doc.Edits.Set(rowId, col, text, original); // 변경이 있으면 한 단계
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
        }

        private static string MatchNewlineStyle(string text, string original) => CellEdits.MatchNewlineStyle(text, original);

        private bool CurrentCellIsEdited()
        {
            if (_doc is null || grid.CurrentCell is not { RowIndex: >= 0 } c) return false;
            int rowId = _doc.GetRowId(c.RowIndex);
            return rowId >= 0 && _doc.Edits.Contains(rowId, c.ColumnIndex);
        }

        private void RevertCurrentCell()
        {
            if (_doc is null || grid.CurrentCell is not { RowIndex: >= 0 } c) return;
            int rowId = _doc.GetRowId(c.RowIndex);
            if (rowId < 0) return;
            _doc.Edits.Revert(rowId, c.ColumnIndex);
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
        }

        private void DiscardAllEdits()
        {
            if (_doc is null || _doc.Edits.IsEmpty) return;
            if (MessageBox.Show(this,
                    LT($"Discard all edits ({EditSummary()})?\n(Edit ▸ Undo brings them back.)",
                       $"모든 편집({EditSummary()})을 버릴까요?\n(편집 ▸ 되돌리기로 되살릴 수 있습니다.)"),
                    ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _doc.Edits.Clear();
            _doc.Edits.MarkSaved(); // 원본과 같아졌으니 저장할 것이 없다
            DeleteJournal();
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
        }

        // ---------------------------------------------------------------- 시트 편집 모드

        private void SetSheetEditing(bool on, bool silent = false)
        {
            if (on == _sheetEditing) return;
            if (on)
            {
                if (_doc is null || !_doc.IndexingComplete || _busy) return;
                if (ActiveTabReadOnly)
                {
                    statusLabel.Text = LT("This tab is read-only (a view table or query result) — editing is not available.",
                                          "이 탭은 읽기 전용입니다(뷰 테이블 또는 질의 결과) — 편집할 수 없습니다.");
                    return;
                }
                if (MessageBox.Show(this,
                        LT("Turn on sheet edit mode?\n\nCells can be edited, pasted and cleared, columns renamed and rows inserted or deleted until you turn it off. Edits are kept in memory (Ctrl+Z undoes them) and never written to the original file; save them with Edit ▸ Save Edits As… (a new file).\nValues are stored exactly as typed (001 stays 001).",
                           "시트 편집 모드를 켤까요?\n\n끌 때까지 셀 편집·붙여넣기·지우기, 컬럼 이름 변경, 행 삽입/삭제를 할 수 있습니다. 편집은 메모리에만 쌓이고(Ctrl+Z로 되돌림) 원본 파일에는 쓰이지 않으며, 편집 ▸ 편집 내용 저장…으로 새 파일에 저장합니다.\n값은 입력한 그대로 저장됩니다(001은 001 그대로)."),
                        ProgramName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            }
            else if (grid.IsCurrentCellInEditMode) grid.EndEdit();

            _sheetEditing = on;
            grid.ReadOnly = !on;
            grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            if (!silent)
            {
                statusLabel.Text = on
                    ? LT("Sheet edit mode ON — double-click or press F2 on a cell to edit (Enter commits, Esc cancels); Ctrl+V pastes, Del clears, double-click a header to rename, Ctrl+Z undoes.",
                         "시트 편집 모드 켜짐 — 셀을 더블클릭하거나 F2로 편집합니다(Enter 확정, Esc 취소). Ctrl+V 붙여넣기, Del 지우기, 헤더 더블클릭 이름 변경, Ctrl+Z 되돌리기.")
                    : LT("Back in view mode.", "보기 모드로 돌아왔습니다.");
                UpdateFeatureState();
            }
            else LocalizeEditButtons();
        }

        private void OnEditCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (!_sheetEditing || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            grid.CurrentCell = grid[e.ColumnIndex, e.RowIndex];
            grid.BeginEdit(false);
        }

        private void OnEditHeaderDoubleClick(object? sender, DataGridViewCellMouseEventArgs e)
        {
            if (!_sheetEditing || e.ColumnIndex < 0 || e.Button != MouseButtons.Left) return;
            _pendingHeaderSort?.Stop();
            RenameColumn(e.ColumnIndex);
        }

        // 시트 편집 모드 단축키. 입력란(필터·찾기)이나 셀 인라인 편집 중에는 그 입력란의 자체 동작(텍스트 되돌리기 등)을 쓴다.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 탭 전환: Ctrl+Tab / Ctrl+Shift+Tab (그리드·입력란 어디에 포커스가 있어도)
            if (keyData == (Keys.Control | Keys.Tab)) { CycleTab(+1); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { CycleTab(-1); return true; }
            if (_doc is not null && grid.Focused && !grid.IsCurrentCellInEditMode)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.Z:
                        UndoEdit();
                        return true;
                    case Keys.Control | Keys.Y:
                    case Keys.Control | Keys.Shift | Keys.Z:
                        RedoEdit();
                        return true;
                    case Keys.Control | Keys.V:
                        PasteFromClipboard();
                        return true;
                    case Keys.Delete when _sheetEditing:
                        ClearSelectedCells();
                        return true;
                }
            }
            if (_sheetEditing && keyData == Keys.F2 && grid.Focused && !grid.IsCurrentCellInEditMode && grid.CurrentCell is { RowIndex: >= 0 })
            {
                grid.BeginEdit(false);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 인라인 편집기는 표시 문자열(잘린 미리보기·통화/퍼센트 스킨) 대신 전체 원본 값으로 시작해야 한다.
        private void OnEditControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
        {
            if (!_sheetEditing || _doc is null || e.Control is not TextBox tb) return;
            if (grid.CurrentCell is not { RowIndex: >= 0, ColumnIndex: >= 0 } c) return;
            string[] row = _doc.GetDisplayRow(c.RowIndex);
            string raw = c.ColumnIndex < row.Length ? row[c.ColumnIndex] : "";
            tb.MaxLength = 0;
            tb.Text = raw.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            tb.SelectAll();
        }

        // 가상 모드 커밋. e.Value는 문자열 그대로(컬럼 ValueType 없음 → 숫자 파싱 없음).
        private void OnEditCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
        {
            if (!_sheetEditing || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            CommitCellEdit(e.RowIndex, e.ColumnIndex, Convert.ToString(e.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }

        // 셀 배경 우선순위: 편집한 셀(앰버) > 추가 컬럼 값(연한 파랑) > 삽입 행(연한 초록) > 조건부 서식 규칙. 규칙의 글자색·굵게는 항상 적용(Form1.Format.cs).
        private void OnEditCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (_doc is null || e.RowIndex < 0 || e.ColumnIndex < 0 || e.CellStyle is null) return;
            var cf = ConditionalFormatFor(_doc, e.RowIndex, e.ColumnIndex);
            bool systemBack = false;
            if (!_doc.Edits.IsEmpty)
            {
                int rowId = _doc.GetRowId(e.RowIndex);
                if (rowId >= 0)
                {
                    if (_doc.Edits.IsAppendedColumn(e.ColumnIndex))
                    {
                        // 정규식 추출 등으로 만든 컬럼: 값이 있는 셀을 "편집됨" 앰버 대신 연한 파랑으로 구분한다.
                        if (_doc.Edits.Contains(rowId, e.ColumnIndex))
                        {
                            e.CellStyle.BackColor = _theme == AppTheme.Dark ? Color.FromArgb(28, 58, 92) : Color.FromArgb(214, 232, 250);
                            systemBack = true;
                        }
                    }
                    else if (_doc.Edits.Contains(rowId, e.ColumnIndex))
                    {
                        e.CellStyle.BackColor = _theme == AppTheme.Dark ? Color.FromArgb(96, 78, 16) : Color.FromArgb(255, 238, 186);
                        systemBack = true;
                    }
                    else if (rowId >= _doc.BaseRowCount)
                    {
                        e.CellStyle.BackColor = _theme == AppTheme.Dark ? Color.FromArgb(30, 74, 44) : Color.FromArgb(214, 240, 220);
                        systemBack = true;
                    }
                }
            }
            if (!cf.IsEmpty) ApplyFormatToCell(e, cf, systemBack);
        }

        // 헤더 우클릭 메뉴(Features의 타입 메뉴)에 시트 편집 모드의 컬럼 항목을 덧붙인다. 타입 메뉴가 없으면(요약 계산 전) 항목만 있는 메뉴를 만든다.
        private ContextMenuStrip? _editHeaderMenu;

        private void OnEditHeaderMenuNeeded(object? sender, DataGridViewCellContextMenuStripNeededEventArgs e)
        {
            if (e.RowIndex != -1 || e.ColumnIndex < 0 || !_sheetEditing || _doc is null || !_doc.IndexingComplete || _busy) return;
            var menu = e.ContextMenuStrip;
            if (menu is null)
            {
                _editHeaderMenu?.Dispose();
                menu = _editHeaderMenu = new ContextMenuStrip();
                e.ContextMenuStrip = menu;
            }
            int col = e.ColumnIndex;
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(LT("Rename Column…", "컬럼 이름 변경…"), null, (_, _) => RenameColumn(col));
            menu.Items.Add(LT("Insert Column…", "컬럼 삽입…"), null, (_, _) => InsertColumnFromUi());
            var del = menu.Items.Add(LT("Delete Column", "컬럼 삭제"), null, (_, _) => DeleteColumnFromUi(col));
            int left = AdjacentVisibleColumn(col, -1), right = AdjacentVisibleColumn(col, +1);
            menu.Items.Add(LT("Move Column Left", "컬럼 왼쪽으로 이동"), null, (_, _) => MoveColumnFromUi(col, left)).Enabled = left >= 0;
            menu.Items.Add(LT("Move Column Right", "컬럼 오른쪽으로 이동"), null, (_, _) => MoveColumnFromUi(col, right)).Enabled = right >= 0;
            del.Enabled = _doc.ColumnCount > 1;
        }

        // ---------------------------------------------------------------- 저장

        private async Task SaveEditsAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || _doc.Edits.IsEmpty) return;
            if (_sheetEditing && grid.IsCurrentCellInEditMode) grid.EndEdit();
            var doc = _doc;

            // 사용자가 연 원본(임포트면 임시 CSV가 아니라 엑셀/SAS/SPSS 파일)을 기준으로 저장 형식을 정한다.
            string sourcePath = _workbook?.SourcePath ?? _currentPath ?? "data";
            string sourceExt = Path.GetExtension(sourcePath).ToLowerInvariant();
            bool excelSource = _workbook is not null && sourceExt is ".xlsx" or ".xlsm" or ".xls";
            bool otherImport = _workbook is not null && !excelSource;
            if (otherImport)
            {
                string kind = sourceExt switch
                {
                    ".sav" => "SPSS", ".sas7bdat" => "SAS", ".db" or ".sqlite" or ".sqlite3" => "SQLite", _ => sourceExt,
                };
                if (MessageBox.Show(this,
                        LT($"This data came from a {kind} file. Edits are saved as a CSV file: values are written as text, and variable labels, value labels and declared types are not kept. The original {kind} file is never changed.",
                           $"이 데이터는 {kind} 파일에서 왔습니다. 편집 내용은 CSV 파일로 저장됩니다: 값은 텍스트로 기록되며 변수 라벨·값 라벨·선언된 타입은 유지되지 않습니다. 원본 {kind} 파일은 바뀌지 않습니다."),
                        ProgramName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            }

            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            using var dlg = new SaveFileDialog { OverwritePrompt = true };
            if (excelSource)
            {
                dlg.Filter = LT("Excel Workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|All Files (*.*)|*.*", "Excel 통합 문서 (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|모든 파일 (*.*)|*.*");
                dlg.FileName = baseName + ".edited.xlsx";
            }
            else
            {
                dlg.Filter = LT("CSV (*.csv)|*.csv|All Files (*.*)|*.*", "CSV (*.csv)|*.csv|모든 파일 (*.*)|*.*");
                dlg.FileName = baseName + ".edited.csv";
            }
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string path = dlg.FileName;
            string fullPath = Path.GetFullPath(path);
            if (string.Equals(fullPath, Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, Path.GetFullPath(_currentPath ?? ""), StringComparison.OrdinalIgnoreCase) ||
                IsOpenInAnyTab(fullPath)) // 다른 탭에서 열려 있는 파일도 덮어쓰지 않는다
            {
                MessageBox.Show(this,
                    LT("The original file is never overwritten. Choose a different file name.", "원본 파일은 덮어쓰지 않습니다. 다른 파일 이름을 고르세요."),
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool asXlsx = Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
            string sheetName = _workbook is not null && _currentSheetIndex >= 0 && _currentSheetIndex < _workbook.SheetNames.Count
                ? _workbook.SheetNames[_currentSheetIndex] : baseName;
            string summary = EditSummary();
            var done = await RunAnalysisOperationAsync(doc, (_, ct) =>
            {
                if (asXlsx) doc.SaveAsXlsx(path, sheetName, null, ct);
                else doc.SaveWithEdits(path, null, ct);
                return "ok";
            });
            if (done is null || IsDisposed || !ReferenceEquals(doc, _doc)) return;
            doc.Edits.MarkSaved();
            DeleteJournal();
            UpdateFeatureState();
            statusLabel.Text = LT($"Saved edits ({summary}) to {Path.GetFileName(path)}.", $"편집({summary})을 {Path.GetFileName(path)}에 저장했습니다.");
            if (MessageBox.Show(this,
                    LT("Saved. Open the saved file now?", "저장했습니다. 저장한 파일을 지금 열까요?"),
                    ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await OpenFileAsync(path);
        }
    }
}
