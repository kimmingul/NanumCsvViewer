using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NanumCsvViewer.Agent
{
    /// <summary>분류 ID(manifest.json의 categories[].id). app은 항상 함께 실리는 앱 전용 규칙이다.</summary>
    internal static class SkillCategories
    {
        public const string App = "app";
        public const string Clinical = "clinical";
        public const string Stats = "stats";
        public const string Ml = "ml";
    }

    /// <summary>manifest.json의 스킬 한 줄. <paramref name="Tokens"/>는 omp 시스템 프롬프트에 실리는 이름+설명 줄의 토큰 추정치(본문은 필요할 때만 읽는다).</summary>
    internal sealed record SkillInfo(string Name, string Category, string SummaryEn, string SummaryKo, IReadOnlyList<string> PythonDeps, int Tokens,
        string Reason, string? Caveat)
    {
        public bool IsApp => Category == SkillCategories.App;
        public string Summary(bool korean) => korean ? SummaryKo : SummaryEn;
    }

    internal sealed record SkillCategoryInfo(string Id, string En, string Ko)
    {
        public string Label(bool korean) => korean ? Ko : En;
    }

    internal sealed record SkillRejection(string Name, string Reason);

    /// <summary>manifest.json: 고정한 업스트림(태그·커밋·라이선스), 분류, 포함한 스킬(이유·Python 의존성·토큰), 검토 후 뺀 스킬.</summary>
    internal sealed record SkillManifest(string UpstreamName, string UpstreamTag, string UpstreamCommit, string UpstreamLicense, string LicenseFile,
        IReadOnlyList<SkillCategoryInfo> Categories, IReadOnlyList<SkillInfo> Skills, IReadOnlyList<SkillRejection> Rejected)
    {
        public SkillInfo? Find(string name) => Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        public int Count(string category) => Skills.Count(s => s.Category == category);
    }

    /// <summary>
    /// 사용자가 고른 스킬 구성(설정 화면). 전체 켜기/끄기, 분류별 끄기, 스킬별 끄기. 이름은 소문자·정렬·중복 제거된 쉼표 목록으로 보관해
    /// 값이 같으면 같은 구성으로 비교된다. 앱 전용 스킬(nanum-python-analysis)은 전체가 켜져 있으면 항상 실린다.
    /// </summary>
    internal sealed record SkillSelection(bool Enabled = true, string CategoriesOff = "", string SkillsOff = "")
    {
        public static readonly SkillSelection Default = new();

        public static string Normalize(string? list) =>
            string.Join(",", (list ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal));

        public static SkillSelection Create(bool enabled, IEnumerable<string>? categoriesOff, IEnumerable<string>? skillsOff) =>
            new(enabled, Normalize(string.Join(",", categoriesOff ?? Array.Empty<string>())), Normalize(string.Join(",", skillsOff ?? Array.Empty<string>())));

        public static SkillSelection FromSettings(AppSettings s) => new(s.AgentSkillsEnabled, Normalize(s.AgentSkillCategoriesOff), Normalize(s.AgentSkillsOff));

        private static HashSet<string> Set(string list) => new(list.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        public bool CategoryOff(string category) => Set(CategoriesOff).Contains(category.ToLowerInvariant());
        public bool SkillOff(string name) => Set(SkillsOff).Contains(name.ToLowerInvariant());

        /// <summary>이 구성에서 스킬이 실리는가.</summary>
        public bool Includes(SkillInfo skill)
        {
            if (!Enabled) return false;
            if (skill.IsApp) return true;
            return !CategoryOff(skill.Category) && !SkillOff(skill.Name);
        }

        /// <summary>
        /// 설정 화면의 체크 상태에서 구성을 만든다. 끈 스킬은 이름으로, 분류의 스킬이 모두 꺼졌으면 그 분류도 끈 것으로 적는다
        /// (두 표현이 서로 어긋나지 않게). 앱 전용 스킬은 항상 켜져 있어 적지 않는다.
        /// </summary>
        public static SkillSelection FromChecks(bool enabled, SkillManifest manifest, Func<SkillInfo, bool> isOn)
        {
            var optional = manifest.Skills.Where(s => !s.IsApp).ToList();
            var skillsOff = optional.Where(s => !isOn(s)).Select(s => s.Name).ToList();
            var categoriesOff = optional.GroupBy(s => s.Category).Where(g => g.All(s => !isOn(s))).Select(g => g.Key).ToList();
            return Create(enabled, categoriesOff, skillsOff);
        }
    }

    /// <summary>풀어 둔 스킬 폴더와 실린 스킬. <paramref name="Key"/>는 실린 스킬 이름 목록의 해시(다시 시작 필요 여부 비교용).</summary>
    internal sealed record SkillLaunch(string Directory, IReadOnlyList<string> Names, int Tokens, string Key);

    /// <summary>
    /// exe에 내장한 분석 스킬 묶음(Skills/**)을 %LOCALAPPDATA%\NanumCsvViewer\skills\&lt;내용 해시&gt;\&lt;선택 해시&gt;\ 로 풀고
    /// omp 덧씌우기 설정(--config)의 skills.customDirectories에 건다. omp의 includeSkills/ignoredSkills는 사용자의 다른 스킬·무시 목록까지
    /// 바꿔 버리므로(실제 omp 18.4.4로 확인: 덧씌우기의 배열은 사용자 값을 대체한다) 쓰지 않고, 켜진 스킬만 풀어 폴더 자체를 고른다.
    /// </summary>
    internal static class SkillPack
    {
        public const string ResourcePrefix = "Skills/";
        public const string ManifestFile = "manifest.json";
        public const string AppSkill = "nanum-python-analysis";
        /// <summary>선택 폴더(해시 폴더 안)를 최근 것부터 이만큼만 남긴다(앱 인스턴스가 여럿이어도 쓰는 폴더는 지우지 않는다).</summary>
        public const int KeepSelections = 4;

        private static readonly Assembly s_assembly = typeof(SkillPack).Assembly;
        private static readonly Lazy<SkillManifest> s_manifest = new(() => ParseManifest(ReadText(s_assembly, ResourcePrefix + ManifestFile)));
        private static string? s_hash;

        public static SkillManifest Manifest => s_manifest.Value;

        // ---- manifest ----------------------------------------------------------------------------------------

        internal static SkillManifest ParseManifest(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var up = root.GetProperty("upstream");
            string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var categories = root.GetProperty("categories").EnumerateArray()
                .Select(c => new SkillCategoryInfo(S(c, "id"), S(c, "en"), S(c, "ko"))).ToList();
            var skills = new List<SkillInfo>();
            foreach (var s in root.GetProperty("skills").EnumerateArray())
            {
                var sum = s.GetProperty("summary");
                var deps = s.TryGetProperty("pythonDeps", out var d) ? d.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                int tokens = s.TryGetProperty("tokens", out var t) && t.TryGetInt32(out int n) ? n : 0;
                string? caveat = s.TryGetProperty("caveat", out var cv) ? cv.GetString() : null;
                skills.Add(new SkillInfo(S(s, "name"), S(s, "category"), S(sum, "en"), S(sum, "ko"), deps, tokens, S(s, "reason"), caveat));
            }
            var rejected = root.TryGetProperty("rejected", out var r)
                ? r.EnumerateArray().Select(x => new SkillRejection(S(x, "name"), S(x, "reason"))).ToList()
                : new List<SkillRejection>();
            return new SkillManifest(S(up, "name"), S(up, "tag"), S(up, "commit"), S(up, "license"), S(up, "licenseFile"), categories, skills, rejected);
        }

        /// <summary>구성에서 실리는 스킬(manifest 순서).</summary>
        public static IReadOnlyList<SkillInfo> Resolve(SkillSelection selection) =>
            Manifest.Skills.Where(selection.Includes).ToList();

        public static int EstimateTokens(SkillSelection selection) => Resolve(selection).Sum(s => s.Tokens);

        /// <summary>실린 스킬 이름 목록의 짧은 해시. 실린 스킬이 없으면 빈 문자열.</summary>
        public static string KeyFor(IReadOnlyList<SkillInfo> skills)
        {
            if (skills.Count == 0) return "";
            string joined = string.Join("\n", skills.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..10].ToLowerInvariant();
        }

        public static string KeyFor(SkillSelection selection) => KeyFor(Resolve(selection));

        // ---- 내장 자원 ----------------------------------------------------------------------------------------

        internal static IReadOnlyList<string> ResourceNames() =>
            s_assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        private static byte[] ReadBytes(Assembly assembly, string name)
        {
            using var s = assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("embedded resource not found: " + name);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private static string ReadText(Assembly assembly, string name)
        {
            var bytes = ReadBytes(assembly, name);
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }

        /// <summary>내장 묶음 전체(이름+내용)의 SHA-256 앞 12자리. 묶음이 바뀌면 폴더가 달라져 오래된 파일을 쓰지 않는다.</summary>
        public static string ContentHash()
        {
            if (s_hash != null) return s_hash;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string name in ResourceNames())
            {
                sha.AppendData(Encoding.UTF8.GetBytes(name));
                sha.AppendData(new byte[] { 0 });
                sha.AppendData(ReadBytes(s_assembly, name));
                sha.AppendData(new byte[] { 0 });
            }
            return s_hash = Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
        }

        /// <summary>%LOCALAPPDATA%\NanumCsvViewer\skills</summary>
        public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NanumCsvViewer", "skills");

        // ---- 풀기 ---------------------------------------------------------------------------------------------

        /// <summary>
        /// 구성에 맞는 스킬을 <paramref name="root"/>\&lt;해시&gt;\&lt;선택&gt;\ 로 풀고(이미 있으면 다시 풀지 않음) 오래된 폴더를 정리한다.
        /// 실리는 스킬이 없으면 null. 임시 폴더에 풀어 통째로 옮겨(원자적) 도중에 끊기거나 두 인스턴스가 동시에 시작해도 반쯤 풀린 폴더를 쓰지 않는다.
        /// </summary>
        public static SkillLaunch? Prepare(SkillSelection selection, string? root = null)
        {
            var skills = Resolve(selection);
            if (skills.Count == 0) return null;
            root ??= DefaultRoot;
            string hash = ContentHash();
            string key = KeyFor(skills);
            string versionDir = Path.Combine(root, hash);
            string dir = Path.Combine(versionDir, "sel-" + key);

            if (!File.Exists(Path.Combine(dir, ManifestFile)))
            {
                Directory.CreateDirectory(versionDir);
                string tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    var wanted = skills.Select(s => ResourcePrefix + s.Name + "/").ToArray();
                    foreach (string name in ResourceNames())
                    {
                        string rel = name[ResourcePrefix.Length..];
                        bool shared = !rel.Contains('/');   // manifest.json, 라이선스, THIRD_PARTY_NOTICES.md: 모든 선택 폴더에 함께 둔다(MIT 고지 유지)
                        if (!shared && !wanted.Any(w => name.StartsWith(w, StringComparison.Ordinal))) continue;
                        string target = Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, ReadBytes(s_assembly, name));
                    }
                    try { Directory.Move(tmp, dir); }
                    catch (IOException) when (File.Exists(Path.Combine(dir, ManifestFile))) { /* 다른 인스턴스가 먼저 풀었다. */ }
                }
                finally
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* 임시 폴더 정리는 최선 */ }
                }
            }
            else
            {
                try { Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); } catch { /* 최근 사용 표시는 최선 */ }
            }
            Cleanup(root, hash, dir);
            return new SkillLaunch(dir, skills.Select(s => s.Name).ToList(), skills.Sum(s => s.Tokens), key);
        }

        /// <summary>현재 해시가 아닌 폴더(옛 버전, 하루 지난 임시 폴더)를 지우고, 현재 해시 안에서는 최근 <see cref="KeepSelections"/>개 선택 폴더만 남긴다. 실패는 무시.</summary>
        public static void Cleanup(string root, string currentHash, string inUse)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (string d in Directory.EnumerateDirectories(root))
                {
                    if (string.Equals(Path.GetFileName(d), currentHash, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(d, true); } catch { /* 다른 인스턴스(옛 버전)가 쓰는 중일 수 있다 */ }
                }
                string versionDir = Path.Combine(root, currentHash);
                if (!Directory.Exists(versionDir)) return;
                var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
                var sels = new List<DirectoryInfo>();
                foreach (var di in new DirectoryInfo(versionDir).EnumerateDirectories())
                {
                    if (di.Name.Contains(".tmp-", StringComparison.Ordinal))
                    {
                        if (di.LastWriteTimeUtc < cutoff) try { di.Delete(true); } catch { }
                    }
                    else sels.Add(di);
                }
                foreach (var di in sels.Where(x => !string.Equals(x.FullName, inUse, StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(x => x.LastWriteTimeUtc).Skip(KeepSelections - 1))
                {
                    try { di.Delete(true); } catch { }
                }
            }
            catch { /* 정리 실패는 무시 */ }
        }

        // ---- omp 덧씌우기 ---------------------------------------------------------------------------------------

        /// <summary>
        /// omp --config로 읽는 덧씌우기(JSON은 YAML의 부분집합이라 host.yml과 같은 방식). 사용자가 omp에 이미 지정한 customDirectories는
        /// 배열이 통째로 대체되므로 <paramref name="userDirectories"/>로 받아 앞에 그대로 둔다(같은 이름이면 사용자의 스킬이 앞선다).
        /// </summary>
        public static string OverlayJson(string skillsDirectory, IEnumerable<string>? userDirectories = null)
        {
            var dirs = new List<string>();
            foreach (string d in userDirectories ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(d) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d);
            if (!dirs.Contains(skillsDirectory, StringComparer.OrdinalIgnoreCase)) dirs.Add(skillsDirectory);
            return JsonSerializer.Serialize(new { skills = new { customDirectories = dirs } });
        }

        /// <summary>`omp config get skills.customDirectories --json`의 출력에서 사용자 디렉터리 목록을 읽는다. 읽을 수 없으면 빈 목록.</summary>
        public static IReadOnlyList<string> ParseUserDirectories(string? cliOutput)
        {
            if (string.IsNullOrWhiteSpace(cliOutput)) return Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(cliOutput);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Array)
                    return Array.Empty<string>();
                return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList();
            }
            catch (JsonException) { return Array.Empty<string>(); }
        }
    }
}
