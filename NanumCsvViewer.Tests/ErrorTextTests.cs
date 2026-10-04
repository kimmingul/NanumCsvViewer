using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 엔진 영어 오류 → 한국어 변환: 실제 엔진이 던지는 메시지로 검증(문구 고정 테스트가 아니라 "변환되는가").
    public class ErrorTextTests
    {
        [Fact]
        public void Real_engine_errors_have_korean_text_with_arguments_preserved()
        {
            var rows = new List<string[]> { new[] { "1", "a" }, new[] { "2", "b" } };
            var headers = new[] { "y", "g" };

            var unknown = Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ nope"), c => VariableKind.Numeric));
            string ko = ErrorText.TryKorean(unknown.Message) ?? "";
            Assert.Contains("nope", ko);
            Assert.NotEqual(unknown.Message, ko);

            var parse = Assert.Throws<FormulaParseException>(() => ModelFormula.Parse("y ~ log(x)"));
            string koParse = ErrorText.TryKorean(parse.Message) ?? "";
            Assert.Contains("log", koParse);
            Assert.Contains("위치", koParse);

            var noRows = Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(new List<string[]> { new[] { "", "a" } }, headers, ModelFormula.Parse("y ~ g"), c => c == 1 ? VariableKind.Categorical : VariableKind.Numeric));
            Assert.NotNull(ErrorText.TryKorean(noRows.Message));
        }

        [Fact]
        public void Unknown_messages_pass_through_unchanged()
        {
            Assert.Null(ErrorText.TryKorean("Something nobody translated."));
            Assert.Equal("Something nobody translated.", ErrorText.Localize("Something nobody translated."));
        }
    }
}
