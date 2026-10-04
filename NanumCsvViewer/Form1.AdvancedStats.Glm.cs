using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvGlm()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var headers = AdvHeaders();
            int yCol = FirstNumericColumn();
            string initial = headers.Length == 0 ? "" : FormulaText.QuoteName(headers[yCol]) + " ~ ";
            using var dlg = new FormulaDialog(
                LT("General Linear Model (GLM)", "일반선형모형(GLM)"),
                headers, ColumnLabels(), _palette, initial,
                LT("Formula: response ~ predictors. Wrap a factor in C(). Example: y ~ x + C(group).",
                   "식: 반응 ~ 설명변수. 요인은 C()로 감쌉니다. 예: y ~ x + C(group)."),
                f => GlmResponseError(headers, f, IsNumericColumn));
            if (!dlg.ShowOk(this)) return;

            var formula = dlg.Parsed!;
            string title = LT("General Linear Model (GLM)", "일반선형모형(GLM)");
            await RunAdvancedAsync(title, input =>
            {
                LinearModel.EnsureNumericResponse(input.Headers, formula, input.KindOf);
                var dm = DesignMatrixBuilder.Build(input.Rows, input.Headers, formula, input.KindOf, cancellation: input.Cancellation);
                var fit = LinearModel.Fit(dm, input.Cancellation);
                var anova = LinearModel.TypeII(dm, fit, input.Cancellation);
                string note = AdvSavedNote(dm.RowCount);
                var bundle = ModelBundle.FromFormula(ModelTypes.LinearModel, ModelTask.Regression, dm, fit, note);
                return new AdvancedOutput(FormatGlmResult(dm, fit, anova) + "\n" + note, bundle);
            });
        }

        private async void AdvAncova()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("ANCOVA", "공분산분석(ANCOVA)"), _palette);
            var dep = dlg.AddCombo(LT("Dependent (numeric)", "종속변수(수치)"), ColumnLabels(), FirstNumericColumn());
            var factorList = dlg.AddCheckedList(LT("Factors (categorical, one or more)", "요인(범주, 하나 이상)"), ColumnLabels(), Math.Min(6, Math.Max(1, _doc.ColumnCount)));
            int factorDefault = Enumerable.Range(0, _doc.ColumnCount).FirstOrDefault(c => !IsNumericColumn(c) && c != dep.SelectedIndex);
            if (factorDefault < factorList.Items.Count) factorList.SetItemChecked(factorDefault, true);
            var cov = dlg.AddCheckedList(LT("Covariates (numeric)", "공변량(수치)"), ColumnLabels(), Math.Min(8, Math.Max(1, _doc.ColumnCount)));
            for (int c = 0; c < cov.Items.Count; c++)
                if (IsNumericColumn(c) && c != dep.SelectedIndex && !factorList.GetItemChecked(c)) cov.SetItemChecked(c, true);
            UncheckWhenSelected(dep, cov);
            UncheckWhenSelected(dep, factorList);
            factorList.ItemCheck += (_, e) =>
            {
                if (e.NewValue == CheckState.Checked && e.Index < cov.Items.Count) cov.SetItemChecked(e.Index, false);
            };
            var interactions = dlg.AddCombo(LT("Factor interactions (several factors)", "요인 상호작용(요인 2개 이상)"), new[]
            {
                LT("None (main effects)", "없음(주효과만)"),
                LT("All 2-way", "2차 상호작용 모두"),
                LT("All orders", "모든 차수"),
            }, 0);
            dlg.AddNote(LT(
                "Adjusted means are estimated at each covariate's mean; with several factors the others are averaged with equal level weights (interaction columns included). With interactions, Type II respects marginality and a cell (level combination) adjusted-mean table is added per interaction. Homogeneity of slopes adds factor×covariate interactions (per factor and overall).",
                "보정 평균은 각 공변량의 평균에서 추정하며, 요인이 여럿이면 다른 요인은 수준 동일 가중으로 평균합니다(상호작용 열 포함). 상호작용을 넣으면 Type II는 주변성을 지키고 상호작용마다 셀(수준 조합) 보정 평균표가 추가됩니다. 기울기 동질성은 요인×공변량 상호작용을 넣어 검정합니다(요인별·전체)."));
            if (!dlg.ShowOk(this)) return;
            int interactionOrder = interactions.SelectedIndex;

            int depCol = dep.SelectedIndex;
            var factorCols = CheckedIndexes(factorList);
            var covCols = CheckedIndexes(cov);
            string title = LT("ANCOVA", "공분산분석(ANCOVA)");
            if (!IsNumericColumn(depCol))
            {
                ShowResult(title, LT("The dependent variable must be numeric.", "종속변수는 수치형이어야 합니다."));
                return;
            }
            if (factorCols.Count == 0)
            {
                ShowResult(title, LT("Select at least one factor.", "요인을 하나 이상 선택하세요."));
                return;
            }
            if (factorCols.Contains(depCol))
            {
                ShowResult(title, LT("A factor must be a different column from the dependent variable.", "요인은 종속변수와 다른 열이어야 합니다."));
                return;
            }
            if (covCols.Count == 0)
            {
                ShowResult(title, LT("Select at least one numeric covariate.", "수치 공변량을 하나 이상 선택하세요."));
                return;
            }
            if (covCols.Contains(depCol) || covCols.Any(factorCols.Contains))
            {
                ShowResult(title, LT("A covariate cannot also be the dependent variable or the factor.", "공변량은 종속변수·요인과 같을 수 없습니다."));
                return;
            }
            foreach (int c in covCols)
            {
                if (!IsNumericColumn(c))
                {
                    ShowResult(title, LT("Every covariate must be numeric.", "공변량은 모두 수치형이어야 합니다."));
                    return;
                }
            }

            var headers = AdvHeaders();
            var factorNames = factorCols.Select(c => headers[c]).ToHashSet(StringComparer.Ordinal);
            var predictors = new List<string>(factorCols.Select(c => headers[c]));
            predictors.AddRange(covCols.Select(c => headers[c]));
            string formulaText = FormulaText.MainEffects(headers[depCol], predictors, name => factorNames.Contains(name));
            if (interactionOrder > 0 && factorCols.Count >= 2)
            {
                var fnames = factorCols.Select(c => FormulaText.QuoteName(headers[c])).Select(q => $"C({q})").ToList();
                int maxOrder = interactionOrder == 1 ? 2 : fnames.Count;
                var extra = new List<string>();
                for (int mask = 1; mask < (1 << fnames.Count); mask++)
                {
                    int bits = System.Numerics.BitOperations.PopCount((uint)mask);
                    if (bits < 2 || bits > maxOrder) continue;
                    extra.Add(string.Join(":", Enumerable.Range(0, fnames.Count).Where(k => (mask & (1 << k)) != 0).Select(k => fnames[k])));
                }
                formulaText += " + " + string.Join(" + ", extra);
            }
            var formula = ModelFormula.Parse(formulaText);
            await RunAdvancedAsync(title, input =>
            {
                LinearModel.EnsureNumericResponse(input.Headers, formula, input.KindOf);
                var dm = DesignMatrixBuilder.Build(input.Rows, input.Headers, formula, input.KindOf, cancellation: input.Cancellation);
                if (factorNames.Count == 1)
                    return FormatAncovaResult(dm, LinearModel.Ancova(dm, factorNames.First(), input.Cancellation));
                return FormatMultiAncovaResult(dm, LinearModel.AncovaMulti(dm, input.Cancellation));
            });
        }

        /// <summary>식 대화상자 추가 검증: 반응이 수치 열이 아니면 안내 문구.</summary>
        private static string? GlmResponseError(IReadOnlyList<string> headers, ModelFormula formula, Func<int, bool> isNumeric)
        {
            int col = StatValue.ResolveColumn(headers, formula.Response);
            if (!isNumeric(col))
                return LT($"Response '{formula.Response}' must be numeric.",
                          $"반응변수 '{formula.Response}'는 수치형이어야 합니다.");
            return null;
        }

        internal static string FormatGlmResult(DesignMatrix dm, LinearModelFit fit, IReadOnlyList<AnovaTerm> anova)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(dm.RowsRead, dm.RowCount, dm.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(dm.Formula.ToString());
            sb.AppendLine(LT("OLS · treatment (dummy) coding · Type II SS (statsmodels anova_lm typ=2)",
                             "OLS · 처리(더미) 코딩 · Type II 제곱합(statsmodels anova_lm typ=2)"));
            sb.AppendLine();
            AppendFitSummary(sb, fit);
            sb.AppendLine();
            sb.AppendLine(LT("Coefficients", "계수"));
            sb.Append(FormatCoefficients(fit));
            sb.AppendLine();
            sb.AppendLine(LT("Type II ANOVA  (F against the full-model residual mean square)",
                             "Type II 분산분석  (F의 분모는 완전모형 잔차 평균제곱)"));
            sb.Append(FormatAnova(anova, fit, partialEta: false));
            sb.AppendLine();
            AppendResidualBlock(sb, fit);
            AppendNotes(sb, fit, anova);
            return sb.ToString();
        }

        internal static string FormatAncovaResult(DesignMatrix dm, AncovaResult result)
        {
            var fit = result.Additive;
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(dm.RowsRead, dm.RowCount, dm.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(dm.Formula.ToString());
            sb.AppendLine(LT("ANCOVA · treatment coding · Type II SS · adjusted means at covariate means",
                             "공분산분석 · 처리 코딩 · Type II 제곱합 · 공변량 평균에서의 보정 평균"));
            sb.AppendLine();
            AppendFitSummary(sb, fit);
            sb.AppendLine();
            sb.AppendLine(LT("Type II ANOVA  (partial η² = SS / (SS + residual SS))",
                             "Type II 분산분석  (부분 η² = SS / (SS + 잔차 SS))"));
            sb.Append(FormatAnova(result.TypeII, fit, partialEta: true));
            sb.AppendLine();
            sb.AppendLine(LT("Homogeneity of regression slopes", "회귀 기울기 동질성"));
            sb.AppendLine(LT("Nested F: the full model adds every factor×covariate interaction. Denominator is the full-model residual mean square.",
                             "내포 F: 완전모형은 요인×공변량 상호작용을 모두 넣습니다. 분모는 완전모형 잔차 평균제곱입니다."));
            var slopes = result.Slopes;
            sb.AppendLine($"F({StatFormat.Int(slopes.DfNumerator)}, {StatFormat.Int(slopes.DfDenominator)}) = {StatFormat.G(slopes.F)}    p {StatFormat.P(slopes.PValue)}{StatFormat.Stars(slopes.PValue)}");
            sb.AppendLine(LT($"SS difference {StatFormat.G(slopes.RssReduced - slopes.RssFull)}    RSS additive {StatFormat.G(slopes.RssReduced)}    RSS full {StatFormat.G(slopes.RssFull)}",
                             $"제곱합 차이 {StatFormat.G(slopes.RssReduced - slopes.RssFull)}    가법 RSS {StatFormat.G(slopes.RssReduced)}    완전 RSS {StatFormat.G(slopes.RssFull)}"));
            if (double.IsNaN(slopes.PValue))
                sb.AppendLine(LT("The slopes test is not defined (the interactions added no estimable columns, or residual df is 0).",
                                 "기울기 검정이 정의되지 않습니다(상호작용이 추정 가능한 열을 더하지 않았거나 잔차 자유도가 0)."));
            else if (slopes.PValue < 0.05)
                sb.AppendLine(LT("The parallel-slopes assumption is rejected (p < 0.05). Adjusted means are still reported from the additive model, but that model is misspecified if slopes differ.",
                                 "평행 기울기 가정이 기각됩니다(p < 0.05). 보정 평균은 가법 모형에서 그대로 보고하지만, 기울기가 다르면 그 모형은 부적합합니다."));
            else
                sb.AppendLine(LT("The parallel-slopes assumption is not rejected (p ≥ 0.05).",
                                 "평행 기울기 가정을 기각하지 않습니다(p ≥ 0.05)."));

            sb.AppendLine();
            sb.AppendLine(LT("Covariate means (evaluation point of adjusted means)", "공변량 평균(보정 평균의 평가 지점)"));
            for (int i = 0; i < result.Covariates.Count; i++)
                sb.AppendLine($"  {result.Covariates[i]} = {StatFormat.G(result.CovariateMeans[i])}");

            sb.AppendLine();
            sb.AppendLine(LT("Adjusted (estimated marginal) means", "보정(추정 주변) 평균"));
            var means = new TextTable(
                LT("Level", "수준"), "n",
                LT("Estimate", "추정값"), "SE",
                LT("95% low", "95% 하한"), LT("95% high", "95% 상한"));
            foreach (var m in result.AdjustedMeans)
                means.AddRow(m.Level, StatFormat.Int(m.Count), StatFormat.G(m.Estimate), StatFormat.G(m.StdError), StatFormat.G(m.CiLow), StatFormat.G(m.CiHigh));
            sb.Append(means.Render());

            sb.AppendLine();
            int comparisons = result.Pairwise.Count;
            sb.AppendLine(LT($"Pairwise differences of adjusted means (B − A), Bonferroni over {comparisons:N0} pairs",
                             $"보정 평균의 쌍별 차이(B − A), {comparisons:N0}쌍 Bonferroni 보정"));
            var pairs = new TextTable("A", "B", LT("B − A", "B − A"), "SE", "t", "p", LT("p Bonf.", "p Bonf."));
            foreach (var d in result.Pairwise)
                pairs.AddRow(d.LevelA, d.LevelB, StatFormat.G(d.Difference), StatFormat.G(d.StdError), StatFormat.G(d.T),
                    StatFormat.P(d.PValue), StatFormat.P(d.BonferroniP));
            sb.Append(pairs.Render());
            sb.AppendLine();
            AppendResidualBlock(sb, fit);
            AppendNotes(sb, fit, result.TypeII);
            return sb.ToString();
        }

        internal static string FormatMultiAncovaResult(DesignMatrix dm, MultiAncovaResult result)
        {
            var fit = result.Additive;
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(dm.RowsRead, dm.RowCount, dm.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(dm.Formula.ToString());
            sb.AppendLine(result.Cells.Count > 0
                ? LT("Multi-factor ANCOVA · with factor interactions · treatment coding · Type II SS (marginality) · adjusted means at covariate means",
                     "다요인 공분산분석 · 요인 상호작용 포함 · 처리 코딩 · Type II 제곱합(주변성) · 공변량 평균에서의 보정 평균")
                : LT("Multi-factor ANCOVA · main effects · treatment coding · Type II SS · adjusted means at covariate means",
                     "다요인 공분산분석 · 주효과 · 처리 코딩 · Type II 제곱합 · 공변량 평균에서의 보정 평균"));
            sb.AppendLine();
            AppendFitSummary(sb, fit);
            sb.AppendLine();
            sb.AppendLine(LT("Type II ANOVA  (partial η² = SS / (SS + residual SS))",
                             "Type II 분산분석  (부분 η² = SS / (SS + 잔차 SS))"));
            sb.Append(FormatAnova(result.TypeII, fit, partialEta: true));
            sb.AppendLine();
            sb.AppendLine(LT("Homogeneity of regression slopes", "회귀 기울기 동질성"));
            sb.AppendLine(LT("Nested F against the additive model; each row adds that factor×covariate interactions. Denominator is the full-model residual mean square.",
                             "가법 모형 대비 내포 F. 각 행은 해당 요인×공변량 상호작용을 더합니다. 분모는 완전모형 잔차 평균제곱입니다."));
            var slopeTable = new TextTable(LT("Added interactions", "추가한 상호작용"), "F", "df", "p", " ");
            foreach (var f in result.Factors)
                slopeTable.AddRow(f.Factor + "×" + LT("covariates", "공변량"), StatFormat.G(f.Slopes.F),
                    $"{StatFormat.Int(f.Slopes.DfNumerator)}, {StatFormat.Int(f.Slopes.DfDenominator)}",
                    StatFormat.P(f.Slopes.PValue), StatFormat.Stars(f.Slopes.PValue));
            slopeTable.AddRow(LT("all factors", "모든 요인"), StatFormat.G(result.AllSlopes.F),
                $"{StatFormat.Int(result.AllSlopes.DfNumerator)}, {StatFormat.Int(result.AllSlopes.DfDenominator)}",
                StatFormat.P(result.AllSlopes.PValue), StatFormat.Stars(result.AllSlopes.PValue));
            sb.Append(slopeTable.Render());
            if (result.Factors.Any(f => f.Slopes.PValue < 0.05) || result.AllSlopes.PValue < 0.05)
                sb.AppendLine(LT("At least one parallel-slopes test is rejected (p < 0.05). Adjusted means come from the additive model, which is misspecified if slopes differ.",
                                 "평행 기울기 검정이 하나 이상 기각됩니다(p < 0.05). 보정 평균은 가법 모형에서 구하며, 기울기가 다르면 그 모형은 부적합합니다."));
            else
                sb.AppendLine(LT("No parallel-slopes test is rejected (p ≥ 0.05).", "평행 기울기 검정을 기각하지 않습니다(p ≥ 0.05)."));

            sb.AppendLine();
            sb.AppendLine(LT("Covariate means (evaluation point of adjusted means)", "공변량 평균(보정 평균의 평가 지점)"));
            for (int i = 0; i < result.Covariates.Count; i++)
                sb.AppendLine($"  {result.Covariates[i]} = {StatFormat.G(result.CovariateMeans[i])}");

            foreach (var f in result.Factors)
            {
                sb.AppendLine();
                sb.AppendLine(LT($"Adjusted (estimated marginal) means — {f.Factor}  (other factors averaged with equal level weights)",
                                 $"보정(추정 주변) 평균 — {f.Factor}  (다른 요인은 수준 동일 가중 평균)"));
                var means = new TextTable(LT("Level", "수준"), "n", LT("Estimate", "추정값"), "SE", LT("95% low", "95% 하한"), LT("95% high", "95% 상한"));
                foreach (var m in f.AdjustedMeans)
                    means.AddRow(m.Level, StatFormat.Int(m.Count), StatFormat.G(m.Estimate), StatFormat.G(m.StdError), StatFormat.G(m.CiLow), StatFormat.G(m.CiHigh));
                sb.Append(means.Render());
                sb.AppendLine();
                sb.AppendLine(LT($"Pairwise differences of adjusted means — {f.Factor} (B − A), Bonferroni over {f.Pairwise.Count:N0} pairs",
                                 $"보정 평균의 쌍별 차이 — {f.Factor} (B − A), {f.Pairwise.Count:N0}쌍 Bonferroni 보정"));
                var pairs = new TextTable("A", "B", "B − A", "SE", "t", "p", LT("p Bonf.", "p Bonf."));
                foreach (var d in f.Pairwise)
                    pairs.AddRow(d.LevelA, d.LevelB, StatFormat.G(d.Difference), StatFormat.G(d.StdError), StatFormat.G(d.T),
                        StatFormat.P(d.PValue), StatFormat.P(d.BonferroniP));
                sb.Append(pairs.Render());
            }
            if (result.Cells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(LT("Note: with interactions, the main-effect adjusted means and pairwise differences above are averaged over the other factors' levels (equal weights); the effect of a factor can differ between levels of the others — read the cell means below and the interaction rows of the Type II table.",
                                 "참고: 상호작용이 있으면 위의 주효과 보정 평균·쌍별 차이는 다른 요인 수준에 걸친 평균(동일 가중)입니다. 한 요인의 효과가 다른 요인의 수준마다 다를 수 있으니 아래 셀 평균과 Type II 표의 상호작용 행을 함께 보세요."));
                foreach (var cell in result.Cells)
                {
                    sb.AppendLine();
                    sb.AppendLine(LT($"Cell adjusted means — {cell.Term}  (at covariate means; other factors averaged with equal weights; empty cells are not estimable)",
                                     $"셀 보정 평균 — {cell.Term}  (공변량 평균에서, 다른 요인은 동일 가중 평균, 빈 셀은 추정 불가)"));
                    var ct = new TextTable(LT("Cell", "셀"), "n", LT("Estimate", "추정값"), "SE", LT("95% low", "95% 하한"), LT("95% high", "95% 상한"));
                    foreach (var m in cell.AdjustedMeans)
                        ct.AddRow(m.Level, StatFormat.Int(m.Count), StatFormat.G(m.Estimate), StatFormat.G(m.StdError), StatFormat.G(m.CiLow), StatFormat.G(m.CiHigh));
                    sb.Append(ct.Render());
                }
            }
            sb.AppendLine();
            AppendResidualBlock(sb, fit);
            AppendNotes(sb, fit, result.TypeII);
            return sb.ToString();
        }

        private static void AppendFitSummary(StringBuilder sb, LinearModelFit fit)
        {
            sb.AppendLine($"n = {StatFormat.Int(fit.N)}    rank = {StatFormat.Int(fit.Rank)}    " +
                          LT($"df model = {StatFormat.Int(fit.DfModel)}    df residual = {StatFormat.Int(fit.DfResidual)}",
                             $"모형 자유도 = {StatFormat.Int(fit.DfModel)}    잔차 자유도 = {StatFormat.Int(fit.DfResidual)}"));
            sb.AppendLine($"R² = {StatFormat.G(fit.RSquared)}    adj R² = {StatFormat.G(fit.AdjustedRSquared)}    " +
                          LT($"residual SE = {StatFormat.G(fit.ResidualSe)}    RSS = {StatFormat.G(fit.Rss)}",
                             $"잔차 SE = {StatFormat.G(fit.ResidualSe)}    RSS = {StatFormat.G(fit.Rss)}"));
            sb.AppendLine($"F({StatFormat.Int(fit.DfModel)}, {StatFormat.Int(fit.DfResidual)}) = {StatFormat.G(fit.FStatistic)}    p {StatFormat.P(fit.FPValue)}{StatFormat.Stars(fit.FPValue)}");
            sb.AppendLine($"logLik = {StatFormat.G(fit.LogLikelihood)}    AIC = {StatFormat.G(fit.Aic)}    BIC = {StatFormat.G(fit.Bic)}");
            if (!fit.HasIntercept)
                sb.AppendLine(LT("No intercept: R² uses the uncentered total sum of squares (statsmodels).",
                                 "절편 없음: R²는 비중심 총제곱합을 씁니다(statsmodels)."));
        }

        private static string FormatCoefficients(LinearModelFit fit)
        {
            var table = new TextTable(
                LT("Term", "항"), LT("Estimate", "추정값"), "SE", "t", "p", " ",
                LT("95% low", "95% 하한"), LT("95% high", "95% 상한"));
            foreach (var c in fit.Coefficients)
            {
                if (c.Aliased)
                    table.AddRow(c.Name, LT("aliased", "별칭"), "—", "—", "—", "", "—", "—");
                else
                    table.AddRow(c.Name, StatFormat.G(c.Estimate), StatFormat.G(c.StdError), StatFormat.G(c.T),
                        StatFormat.P(c.PValue), StatFormat.Stars(c.PValue), StatFormat.G(c.CiLow), StatFormat.G(c.CiHigh));
            }
            return table.Render();
        }

        private static string FormatAnova(IReadOnlyList<AnovaTerm> terms, LinearModelFit fit, bool partialEta)
        {
            TextTable table = partialEta
                ? new TextTable(LT("Term", "항"), "SS", "df", "MS", "F", "p", " ", "η²p")
                : new TextTable(LT("Term", "항"), "SS", "df", "MS", "F", "p", " ");
            foreach (var t in terms)
            {
                if (partialEta)
                    table.AddRow(t.Name, StatFormat.G(t.SumOfSquares), StatFormat.Int(t.Df), StatFormat.G(t.MeanSquare),
                        StatFormat.G(t.F), StatFormat.P(t.PValue), StatFormat.Stars(t.PValue), StatFormat.G(t.PartialEtaSquared));
                else
                    table.AddRow(t.Name, StatFormat.G(t.SumOfSquares), StatFormat.Int(t.Df), StatFormat.G(t.MeanSquare),
                        StatFormat.G(t.F), StatFormat.P(t.PValue), StatFormat.Stars(t.PValue));
            }
            if (partialEta)
                table.AddRow(LT("Residual", "잔차"), StatFormat.G(fit.Rss), StatFormat.Int(fit.DfResidual),
                    StatFormat.G(fit.Sigma2), "—", "—", "", "—");
            else
                table.AddRow(LT("Residual", "잔차"), StatFormat.G(fit.Rss), StatFormat.Int(fit.DfResidual),
                    StatFormat.G(fit.Sigma2), "—", "—", "");
            return table.Render();
        }

        private static void AppendResidualBlock(StringBuilder sb, LinearModelFit fit)
        {
            var r = fit.Residuals;
            sb.AppendLine(LT("Residuals", "잔차"));
            var table = new TextTable(LT("Min", "최소"), "Q1", LT("Median", "중앙값"), "Q3", LT("Max", "최대"), LT("Mean", "평균"));
            table.AddRow(StatFormat.G(r.Min), StatFormat.G(r.Q1), StatFormat.G(r.Median), StatFormat.G(r.Q3), StatFormat.G(r.Max), StatFormat.G(r.Mean));
            sb.Append(table.Render());
            sb.AppendLine();
            if (fit.Normality is { } sw)
            {
                string cap = sw.Capped
                    ? LT($" (first {sw.SampleSize:N0} residuals; Royston approximation, labelled approximate)",
                         $" (앞 {sw.SampleSize:N0}개 잔차, Royston 근사 — 근사로 표기)")
                    : LT($" (n = {sw.SampleSize:N0})", $" (n = {sw.SampleSize:N0})");
                sb.AppendLine(LT($"Shapiro–Wilk on residuals{cap}", $"잔차 Shapiro–Wilk{cap}"));
                sb.AppendLine($"W = {StatFormat.G(sw.W)}    p {StatFormat.P(sw.PValue)}{StatFormat.Stars(sw.PValue)}");
                if (sw.PValue < 0.05)
                    sb.AppendLine(LT("Residual normality is questionable (p < 0.05). t and F p-values are approximate if n is small.",
                                     "잔차 정규성이 의심됩니다(p < 0.05). n이 작으면 t·F p값은 근사입니다."));
            }
            else
                sb.AppendLine(LT("Shapiro–Wilk is undefined (fewer than 3 residuals, or residuals are constant).",
                                 "Shapiro–Wilk를 계산할 수 없습니다(잔차가 3개 미만이거나 상수)."));
        }

        private static void AppendNotes(StringBuilder sb, LinearModelFit fit, IReadOnlyList<AnovaTerm> anova)
        {
            sb.AppendLine();
            sb.AppendLine(LT("Notes", "참고"));
            if (fit.DfResidual == 0)
                sb.AppendLine(LT("- Residual degrees of freedom are 0. Standard errors, t tests, and the residual scale are not defined. Point estimates are still reported.",
                                 "- 잔차 자유도가 0입니다. 표준오차·t검정·잔차 분산은 정의되지 않습니다. 점추정만 보고합니다."));
            else if (fit.DfResidual < 10)
                sb.AppendLine(LT($"- Small residual df ({fit.DfResidual}). Standard errors and p-values are unstable.",
                                 $"- 잔차 자유도가 작습니다({fit.DfResidual}). 표준오차와 p값이 불안정합니다."));
            var aliased = fit.Coefficients.Where(c => c.Aliased).Select(c => c.Name).ToList();
            if (aliased.Count > 0)
                sb.AppendLine(LT($"- Aliased (not estimable, dropped like R): {string.Join(", ", aliased)}.",
                                 $"- 별칭(추정 불가, R처럼 제외): {string.Join(", ", aliased)}."));
            var inestimable = anova.Where(t => t.Df == 0).Select(t => t.Name).ToList();
            if (inestimable.Count > 0)
                sb.AppendLine(LT($"- Type II df is 0 (not estimable given the other terms): {string.Join(", ", inestimable)}.",
                                 $"- Type II 자유도가 0입니다(다른 항이 주어지면 추정 불가): {string.Join(", ", inestimable)}."));
            if (fit.Rss == 0)
                sb.AppendLine(LT("- The fit is exact (RSS = 0). The Gaussian log-likelihood is infinite.",
                                 "- 적합이 정확합니다(RSS = 0). 정규 로그우도는 무한대입니다."));
            if (fit.DfModel == 0)
                sb.AppendLine(LT("- The model has no explanatory degrees of freedom, so the overall F test is undefined.",
                                 "- 설명 자유도가 없어 전체 F검정은 정의되지 않습니다."));
            if (aliased.Count == 0 && fit.DfResidual >= 10 && fit.Rss > 0 && inestimable.Count == 0 && fit.DfModel > 0)
                sb.AppendLine(LT("- No aliased terms.", "- 별칭 항 없음."));
        }
    }
}
