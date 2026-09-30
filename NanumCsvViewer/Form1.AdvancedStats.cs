using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    // ---------------------------------------------------------------- 고급 통계 (이슈 #27, ALGLIB Commercial)
    //
    // 메뉴·공용 실행기·품질 연동만 여기 둔다. 각 분석은 Form1.AdvancedStats.*.cs 부분 파일에 있다.
    // 입력은 문자열 행 스냅샷 대신 뷰의 지연 읽기 목록을 한 번 순회해 필요한 컬럼만 double/범주 코드로
    // 압축한다(Stats/DesignMatrix·FeatureMatrix) — 큰 파일에서도 예산 안에서 전체 뷰를 분석한다.
    public partial class Form1
    {
        private ToolStripMenuItem? _advMenu;

        /// <summary>작업 스레드로 넘기는 분석 입력(UI 컨트롤 접근 금지).</summary>
        internal sealed record AdvancedInput(
            IReadOnlyList<string[]> Rows,
            IReadOnlyList<string> Headers,
            Func<int, VariableKind> KindOf,
            CancellationToken Cancellation);

        private void BuildAdvancedStatsMenu()
        {
            _advMenu = new ToolStripMenuItem();

            var models = new ToolStripMenuItem();
            RegisterLabel(models, "Models", "모형");
            models.DropDownItems.Add(MakeItem("General Linear Model (GLM)…", "일반선형모형(GLM)…", (_, _) => AdvGlm()));
            models.DropDownItems.Add(MakeItem("ANCOVA…", "공분산분석(ANCOVA)…", (_, _) => AdvAncova()));
            models.DropDownItems.Add(MakeItem("Repeated-Measures ANOVA…", "반복측정 분산분석…", (_, _) => AdvRmAnova()));
            models.DropDownItems.Add(new ToolStripSeparator());
            models.DropDownItems.Add(MakeItem("Generalized Linear Model (GLzM)…", "일반화선형모형(GLzM)…", (_, _) => AdvGlzm()));
            models.DropDownItems.Add(MakeItem("Logistic Regression…", "로지스틱 회귀…", (_, _) => AdvLogistic()));
            _advMenu.DropDownItems.Add(models);

            var nonpar = new ToolStripMenuItem();
            RegisterLabel(nonpar, "Nonparametric Tests", "비모수 검정");
            nonpar.DropDownItems.Add(MakeItem("Mann-Whitney U…", "Mann-Whitney U…", (_, _) => AdvMannWhitney()));
            nonpar.DropDownItems.Add(MakeItem("Wilcoxon Signed-Rank…", "Wilcoxon 부호순위…", (_, _) => AdvWilcoxon()));
            nonpar.DropDownItems.Add(MakeItem("Sign Test…", "부호 검정…", (_, _) => AdvSignTest()));
            nonpar.DropDownItems.Add(MakeItem("Kruskal-Wallis H…", "Kruskal-Wallis H…", (_, _) => AdvKruskalWallis()));
            nonpar.DropDownItems.Add(MakeItem("Friedman…", "Friedman…", (_, _) => AdvFriedman()));
            _advMenu.DropDownItems.Add(nonpar);

            var learn = new ToolStripMenuItem();
            RegisterLabel(learn, "Classification · Clustering", "분류·군집");
            learn.DropDownItems.Add(MakeItem("K-means Clustering…", "K-means 군집…", (_, _) => AdvKMeans()));
            learn.DropDownItems.Add(MakeItem("K-Nearest Neighbors (KNN)…", "K-최근접 이웃(KNN)…", (_, _) => AdvKnn()));
            learn.DropDownItems.Add(MakeItem("Naive Bayes…", "나이브 베이즈…", (_, _) => AdvNaiveBayes()));
            _advMenu.DropDownItems.Add(learn);

            var reduce = new ToolStripMenuItem();
            RegisterLabel(reduce, "Dimension Reduction · Features", "차원축소·특성");
            reduce.DropDownItems.Add(MakeItem("Principal Component Analysis (PCA)…", "주성분분석(PCA)…", (_, _) => AdvPca()));
            reduce.DropDownItems.Add(MakeItem("Linear Discriminant Analysis (LDA)…", "선형판별분석(LDA)…", (_, _) => AdvLda()));
            reduce.DropDownItems.Add(MakeItem("Feature Ranking…", "특성 순위(선택)…", (_, _) => AdvFeatureRanking()));
            _advMenu.DropDownItems.Add(reduce);

            RegisterLabel(_advMenu, "Advanced Stats", "고급 통계");
        }

        // ---------------------------------------------------------------- 공용 실행기

        /// <summary>
        /// 백그라운드에서 compute를 실행하고 결과 텍스트를 결과 창에 표시한다. 기존 분석과 같은
        /// 취소·드레인·busy 수명(RunAnalysisOperationAsync)을 쓰며, 사용자 입력 오류(DesignMatrixException,
        /// FormulaParseException, 예산 초과)는 경고 상자로 안내하고 부분 결과를 만들지 않는다.
        /// </summary>
        private async Task RunAdvancedAsync(string title, Func<AdvancedInput, string> compute)
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var doc = _doc;
            var headers = AdvHeaders();
            var kindOf = AdvKindOf();
            var body = await RunAnalysisOperationAsync(doc, (source, cancellation) =>
                compute(new AdvancedInput(source, headers, kindOf, cancellation)));
            if (body is null || _closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;
            ShowAdvancedResult(title, body);
        }

        private void ShowAdvancedResult(string title, string body)
        {
            using var form = new ResultForm(title, body, _palette) { Size = new Size(900, 640) };
            form.ShowDialog(this);
        }

        /// <summary>식·결과에 쓰는 순수 컬럼 이름(타입 배지 없음). 빈 헤더는 ColumnN.</summary>
        private string[] AdvHeaders()
        {
            int n = _doc?.ColumnCount ?? 0;
            var names = new string[n];
            for (int c = 0; c < n; c++)
                names[c] = c < grid.Columns.Count && !string.IsNullOrEmpty(grid.Columns[c].HeaderText)
                    ? grid.Columns[c].HeaderText : $"Column{c + 1}";
            return names;
        }

        /// <summary>추론 타입 기준 변수 종류(수치형 → Numeric, 그 외 → Categorical). UI 스레드에서 캡처.</summary>
        private Func<int, VariableKind> AdvKindOf()
        {
            var kinds = new VariableKind[_doc?.ColumnCount ?? 0];
            for (int c = 0; c < kinds.Length; c++)
                kinds[c] = IsNumericColumn(c) ? VariableKind.Numeric : VariableKind.Categorical;
            return c => c >= 0 && c < kinds.Length ? kinds[c] : VariableKind.Categorical;
        }

        /// <summary>결과 머리말: 분석 범위(현재 뷰)와 목록별 삭제 건수.</summary>
        private static string AdvScope(long rowsRead, long rowsUsed, long rowsDropped)
            => LT($"Scope: current view ({rowsRead:N0} rows) · used {rowsUsed:N0} · excluded (missing/non-numeric) {rowsDropped:N0}",
                  $"분석 범위: 현재 뷰({rowsRead:N0}행) · 사용 {rowsUsed:N0}행 · 제외(결측·수치 아님) {rowsDropped:N0}행");

        // ---------------------------------------------------------------- 품질 → 고급 통계 연동

        /// <summary>
        /// 품질 패널의 "고급 통계" 버튼. 현재 문서에 심각 발견이 남아 있으면 먼저 알린 뒤(차단하지 않음)
        /// 고급 통계 메뉴를 버튼 아래에 띄운다 — "품질 확인 → 분석" 흐름.
        /// </summary>
        private void ShowAdvancedStatsFromQuality(Control anchor)
        {
            if (_advMenu is null || _doc is null || !_doc.IndexingComplete || _busy) return;
            if (ReferenceEquals(_qualityFindingsDoc, _doc))
            {
                int critical = _qualityFindings.Count(f => f.Severity == QualitySeverity.Critical && f.ViolationCount > 0);
                if (critical > 0 && MessageBox.Show(this,
                        LT($"{critical:N0} critical data quality finding(s) remain for this file. Results may be affected. Continue to Advanced Stats?",
                           $"이 파일에 심각한 품질 발견이 {critical:N0}건 남아 있습니다. 분석 결과에 영향을 줄 수 있습니다. 고급 통계로 계속할까요?"),
                        LT("Advanced Stats", "고급 통계"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }
            var menu = new ContextMenuStrip();
            foreach (ToolStripItem item in _advMenu.DropDownItems.Cast<ToolStripItem>().ToList())
            {
                if (item is ToolStripMenuItem mi) menu.Items.Add(CloneMenu(mi));
                else menu.Items.Add(new ToolStripSeparator());
            }
            menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(anchor, new Point(0, anchor.Height));
        }

        /// <summary>
        /// 응답·목표·요인 콤보에서 고른 컬럼을 특성/공변량 목록에서 자동으로 해제한다 — 기본 체크(수치 전부)가
        /// 고른 목표와 겹쳐 "같을 수 없음" 오류로 끝나는 흐름을 막는다. 사용자가 다시 체크하면 검증이 안내한다.
        /// </summary>
        private static void UncheckWhenSelected(ComboBox combo, CheckedListBox list)
        {
            void Sync()
            {
                int c = combo.SelectedIndex;
                if (c >= 0 && c < list.Items.Count && list.GetItemChecked(c)) list.SetItemChecked(c, false);
            }
            combo.SelectedIndexChanged += (_, _) => Sync();
            Sync();
        }

        /// <summary>그룹·클래스 기본 컬럼: 추론 타입이 범주·순서·불리언인 첫 컬럼(식별자·수치 제외). 없으면 0.</summary>
        private int AdvDefaultGroupColumn()
        {
            for (int c = 0; c < _columnSummaries.Length; c++)
            {
                var t = _columnSummaries[c].InferredType;
                if (t.IsCategorical() || t == ColumnValueType.Boolean) return c;
            }
            return 0;
        }

        /// <summary>식별자(행마다 고유한 값) 컬럼 — 특성·그룹 기본 선택에서 제외한다.</summary>
        private bool IsIdentifierColumn(int c)
            => c < _columnSummaries.Length && _columnSummaries[c].InferredType == ColumnValueType.Identifier;

        // 메뉴 항목을 복제(원본 Click을 PerformClick으로 위임) — 같은 항목을 두 부모에 둘 수 없어서.
        private static ToolStripMenuItem CloneMenu(ToolStripMenuItem source)
        {
            var copy = new ToolStripMenuItem(source.Text) { Enabled = source.Enabled };
            if (source.DropDownItems.Count == 0) copy.Click += (_, _) => source.PerformClick();
            foreach (ToolStripItem child in source.DropDownItems)
            {
                if (child is ToolStripMenuItem mi) copy.DropDownItems.Add(CloneMenu(mi));
                else copy.DropDownItems.Add(new ToolStripSeparator());
            }
            return copy;
        }
    }
}
