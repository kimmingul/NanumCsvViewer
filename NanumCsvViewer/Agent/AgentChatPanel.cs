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
    public sealed class AgentChatPanel : UserControl, IChatPage, IChatOutputHost
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
        private string? _outputFolder;

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

        // ── 결과 폴더(채팅 안 그림) ───────────────────────────────────────────────

        /// <summary>
        /// 채팅 페이지가 그림을 읽을 수 있는 폴더(null이면 없음). 페이지의 그림 주소 https://nanumcsv-out.local/&lt;상대 경로&gt;는 네트워크로 가지 않고
        /// 여기서 가로채 이 폴더 안의 그림 파일만 돌려준다(폴더 밖·그림이 아닌 파일은 404). 폴더는 언제든 바뀔 수 있다.
        /// (WebView2 가상 호스트 매핑은 페이지를 연 뒤에 추가하면 적용되지 않아 실제 WebView2에서 확인하고 이 방식으로 바꿨다.)
        /// </summary>
        public void SetOutputFolder(string? folder)
        {
            if (InvokeRequired) { BeginInvoke(() => SetOutputFolder(folder)); return; }
            _outputFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
        }

        private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var core = _web?.CoreWebView2;
            if (core is null) return;
            var (status, reason, bytes, type) = ReadOutputPicture(_outputFolder, e.Request.Uri);
            Stream? body = bytes is null ? null : new MemoryStream(bytes, writable: false);
            e.Response = core.Environment.CreateWebResourceResponse(body, status, reason,
                (type is null ? "" : "Content-Type: " + type + "\r\n") + "Cache-Control: no-store");
        }

        private static readonly Dictionary<string, string> PictureTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
            [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
        };

        /// <summary>그림 요청 주소 → (상태, 문구, 본문, 형식). 출력 폴더 안의 그림 파일만 200, 그 밖은 404.</summary>
        internal static (int Status, string Reason, byte[]? Body, string? ContentType) ReadOutputPicture(string? outputFolder, string requestUri)
        {
            const int MaxBytes = 50 * 1024 * 1024;
            try
            {
                if (outputFolder is null || !Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)
                    || !string.Equals(uri.Host, ChatController.OutputHost, StringComparison.OrdinalIgnoreCase))
                    return (404, "Not Found", null, null);
                string relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                string? full = AgentWorkspace.ResolveInside(outputFolder, relative);
                if (full is null || !PictureTypes.TryGetValue(Path.GetExtension(full), out var type) || !File.Exists(full))
                    return (404, "Not Found", null, null);
                var bytes = ViewerSupport.ReadShared(full, MaxBytes);
                return bytes is null ? (404, "Not Found", null, null) : (200, "OK", bytes, type);
            }
            catch (Exception)
            {
                return (404, "Not Found", null, null);
            }
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

                // 끌어놓기는 켠다: 페이지가 파일 끌기를 받아 postMessageWithAdditionalObjects로 넘기고(OnWebMessageReceived의 attachDrop),
                // 페이지는 file: 이동을 막는다. 끄면 WebView2가 드롭 대상 자체를 거절해 부모 컨트롤도 드롭을 받지 못한다(실제 확인).
                var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor, AllowExternalDrop = true };
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
                core.AddWebResourceRequestedFilter("https://" + ChatController.OutputHost + "/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnWebResourceRequested;
                core.NavigationStarting += OnNavigationStarting;
                core.NewWindowRequested += OnNewWindowRequested;
                core.WebMessageReceived += OnWebMessageReceived;
                // WinForms 래퍼는 컨트롤러를 공개하지 않는다. 없으면(래퍼 버전이 바뀌면) 단축키 전달만 빠지고 나머지는 그대로 동작한다.
                if (typeof(WebView2).GetField("_coreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(web) is CoreWebView2Controller controller)
                    controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
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

        /// <summary>
        /// 채팅 입력창에 포커스가 있으면 WebView2가 키를 먼저 받아 메뉴 단축키(Ctrl+, · F4 · Ctrl+Shift+W …)가 폼에 닿지 않는다.
        /// 앱 단축키로 볼 수 있는 키만 폼에 넘기고(폼이 처리하면 WebView2에는 주지 않는다), 입력창의 편집 키는 그대로 둔다.
        /// </summary>
        internal static bool ForwardsToApp(Keys key)
        {
            var code = key & Keys.KeyCode;
            var mods = key & Keys.Modifiers;
            if (code >= Keys.F1 && code <= Keys.F12) return true;
            if ((mods & (Keys.Control | Keys.Alt)) == 0) return false;
            if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.None) return false;
            bool plain = code is (>= Keys.A and <= Keys.Z) or (>= Keys.D0 and <= Keys.D9) or Keys.Tab or (>= Keys.Oem1 and <= Keys.Oem102);
            if (!plain) return false;   // Enter · 방향키 · Backspace · Delete · Home/End … 는 입력창이 쓴다
            if ((mods & Keys.Control) != 0 && code is Keys.A or Keys.C or Keys.V or Keys.X or Keys.Z or Keys.Y) return false;   // 입력창의 선택·복사·붙여넣기·되돌리기
            return true;
        }

        private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
            if (!ForwardsToApp((Keys)e.VirtualKey | ModifierKeys) || FindForm() is not { } form) return;
            var msg = new Message
            {
                HWnd = form.Handle,
                Msg = e.KeyEventKind == CoreWebView2KeyEventKind.SystemKeyDown ? 0x0104 : 0x0100,   // WM_SYSKEYDOWN : WM_KEYDOWN
                WParam = (IntPtr)e.VirtualKey,
                LParam = IntPtr.Zero,
            };
            if (form.PreProcessMessage(ref msg)) e.Handled = true;
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
                case "attachDrop":
                    // 끌어놓은 파일·폴더의 경로는 브라우저가 건 File 객체(AdditionalObjects)에서만 읽는다 — 페이지가 글로 보낸 경로는 믿지 않는다.
                    // 컨트롤러가 받는 attachPaths는 이 패널만 만든다(페이지가 직접 보낸 같은 이름의 메시지는 아래 case에서 버린다).
                    var dropped = DroppedPaths(e.AdditionalObjects);
                    if (dropped.Count > 0)
                        Received?.Invoke(JsonSerializer.SerializeToElement(new { t = "attachPaths", paths = dropped }));
                    return;
                case "attachPaths":
                    return;
            }
            Received?.Invoke(msg);
        }

        /// <summary>끌어놓은 File 객체들의 전체 경로(경로를 알 수 없는 항목은 제외).</summary>
        internal static List<string> DroppedPaths(IEnumerable<object>? objects)
        {
            var paths = new List<string>();
            if (objects is null) return paths;
            foreach (var o in objects)
                if (o is CoreWebView2File { Path: { Length: > 0 } path }) paths.Add(path);
            return paths;
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
