using System.Globalization;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 메뉴·툴바·우클릭 메뉴·단축키의 동작 검증(실제 Form1, 보이지 않는 창).
    //  - 10개 최상위 메뉴와 단축키 표가 곧 메뉴 구조: 모든 명령이 메뉴에서 닿고, 같은 키가 두 명령에 겹치지 않는다.
    //  - 단축키는 올바른 동작을 일으킨다(정렬 해제 Ctrl+Alt+S · 필터 해제 · 패널 토글 · 설정 · 고급 필터).
    //  - 우클릭 메뉴(셀 · 컬럼 헤더 · 행 헤더 · 탭)는 시트 편집 모드 여부에 따라 항목이 켜지고 꺼진다.
    //  - 언어를 바꾸면 모든 메뉴 항목과 툴바 툴팁이 다시 라벨링된다.
    [Collection("SavedViewStore")]
    public class MenuStructureTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_menus_" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _paths = new();
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

        public MenuStructureTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            foreach (string p in _paths)
                try { if (EditJournal.KeyFor(p) is { } key) EditJournal.Delete(EditJournal.DefaultDirectory, key); } catch { }
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ------------------------------------------------------------------------------------------ 도우미

        private string MakeCsv(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            _paths.Add(path);
            return path;
        }

        private static T Get<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;
        private static object? Invoke(Form1 f, string method, params object[] args) => typeof(Form1).GetMethod(method, Inst)!.Invoke(f, args);
        private static DataGridView GridOf(Form1 f) => f.Controls.Find("grid", true).OfType<DataGridView>().Single();
        private static string T(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        private static void Pump(Task task)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(60)) { Application.DoEvents(); Thread.Sleep(1); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Assert.True(task.IsCompleted, "task did not complete");
            task.GetAwaiter().GetResult();
        }

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 30)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { Application.DoEvents(); Thread.Sleep(2); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Assert.True(condition(), "timed out waiting for: " + what);
        }

        private void OnForm(Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    using var form = new Form1(new AppSettings());
                    _ = form.Handle;
                    form.SheetEditConfirm = () => true;   // 시트 편집 켜기 확인 대화상자는 테스트에서 자동 승인
                    body(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(180)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // 파일을 새 탭으로 열고 인덱싱이 끝나고 첫 행이 그려질 때까지 기다린다. 현재 셀은 (0,0).
        private static DocumentTab Open(Form1 f, string path)
        {
            var task = f.OpenFileTabAsync(path);
            Pump(task);
            var tab = task.Result;
            Assert.NotNull(tab);
            PumpUntil(() => tab!.Document is { IndexingComplete: true } && !tab.IsIndexing && !Get<bool>(f, "_busy") && GridOf(f).RowCount > 0, "tab indexing to finish");
            var grid = GridOf(f);
            grid.CurrentCell = grid[0, 0];
            return tab!;
        }

        private const string Csv = "id,name,score,joined\n1,kim,30,2024-03-01\n2,lee,10,2023-01-15\n3,park,20,2024-07-09\n4,choi,40,\n";

        private static IEnumerable<ToolStripItem> Walk(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                yield return item;
                if (item is ToolStripDropDownItem d)
                    foreach (var child in Walk(d.DropDownItems)) yield return child;
            }
        }

        private static string Plain(ToolStripItem item) => item.Text.Replace("&&", "&");

        private static ToolStripMenuItem Find(IEnumerable<ToolStripItem> items, string en, string ko)
            => items.OfType<ToolStripMenuItem>().Single(i => Plain(i) == en || Plain(i) == ko);

        private static ToolStripMenuItem Find(ToolStripItemCollection items, string en, string ko) => Find(Walk(items), en, ko);

        // 메시지 루프를 돌고 있는 동안(모달 대화상자 안) 열린 대화상자를 검사하고 닫는다.
        private static bool WithModal(Form1 owner, Action trigger, Action<Form> inspect)
        {
            bool seen = false;
            var timer = new System.Windows.Forms.Timer { Interval = 80 };
            timer.Tick += (_, _) =>
            {
                var dlg = Application.OpenForms.Cast<Form>().FirstOrDefault(x => ReferenceEquals(x.Owner, owner) && x.Visible);
                if (dlg is null) return;
                timer.Stop();
                seen = true;
                try { inspect(dlg); }
                finally { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); }
            };
            timer.Start();
            try { trigger(); }
            finally { timer.Stop(); timer.Dispose(); }
            return seen;
        }

        // 키 입력: 먼저 폼의 ProcessCmdKey(탭 전환·그리드 키 등 메뉴에 묶이지 않은 키), 아니면 그 키가 묶인 메뉴 항목을 누른다
        // (보이지 않는 창에서는 ToolStrip 단축키 경로가 동작하지 않으므로, "이 키 → 이 항목 → 이 핸들러" 연결을 직접 검증한다).
        private static bool Press(Form1 f, Keys keys)
        {
            var msg = new Message { HWnd = f.Handle, Msg = 0x0100, WParam = (IntPtr)(int)(keys & Keys.KeyCode) };
            var method = typeof(Form1).GetMethod("ProcessCmdKey", Inst)!;
            if ((bool)method.Invoke(f, new object[] { msg, keys })!) return true;
            var item = Walk(f.MenuStripForTests().Items).OfType<ToolStripMenuItem>().FirstOrDefault(i => i.ShortcutKeys == keys);
            if (item is null || !item.Enabled) return false;
            item.PerformClick();
            return true;
        }

        // ------------------------------------------------------------------------------------------ 메뉴 구조

        [Fact]
        public void The_menu_bar_has_the_approved_ten_top_level_menus_in_order()
        {
            OnForm(form =>
            {
                Assert.Equal(
                    new[] { "fileMenu", "editMenu", "viewMenu", "dataMenu", "workspaceMenu", "statisticsMenu", "visualizationMenu", "qualityMenu", "toolsMenu", "helpMenu" },
                    form.TopLevelMenus.Select(m => m.Name).ToArray());
            });
        }

        [Fact]
        public void Every_shortcut_table_command_is_reachable_from_a_menu_item_that_shows_and_binds_its_key()
        {
            OnForm(form =>
            {
                var items = Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>().ToList();
                foreach (var entry in CommandShortcuts.All)
                {
                    var matches = items.Where(i => (i.Tag as string) == entry.Id).ToList();
                    Assert.True(matches.Count == 1, $"{entry.Id}: expected exactly one menu item, found {matches.Count}");
                    var item = matches[0];
                    Assert.Equal(entry.KeyText, item.ShortcutKeyDisplayString);
                    Assert.Equal(entry.DisplayOnly ? Keys.None : entry.Keys, item.ShortcutKeys);
                }
            });
        }

        [Fact]
        public void No_two_commands_share_a_shortcut_and_the_approved_changes_are_in_the_table()
        {
            var duplicates = CommandShortcuts.AllBindings().GroupBy(b => b.Keys).Where(g => g.Select(x => x.Id).Distinct().Count() > 1)
                .Select(g => CommandShortcuts.Display(g.Key) + " → " + string.Join(", ", g.Select(x => x.Id))).ToList();
            Assert.Empty(duplicates);

            Assert.Equal(Keys.Control | Keys.Oemcomma, CommandShortcuts.KeysOf("tools.settings"));
            Assert.Equal(Keys.Control | Keys.S, CommandShortcuts.KeysOf("file.saveWorkspace"));
            Assert.Equal(Keys.Control | Keys.Shift | Keys.F, CommandShortcuts.KeysOf("data.advFilter"));
            Assert.Equal(Keys.Control | Keys.Alt | Keys.S, CommandShortcuts.KeysOf("data.clearSort"));
            Assert.DoesNotContain(CommandShortcuts.AllBindings(), b => b.Keys == (Keys.Control | Keys.Shift | Keys.S)); // 옛 정렬 해제 키는 비운다
            Assert.Equal("Ctrl+,", CommandShortcuts.Display(Keys.Control | Keys.Oemcomma));
            Assert.Equal("Ctrl+Shift+F", CommandShortcuts.Display(Keys.Control | Keys.Shift | Keys.F));
        }

        [Fact]
        public void The_workspace_explorer_has_one_view_menu_entry_and_one_toolbar_toggle_and_none_in_the_workspace_menu()
        {
            OnForm(form =>
            {
                var explorerItems = Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>().Where(i => (i.Tag as string) == "view.explorer").ToList();
                Assert.Single(explorerItems);
                Assert.Same(form.TopLevelMenus.Single(m => m.Name == "viewMenu"), explorerItems[0].OwnerItem);
                Assert.DoesNotContain(Walk(form.WorkspaceMenu.DropDownItems), i => (i.Tag as string) == "view.explorer");
                Assert.Single(form.ToolbarItems(), i => i.Name == "workspaceToolButton");
            });
        }

        [Fact]
        public void Statistics_merges_basic_and_advanced_analysis_and_visualization_has_no_single_item_submenus()
        {
            OnForm(form =>
            {
                var stats = form.TopLevelMenus.Single(m => m.Name == "statisticsMenu");
                var subs = stats.DropDownItems.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems).ToList();
                Assert.Equal(6, subs.Count); // 기본 · 모형 · 비모수 · 생존 · 분류·군집 · 차원축소
                var viz = form.TopLevelMenus.Single(m => m.Name == "visualizationMenu");
                // 최근 작업 공간은 열 때 채우는 동적 목록이라 비었을 때 자리 표시 항목 하나만 있다.
                var recent = Get<ToolStripMenuItem>(form, "_wfRecentMenu");
                foreach (var sub in Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems && !ReferenceEquals(i, recent)))
                    Assert.True(sub.DropDownItems.OfType<ToolStripMenuItem>().Count() > 1, $"'{sub.Text}' is a submenu with a single item");
                Assert.NotEmpty(viz.DropDownItems);
            });
        }

        [Fact]
        public void Switching_the_language_relabels_every_menu_item_and_the_toolbar_tooltips()
        {
            OnForm(form =>
            {
                var registered = Get<System.Collections.IEnumerable>(form, "_featureLabels").Cast<object>()
                    .Select(t => (ToolStripItem)t.GetType().GetField("Item1")!.GetValue(t)!).ToHashSet();
                var tree = Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>().ToList();
                var dynamic = new HashSet<ToolStripItem>();
                foreach (var host in new ToolStripMenuItem?[] { Get<ToolStripMenuItem>(form, "encodingMenuItem"), Get<ToolStripMenuItem>(form, "languageMenuItem"), Get<ToolStripMenuItem>(form, "_wfRecentMenu"), Get<ToolStripMenuItem>(form, "_columnTypeMenu") })
                    if (host is not null) foreach (var d in Walk(host.DropDownItems)) dynamic.Add(d);
                // 글자가 코드에서 오는 동적 항목(인코딩 이름 · 언어 이름 · 최근 목록)이 아니면 모두 RegisterLabel로 등록돼 있어야 언어 전환에 따라간다.
                var unregistered = tree.Where(i => !registered.Contains(i) && !dynamic.Contains(i)).Select(Plain).ToList();
                Assert.Empty(unregistered);

                var apply = typeof(Form1).GetMethod("ApplyLocalization", Inst)!;
                var original = Thread.CurrentThread.CurrentUICulture;
                try
                {
                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("en");
                    apply.Invoke(form, null);
                    var english = tree.Where(registered.Contains).ToDictionary(i => i, Plain);
                    string openTip = form.ToolbarItems().Single(i => i.Name == "openToolStripButton").ToolTipText!;
                    Assert.Equal("Open (Ctrl+O)", openTip);

                    Thread.CurrentThread.CurrentUICulture = new CultureInfo("ko");
                    apply.Invoke(form, null);
                    int changed = english.Count(kv => Plain(kv.Key) != kv.Value);
                    Assert.True(changed > english.Count / 2, $"only {changed}/{english.Count} menu items changed text when switching to Korean");
                    Assert.Equal("파일", Plain(form.TopLevelMenus.Single(m => m.Name == "fileMenu")));
                    Assert.Equal("작업 공간", Plain(form.TopLevelMenus.Single(m => m.Name == "workspaceMenu")));
                    Assert.Equal("통계", Plain(form.TopLevelMenus.Single(m => m.Name == "statisticsMenu")));
                    Assert.Equal("열기 (Ctrl+O)", form.ToolbarItems().Single(i => i.Name == "openToolStripButton").ToolTipText);
                    Assert.Equal("설정 (Ctrl+,)", form.ToolbarItems().Single(i => i.Name == "settingsButton").ToolTipText);
                    // 같은 기능은 같은 이름: 메뉴와 탭 우클릭 메뉴
                    Assert.Equal("탭 닫기", Plain(Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>().Single(i => (i.Tag as string) == "file.closeTab")));
                }
                finally
                {
                    Thread.CurrentThread.CurrentUICulture = original;
                    apply.Invoke(form, null);
                }
            });
        }

        // ------------------------------------------------------------------------------------------ 툴바

        [Fact]
        public void The_toolbar_groups_are_in_the_approved_order_with_shortcut_tooltips_and_glyph_icons_that_follow_the_theme()
        {
            OnForm(form =>
            {
                var items = form.ToolbarItems();
                string[] left =
                {
                    "openToolStripButton", "saveWorkspaceButton", "|", "undoButton", "redoButton", "|",
                    "findLabel", "findTextBox", "findNextButton", "|",
                    "filterColumnLabel", "filterColumnCombo", "filterTextBox", "applyFilterButton", "clearFilterButton", "|",
                    "sortAscButton", "sortDescButton", "clearSortButton", "|", "editCellButton", "editSheetButton", "|", "cfButton",
                };
                string[] right = { "settingsButton", "|", "agentToggleButton", "detailToggleButton", "workspaceToolButton" };
                Func<ToolStripItem, string> name = i => i is ToolStripSeparator ? "|" : i.Name;
                Assert.Equal(left.Concat(right), items.Select(name));
                // 오른쪽 그룹은 우측 정렬(나중에 추가한 것이 왼쪽): 화면에서는 [탐색기][행 상세][AI] | [설정]
                Assert.All(items.Skip(left.Length), i => Assert.Equal(ToolStripItemAlignment.Right, i.Alignment));
                Assert.All(items.Take(left.Length), i => Assert.Equal(ToolStripItemAlignment.Left, i.Alignment));

                // 툴팁은 "이름 (단축키)" — 표의 단축키와 같다.
                void Tip(string toolbar, string command)
                    => Assert.EndsWith("(" + CommandShortcuts.Get(command).KeyText + ")", items.Single(i => i.Name == toolbar).ToolTipText);
                Tip("openToolStripButton", "file.open"); Tip("saveWorkspaceButton", "file.saveWorkspace"); Tip("undoButton", "edit.undo");
                Tip("redoButton", "edit.redo"); Tip("findNextButton", "data.findNext"); Tip("clearFilterButton", "data.clearFilter");
                Tip("clearSortButton", "data.clearSort"); Tip("editCellButton", "edit.cell"); Tip("editSheetButton", "edit.sheet");
                Tip("settingsButton", "tools.settings"); Tip("agentToggleButton", "view.ai"); Tip("detailToggleButton", "view.detail");
                Tip("workspaceToolButton", "view.explorer");

                if (IconGlyphs.FontName is null) return; // 아이콘 글꼴이 없는 환경: 글자 버튼으로 대체되어 이미지 검사는 의미 없다.
                int px = IconGlyphs.PixelSize(form.DeviceDpi);
                foreach (var button in items.OfType<ToolStripButton>())
                {
                    Assert.NotNull(button.Image);
                    Assert.Equal(new Size(px, px), button.Image!.Size);
                }

                // 테마가 바뀌면 글리프 색이 새 글자색으로 다시 그려진다.
                var open = items.Single(i => i.Name == "openToolStripButton");
                form.ApplyThemeSetting("Light");
                Color light = InkOf((Bitmap)open.Image!);
                form.ApplyThemeSetting("Dark");
                Color dark = InkOf((Bitmap)open.Image!);
                Assert.True(Math.Abs(light.R - ThemePalette.Light.Text.R) <= 2, $"light ink {light}");
                Assert.True(Math.Abs(dark.R - ThemePalette.Dark.Text.R) <= 2, $"dark ink {dark}");
                form.ApplyThemeSetting("Light");
            });
        }

        private static Color InkOf(Bitmap bmp)
        {
            Color best = Color.Transparent;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.A > best.A) best = c;
                }
            Assert.True(best.A > 150, "glyph drew no opaque pixel");
            return best;
        }

        [Fact]
        public void Toolbar_state_follows_the_document_undo_history_and_sheet_edit_mode()
        {
            OnForm(form =>
            {
                var items = form.ToolbarItems();
                var undo = (ToolStripButton)items.Single(i => i.Name == "undoButton");
                var sheet = (ToolStripButton)items.Single(i => i.Name == "editSheetButton");
                var editCell = (ToolStripButton)items.Single(i => i.Name == "editCellButton");
                Assert.False(undo.Enabled); Assert.False(sheet.Enabled); Assert.False(items.Single(i => i.Name == "sortAscButton").Enabled);

                Open(form, MakeCsv("a.csv", Csv));
                Assert.True(sheet.Enabled); Assert.True(editCell.Enabled); Assert.False(undo.Enabled);
                Assert.True(items.Single(i => i.Name == "sortAscButton").Enabled);

                Invoke(form, "SetSheetEditing", true, false);
                Assert.True(sheet.Checked);
                Assert.False(editCell.Enabled); // 시트 편집 중에는 F2·더블클릭으로 인라인 편집
                var doc = Get<VirtualCsvDocument>(form, "_doc");
                doc.Edits.Set(doc.GetRowId(0), 1, "kim2", "kim");
                Invoke(form, "UpdateFeatureState");
                Assert.True(undo.Enabled);
                Invoke(form, "SetSheetEditing", false, false);
                Assert.False(sheet.Checked);
            });
        }

        // ------------------------------------------------------------------------------------------ 단축키

        [Fact]
        public void Ctrl_Alt_S_clears_the_sort_and_the_old_Ctrl_Shift_S_no_longer_does()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                var doc = Get<VirtualCsvDocument>(form, "_doc");
                Invoke(form, "SortColumn", 2, true);
                PumpUntil(() => Get<System.Collections.IList>(form, "_sortKeys").Count == 1 && !Get<bool>(form, "_busy"), "sort");
                Assert.Equal("10", doc.GetDisplayRow(0)[2]);

                Assert.False(Press(form, Keys.Control | Keys.Shift | Keys.S));
                Assert.Equal(1, Get<System.Collections.IList>(form, "_sortKeys").Count);

                Assert.True(Press(form, Keys.Control | Keys.Alt | Keys.S));
                Assert.Equal(0, Get<System.Collections.IList>(form, "_sortKeys").Count);
                Assert.Equal("30", doc.GetDisplayRow(0)[2]); // 파일 순서로 복원
            });
        }

        [Fact]
        public void Ctrl_Shift_L_clears_the_filter_and_F4_Ctrl_Shift_W_toggle_their_panels()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                var doc = Get<VirtualCsvDocument>(form, "_doc");
                Pump(form.FilterByCellAsync(1, 1, Form1.CellFilterOp.Equals));
                Assert.Equal(1, doc.DisplayRowCount);
                Assert.True(Press(form, Keys.Control | Keys.Shift | Keys.L));
                Assert.Equal(4, doc.DisplayRowCount);

                bool detail = form.IsPanelVisible(PanelKind.Detail);
                Assert.True(Press(form, Keys.F4));
                Assert.Equal(!detail, form.IsPanelVisible(PanelKind.Detail));
                Assert.Equal(!detail, form.ToolbarItems().OfType<ToolStripButton>().Single(b => b.Name == "detailToggleButton").Checked); // 툴바도 같이
                Assert.True(Press(form, Keys.F4));
                Assert.Equal(detail, form.IsPanelVisible(PanelKind.Detail));

                bool explorer = form.IsPanelVisible(PanelKind.Explorer);
                Assert.True(Press(form, Keys.Control | Keys.Shift | Keys.W));
                Assert.Equal(!explorer, form.IsPanelVisible(PanelKind.Explorer));
                Assert.Equal(!explorer, form.ToolbarItems().OfType<ToolStripButton>().Single(b => b.Name == "workspaceToolButton").Checked);
                Assert.True(Press(form, Keys.Control | Keys.Shift | Keys.W));
            });
        }

        [Fact]
        public void Ctrl_Comma_opens_the_settings_dialog_and_Ctrl_Shift_F_opens_the_advanced_filter()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                string? opened = null;
                Assert.True(WithModal(form, () => Press(form, Keys.Control | Keys.Oemcomma), d => opened = d.GetType().Name));
                Assert.Equal("SettingsDialog", opened);

                opened = null;
                Assert.True(WithModal(form, () => Press(form, Keys.Control | Keys.Shift | Keys.F), d => opened = d.GetType().Name));
                Assert.Equal("AdvancedFilterDialog", opened);

                // 설정 버튼도 같은 대화상자를 연다.
                opened = null;
                Assert.True(WithModal(form, () => ((ToolStripButton)form.ToolbarItems().Single(i => i.Name == "settingsButton")).PerformClick(), d => opened = d.GetType().Name));
                Assert.Equal("SettingsDialog", opened);
            });
        }

        [Fact]
        public void Save_Workspace_without_a_workspace_file_asks_for_a_name_like_Save_As()
        {
            // Ctrl+S는 작업 공간 저장 항목에 묶여 있다. 파일이 없으면 SaveWorkspace(saveAs:false)가 이름을 묻는다(파일 대화상자라 테스트에서는 항목·키 바인딩과 상태만 확인).
            OnForm(form =>
            {
                var save = Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>().Single(i => (i.Tag as string) == "file.saveWorkspace");
                Assert.Equal(Keys.Control | Keys.S, save.ShortcutKeys);
                Assert.True(save.Enabled);
                Assert.Null(form.WorkspaceFilePath);
                Assert.Contains(Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>(), i => Plain(i) == T("Save Workspace As…", "작업 공간을 다른 이름으로 저장…"));
            });
        }

        // ------------------------------------------------------------------------------------------ 우클릭 메뉴

        private static ToolStripMenuItem[] Edits(ContextMenuStrip menu, params (string en, string ko)[] names)
            => names.Select(n => Find(menu.Items, n.en, n.ko)).ToArray();

        [Fact]
        public void The_cell_menu_always_lists_edit_items_but_enables_them_only_in_sheet_edit_mode()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                var grid = GridOf(form);
                grid.CurrentCell = grid[1, 1];

                using var view = form.BuildCellContextMenu(1, 1);
                var items = Walk(view.Items).OfType<ToolStripMenuItem>().ToList();
                string[] editNames = { "Paste Cells|셀 붙여넣기", "Clear Selected Cells|선택한 셀 지우기", "Find & Replace (regex)…|찾아 바꾸기 (정규식)…",
                    "Insert Row Above|위에 행 삽입", "Insert Row Below|아래에 행 삽입", "Delete Selected Rows|선택한 행 삭제",
                    "Insert Column…|컬럼 삽입…", "Move Column Left|컬럼 왼쪽으로 이동", "Move Column Right|컬럼 오른쪽으로 이동", "Delete Column|컬럼 삭제" };
                foreach (string n in editNames)
                {
                    var parts = n.Split('|');
                    Assert.False(Find(items, parts[0], parts[1]).Enabled, $"{parts[0]} must be disabled outside sheet edit mode");
                }
                Assert.True(Find(items, "Turn On Sheet Edit", "시트 편집 켜기").Enabled);
                Assert.True(Find(items, "Edit Cell…", "셀 편집…").Enabled);
                Assert.True(Find(items, "Copy", "복사").Enabled);
                Assert.Equal("Ctrl+C", Find(items, "Copy", "복사").ShortcutKeyDisplayString);
                // 필터 ▸ 이 값 · 제외 · ≥ · ≤ 는 읽기 모드에서도 켜져 있다.
                Assert.True(Find(items, "Filter by This Cell Value", "이 셀 값으로 필터").Enabled);
                Assert.True(Find(items, "Exclude This Value", "이 값 제외").Enabled);
                Assert.True(Find(items, "≥ This Value", "이 값 이상 (≥)").Enabled);
                Assert.True(Find(items, "≤ This Value", "이 값 이하 (≤)").Enabled);
                Assert.True(Find(items, "Hide Column", "컬럼 숨기기").Enabled);
                Assert.True(Find(items, "Summarize This Column", "이 컬럼 요약").Enabled);

                Invoke(form, "SetSheetEditing", true, false);
                using var sheet = form.BuildCellContextMenu(1, 1);
                var sheetItems = Walk(sheet.Items).OfType<ToolStripMenuItem>().ToList();
                foreach (string n in editNames.Where(n => !n.StartsWith("Move Column")))
                {
                    var parts = n.Split('|');
                    Assert.True(Find(sheetItems, parts[0], parts[1]).Enabled, $"{parts[0]} must be enabled in sheet edit mode");
                }
                Assert.True(Find(sheetItems, "Turn Off Sheet Edit", "시트 편집 끄기").Enabled);
                Assert.False(Find(sheetItems, "Edit Cell…", "셀 편집…").Enabled);
                Invoke(form, "SetSheetEditing", false, false);
            });
        }

        [Fact]
        public void The_column_header_menu_offers_sort_filter_hide_freeze_autofit_type_and_quick_analysis_and_edit_items_by_mode()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                using var view = form.BuildColumnHeaderMenu(2)!;
                var items = Walk(view.Items).OfType<ToolStripMenuItem>().ToList();
                foreach (var (en, ko) in new[] { ("Sort Ascending", "오름차순 정렬"), ("Sort Descending", "내림차순 정렬"), ("Filter…", "필터…"),
                    ("Hide Column", "컬럼 숨기기"), ("Freeze Columns up to Here", "여기까지 열 고정"), ("Auto-fit Width", "너비 자동 맞춤"),
                    ("Change Type", "타입 변경"), ("Quick Analysis", "빠른 분석"), ("Descriptive Statistics…", "기술통계…"),
                    ("Frequency Table…", "빈도분석…"), ("Numeric Distribution…", "수치 분포…") })
                    Assert.True(Find(items, en, ko).Enabled, en);
                Assert.False(Find(items, "Unfreeze Columns", "열 고정 해제").Enabled); // 고정한 적이 없다
                Assert.False(Find(items, "Rename Column…", "컬럼 이름 변경…").Enabled);
                Assert.False(Find(items, "Delete Column", "컬럼 삭제").Enabled);
                Assert.True(Find(items, "Turn On Sheet Edit", "시트 편집 켜기").Enabled);

                Invoke(form, "SetSheetEditing", true, false);
                using var sheet = form.BuildColumnHeaderMenu(2)!;
                var sheetItems = Walk(sheet.Items).OfType<ToolStripMenuItem>().ToList();
                Assert.True(Find(sheetItems, "Rename Column…", "컬럼 이름 변경…").Enabled);
                Assert.True(Find(sheetItems, "Insert Column…", "컬럼 삽입…").Enabled);
                Assert.True(Find(sheetItems, "Delete Column", "컬럼 삭제").Enabled);
                Assert.DoesNotContain(sheetItems, i => Plain(i) is "Turn On Sheet Edit" or "시트 편집 켜기");
                Invoke(form, "SetSheetEditing", false, false);
            });
        }

        [Fact]
        public void The_row_header_menu_acts_on_the_clicked_row_and_enables_row_edits_only_in_sheet_edit_mode()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                using var view = form.BuildRowHeaderMenu(2);
                Assert.True(Find(view.Items, "Copy Row", "행 복사").Enabled);
                Assert.True(Find(view.Items, "Show Row Detail", "행 상세 보기").Enabled);
                Assert.False(Find(view.Items, "Insert Row Above", "위에 행 삽입").Enabled);
                Assert.False(Find(view.Items, "Delete Row", "행 삭제").Enabled);

                // 행 상세 보기: 그 행을 현재 행으로 삼고 행 상세 패널을 연다.
                Find(view.Items, "Show Row Detail", "행 상세 보기").PerformClick();
                Assert.Equal(2, GridOf(form).CurrentCell!.RowIndex);
                Assert.True(form.IsPanelVisible(PanelKind.Detail));
                form.SetPanelVisible(PanelKind.Detail, false);

                Invoke(form, "SetSheetEditing", true, false);
                using var sheet = form.BuildRowHeaderMenu(2);
                Assert.True(Find(sheet.Items, "Insert Row Above", "위에 행 삽입").Enabled);
                Assert.True(Find(sheet.Items, "Insert Row Below", "아래에 행 삽입").Enabled);
                Assert.True(Find(sheet.Items, "Delete Row", "행 삭제").Enabled);
                // 삭제는 우클릭한 행에 작용한다(현재 셀이 다른 행이어도).
                GridOf(form).CurrentCell = GridOf(form)[0, 0];
                Find(sheet.Items, "Delete Row", "행 삭제").PerformClick();
                var doc = Get<VirtualCsvDocument>(form, "_doc");
                Assert.Equal(3, doc.DisplayRowCount);
                Assert.DoesNotContain(Enumerable.Range(0, doc.DisplayRowCount), r => doc.GetDisplayRow(r)[1] == "park");
                Invoke(form, "SetSheetEditing", false, false);
            });
        }

        [Fact]
        public void The_tab_menu_uses_the_same_names_as_the_menu_bar()
        {
            OnForm(form =>
            {
                var a = Open(form, MakeCsv("a.csv", Csv));
                Open(form, MakeCsv("b.csv", "x\n1\n"));
                using var tabMenu = form.BuildTabContextMenu(a);
                var bar = Walk(form.MenuStripForTests().Items).OfType<ToolStripMenuItem>().Select(Plain).ToHashSet();
                foreach (var item in tabMenu.Items.OfType<ToolStripMenuItem>().Where(i => Plain(i) is "Close Tab" or "탭 닫기" or "Close Other Tabs" or "다른 탭 닫기" or "Close All Tabs" or "모든 탭 닫기"))
                    Assert.Contains(Plain(item), bar);
                Assert.True(Find(tabMenu.Items, "Close Other Tabs", "다른 탭 닫기").Enabled);
                Assert.Equal("Ctrl+W", Find(tabMenu.Items, "Close Tab", "탭 닫기").ShortcutKeyDisplayString);
            });
        }

        // ------------------------------------------------------------------------------------------ 우클릭 동작

        [Fact]
        public void Cell_filters_include_exclude_and_compare_by_the_column_type()
        {
            // 같음 · 제외: 정확 문자열. ≥ ≤: 숫자는 수치로, 날짜 컬럼은 날짜로, 그 외는 문자열 순서로. 비교할 수 없는 셀(빈 값)은 ≥ ≤에 일치하지 않는다.
            string[] kim = { "1", "kim", "30", "2024-03-01" }, lee = { "2", "lee", "10", "2023-01-15" }, park = { "3", "park", "9", "2024-07-09" }, blank = { "4", "choi", "", "" };
            var rows = new[] { kim, lee, park, blank };
            string[] Names(Func<string[], bool> p) => rows.Where(p).Select(r => r[1]).ToArray();

            Assert.Equal(new[] { "kim" }, Names(Form1.BuildCellPredicate(1, "kim", Form1.CellFilterOp.Equals, ColumnValueType.String)));
            Assert.Equal(new[] { "lee", "park", "choi" }, Names(Form1.BuildCellPredicate(1, "kim", Form1.CellFilterOp.NotEquals, ColumnValueType.String)));
            // 수치: "9" < "10" (문자열 비교였다면 9가 더 크다)
            Assert.Equal(new[] { "kim", "lee" }, Names(Form1.BuildCellPredicate(2, "10", Form1.CellFilterOp.AtLeast, ColumnValueType.Integer)));
            Assert.Equal(new[] { "lee", "park" }, Names(Form1.BuildCellPredicate(2, "10", Form1.CellFilterOp.AtMost, ColumnValueType.Integer)));
            // 날짜
            Assert.Equal(new[] { "kim", "park" }, Names(Form1.BuildCellPredicate(3, "2024-01-01", Form1.CellFilterOp.AtLeast, ColumnValueType.Date)));
            Assert.Equal(new[] { "lee" }, Names(Form1.BuildCellPredicate(3, "2024-01-01", Form1.CellFilterOp.AtMost, ColumnValueType.Date)));
            // 문자열 순서
            Assert.Equal(new[] { "lee", "park" }, Names(Form1.BuildCellPredicate(1, "lee", Form1.CellFilterOp.AtLeast, ColumnValueType.String)));
        }

        [Fact]
        public void Filtering_from_the_cell_menu_narrows_the_view_and_each_filter_stacks_with_AND()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                var doc = Get<VirtualCsvDocument>(form, "_doc");
                Pump(form.FilterByCellAsync(2, 2, Form1.CellFilterOp.AtLeast)); // score >= 20  (park)
                Assert.Equal(3, doc.DisplayRowCount);
                Pump(form.FilterByCellAsync(0, 1, Form1.CellFilterOp.NotEquals)); // 남은 뷰의 첫 행(kim) 제외
                Assert.Equal(2, doc.DisplayRowCount);
                var names = Enumerable.Range(0, 2).Select(r => doc.GetDisplayRow(r)[1]).OrderBy(x => x).ToArray();
                Assert.Equal(new[] { "choi", "park" }, names);
                Pump(form.FilterByCellAsync(0, 2, Form1.CellFilterOp.AtMost)); // 다시 첫 행 이하
                Assert.True(doc.DisplayRowCount >= 1);
                Invoke(form, "OnClearFilterClick", null!, EventArgs.Empty);
                Assert.Equal(4, doc.DisplayRowCount);
            });
        }

        [Fact]
        public void Ask_AI_items_send_prefilled_prompts_that_name_the_column_and_value()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                var sent = new List<string>();
                form.AskAiSink = sent.Add;
                using var menu = form.BuildCellContextMenu(1, 1);
                Find(menu.Items, "Summarize This Column", "이 컬럼 요약").PerformClick();
                Find(menu.Items, "Analyze Rows with This Value", "이 값을 가진 행 분석").PerformClick();
                Assert.Equal(2, sent.Count);
                Assert.Contains("name", sent[0]);
                Assert.Contains("name", sent[1]);
                Assert.Contains("lee", sent[1]);
            });
        }

        [Fact]
        public void Hide_freeze_and_autofit_from_the_header_menu_and_the_frozen_columns_survive_column_edits_and_tab_switches()
        {
            OnForm(form =>
            {
                var tabA = Open(form, MakeCsv("a.csv", "a,b,c,d\n1,2,3,4\n5,6,7,8\n"));
                var grid = GridOf(form);

                // 자동 맞춤: 넓혀 둔 열이 내용에 맞게 줄어든다.
                grid.Columns[1].Width = 400;
                Invoke(form, "AutoFitColumn", 1);
                Assert.InRange(grid.Columns[1].Width, 50, 399);

                // 열 고정: 선택한 열까지 왼쪽 열이 모두 고정된다. 해제하면 모두 풀린다.
                Invoke(form, "FreezeColumnsUpTo", 1);
                Assert.Equal(2, form.FrozenColumnCount);
                Assert.True(grid.Columns[0].Frozen && grid.Columns[1].Frozen);
                Assert.False(grid.Columns[2].Frozen);
                Invoke(form, "UnfreezeColumns");
                Assert.Equal(0, form.FrozenColumnCount);
                Assert.False(grid.Columns[0].Frozen || grid.Columns[1].Frozen);
                Invoke(form, "FreezeColumnsUpTo", 1);

                // 숨기기는 고정과 함께 간다. 마지막 보이는 열은 숨기지 않는다.
                Invoke(form, "HideColumn", 3);
                Assert.False(grid.Columns[3].Visible);
                Assert.True(grid.Columns[0].Frozen && grid.Columns[1].Frozen);
                Invoke(form, "HideColumn", 2); Invoke(form, "HideColumn", 1);
                Invoke(form, "HideColumn", 0);
                Assert.Equal(1, grid.Columns.Cast<DataGridViewColumn>().Count(c => c.Visible));
                grid.Columns[1].Visible = true; grid.Columns[2].Visible = true; grid.Columns[3].Visible = true;
                Get<HashSet<int>>(form, "_hiddenColumns").Clear();

                // 컬럼 이동·삭제(시트 편집): 고정 열 수는 유지되고, 고정 영역의 열이 지워지면 그만큼 줄어든다.
                Invoke(form, "SetSheetEditing", true, false);
                Invoke(form, "MoveColumnFromUi", 3, 0);
                Pump(Task.Delay(50));
                Assert.Equal(2, form.FrozenColumnCount);
                Assert.True(grid.Columns[0].Frozen && grid.Columns[1].Frozen && !grid.Columns[2].Frozen);
                Invoke(form, "DeleteColumnFromUi", 0);
                Assert.Equal(1, form.FrozenColumnCount);
                Assert.True(grid.Columns[0].Frozen && !grid.Columns[1].Frozen);
                Invoke(form, "SetSheetEditing", false, false);

                // 탭마다 따로 기억한다.
                var tabB = Open(form, MakeCsv("b.csv", "x,y,z\n1,2,3\n"));
                Assert.Equal(0, form.FrozenColumnCount);
                Assert.DoesNotContain(grid.Columns.Cast<DataGridViewColumn>(), c => c.Frozen);
                Pump(form.ActivateTabAsync(tabA));
                Assert.Equal(1, form.FrozenColumnCount);
                Assert.True(grid.Columns[0].Frozen);
                Assert.Same(tabA, form.ActiveTab);
                Assert.NotSame(tabA, tabB);
            });
        }

        [Fact]
        public void Quick_analysis_opens_the_analysis_dialog_preselected_on_the_header_column()
        {
            OnForm(form =>
            {
                Open(form, MakeCsv("a.csv", Csv));
                using var menu = form.BuildColumnHeaderMenu(2)!;
                var quick = Find(menu.Items, "Quick Analysis", "빠른 분석");

                int selected = -1;
                Assert.True(WithModal(form, () => Find(quick.DropDownItems, "Frequency Table…", "빈도분석…").PerformClick(),
                    d => selected = AllControls(d).OfType<ComboBox>().First().SelectedIndex));
                Assert.Equal(2, selected);

                selected = -1;
                Assert.True(WithModal(form, () => Find(quick.DropDownItems, "Numeric Distribution…", "수치 분포…").PerformClick(),
                    d => selected = AllControls(d).OfType<ComboBox>().First().SelectedIndex));
                Assert.Equal(2, selected);

                int[] checkedColumns = Array.Empty<int>();
                Assert.True(WithModal(form, () => Find(quick.DropDownItems, "Descriptive Statistics…", "기술통계…").PerformClick(),
                    d => checkedColumns = AllControls(d).OfType<CheckedListBox>().First().CheckedIndices.Cast<int>().ToArray()));
                Assert.Equal(new[] { 2 }, checkedColumns);
            });
        }

        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in AllControls(c)) yield return d;
            }
        }
    }

    internal static class MenuStructureTestExtensions
    {
        public static MenuStrip MenuStripForTests(this Form1 form) => form.MainMenuStrip!;

        public static IReadOnlyList<ToolStripItem> ToolbarItems(this Form1 form)
            => form.Controls.Find("toolStrip1", true).OfType<ToolStrip>().Single().Items.Cast<ToolStripItem>().ToList();
    }
}
