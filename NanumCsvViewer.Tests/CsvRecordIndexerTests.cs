using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class CsvRecordIndexerTests
    {
        // 데이터를 chunk 크기로 쪼개 ProcessBuffer를 반복 호출하고, 등록된 레코드 시작 오프셋을 반환.
        private static long[] Index(byte[] data, int chunk, byte delim = (byte)',')
        {
            var idx = new RecordIndex();
            var indexer = new CsvRecordIndexer(idx, data.Length, delim, 0);
            for (int off = 0; off < data.Length; off += chunk)
            {
                int len = Math.Min(chunk, data.Length - off);
                indexer.ProcessBuffer(data.AsSpan(off, len), off);
            }
            idx.Publish();
            var result = new long[idx.Count];
            for (long i = 0; i < idx.Count; i++) result[i] = idx[i];
            return result;
        }

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        [Theory]
        [InlineData("a,b\nc,d\ne,f")]   // LF
        [InlineData("a,b\r\nc,d\r\ne")]  // CRLF
        [InlineData("a\rb\rc")]          // 단독 CR
        [InlineData("\"x\ny\",1\nz,2")]  // 따옴표 안 줄바꿈
        [InlineData("a,b\n")]            // 말미 줄바꿈
        [InlineData("")]                  // 빈 파일
        [InlineData("single")]            // 줄바꿈 없는 단일 레코드
        public void Chunked_processing_matches_single_buffer(string text)
        {
            byte[] data = B(text);
            long[] whole = Index(data, Math.Max(1, data.Length));
            foreach (int chunk in new[] { 1, 2, 3, 5, 7 })
                Assert.Equal(whole, Index(data, chunk));
        }

        [Fact]
        public void Records_start_after_each_unquoted_newline()
        {
            Assert.Equal(new long[] { 0, 4, 8 }, Index(B("a,b\nc,d\ne,f"), 64));
        }

        [Fact]
        public void Crlf_counts_as_single_separator()
        {
            // a \r \n b \r \n c  → 시작 오프셋 0, 3, 6
            Assert.Equal(new long[] { 0, 3, 6 }, Index(B("a\r\nb\r\nc"), 64));
        }

        [Fact]
        public void Newline_inside_quotes_does_not_split()
        {
            // "x\ny",1\nz,2 → 인용 밖 \n은 인덱스 7 → 레코드 시작 0, 8
            Assert.Equal(new long[] { 0, 8 }, Index(B("\"x\ny\",1\nz,2"), 64));
        }

        [Fact]
        public void Trailing_newline_does_not_create_phantom_row()
        {
            // a\nb\n → 0, 2 (마지막 \n 뒤는 파일 끝이라 등록 안 함)
            Assert.Equal(new long[] { 0, 2 }, Index(B("a\nb\n"), 64));
        }

        [Fact]
        public void Cr_split_across_chunk_boundary_is_handled()
        {
            // "a\r\nb"를 ["a\r"]["\nb"]로 쪼개 awaitingLfAfterCr 상태가 청크를 넘어가도 동일해야 함
            byte[] data = B("a\r\nb");
            Assert.Equal(new long[] { 0, 3 }, Index(data, 2));
            Assert.Equal(Index(data, 64), Index(data, 2));
        }
    }
}
