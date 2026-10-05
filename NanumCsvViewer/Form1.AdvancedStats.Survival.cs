using System.Globalization;
using System.Text;
using NanumCsvViewer.Charting;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private List<Form> _survivalPlots = new();

        private async void AdvKaplanMeier()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            string title = LT("Kaplan–Meier · Log-rank", "Kaplan–Meier · 로그순위");
            if (_doc.ColumnCount < 2)
            {
                ShowResult(title, LT("Need a time column and an event column.", "시간 컬럼과 사건 컬럼이 필요합니다."));
                return;
            }

            int timeCol, eventCol, groupCol;
            string eventLevel;
            using (var dlg = new ParamDialog(title, _palette))
            {
                dlg.AddNote(LT(
                    "Blank event level means 1 = event and 0 = censored. Any other coding needs the event level (that value is the event; other non-missing values are censored).",
                    "사건 수준을 비우면 1=사건, 0=중도절단입니다. 그 외 코딩은 사건 수준을 입력하세요(그 값이 사건, 나머지 비결측은 중도절단)."));
                var time = dlg.AddCombo(LT("Time (numeric, ≥ 0)", "시간(수치, 0 이상)"), ColumnLabels(), FirstNumericColumn());
                var ev = dlg.AddCombo(LT("Event", "사건"), ColumnLabels(),
                    DefaultEventColumn(Math.Min(FirstNumericColumn() + 1, Math.Max(0, _doc.ColumnCount - 1))));
                var level = dlg.AddText(LT("Event level (blank = 1)", "사건 수준(비우면 1)"), "");
                var groupItems = new[] { LT("(none)", "(없음)") }.Concat(ColumnLabels()).ToArray();
                var groupBox = dlg.AddCombo(LT("Group (optional)", "그룹(선택)"), groupItems, 0);
                if (!dlg.ShowOk(this)) return;
                timeCol = time.SelectedIndex;
                eventCol = ev.SelectedIndex;
                groupCol = groupBox.SelectedIndex - 1;
                eventLevel = level.Text.Trim();
            }
            if (timeCol < 0 || eventCol < 0 || timeCol == eventCol || groupCol == timeCol || groupCol == eventCol)
            {
                ShowResult(title, LT("Time, event, and group must be different columns.", "시간·사건·그룹은 서로 다른 컬럼이어야 합니다."));
                return;
            }

            var doc = _doc;
            int? group = groupCol >= 0 ? groupCol : null;
            string? levelArg = eventLevel.Length == 0 ? null : eventLevel;
            var result = await RunAnalysisOperationAsync(doc, (rows, ct) =>
                KaplanMeierAnalysis.FromRows(rows, timeCol, eventCol, levelArg, group, ct));
            if (result is null || _closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;

            string note = LT(
                "Step curve · censor marks · dashed = log-log 95% CI. Display only; the table uses every event time.",
                "계단 곡선 · 중도절단 표식 · 점선 = 로그-로그 95% 신뢰구간. 그림만의 표시이며 표는 모든 사건 시각을 씁니다.");
            var plot = KaplanMeierPlot.Build(result, title,
                LT("Time", "시간"), LT("Survival", "생존확률"), note);
            ShowSurvivalPlot(plot, title);
            ShowAdvancedResult(title, FormatKaplanMeier(result));
        }

        private async void AdvCox()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            string title = LT("Cox Proportional Hazards", "Cox 비례위험");
            if (_doc.ColumnCount < 2)
            {
                ShowResult(title, LT("Need a time column, an event column, and at least one predictor.", "시간·사건 컬럼과 설명변수가 필요합니다."));
                return;
            }

            var headers = AdvHeaders();
            int timeDefault = FirstNumericColumn();
            string initial = headers.Length == 0 ? "" : FormulaText.QuoteName(headers[timeDefault]) + " ~ ";
            using var dlg = new FormulaDialog(
                title, headers, ColumnLabels(), _palette, initial,
                LT("Left side is the time column. Right side is the predictors. The intercept is not fitted (it is dropped if present). Blank event level = 1.",
                   "왼쪽은 시간 컬럼, 오른쪽은 설명변수입니다. 절편은 적합하지 않습니다(식에 있으면 버립니다). 사건 수준을 비우면 1입니다."),
                f => CoxFormulaError(headers, f));
            var eventBox = dlg.AddOption(LT("Event column", "사건 컬럼"), ColumnLabels(),
                DefaultEventColumn(Math.Min(timeDefault + 1, Math.Max(0, headers.Length - 1))));
            var levelBox = dlg.AddTextOption(LT("Event level (blank = 1)", "사건 수준(비우면 1)"), "");
            var tiesBox = dlg.AddOption(LT("Ties", "동점 처리"), new[]
            {
                LT("Efron (default)", "Efron (기본)"),
                LT("Breslow", "Breslow"),
            }, 0);
            if (!dlg.ShowOk(this) || dlg.Parsed is not { } formula) return;

            int eventCol = eventBox.SelectedIndex;
            string eventLevel = levelBox.Text.Trim();
            var ties = tiesBox.SelectedIndex == 1 ? CoxTies.Breslow : CoxTies.Efron;
            int timeCol = StatValue.ResolveColumn(headers, formula.Response);
            if (eventCol == timeCol)
            {
                ShowResult(title, LT("The event column must differ from the time column.", "사건 컬럼은 시간 컬럼과 달라야 합니다."));
                return;
            }

            var doc = _doc;
            var kindOf = AdvKindOf();
            string? levelArg = eventLevel.Length == 0 ? null : eventLevel;
            var fit = await RunAnalysisOperationAsync(doc, (rows, ct) =>
                CoxRegression.FromFormula(rows, headers, formula, eventCol, levelArg, kindOf, ties, ct));
            if (fit is null || _closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;
            ShowAdvancedResult(title, FormatCox(fit, formula));
        }
        /// <summary>사건 컬럼 기본값: 추론 타입이 Boolean인 첫 컬럼. 없으면 fallback(보통 시간 다음 열).</summary>
        internal static int DefaultEventColumn(IReadOnlyList<ColumnValueType> types, int fallback)
        {
            for (int c = 0; c < types.Count; c++)
                if (types[c] == ColumnValueType.Boolean) return c;
            return fallback;
        }

        private int DefaultEventColumn(int fallback)
            => DefaultEventColumn(
                _columnSummaries.Length == 0 ? Array.Empty<ColumnValueType>() : _columnSummaries.Select(s => s.InferredType).ToArray(),
                fallback);

        private string? CoxFormulaError(string[] headers, ModelFormula formula)
        {
            if (formula.PredictorVariables.Count == 0)
                return LT("Add at least one predictor. Cox models have no intercept.", "설명변수를 하나 이상 넣으세요. Cox 모형에는 절편이 없습니다.");
            try
            {
                int col = StatValue.ResolveColumn(headers, formula.Response);
                if (!IsNumericColumn(col))
                    return LT("The time column (left of ~) must be numeric.", "시간 컬럼(~ 왼쪽)은 수치형이어야 합니다.");
            }
            catch (DesignMatrixException ex)
            {
                return Stats.ErrorText.Localize(ex.Message);
            }
            return null;
        }

        private void ShowSurvivalPlot(PlotModel model, string title)
        {
            var form = new Form
            {
                Text = title,
                StartPosition = FormStartPosition.CenterParent,
                Size = new Size(880, 560),
                BackColor = _palette.Window,
            };
            var plot = new PlotControl { Dock = DockStyle.Fill };
            form.Controls.Add(plot);
            form.Shown += (_, _) => plot.SetModel(model, _palette);
            var ownerList = _survivalPlots; // 이 창이 속한 탭의 목록
            ownerList.Add(form);
            form.FormClosed += (_, _) => ownerList.Remove(form);
            form.Show(this);
        }

        private static string FormatKaplanMeier(KaplanMeierResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(result.RowsRead, result.RowsUsed, result.RowsDropped));
            if (result.EventLevel is not null)
                sb.AppendLine(LT($"Event level: {result.EventLevel} (other non-missing values are censored)",
                    $"사건 수준: {result.EventLevel} (그 외 비결측은 중도절단)"));
            else
                sb.AppendLine(LT("Event coding: 1 = event, 0 = censored. Other values were excluded.",
                    "사건 코딩: 1 = 사건, 0 = 중도절단. 그 외 값은 제외했습니다."));
            sb.AppendLine(LT(
                "Kaplan–Meier at event times only (statsmodels SurvfuncRight, compress=True). Distinct times are aggregated after sorting — not a row sample.",
                "Kaplan–Meier는 사건 시각만 보고합니다(statsmodels SurvfuncRight, compress=True). 정렬 후 고유 시각으로 집계하며 행 표본이 아닙니다."));
            sb.AppendLine(LT(
                "SE is Greenwood's formula (same floor and NaN rules as SurvfuncRight). Pointwise 95% CI is log-log (cloglog), the transform used by SurvfuncRight.quantile_ci; summary() itself has no pointwise interval.",
                "표준오차는 Greenwood 공식입니다(SurvfuncRight와 같은 하한·NaN 규칙). 점별 95% 신뢰구간은 로그-로그(cloglog)이며 SurvfuncRight.quantile_ci의 변환과 같습니다. summary() 자체에는 점별 구간이 없습니다."));
            sb.AppendLine(LT(
                "Median is the first event time with S(t) < 0.5. Its CI limits are observed event times (quantile_ci); an upper limit of ∞ means the interval is not closed.",
                "중앙값은 S(t) < 0.5인 첫 사건 시각입니다. 신뢰구간 한계는 관측된 사건 시각(quantile_ci)이며, 상한 ∞는 구간이 닫히지 않았다는 뜻입니다."));
            sb.AppendLine(LT(
                "Restricted mean is the integral of S(t) to the group's maximum observed time (including censoring). Point estimate only — no standard error.",
                "제한 평균은 그 그룹의 최대 관측 시각(중도절단 포함)까지 S(t)를 적분한 값입니다. 점추정만 보고하며 표준오차는 없습니다."));
            sb.AppendLine(LT(
                "The censored column counts censorings at that exact event time. Censorings at times with no event are not separate rows; they reduce later risk sets.",
                "중도절단 열은 그 사건 시각과 정확히 같은 시각의 중도절단 수입니다. 사건이 없는 시각의 중도절단은 별도 행이 아니며, 이후 위험집합만 줄입니다."));
            sb.AppendLine();

            const int show = 40;
            foreach (var curve in result.Curves)
            {
                sb.AppendLine(curve.Group);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    LT("n = {0}    events = {1}    censored = {2}", "n = {0}    사건 = {1}    중도절단 = {2}"),
                    curve.N, curve.Events, curve.Censored));
                sb.AppendLine(LT($"Median: {StatFormat.G(curve.Median)}    95% CI {StatFormat.G(curve.MedianCiLow)}, {StatFormat.G(curve.MedianCiHigh)}",
                    $"중앙값: {StatFormat.G(curve.Median)}    95% 신뢰구간 {StatFormat.G(curve.MedianCiLow)}, {StatFormat.G(curve.MedianCiHigh)}"));
                sb.AppendLine(LT(
                    $"Restricted mean to τ = {StatFormat.G(curve.RestrictedMeanTau)}: {StatFormat.G(curve.RestrictedMean)}",
                    $"제한 평균 (τ = {StatFormat.G(curve.RestrictedMeanTau)}): {StatFormat.G(curve.RestrictedMean)}"));
                if (curve.CensorMarksSampled)
                    sb.AppendLine(LT(
                        $"Plot censor marks: {curve.CensorTimes.Count:N0} of {curve.CensorMarkPopulation:N0} (reservoir sample, fixed seed). Estimates use every censoring.",
                        $"그림의 중도절단 표식: {curve.CensorMarkPopulation:N0}건 중 {curve.CensorTimes.Count:N0}건(시드 고정 저수지 표본). 추정은 모든 중도절단을 씁니다."));
                if (curve.Rows.Count == 0)
                    sb.AppendLine(LT("No events in this group. S(t) = 1 up to the last observed time.",
                        "이 그룹에는 사건이 없습니다. 마지막 관측 시각까지 S(t) = 1입니다."));
                else
                {
                    var table = new TextTable(
                        LT("Time", "시간"), LT("At risk", "위험집합"), LT("Events", "사건"), LT("Censored", "중도절단"),
                        "S(t)", "SE", LT("95% CI", "95% 신뢰구간"));
                    int n = curve.Rows.Count;
                    void Add(SurvivalTableRow row) => table.AddRow(
                        StatFormat.G(row.Time), StatFormat.Int(row.AtRisk), StatFormat.Int(row.Events), StatFormat.Int(row.Censored),
                        StatFormat.G(row.Survival), StatFormat.G(row.StdError),
                        StatFormat.G(row.CiLow) + ", " + StatFormat.G(row.CiHigh));
                    if (n <= show + 8)
                        foreach (var row in curve.Rows) Add(row);
                    else
                    {
                        for (int i = 0; i < show; i++) Add(curve.Rows[i]);
                        table.AddRow("…", "…", "…", "…", "…", "…", "…");
                        for (int i = n - 8; i < n; i++) Add(curve.Rows[i]);
                        sb.AppendLine(LT(
                            $"Table shows {show} + last 8 of {n:N0} event times. Median, CI, and tests use all of them.",
                            $"표는 사건 시각 {n:N0}개 중 앞 {show}개와 마지막 8개입니다. 중앙값·신뢰구간·검정은 전체를 씁니다."));
                    }
                    sb.Append(table.Render());
                }
                sb.AppendLine();
            }

            AppendLogRank(sb, result.LogRank, LT("Log-rank (survdiff weight_type=None)", "로그순위 (survdiff weight_type=None)"));
            AppendLogRank(sb, result.GehanBreslow, LT("Gehan–Breslow / Wilcoxon (survdiff weight_type='gb', weight = number at risk)",
                "Gehan–Breslow / Wilcoxon (survdiff weight_type='gb', 가중치 = 위험집합 크기)"));
            sb.AppendLine(LT(
                "A Kaplan–Meier window (step curve per group, censor marks, dashed log-log CI) opens with this result. PlotControl draws it.",
                "이 결과와 함께 Kaplan–Meier 창(그룹별 계단 곡선, 중도절단 표식, 점선 로그-로그 신뢰구간)이 열립니다. PlotControl이 그립니다."));
            return sb.ToString();
        }

        private static void AppendLogRank(StringBuilder sb, LogRankTest test, string title)
        {
            sb.AppendLine(title);
            switch (test.Issue)
            {
                case LogRankIssue.TooFewGroups:
                    sb.AppendLine(LT("Not computed — a comparison needs at least two groups.", "계산하지 않음 — 비교에는 그룹이 둘 이상 필요합니다."));
                    break;
                case LogRankIssue.NoEvents:
                    sb.AppendLine(LT("Not computed — there are no events.", "계산하지 않음 — 사건이 없습니다."));
                    break;
                case LogRankIssue.Singular:
                    sb.AppendLine(LT("Not computed — the covariance of (O−E) is singular. No χ² is reported.",
                        "계산하지 않음 — (O−E)의 공분산이 특이합니다. χ²를 보고하지 않습니다."));
                    break;
                default:
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        LT("χ² = {0}    df = {1}    p = {2}{3}", "χ² = {0}    자유도 = {1}    p = {2}{3}"),
                        StatFormat.G(test.ChiSquare), test.DegreesOfFreedom, StatFormat.P(test.P), StatFormat.Stars(test.P)));
                    if (test.ReferenceGroup is not null)
                        sb.AppendLine(LT(
                            $"Reference group (dropped from the (O−E) vector, matching np.unique order in survdiff): {test.ReferenceGroup}",
                            $"기준 그룹 (survdiff의 np.unique 순서와 같이 (O−E) 벡터에서 제외): {test.ReferenceGroup}"));
                    if (test.Weight == LogRankWeight.LogRank && test.Counts.Count > 0)
                    {
                        var counts = new TextTable(LT("Group", "그룹"), LT("Observed", "관측"), LT("Expected", "기대"));
                        foreach (var c in test.Counts)
                            counts.AddRow(c.Group, StatFormat.Int(c.Observed), StatFormat.G(c.Expected));
                        sb.Append(counts.Render());
                    }
                    break;
            }
            sb.AppendLine();
        }

        private static string FormatCox(CoxFit fit, ModelFormula formula)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(fit.RowsRead, fit.RowsUsed, fit.RowsDropped));
            sb.AppendLine(LT("Formula: ", "식: ") + formula + LT("   (intercept not fitted)", "   (절편은 적합하지 않음)"));
            sb.AppendLine(fit.EventLevel is null
                ? LT("Event coding: 1 = event, 0 = censored.", "사건 코딩: 1 = 사건, 0 = 중도절단.")
                : LT($"Event level: {fit.EventLevel}", $"사건 수준: {fit.EventLevel}"));
            sb.AppendLine(LT($"Ties: {(fit.Ties == CoxTies.Efron ? "Efron" : "Breslow")}    (statsmodels PHReg ties=)",
                $"동점: {(fit.Ties == CoxTies.Efron ? "Efron" : "Breslow")}    (statsmodels PHReg ties=)"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("n = {0}    events = {1}    predictors = {2}", "n = {0}    사건 = {1}    설명변수 = {2}"),
                fit.RowsUsed, fit.Events, fit.DfModel));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("Iterations: {0} / {1}    Converged: {2}", "반복: {0} / {1}    수렴: {2}"),
                fit.Iterations, CoxRegression.MaxIterations, fit.Converged ? LT("yes", "예") : LT("no", "아니오")));
            sb.AppendLine(LT(
                "Newton–Raphson on the partial log-likelihood, step halving if a step does not increase it. The Newton step uses a 1e-10 ridge on the information diagonal (statsmodels ridge_factor). Standard errors use the unridged inverse Hessian.",
                "부분 로그가능도에 대한 Newton–Raphson이며, 스텝이 가능도를 올리지 않으면 스텝을 이등분합니다. 뉴턴 스텝은 정보행렬 대각에 1e-10 능선을 더합니다(statsmodels ridge_factor). 표준오차는 능선 없는 역헤시안입니다."));
            sb.AppendLine();

            if (!fit.Converged)
            {
                sb.AppendLine(LT(
                    "NOT CONVERGED — the estimates below are not valid maximum partial-likelihood estimates. Do not interpret them.",
                    "수렴하지 않음 — 아래 추정치는 유효한 최대 부분가능도 추정치가 아닙니다. 해석하지 마세요."));
                sb.AppendLine();
            }
            if (fit.MonotoneLikelihood)
            {
                sb.AppendLine(LT(
                    "MONOTONE LIKELIHOOD — at least one coefficient diverged (possible separation; the MLE may be infinite). Hazard ratios and standard errors are unreliable.",
                    "단조 가능도 — 계수가 적어도 하나 발산했습니다(분리 가능, 최대가능도가 무한일 수 있음). 위험비와 표준오차는 신뢰할 수 없습니다."));
                sb.AppendLine();
            }

            var coef = new TextTable(
                LT("Term", "항"), LT("Coef", "계수"), LT("SE", "표준오차"), "z", LT("p", "p"),
                "HR", LT("HR 95% CI", "HR 95% 신뢰구간"));
            for (int j = 0; j < fit.Coefficients.Length; j++)
                coef.AddRow(fit.Names[j], StatFormat.G(fit.Coefficients[j]), StatFormat.G(fit.StdErrors[j]),
                    StatFormat.G(fit.Z[j]), StatFormat.P(fit.PValues[j]) + StatFormat.Stars(fit.PValues[j]),
                    StatFormat.G(fit.HazardRatio[j]),
                    StatFormat.G(fit.HrCiLow[j]) + ", " + StatFormat.G(fit.HrCiHigh[j]));
            sb.AppendLine(LT("Coefficients (log hazard ratio)", "계수 (로그 위험비)"));
            sb.Append(coef.Render());
            sb.AppendLine();

            sb.AppendLine(LT("Partial log-likelihood: ", "부분 로그가능도: ") + StatFormat.G(fit.LogLikelihood));
            sb.AppendLine(LT("Null partial log-likelihood (β = 0): ", "귀무 부분 로그가능도(β = 0): ") + StatFormat.G(fit.NullLogLikelihood));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("LR χ² (2(llf − llnull)): {0}    df = {1}    p = {2}",
                   "LR χ² (2(llf − llnull)): {0}    자유도 = {1}    p = {2}"),
                StatFormat.G(fit.LikelihoodRatio), fit.DfModel, StatFormat.P(fit.LikelihoodRatioP)));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("Score χ² at β = 0: {0}    df = {1}    p = {2}",
                   "스코어 χ² (β = 0): {0}    자유도 = {1}    p = {2}"),
                StatFormat.G(fit.Score), fit.DfModel, StatFormat.P(fit.ScoreP)));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("Wald χ² (β′ I β): {0}    df = {1}    p = {2}",
                   "Wald χ² (β′ I β): {0}    자유도 = {1}    p = {2}"),
                StatFormat.G(fit.Wald), fit.DfModel, StatFormat.P(fit.WaldP)));
            if (!fit.Converged)
                sb.AppendLine(LT("LR and Wald are omitted when the fit did not converge. The score test does not need the MLE.",
                    "수렴하지 않으면 LR과 Wald는 보고하지 않습니다. 스코어 검정은 최대가능도가 필요 없습니다."));
            sb.AppendLine(LT(
                $"Harrell's C = {StatFormat.G(fit.Concordance)}    comparable pairs = {fit.ComparablePairs:N0}    concordant = {fit.ConcordantPairs:N0}    discordant = {fit.DiscordantPairs:N0}    tied risk = {fit.TiedRiskPairs:N0}",
                $"Harrell's C = {StatFormat.G(fit.Concordance)}    비교 쌍 = {fit.ComparablePairs:N0}    일치 = {fit.ConcordantPairs:N0}    불일치 = {fit.DiscordantPairs:N0}    위험 동점 = {fit.TiedRiskPairs:N0}"));
            sb.AppendLine(LT(
                "Concordance is exact (all comparable pairs, O(n log n) Fenwick tree), not a sample. Pairs with equal times are not compared. Tied linear predictors count 0.5.",
                "일치도는 전체 비교 쌍의 정확값입니다(O(n log n) 펜윅 트리, 표본 아님). 시각이 같은 쌍은 비교하지 않습니다. 선형예측자가 같으면 0.5로 셉니다."));
            sb.AppendLine(LT(
                "No Schoenfeld residual proportional-hazards test — omitted rather than reported from an unchecked implementation. Column cap is 64 because each Newton step is O(n p²); rows are not sampled.",
                "Schoenfeld 잔차 비례위험 가정 검정은 넣지 않았습니다. 검증되지 않은 구현으로 보고하지 않기 위해서입니다. 뉴턴 스텝이 O(n p²)이라 설명변수는 64개까지이며, 행은 표본 추출하지 않습니다."));
            return sb.ToString();
        }
    }
}
