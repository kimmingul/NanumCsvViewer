using System.Globalization;
using System.IO;
using System.Text;
using Curiosity.SPSS.DataReader;
using Curiosity.SPSS.SpssDataset;
using ExcelDataReader;
using Microsoft.Data.Sqlite;
using NanumCsvViewer.Csv;
using SasReader;

namespace NanumCsvViewer.Import
{
    /// <summary>임포트된 한 시트의 이름·변환된 임시 CSV 경로·컬럼별 선언 타입 힌트(SAS/SPSS만, 없으면 null).
    /// AllowedCodes = 컬럼별 허용 코드 집합(SPSS 값라벨·SAS 카탈로그, 원값 모드에서만) — 데이터 품질
    /// 코드북 대조(이슈 #26)의 입력. 라벨 표시 모드에서는 셀이 라벨로 치환되므로 null.</summary>
    public sealed record ImportedSheet(string Name, string CsvPath, IReadOnlyList<ColumnTypeHint?>? Hints = null,
        IReadOnlyList<IReadOnlySet<string>?>? AllowedCodes = null);

    /// <summary>
    /// 엑셀(xlsx/xls)·SAS(sas7bdat)·SPSS(sav)·SQLite(db/sqlite) 파일을 시트별 UTF-8 CSV로 변환한다.
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

        private static readonly string[] Extensions =
            { ".xlsx", ".xlsm", ".xls", ".sas7bdat", ".sav", ".db", ".sqlite", ".sqlite3" };
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
                ".db" or ".sqlite" or ".sqlite3" => ImportSqlite(path, tempDir),
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

        // SAS 변수 라벨은 파일 안에 있고, 값 라벨은 파일 밖 포맷 카탈로그(.sas7bcat)에 있다.
        // 라벨 표시 모드면 헤더를 변수 라벨로 바꾸고, 동반 카탈로그가 있으면 값도 라벨로 치환한다(이슈 #20).
        private static List<ImportedSheet> ImportSas(string path, string tempDir, bool showLabels)
        {
            using var stream = File.OpenRead(path);
            var reader = new SasFileReaderImpl(stream);
            var props = reader.getSasFileProperties();
            string name = string.IsNullOrWhiteSpace(props.getName())
                ? Path.GetFileNameWithoutExtension(path) : props.getName();
            string csv = Path.Combine(tempDir, "sheet_0.csv");

            // 카탈로그 탐색: 동일 파일명 우선, 없으면 SAS 관례상 formats.sas7bcat.
            // 라벨 모드는 값 치환용, 원값 모드는 코드북 대조(허용 코드 집합)용으로 쓴다.
            SasCatalog? found = FindSasCatalog(path);
            SasCatalog? catalog = showLabels ? found : null;

            var columns = reader.getColumns();
            var formatNames = columns.Select(col => col.getFormat()?.getName()).ToArray();
            // 카탈로그 라벨이 적용되는 컬럼은 값이 문자열(라벨)로 바뀌므로 선언 숫자 힌트가 무의미 → 추론 위임.
            // 그 외 컬럼(헤더만 변경)은 선언 힌트가 항상 유효.
            var hints = columns.Select((col, i) =>
                catalog?.HasFormat(formatNames[i]) == true ? null : FormatMappers.MapSas(col)).ToArray();
            // 원값 모드에서만 허용 코드 집합 구성(라벨 모드는 셀이 라벨이라 대조 무의미).
            var allowedCodes = showLabels || found is null
                ? null
                : formatNames.Select(found.TryGetCodes).ToArray();

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
                    for (int c = 0; c < row.Length; c++)
                        cells[c] = Escape(
                            (catalog is not null && c < formatNames.Length
                                ? catalog.TryLabel(formatNames[c], row[c]) : null)
                            ?? FormatCell(row[c]));
                    writer.WriteLine(string.Join(",", cells));
                }
            }
            return new List<ImportedSheet> { new(name, csv, hints, allowedCodes) };
        }

        // 동반 카탈로그 탐색: <파일명>.sas7bcat → formats.sas7bcat 순. 읽기 실패는 null(라벨 없이 진행).
        private static SasCatalog? FindSasCatalog(string sasPath)
        {
            string dir = Path.GetDirectoryName(sasPath) ?? ".";
            string sameName = Path.Combine(dir, Path.GetFileNameWithoutExtension(sasPath) + ".sas7bcat");
            if (File.Exists(sameName) && SasCatalogReader.TryRead(sameName) is { FormatCount: > 0 } c1) return c1;
            string conventional = Path.Combine(dir, "formats.sas7bcat");
            if (File.Exists(conventional) && SasCatalogReader.TryRead(conventional) is { FormatCount: > 0 } c2) return c2;
            return null;
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
            // 원값 모드에서만 값 라벨 키(허용 코드) 집합 구성 — 코드북 대조(이슈 #26) 입력.
            // 숫자 코드는 FormatCell과 동일한 형식으로 정규화해 CSV 셀 텍스트와 그대로 비교한다.
            var allowedCodes = showLabels ? null : vars.Select(SpssAllowedCodes).ToArray();

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
            return new List<ImportedSheet> { new(name, csv, hints, allowedCodes) };
        }

        // SPSS 변수의 값 라벨 키 집합(숫자 코드 → CSV 셀 텍스트 형식). 라벨이 없으면 null.
        private static IReadOnlySet<string>? SpssAllowedCodes(Variable v)
        {
            if (v.ValueLabels is not { Count: > 0 } labels) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (double code in labels.Keys)
                set.Add(code.ToString("0.################", CultureInfo.InvariantCulture));
            return set;
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

        // SQLite(이슈 #16): 테이블·뷰 하나가 시트 하나. 읽기 전용으로 열어 원본을 건드리지 않는다.
        // SQLite에 날짜 타입은 없으므로(TEXT/INTEGER 저장) 기존 CSV 타입 추론이 그대로 동작한다.
        private static List<ImportedSheet> ImportSqlite(string path, string tempDir)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false, // 임포트 후 파일 잠금이 남지 않도록
            };
            using var conn = new SqliteConnection(builder.ConnectionString);
            conn.Open();

            var names = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master " +
                                  "WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var r = cmd.ExecuteReader();
                while (r.Read()) names.Add(r.GetString(0));
            }

            var sheets = new List<ImportedSheet>(names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                string csv = Path.Combine(tempDir, $"sheet_{i}.csv");
                using var cmd = conn.CreateCommand();
                // 식별자는 파라미터 바인딩이 불가하므로 "…" 인용 + 내부 따옴표 이스케이프로 안전 처리.
                cmd.CommandText = $"SELECT * FROM \"{names[i].Replace("\"", "\"\"")}\"";
                using var reader = cmd.ExecuteReader();
                using var writer = NewCsvWriter(csv);
                var header = new string[reader.FieldCount];
                for (int c = 0; c < reader.FieldCount; c++) header[c] = Escape(reader.GetName(c));
                writer.WriteLine(string.Join(",", header));
                while (reader.Read())
                {
                    var cells = new string[reader.FieldCount];
                    for (int c = 0; c < reader.FieldCount; c++)
                        cells[c] = Escape(FormatCell(reader.GetValue(c)));
                    writer.WriteLine(string.Join(",", cells));
                }
                sheets.Add(new ImportedSheet(names[i], csv));
            }
            return sheets;
        }

        private static StreamWriter NewCsvWriter(string path)
            => new(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // 셀 값을 CSV 텍스트로. 날짜/숫자는 타입 추론이 잘 받도록 일관 형식으로 출력.
        private static string FormatCell(object? value) => value switch
        {
            null => "",
            DBNull => "",                                     // SQLite NULL
            byte[] b => $"(BLOB {b.Length:N0} B)",            // SQLite BLOB: 내용 대신 크기 표시
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
        private readonly IReadOnlyList<IReadOnlySet<string>?>?[] _allowedCodes;
        private readonly string _tempDir;

        private WorkbookSession(string sourcePath, string tempDir, IReadOnlyList<ImportedSheet> sheets, bool showLabels)
        {
            SourcePath = sourcePath;
            _tempDir = tempDir;
            ShowLabels = showLabels;
            SheetNames = sheets.Select(s => s.Name).ToArray();
            _csvPaths = sheets.Select(s => s.CsvPath).ToArray();
            _hints = sheets.Select(s => s.Hints).ToArray();
            _allowedCodes = sheets.Select(s => s.AllowedCodes).ToArray();
        }

        /// <summary>해당 시트의 컬럼별 선언 타입 힌트(SAS/SPSS만, 없으면 null).</summary>
        public IReadOnlyList<ColumnTypeHint?>? ColumnHints(int sheetIndex)
            => sheetIndex >= 0 && sheetIndex < _hints.Length ? _hints[sheetIndex] : null;

        /// <summary>해당 시트의 컬럼별 허용 코드 집합(코드북 대조용 — 원값 모드 SPSS/SAS만, 없으면 null).</summary>
        public IReadOnlyList<IReadOnlySet<string>?>? AllowedCodes(int sheetIndex)
            => sheetIndex >= 0 && sheetIndex < _allowedCodes.Length ? _allowedCodes[sheetIndex] : null;

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
