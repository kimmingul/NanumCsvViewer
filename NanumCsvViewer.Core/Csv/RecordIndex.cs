using System.Threading;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 레코드 시작 바이트 오프셋의 세그먼트 배열 저장소.
    /// List&lt;long&gt;의 거대 재할당을 피하려고 고정 크기 세그먼트로 나눠 저장합니다.
    ///
    /// 동시성: 작성자(인덱싱 백그라운드 스레드)는 단 하나, 독자(UI 스레드)는 여럿일 수 있는
    /// single-writer / multi-reader 패턴입니다. Count는 슬롯에 값을 쓴 뒤 마지막에 공개(Volatile.Write)하므로,
    /// 독자는 [0, Count) 범위만 읽으면 항상 유효한 값을 봅니다. 세그먼트 포인터 배열은
    /// 새 세그먼트가 생길 때만 교체(복사 후 Volatile 공개)되어 독자가 일관된 스냅샷을 봅니다.
    /// </summary>
    public sealed class RecordIndex
    {
        private const int SegmentBits = 20;             // 세그먼트당 2^20 = 1,048,576개
        private const int SegmentSize = 1 << SegmentBits;
        private const int SegmentMask = SegmentSize - 1;

        private volatile long[][] _segments = Array.Empty<long[]>();
        private long _count;       // 독자에게 공개된 개수
        private long _writeCount;  // 작성자 전용(미공개 진행 카운터)

        /// <summary>지금까지 <b>공개된</b> 레코드 시작 오프셋 개수(헤더 포함). 독자는 [0, Count)만 읽음.</summary>
        public long Count => Volatile.Read(ref _count);

        /// <summary>
        /// 오프셋 하나 추가. 작성자 스레드에서만 호출. 핫 패스라 매 호출 공개하지 않고
        /// <see cref="Publish"/>로 일괄 공개한다(레코드당 Volatile.Write 제거).
        /// </summary>
        public void Add(long offset)
        {
            long c = _writeCount;
            int seg = (int)(c >> SegmentBits);
            int within = (int)(c & SegmentMask);

            long[][] segs = _segments;
            if (seg >= segs.Length)
            {
                var bigger = new long[seg + 1][];
                Array.Copy(segs, bigger, segs.Length);
                bigger[seg] = new long[SegmentSize];
                _segments = bigger; // volatile 공개(세그먼트 배열)
                segs = bigger;
            }
            long[] segArr = segs[seg];
            if (segArr is null) { segArr = new long[SegmentSize]; segs[seg] = segArr; }

            segArr[within] = offset;
            _writeCount = c + 1; // 공개는 Publish()에서 일괄로
        }

        /// <summary>지금까지 Add한 개수를 독자에게 공개(release). 작성자가 청크 단위로 호출.</summary>
        public void Publish() => Volatile.Write(ref _count, _writeCount);

        /// <summary>index번째 레코드 시작 오프셋.</summary>
        public long this[long index]
        {
            get
            {
                int seg = (int)(index >> SegmentBits);
                int within = (int)(index & SegmentMask);
                return _segments[seg][within];
            }
        }

        /// <summary>공개된 [0, Count) 오프셋의 연속 배열 스냅샷(영속 캐시 저장용).</summary>
        public long[] SnapshotOffsets()
        {
            long count = Count;
            var result = new long[count];
            for (long i = 0; i < count; i++) result[i] = this[i];
            return result;
        }

        /// <summary>
        /// 영속 캐시에서 오프셋을 일괄 적재(작성자 스레드에서 인덱싱 대신 호출).
        /// Add를 재사용해 세그먼트 구조를 동일하게 채운 뒤 <see cref="Publish"/>까지 수행.
        /// </summary>
        public void BulkLoad(ReadOnlySpan<long> offsets)
        {
            foreach (long off in offsets) Add(off);
            Publish();
        }
    }
}
