using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NanumCsvViewer.Csv.DataQuality
{
    /// <summary>
    /// 사용자 정의 규칙 1개. <see cref="Expression"/>은 <b>위반 조건식</b> —
    /// 매칭되는 행이 위반이며 "0건이어야 하는 식"이다(설계 논쟁 합의).
    /// 예: <c>age &lt; 0 OR age &gt; 120</c>, <c>[end_date] &lt; [start_date]</c>.
    /// </summary>
    public sealed record QualityRule
    {
        public required string Name { get; init; }
        public required string Expression { get; init; }
        public QualitySeverity Severity { get; init; } = QualitySeverity.Warning;
        public bool Enabled { get; init; } = true;
    }

    /// <summary>규칙 세트(JSON 저장/불러오기 — 사용자 소유·팀 공유 파일). 내장 의료 팩은 없다(논쟁 4:0).</summary>
    public sealed record QualityRuleSet
    {
        public int SchemaVersion { get; init; } = 1;
        public string Name { get; init; } = "";
        public IReadOnlyList<QualityRule> Rules { get; init; } = Array.Empty<QualityRule>();

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        public string Serialize() => JsonSerializer.Serialize(this, Options);

        /// <summary>역직렬화. 형식 오류는 JsonException을 그대로 전파한다(호출자에서 안내).</summary>
        public static QualityRuleSet Deserialize(string json)
            => JsonSerializer.Deserialize<QualityRuleSet>(json, Options)
               ?? throw new JsonException("규칙 세트 JSON이 비어 있습니다.");
    }

    /// <summary>규칙 컴파일 오류(어느 규칙이 문제인지 포함).</summary>
    public sealed class QualityRuleCompileException : Exception
    {
        public string RuleName { get; }
        public QualityRuleCompileException(string ruleName, string message)
            : base($"[{ruleName}] {message}") => RuleName = ruleName;
    }

    /// <summary>
    /// 규칙 실행기: 활성 규칙 전부를 파티션 병렬 1-pass로 평가해 규칙별 발견을 만든다.
    /// 술어는 기존 식 필터 엔진(AdvancedFilterExpression)을 그대로 재사용한다 — 신규 파서 없음.
    /// </summary>
    public static class QualityRuleRunner
    {
        public static IReadOnlyList<QualityFinding> Run(IReadOnlyList<QualityRule> rules,
            QualityScanSource source, QualityScanOptions options, IProgress<int>? progress, CancellationToken ct)
        {
            var active = new List<(QualityRule Rule, Func<string[], bool> Pred)>();
            foreach (var r in rules)
            {
                if (!r.Enabled) continue;
                // blankNeverMatchesOrdering: 결측 셀이 순서 비교 규칙(age < 0 등)에 문자열 폴백으로
                // 잘못 매칭돼 위반으로 이중 계산되는 것을 막는다(결측은 MissingRate 검사가 담당).
                try { active.Add((r, AdvancedFilterExpression.Compile(r.Expression, source.Headers, blankNeverMatchesOrdering: true).Predicate)); }
                catch (AdvancedFilterExpressionException ex)
                { throw new QualityRuleCompileException(r.Name, ex.Message); }
            }
            if (active.Count == 0) return Array.Empty<QualityFinding>();

            int rows = source.RowCount;
            int k = active.Count;
            int dop = Math.Clamp(options.DegreeOfParallelism, 1, Math.Max(1, rows / 4096 + 1));

            var counts = new long[dop][];
            var examples = new List<QualityExample>[dop][];
            long processed = 0; int lastPercent = -1;

            var tasks = new Task[dop];
            for (int p = 0; p < dop; p++)
            {
                int pi = p;
                int lo = (int)((long)rows * pi / dop);
                int hi = (int)((long)rows * (pi + 1) / dop);
                tasks[pi] = Task.Run(() =>
                {
                    var cnt = new long[k];
                    var exs = new List<QualityExample>[k];
                    for (int j = 0; j < k; j++) exs[j] = new List<QualityExample>();
                    var rowAt = source.RowAt;

                    for (int i = lo; i < hi; i++)
                    {
                        if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                        string[] row = rowAt(i);
                        for (int j = 0; j < k; j++)
                        {
                            if (!active[j].Pred(row)) continue;
                            cnt[j]++;
                            if (exs[j].Count < options.MaxExamples)
                                exs[j].Add(new QualityExample(i + 1L, PreviewRow(row)));
                        }
                        if ((i & 0x1FFF) == 0x1FFF && progress is not null && rows > 0)
                        {
                            long done = Interlocked.Add(ref processed, 8192);
                            int pct = (int)Math.Min(99, done * 100 / rows);
                            if (pct > Volatile.Read(ref lastPercent))
                            {
                                Volatile.Write(ref lastPercent, pct);
                                progress.Report(pct);
                            }
                        }
                    }
                    counts[pi] = cnt;
                    examples[pi] = exs;
                }, ct);
            }
            QualityProfiler.WaitAllDraining(tasks, ct);
            progress?.Report(100);

            var findings = new List<QualityFinding>(k);
            for (int j = 0; j < k; j++)
            {
                long total = 0;
                var all = new List<QualityExample>();
                for (int p = 0; p < dop; p++)
                {
                    total += counts[p][j];
                    all.AddRange(examples[p][j]);
                }
                var (rule, pred) = active[j];
                findings.Add(new QualityFinding
                {
                    Kind = QualityCheckKind.Rule,
                    Dimension = QualityDimension.Plausibility,
                    Severity = total > 0 ? rule.Severity : QualitySeverity.Info,
                    ViolationCount = total,
                    EvaluatedRows = rows,
                    Label = rule.Name,
                    Examples = all.OrderBy(e => e.SourceRow).Take(options.MaxExamples).ToArray(),
                    ViolationPredicate = total > 0 ? pred : null,
                });
            }
            return findings;
        }

        // 예시 값: 규칙은 다중 컬럼일 수 있어 행 앞부분을 짧게 미리보기로 담는다.
        private static string PreviewRow(string[] row)
        {
            const int maxFields = 4, maxLen = 60;
            string s = string.Join(" | ", row.Take(maxFields));
            if (row.Length > maxFields) s += " …";
            return s.Length <= maxLen ? s : s[..maxLen] + "…";
        }
    }

    /// <summary>
    /// 사용자 지정 (복합)키 유일성 검사. 1차 병렬 해시 계수 → 중복 시 2차 순차 패스로
    /// 실제 키 문자열·원본 행번호 예시를 수집한다(결정적). 64-bit 해시 기반(후보 의미론).
    /// </summary>
    public static class KeyUniquenessScanner
    {
        public sealed record Result(QualityFinding? Finding, bool Skipped, long Groups, long ExtraCopies);

        public static Result Scan(QualityScanSource source, IReadOnlyList<int> keyColumns,
            QualityScanOptions options, IProgress<int>? progress, CancellationToken ct)
        {
            int rows = source.RowCount;
            if (rows == 0 || keyColumns.Count == 0) return new Result(null, false, 0, 0);
            if (rows > options.DuplicateRowCap) return new Result(null, true, 0, 0); // 메모리 정직 가드

            var keys = keyColumns.ToArray();
            int dop = Math.Clamp(options.DegreeOfParallelism, 1, Math.Max(1, rows / 4096 + 1));
            // 해시별 (건수, 최초 등장 행 인덱스). 1차 패스에서 둘 다 수집해 2차 전수 재파싱을 없앤다.
            var partDicts = new Dictionary<ulong, (long Count, int FirstRow)>[dop];
            long processed = 0; int lastPercent = -1;

            var tasks = new Task[dop];
            for (int p = 0; p < dop; p++)
            {
                int pi = p;
                int lo = (int)((long)rows * pi / dop);
                int hi = (int)((long)rows * (pi + 1) / dop);
                tasks[pi] = Task.Run(() =>
                {
                    var dict = new Dictionary<ulong, (long, int)>(Math.Max(16, hi - lo));
                    var rowAt = source.RowAt;
                    for (int i = lo; i < hi; i++)
                    {
                        if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                        ulong h = HashKey(rowAt(i), keys);
                        if (dict.TryGetValue(h, out var e)) dict[h] = (e.Item1 + 1, e.Item2);
                        else dict[h] = (1, i); // 파티션은 lo→hi 순이라 Item2가 파티션 내 최초 등장
                        if ((i & 0x1FFF) == 0x1FFF && progress is not null)
                        {
                            long done = Interlocked.Add(ref processed, 8192);
                            int pct = (int)Math.Min(99, done * 100 / rows);
                            if (pct > Volatile.Read(ref lastPercent))
                            {
                                Volatile.Write(ref lastPercent, pct);
                                progress.Report(pct);
                            }
                        }
                    }
                    partDicts[pi] = dict;
                }, ct);
            }
            QualityProfiler.WaitAllDraining(tasks, ct);

            var merged = partDicts[0];
            for (int p = 1; p < dop; p++)
            {
                foreach (var kv in partDicts[p])
                    merged[kv.Key] = merged.TryGetValue(kv.Key, out var e)
                        ? (e.Count + kv.Value.Count, Math.Min(e.FirstRow, kv.Value.FirstRow)) // 최소 행 = 전역 최초
                        : kv.Value;
                partDicts[p] = null!;
            }

            long groups = 0, extra = 0;
            var dupList = new List<ulong>();
            foreach (var kv in merged)
                if (kv.Value.Count >= 2) { groups++; extra += kv.Value.Count - 1; dupList.Add(kv.Key); }

            if (groups == 0)
            {
                progress?.Report(100);
                return new Result(null, false, 0, 0);
            }

            // 건수 상위 MaxExamples 그룹만 선별(전체 그룹이 아니라 정확한 top-N). 키 문자열은 각 그룹의
            // 최초 행에서만 읽는다(≤MaxExamples회 — 2차 전수 패스 불필요).
            var topHashes = dupList
                .Select(h => (Hash: h, merged[h].Count, merged[h].FirstRow))
                .OrderByDescending(t => t.Count).ThenBy(t => t.FirstRow)
                .Take(options.MaxExamples)
                .ToArray();

            var rowAtSeq = source.RowAt;
            var breakdown = new List<ValueCount>(topHashes.Length);
            var examples = new List<QualityExample>(topHashes.Length);
            foreach (var t in topHashes)
            {
                string keyText = KeyDisplay(rowAtSeq(t.FirstRow), keys);
                breakdown.Add(new ValueCount(keyText, t.Count));
                examples.Add(new QualityExample(t.FirstRow + 1L, keyText));
            }
            progress?.Report(100);

            ulong[] dupArr = dupList.ToArray(); // 정렬 배열 + 이진탐색(HashSet 대비 보존 메모리 절감)
            Array.Sort(dupArr);
            var finding = new QualityFinding
            {
                Kind = QualityCheckKind.KeyUniqueness,
                Dimension = QualityDimension.Plausibility,
                Severity = QualitySeverity.Critical,
                ViolationCount = extra,
                EvaluatedRows = rows,
                Label = groups.ToString(CultureInfo.InvariantCulture),
                Breakdown = breakdown.ToArray(),
                Examples = examples.ToArray(),
                ViolationPredicate = row => Array.BinarySearch(dupArr, HashKey(row, keys)) >= 0,
            };
            return new Result(finding, false, groups, extra);
        }

        internal static ulong HashKey(string[] row, int[] keyColumns)
        {
            const ulong Prime = 1099511628211UL;
            ulong h = 14695981039346656037UL;
            foreach (int c in keyColumns)
            {
                string f = c >= 0 && c < row.Length ? row[c] : string.Empty;
                foreach (char ch in f) h = (h ^ ch) * Prime;
                h = (h ^ 0x1F) * Prime;
            }
            return h;
        }

        private static string KeyDisplay(string[] row, int[] keyColumns)
            => string.Join(" | ", keyColumns.Select(c => c >= 0 && c < row.Length ? row[c] : string.Empty));
    }
}
