using System.Globalization;

namespace NanumCsvViewer.Csv.DataQuality
{
    /// <summary>
    /// 부모 키 집합의 예상 메모리가 예산을 초과했다. 부분 집합을 완전한 검사 결과로 반환하지 않는다
    /// (DistinctValueLimitException·PivotMemoryLimitException과 같은 계약).
    /// </summary>
    public sealed class ReferentialIntegrityBudgetException : InvalidOperationException
    {
        public ReferentialIntegrityBudgetException()
            : base("The parent key set exceeds the memory budget. No partial result is returned.")
        {
        }
    }

    /// <summary>부모 테이블의 스레드 안전 행 접근자. 자식은 <see cref="QualityScanSource"/>를 쓴다.</summary>
    public sealed record ReferentialParentSource
    {
        /// <summary>데이터 행 인덱스(0-based) → 필드 배열. 순차 호출을 전제로 한다(부모 적재는 단일 스레드).</summary>
        public required Func<int, string[]> RowAt { get; init; }
        public required int RowCount { get; init; }
        public string Name { get; init; } = "";
    }

    /// <summary>참조 무결성 검사 옵션. 기본은 대소문자 구분·트림 없음·빈 키 건너뜀(후보 의미론, 결측 단정 아님).</summary>
    public sealed record ReferentialIntegrityOptions
    {
        /// <summary>빈 값·널 토큰으로만 이루어진 자식 키를 건너뛴다(기본). false면 부모에 없을 때 위반.</summary>
        public bool SkipBlankChildKeys { get; init; } = true;
        /// <summary>true면 Ordinal, false면 OrdinalIgnoreCase.</summary>
        public bool CaseSensitive { get; init; } = true;
        /// <summary>비교 전에 각 키 파트를 Trim한다. 빈 키 판정은 이 옵션과 무관하게 항상 Trim 후 본다.</summary>
        public bool Trim { get; init; }
        public int MaxExamples { get; init; } = 20;
        public int DegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
        /// <summary>부모 키 집합의 보수적 예상 바이트. 초과 시 예외, 부분 결과 없음. 프로세스 RAM 하드 제한이 아니다.</summary>
        public long ParentKeyMemoryBudgetBytes { get; init; } = 256L * 1024 * 1024;
    }

    /// <summary>
    /// 교차 테이블 참조 무결성(FK) 후보 검사. 부모 (복합)키 집합을 메모리 예산 안에서 만든 뒤
    /// 자식 행을 파티션 병렬로 읽어 고아 행을 센다. 술어는 부모 집합 소속 검사라 필터 칩·행 점프에 그대로 쓴다.
    /// 선언된 제약이 아니라 후보 검사다 — 빈 키는 기본으로 위반이 아니다.
    /// </summary>
    public static class ReferentialIntegrityScanner
    {
        public sealed record Result(
            QualityFinding? Finding,
            long OrphanRows,
            long BlankSkipped,
            long ParentDistinctKeys);

        public static Result Scan(
            QualityScanSource child,
            IReadOnlyList<int> childColumns,
            ReferentialParentSource parent,
            IReadOnlyList<int> parentColumns,
            ReferentialIntegrityOptions options,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(child);
            ArgumentNullException.ThrowIfNull(parent);
            ArgumentNullException.ThrowIfNull(options);
            if (childColumns is null || childColumns.Count == 0)
                throw new ArgumentException("Child key columns are required.", nameof(childColumns));
            if (parentColumns is null || parentColumns.Count == 0)
                throw new ArgumentException("Parent key columns are required.", nameof(parentColumns));
            if (childColumns.Count != parentColumns.Count)
                throw new ArgumentException("Child and parent key column counts must match.");
            if (options.ParentKeyMemoryBudgetBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(options), "Parent key memory budget must be positive.");

            ct.ThrowIfCancellationRequested();

            var childCols = childColumns as int[] ?? childColumns.ToArray();
            var parentCols = parentColumns as int[] ?? parentColumns.ToArray();
            var comparer = new PartKeyComparer(options.CaseSensitive);
            int parentRows = Math.Max(0, parent.RowCount);
            int childRows = Math.Max(0, child.RowCount);
            int maxExamples = Math.Max(0, options.MaxExamples);

            var parentKeys = BuildParentKeys(parent, parentCols, parentRows, options, comparer, progress, ct);
            long parentDistinct = parentKeys.Count;

            if (childRows == 0)
            {
                progress?.Report(100);
                return new Result(null, 0, 0, parentDistinct);
            }

            int dop = Math.Clamp(options.DegreeOfParallelism, 1, Math.Max(1, childRows / 4096 + 1));
            var partOrphans = new long[dop];
            var partBlanks = new long[dop];
            var partExamples = new List<OrphanExample>[dop];
            long processed = 0;
            int lastPercent = -1;

            var tasks = new Task[dop];
            for (int p = 0; p < dop; p++)
            {
                int pi = p;
                int lo = (int)((long)childRows * pi / dop);
                int hi = (int)((long)childRows * (pi + 1) / dop);
                tasks[pi] = Task.Run(() =>
                {
                    var rowAt = child.RowAt;
                    var buf = new string[childCols.Length];
                    var seen = new HashSet<string[]>(comparer);
                    var examples = new List<OrphanExample>(Math.Min(maxExamples, 64));
                    long orphans = 0, blanks = 0;
                    for (int i = lo; i < hi; i++)
                    {
                        if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                        string[] row = rowAt(i);
                        if (options.SkipBlankChildKeys && IsBlankKey(row, childCols))
                        {
                            blanks++;
                        }
                        else
                        {
                            Fill(row, childCols, options.Trim, buf);
                            if (!parentKeys.Contains(buf))
                            {
                                orphans++;
                                if (maxExamples > 0 && seen.Count < maxExamples)
                                {
                                    var copy = Copy(buf);
                                    if (seen.Add(copy))
                                        examples.Add(new OrphanExample(i, Display(copy), copy));
                                }
                            }
                        }
                        if ((i & 0x1FFF) == 0x1FFF && progress is not null)
                            Report(Interlocked.Add(ref processed, 8192), childRows, 40, 79, ref lastPercent, progress);
                    }
                    partOrphans[pi] = orphans;
                    partBlanks[pi] = blanks;
                    partExamples[pi] = examples;
                }, ct);
            }
            QualityProfiler.WaitAllDraining(tasks, ct);

            long orphanRows = 0, blankSkipped = 0;
            for (int p = 0; p < dop; p++)
            {
                orphanRows += partOrphans[p];
                blankSkipped += partBlanks[p];
            }

            if (orphanRows == 0)
            {
                progress?.Report(100);
                return new Result(null, 0, blankSkipped, parentDistinct);
            }

            // 파티션별 최초 MaxExamples개 고유 고아 키의 합집합을 원본 행 순으로 자르면
            // 전역 최초 MaxExamples개와 같다(더 이른 키가 같은 파티션 캡 안에 들어 있으므로).
            var earliest = new Dictionary<string[], OrphanExample>(comparer);
            for (int p = 0; p < dop; p++)
            {
                foreach (var ex in partExamples[p])
                {
                    if (!earliest.TryGetValue(ex.Key, out var cur) || ex.Row < cur.Row)
                        earliest[ex.Key] = ex;
                }
                partExamples[p] = null!;
            }
            var selected = earliest.Values
                .OrderBy(e => e.Row)
                .ThenBy(e => e.Display, StringComparer.Ordinal)
                .Take(maxExamples)
                .ToArray();

            long[] counts = selected.Length == 0
                ? Array.Empty<long>()
                : CountSelected(child, childCols, childRows, options, comparer, selected, dop, progress, ct);

            var examples = new QualityExample[selected.Length];
            var breakdown = new ValueCount[selected.Length];
            for (int i = 0; i < selected.Length; i++)
            {
                examples[i] = new QualityExample(selected[i].Row + 1L, selected[i].Display);
                breakdown[i] = new ValueCount(selected[i].Display, counts[i]);
            }
            progress?.Report(100);

            // 술어가 캡처하는 집합은 이 시점 이후 불변. 빈 키 정책은 스캔과 동일해야 필터 행이 고아 행과 일치한다.
            var frozen = parentKeys;
            bool skipBlank = options.SkipBlankChildKeys;
            bool trim = options.Trim;
            var finding = new QualityFinding
            {
                Kind = QualityCheckKind.ForeignKeyOrphan,
                Dimension = QualityDimension.Conformance,
                Severity = QualitySeverity.Critical,
                ViolationCount = orphanRows,
                EvaluatedRows = childRows,
                Examples = examples,
                Breakdown = breakdown,
                SkippedRows = blankSkipped,
                ViolationPredicate = row =>
                {
                    if (skipBlank && IsBlankKey(row, childCols)) return false;
                    var parts = new string[childCols.Length];
                    Fill(row, childCols, trim, parts);
                    return !frozen.Contains(parts);
                },
            };
            return new Result(finding, orphanRows, blankSkipped, parentDistinct);
        }

        private static HashSet<string[]> BuildParentKeys(
            ReferentialParentSource parent, int[] cols, int rows,
            ReferentialIntegrityOptions options, PartKeyComparer comparer,
            IProgress<int>? progress, CancellationToken ct)
        {
            var set = new HashSet<string[]>(comparer);
            if (rows == 0) return set;

            long budget = options.ParentKeyMemoryBudgetBytes;
            long estimated = 0;
            var rowAt = parent.RowAt;
            var buf = new string[cols.Length];
            int lastPercent = -1;
            for (int i = 0; i < rows; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                Fill(rowAt(i), cols, options.Trim, buf);
                if (set.Contains(buf)) continue;

                long add = EstimateKeyBytes(buf);
                if (estimated < 0 || add < 0 || estimated + add < estimated || estimated + add > budget)
                    throw new ReferentialIntegrityBudgetException();

                var copy = Copy(buf);
                set.Add(copy);
                estimated += add;

                if ((i & 0x1FFF) == 0x1FFF && progress is not null)
                    Report(i + 1L, rows, 0, 39, ref lastPercent, progress);
            }
            return set;
        }

        private static long[] CountSelected(
            QualityScanSource child, int[] cols, int rows, ReferentialIntegrityOptions options,
            PartKeyComparer comparer, OrphanExample[] selected, int dop,
            IProgress<int>? progress, CancellationToken ct)
        {
            var indexOf = new Dictionary<string[], int>(comparer);
            for (int i = 0; i < selected.Length; i++) indexOf[selected[i].Key] = i;
            var partCounts = new long[dop][];
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
                    var local = new long[selected.Length];
                    var rowAt = child.RowAt;
                    var buf = new string[cols.Length];
                    for (int i = lo; i < hi; i++)
                    {
                        if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                        string[] row = rowAt(i);
                        if (options.SkipBlankChildKeys && IsBlankKey(row, cols)) continue;
                        Fill(row, cols, options.Trim, buf);
                        if (indexOf.TryGetValue(buf, out int slot)) local[slot]++;
                        if ((i & 0x1FFF) == 0x1FFF && progress is not null)
                            Report(Interlocked.Add(ref processed, 8192), rows, 80, 99, ref lastPercent, progress);
                    }
                    partCounts[pi] = local;
                }, ct);
            }
            QualityProfiler.WaitAllDraining(tasks, ct);

            var counts = new long[selected.Length];
            for (int p = 0; p < dop; p++)
                for (int i = 0; i < counts.Length; i++)
                    counts[i] += partCounts[p][i];
            return counts;
        }

        // 키 전체가 빈 값·널 토큰일 때만 건너뛴다. 복합키의 일부만 비어 있으면 실제 키로 검사한다.
        internal static bool IsBlankKey(string[]? row, int[] cols)
        {
            if (row is null) return true;
            for (int i = 0; i < cols.Length; i++)
            {
                int c = cols[i];
                string raw = c >= 0 && c < row.Length && row[c] is not null ? row[c] : "";
                if (!IsBlankPart(raw)) return false;
            }
            return true;
        }

        private static bool IsBlankPart(string raw)
        {
            string t = raw.Trim();
            return t.Length == 0 || ColumnStatisticsBuilder.IsNullToken(t);
        }

        private static void Fill(string[]? row, int[] cols, bool trim, string[] dest)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                int c = cols[i];
                string raw = row is not null && c >= 0 && c < row.Length && row[c] is not null ? row[c] : "";
                dest[i] = trim ? raw.Trim() : raw;
            }
        }

        private static string[] Copy(string[] parts)
        {
            var copy = new string[parts.Length];
            Array.Copy(parts, copy, parts.Length);
            return copy;
        }

        private static string Display(string[] parts) => string.Join(" | ", parts);

        // 피벗과 같은 보수적 추정: 슬롯·배열·문자열 헤더 + 문자. 삽입 전에 검사하고, 넘으면 넣지 않는다.
        private static long EstimateKeyBytes(string[] parts)
        {
            long n = 512L + parts.Length * 64L;
            foreach (string p in parts) n += 32L + 2L * p.Length;
            return n;
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

        private readonly record struct OrphanExample(int Row, string Display, string[] Key);

        private sealed class PartKeyComparer : IEqualityComparer<string[]>
        {
            private readonly StringComparer _comparer;
            public PartKeyComparer(bool caseSensitive)
                => _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

            public bool Equals(string[]? x, string[]? y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x is null || y is null || x.Length != y.Length) return false;
                for (int i = 0; i < x.Length; i++)
                    if (_comparer.Compare(x[i], y[i]) != 0) return false;
                return true;
            }

            public int GetHashCode(string[] obj)
            {
                var hc = new HashCode();
                foreach (string p in obj) hc.Add(p, _comparer);
                return hc.ToHashCode();
            }
        }
    }
}
