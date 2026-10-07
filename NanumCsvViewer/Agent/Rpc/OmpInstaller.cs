using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace NanumCsvViewer.Agent.Rpc
{
    internal enum OmpInstallError
    {
        None,
        Cancelled,
        /// <summary>네트워크·프록시·방화벽·GitHub 응답 오류.</summary>
        Network,
        /// <summary>이 PC 아키텍처의 릴리스 파일이 없다.</summary>
        NoAsset,
        /// <summary>릴리스가 SHA-256을 알려 주지 않아 무결성을 확인할 수 없다(자동 설치 거부).</summary>
        NoDigest,
        DigestMismatch,
        /// <summary>복사·다운로드한 파일을 실행할 수 없다.</summary>
        NotRunnable,
        TooOld,
        /// <summary>대상 omp.exe가 실행 중이라 바꿀 수 없다.</summary>
        InUse,
        Io,
    }

    internal sealed record OmpInstallResult(bool Ok, string? InstalledPath, string Message, OmpInstallError Error, OmpVersion? Version = null);

    /// <summary>Stage: "query" | "download" | "verify" | "install".</summary>
    internal sealed record OmpDownloadProgress(long Received, long? Total, string Stage);

    /// <summary>사용자 Path(HKCU\Environment) 읽기/쓰기(테스트는 가짜로 교체).</summary>
    internal interface IUserPathStore
    {
        /// <summary>Path 원문(확장 전)과 값 종류. 값이 없으면 null.</summary>
        string? Read(out RegistryValueKind kind);
        void Write(string value, RegistryValueKind kind);
    }

    internal sealed class RegistryUserPathStore : IUserPathStore
    {
        public string? Read(out RegistryValueKind kind)
        {
            kind = RegistryValueKind.ExpandString;
            using var key = Registry.CurrentUser.OpenSubKey("Environment");
            if (key == null || key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string s) return null;
            kind = key.GetValueKind("Path");
            return s;
        }

        public void Write(string value, RegistryValueKind kind)
        {
            using var key = Registry.CurrentUser.CreateSubKey("Environment");
            key.SetValue("Path", value, kind);
        }
    }

    /// <summary>설치 환경(테스트는 임시 폴더·가짜 HTTP·가짜 레지스트리로 교체). 기본값이 실제 환경이다.</summary>
    internal sealed class OmpInstallOptions
    {
        /// <summary>설치 대상(기본 %LOCALAPPDATA%\omp\omp.exe — 공식 설치 스크립트와 같은 위치).</summary>
        public string? TargetPath { get; init; }
        /// <summary>null이면 시스템 프록시를 쓰는 기본 핸들러.</summary>
        public HttpMessageHandler? HttpHandler { get; init; }
        public string ReleaseApiUrl { get; init; } = OmpInstaller.LatestReleaseApi;
        /// <summary>"x64" | "arm64".</summary>
        public string? Architecture { get; init; }
        public IUserPathStore PathStore { get; init; } = new RegistryUserPathStore();
        /// <summary>사용자 환경 변수가 바뀌었음을 열린 프로그램에 알린다(WM_SETTINGCHANGE).</summary>
        public Action BroadcastEnvironmentChange { get; init; } = OmpInstaller.BroadcastSettingChange;
        public Func<string, CancellationToken, Task<OmpRunOutcome>> RunVersion { get; init; } =
            (p, ct) => OmpLocator.RunVersionOutcomeAsync(p, TimeSpan.FromSeconds(15), ct);
        /// <summary>실행 중인 omp 프로세스의 실행 파일 경로.</summary>
        public Func<IReadOnlyList<string>> RunningPaths { get; init; } = OmpPathEnvironment.RunningOmpPaths;
        public Func<string, string> Expand { get; init; } = Environment.ExpandEnvironmentVariables;
        /// <summary>내려받기가 이 시간 동안 한 바이트도 못 받으면 중단한다.</summary>
        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);
        public bool? Korean { get; init; }

        internal string ResolveTarget() =>
            TargetPath ?? new OmpEnvironment().DefaultInstallPath
            ?? throw new InvalidOperationException("LOCALAPPDATA is not set");

        internal string ResolveArchitecture() =>
            Architecture ?? (RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");

        internal bool IsKorean => Korean ?? Loc.CurrentLanguage == "ko";
    }

    /// <summary>
    /// omp 설치: 후보 파일 복사(Zone.Identifier 제거) 또는 GitHub 릴리스(can1357/oh-my-pi, MIT)에서 내려받기(SHA-256 검증).
    /// 어느 쪽이든 임시 파일에서 `--version`(≥ <see cref="OmpVersion.Minimum"/>)을 확인한 뒤에만 omp.exe를 바꾼다(기존 설치는 실패하면 그대로).
    /// 설정(AgentOmpPath)은 건드리지 않는다. 텔레메트리 없음.
    /// </summary>
    internal static class OmpInstaller
    {
        public const string Repository = "can1357/oh-my-pi";
        public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

        /// <summary>%LOCALAPPDATA%\omp\omp.exe</summary>
        public static string? TargetPath => new OmpEnvironment().DefaultInstallPath;

        // ---- 파일에서 설치 --------------------------------------------------------------------------------------

        public static async Task<OmpInstallResult> InstallFromFileAsync(string src, bool addToPath, CancellationToken ct = default, OmpInstallOptions? options = null)
        {
            options ??= new OmpInstallOptions();
            bool ko = options.IsKorean;
            string T(string en, string kor) => ko ? kor : en;
            string target;
            try { target = Path.GetFullPath(options.ResolveTarget()); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return Fail(OmpInstallError.Io, T("Cannot determine the install folder: ", "설치 폴더를 알 수 없습니다: ") + ex.Message);
            }
            if (string.IsNullOrWhiteSpace(src) || !File.Exists(src))
                return Fail(OmpInstallError.Io, T("The file does not exist: ", "파일이 없습니다: ") + src);

            string temp = TempNameFor(target);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string fullSrc = Path.GetFullPath(src);
                if (string.Equals(fullSrc, target, StringComparison.OrdinalIgnoreCase))
                {
                    // 이미 설치 위치의 파일: 복사 없이 차단 표시만 풀고 검증한다.
                    ZoneMark.Remove(target);
                    var self = await VerifyAsync(target, options, ct).ConfigureAwait(false);
                    if (self.Error != null) return self.Error;
                    return Success(target, self.Version!.Value, addToPath, options);
                }

                await CopyFileAsync(fullSrc, temp, ct).ConfigureAwait(false);
                ZoneMark.Remove(temp);
                var check = await VerifyAsync(temp, options, ct).ConfigureAwait(false);
                if (check.Error != null) return check.Error;
                var swap = Swap(temp, target, options);
                if (swap != null) return swap;
                ZoneMark.Remove(target);
                return Success(target, check.Version!.Value, addToPath, options);
            }
            catch (OperationCanceledException) { return Fail(OmpInstallError.Cancelled, T("Cancelled.", "취소되었습니다.")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(OmpInstallError.Io, IoMessage(ex, ko));
            }
            finally { TryDelete(temp); }
        }

        // ---- 내려받아 설치 --------------------------------------------------------------------------------------

        /// <summary>
        /// 최신 정식 릴리스의 이 PC 아키텍처용 omp-windows-*.exe를 HTTPS로 받아 릴리스가 공개한 SHA-256과 대조한 뒤 설치한다.
        /// SHA-256을 알 수 없으면 설치하지 않는다(NoDigest). 호출 전에 사용자 확인을 받는다.
        /// </summary>
        public static async Task<OmpInstallResult> DownloadAndInstallAsync(IProgress<OmpDownloadProgress>? progress = null, CancellationToken ct = default, bool addToPath = false, OmpInstallOptions? options = null)
        {
            options ??= new OmpInstallOptions();
            bool ko = options.IsKorean;
            string T(string en, string kor) => ko ? kor : en;
            string target;
            try { target = Path.GetFullPath(options.ResolveTarget()); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return Fail(OmpInstallError.Io, T("Cannot determine the install folder: ", "설치 폴더를 알 수 없습니다: ") + ex.Message);
            }

            string arch = options.ResolveArchitecture();
            string assetName = $"omp-windows-{arch}.exe";
            string temp = TempNameFor(target);
            bool ownsHandler = options.HttpHandler == null;
            using var http = new HttpClient(options.HttpHandler ?? CreateDefaultHandler(), disposeHandler: ownsHandler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NanumCsvViewer", "1.0"));
            try
            {
                if (arch is not ("x64" or "arm64"))
                    return Fail(OmpInstallError.NoAsset, T($"No omp download exists for this PC architecture ({arch}).", $"이 PC의 아키텍처({arch})용 omp 다운로드가 없습니다."));

                progress?.Report(new OmpDownloadProgress(0, null, "query"));
                OmpReleaseAsset asset;
                string tag;
                using (var apiCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    apiCts.CancelAfter(TimeSpan.FromSeconds(30));
                    string json = await GetStringAsync(http, options.ReleaseApiUrl, "application/vnd.github+json", apiCts.Token).ConfigureAwait(false);
                    var release = ParseRelease(json, assetName);
                    if (release == null)
                        return Fail(OmpInstallError.NoAsset, T($"The latest release has no {assetName}.", $"최신 릴리스에 {assetName}이(가) 없습니다."));
                    (tag, asset) = (release.Value.Tag, release.Value.Asset);

                    if (!IsGitHubHttps(asset.Url))
                        return Fail(OmpInstallError.Network, T("The release points outside github.com; refusing to download.", "릴리스가 github.com 밖을 가리켜 내려받지 않습니다."));

                    string? digest = asset.Sha256;
                    if (release.Value.ChecksumsUrl is { } sumsUrl && IsGitHubHttps(sumsUrl))
                    {
                        string? fromSums = null;
                        try { fromSums = ParseChecksums(await GetStringAsync(http, sumsUrl, "text/plain", apiCts.Token).ConfigureAwait(false), assetName); }
                        catch (HttpRequestException) { /* 체크섬 파일은 보조: API digest가 있으면 계속한다 */ }
                        if (digest != null && fromSums != null && !string.Equals(digest, fromSums, StringComparison.OrdinalIgnoreCase))
                            return Fail(OmpInstallError.DigestMismatch, T("The release's SHA-256 values disagree (API vs SHA256SUMS.txt); refusing to install.", "릴리스가 알려 준 SHA-256이 서로 다릅니다(API와 SHA256SUMS.txt). 설치하지 않습니다."));
                        digest ??= fromSums;
                    }
                    if (digest == null)
                        return Fail(OmpInstallError.NoDigest, T($"Release {tag} does not publish a SHA-256 for {assetName}, so the download cannot be verified and was not installed. Download it manually and use Install from file.",
                                                              $"릴리스 {tag}에 {assetName}의 SHA-256이 없어 내려받은 파일을 검증할 수 없습니다. 설치하지 않았습니다. 직접 받아 '파일로 설치'를 쓰세요."));
                    asset = asset with { Sha256 = digest };
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CheckFreeSpace(target, asset.Size, ko);

                string actual = await DownloadAsync(http, asset, temp, progress, options.StallTimeout, ct).ConfigureAwait(false);
                progress?.Report(new OmpDownloadProgress(asset.Size ?? 0, asset.Size, "verify"));
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Fail(OmpInstallError.DigestMismatch, T("The downloaded file does not match the published SHA-256 and was discarded.", "내려받은 파일이 공개된 SHA-256과 달라 버렸습니다."));

                var check = await VerifyAsync(temp, options, ct).ConfigureAwait(false);
                if (check.Error != null) return check.Error;
                progress?.Report(new OmpDownloadProgress(asset.Size ?? 0, asset.Size, "install"));
                var swap = Swap(temp, target, options);
                if (swap != null) return swap;
                ZoneMark.Remove(target);
                string headline = T($"Installed omp {check.Version} ({tag}, SHA-256 verified) at {target}.", $"omp {check.Version}({tag}, SHA-256 확인)을 {target}에 설치했습니다.");
                return Success(target, check.Version!.Value, addToPath, options, headline);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Fail(OmpInstallError.Cancelled, T("Cancelled.", "취소되었습니다.")); }
            catch (OperationCanceledException) { return Fail(OmpInstallError.Network, NetworkMessage(null, ko, timedOut: true)); }
            catch (HttpRequestException ex) { return Fail(OmpInstallError.Network, NetworkMessage(ex, ko, timedOut: false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Fail(OmpInstallError.Io, IoMessage(ex, ko)); }
            finally { TryDelete(temp); }
        }

        // ---- 릴리스 JSON ----------------------------------------------------------------------------------------

        internal readonly record struct OmpReleaseAsset(string Name, string Url, long? Size, string? Sha256);

        /// <summary>GitHub 릴리스 JSON에서 에셋 한 개와 SHA256SUMS.txt 주소를 고른다. 초안/시험판이거나 에셋이 없으면 null.</summary>
        internal static (string Tag, OmpReleaseAsset Asset, string? ChecksumsUrl)? ParseRelease(string json, string assetName)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            string tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            OmpReleaseAsset? found = null;
            string? sums = null;
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                string url = a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
                if (!name.Equals(assetName, StringComparison.OrdinalIgnoreCase)) continue;
                long? size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long sz) ? sz : null;
                string? digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? NormalizeDigest(dg.GetString()) : null;
                found = new OmpReleaseAsset(name, url, size, digest);
            }
            return found is { } f ? (tag, f, sums) : null;
        }

        /// <summary>"sha256:&lt;64 hex&gt;" → 소문자 hex. 그 밖의 알고리즘·모양은 null.</summary>
        internal static string? NormalizeDigest(string? digest)
        {
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
            string hex = digest[7..].Trim().ToLowerInvariant();
            return IsHex64(hex) ? hex : null;
        }

        /// <summary>SHA256SUMS.txt("&lt;hex&gt;  &lt;name&gt;" 줄)에서 파일 이름의 해시.</summary>
        internal static string? ParseChecksums(string text, string assetName)
        {
            foreach (string line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                if (sp != 64) continue;
                string hex = line[..64].ToLowerInvariant();
                string name = line[64..].Trim().TrimStart('*');
                if (IsHex64(hex) && name.Equals(assetName, StringComparison.OrdinalIgnoreCase)) return hex;
            }
            return null;
        }

        private static bool IsHex64(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

        private static bool IsGitHubHttps(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

        // ---- 네트워크 -------------------------------------------------------------------------------------------

        private static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
        {
            // 시스템(IE) 프록시를 쓰고, 인증이 필요한 회사 프록시에는 현재 Windows 계정으로 인증한다.
            Proxy = WebRequest.GetSystemWebProxy(),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };

        private static async Task<string> GetStringAsync(HttpClient http, string url, string accept, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd(accept);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        private static async Task<string> DownloadAsync(HttpClient http, OmpReleaseAsset asset, string temp, IProgress<OmpDownloadProgress>? progress, TimeSpan stall, CancellationToken ct)
        {
            using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength ?? asset.Size;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true);
            byte[] buffer = new byte[1 << 17];
            long received = 0;
            long lastReport = Environment.TickCount64;
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                watchdog.CancelAfter(stall);
                int n = await net.ReadAsync(buffer, watchdog.Token).ConfigureAwait(false);
                if (n == 0) break;
                hash.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                received += n;
                long now = Environment.TickCount64;
                if (now - lastReport >= 100) { lastReport = now; progress?.Report(new OmpDownloadProgress(received, total, "download")); }
            }
            await file.FlushAsync(ct).ConfigureAwait(false);
            progress?.Report(new OmpDownloadProgress(received, total, "download"));
            if (total is long t && received != t)
                throw new HttpRequestException($"The download ended early ({received} of {t} bytes).");
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static string NetworkMessage(HttpRequestException? ex, bool ko, bool timedOut)
        {
            string T(string en, string kor) => ko ? kor : en;
            string hint = T("Check the internet connection, proxy and firewall. You can also download omp-windows-x64.exe yourself from https://github.com/can1357/oh-my-pi/releases and use Browse… or Install from file.",
                            "인터넷 연결·프록시·방화벽을 확인하세요. https://github.com/can1357/oh-my-pi/releases 에서 omp-windows-x64.exe를 직접 받아 [찾아보기…]나 '파일로 설치'를 써도 됩니다.");
            if (timedOut) return T("GitHub did not answer in time. ", "GitHub가 제때 응답하지 않았습니다. ") + hint;
            string reason;
            if (ex!.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                reason = T("GitHub refused the request (rate limit?). Try again later.", "GitHub가 요청을 거절했습니다(요청 한도?). 잠시 뒤 다시 시도하세요.");
            else if (ex.StatusCode is { } code)
                reason = T($"GitHub answered HTTP {(int)code}.", $"GitHub가 HTTP {(int)code}(으)로 응답했습니다.");
            else
                reason = T("Could not reach GitHub: ", "GitHub에 연결하지 못했습니다: ") + (ex.InnerException?.Message ?? ex.Message);
            return reason + " " + hint;
        }

        // ---- 파일 -----------------------------------------------------------------------------------------------

        private static string TempNameFor(string target) =>
            Path.Combine(Path.GetDirectoryName(target)!, $"omp.install-{Guid.NewGuid():N}.exe");

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static async Task CopyFileAsync(string src, string dest, CancellationToken ct)
        {
            // 스트림 복사: 대체 데이터 스트림(Zone.Identifier)은 따라오지 않는다. 실행 중인 원본도 읽을 수 있다.
            await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 17, useAsync: true);
            await using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 17, useAsync: true);
            await input.CopyToAsync(output, 1 << 17, ct).ConfigureAwait(false);
        }

        private static void CheckFreeSpace(string target, long? needed, bool ko)
        {
            if (needed is not long n) return;
            try
            {
                string root = Path.GetPathRoot(target)!;
                long free = new DriveInfo(root).AvailableFreeSpace;
                if (free < n + (64L << 20))
                    throw new IOException(ko ? $"디스크 공간이 부족합니다(필요 약 {n >> 20} MB, 여유 {free >> 20} MB): {root}" : $"Not enough disk space ({n >> 20} MB needed, {free >> 20} MB free): {root}");
            }
            catch (ArgumentException) { }
        }

        private static string IoMessage(Exception ex, bool ko)
        {
            string head = ex is UnauthorizedAccessException
                ? (ko ? "폴더에 쓸 권한이 없습니다: " : "No permission to write: ")
                : ex.HResult == unchecked((int)0x80070070)
                    ? (ko ? "디스크 공간이 부족합니다: " : "The disk is full: ")
                    : (ko ? "파일 작업에 실패했습니다: " : "File operation failed: ");
            return head + ex.Message;
        }

        /// <summary>임시 파일을 --version으로 확인한다. 실패하면 Error(그대로 돌려줄 결과), 성공하면 Version.</summary>
        private static async Task<(OmpInstallResult? Error, OmpVersion? Version)> VerifyAsync(string exe, OmpInstallOptions options, CancellationToken ct)
        {
            bool ko = options.IsKorean;
            string T(string en, string kor) => ko ? kor : en;
            var run = await options.RunVersion(exe, ct).ConfigureAwait(false);
            if (!run.Started)
                return (Fail(OmpInstallError.NotRunnable, T("The file cannot be run on this PC: ", "이 PC에서 실행할 수 없는 파일입니다: ") + run.StartError), null);
            if (run.TimedOut)
                return (Fail(OmpInstallError.NotRunnable, T("The file did not answer to --version in time.", "파일이 --version에 제때 답하지 않았습니다.")), null);
            if (!OmpDiscovery.TryParseVersionOutput(run.Output, out var version))
                return (Fail(OmpInstallError.NotRunnable, T("The file does not look like omp (no omp/<version> in its --version output).", "omp 파일이 아닌 것 같습니다(--version 출력에 omp/<버전>이 없습니다).")), null);
            if (version < OmpVersion.Minimum)
                return (Fail(OmpInstallError.TooOld, T($"omp {version} is too old; {OmpVersion.Minimum} or newer is required.", $"omp {version}은(는) 너무 오래되었습니다. {OmpVersion.Minimum} 이상이 필요합니다.")), null);
            return (null, version);
        }

        /// <summary>임시 파일을 대상 자리로: 기존 파일이 없으면 이동, 있으면 원자적 교체. 대상이 실행 중이면 InUse.</summary>
        private static OmpInstallResult? Swap(string temp, string target, OmpInstallOptions options)
        {
            bool ko = options.IsKorean;
            string T(string en, string kor) => ko ? kor : en;
            string InUse() => T($"omp is running from {target}. Close omp (and the AI chat) and try again.", $"{target}의 omp가 실행 중입니다. omp(와 AI 채팅)를 닫고 다시 시도하세요.");
            if (File.Exists(target))
            {
                if (options.RunningPaths().Any(p => string.Equals(SafeFull(p), target, StringComparison.OrdinalIgnoreCase)))
                    return Fail(OmpInstallError.InUse, InUse());
                try { File.Replace(temp, target, null); }
                catch (IOException) when (IsLocked(target)) { return Fail(OmpInstallError.InUse, InUse()); }
                catch (UnauthorizedAccessException) when (IsLocked(target)) { return Fail(OmpInstallError.InUse, InUse()); }
            }
            else File.Move(temp, target);
            return null;
        }

        private static string SafeFull(string p)
        {
            try { return Path.GetFullPath(p); } catch (ArgumentException) { return p; }
        }

        private static bool IsLocked(string path)
        {
            try { using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static OmpInstallResult Success(string target, OmpVersion version, bool addToPath, OmpInstallOptions options, string? headline = null)
        {
            bool ko = options.IsKorean;
            string msg = headline ?? (ko ? $"omp {version}을(를) {target}에 설치했습니다." : $"Installed omp {version} at {target}.");
            if (addToPath)
            {
                string dir = Path.GetDirectoryName(target)!;
                try
                {
                    bool changed = AddToUserPath(dir, options);
                    msg += ko ? (changed ? " 사용자 PATH에 추가했습니다." : " 사용자 PATH에 이미 있습니다.") : (changed ? " Added to the user PATH." : " Already on the user PATH.");
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    msg += ko ? " 사용자 PATH에는 추가하지 못했습니다: " + ex.Message : " Could not add it to the user PATH: " + ex.Message;
                }
            }
            return new OmpInstallResult(true, target, msg, OmpInstallError.None, version);
        }

        private static OmpInstallResult Fail(OmpInstallError error, string message) => new(false, null, message, error);

        // ---- 사용자 PATH ----------------------------------------------------------------------------------------

        /// <summary>
        /// 사용자 Path(HKCU\Environment)에 폴더를 덧붙인다. 이미 있으면(대소문자·끝 '\'·%VAR% 확장 무시) 아무것도 바꾸지 않고 false.
        /// 기존 값의 종류(REG_EXPAND_SZ)를 유지하고, 바뀌면 WM_SETTINGCHANGE를 알린다.
        /// </summary>
        public static bool AddToUserPath(string dir, OmpInstallOptions? options = null)
        {
            options ??= new OmpInstallOptions();
            string raw = options.PathStore.Read(out var kind) ?? "";
            string want = Normalize(dir);
            foreach (string entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(Normalize(entry), want, StringComparison.OrdinalIgnoreCase)) return false;
                string expanded;
                try { expanded = options.Expand(entry); } catch (ArgumentException) { continue; }
                if (string.Equals(Normalize(expanded), want, StringComparison.OrdinalIgnoreCase)) return false;
            }
            string value = raw.Length == 0 ? dir : raw.TrimEnd(';') + ";" + dir;
            options.PathStore.Write(value, raw.Length == 0 ? RegistryValueKind.ExpandString : kind);
            options.BroadcastEnvironmentChange();
            return true;
        }

        private static string Normalize(string p) => p.Trim().Trim('"').TrimEnd('\\', '/');

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        /// <summary>WM_SETTINGCHANGE("Environment")를 모든 최상위 창에 알린다(탐색기가 새 PATH를 읽는다). 응답 없는 창은 기다리지 않는다.</summary>
        internal static void BroadcastSettingChange()
        {
            const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;
            try { SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 3000, out _); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    /// <summary>인터넷에서 받은 파일 표시(대체 데이터 스트림 Zone.Identifier).</summary>
    internal static class ZoneMark
    {
        private const string Stream = ":Zone.Identifier";
        private const uint InvalidAttributes = 0xFFFFFFFF;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteFileW(string path);

        public static bool Exists(string path) => GetFileAttributesW(path + Stream) != InvalidAttributes;

        /// <summary>표시가 있으면 지운다(Unblock-File과 같다). 없거나 NTFS가 아니면 아무 일도 없다.</summary>
        public static void Remove(string path)
        {
            if (Exists(path)) DeleteFileW(path + Stream);
        }
    }
}
