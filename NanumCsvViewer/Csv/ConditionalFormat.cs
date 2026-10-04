using System.Drawing;
using System.Text.Json.Serialization;

namespace NanumCsvViewer.Csv
{
    /// <summary>규칙 종류: 식(고급 필터 식이 참이면 스타일 적용) / 색상 눈금(숫자 컬럼 값의 크기에 따른 배경색).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ConditionalFormatKind { Expression, ColorScale }

    /// <summary>스타일을 입히는 범위: 지정한 컬럼의 셀 / 행 전체.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ConditionalFormatTarget { Cell, Row }

    /// <summary>
    /// 조건부 서식 규칙 하나. 목록의 앞 규칙이 우선한다 — 한 셀에 여러 규칙이 맞으면 배경·글자색·굵게를 각각 "처음 맞은 규칙"의 값으로 정한다.
    /// 컬럼은 이름으로 가리킨다(컬럼을 삭제·이름 변경하면 그 규칙은 "컬럼 없음" 문제로 표시되고 적용되지 않는다).
    /// 색은 "#RRGGBB"(또는 #RGB)나 HTML 색 이름. 저장 뷰(JSON)에 그대로 들어간다.
    /// </summary>
    public sealed record ConditionalFormatRule(
        string Id,
        string Name,
        bool Enabled,
        ConditionalFormatKind Kind,
        string Expression,
        ConditionalFormatTarget Target,
        string? Column,
        string? BackColor,
        string? ForeColor,
        bool Bold,
        string? ScaleMinColor,
        string? ScaleMidColor,
        string? ScaleMaxColor)
    {
        /// <summary>한 파일에 둘 수 있는 규칙 수(행 캐시가 규칙당 한 칸을 쓴다).</summary>
        public const int MaxRules = 32;

        /// <summary>색 문자열을 색으로. 비었거나 해석할 수 없으면 null.</summary>
        public static Color? ParseColor(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var c = ColorTranslator.FromHtml(text.Trim());
                if (c.IsEmpty) return null;
                return Color.FromArgb(255, c.R, c.G, c.B);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                return null; // FromHtml은 잘못된 색 문자열에 ArgumentException 등을 던진다
            }
        }

        /// <summary>색을 "#RRGGBB"로.</summary>
        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>한 줄 요약(관리 대화상자·에이전트 목록용).</summary>
        public string Summary()
        {
            string where = Target == ConditionalFormatTarget.Row ? "row" : $"cell in '{Column}'";
            if (Kind == ConditionalFormatKind.ColorScale)
            {
                string colors = string.Join("→", new[] { ScaleMinColor, ScaleMidColor, ScaleMaxColor }.Where(c => !string.IsNullOrWhiteSpace(c)));
                return $"color scale on '{Column}': {colors}";
            }
            var style = new List<string>();
            if (!string.IsNullOrWhiteSpace(BackColor)) style.Add("back " + BackColor);
            if (!string.IsNullOrWhiteSpace(ForeColor)) style.Add("text " + ForeColor);
            if (Bold) style.Add("bold");
            return $"{where} where {Expression} → {string.Join(", ", style)}";
        }
    }

    /// <summary>미리보기/에이전트용 개수. TimedOut = 정규식 시간 초과로 "일치하지 않음" 처리된 셀 수.</summary>
    public sealed record ConditionalFormatCount(long RowsScanned, long RowsMatched, long TimedOut);

    /// <summary>규칙 검증과 id 부여.</summary>
    public static class ConditionalFormatRules
    {
        /// <summary>규칙이 헤더에 대해 쓸 수 있는지 검사한다. 쓸 수 있으면 null, 아니면 이유(영어).</summary>
        public static string? Validate(ConditionalFormatRule rule, IReadOnlyList<string> headers)
        {
            if (rule.Kind == ConditionalFormatKind.Expression)
            {
                if (string.IsNullOrWhiteSpace(rule.Expression)) return "The condition expression is empty.";
                if (string.IsNullOrWhiteSpace(rule.BackColor) && string.IsNullOrWhiteSpace(rule.ForeColor) && !rule.Bold)
                    return "Give a back color, a text color or bold; otherwise the rule would not change how anything looks.";
                if (!string.IsNullOrWhiteSpace(rule.BackColor) && ConditionalFormatRule.ParseColor(rule.BackColor) is null)
                    return $"Back color '{rule.BackColor}' is not a color (use #RRGGBB or a color name such as red).";
                if (!string.IsNullOrWhiteSpace(rule.ForeColor) && ConditionalFormatRule.ParseColor(rule.ForeColor) is null)
                    return $"Text color '{rule.ForeColor}' is not a color (use #RRGGBB or a color name such as red).";
                try { AdvancedFilterExpression.Compile(rule.Expression, headers); }
                catch (AdvancedFilterExpressionException ex) { return "Invalid expression: " + ex.Message; }
                catch (RegexPatternException ex) { return "Invalid expression: " + ex.Message; }
                if (rule.Target == ConditionalFormatTarget.Cell && string.IsNullOrWhiteSpace(rule.Column))
                    return "A cell rule needs the column whose cells get the style.";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rule.Column)) return "A color scale needs a numeric column.";
                if (ConditionalFormatRule.ParseColor(rule.ScaleMinColor) is null) return "The color scale needs a valid minimum color.";
                if (ConditionalFormatRule.ParseColor(rule.ScaleMaxColor) is null) return "The color scale needs a valid maximum color.";
                if (!string.IsNullOrWhiteSpace(rule.ScaleMidColor) && ConditionalFormatRule.ParseColor(rule.ScaleMidColor) is null)
                    return $"Middle color '{rule.ScaleMidColor}' is not a color.";
            }
            if (!string.IsNullOrWhiteSpace(rule.Column) && ResolveColumn(headers, rule.Column) < 0)
                return $"Unknown column '{rule.Column}'.";
            return null;
        }

        /// <summary>컬럼 이름 → 번호. 정확 일치 → 대소문자 무시 유일 일치 → 빈 헤더의 "ColumnN". 못 찾거나 모호하면 -1.</summary>
        public static int ResolveColumn(IReadOnlyList<string> headers, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string key = name.Trim();
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], key, StringComparison.Ordinal)) return i;
            int hit = -1, hits = 0;
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i].Trim(), key, StringComparison.OrdinalIgnoreCase)) { hit = i; hits++; }
            if (hits == 1) return hit;
            if (hits > 1) return -1;
            if (key.StartsWith("Column", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key.AsSpan(6), out int n) && n >= 1 && n <= headers.Count && string.IsNullOrEmpty(headers[n - 1]))
                return n - 1;
            return -1;
        }

        /// <summary>"cf1", "cf2"… 중 기존 규칙과 겹치지 않는 다음 id.</summary>
        public static string NextId(IEnumerable<ConditionalFormatRule> existing)
        {
            int max = 0;
            foreach (var r in existing)
                if (r.Id.StartsWith("cf", StringComparison.Ordinal) && int.TryParse(r.Id.AsSpan(2), out int n) && n > max) max = n;
            return "cf" + (max + 1);
        }
    }
}
