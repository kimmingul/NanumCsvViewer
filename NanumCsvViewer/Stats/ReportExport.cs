using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace NanumCsvViewer.Stats
{
    /// <summary>보고서 머리말(파일·시각·버전·현재 뷰). 라벨은 UI가 현지화해 넘긴다.</summary>
    public sealed record ReportContext(
        string FileName,
        string AppVersion,
        string ViewScope,
        string FileLabel = "File",
        string AnalyzedLabel = "Analyzed",
        string VersionLabel = "App version",
        string ScopeLabel = "View scope");

    /// <summary>본문 블록. 표는 <see cref="CapturedTable.Rendered"/>가 본문에서 찾아진 것만.</summary>
    public abstract record ReportBlock;

    /// <summary>문단. Preformatted면 표 치환에 실패해 원문을 그대로 둔 블록이다.</summary>
    public sealed record ReportTextBlock(string Text, bool Preformatted) : ReportBlock;

    /// <summary>실제 표. Name은 직전 비어 있지 않은 줄(시트 이름 후보, 아직 정제 전).</summary>
    public sealed record ReportTableBlock(
        string Name,
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> Rows) : ReportBlock;

    /// <summary>내보내기용으로 조립한 보고서. AnalyzedAt은 문화권 무관 <c>yyyy-MM-dd HH:mm:ss</c>.</summary>
    public sealed record ReportDocument(
        string Title,
        string FileName,
        string AnalyzedAt,
        string AppVersion,
        string ViewScope,
        string FileLabel,
        string AnalyzedLabel,
        string VersionLabel,
        string ScopeLabel,
        IReadOnlyList<ReportBlock> Blocks,
        bool Preformatted);

    /// <summary>페이지에 놓을 한 조각. Height는 측정기 단위.</summary>
    public abstract record ReportLayoutItem(float Height);

    /// <summary>Mono는 표를 찾지 못해 원문 그대로 둔 본문(격자 정렬 고정폭).</summary>
    public enum ReportTextRole { Title, Meta, Body, TableHeader, Mono }

    public sealed record ReportLayoutText(string Text, ReportTextRole Role, float Height) : ReportLayoutItem(Height);

    public sealed record ReportLayoutSpacer(float Height) : ReportLayoutItem(Height);

    /// <summary>
    /// 표의 한 행. WrappedLines는 높이 계산에 쓴 줄 나눔이며, PDF는 이 줄을 그대로 그린다.
    /// PadX/PadY/LineHeight도 그 측정과 같다. 페이지가 넘어가면 Header가 다시 나온다.
    /// </summary>
    public sealed record ReportLayoutTableRow(
        string TableName,
        bool Header,
        IReadOnlyList<string> Cells,
        IReadOnlyList<float> ColumnWidths,
        float Height,
        IReadOnlyList<IReadOnlyList<string>> WrappedLines,
        float PadX,
        float PadY,
        float LineHeight) : ReportLayoutItem(Height);

    public sealed record ReportPage(int Number, IReadOnlyList<ReportLayoutItem> Items);

    /// <summary>쪽 나눔이 글꼴 측정만 의존하도록 분리한다. 테스트는 글자 수 측정기를 넣는다.</summary>
    public interface IReportMeasurer
    {
        float ContentWidth { get; }
        float ContentHeight { get; }
        float LineHeight(ReportTextRole role);
        float Measure(string text, ReportTextRole role);
        float CellPadX { get; }
        float CellPadY { get; }
        float BlockGap { get; }
    }

    /// <summary>
    /// 고급 통계 보고서 내보내기(이슈 #27 Phase 3). HTML·XLSX·PDF 모두 패키지·프린터 없이 직접 쓴다.
    /// PDF는 <see cref="ReportPdf"/>(<see cref="PdfDocument"/>)가 만든다. 쪽 나눔은 <see cref="Paginate"/>가 순수하게 계산한다.
    /// </summary>
    public static class ReportExport
    {
        public const string ReportSheetName = "Report";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// 본문에서 각 표의 렌더 문자열을 찾아 실제 표로 바꾼다. 하나도 찾지 못하면 본문 전체를 고정폭 텍스트로 둔다.
        /// 일부만 찾으면 찾은 것만 표로 두고, 없는 표는 끼워 넣지 않는다.
        /// </summary>
        public static ReportDocument Compose(AdvancedReport report, ReportContext context)
        {
            ArgumentNullException.ThrowIfNull(report);
            ArgumentNullException.ThrowIfNull(context);
            string text = Normalize(report.Text);
            var tables = report.Tables ?? Array.Empty<CapturedTable>();
            var hits = new List<(int Start, int End, int Index)>();
            int cursor = 0;
            for (int i = 0; i < tables.Count; i++)
            {
                string rendered = Normalize(tables[i].Rendered);
                if (rendered.Length == 0) continue;
                int at = text.IndexOf(rendered, cursor, StringComparison.Ordinal);
                if (at < 0) continue;
                hits.Add((at, at + rendered.Length, i));
                cursor = at + rendered.Length;
            }

            var blocks = new List<ReportBlock>();
            bool preformatted = hits.Count == 0;
            if (preformatted)
            {
                blocks.Add(new ReportTextBlock(text, Preformatted: true));
            }
            else
            {
                int pos = 0;
                foreach (var hit in hits)
                {
                    if (hit.Start > pos) AddParagraphs(blocks, text[pos..hit.Start]);
                    var table = tables[hit.Index];
                    blocks.Add(new ReportTableBlock(
                        CaptionBefore(text, hit.Start, hit.Index),
                        table.Headers ?? Array.Empty<string>(),
                        table.Rows ?? Array.Empty<IReadOnlyList<string>>()));
                    pos = hit.End;
                }
                if (pos < text.Length) AddParagraphs(blocks, text[pos..]);
            }

            return new ReportDocument(
                report.Title ?? "",
                context.FileName ?? "",
                report.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                context.AppVersion ?? "",
                context.ViewScope ?? "",
                context.FileLabel ?? "File",
                context.AnalyzedLabel ?? "Analyzed",
                context.VersionLabel ?? "App version",
                context.ScopeLabel ?? "View scope",
                blocks,
                preformatted);
        }

        public static string ToHtml(ReportDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>")
                .Append(Html(document.Title.Length == 0 ? "Report" : document.Title))
                .Append("</title>\n<style>\n")
                .Append(HtmlCss)
                .Append("\n</style>\n</head>\n<body>\n<header>\n<h1>")
                .Append(Html(document.Title))
                .Append("</h1>\n<dl class=\"meta\">\n");
            Meta(sb, document.FileLabel, document.FileName);
            Meta(sb, document.AnalyzedLabel, document.AnalyzedAt);
            Meta(sb, document.VersionLabel, document.AppVersion);
            Meta(sb, document.ScopeLabel, document.ViewScope);
            sb.Append("</dl>\n</header>\n<main>\n");
            foreach (var block in document.Blocks)
            {
                if (block is ReportTextBlock text)
                {
                    if (text.Preformatted)
                        sb.Append("<pre>").Append(Html(text.Text)).Append("</pre>\n");
                    else
                        sb.Append("<p>").Append(Html(text.Text).Replace("\n", "<br>\n")).Append("</p>\n");
                }
                else if (block is ReportTableBlock table)
                {
                    sb.Append("<table class=\"data\">\n<thead><tr>");
                    foreach (var h in table.Headers)
                        sb.Append("<th>").Append(Html(h)).Append("</th>");
                    sb.Append("</tr></thead>\n<tbody>\n");
                    foreach (var row in table.Rows)
                    {
                        sb.Append("<tr>");
                        for (int c = 0; c < table.Headers.Count; c++)
                        {
                            string cell = CellAt(row, c);
                            bool num = TryParseNumber(cell, out _);
                            sb.Append(num ? "<td class=\"num\">" : "<td>").Append(Html(cell)).Append("</td>");
                        }
                        sb.Append("</tr>\n");
                    }
                    sb.Append("</tbody></table>\n");
                }
            }
            sb.Append("</main>\n</body>\n</html>\n");
            return sb.ToString();
        }

        public static void WriteHtml(ReportDocument document, string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            WriteAtomic(path, fs =>
            {
                using var writer = new StreamWriter(fs, Utf8);
                writer.Write(ToHtml(document));
            });
        }

        public static void WriteXlsx(ReportDocument document, Stream stream)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(stream);
            var sheets = BuildSheets(document);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            WriteEntry(zip, "[Content_Types].xml", w => WriteContentTypes(w, sheets.Count));
            WriteEntry(zip, "_rels/.rels", WriteRootRels);
            WriteEntry(zip, "xl/workbook.xml", w => WriteWorkbook(w, sheets));
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", w => WriteWorkbookRels(w, sheets.Count));
            WriteEntry(zip, "xl/styles.xml", WriteStyles);
            for (int i = 0; i < sheets.Count; i++)
                WriteEntry(zip, "xl/worksheets/sheet" + (i + 1).ToString(Inv) + ".xml", w => WriteSheet(w, sheets[i]));
        }

        public static void WriteXlsx(ReportDocument document, string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            WriteAtomic(path, fs => WriteXlsx(document, fs));
        }

        /// <summary>
        /// 보고서를 직접 작성한 PDF(A4, 한글 글꼴 부분집합 내장)로 지정 경로에 쓴다. 인쇄 드라이버를 쓰지 않는다.
        /// 한글 TrueType 글꼴이 없으면 <see cref="PdfFontNotFoundException"/>(파일은 만들지 않는다).
        /// </summary>
        public static void WritePdf(ReportDocument document, string path)
            => WritePdf(document, path, ReportPdfFonts.Locate());

        public static void WritePdf(ReportDocument document, string path, ReportPdfFonts fonts)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(fonts);
            WriteAtomic(path, fs => ReportPdf.Write(document, fs, fonts));
        }

        public static void WritePdf(ReportDocument document, Stream stream, ReportPdfFonts fonts)
            => ReportPdf.Write(document, stream, fonts);

        /// <summary>
        /// 제목·메타는 첫 페이지만. 표가 페이지를 넘으면 머리글을 반복한다.
        /// 머리글과 첫 데이터 행이 함께 들어가지 않으면 다음 페이지로 넘긴다.
        /// 한 행이 페이지보다 커도 그 페이지에 두고 진행한다(무한 루프 없음).
        /// </summary>
        public static IReadOnlyList<ReportPage> Paginate(ReportDocument document, IReportMeasurer measure)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(measure);
            var pages = new List<ReportPage>();
            var items = new List<ReportLayoutItem>();
            float y = 0;
            bool breakable = measure.ContentHeight > 0;

            void Flush()
            {
                if (items.Count == 0 && pages.Count > 0) return;
                pages.Add(new ReportPage(pages.Count + 1, items.ToArray()));
                items = new List<ReportLayoutItem>();
                y = 0;
            }

            void Place(ReportLayoutItem item)
            {
                if (breakable && y > 0 && y + item.Height > measure.ContentHeight + 0.01f)
                    Flush();
                items.Add(item);
                y += item.Height;
            }

            void PlaceWrapped(string text, ReportTextRole role)
            {
                float width = Math.Max(1f, measure.ContentWidth);
                foreach (string line in WrapLine(text, width, s => measure.Measure(s, role)))
                    Place(new ReportLayoutText(line, role, measure.LineHeight(role)));
            }

            PlaceWrapped(document.Title, ReportTextRole.Title);
            PlaceWrapped(document.FileLabel + ": " + document.FileName, ReportTextRole.Meta);
            PlaceWrapped(document.AnalyzedLabel + ": " + document.AnalyzedAt, ReportTextRole.Meta);
            PlaceWrapped(document.VersionLabel + ": " + document.AppVersion, ReportTextRole.Meta);
            PlaceWrapped(document.ScopeLabel + ": " + document.ViewScope, ReportTextRole.Meta);
            if (measure.BlockGap > 0) Place(new ReportLayoutSpacer(measure.BlockGap));

            foreach (var block in document.Blocks)
            {
                if (block is ReportTextBlock text)
                {
                    var role = text.Preformatted ? ReportTextRole.Mono : ReportTextRole.Body;
                    foreach (string raw in text.Text.Replace("\r\n", "\n").Split('\n'))
                        PlaceWrapped(raw, role);
                    if (measure.BlockGap > 0) Place(new ReportLayoutSpacer(measure.BlockGap));
                }
                else if (block is ReportTableBlock table)
                {
                    var widths = ColumnWidths(table, measure);
                    var header = MakeRow(table.Name, header: true, table.Headers, widths, measure);
                    var data = new List<ReportLayoutTableRow>();
                    foreach (var row in table.Rows)
                    {
                        var cells = new string[table.Headers.Count];
                        for (int c = 0; c < cells.Length; c++) cells[c] = CellAt(row, c);
                        data.Add(MakeRow(table.Name, header: false, cells, widths, measure));
                    }

                    float keep = header.Height + (data.Count > 0 ? data[0].Height : 0);
                    if (breakable && y > 0 && y + keep > measure.ContentHeight + 0.01f)
                        Flush();
                    Place(header);
                    foreach (var row in data)
                    {
                        if (breakable && y > 0 && y + row.Height > measure.ContentHeight + 0.01f)
                        {
                            Flush();
                            if (header.Height + row.Height <= measure.ContentHeight + 0.01f)
                                Place(header);
                        }
                        Place(row);
                    }
                    if (measure.BlockGap > 0) Place(new ReportLayoutSpacer(measure.BlockGap));
                }
            }

            if (items.Count > 0 || pages.Count == 0) Flush();
            return pages;
        }

        /// <summary>
        /// 숫자가 되는 셀만 숫자로 둔다. <c>&lt;0.0001</c>처럼 비교 부호로 시작하면 텍스트.
        /// 천단위 쉼표(Invariant <c>N0</c>)는 숫자로 읽는다.
        /// </summary>
        public static bool TryParseNumber(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length == 0 || text[0] is '<' or '>') return false;
            const NumberStyles style = NumberStyles.Float | NumberStyles.AllowThousands;
            if (!double.TryParse(text, style, Inv, out value)) return false;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>한 줄을 maxWidth에 맞게 자른다. 가능하면 공백에서 끊고, 한 글자는 항상 진행한다.</summary>
        public static IReadOnlyList<string> WrapLine(string text, float maxWidth, Func<string, float> measure)
        {
            if (string.IsNullOrEmpty(text) || !(maxWidth > 0) || measure(text) <= maxWidth)
                return new[] { text ?? "" };

            var lines = new List<string>();
            int start = 0;
            while (start < text.Length)
            {
                int lastFit = start;
                int breakAfterSpace = -1;
                for (int end = start + 1; end <= text.Length; end++)
                {
                    if (measure(text[start..end]) > maxWidth) break;
                    lastFit = end;
                    if (text[end - 1] == ' ') breakAfterSpace = end;
                }
                if (lastFit <= start) lastFit = Math.Min(text.Length, start + 1);
                int cut = lastFit;
                if (breakAfterSpace > start && breakAfterSpace < lastFit && lastFit < text.Length)
                    cut = breakAfterSpace;
                if (cut >= text.Length)
                {
                    lines.Add(text[start..].TrimEnd(' '));
                    break;
                }
                string piece = text[start..cut].TrimEnd(' ');
                int next = cut;
                if (piece.Length == 0)
                {
                    piece = text.Substring(start, 1);
                    next = start + 1;
                }
                lines.Add(piece);
                while (next < text.Length && text[next] == ' ') next++;
                if (next <= start) next = start + 1;
                start = next;
            }
            return lines;
        }

        private static void AddParagraphs(List<ReportBlock> blocks, string segment)
        {
            var current = new StringBuilder();
            foreach (string line in segment.Split('\n'))
            {
                if (line.Trim().Length == 0)
                {
                    if (current.Length > 0)
                    {
                        blocks.Add(new ReportTextBlock(current.ToString(), Preformatted: false));
                        current.Clear();
                    }
                }
                else
                {
                    if (current.Length > 0) current.Append('\n');
                    current.Append(line);
                }
            }
            if (current.Length > 0)
                blocks.Add(new ReportTextBlock(current.ToString(), Preformatted: false));
        }

        private static string CaptionBefore(string text, int tableStart, int index)
        {
            int i = tableStart - 1;
            while (i >= 0 && text[i] is '\n' or ' ' or '\t') i--;
            if (i < 0) return "Table " + (index + 1).ToString(Inv);
            int end = i + 1;
            while (i >= 0 && text[i] != '\n') i--;
            string line = text[(i + 1)..end].Trim();
            return line.Length == 0 ? "Table " + (index + 1).ToString(Inv) : line;
        }

        private static string Normalize(string? text)
            => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        private static string CellAt(IReadOnlyList<string> row, int column)
            => row is not null && column < row.Count ? row[column] ?? "" : "";

        private static ReportLayoutTableRow MakeRow(
            string name, bool header, IReadOnlyList<string> cells, float[] widths, IReportMeasurer measure)
        {
            var role = header ? ReportTextRole.TableHeader : ReportTextRole.Body;
            float lineHeight = measure.LineHeight(role);
            var wrapped = new IReadOnlyList<string>[cells.Count];
            int lines = 1;
            for (int c = 0; c < cells.Count; c++)
            {
                float col = c < widths.Length ? widths[c] : 0;
                float inner = Math.Max(1f, col - 2 * measure.CellPadX);
                var parts = WrapLine(cells[c] ?? "", inner, s => measure.Measure(s, role));
                wrapped[c] = parts;
                if (parts.Count > lines) lines = parts.Count;
            }
            float height = Math.Max(1, lines) * lineHeight + 2 * measure.CellPadY;
            return new ReportLayoutTableRow(
                name, header, cells, widths, height, wrapped, measure.CellPadX, measure.CellPadY, lineHeight);
        }

        private static float[] ColumnWidths(ReportTableBlock table, IReportMeasurer measure)
        {
            int cols = Math.Max(1, table.Headers.Count);
            var widths = new float[cols];
            void Consider(IReadOnlyList<string>? cells, ReportTextRole role)
            {
                if (cells is null) return;
                for (int c = 0; c < cols && c < cells.Count; c++)
                {
                    float w = measure.Measure(cells[c] ?? "", role) + 2 * measure.CellPadX;
                    if (w > widths[c]) widths[c] = w;
                }
            }
            Consider(table.Headers, ReportTextRole.TableHeader);
            foreach (var row in table.Rows) Consider(row, ReportTextRole.Body);
            for (int c = 0; c < cols; c++)
                if (widths[c] < 2 * measure.CellPadX + 1) widths[c] = 2 * measure.CellPadX + 1;
            float sum = 0;
            foreach (float w in widths) sum += w;
            float limit = Math.Max(1f, measure.ContentWidth);
            if (sum > limit)
            {
                float scale = limit / sum;
                for (int c = 0; c < cols; c++) widths[c] *= scale;
            }
            return widths;
        }

        private const string HtmlCss =
            """
            @page { size: A4; margin: 16mm; }
            html { font-family: "Malgun Gothic", "맑은 고딕", "Segoe UI", sans-serif; color: #1e1e1e; }
            body { margin: 24px; }
            h1 { font-size: 20px; margin: 0 0 12px; }
            dl.meta { display: grid; grid-template-columns: 9em 1fr; gap: 2px 12px; margin: 0 0 18px; font-size: 13px; }
            dt { color: #555; }
            dd { margin: 0; }
            table.data { border-collapse: collapse; margin: 8px 0 16px; }
            table.data th, table.data td { border: 1px solid #bbb; padding: 4px 8px; font-size: 13px; vertical-align: top; }
            table.data th { background: #f0f0f0; text-align: left; }
            table.data td.num { text-align: right; font-variant-numeric: tabular-nums; }
            p { margin: 0 0 10px; white-space: pre-wrap; font-size: 13px; line-height: 1.45; }
            pre { font-family: Consolas, "Malgun Gothic", monospace; white-space: pre-wrap; font-size: 12px; }
            @media print {
              body { margin: 0; }
              h1, dl.meta { page-break-after: avoid; }
              tr { page-break-inside: avoid; }
              thead { display: table-header-group; }
            }
            """;

        private static void Meta(StringBuilder sb, string label, string value)
            => sb.Append("<dt>").Append(Html(label)).Append("</dt><dd>").Append(Html(value)).Append("</dd>\n");

        private static string Html(string? value)
            => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private sealed class XSheet
        {
            public required string Name { get; init; }
            public List<List<XCell>> Rows { get; } = new();
        }

        private readonly record struct XCell(int Col, string Text, double Number, bool IsNumber, int Style);

        private static List<XSheet> BuildSheets(ReportDocument document)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ReportSheetName };
            var report = new XSheet { Name = ReportSheetName };
            var sheets = new List<XSheet> { report };
            AddText(report, document.Title, style: 1);
            AddPair(report, document.FileLabel, document.FileName);
            AddPair(report, document.AnalyzedLabel, document.AnalyzedAt);
            AddPair(report, document.VersionLabel, document.AppVersion);
            AddPair(report, document.ScopeLabel, document.ViewScope);
            report.Rows.Add(new List<XCell>());

            foreach (var block in document.Blocks)
            {
                if (block is ReportTextBlock text)
                {
                    foreach (string line in text.Text.Replace("\r\n", "\n").Split('\n'))
                        AddText(report, line, style: 0);
                    report.Rows.Add(new List<XCell>());
                }
                else if (block is ReportTableBlock table)
                {
                    AddRow(report, table.Headers, style: 2);
                    foreach (var row in table.Rows) AddRow(report, Pad(row, table.Headers.Count), style: 0);
                    report.Rows.Add(new List<XCell>());

                    var extra = new XSheet { Name = SanitizeSheetName(table.Name, used) };
                    AddRow(extra, table.Headers, style: 2);
                    foreach (var row in table.Rows) AddRow(extra, Pad(row, table.Headers.Count), style: 0);
                    sheets.Add(extra);
                }
            }
            return sheets;
        }

        private static IReadOnlyList<string> Pad(IReadOnlyList<string> row, int count)
        {
            if (row.Count == count) return row;
            var cells = new string[count];
            for (int i = 0; i < count; i++) cells[i] = i < row.Count ? row[i] ?? "" : "";
            return cells;
        }

        private static void AddText(XSheet sheet, string text, int style)
            => sheet.Rows.Add(new List<XCell> { Cell(0, text, style) });

        private static void AddPair(XSheet sheet, string label, string value)
            => sheet.Rows.Add(new List<XCell> { Cell(0, label, 0), Cell(1, value, 0) });

        private static void AddRow(XSheet sheet, IReadOnlyList<string> cells, int style)
        {
            var row = new List<XCell>(cells.Count);
            for (int c = 0; c < cells.Count; c++) row.Add(Cell(c, cells[c] ?? "", style));
            sheet.Rows.Add(row);
        }

        private static XCell Cell(int col, string text, int style)
            => TryParseNumber(text, out double number)
                ? new XCell(col, text, number, true, style)
                : new XCell(col, text ?? "", 0, false, style);

        private static string SanitizeSheetName(string raw, HashSet<string> used)
        {
            var sb = new StringBuilder();
            foreach (char ch in (raw ?? "").Trim())
            {
                if (ch < 32) continue;
                sb.Append("\\/?*[]:".Contains(ch) ? '_' : ch);
            }
            string name = sb.ToString().Trim().Trim('\'');
            if (name.Length == 0) name = "Table";
            if (name.Length > 31) name = name[..31].Trim().Trim('\'');
            if (name.Length == 0) name = "Table";

            string candidate = name;
            int n = 2;
            while (!used.Add(candidate))
            {
                string suffix = " (" + n.ToString(Inv) + ")";
                int keep = Math.Max(1, 31 - suffix.Length);
                string stem = name.Length > keep ? name[..keep].TrimEnd() : name;
                if (stem.Length == 0) stem = "T";
                candidate = stem + suffix;
                if (candidate.Length > 31) candidate = candidate[..31];
                n++;
                if (n > 999) break;
            }
            return candidate;
        }

        private static void WriteContentTypes(XmlWriter w, int sheetCount)
        {
            w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(w, "xml", "application/xml");
            Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (int i = 1; i <= sheetCount; i++)
                Override(w, "/xl/worksheets/sheet" + i.ToString(Inv) + ".xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
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

        private static void WriteRootRels(XmlWriter w)
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            Rel(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        }

        private static void WriteWorkbook(XmlWriter w, List<XSheet> sheets)
        {
            w.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            w.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            w.WriteStartElement("sheets");
            for (int i = 0; i < sheets.Count; i++)
            {
                w.WriteStartElement("sheet");
                w.WriteAttributeString("name", sheets[i].Name);
                w.WriteAttributeString("sheetId", (i + 1).ToString(Inv));
                w.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId" + (i + 1).ToString(Inv));
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WriteWorkbookRels(XmlWriter w, int sheetCount)
        {
            w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (int i = 1; i <= sheetCount; i++)
                Rel(w, "rId" + i.ToString(Inv),
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                    "worksheets/sheet" + i.ToString(Inv) + ".xml");
            Rel(w, "rId" + (sheetCount + 1).ToString(Inv),
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles",
                "styles.xml");
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

        private static void WriteStyles(XmlWriter w)
        {
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            w.WriteStartElement("styleSheet", ns);
            w.WriteStartElement("fonts");
            w.WriteAttributeString("count", "2");
            Font(w, bold: false);
            Font(w, bold: true);
            w.WriteEndElement();
            w.WriteStartElement("fills");
            w.WriteAttributeString("count", "3");
            Fill(w, null);
            Fill(w, "gray125");
            Fill(w, "solid", "FFD9E2F3");
            w.WriteEndElement();
            w.WriteStartElement("borders");
            w.WriteAttributeString("count", "1");
            w.WriteStartElement("border");
            foreach (string edge in new[] { "left", "right", "top", "bottom", "diagonal" })
            {
                w.WriteStartElement(edge);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("cellStyleXfs");
            w.WriteAttributeString("count", "1");
            Xf(w, font: 0, fill: 0, applyFont: false, applyFill: false);
            w.WriteEndElement();
            w.WriteStartElement("cellXfs");
            w.WriteAttributeString("count", "3");
            Xf(w, font: 0, fill: 0, applyFont: false, applyFill: false);
            Xf(w, font: 1, fill: 0, applyFont: true, applyFill: false);
            Xf(w, font: 1, fill: 2, applyFont: true, applyFill: true);
            w.WriteEndElement();
            w.WriteStartElement("cellStyles");
            w.WriteAttributeString("count", "1");
            w.WriteStartElement("cellStyle");
            w.WriteAttributeString("name", "Normal");
            w.WriteAttributeString("xfId", "0");
            w.WriteAttributeString("builtinId", "0");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void Font(XmlWriter w, bool bold)
        {
            w.WriteStartElement("font");
            if (bold) { w.WriteStartElement("b"); w.WriteEndElement(); }
            w.WriteStartElement("sz");
            w.WriteAttributeString("val", "11");
            w.WriteEndElement();
            w.WriteStartElement("name");
            w.WriteAttributeString("val", "Calibri");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void Fill(XmlWriter w, string? pattern, string? rgb = null)
        {
            w.WriteStartElement("fill");
            w.WriteStartElement("patternFill");
            w.WriteAttributeString("patternType", pattern ?? "none");
            if (rgb is not null)
            {
                w.WriteStartElement("fgColor");
                w.WriteAttributeString("rgb", rgb);
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void Xf(XmlWriter w, int font, int fill, bool applyFont, bool applyFill)
        {
            w.WriteStartElement("xf");
            w.WriteAttributeString("numFmtId", "0");
            w.WriteAttributeString("fontId", font.ToString(Inv));
            w.WriteAttributeString("fillId", fill.ToString(Inv));
            w.WriteAttributeString("borderId", "0");
            if (applyFont) w.WriteAttributeString("applyFont", "1");
            if (applyFill) w.WriteAttributeString("applyFill", "1");
            w.WriteEndElement();
        }

        private static void WriteSheet(XmlWriter w, XSheet sheet)
        {
            w.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            int maxCol = 1;
            int maxRow = Math.Max(1, sheet.Rows.Count);
            foreach (var row in sheet.Rows)
                foreach (var cell in row)
                    if (cell.Col + 1 > maxCol) maxCol = cell.Col + 1;
            w.WriteStartElement("dimension");
            w.WriteAttributeString("ref", "A1:" + ColName(maxCol - 1) + maxRow.ToString(Inv));
            w.WriteEndElement();
            w.WriteStartElement("sheetData");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                w.WriteStartElement("row");
                w.WriteAttributeString("r", (r + 1).ToString(Inv));
                foreach (var cell in sheet.Rows[r])
                {
                    string refer = ColName(cell.Col) + (r + 1).ToString(Inv);
                    w.WriteStartElement("c");
                    w.WriteAttributeString("r", refer);
                    if (cell.Style != 0) w.WriteAttributeString("s", cell.Style.ToString(Inv));
                    if (cell.IsNumber)
                    {
                        w.WriteElementString("v", cell.Number.ToString("G15", Inv));
                    }
                    else
                    {
                        w.WriteAttributeString("t", "inlineStr");
                        w.WriteStartElement("is");
                        w.WriteStartElement("t");
                        if (NeedsPreserve(cell.Text))
                            w.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                        w.WriteString(XmlText(cell.Text));
                        w.WriteEndElement();
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static bool NeedsPreserve(string text)
            => text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n'));

        private static string XmlText(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text)
            {
                if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ')
                    sb.Append(ch);
            }
            return sb.ToString();
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

        private static void WriteEntry(ZipArchive zip, string name, Action<XmlWriter> write)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var settings = new XmlWriterSettings
            {
                Encoding = Utf8,
                OmitXmlDeclaration = false,
                Indent = false,
                NewLineHandling = NewLineHandling.Replace,
            };
            using var writer = XmlWriter.Create(stream, settings);
            write(writer);
        }

        private static void WriteAtomic(string path, Action<Stream> write)
        {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    write(fs);
                File.Move(tmp, full, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
    }
}
