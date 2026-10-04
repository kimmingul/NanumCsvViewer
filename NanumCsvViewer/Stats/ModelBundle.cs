namespace NanumCsvViewer.Stats
{
    public enum ModelTask { Classification, Regression }

    /// <summary>식 모형 요인의 학습 수준. Levels[0]이 기준(reference) 수준이다.</summary>
    public sealed record ModelFactor(string Variable, IReadOnlyList<string> Levels);

    /// <summary>저장·ONNX가 아는 모형 종류 키. 엔진 객체와 1:1이다.</summary>
    public static class ModelTypes
    {
        public const string Knn = "Knn";
        public const string NaiveBayes = "NaiveBayes";
        public const string Lda = "Lda";
        public const string DecisionTree = "DecisionTree";
        public const string RandomForest = "RandomForest";
        public const string Svm = "Svm";
        public const string GradientBoosting = "GradientBoosting";
        public const string LinearModel = "LinearModel";
        public const string Logistic = "Logistic";
        public const string Glzm = "Glzm";
        public const string AdaBoost = "AdaBoost";
        public const string MultinomialLogistic = "MultinomialLogistic";
    }

    /// <summary>설계행렬/특성행렬의 열 1개가 원본의 어느 컬럼·수준에서 왔는지. Level은 원-핫(범주) 열에서만.</summary>
    public sealed record ModelFeature(string Column, VariableKind Kind, string? Level);

    /// <summary>
    /// 저장·재적용·ONNX 내보내기 가능한 적합 모형 묶음(이슈 #27 Phase 3). 적용 시 입력 행을 Features 순서의
    /// 수치 벡터로 만들고(원-핫 포함) Scaler가 있으면 변환한 뒤 Engine으로 예측한다.
    /// Engine은 각 모듈의 적합 모형 객체(GradientBoostingModel, RandomForestModel, …)이며
    /// 직렬화·예측 어댑터는 ModelStore가 담당한다.
    /// </summary>
    public sealed record ModelBundle
    {
        /// <summary>모형 종류 키(예: "GradientBoosting", "RandomForest", "DecisionTree", "NaiveBayes", "Lda", "Knn", "Svm", "AdaBoost", "MultinomialLogistic", "LinearModel", "Logistic", "Glzm").</summary>
        public required string ModelType { get; init; }
        public required ModelTask Task { get; init; }
        public required IReadOnlyList<ModelFeature> Features { get; init; }
        public FeatureScaler? Scaler { get; init; }
        public required string Target { get; init; }
        /// <summary>분류: 클래스 이름(코드 순서). 회귀: null.</summary>
        public IReadOnlyList<string>? ClassNames { get; init; }
        public required object Engine { get; init; }
        /// <summary>식 기반 모형(GLM·GLzM·로지스틱)의 식 문자열. 특성행렬 모형은 null.</summary>
        public string? Formula { get; init; }
        /// <summary>적합에 쓴 행 수와 평가 요약(보고용, 재현 기록).</summary>
        public int TrainingRows { get; init; }
        public string? EvaluationSummary { get; init; }
        /// <summary>하이퍼파라미터 등 재현 정보(키=값).</summary>
        public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
        /// <summary>식 모형: 요인별 학습 수준(첫 수준이 기준). 적용 시 같은 처치 코딩을 재현한다. 특성행렬 모형은 null.</summary>
        public IReadOnlyList<ModelFactor>? Factors { get; init; }
        /// <summary>식 모형이 절편 열을 포함하는지. 특성행렬 모형은 false.</summary>
        public bool HasIntercept { get; init; }
        /// <summary>식에서 C()로 강제된 범주 변수. 적용 때 파일의 추론 타입과 무관하게 범주로 코딩한다.</summary>
        public IReadOnlyList<string> ForcedCategorical { get; init; } = Array.Empty<string>();
        /// <summary>이항 응답의 [음성, 사건] 수준. 로지스틱·이항 GLzM만.</summary>
        public IReadOnlyList<string>? ResponseLevels { get; init; }


        /// <summary>특성행렬(FeatureMatrixBuilder) 열을 원본 컬럼·수준으로 풀어 ModelFeature 목록을 만든다.</summary>
        public static IReadOnlyList<ModelFeature> FeaturesOf(FeatureMatrix fm, IReadOnlyList<string> headers, Func<int, VariableKind> kindOf)
        {
            var list = new ModelFeature[fm.FeatureCount];
            for (int j = 0; j < fm.FeatureCount; j++)
            {
                int src = fm.SourceColumns[j];
                string column = headers[src];
                var kind = kindOf(src);
                string? level = null;
                if (kind == VariableKind.Categorical)
                {
                    string name = fm.FeatureNames[j];
                    level = name.Length > column.Length + 1 ? name[(column.Length + 1)..] : "";
                }
                list[j] = new ModelFeature(column, kind, level);
            }
            return list;
        }

        /// <summary>특성행렬 모형. Features는 원-핫을 포함한 열 순서이고, 엔진은 그 순서의 수치 벡터를 받는다.</summary>
        public static ModelBundle FromFeatures(
            string modelType, ModelTask task, FeatureMatrix matrix, IReadOnlyList<string> headers,
            Func<int, VariableKind> kindOf, string target, object engine, FeatureScaler? scaler,
            int trainingRows, string? evaluationSummary, IReadOnlyDictionary<string, string>? parameters = null)
            => new()
            {
                ModelType = modelType,
                Task = task,
                Features = FeaturesOf(matrix, headers, kindOf),
                Scaler = scaler is { Method: ScalingMethod.None } ? null : scaler,
                Target = target,
                ClassNames = task == ModelTask.Classification ? matrix.ClassNames : null,
                Engine = engine,
                TrainingRows = trainingRows,
                EvaluationSummary = evaluationSummary,
                Parameters = parameters ?? new Dictionary<string, string>(),
            };

        /// <summary>
        /// 식 모형. Features는 설계행렬 열 이름(절편 포함)이고, 적용 인코딩은 Formula + Factors로 한다.
        /// 범주 상호작용은 열 이름만으로 복원되지 않으므로 요인 수준을 같이 둔다.
        /// </summary>
        public static ModelBundle FromFormula(
            string modelType, ModelTask task, DesignMatrix design, object engine,
            string? evaluationSummary, IReadOnlyDictionary<string, string>? parameters = null,
            IReadOnlyList<string>? classNames = null)
        {
            var features = new ModelFeature[design.ColumnCount];
            for (int j = 0; j < features.Length; j++)
                features[j] = new ModelFeature(design.ColumnNames[j], VariableKind.Numeric, null);
            return new ModelBundle
            {
                ModelType = modelType,
                Task = task,
                Features = features,
                Target = design.Formula.Response,
                ClassNames = classNames ?? (task == ModelTask.Classification ? design.ResponseLevels : null),
                Engine = engine,
                Formula = design.Formula.ToString(),
                HasIntercept = design.HasIntercept,
                ForcedCategorical = design.Formula.ForcedCategorical.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                Factors = design.Factors.Values
                    .Select(f => new ModelFactor(f.Variable, f.Levels.ToArray()))
                    .ToArray(),
                ResponseLevels = design.ResponseLevels,
                TrainingRows = design.RowCount,
                EvaluationSummary = evaluationSummary,
                Parameters = parameters ?? new Dictionary<string, string>(),
            };
        }
    }
}
