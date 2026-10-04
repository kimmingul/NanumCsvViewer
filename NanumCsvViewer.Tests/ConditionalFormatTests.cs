using System.Drawing;
using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 조건부 서식 엔진: 우선순위(속성별 처음 맞은 규칙), 셀/행 대상, 색상 눈금, 정규식 시간 초과 집계, 저장 뷰 왕복·하위 호환,
    // 그리기 캐시(보이는 행만 평가).
    [Collection("SavedViewStore")]
    public class ConditionalFormatTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_cf_" + Guid.NewGuid().ToString("N"));

        public ConditionalFormatTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly string[] Headers = { "name", "x", "y" };

        private static ConditionalFormatRule Expr(string id, string expression, string? back = null, string? fore = null, bool bold = false,
            ConditionalFormatTarget target = ConditionalFormatTarget.Cell, string? column = "x", bool enabled = true)
            => new(id, id, enabled, ConditionalFormatKind.Expression, expression, target, column, back, fore, bold, null, null, null);

        private static ConditionalFormatRule Scale(string id, string column, string min, string? mid, string max, bool enabled = true)
            => new(id, id, enabled, ConditionalFormatKind.ColorScale, "", ConditionalFormatTarget.Cell, column, null, null, false, min, mid, max);

        private static CellFormatResult Style(ConditionalFormatSet set, string[] row, int column)
            => set.Resolve(row, set.Evaluate(row), column);

        private static readonly Color Red = Color.FromArgb(255, 255, 0, 0);
        private static readonly Color Blue = Color.FromArgb(255, 0, 0, 255);
        private static readonly Color Yellow = Color.FromArgb(255, 255, 255, 0);

        // ------------------------------------------------------------ 우선순위

        [Fact]
        public void First_matching_rule_wins_per_property_and_properties_combine_across_rules()
        {
            var set = ConditionalFormatSet.Compile(new[]
            {
                Expr("cf1", "x > 5", back: "#FF0000"),
                Expr("cf2", "x > 0", back: "#0000FF", fore: "#FFFF00", bold: true),
            }, Headers);

            var both = Style(set, new[] { "a", "10", "" }, 1);
            Assert.Equal(Red, both.Back);          // 배경은 먼저 맞은 cf1
            Assert.Equal(Yellow, both.Fore);       // cf1은 글자색이 없어 cf2의 값
            Assert.True(both.Bold);
            Assert.False(both.ForeIsAuto);

            var onlySecond = Style(set, new[] { "a", "3", "" }, 1);
            Assert.Equal(Blue, onlySecond.Back);
            Assert.Equal(Yellow, onlySecond.Fore);

            Assert.True(Style(set, new[] { "a", "-1", "" }, 1).IsEmpty);
        }

        [Fact]
        public void Reordering_the_rules_changes_the_winner()
        {
            var a = Expr("cf1", "x > 0", back: "#FF0000");
            var b = Expr("cf2", "x > 0", back: "#0000FF");
            var row = new[] { "n", "1", "" };
            Assert.Equal(Red, Style(ConditionalFormatSet.Compile(new[] { a, b }, Headers), row, 1).Back);
            Assert.Equal(Blue, Style(ConditionalFormatSet.Compile(new[] { b, a }, Headers), row, 1).Back);
        }

        [Fact]
        public void Background_only_rule_gets_a_readable_automatic_text_color()
        {
            var set = ConditionalFormatSet.Compile(new[] { Expr("cf1", "x > 0", back: "#101010"), Expr("cf2", "y > 0", back: "#FFFFE0") }, Headers);
            var dark = Style(set, new[] { "n", "1", "0" }, 1);
            Assert.Equal(Color.White, dark.Fore);
            Assert.True(dark.ForeIsAuto);
            var light = Style(set, new[] { "n", "0", "1" }, 1);
            Assert.Equal(Color.Black, light.Fore);
        }

        [Fact]
        public void An_explicit_text_color_in_a_later_rule_beats_the_automatic_one()
        {
            var set = ConditionalFormatSet.Compile(new[] { Expr("cf1", "x > 0", back: "#101010"), Expr("cf2", "x > 0", fore: "#FF0000") }, Headers);
            var r = Style(set, new[] { "n", "1", "" }, 1);
            Assert.Equal(Red, r.Fore);
            Assert.False(r.ForeIsAuto);
        }

        // ------------------------------------------------------------ 대상

        [Fact]
        public void Cell_rules_style_only_their_column_and_row_rules_style_every_column()
        {
            var set = ConditionalFormatSet.Compile(new[]
            {
                Expr("cf1", "x > 5", back: "#FF0000", column: "y"),                                       // 조건은 x, 칠하는 곳은 y
                Expr("cf2", "name = \"z\"", back: "#0000FF", target: ConditionalFormatTarget.Row, column: null),
            }, Headers);

            var hit = new[] { "a", "9", "q" };
            Assert.True(Style(set, hit, 0).IsEmpty);
            Assert.True(Style(set, hit, 1).IsEmpty);
            Assert.Equal(Red, Style(set, hit, 2).Back);

            var rowHit = new[] { "z", "0", "q" };
            for (int c = 0; c < 3; c++) Assert.Equal(Blue, Style(set, rowHit, c).Back);
        }

        [Fact]
        public void Disabled_rules_and_rules_with_problems_do_not_apply_and_problems_are_reported()
        {
            var set = ConditionalFormatSet.Compile(new[]
            {
                Expr("cf1", "x > 0", back: "#FF0000", enabled: false),
                Expr("cf2", "x > 0", back: "#0000FF", column: "gone"),
                Expr("cf3", "nosuch > 0", back: "#0000FF"),
                Expr("cf4", "x > 0", back: "#00FF00"),
            }, Headers);
            Assert.Equal(1, set.ActiveCount);
            Assert.Equal(new[] { "cf2", "cf3" }, set.Problems.Select(p => p.Id).ToArray());
            Assert.Equal(Color.FromArgb(255, 0, 255, 0), Style(set, new[] { "n", "1", "" }, 1).Back);
        }

        [Fact]
        public void A_renamed_or_deleted_column_deactivates_the_rule_when_recompiled_against_the_new_header()
        {
            var rule = Expr("cf1", "x > 0", back: "#FF0000");
            Assert.Equal(1, ConditionalFormatSet.Compile(new[] { rule }, Headers).ActiveCount);
            var afterRename = ConditionalFormatSet.Compile(new[] { rule }, new[] { "name", "x2", "y" });
            Assert.Equal(0, afterRename.ActiveCount);
            Assert.Single(afterRename.Problems);
            Assert.Contains("x", afterRename.Problems[0].Problem);
        }

        // ------------------------------------------------------------ 식 문법

        [Fact]
        public void Expression_syntax_of_advanced_filters_works_regex_not_and_any_column()
        {
            var set = ConditionalFormatSet.Compile(new[]
            {
                Expr("cf1", "name matches \"^err\"", back: "#FF0000", target: ConditionalFormatTarget.Row, column: null),
                Expr("cf2", "NOT (x > 5)", fore: "#0000FF", column: "x"),
                Expr("cf3", "* contains \"zz\"", bold: true, column: "y"),
            }, Headers);

            Assert.Equal(Red, Style(set, new[] { "error 1", "9", "" }, 2).Back);
            Assert.Null(Style(set, new[] { "ok", "9", "" }, 2).Back);
            Assert.Equal(Blue, Style(set, new[] { "ok", "2", "" }, 1).Fore);
            Assert.Null(Style(set, new[] { "ok", "9", "" }, 1).Fore);
            Assert.True(Style(set, new[] { "ok", "9", "qzzq" }, 2).Bold);
            Assert.False(Style(set, new[] { "ok", "9", "qq" }, 2).Bold);
        }

        [Fact]
        public void Validation_explains_what_is_wrong()
        {
            string? Why(ConditionalFormatRule r) => ConditionalFormatRules.Validate(r, Headers);
            Assert.Null(Why(Expr("a", "x > 1", back: "red")));
            Assert.Contains("empty", Why(Expr("a", " ", back: "red"))!);
            Assert.Contains("back color", Why(Expr("a", "x > 1"))!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not a color", Why(Expr("a", "x > 1", back: "notacolor"))!);
            Assert.Contains("Invalid expression", Why(Expr("a", "x >> 1", back: "red"))!);
            Assert.Contains("Invalid expression", Why(Expr("a", "name matches \"(\"", back: "red"))!);
            Assert.Contains("Unknown column", Why(Expr("a", "x > 1", back: "red", column: "zzz"))!);
            Assert.Contains("needs the column", Why(Expr("a", "x > 1", back: "red", column: null))!);
            Assert.Null(Why(Expr("a", "x > 1", back: "red", target: ConditionalFormatTarget.Row, column: null)));
            Assert.Contains("minimum", Why(Scale("a", "x", "bad", null, "#FF0000"))!);
            Assert.Contains("numeric column", Why(Scale("a", "", "#FFFFFF", null, "#FF0000"))!);
            Assert.Contains("Middle", Why(Scale("a", "x", "#FFFFFF", "bad", "#FF0000"))!);
        }

        [Fact]
        public void Colors_parse_from_hex_short_hex_and_names_and_unknown_text_is_null()
        {
            Assert.Equal(Red, ConditionalFormatRule.ParseColor("#FF0000"));
            Assert.Equal(Red, ConditionalFormatRule.ParseColor("red"));
            Assert.Equal(Color.FromArgb(255, 0xAA, 0xBB, 0xCC), ConditionalFormatRule.ParseColor("#ABC"));
            Assert.Null(ConditionalFormatRule.ParseColor("nonsense"));
            Assert.Null(ConditionalFormatRule.ParseColor(""));
            Assert.Null(ConditionalFormatRule.ParseColor(null));
            Assert.Equal("#FF0000", ConditionalFormatRule.ToHex(Red));
        }

        [Fact]
        public void Ids_are_unique_and_increase()
        {
            Assert.Equal("cf1", ConditionalFormatRules.NextId(Array.Empty<ConditionalFormatRule>()));
            Assert.Equal("cf8", ConditionalFormatRules.NextId(new[] { Expr("cf7", "x>1", back: "red"), Expr("cf3", "x>1", back: "red") }));
        }

        // ------------------------------------------------------------ 색상 눈금

        private static IReadOnlyList<string[]> Rows(params string[] xs) => xs.Select(v => new[] { "n", v, "" }).ToArray();

        [Fact]
        public void Range_uses_min_and_max_of_the_numeric_values_and_skips_text()
        {
            var range = ConditionalFormatSet.ComputeRange(Rows("5", "abc", "", "-2", "30", "NaN", "1,000"), 1, CancellationToken.None);
            Assert.Equal(-2, range.Min);
            Assert.Equal(1000, range.Max);
            Assert.Equal(4, range.Count);
            Assert.Equal(0, ConditionalFormatSet.ComputeRange(Rows("a", ""), 1, CancellationToken.None).Count);
        }

        [Fact]
        public void Two_color_scale_interpolates_between_min_and_max()
        {
            var set = ConditionalFormatSet.Compile(new[] { Scale("cf1", "x", "#000000", null, "#FF0000") }, Headers);
            Assert.Equal("cf1", set.ScaleRequests.Single().Id);
            Assert.Equal(1, set.ScaleRequests.Single().Column);

            Assert.True(Style(set, new[] { "n", "50", "" }, 1).IsEmpty);   // 범위를 넣기 전에는 색을 입히지 않는다
            set.SetScaleRange("cf1", ConditionalFormatSet.ComputeRange(Rows("0", "100"), 1, CancellationToken.None));

            Assert.Equal(Color.FromArgb(255, 0, 0, 0), Style(set, new[] { "n", "0", "" }, 1).Back);
            Assert.Equal(Color.FromArgb(255, 128, 0, 0), Style(set, new[] { "n", "50", "" }, 1).Back);   // 127.5 → 반올림 128
            Assert.Equal(Red, Style(set, new[] { "n", "100", "" }, 1).Back);
            Assert.Equal(Red, Style(set, new[] { "n", "999", "" }, 1).Back);                              // 범위 밖은 끝 색
            Assert.True(Style(set, new[] { "n", "abc", "" }, 1).IsEmpty);                                 // 숫자가 아니면 칠하지 않음
            Assert.True(Style(set, new[] { "n", "50", "" }, 2).IsEmpty);                                  // 다른 컬럼은 그대로
            Assert.Equal(Color.White, Style(set, new[] { "n", "100", "" }, 1).Fore);                      // 어두운 배경 → 흰 글자
        }

        [Fact]
        public void Three_color_scale_passes_through_the_middle_color()
        {
            var set = ConditionalFormatSet.Compile(new[] { Scale("cf1", "x", "#0000FF", "#FFFFFF", "#FF0000") }, Headers);
            set.SetScaleRange("cf1", new ScaleRange(0, 10, 3));
            Assert.Equal(Blue, Style(set, new[] { "n", "0", "" }, 1).Back);
            Assert.Equal(Color.FromArgb(255, 255, 255, 255), Style(set, new[] { "n", "5", "" }, 1).Back);
            Assert.Equal(Red, Style(set, new[] { "n", "10", "" }, 1).Back);
            Assert.Equal(Color.FromArgb(255, 128, 128, 255), Style(set, new[] { "n", "2.5", "" }, 1).Back);
            Assert.Equal(Color.Black, Style(set, new[] { "n", "5", "" }, 1).Fore);
        }

        [Fact]
        public void A_constant_column_gets_the_midpoint_color_instead_of_dividing_by_zero()
        {
            var set = ConditionalFormatSet.Compile(new[] { Scale("cf1", "x", "#000000", null, "#FFFFFF") }, Headers);
            set.SetScaleRange("cf1", ConditionalFormatSet.ComputeRange(Rows("7", "7"), 1, CancellationToken.None));
            Assert.Equal(Color.FromArgb(255, 128, 128, 128), Style(set, new[] { "n", "7", "" }, 1).Back);
        }

        [Fact]
        public void An_earlier_expression_background_beats_a_color_scale_and_the_scale_beats_a_later_one()
        {
            var set = ConditionalFormatSet.Compile(new[]
            {
                Expr("cf1", "x = 0", back: "#00FF00"),
                Scale("cf2", "x", "#000000", null, "#FFFFFF"),
                Expr("cf3", "x >= 0", back: "#FF00FF"),
            }, Headers);
            set.SetScaleRange("cf2", new ScaleRange(0, 10, 2));
            Assert.Equal(Color.FromArgb(255, 0, 255, 0), Style(set, new[] { "n", "0", "" }, 1).Back);
            Assert.Equal(Color.FromArgb(255, 128, 128, 128), Style(set, new[] { "n", "5", "" }, 1).Back);
        }

        [Fact]
        public void Range_computation_can_be_cancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var rows = Enumerable.Range(0, 100_000).Select(i => new[] { "n", i.ToString(), "" }).ToArray();
            Assert.ThrowsAny<OperationCanceledException>(() => ConditionalFormatSet.ComputeRange(rows, 1, cts.Token));
        }

        // ------------------------------------------------------------ 정규식 시간 초과

        private static readonly string Catastrophic = new string('a', 40) + "!";

        [Fact]
        public void Regex_timeouts_are_counted_per_row_and_the_row_is_treated_as_not_matching()
        {
            var set = ConditionalFormatSet.Compile(new[]
            {
                Expr("cf1", "name matches \"^(a+)+$\"", back: "#FF0000", target: ConditionalFormatTarget.Row, column: null),
            }, Headers);
            var styler = new ConditionalFormatStyler(set);
            var bad = new[] { Catastrophic, "1", "" };
            var good = new[] { "aaa", "1", "" };

            Assert.Null(styler.Style(1, () => bad, 0).Back);        // 시간 초과 → 불일치
            Assert.Equal(1, styler.TimedOutRowCount);
            Assert.True(set.TimeoutCount >= 1);
            Assert.Equal(Red, styler.Style(2, () => good, 0).Back);
            Assert.Equal(1, styler.TimedOutRowCount);                // 정상 행은 세지 않는다

            styler.Style(1, () => bad, 0);                            // 캐시 적중 — 다시 평가하지 않는다
            Assert.Equal(1, styler.TimedOutRowCount);
            Assert.Equal(2, styler.RowsEvaluated);
        }

        [Fact]
        public void Count_reports_matches_limit_and_timeouts()
        {
            var rows = new List<string[]>
            {
                new[] { "a", "1", "" }, new[] { "b", "9", "" }, new[] { "c", "12", "" }, new[] { Catastrophic, "20", "" },
            };
            var all = ConditionalFormatSet.Count("x > 5", Headers, rows, 0, CancellationToken.None);
            Assert.Equal((4L, 3L, 0L), (all.RowsScanned, all.RowsMatched, all.TimedOut));
            var limited = ConditionalFormatSet.Count("x > 5", Headers, rows, 3, CancellationToken.None);
            Assert.Equal((3L, 2L), (limited.RowsScanned, limited.RowsMatched));
            var timed = ConditionalFormatSet.Count("name matches \"^(a+)+$\"", Headers, rows, 0, CancellationToken.None);
            Assert.Equal(1, timed.RowsMatched);      // "a" 만 일치
            Assert.True(timed.TimedOut >= 1);
            Assert.Throws<AdvancedFilterExpressionException>(() => ConditionalFormatSet.Count("zzz = 1", Headers, rows, 0, CancellationToken.None));
        }

        // ------------------------------------------------------------ 저장 뷰 왕복 · 하위 호환

        [Fact]
        public void Rules_round_trip_through_the_saved_view_and_old_views_without_rules_still_load()
        {
            string old = SavedViewStore_Override();
            try
            {
                string csv = Path.Combine(_dir, "data.csv");
                var rules = new[]
                {
                    Expr("cf1", "name matches \"^\\\\d+\"", back: "#FF0000", fore: "blue", bold: true, target: ConditionalFormatTarget.Row, column: null),
                    Scale("cf2", "x", "#FFFFFF", "#FFFF00", "#00AA00"),
                    Expr("cf3", "x > 5", back: "orange", enabled: false),
                };
                Assert.Empty(SavedViewStore.LoadConditionalFormats(csv));        // 저장본 없음
                SavedViewStore.SaveConditionalFormats(csv, rules);
                var loaded = SavedViewStore.LoadConditionalFormats(csv);
                Assert.Equal(rules, loaded);                                      // 레코드 값 동등
                Assert.Contains("ColorScale", File.ReadAllText(Directory.GetFiles(old).Single())); // 열거형은 이름으로 저장

                // "현재 보기 저장"은 규칙을 모르지만 이미 저장된 규칙을 지우지 않는다.
                var plain = SavedCsvView.Create("view", "abc", 1, new[] { new SortKey(2, true) }, new[] { 0 }, null, 1);
                Assert.Null(plain.ConditionalFormats);
                SavedViewStore.Save(csv, plain);
                Assert.Equal(rules, SavedViewStore.LoadConditionalFormats(csv));
                Assert.Equal("abc", SavedViewStore.Load(csv)!.FilterText);

                // 규칙을 비우면 빈 목록이 저장된다(null과 구분)
                SavedViewStore.SaveConditionalFormats(csv, Array.Empty<ConditionalFormatRule>());
                SavedViewStore.Save(csv, SavedCsvView.Create("view", null, null, Array.Empty<SortKey>(), Array.Empty<int>(), null, 0));
                Assert.Empty(SavedViewStore.LoadConditionalFormats(csv));

                // 서식 항목이 없는 이전 버전의 저장 뷰 JSON
                string legacy = Path.Combine(_dir, "legacy.csv");
                SavedViewStore.Save(legacy, new SavedCsvView { Name = "old", FilterText = "x" });
                string file = Directory.GetFiles(old).Single(f => File.ReadAllText(f).Contains("\"old\""));
                File.WriteAllText(file, "{\"Name\":\"old\",\"FilterText\":\"x\",\"SortKeys\":[],\"HiddenColumnIndexes\":[3],\"CurrentColumn\":2,\"MatchAny\":false}");
                Assert.Empty(SavedViewStore.LoadConditionalFormats(legacy));
                Assert.Equal("x", SavedViewStore.Load(legacy)!.FilterText);
                Assert.Equal(new[] { 3 }, SavedViewStore.Load(legacy)!.HiddenColumnIndexes);
            }
            finally { SavedViewStore.DirectoryOverride = null; }
        }

        private string SavedViewStore_Override()
        {
            string dir = Path.Combine(_dir, "views");
            Directory.CreateDirectory(dir);
            SavedViewStore.DirectoryOverride = dir;
            return dir;
        }

        // ------------------------------------------------------------ 그리기 캐시: 보이는 행만

        [Fact]
        public void Styler_evaluates_each_visible_row_once_and_again_only_after_invalidation()
        {
            var set = ConditionalFormatSet.Compile(new[] { Expr("cf1", "x > 5", back: "#FF0000", target: ConditionalFormatTarget.Row, column: null) }, Headers);
            var styler = new ConditionalFormatStyler(set);
            int fetched = 0;
            string[] RowFor(int id) { fetched++; return new[] { "n", (id % 10).ToString(), "" }; }

            for (int pass = 0; pass < 3; pass++)                                  // 같은 화면을 세 번 그림(열 3개)
                for (int r = 100; r < 140; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        int id = r;
                        var s = styler.Style(id, () => RowFor(id), c);
                        Assert.Equal(id % 10 > 5, s.Back == Red);
                    }
            Assert.Equal(40, styler.RowsEvaluated);
            Assert.Equal(40, fetched);

            styler.Invalidate();
            styler.Style(100, () => RowFor(100), 0);
            Assert.Equal(41, styler.RowsEvaluated);
        }

        [Fact]
        public void Styler_cache_stays_bounded()
        {
            var set = ConditionalFormatSet.Compile(new[] { Expr("cf1", "x > 5", back: "#FF0000") }, Headers);
            var styler = new ConditionalFormatStyler(set, capacity: 64);
            for (int id = 0; id < 1000; id++) styler.Style(id, () => new[] { "n", "9", "" }, 1);
            Assert.Equal(1000, styler.RowsEvaluated);
            styler.Style(999, () => new[] { "n", "9", "" }, 1);         // 최근 행은 아직 캐시에 있다
            Assert.Equal(1000, styler.RowsEvaluated);
        }

        [Fact]
        public async Task Painting_a_million_row_view_formats_only_the_visible_rows()
        {
            string path = Path.Combine(_dir, "million.csv");
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16))
            {
                w.Write("id,v\n");
                for (int i = 0; i < 1_000_000; i++) { w.Write(i); w.Write(','); w.Write(i % 100); w.Write('\n'); }
            }
            using var doc = VirtualCsvDocument.Open(path);
            await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            Assert.Equal(1_000_000, doc.DisplayRowCount);

            var set = ConditionalFormatSet.Compile(new[]
            {
                Expr("cf1", "v >= 90", back: "#FF0000", target: ConditionalFormatTarget.Row, column: null),
                Scale("cf2", "v", "#FFFFFF", null, "#00AA00"),
            }, doc.Header);
            set.SetScaleRange("cf2", new ScaleRange(0, 99, 1_000_000));    // 범위는 따로(한 번) 계산 — 그리기와 무관
            var styler = new ConditionalFormatStyler(set);

            const int firstVisible = 654_321, visible = 45;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int r = firstVisible; r < firstVisible + visible; r++)
                for (int c = 0; c < doc.ColumnCount; c++)
                {
                    int viewRow = r;
                    styler.Style(doc.GetRowId(viewRow), () => doc.GetDisplayRow(viewRow), c);
                }
            clock.Stop();

            Assert.Equal(visible, styler.RowsEvaluated);          // 100만 행 중 화면에 보이는 45행만 평가
            Assert.True(clock.ElapsedMilliseconds < 2000, $"painting 45 rows took {clock.ElapsedMilliseconds} ms");
            // 값 확인: 행 654,321 → v = 21 (눈금 색, 규칙 1 아님), 행 654,390 → v = 90 → 규칙 1(빨강)
            var scaled = styler.Style(654_321, () => doc.GetDisplayRow(654_321), 1).Back;
            Assert.NotNull(scaled);
            Assert.NotEqual(Red, scaled);
            Assert.Equal(Red, styler.Style(654_390, () => doc.GetDisplayRow(654_390), 1).Back);
        }

        // ------------------------------------------------------------ 테마 색

        private static Color Rgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        private static ConditionalFormatSet Themed(bool dark, params ConditionalFormatRule[] rules)
        {
            var set = ConditionalFormatSet.Compile(rules, Headers);
            set.IsDark = dark;
            return set;
        }

        [Fact]
        public void Named_color_tokens_resolve_to_their_light_or_dark_pair_and_switching_the_theme_needs_no_recompile()
        {
            var set = ConditionalFormatSet.Compile(new[] { Expr("cf1", "x > 5", back: "red") }, Headers);
            var row = new[] { "n", "9", "" };

            var light = Style(set, row, 1);
            Assert.Equal(Rgb(0xFFC7CE), light.Back);
            Assert.Equal(Rgb(0x9C0006), light.Fore);     // 글자색을 안 정하면 이름 색의 짝 글자색
            Assert.True(light.ForeIsAuto);

            set.IsDark = true;
            var dark = Style(set, row, 1);
            Assert.Equal(Rgb(0x6B2429), dark.Back);
            Assert.Equal(Rgb(0xFFB3B8), dark.Fore);

            set.IsDark = false;
            Assert.Equal(light, Style(set, row, 1));

            // 글자색 토큰도 테마별로 다르고, 대소문자·공백은 무시, 모르는 이름은 직접 지정 색(HTML 이름)으로 취급
            Assert.Equal(Rgb(0x1F4E79), Style(Themed(false, Expr("a", "x > 0", fore: " Blue ")), row, 1).Fore);
            Assert.Equal(Rgb(0x8CC0F0), Style(Themed(true, Expr("a", "x > 0", fore: "BLUE")), row, 1).Fore);
            Assert.False(ThemeColors.IsToken("pink"));
            Assert.Equal(Color.FromArgb(255, 255, 192, 203), Style(Themed(false, Expr("a", "x > 0", back: "pink")), row, 1).Back);
        }

        [Fact]
        public void Every_named_color_keeps_readable_text_on_its_tint_in_both_themes()
        {
            Assert.Equal(new[] { "red", "orange", "yellow", "green", "blue", "purple", "gray" }, ThemeColors.Names.ToArray());
            foreach (bool dark in new[] { false, true })
                foreach (string name in ThemeColors.Names)
                {
                    var back = ThemeColors.Resolve(name, ThemeColorRole.Back, dark, true)!.Value;
                    var fore = ThemeColors.Resolve(name, ThemeColorRole.Fore, dark, true)!.Value;
                    double ratio = ThemeColors.ContrastRatio(fore, back);
                    Assert.True(ratio >= ThemeColors.MinContrast, $"{name} dark={dark}: {ratio:0.00}");
                    // 다크 틴트는 격자 배경과 구분되면서 너무 밝지 않다
                    if (dark) Assert.True(ThemeColors.Luminance(back) < 0.12, $"{name} dark tint too bright");
                }
        }

        [Fact]
        public void Custom_colors_adapt_to_the_dark_theme_unless_the_rule_opts_out()
        {
            var row = new[] { "n", "9", "" };
            var pastel = Expr("cf1", "x > 5", back: "#FFC7CE");

            Assert.Equal(Rgb(0xFFC7CE), Style(Themed(false, pastel), row, 1).Back);       // 라이트: 그대로
            var dark = Style(Themed(true, pastel), row, 1);
            Assert.NotEqual(Rgb(0xFFC7CE), dark.Back);
            Assert.True(ThemeColors.Luminance(dark.Back!.Value) < ThemeColors.Luminance(Rgb(0xFFC7CE)) / 2);
            Assert.True(dark.Back.Value.R > dark.Back.Value.G);                           // 색상(붉은 계열)은 유지
            Assert.True(ThemeColors.ContrastRatio(dark.Fore!.Value, dark.Back.Value) >= ThemeColors.MinContrast);

            // 테마 맞춤을 끄면 다크에서도 그대로
            var pinned = Style(Themed(true, pastel with { AdaptTheme = false }), row, 1);
            Assert.Equal(Rgb(0xFFC7CE), pinned.Back);
            Assert.Equal(Color.Black, pinned.Fore);

            // 흰색(색상 눈금 끝점 등)은 다크에서 격자 배경 가까이로
            var white = ThemeColors.AdaptBack(Color.White);
            Assert.True(ThemeColors.ContrastRatio(white, ThemeColors.GridBackground(true)) < 1.4);
            // 이미 어두운 색은 그대로
            Assert.Equal(Rgb(0x203060), ThemeColors.AdaptBack(Rgb(0x203060)));
        }

        [Fact]
        public void Dark_theme_lifts_a_custom_text_color_until_it_is_readable_and_light_theme_leaves_it_alone()
        {
            var row = new[] { "n", "9", "" };
            // 라이트용 짙은 빨강 글자 + 이름 색 배경(다크 틴트)
            var rule = Expr("cf1", "x > 5", back: "red", fore: "#9C0006");
            Assert.Equal(Rgb(0x9C0006), Style(Themed(false, rule), row, 1).Fore);
            var dark = Style(Themed(true, rule), row, 1);
            Assert.NotEqual(Rgb(0x9C0006), dark.Fore);
            Assert.True(ThemeColors.ContrastRatio(dark.Fore!.Value, dark.Back!.Value) >= ThemeColors.MinContrast);
            Assert.False(dark.ForeIsAuto);

            // 배경 규칙이 없으면 격자 배경 위에서 대비를 맞춘다
            var onlyFore = Style(Themed(true, Expr("cf1", "x > 5", fore: "#202020")), row, 1);
            Assert.True(ThemeColors.ContrastRatio(onlyFore.Fore!.Value, ThemeColors.GridBackground(true)) >= ThemeColors.MinContrast);
            Assert.Null(onlyFore.Back);
            // 이미 읽기 좋은 글자색은 건드리지 않는다
            Assert.Equal(Rgb(0xFFEE99), Style(Themed(true, Expr("cf1", "x > 5", fore: "#FFEE99")), row, 1).Fore);

            // EnsureContrast: 색상 유지, 이미 충분하면 그대로, 어느 쪽으로도 안 되면 흑백
            var lifted = ThemeColors.EnsureContrast(Rgb(0x800000), Color.Black);
            Assert.True(ThemeColors.ContrastRatio(lifted, Color.Black) >= ThemeColors.MinContrast);
            Assert.True(lifted.R > lifted.G + 40);
            Assert.Equal(Color.White.ToArgb(), ThemeColors.EnsureContrast(Color.White, Color.Black).ToArgb());
            Assert.Equal(Color.Black.ToArgb(), ThemeColors.EnsureContrast(Rgb(0x777777), Rgb(0x777777), 20).ToArgb());   // 어느 쪽으로도 모자라면 더 대비가 큰 쪽(검정)
        }

        [Fact]
        public void Color_scale_endpoints_are_themed_and_the_range_survives_a_theme_switch()
        {
            var set = ConditionalFormatSet.Compile(new[] { Scale("cf1", "x", "#FFFFFF", null, "red") }, Headers);
            set.SetScaleRange("cf1", new ScaleRange(0, 10, 3));
            var lowRow = new[] { "n", "0", "" };
            var highRow = new[] { "n", "10", "" };

            Assert.Equal(Color.White.ToArgb(), Style(set, lowRow, 1).Back!.Value.ToArgb());
            Assert.Equal(Rgb(0xF8696B), Style(set, highRow, 1).Back);

            set.IsDark = true;                                              // 컴파일·범위 재계산 없이
            var low = Style(set, lowRow, 1);
            var high = Style(set, highRow, 1);
            Assert.Equal(Rgb(0x8E2F34), high.Back);
            Assert.True(ThemeColors.ContrastRatio(low.Back!.Value, ThemeColors.GridBackground(true)) < 1.4);   // 흰 끝점이 어둡게
            Assert.Equal(Color.White, high.Fore);                            // 어두운 배경 → 흰 글자
            Assert.NotNull(set.GetScaleRange("cf1"));
            // 중간 지점은 두 끝점 사이를 보간
            var mid = Style(set, new[] { "n", "5", "" }, 1).Back!.Value;
            Assert.InRange(mid.R, Math.Min(low.Back.Value.R, high.Back!.Value.R), Math.Max(low.Back.Value.R, high.Back.Value.R));
        }

        [Fact]
        public void Rules_saved_before_theme_colors_load_as_custom_colors_with_adaptation_on()
        {
            const string legacyJson = "{\"Id\":\"cf1\",\"Name\":\"old\",\"Enabled\":true,\"Kind\":\"Expression\",\"Expression\":\"x > 1\",\"Target\":\"Cell\",\"Column\":\"x\","
                + "\"BackColor\":\"#FFE699\",\"ForeColor\":null,\"Bold\":false,\"ScaleMinColor\":null,\"ScaleMidColor\":null,\"ScaleMaxColor\":null}";
            var rule = System.Text.Json.JsonSerializer.Deserialize<ConditionalFormatRule>(legacyJson)!;
            Assert.True(rule.AdaptTheme);
            Assert.Equal("#FFE699", rule.BackColor);
            Assert.False(ThemeColors.IsToken(rule.BackColor));

            var row = new[] { "n", "9", "" };
            Assert.Equal(Rgb(0xFFE699), Style(Themed(false, rule), row, 1).Back);          // 라이트에서는 이전과 같은 색
            Assert.NotEqual(Rgb(0xFFE699), Style(Themed(true, rule), row, 1).Back);        // 다크에서는 조정됨

            // 저장 뷰 파일에서도(이전 버전 파일 → 새 파일) 그대로 읽히고, 새 필드는 왕복한다
            SavedViewStore_Override();
            try
            {
                string csv = Path.Combine(_dir, "legacyrules.csv");
                SavedViewStore.SaveConditionalFormats(csv, new[] { rule });
                string file = Directory.GetFiles(SavedViewStore.DirectoryOverride!).Single();
                File.WriteAllText(file, "{\"Name\":\"v\",\"ConditionalFormats\":[" + legacyJson + "]}");
                Assert.Equal(rule, SavedViewStore.LoadConditionalFormats(csv).Single());

                var pinned = rule with { AdaptTheme = false, ForeColor = "blue" };
                SavedViewStore.SaveConditionalFormats(csv, new[] { pinned });
                Assert.Equal(pinned, SavedViewStore.LoadConditionalFormats(csv).Single());
            }
            finally { SavedViewStore.DirectoryOverride = null; }
        }

        // ------------------------------------------------------------ 서식 이력

        private static ConditionalFormatRule[] Rules(params string[] ids) => ids.Select(id => Expr(id, "x > 1", back: "red")).ToArray();

        [Fact]
        public void History_undoes_and_redoes_in_order_and_a_new_change_drops_the_redo_branch()
        {
            var h = new ConditionalFormatHistory();
            Assert.False(h.CanUndo); Assert.False(h.CanRedo);
            Assert.Null(h.Undo()); Assert.Null(h.Redo());

            var none = Rules(); var one = Rules("cf1"); var two = Rules("cf1", "cf2");
            Assert.True(h.Record("add cf1", none, one));
            Assert.True(h.Record("add cf2", one, two));
            Assert.Equal("add cf2", h.UndoDescription);

            var u = h.Undo()!;
            Assert.Equal("add cf2", u.Description);
            Assert.Equal(one, u.Before);
            Assert.True(h.CanRedo);
            Assert.Equal("add cf2", h.RedoDescription);
            var second = h.Undo()!;                                           // 두 번째 되돌리기 = cf1 추가를 취소
            Assert.Equal("add cf1", second.Description);
            Assert.Empty(second.Before);
            Assert.False(h.CanUndo);

            var r = h.Redo()!;
            Assert.Equal("add cf1", r.Description);
            Assert.Equal(one, r.After);
            Assert.Equal(two, h.Redo()!.After);
            Assert.Null(h.Redo());

            h.Undo();                                                         // cf2 추가 취소 후
            Assert.True(h.Record("clear", one, none));                        // 새 변경 → 다시 실행 가지가 사라진다
            Assert.False(h.CanRedo);
            Assert.Equal(2, h.UndoCount);
        }

        [Fact]
        public void History_ignores_unchanged_rule_sets_keeps_snapshots_independent_and_caps_at_twenty()
        {
            var h = new ConditionalFormatHistory();
            Assert.False(h.Record("noop", Rules("cf1"), Rules("cf1")));        // 값이 같으면 기록 안 함
            Assert.False(h.CanUndo);

            var live = new List<ConditionalFormatRule>(Rules("cf1"));
            var before = live.ToList();
            live.Add(Expr("cf2", "x > 2", back: "blue"));
            h.Record("add cf2", before, live);
            live.Clear();                                                      // 호출자가 목록을 비워도 저장된 스냅샷은 그대로
            var entry = h.Undo()!;
            Assert.Equal(new[] { "cf1" }, entry.Before.Select(x => x.Id).ToArray());
            Assert.Equal(new[] { "cf1", "cf2" }, entry.After.Select(x => x.Id).ToArray());

            h = new ConditionalFormatHistory();
            for (int i = 1; i <= 25; i++)
                h.Record("step " + i, Rules(Enumerable.Range(1, i - 1).Select(n => "cf" + n).ToArray()), Rules(Enumerable.Range(1, i).Select(n => "cf" + n).ToArray()));
            Assert.Equal(ConditionalFormatHistory.MaxEntries, h.UndoCount);
            Assert.Equal(20, h.UndoCount);
            int undone = 0;
            string? oldest = null;
            while (h.Undo() is { } e) { undone++; oldest = e.Description; }
            Assert.Equal(20, undone);
            Assert.Equal("step 6", oldest);                                    // 가장 오래된 5개는 밀려났다
        }
    }

    // 실제 Form1(보이지 않는 창)에서: 서식 변경 이력(에이전트 추가/삭제/전체 삭제, 저장 파일 갱신, 버튼·메뉴 상태)과 테마 전환 다시 그리기.
    [Collection("SavedViewStore")]
    public class ConditionalFormatFormTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_cfform_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        public ConditionalFormatFormTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { SavedViewStore.DirectoryOverride = null; try { Directory.Delete(_dir, true); } catch { } }

        private static T Get<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;

        private void OnForm(string csv, Action<Form1, VirtualCsvDocument, string> body)
        {
            string path = Path.Combine(_dir, "grid.csv");
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            SavedViewStore.DirectoryOverride = Path.Combine(_dir, "views");
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    using var form = new Form1(new AppSettings());
                    _ = form.Handle;
                    typeof(Form1).GetMethod("LoadDocument", Inst)!.Invoke(form, new object[] { path, "grid" });
                    var doc = Get<VirtualCsvDocument>(form, "_doc");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(30) && !(doc.IndexingComplete && !Get<bool>(form, "_indexing") && !Get<bool>(form, "_busy")))
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(5);
                    }
                    Assert.True(doc.IndexingComplete);
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    body(form, doc, path);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static ConditionalFormatRule Draft(string name, string expression, string back = "red")
            => new("", name, true, ConditionalFormatKind.Expression, expression, ConditionalFormatTarget.Row, null, back, null, false, null, null, null);

        private static string[] Ids(IEnumerable<ConditionalFormatRule> rules) => rules.Select(r => r.Id).ToArray();

        [Fact]
        public void Format_history_undoes_and_redoes_agent_changes_updates_the_saved_rules_and_the_toolbar_state()
        {
            OnForm("id,score\n1,30\n2,10\n3,20\n", (form, doc, csv) =>
            {
                var button = Get<System.Windows.Forms.ToolStripButton>(form, "_cfUndoButton");
                var undoMenu = Get<System.Windows.Forms.ToolStripMenuItem>(form, "_cfUndoMenu");
                var redoMenu = Get<System.Windows.Forms.ToolStripMenuItem>(form, "_cfRedoMenu");

                Assert.Null(form.AgentUndoConditionalFormat());                    // 이력 없음
                Assert.Null(form.AgentRedoConditionalFormat());
                Assert.False(button.Enabled);
                Assert.False(undoMenu.Enabled);

                form.AgentAddConditionalFormat(Draft("a", "score > 15"));
                form.AgentAddConditionalFormat(Draft("b", "score > 25", "blue"));
                Assert.True(button.Enabled); Assert.True(undoMenu.Enabled); Assert.False(redoMenu.Enabled);

                var undone = form.AgentUndoConditionalFormat()!;
                Assert.Equal(("add cf2", 1), (undone.Description, undone.RuleCount));
                Assert.Equal(new[] { "cf1" }, Ids(form.AgentListConditionalFormats()));
                Assert.Equal(new[] { "cf1" }, Ids(SavedViewStore.LoadConditionalFormats(csv)));   // 저장 파일도 되돌아간다
                Assert.True(redoMenu.Enabled);

                var redone = form.AgentRedoConditionalFormat()!;
                Assert.Equal(("add cf2", 2), (redone.Description, redone.RuleCount));
                Assert.Equal(new[] { "cf1", "cf2" }, Ids(SavedViewStore.LoadConditionalFormats(csv)));
                Assert.False(redoMenu.Enabled);

                // 삭제·전체 삭제도 되돌릴 수 있고, 되돌릴 때 순서와 내용이 그대로
                Assert.True(form.AgentRemoveConditionalFormat("cf1"));
                Assert.Equal("remove cf1", form.AgentUndoConditionalFormat()!.Description);
                Assert.Equal(new[] { "cf1", "cf2" }, Ids(form.AgentListConditionalFormats()));
                Assert.Equal(2, form.AgentClearConditionalFormats());
                Assert.Empty(SavedViewStore.LoadConditionalFormats(csv));
                var afterClear = form.AgentUndoConditionalFormat()!;
                Assert.Equal(("clear all rules", 2), (afterClear.Description, afterClear.RuleCount));
                Assert.Equal(new[] { "cf1", "cf2" }, Ids(SavedViewStore.LoadConditionalFormats(csv)));

                // 되돌린 뒤 새 변경을 하면 다시 실행은 사라진다
                form.AgentUndoConditionalFormat();
                form.AgentAddConditionalFormat(Draft("c", "score > 5"));
                Assert.Null(form.AgentRedoConditionalFormat());
                Assert.False(redoMenu.Enabled);

                // 셀 편집 되돌리기(Ctrl+Z)는 서식을 건드리지 않는다
                int before = form.AgentListConditionalFormats().Count;
                doc.Edits.Set(0, 1, "99", "30");
                Assert.Equal(before, form.AgentListConditionalFormats().Count);
            });
        }

        [Fact]
        public void Format_history_keeps_the_last_twenty_changes_per_file()
        {
            OnForm("id,score\n1,30\n2,10\n", (form, doc, csv) =>
            {
                for (int i = 1; i <= 24; i++) form.AgentAddConditionalFormat(Draft("r" + i, "score > " + i));
                int undone = 0;
                while (form.AgentUndoConditionalFormat() is not null) undone++;
                Assert.Equal(20, undone);
                Assert.Equal(4, form.AgentListConditionalFormats().Count);          // 가장 오래된 4건은 되돌릴 수 없다
                Assert.Equal(4, SavedViewStore.LoadConditionalFormats(csv).Count);
                Assert.False(Get<System.Windows.Forms.ToolStripButton>(form, "_cfUndoButton").Enabled);
            });
        }

        [Fact]
        public void Switching_the_theme_repaints_named_colors_with_the_other_pair()
        {
            OnForm("id,score\n1,30\n2,10\n", (form, doc, csv) =>
            {
                var grid = form.Controls.Find("grid", true).OfType<System.Windows.Forms.DataGridView>().Single();
                var paint = typeof(Form1).GetMethod("OnEditCellFormatting", Inst)!;
                var apply = typeof(Form1).GetMethod("ApplyTheme", Inst)!;
                System.Windows.Forms.DataGridViewCellStyle Paint(int row, int col)
                {
                    var style = new System.Windows.Forms.DataGridViewCellStyle();
                    paint.Invoke(form, new object[] { grid, new System.Windows.Forms.DataGridViewCellFormattingEventArgs(col, row, "", typeof(string), style) });
                    return style;
                }

                apply.Invoke(form, new object[] { AppTheme.Light });
                form.AgentAddConditionalFormat(Draft("hi", "score >= 20", "red"));
                Assert.Equal(Rgb(0xFFC7CE), Paint(0, 1).BackColor);
                Assert.Equal(Rgb(0x9C0006), Paint(0, 1).ForeColor);

                apply.Invoke(form, new object[] { AppTheme.Dark });
                Assert.Equal(Rgb(0x6B2429), Paint(0, 1).BackColor);
                Assert.Equal(Rgb(0xFFB3B8), Paint(0, 1).ForeColor);

                apply.Invoke(form, new object[] { AppTheme.Light });
                Assert.Equal(Rgb(0xFFC7CE), Paint(0, 1).BackColor);
            });
        }

        private static Color Rgb(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }
}
