using System.Drawing;
using System.Windows.Forms;
using System.Globalization;
using System.Text.Json;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    /// <summary>설정 값(기본값·저장·보정), 창 위치 보정, 테마 '시스템', 언어 '자동', 설정 대화 상자의 쪽들.</summary>
    // 실제 Form1을 만들고 언어·테마 같은 앱 전역 상태를 바꾸므로 다른 Form1 테스트와 직렬화한다.
    [Collection("SavedViewStore")]
    public class SettingsTests
    {
        private static AppSettings RoundTrip(AppSettings s) =>
            JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }))!.Normalize();

        // ---- 기본값·저장 -------------------------------------------------------------------------------------

        [Fact]
        public void Defaults_start_with_the_explorer_and_ai_panels_on_and_row_detail_facets_off()
        {
            var s = new AppSettings();
            var p = s.StartupPanels;
            Assert.True(p.Agent);
            Assert.False(p.Detail);
            Assert.False(p.Facets);
            Assert.True(p.Explorer);
            Assert.False(p.Findings);
            Assert.True(p.CellBar);
            Assert.False(s.RememberLastPanels);
            Assert.True(s.RememberWindow);
            Assert.Equal("auto", s.Language);
            Assert.Equal("", s.Theme);
            Assert.Equal("auto", s.DefaultEncoding);
            Assert.False(s.ReopenLastWorkspace);
        }

        private static AppSettings FromJson(string json) => JsonSerializer.Deserialize<AppSettings>(json)!.Normalize();

        private const string Pre341Default = """{ "Agent": true, "Detail": false, "Facets": false, "Explorer": false, "Findings": false, "CellBar": true }""";

        [Fact]
        public void A_file_that_still_holds_the_old_default_startup_panels_gets_the_explorer_once()
        {
            var s = FromJson("{ \"StartupPanels\": " + Pre341Default + " }");
            Assert.True(s.StartupPanels.Explorer);
            Assert.True(s.StartupPanels.Agent && s.StartupPanels.CellBar);
            Assert.False(s.StartupPanels.Detail || s.StartupPanels.Facets || s.StartupPanels.Findings);
            Assert.Equal(AppSettings.CurrentPanelDefaultsVersion, s.PanelDefaultsVersion);
        }

        [Theory]
        [InlineData("""{ "Agent": false, "Detail": false, "Facets": false, "Explorer": false, "Findings": false, "CellBar": true }""")]   // AI를 껐다
        [InlineData("""{ "Agent": true, "Detail": true, "Facets": false, "Explorer": false, "Findings": false, "CellBar": true }""")]    // 행 상세를 켰다
        [InlineData("""{ "Agent": true, "Detail": false, "Facets": false, "Explorer": false, "Findings": false, "CellBar": false }""")]  // 셀 줄을 껐다
        [InlineData("""{ "Agent": true, "Detail": false, "Facets": false, "Explorer": false, "Findings": true, "CellBar": true }""")]
        public void A_customized_startup_panel_choice_is_left_alone_by_the_migration(string panels)
        {
            var s = FromJson("{ \"StartupPanels\": " + panels + " }");
            Assert.False(s.StartupPanels.Explorer);
            var expect = JsonSerializer.Deserialize<PanelLayout>(panels)!;
            Assert.True(expect.SameVisibility(s.StartupPanels));
            Assert.Equal(AppSettings.CurrentPanelDefaultsVersion, s.PanelDefaultsVersion);   // 그래도 한 번 처리한 것으로 기록한다
        }

        [Fact]
        public void The_migration_runs_once_so_turning_the_explorer_off_again_sticks()
        {
            var s = FromJson("{ \"StartupPanels\": " + Pre341Default + " }");
            Assert.True(s.StartupPanels.Explorer);

            s.StartupPanels.Explorer = false;            // 설정에서 사용자가 끈다 → 옛 기본과 같은 모양이 다시 된다
            var again = RoundTrip(s);                    // 저장 → 다시 읽기(마커가 있으니 다시 켜지 않는다)
            Assert.False(again.StartupPanels.Explorer);
            Assert.False(again.Normalize().StartupPanels.Explorer);
        }

        [Fact]
        public void A_file_written_by_this_version_is_never_migrated_even_if_it_looks_like_the_old_default()
        {
            var s = new AppSettings { StartupPanels = new PanelLayout() };   // 새 설치에서 사용자가 탐색기를 꺼 둔 채 저장
            var path = Path.Combine(Path.GetTempPath(), "nanum_settings_" + Guid.NewGuid().ToString("N"));
            var previous = AppSettings.DirectoryOverride;   // 테스트 전역 격리 폴더(TestEnvironment)를 돌려 놓는다
            AppSettings.DirectoryOverride = path;
            try
            {
                s.Save();
                var loaded = AppSettings.Load();
                Assert.False(loaded.StartupPanels.Explorer);
                Assert.Equal(AppSettings.CurrentPanelDefaultsVersion, loaded.PanelDefaultsVersion);
            }
            finally
            {
                AppSettings.DirectoryOverride = previous;
                try { Directory.Delete(path, true); } catch { }
            }
        }

        [Fact]
        public void Every_setting_survives_a_save_and_load()
        {
            var s = new AppSettings
            {
                Theme = "Dark", Language = "ko", ShowTypeBadges = false, ShowFieldLabels = true, DeleteIndexOnClose = true,
                StartupPanels = new PanelLayout { Agent = false, Detail = true, Facets = true, Explorer = true, Findings = true, CellBar = false },
                RememberLastPanels = true,
                LastPanels = new PanelLayout { Agent = true, Detail = true, DetailWidth = 420, ExplorerWidth = 280, AgentWidth = 500 },
                DetailPanelWidth = 410, ExplorerPanelWidth = 290, AgentPanelWidth = 470,
                RememberWindow = false, Window = new WindowGeometry { X = 10, Y = 20, Width = 1000, Height = 700, Maximized = true },
                ReopenLastWorkspace = true, LastWorkspace = @"C:\w\p.ncvws",
                MaxCellLines = 9, GridFontSize = 11f, DefaultEncoding = EncodingDetector.Cp949, RecentCount = 4,
            };
            var back = RoundTrip(s);

            Assert.Equal((s.Theme, s.Language, s.ShowTypeBadges, s.ShowFieldLabels, s.DeleteIndexOnClose), (back.Theme, back.Language, back.ShowTypeBadges, back.ShowFieldLabels, back.DeleteIndexOnClose));
            Assert.True(s.StartupPanels.SameVisibility(back.StartupPanels));
            Assert.True(back.RememberLastPanels);
            Assert.True(s.LastPanels!.SameVisibility(back.LastPanels!));
            Assert.Equal((420, 280, 500), (back.LastPanels!.DetailWidth, back.LastPanels.ExplorerWidth, back.LastPanels.AgentWidth));
            Assert.Equal((410, 290, 470), (back.DetailPanelWidth, back.ExplorerPanelWidth, back.AgentPanelWidth));
            Assert.False(back.RememberWindow);
            Assert.Equal((10, 20, 1000, 700, true), (back.Window!.X, back.Window.Y, back.Window.Width, back.Window.Height, back.Window.Maximized));
            Assert.True(back.ReopenLastWorkspace);
            Assert.Equal(@"C:\w\p.ncvws", back.LastWorkspace);
            Assert.Equal((9, 11f, EncodingDetector.Cp949, 4), (back.MaxCellLines, back.GridFontSize, back.DefaultEncoding, back.RecentCount));
        }

        [Fact]
        public void A_settings_file_from_before_this_version_loads_with_the_new_defaults()
        {
            var s = JsonSerializer.Deserialize<AppSettings>("""{ "Theme": "Light", "Language": "en", "AgentPanelWidth": 500, "RecentWorkspaces": ["C:\\a.ncvws"] }""")!.Normalize();
            Assert.Equal("Light", s.Theme);
            Assert.Equal(500, s.AgentPanelWidth);
            Assert.True(s.StartupPanels.Agent);
            Assert.False(s.StartupPanels.Detail);
            Assert.Null(s.LastPanels);
            Assert.Null(s.Window);
            Assert.Single(s.RecentWorkspaces);
        }

        [Fact]
        public void Garbage_values_are_pulled_back_to_something_usable()
        {
            var s = JsonSerializer.Deserialize<AppSettings>("""
                { "Theme": "Purple", "Language": "fr", "StartupPanels": null, "DetailPanelWidth": 99999, "ExplorerPanelWidth": 3,
                  "MaxCellLines": 500, "GridFontSize": 400, "DefaultEncoding": "EBCDIC", "RecentCount": 0 }
                """)!.Normalize();
            Assert.Equal("", s.Theme);
            Assert.Equal("auto", s.Language);
            Assert.NotNull(s.StartupPanels);
            Assert.Equal(PanelLayout.MaxWidth, s.DetailPanelWidth);
            Assert.Equal(PanelLayout.MinWidth, s.ExplorerPanelWidth);
            Assert.Equal(20, s.MaxCellLines);
            Assert.Equal(24f, s.GridFontSize);
            Assert.Equal("auto", s.DefaultEncoding);
            Assert.Equal(1, s.RecentCount);
        }

        [Fact]
        public void A_smaller_recent_count_trims_the_list_and_new_entries_respect_it()
        {
            var s = new AppSettings { RecentCount = 3 };
            for (int i = 0; i < 6; i++) s.AddRecentWorkspace(Path.Combine(Path.GetTempPath(), $"r{i}.ncvws"));
            Assert.Equal(3, s.RecentWorkspaces.Count);
            Assert.EndsWith("r5.ncvws", s.RecentWorkspaces[0]);

            s.RecentCount = 1;
            s.TrimRecent();
            Assert.Single(s.RecentWorkspaces);
        }

        [Fact]
        public void Reset_restores_only_the_page_it_belongs_to()
        {
            var s = new AppSettings
            {
                Theme = "Dark", Language = "ko", ShowTypeBadges = false, MaxCellLines = 12, DefaultEncoding = EncodingDetector.Utf8,
                StartupPanels = new PanelLayout { Agent = false }, RememberWindow = false, AgentMaxRows = 17, RecentWorkspaces = { "C:\\keep.ncvws" },
            };
            s.ResetGeneral();
            Assert.Equal(("", "auto"), (s.Theme, s.Language));
            Assert.False(s.ShowTypeBadges);                   // 다른 쪽은 그대로
            s.ResetPanels();
            Assert.True(s.StartupPanels.Agent);
            Assert.True(s.RememberWindow);
            s.ResetGrid();
            Assert.True(s.ShowTypeBadges);
            Assert.Equal(AppSettings.DefaultMaxCellLines, s.MaxCellLines);
            s.ResetFiles();
            Assert.Equal("auto", s.DefaultEncoding);
            Assert.Equal(17, s.AgentMaxRows);                 // AI 설정은 건드리지 않는다
            Assert.Equal("C:\\keep.ncvws", Assert.Single(s.RecentWorkspaces));
        }

        // ---- 창 위치 -----------------------------------------------------------------------------------------

        private static readonly Rectangle Main = new(0, 0, 1920, 1040);
        private static readonly Rectangle Second = new(1920, 0, 1280, 984);

        private static WindowGeometry G(int x, int y, int w, int h) => new() { X = x, Y = y, Width = w, Height = h };

        [Fact]
        public void A_window_that_is_on_a_screen_keeps_its_place()
        {
            Assert.Equal(new Rectangle(100, 80, 1000, 700), WindowGeometry.Fit(G(100, 80, 1000, 700), new[] { Main }));
            Assert.Equal(new Rectangle(2000, 100, 900, 600), WindowGeometry.Fit(G(2000, 100, 900, 600), new[] { Main, Second }));
        }

        [Fact]
        public void A_window_saved_on_a_monitor_that_is_gone_moves_onto_a_remaining_screen()
        {
            var fit = WindowGeometry.Fit(G(2000, 100, 900, 600), new[] { Main })!.Value;     // 둘째 모니터를 뗐다
            Assert.True(Main.Contains(fit));
            Assert.Equal(new Size(900, 600), fit.Size);

            var above = WindowGeometry.Fit(G(300, -5000, 800, 600), new[] { Main })!.Value;   // 화면 위쪽 멀리
            Assert.True(Main.Contains(above));
        }

        [Fact]
        public void A_window_bigger_than_the_screen_is_shrunk_to_fit_and_nonsense_is_discarded()
        {
            var fit = WindowGeometry.Fit(G(-50, -50, 4000, 3000), new[] { Main })!.Value;
            Assert.Equal(Main, fit);
            Assert.Null(WindowGeometry.Fit(G(0, 0, 10, 10), new[] { Main }));                // 너무 작다(손상)
            Assert.Null(WindowGeometry.Fit(null, new[] { Main }));
            Assert.Null(WindowGeometry.Fit(G(0, 0, 800, 600), Array.Empty<Rectangle>()));
        }

        [Fact]
        public void A_window_whose_title_strip_is_barely_on_a_screen_is_pulled_in_so_it_can_be_dragged()
        {
            var fit = WindowGeometry.Fit(G(1900, 100, 800, 600), new[] { Main })!.Value;     // 거의 오른쪽 밖(20px만 걸침)
            Assert.True(Main.Contains(fit));
        }

        // ---- 화면에 반영(보이지 않는 창) -----------------------------------------------------------------------

        private static void OnForm(AppSettings settings, Action<Form1> body)
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
                    body(form);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { form?.Dispose(); } catch { }
                    CultureInfo.DefaultThreadCurrentUICulture = savedDefault;   // 언어 설정이 다른 테스트로 새지 않게
                    Thread.CurrentThread.CurrentUICulture = savedCurrent;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void Theme_setting_system_follows_windows_and_light_or_dark_override_it_and_system_comes_back()
        {
            var settings = new AppSettings { Theme = "Dark" };
            OnForm(settings, form =>
            {
                Assert.Same(ThemePalette.Dark, form.PaletteRef);                  // 저장값이 시스템보다 우선
                form.ApplyThemeSetting("Light");
                Assert.Same(ThemePalette.Light, form.PaletteRef);
                Assert.Equal("Light", settings.Theme);

                form.ApplyThemeSetting("");                                       // 다시 '시스템'
                Assert.Equal("", settings.Theme);
                Assert.Same(ThemePalette.For(ThemeManager.DetectSystem()), form.PaletteRef);

                form.ApplyThemeSetting("nonsense");                               // 모르는 값은 시스템
                Assert.Equal("", settings.Theme);
            });
        }

        [Fact]
        public void Language_setting_auto_follows_the_os_and_explicit_languages_switch_the_ui_at_once()
        {
            var settings = new AppSettings();
            OnForm(settings, form =>
            {
                form.ApplyLanguageSetting("ko");
                Assert.Equal("ko", settings.Language);
                Assert.Equal("ko", Loc.CurrentLanguage);
                Assert.Equal("파일", form.MainMenuStrip!.Items[0].Text);           // 메뉴도 새 언어로

                form.ApplyLanguageSetting("en");
                Assert.Equal("en", Loc.CurrentLanguage);
                Assert.Equal("File", form.MainMenuStrip!.Items[0].Text);

                form.ApplyLanguageSetting("auto");
                Assert.Equal("auto", settings.Language);
                string os = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
                Assert.Equal(os, Loc.CurrentLanguage);

                form.ApplyLanguageSetting("klingon");
                Assert.Equal("auto", settings.Language);
            });
        }

        [Fact]
        public void Grid_settings_change_the_row_metrics_and_are_saved()
        {
            var settings = new AppSettings();
            OnForm(settings, form =>
            {
                form.ApplyGridSettings(typeBadges: false, fieldLabels: false, maxCellLines: 3, fontSize: 14f);
                Assert.False(settings.ShowTypeBadges);
                Assert.Equal(3, settings.MaxCellLines);
                Assert.Equal(14f, settings.GridFontSize);
                var grid = (DataGridView)typeof(Form1).GetField("grid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
                Assert.Equal(14f, grid.Font.SizeInPoints);

                form.ApplyGridSettings(typeBadges: true, fieldLabels: false, maxCellLines: 3, fontSize: 0f);     // 0 = 창 기본 글꼴
                Assert.True(settings.ShowTypeBadges);
                Assert.NotEqual(14f, grid.Font.SizeInPoints);
            });
        }

        // ---- 설정 대화 상자 ------------------------------------------------------------------------------------

        [Fact]
        public void The_dialog_has_the_six_pages_in_order_and_can_open_on_any_of_them()
        {
            OnForm(new AppSettings(), form =>
            {
                using var dlg = new SettingsDialog(form, "shortcuts");
                Assert.Equal(new[] { "general", "panels", "grid", "files", "ai", "shortcuts" }, dlg.Pages.Select(p => p.Id));
                Assert.Equal("shortcuts", dlg.CurrentPage!.Id);
                dlg.SelectPage("ai");
                Assert.Equal("ai", dlg.CurrentPage!.Id);
                dlg.SelectPage("nonsense");
                Assert.Equal("general", dlg.CurrentPage!.Id);
            });
        }

        private static AppSettings NonDefault() => new()
        {
            Theme = "Dark", PanelDefaultsVersion = AppSettings.CurrentPanelDefaultsVersion,   // 읽어 온 설정은 이미 마커가 있다(저장하면 기록된다)
            StartupPanels = new PanelLayout { Agent = false, Detail = true, Facets = true, Explorer = true, Findings = true, CellBar = false },
            RememberLastPanels = true, RememberWindow = false, ReopenLastWorkspace = true,
            ShowTypeBadges = false, MaxCellLines = 8, GridFontSize = 12f,
            DefaultEncoding = EncodingDetector.Cp949, DeleteIndexOnClose = true, RecentCount = 5,
            AnalysisMemoryAuto = false, AnalysisMemoryManualGb = AnalysisMemoryBudget.MinimumManualGb,
        };

        [Fact]
        public void Opening_the_dialog_and_applying_without_touching_anything_keeps_every_value()
        {
            var settings = NonDefault();
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "general");   // 대화 상자가 설정을 입력 칸에 읽어 오고
                string before = JsonSerializer.Serialize(settings);
                Assert.True(dlg.ApplyAll());                           // 입력 칸에서 다시 설정으로 쓴다
                Assert.Equal(before, JsonSerializer.Serialize(settings));
                Assert.Equal("Dark", settings.Theme);
                Assert.Equal(8, settings.MaxCellLines);
                Assert.Equal(EncodingDetector.Cp949, settings.DefaultEncoding);
                Assert.False(settings.StartupPanels.CellBar);
                Assert.True(settings.StartupPanels.Findings);
            });
        }

        [Fact]
        public void Resetting_each_page_and_applying_restores_the_defaults_of_the_non_ai_pages()
        {
            var settings = NonDefault();
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "general");
                foreach (var page in dlg.Pages.Where(p => p.CanReset && p.Id != "ai")) page.ResetDefaults();
                Assert.True(dlg.ApplyAll());

                var d = new AppSettings();
                Assert.Equal(d.Theme, settings.Theme);
                Assert.True(d.StartupPanels.SameVisibility(settings.StartupPanels));
                Assert.Equal((d.RememberLastPanels, d.RememberWindow, d.ReopenLastWorkspace), (settings.RememberLastPanels, settings.RememberWindow, settings.ReopenLastWorkspace));
                Assert.Equal((d.ShowTypeBadges, d.MaxCellLines, d.GridFontSize), (settings.ShowTypeBadges, settings.MaxCellLines, settings.GridFontSize));
                Assert.Equal((d.DefaultEncoding, d.DeleteIndexOnClose, d.RecentCount), (settings.DefaultEncoding, settings.DeleteIndexOnClose, settings.RecentCount));
                Assert.Equal((d.AnalysisMemoryAuto, d.AnalysisMemoryManualGb), (settings.AnalysisMemoryAuto, settings.AnalysisMemoryManualGb));
            });
        }

        [Fact]
        public void Reset_on_a_page_changes_its_inputs_and_only_apply_makes_it_permanent()
        {
            var settings = new AppSettings { MaxCellLines = 12, DefaultEncoding = EncodingDetector.Cp949 };
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "files");
                dlg.CurrentPage!.ResetDefaults();
                Assert.Equal(EncodingDetector.Cp949, settings.DefaultEncoding);      // 아직 적용 전
                Assert.True(dlg.ApplyAll());
                Assert.Equal("auto", settings.DefaultEncoding);
                Assert.Equal(12, settings.MaxCellLines);                             // 다른 쪽은 그대로
            });
        }

        [Fact]
        public void The_files_page_saves_auto_or_a_manual_cap_and_warns_when_the_cap_exceeds_available_memory()
        {
            var settings = new AppSettings();
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "files");
                var page = dlg.CurrentPage!;
                T Get<T>(string name) => (T)page.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;
                var mode = Get<ComboBox>("_memMode");
                var gb = Get<NumericUpDown>("_memGb");
                var warn = Get<Label>("_memWarn");
                var info = Get<Label>("_memInfo");

                Assert.Equal(0, mode.SelectedIndex);                       // 기본은 자동이고 값 칸은 잠겨 있다
                Assert.False(gb.Enabled);
                Assert.Contains("GB", info.Text);

                mode.SelectedIndex = 1;
                Assert.True(gb.Enabled);
                gb.Value = gb.Minimum;
                Assert.True(dlg.ApplyAll());
                Assert.False(settings.AnalysisMemoryAuto);
                Assert.Equal(AnalysisMemoryBudget.MinimumManualGb, settings.AnalysisMemoryManualGb);

                gb.Value = gb.Maximum;                                     // 물리 메모리 전부: 가용 메모리를 넘으니 경고하되 저장은 된다
                Assert.NotEqual("", warn.Text);
                Assert.True(dlg.ApplyAll());
                Assert.Equal((double)gb.Maximum, settings.AnalysisMemoryManualGb);

                mode.SelectedIndex = 0;
                Assert.True(dlg.ApplyAll());
                Assert.True(settings.AnalysisMemoryAuto);
                Assert.Equal((double)gb.Maximum, settings.AnalysisMemoryManualGb);   // 마지막 직접 값은 기억한다
                Assert.Equal(0, mode.SelectedIndex);
            });
        }

        [Fact]
        public void The_shortcuts_page_lists_every_command_in_the_shortcut_table_including_the_new_keys()
        {
            OnForm(new AppSettings(), form =>
            {
                using var dlg = new SettingsDialog(form, "shortcuts");
                var page = Assert.IsType<ShortcutsPage>(dlg.CurrentPage);
                var rows = page.Rows().ToDictionary(r => r.Command, r => r.Keys);
                Assert.Equal(CommandShortcuts.All.Count, rows.Count);
                Assert.Contains("Ctrl+,", rows.Values);
                Assert.Contains("Ctrl+S", rows.Values);
                Assert.Contains("Ctrl+Shift+F", rows.Values);
                Assert.Contains("Ctrl+Alt+S", rows.Values);
            });
        }

        [Fact]
        public void The_ai_page_applies_nothing_until_a_value_changes_and_follows_the_stricter_wins_rule()
        {
            var settings = new AppSettings { AgentDataPolicy = "RowsAllowed", AgentApprovalMode = "yolo", AgentAllowLocalPython = true };
            OnForm(settings, form =>
            {
                using var dlg = new SettingsDialog(form, "ai");
                var page = Assert.IsType<AgentPage>(dlg.CurrentPage);
                Assert.False(page.IsDirty);
                Assert.True(dlg.ApplyAll());                                         // 건드리지 않았으니 설정·에이전트에 아무 일도 없다
                Assert.Equal("RowsAllowed", settings.AgentDataPolicy);

                // 작업 공간이 없을 때 앱 범위로 직접 적용: 값이 저장된다(적용 입력은 대화 상자 쪽이 만든 것과 같은 모양).
                form.ApplyAgentSettings(new Form1.AgentSettingsInput(false, AgentApprovalMode.AlwaysAsk, AgentDataPolicy.SummaryOnly, false, true, 50, " C:\\x\\omp.exe ", " --foo "));
                Assert.Equal("SummaryOnly", settings.AgentDataPolicy);
                Assert.Equal("always-ask", settings.AgentApprovalMode);
                Assert.False(settings.AgentAllowLocalPython);
                Assert.Equal(50, settings.AgentMaxRows);
                Assert.Equal("C:\\x\\omp.exe", settings.AgentOmpPath);
                Assert.Equal("--foo", settings.AgentExtraArgs);
            });
        }

        [Fact]
        public void A_locked_approval_mode_cannot_be_changed_from_the_ai_page()
        {
            var settings = new AppSettings { AgentExtraArgs = "--yolo", AgentApprovalMode = "always-ask" };
            OnForm(settings, form =>
            {
                form.ApplyAgentSettings(new Form1.AgentSettingsInput(false, AgentApprovalMode.Write, AgentDataPolicy.SummaryOnly, false, ApprovalEditable: false, 200, "", "--yolo"));
                Assert.Equal("always-ask", settings.AgentApprovalMode);              // omp 인자가 고정한 모드는 앱 설정으로 못 바꾼다
            });
        }
    }
}
