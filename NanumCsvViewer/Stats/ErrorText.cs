using System.Text.RegularExpressions;

namespace NanumCsvViewer.Stats
{
    /// <summary>
    /// 엔진(WinForms 무관)이 던지는 영어 사용자 오류 메시지를 한국어 화면에서 한국어로 보여 주는 변환기.
    /// 엔진 코드와 테스트는 영어 메시지를 그대로 유지하고, UI가 표시 직전에 Localize를 거친다.
    /// 매칭되지 않는 메시지는 원문 그대로(정보 손실 없음).
    /// </summary>
    public static class ErrorText
    {
        private static readonly (Regex Pattern, string Korean)[] Rules = Build(new (string, string)[]
        {
            // ---- 공통 입력·행 수집
            (@"Select at least one feature column\.", "특성 컬럼을 하나 이상 선택하세요."),
            (@"The target column cannot also be a feature\.", "목표 컬럼은 특성에 포함할 수 없습니다."),
            (@"The target column is not in the table\.", "목표 컬럼이 표에 없습니다."),
            (@"No complete rows: every row has a missing or non-numeric value in the (model|selected) columns\.", "완전한 행이 없습니다: 모든 행에서 선택한 컬럼에 결측이거나 수치가 아닌 값이 있습니다."),
            (@"The offset must be finite\.", "오프셋은 유한한 값이어야 합니다."),
            (@"Frequency weights must be positive integers\.", "빈도 가중치는 양의 정수여야 합니다."),
            (@"The total frequency weight is too large\.", "빈도 가중치의 합이 너무 큽니다."),
            (@"Variance weights must be positive\.", "분산 가중치는 양수여야 합니다."),
            (@"Trials are only valid for the binomial family\.", "시행 수는 이항 분포족에서만 쓸 수 있습니다."),
            (@"Use either trials or variance weights, not both\.", "시행 수와 분산 가중치는 함께 쓸 수 없습니다."),
            (@"Trials must be positive integers\.", "시행 수는 양의 정수여야 합니다."),
            (@"Successes must be integers between 0 and the trials\.", "성공 횟수는 0 이상 시행 수 이하의 정수여야 합니다."),
            (@"Variance weights are not supported for the binomial family\. Use a trials column\.", "이항 분포족은 분산 가중치를 지원하지 않습니다. 시행 수 열을 쓰세요."),
            (@"The target has only one class in the complete rows\.", "완전한 행에서 목표의 클래스가 하나뿐입니다."),
            (@"Unknown column '(.+)'\.", "알 수 없는 컬럼입니다: '$1'"),
            (@"Column name '(.+)' is ambiguous \(differs only by case\)\.", "컬럼 이름 '$1'이(가) 모호합니다(대소문자만 다른 컬럼이 여러 개)."),
            (@"'(.+)' has only one level in the complete rows; it cannot be a categorical predictor\.", "'$1'은(는) 완전한 행에서 수준이 하나뿐이라 범주 설명변수로 쓸 수 없습니다."),
            (@"'(.+)' has more than ([\d,]+) levels\. Use it as numeric, filter rows, or choose another column\.", "'$1'의 수준이 $2개를 넘습니다. 수치로 쓰거나 행을 거르거나 다른 컬럼을 고르세요."),
            (@"'(.+)' has more than ([\d,]+) levels; it cannot be one-hot encoded\.", "'$1'의 수준이 $2개를 넘어 원-핫 인코딩할 수 없습니다."),
            (@"'(.+)' has more than ([\d,]+) levels\.", "'$1'의 수준이 $2개를 넘습니다."),
            (@"Target '(.+)' has more than ([\d,]+) classes\.", "목표 '$1'의 클래스가 $2개를 넘습니다."),
            (@"The model would have more than ([\d,]+) columns \(term '(.+)'\)\. Reduce categorical levels or interactions\.", "모형 열이 $1개를 넘게 됩니다(항 '$2'). 범주 수준이나 상호작용을 줄이세요."),
            (@"The model has no columns \(no intercept and no terms\)\.", "모형에 열이 없습니다(절편도 항도 없음)."),
            (@"Interaction '(.+)' needs the lower-order term '(.+)' in the model\..*", "상호작용 '$1'에는 하위 항 '$2'이(가) 모형에 있어야 합니다. '*'로 쓰거나 '$2'을(를) 추가하세요."),
            (@"Response '(.+)' has more than two distinct values; a binary outcome is required\.", "응답 '$1'의 값이 둘을 넘습니다. 이진 결과가 필요합니다."),
            (@"Response '(.+)' has only one value in the complete rows; a binary outcome needs two\.", "응답 '$1'은(는) 완전한 행에서 값이 하나뿐입니다. 이진 결과에는 두 값이 필요합니다."),
            (@"Response '(.+)' has (\d+) distinct values; a binary outcome is required\.", "응답 '$1'에 서로 다른 값이 $2개 있습니다. 이진 결과가 필요합니다."),
            (@"Event level '(.+)' does not occur in '(.+)'\.", "사건 수준 '$1'은(는) '$2'에 나타나지 않습니다."),
            (@"Response '(.+)' is not numeric\. A general linear model needs a numeric response\.", "응답 '$1'이(가) 수치가 아닙니다. 일반선형모형에는 수치 응답이 필요합니다."),
            (@"The response '(.+)' cannot also be a predictor\.", "응답 '$1'은(는) 설명변수가 될 수 없습니다."),
            (@"A class index is outside 0\.\.K-1\.", "클래스 인덱스가 0..K-1 범위를 벗어났습니다."),
            (@"Regression target has a non-finite value\.", "회귀 목표에 유한하지 않은 값이 있습니다."),
            (@"The numeric target must be finite\.", "수치 목표는 유한한 값이어야 합니다."),
            (@"Feature values must be finite\.", "특성 값은 유한한 수여야 합니다."),
            (@"Need at least (\d+) complete rows? for K-means\.", "K-means에는 완전한 행이 $1개 이상 필요합니다."),
            (@"Need at least (\d+) complete rows? for (\d+)-fold cross-validation\.", "$2-겹 교차검증에는 완전한 행이 $1개 이상 필요합니다."),
            (@"Need at least (\d+) complete rows?\.", "완전한 행이 $1개 이상 필요합니다."),
            (@"PCA needs at least 2 complete rows\.", "PCA에는 완전한 행이 2개 이상 필요합니다."),
            (@"The training split is empty\.", "학습 분할이 비어 있습니다."),
            (@"A cross-validation fold is empty\. Use fewer folds or more rows\.", "교차검증 겹이 비어 있습니다. 겹 수를 줄이거나 행을 늘리세요."),
            (@"A training split has fewer than 2 classes\..*", "학습 분할의 클래스가 2개 미만입니다. 겹 수를 줄이거나 층화를 쓰거나 클래스별 행을 늘리세요."),
            // ---- 설정 검증
            (@"Seed must be a positive integer so .+", "시드는 1 이상의 정수여야 합니다(재현 가능한 결과를 위해)."),
            (@"Test fraction must be between 0 and 1 \(exclusive\)\.", "시험 비율은 0과 1 사이(양 끝 제외)여야 합니다."),
            (@"k-fold needs at least 2 folds\.", "k-겹 교차검증은 2겹 이상이어야 합니다."),
            (@"k must be at least (\d+)\.", "k는 $1 이상이어야 합니다."),
            (@"k \((\d+)\) exceeds the number of complete rows \((\d+)\)\.", "k($1)가 완전한 행 수($2)를 넘습니다."),
            (@"k \((\d+)\) must be between 1 and the training row count \((\d+)\)\.", "k($1)는 1과 학습 행 수($2) 사이여야 합니다."),
            (@"Restarts must be at least 1\.", "재시작 횟수는 1 이상이어야 합니다."),
            (@"Max iterations cannot be negative\.", "최대 반복 횟수는 음수일 수 없습니다."),
            (@"Max iterations must be at least 1\.", "최대 반복 횟수는 1 이상이어야 합니다."),
            (@"Silhouette sample size must be at least 2\.", "실루엣 표본 크기는 2 이상이어야 합니다."),
            (@"The initialization sample must have at least k rows\.", "초기화 표본은 k행 이상이어야 합니다."),
            (@"n_estimators must be at least 1\.", "추정기 수는 1 이상이어야 합니다."),
            (@"Learning rate must be a finite number greater than 0\.", "학습률은 0보다 큰 유한한 수여야 합니다."),
            (@"Max depth cannot be negative.*", "최대 깊이는 음수일 수 없습니다(0은 제한 없음)."),
            (@"min_samples_leaf must be at least 1\.|Min samples per leaf must be at least 1\.", "잎 최소 표본 수는 1 이상이어야 합니다."),
            (@"min_samples_split must be at least 2\.", "분할 최소 표본 수는 2 이상이어야 합니다."),
            (@"Bin count must be at least 2\.", "구간 수는 2 이상이어야 합니다."),
            (@"Max bins must be between 2 and 255\.", "최대 구간 수는 2~255 사이여야 합니다."),
            (@"max_features cannot be negative\.", "분할당 특성 수는 음수일 수 없습니다."),
            (@"Max leaf nodes must be 0 \(no limit\) or at least 2\.", "최대 잎 수는 0(제한 없음) 또는 2 이상이어야 합니다."),
            (@"L2 regularization must be a finite number ≥ 0\.", "L2 정규화는 0 이상의 유한한 수여야 합니다."),
            (@"Validation fraction must be between 0 and 1 \(exclusive\)\.", "검증 비율은 0과 1 사이(양 끝 제외)여야 합니다."),
            (@"The early-stopping validation split is empty\..*", "조기 종료 검증 분할이 비어 있습니다. 검증 비율을 낮추거나 조기 종료를 끄세요."),
            (@"Early stopping left fewer than 2 training rows\..*", "조기 종료로 학습 행이 2개 미만이 됩니다. 검증 비율을 낮추거나 조기 종료를 끄세요."),
            (@"The training row cap must be at least 2\.", "학습 행 상한은 2 이상이어야 합니다."),
            (@"The search row budget must be at least 4\.", "탐색 행 예산은 4 이상이어야 합니다."),
            (@"The refit row cap must be at least 4\.", "재적합 행 상한은 4 이상이어야 합니다."),
            (@"Time budget cannot be negative\.", "시간 예산은 음수일 수 없습니다."),
            (@"Accuracy or macro F1 is the classification metric\..*", "분류 지표는 정확도 또는 매크로 F1입니다. RMSE·R²는 수치 목표용입니다."),
            (@"RMSE or R² is the regression metric\..*", "회귀 지표는 RMSE 또는 R²입니다. 정확도·매크로 F1은 클래스 목표용입니다."),
            (@"Every AutoML configuration failed\. (.*)", "모든 AutoML 설정이 실패했습니다. $1"),
            (@"AutoML did not fit any configuration\.", "AutoML이 적합한 설정이 없습니다."),
            // ---- 알고리즘별
            (@"AdaBoost base learner is worse than random\..*", "AdaBoost 기본 학습기가 무작위보다 나빠 앙상블을 적합할 수 없습니다."),
            (@"AdaBoost stopped before any estimator was kept\.", "AdaBoost가 추정기를 하나도 유지하기 전에 멈췄습니다."),
            (@"AdaBoost sample weights are all zero\.", "AdaBoost 표본 가중치가 모두 0입니다."),
            (@"K-means failed: fewer than (\d+) distinct points.*", "K-means 실패: 서로 다른 점이 $1개 미만입니다. 분할을 보고하지 않습니다."),
            (@"K-means failed.*", "K-means 실패. 분할을 보고하지 않습니다."),
            (@"LDA needs at least 2 classes\.", "LDA에는 클래스가 2개 이상 필요합니다."),
            (@"The number of samples must be greater than the number of classes\.", "표본 수가 클래스 수보다 커야 합니다."),
            (@"Class (\d+) has no training rows\.", "클래스 $1에 학습 행이 없습니다."),
            (@"Within-class covariance has rank 0.*", "급내 공분산의 계수가 0입니다: 특성이 클래스 안에서 변하지 않아 LDA가 판별식을 만들 수 없습니다."),
            (@"Between-class scatter .*", "급간 산포로 판별식을 만들 수 없습니다(클래스가 분리되지 않음)."),
            (@"ANOVA needs at least 2 classes\.", "ANOVA에는 클래스가 2개 이상 필요합니다."),
            (@"A class has no rows for ANOVA\.", "ANOVA에서 행이 없는 클래스가 있습니다."),
            (@"ANOVA needs more rows than classes\.", "ANOVA에는 클래스보다 많은 행이 필요합니다."),
            (@"F-regression needs at least 3 complete rows\.", "F-회귀에는 완전한 행이 3개 이상 필요합니다."),
            (@"The numeric target does not vary in the complete rows\.", "수치 목표가 완전한 행에서 변하지 않습니다."),
            (@"A numeric target is scored with Pearson.*", "수치 목표는 Pearson/F-회귀로 점수화하며 수치 특성이 하나 이상 필요합니다. 범주 특성은 수치 목표에서 점수화하지 않습니다."),
            (@"Link '(.+)' is not valid for the (.+) family\.", "연결 함수 '$1'은(는) $2 분포족에 쓸 수 없습니다."),
            (@"Not enough complete rows to estimate the model.*", "모형을 추정하기에 완전한 행이 부족합니다(모수보다 행이 많아야 함)."),
            (@"The model could not be started \(deviance is undefined\)\..*", "모형을 시작할 수 없습니다(이탈도가 정의되지 않음). 응답이 분포족·연결 함수와 맞는지 확인하세요."),
            (@"Binomial response must be 0/1\..*", "이항 응답은 0/1이어야 합니다. 두 수준의 이진 결과가 필요합니다."),
            (@"Poisson response must be non-negative\.", "포아송 응답은 0 이상이어야 합니다."),
            (@"Gamma response must be strictly positive\.", "감마 응답은 0보다 커야 합니다."),
            (@"Gaussian response must be finite\.", "가우시안 응답은 유한한 값이어야 합니다."),
            (@"The model has no estimable coefficients.*", "추정 가능한 계수가 없습니다(모든 열이 별칭)."),
            (@"ANCOVA requires an intercept.*", "ANCOVA에는 절편이 필요합니다(보정 평균 추정을 위해)."),
            (@"ANCOVA expects main effects only.*", "ANCOVA는 주효과만 받습니다(요인 1개 + 공변량). 기울기 검정용 상호작용은 내부에서 추가합니다."),
            (@"'(.+)' must be a categorical factor with at least two levels in the complete rows\.", "'$1'은(는) 완전한 행에서 수준이 둘 이상인 범주 요인이어야 합니다."),
            (@"'(.+)' is not a term in the model\.", "'$1'은(는) 모형의 항이 아닙니다."),
            (@"'(.+)' is not a numeric covariate\. ANCOVA covariates must be numeric\.", "'$1'은(는) 수치 공변량이 아닙니다. ANCOVA 공변량은 수치여야 합니다."),
            (@"ANCOVA needs at least one categorical factor with two or more levels in the complete rows\.", "ANCOVA에는 완전한 행에서 수준이 둘 이상인 범주 요인이 하나 이상 필요합니다."),
            (@"ANCOVA requires at least one numeric covariate\.", "ANCOVA에는 수치 공변량이 하나 이상 필요합니다."),
            // ---- 혼합모형
            (@"The grouping column is not in the table\.", "그룹 컬럼이 표에 없습니다."),
            (@"The grouping column cannot be the response\.", "그룹 컬럼은 응답일 수 없습니다."),
            (@"At most (\d+) random slopes are supported.*", "임의 기울기는 최대 $1개까지 지원합니다."),
            (@"Random slope '(.+)' must be numeric\.", "임의 기울기 '$1'은(는) 수치여야 합니다."),
            (@"Random slope '(.+)' is the response\.", "임의 기울기 '$1'은(는) 응답입니다."),
            (@"Random slope '(.+)' is the grouping column\.", "임의 기울기 '$1'은(는) 그룹 컬럼입니다."),
            (@"Random slope '(.+)' is listed twice\.", "임의 기울기 '$1'이(가) 두 번 지정됐습니다."),
            (@"A mixed model needs at least two groups in the complete rows\.", "혼합모형에는 완전한 행에서 그룹이 2개 이상 필요합니다."),
            (@"REML is not defined when the number of .*", "고정효과 수 이하의 행에서는 REML을 쓸 수 없습니다. ML을 쓰거나 항을 줄이세요."),
            (@"Fixed-effects columns are linearly dependent \((.+)\)\..*", "고정효과 열이 선형 종속입니다($1). 별칭 항을 제거하세요."),
            (@"Random-effects columns are linearly dependent \((.+)\)\..*", "임의효과 열이 선형 종속입니다($1). 기울기가 상수이거나 절편의 복사본일 수 있습니다."),
            // ---- 모형 저장·ONNX·기타
            (@"The model file is empty\.", "모형 파일이 비어 있습니다."),
            (@"The model file was not found\.", "모형 파일을 찾을 수 없습니다."),
            (@"The model file is not valid JSON: (.*)", "모형 파일이 올바른 JSON이 아닙니다: $1"),
            (@"The model file is ([\d,]+) bytes, over the ([\d,]+) byte limit\. The model was not loaded\.", "모형 파일이 $1바이트로 한도($2바이트)를 넘어 불러오지 않았습니다."),
            (@"Unrecognized model format '(.*)'\. Expected (.+)\.", "알 수 없는 모형 형식 '$1'입니다. $2 형식이어야 합니다."),
            (@"Unsupported model format version (\d+)\..*", "지원하지 않는 모형 형식 버전 $1입니다."),
            (@"Feature count (\d+) does not match the model \((\d+)\)\.", "특성 수 $1이(가) 모형($2)과 맞지 않습니다."),
            (@"This analysis exceeds its memory budget\..*", "이 분석이 메모리 예산을 초과합니다. 행을 거르거나 컬럼을 줄여 다시 시도하세요."),
            (@"The original file is never overwritten\..*", "원본 파일은 덮어쓰지 않습니다. 다른 파일 이름을 고르세요."),
            (@"Indexing is not complete\.", "인덱싱이 아직 끝나지 않았습니다."),
            (@"Enter a formula.*", "식을 입력하세요. 예: y ~ x + C(group)"),
            (@"Formula is empty\. \(position (\d+)\)", "식이 비어 있습니다."),
            (@"Expected '~' after the response\. \(position (\d+)\)", "응답 뒤에 '~'가 필요합니다. (위치 $1)"),
            (@"Expected a term\. \(position (\d+)\)", "항이 필요합니다. (위치 $1)"),
            (@"Expected a variable name\. \(position (\d+)\)", "변수 이름이 필요합니다. (위치 $1)"),
            (@"Unexpected '(.)'\. Use \+ to add terms\. \(position (\d+)\)", "'$1'을(를) 쓸 수 없습니다. 항은 +로 더합니다. (위치 $2)"),
            (@"Unexpected '(.)'\. \(position (\d+)\)", "예상하지 못한 문자 '$1'입니다. (위치 $2)"),
            (@"Parentheses are not supported.*\(position (\d+)\)", "괄호 묶음은 지원하지 않습니다. 항을 풀어 쓰세요(예: a + b + a:b). (위치 $1)"),
            (@"Function '(.+)\(…\)' is not supported\. Only C\(column\) is available\. \(position (\d+)\)", "함수 '$1(…)'은(는) 지원하지 않습니다. C(컬럼)만 쓸 수 있습니다. (위치 $2)"),
            (@"Missing closing '(.)'\. \(position (\d+)\)", "닫는 '$1'이(가) 없습니다. (위치 $2)"),
            (@"Empty column name\. \(position (\d+)\)", "컬럼 이름이 비어 있습니다. (위치 $1)"),
            (@"Expected '\)' to close C\(…\)\. \(position (\d+)\)", "C(…)를 닫는 ')'가 필요합니다. (위치 $1)"),
            (@"Only 0 or 1 may appear as numbers.*\(position (\d+)\)", "숫자는 0 또는 1만 쓸 수 있습니다(절편 제어). (위치 $1)"),
        });

        private static (Regex, string)[] Build((string Pattern, string Korean)[] rules)
            => rules.Select(r => (new Regex("^(?:" + r.Pattern + ")$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline), r.Korean)).ToArray();

        /// <summary>한국어 화면이면 알려진 영어 오류 메시지를 한국어로, 아니면(또는 모르는 메시지면) 원문 그대로.</summary>
        public static string Localize(string message)
        {
            if (string.IsNullOrEmpty(message) || Loc.CurrentLanguage != "ko") return message;
            string trimmed = message.Trim();
            foreach (var (pattern, korean) in Rules)
            {
                var m = pattern.Match(trimmed);
                if (m.Success) return m.Result(korean);
            }
            return message;
        }

        /// <summary>테스트용: 현재 언어와 무관하게 한국어 변환을 시도한다. 매칭 실패면 null.</summary>
        internal static string? TryKorean(string message)
        {
            string trimmed = message.Trim();
            foreach (var (pattern, korean) in Rules)
            {
                var m = pattern.Match(trimmed);
                if (m.Success) return m.Result(korean);
            }
            return null;
        }
    }
}
