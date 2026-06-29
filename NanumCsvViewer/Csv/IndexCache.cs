using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NanumCsvViewer.Csv
{
    /// <summary>
    /// 레코드 오프셋 인덱스를 %LocalAppData%\NanumCsvViewer\index\ 에 영속화하여 재열기 시 인덱싱을 생략합니다.
    /// 파일 크기·최종수정시각·인코딩·구분자가 모두 일치할 때만 캐시를 사용합니다(불일치 시 재인덱싱).
    /// </summary>
    public static class IndexCache
    {
        private const uint Magic = 0x4E435649; // "NCVI"
        private const int Version = 1;

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NanumCsvViewer", "index");

        private static string PathFor(string csvPath)
        {
            string full = Path.GetFullPath(csvPath);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
            return Path.Combine(Dir, Convert.ToHexString(hash, 0, 12) + ".ncvidx");
        }

        /// <summary>너무 큰 인덱스는 캐시하지 않음(레코드 8바이트 × 이 개수 상한).</summary>
        public const long MaxRecords = 200_000_000; // 약 1.6 GB 캐시 상한

        public static void Save(string csvPath, long fileSize, DateTime lastWriteUtc,
            string encodingName, byte delimiter, RecordIndex index)
        {
            long count = index.Count;
            if (count <= 0 || count > MaxRecords) return;
            try
            {
                Directory.CreateDirectory(Dir);
                long[] offsets = index.SnapshotOffsets();
                using var fs = new FileStream(PathFor(csvPath), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                using var bw = new BinaryWriter(fs);
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write(fileSize);
                bw.Write(lastWriteUtc.ToBinary());
                bw.Write(encodingName);
                bw.Write(delimiter);
                bw.Write(offsets.LongLength);
                var bytes = new byte[offsets.Length * sizeof(long)];
                Buffer.BlockCopy(offsets, 0, bytes, 0, bytes.Length);
                bw.Write(bytes);
            }
            catch { /* 캐시 저장 실패는 무시 */ }
        }

        /// <summary>검증 통과 시 오프셋 배열을 반환, 아니면 null.</summary>
        public static long[]? TryLoad(string csvPath, long fileSize, DateTime lastWriteUtc,
            string encodingName, byte delimiter)
        {
            try
            {
                string path = PathFor(csvPath);
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt32() != Magic) return null;
                if (br.ReadInt32() != Version) return null;
                if (br.ReadInt64() != fileSize) return null;
                if (br.ReadInt64() != lastWriteUtc.ToBinary()) return null;
                if (br.ReadString() != encodingName) return null;
                if (br.ReadByte() != delimiter) return null;
                long count = br.ReadInt64();
                if (count < 0 || count > MaxRecords) return null;
                var bytes = br.ReadBytes(checked((int)(count * sizeof(long))));
                if (bytes.Length != count * sizeof(long)) return null;
                var offsets = new long[count];
                Buffer.BlockCopy(bytes, 0, offsets, 0, bytes.Length);
                return offsets;
            }
            catch { return null; }
        }

        public static void Clear()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch { /* 무시 */ }
        }

        /// <summary>특정 CSV 파일의 영속 인덱스만 삭제(닫을 때 정리용).</summary>
        public static void DeleteFor(string csvPath)
        {
            try { string p = PathFor(csvPath); if (File.Exists(p)) File.Delete(p); }
            catch { /* 무시 */ }
        }

        public static string FolderPath => Dir;
    }
}
