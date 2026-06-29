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
    }
}
