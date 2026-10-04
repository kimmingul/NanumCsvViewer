using System.Text;
using System.Text.RegularExpressions;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 정규식 바꾸기·추출 엔진(UI 없음)과 한 단계 적용.
    public class RegexReplaceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_rxrep_" + Guid.NewGuid().ToString("N"));

        public RegexReplaceTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static readonly string[][] Table =
        {
            new[] { "id-1", "Alice 2024-05-06", "x" },
            new[] { "id-2", "bob", "x" },
            new[] { "id-3", "Carol 1999-12-31", "y" },
        };

        private static string[] RowAt(long r) => Table[(int)r];
        private static IEnumerable<long> Rows => Enumerable.Range(0, Table.Length).Select(i => (long)i);
        private static Regex Rx(string pattern, bool cs = false)
            => new(pattern, cs ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

        [Fact]
        public void Plan_substitutes_groups_and_lists_changes_by_row_then_column()
        {
            var plan = RegexReplace.Plan(RowAt, Rows, new[] { 2, 0 }, Rx(@"^(\w+)-(\d)$"), "$2:$1", 100, CancellationToken.None);
            Assert.False(plan.Truncated);
            Assert.Equal(3, plan.RowsScanned);
            Assert.Equal(3, plan.CellsMatched);
            Assert.Equal(new[] { (0L, 0, "id-1", "1:id"), (1L, 0, "id-2", "2:id"), (2L, 0, "id-3", "3:id") },
                plan.Changes.Select(c => (c.DataRow, c.Column, c.OldValue, c.NewValue)).ToArray());
        }

        [Fact]
        public void Plan_supports_named_groups_dollar_escape_and_case_sensitivity()
        {
            var named = RegexReplace.Plan(RowAt, Rows, new[] { 1 }, Rx(@"(?<y>\d{4})-(?<m>\d\d)-(?<d>\d\d)"), "${d}/${m}/${y} $$", 100, CancellationToken.None);
            Assert.Equal(new[] { "Alice 06/05/2024 $", "Carol 31/12/1999 $" }, named.Changes.Select(c => c.NewValue).ToArray());

            var insensitive = RegexReplace.Plan(RowAt, Rows, new[] { 1 }, Rx("^bob$"), "BOB", 100, CancellationToken.None);
            Assert.Single(insensitive.Changes);
            var sensitive = RegexReplace.Plan(RowAt, Rows, new[] { 1 }, Rx("^BOB$", cs: true), "x", 100, CancellationToken.None);
            Assert.Empty(sensitive.Changes);
            Assert.Equal(0, sensitive.CellsMatched);
        }

        [Fact]
        public void Cells_whose_replacement_equals_the_old_value_are_not_changes_but_still_matched()
        {
            var plan = RegexReplace.Plan(RowAt, Rows, new[] { 2 }, Rx("x"), "x", 100, CancellationToken.None);
            Assert.Empty(plan.Changes);
            Assert.Equal(2, plan.CellsMatched);

            var mixed = RegexReplace.Plan(RowAt, Rows, new[] { 2 }, Rx("^x$"), "y", 100, CancellationToken.None);
            Assert.Equal(2, mixed.Changes.Count); // 세 번째 행은 이미 y
            Assert.Equal(2, mixed.CellsMatched);
        }

        [Fact]
        public void Cells_beyond_a_short_row_are_treated_as_empty()
        {
            var rows = new Dictionary<long, string[]> { [0] = new[] { "a" } };
            var plan = RegexReplace.Plan(r => rows[r], new long[] { 0 }, new[] { 0, 3 }, Rx("^$"), "filled", 10, CancellationToken.None);
            Assert.Equal(new[] { (0L, 3, "", "filled") }, plan.Changes.Select(c => (c.DataRow, c.Column, c.OldValue, c.NewValue)).ToArray());
        }

        [Fact]
        public void Timeouts_are_counted_not_dropped_and_do_not_stop_the_scan()
        {
            var table = new[]
            {
                new[] { new string('a', 40) + "!" },   // 파국적 역추적 → 시간 초과
                new[] { "aaa" },                        // 일치
                new[] { new string('a', 40) + "!" },
            };
            var catastrophic = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(15));
            var plan = RegexReplace.Plan(r => table[r], new long[] { 0, 1, 2 }, new[] { 0 }, catastrophic, "z", 10, CancellationToken.None);
            Assert.Equal(2, plan.CellsTimedOut);
            Assert.Equal(1, plan.CellsMatched);
            Assert.Equal(3, plan.RowsScanned);
            Assert.Equal(new[] { "z" }, plan.Changes.Select(c => c.NewValue).ToArray());

            var count = RegexReplace.Count(r => table[r], new long[] { 0, 1, 2 }, new[] { 0 }, catastrophic, 5, CancellationToken.None);
            Assert.Equal(2, count.CellsTimedOut);
            Assert.Equal(1, count.CellsMatched);

            var preview = RegexReplace.Preview(r => table[r], new long[] { 0, 1, 2 }, new[] { 0 }, catastrophic, "z", 5, CancellationToken.None);
            Assert.Equal(2, preview.CellsTimedOut);
            Assert.Equal(1, preview.CellsChanged);
        }

        [Fact]
        public void Plan_stops_at_max_changes_and_says_so()
        {
            var plan = RegexReplace.Plan(RowAt, Rows, new[] { 0 }, Rx(@"\d"), "#", 2, CancellationToken.None);
            Assert.True(plan.Truncated);
            Assert.Equal(2, plan.Changes.Count);
            Assert.Equal(new long[] { 0, 1 }, plan.Changes.Select(c => c.DataRow).ToArray());

            var exact = RegexReplace.Plan(RowAt, Rows, new[] { 0 }, Rx(@"\d"), "#", 3, CancellationToken.None);
            Assert.False(exact.Truncated); // 딱 맞으면 잘린 것이 아니다
            Assert.Equal(3, exact.Changes.Count);
        }

        [Fact]
        public void Scans_are_cancellable_across_many_rows()
        {
            using var cts = new CancellationTokenSource();
            IEnumerable<long> Many()
            {
                for (long i = 0; i < 1_000_000; i++)
                {
                    if (i == 3000) cts.Cancel();
                    yield return i;
                }
            }
            string[] one = { "v" };
            Assert.Throws<OperationCanceledException>(() => RegexReplace.Plan(_ => one, Many(), new[] { 0 }, Rx("zzz"), "", 10, cts.Token));
        }

        [Fact]
        public void Count_reports_cells_rows_and_leading_samples()
        {
            var s = RegexReplace.Count(RowAt, Rows, new[] { 0, 1, 2 }, Rx("x|id-"), 2, CancellationToken.None);
            Assert.Equal(3, s.RowsScanned);
            Assert.Equal(3, s.RowsMatched);
            Assert.Equal(5, s.CellsMatched);
            Assert.Equal(new[] { (0L, 0, "id-1"), (0L, 2, "x") }, s.Samples.ToArray());
        }

        [Fact]
        public void Preview_counts_everything_but_keeps_only_the_first_samples()
        {
            var p = RegexReplace.Preview(RowAt, Rows, new[] { 0, 1, 2 }, Rx("[a-z]"), "_", 2, CancellationToken.None);
            Assert.Equal(3, p.RowsScanned);
            Assert.True(p.CellsChanged > 2);
            Assert.Equal(2, p.Samples.Count);
            Assert.Equal(p.CellsMatched, p.CellsChanged);
        }

        [Fact]
        public void Pattern_test_looks_only_at_the_first_10000_rows_and_10_samples()
        {
            var r = RegexReplace.TestPattern(_ => new[] { "hit" }, Enumerable.Range(0, 50_000).Select(i => (long)i), new[] { 0 }, Rx("hit"), CancellationToken.None);
            Assert.Equal(RegexReplace.TestMaxRows, r.RowsScanned);
            Assert.Equal(RegexReplace.TestMaxRows, r.CellsMatched);
            Assert.Equal(RegexReplace.TestMaxSamples, r.Samples.Count);
        }

        [Fact]
        public void Extract_picks_the_first_group_by_default_and_a_named_or_numbered_group_on_request()
        {
            var rx = Rx(@"(?<y>\d{4})-(\d\d)-\d\d");
            Assert.Equal(1, RegexExtract.ResolveGroup(rx, null));
            Assert.Equal(rx.GroupNumberFromName("y"), RegexExtract.ResolveGroup(rx, "y"));
            Assert.Equal(2, RegexExtract.ResolveGroup(rx, " 2 "));
            Assert.Equal(0, RegexExtract.ResolveGroup(Rx("abc"), ""));
            Assert.Throws<RegexPatternException>(() => RegexExtract.ResolveGroup(rx, "nope"));
            Assert.Throws<RegexPatternException>(() => RegexExtract.ResolveGroup(rx, "9"));

            var year = RegexExtract.Plan(RowAt, Rows, 1, rx, RegexExtract.ResolveGroup(rx, "y"), 100, CancellationToken.None);
            Assert.Equal(new[] { (0L, "2024"), (2L, "1999") }, year.Values.ToArray());
            Assert.Equal(2, year.RowsMatched);
            Assert.Equal(1, year.RowsNotMatched);
            Assert.Equal(3, year.RowsScanned);
            Assert.False(year.Truncated);
        }

        [Fact]
        public void Extract_stores_only_non_empty_values_counts_timeouts_and_truncates_honestly()
        {
            var empties = RegexExtract.Plan(RowAt, Rows, 1, Rx(@"^(x?)bob"), 1, 100, CancellationToken.None);
            Assert.Equal(1, empties.RowsMatched);      // 일치하지만 빈 값 → 저장하지 않는다
            Assert.Empty(empties.Values);

            var table = new[] { new[] { new string('a', 40) + "!" }, new[] { "aa" } };
            var catastrophic = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(15));
            var t = RegexExtract.Plan(r => table[r], new long[] { 0, 1 }, 0, catastrophic, 1, 10, CancellationToken.None);
            Assert.Equal(1, t.CellsTimedOut);
            Assert.Equal(new[] { (1L, "aa") }, t.Values.ToArray());

            var cut = RegexExtract.Plan(RowAt, Rows, 0, Rx(@"(\d)"), 1, 2, CancellationToken.None);
            Assert.True(cut.Truncated);
            Assert.Equal(2, cut.Values.Count);
        }

        [Fact]
        public void Compile_reports_invalid_patterns_instead_of_failing_silently()
        {
            var ex = Assert.Throws<RegexPatternException>(() => RegexSafety.Compile("(unclosed"));
            Assert.Contains("(unclosed", ex.Message);
            Assert.Throws<RegexPatternException>(() => RegexSafety.Compile(""));
        }

        // ------------------------------------------------------------ 한 단계로 적용

        private async Task<VirtualCsvDocument> OpenAsync(string text)
        {
            string path = Path.Combine(_dir, "src.csv");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
            var doc = VirtualCsvDocument.Open(path);
            await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            return doc;
        }

        [Fact]
        public async Task Applying_a_plan_is_one_undo_step_and_one_notification()
        {
            using var doc = await OpenAsync("id,name\nid-1,Ann\nid-2,Bo\nid-3,Cy\n");
            var plan = RegexReplace.Plan(r => doc.GetRowByIdUncached((int)r), Enumerable.Range(0, doc.DisplayRowCount).Select(i => (long)doc.GetRowId(i)),
                new[] { 0, 1 }, Rx("[a-z]"), "#", 1000, CancellationToken.None);
            int notifications = 0;
            doc.Edits.Changed += () => notifications++;

            int changed = RegexReplace.Apply(doc, plan.Changes, "정규식 바꾸기: " + plan.Changes.Count + "셀");
            Assert.Equal(plan.Changes.Count, changed);
            Assert.Equal(1, notifications);
            Assert.Equal(6, plan.Changes.Count);
            Assert.Equal("정규식 바꾸기: 6셀", doc.Edits.UndoDescription);
            Assert.Equal(new[] { "##-1", "###" }, doc.GetDisplayRow(0));
            Assert.Equal(new[] { "##-3", "##" }, doc.GetDisplayRow(2));

            Assert.True(doc.Edits.Undo());
            Assert.True(doc.Edits.IsEmpty);
            Assert.False(doc.Edits.CanUndo); // 단계가 하나뿐이었다
            Assert.Equal(new[] { "id-1", "Ann" }, doc.GetDisplayRow(0));
            Assert.True(doc.Edits.Redo());
            Assert.Equal(new[] { "##-2", "##" }, doc.GetDisplayRow(1));
        }

        [Fact]
        public async Task Replacing_back_to_the_original_value_removes_the_edit_and_deleted_rows_are_skipped()
        {
            using var doc = await OpenAsync("v\nabc\nabd\n");
            doc.Edits.DeleteRows(new[] { 1 });
            var changes = new[]
            {
                new RegexCellChange(0, 0, "abc", "abc"),   // 원래 값과 같음 → 편집 없음
                new RegexCellChange(1, 0, "abd", "zzz"),   // 삭제된 행 → 건너뜀
            };
            int changed = RegexReplace.Apply(doc, changes, "x");
            Assert.Equal(0, changed);
            Assert.Equal(0, doc.Edits.Count);
        }
    }
}
