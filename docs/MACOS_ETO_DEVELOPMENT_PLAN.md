# 공통 C# 엔진 + macOS Eto.Forms 개발 계획

> 상태: **Windows에서 공통 엔진과 Eto CSV 뷰어까지 실행 확인. macOS 백엔드·Apple 서명·공증은 이 환경에서 만들 수 없다.**
> 목적: 기존 Windows 제품을 유지하면서 C# 분석 엔진을 공유하고 macOS 앱을 개발한다.

## 1. 결정 사항과 범위

| 항목 | 결정 |
|---|---|
| Windows | 현재 `NanumCsvViewer` WinForms UI·사용자 파일·배포 경로 유지 |
| 공통 엔진 | UI 없는 `net10.0` C# 라이브러리로 추출. 알고리즘을 복제하지 않음 |
| macOS UI | Eto.Forms 공통 UI API + 현대 .NET macOS/AppKit 백엔드 |
| 첫 의사결정 관문 | **실제 CSV를 여는 macOS 대용량 그리드**로 성능·선택·편집 검증 |
| 그리드 대안 | 기본 Eto 그리드가 기준을 못 맞추면 해당 컨트롤만 C# AppKit 구현 |
| 웹뷰 | Windows WebView2 유지. macOS는 WKWebView 어댑터로 기존 채팅 계약 연결 |
| 차트 | 데이터 모델·계산 공유, Windows GDI+ 유지, macOS Eto.Drawing 렌더러 작성 |
| 플랫폼 | Apple Silicon 먼저 검증하고 Intel도 독립 검증. 정식 지원은 각 아키텍처의 통과 증거가 있어야 표시 |
| 정식 배포 | CSV 탐색뿐 아니라 아래 기능 대응표 전체와 서명·공증 검증 완료 후 |
| Windows Eto 통합 | 이번 개발 범위 밖. macOS 결과와 유지 비용을 확인한 뒤 별도 결정 |

전체 Swift 재작성, WinForms/WPF 범용 translator, Windows UI 전면 교체, 분석 알고리즘 교체, 새 자동 업데이트 시스템은 하지 않는다. 초기 CSV 화면은 **기술 검증 산출물**이지 기능을 줄인 정식 제품이 아니다. 성능 검증 실패를 페이지 탐색만 제공하는 방식이나 전체 행 사전 적재로 숨기지 않는다.

기존 [PORTING_PLAN.md](PORTING_PLAN.md)는 과거 Swift macOS 앱의 기능을 Windows로 옮긴 기록이다. 이번 계획은 반대 방향의 **공통 C# 코드 기반 구성**이며, 그 문서의 옛 단일 문서·탭 제외 결정을 현재 제품에 적용하지 않는다.

## 2. 현재 구조와 실제 분리 지점

현재 `NanumCsvViewer/NanumCsvViewer.csproj`는 `net10.0-windows`, `UseWindowsForms=true`, 기본 x64이다. 엔진과 UI가 같은 어셈블리에 있으며, `NanumCsvViewer.Alglib`만 별도 `net10.0` 라이브러리다. 폴더 이름만 보고 그대로 이식 가능하다고 판단하지 않는다.

아래 소스 경로는 저장소 루트 기준이다.

| 현재 경로·기호 | 공유 대상 | 먼저 해소할 결합 |
|---|---|---|
| `Csv/VirtualCsvDocument.cs`, `RecordIndex`, `ByteSources`, `RowCache` | 바이트 인덱싱·지연 행 읽기·캐시·편집 덮개 | 수명 관리, 경로 동일성, 플랫폼별 파일 공유/교체 동작 |
| `Csv/AnalysisSnapshot.cs`, `AnalysisMemoryBudget.cs` | 분석 입력 수집·예상 예산 계산 | `kernel32!GlobalMemoryStatusEx`와 정책 계산 분리 |
| `Csv/ConditionalFormat*.cs` | 규칙·색상·우선순위 | `System.Drawing.Primitives` 색상과 UI 렌더러 변환 경계 |
| `Csv/DataQuality/`, `Stats/`, `BasicAnalyses.cs` | 품질·통계·모형·예측·ONNX | 언어/버전 헬퍼, ALGLIB, 차트 데이터와 UI의 결합 |
| `AnalysisWork.cs`, `Charting/PlotModel.cs` | UI 없는 작업 입력·결과 및 그림 데이터 | `AnalysisWork`가 사용하는 `ChartKind`는 현재 `ChartForm.cs`에 정의됨 |
| `Stats/ReportPdf.cs` | PDF 생성·글꼴 서브셋 | Windows 글꼴 폴더 탐색. `Locate(IEnumerable<string>)` 이음매 재사용 |
| `Workspace/DataWorkspace*.cs` | DuckDB·SQL·뷰 계산 | `ViewerSupport.LT`, 네이티브 라이브러리, 임시 경로 |
| `Workspace/WorkspaceFile.cs` | `.ncvws` 읽기·저장 | Agent 정책 타입/파서 참조, Windows 절대 경로·대소문자 가정 |
| `Import/` | Excel·SAS·SPSS·SQLite → 임시 CSV | 패키지·네이티브 자산·한글/형식 픽스처 실검증 |
| `Agent/AgentContracts.cs`, `Rpc/`, `Chat/` | RPC·도구·승인·대화 상태 | 웹뷰/대화 상자/UI 디스패치와 정책·프로세스 로직 분리 |
| `Agent/AgentChatPanel.cs`, `MarkdownViewerForm.cs`, `ImageViewerForm.cs` | 자산·메시지·뷰어 기능 계약 | WebView2 전용 호스트 매핑·파일 드롭·네이티브 메시지 |
| `Agent/Python/`, `Agent/Rpc/OmpInstaller.cs` | 분석 환경·omp 연결 기능 | `Scripts/*.exe`, Windows wheel 제한, Windows 릴리스 이름·Registry |
| `Loc.cs`, `ViewerSupport.cs`, `AppInfo.cs` | 기존 번역 방식·제품 버전 전달 | `ViewerSupport`의 WinForms Timer/셸 실행과 순수 번역 분리; 엔진이 UI 어셈블리 버전을 읽지 않게 함 |

`Csv/`부터 `ImageViewerForm.cs`까지의 상대 소스 경로 앞에는 `NanumCsvViewer/`가 붙는다. `System.Drawing.Color` 및 `Microsoft.Win32.SafeHandles.SafeFileHandle`은 이름만으로 Windows 전용으로 분류하지 않는다. 반면 `Graphics`·`Bitmap`·`Font`, WinForms 컨트롤, WebView2와 Win32 호출은 공통 엔진에 남기지 않는다.

DuckDB.NET.Data.Full **1.5.6**의 복원 자산에는 `runtimes/osx/native/libduckdb.dylib`가 있고 SQLite 패키지에도 macOS 자산이 있다. **자산 존재는 arm64/x64 실행 성공의 증거가 아니다.** ALGLIB는 관리형 코드지만 ARM64 수치·SIMD 경로와 추가 플랫폼 배포 권한을 따로 확인한다.

## 3. 목표 프로젝트와 의존 방향

프로젝트 이름은 구현 시 사용할 계획명이다. 현재 존재하는 프로젝트와 혼동하지 않는다.

```text
NanumCsvViewer                         기존 Windows WinForms 실행 앱
 ├─ NanumCsvViewer.Core                CSV·품질·통계·그림 데이터·Import·Workspace
 │   └─ NanumCsvViewer.Alglib          기존 관리형 라이브러리 유지
 └─ NanumCsvViewer.Agent.Core          RPC·채팅 상태·도구·환경 관리의 공통 로직
     └─ NanumCsvViewer.Core

NanumCsvViewer.Mac                     macOS 실행·AppKit/WKWebView·OS 서비스
 ├─ NanumCsvViewer.UI.Eto              메뉴·창·설정·분석 화면·그리드/차트 UI
 │   ├─ NanumCsvViewer.Core
 │   └─ NanumCsvViewer.Agent.Core
 └─ Eto.Platform.macOS
```

- Core·Agent.Core: `net10.0`, AnyCPU. WinForms/WebView2/Eto/AppKit 참조 없음.
- UI.Eto: `net10.0` + Eto.Forms. Windows UI를 이 프로젝트에 옮기지 않는다.
- Mac: 우선 **Eto.Forms 2.12.0 / Eto.Platform.macOS 2.12.0**를 함께 고정하고 `net10.0-macos26.0` 백엔드로 검증한다. 구형 MonoMac/Mac64 백엔드와 섞지 않는다.
- NuGet의 macOS 2.12.0 자산은 `net9.0-macos15.0`, `net10.0-macos26.0`을 제공한다. **API 대상 TFM과 실제 최소 실행 OS는 다르다.** P0에서 SDK·macOS workload·Xcode 조합과 `SupportedOSPlatformVersion`을 확정하고 해당 최소 OS에서 실행한다.
- UI.Eto가 네이티브 컨트롤을 요구하는 경우 Mac에서 Eto handler/팩터리를 주입한다. 공통 엔진에 조건부 UI 코드를 넣지 않는다.
- 작은 실제 경계만 만든다: 메모리 측정, 저장/캐시 경로·경로 동일성, 글꼴 탐색, UI 디스패치, 웹뷰·셸·프로세스 실행. 범용 플랫폼 프레임워크나 서비스 컨테이너는 만들지 않는다.

현재 내부 타입·네임스페이스는 가능한 한 유지하고, 어셈블리 이동에 필요한 접근성만 조정한다. 공개 API 변경 전 참조를 조회한다. 각 이동은 모든 Windows 호출자·테스트·리소스 이름을 같은 변경에서 바꾸고 원래 구현을 제거한다. 소스 복사, 호환 별칭, 같은 알고리즘의 두 구현은 남기지 않는다.

`WorkspaceFile`이 요구하는 데이터/승인 정책 값과 순수 정규화는 Core에 둔다. omp 실행 인수 해석·RPC는 Agent.Core에 두어 Core → Agent.Core 역참조를 없앤다. 현재 `Loc`·resx 번역 관례는 재사용하고 별도 번역 체계를 만들지 않는다. Windows 전용 도움말/진단 문구는 각 호스트에서 제공한다. ONNX 등의 제품 버전은 기존 `ModelStore`처럼 호출자가 전달한다.

## 4. 공통 엔진 계약: 바꾸면 안 되는 것

### 4.1 행·뷰·편집 좌표

`VirtualCsvDocument`의 다음 좌표를 구분한다.

| 좌표 | 현재 API | 의미 |
|---|---|---|
| 표시 행 | `DisplayRowCount`, `GetDisplayRow`, `GetRowId` | 현재 필터·정렬 결과의 위치 |
| 데이터 위치 | `GetDataRow`, `GetDataRowUncached` | 현재 문서의 필터와 무관한 행 위치 |
| 행 ID | `GetRowById`, `CellEdits` | 편집·삽입·삭제의 식별자 |

그리드 인덱스를 행 ID처럼 저장하지 않는다. 정렬/필터 후 복사·편집·되돌리기가 다른 행에 적용되지 않아야 한다. 현재 `int.MaxValue` 행 표시 상한과 `RowCountTruncated` 안내, 구조 편집 제한을 유지한다. 이번 이식에 64비트 행 수 확장을 섞지 않는다.

`SnapshotViewRows()`는 행 문자열을 미리 만들지 않는 **문서 수명에 종속된 지연 입력**이다. `AnalysisSnapshot.Collect()`는 예산 확인 후 만드는 **독립된 완전한 입력**이다. 모든 분석을 전체 문자열 목록으로 바꾸거나, 초과 행을 잘라 성공 결과로 발표하지 않는다. 투영 컬럼·원본 행번호·실제 처리/표본 범위도 보존한다.

### 4.2 비동기 작업과 종료

- 인덱싱·검색·필터·정렬·품질·통계·가져오기·내보내기는 백그라운드 실행. 컨트롤 변경은 UI 스레드에서만 한다.
- 문서/뷰 세대와 요청 식별자를 결과에 연결한다. 이전 요청의 완료·오류가 새 문서/뷰를 덮어쓰지 않게 한다.
- 문서 교체·탭 닫기·앱 종료: 새 작업 차단 → 취소 신호 → **읽기 작업 드레인** → 문서/파일 핸들 해제. UI 스레드의 동기 `Wait()`로 드레인하지 않는다.
- `CellEdits.Changed`는 호출 스레드에서 발생한다. 이벤트 자체를 UI 스레드 보장으로 간주하지 않는다.
- 중복 실행 방지·최신 요청 병합·취소 후 이전 정상 결과 보존 등 기존 화면별 동작을 유지한다.

### 4.3 저장 형식과 경로

`.ncvws`, 편집 저널, 인덱스, 저장된 뷰, 모델 JSON/ONNX의 형식을 불필요하게 바꾸지 않는다. Windows 설정/캐시 위치와 기존 키는 유지한다. Mac 데이터는 `~/Library/Application Support/NanumCsvViewer`, 재생성 가능한 캐시는 `~/Library/Caches/NanumCsvViewer` 아래에 구분한다. 앱 번들은 읽기 전용이다.

현재 경로의 `ToLowerInvariant()` 해시와 `OrdinalIgnoreCase` 비교를 macOS에 그대로 적용하지 않는다. 대소문자 구분 APFS에서도 다른 파일을 합치지 않는 플랫폼 경로 정책을 사용하고, 같은 파일에 덮어쓰기·심볼릭 링크·한글 조합형/분해형 파일명도 검증한다. 경로 정규화가 실제 다른 파일을 합치지 않아야 한다.

작업 공간 내 상대 경로는 두 OS에서 그대로 복원한다. `C:\...` 같은 타 OS 절대 경로는 임의 변환하지 않고 **원본 재지정**을 요청한다. 재지정·저장 후 SQL·뷰·참조 원본 관계가 유지되어야 한다. 세션 경로·실행 파일 검증 캐시는 플랫폼 간 그대로 신뢰하지 않는다. 창 좌표는 OS별 설정으로 두고 Retina point와 픽셀을 혼동하지 않는다.

## 5. 가장 먼저 검증할 대용량 그리드

### 5.1 Eto 2.12.0 소스에서 확인한 사실

- `GridView.DataStore`는 `IEnumerable<object>`이며 전용 `IIndexedStore` API가 아니다.
- macOS `GridViewHandler`는 `NSTableView`의 행 요청에서 `collection.ElementAt(row)`를 호출한다.
- `CollectionChangedHandler`는 `IList<T>`/비제네릭 `IList` 인덱서, `ICollection`의 Count를 활용한다. 그렇지 않으면 `Lookup`이 **`Collection.ToList()`로 전체 구체화**한다.
- macOS collection handler의 `AddRange`는 native `ReloadData()` 경로를 사용한다. 이것만으로 앱 전체의 지연 접근이 보장되지는 않는다.
- `GridView.SelectionPreserver`는 선택 항목 재탐색을 위해 전체 DataStore를 순회한다. 갱신 때 이 도우미를 쓰지 않는다.
- macOS 그리드의 행 높이 계약은 단일 `RowHeight`이다. 기존 다중 줄/가변 높이 표시를 기본 컨트롤이 그대로 제공한다고 가정하지 않는다.

따라서 **indexed data source가 가능하다는 소스 근거는 있지만, 이 앱의 대용량·셀 선택·편집 요구를 만족한다는 실행 증거는 아직 없다.** 근거 링크는 13절에 고정 버전으로 남긴다.

### 5.2 첫 구현 경로

1. Core에서 CSV를 열고 인덱싱하며 확인된 행을 즉시 표시한다. 코드페이지 공급자를 등록해 UTF-8/BOM·CP949/EUC-KR 계약을 유지한다.
2. O(1) Count·인덱서가 있는 read-only `IList` 계열 어댑터를 연결한다. 실제 수정은 `CellEdits` 명령으로 처리한다. 전체 행 객체 배열/ObservableCollection은 만들지 않는다.
3. 행 래퍼·문자열 캐시는 접근된 영역만 제한된 캐시에 보관한다. 새 UI가 Core 캐시 옆에 원본 전체를 중복 저장하지 않게 한다.
4. 초기 열 폭은 고정/제한 표본 측정. 전체 셀 자동 크기·전체 행 높이 측정을 하지 않는다. 캐시 미스의 디스크 읽기로 UI가 멈추면 제한된 비동기 프리페치와 완료 시 해당 셀 갱신을 적용하고 문서 교체 시 취소한다.
5. 셀/범위 선택, 원본 행 ID, 뷰 버전을 별도 상태로 관리한다. 전체 선택은 가능한 한 범위로 표현하고 복사/내보내기는 원본 값으로 처리한다. 선택 복구를 위해 전체 CSV를 다시 파싱하지 않는다.
6. 다중 셀 선택·TSV 복사/붙여넣기·되돌리기·고정 열·헤더/셀 메뉴·줄바꿈/긴 셀·키보드 이동을 실제 화면에서 확인한다. 미리보기만 제한하고 상세/복사는 전체 값을 보존한다.

P2는 원본 연속 스크롤과 행 이동을 유지해야 한다. 단순 페이지 테이블로 바꿔 이 관문을 통과했다고 판정하지 않는다.

### 5.3 통과/전환 판단

- 기본 GridView가 기능과 성능을 모두 통과: 기본 Eto + 어댑터 채택.
- 데이터 접근은 통과하지만 셀 선택·가변 행 높이·고정 열 등 핵심 계약이 불가: Mac 전용 Eto handler/네이티브 그리드로 전환. 같은 Core 행 공급자를 사용한다.
- 대안도 실패: 후속 대화 상자 대량 이식을 시작하지 않는다. 계측 결과·실패 기준·대안 비용을 제시하고 UI 전략을 다시 결정한다. Windows 제품은 유지한다.

Eto 내부 동작에 기대는 부분은 사용하는 버전을 고정하고, 업그레이드 때 그리드 관문을 다시 실행한다.

## 6. macOS 플랫폼 서비스

| 영역 | 구현 방향 | 완료 증거 |
|---|---|---|
| RAM | 총 물리 메모리/가용량 측정과 예산 계산 분리. macOS native 측정 사용 | 총량·가용량·산정식·측정 실패를 확인; OS 도구와 부하 상황 대조 |
| 경로·파일 | 4.3절 정책, 원본 접근/임시 파일 수명 관리 | 대소문자 구분 볼륨·읽기 전용·권한 거절·외부 파일 변경·저장 중 취소 |
| 테마·배율 | Eto/NSAppearance, OS별 창 배치·Retina 대응 | light/dark 전환, 한글 IME, Retina/외부 모니터, 잘림/좌표 오류 확인 |
| 글꼴·PDF | 기존 디렉터리 주입 이음매로 Mac 글꼴 탐색. 필요 시 재배포/임베딩 허용 글꼴 번들 | 한글 글리프·PDF 텍스트·표·페이지 나눔 확인. 글꼴 부재를 성공으로 숨기지 않음 |
| 셸·클립보드 | Eto/AppKit 파일 대화 상자·드롭·브라우저/폴더 열기 | Cmd 기반 단축키, 시스템 메뉴, 취소·파일 권한, TSV 줄바꿈 |
| 원시 라이브러리 | DuckDB/SQLite를 해당 아키텍처로 패키징·로드 | 두 아키텍처에서 SQL·import·앱 설치본 실행, native 로더 오류 없음 |

메모리 정책은 v3.4.3의 세 선택을 보존한다: 자동(총 RAM 50%, 최소 512 MiB), 직접 지정, 여유 RAM 확보(가용량 − 남길 양, 분석 예산 하한 없음). 가용량을 읽지 못하거나 남는 양이 없으면 분석을 중단한다. 현재 Win32 호출 실패 시 가용량이 0이므로 이를 Mac의 정상 구현으로 취급하지 않는다.

macOS는 압축 메모리·회수 가능 캐시가 있어 Windows의 Available 값과 단순 동치가 아니다. **P0에서 공급자의 가용량 산정식·단위를 문서화하고, 압박 상태에서 과대평가하지 않는 기준으로 검증한다.** GC 총량을 현재 가용 물리 RAM처럼 표시하거나 스왑을 여유 RAM에 포함하지 않는다. 기존 정책은 예상 예산이지 예약/프로세스 하드 제한이 아니며, 동시 분석의 합산 예약 기능은 이번 이식에 새로 추가하지 않는다. DuckDB 상한은 별개로 유지한다.

## 7. AI 채팅·omp·Python 이식

### 7.1 기존 계약 재사용

`AgentContracts.cs`의 `IChatPage.Post/Received`, `IAgentApprovals` 및 현재 도구 계약을 먼저 재사용한다. RPC/승인/대화 상태를 플랫폼별로 다시 구현하지 않는다. Windows의 `AgentChatPanel`은 유지하고 Mac에는 WKWebView 호스트를 둔다.

현재 `ChatAssets/chat.js`, `attach.js`, `viewer.js`는 `window.chrome.webview`를 사용한다. 공통 JS bridge와 WebView2/WKWebView transport로 정리하여 메시지 내용·ready handshake·큐·테마·언어·스트리밍 계약은 공유한다. Windows 호출자도 같은 변경에서 이전 경로를 교체한다. Eto WebView를 넣으면 기존 bridge가 자동으로 작동한다고 가정하지 않는다.

필수 경계:

- 로컬 자산/출력 이미지/Markdown/SVG·WebP 뷰어 접근을 제한된 URI 매핑/요청 처리로 구현한다. WebView2의 virtual-host API를 Mac에 그대로 호출할 수 없다.
- 웹뷰에는 승인된 로컬 문서만 표시한다. 외부 링크는 허용한 HTTP(S) URL을 시스템 브라우저로 열고 임의 탐색·새 창을 차단한다.
- 이미지/문서 경로는 실제 허용 루트 내부로 제한한다. `..`, URL 인코딩, 심볼릭 링크를 통한 루트 탈출과 페이지가 보낸 가짜 첨부 경로를 거절한다.
- WebView2의 `postMessageWithAdditionalObjects(File[])`와 파일 경로 전달은 Mac에 없다. **네이티브 파일 드롭/파일 선택**에서 검증한 경로만 전달한다.
- 앱 단축키와 웹 입력/IME 충돌, ready 이전 메시지, 페이지 재로드, 작업 공간 전환, 닫힌 페이지의 늦은 응답을 검증한다. 세 웹뷰 소비자 모두 이식 대상이다.

데이터 정책·작업 공간의 더 엄격한 제한·행 공유 승인·편집/파일 저장 승인·취소 시 거절을 현재 의미대로 유지한다. UI가 달라져도 원시 행을 추가 전송하지 않는다. Python 결과의 **앱 검증 범위 밖** 표시와 요약 정책의 기존 한계 경고도 유지한다.

### 7.2 omp 프로세스와 설치

현재 설치기는 `omp-windows-{x64|arm64}.exe`, Registry PATH, `WM_SETTINGCHANGE`, Zone.Identifier 처리에 의존한다. Mac에서는 다음을 별도 구현한다.

- 정식 릴리스의 **실제** macOS 자산 이름·아키텍처·공개 SHA-256·최소 RPC 버전을 P0에서 확인한다. 자산 이름을 추측하거나 검증을 생략하지 않는다.
- 실행 권한·설치 경로·원자 교체·실행 중 교체 거절·앱에서의 발견·사용자 지정 파일 설치를 지원한다. Finder에서 실행한 앱은 터미널 PATH와 환경이 같다고 가정하지 않는다.
- stdio RPC·로그인·세션 이어받기·실제 도구 호출을 앱 번들에서 검증한다. `~/.omp`/프로필/환경 변수의 기존 우선순위를 보존한다.
- quarantine/Gatekeeper 문제를 구분해 안내한다. unsigned 외부 omp 때문에 막히는 것을 앱이 보안 설정 해제로 우회하지 않는다.

공식 자산·digest가 없으면 자동 내려받기는 정확한 이유로 실패하고, 검증 가능한 사용자 설치 경로를 제공한다. 자동 설치가 없는 상태를 구현 완료로 표시하지 않는다. 현재 Windows 설치 동작은 유지한다.

### 7.3 Python 환경

현재 관리형 분석 환경은 Windows `win_amd64`/CPython 3.10–3.13에 한정된다. 도구용 환경과 분석용 환경을 분리하는 구조는 유지하되 다음 Windows 가정을 제거한다.

- `Scripts/python.exe`, `basedpyright*.exe`, `ruff.exe` → 플랫폼별 venv 실행 경로. Mac에서는 `bin/`을 사용한다.
- 탐색·환경 재생성 안내·requirements lock 헤더·LSP 설정의 `py`/`.exe`/Windows 경로도 함께 이식한다.
- `core/stats/ml/clinical` 고정 패키지를 각 CPU/Python 조합에서 wheel 해석·설치·import·대표 분석으로 검증한다. LibOMP 등 추가 native 의존성은 설치본에서 확인하고 버전/설치 요구를 명시한다.
- 지원 Python 범위는 검증된 조합으로 확정한다. 소스 빌드를 조용히 허용하거나 고정 패키지를 임의로 빼서 성공 처리하지 않는다.
- 실제 데이터 내보내기 → 사용자 승인 → Python 실행 → 결과/이미지 표시 → 재현 묶음 저장까지 실행한다. 개인정보·원시 값·토큰이 진단 로그에 새로 남지 않아야 한다.

패키지 공급 문제는 명시적인 선행 조건이며 가짜 성공/빈 결과로 대체하지 않는다. 정식 배포 전 지원 매트릭스와 모든 기능의 도달 가능한 실행 경로를 완성한다.

## 8. 기능 대응표와 구현 순서

아래 단계 번호는 9절의 P 번호다. 이식 전 현재 메뉴·명령·옵션을 목록화하여 해당 항목/증거를 연결한다. Windows를 픽셀 단위로 복제하기보다 기능 의미와 데이터 정확성을 보존한다.

| 기능 | 계산/저장 | Mac 화면·플랫폼 작업 | 단계 |
|---|---|---|---|
| CSV/TXT 즉시 탐색·인코딩·찾기·필터·정렬 | 기존 Csv 엔진 | 가상 그리드·진행/취소·원본 행번호 | P1–P3 |
| 셀/행/열 편집·찾아바꾸기·undo/redo·저장 | CellEdits·저널·writer | 선택·붙여넣기·저장 확인·편집 강조 | P2–P3 |
| 탭·상세·패싯·배지·조건부 서식·설정 | 문서/뷰 상태·규칙 | 패널 배치·헤더 필터·테마/언어·Cmd 키 | P3 |
| CSV/MD/JSON/HTML/XLSX 내보내기 | 기존 writer | 형식·범위 선택·취소·파일 권한 | P3 |
| Excel/SAS/SPSS/SQLite·다중 시트 | 기존 Import | 시트 선택·메타데이터·임시 파일 관리 | P4 |
| 작업 공간·SQL·조인/이어붙이기/비교·뷰 | 기존 DuckDB/Workspace | 탐색기·질의 편집기·마법사·`.ncvws` 복원 | P4 |
| 기본/고급 통계·모형·예측·모델 저장/ONNX | Stats·ALGLIB | 옵션·결과·변수 매핑·실제 분석 범위 | P5 |
| 분포/상관/피벗/생존/고급 그림 | 기존 PlotModel/빌더 | Eto.Drawing 렌더러·축/범례/hover·출력 | P5 |
| 피벗·품질·참조 무결성·검사 결과 | 기존 집계/품질 | 후보 검색·페이지 범위·예산 초과/이전 결과 | P5 |
| PDF/HTML 보고서·Markdown/이미지 뷰어 | 기존 보고서 + 그림 데이터 | 글꼴·Mac 웹뷰/이미지 표시·내보내기 | P5–P6 |
| AI 도구·채팅·설정 도우미·omp 설치/로그인 | 기존 Agent 계약/RPC | WKWebView·프로세스·진단·OAuth 브라우저 | P6 |
| 관리형 Python·스킬·LSP·재현 묶음 | 기존 환경/정책 | Mac venv/wheel·출력 뷰어·도구 실행 | P6 |
| 파일 연동·번들·서명·공증·배포 | 제품 메타데이터 | Finder 열기·`.app`/DMG·Gatekeeper | P7 |

피벗/후보/패싯은 기존 고카디널리티 설계를 유지한다: 전체 고유값/밀집 조합을 네이티브 컨트롤에 삽입하지 않고, 페이지·검색·표본 범위를 표시한다. [HIGH_CARDINALITY_STABILITY.md](HIGH_CARDINALITY_STABILITY.md)의 과거 고정 메모리 값은 당시 기록이며, 현재 예산은 v3.4.3 정책/소스를 기준으로 한다.

## 9. 단계별 산출물과 완료 관문

순서: **P0 → P1 → P2 → P3 → P4/P5 → P6 → P7**. P4와 P5는 P3 후 공통 계약을 공유하며 독립 화면을 병행할 수 있다. 웹뷰·native 의존성 위험 확인은 P0에 하고, 기능 구현은 P6에 한다.

| 단계 | 작업·산출물 | 완료 조건 / 실패 시 조치 |
|---|---|---|
| **P0: 기준선·환경** | 현재 기능/형식 목록, Windows 실행 기준선, Mac SDK/workload/Xcode·Eto 버전·최소 OS·CPU 표, licensed ALGLIB 제공 경로, native/omp/wheel/RAM 위험표 | 실제 Mac에서 `.app` 실행·한국어 입력·native 로드·웹 메시지 최소 왕복 확인. 성능은 아직 미통과. 없는 인증서/자산/권한을 선행 조건으로 기록 |
| **P1: 최소 엔진 추출** | CSV·인덱스·행 읽기·편집/뷰·메모리/경로 경계, Core 테스트, 공통 엔진 벤치 | Windows가 추출한 동일 엔진으로 기존 파일을 정상 탐색. Mac console에서 같은 CSV·마지막 행·필터·정렬·편집 저장 재열기 실행. UI 복사본 없음 |
| **P2: 그리드 관문** | 실제 CSV Eto 화면, indexed 어댑터, 제한 캐시, 선택/편집 기능 검증, 10절 계측 보고 | 필수 기능·메모리·응답성 전부 통과. 실패 원인이 구조적이면 그리드만 AppKit 대안 구현 후 같은 시나리오 재검증 |
| **P3: 뷰어/셸 완성** | 메뉴·탭·상세·패싯·설정·조건부 서식·저장/내보내기·Cmd 단축키 | 소형/대용량·한국어·취소·재실행·닫기 시나리오 통과. UI 완료 후 늦은 작업이 문서를 건드리지 않음 |
| **P4: import/workspace** | Import/Workspace 추출, native 패키징, SQL/마법사/뷰/작업 공간 화면 | 포맷별 픽스처·형식/행 수·SQL 결과 동일. 두 OS 작업 공간 왕복과 경로 재지정·원본 변경·뷰 읽기 전용 동작 확인 |
| **P5: 분석/시각화/품질** | Stats·분석 헬퍼·그림 데이터 추출, 분석 대화 상자·Eto 차트·피벗/품질·보고서 | 기존 수치 기준/허용 오차·예측/모델 로드·범위/예산·고카디널리티·한글 PDF 실행 검증. 빈 결과/부분 분석을 성공으로 발표하지 않음 |
| **P6: 에이전트/Python** | Agent.Core 추출, 세 웹뷰 소비자, 설치/진단/승인 화면, 환경/LSP/스킬 | 앱 번들에서 로그인·메시지·실제 앱 도구·승인 거절/취소·Python 분석/출력·세션 복원·개인정보 경계 통과 |
| **P7: 출시** | 아키텍처별 `.app`/DMG, Apple 서명·공증 자동화, CI·설치/지원 문서 | 개발 도구 없는 깨끗한 Mac에서 내려받기→Gatekeeper→Finder 열기→전체 대표 경로→종료 검증. Windows 기존 배포 검증도 통과 |

P1에서는 그리드에 필요한 부분만 먼저 추출하되, P4/P5/P6에서 남은 공통 코드도 이동한다. 이동 완료 기준은 **Windows와 Mac의 같은 계산 경로 사용**이다. PR별 변경 소유자를 하나로 두고 공통 계약 변경을 먼저 통합한다. 서로 다른 화면 구현은 그 계약 이후 병행한다. 고정 완료 날짜/속도 향상률은 제시하지 않고 P2 실측 후 잔여 화면·의존성 규모로 일정을 산정한다.

## 10. 실행 검증·성능 통과 기준

### 10.1 입력과 측정 조건

- 형식: UTF-8/BOM·CP949/EUC-KR, LF/CRLF, 따옴표/구분자/필드 내부 줄바꿈, 마지막 줄 개행 없음, 빈값·긴 한글 셀·열 개수 차이.
- 규모: 소형 정확성 픽스처, **100만/1,000만 행**, **1 GB/4 GB** 파일을 별도 축으로 사용한다. 행 수와 바이트 크기를 같은 의미로 취급하지 않는다.
- 폭: 20열 기준 + 200열 스트레스. 긴 셀은 수십 KB 이상으로 준비해 화면 미리보기와 원문 복사가 다름을 확인한다.
- 고카디널리티: 210만 행·행/열 범주 각 2만, 후보 10만 이상, 마지막 행 일치 검색, 희소 피벗/교차표·예산 초과 복구.
- Windows/Mac 각각 OS·CPU·RAM·저장장치·파일 해시·빌드/Eto 버전·RAM/Disk 모드·cold/warm 캐시를 기록한다. 서로 다른 기계의 ms를 단순 동일 기준으로 비교하지 않는다.

### 10.2 제안하는 관문 수치

**아래 수치는 측정 결과가 아닌 초기 개발 통과 목표**다. 기준 장비는 P0에서 고정한다. 기준 변경은 원인/사용자 영향과 함께 별도 결정으로 기록하며 실패 후 숫자만 완화하지 않는다.

| 항목 | 통과 기준 |
|---|---|
| 정확성 | 선택/복사/저장/재열기 값·행번호 일치. 안정 정렬·필터·undo/redo·전체 마지막 행 포함. 통계 오차는 기존 분석별 기준 사용 |
| UI 응답 | warm 탐색/백그라운드 계산 중 UI 이벤트 지연 p95 ≤ 100 ms, 반복적인 250 ms 이상 UI 정지 없음. 느린 저장장치는 I/O 시간과 UI 정지를 분리 기록 |
| 초기 표시 | 기준 SSD에서 1 GB/4 GB CSV 열기 후 첫 화면 ≤ 2초 목표. 인덱싱 완료 시간은 별도 기록하고 인덱싱 완료까지 기다려 첫 화면을 만드는 경로는 불허 |
| 무작위 이동 | 인덱싱 완료·warm 조건에서 1,000회 위치 이동/마지막 행 이동. 가시 값 완료 p95 ≤ 200 ms 목표. 화면 클릭·취소도 계속 처리 |
| UI 메모리 | 1,000만 행 Disk 모드에서 UI/행 래퍼 추가 retained heap ≤ 128 MiB 목표. 인덱스·행맵·Core 캐시·RAM 원본 버퍼·native 웹뷰는 각각 별도 측정 |
| 전체 메모리 | 전체 행 문자열/행 객체의 N개 사전 생성 없음. 인덱스/필터·정렬 행맵은 행 수에 비례할 수 있으며 이를 UI 누수와 구분. 1 GB 원본을 RAM에 보관하는 모드도 따로 기록 |
| 반복 수명 | 열기/스크롤/정렬/편집/닫기를 20회 반복. warm-up 제외 유지 객체·native handle에 누적 증가 추세 없음; 파일 핸들 반환·임시 파일 정리 확인 |
| 취소/종료 | UI 취소 반응 p95 ≤ 100 ms 목표. 완료/drain 시간은 작업별 별도 계측. 마지막 읽기가 끝나기 전 문서 해제 없음 |
| 웹뷰/대화 | 재로드·작업 공간 변경·창 닫기 중 승인/스트리밍/이미지 요청이 늦게 도착해도 새 상태 오염/원본 경로 노출 없음 |

그리드 계측에는 Count/인덱서/열거 횟수·할당·UI 이벤트 간격·cache hit/miss·해제 후 객체/handle을 포함한다. 인덱싱과 분석의 의도된 전체 스캔은 분리한다. **UI가 열기·스크롤·선택 갱신만으로 전체 DataStore를 열거한다면 실패**다.

### 10.3 기존 검증 자산 활용

현재 테스트 프로젝트는 `net10.0-windows`이며 WinForms 앱을 참조한다. 그대로 Mac 테스트라고 부르지 않는다.

- 파서·인덱스·편집·품질·통계·모델·SQL·import의 행동 테스트를 Core.Tests(`net10.0`)로 이동한다.
- `AnalysisMemoryBudgetTests`, `ConditionalFormatTests`, `WorkspaceFileTests`, `HighCardinalityTests`처럼 엔진/UI가 섞인 파일은 분리한다. Windows `Form1`/STA/WebView2 테스트는 남긴다.
- Core 테스트의 디렉터리 격리는 UI `AppSettings`에 의존하지 않게 한다. 기존 `InternalsVisibleTo`, 비병렬 예산/캐시 컬렉션과 예산 override를 새 어셈블리에 맞춘다.
- Agent.Core로 이동한 로직의 행동/정책 테스트도 해당 중립 테스트 프로젝트로 이동한다. UI 호출 전달만 확인하는 테스트를 새로 만들지 않는다.
- 회귀는 수치·파일 값·범위·승인 전이·오류·취소/수명 등 소비자 동작을 검증한다. 앱 실행·실제 컨트롤 조작·그림/한글 렌더 증거를 함께 남긴다.

현재 Windows 기준선 명령:

```powershell
dotnet test NanumCsvViewer.Tests/NanumCsvViewer.Tests.csproj -c Release
dotnet run --project bench/Bench.csproj -c Release -- cardinality <새로운-CSV-경로> 2100000 512
dotnet run --project bench/Bench.csproj -c Release -- index <기존-CSV-경로>
```

이 문서를 위해 위 테스트/벤치를 재실행하지 않았다. `cardinality`는 새 파일을 생성하므로 기존 파일 경로를 주지 않는다. `bench`는 현재 `net10.0-windows`이고 앱 참조/GDI+ `icon` 모드가 있다. P1에서 엔진 벤치를 `net10.0` Core 참조로 전환하고 `icon`은 Windows 전용 도구로 분리하며 기존 호출자도 갱신한다. 그 전에는 위 명령을 macOS 검증 명령으로 안내하지 않는다.

P1 이후 Core.Tests와 벤치를 Windows/Mac 양쪽에서 실행한다. 대규모 성능 시나리오는 전용 작업으로 분리하고 일반 회귀 실행을 느리게 만들지 않는다. 비공개/사용자 CSV 대신 결정론적 합성 파일·배포 가능한 픽스처를 사용하고 테스트 후 임시 산출물을 정리한다.

## 11. 빌드·CI·서명 배포

### 빌드와 CI

- macOS 호스트에서 .NET macOS workload와 호환 Xcode를 사용한다. Windows cross-build 성공만으로 macOS UI를 검증하지 않는다.
- SDK/workload/Eto/네이티브 패키지를 고정하고 lock을 관리한다. 기존 Windows 배포의 버전·날짜 주입 규칙은 유지한다.
- ALGLIB 상용 소스는 공개 저장소/로그/아티팩트에 올리지 않는다. 라이선스가 허용하는 private builder/안전한 공급 경로를 마련한다. 없는 상태에서 통계를 mock/no-op로 빌드하지 않는다.
- CI는 Windows 기존 UI 회귀 + 중립 Core/Agent 회귀, Mac 중립 회귀 + native 로드/번들 빌드를 수행한다. GUI/Retina/IME는 실제 로그인된 데스크톱에서 검증하고 headless 실행과 구분한다.
- Mac 출력은 아키텍처별 앱 번들/DMG로 배포한다. 첫 배포는 사용자에게 SDK/workload 설치를 요구하지 않는 runtime 포함 번들을 목표로 하며 실제 publish 지원·의존 파일을 검증한다. Universal 번들은 별도 필요가 생기기 전 도입하지 않는다.

### Apple 서명·공증

현재 `scripts/release.ps1`의 SafeNet USB **Windows EV Authenticode 인증서는 Apple Developer ID가 아니다.** 기존 인증서를 macOS 서명에 재사용한다고 계획하지 않는다.

1. Apple Developer Program의 **Developer ID Application** 인증서/키와 공증 자격 증명을 준비한다. `.pkg`를 도입하는 경우 Developer ID Installer도 별도로 필요하다.
2. `.app` 안의 실행 파일·런타임·dylib 등 배포 코드를 내부부터 서명하고 secure timestamp/Hardened Runtime을 적용한다. .NET JIT와 native 라이브러리의 실제 요구 entitlement만 부여한다.
3. 번들/DMG 생성 → 서명 검증 → `notarytool` 제출/로그 확인 → `stapler` 처리 → 최종 hash 기록 순서를 자동화한다. `codesign --deep`만으로 누락 서명을 숨기지 않는다.
4. 각 아키텍처의 깨끗한 Mac에서 HTTPS 다운로드의 quarantine을 유지한 채 Gatekeeper 확인 후 실행한다. 개발 환경의 `dotnet run`이나 quarantine 제거 실행은 배포 증거가 아니다.
5. 서명 후 파일을 바꾸지 않는다. 인증서/계정/API 비밀은 저장소·로그에 넣지 않는다. 앱 밖에서 설치하는 omp/Python의 실행 신뢰도는 앱 공증과 별도로 안내/검증한다.

Windows 자산은 기존 이름·서명·설치 방식으로 유지한다. Mac 자산은 플랫폼/아키텍처를 구분한 새 파일명으로 게시하며 다운로드/도움말/제3자 고지에 반영한다. Eto BSD-3-Clause 및 글꼴/네이티브 패키지/스킬의 재배포 조건을 포함한다. 버전 번호는 구현·관문 완료 후 릴리스 때 정하며, 이 문서 추가만으로 제품 버전을 올리지 않는다.

## 12. 위험·미결 선행 조건·완료 판정

| 위험/미확인 항목 | 해결 단계 | 판단 자료 |
|---|---|---|
| 그리드 지연 접근·셀 선택·가변 높이·대용량 native 스크롤 | P2 | 10절 보고 + 실제 화면/상호작용 |
| Mac SDK/Eto/최소 OS/Intel 조합 | P0, P7 | 고정 도구 표 + 최소 OS 및 CPU별 설치본 실행 |
| macOS 가용 RAM 정의·측정 정확성 | P0–P1 | native 공급자 산정식 + OS/메모리 압박 대조 |
| DuckDB osx 자산의 실제 CPU 지원·로딩 | P0, P4 | binary architecture 확인 + 각 CPU SQL 실행 |
| ALGLIB 소스 공급·추가 플랫폼 배포 허가 | P0 | 라이선스 확인 + private build + ARM64 수치 회귀 |
| 한글 PDF 글꼴·임베딩 권리·그림 차이 | P5 | 글꼴 조건 + 한글 PDF/차트 실제 출력 |
| WKWebView 경로/메시지/드롭 계약 | P0, P6 | 최소 bridge 실행 후 전체 채팅/뷰어 경계 시나리오 |
| macOS omp 공식 자산·digest·Gatekeeper | P0, P6 | 실제 정식 릴리스와 설치/로그인/RPC 실행 |
| Python 고정 패키지 wheel/native 지원 | P0, P6 | CPU/Python별 설치/import/대표 분석 표 |
| Developer ID·공증 계정·배포 호스트 | P0, P7 | 자격 준비 + 최종 배포물의 서명/공증/설치 로그 |

각 단계 결과에는 커밋·도구/OS·입력 해시·실행 명령·화면 증거·실패/미검증 범위를 기록한다. 작성 시점의 기존 문서 실측치를 새 Mac 결과로 재사용하지 않는다. 진행 상태는 **미착수 / 구현 중 / 실행 검증 완료 / 선행 조건 대기**로 구분한다.

전체 완료 조건:

- 두 UI가 같은 C# 계산/저장 구현을 사용하고 Windows 동작·저장 파일·배포가 유지된다.
- 기능 대응표의 항목마다 실제 Mac 실행 증거가 있고, 취소/문서 수명/정확성/메모리 기준이 충족된다.
- 표시된 지원 OS/CPU/Python 조합마다 설치본 검증이 있다. 미검증 조합은 지원으로 표시하지 않는다.
- `.ncvws` 상대 경로 왕복·타 OS 원본 재지정·한글 파일/텍스트/보고서가 정상 동작한다.
- 승인·데이터 공유 정책을 바꾸지 않고 세 웹뷰/omp/Python 경로가 완성된다.
- 서명·공증·설치·최종 아티팩트 hash와 사용자/개발 문서·고지가 갱신된다.

**다음 개발 착수 단위는 P0와 P1이다. P2 통과 전 전체 UI 이식을 확대하지 않는다.**

## 13. 근거 문서·외부 출처

저장소의 현재 소스·계약을 우선하고, 다음 문서는 맥락/기존 수치 기준으로 사용한다.

- [작업 공간 설계](WORKSPACE_PLAN.md), [고급 통계 설계](ADVANCED_STATS_PLAN.md), [품질 설계](DATA_QUALITY_PLAN.md), [에이전트 통합 설계](AGENT_INTEGRATION_PLAN.md).
- [고카디널리티 안정성 기록](HIGH_CARDINALITY_STABILITY.md), [그리드 기능 개발 계획](main-grid-enhancements-development-plan.md), [현재 빌드/배포 안내](../README.md).
- [Eto 2.12.0 정식 릴리스](https://github.com/picoe/Eto/releases/tag/2.12.0), [macOS 패키지의 실제 대상 프레임워크](https://www.nuget.org/packages/Eto.Platform.macOS/2.12.0).
- Eto 2.12.0 고정 소스: [GridView/SelectionPreserver](https://github.com/picoe/Eto/blob/2.12.0/src/Eto/Forms/Controls/GridView.cs), [indexed Count/ElementAt 및 ToList fallback](https://github.com/picoe/Eto/blob/2.12.0/src/Eto/CollectionChangedHandler.cs), [NSTableView 데이터 연결](https://github.com/picoe/Eto/blob/2.12.0/src/Eto.Mac/Forms/Controls/GridViewHandler.cs), [Mac 행 높이/열 크기](https://github.com/picoe/Eto/blob/2.12.0/src/Eto.Mac/Forms/Controls/GridHandler.cs).
- [Eto BSD-3 라이선스](https://github.com/picoe/Eto/blob/2.12.0/LICENSE.txt).
- [Apple: Notarizing macOS software before distribution](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution).

외부 패키지 정보·소스 확인은 계획의 근거이며 실행 검증이 아니다. P1에서 CSV 엔진은 `NanumCsvViewer.Core`로 옮겼다. macOS 앱·Eto UI·서명 배포는 구현하지 않았다.
