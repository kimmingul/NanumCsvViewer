using System.Text.RegularExpressions;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Agent.Tools
{
    // CsvHostTools가 실행 중인 창(Form1)에 일을 시키는 경계. 모든 메서드는 UI 스레드에서 호출되며
    // (await 뒤 연속도 UI 스레드), 예상된 실패(문서 없음·작업 중·범위 오류)는 AgentToolException으로 알린다.
    // 이 경계 덕분에 도구 로직(인자 검증·정책·승인 카드·JSON)은 창 없이 가짜 호스트로 테스트된다.

    /// <summary>모델에게 그대로 보여 줄 수 있는 도구 오류. 메시지는 영어(모델용)다.</summary>
    public sealed class AgentToolException : Exception
    {
        public AgentToolException(string message) : base(message) { }
    }

    public sealed record AgentColumn(int Index, string Name, ColumnValueType Type)
    {
        public bool IsNumeric => Type.IsNumeric();
    }

    public enum AgentFilterKind { Expression, TextSearch, CellValue, ColumnFilter }

    /// <summary>활성 필터 1개. Description에는 셀 값이 섞일 수 있어(셀값·컬럼 필터) 정책에 따라 가린다.</summary>
    public sealed record AgentFilterInfo(AgentFilterKind Kind, string Description, string? Column = null);

    public sealed record AgentSortInfo(string Column, bool Ascending);

    public sealed record AgentEditState(
        int Cells, int RenamedColumns, int DeletedRows, int AddedRows,
        bool Unsaved, bool CanUndo, string? UndoDescription, bool AgentCanUndo, bool SheetEditMode,
        int AddedColumns = 0, int DeletedColumns = 0)
    {
        public static readonly AgentEditState None = new(0, 0, 0, 0, false, false, null, false, false);

        /// <summary>저장할 편집이 하나라도 있는가(셀·컬럼 이름·행·컬럼 구조).</summary>
        public bool HasAny => Cells + RenamedColumns + DeletedRows + AddedRows + AddedColumns + DeletedColumns > 0;
    }

    public sealed record AgentCursor(long? SourceRow, string? Column);

    /// <summary>열린 문서의 스냅샷. ProtectedPaths(원본 파일들)는 저장 덮어쓰기 방지용이며 모델에게 보내지 않는다.</summary>
    public sealed record AgentDocumentInfo(
        string FileName,
        string? SheetName,
        IReadOnlyList<string> SheetNames,
        string Encoding,
        string Delimiter,
        long FileBytes,
        bool IndexingComplete,
        int IndexingPercent,
        bool Busy,
        long TotalRows,
        long ViewRows,
        bool RowCountTruncated,
        IReadOnlyList<AgentColumn> Columns,
        IReadOnlyList<AgentFilterInfo> Filters,
        bool FilterMatchAny,
        IReadOnlyList<AgentSortInfo> Sort,
        IReadOnlyList<string> HiddenColumns,
        AgentEditState Edits,
        AgentCursor Cursor,
        string? Directory,
        IReadOnlyList<string> ProtectedPaths);

    /// <summary>필터·정렬 뒤의 뷰 크기. RegexTimedOut은 식 필터의 정규식이 셀당 시간 제한을 넘겨 "불일치"로 처리된 셀 수.</summary>
    public sealed record AgentViewChange(long ViewRows, long TotalRows, long RegexTimedOut = 0);

    /// <summary>현재 뷰의 행 조각. Rows[i].Values는 요청한 컬럼 순서.</summary>
    public sealed record AgentRowsPage(long FirstViewRow, IReadOnlyList<AgentRow> Rows, long ViewRows);

    public sealed record AgentRow(long SourceRow, string[] Values);

    /// <summary>분석·통계 계산용 뷰 행. Rows는 필터·정렬이 반영된 현재 뷰(지연 읽기)이며 작업 스레드에서만 순회한다.</summary>
    public sealed record AgentViewData(
        IReadOnlyList<string[]> Rows,
        IReadOnlyList<string> Headers,
        IReadOnlyList<ColumnValueType> Types);

    public sealed record AgentGotoResult(long SourceRow, long ViewRow, string Column);

    /// <summary>편집 대상 1개. SourceRow는 1-based 현재 행 번호(행 머리글·get_rows의 row), Column은 0-based 컬럼 인덱스.</summary>
    public sealed record AgentCellEdit(long SourceRow, int Column, string Value);

    /// <summary>편집 전 셀 상태. Exists=false면 행 번호가 범위 밖. Current는 덮개 적용 값.</summary>
    public sealed record AgentCellState(bool Exists, string Current);

    public sealed record AgentEditResult(int Changed, int Unchanged, int TotalCells, AgentEditState State);

    public sealed record AgentUndoResult(string Description, AgentEditState State);

    public sealed record AgentSaveResult(string FullPath, string Summary);

    /// <summary>정규식 일치 셀 예시. SourceRow는 1-based 현재 행 번호(행 머리글·get_rows의 row).</summary>
    public sealed record AgentRegexSample(long SourceRow, int Column, string Value);

    /// <summary>현재 뷰 정규식 집계. CellsTimedOut은 셀당 시간 제한을 넘겨 평가하지 못한 셀(일치 수는 그만큼 하한).</summary>
    public sealed record AgentRegexScan(long RowsScanned, long CellsMatched, long RowsMatched, long CellsTimedOut, IReadOnlyList<AgentRegexSample> Samples);

    public sealed record AgentRegexChange(long SourceRow, int Column, string Old, string New);

    /// <summary>정규식 바꾸기 계획(아직 적용 전). Truncated면 상한을 넘는 변경이 더 있다.</summary>
    public sealed record AgentRegexPlan(IReadOnlyList<AgentRegexChange> Changes, long RowsScanned, long CellsMatched, long CellsTimedOut, bool Truncated);

    public sealed record AgentRowNumbers(IReadOnlyList<long> Rows, bool Truncated);

    /// <summary>구조 편집 결과. FirstRow는 새로 들어간 첫 행 번호(삽입), Count는 영향받은 행 수.</summary>
    public sealed record AgentStructureResult(long FirstRow, int Count, long TotalRows, AgentEditState State);

    /// <summary>컬럼 추가·삭제 결과. Column은 뷰(변경 후)의 0-based 인덱스(삭제면 삭제 전 인덱스).</summary>
    public sealed record AgentColumnChange(int Column, string Name, int ColumnCount, AgentEditState State);

    public interface ICsvAgentHost
    {
        /// <summary>열린 문서가 없으면 null.</summary>
        AgentDocumentInfo? GetInfo();

        Task<AgentRowsPage> GetRowsAsync(long firstViewRow, int count, IReadOnlyList<int> columns, CancellationToken cancellation);

        /// <summary>
        /// 현재 뷰 행을 작업 스레드에서 work에 넘긴다. 호스트는 busy 표시·행 스냅샷·문서 교체 감시를 맡는다.
        /// work는 UI에 접근하지 않는다.
        /// </summary>
        Task<T> RunOnViewRowsAsync<T>(string description, Func<AgentViewData, CancellationToken, T> work, CancellationToken cancellation);

        /// <summary>분석 결과 창을 연다(일반 메뉴와 같은 창).</summary>
        void ShowAnalysisWindow(AgentAnalysisOutcome outcome);

        /// <param name="replace">true면 기존 필터를 모두 지우고 이 식만 적용, false면 현재 뷰를 이 식으로 더 좁힌다.</param>
        Task<AgentViewChange> SetFilterAsync(string expression, bool replace, CancellationToken cancellation);

        Task<AgentViewChange> ClearFilterAsync(CancellationToken cancellation);

        /// <summary>keys가 비면 정렬 해제.</summary>
        Task<AgentViewChange> SortAsync(IReadOnlyList<SortKey> keys, CancellationToken cancellation);

        Task<AgentGotoResult> GotoAsync(long? sourceRow, int? column, CancellationToken cancellation);

        /// <summary>품질 프로파일을 실행하고 품질 패널에도 반영한다.</summary>
        Task<QualityReport> RunQualityScanAsync(CancellationToken cancellation);

        /// <summary>현재 뷰(필터·정렬, 편집 덮개 적용)의 지정 컬럼을 정규식으로 센다. 시간 초과 셀은 세어 알린다.</summary>
        Task<AgentRegexScan> RegexCountAsync(Regex regex, IReadOnlyList<int> columns, int maxSamples, CancellationToken cancellation);

        /// <summary>현재 뷰의 바꾸기 계획을 만든다(편집 덮개는 건드리지 않음). 적용은 ApplyEdits로 한 단계에.</summary>
        Task<AgentRegexPlan> PlanRegexReplaceAsync(Regex regex, string replacement, IReadOnlyList<int> columns, int maxChanges, CancellationToken cancellation);

        /// <summary>편집 전 값 조회(승인 카드용). 입력 순서대로.</summary>
        IReadOnlyList<AgentCellState> GetCellStates(IReadOnlyList<(long SourceRow, int Column)> cells);

        /// <summary>편집 덮개에 한 단계로 기록. 시트 편집 모드와 무관. description은 되돌리기 메뉴에 표시된다.</summary>
        AgentEditResult ApplyEdits(IReadOnlyList<AgentCellEdit> edits, string description);

        /// <summary>마지막 단계가 에이전트가 만든 것일 때만 되돌린다.</summary>
        AgentUndoResult UndoAgentEdit();

        /// <summary>편집을 새 파일로 저장(.csv/.tsv/.txt/.xlsx). 경로 정책은 호출자가 이미 검증했다.</summary>
        Task<AgentSaveResult> SaveEditsAsAsync(string fullPath, CancellationToken cancellation);

        // ---- 구조 편집(편집 덮개 한 단계, 시트 편집 모드와 무관). 행 번호는 행 머리글 번호이며 삭제 뒤에는 뒤 행 번호가 당겨진다.

        /// <summary>현재 뷰 행의 행 번호(뷰 순서). cap을 넘으면 Truncated.</summary>
        Task<AgentRowNumbers> GetViewRowNumbersAsync(int cap, CancellationToken cancellation);

        /// <summary>count개의 빈 행을 넣어 첫 새 행이 rowNumber번 행이 되게 한다(rowNumber = 행 수 + 1이면 맨 끝에 추가).</summary>
        AgentStructureResult InsertRows(long rowNumber, int count, string description);

        AgentStructureResult DeleteRows(IReadOnlyList<long> rowNumbers, string description);

        /// <summary>맨 끝에 컬럼을 추가한다. fill이 비어 있지 않으면 모든 행에 그 문자열을 채운다.</summary>
        AgentColumnChange AddColumn(string name, string? fill, string description);

        /// <summary>컬럼 삭제(원본·추가 컬럼 모두). 뒤 컬럼 인덱스가 하나씩 당겨진다.</summary>
        AgentColumnChange DeleteColumn(int column, string description);

        // ---- 조건부 서식(보기 상태, 되돌리기 이력 없음)

        /// <summary>적용되지 않는 규칙(컬럼이 삭제된 경우 등)의 id → 이유.</summary>
        IReadOnlyDictionary<string, string> ConditionalFormatProblems();
        IReadOnlyList<ConditionalFormatRule> ListConditionalFormats();
        ConditionalFormatRule AddConditionalFormat(ConditionalFormatRule draft);
        bool RemoveConditionalFormat(string id);
        int ClearConditionalFormats();

        /// <summary>현재 뷰에서 규칙(id) 또는 초안이 일치하는 행 수.</summary>
        Task<ConditionalFormatCount> CountConditionalFormatAsync(string? id, ConditionalFormatRule? draft, CancellationToken cancellation);

        // ---- 뷰어 창

        ViewerShowResult ShowMarkdown(string fullPath);
        ViewerShowResult ShowImage(string fullPath);

        /// <summary>채팅 패널에 이미지 미리보기를 올린다. 실패(파일 없음·출력 폴더 밖·패널 없음)면 false.</summary>
        bool PostInlineImage(string fullPath, string? caption);
    }
}
