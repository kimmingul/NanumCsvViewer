using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class RecordIndexTests
    {
        [Fact]
        public void Add_increments_count_and_preserves_values()
        {
            var idx = new RecordIndex();
            Assert.Equal(0, idx.Count);
            idx.Add(10);
            idx.Add(25);
            idx.Add(99);
            Assert.Equal(3, idx.Count);
            Assert.Equal(10, idx[0]);
            Assert.Equal(25, idx[1]);
            Assert.Equal(99, idx[2]);
        }

        [Fact]
        public void Values_survive_segment_boundary_crossing()
        {
            // 세그먼트 크기(2^20)를 넘겨 세그먼트 배열 확장 경로를 검증.
            const int SegmentSize = 1 << 20;
            int total = SegmentSize + 5;
            var idx = new RecordIndex();
            for (int i = 0; i < total; i++) idx.Add(i * 2L);

            Assert.Equal(total, idx.Count);
            Assert.Equal(0L, idx[0]);
            Assert.Equal((SegmentSize - 1) * 2L, idx[SegmentSize - 1]); // 첫 세그먼트 끝
            Assert.Equal(SegmentSize * 2L, idx[SegmentSize]);            // 두 번째 세그먼트 시작
            Assert.Equal((total - 1) * 2L, idx[total - 1]);
        }
    }
}
