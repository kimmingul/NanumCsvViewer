using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class EncodingDetectorTests
    {
        private static bool Valid(byte[] bytes, bool allowIncomplete = false)
            => EncodingDetector.IsValidUtf8(bytes, allowIncomplete);

        [Fact]
        public void Ascii_is_valid()
        {
            Assert.True(Valid(new byte[] { 0x41, 0x42, 0x43 }));
        }

        [Fact]
        public void Valid_multibyte_sequences()
        {
            Assert.True(Valid(new byte[] { 0xC3, 0xA9 }));             // é
            Assert.True(Valid(new byte[] { 0xEA, 0xB0, 0x80 }));        // 가
            Assert.True(Valid(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }));  // 😀
        }

        [Fact]
        public void Rejects_lone_continuation_byte()
        {
            Assert.False(Valid(new byte[] { 0x80 }));
        }

        [Fact]
        public void Rejects_overlong_encoding()
        {
            Assert.False(Valid(new byte[] { 0xC0, 0x80 }));
        }

        [Fact]
        public void Rejects_utf16_surrogate_range()
        {
            // U+D800 → ED A0 80 (서로게이트, UTF-8에서 불법)
            Assert.False(Valid(new byte[] { 0xED, 0xA0, 0x80 }));
        }

        [Fact]
        public void Rejects_invalid_lead_bytes()
        {
            Assert.False(Valid(new byte[] { 0xF5 }));
            Assert.False(Valid(new byte[] { 0xFF }));
        }

        [Fact]
        public void Incomplete_sequence_at_end_respects_flag()
        {
            byte[] truncated = { 0x41, 0xC3 }; // 'A' + 잘린 2바이트 시작
            Assert.True(Valid(truncated, allowIncomplete: true));   // 청크 경계 허용
            Assert.False(Valid(truncated, allowIncomplete: false));  // 진짜 EOF면 손상
        }
    }
}
