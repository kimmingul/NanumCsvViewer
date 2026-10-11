using System.Text;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 그리드 선택 영역·행·열을 클립보드용 TSV로 변환합니다(엑셀/시트에 자연스럽게 붙여넣기).
    /// 셀 표시값은 미리보기로 잘려 있으므로, 호출자는 항상 <c>VirtualCsvDocument</c>에서 전체 값을 공급해야 합니다.
    /// </summary>
    public static class GridCopyFormatter
    {
        /// <summary>선택된 셀들의 경계 사각형을 TSV로. 사각형 내 비선택 셀은 빈 문자열.</summary>
        public static string SelectedCellsTsv(ISet<(int Row, int Col)> cells, Func<int, string[]> rowProvider)
        {
            if (cells.Count == 0) return string.Empty;
            int minR = int.MaxValue, maxR = int.MinValue, minC = int.MaxValue, maxC = int.MinValue;
            foreach (var (r, c) in cells)
            {
                if (r < minR) minR = r;
                if (r > maxR) maxR = r;
                if (c < minC) minC = c;
                if (c > maxC) maxC = c;
            }

            var sb = new StringBuilder();
            for (int r = minR; r <= maxR; r++)
            {
                string[] row = rowProvider(r);
                for (int c = minC; c <= maxC; c++)
                {
                    if (c > minC) sb.Append('\t');
                    if (cells.Contains((r, c)) && c < row.Length) sb.Append(Clean(row[c]));
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>한 행을 표시 컬럼 순서로 TSV(한 줄).</summary>
        public static string RowTsv(string[] row, IReadOnlyList<int> columns)
            => string.Join("\t", columns.Select(c => c < row.Length ? Clean(row[c]) : string.Empty)) + "\n";

        /// <summary>한 컬럼을 헤더 + 값들로 TSV(여러 줄).</summary>
        public static string ColumnTsv(string header, IEnumerable<string> values)
        {
            var sb = new StringBuilder();
            sb.Append(Clean(header)).Append('\n');
            foreach (var v in values) sb.Append(Clean(v)).Append('\n');
            return sb.ToString();
        }

        // TSV 한 셀에 탭/개행이 들어가면 열·행이 깨지므로 공백으로 치환.
        private static string Clean(string v)
            => v.Replace("\t", " ").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
    }
}
