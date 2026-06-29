using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NanumCsvViewer.Csv
{
    /// <summary>파일별로 저장하는 뷰 상태: 필터·정렬·숨김컬럼·검색·현재컬럼. macOS SavedCsvView 이식.</summary>
    public sealed class SavedCsvView
    {
        public string Name { get; set; } = "";
        public string? FilterText { get; set; }
        public int? FilterColumn { get; set; }
        public List<SavedSortKey> SortKeys { get; set; } = new();
        public List<int> HiddenColumnIndexes { get; set; } = new();
        public string? SearchText { get; set; }
        public CsvSearchMode? SearchMode { get; set; }
        public int? SearchColumn { get; set; }
        public int CurrentColumn { get; set; }
        public ColumnFilterState? ColumnFilters { get; set; }

        [JsonIgnore]
        public IReadOnlyList<SortKey> Sort =>
            SortKeys.Select(s => new SortKey(s.Column, s.Ascending)).ToArray();

        public static SavedCsvView Create(
            string name, string? filterText, int? filterColumn,
            IReadOnlyList<SortKey> sortKeys, IEnumerable<int> hiddenColumns,
            CsvSearchQuery? searchQuery, int currentColumn, ColumnFilterState? columnFilters = null)
            => new()
            {
                Name = name,
                FilterText = filterText,
                FilterColumn = filterColumn,
                SortKeys = sortKeys.Select(s => new SavedSortKey { Column = s.Column, Ascending = s.Ascending }).ToList(),
                HiddenColumnIndexes = hiddenColumns.Distinct().OrderBy(i => i).ToList(),
                SearchText = searchQuery?.Text,
                SearchMode = searchQuery?.Mode,
                SearchColumn = searchQuery?.Column,
                CurrentColumn = Math.Max(0, currentColumn),
                ColumnFilters = columnFilters is null || columnFilters.IsEmpty ? null : columnFilters
            };
    }

    public sealed class SavedSortKey
    {
        public int Column { get; set; }
        public bool Ascending { get; set; }
    }

    /// <summary>%LocalAppData%\NanumCsvViewer\views\ 에 파일 경로 해시별 JSON으로 저장.</summary>
    public static class SavedViewStore
    {
        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NanumCsvViewer", "views");

        private static string PathFor(string csvPath)
        {
            string full = Path.GetFullPath(csvPath);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
            string name = Convert.ToHexString(hash, 0, 8);
            return Path.Combine(Dir, name + ".json");
        }

        public static void Save(string csvPath, SavedCsvView view)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathFor(csvPath),
                    JsonSerializer.Serialize(view, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 저장 실패는 무시 */ }
        }

        public static SavedCsvView? Load(string csvPath)
        {
            try
            {
                string path = PathFor(csvPath);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<SavedCsvView>(File.ReadAllText(path));
            }
            catch { /* 손상 시 무시 */ }
            return null;
        }
    }
}
