using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// "셀로 이동" 입력의 해석 결과. Row = 행 머리글 번호(1-based, 편집 후 순서), Column = 0-based 컬럼 번호(번호로 지정한 경우),
    /// ColumnName = 이름으로 지정한 컬럼(헤더와의 대조는 <see cref="TryResolve"/>).
    /// </summary>
    public readonly record struct CellAddress(long? Row, int? Column, string? ColumnName)
    {
        /// <summary>
        /// 문법 (대소문자 무시, 앞뒤 공백 무시):
        ///  - <c>120</c> · <c>1,234</c> · <c>R120</c>      행 번호만
        ///  - <c>R120C3</c> · <c>r120c3</c>                  행 + 컬럼 번호(1-based)
        ///  - <c>C3</c>                                       컬럼 번호만
        ///  - <c>이름:120</c> · <c>[이름]120</c> · <c>이름:</c> · <c>[이름]</c>   컬럼 이름 + 행(생략 가능)
        ///  - <c>R120 · 이름</c>                              주소 상자에 표시되는 형식 그대로(왕복 가능)
        ///  - 그 밖의 글자는 컬럼 이름으로 본다(없는 이름이면 해석 단계에서 오류).
        /// </summary>
        public static bool TryParse(string? text, out CellAddress address, out string error)
        {
            address = default;
            error = "";
            string s = (text ?? "").Trim();
            if (s.Length == 0) { error = "Enter a row number, R12C3, C3 or Name:12."; return false; }

            // 1) 행 번호만 ("120", "1,234", "R120")
            if (TryRowNumber(s, out long onlyRow, out bool rowOverflow))
            {
                if (rowOverflow || onlyRow < 1) { error = RowError(s); return false; }
                address = new CellAddress(onlyRow, null, null);
                return true;
            }
            if (rowOverflow) { error = RowError(s); return false; }

            // 2) R12C3 / C3
            if (TryRowColumn(s, out var rc, out error)) { address = rc; return true; }
            if (error.Length > 0) return false;

            // 3) 주소 상자 표시 형식 "R120 · 이름"
            int dot = s.IndexOf('·');
            if (dot > 0 && TryRowNumber(s[..dot].TrimEnd(), out long shownRow, out bool ovf) && !ovf && shownRow >= 1)
            {
                string shownName = s[(dot + 1)..].Trim();
                address = new CellAddress(shownRow, null, shownName.Length == 0 ? null : shownName);
                return true;
            }

            // 4) [이름]120 / [이름]
            if (s[0] == '[')
            {
                int close = s.LastIndexOf(']');
                // 이름 안에 ']'가 있을 수 있으므로, 뒤에 오는 부분이 숫자(또는 비어 있음)가 되는 마지막 ']'를 쓴다.
                while (close > 0)
                {
                    string tail = s[(close + 1)..].Trim();
                    if (tail.Length == 0 || TryRowNumber(tail, out _, out _))
                        return Named(s.Substring(1, close - 1), tail, out address, out error);
                    close = s.LastIndexOf(']', close - 1);
                }
                error = "Unmatched '[': use [Name]120.";
                return false;
            }

            // 5) 이름:120 / 이름:  (마지막 ':' 뒤가 숫자이거나 비어 있으면 이름 + 행)
            int colon = s.LastIndexOf(':');
            if (colon >= 0)
            {
                string tail = s[(colon + 1)..].Trim();
                if (tail.Length == 0 || TryRowNumber(tail, out _, out _))
                    return Named(s[..colon], tail, out address, out error);
            }

            // 6) 숫자처럼 생겼는데 행 번호로 못 읽는 입력(-5, 3.5, 1,2)은 컬럼 이름으로 오해하지 않고 거절한다.
            if (LooksNumeric(s)) { error = RowError(s); return false; }

            // 7) 컬럼 이름만
            address = new CellAddress(null, null, s);
            return true;
        }

        private static bool LooksNumeric(string s)
        {
            int i = s[0] is '+' or '-' ? 1 : 0;
            if (i >= s.Length || !char.IsAsciiDigit(s[i])) return false;
            for (; i < s.Length; i++) if (!(char.IsAsciiDigit(s[i]) || s[i] is '.' or ',')) return false;
            return true;
        }

        private static bool Named(string rawName, string rowText, out CellAddress address, out string error)
        {
            address = default;
            error = "";
            string name = rawName.Trim();
            if (name.Length == 0) { error = "The column name is empty."; return false; }
            long? row = null;
            if (rowText.Length > 0)
            {
                if (!TryRowNumber(rowText, out long r, out bool ovf) || ovf || r < 1) { error = RowError(rowText); return false; }
                row = r;
            }
            address = new CellAddress(row, null, name);
            return true;
        }

        private static string RowError(string s) => $"'{s}' is not a valid row number (rows start at 1).";

        // "120", "1,234", "R120", "r1,234". 쉼표는 3자리 묶음이어야 한다("1,2" 같은 입력은 숫자가 아님).
        private static bool TryRowNumber(string s, out long row, out bool overflow)
        {
            row = 0;
            overflow = false;
            ReadOnlySpan<char> t = s.AsSpan().Trim();
            if (t.Length > 0 && (t[0] == 'R' || t[0] == 'r')) t = t[1..];
            if (t.Length == 0 || !char.IsAsciiDigit(t[0])) return false;
            long value = 0;
            int groupLen = -1; // 쉼표 뒤 자릿수. -1 = 쉼표 없음
            bool seenComma = false;
            int firstGroup = 0;
            for (int i = 0; i < t.Length; i++)
            {
                char ch = t[i];
                if (ch == ',')
                {
                    if (!seenComma) { seenComma = true; firstGroup = i; if (firstGroup < 1 || firstGroup > 3) return false; }
                    else if (groupLen != 3) return false;
                    groupLen = 0;
                    continue;
                }
                if (!char.IsAsciiDigit(ch)) return false;
                if (seenComma) groupLen++;
                if (value > (long.MaxValue - 9) / 10) { overflow = true; return true; }
                value = value * 10 + (ch - '0');
            }
            if (seenComma && groupLen != 3) return false;
            row = value;
            return true;
        }

        // R12C3 / C3. 모양이 맞지 않으면 false + error 비움(다른 형식 시도), 모양은 맞는데 값이 잘못이면 false + error.
        private static bool TryRowColumn(string s, out CellAddress address, out string error)
        {
            address = default;
            error = "";
            ReadOnlySpan<char> t = s.AsSpan();
            int i = 0;
            long? row = null;
            if (t[i] is 'R' or 'r')
            {
                i++;
                int start = i;
                while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
                if (i == start) return false;
                if (!long.TryParse(t[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out long r)) { error = RowError(s); return false; }
                if (r < 1) { error = RowError(s); return false; }
                row = r;
                if (i == t.Length) return false; // "R120"은 행 번호만 — 앞선 단계에서 처리됨
            }
            if (i < t.Length && t[i] is 'C' or 'c')
            {
                i++;
                int start = i;
                while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
                if (i == start || i != t.Length) { return false; }
                if (!int.TryParse(t[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out int c) || c < 1)
                {
                    error = $"'{s}' is not a valid column number (columns start at 1).";
                    return false;
                }
                address = new CellAddress(row, c - 1, null);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 헤더와 대조해 (행, 0-based 컬럼)으로 만든다. 번호 지정이 범위 밖이거나 이름이 없음/모호하면 false + 이유.
        /// 이름 대조: 정확히 일치 → 대소문자 무시 유일 일치 → 이름이 빈 컬럼의 "Column&lt;N&gt;".
        /// </summary>
        public bool TryResolve(IReadOnlyList<string> headers, out long? row, out int? column, out string error)
        {
            row = Row;
            column = null;
            error = "";
            if (Column is { } c)
            {
                if (c >= headers.Count) { error = $"Column {c + 1} does not exist (the table has {headers.Count} columns)."; return false; }
                column = c;
                return true;
            }
            if (ColumnName is not { } name) return true;

            string key = name.Trim();
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], key, StringComparison.Ordinal)) { column = i; return true; }

            int hit = -1, hits = 0;
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i].Trim(), key, StringComparison.OrdinalIgnoreCase)) { hit = i; hits++; }
            if (hits == 1) { column = hit; return true; }
            if (hits > 1) { error = $"Column name '{name}' is ambiguous (it matches {hits} columns that differ only by case)."; return false; }

            if (key.StartsWith("Column", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                && n >= 1 && n <= headers.Count && string.IsNullOrEmpty(headers[n - 1]))
            {
                column = n - 1;
                return true;
            }
            error = $"Unknown column '{name}'.";
            return false;
        }
    }
}
