using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Python;
using NanumCsvViewer.Agent.Rpc;
using NanumCsvViewer.Agent.Tools;

namespace NanumCsvViewer.Tests
{
    public class AnalysisRequirementsTests
    {
        [Fact]
        public void Every_group_file_loads_and_every_line_is_an_exact_pin()
        {
            Assert.Equal(new[] { "core", "stats", "clinical", "ml" }, AnalysisGroups.All.Select(g => g.Name));
            foreach (var g in AnalysisGroups.All)
            {
                Assert.NotEmpty(g.Pins);
                Assert.All(g.Pins, p =>
                {
                    Assert.Matches(@"^\d+(\.\d+)+$", p.Version);
                    Assert.Contains("==" + p.Version, p.Spec);
                });
                Assert.NotEqual(g.Name, g.TitleEn);          // 머리의 title-en/ko·desc-en/ko가 채워져 있다
                Assert.NotEqual(g.Name, g.TitleKo);
                Assert.NotEqual(g.Name, g.DescriptionEn);
                Assert.NotEqual(g.Name, g.DescriptionKo);
                Assert.InRange(g.ApproxMb, 10, 2000);                // 디스크 크기 안내(size-mb)
            }
        }

        [Fact]
        public void The_required_packages_are_in_the_right_groups_and_nowhere_twice()
        {
            string[] Names(string group) => AnalysisGroups.Get(group)!.Pins.Select(p => p.Key).Distinct().ToArray();
            Assert.Subset(Names("core").ToHashSet(), new[] { "pandas", "numpy", "scipy", "statsmodels", "matplotlib", "seaborn", "pyarrow", "openpyxl" }.ToHashSet());
            Assert.Subset(Names("stats").ToHashSet(), new[] { "pingouin", "scikit-posthocs" }.ToHashSet());
            Assert.Subset(Names("clinical").ToHashSet(), new[] { "lifelines", "scikit-survival" }.ToHashSet());
            Assert.Subset(Names("ml").ToHashSet(), new[] { "scikit-learn", "xgboost", "lightgbm", "shap", "imbalanced-learn" }.ToHashSet());

            Assert.Empty(AnalysisGroups.All.SelectMany(g => g.Pins).Where(p => p.Marker == null).GroupBy(p => p.Key).Where(x => x.Count() > 1));
            Assert.Equal("clinical", AnalysisGroups.ForPackage("Scikit_Survival")?.Name);
            Assert.Null(AnalysisGroups.ForPackage("left-pad"));
        }

        [Fact]
        public void Markers_decide_which_pins_apply_to_a_python_version()
        {
            var clinical = AnalysisGroups.Get("clinical")!;
            Assert.Contains(clinical.PinsFor(new Version(3, 12, 1)), p => p.Key == "scikit-survival");
            Assert.DoesNotContain(clinical.PinsFor(new Version(3, 13, 0)), p => p.Key == "scikit-survival");   // ecos: Windows 3.13 wheel 없음
            var stats = AnalysisGroups.Get("stats")!;
            Assert.Equal("1.3.0", Assert.Single(stats.PinsFor(new Version(3, 10, 6)), p => p.Key == "aeon").Version);
            Assert.Equal("1.6.0", Assert.Single(stats.PinsFor(new Version(3, 12, 0)), p => p.Key == "aeon").Version);
        }

        [Fact]
        public void Parser_rejects_ranges_urls_and_duplicates_because_they_break_reproducibility()
        {
            Assert.Throws<FormatException>(() => AnalysisGroups.Parse("x", "pandas>=2.0\n"));
            Assert.Throws<FormatException>(() => AnalysisGroups.Parse("x", "pandas\n"));
            Assert.Throws<FormatException>(() => AnalysisGroups.Parse("x", "git+https://example.com/p.git\n"));
            Assert.Throws<FormatException>(() => AnalysisGroups.Parse("x", "pandas==2.3.3\nPandas==2.3.3\n"));
            Assert.Throws<FormatException>(() => AnalysisGroups.Parse("x", "# only comments\n"));
            var ok = AnalysisGroups.Parse("x", "# title-en: T\n\n numpy==2.2.6 ; python_version < \"3.13\" \n");
            Assert.Equal("T", ok.TitleEn);
            Assert.Equal("python_version < \"3.13\"", ok.Pins[0].Marker);
        }

        [Fact]
        public void Fingerprint_changes_with_a_pin_and_ignores_pins_that_do_not_apply()
        {
            var a = AnalysisGroups.Parse("x", "numpy==2.2.6\nscikit-survival==0.25.0; python_version < \"3.13\"\n");
            var b = AnalysisGroups.Parse("x", "numpy==2.2.7\nscikit-survival==0.25.0; python_version < \"3.13\"\n");
            var c = AnalysisGroups.Parse("x", "numpy==2.2.6\nscikit-survival==0.99.0; python_version < \"3.13\"\n");
            Assert.NotEqual(a.Fingerprint(new Version(3, 12)), b.Fingerprint(new Version(3, 12)));
            Assert.NotEqual(a.Fingerprint(new Version(3, 12)), c.Fingerprint(new Version(3, 12)));
            Assert.Equal(a.Fingerprint(new Version(3, 13)), c.Fingerprint(new Version(3, 13)));
        }

        [Fact]
        public void Import_names_follow_what_the_real_wheels_install()
        {
            // 실제 설치로 확인한 import 이름(배포 이름과 다른 것들). 나머지는 이름의 - 를 _ 로.
            var imports = AnalysisGroups.All.SelectMany(g => g.PinsFor(new Version(3, 10, 6))).ToDictionary(p => p.Key, AnalysisEnvironment.ImportName);
            Assert.Equal("sklearn", imports["scikit-learn"]);
            Assert.Equal("sksurv", imports["scikit-survival"]);
            Assert.Equal("imblearn", imports["imbalanced-learn"]);
            Assert.Equal("umap", imports["umap-learn"]);
            Assert.Equal("scikit_posthocs", imports["scikit-posthocs"]);
            Assert.Equal("pandas", imports["pandas"]);
        }

        [Fact]
        public void Resolve_always_adds_core_orders_groups_and_reports_unknown_names()
        {
            var groups = AnalysisGroups.Resolve(new[] { "ML", "clinical", "ml", " ", "nope" }, out var unknown);
            Assert.Equal(new[] { "core", "clinical", "ml" }, groups.Select(g => g.Name));
            Assert.Equal(new[] { "nope" }, unknown);
        }

        [Fact]
        public void Lock_parsing_keeps_pins_and_skips_comments_editables_and_local_paths()
        {
            var map = AnalysisLock.Parse("# header\nPandas==2.3.3\nscikit_learn==1.7.2 ; python_version>'3'\n-e git+https://x#egg=y\nmine @ file:///C:/x\n\nnumpy==2.2.6  # why\n");
            Assert.Equal(new[] { "numpy", "pandas", "scikit-learn" }, map.Keys.OrderBy(k => k));
            Assert.Equal("1.7.2", map["scikit-learn"]);
            Assert.Equal("2.2.6", map["numpy"]);
            Assert.Empty(AnalysisLock.Parse(null));
        }

        [Fact]
        public void Lock_format_sorts_drops_pip_noise_and_states_how_to_recreate()
        {
            string text = AnalysisLock.Format("[notice] A new release of pip\nWARNING: x\nzeta==1.0\n\nAlpha==2.0\n", new Version(3, 10, 6), new DateTime(2026, 10, 5, 1, 2, 3, DateTimeKind.Utc));
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Contains("Python 3.10.6", lines[1]);
            Assert.Contains("--only-binary=:all:", lines[2]);
            Assert.Equal(new[] { "Alpha==2.0", "zeta==1.0" }, lines.Where(l => !l.StartsWith('#')));
        }
    }

    public class AnalysisEnvironmentTests
    {
        private static readonly string BasePython = @"C:\Python310\python.exe";

        /// <summary>pip·venv를 흉내 내는 실행기: 설치된 목록을 기억해 freeze·uninstall에 답한다.</summary>
        private sealed class Sim
        {
            public FakeRunner Runner { get; } = new();
            public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Func<string[], ProcessResult?>? Intercept { get; set; }
            public string Platform { get; set; } = "win-amd64";
            public string Version { get; set; } = "3.10.6";
            public bool PythonOnPath { get; set; } = true;
            public string[] Orphans { get; set; } = Array.Empty<string>();
            public bool IncludeSystemSite { get; set; }

            public Sim(string root)
            {
                string venv = Path.Combine(root, "venv");
                Runner.OnRun = (exe, args) =>
                {
                    var hit = Intercept?.Invoke(new[] { exe }.Concat(args).ToArray());
                    if (hit != null) return hit;
                    bool isVenvPython = exe.StartsWith(venv, StringComparison.OrdinalIgnoreCase);
                    if (!isVenvPython)
                    {
                        if (args.Length >= 2 && args[0] == "-c" && args[1].Contains("sys.executable"))
                            return PythonOnPath && (exe == "python" || exe == BasePython) ? new ProcessResult(0, BasePython + "\n" + Version + "\n") : new ProcessResult(-1, "", "not found");
                        if (args.Length >= 2 && args[0] == "-c" && args[1].Contains("sysconfig")) return new ProcessResult(0, Platform + "\n");
                        if (args.Length >= 3 && args[0] == "-m" && args[1] == "venv")
                        {
                            Directory.CreateDirectory(Path.Combine(args[2], "Scripts"));
                            File.WriteAllText(Path.Combine(args[2], "Scripts", "python.exe"), "");
                            File.WriteAllText(Path.Combine(args[2], "pyvenv.cfg"), $"home = {Path.GetDirectoryName(BasePython)}\ninclude-system-site-packages = {(IncludeSystemSite ? "true" : "false")}\n");
                            return new ProcessResult(0, "");
                        }
                        return new ProcessResult(-1, "", "unexpected: " + exe);
                    }
                    if (args.Length > 2 && args[0] == "-m" && args[1] == "pip" && args[2] == "install")
                    {
                        foreach (string spec in args.Skip(3).Where(a => !a.StartsWith('-'))) Installed.Add(spec.Split(';')[0].Trim());
                        Installed.Add("tzdata==2025.2");
                        return new ProcessResult(0, "Collecting pandas==2.3.3\nDownloading pandas-2.3.3-cp310-cp310-win_amd64.whl (11.4 MB)\nSuccessfully installed pandas-2.3.3\n");
                    }
                    if (args.Length > 2 && args[0] == "-m" && args[1] == "pip" && args[2] == "freeze")
                        return new ProcessResult(0, string.Join("\n", Installed.OrderBy(x => x)) + "\n[notice] pip update available\n");
                    if (args.Length > 2 && args[0] == "-m" && args[1] == "pip" && args[2] == "uninstall")
                    {
                        foreach (string name in args.Skip(3).Where(a => !a.StartsWith('-')))
                            Installed.RemoveWhere(i => string.Equals(i.Split("==")[0], name, StringComparison.OrdinalIgnoreCase));
                        return new ProcessResult(0, "Successfully uninstalled");
                    }
                    if (args.Length > 1 && args[0] == "-c" && args[1].Contains("ORPHAN")) return new ProcessResult(0, string.Join("\n", Orphans.Select(o => "ORPHAN:" + o)) + "\n");
                    if (args.Length > 1 && args[0] == "-c" && args[1].Contains("importlib")) return new ProcessResult(0, "");
                    return new ProcessResult(-1, "", "unexpected venv call");
                };
            }
        }

        private static (AnalysisEnvironment Env, Sim Sim, TempFolder Tmp) Make(string? toolsMarker = null)
        {
            var tmp = new TempFolder();
            string root = tmp.Combine("python-analysis");
            var sim = new Sim(root);
            return (new AnalysisEnvironment(root, sim.Runner, toolsMarker), sim, tmp);
        }

        [Fact]
        public void Default_root_is_python_analysis_next_to_python_tools_and_the_venv_python_is_in_scripts()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(Path.Combine(local, "NanumCsvViewer", "python-analysis"), AnalysisEnvironment.DefaultRoot);
            Assert.Equal(Path.GetDirectoryName(PythonToolsEnvironment.DefaultRoot), Path.GetDirectoryName(AnalysisEnvironment.DefaultRoot));
            var env = new AnalysisEnvironment(@"X:\r", new FakeRunner());
            Assert.Equal(@"X:\r\venv\Scripts\python.exe", env.PythonPath);
            Assert.Equal(@"X:\r\venv\requirements.lock", env.LockFilePath);
            Assert.Equal(AnalysisEnvState.Missing, env.Inspect().State);
        }

        [Fact]
        public void Command_lines_install_wheels_only_into_the_venv_and_never_use_system_site_packages()
        {
            var install = AnalysisEnvironment.PipInstallArguments(new[] { "pandas==2.3.3", "numpy==2.2.6" });
            Assert.Equal(new[] { "-m", "pip", "install" }, install.Take(3));
            Assert.Contains("--only-binary=:all:", install);
            Assert.Contains("--require-virtualenv", install);
            Assert.Contains("--no-input", install);
            Assert.DoesNotContain("--user", install);
            Assert.DoesNotContain("--no-binary", install);
            Assert.DoesNotContain(install, a => a.Contains("--target") || a.Contains("--prefix") || a.Contains("--break-system-packages"));
            Assert.Equal(new[] { "pandas==2.3.3", "numpy==2.2.6" }, install.TakeLast(2));
            Assert.Equal(new[] { "-m", "venv", @"D:\v" }, AnalysisEnvironment.VenvArguments(@"D:\v"));
            Assert.DoesNotContain("--system-site-packages", AnalysisEnvironment.VenvArguments(@"D:\v"));
            Assert.Contains("--require-virtualenv", AnalysisEnvironment.PipUninstallArguments(new[] { "x" }));
        }

        [Fact]
        public async Task Install_creates_the_venv_pins_core_plus_the_group_checks_imports_and_writes_lock_and_marker()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            var messages = new List<AnalysisProgress>();

            var result = await env.InstallAsync(new[] { "ml" }, messages.Add, default);

            Assert.True(result.Ok, result.Message);
            // 호출 순서: Python 찾기(python) → 플랫폼 확인 → venv → pip install → import 확인 → pip freeze
            var calls = sim.Runner.Calls.ToList();
            Assert.Equal("python", calls[0].Exe);
            Assert.Equal(new[] { "-m", "venv", env.VenvDir }, calls.Single(c => c.Args.Length > 1 && c.Args[1] == "venv").Args);
            var pip = calls.Single(c => c.Args.Length > 2 && c.Args[1] == "pip" && c.Args[2] == "install");
            Assert.Equal(env.PythonPath, pip.Exe);
            Assert.Contains("--only-binary=:all:", pip.Args);
            Assert.Contains("--require-virtualenv", pip.Args);
            Assert.Contains("pandas==2.3.3", pip.Args);                  // core가 자동으로 들어간다
            Assert.Contains("scikit-learn==1.7.2", pip.Args);
            Assert.DoesNotContain(pip.Args, a => a.StartsWith("lifelines"));   // 요청하지 않은 묶음은 없다
            Assert.True(calls.IndexOf(pip) > calls.FindIndex(c => c.Args.Length > 1 && c.Args[1] == "venv"));
            var verify = calls.Single(c => c.Args.Length > 1 && c.Args[0] == "-c" && c.Args[1].Contains("importlib"));
            Assert.Contains("sklearn", verify.Args);                    // 배포 이름과 import 이름이 다른 패키지
            Assert.Contains("imblearn", verify.Args);
            Assert.True(calls.Last().Args.SequenceEqual(AnalysisEnvironment.PipFreezeArguments()));

            Assert.Contains(messages, m => m.Step == "venv");
            Assert.Contains(messages, m => m.Step == "install" && m.Message.Contains("Downloading pandas"));    // pip 줄이 그대로 진행으로 온다
            Assert.False(env.IsBusy);

            string lockText = File.ReadAllText(env.LockFilePath);
            Assert.Contains("pandas==2.3.3", lockText);
            Assert.DoesNotContain("[notice]", lockText);
            var info = env.Inspect();
            Assert.Equal(AnalysisEnvState.Ready, info.State);
            Assert.Equal(new Version(3, 10, 6), info.PythonVersion);
            Assert.Equal(BasePython, info.BasePython);
            Assert.Equal(new[] { "core", "ml" }, info.InstalledGroupNames);
            Assert.Equal("1.7.2", info.Group("ml")!.Versions["scikit-learn"]);
            Assert.Equal(AnalysisGroupState.NotInstalled, info.Group("clinical")!.State);
            Assert.Equal("2.3.3", info.Packages["pandas"]);
        }

        [Fact]
        public async Task A_second_install_of_what_is_present_does_not_run_pip_and_a_new_group_keeps_the_old_ones_pinned()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            Assert.True((await env.InstallAsync(new[] { "stats" }, null, default)).Ok);
            sim.Runner.Calls.Clear();

            var again = await env.InstallAsync(new[] { "stats", "core" }, null, default);
            Assert.True(again.Ok);
            Assert.Empty(sim.Runner.Calls);

            Assert.True((await env.InstallAsync(new[] { "clinical" }, null, default)).Ok);
            var pip = sim.Runner.Calls.Single(c => c.Args.Length > 2 && c.Args[2] == "install");
            Assert.Contains("pingouin==0.5.5", pip.Args);               // 이미 설치된 묶음도 함께 풀어 깨뜨리지 않는다
            Assert.Contains("lifelines==0.30.0", pip.Args);
            Assert.DoesNotContain(sim.Runner.Calls, c => c.Args.Length > 1 && c.Args[1] == "venv");   // 환경은 다시 만들지 않는다
            Assert.Equal(new[] { "core", "stats", "clinical" }, env.Inspect().InstalledGroupNames);
        }

        [Fact]
        public async Task A_changed_pin_marks_the_group_outdated_and_update_runs_pip_again()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            Assert.True((await env.InstallAsync(new[] { "core" }, null, default)).Ok);
            string marker = File.ReadAllText(env.MarkerPath);
            File.WriteAllText(env.MarkerPath, marker.Replace(AnalysisGroups.Get("core")!.Fingerprint(new Version(3, 10, 6)), "0000000000000000"));

            Assert.Equal(AnalysisGroupState.Outdated, env.Inspect().Group("core")!.State);
            sim.Runner.Calls.Clear();
            Assert.True((await env.InstallAsync(new[] { "core" }, null, default)).Ok);
            Assert.Contains(sim.Runner.Calls, c => c.Args.Length > 2 && c.Args[2] == "install");
            Assert.Equal(AnalysisGroupState.Installed, env.Inspect().Group("core")!.State);
        }

        [Fact]
        public async Task Missing_pins_in_the_lock_mark_a_group_damaged()
        {
            var (env, _, tmp) = Make();
            using var _t = tmp;
            Assert.True((await env.InstallAsync(new[] { "core" }, null, default)).Ok);
            File.WriteAllText(env.LockFilePath, File.ReadAllText(env.LockFilePath).Replace("scipy==", "#scipy=="));
            Assert.Equal(AnalysisGroupState.Damaged, env.Inspect().Group("core")!.State);
        }

        [Fact]
        public async Task Unknown_groups_are_rejected_before_anything_runs()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            var r = await env.InstallAsync(new[] { "core", "tensorflow" }, null, default);
            Assert.False(r.Ok);
            Assert.Equal(AnalysisFailure.UnknownGroup, r.Failure);
            Assert.Contains("tensorflow", r.Message);
            Assert.Empty(sim.Runner.Calls);
        }

        [Fact]
        public async Task Missing_python_gives_an_install_guide_and_creates_nothing()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            sim.PythonOnPath = false;

            var r = await env.InstallAsync(new[] { "core" }, null, default);

            Assert.False(r.Ok);
            Assert.Equal(AnalysisFailure.NoPython, r.Failure);
            Assert.Equal(AnalysisEnvState.Missing, env.Inspect().State);
            Assert.DoesNotContain(sim.Runner.Calls, c => c.Args.Length > 1 && c.Args[1] == "venv");
            string en = AnalysisMessages.Describe(r, false), ko = AnalysisMessages.Describe(r, true);
            Assert.Contains("python.org", en);
            Assert.Contains("Python 3.10", en);
            Assert.Contains("python.org", ko);
            Assert.Contains("3.10", ko);
            Assert.False(env.IsBusy);
        }

        [Fact]
        public async Task Too_old_python_is_refused_with_its_version()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            sim.Version = "3.8.10";
            var r = await env.InstallAsync(new[] { "core" }, null, default);
            Assert.Equal(AnalysisFailure.NoPython, r.Failure);
            Assert.Contains("3.8.10", r.Message);
            Assert.Contains("3.10", r.Message);
        }

        [Theory]
        [InlineData("win-arm64")]
        [InlineData("win32")]
        public async Task Wheel_less_platforms_are_refused_honestly_before_creating_the_venv(string platform)
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            sim.Platform = platform;
            var r = await env.InstallAsync(new[] { "core" }, null, default);
            Assert.Equal(AnalysisFailure.UnsupportedPlatform, r.Failure);
            Assert.Contains("never builds from source", AnalysisMessages.Describe(r, false));
            Assert.False(Directory.Exists(env.VenvDir));
        }

        [Fact]
        public async Task A_venv_that_would_see_system_site_packages_is_refused()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            sim.IncludeSystemSite = true;
            var r = await env.InstallAsync(new[] { "core" }, null, default);
            Assert.Equal(AnalysisFailure.VenvFailed, r.Failure);
            Assert.DoesNotContain(sim.Runner.Calls, c => c.Args.Length > 2 && c.Args[2] == "install");
        }

        [Fact]
        public async Task Network_failure_is_reported_as_offline_and_a_retry_repairs_the_half_made_env()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            bool offline = true;
            sim.Intercept = a => offline && a.Length > 3 && a[2] == "pip" && a[3] == "install"
                ? new ProcessResult(1, "WARNING: Retrying (Retry(total=4)) after connection broken by 'NewConnectionError(Failed to establish a new connection: [Errno 11001] getaddrinfo failed)'\nERROR: Could not find a version that satisfies the requirement pandas==2.3.3 (from versions: none)\nERROR: No matching distribution found for pandas==2.3.3\n")
                : null;

            var failed = await env.InstallAsync(new[] { "core" }, null, default);
            Assert.False(failed.Ok);
            Assert.Equal(AnalysisFailure.Offline, failed.Failure);          // "No matching distribution"가 같이 있어도 네트워크가 먼저다
            Assert.Contains("internet", AnalysisMessages.Describe(failed, false));
            Assert.False(File.Exists(env.MarkerPath));
            Assert.NotEqual(AnalysisEnvState.Ready, env.Inspect().State);

            offline = false;
            var retry = await env.InstallAsync(new[] { "core" }, null, default);
            Assert.True(retry.Ok, retry.Message);
            Assert.Equal(AnalysisEnvState.Ready, env.Inspect().State);
        }

        [Fact]
        public async Task A_package_without_a_wheel_is_reported_with_its_name_and_no_source_build_is_attempted()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            sim.Intercept = a => a.Length > 3 && a[2] == "pip" && a[3] == "install"
                ? new ProcessResult(1, "ERROR: Could not find a version that satisfies the requirement ecos (from scikit-survival) (from versions: none)\nERROR: No matching distribution found for ecos\n")
                : null;
            var r = await env.InstallAsync(new[] { "clinical" }, null, default);
            Assert.Equal(AnalysisFailure.NoWheel, r.Failure);
            Assert.Equal("ecos", r.Package);
            Assert.Contains("ecos", AnalysisMessages.Describe(r, true));
        }

        [Fact]
        public async Task Import_failure_after_install_is_reported_and_the_group_is_not_recorded()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            sim.Intercept = a => a.Length > 2 && a[1] == "-c" && a[2].Contains("importlib") ? new ProcessResult(1, "IMPORT-FAILED xgboost: OSError: DLL load failed\n") : null;
            var r = await env.InstallAsync(new[] { "ml" }, null, default);
            Assert.Equal(AnalysisFailure.VerifyFailed, r.Failure);
            Assert.Contains("xgboost", r.Message);
            Assert.Contains("Visual C++", r.Message);
            Assert.False(File.Exists(env.MarkerPath));
        }

        [Fact]
        public async Task Cancelling_returns_cancelled_and_clears_the_busy_state()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            using var cts = new CancellationTokenSource();
            sim.Intercept = a =>
            {
                if (a.Length > 3 && a[2] == "pip" && a[3] == "install") { cts.Cancel(); return new ProcessResult(-1, "Collecting pandas", "cancelled"); }
                return null;
            };
            var r = await env.InstallAsync(new[] { "core" }, null, cts.Token);
            Assert.Equal(AnalysisFailure.Cancelled, r.Failure);
            Assert.False(env.IsBusy);
            Assert.False(File.Exists(env.MarkerPath));
        }

        [Fact]
        public async Task A_second_operation_while_one_runs_is_refused_as_busy()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            AnalysisResult? inner = null;
            sim.Intercept = a =>
            {
                if (a.Length > 3 && a[2] == "pip" && a[3] == "install")
                    inner = env.RemoveAllAsync(null, default).GetAwaiter().GetResult();
                return null;
            };
            Assert.True((await env.InstallAsync(new[] { "core" }, null, default)).Ok);
            Assert.Equal(AnalysisFailure.Busy, inner!.Failure);
            Assert.Equal(AnalysisEnvState.Ready, env.Inspect().State);     // 안쪽 제거는 아무것도 지우지 않았다
        }

        [Fact]
        public async Task Removing_a_group_uninstalls_only_what_the_remaining_groups_do_not_reach_and_refreshes_the_lock()
        {
            var (env, sim, tmp) = Make();
            using var _ = tmp;
            Assert.True((await env.InstallAsync(new[] { "stats", "clinical" }, null, default)).Ok);
            sim.Orphans = new[] { "lifelines", "autograd" };
            sim.Runner.Calls.Clear();

            var r = await env.RemoveGroupAsync("clinical", null, default);

            Assert.True(r.Ok, r.Message);
            var scan = sim.Runner.Calls.Single(c => c.Args.Length > 1 && c.Args[0] == "-c" && c.Args[1].Contains("ORPHAN"));
            Assert.Contains("pingouin", scan.Args);                      // 남는 묶음의 고정이 뿌리
            Assert.Contains("pandas", scan.Args);
            Assert.DoesNotContain("lifelines", scan.Args);
            var un = sim.Runner.Calls.Single(c => c.Args.Length > 2 && c.Args[2] == "uninstall");
            Assert.Equal(new[] { "lifelines", "autograd" }, un.Args.Where(a => !a.StartsWith('-')).Skip(2));
            Assert.Equal(new[] { "core", "stats" }, env.Inspect().InstalledGroupNames);
            Assert.DoesNotContain("lifelines", File.ReadAllText(env.LockFilePath));
        }

        [Fact]
        public async Task Removing_core_or_everything_deletes_the_environment()
        {
            var (env, _, tmp) = Make();
            using var _t = tmp;
            Assert.True((await env.InstallAsync(new[] { "ml" }, null, default)).Ok);
            var r = await env.RemoveGroupAsync("core", null, default);
            Assert.True(r.Ok);
            Assert.False(Directory.Exists(env.VenvDir));
            Assert.Equal(AnalysisEnvState.Missing, env.Inspect().State);
            Assert.True((await env.RemoveAllAsync(null, default)).Ok);   // 이미 없어도 성공
        }

        [Fact]
        public async Task Base_python_is_found_from_the_python_tools_marker_before_PATH_and_never_from_inside_the_venv()
        {
            using var tmp = new TempFolder();
            string toolsMarker = tmp.Combine("tools.json");
            string basePy = tmp.Combine("py311", "python.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(basePy)!);
            File.WriteAllText(basePy, "");
            File.WriteAllText(toolsMarker, JsonSerializer.Serialize(new { basePython = basePy }));
            string root = tmp.Combine("python-analysis");
            var sim = new Sim(root);
            sim.Intercept = a => a.Length > 2 && a[0] == basePy && a[1] == "-c" && a[2].Contains("sys.executable") ? new ProcessResult(0, basePy + "\n3.11.9\n") : null;
            var env = new AnalysisEnvironment(root, sim.Runner, toolsMarker);

            var found = await env.FindBasePythonAsync(default);

            Assert.True(found.Found);
            Assert.Equal(basePy, found.Interpreter!.Path);
            Assert.DoesNotContain(sim.Runner.Calls, c => c.Exe == "python");        // PATH 탐색까지 가지 않았다

            // 도구 환경이 관리 환경 안의 python을 기반으로 적어 두었다면 건너뛴다.
            File.WriteAllText(toolsMarker, JsonSerializer.Serialize(new { basePython = Path.Combine(root, "venv", "Scripts", "python.exe") }));
            Directory.CreateDirectory(Path.Combine(root, "venv", "Scripts"));
            File.WriteAllText(Path.Combine(root, "venv", "Scripts", "python.exe"), "");
            sim.Runner.Calls.Clear();
            var viaPath = await env.FindBasePythonAsync(default);
            Assert.Equal(BasePython, viaPath.Interpreter!.Path);
            Assert.DoesNotContain(sim.Runner.Calls, c => c.Exe.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Failure_texts_exist_in_both_languages_and_include_detail_only_when_useful()
        {
            foreach (var kind in Enum.GetValues<AnalysisFailure>().Where(k => k != AnalysisFailure.None))
            {
                var r = AnalysisResult.Fail(kind, "detail line", "pkg");
                Assert.False(string.IsNullOrWhiteSpace(AnalysisMessages.Describe(r, false)));
                Assert.False(string.IsNullOrWhiteSpace(AnalysisMessages.Describe(r, true)));
            }
            Assert.DoesNotContain("detail line", AnalysisMessages.Describe(AnalysisResult.Fail(AnalysisFailure.Offline, "detail line"), false));
            Assert.Contains("detail line", AnalysisMessages.Describe(AnalysisResult.Fail(AnalysisFailure.PipFailed, "detail line"), false));
        }

        [Fact]
        public void Disk_size_sums_the_venv_files()
        {
            var (env, _, tmp) = Make();
            using var _ = tmp;
            Assert.Equal(0, env.GetSizeBytes());
            Directory.CreateDirectory(Path.Combine(env.VenvDir, "Lib"));
            File.WriteAllBytes(Path.Combine(env.VenvDir, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(env.VenvDir, "Lib", "b.bin"), new byte[234]);
            Assert.Equal(1234, env.GetSizeBytes());
            Assert.Equal("1 KB", AnalysisMessages.FormatSize(1234));
            Assert.Equal("5 MB", AnalysisMessages.FormatSize(5L << 20));
            Assert.Equal("1.5 GB", AnalysisMessages.FormatSize(3L << 29));
        }
    }

    public class AnalysisAgentIntegrationTests
    {
        private static AnalysisEnvInfo Managed(string python = @"C:\env\venv\Scripts\python.exe", params string[] groups)
        {
            var py = new Version(3, 11, 9);
            var list = AnalysisGroups.All.Select(g => new AnalysisGroupStatus(g,
                groups.Contains(g.Name) ? AnalysisGroupState.Installed : AnalysisGroupState.NotInstalled,
                groups.Contains(g.Name) ? g.PinsFor(py).ToDictionary(p => p.Name, p => p.Version) : new Dictionary<string, string>())).ToList();
            return new AnalysisEnvInfo(AnalysisEnvState.Ready, @"C:\env", python, py, @"C:\Python311\python.exe", list, new Dictionary<string, string>());
        }

        [Fact]
        public void Host_config_names_the_interpreter_only_when_given_and_escapes_it()
        {
            Assert.Equal(OmpLaunch.HostConfigJson(AgentApprovalPolicy.Default), OmpLaunch.HostConfigJson(AgentApprovalPolicy.Default, null));
            using var plain = JsonDocument.Parse(OmpLaunch.HostConfigJson(AgentApprovalPolicy.Default));
            Assert.False(plain.RootElement.TryGetProperty("python", out _));

            string path = @"C:\Users\김민\AppData\Local\NanumCsvViewer\python-analysis\venv\Scripts\python.exe";
            using var doc = JsonDocument.Parse(OmpLaunch.HostConfigJson(AgentApprovalMode.Yolo, path));
            Assert.Equal(path, doc.RootElement.GetProperty("python").GetProperty("interpreter").GetString());
            Assert.Equal("yolo", doc.RootElement.GetProperty("tools").GetProperty("approvalMode").GetString());
        }

        [Fact]
        public void Guide_for_a_managed_env_names_the_interpreter_forbids_pip_and_points_to_ensure_packages()
        {
            string guide = AnalysisMessages.BuildGuide(Managed(groups: new[] { "core", "stats" }), useManaged: true, userPython: null);
            Assert.Contains(@"C:\env\venv\Scripts\python.exe", guide);
            Assert.Contains("app-managed analysis environment", guide);
            Assert.Contains("py.ensure_packages", guide);
            Assert.Contains("Never", guide);
            Assert.Contains("pip install", guide);
            Assert.Contains("pandas 2.3.3", guide);
            Assert.Contains("**ml** ", guide);
            Assert.Contains("not installed", guide);
        }

        [Fact]
        public void Guide_without_a_managed_env_says_so_once_and_still_forbids_self_install()
        {
            string guide = AnalysisMessages.BuildGuide(null, useManaged: true, userPython: @"C:\Python311\python.exe");
            Assert.Contains("No app-managed analysis environment", guide);
            Assert.Contains(@"C:\Python311\python.exe", guide);
            Assert.Contains("Settings", guide);
            Assert.Contains("py.ensure_packages", guide);

            string off = AnalysisMessages.BuildGuide(Managed(groups: new[] { "core" }), useManaged: false, userPython: @"C:\Python311\python.exe");
            Assert.Contains("No app-managed analysis environment is in use", off);
            Assert.DoesNotContain(@"C:\env\venv", off);
        }

        // ---- 컨트롤러: 관리 환경을 쓰면 host.yml의 python.interpreter, 아니면 현재 동작 ----

        private sealed class ManagedSetup : IPythonSetup
        {
            private readonly FakePythonSetup _inner = new() { Ready = FakePythonSetup.ReadyTools };
            public AnalysisEnvInfo? Managed { get; set; }
            public void SetLocate(PythonLocateResult r) => _inner.Locate = r;
            public PythonInterpreter? ConfigPython;
            public Task<PythonLocateResult> LocateAsync(string? ompExe, string workingDirectory, CancellationToken ct) => _inner.LocateAsync(ompExe, workingDirectory, ct);
            public Task<IReadOnlyList<string>> PackagesAsync(PythonInterpreter python, CancellationToken ct) => _inner.PackagesAsync(python, ct);
            public PythonToolsResult? ReadyTools() => ((IPythonSetup)_inner).ReadyTools();
            public Task<PythonToolsResult> EnsureToolsAsync(PythonInterpreter python, Action<string>? progress, CancellationToken ct) => _inner.EnsureToolsAsync(python, progress, ct);
            public PythonLspConfig.WriteResult WriteConfig(string outputFolder, PythonToolsResult tools, PythonInterpreter python) { ConfigPython = python; return _inner.WriteConfig(outputFolder, tools, python); }
            public AnalysisEnvInfo? InspectManaged() => Managed;
        }

        private static AgentHostOptions On(bool useManaged = true, bool noticePending = false) =>
            new(Language: "en", AppVersion: "1.2.3", AllowLocalPython: true, UseManagedPython: useManaged, PythonEnvNoticePending: noticePending);

        private static JsonElement HostConfig(FakeOmpProcess p)
        {
            var args = p.Launch.Arguments.ToList();
            return JsonDocument.Parse(File.ReadAllText(args[args.IndexOf("--config") + 1])).RootElement.Clone();
        }

        private static string Guide(FakeOmpProcess p)
        {
            var args = p.Launch.Arguments.ToList();
            return File.ReadAllText(args[args.IndexOf("--append-system-prompt") + 1]);
        }

        [Fact]
        public async Task A_ready_managed_env_becomes_the_omp_interpreter_the_guide_interpreter_and_the_lsp_python()
        {
            using var tmp = new TempFolder();
            var setup = new ManagedSetup { Managed = Managed(groups: new[] { "core" }) };
            using var rig = new ControllerRig(On(), python: setup, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();

            Assert.Equal(@"C:\env\venv\Scripts\python.exe", HostConfig(rig.Proc).GetProperty("python").GetProperty("interpreter").GetString());
            string guide = Guide(rig.Proc);
            Assert.Contains(@"C:\env\venv\Scripts\python.exe", guide);
            Assert.Contains("app-managed analysis environment", guide);
            Assert.Equal(@"C:\env\venv\Scripts\python.exe", setup.ConfigPython!.Path);       // 진단(pyright)도 같은 인터프리터를 본다
            Assert.Empty(rig.Page.Parsed("notice").Where(n => n.Str("text").Contains("uses your own Python")));
        }

        [Fact]
        public async Task Without_a_managed_env_nothing_changes_and_the_setup_hint_is_shown_exactly_once_with_a_settings_button()
        {
            using var tmp = new TempFolder();
            var setup = new ManagedSetup();
            using var rig = new ControllerRig(On(noticePending: true), python: setup, dataFile: tmp.Combine("a.csv"));
            int shown = 0;
            rig.Controller.PythonEnvNoticeShown += () => shown++;
            await rig.StartAsync();

            Assert.False(HostConfig(rig.Proc).TryGetProperty("python", out _));                // 사용자 Python: 지금까지의 동작
            Assert.Contains(FakePythonSetup.Interpreter.Path, Guide(rig.Proc));
            var notice = rig.Page.Parsed("notice").Single(n => n.Str("text").Contains("uses your own Python"));
            Assert.Equal("nanumcsv://settings/ai", notice.Str("url"));
            Assert.Equal(1, shown);

            await rig.Controller.RestartAsync();
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count >= 2 && rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Single(rig.Page.Parsed("notice"), n => n.Str("text").Contains("uses your own Python"));   // 다시 시작해도 한 번만
            Assert.Equal(1, shown);
        }

        [Fact]
        public async Task With_no_python_at_all_only_the_install_python_notice_is_shown_not_the_managed_env_hint()
        {
            using var tmp = new TempFolder();
            var setup = new ManagedSetup();
            setup.SetLocate(new PythonLocateResult(null, "No Python interpreter was found on PATH."));
            using var rig = new ControllerRig(On(noticePending: true), python: setup, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("text").Contains("no usable Python"));
            Assert.DoesNotContain(rig.Page.Parsed("notice"), n => n.Str("text").Contains("uses your own Python"));
        }

        [Fact]
        public async Task The_toggle_off_ignores_an_existing_managed_env()
        {
            using var tmp = new TempFolder();
            var setup = new ManagedSetup { Managed = Managed(groups: new[] { "core" }) };
            using var rig = new ControllerRig(On(useManaged: false), python: setup, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();
            Assert.False(HostConfig(rig.Proc).TryGetProperty("python", out _));
            Assert.DoesNotContain(@"C:\env\venv", Guide(rig.Proc));
        }

        [Fact]
        public async Task Creating_the_env_while_idle_restarts_the_agent_on_the_same_conversation_to_switch_python()
        {
            using var tmp = new TempFolder();
            var setup = new ManagedSetup();
            using var rig = new ControllerRig(On(), python: setup, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();
            Assert.False(HostConfig(rig.Proc).TryGetProperty("python", out _));

            setup.Managed = Managed(groups: new[] { "core" });
            rig.OnUi(() => rig.Controller.Options = On());          // 설정 창이 설치 뒤 알리는 것과 같다
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count >= 2);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Equal(@"C:\env\venv\Scripts\python.exe", HostConfig(rig.Factory.Processes[1]).GetProperty("python").GetProperty("interpreter").GetString());
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("text").Contains("managed environment"));
        }

        // ---- py.ensure_packages ----

        private class StubHost : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                targetMethod?.Name == "get_AnalysisFolder" ? null : throw new NotSupportedException(targetMethod?.Name);
        }

        private sealed class Approvals(bool answer) : IAgentApprovals
        {
            public List<(string Target, string Summary, IReadOnlyList<string> Lines, ApprovalKind Kind)> Asked { get; } = new();
            public Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation, ApprovalKind kind = ApprovalKind.RowSharing)
            {
                Asked.Add((target, summary, lines, kind));
                return Task.FromResult(answer);
            }
        }

        private static (CsvHostTools Tools, AnalysisEnvironment Env, FakeRunner Runner, TempFolder Tmp) ToolRig(bool allowPython = true, bool useManaged = true)
        {
            var tmp = new TempFolder();
            string root = tmp.Combine("python-analysis");
            var sim = new AnalysisSimForTools(root);
            var env = new AnalysisEnvironment(root, sim.Runner);
            var host = DispatchProxy.Create<ICsvAgentHost, StubHost>();
            var tools = new CsvHostTools(host, () => new AgentHostOptions(Language: "en", AllowLocalPython: allowPython, UseManagedPython: useManaged)) { PythonEnvironment = env };
            return (tools, env, sim.Runner, tmp);
        }

        private static Task<HostToolResult> Call(CsvHostTools tools, string json, IAgentApprovals approvals) =>
            tools.ExecuteAsync(new HostToolCall("h1", "t1", "py.ensure_packages", JsonDocument.Parse(json).RootElement.Clone()), approvals, default);

        [Fact]
        public async Task Ensure_packages_asks_with_a_file_save_card_installs_after_approval_and_returns_the_interpreter()
        {
            var (tools, env, runner, tmp) = ToolRig();
            using var _ = tmp;
            var approvals = new Approvals(true);
            var notices = new List<string>();
            tools.Notice += (_, text) => notices.Add(text);

            var r = await Call(tools, """{"groups":["stats"],"reason":"Need pingouin for ICC"}""", approvals);

            Assert.False(r.IsError, r.Text);
            var ask = Assert.Single(approvals.Asked);
            Assert.Equal(ApprovalKind.FileSave, ask.Kind);                       // 승인 모드를 따른다(yolo에서만 자동)
            Assert.Contains("core", ask.Summary);
            Assert.Contains("stats", ask.Summary);
            Assert.Contains(ask.Lines, l => l.StartsWith("+ stats:") && l.Contains("pingouin 0.5.5"));
            Assert.Contains(ask.Lines, l => l.Contains("Need pingouin for ICC"));
            Assert.Contains(ask.Lines, l => l.Contains("Disk: about " + (AnalysisGroups.Get("core")!.ApproxMb + AnalysisGroups.Get("stats")!.ApproxMb) + " MB"));
            Assert.Contains(ask.Lines, l => l.Contains("wheels only", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(env.PythonPath.Replace("\\", "\\\\"), r.Text);
            Assert.Contains("\"pingouin\":\"0.5.5\"", r.Text);
            Assert.Equal(new[] { "core", "stats" }, env.Inspect().InstalledGroupNames);
            Assert.Contains(runner.Calls, c => c.Args.Length > 2 && c.Args[2] == "install" && c.Args.Contains("--only-binary=:all:"));
            Assert.Contains(notices, t => t.Contains("downloading"));
        }

        [Fact]
        public async Task Ensure_packages_without_approval_installs_nothing_and_tells_the_model_to_carry_on()
        {
            var (tools, env, runner, tmp) = ToolRig();
            using var _ = tmp;
            var r = await Call(tools, """{"groups":["ml"]}""", new Approvals(false));
            Assert.True(r.IsError);
            Assert.Contains("did not approve", r.Text);
            Assert.Empty(runner.Calls);
            Assert.Equal(AnalysisEnvState.Missing, env.Inspect().State);
        }

        [Fact]
        public async Task Ensure_packages_when_everything_is_installed_asks_nothing()
        {
            var (tools, env, runner, tmp) = ToolRig();
            using var _ = tmp;
            Assert.True((await env.InstallAsync(new[] { "core" }, null, default)).Ok);
            runner.Calls.Clear();
            var approvals = new Approvals(true);
            var r = await Call(tools, """{"groups":["core"]}""", approvals);
            Assert.False(r.IsError, r.Text);
            Assert.Contains("Already installed", r.Text);
            Assert.Empty(approvals.Asked);
            Assert.Empty(runner.Calls);
        }

        [Theory]
        [InlineData(false, true, "turned off")]
        [InlineData(true, false, "Use the managed Python environment")]
        public async Task Ensure_packages_is_refused_when_local_python_or_the_managed_env_is_off(bool allowPython, bool useManaged, string fragment)
        {
            var (tools, _, runner, tmp) = ToolRig(allowPython, useManaged);
            using var _t = tmp;
            var approvals = new Approvals(true);
            var r = await Call(tools, """{"groups":["core"]}""", approvals);
            Assert.True(r.IsError);
            Assert.Contains(fragment, r.Text);
            Assert.Empty(approvals.Asked);
            Assert.Empty(runner.Calls);
        }

        [Fact]
        public async Task Ensure_packages_rejects_unknown_groups_and_explains_missing_python_and_offline_failures()
        {
            var (tools, _, runner, tmp) = ToolRig();
            using var _ = tmp;
            var bad = await Call(tools, """{"groups":["tensorflow"]}""", new Approvals(true));
            Assert.True(bad.IsError);
            Assert.Contains("Valid groups", bad.Text);
            Assert.Empty(runner.Calls);

            runner.OnRun = (exe, args) => args.Length > 0 && args[0] == "-c" ? new ProcessResult(-1, "", "not found") : new ProcessResult(0, "");
            var noPython = await Call(tools, """{"groups":["core"]}""", new Approvals(true));
            Assert.True(noPython.IsError);
            Assert.Contains("python.org", noPython.Text);
        }

        [Fact]
        public void The_tool_is_registered_with_a_strict_schema_and_a_group_enum()
        {
            var def = Assert.Single(ToolDefinitions.All, d => d.Name == "py.ensure_packages");
            using var schema = JsonDocument.Parse(def.ParametersSchema);
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(AnalysisGroups.Names, schema.RootElement.GetProperty("properties").GetProperty("groups").GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!));
        }
    }

    /// <summary>py.ensure_packages 테스트용: 설치·freeze에 성공하는 가짜 실행기(venv 만들기 포함).</summary>
    internal sealed class AnalysisSimForTools
    {
        public FakeRunner Runner { get; } = new();

        public AnalysisSimForTools(string root)
        {
            var installed = new HashSet<string>();
            Runner.OnRun = (exe, args) =>
            {
                if (args.Length >= 2 && args[0] == "-c" && args[1].Contains("sys.executable")) return new ProcessResult(0, @"C:\Python312\python.exe" + "\n3.12.4\n");
                if (args.Length >= 2 && args[0] == "-c" && args[1].Contains("sysconfig")) return new ProcessResult(0, "win-amd64\n");
                if (args.Length >= 3 && args[1] == "venv")
                {
                    Directory.CreateDirectory(Path.Combine(args[2], "Scripts"));
                    File.WriteAllText(Path.Combine(args[2], "Scripts", "python.exe"), "");
                    File.WriteAllText(Path.Combine(args[2], "pyvenv.cfg"), "home = C:\\Python312\ninclude-system-site-packages = false\n");
                    return new ProcessResult(0, "");
                }
                if (args.Length > 2 && args[1] == "pip" && args[2] == "install")
                {
                    foreach (string spec in args.Skip(3).Where(a => !a.StartsWith('-'))) installed.Add(spec.Split(';')[0].Trim());
                    return new ProcessResult(0, "Collecting x\n");
                }
                if (args.Length > 2 && args[1] == "pip" && args[2] == "freeze") return new ProcessResult(0, string.Join("\n", installed) + "\n");
                return new ProcessResult(0, "");
            };
        }
    }

    public class AnalysisBundleTests
    {
        private static string Seed(TempFolder tmp)
        {
            string folder = tmp.Combine("trial_분석결과");
            Directory.CreateDirectory(Path.Combine(folder, "data"));
            Directory.CreateDirectory(Path.Combine(folder, ".omp"));
            Directory.CreateDirectory(Path.Combine(folder, "__pycache__"));
            Directory.CreateDirectory(Path.Combine(folder, "figs"));
            File.WriteAllText(Path.Combine(folder, "analysis.py"), "print('hi')");
            File.WriteAllText(Path.Combine(folder, "figs", "plot.py"), "x=1");
            File.WriteAllText(Path.Combine(folder, "report.md"), "# r");
            File.WriteAllText(Path.Combine(folder, "fig1.png"), "png");
            File.WriteAllText(Path.Combine(folder, "figs", "roc.svg"), "<svg/>");
            File.WriteAllText(Path.Combine(folder, "summary_table.csv"), "id,v\n1,2\n");
            File.WriteAllText(Path.Combine(folder, "data", "trial.csv"), "id,age\n7,50\n");
            File.WriteAllText(Path.Combine(folder, "data", "trial.schema.json"), """{"columns":[{"name":"id","type":"Integer"},{"name":"age","type":"Integer"}],"filters":[{"text":"name == 'Kim'"}]}""");
            File.WriteAllText(Path.Combine(folder, ".omp", "lsp.json"), "{}");
            File.WriteAllText(Path.Combine(folder, "__pycache__", "analysis.cpython-310.pyc"), "x");
            File.WriteAllText(Path.Combine(folder, "pyproject.toml"), "[tool]");
            return folder;
        }

        private static readonly DateTime Now = new(2026, 10, 5, 3, 4, 5, DateTimeKind.Utc);

        [Fact]
        public void Folder_bundle_has_scripts_reports_figures_lock_and_readme_but_no_data_by_default()
        {
            using var tmp = new TempFolder();
            string folder = Seed(tmp);
            string dest = tmp.Combine("out", "bundle");

            var result = AnalysisBundle.Export(new AnalysisBundleRequest(folder, dest, false, "# lock\npandas==2.3.3\n", "3.10.6", "3.2.0", Now));

            var files = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dest, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "README.md", "analysis.py", "fig1.png", "figs/plot.py", "figs/roc.svg", "report.md", "requirements.lock" }, files);
            Assert.False(result.IsZip);
            Assert.Equal(2, result.Scripts);
            Assert.Equal(1, result.Reports);
            Assert.Equal(2, result.Figures);
            Assert.Equal(0, result.DataFiles);
            Assert.Equal(3, result.ExcludedData);                                   // data\trial.csv + schema, 표 형식 결과(summary_table.csv)
            Assert.Equal("# lock\npandas==2.3.3\n", File.ReadAllText(Path.Combine(dest, "requirements.lock")));
            Assert.DoesNotContain(files, f => f.Contains(".omp") || f.Contains("pycache") || f.EndsWith(".csv") || f.EndsWith("pyproject.toml"));
            Assert.Equal(files, result.Files.Select(f => f.Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal));
        }

        [Fact]
        public void Readme_explains_recreating_rerunning_and_lists_expected_data_with_columns_but_no_values()
        {
            using var tmp = new TempFolder();
            string folder = Seed(tmp);
            string dest = tmp.Combine("bundle");
            AnalysisBundle.Export(new AnalysisBundleRequest(folder, dest, false, "pandas==2.3.3\n", "3.10.6", "3.2.0", Now));
            string readme = File.ReadAllText(Path.Combine(dest, "README.md"));

            Assert.Contains("py -3.10 -m venv .venv", readme);
            Assert.Contains("--only-binary=:all: -r requirements.lock", readme);
            Assert.Contains(@".venv\Scripts\python analysis.py", readme);
            Assert.Contains(@".venv\Scripts\python figs\plot.py", readme);
            Assert.Contains("`data/trial.csv`", readme);
            Assert.Contains("id (Integer), age (Integer)", readme);
            Assert.Contains("**not** included", readme);
            Assert.Contains("3.2.0", readme);
            Assert.DoesNotContain("50", readme.Replace("3.2.0", ""));               // 데이터 값
            Assert.DoesNotContain("Kim", readme);                                    // schema의 필터 문구(값이 섞일 수 있다)도 옮기지 않는다
        }

        [Fact]
        public void Without_a_lock_the_bundle_says_the_versions_are_unknown()
        {
            using var tmp = new TempFolder();
            string folder = Seed(tmp);
            string dest = tmp.Combine("b");
            var result = AnalysisBundle.Export(new AnalysisBundleRequest(folder, dest, false, null, null, "3.2.0", Now));
            Assert.False(File.Exists(Path.Combine(dest, "requirements.lock")));
            Assert.DoesNotContain("requirements.lock", result.Files);
            Assert.Contains("no `requirements.lock`", File.ReadAllText(Path.Combine(dest, "README.md")));
        }

        [Fact]
        public void Including_data_copies_the_data_folder_and_table_files_and_the_readme_warns()
        {
            using var tmp = new TempFolder();
            string folder = Seed(tmp);
            string dest = tmp.Combine("withdata");
            var result = AnalysisBundle.Export(new AnalysisBundleRequest(folder, dest, true, "x==1\n", "3.12.1", "3.2.0", Now));

            Assert.True(File.Exists(Path.Combine(dest, "data", "trial.csv")));
            Assert.True(File.Exists(Path.Combine(dest, "data", "trial.schema.json")));
            Assert.True(File.Exists(Path.Combine(dest, "summary_table.csv")));
            Assert.False(File.Exists(Path.Combine(dest, ".omp", "lsp.json")));
            Assert.Equal(0, result.ExcludedData);
            Assert.Equal(3, result.DataFiles);
            string readme = File.ReadAllText(Path.Combine(dest, "README.md"));
            Assert.Contains("includes the data files", readme);
            Assert.Contains("sensitive", readme);
            Assert.Contains("py -3.12 -m venv .venv", readme);
        }

        [Fact]
        public void Zip_bundle_uses_forward_slashes_and_the_same_contents()
        {
            using var tmp = new TempFolder();
            string folder = Seed(tmp);
            string zip = tmp.Combine("sub", "bundle.zip");

            var result = AnalysisBundle.Export(new AnalysisBundleRequest(folder, zip, false, "x==1\n", "3.10.6", "3.2.0", Now));

            Assert.True(result.IsZip);
            using var za = ZipFile.OpenRead(zip);
            Assert.Equal(new[] { "README.md", "analysis.py", "fig1.png", "figs/plot.py", "figs/roc.svg", "report.md", "requirements.lock" }, za.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
            using var r = new StreamReader(za.GetEntry("analysis.py")!.Open());
            Assert.Equal("print('hi')", r.ReadToEnd());
            Assert.False(File.Exists(zip + ".tmp"));
        }

        [Fact]
        public void Export_of_a_missing_folder_throws_and_default_name_comes_from_the_folder()
        {
            Assert.Throws<DirectoryNotFoundException>(() => AnalysisBundle.Export(new AnalysisBundleRequest(@"Z:\nope\x", @"Z:\out", false, null, null, "1", Now)));
            Assert.Equal("trial_분석결과_python_bundle", AnalysisBundle.DefaultName(@"C:\data\trial_분석결과\"));
        }

        [Fact]
        public void The_dialog_refuses_a_destination_inside_the_analysis_folder()
        {
            Assert.True(PythonBundleDialog.IsInside(@"C:\a\res\bundle", @"C:\a\res"));
            Assert.True(PythonBundleDialog.IsInside(@"C:\a\RES", @"C:\a\res\"));
            Assert.False(PythonBundleDialog.IsInside(@"C:\a\res2\bundle", @"C:\a\res"));
        }
    }
}
