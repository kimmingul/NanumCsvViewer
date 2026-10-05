using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // 조건부 서식: 규칙(식 / 색상 눈금)을 파일별로 저장 뷰에 보관하고 CellFormatting에서 보이는 행만 계산해 입힌다.
    //
    // 우선순위(문서화된 규칙):
    //  1) 규칙 목록의 앞 규칙이 우선 — 속성(배경·글자색·굵게)마다 "처음 맞은 규칙"이 정한다.
    //  2) 시스템 강조가 규칙의 "배경"보다 우선한다: 편집한 셀의 앰버 > 추가 컬럼 값의 연한 파랑 > 삽입 행의 연한 초록 > 규칙 배경.
    //     규칙의 글자색·굵게는 항상 적용된다(배경이 시스템 색일 때 자동 대비색만 건너뛴다).
    //  3) 선택한 셀은 항상 선택 색으로 보인다.
    // 캐시: 행 id → 식 평가 결과(ConditionalFormatStyler). 편집·컬럼 변경·규칙 변경이면 비우고, 색상 눈금의 최소/최대는
    // 규칙을 다시 만들 때와 보기(필터·편집)가 바뀔 때마다 백그라운드에서 한 번 계산한다(그동안 눈금 규칙은 색을 입히지 않는다).
    public partial class Form1
    {
        private List<ConditionalFormatRule> _cfRules = new();
        private ConditionalFormatStyler _cfStyler = new(ConditionalFormatSet.Empty);
        private VirtualCsvDocument? _cfDoc;
        private long _cfSeenEditsVersion = -1, _cfSeenHeaderVersion = -1;
        private (int Count, int First, int Last) _cfViewKey = (-1, -1, -1);
        private CancellationTokenSource? _cfScaleCts;
        private Font? _cfBoldFont, _cfBoldBase;
        private ToolStripMenuItem? _cfMenu, _cfUndoMenu, _cfRedoMenu;
        private ConditionalFormatHistory _cfHistory = new();
        private System.Windows.Forms.Timer? _cfReportTimer;
        private int _cfReportedTimeouts;
        private int _cfFailureShown;

        // 항목만 만든다(View 메뉴 조립은 Ui/Form1.MainMenu.cs, 툴바 단추는 Ui/Form1.Toolbar.cs, 우클릭 항목은 Ui/Form1.ContextMenus.cs).
        private void BuildFormatFeatures()
        {
            _cfMenu = MakeItem("Conditional Formatting…", "조건부 서식…", (_, _) => ShowConditionalFormatManager());
            _cfUndoMenu = MakeItem("Undo Conditional Format", "조건부 서식 되돌리기", (_, _) => OnFormatHistoryStep(undo: true));
            _cfRedoMenu = MakeItem("Redo Conditional Format", "조건부 서식 다시 실행", (_, _) => OnFormatHistoryStep(undo: false));
            _cfReportTimer = new System.Windows.Forms.Timer { Interval = 700 };
            _cfReportTimer.Tick += (_, _) => ReportConditionalFormatTimeouts();
        }

        private void UpdateFormatState()
        {
            bool open = _doc is not null && ReferenceEquals(_cfDoc, _doc); // 아직 이 문서의 규칙을 읽기 전이면 이력도 비어 있다
            bool undo = open && _cfHistory.CanUndo, redo = open && _cfHistory.CanRedo;
            if (_cfMenu is not null) _cfMenu.Enabled = _doc is not null;
            if (_cfUndoMenu is not null) _cfUndoMenu.Enabled = undo;
            if (_cfRedoMenu is not null) _cfRedoMenu.Enabled = redo;
            if (_cfButton is not null) _cfButton.Enabled = _doc is not null;
        }

        // 다크/라이트 전환은 컴파일된 규칙이 두 테마의 색을 모두 들고 있으므로 플래그만 맞추면 된다(다시 그릴 때 호출).
        private void SyncFormatTheme()
        {
            var set = _cfStyler.Set;
            bool dark = _theme == AppTheme.Dark;
            if (!set.IsEmpty && set.IsDark != dark) set.IsDark = dark;
        }

        // ---------------------------------------------------------------- 규칙 보관 · 컴파일

        // 문서가 바뀌었으면 그 파일의 저장된 규칙을 읽는다(그리기 경로에서 지연 호출 — 별도 훅 없이 문서 교체를 따라간다).
        private void EnsureFormatFresh(VirtualCsvDocument doc)
        {
            bool recompile = false;
            if (!ReferenceEquals(_cfDoc, doc))
            {
                _cfScaleCts?.Cancel();
                _cfHistory.Clear(); // 이력은 파일마다 따로: 다른 파일의 규칙으로 되돌리지 않는다
                _cfDoc = doc;
                _cfRules.Clear();
                if (_currentPath is { } path)
                    _cfRules.AddRange(SavedViewStore.LoadConditionalFormats(path).Take(ConditionalFormatRule.MaxRules));
                _cfSeenHeaderVersion = doc.Edits.HeaderVersion;
                _cfSeenEditsVersion = doc.Edits.Version;
                _cfViewKey = (-1, -1, -1);
                _cfFailureShown = 0;
                recompile = true;
            }
            else if (_cfSeenHeaderVersion != doc.Edits.HeaderVersion)
            {
                _cfSeenHeaderVersion = doc.Edits.HeaderVersion; // 이름 변경·컬럼 추가/삭제: 이름 → 번호를 다시 푼다
                recompile = true;
            }
            if (recompile) { CompileFormatRules(doc); UpdateFormatState(); }
            SyncFormatTheme();

            bool dataChanged = _cfSeenEditsVersion != doc.Edits.Version;
            if (dataChanged)
            {
                _cfSeenEditsVersion = doc.Edits.Version;
                _cfStyler.Invalidate();
            }
            if (!_cfStyler.Set.HasColorScale) return;

            var key = (doc.DisplayRowCount, doc.DisplayRowCount > 0 ? doc.GetRowId(0) : -1,
                       doc.DisplayRowCount > 0 ? doc.GetRowId(doc.DisplayRowCount - 1) : -1);
            if ((recompile || dataChanged || key != _cfViewKey) && doc.IndexingComplete && !_busy)
            {
                _cfViewKey = key;
                StartScaleRecompute(doc);
            }
        }

        private void CompileFormatRules(VirtualCsvDocument doc)
        {
            ConditionalFormatSet set;
            try { set = ConditionalFormatSet.Compile(_cfRules, doc.Header); }
            catch (Exception ex) when (ex is AdvancedFilterExpressionException or RegexPatternException or ArgumentException)
            {
                set = ConditionalFormatSet.Empty;
                if (_cfFailureShown++ == 0)
                    statusLabel.Text = LT("Conditional formatting rules could not be applied: ", "조건부 서식 규칙을 적용하지 못했습니다: ") + ex.Message;
            }
            _cfStyler = new ConditionalFormatStyler(set);
            SyncFormatTheme();
            _cfReportedTimeouts = 0;
            _cfViewKey = (-1, -1, -1);
            _cfScaleCts?.Cancel();
        }

        // 색상 눈금의 최소/최대: 현재 뷰(필터 적용)의 숫자 값에서 백그라운드로 한 번 계산. 계산 중에는 눈금 규칙이 색을 입히지 않는다.
        private void StartScaleRecompute(VirtualCsvDocument doc)
        {
            _cfScaleCts?.Cancel();
            var set = _cfStyler.Set;
            var requests = set.ScaleRequests;
            if (requests.Count == 0) return;
            var cts = _cfScaleCts = new CancellationTokenSource();
            var rows = doc.SnapshotViewRows();
            var token = cts.Token;
            _ = Task.Run(() =>
            {
                var result = new List<(string Id, ScaleRange Range)>();
                foreach (var (id, column) in requests) result.Add((id, ConditionalFormatSet.ComputeRange(rows, column, token)));
                return result;
            }, token).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion || token.IsCancellationRequested || IsDisposed) return;
                if (!ReferenceEquals(set, _cfStyler.Set)) return;
                foreach (var (id, range) in t.Result) set.SetScaleRange(id, range);
                grid.Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// 적용 중인 규칙 목록과 저장. 규칙이 바뀔 때마다 불린다. historyLabel이 있으면 변경 전/후를 서식 이력에 기록한다
        /// (이력에서 되돌리기·다시 실행으로 부를 때는 null — 이력을 건드리지 않는다).
        /// </summary>
        private void SetFormatRules(IEnumerable<ConditionalFormatRule> rules, bool persist = true, string? historyLabel = null)
        {
            var before = historyLabel is null ? null : _cfRules.ToList();
            var copy = rules.Take(ConditionalFormatRule.MaxRules).ToList(); // rules가 _cfRules를 가리킬 수 있으므로 비우기 전에 복사
            _cfRules.Clear();
            _cfRules.AddRange(copy);
            if (_doc is not null)
            {
                _cfDoc = _doc;
                CompileFormatRules(_doc);
                _cfSeenHeaderVersion = _doc.Edits.HeaderVersion;
                _cfSeenEditsVersion = _doc.Edits.Version;
                if (before is not null) _cfHistory.Record(historyLabel!, before, copy);
            }
            if (persist && _currentPath is { } path) SavedViewStore.SaveConditionalFormats(path, _cfRules);
            UpdateFormatState();
            grid.Invalidate();
        }

        private ConditionalFormatUndoResult? StepFormatHistory(bool undo)
        {
            RequireFormatDoc();
            var entry = undo ? _cfHistory.Undo() : _cfHistory.Redo();
            if (entry is null) return null;
            SetFormatRules(undo ? entry.Before : entry.After);
            return new ConditionalFormatUndoResult(entry.Description, _cfRules.Count);
        }

        private void OnFormatHistoryStep(bool undo)
        {
            if (_doc is null) return;
            var result = StepFormatHistory(undo);
            if (result is null) return;
            statusLabel.Text = undo
                ? LT($"Undid the last conditional-format change; {result.RuleCount:N0} rule(s).", $"조건부 서식 변경을 되돌렸습니다. 규칙 {result.RuleCount:N0}개.")
                : LT($"Redid the conditional-format change; {result.RuleCount:N0} rule(s).", $"조건부 서식 변경을 다시 실행했습니다. 규칙 {result.RuleCount:N0}개.");
        }

        // ---------------------------------------------------------------- 그리기

        /// <summary>보이는 셀 하나의 서식. 행 단위로 캐시하므로 스크롤해 보이게 된 행만 계산한다.</summary>
        private CellFormatResult ConditionalFormatFor(VirtualCsvDocument doc, int viewRow, int column)
        {
            EnsureFormatFresh(doc);
            var styler = _cfStyler;
            if (styler.Set.IsEmpty) return default;
            int rowId = doc.GetRowId(viewRow);
            if (rowId < 0) return default;
            var result = styler.Style(rowId, () => doc.GetDisplayRow(viewRow), column);
            if (styler.TimedOutRowCount > _cfReportedTimeouts && _cfReportTimer is { Enabled: false }) _cfReportTimer.Start();
            return result;
        }

        private void ReportConditionalFormatTimeouts()
        {
            _cfReportTimer?.Stop();
            int n = _cfStyler.TimedOutRowCount;
            if (n <= _cfReportedTimeouts) return;
            _cfReportedTimeouts = n;
            statusLabel.Text = LT(
                $"Conditional formatting: {n:N0} row(s) exceeded the {RegexSafety.MatchTimeout.TotalMilliseconds:N0} ms regex limit and were treated as NOT matching.",
                $"조건부 서식: {n:N0}개 행이 정규식 시간 제한({RegexSafety.MatchTimeout.TotalMilliseconds:N0}ms)을 넘겨 일치하지 않는 것으로 처리했습니다.");
        }

        private void ApplyFormatToCell(DataGridViewCellFormattingEventArgs e, CellFormatResult cf, bool systemBackWins)
        {
            var style = e.CellStyle!;
            if (!systemBackWins && cf.Back is { } back) style.BackColor = back;
            if (cf.Fore is { } fore && !(systemBackWins && cf.ForeIsAuto)) style.ForeColor = fore;
            if (cf.Bold)
            {
                var baseFont = style.Font ?? grid.DefaultCellStyle.Font ?? grid.Font;
                if (!ReferenceEquals(_cfBoldBase, baseFont) || _cfBoldFont is null)
                {
                    _cfBoldFont?.Dispose();
                    _cfBoldBase = baseFont;
                    _cfBoldFont = new Font(baseFont, FontStyle.Bold);
                }
                style.Font = _cfBoldFont;
            }
        }

        // ---------------------------------------------------------------- 관리 대화상자

        private void ShowConditionalFormatManager()
        {
            var doc = _doc;
            if (doc is null) return;
            EnsureFormatFresh(doc);
            string[] headers = ColumnDisplayNames();
            var sample = doc.SnapshotViewRows();
            using var dlg = new ConditionalFormatDialog(_cfRules.ToList(), headers, doc.Header, sample, _palette);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!ReferenceEquals(doc, _doc)) return;
            SetFormatRules(dlg.Rules, historyLabel: "edit rules in dialog");
            var set = _cfStyler.Set;
            statusLabel.Text = LT($"Conditional formatting: {dlg.Rules.Count:N0} rule(s), {set.ActiveCount:N0} active.",
                                  $"조건부 서식: 규칙 {dlg.Rules.Count:N0}개, 적용 중 {set.ActiveCount:N0}개.");
            if (set.Problems.Count > 0)
                statusLabel.Text += LT($" {set.Problems.Count} rule(s) cannot be applied (missing column or invalid expression).",
                                       $" {set.Problems.Count}개 규칙은 적용할 수 없습니다(없는 컬럼 또는 잘못된 식).");
        }

        // ---------------------------------------------------------------- 에이전트용 진입점 (UI 스레드)
        // 실패는 ArgumentException(인자 문제) / InvalidOperationException(상태 문제). 규칙은 셀 되돌리기(Ctrl+Z) 대상이 아니라 별도 서식 이력(최대 20개)으로
        // 되돌린다(AgentUndoConditionalFormat). 규칙은 파일별 저장 뷰에 자동 저장된다.

        private VirtualCsvDocument RequireFormatDoc()
        {
            var doc = _doc ?? throw new InvalidOperationException("No file is open.");
            EnsureFormatFresh(doc);
            return doc;
        }

        private string CanonicalColumnName(VirtualCsvDocument doc, string name)
        {
            int idx = ConditionalFormatRules.ResolveColumn(doc.Header, name);
            return idx < 0 ? name.Trim() : (string.IsNullOrEmpty(doc.Header[idx]) ? $"Column{idx + 1}" : doc.Header[idx]);
        }

        /// <summary>규칙을 목록 끝(가장 낮은 우선순위)에 추가한다. draft.Id는 무시되고 "cfN"이 붙는다. 검증에 실패하면 ArgumentException.</summary>
        internal ConditionalFormatRule AgentAddConditionalFormat(ConditionalFormatRule draft)
        {
            var doc = RequireFormatDoc();
            if (_cfRules.Count >= ConditionalFormatRule.MaxRules)
                throw new InvalidOperationException($"At most {ConditionalFormatRule.MaxRules} conditional-format rules are allowed; remove one first.");
            string? problem = ConditionalFormatRules.Validate(draft, doc.Header);
            if (problem is not null) throw new ArgumentException(problem);
            string? column = draft.Kind == ConditionalFormatKind.Expression && draft.Target == ConditionalFormatTarget.Row
                ? null
                : CanonicalColumnName(doc, draft.Column!);
            string id = ConditionalFormatRules.NextId(_cfRules);
            string name = string.IsNullOrWhiteSpace(draft.Name) ? id : draft.Name.Trim();
            var rule = draft with { Id = id, Name = name, Column = column };
            SetFormatRules(_cfRules.Append(rule), historyLabel: $"add {id}");
            statusLabel.Text = LT($"Added conditional format {id}.", $"조건부 서식 {id}을(를) 추가했습니다.");
            return rule;
        }

        internal IReadOnlyList<ConditionalFormatRule> AgentListConditionalFormats()
        {
            RequireFormatDoc();
            return _cfRules.ToList();
        }

        /// <summary>적용되지 않는 규칙(id, 이유). 규칙이 가리키던 컬럼이 삭제·이름 변경되면 여기에 나타난다.</summary>
        internal IReadOnlyList<(string Id, string Problem)> AgentConditionalFormatProblems()
        {
            RequireFormatDoc();
            return _cfStyler.Set.Problems;
        }

        /// <summary>정규식 시간 초과가 난 서로 다른 행 수(현재 규칙 컴파일 이후).</summary>
        internal int AgentConditionalFormatTimedOutRows() => _cfStyler.TimedOutRowCount;

        internal bool AgentRemoveConditionalFormat(string id)
        {
            RequireFormatDoc();
            int idx = _cfRules.FindIndex(r => string.Equals(r.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            string removedId = _cfRules[idx].Id;
            var rest = _cfRules.ToList();
            rest.RemoveAt(idx);
            SetFormatRules(rest, historyLabel: $"remove {removedId}");
            return true;
        }

        internal int AgentClearConditionalFormats()
        {
            RequireFormatDoc();
            int n = _cfRules.Count;
            if (n > 0) SetFormatRules(Array.Empty<ConditionalFormatRule>(), historyLabel: "clear all rules");
            return n;
        }

        /// <summary>
        /// 마지막 서식 변경(대화상자 확인·에이전트 추가/삭제/전체 삭제)을 되돌린다. 규칙을 변경 전으로 되돌리고 저장 파일도 갱신한다.
        /// 이력이 없으면 null. 셀 편집 되돌리기(Ctrl+Z)와 별개다.
        /// </summary>
        internal ConditionalFormatUndoResult? AgentUndoConditionalFormat() => StepFormatHistory(undo: true);

        /// <summary>되돌린 서식 변경을 다시 적용한다. 이력이 없으면 null.</summary>
        internal ConditionalFormatUndoResult? AgentRedoConditionalFormat() => StepFormatHistory(undo: false);

        /// <summary>
        /// 현재 뷰에서 규칙(id) 또는 초안의 조건에 맞는 행 수. 뷰 전체를 훑으며 취소할 수 있다. 색상 눈금 규칙은 조건이 없어 ArgumentException.
        /// </summary>
        internal async Task<ConditionalFormatCount> AgentCountConditionalFormatAsync(string? id, ConditionalFormatRule? draft, CancellationToken ct)
        {
            var doc = RequireFormatDoc();
            ConditionalFormatRule rule;
            if (draft is not null)
            {
                rule = draft;
                string? problem = ConditionalFormatRules.Validate(rule, doc.Header);
                if (problem is not null) throw new ArgumentException(problem);
            }
            else
            {
                rule = _cfRules.FirstOrDefault(r => string.Equals(r.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? throw new ArgumentException($"There is no conditional format '{id}'.");
            }
            if (rule.Kind != ConditionalFormatKind.Expression) throw new ArgumentException("A color-scale rule has no condition to count.");
            var rows = doc.SnapshotViewRows();
            string[] headers = doc.Header;
            string expression = rule.Expression;
            return await Task.Run(() => ConditionalFormatSet.Count(expression, headers, rows, 0, ct), ct);
        }
    }
}
