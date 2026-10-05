using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Workspace
{
    /// <summary>CSV 원본 등록 옵션. 모두 선택 사항 — 비워 두면 파일에서 알아낸다.</summary>
    public sealed record CsvSourceOptions
    {
        public static CsvSourceOptions Default { get; } = new();

        /// <summary>구분자. null이면 DuckDB가 판별(앱의 탭이 이미 열려 있으면 그 문서의 구분자를 넘기는 것이 가장 정확).</summary>
        public char? Delimiter { get; init; }

        /// <summary>
        /// 인코딩 이름(<see cref="EncodingDetector"/>의 "UTF-8"·"UTF-8 (BOM)"·"CP949 / EUC-KR"). null이면 자동 감지.
        /// UTF-8이 아니면 앱 파서로 만든 UTF-8 임시 사본을 등록한다.
        /// </summary>
        public string? EncodingName { get; init; }

        /// <summary>첫 줄이 헤더인가. false면 컬럼 이름은 DuckDB 기본값(column0, column1…).</summary>
        public bool HasHeader { get; init; } = true;

        /// <summary>
        /// 컬럼 순서대로의 추론 타입. null이면 앞쪽 <see cref="DataWorkspace.InferenceSampleRows"/>행으로 앱의 추론기(ColumnStatisticsBuilder)가 정한다.
        /// 목록이 컬럼 수보다 짧으면 나머지는 문자열.
        /// </summary>
        public IReadOnlyList<ColumnValueType>? ColumnTypes { get; init; }

        // 열 수가 들쭉날쭉한 행은 항상 허용한다(모자라면 빈 값으로 채움). DuckDB는 이 경우 따옴표 안 줄바꿈 때문에 병렬 파서를 쓸 수 없어 단일 스레드로 읽는다.
    }

    /// <summary>DB형 원본(엑셀 통합 문서·SAS·SPSS·SQLite)의 표 하나: 앱의 WorkbookSession이 만든 임시 CSV와 이름.</summary>
    public sealed record DbTableInput(string TableName, string CsvPath, CsvSourceOptions? Options = null);

    public enum WorkspaceSourceKind { Csv, Database }

    /// <summary>표·뷰 공통 — SQL에 쓸 수 있는 관계(relation).</summary>
    public interface IWorkspaceRelation
    {
        /// <summary>따옴표 없는 표시 이름. CSV·뷰는 "명단", DB 표는 "설문.명단".</summary>
        string DisplayName { get; }
        /// <summary>SQL에 그대로 쓸 수 있는 완전 한정 참조(항상 따옴표 포함).</summary>
        string SqlReference { get; }
        IReadOnlyList<WorkspaceColumn> Columns { get; }
    }

    /// <summary>
    /// 컬럼 하나. <see cref="Type"/>은 SQL에서 노출되는 형(문자열이면 String/Identifier/Categorical…),
    /// <see cref="IsConverted"/>가 참이면 원문 문자열을 TRY_CAST한 형 변환 컬럼이다(원문은 "&lt;표&gt;__raw").
    /// </summary>
    public sealed record WorkspaceColumn(string Name, ColumnValueType Type, string SqlType, bool IsConverted);

    public sealed class WorkspaceTable : IWorkspaceRelation
    {
        internal WorkspaceTable(WorkspaceSource source, string schema, string name, string filePath, string readPath,
            CsvSourceOptions options, IReadOnlyList<WorkspaceColumn> columns, DateTime lastWriteUtc, long length)
        {
            Source = source; Schema = schema; Name = name; FilePath = filePath; ReadPath = readPath;
            Options = options; Columns = columns; LastWriteUtc = lastWriteUtc; Length = length;
        }

        public WorkspaceSource Source { get; }
        /// <summary>DuckDB 스키마. CSV 원본은 "main", DB 원본은 DB 이름.</summary>
        public string Schema { get; }
        public string Name { get; }
        /// <summary>사용자가 지정한 CSV 경로(DB 표는 임포트가 만든 임시 CSV).</summary>
        public string FilePath { get; }
        /// <summary>DuckDB가 실제로 읽는 경로(UTF-8이 아니면 UTF-8 임시 사본).</summary>
        public string ReadPath { get; }
        public bool IsConvertedCopy => !string.Equals(FilePath, ReadPath, StringComparison.OrdinalIgnoreCase);
        public CsvSourceOptions Options { get; }
        public IReadOnlyList<WorkspaceColumn> Columns { get; }
        /// <summary>등록 시점의 <see cref="FilePath"/> 수정 시각·크기(원본 변경 감지용).</summary>
        public DateTime LastWriteUtc { get; }
        public long Length { get; }

        public string DisplayName => Schema == "main" ? Name : Schema + "." + Name;
        public string SqlReference => SqlNames.Qualified(Schema, Name);
        /// <summary>모든 컬럼이 원문 문자열(VARCHAR)인 보조 뷰.</summary>
        public string RawSqlReference => SqlNames.Qualified(Schema, Name + SqlNames.RawSuffix);

        public override string ToString() => DisplayName;
    }

    public sealed class WorkspaceSource
    {
        internal WorkspaceSource(Guid id, WorkspaceSourceKind kind, string name, string path)
        { Id = id; Kind = kind; Name = name; Path = path; }

        public Guid Id { get; }
        public WorkspaceSourceKind Kind { get; }
        /// <summary>최상위 이름: CSV는 표 이름, DB는 DuckDB 스키마 이름.</summary>
        public string Name { get; internal set; }
        /// <summary>CSV 원본 경로, DB 원본은 원래 통합 문서 경로(알면; 없으면 빈 문자열).</summary>
        public string Path { get; internal set; }
        public IReadOnlyList<WorkspaceTable> Tables { get; internal set; } = Array.Empty<WorkspaceTable>();
        /// <summary>등록(또는 마지막 다시 읽기) 시점의 원본 파일 수정 시각·크기 표식.</summary>
        internal string RegisteredStamp { get; set; } = string.Empty;

        public override string ToString() => Name;
    }

    /// <summary>
    /// 뷰를 누가 어떻게 만들었나: "user"(SQL 편집기 등 직접), "agent"(AI 에이전트 ws.* 도구), "wizard:join|append|compare|group"(마법사).
    /// 에이전트가 만든 뷰는 그 턴의 사용자 요청 글(<see cref="MaxRequestChars"/>자까지)을 함께 적는다. 작업 공간 파일(.ncvws v2)에 저장된다.
    /// </summary>
    public sealed record ViewProvenance(string CreatedBy, DateTime CreatedUtc, string? Request = null)
    {
        public const string UserKind = "user";
        public const string AgentKind = "agent";
        public const string WizardPrefix = "wizard:";
        /// <summary>에이전트 뷰에 적는 사용자 요청 글의 최대 길이.</summary>
        public const int MaxRequestChars = 500;

        public bool IsAgent => string.Equals(CreatedBy, AgentKind, StringComparison.Ordinal);
        public bool IsWizard => CreatedBy.StartsWith(WizardPrefix, StringComparison.Ordinal);
        /// <summary>마법사 종류(join·append·compare·group), 마법사가 아니면 null.</summary>
        public string? WizardName => IsWizard ? CreatedBy[WizardPrefix.Length..] : null;

        public static ViewProvenance User(DateTime? utc = null) => new(UserKind, Stamp(utc));
        public static ViewProvenance Agent(string? request, DateTime? utc = null) => new(AgentKind, Stamp(utc), ClipRequest(request));
        public static ViewProvenance Wizard(string kind, DateTime? utc = null) => new(WizardPrefix + kind.Trim().ToLowerInvariant(), Stamp(utc));

        /// <summary>저장 파일에서 읽은 값 검사: 알려진 종류만 받는다(그 밖은 null — 출처 모름).</summary>
        public static bool IsKnownKind(string? createdBy) =>
            createdBy is UserKind or AgentKind
            || createdBy is not null && createdBy.StartsWith(WizardPrefix, StringComparison.Ordinal)
               && createdBy[WizardPrefix.Length..] is "join" or "append" or "compare" or "group";

        /// <summary>공백을 정리하고 <see cref="MaxRequestChars"/>자로 줄인다(넘으면 …). 비면 null.</summary>
        public static string? ClipRequest(string? request)
        {
            if (string.IsNullOrWhiteSpace(request)) return null;
            string t = request.Trim();
            return t.Length <= MaxRequestChars ? t : t[..(MaxRequestChars - 1)] + "…";
        }

        private static DateTime Stamp(DateTime? utc)
        {
            var t = utc ?? DateTime.UtcNow;
            return t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
        }
    }

    /// <summary>뷰 테이블: 이름 + SQL. 결과는 <see cref="DataWorkspace.MaterializeViewAsync"/>가 임시 CSV로 쓴다.</summary>
    public sealed class WorkspaceView : IWorkspaceRelation
    {
        internal WorkspaceView(Guid id, string name, string sql, bool includeUnsavedEdits)
        { Id = id; Name = name; Sql = sql; IncludeUnsavedEdits = includeUnsavedEdits; }

        /// <summary>누가 언제 만들었나(작업 공간 파일 v1에서 온 뷰처럼 모르면 null).</summary>
        public ViewProvenance? Provenance { get; internal set; }

        public Guid Id { get; }
        public string Name { get; internal set; }
        public string Sql { get; internal set; }
        /// <summary>결과를 계산할 때 저장 안 한 편집(앱이 제공하는 스냅숏)을 반영하는가.</summary>
        public bool IncludeUnsavedEdits { get; internal set; }
        /// <summary>이 뷰가 직접 참조하는 원본·뷰의 최상위 이름(대소문자 무시 중복 제거).</summary>
        public IReadOnlyList<string> Dependencies { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<WorkspaceColumn> Columns { get; internal set; } = Array.Empty<WorkspaceColumn>();
        /// <summary>정의가 지금 실행 불가능하면(예: 참조하던 원본 제거·컬럼 삭제) 이유. 정상이면 null.</summary>
        public string? Error { get; internal set; }

        /// <summary>마지막 계산 결과 임시 CSV(없으면 null).</summary>
        public string? ResultPath { get; internal set; }
        public long? ResultRowCount { get; internal set; }
        public DateTime? ComputedUtc { get; internal set; }
        public bool HasResult => ResultPath is not null;

        internal long Version;
        internal string? ComputedSignature;

        public string DisplayName => Name;
        public string SqlReference => SqlNames.Qualified("main", Name);

        public override string ToString() => Name;
    }

    public sealed record QueryColumn(string Name, string SqlType, ColumnValueType Type);

    /// <summary>미리보기 결과. 값은 DuckDB의 VARCHAR 표현(CSV로 쓸 때와 같은 글자)이고 NULL은 null.</summary>
    public sealed record QueryPreview(IReadOnlyList<QueryColumn> Columns, IReadOnlyList<string?[]> Rows, bool Truncated, TimeSpan Elapsed);

    public sealed record QueryResultInfo(long RowCount, IReadOnlyList<QueryColumn> Columns, TimeSpan Elapsed, string OutputPath);

    /// <summary>형 변환 컬럼 하나의 변환 결과 요약: 비어 있지 않은 값 수, 변환 실패(NULL이 된) 수와 예시.</summary>
    public sealed record ColumnCastReport(string Table, string Column, ColumnValueType Type, long NonEmptyCount, long FailureCount,
        IReadOnlyList<string> FailureExamples);

    public enum JoinKind { Inner, Left, Right, Full }

    public sealed record JoinKey(string LeftColumn, string RightColumn);

    public sealed record JoinSpec(IWorkspaceRelation Left, IWorkspaceRelation Right, IReadOnlyList<JoinKey> Keys, JoinKind Kind = JoinKind.Inner);

    public enum JoinCardinality { OneToOne, OneToMany, ManyToOne, ManyToMany }

    /// <summary>
    /// 조인 키 진단. 키가 하나라도 NULL인 행은 어떤 행과도 일치하지 않는 것으로 센다(SQL 조인 의미 그대로).
    /// 모든 수치는 같은 키 식(<see cref="JoinSql"/>)으로 계산하므로 실제 조인 결과의 행 수와 같다.
    /// </summary>
    public sealed record JoinDiagnostics(
        long LeftRows, long RightRows,
        long LeftNullKeyRows, long RightNullKeyRows,
        long LeftDistinctKeys, long RightDistinctKeys,
        long MatchedKeys, long LeftOnlyKeys, long RightOnlyKeys,
        long LeftDuplicateKeys, long RightDuplicateKeys,
        long LeftMaxKeyRows, long RightMaxKeyRows,
        long MatchedRowsInner,
        long ExpectedRows, JoinKind Kind,
        JoinCardinality Cardinality,
        IReadOnlyList<string> KeyTypeWarnings)
    {
        /// <summary>예상 결과 행 수 ÷ max(왼쪽 행 수, 오른쪽 행 수). 1보다 크게 늘면 행이 불어난 것(중복 키).</summary>
        public double GrowthFactor => Math.Max(LeftRows, RightRows) == 0 ? 1 : (double)ExpectedRows / Math.Max(LeftRows, RightRows);
        /// <summary>양쪽 키 모두 일치하는 키가 하나도 없는가.</summary>
        public bool NoMatches => MatchedKeys == 0;
    }

    /// <summary>SQL 분석 결과: 한 문장 SELECT인지, 오류 위치, 참조한 표 이름.</summary>
    public sealed record SqlAnalysis(bool IsSingleSelect, string? Error, int? Line, int? Column, IReadOnlyList<SqlTableRef> Tables)
    {
        public bool IsValid => Error is null;
    }

    /// <summary>FROM 절에서 참조한 표(스키마가 안 적혔으면 Schema는 빈 문자열). CTE 이름도 섞여 나올 수 있다.</summary>
    public sealed record SqlTableRef(string Schema, string Name);

    /// <summary>"저장 안 한 편집 포함" 뷰를 계산할 때 앱이 제공하는 편집 반영 임시 CSV(<c>SaveWithEdits</c> 결과).</summary>
    public interface IEditSnapshotProvider
    {
        /// <summary>표에 저장 안 한 편집이 있으면 편집 반영본 CSV의 경로, 없으면 null. 백그라운드 스레드에서 호출된다.</summary>
        string? GetEditedSnapshotPath(WorkspaceTable table, CancellationToken ct);
    }
}
