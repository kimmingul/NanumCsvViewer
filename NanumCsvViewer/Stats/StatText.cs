using System.Globalization;
using System.Text;

namespace NanumCsvViewer.Stats
{
    /// <summary>고급 통계 결과 텍스트의 수치 표기(모든 모듈 공통, 문화권 무관).</summary>
    public static class StatFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>p값: 1e-4 미만은 "&lt;0.0001", NaN은 "—".</summary>
        public static string P(double p)
            => double.IsNaN(p) ? "—" : p < 1e-4 ? "<0.0001" : p.ToString("0.0000", Inv);

        /// <summary>유효숫자 6자리 일반 표기.</summary>
        public static string G(double v)
            => double.IsNaN(v) ? "—" : double.IsPositiveInfinity(v) ? "∞" : double.IsNegativeInfinity(v) ? "-∞" : v.ToString("G6", Inv);

        /// <summary>소수 고정 자리.</summary>
        public static string F(double v, int decimals = 4)
            => double.IsNaN(v) ? "—" : v.ToString("F" + decimals, Inv);

        public static string Int(long v) => v.ToString("N0", Inv);

        /// <summary>유의성 표시(* p&lt;.05, ** p&lt;.01, *** p&lt;.001).</summary>
        public static string Stars(double p)
            => double.IsNaN(p) ? "" : p < 0.001 ? "***" : p < 0.01 ? "**" : p < 0.05 ? "*" : "";
    }

    /// <summary>고정폭 텍스트 표. 첫 열은 왼쪽, 나머지는 오른쪽 정렬. 결과창(고정폭 글꼴)·복사용.</summary>
    public sealed class TextTable
    {
        private readonly string[] _headers;
        private readonly List<string[]> _rows = new();

        public TextTable(params string[] headers) => _headers = headers;

        public TextTable AddRow(params string[] cells)
        {
            if (cells.Length != _headers.Length) throw new ArgumentException("Cell count must match headers.", nameof(cells));
            _rows.Add(cells);
            return this;
        }

        public int RowCount => _rows.Count;

        public string Render()
        {
            int c = _headers.Length;
            var width = new int[c];
            for (int j = 0; j < c; j++)
                width[j] = Math.Max(DisplayWidth(_headers[j]), _rows.Count == 0 ? 0 : _rows.Max(r => DisplayWidth(r[j])));
            var sb = new StringBuilder();
            AppendRow(sb, _headers, width);
            sb.AppendLine(new string('─', width.Sum() + 2 * (c - 1)));
            foreach (var r in _rows) AppendRow(sb, r, width);
            string rendered = sb.ToString();
            ReportCapture.Record(new CapturedTable(rendered, _headers.ToArray(), _rows.Select(r => (IReadOnlyList<string>)r.ToArray()).ToArray()));
            return rendered;
        }

        private static void AppendRow(StringBuilder sb, string[] cells, int[] width)
        {
            for (int j = 0; j < cells.Length; j++)
            {
                if (j > 0) sb.Append("  ");
                int pad = width[j] - DisplayWidth(cells[j]);
                if (j == 0) sb.Append(cells[j]).Append(' ', j == cells.Length - 1 ? 0 : pad);
                else sb.Append(' ', pad).Append(cells[j]);
            }
            sb.AppendLine();
        }

        // 한글·전각 문자는 고정폭 글꼴에서 2칸을 차지한다.
        private static int DisplayWidth(string s)
        {
            int w = 0;
            foreach (char ch in s) w += ch >= 0x1100 && (ch <= 0x115F || (ch >= 0x2E80 && ch <= 0xA4CF) || (ch >= 0xAC00 && ch <= 0xD7A3) || (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFF00 && ch <= 0xFF60)) ? 2 : 1;
            return w;
        }
    }
}
