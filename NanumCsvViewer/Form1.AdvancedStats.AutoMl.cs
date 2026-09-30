using System.Globalization;
using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvAdaBoost()
        {
            if (!TryAdaBoostDialog(out var features, out var target, out bool regression, out var boost, out var eval)) return;
            string title = LT("AdaBoost", "AdaBoost");
            await RunAdvancedAsync(title, input =>
            {
                var kind = regression ? TargetKind.Numeric : TargetKind.Categorical;
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, kind, cancellation: input.Cancellation);
                if (regression)
                {
                    var run = AdaBoost.EvaluateRegression(matrix.X, matrix.NumericTarget!, eval, boost, input.Cancellation);
                    var saved = run.ModelUsesAllRows
                        ? run.Model
                        : AdaBoost.FitRegression(matrix.X, matrix.NumericTarget!, boost, input.Cancellation);
                    string note = AdaSavedNote(saved, matrix.RowCount);
                    var bundle = ModelBundle.FromFeatures(AdaBoost.ModelTypeName, ModelTask.Regression, matrix, input.Headers, input.KindOf,
                        input.Headers[target], saved, null, saved.RowsFit, note, AdaParameters(boost, eval.Seed));
                    return new AdvancedOutput(FormatAdaRegression(matrix, run, boost) + "\n" + note, bundle);
                }
                var cls = AdaBoost.EvaluateClassification(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, eval, boost, input.Cancellation);
                var model = cls.ModelUsesAllRows
                    ? cls.Model
                    : AdaBoost.FitClassification(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, boost, input.Cancellation);
                string savedNote = AdaSavedNote(model, matrix.RowCount);
                var classified = ModelBundle.FromFeatures(AdaBoost.ModelTypeName, ModelTask.Classification, matrix, input.Headers, input.KindOf,
                    input.Headers[target], model, null, model.RowsFit, savedNote, AdaParameters(boost, eval.Seed));
                return new AdvancedOutput(FormatAdaClassification(matrix, cls, boost) + "\n" + savedNote, classified);
            });
        }

        private async void AdvAutoMl()
        {
            if (!TryAutoMlDialog(out var features, out var target, out bool regression, out var options)) return;
            string title = LT("AutoML", "AutoML");
            await RunAdvancedAsync(title, input =>
            {
                var kind = regression ? TargetKind.Numeric : TargetKind.Categorical;
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, kind, cancellation: input.Cancellation);
                var report = regression
                    ? AutoMl.Search(matrix.X, matrix.NumericTarget!, options, input.Cancellation)
                    : AutoMl.Search(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, options, input.Cancellation);
                string note = AutoSavedNote(report);
                var bundle = ModelBundle.FromFeatures(report.BestModelType,
                    regression ? ModelTask.Regression : ModelTask.Classification,
                    matrix, input.Headers, input.KindOf, input.Headers[target], report.BundleEngine, report.BundleScaler,
                    report.BundleRows, note, new Dictionary<string, string>
                    {
                        ["config"] = report.BestName,
                        ["metric"] = report.Metric.ToString(),
                        ["seed"] = report.Seed.ToString(CultureInfo.InvariantCulture),
                        ["folds"] = report.Folds.ToString(CultureInfo.InvariantCulture),
                    });
                return new AdvancedOutput(FormatAutoMl(matrix, report) + "\n" + note, bundle);
            });
        }

        private bool TryAdaBoostDialog(out List<int> features, out int target, out bool regression,
            out AdaBoostOptions boost, out ClassifierOptions eval)
        {
            features = new List<int>();
            target = 0;
            regression = false;
            boost = new AdaBoostOptions();
            eval = new ClassifierOptions();
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return false;
            string title = LT("AdaBoost", "AdaBoost");
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
            var targetBox = dlg.AddCombo(LT("Target", "목표"), ColumnLabels(), targetDefault);
            UncheckWhenSelected(targetBox, list);
            var estimators = dlg.AddNumeric(LT("Estimators", "추정기 수"), 1, 500, 50);
            var rate = dlg.AddText(LT("Learning rate", "학습률"), "1");
            var depth = dlg.AddNumeric(LT("Max depth (0 = stump / sklearn default)", "최대 깊이(0 = 스텀프·sklearn 기본)"), 0, 8, 1);
            var loss = dlg.AddCombo(LT("Regression loss", "회귀 손실"), new[]
            {
                LT("Linear", "선형"),
                LT("Square", "제곱"),
                LT("Exponential", "지수"),
            }, 0);
            var criterion = dlg.AddCombo(LT("Classification split", "분류 분할"), new[]
            {
                LT("Gini", "지니"),
                LT("Entropy", "엔트로피"),
            }, 0);
            var scheme = dlg.AddCombo(LT("Evaluation", "평가"), new[]
            {
                LT("Holdout (stratified if class)", "홀드아웃(클래스는 층화)"),
                LT("K-fold (stratified if class)", "k-겹(클래스는 층화)"),
            }, 0);
            var pct = dlg.AddNumeric(LT("Test % (holdout)", "시험 비율 %(홀드아웃)"), 5, 50, 30);
            var folds = dlg.AddNumeric(LT("Folds (k-fold)", "겹 수(k-겹)"), 2, 10, 5);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 1, 1_000_000_000, 1);
            var seconds = dlg.AddNumeric(LT("Time budget (seconds, 0 = none)", "시간 예산(초, 0 = 없음)"), 0, 3600, 0);
            dlg.AddNote(LT(
                $"Classification is SAMME with a weighted shallow tree (depth 0 means a stump). Regression is AdaBoost.R2: weighted bootstrap (numpy RandomState) and an unweighted CART tree (depth 0 means 3, the sklearn default). Prediction is a weighted median. Loss applies only to a numeric target. Above {AdaBoost.DefaultMaxTrainingRows:N0} rows the fit uses a fixed-seed sample and says so. Ties in classification splits take the lower feature index.",
                $"분류는 가중 얕은 트리 SAMME입니다(깊이 0은 스텀프). 회귀는 AdaBoost.R2로, 가중 부트스트랩(numpy RandomState)과 비가중 CART입니다(깊이 0은 sklearn 기본인 3). 예측은 가중 중앙값입니다. 손실은 수치 목표에만 씁니다. {AdaBoost.DefaultMaxTrainingRows:N0}행을 넘으면 시드 고정 표본만 쓰고 그 사실을 적습니다. 분류 분할 동점은 특성 인덱스가 작은 쪽입니다."));
            if (!dlg.ShowOk(this)) return false;

            features = CheckedIndexes(list);
            target = targetBox.SelectedIndex;
            if (!AcceptFeatures(title, features, target)) return false;
            if (!TryBoostDouble(rate.Text, out double learningRate) || !(learningRate > 0))
            {
                ShowResult(title, LT("Learning rate must be a finite number greater than 0.", "학습률은 0보다 큰 유한한 수여야 합니다."));
                return false;
            }
            regression = IsNumericColumn(target);
            boost = new AdaBoostOptions
            {
                Estimators = (int)estimators.Value,
                LearningRate = learningRate,
                MaxDepth = (int)depth.Value,
                Seed = (int)seed.Value,
                Loss = loss.SelectedIndex switch
                {
                    1 => AdaBoostLoss.Square,
                    2 => AdaBoostLoss.Exponential,
                    _ => AdaBoostLoss.Linear,
                },
                Criterion = criterion.SelectedIndex == 1 ? TreeCriterion.Entropy : TreeCriterion.Gini,
                TimeBudgetSeconds = (double)seconds.Value,
            };
            eval = new ClassifierOptions
            {
                Scheme = scheme.SelectedIndex == 1 ? EvalScheme.KFold : EvalScheme.Holdout,
                TestFraction = (double)pct.Value / 100.0,
                Folds = (int)folds.Value,
                Seed = (int)seed.Value,
                Scaling = ScalingMethod.None,
            };
            return true;
        }

        private bool TryAutoMlDialog(out List<int> features, out int target, out bool regression, out AutoMlOptions options)
        {
            features = new List<int>();
            target = 0;
            regression = false;
            options = new AutoMlOptions();
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return false;
            string title = LT("AutoML", "AutoML");
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
            var targetBox = dlg.AddCombo(LT("Target", "목표"), ColumnLabels(), targetDefault);
            UncheckWhenSelected(targetBox, list);
            var metric = dlg.AddCombo(LT("Metric", "지표"), new[]
            {
                LT("Accuracy (class)", "정확도(분류)"),
                LT("Macro F1 (class)", "매크로 F1(분류)"),
                LT("RMSE (numeric)", "RMSE(수치)"),
                LT("R² (numeric)", "R²(수치)"),
            }, 0);
            var seconds = dlg.AddNumeric(LT("Time budget (seconds)", "시간 예산(초)"), 0, 3600, 30);
            var rows = dlg.AddNumeric(LT("Search row budget", "탐색 행 예산"), 20, 100_000, AutoMl.DefaultSearchRows);
            var folds = dlg.AddNumeric(LT("Folds", "겹 수"), 2, 10, 3);
            var pct = dlg.AddNumeric(LT("Test % (held out)", "시험 비율 %(홀드아웃)"), 5, 50, 30);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 1, 1_000_000_000, 1);
            dlg.AddNote(LT(
                "Search is a fixed small grid on a seeded sample of the training split only. The test split is not used to choose a model. A time budget of 0 fits the first configuration and does not start another. Linear and logistic are scaled on each fold's training rows. SVM is tried only when the search sample has at most "
                + AutoMl.SvmSearchRowCap.ToString("N0", CultureInfo.InvariantCulture)
                + " rows. The saved model is refit on all used rows (including the holdout), or a seeded sample if that exceeds the refit cap — the note says which. Test metrics are from a separate fit on the training split.",
                "탐색은 학습 분할의 시드 고정 표본에서만 도는 고정된 작은 격자입니다. 시험 분할은 모형 선택에 쓰지 않습니다. 시간 예산 0은 첫 설정만 적합하고 다음 설정은 시작하지 않습니다. 선형·로지스틱은 각 겹의 학습 행으로만 스케일합니다. SVM은 탐색 표본이 "
                + AutoMl.SvmSearchRowCap.ToString("N0", CultureInfo.InvariantCulture)
                + "행 이하일 때만 시도합니다. 저장 모형은 사용 행 전체(홀드아웃 포함)로 다시 적합하거나, 재적합 상한을 넘으면 시드 고정 표본입니다 — 어느 쪽인지는 안내 문장이 적습니다. 시험 지표는 학습 분할만으로 다시 적합한 별도 모형입니다."));
            if (!dlg.ShowOk(this)) return false;

            features = CheckedIndexes(list);
            target = targetBox.SelectedIndex;
            if (!AcceptFeatures(title, features, target)) return false;
            regression = IsNumericColumn(target);
            var chosen = metric.SelectedIndex switch
            {
                1 => AutoMlMetric.MacroF1,
                2 => AutoMlMetric.Rmse,
                3 => AutoMlMetric.RSquared,
                _ => AutoMlMetric.Accuracy,
            };
            bool metricIsRegression = chosen is AutoMlMetric.Rmse or AutoMlMetric.RSquared;
            if (regression != metricIsRegression)
            {
                ShowResult(title, regression
                    ? LT("A numeric target needs RMSE or R².", "수치 목표는 RMSE 또는 R²를 고르세요.")
                    : LT("A class target needs accuracy or macro F1.", "분류 목표는 정확도 또는 매크로 F1을 고르세요."));
                return false;
            }
            options = new AutoMlOptions
            {
                Metric = chosen,
                Folds = (int)folds.Value,
                Seed = (int)seed.Value,
                TestFraction = (double)pct.Value / 100.0,
                TimeBudgetSeconds = (double)seconds.Value,
                SearchRowBudget = (int)rows.Value,
            };
            return true;
        }

        private bool AcceptFeatures(string title, List<int> features, int target)
        {
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
            return true;
        }

        private static Dictionary<string, string> AdaParameters(AdaBoostOptions boost, int seed) => new()
        {
            ["estimators"] = boost.Estimators.ToString(CultureInfo.InvariantCulture),
            ["learningRate"] = boost.LearningRate.ToString("G", CultureInfo.InvariantCulture),
            ["maxDepth"] = boost.MaxDepth.ToString(CultureInfo.InvariantCulture),
            ["loss"] = boost.Loss.ToString(),
            ["seed"] = seed.ToString(CultureInfo.InvariantCulture),
        };

        private string AdaSavedNote(AdaBoostModel model, int usedRows)
        {
            if (!model.Sampled)
                return AdvSavedNote(usedRows);
            return LT(
                $"Saved model fitted on a seeded sample of {model.RowsFit:N0} of {model.SourceRows:N0} used rows (cap {model.SourceRows:N0} requested, engine cap {AdaBoost.DefaultMaxTrainingRows:N0}). Metrics above are from the evaluation split, not in-sample scores of the saved model.",
                $"저장 모형은 사용 {model.SourceRows:N0}행 중 시드 고정 표본 {model.RowsFit:N0}행에 적합했습니다(엔진 상한 {AdaBoost.DefaultMaxTrainingRows:N0}). 위 지표는 평가 분할의 것이며 저장 모형의 표본 내 점수가 아닙니다.");
        }

        private string AutoSavedNote(AutoMlReport report)
        {
            if (report.BundleUsesAllRows)
                return LT(
                    $"Saved model ({report.BestName}) refit on all {report.BundleRows:N0} used rows, including the holdout. Test metrics above are from a separate fit on the training split only.",
                    $"저장 모형({report.BestName})은 홀드아웃을 포함한 사용 {report.BundleRows:N0}행 전체에 다시 적합했습니다. 위 시험 지표는 학습 분할만으로 다시 적합한 별도 모형입니다.");
            return LT(
                $"Saved model ({report.BestName}) refit on a seeded sample of {report.BundleRows:N0} used rows (refit cap), not every used row. Test metrics above are from a separate fit on the training split only.",
                $"저장 모형({report.BestName})은 사용 행 전체가 아니라 재적합 상한의 시드 고정 표본 {report.BundleRows:N0}행에 다시 적합했습니다. 위 시험 지표는 학습 분할만으로 다시 적합한 별도 모형입니다.");
        }

        private string FormatAdaClassification(FeatureMatrix matrix, AdaBoostClassificationResult run, AdaBoostOptions options)
        {
            var sb = new StringBuilder();
            var model = run.Model;
            AppendScopeAndClasses(sb, matrix, run.Evaluation, classification: true);
            sb.AppendLine(LT(
                $"Method: AdaBoost SAMME, weighted CART base learner, max depth {model.MaxDepth}, learning rate {StatFormat.G(model.LearningRate)}, estimators kept {model.EstimatorsUsed} of {model.EstimatorsRequested}. Criterion {model.Criterion}. Ties: lowest feature index, then lowest threshold (sklearn may differ only on ties, because it shuffles features).",
                $"방법: AdaBoost SAMME, 가중 CART 기본 학습기, 최대 깊이 {model.MaxDepth}, 학습률 {StatFormat.G(model.LearningRate)}, 유지한 추정기 {model.EstimatorsUsed}/{model.EstimatorsRequested}. 기준 {model.Criterion}. 동점: 가장 작은 특성 인덱스, 그다음 낮은 임계값(sklearn은 특성 순서를 섞으므로 동점일 때만 다를 수 있습니다)."));
            AppendAdaFitNotes(sb, model, options);
            sb.AppendLine(run.ModelUsesAllRows
                ? LT("The model below was refit on all used rows.", "아래 모형은 사용 행 전체에 다시 적합했습니다.")
                : LT("The model below is the holdout training fit. The saved bundle is a separate refit on all used rows.",
                    "아래 모형은 홀드아웃 학습 적합입니다. 저장 묶음은 사용 행 전체에 다시 적합한 별도 모형입니다."));
            AppendClassificationMetrics(sb, matrix, run.Evaluation);
            AppendImportances(sb, matrix.FeatureNames, model.Importances, LT(
                "Feature importance: estimator-weight average of normalized impurity decrease (not permutation).",
                "특성 중요도: 정규화 불순도 감소를 추정기 가중으로 평균(순열 중요도 아님)."));
            return sb.ToString();
        }

        private string FormatAdaRegression(FeatureMatrix matrix, AdaBoostRegressionResult run, AdaBoostOptions options)
        {
            var sb = new StringBuilder();
            var model = run.Model;
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(LT($"Features: {FeatureList(matrix.FeatureNames)}", $"특성: {FeatureList(matrix.FeatureNames)}"));
            AppendEvalScheme(sb, run.Scheme, run.Seed, run.TestFraction, run.Folds, run.TrainRows, run.TestRows, stratified: false);
            sb.AppendLine(LT(
                $"Method: AdaBoost.R2, loss {model.Loss}, unweighted CART on a weighted bootstrap (numpy RandomState, seed {model.Seed}), max depth {model.MaxDepth}, learning rate {StatFormat.G(model.LearningRate)}, estimators kept {model.EstimatorsUsed} of {model.EstimatorsRequested}. Prediction is the weighted median.",
                $"방법: AdaBoost.R2, 손실 {model.Loss}, 가중 부트스트랩(numpy RandomState, 시드 {model.Seed}) 위의 비가중 CART, 최대 깊이 {model.MaxDepth}, 학습률 {StatFormat.G(model.LearningRate)}, 유지한 추정기 {model.EstimatorsUsed}/{model.EstimatorsRequested}. 예측은 가중 중앙값입니다."));
            AppendAdaFitNotes(sb, model, options);
            sb.AppendLine(run.ModelUsesAllRows
                ? LT("The model below was refit on all used rows.", "아래 모형은 사용 행 전체에 다시 적합했습니다.")
                : LT("The model below is the holdout training fit. The saved bundle is a separate refit on all used rows.",
                    "아래 모형은 홀드아웃 학습 적합입니다. 저장 묶음은 사용 행 전체에 다시 적합한 별도 모형입니다."));
            AppendRegressionMetrics(sb, run.Metrics, run.BaselineRmse);
            AppendImportances(sb, matrix.FeatureNames, model.Importances, LT(
                "Feature importance: estimator-weight average of normalized impurity decrease (not permutation).",
                "특성 중요도: 정규화 불순도 감소를 추정기 가중으로 평균(순열 중요도 아님)."));
            return sb.ToString();
        }

        private void AppendAdaFitNotes(StringBuilder sb, AdaBoostModel model, AdaBoostOptions options)
        {
            if (model.Sampled)
                sb.AppendLine(LT(
                    $"Row cap: seeded sample of {model.RowsFit:N0} of {model.SourceRows:N0} rows (cap {options.MaxTrainingRows:N0}, seed {model.Seed}). Importances refer to that sample.",
                    $"행 상한: {model.SourceRows:N0}행 중 {model.RowsFit:N0}행 표본(상한 {options.MaxTrainingRows:N0}, 시드 {model.Seed}). 중요도는 그 표본 기준입니다."));
            else
                sb.AppendLine(LT($"Training rows in this fit: {model.RowsFit:N0} (no row cap).",
                    $"이 적합의 학습 행: {model.RowsFit:N0} (행 상한 없음)."));
            if (model.TimeBudgetHit)
                sb.AppendLine(LT(
                    $"Time budget ({options.TimeBudgetSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s) stopped the ensemble after {model.EstimatorsUsed} estimators. Later estimators were not fit.",
                    $"시간 예산({options.TimeBudgetSeconds.ToString("0.###", CultureInfo.InvariantCulture)}초)으로 추정기 {model.EstimatorsUsed}개에서 멈췄습니다. 그 다음 추정기는 적합하지 않았습니다."));
            else if (model.StoppedEarly && model.StopReason == "perfect")
                sb.AppendLine(LT("Stopped early: a base learner had zero weighted error.",
                    "조기 종료: 기본 학습기의 가중 오차가 0이었습니다."));
            else if (model.StoppedEarly && model.StopReason == "worse-than-random")
                sb.AppendLine(LT("Stopped early: the next base learner was no better than random and was discarded.",
                    "조기 종료: 다음 기본 학습기가 무작위보다 낫지 않아 버렸습니다."));
        }

        private string FormatAutoMl(FeatureMatrix matrix, AutoMlReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(LT($"Features: {FeatureList(matrix.FeatureNames)}", $"특성: {FeatureList(matrix.FeatureNames)}"));
            if (!report.Regression && matrix.ClassNames != null)
                sb.AppendLine(LT($"Classes: {FeatureList(matrix.ClassNames)}", $"클래스: {FeatureList(matrix.ClassNames)}"));
            sb.AppendLine(LT(
                "Search: fixed grid (linear/logistic, naive Bayes, LDA, KNN 3 and 5, trees depth 2 and 4, random forest 12×depth 4, gradient boosting 20 iterations, AdaBoost, linear SVM when the search sample is small enough). Not a claim that the winner is optimal outside this grid.",
                "탐색: 고정 격자(선형/로지스틱, 나이브 베이즈, LDA, KNN 3·5, 트리 깊이 2·4, 랜덤 포레스트 12×깊이 4, 그래디언트 부스팅 20회, AdaBoost, 탐색 표본이 작을 때만 선형 SVM). 이 격자 밖에서 최적이라는 뜻은 아닙니다."));
            sb.AppendLine(LT(
                $"Outer split: {(report.Regression ? "random" : "stratified")} holdout, test fraction {report.TestFraction.ToString("0.00", CultureInfo.InvariantCulture)}, seed {report.Seed}. Train {report.TrainRows.Length:N0} · test {report.TestRows.Length:N0}. The test rows were not used in search or in cross-validation.",
                $"바깥 분할: {(report.Regression ? "무작위" : "층화")} 홀드아웃, 시험 비율 {report.TestFraction.ToString("0.00", CultureInfo.InvariantCulture)}, 시드 {report.Seed}. 학습 {report.TrainRows.Length:N0} · 시험 {report.TestRows.Length:N0}. 시험 행은 탐색과 교차검증에 쓰지 않았습니다."));
            if (report.SearchSampled)
                sb.AppendLine(LT(
                    $"Search sample: {report.SearchRows.Length:N0} of {report.TrainRows.Length:N0} training rows (budget {report.SearchRowBudget:N0}, seed {report.Seed}). Cross-validation ran on that sample only.",
                    $"탐색 표본: 학습 {report.TrainRows.Length:N0}행 중 {report.SearchRows.Length:N0}행(예산 {report.SearchRowBudget:N0}, 시드 {report.Seed}). 교차검증은 그 표본에서만 돌았습니다."));
            else
                sb.AppendLine(LT(
                    $"Search used all {report.SearchRows.Length:N0} training rows (budget {report.SearchRowBudget:N0} was not hit).",
                    $"탐색은 학습 {report.SearchRows.Length:N0}행을 전부 썼습니다(예산 {report.SearchRowBudget:N0}에 닿지 않음)."));
            sb.AppendLine(LT(
                $"Cross-validation: {(report.Regression ? "random" : "stratified")} {report.Folds}-fold on the search rows, seed {report.Seed}. Score is the mean ± sample sd of the fold {MetricName(report.Metric)}.",
                $"교차검증: 탐색 행의 {(report.Regression ? "무작위" : "층화")} {report.Folds}-겹, 시드 {report.Seed}. 점수는 겹별 {MetricName(report.Metric)}의 평균 ± 표본 표준편차입니다."));
            if (report.BudgetStopped)
                sb.AppendLine(LT(
                    $"Time budget ({report.TimeBudgetSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s) stopped the search after {report.ConfigsTried} of {report.ConfigsPlanned} configurations. Configurations not started are omitted, not failed.",
                    $"시간 예산({report.TimeBudgetSeconds.ToString("0.###", CultureInfo.InvariantCulture)}초)으로 {report.ConfigsPlanned}개 중 {report.ConfigsTried}개에서 탐색을 멈췄습니다. 시작하지 않은 설정은 실패가 아니라 생략입니다."));
            else
                sb.AppendLine(LT(
                    $"Tried {report.ConfigsTried} of {report.ConfigsPlanned} configurations in {report.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.",
                    $"{report.ConfigsPlanned}개 중 {report.ConfigsTried}개를 {report.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture)}초에 시도했습니다."));
            foreach (var skip in report.Skipped)
            {
                sb.AppendLine(skip switch
                {
                    AutoMlSkip.LogisticNeedsBinary => LT("Skipped logistic: the target is not binary.", "로지스틱 생략: 목표가 이진이 아닙니다."),
                    _ => LT($"Skipped linear SVM: the search sample has more than {AutoMl.SvmSearchRowCap:N0} rows.",
                        $"선형 SVM 생략: 탐색 표본이 {AutoMl.SvmSearchRowCap:N0}행을 넘습니다."),
                });
            }
            sb.AppendLine();
            sb.AppendLine(LT($"Best configuration: {report.BestName} ({report.BestModelType}).",
                $"최고 설정: {report.BestName} ({report.BestModelType})."));
            sb.AppendLine(LT(
                $"Held-out test {MetricName(report.Metric)} {StatFormat.G(report.TestScore)}. Baseline (training-split {(report.Regression ? "mean" : "majority class")}, scored on the test rows) {StatFormat.G(report.BaselineScore)}.",
                $"홀드아웃 시험 {MetricName(report.Metric)} {StatFormat.G(report.TestScore)}. 기준선(학습 분할의 {(report.Regression ? "평균" : "최다 클래스")}을 시험 행에 적용) {StatFormat.G(report.BaselineScore)}."));
            if (report.EvaluationSampled)
                sb.AppendLine(LT(
                    $"The test-metric model was fit on a seeded sample of {report.EvaluationRows.Length:N0} training rows, not the full training split.",
                    $"시험 지표 모형은 학습 분할 전체가 아니라 시드 고정 표본 {report.EvaluationRows.Length:N0}행에 적합했습니다."));
            if (report.TestClassification is { } cls)
            {
                sb.AppendLine($"{LT("Accuracy", "정확도")}  {StatFormat.F(cls.Accuracy)}");
                sb.AppendLine($"{LT("Macro F1", "매크로 F1")}  {StatFormat.F(cls.MacroF1)}");
            }
            if (report.TestRegression is { } reg)
            {
                sb.AppendLine($"RMSE  {StatFormat.F(reg.Rmse)}");
                sb.AppendLine($"MAE  {StatFormat.F(reg.Mae)}");
                sb.AppendLine($"R²  {StatFormat.F(reg.RSquared)}");
            }
            sb.AppendLine();
            sb.AppendLine(LT("Leaderboard (search CV only; the test split is not in these scores):",
                "리더보드(탐색 교차검증만. 시험 분할은 이 점수에 없습니다):"));
            var table = new TextTable(LT("Config", "설정"), LT("Mean", "평균"), LT("SD", "표준편차"), LT("Seconds", "초"));
            foreach (var trial in report.Leaderboard)
                table.AddRow(trial.Name, StatFormat.G(trial.Mean), StatFormat.G(trial.Sd), trial.Seconds.ToString("0.00", CultureInfo.InvariantCulture));
            sb.Append(table.Render());
            if (report.Failures.Count > 0)
            {
                sb.AppendLine(LT("Configurations that failed (not ranked):", "실패한 설정(순위에 없음):"));
                foreach (var fail in report.Failures)
                    sb.AppendLine($"- {fail.Name}: {fail.Message}");
            }
            var warnings = new List<string>();
            if (report.TestRows.Length < 30)
                warnings.Add(LT("Small test set (fewer than 30 rows). The held-out metric is unstable.",
                    "시험 집합이 작습니다(30행 미만). 홀드아웃 지표가 불안정할 수 있습니다."));
            if (report.BudgetStopped)
                warnings.Add(LT("The search did not finish the grid. The winner is the best configuration that was started, not the best in the full grid.",
                    "탐색이 격자를 끝내지 못했습니다. 승자는 시작한 설정 중 최고이며, 전체 격자의 최고는 아닙니다."));
            if (report.SearchSampled)
                warnings.Add(LT("Search scores are on a sample of the training split. They are not a claim about every training row.",
                    "탐색 점수는 학습 분할의 표본 기준입니다. 모든 학습 행에 대한 주장이 아닙니다."));
            AppendWarnings(sb, warnings);
            return sb.ToString();
        }

        private static string MetricName(AutoMlMetric metric) => metric switch
        {
            AutoMlMetric.MacroF1 => "macro F1",
            AutoMlMetric.Rmse => "RMSE",
            AutoMlMetric.RSquared => "R²",
            _ => "accuracy",
        };
    }
}
