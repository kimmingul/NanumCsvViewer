using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 크래시 복구 저널. 저장하지 않은 편집(셀·컬럼 이름·삭제/추가 행)을 %LOCALAPPDATA%\NanumCsvViewer\edits 에
    /// 파일별 JSON 하나로 보관한다. 키 = 경로 + 크기 + 수정 시각(+시트 번호)이라 원본이 바뀌면 다른 키가 되어
    /// 엉뚱한 파일에 편집이 입혀지지 않는다. 저장·버리기 때 삭제하고, 같은 파일을 다시 열면 복구를 제안한다.
    /// </summary>
    public static class EditJournal
    {
        public const int FormatVersion = 1;

        public static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NanumCsvViewer", "edits");

        /// <summary>원본 파일 + 시트로 만든 키. 파일이 없거나 읽을 수 없으면 null(저널을 쓰지 않는다).</summary>
        public static string? KeyFor(string sourcePath, int sheetIndex = 0)
        {
            try
            {
                var info = new FileInfo(Path.GetFullPath(sourcePath));
                if (!info.Exists) return null;
                string id = string.Join("|", info.FullName.ToLowerInvariant(), info.Length, info.LastWriteTimeUtc.Ticks, sheetIndex);
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32].ToLowerInvariant();
            }
            catch { return null; }
        }

        public static string FilePath(string directory, string key) => Path.Combine(directory, key + ".json");

        public static bool Exists(string directory, string key) => File.Exists(FilePath(directory, key));

        /// <summary>스냅샷을 원자적으로(임시 파일 → 이동) 기록한다.</summary>
        public static void Write(string directory, string key, string sourcePath, int sheetIndex, EditSnapshot snapshot)
        {
            Directory.CreateDirectory(directory);
            var dto = new Dto
            {
                Version = FormatVersion,
                Source = sourcePath,
                Sheet = sheetIndex,
                SavedAtUtc = DateTime.UtcNow.ToString("O"),
                BaseRows = snapshot.BaseRows,
                Cells = snapshot.Cells.Select(c => new CellDto { Row = c.Row, Col = c.Col, Value = c.Value }).ToList(),
                Headers = snapshot.Headers.Select(h => new HeaderDto { Col = h.Col, Name = h.Name }).ToList(),
                Deleted = snapshot.Deleted.ToList(),
                Added = snapshot.Added.Select(a => new AddedDto { Anchor = a.Anchor, Values = a.Values }).ToList(),
            };
            string path = FilePath(directory, key);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(fs, dto);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 임시 파일 정리 실패는 무시 */ }
                throw;
            }
        }

        /// <summary>저널을 읽는다. 없으면 null, 깨졌거나 버전이 다르면 InvalidDataException.</summary>
        public static EditSnapshot? TryRead(string directory, string key)
        {
            string path = FilePath(directory, key);
            if (!File.Exists(path)) return null;
            Dto? dto;
            try
            {
                using var fs = File.OpenRead(path);
                dto = JsonSerializer.Deserialize<Dto>(fs);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("The recovery file could not be read.", ex);
            }
            if (dto is null || dto.Version != FormatVersion || dto.Cells is null || dto.Headers is null || dto.Deleted is null || dto.Added is null)
                throw new InvalidDataException("The recovery file has an unsupported format.");

            return new EditSnapshot(
                dto.BaseRows,
                dto.Cells.Select(c => (c.Row, c.Col, c.Value ?? throw new InvalidDataException("Null cell value."))).ToArray(),
                dto.Headers.Select(h => (h.Col, h.Name ?? throw new InvalidDataException("Null column name."))).ToArray(),
                dto.Deleted.ToArray(),
                dto.Added.Select(a => new AddedRow(a.Anchor, a.Values ?? throw new InvalidDataException("Null added row"))).ToArray());
        }

        public static void Delete(string directory, string key)
        {
            try
            {
                string path = FilePath(directory, key);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* 저널 삭제 실패가 저장·버리기를 막으면 안 된다 */ }
        }

        /// <summary>오래된 저널(원본이 바뀌어 더는 열 수 없는 것)을 정리한다.</summary>
        public static void Prune(string directory, TimeSpan maxAge)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                var cutoff = DateTime.UtcNow - maxAge;
                foreach (string f in Directory.EnumerateFiles(directory, "*.json"))
                    if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
            }
            catch { /* 정리는 최선 노력 */ }
        }

        private sealed class Dto
        {
            public int Version { get; set; }
            public string? Source { get; set; }
            public int Sheet { get; set; }
            public string? SavedAtUtc { get; set; }
            public int BaseRows { get; set; }
            public List<CellDto>? Cells { get; set; }
            public List<HeaderDto>? Headers { get; set; }
            public List<int>? Deleted { get; set; }
            public List<AddedDto>? Added { get; set; }
        }

        private sealed class CellDto { public int Row { get; set; } public int Col { get; set; } public string? Value { get; set; } }
        private sealed class HeaderDto { public int Col { get; set; } public string? Name { get; set; } }
        private sealed class AddedDto { public int Anchor { get; set; } public string[]? Values { get; set; } }
    }
}
