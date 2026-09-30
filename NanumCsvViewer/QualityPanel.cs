using System.Drawing;
using System.Windows.Forms;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer
{
    /// <summary>발견 항목의 표시 문자열(패널·보고서 공용). 엔진은 문화 중립 데이터만 내므로 여기서 현지화한다.</summary>
    internal static class QualityText
    {
        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        public static string SeverityName(QualitySeverity s) => s switch
        {
            QualitySeverity.Critical => LT("Critical", "심각"),
            QualitySeverity.Warning => LT("Warning", "경고"),
            _ => LT("Info", "정보"),
        };

        public static string KindName(QualityCheckKind k) => k switch
        {
            QualityCheckKind.MissingRate => LT("Missing values", "결측"),
            QualityCheckKind.DisguisedMissing => LT("Disguised missing (candidates)", "위장결측 후보"),
            QualityCheckKind.TypeConformance => LT("Type conformance", "타입 부합"),
            QualityCheckKind.CodebookConformance => LT("Codebook conformance", "코드북 대조"),
            QualityCheckKind.ConstantColumn => LT("Constant column", "상수 컬럼"),
            QualityCheckKind.EmptyColumn => LT("Empty column", "빈 컬럼"),
            QualityCheckKind.DuplicateRows => LT("Duplicate rows", "중복 행"),
            QualityCheckKind.Outliers => LT("Outliers (IQR)", "이상치(IQR)"),
            QualityCheckKind.FutureDate => LT("Future dates", "미래 날짜"),
            QualityCheckKind.RaggedRows => LT("Field count mismatch", "필드 수 불일치"),
            QualityCheckKind.KeyUniqueness => LT("Key uniqueness", "키 유일성"),
            QualityCheckKind.Rule => LT("Rule", "규칙"),
            QualityCheckKind.ForeignKeyOrphan => LT("Referential integrity", "참조 무결성"),
            _ => k.ToString(),
        };

        public static string DimensionName(QualityDimension d) => d switch
        {
            QualityDimension.Conformance => LT("Conformance", "적합성"),
            QualityDimension.Completeness => LT("Completeness", "완전성"),
            _ => LT("Plausibility", "타당성"),
        };

        /// <summary>세부 열 텍스트. 예시·분해·울타리 등 발견 종류별 요약.</summary>
        public static string Detail(QualityFinding f)
        {
            string approx = f.Approximate ? "≈ " : "";
            switch (f.Kind)
            {
                case QualityCheckKind.MissingRate:
                {
                    var parts = f.Breakdown.Select(b => b.Value switch
                    {
                        "" => LT($"empty {b.Count:N0}", $"빈값 {b.Count:N0}"),
                        " " => LT($"whitespace {b.Count:N0}", $"공백 {b.Count:N0}"),
                        _ => LT($"null-tokens {b.Count:N0}", $"NA류 {b.Count:N0}"),
                    });
                    double share = f.EvaluatedRows > 0 ? 100.0 * f.ViolationCount / f.EvaluatedRows : 0;
                    return $"{share:0.#}% · {string.Join(" · ", parts)}";
                }
                case QualityCheckKind.DisguisedMissing:
                    return string.Join(", ", f.Breakdown.Select(b => $"{b.Value}×{b.Count:N0}"))
                        + LT(" — confirm before treating as missing", " — 결측 처리 전 확인 필요");
                case QualityCheckKind.TypeConformance:
                case QualityCheckKind.CodebookConformance:
                case QualityCheckKind.FutureDate:
                    return LT("e.g. ", "예: ") + string.Join(", ", f.Examples.Take(3).Select(e => e.Value))
                        + (f.Examples.Count > 3 ? " …" : "");
                case QualityCheckKind.ConstantColumn:
                    return f.ViolationCount == 0 || f.Breakdown.Count == 0
                        ? LT($"all values = \"{f.Label}\"", $"모든 값 = \"{f.Label}\"")
                        : LT($"dominant \"{f.Label}\" · {f.ViolationCount:N0} other(s)",
                             $"지배값 \"{f.Label}\" · 예외 {f.ViolationCount:N0}건");
                case QualityCheckKind.EmptyColumn:
                    return LT("column has no values", "값이 하나도 없는 컬럼");
                case QualityCheckKind.DuplicateRows:
                    // Count=초과 사본, 필터는 원본 포함 전체 그룹을 보여줌 — 두 수치 차이를 명시.
                    return approx + LT($"{f.Label} group(s) · filter shows all incl. originals",
                                       $"그룹 {f.Label}개 · 필터는 원본 포함 전체 표시");
                case QualityCheckKind.Outliers:
                    return approx + LT($"outside [{f.FenceLow:G6}, {f.FenceHigh:G6}]",
                                       $"울타리 [{f.FenceLow:G6}, {f.FenceHigh:G6}] 밖");
                case QualityCheckKind.RaggedRows:
                    return LT("e.g. row ", "예: 행 ")
                        + string.Join(", ", f.Examples.Take(3).Select(e =>
                            LT($"{e.SourceRow}({e.Value} fields)", $"{e.SourceRow}({e.Value}필드)")));
                case QualityCheckKind.KeyUniqueness:
                    // Count=초과 사본, 필터는 원본 포함 전체 중복 그룹 표시.
                    return LT($"{f.Label} dup key group(s), filter incl. originals · e.g. ",
                              $"중복 키 그룹 {f.Label}개, 필터는 원본 포함 · 예: ")
                        + string.Join(", ", f.Breakdown.Take(2).Select(b => $"{b.Value}×{b.Count}"));
                case QualityCheckKind.Rule:
                    return f.ViolationCount == 0
                        ? LT("passed (0 violations)", "통과(위반 0건)")
                        : LT("e.g. ", "예: ") + string.Join("; ", f.Examples.Take(2).Select(e => e.Value));
                case QualityCheckKind.ForeignKeyOrphan:
                {
                    // SkippedRows = 건너뛴 빈 키 건수. 필터는 고아 행만 매칭한다.
                    string ex = string.Join(", ", f.Examples.Take(3).Select(e => $"{e.Value}@{e.SourceRow}"));
                    return LT($"{f.SkippedRows:N0} blank key(s) skipped · e.g. ", $"빈 키 {f.SkippedRows:N0}건 제외 · 예: ") + ex;
                }
                default:
                    return "";
            }
        }

        /// <summary>필터 칩에 붙는 짧은 설명.</summary>
        public static string ChipLabel(QualityFinding f)
        {
            string subject = f.Kind is QualityCheckKind.Rule or QualityCheckKind.KeyUniqueness
                ? f.Label is { Length: > 0 } l && f.Kind == QualityCheckKind.Rule ? l : KindName(f.Kind)
                : f.ColumnName.Length > 0 ? $"{KindName(f.Kind)}: {f.ColumnName}" : KindName(f.Kind);
            return LT("QC ", "품질 ") + subject;
        }
    }

    /// <summary>
    /// 검사 결과 도킹 패널(하단, 이슈 #26). 발견 목록을 심각도 순으로 표시하고
    /// 더블클릭/버튼으로 "위반 행만 필터"(칩 변환)와 "행 점프"를 제공한다 — 설계 논쟁의 핵심 루프.
    /// </summary>
    internal sealed class QualityPanel : Panel
    {
        // 넓은(수천 컬럼) 파일에서 발견이 O(컬럼수)로 늘어 비가상 ListView 항목 생성이 UI를 멈출 수 있어
        // 패널에는 상위 N건만 표시하고 전체는 보고서로 안내한다(설계 논쟁 후 리뷰 반영).
        private const int MaxDisplayed = 2000;

        private readonly ListView _list;
        private readonly Label _title, _summary;
        private readonly Button _btnFilter, _btnJump, _btnExport, _btnAdvanced;
        private readonly ColumnHeader _colSeverity, _colCheck, _colColumn, _colCount, _colDetails;
        private ThemePalette _palette;
        private IReadOnlyList<QualityFinding> _findings = Array.Empty<QualityFinding>();
        private string _summaryText = "";

        public event Action<QualityFinding>? ApplyFilterRequested;
        public event Action<long>? JumpRequested;
        public event Action? ExportRequested;
        /// <summary>"품질 확인 → 고급 통계" 연동(이슈 #27). 버튼 위치를 넘겨 메뉴를 그 아래에 띄운다.</summary>
        public event Action<Control>? AdvancedStatsRequested;
        public event Action? CloseRequested;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        public QualityPanel(ThemePalette palette)
        {
            _palette = palette;
            Dock = DockStyle.Bottom;
            Height = 230;
            BackColor = palette.Surface;
            Padding = new Padding(6, 4, 6, 6);

            var header = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = palette.Surface };
            _title = new Label
            {
                Text = LT("Data Quality Findings", "데이터 품질 검사 결과"),
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = palette.Text,
                AutoSize = true,
                Dock = DockStyle.Left,
                Padding = new Padding(2, 7, 8, 0),
            };
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Right,
                Width = 560,
                WrapContents = false,
                Padding = new Padding(0),
                Margin = new Padding(0),
            };
            // 요약은 제목(왼쪽)·버튼(오른쪽) 사이 남는 영역을 채우고 넘치면 말줄임 — 버튼을 덮지 않는다.
            _summary = new Label
            {
                ForeColor = palette.Text,
                AutoSize = false,
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
            };

            Button Make(string text, Action onClick, int minW = 96)
            {
                var b = new Button
                {
                    Text = text,
                    AutoSize = true,
                    MinimumSize = new Size(minW, 24),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = palette.Surface,
                    ForeColor = palette.Text,
                    Margin = new Padding(3, 2, 0, 2),
                    TabStop = false,
                };
                b.FlatAppearance.BorderColor = palette.Border;
                b.Click += (_, _) => onClick();
                return b;
            }
            var close = Make("✕", () => CloseRequested?.Invoke(), 28);
            _btnExport = Make(LT("Report…", "보고서…"), () => ExportRequested?.Invoke());
            _btnAdvanced = Make(LT("Advanced Stats ▾", "고급 통계 ▾"), () => AdvancedStatsRequested?.Invoke(_btnAdvanced!), 110);
            _btnJump = Make(LT("Go to Row", "행 이동"), JumpToSelected);
            _btnFilter = Make(LT("Filter Rows", "위반 행만 보기"), ApplySelected);
            buttons.Controls.Add(close);
            buttons.Controls.Add(_btnExport);
            buttons.Controls.Add(_btnAdvanced);
            buttons.Controls.Add(_btnJump);
            buttons.Controls.Add(_btnFilter);

            // Fill(_summary)을 먼저 Add하고 Dock=Left/Right가 그 위에 자리를 잡도록 순서를 준다.
            header.Controls.Add(_summary);
            header.Controls.Add(_title);
            header.Controls.Add(buttons);

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                Dock = DockStyle.Fill,
                BackColor = palette.GridBg,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _colSeverity = new ColumnHeader { Text = LT("Severity", "심각도"), Width = 76 };
            _colCheck = new ColumnHeader { Text = LT("Check", "검사"), Width = 170 };
            _colColumn = new ColumnHeader { Text = LT("Column", "컬럼"), Width = 150 };
            _colCount = new ColumnHeader { Text = LT("Count", "건수"), Width = 90, TextAlign = HorizontalAlignment.Right };
            _colDetails = new ColumnHeader { Text = LT("Details", "세부"), Width = 560 };
            _list.Columns.AddRange(new[] { _colSeverity, _colCheck, _colColumn, _colCount, _colDetails });
            _list.DoubleClick += (_, _) => ApplySelected();
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();

            Controls.Add(_list);
            Controls.Add(header);
            UpdateButtons();
        }

        public void ShowFindings(IReadOnlyList<QualityFinding> findings, string summaryText)
        {
            _findings = findings;
            _summaryText = summaryText;
            int shown = Math.Min(findings.Count, MaxDisplayed);
            _summary.Text = findings.Count > MaxDisplayed
                ? summaryText + LT($"  ·  showing {shown:N0}/{findings.Count:N0} (full list in report)",
                                   $"  ·  {findings.Count:N0}건 중 {shown:N0}건 표시(전체는 보고서)")
                : summaryText;

            _list.BeginUpdate();
            _list.Items.Clear();
            var items = new ListViewItem[shown];
            for (int i = 0; i < shown; i++)
            {
                var f = findings[i];
                var item = new ListViewItem(QualityText.SeverityName(f.Severity))
                {
                    Tag = f,
                    UseItemStyleForSubItems = false,
                };
                item.SubItems[0].ForeColor = SeverityColor(f.Severity);
                string check = f.Kind == QualityCheckKind.Rule && f.Label is { Length: > 0 }
                    ? $"{QualityText.KindName(f.Kind)}: {f.Label}"
                    : QualityText.KindName(f.Kind);
                item.SubItems.Add(MakeSub(check));
                item.SubItems.Add(MakeSub(f.Column >= 0 ? f.ColumnName : f.ColumnName.Length > 0 ? f.ColumnName : "—"));
                item.SubItems.Add(MakeSub((f.Approximate ? "≈" : "") + f.ViolationCount.ToString("N0")));
                item.SubItems.Add(MakeSub(QualityText.Detail(f)));
                items[i] = item;
            }
            _list.Items.AddRange(items);
            _list.EndUpdate();
            UpdateButtons();

            ListViewItem.ListViewSubItem MakeSub(string text)
                => new() { Text = text, ForeColor = _list.ForeColor };
        }

        private Color SeverityColor(QualitySeverity s) => s switch
        {
            QualitySeverity.Critical => Color.OrangeRed,
            QualitySeverity.Warning => Color.DarkOrange,
            _ => _list.ForeColor,
        };

        /// <summary>테마 전환 시 색을 다시 적용하고 목록을 재구성(서브아이템 ForeColor가 옛 색으로 고정되는 문제 해결).</summary>
        public void ApplyPalette(ThemePalette palette)
        {
            _palette = palette;
            BackColor = palette.Surface;
            _title.ForeColor = palette.Text;
            _summary.ForeColor = palette.Text;
            _list.BackColor = palette.GridBg;
            _list.ForeColor = palette.Text;
            foreach (Control c in Controls) c.BackColor = palette.Surface;
            foreach (var b in new[] { _btnFilter, _btnJump, _btnExport })
            {
                b.BackColor = palette.Surface;
                b.ForeColor = palette.Text;
                b.FlatAppearance.BorderColor = palette.Border;
            }
            ShowFindings(_findings, _summaryText); // 서브아이템 색을 새 팔레트로 다시 채운다
        }

        /// <summary>언어 전환 시 제목·버튼·컬럼 헤더 텍스트를 다시 적용(패널은 1회 생성·캐시되므로 수동 갱신).</summary>
        public void Relocalize()
        {
            _title.Text = LT("Data Quality Findings", "데이터 품질 검사 결과");
            _btnExport.Text = LT("Report…", "보고서…");
            _btnAdvanced.Text = LT("Advanced Stats ▾", "고급 통계 ▾");
            _btnJump.Text = LT("Go to Row", "행 이동");
            _btnFilter.Text = LT("Filter Rows", "위반 행만 보기");
            _colSeverity.Text = LT("Severity", "심각도");
            _colCheck.Text = LT("Check", "검사");
            _colColumn.Text = LT("Column", "컬럼");
            _colCount.Text = LT("Count", "건수");
            _colDetails.Text = LT("Details", "세부");
            ShowFindings(_findings, _summaryText); // 행 텍스트(QualityText)도 새 언어로
        }

        private QualityFinding? Selected
            => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as QualityFinding : null;

        private void UpdateButtons()
        {
            var f = Selected;
            _btnFilter.Enabled = f?.ViolationPredicate is not null;
            _btnJump.Enabled = f is { Examples.Count: > 0 };
            _btnExport.Enabled = _findings.Count > 0;
        }

        private void ApplySelected()
        {
            if (Selected is { ViolationPredicate: not null } f) ApplyFilterRequested?.Invoke(f);
        }

        private void JumpToSelected()
        {
            if (Selected is { Examples.Count: > 0 } f) JumpRequested?.Invoke(f.Examples[0].SourceRow);
        }
    }

    /// <summary>
    /// 타당성 규칙 관리 다이얼로그: "0건이어야 하는 위반 조건식" 목록의 추가·편집·삭제·JSON 저장/불러오기.
    /// 내장 의료 규칙 팩은 두지 않는다(설계 논쟁 4:0) — 규칙은 사용자가 소유하는 살아있는 자산.
    /// </summary>
    internal sealed class QualityRulesDialog : Form
    {
        private readonly ListView _list;
        private readonly IReadOnlyList<string> _headers;
        private readonly ThemePalette _palette;
        private bool _syncingChecks;

        /// <summary>작업 사본. OK/실행으로 닫히면 호출자가 반영한다.</summary>
        public List<QualityRule> Rules { get; }
        public bool RunRequested { get; private set; }

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        public QualityRulesDialog(IEnumerable<QualityRule> initial, IReadOnlyList<string> headers, ThemePalette palette)
        {
            _headers = headers;
            _palette = palette;
            Rules = initial.ToList();

            Text = LT("Validation Rules", "타당성 규칙");
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(720, 420);
            MinimizeBox = false; MaximizeBox = false; ShowIcon = false;
            BackColor = palette.Window; ForeColor = palette.Text;

            var note = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(8, 6, 8, 0),
                ForeColor = palette.Text,
                Text = LT(
                    "A rule is a violation expression — matching rows are violations (expected: 0).  e.g.  age < 0 OR age > 120   ·   [end_date] < [start_date]",
                    "규칙은 위반 조건식입니다 — 매칭되는 행이 위반(기대: 0건).  예)  age < 0 OR age > 120   ·   [end_date] < [start_date]"),
            };

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                CheckBoxes = true,
                MultiSelect = false,
                Dock = DockStyle.Fill,
                BackColor = palette.GridBg,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _list.Columns.Add(LT("Rule", "규칙"), 170);
            _list.Columns.Add(LT("Severity", "심각도"), 80);
            _list.Columns.Add(LT("Violation expression", "위반 조건식"), 400);
            _list.ItemChecked += (_, e) =>
            {
                if (_syncingChecks) return;
                int i = e.Item.Index;
                if (i >= 0 && i < Rules.Count) Rules[i] = Rules[i] with { Enabled = e.Item.Checked };
            };
            _list.DoubleClick += (_, _) => EditSelected();

            var bottom = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(6),
            };
            Button Make(string text, Action onClick, int minW = 84)
            {
                var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(minW, 26) };
                b.Click += (_, _) => onClick();
                return b;
            }
            var run = Make(LT("Run", "실행"), () => { RunRequested = true; DialogResult = DialogResult.OK; Close(); });
            var closeBtn = new Button { Text = LT("Close", "닫기"), AutoSize = true, MinimumSize = new Size(84, 26), DialogResult = DialogResult.OK };
            bottom.Controls.Add(closeBtn);
            bottom.Controls.Add(run);
            bottom.Controls.Add(Make(LT("Save…", "저장…"), SaveRules));
            bottom.Controls.Add(Make(LT("Load…", "불러오기…"), LoadRules));
            bottom.Controls.Add(Make(LT("Delete", "삭제"), DeleteSelected));
            bottom.Controls.Add(Make(LT("Edit…", "편집…"), EditSelected));
            bottom.Controls.Add(Make(LT("Add…", "추가…"), AddRule));

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(note);
            AcceptButton = closeBtn;
            RefreshList();
        }

        private void RefreshList()
        {
            _syncingChecks = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var r in Rules)
            {
                var item = new ListViewItem(r.Name) { Checked = r.Enabled };
                item.SubItems.Add(QualityText.SeverityName(r.Severity));
                item.SubItems.Add(r.Expression);
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            _syncingChecks = false;
        }

        private int SelectedIndex => _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;

        private void AddRule()
        {
            if (EditRuleDialog(null) is { } rule) { Rules.Add(rule); RefreshList(); }
        }

        private void EditSelected()
        {
            int i = SelectedIndex;
            if (i < 0) return;
            if (EditRuleDialog(Rules[i]) is { } rule) { Rules[i] = rule; RefreshList(); }
        }

        private void DeleteSelected()
        {
            int i = SelectedIndex;
            if (i < 0) return;
            Rules.RemoveAt(i);
            RefreshList();
        }

        // 이름·식·심각도를 받고 컴파일 검증까지 통과해야 반환. 실패 시 값 유지한 채 재입력.
        private QualityRule? EditRuleDialog(QualityRule? existing)
        {
            string name = existing?.Name ?? "";
            string expr = existing?.Expression ?? "";
            int sev = (int)(existing?.Severity ?? QualitySeverity.Warning);
            while (true)
            {
                using var dlg = new ParamDialog(existing is null ? LT("Add Rule", "규칙 추가") : LT("Edit Rule", "규칙 편집"), _palette);
                dlg.AddNote(LT("Matching rows are violations (expected 0). [col] compares columns.",
                               "매칭 행이 위반입니다(기대 0건). [컬럼]은 컬럼끼리 비교."));
                var nameBox = dlg.AddText(LT("Name", "이름"), name);
                var exprBox = dlg.AddText(LT("Violation expression", "위반 조건식"), expr);
                var sevBox = dlg.AddCombo(LT("Severity", "심각도"),
                    new[] { QualityText.SeverityName(QualitySeverity.Info), QualityText.SeverityName(QualitySeverity.Warning), QualityText.SeverityName(QualitySeverity.Critical) }, sev);
                if (!dlg.ShowOk(this)) return null;

                name = nameBox.Text.Trim();
                expr = exprBox.Text.Trim();
                sev = sevBox.SelectedIndex;
                if (name.Length == 0) name = expr;
                if (expr.Length == 0) return null;
                try
                {
                    AdvancedFilterExpression.Compile(expr, _headers);
                    // 대괄호 없는 우변이 헤더명이면(예: end_date < start_date) 리터럴 문자열 비교가 되어
                    // 거의 모든 행이 위반으로 잡히는 함정 — 컬럼 비교 의도인지 확인시킨다(컴파일은 허용).
                    if (AdvancedFilterExpression.LooksLikeUnbracketedColumnComparison(expr, _headers))
                    {
                        var r = MessageBox.Show(this,
                            LT("The right side looks like a column name. For a column-to-column comparison, use [brackets], e.g. [end_date] < [start_date]. Keep it as a text comparison?",
                               "우변이 컬럼명처럼 보입니다. 컬럼끼리 비교하려면 [대괄호]를 쓰세요(예: [end_date] < [start_date]). 문자열 비교로 그대로 둘까요?"),
                            LT("Column comparison?", "컬럼 비교 확인"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (r != DialogResult.Yes) continue; // 아니오 → 식 수정하도록 루프 유지
                    }
                    return new QualityRule { Name = name, Expression = expr, Severity = (QualitySeverity)sev, Enabled = existing?.Enabled ?? true };
                }
                catch (AdvancedFilterExpressionException ex)
                {
                    MessageBox.Show(this, ex.Message, LT("Invalid expression", "잘못된 식"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    // 루프 계속 — 입력값 보존
                }
            }
        }

        private void LoadRules()
        {
            using var dlg = new OpenFileDialog { Filter = LT("Rule set (*.json)|*.json", "규칙 세트 (*.json)|*.json") };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var set = QualityRuleSet.Deserialize(System.IO.File.ReadAllText(dlg.FileName));
                Rules.Clear();
                Rules.AddRange(set.Rules);
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, LT("Failed to load rule set", "규칙 세트 불러오기 실패"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SaveRules()
        {
            using var dlg = new SaveFileDialog
            {
                Filter = LT("Rule set (*.json)|*.json", "규칙 세트 (*.json)|*.json"),
                FileName = "quality-rules.json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var set = new QualityRuleSet { Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), Rules = Rules.ToArray() };
                System.IO.File.WriteAllText(dlg.FileName, set.Serialize());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, LT("Failed to save rule set", "규칙 세트 저장 실패"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
