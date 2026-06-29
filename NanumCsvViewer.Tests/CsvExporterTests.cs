using System.IO;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class CsvExporterTests
    {
        private static string ExportToString(ExportFormat format, string[] headers, List<string[]> rows,
            int[]? order = null)
        {
            string path = Path.Combine(Path.GetTempPath(), "ncv_test_" + Guid.NewGuid().ToString("N") + ".out");
            try
            {
                CsvExporter.Export(format, path, headers, rows, order);
                return File.ReadAllText(path);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Csv_escapes_special_characters()
        {
            var rows = new List<string[]> { new[] { "a,b", "he said \"hi\"", "line1\nline2" } };
            string csv = ExportToString(ExportFormat.Csv, new[] { "c1", "c2", "c3" }, rows);
            Assert.Contains("\"a,b\"", csv);
            Assert.Contains("\"he said \"\"hi\"\"\"", csv);
            Assert.Contains("\"line1\nline2\"", csv);
        }

        [Fact]
        public void Json_disambiguates_duplicate_headers()
        {
            var rows = new List<string[]> { new[] { "1", "2" } };
            string json = ExportToString(ExportFormat.Json, new[] { "value", "value" }, rows);
            Assert.Contains("\"value\"", json);
            Assert.Contains("\"value (2)\"", json);
        }

        [Fact]
        public void Markdown_has_header_separator_row()
        {
            var rows = new List<string[]> { new[] { "x", "y" } };
            string md = ExportToString(ExportFormat.Markdown, new[] { "a", "b" }, rows);
            Assert.Contains("| a | b |", md);
            Assert.Contains("| --- | --- |", md);
        }

        [Fact]
        public void Html_escapes_markup()
        {
            var rows = new List<string[]> { new[] { "<script>" } };
            string html = ExportToString(ExportFormat.Html, new[] { "c" }, rows);
            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("<script>", html);
        }

        [Fact]
        public void Column_order_projects_visible_columns()
        {
            var rows = new List<string[]> { new[] { "A", "B", "C" } };
            // 컬럼 2,0만 이 순서로 내보내기
            string csv = ExportToString(ExportFormat.Csv, new[] { "h0", "h1", "h2" }, rows, new[] { 2, 0 });
            var lines = csv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Equal("h2,h0", lines[0]);
            Assert.Equal("C,A", lines[1]);
        }
    }
}
