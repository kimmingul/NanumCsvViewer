using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NanumCsvViewer.Import
{
    /// <summary>
    /// SAS 포맷 카탈로그(.sas7bcat)에서 읽은 값 라벨 집합.
    /// 포맷 이름은 '$' 접두를 제거하고 대문자로 정규화해 보관한다(문자·숫자 포맷 별도 사전).
    /// </summary>
    public sealed class SasCatalog
    {
        private readonly Dictionary<string, Dictionary<string, string>> _charFormats;
        private readonly Dictionary<string, Dictionary<double, string>> _numFormats;

        internal SasCatalog(
            Dictionary<string, Dictionary<string, string>> charFormats,
            Dictionary<string, Dictionary<double, string>> numFormats)
        {
            _charFormats = charFormats;
            _numFormats = numFormats;
        }

        public int FormatCount => _charFormats.Count + _numFormats.Count;

        /// <summary>포맷 이름 정규화: 공백 제거·'$' 접두 제거·대문자·끝의 자릿수 지정(w.d) 제거.</summary>
        internal static string NormalizeName(string name)
        {
            string s = name.Trim().ToUpperInvariant();
            if (s.StartsWith('$')) s = s[1..];
            // "AGEGRP8." 같은 너비 표기 제거: 끝의 '.'과 숫자를 걷어낸다(포맷 이름 본체만 비교).
            int end = s.Length;
            while (end > 0 && (char.IsAsciiDigit(s[end - 1]) || s[end - 1] == '.')) end--;
            return s[..end];
        }

        /// <summary>해당 포맷 이름의 값 라벨이 카탈로그에 있는지.</summary>
        public bool HasFormat(string? formatName)
        {
            if (string.IsNullOrWhiteSpace(formatName)) return false;
            string key = NormalizeName(formatName);
            return key.Length > 0 && (_charFormats.ContainsKey(key) || _numFormats.ContainsKey(key));
        }

        /// <summary>
        /// 해당 포맷의 허용 코드 집합(데이터 품질 코드북 대조용, 이슈 #26).
        /// 숫자 코드는 CSV 변환(FormatCell)과 동일한 "0.################" 형식으로 정규화해
        /// 임포트된 셀 텍스트와 그대로 비교할 수 있다. 포맷이 없으면 null.
        /// </summary>
        public IReadOnlySet<string>? TryGetCodes(string? formatName)
        {
            if (string.IsNullOrWhiteSpace(formatName)) return null;
            string key = NormalizeName(formatName);
            if (key.Length == 0) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (_charFormats.TryGetValue(key, out var cd))
                foreach (string code in cd.Keys) set.Add(code.Trim());
            if (_numFormats.TryGetValue(key, out var nd))
                foreach (double code in nd.Keys)
                    set.Add(code.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture));
            return set.Count > 0 ? set : null;
        }

        /// <summary>셀 원값에 대응하는 라벨. 포맷이 없거나 값이 라벨되지 않았으면 null(원값 표시).</summary>
        public string? TryLabel(string? formatName, object? value)
        {
            if (string.IsNullOrWhiteSpace(formatName) || value is null) return null;
            string key = NormalizeName(formatName);
            if (key.Length == 0) return null;
            switch (value)
            {
                case string s when _charFormats.TryGetValue(key, out var cd):
                    return cd.TryGetValue(s.Trim(), out string? cl) ? cl : null;
                case double d when _numFormats.TryGetValue(key, out var nd):
                    return nd.TryGetValue(d, out string? nl) ? nl : null;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// .sas7bcat 파서. 포맷은 공식 문서가 없어 ReadStat(MIT, readstat_sas7bcat_read.c)의
    /// 리버스 엔지니어링 구현을 순수 C#으로 포팅했다(이슈 #20).
    /// 헤더는 sas7bdat과 공유, 값 라벨 블록은 인덱스 페이지의 XLSR 엔트리 → 페이지 체인으로 위치를 찾는다.
    /// 카탈로그 포맷은 취약하므로 실패는 조용히 건너뛴다(불량 블록 스킵, 파일 단위 실패는 null).
    /// </summary>
    public static class SasCatalogReader
    {
        static SasCatalogReader()
        {
            // 한국어(CP949·EUC-KR) 등 비유니코드 카탈로그 디코딩에 필요.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private const int CatalogFirstIndexPage = 1;
        private const int CatalogUselessPages = 3;
        private const long MaxCatalogBytes = 64L * 1024 * 1024; // 방어적 상한(포맷 카탈로그는 보통 수십 KB)

        private static readonly byte[] CatalogMagic =
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xc2, 0xea, 0x81, 0x63,
            0xb3, 0x14, 0x11, 0xcf, 0xbd, 0x92, 0x08, 0x00,
            0x09, 0xc7, 0x31, 0x8c, 0x18, 0x1f, 0x10, 0x11
        };

        /// <summary>카탈로그를 읽는다. 어떤 이유로든 실패하면 null(호출부는 라벨 없이 진행).</summary>
        public static SasCatalog? TryRead(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 1024 || info.Length > MaxCatalogBytes) return null;
                byte[] data = File.ReadAllBytes(path);
                return Read(data);
            }
            catch
            {
                return null; // 손상·미지원 카탈로그는 값 라벨 없이 열기(graceful fallback)
            }
        }

        /// <summary>파싱 본체(테스트용 공개 지점). 형식 위반 시 null.</summary>
        internal static SasCatalog? Read(byte[] data)
        {
            // ---- 헤더 (sas7bdat과 동일 레이아웃, magic만 카탈로그용) ----
            if (data.Length < 336) return null;
            if (!data.AsSpan(0, 32).SequenceEqual(CatalogMagic)) return null;

            int pad1 = data[35] == 0x33 ? 4 : 0;   // a1
            bool u64 = data[32] == 0x33;            // a2
            byte endian = data[37];
            if (endian != 0x00 && endian != 0x01) return null;
            bool bigEndian = endian == 0x00;
            Encoding encoding = ResolveEncoding(data[70]);

            // sas_header_start_t(164바이트) + pad1 + 시간(16) + 예약(16) 뒤에 header_size·page_size.
            int off = 164 + pad1 + 16 + 16;
            long headerSize = Read4(data, off, bigEndian);
            long pageSize = Read4(data, off + 4, bigEndian);
            if (headerSize < 1024 || pageSize < 1024 || headerSize > (1 << 24) || pageSize > (1 << 24)) return null;
            long pageCount = u64 ? (long)Read8(data, off + 8, bigEndian) : Read4(data, off + 8, bigEndian);
            if (pageCount < 0 || pageCount > (1 << 24)) return null;

            // ---- XLSR 인덱스 위치 상수 (ReadStat과 동일) ----
            int xlsrSize = 212 + pad1 + (u64 ? 72 : 0);
            int xlsrOffset = 856 + 2 * pad1 + (u64 ? 144 : 0);
            int xlsrOOffset = 50 + pad1 + (u64 ? 24 : 0);

            var blockPointers = new List<ulong>();

            // 첫 인덱스 페이지(페이지 1)의 XLSR 목록
            long p1 = headerSize + CatalogFirstIndexPage * pageSize;
            if (p1 + pageSize > data.Length) return null;
            AugmentIndex(data, (int)(p1 + xlsrOffset), (int)(pageSize - xlsrOffset),
                xlsrSize, xlsrOOffset, u64, bigEndian, blockPointers);

            // 이후 페이지 중 선두가 XLSR인 페이지도 인덱스로 추가
            for (long i = CatalogUselessPages; i < pageCount; i++)
            {
                long pageStart = headerSize + i * pageSize;
                if (pageStart + pageSize > data.Length) break;
                if (HasAscii(data, (int)pageStart + 16, "XLSR"))
                    AugmentIndex(data, (int)pageStart + 16, (int)(pageSize - 16),
                        xlsrSize, xlsrOOffset, u64, bigEndian, blockPointers);
            }

            blockPointers.Sort();
            var charFormats = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            var numFormats = new Dictionary<string, Dictionary<double, string>>(StringComparer.Ordinal);

            ulong prev = ulong.MaxValue;
            foreach (ulong bp in blockPointers)
            {
                if (bp == prev) continue; // uniq
                prev = bp;
                long startPage = (long)(bp >> 32);
                int startPos = (int)(bp & 0xFFFF);
                byte[]? block = ReadBlock(data, headerSize, pageSize, pageCount, startPage, startPos, u64, bigEndian);
                if (block is null || block.Length == 0) continue;
                // 불량 블록은 건너뛰고 나머지 포맷은 살린다(카탈로그 취약성 대응).
                try { ParseBlock(block, pad1, u64, bigEndian, encoding, charFormats, numFormats); }
                catch { }
            }

            return new SasCatalog(charFormats, numFormats);
        }

        // 인덱스 영역에서 XLSR 엔트리를 훑어 'O' 타입 블록 포인터(page<<32|pos)를 수집.
        private static void AugmentIndex(byte[] data, int start, int len,
            int xlsrSize, int xlsrOOffset, bool u64, bool bigEndian, List<ulong> pointers)
        {
            int xlsr = start;
            int end = start + len;
            while (xlsr + xlsrSize <= end)
            {
                if (!HasAscii(data, xlsr, "XLSR")) xlsr += 8; // 일부 포인터에 8바이트 패딩
                if (!HasAscii(data, xlsr, "XLSR")) break;
                if (xlsr + xlsrSize > end) break;

                if (data[xlsr + xlsrOOffset] == (byte)'O')
                {
                    ulong page, pos;
                    if (u64) { page = Read8(data, xlsr + 8, bigEndian); pos = Read2(data, xlsr + 16, bigEndian); }
                    else { page = Read4(data, xlsr + 4, bigEndian); pos = Read2(data, xlsr + 8, bigEndian); }
                    pointers.Add((page << 32) + pos);
                }
                xlsr += xlsrSize;
            }
        }

        // 페이지 체인으로 흩어진 블록을 하나의 버퍼로 재조립. 링크 헤더가 다음 조각 위치와 이번 조각 길이를 가진다.
        private static byte[]? ReadBlock(byte[] data, long headerSize, long pageSize, long pageCount,
            long startPage, int startPos, bool u64, bool bigEndian)
        {
            var chunks = new List<(int Offset, int Len)>();
            long page = startPage;
            int pos = startPos;
            int linkCount = 0;
            int headerLen = u64 ? 32 : 16;
            int total = 0;

            while (page > 0 && pos > 0 && page <= pageCount && linkCount++ < pageCount)
            {
                long linkStart = headerSize + (page - 1) * pageSize + pos;
                if (linkStart < 0 || linkStart + headerLen > data.Length) return null;
                int next, nextPos, len;
                if (u64)
                {
                    next = (int)Read4(data, (int)linkStart, bigEndian);
                    nextPos = Read2(data, (int)linkStart + 8, bigEndian);
                    len = Read2(data, (int)linkStart + 10, bigEndian);
                }
                else
                {
                    next = (int)Read4(data, (int)linkStart, bigEndian);
                    nextPos = Read2(data, (int)linkStart + 4, bigEndian);
                    len = Read2(data, (int)linkStart + 6, bigEndian);
                }
                if (linkStart + headerLen + len > data.Length) return null;
                chunks.Add(((int)linkStart + headerLen, len));
                total += len;
                page = next;
                pos = nextPos;
            }

            var buffer = new byte[total];
            int at = 0;
            foreach (var (o, l) in chunks) { Array.Copy(data, o, buffer, at, l); at += l; }
            return buffer;
        }

        // 블록 = 포맷 하나: 헤더(이름·엔트리 수) + 값 엔트리들 + 라벨 엔트리들.
        private static void ParseBlock(byte[] block, int pad1, bool u64, bool bigEndian, Encoding encoding,
            Dictionary<string, Dictionary<string, string>> charFormats,
            Dictionary<string, Dictionary<double, string>> numFormats)
        {
            int payloadOffset = 106;
            if (block.Length < payloadOffset) return;

            int flags = Read2(block, 2, bigEndian);
            int pad = (flags & 0x08) != 0 ? 4 : 0;
            long capacity, used;
            if (u64)
            {
                capacity = (long)Read8(block, 42 + pad, bigEndian);
                used = (long)Read8(block, 50 + pad, bigEndian);
                payloadOffset += 32;
            }
            else
            {
                capacity = Read4(block, 38 + pad, bigEndian);
                used = Read4(block, 42 + pad, bigEndian);
            }

            string name = Decode(block, 8, 8, encoding);
            if (pad != 0) pad += 16;
            bool hasLongName = (!u64 && (flags & 0x80) != 0) || (u64 && (flags & 0x20) != 0);
            if (hasLongName)
            {
                if (block.Length < payloadOffset + pad + 32) return;
                name = Decode(block, payloadOffset + pad, 32, encoding);
                pad += 32;
            }
            if (block.Length < payloadOffset + pad || used <= 0) return;

            bool isString = name.StartsWith('$');
            string key = SasCatalog.NormalizeName(name);
            if (key.Length == 0) return;

            var stringLabels = isString ? new Dictionary<string, string>(StringComparer.Ordinal) : null;
            var numberLabels = isString ? null : new Dictionary<double, string>();
            ParseValueLabels(block, payloadOffset + pad, (int)used, (int)capacity,
                isString, pad1, bigEndian, encoding, stringLabels, numberLabels);

            if (stringLabels is { Count: > 0 }) charFormats[key] = stringLabels;
            else if (numberLabels is { Count: > 0 }) numFormats[key] = numberLabels;
        }

        private static void ParseValueLabels(byte[] block, int valueStart, int used, int capacity,
            bool isString, int pad1, bool bigEndian, Encoding encoding,
            Dictionary<string, string>? stringLabels, Dictionary<double, string>? numberLabels)
        {
            int end = block.Length;
            var valueOffset = new int[used];

            // 1차: 값 엔트리를 순회하며 라벨 순번(label_pos) → 엔트리 오프셋 매핑을 만든다.
            int lbp1 = valueStart;
            for (int i = 0; i < capacity; i++)
            {
                if (lbp1 + 4 > end) return;
                int entryLen = 6 + Read2(block, lbp1 + 2, bigEndian);
                if (i < used)
                {
                    if (lbp1 + 10 + pad1 + 4 > end) return;
                    long labelPos = Read4(block, lbp1 + 10 + pad1, bigEndian);
                    if (labelPos >= used) return; // ReadStat과 동일: 형식 위반이면 이 블록 포기
                    valueOffset[labelPos] = lbp1 - valueStart;
                }
                lbp1 += entryLen;
            }

            // 2차: (값, 라벨) 쌍을 라벨 순서대로 짝지어 사전에 담는다.
            int lbp2 = lbp1;
            for (int i = 0; i < used && i < capacity; i++)
            {
                int v = valueStart + valueOffset[i];
                if (v + 30 > end || lbp2 + 10 > end) return;

                string? sval = null;
                double dval = double.NaN;
                bool taggedMissing = false;
                if (isString)
                {
                    int valueEntryLen = 6 + Read2(block, v + 2, bigEndian);
                    if (v + valueEntryLen > end || valueEntryLen < 16) return;
                    sval = Decode(block, v + valueEntryLen - 16, 16, encoding).Trim();
                }
                else
                {
                    // 카탈로그의 수치 값은 파일 엔디언과 무관하게 항상 big-endian + 부호 반전 저장.
                    ulong raw = BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(v + 22, 8));
                    if ((raw | 0xFF0000000000UL) == 0xFFFFFFFFFFFFUL)
                        taggedMissing = true; // 결측 태그(.A 등): 셀 원값이 빈 문자열이라 매칭 대상 아님
                    else
                        dval = -BitConverter.UInt64BitsToDouble(raw);
                }

                int labelLen = Read2(block, lbp2 + 8, bigEndian);
                if (lbp2 + 10 + labelLen > end) return;
                string label = Decode(block, lbp2 + 10, labelLen, encoding);

                if (!taggedMissing && label.Length > 0)
                {
                    if (isString && sval is not null) stringLabels![sval] = label;
                    else if (!isString && !double.IsNaN(dval)) numberLabels![dval] = label;
                }
                lbp2 += 8 + 2 + labelLen + 1;
            }
        }

        // ---- 저수준 헬퍼 ----

        private static bool HasAscii(byte[] data, int offset, string token)
        {
            if (offset < 0 || offset + token.Length > data.Length) return false;
            for (int i = 0; i < token.Length; i++)
                if (data[offset + i] != (byte)token[i]) return false;
            return true;
        }

        private static ushort Read2(byte[] d, int o, bool bigEndian)
            => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2))
                         : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o, 2));

        private static uint Read4(byte[] d, int o, bool bigEndian)
            => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4))
                         : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o, 4));

        private static ulong Read8(byte[] d, int o, bool bigEndian)
            => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(o, 8))
                         : BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o, 8));

        private static string Decode(byte[] data, int offset, int len, Encoding encoding)
        {
            if (offset < 0 || len <= 0 || offset + len > data.Length) return string.Empty;
            return encoding.GetString(data, offset, len).TrimEnd('\0', ' ');
        }

        // SAS 인코딩 바이트 → .NET Encoding (ReadStat charset 표의 주요 항목. 미지 코드는 1252 폴백).
        private static Encoding ResolveEncoding(byte code)
        {
            try
            {
                return code switch
                {
                    0 or 204 => Encoding.GetEncoding(1252),
                    20 => Encoding.UTF8,
                    28 => Encoding.ASCII,
                    29 => Encoding.GetEncoding(28591), // ISO-8859-1
                    30 => Encoding.GetEncoding(28592),
                    33 => Encoding.GetEncoding(28595),
                    35 => Encoding.GetEncoding(28597),
                    36 => Encoding.GetEncoding(28598),
                    37 => Encoding.GetEncoding(28599),
                    40 => Encoding.GetEncoding(28605),
                    51 => Encoding.GetEncoding(874),
                    60 => Encoding.GetEncoding(1250),
                    61 => Encoding.GetEncoding(1251),
                    62 => Encoding.GetEncoding(1252),
                    63 => Encoding.GetEncoding(1253),
                    64 => Encoding.GetEncoding(1254),
                    65 => Encoding.GetEncoding(1255),
                    66 => Encoding.GetEncoding(1256),
                    67 => Encoding.GetEncoding(1257),
                    68 => Encoding.GetEncoding(1258),
                    118 or 123 => Encoding.GetEncoding(950),  // Big5
                    125 or 205 => Encoding.GetEncoding(54936), // GB18030
                    126 => Encoding.GetEncoding(936),
                    134 => Encoding.GetEncoding(51932),        // EUC-JP
                    136 or 141 or 142 => Encoding.GetEncoding(949), // CP949 (한국어)
                    138 => Encoding.GetEncoding(932),          // Shift-JIS
                    140 => Encoding.GetEncoding(51949),        // EUC-KR
                    167 => Encoding.GetEncoding(50220),        // ISO-2022-JP
                    168 => Encoding.GetEncoding(50225),        // ISO-2022-KR
                    _ => Encoding.GetEncoding(1252),
                };
            }
            catch
            {
                return Encoding.Latin1; // 인코딩 미지원 환경에서도 죽지 않게
            }
        }
    }
}
