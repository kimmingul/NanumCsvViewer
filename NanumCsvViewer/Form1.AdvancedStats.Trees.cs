using System.Globalization;
using System.Text;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvDecisionTree()
        {
            if (!TryTreeDialog(LT("Decision Tree", "결정트리"), forest: false,
                    out var features, out var target, out bool regression, out var tree, out var eval, out _)) return;
            string title = LT("Decision Tree", "결정트리");
            await RunAdvancedAsync(title, input =>
            {
                var kind = regression ? TargetKind.Numeric : TargetKind.Categorical;
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, kind, cancellation: input.Cancellation);
                if (regression)
                {
                    var run = DecisionTree.EvaluateRegression(matrix.X, matrix.NumericTarget!, eval,
                        tree with { Criterion = TreeCriterion.Mse }, input.Cancellation);
                    return FormatTreeRegression(matrix, run, tree);
                }
                var cls = DecisionTree.EvaluateClassification(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, eval, tree, input.Cancellation);
                return FormatTreeClassification(matrix, cls, tree);
            });
        }

        private async void AdvRandomForest()
        {
            if (!TryTreeDialog(LT("Random Forest", "랜덤 포레스트"), forest: true,
                    out var features, out var target, out bool regression, out var tree, out var eval, out var forest)) return;
            string title = LT("Random Forest", "랜덤 포레스트");
            await RunAdvancedAsync(title, input =>
            {
                var kind = regression ? TargetKind.Numeric : TargetKind.Categorical;
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, kind, cancellation: input.Cancellation);
                if (regression)
                {
                    var run = RandomForest.EvaluateRegression(matrix.X, matrix.NumericTarget!, eval, forest, input.Cancellation);
                    return FormatForestRegression(matrix, run, forest!);
                }
                var cls = RandomForest.EvaluateClassification(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, eval, forest, input.Cancellation);
                return FormatForestClassification(matrix, cls, forest!);
            });
        }

        private async void AdvSvm()
        {
            if (!TrySvmDialog(out var features, out var target, out var eval, out var svm)) return;
            string title = LT("Support Vector Machine", "서포트 벡터 머신");
            await RunAdvancedAsync(title, input =>
            {
                if (input.KindOf(target) == VariableKind.Numeric)
                    throw new DesignMatrixException("SVM classification needs a categorical target. A numeric column is treated as a regression target.");
                var matrix = FeatureMatrixBuilder.Build(input.Rows, input.Headers, features, input.KindOf,
                    target, TargetKind.Categorical, cancellation: input.Cancellation);
                var run = SupportVectorMachine.Evaluate(matrix.X, matrix.ClassLabels!, matrix.ClassNames!.Count, eval, svm, input.Cancellation);
                return FormatSvm(matrix, run, svm);
            });
        }

        private bool TryTreeDialog(string title, bool forest,
            out List<int> features, out int target, out bool regression,
            out DecisionTreeOptions tree, out ClassifierOptions eval, out RandomForestOptions? forestOpt)
        {
            features = new List<int>();
            target = 0;
            regression = false;
            tree = new DecisionTreeOptions();
            eval = new ClassifierOptions();
            forestOpt = null;
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
            var targetBox = dlg.AddCombo(LT("Target", "목표"), ColumnLabels(), targetDefault);
            UncheckWhenSelected(targetBox, list);
            var criterion = dlg.AddCombo(LT("Split criterion", "분할 기준"), new[]
            {
                LT("Gini (classification)", "지니 (분류)"),
                LT("Entropy (classification)", "엔트로피 (분류)"),
            }, 0);
            NumericUpDown? treesBox = null;
            ComboBox? featMode = null;
            if (forest)
            {
                treesBox = dlg.AddNumeric(LT("Trees", "트리 수"), 1, 500, 40);
                featMode = dlg.AddCombo(LT("Features per split", "분할당 특성"), new[]
                {
                    LT("Auto (sqrt for classes, all for regression)", "자동(분류는 제곱근, 회귀는 전부)"),
                    LT("sqrt", "제곱근"),
                    LT("log2", "로그2"),
                    LT("All", "전부"),
                }, 0);
            }
            var depth = dlg.AddNumeric(LT("Max depth", "최대 깊이"), 1, 64, forest ? 12 : 8);
            var minSplit = dlg.AddNumeric(LT("Min samples to split", "분할 최소 표본"), 2, 1_000_000, 2);
            var minLeaf = dlg.AddNumeric(LT("Min samples per leaf", "잎 최소 표본"), 1, 1_000_000, 1);
            var splitMode = dlg.AddCombo(LT("Splits", "분할"), new[]
            {
                LT("Auto (exact, else quantile bins)", "자동(작으면 정확, 크면 분위 구간)"),
                LT("Exact (sorted, sklearn CART)", "정확(정렬, sklearn CART)"),
                LT("Quantile bins (approximate)", "분위 구간(근사)"),
            }, 0);
            var scheme = dlg.AddCombo(LT("Evaluation", "평가"), new[]
            {
                LT("Stratified holdout", "층화 홀드아웃"),
                LT("Stratified k-fold", "층화 k-겹"),
            }, 0);
            var pct = dlg.AddNumeric(LT("Test % (holdout)", "시험 비율 %(홀드아웃)"), 5, 50, 30);
            var folds = dlg.AddNumeric(LT("Folds (k-fold)", "겹 수(k-겹)"), 2, 10, 5);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 1, 1_000_000_000, 1);
            dlg.AddNote(forest
                ? LT($"A numeric target is regression (MSE); any other target is classification. Importances are mean impurity decrease (MDI), not permutation. Out-of-bag error is on the training sample. Above {RandomForest.DefaultMaxTrainingRows:N0} rows the forest uses a fixed-seed sample. Auto splits bin above {RandomForest.AutoBinRowThreshold:N0} training rows.",
                     $"수치 목표는 회귀(MSE), 그 외는 분류입니다. 중요도는 불순도 감소 평균(MDI)이며 순열 중요도가 아닙니다. OOB는 학습 표본 기준입니다. {RandomForest.DefaultMaxTrainingRows:N0}행을 넘으면 시드 고정 표본만 학습합니다. 자동 분할은 학습 행 {RandomForest.AutoBinRowThreshold:N0}을 넘으면 구간 근사입니다.")
                : LT($"A numeric target is regression (MSE); any other target is classification. Ties break toward the lower feature index, then the lower threshold. Auto uses exact splits up to {DecisionTree.AutoBinRowThreshold:N0} rows, then quantile bins (labelled as approximate).",
                     $"수치 목표는 회귀(MSE), 그 외는 분류입니다. 동점은 특성 인덱스가 작은 쪽, 그다음 낮은 임계값입니다. 자동은 {DecisionTree.AutoBinRowThreshold:N0}행까지 정확 분할, 그 이상은 분위 구간(근사로 표시)입니다."));
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
            regression = IsNumericColumn(target);
            var mode = splitMode.SelectedIndex switch
            {
                1 => TreeSplitMode.Exact,
                2 => TreeSplitMode.Binned,
                _ => TreeSplitMode.Auto,
            };
            var crit = criterion.SelectedIndex == 1 ? TreeCriterion.Entropy : TreeCriterion.Gini;
            tree = new DecisionTreeOptions
            {
                Criterion = regression ? TreeCriterion.Mse : crit,
                MaxDepth = (int)depth.Value,
                MinSamplesSplit = (int)minSplit.Value,
                MinSamplesLeaf = (int)minLeaf.Value,
                SplitMode = mode,
                Seed = (int)seed.Value,
            };
            eval = new ClassifierOptions
            {
                Scheme = scheme.SelectedIndex == 1 ? EvalScheme.KFold : EvalScheme.Holdout,
                TestFraction = (double)pct.Value / 100.0,
                Folds = (int)folds.Value,
                Seed = (int)seed.Value,
                Scaling = ScalingMethod.None,
            };
            if (forest)
            {
                forestOpt = new RandomForestOptions
                {
                    Trees = (int)treesBox!.Value,
                    MaxDepth = (int)depth.Value,
                    MinSamplesSplit = (int)minSplit.Value,
                    MinSamplesLeaf = (int)minLeaf.Value,
                    FeatureMode = featMode!.SelectedIndex switch
                    {
                        1 => ForestFeatureMode.Sqrt,
                        2 => ForestFeatureMode.Log2,
                        3 => ForestFeatureMode.All,
                        _ => ForestFeatureMode.Auto,
                    },
                    Seed = (int)seed.Value,
                    SplitMode = mode,
                    Criterion = regression ? TreeCriterion.Mse : crit,
                    Bins = 64,
                };
            }
            return true;
        }

        private bool TrySvmDialog(out List<int> features, out int target, out ClassifierOptions eval, out SvmOptions svm)
        {
            features = new List<int>();
            target = 0;
            eval = new ClassifierOptions();
            svm = new SvmOptions();
            string title = LT("Support Vector Machine", "서포트 벡터 머신");
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
            var kernel = dlg.AddCombo(LT("Kernel", "커널"), new[]
            {
                LT("RBF", "RBF"),
                LT("Linear", "선형"),
            }, 0);
            var cBox = dlg.AddText("C", "1");
            var gammaMode = dlg.AddCombo(LT("Gamma", "감마"), new[]
            {
                LT("scale (1 / (p · var))", "scale (1 / (p · 분산))"),
                LT("auto (1 / p)", "auto (1 / p)"),
                LT("Custom", "직접 입력"),
            }, 0);
            var gammaBox = dlg.AddText(LT("Custom gamma", "감마 값"), "0.1");
            var scaling = dlg.AddCombo(LT("Scaling", "스케일링"), ScalingChoices(), 1);
            var scheme = dlg.AddCombo(LT("Evaluation", "평가"), new[]
            {
                LT("Stratified holdout", "층화 홀드아웃"),
                LT("Stratified k-fold", "층화 k-겹"),
            }, 0);
            var pct = dlg.AddNumeric(LT("Test % (holdout)", "시험 비율 %(홀드아웃)"), 5, 50, 30);
            var folds = dlg.AddNumeric(LT("Folds (k-fold)", "겹 수(k-겹)"), 2, 10, 5);
            var seed = dlg.AddNumeric(LT("Seed", "시드"), 1, 1_000_000_000, 1);
            dlg.AddNote(LT(
                $"Classification only. Scaling is fit on training rows only. Gamma 'scale' uses the population variance of the rows actually fit. Kernel SMO (libsvm WSS, no shrinking) trains on at most {SupportVectorMachine.DefaultMaxTrainingRows:N0} rows (fixed-seed stratified sample). Metrics score at most {SupportVectorMachine.DefaultMaxEvaluationRows:N0} test rows the same way; both sizes are reported. Linear above the training cap uses dual coordinate descent on all rows; that intercept is regularized and is not sklearn SVC.",
                $"분류만 지원합니다. 스케일링은 학습 행으로만 적합합니다. gamma 'scale'은 실제 학습 행의 모분산입니다. 커널 SMO(libsvm WSS, shrinking 없음)는 최대 {SupportVectorMachine.DefaultMaxTrainingRows:N0}행(시드 고정 층화 표본)만 학습합니다. 지표는 같은 방식으로 시험 행 최대 {SupportVectorMachine.DefaultMaxEvaluationRows:N0}개만 점수화하며, 두 크기를 모두 적습니다. 선형이 학습 상한을 넘으면 전 행에 쌍대 좌표 강하를 쓰며, 절편은 정규화되어 sklearn SVC와 다릅니다."));
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
            if (IsNumericColumn(target))
            {
                ShowResult(title, LT("SVM here is classification only. Choose a categorical target.", "이 SVM은 분류만 지원합니다. 범주 목표를 고르세요."));
                return false;
            }
            if (!double.TryParse(cBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cValue) || !(cValue > 0))
            {
                ShowResult(title, LT("C must be a positive number.", "C는 양수여야 합니다."));
                return false;
            }
            if (!double.TryParse(gammaBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double gamma) || !(gamma > 0))
                gamma = 0.1;
            var gMode = gammaMode.SelectedIndex switch
            {
                1 => SvmGammaMode.Auto,
                2 => SvmGammaMode.Value,
                _ => SvmGammaMode.Scale,
            };
            svm = new SvmOptions
            {
                Kernel = kernel.SelectedIndex == 1 ? SvmKernel.Linear : SvmKernel.Rbf,
                C = cValue,
                GammaMode = gMode,
                Gamma = gamma,
                Seed = (int)seed.Value,
            };
            eval = new ClassifierOptions
            {
                Scheme = scheme.SelectedIndex == 1 ? EvalScheme.KFold : EvalScheme.Holdout,
                TestFraction = (double)pct.Value / 100.0,
                Folds = (int)folds.Value,
                Seed = (int)seed.Value,
                Scaling = ScalingOf(scaling.SelectedIndex),
            };
            return true;
        }

        private string FormatTreeClassification(FeatureMatrix matrix, TreeClassificationResult run, DecisionTreeOptions options)
        {
            var sb = new StringBuilder();
            AppendScopeAndClasses(sb, matrix, run.Evaluation, classification: true);
            AppendTreeMethod(sb, run.Display, options, regression: false);
            AppendClassificationMetrics(sb, matrix, run.Evaluation);
            sb.AppendLine(run.DisplayUsesAllRows
                ? LT("Tree and importances below are fit on all used rows, not on an evaluation split.",
                     "아래 트리와 중요도는 평가 분할이 아니라 사용 행 전체에 적합한 것입니다.")
                : LT("Tree and importances below are the holdout training fit, not a refit on the test rows.",
                     "아래 트리와 중요도는 홀드아웃 학습 적합입니다. 시험 행으로 다시 적합하지 않았습니다."));
            AppendImportances(sb, matrix.FeatureNames, run.Display.Importances, LT("Impurity decrease (MDI), normalized to sum to 1.", "불순도 감소(MDI), 합이 1이 되도록 정규화."));
            sb.AppendLine(LT("Tree (display depth limited)", "트리 (표시 깊이 제한)"));
            sb.Append(run.Display.Format(matrix.FeatureNames, matrix.ClassNames, 4));
            if (run.Display.Depth > 4)
                sb.AppendLine(LT($"Full depth is {run.Display.Depth}. Deeper nodes are omitted.", $"전체 깊이는 {run.Display.Depth}입니다. 더 깊은 노드는 생략했습니다."));
            return sb.ToString();
        }

        private string FormatTreeRegression(FeatureMatrix matrix, TreeRegressionResult run, DecisionTreeOptions options)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            AppendTreeMethod(sb, run.Display, options, regression: true);
            AppendEvalScheme(sb, run.Scheme, run.Seed, run.TestFraction, run.Folds, run.TrainRows, run.TestRows, stratified: false);
            AppendRegressionMetrics(sb, run.Metrics, run.BaselineRmse);
            sb.AppendLine(run.DisplayUsesAllRows
                ? LT("Tree and importances below are fit on all used rows, not on an evaluation split.",
                     "아래 트리와 중요도는 평가 분할이 아니라 사용 행 전체에 적합한 것입니다.")
                : LT("Tree and importances below are the holdout training fit, not a refit on the test rows.",
                     "아래 트리와 중요도는 홀드아웃 학습 적합입니다. 시험 행으로 다시 적합하지 않았습니다."));
            AppendImportances(sb, matrix.FeatureNames, run.Display.Importances, LT("Impurity decrease (MDI), normalized to sum to 1.", "불순도 감소(MDI), 합이 1이 되도록 정규화."));
            sb.AppendLine(LT("Tree (display depth limited)", "트리 (표시 깊이 제한)"));
            sb.Append(run.Display.Format(matrix.FeatureNames, null, 4));
            if (run.Display.Depth > 4)
                sb.AppendLine(LT($"Full depth is {run.Display.Depth}. Deeper nodes are omitted.", $"전체 깊이는 {run.Display.Depth}입니다. 더 깊은 노드는 생략했습니다."));
            return sb.ToString();
        }

        private string FormatForestClassification(FeatureMatrix matrix, ForestClassificationResult run, RandomForestOptions options)
        {
            var sb = new StringBuilder();
            AppendScopeAndClasses(sb, matrix, run.Evaluation, classification: true);
            AppendForestMethod(sb, run.Model, options, regression: false);
            AppendClassificationMetrics(sb, matrix, run.Evaluation);
            sb.AppendLine(run.ModelUsesAllRows
                ? LT("Importances and OOB below are from a forest fit on all used rows, separate from the cross-validation fits.",
                     "아래 중요도와 OOB는 교차검증 적합과 별개로, 사용 행 전체에 적합한 포레스트입니다.")
                : LT("Importances and OOB below are from the holdout training forest, not a refit on the test rows.",
                     "아래 중요도와 OOB는 홀드아웃 학습 포레스트입니다. 시험 행으로 다시 적합하지 않았습니다."));
            AppendImportances(sb, matrix.FeatureNames, run.Model.Importances,
                LT("Mean of per-tree normalized impurity decrease (MDI). Not permutation importance.",
                   "트리별 정규화 불순도 감소(MDI)의 평균입니다. 순열 중요도가 아닙니다."));
            return sb.ToString();
        }

        private string FormatForestRegression(FeatureMatrix matrix, ForestRegressionResult run, RandomForestOptions options)
        {
            var sb = new StringBuilder();
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            AppendForestMethod(sb, run.Model, options, regression: true);
            AppendEvalScheme(sb, run.Scheme, run.Seed, run.TestFraction, run.Folds, run.TrainRows, run.TestRows, stratified: false);
            AppendRegressionMetrics(sb, run.Metrics, run.BaselineRmse);
            sb.AppendLine(run.ModelUsesAllRows
                ? LT("Importances and OOB below are from a forest fit on all used rows, separate from the cross-validation fits.",
                     "아래 중요도와 OOB는 교차검증 적합과 별개로, 사용 행 전체에 적합한 포레스트입니다.")
                : LT("Importances and OOB below are from the holdout training forest, not a refit on the test rows.",
                     "아래 중요도와 OOB는 홀드아웃 학습 포레스트입니다. 시험 행으로 다시 적합하지 않았습니다."));
            AppendImportances(sb, matrix.FeatureNames, run.Model.Importances,
                LT("Mean of per-tree normalized impurity decrease (MDI). Not permutation importance.",
                   "트리별 정규화 불순도 감소(MDI)의 평균입니다. 순열 중요도가 아닙니다."));
            return sb.ToString();
        }

        private string FormatSvm(FeatureMatrix matrix, SvmClassificationResult run, SvmOptions options)
        {
            var sb = new StringBuilder();
            var model = run.Display;
            AppendScopeAndClasses(sb, matrix, run.Evaluation, classification: true);
            sb.AppendLine(LT($"Scaling: {ScalingLabel(run.Evaluation.Scaling)}, fit on training rows only.",
                $"스케일링: {ScalingLabel(run.Evaluation.Scaling)}, 학습 행으로만 적합."));
            string kernel = model.Kernel == SvmKernel.Linear ? LT("linear", "선형") : "RBF";
            sb.AppendLine(LT(
                $"Method: {model.Solver} SVM, kernel {kernel}, C = {options.C.ToString("G6", CultureInfo.InvariantCulture)}, gamma = {model.Gamma.ToString("G6", CultureInfo.InvariantCulture)} ({options.GammaMode}). Multiclass: {model.Multiclass}.",
                $"방법: {model.Solver} SVM, 커널 {kernel}, C = {options.C.ToString("G6", CultureInfo.InvariantCulture)}, gamma = {model.Gamma.ToString("G6", CultureInfo.InvariantCulture)} ({options.GammaMode}). 다중 클래스: {model.Multiclass}."));
            if (model.Solver == "SMO")
                sb.AppendLine(LT(
                    "Solver: libsvm-style SMO (second-order working set, no shrinking). Binary decision values use the sklearn sign (positive → second sorted class). Kernel training is O(n²).",
                    "솔버: libsvm식 SMO(2차 작업 집합, shrinking 없음). 이진 결정값 부호는 sklearn과 같습니다(양수 → 정렬상 두 번째 클래스). 커널 학습은 O(n²)입니다."));
            else
                sb.AppendLine(LT(
                    "Solver: dual coordinate descent on all presented rows (no kernel matrix). The intercept is an L2-regularized constant feature, not libsvm's unregularized bias. Decision values are not comparable to sklearn SVC. Multiclass is one-vs-rest.",
                    "솔버: 제시된 전 행에 대한 쌍대 좌표 강하(커널 행렬 없음). 절편은 L2 정규화된 상수 특성이며 libsvm의 비정규 편향이 아닙니다. 결정값은 sklearn SVC와 비교할 수 없습니다. 다중 클래스는 one-vs-rest입니다."));
            if (model.Sampled)
                sb.AppendLine(LT(
                    $"Training sample: stratified {model.RowsUsed:N0} of {model.RowsPresented:N0} rows (cap {options.MaxTrainingRows:N0}, seed {options.Seed}). Gamma was computed on that sample.",
                    $"학습 표본: {model.RowsPresented:N0}행 중 층화 {model.RowsUsed:N0}행(상한 {options.MaxTrainingRows:N0}, 시드 {options.Seed}). gamma는 그 표본에서 계산했습니다."));
            else
                sb.AppendLine(LT($"Training rows used by the displayed model: {model.RowsUsed:N0} (no row cap applied).",
                    $"표시 모형이 쓴 학습 행: {model.RowsUsed:N0} (행 상한 미적용)."));
            if (!model.Converged)
                sb.AppendLine(LT(
                    $"Not converged within {options.MaxIterations:N0} SMO iterations (ran {model.Iterations:N0}). Support vectors and the decision function are the unfinished dual solution, not a claim of optimality.",
                    $"SMO가 {options.MaxIterations:N0}회 안에 수렴하지 않았습니다(실행 {model.Iterations:N0}회). 서포트 벡터와 결정함수는 끝내지 못한 쌍대해이며 최적이라는 뜻이 아닙니다."));
            int evalCap = options.MaxEvaluationRows <= 0 ? SupportVectorMachine.DefaultMaxEvaluationRows : options.MaxEvaluationRows;
            sb.AppendLine(run.EvaluationSampled
                ? LT($"Evaluation sample: stratified {run.EvaluationRowsScored:N0} of {run.EvaluationRowsPresented:N0} rows (cap {evalCap:N0}, seed {run.Evaluation.Seed}). Metrics are on this sample, not the full split.",
                     $"평가 표본: {run.EvaluationRowsPresented:N0}행 중 층화 {run.EvaluationRowsScored:N0}행(상한 {evalCap:N0}, 시드 {run.Evaluation.Seed}). 지표는 전체 분할이 아니라 이 표본 기준입니다.")
                : LT($"Evaluation scored all {run.EvaluationRowsPresented:N0} rows (cap {evalCap:N0}, not applied).",
                     $"평가: {run.EvaluationRowsPresented:N0}행을 모두 점수화했습니다(상한 {evalCap:N0}, 미적용)."));
            sb.AppendLine(run.DisplayUsesAllRows
                ? LT("Support counts and gamma below are from a fit on all used rows after a scaler fit on those rows, separate from the per-fold training scalers.",
                     "아래 서포트 개수와 gamma는 겹별 학습 스케일러가 아니라, 사용 행 전체로 적합한 스케일러를 적용한 뒤의 모형입니다.")
                : LT("Support counts and gamma below are from the holdout training fit (scaler fit on training rows only).",
                     "아래 서포트 개수와 gamma는 홀드아웃 학습 적합입니다(스케일러는 학습 행으로만 적합)."));
            AppendClassificationMetrics(sb, matrix, run.Evaluation);
            if (model.Solver == "SMO")
            {
                var names = matrix.ClassNames ?? Array.Empty<string>();
                var table = new TextTable(LT("Class", "클래스"), LT("Support vectors", "서포트 벡터"));
                int show = Math.Min(names.Count, model.SupportPerClass.Length);
                for (int c = 0; c < show; c++)
                    table.AddRow(names[c], StatFormat.Int(model.SupportPerClass[c]));
                sb.AppendLine(LT($"Support vectors: {model.SupportVectorCount:N0} (a row counted once if it is a support vector in any pair).",
                    $"서포트 벡터: {model.SupportVectorCount:N0} (어느 쌍에서든 서포트 벡터면 한 번만 셉니다)."));
                sb.Append(table.Render());
            }
            return sb.ToString();
        }

        private void AppendScopeAndClasses(StringBuilder sb, FeatureMatrix matrix, ClassifierEvalResult eval, bool classification)
        {
            sb.AppendLine(AdvScope(matrix.RowsRead, matrix.RowCount, matrix.RowsDropped));
            sb.AppendLine();
            sb.AppendLine(LT($"Features: {FeatureList(matrix.FeatureNames)}", $"특성: {FeatureList(matrix.FeatureNames)}"));
            if (classification)
                sb.AppendLine(LT($"Classes: {FeatureList(matrix.ClassNames ?? Array.Empty<string>())}",
                    $"클래스: {FeatureList(matrix.ClassNames ?? Array.Empty<string>())}"));
            AppendEvalScheme(sb, eval.Scheme, eval.Seed, eval.TestFraction, eval.Folds, eval.TrainRows, eval.TestRows, stratified: true);
        }

        private static void AppendEvalScheme(StringBuilder sb, EvalScheme scheme, int seed, double testFraction, int folds, int train, int test, bool stratified)
        {
            string how = stratified ? LT("stratified", "층화") : LT("random", "무작위");
            sb.AppendLine(scheme == EvalScheme.Holdout
                ? LT($"Evaluation: {how} holdout, test fraction {testFraction.ToString("0.00", CultureInfo.InvariantCulture)}, seed {seed}. Train {train:N0} · test {test:N0}.",
                     $"평가: {how} 홀드아웃, 시험 비율 {testFraction.ToString("0.00", CultureInfo.InvariantCulture)}, 시드 {seed}. 학습 {train:N0} · 시험 {test:N0}.")
                : LT($"Evaluation: {how} {folds}-fold, seed {seed}. Each of {test:N0} rows is predicted once.",
                     $"평가: {how} {folds}-겹, 시드 {seed}. {test:N0}행을 각각 한 번씩 예측했습니다."));
        }

        private void AppendTreeMethod(StringBuilder sb, DecisionTreeModel model, DecisionTreeOptions options, bool regression)
        {
            string crit = regression ? "MSE" : options.Criterion == TreeCriterion.Entropy ? LT("entropy", "엔트로피") : LT("Gini", "지니");
            string splits = model.ApproximateSplits
                ? LT($"quantile bins (up to {model.BinCount}), approximate thresholds — not exact CART cuts",
                     $"분위 구간(최대 {model.BinCount}), 임계값은 근사 — 정확한 CART 절단이 아닙니다")
                : LT("exact sorted splits (float32 midpoint, FEATURE_THRESHOLD 1e-7). Ties: lowest feature index, then lowest threshold.",
                     "정확 정렬 분할(float32 중간점, FEATURE_THRESHOLD 1e-7). 동점: 가장 작은 특성 인덱스, 그다음 낮은 임계값.");
            sb.AppendLine(LT(
                $"Method: CART, criterion {crit}, max depth {options.MaxDepth}, min samples split {options.MinSamplesSplit}, min samples leaf {options.MinSamplesLeaf}. {splits}",
                $"방법: CART, 기준 {crit}, 최대 깊이 {options.MaxDepth}, 분할 최소 표본 {options.MinSamplesSplit}, 잎 최소 표본 {options.MinSamplesLeaf}. {splits}"));
            sb.AppendLine(LT($"Nodes {model.NodeCount:N0} · leaves {model.LeafCount:N0} · depth {model.Depth}.",
                $"노드 {model.NodeCount:N0} · 잎 {model.LeafCount:N0} · 깊이 {model.Depth}."));
            if (model.LeafCount == 1)
                sb.AppendLine(LT("No split improved impurity enough to grow the tree. The model is a single leaf.",
                    "불순도를 충분히 낮추는 분할이 없어 트리가 자라지 않았습니다. 모형은 잎 하나입니다."));
        }

        private void AppendForestMethod(StringBuilder sb, RandomForestModel model, RandomForestOptions options, bool regression)
        {
            sb.AppendLine(LT(
                $"Method: random forest, {model.TreeCount:N0} trees, max depth {options.MaxDepth}, {model.MaxFeatures} features per split, bootstrap, seed {model.Seed}. Importance: {RandomForest.ImportanceMethod} (mean of per-tree normalized impurity decrease), not permutation.",
                $"방법: 랜덤 포레스트, 트리 {model.TreeCount:N0}개, 최대 깊이 {options.MaxDepth}, 분할당 특성 {model.MaxFeatures}개, 부트스트랩, 시드 {model.Seed}. 중요도: {RandomForest.ImportanceMethod}(트리별 정규화 불순도 감소의 평균), 순열 중요도 아님."));
            if (model.ApproximateSplits)
                sb.AppendLine(LT($"Splits: quantile bins (up to {model.BinCount}), computed once on the forest training sample. Thresholds are approximate.",
                    $"분할: 분위 구간(최대 {model.BinCount}), 포레스트 학습 표본에서 한 번 계산. 임계값은 근사입니다."));
            else
                sb.AppendLine(LT("Splits: exact sorted CART on each bootstrap sample.",
                    "분할: 각 부트스트랩 표본에서 정확 정렬 CART."));
            if (model.Sampled)
                sb.AppendLine(LT(
                    $"Row cap: stratified/random sample of {model.TrainingRows:N0} of {model.SourceRows:N0} rows (cap {options.MaxTrainingRows:N0}, seed {model.Seed}). OOB and importances refer to that sample, not the full view.",
                    $"행 상한: {model.SourceRows:N0}행 중 {model.TrainingRows:N0}행 표본(상한 {options.MaxTrainingRows:N0}, 시드 {model.Seed}). OOB와 중요도는 전체 뷰가 아니라 그 표본 기준입니다."));
            else
                sb.AppendLine(LT($"Training rows: {model.TrainingRows:N0} (no row cap applied).",
                    $"학습 행: {model.TrainingRows:N0} (행 상한 미적용)."));
            if (double.IsNaN(model.OobScore))
                sb.AppendLine(LT("OOB estimate unavailable: no row was left out of every bootstrap.",
                    "OOB 추정을 낼 수 없습니다. 모든 부트스트랩에 들어간 행만 있습니다."));
            else if (regression)
                sb.AppendLine(LT(
                    $"OOB on {model.OobRows:N0} of {model.TrainingRows:N0} training rows: R² {StatFormat.F(model.OobScore)}, RMSE {StatFormat.F(model.OobError)}. This is not the holdout score.",
                    $"학습 {model.TrainingRows:N0}행 중 OOB {model.OobRows:N0}행: R² {StatFormat.F(model.OobScore)}, RMSE {StatFormat.F(model.OobError)}. 홀드아웃 점수가 아닙니다."));
            else
                sb.AppendLine(LT(
                    $"OOB on {model.OobRows:N0} of {model.TrainingRows:N0} training rows: accuracy {StatFormat.F(model.OobScore)}, error {StatFormat.F(model.OobError)}. This is not the holdout score.",
                    $"학습 {model.TrainingRows:N0}행 중 OOB {model.OobRows:N0}행: 정확도 {StatFormat.F(model.OobScore)}, 오차 {StatFormat.F(model.OobError)}. 홀드아웃 점수가 아닙니다."));
        }

        private void AppendClassificationMetrics(StringBuilder sb, FeatureMatrix matrix, ClassifierEvalResult eval)
        {
            var names = matrix.ClassNames ?? Array.Empty<string>();
            var m = eval.Metrics;
            sb.AppendLine();
            sb.AppendLine($"{LT("Accuracy", "정확도")}  {StatFormat.F(m.Accuracy)}");
            sb.AppendLine($"{LT("Macro F1", "매크로 F1")}  {StatFormat.F(m.MacroF1)}");
            sb.AppendLine($"{LT("Majority-class baseline", "최다 클래스 기준선")}  {StatFormat.F(eval.MajorityBaseline)}");
            sb.AppendLine();
            int show = Math.Min(names.Count, 40);
            var per = new TextTable(LT("Class", "클래스"), LT("Support", "지지"), LT("Precision", "정밀도"), LT("Recall", "재현율"), "F1");
            for (int c = 0; c < show; c++)
                per.AddRow(names[c], StatFormat.Int(m.Support[c]), StatFormat.F(m.Precision[c]), StatFormat.F(m.Recall[c]), StatFormat.F(m.F1[c]));
            sb.Append(per.Render());
            if (names.Count > 0 && names.Count <= 8)
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
            var warnings = new List<string>();
            if (m.Total < 30)
                warnings.Add(LT("Small evaluation set (fewer than 30 predicted rows). Accuracy and F1 are unstable.",
                    "평가 집합이 작습니다(예측 30행 미만). 정확도와 F1이 불안정할 수 있습니다."));
            if (m.Accuracy <= eval.MajorityBaseline + 1e-12)
                warnings.Add(LT("Accuracy does not beat the majority-class baseline. The fit is not evidence of useful discrimination.",
                    "정확도가 최다 클래스 기준선을 넘지 않습니다. 이 적합이 유용한 구분 능력이 있다는 증거가 되지 않습니다."));
            AppendWarnings(sb, warnings);
        }

        private void AppendRegressionMetrics(StringBuilder sb, RegressionMetrics metrics, double baselineRmse)
        {
            sb.AppendLine();
            sb.AppendLine($"RMSE  {StatFormat.F(metrics.Rmse)}");
            sb.AppendLine($"MAE  {StatFormat.F(metrics.Mae)}");
            sb.AppendLine($"R²  {StatFormat.F(metrics.RSquared)}");
            sb.AppendLine(LT($"Baseline RMSE (predict the training-split mean)  {StatFormat.F(baselineRmse)}",
                $"기준 RMSE (학습 분할 평균을 예측)  {StatFormat.F(baselineRmse)}"));
            sb.AppendLine(LT("R² is relative to the evaluation-row mean (sklearn r2_score), not the training mean.",
                "R²는 학습 평균이 아니라 평가 행 평균 기준입니다(sklearn r2_score)."));
            if (metrics.Total < 30)
            {
                var warnings = new List<string>
                {
                    LT("Small evaluation set (fewer than 30 predicted rows). RMSE and R² are unstable.",
                       "평가 집합이 작습니다(예측 30행 미만). RMSE와 R²가 불안정할 수 있습니다."),
                };
                AppendWarnings(sb, warnings);
            }
        }

        private void AppendImportances(StringBuilder sb, IReadOnlyList<string> names, double[] importances, string note)
        {
            sb.AppendLine();
            sb.AppendLine(note);
            int n = Math.Min(names.Count, importances.Length);
            var order = Enumerable.Range(0, n).OrderByDescending(i => importances[i]).Take(20).ToArray();
            var table = new TextTable(LT("Feature", "특성"), LT("Importance", "중요도"));
            foreach (int i in order)
                table.AddRow(names[i], StatFormat.F(importances[i], 6));
            sb.Append(table.Render());
            if (n > 20)
                sb.AppendLine(LT($"… {n - 20} more features omitted.", $"… 특성 {n - 20}개 생략."));
            sb.AppendLine();
        }
    }
}
