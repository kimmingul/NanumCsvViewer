using System.Text;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    public class WorkspaceFileTests
    {
        private static WorkspaceCaptureSource Csv(string name, string path, string? enc = "UTF-8", char? delim = ',') =>
            new(WorkspaceFileSource.KindCsv, name, path, enc, delim, true, null);

        private static WorkspaceFileModel Sample(string wsPath, string a, string b, string book)
        {
            var sources = new[]
            {
                Csv("sales", a, "CP949 / EUC-KR", ';'),
                Csv("people", b),
                new WorkspaceCaptureSource(WorkspaceFileSource.KindDatabase, "book", book, null, null, true, new[] { "S1", "S2" }),
            };
            var views = new[]
            {
                new WorkspaceFileView { Name = "joined", Sql = "SELECT *\nFROM sales JOIN people USING (id)", IncludeUnsavedEdits = true },
                new WorkspaceFileView { Name = "top", Sql = "SELECT * FROM joined LIMIT 5" },
            };
            var tabs = new[]
            {
                new WorkspaceCaptureTab(WorkspaceFileTab.KindFile, a, null, null),
                new WorkspaceCaptureTab(WorkspaceFileTab.KindSheet, book, "S2", null),
                new WorkspaceCaptureTab("result", null, null, null),                      // 알 수 없는 종류·경로 없음 → 저장 안 함
                new WorkspaceCaptureTab(WorkspaceFileTab.KindView, null, null, "joined"),
            };
            return WorkspaceFile.Capture(wsPath, sources, views, Array.Empty<WorkspaceFileView>(), tabs, activeTab: 3, explorerVisible: true);
        }

        [Fact]
        public void Round_trip_keeps_sources_options_views_tabs_active_tab_and_explorer_state()
        {
            using var tmp = new TempFolder();
            string ws = tmp.Combine("proj", "w.ncvws");
            string a = tmp.Combine("proj", "data", "a.csv"), b = tmp.Combine("proj", "b.csv"), book = tmp.Combine("proj", "book.xlsx");

            WorkspaceFile.Save(ws, Sample(ws, a, b, book));
            var m = WorkspaceFile.Load(ws);

            Assert.Equal(WorkspaceFile.CurrentVersion, m.Version);
            Assert.Equal(3, m.Sources.Count);
            Assert.Equal(("csv", "sales", "CP949 / EUC-KR", ";"), (m.Sources[0].Kind, m.Sources[0].Name, m.Sources[0].Encoding, m.Sources[0].Delimiter));
            Assert.Equal("data/a.csv", m.Sources[0].Path);                  // 작업 공간 파일 기준 상대 경로, '/' 구분
            Assert.Equal(Path.GetFullPath(a), m.Sources[0].AbsolutePath);
            Assert.Equal("b.csv", m.Sources[1].Path);
            Assert.Equal(new[] { "S1", "S2" }, m.Sources[2].Tables);
            Assert.Equal("database", m.Sources[2].Kind);

            Assert.Equal(new[] { "joined", "top" }, m.Views.Select(v => v.Name));
            Assert.Equal("SELECT *\nFROM sales JOIN people USING (id)", m.Views[0].Sql);
            Assert.True(m.Views[0].IncludeUnsavedEdits);
            Assert.False(m.Views[1].IncludeUnsavedEdits);

            Assert.Equal(new[] { "file", "sheet", "view" }, m.Tabs.Select(t => t.Kind));
            Assert.Equal((0, (string?)null), (m.Tabs[0].Source, m.Tabs[0].Sheet));
            Assert.Equal((2, "S2"), (m.Tabs[1].Source, m.Tabs[1].Sheet));
            Assert.Equal("joined", m.Tabs[2].View);
            Assert.Equal(2, m.ActiveTab);                                   // 건너뛴 결과 탭을 빼고 센 순번
            Assert.True(m.ExplorerVisible);
            Assert.False(File.Exists(ws + ".tmp"));
        }

        [Fact]
        public void Saving_over_an_existing_file_replaces_it_and_leaves_no_temp_file()
        {
            using var tmp = new TempFolder();
            string ws = tmp.Combine("w.ncvws");
            File.WriteAllText(ws, "old");
            WorkspaceFile.Save(ws, new WorkspaceFileModel());
            Assert.Empty(WorkspaceFile.Load(ws).Sources);
            Assert.Equal(new[] { "w.ncvws" }, Directory.GetFiles(tmp.Path).Select(Path.GetFileName));
        }

        [Fact]
        public void Moving_the_whole_folder_resolves_files_by_their_relative_paths()
        {
            using var tmp = new TempFolder();
            string proj = tmp.Combine("proj");
            Directory.CreateDirectory(Path.Combine(proj, "data"));
            string a = Path.Combine(proj, "data", "a.csv"), b = Path.Combine(proj, "b.csv");
            File.WriteAllText(a, "x"); File.WriteAllText(b, "x");
            string ws = Path.Combine(proj, "w.ncvws");
            WorkspaceFile.Save(ws, Sample(ws, a, b, Path.Combine(proj, "book.xlsx")));

            string moved = tmp.Combine("elsewhere", "renamed");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            Directory.Move(proj, moved);                                      // 옛 절대 경로는 이제 없다
            var m = WorkspaceFile.Load(Path.Combine(moved, "w.ncvws"));

            Assert.Equal(Path.Combine(moved, "data", "a.csv"), WorkspaceFile.Resolve(m.Sources[0], moved));
            Assert.Equal(Path.Combine(moved, "b.csv"), WorkspaceFile.Resolve(m.Sources[1], moved));
            Assert.Null(WorkspaceFile.Resolve(m.Sources[2], moved));          // 워크북은 없다 → 누락
        }

        [Fact]
        public void Relative_path_wins_over_a_stale_copy_at_the_old_absolute_path_and_absolute_is_the_fallback_when_only_the_workspace_file_moved()
        {
            using var tmp = new TempFolder();
            string proj = tmp.Combine("proj");
            Directory.CreateDirectory(proj);
            string a = Path.Combine(proj, "a.csv");
            File.WriteAllText(a, "old");
            string ws = Path.Combine(proj, "w.ncvws");
            var model = new WorkspaceFileModel { Sources = { new WorkspaceFileSource { Name = "a" } } };
            WorkspaceFile.SetPath(model.Sources[0], proj, a);

            // 폴더를 복사해 옮김: 옛 위치에 복사본이 남아 있어도 작업 공간 파일 옆의 것을 쓴다.
            string copy = tmp.Combine("copy");
            Directory.CreateDirectory(copy);
            File.WriteAllText(Path.Combine(copy, "a.csv"), "new");
            Assert.Equal(Path.Combine(copy, "a.csv"), WorkspaceFile.Resolve(model.Sources[0], copy));

            // 작업 공간 파일만 다른 곳으로: 상대 경로는 없고 절대 경로로 찾는다.
            string lonely = tmp.Combine("lonely");
            Directory.CreateDirectory(lonely);
            Assert.Equal(a, WorkspaceFile.Resolve(model.Sources[0], lonely));
            Assert.Equal(new[] { Path.Combine(lonely, "a.csv"), a }, WorkspaceFile.Candidates(model.Sources[0], lonely));
            Assert.NotNull(ws);
        }

        [Fact]
        public void Missing_files_resolve_to_null_and_lists_both_places_that_were_tried()
        {
            using var tmp = new TempFolder();
            var s = new WorkspaceFileSource { Name = "gone" };
            WorkspaceFile.SetPath(s, tmp.Path, tmp.Combine("sub", "gone.csv"));
            Assert.Null(WorkspaceFile.Resolve(s, tmp.Path));
            // 작업 공간 파일이 다른 폴더로 옮겨졌다면 시도하는 곳은 둘(새 상대 위치, 옛 절대 위치).
            string movedDir = tmp.Combine("moved");
            Assert.Equal(new[] { Path.Combine(movedDir, "sub", "gone.csv"), tmp.Combine("sub", "gone.csv") }, WorkspaceFile.Candidates(s, movedDir));

            // 경로가 전혀 없는 항목도 예외 없이 누락으로 본다.
            Assert.Null(WorkspaceFile.Resolve(new WorkspaceFileSource(), tmp.Path));
            // 다른 드라이브·UNC는 상대 경로를 만들 수 없다.
            Assert.Null(WorkspaceFile.ToRelative(@"C:\proj", @"\\server\share\a.csv"));
        }

        [Fact]
        public void A_newer_file_version_is_refused_before_its_content_is_interpreted()
        {
            string future = "{\"format\":\"ncvws\",\"version\":99,\"sources\":\"this shape changed\",\"views\":42}";
            var ex = Assert.Throws<WorkspaceFileException>(() => WorkspaceFile.Parse(future));
            Assert.Equal(WorkspaceFileError.TooNew, ex.Error);
            Assert.Equal(99, ex.FileVersion);

            Assert.Equal(WorkspaceFileError.TooNew, Assert.Throws<WorkspaceFileException>(() =>
                WorkspaceFile.Parse("{\"format\":\"ncvws\",\"version\":" + (WorkspaceFile.CurrentVersion + 1) + "}")).Error);
            Assert.Empty(WorkspaceFile.Parse("{\"format\":\"ncvws\",\"version\":" + WorkspaceFile.CurrentVersion + "}").Sources);
        }

        [Theory]
        [InlineData("not json at all", WorkspaceFileError.Corrupt)]
        [InlineData("", WorkspaceFileError.Corrupt)]
        [InlineData("{\"format\":\"ncvws\"}", WorkspaceFileError.Corrupt)]
        [InlineData("{\"format\":\"ncvws\",\"version\":0}", WorkspaceFileError.Corrupt)]
        [InlineData("{\"format\":\"ncvws\",\"version\":\"1\"}", WorkspaceFileError.Corrupt)]
        [InlineData("{\"format\":\"ncvws\",\"version\":1,\"sources\":7}", WorkspaceFileError.Corrupt)]
        [InlineData("{\"version\":1,\"sources\":[]}", WorkspaceFileError.NotAWorkspace)]
        [InlineData("{\"format\":\"other\",\"version\":1}", WorkspaceFileError.NotAWorkspace)]
        [InlineData("[1,2,3]", WorkspaceFileError.NotAWorkspace)]
        public void Damaged_or_foreign_files_are_reported_not_thrown_as_raw_json_errors(string json, WorkspaceFileError expected)
        {
            Assert.Equal(expected, Assert.Throws<WorkspaceFileException>(() => WorkspaceFile.Parse(json)).Error);
        }

        [Fact]
        public void Hand_edited_files_are_tolerated_unknown_properties_ignored_bad_tabs_dropped_and_the_active_index_follows()
        {
            string json = """
            {
              "format": "ncvws", "version": 1, "futureThing": {"x": 1},
              "sources": [ { "name": "a", "absolutePath": "C:\\data\\a.csv", "extra": true }, null ],
              "views": [ { "name": "v", "sql": "SELECT 1" }, { "name": "", "sql": "SELECT 2" }, { "name": "w" } ],
              "tabs": [
                { "kind": "file", "source": 5 },
                { "kind": "file", "source": 0 },
                { "kind": "view" },
                { "kind": "sheet", "source": 1, "sheet": "S" },
                { "kind": "view", "view": "v" }
              ],
              "activeTab": 4
            }
            """;
            var m = WorkspaceFile.Parse(json);
            Assert.Equal(2, m.Sources.Count);                                 // 순번이 탭의 참조이므로 남긴다
            Assert.Equal(new[] { "v" }, m.Views.Select(v => v.Name));
            Assert.Equal(new[] { "file", "sheet", "view" }, m.Tabs.Select(t => t.Kind));
            Assert.Equal(2, m.ActiveTab);
            Assert.True(m.Sources[0].HasHeader);                              // 빠진 값은 기본값
        }

        [Fact]
        public void Capture_keeps_tab_files_even_without_the_engine_dedupes_views_and_never_saves_result_tabs()
        {
            using var tmp = new TempFolder();
            string ws = tmp.Combine("w.ncvws");
            string a = tmp.Combine("a.csv"), book = tmp.Combine("book.xlsx"), c = tmp.Combine("c.csv");
            var carried = new[]
            {
                new WorkspaceFileView { Name = "Joined", Sql = "SELECT carried" },    // 같은 이름(대소문자만 다름) → 실제 뷰가 우선
                new WorkspaceFileView { Name = "broken", Sql = "SELECT * FROM missing_src" },
            };
            var m = WorkspaceFile.Capture(ws,
                sources: new[] { Csv("a", a) },
                views: new[] { new WorkspaceFileView { Name = "joined", Sql = "SELECT live" } },
                carriedViews: carried,
                tabs: new[]
                {
                    new WorkspaceCaptureTab(WorkspaceFileTab.KindFile, a, null, null),
                    new WorkspaceCaptureTab(WorkspaceFileTab.KindSheet, book, "S1", null),   // 작업 공간에 없는 워크북 → 원본으로 덧붙임
                    new WorkspaceCaptureTab(WorkspaceFileTab.KindFile, c, null, null),
                    new WorkspaceCaptureTab(WorkspaceFileTab.KindFile, a, null, null),        // 같은 파일의 두 번째 탭은 같은 원본을 가리킨다
                },
                activeTab: 1, explorerVisible: false);

            Assert.Equal(new[] { "a", "book", "c" }, m.Sources.Select(s => s.Name));
            Assert.Equal(new[] { "csv", "database", "csv" }, m.Sources.Select(s => s.Kind));
            Assert.Equal(new int?[] { 0, 1, 2, 0 }, m.Tabs.Select(t => t.Source));
            Assert.Equal(1, m.ActiveTab);
            Assert.Equal(new[] { "joined", "broken" }, m.Views.Select(v => v.Name));
            Assert.Equal("SELECT live", m.Views[0].Sql);
        }

        [Fact]
        public void The_dirty_signature_sees_content_changes_but_not_tab_order_active_tab_sheet_explorer_or_source_order()
        {
            using var tmp = new TempFolder();
            string ws = tmp.Combine("w.ncvws");
            string a = tmp.Combine("a.csv"), b = tmp.Combine("b.csv"), book = tmp.Combine("book.xlsx");
            var baseline = WorkspaceFile.Signature(Sample(ws, a, b, book));

            var other = Sample(ws, a, b, book);
            other.Tabs.Reverse();
            other.Tabs.RemoveAt(0);
            other.ActiveTab = 0;
            other.ExplorerVisible = false;
            other.Sources[2].Tables = new List<string> { "S1", "S2", "S3" };         // 워크북에 시트가 늘어난 것은 사용자의 변경이 아니다
            other.Sources.Reverse();                                                  // 등록 순서가 달라도(복원은 워크북을 나중에 등록) 같다
            other.Views.Reverse();
            Assert.Equal(baseline, WorkspaceFile.Signature(other));

            var edited = Sample(ws, a, b, book);
            edited.Views[1].Sql += " OFFSET 1";
            Assert.NotEqual(baseline, WorkspaceFile.Signature(edited));
            var renamed = Sample(ws, a, b, book);
            renamed.Sources[0].Name = "sales2";
            Assert.NotEqual(baseline, WorkspaceFile.Signature(renamed));
            var encoded = Sample(ws, a, b, book);
            encoded.Sources[1].Encoding = "UTF-16 LE";
            Assert.NotEqual(baseline, WorkspaceFile.Signature(encoded));
            var removed = Sample(ws, a, b, book);
            removed.Sources.RemoveAt(1);
            Assert.NotEqual(baseline, WorkspaceFile.Signature(removed));
        }

        [Fact]
        public void Recent_workspaces_move_to_the_front_without_duplicates_and_are_capped()
        {
            var s = new AppSettings();
            for (int i = 0; i < AppSettings.MaxRecentWorkspaces + 3; i++) s.AddRecentWorkspace(Path.Combine(Path.GetTempPath(), $"w{i}.ncvws"));
            Assert.Equal(AppSettings.MaxRecentWorkspaces, s.RecentWorkspaces.Count);
            Assert.Equal(Path.Combine(Path.GetTempPath(), $"w{AppSettings.MaxRecentWorkspaces + 2}.ncvws"), s.RecentWorkspaces[0]);

            string old = s.RecentWorkspaces[^1];
            s.AddRecentWorkspace(old.ToUpperInvariant());                             // 대소문자만 다른 같은 경로
            Assert.Equal(AppSettings.MaxRecentWorkspaces, s.RecentWorkspaces.Count);
            Assert.Equal(old.ToUpperInvariant(), s.RecentWorkspaces[0]);
            Assert.Single(s.RecentWorkspaces, p => string.Equals(p, old, StringComparison.OrdinalIgnoreCase));

            s.RemoveRecentWorkspace(old);
            Assert.DoesNotContain(s.RecentWorkspaces, p => string.Equals(p, old, StringComparison.OrdinalIgnoreCase));
            s.AddRecentWorkspace("  ");
            Assert.Equal(AppSettings.MaxRecentWorkspaces - 1, s.RecentWorkspaces.Count);
        }

        // ---- 엔진까지: 저장 → 폴더째 이동 → 불러와 복원 ----------------------------------------------------------

        [Fact]
        public async Task A_saved_workspace_restores_through_the_engine_after_the_folder_moved_with_names_encodings_and_views_intact()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var tmp = new TempFolder();
            string proj = tmp.Combine("proj");
            Directory.CreateDirectory(Path.Combine(proj, "x"));
            // 같은 이름 두 파일(→ sales, sales_2)과 CP949 파일.
            string s1 = Path.Combine(proj, "sales.csv"), s2 = Path.Combine(proj, "x", "sales.csv"), kr = Path.Combine(proj, "고객.csv");
            File.WriteAllText(s1, "id,amount\n1,10\n2,20\n", new UTF8Encoding(false));
            File.WriteAllText(s2, "id,amount\n1,100\n", new UTF8Encoding(false));
            File.WriteAllText(kr, "id,이름\n1,김하나\n2,이두리\n3,박세나\n", Encoding.GetEncoding(949));

            string wsFile = Path.Combine(proj, "w.ncvws");
            WorkspaceFileModel saved;
            using (var ws = new DataWorkspace(new DataWorkspaceOptions { TempRoot = tmp.Combine("duck1"), MemoryLimitBytes = 1L << 30, Threads = 2 }))
            {
                var a = await ws.AddCsvAsync(s1);
                var b = await ws.AddCsvAsync(s2);
                var c = await ws.AddCsvAsync(kr);
                Assert.Equal(new[] { "sales", "sales_2", "고객" }, new[] { a.Name, b.Name, c.Name });
                ws.CreateView("merged", "SELECT * FROM sales UNION ALL SELECT * FROM sales_2");
                ws.CreateView("names", "SELECT 이름 FROM 고객 WHERE 이름 <> '김하나'", includeUnsavedEdits: true);

                var capture = ws.Sources.Select(s =>
                {
                    var o = s.Tables[0].Options;
                    return new WorkspaceCaptureSource(WorkspaceFileSource.KindCsv, s.Name, s.Path, o.EncodingName, o.Delimiter, o.HasHeader, null);
                }).ToList();
                saved = WorkspaceFile.Capture(wsFile, capture,
                    ws.Views.Select(v => new WorkspaceFileView { Name = v.Name, Sql = v.Sql, IncludeUnsavedEdits = v.IncludeUnsavedEdits }),
                    Array.Empty<WorkspaceFileView>(),
                    new[] { new WorkspaceCaptureTab(WorkspaceFileTab.KindFile, kr, null, null), new WorkspaceCaptureTab(WorkspaceFileTab.KindView, null, null, "names") },
                    activeTab: 1, explorerVisible: true);
                WorkspaceFile.Save(wsFile, saved);
            }
            Assert.NotNull(saved.Sources.Single(s => s.Name == "고객").Encoding);
            Assert.DoesNotContain("UTF-8", saved.Sources.Single(s => s.Name == "고객").Encoding!, StringComparison.OrdinalIgnoreCase);

            string moved = tmp.Combine("moved");
            Directory.Move(proj, moved);
            var loaded = WorkspaceFile.Load(Path.Combine(moved, "w.ncvws"));

            // Form1.RestoreWorkspaceAsync와 같은 순서: 해결 → CSV 등록(저장된 옵션) → 이름 되돌리기 → 뷰 만들기.
            using var restored = new DataWorkspace(new DataWorkspaceOptions { TempRoot = tmp.Combine("duck2"), MemoryLimitBytes = 1L << 30, Threads = 2 });
            // 일부러 거꾸로 등록해 이름이 어긋나게 한 뒤 저장된 이름으로 되돌린다.
            foreach (var s in loaded.Sources.AsEnumerable().Reverse())
            {
                string? path = WorkspaceFile.Resolve(s, moved);
                Assert.NotNull(path);
                var src = await restored.AddCsvAsync(path!, new CsvSourceOptions
                {
                    EncodingName = s.Encoding, Delimiter = s.Delimiter is { Length: > 0 } d ? d[0] : null, HasHeader = s.HasHeader,
                });
                if (src.Name != s.Name) restored.Rename(src, s.Name);
            }
            foreach (var v in loaded.Views) restored.CreateView(v.Name, v.Sql, v.IncludeUnsavedEdits);

            Assert.Equal(new[] { "sales", "sales_2", "고객" }, loaded.Sources.Select(s => s.Name));
            Assert.Equal(new[] { "sales", "sales_2", "고객" }, restored.Sources.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
            var merged = await restored.PreviewAsync("SELECT count(*) FROM merged", 10);
            Assert.Equal("3", merged.Rows[0][0]);
            var names = await restored.PreviewAsync("SELECT * FROM names ORDER BY 1", 10);
            Assert.Equal(new[] { "박세나", "이두리" }, names.Rows.Select(r => r[0]!).ToArray());   // CP949가 저장된 옵션대로 읽힘
            var nv = restored.Views.Single(v => v.Name == "names");
            Assert.True(nv.IncludeUnsavedEdits);
            Assert.False(nv.HasResult);                                                              // 뷰는 열 때까지 계산하지 않는다(오래된 상태)
            Assert.Equal(1, loaded.ActiveTab);
        }
    }
}
