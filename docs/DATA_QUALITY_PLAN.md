# 데이터 품질 검토 모듈 — 킥오프 브리프 (이슈 #26)

> 새 세션이 바로 착수할 수 있도록 정리한 계획 문서. 실제 구현 전 §6(열린 결정)을 먼저 확정할 것.
> 관련 에픽 #27(고급통계·ML)은 별개 — 이 문서는 #26(품질 검토)만 다룬다.

## 1. 목표 (이슈 #26 요지)

의료빅데이터(OMOP-CDM / CDISC SDTM·ADaM / EMR·CDW)를 **Kahn Framework 3차원**으로 체계적 품질 검토:
**Conformance(형식 적합) · Completeness(완전성) · Plausibility(타당성)**. (OHDSI DataQualityDashboard도 이 3분류 사용.)

**MVP 수용 기준**: 품질 탭 UI · 의료 기본 규칙 10+개 · 1GB 파일 5초 이내 프로파일링 · 보고서 Export placeholder.

## 2. Kahn 3차원 → 규칙 분류(구현 taxonomy)

| 차원 | 검사 예시 | 재사용 엔진 |
|---|---|---|
| **Conformance** (값이 타입·형식·관계 제약 준수) | 타입 불일치, 날짜 형식 오류, 코드북(codelist) 위반, CDISC 변수 길이 초과, `visit_start < visit_end` 관계 | `ColumnTypeConversion.Validate`, `CsvDateParser.ParseDetailed`, `TextFilterOp.InList`, `AdvancedFilterExpression`(관계식) |
| **Completeness** (필수·결측) | 결측률 임계 초과, required field 누락, 상수/거의상수 컬럼 | `ColumnSummary.NullCount/NonNullCount/UniqueCount`, `IsNullToken` |
| **Plausibility** (값이 현실적) | 범위 이탈(age 0–150), 시간 순서, 분포 이상치(IQR±1.5), 완전중복 PK, 성별 M/F/UNK | `Describe`(Q1/Q3/IQR·왜도), `Percentile`, `FindDuplicates`, `NumericRangeFilter`/`DateRangeFilter` |

## 3. 재사용 자산 (조사 완료 — 신규 코드 최소)

**이미 계산 가능**: 결측·고유·타입추론·수치기술통계(사분위/IQR/왜도)·중복(키+원본행번호)·범위이탈 술어·코드북 값라벨 집합.
**새로 필요한 것은 대부분 "규칙 표현/집계/리포트 오케스트레이션 계층"뿐.**

| 계층 | 재사용 지점 |
|---|---|
| 컬럼 요약 | `Csv/ColumnStatistics.cs` — `ColumnStatisticsBuilder.Summarize` → `ColumnSummary`(Null/NonNull/Unique/InferredType/Numeric/TopValues), `IsNullToken`/`IsBooleanToken` |
| 분포·이상치 | `Csv/CsvStatistics.cs` — `Describe`(Q1/Median/Q3/IQR/Skew/CV/Modes), `FrequencyTable`(코드북 분포) |
| 중복·백분위 | `Csv/CsvAnalytics.cs` — `FindDuplicates`(키+SourceRow), `Percentile`, `NumericDistributionOf`/`DateHistogramOf`/`GroupBy`(세그먼트 집계) |
| 타입/형식 검증 | `Csv/ColumnTypeConversion.cs` — `Validate(target, values)`→`TypeChangeValidation`(실패수·예시), `Classify`/`TypeChangePolicy`(심각도 매핑 참고) |
| 날짜 검증 | `Csv/CsvDateParser.cs` — `ParseDetailed`, 연도 타당성 가드(1900~2100) |
| **규칙 = 술어** | `Csv/ColumnFilterState.cs`(`NumericRangeFilter`/`DateRangeFilter`/`TextFilterOp.InList`/`IsBlank`/`Regex` → `Func<string[],bool>` 컴파일), `Csv/AdvancedFilterExpression.cs`(`Compile(expr, headers)` — `age>=0 AND age<=120` DSL 그대로 채택). **위반행 = 술어 반전 집계.** |
| 입력 행 | `Form1.Features.cs` — `GatherViewRows`/`GatherViewRowsWithSource`(원본 행번호 포함), `NumericColumn`(NumericAffix) |
| 결과 UI | `FeatureDialogs.cs`(`ParamDialog`/`ResultForm`+액션버튼), `FacetView`(컬럼별 비율 막대바), `ChartForm`/`PlotControl`(결측 Pareto·이상치 박스플롯 — `ChartContext` 재사용) |
| 코드북·선언타입 | `Import/FormatMappers.cs`(SPSS `ValueLabels`·SAS 포맷), `Import/SasCatalogReader.cs`(`SasCatalog.HasFormat/TryLabel` = 허용코드 사전), `ComputeColumnTypeTags`(추론 vs 선언 불일치 = 품질 신호) |
| 멀티테이블 | `Import/WorkbookImport.cs` — OMOP/CDISC 여러 테이블 = 여러 시트. **단, 테이블 간 참조무결성(FK) 검사는 전무 → 신규 계층 필요.** |

## 4. 아키텍처 제안 (계산/렌더 분리 — v1.13 통계·#19 시각화 관행 계승)

```
Csv/DataQuality/
  QualityRule.cs      — 규칙 선언(대상 컬럼·차원·심각도·술어 팩토리). #12 변환규칙표·#19 슬롯테이블식 데이터 주도.
  QualityRuleSet.cs   — 내장 의료 규칙 10+ + JSON 확장(OMOP/CDISC codelist·required field). 설정 파일.
  QualityEngine.cs    — 행 1패스 다중 규칙 평가. 규칙별 위반 카운터 + 위반 원본행번호(전체 행 저장 X) 누적.
  QualityReport.cs    — 차원별 점수(0~100)·규칙별 통과/실패·문제 행 인덱스. xUnit 테스트 대상.
Tests/QualityEngineTests.cs — 규칙 평가·점수·집계 검증(합성 데이터).
```
- **규칙 술어는 기존 필터 계층으로 컴파일** → 파싱·타입판정·범위검사 신규 코드 거의 0.
- UI(`QualityForm` 또는 탭): 차원별 게이지 + 실패 규칙 테이블(정렬/필터) + **"문제 행 바로 보기"**(원본 행번호 → 기존 `Ctrl+G` 이동/그리드 하이라이트 재사용) + Export placeholder.

## 5. MVP 슬라이스 (1릴리즈, v1.15.0 후보)

- **Phase A (계산·테스트 가능)**: QualityRule/RuleSet/Engine/Report + 내장 규칙 10+개(결측률·상수·완전중복·타입불일치·범위(age)·성별 코드셋·날짜순서·이상치·형식) + scipy 무관 합성 데이터 테스트.
- **Phase B (UI)**: 품질 탭/창 — 게이지·실패 테이블·문제행 점프·Export placeholder.
- **후속**: OMOP/CDISC codelist·concept_id·domain·FK 검증(설정 스키마부터), OHDSI DQD JSON import, #27 "품질 OK → Advanced Stats" 워크플로 연동 버튼.

## 6. 열린 결정 (새 세션에서 먼저 확정)

1. **UI 형태**: 별도 모델리스 창(기존 ChartForm/PivotForm 관행) vs 진짜 "탭"(메인 폼에 탭 컨트롤 신규). 이슈는 "탭 또는 사이드바" — 기존 앱은 탭 UI가 없으므로 창 방식이 관행에 부합.
2. **대용량 전략 (핵심 리스크)**: 수용 기준은 **"1GB 파일 5초 이내"**인데 기존 `GatherViewRows`는 **현재 뷰 200만행 상한**. 전체 파일 프로파일링은 `VirtualCsvDocument` **스트리밍(SIMD 인덱스)** 을 직접 써야 할 수 있음 — 뷰 기반 vs 파일 전수 스트리밍 중 결정. (§4 엔진을 `IEnumerable<string[]>` 입력으로 두면 둘 다 지원.)
3. **규칙 정의 포맷**: 내장(C#) + JSON 확장. OHDSI DQD JSON 결과 스키마와 호환할지(향후 import).
4. **"문제 행 바로 보기"**: 기존 그리드에 행 하이라이트 인프라 유무 확인(원본 행번호·`Ctrl+G` 점프는 있음). 하이라이트가 없으면 필터 적용으로 대체.
5. **#27 경계**: 이 세션은 #26만. "품질 OK → 고급통계" 연동은 버튼 자리(placeholder)만 남김.

## 7. 참고

- OHDSI **DataQualityDashboard** (Kahn 기반, 카테고리·명명 참고): https://github.com/OHDSI/DataQualityDashboard
- Kahn 2016 harmonized data quality framework (Conformance/Completeness/Plausibility 원출처)
