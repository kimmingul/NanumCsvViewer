using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NanumCsvViewer.Agent.Python
{
    /// <summary>고정된 요구사항 1줄: <c>name==version[; marker]</c>. Marker는 <c>python_version &lt; "3.13"</c> 꼴만 직접 평가한다(그 밖의 마커는 pip가 판단하므로 항상 적용으로 본다).</summary>
    internal sealed record RequirementPin(string Name, string Version, string? Marker)
    {
        private static readonly Regex MarkerRegex = new(@"^python_version\s*(<=|>=|==|!=|<|>)\s*[""'](\d+)\.(\d+)[""']$", RegexOptions.Compiled);

        /// <summary>pip에 넘기는 요구 문자열(마커 포함).</summary>
        public string Spec => Marker == null ? $"{Name}=={Version}" : $"{Name}=={Version}; {Marker}";

        /// <summary>해당 Python 버전에 설치 대상인가. 알 수 없는 마커는 true(pip가 결정).</summary>
        public bool AppliesTo(Version python)
        {
            if (Marker == null) return true;
            var m = MarkerRegex.Match(Marker.Trim());
            if (!m.Success) return true;
            int cmp = python.Major != int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                ? python.Major.CompareTo(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                : python.Minor.CompareTo(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            return m.Groups[1].Value switch
            {
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                "==" => cmp == 0,
                _ => cmp != 0,
            };
        }

        public string Key => AnalysisGroups.NormalizeName(Name);
    }

    /// <summary>요구사항 묶음(core·stats·clinical·ml). 파일 형식: 머리의 <c># title-en:</c> 등 주석 + 한 줄에 하나의 정확한 고정(==). ApproxMb는 core 위에 더해지는 디스크 크기의 대략값(Windows x64 Python 3.10에서 잰 값).</summary>
    internal sealed record AnalysisGroup(
        string Name, string TitleEn, string TitleKo, string DescriptionEn, string DescriptionKo, IReadOnlyList<RequirementPin> Pins, int ApproxMb = 0)
    {
        public string Title(bool korean) => korean ? TitleKo : TitleEn;
        public string Description(bool korean) => korean ? DescriptionKo : DescriptionEn;

        public IEnumerable<RequirementPin> PinsFor(Version python) => Pins.Where(p => p.AppliesTo(python));

        /// <summary>이 Python에 적용되는 고정 목록의 지문(핀을 바꾸면 달라진다). "업데이트 필요" 판정에 쓴다.</summary>
        public string Fingerprint(Version python)
        {
            string text = string.Join("\n", PinsFor(python).Select(p => p.Spec).OrderBy(s => s, StringComparer.Ordinal));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        }
    }

    /// <summary>임베디드 requirements/*.txt를 읽어 만든 묶음 목록.</summary>
    internal static class AnalysisGroups
    {
        public const string Core = "core", Stats = "stats", Clinical = "clinical", Ml = "ml";

        /// <summary>표시·설치 순서. core가 나머지의 바탕이다(모든 묶음 설치에 core가 포함된다).</summary>
        public static readonly IReadOnlyList<string> Names = new[] { Core, Stats, Clinical, Ml };

        private static readonly Regex PinRegex = new(
            @"^(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)==(?<ver>[A-Za-z0-9][A-Za-z0-9.!+_-]*)\s*(?:;\s*(?<marker>.+))?$", RegexOptions.Compiled);

        private static IReadOnlyList<AnalysisGroup>? s_all;

        public static IReadOnlyList<AnalysisGroup> All => s_all ??= Names.Select(n => Parse(n, ReadResource(n))).ToArray();

        public static AnalysisGroup? Get(string name) =>
            All.FirstOrDefault(g => string.Equals(g.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>패키지 이름(정규화 비교) → 그 패키지를 고정하는 묶음. 없으면 null.</summary>
        public static AnalysisGroup? ForPackage(string package)
        {
            string key = NormalizeName(package);
            return All.FirstOrDefault(g => g.Pins.Any(p => p.Key == key));
        }

        /// <summary>PEP 503 정규화: 소문자, 연속된 - _ . 를 하나의 -로.</summary>
        public static string NormalizeName(string name) => Regex.Replace(name.Trim(), @"[-_.]+", "-").ToLowerInvariant();

        public static string ReadResource(string group)
        {
            var asm = typeof(AnalysisGroups).Assembly;
            using var s = asm.GetManifestResourceStream($"requirements/{group}.txt")
                ?? throw new InvalidOperationException($"Embedded requirements/{group}.txt is missing.");
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        /// <summary>
        /// 파일 본문 → 묶음. 빈 줄·<c>#</c> 주석은 무시(<c># title-en:</c>·<c># desc-ko:</c> 등 머리 필드는 읽는다).
        /// 고정(==)이 아닌 줄(범위·URL·옵션)은 재현성을 해치므로 <see cref="FormatException"/>.
        /// </summary>
        public static AnalysisGroup Parse(string name, string text)
        {
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pins = new List<RequirementPin>();
            var seen = new HashSet<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    var m = Regex.Match(line, @"^#\s*(title-en|title-ko|desc-en|desc-ko|size-mb)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                    if (m.Success) meta[m.Groups[1].Value] = m.Groups[2].Value.Trim();
                    continue;
                }
                var p = PinRegex.Match(line);
                if (!p.Success) throw new FormatException($"requirements/{name}.txt: not an exact pin (name==version): '{line}'");
                var pin = new RequirementPin(p.Groups["name"].Value, p.Groups["ver"].Value, p.Groups["marker"].Success ? p.Groups["marker"].Value.Trim() : null);
                if (!seen.Add(pin.Key + "|" + pin.Marker)) throw new FormatException($"requirements/{name}.txt: duplicate '{pin.Name}'");
                pins.Add(pin);
            }
            if (pins.Count == 0) throw new FormatException($"requirements/{name}.txt has no packages");
            string Get(string key) => meta.TryGetValue(key, out var v) ? v : name;
            int.TryParse(meta.GetValueOrDefault("size-mb"), out int mb);
            return new AnalysisGroup(name, Get("title-en"), Get("title-ko"), Get("desc-en"), Get("desc-ko"), pins, mb);
        }

        /// <summary>요청한 묶음 이름들 → 정규화·중복 제거·표시 순서·core 포함. 모르는 이름이 있으면 unknown에 담는다.</summary>
        public static IReadOnlyList<AnalysisGroup> Resolve(IEnumerable<string> requested, out IReadOnlyList<string> unknown)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Core };
            var bad = new List<string>();
            foreach (string r in requested)
            {
                if (string.IsNullOrWhiteSpace(r)) continue;
                var g = Get(r);
                if (g == null) bad.Add(r.Trim()); else wanted.Add(g.Name);
            }
            unknown = bad;
            return All.Where(g => wanted.Contains(g.Name)).ToArray();
        }
    }

    /// <summary>venv 안의 <c>requirements.lock</c>(pip freeze 결과) 읽기·쓰기.</summary>
    internal static class AnalysisLock
    {
        public const string FileName = "requirements.lock";

        /// <summary><c>name==version</c> 줄만 읽는다(정규화한 이름 → 버전). 주석·편집 가능 설치(-e)·로컬 경로(@)는 무시.</summary>
        public static IReadOnlyDictionary<string, string> Parse(string? text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return map;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == '-') continue;
                int eq = line.IndexOf("==", StringComparison.Ordinal);
                if (eq <= 0 || line.Contains(" @ ", StringComparison.Ordinal)) continue;
                string name = AnalysisGroups.NormalizeName(line[..eq]);
                string ver = line[(eq + 2)..].Split(';', ' ', '#')[0].Trim();
                if (name.Length > 0 && ver.Length > 0) map[name] = ver;
            }
            return map;
        }

        /// <summary>pip freeze 출력 → 정렬된 lock 본문(머리 주석 포함). pip 출력의 경고·빈 줄은 버린다.</summary>
        public static string Format(string freezeOutput, Version python, DateTime utcNow)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Nanum CSV Viewer analysis environment (pip freeze).");
            sb.AppendLine($"# Python {python.Major}.{python.Minor}.{python.Build}; created {utcNow:yyyy-MM-dd HH:mm}Z");
            sb.AppendLine("# Recreate: python -m venv .venv && .venv\\Scripts\\python -m pip install --only-binary=:all: -r requirements.lock");
            var lines = freezeOutput.Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("[notice]", StringComparison.OrdinalIgnoreCase) && l.Contains("==", StringComparison.Ordinal))
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase);
            foreach (string l in lines) sb.AppendLine(l);
            return sb.ToString();
        }
    }
}
