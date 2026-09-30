using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvPca()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var labels = ColumnLabels();
            using var dlg = new ParamDialog(LT("Principal Component Analysis", "주성분분석(PCA)"), _palette);
            var list = dlg.AddCheckedList(LT("Features", "특성"), labels, Math.Min(12, Math.Max(1, labels.Length)));
            for (int c = 0; c < list.Items.Count; c++)
                if (IsNumericColumn(c)) list.SetItemChecked(c, true);
            var matrix = dlg.AddCombo(LT("Matrix", "행렬"), new[]
            {
                LT("Correlation (standardize, default)", "상관행렬(표준화, 기본)"),
                LT("Covariance", "공분산행렬"),
            });
            var comps = dlg.AddNumeric(LT("Components to show", "표시할 성분 수"), 1, 100, 5);
            dlg.AddNote(LT(
                "Correlation divides each feature by its sample standard deviation (ddof=1), so eigenvalues are those of the correlation matrix. Categorical features are one-hot encoded. Rows with any missing value are excluded.",
                "상관행렬은 각 특성을 표본 표준편차(ddof=1)로 나눈 것입니다. 범주 특성은 원-핫으로 전개되고, 결측이 있는 행은 빠집니다."));
            if (!dlg.ShowOk(this)) return;

            var cols = CheckedIndexes(list);
            if (cols.Count < 2)
            {
                ShowResult(LT("Principal Component Analysis", "주성분분석(PCA)"),
                    LT("Select at least two feature columns.", "특성 컬럼을 둘 이상 선택하세요."));
                return;
            }
            int show = (int)comps.Value;
            bool correlation = matrix.SelectedIndex == 0;
            string title = LT("Principal Component Analysis", "주성분분석(PCA)");
            await RunAdvancedAsync(title, input =>
            {
                var fm = FeatureMatrixBuilder.Build(input.Rows, input.Headers, cols, input.KindOf, null, TargetKind.None, cancellation: input.Cancellation);
                if (fm.FeatureCount < 1)
                    throw new DesignMatrixException("Select at least one feature column.");
                var pca = PrincipalComponents.Fit(fm.X, fm.FeatureNames, correlation ? PcaScale.Correlation : PcaScale.Covariance, show, input.Cancellation);
                return FormatPca(fm, pca);
            });
        }

        private async void AdvLda()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var labels = ColumnLabels();
            if (labels.Length < 2)
            {
                ShowResult(LT("Linear Discriminant Analysis", "선형판별분석(LDA)"),
                    LT("Need a target column and at least one feature.", "목표 컬럼과 특성을 하나 이상 선택하세요."));
                return;
            }
            int targetDefault = AdvDefaultGroupColumn();
            using var dlg = new ParamDialog(LT("Linear Discriminant Analysis", "선형판별분석(LDA)"), _palette);
            var target = dlg.AddCombo(LT("Class column", "클래스 컬럼"), labels, targetDefault);
            var list = dlg.AddCheckedList(LT("Features", "특성"), labels, Math.Min(10, labels.Length));
            for (int c = 0; c < list.Items.Count; c++)
                if (c != targetDefault && IsNumericColumn(c)) list.SetItemChecked(c, true);
            UncheckWhenSelected(target, list);
            var method = dlg.AddCombo(LT("Evaluation", "평가"), new[]
            {
                LT("Stratified holdout", "층화 홀드아웃"),
                LT("Stratified k-fold", "층화 k-겹"),
                LT("Random holdout", "무작위 홀드아웃"),
                LT("Random k-fold", "무작위 k-겹"),
            });
            var testPct = dlg.AddNumeric(LT("Test % (holdout)", "검증 비율 %(홀드아웃)"), 5, 50, 20);
            var folds = dlg.AddNumeric(LT("Folds (k-fold)", "겹 수(k-겹)"), 2, 20, 5);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 0, 999999, 1);
            dlg.AddNote(LT(
                "Pooled within-class covariance, priors from training frequencies. Predictions match sklearn LinearDiscriminantAnalysis (svd). Discriminant directions come from ALGLIB Fisher LDA. Categorical features are one-hot encoded.",
                "합동 급내 공분산, 사전확률은 학습 빈도입니다. 예측은 sklearn 선형판별(svd)과 같고, 판별 방향은 ALGLIB Fisher LDA입니다. 범주 특성은 원-핫입니다."));
            if (!dlg.ShowOk(this)) return;

            int targetCol = target.SelectedIndex;
            var cols = CheckedIndexes(list);
            cols.Remove(targetCol);
            if (cols.Count == 0)
            {
                ShowResult(LT("Linear Discriminant Analysis", "선형판별분석(LDA)"),
                    LT("Select at least one feature column other than the class.", "클래스 이외의 특성을 하나 이상 선택하세요."));
                return;
            }
            if (IsNumericColumn(targetCol))
            {
                ShowResult(LT("Linear Discriminant Analysis", "선형판별분석(LDA)"),
                    LT("The class column must be categorical. A numeric column has too many levels to be a class label.",
                        "클래스 컬럼은 범주형이어야 합니다. 수치 컬럼은 수준이 너무 많아 클래스 라벨로 쓰지 않습니다."));
                return;
            }
            int methodIndex = method.SelectedIndex;
            int pct = (int)testPct.Value;
            int k = (int)folds.Value;
            int seedValue = (int)seed.Value;
            string title = LT("Linear Discriminant Analysis", "선형판별분석(LDA)");
            await RunAdvancedAsync(title, input =>
            {
                var fm = FeatureMatrixBuilder.Build(input.Rows, input.Headers, cols, input.KindOf, targetCol, TargetKind.Categorical, cancellation: input.Cancellation);
                var model = LinearDiscriminant.Fit(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, fm.FeatureNames, fm.ClassNames, cancellation: input.Cancellation);
                bool holdout = methodIndex is 0 or 2;
                bool stratified = methodIndex is 0 or 1;
                var eval = LinearDiscriminant.Evaluate(
                    fm.X, fm.ClassLabels!, fm.ClassNames.Count, fm.ClassNames,
                    holdout ? LdaSplitKind.Holdout : LdaSplitKind.KFold,
                    k, pct / 100.0, seedValue, stratified, cancellation: input.Cancellation);
                string note = AdvSavedNote(fm.RowCount);
                var bundle = ModelBundle.FromFeatures(ModelTypes.Lda, ModelTask.Classification, fm, input.Headers, input.KindOf,
                    input.Headers[targetCol], model, null, fm.RowCount, note);
                return new AdvancedOutput(FormatLda(fm, model, eval, holdout, stratified, k, pct, seedValue) + "\n" + note, bundle);
            });
        }

        private async void AdvFeatureRanking()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            var labels = ColumnLabels();
            if (labels.Length < 2)
            {
                ShowResult(LT("Feature Ranking", "특성 순위"),
                    LT("Need a target column and at least one feature.", "목표 컬럼과 특성을 하나 이상 선택하세요."));
                return;
            }
            int targetDefault = Math.Max(0, labels.Length - 1);
            using var dlg = new ParamDialog(LT("Feature Ranking", "특성 순위"), _palette);
            var target = dlg.AddCombo(LT("Target", "목표"), labels, targetDefault);
            var list = dlg.AddCheckedList(LT("Features", "특성"), labels, Math.Min(10, labels.Length));
            for (int c = 0; c < list.Items.Count; c++)
                if (c != targetDefault && !IsIdentifierColumn(c)) list.SetItemChecked(c, true);
            UncheckWhenSelected(target, list);
            dlg.AddNote(LT(
                "Univariate filter. Numeric target: Pearson r and F-regression. Categorical target: ANOVA F for numeric features, chi-square test of independence for categorical features. Benjamini–Hochberg q-values. Interactions and redundancy are ignored.",
                "단변량 필터입니다. 수치 목표는 Pearson r·F-회귀, 범주 목표는 수치 특성 ANOVA F·범주 특성 독립성 카이제곱입니다. Benjamini–Hochberg q값을 붙입니다. 상호작용과 중복은 보지 않습니다."));
            if (!dlg.ShowOk(this)) return;

            int targetCol = target.SelectedIndex;
            var cols = CheckedIndexes(list);
            cols.Remove(targetCol);
            if (cols.Count == 0)
            {
                ShowResult(LT("Feature Ranking", "특성 순위"),
                    LT("Select at least one feature column other than the target.", "목표 이외의 특성을 하나 이상 선택하세요."));
                return;
            }
            var kind = IsNumericColumn(targetCol) ? TargetKind.Numeric : TargetKind.Categorical;
            string title = LT("Feature Ranking", "특성 순위");
            await RunAdvancedAsync(title, input =>
            {
                var result = FeatureRanking.Rank(input.Rows, input.Headers, cols, input.KindOf, targetCol, kind, cancellation: input.Cancellation);
                return FormatRanking(result);
            });
        }

        private static string FormatPca(FeatureMatrix fm, PcaResult pca)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(fm.RowsRead, fm.RowCount, fm.RowsDropped));
            sb.AppendLine();
            bool corr = pca.Scale == PcaScale.Correlation;
            sb.AppendLine(LT("Principal component analysis  ·  ALGLIB pcabuildbasis (full SVD)",
                              "주성분분석  ·  ALGLIB pcabuildbasis (전체 SVD)"));
            sb.AppendLine(corr
                ? LT("Matrix: correlation — each feature divided by its sample sd (ddof=1). Eigenvalues are those of the correlation matrix and match sklearn PCA explained_variance_ on that standardized data.",
                     "행렬: 상관 — 각 특성을 표본 표준편차(ddof=1)로 나눔. 고유값은 상관행렬의 고유값이며, 그 표준화 데이터에 대한 sklearn PCA explained_variance_와 같습니다.")
                : LT("Matrix: covariance — features centered, not scaled. Eigenvalues = squared singular values / (n−1), matching sklearn PCA explained_variance_.",
                     "행렬: 공분산 — 중심화만 하고 척도를 맞추지 않음. 고유값 = 특이값²/(n−1), sklearn PCA explained_variance_와 같습니다."));
            sb.AppendLine(LT($"Features: {pca.FeatureCount} ({ClipJoin(pca.FeatureNames)})",
                              $"특성: {pca.FeatureCount}개 ({ClipJoin(pca.FeatureNames)})"));
            sb.AppendLine(LT($"Loadings shown: {pca.ComponentCount} of {pca.FeatureCount}" + (pca.RequestedComponents > pca.FeatureCount ? $" (requested {pca.RequestedComponents}, clamped)" : ""),
                              $"적재량 표시: {pca.FeatureCount}개 중 {pca.ComponentCount}개" + (pca.RequestedComponents > pca.FeatureCount ? $" (요청 {pca.RequestedComponents}개를 자름)" : "")));
            if (fm.FeatureNames.Count != fm.SourceColumns.Distinct().Count())
                sb.AppendLine(LT("Categorical features were one-hot encoded (all levels).", "범주 특성은 모든 수준을 원-핫으로 전개했습니다."));
            if (pca.ConstantFeatures > 0)
                sb.AppendLine(LT($"Warning: {pca.ConstantFeatures} feature(s) are constant and contribute no variance.",
                                  $"경고: 상수 특성이 {pca.ConstantFeatures}개라 분산에 기여하지 않습니다."));
            if (pca.RowCount <= pca.FeatureCount)
                sb.AppendLine(LT("Warning: rows ≤ features, so at most n−1 components have nonzero variance after centering.",
                                  "경고: 행 수 ≤ 특성 수라, 중심화 후 분산이 0이 아닌 성분은 최대 n−1개입니다."));
            sb.AppendLine();

            var eig = new TextTable(
                LT("Component", "성분"),
                LT("Eigenvalue", "고유값"),
                LT("Ratio", "비율"),
                LT("Cumulative", "누적"),
                "Kaiser");
            for (int i = 0; i < pca.FeatureCount; i++)
                eig.AddRow("PC" + (i + 1), StatFormat.G(pca.Eigenvalues[i]), StatFormat.F(pca.ExplainedRatio[i], 4),
                    StatFormat.F(pca.CumulativeRatio[i], 4), pca.Eigenvalues[i] > 1 ? LT("yes", "예") : "");
            sb.AppendLine(eig.Render());
            if (corr)
                sb.AppendLine(LT($"Kaiser criterion: keep components with eigenvalue > 1. {pca.KaiserCount} of {pca.FeatureCount} meet it. (This rule is for the correlation matrix.)",
                                  $"Kaiser 기준: 고유값 > 1 인 성분을 유지. {pca.FeatureCount}개 중 {pca.KaiserCount}개. (이 기준은 상관행렬용입니다.)"));
            else
                sb.AppendLine(LT($"Kaiser (eigenvalue > 1) is defined for the correlation matrix, not this covariance scale. {pca.KaiserCount} component(s) exceed 1; {pca.AboveMeanCount} exceed the mean eigenvalue ({StatFormat.G(pca.MeanEigenvalue)}).",
                                  $"Kaiser(고유값 > 1)는 상관행렬 기준이라 이 공분산 척도에는 그대로 쓰지 않습니다. 1을 넘는 성분은 {pca.KaiserCount}개, 평균 고유값({StatFormat.G(pca.MeanEigenvalue)})을 넘는 성분은 {pca.AboveMeanCount}개입니다."));
            sb.AppendLine();

            var headers = new string[pca.ComponentCount + 1];
            headers[0] = LT("Feature", "특성");
            for (int c = 0; c < pca.ComponentCount; c++) headers[c + 1] = "PC" + (c + 1);
            var load = new TextTable(headers);
            for (int j = 0; j < pca.FeatureCount; j++)
            {
                var cells = new string[headers.Length];
                cells[0] = Clip(pca.FeatureNames[j], 22);
                for (int c = 0; c < pca.ComponentCount; c++) cells[c + 1] = StatFormat.F(pca.Loadings[j, c], 4);
                load.AddRow(cells);
            }
            sb.AppendLine(LT("Loadings (sign fixed so the largest |loading| in each component is positive)",
                              "적재량 (각 성분에서 |적재량|이 가장 큰 원소가 양이 되도록 부호 고정)"));
            sb.AppendLine(load.Render());

            sb.AppendLine(LT("Top contributors (|loading|)", "기여가 큰 특성 (|적재량|)"));
            for (int c = 0; c < pca.ComponentCount; c++)
            {
                var order = Enumerable.Range(0, pca.FeatureCount)
                    .OrderByDescending(j => Math.Abs(pca.Loadings[j, c])).ThenBy(j => j)
                    .Take(Math.Min(5, pca.FeatureCount));
                string parts = string.Join(", ", order.Select(j => $"{Clip(pca.FeatureNames[j], 18)} ({StatFormat.F(pca.Loadings[j, c], 3)})"));
                sb.AppendLine($"PC{c + 1}: {parts}");
            }
            sb.AppendLine();
            sb.AppendLine(LT("Components describe variance, not class separation. A large loading is not by itself a significant effect.",
                              "성분은 분산을 설명할 뿐 클래스 분리를 뜻하지 않습니다. 적재량이 크다고 그 자체로 유의한 효과는 아닙니다."));
            return sb.ToString();
        }

        private static string FormatLda(FeatureMatrix fm, LinearDiscriminantModel model, LdaEvaluation eval, bool holdout, bool stratified, int folds, int testPct, int seed)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(fm.RowsRead, fm.RowCount, fm.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(LT("Linear discriminant analysis  ·  pooled within-class covariance, priors = class frequencies",
                              "선형판별분석  ·  합동 급내 공분산, 사전확률 = 클래스 빈도"));
            sb.AppendLine(LT($"Target: {model.ClassNames.Count} classes ({ClipJoin(model.ClassNames)})",
                              $"목표: 클래스 {model.ClassNames.Count}개 ({ClipJoin(model.ClassNames)})"));
            sb.AppendLine(LT($"Features: {model.FeatureCount} ({ClipJoin(model.FeatureNames)})",
                              $"특성: {model.FeatureCount}개 ({ClipJoin(model.FeatureNames)})"));
            sb.AppendLine(LT("Coefficients below are fit on all used rows. Accuracy is from the evaluation splits (refit on training rows only).",
                              "아래 계수는 사용 행 전체에 대한 적합입니다. 정확도는 평가 분할에서 학습 행만으로 다시 적합해 계산했습니다."));
            sb.AppendLine();

            var meanHeaders = new List<string> { LT("Class", "클래스"), "n", LT("Prior", "사전확률") };
            int showF = Math.Min(model.FeatureCount, 6);
            for (int j = 0; j < showF; j++) meanHeaders.Add(Clip(model.FeatureNames[j], 12));
            if (model.FeatureCount > showF) meanHeaders.Add("…");
            var means = new TextTable(meanHeaders.ToArray());
            for (int c = 0; c < model.ClassCount; c++)
            {
                var cells = new List<string> { Clip(model.ClassNames[c], 16), StatFormat.Int(model.ClassCounts[c]), StatFormat.F(model.Priors[c], 3) };
                for (int j = 0; j < showF; j++) cells.Add(StatFormat.G(model.ClassMeans[c, j]));
                if (model.FeatureCount > showF) cells.Add("");
                means.AddRow(cells.ToArray());
            }
            sb.AppendLine(LT("Class means (full sample)", "클래스 평균 (전체 사용 행)"));
            sb.AppendLine(means.Render());

            if (model.UsedFisherDirections && model.DirectionCount > 0)
            {
                sb.AppendLine(LT("Discriminant coefficients — ALGLIB fisherldan, unit norm, largest |coefficient| positive. First min(p, K−1) directions, sorted by separation.",
                                  "판별 계수 — ALGLIB fisherldan, 단위 노름, |계수| 최대 원소가 양. 분리 품질 순으로 min(p, K−1)개."));
                sb.Append(RenderDirections(model));
            }
            else if (model.DirectionCount > 0)
            {
                sb.AppendLine(LT("Fisher LDA directions were not available. Coefficients below are the SVD scalings used for classification (not unit-norm Fisher vectors).",
                                  "Fisher LDA 방향을 구하지 못했습니다. 아래 계수는 분류에 쓴 SVD 스케일링이며, 단위 노름 Fisher 벡터가 아닙니다."));
                sb.Append(RenderDirections(model));
            }
            else
                sb.AppendLine(LT("Discriminant directions were not computed.", "판별 방향을 계산하지 않았습니다."));

            if (model.ExplainedVarianceRatio.Length > 0)
            {
                var ratio = new TextTable(LT("Discriminant", "판별함수"), LT("Variance ratio", "분산 비율"));
                for (int i = 0; i < model.ExplainedVarianceRatio.Length; i++)
                    ratio.AddRow("LD" + (i + 1), StatFormat.F(model.ExplainedVarianceRatio[i], 4));
                sb.AppendLine(LT("Explained variance ratio of discriminants (sklearn svd solver; ratio of between-class singular values, not a class-separation test)",
                                  "판별함수의 설명 분산 비율 (sklearn svd 솔버. 급간 특이값 비율이며 분리 검정은 아닙니다)"));
                sb.AppendLine(ratio.Render());
            }

            if (model.Singular)
                sb.AppendLine(LT($"Warning: within-class covariance is rank-deficient (rank {model.WithinRank} of {model.FeatureCount}). Predictions use the SVD pseudo-inverse (tol={StatFormat.G(model.Tolerance)}), the same truncation as sklearn's svd solver. No ridge penalty was added.",
                                  $"경고: 급내 공분산이 특이합니다 (계수 {model.WithinRank}/{model.FeatureCount}). 예측은 SVD 유사역(tol={StatFormat.G(model.Tolerance)})을 쓰며 sklearn svd 솔버와 같은 절단입니다. 능형 벌점은 더하지 않았습니다."));
            else
                sb.AppendLine(LT($"Within-class covariance rank {model.WithinRank} of {model.FeatureCount} (full).",
                                  $"급내 공분산 계수 {model.WithinRank}/{model.FeatureCount} (완전)."));
            sb.AppendLine();

            string evalName = holdout
                ? LT($"{(stratified ? "Stratified" : "Random")} holdout, test {testPct}%, seed {seed}",
                     $"{(stratified ? "층화" : "무작위")} 홀드아웃, 검증 {testPct}%, 시드 {seed}")
                : LT($"{(stratified ? "Stratified" : "Random")} {folds}-fold, seed {seed}",
                     $"{(stratified ? "층화" : "무작위")} {folds}-겹, 시드 {seed}");
            sb.AppendLine(LT($"Evaluation: {evalName}", $"평가: {evalName}"));
            var summary = new TextTable(LT("Accuracy", "정확도"), LT("Macro F1", "매크로 F1"), LT("Majority baseline", "최빈 기준선"), "n");
            summary.AddRow(StatFormat.F(eval.Metrics.Accuracy, 4), StatFormat.F(eval.Metrics.MacroF1, 4),
                StatFormat.F(eval.MajorityBaselineAccuracy, 4), StatFormat.Int(eval.Metrics.Total));
            sb.AppendLine(summary.Render());
            string majName = model.ClassNames[eval.OverallMajorityClass];
            sb.AppendLine(LT($"Majority baseline predicts, on each test row, the most frequent class in that split's training rows (ties: lowest class index). Overall majority '{majName}' is {StatFormat.F(eval.OverallMajorityProportion, 3)} of used rows.",
                              $"최빈 기준선은 각 검증 행에 그 분할 학습 행의 최빈 클래스를 예측합니다(동률이면 인덱스가 작은 클래스). 전체 최빈 클래스 '{majName}'는 사용 행의 {StatFormat.F(eval.OverallMajorityProportion, 3)}입니다."));
            if (eval.AnySingular)
                sb.AppendLine(LT("At least one training split had a rank-deficient within-class covariance; that split used the same SVD truncation.",
                                  "학습 분할 중 적어도 하나는 급내 공분산이 특이했고, 그 분할도 같은 SVD 절단을 썼습니다."));
            sb.AppendLine();

            var cmHeaders = new List<string> { LT("Actual \\ Pred", "실제 \\ 예측") };
            for (int c = 0; c < model.ClassCount; c++) cmHeaders.Add(Clip(model.ClassNames[c], 12));
            cmHeaders.Add("n");
            var cm = new TextTable(cmHeaders.ToArray());
            for (int a = 0; a < model.ClassCount; a++)
            {
                var cells = new List<string> { Clip(model.ClassNames[a], 16) };
                long rowSum = 0;
                for (int p = 0; p < model.ClassCount; p++)
                {
                    cells.Add(StatFormat.Int(eval.Metrics.Confusion[a, p]));
                    rowSum += eval.Metrics.Confusion[a, p];
                }
                cells.Add(StatFormat.Int(rowSum));
                cm.AddRow(cells.ToArray());
            }
            sb.AppendLine(LT("Confusion matrix (rows = actual, columns = predicted)", "혼동행렬 (행 = 실제, 열 = 예측)"));
            sb.AppendLine(cm.Render());

            var per = new TextTable(LT("Class", "클래스"), LT("Precision", "정밀도"), LT("Recall", "재현율"), "F1", LT("Support", "지지수"));
            for (int c = 0; c < model.ClassCount; c++)
                per.AddRow(Clip(model.ClassNames[c], 16), StatFormat.F(eval.Metrics.Precision[c], 3),
                    StatFormat.F(eval.Metrics.Recall[c], 3), StatFormat.F(eval.Metrics.F1[c], 3), StatFormat.Int(eval.Metrics.Support[c]));
            sb.AppendLine(per.Render());
            sb.AppendLine(LT("Assumes a shared within-class covariance (and, for the probabilities, class-conditional normality). Hard labels are more robust to that assumption than the probabilities. Zero-division precision/recall is 0.",
                              "급내 공분산이 같다고 가정합니다(확률에는 클래스별 정규성도 가정). 하드 라벨이 확률보다 이 가정에 덜 민감합니다. 분모가 0인 정밀도·재현율은 0입니다."));
            return sb.ToString();
        }

        private static string RenderDirections(LinearDiscriminantModel model)
        {
            int show = Math.Min(model.DirectionCount, 6);
            var headers = new string[show + 1];
            headers[0] = LT("Feature", "특성");
            for (int c = 0; c < show; c++) headers[c + 1] = "LD" + (c + 1);
            var table = new TextTable(headers);
            int showF = model.FeatureCount;
            for (int j = 0; j < showF; j++)
            {
                var cells = new string[show + 1];
                cells[0] = Clip(model.FeatureNames[j], 22);
                for (int c = 0; c < show; c++) cells[c + 1] = StatFormat.F(model.Directions[j, c], 4);
                table.AddRow(cells);
            }
            var sb = new StringBuilder();
            sb.AppendLine(table.Render());
            if (model.DirectionCount > show)
                sb.AppendLine(LT($"({model.DirectionCount - show} further direction(s) omitted from the table.)",
                                  $"(표에서 방향 {model.DirectionCount - show}개를 생략했습니다.)"));
            return sb.ToString();
        }

        private static string FormatRanking(FeatureRankingResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(result.RowsRead, result.RowsUsed, result.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(LT($"Univariate feature ranking  ·  target {result.TargetName} ({(result.TargetKind == TargetKind.Numeric ? "numeric" : "categorical")})",
                              $"단변량 특성 순위  ·  목표 {result.TargetName} ({(result.TargetKind == TargetKind.Numeric ? "수치" : "범주")})"));
            if (result.TargetKind == TargetKind.Numeric)
                sb.AppendLine(LT("Numeric features: Pearson r and F-regression (sklearn f_regression, center=True, force_finite=True). F = r²/(1−r²)·(n−2), df = 1, n−2.",
                                  "수치 특성: Pearson r과 F-회귀(sklearn f_regression, center=True, force_finite=True). F = r²/(1−r²)·(n−2), df = 1, n−2."));
            else
                sb.AppendLine(LT("Numeric features: ANOVA F (sklearn f_classif). Categorical features: chi-square test of independence on the level × class table (scipy.stats.chi2_contingency, correction=False). This is not sklearn.feature_selection.chi2, which scores one-hot counts.",
                                  "수치 특성: ANOVA F(sklearn f_classif). 범주 특성: 수준×클래스 분할표의 독립성 카이제곱(scipy.stats.chi2_contingency, correction=False). 원-핫 카운트를 쓰는 sklearn.feature_selection.chi2와는 다릅니다."));
            sb.AppendLine(LT($"q-values: Benjamini–Hochberg (statsmodels multipletests, method='fdr_bh') over {result.TestsInFamily} feature(s) with a defined p-value. Rank is by p, then |score|, then column index.",
                              $"q값: p가 정의된 특성 {result.TestsInFamily}개에 대한 Benjamini–Hochberg(statsmodels multipletests, method='fdr_bh'). 순위는 p, 그다음 |점수|, 그다음 컬럼 인덱스입니다."));
            sb.AppendLine(LT("This filter ignores interactions and redundancy: two copies of the same feature both rank high.",
                              "이 필터는 상호작용과 중복을 무시합니다. 같은 특성의 복사본 둘 다 상위에 올 수 있습니다."));
            if (result.SkippedCategorical.Count > 0)
                sb.AppendLine(LT($"Not scored (categorical features need a categorical target for the chi-square test): {ClipJoin(result.SkippedCategorical)}.",
                                  $"점수 없음(범주 특성의 카이제곱은 범주 목표가 필요): {ClipJoin(result.SkippedCategorical)}."));
            sb.AppendLine();

            var table = new TextTable(LT("Rank", "순위"), LT("Feature", "특성"), LT("Test", "검정"), LT("Score", "점수"),
                LT("Statistic", "통계량"), "df", "p", "q");
            int sig = 0;
            for (int i = 0; i < result.Ranked.Count; i++)
            {
                var f = result.Ranked[i];
                if (!double.IsNaN(f.QValue) && f.QValue < 0.05) sig++;
                table.AddRow(
                    (i + 1).ToString(),
                    Clip(f.Name, 16),
                    TestName(f.Test),
                    ScoreText(f),
                    StatText(f),
                    DfText(f),
                    StatFormat.P(f.PValue) + StatFormat.Stars(f.PValue),
                    StatFormat.P(f.QValue));
            }
            sb.AppendLine(table.Render());
            sb.AppendLine(LT($"{sig} feature(s) have q < 0.05. Stars on p: * <.05, ** <.01, *** <.001.",
                              $"q < 0.05 인 특성 {sig}개. p의 별: * <.05, ** <.01, *** <.001."));

            bool lowExp = false, constant = false, perfect = false, single = false;
            foreach (var f in result.Ranked)
            {
                if (f.Note == RankNote.LowExpected) lowExp = true;
                if (f.Note == RankNote.Constant) constant = true;
                if (f.Note is RankNote.PerfectCorrelation or RankNote.PerfectSeparation) perfect = true;
                if (f.Note == RankNote.SingleLevel) single = true;
            }
            if (constant)
                sb.AppendLine(LT("Constant numeric features: correlation/ANOVA is undefined. F-regression reports F=0, p=1 (sklearn force_finite). ANOVA reports F and p as undefined (—).",
                                  "상수 수치 특성: 상관/ANOVA가 정의되지 않습니다. F-회귀는 F=0, p=1(sklearn force_finite). ANOVA는 F와 p를 정의되지 않음(—)으로 둡니다."));
            if (perfect)
                sb.AppendLine(LT("A feature is perfectly associated with the target (F = ∞, p = 0). That is a real fit, not a failed one, but it can be a data leak or a transformed copy of the target.",
                                  "특성이 목표와 완전히 연관되어 있습니다(F = ∞, p = 0). 실패한 적합이 아니라 실제 결과이지만, 누수이거나 목표의 변환 복사본일 수 있습니다."));
            if (single)
                sb.AppendLine(LT("A categorical feature has only one level in the complete rows, so the chi-square test is undefined (reported as 0, p = 1) and is still in the q-value family.",
                                  "범주 특성이 사용 행에서 수준이 하나뿐이라 카이제곱이 정의되지 않습니다(0, p = 1로 보고). q값 가족에는 포함됩니다."));
            if (lowExp)
                sb.AppendLine(LT("At least one chi-square table has an expected count below 5. The asymptotic p-value can be a poor approximation; no continuity correction was applied.",
                                  "카이제곱 표 중 기대빈도가 5 미만인 칸이 있습니다. 점근 p값이 부정확할 수 있습니다. 연속성 보정은 하지 않았습니다."));
            if (result.TargetKind == TargetKind.Categorical)
                sb.AppendLine(LT("ANOVA assumes normality and equal variance within classes. The chi-square test does not, but it only sees category frequencies.",
                                  "ANOVA는 클래스 안 정규성과 등분산을 가정합니다. 카이제곱은 그렇지 않지만 범주 빈도만 봅니다."));
            return sb.ToString();
        }

        private static string TestName(RankingTest test) => test switch
        {
            RankingTest.PearsonF => "Pearson",
            RankingTest.AnovaF => "ANOVA F",
            _ => LT("Chi-square", "카이제곱"),
        };

        private static string ScoreText(RankedFeature f) => f.Test switch
        {
            RankingTest.PearsonF => "r=" + StatFormat.G(f.Score),
            RankingTest.AnovaF => "F=" + StatFormat.G(f.Score),
            _ => "χ²=" + StatFormat.G(f.Score),
        };

        private static string StatText(RankedFeature f)
            => f.Test == RankingTest.PearsonF ? "F=" + StatFormat.G(f.Statistic) : StatFormat.G(f.Statistic);

        private static string DfText(RankedFeature f)
            => f.Test == RankingTest.ChiSquare ? f.Df1.ToString() : $"{f.Df1}, {f.Df2}";

        private static string Clip(string s, int n)
            => s.Length <= n ? s : s[..(n - 1)] + "…";

        private static string ClipJoin(IReadOnlyList<string> names, int max = 8)
        {
            if (names.Count <= max) return string.Join(", ", names);
            return string.Join(", ", names.Take(max)) + $" … (+{names.Count - max})";
        }
    }
}
