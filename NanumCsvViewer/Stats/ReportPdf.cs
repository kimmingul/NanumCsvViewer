using System.Globalization;

namespace NanumCsvViewer.Stats
{
    /// <summary>
    /// 보고서 PDF에 쓸 TrueType 글꼴 묶음. Regular/Bold/Mono는 글꼴 대체 순서이며 첫 번째가 한글 글꼴이다.
    /// <see cref="Locate"/>는 설치된 글꼴 파일에서 찾는다(Windows 글꼴 폴더, 사용자 글꼴 폴더).
    /// </summary>
    public sealed record ReportPdfFonts(
        IReadOnlyList<TrueTypeFont> Regular,
        IReadOnlyList<TrueTypeFont> Bold,
        IReadOnlyList<TrueTypeFont> Mono)
    {
        private static readonly string[] KoreanRegular =
            { "malgun.ttf", "NanumGothic.ttf", "NanumBarunGothic.ttf", "gulim.ttc", "NGULIM.TTF", "batang.ttc" };

        private static readonly string[] KoreanBold =
            { "malgunbd.ttf", "NanumGothicBold.ttf", "NanumBarunGothicBold.ttf" };

        private static readonly string[] SymbolFallbacks = { "seguisym.ttf", "segoeui.ttf", "arial.ttf" };

        /// <summary>글꼴 폴더 후보: 시스템 글꼴, 사용자 글꼴.</summary>
        public static IReadOnlyList<string> DefaultDirectories()
        {
            var dirs = new List<string>();
            string system = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(system)) dirs.Add(system);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) dirs.Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));
            return dirs;
        }

        public static ReportPdfFonts Locate() => Locate(DefaultDirectories());

        /// <summary>
        /// 한글 글리프('한')가 있는 글꼴을 첫 번째로 둔다. 없으면 <see cref="PdfFontNotFoundException"/>.
        /// 굵은 글꼴이 없으면 보통 글꼴로 대신하고, 고정폭은 D2Coding → Consolas → 한글 글꼴 순이다.
        /// </summary>
        public static ReportPdfFonts Locate(IEnumerable<string> directories)
        {
            ArgumentNullException.ThrowIfNull(directories);
            var dirs = directories.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).ToArray();
            var cache = new Dictionary<string, TrueTypeFont?>(StringComparer.OrdinalIgnoreCase);

            TrueTypeFont? Load(string fileName, bool requireHangul)
            {
                foreach (string dir in dirs)
                {
                    string path = Path.Combine(dir, fileName);
                    if (!File.Exists(path)) continue;
                    if (!cache.TryGetValue(path, out var font))
                    {
                        try { font = TrueTypeFont.Load(path); }
                        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
                        {
                            font = null;
                        }
                        cache[path] = font;
                    }
                    if (font is not null && (!requireHangul || font.TryGetGlyph('한', out _))) return font;
                }
                return null;
            }

            TrueTypeFont? First(IEnumerable<string> names, bool requireHangul)
            {
                foreach (string name in names)
                    if (Load(name, requireHangul) is { } font) return font;
                return null;
            }

            var regular = First(KoreanRegular, requireHangul: true);
            if (regular is null)
            {
                string searched = dirs.Length == 0 ? "(no font folders found)" : string.Join("; ", dirs);
                throw new PdfFontNotFoundException(
                    "No installed TrueType font with Korean glyphs was found (looked for "
                    + string.Join(", ", KoreanRegular) + " in " + searched + ").");
            }
            var bold = First(KoreanBold, requireHangul: true) ?? regular;

            var fallbacks = new List<TrueTypeFont>();
            foreach (string name in SymbolFallbacks)
                if (Load(name, requireHangul: false) is { } f && !ReferenceEquals(f, regular)) fallbacks.Add(f);

            TrueTypeFont? d2 = null;
            foreach (string dir in dirs)
            {
                foreach (string path in SafeEnumerate(dir, "D2Coding*.ttf"))
                {
                    string file = Path.GetFileName(path);
                    if (file.Contains("Bold", StringComparison.OrdinalIgnoreCase)) continue;
                    d2 = Load(file, requireHangul: true);
                    if (d2 is not null) break;
                }
                if (d2 is not null) break;
            }
            var mono = new List<TrueTypeFont>();
            if (d2 is not null) mono.Add(d2);
            else if (Load("consola.ttf", requireHangul: false) is { } consolas) mono.Add(consolas);
            mono.Add(regular);
            mono.AddRange(fallbacks);

            var reg = new List<TrueTypeFont> { regular };
            reg.AddRange(fallbacks);
            var bol = new List<TrueTypeFont> { bold };
            bol.AddRange(fallbacks);
            return new ReportPdfFonts(reg, bol, mono);
        }

        private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
        }
    }

    /// <summary>
    /// 보고서 문서를 A4 PDF로 그린다. 쪽 나눔은 <see cref="ReportExport.Paginate"/>를 그대로 쓰고,
    /// 측정은 내장 글꼴의 실제 진행폭이다(화면과 PDF가 같은 폭).
    /// </summary>
    public static class ReportPdf
    {
        public const float MarginLeft = 40f;
        public const float MarginRight = 40f;
        public const float MarginTop = 40f;
        public const float MarginBottom = 50f;
        public const float TitleSize = 14f;
        public const float MetaSize = 9f;
        public const float BodySize = 10f;
        public const float MonoSize = 8f;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Write(ReportDocument document, Stream output, ReportPdfFonts fonts)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(fonts);

            var pdf = new PdfDocument { Title = document.Title.Length == 0 ? "Report" : document.Title };
            if (DateTime.TryParseExact(document.AnalyzedAt, "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var created))
                pdf.Created = created;
            var regular = pdf.CreateFontStack(fonts.Regular, monospace: false);
            var bold = pdf.CreateFontStack(fonts.Bold, monospace: false);
            var mono = pdf.CreateFontStack(fonts.Mono, monospace: true);
            var measurer = new Measurer(
                PdfDocument.A4Width - MarginLeft - MarginRight,
                PdfDocument.A4Height - MarginTop - MarginBottom,
                regular, bold, mono);

            var pages = ReportExport.Paginate(document, measurer);
            string total = pages.Count.ToString(Inv);
            foreach (var layout in pages)
            {
                var page = pdf.AddPage();
                float y = MarginTop;
                foreach (var item in layout.Items)
                {
                    if (item is ReportLayoutText text)
                    {
                        var (stack, size) = measurer.StyleOf(text.Role);
                        page.DrawText(stack, size, MarginLeft, y, text.Text);
                    }
                    else if (item is ReportLayoutTableRow row)
                    {
                        DrawRow(page, measurer, row, y);
                    }
                    y += item.Height;
                }
                string footer = layout.Number.ToString(Inv) + " / " + total;
                var (footStack, footSize) = measurer.StyleOf(ReportTextRole.Meta);
                page.DrawText(footStack, footSize, MarginLeft, PdfDocument.A4Height - MarginBottom + 14, footer);
            }
            pdf.Save(output);
        }

        private static void DrawRow(PdfPage page, Measurer measurer, ReportLayoutTableRow row, float top)
        {
            var (stack, size) = measurer.StyleOf(row.Header ? ReportTextRole.TableHeader : ReportTextRole.Body);
            float x = MarginLeft;
            float width = 0;
            foreach (float w in row.ColumnWidths) width += w;
            if (row.Header) page.FillRect(x, top, width, row.Height, 0.94f);
            for (int c = 0; c < row.Cells.Count && c < row.ColumnWidths.Count; c++)
            {
                float col = row.ColumnWidths[c];
                page.StrokeRect(x, top, col, row.Height, 0.7f, 0.5f);
                var lines = c < row.WrappedLines.Count ? row.WrappedLines[c] : new[] { row.Cells[c] ?? "" };
                bool numeric = !row.Header && lines.Count <= 1 && ReportExport.TryParseNumber(row.Cells[c], out _);
                float textTop = top + row.PadY;
                foreach (string line in lines)
                {
                    float textX = x + row.PadX;
                    if (numeric)
                    {
                        float inner = Math.Max(0, col - 2 * row.PadX);
                        textX = x + row.PadX + Math.Max(0, inner - stack.Measure(line, size));
                    }
                    page.DrawText(stack, size, textX, textTop, line);
                    textTop += row.LineHeight;
                }
                x += col;
            }
        }

        private sealed class Measurer : IReportMeasurer
        {
            private readonly PdfFontStack _regular, _bold, _mono;

            public Measurer(float width, float height, PdfFontStack regular, PdfFontStack bold, PdfFontStack mono)
            {
                ContentWidth = width;
                ContentHeight = height;
                _regular = regular;
                _bold = bold;
                _mono = mono;
            }

            public float ContentWidth { get; }
            public float ContentHeight { get; }
            public float CellPadX => 4;
            public float CellPadY => 2;
            public float BlockGap => 8;

            public (PdfFontStack Stack, float Size) StyleOf(ReportTextRole role) => role switch
            {
                ReportTextRole.Title => (_bold, TitleSize),
                ReportTextRole.Meta => (_regular, MetaSize),
                ReportTextRole.TableHeader => (_bold, BodySize),
                ReportTextRole.Mono => (_mono, MonoSize),
                _ => (_regular, BodySize),
            };

            public float LineHeight(ReportTextRole role)
            {
                var (stack, size) = StyleOf(role);
                return stack.LineHeight(size);
            }

            public float Measure(string text, ReportTextRole role)
            {
                var (stack, size) = StyleOf(role);
                return stack.Measure(text, size);
            }
        }
    }
}
