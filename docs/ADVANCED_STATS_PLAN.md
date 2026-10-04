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
- **정직한 출력** — 모든 결과 머리말에 분석 범위(뷰 행·사용·결측 제외), 비수렴·분리·별칭·표본 근사·가정 경고를 표시. 예: K-means는 20만 행을 넘으면 시드 고정 표본으로 ALGLIB k-means++ 초기 중심을 찾고 전체 행 Lloyd 반복으로 정제했다고 적는다. 실루엣은 5,000행 표본(근사) 표기.
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
- 이항 GLzM은 0/1 응답만(시행 수·오프셋·가중치 없음). ANCOVA는 요인 1개·주효과만.
- ALGLIB k-means·PCA SVD는 호출 중간 취소가 되지 않는다(호출 전후에만 확인).
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
- **엔진 오류 메시지 한국어화** — `Stats/ErrorText.cs`가 알려진 영어 엔진 메시지(약 150종, 인자 보존)를 한국어 화면에서 한국어로 변환한다. 모르는 메시지는 원문 그대로. 식 편집기·고급 통계 실행기·모형 저장/적용·변수 매핑에 연결. (Mapping 근거 문장과 일부 데이터 품질 메시지는 아직 영어.)
- **LDA ONNX 내보내기** — MatMul + Add + Softmax + ArgMax. onnxruntime 1.20.1 기록값과 1e-5 일치. KNN은 계속 내보낼 수 없다(명확한 메시지). SVM·나이브 베이즈는 아래 항목.
- **SVM·나이브 베이즈 ONNX 내보내기** — SVM(RBF·선형, one-vs-one): 쌍 점수(중복 제거한 서포트 벡터, RBF는 |x|²+|sv|²−2x·sv를 0으로 하한) → 득표(점수 > 0이면 +1 클래스) → ArgMax(첫 최댓값). 출력 `label`, `votes`(득표 수, 확률 아님). 선형 SVM이 학습 행 상한을 넘어 쓰는 DCD(one-vs-rest)는 MatMul + Add 점수 + ArgMax(출력 `scores`, 가중치 없는 클래스는 −1e30). 나이브 베이즈: 로그 사전 + 가우시안 수치 로그우도 + 원-핫 범주 MatMul → Softmax 확률 + ArgMax(빈 클래스 −1e30, 출력 `label`, `probabilities`). onnxruntime 1.20.1 기록값과 라벨 정확·실수 1e-4 일치. float32라 쌍 점수가 0에 1e-5 이내면 앱(double)과 득표가 다를 수 있다. 거부: 범주 열이 번들 스케일러로 변환된 나이브 베이즈(앱은 변환된 값의 최댓값으로 범주를 고르므로 0/1 MatMul로 재현 불가 — 스케일링 "없음"으로 다시 적합), 비어 있지 않은 클래스에서 분산 ≤ 0인 수치 열, 비유한 값·잘못된 크기. 입력 `features` 한 행의 수치×클래스×서포트 벡터 크기에 비례하는 중간 텐서를 쓴다(큰 배치는 나눠서 실행).
- **셀 편집** — README 참고(보기 전용 기본, 셀 편집·시트 편집 모드, 새 파일 저장, 문자열 그대로 보관).
- **AutoML 다항 로지스틱·큰 예산 SVM** — 3클래스 이상이면 이진 로지스틱 대신 다항(소프트맥스) 로지스틱 `Multinomial-C1.0`, `Multinomial-C0.1`(L2, 절편 비규제, ALGLIB L-BFGS, 목적 = 평균 교차엔트로피 + ‖W‖²/(2Cn))이 격자 맨 앞에 들어가며 같은 층화 k-겹 CV·재적합 행 상한을 쓴다. sklearn `LogisticRegression`(multinomial, lbfgs, tol=1e-12) 계수·확률과 1e-6 일치. **저장·ONNX 지원** — 모형 종류 `MultinomialLogistic`(계수 [클래스, 1+특성]·클래스 존재 표시·C·수렴 여부를 저장, 로드 시 차원·유한성·C>0 검증, 저장 후 예측 비트 동일). ONNX는 MatMul + Add + Softmax + ArgMax(첫 최댓값, 출력 `label`·`probabilities`), 학습 행에 없던 클래스는 가중치 0 + 편향 −1e30(확률 0). onnxruntime 1.20.1 기록값과 1e-5 일치. 그래서 다항 로지스틱이 승자여도 다른 승자처럼 저장 모형을 제공한다. SVM은 탐색 표본이 800행·특성 40개 이하면 정확한 SMO(`SVM-linear`), 그 밖에는 DCD(`SVM-linear-dcd`, one-vs-rest)로 항상 경쟁하며 한 번의 적합은 행×특성×클래스 ≤ 10,000,000(최대 10만 행)이 되도록 시드 고정 층화 표본으로 줄이고(200행도 못 넣으면 생략·표시), 결과에 실제 격자·SVM 해법·표본 상한을 적는다.
- **GLzM 오프셋·노출·가중치·이항 시행 수** — 오프셋(그대로)·노출(ln 값)·분산 가중치·빈도 가중치(양의 정수)·이항 시행 수(반응 = 성공 횟수). 자유도·AIC/BIC는 빈도 가중치 합 기준, 귀무 모형은 오프셋을 둔 절편 모형 재적합. statsmodels 0.14.5(`GLM` freq/var weights, offset, 2열 이항)와 계수·SE·이탈도·귀무 이탈도·로그가능도·AIC·BIC·Pearson·척도·df 일치(`GlmExtrasTests`). 오프셋·시행 수 모형은 저장 모형으로 저장하지 않고(새 데이터에 같은 열이 필요하거나 반응 의미가 다름) 결과에 그렇게 적는다. 이항 + 분산 가중치는 거부(시행 수 열 사용). 가중치·오프셋 사용 시 로지스틱 부가 지표(오즈비·ROC·분류표)는 만들지 않는다.
- **다요인 ANCOVA(주효과)** — 범주 요인 2개 이상 + 수치 공변량. Type II, 요인별 보정 평균(다른 요인은 수준 동일 가중 평균, 공변량은 평균) + Bonferroni 쌍별 비교, 기울기 동질성(요인별 + 전체 내포 F). statsmodels `anova_lm(typ=2)`·`get_prediction`·`anova_lm(reduced, full)`과 일치(`MultiAncovaTests`). 요인 1개는 기존 경로 그대로.
- **PCA·K-means 취소** — PCA는 행 범위 병렬·취소 가능한 공분산 누적 + p×p 고유분해(취소 불가 구간은 p³만). K-means는 ALGLIB 호출을 별도 작업에서 돌리고 취소되면 호출 쪽이 즉시 빠져나온다(포기된 계산은 초기화 표본 크기로 제한되며 결과를 버림).
- **행 수 상한 사용자 설정** — SVM 학습·평가 행 상한, 랜덤 포레스트 학습 행 상한·분위 구간 수를 대화상자에서 입력(기본값 유지, 결과에 실제 상한 표기). 트리의 정확/구간 임계는 분할 방식(정확/구간)을 직접 고르면 된다.
- **저장 NB의 스케일러 수정** — 범주(원-핫) 열은 스케일하지 않도록 저장 스케일러를 수치 열로 제한(`FeatureScaler.WithIdentityOutside`). 이전에는 k-겹 경로에서 저장 모형의 학습 공간과 적용 공간이 달랐다.
- 남은 한계: 엔진 메시지 외 일부 근거 문장(변수 매핑)·데이터 품질 메시지 영문, ANCOVA 요인 간 상호작용, SVM ONNX는 float32라 점수가 0에 매우 가까우면 득표가 다를 수 있음.
- **PDF 직접 작성** — "Microsoft Print to PDF" 프린터 경로를 없애고 순수 관리형 작성기로 교체(`Stats/PdfWriter.cs`: PDF 1.7 문법·고전 xref, A4, Flate 내용 스트림; `Stats/ReportPdf.cs`: 보고서 배치). 한글은 설치된 TrueType 글꼴(맑은 고딕 → 나눔고딕 → 굴림 등, `malgunbd.ttf` 굵게)을 **사용한 글리프만 부분집합**으로 CIDFontType2 + Identity-H로 내장하고 ToUnicode CMap을 넣어 글자 선택·검색이 된다(표 90행 3쪽 보고서 약 37KB, 13MB 글꼴 전체를 넣지 않음). 표를 찾지 못한 원문 본문은 고정폭 격자(D2Coding 또는 Consolas + 한글 글꼴, 한글 2칸)로 정렬을 유지한다. 글리프가 글꼴에 없으면 □(.notdef)로 그리고 ToUnicode는 U+FFFD. 내장 금지 라이선스(fsType 제한·부분집합 금지)·CFF(OTTO) 글꼴은 건너뛰고, 쓸 수 있는 한글 글꼴이 없으면 `PdfFontNotFoundException`을 던져 UI가 HTML 내보내기를 권한다. 보고서에는 이미지가 없어(표·문단뿐) 이미지 내장은 없다. 프린터 경로는 다른 기능의 유일한 수단이 아니므로 제거했다.
