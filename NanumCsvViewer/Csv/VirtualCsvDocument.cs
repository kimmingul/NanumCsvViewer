using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace NanumCsvViewer.Csv
{
    public readonly record struct IndexProgress(long BytesProcessed, long FileLength, long RowsSoFar)
    {
        public int Percent => FileLength <= 0 ? 100 : (int)Math.Min(100, BytesProcessed * 100 / FileLength);
    }

    /// <summary>정렬 기준 한 단계: 컬럼 인덱스 + 오름차순 여부. 다중 컬럼 정렬은 이 목록의 순서대로 우선순위를 가짐.</summary>
    public readonly record struct SortKey(int Column, bool Ascending);

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
        private const int PrefetchChunks = 3;                    // 읽기/스캔 겹치기용 선반입 청크 수

        private readonly string _path;
        private readonly RecordIndex _index = new();
        private readonly RowCache _cache = new(RowCacheCapacity);

        /// <summary>셀 편집 덮개. 변경되면 행 캐시를 비워 다음 읽기에 반영한다.</summary>
        public CellEdits Edits { get; } = new();

        private Encoding _encoding;
        private readonly int _preamble;
        private byte _delim; // 실제 사용 구분자 바이트(Initialize에서 감지)

        private readonly IRandomByteSource _diskSource;
        private volatile MemoryFileBuffer? _ramBuffer;
        private readonly MemoryFileBuffer? _ramBufferPending;

        private long _headerStart;
        private long _headerEnd;
        // 백그라운드(필터/정렬)에서 교체하고 UI 스레드에서 읽으므로 volatile로 가시성 보장.
        // 참조 대입은 원자적이며, 읽는 쪽은 항상 지역 변수로 스냅샷을 떠 길이/인덱스를 일관되게 사용한다.
        private volatile int[]? _viewMap; // null이면 항등(전체, 원래 순서). 값은 "행 id"(원본 0..N-1, 추가 행은 그 뒤)
        // 행 삽입/삭제가 있을 때만 존재: _live = 화면 순서의 행 id(삭제 제외), _rank = 행 id → 화면 순서 위치(삭제는 -1).
        private volatile int[]? _live;
        private volatile int[]? _rank;
        private long _structureSeen, _headerSeen;

        private static readonly string[] EmptyRow = { string.Empty };

        public long FileLength { get; }
        private string[] _rawHeader = Array.Empty<string>();
        private volatile string[] _header = Array.Empty<string>();

        /// <summary>현재 컬럼 이름(파일의 이름 + 사용자가 바꾼 이름). 분석·필터·그리드가 모두 이 이름을 본다.</summary>
        public string[] Header => _header;

        /// <summary>원래 컬럼 이름: 파일의 이름 + 시트 편집으로 추가한 컬럼의 처음 이름(이름 변경 전). 이름 변경 되돌림의 기준. 표시 순서·삭제를 반영한다.</summary>
        public string[] OriginalHeader => Edits.HasAppendedColumns || Edits.HasDeletedColumns || Edits.HasColumnOrder
            ? Edits.ToDisplayOrder(Edits.HasAppendedColumns ? _rawHeader.Concat(Edits.AppendedColumnNames()).ToArray() : _rawHeader)
            : _rawHeader;

        /// <summary>파일에 적힌 컬럼 수(시트 편집으로 추가한 컬럼 제외). 추가 컬럼 인덱스는 이 값부터 시작한다.</summary>
        public int RawColumnCount => _rawHeader.Length;

        public int ColumnCount => Header.Length;
        public string EncodingName { get; private set; }
        public char Delimiter => (char)_delim;
        public bool IndexingComplete { get; private set; }
        public bool WillUseRam { get; }
        public bool InMemory => _ramBuffer is not null;
        public bool IsFiltered => _viewMap is not null;

        // 영속 인덱스 캐시 검증 키(디스크 모드 재열기 시 인덱싱 생략용). 감지 시점 값으로 고정.
        private readonly string _detectedEncodingName;
        private readonly DateTime _lastWriteUtc;

        private VirtualCsvDocument(string path, EncodingDetectionResult det)
        {
            _path = path;
            Edits.Changed += OnEditsChanged;
            _encoding = det.Encoding;
            _preamble = det.PreambleLength;
            EncodingName = det.DisplayName;
            _detectedEncodingName = det.DisplayName;
            try { _lastWriteUtc = File.GetLastWriteTimeUtc(path); } catch { _lastWriteUtc = DateTime.MinValue; }
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
            tmp.Publish();
            _headerStart = _preamble;
            _headerEnd = tmp.Count >= 2 ? tmp[1] : Math.Min(sampleLen, FileLength);

            _rawHeader = DecodeAndParse(_headerStart, _headerEnd);
            _header = Edits.ApplyHeader(_rawHeader);
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
            return Task.Run(async () =>
            {
                // 디스크 모드: 유효한 영속 인덱스 캐시가 있으면 전체 스캔을 생략(RAM 모드는 버퍼 적재가 필요해 제외).
                if (!WillUseRam)
                {
                    long[]? cached = IndexCache.TryLoad(_path, FileLength, _lastWriteUtc, _detectedEncodingName, _delim);
                    if (cached is not null)
                    {
                        _index.BulkLoad(cached);
                        IndexingComplete = true;
                        progress.Report(new IndexProgress(FileLength, FileLength, _index.Count));
                        return;
                    }
                }

                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                var indexer = new CsvRecordIndexer(_index, FileLength, _delim, _preamble);

                // 읽기(I/O)와 스캔(CPU)을 겹친다: 생산자가 다음 청크를 읽는 동안 소비자가 현재 청크를 스캔.
                var channel = Channel.CreateBounded<(byte[] buf, int len, long off, int idx)>(
                    new BoundedChannelOptions(PrefetchChunks) { SingleReader = true, SingleWriter = true });

                // 디스크 모드(미적재)는 버퍼 재사용 풀, RAM 모드는 청크마다 새 배열(어차피 보관됨).
                var pool = WillUseRam ? null : new System.Collections.Concurrent.ConcurrentQueue<byte[]>();

                // ── 생산자: 순차 읽기 ──
                var producer = Task.Run(async () =>
                {
                    long off = 0;
                    int idx = 0;
                    try
                    {
                        while (off < FileLength)
                        {
                            ct.ThrowIfCancellationRequested();
                            int len = (int)Math.Min(ReadUnit, FileLength - off);
                            byte[] buf;
                            if (WillUseRam) buf = new byte[len];
                            else if (!pool!.TryDequeue(out buf!)) buf = new byte[ReadUnit];
                            ReadFully(fs, buf, len);
                            await channel.Writer.WriteAsync((buf, len, off, idx), ct);
                            off += len;
                            idx++;
                        }
                        channel.Writer.Complete();
                    }
                    catch (Exception ex)
                    {
                        channel.Writer.Complete(ex); // 소비자 측에서 예외 전파
                    }
                }, ct);

                // ── 소비자(현재 스레드): 순서대로 스캔 ──
                long lastReport = 0;
                await foreach (var item in channel.Reader.ReadAllAsync(ct))
                {
                    if (WillUseRam) _ramBufferPending!.SetChunk(item.idx, item.buf);
                    indexer.ProcessBuffer(item.buf.AsSpan(0, item.len), item.off);
                    _index.Publish(); // 청크 단위로 개수 공개

                    if (!WillUseRam) pool!.Enqueue(item.buf); // 버퍼 재사용

                    long processed = item.off + item.len;
                    if (processed - lastReport >= ReadUnit || processed >= FileLength)
                    {
                        lastReport = processed;
                        progress.Report(new IndexProgress(processed, FileLength, _index.Count));
                    }
                }

                await producer; // 생산자 예외 전파

                _index.Publish(); // 최종 공개
                IndexingComplete = true;
                if (WillUseRam) _ramBuffer = _ramBufferPending; // 디스크→RAM 전환(이후 필터 스캔 가속)
                else IndexCache.Save(_path, FileLength, _lastWriteUtc, _detectedEncodingName, _delim, _index); // 디스크 모드만 캐시
                progress.Report(new IndexProgress(FileLength, FileLength, _index.Count));
            }, ct);
        }

        /// <summary>int 범위로 자르기 전, 실제 원본 데이터 행 수(헤더 제외). 인덱싱 중에는 끝 오프셋이 확정된 행만.</summary>
        private long RawDataRowCount
        {
            get
            {
                long rows = IndexingComplete ? _index.Count - 1 : _index.Count - 2;
                return rows < 0 ? 0 : rows;
            }
        }

        /// <summary>행 수가 int.MaxValue를 넘어 일부만 표시되는지. 부수효과 없는 순수 계산.</summary>
        public bool RowCountTruncated => RawDataRowCount > int.MaxValue;

        /// <summary>원본 파일의 데이터 행 수(DataGridView용 int 상한). 추가 행의 id는 이 값부터 시작한다.</summary>
        public int BaseRowCount => (int)Math.Min(int.MaxValue, RawDataRowCount);

        /// <summary>행 삽입·삭제가 가능한가(인덱싱 완료 + 행 수가 int 범위 안).</summary>
        public bool CanEditStructure => IndexingComplete && !RowCountTruncated;

        /// <summary>현재 데이터 행 수(헤더 제외, 삭제 제외·추가 포함, DataGridView용 int 상한 적용). 필터와 무관.</summary>
        public int DataRowsAvailable => _live?.Length ?? BaseRowCount;

        /// <summary>그리드에 표시할 행 수(필터 적용 시 일치 행 수).</summary>
        public int DisplayRowCount => _viewMap?.Length ?? DataRowsAvailable;

        /// <summary>Freeze the current row order without materializing CSV fields.
        /// The caller must keep this document open while enumerating the snapshot.</summary>
        public IReadOnlyList<string[]> SnapshotViewRows()
            => new ViewRows(this, _viewMap?.ToArray(), _live, DataRowsAvailable);

        private sealed class ViewRows(VirtualCsvDocument document, int[]? map, int[]? live, int total) : IReadOnlyList<string[]>
        {
            public int Count => map?.Length ?? live?.Length ?? total;
            public string[] this[int index] => index >= 0 && index < Count
                ? document.ParseDataRow(map is not null ? map[index] : live is not null ? live[index] : index)
                : throw new ArgumentOutOfRangeException(nameof(index));
            public IEnumerator<string[]> GetEnumerator()
            {
                for (int i = 0; i < Count; i++) yield return this[i];
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>표시 행(viewIndex)을 행 id로 변환. 뷰맵 스냅샷 1회 + 범위 가드(레이스 안전).</summary>
        private bool TryMapToDataRow(int viewIndex, out int dataRow)
        {
            int[]? map = _viewMap; // 스냅샷: 길이/인덱스 모두 이 참조로만 판단
            if (map is not null)
            {
                if ((uint)viewIndex >= (uint)map.Length) { dataRow = -1; return false; }
                dataRow = map[viewIndex];
                return true;
            }
            int[]? live = _live;
            if (live is not null)
            {
                if ((uint)viewIndex >= (uint)live.Length) { dataRow = -1; return false; }
                dataRow = live[viewIndex];
                return true;
            }
            dataRow = viewIndex;
            return viewIndex >= 0;
        }

        /// <summary>현재 화면 순서(삭제 제외)의 행 id 전체. 행 구조 편집이 없으면 새 항등 배열.</summary>
        private int[] LiveIds() => _live ?? CreateIdentity(BaseRowCount);

        /// <summary>데이터 행 위치(0 = 헤더 다음 첫 행, 삭제 제외·추가 포함) → 행 id. 범위 밖이면 -1.</summary>
        private int PositionToId(int position)
        {
            int[]? live = _live;
            if (live is null) return position >= 0 ? position : -1;
            return (uint)position < (uint)live.Length ? live[position] : -1;
        }

        /// <summary>표시 행(viewIndex) → 실제 데이터 행을 디코드/파싱하여 반환(캐시 사용).</summary>
        public string[] GetDisplayRow(int viewIndex)
            => TryMapToDataRow(viewIndex, out int rowId) ? GetRowById(rowId) : EmptyRow;

        /// <summary>
        /// 표시 행(viewIndex) → 현재 행 번호(1-based, 헤더 제외). 필터/정렬 시 원래 위치를 유지한다.
        /// 행을 삭제·추가했으면 편집 후 순서 기준 번호(삭제된 행은 건너뛰고 번호가 당겨진다).
        /// </summary>
        public long GetSourceRowNumber(int viewIndex)
        {
            if (!TryMapToDataRow(viewIndex, out int rowId)) return 0L;
            var rank = _rank;
            if (rank is null) return rowId + 1L;
            return (uint)rowId < (uint)rank.Length && rank[rowId] >= 0 ? rank[rowId] + 1L : 0L;
        }

        /// <summary>표시 행 → 행 id(편집 덮개의 키). 범위 밖이면 -1. 추가 행의 id는 BaseRowCount 이상.</summary>
        public int GetRowId(int viewIndex) => TryMapToDataRow(viewIndex, out int r) ? r : -1;

        /// <summary>데이터 행 위치(필터와 무관한 화면 순서, 0-based) → 행 id. 범위 밖이면 -1.</summary>
        public int GetRowIdAtPosition(int position) => PositionToId(position);

        /// <summary>행 id가 현재 표시 중인 위치(없으면 -1). 필터에 걸러졌거나 삭제된 행은 -1.</summary>
        public int FindViewIndex(int rowId)
        {
            if (_viewMap is { } map) return Array.IndexOf(map, rowId);
            var rank = _rank;
            if (rank is not null) return (uint)rowId < (uint)rank.Length ? rank[rowId] : -1;
            return rowId >= 0 && rowId < BaseRowCount ? rowId : -1;
        }

        /// <summary>화면 순서에서 rowId 바로 앞 행의 id(첫 행이면 -1). "위에 행 삽입"의 앵커용.</summary>
        public int PredecessorId(int rowId)
        {
            var live = _live;
            if (live is null) return rowId - 1;
            var rank = _rank;
            if (rank is null || (uint)rowId >= (uint)rank.Length || rank[rowId] <= 0) return -1;
            return live[rank[rowId] - 1];
        }

        /// <summary>표시 행이 시트 편집으로 추가한 행인가.</summary>
        public bool IsAddedRow(int viewIndex) => TryMapToDataRow(viewIndex, out int rowId) && Edits.IsAddedRow(rowId);

        /// <summary>이미 캐시에 있을 때만 행을 반환(디스크/파싱 트리거 없음). 행 높이 계산 등 핫 패스용.</summary>
        public bool TryGetCachedDisplayRow(int viewIndex, out string[] fields)
        {
            if (!TryMapToDataRow(viewIndex, out int rowId)) { fields = EmptyRow; return false; }
            return _cache.TryGet(rowId, out fields);
        }

        /// <summary>데이터 행 위치(필터와 무관한 화면 순서) → 행 파싱(캐시 사용).</summary>
        public string[] GetDataRow(int position)
        {
            int rowId = PositionToId(position);
            return rowId < 0 ? EmptyRow : GetRowById(rowId);
        }

        /// <summary>행 id → 현재 행(편집 덮개·추가 컬럼 적용, 캐시 사용). 행 id는 GetRowId가 돌려주는 값.</summary>
        public string[] GetRowById(int rowId)
        {
            if (_cache.TryGet(rowId, out var cached)) return cached;
            string[] fields = ParseDataRow(rowId);
            _cache.Add(rowId, fields);
            return fields;
        }

        /// <summary>행 id → 현재 행. 캐시를 조회하지도 채우지도 않는다(대량 스캔용).</summary>
        public string[] GetRowByIdUncached(int rowId) => rowId < 0 ? EmptyRow : ParseDataRow(rowId);

        /// <summary>캐시를 조회하지도 채우지도 않는 디코드(데이터 행 위치 기준). 필터/정렬의 대량 스캔용(LRU 오염·락 경합 방지).</summary>
        public string[] GetDataRowUncached(int position)
        {
            int rowId = PositionToId(position);
            return rowId < 0 ? EmptyRow : ParseDataRow(rowId);
        }

        // 모든 읽기(그리드·필터·정렬·분석·내보내기)의 단일 경로: 원본 파싱(또는 추가 행 기본값) + 셀 편집 덮개.
        private string[] ParseDataRow(int rowId) => Edits.Apply(rowId, ParseRawDataRow(rowId));

        /// <summary>
        /// 편집 덮개의 셀 값을 적용하지 않은 행(편집 전 값 확인·되돌림 비교용). 추가 행은 빈 값 행.
        /// 시트 편집으로 추가한 컬럼이 있으면 그 칸(빈 값)까지 포함한 전체 너비다 — 추가 컬럼의 "원래 값"은 항상 빈 값.
        /// </summary>
        public string[] GetOriginalRow(int rowId) => Edits.ToDisplayOrder(ParseRawDataRow(rowId));

        private string[] ParseRawDataRow(int rowId)
        {
            long rec = rowId + 1L; // 0번 레코드는 헤더
            long count = _index.Count;
            if (rec < 1) return EmptyRow;
            if (rec >= count)
            {
                // 원본 끝을 넘은 id = 시트 편집으로 추가한 행. 그 밖(레이스/인덱싱 중)이면 안전하게 빈 행.
                return Edits.ExpandRaw(IndexingComplete && Edits.GetAddedBase(rowId) is { } added ? added : EmptyRow, isAddedRow: true);
            }
            long start = _index[rec];
            long end = (rec + 1 < count) ? _index[rec + 1] : FileLength;
            return Edits.ExpandRaw(DecodeAndParse(start, end), isAddedRow: false);
        }

        // 덮개 변경 반영: 행 캐시 폐기 + 행 구조(화면 순서)·헤더 이름 재구성. 구독 순서상 UI 핸들러보다 먼저 실행된다.
        private void OnEditsChanged()
        {
            _cache.Clear();
            if (_structureSeen != Edits.StructureVersion) { _structureSeen = Edits.StructureVersion; RebuildStructure(); }
            if (_headerSeen != Edits.HeaderVersion)
            {
                _headerSeen = Edits.HeaderVersion;
                _header = Edits.ApplyHeader(_rawHeader);
            }
        }

        private void RebuildStructure()
        {
            if (!Edits.HasStructureEdits)
            {
                _live = null;
                _rank = null;
            }
            else
            {
                int baseRows = Edits.BaseRowCount >= 0 ? Edits.BaseRowCount : BaseRowCount;
                int[] live = Edits.BuildLiveOrder(baseRows);
                var rank = new int[Edits.TotalRowIds(baseRows)];
                Array.Fill(rank, -1);
                for (int p = 0; p < live.Length; p++) rank[live[p]] = p;
                _rank = rank;
                _live = live;
            }

            // 필터/정렬 뷰: 삭제됐거나 사라진(추가를 되돌린) 행은 즉시 뺀다. 새로 보여야 할 행은 재평가가 채운다.
            if (_viewMap is { } map)
            {
                int total = Edits.TotalRowIds(Edits.BaseRowCount >= 0 ? Edits.BaseRowCount : BaseRowCount);
                var kept = new List<int>(map.Length);
                foreach (int id in map)
                    if (id < total && !Edits.IsDeleted(id)) kept.Add(id);
                if (kept.Count != map.Length) _viewMap = kept.ToArray();
            }
        }

        /// <summary>
        /// 편집 내용을 새 파일로 저장(원본은 절대 덮어쓰지 않는다). 편집되지 않은 행은 원본 바이트를 그대로 복사하고,
        /// 편집된 행만 같은 인코딩·구분자·줄바꿈으로 다시 쓴다(값은 문자열 그대로 — 선행 0 보존).
        /// 이름을 바꾸거나 컬럼을 추가했으면 헤더 레코드를 다시 쓰고(BOM·줄바꿈 보존), 컬럼을 추가했으면 모든 행을 같은 너비로 다시 쓰고, 삭제한 행은 건너뛰며,
        /// 추가한 행은 화면 순서대로 같은 줄바꿈으로 쓴다.
        /// 인덱싱이 끝난 뒤에만 호출한다. 실패·취소 시 부분 파일을 남기지 않는다.
        /// </summary>
        public void SaveWithEdits(string destinationPath, IProgress<int>? progress, CancellationToken ct)
        {
            if (!IndexingComplete) throw new InvalidOperationException("Indexing is not complete.");
            string full = Path.GetFullPath(destinationPath);
            if (string.Equals(full, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The original file is never overwritten. Choose a different file name.");

            string tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var src = (IRandomByteSource?)_ramBuffer ?? _diskSource;
                    long count = _index.Count;
                    int baseRows = BaseRowCount;
                    var chunk = new byte[1 << 20];
                    long first = count > 1 ? _index[1] : FileLength; // 헤더(+BOM) 끝
                    char delim = (char)_delim;

                    // 헤더 레코드의 줄바꿈 종류를 알아 둔다(추가 행·구분용). 없으면 CRLF.
                    int headerLen = (int)Math.Max(0, first - _headerStart);
                    var headerRaw = new byte[headerLen];
                    if (headerLen > 0) src.Read(_headerStart, headerRaw);
                    int headerBody = headerLen;
                    while (headerBody > 0 && (headerRaw[headerBody - 1] == 0x0A || headerRaw[headerBody - 1] == 0x0D)) headerBody--;
                    string term = headerLen - headerBody == 0 ? "\r\n"
                        : headerRaw[headerLen - 1] == 0x0A ? (headerLen - headerBody >= 2 && headerRaw[headerLen - 2] == 0x0D ? "\r\n" : "\n")
                        : "\r";
                    byte[] termBytes = _encoding.GetBytes(term);

                    bool pendingSeparator;
                    bool widened = Edits.HasAppendedColumns || Edits.HasDeletedColumns || Edits.HasColumnOrder; // 컬럼이 추가·삭제·이동되면 모든 레코드가 새 너비·순서로 다시 직렬화되어야 한다
                    if (Edits.HeaderEditCount > 0 || widened)
                    {
                        CopyRange(src, fs, 0, _headerStart, chunk, ct); // BOM
                        byte[] body = _encoding.GetBytes(CellEdits.JoinRecord(_header, delim));
                        fs.Write(body, 0, body.Length);
                        fs.Write(headerRaw, headerBody, headerLen - headerBody);
                        pendingSeparator = headerLen > 0 && headerBody == headerLen;
                    }
                    else
                    {
                        CopyRange(src, fs, 0, first, chunk, ct);
                        pendingSeparator = headerLen > 0 && headerBody == headerLen; // 헤더만 있고 줄바꿈 없는 파일
                    }

                    int[]? live = _live;
                    long total = live?.Length ?? Math.Max(0, count - 1);
                    for (long i = 0; i < total; i++)
                    {
                        if ((i & 0x3FFF) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            progress?.Report((int)(i * 100 / Math.Max(1, total)));
                        }
                        int rowId = live is null ? (int)i : live[i];
                        if (pendingSeparator) { fs.Write(termBytes, 0, termBytes.Length); pendingSeparator = false; }

                        if (rowId >= baseRows)
                        {
                            // 추가 행: 새로 직렬화 + 파일의 줄바꿈.
                            byte[] added = _encoding.GetBytes(CellEdits.JoinRecord(ParseDataRow(rowId), delim));
                            fs.Write(added, 0, added.Length);
                            fs.Write(termBytes, 0, termBytes.Length);
                            continue;
                        }

                        long rec = rowId + 1L;
                        long start = _index[rec];
                        long end = rec + 1 < count ? _index[rec + 1] : FileLength;
                        bool isLastRecord = rec + 1 >= count;
                        if (widened || Edits.HasRowEdits(rowId))
                        {
                            // 원본 레코드의 끝 줄바꿈을 보존하고, 필드만 다시 직렬화한다.
                            int len = (int)(end - start);
                            var raw = new byte[len];
                            src.Read(start, raw);
                            int n = len;
                            while (n > 0 && (raw[n - 1] == 0x0A || raw[n - 1] == 0x0D)) n--;
                            byte[] body = _encoding.GetBytes(CellEdits.JoinRecord(ParseDataRow(rowId), delim));
                            fs.Write(body, 0, body.Length);
                            fs.Write(raw, n, len - n);
                            if (n == len) pendingSeparator = true; // 줄바꿈 없는 마지막 레코드 — 뒤에 무엇이 오면 구분 필요
                        }
                        else
                        {
                            CopyRange(src, fs, start, end, chunk, ct);
                            if (isLastRecord && end > start)
                            {
                                var last = new byte[1];
                                src.Read(end - 1, last);
                                if (last[0] != 0x0A && last[0] != 0x0D) pendingSeparator = true;
                            }
                        }
                    }
                }
                File.Move(tmp, full, overwrite: true);
                progress?.Report(100);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 부분 파일은 남기지 않는다. */ }
                throw;
            }
        }

        /// <summary>
        /// 현재 데이터(헤더 이름 변경·셀 편집·삭제/추가 행 반영, 값은 전부 문자열)를 단일 시트 .xlsx로 저장한다.
        /// 원본(또는 임포트 임시 CSV)은 덮어쓰지 않는다. 실패·취소 시 부분 파일을 남기지 않는다.
        /// </summary>
        public void SaveAsXlsx(string destinationPath, string sheetName, IProgress<int>? progress, CancellationToken ct)
        {
            if (!IndexingComplete) throw new InvalidOperationException("Indexing is not complete.");
            string full = Path.GetFullPath(destinationPath);
            if (string.Equals(full, Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The original file is never overwritten. Choose a different file name.");

            string tmp = full + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                int total = DataRowsAvailable;
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
                    XlsxDataWriter.Write(fs, sheetName, EnumerateExportRows(total, progress, ct), (long)total + 1);
                File.Move(tmp, full, overwrite: true);
                progress?.Report(100);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 부분 파일은 남기지 않는다. */ }
                throw;
            }
        }

        private IEnumerable<string[]> EnumerateExportRows(int total, IProgress<int>? progress, CancellationToken ct)
        {
            yield return _header;
            for (int i = 0; i < total; i++)
            {
                if ((i & 0x3FFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report((int)(i * 100L / Math.Max(1, total)));
                }
                yield return GetDataRowUncached(i);
            }
        }

        private static void CopyRange(IRandomByteSource src, Stream dst, long from, long to, byte[] chunk, CancellationToken ct)
        {
            for (long pos = from; pos < to;)
            {
                ct.ThrowIfCancellationRequested();
                int take = (int)Math.Min(chunk.Length, to - pos);
                src.Read(pos, chunk.AsSpan(0, take));
                dst.Write(chunk, 0, take);
                pos += take;
            }
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
            _rawHeader = DecodeAndParse(_headerStart, _headerEnd);
            _header = Edits.ApplyHeader(_rawHeader);
        }

        // ---- 필터 / 정렬 (Phase 3) : 인덱싱 완료 후에만 호출 ----
        // 뷰맵은 "행 id" 배열이다(행 삽입·삭제로 위치가 밀려도 id는 그대로). 계산은 모두 새 배열을 만든 뒤 한 번에 교체한다.

        /// <summary>predicate가 참인 데이터 행만 남기는 뷰맵을 백그라운드로 구성.</summary>
        public Task ApplyFilterAsync(Func<string[], bool> predicate, IProgress<int>? progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                _viewMap = ComputeFilter(predicate, progress, ct);
                progress?.Report(100);
            }, ct);
        }

        private int[] ComputeFilter(Func<string[], bool> predicate, IProgress<int>? progress, CancellationToken ct)
        {
            int[]? live = _live; // 스냅샷
            int total = live?.Length ?? BaseRowCount;
            var matches = new List<int>();
            for (int i = 0; i < total; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                int id = live is null ? i : live[i];
                if (predicate(ParseDataRow(id))) matches.Add(id);
                if (progress is not null && (i & 0x3FFFF) == 0)
                    progress.Report(total == 0 ? 100 : (int)(i * 100L / total));
            }
            return matches.ToArray();
        }

        /// <summary>
        /// 현재 뷰(이미 필터된 결과, 없으면 전체)만 새 조건으로 더 좁힘(증분 AND).
        /// 전체 데이터를 다시 스캔하지 않고 현재 표시 행만 평가하므로 2중·3중 필터가 빠름. 정렬 순서는 유지.
        /// </summary>
        public Task FilterWithinViewAsync(Func<string[], bool> predicate, IProgress<int>? progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                int[] baseMap = _viewMap ?? LiveIds();
                var result = new List<int>(Math.Min(baseMap.Length, 1 << 16));
                for (int i = 0; i < baseMap.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    int id = baseMap[i];
                    if (predicate(ParseDataRow(id))) result.Add(id);
                    if (progress is not null && (i & 0x3FFFF) == 0 && baseMap.Length > 0)
                        progress.Report((int)(i * 100L / baseMap.Length));
                }
                _viewMap = result.ToArray();
                progress?.Report(100);
            }, ct);
        }

        /// <summary>
        /// 활성 필터(없으면 null)와 정렬 키(없으면 빈 목록)를 처음부터 다시 평가해 뷰맵을 한 번에 교체한다.
        /// 셀 편집·행 삽입/삭제 뒤 필터/정렬을 현재 데이터로 재평가하는 데 쓴다(중간에 전체 보기로 깜빡이지 않음).
        /// 둘 다 없으면 뷰맵을 비운다.
        /// </summary>
        public Task RebuildViewAsync(Func<string[], bool>? predicate, IReadOnlyList<SortKey> sortKeys,
            IProgress<int>? progress, CancellationToken ct)
        {
            var keys = sortKeys.ToArray();
            return Task.Run(() =>
            {
                int[]? map = predicate is null ? null : ComputeFilter(predicate, progress, ct);
                if (keys.Length > 0) map = ComputeSort(map ?? LiveIds(), keys, progress, ct);
                _viewMap = map;
                progress?.Report(100);
            }, ct);
        }

        /// <summary>현재 뷰를 원래(파일) 순서로 즉시 되돌림(행 재읽기 없음). 필터 결과는 유지.</summary>
        public void ResetViewOrder()
        {
            if (_viewMap is not { } map) return;
            var rank = _rank;
            if (rank is null) { Array.Sort(map); return; } // 행 id 오름차순 = 파일 순서
            var order = new int[map.Length];
            for (int i = 0; i < map.Length; i++) order[i] = (uint)map[i] < (uint)rank.Length ? rank[map[i]] : int.MaxValue;
            Array.Sort(order, map); // 행 구조 편집이 있으면 화면 순서(위치) 기준
        }

        /// <summary>현재 뷰(필터 결과 또는 전체)를 단일 컬럼 기준으로 정렬(다중 키 버전의 편의 오버로드).</summary>
        public Task SortAsync(int column, bool ascending, IProgress<int>? progress, CancellationToken ct)
            => SortAsync(new[] { new SortKey(column, ascending) }, progress, ct);

        /// <summary>
        /// 현재 뷰를 여러 컬럼 기준으로 정렬한 뷰맵 구성. sortKeys 순서가 우선순위(앞이 1차).
        /// 각 컬럼 키는 1회만 추출하고 숫자 여부도 미리 판정하며, 동률은 파일 순서로 안정화한다.
        /// </summary>
        public Task SortAsync(IReadOnlyList<SortKey> sortKeys, IProgress<int>? progress, CancellationToken ct)
        {
            // 호출 스레드에서 스냅샷(공유 컬렉션 변경과 분리).
            var keys = sortKeys.ToArray();
            return Task.Run(() =>
            {
                if (keys.Length == 0) { progress?.Report(100); return; }
                _viewMap = ComputeSort(_viewMap ?? LiveIds(), keys, progress, ct);
                progress?.Report(100);
            }, ct);
        }

        private int[] ComputeSort(int[] baseMap, SortKey[] sortKeys, IProgress<int>? progress, CancellationToken ct)
        {
            int k = sortKeys.Length;
            var cols = new int[k];
            var asc = new bool[k];
            for (int j = 0; j < k; j++) { cols[j] = sortKeys[j].Column; asc[j] = sortKeys[j].Ascending; }
            if (k == 0) return baseMap;

            int n = baseMap.Length;

            // 컬럼별 키/숫자값을 한 번만 추출(캐시 우회) + 숫자 여부 1회 판정.
            var keys = new string[k][];
            var num = new double[k][];
            var allNumeric = new bool[k];
            for (int j = 0; j < k; j++) { keys[j] = new string[n]; num[j] = new double[n]; allNumeric[j] = n > 0; }

            for (int i = 0; i < n; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                string[] row = ParseDataRow(baseMap[i]);
                for (int j = 0; j < k; j++)
                {
                    int col = cols[j];
                    string key = (col >= 0 && col < row.Length) ? row[col] : string.Empty;
                    keys[j][i] = key;
                    if (allNumeric[j])
                    {
                        if (double.TryParse(key, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) num[j][i] = d;
                        else if (key.Length == 0) num[j][i] = double.NegativeInfinity; // 빈 값은 맨 앞
                        else allNumeric[j] = false; // 숫자 아님 → 해당 컬럼은 문자열 비교
                    }
                }
                if (progress is not null && (i & 0x3FFFF) == 0 && n > 0)
                    progress.Report((int)(i * 100L / n));
            }

            // idx를 다중 키 우선순위로 정렬 후 baseMap을 재배열. 모든 키가 같으면 화면(파일) 순서로 안정화한다.
            int[] idx = CreateIdentity(n);
            var rank = _rank;
            Array.Sort(idx, (x, y) =>
            {
                for (int j = 0; j < k; j++)
                {
                    int c = allNumeric[j]
                        ? num[j][x].CompareTo(num[j][y])
                        : string.Compare(keys[j][x], keys[j][y], StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return asc[j] ? c : -c;
                }
                int bx = baseMap[x], by = baseMap[y];
                if (rank is not null && (uint)bx < (uint)rank.Length && (uint)by < (uint)rank.Length)
                    return rank[bx].CompareTo(rank[by]); // 행 구조 편집이 있으면 화면 순서
                return bx.CompareTo(by); // 안정 정렬 tie-break
            });
            var result = new int[n];
            for (int i = 0; i < n; i++) result[i] = baseMap[idx[i]];
            return result;
        }

        private static int[] CreateIdentity(int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = i;
            return a;
        }

        public void ClearView() => _viewMap = null;

        /// <summary>
        /// 한 컬럼의 고유값과 개수를 수집(헤더 필터·범주 선택용). 개수 내림차순→값 오름차순 정렬.
        /// 빈 값은 ""로 보존한다. 캐시를 오염시키지 않도록 uncached 경로로 스캔하며 취소 가능.
        /// </summary>
        public IReadOnlyList<(string Value, int Count)> DistinctValues(int column, bool withinCurrentView, CancellationToken ct,
            int maxDistinctValues = int.MaxValue, long maxValueCharacters = long.MaxValue)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            long characters = 0;
            ct.ThrowIfCancellationRequested();

            void Tally(string[] row)
            {
                string v = column >= 0 && column < row.Length ? row[column] : string.Empty;
                if (counts.TryGetValue(v, out int c)) counts[v] = c + 1;
                else
                {
                    if (counts.Count >= maxDistinctValues || v.Length > maxValueCharacters - characters)
                        throw new DistinctValueLimitException();
                    counts.Add(v, 1);
                    characters += v.Length;
                }
            }

            if (withinCurrentView && _viewMap is { } map)
            {
                for (int i = 0; i < map.Length; i++)
                {
                    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    Tally(ParseDataRow(map[i]));
                }
            }
            else
            {
                int total = DataRowsAvailable;
                for (int i = 0; i < total; i++)
                {
                    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    Tally(GetDataRowUncached(i));
                }
            }

            return counts
                .Select(kv => (kv.Key, kv.Value))
                .OrderByDescending(t => t.Value)
                .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

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
                if (r == 0)
                    throw new EndOfStreamException("파일이 예상보다 짧습니다(인덱싱 중 외부에서 변경되었을 수 있음).");
                total += r;
            }
        }
    }
}
