using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NanumCsvViewer.Agent.Rpc
{
    internal enum OmpProblemKind
    {
        None,
        /// <summary>어디에서도 찾지 못했다.</summary>
        NotFound,
        /// <summary>설정에 적은 경로에 파일이 없고 다른 곳에서도 찾지 못했다.</summary>
        ConfiguredPathMissing,
        /// <summary>PATH에서 omp.ps1(PowerShell 래퍼)만 찾았다 — 표준 입출력 RPC에는 쓸 수 없다.</summary>
        UnsupportedWrapper,
        /// <summary>찾았지만 실행할 수 없다(차단·아키텍처 불일치·접근 거부·곧바로 종료).</summary>
        NotRunnable,
        TooOld,
        /// <summary>실행은 되지만 `omp/&lt;버전&gt;`을 읽을 수 없다(시간 초과·낯선 출력).</summary>
        UnknownVersion,
    }

    internal enum OmpLocationStatus { Found, Missing, Skipped, Unsupported }

    /// <summary>확인한 위치 한 곳(오류 안내와 진단에 그대로 나열한다).</summary>
    internal sealed record OmpLocation(string Key, string? Path, OmpLocationStatus Status)
    {
        /// <summary>현재 UI 언어의 이름.</summary>
        public string Description => Label(Loc.CurrentLanguage == "ko");

        public string Label(bool korean) => OmpDiscovery.LocationLabel(Key, korean);
    }

    internal enum OmpCandidateKind { Downloads, Path, RunningProcess }

    /// <summary>자동으로 쓰지 않는 후보(릴리스 원본 이름 omp-windows-x64.exe, 실행 중인 omp). 사용자가 '설치'를 고르면 복사해서 쓴다.</summary>
    internal sealed record OmpCandidate(string Path, string Reason, OmpCandidateKind Kind = OmpCandidateKind.Downloads)
    {
        public string ReasonText(bool korean) => Kind switch
        {
            OmpCandidateKind.Downloads => korean ? "다운로드 폴더" : "Downloads folder",
            OmpCandidateKind.Path => korean ? "PATH의 릴리스 파일" : "release file on PATH",
            _ => korean ? "실행 중인 omp" : "running omp",
        };
    }

    internal sealed record OmpDiscoveryResult(
        IReadOnlyList<OmpLocation> Checked,
        string? ChosenExe,
        IReadOnlyList<OmpCandidate> Candidates,
        OmpVersion? Version,
        OmpProblemKind Problem,
        string RefreshedPath)
    {
        /// <summary>문제의 세부(설정 경로, 래퍼 경로, 실행 오류 문구, 버전 출력 등).</summary>
        public string? ProblemDetail { get; init; }

        /// <summary>설정에 적었지만 파일이 없는 경로(다른 곳에서 찾아 계속 쓸 수 있어도 알린다).</summary>
        public string? ConfiguredMissing { get; init; }

        /// <summary>검증에 성공했을 때 설정에 캐시할 값(<see cref="OmpDiscovery.MakeCacheEntry"/>). 래퍼(.cmd)는 캐시하지 않는다.</summary>
        public string? VerifiedCache { get; init; }

        public bool IsOk => Problem == OmpProblemKind.None && ChosenExe != null;

        /// <summary>현재 UI 언어의 한 문단 안내.</summary>
        public string Message => Describe(Loc.CurrentLanguage == "ko");

        public string Describe(bool korean) => OmpDiscovery.Describe(this, korean);

        /// <summary>지원 요청에 붙여 넣을 수 있는 영어 평문(경로·상태·버전).</summary>
        public string ToDiagnosticText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("omp discovery");
            sb.AppendLine($"  result: {(IsOk ? "ok" : Problem.ToString())}");
            if (ChosenExe != null) sb.AppendLine($"  executable: {ChosenExe}");
            if (Version != null) sb.AppendLine($"  version: {Version} (minimum {OmpVersion.Minimum})");
            if (!string.IsNullOrEmpty(ProblemDetail)) sb.AppendLine($"  detail: {ProblemDetail}");
            if (!string.IsNullOrEmpty(ConfiguredMissing)) sb.AppendLine($"  configured path missing: {ConfiguredMissing}");
            sb.AppendLine("  checked:");
            foreach (var l in Checked) sb.AppendLine($"    - {l.Label(false)}: {l.Status}{(l.Path != null ? " " + l.Path : "")}");
            if (Candidates.Count > 0)
            {
                sb.AppendLine("  candidates:");
                foreach (var c in Candidates) sb.AppendLine($"    - {c.Kind}: {c.Path}");
            }
            sb.AppendLine($"  PATH entries: {RefreshedPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Length}");
            return sb.ToString();
        }
    }

    /// <summary>탐색·검증이 읽는 환경(테스트는 가짜로 교체). 기본값이 실제 환경이다.</summary>
    internal sealed class OmpEnvironment
    {
        internal const string MachineEnvKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        internal const string UserEnvKey = "Environment";

        public Func<string, string?> GetEnv { get; init; } = Environment.GetEnvironmentVariable;
        public Func<string, bool> FileExists { get; init; } = File.Exists;
        /// <summary>레지스트리의 Machine/User Path 원문(확장 전). 읽을 수 없으면 null.</summary>
        public Func<string?> ReadMachinePath { get; init; } = () => OmpPathEnvironment.ReadRegistryPath(RegistryHive.LocalMachine, MachineEnvKey);
        public Func<string?> ReadUserPath { get; init; } = () => OmpPathEnvironment.ReadRegistryPath(RegistryHive.CurrentUser, UserEnvKey);
        /// <summary>%VAR% 확장(REG_EXPAND_SZ 대응).</summary>
        public Func<string, string> Expand { get; init; } = Environment.ExpandEnvironmentVariables;
        /// <summary>실행 중인 omp 프로세스의 실행 파일 경로.</summary>
        public Func<IReadOnlyList<string>> RunningOmpPaths { get; init; } = OmpPathEnvironment.RunningOmpPaths;
        /// <summary>파일 크기와 수정 시각(UTC ticks). 없으면 null.</summary>
        public Func<string, (long Length, long Ticks)?> Stamp { get; init; } = OmpPathEnvironment.StampOf;
        public Func<string, CancellationToken, Task<OmpRunOutcome>> RunVersion { get; init; } =
            (p, ct) => OmpLocator.RunVersionOutcomeAsync(p, TimeSpan.FromSeconds(10), ct);
        /// <summary>"x64" | "arm64" | 그 밖. 후보 파일 이름의 우선순위에만 쓴다.</summary>
        public string Architecture { get; init; } = RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        public string? LocalAppData => Normalize(GetEnv("LOCALAPPDATA"));
        public string? UserProfile => Normalize(GetEnv("USERPROFILE"));

        private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        /// <summary>%LOCALAPPDATA%\omp\omp.exe (공식 설치 스크립트와 같은 위치).</summary>
        public string? DefaultInstallPath => LocalAppData is { } l ? Path.Combine(l, "omp", OmpLocator.ExeName) : null;
    }

    /// <summary>PATH를 레지스트리에서 다시 읽어 프로세스 PATH와 합친다(앱이 뜬 뒤에 바뀐 PATH 반영).</summary>
    internal static class OmpPathEnvironment
    {
        internal static string? ReadRegistryPath(RegistryHive hive, string subKey)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(subKey);
                return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// 프로세스 PATH 항목을 그대로(순서 유지) 앞에 두고, 레지스트리 Machine → User의 새 항목만 뒤에 덧붙인다. 대소문자·끝 '\'는 같은 항목으로 본다.
        /// </summary>
        public static string Build(OmpEnvironment env)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = new List<string>();
            void Add(string? list, bool expand)
            {
                if (string.IsNullOrWhiteSpace(list)) return;
                foreach (string raw in list.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = raw.Trim();
                    if (expand) p = env.Expand(p).Trim();
                    if (p.Length == 0) continue;
                    if (seen.Add(p.TrimEnd('\\', '/'))) parts.Add(p);
                }
            }
            Add(env.GetEnv("PATH"), expand: false);
            Add(env.ReadMachinePath(), expand: true);
            Add(env.ReadUserPath(), expand: true);
            return string.Join(';', parts);
        }

        /// <summary>자식 프로세스 환경의 PATH를 새로 읽은 값으로 바꾼다(omp가 실행하는 도구가 최신 PATH를 보게). 실패하면 그대로 둔다.</summary>
        public static void Apply(ProcessStartInfo psi)
        {
            try
            {
                string path = Build(new OmpEnvironment());
                if (path.Length > 0) psi.Environment["PATH"] = path;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* 프로세스 PATH를 그대로 쓴다 */ }
        }

        internal static IReadOnlyList<string> RunningOmpPaths()
        {
            var result = new List<string>();
            foreach (string name in new[] { "omp", "omp-windows-x64", "omp-windows-arm64" })
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                foreach (var p in procs)
                {
                    try
                    {
                        string? file = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file)) result.Add(file);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* 다른 사용자/권한 */ }
                    finally { p.Dispose(); }
                }
            }
            return result;
        }

        internal static (long Length, long Ticks)? StampOf(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        }
    }

    /// <summary>
    /// omp 실행 파일 찾기와 검증. 순서: 설정 경로 → PATH(omp.exe, 그다음 omp.cmd/omp.bat, omp.ps1은 미지원 표시) → %LOCALAPPDATA%\omp\omp.exe.
    /// 릴리스 원본 이름(omp-windows-x64.exe)과 실행 중인 omp는 후보로만 알린다(자동으로 쓰지 않는다). 모델 호출 없음.
    /// </summary>
    internal static class OmpDiscovery
    {
        private static readonly string[] s_defaultPathExt = { ".COM", ".EXE", ".BAT", ".CMD" };

        /// <summary>확인한 위치의 이름(키 → 언어별 문구). 모르는 키는 그대로.</summary>
        internal static string LocationLabel(string key, bool ko) => key switch
        {
            "configured" => ko ? "설정의 omp 경로" : "Configured omp path",
            "path-exe" => ko ? "PATH의 omp.exe" : "omp.exe on PATH",
            "path-cmd" => ko ? "PATH의 omp.cmd / omp.bat" : "omp.cmd / omp.bat on PATH",
            "path-ps1" => ko ? "PATH의 omp.ps1" : "omp.ps1 on PATH",
            "installed" => @"%LOCALAPPDATA%\omp\omp.exe",
            _ => key,
        };

        /// <summary>프로세스를 실행하지 않는 탐색(위치 확인만). 찾으면 Problem은 None이다(버전은 <see cref="DiscoverAsync(string?, string?, OmpEnvironment?, CancellationToken)"/>이 확인한다).</summary>
        public static OmpDiscoveryResult Discover(string? configured, OmpEnvironment? env = null)
        {
            env ??= new OmpEnvironment();
            string refreshed = OmpPathEnvironment.Build(env);
            var dirs = SplitDirs(refreshed);
            var locations = new List<OmpLocation>();
            string? chosen = null;
            string? configuredMissing = null;

            // 1. 설정 경로
            if (string.IsNullOrWhiteSpace(configured))
                locations.Add(new OmpLocation("configured", null, OmpLocationStatus.Skipped));
            else
            {
                string path = configured.Trim().Trim('"');
                try { path = env.Expand(path); } catch (ArgumentException) { }
                if (env.FileExists(path))
                {
                    locations.Add(new OmpLocation("configured", path, OmpLocationStatus.Found));
                    chosen = path;
                }
                else
                {
                    locations.Add(new OmpLocation("configured", path, OmpLocationStatus.Missing));
                    configuredMissing = path;
                }
            }

            // 2. PATH: omp.exe → 래퍼(.cmd/.bat, PATHEXT 순서) → omp.ps1(미지원)
            string? onPathExe = FindOnPath(dirs, "omp.exe", env);
            locations.Add(new OmpLocation("path-exe", onPathExe, onPathExe != null ? OmpLocationStatus.Found : OmpLocationStatus.Missing));
            string? onPathWrapper = null;
            foreach (string ext in WrapperExtensions(env))
            {
                onPathWrapper = FindOnPath(dirs, "omp" + ext, env);
                if (onPathWrapper != null) break;
            }
            locations.Add(new OmpLocation("path-cmd", onPathWrapper, onPathWrapper != null ? OmpLocationStatus.Found : OmpLocationStatus.Missing));
            string? onPathPs1 = FindOnPath(dirs, "omp.ps1", env);
            locations.Add(new OmpLocation("path-ps1", onPathPs1, onPathPs1 != null ? OmpLocationStatus.Unsupported : OmpLocationStatus.Missing));
            chosen ??= onPathExe ?? onPathWrapper;

            // 3. %LOCALAPPDATA%\omp\omp.exe
            string? installed = env.DefaultInstallPath;
            if (installed == null)
                locations.Add(new OmpLocation("installed", null, OmpLocationStatus.Skipped));
            else
            {
                bool there = env.FileExists(installed);
                locations.Add(new OmpLocation("installed", installed, there ? OmpLocationStatus.Found : OmpLocationStatus.Missing));
                if (there) chosen ??= installed;
            }

            var candidates = chosen == null ? FindCandidates(env, dirs, locations) : Array.Empty<OmpCandidate>();
            OmpProblemKind problem = OmpProblemKind.None;
            string? detail = null;
            if (chosen == null)
            {
                if (onPathPs1 != null) { problem = OmpProblemKind.UnsupportedWrapper; detail = onPathPs1; }
                else if (configuredMissing != null) { problem = OmpProblemKind.ConfiguredPathMissing; detail = configuredMissing; }
                else problem = OmpProblemKind.NotFound;
            }
            return new OmpDiscoveryResult(locations, chosen, candidates, null, problem, refreshed)
            {
                ProblemDetail = detail,
                ConfiguredMissing = configuredMissing,
            };
        }

        /// <summary>설정을 읽어 탐색하고 `omp --version`으로 검증한다(UI 없음, 백그라운드 스레드 가능).</summary>
        public static Task<OmpDiscoveryResult> DiscoverAsync(AppSettings settings, CancellationToken ct = default) =>
            DiscoverAsync(settings.AgentOmpPath, settings.AgentOmpVerified, null, ct);

        /// <summary>
        /// 탐색 + 검증. verifiedCache가 같은 파일(경로·크기·수정 시각)의 이전 검증이면 실행을 건너뛴다.
        /// 문제가 있으면 후보(Downloads의 릴리스 파일·실행 중인 omp)도 채운다.
        /// </summary>
        public static async Task<OmpDiscoveryResult> DiscoverAsync(string? configured, string? verifiedCache, OmpEnvironment? env, CancellationToken ct = default)
        {
            env ??= new OmpEnvironment();
            var found = Discover(configured, env);
            if (found.ChosenExe == null) return found;
            string exe = found.ChosenExe;
            ct.ThrowIfCancellationRequested();

            if (TryReadCache(verifiedCache, exe, env, out var cachedVersion) && cachedVersion >= OmpVersion.Minimum)
                return found with { Version = cachedVersion, VerifiedCache = verifiedCache };

            OmpRunOutcome run = await env.RunVersion(exe, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            OmpDiscoveryResult Fail(OmpProblemKind kind, string detail, OmpVersion? version = null)
            {
                var locations = found.Checked.ToList();
                return found with
                {
                    Problem = kind,
                    ProblemDetail = detail,
                    Version = version,
                    Candidates = FindCandidates(env, SplitDirs(found.RefreshedPath), locations),
                };
            }

            if (!run.Started)
                return Fail(OmpProblemKind.NotRunnable, StartFailure(run, exe, env));
            if (run.TimedOut)
                return Fail(OmpProblemKind.UnknownVersion, "timeout");
            if (!TryParseVersionOutput(run.Output, out var version))
            {
                string text = FirstLine(run.Output);
                if (run.ExitCode is int code and not 0 && text.Length == 0)
                    return Fail(OmpProblemKind.NotRunnable, $"exit code {code}");
                return Fail(OmpProblemKind.UnknownVersion, text);
            }
            if (version < OmpVersion.Minimum)
                return Fail(OmpProblemKind.TooOld, version.ToString(), version);

            return found with { Version = version, VerifiedCache = MakeCacheEntry(exe, version, env) };
        }

        /// <summary>출력의 첫 줄에서 버전을 읽고, 없으면 다른 줄에서 `omp/&lt;버전&gt;` 모양을 찾는다.</summary>
        internal static bool TryParseVersionOutput(string? output, out OmpVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(output)) return false;
            string[] lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0 && OmpVersion.TryParse(lines[0], out version)) return true;
            foreach (string line in lines)
            {
                var m = Regex.Match(line, @"\bomp/(\d+\.\d+(?:\.\d+)?)");
                if (m.Success && OmpVersion.TryParse(m.Groups[1].Value, out version)) return true;
            }
            return false;
        }

        private static string FirstLine(string? text) =>
            text?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        private static string StartFailure(OmpRunOutcome run, string exe, OmpEnvironment env)
        {
            string text = run.StartError ?? "";
            string hint = run.StartErrorCode switch
            {
                5 => "access denied",
                193 => "not a valid Windows application for this PC (architecture mismatch or damaged download)",
                1260 => "blocked by a Windows policy",
                225 => "blocked by antivirus",
                740 => "requires elevation",
                _ => "",
            };
            if (hint.Length > 0) text = $"{hint}; {text}";
            if (HasInternetMark(exe)) text += " [downloaded file is marked as from the internet (Zone.Identifier)]";
            return text;
        }

        /// <summary>인터넷에서 받은 파일 표시(Zone.Identifier 스트림)가 있는지.</summary>
        internal static bool HasInternetMark(string path)
        {
            try { return ZoneMark.Exists(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        }

        // ---- 캐시 -----------------------------------------------------------------------------------------------

        /// <summary>"경로|버전|크기|수정 시각 ticks". .exe가 아닌 래퍼는 null(래퍼 뒤의 실제 프로그램이 바뀌어도 알 수 없다).</summary>
        public static string? MakeCacheEntry(string exe, OmpVersion version, OmpEnvironment? env = null)
        {
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            var stamp = (env ?? new OmpEnvironment()).Stamp(exe);
            return stamp is { } s ? $"{exe}|{version}|{s.Length}|{s.Ticks}" : null;
        }

        private static bool TryReadCache(string? cache, string exe, OmpEnvironment env, out OmpVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(cache)) return false;
            string[] f = cache.Split('|');
            if (f.Length != 4 || !string.Equals(f[0], exe, StringComparison.OrdinalIgnoreCase)) return false;
            if (!OmpVersion.TryParse(f[1], out version)) return false;
            if (!long.TryParse(f[2], out long length) || !long.TryParse(f[3], out long ticks)) return false;
            return env.Stamp(exe) is { } s && s.Length == length && s.Ticks == ticks;
        }

        // ---- PATH 탐색 ------------------------------------------------------------------------------------------

        private static List<string> SplitDirs(string path)
        {
            var dirs = new List<string>();
            foreach (string d in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = d.Trim().Trim('"');
                if (t.Length > 0) dirs.Add(t);
            }
            return dirs;
        }

        /// <summary>PATHEXT 순서대로 .cmd/.bat만(그 밖의 확장자는 표준 입출력 omp로 쓸 수 없다).</summary>
        private static IEnumerable<string> WrapperExtensions(OmpEnvironment env)
        {
            string pathExt = env.GetEnv("PATHEXT") ?? string.Join(';', s_defaultPathExt);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string e in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string ext = e.StartsWith('.') ? e : "." + e;
                if ((ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)) && seen.Add(ext))
                    yield return ext.ToLowerInvariant();
            }
        }

        private static string? FindOnPath(List<string> dirs, string fileName, OmpEnvironment env)
        {
            foreach (string dir in dirs)
            {
                string candidate;
                try { candidate = Path.Combine(dir, fileName); }
                catch (ArgumentException) { continue; }
                if (env.FileExists(candidate)) return candidate;
            }
            return null;
        }

        // ---- 후보 -----------------------------------------------------------------------------------------------

        /// <summary>omp-windows-x64.exe / omp-windows-arm64.exe(PATH·Downloads)와 실행 중인 omp 프로세스. 이미 확인한 위치와 중복은 뺀다.</summary>
        private static IReadOnlyList<OmpCandidate> FindCandidates(OmpEnvironment env, List<string> pathDirs, List<OmpLocation> checkedLocations)
        {
            bool ko = Loc.CurrentLanguage == "ko";
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in checkedLocations) if (l.Path != null) known.Add(l.Path);
            var result = new List<OmpCandidate>();
            void Add(string path, string reason, OmpCandidateKind kind)
            {
                if (known.Add(path)) result.Add(new OmpCandidate(path, reason, kind));
            }

            string[] names = env.Architecture == "arm64"
                ? new[] { "omp-windows-arm64.exe", "omp-windows-x64.exe" }
                : new[] { "omp-windows-x64.exe", "omp-windows-arm64.exe" };

            if (env.UserProfile is { } profile)
            {
                string downloads = Path.Combine(profile, "Downloads");
                foreach (string name in names)
                {
                    string p = Path.Combine(downloads, name);
                    if (env.FileExists(p)) Add(p, ko ? "다운로드 폴더" : "Downloads folder", OmpCandidateKind.Downloads);
                }
            }
            foreach (string dir in pathDirs)
                foreach (string name in names)
                {
                    string p;
                    try { p = Path.Combine(dir, name); } catch (ArgumentException) { continue; }
                    if (env.FileExists(p)) Add(p, ko ? "PATH의 릴리스 파일" : "release file on PATH", OmpCandidateKind.Path);
                }
            foreach (string running in env.RunningOmpPaths())
                if (env.FileExists(running)) Add(running, ko ? "실행 중인 omp" : "running omp", OmpCandidateKind.RunningProcess);
            return result;
        }

        // ---- 안내 문구 ------------------------------------------------------------------------------------------

        internal static string Describe(OmpDiscoveryResult r, bool ko)
        {
            string T(string en, string kor) => ko ? kor : en;
            string detail = r.ProblemDetail ?? "";
            switch (r.Problem)
            {
                case OmpProblemKind.None:
                    return r.Version is { } v ? $"omp {v}" : "omp";
                case OmpProblemKind.NotFound:
                    return T("omp (oh-my-pi) was not found.", "omp(oh-my-pi)를 찾을 수 없습니다.") + "\n" + CheckedText(r, ko);
                case OmpProblemKind.ConfiguredPathMissing:
                    return T($"There is no file at the omp path in the settings: {detail}", $"설정에 지정한 omp 경로에 파일이 없습니다: {detail}") + "\n" + CheckedText(r, ko);
                case OmpProblemKind.UnsupportedWrapper:
                    return T($"Only a PowerShell wrapper was found ({detail}). It cannot be used here; install omp.exe.",
                             $"PowerShell 래퍼만 찾았습니다({detail}). 이 앱에서는 쓸 수 없습니다. omp.exe를 설치하세요.") + "\n" + CheckedText(r, ko);
                case OmpProblemKind.NotRunnable:
                    return T($"omp was found but cannot be run: {r.ChosenExe} ({detail})", $"omp를 찾았지만 실행할 수 없습니다: {r.ChosenExe} ({detail})");
                case OmpProblemKind.TooOld:
                    return T($"omp {r.Version} is too old; {OmpVersion.Minimum} or newer is required.", $"omp {r.Version}은(는) 너무 오래되었습니다. {OmpVersion.Minimum} 이상이 필요합니다.");
                case OmpProblemKind.UnknownVersion:
                    return T($"Cannot determine the omp version of {r.ChosenExe} ({(detail.Length == 0 ? "no answer" : detail)}).",
                             $"omp 버전을 확인할 수 없습니다: {r.ChosenExe} ({(detail.Length == 0 ? "응답 없음" : detail)})");
                default:
                    return detail;
            }
        }

        /// <summary>"확인한 위치:" 목록.</summary>
        internal static string CheckedText(OmpDiscoveryResult r, bool ko)
        {
            var sb = new StringBuilder(ko ? "확인한 위치:" : "Checked locations:");
            foreach (var l in r.Checked)
            {
                string status = l.Status switch
                {
                    OmpLocationStatus.Found => ko ? "있음" : "found",
                    OmpLocationStatus.Missing => ko ? "없음" : "missing",
                    OmpLocationStatus.Unsupported => ko ? "지원 안 함" : "unsupported",
                    _ => ko ? "설정 안 됨" : "not set",
                };
                sb.Append("\n- ").Append(l.Label(ko)).Append(": ");
                if (l.Path != null) sb.Append(l.Path).Append(" — ");
                sb.Append(status);
            }
            return sb.ToString();
        }
    }
}
