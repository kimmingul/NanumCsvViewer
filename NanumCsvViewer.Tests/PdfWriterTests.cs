using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    public class PdfWriterTests
    {
        // 참조: 같은 방식으로 만든 PDF를 pypdf/PyMuPDF(throwaway, 2026-10)로 열어 쪽 수·한글 영문 텍스트·글꼴 부분집합(fontTools로 열림)을 확인했다.
        // 여기서는 합성 TrueType(글리프 0 .notdef, 1 'A', 2 '한', 3 'Á' = 'A' 합성, 4 ' ', 5 '0', 6 'B', 7 '가')으로 구조를 검증한다.
        private static readonly Encoding Latin1 = Encoding.Latin1;

        private static PdfDocument NewDoc(out PdfFontStack regular, out PdfFontStack mono, bool longLoca = false)
        {
            var font = TrueTypeFont.Parse(SyntheticFont.Build(longLoca), "Synthetic");
            var doc = new PdfDocument { Title = "보고서 Title" };
            regular = doc.CreateFontStack(new[] { font }, monospace: false);
            mono = doc.CreateFontStack(new[] { font }, monospace: true);
            return doc;
        }

        private static byte[] Save(PdfDocument doc)
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        [Fact]
        public void Output_has_header_valid_xref_trailer_and_page_tree()
        {
            var doc = NewDoc(out var regular, out _);
            doc.AddPage().DrawText(regular, 10, 40, 40, "A0 B");
            doc.AddPage().DrawText(regular, 10, 40, 40, "한");
            doc.Created = new DateTime(2026, 10, 4, 12, 30, 5);
            var pdf = new PdfInspector(Save(doc));

            Assert.StartsWith("%PDF-1.7\n%", pdf.Text);
            Assert.All(pdf.Bytes[10..14], b => Assert.True(b >= 0x80));
            Assert.EndsWith("startxref\n" + pdf.XrefOffset.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n", pdf.Text);
            Assert.StartsWith("xref\n0 ", pdf.Text[pdf.XrefOffset..]);
            Assert.Equal(pdf.ObjectCount + 1, pdf.Size);
            for (int id = 1; id <= pdf.ObjectCount; id++)
                Assert.Equal(id.ToString(CultureInfo.InvariantCulture) + " 0 obj\n",
                    pdf.Text.Substring(pdf.OffsetOf(id), (id.ToString(CultureInfo.InvariantCulture) + " 0 obj\n").Length));

            Assert.Contains("/Root 1 0 R", pdf.Trailer);
            Assert.Contains("/Type /Catalog", pdf.Body(1));
            Assert.Contains("/Type /Pages /Count 2", pdf.Body(2));
            Assert.Equal(2, pdf.PageIds().Count);
            Assert.All(pdf.PageIds(), id => Assert.Contains("/MediaBox [0 0 595.276 841.89]", pdf.Body(id)));
            Assert.Contains("/CreationDate (D:20261004123005)", pdf.Body(3));
            Assert.Contains("/Title <FEFF" + "BCF4" + "ACE0" + "C11C", pdf.Body(3)); // "보고서"
        }

        [Fact]
        public void Text_round_trips_through_ToUnicode_for_latin_and_korean()
        {
            var doc = NewDoc(out var regular, out _);
            var page = doc.AddPage();
            page.DrawText(regular, 10, 40, 40, "A0 B 한 Á");
            page.DrawText(regular, 12, 40, 70, "BAB");
            var pdf = new PdfInspector(Save(doc));

            var lines = pdf.PageText(pdf.PageIds()[0]);
            Assert.Equal(new[] { "A0 B 한 Á", "BAB" }, lines);

            var font = pdf.FontObjects().Single();
            Assert.Contains("/Encoding /Identity-H", pdf.Body(font));
            Assert.Contains("/ToUnicode ", pdf.Body(font));
            Assert.Contains("/CIDToGIDMap /Identity", pdf.Body(pdf.DescendantOf(font)));
            Assert.Contains("/Subtype /CIDFontType2", pdf.Body(pdf.DescendantOf(font)));
            Assert.Matches(@"/BaseFont /[A-Z]{6}\+SyntheticFont", pdf.Body(font));
        }

        [Fact]
        public void Unmapped_character_uses_notdef_and_is_reported_as_replacement()
        {
            var doc = NewDoc(out var regular, out _);
            doc.AddPage().DrawText(regular, 10, 40, 40, "AZ");
            var pdf = new PdfInspector(Save(doc));
            Assert.Equal(new[] { "A\uFFFD" }, pdf.PageText(pdf.PageIds()[0]));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Subset_keeps_used_glyphs_composite_parts_and_valid_checksums(bool longLoca)
        {
            var doc = NewDoc(out var regular, out _, longLoca);
            doc.AddPage().DrawText(regular, 10, 40, 40, "Á"); // 3 = composite of 1; 2 and 7 are unused
            var pdf = new PdfInspector(Save(doc));
            byte[] ttf = pdf.FontFile(pdf.FontObjects().Single());
            var tables = SubsetTables(ttf);

            Assert.Equal(new[] { "glyf", "head", "hhea", "hmtx", "loca", "maxp" }, tables.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal(0xB1B0AFBAu, TrueTypeFont.Checksum(ttf));
            foreach (var (name, (offset, length, checksum)) in tables)
            {
                uint expected = name == "head"
                    ? TrueTypeFont.Checksum(ttf.AsSpan(offset, length)) - BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(offset + 8))
                    : TrueTypeFont.Checksum(ttf.AsSpan(offset, length));
                Assert.Equal(expected, checksum);
            }
            var head = tables["head"];
            Assert.Equal(1, BinaryPrimitives.ReadInt16BigEndian(ttf.AsSpan(head.Offset + 50)));
            var maxp = tables["maxp"];
            Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(maxp.Offset + 4)));
            var loca = tables["loca"];
            var glyfLengths = Enumerable.Range(0, 4).Select(g =>
                BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(loca.Offset + 4 * g + 4))
                - BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(loca.Offset + 4 * g))).ToArray();
            Assert.True(glyfLengths[0] > 0, ".notdef");
            Assert.True(glyfLengths[1] > 0, "component A");
            Assert.Equal(0u, glyfLengths[2]);
            Assert.True(glyfLengths[3] > 0, "composite");
            Assert.Equal((uint)tables["glyf"].Length, BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(loca.Offset + 16)));
        }

        [Fact]
        public void Widths_array_and_font_flags_use_thousandths_of_em()
        {
            var doc = NewDoc(out var regular, out _);
            doc.AddPage().DrawText(regular, 10, 40, 40, "A한");
            var pdf = new PdfInspector(Save(doc));
            string cid = pdf.Body(pdf.DescendantOf(pdf.FontObjects().Single()));
            Assert.Contains("/W [1 [700] 2 [1000] ]", cid);
            Assert.Contains("/DW 1000", cid);
        }

        [Fact]
        public void Measure_uses_advances_and_monospace_uses_two_cells_for_wide_glyphs()
        {
            NewDoc(out var regular, out var mono);
            Assert.Equal(10 * (0.7f + 1.0f), regular.Measure("A한", 10), 3);
            Assert.Equal(10 * 0.6f * 3, mono.Measure("A한", 10), 3);
            Assert.Equal(10 * 0.6f * 4, mono.Measure("A B ", 10) , 3);
            Assert.Equal(0f, regular.Measure("", 10));
            Assert.Equal(10 * 1.0f, regular.LineHeight(10), 3); // ascent 800 + descent 200, no gap
            Assert.Equal(40 + 8f, regular.Baseline(40, 10), 3);
        }

        [Fact]
        public void Monospace_text_is_positioned_on_a_cell_grid_and_still_extracts()
        {
            var doc = NewDoc(out _, out var mono);
            doc.AddPage().DrawText(mono, 10, 40, 40, "A한0");
            var pdf = new PdfInspector(Save(doc));
            Assert.Equal(new[] { "A한0" }, pdf.PageText(pdf.PageIds()[0]));
            string content = pdf.Content(pdf.PageContentId(pdf.PageIds()[0]));
            // 칸 0.6em: 'A'(0.7em)는 0.1em 넘치지만 한글(1.0em)이 2칸(1.2em) 안에서 0.1em 들여써 시작해 이동이 상쇄되고,
            // 한글이 끝난 자리(1.7em)에서 다음 칸 시작(1.8em)까지 0.1em만 오른쪽으로 민다.
            Assert.Contains("[<0001> <0002> -100 <0005> ] TJ", content);
        }

        [Fact]
        public void Same_input_gives_identical_bytes()
        {
            byte[] Make()
            {
                var doc = NewDoc(out var regular, out _);
                doc.AddPage().DrawText(regular, 10, 40, 40, "A 한 B");
                return Save(doc);
            }
            Assert.Equal(Make(), Make());
        }

        [Fact]
        public void Save_without_pages_is_an_error()
        {
            var doc = NewDoc(out _, out _);
            Assert.Throws<InvalidOperationException>(() => Save(doc));
        }

        [Fact]
        public void Fonts_without_embedding_permission_or_cff_outlines_are_rejected()
        {
            Assert.Throws<NotSupportedException>(() => TrueTypeFont.Parse(SyntheticFont.Build(false, fsType: 2), "Restricted"));
            Assert.Throws<NotSupportedException>(() => TrueTypeFont.Parse(SyntheticFont.Build(false, fsType: 0x0100), "NoSubset"));
            Assert.Throws<NotSupportedException>(() => TrueTypeFont.Parse(Encoding.ASCII.GetBytes("OTTO" + new string('\0', 40)), "Cff"));
            Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(new byte[40], "Junk"));
            Assert.Throws<InvalidDataException>(() => TrueTypeFont.Parse(SyntheticFont.Build(false)[..60], "Truncated"));
            Assert.NotNull(TrueTypeFont.Parse(SyntheticFont.Build(false, fsType: 8), "Editable"));
        }

        [Fact]
        public void Locate_reports_missing_font_clearly_and_finds_a_korean_font_in_the_directory()
        {
            string empty = Path.Combine(Path.GetTempPath(), "nanum-pdf-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            try
            {
                var ex = Assert.Throws<PdfFontNotFoundException>(() => ReportPdfFonts.Locate(new[] { empty }));
                Assert.Contains("Korean", ex.Message);
                Assert.Contains(empty, ex.Message);
                Assert.Throws<PdfFontNotFoundException>(() => ReportPdfFonts.Locate(Array.Empty<string>()));

                File.WriteAllBytes(Path.Combine(empty, "malgun.ttf"), SyntheticFont.Build(false));
                var fonts = ReportPdfFonts.Locate(new[] { empty });
                Assert.Same(fonts.Regular[0], fonts.Bold[0]);
                Assert.Contains(fonts.Regular[0], fonts.Mono);
            }
            finally { Directory.Delete(empty, true); }
        }

        [Fact]
        public void Report_pdf_paginates_repeats_headers_and_round_trips_table_text()
        {
            var rows = Enumerable.Range(1, 120)
                .Select(i => (IReadOnlyList<string>)new[] { "A" + (i % 3 == 0 ? "B" : ""), (i * 7 % 100).ToString(CultureInfo.InvariantCulture) })
                .ToArray();
            var document = new ReportDocument("보고서 A", "file.csv", "2026-10-04 12:30:05", "1.0", "BAB 0",
                "File", "Analyzed", "App version", "View scope",
                new ReportBlock[]
                {
                    new ReportTextBlock("A 0 B", Preformatted: false),
                    new ReportTableBlock("Table", new[] { "A", "B" }, rows),
                }, Preformatted: false);
            var font = TrueTypeFont.Parse(SyntheticFont.Build(false), "Synthetic");
            var fonts = new ReportPdfFonts(new[] { font }, new[] { font }, new[] { font });

            using var ms = new MemoryStream();
            ReportExport.WritePdf(document, ms, fonts);
            var pdf = new PdfInspector(ms.ToArray());

            var pageIds = pdf.PageIds();
            Assert.True(pageIds.Count >= 2);
            Assert.Contains("/Count " + pageIds.Count.ToString(CultureInfo.InvariantCulture), pdf.Body(2));
            for (int p = 0; p < pageIds.Count; p++)
            {
                var lines = pdf.PageText(pageIds[p]);
                Assert.Equal((p + 1) + " / " + pageIds.Count, lines[^1]);
                if (p > 0) Assert.Equal("A", lines[0]); // 표 머리글 반복
            }
            Assert.Equal(new[] { "A", "B" }, pdf.PageText(pageIds[0]).Where(l => l is "A" or "B").Take(2));
            // 숫자 셀은 오른쪽 맞춤이라 같은 줄 폭 안에서도 문자열은 그대로 보존된다.
            var all = pageIds.SelectMany(id => pdf.PageText(id)).ToArray();
            Assert.Equal(40, all.Count(l => l == "AB"));
        }

        [Fact]
        public void Preformatted_body_uses_the_monospace_role()
        {
            var document = new ReportDocument("T", "f", "2026-10-04 00:00:00", "1", "s", "File", "Analyzed", "V", "S",
                new ReportBlock[] { new ReportTextBlock("a  b\nc", Preformatted: true) }, Preformatted: true);
            var pages = ReportExport.Paginate(document, new FixedMeasurer());
            var body = pages.SelectMany(p => p.Items).OfType<ReportLayoutText>()
                .Where(t => t.Role is ReportTextRole.Body or ReportTextRole.Mono).ToArray();
            Assert.Equal(new[] { "a  b", "c" }, body.Select(t => t.Text));
            Assert.All(body, t => Assert.Equal(ReportTextRole.Mono, t.Role));
        }

        [Fact]
        public void Real_korean_system_font_produces_a_small_selectable_pdf()
        {
            ReportPdfFonts fonts;
            try { fonts = ReportPdfFonts.Locate(); }
            catch (PdfFontNotFoundException) { return; } // 한글 글꼴이 없는 기계(이 경우 앱은 HTML을 권한다)

            var table = new TextTable("Term", "Estimate", "p");
            table.AddRow("intercept", "1.5", "0.0123");
            table.AddRow("그룹", "—", "0.2500");
            string rendered = table.Render();
            string text = "분석 범위: 현재 뷰, χ² ≤ β\n\nCoefficients\n" + rendered + "\n끝.\n";
            var captured = new CapturedTable(rendered, new[] { "Term", "Estimate", "p" },
                new IReadOnlyList<string>[] { new[] { "intercept", "1.5", "0.0123" }, new[] { "그룹", "—", "0.2500" } });
            var report = new AdvancedReport("GLM 보고서", text, new[] { captured }, null, new DateTime(2026, 10, 4, 12, 30, 5));
            var document = ReportExport.Compose(report, new ReportContext("데이터.csv", "1.0", "현재 뷰 · 10행"));

            using var ms = new MemoryStream();
            ReportExport.WritePdf(document, ms, fonts);
            Assert.True(ms.Length < 400_000, "font must be subset, was " + ms.Length);
            var pdf = new PdfInspector(ms.ToArray());
            var lines = pdf.PageText(Assert.Single(pdf.PageIds()));
            Assert.Contains("GLM 보고서", lines);
            Assert.Contains("분석 범위: 현재 뷰, χ² ≤ β", lines);
            Assert.Contains("그룹", lines);
            Assert.Contains("File: 데이터.csv", lines);
            Assert.Equal("1 / 1", lines[^1]);
        }

        private sealed class FixedMeasurer : IReportMeasurer
        {
            public float ContentWidth => 1000;
            public float ContentHeight => 1000;
            public float CellPadX => 1;
            public float CellPadY => 1;
            public float BlockGap => 1;
            public float LineHeight(ReportTextRole role) => 1;
            public float Measure(string text, ReportTextRole role) => text.Length;
        }

        private static Dictionary<string, (int Offset, int Length, uint Checksum)> SubsetTables(byte[] ttf)
        {
            Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(ttf));
            int n = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(4));
            var tables = new Dictionary<string, (int, int, uint)>();
            for (int i = 0; i < n; i++)
            {
                int rec = 12 + 16 * i;
                tables[Encoding.ASCII.GetString(ttf, rec, 4)] = (
                    (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(rec + 8)),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(rec + 12)),
                    BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(rec + 4)));
            }
            return tables;
        }

        /// <summary>자체 출력 PDF를 xref로 읽는 최소 해석기(바이트 = Latin1 문자).</summary>
        private sealed class PdfInspector
        {
            private readonly long[] _offsets;

            public PdfInspector(byte[] bytes)
            {
                Bytes = bytes;
                Text = Latin1.GetString(bytes);
                var m = Regex.Match(Text, @"startxref\n(\d+)\n%%EOF\n$");
                Assert.True(m.Success, "startxref");
                XrefOffset = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var head = Regex.Match(Text[XrefOffset..], @"^xref\n0 (\d+)\n");
                Assert.True(head.Success, "xref header");
                Size = int.Parse(head.Groups[1].Value, CultureInfo.InvariantCulture);
                int pos = XrefOffset + head.Length;
                Assert.Equal("0000000000 65535 f \n", Text.Substring(pos, 20));
                _offsets = new long[Size];
                for (int i = 1; i < Size; i++)
                {
                    string entry = Text.Substring(pos + 20 * i, 20);
                    Assert.Matches(@"^\d{10} 00000 n \n$", entry);
                    _offsets[i] = long.Parse(entry[..10], CultureInfo.InvariantCulture);
                }
                int trailerAt = pos + 20 * Size;
                Assert.StartsWith("trailer\n", Text[trailerAt..]);
                Trailer = Text[trailerAt..Text.IndexOf("startxref", trailerAt, StringComparison.Ordinal)];
                Assert.Contains("/Size " + Size.ToString(CultureInfo.InvariantCulture), Trailer);
                Assert.Matches(@"/ID \[<[0-9A-F]{32}> <[0-9A-F]{32}>\]", Trailer);
            }

            public byte[] Bytes { get; }
            public string Text { get; }
            public int XrefOffset { get; }
            public int Size { get; }
            public int ObjectCount => Size - 1;
            public string Trailer { get; }
            public int OffsetOf(int id) => (int)_offsets[id];

            public string Body(int id)
            {
                string prefix = id.ToString(CultureInfo.InvariantCulture) + " 0 obj\n";
                int start = OffsetOf(id) + prefix.Length;
                int end = Text.IndexOf("\nendobj\n", start, StringComparison.Ordinal);
                Assert.True(end > start, "endobj of " + id);
                return Text[start..end];
            }

            public byte[] StreamData(int id)
            {
                string body = Body(id);
                var length = int.Parse(Regex.Match(body, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
                int dataStart = body.IndexOf("stream\n", StringComparison.Ordinal) + "stream\n".Length;
                Assert.Equal("\nendstream", body[(dataStart + length)..]);
                Assert.Contains("/Filter /FlateDecode", body);
                using var z = new ZLibStream(new MemoryStream(Latin1.GetBytes(body.Substring(dataStart, length))), CompressionMode.Decompress);
                using var output = new MemoryStream();
                z.CopyTo(output);
                return output.ToArray();
            }

            public string Content(int id) => Latin1.GetString(StreamData(id));

            public List<int> PageIds()
            {
                var ids = new List<int>();
                for (int id = 1; id <= ObjectCount; id++)
                    if (Body(id).StartsWith("<< /Type /Page /Parent", StringComparison.Ordinal)) ids.Add(id);
                var kids = Regex.Match(Body(2), @"/Kids \[([^\]]*)\]").Groups[1].Value;
                Assert.Equal(ids, Regex.Matches(kids, @"(\d+) 0 R").Select(x => int.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture)));
                return ids;
            }

            public int PageContentId(int pageId)
                => int.Parse(Regex.Match(Body(pageId), @"/Contents (\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);

            public List<int> FontObjects()
            {
                var ids = new List<int>();
                for (int id = 1; id <= ObjectCount; id++)
                    if (Body(id).Contains("/Subtype /Type0", StringComparison.Ordinal)) ids.Add(id);
                return ids;
            }

            public int DescendantOf(int type0)
                => int.Parse(Regex.Match(Body(type0), @"/DescendantFonts \[(\d+) 0 R\]").Groups[1].Value, CultureInfo.InvariantCulture);

            public byte[] FontFile(int type0)
            {
                string descriptor = Body(DescendantOf(type0));
                int descriptorId = int.Parse(Regex.Match(descriptor, @"/FontDescriptor (\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);
                int file = int.Parse(Regex.Match(Body(descriptorId), @"/FontFile2 (\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);
                var data = StreamData(file);
                Assert.Contains("/Length1 " + data.Length.ToString(CultureInfo.InvariantCulture), Body(file));
                return data;
            }

            private Dictionary<ushort, string> ToUnicode(int type0)
            {
                int id = int.Parse(Regex.Match(Body(type0), @"/ToUnicode (\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);
                string cmap = Content(id);
                Assert.Contains("begincodespacerange\n<0000> <FFFF>", cmap);
                var map = new Dictionary<ushort, string>();
                foreach (Match m in Regex.Matches(cmap, @"<([0-9A-F]{4})> <([0-9A-F]+)>\n"))
                {
                    var units = new StringBuilder();
                    for (int i = 0; i < m.Groups[2].Length; i += 4)
                        units.Append((char)int.Parse(m.Groups[2].Value.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    map[ushort.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = units.ToString();
                }
                return map;
            }

            /// <summary>쪽의 BT…ET 블록마다 ToUnicode로 되읽은 문자열.</summary>
            public List<string> PageText(int pageId)
            {
                string resources = Body(pageId);
                var fontMap = new Dictionary<string, Dictionary<ushort, string>>();
                foreach (Match f in Regex.Matches(resources, @"/(F\d+) (\d+) 0 R"))
                    fontMap[f.Groups[1].Value] = ToUnicode(int.Parse(f.Groups[2].Value, CultureInfo.InvariantCulture));
                var lines = new List<string>();
                foreach (Match block in Regex.Matches(Content(PageContentId(pageId)), @"BT\n(.*?)ET\n", RegexOptions.Singleline))
                {
                    var sb = new StringBuilder();
                    Dictionary<ushort, string>? current = null;
                    foreach (Match token in Regex.Matches(block.Groups[1].Value, @"/(F\d+) [\d.]+ Tf|<([0-9A-F]{4,})>"))
                    {
                        if (token.Groups[1].Success) { current = fontMap[token.Groups[1].Value]; continue; }
                        string hex = token.Groups[2].Value;
                        for (int i = 0; i < hex.Length; i += 4)
                            sb.Append(current![ushort.Parse(hex.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)]);
                    }
                    lines.Add(sb.ToString());
                }
                return lines;
            }
        }

        /// <summary>시험용 최소 TrueType(unitsPerEm 1000, ascent 800/descent 200).</summary>
        private static class SyntheticFont
        {
            private static readonly (int Cp, int Gid)[] Map =
            {
                (0x20, 4), (0x30, 5), (0x41, 1), (0x42, 6), (0xC1, 3), (0xAC00, 7), (0xD55C, 2),
                (0x31, 8), (0x32, 9), (0x33, 10), (0x34, 11), (0x35, 12), (0x36, 13), (0x37, 14), (0x38, 15), (0x39, 16), (0x2F, 17),
            };
            private static readonly int[] Advances = { 600, 700, 1000, 700, 300, 600, 700 };
            private const int GlyphCount = 18;

            public static byte[] Build(bool longLoca, int fsType = 0)
            {
                var glyphs = new byte[GlyphCount][];
                glyphs[0] = Simple(50, 0, 550, 700);
                glyphs[1] = Simple(50, 0, 650, 700);
                glyphs[2] = Simple(50, -100, 950, 800);
                glyphs[3] = Composite(1, 0, 100);
                glyphs[4] = Array.Empty<byte>();
                glyphs[5] = Simple(50, 0, 550, 700);
                glyphs[6] = Simple(50, 0, 650, 700);
                glyphs[7] = Simple(50, -100, 950, 800);
                for (int g = 8; g < GlyphCount; g++) glyphs[g] = Simple(50, 0, 550, 700);

                var glyf = new MemoryStream();
                var loca = new List<uint>();
                foreach (var g in glyphs)
                {
                    loca.Add((uint)glyf.Length);
                    glyf.Write(g);
                    while (glyf.Length % 4 != 0) glyf.WriteByte(0);
                }
                loca.Add((uint)glyf.Length);
                var locaBytes = new byte[loca.Count * (longLoca ? 4 : 2)];
                for (int i = 0; i < loca.Count; i++)
                {
                    if (longLoca) BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(4 * i), loca[i]);
                    else BinaryPrimitives.WriteUInt16BigEndian(locaBytes.AsSpan(2 * i), (ushort)(loca[i] / 2));
                }

                var head = new byte[54];
                BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
                BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
                BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
                BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(36), 50);
                BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), -100);
                BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), 950);
                BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), 800);
                BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), (short)(longLoca ? 1 : 0));
                var hhea = new byte[36];
                BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
                BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
                BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
                BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), 7); // 마지막 두 글리프는 lsb만
                var maxp = new byte[32];
                BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00010000);
                BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), GlyphCount);
                var hmtx = new byte[7 * 4 + 2 * (GlyphCount - 7)];
                for (int g = 0; g < 7; g++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4 * g), (ushort)Advances[g]);
                    BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(4 * g + 2), 50);
                }
                // 글리프 7 이상은 numberOfHMetrics 밖이라 lsb만 있고 마지막 진행폭(글리프 6 = 700)을 쓴다.
                for (int g = 7; g < GlyphCount; g++) BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(28 + 2 * (g - 7)), 50);
                var os2 = new byte[78];
                BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), 400);
                BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8), (ushort)fsType);

                var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    ["OS/2"] = os2, ["cmap"] = Cmap(), ["glyf"] = glyf.ToArray(), ["head"] = head, ["hhea"] = hhea,
                    ["hmtx"] = hmtx, ["loca"] = locaBytes, ["maxp"] = maxp, ["name"] = Name("SyntheticFont"),
                };
                int pos = 12 + 16 * tables.Count;
                var file = new MemoryStream();
                var dir = new byte[pos];
                BinaryPrimitives.WriteUInt32BigEndian(dir, 0x00010000);
                BinaryPrimitives.WriteUInt16BigEndian(dir.AsSpan(4), (ushort)tables.Count);
                int rec = 12;
                foreach (var (tag, body) in tables)
                {
                    Encoding.ASCII.GetBytes(tag, dir.AsSpan(rec, 4));
                    BinaryPrimitives.WriteUInt32BigEndian(dir.AsSpan(rec + 8), (uint)(pos + file.Length));
                    BinaryPrimitives.WriteUInt32BigEndian(dir.AsSpan(rec + 12), (uint)body.Length);
                    file.Write(body);
                    while (file.Length % 4 != 0) file.WriteByte(0);
                    rec += 16;
                }
                return dir.Concat(file.ToArray()).ToArray();
            }

            private static byte[] Simple(int x0, int y0, int x1, int y1)
            {
                var w = new MemoryStream();
                void I16(int v) { w.WriteByte((byte)(v >> 8)); w.WriteByte((byte)v); }
                I16(1); I16(x0); I16(y0); I16(x1); I16(y1);
                I16(2); I16(0);
                w.WriteByte(1); w.WriteByte(1); w.WriteByte(1);
                I16(x0); I16(x1 - x0); I16(x0 - x1);
                I16(y0); I16(0); I16(y1 - y0);
                return w.ToArray();
            }

            private static byte[] Composite(int component, int dx, int dy)
            {
                var w = new MemoryStream();
                void I16(int v) { w.WriteByte((byte)(v >> 8)); w.WriteByte((byte)v); }
                I16(-1); I16(50); I16(0); I16(650); I16(800);
                I16(0x0003); I16(component); I16(dx); I16(dy);
                return w.ToArray();
            }

            private static byte[] Cmap()
            {
                var maps = Map.OrderBy(m => m.Cp).ToList();
                int segs = maps.Count + 1;
                var sub = new byte[16 + 8 * segs];
                BinaryPrimitives.WriteUInt16BigEndian(sub, 4);
                BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(2), (ushort)sub.Length);
                BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(6), (ushort)(segs * 2));
                for (int i = 0; i < segs; i++)
                {
                    int cp = i < maps.Count ? maps[i].Cp : 0xFFFF;
                    int gid = i < maps.Count ? maps[i].Gid : 0;
                    BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(14 + 2 * i), (ushort)cp);
                    BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(16 + 2 * segs + 2 * i), (ushort)cp);
                    BinaryPrimitives.WriteUInt16BigEndian(sub.AsSpan(16 + 4 * segs + 2 * i), (ushort)(i < maps.Count ? gid - cp : 1));
                }
                var table = new byte[12 + sub.Length];
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 3);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 1);
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 12);
                sub.CopyTo(table, 12);
                return table;
            }

            private static byte[] Name(string postScript)
            {
                byte[] text = Encoding.BigEndianUnicode.GetBytes(postScript);
                var table = new byte[6 + 12 + text.Length];
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 18);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 3);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(8), 1);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(10), 0x409);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12), 6);
                BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(14), (ushort)text.Length);
                text.CopyTo(table, 18);
                return table;
            }
        }
    }
}
