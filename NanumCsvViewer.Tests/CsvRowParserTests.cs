using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class CsvRowParserTests
    {
        [Fact]
        public void Splits_simple_fields()
        {
            Assert.Equal(new[] { "a", "b", "c" }, CsvRowParser.Parse("a,b,c", ','));
        }

        [Fact]
        public void Empty_fields_are_preserved()
        {
            Assert.Equal(new[] { "", "b", "" }, CsvRowParser.Parse(",b,", ','));
        }

        [Fact]
        public void Quoted_field_keeps_delimiter()
        {
            Assert.Equal(new[] { "a", "b,c", "d" }, CsvRowParser.Parse("a,\"b,c\",d", ','));
        }

        [Fact]
        public void Escaped_double_quote_collapses()
        {
            Assert.Equal(new[] { "she said \"hi\"" }, CsvRowParser.Parse("\"she said \"\"hi\"\"\"", ','));
        }

        [Fact]
        public void Embedded_newline_in_quotes_is_normalized_to_lf()
        {
            // 따옴표 안 CRLF/단독 CR → LF로 정규화
            Assert.Equal(new[] { "line1\nline2", "x" }, CsvRowParser.Parse("\"line1\r\nline2\",x", ','));
            Assert.Equal(new[] { "line1\nline2" }, CsvRowParser.Parse("\"line1\rline2\"", ','));
        }

        [Theory]
        [InlineData('\t')]
        [InlineData(';')]
        [InlineData('|')]
        public void Honors_alternate_delimiters(char d)
        {
            Assert.Equal(new[] { "a", "b", "c" }, CsvRowParser.Parse($"a{d}b{d}c", d));
        }

        [Fact]
        public void Trailing_garbage_after_closing_quote_is_appended()
        {
            // 닫는 따옴표 뒤 구분자 전 문자는 관대 모드로 이어붙임
            Assert.Equal(new[] { "abc", "d" }, CsvRowParser.Parse("\"ab\"c,d", ','));
        }
    }
}
