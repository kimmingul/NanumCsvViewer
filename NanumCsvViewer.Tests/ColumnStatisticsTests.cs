using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class ColumnStatisticsTests
    {
        private static ColumnSummary Summarize(string header, params string[] values)
        {
            var rows = values.Select(v => new[] { v }).ToList();
            return ColumnStatisticsBuilder.SummarizeColumn(0, header, rows);
        }

        [Fact]
        public void Infers_integer()
        {
            Assert.Equal(ColumnValueType.Integer, Summarize("n", "2", "3", "4", "100").InferredType);
        }

        [Fact]
        public void Infers_float()
        {
            Assert.Equal(ColumnValueType.Float, Summarize("x", "1.5", "2.0", "3.25").InferredType);
        }

        [Fact]
        public void Infers_boolean()
        {
            Assert.Equal(ColumnValueType.Boolean, Summarize("flag", "true", "false", "yes", "no").InferredType);
        }

        [Fact]
        public void Infers_date_with_date_header()
        {
            Assert.Equal(ColumnValueType.Date, Summarize("date", "2024-01-01", "2024-02-01", "2024-03-01").InferredType);
        }

        [Fact]
        public void Infers_categorical_for_few_distinct_strings()
        {
            Assert.Equal(ColumnValueType.Categorical, Summarize("city", "서울", "부산", "서울", "대구", "부산").InferredType);
        }

        [Fact]
        public void Counts_nulls_and_uniques()
        {
            var s = Summarize("c", "a", "", "a", "b", "n/a");
            Assert.Equal(2, s.NullCount);       // "" 와 "n/a"
            Assert.Equal(3, s.NonNullCount);    // a, a, b
            Assert.Equal(2, s.UniqueCount);     // a, b
        }

        [Fact]
        public void Numeric_summary_values()
        {
            var s = Summarize("n", "1", "2", "3", "4");
            Assert.NotNull(s.Numeric);
            Assert.Equal(1, s.Numeric!.Value.Min);
            Assert.Equal(4, s.Numeric.Value.Max);
            Assert.Equal(2.5, s.Numeric.Value.Mean, 6);
            Assert.Equal(2.5, s.Numeric.Value.Median, 6);
        }

        [Fact]
        public void Empty_column_is_empty_type()
        {
            Assert.Equal(ColumnValueType.Empty, Summarize("e", "", "n/a", "null").InferredType);
        }

        [Fact]
        public void Infers_date_for_compact_numeric_without_date_header()
        {
            // 헤더가 날짜를 암시하지 않아도(예: "code") 컴팩트 숫자 날짜는 Date.
            Assert.Equal(ColumnValueType.Date, Summarize("code", "20240101", "20240202", "20240303").InferredType);
        }

        [Fact]
        public void Infers_datetime_for_compact_numeric()
        {
            Assert.Equal(ColumnValueType.DateTime, Summarize("ts", "20240101120000", "20240102093000").InferredType);
        }

        [Fact]
        public void Compact_numeric_out_of_year_range_is_integer()
        {
            // 타당 연도(1900~2100) 밖이면 날짜로 보지 않고 정수.
            Assert.Equal(ColumnValueType.Integer, Summarize("n", "30001231", "30011231").InferredType);
        }

        [Fact]
        public void Infers_datetime()
        {
            Assert.Equal(ColumnValueType.DateTime, Summarize("when", "2024-01-01 14:30:00", "2024-01-02 09:15:00").InferredType);
        }

        [Fact]
        public void Infers_time()
        {
            Assert.Equal(ColumnValueType.Time, Summarize("t", "14:30:00", "09:15:00", "23:59:59").InferredType);
        }

        [Fact]
        public void Mixed_date_and_datetime_is_datetime()
        {
            Assert.Equal(ColumnValueType.DateTime, Summarize("d", "2024-01-01", "2024-01-02 14:30").InferredType);
        }

        [Fact]
        public void Infers_identifier_for_leading_zero_numbers()
        {
            // 선행 0이 있는 숫자코드(우편번호 등)는 수량이 아니라 식별자.
            Assert.Equal(ColumnValueType.Identifier, Summarize("zip", "06236", "01001", "13529").InferredType);
        }

        [Fact]
        public void Infers_identifier_when_header_suggests_id()
        {
            Assert.Equal(ColumnValueType.Identifier, Summarize("user_id", "1", "2", "3", "4").InferredType);
            Assert.Equal(ColumnValueType.Identifier, Summarize("주문번호", "1001", "1002", "1003").InferredType);
        }

        [Fact]
        public void Identifier_suppresses_numeric_summary()
        {
            // 식별자에는 평균/표준편차 등 무의미한 수치 통계를 붙이지 않는다.
            var s = Summarize("id", "1001", "1002", "1003");
            Assert.Equal(ColumnValueType.Identifier, s.InferredType);
            Assert.Null(s.Numeric);
        }

        [Fact]
        public void Plain_integer_without_id_signal_stays_integer()
        {
            // 식별자 신호(선행 0·헤더 키워드)가 없으면 일반 정수 그대로 + 수치 통계 유지(회귀 방지).
            var s = Summarize("amount", "10", "20", "30");
            Assert.Equal(ColumnValueType.Integer, s.InferredType);
            Assert.NotNull(s.Numeric);
        }

        [Theory]
        [InlineData("₩1,000", "₩2,500", "₩900")]     // 원(KRW)
        [InlineData("$10.50", "$3.25", "$99.00")]      // 달러(USD)
        [InlineData("¥1200", "¥800", "¥1500")]         // 엔/위안
        [InlineData("€5", "€12", "€3")]                // 유로
        [InlineData("£7", "£2", "£9")]                 // 파운드
        public void Currency_symbols_infer_currency(string a, string b, string c)
        {
            var s = Summarize("price", a, b, c);
            Assert.Equal(ColumnValueType.Currency, s.InferredType);
            Assert.NotNull(s.Numeric);   // 밑에 깔린 숫자로 통계 산출
        }

        [Fact]
        public void Percent_infers_percent_with_numeric_stats()
        {
            var s = Summarize("rate", "25%", "50%", "12.5%");
            Assert.Equal(ColumnValueType.Percent, s.InferredType);
            Assert.NotNull(s.Numeric);
        }

        [Fact]
        public void Mixed_bare_and_affixed_not_classified_as_percent_or_currency()
        {
            // 기호 없는 값이 섞이면 통화/퍼센트로 오분류하지 않는다(표시 스킨이 값을 왜곡하는 것 방지).
            Assert.NotEqual(ColumnValueType.Percent, Summarize("m", "10", "20", "50%").InferredType);
            Assert.NotEqual(ColumnValueType.Currency, Summarize("m", "₩1,000", "500", "₩900").InferredType);
        }

        [Fact]
        public void Scientific_notation_infers_scientific()
        {
            var s = Summarize("v", "1.2e3", "3.4E5", "5e-2");
            Assert.Equal(ColumnValueType.Scientific, s.InferredType);
        }

        [Fact]
        public void Plain_number_without_affix_is_not_currency_or_percent()
        {
            // 기호 없는 숫자는 통화/퍼센트로 오인되면 안 된다(회귀 방지).
            Assert.Equal(ColumnValueType.Integer, Summarize("n", "1000", "2000", "3000").InferredType);
        }

        [Theory]
        [InlineData('$', "1000", "$1,000")]      // 선언 통화(원시 숫자)에 기호+천단위
        [InlineData('₩', "5000", "₩5,000")]
        [InlineData('¥', "1200.5", "¥1,200.5")]
        public void CellDisplay_currency_adds_symbol(char sym, string raw, string expected)
            => Assert.Equal(expected, CellDisplay.Format(ColumnValueType.Currency, sym, false, raw));

        [Fact]
        public void CellDisplay_currency_without_symbol_left_as_is()
            => Assert.Equal("₩1,000", CellDisplay.Format(ColumnValueType.Currency, null, false, "₩1,000"));

        [Theory]
        [InlineData(false, "25", "25%")]         // SPSS PCT: 정수값 저장
        [InlineData(true, "0.25", "25%")]        // SAS PERCENT: 분수 저장 → ×100
        [InlineData(false, "12.5%", "12.5%")]    // 이미 %가 붙은 추론 텍스트는 그대로
        public void CellDisplay_percent(bool isFraction, string raw, string expected)
            => Assert.Equal(expected, CellDisplay.Format(ColumnValueType.Percent, null, isFraction, raw));

        [Fact]
        public void CellDisplay_non_currency_percent_unchanged()
        {
            Assert.Equal("42", CellDisplay.Format(ColumnValueType.Integer, null, false, "42"));
            Assert.Equal("", CellDisplay.Format(ColumnValueType.Currency, '$', false, ""));
        }
    }
}
