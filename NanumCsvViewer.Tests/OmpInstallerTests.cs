using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NanumCsvViewer.Agent.Rpc;
using Xunit;

namespace NanumCsvViewer.Tests
{
    public sealed class OmpInstallerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncv-omp-inst-" + Guid.NewGuid().ToString("N"));
        private string Target => Path.Combine(_dir, "install", "omp.exe");

        public OmpInstallerTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // ---- 도우미 ---------------------------------------------------------------------------------------------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        /// <summary>NTFS 대체 스트림(Zone.Identifier)을 만든다.</summary>
        private static void MarkFromInternet(string path)
        {
            using var h = CreateFileW(path + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero);
            Assert.False(h.IsInvalid, "cannot create an alternate data stream (NTFS required)");
            using var fs = new FileStream(h, FileAccess.Write);
            fs.Write(Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"));
        }

        private string WriteFile(string name, string content)
        {
            string p = Path.Combine(_dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            return p;
        }

        private static OmpInstallOptions Options(string target, string version = "omp/18.5.0", Func<IReadOnlyList<string>>? running = null,
            IUserPathStore? store = null, Action? broadcast = null, HttpMessageHandler? http = null, string arch = "x64", string? apiUrl = null,
            Func<string, OmpRunOutcome>? run = null) => new()
            {
                TargetPath = target,
                Korean = false,
                RunVersion = (p, _) => Task.FromResult(run?.Invoke(p) ?? OmpRunOutcome.Ran(version + "\n")),
                RunningPaths = running ?? (() => Array.Empty<string>()),
                PathStore = store ?? new FakePathStore(),
                BroadcastEnvironmentChange = broadcast ?? (() => { }),
                HttpHandler = http,
                Architecture = arch,
                ReleaseApiUrl = apiUrl ?? "https://api.github.com/repos/can1357/oh-my-pi/releases/latest",
                Expand = s => s.Replace("%LOCALAPPDATA%", @"C:\Users\u\AppData\Local", StringComparison.OrdinalIgnoreCase),
                StallTimeout = TimeSpan.FromSeconds(5),
            };

        private sealed class FakePathStore : IUserPathStore
        {
            public string? Value;
            public RegistryValueKind Kind = RegistryValueKind.ExpandString;
            public int Writes;
            public string? Read(out RegistryValueKind kind) { kind = Kind; return Value; }
            public void Write(string value, RegistryValueKind kind) { Value = value; Kind = kind; Writes++; }
        }

        private string[] LeftoverTempFiles() =>
            Directory.Exists(Path.GetDirectoryName(Target)!) ? Directory.GetFiles(Path.GetDirectoryName(Target)!, "omp.install-*") : Array.Empty<string>();

        // ---- 파일에서 설치 --------------------------------------------------------------------------------------

        [Fact]
        public async Task Install_copies_the_file_without_the_internet_mark_and_leaves_the_source_alone()
        {
            string src = WriteFile(@"Downloads\omp-windows-x64.exe", "binary-v1");
            MarkFromInternet(src);
            Assert.True(ZoneMark.Exists(src));

            var r = await OmpInstaller.InstallFromFileAsync(src, addToPath: false, options: Options(Target));

            Assert.True(r.Ok, r.Message);
            Assert.Equal(Target, r.InstalledPath);
            Assert.Equal(new OmpVersion(18, 5, 0), r.Version);
            Assert.Equal("binary-v1", File.ReadAllText(Target));
            Assert.False(ZoneMark.Exists(Target));
            Assert.True(ZoneMark.Exists(src));   // 원본은 건드리지 않는다
            Assert.Empty(LeftoverTempFiles());
        }

        [Fact]
        public async Task Install_over_an_existing_omp_replaces_it_and_a_running_one_is_refused()
        {
            string src = WriteFile("new.exe", "new");
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            File.WriteAllText(Target, "old");

            var busy = await OmpInstaller.InstallFromFileAsync(src, false, options: Options(Target, running: () => new[] { Target.ToUpperInvariant() }));
            Assert.Equal(OmpInstallError.InUse, busy.Error);
            Assert.Equal("old", File.ReadAllText(Target));
            Assert.Empty(LeftoverTempFiles());

            var ok = await OmpInstaller.InstallFromFileAsync(src, false, options: Options(Target));
            Assert.True(ok.Ok, ok.Message);
            Assert.Equal("new", File.ReadAllText(Target));
            Assert.Empty(LeftoverTempFiles());
        }

        [Fact]
        public async Task A_file_that_cannot_run_or_is_too_old_never_replaces_a_working_install()
        {
            string src = WriteFile("bad.exe", "bad");
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            File.WriteAllText(Target, "good");

            var blocked = await OmpInstaller.InstallFromFileAsync(src, false,
                options: Options(Target, run: _ => new OmpRunOutcome(null, null, false, 193, "not a valid Win32 application")));
            Assert.Equal(OmpInstallError.NotRunnable, blocked.Error);

            var old = await OmpInstaller.InstallFromFileAsync(src, false, options: Options(Target, version: "omp/18.4.3"));
            Assert.Equal(OmpInstallError.TooOld, old.Error);

            var notOmp = await OmpInstaller.InstallFromFileAsync(src, false, options: Options(Target, version: "hello"));
            Assert.Equal(OmpInstallError.NotRunnable, notOmp.Error);

            Assert.Equal("good", File.ReadAllText(Target));
            Assert.Empty(LeftoverTempFiles());
        }

        [Fact]
        public async Task Installing_the_file_that_is_already_in_place_only_unblocks_it()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            File.WriteAllText(Target, "inplace");
            MarkFromInternet(Target);

            var r = await OmpInstaller.InstallFromFileAsync(Target, false, options: Options(Target));
            Assert.True(r.Ok, r.Message);
            Assert.False(ZoneMark.Exists(Target));
            Assert.Equal("inplace", File.ReadAllText(Target));
        }

        [Fact]
        public async Task A_missing_source_is_an_error_not_an_exception()
        {
            var r = await OmpInstaller.InstallFromFileAsync(Path.Combine(_dir, "nope.exe"), false, options: Options(Target));
            Assert.False(r.Ok);
            Assert.Equal(OmpInstallError.Io, r.Error);
        }

        // ---- 사용자 PATH ----------------------------------------------------------------------------------------

        [Fact]
        public void Adding_to_the_user_path_is_idempotent_and_keeps_the_value_kind()
        {
            var store = new FakePathStore { Value = @"C:\Windows;%USERPROFILE%\bin", Kind = RegistryValueKind.ExpandString };
            int broadcasts = 0;
            var options = Options(Target, store: store, broadcast: () => broadcasts++);
            string dir = @"C:\Users\u\AppData\Local\omp";

            Assert.True(OmpInstaller.AddToUserPath(dir, options));
            Assert.Equal(@"C:\Windows;%USERPROFILE%\bin;" + dir, store.Value);
            Assert.Equal(RegistryValueKind.ExpandString, store.Kind);
            Assert.Equal(1, broadcasts);

            // 같은 항목(대소문자·끝 '\'·%VAR% 확장)은 다시 넣지 않는다.
            Assert.False(OmpInstaller.AddToUserPath(dir.ToUpperInvariant() + "\\", options));
            Assert.False(OmpInstaller.AddToUserPath(dir, options));
            Assert.Equal(1, store.Writes);
            Assert.Equal(1, broadcasts);

            var viaVariable = new FakePathStore { Value = @"%LOCALAPPDATA%\omp" };
            Assert.False(OmpInstaller.AddToUserPath(dir, Options(Target, store: viaVariable)));
            Assert.Equal(0, viaVariable.Writes);
        }

        [Fact]
        public void Adding_to_an_empty_or_missing_user_path_creates_an_expandable_value()
        {
            var store = new FakePathStore { Value = null };
            Assert.True(OmpInstaller.AddToUserPath(@"C:\omp", Options(Target, store: store)));
            Assert.Equal(@"C:\omp", store.Value);
            Assert.Equal(RegistryValueKind.ExpandString, store.Kind);

            var plain = new FakePathStore { Value = @"C:\a;", Kind = RegistryValueKind.String };
            Assert.True(OmpInstaller.AddToUserPath(@"C:\omp", Options(Target, store: plain)));
            Assert.Equal(@"C:\a;C:\omp", plain.Value);
            Assert.Equal(RegistryValueKind.String, plain.Kind);
        }

        [Fact]
        public async Task Install_adds_the_folder_to_the_user_path_only_when_asked()
        {
            string src = WriteFile("a.exe", "a");
            var store = new FakePathStore { Value = @"C:\Windows" };

            var without = await OmpInstaller.InstallFromFileAsync(src, false, options: Options(Target, store: store));
            Assert.True(without.Ok);
            Assert.Equal(0, store.Writes);

            var with = await OmpInstaller.InstallFromFileAsync(src, true, options: Options(Target, store: store));
            Assert.True(with.Ok, with.Message);
            Assert.Equal(@"C:\Windows;" + Path.GetDirectoryName(Target), store.Value);
        }

        // ---- 릴리스 JSON·체크섬 ---------------------------------------------------------------------------------

        private const string Sha64 = "ddee7535c8dd0000406d0365de19d3c2863ba15bd1274408c1d12758c6a983a0";

        [Fact]
        public void Release_json_yields_the_asset_for_the_architecture_with_its_digest()
        {
            string json = """
                {"tag_name":"v9.9.9","draft":false,"prerelease":false,"assets":[
                 {"name":"omp-windows-arm64.exe","size":3,"browser_download_url":"https://github.com/o/r/releases/download/v9.9.9/omp-windows-arm64.exe","digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"},
                 {"name":"omp-windows-x64.exe","size":7,"browser_download_url":"https://github.com/o/r/releases/download/v9.9.9/omp-windows-x64.exe","digest":"sha256:DDEE7535C8DD0000406D0365DE19D3C2863BA15BD1274408C1D12758C6A983A0"},
                 {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/o/r/releases/download/v9.9.9/SHA256SUMS.txt"}]}
                """;
            var (tag, asset, sums) = OmpInstaller.ParseRelease(json, "omp-windows-x64.exe")!.Value;
            Assert.Equal("v9.9.9", tag);
            Assert.Equal(7, asset.Size);
            Assert.Equal(Sha64, asset.Sha256);
            Assert.EndsWith("SHA256SUMS.txt", sums);
            Assert.Null(OmpInstaller.ParseRelease(json, "omp-windows-riscv.exe"));
            Assert.Null(OmpInstaller.ParseRelease(json.Replace("\"prerelease\":false", "\"prerelease\":true"), "omp-windows-x64.exe"));
            Assert.Null(OmpInstaller.ParseRelease(json.Replace("\"draft\":false", "\"draft\":true"), "omp-windows-x64.exe"));
        }

        [Theory]
        [InlineData("sha1:ddee7535c8dd0000406d0365de19d3c2863ba15bd1274408c1d12758c6a983a0")]
        [InlineData("sha256:xyz")]
        [InlineData("sha256:")]
        [InlineData(null)]
        public void Only_a_well_formed_sha256_digest_is_accepted(string? digest) => Assert.Null(OmpInstaller.NormalizeDigest(digest));

        [Fact]
        public void Checksum_file_lines_are_matched_by_exact_file_name()
        {
            string text = $"{Sha64}  omp-windows-x64.exe\r\n{new string('a', 64)} *omp-windows-arm64.exe\n{new string('b', 64)}  omp-windows-x64.exe.sig\n";
            Assert.Equal(Sha64, OmpInstaller.ParseChecksums(text, "omp-windows-x64.exe"));
            Assert.Equal(new string('a', 64), OmpInstaller.ParseChecksums(text, "omp-windows-arm64.exe"));
            Assert.Null(OmpInstaller.ParseChecksums(text, "omp-linux-x64"));
        }

        // ---- 다운로드(가짜 HTTP) --------------------------------------------------------------------------------

        private sealed class FakeGitHub : HttpMessageHandler
        {
            public byte[] Binary = Encoding.ASCII.GetBytes("pretend-omp-binary");
            public string? ApiDigest;       // null이면 API에 digest 없음
            public string? SumsLine;        // null이면 SHA256SUMS.txt 에셋 없음
            public string AssetUrl = "https://github.com/can1357/oh-my-pi/releases/download/v9.9.9/omp-windows-x64.exe";
            public bool Fail;
            public long? DeclaredLength;
            public List<string> Requests { get; } = new();
            public string Asset = "omp-windows-x64.exe";
            public TaskCompletionSource? Gate;

            public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string url = request.RequestUri!.ToString();
                Requests.Add(url);
                if (Fail) throw new HttpRequestException("name resolution failed");
                if (url.EndsWith("/releases/latest"))
                {
                    string digest = ApiDigest == null ? "" : $",\"digest\":\"sha256:{ApiDigest}\"";
                    string sums = SumsLine == null ? "" : ",{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/can1357/oh-my-pi/releases/download/v9.9.9/SHA256SUMS.txt\"}";
                    string json = $"{{\"tag_name\":\"v9.9.9\",\"prerelease\":false,\"draft\":false,\"assets\":[{{\"name\":\"{Asset}\",\"size\":{Binary.Length},\"browser_download_url\":\"{AssetUrl}\"{digest}}}{sums}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
                }
                if (url.EndsWith("SHA256SUMS.txt"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SumsLine ?? "") };
                if (Gate != null) await Gate.Task.WaitAsync(ct);
                var content = new ByteArrayContent(Binary);
                if (DeclaredLength != null) content.Headers.ContentLength = DeclaredLength;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
        }

        [Fact]
        public async Task Download_installs_when_the_published_digest_matches()
        {
            var gh = new FakeGitHub();
            gh.ApiDigest = FakeGitHub.Sha(gh.Binary);
            var stages = new List<string>();
            var progress = new SyncProgress(p => stages.Add(p.Stage));

            var r = await OmpInstaller.DownloadAndInstallAsync(progress, options: Options(Target, http: gh));

            Assert.True(r.Ok, r.Message);
            Assert.Equal("pretend-omp-binary", File.ReadAllText(Target));
            Assert.Contains("SHA-256", r.Message);
            Assert.Equal(new[] { "query", "download", "verify", "install" }, stages.Distinct());
            Assert.Empty(LeftoverTempFiles());
            Assert.All(gh.Requests, u => Assert.StartsWith("https://", u));
        }

        [Fact]
        public async Task Download_with_a_wrong_digest_is_discarded_and_does_not_touch_the_install()
        {
            var gh = new FakeGitHub { ApiDigest = new string('0', 64) };
            Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
            File.WriteAllText(Target, "existing");

            var r = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: gh));

            Assert.Equal(OmpInstallError.DigestMismatch, r.Error);
            Assert.Equal("existing", File.ReadAllText(Target));
            Assert.Empty(LeftoverTempFiles());
        }

        [Fact]
        public async Task Download_without_any_published_digest_is_refused_before_downloading()
        {
            var gh = new FakeGitHub();
            var r = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: gh));
            Assert.Equal(OmpInstallError.NoDigest, r.Error);
            Assert.DoesNotContain(gh.Requests, u => u.EndsWith("omp-windows-x64.exe"));
            Assert.False(File.Exists(Target));
        }

        [Fact]
        public async Task Digest_may_come_from_SHA256SUMS_but_disagreeing_sources_are_refused()
        {
            var gh = new FakeGitHub();
            string good = FakeGitHub.Sha(gh.Binary);
            gh.SumsLine = $"{good}  omp-windows-x64.exe\n";
            var viaSums = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: gh));
            Assert.True(viaSums.Ok, viaSums.Message);

            var disagree = new FakeGitHub { ApiDigest = good, SumsLine = new string('1', 64) + "  omp-windows-x64.exe\n" };
            string other = Path.Combine(_dir, "other", "omp.exe");
            var r = await OmpInstaller.DownloadAndInstallAsync(options: Options(other, http: disagree));
            Assert.Equal(OmpInstallError.DigestMismatch, r.Error);
            Assert.DoesNotContain(disagree.Requests, u => u.EndsWith("omp-windows-x64.exe"));
            Assert.False(File.Exists(other));
        }

        [Fact]
        public async Task Download_picks_the_asset_for_the_architecture_and_refuses_unknown_ones()
        {
            var gh = new FakeGitHub { Asset = "omp-windows-arm64.exe", AssetUrl = "https://github.com/can1357/oh-my-pi/releases/download/v9.9.9/omp-windows-arm64.exe" };
            gh.ApiDigest = FakeGitHub.Sha(gh.Binary);
            Assert.True((await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: gh, arch: "arm64"))).Ok);
            Assert.Contains(gh.Requests, u => u.EndsWith("omp-windows-arm64.exe"));

            var none = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: new FakeGitHub(), arch: "x86"));
            Assert.Equal(OmpInstallError.NoAsset, none.Error);
        }

        [Fact]
        public async Task Download_refuses_assets_outside_github()
        {
            var gh = new FakeGitHub { AssetUrl = "https://evil.example.com/omp-windows-x64.exe", ApiDigest = new string('2', 64) };
            var r = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: gh));
            Assert.Equal(OmpInstallError.Network, r.Error);
            Assert.DoesNotContain(gh.Requests, u => u.Contains("evil"));

            var http = new FakeGitHub { AssetUrl = "http://github.com/x/omp-windows-x64.exe", ApiDigest = new string('2', 64) };
            Assert.Equal(OmpInstallError.Network, (await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: http))).Error);
        }

        [Fact]
        public async Task Network_failures_and_truncated_downloads_give_a_network_error()
        {
            var down = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: new FakeGitHub { Fail = true }));
            Assert.Equal(OmpInstallError.Network, down.Error);
            Assert.Contains("internet", down.Message);

            var gh = new FakeGitHub();
            gh.ApiDigest = FakeGitHub.Sha(gh.Binary);
            gh.DeclaredLength = gh.Binary.Length + 10;   // 서버가 약속한 길이보다 일찍 끝난다
            var cut = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: gh));
            Assert.False(cut.Ok);
            Assert.False(File.Exists(Target));
            Assert.Empty(LeftoverTempFiles());
        }

        [Fact]
        public async Task Download_can_be_cancelled_and_leaves_no_file()
        {
            var gh = new FakeGitHub { Gate = new TaskCompletionSource() };
            gh.ApiDigest = FakeGitHub.Sha(gh.Binary);
            using var cts = new CancellationTokenSource();
            var task = OmpInstaller.DownloadAndInstallAsync(null, cts.Token, options: Options(Target, http: gh));
            while (!gh.Requests.Any(u => u.EndsWith("omp-windows-x64.exe"))) await Task.Delay(10);
            cts.Cancel();
            var r = await task;
            Assert.Equal(OmpInstallError.Cancelled, r.Error);
            Assert.False(File.Exists(Target));
            Assert.Empty(LeftoverTempFiles());
        }

        [Fact]
        public async Task A_downloaded_file_that_cannot_run_is_not_installed()
        {
            var gh = new FakeGitHub();
            gh.ApiDigest = FakeGitHub.Sha(gh.Binary);
            var r = await OmpInstaller.DownloadAndInstallAsync(options: Options(Target, http: gh, run: _ => new OmpRunOutcome(null, null, false, 193, "bad image")));
            Assert.Equal(OmpInstallError.NotRunnable, r.Error);
            Assert.False(File.Exists(Target));
            Assert.Empty(LeftoverTempFiles());
        }

        private sealed class SyncProgress : IProgress<OmpDownloadProgress>
        {
            private readonly Action<OmpDownloadProgress> _on;
            public SyncProgress(Action<OmpDownloadProgress> on) => _on = on;
            public void Report(OmpDownloadProgress value) => _on(value);
        }
    }
}
