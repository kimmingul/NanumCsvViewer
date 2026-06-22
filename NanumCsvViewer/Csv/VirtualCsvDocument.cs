using System.Buffers;
using System.IO;
using System.Text;

namespace NanumCsvViewer.Csv
{
    public readonly record struct IndexProgress(long BytesProcessed, long FileLength, long RowsSoFar)
    {
        public int Percent => FileLength <= 0 ? 100 : (int)Math.Min(100, BytesProcessed * 100 / FileLength);
    }

    /// <summary>
    /// 대용량 CSV의 가상 뷰 문서. 첫 페이지는 즉시 제공하고, 백그라운드 단일 디스크 패스로
    /// 레코드 오프셋을 인덱싱하면서(적응형이면 동시에) RAM 버퍼를 채웁니다. 행은 요청 시 디코드·파싱하여
    /// LRU 캐시에 보관하므로 파일 크기와 무관하게 메모리가 일정합니다.
    /// </summary>
    public sealed class VirtualCsvDocument : IDisposable
    {
        /// <summary>이 크기 이하면 필터/정렬을 빠르게 하려고 파일 전체를 RAM에 보관(적응형).</summary>
        public const long RamBufferBudgetBytes = 1_500_000_000;
        private const int RowCacheCapacity = 8192;
        private const int ReadUnit = MemoryFileBuffer.ChunkSize; // 16 MB

        private readonly string _path;
        private readonly RecordIndex _index = new();
        private readonly RowCache _cache = new(RowCacheCapacity);

        private Encoding _encoding;
        private readonly int _preamble;
        private byte _delim; // 실제 사용 구분자 바이트(Initialize에서 감지)

        private readonly IRandomByteSource _diskSource;
        private volatile MemoryFileBuffer? _ramBuffer;
        private readonly MemoryFileBuffer? _ramBufferPending;

        private long _headerStart;
        private long _headerEnd;
        private int[]? _viewMap; // null이면 항등(전체, 원래 순서)

        public long FileLength { get; }
        public string[] Header { get; private set; } = Array.Empty<string>();
        public int ColumnCount => Header.Length;
        public string EncodingName { get; private set; }
        public char Delimiter => (char)_delim;
        public bool IndexingComplete { get; private set; }
        public bool WillUseRam { get; }
        public bool InMemory => _ramBuffer is not null;
        public bool RowCountTruncated { get; private set; }
        public bool IsFiltered => _viewMap is not null;

        private VirtualCsvDocument(string path, EncodingDetectionResult det)
        {
            _path = path;
            _encoding = det.Encoding;
            _preamble = det.PreambleLength;
            EncodingName = det.DisplayName;
            _diskSource = new FileByteSource(path);
            FileLength = _diskSource.Length;
            WillUseRam = FileLength <= RamBufferBudgetBytes;
            if (WillUseRam) _ramBufferPending = new MemoryFileBuffer(FileLength);
        }

        public static VirtualCsvDocument Open(string path)
        {
            var det = EncodingDetector.Detect(path);
            if (!det.IsByteIndexable)
                throw new NotSupportedException(
                    $"'{det.DisplayName}' 인코딩은 대용량 고속 모드에서 지원되지 않습니다.\nUTF-8 또는 CP949(EUC-KR) 파일을 사용하세요.");

            var doc = new VirtualCsvDocument(path, det);
            doc.Initialize();
            return doc;
        }

        private void Initialize()
        {
            // 첫 부분 표본을 읽어 구분자 감지 + 헤더 범위 계산.
            int sampleLen = (int)Math.Min(1 << 20, FileLength);
            byte[] sample = new byte[Math.Max(sampleLen, 1)];
            _diskSource.Read(0, sample.AsSpan(0, sampleLen));

            _delim = DetectDelimiter(sample.AsSpan(0, sampleLen), _preamble);

            // 헤더 레코드 범위: 임시 인덱서로 첫 레코드 경계를 찾음.
            var tmp = new RecordIndex();
            var probe = new CsvRecordIndexer(tmp, FileLength, _delim, _preamble);
            probe.ProcessBuffer(sample.AsSpan(0, sampleLen), 0);
            _headerStart = _preamble;
            _headerEnd = tmp.Count >= 2 ? tmp[1] : Math.Min(sampleLen, FileLength);

            Header = DecodeAndParse(_headerStart, _headerEnd);
        }

        private static byte DetectDelimiter(ReadOnlySpan<byte> sample, int preamble)
        {
            // 첫 줄(인용 밖)에서 후보 구분자 빈도를 세어 가장 많은 것을 선택. 기본 ','.
            ReadOnlySpan<byte> candidates = stackalloc byte[] { (byte)',', (byte)';', (byte)'\t', (byte)'|' };
            Span<int> counts = stackalloc int[4];
            bool inQuotes = false;
            for (int i = preamble; i < sample.Length; i++)
            {
                byte b = sample[i];
                if (b == (byte)'"') { inQuotes = !inQuotes; continue; }
                if (inQuotes) continue;
                if (b == 0x0A || b == 0x0D) break; // 첫 줄 끝
                for (int c = 0; c < candidates.Length; c++)
                    if (b == candidates[c]) counts[c]++;
            }
            int best = 0;
            for (int c = 1; c < counts.Length; c++)
                if (counts[c] > counts[best]) best = c;
            return counts[best] > 0 ? candidates[best] : (byte)',';
        }

        /// <summary>백그라운드 단일 패스: 순차로 읽으며 인덱싱(+적응형이면 RAM 적재).</summary>
        public Task RunIndexingAsync(IProgress<IndexProgress> progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                var indexer = new CsvRecordIndexer(_index, FileLength, _delim, _preamble);

                byte[]? reuse = WillUseRam ? null : new byte[ReadUnit];
                long offset = 0;
                int chunkIndex = 0;
                long lastReport = 0;

                while (offset < FileLength)
                {
                    ct.ThrowIfCancellationRequested();
                    int thisLen = (int)Math.Min(ReadUnit, FileLength - offset);
                    byte[] chunk = WillUseRam ? new byte[thisLen] : reuse!;
                    ReadFully(fs, chunk, thisLen);

                    if (WillUseRam) _ramBufferPending!.SetChunk(chunkIndex, chunk);
                    indexer.ProcessBuffer(chunk.AsSpan(0, thisLen), offset);

                    offset += thisLen;
                    chunkIndex++;

                    if (offset - lastReport >= ReadUnit || offset >= FileLength)
                    {
                        lastReport = offset;
                        progress.Report(new IndexProgress(offset, FileLength, _index.Count));
                    }
                }

                IndexingComplete = true;
                if (WillUseRam) _ramBuffer = _ramBufferPending; // 디스크→RAM 전환(이후 필터 스캔 가속)
                progress.Report(new IndexProgress(FileLength, FileLength, _index.Count));
            }, ct);
        }

        /// <summary>현재 표시 가능한 데이터 행 수(헤더 제외). 인덱싱 진행 중에는 끝 오프셋이 확정된 행만.</summary>
        public int DataRowsAvailable
        {
            get
            {
                long recs = _index.Count;
                long rows = IndexingComplete ? recs - 1 : recs - 2;
                if (rows < 0) rows = 0;
                if (rows > int.MaxValue) { RowCountTruncated = true; rows = int.MaxValue; }
                return (int)rows;
            }
        }

        /// <summary>그리드에 표시할 행 수(필터 적용 시 일치 행 수).</summary>
        public int DisplayRowCount => _viewMap?.Length ?? DataRowsAvailable;

        /// <summary>표시 행(viewIndex) → 실제 데이터 행을 디코드/파싱하여 반환(캐시 사용).</summary>
        public string[] GetDisplayRow(int viewIndex)
        {
            int dataRow = _viewMap is { } map ? map[viewIndex] : viewIndex;
            return GetDataRow(dataRow);
        }

        /// <summary>표시 행(viewIndex) → 원본 데이터 행 번호(1-based, 헤더 제외). 필터/정렬 시 원래 위치를 유지.</summary>
        public long GetSourceRowNumber(int viewIndex)
        {
            int dataRow = _viewMap is { } map ? map[viewIndex] : viewIndex;
            return dataRow + 1L;
        }

        public string[] GetDataRow(int dataRow)
        {
            if (_cache.TryGet(dataRow, out var cached)) return cached;

            long rec = dataRow + 1L; // 0번 레코드는 헤더
            long count = _index.Count;
            long start = _index[rec];
            long end = (rec + 1 < count) ? _index[rec + 1] : FileLength;

            string[] fields = DecodeAndParse(start, end);
            _cache.Add(dataRow, fields);
            return fields;
        }

        private string[] DecodeAndParse(long start, long end)
        {
            int len = (int)(end - start);
            if (len <= 0) return new[] { string.Empty };

            byte[] buf = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                var src = (IRandomByteSource?)_ramBuffer ?? _diskSource;
                src.Read(start, buf.AsSpan(0, len));

                // 말미 CR/LF 제거(레코드 범위는 다음 레코드 시작까지라 줄바꿈을 포함).
                int n = len;
                while (n > 0 && (buf[n - 1] == 0x0A || buf[n - 1] == 0x0D)) n--;

                string line = _encoding.GetString(buf, 0, n);
                return CsvRowParser.Parse(line, (char)_delim);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        public void ChangeEncoding(string encodingName)
        {
            _encoding = EncodingDetector.GetEncodingByName(encodingName);
            EncodingName = encodingName;
            _cache.Clear();
            Header = DecodeAndParse(_headerStart, _headerEnd);
        }

        // ---- 필터 / 정렬 (Phase 3) : 인덱싱 완료 후에만 호출 ----

        /// <summary>predicate가 참인 데이터 행만 남기는 뷰맵을 백그라운드로 구성.</summary>
        public Task ApplyFilterAsync(Func<string[], bool> predicate, IProgress<int>? progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                int total = DataRowsAvailable;
                var matches = new List<int>();
                for (int i = 0; i < total; i++)
                {
                    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    if (predicate(GetDataRow(i))) matches.Add(i);
                    if (progress is not null && (i & 0x3FFFF) == 0)
                        progress.Report(total == 0 ? 100 : (int)(i * 100L / total));
                }
                _viewMap = matches.ToArray();
                progress?.Report(100);
            }, ct);
        }

        /// <summary>현재 뷰(필터 결과 또는 전체)를 지정 컬럼 기준으로 정렬한 뷰맵 구성.</summary>
        public Task SortAsync(int column, bool ascending, IProgress<int>? progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                int total = DataRowsAvailable;
                int[] baseMap = _viewMap ?? CreateIdentity(total);
                var keys = new string[baseMap.Length];
                for (int i = 0; i < baseMap.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    string[] row = GetDataRow(baseMap[i]);
                    keys[i] = column < row.Length ? row[column] : string.Empty;
                    if (progress is not null && (i & 0x3FFFF) == 0 && baseMap.Length > 0)
                        progress.Report((int)(i * 100L / baseMap.Length));
                }
                // keys[i]는 baseMap[i]에 대응 → 인덱스(idx)를 키 기준으로 정렬한 뒤 baseMap을 재배열
                int[] idx = CreateIdentity(baseMap.Length);
                Array.Sort(idx, (x, y) =>
                {
                    int c = NaturalCompare(keys[x], keys[y]);
                    return ascending ? c : -c;
                });
                var result = new int[baseMap.Length];
                for (int i = 0; i < idx.Length; i++) result[i] = baseMap[idx[i]];
                _viewMap = result;
                progress?.Report(100);
            }, ct);
        }

        private static int[] CreateIdentity(int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = i;
            return a;
        }

        /// <summary>숫자처럼 보이면 수치 비교, 아니면 서수 비교(자연 정렬 근사).</summary>
        private static int NaturalCompare(string a, string b)
        {
            bool na = double.TryParse(a, out double da);
            bool nb = double.TryParse(b, out double db);
            if (na && nb) return da.CompareTo(db);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public void ClearView() => _viewMap = null;

        public void Dispose()
        {
            _diskSource.Dispose();
            _ramBuffer?.Dispose();
        }

        private static void ReadFully(FileStream fs, byte[] buffer, int length)
        {
            int total = 0;
            while (total < length)
            {
                int r = fs.Read(buffer, total, length - total);
                if (r == 0) break;
                total += r;
            }
        }
    }
}
