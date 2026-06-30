using System.IO;
using System.IO.Compression;
using System.Text;
using NanumCsvViewer.Import;

namespace NanumCsvViewer.Tests
{
    public class WorkbookImportTests
    {
        [Fact]
        public void IsImportable_matches_excel_and_sas_extensions()
        {
            Assert.True(TabularImporter.IsImportable("a.xlsx"));
            Assert.True(TabularImporter.IsImportable("a.XLS"));
            Assert.True(TabularImporter.IsImportable("data.sas7bdat"));
            Assert.False(TabularImporter.IsImportable("a.csv"));
            Assert.False(TabularImporter.IsImportable("a.txt"));
        }

        [Fact]
        public void Imports_xlsx_each_sheet_to_csv()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_xlsxtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string xlsx = Path.Combine(dir, "book.xlsx");
                CreateTwoSheetXlsx(xlsx);

                var sheets = TabularImporter.Import(xlsx, Path.Combine(dir, "out"));

                Assert.Equal(2, sheets.Count);
                Assert.Equal("Alpha", sheets[0].Name);
                Assert.Equal("Beta", sheets[1].Name);

                string csv0 = File.ReadAllText(sheets[0].CsvPath).Replace("\r\n", "\n").Trim();
                var lines0 = csv0.Split('\n');
                Assert.Equal("name,age", lines0[0]);
                Assert.Equal("Kim,30", lines0[1]);
                Assert.Equal("Lee,25", lines0[2]);

                string csv1 = File.ReadAllText(sheets[1].CsvPath).Replace("\r\n", "\n").Trim();
                Assert.Equal("city", csv1.Split('\n')[0]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // 최소 OOXML(xlsx) 두 시트짜리 워크북을 직접 생성.
        private static void CreateTwoSheetXlsx(string path)
        {
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            Write(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");

            Write(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");

            Write(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Alpha\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Beta\" sheetId=\"2\" r:id=\"rId2\"/></sheets>" +
                "</workbook>");

            Write(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
                "</Relationships>");

            Write(zip, "xl/worksheets/sheet1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>name</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>age</t></is></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Kim</t></is></c><c r=\"B2\"><v>30</v></c></row>" +
                "<row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>Lee</t></is></c><c r=\"B3\"><v>25</v></c></row>" +
                "</sheetData></worksheet>");

            Write(zip, "xl/worksheets/sheet2.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>city</t></is></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Seoul</t></is></c></row>" +
                "</sheetData></worksheet>");
        }

        private static void Write(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var s = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
    }
}
