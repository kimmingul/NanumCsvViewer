# 고급 통계·머신러닝 모듈 (이슈 #27)

> Phase 1: v1.17.0. Phase 2: v1.18.0 (§5). Phase 3: v1.19.0 (§6). ALGLIB Commercial 4.04(순수 C#) + 자체 엔진.

## 1. 범위 (Phase 1)

최상위 메뉴 **고급 통계**(데이터 품질 옆) — 16개 분석:

| 하위 메뉴 | 분석 | 엔진 | 기준 구현(교차검증) |
|---|---|---|---|
| 모형 | 일반선형모형(GLM) — 식 편집기, 계수·R²·AIC/BIC, Type II ANOVA | `Stats/LinearModel.cs` | statsmodels `ols`, `anova_lm(typ=2)` |
| | 공분산분석(ANCOVA) — 보정 평균, 기울기 동질성, Bonferroni 쌍비교 | 〃 | statsmodels 대비·내포 F |
| | 반복측정 분산분석 — Mauchly, GG/HF ε | `Stats/RepeatedMeasuresAnova.cs` | statsmodels `AnovaRM` + 교과서 공식 |
| | 일반화선형모형(GLzM) — 가우시안·이항·포아송·감마 × 연결 함수 | `Stats/GeneralizedLinearModel.cs` (IRLS) | statsmodels `GLM` |
| | 로지스틱 회귀 — 오즈비, McFadden R², 분류표, AUC, Hosmer–Lemeshow | 〃 | statsmodels `Logit`, sklearn AUC |
| 비모수 검정 | Mann-Whitney U · Wilcoxon 부호순위 · 부호 · Kruskal-Wallis(+Dunn) · Friedman | `Stats/NonparametricTests.cs` | scipy.stats |
| 분류·군집 | K-means · KNN · 나이브 베이즈(가우시안+범주) | `Stats/Clustering.cs`, `Stats/Classifiers.cs` | sklearn |
| 차원축소·특성 | PCA · LDA(판별+분류) · 특성 순위(단변량 필터+BH q) | `Stats/DimensionReduction.cs`, `Stats/FeatureRanking.cs` | sklearn, scipy, statsmodels |

**품질 연동**: 검사 결과 패널의 **고급 통계 ▾** 버튼. 현재 파일에 심각 발견이 남아 있으면 먼저 경고한 뒤 메뉴를 띄운다(차단하지 않음).

## 2. 설계 결정

- **입력 계층 공용화** — `Stats/DesignMatrix.cs`(모형식 → 처치 코딩 설계행렬), `Stats/FeatureMatrix.cs`(특성·목표, 범주 원-핫). 뷰의 지연 읽기 목록을 **한 번만** 순회하며 필요한 컬럼만 double/범주 코드로 압축한다. 문자열 행 스냅샷(기존 분석의 128MiB 예산)을 만들지 않아 500MB 파일 전체를 분석한다. 예산 1GiB, 초과 시 부분 결과 없이 중단.
- **모형식** — `Stats/ModelFormula.cs`: `y ~ x + C(g) + x:g`, `a*b`, `- 1`, `[공백 이름]`. 수준은 수치면 수치 순, 아니면 서수 순(patsy와 같음), 첫 수준이 기준. 절편이 없으면 첫 범주 주효과는 전체 수준. **범주 상호작용은 하위 항이 모형에 있어야 한다** — `y ~ x:C(g)`처럼 patsy가 다른 코딩을 쓰는 비계층 식은 다른 모형을 몰래 적합하지 않고 거부한다.
- **최소제곱** — `Stats/LeastSquares.cs`: XᵀWX 병렬 누적 + 허용오차 촐레스키로 별칭(선형 종속) 열 검출, ALGLIB `spdmatrixcholeskyinverse`. 절편이 있으면 가중 평균으로 중심화해 풀어, 원점이 큰 예측변수(예: 1,000,000 근처)를 별칭으로 오판하지 않는다. OLS와 IRLS가 공유.
- **정직한 출력** — 모든 결과 머리말에 분석 범위(뷰 행·사용·결측 제외), 비수렴·분리·별칭·표본 근사·가정 경고를 표시. 예: K-means는 20만 행을 넘으면 시드 고정 표본으로 k-means++ 초기 중심을 찾고 전체 행 Lloyd 반복으로 정제했다고 적는다. 실루엣은 5,000행 표본(근사) 표기.
- **평가 누수 방지** — 분류기 스케일링은 학습 분할로만 적합(`ClassifierEvaluation` 층화 홀드아웃·k-겹, 시드 고정), 최다 클래스 기준선을 함께 보고.
- **라이선스** — ALGLIB 소스는 `ExternalLibs/ALGLIB/`(`.gitignore`)에 로컬로만 둔다(라이선스 Appendix A H: 소스 비공개 배포). `NanumCsvViewer.Alglib` 프로젝트가 순수 C# 소스만 컴파일하고 단일 exe에 번들된다. 네이티브 HPC 코어는 쓰지 않는다.

## 3. 검증

- xUnit: 공용 계층(`StatsFoundationTests`), 모듈별 테스트, 코드 리뷰 회귀(`AdvancedStatsRegressionTests`) — 참조값은 statsmodels 0.14 / scipy 1.15 / sklearn 1.7로 생성해 하드코딩.
- 코드 리뷰(reviewer 에이전트)에서 찾은 결함 8건 수정: LDA 평가 이름 길이, AUC 정수 오버플로, Dunn 쌍 폭증(100그룹 초과 시 생략), 비계층 상호작용 코딩, 별칭 판정의 원점 의존, 범주 상호작용 열 폭 사전 검사(최대 1,000열), 단위 의존 분리 경고, 모든 군집이 단일 행일 때 실루엣.
- 실측(`dotnet run --project bench/Bench.csproj -c Release -- advstats <새 경로> 6200000`, 499,867,150 bytes · 620만 행 · RAM 모드, 2026-09-30):

| 분석 | 시간 |
|---|---:|
| GLM(OLS + Type II, 9열) | 5.2 s |
| 로지스틱(IRLS, 6회) | 6.9 s |
| Mann-Whitney / Kruskal-Wallis | 3.2 s / 3.3 s |
| Friedman / 반복측정 ANOVA | 2.2 s / 2.1 s |
| 특성 순위 | 3.1 s |
| 특성행렬 수집 + PCA / LDA / 나이브 베이즈 | 2.7 s + 2.0 s / 1.8 s / 1.0 s |
| K-means(k=5, 표본 초기화 + 전체 Lloyd) | 8.6 s |
| KNN(k=5, 홀드아웃 186만 질의, 병렬 kd-tree) | 23.1 s |

수용 기준 "500MB 이상 CSV에서 30초 이내 대부분 분석" 충족(전 항목). 합성 데이터·현재 장비 실측이며 모든 파일에 대한 보장은 아니다.

## 4. 알려진 한계 · 후속

- 엔진 오류 메시지(식 파싱·입력 검증)는 영어로 표시된다.
- GLzM 오프셋·노출·가중치·이항 시행 수 지원(§7). ANCOVA 상호작용은 요인×요인만(요인×공변량은 기울기 검정으로만 다룸).
- PCA의 p×p 고유분해(ALGLIB `smatrixevd`, p³)와 LDA의 ALGLIB `rmatrixsvd`(n×p 전체 행)·`fisherldan`(n행)은 호출 중간 취소가 되지 않는다(호출 전후에만 확인). 이 중 LDA SVD/`fisherldan`은 행 수에 비례하므로 PCA 고유분해(p³)와 달리 큰 n에서 길어질 수 있다. K-means는 자체 구현이라 해당 없음.
- AutoML 탐색 격자는 고정 후보(§6)이며 전역 최적을 뜻하지 않는다. NGBoost·CatBoost·XGBoost 자체는 제공하지 않는다(히스토그램 부스팅이 같은 계열).

## 5. Phase 2 (v1.18.0)

| 메뉴 | 분석 | 엔진 | 기준 |
|---|---|---|---|
| 모형 | 선형 혼합모형(LMM) — 그룹 1개, 임의 절편 + 임의 기울기(비구조 공분산), REML/ML, ICC | `Stats/MixedModels.cs` (그룹별 충분통계·Woodbury, ALGLIB L-BFGS) | statsmodels `mixedlm` |
| | 비선형 혼합모형(NLMM) — 지수 감쇠·로지스틱 성장·Michaelis–Menten·Emax, 모수별 임의효과 | `Stats/NonlinearMixedModel.cs` (Lindstrom–Bates) | 시뮬레이션 회복 + 임의효과 0일 때 ALGLIB NLS(로컬 Python 기준 없음) |
| 생존분석 | Kaplan–Meier(Greenwood, log-log CI, 중앙값, 제한 평균) · 로그순위/Gehan–Breslow · 곡선 창 | `Stats/Survival.cs` | statsmodels `SurvfuncRight`, `survdiff` |
| | Cox 비례위험(Efron/Breslow, HR, Wald/score/LR, Harrell C) | 〃 | statsmodels `PHReg` |
| 분류·군집 | 결정트리(CART, 분류·회귀) · 랜덤 포레스트(OOB, MDI 중요도) · SVM(선형/RBF, SMO) | `Stats/DecisionTrees.cs`, `Stats/SupportVectorMachine.cs` | sklearn |
| | 그래디언트 부스팅(히스토그램, LightGBM/XGBoost 계열) | `Stats/GradientBoosting.cs` | sklearn `HistGradientBoosting` |

부스팅은 순수 관리형 자체 구현이다(ML.NET LightGBM은 네이티브 DLL이 필요해 단일 exe 원칙과 충돌). AdaBoost·CatBoost·NGBoost는 제공하지 않는다.

정직한 상한(결과에 표기): RBF SVM 학습 20,000행, 평가 100,000행(시드 고정 층화 표본). 랜덤 포레스트 학습 80,000행. 트리는 80,000행(포레스트 12,000행) 초과 시 분위 구간 분할(근사). Cox 최대 64열, 생존 그룹 최대 50.

리뷰 수정 8건: 로그순위 분산 Int32 오버플로, 그룹별 제한 평균 τ, 회귀 트리 분할 O(n²)·취소 불가, 지수 감쇠 언더플로 처리, SVM gamma 분산 소거 오차, SVM 표시 모형 스케일링 불일치, 적합성 참조 캐시 키 공백, NLMM 식 표기. 성능 수정: SVM 병렬 예측·평가 표본 상한(256초 → 4.9초), 부스팅 병렬 히스토그램(71초 → 14.6초 단독 실측).

실측(같은 bench, 620만 행, 연속 실행): LMM(997그룹) 5.2 s · Kaplan–Meier 4.9 s · Cox 22.4 s · 결정트리 3.2 s · 랜덤 포레스트 20.5 s · SVM 4.9 s · 그래디언트 부스팅 37.8 s(연속 실행 중 메모리 압박 상태; 단독 14.6 s).

## 6. Phase 3 (v1.19.0)

| 기능 | 위치 | 검증 |
|---|---|---|
| AdaBoost(SAMME 분류, AdaBoost.R2 회귀) | `Stats/AdaBoost.cs`, 분류·군집 메뉴 | sklearn `AdaBoostClassifier/Regressor` |
| AutoML — 고정 후보 격자(선형·로지스틱, NB, LDA, KNN, 트리, 포레스트, 부스팅, AdaBoost, 작을 때 SVM), 학습 분할 안 층화 k-겹, 시간·행 예산, 리더보드, 시험 분할은 탐색에 미사용 | `Stats/AutoMl.cs` | 결정성·누수 없음(시험 행 변조 테스트)·예산 |
| 모형 저장·불러오기·적용 — 버전 JSON(`nanum-model` v1), 저장 후 예측 비트 동일, 이름으로 컬럼 연결, 학습에 없던 수준은 "채점 불가"로 보고, 예측 CSV(원본 행번호) 내보내기, 목표가 있으면 지표 | `Stats/ModelStore.cs`, "저장된 모형 적용…", 결과창 "모형 저장…" | 모형별 왕복 테스트; 신뢰할 수 없는 파일의 크기·개수 검증(checked 산술, 할당 상한), 백그라운드 로드 |
| ONNX 내보내기 — protobuf 직접 작성(IR 8, ai.onnx 13, ai.onnx.ml 3). 선형/GLM(항등), 로지스틱, 트리·포레스트·부스팅(TreeEnsemble), AdaBoost SAMME(출력 `scores` = 결정 점수), AutoML 선형 점수 모형. 입력은 사이드카 JSON 순서의 float 특성 벡터 | `Stats/OnnxExport.cs`, 결과창 "ONNX 내보내기…" | onnxruntime 1.20.1 출력 기록과 1e-5 일치 |
| 보고서 내보내기 — 템플릿(제목·파일·시각·앱 버전·분석 범위) + 본문, 캡처한 표를 실제 표로. HTML(자체 완결), Excel(.xlsx, SpreadsheetML 직접 작성, 표마다 시트), PDF(직접 작성 — 아래 §7, 인쇄 드라이버 불필요) | `Stats/ReportExport.cs`, `Stats/PdfWriter.cs`, `Stats/ReportPdf.cs`, `AdvancedResultForm.cs` | openpyxl로 열기 확인, PDF는 pypdf(strict)·PyMuPDF로 열기·텍스트 추출·렌더 확인 |
| 변수 매핑 추천(OMOP/CDISC) — 사용자가 불러온 명세 CSV(OHDSI field-level 또는 CDISC 변수 메타데이터)에 대해 이름·라벨(SPSS/SAS)·타입·값 패턴·테이블 맥락으로 설명 가능한 점수, 필수 필드 누락, 추천 CSV·적합성 프로파일 뼈대 내보내기. 내장 명세 없음 | `Stats/VariableMapping.cs` | 두 레이아웃 합성 픽스처 |

SVM·KNN·NB·LDA는 ONNX로 내보내지 않는다(명확한 메시지). AdaBoost 회귀는 가중 중앙값이라 TreeEnsemble로 재현할 수 없어 거부한다. 예측 CSV는 SVM·AdaBoost 분류에 확률 열을 만들지 않는다(클래스만).

리뷰 수정: 신뢰할 수 없는 모형 파일의 정수 오버플로 할당(보안), PDF 표 셀 줄바꿈 누락, AdaBoost 회귀 잎 최소 표본 무시. 앱 실행 확인: AutoML → 모형 저장 → HTML·Excel·PDF 보고서 → 저장된 모형 적용(예측 CSV) → AdaBoost → 변수 매핑.

## 7. 다음 버전 변경(미출시)

- **AdaBoost 분할 탐색 O(n²) 수정** — 임계값마다 오른쪽 가중 도수를 다시 합산하던 것을 큰 노드(2,048행 초과)에서 역방향 누적합으로 바꿈. 200만 행 합성 데이터: 적합 239 s → 2.1 s, 평가 236 s → 2.2 s, 회귀 평가 3.6 s. 작은 노드는 기존 합산 순서를 유지해 sklearn 참조 테스트가 그대로 통과한다. 예측은 행 범위 병렬.
- **엔진 오류 메시지 한국어화** — `Stats/ErrorText.cs`가 알려진 영어 엔진 메시지(인자 보존)를 한국어 화면에서 한국어로 변환한다. 모르는 메시지는 원문 그대로. 식 편집기·고급 통계 실행기·모형 저장/적용·변수 매핑에 연결. 변수 매핑·데이터 품질 항목은 아래 "변수 매핑·데이터 품질 메시지 한국어화".
- **LDA ONNX 내보내기** — MatMul + Add + Softmax + ArgMax. onnxruntime 1.20.1 기록값과 1e-5 일치. KNN은 계속 내보낼 수 없다(명확한 메시지). SVM·나이브 베이즈는 아래 항목.
- **SVM·나이브 베이즈 ONNX 내보내기** — SVM(RBF·선형, one-vs-one): 쌍 점수(중복 제거한 서포트 벡터, RBF는 |x|²+|sv|²−2x·sv를 0으로 하한) → 득표(점수 > 0이면 +1 클래스) → ArgMax(첫 최댓값). 출력 `label`, `votes`(득표 수, 확률 아님). 선형 SVM이 학습 행 상한을 넘어 쓰는 DCD(one-vs-rest)는 MatMul + Add 점수 + ArgMax(출력 `scores`, 가중치 없는 클래스는 −1e30). 나이브 베이즈: 로그 사전 + 가우시안 수치 로그우도 + 원-핫 범주 MatMul → Softmax 확률 + ArgMax(빈 클래스 −1e30, 출력 `label`, `probabilities`). onnxruntime 1.20.1 기록값과 라벨 정확·실수 1e-4 일치. float32라 쌍 점수가 0에 1e-5 이내면 앱(double)과 득표가 다를 수 있다. 거부: 범주 열이 번들 스케일러로 변환된 나이브 베이즈(앱은 변환된 값의 최댓값으로 범주를 고르므로 0/1 MatMul로 재현 불가 — 스케일링 "없음"으로 다시 적합), 비어 있지 않은 클래스에서 분산 ≤ 0인 수치 열, 비유한 값·잘못된 크기. 입력 `features` 한 행의 수치×클래스×서포트 벡터 크기에 비례하는 중간 텐서를 쓴다(큰 배치는 나눠서 실행).
- **셀 편집** — README 참고(보기 전용 기본, 셀 편집·시트 편집 모드, 새 파일 저장, 문자열 그대로 보관).
- **AutoML 다항 로지스틱·큰 예산 SVM** — 3클래스 이상이면 이진 로지스틱 대신 다항(소프트맥스) 로지스틱 `Multinomial-C1.0`, `Multinomial-C0.1`(L2, 절편 비규제, ALGLIB L-BFGS, 목적 = 평균 교차엔트로피 + ‖W‖²/(2Cn))이 격자 맨 앞에 들어가며 같은 층화 k-겹 CV·재적합 행 상한을 쓴다. sklearn `LogisticRegression`(multinomial, lbfgs, tol=1e-12) 계수·확률과 1e-6 일치. **저장·ONNX 지원** — 모형 종류 `MultinomialLogistic`(계수 [클래스, 1+특성]·클래스 존재 표시·C·수렴 여부를 저장, 로드 시 차원·유한성·C>0 검증, 저장 후 예측 비트 동일). ONNX는 MatMul + Add + Softmax + ArgMax(첫 최댓값, 출력 `label`·`probabilities`), 학습 행에 없던 클래스는 가중치 0 + 편향 −1e30(확률 0). onnxruntime 1.20.1 기록값과 1e-5 일치. 그래서 다항 로지스틱이 승자여도 다른 승자처럼 저장 모형을 제공한다. SVM은 탐색 표본이 800행·특성 40개 이하면 정확한 SMO(`SVM-linear`), 그 밖에는 DCD(`SVM-linear-dcd`, one-vs-rest)로 항상 경쟁하며 한 번의 적합은 행×특성×클래스 ≤ 10,000,000(최대 10만 행)이 되도록 시드 고정 층화 표본으로 줄이고(200행도 못 넣으면 생략·표시), 결과에 실제 격자·SVM 해법·표본 상한을 적는다.
- **GLzM 오프셋·노출·가중치·이항 시행 수** — 오프셋(그대로)·노출(ln 값)·분산 가중치·빈도 가중치(양의 정수)·이항 시행 수(반응 = 성공 횟수). 자유도·AIC/BIC는 빈도 가중치 합 기준, 귀무 모형은 오프셋을 둔 절편 모형 재적합. statsmodels 0.14.5(`GLM` freq/var weights, offset, 2열 이항)와 계수·SE·이탈도·귀무 이탈도·로그가능도·AIC·BIC·Pearson·척도·df 일치(`GlmExtrasTests`). 이항 + 분산 가중치는 거부(시행 수 열 사용). 가중치·오프셋 사용 시 로지스틱 부가 지표(오즈비·ROC·분류표)는 만들지 않는다. 저장 모형 지원은 아래 "GLzM 저장 모형" 항목.
- **다요인 ANCOVA(주효과)** — 범주 요인 2개 이상 + 수치 공변량. Type II, 요인별 보정 평균(다른 요인은 수준 동일 가중 평균, 공변량은 평균) + Bonferroni 쌍별 비교, 기울기 동질성(요인별 + 전체 내포 F). statsmodels `anova_lm(typ=2)`·`get_prediction`·`anova_lm(reduced, full)`과 일치(`MultiAncovaTests`). 요인 1개는 기존 경로 그대로.
- **ANCOVA 요인 상호작용** — 요인 2개 이상일 때 대화상자에서 "2차 상호작용 모두" 또는 "모든 차수"를 고르면 요인×요인 항을 넣는다(`AncovaMulti`가 순서 ≥2 범주 항을 받음). Type II는 GLM 경로 그대로 주변성을 지키고(주효과 SS는 그 요인을 포함한 상호작용을 뺀 모형 기준), 요인별 보정 평균은 상호작용 열까지 포함해 다른 요인을 수준 동일 가중 평균(emmeans), 상호작용 항마다 셀(수준 조합) 보정 평균표를 추가하며 빈 셀은 추정 불가(NaN)로 표시한다. 결과에 "상호작용이 있으면 주효과 비교는 평균값"이라는 주의를 붙인다. 기울기 동질성은 그대로. 2요인은 statsmodels `ols(C(g)*C(h)+공변량)`·`anova_lm(typ=2)`·patsy 설계행 평균 대비와 일치, 3요인(모든 차수)은 SAS 정의의 Type II(중첩 RSS 차 — statsmodels `typ=2`는 열 기반 가설이라 다중 상호작용에서 다를 수 있음)와 emmeans가 일치(`AncovaInteractionTests`).
- **PCA·K-means 취소** — PCA는 행 범위 병렬·취소 가능한 공분산 누적 + p×p 고유분해(취소 불가 구간은 p³). K-means는 ALGLIB clusterizer를 버리고 `Stats/Clustering.cs`에 탐욕 k-means++(sklearn과 같은 2+ln k 후보) · 재시작 · 청크 병렬 Lloyd를 직접 구현했다. 취소는 청크(수 ms)마다 확인해 취소 요청 후 즉시 빠져나오며 백그라운드에 남는 계산이 없다(이전: ALGLIB 호출을 별도 Task에서 돌려 포기만 했고 계산은 계속됨). 병렬 합산은 n·k·p에만 의존하는 고정 청크 순서라 코어 수와 무관하게 결정적. 빈 군집은 자기 중심에서 가장 먼 행으로 재배치(sklearn 방식), 서로 다른 점이 k개 미만이면 배정 대신 오류. `TerminationType` 1 = 수렴(배정 불변), 2 = 재시작당 반복 상한(0 요청 시 안전 상한 1000) 도달·미수렴(`ConvergedRestarts` 보고). sklearn(KMeans lloyd) 대비: 난수열이 달라 최종 inertia·분할로만 비교 — 겹치는 6덩어리·5차원 5덩어리 fixture에서 sklearn 최소 inertia(400회 단일 시작 중 최소)와 같은 값·분할(`KMeansOwnTests`). 186만 행 합성 자료(k=5, 5회 재시작, 20만 행 표본 초기화)는 현재 장비에서 1.2 s(§3 표의 8.6 s는 이전 ALGLIB 구현·다른 합성 자료).
- **행 수 상한 사용자 설정** — SVM 학습·평가 행 상한, 랜덤 포레스트 학습 행 상한·분위 구간 수를 대화상자에서 입력(기본값 유지, 결과에 실제 상한 표기). 트리의 정확/구간 임계는 분할 방식(정확/구간)을 직접 고르면 된다.
- **저장 NB의 스케일러 수정** — 범주(원-핫) 열은 스케일하지 않도록 저장 스케일러를 수치 열로 제한(`FeatureScaler.WithIdentityOutside`). 이전에는 k-겹 경로에서 저장 모형의 학습 공간과 적용 공간이 달랐다.
- 남은 한계: float32 ONNX는 점수가 결정 경계에 1e-5 안팎으로 가까우면 득표·라벨이 앱과 다를 수 있다(SVM·NB·LDA·로지스틱 등). 밀집 그래프는 아래 float64 내보내기로 피할 수 있고, 트리 계열은 float32뿐이다.
- **GLzM 저장 모형(오프셋·노출·시행 수)** — 오프셋·노출·시행 수 열로 적합한 GLzM도 저장한다. 번들이 열 이름을 기록하고(`ModelBundle.OffsetColumn/ExposureColumn/TrialsColumn`, 가중치 열은 예측에 영향이 없어 기록만), 적용 때 새 데이터에 같은 수치 열이 있어야 한다: 평균 = 역연결(Xβ + 오프셋 + ln 노출). 이항 + 시행 수는 확률(`prediction`)과 기대 성공 횟수(`expected_successes` = 시행 수 × 확률)를 내고 목표 지표는 성공 횟수를 기대 성공 횟수와 비교한다. 열이 없으면 적용을 거부하고(어느 열이 필요한지 한국어/영어로 안내), 행 단위로는 결측·비수치·노출 ≤ 0·시행 수가 양의 정수가 아님 → 이유와 함께 채점 불가로 표시(조용히 0으로 두지 않음). 파일 형식: 이런 열이 필요한 모형만 버전 2로 쓴다(이 열을 모르는 이전 빌드가 읽고 조용히 틀린 예측을 내지 않도록 버전 거부), 그 외 모형은 버전 1 그대로이며 옛 파일은 계속 읽힌다. 열 이름이 있는데 GLzM이 아니거나 버전 1이면 불러오지 않는다. ONNX는 특성 벡터만 입력이라 이런 모형은 내보내지 않는다. statsmodels 0.14 `predict(offset=, exposure=)` 및 2열 이항 예측과 1e-5 일치(`GlzmSaveTests`).
- **PDF 직접 작성** — "Microsoft Print to PDF" 프린터 경로를 없애고 순수 관리형 작성기로 교체(`Stats/PdfWriter.cs`: PDF 1.7 문법·고전 xref, A4, Flate 내용 스트림; `Stats/ReportPdf.cs`: 보고서 배치). 한글은 설치된 TrueType 글꼴(맑은 고딕 → 나눔고딕 → 굴림 등, `malgunbd.ttf` 굵게)을 **사용한 글리프만 부분집합**으로 CIDFontType2 + Identity-H로 내장하고 ToUnicode CMap을 넣어 글자 선택·검색이 된다(표 90행 3쪽 보고서 약 37KB, 13MB 글꼴 전체를 넣지 않음). 표를 찾지 못한 원문 본문은 고정폭 격자(D2Coding 또는 Consolas + 한글 글꼴, 한글 2칸)로 정렬을 유지한다. 글리프가 글꼴에 없으면 □(.notdef)로 그리고 ToUnicode는 U+FFFD. 내장 금지 라이선스(fsType 제한·부분집합 금지)·CFF(OTTO) 글꼴은 건너뛰고, 쓸 수 있는 한글 글꼴이 없으면 `PdfFontNotFoundException`을 던져 UI가 HTML 내보내기를 권한다. 보고서에는 이미지가 없어(표·문단뿐) 이미지 내장은 없다. 프린터 경로는 다른 기능의 유일한 수단이 아니므로 제거했다.
- **변수 매핑·데이터 품질 메시지 한국어화** — 변수 매핑 결과(`VariableMapping.Format/ExportCsv`에 `korean` 인자: 제목·표 머리글·근거 문장·상태, 추천 CSV는 열 머리글을 안정 스키마로 영어 유지하고 근거 열만 번역)와 명세 읽기 오류, 적합성 프로파일 검증·참조 파일·예산·정규식 시간 초과 오류, 프로파일 `Notes`·결과 라벨(`column absent` 등)·`N codes`·`N reference keys` 주석, DQD 가져오기 주석(`threshold …`, `not applicable: …`, `… not reported`)·경고·오류, 참조 무결성 오류, System.Text.Json 파싱 오류(위치는 `줄 N, M번째 바이트`로)가 한국어 화면에서 한국어로 나온다(`QualityText`·오류 대화상자가 `ErrorText.Localize/LocalizeNote`를 거침). 여러 줄 메시지(파일별 오류 모음)는 줄마다 변환. 영어로 남는 것: 사용자 데이터(컬럼·필드·테이블·파일 이름, DQD 파일의 설명·오류 원문), 표준 코드(타입 이름 `Integer`·`Float`, JSON 키 `maxLength` 등, OMOP·CDISC), 프레임워크·OS 원문(잘못된 정규식의 .NET 설명, 파일 열기 실패의 OS 메시지, 알 수 없는 JSON 파서 문장은 `JSON 오류(위치): 원문`). `MessageLocalizationTests`가 실제 엔진 경로(명세 파싱·추천, 프로파일 JSON 변형 21종, 참조 파일 오류, 예산·시간 초과, 실제 결과 렌더링, DQD 샘플, 참조 무결성)의 한국어 출력에 번역되지 않은 영어 단어가 남지 않았음을 허용 목록(데이터·표준 코드) 기준으로 검사한다.
- **ONNX float64(배정밀도) 내보내기** — 밀집 그래프(선형·GLM 항등/로짓·선형 점수·LDA·다항 로지스틱·SVM SMO/DCD·나이브 베이즈)는 입력 `features`와 모든 부동소수 초기값·출력이 float64(TensorProto DOUBLE)이며 ai.onnx opset 13 연산(MatMul·Add·Sub·Mul·Div·Exp·Relu·Greater·GreaterOrEqual·Cast·ReduceSum·Softmax·Sigmoid·ArgMax·Gather·Unsqueeze)만 쓴다. 선형 회귀는 float32의 LinearRegressor(ai.onnx.ml, 계수가 float 속성) 대신 MatMul을 쓴다. `OnnxExport.Export(bundle, OnnxPrecision.Float64)` / `SupportsFloat64`; 기본은 float32 그대로. 트리·포레스트·부스팅·AdaBoost는 TreeEnsemble(ai.onnx.ml)의 임계값·잎 가중치가 float32 속성이라 float64를 거부하며(이유 메시지), UI는 이런 모형에는 선택 대화상자를 보이지 않는다. 사이드카 JSON은 `precision`과 `input.dtype`에 정밀도를 적고 요약에도 적는다. 널·빈 클래스 상수는 앱과 같은 정확한 −1e30. float32 ‘값 범위 초과’ 계수는 float64로는 내보낼 수 있다(무한·NaN은 둘 다 거부). UI: 결과 창 "ONNX 내보내기…"가 float32(기본)/배정밀도 선택 대화상자를 먼저 띄운다(기본 파일 이름에 `-float64`). 검증(`OnnxDoubleTests`): onnxruntime 1.20.1 CPU로 11개 모형의 float64 그래프가 앱의 라벨과 모든 행에서 정확히 같고 득표·점수·확률·예측이 1e-12 이내로 일치(앱 표본 + 앱의 double 산술로 결정 경계 1e-9 이내에 만든 행 10–12개)하며, 같은 경계 행에서 float32 그래프는 SVM RBF 4/12·선형 4/10·DCD 3/12·NB 4/12·LDA 6/12·다항 2/12·GLM 6/10·선형 점수 로짓 5/10행의 라벨을 뒤집는다(기록값). 한계: onnxruntime의 합산 순서가 앱과 달라 점수가 0에 1e-12 이내이면 float64에서도 득표가 다를 수 있다.
- **셀 편집 확장(되돌리기·붙여넣기·이름 변경·행 삽입/삭제·xlsx 저장·복구 저널)** — `Csv/CellEdits.cs`: 셀·컬럼 이름·삭제 집합·추가 행(앵커 아래 위치, 비재귀 순회)을 한 덮개에 두고 모든 변경을 되돌리기/다시 실행 단계(`BeginStep`, 500단계·200만 변경 한도, 저장 시점 추적 `IsDirty`)로 기록한다. `VirtualCsvDocument`는 행 id(원본 0..N-1, 추가 행은 그 뒤)와 화면 순서(`_live`/`_rank`)를 분리해 필터·정렬 뷰맵을 id로 유지하고, 행 번호(`GetSourceRowNumber`)는 편집 후 순서 기준이다. `SaveWithEdits`는 이름 변경 헤더·삭제 행 건너뜀·추가 행(파일의 줄바꿈)·줄바꿈 없는 마지막 행을 처리하고, `SaveAsXlsx`(`Csv/XlsxDataWriter.cs`)는 모든 값을 inlineStr로 써 선행 0을 보존한다 — 보고서용 `ReportExport.WriteXlsx`는 숫자처럼 보이는 값을 숫자로 바꿔 데이터 저장에는 쓸 수 없어 재사용하지 않았다. `Csv/ClipboardGrid.cs`(엑셀 TSV 해석), `Csv/EditJournal.cs`(경로+크기+수정시각 키 JSON 저널). 편집 후 필터·정렬은 `RebuildViewAsync`로 한 번에 교체(깜빡임 없음)하고 열린 고급 통계·차트 창에는 "편집 후 데이터" 표시를 붙인다. 테스트 `CellEditPlusTests`(34개) + 실제 앱 UIA 스모크(단일/시트 모드, 붙여넣기·되돌리기, 이름 변경, 행 삽입/삭제, 필터 재평가, 차트 창 표시, CSV·xlsx·SAS 저장, 비정상 종료 후 복구). 한계: 단축키(Ctrl+Z/Y/V, Delete)와 헤더 더블클릭은 같은 메서드를 메뉴로 호출해 검증했고 키 입력 자체는 자동화하지 못했다. 행 삽입/삭제는 행 수가 int 범위 안일 때만, 붙여넣기·지우기는 한 번에 100만 셀까지.
- **620만 행 벤치(다음 버전 기준)** — GLM 6.9 s, 로지스틱 8.9 s, PCA 2.4 s, LDA 2.2 s, NB 1.0 s, K-means(자체 구현) 9.2 s, KNN 24.9 s, LMM 8.1 s, Cox 28.5 s, 결정 트리 3.6 s, 랜덤 포레스트 25–27 s, SVM 5.2 s, 그래디언트 부스팅 42.7 s(순차 벤치; 단독 약 15 s), **AdaBoost 152.7 s → 1.7 s**, AutoML 1.5 s. 최대 작업 집합 4.2 GiB.
