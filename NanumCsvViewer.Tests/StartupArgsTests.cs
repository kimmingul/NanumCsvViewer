namespace NanumCsvViewer.Tests
{
    // 탐색기 "연결 프로그램"으로 실행 시 전달되는 인수에서 열 파일을 찾는지 검증(설치본에서 파일이 열리지 않던 회귀).
    public sealed class StartupArgsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncv_args_" + Guid.NewGuid().ToString("N"), "my data");
        private readonly string _file;

        public StartupArgsTests()
        {
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "환자 목록.csv");
            File.WriteAllText(_file, "a,b\n1,2\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
        }

        [Fact]
        public void No_arguments_opens_nothing()
        {
            Assert.Null(StartupArgs.ResolveFilePath(Array.Empty<string>()));
        }

        [Fact]
        public void Quoted_path_with_spaces_resolves()
        {
            Assert.Equal(_file, StartupArgs.ResolveFilePath(new[] { _file }));
        }

        [Fact]
        public void Unquoted_association_command_split_on_spaces_is_rejoined()
        {
            // 연결 명령이 %1을 따옴표로 감싸지 않으면 공백마다 인수가 쪼개진다.
            string[] split = _file.Split(' ');
            Assert.True(split.Length > 1);
            Assert.Equal(_file, StartupArgs.ResolveFilePath(split));
        }

        [Fact]
        public void File_uri_resolves_to_local_path()
        {
            Assert.Equal(_file, StartupArgs.ResolveFilePath(new[] { new Uri(_file).AbsoluteUri }));
        }

        [Fact]
        public void Relative_path_resolves_against_base_directory()
        {
            Assert.Equal(_file, StartupArgs.ResolveFilePath(new[] { Path.GetFileName(_file) }, _dir));
        }

        [Fact]
        public void Missing_file_and_switches_are_ignored()
        {
            Assert.Null(StartupArgs.ResolveFilePath(new[] { Path.Combine(_dir, "none.csv") }));
            Assert.Null(StartupArgs.ResolveFilePath(new[] { "--flag", "<|>" }));
        }
    }
}
