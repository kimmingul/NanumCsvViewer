using System.IO;
using NanumCsvViewer.Import;

namespace NanumCsvViewer.Tests
{
    // .sas7bcat 파서(이슈 #20). 기대값은 pyreadstat(ReadStat 래퍼) read_sas7bcat 출력과 대조.
    public class SasCatalogReaderTests
    {
        private static string Fixture(string name)
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sas_catalog", name);

        [Theory]
        [InlineData("test_formats_linux.sas7bcat")]
        [InlineData("test_formats_win.sas7bcat")]
        public void Reads_catalog_value_labels(string file)
        {
            var catalog = SasCatalogReader.TryRead(Fixture(file));
            Assert.NotNull(catalog);
            // pyreadstat: {"$A": {"1": "Male", "2": "Female"}, "$B": {"2": "Female", "1": "Male"}}
            Assert.True(catalog!.HasFormat("$A"));
            Assert.True(catalog.HasFormat("$B"));
            Assert.Equal("Male", catalog.TryLabel("$A", "1"));
            Assert.Equal("Female", catalog.TryLabel("$A", "2"));
            Assert.Equal("Male", catalog.TryLabel("$B", "1"));
            Assert.Equal("Female", catalog.TryLabel("$B", "2"));
            // 라벨되지 않은 값·미지 포맷은 null(원값 표시)
            Assert.Null(catalog.TryLabel("$A", "3"));
            Assert.Null(catalog.TryLabel("NOPE", "1"));
        }

        [Fact]
        public void Format_name_matching_is_flexible()
        {
            var catalog = SasCatalogReader.TryRead(Fixture("test_formats_linux.sas7bcat"))!;
            // '$' 유무·대소문자·너비 표기(w.d)에 무관하게 매칭
            Assert.True(catalog.HasFormat("A"));
            Assert.True(catalog.HasFormat("$a"));
            Assert.True(catalog.HasFormat("$A8."));
            Assert.Equal("Male", catalog.TryLabel("A", "1"));
        }

        [Fact]
        public void TryRead_returns_null_for_non_catalog_file()
        {
            // sas7bdat은 magic이 달라 카탈로그로 열리지 않는다.
            Assert.Null(SasCatalogReader.TryRead(Fixture("test_data_linux.sas7bdat")));
        }

        // ---- ImportSas 통합: 동반 카탈로그 자동 감지 + 값 라벨 치환 ----

        [Theory]
        [InlineData("linux")]
        [InlineData("win")]
        public void Import_applies_labels_from_same_name_catalog(string plat)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 이슈 #20 시나리오: 동일 파일명의 .sas7bcat이 옆에 있으면 함께 불러온다.
                File.Copy(Fixture($"test_data_{plat}.sas7bdat"), Path.Combine(dir, "data.sas7bdat"));
                File.Copy(Fixture($"test_formats_{plat}.sas7bcat"), Path.Combine(dir, "data.sas7bcat"));

                var sheets = TabularImporter.Import(Path.Combine(dir, "data.sas7bdat"),
                    Path.Combine(dir, "out"), showLabels: true);

                // pyreadstat sas_formatted.csv와 동일해야 한다.
                var lines = File.ReadAllLines(sheets[0].CsvPath);
                Assert.Equal("ID,SEXA,SEXB", lines[0]);
                Assert.Equal("ID1,Male,Male", lines[1]);
                Assert.Equal("ID2,Female,Female", lines[2]);
                Assert.Equal("ID3,Male,Male", lines[3]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Import_falls_back_to_conventional_formats_catalog()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.Copy(Fixture("test_data_linux.sas7bdat"), Path.Combine(dir, "data.sas7bdat"));
                File.Copy(Fixture("test_formats_linux.sas7bcat"), Path.Combine(dir, "formats.sas7bcat"));

                var sheets = TabularImporter.Import(Path.Combine(dir, "data.sas7bdat"),
                    Path.Combine(dir, "out"), showLabels: true);
                Assert.Equal("ID1,Male,Male", File.ReadAllLines(sheets[0].CsvPath)[1]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void Import_without_label_mode_keeps_raw_codes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.Copy(Fixture("test_data_linux.sas7bdat"), Path.Combine(dir, "data.sas7bdat"));
                File.Copy(Fixture("test_formats_linux.sas7bcat"), Path.Combine(dir, "data.sas7bcat"));

                // 라벨 모드가 아니면 카탈로그가 있어도 원값(코드) 유지.
                var sheets = TabularImporter.Import(Path.Combine(dir, "data.sas7bdat"),
                    Path.Combine(dir, "out"), showLabels: false);
                Assert.Equal("ID1,1,1", File.ReadAllLines(sheets[0].CsvPath)[1]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
