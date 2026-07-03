using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class AdvancedFilterExpressionTests
    {
        private static readonly string[] Headers = { "name", "age", "city" };

        private static bool Eval(string expr, params string[] row)
            => AdvancedFilterExpression.Compile(expr, Headers).Predicate(row);

        [Fact]
        public void Numeric_greater_than()
        {
            Assert.True(Eval("age > 30", "bob", "40", "서울"));
            Assert.False(Eval("age > 30", "al", "20", "부산"));
        }

        [Theory]
        [InlineData("age = 30", "30", true)]
        [InlineData("age = 30", "31", false)]
        [InlineData("age != 30", "31", true)]
        [InlineData("age >= 30", "30", true)]
        [InlineData("age <= 30", "29", true)]
        public void Comparison_operators(string expr, string age, bool expected)
        {
            Assert.Equal(expected, Eval(expr, "x", age, "y"));
        }

        [Fact]
        public void Contains_is_case_insensitive()
        {
            Assert.True(Eval("name contains KIM", "kimchi", "1", "z"));
        }

        [Fact]
        public void And_or_and_parentheses()
        {
            Assert.True(Eval("age > 30 AND city = 서울", "x", "40", "서울"));
            Assert.False(Eval("age > 30 AND city = 서울", "x", "40", "부산"));
            Assert.True(Eval("age > 100 OR city = 서울", "x", "40", "서울"));
            Assert.True(Eval("(age > 100 OR age < 10) OR city = 서울", "x", "40", "서울"));
        }

        [Fact]
        public void Quoted_value_with_space()
        {
            Assert.True(Eval("city = \"서울 특별시\"", "x", "1", "서울 특별시"));
        }

        [Fact]
        public void Column_n_reference()
        {
            Assert.True(Eval("Column2 = 40", "x", "40", "y"));
        }

        [Fact]
        public void Unknown_column_throws()
        {
            Assert.Throws<AdvancedFilterExpressionException>(() => AdvancedFilterExpression.Compile("zzz = 1", Headers));
        }

        // ---------------------------------------------------------------- [컬럼] 참조 확장 (이슈 #26)

        private static readonly string[] DateHeaders = { "start_date", "end_date", "amount", "limit" };

        private static bool EvalD(string expr, params string[] row)
            => AdvancedFilterExpression.Compile(expr, DateHeaders).Predicate(row);

        [Fact]
        public void Bracketed_column_on_left_side_works_like_bare_name()
        {
            Assert.True(Eval("[age] > 30", "x", "40", "y"));
            Assert.False(Eval("[age] > 30", "x", "20", "y"));
        }

        [Fact]
        public void Cross_column_date_comparison_is_date_aware()
        {
            Assert.True(EvalD("[end_date] >= [start_date]", "2024-01-01", "2024-01-02", "0", "0"));
            Assert.False(EvalD("[end_date] >= [start_date]", "2024-03-01", "2024-01-02", "0", "0"));
            // 형식이 달라도 날짜로 비교(문자열 비교라면 "2024-1-9" > "2024-01-10"이 되어 틀린다)
            Assert.True(EvalD("[end_date] >= [start_date]", "2024-1-9", "2024-01-10", "0", "0"));
        }

        [Fact]
        public void Cross_column_numeric_comparison_is_numeric()
        {
            // 문자열 비교라면 "100" < "20" — 수치 비교여야 참
            Assert.True(EvalD("[amount] > [limit]", "x", "y", "100", "20"));
            Assert.True(EvalD("[amount] = [limit]", "x", "y", "1.0", "1")); // 수치 동등
        }

        [Fact]
        public void Cross_column_with_blank_is_always_false()
        {
            Assert.False(EvalD("[end_date] < [start_date]", "", "2024-01-01", "0", "0"));
            Assert.False(EvalD("[end_date] >= [start_date]", "2024-01-01", "", "0", "0"));
            Assert.False(EvalD("[amount] != [limit]", "x", "y", "", "5"));
        }

        [Fact]
        public void Cross_column_contains()
        {
            Assert.True(Eval("[name] contains [city]", "서울사람", "1", "서울"));
            Assert.False(Eval("[name] contains [city]", "부산사람", "1", "서울"));
        }

        [Fact]
        public void Literal_comparison_semantics_unchanged_by_extension()
        {
            // 기존 문법의 의미는 그대로(하위 호환 — 저장된 뷰 보호)
            Assert.True(Eval("city = 서울", "x", "1", "서울"));
            Assert.True(Eval("age >= 30", "x", "30", "y"));
        }

        // ---------------------------------------------------------------- 하위 호환 회귀 (리뷰 수정)

        [Fact]
        public void Unclosed_bracket_is_literal_not_error()
        {
            // 'note contains [draft' 같은 기존 식은 미닫힘 '['를 리터럴로 취급해 그대로 동작해야 한다.
            var headers = new[] { "note" };
            var pred = AdvancedFilterExpression.Compile("note contains [draft", headers).Predicate;
            Assert.True(pred(new[] { "final [draft version" }));
            Assert.False(pred(new[] { "final version" }));
        }

        [Fact]
        public void Bracketed_value_that_is_not_a_column_falls_back_to_literal()
        {
            // 'code = [A12]' — [A12]가 컬럼이 아니면 리터럴 "[A12]" 비교로 폴백(컴파일 오류 아님).
            var headers = new[] { "code" };
            var pred = AdvancedFilterExpression.Compile("code = [A12]", headers).Predicate;
            Assert.True(pred(new[] { "[A12]" }));
            Assert.False(pred(new[] { "A12" }));
        }

        [Fact]
        public void Header_name_containing_bracket_still_referenced_by_name()
        {
            // 'weight[kg]'처럼 대괄호가 든 헤더는 토큰 중간의 '['라 리터럴로 누적돼 이름 매칭이 유지된다.
            var headers = new[] { "weight[kg]", "x" };
            var pred = AdvancedFilterExpression.Compile("weight[kg] > 100", headers).Predicate;
            Assert.True(pred(new[] { "150", "a" }));
            Assert.False(pred(new[] { "50", "a" }));
        }

        [Fact]
        public void Cross_column_yyyy_month_dates_compare_as_dates_not_numbers()
        {
            // '2024.11'(11월)은 '2024.5'(5월)보다 커야 한다 — 수치 비교라면 2024.11 < 2024.5로 뒤집힘.
            var headers = new[] { "start", "end" };
            var pred = AdvancedFilterExpression.Compile("[end] < [start]", headers).Predicate;
            Assert.False(pred(new[] { "2024.5", "2024.11" })); // 정상 순서 → 위반 아님
            Assert.True(pred(new[] { "2024.11", "2024.5" }));  // 역전 → 위반
        }

        [Fact]
        public void Cross_column_time_vs_datetime_is_not_comparable()
        {
            // 시각 전용 vs 일시 혼합은 비교 불가(오늘 날짜 주입 회피) → 어떤 순서 비교도 false.
            var headers = new[] { "a", "b" };
            var lt = AdvancedFilterExpression.Compile("[a] < [b]", headers).Predicate;
            var gt = AdvancedFilterExpression.Compile("[a] > [b]", headers).Predicate;
            Assert.False(lt(new[] { "09:00", "2024-01-01 17:00" }));
            Assert.False(gt(new[] { "09:00", "2024-01-01 17:00" }));
        }

        [Fact]
        public void Cross_column_both_time_compares_time_of_day()
        {
            var headers = new[] { "a", "b" };
            var pred = AdvancedFilterExpression.Compile("[a] < [b]", headers).Predicate;
            Assert.True(pred(new[] { "09:00", "17:30" }));
            Assert.False(pred(new[] { "18:00", "17:30" }));
        }

        [Fact]
        public void Rule_mode_blank_never_matches_ordering()
        {
            // 규칙 실행 모드: 결측 셀(빈 값·NA)은 순서 비교 규칙에 매칭되지 않아야(결측 이중 계산 방지).
            var headers = new[] { "age" };
            var rulePred = AdvancedFilterExpression.Compile("age < 0 OR age > 120", headers,
                blankNeverMatchesOrdering: true).Predicate;
            Assert.False(rulePred(new[] { "" }));   // 빈 값
            Assert.False(rulePred(new[] { "NA" }));  // 널 토큰
            Assert.False(rulePred(new[] { "45" }));  // 정상값
            Assert.True(rulePred(new[] { "-3" }));   // 실제 위반
            Assert.True(rulePred(new[] { "130" }));

            // 필터 모드(기본): 하위 호환 위해 기존 문자열 폴백 의미 유지(빈 값이 'age < 0'에 매칭).
            var filterPred = AdvancedFilterExpression.Compile("age < 0", headers).Predicate;
            Assert.True(filterPred(new[] { "" }));
        }

        [Fact]
        public void Detects_unbracketed_column_comparison()
        {
            var headers = new[] { "start_date", "end_date" };
            Assert.True(AdvancedFilterExpression.LooksLikeUnbracketedColumnComparison("end_date < start_date", headers));
            Assert.False(AdvancedFilterExpression.LooksLikeUnbracketedColumnComparison("end_date < [start_date]", headers));
            Assert.False(AdvancedFilterExpression.LooksLikeUnbracketedColumnComparison("end_date < \"start_date\"", headers));
            Assert.False(AdvancedFilterExpression.LooksLikeUnbracketedColumnComparison("age > 30", new[] { "age" }));
        }

        [Fact]
        public void Empty_expression_throws()
        {
            Assert.Throws<AdvancedFilterExpressionException>(() => AdvancedFilterExpression.Compile("   ", Headers));
        }
    }
}
