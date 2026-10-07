# v2 — AI 에이전트 내장 (Nanum CSV Viewer + omp)

## 1. 목표

앱 안의 채팅 패널에서 omp(oh-my-pi) 에이전트에게 자연어로 요청하면, 에이전트가 **지금 열려 있는 창**을 직접 조작한다
(필터·정렬·통계·분석·셀 편집·저장). RAD Agent가 RAD Studio를 조작하는 방식과 같다.

## 2. 구조

```
NanumCsvViewer.exe
 ├─ AgentChatPanel (WebView2, RAD Agent 채팅 UI 이식)  ⇄  ChatController
 ├─ ChatController: omp 이벤트 → 페이지 메시지, 페이지 명령 → RPC
 ├─ OmpRpcClient: omp --mode rpc-ui 자식 프로세스, JSONL(stdin/stdout), v2 청크
 └─ CsvHostTools (ICsvToolExecutor): csv.* 도구 → 실행 중인 Form1 상태
```

- **전송**: omp RPC 모드(`omp --mode rpc-ui --cwd <파일 폴더> --config <호스트 yml> --append-system-prompt <가이드>`).
  MCP 서버를 두지 않는다. 앱이 omp의 호스트가 되어 `set_host_tools`로 도구를 등록하고 `host_tool_call`을 직접 처리한다.
- **UI**: RAD Agent(`D:/repo/RADAgent/src/chat`, MIT, 같은 회사)의 HTML/CSS/JS 채팅 페이지를 이식한다.
  WebView2(`Microsoft.Web.WebView2`)로 `https://nanumcsv.local/`에 매핑. 페이지 ↔ 호스트 메시지 형식은 RAD Agent와 같다.
- **omp 위치**: 설정의 경로 → PATH의 `omp.exe`(그다음 `omp.cmd`/`omp.bat`) → `%LOCALAPPDATA%\omp\omp.exe`(§16에서 바뀐 순서·설치·검증). 최소 버전 18.4.4. 없으면 패널에 설치 안내와 설정 도우미.
- **omp의 역할**: 모델·인증·작업 목록·하위 에이전트·세션 저장은 omp가 맡는다. 앱은 LLM을 직접 부르지 않는다.

## 3. csv.* 도구 (1차)

| 도구 | 권한 | 내용 |
|---|---|---|
| `csv.info` | 읽기 | 파일·시트·행/열 수·컬럼 이름·추론 타입·현재 필터/정렬·편집 상태 |
| `csv.column_stats` | 읽기 | 컬럼별 결측·고유값·최소/최대/평균/분위수·상위 빈도(집계값만) |
| `csv.get_rows` | 데이터 | 현재 뷰의 행 값(정책에 따라 승인·상한) |
| `csv.set_filter` / `csv.clear_filter` | 뷰 | 고급 식 필터 적용·해제(화면에 반영) |
| `csv.sort` | 뷰 | 정렬 키 설정·해제 |
| `csv.goto` | 뷰 | 셀로 이동·선택 |
| `csv.run_analysis` | 읽기 | 기술통계·GLM·ANCOVA·GLzM·로지스틱 등. 결과 창을 열고 구조화된 JSON(계수·검정·지표) 반환 |
| `csv.quality_scan` | 읽기 | 데이터 품질 발견 목록 |
| `csv.edit_cells` | 편집 | 셀 값 변경(편집 덮개, 실행 취소 가능). 승인 카드 |
| `csv.undo` | 편집 | 편집 되돌리기 |
| `csv.save_edits_as` | 파일 쓰기 | 새 파일로 저장. 항상 승인 |

결과는 짧은 텍스트 + JSON. 큰 결과는 상한을 두고 잘린 사실을 적는다.

## 4. 권한과 개인정보

- **데이터 정책(`AgentDataPolicy`)**: 기본 `SummaryOnly` — 스키마·집계·분석 결과만 모델로 간다. 원시 행은 `RowsWithApproval`(요청마다
  승인, 상한) 또는 `RowsAllowed`(상한)일 때만. 랜딩 페이지의 "CSV는 PC를 떠나지 않습니다"는 v2부터 "에이전트를 쓰지 않거나 요약만
  허용하면"으로 조건을 명시한다.
- **편집·저장**: 편집은 시트 편집 덮개에 쌓이고(원본 불변, Ctrl+Z), 저장은 항상 승인 카드. 승인 거부·중지 시 도구는 오류로 끝난다.
- **기록**: 에이전트가 실행한 도구와 인자를 `%LOCALAPPDATA%\NanumCsvViewer\agent\tool-log.jsonl`에 남긴다.
- omp 자체 도구(bash·write 등)의 승인은 omp `extension_ui_request`를 같은 승인 카드로 보여 준다.

## 5. RPC 처리 순서

1. `ready` → (v2 제공 시) `negotiate_protocol 2` → `set_host_tools` → `get_state`, `get_available_commands` →
   `get_available_models`, `get_available_thinking_levels`.
2. 입력: 대기 중이면 `prompt`, 실행 중이면 Enter=`steer`, Ctrl+Enter=`follow_up`. Esc/■ = `abort`(5초 후 강제 재시작).
3. 이벤트: `message_update`(text/thinking/toolcall 델타), `tool_execution_*`, `agent_end`(isTerminal≠false면 턴 종료),
   `prompt_result`, `queue_update`, `notice`, `auto_retry_*`, `auto_compaction_*`, `subagent_*`.
4. `host_tool_call` → `ICsvToolExecutor.ExecuteAsync`(UI 스레드, 비동기 승인) → `host_tool_result`(1 MiB 초과 시 오류 문구).
5. `extension_ui_request`: select/confirm/input/editor → 채팅 카드 또는 모달, notify/setStatus → 알림.
6. 자식 종료: 같은 세션으로 1회 재시작(60초 간격 제한). 모든 프레임은 `%TEMP%\NanumCsvViewer\rpc.log`.

## 6. 단계

1. 계약·문서(이 문서, `Agent/AgentContracts.cs`).
2. `OmpRpcClient` + `ChatController`(테스트: 가짜 omp 스트림으로 핸드셰이크·청크·도구 호출·승인).
3. `AgentChatPanel`(WebView2 + 이식한 채팅 자산, 한국어/영어).
4. `CsvHostTools` + `Form1.Agent.cs`(실행 중인 창 조작, 데이터 정책, 기록).
5. 통합: 툴바/메뉴 토글, 오른쪽 도킹, 설정(omp 경로·데이터 정책), 실제 omp로 "필터 걸고 회귀" 끝까지 스모크.

## 7. 구현 상태 (v2.0.0)

- 1~5단계 구현. 보기 ▸ AI 에이전트 패널(Ctrl+Shift+A, 툴바 `✦ AI`), 보기 ▸ AI 에이전트 설정…(데이터 공유·행 상한·omp 경로·추가 인자).
- 실제 omp 18.4.4 + 모델로 끝까지 확인: "2023년 이후 남성만 필터 → sbp ~ age GLM" 요청에 그리드 필터 칩·결과 창이 열리고,
  답변의 age 계수 0.454(SE 0.058, p 1.28e-10, R² 0.512)가 statsmodels OLS와 일치. "1행 age를 75로" 요청은 승인 카드(− 74 / + 75)
  → 승인 → 편집 덮개에 반영(앰버, `*편집됨`, Ctrl+Z 1단계).
- omp 자체 도구 승인(`Allow tool: …`)도 같은 카드로 표시. 승인 모드는 채팅 창 아래 드롭다운(§9)으로 고르며 host.yml `tools.approvalMode`에 반영된다.

| 데이터 정책 | `csv.get_rows` | `csv.column_stats` 상위 값 | `csv.quality_scan` 예시 값 |
|---|---|---|---|
| 요약만(기본) | 거부(설정 안내) | 생략 | 생략(행 번호만) |
| 행 값 — 요청마다 승인 | 승인 카드 후 상한까지 | 승인 카드 | 생략 |
| 행 값 — 승인 없이 | 상한까지 | 포함 | 포함 |

수치 집계(최소·최대·평균·분위수)와 분석 결과(요인 수준 이름 포함)는 정책과 무관하게 보낸다. 도구 기록에는 셀 값을 남기지 않는다.
- 사용량 팝업(get_session_stats + `omp usage --json`, 60초 캐시)과 세션 목록(omp 세션 폴더, 최신순, 전환 시 기록 재생) 구현. omp가 없으면 설치 안내 링크. README·랜딩 페이지 개인정보 문구 갱신(v2 브랜치, 배포는 master 병합 때).

## 8. 확장 (편집·서식·Python)

- 도구 추가: `csv.insert_rows`, `csv.delete_rows`, `csv.add_column`, `csv.delete_column`(승인, AI 실행 취소 1단계), `csv.goto`(셀 주소),
  `csv.format_add/list/remove/clear`(조건부 서식, 승인 없음), `csv.regex_count`, `csv.regex_replace`, `csv.export_view`(로컬 Python 허용 시만),
  `csv.show_markdown`, `csv.show_image`.
- 로컬 Python: 설정 "로컬 Python 분석 허용"(기본 꺼짐). 켜면 omp `--cwd` = `<파일명>_분석결과`, 데이터는 `data\`로 내보내고 omp `eval`(Python)로 분석.
  Python `eval`은 대화마다 처음 한 번 승인. 보고서(.md)·그림은 같은 폴더, 보고서 창·그림 창·채팅 미리보기.
- Python LSP: `%LOCALAPPDATA%\NanumCsvViewer\python-tools\venv`(basedpyright·ruff 고정 버전) + 분석 폴더 `.omp\lsp.json`(절대 경로).
  omp는 시작할 때만 lsp.json을 읽으므로 첫 설치 뒤 같은 대화로 재시작한다. 작업 폴더가 바뀌면 `--resume`으로 재시작한다.
- 실제 확인(omp 18.4.4 + 모델): "성별 나이–혈압 산점도+회귀선 PNG, 마크다운 보고서" 요청 → 내보내기·스크립트 작성·Python 실행(승인 1회)·
  그림·보고서 저장·보고서 창/그림 창/채팅 미리보기. 회귀식(F: 97.6 + 0.54·age, M: 104.5 + 0.50·age, R² 0.56)과 상호작용 p 0.48이 statsmodels와 일치.

## 9. v2.2.0 — 승인 모드, 열 순서, 조건부 서식 테마·되돌리기

- 승인 모드 드롭다운(채팅 창 아래, 설정 창에도): 항상 묻기(always-ask) / 편집 자동 승인(write) / 모두 허용(yolo, 기본값). 바꾸면 같은 대화로 omp 재시작(작업 중이면 끝난 뒤).
  yolo를 고르면 확인 창, 기본값 yolo로 처음 연결할 때 한 번 안내. 앱 승인 카드도 모드를 따른다: always-ask = 모두 묻기,
  write = 데이터 편집(실행 취소 가능) 자동, yolo = 파일 저장까지 자동. 원시 행 공유는 항상 데이터 정책을 따른다. 자동 승인은 채팅에 알림.
- 열 표시 순서: 중간 삽입(현재 열 앞/뒤·처음·끝), 열 이동(시트 편집 모드에서 헤더 끌어다 놓기, 왼쪽/오른쪽으로 이동), 각각 실행 취소 1단계.
  저장·필터·정렬·분석·에이전트·복구 모두 표시 순서. 에이전트 `csv.add_column`(position), `csv.move_column`.
- 조건부 서식: 서식 전용 되돌리기/다시 실행(최근 20개, 에이전트 변경 포함, 툴바 `↶ 서식`), 이름 있는 테마 색 7가지(밝은/어두운 짝),
  직접 고른 색의 테마 자동 조정, 글자색 자동 대비. 에이전트 `csv.format_undo`, 테마 색 이름 지정.
- omp 추가 인자에 `--approval-mode X`·`--yolo`·`--auto-approve`가 있으면 그 값이 실제 모드가 되어 드롭다운·설정이 잠기고, 마우스를 올리면 이유를 보여 준다. 앱 카드도 실제 모드를 따른다.

## 10. v3.0.0 — 여러 파일 작업 공간 `ws.*` 도구

- 도구: `ws.list_tables`(테이블·뷰·열 타입·행 수·열린 탭·활성 탭), `ws.describe`(타입, **변환 실패 수**, 고유/null 수, 키 후보), `ws.add_source`(데이터 파일만, 읽기 전용),
  `ws.query`(SELECT 하나만), `ws.check_join`(조인 전 진단: 일치/불일치 키, null·중복, 카디널리티, 예상 행 수), `ws.create_view`·`ws.append`·`ws.compare`·`ws.group`(뷰 생성, 원본 불변),
  `ws.materialize`(새 파일로 저장, 열려 있거나 등록된 원본은 덮어쓰지 않음), `ws.open`·`ws.switch`(활성 탭 전환). `csv.*` 도구는 항상 활성 탭만 다룬다.
- 승인: 뷰 생성·실체화는 승인 모드를 따른다(항상 묻기 = 묻고, 편집 자동 승인·모두 허용 = 자동). 실체화는 파일을 쓰므로 승인 카드가 필요하다.
- `ws.query`와 데이터 정책: **요약만** = 행 수·열 이름/타입·null/고유 수·숫자 열 최소/최대/평균만(행 없음, 집계 SQL을 쓰도록 안내). **요청마다 승인** = 사용자가 승인하면 상한까지 행, 거부하면 집계만.
  **승인 없이** = 상한까지 행. `open_tab:true`는 정책과 무관하게 사용자에게 전체 결과를 Result 탭으로 보여 주되 에이전트가 받는 내용은 정책을 따른다.
  `ws.describe`의 실패 예시 값은 요약만에서 생략, 조인·비교 진단은 숫자만 반환한다. 도구 기록에는 셀 값을 남기지 않는다.
- SQL 규칙: 한글·공백·숫자로 시작하는 이름은 `"…"`; DB 테이블은 `"스키마"."테이블"`; 타입 열(`T`, TRY_CAST 실패 = NULL)과 원본 텍스트 `T__raw`; 식별자성 열은 VARCHAR이므로 숫자 비교·조인은 `CAST`. CP949 CSV는 UTF-8 임시 사본으로 읽는다.
- 뷰는 기본이 **저장된 파일** 기준(`include_unsaved_edits:true`면 편집 반영 스냅숏), 원본이 바뀌면 `stale`이고 열 때 다시 계산한다. 뷰 탭은 읽기 전용이라 `csv.*` 편집은 거부된다.
- 분석 폴더(로컬 Python 허용 시 omp `--cwd`)는 **작업 공간 단위로 고정**: `<작업 공간 파일 폴더>\<작업 공간 이름>_분석결과`, 작업 공간 파일이 없으면 첫 데이터 파일의 폴더. 탭을 바꿔도 omp가 재시작되지 않는다.
  `AgentGuide.md`에 같은 내용(작업 흐름, SQL 규칙, 정책별 `ws.query` 동작)을 담았다.

## 11. v3.1.0 — 작업 공간별 대화·설정·메모

`.ncvws` v2의 `agent` 섹션(형식은 `WORKSPACE_PLAN.md` 7절)을 에이전트가 사용하는 방식.

- **작업 공간별 대화**: 작업 공간 저장 시 지금 omp 세션(id + `.jsonl` 경로, `get_state.sessionFile`)을 기록한다(세션 파일이 디스크에 있을 때만). 작업 공간을 열면 `ChatController.ResolveConversation`이 저장된 경로를 먼저, 없으면 id로 `~/.omp/agent/sessions/*`를 찾고, 찾으면 omp를 `--resume <파일>`로 시작해 채팅 기록을 다시 불러오며 안내를 올린다. 못 찾으면(다른 PC 등) 새 대화 + 경고. 작업 공간 파일이 없으면 예전처럼 항상 새 대화.
  - 작업 공간 전환은 `ChatController.SwitchWorkspaceAsync(options, context, conversation)`: 진행 중이면 중단하고 설정·작업 공간 상태를 바꾼 뒤 그 작업 공간의 대화와 분석 폴더로 omp를 재시작한다(RPC `switch_session`은 다른 폴더의 세션을 거절하므로 쓰지 않고 명령줄 `--resume`을 쓴다).
  - 작업 공간 ▸ "이 작업 공간의 새 대화 시작"(작업 공간 파일이 열려 있을 때만; 확인 → `StartNewConversationAsync`): 같은 작업 공간·설정으로 새 세션에서 재시작하고 저장된 연결을 비운다. 작업 공간을 저장하면 새 연결로 바뀌며 이전 대화는 omp 세션 목록에 남는다.
- **작업 공간별 설정 — 더 엄격한 쪽이 이긴다**: 승인 모드·데이터 정책·로컬 Python의 실제 값 = 앱 설정과 작업 공간 값 중 엄격한 쪽. 순서: 승인 always-ask > write > yolo, 데이터 SummaryOnly > RowsWithApproval > RowsAllowed, 로컬 Python 끔 > 켬. 받은 작업 공간 파일은 조일 수만 있고 풀 수 없다(보안 규칙). omp 추가 인자 잠금(`--yolo`·`--auto-approve`·`--approval-mode X`)은 승인 모드에서 여전히 우선하며, 그때는 작업 공간의 승인 제한을 표시하지 않는다.
  - 설정 창(보기 ▸ AI 에이전트 설정…): 작업 공간 파일이 열려 있으면 맨 위에 "설정 적용 대상" 선택(이 작업 공간(파일명) 기본 / 앱 기본값(모든 작업 공간)); 세 컨트롤은 고른 범위의 값을 보여 주고, 규칙 설명이 붙으며, OK 뒤 더 엄격한 값이 이겨 적용되지 않은 항목은 메시지 상자로 알린다. omp 경로·추가 인자·행 상한은 앱 전체 설정.
  - 채팅 승인 드롭다운: 컨트롤러가 yolo 확인까지 마친 뒤 `ChatController.ApprovalModeApplier` 훅을 호출한다. 호스트(`Form1`)는 작업 공간 파일이 열려 있으면 선택 대화상자(TaskDialog: "이 작업 공간" / "앱 기본값" / 취소)를 띄워 고른 곳에 저장하고 작업 공간 제한을 합친 옵션을 돌려준다(null = 취소). 작업 공간 파일이 없으면 예전처럼 앱 설정에 저장.
  - 상태: 상태 JSON에 `workspaceLimit`(툴팁 글. 작업 공간이 실제로 무언가를 조일 때만 채워지고, 아니면 빈 문자열)와 `workspaceLimitLabel`("🔒 Workspace" / "🔒 작업 공간")이 추가되어, 채팅 표시줄에 승인 선택 옆 칩으로 나온다. 툴팁: "작업 공간 설정으로 제한됨: 승인 모드 …, 데이터 공유 …, 로컬 Python 분석 꺼짐…"(영문 "Limited by the workspace settings: …"). 제한이 나타날 때 채팅에 안내를 올린다.
- **메모 도구**: `ws.notes`(현재 메모 읽기), `ws.set_notes`(`notes`, `mode: replace|append`; 전/후 승인 카드; 승인 종류 DataEdit → write·yolo에서 자동, always-ask에서는 묻기). 메모는 최대 20,000자.
- **시스템 안내문 주입**: omp를 시작하거나 이어 갈 때마다 메모를 안내문에 넣는다(최대 4,000자; 넘으면 앞 4,000자 + `ws.notes` 안내). **사용자가 쓴 데이터**로 틀을 씌워 안내문·데이터 정책·승인 모드·사용자 요청을 덮어쓰지 못한다고 명시한다. 실행 중 메모를 고쳐도 omp를 재시작하지 않는다 — 에이전트는 `ws.notes`·`ws.list_tables`(`workspace_notes`: 앞 1,000자 + 전체 글자 수)로 현재 값을 보고, 안내문 사본은 다음 시작·이어 가기 때 갱신된다.
- **출처**: 에이전트가 만든 뷰(`ws.create_view`·`append`·`compare`·`group`)는 `createdBy: "agent"` + 그 턴의 사용자 메시지(`request`, 500자)와 함께 기록된다. `ws.list_tables`의 뷰별 `created_by`·`created_utc`·`user_request`로 에이전트도 볼 수 있다.
- **위험과 한계**: 출처 불명의 작업 공간 파일이 자기 메모를 넣을 수 있다(에이전트에게는 데이터로만 보이고 정책은 못 바꾼다). 작업 공간 설정은 조이기만 한다. 대화 연결은 저장 시에만·세션 파일이 있을 때만 기록되고 경로는 이 PC 고유다. 공유한 `.ncvws`에는 로컬 경로 외에 새는 것이 없고 대화 내용은 저장되지 않는다.


## 12. v3.2.0 — AI 패널 시작 상태·지연 시작·설정 이동·AI에게 묻기

- **시작 때 AI 패널 표시 + omp 지연 시작**: 시작 패널 기본은 AI 켜짐·행 상세 꺼짐·패싯 꺼짐(설정 ▸ 패널과 배치, 또는 "마지막 상태 기억"). omp 프로세스는 앱이 뜨는 즉시 시작하지 않고 `ChatController.StartOnFirstUse`로 **지연**해 둔다. 지연된 동안 채팅에는 "준비됨 — omp는 첫 메시지를 보낼 때 시작합니다"가 보이고(상태 JSON의 `ready` 플래그로 omp 연결 전에도 입력 가능), **첫 메시지를 보내면** 그 메시지를 대기열에 넣고 omp를 시작해 연결되면 전송한다. 시작 전 설정 변경·작업 공간 전환·`/new`는 지연 시작만 다시 걸어 두고, 시작에 실패하면 메시지는 버려지고 채팅에 안내가 뜬다.
  - **미리 시작(Unreleased)**: 모델·생각·승인 선택은 연결된 뒤(`get_available_models` 핸드셰이크)에만 켜지므로, 첫 메시지까지 기다리면 그 전에는 선택이 모두 꺼져 있었다. 그래서 **AI 패널이 보이는 동안** `Form1`이 앱이 한가해진 뒤 약 1.5초에(원샷 타이머; 시작 배치 적용·패널 열기·작업 공간 열기/닫기/전환·파일 열기 뒤 `KickAgentPrewarm`이 잡는다) `ChatController.StartDeferredNow()`로 메시지 없이 omp를 시작한다. 패널이 숨겨져 있으면 시작하지 않고 계속 지연한다(패널을 열면 그때 잡는다).
  - 한가함 판단(`TryPrewarmAgent`): 작업 공간 열기·저장 중(`_wfBusy`), 파일 열기 중(`_openGate`), 앞 작업(`ForegroundWorkRunning`: 필터·정렬·분석 등)이 있으면 타이머를 다시 잡는다. 대용량 파일의 백그라운드 인덱싱은 기다리지 않는다. 로컬 Python이 켜져 있는데 작업 공간 파일도 데이터 파일도 없으면 분석 폴더가 정해지지 않았으므로(지금 시작하면 첫 파일을 열 때 폴더가 바뀌어 한 번 더 재시작) 첫 파일이 열릴 때까지 미룬다. 작업 폴더·분석 폴더 규칙은 지연 시작과 같다(시작하는 순간의 열린 파일).
  - `StartDeferredNow`는 지연 상태가 아니면 아무것도 하지 않는다(중복 시작 없음). 미리 시작한 omp가 연결 중일 때 들어온 첫 메시지(`AI에게 묻기` 등)는 대기열에 넣어 연결되면 보내고(상태의 `ready`도 참), 시작에 실패하면 지연 시작과 같이 버려지고 안내가 뜬다. omp가 없거나 시작에 실패해도 대화상자는 없고 채팅 알림·상태 줄로만 알린다.
- **AI 설정은 설정 ▸ AI 에이전트 쪽으로 이동**: 옛 "AI 에이전트 설정…" 대화상자 대신 도구 ▸ 설정…(`Ctrl+,`)의 AI 에이전트 쪽에 같은 내용(적용 대상 선택, 더 엄격한 쪽 우선, omp 경로·추가 인자·행 상한, 승인 모드 잠금)이 있다. 채팅 창의 ⚙는 이 쪽을 바로 연다. 값이 바뀐 경우에만 적용해, 다른 쪽만 고친 확인이 AI 확인창을 띄우지 않는다.
- **AI에게 묻기 우클릭 메뉴**: 셀 우클릭 ▸ AI에게 묻기 ▸ "이 컬럼 요약" · "이 값을 가진 행 분석". 선택하면 AI 패널을 열고 미리 쓴 질문(컬럼 이름·셀 값 포함)을 채팅으로 보낸다. 처음 보내는 질문이면 위의 지연 시작 경로를 탄다. 데이터 정책·승인 모드는 그대로 적용된다.

## 13. 분석 스킬 팩과 관리형 Python 환경

목적: 임상 연구·일반 통계·기계학습 Python 분석에서 앱의 검증된 도구를 먼저 쓰게 하고, 앱에 없는 분석에만 검증되지 않은 제3자 스킬과 고정된 Python 환경을 쓰게 한다.

### 13.1 분석 스킬 팩

- **구성**: `Agent/Skills/**`를 exe에 내장한다(`NanumCsvViewer.csproj`가 `Agent\Skills\**`를 임베드). K-Dense-AI/scientific-agent-skills v2.72.0(커밋 526ebce, MIT)에서 19개를 원본 그대로 복사했고, 앱 전용 `nanum-python-analysis`가 추가된다. 분류는 clinical 7, stats 9, ml 3, app 1. `manifest.json`이 분류·이유·Python 의존성·주의·토큰 추정(omp 시스템 프롬프트의 이름+첫 문장 줄 길이/4, 합계 553)과 검토 후 제외한 26개(네트워크·GPU·업로드·환자 개별 판단 등)를 기록한다. 포함한 스크립트는 네트워크/subprocess/자격증명 패턴 검사(`SkillPackTests`)를 통과한다. 고지문은 `THIRD_PARTY_NOTICES.md`·MIT 전문·`ChatAssets/NOTICE.txt` 4절에 있다.
- **로딩 조건**: 로컬 Python이 켜져 있고 마스터 스위치가 켜져 있을 때만. 꺼져 있으면 아무것도 풀지 않고 오버레이도 쓰지 않으며 비용은 0 토큰. `nanum-python-analysis`는 팩의 일부로 단독 로드되지 않고, 팩이 켜져 있으면 분류를 모두 꺼도 로드된다.
- **압축 해제**: omp 시작 전에 켜진 스킬만 `%LOCALAPPDATA%\NanumCsvViewer\skills\<내용해시>\sel-<선택해시>\`에 푼다(임시 폴더 + 원자적 이동, 재실행은 아무 일도 안 함, 다른 해시 폴더 삭제, 선택 폴더는 최근 4개만 유지). 각 선택 폴더에 MIT 전문·고지문·manifest가 함께 들어간다.
- **omp `--config` 오버레이**: `{"skills":{"customDirectories":[<사용자 디렉터리>, <우리 디렉터리>]}}`를 `%TEMP%\NanumCsvViewer\omp-skills-p<tag>.yml`(프로세스별)에 쓰고, omp를 `--config host.yml --config skills.yml` 순서로 시작한다(host.yml이 항상 첫 번째라 기존 `--config` 조회 코드는 영향이 없다). 사용자의 기존 `skills.customDirectories`는 `omp config get skills.customDirectories --json`으로 읽어 우리 것 앞에 유지한다. 사용자의 전역 `~/.omp` 설정은 건드리지 않는다.
- **includeSkills/ignoredSkills를 쓰지 않은 이유**: 실제 omp 18.4.4에서 확인한 결과 오버레이의 배열은 사용자 값을 병합하지 않고 **대체**한다. `ignoredSkills`를 쓰면 사용자가 기본으로 무시하던 스킬(debugging, ide-file-operations, project-management)이 되살아나고, `includeSkills`를 쓰면 사용자 자신의 스킬이 사라진다. 그래서 켜진 스킬만 풀어 두는 방식으로 토글을 구현했다.
- **재시작**: 원하는 스킬 키(마스터·분류·스킬별·로컬 Python 켜짐/꺼짐)가 실행 중인 것과 다르면 같은 대화로 에이전트를 다시 시작한다("분석 스킬 설정을 적용하는 중…"). 같은 선택이면 재시작하지 않는다.
- **라우팅**: `AgentGuide.md`의 '앱 도구 먼저, Python은 그 다음'(항상 프롬프트에 있음). ① 에이전트가 부를 수 있는 도구(`csv.column_stats`·`csv.quality_scan`·`csv.run_analysis`(describe|glm|ancova|glzm|logistic)·`ws.*`) ② 앱에 있지만 사용자가 메뉴에서 실행해야 하는 분석은 안내만 하고 Python으로 다시 만들지 않음 ③ 앱에 없거나 사용자가 명시한 분석만 Python+스킬. 어느 길을 썼는지 답에 밝힌다. `PythonGuide`는 스킬이 실리면 `skill://nanum-python-analysis`를 먼저 읽고 보고서 머리말에 쓴 스킬 이름을 적게 한다.
- **앱 전용 스킬**: 내보낸 데이터 배치, 출력 데이터 정책, 재현성 머리말(`assets/analysis_template.py`의 `repro_header()`), 정직한 보고, 보고서·그림 위치, 'Python 분석 (앱 검증 범위 밖)' 표기, 연구 전용 경계, `pip install`·네트워크 금지.
- **설정 UI**: 설정 ▸ AI 에이전트 ▸ '분석 스킬'(마스터, 분류별 개수 7/9/3, 스킬 목록·설명·토큰, 총 토큰 추정, 제3자 안내, Python 꺼짐 안내, '요약만'+Python 경고 — 하드 보장이 아니라 안내).

### 13.2 관리형 Python 분석 환경

- **환경**: `%LOCALAPPDATA%\NanumCsvViewer\python-analysis`의 독립 venv(system-site-packages 없음, 시스템 Python 불변). 요구사항은 내장 `Agent/Python/requirements/{core,stats,clinical,ml}.txt` 4묶음(`name==ver[; python_version<…]`).
- **설치 규칙**: pip는 항상 `--only-binary=:all: --require-virtualenv`(소스 빌드 없음). win-amd64의 CPython 3.10~3.13만 지원하고 ARM64·32비트는 거절한다. scikit-survival은 `python_version < "3.13"`(의존 패키지 ecos에 Windows 3.13 wheel 없음), aeon은 3.10에서 1.3·3.11 이상에서 1.6. 설치 뒤 import를 확인하고 `requirements.lock`(pip freeze)과 묶음 지문 `env.json`을 기록한다.
- **에이전트 연결**: host.yml의 `python.interpreter`를 이 venv로 지정(omp 18.4.4에서 설정 존재와 실제 `eval`이 venv python을 쓰는 것을 확인). 가이드에 인터프리터·설치된 묶음·"스스로 pip 금지, `py.ensure_packages` 사용"을 적는다.
- **`py.ensure_packages`**: 에이전트가 묶음 이름(`core`·`stats`·`clinical`·`ml`)으로 요청하면 앱이 사용자 승인(FileSave 종류 — 승인 모드를 따름)을 받은 뒤 관리형 환경에만 설치하고 진행을 채팅 알림으로 올린다. 환경이 없으면 만들고, 에이전트가 다시 시작될 때 `eval`이 그 환경으로 바뀐다.
- **재현 패키지 내보내기**: 도구 ▸ Python 분석 재현 패키지 내보내기… — 스크립트·보고서·그림·`requirements.lock`·README를 폴더/zip으로. 데이터는 기본 제외.

### 13.3 한계와 위험

- 스킬은 제3자 지시문이다. 일부 원문이 `uv pip install`·네트워크 단계를 말하는데 앱 스킬·가이드가 금지할 뿐 기술적 차단은 아니다.
- `csv.run_analysis`는 describe|glm|ancova|glzm|logistic만 실행한다. 나머지 앱 분석은 사용자가 시작해야 한다.
- 스킬 선택은 앱 전체 설정이며 작업 공간 파일의 '더 엄격한 쪽 우선' 대상이 아니다. omp는 스킬 설명의 첫 문장(약 100자)만 프롬프트에 싣는다.
- '요약만' 정책에서도 Python 출력은 모델에 갈 수 있다. 출력 정책은 지시이며 하드 보장이 아니다.
- 앱 정보 창에는 라이선스 목록이 없다(고지는 `NOTICE.txt`와 풀린 스킬 폴더에 있음).
- 검증: `SkillPackTests` 39개, 전체 1837 통과. 실제 omp 18.4.4 + 합성 자료로 스킬 발견과 Bland-Altman(라벨·머리말·산출물) 확인.

## 14. 모델 선택기의 '최근 사용'

- **출처**: omp가 `<에이전트 폴더>\agent.db`(SQLite, WAL)의 `model_usage(model_key TEXT PRIMARY KEY, last_used_at INTEGER)`에 적는 "모델 마지막 사용 시각"(키 `provider/id`, 유닉스 초). RPC `get_available_models`에는 최근성 정보가 없다(확인).
- **omp 18.4.4로 확인한 것**: ① RPC `set_model`은 응답을 돌려주기 전에 이 표를 갱신한다(`--no-session`, 임시 cwd에서 전후 비교). ② 기본 모델로 프롬프트만 보낸 `omp -p`는 갱신하지 않는다. ③ omp가 `--mode rpc`로 실행 중일 때 읽기 전용 연결로 WAL이 읽히고 잠금 오류가 없다. ④ 에이전트 폴더: 기본 프로필은 `PI_CODING_AGENT_DIR`(있으면) 아니면 `~/<PI_CONFIG_DIR|.omp>/agent`, 이름 있는 프로필(`--profile`, `OMP_PROFILE`, 옛 `PI_PROFILE`)은 `~/.omp/profiles/<이름>/agent`이며 이때 `PI_CODING_AGENT_DIR`는 무시된다. 앱은 이런 인자·환경 변수를 따로 넘기지 않고 환경을 물려주므로(사용자가 'omp 추가 인자'에 `--profile`을 넣은 경우 포함) `OmpAgentDir`가 같은 규칙으로 폴더를 찾는다. `.env` 파일로 바꾼 위치는 따라가지 않는다.
- **`OmpModelUsage.Read`**(`Agent/OmpModelUsage.cs`, 순수·UI 스레드 밖): `Mode=ReadOnly`·`Pooling=false`·`PRAGMA busy_timeout=250`, 쓰기·체크포인트 없음. 결과 `Ok | NoDatabase | Busy | SchemaChanged | Error` + `Items(키, 마지막 사용 UTC)` + `Detail`. 표·두 열(선언 형식 TEXT/INT 계열)을 `PRAGMA table_info`로, 값은 `typeof()`로 확인한다: 키가 text가 아니거나 시각이 정수가 아니거나 2000~2100년(초 단위) 밖(밀리초로 바뀐 경우)이면 `SchemaChanged`. `provider/id` 모양이 아닌 행은 건너뛰되 전부 그렇다면 `SchemaChanged`. 잠금(SQLITE_BUSY/LOCKED)은 200 ms 뒤 한 번 다시 시도하고 그래도 안 되면 `Busy`. 파일 없음은 `NoDatabase`, 그 밖은 `Error`.
- **컨트롤러**: 모델 목록을 받을 때마다(`RequestModels`)와 `set_model` 성공 뒤에 UI 밖에서 읽고, 목록은 기다리지 않고 먼저 보낸다. `recent` = 현재 목록에 있는 키만, 최근 순(같으면 키 순), 8개. 카탈로그 메시지에 `recent` 배열을 싣고, 읽기 결과로 `recent`가 달라질 때만 다시 보낸다. 잠겨서 못 읽으면 직전 값을 유지하고, 그 밖의 실패는 비워 전체 목록(현재 동작)으로 되돌아간다.
- **경고**: `SchemaChanged`·`Error`일 때만 채팅 알림(warn) "omp 내부 형식이 바뀌어 최근 사용 모델을 표시할 수 없습니다. 전체 모델 목록을 표시합니다. 앱을 업데이트하세요. (이유)"(영어판 포함). `NoDatabase`는 알리지 않고, `Busy`는 연속 3번(각각 재시도 포함) 못 읽을 때만 "계속 잠겨 있다"고 알린다. **omp 버전마다 한 번**: `AppSettings.AgentModelUsageAlertVersion` ↔ `AgentHostOptions.ModelUsageAlertedVersion`(`ModelUsageAlertShown` 이벤트로 저장). 모든 비정상 결과는 rpc.log에 남는다.
- **선택기 화면**: 메뉴 맨 위 '최근 사용'(페이지 i18n `page.modelpicker.recent`) 그룹, 제공자 이름은 행 오른쪽에 작게. **최근 모델은 제공자별 전체 목록에도 그대로 남긴다** — 제공자별로 훑을 때 모델이 빠지면 오히려 헷갈리고, 목록은 이미 길어서 중복 몇 줄이 더 낫다. 검색은 두 곳 모두에 적용, 현재 모델은 어느 쪽에서든 선택·굵게 표시. 기록을 못 읽으면 그룹이 없다(전체 목록만).
- **설정 ▸ AI 에이전트**: 맨 아래 읽기 전용 한 줄 "최근 사용 모델: omp 기록 사용 중 / 읽을 수 없음 (이유) / 아직 확인하지 않음".
- **진단용 환경 변수**: `NANUMCSV_OMP_AGENT_DIR`가 있으면 앱이 최근 사용 기록만 그 폴더에서 읽는다(omp 자신은 영향 없음). 실제 DB를 건드리지 않고 형식 변경 경보를 재현하는 데 쓴다.
- **한계**: omp가 `model_usage`를 쓰는 때가 앱이 `set_model`을 보낼 때뿐이면, 모델을 바꾸지 않고 기본 모델만 쓰는 사용자는 '최근 사용'이 갱신되지 않는다. 내부 형식이라 omp 업데이트로 언제든 달라질 수 있어 위 경보와 폴백이 있다.
- **검증**: `OmpModelUsageTests`(임시 SQLite: 정상·빈 표·WAL에서 쓰는 쪽이 열려 있을 때·쓰기 없음·파일 없음·표/열/선언형식/값형식(밀리초·텍스트·실수)·`/` 없는 키·깨진 파일·잠금 Busy와 재시도 성공, 정렬·필터·8개 제한, 에이전트 폴더 규칙, 컨트롤러의 `recent`·set_model 뒤 갱신·폴백·경고 1회/버전/한국어/Busy 연속/예외) 36개, 전체 1885 통과. 실제 앱(Debug)에서 이 PC의 기록으로 '최근 사용'이 보이는 것과, 열 이름을 바꾼 복사본으로 경고·전체 목록·설정 줄이 나오는 것을 확인했다.

## 15. 채팅 입력창의 `+` — 파일·폴더 첨부

- **흐름**: 입력창 왼쪽 아래 `+`(`attach.js`)의 메뉴 "파일 추가… / 폴더 추가…"는 페이지 메시지 `{t:"attach", kind:"files"|"folder"}`를 보내고, 컨트롤러(`ChatController.Attach.cs`)가 `IChatDialogs.PickFiles/PickFolder`로 기본 대화 상자를 연다. 호스트(`Form1.AgentAttach.cs`의 `IChatAttachHost`)는 **파일마다** `AddSourcesAsync`를 불러 원본으로 올리고(`RunWorkspaceUiAsync`: 진행 표시·취소·한 번에 하나, 탐색기 패널은 새로 펼치지 않음) **탭은 열지 않는다**. 작업 공간 파일이 없어도 되고, 이미 올린 파일은 `AddSourcesAsync`가 같은 원본을 돌려주므로 멱등이며 칩만 다시 만든다. 질의 엔진을 쓸 수 없으면 대화 상자를 열기 전에 이유를 채팅 알림으로 알린다.
- **걸러내기**: 지원 형식은 CSV/TSV/TAB/TXT·엑셀(xlsx/xlsm/xls)·SAS·SPSS·SQLite(db/sqlite/sqlite3)(`ChatController.IsAttachable`). 폴더는 **바로 아래** 데이터 파일만(하위 폴더 제외), 폴더에서 10개를 넘으면 "N개 파일을 추가할까요?"로 먼저 묻는다(직접 고른 파일은 묻지 않음). 파일로 고르거나 끌어놓은 지원하지 않는 형식은 올리지 않고 "지원하지 않는 형식: 이름…" 알림에 적는다(폴더 안의 다른 파일은 알리지 않음).
- **끌어놓기**: `AllowExternalDrop=false`이면 WebView2가 드롭 대상을 거절해 부모 컨트롤(`AllowDrop`)도 드롭을 받지 못한다(실제 확인). 그래서 `AllowExternalDrop=true`로 켜고, 페이지가 파일 끌기만 받아(`dragenter/over/leave/drop`의 `Files`, 안내 띠 표시, 기본 동작은 막아 `file:` 이동 없음) `postMessageWithAdditionalObjects({t:"attachDrop"}, files)`로 넘긴다. **경로는 호스트(`AgentChatPanel`)가 `CoreWebView2File.Path`에서만 읽고**(폴더도 경로로 옴) 컨트롤러용 `attachPaths`를 직접 만든다. 페이지가 글로 보낸 `attachPaths`는 버린다.
- **칩과 전송**: 호스트는 `{t:"attached", items:[{name, path, label:"📎 orders.csv", tables:[…]}]}`로 칩을 돌려준다(DB 파일은 표 이름 전부, 다시 올린 파일은 같은 경로의 칩을 바꿔 끼움). 전송(`submit`)은 `attachments:[{name, tables}]`를 함께 보내고(경로는 받지 않음), 컨트롤러가 슬래시·`!`가 아닌 메시지 앞에 `AttachmentContext.Compose`의 한 줄 — `[Attached workspace tables: orders (orders.csv), …] Use ws.* tools to inspect them.` + 빈 줄 — 을 붙여 omp로 보낸다. **파일 내용은 없고 이름뿐**이라 데이터 정책은 그대로다(표 40개 초과분은 `+N more`).
- **말풍선·기록**: omp에는 머리말이 붙은 원문이 가고(대기열 `sent`도 원문 — 취소는 omp가 가진 글로 찾는다), 화면에는 `ChatPageMessages.User/QueuedUser/History`가 `AttachmentContext.TryParse`로 머리말을 떼어 **글 + `attachments` 칩**을 싣는다. 그래서 대화를 다시 불러오거나(`history`) 세션 목록 제목(`SessionCatalog`)·뷰 출처(`_lastUserRequest`)에도 머리말 줄이 아니라 사용자가 쓴 글만 나온다. 읽지 못하는 머리말은 글자 그대로 보인다(표 이름에 공백이 없다는 작업 공간 규칙에 기댐). 칩은 보낸 뒤 지워지고(보내는 동안 새로 단 칩은 남음), ×는 그 메시지에서만 뺀다.
- **상단 `＋`**(새 세션)는 동작 그대로, 툴팁만 "새 대화 / New chat".
- **검증**: `ChatAttachTests`(형식 걸러내기·지원하지 않는 형식 알림·폴더 비재귀·10개 초과 확인/취소·엔진 없음·실패 파일·진행 중 거절·끌어놓은 경로·머리말 내용(표 이름만, 데이터·경로 없음)·슬래시 제외·대기열 `sent`·머리말 왕복/40개 상한/읽지 못하는 머리말·기록 칩·세션 제목) 26개와 실제 `Form1` 호스트 `ChatAttachFormTests`(작업 공간 파일 없이 CSV 둘+DB, 탭 0, 멱등, DB 표 이름) 1개, 전체 1947 통과. 실제 앱(Debug, 가짜 omp)에서 `+` → 파일 3개 대화 상자(UIA)·칩·탐색기 원본 3·탭 0·폴더 비재귀·12개 확인 취소/확인·OS 끌어놓기(폴더+파일)·전송 시 RPC `prompt`의 머리말과 칩이 있는 말풍선·슬래시 명령 제외를 확인했다.

## 16. AI 환경 설정 안정화 — omp 찾기·설치, WebView2 실패 처리, 설정 도우미

처음 설치한 PC·회사 PC에서 채팅이 빈 화면이나 모호한 오류로 끝나는 일을 줄이기 위해, 실패 원인을 **분류해 말해 주고** 앱 안에서 고치게 한다. 모델에는 아무것도 보내지 않으며, 앱은 어떤 정보도 외부로 보내지 않는다(내려받기 때 GitHub에 요청하는 것만 예외).

### 16.1 omp 찾기 (`Agent/Rpc/OmpDiscovery.cs`)

- **순서**: ① 설정의 omp 경로(`AgentOmpPath`, `%VAR%` 확장) → ② PATH의 `omp.exe` → ③ PATH의 `omp.cmd`/`omp.bat`(PATHEXT 순서) → ④ `omp.ps1`은 **찾아도 쓰지 않고** "미지원 래퍼"로 표시(표준 입출력 RPC에 쓸 수 없음) → ⑤ `%LOCALAPPDATA%\omp\omp.exe`(공식 설치 스크립트와 같은 위치). 설정 경로에 파일이 없어도 다른 곳에서 찾으면 계속 쓰되 "설정 경로 없음"을 알린다.
- **PATH 새로 읽기**: 프로세스 PATH를 앞에 두고, 레지스트리 Machine → User의 `Path`(미확장 원문을 `%VAR%` 확장)에서 **새 항목만** 뒤에 덧붙인다(대소문자·끝 `\` 무시). 앱을 켠 뒤에 omp를 설치하거나 PATH를 고쳐도 [다시 시도]로 잡힌다. omp 자식 프로세스의 PATH도 같은 값으로 덮어 omp가 실행하는 도구가 최신 PATH를 보게 한다.
- **검증**: `omp --version`(10초)의 `omp/<버전>`을 읽어 **최소 18.4.4**를 확인한다. 같은 파일(경로·크기·수정 시각)을 이미 검증했으면(`AppSettings.AgentOmpVerified` 캐시; `.cmd`는 캐시하지 않음) 실행을 건너뛴다. 결과 `OmpProblemKind`: `NotFound | ConfiguredPathMissing | UnsupportedWrapper | NotRunnable | TooOld | UnknownVersion`. 결과는 확인한 위치를 모두 나열한 문단(`Describe`)과 영어 평문(`ToDiagnosticText`)을 만든다.
- **후보**: 못 쓸 때만 `OmpCandidate`를 채운다 — 다운로드 폴더·PATH의 릴리스 원본 이름(`omp-windows-x64.exe`/`omp-windows-arm64.exe`, 이 PC 아키텍처 우선)과 **실행 중인 omp 프로세스**의 실행 파일. 자동으로 쓰지 않고 사용자가 [이 파일 설치]를 고를 때만 복사한다.

### 16.2 omp 설치·내려받기 (`Agent/Rpc/OmpInstaller.cs`, `ChatController.OmpSetup.cs`)

- **채팅 오류 알림**: omp를 못 쓰면 상태 줄 한 줄 + 채팅에 확인한 위치를 담은 오류와 버튼 알림 — [찾아보기…] · [이 파일로 설치(후보마다)] · [다운로드하여 설치] · [다시 시도] · [설치 안내(GitHub)] — 과 선택 체크박스 "사용자 PATH에도 추가(기본 꺼짐)". 버튼은 앱 내부 주소 `nanumcsv://omp/{browse | install?src=… | download | retry}`로 컨트롤러가 처리한다. 같은 실패가 `SetupNeeded` 이벤트로 설정 도우미도 연다(§16.4).
- **파일에서 설치**(`InstallFromFileAsync`): 후보를 `%LOCALAPPDATA%\omp\omp.install-<guid>.exe`로 **스트림 복사**(NTFS 대체 스트림 `Zone.Identifier`는 따라오지 않음, 실행 중인 원본도 읽음)하고 차단 표시를 지운 뒤 `--version`(≥ 18.4.4)으로 검증한 다음에만 `omp.exe`를 바꾼다. 이미 설치 위치에 있는 파일이면 복사 없이 차단 표시만 풀고 검증한다.
- **내려받기**(`DownloadAndInstallAsync`, 사용자 확인 후): ① GitHub `repos/can1357/oh-my-pi/releases/latest`(초안·시험판은 거부) 응답에서 `omp-windows-<x64|arm64>.exe`를 고른다(이 PC 아키텍처, 그 밖은 `NoAsset`). ② 에셋 URL이 `github.com`(또는 하위 도메인)의 HTTPS가 아니면 거부. ③ **SHA-256**은 에셋의 `digest`(`sha256:<hex>`)와 `SHA256SUMS.txt`에서 얻고, 둘 다 있는데 다르면 `DigestMismatch`로 거부, **둘 다 없으면 `NoDigest`로 설치하지 않는다**(직접 받아 '파일로 설치'를 안내). ④ 디스크 여유(필요 크기 + 64 MB)를 확인하고 임시 파일로 받으며 해시를 같은 패스에서 계산한다(한 바이트도 60초 동안 못 받으면 중단, 길이가 모자라면 오류). ⑤ 해시가 다르면 임시 파일을 버린다. ⑥ `--version` 검증(≥ 18.4.4). ⑦ 대상이 없으면 이동, 있으면 **원자적 교체**(실패하면 기존 설치 그대로; 대상이 실행 중이면 `InUse`로 닫고 다시 시도하라고 안내). 시스템(IE) 프록시와 현재 Windows 계정 자격 증명을 쓰며(연결 20초·API 30초 제한), 취소할 수 있다.
- **설정은 건드리지 않는다**: `AgentOmpPath`를 자동으로 바꾸지 않는다(호스트 `OnAiSetupOmpInstalled`가, 설정에 적힌 경로의 파일이 없을 때만 설치 경로로 바꾼다). [찾아보기…]로 고른 파일은 검증 뒤 설정에 저장한다.
- **사용자 PATH 추가**(선택): `HKCU\Environment\Path`에 설치 폴더를 덧붙인다(이미 있으면 대소문자·끝 `\`·`%VAR%` 확장을 무시하고 그대로 둠, 값 종류 REG_EXPAND_SZ 보존), 그리고 `WM_SETTINGCHANGE("Environment")`를 방송한다(응답 없는 창은 기다리지 않음). 실패해도 설치는 성공으로 두고 이유를 알린다.
- **서명**: omp 릴리스(MIT)는 **Authenticode 서명이 없다**. SHA-256 대조는 "전송 중 변조·손상이 없음"을 보장할 뿐 게시자 신원을 증명하지 않는다. 앱은 SmartScreen·보안 프로그램의 경고를 우회하지 않는다.

### 16.3 WebView2 실패 처리 (`Agent/WebView/`, `AgentChatPanel.cs`)

- **분류**(`WebViewProblem`): `RuntimeMissing`(런타임 없음; 이때만 설치 링크) / `CompatLayer`(`0x8007139F` + DPI 호환성 레이어) / `StateMismatch`(`0x8007139F`인데 레이어 없음 — 같은 데이터 폴더를 다른 인자로 쓰는 프로세스 등) / `AccessDenied`(`0x80070005`, 사용자 데이터 폴더 `%LOCALAPPDATA%\NanumCsvViewer\WebView2` 권한) / `BinaryOrArch`(`0x8007007E`/`0x800700C1`, 런타임 손상) / `Other`. `WebViewFailureText.Build`가 원인별 한/영 안내문을 만든다(런타임이 있으면 "다시 설치"를 권하지 않음).
- **호환성 레이어**(`CompatLayers`): `HKCU`·`HKLM`(64/32비트 뷰) `…\AppCompatFlags\Layers`에서 `msedgewebview2.exe`·`NanumCsvViewer.exe` 값을 읽고, DPI 토큰(`HIGHDPIAWARE`·`DPIUNAWARE`·`GDIDPISCALING`·`PERPROCESSSYSTEMDPIFORCEON/OFF`)을 가진 것을 표시한다. **HKCU만** [설정 해제]로 지울 수 있다: 먼저 HKCU Layers 전체를 regedit 형식 `%LOCALAPPDATA%\NanumCsvViewer\backup\appcompat-<yyyyMMdd-HHmmss>.reg`(UTF-16 LE, 더블클릭 병합으로 복원)로 저장하고(저장 실패 시 아무것도 바꾸지 않음), 값에 DPI 토큰만 있으면 삭제, 다른 호환성 토큰(예: `WINXPSP3`)이 있으면 DPI 토큰만 빼고 다시 쓴다. HKLM 항목은 관리자 권한이 필요해 안내만 한다.
- **다시 시도**: 컨트롤을 새로 만들어 초기화를 다시 한다(실패한 `CoreWebView2` 상태를 재사용하지 않음).
- **DPI 모드**: `ApplicationHighDpiMode`를 **PerMonitorV2**로(`NanumCsvViewer.csproj`; `WebViewInitTests`가 확인). System aware일 때 `msedgewebview2.exe`에 `HIGHDPIAWARE` 레이어가 걸린 PC에서 컨트롤러 생성이 `0x8007139F`로 실패했고, PerMonitorV2에서는 같은 레이어가 있어도 채팅이 뜬다(실제 앱에서 확인). `DPIUNAWARE`·`GDIDPISCALING` 레이어는 PerMonitorV2에서도 초기화를 막아 위 [설정 해제]가 필요하다.
- **로그**: `%LOCALAPPDATA%\NanumCsvViewer\agent\webview2-init.log` — 초기화가 **실패할 때마다** 시각·앱 버전·WebView2 상태 보고(런타임 버전·DPI 모드·배율·감지한 레이어·분류)·예외를 덧붙인다(성공은 기록하지 않음). 지원용 환경 변수 **`NANUMCSV_WEBVIEW2_LOG=1`**을 주고 시작하면 브라우저 로그(`--enable-logging`)를 `agent\webview2-browser.log`에 남기며 앱의 모든 WebView2에 적용된다(프로세스 시작 때 한 번만 읽음).

### 16.4 AI 환경 설정 도우미 (`Agent/Setup/`, `Ui/Form1.AgentSetup.cs`)

- **진입점**: 도구 ▸ **AI 환경 설정 도우미…**, 설정 ▸ AI 에이전트의 같은 이름 단추(모달), 그리고 **자동**: WebView2 초기화 실패(`WebViewInitFailed`)·omp 못 찾음·채팅 시작 실패 때 **앱 버전마다 한 번**(`AppSettings.AiSetupShownVersion`에 현재 버전 기록) 열린다. 도우미의 '다시 표시하지 않기'(`AiSetupDisabled`)를 켜면 자동으로는 열지 않는다(메뉴·설정은 무관). 보이지 않는 창에서는 열지 않는다.
- **네 단계**(`AiSetupRunner`, 상태 `Pending/Checking/Ok/Warn/Fail/Blocked`): ① **WebView2** — 런타임 버전·앱 DPI 모드·시스템 배율·호환성 레이어·마지막 초기화 결과 → [런타임 설치 페이지 열기] · [DPI 호환성 설정 해제] · [다시 시도] · [로그 폴더 열기]. ② **omp** — §16.1 결과 → [찾아보기…] · [이 파일 설치(복사)] · [omp 내려받기](너무 오래된 경우 "최신 omp 내려받기") + '사용자 PATH에도 추가' 체크. ③ **로그인**(omp가 됐을 때만) ④ **연결 시험**: `omp --mode rpc --no-session`을 짧게 띄워 핸드셰이크·`get_login_providers`·`get_available_models`만 확인한다 — **프롬프트를 보내지 않으므로 모델 호출 비용이 없다**. 모델이 1개 이상이면 로그인 단계가 정상(로그인한 제공자 또는 API 키·환경 변수 출처를 표시), 0개면 실패로 제공자 목록을 보여 준다.
- **로그인**(`OmpRpcAccountProbe.LoginAsync`): 제공자 옆 [로그인]/[다시 로그인] → RPC `login`(제한 시간 5분, 취소 가능). omp가 `open_url` 확장 UI 요청으로 알려 준 **OAuth 인증 주소를 기본 브라우저로 열고**(주소 복사 단추도 있음), 코드 붙여 넣기 같은 짧은 입력은 도우미의 입력란으로 받는다. **API 키처럼 비밀 값**을 요구하는 제공자는 omp RPC가 거절하므로(오류 문구가 secret/terminal/API key를 말함) 도우미는 [이 제공자로 터미널에서 로그인](`omp login`을 새 터미널에서 실행)과 제공자 환경 변수 안내로 돌린다. 로그인 성공 뒤에는 `CredentialsChanged`로 에이전트를 다시 시작하고 단계를 다시 검사한다(같은 프로세스는 새 자격 증명을 모를 수 있어 다음 검사 때 프로세스를 새로 띄움).
- **빈 모델 목록 알림**: 채팅의 "사용할 수 있는 AI 모델이 없습니다…" 알림에 **[로그인…]** 단추(`nanumcsv://setup/login`)가 붙고, 누르면 도우미의 로그인 단계가 열린다(자동 규칙과 무관).
- **고친 뒤**: omp 경로·설치·로그인이 바뀌면 설정을 저장하고 에이전트를 다시 시작하며, WebView2를 고치면 채팅 화면을 다시 시도한다(`Form1.AgentSetup.cs`).

### 16.5 진단 정보 복사 (`AiDiagnostics.cs`)

- **도움말 ▸ AI 환경 진단 정보 복사**(도우미에도 같은 단추): 앱 버전, OS·아키텍처(OS/프로세스)·.NET·UI 문화권·창 DPI, `-- WebView2 --`(감지 결과), `-- omp --`(§16.1 `ToDiagnosticText`), `-- Python analysis environment --`, `-- Agent settings --`, `webview2-init.log` 끝 60줄, `rpc.log`의 최근 400줄에서 추린 60줄 요약(프레임 종류만, 메시지 내용 없음)을 영어 평문으로 모아 클립보드에 넣고 파일 저장 여부를 묻는다.
- **가리기**(`DiagnosticRedactor`, 패턴 기반): `sk-…`·`Bearer …`·JWT·GitHub 토큰·Google API 키·Slack 토큰·AWS 키, 이름에 `api key/token/secret/password/authorization/credential/verifier/auth code`가 들어간 `이름: 값`·`이름=값`(복수형 `tokens`는 제외), 인증 URL의 `code·state·token·key·…` 쿼리 값, `--api-key` 인자, 영문·숫자를 모두 가진 40자 이상 불투명 문자열 → `<redacted>`; 사용자 폴더는 `%USERPROFILE%`, `C:\Users\<이름>`과 사용자 이름은 `<user>`로 바꾼다. **모르는 형식의 비밀은 못 가릴 수 있으므로** 복사 후 붙여 넣기 전에 훑어보도록 안내한다. 셀 값·질문·답변 같은 데이터 내용은 애초에 수집하지 않는다.

### 16.6 한계와 검증

- **한계**: 최신 정식 릴리스만 내려받고(버전 선택·자동 업데이트 없음) SHA-256이 없으면 설치하지 않는다. 코드 서명이 없어 SmartScreen이 경고할 수 있다. OAuth 제공자만 도우미 안에서 로그인한다. HKLM 호환성 설정은 안내만 한다. 도우미는 omp RPC(`get_login_providers`·`get_available_models`·`login`·확장 UI 요청)에 기대므로 omp 쪽이 바뀌면 해당 단계가 실패로 표시될 수 있다. 가리기는 패턴 기반이다.
- **검증**: 전체 2090 통과(`OmpDiscoveryTests`·`OmpInstallerTests`·`OmpSetupChatTests`·`CompatLayersTests`·`WebViewInitTests`·`AiSetupWizardTests` 등, 가짜 레지스트리·가짜 HTTP·임시 폴더·가짜 omp). 실제 앱(Debug, 100% 배율)에서 HKCU `msedgewebview2.exe=HIGHDPIAWARE` 레이어가 있을 때 System aware는 `0x8007139F`로 실패하고 PerMonitorV2는 채팅이 뜨는 것을 확인했고(레이어는 확인 뒤 되돌림), 도우미의 네 단계가 이 PC에서 모두 정상으로 나오는 것을 확인했다. **확인하지 못한 것**: 150% 이상 배율·다중 모니터, 실제 앱에서 omp를 못 찾는 상태(가짜 "없음")의 도우미·채팅 오류 화면, 실제 GitHub 서버에서의 내려받기, 실제 OAuth 로그인 완료.
