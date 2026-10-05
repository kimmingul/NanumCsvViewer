using System.Security.Cryptography;
using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Workspace
{
    /// <summary>UTF-8이 아닌 CSV(CP949·EUC-KR·UTF-16/32)를 DuckDB가 읽을 수 있게 UTF-8 임시 사본으로 바꾼다. 원본은 읽기만 한다.</summary>
    internal static class CsvUtf8Copy
    {
        public readonly record struct Result(string ReadPath, string EncodingName, bool IsCopy);

        /// <summary>
        /// 인코딩(없으면 앱의 <see cref="EncodingDetector"/>로 감지)을 정하고, UTF-8이면 원본 경로를, 아니면 UTF-8 사본 경로를 돌려준다.
        /// 사본은 (경로, 수정 시각, 크기, 인코딩)이 같으면 재사용한다. 변환은 앱이 쓰는 같은 <see cref="Encoding"/> 객체로 글자 단위로 옮기므로
        /// 따옴표·줄바꿈·구분자는 한 바이트도 바뀌지 않고 BOM만 사라진다.
        /// </summary>
        public static Result Prepare(string path, string? encodingName, string cacheDir, IProgress<int>? progress, CancellationToken ct)
        {
            if (new FileInfo(path).Length == 0)
                throw new WorkspaceQueryException(ViewerSupport.LT($"The file is empty: {path}", $"빈 파일입니다: {path}"));
            Encoding enc;
            string display;
            if (string.IsNullOrWhiteSpace(encodingName))
            {
                var det = EncodingDetector.Detect(path);
                enc = det.Encoding; display = det.DisplayName;
            }
            else
            {
                display = encodingName;
                enc = ResolveEncoding(encodingName, path);
            }

            if (enc.CodePage == 65001) return new Result(path, display, false);

            var info = new FileInfo(path);
            string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
                $"{Path.GetFullPath(path).ToLowerInvariant()}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{display}"))).ToLowerInvariant();
            Directory.CreateDirectory(cacheDir);
            string copy = Path.Combine(cacheDir, key + ".csv");
            if (File.Exists(copy))
            {
                try { File.SetLastWriteTimeUtc(copy, DateTime.UtcNow); } catch (IOException) { /* 사용 중이면 시각 갱신 생략 */ }
                return new Result(copy, display, true);
            }

            string tmp = copy + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                using (var reader = new StreamReader(input, enc, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16))
                using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16))
                {
                    var buffer = new char[1 << 16];
                    long length = Math.Max(1, input.Length);
                    int lastPercent = -1, read;
                    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        writer.Write(buffer, 0, read);
                        int percent = (int)Math.Min(99, input.Position * 100 / length);
                        if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                    }
                }
                try { File.Move(tmp, copy); }
                catch (IOException) when (File.Exists(copy)) { /* 다른 스레드가 먼저 만들었다 — 그 사본을 쓴다. */ }
                progress?.Report(100);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            }
            return new Result(copy, display, true);
        }

        private static Encoding ResolveEncoding(string name, string path)
        {
            if (name == EncodingDetector.Cp949) return Encoding.GetEncoding(949);
            if (name == EncodingDetector.Utf8 || name == EncodingDetector.Utf8Bom) return new UTF8Encoding(false);
            // 앱 선택 목록에 없는 이름(UTF-16 LE 등)은 .NET 인코딩 이름으로 시도하고, 안 되면 감지 결과를 쓴다.
            try { return Encoding.GetEncoding(name); }
            catch (ArgumentException) { return EncodingDetector.Detect(path).Encoding; }
        }

        /// <summary>오래 안 쓴 사본(기본 7일)을 지운다. 사용 중이거나 지울 수 없는 파일은 건너뛴다.</summary>
        public static void Sweep(string cacheDir, TimeSpan maxAge)
        {
            try
            {
                if (!Directory.Exists(cacheDir)) return;
                foreach (string f in Directory.EnumerateFiles(cacheDir))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > maxAge) File.Delete(f);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
