# macOS → Windows 11 기능 포팅 설계 문서

> 대상: **Nanum CSV Viewer** (Windows / .NET 10 · WinForms)
> 출처: `NanumCsvViewer_for_macOS` (Swift / AppKit, v1.7.6)
> 목적: macOS 버전에만 있는 고급 분석·탐색·내보내기 기능을 Windows 버전으로 이식하기 위한 상세 설계
> 작성: 2026-06-29

---

## 구현 상태 (2026-06-30)

**다중 파일/탭(I)을 제외한 전 항목(A–H, J–Q) 구현 완료.** 빌드 경고 0·오류 0, 단위테스트 121개 전부 통과, 오프스크린 렌더로 다이얼로그·헤더 배지·피벗 빌더·차트 시각 검증 완료.

**신규 엔진 (`NanumCsvViewer/Csv/`)** — `CsvDateParser`, `ColumnStatistics`, `CsvSearch`, `AdvancedFilterExpression`, `CsvAnalytics`(피벗 포함), `CsvStatistics`, `CsvExporter`, `SavedCsvView`, `IndexCache` (+ `RecordIndex` 벌크 직렬화). 통계 p값은 Cauchy(df=1→0.5)·χ²(3.8415,df=1→0.05) 등 알려진 정답으로 대조 검증.

**UI** — `Form1.Features.cs`(부분 클래스, Designer 미수정·메뉴 코드 구성), `FeatureDialogs.cs`(`ParamDialog`/`ResultForm`), `PivotForm.cs`(피벗 빌더), `ChartControl.cs`(GDI+ 차트).

| 항목 | 상태 |
|---|---|
| A 헤더 타입 배지(+토글) · B 검색모드(regex/fuzzy) · C 표현식필터 · D 행 이동(Ctrl+G) | ✅ |
| E 내보내기(CSV/MD/JSON/HTML, 진행 표시) · F 영속 인덱스 캐시(+닫을 때 삭제 토글) | ✅ |
| G 컬럼 숨김 · H 저장된 뷰(복원 시 컬럼 위치 포함) · J 클립보드 · K 드래그앤드롭 · L 성능 대시보드 | ✅ |
| M 분석(분포/날짜/중복/그룹바이) · N 통계(상관/t검정/카이제곱) | ✅ |
| **O 피벗 빌더**(행/열/값/필터·다중측정·합계·날짜그룹핑) · **P 피벗 차트**(GDI+ 막대/묶은/누적/꺾은선·범례·축·호버) | ✅ |
| Q 긴 셀 1줄 미리보기 제한 | ✅ |
| **I 다중 파일/탭** | ❌ 의도적 제외 |

> 미세 잔여: 호버 툴팁은 정적 렌더로 검증 불가(코드만). 실파일로 한 번 수동 점검 권장.

---

## 0. 의사결정 (확정)

| 항목 | 결정 |
|---|---|
| 피벗 차트 렌더링 | **GDI+ 자작 차트** (외부 라이브러리 미사용 → 단일 exe·EV 서명 영향 없음) |
| 다중 파일/탭 | **제외(최후순위)** — 단일 폼 아키텍처 유지 |
| 작업 단위 | Phase 단위 독립 PR |

---

## 1. 배경: 두 버전의 구조 비교

| | Windows (현재) | macOS (목표 기능) |
|---|---|---|
| 코드량 | ~3,800줄 | ~12,000줄 |
| CSV 엔진 | `NanumCsvViewer/Csv/` (7파일) | `Sources/CsvCore/` (15파일) |
| UI | `Form1.cs` (단일 폼) | `MainWindowController` + Pivot/Analysis 등 |

**핵심 엔진은 동등**하다. 양쪽 모두 (1) 레코드 바이트 오프셋 인덱싱, (2) 가상 행 렌더링, (3) UTF-8/CP949 인코딩 감지, (4) 다중 컬럼 안정 정렬, (5) 증분 AND 필터를 갖는다. 격차는 전부 **분석/탐색/내보내기/생산성 레이어**에 있다.

### Windows가 이미 가진 것 (재작업 불필요)
라이트/다크 테마, 다국어(영/한), 원본 행번호, 멀티라인 셀 표시, 상세 패널(F4), 다중 컬럼 안정 정렬, 셀값 AND 누적 필터(Filter by Cell), contains 검색(Find Next).

---

## 2. 이식 전략

### 2.1 엔진 로직은 C#로 거의 1:1 이식 가능
Swift `CsvCore`는 행을 `[String]`로, 필터를 클로저 술어로 다룬다. 이는 Windows의
`VirtualCsvDocument.GetDataRowUncached(int dataRow) → string[]` + `Func<string[], bool>` 패턴과 정확히 대응한다.

macOS 분석기가 `currentDisplayRows`(구체화 배열)에 작동하듯, Windows는 **기존 `_viewMap` 스냅샷을 순회하며 `GetDataRowUncached`로 행을 공급**하면 동일 알고리즘을 그대로 쓸 수 있다.

### 2.2 신규 엔진 파일 (`NanumCsvViewer/Csv/` 아래)
macOS 카운터파트와 동일 책임, .NET 관용구(LINQ, `Span<T>`, `System.Text.Json`, `System.Math`, `Regex`)로 작성.

| 신규 파일 | 대응 macOS 파일 | 내용 |
|---|---|---|
| `CsvDateParser.cs` | `CsvDateParser.swift` | 다양한 날짜 형식 파싱(점/한국어/월only/`yyyyMMdd`) |
| `ColumnStatistics.cs` | `ColumnStatistics.swift` | 컬럼 추론 타입 + 요약 통계 |
| `CsvSearch.cs` | `CsvSearch.swift` | contains/regex/fuzzy 매처 |
| `AdvancedFilterExpression.cs` | `AdvancedFilterExpression.swift` | 표현식 필터 토크나이저+파서 |
| `CsvAnalytics.cs` | `CsvAnalytics.swift` | 분포/날짜히스토그램/중복/그룹바이/피벗 |
| `CsvStatistics.cs` | `CsvStatistics.swift` | 상관/t검정/카이제곱 (수치 적분 포함) |
| `CsvExporter.cs` | `VirtualCsvDocument.swift`(export) | CSV/MD/JSON/HTML 스트리밍 내보내기 |
| `SavedCsvView.cs` | `SavedCsvView.swift` | 뷰 상태 직렬화 모델 |
| `IndexCache.cs` | `VirtualCsvDocument.swift`(`.ncvidx`) | 오프셋 인덱스 영속 캐시 |

### 2.3 UI는 WinForms 등가물로 재설계
| macOS (AppKit) | Windows (WinForms) |
|---|---|
| 매개변수 시트(sheet) | 모달 `Form` 다이얼로그 |
| 툴바 버튼 | `ToolStrip` 버튼 |
| `NSTableView` 결과 | `DataGridView` 또는 `ListView` |
| 드래그 드롭존 | `ListBox` + 드래그 / 우클릭 할당 |
| SwiftUI Charts | **GDI+ 자작 `ChartControl`** |

---

## 3. 기능 격차 매트릭스

| # | 기능 | Win 현재 | 포팅 성격 | Phase |
|---|---|:---:|---|:---:|
| A | 컬럼 통계 + 헤더 타입 태그 | ❌ | 엔진+헤더UI | 1 |
| B | 고급 검색(regex/fuzzy) | contains만 | 엔진+입력파싱 | 1 |
| C | 표현식 필터 | 셀 AND만 | 엔진(파서) | 2 |
| D | Go to Row | ❌ | 소UI | 1 |
| E | 내보내기 CSV/MD/JSON/HTML | ❌ | 엔진+메뉴 | 1 |
| F | 영속 인덱스 캐시(`.ncvidx`) | ❌ | 엔진(직렬화) | 3 |
| G | 컬럼 숨기기/보이기 | ❌ | UI | 2 |
| H | 저장된 뷰(파일별) | ❌ | 직렬화+UI | 3 |
| I | 다중 파일/탭 | 단일 | 아키텍처 | **제외** |
| J | 클립보드 가져오기 | ❌ | 소UI | 2 |
| K | 드래그앤드롭 가져오기 | ❌ | 소UI | 2 |
| L | 성능 대시보드 | 상태바만 | 소UI | 2 |
| M | 분석(분포/날짜/중복/그룹바이) | ❌ | 엔진+시트 | 3 |
| N | 통계(상관/t검정/카이제곱) | ❌ | 엔진(검증중요) | 3 |
| O | 피벗 빌더 | ❌ | 대형 UI+엔진 | 4 |
| P | 피벗 차트(GDI+) | ❌ | 자작 차트 | 4 |
| Q | 긴 셀 1줄 미리보기 제한 | 부분 | 렌더 튜닝 | 상시 |

---

## 4. Phase별 상세 설계

### Phase 1 — 엔진 기반 + 즉효 기능 (저비용·고가치)

#### A. 컬럼 통계 / 타입 추론
- **엔진**: `CsvDateParser.cs`, `ColumnStatistics.cs` 이식.
  - 추론 타입: `Integer / Float / Date / Boolean / Categorical / String / Empty`.
  - 판정 순서(macOS와 동일): null 토큰(`"", na, n/a, null, nil, missing`) 제외 → boolean 토큰 집합 → date(숫자형 아님이거나 헤더가 날짜 암시) → integer → float → categorical(고유값 ≤ max(20, n/2)) → string.
  - 표본: 인덱싱 완료 후 첫 N행(예: 10,000) 또는 전체. 헤더가 날짜 암시(`date/일자/날짜/dt` 등)면 `yyyyMMdd` 컴팩트 날짜 허용.
- **UI**: `DataGridView` 컬럼 헤더에 타입 태그. 헤더 `OwnerDraw`로 컬럼명 아래 작은 회색 태그(예: `이름  String`), 또는 헤더 텍스트 접미사.
- **연계**: 이 추론 타입은 이후 정렬(수치 vs 문자), 분석 기본값(M), 피벗 측정 후보(O) 판정에 재사용.
- **테스트**: macOS `ColumnStatisticsBuilder` 케이스(정수/실수/혼합/날짜/불리언/카테고리) 동일 입력→동일 추론.

#### B. 고급 검색 (regex / fuzzy)
- **엔진**: `CsvSearch.cs` — `CsvSearchMode { Contains, Regex, Fuzzy }`, `CsvSearchMatcher`.
  - Regex: .NET `Regex(pattern, RegexOptions.IgnoreCase)`, **검색당 1회 컴파일**(셀당 재컴파일 금지 — macOS도 감사 후 수정).
  - Fuzzy: 순서 보존 부분일치(needle 문자들이 순서대로 등장). 대소문자·분음 무시 정규화.
  - 컬럼 스코프 옵션(특정 컬럼만).
- **입력 라우팅**: 검색창 텍스트 파싱 — `/pattern/` 또는 `regex:...` → Regex, `fuzzy:...` → Fuzzy, 그 외 → Contains. 잘못된 정규식은 상태바에 오류 표시.
- **연계**: 기존 "Find Next" 스트리밍 탐색에 매처만 교체. 일치 시 해당 셀로 스크롤·선택.
- **테스트**: regex/fuzzy/contains 각 일치·불일치, 컬럼 스코프, 잘못된 정규식 예외.

#### D. Go to Row
- 원본 행번호(1-based) 입력 → 해당 데이터 행이 현재 뷰(`_viewMap`)에 보이면 그 viewRow로 스크롤·선택. 필터로 가려졌으면 안내.
- 역매핑: `_viewMap`에서 `dataRow == n-1`인 인덱스 탐색(필터 시 선형/이진). `Ctrl+G`.

#### E. 내보내기 (CSV / Markdown / JSON / HTML)
- **엔진**: `CsvExporter.cs` — 현재 표시 순서(`_viewMap`)·표시 컬럼(숨김 제외, Phase 2 G와 연계)만, **행 단위 스트리밍**(전체 구체화 금지).
  - CSV: RFC4180 이스케이프(`,`/`"`/개행 포함 시 `"`로 감싸고 `"`→`""`).
  - JSON: 객체 스트리밍, **중복 헤더는 안정 키**(`value`, `value (2)`) — macOS 감사 수정 반영.
  - Markdown: 파이프 테이블. HTML: `<table>` + 최소 스타일.
- **UI**: File ▸ Export as… + `SaveFileDialog`. 대용량 시 진행 표시.
- **테스트**: 각 형식 이스케이프, 중복 헤더 키, 빈 값/멀티라인 셀.

---

### Phase 2 — 표현식 필터 + 생산성

#### C. 표현식 필터
- **엔진**: `AdvancedFilterExpression.cs` — 토크나이저 + 재귀하강 파서.
  - 문법: `AND` / `OR` / `( )`, 비교 `== = != < <= > >= contains`.
  - 컬럼 참조: 헤더명(대소문자 무시) 또는 `Column<N>`(1-based).
  - 값: 따옴표 문자열(`"서울 특별시"`) 또는 베어 토큰. 비교는 양쪽 숫자면 수치, 아니면 문화권 무시 문자열 비교.
  - 컴파일 결과 `Func<string[], bool>` → 기존 `ApplyFilterAsync` / `FilterWithinViewAsync`에 그대로 투입.
- **UI**: 필터창에 "고급(표현식)" 모드 토글. 파싱 오류는 인라인 메시지(`Unknown column` / `Invalid syntax`).
- **테스트**: macOS 케이스 — `age>65`, `age=65`, `score<=10`, `name contains kim AND city = 서울`, 괄호 우선순위, 알 수 없는 컬럼, 빈 식.

#### G. 컬럼 숨기기/보이기
- `DataGridView` 컬럼 `Visible` 토글 + 체크리스트 팝업(View 메뉴). 숨김 집합은 내보내기(E)·상세패널·저장된 뷰(H)와 일관.

#### J / K. 클립보드·드래그앤드롭 가져오기
- **K**: 폼 `AllowDrop`, `DragEnter`(파일/텍스트 허용), `DragDrop`(파일 경로면 열기, CSV 텍스트면 임시파일로 저장 후 열기).
- **J**: File ▸ Open from Clipboard — 클립보드가 CSV 텍스트 / 파일 경로 / `file://` URL인지 해석(`ClipboardImportResolver` 대응).
- **임시파일 수명 관리**: 텍스트 가져오기로 만든 임시파일은 닫을 때 정리(macOS ROADMAP 미해결 항목 → 처음부터 처리).

#### L. 성능 대시보드
- 모달: 행수, 파일 크기, 저장 모드(RAM/디스크), 인덱싱 소요시간, 처리량(GB/s), 인코딩, 컬럼 수.
- 기존 `IndexProgress` + 인덱싱 시작/종료 타임스탬프 수집만 추가.

---

### Phase 3 — 분석·통계·영속화 (엔진 1:1 + 시트 UI)

#### M. 분석 (Analytics)
- **엔진**: `CsvAnalytics.cs` 이식.
  - 수치 분포: min/max/mean/median/q1/q3/std + 히스토그램(균등 bin).
  - 날짜 히스토그램: 일/주/월/년 버킷(`yyyy-MM-dd`, `yyyy-Www`, `yyyy-MM`, `yyyy`), 선택 값컬럼 합/평균.
  - 중복 탐지: 키 컬럼 조합으로 그룹화, 2건 이상만, 원본 행번호 정렬.
  - 그룹바이: Count/Sum/Mean/Median/Min/Max/UniqueCount/Std.
- **UI**: Analysis 메뉴 → 컬럼·매개변수 다이얼로그(넓은 필드, 고정 액션 버튼) → 결과 표/텍스트. 기본 컬럼은 추론 타입(A)으로 가이드.
- **메모리**: 전체 구체화 대신 `_viewMap` 순회 스트리밍/표본화 우선 고려.

#### N. 통계 (Statistics)
- **엔진**: `CsvStatistics.cs` 이식 — 상관(Pearson/Spearman), 독립 t검정(Welch df), 대응 t검정, 카이제곱.
  - p값: 정규근사 금지. **Student-t 양측 = 정규화 불완전 베타(연분수)**, **카이제곱 생존 = 정규화 불완전 감마 Q**. macOS의 `regularizedIncompleteBeta`/`regularizedGammaQ` 그대로 이식.
  - 신뢰구간: t 임계값 이분 탐색.
- ⚠️ **검증 필수**: 경계조건(작은 df, 극단 p)에서 미세 오차 가능. macOS와 동일 입력에 대한 **수치 대조 단위테스트**로 고정(독립/대응 t, 카이제곱 p값 회귀 테스트 포함).

#### F. 영속 인덱스 캐시 (`.ncvidx`)
- `RecordIndex` 오프셋 배열을 `%LocalAppData%\NanumCsvViewer\index\`에 직렬화.
- 검증 키: 파일 경로 + 크기 + 최종 수정시각 + 인코딩 + 구분자. 불일치 시 재인덱싱.
- 쓰기는 로드 완료 경로 밖에서(백그라운드), 너무 크면 생략.
- 설정: 폴더 열기 / 비우기 / 닫을 때 삭제 토글.

#### H. 저장된 뷰
- **엔진**: `SavedCsvView.cs` — name, filterText, filterColumn, sortKeys, hiddenColumns, searchQuery, currentColumn.
- 파일별 JSON(`%LocalAppData%`). View ▸ Save / Restore. (v1은 파일당 1개; 다중 명명 북마크는 후속.)

---

### Phase 4 — 피벗 & 차트 (대형)

#### O. 피벗 빌더
- **엔진**: `CsvAnalytics.PivotTable` — 행/열/값/필터 + 날짜 그룹핑 + 다중 측정 + 합계. macOS 알고리즘 1:1.
  - 레이아웃: Values-only / Rows+Values / Columns+Values / Rows+Columns+Values 모두 지원.
  - 측정: 같은 필드 다중 추가(예: 한 컬럼에 Mean/Std/Min/Max). null 차원값은 `null` 그룹.
  - 집계는 백그라운드 스레드 + 취소(stale 작업 폐기).
- **UI**: 별도 폼. 좌측 필드 목록(타입 태그) + 행/열/값/필터 4개 드롭존(`ListBox` 드래그 또는 우클릭/버튼 할당, 칩 재정렬), 우측 큰 결과 패널(피벗 표 / 차트 탭).

#### P. 피벗 차트 — **GDI+ 자작**
- `ChartControl : Control`, `OnPaint`에서 `Graphics` 직접 렌더.
- 차트 종류: Bar / Grouped Bar / Stacked Bar / Line. 날짜형 카테고리(`^\d{4}(-\d{2}){0,2}$|^\d{4}-W\d{2}$`)는 기본 Line.
- 구성요소: 축·눈금·격자, 범례, 카테고리/시리즈/값, 호버 툴팁(마우스 위치 근처, 컴팩트·불투명).
- 입력 모델: macOS `PivotChartModel`(categories, series, points, recommendedKind) 대응 구조체.
- 라이트/다크 테마 색상 연동(기존 `Theme.cs`).
- ⚠️ 단일 exe·EV 서명에 영향 없음(외부 의존 0).

#### I. 다중 파일/탭 — **제외(최후순위)**
단일 `Form1` 전제(전역 상태 다수)를 다문서로 바꾸는 리팩터링은 회귀 위험이 커 이번 범위에서 제외. 향후 필요 시 `TabControl` 기반 별도 과제.

---

### 상시 병행

#### Q. 긴 셀 1줄 미리보기 제한
- 셀 렌더 시 1줄·길이 상한(예: 수백 자) 미리보기로 잘라 그려 긴 XML/CLOB의 비싼 텍스트 레이아웃 회피. **전체 값은 상세 패널(F4)·복사로 보존.** 셀 `OwnerDraw` 또는 `CellFormatting`.

#### 테스트 전략
- 신규 엔진 파일마다 `NanumCsvViewer.Tests`에 단위테스트. macOS `Tests/CsvCoreTests`가 케이스 참고서.
- 통계(N)는 macOS 산출값과 수치 대조 회귀 테스트 필수.

---

## 5. 위험 요소 및 대응

| 위험 | 영향 | 대응 |
|---|---|---|
| 통계 수치 정확도(N) | 잘못된 p값 | macOS 대조 단위테스트로 고정, 경계조건 케이스 추가 |
| GDI+ 차트 구현비용(P) | 일정 | Bar→Line→Stacked 순 점진 구현, 호버/범례는 후순위 다듬기 |
| 대용량 분석 메모리(M) | OOM | `_viewMap` 스트리밍/표본화, 전체 구체화 회피 |
| 단일 폼 결합도 | 회귀 | Phase 단위 독립 PR + 기존 기능 회귀 테스트 |
| 임시파일 누수(J/K) | 디스크 | 닫을 때 정리 로직 처음부터 포함 |

이전에 Avalonia 이식을 그리드 라이선스 문제로 접은 이력이 있어, 차트는 외부 의존 없는 **GDI+ 자작**으로 확정했다.

---

## 6. 권장 실행 순서

```
Phase 1 (A·B·D·E)  →  Phase 2 (C·G·J·K·L)  →  Phase 3 (M·N·F·H)  →  Phase 4 (O·P)
                                                                      (I 제외)
Q는 Phase 1~2 중 셀 렌더 손볼 때 함께
```

Phase 1·2만으로도 검색·필터·내보내기·타입 태그 등 체감 가치가 크고 위험이 낮다. Phase 4(피벗/차트)는 의존성·UI 규모가 커 마지막에 분리 진행한다.
