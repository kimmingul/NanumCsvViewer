using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class CsvDateParserTests
    {
        [Theory]
        [InlineData("2024-01-15")]
        [InlineData("2024/01/15")]
        [InlineData("2024.01.15")]
        [InlineData("2024-01")]
        [InlineData("2024년 1월 15일")]
        [InlineData("2024-01-15 13:45:00")]
        public void Parses_common_separated_formats(string value)
        {
            Assert.NotNull(CsvDateParser.Parse(value));
        }

        [Fact]
        public void Parses_year_correctly()
        {
            var date = CsvDateParser.Parse("2024-03-15");
            Assert.NotNull(date);
            Assert.Equal(2024, date!.Value.Year);
            Assert.Equal(3, date.Value.Month);
            Assert.Equal(15, date.Value.Day);
        }

        [Fact]
        public void Compact_numeric_requires_flag()
        {
            Assert.Null(CsvDateParser.Parse("20240115"));
            Assert.NotNull(CsvDateParser.Parse("20240115", allowCompactNumeric: true));
        }

        [Theory]
        [InlineData("hello")]
        [InlineData("12345")]   // 숫자지만 구분자 없음 → 날짜 아님
        [InlineData("")]
        public void Rejects_non_dates(string value)
        {
            Assert.Null(CsvDateParser.Parse(value));
        }

        [Theory]
        [InlineData("date", true)]
        [InlineData("created_at", false)]
        [InlineData("datetime", true)]
        [InlineData("생년월일", true)]
        [InlineData("일자", true)]
        [InlineData("name", false)]
        public void Header_date_suggestion(string header, bool expected)
        {
            Assert.Equal(expected, CsvDateParser.HeaderSuggestsDate(header));
        }
    }
}
