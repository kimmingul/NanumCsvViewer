using System.Collections.Concurrent;
using System.Text;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 셀 편집 덮개(overlay). 원본 파일은 절대 바꾸지 않고, (데이터 행, 컬럼) → 문자열만 보관한다.
    /// 값은 항상 "문자열 그대로" — 숫자로 해석·변환하지 않으므로 001 같은 선행 0이 사라지지 않는다.
    /// 읽기(그리드·필터·분석·내보내기)는 VirtualCsvDocument의 단일 파싱 경로에서 덮개를 적용한다.
    /// </summary>
    public sealed class CellEdits
    {
        private readonly ConcurrentDictionary<(int Row, int Col), string> _cells = new();
        // 행별 편집 컬럼(읽기 경로의 빠른 판정용). 값은 컬럼 → 문자열.
        private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, string>> _rows = new();

        public int Count => _cells.Count;
        public bool IsEmpty => _cells.IsEmpty;
        public event Action? Changed;

        public bool Contains(int dataRow, int col) => _cells.ContainsKey((dataRow, col));

        public bool TryGet(int dataRow, int col, out string value) => _cells.TryGetValue((dataRow, col), out value!);

        /// <summary>편집값 저장. original과 같으면 편집을 제거(되돌림)한다.</summary>
        public void Set(int dataRow, int col, string value, string original)
        {
            if (string.Equals(value, original, StringComparison.Ordinal)) { Revert(dataRow, col); return; }
            _cells[(dataRow, col)] = value;
            _rows.GetOrAdd(dataRow, _ => new ConcurrentDictionary<int, string>())[col] = value;
            Changed?.Invoke();
        }

        public void Revert(int dataRow, int col)
        {
            if (!_cells.TryRemove((dataRow, col), out _)) return;
            if (_rows.TryGetValue(dataRow, out var cols))
            {
                cols.TryRemove(col, out _);
                if (cols.IsEmpty) _rows.TryRemove(dataRow, out _);
            }
            Changed?.Invoke();
        }

        public void Clear()
        {
            if (_cells.IsEmpty) return;
            _cells.Clear();
            _rows.Clear();
            Changed?.Invoke();
        }

        /// <summary>편집된 행이면 복사본에 덮어쓴 새 배열(컬럼이 모자라면 확장), 아니면 원본 그대로.</summary>
        public string[] Apply(int dataRow, string[] fields)
        {
            if (_rows.IsEmpty || !_rows.TryGetValue(dataRow, out var cols)) return fields;
            int width = fields.Length;
            foreach (var kv in cols) if (kv.Key >= width) width = kv.Key + 1;
            var copy = new string[width];
            Array.Copy(fields, copy, fields.Length);
            for (int i = fields.Length; i < width; i++) copy[i] = string.Empty;
            foreach (var kv in cols) copy[kv.Key] = kv.Value;
            return copy;
        }

        /// <summary>편집된 행 번호(오름차순).</summary>
        public int[] EditedRows()
        {
            var rows = _rows.Keys.ToArray();
            Array.Sort(rows);
            return rows;
        }

        /// <summary>
        /// CSV 필드 직렬화. 구분자·따옴표·줄바꿈이 있거나 앞뒤 공백이 있으면 따옴표로 감싼다.
        /// 값은 해석하지 않고 그대로 쓴다(선행 0 보존). 선행 0 숫자는 CSV에서 따옴표와 무관하게 텍스트로 저장되며,
        /// 엑셀이 열 때 0을 지우는 것은 엑셀의 동작이다(앱은 값 자체를 바꾸지 않는다).
        /// </summary>
        public static string QuoteField(string value, char delimiter)
        {
            bool needs = value.Length > 0 &&
                         (value.IndexOf(delimiter) >= 0 || value.IndexOf('"') >= 0 || value.IndexOf('\n') >= 0 ||
                          value.IndexOf('\r') >= 0 || value[0] == ' ' || value[^1] == ' ');
            if (!needs) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        public static string JoinRecord(IReadOnlyList<string> fields, char delimiter)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                sb.Append(QuoteField(fields[i], delimiter));
            }
            return sb.ToString();
        }
    }
}
