using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Python;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    /// <summary>분석 스킬 묶음: manifest 무결성, 네트워크 호출 없음, 풀기(멱등·정리), 덧씌우기, 구성별 선택, 가이드 문구, 컨트롤러 연동.</summary>
    // 실제 Form1을 만들고 UI 언어(앱 전역 상태)를 바꾸므로 다른 Form1 테스트와 직렬화한다.
    [Collection("SavedViewStore")]
    public class SkillPackTests
    {
        private static readonly Assembly App = typeof(SkillPack).Assembly;

        private static string ResourceText(string name)
        {
            using var s = App.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }

        /// <summary>마크다운 줄바꿈을 공백 하나로(문구가 줄 중간에서 접혀도 찾는다).</summary>
        private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

        private static (string? Name, string? Description) Frontmatter(string skillMd)
        {
            var m = Regex.Match(skillMd, @"\A---\r?\n(?<fm>.*?)\r?\n---", RegexOptions.Singleline);
            if (!m.Success) return (null, null);
            string fm = m.Groups["fm"].Value;
            string? Get(string key)
            {
                var line = Regex.Match(fm, @"^" + key + @":\s*(?<v>.+)$", RegexOptions.Multiline);
                return line.Success ? line.Groups["v"].Value.Trim().Trim('"') : null;
            }
            return (Get("name"), Get("description"));
        }

        private static IReadOnlyList<string> EmbeddedSkillFolders() =>
            SkillPack.ResourceNames().Select(n => n[SkillPack.ResourcePrefix.Length..]).Where(r => r.Contains('/')).Select(r => r[..r.IndexOf('/')]).Distinct().OrderBy(x => x).ToList();

        // ---- manifest -------------------------------------------------------------------------------------------

        [Fact]
        public void Manifest_lists_a_curated_set_whose_folders_all_exist_and_are_all_listed()
        {
            var m = SkillPack.Manifest;
            Assert.InRange(m.Skills.Count, 18, 28);
            Assert.Equal(m.Skills.Select(s => s.Name).OrderBy(x => x), EmbeddedSkillFolders());            // 목록 밖 폴더도, 폴더 없는 항목도 없다
            Assert.Equal(m.Skills.Count, m.Skills.Select(s => s.Name).Distinct().Count());

            var categories = m.Categories.Select(c => c.Id).ToHashSet();
            Assert.Superset(new HashSet<string> { "app", "clinical", "stats", "ml" }, categories);
            foreach (var s in m.Skills)
            {
                Assert.Contains(s.Category, categories);
                Assert.False(string.IsNullOrWhiteSpace(s.Reason), s.Name + " has no recorded reason");
                Assert.False(string.IsNullOrWhiteSpace(s.SummaryEn) || string.IsNullOrWhiteSpace(s.SummaryKo), s.Name + " needs both summaries");
                Assert.InRange(s.Tokens, 1, 200);
                Assert.NotNull(s.PythonDeps);
            }
            foreach (var cat in new[] { "clinical", "stats", "ml" }) Assert.True(m.Count(cat) >= 3, cat);
            Assert.Single(m.Skills, s => s.IsApp);
            Assert.Equal(SkillPack.AppSkill, m.Skills.Single(s => s.IsApp).Name);
            Assert.All(m.Rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
            Assert.Contains(m.Rejected, r => r.Name == "database-lookup");
            Assert.DoesNotContain(m.Skills, s => m.Rejected.Any(r => r.Name == s.Name));
        }

        [Fact]
        public void Manifest_pins_the_upstream_release()
        {
            var m = SkillPack.Manifest;
            Assert.Equal("K-Dense-AI/scientific-agent-skills", m.UpstreamName);
            Assert.Matches("^v\\d+\\.\\d+\\.\\d+$", m.UpstreamTag);
            Assert.Matches("^[0-9a-f]{40}$", m.UpstreamCommit);
            Assert.Equal("MIT", m.UpstreamLicense);
        }

        [Fact]
        public void Every_skill_has_a_SKILL_md_with_matching_name_and_a_description()
        {
            foreach (var s in SkillPack.Manifest.Skills)
            {
                var (name, description) = Frontmatter(ResourceText($"Skills/{s.Name}/SKILL.md"));
                Assert.Equal(s.Name, name);
                Assert.False(string.IsNullOrWhiteSpace(description), s.Name + " has no description (omp requires one for customDirectories)");
            }
        }

        [Fact]
        public void Skill_scripts_contain_no_network_or_process_calls()
        {
            // 단순 검사: 가져오는 네트워크·프로세스 모듈, URL 열기, 외부 서비스 호출. (URL 문자열 자체는 정적 참고 자료로 허용)
            var forbidden = new Regex(
                @"^\s*(import|from)\s+(requests|urllib\.request|urllib3|httpx|aiohttp|http\.client|socket|ftplib|smtplib|telnetlib|webbrowser|paramiko|boto3|subprocess)\b" +
                @"|\burlopen\s*\(|\bos\.system\s*\(|\bsubprocess\.|\brequests\.(get|post|put|delete)\s*\(|\bsns\.load_dataset\s*\(|\bfetch_openml\s*\(",
                RegexOptions.Multiline);
            var scanned = 0;
            foreach (string name in SkillPack.ResourceNames().Where(n => n.EndsWith(".py", StringComparison.Ordinal)))
            {
                scanned++;
                var m = forbidden.Match(ResourceText(name));
                Assert.False(m.Success, $"{name}: {m.Value.Trim()}");
            }
            Assert.True(scanned >= 30, "expected the bundled helper scripts to be scanned");
        }

        [Fact]
        public void Shipped_skill_files_do_not_carry_credentials_or_third_party_upload_endpoints()
        {
            var bad = new Regex(@"OPENROUTER_API_KEY|api\.openai\.com|sk-[A-Za-z0-9]{20,}|openrouter\.ai/api", RegexOptions.IgnoreCase);
            foreach (string name in SkillPack.ResourceNames().Where(n => !n.EndsWith(".png", StringComparison.Ordinal)))
            {
                var m = bad.Match(ResourceText(name));
                Assert.False(m.Success, $"{name}: {m.Value}");
            }
        }

        // ---- 라이선스 고지 ---------------------------------------------------------------------------------------

        [Fact]
        public void The_MIT_notice_and_attribution_ship_with_the_pack_and_the_app_notice()
        {
            string license = ResourceText("Skills/" + SkillPack.Manifest.LicenseFile);
            Assert.Contains("MIT License", license);
            Assert.Contains("K-Dense Inc.", license);
            Assert.Contains("Permission is hereby granted", license);

            string notices = ResourceText("Skills/THIRD_PARTY_NOTICES.md");
            Assert.Contains("K-Dense-AI/scientific-agent-skills", notices);
            Assert.Contains(SkillPack.Manifest.UpstreamCommit, notices);
            Assert.Contains(SkillPack.Manifest.UpstreamTag, notices);

            string appNotice = ResourceText("ChatAssets/NOTICE.txt");
            Assert.Contains("K-Dense-AI/scientific-agent-skills", appNotice);
            Assert.Contains("MIT License", appNotice);
            Assert.Contains("K-Dense Inc.", appNotice);
        }

        [Fact]
        public void The_extracted_folder_keeps_the_license_notice_manifest_and_only_the_chosen_skills()
        {
            using var root = new TempFolder();
            var launch = SkillPack.Prepare(SkillSelection.Create(true, new[] { "ml", "stats" }, null), root.Path)!;
            Assert.True(File.Exists(Path.Combine(launch.Directory, SkillPack.Manifest.LicenseFile)));
            Assert.True(File.Exists(Path.Combine(launch.Directory, "THIRD_PARTY_NOTICES.md")));
            Assert.True(File.Exists(Path.Combine(launch.Directory, SkillPack.ManifestFile)));
            var folders = Directory.GetDirectories(launch.Directory).Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(launch.Names.OrderBy(x => x), folders);
            Assert.All(folders, f => Assert.True(File.Exists(Path.Combine(launch.Directory, f!, "SKILL.md"))));
        }

        // ---- 구성 ------------------------------------------------------------------------------------------------

        [Fact]
        public void The_default_selection_loads_everything_and_the_app_skill_is_always_with_an_enabled_pack()
        {
            var all = SkillPack.Resolve(SkillSelection.Default);
            Assert.Equal(SkillPack.Manifest.Skills.Count, all.Count);

            Assert.Empty(SkillPack.Resolve(new SkillSelection(Enabled: false)));
            Assert.Equal("", SkillPack.KeyFor(new SkillSelection(Enabled: false)));

            var onlyApp = SkillPack.Resolve(SkillSelection.Create(true, new[] { "clinical", "stats", "ml" }, null));
            Assert.Equal(new[] { SkillPack.AppSkill }, onlyApp.Select(s => s.Name));
            var cannotDisableApp = SkillPack.Resolve(SkillSelection.Create(true, null, new[] { SkillPack.AppSkill }));
            Assert.Contains(cannotDisableApp, s => s.IsApp);
        }

        [Theory]
        [InlineData(true, "", "", true, true, true)]
        [InlineData(true, "clinical", "", false, true, true)]
        [InlineData(true, "stats,ml", "", true, false, false)]
        [InlineData(true, "", "shap,pkpd-modeling", true, true, true)]
        [InlineData(false, "", "", false, false, false)]
        public void Categories_follow_the_toggles(bool enabled, string categoriesOff, string skillsOff, bool clinical, bool stats, bool ml)
        {
            var sel = SkillSelection.Create(enabled, categoriesOff.Split(',', StringSplitOptions.RemoveEmptyEntries), skillsOff.Split(',', StringSplitOptions.RemoveEmptyEntries));
            var names = SkillPack.Resolve(sel).Select(s => s.Name).ToHashSet();
            Assert.Equal(enabled, names.Contains(SkillPack.AppSkill));
            Assert.Equal(clinical && enabled, names.Contains("clinical-reports"));
            Assert.Equal(stats && enabled, names.Contains("statsmodels"));
            Assert.Equal(ml && enabled, names.Contains("scikit-learn"));
            foreach (string off in skillsOff.Split(',', StringSplitOptions.RemoveEmptyEntries)) Assert.DoesNotContain(off, names);
        }

        [Fact]
        public void The_selection_key_changes_with_the_loaded_skills_and_is_stable_otherwise()
        {
            string full = SkillPack.KeyFor(SkillSelection.Default);
            Assert.NotEqual("", full);
            Assert.Equal(full, SkillPack.KeyFor(SkillSelection.Create(true, Array.Empty<string>(), Array.Empty<string>())));
            Assert.NotEqual(full, SkillPack.KeyFor(SkillSelection.Create(true, null, new[] { "shap" })));
            Assert.Equal(SkillPack.KeyFor(SkillSelection.Create(true, null, new[] { "shap" })), SkillPack.KeyFor(SkillSelection.Create(true, null, new[] { " SHAP " })));
            // 이미 꺼진 분류의 스킬을 또 끈다고 키가 달라지지 않는다(실리는 목록이 같다).
            Assert.Equal(SkillPack.KeyFor(SkillSelection.Create(true, new[] { "ml" }, null)),
                         SkillPack.KeyFor(SkillSelection.Create(true, new[] { "ml" }, new[] { "shap" })));
        }

        [Fact]
        public void Checks_round_trip_through_settings()
        {
            var m = SkillPack.Manifest;
            var sel = SkillSelection.FromChecks(true, m, s => s.Name is not ("shap" or "aeon") && s.Category != "clinical");
            Assert.Equal("clinical", sel.CategoriesOff);
            Assert.Contains("shap", sel.SkillsOff.Split(','));
            Assert.Contains("aeon", sel.SkillsOff.Split(','));

            var settings = new AppSettings { AgentSkillsEnabled = true, AgentSkillCategoriesOff = sel.CategoriesOff, AgentSkillsOff = sel.SkillsOff };
            var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            Assert.Equal(sel, SkillSelection.FromSettings(back));
            Assert.DoesNotContain(SkillPack.Resolve(sel), s => s.Category == "clinical" || s.Name is "shap" or "aeon");
        }

        [Fact]
        public void Settings_default_to_the_pack_on_and_old_settings_files_keep_it_on()
        {
            var d = new AppSettings();
            Assert.True(d.AgentSkillsEnabled);
            Assert.Equal("", d.AgentSkillCategoriesOff);
            Assert.Equal("", d.AgentSkillsOff);
            var legacy = JsonSerializer.Deserialize<AppSettings>("{\"AgentAllowLocalPython\":true}")!;
            Assert.True(legacy.AgentSkillsEnabled);

            var opts = new AgentHostOptions(SkillsEnabled: false, SkillCategoriesOff: "ml", SkillsOff: "shap");
            Assert.False(opts.Skills.Enabled);
            Assert.True(opts.Skills.CategoryOff("ml"));
            Assert.True(opts.Skills.SkillOff("shap"));
        }

        // ---- 풀기 ------------------------------------------------------------------------------------------------

        [Fact]
        public void Extraction_writes_only_enabled_skills_and_is_idempotent()
        {
            using var root = new TempFolder();
            var sel = SkillSelection.Create(true, new[] { "ml" }, new[] { "dask" });
            var first = SkillPack.Prepare(sel, root.Path)!;

            Assert.StartsWith(Path.Combine(root.Path, SkillPack.ContentHash()), first.Directory);
            Assert.True(File.Exists(Path.Combine(first.Directory, "statsmodels", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(first.Directory, SkillPack.AppSkill, "assets", "analysis_template.py")));
            Assert.False(Directory.Exists(Path.Combine(first.Directory, "scikit-learn")));          // 끈 분류
            Assert.False(Directory.Exists(Path.Combine(first.Directory, "dask")));                  // 끈 스킬
            Assert.Equal(SkillPack.KeyFor(sel), first.Key);
            Assert.Equal(first.Names.Count, Directory.GetDirectories(first.Directory).Length);

            string probe = Path.Combine(first.Directory, "statsmodels", "SKILL.md");
            var stamp = File.GetLastWriteTimeUtc(probe);
            File.SetLastWriteTimeUtc(probe, stamp.AddHours(-5));                                    // 다시 쓰면 시각이 바뀐다
            var second = SkillPack.Prepare(sel, root.Path)!;
            Assert.Equal(first.Directory, second.Directory);
            Assert.Equal(stamp.AddHours(-5), File.GetLastWriteTimeUtc(probe));
            Assert.Empty(Directory.GetDirectories(Path.Combine(root.Path, SkillPack.ContentHash()), "*.tmp-*"));
        }

        [Fact]
        public void Nothing_is_extracted_when_nothing_is_enabled()
        {
            using var root = new TempFolder();
            Assert.Null(SkillPack.Prepare(new SkillSelection(Enabled: false), root.Path));
            Assert.Empty(Directory.GetFileSystemEntries(root.Path));
        }

        [Fact]
        public void Old_versions_and_stale_selections_are_cleaned_up_but_the_folder_in_use_stays()
        {
            using var root = new TempFolder();
            string oldVersion = Path.Combine(root.Path, "0123456789ab");
            Directory.CreateDirectory(Path.Combine(oldVersion, "sel-x", "shap"));
            File.WriteAllText(Path.Combine(oldVersion, "sel-x", "shap", "SKILL.md"), "x");

            string version = Path.Combine(root.Path, SkillPack.ContentHash());
            Directory.CreateDirectory(version);
            var stale = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                string d = Path.Combine(version, "sel-old" + i);
                Directory.CreateDirectory(d);
                Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddDays(-10 + i));
                stale.Add(d);
            }
            string tmp = Path.Combine(version, "sel-zzz.tmp-abc");
            Directory.CreateDirectory(tmp);
            Directory.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddDays(-3));

            var launch = SkillPack.Prepare(SkillSelection.Default, root.Path)!;

            Assert.False(Directory.Exists(oldVersion));
            Assert.False(Directory.Exists(tmp));
            Assert.True(Directory.Exists(launch.Directory));
            var sels = Directory.GetDirectories(version);
            Assert.Equal(SkillPack.KeepSelections, sels.Length);
            Assert.Contains(sels, s => s == launch.Directory);
            Assert.True(Directory.Exists(stale[5]));                                                // 가장 최근 것들만 남는다
            Assert.False(Directory.Exists(stale[0]));
        }

        // ---- 덧씌우기·인자 ----------------------------------------------------------------------------------------

        [Fact]
        public void The_overlay_only_sets_custom_directories_and_keeps_the_users_own_directories_first()
        {
            string json = SkillPack.OverlayJson(@"C:\skills\abc\sel-1", new[] { @"D:\mine", @"d:\MINE", "" });
            using var doc = JsonDocument.Parse(json);
            var skills = doc.RootElement.GetProperty("skills");
            // includeSkills/ignoredSkills는 쓰지 않는다: omp 덧씌우기는 배열을 통째로 대체해 사용자의 다른 스킬과 무시 목록을 바꿔 버린다.
            Assert.Equal(new[] { "customDirectories" }, skills.EnumerateObject().Select(p => p.Name));
            Assert.Equal(new[] { @"D:\mine", @"C:\skills\abc\sel-1" }, skills.GetProperty("customDirectories").EnumerateArray().Select(e => e.GetString()));
            Assert.Single(doc.RootElement.EnumerateObject());
        }

        [Theory]
        [InlineData("{\"key\":\"skills.customDirectories\",\"value\":[\"C:/a\",\"D:/b\"],\"type\":\"array\"}", 2)]
        [InlineData("{\"key\":\"skills.customDirectories\",\"value\":[],\"type\":\"array\"}", 0)]
        [InlineData("not json", 0)]
        [InlineData("", 0)]
        [InlineData("{\"value\":3}", 0)]
        public void User_directories_are_read_from_omp_config_output(string output, int count) =>
            Assert.Equal(count, SkillPack.ParseUserDirectories(output).Count);

        [Fact]
        public void Overlay_file_is_written_for_loaded_skills_and_removed_otherwise()
        {
            string tag = "t" + Guid.NewGuid().ToString("N")[..8];
            using var root = new TempFolder();
            var launch = SkillPack.Prepare(SkillSelection.Default, root.Path)!;
            try
            {
                string? path = OmpLaunch.WriteSkillsOverlay(tag, launch, new[] { @"D:\mine" });
                Assert.Equal(OmpLaunch.SkillsOverlayPath(tag), path);
                using (var doc = JsonDocument.Parse(File.ReadAllText(path!)))
                    Assert.Equal(launch.Directory, doc.RootElement.GetProperty("skills").GetProperty("customDirectories").EnumerateArray().Last().GetString());

                Assert.Null(OmpLaunch.WriteSkillsOverlay(tag, null));
                Assert.False(File.Exists(OmpLaunch.SkillsOverlayPath(tag)));
            }
            finally { try { File.Delete(OmpLaunch.SkillsOverlayPath(tag)); } catch { } }
        }

        [Fact]
        public void The_overlay_follows_the_host_config_on_the_command_line()
        {
            var args = OmpLaunch.BuildArguments("D:\\w", "C:\\t\\host.yml", "C:\\t\\guide.md", null, null, "C:\\t\\skills.yml");
            Assert.Equal(new[] { "--mode", "rpc-ui", "--cwd", "D:\\w", "--config", "C:\\t\\host.yml", "--config", "C:\\t\\skills.yml", "--append-system-prompt", "C:\\t\\guide.md" }, args);
            Assert.DoesNotContain("--config", OmpLaunch.BuildArguments("X", null, null, null));
            Assert.Equal(1, OmpLaunch.BuildArguments("X", "h.yml", null, null).Count(a => a == "--config"));
        }

        // ---- 가이드 문구 ------------------------------------------------------------------------------------------

        [Fact]
        public void The_agent_guide_routes_to_app_tools_first_and_names_what_the_app_has()
        {
            string guide = Flat(OmpLaunch.ReadGuideResource()!);
            Assert.Contains("Choosing the path: app tools first, Python second", guide);
            foreach (string term in new[]
            {
                "csv.run_analysis", "t-tests", "ANOVA", "chi-square", "correlation", "normality", "repeated-measures", "mixed models",
                "non-parametric", "Kaplan-Meier", "Cox", "k-means", "random forest", "SVM", "gradient boosting", "AdaBoost", "AutoML", "PCA", "LDA",
                "Always state the path", "Python 분석 (앱 검증 범위 밖) / Python analysis (outside the app's validated tools)",
                "skill://nanum-python-analysis", "patient-specific",
            })
                Assert.Contains(term, guide);
        }

        [Fact]
        public void The_python_guide_tells_which_path_to_use_and_what_the_loaded_skills_are_for()
        {
            string with = PythonGuide.Build(Context(new[] { SkillPack.AppSkill, "shap" }));
            Assert.Contains("Which path", with);
            Assert.Contains("Python 분석 (앱 검증 범위 밖) / Python analysis (outside the app's validated tools)", with);
            Assert.Contains("skill://" + SkillPack.AppSkill, with);
            Assert.Contains("never patient-specific", with);
            Assert.Contains("third-party guidance", with);

            string without = PythonGuide.Build(Context(null));
            Assert.Contains("Which path", without);
            Assert.Contains("No analysis skills are loaded", without);
            Assert.DoesNotContain("skill://", without);
        }

        private static PythonGuideContext Context(IReadOnlyList<string>? skills) =>
            new(AgentWorkspaceContext.Empty, @"D:\data\a_분석결과", null, Array.Empty<string>(), LspStatus.Unavailable, AgentDataPolicy.SummaryOnly, skills);

        [Fact]
        public void The_app_skill_states_the_data_policy_header_honesty_label_and_clinical_boundary()
        {
            string text = Flat(ResourceText($"Skills/{SkillPack.AppSkill}/SKILL.md"));
            foreach (string term in new[]
            {
                "data/<name>.csv", "schema.json", "Summary only", "Rows with approval", "Never** print raw rows", "Reproducibility header",
                "row count used and dropped", "package versions", "multiple testing", "convergence",
                "Python 분석 (앱 검증 범위 밖) / Python analysis (outside the app's validated tools)",
                "not** for patient-specific", "csv.show_markdown", "csv.show_image", "_report.md", "figures/", "Use the app first",
            })
                Assert.Contains(term, text, StringComparison.Ordinal);
        }

        // ---- 컨트롤러 연동 ----------------------------------------------------------------------------------------

        private static AgentHostOptions On(bool python = true, bool skills = true, string categoriesOff = "", string skillsOff = "") =>
            new(Language: "en", AppVersion: "1.2.3", AllowLocalPython: python, SkillsEnabled: skills, SkillCategoriesOff: categoriesOff, SkillsOff: skillsOff);

        private static List<string> Args(FakeOmpProcess p) => p.Launch.Arguments.ToList();

        private static List<string> ConfigFiles(FakeOmpProcess p)
        {
            var a = Args(p);
            return a.Select((x, i) => (x, i)).Where(t => t.x == "--config").Select(t => a[t.i + 1]).ToList();
        }

        private static async Task SettledAsync(ControllerRig rig, int processes)
        {
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count >= processes);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
        }

        private static string SkillsDirOf(string overlayPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(overlayPath));
            return doc.RootElement.GetProperty("skills").GetProperty("customDirectories").EnumerateArray().Last().GetString()!;
        }

        [Fact]
        public async Task Python_on_launches_omp_with_the_skills_overlay_and_the_guide_points_at_them()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools }, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();

            var configs = ConfigFiles(rig.Proc);
            Assert.Equal(2, configs.Count);
            Assert.StartsWith("omp-host-p", Path.GetFileName(configs[0]));
            Assert.EndsWith(".yml", configs[1]);
            Assert.StartsWith("omp-skills-p", Path.GetFileName(configs[1]));
            string dir = SkillsDirOf(configs[1]);
            Assert.StartsWith(rig.SkillRoot, dir);
            Assert.True(File.Exists(Path.Combine(dir, SkillPack.AppSkill, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(dir, "pkpd-modeling", "SKILL.md")));

            var args = Args(rig.Proc);
            string guide = File.ReadAllText(args[args.IndexOf("--append-system-prompt") + 1]);
            Assert.Contains("Analysis skills** are loaded", guide);
            Assert.Contains("skill://nanum-python-analysis", guide);
        }

        [Fact]
        public async Task Python_off_loads_no_skills_at_all()
        {
            using var rig = new ControllerRig(On(python: false));
            await rig.StartAsync();

            Assert.Single(ConfigFiles(rig.Proc));
            Assert.False(Directory.Exists(rig.SkillRoot));
            Assert.DoesNotContain("skill://", File.ReadAllText(Args(rig.Proc)[Args(rig.Proc).IndexOf("--append-system-prompt") + 1]));
        }

        [Fact]
        public async Task The_master_switch_off_loads_no_skills_even_with_python_on()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(skills: false), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools }, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();

            Assert.Single(ConfigFiles(rig.Proc));
            Assert.False(Directory.Exists(rig.SkillRoot));
            var args = Args(rig.Proc);
            string guide = File.ReadAllText(args[args.IndexOf("--append-system-prompt") + 1]);
            Assert.Contains("No analysis skills are loaded", guide);
            Assert.Contains("Which path", guide);                                               // 길 안내와 표기 규칙은 스킬 없이도 같다
        }

        [Fact]
        public async Task A_category_off_changes_which_skills_are_extracted_and_the_user_directories_are_kept()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(categoriesOff: "clinical", skillsOff: "shap"), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools }, dataFile: tmp.Combine("a.csv"));
            rig.Cli = (_, args, _, _) => Task.FromResult<string?>(args.SequenceEqual(new[] { "config", "get", "skills.customDirectories", "--json" })
                ? "{\"key\":\"skills.customDirectories\",\"value\":[\"E:\\\\mine\"],\"type\":\"array\"}" : null);
            await rig.StartAsync();

            string overlay = ConfigFiles(rig.Proc)[1];
            using var doc = JsonDocument.Parse(File.ReadAllText(overlay));
            var dirs = doc.RootElement.GetProperty("skills").GetProperty("customDirectories").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal(2, dirs.Count);
            Assert.Equal("E:\\mine", dirs[0]);
            Assert.False(Directory.Exists(Path.Combine(dirs[1], "pkpd-modeling")));
            Assert.False(Directory.Exists(Path.Combine(dirs[1], "shap")));
            Assert.True(Directory.Exists(Path.Combine(dirs[1], "scikit-learn")));
        }

        [Fact]
        public async Task Changing_the_skill_selection_restarts_idle_omp_with_the_new_set()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools }, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            string first = SkillsDirOf(ConfigFiles(rig.Proc)[1]);

            rig.OnUi(() => rig.Controller.Options = On(categoriesOff: "ml"));
            await SettledAsync(rig, 2);
            string second = SkillsDirOf(ConfigFiles(rig.Proc)[1]);
            Assert.NotEqual(first, second);
            Assert.False(Directory.Exists(Path.Combine(second, "scikit-learn")));
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("text").Contains("analysis-skill settings") && n.Str("text").Contains("restarting"));

            // 같은 구성을 다시 넘기면 다시 시작하지 않는다.
            rig.OnUi(() => rig.Controller.Options = On(categoriesOff: "ml"));
            await Task.Delay(300);
            Assert.Equal(2, rig.Factory.Processes.Count);

            // 로컬 Python을 끄면 스킬이 사라진다.
            rig.OnUi(() => rig.Controller.Options = On(python: false));
            await SettledAsync(rig, 3);
            Assert.Single(ConfigFiles(rig.Proc));
        }

        // ---- 설정 화면 ---------------------------------------------------------------------------------------------

        private static void OnForm(AppSettings settings, Action<Form1> body, string language = "en")
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Form1? form = null;
                var savedDefault = CultureInfo.DefaultThreadCurrentUICulture;
                var savedCurrent = Thread.CurrentThread.CurrentUICulture;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    form = new Form1(settings);
                    _ = form.Handle;
                    form.ApplyLanguageSetting(language);
                    body(form);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { form?.Dispose(); } catch { }
                    CultureInfo.DefaultThreadCurrentUICulture = savedDefault;
                    Thread.CurrentThread.CurrentUICulture = savedCurrent;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void The_settings_section_shows_counts_descriptions_cost_and_leaves_settings_alone_until_changed()
        {
            var settings = new AppSettings { AgentAllowLocalPython = true, AgentDataPolicy = "RowsAllowed" };
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "ai");
                var page = Assert.IsType<AgentPage>(dlg.CurrentPage);
                var section = page.Skills;
                var m = SkillPack.Manifest;

                Assert.False(section.IsDirty);
                Assert.Equal(SkillSelection.Default, section.Current());
                Assert.True(dlg.ApplyAll());
                Assert.True(settings.AgentSkillsEnabled);
                Assert.Equal("", settings.AgentSkillsOff);

                foreach (var cat in new[] { "clinical", "stats", "ml" })
                    Assert.Contains($"({m.Count(cat)})", section.CategoryText(cat));
                Assert.False(string.IsNullOrWhiteSpace(section.Description("pkpd-modeling")));
                int tokens = SkillPack.EstimateTokens(SkillSelection.Default);
                Assert.Contains(tokens.ToString("N0"), section.TokensText);
                Assert.Contains("MIT", section.NoteText);
                Assert.Contains(m.UpstreamTag, section.NoteText);
                Assert.Contains("validated", section.NoteText);
                Assert.False(section.PythonOffNoticeVisible);
                Assert.False(section.WarningVisible);                                            // 행 공유를 허용했으니 '요약만' 경고는 없다
            });
        }

        [Fact]
        public void The_settings_section_writes_category_and_skill_choices_and_cannot_turn_off_the_app_rules()
        {
            var settings = new AppSettings { AgentAllowLocalPython = true };
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "ai");
                var section = Assert.IsType<AgentPage>(dlg.CurrentPage).Skills;

                section.ToggleCategory("ml");                                                    // 분류 하나 끄기
                section.SetSkillChecked("pkpd-modeling", false);                                 // 개별 스킬 끄기
                section.SetSkillChecked(SkillPack.AppSkill, false);                              // 앱 규칙은 끌 수 없다
                Assert.Contains(SkillPack.Resolve(section.Current()), s => s.IsApp);          // 선택에서도 항상 실린다
                Assert.True(section.IsDirty);
                Assert.True(dlg.ApplyAll());

                Assert.True(settings.AgentSkillsEnabled);
                Assert.Equal("ml", settings.AgentSkillCategoriesOff);
                Assert.Contains("pkpd-modeling", settings.AgentSkillsOff.Split(','));
                Assert.Contains("shap", settings.AgentSkillsOff.Split(','));
                Assert.DoesNotContain(SkillPack.AppSkill, settings.AgentSkillsOff.Split(','));
                var names = SkillPack.Resolve(SkillSelection.FromSettings(settings)).Select(s => s.Name).ToHashSet();
                Assert.DoesNotContain("scikit-learn", names);
                Assert.DoesNotContain("pkpd-modeling", names);
                Assert.Contains("clinical-reports", names);
                Assert.False(section.IsDirty);                                                   // 적용했으니 다시 적용할 것이 없다

                section.ToggleCategory("ml");                                                    // 다시 누르면 분류 전체가 켜진다
                Assert.Equal("", string.Join(",", section.Current().CategoriesOff.Split(',').Where(c => c == "ml")));
                Assert.True(section.SkillChecked("shap"));

                section.MasterChecked = false;                                                   // 전체 끄기
                Assert.False(section.ListEnabled);
                Assert.True(dlg.ApplyAll());
                Assert.False(settings.AgentSkillsEnabled);
                Assert.Empty(SkillPack.Resolve(SkillSelection.FromSettings(settings)));
            });
        }

        [Fact]
        public void The_settings_section_warns_about_summary_only_with_python_and_explains_when_python_is_off()
        {
            OnForm(new AppSettings { AgentAllowLocalPython = true, AgentDataPolicy = "SummaryOnly" }, form =>
            {
                using var dlg = new SettingsDialog(form, "ai");
                var section = Assert.IsType<AgentPage>(dlg.CurrentPage).Skills;

                Assert.True(section.WarningVisible);
                Assert.Contains("read by the AI model", section.WarningText);
                Assert.Contains("not a hard guarantee", section.WarningText);

                section.SetContext(false, AgentDataPolicy.SummaryOnly);                          // Python이 꺼지면 경고 대신 '실리지 않는다' 안내
                Assert.False(section.WarningVisible);
                Assert.True(section.PythonOffNoticeVisible);

                section.SetContext(true, AgentDataPolicy.RowsWithApproval);
                Assert.False(section.WarningVisible);

                section.SetContext(true, AgentDataPolicy.SummaryOnly);
                section.MasterChecked = false;                                                   // 스킬이 꺼져 있으면 경고도 필요 없다
                Assert.False(section.WarningVisible);
                Assert.Contains("nothing is added", section.TokensText);
            });
        }

        [Fact]
        public void The_settings_section_follows_the_ui_language()
        {
            var settings = new AppSettings { Language = "ko", AgentAllowLocalPython = true };
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "ai");
                var section = Assert.IsType<AgentPage>(dlg.CurrentPage).Skills;
                Assert.Contains("임상 연구", section.CategoryText("clinical"));
                Assert.Contains("검증", section.NoteText);
                Assert.Equal(SkillPack.Manifest.Find("shap")!.SummaryKo, section.Description("shap"));
            }, "ko");
        }
    }
}
