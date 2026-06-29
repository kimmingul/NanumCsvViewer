using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NanumCsvViewer.Csv
{
    public enum ExportFormat
    {
        Csv,
        Markdown,
        Json,
        Html
    }

    /// <summary>
    /// 현재 표시 뷰(순서·표시 컬럼)를 CSV/Markdown/JSON/HTML로 내보냅니다. 행 단위 스트리밍(전체 구체화 없음).
    /// 호출자는 표시 컬럼 인덱스 순서(<paramref name="columnOrder"/>)와 행 시퀀스를 공급합니다.
    /// </summary>
    public static class CsvExporter
    {
        public static ExportFormat FormatFromExtension(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".md" => ExportFormat.Markdown,
                ".json" => ExportFormat.Json,
                ".html" or ".htm" => ExportFormat.Html,
                _ => ExportFormat.Csv
            };
        }

        public static string FilterString =>
            "CSV (*.csv)|*.csv|Markdown (*.md)|*.md|JSON (*.json)|*.json|HTML (*.html)|*.html";

        /// <param name="columnOrder">내보낼 컬럼 인덱스(표시 순서). null이면 헤더 전체 순서.</param>
        public static void Export(
            ExportFormat format, string outputPath,
            IReadOnlyList<string> headers, IEnumerable<string[]> rows,
            IReadOnlyList<int>? columnOrder = null)
        {
            int[] order = (columnOrder ?? Enumerable.Range(0, headers.Count)).ToArray();
            string[] visibleHeaders = order.Select(c => c < headers.Count ? headers[c] : "").ToArray();

            using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true));
            switch (format)
            {
                case ExportFormat.Csv: WriteCsv(writer, visibleHeaders, rows, order); break;
                case ExportFormat.Markdown: WriteMarkdown(writer, visibleHeaders, rows, order); break;
                case ExportFormat.Json: WriteJson(writer, visibleHeaders, rows, order); break;
                case ExportFormat.Html: WriteHtml(writer, visibleHeaders, rows, order); break;
            }
        }

        private static string Cell(string[] row, int column)
            => column < row.Length ? row[column] : "";

        // ---- CSV (RFC 4180) ----
        private static void WriteCsv(TextWriter w, string[] headers, IEnumerable<string[]> rows, int[] order)
        {
            w.WriteLine(string.Join(",", headers.Select(CsvEscape)));
            foreach (var row in rows)
                w.WriteLine(string.Join(",", order.Select(c => CsvEscape(Cell(row, c)))));
        }

        private static string CsvEscape(string value)
        {
            if (value.IndexOfAny(CsvSpecials) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static readonly char[] CsvSpecials = { ',', '"', '\n', '\r' };

        // ---- Markdown 파이프 테이블 ----
        private static void WriteMarkdown(TextWriter w, string[] headers, IEnumerable<string[]> rows, int[] order)
        {
            w.WriteLine("| " + string.Join(" | ", headers.Select(MdEscape)) + " |");
            w.WriteLine("| " + string.Join(" | ", headers.Select(_ => "---")) + " |");
            foreach (var row in rows)
                w.WriteLine("| " + string.Join(" | ", order.Select(c => MdEscape(Cell(row, c)))) + " |");
        }

        private static string MdEscape(string value)
            => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        // ---- JSON (객체 스트리밍, 중복 헤더는 안정 키) ----
        private static void WriteJson(TextWriter w, string[] headers, IEnumerable<string[]> rows, int[] order)
        {
            string[] keys = StableKeys(headers);
            var options = new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            bool first = true;
            w.Write("[");
            foreach (var row in rows)
            {
                w.Write(first ? "\n" : ",\n");
                first = false;
                using var stream = new MemoryStream();
                using (var json = new Utf8JsonWriter(stream, options))
                {
                    json.WriteStartObject();
                    for (int i = 0; i < order.Length; i++)
                        json.WriteString(keys[i], Cell(row, order[i]));
                    json.WriteEndObject();
                }
                w.Write(Encoding.UTF8.GetString(stream.ToArray()));
            }
            w.Write(first ? "]\n" : "\n]\n");
        }

        /// <summary>한 행을 JSON 객체 문자열로(인스펙터 복사용). 중복 헤더는 안정 키, 표시 컬럼만.</summary>
        public static string RowJson(IReadOnlyList<string> headers, string[] row, IReadOnlyList<int>? columnOrder = null)
        {
            int[] order = (columnOrder ?? Enumerable.Range(0, headers.Count)).ToArray();
            string[] visibleHeaders = order.Select(c => c < headers.Count ? headers[c] : "").ToArray();
            string[] keys = StableKeys(visibleHeaders);

            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                json.WriteStartObject();
                for (int i = 0; i < order.Length; i++)
                    json.WriteString(keys[i], order[i] < row.Length ? row[order[i]] : "");
                json.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>중복 헤더에 안정 키 부여: 두 번째부터 "name (2)", "name (3)" …</summary>
        private static string[] StableKeys(string[] headers)
        {
            var seen = new Dictionary<string, int>();
            var keys = new string[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                string name = string.IsNullOrEmpty(headers[i]) ? "value" : headers[i];
                int count = seen.TryGetValue(name, out int c) ? c + 1 : 1;
                seen[name] = count;
                keys[i] = count == 1 ? name : $"{name} ({count})";
            }
            return keys;
        }

        // ---- HTML 테이블 ----
        private static void WriteHtml(TextWriter w, string[] headers, IEnumerable<string[]> rows, int[] order)
        {
            w.WriteLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            w.WriteLine("<style>table{border-collapse:collapse}th,td{border:1px solid #ccc;padding:4px 8px;font-family:sans-serif;font-size:13px}th{background:#f0f0f0}</style>");
            w.WriteLine("</head><body><table>");
            w.WriteLine("<thead><tr>" + string.Concat(headers.Select(h => "<th>" + HtmlEscape(h) + "</th>")) + "</tr></thead>");
            w.WriteLine("<tbody>");
            foreach (var row in rows)
                w.WriteLine("<tr>" + string.Concat(order.Select(c => "<td>" + HtmlEscape(Cell(row, c)) + "</td>")) + "</tr>");
            w.WriteLine("</tbody></table></body></html>");
        }

        private static string HtmlEscape(string value)
            => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("\n", "<br>");
    }
}
