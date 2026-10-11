using System.Text.RegularExpressions;

namespace NanumCsvViewer.Csv
{
    /// <summary>정규식 바꾸기로 바뀔 셀 하나. DataRow = 행 id(편집 덮개의 키, doc.GetRowId가 돌려주는 값).</summary>
    public sealed record RegexCellChange(long DataRow, int Column, string OldValue, string NewValue);

    /// <summary>바꾸기 계획. Truncated = maxChanges에 걸려 스캔을 멈췄다(Changes는 앞쪽 maxChanges개뿐이다).</summary>
    public sealed record RegexReplacePlan(IReadOnlyList<RegexCellChange> Changes, long RowsScanned, long CellsMatched, long CellsTimedOut, bool Truncated);

    /// <summary>일치 요약(값은 바꾸지 않음). Samples = 앞쪽 일치 셀.</summary>
    public sealed record RegexMatchSummary(long RowsScanned, long CellsMatched, long RowsMatched, long CellsTimedOut,
        IReadOnlyList<(long DataRow, int Column, string Value)> Samples);

    /// <summary>바꾸기 미리보기: 결과를 쌓지 않고 전체 개수만 센다(입력하는 동안 반복 실행해도 메모리가 일정).</summary>
    public sealed record RegexReplacePreview(long RowsScanned, long CellsMatched, long CellsChanged, long CellsTimedOut,
        IReadOnlyList<RegexCellChange> Samples);

    /// <summary>정규식 추출 계획. Values = 값이 비어 있지 않은 행만(O(비어 있지 않은 값)).</summary>
    public sealed record RegexExtractPlan(IReadOnlyList<(long DataRow, string Value)> Values, long RowsScanned, long RowsMatched,
        long RowsNotMatched, long CellsTimedOut, bool Truncated);

    /// <summary>패턴 시험 결과(앞쪽 일부 행만 검사).</summary>
    public sealed record RegexTestResult(long RowsScanned, long CellsMatched, long RowsMatched, long CellsTimedOut, IReadOnlyList<string> Samples);

    /// <summary>
    /// 정규식 찾아 바꾸기 / 추출 엔진(UI 없음). 행은 호출자가 준 순서대로, 한 행 안에서는 컬럼 번호 오름차순으로 훑는다(결정적).
    /// 행이 모자란 셀은 빈 문자열로 본다. 셀 하나가 시간 제한(<see cref="RegexSafety.MatchTimeout"/>)을 넘기면 일치 없음으로 처리하되
    /// 반드시 CellsTimedOut에 센다 — 호출자가 사용자에게 알려야 한다. 취소는 1024행마다 확인한다.
    /// 바꿀 문자열은 .NET 치환 문법($1, ${name}, $$)이다.
    /// </summary>
    public static class RegexReplace
    {
        /// <summary>패턴 시험이 검사하는 최대 행 수.</summary>
        public const int TestMaxRows = 10_000;
        /// <summary>패턴 시험이 보여 주는 최대 표본 수.</summary>
        public const int TestMaxSamples = 10;

        private const int CancelMask = 1023;

        /// <summary>
        /// 바꿀 셀 목록을 만든다. 바꾼 결과가 기존 값과 같은 셀은 변경이 아니다(CellsMatched에는 포함).
        /// 변경이 maxChanges를 넘으면 그 시점에 멈추고 Truncated=true(변경 목록은 앞쪽 maxChanges개).
        /// </summary>
        public static RegexReplacePlan Plan(Func<long, string[]> rowAt, IEnumerable<long> rows, IReadOnlyList<int> columns,
            Regex regex, string replacement, int maxChanges, CancellationToken ct)
        {
            var cols = SortedColumns(columns);
            var changes = new List<RegexCellChange>();
            long scanned = 0, matched = 0, timedOut = 0;
            bool truncated = false;

            foreach (long r in rows)
            {
                if ((scanned & CancelMask) == 0) ct.ThrowIfCancellationRequested();
                scanned++;
                var row = rowAt(r);
                foreach (int c in cols)
                {
                    string value = c < row.Length ? row[c] : string.Empty;
                    string replaced;
                    try
                    {
                        if (!regex.Match(value).Success) continue;
                        replaced = regex.Replace(value, replacement);
                    }
                    catch (RegexMatchTimeoutException) { timedOut++; continue; }
                    matched++;
                    if (string.Equals(replaced, value, StringComparison.Ordinal)) continue;
                    if (changes.Count >= maxChanges) { truncated = true; break; }
                    changes.Add(new RegexCellChange(r, c, value, replaced));
                }
                if (truncated) break;
            }
            return new RegexReplacePlan(changes, scanned, matched, timedOut, truncated);
        }

        /// <summary>일치하는 셀 수·행 수와 앞쪽 maxSamples개 표본을 센다(값은 바꾸지 않음).</summary>
        public static RegexMatchSummary Count(Func<long, string[]> rowAt, IEnumerable<long> rows, IReadOnlyList<int> columns,
            Regex regex, int maxSamples, CancellationToken ct)
        {
            var cols = SortedColumns(columns);
            var samples = new List<(long, int, string)>();
            long scanned = 0, matched = 0, rowsMatched = 0, timedOut = 0;

            foreach (long r in rows)
            {
                if ((scanned & CancelMask) == 0) ct.ThrowIfCancellationRequested();
                scanned++;
                var row = rowAt(r);
                bool any = false;
                foreach (int c in cols)
                {
                    string value = c < row.Length ? row[c] : string.Empty;
                    try { if (!regex.IsMatch(value)) continue; }
                    catch (RegexMatchTimeoutException) { timedOut++; continue; }
                    matched++;
                    any = true;
                    if (samples.Count < maxSamples) samples.Add((r, c, value));
                }
                if (any) rowsMatched++;
            }
            return new RegexMatchSummary(scanned, matched, rowsMatched, timedOut, samples);
        }

        /// <summary>
        /// 바꾸기 미리보기. 변경 목록을 쌓지 않고 전체 범위의 일치·변경·시간 초과 셀 수와 앞쪽 maxSamples개 변경 표본을 돌려준다.
        /// </summary>
        public static RegexReplacePreview Preview(Func<long, string[]> rowAt, IEnumerable<long> rows, IReadOnlyList<int> columns,
            Regex regex, string replacement, int maxSamples, CancellationToken ct)
        {
            var cols = SortedColumns(columns);
            var samples = new List<RegexCellChange>();
            long scanned = 0, matched = 0, changed = 0, timedOut = 0;

            foreach (long r in rows)
            {
                if ((scanned & CancelMask) == 0) ct.ThrowIfCancellationRequested();
                scanned++;
                var row = rowAt(r);
                foreach (int c in cols)
                {
                    string value = c < row.Length ? row[c] : string.Empty;
                    string replaced;
                    try
                    {
                        if (!regex.Match(value).Success) continue;
                        replaced = regex.Replace(value, replacement);
                    }
                    catch (RegexMatchTimeoutException) { timedOut++; continue; }
                    matched++;
                    if (string.Equals(replaced, value, StringComparison.Ordinal)) continue;
                    changed++;
                    if (samples.Count < maxSamples) samples.Add(new RegexCellChange(r, c, value, replaced));
                }
            }
            return new RegexReplacePreview(scanned, matched, changed, timedOut, samples);
        }

        /// <summary>패턴 시험: 앞쪽 <see cref="TestMaxRows"/>행에서 일치하는 셀 수와 최대 <see cref="TestMaxSamples"/>개 값.</summary>
        public static RegexTestResult TestPattern(Func<long, string[]> rowAt, IEnumerable<long> rows, IReadOnlyList<int> columns,
            Regex regex, CancellationToken ct)
        {
            var s = Count(rowAt, rows.Take(TestMaxRows), columns, regex, TestMaxSamples, ct);
            return new RegexTestResult(s.RowsScanned, s.CellsMatched, s.RowsMatched, s.CellsTimedOut, s.Samples.Select(x => x.Value).ToList());
        }

        /// <summary>
        /// 변경 목록을 문서의 편집 덮개에 한 단계(되돌리기 한 번)로 기록하고, 실제로 값이 바뀐 셀 수를 돌려준다.
        /// 줄바꿈은 셀 원래 값의 스타일에 맞춘다. 삭제된 행·범위 밖 컬럼은 건너뛴다. 확인창 없음(UI는 호출자 몫).
        /// </summary>
        public static int Apply(VirtualCsvDocument doc, IReadOnlyList<RegexCellChange> changes, string description)
        {
            if (changes.Count == 0) return 0;
            var edits = doc.Edits;
            int changed = 0;
            using (edits.BeginStep(description))
            {
                int currentRow = int.MinValue;
                string[] original = Array.Empty<string>();
                foreach (var change in changes)
                {
                    int rowId = (int)change.DataRow;
                    if (change.Column < 0 || change.Column >= doc.ColumnCount || edits.IsDeleted(rowId)) continue;
                    if (rowId != currentRow) { original = doc.GetOriginalRow(rowId); currentRow = rowId; }
                    string orig = change.Column < original.Length ? original[change.Column] : "";
                    string value = CellEdits.MatchNewlineStyle(change.NewValue, orig);
                    string before = edits.TryGet(rowId, change.Column, out string? cur) ? cur! : orig;
                    if (!string.Equals(before, value, StringComparison.Ordinal)) changed++;
                    edits.Set(rowId, change.Column, value, orig);
                }
            }
            return changed;
        }

        private static int[] SortedColumns(IReadOnlyList<int> columns)
            => columns.Where(c => c >= 0).Distinct().OrderBy(c => c).ToArray();
    }

    /// <summary>정규식 추출: 한 컬럼의 값에서 캡처 그룹을 뽑아 새 컬럼의 값으로 쓴다.</summary>
    public static class RegexExtract
    {
        /// <summary>
        /// 뽑을 그룹 번호. spec이 비었으면 첫 캡처 그룹(1번), 그룹이 없으면 전체 일치(0).
        /// 숫자면 그 번호, 아니면 그룹 이름. 없는 그룹이면 RegexPatternException.
        /// </summary>
        public static int ResolveGroup(Regex regex, string? spec)
        {
            int[] numbers = regex.GetGroupNumbers();
            if (string.IsNullOrWhiteSpace(spec)) return numbers.Length > 1 ? numbers[1] : 0;
            spec = spec.Trim();
            if (int.TryParse(spec, out int n))
            {
                if (Array.IndexOf(numbers, n) < 0)
                    throw new RegexPatternException($"그룹 {n}번이 패턴에 없습니다(그룹 번호: {string.Join(", ", numbers)}).");
                return n;
            }
            int byName = regex.GroupNumberFromName(spec);
            if (byName < 0) throw new RegexPatternException($"그룹 이름 '{spec}'이(가) 패턴에 없습니다.");
            return byName;
        }

        /// <summary>
        /// 원본 컬럼의 값마다 패턴을 적용해 group 값을 모은다. 일치하지 않거나 그룹이 참여하지 않은 행·시간 초과 셀은 값 없음(빈 값)이다.
        /// 값이 비어 있지 않은 행만 Values에 담는다. 값이 maxValues를 넘으면 멈추고 Truncated=true.
        /// </summary>
        public static RegexExtractPlan Plan(Func<long, string[]> rowAt, IEnumerable<long> rows, int sourceColumn, Regex regex, int group,
            int maxValues, CancellationToken ct)
        {
            var values = new List<(long, string)>();
            long scanned = 0, matched = 0, notMatched = 0, timedOut = 0;
            bool truncated = false;

            foreach (long r in rows)
            {
                if ((scanned & 1023) == 0) ct.ThrowIfCancellationRequested();
                scanned++;
                var row = rowAt(r);
                string value = sourceColumn >= 0 && sourceColumn < row.Length ? row[sourceColumn] : string.Empty;
                string? extracted = null;
                try
                {
                    var m = regex.Match(value);
                    if (m.Success && m.Groups[group].Success) extracted = m.Groups[group].Value;
                }
                catch (RegexMatchTimeoutException) { timedOut++; continue; }
                if (extracted is null) { notMatched++; continue; }
                matched++;
                if (extracted.Length == 0) continue;
                if (values.Count >= maxValues) { truncated = true; break; }
                values.Add((r, extracted));
            }
            return new RegexExtractPlan(values, scanned, matched, notMatched, timedOut, truncated);
        }
    }
}
