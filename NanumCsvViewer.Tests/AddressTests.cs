using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // "셀로 이동" 주소 문법: 120 · R120C3 · C3 · 이름:120 · [이름]120 (순수 파서 + 헤더 대조).
    public class AddressTests
    {
        private static CellAddress Parse(string text)
        {
            Assert.True(CellAddress.TryParse(text, out var a, out string error), $"'{text}' should parse but: {error}");
            return a;
        }

        private static string Fail(string text)
        {
            Assert.False(CellAddress.TryParse(text, out _, out string error), $"'{text}' should not parse");
            Assert.NotEmpty(error);
            return error;
        }

        [Theory]
        [InlineData("120", 120)]
        [InlineData("  7  ", 7)]
        [InlineData("1,234", 1234)]
        [InlineData("12,345,678", 12345678)]
        [InlineData("R120", 120)]
        [InlineData("r5", 5)]
        public void A_plain_number_is_a_row(string text, long row)
        {
            var a = Parse(text);
            Assert.Equal(row, a.Row);
            Assert.Null(a.Column);
            Assert.Null(a.ColumnName);
        }

        [Theory]
        [InlineData("R120C3", 120, 2)]
        [InlineData("r1c1", 1, 0)]
        [InlineData("R7C12", 7, 11)]
        public void R_C_form_is_row_plus_one_based_column(string text, long row, int column0)
        {
            var a = Parse(text);
            Assert.Equal(row, a.Row);
            Assert.Equal(column0, a.Column);
            Assert.Null(a.ColumnName);
        }

        [Fact]
        public void C_alone_is_a_column_only()
        {
            var a = Parse("C3");
            Assert.Null(a.Row);
            Assert.Equal(2, a.Column);
            Assert.Equal(4, Parse("c5").Column + 0);
        }

        [Theory]
        [InlineData("이름:120", "이름", 120L)]
        [InlineData("[이름]120", "이름", 120L)]
        [InlineData("age: 5", "age", 5L)]
        [InlineData("[이름]", "이름", null)]
        [InlineData("이름:", "이름", null)]
        [InlineData("a:b:5", "a:b", 5L)]
        [InlineData("[w[kg]]9", "w[kg]", 9L)]
        [InlineData("age", "age", null)]
        [InlineData("a:b", "a:b", null)]
        public void Name_forms_split_column_name_and_optional_row(string text, string name, long? row)
        {
            var a = Parse(text);
            Assert.Equal(name, a.ColumnName);
            Assert.Equal(row, a.Row);
            Assert.Null(a.Column);
        }

        [Fact]
        public void The_text_the_address_box_displays_round_trips()
        {
            // 주소 상자는 "R{행:N0} · {컬럼}"을 보여 준다. Enter만 눌러도 같은 셀로 이동해야 한다.
            var a = Parse("R1,234 · age");
            Assert.Equal(1234L, a.Row);
            Assert.Equal("age", a.ColumnName);
            Assert.True(a.TryResolve(new[] { "id", "age" }, out long? row, out int? col, out _));
            Assert.Equal((1234L, 1), (row, col));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("R0")]
        [InlineData("R0C1")]
        [InlineData("R1C0")]
        [InlineData("C0")]
        [InlineData("-5")]
        [InlineData("3.5")]
        [InlineData("1,2")]
        [InlineData("1,2345")]
        [InlineData("99999999999999999999")]
        [InlineData("[이름")]
        [InlineData(":5")]
        [InlineData("[]5")]
        public void Invalid_addresses_are_rejected_with_a_reason(string text)
        {
            Fail(text);
        }

        [Fact]
        public void Column_numbers_past_the_table_and_unknown_names_fail_at_resolution()
        {
            string[] headers = { "id", "Age", "", "age2" };
            Assert.False(Parse("C9").TryResolve(headers, out _, out _, out string e1));
            Assert.Contains("Column 9", e1);
            Assert.False(Parse("nope:3").TryResolve(headers, out _, out _, out string e2));
            Assert.Contains("nope", e2);
            Assert.False(Parse("R5C5").TryResolve(headers, out _, out _, out _));
        }

        [Fact]
        public void Names_resolve_exactly_then_case_insensitively_then_as_ColumnN_for_blank_headers()
        {
            string[] headers = { "id", "Age", "", "Score", "score" };
            Assert.True(Parse("Age:4").TryResolve(headers, out var row, out var col, out _));
            Assert.Equal((4L, 1), (row, col));
            Assert.True(Parse("age:4").TryResolve(headers, out _, out col, out _));      // 대소문자 무시 유일 일치
            Assert.Equal(1, col);
            Assert.True(Parse("Column3").TryResolve(headers, out row, out col, out _));   // 빈 헤더
            Assert.Equal(2, col);
            Assert.Null(row);
            Assert.True(Parse("Score").TryResolve(headers, out _, out col, out _));       // 정확 일치가 우선
            Assert.Equal(3, col);
            Assert.False(Parse("SCORE").TryResolve(headers, out _, out _, out string ambiguous)); // 두 개가 대소문자만 다름
            Assert.Contains("ambiguous", ambiguous);
        }

        [Fact]
        public void Row_only_and_number_only_addresses_resolve_without_touching_headers()
        {
            Assert.True(Parse("42").TryResolve(Array.Empty<string>(), out var row, out var col, out _));
            Assert.Equal(42L, row);
            Assert.Null(col);
            Assert.True(Parse("R3C2").TryResolve(new[] { "a", "b" }, out row, out col, out _));
            Assert.Equal((3L, 1), (row, col));
        }
    }
}
