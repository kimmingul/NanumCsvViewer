namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 클립보드 텍스트(엑셀 호환 TSV)를 셀 블록으로 해석한다: 탭 = 열 구분, 줄바꿈(CRLF/LF/CR) = 행 구분,
    /// 큰따옴표로 감싼 셀은 안의 탭·줄바꿈을 그대로 두고 ""는 "로 읽는다.
    /// 엑셀은 마지막 행 끝에도 줄바꿈을 붙이므로 끝의 줄바꿈 하나는 행으로 세지 않는다.
    /// 값은 해석하지 않는다(숫자 변환 없음).
    /// </summary>
    public static class ClipboardGrid
    {
        /// <summary>직사각형(짧은 행은 빈 문자열로 채움) 블록. 빈 텍스트면 행 0개.</summary>
        public static string[][] Parse(string? text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string[]>();

            var rows = new List<string[]>();
            var row = new List<string>();
            int i = 0, n = text.Length;
            while (i < n)
            {
                string field;
                if (text[i] == '"' && TryReadQuoted(text, i, out field, out int next))
                {
                    i = next;
                }
                else
                {
                    int start = i;
                    while (i < n && text[i] != '\t' && text[i] != '\r' && text[i] != '\n') i++;
                    field = text.Substring(start, i - start);
                }
                row.Add(field);
                if (i >= n) break;

                char c = text[i];
                if (c == '\t')
                {
                    i++;
                    if (i >= n) row.Add(string.Empty); // 끝의 탭 뒤 빈 셀
                    continue;
                }
                if (c == '\r' && i + 1 < n && text[i + 1] == '\n') i += 2; else i++;
                rows.Add(row.ToArray());
                row.Clear();
            }
            if (row.Count > 0) rows.Add(row.ToArray());

            int width = 0;
            foreach (var r in rows) if (r.Length > width) width = r.Length;
            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].Length == width) continue;
                var padded = new string[width];
                Array.Copy(rows[r], padded, rows[r].Length);
                for (int c = rows[r].Length; c < width; c++) padded[c] = string.Empty;
                rows[r] = padded;
            }
            return rows.ToArray();
        }

        // 닫는 따옴표 뒤가 탭/줄바꿈/끝이어야 따옴표 셀로 인정한다(그 밖은 일반 글자로 취급).
        private static bool TryReadQuoted(string text, int start, out string value, out int next)
        {
            var sb = new System.Text.StringBuilder();
            int j = start + 1, n = text.Length;
            while (j < n)
            {
                char c = text[j];
                if (c == '"')
                {
                    if (j + 1 < n && text[j + 1] == '"') { sb.Append('"'); j += 2; continue; }
                    j++;
                    if (j == n || text[j] == '\t' || text[j] == '\r' || text[j] == '\n')
                    {
                        value = sb.ToString();
                        next = j;
                        return true;
                    }
                    break;
                }
                sb.Append(c);
                j++;
            }
            value = string.Empty;
            next = start;
            return false;
        }
    }
}
