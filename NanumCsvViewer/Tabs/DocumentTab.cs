using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer
{
    /// <summary>탭이 보여 주는 문서의 종류.</summary>
    public enum TabKind
    {
        /// <summary>CSV·텍스트 파일 하나.</summary>
        File,
        /// <summary>엑셀·SAS·SPSS·SQLite 파일(워크북). 시트·테이블 전환은 화면 아래 시트 탭이 맡는다 — 워크북 하나가 탭 하나.</summary>
        Sheet,
        /// <summary>뷰 테이블(SQL이 만든 임시 CSV). 읽기 전용.</summary>
        View,
        /// <summary>질의 결과(임시 CSV). 읽기 전용.</summary>
        Result,
    }

    /// <summary>
    /// 열린 문서 하나의 세션. 문서(<see cref="VirtualCsvDocument"/>)·인덱싱·필터·정렬·숨김 열·타입 지정·조건부 서식·품질 발견·창 위치를 쥔다.
    /// 활성 탭의 상태는 Form1의 필드에 올라가 있고(기존 코드가 그대로 읽고 쓴다), 탭을 바꿀 때 이 객체로 내려 저장하고 다음 탭 것을 올린다.
    /// 문서를 다시 열지 않으므로 편집(덮개)·뷰 맵·인덱스가 그대로 유지된다.
    /// </summary>
    public sealed class DocumentTab
    {
        internal DocumentTab(Form1 host, TabKind kind, string path, string title, bool readOnly, string? viewName)
        {
            Host = host;
            Kind = kind;
            Path = path;
            Title = title;
            IsReadOnly = readOnly;
            ViewName = viewName;
        }

        internal Form1 Host { get; }

        public Guid Id { get; } = Guid.NewGuid();
        public TabKind Kind { get; }

        /// <summary>사용자가 연 원본 경로(워크북이면 xlsx·sav 등 원본). 뷰·결과 탭은 그 임시 CSV 경로.</summary>
        public string Path { get; internal set; }

        /// <summary>탭 제목(보통 파일 이름).</summary>
        public string Title { get; internal set; }

        /// <summary>뷰 테이블 이름(뷰·결과 탭). 그 밖에는 null.</summary>
        public string? ViewName { get; }

        /// <summary>true면 편집·구조 변경·붙여넣기가 모두 거부된다(뷰 테이블·질의 결과).</summary>
        public bool IsReadOnly { get; }

        /// <summary>워크북의 현재 시트 이름(워크북이 아니면 null).</summary>
        public string? SheetName { get; internal set; }

        /// <summary>탭 띠에 보이는 이름. 시트가 여럿인 워크북은 "파일 [시트]".</summary>
        public string DisplayName
        {
            get
            {
                var wb = Workbook;
                return wb is { SheetNames.Count: > 1 } && SheetName is { Length: > 0 } ? $"{Title} [{SheetName}]" : Title;
            }
        }

        public bool IsActive => ReferenceEquals(Host.ActiveTab, this);

        /// <summary>이 탭의 문서. 활성이면 Form1의 현재 문서(시트 전환으로 바뀔 수 있음), 아니면 저장된 것.</summary>
        public VirtualCsvDocument? Document => IsClosed ? null : IsActive ? Host.CurrentDocument : Doc;

        internal Import.WorkbookSession? Workbook => IsClosed ? null : IsActive ? Host.CurrentWorkbook : Wb;

        /// <summary>저장하지 않은 편집이 있는가(탭 닫기·종료 확인의 기준).</summary>
        public bool HasUnsavedEdits => Document is { } d && !d.Edits.IsEmpty && d.Edits.IsDirty;

        /// <summary>인덱싱 중인가(백그라운드 탭도 계속 인덱싱한다).</summary>
        public bool IsIndexing => IsActive ? Host.CurrentIsIndexing : Indexing;

        /// <summary>인덱싱 진행률 0–100, 인덱싱 중이 아니면 -1.</summary>
        public int IndexingPercent => IsIndexing ? Math.Clamp(LastProgress?.Percent ?? 0, 0, 99) : -1;

        /// <summary>컬럼별 추론 타입(수동 지정·SAS/SPSS 선언 타입 반영). 인덱싱이 끝나 요약이 계산되기 전에는 빈 목록.</summary>
        public IReadOnlyList<ColumnValueType> InferredColumnTypes
        {
            get
            {
                var summaries = IsActive ? Host.CurrentColumnSummaries : Summaries;
                var types = new ColumnValueType[summaries.Length];
                for (int i = 0; i < types.Length; i++) types[i] = summaries[i].InferredType;
                return types;
            }
        }

        /// <summary>실제로 읽는 파일 경로(워크북이면 시트의 임시 CSV).</summary>
        public string? DocumentPath => IsActive ? Host.CurrentDocumentPath : DocPath;

        public bool IsClosed { get; internal set; }

        public override string ToString() => DisplayName;

        // ------------------------------------------------------------------ 저장된 세션 상태(비활성일 때의 값)

        internal VirtualCsvDocument? Doc;
        internal string? DocPath;
        internal Import.WorkbookSession? Wb;
        internal int SheetIndex;
        internal long LastIndexMs;
        internal ColumnSummary[] Summaries = Array.Empty<ColumnSummary>();

        // 인덱싱(백그라운드 탭도 계속된다)
        internal CancellationTokenSource? IndexCts;
        internal Task? IndexTask;
        internal bool Indexing;
        internal IndexProgress? LastProgress;
        internal bool NeedsIndexFinalize;   // 백그라운드에서 인덱싱이 끝났다 — 활성화할 때 요약·상태를 마무리한다

        // 필터·정렬·열
        internal string WindowTitle = "";        // 창 제목에 붙는 이름("파일  [시트]")
        internal Func<string[], bool>? TextCondition;
        internal string TextConditionDesc = "";
        internal bool FilterMatchAny;
        internal List<(string desc, Func<string[], bool> pred, string? expr)> ValueConditions = new();
        internal List<SortKey> SortKeys = new();
        internal ColumnFilterState ColumnFilters = new();
        internal HashSet<int> HiddenColumns = new();
        internal Dictionary<int, ColumnValueType> ManualTypeOverrides = new();
        internal bool UserResizedRowHeader;

        // 조건부 서식(규칙·컴파일 결과·이력은 문서별)
        internal List<ConditionalFormatRule> CfRules = new();
        internal ConditionalFormatStyler CfStyler = new(ConditionalFormatSet.Empty);
        internal VirtualCsvDocument? CfDoc;
        internal long CfSeenEditsVersion = -1, CfSeenHeaderVersion = -1;
        internal (int Count, int First, int Last) CfViewKey = (-1, -1, -1);
        internal int CfReportedTimeouts, CfFailureShown;
        internal ConditionalFormatHistory CfHistory = new();

        // 데이터 품질 발견
        internal QualityReport? QualityReport;
        internal List<QualityFinding> QualityFindings = new();
        internal VirtualCsvDocument? QualityFindingsDoc;

        // 이 탭이 연 비모달 창(차트·고급 분석·생존 곡선). 비활성이면 숨겨 두었다가 돌아오면 다시 보인다.
        internal List<ChartForm> ChartForms = new();
        internal List<Form> SurvivalPlots = new();
        internal List<Form> HiddenWindows = new();

        // 화면 상태
        internal int[]? ColumnWidths;
        internal int FrozenColumns;   // 왼쪽부터 고정한 열 수
        internal int CurrentRow = -1, CurrentColumn = -1, FirstRow = -1, HorizontalScroll;
        internal string FilterBoxText = "";
        internal int FilterColumnIndex;
        internal string? StatusText;
        internal bool SettlePending;
        internal VirtualCsvDocument? RecoveryOfferedFor;
        internal long ActivationStamp;

        /// <summary>탭을 닫은 뒤 문서·워크북 해제까지(인덱싱 스레드가 멈춘 뒤) 끝나면 완료되는 작업.</summary>
        internal Task DisposeCompletion = Task.CompletedTask;
    }
}
