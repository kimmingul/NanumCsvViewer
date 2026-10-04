using System.Diagnostics;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // 시트 편집 보조 기능: 되돌리기/다시 실행, 붙여넣기·지우기, 컬럼 이름 변경, 행 삽입/삭제,
    // 편집 직후 필터·정렬 재평가(E1), 열린 분석 창의 "데이터가 바뀜" 표시, 크래시 복구 저널(E7).
    public partial class Form1
    {
        private const int MaxSelectionCells = 1_000_000;  // 한 번에 다루는 셀 수 상한(이보다 크면 거부)
        private const int ConfirmCellThreshold = 100;      // 이 수를 넘는 붙여넣기·지우기는 확인을 받는다

        private readonly System.Windows.Forms.Timer _settleTimer = new() { Interval = 400 };
        private readonly System.Windows.Forms.Timer _journalTimer = new() { Interval = 1500 };
        private int _pendingSelectRowId = -1;

        // ---------------------------------------------------------------- 되돌리기 / 다시 실행

        private void CancelInlineEdit()
        {
            if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
        }

        private void UndoEdit()
        {
            if (!EditsReady) return;
            var edits = _doc!.Edits;
            if (!edits.CanUndo) { statusLabel.Text = LT("Nothing to undo.", "되돌릴 편집이 없습니다."); return; }
            CancelInlineEdit();
            string? what = edits.UndoDescription;
            edits.Undo();
            AfterHistoryStep(LT("Undone", "되돌림"), what);
        }

        private void RedoEdit()
        {
            if (!EditsReady) return;
            var edits = _doc!.Edits;
            if (!edits.CanRedo) { statusLabel.Text = LT("Nothing to redo.", "다시 실행할 편집이 없습니다."); return; }
            CancelInlineEdit();
            string? what = edits.RedoDescription;
            edits.Redo();
            AfterHistoryStep(LT("Redone", "다시 실행"), what);
        }

        private void AfterHistoryStep(string verb, string? what)
        {
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
            statusLabel.Text = what is { Length: > 0 } ? $"{verb}: {what}" : verb;
        }

        // ---------------------------------------------------------------- 덮개 변경 처리

        private void OnEditsChanged()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(OnEditsChanged)); } catch (ObjectDisposedException) { }
                return;
            }
            if (IsDisposed || _doc is null) return;
            var edits = _doc.Edits;
            if (edits.HeaderVersion != _seenHeaderVersion)
            {
                _seenHeaderVersion = edits.HeaderVersion;
                ApplyHeaderNamesToUi();
            }
            bool structural = edits.StructureVersion != _seenStructureVersion;
            if (structural)
            {
                _seenStructureVersion = edits.StructureVersion;
                RefreshRowCount();
            }
            grid.Invalidate();
            UpdateDetailPanel();
            MarkAnalysisWindowsStale();
            UpdateEditTitle();
            UpdateEditStateMenusOnly();

            // E1: 편집이 멈추면(디바운스) 필터·정렬·타입 배지를 새 데이터로 다시 계산. 행 구조 변경은 바로.
            _settleTimer.Stop();
            _settleTimer.Interval = structural ? 60 : 400;
            _settleTimer.Start();
            // E7: 저장하지 않은 편집은 잠시 뒤 복구 저널에 기록.
            _journalTimer.Stop();
            _journalTimer.Start();
        }

        // 컬럼 이름이 바뀌면(되돌리기 포함) 그리드·필터 콤보를 현재 이름으로 맞춘다.
        // 분석·필터·차트는 grid.Columns[c].HeaderText와 doc.Header를 읽으므로 둘 다 갱신하면 모두 새 이름을 본다.
        private void ApplyHeaderNamesToUi()
        {
            if (_doc is null) return;
            var header = _doc.Header;
            int selected = filterColumnCombo.SelectedIndex;
            for (int c = 0; c < grid.Columns.Count && c < header.Length; c++)
            {
                string name = string.IsNullOrEmpty(header[c]) ? $"Column{c + 1}" : header[c];
                if (!string.Equals(grid.Columns[c].HeaderText, name, StringComparison.Ordinal)) grid.Columns[c].HeaderText = name;
                int item = c + 1;
                if (item < filterColumnCombo.Items.Count && !string.Equals(filterColumnCombo.Items[item]?.ToString(), name, StringComparison.Ordinal))
                    filterColumnCombo.Items[item] = name;
            }
            if (filterColumnCombo.SelectedIndex != selected && selected < filterColumnCombo.Items.Count)
                filterColumnCombo.SelectedIndex = selected;
            grid.Invalidate();
            if (_columnSummaries.Length > 0) ComputeColumnTypeTags();
            RebuildFilterChips();
        }

        // ---------------------------------------------------------------- E1 편집 후 재평가

        // 타이머 틱(async void)에서 불리므로 예외가 앱을 죽이지 않게 막고 상태 표시줄에 사유를 남긴다(조용히 삼키지 않음).
        private async Task SettleAfterEditAsync()
        {
            try { await SettleAfterEditCoreAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EditSettle] {ex}");
                if (!IsDisposed) statusLabel.Text = LT("Re-evaluating the view after the edit failed: ", "편집 후 뷰 재계산에 실패했습니다: ") + ex.Message;
            }
        }

        private async Task SettleAfterEditCoreAsync()
        {
            _settleTimer.Stop();
            var doc = _doc;
            if (doc is null || _closing || IsDisposed || !doc.IndexingComplete) return;
            if (_busy || grid.IsCurrentCellInEditMode)
            {
                // 다른 작업이나 셀 편집 중이면 끝난 뒤에 다시 시도(편집 중인 셀을 날리지 않는다).
                _settleTimer.Interval = 400;
                _settleTimer.Start();
                return;
            }

            bool reapplied = false;
            if (HasAnyFilter || _sortKeys.Count > 0)
            {
                await ReapplyViewAfterEditAsync(doc);
                reapplied = true;
            }
            if (_closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;

            ComputeColumnTypeTags();
            if (_facetsVisible && !reapplied) BuildFacets();
            if (_qualityFindings.Count > 0)
            {
                ResetQualityState();
                statusLabel.Text = LT("Data was edited — the data-quality findings were cleared; run the check again.",
                                      "데이터가 편집되어 데이터 품질 발견 목록을 비웠습니다. 다시 검사하세요.");
            }
            ResolvePendingRowSelection(doc);
        }

        /// <summary>활성 필터·정렬을 현재(편집된) 데이터로 처음부터 다시 평가한다. 보던 위치(현재 셀의 행)는 유지하려 시도한다.</summary>
        private async Task ReapplyViewAfterEditAsync(VirtualCsvDocument doc)
        {
            var keys = _sortKeys.ToArray();
            Func<string[], bool>? predicate = HasAnyFilter ? BuildCombinedPredicate() : null;
            if (predicate is null && keys.Length == 0) return;

            int curCol = grid.CurrentCell?.ColumnIndex ?? -1;
            int curId = grid.CurrentCell is { RowIndex: >= 0 } cur ? doc.GetRowId(cur.RowIndex) : -1;
            int firstRow = grid.RowCount > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;

            await RunViewOpAsync(p => doc.RebuildViewAsync(predicate, keys, p, _opCts!.Token),
                LT("Edit applied — re-evaluating filter/sort…", "편집 반영 — 필터·정렬 다시 계산 중…"));
            if (_closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;

            bool lostCurrent = false;
            if (curId >= 0)
            {
                int vi = doc.FindViewIndex(curId);
                if (vi >= 0 && vi < grid.RowCount && curCol >= 0 && curCol < grid.ColumnCount && grid.Columns[curCol].Visible)
                {
                    try
                    {
                        grid.CurrentCell = grid[curCol, vi];
                        int shown = Math.Max(1, grid.DisplayedRowCount(false));
                        int first = firstRow >= 0 && vi >= firstRow && vi < firstRow + shown ? firstRow : Math.Max(0, vi - 3);
                        grid.FirstDisplayedScrollingRowIndex = Math.Min(first, grid.RowCount - 1);
                    }
                    catch { /* 레이아웃 중 일시 예외 무시 */ }
                }
                else lostCurrent = true;
            }
            else if (firstRow >= 0 && firstRow < grid.RowCount)
            {
                try { grid.FirstDisplayedScrollingRowIndex = firstRow; } catch { }
            }

            UpdateSortGlyphs();
            if (HasAnyFilter) UpdateFilterStatus();
            else statusLabel.Text = Loc.F("Status_SortFmt", DescribeSort(), doc.DisplayRowCount.ToString("N0"));
            OnCurrentCellChanged(grid, EventArgs.Empty);
            string note = LT($"Edit applied — filter/sort re-evaluated ({doc.DisplayRowCount:N0} rows shown).",
                             $"편집 반영 — 필터·정렬을 다시 계산했습니다({doc.DisplayRowCount:N0}행 표시).");
            if (lostCurrent)
                note += LT(" The edited row no longer matches the active filter and is hidden (Undo brings it back).",
                           " 편집한 행이 현재 필터에 맞지 않아 숨겨졌습니다(되돌리기로 복원).");
            statusLabel.Text = note;
        }

        // 필터가 걸린 상태에서 삽입한 행은 재평가 뒤에야 보일 수 있다. 보이면 선택하고, 걸러졌으면 그렇게 알린다.
        private void ResolvePendingRowSelection(VirtualCsvDocument doc)
        {
            int id = _pendingSelectRowId;
            if (id < 0) return;
            _pendingSelectRowId = -1;
            int vi = doc.FindViewIndex(id);
            if (vi >= 0 && vi < grid.RowCount)
            {
                SelectViewRow(vi);
                return;
            }
            if (doc.Edits.IsDeleted(id)) return;
            statusLabel.Text = LT("The inserted row is hidden by the active filter/sort until its values match; Undo removes it.",
                                  "삽입한 행은 값이 현재 필터/정렬 조건에 맞기 전까지 숨겨집니다(되돌리기로 취소).");
        }

        private void SelectViewRow(int viewRow)
        {
            int col = grid.CurrentCell?.ColumnIndex ?? -1;
            if (col < 0 || col >= grid.ColumnCount || !grid.Columns[col].Visible)
                col = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).Select(c => c.Index).DefaultIfEmpty(-1).First();
            if (col < 0 || viewRow < 0 || viewRow >= grid.RowCount) return;
            try
            {
                grid.ClearSelection();
                grid.CurrentCell = grid[col, viewRow];
                int shown = Math.Max(1, grid.DisplayedRowCount(false));
                int first = grid.FirstDisplayedScrollingRowIndex;
                if (viewRow < first || viewRow >= first + shown) grid.FirstDisplayedScrollingRowIndex = Math.Max(0, viewRow - 3);
            }
            catch { /* 레이아웃 중 일시 예외 무시 */ }
            OnCurrentCellChanged(grid, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- 열린 분석 창 표시 (E1)

        // 분석 결과·차트 창은 만들어질 때의 데이터 스냅샷을 보여 준다. 그 뒤 편집되면 결과가 낡았다고 창 맨 위에 표시한다.
        private void MarkAnalysisWindowsStale()
        {
            string note = LT("Data was edited after this window was created — re-run the analysis (charts: use refresh) to include the edits.",
                             "이 창을 만든 뒤 데이터가 편집되었습니다 — 편집을 반영하려면 분석을 다시 실행하세요(차트는 새로고침).");
            foreach (var f in OwnedForms)
            {
                if (f.IsDisposed || f is not (AdvancedResultForm or ChartForm)) continue;
                if (f.Controls.ContainsKey("staleBanner")) continue;
                var banner = new Label
                {
                    Name = "staleBanner",
                    Dock = DockStyle.Top,
                    AutoSize = false,
                    Height = 42,
                    Padding = new Padding(8, 2, 8, 2),
                    TextAlign = ContentAlignment.MiddleLeft,
                    BackColor = Color.FromArgb(255, 238, 186),
                    ForeColor = Color.FromArgb(90, 60, 0),
                    Text = "⚠ " + note,
                };
                f.Controls.Add(banner); // 가장 나중에 넣은 Dock=Top 컨트롤이 맨 위에 놓인다
                if (!f.Text.StartsWith("⚠", StringComparison.Ordinal)) f.Text = "⚠ " + f.Text;
            }
        }

        // ---------------------------------------------------------------- 선택 수집

        /// <summary>선택된 셀을 (표시 행 → 컬럼들)로 모은다. 너무 크면 안내 후 null.</summary>
        private Dictionary<int, List<int>>? CollectSelection(out int count)
        {
            count = grid.GetCellCount(DataGridViewElementStates.Selected);
            if (count > MaxSelectionCells)
            {
                MessageBox.Show(this,
                    LT($"The selection has {count:N0} cells; at most {MaxSelectionCells:N0} can be changed at once. Select a smaller range.",
                       $"선택한 셀이 {count:N0}개입니다. 한 번에 {MaxSelectionCells:N0}개까지만 바꿀 수 있습니다. 범위를 줄여 선택하세요."),
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            var map = new Dictionary<int, List<int>>();
            count = 0;
            foreach (DataGridViewCell c in grid.SelectedCells)
            {
                if (c.RowIndex < 0 || c.ColumnIndex < 0) continue;
                if (!map.TryGetValue(c.RowIndex, out var list)) map[c.RowIndex] = list = new List<int>();
                list.Add(c.ColumnIndex);
                count++;
            }
            if (map.Count == 0 && grid.CurrentCell is { RowIndex: >= 0, ColumnIndex: >= 0 } cc)
            {
                map[cc.RowIndex] = new List<int> { cc.ColumnIndex };
                count = 1;
            }
            return map;
        }

        private List<int> PasteColumnOrder()
            => grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).Select(c => c.Index).ToList();

        private bool ConfirmManyCells(int count, string verbEn, string verbKo)
        {
            if (count <= ConfirmCellThreshold) return true;
            return MessageBox.Show(this,
                LT($"{verbEn} {count:N0} cells? (More than {ConfirmCellThreshold} — Undo brings them back.)",
                   $"셀 {count:N0}개를 {verbKo}까요? ({ConfirmCellThreshold}개 초과 — 되돌리기로 복원할 수 있습니다.)"),
                ProgramName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        // ---------------------------------------------------------------- E3 붙여넣기 · 지우기

        private void PasteFromClipboard()
        {
            if (!EditsReady) return;
            if (!_sheetEditing)
            {
                statusLabel.Text = LT("Paste works only in sheet edit mode (Ctrl+Shift+E); Edit Cell changes one cell at a time.",
                                      "붙여넣기는 시트 편집 모드(Ctrl+Shift+E)에서만 됩니다. 셀 편집은 셀 하나씩만 고칩니다.");
                return;
            }
            string? text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (Exception ex)
            {
                statusLabel.Text = LT("The clipboard could not be read: ", "클립보드를 읽을 수 없습니다: ") + ex.Message;
                return;
            }
            var block = ClipboardGrid.Parse(text);
            if (block.Length == 0) { statusLabel.Text = LT("The clipboard has no text to paste.", "붙여넣을 텍스트가 클립보드에 없습니다."); return; }
            int h = block.Length, w = block[0].Length;
            if ((long)h * w > MaxSelectionCells)
            {
                MessageBox.Show(this,
                    LT($"The clipboard block is {h:N0} × {w:N0} cells; at most {MaxSelectionCells:N0} can be pasted at once. Nothing was pasted.",
                       $"클립보드 블록이 {h:N0} × {w:N0} 셀입니다. 한 번에 {MaxSelectionCells:N0}개까지만 붙여넣을 수 있습니다. 아무것도 붙여넣지 않았습니다."),
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            CancelInlineEdit();
            var sel = CollectSelection(out int selCount);
            if (sel is null) return;
            if (sel.Count == 0) { statusLabel.Text = LT("Select a cell to paste into.", "붙여넣을 셀을 선택하세요."); return; }

            var targets = new List<(int Row, int Col, string Text)>();
            if (h == 1 && w == 1 && selCount > 1)
            {
                // 셀 하나를 복사해 여러 셀을 선택했으면 선택 전체를 채운다(엑셀과 같음).
                foreach (var (row, cols) in sel) foreach (int c in cols) targets.Add((row, c, block[0][0]));
            }
            else
            {
                var order = PasteColumnOrder();
                int top = sel.Keys.Min();
                int left = sel.Values.SelectMany(v => v).Select(c => order.IndexOf(c)).Where(p => p >= 0).DefaultIfEmpty(-1).Min();
                if (left < 0) { statusLabel.Text = LT("Select a visible cell to paste into.", "붙여넣을 보이는 셀을 선택하세요."); return; }
                int availRows = grid.RowCount - top, availCols = order.Count - left;
                if (h > availRows || w > availCols)
                {
                    MessageBox.Show(this,
                        LT($"The pasted block is {h:N0} rows × {w:N0} columns, but only {availRows:N0} row(s) × {availCols:N0} column(s) are available from the selected cell. Nothing was pasted.\nInsert rows first (Edit ▸ Insert Row) or select an earlier cell.",
                           $"붙여넣을 블록이 {h:N0}행 × {w:N0}열인데 선택한 셀부터는 {availRows:N0}행 × {availCols:N0}열만 남아 있습니다. 아무것도 붙여넣지 않았습니다.\n먼저 행을 삽입(편집 ▸ 행 삽입)하거나 더 앞의 셀을 선택하세요."),
                        ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                for (int r = 0; r < h; r++)
                    for (int c = 0; c < w; c++)
                        targets.Add((top + r, order[left + c], block[r][c]));
            }

            if (!ConfirmManyCells(targets.Count, "Paste into", "붙여넣을")) return;
            int changed = ApplyCellValues(targets, LT($"Paste {targets.Count:N0} cell(s)", $"셀 {targets.Count:N0}개 붙여넣기"));
            statusLabel.Text = LT($"Pasted {targets.Count:N0} cell(s) ({h:N0} × {w:N0}); {changed:N0} changed. Ctrl+Z undoes the whole paste.",
                                  $"셀 {targets.Count:N0}개를 붙여넣었습니다({h:N0} × {w:N0}, {changed:N0}개 변경). Ctrl+Z로 한 번에 되돌립니다.");
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
        }

        private void ClearSelectedCells()
        {
            if (!EditsReady || !_sheetEditing) return;
            CancelInlineEdit();
            var sel = CollectSelection(out int count);
            if (sel is null || count == 0) return;
            if (!ConfirmManyCells(count, "Clear", "지울")) return;
            var targets = new List<(int Row, int Col, string Text)>(count);
            foreach (var (row, cols) in sel) foreach (int c in cols) targets.Add((row, c, ""));
            int changed = ApplyCellValues(targets, LT($"Clear {count:N0} cell(s)", $"셀 {count:N0}개 지우기"));
            statusLabel.Text = LT($"Cleared {count:N0} cell(s); {changed:N0} changed. Ctrl+Z undoes.", $"셀 {count:N0}개를 지웠습니다({changed:N0}개 변경). Ctrl+Z로 되돌립니다.");
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
        }

        /// <summary>(표시 행, 컬럼, 텍스트) 목록을 한 단계로 덮개에 기록. 실제로 값이 바뀐 셀 수를 돌려준다.</summary>
        private int ApplyCellValues(List<(int Row, int Col, string Text)> targets, string description)
        {
            var doc = _doc!;
            var edits = doc.Edits;
            int changed = 0;
            using (edits.BeginStep(description))
            {
                foreach (var group in targets.GroupBy(t => t.Row))
                {
                    int rowId = doc.GetRowId(group.Key);
                    if (rowId < 0) continue;
                    var original = doc.GetOriginalRow(rowId);
                    foreach (var (_, col, text) in group)
                    {
                        string orig = col < original.Length ? original[col] : "";
                        string value = MatchNewlineStyle(text, orig);
                        bool had = edits.TryGet(rowId, col, out string? cur);
                        string before = had ? cur! : orig;
                        if (!string.Equals(before, value, StringComparison.Ordinal)) changed++;
                        edits.Set(rowId, col, value, orig);
                    }
                }
            }
            return changed;
        }

        // ---------------------------------------------------------------- E4 컬럼 이름 변경

        private void RenameCurrentColumn()
        {
            if (grid.CurrentCell is { ColumnIndex: >= 0 } c) RenameColumn(c.ColumnIndex);
        }

        private void RenameColumn(int col)
        {
            if (!_sheetEditing || _doc is null) return;
            if (!EditsReady)
            {
                statusLabel.Text = LT("Wait for the current operation to finish, then rename the column.", "진행 중인 작업이 끝난 뒤 컬럼 이름을 바꾸세요.");
                return;
            }
            var doc = _doc;
            if (col < 0 || col >= doc.ColumnCount) return;
            CancelInlineEdit();
            string current = doc.Header[col];
            using var dlg = new HeaderRenameDialog(col + 1, current, _palette, name => ValidateColumnName(col, name));
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string name = dlg.Value.Trim();
            if (string.Equals(name, current, StringComparison.Ordinal)) return;

            using (doc.Edits.BeginStep(LT($"Rename column {col + 1}", $"{col + 1}번 컬럼 이름 변경")))
                doc.Edits.SetHeader(col, name, doc.OriginalHeader[col]);

            string shownOld = string.IsNullOrEmpty(current) ? $"Column{col + 1}" : current;
            int stale = MarkStaleExpressionFilters();
            string note = LT($"Column renamed: {shownOld} → {name}. It is used by filters, charts and analyses and written to the first line of the saved file.",
                             $"컬럼 이름을 바꿨습니다: {shownOld} → {name}. 필터·차트·분석에 쓰이며 저장 파일의 첫 줄에 기록됩니다.");
            if (stale > 0)
                note += LT($" {stale} expression filter(s) still use an old name: they keep working but must be re-entered to edit.",
                           $" 표현식 필터 {stale}개가 옛 이름을 씁니다. 계속 동작하지만 고치려면 새 이름으로 다시 입력해야 합니다.");
            statusLabel.Text = note;
            UpdateFeatureState();
        }

        private string? ValidateColumnName(int col, string raw)
        {
            string name = raw.Trim();
            if (name.Length == 0)
                return LT("The column name cannot be empty.", "컬럼 이름은 비워 둘 수 없습니다.");
            if (name.Contains('\r') || name.Contains('\n'))
                return LT("The column name must be a single line.", "컬럼 이름은 한 줄이어야 합니다.");
            var header = _doc!.Header;
            for (int c = 0; c < header.Length; c++)
            {
                if (c == col) continue;
                string other = string.IsNullOrEmpty(header[c]) ? $"Column{c + 1}" : header[c];
                if (string.Equals(other, name, StringComparison.OrdinalIgnoreCase))
                    return LT($"Column {c + 1} is already named '{other}'. Names must be unique so filters and analyses can refer to a column unambiguously.",
                              $"{c + 1}번 컬럼이 이미 '{other}'입니다. 필터·분석이 컬럼을 헷갈리지 않도록 이름은 서로 달라야 합니다.");
            }
            return null;
        }

        // 표현식 필터는 만들 때 컬럼 위치로 컴파일되어 이름이 바뀌어도 계속 동작한다. 다만 저장된 식 글자가 새 헤더로
        // 다시 풀리지 않으면(옛 이름) 칩에 표시해 사용자가 알 수 있게 한다.
        private int MarkStaleExpressionFilters()
        {
            if (_doc is null) return 0;
            int stale = 0;
            for (int i = 0; i < _valueConditions.Count; i++)
            {
                var (desc, pred, expr) = _valueConditions[i];
                if (expr is null) continue;
                try { AdvancedFilterExpression.Compile(expr, _doc.Header); }
                catch (AdvancedFilterExpressionException)
                {
                    stale++;
                    if (!desc.EndsWith(" ⚠", StringComparison.Ordinal)) _valueConditions[i] = (desc + " ⚠", pred, expr);
                }
            }
            if (stale > 0) RebuildFilterChips();
            return stale;
        }

        // ---------------------------------------------------------------- E5 행 삽입 / 삭제

        private void InsertRow(bool above)
        {
            if (!EditsReady || !_sheetEditing) return;
            var doc = _doc!;
            if (!doc.CanEditStructure)
            {
                statusLabel.Text = LT("Rows cannot be inserted or deleted in a table this large.", "이렇게 큰 표에서는 행을 삽입하거나 삭제할 수 없습니다.");
                return;
            }
            CancelInlineEdit();
            int anchor;
            int viewRow = grid.CurrentCell is { RowIndex: >= 0 } c ? c.RowIndex : -1;
            int rowId = viewRow >= 0 ? doc.GetRowId(viewRow) : -1;
            if (rowId >= 0) anchor = above ? doc.PredecessorId(rowId) : rowId;
            else if (above || doc.DataRowsAvailable == 0) anchor = -1;
            else anchor = doc.GetRowIdAtPosition(doc.DataRowsAvailable - 1); // 선택이 없으면 맨 끝에 추가

            int newId = doc.Edits.AddRow(anchor, doc.ColumnCount, doc.BaseRowCount,
                above ? LT("Insert row above", "위에 행 삽입") : LT("Insert row below", "아래에 행 삽입"));
            int vi = doc.FindViewIndex(newId);
            if (vi >= 0) SelectViewRow(vi);
            else _pendingSelectRowId = newId; // 필터/정렬 재평가 뒤에 선택
            statusLabel.Text = LT("Row inserted (green). Ctrl+Z undoes. Saved files include it at this position.",
                                  "행을 삽입했습니다(초록색). Ctrl+Z로 되돌립니다. 저장 파일에 이 위치로 포함됩니다.");
            UpdateFeatureState();
        }

        private void DeleteSelectedRows()
        {
            if (!EditsReady || !_sheetEditing) return;
            var doc = _doc!;
            if (!doc.CanEditStructure)
            {
                statusLabel.Text = LT("Rows cannot be inserted or deleted in a table this large.", "이렇게 큰 표에서는 행을 삽입하거나 삭제할 수 없습니다.");
                return;
            }
            CancelInlineEdit();
            var sel = CollectSelection(out int count);
            if (sel is null || sel.Count == 0) return;
            var viewRows = sel.Keys.OrderBy(r => r).ToList();
            if (viewRows.Count > ConfirmCellThreshold &&
                MessageBox.Show(this,
                    LT($"Delete {viewRows.Count:N0} rows? (Undo brings them back.)", $"행 {viewRows.Count:N0}개를 삭제할까요? (되돌리기로 복원할 수 있습니다.)"),
                    ProgramName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            var ids = viewRows.Select(doc.GetRowId).Where(id => id >= 0).ToList();
            int col = grid.CurrentCell?.ColumnIndex ?? 0;
            int n = doc.Edits.DeleteRows(ids, LT($"Delete {ids.Count:N0} row(s)", $"행 {ids.Count:N0}개 삭제"));
            if (grid.RowCount > 0) SelectViewRow(Math.Min(viewRows[0], grid.RowCount - 1));
            statusLabel.Text = LT($"Deleted {n:N0} row(s); later row numbers shift up. Ctrl+Z undoes. Saved files omit them.",
                                  $"행 {n:N0}개를 삭제했습니다(뒤 행 번호가 당겨집니다). Ctrl+Z로 되돌립니다. 저장 파일에서 빠집니다.");
            UpdateFeatureState();
        }

        // ---------------------------------------------------------------- E7 크래시 복구 저널

        private readonly object _journalLock = new();
        private int _journalGeneration;
        private string? _journalKey;
        private string _journalSource = "";
        private int _journalSheet;
        private VirtualCsvDocument? _recoveryOfferedFor;
        private static bool s_journalPruned;

        // 문서가 바뀔 때(WatchEdits) 키를 새로 계산하고 대기 중인 기록을 무효화한다.
        private void ResetJournalForDocument()
        {
            lock (_journalLock) _journalGeneration++;
            string? src = _workbook?.SourcePath ?? _currentPath;
            _journalSheet = _workbook is null ? 0 : _currentSheetIndex;
            _journalSource = src ?? "";
            _journalKey = src is null || _doc is null ? null : EditJournal.KeyFor(src, _journalSheet);
        }

        private void FlushJournal()
        {
            _journalTimer.Stop();
            var doc = _doc;
            string? key = _journalKey;
            if (doc is null || key is null || IsDisposed) return;
            var edits = doc.Edits;
            if (edits.IsEmpty || !edits.IsDirty) { DeleteJournal(); return; }

            var snapshot = edits.Snapshot();
            int generation;
            lock (_journalLock) generation = _journalGeneration;
            string source = _journalSource;
            int sheet = _journalSheet;
            _ = Task.Run(() =>
            {
                try
                {
                    lock (_journalLock)
                    {
                        if (generation != _journalGeneration) return; // 그 사이 저장·버리기·문서 전환이 있었다
                        string dir = EditJournal.DefaultDirectory;
                        if (!s_journalPruned) { s_journalPruned = true; EditJournal.Prune(dir, TimeSpan.FromDays(60)); }
                        EditJournal.Write(dir, key, source, sheet, snapshot);
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[EditJournal] {ex.Message}"); }
            });
        }

        /// <summary>저장했거나 버렸을 때: 대기 중인 기록을 무효화하고 저널 파일을 지운다.</summary>
        private void DeleteJournal()
        {
            _journalTimer.Stop();
            lock (_journalLock)
            {
                _journalGeneration++;
                if (_journalKey is { } key) EditJournal.Delete(EditJournal.DefaultDirectory, key);
            }
        }

        // 인덱싱이 끝난 문서마다 한 번: 같은 파일의 저널이 있으면 복구를 제안한다.
        private void OfferRecoveryWhenReady()
        {
            var doc = _doc;
            if (doc is null || !doc.IndexingComplete || _busy || ReferenceEquals(_recoveryOfferedFor, doc)) return;
            _recoveryOfferedFor = doc;
            string? key = _journalKey;
            if (key is null || !doc.Edits.IsEmpty) return;
            if (!EditJournal.Exists(EditJournal.DefaultDirectory, key)) return;
            BeginInvoke(new Action(() => OfferRecovery(doc, key)));
        }

        private void OfferRecovery(VirtualCsvDocument doc, string key)
        {
            if (IsDisposed || _closing || !ReferenceEquals(doc, _doc) || !doc.Edits.IsEmpty) return;
            string dir = EditJournal.DefaultDirectory;
            EditSnapshot? snapshot;
            try { snapshot = EditJournal.TryRead(dir, key); }
            catch (InvalidDataException)
            {
                EditJournal.Delete(dir, key);
                MessageBox.Show(this,
                    LT("An earlier unsaved-edit recovery file for this file was damaged and has been removed.",
                       "이 파일의 이전 미저장 편집 복구 파일이 손상되어 삭제했습니다."),
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (snapshot is null) return;

            string summary = LT(
                $"{snapshot.Cells.Length:N0} cell(s), {snapshot.Headers.Length:N0} column name(s), {snapshot.Deleted.Length:N0} deleted row(s), {snapshot.Added.Length:N0} added row(s)",
                $"셀 {snapshot.Cells.Length:N0}개, 컬럼 이름 {snapshot.Headers.Length:N0}개, 삭제 행 {snapshot.Deleted.Length:N0}개, 추가 행 {snapshot.Added.Length:N0}개");
            var answer = MessageBox.Show(this,
                LT($"Unsaved edits from an earlier session were found for this file ({summary}).\n\nYes = recover them (they stay unsaved until you use Save Edits As…)\nNo = discard them",
                   $"이 파일에 대한 이전 세션의 저장되지 않은 편집이 있습니다({summary}).\n\n예 = 복구합니다(편집 내용 저장…을 쓰기 전까지는 저장되지 않은 상태)\n아니요 = 버립니다"),
                ProgramName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) { DeleteJournal(); return; }
            try
            {
                doc.Edits.Restore(snapshot, doc.BaseRowCount, doc.ColumnCount);
                statusLabel.Text = LT($"Recovered unsaved edits ({summary}). Save them with Edit ▸ Save Edits As….",
                                      $"저장되지 않은 편집({summary})을 복구했습니다. 편집 ▸ 편집 내용 저장…으로 저장하세요.");
                UpdateFeatureState();
            }
            catch (InvalidDataException ex)
            {
                DeleteJournal();
                MessageBox.Show(this,
                    LT("The recovery data does not fit this file and was discarded: ", "복구 데이터가 이 파일과 맞지 않아 버렸습니다: ") + ex.Message,
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
