using Curiosity.SPSS.SpssDataset;
using NanumCsvViewer.Csv;
using SasReader;

namespace NanumCsvViewer.Import
{
    /// <summary>
    /// SPSS·SAS가 파일에 선언한 컬럼 포맷/측도/값라벨을 우리 앱의 타입 힌트로 변환한다.
    /// 추론이 이미 정확히 잡는 경우(날짜·일반 숫자·문자)는 null을 반환해 추론에 맡기고,
    /// 추론이 알 수 없는 경우(통화·퍼센트·지수·코드형 범주/순서)만 힌트를 준다.
    /// </summary>
    internal static class FormatMappers
    {
        /// <summary>SPSS 변수 → 타입 힌트(없으면 null).</summary>
        public static ColumnTypeHint? MapSpss(Variable v)
        {
            bool hasLabels = v.ValueLabels is { Count: > 0 };
            bool ordinal = v.MeasurementType == MeasurementType.Ordinal;

            // 문자 변수: 값 라벨이 있으면 범주/순서형, 아니면 추론(String)에 맡김.
            if (v.Type == DataType.Text)
                return hasLabels ? new ColumnTypeHint(ordinal ? ColumnValueType.Ordinal : ColumnValueType.Categorical) : null;

            // 숫자 변수: 포맷으로 날짜/시간·통화·퍼센트·지수를 그대로 매핑.
            var byFormat = (v.PrintFormat?.FormatType) switch
            {
                FormatType.DATE or FormatType.ADATE or FormatType.EDATE or FormatType.JDATE
                    or FormatType.SDATE or FormatType.MOYR or FormatType.QYR or FormatType.WKYR
                    => new ColumnTypeHint(ColumnValueType.Date),
                FormatType.DATETIME => new ColumnTypeHint(ColumnValueType.DateTime),
                FormatType.TIME or FormatType.DTIME => new ColumnTypeHint(ColumnValueType.Time),
                FormatType.WKDAY or FormatType.MONTH => new ColumnTypeHint(ColumnValueType.Categorical), // 요일·월 이름
                FormatType.DOLLAR => new ColumnTypeHint(ColumnValueType.Currency, '$'),
                FormatType.CCA or FormatType.CCB or FormatType.CCC or FormatType.CCD or FormatType.CCE
                    => new ColumnTypeHint(ColumnValueType.Currency, null),          // 사용자 정의 통화(기호 미상)
                FormatType.PCT => new ColumnTypeHint(ColumnValueType.Percent, PercentIsFraction: false), // SPSS PCT는 정수값 저장
                FormatType.E => new ColumnTypeHint(ColumnValueType.Scientific),
                _ => null
            };
            if (byFormat is not null) return byFormat;

            // 코드형 숫자(값 라벨/순서 측도) → 범주/순서형.
            if (ordinal) return new ColumnTypeHint(ColumnValueType.Ordinal);
            if (hasLabels) return new ColumnTypeHint(ColumnValueType.Categorical);
            // 그 외 일반 숫자 → 선언된 숫자 타입(Identifier·Boolean 등 추론 휴리스틱 대신 파일 명시 존중).
            int dec = v.PrintFormat?.DecimalPlaces ?? 0;
            return new ColumnTypeHint(dec > 0 ? ColumnValueType.Float : ColumnValueType.Integer);
        }

        // SAS 일반 숫자 포맷의 기본 이름(정확 매칭). 날짜 포맷(DATE·YYMMDD…)과 겹치지 않도록 StartsWith 대신 정확 매칭.
        private static readonly HashSet<string> PlainNumericSasFormats =
            new(StringComparer.Ordinal) { "BEST", "BESTD", "COMMA", "COMMAX", "F", "Z", "D", "NUMX" };

        /// <summary>SAS 컬럼 → 타입 힌트(없으면 null). 값 라벨은 외부 카탈로그(.sas7bcat)라 다루지 않는다(문자는 추론 위임).</summary>
        public static ColumnTypeHint? MapSas(Column col)
        {
            var f = col.getFormat();
            return MapSasFormat(col.getType()?.Name, f?.getName(), f?.getPrecision() ?? 0);
        }

        // 프리미티브 기반 SAS 매핑(단위 테스트용). typeName은 SasReader Column.getType().Name("String"/"Double"…).
        internal static ColumnTypeHint? MapSasFormat(string? typeName, string? formatName, int precision)
        {
            // 문자 컬럼: 값 라벨 카탈로그 미지원 → 추론(String/Categorical)에 위임.
            if (typeName == "String") return null;

            string fmt = (formatName ?? string.Empty).ToUpperInvariant();
            if (fmt.Length > 0)
            {
                if (fmt.StartsWith("WON")) return new ColumnTypeHint(ColumnValueType.Currency, '₩');
                if (fmt.StartsWith("YEN")) return new ColumnTypeHint(ColumnValueType.Currency, '¥');
                if (fmt.StartsWith("EURO")) return new ColumnTypeHint(ColumnValueType.Currency, '€');
                if (fmt.StartsWith("DOLLAR") || fmt.StartsWith("NLMNY")) return new ColumnTypeHint(ColumnValueType.Currency, '$');
                // SAS PERCENT: 리더 반환 스케일이 불확실해 표시 ×100을 하지 않는다(표시·필터·통계 일관).
                if (fmt.StartsWith("PERCENT")) return new ColumnTypeHint(ColumnValueType.Percent);
                if (fmt[0] == 'E' && (fmt.Length == 1 || char.IsDigit(fmt[1]))) return new ColumnTypeHint(ColumnValueType.Scientific);
                // 날짜/시간 등 일반 숫자 포맷이 아니면 리더가 값을 변환하므로 추론에 위임(예: YYMMDDS → Date).
                if (!PlainNumericSasFormats.Contains(fmt) && !char.IsDigit(fmt[0])) return null;
            }
            // 포맷 없음(대다수 일반 숫자) 또는 일반 숫자 포맷 → 선언 숫자 타입(id의 Identifier 오인 방지, SPSS와 대칭).
            return new ColumnTypeHint(precision > 0 ? ColumnValueType.Float : ColumnValueType.Integer);
        }
    }
}
