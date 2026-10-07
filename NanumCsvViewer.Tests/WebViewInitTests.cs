using System.Windows.Forms;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer.Tests
{
    // WebView2 초기화 실패 분류·안내문·브라우저 로그 인자·앱 DPI 모드.
    public class WebViewInitTests
    {
        private static readonly IReadOnlyList<CompatLayerEntry> None = Array.Empty<CompatLayerEntry>();
        private static readonly IReadOnlyList<CompatLayerEntry> Dpi = new[]
        {
            new CompatLayerEntry("HKCU", @"C:\EdgeWebView\149\msedgewebview2.exe", "HIGHDPIAWARE", true, true),
        };

        private static COMException Com(uint hr) => new("x", unchecked((int)hr));

        [Fact]
        public void Missing_runtime_wins_over_any_hresult()
        {
            Assert.Equal(WebViewProblem.RuntimeMissing, WebViewDiagnostics.Classify(Com(0x8007139F), null, Dpi));
            Assert.Equal(WebViewProblem.RuntimeMissing, WebViewDiagnostics.Classify(Com(0x80004005), "", None));
        }

        [Theory]
        [InlineData(0x80070005u, "AccessDenied")]
        [InlineData(0x8007007Eu, "BinaryOrArch")]
        [InlineData(0x800700C1u, "BinaryOrArch")]
        [InlineData(0x80004005u, "Other")]
        public void Hresult_maps_to_problem(uint hr, string expected) =>
            Assert.Equal(Enum.Parse<WebViewProblem>(expected), WebViewDiagnostics.Classify(Com(hr), "149.0.1.1", Dpi));

        [Fact]
        public void State_mismatch_is_compat_layer_only_when_a_dpi_layer_exists()
        {
            Assert.Equal(WebViewProblem.CompatLayer, WebViewDiagnostics.Classify(Com(0x8007139F), "149", Dpi));
            Assert.Equal(WebViewProblem.StateMismatch, WebViewDiagnostics.Classify(Com(0x8007139F), "149", None));
            var nonDpi = new[] { new CompatLayerEntry("HKCU", @"C:\a\msedgewebview2.exe", "WIN7RTM", false, true) };
            Assert.Equal(WebViewProblem.StateMismatch, WebViewDiagnostics.Classify(Com(0x8007139F), "149", nonDpi));
        }

        [Fact]
        public void Hresult_is_found_through_inner_exceptions()
        {
            var wrapped = new InvalidOperationException("outer", Com(0x8007139F));
            Assert.Equal(unchecked((int)0x8007139F), WebViewDiagnostics.HResultOf(wrapped));
            Assert.Equal(WebViewProblem.CompatLayer, WebViewDiagnostics.Classify(wrapped, "149", Dpi));
        }

        [Theory]
        [InlineData("RuntimeMissing", true)]
        [InlineData("CompatLayer", false)]
        [InlineData("StateMismatch", false)]
        [InlineData("AccessDenied", false)]
        [InlineData("BinaryOrArch", false)]
        [InlineData("Other", false)]
        public void Install_guidance_only_for_missing_runtime(string problem, bool install)
        {
            var p = Enum.Parse<WebViewProblem>(problem);
            foreach (bool ko in new[] { false, true })
            {
                var text = WebViewFailureText.Build(p, unchecked((int)0x8007139F), "msg", @"C:\udf", @"C:\log.txt", Dpi, ko);
                Assert.Equal(install, WebViewFailureText.ShowsInstallLink(p));
                Assert.Equal(install, text.Contains(ko ? "설치되어 있지 않습니다" : "not installed on this PC", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void Compat_screen_lists_layers_and_separates_hkcu_from_hklm()
        {
            var layers = new[]
            {
                new CompatLayerEntry("HKCU", @"C:\u\msedgewebview2.exe", "HIGHDPIAWARE", true, true),
                new CompatLayerEntry("HKLM64", @"C:\m\msedgewebview2.exe", "DPIUNAWARE", true, false),
            };
            var en = WebViewFailureText.Build(WebViewProblem.CompatLayer, unchecked((int)0x8007139F), "m", "", "L", layers, ko: false);
            Assert.Contains("0x8007139F", en);
            Assert.Contains(@"[HKCU] C:\u\msedgewebview2.exe = HIGHDPIAWARE", en);
            Assert.Contains(@"[HKLM64] C:\m\msedgewebview2.exe = DPIUNAWARE", en);
            Assert.Contains("[Remove setting]", en);
            Assert.Contains("administrator", en);
            Assert.Contains("start it again", en);

            var onlyMachine = WebViewFailureText.Build(WebViewProblem.CompatLayer, unchecked((int)0x8007139F), "m", "", "L", layers.Skip(1).ToList(), ko: false);
            Assert.DoesNotContain("[Remove setting]", onlyMachine);
        }

        [Fact]
        public void Access_denied_shows_udf_and_other_shows_hresult_message_and_log()
        {
            var denied = WebViewFailureText.Build(WebViewProblem.AccessDenied, unchecked((int)0x80070005), "m", @"C:\my udf", @"C:\log.txt", None, false);
            Assert.Contains(@"C:\my udf", denied);
            var other = WebViewFailureText.Build(WebViewProblem.Other, unchecked((int)0x80004005), "boom", @"C:\udf", @"C:\log.txt", None, false);
            Assert.Contains("boom", other); Assert.Contains("0x80004005", other); Assert.Contains(@"C:\log.txt", other);
        }

        [Fact]
        public void Browser_arguments_only_when_enabled_and_quote_paths_with_spaces()
        {
            Assert.Null(WebViewDiagnostics.BrowserArguments(false, @"C:\a\b.log"));
            Assert.Equal(@"--enable-logging --v=0 --log-file=C:\a\b.log", WebViewDiagnostics.BrowserArguments(true, @"C:\a\b.log"));
            Assert.Equal("--enable-logging --v=0 --log-file=\"C:\\a b\\b.log\"", WebViewDiagnostics.BrowserArguments(true, @"C:\a b\b.log"));
        }

        // ── 앱 DPI 모드 회귀: ApplicationConfiguration.Initialize가 PerMonitorV2로 설정하는가 ────────────────

        [Fact]
        public void App_configuration_sets_PerMonitorV2()
        {
            var type = typeof(AgentChatPanel).Assembly.GetType("NanumCsvViewer.ApplicationConfiguration", throwOnError: true)!;
            var init = type.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!;
            var il = init.GetMethodBody()!.GetILAsByteArray()!;
            var module = init.Module;
            var setMode = typeof(Application).GetMethod(nameof(Application.SetHighDpiMode), new[] { typeof(HighDpiMode) })!;

            // ldc.i4.<mode> 바로 뒤의 call SetHighDpiMode를 찾는다.
            int? mode = null;
            for (int i = 1; i + 4 < il.Length; i++)
            {
                if (il[i] != OpCodes.Call.Value) continue;
                if (module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) != setMode) continue;
                mode = il[i - 1] - OpCodes.Ldc_I4_0.Value;
                break;
            }
            Assert.Equal((int)HighDpiMode.PerMonitorV2, mode);
        }
    }
}
