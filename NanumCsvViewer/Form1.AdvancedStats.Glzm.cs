using System.Globalization;
using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private static readonly GlmFamily[] GlmFamilyOrder =
            [GlmFamily.Gaussian, GlmFamily.Binomial, GlmFamily.Poisson, GlmFamily.Gamma];

        private async void AdvGlzm()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new FormulaDialog(
                LT("Generalized Linear Model", "일반화선형모형"),
                AdvHeaders(), ColumnLabels(), _palette, SuggestGlmFormula(),
                LT("response ~ predictors. C(name) forces a factor, a:b is an interaction. Event level is used only for the binomial family (blank = second sorted level, or 1).",
                   "반응 ~ 설명변수. C(이름)은 요인 강제, a:b는 상호작용. 사건 수준은 이항 분포에서만 쓰입니다(비우면 정렬상 두 번째 수준, 또는 1)."));
            var familyBox = dlg.AddOption(LT("Family", "분포족"), GlmFamilyOrder.Select(FamilyLabel), 0);
            var linkBox = dlg.AddOption(LT("Link", "연결 함수"), LinkLabels(GlmFamily.Gaussian), 0);
            var eventBox = dlg.AddTextOption(LT("Event level (binomial)", "사건 수준(이항)"), "");
            familyBox.SelectedIndexChanged += (_, _) => RepopulateLinks(familyBox, linkBox);
            if (!dlg.ShowOk(this)) return;
            if (dlg.Parsed is not { } formula) return;

            var family = GlmFamilyOrder[Math.Clamp(familyBox.SelectedIndex, 0, GlmFamilyOrder.Length - 1)];
            var links = LinksFor(family);
            var link = links[Math.Clamp(linkBox.SelectedIndex, 0, links.Length - 1)];
            string eventLevel = eventBox.Text.Trim();
            string title = LT("Generalized Linear Model", "일반화선형모형");

            await RunAdvancedAsync(title, input =>
            {
                var design = DesignMatrixBuilder.Build(input.Rows, input.Headers, formula, input.KindOf,
                    new DesignMatrixOptions
                    {
                        Response = family == GlmFamily.Binomial ? ResponseKind.Binary : ResponseKind.Numeric,
                        BinaryEventLevel = family == GlmFamily.Binomial && eventLevel.Length > 0 ? eventLevel : null,
                    }, input.Cancellation);
                bool logistic = family == GlmFamily.Binomial && link == GlmLink.Logit;
                var fit = GeneralizedLinearModel.Fit(design, family, link, logistic, input.Cancellation);
                return RenderGlm(design, fit, logistic);
            });
        }

        private async void AdvLogistic()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new FormulaDialog(
                LT("Logistic Regression", "로지스틱 회귀"),
                AdvHeaders(), ColumnLabels(), _palette, SuggestGlmFormula(),
                LT("Binomial logit. response ~ predictors. Blank event level = second sorted level (or 1 for a 0/1 column).",
                   "이항 로짓. 반응 ~ 설명변수. 사건 수준을 비우면 정렬상 두 번째 수준(0/1 열이면 1)입니다."));
            var eventBox = dlg.AddTextOption(LT("Event level", "사건 수준"), "");
            if (!dlg.ShowOk(this)) return;
            if (dlg.Parsed is not { } formula) return;

            string eventLevel = eventBox.Text.Trim();
            string title = LT("Logistic Regression", "로지스틱 회귀");
            await RunAdvancedAsync(title, input =>
            {
                var design = DesignMatrixBuilder.Build(input.Rows, input.Headers, formula, input.KindOf,
                    new DesignMatrixOptions
                    {
                        Response = ResponseKind.Binary,
                        BinaryEventLevel = eventLevel.Length > 0 ? eventLevel : null,
                    }, input.Cancellation);
                var fit = GeneralizedLinearModel.Fit(design, GlmFamily.Binomial, GlmLink.Logit, logisticExtras: true, input.Cancellation);
                return RenderGlm(design, fit, logistic: true);
            });
        }

        private static void RepopulateLinks(ComboBox familyBox, ComboBox linkBox)
        {
            int fam = Math.Clamp(familyBox.SelectedIndex, 0, GlmFamilyOrder.Length - 1);
            var labels = LinkLabels(GlmFamilyOrder[fam]);
            linkBox.Items.Clear();
            foreach (var label in labels) linkBox.Items.Add(label);
            if (linkBox.Items.Count > 0) linkBox.SelectedIndex = 0;
        }

        private string SuggestGlmFormula()
        {
            var headers = AdvHeaders();
            if (headers.Length == 0) return "";
            var preds = new List<string>();
            for (int c = 1; c < headers.Length && preds.Count < 3; c++) preds.Add(headers[c]);
            return FormulaText.MainEffects(headers[0], preds);
        }

        private static GlmLink[] LinksFor(GlmFamily family) => family switch
        {
            GlmFamily.Gaussian => [GlmLink.Identity, GlmLink.Log, GlmLink.Inverse],
            GlmFamily.Binomial => [GlmLink.Logit, GlmLink.Probit, GlmLink.CLogLog],
            GlmFamily.Poisson => [GlmLink.Log, GlmLink.Identity, GlmLink.Sqrt],
            GlmFamily.Gamma => [GlmLink.Inverse, GlmLink.Log, GlmLink.Identity],
            _ => [GlmLink.Identity],
        };

        private static string[] LinkLabels(GlmFamily family) => LinksFor(family).Select(LinkLabel).ToArray();

        private static string FamilyLabel(GlmFamily family) => family switch
        {
            GlmFamily.Gaussian => LT("Gaussian", "가우시안"),
            GlmFamily.Binomial => LT("Binomial", "이항"),
            GlmFamily.Poisson => LT("Poisson", "포아송"),
            GlmFamily.Gamma => LT("Gamma", "감마"),
            _ => family.ToString(),
        };

        private static string LinkLabel(GlmLink link) => link switch
        {
            GlmLink.Identity => LT("Identity", "항등"),
            GlmLink.Log => LT("Log", "로그"),
            GlmLink.Inverse => LT("Inverse", "역수"),
            GlmLink.Logit => LT("Logit", "로짓"),
            GlmLink.Probit => LT("Probit", "프로빗"),
            GlmLink.CLogLog => LT("Complementary log-log", "보완 로그-로그"),
            GlmLink.Sqrt => LT("Square root", "제곱근"),
            _ => link.ToString(),
        };

        private static string RenderGlm(DesignMatrix design, GeneralizedLinearFit fit, bool logistic)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(design.RowsRead, design.RowCount, design.RowsDropped));
            sb.AppendLine();
            if (!fit.Converged)
            {
                sb.AppendLine(LT("NOT CONVERGED — the estimates below are not valid maximum-likelihood estimates. Do not interpret them.",
                    "수렴하지 않음 — 아래 추정치는 유효한 최대가능도 추정치가 아닙니다. 해석하지 마세요."));
                sb.AppendLine();
            }
            if (fit.Diagnostics.Contains(GlmDiagnostic.Separation))
            {
                sb.AppendLine(LT("SEPARATION — fitted probabilities are numerically 0/1 or coefficients diverged. Estimates and standard errors are unreliable.",
                    "분리 — 적합 확률이 수치적으로 0/1이거나 계수가 발산했습니다. 추정치와 표준오차는 신뢰할 수 없습니다."));
                sb.AppendLine();
            }

            sb.AppendLine(LT("Generalized linear model (IRLS)", "일반화선형모형 (IRLS)"));
            sb.AppendLine(LT("Formula: ", "식: ") + design.Formula);
            sb.AppendLine(LT("Family: ", "분포족: ") + FamilyLabel(fit.Family) + "    " + LT("Link: ", "연결: ") + LinkLabel(fit.Link));
            if (design.ResponseLevels is { Count: 2 } levels)
                sb.AppendLine(LT($"Event: {levels[1]}  (reference {levels[0]})", $"사건: {levels[1]}  (기준 {levels[0]})"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("n = {0}    rank = {1}    df model = {2}    df residual = {3}",
                   "n = {0}    계수 순위 = {1}    모형 자유도 = {2}    잔차 자유도 = {3}"),
                fit.N, fit.Rank, fit.DfModel, fit.DfResid));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("Iterations: {0} / {1}    Converged: {2}", "반복: {0} / {1}    수렴: {2}"),
                fit.Iterations, GeneralizedLinearModel.DefaultMaxIterations,
                fit.Converged ? LT("yes", "예") : LT("no", "아니오")));
            bool fixedScale = fit.Family is GlmFamily.Binomial or GlmFamily.Poisson;
            sb.AppendLine(fixedScale
                ? LT($"Scale φ = {StatFormat.G(fit.Scale)}  (fixed at 1 for binomial and Poisson)",
                     $"척도 φ = {StatFormat.G(fit.Scale)}  (이항·포아송은 1로 고정)")
                : LT($"Scale φ = {StatFormat.G(fit.Scale)}  (Pearson χ² / df residual)",
                     $"척도 φ = {StatFormat.G(fit.Scale)}  (Pearson χ² / 잔차 자유도)"));
            sb.AppendLine(LT("Convergence: |Δ deviance| ≤ 1e-8 (deviance includes the current scale). Wald z, normal 95% CI (use_t = false).",
                "수렴: |Δ 이탈도| ≤ 1e-8 (이탈도는 당시 척도를 반영). Wald z, 정규 95% 신뢰구간(use_t = false)."));
            sb.AppendLine();

            var coef = new TextTable(
                LT("Term", "항"), LT("Coef", "계수"), LT("SE", "표준오차"), "z",
                LT("p", "p"), LT("95% CI", "95% 신뢰구간"));
            for (int j = 0; j < fit.Coefficients.Length; j++)
            {
                string ci = fit.Aliased[j]
                    ? LT("aliased", "별칭")
                    : StatFormat.G(fit.CiLow[j]) + ", " + StatFormat.G(fit.CiHigh[j]);
                coef.AddRow(fit.Names[j], StatFormat.G(fit.Coefficients[j]), StatFormat.G(fit.StdErrors[j]),
                    StatFormat.G(fit.Z[j]), StatFormat.P(fit.PValues[j]) + StatFormat.Stars(fit.PValues[j]), ci);
            }
            sb.AppendLine(LT("Coefficients", "계수"));
            sb.Append(coef.Render());
            sb.AppendLine();

            sb.AppendLine(LT("Fit", "적합"));
            sb.AppendLine(LT("Deviance: ", "이탈도: ") + StatFormat.G(fit.Deviance));
            sb.AppendLine(LT("Null deviance (intercept-only mean): ", "귀무 이탈도(절편만, 평균): ") + StatFormat.G(fit.NullDeviance));
            sb.AppendLine(LT("Pearson χ²: ", "Pearson χ²: ") + StatFormat.G(fit.PearsonChi2));
            sb.AppendLine(LT("Log-likelihood: ", "로그가능도: ") + StatFormat.G(fit.LogLikelihood));
            sb.AppendLine(LT("AIC: ", "AIC: ") + StatFormat.G(fit.Aic));
            sb.AppendLine(LT("BIC (llf): ", "BIC (로그가능도): ") + StatFormat.G(fit.Bic));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                LT("LR χ² vs intercept-only: {0}    df = {1}    p = {2}",
                   "절편만 모형 대비 LR χ²: {0}    자유도 = {1}    p = {2}"),
                StatFormat.G(fit.LikelihoodRatio), fit.DfModel, StatFormat.P(fit.LikelihoodRatioP)));
            if (!fixedScale)
                sb.AppendLine(LT("LR uses 2(llf − llnull) at the reported scale. For Gaussian identity, llf is the concentrated log-likelihood; the χ² p-value treats the scale as known.",
                    "LR은 보고된 척도에서 2(llf − llnull)입니다. 가우시안 항등 연결의 llf는 집중 로그가능도이며, χ² p값은 척도를 알고 있다고 봅니다."));
            sb.AppendLine();

            if (logistic && fit.Logistic is { } log)
            {
                sb.AppendLine(LT("Logistic extras (binomial logit)", "로지스틱 추가 지표(이항 로짓)"));
                var odds = new TextTable(LT("Term", "항"), LT("Odds ratio", "오즈비"), LT("95% CI", "95% 신뢰구간"));
                for (int j = 0; j < fit.Coefficients.Length; j++)
                    odds.AddRow(fit.Names[j], StatFormat.G(log.OddsRatio[j]),
                        StatFormat.G(log.OddsRatioCiLow[j]) + ", " + StatFormat.G(log.OddsRatioCiHigh[j]));
                sb.Append(odds.Render());
                sb.AppendLine(LT("McFadden pseudo-R² (1 − llf/llnull): ", "McFadden 의사 R² (1 − llf/llnull): ") + StatFormat.G(log.McFaddenRSquared));
                sb.AppendLine();
                sb.AppendLine(LT($"Classification at probability {log.Threshold.ToString("0.0", CultureInfo.InvariantCulture)} (event = 1)",
                    $"확률 {log.Threshold.ToString("0.0", CultureInfo.InvariantCulture)} 기준 분류 (사건 = 1)"));
                var cls = new TextTable("TP", "FP", "TN", "FN",
                    LT("Accuracy", "정확도"), LT("Sensitivity", "민감도"), LT("Specificity", "특이도"));
                cls.AddRow(StatFormat.Int(log.TruePositive), StatFormat.Int(log.FalsePositive),
                    StatFormat.Int(log.TrueNegative), StatFormat.Int(log.FalseNegative),
                    StatFormat.F(log.Accuracy, 4), StatFormat.F(log.Sensitivity, 4), StatFormat.F(log.Specificity, 4));
                sb.Append(cls.Render());
                sb.AppendLine(LT("ROC AUC (Mann–Whitney, midranks for ties): ", "ROC AUC (Mann–Whitney, 동점은 평균 순위): ") + StatFormat.G(log.Auc));
                var hl = log.HosmerLemeshow;
                sb.AppendLine(LT("Hosmer–Lemeshow: sort by fitted probability (ties keep row order), split into 10 groups as evenly as possible (the first n mod 10 groups get one extra row). χ² on g−2 df. Groups with a zero expected count are dropped.",
                    "Hosmer–Lemeshow: 적합 확률로 정렬(동점은 행 순서 유지)한 뒤 10개 그룹으로 가능한 한 균등하게 분할(앞쪽 n mod 10개 그룹이 한 행 더). χ² 자유도는 g−2. 기대도수가 0인 그룹은 제외."));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    LT("HL χ² = {0}    groups = {1}    df = {2}    p = {3}{4}",
                       "HL χ² = {0}    그룹 = {1}    자유도 = {2}    p = {3}{4}"),
                    StatFormat.G(hl.ChiSquare), hl.Groups, hl.DegreesOfFreedom, StatFormat.P(hl.PValue),
                    hl.Reliable ? "" : LT("  (not reliable)", "  (신뢰 불가)")));
                sb.AppendLine();
            }

            sb.AppendLine(LT("Warnings", "경고"));
            if (fit.Diagnostics.Count == 0)
                sb.AppendLine(LT("None.", "없음."));
            else
            {
                foreach (var d in fit.Diagnostics)
                    sb.AppendLine("• " + DiagnosticText(d));
            }
            return sb.ToString().TrimEnd();
        }

        private static string DiagnosticText(GlmDiagnostic d) => d switch
        {
            GlmDiagnostic.NotConverged => LT(
                "Did not converge within 100 iterations. Estimates are not valid.",
                "100회 안에 수렴하지 않았습니다. 추정치는 유효하지 않습니다."),
            GlmDiagnostic.Separation => LT(
                "(Quasi-)complete separation: estimates and standard errors are unreliable.",
                "(준)완전 분리: 추정치와 표준오차는 신뢰할 수 없습니다."),
            GlmDiagnostic.AliasedTerms => LT(
                "Aliased (linearly dependent) terms were dropped; their coefficients are blank.",
                "별칭(선형 종속) 항은 제외했습니다. 해당 계수는 비어 있습니다."),
            GlmDiagnostic.SmallSample => LT(
                "Small sample (residual df < 10). Standard errors and tests may be unstable.",
                "표본이 작습니다(잔차 자유도 < 10). 표준오차와 검정이 불안정할 수 있습니다."),
            GlmDiagnostic.NonIntegerPoisson => LT(
                "Poisson response is not all integers. With scale fixed at 1 this is a quasi-likelihood.",
                "포아송 반응이 모두 정수는 아닙니다. 척도를 1로 고정한 준가능도입니다."),
            _ => d.ToString(),
        };
    }
}
