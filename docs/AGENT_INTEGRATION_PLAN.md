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
- **omp 위치**: PATH의 `omp.exe` → `%LOCALAPPDATA%\omp\omp.exe` → 설정의 경로. 최소 버전 18.4.4. 없으면 패널에 설치 안내.
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
- omp 자체 도구 승인(`Allow tool: …`)도 같은 카드로 표시. host.yml은 `approvalMode: always-ask`(omp 기본 yolo는 bash·write를
  묻지 않으므로). `--approval-mode yolo`를 추가 인자로 주면 바뀐다.

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
