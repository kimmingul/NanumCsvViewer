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
        private long _count;

        /// <summary>지금까지 등록된 레코드 시작 오프셋 개수(헤더 포함).</summary>
        public long Count => Volatile.Read(ref _count);

        /// <summary>오프셋 하나 추가. 작성자 스레드에서만 호출.</summary>
        public void Add(long offset)
        {
            long c = _count;
            int seg = (int)(c >> SegmentBits);
            int within = (int)(c & SegmentMask);

            long[][] segs = _segments;
            if (seg >= segs.Length)
            {
                var bigger = new long[seg + 1][];
                Array.Copy(segs, bigger, segs.Length);
                bigger[seg] = new long[SegmentSize];
                _segments = bigger; // volatile 공개
            }
            else if (segs[seg] is null)
            {
                segs[seg] = new long[SegmentSize];
            }

            _segments[seg][within] = offset;
            Volatile.Write(ref _count, c + 1); // 값을 쓴 뒤 마지막에 개수 공개
        }

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
    }
}
