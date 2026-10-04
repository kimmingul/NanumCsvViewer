using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NanumCsvViewer.Stats
{
    /// <summary>PDF에 넣을 한글 TrueType 글꼴을 찾지 못했다.</summary>
    public sealed class PdfFontNotFoundException : InvalidOperationException
    {
        public PdfFontNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// TrueType(glyf) 글꼴 읽기 + 사용한 글리프만 남기는 부분집합 만들기. 외부 라이브러리 없음.
    /// 부분집합은 글리프 번호(GID)를 바꾸지 않는다(CIDToGIDMap=Identity). 쓰지 않은 글리프는 빈 윤곽이다.
    /// ttc는 첫 글꼴을 쓴다. CFF(OTTO) 윤곽·내장 금지(fsType 제한/부분집합 금지) 글꼴은 거부한다.
    /// </summary>
    public sealed class TrueTypeFont
    {
        private readonly byte[] _data;
        private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
        private readonly Dictionary<int, ushort> _cmap = new();
        private readonly int _numHMetrics;
        private readonly bool _longLoca;

        public string Name { get; }
        public int UnitsPerEm { get; }
        public int Ascent { get; }
        /// <summary>양수(글꼴 단위, 기준선 아래).</summary>
        public int Descent { get; }
        public int LineGap { get; }
        public int NumGlyphs { get; }
        public int XMin { get; }
        public int YMin { get; }
        public int XMax { get; }
        public int YMax { get; }
        public int CapHeight { get; }
        public int StemV { get; }
        public double ItalicAngle { get; }

        public static TrueTypeFont Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            try { return Parse(data, Path.GetFileNameWithoutExtension(path)); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException("Not a valid TrueType font: " + path, ex);
            }
        }

        public static TrueTypeFont Parse(byte[] data, string fallbackName)
        {
            ArgumentNullException.ThrowIfNull(data);
            try { return new TrueTypeFont(data, fallbackName); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
                throw new InvalidDataException("Not a valid TrueType font: " + fallbackName, ex);
            }
        }

        private TrueTypeFont(byte[] data, string fallbackName)
        {
            _data = data;
            if (data.Length < 12) throw new InvalidDataException("Font file is too short.");
            int baseOffset = 0;
            uint tag = U32(0);
            if (tag == 0x74746366) // 'ttcf'
            {
                if (U32(8) == 0) throw new InvalidDataException("Empty font collection.");
                baseOffset = checked((int)U32(12));
                tag = U32(baseOffset);
            }
            if (tag == 0x4F54544F) // 'OTTO'
                throw new NotSupportedException("OpenType fonts with CFF outlines are not supported: " + fallbackName);
            if (tag != 0x00010000 && tag != 0x74727565) // 0x00010000, 'true'
                throw new InvalidDataException("Unknown font format: " + fallbackName);

            int numTables = U16(baseOffset + 4);
            for (int i = 0; i < numTables; i++)
            {
                int rec = baseOffset + 12 + 16 * i;
                string name = Encoding.ASCII.GetString(data, rec, 4);
                int off = checked((int)U32(rec + 8));
                int len = checked((int)U32(rec + 12));
                if (off < 0 || len < 0 || (long)off + len > data.Length)
                    throw new InvalidDataException("Font table '" + name + "' is outside the file.");
                _tables[name] = (off, len);
            }
            foreach (string required in new[] { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cmap" })
                if (!_tables.ContainsKey(required))
                    throw new NotSupportedException("Font has no '" + required + "' table: " + fallbackName);

            // OS/2 fsType: 0x0002 제한 라이선스 내장 금지, 0x0100 부분집합 금지.
            if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 10)
            {
                int fsType = U16(os2.Offset + 8);
                if ((fsType & 0x000F) == 0x0002 || (fsType & 0x0100) != 0)
                    throw new NotSupportedException("The font license does not allow embedding: " + fallbackName);
            }

            var head = _tables["head"];
            UnitsPerEm = U16(head.Offset + 18);
            if (UnitsPerEm == 0) throw new InvalidDataException("unitsPerEm is zero.");
            XMin = I16(head.Offset + 36);
            YMin = I16(head.Offset + 38);
            XMax = I16(head.Offset + 40);
            YMax = I16(head.Offset + 42);
            _longLoca = I16(head.Offset + 50) != 0;

            var hhea = _tables["hhea"];
            Ascent = I16(hhea.Offset + 4);
            Descent = -I16(hhea.Offset + 6);
            LineGap = I16(hhea.Offset + 8);
            _numHMetrics = U16(hhea.Offset + 34);
            NumGlyphs = U16(_tables["maxp"].Offset + 4);
            if (_numHMetrics == 0 || NumGlyphs == 0) throw new InvalidDataException("Font has no glyphs.");
            if (_tables["hmtx"].Length < 4 * _numHMetrics) throw new InvalidDataException("hmtx is truncated.");
            var loca = _tables["loca"];
            if (loca.Length < (NumGlyphs + 1) * (_longLoca ? 4 : 2)) throw new InvalidDataException("loca is truncated.");

            CapHeight = (int)(Ascent * 0.7);
            int weight = 400;
            if (os2.Length >= 6) weight = U16(os2.Offset + 4);
            if (os2.Length >= 90 && U16(os2.Offset) >= 2)
            {
                int cap = I16(os2.Offset + 88);
                if (cap > 0) CapHeight = cap;
            }
            StemV = 50 + (int)(Math.Pow(weight / 65.0, 2));
            if (_tables.TryGetValue("post", out var post) && post.Length >= 8)
                ItalicAngle = I32(post.Offset + 4) / 65536.0;

            Name = ReadPostScriptName() is { Length: > 0 } ps ? ps : Sanitize(fallbackName);
            ReadCmap();
            if (_cmap.Count == 0) throw new NotSupportedException("Font has no usable Unicode cmap: " + fallbackName);
        }

        public bool TryGetGlyph(int codePoint, out ushort gid)
            => _cmap.TryGetValue(codePoint, out gid) && gid < NumGlyphs;

        /// <summary>글리프 진행폭(글꼴 단위).</summary>
        public int Advance(ushort gid)
        {
            var (off, _) = _tables["hmtx"];
            int index = Math.Min((int)gid, _numHMetrics - 1);
            return U16(off + 4 * index);
        }

        private int LeftSideBearing(ushort gid)
        {
            var (off, len) = _tables["hmtx"];
            if (gid < _numHMetrics) return I16(off + 4 * gid + 2);
            int p = off + 4 * _numHMetrics + 2 * (gid - _numHMetrics);
            return p + 2 <= off + len ? I16(p) : 0;
        }

        /// <summary>
        /// 주어진 글리프(와 합성 글리프의 구성 요소, .notdef)만 윤곽을 남긴 TrueType 파일을 만든다.
        /// 글리프 번호는 그대로이고 번호 수만 가장 큰 사용 번호 + 1로 줄어든다.
        /// </summary>
        public byte[] Subset(IEnumerable<ushort> glyphs)
        {
            ArgumentNullException.ThrowIfNull(glyphs);
            var keep = new SortedSet<ushort> { 0 };
            var pending = new Stack<ushort>();
            pending.Push(0);
            foreach (ushort g in glyphs)
                if (g < NumGlyphs && keep.Add(g)) pending.Push(g);
            while (pending.Count > 0)
            {
                var (start, end) = GlyphRange(pending.Pop());
                if (end - start < 10) continue;
                int glyf = _tables["glyf"].Offset;
                if (I16(glyf + start) >= 0) continue;
                int p = glyf + start + 10;
                while (true)
                {
                    int flags = U16(p);
                    ushort component = (ushort)U16(p + 2);
                    if (component < NumGlyphs && keep.Add(component)) pending.Push(component);
                    p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
                    if ((flags & 0x0008) != 0) p += 2;
                    else if ((flags & 0x0040) != 0) p += 4;
                    else if ((flags & 0x0080) != 0) p += 8;
                    if ((flags & 0x0020) == 0) break;
                }
            }

            int count = keep.Max + 1;
            var glyfOut = new MemoryStream();
            var locaOut = new byte[(count + 1) * 4];
            var hmtxOut = new byte[count * 4];
            for (int g = 0; g < count; g++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(g * 4), (uint)glyfOut.Length);
                if (keep.Contains((ushort)g))
                {
                    var (start, end) = GlyphRange((ushort)g);
                    glyfOut.Write(_data, _tables["glyf"].Offset + start, end - start);
                    while (glyfOut.Length % 4 != 0) glyfOut.WriteByte(0);
                }
                BinaryPrimitives.WriteUInt16BigEndian(hmtxOut.AsSpan(g * 4), (ushort)Advance((ushort)g));
                BinaryPrimitives.WriteInt16BigEndian(hmtxOut.AsSpan(g * 4 + 2), (short)LeftSideBearing((ushort)g));
            }
            BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(count * 4), (uint)glyfOut.Length);

            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            byte[] head = Slice("head");
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);
            BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
            tables["head"] = head;
            byte[] hhea = Slice("hhea");
            BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)count);
            tables["hhea"] = hhea;
            byte[] maxp = Slice("maxp");
            BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)count);
            tables["maxp"] = maxp;
            tables["hmtx"] = hmtxOut;
            tables["loca"] = locaOut;
            tables["glyf"] = glyfOut.ToArray();
            foreach (string optional in new[] { "cvt ", "fpgm", "prep" })
                if (_tables.ContainsKey(optional)) tables[optional] = Slice(optional);
            return Assemble(tables);
        }

        private byte[] Slice(string table)
        {
            var (off, len) = _tables[table];
            return _data.AsSpan(off, len).ToArray();
        }

        private (int Start, int End) GlyphRange(ushort gid)
        {
            int loca = _tables["loca"].Offset;
            int glyfLen = _tables["glyf"].Length;
            long start, end;
            if (_longLoca)
            {
                start = U32(loca + 4 * gid);
                end = U32(loca + 4 * gid + 4);
            }
            else
            {
                start = 2L * U16(loca + 2 * gid);
                end = 2L * U16(loca + 2 * gid + 2);
            }
            if (end < start || end > glyfLen) throw new InvalidDataException("Glyph " + gid + " is outside glyf.");
            return ((int)start, (int)end);
        }

        private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
        {
            int n = tables.Count;
            int headerLen = 12 + 16 * n;
            int total = headerLen;
            foreach (var t in tables.Values) total += (t.Length + 3) & ~3;
            var file = new byte[total];
            int entrySelector = 0;
            while ((1 << (entrySelector + 1)) <= n) entrySelector++;
            int searchRange = (1 << entrySelector) * 16;
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0), 0x00010000);
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)n);
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)searchRange);
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)entrySelector);
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)(n * 16 - searchRange));
            int rec = 12, pos = headerLen, headPos = -1;
            foreach (var (name, body) in tables)
            {
                Encoding.ASCII.GetBytes(name, file.AsSpan(rec, 4));
                BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(rec + 4), Checksum(body));
                BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(rec + 8), (uint)pos);
                BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(rec + 12), (uint)body.Length);
                body.CopyTo(file.AsSpan(pos));
                if (name == "head") headPos = pos;
                pos += (body.Length + 3) & ~3;
                rec += 16;
            }
            if (headPos >= 0)
                BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headPos + 8), unchecked(0xB1B0AFBAu - Checksum(file)));
            return file;
        }

        /// <summary>TrueType 표 체크섬(4바이트 빅엔디언 합, 끝은 0 채움).</summary>
        public static uint Checksum(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            int i = 0;
            for (; i + 4 <= data.Length; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
            if (i < data.Length)
            {
                uint last = 0;
                for (int shift = 24; i < data.Length; i++, shift -= 8) last |= (uint)data[i] << shift;
                sum = unchecked(sum + last);
            }
            return sum;
        }

        private string? ReadPostScriptName()
        {
            if (!_tables.TryGetValue("name", out var name) || name.Length < 6) return null;
            int count = U16(name.Offset + 2);
            int strings = name.Offset + U16(name.Offset + 4);
            string? mac = null;
            for (int i = 0; i < count; i++)
            {
                int rec = name.Offset + 6 + 12 * i;
                if (rec + 12 > name.Offset + name.Length) break;
                if (U16(rec + 6) != 6) continue;
                int platform = U16(rec), len = U16(rec + 8), off = strings + U16(rec + 10);
                if (off < 0 || off + len > _data.Length) continue;
                if (platform == 3 || platform == 0) return Sanitize(Encoding.BigEndianUnicode.GetString(_data, off, len));
                if (platform == 1) mac ??= Sanitize(Encoding.ASCII.GetString(_data, off, len));
            }
            return mac;
        }

        private static string Sanitize(string raw)
        {
            var sb = new StringBuilder();
            foreach (char ch in raw)
                if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-') sb.Append(ch);
            return sb.Length == 0 ? "Font" : sb.ToString();
        }

        private void ReadCmap()
        {
            var (cmap, len) = _tables["cmap"];
            int count = U16(cmap + 2);
            int best = -1, bestRank = -1;
            for (int i = 0; i < count; i++)
            {
                int rec = cmap + 4 + 8 * i;
                int platform = U16(rec), encoding = U16(rec + 2);
                int off = cmap + checked((int)U32(rec + 4));
                if (off < cmap || off + 4 > cmap + len) continue;
                int format = U16(off);
                int rank = (platform, encoding, format) switch
                {
                    (3, 10, 12) => 4,
                    (0, 4 or 6, 12) => 4,
                    (3, 1, 4) => 3,
                    (0, _, 4) => 2,
                    (0, _, 12) => 2,
                    _ => -1,
                };
                if (rank > bestRank) { bestRank = rank; best = off; }
            }
            if (best < 0) return;
            if (U16(best) == 4) ReadFormat4(best); else ReadFormat12(best);
        }

        private void ReadFormat4(int off)
        {
            int segX2 = U16(off + 6);
            int endCodes = off + 14;
            int startCodes = endCodes + segX2 + 2;
            int deltas = startCodes + segX2;
            int rangeOffsets = deltas + segX2;
            for (int s = 0; s < segX2 / 2; s++)
            {
                int end = U16(endCodes + 2 * s), start = U16(startCodes + 2 * s);
                int delta = I16(deltas + 2 * s), ro = U16(rangeOffsets + 2 * s);
                if (start > end) continue;
                for (int c = start; c <= end && c < 0xFFFF; c++)
                {
                    int g;
                    if (ro == 0) g = (c + delta) & 0xFFFF;
                    else
                    {
                        int p = rangeOffsets + 2 * s + ro + 2 * (c - start);
                        if (p + 2 > _data.Length) continue;
                        g = U16(p);
                        if (g != 0) g = (g + delta) & 0xFFFF;
                    }
                    if (g != 0) _cmap[c] = (ushort)g;
                }
            }
        }

        private void ReadFormat12(int off)
        {
            uint groups = U32(off + 12);
            for (uint i = 0; i < groups; i++)
            {
                int p = off + 16 + 12 * (int)i;
                uint start = U32(p), end = U32(p + 4), gid = U32(p + 8);
                if (end < start || end > 0x10FFFF || end - start > 0x10FFFF) continue;
                for (uint c = start; c <= end; c++)
                {
                    uint g = gid + (c - start);
                    if (g != 0 && g < 0xFFFF) _cmap[(int)c] = (ushort)g;
                }
            }
        }

        private int U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset));
        private int I16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset));
        private uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset));
        private int I32(int offset) => BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(offset));
    }

    /// <summary>한 PDF 문서 안에서 한 글꼴 파일의 사용 글리프를 모은다. 문서가 <see cref="PdfDocument.CreateFontStack"/>로 만든다.</summary>
    internal sealed class PdfFontFace
    {
        public PdfFontFace(TrueTypeFont font, string resourceName)
        {
            Font = font;
            ResourceName = resourceName;
        }

        public TrueTypeFont Font { get; }
        public string ResourceName { get; }
        /// <summary>GID → 처음 쓴 유니코드 코드 포인트(ToUnicode용). .notdef는 U+FFFD.</summary>
        public SortedDictionary<ushort, int> Used { get; } = new();
    }

    /// <summary>
    /// 글꼴 대체 순서. 글자마다 첫 번째로 글리프를 가진 글꼴을 쓴다. 어느 글꼴에도 없으면 첫 글꼴의 .notdef.
    /// Monospace면 영문 폭(첫 글꼴 '0')의 1칸, 동아시아 전각 글자는 2칸 격자에 배치해 정렬이 유지된다.
    /// </summary>
    public sealed class PdfFontStack
    {
        internal readonly IReadOnlyList<PdfFontFace> Faces;
        private readonly double _cellEm;

        internal PdfFontStack(IReadOnlyList<PdfFontFace> faces, bool monospace)
        {
            if (faces.Count == 0) throw new ArgumentException("At least one font is required.", nameof(faces));
            Faces = faces;
            Monospace = monospace;
            var first = faces[0].Font;
            _cellEm = first.TryGetGlyph('0', out ushort zero)
                ? first.Advance(zero) / (double)first.UnitsPerEm
                : 0.5;
        }

        public bool Monospace { get; }

        public float Ascent(float size) => (float)(Faces.Max(f => f.Font.Ascent / (double)f.Font.UnitsPerEm) * size);
        public float Descent(float size) => (float)(Faces.Max(f => f.Font.Descent / (double)f.Font.UnitsPerEm) * size);
        public float LineGap(float size) => (float)(Faces.Max(f => Math.Max(0, f.Font.LineGap) / (double)f.Font.UnitsPerEm) * size);
        public float LineHeight(float size) => Ascent(size) + Descent(size) + LineGap(size);
        /// <summary>줄 맨 위에서 기준선까지(위 줄간격 절반 포함).</summary>
        public float Baseline(float lineTop, float size) => lineTop + LineGap(size) / 2 + Ascent(size);

        public float Measure(string text, float size)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double em = 0;
            foreach (var g in Shape(text, record: false)) em += g.LayoutEm;
            return (float)(em * size);
        }

        internal readonly record struct Glyph(PdfFontFace Face, ushort Gid, int CodePoint, double NaturalEm, double LayoutEm);

        internal List<Glyph> Shape(string text, bool record)
        {
            var result = new List<Glyph>(text.Length);
            foreach (Rune rune in text.EnumerateRunes())
            {
                int cp = rune.Value;
                if (cp < 0x20 || cp == 0x7F || (cp is >= 0x200B and <= 0x200F) || cp == 0xFEFF) cp = ' ';
                PdfFontFace? face = null;
                ushort gid = 0;
                foreach (var f in Faces)
                    if (f.Font.TryGetGlyph(cp, out gid)) { face = f; break; }
                if (face is null) { face = Faces[0]; gid = 0; }
                double natural = face.Font.Advance(gid) / (double)face.Font.UnitsPerEm;
                double layout = Monospace ? _cellEm * (IsWide(cp) ? 2 : 1) : natural;
                if (record && !face.Used.ContainsKey(gid)) face.Used[gid] = gid == 0 ? 0xFFFD : cp;
                result.Add(new Glyph(face, gid, cp, natural, layout));
            }
            return result;
        }

        /// <summary>동아시아 전각(East Asian Wide/Fullwidth) 근사: 한글·CJK·전각 기호.</summary>
        internal static bool IsWide(int cp) =>
            cp is >= 0x1100 and <= 0x115F
            or >= 0x2E80 and <= 0x303E
            or >= 0x3041 and <= 0x33FF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xA000 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF00 and <= 0xFF60
            or >= 0xFFE0 and <= 0xFFE6
            or >= 0x20000 and <= 0x3FFFD;
    }

    /// <summary>한 쪽. 좌표는 왼쪽 위 원점, 아래로 증가, 단위 pt.</summary>
    public sealed class PdfPage
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private readonly StringBuilder _content = new();
        private readonly SortedSet<PdfFontFace> _fonts = new(Comparer<PdfFontFace>.Create((a, b) => string.CompareOrdinal(a.ResourceName, b.ResourceName)));

        internal PdfPage(float width, float height)
        {
            Width = width;
            Height = height;
        }

        public float Width { get; }
        public float Height { get; }
        internal IEnumerable<PdfFontFace> FontsUsed => _fonts;
        internal string Content => _content.ToString();

        public void FillRect(float x, float top, float width, float height, float gray)
        {
            _content.Append(Num(gray)).Append(" g ")
                .Append(Num(x)).Append(' ').Append(Num(Height - top - height)).Append(' ')
                .Append(Num(width)).Append(' ').Append(Num(height)).Append(" re f\n");
        }

        public void StrokeRect(float x, float top, float width, float height, float gray, float lineWidth)
        {
            _content.Append(Num(gray)).Append(" G ").Append(Num(lineWidth)).Append(" w ")
                .Append(Num(x)).Append(' ').Append(Num(Height - top - height)).Append(' ')
                .Append(Num(width)).Append(' ').Append(Num(height)).Append(" re S\n");
        }

        /// <summary>한 줄 글자. lineTop은 줄 상단(<see cref="PdfFontStack.LineHeight"/> 높이 안에서 기준선 계산).</summary>
        public void DrawText(PdfFontStack stack, float size, float x, float lineTop, string text, float gray = 0f)
        {
            if (string.IsNullOrEmpty(text)) return;
            var glyphs = stack.Shape(text, record: true);
            float baseline = Height - stack.Baseline(lineTop, size);
            _content.Append(Num(gray)).Append(" g\nBT\n");
            double cursor = x;
            int i = 0;
            while (i < glyphs.Count)
            {
                var face = glyphs[i].Face;
                int j = i;
                double runEm = 0;
                while (j < glyphs.Count && ReferenceEquals(glyphs[j].Face, face)) runEm += glyphs[j++].LayoutEm;
                _fonts.Add(face);
                _content.Append('/').Append(face.ResourceName).Append(' ').Append(Num(size)).Append(" Tf\n")
                    .Append("1 0 0 1 ").Append(Num(cursor)).Append(' ').Append(Num(baseline)).Append(" Tm\n");
                if (!stack.Monospace)
                {
                    _content.Append('<');
                    for (int k = i; k < j; k++) _content.Append(glyphs[k].Gid.ToString("X4", Inv));
                    _content.Append("> Tj\n");
                }
                else
                {
                    _content.Append('[');
                    double pending = 0;
                    for (int k = i; k < j; k++)
                    {
                        double slack = glyphs[k].LayoutEm - glyphs[k].NaturalEm;
                        double lead = Math.Max(0, slack / 2);
                        pending += lead;
                        if (Math.Abs(pending) > 1e-6) _content.Append(Num(-pending * 1000)).Append(' ');
                        _content.Append('<').Append(glyphs[k].Gid.ToString("X4", Inv)).Append("> ");
                        pending = slack - lead;
                    }
                    _content.Append("] TJ\n");
                }
                cursor += runEm * size;
                i = j;
            }
            _content.Append("ET\n");
        }

        internal static string Num(double value)
        {
            double r = Math.Round(value, 3);
            if (r == 0) r = 0; // -0 방지
            return r.ToString("0.###", Inv);
        }
    }

    /// <summary>
    /// 순수 관리형 PDF 작성기(PDF 1.7 문법, 고전 xref 표). 글꼴은 TrueType을 CIDFontType2 + Identity-H로 부분집합 내장하고
    /// ToUnicode CMap을 넣어 글자 선택·검색이 된다. 내용 스트림은 Flate. 같은 입력은 같은 바이트를 만든다.
    /// </summary>
    public sealed class PdfDocument
    {
        public const float A4Width = 595.276f;
        public const float A4Height = 841.89f;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly Encoding Latin1 = Encoding.Latin1;
        private readonly List<PdfPage> _pages = new();
        private readonly Dictionary<TrueTypeFont, PdfFontFace> _faces = new();
        private readonly List<PdfFontFace> _faceOrder = new();

        public string? Title { get; set; }
        public string Producer { get; set; } = "NanumCsvViewer";
        /// <summary>선택. 지정하면 Info /CreationDate에 로컬 시각(오프셋 없음)으로 기록한다.</summary>
        public DateTime? Created { get; set; }
        public int PageCount => _pages.Count;

        public PdfFontStack CreateFontStack(IEnumerable<TrueTypeFont> fonts, bool monospace)
        {
            ArgumentNullException.ThrowIfNull(fonts);
            var faces = new List<PdfFontFace>();
            foreach (var font in fonts)
            {
                if (!_faces.TryGetValue(font, out var face))
                {
                    face = new PdfFontFace(font, "F" + (_faceOrder.Count + 1).ToString(Inv));
                    _faces[font] = face;
                    _faceOrder.Add(face);
                }
                if (!faces.Contains(face)) faces.Add(face);
            }
            return new PdfFontStack(faces, monospace);
        }

        public PdfPage AddPage(float width = A4Width, float height = A4Height)
        {
            var page = new PdfPage(width, height);
            _pages.Add(page);
            return page;
        }

        public void Save(Stream output)
        {
            ArgumentNullException.ThrowIfNull(output);
            if (_pages.Count == 0) throw new InvalidOperationException("A PDF needs at least one page.");

            var objects = new List<byte[]?>();
            int Reserve() { objects.Add(null); return objects.Count; }
            const int catalog = 1, pagesNode = 2, info = 3;
            Reserve(); Reserve(); Reserve();

            var fontObject = new Dictionary<PdfFontFace, int>();
            foreach (var face in _faceOrder)
            {
                if (face.Used.Count == 0) continue;
                fontObject[face] = WriteFont(face, objects);
            }

            var pageIds = new List<int>();
            foreach (var page in _pages)
            {
                int pageId = Reserve();
                int contentId = Reserve();
                pageIds.Add(pageId);
                objects[contentId - 1] = StreamObject("", Latin1.GetBytes(page.Content));
                var res = new StringBuilder("<< /Font << ");
                foreach (var face in page.FontsUsed)
                    res.Append('/').Append(face.ResourceName).Append(' ').Append(fontObject[face].ToString(Inv)).Append(" 0 R ");
                res.Append(">> >>");
                objects[pageId - 1] = Latin1.GetBytes(
                    "<< /Type /Page /Parent " + pagesNode.ToString(Inv) + " 0 R /MediaBox [0 0 "
                    + PdfPage.Num(page.Width) + " " + PdfPage.Num(page.Height) + "] /Resources " + res
                    + " /Contents " + contentId.ToString(Inv) + " 0 R >>");
            }

            var kids = new StringBuilder();
            foreach (int id in pageIds) kids.Append(id.ToString(Inv)).Append(" 0 R ");
            objects[pagesNode - 1] = Latin1.GetBytes(
                "<< /Type /Pages /Count " + pageIds.Count.ToString(Inv) + " /Kids [" + kids + "] >>");
            objects[catalog - 1] = Latin1.GetBytes("<< /Type /Catalog /Pages " + pagesNode.ToString(Inv) + " 0 R >>");

            var infoText = new StringBuilder("<< /Producer " + TextString(Producer));
            if (!string.IsNullOrEmpty(Title)) infoText.Append(" /Title ").Append(TextString(Title));
            if (Created is { } created)
                infoText.Append(" /CreationDate (D:").Append(created.ToString("yyyyMMddHHmmss", Inv)).Append(')');
            infoText.Append(" >>");
            objects[info - 1] = Latin1.GetBytes(infoText.ToString());

            using var body = new MemoryStream();
            void Write(byte[] bytes) => body.Write(bytes, 0, bytes.Length);
            Write(Latin1.GetBytes("%PDF-1.7\n"));
            Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
            var offsets = new long[objects.Count];
            for (int i = 0; i < objects.Count; i++)
            {
                offsets[i] = body.Length;
                Write(Latin1.GetBytes((i + 1).ToString(Inv) + " 0 obj\n"));
                Write(objects[i] ?? throw new InvalidOperationException("Object " + (i + 1) + " was never written."));
                Write(Latin1.GetBytes("\nendobj\n"));
            }

            long xref = body.Length;
            var tail = new StringBuilder();
            tail.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
            tail.Append("0000000000 65535 f \n");
            foreach (long off in offsets) tail.Append(off.ToString("D10", Inv)).Append(" 00000 n \n");
            string id16 = Convert.ToHexString(SHA256.HashData(body.ToArray()).AsSpan(0, 16));
            tail.Append("trailer\n<< /Size ").Append(objects.Count + 1)
                .Append(" /Root 1 0 R /Info 3 0 R /ID [<").Append(id16).Append("> <").Append(id16).Append(">] >>\n")
                .Append("startxref\n").Append(xref.ToString(Inv)).Append("\n%%EOF\n");
            Write(Latin1.GetBytes(tail.ToString()));

            body.Position = 0;
            body.CopyTo(output);
        }

        private int WriteFont(PdfFontFace face, List<byte[]?> objects)
        {
            int Add(byte[] bytes) { objects.Add(bytes); return objects.Count; }
            var font = face.Font;
            byte[] subset = font.Subset(face.Used.Keys);
            string baseName = SubsetTag(face.Used.Keys) + "+" + font.Name;
            int fileId = Add(StreamObject("/Length1 " + subset.Length.ToString(Inv), subset));

            string Scaled(double v) => ((int)Math.Round(v * 1000.0 / font.UnitsPerEm)).ToString(Inv);
            int descriptorId = Add(Latin1.GetBytes(
                "<< /Type /FontDescriptor /FontName /" + baseName + " /Flags 4 /FontBBox ["
                + Scaled(font.XMin) + " " + Scaled(font.YMin) + " " + Scaled(font.XMax) + " " + Scaled(font.YMax)
                + "] /ItalicAngle " + PdfPage.Num(font.ItalicAngle) + " /Ascent " + Scaled(font.Ascent)
                + " /Descent -" + Scaled(font.Descent) + " /CapHeight " + Scaled(font.CapHeight)
                + " /StemV " + font.StemV.ToString(Inv) + " /FontFile2 " + fileId.ToString(Inv) + " 0 R >>"));

            var widths = new StringBuilder();
            foreach (var gid in face.Used.Keys)
                widths.Append(gid.ToString(Inv)).Append(" [").Append(Scaled(font.Advance(gid))).Append("] ");
            int cidId = Add(Latin1.GetBytes(
                "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /" + baseName
                + " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor "
                + descriptorId.ToString(Inv) + " 0 R /DW 1000 /W [" + widths + "] /CIDToGIDMap /Identity >>"));

            int toUnicodeId = Add(StreamObject("", Latin1.GetBytes(ToUnicodeCMap(face.Used))));
            return Add(Latin1.GetBytes(
                "<< /Type /Font /Subtype /Type0 /BaseFont /" + baseName
                + " /Encoding /Identity-H /DescendantFonts [" + cidId.ToString(Inv) + " 0 R] /ToUnicode "
                + toUnicodeId.ToString(Inv) + " 0 R >>"));
        }

        private static string SubsetTag(IEnumerable<ushort> gids)
        {
            uint h = 2166136261;
            foreach (ushort g in gids) h = unchecked((h ^ g) * 16777619);
            var tag = new char[6];
            for (int i = 0; i < 6; i++) { tag[i] = (char)('A' + h % 26); h /= 26; if (h == 0) h = 0x9E3779B9; }
            return new string(tag);
        }

        private static byte[] StreamObject(string extraEntries, byte[] data)
        {
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
                packed = ms.ToArray();
            }
            byte[] head = Latin1.GetBytes("<< /Length " + packed.Length.ToString(Inv) + " /Filter /FlateDecode "
                + extraEntries + " >>\nstream\n");
            byte[] tail = Latin1.GetBytes("\nendstream");
            var result = new byte[head.Length + packed.Length + tail.Length];
            head.CopyTo(result, 0);
            packed.CopyTo(result, head.Length);
            tail.CopyTo(result, head.Length + packed.Length);
            return result;
        }

        private static string ToUnicodeCMap(SortedDictionary<ushort, int> used)
        {
            var sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
              .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
              .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n")
              .Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
            var entries = used.ToArray();
            for (int i = 0; i < entries.Length; i += 100)
            {
                int n = Math.Min(100, entries.Length - i);
                sb.Append(n.ToString(Inv)).Append(" beginbfchar\n");
                for (int k = i; k < i + n; k++)
                {
                    sb.Append('<').Append(entries[k].Key.ToString("X4", Inv)).Append("> <");
                    var utf16 = new Rune(entries[k].Value).ToString();
                    foreach (char ch in utf16) sb.Append(((int)ch).ToString("X4", Inv));
                    sb.Append(">\n");
                }
                sb.Append("endbfchar\n");
            }
            sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            return sb.ToString();
        }

        /// <summary>PDF 텍스트 문자열: UTF-16BE + BOM 16진.</summary>
        private static string TextString(string value)
        {
            var sb = new StringBuilder("<FEFF");
            foreach (char ch in value) sb.Append(((int)ch).ToString("X4", Inv));
            return sb.Append('>').ToString();
        }
    }
}
