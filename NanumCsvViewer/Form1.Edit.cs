using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // ---------------------------------------------------------------- 편집 (보기 전용이 기본)
    //
    // 기본은 읽기 전용 뷰어다. 데이터가 함부로 바뀌지 않도록 편집은 별도 버튼으로만 켠다:
    //  - 셀 편집: 선택한 셀 1개를 대화상자에서 고치면 바로 보기 모드로 돌아온다.
    //  - 시트 편집 모드: 켜 두는 동안 여러 셀을 인라인 편집한다. 끄면 보기 모드.
    // 편집은 원본 파일에 쓰지 않고 덮개(CellEdits)에만 쌓이며, "편집 내용 저장…"으로 새 파일에 저장한다.
    // 값은 항상 문자열 그대로 다룬다(숫자 변환 없음) — 001의 선행 0이 사라지지 않는다.
    public partial class Form1
    {
        private ToolStripMenuItem? _editCellMenu, _editSheetMenu, _saveEditsMenu, _revertCellMenu, _discardEditsMenu;
        private ToolStripButton? _editCellButton, _editSheetButton;
        private bool _sheetEditing;
        private bool _editWired;
        private int _editVersion, _savedEditVersion;
        private string _editTitleSuffix = "";
        private CellEdits? _watchedEdits;

        private bool HasUnsavedEdits => _doc is not null && !_doc.Edits.IsEmpty && _editVersion != _savedEditVersion;

        private void BuildEditFeatures()
        {
            editToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _editCellMenu = MakeItem("Edit Cell…", "셀 편집…", (_, _) => EditCurrentCell());
            _editCellMenu.ShortcutKeys = Keys.F2;
            _editSheetMenu = MakeItem("Sheet Edit Mode", "시트 편집 모드", (_, _) => SetSheetEditing(!_sheetEditing));
            _editSheetMenu.ShortcutKeys = Keys.Control | Keys.Shift | Keys.E;
            _revertCellMenu = MakeItem("Revert This Cell", "이 셀 편집 되돌리기", (_, _) => RevertCurrentCell());
            _saveEditsMenu = MakeItem("Save Edits As…", "편집 내용 저장…", async (_, _) => await SaveEditsAsync());
            _discardEditsMenu = MakeItem("Discard All Edits", "모든 편집 버리기", (_, _) => DiscardAllEdits());
            foreach (var m in new[] { _editCellMenu, _editSheetMenu, _revertCellMenu, _saveEditsMenu, _discardEditsMenu })
                editToolStripMenuItem.DropDownItems.Add(m);

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

            if (_editWired) return;
            _editWired = true;
            grid.CellFormatting += OnEditCellFormatting;
            grid.CellValuePushed += OnEditCellValuePushed;
            grid.EditingControlShowing += OnEditControlShowing;
            grid.CellDoubleClick += OnEditCellDoubleClick;
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
                _editSheetButton.ToolTipText = LT("Sheet Edit Mode — turn on/off editing of many cells (off = view mode)", "시트 편집 모드 — 여러 셀 편집을 켜고 끕니다(끄면 보기 모드)");
                _editSheetButton.Checked = _sheetEditing;
            }
        }

        // Form1.UpdateFeatureMenuState()에서 호출.
        private void UpdateEditState()
        {
            bool ready = _doc is not null && _doc.IndexingComplete && !_busy;
            bool hasCell = ready && grid.CurrentCell is { RowIndex: >= 0, ColumnIndex: >= 0 };
            WatchEdits();
            if (_editCellMenu is not null) _editCellMenu.Enabled = hasCell && !_sheetEditing;
            if (_editCellButton is not null) _editCellButton.Enabled = hasCell && !_sheetEditing;
            if (_editSheetMenu is not null) { _editSheetMenu.Enabled = ready; _editSheetMenu.Checked = _sheetEditing; }
            if (_editSheetButton is not null) _editSheetButton.Enabled = ready;
            bool onEditedCell = hasCell && CurrentCellIsEdited();
            if (_revertCellMenu is not null) _revertCellMenu.Enabled = onEditedCell;
            bool any = _doc is not null && !_doc.Edits.IsEmpty;
            if (_saveEditsMenu is not null) _saveEditsMenu.Enabled = ready && any;
            if (_discardEditsMenu is not null) _discardEditsMenu.Enabled = ready && any;
            LocalizeEditButtons();
            UpdateEditTitle();
        }

        // 문서마다 덮개가 새로 생기므로 변경 알림을 문서에 맞춰 다시 건다.
        private void WatchEdits()
        {
            var edits = _doc?.Edits;
            if (ReferenceEquals(edits, _watchedEdits)) return;
            if (_watchedEdits is not null) _watchedEdits.Changed -= OnEditsChanged;
            _watchedEdits = edits;
            if (edits is not null) edits.Changed += OnEditsChanged;
        }

        private void OnEditsChanged()
        {
            _editVersion++;
            if (InvokeRequired) BeginInvoke(new Action(RefreshAfterEdit)); else RefreshAfterEdit();
        }

        private void RefreshAfterEdit()
        {
            if (IsDisposed) return;
            grid.Invalidate();
            UpdateEditTitle();
            if (_editSaveMenuRefresh) return;
            _editSaveMenuRefresh = true;
            try { UpdateEditStateMenusOnly(); } finally { _editSaveMenuRefresh = false; }
        }

        private bool _editSaveMenuRefresh;

        private void UpdateEditStateMenusOnly()
        {
            bool ready = _doc is not null && _doc.IndexingComplete && !_busy;
            bool any = _doc is not null && !_doc.Edits.IsEmpty;
            if (_saveEditsMenu is not null) _saveEditsMenu.Enabled = ready && any;
            if (_discardEditsMenu is not null) _discardEditsMenu.Enabled = ready && any;
            if (_revertCellMenu is not null)
                _revertCellMenu.Enabled = ready && grid.CurrentCell is { RowIndex: >= 0 } && CurrentCellIsEdited();
        }

        private void UpdateEditTitle()
        {
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
            _editVersion = 0;
            _savedEditVersion = 0;
            if (_editTitleSuffix.Length > 0 && Text.EndsWith(_editTitleSuffix, StringComparison.Ordinal))
                Text = Text[..^_editTitleSuffix.Length];
            _editTitleSuffix = "";
        }

        /// <summary>저장하지 않은 편집이 있으면 버릴지 묻는다. false면 호출자는 동작을 중단한다.</summary>
        private bool ConfirmDiscardEdits()
        {
            if (!HasUnsavedEdits) return true;
            var r = MessageBox.Show(this,
                LT($"There are {_doc!.Edits.Count:N0} unsaved cell edit(s). Discard them?\n(Use Edit ▸ Save Edits As… to keep them.)",
                   $"저장하지 않은 셀 편집이 {_doc.Edits.Count:N0}건 있습니다. 버릴까요?\n(보존하려면 편집 ▸ 편집 내용 저장…을 먼저 사용하세요.)"),
                ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return r == DialogResult.Yes;
        }

        // ---------------------------------------------------------------- 단일 셀 편집

        private void EditCurrentCell()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || _sheetEditing) return;
            if (grid.CurrentCell is not { RowIndex: >= 0, ColumnIndex: >= 0 } cell) return;
            int viewRow = cell.RowIndex, col = cell.ColumnIndex;
            int dataRow = _doc.GetDataRowIndex(viewRow);
            if (dataRow < 0) return;

            string[] row = _doc.GetDisplayRow(viewRow);
            string current = col < row.Length ? row[col] : "";
            var originalRow = _doc.GetOriginalDataRow(dataRow);
            string original = col < originalRow.Length ? originalRow[col] : "";
            string colName = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";

            using var dlg = new CellEditDialog(colName, dataRow + 1L, current, original, _doc.Edits.Contains(dataRow, col), _palette);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            CommitCellEdit(viewRow, col, dlg.Value);
            // 단일 셀 편집은 적용 즉시 보기 모드 — 따로 켜 둔 상태가 없다.
            statusLabel.Text = LT($"Cell edited (row {dataRow + 1:N0}, {colName}). Back in view mode — save with Edit ▸ Save Edits As….",
                                  $"셀을 편집했습니다(행 {dataRow + 1:N0}, {colName}). 보기 모드로 돌아왔습니다 — 편집 ▸ 편집 내용 저장…으로 저장하세요.");
        }

        /// <summary>텍스트를 그대로 덮개에 기록. 줄바꿈은 원래 값의 스타일(CRLF/LF)에 맞춘다.</summary>
        private void CommitCellEdit(int viewRow, int col, string text)
        {
            if (_doc is null) return;
            int dataRow = _doc.GetDataRowIndex(viewRow);
            if (dataRow < 0) return;
            var originalRow = _doc.GetOriginalDataRow(dataRow);
            string original = col < originalRow.Length ? originalRow[col] : "";
            if (!original.Contains("\r\n", StringComparison.Ordinal))
                text = text.Replace("\r\n", "\n");
            _doc.Edits.Set(dataRow, col, text, original);
            grid.InvalidateRow(viewRow);
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
        }

        private bool CurrentCellIsEdited()
        {
            if (_doc is null || grid.CurrentCell is not { RowIndex: >= 0 } c) return false;
            int dataRow = _doc.GetDataRowIndex(c.RowIndex);
            return dataRow >= 0 && _doc.Edits.Contains(dataRow, c.ColumnIndex);
        }

        private void RevertCurrentCell()
        {
            if (_doc is null || grid.CurrentCell is not { RowIndex: >= 0 } c) return;
            int dataRow = _doc.GetDataRowIndex(c.RowIndex);
            if (dataRow < 0) return;
            _doc.Edits.Revert(dataRow, c.ColumnIndex);
            grid.InvalidateRow(c.RowIndex);
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
        }

        private void DiscardAllEdits()
        {
            if (_doc is null || _doc.Edits.IsEmpty) return;
            if (MessageBox.Show(this,
                    LT($"Discard all {_doc.Edits.Count:N0} cell edit(s)?", $"셀 편집 {_doc.Edits.Count:N0}건을 모두 버릴까요?"),
                    ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            _doc.Edits.Clear();
            _savedEditVersion = _editVersion;
            grid.Invalidate();
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
                if (MessageBox.Show(this,
                        LT("Turn on sheet edit mode?\n\nCells can be edited until you turn it off. Edits are kept in memory and never written to the original file; save them with Edit ▸ Save Edits As… (a new file).\nValues are stored exactly as typed (001 stays 001).",
                           "시트 편집 모드를 켤까요?\n\n끌 때까지 여러 셀을 편집할 수 있습니다. 편집은 메모리에만 쌓이고 원본 파일에는 쓰이지 않으며, 편집 ▸ 편집 내용 저장…으로 새 파일에 저장합니다.\n값은 입력한 그대로 저장됩니다(001은 001 그대로)."),
                        ProgramName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            }
            else if (grid.IsCurrentCellInEditMode) grid.EndEdit();

            _sheetEditing = on;
            grid.ReadOnly = !on;
            grid.EditMode = on ? DataGridViewEditMode.EditProgrammatically : DataGridViewEditMode.EditProgrammatically;
            if (!silent)
            {
                statusLabel.Text = on
                    ? LT("Sheet edit mode ON — double-click or press F2 on a cell to edit; Enter commits, Esc cancels.",
                         "시트 편집 모드 켜짐 — 셀을 더블클릭하거나 F2로 편집합니다. Enter 확정, Esc 취소.")
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

        // F2 인라인 편집 시작(시트 편집 모드에서만 ReadOnly가 풀려 있다).
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
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

        // 편집된 셀 강조(앰버).
        private void OnEditCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
        {
            if (_doc is null || _doc.Edits.IsEmpty || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            int dataRow = _doc.GetDataRowIndex(e.RowIndex);
            if (dataRow < 0 || !_doc.Edits.Contains(dataRow, e.ColumnIndex) || e.CellStyle is null) return;
            e.CellStyle.BackColor = _theme == AppTheme.Dark ? Color.FromArgb(96, 78, 16) : Color.FromArgb(255, 238, 186);
        }

        // ---------------------------------------------------------------- 저장

        private async Task SaveEditsAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || _doc.Edits.IsEmpty) return;
            if (_sheetEditing && grid.IsCurrentCellInEditMode) grid.EndEdit();
            var doc = _doc;
            string baseName = Path.GetFileNameWithoutExtension(_currentPath ?? "data");
            using var dlg = new SaveFileDialog
            {
                Filter = LT("CSV (*.csv)|*.csv|All Files (*.*)|*.*", "CSV (*.csv)|*.csv|모든 파일 (*.*)|*.*"),
                FileName = baseName + ".edited.csv",
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string path = dlg.FileName;
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_currentPath ?? ""), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    LT("The original file is never overwritten. Choose a different file name.", "원본 파일은 덮어쓰지 않습니다. 다른 파일 이름을 고르세요."),
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int version = _editVersion;
            int count = doc.Edits.Count;
            var done = await RunAnalysisOperationAsync(doc, (_, ct) =>
            {
                doc.SaveWithEdits(path, null, ct);
                return "ok";
            });
            if (done is null || IsDisposed || !ReferenceEquals(doc, _doc)) return;
            _savedEditVersion = version;
            UpdateFeatureState();
            statusLabel.Text = LT($"Saved {count:N0} cell edit(s) to {Path.GetFileName(path)}.", $"셀 편집 {count:N0}건을 {Path.GetFileName(path)}에 저장했습니다.");
            if (MessageBox.Show(this,
                    LT("Saved. Open the saved file now?", "저장했습니다. 저장한 파일을 지금 열까요?"),
                    ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await OpenFileAsync(path);
        }
    }
}
