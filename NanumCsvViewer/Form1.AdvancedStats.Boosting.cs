using System.Globalization;
using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvGradientBoosting()
        {
            if (!TryBoostingDialog(out var features, out var target, out var numericTarget, out var options)) return;
            string title = LT("Gradient Boosting", "그래디언트 부스팅");
            await RunAdvancedAsync(title, input =>
            {
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, numericTarget ? TargetKind.Numeric : TargetKind.Categorical, cancellation: input.Cancellation);
                if (numericTarget)
                {
                    var report = GradientBoosting.Evaluate(matrix.X, matrix.NumericTarget!, options, input.Cancellation);
                    return FormatBoosting(matrix, report, null);
                }
                var cls = GradientBoosting.Evaluate(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, options, input.Cancellation);
                return FormatBoosting(matrix, cls, matrix.ClassNames);
            });
        }

        private bool TryBoostingDialog(out List<int> features, out int target, out bool numericTarget, out GradientBoostingOptions options)
        {
            features = new List<int>();
            target = 0;
            numericTarget = false;
            options = new GradientBoostingOptions();
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return false;
            string title = LT("Gradient Boosting", "그래디언트 부스팅");
            if (_doc.ColumnCount == 0)
            {
                ShowResult(title, LT("This file has no columns.", "이 파일에는 컬럼이 없습니다."));
                return false;
            }

            int targetDefault = AdvDefaultGroupColumn();
            using var dlg = new ParamDialog(title, _palette);
            var list = dlg.AddCheckedList(LT("Feature columns", "특성 컬럼"), ColumnLabels(), Math.Min(6, _doc.ColumnCount));
            for (int c = 0; c < list.Items.Count; c++)
                if (IsNumericColumn(c) && c != targetDefault) list.SetItemChecked(c, true);
            var targetBox = dlg.AddCombo(LT("Target", "목표"), ColumnLabels(), targetDefault);
            UncheckWhenSelected(targetBox, list);
            var rate = dlg.AddText(LT("Learning rate", "학습률"), "0.1");
            var iters = dlg.AddNumeric(LT("Max iterations", "최대 반복"), 1, 2000, 100);
            var depth = dlg.AddNumeric(LT("Max depth (0 = none)", "최대 깊이(0 = 없음)"), 0, 64, 0);
            var leaves = dlg.AddNumeric(LT("Max leaves (0 = none)", "최대 잎 수(0 = 없음)"), 0, 4096, 31);
            var minLeaf = dlg.AddNumeric(LT("Min samples / leaf", "잎 최소 표본"), 1, 1_000_000, 20);
            var l2 = dlg.AddText(LT("L2 regularization", "L2 규제"), "0");
            var bins = dlg.AddNumeric(LT("Max bins", "최대 구간 수"), 2, 255, 255);
            var early = dlg.AddCombo(LT("Early stopping", "조기 종료"), new[] { LT("No", "아니오"), LT("Yes", "예") }, 0);
            var valPct = dlg.AddNumeric(LT("Validation %", "검증 비율 %"), 5, 50, 10);
            var noChange = dlg.AddNumeric(LT("Rounds w/o improvement", "개선 없는 반복"), 1, 100, 10);
            var scheme = dlg.AddCombo(LT("Evaluation", "평가"), new[]
            {
                LT("Holdout (stratified if class)", "홀드아웃(클래스는 층화)"),
                LT("K-fold (stratified if class)", "k-겹(클래스는 층화)"),
            }, 0);
            var pct = dlg.AddNumeric(LT("Test % (holdout)", "시험 비율 %(홀드아웃)"), 5, 50, 30);
            var folds = dlg.AddNumeric(LT("Folds (k-fold)", "겹 수(k-겹)"), 2, 10, 5);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 1, 1_000_000_000, 1);
            dlg.AddNote(LT(
                "Histogram gradient boosting (LightGBM / XGBoost-style, sklearn HistGradientBoosting). A numeric target is squared-error regression; any other target is log-loss classification. Categorical features are one-hot encoded, then binned as numeric — native categorical splits are not used. AdaBoost, CatBoost, and NGBoost are not provided. Quantile edges use all training rows up to 200,000; larger fits use a seeded subsample of 200,000 rows for the edges only. Early stopping keeps the trailing non-improving trees (it does not roll back).",
                "히스토그램 그래디언트 부스팅(LightGBM / XGBoost 계열, sklearn HistGradientBoosting)입니다. 수치 목표는 제곱오차 회귀, 그 외는 로그손실 분류입니다. 범주 특성은 원-핫 뒤 수치처럼 구간화합니다. 범주 전용 분할은 쓰지 않습니다. AdaBoost, CatBoost, NGBoost는 제공하지 않습니다. 분위 경계는 학습 행 20만 행까지 전부 쓰고, 더 크면 경계만 시드 고정 20만 행 표본입니다. 조기 종료는 개선이 멈춘 뒤의 트리도 유지합니다(최고 반복으로 되돌리지 않음)."));
            if (!dlg.ShowOk(this)) return false;

            features = CheckedIndexes(list);
            target = targetBox.SelectedIndex;
            if (features.Count == 0)
            {
                ShowResult(title, LT("Select at least one feature column.", "특성 컬럼을 하나 이상 선택하세요."));
                return false;
            }
            if (target < 0 || features.Contains(target))
            {
                ShowResult(title, LT("The target column cannot also be a feature.", "목표 컬럼은 특성으로 쓸 수 없습니다."));
                return false;
            }
            if (!TryBoostDouble(rate.Text, out double learningRate) || !(learningRate > 0))
            {
                ShowResult(title, LT("Learning rate must be a finite number greater than 0.", "학습률은 0보다 큰 유한한 수여야 합니다."));
                return false;
            }
            if (!TryBoostDouble(l2.Text, out double l2Value) || l2Value < 0)
            {
                ShowResult(title, LT("L2 regularization must be a finite number ≥ 0.", "L2 규제는 0 이상의 유한한 수여야 합니다."));
                return false;
            }
            int maxLeaves = (int)leaves.Value;
            if (maxLeaves == 1)
            {
                ShowResult(title, LT("Max leaves must be 0 (no limit) or at least 2.", "최대 잎 수는 0(제한 없음) 또는 2 이상이어야 합니다."));
                return false;
            }
            numericTarget = IsNumericColumn(target);
            options = new GradientBoostingOptions
            {
                LearningRate = learningRate,
                MaxIterations = (int)iters.Value,
                MaxDepth = (int)depth.Value,
                MaxLeafNodes = maxLeaves,
                MinSamplesLeaf = (int)minLeaf.Value,
                L2Regularization = l2Value,
                MaxBins = (int)bins.Value,
                EarlyStopping = early.SelectedIndex == 1,
                ValidationFraction = (double)valPct.Value / 100.0,
                IterationsNoChange = (int)noChange.Value,
                Scheme = scheme.SelectedIndex == 1 ? EvalScheme.KFold : EvalScheme.Holdout,
                TestFraction = (double)pct.Value / 100.0,
                Folds = (int)folds.Value,
                Seed = (int)seed.Value,
            };
            return true;
        }

        private static bool TryBoostDouble(string text, out double value)
            => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

        private string FormatBoosting(FeatureMatrix matrix, GradientBoostingReport report, IReadOnlyList<string>? classNames)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(LT(
                "Method: histogram gradient boosting (sklearn HistGradientBoosting semantics; LightGBM / XGBoost-style). Best-first trees, quantile bins, Newton step with L2 on the leaf. Not AdaBoost, CatBoost, or NGBoost.",
                "방법: 히스토그램 그래디언트 부스팅(sklearn HistGradientBoosting 의미, LightGBM / XGBoost 계열). 잎 우선 성장, 분위 구간, 잎의 L2 뉴턴 단계. AdaBoost·CatBoost·NGBoost가 아닙니다."));
            sb.AppendLine(report.Loss switch
            {
                BoostingLoss.HalfSquaredError => LT(
                    "Loss: mean half squared error, 0.5·(F−y)² (sklearn HalfSquaredError). Prediction is the identity link.",
                    "손실: 평균 반제곱오차 0.5·(F−y)² (sklearn HalfSquaredError). 예측은 항등 링크입니다."),
                BoostingLoss.HalfBinomial => LT(
                    "Loss: mean half binomial deviance (log-loss). Raw score is log-odds; class 1 if raw > 0. Positive class is the second sorted level.",
                    "손실: 평균 반이항 이탈도(로그손실). 원시 점수는 로그오즈이고, raw > 0이면 클래스 1입니다. 양성 클래스는 정렬상 두 번째 수준입니다."),
                _ => LT(
                    "Loss: mean categorical cross-entropy. One tree per class per iteration (diagonal Hessian). Class is argmax; ties take the lowest index.",
                    "손실: 평균 범주 교차엔트로피. 반복마다 클래스당 트리 하나(대각 헤시안). 클래스는 argmax이고, 동점이면 가장 작은 인덱스입니다."),
            });
            string depth = report.MaxDepth == 0 ? LT("none", "없음") : report.MaxDepth.ToString(CultureInfo.InvariantCulture);
            string leaves = report.MaxLeafNodes == 0 ? LT("none", "없음") : report.MaxLeafNodes.ToString(CultureInfo.InvariantCulture);
            sb.AppendLine(LT(
                $"Learning rate {StatFormat.G(report.LearningRate)}, max iterations {report.MaxIterations}, max depth {depth}, max leaves {leaves}, min samples/leaf {report.MinSamplesLeaf}, L2 {StatFormat.G(report.L2Regularization)}, max bins {report.MaxBins}.",
                $"학습률 {StatFormat.G(report.LearningRate)}, 최대 반복 {report.MaxIterations}, 최대 깊이 {depth}, 최대 잎 {leaves}, 잎 최소 표본 {report.MinSamplesLeaf}, L2 {StatFormat.G(report.L2Regularization)}, 최대 구간 {report.MaxBins}."));
            sb.AppendLine(LT(
                $"Split constraints fixed to sklearn: min sum of hessians to split = {GradientBoostingModel.MinHessianToSplit.ToString("G", CultureInfo.InvariantCulture)}, min gain to split = 0. Gradients are accumulated from float32 values.",
                $"분할 제약은 sklearn과 같이 고정: 분할에 필요한 헤시안 합 최소 = {GradientBoostingModel.MinHessianToSplit.ToString("G", CultureInfo.InvariantCulture)}, 최소 이득 = 0. 기울기는 float32로 캐스팅한 뒤 합산합니다."));
            if (report.BinEdgesSubsampled)
                sb.AppendLine(LT(
                    $"Bin edges: quantile subsample of {report.BinEdgeRows:N0} training rows (seed {report.Seed}, cap {GradientBoostingModel.BinEdgeSubsample:N0}). Edges are not bit-identical to sklearn's RandomState draw.",
                    $"구간 경계: 학습 행 {report.BinEdgeRows:N0}개의 분위 표본(시드 {report.Seed}, 상한 {GradientBoostingModel.BinEdgeSubsample:N0}). sklearn RandomState 추출과 비트가 같지는 않습니다."));
            else
                sb.AppendLine(LT(
                    "Bin edges: quantiles (or midpoints of unique values, if fewer than max bins) on every row used to grow trees.",
                    "구간 경계: 트리를 키운 행 전체의 분위수(고유값이 최대 구간보다 적으면 인접 고유값의 중점)."));
            if (report.EarlyStopping)
                sb.AppendLine(LT(
                    $"Early stopping: validation fraction {report.ValidationFraction.ToString("0.00", CultureInfo.InvariantCulture)} of the training split, seed {report.Seed}, stop when the last {report.IterationsNoChange} validation losses are not better than the reference by more than {StatFormat.G(report.EarlyStoppingTolerance)}. Trailing trees are kept; the fit is not rolled back to the best round.",
                    $"조기 종료: 학습 분할의 {report.ValidationFraction.ToString("0.00", CultureInfo.InvariantCulture)}를 검증으로 떼고(시드 {report.Seed}), 최근 {report.IterationsNoChange}회 검증 손실이 기준보다 {StatFormat.G(report.EarlyStoppingTolerance)}를 넘겨 개선되지 않으면 멈춥니다. 그 뒤의 트리도 유지하며, 최고 반복으로 되돌리지 않습니다."));
            else
                sb.AppendLine(LT("Early stopping: off. Every requested iteration is fit on the training split.",
                    "조기 종료: 끔. 요청한 반복을 학습 분할 전체에 적합합니다."));
            sb.AppendLine(report.Scheme == EvalScheme.Holdout
                ? LT($"Evaluation: {(report.Task == BoostingTask.Regression ? "random" : "stratified")} holdout, test fraction {report.TestFraction.ToString("0.00", CultureInfo.InvariantCulture)}, seed {report.Seed}. Train {report.TrainRows:N0} · test {report.TestRows:N0}.",
                     $"평가: {(report.Task == BoostingTask.Regression ? "무작위" : "층화")} 홀드아웃, 시험 비율 {report.TestFraction.ToString("0.00", CultureInfo.InvariantCulture)}, 시드 {report.Seed}. 학습 {report.TrainRows:N0} · 시험 {report.TestRows:N0}.")
                : LT($"Evaluation: {(report.Task == BoostingTask.Regression ? "random" : "stratified")} {report.Folds}-fold, seed {report.Seed}. Each of {report.TestRows:N0} rows is predicted once.",
                     $"평가: {(report.Task == BoostingTask.Regression ? "무작위" : "층화")} {report.Folds}-겹, 시드 {report.Seed}. {report.TestRows:N0}행을 각각 한 번씩 예측했습니다."));
            sb.AppendLine(LT(
                $"Rows used to grow trees: {report.RowsFit:N0}. Early-stopping validation rows: {report.RowsValidation:N0}. The loss curve is on those grow rows (and the validation holdout), not on the evaluation test rows.",
                $"트리를 키운 행: {report.RowsFit:N0}. 조기 종료 검증 행: {report.RowsValidation:N0}. 손실 곡선은 그 성장 행(과 검증 홀드아웃)의 것이고, 평가 시험 행의 것이 아닙니다."));
            sb.AppendLine(LT($"Features: {FeatureList(matrix.FeatureNames)}", $"특성: {FeatureList(matrix.FeatureNames)}"));
            if (classNames != null)
                sb.AppendLine(LT($"Classes: {FeatureList(classNames)}", $"클래스: {FeatureList(classNames)}"));
            sb.AppendLine();

            if (report.Regression is { } reg)
            {
                sb.AppendLine($"{LT("RMSE", "RMSE")}  {StatFormat.G(reg.Rmse)}");
                sb.AppendLine($"{LT("MAE", "MAE")}  {StatFormat.G(reg.Mae)}");
                sb.AppendLine($"{LT("R²", "R²")}  {StatFormat.F(reg.RSquared)}");
                if (report.RegressionBaseline is { } b)
                {
                    sb.AppendLine(LT(
                        $"Intercept baseline RMSE {StatFormat.G(b.Rmse)} · MAE {StatFormat.G(b.Mae)} · R² {StatFormat.F(b.RSquared)} (constant = mean of rows used to grow trees, scored on the same evaluation rows).",
                        $"절편 기준선 RMSE {StatFormat.G(b.Rmse)} · MAE {StatFormat.G(b.Mae)} · R² {StatFormat.F(b.RSquared)} (상수 = 트리를 키운 행의 평균, 같은 평가 행에서 계산)."));
                }
            }
            else if (report.Classification is { } cls)
            {
                sb.AppendLine($"{LT("Accuracy", "정확도")}  {StatFormat.F(cls.Accuracy)}");
                sb.AppendLine($"{LT("Macro F1", "매크로 F1")}  {StatFormat.F(cls.MacroF1)}");
                sb.AppendLine(LT($"Majority-class baseline (evaluation rows)  {StatFormat.F(report.MajorityBaseline)}",
                    $"최다 클래스 기준선(평가 행)  {StatFormat.F(report.MajorityBaseline)}"));
            }
            sb.AppendLine();

            AppendBoostIterations(sb, report);
            AppendBoostCurve(sb, report);
            AppendBoostImportance(sb, matrix.FeatureNames, report);
            if (report.Classification is { } cm && classNames != null)
                AppendBoostConfusion(sb, classNames, cm);

            var warnings = new List<string>();
            if (report.TestRows < 30)
                warnings.Add(LT("Small evaluation set (fewer than 30 predicted rows). Metrics are unstable.",
                    "평가 집합이 작습니다(예측 30행 미만). 지표가 불안정할 수 있습니다."));
            if (report.Regression is { } r && report.RegressionBaseline is { } rb && r.Rmse >= rb.Rmse - 1e-12)
                warnings.Add(LT("RMSE does not beat the intercept baseline. The fit is not evidence of useful prediction.",
                    "RMSE가 절편 기준선을 이기지 못했습니다. 이 적합이 유용한 예측이라는 증거가 되지 않습니다."));
            if (report.Classification is { } c && c.Accuracy <= report.MajorityBaseline + 1e-12)
                warnings.Add(LT("Accuracy does not beat the majority-class baseline. The fit is not evidence of useful discrimination.",
                    "정확도가 최다 클래스 기준선을 넘지 않습니다. 이 적합이 유용한 구분 능력이 있다는 증거가 되지 않습니다."));
            if (report.TotalGain == 0)
                warnings.Add(LT("No split was accepted. Predictions are the intercept only (baseline).",
                    "받아들여진 분할이 없습니다. 예측은 절편(기준선)뿐입니다."));
            if (report.EarlyStopped)
                warnings.Add(LT("Early stopping kept iterations after the best validation loss. Read the best-validation index; predictions use every kept tree.",
                    "조기 종료가 최고 검증 손실 이후의 반복도 유지했습니다. 최고 검증 인덱스를 보고, 예측은 유지된 트리를 모두 사용합니다."));
            if (report.BinEdgesSubsampled)
                warnings.Add(LT("Bin edges were estimated on a subsample. Two runs with the same seed match each other; they are not a claim about every row's quantile.",
                    "구간 경계는 표본으로 추정했습니다. 같은 시드의 두 실행은 서로 일치하지만, 모든 행의 분위수라는 뜻은 아닙니다."));
            AppendWarnings(sb, warnings);
            return sb.ToString();
        }

        private static void AppendBoostIterations(StringBuilder sb, GradientBoostingReport report)
        {
            if (report.Scheme == EvalScheme.Holdout)
            {
                sb.AppendLine(report.EarlyStopped
                    ? LT($"Iterations used: {report.IterationsUsed} of {report.MaxIterations} (early stopping).",
                         $"사용한 반복: {report.IterationsUsed} / {report.MaxIterations} (조기 종료).")
                    : LT($"Iterations used: {report.IterationsUsed} (no early stop).",
                         $"사용한 반복: {report.IterationsUsed} (조기 종료 없음)."));
                if (report.BestValidationIndex >= 0)
                    sb.AppendLine(LT(
                        $"Best validation loss at curve index {report.BestValidationIndex} (0 = intercept, before any tree).",
                        $"최고 검증 손실은 곡선 인덱스 {report.BestValidationIndex} (0 = 절편, 트리 전)."));
                return;
            }
            sb.AppendLine(LT("Iterations by fold (trees kept; early stopping does not roll back):",
                "겹별 반복(유지된 트리 수, 조기 종료는 되돌리지 않음):"));
            var table = new TextTable(LT("Fold", "겹"), LT("Iterations", "반복"), LT("Stopped", "종료"), LT("Final train loss", "최종 학습 손실"));
            for (int i = 0; i < report.FoldIterations.Length; i++)
                table.AddRow((i + 1).ToString(CultureInfo.InvariantCulture), report.FoldIterations[i].ToString(CultureInfo.InvariantCulture),
                    report.FoldEarlyStopped[i] ? LT("yes", "예") : LT("no", "아니오"), StatFormat.G(report.FoldFinalTrainLoss[i]));
            sb.Append(table.Render());
            sb.AppendLine();
        }

        private static void AppendBoostCurve(StringBuilder sb, GradientBoostingReport report)
        {
            if (report.TrainLoss == null)
            {
                sb.AppendLine(LT("Loss curve omitted: folds stopped at different iterations, so a single curve would mix lengths.",
                    "손실 곡선 생략: 겹마다 멈춘 반복이 달라 한 곡선으로 길이를 섞지 않습니다."));
                sb.AppendLine();
                return;
            }
            sb.AppendLine(report.LossCurveIsFoldMean
                ? LT("Mean training loss across folds (same length). Lower is better. Index 0 is the intercept.",
                     "겹 평균 학습 손실(길이가 같음). 낮을수록 좋습니다. 인덱스 0은 절편입니다.")
                : LT("Training loss of the holdout model (rows used to grow trees). Lower is better. Index 0 is the intercept.",
                     "홀드아웃 모델의 학습 손실(트리를 키운 행). 낮을수록 좋습니다. 인덱스 0은 절편입니다."));
            var loss = report.TrainLoss;
            var val = report.ValidationLoss;
            int n = loss.Length;
            var headers = val == null
                ? new[] { LT("Iter", "반복"), LT("Train loss", "학습 손실") }
                : new[] { LT("Iter", "반복"), LT("Train loss", "학습 손실"), LT("Validation loss", "검증 손실") };
            var curve = new TextTable(headers);
            void Row(int i)
            {
                if (val == null) curve.AddRow(i.ToString(CultureInfo.InvariantCulture), StatFormat.G(loss[i]));
                else curve.AddRow(i.ToString(CultureInfo.InvariantCulture), StatFormat.G(loss[i]), i < val.Length ? StatFormat.G(val[i]) : "—");
            }
            if (n <= 40)
                for (int i = 0; i < n; i++) Row(i);
            else
            {
                for (int i = 0; i < 15; i++) Row(i);
                if (val == null) curve.AddRow("…", "…");
                else curve.AddRow("…", "…", "…");
                for (int i = n - 10; i < n; i++) Row(i);
            }
            sb.Append(curve.Render());
            if (n > 40)
                sb.AppendLine(LT($"Curve has {n} points; middle rows omitted.", $"곡선은 {n}점입니다. 가운데 행은 생략했습니다."));
            sb.AppendLine();
        }

        private static void AppendBoostImportance(StringBuilder sb, IReadOnlyList<string> names, GradientBoostingReport report)
        {
            sb.AppendLine(report.ImportanceAveragedAcrossFolds
                ? LT("Feature importance: mean of fold-wise split-gain sums, then normalized to 1. Gain-based, not permutation importance. A k-fold report is not one deployed model.",
                     "특성 중요도: 겹별 분할 이득 합의 평균을 1로 정규화. 이득 기준이며 순열 중요도가 아닙니다. k-겹 보고서는 배포용 단일 모델이 아닙니다.")
                : LT("Feature importance: sum of split gains, normalized to 1. Gain-based, not permutation importance.",
                     "특성 중요도: 분할 이득의 합을 1로 정규화. 이득 기준이며 순열 중요도가 아닙니다."));
            sb.AppendLine(LT($"Total gain {StatFormat.G(report.TotalGain)}.", $"이득 합 {StatFormat.G(report.TotalGain)}."));
            int show = Math.Min(names.Count, 40);
            var order = Enumerable.Range(0, report.FeatureImportance.Length).OrderByDescending(i => report.FeatureImportance[i]).ThenBy(i => i).Take(show).ToArray();
            var table = new TextTable(LT("Feature", "특성"), LT("Importance", "중요도"));
            for (int i = 0; i < order.Length; i++)
            {
                int j = order[i];
                table.AddRow(j < names.Count ? names[j] : j.ToString(CultureInfo.InvariantCulture), StatFormat.F(report.FeatureImportance[j], 4));
            }
            sb.Append(table.Render());
            if (names.Count > show)
                sb.AppendLine(LT($"… {names.Count - show} more features omitted.", $"… 특성 {names.Count - show}개 생략."));
            sb.AppendLine();
        }

        private static void AppendBoostConfusion(StringBuilder sb, IReadOnlyList<string> names, ClassificationMetrics m)
        {
            if (names.Count == 0 || names.Count > 8)
            {
                sb.AppendLine(LT("Confusion matrix omitted (no classes, or more than 8). Rows would be actual, columns predicted.",
                    "클래스가 없거나 8개를 넘어 혼동행렬은 생략합니다. 행은 실제, 열은 예측입니다."));
                sb.AppendLine();
                return;
            }
            sb.AppendLine(LT("Confusion matrix (rows = actual, columns = predicted)", "혼동행렬 (행 = 실제, 열 = 예측)"));
            var headers = new string[names.Count + 1];
            headers[0] = LT("Actual", "실제");
            for (int c = 0; c < names.Count; c++) headers[c + 1] = ShortName(names[c]);
            var cm = new TextTable(headers);
            for (int a = 0; a < names.Count; a++)
            {
                var cells = new string[names.Count + 1];
                cells[0] = ShortName(names[a]);
                for (int p = 0; p < names.Count; p++) cells[p + 1] = StatFormat.Int(m.Confusion[a, p]);
                cm.AddRow(cells);
            }
            sb.Append(cm.Render());
            sb.AppendLine();
        }
    }
}
