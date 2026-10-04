using System.IO.Compression;
using System.Text;
using System.Xml;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 표 데이터를 단일 시트 .xlsx로 쓰는 스트리밍 작성기(순수 관리형, 추가 패키지 없음).
    /// 모든 값을 문자열(inlineStr)로 저장하므로 001의 선행 0·긴 숫자·날짜처럼 보이는 글자가 그대로 남는다.
    /// 엑셀이 열 때 "텍스트로 저장된 숫자" 표시가 뜰 수 있으나 값은 바뀌지 않는다.
    /// 보고서 내보내기 작성기(ReportExport)는 숫자처럼 보이는 값을 숫자로 바꾸므로(선행 0 손실) 데이터 저장에는 쓰지 않는다.
    /// </summary>
    public static class XlsxDataWriter
    {
        public const int MaxRows = 1_048_576;
        public const int MaxColumns = 16_384;
        public const int MaxCellCharacters = 32_767;

        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        /// <param name="rows">첫 행이 헤더. 각 행은 자기 길이만큼 셀을 쓴다(빈 문자열은 빈 셀).</param>
        /// <param name="expectedRows">미리 아는 총 행 수(헤더 포함, 모르면 -1). 한도를 넘으면 쓰기 전에 거부.</param>
        public static void Write(Stream stream, string sheetName, IEnumerable<string[]> rows, long expectedRows = -1)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(rows);
            if (expectedRows > MaxRows)
                throw new InvalidOperationException(LT(
                    $"Excel supports at most {MaxRows:N0} rows; this table has {expectedRows:N0} (including the header). Save as CSV instead.",
                    $"엑셀은 최대 {MaxRows:N0}행까지 지원하지만 이 표는 헤더 포함 {expectedRows:N0}행입니다. CSV로 저장하세요."));

            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            Entry(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default(w, "xml", "application/xml");
                Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                Override(w, "/xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                w.WriteEndElement();
            });
            Entry(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", PkgRelNs);
                Rel(w, "rId1", RelNs + "/officeDocument", "xl/workbook.xml");
                w.WriteEndElement();
            });
            Entry(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", MainNs);
                w.WriteAttributeString("xmlns", "r", null, RelNs);
                w.WriteStartElement("sheets");
                w.WriteStartElement("sheet");
                w.WriteAttributeString("name", SanitizeSheetName(sheetName));
                w.WriteAttributeString("sheetId", "1");
                w.WriteAttributeString("r", "id", RelNs, "rId1");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            });
            Entry(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", PkgRelNs);
                Rel(w, "rId1", RelNs + "/worksheet", "worksheets/sheet1.xml");
                Rel(w, "rId2", RelNs + "/styles", "styles.xml");
                w.WriteEndElement();
            });
            Entry(zip, "xl/styles.xml", WriteStyles);
            Entry(zip, "xl/worksheets/sheet1.xml", w => WriteSheet(w, rows));
        }

        public static string SanitizeSheetName(string? raw)
        {
            var sb = new StringBuilder();
            foreach (char ch in (raw ?? "").Trim())
            {
                if (ch < 32) continue;
                sb.Append("\\/?*[]:".Contains(ch) ? '_' : ch);
            }
            string name = sb.ToString().Trim().Trim('\'');
            if (name.Length > 31) name = name[..31].Trim().Trim('\'');
            return name.Length == 0 ? "Sheet1" : name;
        }

        private static void WriteSheet(XmlWriter w, IEnumerable<string[]> rows)
        {
            w.WriteStartElement("worksheet", MainNs);
            w.WriteStartElement("sheetData");
            int r = 0;
            foreach (var row in rows)
            {
                r++;
                if (r > MaxRows)
                    throw new InvalidOperationException(LT(
                        $"Excel supports at most {MaxRows:N0} rows. Save as CSV instead.",
                        $"엑셀은 최대 {MaxRows:N0}행까지 지원합니다. CSV로 저장하세요."));
                if (row.Length > MaxColumns)
                    throw new InvalidOperationException(LT(
                        $"Excel supports at most {MaxColumns:N0} columns; row {r:N0} has {row.Length:N0}. Save as CSV instead.",
                        $"엑셀은 최대 {MaxColumns:N0}열까지 지원하지만 {r:N0}행에 {row.Length:N0}열이 있습니다. CSV로 저장하세요."));
                w.WriteStartElement("row");
                w.WriteAttributeString("r", r.ToString(System.Globalization.CultureInfo.InvariantCulture));
                for (int c = 0; c < row.Length; c++)
                {
                    string text = row[c] ?? "";
                    if (text.Length == 0) continue; // 빈 셀은 쓰지 않는다
                    if (text.Length > MaxCellCharacters)
                        throw new InvalidOperationException(LT(
                            $"Row {r:N0}, column {c + 1:N0} has {text.Length:N0} characters; Excel allows at most {MaxCellCharacters:N0} per cell. Save as CSV instead.",
                            $"{r:N0}행 {c + 1:N0}열이 {text.Length:N0}자입니다. 엑셀 셀 한도는 {MaxCellCharacters:N0}자입니다. CSV로 저장하세요."));
                    w.WriteStartElement("c");
                    w.WriteAttributeString("r", ColName(c) + r.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteAttributeString("t", "inlineStr");
                    w.WriteStartElement("is");
                    w.WriteStartElement("t");
                    if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n') || text.Contains('\r'))
                        w.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                    w.WriteString(EscapeXmlString(text));
                    w.WriteEndElement();
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        /// <summary>
        /// 엑셀의 ST_Xstring 이스케이프: XML 1.0에 쓸 수 없는 문자(제어문자·짝 없는 서로게이트)는 _xHHHH_로,
        /// 글자 그대로의 "_xHHHH_" 형태는 앞 밑줄을 _x005F_로 바꿔 구분한다. 읽는 쪽이 원래 문자열을 복원한다.
        /// </summary>
        public static string EscapeXmlString(string s)
        {
            StringBuilder? sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                string? repl = null;
                if (ch == '_' && IsHexEscapeAt(s, i)) repl = "_x005F_";
                else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') repl = "_x" + ((int)ch).ToString("X4") + "_";
                else if (ch == '\uFFFE' || ch == '\uFFFF') repl = "_x" + ((int)ch).ToString("X4") + "_";
                else if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb?.Append(ch).Append(s[i + 1]); i++; continue; }
                    repl = "_x" + ((int)ch).ToString("X4") + "_";
                }
                else if (char.IsLowSurrogate(ch)) repl = "_x" + ((int)ch).ToString("X4") + "_";

                if (repl is null) { sb?.Append(ch); continue; }
                sb ??= new StringBuilder(s, 0, i, s.Length + 16);
                sb.Append(repl);
            }
            return sb?.ToString() ?? s;
        }

        private static bool IsHexEscapeAt(string s, int i)
        {
            // "_x" + 4 hex + "_"
            if (i + 6 >= s.Length) return false;
            if (s[i + 1] != 'x' && s[i + 1] != 'X') return false;
            for (int k = 2; k <= 5; k++) if (!Uri.IsHexDigit(s[i + k])) return false;
            return s[i + 6] == '_';
        }

        private static string ColName(int col)
        {
            col++;
            var chars = new Stack<char>();
            while (col > 0)
            {
                col--;
                chars.Push((char)('A' + col % 26));
                col /= 26;
            }
            return new string(chars.ToArray());
        }

        private static void WriteStyles(XmlWriter w)
        {
            w.WriteStartElement("styleSheet", MainNs);
            w.WriteStartElement("fonts"); w.WriteAttributeString("count", "1");
            w.WriteStartElement("font");
            w.WriteStartElement("sz"); w.WriteAttributeString("val", "11"); w.WriteEndElement();
            w.WriteStartElement("name"); w.WriteAttributeString("val", "Calibri"); w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("fills"); w.WriteAttributeString("count", "2");
            foreach (string pattern in new[] { "none", "gray125" })
            {
                w.WriteStartElement("fill");
                w.WriteStartElement("patternFill"); w.WriteAttributeString("patternType", pattern); w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteStartElement("borders"); w.WriteAttributeString("count", "1");
            w.WriteStartElement("border");
            foreach (string edge in new[] { "left", "right", "top", "bottom", "diagonal" }) { w.WriteStartElement(edge); w.WriteEndElement(); }
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("cellStyleXfs"); w.WriteAttributeString("count", "1");
            Xf(w, withXfId: false);
            w.WriteEndElement();
            w.WriteStartElement("cellXfs"); w.WriteAttributeString("count", "1");
            Xf(w, withXfId: true);
            w.WriteEndElement();
            w.WriteStartElement("cellStyles"); w.WriteAttributeString("count", "1");
            w.WriteStartElement("cellStyle");
            w.WriteAttributeString("name", "Normal");
            w.WriteAttributeString("xfId", "0");
            w.WriteAttributeString("builtinId", "0");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void Xf(XmlWriter w, bool withXfId)
        {
            w.WriteStartElement("xf");
            w.WriteAttributeString("numFmtId", "0");
            w.WriteAttributeString("fontId", "0");
            w.WriteAttributeString("fillId", "0");
            w.WriteAttributeString("borderId", "0");
            if (withXfId) w.WriteAttributeString("xfId", "0");
            w.WriteEndElement();
        }

        private static void Default(XmlWriter w, string ext, string type)
        {
            w.WriteStartElement("Default");
            w.WriteAttributeString("Extension", ext);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        private static void Override(XmlWriter w, string part, string type)
        {
            w.WriteStartElement("Override");
            w.WriteAttributeString("PartName", part);
            w.WriteAttributeString("ContentType", type);
            w.WriteEndElement();
        }

        private static void Rel(XmlWriter w, string id, string type, string target)
        {
            w.WriteStartElement("Relationship");
            w.WriteAttributeString("Id", id);
            w.WriteAttributeString("Type", type);
            w.WriteAttributeString("Target", target);
            w.WriteEndElement();
        }

        private static void Entry(ZipArchive zip, string name, Action<XmlWriter> write)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                OmitXmlDeclaration = false,
                Indent = false,
                NewLineHandling = NewLineHandling.Entitize, // 셀 안의 \r\n을 바꾸지 않고 그대로 보존
                CloseOutput = false,
            };
            using var writer = XmlWriter.Create(stream, settings);
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }
    }
}
