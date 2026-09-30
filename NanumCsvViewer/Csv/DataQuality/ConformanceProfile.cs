using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NanumCsvViewer.Csv.DataQuality
{
    /// <summary>
    /// 사용자 소유 적합성 프로파일(OMOP-CDM / CDISC 스타일의 제약을 표현할 수 있는 일반 스키마).
    /// 내장 의료 규칙 팩은 없다 — 컬럼명·코드·참조 파일은 사용자가 적는다.
    /// 검사는 후보 의미론이다. 선언된 제약이 아니며, 결측 토큰을 코드 위반으로 단정하지 않는다.
    /// </summary>
    public sealed record ConformanceProfile
    {
        /// <summary>0 또는 생략은 스키마 1로 읽는다. 1보다 크면 이 빌드는 거부한다.</summary>
        public int SchemaVersion { get; init; } = 1;
        public string Name { get; init; } = "";
        /// <summary>null = 기본 true. 열린 테이블의 헤더와 스펙 컬럼명을 맞출 때.</summary>
        public bool? CaseInsensitiveColumnMatch { get; init; }
        public IReadOnlyList<string> RequiredColumns { get; init; } = Array.Empty<string>();
        public IReadOnlyList<ConformanceColumnSpec> Columns { get; init; } = Array.Empty<ConformanceColumnSpec>();
        /// <summary>null = <see cref="ConformanceProfileJson.DefaultReferenceBudgetBytes"/>. 0 이하는 오류.</summary>
        public long? ReferenceMemoryBudgetBytes { get; init; }

        public bool MatchColumnsIgnoreCase => CaseInsensitiveColumnMatch ?? true;
    }

    /// <summary>컬럼 1개의 적합성 스펙. 이름 매칭은 프로파일의 대소문자 옵션을 따른다.</summary>
    public sealed record ConformanceColumnSpec
    {
        public string Column { get; init; } = "";
        /// <summary>비결측이어야 한다(빈 값·공백·널 토큰이 위반). 컬럼 자체가 없으면 테이블 수준 발견.</summary>
        public bool Required { get; init; }
        /// <summary><see cref="ColumnValueType"/> 이름. Empty는 허용하지 않는다.</summary>
        public string? DeclaredType { get; init; }
        /// <summary>저장 텍스트의 최대 문자 수(CDISC 변수 길이). 결측은 길이 위반이 아니다.</summary>
        public int? MaxLength { get; init; }
        public ConformanceCodelist? Codelist { get; init; }
        /// <summary>.NET 정규식. 결측을 건너뛴 뒤 Trim한 값에 매칭한다.</summary>
        public string? Pattern { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        /// <summary>null = 기본 true(폐구간).</summary>
        public bool? MinInclusive { get; init; }
        public bool? MaxInclusive { get; init; }
        public ConformanceConceptRef? ConceptRef { get; init; }
        public QualitySeverity Severity { get; init; } = QualitySeverity.Warning;

        public bool MinIsInclusive => MinInclusive ?? true;
        public bool MaxIsInclusive => MaxInclusive ?? true;
    }

    /// <summary>허용 코드. 인라인 값과 파일(프로파일 기준 상대 경로)을 합친다.</summary>
    public sealed record ConformanceCodelist
    {
        public IReadOnlyList<string>? Values { get; init; }
        public string? File { get; init; }
        /// <summary>CSV 헤더의 컬럼명. 있으면 헤더를 읽는다. 없으면 확장자에 따라 한 줄 한 코드 또는 첫 컬럼.</summary>
        public string? ValueColumn { get; init; }
        /// <summary>null = ValueColumn이 있으면 true, 텍스트 목록이면 false, CSV면 false(첫 행도 코드).</summary>
        public bool? HasHeader { get; init; }
        public bool CaseInsensitive { get; init; }
        public bool Trim { get; init; }
    }

    /// <summary>
    /// 값이 다른 파일의 참조 컬럼에 있어야 한다(예: CONCEPT.csv의 concept_id).
    /// domainColumn + expectedDomain이 있으면 그 도메인 행의 키만 집합에 넣는다.
    /// 빈 키는 기본으로 건너뛴다(후보 검사, 결측 단정 아님).
    /// </summary>
    public sealed record ConformanceConceptRef
    {
        public string File { get; init; } = "";
        public string KeyColumn { get; init; } = "";
        public string? DomainColumn { get; init; }
        public string? ExpectedDomain { get; init; }
        public bool CaseInsensitive { get; init; }
        public bool Trim { get; init; }
        /// <summary>null = 기본 true.</summary>
        public bool? SkipBlank { get; init; }

        public bool SkipBlankKeys => SkipBlank ?? true;
    }

    /// <summary>프로파일 형식·스키마 오류. 메시지는 어느 컬럼·필드가 문제인지 적는다.</summary>
    public class ConformanceProfileException : Exception
    {
        public ConformanceProfileException(string message) : base(message) { }
        public ConformanceProfileException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>이 빌드가 읽지 않는 스키마 버전. 부분 해석으로 검사하지 않는다.</summary>
    public sealed class ConformanceSchemaException : ConformanceProfileException
    {
        public int SchemaVersion { get; }
        public int SupportedVersion { get; }

        public ConformanceSchemaException(int schemaVersion, int supportedVersion)
            : base($"Conformance profile schema version {schemaVersion} is not supported (supported: {supportedVersion}).")
        {
            SchemaVersion = schemaVersion;
            SupportedVersion = supportedVersion;
        }
    }

    /// <summary>
    /// 참조 키·코드 집합의 예상 메모리가 예산을 넘었다. 부분 집합을 검사 결과로 반환하지 않는다
    /// (<see cref="ReferentialIntegrityBudgetException"/>과 같은 계약).
    /// </summary>
    public sealed class ConformanceBudgetException : InvalidOperationException
    {
        public string CheckLabel { get; }

        public ConformanceBudgetException(string checkLabel)
            : base($"The reference key set for '{checkLabel}' exceeds the memory budget. No partial result is returned.")
        {
            CheckLabel = checkLabel;
        }
    }

    /// <summary>프로파일 JSON. 알 수 없는 필드는 무시한다(앞 호환). 주석을 허용한다.</summary>
    public static class ConformanceProfileJson
    {
        public const int SupportedSchemaVersion = 1;
        public const long DefaultReferenceBudgetBytes = 256L * 1024 * 1024;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string Serialize(ConformanceProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            return JsonSerializer.Serialize(profile, Options);
        }

        /// <summary>
        /// 역직렬화 후 구조 검증. 파일 존재는 실행 시점에 확인한다(템플릿은 경로만 담을 수 있다).
        /// 깨진 JSON은 <see cref="JsonException"/>. 스키마·스펙 오류는 <see cref="ConformanceProfileException"/>.
        /// </summary>
        public static ConformanceProfile Deserialize(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            if (string.IsNullOrWhiteSpace(json))
                throw new JsonException("Conformance profile JSON is empty.");

            ConformanceProfile? profile;
            try
            {
                profile = JsonSerializer.Deserialize<ConformanceProfile>(json, Options);
            }
            catch (JsonException)
            {
                throw;
            }
            catch (NotSupportedException ex)
            {
                throw new JsonException("Conformance profile JSON is not valid.", ex);
            }

            if (profile is null)
                throw new JsonException("Conformance profile JSON is empty.");

            profile = profile with
            {
                Name = profile.Name ?? "",
                RequiredColumns = profile.RequiredColumns ?? Array.Empty<string>(),
                Columns = profile.Columns ?? Array.Empty<ConformanceColumnSpec>(),
            };
            Validate(profile);
            return profile;
        }

        /// <summary>구조 검증. 통과하지 못하면 검사를 시작하지 않는다.</summary>
        public static void Validate(ConformanceProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            int version = profile.SchemaVersion == 0 ? SupportedSchemaVersion : profile.SchemaVersion;
            if (version != SupportedSchemaVersion)
                throw new ConformanceSchemaException(version, SupportedSchemaVersion);
            if (profile.ReferenceMemoryBudgetBytes is long budget && budget <= 0)
                throw new ConformanceProfileException("referenceMemoryBudgetBytes must be positive.");

            var required = profile.RequiredColumns ?? Array.Empty<string>();
            for (int i = 0; i < required.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(required[i]))
                    throw new ConformanceProfileException($"requiredColumns[{i}] is empty.");
            }

            var columns = profile.Columns ?? Array.Empty<ConformanceColumnSpec>();
            for (int i = 0; i < columns.Count; i++)
            {
                var spec = columns[i] ?? throw new ConformanceProfileException($"columns[{i}] is null.");
                if (string.IsNullOrWhiteSpace(spec.Column))
                    throw new ConformanceProfileException($"columns[{i}] is missing 'column'.");
                string name = spec.Column.Trim();
                ValidateSpec(name, spec);
            }
        }

        /// <summary>
        /// 예제 템플릿. 의료 코드·도메인 값은 넣지 않는다.
        /// conceptRef는 프로파일 옆의 reference.csv를 가리킨다 — 그 컬럼이 열린 테이블에 있고 파일이 없으면 실행은 실패한다(부분 결과 없음).
        /// </summary>
        public static string ExampleJson() => """
            {
              "schemaVersion": 1,
              "name": "example-conformance",
              "caseInsensitiveColumnMatch": true,
              "requiredColumns": [ "id" ],
              "columns": [
                {
                  "column": "id",
                  "required": true,
                  "declaredType": "Identifier",
                  "maxLength": 20,
                  "severity": "Critical"
                },
                {
                  "column": "code",
                  "required": true,
                  "codelist": {
                    "values": [ "A", "B", "C" ],
                    "caseInsensitive": true,
                    "trim": true
                  }
                },
                {
                  "column": "amount",
                  "declaredType": "Float",
                  "min": 0,
                  "max": 1000
                },
                {
                  "column": "note",
                  "pattern": "^[A-Za-z0-9_ ]*$",
                  "maxLength": 40
                },
                {
                  "column": "ref_id",
                  "conceptRef": {
                    "file": "reference.csv",
                    "keyColumn": "id",
                    "domainColumn": "domain",
                    "expectedDomain": "Example",
                    "trim": true,
                    "skipBlank": true
                  }
                }
              ]
            }
            """;

        private static void ValidateSpec(string name, ConformanceColumnSpec spec)
        {
            bool any = spec.Required
                || !string.IsNullOrWhiteSpace(spec.DeclaredType)
                || spec.MaxLength is not null
                || spec.Codelist is not null
                || !string.IsNullOrWhiteSpace(spec.Pattern)
                || spec.Min is not null
                || spec.Max is not null
                || spec.ConceptRef is not null;
            if (!any)
                throw new ConformanceProfileException($"Column '{name}' has no checks.");

            if (!string.IsNullOrWhiteSpace(spec.DeclaredType) && !ConformanceChecks.TryParseType(spec.DeclaredType, out _))
            {
                string known = string.Join(", ", Enum.GetNames<ColumnValueType>().Where(n => n != nameof(ColumnValueType.Empty)));
                throw new ConformanceProfileException(
                    $"Column '{name}' declared type '{spec.DeclaredType.Trim()}' is not a known type. Expected one of: {known}.");
            }

            if (spec.MaxLength is int len && len < 0)
                throw new ConformanceProfileException($"Column '{name}' maxLength must be zero or positive.");

            if (spec.Min is double lo && spec.Max is double hi && lo > hi)
                throw new ConformanceProfileException($"Column '{name}' min ({lo}) is greater than max ({hi}).");

            if (!string.IsNullOrWhiteSpace(spec.Pattern))
            {
                try
                {
                    _ = new Regex(spec.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    throw new ConformanceProfileException(
                        $"Column '{name}' pattern is not a valid .NET regular expression: {ex.Message}");
                }
            }

            if (spec.Codelist is { } codes)
            {
                bool hasValues = codes.Values is { Count: > 0 };
                bool hasFile = !string.IsNullOrWhiteSpace(codes.File);
                if (!hasValues && !hasFile)
                    throw new ConformanceProfileException(
                        $"Column '{name}' codelist needs 'values' or 'file'.");
                if (codes.HasHeader == false && !string.IsNullOrWhiteSpace(codes.ValueColumn))
                    throw new ConformanceProfileException(
                        $"Column '{name}' codelist sets valueColumn but hasHeader is false.");
            }

            if (spec.ConceptRef is { } concept)
            {
                if (string.IsNullOrWhiteSpace(concept.File))
                    throw new ConformanceProfileException($"Column '{name}' conceptRef is missing 'file'.");
                if (string.IsNullOrWhiteSpace(concept.KeyColumn))
                    throw new ConformanceProfileException($"Column '{name}' conceptRef is missing 'keyColumn'.");
                if (!string.IsNullOrWhiteSpace(concept.ExpectedDomain) && string.IsNullOrWhiteSpace(concept.DomainColumn))
                    throw new ConformanceProfileException(
                        $"Column '{name}' conceptRef sets expectedDomain but not domainColumn.");
            }
        }
    }

    /// <summary>실행 옵션. 예산은 null이면 프로파일 값, 그것도 없으면 256 MiB.</summary>
    public sealed record ConformanceRunOptions
    {
        public string? ProfileDirectory { get; init; }
        public int MaxExamples { get; init; } = 20;
        public int DegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
        public long? ReferenceMemoryBudgetBytes { get; init; }
    }

    /// <summary>
    /// 적합성 프로파일 실행. 열린 테이블은 파티션 병렬 1-pass.
    /// 참조 키 집합은 예산 안에서만 만들고, 넘으면 예외 — 부분 결과는 없다.
    /// </summary>
    public static class ConformanceProfileRunner
    {
        public sealed record Result(
            IReadOnlyList<QualityFinding> Findings,
            long RowsScanned,
            bool ScannedFully,
            int ChecksEvaluated,
            int ChecksNotRun,
            IReadOnlyList<string> Notes);

        public static Result Run(
            ConformanceProfile profile,
            QualityScanSource source,
            ConformanceRunOptions? options = null,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(source.Headers);
            ArgumentNullException.ThrowIfNull(source.RowAt);
            ConformanceProfileJson.Validate(profile);
            ct.ThrowIfCancellationRequested();

            options ??= new ConformanceRunOptions();
            long budget = options.ReferenceMemoryBudgetBytes
                ?? profile.ReferenceMemoryBudgetBytes
                ?? ConformanceProfileJson.DefaultReferenceBudgetBytes;
            if (budget <= 0)
                throw new ConformanceProfileException("Reference memory budget must be positive.");

            int rows = Math.Max(0, source.RowCount);
            int maxExamples = Math.Max(0, options.MaxExamples);
            bool ignoreCase = profile.MatchColumnsIgnoreCase;
            var notes = new List<string>();
            var findings = new List<QualityFinding>();

            if (!source.CoversAllRows)
                notes.Add("Scan covers the open document's indexed rows only (file truncated). Counts are exact for those rows.");

            var headers = source.Headers;
            var requiredMissing = new List<string>();
            var requiredSeen = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (string raw in profile.RequiredColumns)
            {
                string name = raw.Trim();
                if (!requiredSeen.Add(name)) continue;
                var match = MatchColumn(headers, name, ignoreCase);
                if (match.Index < 0) requiredMissing.Add(name);
                else if (match.Ambiguous is not null) notes.Add(match.Ambiguous);
            }

            var specs = profile.Columns;
            var prepared = new List<PreparedSpec>(specs.Count);
            int notRun = 0;
            foreach (var spec in specs)
            {
                string name = spec.Column.Trim();
                var match = MatchColumn(headers, name, ignoreCase);
                if (match.Index < 0)
                {
                    notRun++;
                    if (spec.Required && requiredSeen.Add(name))
                        requiredMissing.Add(name);
                    else if (!spec.Required && !requiredMissing.Contains(name, requiredSeen.Comparer))
                    {
                        findings.Add(NotRunFinding(spec, name));
                    }
                    continue;
                }
                if (match.Ambiguous is not null) notes.Add(match.Ambiguous);
                prepared.Add(new PreparedSpec(spec, match.Index, match.Name, match.Ambiguous));
            }

            foreach (string name in requiredMissing)
                findings.Add(MissingColumnFinding(name, rows));

            // 컬럼이 있는 스펙만 파일을 연다. 없는 컬럼의 참조 파일 부재로 전체 실행을 막지 않는다.
            var fileErrors = new List<string>();
            var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var active = new List<ActiveCheck>();
            foreach (var prep in prepared)
            {
                try
                {
                    active.AddRange(Compile(prep, options.ProfileDirectory, budget, sets, ct, progress));
                }
                catch (ConformanceProfileException ex) when (ex is not ConformanceSchemaException)
                {
                    fileErrors.Add(ex.Message);
                }
            }
            if (fileErrors.Count > 0)
                throw new ConformanceProfileException(string.Join(Environment.NewLine, fileErrors));

            progress?.Report(active.Count == 0 ? 100 : 40);

            if (rows > 0 && active.Count > 0)
                Scan(source, rows, active, options.DegreeOfParallelism, maxExamples, progress, ct);

            foreach (var check in active)
                findings.Add(ToFinding(check, rows));

            findings.Sort(static (a, b) =>
            {
                int c = a.Column.CompareTo(b.Column);
                if (c != 0) return c;
                c = a.Kind.CompareTo(b.Kind);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Label, b.Label);
            });

            progress?.Report(100);
            return new Result(findings, rows, source.CoversAllRows, active.Count, notRun, notes);
        }

        /// <summary>상대 경로는 프로파일 파일이 있는 폴더 기준. 절대 경로는 그대로.</summary>
        public static string ResolvePath(string file, string? profileDirectory)
        {
            if (string.IsNullOrWhiteSpace(file))
                throw new ConformanceProfileException("A file path is empty.");
            string trimmed = file.Trim();
            if (Path.IsPathRooted(trimmed)) return Path.GetFullPath(trimmed);
            if (string.IsNullOrEmpty(profileDirectory))
                throw new ConformanceProfileException(
                    $"Relative path '{trimmed}' needs the profile directory. Load the profile from a file, or use an absolute path.");
            return Path.GetFullPath(Path.Combine(profileDirectory, trimmed));
        }

        private static IEnumerable<ActiveCheck> Compile(
            PreparedSpec prep, string? profileDirectory, long budget,
            Dictionary<string, HashSet<string>> sets,
            CancellationToken ct, IProgress<int>? progress)
        {
            var spec = prep.Spec;
            string colName = prep.HeaderName;
            string? extra = prep.Ambiguous;

            if (spec.Required)
            {
                yield return CellCheck(spec, prep.Index, colName, QualityCheckKind.ConformanceRequired,
                    QualityDimension.Completeness, "required",
                    raw => ConformanceChecks.IsMissing(raw), extra);
            }

            if (!string.IsNullOrWhiteSpace(spec.DeclaredType) && ConformanceChecks.TryParseType(spec.DeclaredType, out var type))
            {
                var checker = ConformanceChecks.CheckerFor(type);
                string note = ConformanceChecks.TypeAcceptsAnyText(type)
                    ? "declared type accepts any non-missing text — type check cannot fail"
                    : "";
                yield return CellCheck(spec, prep.Index, colName, QualityCheckKind.ConformanceType,
                    QualityDimension.Conformance, type.ToString(),
                    raw =>
                    {
                        string v = raw.Trim();
                        return !ConformanceChecks.IsMissing(v) && ConformanceChecks.ViolatesType(checker, v);
                    },
                    JoinNotes(extra, note));
            }

            if (spec.MaxLength is int maxLen)
            {
                yield return CellCheck(spec, prep.Index, colName, QualityCheckKind.ConformanceMaxLength,
                    QualityDimension.Conformance, $"len<={maxLen.ToString(CultureInfo.InvariantCulture)}",
                    raw => !ConformanceChecks.IsMissing(raw) && raw.Length > maxLen, extra);
            }

            if (spec.Codelist is { } codes)
            {
                var set = LoadCodelist(spec.Column.Trim(), codes, profileDirectory, budget, ct, progress);
                bool trim = codes.Trim;
                yield return CellCheck(spec, prep.Index, colName, QualityCheckKind.ConformanceCodelist,
                    QualityDimension.Conformance, CodelistLabel(codes),
                    raw =>
                    {
                        if (ConformanceChecks.IsMissing(raw)) return false;
                        string key = trim ? raw.Trim() : raw;
                        return !set.Contains(key);
                    },
                    JoinNotes(extra, $"{set.Count.ToString(CultureInfo.InvariantCulture)} codes"));
            }

            if (!string.IsNullOrWhiteSpace(spec.Pattern))
            {
                var regex = new Regex(spec.Pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
                string label = spec.Pattern.Length <= 60 ? spec.Pattern : spec.Pattern[..60] + "…";
                string column = spec.Column.Trim();
                yield return CellCheck(spec, prep.Index, colName, QualityCheckKind.ConformancePattern,
                    QualityDimension.Conformance, label,
                    raw =>
                    {
                        string v = raw.Trim();
                        if (ConformanceChecks.IsMissing(v)) return false;
                        try { return !regex.IsMatch(v); }
                        catch (RegexMatchTimeoutException)
                        {
                            throw new ConformanceProfileException(
                                $"Column '{column}' pattern timed out. No partial result is returned.");
                        }
                    }, extra);
            }

            if (spec.Min is not null || spec.Max is not null)
            {
                double? min = spec.Min;
                double? max = spec.Max;
                bool minInc = spec.MinIsInclusive;
                bool maxInc = spec.MaxIsInclusive;
                yield return CellCheck(spec, prep.Index, colName, QualityCheckKind.ConformanceRange,
                    QualityDimension.Plausibility, FormatRange(min, max, minInc, maxInc),
                    raw =>
                    {
                        string v = raw.Trim();
                        if (ConformanceChecks.IsMissing(v)) return false;
                        if (!NumericAffix.TryParseNumber(v, out double d) || !double.IsFinite(d)) return true;
                        if (min is double lo && (minInc ? d < lo : d <= lo)) return true;
                        if (max is double hi && (maxInc ? d > hi : d >= hi)) return true;
                        return false;
                    }, extra);
            }

            if (spec.ConceptRef is { } concept)
            {
                var set = LoadConceptSet(spec.Column.Trim(), concept, profileDirectory, budget, sets, ct, progress);
                bool trim = concept.Trim;
                bool skipBlank = concept.SkipBlankKeys;
                string label = ConceptLabel(concept);
                string note = JoinNotes(extra, $"{set.Count.ToString(CultureInfo.InvariantCulture)} reference keys");
                yield return CellCheck(spec, prep.Index, colName, QualityCheckKind.ConformanceConcept,
                    QualityDimension.Conformance, label,
                    raw =>
                    {
                        if (skipBlank && ConformanceChecks.IsMissing(raw)) return false;
                        string key = trim ? raw.Trim() : raw;
                        return !set.Contains(key);
                    }, note, skipBlank);
            }
        }

        private static ActiveCheck CellCheck(
            ConformanceColumnSpec spec, int column, string columnName,
            QualityCheckKind kind, QualityDimension dimension, string label,
            Func<string, bool> violates, string? note, bool skipMissing = true)
        {
            return new ActiveCheck
            {
                Kind = kind,
                Dimension = dimension,
                Severity = spec.Severity,
                Column = column,
                ColumnName = columnName,
                Label = label,
                Violates = violates,
                Note = note ?? "",
                SkipMissing = kind == QualityCheckKind.ConformanceRequired ? false : skipMissing,
            };
        }

        private static void Scan(
            QualityScanSource source, int rows, List<ActiveCheck> checks, int degree,
            int maxExamples, IProgress<int>? progress, CancellationToken ct)
        {
            int dop = Math.Clamp(degree, 1, Math.Max(1, rows / 4096 + 1));
            int n = checks.Count;
            var partCounts = new long[dop][];
            var partSkipped = new long[dop][];
            var partExamples = new List<QualityExample>[dop][];
            long processed = 0;
            int lastPercent = -1;

            var tasks = new Task[dop];
            for (int p = 0; p < dop; p++)
            {
                int pi = p;
                int lo = (int)((long)rows * pi / dop);
                int hi = (int)((long)rows * (pi + 1) / dop);
                tasks[pi] = Task.Run(() =>
                {
                    var counts = new long[n];
                    var skipped = new long[n];
                    var examples = new List<QualityExample>[n];
                    for (int j = 0; j < n; j++)
                        examples[j] = new List<QualityExample>(Math.Min(maxExamples, 32));
                    var rowAt = source.RowAt;
                    for (int i = lo; i < hi; i++)
                    {
                        if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                        string[] row = rowAt(i);
                        long sourceRow = i + 1L;
                        for (int j = 0; j < n; j++)
                        {
                            var check = checks[j];
                            string raw = Cell(row, check.Column);
                            if (check.SkipMissing && ConformanceChecks.IsMissing(raw))
                            {
                                skipped[j]++;
                                continue;
                            }
                            if (!check.Violates(raw)) continue;
                            counts[j]++;
                            if (maxExamples > 0 && examples[j].Count < maxExamples)
                                examples[j].Add(new QualityExample(sourceRow, Clip(raw)));
                        }
                        if ((i & 0x1FFF) == 0x1FFF && progress is not null)
                            Report(Interlocked.Add(ref processed, 8192), rows, 40, 99, ref lastPercent, progress);
                    }
                    partCounts[pi] = counts;
                    partSkipped[pi] = skipped;
                    partExamples[pi] = examples;
                }, ct);
            }
            Wait(tasks, ct);

            for (int j = 0; j < n; j++)
            {
                long count = 0, skip = 0;
                var examples = new List<QualityExample>();
                for (int p = 0; p < dop; p++)
                {
                    count += partCounts[p][j];
                    skip += partSkipped[p][j];
                    examples.AddRange(partExamples[p][j]);
                }
                checks[j].ViolationCount = count;
                checks[j].SkippedRows = skip;
                checks[j].Examples = examples
                    .OrderBy(e => e.SourceRow)
                    .ThenBy(e => e.Value, StringComparer.Ordinal)
                    .Take(maxExamples)
                    .ToArray();
            }
        }

        // 결측을 위반으로 세는 검사(필수 값)는 빈 값을 건너뛰지 않는다. 그 외는 결측을 위반으로 단정하지 않는다.
        private static QualityFinding ToFinding(ActiveCheck check, int rows)
        {
            bool failed = check.ViolationCount > 0;
            var breakdown = new List<ValueCount>();
            if (check.Note.Length > 0)
                breakdown.Add(new ValueCount(check.Note, check.ViolationCount));
            int col = check.Column;
            var violates = check.Violates;
            return new QualityFinding
            {
                Kind = check.Kind,
                Dimension = check.Dimension,
                Severity = failed ? check.Severity : QualitySeverity.Info,
                Column = col,
                ColumnName = check.ColumnName,
                Label = check.Label,
                ViolationCount = check.ViolationCount,
                EvaluatedRows = rows,
                SkippedRows = check.SkippedRows,
                Examples = failed ? check.Examples : Array.Empty<QualityExample>(),
                Breakdown = breakdown,
                ViolationPredicate = failed ? row => violates(Cell(row, col)) : null,
            };
        }

        private static QualityFinding MissingColumnFinding(string name, int rows) => new()
        {
            Kind = QualityCheckKind.ConformanceRequired,
            Dimension = QualityDimension.Completeness,
            Severity = QualitySeverity.Critical,
            Column = -1,
            ColumnName = name,
            Label = "column absent",
            // 행이 0이어도 부재는 보여야 한다. 건수는 스캔한 행 수(없으면 1) — 행 술어는 없다.
            ViolationCount = Math.Max(1, rows),
            EvaluatedRows = rows,
            Breakdown = new[] { new ValueCount("required column is not in this table", 1) },
        };

        private static QualityFinding NotRunFinding(ConformanceColumnSpec spec, string name) => new()
        {
            Kind = ScopeKind(spec),
            Dimension = QualityDimension.Conformance,
            Severity = QualitySeverity.Info,
            Column = -1,
            ColumnName = name,
            Label = "not in table",
            ViolationCount = 0,
            Breakdown = new[] { new ValueCount("column not in this table — checks not run", 0) },
        };

        private static QualityCheckKind ScopeKind(ConformanceColumnSpec spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.DeclaredType)) return QualityCheckKind.ConformanceType;
            if (spec.MaxLength is not null) return QualityCheckKind.ConformanceMaxLength;
            if (spec.Codelist is not null) return QualityCheckKind.ConformanceCodelist;
            if (!string.IsNullOrWhiteSpace(spec.Pattern)) return QualityCheckKind.ConformancePattern;
            if (spec.Min is not null || spec.Max is not null) return QualityCheckKind.ConformanceRange;
            if (spec.ConceptRef is not null) return QualityCheckKind.ConformanceConcept;
            return QualityCheckKind.ConformanceRequired;
        }

        private static HashSet<string> LoadCodelist(
            string column, ConformanceCodelist codes, string? profileDirectory, long budget,
            CancellationToken ct, IProgress<int>? progress)
        {
            var comparer = codes.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var set = new HashSet<string>(comparer);
            long estimated = 0;
            if (codes.Values is not null)
            {
                foreach (string raw in codes.Values)
                {
                    if (raw is null) continue;
                    AddKey(set, ref estimated, budget, codes.Trim ? raw.Trim() : raw, $"codelist:{column}");
                }
            }
            if (!string.IsNullOrWhiteSpace(codes.File))
            {
                string path = ResolvePath(codes.File, profileDirectory);
                if (!File.Exists(path))
                    throw new ConformanceProfileException(
                        $"Codelist file for column '{column}' was not found: {codes.File.Trim()} (resolved: {path}).");
                bool header = codes.HasHeader ?? !string.IsNullOrWhiteSpace(codes.ValueColumn);
                ReadCodeFile(path, header, codes.ValueColumn, codes.Trim, set, ref estimated, budget, $"codelist:{column}", ct, progress);
            }
            return set;
        }

        /// <summary>
        /// 도메인 필터에 실제로 비교하는 문자열. trim이 꺼져 있으면 공백을 유지한다.
        /// 공백만 있으면 필터 없음(null).
        /// </summary>
        private static string? EffectiveExpectedDomain(ConformanceConceptRef concept)
        {
            if (string.IsNullOrWhiteSpace(concept.ExpectedDomain)) return null;
            return concept.Trim ? concept.ExpectedDomain.Trim() : concept.ExpectedDomain;
        }
        private static HashSet<string> LoadConceptSet(
            string column, ConformanceConceptRef concept, string? profileDirectory, long budget,
            Dictionary<string, HashSet<string>> cache, CancellationToken ct, IProgress<int>? progress)
        {
            string path = ResolvePath(concept.File, profileDirectory);
            // 비교에 쓰는 도메인 값으로 키를 만든다. trim=false면 "D"와 " D "는 다른 집합이다.
            // 무조건 Trim하면 두 검사가 한 캐시를 나눠 갖고 맞는 키를 거부한다.
            string? expected = EffectiveExpectedDomain(concept);
            string cacheKey = string.Join('\n',
                path,
                concept.KeyColumn.Trim(),
                concept.DomainColumn?.Trim() ?? "",
                expected ?? "",
                concept.CaseInsensitive,
                concept.Trim);
            if (cache.TryGetValue(cacheKey, out var cached)) return cached;

            if (!File.Exists(path))
                throw new ConformanceProfileException(
                    $"Concept reference file for column '{column}' was not found: {concept.File.Trim()} (resolved: {path}).");

            var comparer = concept.CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var set = new HashSet<string>(comparer);
            long estimated = 0;
            string label = $"concept:{column}";
            bool filterDomain = expected is not null;

            using var reader = OpenReader(path);
            using var records = ReadRecords(reader, ct).GetEnumerator();
            if (!records.MoveNext())
                throw new ConformanceProfileException(
                    $"Concept reference file for column '{column}' has no header row: {path}.");

            char delim = DetectDelimiter(path, records.Current);
            string[] header = ParseFields(records.Current, delim);
            int keyCol = FindHeader(header, concept.KeyColumn, path, column, "keyColumn");
            int domainCol = -1;
            if (!string.IsNullOrWhiteSpace(concept.DomainColumn))
                domainCol = FindHeader(header, concept.DomainColumn, path, column, "domainColumn");
            if (filterDomain && domainCol < 0)
                throw new ConformanceProfileException(
                    $"Column '{column}' conceptRef sets expectedDomain but the reference file has no domain column.");

            while (records.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                string[] fields = ParseFields(records.Current, delim);
                string keyRaw = Field(fields, keyCol);
                if (ConformanceChecks.IsMissing(keyRaw)) continue;
                if (filterDomain)
                {
                    string domainRaw = Field(fields, domainCol);
                    string domain = concept.Trim ? domainRaw.Trim() : domainRaw;
                    if (!comparer.Equals(domain, expected)) continue;
                }
                string key = concept.Trim ? keyRaw.Trim() : keyRaw;
                AddKey(set, ref estimated, budget, key, label);
            }

            cache[cacheKey] = set;
            progress?.Report(30);
            return set;
        }

        private static void ReadCodeFile(
            string path, bool hasHeader, string? valueColumn, bool trim,
            HashSet<string> set, ref long estimated, long budget, string label,
            CancellationToken ct, IProgress<int>? progress)
        {
            bool text = IsTextList(path) && string.IsNullOrWhiteSpace(valueColumn);
            using var reader = OpenReader(path);
            if (text)
            {
                string? line;
                int n = 0;
                bool first = true;
                while ((line = reader.ReadLine()) is not null)
                {
                    if ((n++ & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                    if (first && hasHeader) { first = false; continue; }
                    first = false;
                    if (line.Length == 0 || line[0] == '#') continue;
                    AddKey(set, ref estimated, budget, trim ? line.Trim() : line, label);
                }
                progress?.Report(30);
                return;
            }

            using var records = ReadRecords(reader, ct).GetEnumerator();
            if (!records.MoveNext())
            {
                if (hasHeader)
                    throw new ConformanceProfileException($"Codelist file has no header row: {path}.");
                return;
            }
            char delim = DetectDelimiter(path, records.Current);
            int valueCol = 0;
            if (hasHeader)
            {
                string[] header = ParseFields(records.Current, delim);
                if (!string.IsNullOrWhiteSpace(valueColumn))
                    valueCol = FindHeader(header, valueColumn, path, label, "valueColumn");
            }
            else
            {
                string key = Field(ParseFields(records.Current, delim), 0);
                if (key.Length > 0 && key[0] != '#')
                    AddKey(set, ref estimated, budget, trim ? key.Trim() : key, label);
            }
            while (records.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                string key = Field(ParseFields(records.Current, delim), valueCol);
                if (key.Length == 0 || key[0] == '#') continue;
                AddKey(set, ref estimated, budget, trim ? key.Trim() : key, label);
            }
            progress?.Report(30);
        }

        private static void AddKey(HashSet<string> set, ref long estimated, long budget, string key, string label)
        {
            if (key.Length == 0) return;
            if (set.Contains(key)) return;
            long add = EstimateKeyBytes(key);
            if (estimated < 0 || add < 0 || estimated + add < estimated || estimated + add > budget)
                throw new ConformanceBudgetException(label);
            set.Add(key);
            estimated += add;
        }

        // 참조 무결성 스캐너와 같은 보수적 추정. 삽입 전에 검사하고, 넘으면 넣지 않는다.
        private static long EstimateKeyBytes(string key) => 512L + 64L + 32L + 2L * key.Length;

        private static StreamReader OpenReader(string path)
        {
            try
            {
                var det = EncodingDetector.Detect(path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
                return new StreamReader(stream, det.Encoding, detectEncodingFromByteOrderMarks: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ConformanceProfileException($"Could not read '{path}': {ex.Message}", ex);
            }
        }

        private static IEnumerable<string> ReadRecords(TextReader reader, CancellationToken ct)
        {
            var sb = new StringBuilder();
            bool inside = false;
            int n = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if ((n++ & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                inside = EndsInsideQuotes(line, inside);
                if (!inside)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
            if (sb.Length > 0)
                yield return sb.ToString();
        }

        private static bool EndsInsideQuotes(string line, bool inside)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] != '"') continue;
                if (inside)
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
                    inside = false;
                }
                else inside = true;
            }
            return inside;
        }

        private static bool IsTextList(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".txt" or ".lst" or ".codes";
        }

        private static char DetectDelimiter(string path, string headerLine)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".tsv" or ".tab") return '\t';
            int commas = 0, tabs = 0;
            foreach (char c in headerLine)
            {
                if (c == ',') commas++;
                else if (c == '\t') tabs++;
            }
            return tabs > commas ? '\t' : ',';
        }

        private static string[] ParseFields(string record, char delimiter)
            => delimiter == '\0' ? new[] { record } : CsvRowParser.Parse(record, delimiter);

        private static int FindHeader(string[] header, string wanted, string path, string column, string field)
        {
            string name = wanted.Trim();
            int found = -1;
            for (int i = 0; i < header.Length; i++)
            {
                if (!string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                found = i;
                break;
            }
            if (found >= 0) return found;
            string shown = string.Join(", ", header.Take(20).Select(h => h.Trim()));
            throw new ConformanceProfileException(
                $"Column '{column}' {field} '{name}' was not found in '{path}'. Header: {shown}.");
        }

        private static string Field(string[] fields, int index)
            => index >= 0 && index < fields.Length && fields[index] is not null ? fields[index] : "";

        private readonly record struct ColumnMatch(int Index, string Name, string? Ambiguous);

        private static ColumnMatch MatchColumn(IReadOnlyList<string> headers, string name, bool ignoreCase)
        {
            var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            int found = -1;
            string? second = null;
            for (int i = 0; i < headers.Count; i++)
            {
                if (!string.Equals(headers[i]?.Trim(), name, cmp)) continue;
                if (found < 0) found = i;
                else { second = headers[i]; break; }
            }
            if (found < 0) return new ColumnMatch(-1, name, null);
            string used = headers[found] ?? name;
            string? note = second is null ? null
                : $"Column name '{name}' matched '{used}' and also '{second}'. The first match was used.";
            return new ColumnMatch(found, used, note);
        }

        private static string Cell(string[]? row, int col)
            => row is not null && col >= 0 && col < row.Length && row[col] is not null ? row[col] : "";

        private static string Clip(string raw)
        {
            string v = raw.Replace('\r', ' ').Replace('\n', ' ');
            return v.Length <= 120 ? v : v[..120] + "…";
        }

        private static string CodelistLabel(ConformanceCodelist codes)
            => string.IsNullOrWhiteSpace(codes.File) ? "inline" : Path.GetFileName(codes.File.Trim());

        private static string ConceptLabel(ConformanceConceptRef concept)
        {
            string file = Path.GetFileName(concept.File.Trim());
            string label = $"{concept.KeyColumn.Trim()}@{file}";
            if (!string.IsNullOrWhiteSpace(concept.ExpectedDomain))
                label += $" domain={concept.ExpectedDomain.Trim()}";
            return label;
        }

        private static string FormatRange(double? min, double? max, bool minInc, bool maxInc)
        {
            static string N(double d) => d.ToString("G", CultureInfo.InvariantCulture);
            if (min is double lo && max is double hi)
                return $"{(minInc ? '[' : '(')}{N(lo)}, {N(hi)}{(maxInc ? ']' : ')')}";
            if (min is double onlyLo) return $"{(minInc ? "≥" : ">")}{N(onlyLo)}";
            if (max is double onlyHi) return $"{(maxInc ? "≤" : "<")}{N(onlyHi)}";
            return "range";
        }

        private static string JoinNotes(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + " · " + b;
        }

        private static void Wait(Task[] tasks, CancellationToken ct)
        {
            try { QualityProfiler.WaitAllDraining(tasks, ct); }
            catch (AggregateException ae)
            {
                foreach (var inner in ae.Flatten().InnerExceptions)
                {
                    if (inner is OperationCanceledException or ConformanceProfileException or ConformanceBudgetException)
                        throw inner;
                }
                throw;
            }
        }

        private static void Report(long done, long total, int floor, int ceil, ref int lastPercent, IProgress<int> progress)
        {
            if (total <= 0) return;
            int pct = floor + (int)Math.Min(ceil - floor, done * (ceil - floor) / total);
            if (pct > Volatile.Read(ref lastPercent))
            {
                Volatile.Write(ref lastPercent, pct);
                progress.Report(pct);
            }
        }

        private sealed class ActiveCheck
        {
            public QualityCheckKind Kind { get; init; }
            public QualityDimension Dimension { get; init; }
            public QualitySeverity Severity { get; init; }
            public int Column { get; init; }
            public string ColumnName { get; init; } = "";
            public string Label { get; init; } = "";
            public Func<string, bool> Violates { get; init; } = null!;
            public string Note { get; init; } = "";
            /// <summary>true면 결측은 위반이 아니다. 필수 값 검사와 skipBlank=false 개념 참조만 false.</summary>
            public bool SkipMissing { get; init; } = true;
            public long ViolationCount { get; set; }
            public long SkippedRows { get; set; }
            public QualityExample[] Examples { get; set; } = Array.Empty<QualityExample>();
        }

        private readonly record struct PreparedSpec(
            ConformanceColumnSpec Spec, int Index, string HeaderName, string? Ambiguous);
    }

    /// <summary>프로파일러의 타입 판정과 같은 규칙. 칩 술어와 계수가 갈라지지 않게 한곳에 둔다.</summary>
    internal static class ConformanceChecks
    {
        internal enum Checker { None, Numeric, Integer, TemporalDate, TemporalTime, Boolean }

        internal static bool TryParseType(string text, out ColumnValueType type)
        {
            type = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!Enum.TryParse(text.Trim(), ignoreCase: true, out type)) return false;
            return Enum.IsDefined(type) && type != ColumnValueType.Empty;
        }

        internal static Checker CheckerFor(ColumnValueType t) => t switch
        {
            ColumnValueType.Integer => Checker.Integer,
            ColumnValueType.Float or ColumnValueType.Currency
                or ColumnValueType.Percent or ColumnValueType.Scientific => Checker.Numeric,
            ColumnValueType.Date or ColumnValueType.DateTime => Checker.TemporalDate,
            ColumnValueType.Time => Checker.TemporalTime,
            ColumnValueType.Boolean => Checker.Boolean,
            _ => Checker.None,
        };

        internal static bool TypeAcceptsAnyText(ColumnValueType t) => t is
            ColumnValueType.String or ColumnValueType.Categorical
            or ColumnValueType.Ordinal or ColumnValueType.Identifier;

        internal static bool IsMissing(string? raw)
        {
            if (raw is null) return true;
            string t = raw.Trim();
            return t.Length == 0 || ColumnStatisticsBuilder.IsNullToken(t);
        }

        /// <summary>QualityProfiler.ViolatesType과 동일. 비유한 값(NaN/Infinity)은 수치·정수 위반.</summary>
        internal static bool ViolatesType(Checker checker, string v) => checker switch
        {
            Checker.Numeric => !(NumericAffix.TryParseNumber(v, out double dn) && double.IsFinite(dn)),
            Checker.Integer => !(double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
                                 && double.IsFinite(d) && Math.Truncate(d) == d && !v.Contains('.')
                                 && v.IndexOf('e', StringComparison.OrdinalIgnoreCase) < 0),
            Checker.TemporalDate => CsvDateParser.ParseDetailed(v, true) is not { } t || t.Kind == TemporalKind.Time,
            Checker.TemporalTime => CsvDateParser.ParseDetailed(v, true) is not { } tt || tt.Kind != TemporalKind.Time,
            Checker.Boolean => !ColumnStatisticsBuilder.IsBooleanToken(v),
            _ => false,
        };
    }
}
