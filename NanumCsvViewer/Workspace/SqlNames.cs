using System.Text;
using System.Text.RegularExpressions;

namespace NanumCsvViewer.Workspace
{
    /// <summary>
    /// 작업 공간 이름 규칙.
    /// <list type="bullet">
    /// <item>원본·뷰·DB 스키마 이름(최상위 이름)은 문자(한글 포함 유니코드 문자)·숫자·밑줄만 쓴다. 그 밖의 문자(공백·괄호·마침표 등)는 '_'로 바꾸되
    ///   연속된 그런 문자는 '_' 하나로 줄이고 양끝 '_'는 뗀다(원래 있던 밑줄은 그대로 둔다).</item>
    /// <item>숫자로 시작하면 앞에 '_'를 붙이고, DuckDB 예약어(select·order·table·left 등 따옴표 없이 못 쓰는 이름)면 뒤에 '_'를 붙인다.
    ///   비면 "data"(뷰는 "view", DB는 "database")를 쓴다. 127자를 넘으면 자른다.</item>
    /// <item>DuckDB 식별자는 대소문자를 구분하지 않으므로 대소문자만 다른 이름도 겹친 것으로 보고 "_2", "_3"… 을 붙인다.
    ///   CSV 표 하나는 보조 뷰 "&lt;이름&gt;__raw"(원문 문자열)도 차지하므로 그 이름도 함께 피한다. main·temp·system 같은 DuckDB 예약 스키마 이름도 피한다.</item>
    /// <item>생성하는 SQL은 모든 식별자를 항상 큰따옴표로 감싼다(<see cref="Quote"/>). 사용자가 직접 쓸 때는 <see cref="NeedsQuoting"/>이 거짓인 이름은 따옴표 없이 써도 된다.
    ///   한글 이름은 따옴표 없이도 되지만, 컬럼 이름처럼 공백·특수문자가 든 이름은 "큰따옴표"가 필요하다.</item>
    /// <item>컬럼 이름은 파일 헤더 그대로(공백·특수문자 포함) 보존하며, 중복·빈 이름만 DuckDB가 "이름_1"·"column0" 꼴로 고친다.</item>
    /// </list>
    /// </summary>
    public static class SqlNames
    {
        public const int MaxLength = 128;
        public const string RawSuffix = "__raw";

        private static readonly HashSet<string> ReservedSchemas = new(StringComparer.OrdinalIgnoreCase)
        {
            "main", "temp", "system", "memory", "pg_catalog", "information_schema"
        };

        /// <summary>DuckDB 예약어(따옴표 없이는 식별자로 못 씀). 엔진이 <c>duckdb_keywords()</c>로 채우기 전의 기본 목록.</summary>
        private static volatile HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "all","analyse","analyze","and","any","array","as","asc","asymmetric","both","case","cast","check","collate","column",
            "constraint","create","default","deferrable","desc","describe","distinct","do","else","end","except","false","fetch","for",
            "foreign","from","grant","group","having","in","initially","intersect","into","lateral","leading","left","limit","natural",
            "not","null","offset","on","only","or","order","pivot","pivot_longer","pivot_wider","placing","primary","qualify","references",
            "returning","right","select","semi","show","some","summarize","symmetric","table","then","to","trailing","true","union",
            "unique","unpivot","using","variadic","when","where","window","with"
        };

        internal static void SetReservedKeywords(IEnumerable<string> keywords)
            => _reserved = new HashSet<string>(keywords, StringComparer.OrdinalIgnoreCase);

        /// <summary>식별자를 큰따옴표로 감싼다(내부 큰따옴표는 두 번).</summary>
        public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        /// <summary>SQL 문자열 리터럴(작은따옴표).</summary>
        public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        /// <summary>"스키마"."이름" 형태의 완전 한정 참조.</summary>
        public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);

        /// <summary>따옴표 없이 쓰면 안 되는 식별자인가(문자·숫자·밑줄 이외·숫자로 시작·예약어).</summary>
        public static bool NeedsQuoting(string identifier)
        {
            if (identifier.Length == 0) return true;
            if (char.IsDigit(identifier[0])) return true;
            foreach (char c in identifier)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return true;
            return _reserved.Contains(identifier);
        }

        /// <summary>필요할 때만 따옴표를 붙인 식별자(자동완성 삽입용).</summary>
        public static string QuoteIfNeeded(string identifier) => NeedsQuoting(identifier) ? Quote(identifier) : identifier;

        /// <summary>임의 텍스트(파일 이름·시트 이름…)를 최상위 이름 규칙에 맞는 식별자로 바꾼다(중복 검사는 하지 않음).</summary>
        public static string Sanitize(string? text, string fallback = "data")
        {
            var sb = new StringBuilder();
            bool lastInvalid = true; // 앞쪽 구분 문자 제거
            foreach (char c in text ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c) || c == '_') { sb.Append(c); lastInvalid = false; }
                else if (!lastInvalid) { sb.Append('_'); lastInvalid = true; }
            }
            while (sb.Length > 0 && sb[^1] == '_') sb.Length--;
            while (sb.Length > 0 && sb[0] == '_') sb.Remove(0, 1);
            if (sb.Length == 0) return fallback;
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            if (sb.Length > MaxLength - 1) sb.Length = MaxLength - 1;
            string name = sb.ToString();
            return _reserved.Contains(name) ? name + "_" : name;
        }

        /// <summary>이미 쓰는 이름 집합(대소문자 무시)과 겹치지 않는 이름. 겹치면 "_2", "_3"… 을 붙인다.</summary>
        public static string MakeUnique(string baseName, Func<string, bool> isTaken)
        {
            string name = baseName;
            for (int n = 2; isTaken(name) || ReservedSchemas.Contains(name); n++)
            {
                string suffix = "_" + n;
                string stem = baseName.Length + suffix.Length > MaxLength ? baseName[..(MaxLength - suffix.Length)] : baseName;
                name = stem + suffix;
            }
            return name;
        }

        // 에러 메시지의 "LINE n:" 파싱용
        internal static readonly Regex LineMarker = new(@"^LINE (\d+):", RegexOptions.Compiled | RegexOptions.Multiline);
    }
}
