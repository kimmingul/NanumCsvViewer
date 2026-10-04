using System.Text.Json;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    /// <summary>컬럼 필터(깔때기) 정규식: 공용 안전 규칙(RegexSafety)·정직한 보고(오류/시간 초과).</summary>
    public class RegexSafetyTests
    {
        private static readonly string Catastrophic = new string('a', 40) + "!";

        [Fact]
        public void SetText_refuses_an_invalid_regex_and_keeps_the_existing_filter()
        {
            var s = new ColumnFilterState();
            s.SetText(0, TextFilterOp.Contains, "abc", caseSensitive: false);

            var ex = Assert.Throws<RegexPatternException>(() => s.SetText(0, TextFilterOp.Regex, "(", caseSensitive: false));
            Assert.Contains("(", ex.Message);

            // 거부됐으므로 이전 필터가 그대로 남는다(조용히 0행이 되거나 필터가 사라지지 않음).
            var f = Assert.Single(s.TextFilters);
            Assert.Equal(TextFilterOp.Contains, f.Op);
            Assert.True(s.Predicate()(new[] { "xabcx" }));
        }

        [Fact]
        public void Restored_invalid_regex_is_reported_not_silently_applied()
        {
            // 저장된 뷰(JSON)에는 SetText 검증을 거치지 않은 잘못된 패턴이 들어 있을 수 있다.
            var s = new ColumnFilterState();
            s.TextFilters.Add(new TextFilter { Column = 2, Op = TextFilterOp.Regex, Value = "[unclosed" });
            Assert.Null(s.RegexErrorFor(2));            // 컴파일 전에는 알 수 없다

            var preds = s.IndividualPredicates();
            Assert.False(preds[0](new[] { "a", "b", "[unclosed" }));   // 어떤 행도 통과시키지 않지만…
            Assert.Contains("[unclosed", s.RegexErrorFor(2));            // …이유가 기록된다
            var err = Assert.Single(s.RegexErrors());
            Assert.Equal(2, err.Column);
            Assert.Equal(0, s.TotalTimeouts());

            // 필터를 지우면 오류 기록도 사라진다.
            s.Remove(2);
            Assert.Null(s.RegexErrorFor(2));
        }

        [Fact]
        public void Empty_regex_in_restored_state_is_an_error_too()
        {
            var s = new ColumnFilterState();
            s.TextFilters.Add(new TextFilter { Column = 0, Op = TextFilterOp.Regex, Value = "" });
            s.IndividualPredicates();
            Assert.NotNull(s.RegexErrorFor(0));
        }

        [Fact]
        public void Column_regex_honours_case_sensitivity_option()
        {
            var s = new ColumnFilterState();
            s.SetText(0, TextFilterOp.Regex, "^abc$", caseSensitive: false);
            Assert.True(s.Predicate()(new[] { "ABC" }));
            s.SetText(0, TextFilterOp.Regex, "^abc$", caseSensitive: true);
            Assert.False(s.Predicate()(new[] { "ABC" }));
            Assert.True(s.Predicate()(new[] { "abc" }));
        }

        [Fact]
        public void Column_regex_timeout_is_counted_per_column_and_cell_is_non_match()
        {
            var s = new ColumnFilterState();
            s.SetText(1, TextFilterOp.Regex, "^(a+)+$", caseSensitive: false);
            s.SetText(3, TextFilterOp.Regex, "^ok$", caseSensitive: false);

            var preds = s.IndividualPredicates();
            Assert.Equal(0, s.TotalTimeouts());

            Assert.False(preds[0](new[] { "", Catastrophic, "", "ok" }));
            Assert.True(preds[1](new[] { "", Catastrophic, "", "ok" }));

            Assert.Equal(1, s.TimeoutCount(1));
            Assert.Equal(0, s.TimeoutCount(3));
            Assert.Equal(1, s.TotalTimeouts());
            Assert.Empty(s.RegexErrors());
        }

        [Fact]
        public void Recompiling_starts_a_fresh_timeout_count()
        {
            var s = new ColumnFilterState();
            s.SetText(0, TextFilterOp.Regex, "^(a+)+$", caseSensitive: false);
            var first = s.IndividualPredicates();
            first[0](new[] { Catastrophic });
            Assert.Equal(1, s.TimeoutCount(0));

            s.IndividualPredicates();           // 전체 재평가 직전의 컴파일
            Assert.Equal(0, s.TimeoutCount(0));
        }

        [Fact]
        public void Runtime_regex_state_is_not_serialized_with_saved_views()
        {
            var s = new ColumnFilterState();
            s.TextFilters.Add(new TextFilter { Column = 0, Op = TextFilterOp.Regex, Value = "(" });
            s.IndividualPredicates();
            string json = JsonSerializer.Serialize(s);
            Assert.DoesNotContain("imeout", json);
            Assert.DoesNotContain("RegexError", json);
            var back = JsonSerializer.Deserialize<ColumnFilterState>(json)!;
            Assert.Equal("(", Assert.Single(back.TextFilters).Value);
        }
    }
}
