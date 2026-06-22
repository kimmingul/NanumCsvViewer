using System.Text;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 결과: 사용할 인코딩, 파일 앞쪽에서 건너뛸 BOM(preamble) 바이트 수, 사람이 읽을 이름,
    /// 그리고 바이트 단위 줄 인덱싱이 안전한지 여부(UTF-16/32는 불가).
    /// </summary>
    public readonly record struct EncodingDetectionResult(
        Encoding Encoding,
        int PreambleLength,
        string DisplayName,
        bool IsByteIndexable);

    /// <summary>
    /// CSV 파일의 텍스트 인코딩을 감지합니다.
    /// 우선순위: BOM 확인 → (BOM 없으면) 표본을 strict UTF-8로 검증 → 실패 시 CP949 폴백.
    /// EUC-KR과 CP949는 실무상 하나(코드페이지 949)로 취급합니다.
    /// </summary>
    public static class EncodingDetector
    {
        public const string Utf8 = "UTF-8";
        public const string Utf8Bom = "UTF-8 (BOM)";
        public const string Cp949 = "CP949 / EUC-KR";

        /// <summary>UI 콤보박스에 표시할, 사용자가 수동 선택 가능한 인코딩 목록.</summary>
        public static readonly string[] SelectableNames = { Utf8, Utf8Bom, Cp949 };

        /// <summary>표본 검증에 사용할 최대 바이트 수(앞/중간/끝 각각).</summary>
        private const int SampleSize = 1 << 20; // 1 MB

        public static Encoding GetEncodingByName(string name) => name switch
        {
            Cp949 => Encoding.GetEncoding(949),
            _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        public static EncodingDetectionResult Detect(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            long length = fs.Length;

            // 1) BOM 확인
            Span<byte> bom = stackalloc byte[4];
            int bomRead = ReadFully(fs, bom);
            if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                return new EncodingDetectionResult(new UTF8Encoding(false), 3, Utf8Bom, true);
            if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            {
                if (bomRead >= 4 && bom[2] == 0x00 && bom[3] == 0x00)
                    return new EncodingDetectionResult(new UTF32Encoding(false, true), 4, "UTF-32 LE", false);
                return new EncodingDetectionResult(Encoding.Unicode, 2, "UTF-16 LE", false);
            }
            if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
                return new EncodingDetectionResult(Encoding.BigEndianUnicode, 2, "UTF-16 BE", false);
            if (bomRead >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xFE && bom[3] == 0xFF)
                return new EncodingDetectionResult(new UTF32Encoding(true, true), 4, "UTF-32 BE", false);

            // 2) BOM 없음 → strict UTF-8 검증 (앞/중간/끝 표본)
            bool looksUtf8 = SampleIsValidUtf8(fs, 0, length)
                             && SampleIsValidUtf8(fs, length / 2, length)
                             && SampleIsValidUtf8(fs, Math.Max(0, length - SampleSize), length);

            return looksUtf8
                ? new EncodingDetectionResult(new UTF8Encoding(false), 0, Utf8, true)
                : new EncodingDetectionResult(Encoding.GetEncoding(949), 0, Cp949, true);
        }

        private static bool SampleIsValidUtf8(FileStream fs, long start, long length)
        {
            if (length <= 0) return true;
            int toRead = (int)Math.Min(SampleSize, length - start);
            if (toRead <= 0) return true;

            byte[] buffer = new byte[toRead];
            fs.Seek(start, SeekOrigin.Begin);
            int read = ReadFully(fs, buffer);
            // 표본 시작이 멀티바이트 문자 중간일 수 있으므로 선행 continuation 바이트(0x80-0xBF)는 건너뜀.
            int offset = 0;
            if (start > 0)
                while (offset < read && (buffer[offset] & 0xC0) == 0x80) offset++;
            // 표본 끝의 불완전한 시퀀스는 허용(청크 경계).
            return IsValidUtf8(buffer.AsSpan(offset, read - offset), allowIncompleteAtEnd: true);
        }

        /// <summary>
        /// UTF-8 바이트열 유효성 검사. allowIncompleteAtEnd=true면 끝에서 잘린 멀티바이트는 유효로 간주.
        /// </summary>
        public static bool IsValidUtf8(ReadOnlySpan<byte> bytes, bool allowIncompleteAtEnd)
        {
            int i = 0, n = bytes.Length;
            while (i < n)
            {
                byte b = bytes[i];
                if (b <= 0x7F) { i++; continue; }

                int need;
                byte min2 = 0x80, max2 = 0xBF; // 두 번째 바이트 허용 범위(특수 케이스만 조정)
                if (b >= 0xC2 && b <= 0xDF) need = 1;
                else if (b == 0xE0) { need = 2; min2 = 0xA0; }
                else if (b >= 0xE1 && b <= 0xEC) need = 2;
                else if (b == 0xED) { need = 2; max2 = 0x9F; } // 서로게이트 제외
                else if (b >= 0xEE && b <= 0xEF) need = 2;
                else if (b == 0xF0) { need = 3; min2 = 0x90; }
                else if (b >= 0xF1 && b <= 0xF3) need = 3;
                else if (b == 0xF4) { need = 3; max2 = 0x8F; }
                else return false; // 0xC0,0xC1,0xF5-0xFF 및 단독 continuation

                if (i + need >= n)
                    return allowIncompleteAtEnd; // 끝에서 잘림

                // 두 번째 바이트(특수 범위 적용)
                byte b1 = bytes[i + 1];
                if (b1 < min2 || b1 > max2) return false;
                // 나머지 continuation 바이트
                for (int k = 2; k <= need; k++)
                {
                    byte bk = bytes[i + k];
                    if (bk < 0x80 || bk > 0xBF) return false;
                }
                i += need + 1;
            }
            return true;
        }

        private static int ReadFully(FileStream fs, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int r = fs.Read(buffer.Slice(total));
                if (r == 0) break;
                total += r;
            }
            return total;
        }
    }
}
