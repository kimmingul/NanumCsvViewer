using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;
using SasReader;

namespace NanumCsvViewer.Import
{
    /// <summary>임포트된 한 시트(=엑셀 시트 또는 SAS 데이터셋)의 이름과 변환된 임시 CSV 경로.</summary>
    public sealed record ImportedSheet(string Name, string CsvPath);

    /// <summary>
    /// 엑셀(xlsx/xls)·SAS(sas7bdat) 파일을 시트별 UTF-8 CSV로 변환한다.
    /// 변환 결과를 기존 CSV 엔진(VirtualCsvDocument)이 그대로 열어 모든 기능을 재사용한다.
    /// </summary>
    public static class TabularImporter
    {
        static TabularImporter()
        {
            // ExcelDataReader는 기본 설정에서 코드페이지 1252를 요구한다(.xls 디코딩에도 필요).
            // 중복 등록은 안전하므로 임포터가 호출자와 무관하게 동작하도록 여기서 보장한다.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private static readonly string[] Extensions = { ".xlsx", ".xlsm", ".xls", ".sas7bdat" };
        private static readonly char[] CsvSpecials = { ',', '"', '\n', '\r' };

        public static bool IsImportable(string path)
            => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>각 시트를 tempDir 안의 CSV로 변환하고 (이름, CSV 경로) 목록을 반환.</summary>
        public static IReadOnlyList<ImportedSheet> Import(string path, string tempDir)
        {
            Directory.CreateDirectory(tempDir);
            return Path.GetExtension(path).ToLowerInvariant() == ".sas7bdat"
                ? ImportSas(path, tempDir)
                : ImportExcel(path, tempDir);
        }

        private static List<ImportedSheet> ImportExcel(string path, string tempDir)
        {
            var sheets = new List<ImportedSheet>();
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read);
            using var reader = ExcelReaderFactory.CreateReader(stream); // xlsx/xls 자동 감지
            int index = 0;
            do
            {
                string name = string.IsNullOrWhiteSpace(reader.Name) ? $"Sheet{index + 1}" : reader.Name;
                string csv = Path.Combine(tempDir, $"sheet_{index}.csv");
                int rows = 0;
                using (var writer = NewCsvWriter(csv))
                {
                    while (reader.Read())
                    {
                        var cells = new string[reader.FieldCount];
                        for (int c = 0; c < reader.FieldCount; c++)
                            cells[c] = Escape(FormatCell(reader.GetValue(c)));
                        writer.WriteLine(string.Join(",", cells));
                        rows++;
                    }
                    if (rows == 0) writer.WriteLine(""); // 빈 시트도 열 수 있게 최소 1줄
                }
                sheets.Add(new ImportedSheet(name, csv));
                index++;
            } while (reader.NextResult());
            return sheets;
        }

        private static List<ImportedSheet> ImportSas(string path, string tempDir)
        {
            using var stream = File.OpenRead(path);
            var reader = new SasFileReaderImpl(stream);
            var props = reader.getSasFileProperties();
            string name = string.IsNullOrWhiteSpace(props.getName())
                ? Path.GetFileNameWithoutExtension(path) : props.getName();
            string csv = Path.Combine(tempDir, "sheet_0.csv");

            using (var writer = NewCsvWriter(csv))
            {
                var columns = reader.getColumns();
                writer.WriteLine(string.Join(",", columns.Select(col => Escape(col.getName()))));
                long rowCount = props.getRowCount();
                for (long i = 0; i < rowCount; i++)
                {
                    object[] row = reader.readNext();
                    if (row is null) break;
                    var cells = new string[row.Length];
                    for (int c = 0; c < row.Length; c++) cells[c] = Escape(FormatCell(row[c]));
                    writer.WriteLine(string.Join(",", cells));
                }
            }
            return new List<ImportedSheet> { new(name, csv) };
        }

        private static StreamWriter NewCsvWriter(string path)
            => new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 셀 값을 CSV 텍스트로. 날짜/숫자는 타입 추론이 잘 받도록 일관 형식으로 출력.
        private static string FormatCell(object? value) => value switch
        {
            null => "",
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double d => d.ToString("0.################", CultureInfo.InvariantCulture),
            float f => f.ToString("0.################", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };

        private static string Escape(string s)
            => s.IndexOfAny(CsvSpecials) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>멀티시트 임포트 1건의 수명: 임시 CSV 폴더 + 시트 목록. Dispose 시 임시 폴더 삭제.</summary>
    public sealed class WorkbookSession : IDisposable
    {
        public string SourcePath { get; }
        public IReadOnlyList<string> SheetNames { get; }
        private readonly string[] _csvPaths;
        private readonly string _tempDir;

        private WorkbookSession(string sourcePath, string tempDir, IReadOnlyList<ImportedSheet> sheets)
        {
            SourcePath = sourcePath;
            _tempDir = tempDir;
            SheetNames = sheets.Select(s => s.Name).ToArray();
            _csvPaths = sheets.Select(s => s.CsvPath).ToArray();
        }

        public static WorkbookSession Create(string path)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ncv_wb_" + Guid.NewGuid().ToString("N"));
            var sheets = TabularImporter.Import(path, tempDir);
            if (sheets.Count == 0)
            {
                try { Directory.Delete(tempDir, true); } catch { }
                throw new InvalidDataException("열 수 있는 시트가 없습니다.");
            }
            return new WorkbookSession(path, tempDir, sheets);
        }

        public string CsvPath(int sheetIndex) => _csvPaths[sheetIndex];

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { /* 임시폴더 정리 실패는 무시 */ }
        }
    }
}
