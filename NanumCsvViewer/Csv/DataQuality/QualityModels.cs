using System.Text.Json;
using System.Text.Json.Serialization;

namespace NanumCsvViewer.Csv.DataQuality
{
    // 데이터 품질 검토(이슈 #26)의 순수 데이터 모델. WinForms 의존 없음 — 계산/렌더 분리 관행(#19 계승).
    // 4-모델 설계 논쟁 합의: 점수 게이지 없음, Kahn 차원은 내부 태그로만, 발견 = 심각도별 건수 + 위반 행 술어.

    /// <summary>Kahn 3차원. UI 라벨이 아니라 발견 항목의 분류 태그로만 사용한다(설계 논쟁 4:0 합의).</summary>
    public enum QualityDimension { Conformance, Completeness, Plausibility }

    /// <summary>발견 심각도. 합성 점수 대신 심각도별 건수가 이 모듈의 요약 지표다.</summary>
    public enum QualitySeverity { Info, Warning, Critical }

    /// <summary>검사 종류. UI가 이름·설명을 현지화할 때의 키.</summary>
    public enum QualityCheckKind
    {
        MissingRate,          // 결측률(빈 문자열·공백·널 토큰)
        DisguisedMissing,     // 위장결측 후보(99/999/"NA"/1900-01-01 등 sentinel) — "후보+확인" 의미론
        TypeConformance,      // 기대 타입으로 해석 불가한 값
        CodebookConformance,  // 코드북(값 라벨) 집합 밖 값 — SPSS/SAS 메타데이터 있을 때 자동
        ConstantColumn,       // 상수/거의상수 컬럼
        EmptyColumn,          // 전부 결측인 컬럼
        DuplicateRows,        // 완전 중복 행(64-bit 해시 기반)
        Outliers,             // IQR 1.5 울타리 밖 수치(표본 기반 추정 가능)
        FutureDate,           // 기준일 이후 날짜
        RaggedRows,           // 헤더와 필드 수가 다른 행(구조 위반)
        KeyUniqueness,        // 사용자 지정 (복합)키 중복
        Rule,                 // 사용자 정의 규칙 위반(위반 조건식)
        ForeignKeyOrphan,     // 자식 키 값이 부모 키 집합에 없음(참조 무결성 후보)
        ConformanceRequired,  // 적합성 프로파일: 필수 컬럼 부재(테이블) 또는 필수 값 결측
        ConformanceType,      // 적합성 프로파일: 선언 타입 불일치
        ConformanceMaxLength, // 적합성 프로파일: 최대 길이(CDISC 변수 길이) 초과
        ConformanceCodelist,  // 적합성 프로파일: 허용 코드 밖
        ConformancePattern,   // 적합성 프로파일: 정규식 불일치
        ConformanceRange,     // 적합성 프로파일: 수치 범위 이탈
        ConformanceConcept,   // 적합성 프로파일: 개념 참조(다른 파일 키+선택 도메인) 불일치
        DqdImported,          // OHDSI DQD 결과 JSON에서 가져온 발견(행 술어 없음)
    }

    /// <summary>
    /// 사용자가 세션마다 실행하는 검사. 품질 프로파일 재실행이 지우면 안 되고,
    /// 스냅샷 diff는 현재에 없다고 "해소"로 단정하지 않는다(재검사 안 됨).
    /// </summary>
    public static class QualitySessionChecks
    {
        public static bool IsConformance(QualityCheckKind kind) => kind is
            QualityCheckKind.ConformanceRequired or QualityCheckKind.ConformanceType
            or QualityCheckKind.ConformanceMaxLength or QualityCheckKind.ConformanceCodelist
            or QualityCheckKind.ConformancePattern or QualityCheckKind.ConformanceRange
            or QualityCheckKind.ConformanceConcept;

        public static bool IsUserRun(QualityCheckKind kind) =>
            kind is QualityCheckKind.Rule or QualityCheckKind.KeyUniqueness
                or QualityCheckKind.ForeignKeyOrphan or QualityCheckKind.DqdImported
            || IsConformance(kind);

        /// <summary>같은 종류·컬럼에 검사가 여럿일 수 있어 라벨(규칙명·검사명)이 정체성에 포함된다.</summary>
        public static bool UsesLabel(QualityCheckKind kind) =>
            kind is QualityCheckKind.Rule or QualityCheckKind.DqdImported || IsConformance(kind);
    }


    /// <summary>위반 예시 1건: 원본 행번호(1-based) + 문제 값.</summary>
    public readonly record struct QualityExample(long SourceRow, string Value);

    /// <summary>값별 건수(위장결측 후보 분해 등).</summary>
    public readonly record struct ValueCount(string Value, long Count);

    /// <summary>
    /// 발견 1건. <see cref="ViolationPredicate"/>는 위반 행 술어 — 기존 필터 칩으로 변환해
    /// "발견 → 필터 → 행 점프" 루프를 만든다(직렬화 제외).
    /// </summary>
    public sealed record QualityFinding
    {
        public required QualityCheckKind Kind { get; init; }
        public required QualityDimension Dimension { get; init; }
        public required QualitySeverity Severity { get; init; }
        /// <summary>대상 컬럼 인덱스. -1 = 테이블 수준(중복 행·구조 등).</summary>
        public int Column { get; init; } = -1;
        public string ColumnName { get; init; } = "";
        public long ViolationCount { get; init; }
        public long EvaluatedRows { get; init; }
        /// <summary>검사 대상에서 제외한 행 수(예: 참조 무결성의 빈 자식 키). 제외 사유는 검사 종류가 정한다.</summary>
        public long SkippedRows { get; init; }
        /// <summary>표본 기반 추정치인지(이상치 등). true면 UI가 "≈"를 표기해 정직하게 알린다.</summary>
        public bool Approximate { get; init; }
        public IReadOnlyList<QualityExample> Examples { get; init; } = Array.Empty<QualityExample>();
        /// <summary>세부 분해(예: 위장결측 후보 값별 건수).</summary>
        public IReadOnlyList<ValueCount> Breakdown { get; init; } = Array.Empty<ValueCount>();
        /// <summary>이상치 울타리(IQR ± 1.5×IQR). Outliers에서만.</summary>
        public double? FenceLow { get; init; }
        public double? FenceHigh { get; init; }
        /// <summary>부가 식별자: 규칙명(Rule)·키 구성(KeyUniqueness)·상수값(ConstantColumn) 등.</summary>
        public string? Label { get; init; }
        [JsonIgnore] public Func<string[], bool>? ViolationPredicate { get; init; }
    }

    /// <summary>컬럼 1개의 프로파일(스냅샷 직렬화 대상 — 후속 diff의 선행물).</summary>
    public sealed record QualityColumnProfile
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required ColumnValueType ExpectedType { get; init; }
        public long EmptyCount { get; init; }
        public long WhitespaceCount { get; init; }
        public long NullTokenCount { get; init; }
        public long NonNullCount { get; init; }
        public long MissingCount => EmptyCount + WhitespaceCount + NullTokenCount;
        public long DistinctCount { get; init; }
        /// <summary>고유값 추적 상한에 걸려 DistinctCount가 하한값인지.</summary>
        public bool DistinctIsLowerBound { get; init; }
        public long TypeViolationCount { get; init; }
        public long CodebookViolationCount { get; init; }
        public double? NumericMin { get; init; }
        public double? NumericMax { get; init; }
        public double? NumericMean { get; init; }
        public double? NumericStdDev { get; init; }
        /// <summary>시간 컬럼의 최소/최대(ISO-8601 문자열 — 직렬화 안정).</summary>
        public string? TemporalMin { get; init; }
        public string? TemporalMax { get; init; }
    }

    /// <summary>
    /// 품질 스캔 결과 전체. JSON 직렬화 = 프로파일 스냅샷(안정 스키마, 후속 버전 diff의 기반).
    /// ScannedFully로 전수/부분을 정직하게 표기한다(설계 논쟁 합의 — "거짓말하지 않는 검사 범위").
    /// </summary>
    public sealed record QualityReport
    {
        /// <summary>스냅샷 스키마 버전. 필드 추가·의미 변경 시 올린다(후속 diff 호환성 판정용).</summary>
        public int SchemaVersion { get; init; } = 1;
        public required long RowsScanned { get; init; }
        public required bool ScannedFully { get; init; }
        /// <summary>행 수가 상한을 넘어 완전 중복 행 검사를 생략했는지(정직 표기).</summary>
        public bool DuplicateRowCheckSkipped { get; init; }
        public required double ElapsedSeconds { get; init; }
        public required IReadOnlyList<QualityColumnProfile> Columns { get; init; }
        public required IReadOnlyList<QualityFinding> Findings { get; init; }
        /// <summary>파일 지문: 이름·바이트 크기(내용 해시는 후속 — 별도 패스 비용).</summary>
        public string SourceName { get; init; } = "";
        public long SourceBytes { get; init; }
        /// <summary>ISO-8601. 호출자가 주입(엔진은 시계에 접근하지 않음 — 결정론).</summary>
        public string ScanTimestamp { get; init; } = "";
        public string AppVersion { get; init; } = "";

        public long CountBySeverity(QualitySeverity s)
        {
            long n = 0;
            foreach (var f in Findings) if (f.Severity == s) n++;
            return n;
        }
    }

    /// <summary>스캔 동작 파라미터. 기본값은 설계 논쟁에서 합의한 "후보 의미론" 임계.</summary>
    public sealed record QualityScanOptions
    {
        public int MaxExamples { get; init; } = 20;
        public int DegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
        /// <summary>파티션당 고유값 추적 상한(메모리 가드). 초과 시 하한 표기.</summary>
        public int DistinctCapPerPartition { get; init; } = 1024;
        public int DistinctCapMerged { get; init; } = 4096;
        /// <summary>파티션당 수치 표본 상한. 초과 시 결정적 스트라이드 축약(이상치 = 추정).</summary>
        public int NumericSampleCap { get; init; } = 65_536;
        /// <summary>이 행 수를 넘으면 중복 행 검사를 생략(메모리 정직 가드).</summary>
        public long DuplicateRowCap { get; init; } = 8_000_000;
        public double NearConstantShare { get; init; } = 0.995;
        public double MissingInfoShare { get; init; } = 0.01;
        public double MissingWarnShare { get; init; } = 0.20;
        public double MissingCriticalShare { get; init; } = 0.60;
        /// <summary>위장결측 후보 최소 출현: max(건수, 비율×비결측).</summary>
        public long SentinelMinCount { get; init; } = 10;
        public double SentinelMinShare { get; init; } = 0.005;
        /// <summary>미래 날짜 판정 기준일. 테스트 결정론을 위해 주입 가능.</summary>
        public DateTime Today { get; init; } = DateTime.Today;
    }

    /// <summary>스캔 입력: 스레드 안전한 행 접근자(전수 경로 — 뷰 상한과 무관).</summary>
    public sealed record QualityScanSource
    {
        public required IReadOnlyList<string> Headers { get; init; }
        /// <summary>데이터 행 인덱스(0-based) → 필드 배열. 여러 스레드에서 동시 호출 가능해야 한다.</summary>
        public required Func<int, string[]> RowAt { get; init; }
        public required int RowCount { get; init; }
        /// <summary>스캔 범위가 파일 전체인지(int 상한 절단 등으로 아닐 수 있음 — 정직 표기).</summary>
        public bool CoversAllRows { get; init; } = true;
        /// <summary>컬럼별 기대 타입(추론+선언+수동 오버라이드 반영). null이면 검사 생략.</summary>
        public IReadOnlyList<ColumnValueType>? ColumnTypes { get; init; }
        /// <summary>컬럼별 허용 코드 집합(SPSS 값라벨·SAS 카탈로그). 원값 모드에서만 의미.</summary>
        public IReadOnlyList<IReadOnlySet<string>?>? AllowedCodes { get; init; }
        public string SourceName { get; init; } = "";
        public long SourceBytes { get; init; }
    }

    /// <summary>스냅샷 JSON 직렬화(안정 스키마). 같은 파일 → 같은 내용(타임스탬프 필드 제외).</summary>
    public static class QualityReportJson
    {
        /// <summary>이 빌드가 비교할 수 있는 스냅샷 스키마. 필드 의미 변경 시 올린다.</summary>
        public const int SupportedSchemaVersion = 1;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
            // 이후 버전이 필드를 추가해도 이 빌드는 아는 필드만 읽는다.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        public static string Serialize(QualityReport report)
            => JsonSerializer.Serialize(report, Options);

        /// <summary>
        /// 스냅샷 JSON을 읽는다. 알 수 없는 필드는 무시한다.
        /// 스키마 버전이 <see cref="SupportedSchemaVersion"/>이 아니면
        /// <see cref="QualitySnapshotSchemaException"/>. 빈 문서·필수 필드 누락·깨진 JSON은
        /// <see cref="JsonException"/>.
        /// </summary>
        public static QualityReport Deserialize(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            if (string.IsNullOrWhiteSpace(json))
                throw new JsonException("품질 스냅샷 JSON이 비어 있습니다.");

            QualityReport? report;
            try
            {
                report = JsonSerializer.Deserialize<QualityReport>(json, Options);
            }
            catch (JsonException)
            {
                throw;
            }
            catch (NotSupportedException ex)
            {
                throw new JsonException("품질 스냅샷 JSON 형식이 올바르지 않습니다.", ex);
            }

            if (report is null)
                throw new JsonException("품질 스냅샷 JSON이 비어 있습니다.");
            if (report.Columns is null || report.Findings is null)
                throw new JsonException("품질 스냅샷에 컬럼 또는 발견 목록이 없습니다.");
            if (report.SchemaVersion != SupportedSchemaVersion)
                throw new QualitySnapshotSchemaException(report.SchemaVersion, SupportedSchemaVersion);
            return report;
        }
    }
}
