using System.Diagnostics;
using System.Text.RegularExpressions;
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
            bool columnsChanged = false;
            if (edits.HeaderVersion != _seenHeaderVersion)
            {
                _seenHeaderVersion = edits.HeaderVersion;
                columnsChanged = SyncGridColumnsWithHeader();
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
            _settleTimer.Interval = structural || columnsChanged ? 60 : 400;
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

        // ---------------------------------------------------------------- 정규식 바꾸기 · 추출

        private const int MaxRegexChanges = 1_000_000;     // 한 번에 바꾸거나 추출할 수 있는 셀 수 상한(넘으면 정직하게 거부)
        private const int ConfirmRegexChanges = 1_000;     // 이 수를 넘으면 적용 전에 확인
        private const int MaxSelectionScanCells = 200_000; // 이보다 큰 선택은 셀 단위로 훑지 않고 "보이는 모든 컬럼"으로 본다

        /// <summary>현재 뷰의 행 id를 화면 순서대로(필터 적용). 편집 덮개의 키와 같은 값.</summary>
        private static IEnumerable<long> ViewRowIds(VirtualCsvDocument doc)
        {
            int n = doc.DisplayRowCount;
            for (int i = 0; i < n; i++)
            {
                int id = doc.GetRowId(i);
                if (id >= 0) yield return id;
            }
        }

        /// <summary>필터와 무관하게 표 전체(삭제 제외·추가 행 포함)의 행 id를 화면 순서대로.</summary>
        private static IEnumerable<long> AllRowIds(VirtualCsvDocument doc)
        {
            int n = doc.DataRowsAvailable;
            for (int i = 0; i < n; i++)
            {
                int id = doc.GetRowIdAtPosition(i);
                if (id >= 0) yield return id;
            }
        }

        private string[] ColumnDisplayNames()
            => _doc!.Header.Select((h, i) => string.IsNullOrEmpty(h) ? $"Column{i + 1}" : h).ToArray();

        /// <summary>선택한 셀이 걸친 보이는 컬럼들(없으면 현재 셀의 컬럼). 선택이 아주 크면 보이는 모든 컬럼.</summary>
        private List<int> SelectedColumnsForRegex(List<int> visible)
        {
            var cols = new SortedSet<int>();
            if (grid.GetCellCount(DataGridViewElementStates.Selected) > MaxSelectionScanCells) return new List<int>(visible);
            foreach (DataGridViewCell c in grid.SelectedCells)
                if (c.ColumnIndex >= 0 && c.ColumnIndex < grid.ColumnCount && grid.Columns[c.ColumnIndex].Visible) cols.Add(c.ColumnIndex);
            if (cols.Count == 0 && grid.CurrentCell is { ColumnIndex: >= 0 } cc && grid.Columns[cc.ColumnIndex].Visible) cols.Add(cc.ColumnIndex);
            return cols.ToList();
        }

        private static string TimeoutWarning(long timedOut, bool ko)
            => timedOut <= 0 ? "" : ko
                ? $"\n⚠ {timedOut:N0}개 셀이 {RegexSafety.MatchTimeout.TotalMilliseconds:N0}ms를 넘겨 일치하지 않는 것으로 처리했습니다(바뀌지 않음)."
                : $"\n⚠ {timedOut:N0} cell(s) took longer than {RegexSafety.MatchTimeout.TotalMilliseconds:N0} ms and were treated as NOT matching (left unchanged).";

        private string TimeoutWarning(long timedOut) => TimeoutWarning(timedOut, Loc.CurrentLanguage == "ko");

        /// <summary>
        /// 시트 편집 모드: 정규식 찾아 바꾸기. 현재 뷰(필터 적용)의 선택 컬럼 또는 보이는 모든 컬럼에서 일치하는 셀을 바꾸고,
        /// 모든 변경을 되돌리기 한 단계로 기록한다. 시간 초과 셀은 세어서 알리고, 변경이 1,000개를 넘으면 확인하며, 100만 개를 넘으면 거부한다.
        /// </summary>
        private async Task RegexReplaceAsync()
        {
            if (!EditsReady) return;
            if (!_sheetEditing)
            {
                statusLabel.Text = LT("Find & Replace works only in sheet edit mode (Ctrl+Shift+E); Edit Cell changes one cell at a time.",
                                      "찾아 바꾸기는 시트 편집 모드(Ctrl+Shift+E)에서만 됩니다. 셀 편집은 셀 하나씩만 고칩니다.");
                return;
            }
            var doc = _doc!;
            CancelInlineEdit();
            if (doc.DisplayRowCount == 0) { statusLabel.Text = LT("There are no rows to replace in.", "바꿀 행이 없습니다."); return; }
            var visible = PasteColumnOrder();
            if (visible.Count == 0) { statusLabel.Text = LT("There are no visible columns.", "보이는 컬럼이 없습니다."); return; }

            string pattern, replacement;
            bool caseSensitive;
            IReadOnlyList<int> columns;
            using (var dlg = new RegexReplaceDialog(_palette, id => doc.GetRowByIdUncached((int)id), () => ViewRowIds(doc), doc.DisplayRowCount,
                       ColumnDisplayNames(), SelectedColumnsForRegex(visible), visible,
                       id => { int vi = doc.FindViewIndex((int)id); return vi >= 0 ? doc.GetSourceRowNumber(vi) : id + 1; }))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                pattern = dlg.Pattern;
                replacement = dlg.Replacement;
                caseSensitive = dlg.CaseSensitive;
                columns = dlg.Columns.ToList();
            }

            Regex regex;
            try { regex = RegexSafety.Compile(pattern, caseSensitive); }
            catch (RegexPatternException ex)
            {
                MessageBox.Show(this, ex.Message, ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var plan = await RunAnalysisOperationAsync(doc, (_, ct) =>
                RegexReplace.Plan(id => doc.GetRowByIdUncached((int)id), ViewRowIds(doc), columns, regex, replacement, MaxRegexChanges, ct));
            if (IsDisposed || _closing || !ReferenceEquals(doc, _doc)) return;
            if (plan is null)
            {
                statusLabel.Text = LT("Find & Replace was cancelled or failed; nothing was changed.", "찾아 바꾸기가 취소되었거나 실패했습니다. 아무것도 바꾸지 않았습니다.");
                return;
            }

            if (plan.Truncated)
            {
                MessageBox.Show(this,
                    LT($"More than {MaxRegexChanges:N0} cells would change, which is more than one replace can apply. Nothing was changed.\nNarrow the filter or the columns, or make the pattern more specific.",
                       $"바뀔 셀이 {MaxRegexChanges:N0}개를 넘어 한 번에 적용할 수 없습니다. 아무것도 바꾸지 않았습니다.\n필터나 컬럼을 줄이거나 패턴을 더 구체적으로 만드세요.") + TimeoutWarning(plan.CellsTimedOut),
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                statusLabel.Text = LT($"Replace refused: more than {MaxRegexChanges:N0} cells would change.", $"바꾸기 거부: 바뀔 셀이 {MaxRegexChanges:N0}개를 넘습니다.");
                return;
            }

            if (plan.Changes.Count == 0)
            {
                statusLabel.Text = (plan.CellsMatched == 0
                    ? LT($"No cell matched the pattern ({plan.RowsScanned:N0} rows scanned); nothing was changed.", $"일치하는 셀이 없습니다({plan.RowsScanned:N0}행 검사). 아무것도 바꾸지 않았습니다.")
                    : LT($"{plan.CellsMatched:N0} cell(s) matched but the replacement gives the same values; nothing was changed.", $"{plan.CellsMatched:N0}개 셀이 일치하지만 바꿔도 같은 값이라 아무것도 바꾸지 않았습니다."))
                    + TimeoutWarning(plan.CellsTimedOut).Replace("\n", " ");
                if (plan.CellsTimedOut > 0)
                    MessageBox.Show(this, statusLabel.Text, ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (plan.Changes.Count > ConfirmRegexChanges || plan.CellsTimedOut > 0)
            {
                string ask = LT($"Replace in {plan.Changes.Count:N0} cell(s)? (Undo brings them all back in one step.)",
                                $"셀 {plan.Changes.Count:N0}개를 바꿀까요? (되돌리기 한 번으로 전부 복원됩니다.)") + TimeoutWarning(plan.CellsTimedOut);
                if (MessageBox.Show(this, ask, ProgramName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    statusLabel.Text = LT("Replace cancelled; nothing was changed.", "바꾸기를 취소했습니다. 아무것도 바꾸지 않았습니다.");
                    return;
                }
            }

            string description = LT($"Regex replace: {plan.Changes.Count:N0} cells", $"정규식 바꾸기: {plan.Changes.Count:N0}셀");
            int changed = ApplyCellChanges(plan.Changes, description);
            statusLabel.Text = LT($"Replaced {changed:N0} cell(s) ({plan.CellsMatched:N0} matched in {plan.RowsScanned:N0} rows). Ctrl+Z undoes the whole replace.",
                                  $"셀 {changed:N0}개를 바꿨습니다({plan.RowsScanned:N0}행에서 {plan.CellsMatched:N0}개 일치). Ctrl+Z로 한 번에 되돌립니다.")
                + TimeoutWarning(plan.CellsTimedOut).Replace("\n", " ");
        }

        /// <summary>
        /// 변경 목록(행 id·컬럼·새 값)을 한 단계로 덮개에 기록한다. 실제로 값이 바뀐 셀 수를 돌려준다.
        /// 확인창 없음 — 정규식 바꾸기와 에이전트 도구가 같이 쓴다. 삭제된 행·범위 밖 컬럼은 건너뛴다.
        /// </summary>
        internal int ApplyCellChanges(IReadOnlyList<RegexCellChange> changes, string description)
        {
            var doc = _doc;
            if (doc is null || changes.Count == 0) return 0;
            int changed = RegexReplace.Apply(doc, changes, description);
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
            return changed;
        }

        /// <summary>
        /// 시트 편집 모드: 정규식 추출. 원본 컬럼의 값에서 캡처 그룹을 뽑아 표 맨 뒤 새 컬럼에 채운다(표 전체 행, 필터 무관).
        /// 컬럼과 값은 편집 덮개에 한 단계로 기록되어 저장 파일에 쓰이고, 되돌리기 한 번으로 사라진다.
        /// </summary>
        private async Task ExtractColumnAsync()
        {
            if (!EditsReady) return;
            if (!_sheetEditing)
            {
                statusLabel.Text = LT("Extract to New Column works only in sheet edit mode (Ctrl+Shift+E).",
                                      "새 컬럼에 추출은 시트 편집 모드(Ctrl+Shift+E)에서만 됩니다.");
                return;
            }
            var doc = _doc!;
            if (!doc.CanEditStructure)
            {
                statusLabel.Text = LT("A column cannot be added to a table this large.", "이렇게 큰 표에는 컬럼을 추가할 수 없습니다.");
                return;
            }
            CancelInlineEdit();
            if (doc.DataRowsAvailable == 0) { statusLabel.Text = LT("There are no rows to extract from.", "추출할 행이 없습니다."); return; }

            Regex regex;
            int group, source;
            string newName;
            using (var dlg = new ExtractColumnDialog(_palette, ColumnDisplayNames(), grid.CurrentCell?.ColumnIndex ?? 0,
                       id => doc.GetRowByIdUncached((int)id), () => ViewRowIds(doc), name => ValidateColumnName(-1, name)))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { (regex, group) = ExtractColumnDialog.Compile(dlg.Pattern, dlg.GroupSpec, dlg.CaseSensitive); }
                catch (RegexPatternException ex)
                {
                    MessageBox.Show(this, ex.Message, ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                source = dlg.SourceColumn;
                newName = dlg.NewColumnName;
            }

            var plan = await RunAnalysisOperationAsync(doc, (_, ct) =>
                RegexExtract.Plan(id => doc.GetRowByIdUncached((int)id), AllRowIds(doc), source, regex, group, MaxRegexChanges, ct));
            if (IsDisposed || _closing || !ReferenceEquals(doc, _doc)) return;
            if (plan is null)
            {
                statusLabel.Text = LT("Extract was cancelled or failed; no column was added.", "추출이 취소되었거나 실패했습니다. 컬럼을 추가하지 않았습니다.");
                return;
            }

            string counts = LT($"{plan.RowsMatched:N0} of {plan.RowsScanned:N0} rows matched, {plan.RowsNotMatched:N0} did not",
                               $"{plan.RowsScanned:N0}행 중 {plan.RowsMatched:N0}행 일치, {plan.RowsNotMatched:N0}행 불일치");
            if (plan.Truncated)
            {
                MessageBox.Show(this,
                    LT($"More than {MaxRegexChanges:N0} rows would get a value, which is more than one extraction can store. No column was added.\nMake the pattern more specific.",
                       $"값이 들어갈 행이 {MaxRegexChanges:N0}개를 넘어 한 번에 저장할 수 없습니다. 컬럼을 추가하지 않았습니다.\n패턴을 더 구체적으로 만드세요.") + TimeoutWarning(plan.CellsTimedOut),
                    ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                statusLabel.Text = LT("Extract refused: too many values.", "추출 거부: 값이 너무 많습니다.");
                return;
            }
            if (plan.Values.Count == 0)
            {
                string none = LT($"Nothing was extracted ({counts}); no column was added.", $"추출된 값이 없습니다({counts}). 컬럼을 추가하지 않았습니다.")
                    + TimeoutWarning(plan.CellsTimedOut).Replace("\n", " ");
                statusLabel.Text = none;
                MessageBox.Show(this, none, ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var edits = doc.Edits;
            int newCol = doc.ColumnCount;
            using (edits.BeginStep(LT($"Extract to column '{newName}': {plan.Values.Count:N0} values", $"정규식 추출 → '{newName}': {plan.Values.Count:N0}값")))
            {
                edits.AppendColumn(newName, doc.RawColumnCount);
                foreach (var (row, value) in plan.Values) edits.Set((int)row, newCol, value, "");
            }

            try
            {
                if (newCol < grid.ColumnCount && grid.RowCount > 0)
                {
                    int vi = Math.Clamp(grid.CurrentCell?.RowIndex ?? 0, 0, grid.RowCount - 1);
                    grid.CurrentCell = grid[newCol, vi];
                }
            }
            catch { /* 레이아웃 중 일시 예외 무시 */ }
            statusLabel.Text = LT($"Added column '{newName}' with {plan.Values.Count:N0} value(s) ({counts}; the rest are empty). It is saved with the file; Ctrl+Z removes it.",
                                  $"컬럼 '{newName}'을 추가했습니다(값 {plan.Values.Count:N0}개, {counts}, 나머지는 빈 값). 저장 파일에 포함되며 Ctrl+Z로 제거됩니다.")
                + TimeoutWarning(plan.CellsTimedOut).Replace("\n", " ");
        }

        // 그리드 컬럼의 물리 번호(만들 때 이름을 "col"+물리 번호로 붙인다). 컬럼을 삭제·복원해도 이 번호로 어느 컬럼인지 알 수 있다.
        private static int PhysicalIdOf(DataGridViewColumn col)
            => col.Name.StartsWith("col", StringComparison.Ordinal) && int.TryParse(col.Name.AsSpan(3), out int p) ? p : col.Index;

        /// <summary>
        /// 덮개가 컬럼을 추가·삭제·복원했으면(추출 · 삭제 · 되돌리기 포함) 그리드 컬럼과 필터 콤보를 문서 헤더에 맞춘다.
        /// 컬럼 번호로 보관하는 뷰 상태(정렬·컬럼 필터·숨김·타입 지정·식 필터)는 같은 번호 대응표로 함께 옮기고,
        /// 사라지는 컬럼을 가리키던 것은 정리한다. 바뀌었으면 true.
        /// </summary>
        private bool SyncGridColumnsWithHeader()
        {
            if (_doc is null) return false;
            var doc = _doc;
            int want = doc.Header.Length;
            int[] desired = doc.Edits.VisiblePhysicalColumns(doc.RawColumnCount);
            if (desired.Length != want) desired = Enumerable.Range(0, want).ToArray();

            var cur = new int[grid.Columns.Count];
            for (int i = 0; i < cur.Length; i++) cur[i] = PhysicalIdOf(grid.Columns[i]);
            if (cur.AsSpan().SequenceEqual(desired)) return false;

            if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
            var map = new int[cur.Length]; // 옛 보이는 번호 → 새 번호(사라지면 -1)
            bool removedAny = false, shifted = false;
            for (int i = 0; i < cur.Length; i++)
            {
                map[i] = Array.IndexOf(desired, cur[i]);
                if (map[i] < 0) removedAny = true;
                if (map[i] != i) shifted = true;
            }

            // 컬럼 이동이면 그리드 컬럼 객체를 옮긴다(폭·숨김 유지). 옮기는 동안 잃기 쉬운 현재 셀·가로 스크롤 위치를 물리 번호로 기억해 되살린다.
            int curRow = grid.CurrentCell is { RowIndex: >= 0 } at0 ? at0.RowIndex : -1;
            int curPhys = grid.CurrentCell is { ColumnIndex: >= 0 } at1 && at1.ColumnIndex < cur.Length ? cur[at1.ColumnIndex] : -1;
            int firstPhys = -1;
            try { int fc = grid.FirstDisplayedScrollingColumnIndex; if (fc >= 0 && fc < cur.Length) firstPhys = cur[fc]; } catch { /* 레이아웃 중 일시 예외 무시 */ }
            bool reordered = false;
            int oldComboCol = filterColumnCombo.SelectedIndex - 1;
            RemapColumnState(map, removedAny, shifted, oldComboCol);

            // 현재 셀이 사라질 컬럼에 있으면 먼저 옮긴다(그리드가 컬럼 제거 중 현재 셀을 잃지 않게).
            if (grid.CurrentCell is { RowIndex: >= 0 } at && at.ColumnIndex < map.Length && map[at.ColumnIndex] < 0)
            {
                int target = -1;
                for (int i = at.ColumnIndex + 1; i < map.Length && target < 0; i++) if (map[i] >= 0) target = i;
                for (int i = at.ColumnIndex - 1; i >= 0 && target < 0; i--) if (map[i] >= 0) target = i;
                if (target >= 0) { try { grid.CurrentCell = grid[target, at.RowIndex]; } catch { /* 레이아웃 중 일시 예외 무시 */ } }
            }

            for (int i = cur.Length - 1; i >= 0; i--)
            {
                if (map[i] >= 0) continue;
                grid.Columns.RemoveAt(i);
                if (i + 1 < filterColumnCombo.Items.Count) filterColumnCombo.Items.RemoveAt(i + 1);
            }
            for (int j = 0; j < desired.Length; j++)
            {
                if (j < grid.Columns.Count && PhysicalIdOf(grid.Columns[j]) == desired[j]) continue;
                int existing = -1;
                for (int i = j + 1; i < grid.Columns.Count; i++)
                    if (PhysicalIdOf(grid.Columns[i]) == desired[j]) { existing = i; break; }
                if (existing >= 0)
                {
                    // 이미 있는 컬럼이 뒤에 있다 = 이동. 객체째 옮겨 폭·숨김 상태를 지킨다.
                    var moving = grid.Columns[existing];
                    grid.Columns.RemoveAt(existing);
                    grid.Columns.Insert(j, moving);
                    if (existing + 1 < filterColumnCombo.Items.Count)
                    {
                        object? item = filterColumnCombo.Items[existing + 1];
                        filterColumnCombo.Items.RemoveAt(existing + 1);
                        filterColumnCombo.Items.Insert(j + 1, item ?? "");
                    }
                    reordered = true;
                    continue;
                }
                string name = string.IsNullOrEmpty(doc.Header[j]) ? $"Column{j + 1}" : doc.Header[j];
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = name, Name = "col" + desired[j], SortMode = DataGridViewColumnSortMode.Programmatic,
                    Width = 130, Resizable = DataGridViewTriState.True,
                };
                if (j >= grid.Columns.Count) grid.Columns.Add(col); else grid.Columns.Insert(j, col);
                if (j + 1 >= filterColumnCombo.Items.Count) filterColumnCombo.Items.Add(name); else filterColumnCombo.Items.Insert(j + 1, name);
            }
            while (filterColumnCombo.Items.Count > want + 1) filterColumnCombo.Items.RemoveAt(filterColumnCombo.Items.Count - 1);
            int newCombo = oldComboCol >= 0 && oldComboCol < map.Length && map[oldComboCol] >= 0 ? map[oldComboCol] + 1 : 0;
            if (filterColumnCombo.SelectedIndex != newCombo) filterColumnCombo.SelectedIndex = newCombo;
            NormalizeGridDisplayOrder();
            if (reordered) RestoreGridPositionAfterReorder(desired, curRow, curPhys, firstPhys);

            if (_sortDroppedByColumnEdit)
            {
                _sortDroppedByColumnEdit = false;
                UpdateSortGlyphs();
                if (_sortKeys.Count == 0) { doc.ResetViewOrder(); grid.Invalidate(); }
            }
            else if (shifted) UpdateSortGlyphs();
            return true;
        }

        private bool _sortDroppedByColumnEdit;

        // map[옛 번호] = 새 번호 또는 -1(사라짐). shifted = 번호가 하나라도 달라짐(중간 컬럼 삭제·복원).
        private void RemapColumnState(int[] map, bool removedAny, bool shifted, int oldComboCol)
        {
            int Map(int c) => (uint)c < (uint)map.Length ? map[c] : -1;

            // 정렬 키
            var keys = _sortKeys.ToArray();
            _sortKeys.Clear();
            foreach (var k in keys)
            {
                int n = Map(k.Column);
                if (n < 0) { _sortDroppedByColumnEdit = true; continue; }
                _sortKeys.Add(new SortKey(n, k.Ascending));
            }

            // 컬럼 필터(선택값·날짜·숫자·텍스트)
            _columnFilters.ValueFilters.RemoveAll(f => Map(f.Column) < 0);
            _columnFilters.DateFilters.RemoveAll(f => Map(f.Column) < 0);
            _columnFilters.NumericFilters.RemoveAll(f => Map(f.Column) < 0);
            _columnFilters.TextFilters.RemoveAll(f => Map(f.Column) < 0);
            foreach (var f in _columnFilters.ValueFilters) f.Column = Map(f.Column);
            foreach (var f in _columnFilters.DateFilters) f.Column = Map(f.Column);
            foreach (var f in _columnFilters.NumericFilters) f.Column = Map(f.Column);
            foreach (var f in _columnFilters.TextFilters) f.Column = Map(f.Column);

            // 수동 타입 지정 · 숨긴 컬럼
            if (_manualTypeOverrides.Count > 0)
            {
                var old = _manualTypeOverrides.ToArray();
                _manualTypeOverrides.Clear();
                foreach (var (c, t) in old) if (Map(c) is var n && n >= 0) _manualTypeOverrides[n] = t;
            }
            if (_hiddenColumns.Count > 0)
            {
                var old = _hiddenColumns.ToArray();
                _hiddenColumns.Clear();
                foreach (int c in old) if (Map(c) is var n && n >= 0) _hiddenColumns.Add(n);
            }

            if (!shifted) return;

            // 툴바 텍스트 필터: 특정 컬럼이면 새 번호로 술어를 다시 만든다(사라진 컬럼이면 해제).
            if (_textCondition is not null && oldComboCol >= 0)
            {
                int n = Map(oldComboCol);
                if (n >= 0) _textCondition = BuildContainsPredicate(filterTextBox.Text, n);
                else { _textCondition = null; _textConditionDesc = ""; filterTextBox.Text = ""; }
            }
            // 식 필터는 식 글자를 새 헤더로 다시 컴파일한다. 컬럼 위치로 만든 "셀 값" 필터는 어느 컬럼인지 알 수 없어 해제한다.
            int dropped = 0;
            for (int i = _valueConditions.Count - 1; i >= 0; i--)
            {
                var (desc, _, expr) = _valueConditions[i];
                if (expr is not null)
                {
                    try { _valueConditions[i] = (desc, AdvancedFilterExpression.Compile(expr, _doc!.Header).Predicate, expr); continue; }
                    catch (AdvancedFilterExpressionException) { /* 삭제한 컬럼을 쓰는 식 */ }
                }
                _valueConditions.RemoveAt(i);
                dropped++;
            }
            if (dropped > 0)
                statusLabel.Text = LT($"{dropped} filter(s) that depended on column positions or on the removed column were cleared.",
                                      $"컬럼 위치나 삭제된 컬럼에 의존하던 필터 {dropped}개를 해제했습니다.");
        }

        // ---------------------------------------------------------------- 컬럼 삽입(원하는 위치) / 이동 / 삭제

        private const int MaxColumnFillRows = 1_000_000;  // 새 컬럼을 같은 값으로 채울 수 있는 행 수 상한(셀 편집 하나씩 기록)
        private const int MaxAgentInsertRows = 10_000;     // 에이전트가 한 번에 삽입할 수 있는 행 수

        private void EnsureEditable()
        {
            if (_doc is null || !_doc.IndexingComplete)
                throw new InvalidOperationException("The file is still being indexed (or nothing is open). Retry when indexing finishes.");
            if (_busy) throw new InvalidOperationException("Another operation is running. Retry when it finishes.");
            if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
        }

        private void InsertColumnFromUi()
        {
            if (!_sheetEditing || _doc is null) return;
            if (!EditsReady)
            {
                statusLabel.Text = LT("Wait for the current operation to finish, then insert the column.", "진행 중인 작업이 끝난 뒤 컬럼을 삽입하세요.");
                return;
            }
            CancelInlineEdit();
            string name = "", fill = "";
            int count = _doc.ColumnCount;
            int cur = grid.CurrentCell is { ColumnIndex: >= 0 } at && at.ColumnIndex < count ? at.ColumnIndex : -1;
            var places = new List<(string Label, int Position)>();
            if (cur >= 0)
            {
                string curName = grid.Columns[cur].HeaderText;
                places.Add((LT($"Before '{curName}'", $"'{curName}' 앞"), cur));
                places.Add((LT($"After '{curName}'", $"'{curName}' 뒤"), cur + 1));
            }
            places.Add((LT("At the left end", "맨 왼쪽"), 0));
            places.Add((LT("At the right end", "맨 오른쪽"), count));
            int place = cur >= 0 ? 1 : places.Count - 1; // 현재 컬럼 뒤가 기본, 현재 컬럼이 없으면 맨 오른쪽
            while (true)
            {
                using var dlg = new ParamDialog(LT("Insert Column", "컬럼 삽입"), _palette);
                var nameBox = dlg.AddText(LT("Column name", "컬럼 이름"), name);
                var placeBox = dlg.AddCombo(LT("Position", "위치"), places.Select(p => p.Label), place);
                var fillBox = dlg.AddText(LT("Value for every row (optional)", "모든 행의 값 (선택)"), fill);
                dlg.AddNote(LT("The new column is empty unless you give a value. Undo removes it; it is saved with the file in this position.",
                               "새 컬럼은 값을 적지 않으면 비어 있습니다. 되돌리기로 제거할 수 있고 저장 파일에 이 위치로 포함됩니다."));
                if (!dlg.ShowOk(this)) return;
                name = nameBox.Text.Trim();
                fill = fillBox.Text;
                place = Math.Clamp(placeBox.SelectedIndex, 0, places.Count - 1);
                string? problem = ValidateColumnName(-1, name);
                if (problem is not null) { MessageBox.Show(this, problem, ProgramName, MessageBoxButtons.OK, MessageBoxIcon.Information); continue; }
                break;
            }
            try
            {
                int col = AddColumnCore(name, fill, LT($"Insert column '{name}'", $"컬럼 '{name}' 삽입"), places[place].Position);
                statusLabel.Text = LT($"Inserted column '{name}' at position {col + 1}. Ctrl+Z removes it.", $"컬럼 '{name}'을(를) {col + 1}번째 위치에 삽입했습니다. Ctrl+Z로 제거합니다.");
                SelectColumnInCurrentRow(col);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                statusLabel.Text = ex.Message;
            }
            UpdateFeatureState();
        }

        private void SelectColumnInCurrentRow(int col)
        {
            try
            {
                if (col >= 0 && col < grid.ColumnCount && grid.RowCount > 0 && grid.Columns[col].Visible)
                {
                    int vi = Math.Clamp(grid.CurrentCell?.RowIndex ?? 0, 0, grid.RowCount - 1);
                    grid.CurrentCell = grid[col, vi];
                }
            }
            catch { /* 레이아웃 중 일시 예외 무시 */ }
        }

        /// <summary>
        /// 컬럼을 position(0..ColumnCount, 생략하거나 ColumnCount면 맨 뒤)에 삽입한다(한 단계). fill이 비어 있지 않으면 모든 행(삭제 제외·추가 행 포함)에 그 값을 넣는다.
        /// 새 컬럼의 0-based 번호(= position)를 돌려준다. 이름 규칙은 컬럼 이름 변경과 같다(비어 있지 않음·한 줄·중복 불가).
        /// </summary>
        private int AddColumnCore(string name, string? fill, string description, int? position = null)
        {
            EnsureEditable();
            var doc = _doc!;
            name = (name ?? "").Trim();
            string? problem = ValidateColumnName(-1, name);
            if (problem is not null) throw new ArgumentException(problem);
            int at = position ?? doc.ColumnCount;
            if (at < 0 || at > doc.ColumnCount) throw new ArgumentException($"position must be 0..{doc.ColumnCount} (0 = before the first column, {doc.ColumnCount} = at the end).");
            fill ??= "";
            if (fill.Length > 0 && doc.DataRowsAvailable > MaxColumnFillRows)
                throw new ArgumentException($"A fill value can be set for at most {MaxColumnFillRows:N0} rows; this table has {doc.DataRowsAvailable:N0}. Add the column empty and fill part of it with a regex extract or csv.edit_cells.");
            var edits = doc.Edits;
            int col;
            using (edits.BeginStep(description))
            {
                col = edits.InsertColumn(name, at, doc.ColumnCount, doc.RawColumnCount);
                if (fill.Length > 0)
                    foreach (long id in AllRowIds(doc)) edits.Set((int)id, col, fill, "");
            }
            return col;
        }

        private void DeleteColumnFromUi(int col)
        {
            if (!_sheetEditing || _doc is null) return;
            if (!EditsReady)
            {
                statusLabel.Text = LT("Wait for the current operation to finish, then delete the column.", "진행 중인 작업이 끝난 뒤 컬럼을 삭제하세요.");
                return;
            }
            if (col < 0 || col >= _doc.ColumnCount) return;
            CancelInlineEdit();
            try
            {
                string name = DeleteColumnCore(col, null);
                statusLabel.Text = LT($"Deleted column '{name}' from the table (the original file is untouched). Ctrl+Z restores it; saved files omit it.",
                                      $"컬럼 '{name}'을(를) 표에서 삭제했습니다(원본 파일은 그대로). Ctrl+Z로 복원하며 저장 파일에서 빠집니다.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                statusLabel.Text = ex.Message;
            }
            UpdateFeatureState();
        }

        /// <summary>보이는 컬럼 하나를 삭제 표시한다(한 단계). 삭제한 컬럼의 표시 이름을 돌려준다.</summary>
        private string DeleteColumnCore(int col, string? description)
        {
            EnsureEditable();
            var doc = _doc!;
            if (col < 0 || col >= doc.ColumnCount) throw new ArgumentException($"Column index {col} is out of range (0..{doc.ColumnCount - 1}).");
            if (doc.ColumnCount <= 1) throw new InvalidOperationException(LT("The last remaining column cannot be deleted.", "마지막 남은 컬럼은 삭제할 수 없습니다."));
            string name = string.IsNullOrEmpty(doc.Header[col]) ? $"Column{col + 1}" : doc.Header[col];
            doc.Edits.DeleteColumn(col, doc.ColumnCount, doc.RawColumnCount,
                description ?? LT($"Delete column '{name}'", $"컬럼 '{name}' 삭제"));
            return name;
        }

        // ---- 컬럼 이동

        /// <summary>col의 왼쪽(dir &lt; 0)·오른쪽(dir &gt; 0)에서 가장 가까운 보이는(숨기지 않은) 컬럼. 없으면 -1.</summary>
        private int AdjacentVisibleColumn(int col, int dir)
        {
            for (int c = col + Math.Sign(dir); c >= 0 && c < grid.Columns.Count; c += Math.Sign(dir))
                if (grid.Columns[c].Visible) return c;
            return -1;
        }

        private void MoveCurrentColumn(int dir)
        {
            if (!_sheetEditing || _doc is null || grid.CurrentCell is not { ColumnIndex: >= 0 } cell) return;
            int target = AdjacentVisibleColumn(cell.ColumnIndex, dir);
            if (target >= 0) MoveColumnFromUi(cell.ColumnIndex, target);
        }

        /// <summary>메뉴·드래그가 부르는 컬럼 이동(한 단계). 이동한 컬럼의 현재 행 셀을 선택한다.</summary>
        private void MoveColumnFromUi(int from, int to)
        {
            if (!_sheetEditing || _doc is null) return;
            if (!EditsReady)
            {
                statusLabel.Text = LT("Wait for the current operation to finish, then move the column.", "진행 중인 작업이 끝난 뒤 컬럼을 이동하세요.");
                return;
            }
            CancelInlineEdit();
            try
            {
                string name = MoveColumnCore(from, to, null);
                SelectColumnInCurrentRow(to);
                statusLabel.Text = LT($"Moved column '{name}' to position {to + 1}. Ctrl+Z moves it back; saved files use the new order.",
                                      $"컬럼 '{name}'을(를) {to + 1}번째로 옮겼습니다. Ctrl+Z로 되돌리며 저장 파일에 새 순서가 반영됩니다.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                statusLabel.Text = ex.Message;
            }
            UpdateFeatureState();
        }

        /// <summary>보이는 컬럼 from을 결과 위치 to(0-based)로 옮긴다(한 단계). 옮긴 컬럼의 표시 이름을 돌려준다.</summary>
        private string MoveColumnCore(int from, int to, string? description)
        {
            EnsureEditable();
            var doc = _doc!;
            int n = doc.ColumnCount;
            if (from < 0 || from >= n) throw new ArgumentException($"Column index {from} is out of range (0..{n - 1}).");
            if (to < 0 || to >= n) throw new ArgumentException($"Target position {to} is out of range (0..{n - 1}).");
            if (from == to) throw new ArgumentException($"Column {from} is already at position {to}; nothing to move.");
            string name = string.IsNullOrEmpty(doc.Header[from]) ? $"Column{from + 1}" : doc.Header[from];
            doc.Edits.MoveColumn(from, to, n, doc.RawColumnCount,
                description ?? LT($"Move column '{name}' to position {to + 1}", $"컬럼 '{name}'을(를) {to + 1}번째로 이동"));
            return name;
        }

        // 그리드는 컬럼을 DisplayIndex 순서로 그린다. 컬럼 객체를 옮기거나 중간에 끼워 넣으면 DisplayIndex가 예전 값으로 남아
        // 컬렉션 순서(= 문서 컬럼 번호)와 화면 순서가 어긋나므로, 항상 둘을 같게 맞춘다. 앱 전체가 "컬럼 번호 = 화면 위치"를 전제한다.
        private void NormalizeGridDisplayOrder()
        {
            for (int i = 0; i < grid.Columns.Count; i++)
                if (grid.Columns[i].DisplayIndex != i) grid.Columns[i].DisplayIndex = i;
        }

        // 이동 직후 그리드가 잃기 쉬운 현재 셀(이동한 컬럼을 따라감)·가로 스크롤 위치를 되살린다.
        private void RestoreGridPositionAfterReorder(int[] desired, int curRow, int curPhys, int firstPhys)
        {
            try
            {
                int c = curPhys >= 0 ? Array.IndexOf(desired, curPhys) : -1;
                if (c >= 0 && curRow >= 0 && curRow < grid.RowCount && c < grid.ColumnCount && grid.Columns[c].Visible
                    && (grid.CurrentCell is null || PhysicalIdOf(grid.Columns[grid.CurrentCell.ColumnIndex]) != curPhys))
                    grid.CurrentCell = grid[c, curRow];
                int f = firstPhys >= 0 ? Array.IndexOf(desired, firstPhys) : -1;
                if (f >= 0 && f < grid.ColumnCount && grid.Columns[f].Visible && grid.FirstDisplayedScrollingColumnIndex != f)
                    grid.FirstDisplayedScrollingColumnIndex = f;
            }
            catch { /* 레이아웃 중 일시 예외 무시 */ }
        }

        /// <summary>표시 컬럼 번호 → 파일의 원본 컬럼 번호(시트 편집으로 추가한 컬럼이면 -1). 선언 타입·변수 라벨처럼 파일 기준 메타데이터를 표시 컬럼에 맞추는 데 쓴다.</summary>
        internal int SourceColumnOf(int displayColumn)
        {
            var doc = _doc;
            if (doc is null) return displayColumn;
            int p = doc.Edits.ToPhysical(displayColumn);
            return p >= 0 && p < doc.RawColumnCount ? p : -1;
        }

        /// <summary>파일의 원본 컬럼 순서 목록을 지금 표시 컬럼 순서(이동·삭제·삽입 반영)로 다시 늘어놓는다. 편집이 컬럼을 바꾸지 않았으면 그대로.</summary>
        internal IReadOnlyList<T?>? AlignToDisplayColumns<T>(IReadOnlyList<T?>? bySource) where T : class
        {
            var doc = _doc;
            if (bySource is null || doc is null) return bySource;
            var e = doc.Edits;
            if (!e.HasColumnOrder && !e.HasDeletedColumns && !e.HasAppendedColumns) return bySource;
            var result = new T?[doc.ColumnCount];
            for (int i = 0; i < result.Length; i++)
            {
                int s = SourceColumnOf(i);
                result[i] = s >= 0 && s < bySource.Count ? bySource[s] : null;
            }
            return result;
        }

        // ---- 헤더 드래그로 컬럼 이동(시트 편집 모드). 그리드 자체의 AllowUserToOrderColumns는 쓰지 않는다 —
        //      그리드 컬럼 번호 = 문서 컬럼 번호라는 전제가 깨지지 않도록, 놓는 순간 덮개에 한 단계로 기록하고 그리드는 그 결과를 따라간다.

        private int _colDragFrom = -1, _colDragTo = -1;
        private Point _colDragStart;
        private bool _colDragActive, _colDragCancelled, _colDragSuppressClick;

        private void OnColumnDragMouseDown(object? sender, MouseEventArgs e)
        {
            _colDragFrom = -1;
            _colDragActive = _colDragCancelled = false;
            if (e.Button != MouseButtons.Left || !_sheetEditing || !EditsReady) return;
            var hit = grid.HitTest(e.X, e.Y);
            if (hit.Type != DataGridViewHitTestType.ColumnHeader || hit.ColumnIndex < 0) return;
            var rect = grid.GetColumnDisplayRectangle(hit.ColumnIndex, false);
            if (e.X >= rect.Right - 5 || e.X <= rect.Left + 4) return; // 폭 조절 경계는 그리드가 처리한다
            if (IsFilterableColumn(hit.ColumnIndex) && e.X - rect.Left >= grid.Columns[hit.ColumnIndex].Width - 18) return; // 깔때기 영역 = 필터 팝오버
            _colDragFrom = hit.ColumnIndex;
            _colDragStart = e.Location;
        }

        private void OnColumnDragMouseMove(object? sender, MouseEventArgs e)
        {
            if (_colDragFrom < 0) return;
            if (e.Button != MouseButtons.Left || !_sheetEditing) { EndColumnDrag(); return; }
            if (!_colDragActive)
            {
                var drag = SystemInformation.DragSize;
                if (Math.Abs(e.X - _colDragStart.X) < drag.Width && Math.Abs(e.Y - _colDragStart.Y) < drag.Height) return;
                _colDragActive = true;
                grid.Cursor = Cursors.SizeAll;
            }
            if (_colDragCancelled) return;
            AutoScrollForColumnDrag(e.X);
            int to = ColumnDropTarget(e.X);
            if (to != _colDragTo) { _colDragTo = to; grid.Invalidate(); }
        }

        private void OnColumnDragMouseUp(object? sender, MouseEventArgs e)
        {
            if (_colDragFrom < 0) return;
            int from = _colDragFrom, to = _colDragTo;
            bool active = _colDragActive, cancelled = _colDragCancelled;
            if (active) _colDragSuppressClick = true; // 같은 헤더에 놓았을 때 그리드가 클릭(정렬)으로 처리하지 않게
            EndColumnDrag();
            if (!active) return;
            // 클릭 이벤트가 같은 메시지 안에서 끝난 뒤에 이동한다(그리드 안에서 컬럼을 바꾸지 않으려고).
            BeginInvoke(new Action(() =>
            {
                _colDragSuppressClick = false;
                if (!cancelled && !IsDisposed && to >= 0 && to != from) MoveColumnFromUi(from, to);
            }));
        }

        private void OnColumnDragKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Escape || !_colDragActive) return;
            _colDragCancelled = true;
            _colDragTo = -1;
            grid.Cursor = Cursors.Default;
            grid.Invalidate();
            e.Handled = true;
        }

        private void EndColumnDrag()
        {
            bool repaint = _colDragActive;
            _colDragFrom = _colDragTo = -1;
            _colDragActive = _colDragCancelled = false;
            grid.Cursor = Cursors.Default;
            if (repaint) grid.Invalidate();
        }

        // 놓을 위치 = 마우스 아래 컬럼의 자리(이동 뒤 그 번호가 된다). 숨긴 컬럼은 건너뛴다.
        private int ColumnDropTarget(int x)
        {
            int last = -1;
            for (int c = 0; c < grid.ColumnCount; c++)
            {
                if (!grid.Columns[c].Visible) continue;
                var r = grid.GetColumnDisplayRectangle(c, false);
                if (r.Width <= 0) continue;
                last = c;
                if (x < r.Right) return c;
            }
            return last >= 0 ? last : _colDragFrom;
        }

        private void AutoScrollForColumnDrag(int x)
        {
            try
            {
                int first = grid.FirstDisplayedScrollingColumnIndex;
                if (first < 0) return;
                if (x < grid.RowHeadersWidth + 24)
                {
                    int prev = AdjacentVisibleColumn(first, -1);
                    if (prev >= 0) grid.FirstDisplayedScrollingColumnIndex = prev;
                }
                else if (x > grid.ClientSize.Width - 24)
                {
                    int lastCol = grid.Columns.GetLastColumn(DataGridViewElementStates.Visible, DataGridViewElementStates.None)?.Index ?? -1;
                    var lastRect = lastCol >= 0 ? grid.GetColumnDisplayRectangle(lastCol, false) : Rectangle.Empty;
                    int next = AdjacentVisibleColumn(first, +1);
                    if (next >= 0 && !(lastRect.Width > 0 && lastRect.Right <= grid.ClientSize.Width)) grid.FirstDisplayedScrollingColumnIndex = next;
                }
            }
            catch { /* 레이아웃 중 일시 예외 무시 */ }
        }

        // 놓을 자리 표시: 끌고 있는 헤더를 옅게 칠하고, 놓일 경계에 굵은 선을 긋는다(오른쪽으로 옮기면 대상 컬럼의 오른쪽, 왼쪽이면 왼쪽).
        private void OnColumnDragPaint(object? sender, PaintEventArgs e)
        {
            if (!_colDragActive || _colDragCancelled || _colDragTo < 0 || _colDragTo >= grid.ColumnCount || _colDragFrom >= grid.ColumnCount) return;
            var src = grid.GetColumnDisplayRectangle(_colDragFrom, false);
            if (src.Width > 0)
            {
                using var tint = new SolidBrush(Color.FromArgb(70, SystemColors.Highlight));
                e.Graphics.FillRectangle(tint, src.Left, 0, src.Width, grid.ColumnHeadersHeight);
            }
            if (_colDragTo == _colDragFrom) return;
            var dst = grid.GetColumnDisplayRectangle(_colDragTo, false);
            if (dst.Width <= 0) return;
            int x = _colDragTo > _colDragFrom ? dst.Right - 1 : dst.Left;
            using var bar = new Pen(SystemColors.Highlight, 3);
            e.Graphics.DrawLine(bar, x, 0, x, grid.ClientSize.Height);
        }

        // ---------------------------------------------------------------- 에이전트용 편집 진입점 (UI 스레드, 시트 편집 모드와 무관)
        // 실패는 ArgumentException(인자 문제) / InvalidOperationException(상태 문제)로 알린다. 모두 되돌리기 한 단계.

        /// <summary>빈 행 count개를 삽입해 첫 새 행이 rowNumber(1-based, 편집 후 순서)가 되게 한다. 표 끝 다음 번호면 맨 끝에 추가. 첫 새 행 번호를 돌려준다.</summary>
        internal int AgentInsertRows(long rowNumber, int count, string description)
        {
            EnsureEditable();
            var doc = _doc!;
            if (!doc.CanEditStructure) throw new InvalidOperationException("Rows cannot be inserted or deleted in a table this large.");
            if (count < 1 || count > MaxAgentInsertRows) throw new ArgumentException($"count must be 1..{MaxAgentInsertRows:N0}.");
            int available = doc.DataRowsAvailable;
            if (rowNumber < 1 || rowNumber > (long)available + 1)
                throw new ArgumentException($"row_number must be 1..{(long)available + 1:N0} (use {(long)available + 1:N0} to append at the end).");

            int anchor;
            if (rowNumber > available) anchor = available > 0 ? doc.GetRowIdAtPosition(available - 1) : -1;
            else
            {
                int target = doc.GetRowIdAtPosition((int)(rowNumber - 1));
                anchor = target < 0 ? -1 : doc.PredecessorId(target);
            }
            int firstId = -1;
            using (doc.Edits.BeginStep(description))
            {
                for (int i = 0; i < count; i++)
                {
                    anchor = doc.Edits.AddRow(anchor, doc.ColumnCount, doc.BaseRowCount);
                    if (i == 0) firstId = anchor;
                }
            }
            int vi = doc.FindViewIndex(firstId);
            if (vi >= 0) SelectViewRow(vi);
            else _pendingSelectRowId = firstId;
            statusLabel.Text = LT($"Inserted {count:N0} row(s) at row {rowNumber:N0}. Ctrl+Z undoes.", $"{rowNumber:N0}행 위치에 행 {count:N0}개를 삽입했습니다. Ctrl+Z로 되돌립니다.");
            return (int)rowNumber;
        }

        /// <summary>행 번호(1-based, 편집 후 순서, 필터와 무관)로 행을 삭제한다. 삭제한 행 수를 돌려준다.</summary>
        internal int AgentDeleteRows(IReadOnlyList<long> rowNumbers, string description)
        {
            EnsureEditable();
            var doc = _doc!;
            if (!doc.CanEditStructure) throw new InvalidOperationException("Rows cannot be inserted or deleted in a table this large.");
            if (rowNumbers.Count == 0) throw new ArgumentException("No row numbers were given.");
            int available = doc.DataRowsAvailable;
            var ids = new List<int>(rowNumbers.Count);
            foreach (long n in rowNumbers)
            {
                if (n < 1 || n > available) throw new ArgumentException($"Row {n:N0} does not exist (the table has {available:N0} rows).");
                int id = doc.GetRowIdAtPosition((int)(n - 1));
                if (id < 0) throw new ArgumentException($"Row {n:N0} does not exist.");
                ids.Add(id);
            }
            int deleted = doc.Edits.DeleteRows(ids, description);
            if (grid.RowCount > 0 && grid.CurrentCell is null) SelectViewRow(0);
            statusLabel.Text = LT($"Deleted {deleted:N0} row(s); later row numbers shift up. Ctrl+Z undoes.",
                                  $"행 {deleted:N0}개를 삭제했습니다(뒤 행 번호가 당겨집니다). Ctrl+Z로 되돌립니다.");
            return deleted;
        }

        /// <summary>컬럼을 표의 맨 끝에 추가한다(원하는 위치는 AgentInsertColumn). fill = 모든 행에 넣을 값(null/빈 값 = 빈 컬럼, 행 수 100만 이하). 새 컬럼의 0-based 번호.</summary>
        internal int AgentAddColumn(string name, string? fill, string description)
        {
            int col = AddColumnCore(name, fill, description);
            SelectColumnInCurrentRow(col);
            statusLabel.Text = LT($"Added column '{name.Trim()}' at the right end. Ctrl+Z removes it.", $"컬럼 '{name.Trim()}'을(를) 맨 오른쪽에 추가했습니다. Ctrl+Z로 제거합니다.");
            return col;
        }

        /// <summary>
        /// 컬럼을 position(삽입 후 새 컬럼의 0-based 번호, 0..ColumnCount — ColumnCount = 맨 끝)에 삽입한다. 한 단계.
        /// fill = 모든 행에 넣을 값(null/빈 값 = 빈 컬럼, 행 수 100만 이하). 삽입한 컬럼의 0-based 번호(= position)를 돌려준다.
        /// </summary>
        internal int AgentInsertColumn(string name, int position, string? fill, string description)
        {
            int col = AddColumnCore(name, fill, description, position);
            SelectColumnInCurrentRow(col);
            statusLabel.Text = LT($"Inserted column '{name.Trim()}' at position {col + 1}. Ctrl+Z removes it.", $"컬럼 '{name.Trim()}'을(를) {col + 1}번째 위치에 삽입했습니다. Ctrl+Z로 제거합니다.");
            return col;
        }

        /// <summary>
        /// 보이는 컬럼 from(현재 표시 순서의 0-based 번호)을 이동해 결과 번호가 to가 되게 한다(나머지는 밀린다). 한 단계.
        /// from == to나 범위 밖이면 ArgumentException. 옮긴 컬럼 이름을 돌려준다.
        /// </summary>
        internal string AgentMoveColumn(int from, int to, string description)
        {
            string name = MoveColumnCore(from, to, description);
            SelectColumnInCurrentRow(to);
            statusLabel.Text = LT($"Moved column '{name}' to position {to + 1}. Ctrl+Z moves it back; saved files use the new order.",
                                  $"컬럼 '{name}'을(를) {to + 1}번째로 옮겼습니다. Ctrl+Z로 되돌리며 저장 파일에 새 순서가 반영됩니다.");
            return name;
        }

        /// <summary>보이는 컬럼(원본·추가 모두)을 삭제 표시한다. 뒤 컬럼 번호가 1씩 당겨진다. 삭제한 컬럼 이름을 돌려준다.</summary>
        internal string AgentDeleteColumn(int column, string description)
        {
            string name = DeleteColumnCore(column, description);
            statusLabel.Text = LT($"Deleted column '{name}' from the table. Ctrl+Z restores it; saved files omit it.",
                                  $"컬럼 '{name}'을(를) 표에서 삭제했습니다. Ctrl+Z로 복원하며 저장 파일에서 빠집니다.");
            return name;
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
                $"{snapshot.Cells.Length:N0} cell(s), {snapshot.Headers.Length:N0} column name(s), {snapshot.Deleted.Length:N0} deleted row(s), {snapshot.Added.Length:N0} added row(s), {snapshot.AppendedColumns?.Length ?? 0:N0} new column(s)",
                $"셀 {snapshot.Cells.Length:N0}개, 컬럼 이름 {snapshot.Headers.Length:N0}개, 삭제 행 {snapshot.Deleted.Length:N0}개, 추가 행 {snapshot.Added.Length:N0}개, 새 컬럼 {snapshot.AppendedColumns?.Length ?? 0:N0}개");
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
