using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// CSV 셀 값에서 날짜를 파싱하고 헤더명이 날짜를 암시하는지 판정합니다.
    /// 점(.)·슬래시(/)·하이픈(-) 구분, 한국어(yyyy년 M월 d일), 월only, 컴팩트(yyyyMMdd) 형식을 지원합니다.
    /// macOS CsvDateParser 이식. 타입 추론(ColumnStatistics)과 분석(CsvAnalytics)에서 공용으로 사용.
    /// </summary>
    public static class CsvDateParser
    {
        private static readonly HashSet<string> DateHeaderTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "date", "datetime", "time", "timestamp", "dt", "dob"
        };

        private static readonly string[] DateHeaderSubstrings =
        {
            "날짜", "일자", "일시", "생년", "년월", "월일"
        };

        // 리터럴 한글은 작은따옴표로 이스케이프(.NET 커스텀 형식 규칙).
        private static readonly string[] SeparatedFormats =
        {
            "yyyy-MM-dd", "yyyy-M-d",
            "yyyy/MM/dd", "yyyy/M/d",
            "yyyy.MM.dd", "yyyy.M.d",
            "yyyy. MM. dd",
            "yyyy-MM", "yyyy-M",
            "yyyy/MM", "yyyy/M",
            "yyyy.MM", "yyyy.M",
            "MM/dd/yyyy", "M/d/yyyy",
            "dd/MM/yyyy", "d/M/yyyy",
            "MM-dd-yyyy", "M-d-yyyy",
            "dd-MM-yyyy", "d-M-yyyy",
            "yyyy-MM-dd HH:mm:ss", "yyyy-M-d H:mm:ss",
            "yyyy-MM-dd HH:mm", "yyyy-M-d H:mm",
            "yyyy/MM/dd HH:mm:ss", "yyyy/M/d H:mm:ss",
            "yyyy/MM/dd HH:mm", "yyyy/M/d H:mm",
            "yyyy.MM.dd HH:mm:ss", "yyyy.M.d H:mm:ss",
            "yyyy.MM.dd HH:mm", "yyyy.M.d H:mm",
            "yyyy'년' M'월' d'일'",
            "yyyy'년' M'월' d'일' H:mm:ss",
            "yyyy'년' M'월' d'일' H:mm"
        };

        private static readonly string[] CompactFormats =
        {
            "yyyyMMdd", "yyyyMMddHHmmss"
        };

        /// <summary>값을 날짜로 파싱. allowCompactNumeric이면 yyyyMMdd 같은 순수 숫자 날짜도 허용.</summary>
        public static DateTime? Parse(string value, bool allowCompactNumeric = false)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0) return null;
            if (!LooksDateLike(trimmed, allowCompactNumeric)) return null;

            if (LooksIso8601(trimmed) &&
                DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso))
            {
                return iso.UtcDateTime;
            }

            if (DateTime.TryParseExact(trimmed, SeparatedFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var separated))
            {
                return separated;
            }

            if (allowCompactNumeric &&
                DateTime.TryParseExact(trimmed, CompactFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var compact))
            {
                return compact;
            }

            return null;
        }

        /// <summary>헤더명이 날짜 컬럼을 암시하는지(영문 토큰 또는 한국어 부분문자열).</summary>
        public static bool HeaderSuggestsDate(string name)
        {
            string lower = name.ToLowerInvariant();
            foreach (var sub in DateHeaderSubstrings)
                if (lower.Contains(sub)) return true;

            foreach (var token in lower.Split(SplitOnNonAlphanumeric, StringSplitOptions.RemoveEmptyEntries))
                if (DateHeaderTokens.Contains(token)) return true;

            return false;
        }

        private static readonly char[] SplitOnNonAlphanumeric = BuildSeparators();

        private static char[] BuildSeparators()
        {
            // 영숫자가 아닌 일반적인 구분자(공백·기호). 토큰화는 ASCII 기준이면 충분.
            var seps = new List<char>();
            for (char c = (char)0; c < 128; c++)
                if (!char.IsLetterOrDigit(c)) seps.Add(c);
            return seps.ToArray();
        }

        private static bool LooksDateLike(string value, bool allowCompactNumeric)
        {
            int digitCount = 0;
            bool hasSeparator = false;
            bool hasKoreanMarker = false;
            int charCount = 0;
            bool allDigits = true;

            foreach (char c in value)
            {
                charCount++;
                if (c >= '0' && c <= '9') digitCount++;
                else allDigits = false;

                switch (c)
                {
                    case '-': case '/': case '.': case ':':
                    case ' ': case 'T': case 'Z': case '+':
                        hasSeparator = true;
                        break;
                    case '년': case '월': case '일':
                        hasKoreanMarker = true;
                        break;
                }
            }

            if (allowCompactNumeric && allDigits && digitCount == charCount &&
                (digitCount == 8 || digitCount == 14))
            {
                return true;
            }
            if (digitCount < 5) return false;
            return hasSeparator || hasKoreanMarker;
        }

        private static bool LooksIso8601(string value)
            => value.Contains('T') || value.EndsWith("Z", StringComparison.Ordinal);
    }
}
