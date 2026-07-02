using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>수동 타입 변경 정책(이슈 #12). 안전 우선: 의미가 달라지는 전환은 차단, 재해석이 필요한 전환은 표본 검증.</summary>
    public enum TypeChangePolicy
    {
        /// <summary>즉시 허용(의미 보존 전환).</summary>
        Allowed,
        /// <summary>제한적: 표본 검증 결과·경고를 보여준 뒤 사용자가 확인해야 적용.</summary>
        RequiresValidation,
        /// <summary>차단(기본 차단 — 손실·오해석 가능 전환).</summary>
        Blocked,
    }

    /// <summary>제한적 전환의 표본 검증 결과. 널 토큰은 표본에서 제외.</summary>
    public sealed record TypeChangeValidation(int SampleCount, int ValidCount, IReadOnlyList<string> FailingExamples)
    {
        public int FailCount => SampleCount - ValidCount;
        public bool AllValid => FailCount == 0;
    }

    /// <summary>
    /// 이슈 #12의 변환 규칙표. 13개 타입을 6개 의미 그룹(INT/FLT/시간/BOOL/CAT/STR)으로 사상해 판정한다.
    /// 데이터는 불변(뷰어)이므로 규칙은 손실 방지가 아니라 오해석 방지 목적.
    /// </summary>
    public static class ColumnTypeConversion
    {
        private enum Family { Int, Flt, Temporal, Bool, Cat, Str, Empty }

        private static Family FamilyOf(ColumnValueType t) => t switch
        {
            ColumnValueType.Integer => Family.Int,
            ColumnValueType.Float or ColumnValueType.Currency
                or ColumnValueType.Percent or ColumnValueType.Scientific => Family.Flt,
            ColumnValueType.Date or ColumnValueType.DateTime or ColumnValueType.Time => Family.Temporal,
            ColumnValueType.Boolean => Family.Bool,
            ColumnValueType.Categorical or ColumnValueType.Ordinal or ColumnValueType.Identifier => Family.Cat,
            ColumnValueType.Empty => Family.Empty,
            _ => Family.Str,
        };

        /// <summary>from → to 전환 정책. 동일 타입은 no-op(허용), Empty로의 전환은 차단.</summary>
        public static TypeChangePolicy Classify(ColumnValueType from, ColumnValueType to)
        {
            if (from == to) return TypeChangePolicy.Allowed;
            if (to == ColumnValueType.Empty) return TypeChangePolicy.Blocked;

            Family f = FamilyOf(from), t = FamilyOf(to);
            if (f == Family.Empty) return TypeChangePolicy.Allowed; // 값이 없으니 무해
            if (f == t)
                // 그룹 내부: 수치 표기(Float↔Currency…)·범주 종류(Categorical↔Identifier…)는 의미 동일 → 허용.
                // 시간 계열 내부(Date↔Time…)는 정밀도가 달라 표본 검증.
                return f == Family.Temporal ? TypeChangePolicy.RequiresValidation : TypeChangePolicy.Allowed;

            return (f, t) switch
            {
                (Family.Int, Family.Flt) => TypeChangePolicy.Allowed,          // 확대 변환
                (Family.Int, Family.Temporal) => TypeChangePolicy.RequiresValidation,
                (Family.Int, Family.Bool) => TypeChangePolicy.RequiresValidation,
                (Family.Int, Family.Cat) => TypeChangePolicy.Allowed,
                (Family.Int, Family.Str) => TypeChangePolicy.Allowed,

                (Family.Flt, Family.Int) => TypeChangePolicy.Blocked,          // 소수부 손실
                (Family.Flt, Family.Temporal) => TypeChangePolicy.RequiresValidation,
                (Family.Flt, Family.Bool) => TypeChangePolicy.RequiresValidation,
                (Family.Flt, Family.Cat) => TypeChangePolicy.Allowed,
                (Family.Flt, Family.Str) => TypeChangePolicy.Allowed,

                (Family.Temporal, Family.Cat) => TypeChangePolicy.RequiresValidation,
                (Family.Temporal, Family.Str) => TypeChangePolicy.Allowed,
                (Family.Temporal, _) => TypeChangePolicy.Blocked,

                (Family.Bool, Family.Int) => TypeChangePolicy.RequiresValidation,
                (Family.Bool, Family.Flt) => TypeChangePolicy.RequiresValidation,
                (Family.Bool, Family.Temporal) => TypeChangePolicy.Blocked,
                (Family.Bool, Family.Cat) => TypeChangePolicy.Allowed,
                (Family.Bool, Family.Str) => TypeChangePolicy.Allowed,

                (Family.Cat, Family.Bool) => TypeChangePolicy.RequiresValidation,
                (Family.Cat, Family.Str) => TypeChangePolicy.Allowed,
                (Family.Cat, _) => TypeChangePolicy.Blocked,

                (Family.Str, Family.Temporal) => TypeChangePolicy.RequiresValidation,
                (Family.Str, Family.Bool) => TypeChangePolicy.RequiresValidation,
                (Family.Str, Family.Cat) => TypeChangePolicy.Allowed,
                (Family.Str, _) => TypeChangePolicy.Blocked,

                _ => TypeChangePolicy.Blocked,
            };
        }

        /// <summary>표본 값들이 대상 타입으로 해석되는지 검증. 널 토큰은 건너뛰고, 실패 예시를 최대 maxExamples개 수집.</summary>
        public static TypeChangeValidation Validate(ColumnValueType target, IEnumerable<string> values, int maxExamples = 5)
        {
            int sample = 0, valid = 0;
            var failing = new List<string>();
            foreach (string raw in values)
            {
                string v = raw.Trim();
                if (ColumnStatisticsBuilder.IsNullToken(v)) continue;
                sample++;
                if (ParsesAs(target, v)) valid++;
                else if (failing.Count < maxExamples) failing.Add(v);
            }
            return new TypeChangeValidation(sample, valid, failing);
        }

        // 대상 타입으로의 해석 가능 여부. 추론기(ColumnStatisticsBuilder)와 같은 판정 기준을 사용한다.
        private static bool ParsesAs(ColumnValueType target, string value) => target switch
        {
            ColumnValueType.Integer =>
                double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
                && Math.Truncate(d) == d && !value.Contains('.')
                && value.IndexOf('e', StringComparison.OrdinalIgnoreCase) < 0,
            ColumnValueType.Float or ColumnValueType.Currency
                or ColumnValueType.Percent or ColumnValueType.Scientific
                => NumericAffix.TryParseNumber(value, out _),
            ColumnValueType.Date or ColumnValueType.DateTime =>
                CsvDateParser.ParseDetailed(value, true) is { } t && t.Kind != TemporalKind.Time,
            ColumnValueType.Time =>
                CsvDateParser.ParseDetailed(value, true) is { } tt && tt.Kind == TemporalKind.Time,
            ColumnValueType.Boolean => ColumnStatisticsBuilder.IsBooleanToken(value),
            _ => true, // Categorical/Ordinal/Identifier/String: 항상 안전
        };
    }
}
