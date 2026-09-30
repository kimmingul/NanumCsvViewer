using System.Text.Json;

namespace NanumCsvViewer.Csv.DataQuality
{
    /// <summary>
    /// 품질 스냅샷 스키마가 이 빌드가 비교할 수 있는 버전이 아닐 때.
    /// 알 수 없는 JSON 필드와는 별개다 — 필드는 무시하고, 버전만 거부한다.
    /// </summary>
    public sealed class QualitySnapshotSchemaException : Exception
    {
        public int SchemaVersion { get; }
        public int SupportedVersion { get; }

        public QualitySnapshotSchemaException(int schemaVersion, int supportedVersion)
            : base($"지원하지 않는 품질 스냅샷 스키마 버전입니다: {schemaVersion} (이 버전은 {supportedVersion}만 비교할 수 있습니다).")
        {
            SchemaVersion = schemaVersion;
            SupportedVersion = supportedVersion;
        }
    }

    /// <summary>스냅샷 diff 항목 종류. 표시 순서·현지화 키. 행 술어가 없으므로 발견 목록에 넣지 않는다.</summary>
    public enum QualitySnapshotDiffKind
    {
        RowCountChanged,
        ColumnAdded,
        ColumnRemoved,
        TypeChanged,
        MissingRateChanged,
        UniqueCountChanged,
        NumericMinChanged,
        NumericMaxChanged,
        NumericMeanChanged,
        SentinelAppeared,      // 위장결측 후보 — 단정이 아니라 후보
        SentinelDisappeared,
        FindingNew,
        FindingResolved,
        FindingPersisting,
        CheckNotComparable,    // 한쪽이 검사를 생략해 신규/해소로 단정할 수 없음
        FindingNotRechecked,   // 사용자 실행 검사(규칙·키·참조)의 기준선 발견이 현재 세션에서 재실행되지 않음 — 해소 아님
    }

    /// <summary>diff 임계. 결측률은 퍼센트포인트, 고유값은 변화율, 수치는 상대 변화.</summary>
    public sealed record QualitySnapshotDiffOptions
    {
        /// <summary>결측률 변화(퍼센트포인트)가 이 값을 <b>초과</b>할 때만 항목화. 같으면 침묵.</summary>
        public double MissingRateDeltaPoints { get; init; } = 1.0;

        /// <summary>고유값 개수 변화율 |Δ|/기준 이 값을 초과할 때만. 기준이 0이고 현재가 0이 아니면 항상 항목화.</summary>
        public double UniqueCountChangeRatio { get; init; } = 0.10;

        /// <summary>min/max/mean 상대 변화 |Δ|/scale 이 값을 초과할 때만. 0이면 어떤 차이든 항목화.</summary>
        public double NumericRelativeThreshold { get; init; } = 0.0;

        public static QualitySnapshotDiffOptions Default { get; } = new();
    }

    /// <summary>
    /// diff 1건. 수치는 문화 중립(비율·건수·절대차). 메시지 문자열이 아니라 구조 필드로 남긴다.
    /// Approximate는 부분 스캔·고유값 하한 등으로 이 비교를 단정할 수 없음을 뜻한다.
    /// </summary>
    public sealed record QualitySnapshotDiffItem
    {
        public required QualitySnapshotDiffKind Kind { get; init; }
        public required QualitySeverity Severity { get; init; }
        public string ColumnName { get; init; } = "";
        public int BaselineColumnIndex { get; init; } = -1;
        public int CurrentColumnIndex { get; init; } = -1;
        public QualityCheckKind? CheckKind { get; init; }
        /// <summary>규칙명(Kind=Rule일 때의 Label). 그 외 검사는 비움 — 건수·예시 문구로 짝짓지 않는다.</summary>
        public string RuleName { get; init; } = "";
        public bool Approximate { get; init; }
        public string? BaselineText { get; init; }
        public string? CurrentText { get; init; }
        public double? BaselineNumber { get; init; }
        public double? CurrentNumber { get; init; }
        /// <summary>종류별 변화량: 행수·수치=절대차, 결측률=퍼센트포인트, 고유값=변화율(기준 0이면 null).</summary>
        public double? Delta { get; init; }
    }

    /// <summary>
    /// 기준선 스냅샷과 현재 프로파일의 차이. Comparable=false면 한쪽이라도 부분 스캔이라
    /// 수치 비교는 근사·비교불가다(항목은 그대로 두되 Approximate로 정직하게 표기).
    /// </summary>
    public sealed record QualitySnapshotDiffResult
    {
        public int SchemaVersion { get; init; } = 1;
        public required bool BaselineScannedFully { get; init; }
        public required bool CurrentScannedFully { get; init; }
        /// <summary>양쪽 모두 전수일 때만 true. 아니면 비교는 근사로 라벨한다.</summary>
        public bool Comparable { get; init; }
        public required long BaselineRows { get; init; }
        public required long CurrentRows { get; init; }
        public long RowCountDelta => CurrentRows - BaselineRows;
        public string BaselineSourceName { get; init; } = "";
        public string CurrentSourceName { get; init; } = "";
        public string BaselineTimestamp { get; init; } = "";
        public string CurrentTimestamp { get; init; } = "";
        public bool BaselineDuplicateCheckSkipped { get; init; }
        public bool CurrentDuplicateCheckSkipped { get; init; }
        /// <summary>공통 컬럼 중 고유값 추적이 상한에 걸린 쪽이 있는지(하한이라 변화율이 과소일 수 있음).</summary>
        public bool DistinctCountsCapped { get; init; }
        public double MissingRateDeltaPoints { get; init; }
        public double UniqueCountChangeRatio { get; init; }
        public double NumericRelativeThreshold { get; init; }
        public required IReadOnlyList<QualitySnapshotDiffItem> Items { get; init; }
    }

    /// <summary>
    /// 품질 보고서 JSON 스냅샷과 현재 <see cref="QualityReport"/>의 결정적 diff.
    /// 컬럼은 헤더 이름으로 짝짓고, 위치만 바뀐 개명은 삭제+추가로 정직하게 보고한다.
    /// 발견은 검사 종류 + 컬럼명 + 규칙명으로 짝짓는다(메시지·건수·예시로 짝짓지 않음).
    /// </summary>
    public static class QualitySnapshotDiff
    {
        public static QualitySnapshotDiffResult Compare(QualityReport baseline, QualityReport current,
            QualitySnapshotDiffOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(baseline);
            ArgumentNullException.ThrowIfNull(current);
            options ??= QualitySnapshotDiffOptions.Default;

            bool partial = !baseline.ScannedFully || !current.ScannedFully;
            var items = new List<QualitySnapshotDiffItem>();

            if (baseline.RowsScanned != current.RowsScanned)
            {
                items.Add(new QualitySnapshotDiffItem
                {
                    Kind = QualitySnapshotDiffKind.RowCountChanged,
                    Severity = QualitySeverity.Info,
                    Approximate = partial,
                    BaselineNumber = baseline.RowsScanned,
                    CurrentNumber = current.RowsScanned,
                    Delta = current.RowsScanned - baseline.RowsScanned,
                });
            }

            var baseCols = GroupColumns(baseline.Columns);
            var curCols = GroupColumns(current.Columns);
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var n in baseCols.Keys) names.Add(n);
            foreach (var n in curCols.Keys) names.Add(n);

            var paired = new List<(QualityColumnProfile Baseline, QualityColumnProfile Current)>();
            bool distinctCapped = false;
            foreach (string name in names)
            {
                baseCols.TryGetValue(name, out var bs);
                curCols.TryGetValue(name, out var cs);
                bs ??= new List<QualityColumnProfile>();
                cs ??= new List<QualityColumnProfile>();
                int n = Math.Min(bs.Count, cs.Count);
                for (int i = 0; i < n; i++)
                    paired.Add((bs[i], cs[i]));
                for (int i = n; i < bs.Count; i++)
                    items.Add(ColumnItem(QualitySnapshotDiffKind.ColumnRemoved, QualitySeverity.Warning,
                        bs[i], current: null, partial));
                for (int i = n; i < cs.Count; i++)
                    items.Add(ColumnItem(QualitySnapshotDiffKind.ColumnAdded, QualitySeverity.Info,
                        baseline: null, cs[i], partial));
            }

            var baseSentinels = SentinelsByColumn(baseline);
            var curSentinels = SentinelsByColumn(current);
            var pairedNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (b, c) in paired)
            {
                pairedNames.Add(b.Name);
                if (b.DistinctIsLowerBound || c.DistinctIsLowerBound) distinctCapped = true;
                CompareColumn(b, c, baseline.RowsScanned, current.RowsScanned, options, partial, items);
            }
            CompareSentinels(pairedNames, baseSentinels, curSentinels, partial, items);
            CompareFindings(baseline, current, partial, items);

            items.Sort(CompareItems);

            return new QualitySnapshotDiffResult
            {
                BaselineScannedFully = baseline.ScannedFully,
                CurrentScannedFully = current.ScannedFully,
                Comparable = !partial,
                BaselineRows = baseline.RowsScanned,
                CurrentRows = current.RowsScanned,
                BaselineSourceName = baseline.SourceName,
                CurrentSourceName = current.SourceName,
                BaselineTimestamp = baseline.ScanTimestamp,
                CurrentTimestamp = current.ScanTimestamp,
                BaselineDuplicateCheckSkipped = baseline.DuplicateRowCheckSkipped,
                CurrentDuplicateCheckSkipped = current.DuplicateRowCheckSkipped,
                DistinctCountsCapped = distinctCapped,
                MissingRateDeltaPoints = options.MissingRateDeltaPoints,
                UniqueCountChangeRatio = options.UniqueCountChangeRatio,
                NumericRelativeThreshold = options.NumericRelativeThreshold,
                Items = items,
            };
        }

        /// <summary>diff JSON(안정 스키마, 문화 중립). 보고서 스냅샷과는 다른 문서다.</summary>
        public static string Serialize(QualitySnapshotDiffResult diff)
        {
            ArgumentNullException.ThrowIfNull(diff);
            return JsonSerializer.Serialize(diff, JsonOptions);
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // ---------------------------------------------------------------- 컬럼

        private static Dictionary<string, List<QualityColumnProfile>> GroupColumns(
            IReadOnlyList<QualityColumnProfile>? columns)
        {
            var map = new Dictionary<string, List<QualityColumnProfile>>(StringComparer.Ordinal);
            if (columns is null) return map;
            foreach (var c in columns)
            {
                string name = c.Name ?? "";
                if (!map.TryGetValue(name, out var list))
                {
                    list = new List<QualityColumnProfile>();
                    map[name] = list;
                }
                list.Add(c);
            }
            return map;
        }

        private static QualitySnapshotDiffItem ColumnItem(QualitySnapshotDiffKind kind, QualitySeverity severity,
            QualityColumnProfile? baseline, QualityColumnProfile? current, bool approximate)
        {
            var col = current ?? baseline!;
            return new QualitySnapshotDiffItem
            {
                Kind = kind,
                Severity = severity,
                ColumnName = col.Name ?? "",
                BaselineColumnIndex = baseline?.Index ?? -1,
                CurrentColumnIndex = current?.Index ?? -1,
                Approximate = approximate,
                BaselineText = baseline?.ExpectedType.ToString(),
                CurrentText = current?.ExpectedType.ToString(),
            };
        }

        private static void CompareColumn(QualityColumnProfile b, QualityColumnProfile c,
            long baselineRows, long currentRows, QualitySnapshotDiffOptions options, bool partial,
            List<QualitySnapshotDiffItem> items)
        {
            if (b.ExpectedType != c.ExpectedType)
            {
                items.Add(new QualitySnapshotDiffItem
                {
                    Kind = QualitySnapshotDiffKind.TypeChanged,
                    Severity = QualitySeverity.Warning,
                    ColumnName = c.Name ?? "",
                    BaselineColumnIndex = b.Index,
                    CurrentColumnIndex = c.Index,
                    Approximate = partial,
                    BaselineText = b.ExpectedType.ToString(),
                    CurrentText = c.ExpectedType.ToString(),
                });
            }

            decimal pointsExact = MissingRatePoints(b.MissingCount, baselineRows, c.MissingCount, currentRows);
            double points = (double)pointsExact;
            if (Math.Abs(pointsExact) > (decimal)Math.Max(0, options.MissingRateDeltaPoints))
            {
                items.Add(new QualitySnapshotDiffItem
                {
                    Kind = QualitySnapshotDiffKind.MissingRateChanged,
                    // 결측이 늘면 경고, 줄면 정보 — 개선을 악화와 같은 심각도로 올리지 않는다.
                    Severity = points > 0 ? QualitySeverity.Warning : QualitySeverity.Info,
                    ColumnName = c.Name ?? "",
                    BaselineColumnIndex = b.Index,
                    CurrentColumnIndex = c.Index,
                    Approximate = partial,
                    BaselineNumber = Rate(b.MissingCount, baselineRows),
                    CurrentNumber = Rate(c.MissingCount, currentRows),
                    Delta = points,
                });
            }

            bool uniqueApprox = partial || b.DistinctIsLowerBound || c.DistinctIsLowerBound;
            if (UniqueBeyond(b.DistinctCount, c.DistinctCount, options.UniqueCountChangeRatio))
            {
                double? ratio = b.DistinctCount == 0
                    ? null
                    : (c.DistinctCount - b.DistinctCount) / (double)b.DistinctCount;
                items.Add(new QualitySnapshotDiffItem
                {
                    Kind = QualitySnapshotDiffKind.UniqueCountChanged,
                    Severity = QualitySeverity.Info,
                    ColumnName = c.Name ?? "",
                    BaselineColumnIndex = b.Index,
                    CurrentColumnIndex = c.Index,
                    Approximate = uniqueApprox,
                    BaselineNumber = b.DistinctCount,
                    CurrentNumber = c.DistinctCount,
                    Delta = ratio,
                });
            }

            CompareNumeric(QualitySnapshotDiffKind.NumericMinChanged, b.NumericMin, c.NumericMin,
                b, c, options.NumericRelativeThreshold, partial, items);
            CompareNumeric(QualitySnapshotDiffKind.NumericMaxChanged, b.NumericMax, c.NumericMax,
                b, c, options.NumericRelativeThreshold, partial, items);
            CompareNumeric(QualitySnapshotDiffKind.NumericMeanChanged, b.NumericMean, c.NumericMean,
                b, c, options.NumericRelativeThreshold, partial, items);
        }

        private static void CompareNumeric(QualitySnapshotDiffKind kind, double? baseline, double? current,
            QualityColumnProfile b, QualityColumnProfile c, double relativeThreshold, bool partial,
            List<QualitySnapshotDiffItem> items)
        {
            // 한쪽만 있으면 "when available"이 아니다 — 타입 변화 항목이 그 자리를 맡는다.
            if (baseline is not { } bv || current is not { } cv) return;
            if (!NumericShifted(bv, cv, relativeThreshold)) return;
            items.Add(new QualitySnapshotDiffItem
            {
                Kind = kind,
                Severity = QualitySeverity.Info,
                ColumnName = c.Name ?? "",
                BaselineColumnIndex = b.Index,
                CurrentColumnIndex = c.Index,
                Approximate = partial,
                BaselineNumber = bv,
                CurrentNumber = cv,
                Delta = cv - bv,
            });
        }

        private static double Rate(long missing, long rows) => rows <= 0 ? 0 : (double)missing / rows;
        /// <summary>결측률 차(퍼센트포인트, 부호 있음). 건수/행수의 십진 나눗셈이라 5.0pp 경계가 이진 오차로 넘어가지 않는다.</summary>
        private static decimal MissingRatePoints(long missingB, long rowsB, long missingC, long rowsC)
        {
            decimal rateB = rowsB <= 0 ? 0 : (decimal)missingB / rowsB;
            decimal rateC = rowsC <= 0 ? 0 : (decimal)missingC / rowsC;
            return (rateC - rateB) * 100m;
        }


        /// <summary>임계를 엄격히 초과할 때만. 동일값(경계)은 항목화하지 않는다.</summary>
        private static bool Beyond(double magnitude, double threshold)
        {
            if (threshold < 0) threshold = 0;
            if (!(magnitude > threshold)) return false;
            // 이진 부동소수에서 경계가 한 ulp 넘어 보이는 경우를 경계로 되돌린다.
            double slack = 1e-9 * Math.Max(1.0, Math.Abs(threshold));
            return magnitude - threshold > slack;
        }

        private static bool UniqueBeyond(long baseline, long current, double threshold)
        {
            if (baseline == current) return false;
            if (baseline == 0) return current != 0;
            if (threshold < 0) threshold = 0;
            double ratio = Math.Abs(current - baseline) / (double)baseline;
            return Beyond(ratio, threshold);
        }

        private static bool NumericShifted(double baseline, double current, double relativeThreshold)
        {
            double abs = Math.Abs(current - baseline);
            if (abs == 0 || double.IsNaN(abs)) return false;
            if (relativeThreshold <= 0) return true;
            double scale = Math.Max(Math.Abs(baseline), Math.Abs(current));
            if (scale == 0 || double.IsInfinity(scale)) return abs > 0;
            return Beyond(abs / scale, relativeThreshold);
        }

        // ---------------------------------------------------------------- 위장결측 후보

        private static Dictionary<string, Dictionary<string, long>> SentinelsByColumn(QualityReport report)
        {
            var map = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            if (report.Findings is null) return map;
            foreach (var f in report.Findings)
            {
                if (f.Kind != QualityCheckKind.DisguisedMissing) continue;
                string col = f.ColumnName ?? "";
                if (!map.TryGetValue(col, out var values))
                {
                    values = new Dictionary<string, long>(StringComparer.Ordinal);
                    map[col] = values;
                }
                if (f.Breakdown is null) continue;
                foreach (var b in f.Breakdown)
                {
                    values.TryGetValue(b.Value ?? "", out long n);
                    values[b.Value ?? ""] = n + b.Count;
                }
            }
            return map;
        }

        private static void CompareSentinels(SortedSet<string> pairedNames,
            Dictionary<string, Dictionary<string, long>> baseline,
            Dictionary<string, Dictionary<string, long>> current,
            bool partial, List<QualitySnapshotDiffItem> items)
        {
            foreach (string name in pairedNames)
            {
                baseline.TryGetValue(name, out var bs);
                current.TryGetValue(name, out var cs);
                bs ??= new Dictionary<string, long>(StringComparer.Ordinal);
                cs ??= new Dictionary<string, long>(StringComparer.Ordinal);

                var values = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var v in bs.Keys) values.Add(v);
                foreach (var v in cs.Keys) values.Add(v);
                foreach (string value in values)
                {
                    bool inB = bs.ContainsKey(value);
                    bool inC = cs.ContainsKey(value);
                    if (inB == inC) continue;
                    items.Add(new QualitySnapshotDiffItem
                    {
                        Kind = inC
                            ? QualitySnapshotDiffKind.SentinelAppeared
                            : QualitySnapshotDiffKind.SentinelDisappeared,
                        // 후보는 단정이 아니다 — 심각도로 단정하지 않는다.
                        Severity = QualitySeverity.Info,
                        ColumnName = name,
                        CheckKind = QualityCheckKind.DisguisedMissing,
                        Approximate = partial,
                        BaselineText = inB ? value : null,
                        CurrentText = inC ? value : null,
                        BaselineNumber = inB ? bs[value] : null,
                        CurrentNumber = inC ? cs[value] : null,
                    });
                }
            }
        }

        // ---------------------------------------------------------------- 발견

        private readonly record struct FindingKey(QualityCheckKind Kind, string Column, string RuleName);

        /// <summary>검사 종류 + 컬럼명 + 규칙명. 규칙·적합성·DQD가 아니면 라벨은 비운다(상수값·건수·예시는 정체성이 아님).</summary>
        private static FindingKey KeyOf(QualityFinding f)
            => new(f.Kind, f.ColumnName ?? "",
                QualitySessionChecks.UsesLabel(f.Kind) ? f.Label ?? "" : "");

        private static void CompareFindings(QualityReport baseline, QualityReport current, bool partial,
            List<QualitySnapshotDiffItem> items)
        {
            bool dupSkipped = baseline.DuplicateRowCheckSkipped || current.DuplicateRowCheckSkipped;
            if (dupSkipped)
            {
                items.Add(new QualitySnapshotDiffItem
                {
                    Kind = QualitySnapshotDiffKind.CheckNotComparable,
                    Severity = QualitySeverity.Warning,
                    CheckKind = QualityCheckKind.DuplicateRows,
                    Approximate = true,
                    BaselineText = baseline.DuplicateRowCheckSkipped ? "skipped" : "ran",
                    CurrentText = current.DuplicateRowCheckSkipped ? "skipped" : "ran",
                });
            }

            var baseGroups = GroupFindings(baseline.Findings, dupSkipped);
            var curGroups = GroupFindings(current.Findings, dupSkipped);
            var keys = new List<FindingKey>(baseGroups.Count + curGroups.Count);
            foreach (var k in baseGroups.Keys)
                if (!curGroups.ContainsKey(k)) keys.Add(k);
            foreach (var k in curGroups.Keys) keys.Add(k);
            keys.Sort(static (a, b) =>
            {
                int c = a.Kind.CompareTo(b.Kind);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Column, b.Column);
                return c != 0 ? c : string.CompareOrdinal(a.RuleName, b.RuleName);
            });

            foreach (var key in keys)
            {
                baseGroups.TryGetValue(key, out var bs);
                curGroups.TryGetValue(key, out var cs);
                bs ??= new List<QualityFinding>();
                cs ??= new List<QualityFinding>();
                int n = Math.Min(bs.Count, cs.Count);
                for (int i = 0; i < n; i++)
                    items.Add(FindingItem(QualitySnapshotDiffKind.FindingPersisting, QualitySeverity.Info,
                        key, bs[i], cs[i], partial));
                // 규칙·키 유일성·참조 무결성은 사용자가 세션마다 실행한다. 현재 쪽에 없으면 "해소"가 아니라
                // 재실행되지 않았을 수 있다(참조 무결성은 통과 시 발견을 남기지 않음) — 정직하게 구분한다.
                var missingKind = IsSessionCheck(key.Kind)
                    ? QualitySnapshotDiffKind.FindingNotRechecked
                    : QualitySnapshotDiffKind.FindingResolved;
                for (int i = n; i < bs.Count; i++)
                    items.Add(FindingItem(missingKind, QualitySeverity.Info,
                        key, bs[i], current: null, partial));
                for (int i = n; i < cs.Count; i++)
                    items.Add(FindingItem(QualitySnapshotDiffKind.FindingNew, cs[i].Severity,
                        key, baseline: null, cs[i], partial));
            }
        }

        private static bool IsSessionCheck(QualityCheckKind kind) => QualitySessionChecks.IsUserRun(kind);

        private static Dictionary<FindingKey, List<QualityFinding>> GroupFindings(
            IReadOnlyList<QualityFinding>? findings, bool skipDuplicateRows)
        {
            var map = new Dictionary<FindingKey, List<QualityFinding>>();
            if (findings is null) return map;
            foreach (var f in findings)
            {
                if (skipDuplicateRows && f.Kind == QualityCheckKind.DuplicateRows) continue;
                var key = KeyOf(f);
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<QualityFinding>();
                    map[key] = list;
                }
                list.Add(f);
            }
            return map;
        }

        private static QualitySnapshotDiffItem FindingItem(QualitySnapshotDiffKind kind, QualitySeverity severity,
            FindingKey key, QualityFinding? baseline, QualityFinding? current, bool partial)
        {
            var sample = current ?? baseline!;
            return new QualitySnapshotDiffItem
            {
                Kind = kind,
                Severity = severity,
                ColumnName = key.Column,
                BaselineColumnIndex = baseline?.Column ?? -1,
                CurrentColumnIndex = current?.Column ?? -1,
                CheckKind = key.Kind,
                RuleName = key.RuleName,
                Approximate = partial || (baseline?.Approximate ?? false) || (current?.Approximate ?? false),
                BaselineNumber = baseline?.ViolationCount,
                CurrentNumber = current?.ViolationCount,
                Delta = baseline is not null && current is not null
                    ? current.ViolationCount - baseline.ViolationCount
                    : null,
                BaselineText = baseline is null ? null : sample.Kind.ToString(),
                CurrentText = current is null ? null : sample.Kind.ToString(),
            };
        }

        // ---------------------------------------------------------------- 정렬

        private static int CategoryRank(QualitySnapshotDiffKind kind) => kind switch
        {
            QualitySnapshotDiffKind.ColumnRemoved or QualitySnapshotDiffKind.TypeChanged
                or QualitySnapshotDiffKind.MissingRateChanged => 0,
            QualitySnapshotDiffKind.ColumnAdded or QualitySnapshotDiffKind.UniqueCountChanged
                or QualitySnapshotDiffKind.NumericMinChanged or QualitySnapshotDiffKind.NumericMaxChanged
                or QualitySnapshotDiffKind.NumericMeanChanged or QualitySnapshotDiffKind.SentinelAppeared
                or QualitySnapshotDiffKind.SentinelDisappeared or QualitySnapshotDiffKind.RowCountChanged
                or QualitySnapshotDiffKind.CheckNotComparable or QualitySnapshotDiffKind.FindingNotRechecked => 1,
            QualitySnapshotDiffKind.FindingNew => 2,
            QualitySnapshotDiffKind.FindingResolved => 3,
            _ => 4, // persisting은 변화 뒤에
        };

        private static int CompareItems(QualitySnapshotDiffItem a, QualitySnapshotDiffItem b)
        {
            int c = b.Severity.CompareTo(a.Severity);
            if (c != 0) return c;
            c = CategoryRank(a.Kind).CompareTo(CategoryRank(b.Kind));
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ColumnName, b.ColumnName);
            if (c != 0) return c;
            c = a.Kind.CompareTo(b.Kind);
            if (c != 0) return c;
            c = (a.CheckKind ?? QualityCheckKind.MissingRate).CompareTo(b.CheckKind ?? QualityCheckKind.MissingRate);
            if (c != 0) return c;
            return string.CompareOrdinal(a.RuleName, b.RuleName);
        }
    }
}
