using System.Globalization;
using System.IO;
using System.Text;
using Curiosity.SPSS.DataReader;
using Curiosity.SPSS.SpssDataset;
using ExcelDataReader;
using NanumCsvViewer.Csv;
using SasReader;

namespace NanumCsvViewer.Import
{
    /// <summary>임포트된 한 시트의 이름·변환된 임시 CSV 경로·컬럼별 선언 타입 힌트(SAS/SPSS만, 없으면 null).</summary>
    public sealed record ImportedSheet(string Name, string CsvPath, IReadOnlyList<ColumnTypeHint?>? Hints = null);

    /// <summary>
    /// 엑셀(xlsx/xls)·SAS(sas7bdat)·SPSS(sav) 파일을 시트별 UTF-8 CSV로 변환한다.
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

        private static readonly string[] Extensions = { ".xlsx", ".xlsm", ".xls", ".sas7bdat", ".sav" };
        private static readonly char[] CsvSpecials = { ',', '"', '\n', '\r' };

        public static bool IsImportable(string path)
            => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>SPSS·SAS처럼 변수/변수라벨이 있는 포맷인지(필드 라벨 토글 대상).</summary>
        public static bool SupportsFieldLabels(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".sav" or ".sas7bdat";
        }

        /// <summary>
        /// 각 시트를 tempDir 안의 CSV로 변환하고 (이름, CSV 경로) 목록을 반환.
        /// showLabels=true면 SPSS·SAS에서 변수 라벨을 헤더로 쓰고, SPSS는 값 라벨로 코드를 치환한다.
        /// </summary>
        public static IReadOnlyList<ImportedSheet> Import(string path, string tempDir, bool showLabels = false)
        {
            Directory.CreateDirectory(tempDir);
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".sas7bdat" => ImportSas(path, tempDir, showLabels),
                ".sav" => ImportSpss(path, tempDir, showLabels),
                _ => ImportExcel(path, tempDir),
            };
        }

        // 필드 라벨 표시 모드면 라벨(있을 때)을, 아니면 원래 이름을 헤더로 쓴다. SPSS·SAS 공통.
        private static string ResolveHeader(string name, string? label, bool showLabels)
            => showLabels && !string.IsNullOrWhiteSpace(label) ? label! : name;

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

        // SAS는 변수 라벨은 있으나 값 라벨은 파일 밖(.sas7bcat 카탈로그)에 있어 코드 치환 불가.
        // showLabels는 헤더를 변수 라벨로 바꾸는 데만 쓴다. 날짜는 리더가 이미 DateTime으로 반환.
        private static List<ImportedSheet> ImportSas(string path, string tempDir, bool showLabels)
        {
            using var stream = File.OpenRead(path);
            var reader = new SasFileReaderImpl(stream);
            var props = reader.getSasFileProperties();
            string name = string.IsNullOrWhiteSpace(props.getName())
                ? Path.GetFileNameWithoutExtension(path) : props.getName();
            string csv = Path.Combine(tempDir, "sheet_0.csv");

            var columns = reader.getColumns();
            // SAS 값은 라벨 표시 모드에서도 바뀌지 않으므로(헤더만 변경) 선언 힌트는 항상 유효.
            var hints = columns.Select(FormatMappers.MapSas).ToArray();

            using (var writer = NewCsvWriter(csv))
            {
                writer.WriteLine(string.Join(",", columns.Select(col =>
                    Escape(ResolveHeader(col.getName(), col.getLabel(), showLabels)))));
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
            return new List<ImportedSheet> { new(name, csv, hints) };
        }

        // SPSS(.sav)는 단일 데이터셋 → 시트 1개. 헤더는 변수명, 값은 원값(코드)을 그대로 내보내
        // 기존 타입 추론이 정상 동작하게 한다. Value Label 전개·Variable Label 노출은 후속(Phase 2).
        private static List<ImportedSheet> ImportSpss(string path, string tempDir, bool showLabels)
        {
            using var stream = File.OpenRead(path);
            using var reader = new SpssReader(stream);
            var vars = reader.Variables.ToList();
            string name = Path.GetFileNameWithoutExtension(path);
            string csv = Path.Combine(tempDir, "sheet_0.csv");

            // 라벨 표시 모드면 값 라벨로 코드를 치환하므로 선언 타입 힌트는 무의미(문자 표시) → null.
            var hints = showLabels ? null
                : vars.Select(FormatMappers.MapSpss).ToArray();

            using (var writer = NewCsvWriter(csv))
            {
                writer.WriteLine(string.Join(",", vars.Select(v =>
                    Escape(ResolveHeader(SpssHeaderName(v.Name), v.Label, showLabels)))));
                foreach (var record in reader.Records)
                {
                    var cells = new string[vars.Count];
                    for (int c = 0; c < vars.Count; c++)
                        cells[c] = Escape(FormatSpssCell(record.GetValue(vars[c]), vars[c], showLabels));
                    writer.WriteLine(string.Join(",", cells));
                }
            }
            return new List<ImportedSheet> { new(name, csv, hints) };
        }

        // 라벨 모드에서 값 라벨이 있으면 코드를 라벨로 치환(예: 1→"남"). 없으면 원값 포맷.
        // 날짜 변수는 리더가 DateTime을 돌려주므로 FormatCell이 그대로 yyyy-MM-dd로 출력한다.
        private static string FormatSpssCell(object? value, Variable variable, bool showLabels)
        {
            if (showLabels && value is double d && variable.ValueLabels is { } labels
                && labels.TryGetValue(d, out var label) && !string.IsNullOrEmpty(label))
                return label;
            return FormatCell(value);
        }

        // Curiosity.SPSS는 숫자로 시작하는 변수명에 '@'를 접두한다(SPSS 식별자 규칙 표현).
        // SPSS 식별자는 '@'로 시작할 수 없으므로 선행 '@' 하나를 제거해 원래 표시명을 복원한다.
        internal static string SpssHeaderName(string name)
            => name.Length > 1 && name[0] == '@' ? name[1..] : name;

        private static StreamWriter NewCsvWriter(string path)
            => new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 셀 값을 CSV 텍스트로. 날짜/숫자는 타입 추론이 잘 받도록 일관 형식으로 출력.
        private static string FormatCell(object? value) => value switch
        {
            null => "",
            DateTime dt => dt.TimeOfDay == TimeSpan.Zero
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            double d when double.IsNaN(d) => "",   // SPSS 시스템 결측(sysmis)은 NaN → 빈 셀
            double d => d.ToString("0.################", CultureInfo.InvariantCulture),
            float f when float.IsNaN(f) => "",
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
        /// <summary>필드 라벨 표시 모드로 임포트되었는지.</summary>
        public bool ShowLabels { get; }
        /// <summary>이 워크북이 변수/변수라벨을 가진 포맷(SPSS·SAS)이라 라벨 토글이 의미 있는지.</summary>
        public bool SupportsFieldLabels => TabularImporter.SupportsFieldLabels(SourcePath);
        private readonly string[] _csvPaths;
        private readonly IReadOnlyList<ColumnTypeHint?>?[] _hints;
        private readonly string _tempDir;

        private WorkbookSession(string sourcePath, string tempDir, IReadOnlyList<ImportedSheet> sheets, bool showLabels)
        {
            SourcePath = sourcePath;
            _tempDir = tempDir;
            ShowLabels = showLabels;
            SheetNames = sheets.Select(s => s.Name).ToArray();
            _csvPaths = sheets.Select(s => s.CsvPath).ToArray();
            _hints = sheets.Select(s => s.Hints).ToArray();
        }

        /// <summary>해당 시트의 컬럼별 선언 타입 힌트(SAS/SPSS만, 없으면 null).</summary>
        public IReadOnlyList<ColumnTypeHint?>? ColumnHints(int sheetIndex)
            => sheetIndex >= 0 && sheetIndex < _hints.Length ? _hints[sheetIndex] : null;

        public static WorkbookSession Create(string path, bool showLabels = false)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ncv_wb_" + Guid.NewGuid().ToString("N"));
            var sheets = TabularImporter.Import(path, tempDir, showLabels);
            if (sheets.Count == 0)
            {
                try { Directory.Delete(tempDir, true); } catch { }
                throw new InvalidDataException("열 수 있는 시트가 없습니다.");
            }
            return new WorkbookSession(path, tempDir, sheets, showLabels);
        }

        public string CsvPath(int sheetIndex) => _csvPaths[sheetIndex];

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { /* 임시폴더 정리 실패는 무시 */ }
        }
    }
}
