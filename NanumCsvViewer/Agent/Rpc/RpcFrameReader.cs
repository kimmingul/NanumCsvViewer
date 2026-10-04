using System.Text;
using System.Text.Json;

namespace NanumCsvViewer.Agent.Rpc
{
    internal delegate void RpcLineHandler(ReadOnlySpan<byte> line);

    /// <summary>
    /// stdout 바이트 → JSONL 줄. LF로 자르고 CR은 떼며 빈 줄은 건너뛴다.
    /// 상한(기본 1 MiB)을 넘는 줄은 통째로 버리고(다음 LF까지 건너뜀) onDropped(버린 바이트 수)를 한 번 부른다.
    /// </summary>
    internal sealed class RpcLineSplitter
    {
        private readonly int _maxLineBytes;
        private byte[] _buffer = new byte[8192];
        private int _length;
        private bool _skipping;
        private long _skipped;

        public RpcLineSplitter(int maxLineBytes = RpcProtocol.MaxFrameBytes) => _maxLineBytes = maxLineBytes;

        public void Feed(ReadOnlySpan<byte> data, RpcLineHandler onLine, Action<long> onDropped)
        {
            while (!data.IsEmpty)
            {
                int lf = data.IndexOf((byte)'\n');
                ReadOnlySpan<byte> part = lf < 0 ? data : data[..lf];
                data = lf < 0 ? default : data[(lf + 1)..];

                if (_skipping)
                {
                    _skipped += part.Length;
                    if (lf >= 0) { _skipping = false; onDropped(_skipped); _skipped = 0; }
                    continue;
                }
                if ((long)_length + part.Length > _maxLineBytes)
                {
                    _skipped = (long)_length + part.Length;
                    _length = 0;
                    if (lf >= 0) { onDropped(_skipped); _skipped = 0; }
                    else _skipping = true;
                    continue;
                }
                Append(part);
                if (lf >= 0) Emit(onLine);
            }
        }

        /// <summary>EOF: 개행 없이 끝난 마지막 줄을 내보낸다.</summary>
        public void Complete(RpcLineHandler onLine, Action<long> onDropped)
        {
            if (_skipping) { _skipping = false; onDropped(_skipped); _skipped = 0; _length = 0; return; }
            if (_length > 0) Emit(onLine);
        }

        private void Append(ReadOnlySpan<byte> part)
        {
            if (part.IsEmpty) return;
            if (_length + part.Length > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + part.Length));
            part.CopyTo(_buffer.AsSpan(_length));
            _length += part.Length;
        }

        private void Emit(RpcLineHandler onLine)
        {
            int n = _length;
            _length = 0;
            if (n > 0 && _buffer[n - 1] == (byte)'\r') n--;
            if (n == 0) return;
            onLine(_buffer.AsSpan(0, n));
        }
    }

    internal enum ChunkStatus { NeedMore, Complete, Error }

    internal readonly record struct ChunkResult(ChunkStatus Status, byte[]? Payload = null, string? Error = null);

    /// <summary>
    /// v2 rpc_chunk 복원기. 한 번에 한 시퀀스만 허용: chunkId/index/count/byteLength 검증, 끼어듦·중단·순서 오류·상한(64 MiB)
    /// 초과·길이 불일치는 시퀀스를 버리고 Error. 완료되면 엄격한 UTF-8 디코딩 전의 원본 바이트를 돌려준다.
    /// </summary>
    internal sealed class RpcChunkAssembler
    {
        private readonly long _maxBytes;
        private string? _chunkId;
        private int _count;
        private long _byteLength;
        private int _next;
        private MemoryStream? _data;

        public RpcChunkAssembler(long maxReassembledBytes = RpcProtocol.MaxReassembledFrameBytes) => _maxBytes = maxReassembledBytes;

        public bool Active => _chunkId != null;

        /// <summary>rpc_chunk가 아닌 프레임이 왔다. 시퀀스 도중이면 중단된 것이다.</summary>
        public string? NoteOtherFrame()
        {
            if (_chunkId == null) return null;
            string id = _chunkId;
            int got = _next;
            Reset();
            return $"rpc_chunk {id}: interrupted by another frame after {got} chunk(s)";
        }

        public ChunkResult Accept(JsonElement chunk)
        {
            if (chunk.ValueKind != JsonValueKind.Object) return Fail("rpc_chunk is not an object");
            if (!chunk.TryGetProperty("chunkId", out var idEl) || idEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(idEl.GetString()))
                return Fail("rpc_chunk: missing chunkId");
            string id = idEl.GetString()!;
            if (!TryInt(chunk, "index", out long index) || index < 0) return Fail($"rpc_chunk {id}: bad index");
            if (!TryInt(chunk, "count", out long count) || count < 1 || count > int.MaxValue) return Fail($"rpc_chunk {id}: bad count");
            if (!TryInt(chunk, "byteLength", out long byteLength) || byteLength < 0) return Fail($"rpc_chunk {id}: bad byteLength");
            if (!chunk.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
                return Fail($"rpc_chunk {id}: missing data");

            if (_chunkId == null)
            {
                if (index != 0) return Fail($"rpc_chunk {id}: sequence does not start at index 0 (got {index})");
                if (byteLength > _maxBytes)
                    return Fail($"rpc_chunk {id}: {byteLength} bytes exceeds the {_maxBytes} byte limit");
                _chunkId = id;
                _count = (int)count;
                _byteLength = byteLength;
                _next = 0;
                _data = new MemoryStream((int)Math.Min(byteLength, 4 << 20));
            }
            else
            {
                if (id != _chunkId) return Fail($"rpc_chunk {id}: interleaved with {_chunkId}");
                if (count != _count || byteLength != _byteLength) return Fail($"rpc_chunk {id}: count/byteLength changed mid-sequence");
                if (index != _next) return Fail($"rpc_chunk {id}: out of order (expected {_next}, got {index})");
            }

            string b64 = dataEl.GetString() ?? "";
            byte[] decoded;
            try { decoded = Convert.FromBase64String(b64); }
            catch (FormatException) { return Fail($"rpc_chunk {id}: invalid base64 at index {index}"); }
            if (_data!.Length + decoded.Length > _byteLength) return Fail($"rpc_chunk {id}: more data than byteLength");
            _data.Write(decoded, 0, decoded.Length);
            _next++;

            if (_next < _count) return new ChunkResult(ChunkStatus.NeedMore);
            if (_data.Length != _byteLength) return Fail($"rpc_chunk {id}: byteLength mismatch ({_data.Length} of {_byteLength})");
            byte[] payload = _data.ToArray();
            Reset();
            return new ChunkResult(ChunkStatus.Complete, payload);
        }

        /// <summary>복원된 바이트를 엄격 UTF-8로 디코딩(잘못된 바이트면 null).</summary>
        public static string? DecodeStrict(byte[] payload)
        {
            try { return new UTF8Encoding(false, true).GetString(payload); }
            catch (DecoderFallbackException) { return null; }
        }

        private static bool TryInt(JsonElement e, string name, out long value)
        {
            value = 0;
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value);
        }

        private ChunkResult Fail(string message)
        {
            Reset();
            return new ChunkResult(ChunkStatus.Error, Error: message);
        }

        private void Reset()
        {
            _chunkId = null;
            _count = 0;
            _byteLength = 0;
            _next = 0;
            _data = null;
        }
    }
}
