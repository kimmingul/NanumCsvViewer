using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // 그리드 우클릭 메뉴 세 가지: 셀 · 컬럼 헤더 · 행 헤더(행 번호 칸). 열릴 때마다 현재 상태(언어·모드·타입)로 새로 만든다.
    // 편집 항목은 항상 보이고 시트 편집 모드가 아니면 비활성이다(맨 위의 "시트 편집 켜기"로 바로 켤 수 있다).
    public partial class Form1
    {
        private ContextMenuStrip? _gridContext;

        /// <summary>"AI에게 묻기" 항목이 보낼 글을 가로채는 이음매(테스트용). null이면 AI 패널을 열고 채팅으로 보낸다.</summary>
        internal Action<string>? AskAiSink;

        // 그리드가 우클릭 위치(셀 · 컬럼 헤더 · 행 헤더)에 맞는 메뉴를 요청한다. 헤더는 색인이 끝난 뒤에만(요약·정렬이 준비돼야 한다).
        private void OnCellContextMenuStripNeeded(object? sender, DataGridViewCellContextMenuStripNeededEventArgs e)
        {
            if (_doc is null || _busy) return;
            ContextMenuStrip? menu = null;
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0) menu = BuildCellContextMenu(e.RowIndex, e.ColumnIndex);
            else if (e.RowIndex == -1 && e.ColumnIndex >= 0) menu = _doc.IndexingComplete ? BuildColumnHeaderMenu(e.ColumnIndex) : null;
            else if (e.RowIndex >= 0 && e.ColumnIndex == -1) menu = BuildRowHeaderMenu(e.RowIndex);
            if (menu is null) return;
            _gridContext?.Dispose();
            _gridContext = menu;
            e.ContextMenuStrip = menu;
        }

        // ---------------------------------------------------------------- 항목 도우미

        private static ToolStripMenuItem Ctx(string text, Action? action, bool enabled = true, string? shortcutText = null, bool check = false)
        {
            var item = new ToolStripMenuItem(text) { Enabled = enabled && action is not null, Checked = check };
            if (shortcutText is not null) item.ShortcutKeyDisplayString = shortcutText;
            if (action is not null) item.Click += (_, _) => action();
            return item;
        }

        // 명령 표에 있는 이름·단축키를 쓰는 항목(메뉴와 한 이름).
        private static ToolStripMenuItem CtxCmd(string id, Action? action, bool enabled = true)
        {
            var e = CommandShortcuts.Get(id);
            return Ctx((Loc.CurrentLanguage == "ko" ? e.Ko : e.En).Replace("&", "&&"), action, enabled, e.KeyText);
        }

        private static ToolStripMenuItem CtxSub(string text, params ToolStripItem[] items)
        {
            var m = new ToolStripMenuItem(text);
            foreach (var i in items) m.DropDownItems.Add(i);
            return m;
        }

        private string ColumnNameOf(int col)
            => col >= 0 && col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";

        // 시트 편집 모드 상태(UpdateEditState와 같은 기준).
        private (bool sheet, bool structure, bool hasCell) EditContextState()
        {
            bool ready = EditsReady;
            bool hasCell = ready && grid.CurrentCell is { RowIndex: >= 0, ColumnIndex: >= 0 };
            bool sheet = ready && _sheetEditing;
            return (sheet, sheet && _doc!.CanEditStructure, hasCell);
        }

        private ToolStripMenuItem SheetEditSwitchItem()
            => Ctx(_sheetEditing ? LT("Turn Off Sheet Edit", "시트 편집 끄기") : LT("Turn On Sheet Edit", "시트 편집 켜기"),
                   EditsReady ? () => SetSheetEditing(!_sheetEditing) : null, EditsReady, CommandShortcuts.Get("edit.sheet").KeyText);

        // ---------------------------------------------------------------- 셀

        internal ContextMenuStrip BuildCellContextMenu(int row, int col)
        {
            var menu = new ContextMenuStrip();
            bool ready = _doc is { IndexingComplete: true } && !_busy;
            string colName = ColumnNameOf(col);
            string value = "";
            try { if (_doc is not null) { var r = _doc.GetDisplayRow(row); value = col < r.Length ? r[col] : ""; } } catch { /* 행을 읽지 못하면 값 없음 */ }
            var (sheet, structure, hasCell) = EditContextState();

            menu.Items.Add(CtxCmd("edit.copy", () => CopySelectedCells(), _doc is not null));
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(CtxSub(LT("Filter", "필터"),
                CtxCmd("data.filterByCell", () => _ = FilterByCellAsync(row, col, CellFilterOp.Equals), ready),
                Ctx(LT("Exclude This Value", "이 값 제외"), () => _ = FilterByCellAsync(row, col, CellFilterOp.NotEquals), ready),
                Ctx(LT("≥ This Value", "이 값 이상 (≥)"), () => _ = FilterByCellAsync(row, col, CellFilterOp.AtLeast), ready),
                Ctx(LT("≤ This Value", "이 값 이하 (≤)"), () => _ = FilterByCellAsync(row, col, CellFilterOp.AtMost), ready)));
            menu.Items.Add(CtxSub(LT("Sort This Column", "이 컬럼 정렬"),
                Ctx(LT("Sort Ascending", "오름차순 정렬"), () => SortColumn(col, true), ready),
                Ctx(LT("Sort Descending", "내림차순 정렬"), () => SortColumn(col, false), ready)));
            menu.Items.Add(Ctx(LT("Hide Column", "컬럼 숨기기"), () => HideColumn(col), _doc is not null));
            menu.Items.Add(Ctx(LT("Conditional Formatting…", "조건부 서식…"), () => ShowConditionalFormatManager(), _doc is not null));
            menu.Items.Add(CtxSub(LT("Ask AI", "AI에게 묻기"),
                Ctx(LT("Summarize This Column", "이 컬럼 요약"), () => AskAi(SummarizeColumnPrompt(colName)), ready),
                Ctx(LT("Analyze Rows with This Value", "이 값을 가진 행 분석"), () => AskAi(AnalyzeValuePrompt(colName, value)), ready)));

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(SheetEditSwitchItem());
            menu.Items.Add(CtxCmd("edit.cell", () => EditCurrentCell(), hasCell && !_sheetEditing));
            menu.Items.Add(CtxCmd("edit.paste", () => PasteFromClipboard(), sheet && hasCell));
            menu.Items.Add(CtxCmd("edit.clear", () => ClearSelectedCells(), sheet && hasCell));
            menu.Items.Add(CtxSub(LT("Rows", "행"),
                Ctx(LT("Insert Row Above", "위에 행 삽입"), () => InsertRow(above: true), structure),
                Ctx(LT("Insert Row Below", "아래에 행 삽입"), () => InsertRow(above: false), structure),
                Ctx(LT("Delete Selected Rows", "선택한 행 삭제"), () => DeleteSelectedRows(), structure && hasCell)));
            int left = sheet ? AdjacentVisibleColumn(col, -1) : -1, right = sheet ? AdjacentVisibleColumn(col, +1) : -1;
            menu.Items.Add(CtxSub(LT("Columns", "컬럼"),
                Ctx(LT("Insert Column…", "컬럼 삽입…"), () => InsertColumnFromUi(), sheet),
                Ctx(LT("Move Column Left", "컬럼 왼쪽으로 이동"), () => MoveColumnFromUi(col, left), left >= 0),
                Ctx(LT("Move Column Right", "컬럼 오른쪽으로 이동"), () => MoveColumnFromUi(col, right), right >= 0),
                Ctx(LT("Delete Column", "컬럼 삭제"), () => DeleteColumnFromUi(col), sheet && hasCell && _doc!.ColumnCount > 1),
                new ToolStripSeparator(),
                Ctx(LT("Extract to New Column (regex)…", "정규식으로 새 컬럼에 추출…"), () => _ = ExtractColumnAsync(), sheet)));
            menu.Items.Add(CtxCmd("edit.replace", () => _ = RegexReplaceAsync(), sheet));
            return menu;
        }

        // ---------------------------------------------------------------- 컬럼 헤더

        internal ContextMenuStrip? BuildColumnHeaderMenu(int col)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || col < 0 || col >= grid.Columns.Count) return null;
            var menu = new ContextMenuStrip();
            bool hasSummary = col < _columnSummaries.Length;
            var (sheet, _, _) = EditContextState();

            string caption = hasSummary ? $"\"{ColumnNameOf(col)}\" — {_columnSummaries[col].InferredType.DisplayName()}" : $"\"{ColumnNameOf(col)}\"";
            menu.Items.Add(new ToolStripMenuItem(caption) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Ctx(LT("Sort Ascending", "오름차순 정렬"), () => SortColumn(col, true)));
            menu.Items.Add(Ctx(LT("Sort Descending", "내림차순 정렬"), () => SortColumn(col, false)));
            menu.Items.Add(Ctx(LT("Filter…", "필터…"), () => OpenColumnFilter(col), IsFilterableColumn(col)));
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Ctx(LT("Hide Column", "컬럼 숨기기"), () => HideColumn(col)));
            bool frozenHere = _frozenColumnCount > 0;
            menu.Items.Add(Ctx(LT("Freeze Columns up to Here", "여기까지 열 고정"), () => FreezeColumnsUpTo(col)));
            menu.Items.Add(Ctx(LT("Unfreeze Columns", "열 고정 해제"), UnfreezeColumns, frozenHere));
            menu.Items.Add(Ctx(LT("Auto-fit Width", "너비 자동 맞춤"), () => AutoFitColumn(col)));
            menu.Items.Add(new ToolStripSeparator());

            if (hasSummary)
                foreach (var item in BuildTypeItems(col)) menu.Items.Add(item);
            menu.Items.Add(CtxSub(LT("Quick Analysis", "빠른 분석"),
                Ctx(LT("Descriptive Statistics…", "기술통계…"), () => AnalyzeDescriptives(col)),
                Ctx(LT("Frequency Table…", "빈도분석…"), () => AnalyzeFrequency(col)),
                Ctx(LT("Numeric Distribution…", "수치 분포…"), () => AnalyzeDistribution(col))));
            menu.Items.Add(new ToolStripSeparator());

            int left = sheet ? AdjacentVisibleColumn(col, -1) : -1, right = sheet ? AdjacentVisibleColumn(col, +1) : -1;
            menu.Items.Add(Ctx(LT("Rename Column…", "컬럼 이름 변경…"), () => RenameColumn(col), sheet));
            menu.Items.Add(Ctx(LT("Insert Column…", "컬럼 삽입…"), () => InsertColumnFromUi(), sheet));
            menu.Items.Add(Ctx(LT("Delete Column", "컬럼 삭제"), () => DeleteColumnFromUi(col), sheet && _doc.ColumnCount > 1));
            menu.Items.Add(Ctx(LT("Move Column Left", "컬럼 왼쪽으로 이동"), () => MoveColumnFromUi(col, left), left >= 0));
            menu.Items.Add(Ctx(LT("Move Column Right", "컬럼 오른쪽으로 이동"), () => MoveColumnFromUi(col, right), right >= 0));
            if (!sheet) menu.Items.Add(SheetEditSwitchItem());
            return menu;
        }

        // 컬럼 타입 변경 항목(헤더 메뉴 · 데이터 ▸ 컬럼 타입 공용): "타입 변경 ▸ …" + "자동 감지로 되돌리기".
        private List<ToolStripItem> BuildTypeItems(int col)
        {
            var items = new List<ToolStripItem>();
            var current = _columnSummaries[col].InferredType;
            var change = new ToolStripMenuItem(LT("Change Type", "타입 변경"));
            foreach (var target in ManualTypeTargets)
            {
                var policy = ColumnTypeConversion.Classify(current, target);
                var item = new ToolStripMenuItem(target.DisplayName())
                {
                    Checked = target == current,
                    Enabled = target != current && policy != TypeChangePolicy.Blocked,
                };
                if (policy == TypeChangePolicy.Blocked)
                    item.ToolTipText = LT("Blocked: lossy or unsafe reinterpretation", "차단됨: 손실·오해석 가능 전환");
                else if (policy == TypeChangePolicy.RequiresValidation)
                    item.ToolTipText = LT("Applies after sample validation", "표본 검증 후 적용");
                var t = target;
                item.Click += (_, _) => ApplyManualType(col, t);
                change.DropDownItems.Add(item);
            }
            items.Add(change);
            items.Add(Ctx(LT("Reset to Auto-detected", "자동 감지로 되돌리기"), () => ResetManualType(col), _manualTypeOverrides.ContainsKey(col)));
            return items;
        }

        // ---------------------------------------------------------------- 행 헤더

        internal ContextMenuStrip BuildRowHeaderMenu(int row)
        {
            var menu = new ContextMenuStrip();
            var (sheet, structure, _) = EditContextState();
            bool open = _doc is not null && !_busy;

            menu.Items.Add(Ctx(LT("Copy Row", "행 복사"), () => CopyRow(row), open));
            menu.Items.Add(Ctx(LT("Show Row Detail", "행 상세 보기"), () => ShowRowDetail(row), open));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Ctx(LT("Insert Row Above", "위에 행 삽입"), () => { SelectRowForContext(row); InsertRow(above: true); }, structure));
            menu.Items.Add(Ctx(LT("Insert Row Below", "아래에 행 삽입"), () => { SelectRowForContext(row); InsertRow(above: false); }, structure));
            menu.Items.Add(Ctx(LT("Delete Row", "행 삭제"), () => { SelectRowForContext(row); DeleteSelectedRows(); }, structure));
            if (!sheet) { menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(SheetEditSwitchItem()); }
            return menu;
        }

        // 행 헤더를 우클릭하면 그 행의 보이는 셀 전체를 선택해 현재 행으로 삼는다(복사·삽입·삭제가 이 행에 작용).
        private void SelectRowForContext(int row)
        {
            if (_doc is null || row < 0 || row >= grid.RowCount) return;
            int keep = grid.CurrentCell is { ColumnIndex: >= 0 } cur && grid.Columns[cur.ColumnIndex].Visible ? cur.ColumnIndex : FirstVisibleColumn();
            if (keep < 0) return;
            try
            {
                grid.CurrentCell = grid[keep, row];
                grid.ClearSelection();
                foreach (DataGridViewColumn c in grid.Columns)
                    if (c.Visible) grid[c.Index, row].Selected = true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RowHeader] {ex.Message}"); }
        }

        private int FirstVisibleColumn()
        {
            foreach (DataGridViewColumn c in grid.Columns) if (c.Visible) return c.Index;
            return -1;
        }

        private void CopyRow(int row)
        {
            SelectRowForContext(row);
            CopyCurrentRow();
        }

        private void ShowRowDetail(int row)
        {
            int keep = grid.CurrentCell is { ColumnIndex: >= 0 } cur && grid.Columns[cur.ColumnIndex].Visible ? cur.ColumnIndex : FirstVisibleColumn();
            if (keep >= 0 && row >= 0 && row < grid.RowCount) { try { grid.CurrentCell = grid[keep, row]; } catch { /* 레이아웃 중 일시 예외 */ } }
            SetPanelVisible(PanelKind.Detail, true);
            UpdateDetailPanel();
        }

        // ---------------------------------------------------------------- 컬럼 동작: 숨기기 · 고정 · 너비 자동 맞춤

        private void HideColumn(int col)
        {
            if (_doc is null || col < 0 || col >= grid.Columns.Count) return;
            int visible = grid.Columns.Cast<DataGridViewColumn>().Count(c => c.Visible);
            if (visible <= 1)
            {
                statusLabel.Text = LT("At least one column must stay visible.", "컬럼이 하나는 보여야 합니다.");
                return;
            }
            grid.Columns[col].Visible = false;
            _hiddenColumns.Add(col);
            if (_facetsVisible) BuildFacets();
            statusLabel.Text = LT($"Hid column '{ColumnNameOf(col)}' — Data ▸ Columns… shows it again.", $"'{ColumnNameOf(col)}' 컬럼을 숨겼습니다 — 데이터 ▸ 컬럼 표시…에서 다시 켤 수 있습니다.");
        }

        // 고정 열 수(왼쪽부터). 컬럼을 옮기거나 지우거나 탭을 바꿔도 이 수가 기준이 되어 ApplyFrozenColumns가 다시 적용한다.
        private int _frozenColumnCount;

        internal int FrozenColumnCount => _frozenColumnCount;

        private void FreezeColumnsUpTo(int col)
        {
            if (col < 0 || col >= grid.Columns.Count) return;
            int want = col + 1;
            // 고정 열이 그리드 폭을 거의 다 차지하면 나머지 열로 스크롤할 수 없다.
            int width = 0;
            for (int i = 0; i < want; i++) if (grid.Columns[i].Visible) width += grid.Columns[i].Width;
            if (width > grid.ClientSize.Width * 0.7)
            {
                statusLabel.Text = LT("Those columns are too wide to freeze — narrow them or freeze fewer columns.",
                                      "고정할 열이 너무 넓습니다 — 너비를 줄이거나 더 적은 열을 고정하세요.");
                return;
            }
            _frozenColumnCount = want;
            ApplyFrozenColumns();
        }

        private void UnfreezeColumns()
        {
            _frozenColumnCount = 0;
            ApplyFrozenColumns();
        }

        // DataGridView는 열 하나를 고정하면 왼쪽 열 전부를 함께 고정한다(해제는 오른쪽 전부). 그 규칙에 맞춰 고정 열 수를 적용한다.
        private void ApplyFrozenColumns()
        {
            int count = grid.Columns.Count;
            int n = Math.Clamp(_frozenColumnCount, 0, count);
            _frozenColumnCount = n;
            if (count == 0) return;
            if (n < count) grid.Columns[n].Frozen = false;
            if (n > 0) grid.Columns[n - 1].Frozen = true;
        }

        // 컬럼 객체를 옮기기 전에 모두 풀어 둔다(고정 열 앞에 고정 안 된 열을 끼워 넣으면 DataGridView가 거부한다).
        private void SuspendFrozenColumns()
        {
            if (grid.Columns.Count > 0) grid.Columns[0].Frozen = false;
        }

        // 보이는 행 근처 표본(최대 300행)과 헤더 글자를 직접 재서 너비를 정한다. DataGridView.AutoResizeColumn은 가상 모드에서
        // 화면에 그려진 셀만 보므로(창이 아직 안 그려졌거나 행이 없으면 아무것도 못 줄인다) 쓰지 않는다.
        private void AutoFitColumn(int col)
        {
            if (_doc is null || col < 0 || col >= grid.Columns.Count) return;
            try
            {
                var font = grid.DefaultCellStyle.Font ?? grid.Font;
                const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                // 헤더에는 타입 배지와 필터 깔때기가 얹히므로 여유를 더 준다.
                int width = TextRenderer.MeasureText(grid.Columns[col].HeaderText, font, Size.Empty, flags).Width + 64;
                int first = 0;
                try { if (grid.RowCount > 0 && grid.FirstDisplayedScrollingRowIndex >= 0) first = grid.FirstDisplayedScrollingRowIndex; } catch { /* 레이아웃 전 */ }
                int last = Math.Min(grid.RowCount, first + 300);
                for (int r = first; r < last; r++)
                {
                    string[] row = _doc.GetDisplayRow(r);
                    if (col >= row.Length) continue;
                    foreach (string line in PreviewCell(row[col]).Split('\n'))
                        width = Math.Max(width, TextRenderer.MeasureText(line.TrimEnd('\r'), font, Size.Empty, flags).Width + 24);
                }
                grid.Columns[col].Width = Math.Clamp(width, 50, 600);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AutoFit] {ex.Message}"); }
        }

        // ---------------------------------------------------------------- 셀 값 필터 (같음 · 제외 · ≥ · ≤)

        internal enum CellFilterOp { Equals, NotEquals, AtLeast, AtMost }

        internal async Task FilterByCellAsync(int viewRow, int col, CellFilterOp op)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            if (viewRow < 0 || col < 0)
            {
                statusLabel.Text = Loc.T("Status_SelectCellFirst");
                return;
            }
            string[] row;
            try { row = _doc.GetDisplayRow(viewRow); } catch { return; }
            string value = col < row.Length ? row[col] : "";
            string colName = ColumnNameOf(col);
            var type = col < _columnSummaries.Length ? _columnSummaries[col].InferredType : ColumnValueType.String;

            var pred = BuildCellPredicate(col, value, op, type);
            string shown = Trunc(value);
            string desc = op switch
            {
                CellFilterOp.Equals => Loc.F("Filter_EqualsFmt", colName, shown),
                CellFilterOp.NotEquals => $"{colName} ≠ \"{shown}\"",
                CellFilterOp.AtLeast => $"{colName} ≥ \"{shown}\"",
                _ => $"{colName} ≤ \"{shown}\"",
            };
            _valueConditions.Add((desc, pred, null));
            // 증분: 현재 뷰만 새 조건으로 좁힌다(전체 재스캔 안 함). 정렬 순서 유지.
            await RunViewOpAsync(p => _doc.FilterWithinViewAsync(pred, p, _opCts!.Token), Loc.T("Status_CellFilterApplying"));
            UpdateFilterStatus();
        }

        /// <summary>
        /// 셀 값 조건. 같음/제외는 정확 문자열 일치. ≥/≤는 컬럼 타입을 따른다: 날짜·일시·시간 컬럼은 날짜로, 값이 숫자(통화·퍼센트 포함)면 수치로,
        /// 그 외는 문자열 순서로 비교한다. 비교할 수 없는 셀(빈 값·형식이 다른 값)은 ≥/≤에서 일치하지 않는다.
        /// </summary>
        internal static Func<string[], bool> BuildCellPredicate(int col, string value, CellFilterOp op, ColumnValueType type)
        {
            if (op == CellFilterOp.Equals) return r => col < r.Length && string.Equals(r[col], value, StringComparison.Ordinal);
            if (op == CellFilterOp.NotEquals) return r => col >= r.Length || !string.Equals(r[col], value, StringComparison.Ordinal);

            var compare = OrderComparerFor(type, value);
            bool atLeast = op == CellFilterOp.AtLeast;
            return r =>
            {
                if (col >= r.Length) return false;
                int? c = compare(r[col]);
                return c is { } v && (atLeast ? v >= 0 : v <= 0);
            };
        }

        private static Func<string, int?> OrderComparerFor(ColumnValueType type, string pivot)
        {
            if (type is ColumnValueType.Date or ColumnValueType.DateTime or ColumnValueType.Time && CsvDateParser.Parse(pivot) is { } pivotDate)
                return s => CsvDateParser.Parse(s) is { } d ? d.CompareTo(pivotDate) : null;
            if (NumericAffix.TryParseNumber(pivot, out double pivotNumber))
                return s => NumericAffix.TryParseNumber(s, out double v) ? v.CompareTo(pivotNumber) : null;
            return s => s.Length == 0 ? null : Math.Sign(string.CompareOrdinal(s, pivot));
        }

        // ---------------------------------------------------------------- AI에게 묻기

        private static string SummarizeColumnPrompt(string column)
            => LT($"Summarize the column \"{column}\" of the open file: its type, distribution, missing values and anything unusual.",
                  $"열려 있는 파일의 \"{column}\" 컬럼을 요약해 줘: 타입, 분포, 결측값, 눈에 띄는 특이점.");

        private static string AnalyzeValuePrompt(string column, string value)
        {
            string v = value.Length > 200 ? value[..200] + "…" : value;
            return LT($"Analyze the rows of the open file where \"{column}\" is \"{v}\": how many there are and what they have in common compared with the rest.",
                      $"열려 있는 파일에서 \"{column}\"이(가) \"{v}\"인 행을 분석해 줘: 몇 행인지, 나머지와 비교해 무엇이 공통인지.");
        }

        /// <summary>AI 패널을 열고 질문을 채팅으로 보낸다(AskAgent: omp는 첫 메시지로 시작). 테스트는 AskAiSink로 가로챈다.</summary>
        internal void AskAi(string prompt)
        {
            if (AskAiSink is { } sink) { sink(prompt); return; }
            AskAgent(prompt);
        }

        // ---------------------------------------------------------------- 컬럼 정렬

        private void SortColumn(int col, bool ascending)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || col < 0 || col >= grid.Columns.Count) return;
            _sortKeys.Clear();
            _sortKeys.Add(new SortKey(col, ascending));
            _ = SortAsync();
        }
    }
}
