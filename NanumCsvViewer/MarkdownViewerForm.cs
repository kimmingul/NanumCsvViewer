using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer
{
    /// <summary>
    /// 마크다운 보고서 보기 창. 에이전트가 결과 폴더에 쓴 .md를 채팅과 같은 렌더러(표·코드 블록·같은 폴더의 그림)로 보여 주고,
    /// 다른 이름으로 저장·폴더 열기·복사를 제공하며, 파일이 바뀌면 읽기 위치를 유지한 채 자동으로 다시 그린다.
    /// 경로당 창 하나(이미 열려 있으면 다시 그리고 앞으로). WebView2 런타임이 없으면 원문 텍스트로 보여 준다.
    /// </summary>
    internal sealed class MarkdownViewerForm : Form
    {
        internal const long MaxBytes = 20L * 1024 * 1024;
        private const string DocHost = "nanumcsv-doc.local";
        private static readonly Dictionary<string, MarkdownViewerForm> s_open = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private ThemePalette _palette;
        private readonly ToolStrip _bar = new();
        private readonly ToolStripLabel _status = new();
        private readonly DebouncedFileWatcher? _watcher;
        private WebView2? _web;
        private TextBox? _plain;
        private bool _pageReady;
        private string _text = "";
        private long _version;

        /// <summary>마크다운(.md·.markdown·.txt)을 보기 창으로 연다. 에이전트가 쓰는 중인 파일도 읽는다.</summary>
        public static ViewerShowResult ShowFile(IWin32Window owner, string path, ThemePalette palette)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return new ViewerShowResult(false, ViewerSupport.LT("Invalid path: ", "경로가 올바르지 않습니다: ") + path); }
            if (!File.Exists(full)) return new ViewerShowResult(false, ViewerSupport.LT("File not found: ", "파일을 찾을 수 없습니다: ") + full);
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is not (".md" or ".markdown" or ".txt"))
                return new ViewerShowResult(false, ViewerSupport.LT("Not a Markdown file (.md, .markdown, .txt): ", "마크다운 파일(.md, .markdown, .txt)이 아닙니다: ") + full);

            if (s_open.TryGetValue(full, out var existing) && !existing.IsDisposed)
            {
                existing.ApplyPalette(palette);
                existing.Reload();
                if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
                existing.Activate();
                return new ViewerShowResult(true, ViewerSupport.LT("Report window refreshed.", "보고서 창을 새로 고쳤습니다."));
            }
            try
            {
                var form = new MarkdownViewerForm(full, palette);
                s_open[full] = form;
                form.FormClosed += (_, _) => s_open.Remove(full);
                form.Show(owner);
                return new ViewerShowResult(true, ViewerSupport.LT("Opened the report window.", "보고서 창을 열었습니다."));
            }
            catch (Exception ex)
            {
                s_open.Remove(full);
                return new ViewerShowResult(false, ViewerSupport.LT("Could not open the report: ", "보고서를 열지 못했습니다: ") + ex.Message);
            }
        }

        private MarkdownViewerForm(string path, ThemePalette palette)
        {
            _path = path;
            _palette = palette;
            Text = Path.GetFileName(path) + " — " + ViewerSupport.LT("Report", "보고서");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 700);
            MinimumSize = new Size(480, 320);
            ShowIcon = false;
            ShowInTaskbar = true;

            _bar.GripStyle = ToolStripGripStyle.Hidden;
            AddButton(ViewerSupport.LT("Reload", "새로 고침"), (_, _) => Reload());
            AddButton(ViewerSupport.LT("Save As…", "다른 이름으로 저장…"), (_, _) => SaveAs());
            AddButton(ViewerSupport.LT("Open folder", "폴더 열기"), (_, _) => ViewerSupport.RevealInExplorer(_path));
            AddButton(ViewerSupport.LT("Copy Markdown", "마크다운 복사"), (_, _) => CopyText());
            _status.Alignment = ToolStripItemAlignment.Right;
            _status.ForeColor = Color.Gray;
            _bar.Items.Add(_status);
            Controls.Add(_bar);

            ApplyPalette(palette);
            Reload();
            try
            {
                string dir = Path.GetDirectoryName(path)!;
                _watcher = new DebouncedFileWatcher(dir, p =>
                    string.Equals(p, _path, StringComparison.OrdinalIgnoreCase) || Agent.AgentWorkspace.IsImageFile(p));
                _watcher.Changed += Reload;
                _watcher.Start();
            }
            catch (Exception) { /* 자동 새로고침 없이도 동작 */ }
        }

        private void AddButton(string text, EventHandler click)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.Click += click;
            _bar.Items.Add(b);
        }

        private void ApplyPalette(ThemePalette palette)
        {
            _palette = palette;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            _bar.BackColor = palette.ToolStrip;
            _bar.ForeColor = palette.Text;
            foreach (ToolStripItem item in _bar.Items) if (item is ToolStripButton) item.ForeColor = palette.Text;
            if (_plain is not null) { _plain.BackColor = palette.Surface; _plain.ForeColor = palette.Text; }
            if (_web is not null) _web.DefaultBackgroundColor = palette.Surface;
            if (_pageReady) PostTheme();
        }

        // ── 읽기·그리기 ──────────────────────────────────────────────────────────

        private void Reload()
        {
            string? error = null;
            try
            {
                byte[]? bytes = ViewerSupport.ReadShared(_path, MaxBytes);
                if (bytes == null) error = ViewerSupport.LT("The file is larger than 20 MB.", "파일이 20 MB를 넘습니다.");
                else _text = ViewerSupport.DecodeText(bytes);
            }
            catch (Exception ex) { error = ex.Message; }
            _version++;
            _status.Text = error ?? ViewerSupport.LT("Updates automatically when the file changes", "파일이 바뀌면 자동으로 다시 그립니다") + " · " + DateTime.Now.ToString("HH:mm:ss");
            if (error != null) _text = "> " + error;
            Render();
        }

        private void Render()
        {
            if (_plain is not null) { _plain.Text = _text.Replace("\r\n", "\n").Replace("\n", "\r\n"); return; }
            if (_web is null) { _ = InitializeWebAsync(); return; }
            if (_pageReady) PostDoc();
        }

        private bool _initStarted;

        private async Task InitializeWebAsync()
        {
            if (_initStarted) return;
            _initStarted = true;
            try
            {
                if (!AgentChatPanel.IsWebView2Available(out _)) { ShowPlain(); return; }
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string assets = ChatAssetStore.Extract(typeof(AgentChatPanel).Assembly, Path.Combine(local, "NanumCsvViewer", "chat"), AppInfo.Version);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(local, "NanumCsvViewer", "WebView2"));
                if (IsDisposed) return;
                var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = _palette.Surface, AllowExternalDrop = false };
                Controls.Add(web);
                web.BringToFront();
                _bar.BringToFront();
                _web = web;
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true; // 복사
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = true;
                core.SetVirtualHostNameToFolderMapping(ChatTheme.HostName, assets, CoreWebView2HostResourceAccessKind.DenyCors);
                core.SetVirtualHostNameToFolderMapping(DocHost, Path.GetDirectoryName(_path)!, CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (_, e) =>
                {
                    if (!e.Uri.StartsWith("https://" + ChatTheme.HostName + "/", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; AgentChatPanel.OpenInBrowser(e.Uri); }
                    else _pageReady = false;
                };
                core.NewWindowRequested += (_, e) => { e.Handled = true; AgentChatPanel.OpenInBrowser(e.Uri); };
                core.WebMessageReceived += OnWebMessage;
                core.Navigate("https://" + ChatTheme.HostName + "/viewer.html");
            }
            catch (Exception ex)
            {
                if (_web is not null) { Controls.Remove(_web); _web.Dispose(); _web = null; }
                ShowPlain();
                _status.Text = ViewerSupport.LT("Rendering unavailable: ", "그림 표시 불가: ") + ex.Message;
            }
        }

        private void ShowPlain()
        {
            _plain = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9.5f), BorderStyle = BorderStyle.None,
                BackColor = _palette.Surface, ForeColor = _palette.Text,
            };
            Controls.Add(_plain);
            _plain.BringToFront();
            _bar.BringToFront();
            Render();
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonElement msg;
            try { using var doc = JsonDocument.Parse(e.WebMessageAsJson); msg = doc.RootElement.Clone(); }
            catch (JsonException) { return; }
            if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return;
            string Str(string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            switch (t.GetString())
            {
                case "ready":
                    _pageReady = true;
                    PostRaw(ChatStrings.Message(typeof(AgentChatPanel).Assembly, Loc.CurrentLanguage));
                    PostTheme();
                    PostDoc();
                    break;
                case "openUrl":
                    AgentChatPanel.OpenInBrowser(Str("url"));
                    break;
                case "copy":
                    try { Clipboard.SetText(Str("text")); } catch (Exception) { }
                    break;
                case "openImage":
                    OpenSibling(Str("path"));
                    break;
                case "openFile":
                    OpenSibling(Str("path"));
                    break;
            }
        }

        /// <summary>보고서 안의 링크·그림 클릭: 같은 폴더 아래의 마크다운·그림·pdf만 연다(그 밖은 무시).</summary>
        private void OpenSibling(string relative)
        {
            string dir = Path.GetDirectoryName(_path)!;
            string? full = AgentWorkspace.ResolveInside(dir, relative);
            if (full == null || !File.Exists(full)) return;
            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is ".md" or ".markdown") ShowFile(this, full, _palette);
            else if (AgentWorkspace.IsImageFile(full) || ext == ".pdf") ImageViewerForm.ShowFile(this, full, _palette);
        }

        private void PostRaw(string json)
        {
            try { _web?.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception) { /* 닫히는 중 */ }
        }

        private void PostTheme() => PostRaw(ChatTheme.ThemeMessage(ViewerSupport.IsDark(_palette), Font.Name, Font.SizeInPoints));

        private void PostDoc() =>
            PostRaw(JsonSerializer.Serialize(new
            {
                t = "doc",
                text = _text,
                @base = "https://" + DocHost + "/",
                version = _version,
                name = Path.GetFileName(_path),
            }));

        // ── 단추 ─────────────────────────────────────────────────────────────────

        private void CopyText()
        {
            try { Clipboard.SetText(_text); _status.Text = ViewerSupport.LT("Copied.", "복사했습니다."); }
            catch (Exception) { _status.Text = ViewerSupport.LT("The clipboard is busy.", "클립보드를 사용할 수 없습니다."); }
        }

        private void SaveAs()
        {
            using var dlg = new SaveFileDialog
            {
                Title = ViewerSupport.LT("Save report as", "보고서를 다른 이름으로 저장"),
                FileName = Path.GetFileName(_path),
                InitialDirectory = Path.GetDirectoryName(_path),
                Filter = ViewerSupport.LT("Markdown (*.md)|*.md|All files (*.*)|*.*", "마크다운 (*.md)|*.md|모든 파일 (*.*)|*.*"),
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string target = Path.GetFullPath(dlg.FileName);
                if (!string.Equals(target, _path, StringComparison.OrdinalIgnoreCase)) File.Copy(_path, target, overwrite: true);
                var (copied, missing) = ViewerSupport.CopyReferencedImages(_text, Path.GetDirectoryName(_path)!, Path.GetDirectoryName(target)!);
                _status.Text = ViewerSupport.LT($"Saved. {copied} picture(s) copied next to it", $"저장했습니다. 그림 {copied}개를 함께 복사") +
                               (missing > 0 ? ViewerSupport.LT($", {missing} missing.", $", {missing}개는 없음.") : ".");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _watcher?.Dispose(); s_open.Remove(_path); }
            base.Dispose(disposing);
        }
    }
}
