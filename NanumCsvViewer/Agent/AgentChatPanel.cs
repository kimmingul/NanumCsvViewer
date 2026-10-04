using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NanumCsvViewer.Agent
{
    /// <summary>
    /// 에이전트 채팅 패널: WebView2 안에 RAD Agent에서 이식한 채팅 페이지(내장 자산)를 띄운다.
    /// 호스트(ChatController)와는 <see cref="IChatPage"/>의 JSON 메시지로만 통신한다.
    /// 페이지의 <c>ready</c> 전에 Post된 메시지는 쌓아 두었다가 ready 뒤에 순서대로 보낸다.
    /// WebView2 런타임이 없으면 설치 안내 라벨을 보인다.
    /// </summary>
    public sealed class AgentChatPanel : UserControl, IChatPage
    {
        private const string PageUrl = "https://" + ChatTheme.HostName + "/chat.html";
        private const string RuntimeInstallUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        private const int MaxRendererReloads = 3;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        private readonly ChatPostQueue _queue;
        private WebView2? _web;
        private Panel? _fallback;
        private bool _initStarted;
        private int _reloads;
        private string? _strings;
        private string? _theme;
        private bool _dark = true;

        public AgentChatPanel()
        {
            _queue = new ChatPostQueue(SendNow);
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(0x1e, 0x1e, 0x1e);
        }

        /// <summary>page→host 메시지(ready·submit·abort …). UI 스레드에서 발생. openUrl·copy는 패널이 직접 처리하므로 오지 않는다.</summary>
        public event Action<JsonElement>? Received;

        /// <summary>페이지가 ready를 보냈고 쌓인 메시지를 모두 내보냈다.</summary>
        public bool IsReady => _queue.IsReady;

        /// <summary>Evergreen WebView2 런타임이 설치되어 있는가. version은 설치된 런타임 버전.</summary>
        public static bool IsWebView2Available(out string? version)
        {
            try
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrEmpty(version);
            }
            catch (Exception)
            {
                // WebView2RuntimeNotFoundException, 손상된 설치 등.
                version = null;
                return false;
            }
        }

        // ── IChatPage ──────────────────────────────────────────────────────────────

        public void Post(string json)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(() => Post(json)); return; }
            _queue.Post(json);
        }

        // ── 테마·언어 ──────────────────────────────────────────────────────────────

        /// <summary>어둡거나 밝은 팔레트와 글꼴을 페이지에 보낸다(ready 전이면 ready 직후 먼저 보낸다).</summary>
        public void ApplyTheme(bool dark, Font uiFont)
        {
            _dark = dark;
            var palette = ChatTheme.Palette(dark);
            BackColor = ColorTranslator.FromHtml(palette["bg"]);
            if (_web is not null) _web.DefaultBackgroundColor = BackColor;
            _theme = ChatTheme.ThemeMessage(dark, uiFont.Name, uiFont.SizeInPoints);
            if (_queue.IsReady) Post(_theme);
        }

        /// <summary>페이지 문구 언어("ko"|"en").</summary>
        public void SetLanguage(string language)
        {
            _strings = ChatStrings.Message(typeof(AgentChatPanel).Assembly, language);
            if (_queue.IsReady) Post(_strings);
        }

        /// <summary>입력창에 포커스.</summary>
        public void FocusInput()
        {
            _web?.Focus();
            Post("{\"t\":\"focusInput\"}");
        }

        // ── 초기화 ─────────────────────────────────────────────────────────────────

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!DesignMode && !_initStarted) _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            _initStarted = true;
            if (!IsWebView2Available(out _))
            {
                ShowFallback(LT(
                    "The AI chat needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.",
                    "AI 채팅에는 Microsoft Edge WebView2 런타임이 필요하지만 이 PC에는 설치되어 있지 않습니다."));
                return;
            }
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var assetDir = ChatAssetStore.Extract(typeof(AgentChatPanel).Assembly,
                    Path.Combine(local, "NanumCsvViewer", "chat"), AppInfo.Version);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(local, "NanumCsvViewer", "WebView2"));

                var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor, AllowExternalDrop = false };
                Controls.Add(web);
                _web = web;
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;

                var s = core.Settings;
#if DEBUG
                s.AreDevToolsEnabled = true;
                s.AreDefaultContextMenusEnabled = true;
#else
                s.AreDevToolsEnabled = false;
                s.AreDefaultContextMenusEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
#endif
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsSwipeNavigationEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsGeneralAutofillEnabled = false;

                core.SetVirtualHostNameToFolderMapping(ChatTheme.HostName, assetDir, CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += OnNewWindowRequested;
                core.WebMessageReceived += OnWebMessageReceived;
                core.ProcessFailed += OnProcessFailed;
                core.Navigate(PageUrl);
            }
            catch (Exception ex)
            {
                if (_web is not null) { Controls.Remove(_web); _web.Dispose(); _web = null; }
                ShowFallback(LT("The chat page could not be started: ", "채팅 화면을 시작하지 못했습니다: ") + ex.Message);
            }
        }

        private void ShowFallback(string message)
        {
            _initStarted = false;
            if (_fallback is not null) { Controls.Remove(_fallback); _fallback.Dispose(); }
            var palette = ChatTheme.Palette(_dark);
            var fg = ColorTranslator.FromHtml(palette["fg"]);
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Padding = new Padding(16) };
            var label = new Label
            {
                Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(10000, 0), ForeColor = fg,
                Text = message + Environment.NewLine + Environment.NewLine +
                       LT("Install it, then press Retry.", "설치한 뒤 [다시 시도]를 누르세요."),
            };
            var link = new LinkLabel
            {
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8),
                Text = RuntimeInstallUrl, LinkColor = ColorTranslator.FromHtml(palette["link"]),
                ActiveLinkColor = ColorTranslator.FromHtml(palette["link"]),
            };
            link.LinkClicked += (_, _) => OpenInBrowser(RuntimeInstallUrl);
            var retry = new Button { Dock = DockStyle.Top, AutoSize = true, Text = LT("Retry", "다시 시도"), FlatStyle = FlatStyle.System };
            retry.Click += (_, _) =>
            {
                if (_fallback is not null) { Controls.Remove(_fallback); _fallback.Dispose(); _fallback = null; }
                if (!_initStarted) _ = InitializeAsync();
            };
            // Dock=Top은 추가한 역순으로 쌓인다.
            panel.Controls.Add(retry);
            panel.Controls.Add(link);
            panel.Controls.Add(label);
            _fallback = panel;
            Controls.Add(panel);
            panel.BringToFront();
        }

        // ── WebView2 이벤트 ────────────────────────────────────────────────────────

        private static bool IsHostUri(string uri) =>
            uri.StartsWith("https://" + ChatTheme.HostName + "/", StringComparison.OrdinalIgnoreCase);

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!IsHostUri(e.Uri))
            {
                e.Cancel = true;
                OpenInBrowser(e.Uri);
                return;
            }
            // 페이지가 (다시) 로드된다: 새 ready까지 메시지를 쌓는다.
            _queue.Reset();
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenInBrowser(e.Uri);
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            // 렌더러가 죽으면 페이지 상태가 사라진다: 다시 로드하면 페이지가 ready를 다시 보내고 호스트가 상태를 다시 보낸다.
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)) return;
            if (++_reloads > MaxRendererReloads)
            {
                ShowFallback(LT("The chat page keeps crashing.", "채팅 화면이 계속 비정상 종료됩니다."));
                return;
            }
            _queue.Reset();
            try { _web?.CoreWebView2.Reload(); } catch (Exception) { /* 닫히는 중 */ }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonElement msg;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                msg = doc.RootElement.Clone();
            }
            catch (JsonException) { return; }
            if (msg.ValueKind != JsonValueKind.Object ||
                !msg.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return;

            switch (t.GetString())
            {
                case "ready":
                    // 문구·테마가 먼저, 그다음 호스트가 쌓아 둔 메시지.
                    _strings ??= ChatStrings.Message(typeof(AgentChatPanel).Assembly, Loc.CurrentLanguage);
                    _theme ??= ChatTheme.ThemeMessage(_dark, null, 9f);
                    SendNow(_strings);
                    SendNow(_theme);
                    _queue.MarkReady();
                    break;
                case "openUrl":
                    if (msg.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                        OpenInBrowser(url.GetString());
                    return;
                case "copy":
                    if (msg.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        try { Clipboard.SetText(text.GetString() ?? ""); }
                        catch (Exception) { /* 클립보드를 다른 프로그램이 잡고 있음 */ }
                    }
                    return;
            }
            Received?.Invoke(msg);
        }

        private void SendNow(string json)
        {
            var core = _web?.CoreWebView2;
            if (core is null) return;
            try { core.PostWebMessageAsJson(json); }
            catch (Exception) { /* 닫히는 중 */ }
        }

        /// <summary>http/https만 기본 브라우저로 연다(file:·javascript: 등은 무시).</summary>
        internal static bool OpenInBrowser(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return true;
            }
            catch (Exception) { return false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _queue.Clear();
            base.Dispose(disposing);
        }
    }
}
