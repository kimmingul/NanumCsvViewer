using NanumCsvViewer.Agent.Rpc;
using Xunit;

namespace NanumCsvViewer.Tests
{
    /// <summary>컨트롤러 테스트용: omp 찾기·검증 결과를 가짜로 만든다(경로 null = 못 찾음, 버전 글이 최소보다 낮거나 읽을 수 없으면 해당 문제).</summary>
    internal static class TestOmp
    {
        public static OmpDiscoveryResult Result(string? path, string? versionText)
        {
            if (path == null)
                return new OmpDiscoveryResult(Array.Empty<OmpLocation>(), null, Array.Empty<OmpCandidate>(), null, OmpProblemKind.NotFound, "");
            OmpVersion? version = OmpDiscovery.TryParseVersionOutput(versionText, out var v) ? v : null;
            var problem = version is not { } ver ? OmpProblemKind.UnknownVersion : ver < OmpVersion.Minimum ? OmpProblemKind.TooOld : OmpProblemKind.None;
            return new OmpDiscoveryResult(Array.Empty<OmpLocation>(), path, Array.Empty<OmpCandidate>(), version, problem, "");
        }
    }

    public class OmpDiscoveryTests
    {
        private const string Local = @"C:\Users\u\AppData\Local";
        private const string Profile = @"C:\Users\u";

        /// <summary>가짜 파일 시스템·환경 변수·레지스트리 PATH.</summary>
        private sealed class Rig
        {
            public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string? ProcessPath { get; set; } = "";
            public string? MachinePath { get; set; }
            public string? UserPath { get; set; }
            public string? PathExt { get; set; }
            public string Arch { get; set; } = "x64";
            public List<string> Running { get; } = new();
            public Dictionary<string, (long, long)> Stamps { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Func<string, OmpRunOutcome> Version { get; set; } = _ => OmpRunOutcome.Ran("omp/18.5.0\n");
            public int VersionRuns { get; private set; }

            public OmpEnvironment Env() => new()
            {
                GetEnv = n => n switch
                {
                    "PATH" => ProcessPath,
                    "LOCALAPPDATA" => Local,
                    "USERPROFILE" => Profile,
                    "PATHEXT" => PathExt,
                    _ => null,
                },
                FileExists = p => Files.Contains(p),
                ReadMachinePath = () => MachinePath,
                ReadUserPath = () => UserPath,
                Expand = s => s.Replace("%LOCALAPPDATA%", Local, StringComparison.OrdinalIgnoreCase).Replace("%USERPROFILE%", Profile, StringComparison.OrdinalIgnoreCase),
                RunningOmpPaths = () => Running,
                Stamp = p => Stamps.TryGetValue(p, out var s) ? s : null,
                RunVersion = (p, _) => { VersionRuns++; return Task.FromResult(Version(p)); },
                Architecture = Arch,
            };
        }

        [Fact]
        public void Discovery_prefers_configured_then_PATH_then_LocalAppData_and_lists_every_location()
        {
            var rig = new Rig { ProcessPath = @"C:\tools;C:\other" };
            string configured = @"D:\mine\omp.exe", onPath = @"C:\tools\omp.exe", installed = Local + @"\omp\omp.exe";
            rig.Files.UnionWith(new[] { configured, onPath, installed });

            var all = OmpDiscovery.Discover(configured, rig.Env());
            Assert.Equal(configured, all.ChosenExe);
            Assert.Equal(OmpProblemKind.None, all.Problem);
            Assert.Equal(new[] { configured, onPath, installed }, all.Checked.Where(l => l.Status == OmpLocationStatus.Found).Select(l => l.Path));

            rig.Files.Remove(configured);
            var noConfigured = OmpDiscovery.Discover(configured, rig.Env());
            Assert.Equal(onPath, noConfigured.ChosenExe);
            Assert.Equal(configured, noConfigured.ConfiguredMissing);

            rig.Files.Remove(onPath);
            Assert.Equal(installed, OmpDiscovery.Discover(null, rig.Env()).ChosenExe);
        }

        [Fact]
        public void A_configured_path_without_a_file_is_reported_when_nothing_else_is_found()
        {
            var rig = new Rig();
            var r = OmpDiscovery.Discover("\"D:\\gone\\omp.exe\"", rig.Env());
            Assert.Null(r.ChosenExe);
            Assert.Equal(OmpProblemKind.ConfiguredPathMissing, r.Problem);
            Assert.Equal(@"D:\gone\omp.exe", r.ProblemDetail);
            Assert.Contains(r.Checked, l => l.Path == @"D:\gone\omp.exe" && l.Status == OmpLocationStatus.Missing);

            var none = OmpDiscovery.Discover("  ", rig.Env());
            Assert.Equal(OmpProblemKind.NotFound, none.Problem);
            Assert.Equal(OmpLocationStatus.Skipped, none.Checked[0].Status);
        }

        [Fact]
        public void PATH_is_reread_from_the_registry_and_expanded()
        {
            var rig = new Rig
            {
                ProcessPath = @"C:\bin;C:\Tools\",
                MachinePath = @"C:\Windows;c:\tools",
                UserPath = @"%USERPROFILE%\newbin\;C:\bin",
            };
            rig.Files.Add(Profile + @"\newbin\omp.exe");

            var r = OmpDiscovery.Discover(null, rig.Env());
            Assert.Equal(Profile + @"\newbin\omp.exe", r.ChosenExe);
            // 프로세스 PATH의 순서를 지키고, 같은 항목(대소문자·끝 '\')은 한 번만, 새 항목은 뒤에 붙는다.
            Assert.Equal(@"C:\bin;C:\Tools\;C:\Windows;" + Profile + @"\newbin\", r.RefreshedPath);
        }

        [Fact]
        public void Exe_wins_over_wrappers_and_PATHEXT_orders_the_wrappers()
        {
            var rig = new Rig { ProcessPath = @"C:\a;C:\b", PathExt = ".COM;.BAT;.CMD" };
            rig.Files.UnionWith(new[] { @"C:\a\omp.cmd", @"C:\b\omp.bat" });
            Assert.Equal(@"C:\b\omp.bat", OmpDiscovery.Discover(null, rig.Env()).ChosenExe);

            rig.PathExt = ".CMD;.BAT";
            Assert.Equal(@"C:\a\omp.cmd", OmpDiscovery.Discover(null, rig.Env()).ChosenExe);

            rig.Files.Add(@"C:\b\omp.exe");
            Assert.Equal(@"C:\b\omp.exe", OmpDiscovery.Discover(null, rig.Env()).ChosenExe);
        }

        [Fact]
        public void A_PowerShell_wrapper_alone_is_reported_as_unsupported()
        {
            var rig = new Rig { ProcessPath = @"C:\npm" };
            rig.Files.Add(@"C:\npm\omp.ps1");
            var r = OmpDiscovery.Discover(null, rig.Env());
            Assert.Null(r.ChosenExe);
            Assert.Equal(OmpProblemKind.UnsupportedWrapper, r.Problem);
            Assert.Equal(@"C:\npm\omp.ps1", r.ProblemDetail);
            Assert.Contains(r.Checked, l => l.Status == OmpLocationStatus.Unsupported);

            rig.Files.Add(@"C:\npm\omp.cmd");   // npm은 셋을 함께 만든다: .cmd가 쓰인다
            Assert.Equal(@"C:\npm\omp.cmd", OmpDiscovery.Discover(null, rig.Env()).ChosenExe);
        }

        [Fact]
        public void Release_named_files_and_running_processes_are_candidates_only_and_never_chosen()
        {
            var rig = new Rig { ProcessPath = @"C:\pathdir", Arch = "arm64" };
            string dl64 = Profile + @"\Downloads\omp-windows-x64.exe", dlArm = Profile + @"\Downloads\omp-windows-arm64.exe";
            string onPath = @"C:\pathdir\omp-windows-x64.exe", running = @"D:\elsewhere\omp-windows-x64.exe";
            rig.Files.UnionWith(new[] { dl64, dlArm, onPath, running });
            rig.Running.AddRange(new[] { running, running, @"X:\vanished\omp.exe" });

            var r = OmpDiscovery.Discover(null, rig.Env());
            Assert.Null(r.ChosenExe);
            Assert.Equal(OmpProblemKind.NotFound, r.Problem);
            Assert.Equal(new[] { dlArm, dl64, onPath, running }, r.Candidates.Select(c => c.Path));   // 네이티브(arm64) 먼저, 중복·없는 파일 제외
            Assert.Equal(OmpCandidateKind.RunningProcess, r.Candidates[^1].Kind);

            rig.Files.Add(Local + @"\omp\omp.exe");
            Assert.Empty(OmpDiscovery.Discover(null, rig.Env()).Candidates);   // 찾았으면 후보를 보이지 않는다
        }

        [Fact]
        public async Task Verification_separates_blocked_too_old_unknown_and_ok()
        {
            var rig = new Rig();
            string exe = Local + @"\omp\omp.exe";
            rig.Files.UnionWith(new[] { exe, Profile + @"\Downloads\omp-windows-x64.exe" });

            rig.Version = _ => new OmpRunOutcome(null, null, false, 193, "not a valid Win32 application");
            var blocked = await OmpDiscovery.DiscoverAsync(null, null, rig.Env());
            Assert.Equal(OmpProblemKind.NotRunnable, blocked.Problem);
            Assert.Equal(exe, blocked.ChosenExe);
            Assert.Contains("architecture", blocked.ProblemDetail);
            Assert.Single(blocked.Candidates);   // 실패하면 Downloads의 릴리스 파일을 후보로 낸다

            rig.Version = _ => OmpRunOutcome.Ran("omp/18.4.3\n");
            var old = await OmpDiscovery.DiscoverAsync(null, null, rig.Env());
            Assert.Equal(OmpProblemKind.TooOld, old.Problem);
            Assert.Equal(new OmpVersion(18, 4, 3), old.Version);

            rig.Version = _ => OmpRunOutcome.Ran("something else\n");
            Assert.Equal(OmpProblemKind.UnknownVersion, (await OmpDiscovery.DiscoverAsync(null, null, rig.Env())).Problem);

            rig.Version = _ => new OmpRunOutcome(null, null, true, null, null);
            Assert.Equal(OmpProblemKind.UnknownVersion, (await OmpDiscovery.DiscoverAsync(null, null, rig.Env())).Problem);

            rig.Version = _ => new OmpRunOutcome("", 1, false, null, null);
            Assert.Equal(OmpProblemKind.NotRunnable, (await OmpDiscovery.DiscoverAsync(null, null, rig.Env())).Problem);

            rig.Version = _ => OmpRunOutcome.Ran("(node) ExperimentalWarning\nomp/18.5.0\n");
            var ok = await OmpDiscovery.DiscoverAsync(null, null, rig.Env());
            Assert.True(ok.IsOk);
            Assert.Equal(new OmpVersion(18, 5, 0), ok.Version);
        }

        [Fact]
        public async Task A_cache_hit_skips_the_version_run_and_a_changed_file_does_not()
        {
            var rig = new Rig();
            string exe = Local + @"\omp\omp.exe";
            rig.Files.Add(exe);
            rig.Stamps[exe] = (1000, 5);

            var first = await OmpDiscovery.DiscoverAsync(null, null, rig.Env());
            Assert.True(first.IsOk);
            Assert.Equal(1, rig.VersionRuns);
            Assert.StartsWith(exe + "|18.5.0|1000|5", first.VerifiedCache);

            var cached = await OmpDiscovery.DiscoverAsync(null, first.VerifiedCache, rig.Env());
            Assert.True(cached.IsOk);
            Assert.Equal(new OmpVersion(18, 5, 0), cached.Version);
            Assert.Equal(1, rig.VersionRuns);

            rig.Stamps[exe] = (1001, 5);   // 파일이 바뀌었다
            await OmpDiscovery.DiscoverAsync(null, first.VerifiedCache, rig.Env());
            Assert.Equal(2, rig.VersionRuns);

            // 최소 버전이 올라가 캐시된 버전이 모자라면 다시 확인한다.
            string stale = $"{exe}|18.0.0|1001|5";
            rig.Version = _ => OmpRunOutcome.Ran("omp/18.0.0");
            var tooOld = await OmpDiscovery.DiscoverAsync(null, stale, rig.Env());
            Assert.Equal(OmpProblemKind.TooOld, tooOld.Problem);
            Assert.Equal(3, rig.VersionRuns);
        }

        [Fact]
        public void Wrappers_are_never_cached_and_a_missing_file_is_rediscovered()
        {
            var rig = new Rig();
            rig.Stamps[@"C:\x\omp.cmd"] = (1, 1);
            Assert.Null(OmpDiscovery.MakeCacheEntry(@"C:\x\omp.cmd", new OmpVersion(18, 5, 0), rig.Env()));
            Assert.Null(OmpDiscovery.MakeCacheEntry(@"C:\x\gone.exe", new OmpVersion(18, 5, 0), rig.Env()));
        }

        [Fact]
        public async Task A_cached_path_that_vanished_falls_back_to_the_next_location()
        {
            var rig = new Rig { ProcessPath = @"C:\tools" };
            string cachedExe = Local + @"\omp\omp.exe", onPath = @"C:\tools\omp.exe";
            rig.Files.Add(onPath);
            rig.Stamps[onPath] = (7, 7);
            var r = await OmpDiscovery.DiscoverAsync(null, $"{cachedExe}|18.5.0|1|1", rig.Env());
            Assert.Equal(onPath, r.ChosenExe);
            Assert.Equal(1, rig.VersionRuns);
        }

        [Fact]
        public void Process_path_refresh_keeps_everything_from_the_process_and_adds_registry_entries()
        {
            var rig = new Rig { ProcessPath = @"C:\venv\Scripts;C:\Windows", MachinePath = @"C:\Windows\;C:\M", UserPath = null };
            Assert.Equal(@"C:\venv\Scripts;C:\Windows;C:\M", OmpPathEnvironment.Build(rig.Env()));
            rig.ProcessPath = null;
            Assert.Equal(@"C:\Windows\;C:\M", OmpPathEnvironment.Build(rig.Env()));
        }

        [Fact]
        public void Problem_text_lists_the_checked_locations()
        {
            var rig = new Rig { ProcessPath = @"C:\tools" };
            var r = OmpDiscovery.Discover(null, rig.Env());
            string text = r.Describe(korean: false);
            Assert.Contains("was not found", text);
            Assert.Contains(@"%LOCALAPPDATA%\omp\omp.exe", text);
            Assert.Contains("Checked locations", text);
            Assert.Contains("NotFound", r.ToDiagnosticText());
            Assert.Contains("찾을 수 없습니다", r.Describe(korean: true));
        }
    }
}
