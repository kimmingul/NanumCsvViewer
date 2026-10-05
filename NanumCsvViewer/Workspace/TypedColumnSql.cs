using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Workspace
{
    /// <summary>
    /// 앱 추론 타입(<see cref="ColumnValueType"/>) → DuckDB 형 변환 식. 변환은 앱의 해석 규칙과 맞춘다:
    /// 빈 값·na·n/a·null·nil·missing은 NULL(실패로 세지 않음), 정수·실수는 천 단위 콤마·앞뒤 공백 허용, 통화는 기호(₩$¥￥元€£) 제거,
    /// 퍼센트는 % 제거(12% → 12, 분수로 나누지 않음), 불리언은 true/false·yes/no·y/n·1/0, 날짜는 앱의 <see cref="CsvDateParser"/>와 같은 서식들.
    /// 변환할 수 없으면 NULL이며 <see cref="DataWorkspace.CheckTypedColumnsAsync"/>가 실패 수를 센다.
    /// 식별자·범주·순서형·문자열·빈 컬럼은 변환하지 않는다(<c>001</c> 보존).
    /// </summary>
    public static class TypedColumnSql
    {
        private const string NullTokens = "'', 'na', 'n/a', 'null', 'nil', 'missing'";

        private static readonly string[] DateFormats =
        {
            "%Y-%m-%d", "%Y/%m/%d", "%Y.%m.%d", "%Y. %m. %d", "%Y년 %m월 %d일", "%Y%m%d",
            "%m/%d/%Y", "%d/%m/%Y", "%m-%d-%Y", "%d-%m-%Y"
        };

        private static readonly string[] DateTimeFormats =
        {
            "%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M", "%Y/%m/%d %H:%M:%S", "%Y/%m/%d %H:%M",
            "%Y.%m.%d %H:%M:%S", "%Y.%m.%d %H:%M", "%Y년 %m월 %d일 %H:%M:%S", "%Y년 %m월 %d일 %H:%M",
            "%Y-%m-%dT%H:%M:%S", "%Y-%m-%dT%H:%M:%S.%f", "%Y-%m-%d %H:%M:%S.%f", "%Y%m%d%H%M%S"
        };

        private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(SqlNames.Literal)) + "]";

        /// <summary>이 타입이 VARCHAR 원문과 다른 형으로 변환되는가.</summary>
        public static bool IsConverted(ColumnValueType type) => SqlType(type) is not null;

        /// <summary>변환 후 DuckDB 형 이름(변환하지 않으면 null).</summary>
        public static string? SqlType(ColumnValueType type) => type switch
        {
            ColumnValueType.Integer => "BIGINT",
            ColumnValueType.Float or ColumnValueType.Scientific or ColumnValueType.Currency or ColumnValueType.Percent => "DOUBLE",
            ColumnValueType.Boolean => "BOOLEAN",
            ColumnValueType.Date => "DATE",
            ColumnValueType.DateTime => "TIMESTAMP",
            ColumnValueType.Time => "TIME",
            _ => null,
        };

        /// <summary>비어 있거나 널 토큰인 값인가(실패로 세지 않을 값)를 가리키는 SQL 조건. <paramref name="raw"/>는 VARCHAR 식.</summary>
        public static string IsNullToken(string raw) => $"({raw} IS NULL OR lower(trim({raw})) IN ({NullTokens}))";

        /// <summary>변환 식(널 토큰은 NULL, 변환 실패도 NULL). 변환하지 않는 타입이면 null.</summary>
        public static string? Expression(ColumnValueType type, string raw)
        {
            string t = $"trim({raw})";
            string? conv = type switch
            {
                ColumnValueType.Integer => $"TRY_CAST(replace({t}, ',', '') AS BIGINT)",
                ColumnValueType.Float or ColumnValueType.Scientific => $"TRY_CAST(replace({t}, ',', '') AS DOUBLE)",
                ColumnValueType.Currency => $"TRY_CAST(regexp_replace({t}, '[₩$¥￥元€£,\\s]', '', 'g') AS DOUBLE)",
                ColumnValueType.Percent => $"TRY_CAST(regexp_replace({t}, '[%,\\s]', '', 'g') AS DOUBLE)",
                ColumnValueType.Boolean =>
                    $"CASE lower({t}) WHEN 'true' THEN TRUE WHEN 'yes' THEN TRUE WHEN 'y' THEN TRUE WHEN '1' THEN TRUE " +
                    "WHEN 'false' THEN FALSE WHEN 'no' THEN FALSE WHEN 'n' THEN FALSE WHEN '0' THEN FALSE END",
                // 연-월만 있는 값(2024-03)은 1일로 본다.
                ColumnValueType.Date =>
                    $"CAST(try_strptime(regexp_replace({t}, '^(\\d{{4}})[-/.](\\d{{1,2}})$', '\\1-\\2-01'), {List(DateFormats)}) AS DATE)",
                ColumnValueType.DateTime =>
                    $"try_strptime({t}, {List(DateTimeFormats.Concat(DateFormats))})",
                ColumnValueType.Time => $"CAST(try_strptime({t}, {List(new[] { "%H:%M:%S", "%H:%M" })}) AS TIME)",
                _ => null,
            };
            return conv is null ? null : $"CASE WHEN {IsNullToken(raw)} THEN NULL ELSE {conv} END";
        }

        /// <summary>DuckDB 형 이름 → 앱 타입(뷰 결과 컬럼 설명용 대략 매핑).</summary>
        public static ColumnValueType FromSqlType(string sqlType)
        {
            string t = sqlType.ToUpperInvariant();
            if (t is "BIGINT" or "INTEGER" or "SMALLINT" or "TINYINT" or "HUGEINT" or "UBIGINT" or "UINTEGER" or "USMALLINT" or "UTINYINT" or "UHUGEINT")
                return ColumnValueType.Integer;
            if (t is "DOUBLE" or "FLOAT" or "REAL" || t.StartsWith("DECIMAL", StringComparison.Ordinal)) return ColumnValueType.Float;
            if (t == "DATE") return ColumnValueType.Date;
            if (t.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return ColumnValueType.DateTime;
            if (t.StartsWith("TIME", StringComparison.Ordinal)) return ColumnValueType.Time;
            if (t == "BOOLEAN") return ColumnValueType.Boolean;
            return ColumnValueType.String;
        }
    }
}
