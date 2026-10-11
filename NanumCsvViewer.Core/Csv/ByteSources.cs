using System.IO;
using Microsoft.Win32.SafeHandles;

namespace NanumCsvViewer.Csv
{
    /// <summary>지정 오프셋의 바이트를 무작위로 읽는 소스. 디스크 또는 RAM 백엔드.</summary>
    public interface IRandomByteSource : IDisposable
    {
        long Length { get; }
        /// <summary>[offset, offset+dest.Length) 구간을 dest에 가득 채워 읽습니다. 호출자가 범위를 보장합니다.</summary>
        void Read(long offset, Span<byte> dest);
    }

    /// <summary>디스크 기반. RandomAccess는 위치 지정 읽기라 여러 스레드에서 동시 호출해도 안전합니다.</summary>
    public sealed class FileByteSource : IRandomByteSource
    {
        private readonly SafeFileHandle _handle;
        public long Length { get; }

        public FileByteSource(string path)
        {
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(_handle);
        }

        public void Read(long offset, Span<byte> dest)
        {
            int total = 0;
            while (total < dest.Length)
            {
                int r = RandomAccess.Read(_handle, dest.Slice(total), offset + total);
                // 정상 범위면 EOF가 나오지 않는다. 발생했다면 외부에서 파일이 잘렸다는 뜻 → 무음 수용 대신 표면화.
                if (r == 0)
                    throw new EndOfStreamException("파일이 예상보다 짧습니다(외부에서 변경되었을 수 있음).");
                total += r;
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    /// <summary>
    /// 파일 전체를 고정 크기 청크 배열로 RAM에 보관(적응형 메모리 모드).
    /// 단일 byte[]는 약 2GB 제한이 있어, 청크로 나눠 2GB 초과 파일도 담을 수 있게 합니다.
    /// </summary>
    public sealed class MemoryFileBuffer : IRandomByteSource
    {
        public const int ChunkBits = 24;            // 16 MB
        public const int ChunkSize = 1 << ChunkBits;
        private const int ChunkMask = ChunkSize - 1;

        private readonly byte[][] _chunks;
        public long Length { get; }

        public MemoryFileBuffer(long length)
        {
            Length = length;
            int chunkCount = (int)((length + ChunkSize - 1) / ChunkSize);
            _chunks = new byte[Math.Max(chunkCount, 0)][];
        }

        /// <summary>순차 패스에서 chunkIndex번째(16MB 단위) 청크를 그대로 보관.</summary>
        public void SetChunk(int chunkIndex, byte[] data) => _chunks[chunkIndex] = data;

        public void Read(long offset, Span<byte> dest)
        {
            int destPos = 0;
            long pos = offset;
            while (destPos < dest.Length)
            {
                int chunk = (int)(pos >> ChunkBits);
                int within = (int)(pos & ChunkMask);
                byte[] src = _chunks[chunk];
                int available = src.Length - within;
                int toCopy = Math.Min(available, dest.Length - destPos);
                src.AsSpan(within, toCopy).CopyTo(dest.Slice(destPos));
                destPos += toCopy;
                pos += toCopy;
            }
        }

        public void Dispose() { /* GC가 청크를 회수 */ }
    }
}
