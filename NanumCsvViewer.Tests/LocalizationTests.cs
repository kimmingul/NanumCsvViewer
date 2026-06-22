using System.Globalization;
using System.Resources;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class LocalizationTests
    {
        // Loc는 internal이므로 ResourceManager로 직접 검증(베이스=영어, ko 위성=한국어).
        private static readonly ResourceManager Rm =
            new("NanumCsvViewer.Resources.Strings", typeof(VirtualCsvDocument).Assembly);

        [Theory]
        [InlineData("Menu_Open", "Open...")]
        [InlineData("Menu_DetailPanel", "Detail Panel")]
        [InlineData("Signal_Ready", "Ready")]
        public void English_is_the_base_language(string key, string expected)
        {
            Assert.Equal(expected, Rm.GetString(key, CultureInfo.GetCultureInfo("en")));
        }

        [Theory]
        [InlineData("Menu_Open", "열기...")]
        [InlineData("Menu_DetailPanel", "상세 패널")]
        [InlineData("Signal_Ready", "준비완료")]
        public void Korean_satellite_overrides(string key, string expected)
        {
            Assert.Equal(expected, Rm.GetString(key, CultureInfo.GetCultureInfo("ko")));
        }

        [Fact]
        public void Format_strings_have_matching_placeholders_in_both_languages()
        {
            // 형식 문자열의 {n} 개수가 언어별로 일치해야 string.Format 시 예외가 없다.
            string[] fmtKeys =
            {
                "Status_LoadingProgressFmt", "Status_ReadyFmt", "Status_FilterFmt",
                "Status_NoFilterFmt", "Status_FilterClearedFmt", "Status_SortFmt",
                "Status_FoundFmt", "Filter_ContainsFmt", "Filter_EqualsFmt", "Err_UnsupportedEncodingFmt",
            };
            foreach (var key in fmtKeys)
            {
                string en = Rm.GetString(key, CultureInfo.GetCultureInfo("en"))!;
                string ko = Rm.GetString(key, CultureInfo.GetCultureInfo("ko"))!;
                Assert.Equal(MaxPlaceholder(en), MaxPlaceholder(ko));
            }
        }

        // 문자열에서 가장 큰 {n} 인덱스(없으면 -1) → 필요한 인자 개수의 척도.
        private static int MaxPlaceholder(string s)
        {
            int max = -1;
            for (int i = 0; i < s.Length - 1; i++)
                if (s[i] == '{' && char.IsDigit(s[i + 1]))
                    max = Math.Max(max, s[i + 1] - '0');
            return max;
        }
    }
}
