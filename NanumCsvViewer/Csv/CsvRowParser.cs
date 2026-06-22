using System.Text;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 이미 디코드된 한 레코드(줄바꿈 제거됨)를 필드 배열로 파싱합니다.
    /// RFC 4180 따옴표 규칙: 따옴표로 감싼 필드는 구분자/줄바꿈을 포함할 수 있고,
    /// 필드 내 따옴표는 ""로 이스케이프합니다. 가상 모드의 핫 패스용으로 손수 구현.
    /// </summary>
    public static class CsvRowParser
    {
        public static string[] Parse(ReadOnlySpan<char> line, char delimiter, char quote = '"')
        {
            var fields = new List<string>(16);
            int i = 0, n = line.Length;
            StringBuilder? sb = null;

            while (true)
            {
                if (i < n && line[i] == quote)
                {
                    // 따옴표로 감싼 필드
                    sb ??= new StringBuilder();
                    sb.Clear();
                    i++; // 여는 따옴표 건너뜀
                    while (i < n)
                    {
                        char c = line[i];
                        if (c == quote)
                        {
                            if (i + 1 < n && line[i + 1] == quote) { sb.Append(quote); i += 2; }
                            else { i++; break; } // 닫는 따옴표
                        }
                        else if (c == '\r')
                        {
                            // 셀 내 줄바꿈은 LF로 정규화(\r\n, 단독 \r → \n) → 그리드/표시줄에서 일관되게 줄바꿈
                            sb.Append('\n');
                            if (i + 1 < n && line[i + 1] == '\n') i += 2; else i++;
                        }
                        else { sb.Append(c); i++; }
                    }
                    // 닫는 따옴표 뒤, 구분자 전의 이질적 문자는 관대하게 이어붙임
                    while (i < n && line[i] != delimiter) { sb.Append(line[i]); i++; }
                    fields.Add(sb.ToString());
                }
                else
                {
                    int start = i;
                    while (i < n && line[i] != delimiter) i++;
                    fields.Add(line.Slice(start, i - start).ToString());
                }

                if (i < n && line[i] == delimiter) { i++; continue; }
                break;
            }

            return fields.ToArray();
        }
    }
}
