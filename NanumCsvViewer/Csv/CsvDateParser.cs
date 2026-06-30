using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>날짜/시각 값의 세분도(날짜만 / 날짜+시각 / 시각만).</summary>
    public enum TemporalKind { Date, DateTime, Time }

    /// <summary>파싱된 시간 값과 그 세분도.</summary>
    public readonly record struct TemporalValue(DateTime Value, TemporalKind Kind);

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
        // 날짜 성분만(시각 없음) → TemporalKind.Date.
        private static readonly string[] DateOnlyFormats =
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
            "yyyy'년' M'월' d'일'"
        };

        // 날짜 + 시각 → TemporalKind.DateTime.
        private static readonly string[] DateTimeFormats =
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-M-d H:mm:ss",
            "yyyy-MM-dd HH:mm", "yyyy-M-d H:mm",
            "yyyy/MM/dd HH:mm:ss", "yyyy/M/d H:mm:ss",
            "yyyy/MM/dd HH:mm", "yyyy/M/d H:mm",
            "yyyy.MM.dd HH:mm:ss", "yyyy.M.d H:mm:ss",
            "yyyy.MM.dd HH:mm", "yyyy.M.d H:mm",
            "yyyy'년' M'월' d'일' H:mm:ss",
            "yyyy'년' M'월' d'일' H:mm"
        };

        // 시각만(날짜 없음) → TemporalKind.Time.
        private static readonly string[] TimeOnlyFormats =
        {
            "HH:mm:ss", "H:mm:ss",
            "HH:mm", "H:mm"
        };

        private static readonly string[] CompactDateFormats = { "yyyyMMdd" };
        private static readonly string[] CompactDateTimeFormats = { "yyyyMMddHHmmss" };

        /// <summary>값을 날짜로 파싱. allowCompactNumeric이면 yyyyMMdd 같은 순수 숫자 날짜도 허용.</summary>
        public static DateTime? Parse(string value, bool allowCompactNumeric = false)
            => ParseDetailed(value, allowCompactNumeric)?.Value;

        /// <summary>
        /// 값을 시간 값으로 파싱하고 세분도(Date / DateTime / Time)까지 반환.
        /// allowCompactNumeric이면 yyyyMMdd·yyyyMMddHHmmss 같은 순수 숫자 날짜도 허용하되,
        /// 연도가 타당 범위(1900~2100)일 때만 인정한다(정수 코드와의 혼동 방지).
        /// </summary>
        public static TemporalValue? ParseDetailed(string value, bool allowCompactNumeric = false)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0) return null;

            // 시각 전용(날짜 성분 없음). 시각은 자릿수가 적어 날짜류 게이트를 못 넘으므로 먼저 시도.
            if (LooksTimeOnly(trimmed) &&
                DateTime.TryParseExact(trimmed, TimeOnlyFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var timeOnly))
            {
                return new TemporalValue(timeOnly, TemporalKind.Time);
            }

            if (!LooksDateLike(trimmed, allowCompactNumeric)) return null;

            // ISO 8601(…T…Z)은 날짜+시각으로 본다.
            if (LooksIso8601(trimmed) &&
                DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso))
            {
                return new TemporalValue(iso.UtcDateTime, TemporalKind.DateTime);
            }

            // 시각 성분이 있는 형식을 날짜 전용보다 먼저 시도해 DateTime/Date를 정확히 구분.
            if (DateTime.TryParseExact(trimmed, DateTimeFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dateTime))
            {
                return new TemporalValue(dateTime, TemporalKind.DateTime);
            }

            if (DateTime.TryParseExact(trimmed, DateOnlyFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dateOnly))
            {
                return new TemporalValue(dateOnly, TemporalKind.Date);
            }

            if (allowCompactNumeric)
            {
                if (DateTime.TryParseExact(trimmed, CompactDateTimeFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var compactDt) && IsPlausibleYear(compactDt.Year))
                {
                    return new TemporalValue(compactDt, TemporalKind.DateTime);
                }
                if (DateTime.TryParseExact(trimmed, CompactDateFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var compactD) && IsPlausibleYear(compactD.Year))
                {
                    return new TemporalValue(compactD, TemporalKind.Date);
                }
            }

            return null;
        }

        private static bool IsPlausibleYear(int year) => year is >= 1900 and <= 2100;

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

        // 숫자와 콜론만으로 이루어지고 콜론이 하나 이상 → 시각 전용 후보(예: 14:30, 09:15:00).
        private static bool LooksTimeOnly(string value)
        {
            bool hasColon = false;
            foreach (char c in value)
            {
                if (c == ':') { hasColon = true; continue; }
                if (c < '0' || c > '9') return false;
            }
            return hasColon;
        }
    }
}
