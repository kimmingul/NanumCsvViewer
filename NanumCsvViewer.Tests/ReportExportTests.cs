using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    public class ReportExportTests
    {
        // openpyxl 3.1.5 (throwaway, 2026-09-30): sheets ['Report','Coefficients'];
        // A1='GLM <test>', B2='data.csv', B3='2026-09-30 12:34:56', B4='1.18.0',
        // B5='Current view · 10 of 12 rows'; B12=1.5 float, B13=1234 int, C13='<0.0001', C14=0.25.
        private static readonly DateTime When = new(2026, 9, 30, 12, 34, 56);

        private static ReportContext Context() => new(
            "data.csv", "1.18.0", "Current view · 10 of 12 rows");

        private static (AdvancedReport Report, string Rendered) Sample()
        {
            var table = new TextTable("Term", "Estimate", "p");
            table.AddRow("intercept", "1.5", "0.0123");
            table.AddRow("x", "1,234", "<0.0001");
            table.AddRow("그룹", "—", "0.2500");
            string rendered = table.Render();
            string text = "분석 범위: 현재 뷰\n\nCoefficients\n" + rendered + "\n끝.\n";
            var captured = new CapturedTable(
                rendered,
                new[] { "Term", "Estimate", "p" },
                new IReadOnlyList<string>[]
                {
                    new[] { "intercept", "1.5", "0.0123" },
                    new[] { "x", "1,234", "<0.0001" },
                    new[] { "그룹", "—", "0.2500" },
                });
            var report = new AdvancedReport("GLM <test>", text, new[] { captured }, null, When);
            return (report, rendered);
        }

        [Fact]
        public void Compose_substitutes_table_at_rendered_position()
        {
            var (report, _) = Sample();
            var doc = ReportExport.Compose(report, Context());

            Assert.False(doc.Preformatted);
            Assert.Equal("2026-09-30 12:34:56", doc.AnalyzedAt);
            Assert.Equal("data.csv", doc.FileName);
            Assert.Equal("1.18.0", doc.AppVersion);
            var table = Assert.IsType<ReportTableBlock>(Assert.Single(doc.Blocks, b => b is ReportTableBlock));
            Assert.Equal("Coefficients", table.Name);
            Assert.Equal(new[] { "Term", "Estimate", "p" }, table.Headers);
            Assert.Equal("1.5", table.Rows[0][1]);
            Assert.Equal("<0.0001", table.Rows[1][2]);
            Assert.Contains(doc.Blocks, b => b is ReportTextBlock t && t.Text.Contains("분석 범위") && !t.Preformatted);
            Assert.Contains(doc.Blocks, b => b is ReportTextBlock t && t.Text == "끝.");
            Assert.DoesNotContain(doc.Blocks, b => b is ReportTextBlock t && t.Text.Contains("Estimate"));
        }

        [Fact]
        public void Compose_falls_back_to_preformatted_when_no_table_is_located()
        {
            var missing = new CapturedTable("not in the body\n", new[] { "A" }, new[] { (IReadOnlyList<string>)new[] { "1" } });
            var report = new AdvancedReport("T", "plain paragraph\n\nstill text", new[] { missing }, null, When);
            var doc = ReportExport.Compose(report, Context());

            Assert.True(doc.Preformatted);
            var block = Assert.IsType<ReportTextBlock>(Assert.Single(doc.Blocks));
            Assert.True(block.Preformatted);
            Assert.Contains("plain paragraph", block.Text);
            Assert.DoesNotContain(doc.Blocks, b => b is ReportTableBlock);
        }

        [Fact]
        public void Compose_skips_unlocated_tables_and_keeps_found_ones()
        {
            var (report, rendered) = Sample();
            var missing = new CapturedTable("absent table\n", new[] { "Z" }, Array.Empty<IReadOnlyList<string>>());
            var mixed = report with { Tables = new[] { report.Tables[0], missing } };
            var doc = ReportExport.Compose(mixed, Context());

            Assert.False(doc.Preformatted);
            Assert.Single(doc.Blocks.OfType<ReportTableBlock>());
            Assert.Contains(rendered.Replace("\r\n", "\n").Trim(), mixed.Text.Replace("\r\n", "\n"));
        }

        [Fact]
        public void Html_is_self_contained_and_escapes_markup()
        {
            var (report, _) = Sample();
            string html = ReportExport.ToHtml(ReportExport.Compose(report, Context()));

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("<meta charset=\"utf-8\">", html);
            Assert.Contains("<style>", html);
            Assert.Contains("@page", html);
            Assert.Contains("@media print", html);
            Assert.DoesNotContain("<link", html);
            Assert.DoesNotContain("url(", html);
            Assert.DoesNotContain("http://", html);
            Assert.DoesNotContain("https://", html);
            Assert.Contains("GLM &lt;test&gt;", html);
            Assert.Contains("data.csv", html);
            Assert.Contains("2026-09-30 12:34:56", html);
            Assert.Contains("1.18.0", html);
            Assert.Contains("Current view · 10 of 12 rows", html);
            Assert.Contains("분석 범위", html);
            Assert.Contains("<table class=\"data\">", html);
            Assert.Contains("<td class=\"num\">1.5</td>", html);
            Assert.Contains("<td>&lt;0.0001</td>", html);
            Assert.Contains("그룹", html);
            Assert.DoesNotContain("<script>", html);
        }

        [Fact]
        public void Html_uses_pre_when_tables_were_not_located()
        {
            var missing = new CapturedTable("nope", new[] { "A" }, Array.Empty<IReadOnlyList<string>>());
            var report = new AdvancedReport("T", "keep\nme", new[] { missing }, null, When);
            string html = ReportExport.ToHtml(ReportExport.Compose(report, Context()));

            Assert.Contains("<pre>keep\nme</pre>", html);
            Assert.DoesNotContain("class=\"data\"", html);
        }

        [Fact]
        public void Html_file_is_utf8_without_bom()
        {
            var (report, _) = Sample();
            string path = TempPath(".html");
            try
            {
                ReportExport.WriteHtml(ReportExport.Compose(report, Context()), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                Assert.Contains("그룹", text);
                Assert.Contains("charset=\"utf-8\"", text);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Xlsx_report_sheet_keeps_p_values_as_text_and_parses_numbers()
        {
            // openpyxl이 같은 바이트를 열면: A1='GLM <test>', B2='data.csv', B3='2026-09-30 12:34:56',
            // B4='1.18.0', 숫자 셀 1.5와 1234, 텍스트 셀 '<0.0001', 시트 ['Report','Coefficients'].
            var (report, _) = Sample();
            string path = TempPath(".xlsx");
            try
            {
                ReportExport.WriteXlsx(ReportExport.Compose(report, Context()), path);
                using var zip = ZipFile.OpenRead(path);
                var workbook = Load(zip, "xl/workbook.xml");
                var names = SheetNames(workbook);
                Assert.Equal(new[] { "Report", "Coefficients" }, names);

                var sheet = Load(zip, "xl/worksheets/sheet1.xml");
                Assert.Equal("GLM <test>", Text(sheet, "A1"));
                Assert.Equal("data.csv", Text(sheet, "B2"));
                Assert.Equal("2026-09-30 12:34:56", Text(sheet, "B3"));
                Assert.Equal("1.18.0", Text(sheet, "B4"));
                Assert.Equal("Current view · 10 of 12 rows", Text(sheet, "B5"));
                Assert.Equal(1.5, Number(sheet, "1.5"));
                Assert.Equal(1234, Number(sheet, "1234"));
                Assert.Contains(InlineTexts(sheet), t => t == "<0.0001");
                Assert.Contains(InlineTexts(sheet), t => t == "—");

                var table = Load(zip, "xl/worksheets/sheet2.xml");
                Assert.Equal("Term", Text(table, "A1"));
                Assert.Equal(1.5, Number(table, "1.5"));
                Assert.Contains(InlineTexts(table), t => t == "<0.0001");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Xlsx_sheet_names_are_unique_sanitized_and_at_most_31_chars()
        {
            string longName = new string('가', 40);
            var blocks = new ReportBlock[]
            {
                Table(longName, "1"),
                Table("A/B?C*:D", "2"),
                Table("Report", "3"),
                Table("Same", "4"),
                Table("same", "5"),
            };
            var doc = new ReportDocument(
                "T", "f", "2026-09-30 00:00:00", "1.18.0", "scope",
                "File", "Analyzed", "App version", "View scope", blocks, false);
            string path = TempPath(".xlsx");
            try
            {
                ReportExport.WriteXlsx(doc, path);
                using var zip = ZipFile.OpenRead(path);
                var names = SheetNames(Load(zip, "xl/workbook.xml"));
                Assert.Equal("Report", names[0]);
                Assert.Equal(31, names[1].Length);
                Assert.Equal(new string('가', 31), names[1]);
                Assert.Equal("A_B_C__D", names[2]);
                Assert.Equal("Report (2)", names[3]);
                Assert.Equal("Same", names[4]);
                Assert.Equal("same (2)", names[5]);
                Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                foreach (string name in names)
                {
                    Assert.InRange(name.Length, 1, 31);
                    Assert.DoesNotContain('\\', name);
                    Assert.DoesNotContain('/', name);
                    Assert.DoesNotContain('?', name);
                    Assert.DoesNotContain('*', name);
                    Assert.DoesNotContain('[', name);
                    Assert.DoesNotContain(']', name);
                    Assert.DoesNotContain(':', name);
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Number_parser_keeps_comparison_p_values_as_text()
        {
            Assert.True(ReportExport.TryParseNumber("1.5", out double one));
            Assert.Equal(1.5, one);
            Assert.True(ReportExport.TryParseNumber("1,234", out double thousands));
            Assert.Equal(1234, thousands);
            Assert.True(ReportExport.TryParseNumber("1,234.5", out double mixed));
            Assert.Equal(1234.5, mixed);
            Assert.True(ReportExport.TryParseNumber("  3 ", out double trimmed));
            Assert.Equal(3, trimmed);
            Assert.True(ReportExport.TryParseNumber("1.23E-4", out double sci));
            Assert.Equal(0.000123, sci, 8);
            Assert.False(ReportExport.TryParseNumber("<0.0001", out _));
            Assert.False(ReportExport.TryParseNumber(">0.9999", out _));
            Assert.False(ReportExport.TryParseNumber("—", out _));
            Assert.False(ReportExport.TryParseNumber("", out _));
            Assert.False(ReportExport.TryParseNumber("∞", out _));
            Assert.False(ReportExport.TryParseNumber("0.0312*", out _));
        }

        [Fact]
        public void Wrap_breaks_on_spaces_and_forces_progress()
        {
            static float Width(string s) => s.Length;
            Assert.Equal(new[] { "hello", "world" }, ReportExport.WrapLine("hello world", 5, Width));
            Assert.Equal(new[] { "abc", "def", "g" }, ReportExport.WrapLine("abcdefg", 3, Width));
            Assert.Equal(new[] { "short" }, ReportExport.WrapLine("short", 10, Width));
            Assert.Equal(new[] { "" }, ReportExport.WrapLine("", 4, Width));
        }

        [Fact]
        public void Paginate_keeps_a_short_report_on_one_page()
        {
            var doc = Tiny("Hi", new ReportTextBlock("ok", false));
            var pages = ReportExport.Paginate(doc, new UnitMeasurer { ContentWidth = 20, ContentHeight = 20 });
            var page = Assert.Single(pages);
            Assert.Contains(page.Items, i => i is ReportLayoutText t && t.Role == ReportTextRole.Title && t.Text == "Hi");
            Assert.Contains(page.Items, i => i is ReportLayoutText t && t.Text == "ok");
        }

        [Fact]
        public void Paginate_repeats_table_header_on_the_next_page()
        {
            var rows = Enumerable.Range(1, 4)
                .Select(i => (IReadOnlyList<string>)new[] { "r" + i.ToString(CultureInfo.InvariantCulture) })
                .ToArray();
            var doc = Tiny("Hi", new ReportTableBlock("T", new[] { "H" }, rows));
            // 제목 2 + 메타 4 + 간격 1 = 7. 높이 10이면 표 머리글+2행만 첫 페이지에 들어간다.
            var pages = ReportExport.Paginate(doc, new UnitMeasurer { ContentWidth = 20, ContentHeight = 10, CellPadX = 0, CellPadY = 0, BlockGap = 1 });

            Assert.True(pages.Count >= 2);
            var firstRows = pages[0].Items.OfType<ReportLayoutTableRow>().ToArray();
            Assert.True(firstRows[0].Header);
            Assert.Equal("H", firstRows[0].Cells[0]);
            Assert.Equal("r1", firstRows[1].Cells[0]);
            Assert.DoesNotContain(firstRows, r => r.Cells[0] == "r4");

            var secondRows = pages[1].Items.OfType<ReportLayoutTableRow>().ToArray();
            Assert.True(secondRows[0].Header);
            Assert.Equal("r3", secondRows[1].Cells[0]);
            Assert.DoesNotContain(pages[1].Items, i => i is ReportLayoutText t && t.Role == ReportTextRole.Title);
        }

        [Fact]
        public void Paginate_wraps_a_long_body_line()
        {
            var doc = Tiny("Hi", new ReportTextBlock("hello world", false));
            var pages = ReportExport.Paginate(doc, new UnitMeasurer { ContentWidth = 5, ContentHeight = 30, BlockGap = 0 });
            var lines = pages.SelectMany(p => p.Items).OfType<ReportLayoutText>()
                .Where(t => t.Role == ReportTextRole.Body).Select(t => t.Text).ToArray();
            Assert.Equal(new[] { "hello", "world" }, lines);
        }

        [Fact]
        public void Paginate_stores_the_wrapped_cell_lines_that_pdf_draws()
        {
            const string cell = "abcdefghijKLMN";
            var doc = Tiny("Hi", new ReportTableBlock("T", new[] { "H" }, new[] { (IReadOnlyList<string>)new[] { cell } }));
            var measure = new UnitMeasurer { ContentWidth = 10, ContentHeight = 40, CellPadX = 1, CellPadY = 1, BlockGap = 0 };
            var row = ReportExport.Paginate(doc, measure)
                .SelectMany(p => p.Items).OfType<ReportLayoutTableRow>().Single(r => !r.Header);

            float inner = row.ColumnWidths[0] - 2 * row.PadX;
            var expected = ReportExport.WrapLine(cell, inner, s => measure.Measure(s, ReportTextRole.Body));
            Assert.Equal(expected, row.WrappedLines[0]);
            Assert.True(expected.Count >= 2);
            Assert.Equal(cell, string.Concat(expected));
            Assert.Equal(1f, row.PadX);
            Assert.Equal(1f, row.PadY);
            Assert.Equal(measure.LineHeight(ReportTextRole.Body), row.LineHeight);
            Assert.Equal(expected.Count * row.LineHeight + 2 * row.PadY, row.Height);
            foreach (string line in row.WrappedLines[0])
                Assert.True(measure.Measure(line, ReportTextRole.Body) <= inner + 0.01f);
        }

        [Fact]
        public void Paginate_places_an_oversized_row_without_looping()
        {
            var doc = Tiny("Hi", new ReportTableBlock("T", new[] { "H" }, new[] { (IReadOnlyList<string>)new[] { new string('x', 40) } }));
            var pages = ReportExport.Paginate(doc, new UnitMeasurer { ContentWidth = 4, ContentHeight = 5, CellPadX = 0, CellPadY = 0, BlockGap = 1 });
            Assert.InRange(pages.Count, 1, 8);
            Assert.Contains(pages.SelectMany(p => p.Items).OfType<ReportLayoutTableRow>(), r => !r.Header && r.Height > 5);
        }

        private static ReportDocument Tiny(string title, params ReportBlock[] blocks) => new(
            title, "f", "t", "v", "s", "F", "A", "V", "S", blocks, false);

        private static ReportTableBlock Table(string name, string value) => new(
            name, new[] { "A" }, new[] { (IReadOnlyList<string>)new[] { value } });

        private static string TempPath(string ext)
            => Path.Combine(Path.GetTempPath(), "ncv_report_" + Guid.NewGuid().ToString("N") + ext);

        private static XDocument Load(ZipArchive zip, string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidOperationException("Missing " + name);
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }

        private static string[] SheetNames(XDocument workbook)
            => workbook.Descendants().Where(e => e.Name.LocalName == "sheet")
                .Select(e => (string?)e.Attribute("name") ?? "")
                .ToArray();

        private static string Text(XDocument sheet, string cell)
        {
            var node = Cell(sheet, cell);
            var inline = node.Descendants().FirstOrDefault(e => e.Name.LocalName == "t");
            return inline?.Value ?? "";
        }

        private static double Number(XDocument sheet, string exact)
        {
            foreach (var cell in sheet.Descendants().Where(e => e.Name.LocalName == "c"))
            {
                var v = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
                if (v is null) continue;
                if (double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    && n.ToString("G15", CultureInfo.InvariantCulture) == double.Parse(exact, CultureInfo.InvariantCulture).ToString("G15", CultureInfo.InvariantCulture))
                    return n;
            }
            throw new InvalidOperationException("Number " + exact + " not found.");
        }

        private static IEnumerable<string> InlineTexts(XDocument sheet)
            => sheet.Descendants().Where(e => e.Name.LocalName == "c")
                .SelectMany(c => c.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));

        private static XElement Cell(XDocument sheet, string refer)
            => sheet.Descendants().First(e => e.Name.LocalName == "c" && (string?)e.Attribute("r") == refer);

        private sealed class UnitMeasurer : IReportMeasurer
        {
            public float ContentWidth { get; init; } = 20;
            public float ContentHeight { get; init; } = 20;
            public float CellPadX { get; init; } = 1;
            public float CellPadY { get; init; }
            public float BlockGap { get; init; } = 1;
            public float LineHeight(ReportTextRole role) => role == ReportTextRole.Title ? 2 : 1;
            public float Measure(string text, ReportTextRole role)
            {
                float w = 0;
                foreach (char ch in text)
                    w += ch is >= '\uAC00' and <= '\uD7A3' ? 2 : 1;
                return w;
            }
        }
    }
}
