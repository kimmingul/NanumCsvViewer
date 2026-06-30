using System.Globalization;
using System.Text.RegularExpressions;

namespace NanumCsvViewer.Csv
{
    /// <summary>특정 컬럼의 선택값 필터(체크박스). 빈 값은 IncludeBlanks로 별도 처리.</summary>
    public sealed class SelectedValuesFilter
    {
        public int Column { get; set; }
        public List<string> Values { get; set; } = new();
        public bool IncludeBlanks { get; set; }
    }

    /// <summary>시간 범위 필터의 비교 정밀도.</summary>
    public enum TemporalFilterKind { Date, DateTime, Time }

    /// <summary>텍스트 필터 연산.</summary>
    public enum TextFilterOp { Contains, Equals, StartsWith, EndsWith, Regex, IsBlank, IsNotBlank, InList }

    /// <summary>특정 컬럼의 시간 범위 필터(양끝 포함, 빈 경계는 무한). Kind에 따라 날짜/일시/시각 정밀도로 비교.</summary>
    public sealed class DateRangeFilter
    {
        public int Column { get; set; }
        public DateTime? Start { get; set; }
        public DateTime? End { get; set; }
        public TemporalFilterKind Kind { get; set; } = TemporalFilterKind.Date;
    }

    /// <summary>특정 컬럼의 숫자 범위 필터(양끝 포함, 빈 경계는 무한).</summary>
    public sealed class NumericRangeFilter
    {
        public int Column { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    /// <summary>특정 컬럼의 텍스트 술어 필터(문자열/식별자 등).</summary>
    public sealed class TextFilter
    {
        public int Column { get; set; }
        public TextFilterOp Op { get; set; }
        public string Value { get; set; } = "";
        public bool CaseSensitive { get; set; }
    }

    /// <summary>
    /// 컬럼별 구조화 필터 모음(선택값·날짜범위). 모든 필터를 AND로 결합한 술어로 컴파일하며 JSON 직렬화 가능.
    /// macOS ColumnFilterState 이식. 기존 텍스트/셀값 필터와 함께 동작.
    /// </summary>
    public sealed class ColumnFilterState
    {
        public List<SelectedValuesFilter> ValueFilters { get; set; } = new();
        public List<DateRangeFilter> DateFilters { get; set; } = new();
        public List<NumericRangeFilter> NumericFilters { get; set; } = new();
        public List<TextFilter> TextFilters { get; set; } = new();

        public bool IsEmpty => ValueFilters.Count == 0 && DateFilters.Count == 0
            && NumericFilters.Count == 0 && TextFilters.Count == 0;

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

        public void SetDateRange(int column, DateTime? start, DateTime? end,
            TemporalFilterKind kind = TemporalFilterKind.Date)
        {
            Remove(column);
            if (start is null && end is null) return; // 빈 범위는 필터 없음
            DateFilters.Add(new DateRangeFilter { Column = column, Start = start, End = end, Kind = kind });
        }

        public void SetNumericRange(int column, double? min, double? max)
        {
            Remove(column);
            if (min is null && max is null) return; // 빈 범위는 필터 없음
            NumericFilters.Add(new NumericRangeFilter { Column = column, Min = min, Max = max });
        }

        public void SetText(int column, TextFilterOp op, string value, bool caseSensitive)
        {
            Remove(column);
            // IsBlank/IsNotBlank가 아니면서 값이 비면 필터 없음으로 간주.
            if (op is not (TextFilterOp.IsBlank or TextFilterOp.IsNotBlank) && value.Length == 0) return;
            TextFilters.Add(new TextFilter { Column = column, Op = op, Value = value, CaseSensitive = caseSensitive });
        }

        /// <summary>해당 컬럼의 모든 필터 제거(한 컬럼당 한 종류만 활성, 상호 배타).</summary>
        public void Remove(int column)
        {
            ValueFilters.RemoveAll(f => f.Column == column);
            DateFilters.RemoveAll(f => f.Column == column);
            NumericFilters.RemoveAll(f => f.Column == column);
            TextFilters.RemoveAll(f => f.Column == column);
        }

        public void Clear()
        {
            ValueFilters.Clear();
            DateFilters.Clear();
            NumericFilters.Clear();
            TextFilters.Clear();
        }

        /// <summary>다른 상태의 모든 필터(값/시간/숫자/텍스트)를 깊은 복사로 대체. 저장된 뷰 복원에 사용.</summary>
        public void CopyFrom(ColumnFilterState other)
        {
            Clear();
            ValueFilters.AddRange(other.ValueFilters.Select(f =>
                new SelectedValuesFilter { Column = f.Column, Values = new List<string>(f.Values), IncludeBlanks = f.IncludeBlanks }));
            DateFilters.AddRange(other.DateFilters.Select(f =>
                new DateRangeFilter { Column = f.Column, Start = f.Start, End = f.End, Kind = f.Kind }));
            NumericFilters.AddRange(other.NumericFilters.Select(f =>
                new NumericRangeFilter { Column = f.Column, Min = f.Min, Max = f.Max }));
            TextFilters.AddRange(other.TextFilters.Select(f =>
                new TextFilter { Column = f.Column, Op = f.Op, Value = f.Value, CaseSensitive = f.CaseSensitive }));
        }

        public bool HasFilterFor(int column)
            => ValueFilters.Any(f => f.Column == column) || DateFilters.Any(f => f.Column == column)
            || NumericFilters.Any(f => f.Column == column) || TextFilters.Any(f => f.Column == column);

        /// <summary>모든 컬럼 필터를 AND로 결합한 행 술어.</summary>
        public Func<string[], bool> Predicate()
        {
            var preds = IndividualPredicates();
            return row =>
            {
                for (int i = 0; i < preds.Count; i++)
                    if (!preds[i](row)) return false;
                return true;
            };
        }

        /// <summary>필터 하나당 술어를 하나씩 컴파일한 목록. AND는 Predicate(), OR(any)는 호출부에서 결합.</summary>
        public IReadOnlyList<Func<string[], bool>> IndividualPredicates()
            => CompileFilters().ToArray();

        private IEnumerable<Func<string[], bool>> CompileFilters()
        {
            // 각 술어는 자신의 스냅샷을 클로저로 캡처(스레드 안전).
            foreach (var f in ValueFilters)
            {
                int col = f.Column;
                var set = new HashSet<string>(f.Values, StringComparer.Ordinal);
                bool includeBlanks = f.IncludeBlanks;
                yield return row =>
                {
                    string v = Cell(row, col);
                    return v.Length == 0 ? includeBlanks : set.Contains(v);
                };
            }
            foreach (var f in DateFilters)
            {
                int col = f.Column; var start = f.Start; var end = f.End; var kind = f.Kind;
                yield return row =>
                {
                    var parsed = CsvDateParser.ParseDetailed(Cell(row, col), allowCompactNumeric: true);
                    return parsed is not null && InTemporalRange(parsed.Value.Value, start, end, kind);
                };
            }
            foreach (var f in NumericFilters)
            {
                int col = f.Column; var min = f.Min; var max = f.Max;
                yield return row =>
                {
                    if (!double.TryParse(Cell(row, col), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                        return false;
                    if (min is double mn && d < mn) return false;
                    if (max is double mx && d > mx) return false;
                    return true;
                };
            }
            foreach (var f in TextFilters)
            {
                int col = f.Column; var op = f.Op; var value = f.Value; bool cs = f.CaseSensitive;
                var regex = CompileRegex(f); var list = BuildList(f);
                yield return row => TextMatches(Cell(row, col), op, value, cs, regex, list);
            }
        }

        private static string Cell(string[] row, int col)
            => col >= 0 && col < row.Length ? row[col] : string.Empty;

        private static bool InTemporalRange(DateTime value, DateTime? start, DateTime? end, TemporalFilterKind kind)
        {
            switch (kind)
            {
                case TemporalFilterKind.Time:
                    TimeSpan t = value.TimeOfDay;
                    if (start is DateTime s1 && t < s1.TimeOfDay) return false;
                    if (end is DateTime e1 && t > e1.TimeOfDay) return false;
                    return true;
                case TemporalFilterKind.DateTime:
                    if (start is DateTime s2 && value < s2) return false;
                    if (end is DateTime e2 && value > e2) return false;
                    return true;
                default: // Date — 시각을 버리고 날짜만 비교
                    DateTime d = value.Date;
                    if (start is DateTime s3 && d < s3.Date) return false;
                    if (end is DateTime e3 && d > e3.Date) return false;
                    return true;
            }
        }

        private static Regex? CompileRegex(TextFilter f)
        {
            if (f.Op != TextFilterOp.Regex) return null;
            try
            {
                var opts = RegexOptions.CultureInvariant | (f.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return new Regex(f.Value, opts);
            }
            catch (ArgumentException) { return null; } // 잘못된 정규식 → 매칭 없음
        }

        // InList: 값을 줄바꿈/쉼표로 분리한 집합. 그 외 연산은 null.
        private static HashSet<string>? BuildList(TextFilter f)
        {
            if (f.Op != TextFilterOp.InList) return null;
            var set = new HashSet<string>(f.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var tok in f.Value.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(tok);
            return set;
        }

        private static bool TextMatches(string v, TextFilterOp op, string value, bool caseSensitive, Regex? regex, HashSet<string>? list)
        {
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return op switch
            {
                TextFilterOp.IsBlank => v.Length == 0,
                TextFilterOp.IsNotBlank => v.Length > 0,
                TextFilterOp.Contains => v.Contains(value, cmp),
                TextFilterOp.Equals => v.Equals(value, cmp),
                TextFilterOp.StartsWith => v.StartsWith(value, cmp),
                TextFilterOp.EndsWith => v.EndsWith(value, cmp),
                TextFilterOp.Regex => regex is not null && regex.IsMatch(v),
                TextFilterOp.InList => list is not null && list.Contains(v),
                _ => true
            };
        }

        public IEnumerable<string> Descriptions(IReadOnlyList<string> headers)
            => DescribeEntries(headers).Select(e => e.Text);

        /// <summary>활성 필터를 (출처 컬럼, 설명) 쌍으로 열거. 칩 바 등 개별 제거 UI에서 사용.</summary>
        public IEnumerable<(int Column, string Text)> DescribeEntries(IReadOnlyList<string> headers)
        {
            string Name(int c) => c >= 0 && c < headers.Count && headers[c].Length > 0 ? headers[c] : $"Column{c + 1}";

            foreach (var f in ValueFilters)
            {
                int n = f.Values.Count + (f.IncludeBlanks ? 1 : 0);
                yield return (f.Column, $"{Name(f.Column)} ∈ {n}");
            }
            foreach (var f in DateFilters)
            {
                string fmt = f.Kind switch
                {
                    TemporalFilterKind.Time => "HH:mm:ss",
                    TemporalFilterKind.DateTime => "yyyy-MM-dd HH:mm",
                    _ => "yyyy-MM-dd"
                };
                string s = f.Start?.ToString(fmt) ?? "…";
                string e = f.End?.ToString(fmt) ?? "…";
                yield return (f.Column, $"{Name(f.Column)} {s}~{e}");
            }
            foreach (var f in NumericFilters)
            {
                string s = f.Min?.ToString(CultureInfo.InvariantCulture) ?? "…";
                string e = f.Max?.ToString(CultureInfo.InvariantCulture) ?? "…";
                yield return (f.Column, $"{Name(f.Column)} {s}~{e}");
            }
            foreach (var f in TextFilters)
            {
                string op = f.Op switch
                {
                    TextFilterOp.Contains => "⊃",
                    TextFilterOp.Equals => "=",
                    TextFilterOp.StartsWith => "^",
                    TextFilterOp.EndsWith => "$",
                    TextFilterOp.Regex => "/./",
                    TextFilterOp.IsBlank => "= ∅",
                    TextFilterOp.IsNotBlank => "≠ ∅",
                    TextFilterOp.InList => "∈",
                    _ => "?"
                };
                if (f.Op is TextFilterOp.IsBlank or TextFilterOp.IsNotBlank)
                    yield return (f.Column, $"{Name(f.Column)} {op}");
                else if (f.Op is TextFilterOp.InList)
                {
                    int n = f.Value.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
                    yield return (f.Column, $"{Name(f.Column)} {op} {n}");
                }
                else
                    yield return (f.Column, $"{Name(f.Column)} {op} \"{f.Value}\"");
            }
        }
    }
}
