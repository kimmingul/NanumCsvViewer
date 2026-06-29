namespace NanumCsvViewer.Csv
{
    /// <summary>특정 컬럼의 선택값 필터(체크박스). 빈 값은 IncludeBlanks로 별도 처리.</summary>
    public sealed class SelectedValuesFilter
    {
        public int Column { get; set; }
        public List<string> Values { get; set; } = new();
        public bool IncludeBlanks { get; set; }
    }

    /// <summary>특정 컬럼의 날짜 범위 필터(양끝 포함, 빈 경계는 무한).</summary>
    public sealed class DateRangeFilter
    {
        public int Column { get; set; }
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
    }

    /// <summary>
    /// 컬럼별 구조화 필터 모음(선택값·날짜범위). 모든 필터를 AND로 결합한 술어로 컴파일하며 JSON 직렬화 가능.
    /// macOS ColumnFilterState 이식. 기존 텍스트/셀값 필터와 함께 동작.
    /// </summary>
    public sealed class ColumnFilterState
    {
        public List<SelectedValuesFilter> ValueFilters { get; set; } = new();
        public List<DateRangeFilter> DateFilters { get; set; } = new();

        public bool IsEmpty => ValueFilters.Count == 0 && DateFilters.Count == 0;

        public void SetValues(int column, IEnumerable<string> values, bool includeBlanks)
        {
            Remove(column);
            ValueFilters.Add(new SelectedValuesFilter
            {
                Column = column,
                Values = values.Where(v => v.Length > 0).Distinct().ToList(),
                IncludeBlanks = includeBlanks
            });
        }

        public void SetDateRange(int column, DateTime? start, DateTime? end)
        {
            Remove(column);
            if (start is null && end is null) return; // 빈 범위는 필터 없음
            DateFilters.Add(new DateRangeFilter { Column = column, Start = start, End = end });
        }

        /// <summary>해당 컬럼의 모든 필터 제거(선택값/날짜 한 컬럼은 상호 배타).</summary>
        public void Remove(int column)
        {
            ValueFilters.RemoveAll(f => f.Column == column);
            DateFilters.RemoveAll(f => f.Column == column);
        }

        public void Clear()
        {
            ValueFilters.Clear();
            DateFilters.Clear();
        }

        public bool HasFilterFor(int column)
            => ValueFilters.Any(f => f.Column == column) || DateFilters.Any(f => f.Column == column);

        /// <summary>모든 컬럼 필터를 AND로 결합한 행 술어.</summary>
        public Func<string[], bool> Predicate()
        {
            // 스냅샷(스레드 안전): 셋을 미리 만든다.
            var valueFilters = ValueFilters
                .Select(f => (f.Column, Set: new HashSet<string>(f.Values, StringComparer.Ordinal), f.IncludeBlanks))
                .ToArray();
            var dateFilters = DateFilters
                .Select(f => (f.Column, Start: f.Start?.Date, End: f.End?.Date))
                .ToArray();

            return row =>
            {
                foreach (var (col, set, includeBlanks) in valueFilters)
                {
                    string v = col >= 0 && col < row.Length ? row[col] : string.Empty;
                    if (v.Length == 0) { if (!includeBlanks) return false; }
                    else if (!set.Contains(v)) return false;
                }
                foreach (var (col, start, end) in dateFilters)
                {
                    string raw = col >= 0 && col < row.Length ? row[col] : string.Empty;
                    var date = CsvDateParser.Parse(raw, allowCompactNumeric: true);
                    if (date is null) return false;
                    DateTime d = date.Value.Date;
                    if (start is DateTime s && d < s) return false;
                    if (end is DateTime e && d > e) return false;
                }
                return true;
            };
        }

        public IEnumerable<string> Descriptions(IReadOnlyList<string> headers)
        {
            string Name(int c) => c >= 0 && c < headers.Count && headers[c].Length > 0 ? headers[c] : $"Column{c + 1}";

            foreach (var f in ValueFilters)
            {
                int n = f.Values.Count + (f.IncludeBlanks ? 1 : 0);
                yield return $"{Name(f.Column)} ∈ {n}";
            }
            foreach (var f in DateFilters)
            {
                string s = f.Start?.ToString("yyyy-MM-dd") ?? "…";
                string e = f.End?.ToString("yyyy-MM-dd") ?? "…";
                yield return $"{Name(f.Column)} {s}~{e}";
            }
        }
    }
}
