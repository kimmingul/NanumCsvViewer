using System.Diagnostics;
using System.Globalization;

namespace NanumCsvViewer.Csv.DataQuality
{
    /// <summary>
    /// 전수 스트리밍 1-pass 품질 프로파일러(이슈 #26). 뷰 상한(200만 행)과 무관하게
    /// 파일 전체를 파티션 병렬로 스캔한다(RandomAccess·RecordIndex가 다중 독자 안전).
    /// 메모리 가드: 고유값 추적·수치 표본·중복 해시 모두 상한이 있고, 상한에 걸리면
    /// 결과에 하한/추정임을 정직하게 표기한다(설계 논쟁 합의 — "검사 범위를 거짓말하지 않는다").
    /// </summary>
    public static class QualityProfiler
    {
        // 위장결측 후보 테이블(고정 집합 → O(1) 메모리·정확 계수). "후보 + 사용자 확인" 의미론:
        // 수치·날짜는 분포 끝값(min/max)일 때만 후보로 승격해 오탐을 줄인다.
        private static readonly double[] NumericSentinels =
            { -9999, -999, -99, -9, -1, 9, 99, 999, 9999, 99999, 999999, 8888, 9998 };
        private static readonly string[] TextSentinels = { "-", ".", "?", "unknown", "none" };
        private static readonly DateTime[] DateSentinels =
            { new(1900, 1, 1), new(1970, 1, 1), new(2099, 12, 31), new(2100, 1, 1) };

        private static readonly Dictionary<double, int> NumericSentinelIndex = BuildIndex(NumericSentinels);
        private static readonly Dictionary<string, int> TextSentinelIndex = BuildTextIndex(TextSentinels);
        private static readonly Dictionary<DateTime, int> DateSentinelIndex = BuildIndex(DateSentinels);

        private static Dictionary<T, int> BuildIndex<T>(T[] values) where T : notnull
        {
            var d = new Dictionary<T, int>(values.Length);
            for (int i = 0; i < values.Length; i++) d[values[i]] = i;
            return d;
        }

        private static Dictionary<string, int> BuildTextIndex(string[] values)
        {
            var d = new Dictionary<string, int>(values.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < values.Length; i++) d[values[i]] = i;
            return d;
        }

        /// <summary>
        /// 모든 워커 태스크가 <b>실제로 끝날 때까지</b> 대기한 뒤 취소를 던진다.
        /// Task.WaitAll(tasks, ct)의 토큰 오버로드는 취소 시 워커 종료를 기다리지 않고 즉시 반환해
        /// 유기된 워커가 Dispose된 문서(SafeFileHandle)를 읽을 수 있다 — CancelAndDrainAsync 드레인 계약 위반.
        /// 워커는 스스로 ct를 관찰하므로 토큰 없는 WaitAll로 종료만 보장하면 문서 접근이 끝난 상태가 된다.
        /// </summary>
        internal static void WaitAllDraining(Task[] tasks, CancellationToken ct)
        {
            try { Task.WaitAll(tasks); }
            catch (AggregateException ae)
            {
                ct.ThrowIfCancellationRequested(); // 취소 원인이면 OCE로 통일
                throw ae.Flatten();                // 그 외 실제 오류는 전파
            }
            ct.ThrowIfCancellationRequested();
        }

        // 컬럼별 셀 검사 방식(기대 타입에서 유도).
        private enum Checker { None, Numeric, Integer, TemporalDate, TemporalTime, Boolean }

        private static Checker CheckerFor(ColumnValueType t) => t switch
        {
            ColumnValueType.Integer => Checker.Integer,
            ColumnValueType.Float or ColumnValueType.Currency
                or ColumnValueType.Percent or ColumnValueType.Scientific => Checker.Numeric,
            ColumnValueType.Date or ColumnValueType.DateTime => Checker.TemporalDate,
            ColumnValueType.Time => Checker.TemporalTime,
            ColumnValueType.Boolean => Checker.Boolean,
            _ => Checker.None,
        };

        /// <summary>값이 기대 타입 검사를 위반하는지(엔진 계수와 칩 술어가 같은 판정을 쓰도록 단일화).
        /// 비유한 값(NaN/Infinity/오버플로 1e999)은 수치·정수 모두 위반 — 스캔 경로의 IsFinite 게이트와 일치.</summary>
        private static bool ViolatesType(Checker checker, string v) => checker switch
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

        private static bool IsMissing(string trimmed)
            => trimmed.Length == 0 || ColumnStatisticsBuilder.IsNullToken(trimmed);

        private static string Cell(string[] row, int col)
            => col >= 0 && col < row.Length ? row[col] : string.Empty;

        // ---------------------------------------------------------------- 파티션 누적기

        private sealed class ColAcc
        {
            public long Empty, Whitespace, NullTok, NonNull;
            public long TypeViol; public List<QualityExample> TypeEx = new();
            public long CodeViol; public List<QualityExample> CodeEx = new();

            public long NumCount;
            public double Min = double.PositiveInfinity, Max = double.NegativeInfinity;
            public double Mean, M2;                       // Welford
            public List<double> Sample = new();
            public int Stride = 1, SeenSinceKeep;

            public long Future; public List<QualityExample> FutureEx = new();
            public bool HasTemporal;
            public DateTime TMin = DateTime.MaxValue, TMax = DateTime.MinValue;

            public Dictionary<string, long> Freq = new(StringComparer.Ordinal);
            public bool FreqOverflow;

            public long[] NumSent = new long[NumericSentinels.Length];
            public long[] TextSent = new long[TextSentinels.Length];
            public long[] DateSent = new long[DateSentinels.Length];
        }

        private sealed class PartitionAcc
        {
            public ColAcc[] Cols = Array.Empty<ColAcc>();
            public long Ragged; public List<QualityExample> RaggedEx = new();
            public Dictionary<ulong, long>? RowHashes;
        }

        // ---------------------------------------------------------------- 스캔 본체

        public static QualityReport Scan(QualityScanSource source, QualityScanOptions options,
            IProgress<int>? progress, CancellationToken ct)
        {
            int cols = source.Headers.Count;
            int rows = source.RowCount;
            var sw = Stopwatch.StartNew();

            var checkers = new Checker[cols];
            for (int c = 0; c < cols; c++)
                checkers[c] = source.ColumnTypes is { } types && c < types.Count
                    ? CheckerFor(types[c]) : Checker.None;

            bool dupEnabled = rows > 0 && rows <= options.DuplicateRowCap;

            int dop = Math.Clamp(options.DegreeOfParallelism, 1, Math.Max(1, rows / 4096 + 1));
            var parts = new PartitionAcc[dop];
            long processed = 0; int lastPercent = -1;

            var tasks = new Task[dop];
            for (int p = 0; p < dop; p++)
            {
                int pi = p;
                int lo = (int)((long)rows * pi / dop);
                int hi = (int)((long)rows * (pi + 1) / dop);
                tasks[pi] = Task.Run(() =>
                {
                    parts[pi] = ScanPartition(source, options, checkers, dupEnabled, lo, hi, cols, () =>
                    {
                        long done = Interlocked.Add(ref processed, 8192);
                        if (progress is null || rows == 0) return;
                        int pct = (int)Math.Min(99, done * 100 / rows);
                        if (pct > Volatile.Read(ref lastPercent))
                        {
                            Volatile.Write(ref lastPercent, pct);
                            progress.Report(pct);
                        }
                    }, ct);
                }, ct);
            }
            WaitAllDraining(tasks, ct);
            progress?.Report(100);

            // ── 병합 ──
            var merged = MergePartitions(parts, cols, options);
            sw.Stop();

            var profiles = BuildProfiles(source, merged, cols);
            var findings = BuildFindings(source, options, checkers, merged, rows, cols);

            return new QualityReport
            {
                RowsScanned = rows,
                ScannedFully = source.CoversAllRows,
                DuplicateRowCheckSkipped = rows > 0 && !dupEnabled,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                Columns = profiles,
                Findings = findings,
                SourceName = source.SourceName,
                SourceBytes = source.SourceBytes,
            };
        }

        private static PartitionAcc ScanPartition(QualityScanSource source, QualityScanOptions options,
            Checker[] checkers, bool dupEnabled, int lo, int hi, int cols, Action tick, CancellationToken ct)
        {
            var acc = new PartitionAcc { Cols = new ColAcc[cols] };
            for (int c = 0; c < cols; c++) acc.Cols[c] = new ColAcc();
            // 용량 미지정 시 8M/dop 항목까지 ~20회 리사이즈 가비지. 파티션 행수로 미리 예약.
            if (dupEnabled) acc.RowHashes = new Dictionary<ulong, long>(Math.Max(16, hi - lo));

            var allowed = source.AllowedCodes;
            int maxEx = options.MaxExamples;
            var rowAt = source.RowAt;

            for (int i = lo; i < hi; i++)
            {
                if ((i & 0xFFF) == 0) ct.ThrowIfCancellationRequested();
                if ((i & 0x1FFF) == 0x1FFF) tick();

                string[] row = rowAt(i);
                long sourceRow = i + 1L; // GetSourceRowNumber와 동일한 1-based 원본 행번호

                if (row.Length != cols)
                {
                    acc.Ragged++;
                    if (acc.RaggedEx.Count < maxEx)
                        acc.RaggedEx.Add(new QualityExample(sourceRow, LabelForFieldCount(row.Length)));
                }

                if (acc.RowHashes is { } hashes)
                {
                    ulong h = HashRow(row);
                    hashes[h] = hashes.TryGetValue(h, out long hc) ? hc + 1 : 1;
                }

                for (int c = 0; c < cols; c++)
                {
                    var a = acc.Cols[c];
                    string raw = Cell(row, c);
                    string v = raw.Trim();

                    if (v.Length == 0)
                    {
                        if (raw.Length == 0) a.Empty++; else a.Whitespace++;
                        continue;
                    }
                    if (ColumnStatisticsBuilder.IsNullToken(v)) { a.NullTok++; continue; }
                    a.NonNull++;

                    // 고유값(상한 있는 정확 계수): 상한 도달 후 새 키는 개수만 추정 불가로 표기.
                    if (a.Freq.TryGetValue(v, out long fc)) a.Freq[v] = fc + 1;
                    else if (a.Freq.Count < options.DistinctCapPerPartition) a.Freq[v] = 1;
                    else a.FreqOverflow = true;

                    if (TextSentinelIndex.TryGetValue(v, out int tsi)) a.TextSent[tsi]++;

                    switch (checkers[c])
                    {
                        case Checker.Numeric:
                        case Checker.Integer:
                            if (NumericAffix.TryParseNumber(v, out double d) && double.IsFinite(d))
                            {
                                AddNumeric(a, d, options.NumericSampleCap);
                                if (NumericSentinelIndex.TryGetValue(d, out int nsi)) a.NumSent[nsi]++;
                                if (checkers[c] == Checker.Integer && ViolatesType(Checker.Integer, v))
                                    AddViolation(a, sourceRow, v, maxEx);
                            }
                            else AddViolation(a, sourceRow, v, maxEx);
                            break;

                        case Checker.TemporalDate:
                        case Checker.TemporalTime:
                            var t = CsvDateParser.ParseDetailed(v, true);
                            bool bad = checkers[c] == Checker.TemporalDate
                                ? t is not { } td || td.Kind == TemporalKind.Time
                                : t is not { } tt || tt.Kind != TemporalKind.Time;
                            if (bad) AddViolation(a, sourceRow, v, maxEx);
                            else if (checkers[c] == Checker.TemporalDate)
                            {
                                DateTime dt = t!.Value.Value;
                                a.HasTemporal = true;
                                if (dt < a.TMin) a.TMin = dt;
                                if (dt > a.TMax) a.TMax = dt;
                                if (dt.Date > options.Today)
                                {
                                    a.Future++;
                                    if (a.FutureEx.Count < maxEx) a.FutureEx.Add(new QualityExample(sourceRow, v));
                                }
                                if (DateSentinelIndex.TryGetValue(dt.Date, out int dsi)) a.DateSent[dsi]++;
                            }
                            break;

                        case Checker.Boolean:
                            if (!ColumnStatisticsBuilder.IsBooleanToken(v)) AddViolation(a, sourceRow, v, maxEx);
                            break;
                    }

                    if (allowed is not null && c < allowed.Count && allowed[c] is { } set && !set.Contains(v))
                    {
                        a.CodeViol++;
                        if (a.CodeEx.Count < maxEx) a.CodeEx.Add(new QualityExample(sourceRow, v));
                    }
                }
            }
            return acc;
        }

        internal static string LabelForFieldCount(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static void AddViolation(ColAcc a, long sourceRow, string v, int maxEx)
        {
            a.TypeViol++;
            if (a.TypeEx.Count < maxEx) a.TypeEx.Add(new QualityExample(sourceRow, v));
        }

        private static void AddNumeric(ColAcc a, double d, int sampleCap)
        {
            a.NumCount++;
            if (d < a.Min) a.Min = d;
            if (d > a.Max) a.Max = d;
            double delta = d - a.Mean;
            a.Mean += delta / a.NumCount;
            a.M2 += delta * (d - a.Mean);

            // 결정적 스트라이드 표본: 가득 차면 짝수 인덱스만 남기고 보폭을 2배로.
            if (++a.SeenSinceKeep >= a.Stride)
            {
                a.SeenSinceKeep = 0;
                if (a.Sample.Count >= sampleCap)
                {
                    var s = a.Sample;
                    int w = 0;
                    for (int i = 0; i < s.Count; i += 2) s[w++] = s[i];
                    s.RemoveRange(w, s.Count - w);
                    a.Stride *= 2;
                }
                a.Sample.Add(d);
            }
        }

        /// <summary>완전 중복 행 탐지용 64-bit FNV-1a(필드 구분자 0x1F 포함). 해시 기반 후보 의미론.</summary>
        internal static ulong HashRow(string[] row)
        {
            const ulong Prime = 1099511628211UL;
            ulong h = 14695981039346656037UL;
            foreach (string f in row)
            {
                foreach (char ch in f) h = (h ^ ch) * Prime;
                h = (h ^ 0x1F) * Prime;
            }
            return h;
        }

        // ---------------------------------------------------------------- 병합

        private sealed class MergedCol
        {
            public long Empty, Whitespace, NullTok, NonNull;
            public long TypeViol; public List<QualityExample> TypeEx = new();
            public long CodeViol; public List<QualityExample> CodeEx = new();
            public long NumCount; public double Min, Max, Mean, M2;
            public List<double> Sample = new(); public bool SampleApproximate;
            public long Future; public List<QualityExample> FutureEx = new();
            public bool HasTemporal; public DateTime TMin, TMax;
            public Dictionary<string, long> Freq = new(StringComparer.Ordinal);
            public bool FreqOverflow;
            public long[] NumSent = new long[NumericSentinels.Length];
            public long[] TextSent = new long[TextSentinels.Length];
            public long[] DateSent = new long[DateSentinels.Length];
        }

        private sealed class Merged
        {
            public MergedCol[] Cols = Array.Empty<MergedCol>();
            public long Ragged; public List<QualityExample> RaggedEx = new();
            public Dictionary<ulong, long>? RowHashes;
        }

        private static Merged MergePartitions(PartitionAcc[] parts, int cols, QualityScanOptions options)
        {
            var m = new Merged { Cols = new MergedCol[cols] };
            for (int c = 0; c < cols; c++)
            {
                var g = new MergedCol { Min = double.PositiveInfinity, Max = double.NegativeInfinity, TMin = DateTime.MaxValue, TMax = DateTime.MinValue };
                foreach (var p in parts)
                {
                    var a = p.Cols[c];
                    g.Empty += a.Empty; g.Whitespace += a.Whitespace; g.NullTok += a.NullTok; g.NonNull += a.NonNull;
                    g.TypeViol += a.TypeViol; g.CodeViol += a.CodeViol;
                    MergeExamples(g.TypeEx, a.TypeEx); MergeExamples(g.CodeEx, a.CodeEx);
                    g.Future += a.Future; MergeExamples(g.FutureEx, a.FutureEx);
                    if (a.HasTemporal)
                    {
                        g.HasTemporal = true;
                        if (a.TMin < g.TMin) g.TMin = a.TMin;
                        if (a.TMax > g.TMax) g.TMax = a.TMax;
                    }
                    if (a.NumCount > 0)
                    {
                        if (a.Min < g.Min) g.Min = a.Min;
                        if (a.Max > g.Max) g.Max = a.Max;
                        // Welford 병렬 결합(Chan et al.)
                        long n1 = g.NumCount, n2 = a.NumCount;
                        double delta = a.Mean - g.Mean;
                        long n = n1 + n2;
                        g.Mean += delta * n2 / n;
                        g.M2 += a.M2 + delta * delta * n1 * n2 / (double)n;
                        g.NumCount = n;
                    }
                    if (a.Stride > 1) g.SampleApproximate = true;
                    g.Sample.AddRange(a.Sample);
                    // 병합 표본에도 상한을 적용해 dop에 비례한 메모리 증가를 막는다(파티션당 cap만으로는 부족).
                    if (g.Sample.Count > options.NumericSampleCap * 2)
                    { CompactSample(g.Sample, options.NumericSampleCap); g.SampleApproximate = true; }
                    foreach (var kv in a.Freq)
                    {
                        if (g.Freq.TryGetValue(kv.Key, out long cur)) g.Freq[kv.Key] = cur + kv.Value;
                        else if (g.Freq.Count < options.DistinctCapMerged) g.Freq[kv.Key] = kv.Value;
                        else g.FreqOverflow = true;
                    }
                    if (a.FreqOverflow) g.FreqOverflow = true;
                    for (int i = 0; i < g.NumSent.Length; i++) g.NumSent[i] += a.NumSent[i];
                    for (int i = 0; i < g.TextSent.Length; i++) g.TextSent[i] += a.TextSent[i];
                    for (int i = 0; i < g.DateSent.Length; i++) g.DateSent[i] += a.DateSent[i];
                }
                m.Cols[c] = g;
            }

            // 병합 사전을 파티션 개수 합으로 미리 예약(중복 병합으로 실제는 더 작지만 리사이즈 가비지 제거).
            long hashCap = 0;
            foreach (var p in parts) if (p.RowHashes is { } h) hashCap += h.Count;
            if (hashCap > 0) m.RowHashes = new Dictionary<ulong, long>((int)Math.Min(hashCap, int.MaxValue));

            foreach (var p in parts)
            {
                m.Ragged += p.Ragged;
                MergeExamples(m.RaggedEx, p.RaggedEx);
                if (p.RowHashes is { } src)
                {
                    foreach (var kv in src)
                        m.RowHashes![kv.Key] = m.RowHashes.TryGetValue(kv.Key, out long cur) ? cur + kv.Value : kv.Value;
                    p.RowHashes = null; // 파티션 사전을 조기 해제
                }
            }
            return m;
        }

        private static void MergeExamples(List<QualityExample> into, List<QualityExample> from)
        {
            foreach (var e in from) into.Add(e);
        }

        // 결정적 스트라이드 축약: cap 이하가 될 때까지 짝수 인덱스만 남긴다(AddNumeric과 동일 규칙).
        private static void CompactSample(List<double> sample, int cap)
        {
            while (sample.Count > cap)
            {
                int w = 0;
                for (int i = 0; i < sample.Count; i += 2) sample[w++] = sample[i];
                sample.RemoveRange(w, sample.Count - w);
            }
        }

        private static IReadOnlyList<QualityExample> TakeExamples(List<QualityExample> all, int max)
            => all.OrderBy(e => e.SourceRow).Take(max).ToArray();

        // ---------------------------------------------------------------- 프로파일·발견 생성

        private static IReadOnlyList<QualityColumnProfile> BuildProfiles(QualityScanSource source, Merged m, int cols)
        {
            var list = new QualityColumnProfile[cols];
            for (int c = 0; c < cols; c++)
            {
                var g = m.Cols[c];
                double? std = g.NumCount > 1 ? Math.Sqrt(g.M2 / g.NumCount) : (g.NumCount == 1 ? 0 : null);
                list[c] = new QualityColumnProfile
                {
                    Index = c,
                    Name = c < source.Headers.Count ? source.Headers[c] : $"Column{c + 1}",
                    ExpectedType = source.ColumnTypes is { } t && c < t.Count ? t[c] : ColumnValueType.String,
                    EmptyCount = g.Empty,
                    WhitespaceCount = g.Whitespace,
                    NullTokenCount = g.NullTok,
                    NonNullCount = g.NonNull,
                    DistinctCount = g.Freq.Count,
                    DistinctIsLowerBound = g.FreqOverflow,
                    TypeViolationCount = g.TypeViol,
                    CodebookViolationCount = g.CodeViol,
                    NumericMin = g.NumCount > 0 ? g.Min : null,
                    NumericMax = g.NumCount > 0 ? g.Max : null,
                    NumericMean = g.NumCount > 0 ? g.Mean : null,
                    NumericStdDev = std,
                    TemporalMin = g.HasTemporal ? g.TMin.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : null,
                    TemporalMax = g.HasTemporal ? g.TMax.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : null,
                };
            }
            return list;
        }

        private static IReadOnlyList<QualityFinding> BuildFindings(QualityScanSource source,
            QualityScanOptions options, Checker[] checkers, Merged m, int rows, int cols)
        {
            var findings = new List<QualityFinding>();
            var allowed = source.AllowedCodes;

            for (int c = 0; c < cols; c++)
            {
                var g = m.Cols[c];
                string name = c < source.Headers.Count ? source.Headers[c] : $"Column{c + 1}";
                int col = c;

                long missing = g.Empty + g.Whitespace + g.NullTok;

                // 전부 결측 컬럼 — 100% 결측이므로 심각도는 결측률에 단조가 되도록 Critical
                // (60~99.9% 결측 MissingRate가 Critical인데 100%가 그보다 낮으면 역전).
                if (rows > 0 && g.NonNull == 0)
                {
                    findings.Add(new QualityFinding
                    {
                        Kind = QualityCheckKind.EmptyColumn,
                        Dimension = QualityDimension.Completeness,
                        Severity = QualitySeverity.Critical,
                        Column = c, ColumnName = name,
                        ViolationCount = rows, EvaluatedRows = rows,
                    });
                    continue; // 나머지 검사는 무의미
                }

                // 결측률
                if (rows > 0 && missing > 0)
                {
                    double share = (double)missing / rows;
                    QualitySeverity? sev =
                        share >= options.MissingCriticalShare ? QualitySeverity.Critical :
                        share >= options.MissingWarnShare ? QualitySeverity.Warning :
                        share >= options.MissingInfoShare ? QualitySeverity.Info : null;
                    if (sev is { } s)
                        findings.Add(new QualityFinding
                        {
                            Kind = QualityCheckKind.MissingRate,
                            Dimension = QualityDimension.Completeness,
                            Severity = s,
                            Column = c, ColumnName = name,
                            ViolationCount = missing, EvaluatedRows = rows,
                            Breakdown = MissingBreakdown(g),
                            ViolationPredicate = row => IsMissing(Cell(row, col).Trim()),
                        });
                }

                // 위장결측 후보(sentinel) — 값별 임계 + 수치/날짜는 분포 끝값 조건
                var sentinel = BuildSentinelFinding(options, g, c, name, rows);
                if (sentinel is not null) findings.Add(sentinel);

                // 타입 부합
                if (g.TypeViol > 0)
                {
                    var checker = checkers[c];
                    findings.Add(new QualityFinding
                    {
                        Kind = QualityCheckKind.TypeConformance,
                        Dimension = QualityDimension.Conformance,
                        Severity = QualitySeverity.Warning,
                        Column = c, ColumnName = name,
                        ViolationCount = g.TypeViol, EvaluatedRows = rows,
                        Examples = TakeExamples(g.TypeEx, options.MaxExamples),
                        ViolationPredicate = row =>
                        {
                            string v = Cell(row, col).Trim();
                            return !IsMissing(v) && ViolatesType(checker, v);
                        },
                    });
                }

                // 코드북 대조(값 라벨 집합 밖 값) — 선언 메타데이터 위반은 Critical
                if (g.CodeViol > 0 && allowed is not null && c < allowed.Count && allowed[c] is { } set)
                {
                    findings.Add(new QualityFinding
                    {
                        Kind = QualityCheckKind.CodebookConformance,
                        Dimension = QualityDimension.Conformance,
                        Severity = QualitySeverity.Critical,
                        Column = c, ColumnName = name,
                        ViolationCount = g.CodeViol, EvaluatedRows = rows,
                        Examples = TakeExamples(g.CodeEx, options.MaxExamples),
                        ViolationPredicate = row =>
                        {
                            string v = Cell(row, col).Trim();
                            return !IsMissing(v) && !set.Contains(v);
                        },
                    });
                }

                // 상수/거의상수
                if (g.NonNull > 0 && rows > 1)
                {
                    if (!g.FreqOverflow && g.Freq.Count == 1)
                    {
                        findings.Add(new QualityFinding
                        {
                            Kind = QualityCheckKind.ConstantColumn,
                            Dimension = QualityDimension.Plausibility,
                            Severity = QualitySeverity.Info,
                            Column = c, ColumnName = name,
                            ViolationCount = g.NonNull, EvaluatedRows = rows,
                            Label = g.Freq.Keys.First(),
                        });
                    }
                    else if (g.Freq.Count > 1)
                    {
                        // 상한 이후 새 키는 계수되지 않으므로 top1 점유율은 하한 — 보수적 판정.
                        var top = g.Freq.OrderByDescending(kv => kv.Value)
                            .ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
                        if ((double)top.Value / g.NonNull >= options.NearConstantShare)
                        {
                            string topValue = top.Key;
                            findings.Add(new QualityFinding
                            {
                                Kind = QualityCheckKind.ConstantColumn,
                                Dimension = QualityDimension.Plausibility,
                                Severity = QualitySeverity.Info,
                                Column = c, ColumnName = name,
                                ViolationCount = g.NonNull - top.Value, EvaluatedRows = rows,
                                Label = topValue,
                                Breakdown = new[] { new ValueCount(topValue, top.Value) },
                                ViolationPredicate = row =>
                                {
                                    string v = Cell(row, col).Trim();
                                    return !IsMissing(v) && !string.Equals(v, topValue, StringComparison.Ordinal);
                                },
                            });
                        }
                    }
                }

                // 이상치(IQR ± 1.5) — 표본 축약 시 추정으로 표기
                if (g.NumCount >= 8 && g.Sample.Count >= 8)
                {
                    var sample = g.Sample.ToArray();
                    Array.Sort(sample);
                    double q1 = Quantile(sample, 0.25), q3 = Quantile(sample, 0.75);
                    double iqr = q3 - q1;
                    if (iqr > 0)
                    {
                        double lo = q1 - 1.5 * iqr, hi = q3 + 1.5 * iqr;
                        long sampleOut = 0;
                        foreach (double d in sample) if (d < lo || d > hi) sampleOut++;
                        if (sampleOut > 0)
                        {
                            bool approx = g.SampleApproximate;
                            long estimated = approx
                                ? (long)Math.Round(sampleOut * (double)g.NumCount / sample.Length)
                                : sampleOut;
                            findings.Add(new QualityFinding
                            {
                                Kind = QualityCheckKind.Outliers,
                                Dimension = QualityDimension.Plausibility,
                                Severity = QualitySeverity.Info,
                                Column = c, ColumnName = name,
                                ViolationCount = estimated, EvaluatedRows = rows,
                                Approximate = approx,
                                FenceLow = lo, FenceHigh = hi,
                                ViolationPredicate = row =>
                                    NumericAffix.TryParseNumber(Cell(row, col), out double d)
                                    && double.IsFinite(d) && (d < lo || d > hi),
                            });
                        }
                    }
                }

                // 미래 날짜
                if (g.Future > 0)
                {
                    DateTime today = options.Today;
                    findings.Add(new QualityFinding
                    {
                        Kind = QualityCheckKind.FutureDate,
                        Dimension = QualityDimension.Plausibility,
                        Severity = QualitySeverity.Warning,
                        Column = c, ColumnName = name,
                        ViolationCount = g.Future, EvaluatedRows = rows,
                        Examples = TakeExamples(g.FutureEx, options.MaxExamples),
                        ViolationPredicate = row =>
                            CsvDateParser.ParseDetailed(Cell(row, col).Trim(), true) is { } t
                            && t.Kind != TemporalKind.Time && t.Value.Date > today,
                    });
                }
            }

            // 구조: 필드 수 불일치
            if (m.Ragged > 0)
            {
                int expected = cols;
                findings.Add(new QualityFinding
                {
                    Kind = QualityCheckKind.RaggedRows,
                    Dimension = QualityDimension.Conformance,
                    Severity = QualitySeverity.Critical,
                    ViolationCount = m.Ragged, EvaluatedRows = rows,
                    Examples = TakeExamples(m.RaggedEx, options.MaxExamples),
                    ViolationPredicate = row => row.Length != expected,
                });
            }

            // 완전 중복 행(해시 기반)
            if (m.RowHashes is { } hashes)
            {
                long extraCopies = 0; long groups = 0;
                var dupList = new List<ulong>();
                foreach (var kv in hashes)
                    if (kv.Value >= 2) { groups++; extraCopies += kv.Value - 1; dupList.Add(kv.Key); }
                if (groups > 0)
                {
                    // 정렬 배열 + 이진탐색: HashSet 대비 항목당 8B로 줄여 발견이 살아있는 동안의 보존 메모리 절감.
                    ulong[] dupArr = dupList.ToArray();
                    Array.Sort(dupArr);
                    findings.Add(new QualityFinding
                    {
                        Kind = QualityCheckKind.DuplicateRows,
                        Dimension = QualityDimension.Plausibility,
                        Severity = QualitySeverity.Warning,
                        // ViolationCount=초과 사본. 술어는 그룹 전체(원본 포함)를 매칭하므로 필터 행 수는
                        // ViolationCount+groups가 된다 — UI Detail에 그룹 수를 함께 표기해 차이를 알린다.
                        ViolationCount = extraCopies, EvaluatedRows = rows,
                        Label = groups.ToString(CultureInfo.InvariantCulture),
                        ViolationPredicate = row => Array.BinarySearch(dupArr, HashRow(row)) >= 0,
                    });
                }
            }

            // 결정적 정렬: 심각도(높은 것 먼저) → 컬럼 → 종류
            return findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.Kind)
                .ToArray();
        }

        private static IReadOnlyList<ValueCount> MissingBreakdown(MergedCol g)
        {
            var list = new List<ValueCount>(3);
            if (g.Empty > 0) list.Add(new ValueCount("", g.Empty));
            if (g.Whitespace > 0) list.Add(new ValueCount(" ", g.Whitespace));
            if (g.NullTok > 0) list.Add(new ValueCount("NA", g.NullTok));
            return list;
        }

        private static QualityFinding? BuildSentinelFinding(QualityScanOptions options, MergedCol g,
            int col, string name, int rows)
        {
            if (g.NonNull == 0) return null;
            long threshold = Math.Max(options.SentinelMinCount, (long)Math.Ceiling(g.NonNull * options.SentinelMinShare));

            var breakdown = new List<ValueCount>();
            var textSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var numSet = new HashSet<double>();
            var dateSet = new HashSet<DateTime>();
            long total = 0;

            for (int i = 0; i < TextSentinels.Length; i++)
                if (g.TextSent[i] >= threshold)
                {
                    breakdown.Add(new ValueCount(TextSentinels[i], g.TextSent[i]));
                    textSet.Add(TextSentinels[i]);
                    total += g.TextSent[i];
                }

            for (int i = 0; i < NumericSentinels.Length; i++)
            {
                long n = g.NumSent[i];
                if (n < threshold) continue;
                double v = NumericSentinels[i];
                // 분포 끝값일 때만 후보(중간값 999는 합법일 수 있음 — 오탐 방지).
                if (g.NumCount > 0 && (v == g.Min || v == g.Max))
                {
                    breakdown.Add(new ValueCount(v.ToString("0.################", CultureInfo.InvariantCulture), n));
                    numSet.Add(v);
                    total += n;
                }
            }

            for (int i = 0; i < DateSentinels.Length; i++)
            {
                long n = g.DateSent[i];
                if (n < threshold) continue;
                DateTime v = DateSentinels[i];
                if (g.HasTemporal && (v == g.TMin.Date || v == g.TMax.Date))
                {
                    breakdown.Add(new ValueCount(v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), n));
                    dateSet.Add(v);
                    total += n;
                }
            }

            if (breakdown.Count == 0) return null;
            breakdown.Sort((a, b) =>
            {
                int byCount = b.Count.CompareTo(a.Count);
                return byCount != 0 ? byCount : string.Compare(a.Value, b.Value, StringComparison.Ordinal);
            });

            return new QualityFinding
            {
                Kind = QualityCheckKind.DisguisedMissing,
                Dimension = QualityDimension.Completeness,
                Severity = QualitySeverity.Warning,
                Column = col, ColumnName = name,
                ViolationCount = total, EvaluatedRows = rows,
                Breakdown = breakdown,
                ViolationPredicate = row =>
                {
                    string v = Cell(row, col).Trim();
                    if (IsMissing(v)) return false;
                    if (textSet.Contains(v)) return true;
                    if (numSet.Count > 0 && NumericAffix.TryParseNumber(v, out double d) && numSet.Contains(d)) return true;
                    if (dateSet.Count > 0 && CsvDateParser.ParseDetailed(v, true) is { } t
                        && t.Kind != TemporalKind.Time && dateSet.Contains(t.Value.Date)) return true;
                    return false;
                },
            };
        }

        /// <summary>정렬된 배열의 선형보간 분위수(Type 7 — 기존 통계 엔진과 동일 계열).</summary>
        internal static double Quantile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return double.NaN;
            if (sorted.Length == 1) return sorted[0];
            double h = (sorted.Length - 1) * p;
            int lo = (int)Math.Floor(h);
            int hi = Math.Min(lo + 1, sorted.Length - 1);
            return sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
        }
    }
}
