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
        public bool MatchAny { get; set; }   // 활성 조건 결합: false=AND(모두), true=OR(하나라도)

        /// <summary>
        /// 조건부 서식 규칙(파일별). null = 이 저장본에는 서식 정보가 없음(이전 버전이 쓴 파일 포함) — 저장할 때 기존 서식을 그대로 이어받는다.
        /// 빈 목록 = 서식 없음으로 저장됨.
        /// </summary>
        public List<ConditionalFormatRule>? ConditionalFormats { get; set; }

        [JsonIgnore]
        public IReadOnlyList<SortKey> Sort =>
            SortKeys.Select(s => new SortKey(s.Column, s.Ascending)).ToArray();

        public static SavedCsvView Create(
            string name, string? filterText, int? filterColumn,
            IReadOnlyList<SortKey> sortKeys, IEnumerable<int> hiddenColumns,
            CsvSearchQuery? searchQuery, int currentColumn, ColumnFilterState? columnFilters = null,
            bool matchAny = false)
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
                ColumnFilters = columnFilters is null || columnFilters.IsEmpty ? null : columnFilters,
                MatchAny = matchAny
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
        /// <summary>
        /// 테스트에서 저장 위치를 임시 폴더로 바꾼다(null = 기본 위치). 실행 흐름별(AsyncLocal)이라 병렬 테스트끼리 서로의 위치를 바꾸지 않는다
        /// (설정한 흐름에서 시작한 스레드에는 함께 전달된다).
        /// </summary>
        private static readonly AsyncLocal<string?> s_directoryOverride = new();
        internal static string? DirectoryOverride { get => s_directoryOverride.Value; set => s_directoryOverride.Value = value; }

        private static string Dir => DirectoryOverride ?? Path.Combine(
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
                // "현재 보기 저장"은 서식 규칙을 모른다 — 규칙이 없는 저장본이면 이미 저장된 규칙을 유지한다.
                if (view.ConditionalFormats is null) view.ConditionalFormats = Load(csvPath)?.ConditionalFormats;
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathFor(csvPath),
                    JsonSerializer.Serialize(view, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 저장 실패는 무시 */ }
        }

        /// <summary>파일에 저장된 조건부 서식 규칙. 저장본이 없거나 서식 정보가 없으면 빈 목록.</summary>
        public static List<ConditionalFormatRule> LoadConditionalFormats(string csvPath)
            => Load(csvPath)?.ConditionalFormats?.ToList() ?? new List<ConditionalFormatRule>();

        /// <summary>조건부 서식 규칙만 갱신한다(필터·정렬 등 다른 저장 내용은 그대로, 저장본이 없으면 새로 만든다).</summary>
        public static void SaveConditionalFormats(string csvPath, IReadOnlyList<ConditionalFormatRule> rules)
        {
            var view = Load(csvPath) ?? new SavedCsvView { Name = "view" };
            view.ConditionalFormats = rules.ToList();
            Save(csvPath, view);
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
