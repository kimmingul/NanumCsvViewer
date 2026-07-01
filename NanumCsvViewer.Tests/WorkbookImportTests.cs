using System.IO;
using System.IO.Compression;
using System.Text;
using Curiosity.SPSS.DataReader;
using Curiosity.SPSS.SpssDataset;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Import;

namespace NanumCsvViewer.Tests
{
    public class WorkbookImportTests
    {
        [Fact]
        public void IsImportable_matches_excel_sas_and_spss_extensions()
        {
            Assert.True(TabularImporter.IsImportable("a.xlsx"));
            Assert.True(TabularImporter.IsImportable("a.XLS"));
            Assert.True(TabularImporter.IsImportable("data.sas7bdat"));
            Assert.True(TabularImporter.IsImportable("survey.sav"));
            Assert.True(TabularImporter.IsImportable("survey.SAV"));
            Assert.False(TabularImporter.IsImportable("a.csv"));
            Assert.False(TabularImporter.IsImportable("a.txt"));
        }

        [Theory]
        [InlineData("@2010_TotalProfit", "2010_TotalProfit")]  // 숫자로 시작하는 변수명의 라이브러리 '@' 접두 제거
        [InlineData("@x", "x")]
        [InlineData("Region", "Region")]                       // 정상 이름은 그대로
        [InlineData("@", "@")]                                 // '@' 단독은 보존(길이 1 가드)
        [InlineData("a@b", "a@b")]                             // 선행이 아닌 '@'는 건드리지 않음
        public void SpssHeaderName_strips_only_leading_at(string input, string expected)
            => Assert.Equal(expected, TabularImporter.SpssHeaderName(input));

        [Fact]
        public void MapSpss_dollar_format_is_currency()
        {
            var v = NumericVar("sal", FormatType.DOLLAR);
            var hint = FormatMappers.MapSpss(v);
            Assert.Equal(ColumnValueType.Currency, hint!.Type);
            Assert.Equal('$', hint.CurrencySymbol);
        }

        [Fact]
        public void MapSpss_pct_format_is_percent_whole()
        {
            var hint = FormatMappers.MapSpss(NumericVar("rate", FormatType.PCT));
            Assert.Equal(ColumnValueType.Percent, hint!.Type);
            Assert.False(hint.PercentIsFraction); // SPSS PCT는 정수값 저장
        }

        [Fact]
        public void MapSpss_scientific_format_is_scientific()
            => Assert.Equal(ColumnValueType.Scientific, FormatMappers.MapSpss(NumericVar("v", FormatType.E))!.Type);

        [Fact]
        public void MapSpss_value_labels_make_categorical_ordinal()
        {
            var nominal = NumericVar("sex", FormatType.F);
            nominal.MeasurementType = MeasurementType.Nominal;
            nominal.ValueLabels = new Dictionary<double, string> { { 1, "남" }, { 2, "여" } };
            Assert.Equal(ColumnValueType.Categorical, FormatMappers.MapSpss(nominal)!.Type);

            var ordinal = NumericVar("grade", FormatType.F);
            ordinal.MeasurementType = MeasurementType.Ordinal;
            Assert.Equal(ColumnValueType.Ordinal, FormatMappers.MapSpss(ordinal)!.Type);
        }

        [Fact]
        public void MapSpss_plain_numeric_is_integer_overriding_identifier_heuristic()
        {
            // 선언된 F 숫자는 정수. 헤더가 'id'라도 Identifier 추론에 넘기지 않고 파일 명시를 존중.
            Assert.Equal(ColumnValueType.Integer, FormatMappers.MapSpss(NumericVar("id", FormatType.F))!.Type);
            Assert.Equal(ColumnValueType.Float, FormatMappers.MapSpss(NumericVar("x", FormatType.F, 2))!.Type);
        }

        [Fact]
        public void MapSpss_date_formats_map_to_temporal()
        {
            Assert.Equal(ColumnValueType.Date, FormatMappers.MapSpss(NumericVar("d", FormatType.DATE))!.Type);
            Assert.Equal(ColumnValueType.DateTime, FormatMappers.MapSpss(NumericVar("ts", FormatType.DATETIME))!.Type);
            Assert.Equal(ColumnValueType.Time, FormatMappers.MapSpss(NumericVar("t", FormatType.TIME))!.Type);
        }

        [Fact]
        public void MapSas_string_defers_to_inference()
            => Assert.Null(FormatMappers.MapSasFormat("String", "$", 0));

        [Fact]
        public void MapSas_plain_numeric_is_declared_integer_float()
        {
            // 무포맷/일반 숫자 포맷은 선언 숫자로(‘seq’ 헤더의 Identifier 오인 방지, SPSS와 대칭).
            Assert.Equal(ColumnValueType.Integer, FormatMappers.MapSasFormat("Double", "", 0)!.Type);
            Assert.Equal(ColumnValueType.Integer, FormatMappers.MapSasFormat("Double", "BEST", 0)!.Type);
            Assert.Equal(ColumnValueType.Float, FormatMappers.MapSasFormat("Double", "COMMA", 2)!.Type);
        }

        [Fact]
        public void MapSas_date_format_defers_to_inference()
            // 날짜 포맷은 리더가 DateTime으로 변환하므로 추론에 위임(Integer로 강제하지 않음).
            => Assert.Null(FormatMappers.MapSasFormat("Double", "YYMMDDS", 0));

        [Theory]
        [InlineData("DOLLAR", ColumnValueType.Currency, '$')]
        [InlineData("WON", ColumnValueType.Currency, '₩')]
        [InlineData("YEN", ColumnValueType.Currency, '¥')]
        [InlineData("EURO", ColumnValueType.Currency, '€')]
        public void MapSas_currency_formats(string fmt, ColumnValueType type, char sym)
        {
            var h = FormatMappers.MapSasFormat("Double", fmt, 0)!;
            Assert.Equal(type, h.Type);
            Assert.Equal(sym, h.CurrencySymbol);
        }

        [Fact]
        public void MapSas_percent_and_scientific()
        {
            Assert.Equal(ColumnValueType.Percent, FormatMappers.MapSasFormat("Double", "PERCENT", 0)!.Type);
            Assert.Equal(ColumnValueType.Scientific, FormatMappers.MapSasFormat("Double", "E", 0)!.Type);
        }

        private static Variable NumericVar(string name, FormatType fmt, int dec = 0) => new(name)
        {
            Type = DataType.Numeric,
            MeasurementType = MeasurementType.Scale,
            PrintFormat = new OutputFormat(fmt, 8, dec),
            WriteFormat = new OutputFormat(fmt, 8, dec),
        };

        [Fact]
        public void Import_sav_carries_declared_type_hints_and_drops_them_in_label_mode()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_savhint_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string sav = Path.Combine(dir, "clinic.sav");
                var salary = NumericVar("salary", FormatType.DOLLAR);
                var sex = NumericVar("sex", FormatType.F);
                sex.MeasurementType = MeasurementType.Nominal;
                sex.ValueLabels = new Dictionary<double, string> { { 1, "남" }, { 2, "여" } };
                using (var os = File.Create(sav))
                using (var w = new SpssWriter(os, new List<Variable> { salary, sex },
                    Array.Empty<Mrset>(), new SpssOptions(), leaveOpen: false))
                {
                    var r = w.CreateRecord(); r[0] = 1000.0; r[1] = 1.0; w.WriteRecord(r);
                    w.EndFile();
                }

                var hints = TabularImporter.Import(sav, Path.Combine(dir, "raw"), showLabels: false)[0].Hints;
                Assert.NotNull(hints);
                Assert.Equal(ColumnValueType.Currency, hints![0]!.Type);      // DOLLAR → 통화
                Assert.Equal('$', hints[0]!.CurrencySymbol);
                Assert.Equal(ColumnValueType.Categorical, hints[1]!.Type);    // 값라벨+명목 → 범주

                // 라벨 표시 모드에선 코드가 라벨 텍스트로 치환되므로 선언 힌트는 사용하지 않는다.
                Assert.Null(TabularImporter.Import(sav, Path.Combine(dir, "lbl"), showLabels: true)[0].Hints);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Imports_sav_raw_mode_keeps_codes_and_decodes_dates()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_savraw_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string sav = Path.Combine(dir, "survey.sav");
                CreateLabeledSav(sav);

                var sheets = TabularImporter.Import(sav, Path.Combine(dir, "out"), showLabels: false);

                var lines = File.ReadAllText(sheets[0].CsvPath).Replace("\r\n", "\n").Trim().Split('\n');
                Assert.Equal("sex,visit", lines[0]);        // 헤더 = 변수명
                Assert.Equal("1,2020-01-15", lines[1]);     // 원값(코드) + 날짜는 원값 모드에서도 디코딩
                Assert.Equal("2,2020-01-15", lines[2]);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Imports_sav_label_mode_uses_value_and_variable_labels()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_savlbl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string sav = Path.Combine(dir, "survey.sav");
                CreateLabeledSav(sav);

                var sheets = TabularImporter.Import(sav, Path.Combine(dir, "out"), showLabels: true);

                var lines = File.ReadAllText(sheets[0].CsvPath).Replace("\r\n", "\n").Trim().Split('\n');
                Assert.Equal("성별,방문일", lines[0]);        // 헤더 = 변수 라벨
                Assert.Equal("남,2020-01-15", lines[1]);      // 값 라벨로 치환(1→남), 날짜 유지
                Assert.Equal("여,2020-01-15", lines[2]);      // 2→여
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // sex(값라벨 1남/2여, 변수라벨 "성별") + visit(DATE 포맷, 변수라벨 "방문일"), 2행.
        private static void CreateLabeledSav(string path)
        {
            var sex = new Variable("sex")
            {
                Type = DataType.Numeric,
                MeasurementType = MeasurementType.Nominal,
                Label = "성별",
                PrintFormat = new OutputFormat(FormatType.F, 8, 0),
                WriteFormat = new OutputFormat(FormatType.F, 8, 0),
                ValueLabels = new Dictionary<double, string> { { 1, "남" }, { 2, "여" } },
            };
            var visit = new Variable("visit")
            {
                Type = DataType.Numeric,
                MeasurementType = MeasurementType.Scale,
                Label = "방문일",
                PrintFormat = new OutputFormat(FormatType.DATE, 11, 0),
                WriteFormat = new OutputFormat(FormatType.DATE, 11, 0),
            };
            double visitSeconds = (new DateTime(2020, 1, 15) - new DateTime(1582, 10, 14)).TotalSeconds;

            using var os = File.Create(path);
            using var writer = new SpssWriter(os, new List<Variable> { sex, visit },
                Array.Empty<Mrset>(), new SpssOptions(), leaveOpen: false);
            var r1 = writer.CreateRecord(); r1[0] = 1.0; r1[1] = visitSeconds; writer.WriteRecord(r1);
            var r2 = writer.CreateRecord(); r2[0] = 2.0; r2[1] = visitSeconds; writer.WriteRecord(r2);
            writer.EndFile();
        }

        [Fact]
        public void Imports_sav_with_values_and_maps_sysmis_to_empty()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_savtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string sav = Path.Combine(dir, "clinic.sav");
                CreateSampleSav(sav);

                var sheets = TabularImporter.Import(sav, Path.Combine(dir, "out"));

                Assert.Single(sheets);
                Assert.Equal("clinic", sheets[0].Name);   // 시트명 = 파일명

                var lines = File.ReadAllText(sheets[0].CsvPath).Replace("\r\n", "\n").Trim().Split('\n');
                Assert.Equal("id,name", lines[0]);
                Assert.Equal("1,Kim", lines[1]);
                Assert.Equal(",Lee", lines[2]);            // 시스템 결측(sysmis) → 빈 셀
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // 숫자 1개 + 텍스트 1개, 2행(2번째 행의 숫자는 sysmis)짜리 최소 .sav를 생성.
        private static void CreateSampleSav(string path)
        {
            var idVar = new Variable("id")
            {
                Type = DataType.Numeric,
                MeasurementType = MeasurementType.Scale,
                PrintFormat = new OutputFormat(FormatType.F, 8, 0),
                WriteFormat = new OutputFormat(FormatType.F, 8, 0),
            };
            var nameVar = new Variable("name")
            {
                Type = DataType.Text,
                MeasurementType = MeasurementType.Nominal,
                Width = 20,
                TextWidth = 20,
                PrintFormat = new OutputFormat(FormatType.A, 20, 0),
                WriteFormat = new OutputFormat(FormatType.A, 20, 0),
            };

            using var os = File.Create(path);
            using var writer = new SpssWriter(os, new List<Variable> { idVar, nameVar },
                Array.Empty<Mrset>(), new SpssOptions(), leaveOpen: false);

            var r1 = writer.CreateRecord(); r1[0] = 1.0; r1[1] = "Kim"; writer.WriteRecord(r1);
            var r2 = writer.CreateRecord(); r2[0] = double.NaN; r2[1] = "Lee"; writer.WriteRecord(r2);
            writer.EndFile();
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
