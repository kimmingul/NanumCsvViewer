# 메인 그리드 향상 — Windows 11 적용 설계 제안

> 출처: `docs/main-grid-enhancements-development-plan.md` (macOS / Swift·AppKit 계획)
> 대상: **Nanum CSV Viewer** (Windows / .NET 10 · WinForms · `DataGridView`)
> 목적: macOS 그리드 향상 12개 Task를 Windows 버전에 어떻게 구현할지 매핑
> 작성: 2026-06-30

---

## 구현 상태 (2026-06-30) — 완료

A~I 전 항목 구현. 빌드 경고 0·오류 0, 단위테스트 136개 통과(+15: 복사 포매터·컬럼필터), 헤더 필터 팝오버는 오프스크린 렌더로 라이트/다크 시각 검증.

- **신규**: `Csv/GridCopyFormatter.cs`, `Csv/ColumnFilterState.cs`, `ColumnFilterPopup.cs` (+ `CsvExporter.RowJson`, `VirtualCsvDocument.DistinctValues`)
- **수정**: `Form1.Designer.cs`(MultiSelect), `Form1.cs`(Ctrl+C·헤더 깔때기 히트테스트·필터 파이프라인), `Form1.Features.cs`(복사 메뉴·인스펙터 버튼·헤더 필터·깔때기 그리기), `Csv/SavedCsvView.cs`(필터 영속)
- A 다중 셀 선택 · B 전체값 TSV 복사 · C 행/열 복사 · D 인스펙터 TEXT/JSON · E 구조화 필터 · F 고유값 · G 헤더 필터 팝오버 · H 날짜 범위 · I 저장된 뷰 영속 — 모두 ✅

> GUI 상호작용(드래그 선택·헤더 클릭·붙여넣기)은 실행 환경상 자동 구동 불가 — 빌드·단위테스트·팝오버 렌더만 검증. 실파일로 수동 점검 권장.

---

## 0. 목표 기능 (macOS 계획 요약)

엑셀식 셀 선택/복사, 행·열 복사, Categorical/Date 컬럼의 타입별 헤더 필터, 인스펙터(상세 패널) 복사, 날짜 범위 필터, 저장된 뷰에 필터 영속.

---

## 1. 핵심 통찰 — Windows는 상당 부분이 더 쉽다

macOS는 `NSTableView`에 셀 선택 개념이 없어 `GridSelectionModel`을 직접 만들었지만, **WinForms `DataGridView`는 엑셀식 다중 셀 선택을 네이티브 지원**한다. macOS Task 1~3(선택 모델·렌더링·TSV 복사) 대부분이 설정 + 소량 코드로 해결된다.

| macOS가 직접 구현 | Windows 대응 |
|---|---|
| `GridSelectionModel`(셀 집합·앵커·범위) | **불필요** — `DataGridView.SelectedCells` |
| 드래그 사각 선택·Shift 확장·Cmd 토글 | **네이티브** — `MultiSelect=true` + `SelectionMode=CellSelect` (Ctrl/Shift 기본) |
| 다중 셀 하이라이트 | **네이티브** |
| `NSPopover` | `ToolStripDropDown` 또는 경계 없는 `Form` |
| `Cmd+C` 모디파이어 | Windows는 **Ctrl**(토글) / **Shift**(확장) |

**이미 이식해 둔 재사용 자산**: `CsvDateParser`(날짜 파싱·타입 추론 — macOS의 "날짜 파서 일관성" 위험이 Windows에선 이미 해결), `ColumnStatistics`(추론 타입), `SavedCsvView`(뷰 저장), `CsvExporter`(JSON 안정키).

**현재 Windows 그리드 상태**(`Form1.Designer.cs`): `grid.MultiSelect = false`, `SelectionMode = CellSelect`, `gridContextMenu`에 "셀값으로 필터"만 존재.

---

## 2. 기능별 적용 방안

### A. 그리드 셀 선택 (macOS Task 1·2) — 거의 무료
- `grid.MultiSelect = true;` (CellSelect 유지)
- `grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;`
- → 클릭·드래그 사각 선택·`Shift+클릭` 확장·`Ctrl+클릭` 토글·하이라이트 즉시 동작
- 행번호 거터는 `RowHeader`라 셀 선택 대상 아님(자동 충족)
- 필터/정렬/새 파일 시 `grid.RowCount=0`으로 선택 자동 초기화

### B. 전체값 TSV 복사 (macOS Task 3) — 커스텀 필요 ⚠️
- **함정**: `OnCellValueNeeded`가 길이 제한 미리보기(`PreviewCell`)를 반환 → 기본 `Ctrl+C`는 **잘린 값** 복사
- 해결: `Ctrl+C`/메뉴 복사를 가로채 `_doc.GetDisplayRow`로 **전체 값**을 읽어 TSV 생성
  - 신규 `Csv/GridCopyFormatter.cs`(또는 `CsvExporter`에 `ToTsv`): 선택 셀 경계 사각형 → 행별 탭 구분, 빈 셀은 빈 문자열
  - `grid.KeyDown`에서 `Ctrl+C` 처리 + 컨텍스트 메뉴 "선택 영역 복사"
  - `Clipboard.SetText`(TSV) → 엑셀/시트에 자연 붙여넣기

### C. 행/열 전체 복사 (macOS Task 4)
- 기존 `gridContextMenu`에 **"행 전체 복사" · "열 전체 복사"** 프로그램적 추가
- 행: `_doc.GetDisplayRow(row)` → 표시(숨김 제외) 컬럼만 TSV
- 열: 표시 행 스캔 → 대용량은 `RunViewOpAsync` 유사 백그라운드 + 취소, 첫 줄 헤더 포함

### D. 인스펙터(상세 패널) 복사 (macOS Task 5)
- `detailHeaderLabel` 옆에 **`복사(TEXT)` · `복사(JSON)`** 버튼
- TEXT: `detailRichText` 가독 텍스트
- JSON: 선택 행 → 객체. **`CsvExporter`의 중복 헤더 안정키 로직 재사용**(행 1건)
- Windows는 통계/성능을 별도 다이얼로그로 표시 → JSON은 **행 표시일 때만 활성화**(macOS의 `InspectorContentKind` 분기보다 단순)

### E. 구조화된 컬럼 필터 상태 (macOS Task 6) — 아키텍처 핵심
- 신규 `Csv/ColumnFilterState.cs` (JSON 직렬화):
  ```csharp
  record SelectedValuesFilter(int Column, HashSet<string> Values, bool IncludeBlanks);
  record DateRangeFilter(int Column, DateTime? Start, DateTime? End);
  class ColumnFilterState {
     Func<string[],bool> Predicate();              // AND 결합
     IEnumerable<string> Descriptions(string[] headers);
  }
  ```
- 기존 `BuildCombinedPredicate()`에 **3번째 항 합류**: 텍스트 → 셀값 → 컬럼필터
- 기존 `_valueConditions`(Filter by Cell)는 호환 위해 **병행 유지** 권장(추후 통합)
- `UpdateFilterStatus`(토큰/설명)·`OnClearFilterClick`에 컬럼필터 포함

### F. 고유값 수집 (macOS Task 7) — 엔진
- `VirtualCsvDocument`에:
  ```csharp
  public IReadOnlyList<(string Value, int Count)> DistinctValues(
      int column, bool withinCurrentView, CancellationToken ct);
  ```
- 표시 행 또는 전체 데이터 행을 취소 가능 스캔, **개수 내림차순→값 정렬**, 빈 값 `""` 보존(UI `(빈 값)`)
- 분석 기능의 `GetDataRowUncached` 패턴 재사용

### G. 헤더 필터 어포던스 + 팝오버 (macOS Task 8) — 가장 큰 작업
- 헤더는 이미 오너드로우(`OnGridCellPainting`: 타입 배지+정렬 화살표) → **Categorical/Date 컬럼에 깔때기 아이콘** 추가 그림(고정 히트영역)
- 히트 테스트: `OnColumnHeaderMouseClick`에서 클릭 좌표가 깔때기 영역이면 팝오버(정렬 안 함), 아니면 기존 정렬
- 팝오버: `ToolStripDropDown`에 패널 호스팅(헤더 셀 아래) 또는 경계 없는 `Form`
  - Categorical: 검색 `TextBox` + `CheckedListBox`(고유값+개수) + 전체선택/해제/적용/취소
  - Date: `DateTimePicker` 2개(시작/끝, 무한 체크) + 적용/취소
- 고유값 **백그라운드 비동기 로드**(로딩 표시), 적용 시 `ColumnFilterState` 갱신 → 재필터
- 활성 필터는 헤더 깔때기 색/채움으로 표시(배지처럼 오너드로우)

### H. 날짜 범위 필터 (macOS Task 9)
- **`CsvDateParser`(이식 완료) 재사용** — 타입 추론과 동일 파서라 일관성 보장(macOS 위험 요소 선해결)
- 술어: 셀 파싱 → `[Start,End]` 양끝 포함, 파싱 실패는 불일치, 빈 경계는 무한
- 첫 구현은 macOS 권고대로 체크박스 값 필터와 날짜 범위를 **상호 배타**

### I. 저장된 뷰 저장/복원 (macOS Task 10)
- **이식 완료한 `SavedCsvView`** 에 `ColumnFilterState?` 필드 추가(널 허용 → 구버전 호환)
- 저장 시 포함, 복원 시 필터→정렬 순서로 적용(`RestoreSavedViewAsync` 확장)

### (메뉴/상태 검증·최종 — macOS Task 11·12)
- `UpdateFeatureMenuState`에 복사/필터 명령 활성/비활성(문서 없음·선택 없음·바쁨 시 비활성)
- 복사 명령 이름 통일: `선택 영역 복사 / 행 전체 복사 / 열 전체 복사 / 복사(TEXT) / 복사(JSON)`
- 모든 신규 문자열은 영/한 `LT(en,ko)` 처리

---

## 3. 신규/수정 파일

**신규**
- `Csv/ColumnFilterState.cs` — 구조화 필터(직렬화)
- `Csv/GridCopyFormatter.cs` — 선택 셀/행/열 → TSV (또는 `CsvExporter`에 통합)
- `ColumnFilterPopup.cs` — 헤더 필터 팝오버(코드 전용)

**수정**
- `Form1.Designer.cs` — `grid.MultiSelect=true`, `ClipboardCopyMode`
- `Form1.cs` — Ctrl+C 가로채기, 컨텍스트 메뉴 행/열 복사, 헤더 히트테스트, `BuildCombinedPredicate`/`UpdateFilterStatus`/`OnClearFilterClick` 확장
- `Form1.Features.cs` — 헤더 깔때기 아이콘 오너드로우(타입 배지 옆), 상세 패널 복사 버튼, 헤더 필터 열기
- `Csv/VirtualCsvDocument.cs` — `DistinctValues(...)`
- `Csv/SavedCsvView.cs` — `ColumnFilterState` 영속

---

## 4. 권장 구현 순서 (각 단계 독립 검증)

```
1. 다중 셀 선택(A) + 전체값 TSV 복사(B)      ← 설정+소량, 즉효
2. 행/열 복사(C) + 인스펙터 복사(D)          ← 컨텍스트 메뉴·버튼
3. 구조화 필터 상태(E) + 고유값 수집(F)      ← 엔진 기반(테스트 우선)
4. 헤더 필터 어포던스·팝오버(G)              ← 대형 UI
5. 날짜 범위 필터(H)                          ← CsvDateParser 재사용
6. 저장된 뷰 영속(I)                          ← SavedCsvView 확장
```

## 5. 주의점 (Windows 특유)

1. **복사 vs 미리보기 잘림** — `Ctrl+C`는 반드시 `_doc`에서 전체 값을 다시 읽어야 함(셀 표시값은 잘려 있음). 가장 흔한 함정.
2. **헤더 클릭 충돌** — 깔때기 히트영역을 `OnGridCellPainting`이 그린 좌표와 정확히 일치시켜야 정렬과 안 부딪힘.
3. **모디파이어** — Cmd→Ctrl. 별도 코드 없이 DataGridView 기본 동작.
4. **테스트** — 엔진(`ColumnFilterState`, `DistinctValues`, 날짜범위 술어)은 기존 121개 테스트 패턴대로 단위테스트 RED→GREEN. 헤더 팝오버는 오프스크린 렌더로 시각 검증.

---

## 6. 요약

macOS 12개 Task 중 절반 가까이가 `DataGridView` 네이티브 기능(다중 셀 선택·하이라이트·클립보드)으로 대폭 단순화되고, 이미 이식한 엔진(`CsvDateParser`·`ColumnStatistics`·`SavedCsvView`·`CsvExporter`)을 재사용해 위험이 낮다. 최대 작업은 G(헤더 필터 팝오버)이며, 나머지는 소~중 규모다.
