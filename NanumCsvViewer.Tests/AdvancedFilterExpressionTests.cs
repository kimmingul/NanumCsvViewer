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

        [Fact]
        public void Empty_expression_throws()
        {
            Assert.Throws<AdvancedFilterExpressionException>(() => AdvancedFilterExpression.Compile("   ", Headers));
        }
    }
}
