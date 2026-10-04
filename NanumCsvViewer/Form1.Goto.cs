using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // 셀로 이동: 주소 상자(Excel 이름 상자처럼 편집 가능)와 Ctrl+G 대화상자가 같은 문법을 쓴다.
    //   120 · R120C3 · C3 · 이름:120 · [이름]120  — 해석은 Csv/CellAddress.cs(순수 클래스).
    public partial class Form1
    {
        private bool _addressWired;

        private void WireAddressBox()
        {
            if (_addressWired) return;
            _addressWired = true;
            cellAddressBox.GotFocus += (_, _) => BeginInvoke(new Action(() => { if (cellAddressBox.Focused) cellAddressBox.SelectAll(); }));
            cellAddressBox.Leave += (_, _) => { if (_doc is not null) OnCurrentCellChanged(grid, EventArgs.Empty); };
            cellAddressBox.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    if (await GoToAddressAsync(cellAddressBox.Text, notifyInvalid: true)) grid.Focus();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    grid.Focus();
                }
            };
            var tip = new ToolTip();
            tip.SetToolTip(cellAddressBox, LT(
                "Go to cell — type a row number (120), R120C3, C3 (column), or Name:120 / [Name]120, then press Enter",
                "셀로 이동 — 행 번호(120), R120C3, C3(컬럼), 이름:120 / [이름]120을 입력하고 Enter"));
        }

        /// <summary>
        /// 주소 문자열로 이동해 그 셀을 선택한다. 잘못된 입력·없는 컬럼·현재 보기에 없는 행이면 false(상태 표시줄에 이유).
        /// 행이 없으면 현재 행 유지, 컬럼이 없으면 현재 컬럼 유지.
        /// </summary>
        internal async Task<bool> GoToAddressAsync(string text, bool notifyInvalid = true)
        {
            var doc = _doc;
            if (doc is null || doc.DisplayRowCount == 0)
            {
                if (notifyInvalid) statusLabel.Text = LT("There is no data to go to.", "이동할 데이터가 없습니다.");
                return false;
            }
            var result = await ResolveAndJumpAsync(text);
            if (result.Error is { } err)
            {
                if (notifyInvalid) statusLabel.Text = err;
                return false;
            }
            statusLabel.Text = result.Message ?? "";
            return true;
        }

        /// <summary>주소를 해석해 이동한다. 에이전트용: 성공하면 (행 번호, 컬럼 이름) 상태 문구, 실패하면 Error.</summary>
        private async Task<(string? Error, string? Message)> ResolveAndJumpAsync(string text)
        {
            var doc = _doc!;
            if (!CellAddress.TryParse(text, out var addr, out string error))
                return (LT("Invalid cell address: ", "셀 주소가 올바르지 않습니다: ") + error, null);

            var headers = ColumnDisplayNames();
            if (!addr.TryResolve(headers, out long? row, out int? col, out string resolveError))
                return (LT("Cannot go there: ", "이동할 수 없습니다: ") + resolveError, null);

            if (col is { } c && (c >= grid.Columns.Count || !grid.Columns[c].Visible))
                return (LT($"Column '{headers[c]}' is hidden (View ▸ Columns shows it).", $"'{headers[c]}' 컬럼이 숨겨져 있습니다(보기 ▸ 컬럼 표시에서 켜세요)."), null);

            int viewRow;
            if (row is { } target)
            {
                if (!doc.IsFiltered)
                    viewRow = target <= doc.DataRowsAvailable ? (int)(target - 1) : -1;
                else
                {
                    int total = doc.DisplayRowCount;
                    viewRow = await Task.Run(() =>
                    {
                        for (int i = 0; i < total; i++)
                            if (doc.GetSourceRowNumber(i) == target) return i;
                        return -1;
                    });
                    if (!ReferenceEquals(doc, _doc)) return (LT("The file changed while searching; try again.", "찾는 동안 파일이 바뀌었습니다. 다시 시도하세요."), null);
                }
                if (viewRow < 0 || viewRow >= doc.DisplayRowCount)
                    return (doc.IsFiltered
                        ? LT($"Row {target:N0} is not in the current view (filtered out or past the last row).", $"{target:N0}행이 현재 보기에 없습니다(필터에 걸러졌거나 마지막 행 뒤입니다).")
                        : LT($"Row {target:N0} does not exist (the table has {doc.DataRowsAvailable:N0} rows).", $"{target:N0}행이 없습니다(표에는 {doc.DataRowsAvailable:N0}행이 있습니다)."), null);
            }
            else viewRow = Math.Clamp(grid.CurrentCell?.RowIndex ?? 0, 0, doc.DisplayRowCount - 1);

            int toCol = col ?? grid.CurrentCell?.ColumnIndex ?? -1;
            if (toCol < 0 || toCol >= grid.Columns.Count || !grid.Columns[toCol].Visible)
                toCol = grid.Columns.Cast<DataGridViewColumn>().Where(x => x.Visible).Select(x => x.Index).DefaultIfEmpty(-1).First();
            if (toCol < 0) return (LT("All columns are hidden.", "표시 중인 컬럼이 없습니다."), null);

            try
            {
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                grid.ClearSelection();
                grid.CurrentCell = grid[toCol, viewRow];
                grid[toCol, viewRow].Selected = true;
            }
            catch (InvalidOperationException ex)
            {
                return (LT("Could not move the cursor: ", "커서를 옮기지 못했습니다: ") + ex.Message, null);
            }
            try
            {
                int shown = Math.Max(1, grid.DisplayedRowCount(false));
                int first = grid.FirstDisplayedScrollingRowIndex;
                if (viewRow < first || viewRow >= first + shown) grid.FirstDisplayedScrollingRowIndex = Math.Max(0, viewRow - 3);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
            {
                // 창이 아직 표시 전이거나 레이아웃 중이면 스크롤할 수 없다 — 커서는 이미 옮겼다.
            }
            OnCurrentCellChanged(grid, EventArgs.Empty);
            long number = doc.GetSourceRowNumber(viewRow);
            return (null, LT($"Moved to row {number:N0}, {grid.Columns[toCol].HeaderText}.", $"{number:N0}행, {grid.Columns[toCol].HeaderText}(으)로 이동했습니다."));
        }
    }
}
