using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvLmm()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var headers = AdvHeaders();
            if (headers.Length == 0) return;
            int yCol = FirstNumericColumn();
            string initial = FormulaText.QuoteName(headers[yCol]) + " ~ ";
            using var dlg = new FormulaDialog(
                LT("Linear Mixed Model (LMM)", "선형 혼합모형(LMM)"),
                headers, ColumnLabels(), _palette, initial,
                LT("Fixed effects: response ~ predictors. One grouping factor, optional random slopes (comma-separated numeric columns; blank = random intercept only). The random-effects covariance is unstructured. REML is the default.",
                   "고정효과: 반응 ~ 설명변수. 그룹 요인 1개, 선택적 임의 기울기(쉼표로 구분한 수치 열, 비우면 임의 절편만). 임의효과 공분산은 비구조입니다. 기본은 REML."),
                f => GlmResponseError(headers, f, IsNumericColumn));
            var group = dlg.AddOption(LT("Group", "그룹"), headers, Math.Clamp(AdvDefaultGroupColumn(), 0, headers.Length - 1));
            var slopes = dlg.AddTextOption(LT("Random slopes", "임의 기울기"), "");
            var method = dlg.AddOption(LT("Criterion", "기준"), new[] { "REML", "ML" }, 0);
            if (!dlg.ShowOk(this)) return;

            var formula = dlg.Parsed!;
            int groupCol = group.SelectedIndex;
            string slopeText = slopes.Text;
            bool reml = method.SelectedIndex == 0;
            string title = LT("Linear Mixed Model (LMM)", "선형 혼합모형(LMM)");
            await RunAdvancedAsync(title, input =>
            {
                var slopeCols = ParseSlopeColumns(slopeText, input.Headers, formula);
                var result = MixedModel.Fit(input.Rows, input.Headers, formula, input.KindOf, groupCol, slopeCols, reml, input.Cancellation);
                return FormatLmmResult(formula, input.Headers[groupCol], slopeCols.Select(c => input.Headers[c]).ToArray(), result);
            });
        }

        private async void AdvNlmm()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var headers = AdvHeaders();
            if (headers.Length == 0) return;
            var models = new[]
            {
                NonlinearMean.ExponentialDecay,
                NonlinearMean.LogisticGrowth,
                NonlinearMean.MichaelisMenten,
                NonlinearMean.Emax,
            };
            string[] modelLabels = models.Select(ModelLabel).ToArray();
            using var dlg = new ParamDialog(LT("Nonlinear Mixed Model (NLMM)", "비선형 혼합모형(NLMM)"), _palette);
            var y = dlg.AddCombo(LT("Response (numeric)", "반응(수치)"), ColumnLabels(), FirstNumericColumn());
            var x = dlg.AddCombo(LT("Predictor / time", "예측변수·시간"), ColumnLabels(), SecondNumericColumn(FirstNumericColumn()));
            var g = dlg.AddCombo(LT("Group", "그룹"), ColumnLabels(), Math.Clamp(AdvDefaultGroupColumn(), 0, headers.Length - 1));
            var model = dlg.AddCombo(LT("Mean function", "평균함수"), modelLabels, 0);
            var random = dlg.AddCheckedList(LT("Random parameters", "임의효과 모수"), NonlinearMixedModel.ParameterNames(models[0]), 4);
            random.SetItemChecked(0, true);
            model.SelectedIndexChanged += (_, _) =>
            {
                int sel = Math.Clamp(model.SelectedIndex, 0, models.Length - 1);
                random.Items.Clear();
                foreach (string name in NonlinearMixedModel.ParameterNames(models[sel])) random.Items.Add(name);
                if (random.Items.Count > 0) random.SetItemChecked(0, true);
            };
            dlg.AddNote(LT(
                "Lindstrom–Bates (alternating PNLS / linear mixed model, inner fit is ML). Uncheck every parameter for ordinary nonlinear least squares. Approximate standard errors are stated with the result.",
                "Lindstrom–Bates(벌점 비선형최소제곱과 선형혼합모형 교대, 안쪽 적합은 ML). 모수를 모두 해제하면 일반 비선형최소제곱입니다. 표준오차는 근사며 방법에 적습니다."));
            if (!dlg.ShowOk(this)) return;

            int yCol = y.SelectedIndex, xCol = x.SelectedIndex, gCol = g.SelectedIndex;
            var chosen = models[Math.Clamp(model.SelectedIndex, 0, models.Length - 1)];
            var randomIndex = new List<int>();
            for (int i = 0; i < random.Items.Count; i++)
                if (random.GetItemChecked(i)) randomIndex.Add(i);
            string title = LT("Nonlinear Mixed Model (NLMM)", "비선형 혼합모형(NLMM)");
            await RunAdvancedAsync(title, input =>
            {
                var result = NonlinearMixedModel.Fit(input.Rows, input.Headers, yCol, xCol, gCol, chosen, randomIndex, input.Cancellation);
                return FormatNlmmResult(input.Headers[yCol], input.Headers[xCol], input.Headers[gCol], result);
            });
        }

        private static int[] ParseSlopeColumns(string text, IReadOnlyList<string> headers, ModelFormula formula)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
            int response = StatValue.ResolveColumn(headers, formula.Response);
            var cols = new List<int>();
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int c = StatValue.ResolveColumn(headers, part);
                if (c == response) throw new DesignMatrixException($"Random slope '{headers[c]}' is the response.");
                if (cols.Contains(c)) throw new DesignMatrixException($"Random slope '{headers[c]}' is listed twice.");
                cols.Add(c);
            }
            return cols.ToArray();
        }

        private int SecondNumericColumn(int first)
        {
            for (int c = 0; c < _columnSummaries.Length; c++)
                if (c != first && IsNumericColumn(c)) return c;
            return first;
        }

        private static string ModelLabel(NonlinearMean model) => model switch
        {
            NonlinearMean.ExponentialDecay => LT("Exponential decay  a·exp(−b·x)", "지수 감쇠  a·exp(−b·x)"),
            NonlinearMean.LogisticGrowth => LT("Logistic growth  a / (1 + exp((b − x) / c))", "로지스틱 성장  a / (1 + exp((b − x) / c))"),
            NonlinearMean.MichaelisMenten => LT("Michaelis–Menten  a·x / (b + x)", "Michaelis–Menten  a·x / (b + x)"),
            NonlinearMean.Emax => LT("Emax  a + b·x / (c + x)", "Emax  a + b·x / (c + x)"),
            _ => model.ToString(),
        };

        internal static string FormatLmmResult(ModelFormula formula, string group, IReadOnlyList<string> slopes, MixedModelResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(LT("Linear mixed model", "선형 혼합모형"));
            sb.AppendLine(formula.ToString());
            sb.AppendLine(LT($"Group: {group}    criterion: {(result.Reml ? "REML" : "ML")}",
                             $"그룹: {group}    기준: {(result.Reml ? "REML" : "ML")}"));
            sb.AppendLine(slopes.Count == 0
                ? LT("Random effects: intercept", "임의효과: 절편")
                : LT($"Random effects: intercept + {string.Join(", ", slopes)} (unstructured covariance)",
                     $"임의효과: 절편 + {string.Join(", ", slopes)} (비구조 공분산)"));
            sb.AppendLine(AdvScope(result.RowsRead, result.RowsUsed, result.RowsDropped));
            sb.AppendLine(LT($"Groups {StatFormat.Int(result.GroupCount)}    size min {StatFormat.Int(result.MinGroupSize)}    max {StatFormat.Int(result.MaxGroupSize)}",
                             $"그룹 {StatFormat.Int(result.GroupCount)}    크기 최소 {StatFormat.Int(result.MinGroupSize)}    최대 {StatFormat.Int(result.MaxGroupSize)}"));
            sb.AppendLine(result.Converged
                ? LT("Converged.", "수렴했습니다.")
                : LT("Did not converge. Estimates are the best point found; do not treat standard errors as confirmed.",
                     "수렴하지 않았습니다. 추정값은 지금까지 찾은 최선점이며, 표준오차를 확정된 값으로 보지 마세요."));
            if (result.Singular)
                sb.AppendLine(LT("Boundary fit: the random-effects covariance is numerically singular. A variance component is at or near zero; standard errors may be unreliable.",
                                 "경계 적합: 임의효과 공분산이 수치적으로 특이합니다. 분산성분이 0 근처이며 표준오차는 신뢰하기 어렵습니다."));
            if (!result.InformationPositiveDefinite)
                sb.AppendLine(LT("The observed information matrix is not positive definite. Standard errors are omitted.",
                                 "관측 정보행렬이 양의 정부호가 아닙니다. 표준오차는 표시하지 않습니다."));
            sb.AppendLine();

            var table = new TextTable(
                LT("Term", "항"), LT("Estimate", "추정"), "SE", "z", "p",
                LT("95% CI low", "95% CI 하한"), LT("95% CI high", "95% CI 상한"));
            foreach (var c in result.Coefficients)
                table.AddRow(c.Name, StatFormat.G(c.Estimate), StatFormat.G(c.StdError), StatFormat.G(c.Z),
                    StatFormat.P(c.PValue) + StatFormat.Stars(c.PValue), StatFormat.G(c.CiLow), StatFormat.G(c.CiHigh));
            sb.AppendLine(LT("Fixed effects (z and CI use the standard normal, statsmodels MixedLM)",
                             "고정효과 (z·신뢰구간은 표준정규, statsmodels MixedLM)"));
            sb.Append(table.Render());
            sb.AppendLine();

            sb.AppendLine(LT("Random-effects covariance", "임의효과 공분산"));
            var names = result.RandomEffectNames;
            var cov = new TextTable(new[] { "" }.Concat(names).ToArray());
            for (int i = 0; i < names.Count; i++)
            {
                var cells = new string[names.Count + 1];
                cells[0] = names[i];
                for (int j = 0; j < names.Count; j++) cells[j + 1] = StatFormat.G(result.RandomCovariance[i, j]);
                cov.AddRow(cells);
            }
            sb.Append(cov.Render());
            sb.AppendLine(LT($"Residual variance σ² = {StatFormat.G(result.Scale)}",
                             $"잔차 분산 σ² = {StatFormat.G(result.Scale)}"));
            if (result.Icc is double icc)
                sb.AppendLine(LT($"ICC (random intercept) = τ² / (τ² + σ²) = {StatFormat.G(icc)}",
                                 $"ICC (임의 절편) = τ² / (τ² + σ²) = {StatFormat.G(icc)}"));
            else
                sb.AppendLine(LT("ICC is not a single number when a random slope is present (it depends on the covariate).",
                                 "임의 기울기가 있으면 ICC는 하나의 수가 아닙니다(공변량에 따라 다릅니다)."));
            sb.AppendLine();
            sb.AppendLine(LT($"logLik ({(result.Reml ? "REML" : "ML")}) = {StatFormat.G(result.LogLikelihood)}",
                             $"logLik ({(result.Reml ? "REML" : "ML")}) = {StatFormat.G(result.LogLikelihood)}"));
            sb.AppendLine($"AIC = {StatFormat.G(result.Aic)}    BIC = {StatFormat.G(result.Bic)}");
            if (result.Reml)
                sb.AppendLine(LT("AIC/BIC use the ML log-likelihood evaluated at the REML estimates (not a separate ML fit). statsmodels omits AIC/BIC for REML fits.",
                                 "AIC/BIC는 REML 추정값에서 평가한 ML 로그우도를 씁니다(별도의 ML 적합이 아닙니다). statsmodels는 REML 적합의 AIC/BIC를 비웁니다."));
            else
                sb.AppendLine(LT("AIC/BIC follow statsmodels MixedLM: −2·llf + k·{2, log n}, k = fixed effects + covariance parameters + scale.",
                                 "AIC/BIC는 statsmodels MixedLM과 같습니다: −2·llf + k·{2, log n}, k = 고정효과 + 공분산 모수 + 척도."));
            sb.AppendLine(LT("Fixed-effect standard errors are from the observed information of the profile likelihood (variance components treated as estimated), matching statsmodels bse.",
                             "고정효과 표준오차는 프로파일 우도의 관측 정보에서 구하며(분산성분의 추정 불확실성을 포함), statsmodels bse와 같은 정의입니다."));
            return sb.ToString();
        }

        internal static string FormatNlmmResult(string response, string predictor, string group, NonlinearMixedResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(LT("Nonlinear mixed model", "비선형 혼합모형"));
            sb.AppendLine($"{response} ~ {SubstitutePredictor(result.Formula, predictor)}");
            sb.AppendLine(LT($"Mean: {result.Formula}", $"평균: {result.Formula}"));
            sb.AppendLine(LT($"Group: {group}", $"그룹: {group}"));
            var random = result.RandomEffectNames;
            sb.AppendLine(random.Count == 0
                ? LT("Random effects: none (nonlinear least squares)", "임의효과: 없음 (비선형최소제곱)")
                : LT($"Random effects on: {string.Join(", ", random)}", $"임의효과 모수: {string.Join(", ", random)}"));
            sb.AppendLine(AdvScope(result.RowsRead, result.RowsUsed, result.RowsDropped));
            sb.AppendLine(LT($"Groups {StatFormat.Int(result.GroupCount)}    size min {StatFormat.Int(result.MinGroupSize)}    max {StatFormat.Int(result.MaxGroupSize)}",
                             $"그룹 {StatFormat.Int(result.GroupCount)}    크기 최소 {StatFormat.Int(result.MinGroupSize)}    최대 {StatFormat.Int(result.MaxGroupSize)}"));
            sb.AppendLine(result.Converged
                ? LT("Converged.", "수렴했습니다.")
                : LT("Did not converge. Estimates are the best point found.",
                     "수렴하지 않았습니다. 추정값은 지금까지 찾은 최선점입니다."));
            if (result.Singular)
                sb.AppendLine(LT("Boundary fit: the random-effects covariance is numerically singular.",
                                 "경계 적합: 임의효과 공분산이 수치적으로 특이합니다."));
            sb.AppendLine();

            var table = new TextTable(LT("Parameter", "모수"), LT("Estimate", "추정"), "SE", LT("Random", "임의"));
            for (int j = 0; j < result.ParameterNames.Count; j++)
                table.AddRow(result.ParameterNames[j], StatFormat.G(result.Estimates[j]), StatFormat.G(result.StdErrors[j]),
                    result.Random[j] ? LT("yes", "예") : LT("no", "아니오"));
            sb.AppendLine(LT("Mean parameters", "평균 모수"));
            sb.Append(table.Render());
            sb.AppendLine();
            if (random.Count > 0)
            {
                sb.AppendLine(LT("Random-effects covariance", "임의효과 공분산"));
                var cov = new TextTable(new[] { "" }.Concat(random).ToArray());
                for (int i = 0; i < random.Count; i++)
                {
                    var cells = new string[random.Count + 1];
                    cells[0] = random[i];
                    for (int j = 0; j < random.Count; j++) cells[j + 1] = StatFormat.G(result.RandomCovariance[i, j]);
                    cov.AddRow(cells);
                }
                sb.Append(cov.Render());
            }
            sb.AppendLine(LT($"Residual variance σ² = {StatFormat.G(result.Scale)}",
                             $"잔차 분산 σ² = {StatFormat.G(result.Scale)}"));
            sb.AppendLine();
            sb.AppendLine(LT("Standard errors", "표준오차"));
            sb.AppendLine(result.StandardErrorMethod);
            sb.AppendLine(LT("Method: Lindstrom–Bates alternating PNLS/LME when any parameter is random; otherwise ALGLIB nonlinear least squares. There is no local Python reference for this NLMM — recovery is checked by simulation, and the no-random-effect fit is the ALGLIB NLS solution.",
                             "방법: 임의효과가 있으면 Lindstrom–Bates 교대 PNLS/LME, 없으면 ALGLIB 비선형최소제곱. 이 NLMM의 로컬 Python 기준은 없습니다. 회복은 시뮬레이션으로 확인하고, 임의효과가 없는 적합은 ALGLIB NLS 해입니다."));
            return sb.ToString();
        }

        /// <summary>식의 독립 변수 x만 바꾼다. exp 안의 x는 글자 앞뒤 조건으로 남긴다.</summary>
        private static string SubstitutePredictor(string formula, string predictor)
        {
            string body = formula.StartsWith("y = ", StringComparison.Ordinal) ? formula["y = ".Length..] : formula;
            return Regex.Replace(body, @"(?<!\p{L})x(?!\p{L})", _ => predictor);
        }
    }
}
