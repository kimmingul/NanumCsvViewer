# v3 — 여러 파일 작업 공간 (CSV = 테이블, 엑셀·SAS·SPSS·SQLite = DB, DuckDB 질의, 뷰 테이블)

## 1. 결정 사항 (사용자 확정)

| 항목 | 결정 |
|---|---|
| 질의 엔진 | **DuckDB** (DuckDB.NET, 프로세스 내장). 네이티브 DLL 추가를 감수한다. |
| 뷰가 보는 데이터 | 기본은 **저장된 파일 기준**. 뷰를 만들 때 "저장 안 한 편집 포함"을 고르면 편집 반영본(임시 스냅숏)으로 계산 |
| 뷰 계산 시점 | 탭을 열 때 계산, 결과는 임시 파일로 보관(재사용). 원본이 바뀌면 "원본 변경됨 — 새로 고침" |
| SQL 편집기 | 일반 사용자에게도 노출. 마법사가 만든 SQL을 보여 주고 편집 가능 |
| 단계 | 1 탭·탐색기 → 2 엔진·SQL·뷰 탭 → 3 조인·이어 붙이기·비교 마법사 → 4 에이전트 `ws.*` → 5 작업 공간 저장·실체화·문서·릴리즈(v3.0.0) |

## 2. 개념

- **작업 공간(Workspace)**: 열린 원본 + 뷰. `.ncvws`(JSON)로 저장·복원(데이터는 저장하지 않음).
- **CSV 원본 = 테이블**: 이름은 파일 이름에서 만든 SQL 식별자(겹치면 `_2`…).
- **DB 원본**: 엑셀·SAS·SPSS·SQLite 파일 하나 = DuckDB 스키마 하나, 시트·테이블 = 그 스키마의 테이블(`설문.명단`).
- **뷰 테이블**: 이름 + SQL. 열면 DuckDB가 결과를 임시 CSV로 쓰고 그 파일을 일반 문서 탭(읽기 전용)으로 연다
  → 필터·정렬·통계·고급 분석·시각화·조건부 서식·내보내기·에이전트가 모두 그대로 동작한다.
- **실체화**: 뷰 결과를 사용자가 고른 CSV·xlsx로 저장 → 일반 원본으로 추가 가능.

## 3. 엔진 설계 (DuckDB)

- 모든 원본은 DuckDB에 **CSV 파일 경로**로 등록한다(`CREATE VIEW <schema>.<table> AS SELECT … FROM read_csv(…)`).
  - UTF-8(BOM 포함) CSV: 원본 파일을 그대로 읽는다.
  - 그 밖의 인코딩(CP949·EUC-KR·UTF-16…): 앱의 파서로 UTF-8 임시 사본을 만든 뒤 등록(한 번, 원본 수정 시각이 바뀌면 다시).
  - 엑셀·SAS·SPSS·SQLite: 앱이 이미 시트·테이블을 임시 CSV로 가져오는 `WorkbookSession`을 재사용한다(DuckDB 확장 다운로드 없음).
- 컬럼 타입: 모두 VARCHAR로 읽고, 앱이 추론한 타입(정수·실수·날짜·불리언)은 `TRY_CAST` 열로 노출한다.
  식별자·범주·문자열은 VARCHAR 그대로(`001` 보존). 변환 실패는 NULL이며 미리보기에서 실패 수를 보고한다.
- 질의 결과: `COPY (<sql>) TO '<임시>.csv' (HEADER, DELIMITER ',')` → 결과 탭. 미리보기는 `LIMIT`.
- 모든 실행은 취소 가능(`duckdb_interrupt`), 진행률(행 수)·메모리 상한(`SET memory_limit`)·임시 디렉터리(`SET temp_directory`) 지정.
- 저장 안 한 편집 포함: 해당 탭 문서를 `SaveWithEdits`로 임시 CSV에 쓰고 그 사본을 등록.

## 4. UI

- **탭 바**: 파일·시트·뷰·질의 결과마다 탭. 탭별 상태(필터·정렬·숨김 열·컬럼 필터·편집·조건부 서식·스크롤·선택 셀) 유지.
  닫기(저장 안 한 편집 확인), 가운데 클릭 닫기, 순서 바꾸기.
- **작업 공간 탐색기(왼쪽)**: 원본(CSV / DB → 테이블), 뷰, 결과. 컬럼·타입·행 수. 더블클릭 = 탭 열기, 오른쪽 클릭 = 이름 바꾸기·제거·SQL 편집·새로 고침·실체화.
- **SQL 편집기**: 구문 색, 테이블·컬럼 자동완성, 실행(Ctrl+Enter)·미리보기(앞 200행)·취소, 결과 → 새 탭 또는 "뷰로 저장".
- **마법사**: 조인(키 여러 개·조인 종류·컬럼 선택·키 일치 진단), 이어 붙이기(컬럼 맞추기·타입 충돌·출처 컬럼), 비교(키 기준 추가·삭제·변경), 그룹 집계. 생성 SQL 표시.

## 5. AI 에이전트 (`ws.*`)

`ws.list_tables`, `ws.describe`, `ws.add_source`, `ws.query`(원시 행은 데이터 정책을 따름), `ws.check_join`,
`ws.create_view`, `ws.append`, `ws.compare`, `ws.materialize`(파일 저장 승인), `ws.open`/`ws.switch`(이후 `csv.*`는 현재 탭).
승인은 기존 승인 모드 정책을 따른다(뷰 생성 = 되돌릴 수 있는 작업, 실체화 = 파일 저장).

## 6. 원칙

정직한 결과(조인 행 폭증·키 불일치·타입 변환 실패·잘림을 숨기지 않음), 원본 파일 불변, 취소 가능, 1 GB급 CSV에서도 UI가 멈추지 않음.

## 구현 현황 (v3.0.0 릴리즈)

계획한 1~5단계가 모두 구현되었다(테스트 1566개 통과).

- **탭**: 다중 문서 탭(Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+W / 가운데 클릭 / 탭 메뉴), 통합 문서는 탭 하나 + 하단 시트 버튼. 보기 ▸ 작업 공간 탐색기(Ctrl+Shift+W).
- **엔진**(`Workspace/*`): DuckDB. CSV = 테이블 `T`(TRY_CAST 타입) + `T__raw`(원본 텍스트), 엑셀·SAS·SPSS·SQLite = 스키마 + 테이블. **SELECT 하나만** 허용. CP949 CSV는 UTF-8 임시 사본으로 변환.
- **SQL 편집기**: 작업 공간 ▸ 새 질의(Ctrl+Alt+Q; Ctrl+Shift+Q는 품질 프로파일 실행으로 유지). 결과는 Result 탭.
- **뷰**: 읽기 전용 View 탭, 원본 변경 시 ⚠ stale, 새로 고침.
- **마법사**(`Workspace/UI/Wizards/*`, `WizardSql.cs`): 조인(진단 포함)·이어 붙이기·비교·그룹. 생성 SQL을 보여 주고 편집 가능.
- **에이전트**: `ws.list_tables/describe/add_source/query/check_join/create_view/append/compare/group/materialize/open/switch`, `AgentGuide.md`의 작업 흐름·SQL 규칙·데이터 정책별 `ws.query` 동작. omp 분석 폴더는 작업 공간 단위로 고정(`<작업 공간 폴더>\<이름>_분석결과` 또는 첫 데이터 파일 폴더).
- **작업 공간 파일**: `.ncvws`, 파일 ▸ 작업 공간 열기(Ctrl+Shift+O)/저장, 최근 목록, 상대+절대 경로, 없는 파일은 위치 지정/건너뛰기. 뷰 정의만 저장하고 데이터는 저장하지 않음. 설치 프로그램이 `.ncvws` 연결을 등록.
- **실체화**: 테이블·뷰를 새 CSV·xlsx로 저장.

### 알려진 제한

- 뷰 새로 고침은 그 View 탭의 필터·정렬을 초기화한다.
- 뷰는 읽기 전용이다(편집하려면 파일로 저장한 뒤 연다).
- 식별자성 열은 텍스트(VARCHAR)다. 숫자 비교·조인은 `CAST`가 필요하다.
- CSV 스캔은 단일 스레드이며 열 개수가 다른 행(ragged row)을 허용한다.
- DB 원본(엑셀·SAS·SPSS·SQLite)은 앱이 다시 가져올 때만 갱신된다.
- DuckDB 때문에 포터블 exe가 x64 기준 약 48 MB로 커진다.
- 뷰가 쓰는 원본의 이름 바꾸기·제거는 거부된다.

## 7. v3.1.0 — `.ncvws` v2: `agent` 섹션·뷰 출처·메모

- **형식**: `version: 2`. v1 파일은 그대로 읽는다(`agent` 없음, 뷰에 출처 없음). v1만 아는 앱은 v2 파일을 "더 새로운 버전이 만든 파일"로 거부한다.
- **`agent`(모두 선택)**: `{ "session": {"id","file"}, "approvalMode": "always-ask|write|yolo", "dataPolicy": "SummaryOnly|RowsWithApproval|RowsAllowed", "allowLocalPython": bool, "notes": "텍스트" }`.
  - `session`: 이 작업 공간의 omp 대화. **작업 공간을 저장할 때만**, 세션 `.jsonl`이 디스크에 있을 때만 기록한다(메시지가 없는 대화는 저장 안 함). 경로는 이 PC의 것이다: 열 때 `omp --resume <file>`로 이어 가고, 파일이 옮겨졌으면 `~/.omp/agent/sessions/*`에서 id로 찾으며, 없으면(다른 PC 등) 새 대화 + 채팅 경고. 대화 내용은 파일에 저장하지 않는다. 연결이 바뀌면 작업 공간이 "수정됨"이 되어 저장 안내가 나온다.
  - `approvalMode`·`dataPolicy`·`allowLocalPython`: **앱 설정과 작업 공간 값 중 더 엄격한 쪽이 실제 값**이다(승인 always-ask > write > yolo, 데이터 SummaryOnly > RowsWithApproval > RowsAllowed, 로컬 Python 끔 > 켬). 받은 작업 공간 파일은 조일 수만 있고 풀 수 없다. omp 추가 인자 잠금(`--yolo`·`--auto-approve`·`--approval-mode X`)이 승인 모드에서는 여전히 우선한다.
  - `notes`: 사용자 메모(최대 20,000자). 작업 공간 ▸ 작업 공간 메모…로 편집, 에이전트는 `ws.notes` 읽기 / `ws.set_notes`(`notes`, `mode: replace|append`) 수정 제안(전/후 승인 카드, DataEdit 승인 종류). omp 시작·이어 가기 때마다 시스템 안내문에 4,000자까지 데이터로 주입(더 길면 앞 4,000자 + `ws.notes` 안내).
- **뷰 출처**: 뷰마다 `createdBy`(`user` | `agent` | `wizard:join|append|compare|group`), `createdUtc`, 에이전트 뷰는 `request`(그 턴의 사용자 메시지, 500자까지). 작업 공간 탐색기: 에이전트 뷰는 ✦(사용자·마법사 뷰는 ◈), 툴팁은 "✦ AI 에이전트가 만듦 · 시각 / 요청: …"(사용자: "직접 만듦", 마법사: "조인 마법사로 만듦" 등)으로 시작. v1에서 읽은 뷰는 출처 없음. SQL 편집기에서 뷰 SQL을 다시 쓰면 사용자 뷰가 되고 이름만 바꾸면 유지된다.
- **에이전트**: `ws.list_tables`에 `workspace_notes`(앞 1,000자 + 전체 글자 수)와 뷰별 `created_by`·`created_utc`·`user_request`가 추가된다. 자세한 설계는 `AGENT_INTEGRATION_PLAN.md` 11절.

### 구현 현황 (v3.1.0 릴리즈)

- `.ncvws` v2 읽기/쓰기(v1 호환), 작업 공간별 대화 연결·이어 가기·새 대화, 작업 공간별 에이전트 설정(더 엄격한 쪽 우선), 작업 공간 메모(대화상자·`ws.notes`·`ws.set_notes`·시스템 안내문 주입), 뷰 출처와 탐색기 표시(✦·툴팁).

### 알려진 제한 (v3.1.0)

- 대화 연결은 작업 공간 저장 시에만, 세션 파일이 있을 때만 저장된다. 경로는 이 PC 고유라 다른 PC에서는 새 대화 + 안내, 파일을 옮겼으면 id로 찾는다.
- 메모를 고쳐도 omp는 다시 시작하지 않는다. 시스템 안내문 사본은 다음 시작·이어 가기 때 갱신되고, `ws.notes`는 현재 텍스트를 준다.
- 작업 공간 설정은 조이기만 한다. 풀려면 앱 기본값을 바꾼다.
- 출처를 모르는 작업 공간 파일도 자기 메모를 가질 수 있다(에이전트에게는 데이터로만 보이며 정책을 바꾸지 못한다).
- 공유한 `.ncvws`의 세션 id·경로는 로컬 경로 외에 새는 정보가 없고, 대화 내용은 저장되지 않는다.

