using System.Text.Json;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Rpc;
using Xunit;

namespace NanumCsvViewer.Tests
{
    /// <summary>omp를 못 찾았을 때 채팅 알림의 버튼(찾아보기·이 파일로 설치·다운로드·다시 시도).</summary>
    public class OmpSetupChatTests
    {
        private const string Candidate = @"C:\Users\u\Downloads\omp-windows-x64.exe";

        private static string Click(string url) => JsonSerializer.Serialize(new { t = "openUrl", url });

        private static ControllerRig MissingRig()
        {
            var rig = new ControllerRig { OmpPath = null };
            rig.Candidates.Add(new OmpCandidate(Candidate, "Downloads folder"));
            return rig;
        }

        private static async Task StartMissingAsync(ControllerRig rig) =>
            await rig.Ui.InvokeAsync(() => rig.Controller.StartAsync(rig.WorkDir));

        private static JsonElement ActionNotice(ControllerRig rig) =>
            rig.Page.Parsed("notice").Last(n => n.TryGetProperty("actions", out _));

        private static string[] ActionUrls(JsonElement notice) => notice.Child("actions").Items().Select(a => a.Str("url")).ToArray();

        [Fact]
        public async Task Missing_omp_offers_browse_install_of_each_candidate_download_and_retry()
        {
            using var rig = MissingRig();
            var reasons = new List<AgentSetupReason>();
            rig.Controller.SetupNeeded += (r, _) => reasons.Add(r);
            await StartMissingAsync(rig);

            var notice = ActionNotice(rig);
            string[] urls = ActionUrls(notice);
            Assert.Equal("nanumcsv://omp/browse", urls[0]);
            Assert.Contains("nanumcsv://omp/install?src=" + Uri.EscapeDataString(Candidate), urls);
            Assert.Contains(urls, u => u.StartsWith("nanumcsv://omp/download"));
            Assert.Contains("nanumcsv://omp/retry", urls);
            // 사용자 PATH 추가 선택은 체크 상자(기본 꺼짐)로, 설치 단추에만 걸린다.
            Assert.False(string.IsNullOrEmpty(notice.Str("checkbox")));
            Assert.All(notice.Child("actions").Items(), a =>
                Assert.Equal(a.Str("url").Contains("install") || a.Str("url").Contains("download"), a.Bool("pathOption") == true));
            Assert.Equal(new[] { AgentSetupReason.Omp }, reasons);
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("level") == "error");
        }

        [Fact]
        public async Task Install_click_copies_the_candidate_then_retries_and_connects()
        {
            using var rig = MissingRig();
            rig.InstallFile = (src, add, _) =>
            {
                rig.OmpPath = @"C:\fake\omp.exe";   // 설치가 끝나면 다음 탐색에서 찾는다
                return Task.FromResult(new OmpInstallResult(true, @"C:\fake\omp.exe", "Installed omp 18.5.0", OmpInstallError.None, new OmpVersion(18, 5, 0)));
            };
            await StartMissingAsync(rig);

            rig.FromPage(Click("nanumcsv://omp/install?src=" + Uri.EscapeDataString(Candidate) + "&path=1"));
            await rig.WaitUntilAsync(() => rig.Ui.Invoke(() => rig.Controller.IsRunning));

            Assert.Equal((Candidate, true), rig.Installed.Single());
            Assert.Equal(2, rig.DiscoverCalls);
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("text").Contains("Installed omp 18.5.0"));
        }

        [Fact]
        public async Task Install_click_only_accepts_files_the_discovery_offered()
        {
            using var rig = MissingRig();
            await StartMissingAsync(rig);

            rig.FromPage(Click("nanumcsv://omp/install?src=" + Uri.EscapeDataString(@"C:\evil\payload.exe")));
            await Task.Delay(100);

            Assert.Empty(rig.Installed);
            Assert.Equal(1, rig.DiscoverCalls);
        }

        [Fact]
        public async Task A_failed_install_shows_the_reason_and_offers_the_choices_again()
        {
            using var rig = MissingRig();
            rig.InstallFile = (_, _, _) => Task.FromResult(new OmpInstallResult(false, null, "The file cannot be run on this PC", OmpInstallError.NotRunnable));
            await StartMissingAsync(rig);

            rig.FromPage(Click("nanumcsv://omp/install?src=" + Uri.EscapeDataString(Candidate)));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Count(n => n.TryGetProperty("actions", out _)) == 2);

            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("level") == "error" && n.Str("text") == "The file cannot be run on this PC");
            Assert.Equal(1, rig.DiscoverCalls);
        }

        [Fact]
        public async Task Browse_saves_the_chosen_path_and_retries_with_it()
        {
            using var rig = MissingRig();
            string picked = @"D:\tools\omp.exe";
            rig.Dialogs.PickedFiles = new[] { picked };
            string? saved = null;
            rig.Controller.OmpPathPicked += p => saved = p;
            await StartMissingAsync(rig);

            rig.FromPage(Click("nanumcsv://omp/browse"));
            await rig.WaitUntilAsync(() => rig.DiscoverCalls == 2);

            Assert.Equal(picked, saved);
            Assert.Equal(picked, rig.OnUi(() => rig.Controller.Options.OmpPath));
            Assert.Contains("omp*.exe", rig.Dialogs.PickFilters.Single());

            // 취소하면 아무것도 바뀌지 않는다.
            rig.Dialogs.PickedFiles = null;
            saved = null;
            rig.FromPage(Click("nanumcsv://omp/browse"));
            await Task.Delay(100);
            Assert.Null(saved);
            Assert.Equal(2, rig.DiscoverCalls);
        }

        [Fact]
        public async Task Download_asks_first_and_installs_only_after_the_user_agrees()
        {
            using var rig = MissingRig();
            int downloads = 0;
            bool? addToPath = null;
            rig.Download = (progress, add, _) =>
            {
                downloads++;
                addToPath = add;
                progress.Report(new OmpDownloadProgress(10, 100, "download"));
                rig.OmpPath = @"C:\fake\omp.exe";
                return Task.FromResult(new OmpInstallResult(true, @"C:\fake\omp.exe", "Installed", OmpInstallError.None, new OmpVersion(18, 8, 0)));
            };
            await StartMissingAsync(rig);

            rig.Dialogs.OnConfirm = (_, _) => false;
            rig.FromPage(Click("nanumcsv://omp/download?"));
            await Task.Delay(100);
            Assert.Equal(0, downloads);
            Assert.Single(rig.Dialogs.ConfirmTitles);

            rig.Dialogs.OnConfirm = (_, _) => true;
            rig.FromPage(Click("nanumcsv://omp/download?&path=1"));
            await rig.WaitUntilAsync(() => rig.Ui.Invoke(() => rig.Controller.IsRunning));
            Assert.Equal(1, downloads);
            Assert.True(addToPath);
        }

        [Fact]
        public async Task Retry_discovers_again_and_connects_once_omp_is_there()
        {
            using var rig = MissingRig();
            await StartMissingAsync(rig);
            Assert.Equal(1, rig.DiscoverCalls);

            rig.OmpPath = @"C:\fake\omp.exe";
            rig.FromPage(Click("nanumcsv://omp/retry"));
            await rig.WaitUntilAsync(() => rig.Ui.Invoke(() => rig.Controller.IsRunning));
            Assert.Equal(2, rig.DiscoverCalls);
        }

        [Theory]
        [InlineData("nanumcsv://setup/login", AgentSetupReason.Login)]
        [InlineData("nanumcsv://setup/omp", AgentSetupReason.Omp)]
        [InlineData("nanumcsv://setup/open", AgentSetupReason.Omp)]
        [InlineData("nanumcsv://setup/webview", AgentSetupReason.WebView)]
        public async Task Setup_links_raise_the_setup_event(string url, AgentSetupReason expected)
        {
            using var rig = new ControllerRig();
            var seen = new List<AgentSetupReason>();
            rig.Controller.SetupNeeded += (r, _) => seen.Add(r);
            rig.FromPage(Click(url));
            await rig.WaitUntilAsync(() => seen.Count > 0);
            Assert.Equal(new[] { expected }, seen);
            Assert.Empty(rig.Dialogs.Opened);   // 브라우저로 새어 나가지 않는다
        }

        [Fact]
        public async Task A_new_verification_is_reported_once_so_the_host_can_cache_it()
        {
            using var rig = new ControllerRig { VerifiedCache = @"C:\fake\omp.exe|18.4.4|1|2" };
            var seen = new List<string>();
            rig.Controller.OmpVerifiedChanged += seen.Add;
            await rig.StartAsync();
            rig.OnUi(() => rig.Controller.Stop());
            await rig.StartAsync();
            Assert.Equal(new[] { @"C:\fake\omp.exe|18.4.4|1|2" }, seen);
        }
    }
}
