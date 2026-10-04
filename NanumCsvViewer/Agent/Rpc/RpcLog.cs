using System.Text;

namespace NanumCsvViewer.Agent.Rpc
{
    /// <summary>RPC 프레임 기록(진단용). 모든 프레임을 "&lt; 받음"/"&gt; 보냄"으로 남긴다.</summary>
    internal interface IRpcLog
    {
        void Header(string text);
        void Received(string line);
        void Sent(string line);
        void Note(string text);
    }

    internal sealed class NullRpcLog : IRpcLog
    {
        public static readonly NullRpcLog Instance = new();
        public void Header(string text) { }
        public void Received(string line) { }
        public void Sent(string line) { }
        public void Note(string text) { }
    }

    /// <summary>%TEMP%\NanumCsvViewer\rpc.log. 20 MB를 넘으면 rpc.1.log로 돌린다. 여러 인스턴스가 같은 파일에 덧붙인다.</summary>
    internal sealed class FileRpcLog : IRpcLog, IDisposable
    {
        public const long RotateBytes = 20L * 1024 * 1024;
        /// <summary>한 줄 기록 상한: 거대한 프레임은 앞부분만 남기고 길이를 적는다.</summary>
        private const int MaxLoggedChars = 64 * 1024;

        private readonly object _gate = new();
        private readonly string _path;
        private readonly long _rotateBytes;
        private FileStream? _file;
        private bool _broken;

        public FileRpcLog(string? path = null, long rotateBytes = RotateBytes)
        {
            _path = path ?? Path.Combine(OmpLaunch.TempDirectory, "rpc.log");
            _rotateBytes = rotateBytes;
        }

        public string FilePath => _path;

        public void Header(string text) => Write("# " + text);
        public void Received(string line) => Write("< " + Clip(line));
        public void Sent(string line) => Write("> " + Clip(line));
        public void Note(string text) => Write("! " + text);

        private static string Clip(string line) =>
            line.Length <= MaxLoggedChars ? line : line[..MaxLoggedChars] + $"…[{line.Length} chars]";

        private void Write(string text)
        {
            lock (_gate)
            {
                if (_broken) return;
                try
                {
                    Open();
                    if (_file!.Length >= _rotateBytes) Rotate();
                    byte[] bytes = Encoding.UTF8.GetBytes(DateTime.Now.ToString("HH:mm:ss.fff ") + text + "\n");
                    _file!.Write(bytes, 0, bytes.Length);
                    _file.Flush();
                }
                catch { _broken = true; CloseQuietly(); }
            }
        }

        private void Open()
        {
            if (_file != null) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            _file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        private void Rotate()
        {
            CloseQuietly();
            string old = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!,
                System.IO.Path.GetFileNameWithoutExtension(_path) + ".1" + System.IO.Path.GetExtension(_path));
            try { File.Move(_path, old, overwrite: true); }
            catch { try { File.Delete(_path); } catch { } }
            Open();
        }

        private void CloseQuietly()
        {
            try { _file?.Dispose(); } catch { }
            _file = null;
        }

        public void Dispose()
        {
            lock (_gate) CloseQuietly();
        }
    }
}
