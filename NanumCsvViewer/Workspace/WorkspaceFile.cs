using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NanumCsvViewer.Workspace
{
    /// <summary>작업 공간 파일(.ncvws)을 읽지 못한 이유.</summary>
    public enum WorkspaceFileError
    {
        /// <summary>JSON이 아니거나 손상되었다.</summary>
        Corrupt,
        /// <summary>JSON이지만 작업 공간 파일이 아니다.</summary>
        NotAWorkspace,
        /// <summary>더 새로운 버전의 앱이 만든 파일이다(이 앱이 아는 버전보다 높다).</summary>
        TooNew,
    }

    public sealed class WorkspaceFileException : Exception
    {
        public WorkspaceFileException(WorkspaceFileError error, string message, int fileVersion = 0) : base(message)
        {
            Error = error;
            FileVersion = fileVersion;
        }

        public WorkspaceFileError Error { get; }
        /// <summary>파일에 적힌 버전(알 수 없으면 0).</summary>
        public int FileVersion { get; }
    }

    /// <summary>
    /// 작업 공간 원본 하나. 경로는 작업 공간 파일 폴더 기준 상대 경로(<see cref="Path"/>, '/' 구분, 불가능하면 null)와 절대 경로
    /// (<see cref="AbsolutePath"/>)를 함께 적는다 — 폴더째 옮기면 상대 경로로, 작업 공간 파일만 옮기면 절대 경로로 찾는다.
    /// 데이터는 저장하지 않는다.
    /// </summary>
    public sealed class WorkspaceFileSource
    {
        /// <summary>"csv" 또는 "database"(엑셀·SAS·SPSS·SQLite 파일 하나 = 스키마 하나).</summary>
        public string Kind { get; set; } = KindCsv;
        /// <summary>작업 공간에서 쓰는 이름(CSV는 표 이름, DB는 스키마 이름). 뷰의 SQL이 이 이름을 쓴다.</summary>
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public string AbsolutePath { get; set; } = "";
        /// <summary>인코딩 이름("UTF-8"·"UTF-8 (BOM)"·"CP949 / EUC-KR"…). null이면 열 때 자동 감지(CSV만).</summary>
        public string? Encoding { get; set; }
        /// <summary>구분자 한 글자. null이면 자동 판별(CSV만).</summary>
        public string? Delimiter { get; set; }
        public bool HasHeader { get; set; } = true;
        /// <summary>DB 원본의 표(시트) 이름 목록. 다시 열 때 달라졌는지 알려 주는 용도.</summary>
        public List<string>? Tables { get; set; }

        public const string KindCsv = "csv";
        public const string KindDatabase = "database";
    }

    /// <summary>뷰 정의: 이름 + SQL + 저장 안 한 편집 포함 여부. 결과는 저장하지 않는다(열 때 다시 계산 — 그때까지 "오래된" 상태).</summary>
    public sealed class WorkspaceFileView
    {
        public string Name { get; set; } = "";
        public string Sql { get; set; } = "";
        public bool IncludeUnsavedEdits { get; set; }
    }

    /// <summary>
    /// 열려 있던 탭 하나. 탭별 보기 상태(필터·정렬·숨김 열·서식)는 파일 경로로 저장된 보기 저장소(SavedViewStore)가 이미 가지고 있으므로
    /// 여기에는 넣지 않고 어떤 파일(또는 뷰)인지만 적는다.
    /// </summary>
    public sealed class WorkspaceFileTab
    {
        /// <summary>"file"(CSV·텍스트), "sheet"(엑셀·SAS·SPSS·SQLite 파일 하나), "view"(뷰 테이블).</summary>
        public string Kind { get; set; } = KindFile;
        /// <summary>file·sheet 탭이 가리키는 <see cref="WorkspaceFileModel.Sources"/>의 순번.</summary>
        public int? Source { get; set; }
        /// <summary>sheet 탭의 시트(표) 이름.</summary>
        public string? Sheet { get; set; }
        /// <summary>view 탭의 뷰 이름.</summary>
        public string? View { get; set; }

        public const string KindFile = "file";
        public const string KindSheet = "sheet";
        public const string KindView = "view";
    }

    public sealed class WorkspaceFileModel
    {
        /// <summary>파일 종류 표지.</summary>
        public string Format { get; set; } = WorkspaceFile.FormatName;
        public int Version { get; set; } = WorkspaceFile.CurrentVersion;
        public List<WorkspaceFileSource> Sources { get; set; } = new();
        public List<WorkspaceFileView> Views { get; set; } = new();
        public List<WorkspaceFileTab> Tabs { get; set; } = new();
        /// <summary>활성 탭의 <see cref="Tabs"/> 순번(없으면 -1).</summary>
        public int ActiveTab { get; set; } = -1;
        public bool ExplorerVisible { get; set; }
    }

    /// <summary>저장 직전의 원본 한 개(작업 공간·탭에서 읽은 값).</summary>
    public sealed record WorkspaceCaptureSource(
        string Kind, string Name, string Path, string? Encoding, char? Delimiter, bool HasHeader, IReadOnlyList<string>? Tables);

    /// <summary>저장 직전의 탭 한 개. 질의 결과 탭은 임시 파일이라 저장 대상이 아니므로 넘기지 않는다.</summary>
    public sealed record WorkspaceCaptureTab(string Kind, string? Path, string? Sheet, string? View);

    /// <summary>작업 공간 파일(.ncvws) 형식: 버전이 있는 JSON. 읽기·쓰기·경로 풀기·저장 직전 모델 만들기(순수 함수, UI 없음).</summary>
    public static class WorkspaceFile
    {
        public const string Extension = ".ncvws";
        public const string FormatName = "ncvws";
        /// <summary>이 앱이 쓰고 읽을 수 있는 최신 버전. 파일의 버전이 이보다 높으면 열지 않는다.</summary>
        public const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public static bool IsWorkspaceFile(string? path) =>
            !string.IsNullOrEmpty(path) && string.Equals(System.IO.Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

        // ---- 읽기·쓰기 -------------------------------------------------------------------------------------

        public static string Serialize(WorkspaceFileModel model) => JsonSerializer.Serialize(model, JsonOptions);

        /// <summary>JSON을 읽어 모델로. 형식·버전을 먼저 확인하므로 더 새로운 버전의 파일은 내용을 해석하기 전에 <see cref="WorkspaceFileError.TooNew"/>로 거절한다.</summary>
        public static WorkspaceFileModel Parse(string json)
        {
            int version;
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !TryGetIgnoreCase(root, "format", out var fmt)
                    || fmt.ValueKind != JsonValueKind.String || !string.Equals(fmt.GetString(), FormatName, StringComparison.Ordinal))
                    throw new WorkspaceFileException(WorkspaceFileError.NotAWorkspace,
                        ViewerSupport.LT("This is not a Nanum CSV Viewer workspace file.", "나눔 CSV 뷰어 작업 공간 파일이 아닙니다."));
                if (!TryGetIgnoreCase(root, "version", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out version) || version < 1)
                    throw new WorkspaceFileException(WorkspaceFileError.Corrupt,
                        ViewerSupport.LT("The workspace file has no valid version.", "작업 공간 파일에 올바른 버전이 없습니다."));
            }
            catch (JsonException ex)
            {
                throw new WorkspaceFileException(WorkspaceFileError.Corrupt,
                    ViewerSupport.LT("The workspace file is damaged: ", "작업 공간 파일이 손상되었습니다: ") + ex.Message);
            }
            if (version > CurrentVersion)
                throw new WorkspaceFileException(WorkspaceFileError.TooNew,
                    ViewerSupport.LT($"This workspace file was made by a newer version of the app (file format {version}; this app reads up to {CurrentVersion}). Update the app to open it.",
                        $"이 작업 공간 파일은 더 새로운 버전의 앱이 만들었습니다(파일 형식 {version}, 이 앱은 {CurrentVersion}까지 읽습니다). 앱을 업데이트한 뒤 여세요."),
                    version);

            WorkspaceFileModel? model;
            try { model = JsonSerializer.Deserialize<WorkspaceFileModel>(json, JsonOptions); }
            catch (JsonException ex)
            {
                throw new WorkspaceFileException(WorkspaceFileError.Corrupt,
                    ViewerSupport.LT("The workspace file is damaged: ", "작업 공간 파일이 손상되었습니다: ") + ex.Message);
            }
            if (model is null)
                throw new WorkspaceFileException(WorkspaceFileError.Corrupt, ViewerSupport.LT("The workspace file is empty.", "작업 공간 파일이 비어 있습니다."));
            Normalize(model);
            return model;
        }

        private static bool TryGetIgnoreCase(JsonElement obj, string name, out JsonElement value)
        {
            foreach (var p in obj.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
            value = default;
            return false;
        }

        /// <summary>손으로 고친 파일·부분적으로 빠진 값에 견디도록 다듬는다: null 목록 → 빈 목록, 쓸 수 없는 뷰·탭 제거, 활성 탭 순번 보정.</summary>
        private static void Normalize(WorkspaceFileModel m)
        {
            // 원본은 순번이 탭의 참조이므로 걸러 내지 않는다(경로가 비면 열 때 누락으로 보고된다). null 항목만 빈 원본으로 둔다.
            m.Sources = (m.Sources ?? new()).Select(s => s ?? new WorkspaceFileSource()).ToList();
            m.Views = (m.Views ?? new()).Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Name) && !string.IsNullOrWhiteSpace(v.Sql)).ToList();
            var tabs = new List<WorkspaceFileTab>();
            int active = -1;
            for (int i = 0; i < (m.Tabs?.Count ?? 0); i++)
            {
                var t = m.Tabs![i];
                if (t is null) continue;
                bool ok = t.Kind == WorkspaceFileTab.KindView
                    ? !string.IsNullOrWhiteSpace(t.View)
                    : t.Source is { } s && s >= 0 && s < m.Sources.Count && t.Kind is WorkspaceFileTab.KindFile or WorkspaceFileTab.KindSheet;
                if (!ok) continue;
                if (i == m.ActiveTab) active = tabs.Count;
                tabs.Add(t);
            }
            m.Tabs = tabs;
            m.ActiveTab = active;
        }

        public static WorkspaceFileModel Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

        /// <summary>임시 파일에 쓴 뒤 바꿔 끼운다(쓰다 중단돼도 이전 파일이 남는다).</summary>
        public static void Save(string path, WorkspaceFileModel model)
        {
            string full = System.IO.Path.GetFullPath(path);
            string? dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = full + ".tmp";
            try
            {
                File.WriteAllText(tmp, Serialize(model), new UTF8Encoding(false));
                File.Move(tmp, full, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { } }
            }
        }

        // ---- 경로 ----------------------------------------------------------------------------------------

        /// <summary>
        /// 작업 공간 파일 폴더 기준 상대 경로('/' 구분). 다른 드라이브·UNC 루트처럼 상대 경로를 만들 수 없으면 null.
        /// </summary>
        public static string? ToRelative(string workspaceDir, string fullPath)
        {
            try
            {
                string rel = System.IO.Path.GetRelativePath(workspaceDir, fullPath);
                if (System.IO.Path.IsPathRooted(rel)) return null;
                return rel.Replace(System.IO.Path.DirectorySeparatorChar, '/');
            }
            catch (ArgumentException) { return null; }
        }

        /// <summary>원본의 경로를 채운다(상대 + 절대).</summary>
        public static void SetPath(WorkspaceFileSource source, string workspaceDir, string fullPath)
        {
            source.AbsolutePath = fullPath;
            source.Path = ToRelative(workspaceDir, fullPath);
        }

        /// <summary>
        /// 원본 파일을 찾는다: 상대 경로(작업 공간 파일 폴더 기준)를 먼저, 다음에 절대 경로. 둘 다 없으면 null(누락).
        /// 폴더째 옮긴 경우 옛 위치에 같은 이름 파일이 남아 있어도 새 위치의 것을 쓴다.
        /// </summary>
        public static string? Resolve(WorkspaceFileSource source, string workspaceDir, Func<string, bool>? exists = null)
        {
            exists ??= File.Exists;
            foreach (string candidate in Candidates(source, workspaceDir))
                if (exists(candidate)) return candidate;
            return null;
        }

        /// <summary>찾는 순서대로의 후보 경로(전체 경로). 누락 대화 상자가 "어디를 찾았는지" 보여 주는 데에도 쓴다.</summary>
        public static IReadOnlyList<string> Candidates(WorkspaceFileSource source, string workspaceDir)
        {
            var list = new List<string>();
            try
            {
                if (!string.IsNullOrWhiteSpace(source.Path))
                    list.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(workspaceDir, source.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            try
            {
                if (!string.IsNullOrWhiteSpace(source.AbsolutePath))
                {
                    string abs = System.IO.Path.GetFullPath(source.AbsolutePath);
                    if (!list.Contains(abs, StringComparer.OrdinalIgnoreCase)) list.Add(abs);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            return list;
        }

        // ---- 저장 직전 모델 만들기 -------------------------------------------------------------------------------

        /// <summary>
        /// 현재 상태에서 모델을 만든다. <paramref name="sources"/>는 작업 공간의 원본(추가한 순서), 탭이 가리키는 파일이 거기 없으면(엔진을 쓸 수 없을 때 등)
        /// 탭의 파일을 원본으로 덧붙인다. <paramref name="carriedViews"/>는 지난번에 복원하지 못해 그대로 보관 중인 뷰 정의(같은 이름이 이미 있으면 버린다).
        /// </summary>
        public static WorkspaceFileModel Capture(
            string workspacePath,
            IEnumerable<WorkspaceCaptureSource> sources,
            IEnumerable<WorkspaceFileView> views,
            IEnumerable<WorkspaceFileView> carriedViews,
            IEnumerable<WorkspaceCaptureTab> tabs,
            int activeTab,
            bool explorerVisible)
        {
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(workspacePath)) ?? "";
            var model = new WorkspaceFileModel { ExplorerVisible = explorerVisible };
            var indexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int AddSource(WorkspaceCaptureSource s)
            {
                var item = new WorkspaceFileSource
                {
                    Kind = s.Kind,
                    Name = s.Name,
                    Encoding = s.Encoding,
                    Delimiter = s.Delimiter?.ToString(),
                    HasHeader = s.HasHeader,
                    Tables = s.Tables?.ToList(),
                };
                string full = SafeFull(s.Path);
                SetPath(item, dir, full);
                model.Sources.Add(item);
                if (full.Length > 0 && !indexByPath.ContainsKey(full)) indexByPath[full] = model.Sources.Count - 1;
                return model.Sources.Count - 1;
            }

            foreach (var s in sources) AddSource(s);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in views)
                if (names.Add(v.Name)) model.Views.Add(new WorkspaceFileView { Name = v.Name, Sql = v.Sql, IncludeUnsavedEdits = v.IncludeUnsavedEdits });
            foreach (var v in carriedViews)
                if (names.Add(v.Name)) model.Views.Add(new WorkspaceFileView { Name = v.Name, Sql = v.Sql, IncludeUnsavedEdits = v.IncludeUnsavedEdits });

            int index = 0;
            foreach (var t in tabs)
            {
                WorkspaceFileTab? item = null;
                if (t.Kind == WorkspaceFileTab.KindView)
                {
                    if (!string.IsNullOrEmpty(t.View)) item = new WorkspaceFileTab { Kind = t.Kind, View = t.View };
                }
                else if (!string.IsNullOrEmpty(t.Path))
                {
                    string full = SafeFull(t.Path);
                    if (!indexByPath.TryGetValue(full, out int si))
                    {
                        bool sheet = t.Kind == WorkspaceFileTab.KindSheet;
                        si = AddSource(new WorkspaceCaptureSource(sheet ? WorkspaceFileSource.KindDatabase : WorkspaceFileSource.KindCsv,
                            System.IO.Path.GetFileNameWithoutExtension(full), full, null, null, true, null));
                    }
                    item = new WorkspaceFileTab { Kind = t.Kind, Source = si, Sheet = t.Sheet };
                }
                if (item is null) { index++; continue; }
                if (index == activeTab) model.ActiveTab = model.Tabs.Count;
                model.Tabs.Add(item);
                index++;
            }
            return model;
        }

        private static string SafeFull(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try { return System.IO.Path.GetFullPath(path); } catch (ArgumentException) { return path; }
        }

        /// <summary>
        /// 저장되지 않은 변경이 있는지 비교하는 표식. 작업 공간의 내용(원본의 이름·경로·옵션, 뷰 정의)만 본다. 원본·뷰는 순서와 무관하게 비교하고
        /// (복원은 CSV를 먼저, 워크북을 탭을 열 때 등록하므로 순서가 달라질 수 있다), 열린 탭·활성 탭·시트·탐색기 표시 여부·DB 표 목록은 뺀다 — 탐색일 뿐
        /// 작업 공간 내용의 변경이 아니다(저장하면 함께 저장된다).
        /// </summary>
        public static string Signature(WorkspaceFileModel model)
        {
            static string Norm(string? p) => string.IsNullOrEmpty(p) ? "" : p.ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (var s in model.Sources.OrderBy(s => Norm(s.AbsolutePath), StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal))
                sb.Append("S|").Append(s.Kind).Append('|').Append(s.Name).Append('|').Append(Norm(s.AbsolutePath)).Append('|')
                  .Append(s.Encoding).Append('|').Append(s.Delimiter).Append('|').Append(s.HasHeader).Append('\n');
            foreach (var v in model.Views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                sb.Append("V|").Append(v.Name).Append('|').Append(v.IncludeUnsavedEdits).Append('|').Append(v.Sql.Replace("\r\n", "\n")).Append('\n');
            return sb.ToString();
        }
    }
}
