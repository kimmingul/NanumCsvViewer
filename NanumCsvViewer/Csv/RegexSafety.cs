using System.Text.RegularExpressions;

namespace NanumCsvViewer.Csv
{
    /// <summary>잘못된 정규식. 메시지는 사용자에게 그대로 보여 줄 수 있는 문장(패턴 포함).</summary>
    public sealed class RegexPatternException : Exception
    {
        public RegexPatternException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// 앱 전체 정규식 공용 규칙: 같은 시간 제한(셀 하나당), 같은 옵션, 같은 오류 문구.
    /// 검색·컬럼 필터·고급 필터·찾아 바꾸기·에이전트 도구가 모두 이 클래스로 컴파일한다.
    /// 시간 초과는 조용히 삼키지 않는다: 호출자가 <see cref="RegexTimeoutCounter"/>로 세어 사용자에게 알린다.
    /// </summary>
    public static class RegexSafety
    {
        /// <summary>셀 하나에 대한 매칭 시간 상한(파국적 역추적 방지).</summary>
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        /// <summary>패턴 컴파일. caseSensitive=false면 대소문자 무시. 잘못된 패턴은 RegexPatternException.</summary>
        public static Regex Compile(string pattern, bool caseSensitive = false)
        {
            if (string.IsNullOrEmpty(pattern)) throw new RegexPatternException("정규식이 비어 있습니다.");
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled
                          | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            try
            {
                return new Regex(pattern, options, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new RegexPatternException($"잘못된 정규식: {pattern} ({ex.Message})", ex);
            }
        }

        /// <summary>시간 초과면 false를 돌려주고 counter에 1을 더한다(counter가 null이면 세지 않음).</summary>
        public static bool IsMatch(Regex regex, string input, RegexTimeoutCounter? counter)
        {
            try { return regex.IsMatch(input); }
            catch (RegexMatchTimeoutException)
            {
                counter?.Hit();
                return false;
            }
        }
    }

    /// <summary>시간 초과 셀 수(스레드 안전). 필터·검색 결과 옆에 "시간 초과 N셀"로 보고한다.</summary>
    public sealed class RegexTimeoutCounter
    {
        private long _count;
        public long Count => Interlocked.Read(ref _count);
        public void Hit() => Interlocked.Increment(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
    }
}
