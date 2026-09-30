using System.Globalization;
using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvKMeans()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            string title = LT("K-means Clustering", "K-means 군집");
            if (_doc.ColumnCount == 0) { ShowResult(title, LT("This file has no columns.", "이 파일에는 컬럼이 없습니다.")); return; }

            using var dlg = new ParamDialog(title, _palette);
            var list = dlg.AddCheckedList(LT("Feature columns", "특성 컬럼"), ColumnLabels(), Math.Min(8, _doc.ColumnCount));
            for (int c = 0; c < list.Items.Count; c++)
                if (IsNumericColumn(c)) list.SetItemChecked(c, true);
            var scaling = dlg.AddCombo(LT("Scaling", "스케일링"), ScalingChoices(), 1);
            var kBox = dlg.AddNumeric(LT("Clusters (k)", "군집 수(k)"), 2, 20, 3);
            var restarts = dlg.AddNumeric(LT("Restarts", "재시작"), 1, 20, 5);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 1, 1_000_000_000, 1);
            var elbow = dlg.AddCombo(LT("Elbow table", "엘보 표"), new[]
            {
                LT("No", "아니오"),
                LT("Yes (k = 2..K)", "예 (k = 2..K)"),
            }, 0);
            dlg.AddNote(LT(
                "Categorical columns are one-hot encoded. Missing rows are dropped. Silhouette uses a fixed-seed sample of at most 5,000 rows. An elbow table refits k = 2..K and can be slow on large views.",
                "범주 컬럼은 원-핫으로 펼칩니다. 결측 행은 제외됩니다. 실루엣은 시드 고정 표본 최대 5,000행입니다. 엘보 표는 k = 2..K를 다시 적합하므로 큰 보기에서는 느릴 수 있습니다."));
            if (!dlg.ShowOk(this)) return;

            var features = CheckedIndexes(list);
            if (features.Count == 0)
            {
                ShowResult(title, LT("Select at least one feature column.", "특성 컬럼을 하나 이상 선택하세요."));
                return;
            }
            var opt = new KMeansOptions
            {
                K = (int)kBox.Value,
                Restarts = (int)restarts.Value,
                Seed = (int)seed.Value,
                Scaling = ScalingOf(scaling.SelectedIndex),
                Elbow = elbow.SelectedIndex == 1,
            };
            await RunAdvancedAsync(title, input =>
            {
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    null, TargetKind.None, cancellation: input.Cancellation);
                var result = KMeansClustering.Fit(matrix.X, opt, matrix.FeatureNames, input.Cancellation);
                return FormatKMeans(matrix, result);
            });
        }

        private async void AdvKnn()
        {
            if (!TryClassificationDialog(LT("K-Nearest Neighbors", "K-최근접 이웃"), knn: true,
                    out var features, out var target, out var options)) return;
            string title = LT("K-Nearest Neighbors", "K-최근접 이웃");
            await RunAdvancedAsync(title, input =>
            {
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, TargetKind.Categorical, cancellation: input.Cancellation);
                var eval = KnnClassifier.Evaluate(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, options, input.Cancellation);
                return FormatClassification(matrix, eval, options, knn: true, model: null, allRows: false, scaler: null);
            });
        }

        private async void AdvNaiveBayes()
        {
            if (!TryClassificationDialog(LT("Naive Bayes", "나이브 베이즈"), knn: false,
                    out var features, out var target, out var options)) return;
            string title = LT("Naive Bayes", "나이브 베이즈");
            await RunAdvancedAsync(title, input =>
            {
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, TargetKind.Categorical, cancellation: input.Cancellation);
                var groups = FeatureGroups.FromMatrix(matrix.SourceColumns, matrix.FeatureNames);
                var fit = NaiveBayesClassifier.Evaluate(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, groups, options, input.Cancellation);
                return FormatClassification(matrix, fit.Evaluation, options, knn: false, fit.Parameters, fit.ParametersUseAllRows, fit.ParameterScaler);
            });
        }

        // 분류 대화상자. 실패(취소·검증)면 false. 컨트롤 값은 반환 전에 복사한다.
        private bool TryClassificationDialog(string title, bool knn,
            out List<int> features, out int target, out ClassifierOptions options)
        {
            features = new List<int>();
            target = 0;
            options = new ClassifierOptions();
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return false;
            if (_doc.ColumnCount == 0)
            {
                ShowResult(title, LT("This file has no columns.", "이 파일에는 컬럼이 없습니다."));
                return false;
            }

            int targetDefault = AdvDefaultGroupColumn();

            using var dlg = new ParamDialog(title, _palette);
            var list = dlg.AddCheckedList(LT("Feature columns", "특성 컬럼"), ColumnLabels(), Math.Min(8, _doc.ColumnCount));
            for (int c = 0; c < list.Items.Count; c++)
                if (IsNumericColumn(c) && c != targetDefault) list.SetItemChecked(c, true);
            var targetBox = dlg.AddCombo(LT("Target (class)", "목표(클래스)"), ColumnLabels(), targetDefault);
            UncheckWhenSelected(targetBox, list);
            var scaling = dlg.AddCombo(LT("Scaling", "스케일링"), ScalingChoices(), 1);
            NumericUpDown? kBox = null;
            if (knn) kBox = dlg.AddNumeric(LT("Neighbors (k)", "이웃 수(k)"), 1, 100, 5);
            var scheme = dlg.AddCombo(LT("Evaluation", "평가"), new[]
            {
                LT("Stratified holdout", "층화 홀드아웃"),
                LT("Stratified k-fold", "층화 k-겹"),
            }, 0);
            var pct = dlg.AddNumeric(LT("Test % (holdout)", "시험 비율 %(홀드아웃)"), 5, 50, 30);
            var folds = dlg.AddNumeric(LT("Folds (k-fold)", "겹 수(k-겹)"), 2, 10, 5);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 1, 1_000_000_000, 1);
            dlg.AddNote(knn
                ? LT("Scaling is fit on training rows only. Categorical columns are one-hot encoded. Evaluation is stratified.",
                     "스케일링은 학습 행으로만 적합합니다. 범주 컬럼은 원-핫으로 펼칩니다. 평가는 층화입니다.")
                : LT("Numeric features use a Gaussian likelihood (var_smoothing 1e-9). Categorical features use Laplace α = 1. Scaling is fit on training rows only and applied to numeric features.",
                     "수치 특성은 가우시안 우도(var_smoothing 1e-9), 범주 특성은 라플라스 α = 1입니다. 스케일링은 학습 행으로만 적합하고 수치 특성에만 적용합니다."));
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
            options = new ClassifierOptions
            {
                Scheme = scheme.SelectedIndex == 1 ? EvalScheme.KFold : EvalScheme.Holdout,
                TestFraction = (double)pct.Value / 100.0,
                Folds = (int)folds.Value,
                Seed = (int)seed.Value,
                Scaling = ScalingOf(scaling.SelectedIndex),
                Neighbors = kBox is null ? 5 : (int)kBox.Value,
            };
            return true;
        }

        private string FormatKMeans(FeatureMatrix matrix, KMeansResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(LT(
                $"Method: Lloyd K-means, k-means++ initialization, Euclidean distance, seed {result.Seed}, {result.Restarts} restart(s).",
                $"방법: Lloyd K-means, k-means++ 초기화, 유클리드 거리, 시드 {result.Seed}, 재시작 {result.Restarts}회."));
            if (result.InitializationSampleRows > 0)
                sb.AppendLine(LT(
                    $"Large data: ALGLIB k-means++ ({result.Iterations} iteration(s) across restarts) on a fixed-seed sample of {result.InitializationSampleRows:N0} rows found the starting centers; Lloyd iterations on all {matrix.RowCount:N0} rows refined them ({result.RefinementIterations} iteration(s), {(result.RefinementConverged ? "converged" : "iteration limit reached")}).",
                    $"대용량: 시드 고정 표본 {result.InitializationSampleRows:N0}행에서 ALGLIB k-means++(재시작 합계 {result.Iterations}회)로 시작 중심을 찾고, 전체 {matrix.RowCount:N0}행 Lloyd 반복으로 정제했습니다({result.RefinementIterations}회, {(result.RefinementConverged ? "수렴" : "반복 한도 도달")})."));
            else
                sb.AppendLine(LT(
                    $"ALGLIB termination {result.TerminationType} (success), {result.Iterations} iteration(s) across restarts.",
                    $"ALGLIB 종료코드 {result.TerminationType}(성공), 재시작 합계 반복 {result.Iterations}회."));
            sb.AppendLine(LT($"Scaling: {ScalingLabel(result.Scaling)} (fit on all used rows).",
                $"스케일링: {ScalingLabel(result.Scaling)} (사용한 행 전체로 적합)."));
            sb.AppendLine(LT($"Features: {FeatureList(result.FeatureNames)}", $"특성: {FeatureList(result.FeatureNames)}"));
            sb.AppendLine(LT($"k = {result.Clusters.Length}", $"k = {result.Clusters.Length}"));
            sb.AppendLine();

            string space = result.Scaling == ScalingMethod.None
                ? LT("original units", "원래 단위")
                : LT("scaled space", "스케일된 공간");
            sb.AppendLine(LT($"Cluster sizes and within-cluster SS ({space})", $"군집 크기와 군집 내 SS ({space})"));
            var sizes = new TextTable(LT("Cluster", "군집"), LT("Size", "크기"), LT("Within SS", "군집 내 SS"));
            foreach (var c in result.Clusters)
                sizes.AddRow(ClusterName(c.Index), StatFormat.Int(c.Size), StatFormat.G(c.WithinSs));
            sb.Append(sizes.Render());
            sb.AppendLine($"{LT("Total WSS", "총 WSS")}  {StatFormat.G(result.TotalWss)}");
            sb.AppendLine($"{LT("Between SS", "군집 간 SS")}  {StatFormat.G(result.BetweenSs)}");
            sb.AppendLine($"{LT("Total SS", "총 SS")}  {StatFormat.G(result.TotalSs)}");
            sb.AppendLine($"{LT("Between / total", "군집 간 / 총")}  {StatFormat.F(result.BetweenTotalRatio)}");
            sb.AppendLine();

            sb.AppendLine(LT("Centers (original units, means of assigned rows)", "중심 (원래 단위, 배정된 행의 평균)"));
            AppendBlocked(sb, result.FeatureNames, result.Clusters.Length,
                (j, c) => StatFormat.G(result.Clusters[c].CenterOriginal[j]), LT("Feature", "특성"));
            sb.AppendLine();

            if (result.Elbow is { } elbow)
            {
                string on = result.ElbowSampleRows > 0
                    ? LT($", on the {result.ElbowSampleRows:N0}-row sample", $", 표본 {result.ElbowSampleRows:N0}행 기준")
                    : "";
                sb.AppendLine(LT($"Elbow WSS for k = 2..{result.Clusters.Length} ({space}, same seed and restarts{on})",
                    $"엘보 WSS, k = 2..{result.Clusters.Length} ({space}, 같은 시드·재시작{on})"));
                var table = new TextTable("k", "WSS");
                foreach (var e in elbow)
                    table.AddRow(e.K.ToString(CultureInfo.InvariantCulture), e.Succeeded ? StatFormat.G(e.TotalWss) : LT("failed", "실패"));
                sb.Append(table.Render());
                sb.AppendLine();
            }

            if (!result.SilhouetteDefined)
                sb.AppendLine(LT("Silhouette: undefined (needs 2 ≤ clusters ≤ rows − 1 in the sample).",
                    "실루엣: 정의되지 않음(표본에서 2 ≤ 군집 수 ≤ 행 수 − 1 이어야 함)."));
            else if (result.SilhouetteSampled)
                sb.AppendLine(LT(
                    $"Silhouette (Euclidean, approximate, fixed-seed sample of {result.SilhouetteRows:N0} / {matrix.RowCount:N0} rows): {StatFormat.F(result.Silhouette)}",
                    $"실루엣(유클리드, 근사, 시드 고정 표본 {result.SilhouetteRows:N0} / {matrix.RowCount:N0}행): {StatFormat.F(result.Silhouette)}"));
            else
                sb.AppendLine(LT(
                    $"Silhouette (Euclidean, all {result.SilhouetteRows:N0} rows): {StatFormat.F(result.Silhouette)}",
                    $"실루엣(유클리드, 전체 {result.SilhouetteRows:N0}행): {StatFormat.F(result.Silhouette)}"));

            var warnings = new List<string>();
            if (matrix.RowCount < 30)
                warnings.Add(LT("Small sample (fewer than 30 rows). Cluster sizes and silhouette are unstable.",
                    "표본이 작습니다(30행 미만). 군집 크기와 실루엣이 불안정할 수 있습니다."));
            if (result.EmptyClusters > 0)
                warnings.Add(LT($"{result.EmptyClusters} empty cluster(s). Those centers are not estimated.",
                    $"빈 군집이 {result.EmptyClusters}개입니다. 그 중심은 추정되지 않았습니다."));
            if (!result.LloydFixedPoint)
                warnings.Add(LT("The partition is not a fixed point of Lloyd's iteration (energy rose after a center update). Centers are still the means of the assignment, and WSS is that assignment's inertia.",
                    "배정이 Lloyd 반복의 고정점이 아닙니다(중심 갱신 후 에너지가 증가). 중심은 그래도 배정 평균이고, WSS는 그 배정의 관성입니다."));
            if (double.IsNaN(result.BetweenTotalRatio))
                warnings.Add(LT("Total SS is 0 (all used rows are identical in the scaled space). The between/total ratio is undefined.",
                    "총 SS가 0입니다(스케일 공간에서 사용한 행이 모두 같음). 군집 간/총 비율은 정의되지 않습니다."));
            if (!result.RefinementConverged)
                warnings.Add(LT($"Full-data Lloyd refinement stopped at {KMeansClustering.MaxRefineIterations} iterations without a stable assignment.",
                    $"전체 행 Lloyd 정제가 {KMeansClustering.MaxRefineIterations}회 안에 배정이 안정되지 않았습니다."));
            if (result.Elbow?.Any(e => !e.Succeeded) == true)
                warnings.Add(LT("Some elbow values failed (too few distinct points for that k) and are not reported as WSS.",
                    "일부 엘보 k는 서로 다른 점이 부족해 실패했습니다. 그 값은 WSS로 보고하지 않습니다."));
            AppendWarnings(sb, warnings);
            return sb.ToString();
        }

        private string FormatClassification(FeatureMatrix matrix, ClassifierEvalResult eval, ClassifierOptions options,
            bool knn, NaiveBayesModel? model, bool allRows, FeatureScaler? scaler)
        {
            var sb = new StringBuilder();
            var names = matrix.ClassNames ?? Array.Empty<string>();
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            if (knn)
                sb.AppendLine(LT(
                    $"Method: k-nearest neighbors, k = {options.Neighbors}, Euclidean, exact ALGLIB kd-tree (eps = 0), uniform vote.",
                    $"방법: k-최근접 이웃, k = {options.Neighbors}, 유클리드, ALGLIB kd-tree 정확 검색(eps = 0), 균등 투표."));
            else
                sb.AppendLine(LT(
                    "Method: naive Bayes. Numeric features: Gaussian (sklearn GaussianNB, var_smoothing = 1e-9 × max feature variance, population variance). Categorical features: Laplace α = 1 (sklearn CategoricalNB, level count = one-hot width).",
                    "방법: 나이브 베이즈. 수치 특성은 가우시안(sklearn GaussianNB, var_smoothing = 1e-9 × 최대 특성 분산, 모분산). 범주 특성은 라플라스 α = 1(sklearn CategoricalNB, 수준 수 = 원-핫 폭)."));
            sb.AppendLine(LT($"Scaling: {ScalingLabel(options.Scaling)}, fit on training rows only.",
                $"스케일링: {ScalingLabel(options.Scaling)}, 학습 행으로만 적합."));
            if (!knn && options.Scaling != ScalingMethod.None)
                sb.AppendLine(LT("Scaling is applied to numeric features only, so one-hot category codes stay intact.",
                    "스케일링은 수치 특성에만 적용합니다. 원-핫 범주 코드는 그대로입니다."));
            sb.AppendLine(options.Scheme == EvalScheme.Holdout
                ? LT($"Evaluation: stratified holdout, test fraction {options.TestFraction.ToString("0.00", CultureInfo.InvariantCulture)}, seed {options.Seed}. Train {eval.TrainRows:N0} · test {eval.TestRows:N0}.",
                     $"평가: 층화 홀드아웃, 시험 비율 {options.TestFraction.ToString("0.00", CultureInfo.InvariantCulture)}, 시드 {options.Seed}. 학습 {eval.TrainRows:N0} · 시험 {eval.TestRows:N0}.")
                : LT($"Evaluation: stratified {options.Folds}-fold cross-validation, seed {options.Seed}. Each of {eval.TestRows:N0} rows is predicted once.",
                     $"평가: 층화 {options.Folds}-겹 교차검증, 시드 {options.Seed}. {eval.TestRows:N0}행을 각각 한 번씩 예측했습니다."));
            sb.AppendLine(LT($"Features: {FeatureList(matrix.FeatureNames)}", $"특성: {FeatureList(matrix.FeatureNames)}"));
            sb.AppendLine(LT($"Classes: {FeatureList(names)}", $"클래스: {FeatureList(names)}"));
            sb.AppendLine();

            var m = eval.Metrics;
            sb.AppendLine($"{LT("Accuracy", "정확도")}  {StatFormat.F(m.Accuracy)}");
            sb.AppendLine($"{LT("Macro F1", "매크로 F1")}  {StatFormat.F(m.MacroF1)}");
            sb.AppendLine($"{LT("Majority-class baseline", "최다 클래스 기준선")}  {StatFormat.F(eval.MajorityBaseline)}");
            sb.AppendLine();

            int show = Math.Min(names.Count, 40);
            var per = new TextTable(LT("Class", "클래스"), LT("Support", "지지"), LT("Precision", "정밀도"), LT("Recall", "재현율"), "F1");
            for (int c = 0; c < show; c++)
                per.AddRow(names[c], StatFormat.Int(m.Support[c]), StatFormat.F(m.Precision[c]), StatFormat.F(m.Recall[c]), StatFormat.F(m.F1[c]));
            sb.Append(per.Render());
            if (show < names.Count)
                sb.AppendLine(LT($"… {names.Count - show} more classes omitted.", $"… 클래스 {names.Count - show}개 생략."));
            sb.AppendLine();

            if (names.Count == 0)
                sb.AppendLine(LT("No classes to tabulate.", "표로 만들 클래스가 없습니다."));
            else if (names.Count > 8)
                sb.AppendLine(LT("Confusion matrix omitted (more than 8 classes). Rows of the matrix are actual, columns predicted.",
                    "클래스가 8개를 넘어 혼동행렬은 생략합니다. 행은 실제, 열은 예측입니다."));
            else
            {
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
            }
            sb.AppendLine();

            if (model is not null)
                AppendBayes(sb, matrix, model, allRows, scaler, options);

            var warnings = new List<string>();
            if (m.Total < 30)
                warnings.Add(LT("Small evaluation set (fewer than 30 predicted rows). Accuracy and F1 are unstable.",
                    "평가 집합이 작습니다(예측 30행 미만). 정확도와 F1이 불안정할 수 있습니다."));
            if (m.Accuracy <= eval.MajorityBaseline + 1e-12)
                warnings.Add(LT("Accuracy does not beat the majority-class baseline. The fit is not evidence of useful discrimination.",
                    "정확도가 최다 클래스 기준선을 넘지 않습니다. 이 적합이 유용한 구분 능력이 있다는 증거가 되지 않습니다."));
            if (m.Support.Any(s => s > 0 && s < 5))
                warnings.Add(LT("At least one class has support below 5 in the evaluation. Per-class precision and recall are noisy.",
                    "평가에서 지지가 5 미만인 클래스가 있습니다. 클래스별 정밀도·재현율은 잡음이 큽니다."));
            if (knn && options.Neighbors > eval.TrainRows / 2 && options.Scheme == EvalScheme.Holdout)
                warnings.Add(LT("k is at least half the training rows. Neighbors then reach far from the query.",
                    "k가 학습 행의 절반 이상입니다. 이웃이 질의에서 멀리까지 닿습니다."));
            AppendWarnings(sb, warnings);
            if (!knn)
            {
                sb.AppendLine(LT("Note: features are treated as independent given the class. That assumption is often false; read the metrics against the baseline.",
                    "참고: 클래스가 주어지면 특성이 독립이라고 가정합니다. 이 가정은 자주 어긋나므로, 지표는 기준선과 함께 읽으세요."));
            }
            return sb.ToString();
        }

        private void AppendBayes(StringBuilder sb, FeatureMatrix matrix, NaiveBayesModel model, bool allRows, FeatureScaler? scaler, ClassifierOptions options)
        {
            sb.AppendLine(allRows
                ? LT("Parameters below are a descriptive fit on all used rows. The metrics above are from cross-validation, not from this fit.",
                     "아래 매개변수는 사용한 행 전체의 기술 적합입니다. 위 지표는 교차검증 결과이며 이 적합의 지표가 아닙니다.")
                : LT("Parameters below are the training-split fit used for the holdout predictions.",
                     "아래 매개변수는 홀드아웃 예측에 쓴 학습 분할 적합입니다."));
            sb.AppendLine(LT($"var_smoothing epsilon = {StatFormat.G(model.Epsilon)} (added to each class variance).",
                $"var_smoothing epsilon = {StatFormat.G(model.Epsilon)} (클래스 분산마다 더함)."));
            sb.AppendLine();
            var names = matrix.ClassNames ?? Array.Empty<string>();
            sb.AppendLine(LT("Class priors (training frequencies)", "클래스 사전확률 (학습 빈도)"));
            var priors = new TextTable(LT("Class", "클래스"), LT("Count", "개수"), LT("Prior", "사전확률"));
            int show = Math.Min(model.ClassCount, 40);
            for (int c = 0; c < show; c++)
                priors.AddRow(c < names.Count ? names[c] : c.ToString(CultureInfo.InvariantCulture),
                    StatFormat.Int(model.ClassCounts[c]), StatFormat.F(model.Prior[c], 4));
            sb.Append(priors.Render());
            sb.AppendLine();

            if (model.NumericColumns.Length == 0)
                sb.AppendLine(LT("No numeric features. Likelihood is categorical only.", "수치 특성이 없습니다. 우도는 범주형만 사용합니다."));
            else
            {
                string unit = options.Scaling == ScalingMethod.None || scaler is null
                    ? LT("original units", "원래 단위")
                    : LT("original units (inverse-transformed from the scaled fit)", "원래 단위(스케일 적합을 역변환)");
                sb.AppendLine(LT($"Per-class means of numeric features ({unit})", $"수치 특성의 클래스별 평균 ({unit})"));
                var featNames = model.NumericColumns.Select(c => c < matrix.FeatureNames.Count ? matrix.FeatureNames[c] : "X").ToArray();
                AppendBlocked(sb, featNames, Math.Min(model.ClassCount, names.Count == 0 ? model.ClassCount : names.Count),
                    (j, c) => model.ClassCounts[c] == 0 ? "—" : StatFormat.G(model.MeanInOriginalUnits(c, j, scaler)),
                    LT("Feature", "특성"),
                    c => c < names.Count ? ShortName(names[c]) : ClusterName(c));
                if (model.ClassCounts.Any(c => c == 0))
                    sb.AppendLine(LT("A class absent from this fit has prior 0 and is not predicted. Its mean is not shown.",
                        "이 적합에 없는 클래스는 사전확률이 0이고 예측되지 않습니다. 평균은 표시하지 않습니다."));
            }
            sb.AppendLine();

            if (model.Categorical.Length == 0)
                sb.AppendLine(LT("No categorical features. Likelihood is Gaussian only.", "범주 특성이 없습니다. 우도는 가우시안만 사용합니다."));
            else
            {
                sb.AppendLine(LT($"Categorical features (Laplace α = {model.Alpha.ToString("0.###", CultureInfo.InvariantCulture)})",
                    $"범주 특성 (라플라스 α = {model.Alpha.ToString("0.###", CultureInfo.InvariantCulture)})"));
                foreach (var f in model.Categorical)
                {
                    var levels = f.Columns.Select(c => c < matrix.FeatureNames.Count ? matrix.FeatureNames[c] : "?").ToArray();
                    string shown = string.Join(", ", levels.Take(8));
                    if (levels.Length > 8) shown += LT($" … +{levels.Length - 8}", $" … +{levels.Length - 8}");
                    sb.AppendLine("  " + shown);
                }
            }
            sb.AppendLine();
        }

        private static void AppendBlocked(StringBuilder sb, IReadOnlyList<string> rowNames, int columns,
            Func<int, int, string> cell, string rowHeader, Func<int, string>? colName = null)
        {
            const int block = 6;
            int shown = Math.Min(rowNames.Count, 40);
            for (int start = 0; start < columns; start += block)
            {
                int end = Math.Min(columns, start + block);
                var headers = new string[1 + end - start];
                headers[0] = rowHeader;
                for (int c = start; c < end; c++)
                    headers[1 + c - start] = colName?.Invoke(c) ?? ClusterName(c);
                var table = new TextTable(headers);
                for (int j = 0; j < shown; j++)
                {
                    var cells = new string[headers.Length];
                    cells[0] = rowNames[j];
                    for (int c = start; c < end; c++) cells[1 + c - start] = cell(j, c);
                    table.AddRow(cells);
                }
                sb.Append(table.Render());
            }
            if (shown < rowNames.Count)
                sb.AppendLine(LT($"… {rowNames.Count - shown} more features omitted.", $"… 특성 {rowNames.Count - shown}개 생략."));
        }

        private static void AppendWarnings(StringBuilder sb, List<string> warnings)
        {
            if (warnings.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine(LT("Warnings", "주의"));
            foreach (var w in warnings) sb.AppendLine("  " + w);
        }

        private static string[] ScalingChoices() => new[]
        {
            LT("None", "없음"),
            LT("Z-score", "Z-점수"),
            LT("Min-max", "최소-최대"),
        };

        private static ScalingMethod ScalingOf(int index) => index switch
        {
            0 => ScalingMethod.None,
            2 => ScalingMethod.MinMax,
            _ => ScalingMethod.ZScore,
        };

        private static string ScalingLabel(ScalingMethod method) => method switch
        {
            ScalingMethod.None => LT("none", "없음"),
            ScalingMethod.ZScore => LT("Z-score (sample sd, ddof=1)", "Z-점수(표본 표준편차, ddof=1)"),
            ScalingMethod.MinMax => LT("min-max", "최소-최대"),
            _ => method.ToString(),
        };

        private static string FeatureList(IReadOnlyList<string> names)
        {
            if (names.Count == 0) return "—";
            int show = Math.Min(names.Count, 8);
            string text = string.Join(", ", names.Take(show));
            if (names.Count > show) text += $" (+{names.Count - show})";
            return text;
        }

        private static string ClusterName(int index) => LT("C", "군집") + (index + 1).ToString(CultureInfo.InvariantCulture);

        private static string ShortName(string name)
            => name.Length <= 16 ? name : name[..15] + "…";
    }
}
