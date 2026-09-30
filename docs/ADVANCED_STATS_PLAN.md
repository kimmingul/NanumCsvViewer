# 고급 통계·머신러닝 모듈 — Phase 1 (이슈 #27)

> 구현: v1.17.0. ALGLIB Commercial 4.04(순수 C#) + 자체 엔진. Phase 2(LMM/NLMM, 생존분석, 트리·SVM·부스팅)와 Phase 3(AutoML, ONNX)는 미착수.

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
- Phase 2: 선형·비선형 혼합모형, 생존분석(Kaplan–Meier, log-rank), 결정트리·랜덤포레스트·SVM, 부스팅. Phase 3: AutoML, 모델 저장(ONNX), 보고서 템플릿.
