using System.Diagnostics;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    // ---------------------------------------------------------------- AI 에이전트 호스트 (v2)
    //
    // csv.* host tool(Agent/CsvHostTools)이 실행 중인 창을 조작하는 경계(ICsvAgentHost)의 구현이다.
    // 인자 검증·데이터 정책·승인 카드·결과 JSON은 도구 쪽(WinForms 없음)에 있고, 여기에는 창 상태를 건드리는 일만 둔다.
    // 모든 호출은 UI 스레드. 기존 사용자 경로(필터·정렬·분석·편집·저장)와 같은 필드·헬퍼를 쓰되,
    // 사용자 상호작용 대신 예외(AgentToolException)로 실패를 알리고 호출자의 취소 토큰을 이어 준다.
    public partial class Form1 : ICsvAgentHost
    {
        private const int AgentMaxRowsRead = 5000;

        [Conditional("DEBUG")]
        private void AgentAssertUi() => Debug.Assert(!InvokeRequired, "ICsvAgentHost must be called on the UI thread.");

        private void AgentRequireReady()
        {
            AgentAssertUi();
            if (_doc is null) throw new AgentToolException("No file is open in Nanum CSV Viewer. Ask the user to open one.");
            if (_closing) throw new AgentToolException("The window is closing.");
            if (!_doc.IndexingComplete) throw new AgentToolException("The file is still being indexed. Retry in a few seconds.");
            if (_busy) throw new AgentToolException("The viewer is busy with another operation (filter, sort, analysis or save). Retry in a moment.");
        }

        private ColumnValueType[] AgentColumnTypes(int count)
        {
            var types = new ColumnValueType[count];
            for (int c = 0; c < count; c++)
                types[c] = c < _columnSummaries.Length ? _columnSummaries[c].InferredType : ColumnValueType.String;
            return types;
        }

        private AgentEditState AgentEditStateNow()
        {
            var e = _doc?.Edits;
            if (e is null) return AgentEditState.None;
            string? what = e.UndoDescription;
            bool agentCanUndo = e.CanUndo && what is not null && what.StartsWith(AgentEditTag.Prefix, StringComparison.Ordinal);
            return new AgentEditState(e.Count, e.HeaderEditCount, e.DeletedCount, e.AddedCount,
                HasUnsavedEdits, e.CanUndo, what, agentCanUndo, _sheetEditing);
        }

        // ------------------------------------------------------------------ 상태

        AgentDocumentInfo? ICsvAgentHost.GetInfo()
        {
            AgentAssertUi();
            var doc = _doc;
            if (doc is null) return null;

            string[] names = AdvHeaders();
            var types = AgentColumnTypes(names.Length);
            var columns = new List<AgentColumn>(names.Length);
            for (int c = 0; c < names.Length; c++) columns.Add(new AgentColumn(c, names[c], types[c]));

            string NameOf(int c) => c >= 0 && c < names.Length ? names[c] : $"Column{c + 1}";

            var filters = new List<AgentFilterInfo>();
            if (_textCondition is not null) filters.Add(new AgentFilterInfo(AgentFilterKind.TextSearch, _textConditionDesc));
            foreach (var (desc, _, expr) in _valueConditions)
                filters.Add(expr is not null
                    ? new AgentFilterInfo(AgentFilterKind.Expression, expr)
                    : new AgentFilterInfo(AgentFilterKind.CellValue, desc));
            foreach (var (col, text) in _columnFilters.DescribeEntries(doc.Header))
                filters.Add(new AgentFilterInfo(AgentFilterKind.ColumnFilter, text, NameOf(col)));

            var sort = _sortKeys.Select(s => new AgentSortInfo(NameOf(s.Column), s.Ascending)).ToList();

            string? sheet = null;
            IReadOnlyList<string> sheets = Array.Empty<string>();
            if (_workbook is not null)
            {
                sheets = _workbook.SheetNames;
                if (_currentSheetIndex >= 0 && _currentSheetIndex < sheets.Count) sheet = sheets[_currentSheetIndex];
            }
            string sourcePath = _workbook?.SourcePath ?? _currentPath ?? "";
            var protectedPaths = new List<string>();
            if (!string.IsNullOrEmpty(_currentPath)) protectedPaths.Add(_currentPath);
            if (!string.IsNullOrEmpty(_workbook?.SourcePath) && !protectedPaths.Contains(_workbook!.SourcePath)) protectedPaths.Add(_workbook.SourcePath);

            AgentCursor cursor = new(null, null);
            if (grid.CurrentCell is { RowIndex: >= 0, ColumnIndex: >= 0 } cell && cell.RowIndex < doc.DisplayRowCount)
                cursor = new AgentCursor(doc.GetSourceRowNumber(cell.RowIndex), NameOf(cell.ColumnIndex));

            string delimiter = doc.Delimiter switch { '\t' => "tab", ' ' => "space", var d => d.ToString() };
            return new AgentDocumentInfo(
                FileName: Path.GetFileName(sourcePath),
                SheetName: sheet,
                SheetNames: sheets,
                Encoding: doc.EncodingName,
                Delimiter: delimiter,
                FileBytes: doc.FileLength,
                IndexingComplete: doc.IndexingComplete,
                IndexingPercent: doc.IndexingComplete ? 100 : Math.Clamp(progressBar.Value, 0, 99),
                Busy: _busy,
                TotalRows: doc.DataRowsAvailable,
                ViewRows: doc.DisplayRowCount,
                RowCountTruncated: doc.RowCountTruncated,
                Columns: columns,
                Filters: filters,
                FilterMatchAny: _filterMatchAny,
                Sort: sort,
                HiddenColumns: _hiddenColumns.OrderBy(c => c).Select(NameOf).ToList(),
                Edits: AgentEditStateNow(),
                Cursor: cursor,
                Directory: string.IsNullOrEmpty(sourcePath) ? null : Path.GetDirectoryName(sourcePath),
                ProtectedPaths: protectedPaths);
        }

        // ------------------------------------------------------------------ 읽기

        async Task<AgentRowsPage> ICsvAgentHost.GetRowsAsync(long firstViewRow, int count, IReadOnlyList<int> columns, CancellationToken cancellation)
        {
            AgentRequireReady();
            var doc = _doc!;
            count = Math.Clamp(count, 1, AgentMaxRowsRead);
            long view = doc.DisplayRowCount;
            var cols = columns.ToArray();
            var rows = await Task.Run(() =>
            {
                var list = new List<AgentRow>(count);
                for (long i = firstViewRow; i < firstViewRow + count && i < view; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string[] fields = doc.GetDisplayRow((int)i);
                    var values = new string[cols.Length];
                    for (int k = 0; k < cols.Length; k++) values[k] = cols[k] < fields.Length ? fields[cols[k]] : "";
                    list.Add(new AgentRow(doc.GetSourceRowNumber((int)i), values));
                }
                return list;
            }, cancellation);
            if (!ReferenceEquals(doc, _doc)) throw new AgentToolException("The file was changed while reading rows. Retry.");
            return new AgentRowsPage(firstViewRow, rows, view);
        }

        /// <summary>
        /// 호출자 취소(중지)를 잇는 백그라운드 실행기. RunAnalysisOperationAsync와 같은 busy·드레인 수명(_analysisCts/_analysisTask)을 쓰되
        /// 오류를 경고 상자 대신 예외로 돌려준다.
        /// </summary>
        private async Task<T> AgentRunAsync<T>(VirtualCsvDocument doc, string statusText, Func<CancellationToken, T> work, CancellationToken external)
        {
            AgentRequireReady();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
            _analysisCts = cancellation;
            SetBusy(true);
            statusLabel.Text = statusText;
            Task<T>? worker = null;
            try
            {
                worker = Task.Run(() => work(cancellation.Token));
                _analysisTask = worker;
                T result = await worker;
                if (_closing || IsDisposed || !ReferenceEquals(doc, _doc))
                    throw new AgentToolException("The file was closed or replaced while the tool was running.");
                return result;
            }
            finally
            {
                if (ReferenceEquals(_analysisCts, cancellation)) _analysisCts = null;
                if (ReferenceEquals(_analysisTask, worker)) _analysisTask = null;
                if (!_closing && !IsDisposed) { SetBusy(false); UpdateFilterStatus(); }
            }
        }

        async Task<T> ICsvAgentHost.RunOnViewRowsAsync<T>(string description, Func<AgentViewData, CancellationToken, T> work, CancellationToken cancellation)
        {
            AgentRequireReady();
            var doc = _doc!;
            var data = new AgentViewData(doc.SnapshotViewRows(), AdvHeaders(), AgentColumnTypes(doc.ColumnCount));
            return await AgentRunAsync(doc, LT($"AI: {description}…", $"AI: {description}…"), token => work(data, token), cancellation);
        }

        // ------------------------------------------------------------------ 분석 결과 창

        void ICsvAgentHost.ShowAnalysisWindow(AgentAnalysisOutcome outcome)
        {
            AgentAssertUi();
            if (_doc is null || _closing || IsDisposed) return;
            ShowAdvancedResult(BuildAgentReport(outcome));
        }

        private AdvancedReport BuildAgentReport(AgentAnalysisOutcome o)
        {
            using var capture = ReportCapture.Begin();
            string title, text;
            object? model = null;
            var request = o.Request;
            string[] headers = AdvHeaders();
            string? Canon(string? name) => name is { Length: > 0 } ? headers[ColumnNames.Resolve(headers, name)] : null;

            switch (request.Kind)
            {
                case AgentAnalysisKind.Describe:
                    title = LT("Descriptive Statistics", "기술통계");
                    text = AgentAnalysis.DescribeText(o.Describe!, Loc.CurrentLanguage == "ko");
                    break;

                case AgentAnalysisKind.Glm:
                {
                    title = LT("General Linear Model (GLM)", "일반선형모형(GLM)");
                    var dm = o.Design!;
                    string note = AdvSavedNote(dm.RowCount);
                    text = FormatGlmResult(dm, o.Linear!, o.Anova!) + "\n" + note;
                    model = ModelBundle.FromFormula(ModelTypes.LinearModel, ModelTask.Regression, dm, o.Linear!, note);
                    break;
                }

                case AgentAnalysisKind.Ancova:
                    title = LT("ANCOVA", "공분산분석(ANCOVA)");
                    text = o.Ancova is { } single
                        ? FormatAncovaResult(o.Design!, single)
                        : FormatMultiAncovaResult(o.Design!, o.MultiAncova!);
                    break;

                case AgentAnalysisKind.Logistic:
                {
                    title = LT("Logistic Regression", "로지스틱 회귀");
                    var design = o.Design!;
                    var fit = o.Glm!;
                    string note = AdvSavedNote(design.RowCount);
                    text = RenderGlm(design, fit, logistic: true) + "\n" + note;
                    model = ModelBundle.FromFormula(ModelTypes.Logistic, ModelTask.Classification, design, fit, note,
                        new Dictionary<string, string> { ["link"] = "Logit", ["converged"] = fit.Converged.ToString() });
                    break;
                }

                default: // Glzm
                {
                    title = LT("Generalized Linear Model", "일반화선형모형");
                    var design = o.Design!;
                    var fit = o.Glm!;
                    string note = AdvSavedNote(design.RowCount);
                    bool logistic = fit.Family == GlmFamily.Binomial && fit.Link == GlmLink.Logit && design.GlmExtras is null;
                    string? trials = Canon(request.Trials);
                    string? offset = Canon(request.Offset), exposure = Canon(request.Exposure);
                    model = ModelBundle.FromFormula(ModelTypes.Glzm,
                            fit.Family == GlmFamily.Binomial && trials is null ? ModelTask.Classification : ModelTask.Regression,
                            design, fit, note,
                            new Dictionary<string, string> { ["family"] = fit.Family.ToString(), ["link"] = fit.Link.ToString(), ["converged"] = fit.Converged.ToString() })
                        with
                        {
                            OffsetColumn = offset,
                            ExposureColumn = exposure,
                            TrialsColumn = trials,
                            VarianceWeightColumn = Canon(request.VarianceWeights),
                            FrequencyWeightColumn = Canon(request.FrequencyWeights),
                        };
                    text = RenderGlm(design, fit, logistic) + "\n" + note;
                    break;
                }
            }
            return new AdvancedReport(title, text, capture.Tables, model, DateTime.Now);
        }

        // ------------------------------------------------------------------ 필터 · 정렬 · 이동

        /// <summary>
        /// RunViewOpAsync(필터·정렬 공용 실행기)에 호출자 취소를 잇는다. RunViewOpAsync는 취소를 삼키므로
        /// 취소로 끝났으면 여기서 OperationCanceledException을 다시 던져 호출자가 상태를 되돌릴 수 있게 한다.
        /// </summary>
        private async Task AgentViewOpAsync(Func<IProgress<int>, CancellationToken, Task> op, string busyText, CancellationToken external)
        {
            bool canceled = false;
            await RunViewOpAsync(async progress =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_opCts!.Token, external);
                try { await op(progress, linked.Token); }
                catch (OperationCanceledException) { canceled = true; throw; }
            }, busyText);
            if (canceled || external.IsCancellationRequested) throw new OperationCanceledException(external);
        }

        async Task<AgentViewChange> ICsvAgentHost.SetFilterAsync(string expression, bool replace, CancellationToken cancellation)
        {
            AgentRequireReady();
            var doc = _doc!;
            CompiledAdvancedFilter compiled;
            try { compiled = AdvancedFilterExpression.Compile(expression, doc.Header); }
            catch (AdvancedFilterExpressionException ex) { throw new AgentToolException("Invalid filter expression: " + ex.Message); }
            var entry = ($"⨍ {Trunc(expression)}", compiled.Predicate, (string?)expression);

            if (replace)
            {
                // 되돌릴 수 있게 현재 조건을 백업한다(취소 시 복원).
                var oldText = _textCondition;
                string oldTextDesc = _textConditionDesc;
                var oldValues = _valueConditions.ToList();
                var oldColumns = new ColumnFilterState();
                oldColumns.CopyFrom(_columnFilters);
                bool oldAny = _filterMatchAny;
                var oldSort = _sortKeys.ToList();

                _textCondition = null;
                _textConditionDesc = "";
                _valueConditions.Clear();
                _columnFilters.Clear();
                _filterMatchAny = false;
                _valueConditions.Add(entry);
                _sortKeys.Clear();
                ClearSortGlyphs();
                var combined = BuildCombinedPredicate();
                try
                {
                    await AgentViewOpAsync((p, t) => doc.ApplyFilterAsync(combined, p, t), LT("Applying expression…", "표현식 적용 중…"), cancellation);
                }
                catch (OperationCanceledException)
                {
                    _textCondition = oldText;
                    _textConditionDesc = oldTextDesc;
                    _valueConditions.Clear();
                    _valueConditions.AddRange(oldValues);
                    _columnFilters.CopyFrom(oldColumns);
                    _filterMatchAny = oldAny;
                    _sortKeys.Clear();
                    _sortKeys.AddRange(oldSort);
                    UpdateSortGlyphs();
                    UpdateFilterStatus();
                    throw;
                }
            }
            else
            {
                if (_filterMatchAny && HasAnyFilter)
                    throw new AgentToolException("The existing filters are combined with OR, so narrowing them with AND would change their meaning. Use mode 'replace' or csv.clear_filter first.");
                _valueConditions.Add(entry);
                try
                {
                    await AgentViewOpAsync((p, t) => doc.FilterWithinViewAsync(compiled.Predicate, p, t), LT("Applying expression…", "표현식 적용 중…"), cancellation);
                }
                catch (OperationCanceledException)
                {
                    _valueConditions.Remove(entry);
                    UpdateFilterStatus();
                    throw;
                }
            }
            UpdateFilterStatus();
            return new AgentViewChange(doc.DisplayRowCount, doc.DataRowsAvailable);
        }

        Task<AgentViewChange> ICsvAgentHost.ClearFilterAsync(CancellationToken cancellation)
        {
            AgentRequireReady();
            OnClearFilterClick(this, EventArgs.Empty);
            return Task.FromResult(new AgentViewChange(_doc!.DisplayRowCount, _doc.DataRowsAvailable));
        }

        async Task<AgentViewChange> ICsvAgentHost.SortAsync(IReadOnlyList<SortKey> keys, CancellationToken cancellation)
        {
            AgentRequireReady();
            var doc = _doc!;
            if (keys.Count == 0)
            {
                OnClearSortClick(this, EventArgs.Empty);
                return new AgentViewChange(doc.DisplayRowCount, doc.DataRowsAvailable);
            }
            var previous = _sortKeys.ToList();
            _sortKeys.Clear();
            _sortKeys.AddRange(keys);
            var snapshot = keys.ToArray();
            try
            {
                await AgentViewOpAsync((p, t) => doc.SortAsync(snapshot, p, t), LT("Sorting…", "정렬 중…"), cancellation);
            }
            catch (OperationCanceledException)
            {
                _sortKeys.Clear();
                _sortKeys.AddRange(previous);
                UpdateSortGlyphs();
                throw;
            }
            UpdateSortGlyphs();
            statusLabel.Text = Loc.F("Status_SortFmt", DescribeSort(), doc.DisplayRowCount.ToString("N0"));
            return new AgentViewChange(doc.DisplayRowCount, doc.DataRowsAvailable);
        }

        async Task<AgentGotoResult> ICsvAgentHost.GotoAsync(long? sourceRow, int? column, CancellationToken cancellation)
        {
            AgentRequireReady();
            var doc = _doc!;
            if (doc.DisplayRowCount == 0) throw new AgentToolException("The current view has no rows.");

            int col = column ?? Math.Max(0, grid.CurrentCell?.ColumnIndex ?? 0);
            if (col < 0 || col >= grid.Columns.Count) throw new AgentToolException("Column out of range.");
            if (!grid.Columns[col].Visible)
                throw new AgentToolException($"Column '{grid.Columns[col].HeaderText}' is hidden in the grid (the user can show it in View ▸ Columns).");

            int viewRow;
            if (sourceRow is { } target)
            {
                if (!doc.IsFiltered)
                    viewRow = target <= doc.DataRowsAvailable ? (int)(target - 1) : -1;
                else
                {
                    int total = doc.DisplayRowCount;
                    viewRow = await Task.Run(() =>
                    {
                        for (int i = 0; i < total; i++)
                        {
                            if ((i & 0xFFFF) == 0) cancellation.ThrowIfCancellationRequested();
                            if (doc.GetSourceRowNumber(i) == target) return i;
                        }
                        return -1;
                    }, cancellation);
                }
                if (viewRow < 0) throw new AgentToolException($"Row {target:N0} is not in the current view (filtered out, or beyond the last row).");
            }
            else viewRow = Math.Clamp(grid.CurrentCell?.RowIndex ?? 0, 0, doc.DisplayRowCount - 1);

            if (!ReferenceEquals(doc, _doc)) throw new AgentToolException("The file was changed. Retry.");
            if (grid.IsCurrentCellInEditMode) grid.CancelEdit();
            grid.CurrentCell = grid.Rows[viewRow].Cells[col];
            try { grid.FirstDisplayedScrollingRowIndex = Math.Max(0, viewRow - 3); } catch (InvalidOperationException) { }
            return new AgentGotoResult(doc.GetSourceRowNumber(viewRow), viewRow + 1L, grid.Columns[col].HeaderText);
        }

        // ------------------------------------------------------------------ 품질

        async Task<QualityReport> ICsvAgentHost.RunQualityScanAsync(CancellationToken cancellation)
        {
            AgentRequireReady();
            var doc = _doc!;

            _qualityCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            _qualityCts = cts;
            var src = BuildQualityScanSource(doc, withTypes: true);
            var options = new QualityScanOptions();

            SetBusy(true);
            statusLabel.Text = LT("AI: quality scan…", "AI: 품질 스캔 중…");
            var progress = new Progress<int>(p =>
            {
                if (!cts.IsCancellationRequested) statusLabel.Text = LT($"AI: quality scan… {p}%", $"AI: 품질 스캔 중… {p}%");
            });

            QualityReport report;
            var task = Task.Run(() => QualityProfiler.Scan(src, options, progress, cts.Token), cts.Token);
            _qualityTask = task;
            try { report = await task; }
            finally
            {
                if (ReferenceEquals(_qualityTask, task)) _qualityTask = null;
                if (ReferenceEquals(_qualityCts, cts)) _qualityCts = null;
                if (!_closing && !IsDisposed) SetBusy(false);
                cts.Dispose();
            }
            if (cts.IsCancellationRequested || !ReferenceEquals(_doc, doc))
                throw new AgentToolException("The file was closed or replaced during the scan.");

            _qualityReport = report with
            {
                ScanTimestamp = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
                AppVersion = AppInfo.Version,
            };
            _qualityFindings.RemoveAll(f => !QualitySessionChecks.IsUserRun(f.Kind));
            _qualityFindings.InsertRange(0, report.Findings);
            ShowQualityFindings();
            statusLabel.Text = QualitySummaryText();
            return _qualityReport;
        }

        // ------------------------------------------------------------------ 편집 · 저장

        IReadOnlyList<AgentCellState> ICsvAgentHost.GetCellStates(IReadOnlyList<(long SourceRow, int Column)> cells)
        {
            AgentAssertUi();
            var doc = _doc ?? throw new AgentToolException("No file is open.");
            var result = new List<AgentCellState>(cells.Count);
            foreach (var (row, col) in cells)
            {
                int position = row >= 1 && row <= doc.DataRowsAvailable ? (int)(row - 1) : -1;
                if (position < 0 || doc.GetRowIdAtPosition(position) < 0) { result.Add(new AgentCellState(false, "")); continue; }
                string[] fields = doc.GetDataRow(position); // 편집 덮개가 적용된 현재 값
                result.Add(new AgentCellState(true, col >= 0 && col < fields.Length ? fields[col] : ""));
            }
            return result;
        }

        AgentEditResult ICsvAgentHost.ApplyEdits(IReadOnlyList<AgentCellEdit> edits, string description)
        {
            AgentRequireReady();
            var doc = _doc!;
            if (grid.IsCurrentCellInEditMode)
                throw new AgentToolException("The user is typing in a cell right now. Retry after they finish.");

            // 먼저 전부 검증 — 한 단계 안에서 일부만 적용되는 일이 없게.
            var targets = new List<(int RowId, int Col, string Value)>(edits.Count);
            foreach (var edit in edits)
            {
                int position = edit.SourceRow >= 1 && edit.SourceRow <= doc.DataRowsAvailable ? (int)(edit.SourceRow - 1) : -1;
                int rowId = position < 0 ? -1 : doc.GetRowIdAtPosition(position);
                if (rowId < 0) throw new AgentToolException($"Row {edit.SourceRow:N0} does not exist.");
                if (edit.Column < 0 || edit.Column >= doc.ColumnCount) throw new AgentToolException("Column out of range.");
                targets.Add((rowId, edit.Column, edit.Value));
            }

            int changed = 0, unchanged = 0;
            var overlay = doc.Edits;
            using (overlay.BeginStep(description))
            {
                foreach (var (rowId, col, text) in targets)
                {
                    string[] original = doc.GetOriginalRow(rowId);
                    string orig = col < original.Length ? original[col] : "";
                    string value = MatchNewlineStyle(text, orig);
                    string before = overlay.TryGet(rowId, col, out string? cur) ? cur : orig;
                    if (string.Equals(before, value, StringComparison.Ordinal)) unchanged++; else changed++;
                    overlay.Set(rowId, col, value, orig);
                }
            }
            OnCurrentCellChanged(grid, EventArgs.Empty);
            UpdateFeatureState();
            statusLabel.Text = LT($"AI edited {changed:N0} cell(s). Ctrl+Z undoes them in one step; save with Edit ▸ Save Edits As….",
                                  $"AI가 셀 {changed:N0}개를 편집했습니다. Ctrl+Z로 한 번에 되돌립니다. 편집 ▸ 편집 내용 저장…으로 저장하세요.");
            return new AgentEditResult(changed, unchanged, targets.Count, AgentEditStateNow());
        }

        AgentUndoResult ICsvAgentHost.UndoAgentEdit()
        {
            AgentRequireReady();
            var edits = _doc!.Edits;
            if (!edits.CanUndo) throw new AgentToolException("There is nothing to undo.");
            string? what = edits.UndoDescription;
            if (what is null || !what.StartsWith(AgentEditTag.Prefix, StringComparison.Ordinal))
                throw new AgentToolException(
                    $"The latest edit step was not made by the agent ('{what ?? "unnamed edit"}'), so it is not undone here. Ask the user to undo it (Ctrl+Z).");
            if (grid.IsCurrentCellInEditMode)
                throw new AgentToolException("The user is typing in a cell right now. Retry after they finish.");
            edits.Undo();
            AfterHistoryStep(LT("Undone", "되돌림"), what);
            return new AgentUndoResult(what[AgentEditTag.Prefix.Length..], AgentEditStateNow());
        }

        async Task<AgentSaveResult> ICsvAgentHost.SaveEditsAsAsync(string fullPath, CancellationToken cancellation)
        {
            AgentRequireReady();
            var doc = _doc!;
            if (doc.Edits.IsEmpty) throw new AgentToolException("There are no edits to save.");
            if (grid.IsCurrentCellInEditMode) throw new AgentToolException("The user is typing in a cell right now. Retry after they finish.");

            string sourcePath = _workbook?.SourcePath ?? _currentPath ?? "data";
            if (AgentSavePolicy.SameFile(fullPath, sourcePath) || AgentSavePolicy.SameFile(fullPath, _currentPath ?? ""))
                throw new AgentToolException("Refused: that is the source file. Edits are never written over the original.");

            bool asXlsx = string.Equals(Path.GetExtension(fullPath), ".xlsx", StringComparison.OrdinalIgnoreCase);
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            string sheetName = _workbook is not null && _currentSheetIndex >= 0 && _currentSheetIndex < _workbook.SheetNames.Count
                ? _workbook.SheetNames[_currentSheetIndex] : baseName;
            string summary = EditSummary();

            await AgentRunAsync(doc, LT("AI: saving edits…", "AI: 편집 내용 저장 중…"), token =>
            {
                if (asXlsx) doc.SaveAsXlsx(fullPath, sheetName, null, token);
                else doc.SaveWithEdits(fullPath, null, token);
                return true;
            }, cancellation);

            doc.Edits.MarkSaved();
            DeleteJournal();
            UpdateFeatureState();
            statusLabel.Text = LT($"Saved edits ({summary}) to {Path.GetFileName(fullPath)}.", $"편집({summary})을 {Path.GetFileName(fullPath)}에 저장했습니다.");
            return new AgentSaveResult(fullPath, summary);
        }
    }
}
