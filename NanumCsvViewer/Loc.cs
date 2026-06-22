using System.Globalization;
using System.Resources;
using System.Threading;

namespace NanumCsvViewer
{
    /// <summary>
    /// 문자열 리소스 조회 + 언어 결정. 기본 영어(Strings.resx), 한국어는 Strings.ko.resx(위성 어셈블리).
    /// </summary>
    internal static class Loc
    {
        private static readonly ResourceManager Rm =
            new("NanumCsvViewer.Resources.Strings", typeof(Loc).Assembly);

        /// <summary>키에 해당하는 현재 언어 문자열. 없으면 키 자체 반환.</summary>
        public static string T(string key) => Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;

        /// <summary>형식 문자열 조회 + string.Format.</summary>
        public static string F(string key, params object?[] args) => string.Format(T(key), args);

        /// <summary>"en" | "ko" — 현재 적용된 2글자 언어 코드.</summary>
        public static string CurrentLanguage =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";

        /// <summary>
        /// 설정값("auto"|"en"|"ko")을 실제 UI 컬처로 적용. "auto"면 OS 언어가 한국어일 때만 한국어.
        /// </summary>
        public static void Apply(string language)
        {
            string code = language switch
            {
                "ko" => "ko",
                "en" => "en",
                _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en",
            };
            var culture = new CultureInfo(code);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
    }
}
