using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class CsvSearchTests
    {
        private static (int, string)? Match(string input, int? column, params string[] row)
        {
            var query = CsvSearchQuery.FromUserInput(input, column);
            Assert.NotNull(query);
            return new CsvSearchMatcher(query!).FirstMatch(row);
        }

        [Fact]
        public void Contains_is_case_insensitive()
        {
            var m = Match("kim", null, "Bob", "KIMCHI");
            Assert.NotNull(m);
            Assert.Equal(1, m!.Value.Item1);
        }

        [Fact]
        public void Slash_regex_routes_to_regex()
        {
            var q = CsvSearchQuery.FromUserInput("/ab.*z/", null);
            Assert.Equal(CsvSearchMode.Regex, q!.Mode);
            Assert.NotNull(new CsvSearchMatcher(q).FirstMatch(new[] { "abcz" }));
        }

        [Fact]
        public void Regex_prefix_routes_to_regex()
        {
            var q = CsvSearchQuery.FromUserInput("regex:^\\d+$", null);
            Assert.Equal(CsvSearchMode.Regex, q!.Mode);
            Assert.NotNull(new CsvSearchMatcher(q).FirstMatch(new[] { "12345" }));
            Assert.Null(new CsvSearchMatcher(q).FirstMatch(new[] { "12a45" }));
        }

        [Fact]
        public void Fuzzy_matches_ordered_subsequence()
        {
            var q = CsvSearchQuery.FromUserInput("fuzzy:abc", null);
            Assert.Equal(CsvSearchMode.Fuzzy, q!.Mode);
            Assert.NotNull(new CsvSearchMatcher(q).FirstMatch(new[] { "axbxxc" }));
            Assert.Null(new CsvSearchMatcher(q).FirstMatch(new[] { "acb" }));
        }

        [Fact]
        public void Column_scope_limits_search()
        {
            Assert.Null(Match("kim", 0, "Bob", "KIMCHI"));     // 컬럼 0만 → 불일치
            Assert.NotNull(Match("kim", 1, "Bob", "KIMCHI"));  // 컬럼 1 → 일치
        }

        [Fact]
        public void Invalid_regex_throws()
        {
            Assert.Throws<CsvSearchException>(() => CsvSearchQuery.FromUserInput("regex:[unclosed", null));
        }

        [Fact]
        public void Empty_input_returns_null_query()
        {
            Assert.Null(CsvSearchQuery.FromUserInput("   ", null));
        }

        [Theory]
        [InlineData("//")]          // 빈 패턴
        [InlineData("regex:")]
        [InlineData("/(/")]
        public void Empty_or_invalid_regex_reports_the_pattern_problem(string input)
        {
            var ex = Assert.Throws<CsvSearchException>(() => CsvSearchQuery.FromUserInput(input, null));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }

        [Fact]
        public void Regex_timeout_is_counted_and_cell_is_treated_as_non_match()
        {
            var q = CsvSearchQuery.FromUserInput("/^(a+)+$/", null)!;
            var matcher = new CsvSearchMatcher(q);
            Assert.Equal(0, matcher.Timeouts.Count);

            // 파국적 역추적 셀: 예외 없이 불일치. 같은 행의 뒤 셀은 계속 검사한다.
            string bad = new string('a', 40) + "!";
            Assert.Null(matcher.FirstMatch(new[] { bad, "x" }));
            Assert.Equal(1, matcher.Timeouts.Count);

            var hit = matcher.FirstMatch(new[] { bad, "aaa" });
            Assert.Equal((1, "aaa"), hit!.Value);
            Assert.Equal(2, matcher.Timeouts.Count);
        }

        [Fact]
        public void Non_regex_modes_never_count_timeouts()
        {
            var matcher = new CsvSearchMatcher(CsvSearchQuery.FromUserInput("fuzzy:abc", null)!);
            matcher.FirstMatch(new[] { new string('a', 40) + "!" });
            Assert.Equal(0, matcher.Timeouts.Count);
        }

        [Fact]
        public void Regex_search_checks_cancellation_between_cells()
        {
            var matcher = new CsvSearchMatcher(CsvSearchQuery.FromUserInput("/^(a+)+$/", null)!);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            string bad = new string('a', 40) + "!";
            // 취소된 토큰이면 첫 셀(최대 250ms)조차 시작하지 않고 즉시 중단한다.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<OperationCanceledException>(() => matcher.FirstMatch(new[] { bad, bad, bad }, cts.Token));
            Assert.True(sw.ElapsedMilliseconds < 200, $"cancel took {sw.ElapsedMilliseconds} ms");
            Assert.Equal(0, matcher.Timeouts.Count);
        }
    }
}
