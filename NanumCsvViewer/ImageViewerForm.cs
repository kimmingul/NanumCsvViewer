using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer
{
    /// <summary>
    /// 그림 보기 창: png·jpg·gif·bmp는 확대/축소·창에 맞춤·끌어서 이동, svg·webp는 WebView2, pdf는 시스템 기본 프로그램으로 연다.
    /// 복사·다른 이름으로 저장·폴더 열기를 제공하고 파일이 바뀌면 자동으로 다시 읽는다. 경로당 창 하나.
    /// 파일은 읽을 때 메모리로 복사해 잠그지 않는다(에이전트가 같은 이름으로 다시 저장할 수 있다).
    /// </summary>
    internal sealed class ImageViewerForm : Form
    {
        internal const long MaxBytes = 100L * 1024 * 1024;
        private const string ImageHost = "nanumcsv-img.local";
        private static readonly Dictionary<string, ImageViewerForm> s_open = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private ThemePalette _palette;
        private readonly ToolStrip _bar = new();
        private readonly ToolStripLabel _status = new();
        private readonly DebouncedFileWatcher? _watcher;
        private readonly bool _useWeb;
        // 비트맵 모드
        private Panel? _scroll;
        private PictureBox? _pic;
        private MemoryStream? _imageData;
        private double _zoom = 1;
        private bool _fit = true;
        private Point _dragFrom;
        // 웹 모드(svg·webp)
        private WebView2? _web;
        private double _webZoom = 1;

        /// <summary>그림(png·jpg·gif·bmp·webp·svg)은 보기 창으로, pdf는 시스템 기본 프로그램으로 연다.</summary>
        public static ViewerShowResult ShowFile(IWin32Window owner, string path, ThemePalette palette)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception) { return new ViewerShowResult(false, ViewerSupport.LT("Invalid path: ", "경로가 올바르지 않습니다: ") + path); }
            if (!File.Exists(full)) return new ViewerShowResult(false, ViewerSupport.LT("File not found: ", "파일을 찾을 수 없습니다: ") + full);
            string ext = Path.GetExtension(full).ToLowerInvariant();

            if (ext == ".pdf")
            {
                return ViewerSupport.OpenWithShell(full)
                    ? new ViewerShowResult(true, ViewerSupport.LT("PDF opened with the system default app.", "PDF를 시스템 기본 프로그램으로 열었습니다."))
                    : new ViewerShowResult(false, ViewerSupport.LT("No default app could open the PDF: ", "PDF를 열 기본 프로그램이 없습니다: ") + full);
            }
            if (!AgentWorkspace.IsImageFile(full))
                return new ViewerShowResult(false, ViewerSupport.LT("Not a picture or PDF (png, jpg, gif, bmp, webp, svg, pdf): ", "그림이나 PDF(png, jpg, gif, bmp, webp, svg, pdf)가 아닙니다: ") + full);

            if (s_open.TryGetValue(full, out var existing) && !existing.IsDisposed)
            {
                existing.ApplyPalette(palette);
                existing.Reload();
                if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
                existing.Activate();
                return new ViewerShowResult(true, ViewerSupport.LT("Picture window refreshed.", "그림 창을 새로 고쳤습니다."));
            }
            try
            {
                var form = new ImageViewerForm(full, palette);
                s_open[full] = form;
                form.FormClosed += (_, _) => s_open.Remove(full);
                form.Show(owner);
                return new ViewerShowResult(true, ViewerSupport.LT("Opened the picture window.", "그림 창을 열었습니다."));
            }
            catch (Exception ex)
            {
                s_open.Remove(full);
                return new ViewerShowResult(false, ViewerSupport.LT("Could not open the picture: ", "그림을 열지 못했습니다: ") + ex.Message);
            }
        }

        private ImageViewerForm(string path, ThemePalette palette)
        {
            _path = path;
            _palette = palette;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            _useWeb = ext is ".svg" or ".webp";
            Text = Path.GetFileName(path) + " — " + ViewerSupport.LT("Picture", "그림");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(900, 650);
            MinimumSize = new Size(360, 260);
            ShowIcon = false;
            KeyPreview = true;

            _bar.GripStyle = ToolStripGripStyle.Hidden;
            AddButton(ViewerSupport.LT("Fit", "맞춤"), (_, _) => SetFit());
            AddButton("100%", (_, _) => SetZoom(1));
            AddButton("−", (_, _) => SetZoom(CurrentZoom / 1.25));
            AddButton("+", (_, _) => SetZoom(CurrentZoom * 1.25));
            _bar.Items.Add(new ToolStripSeparator());
            if (!_useWeb) AddButton(ViewerSupport.LT("Copy", "복사"), (_, _) => CopyImage());
            AddButton(ViewerSupport.LT("Save As…", "다른 이름으로 저장…"), (_, _) => SaveAs());
            AddButton(ViewerSupport.LT("Open folder", "폴더 열기"), (_, _) => ViewerSupport.RevealInExplorer(_path));
            AddButton(ViewerSupport.LT("Reload", "새로 고침"), (_, _) => Reload());
            _status.Alignment = ToolStripItemAlignment.Right;
            _status.ForeColor = Color.Gray;
            _bar.Items.Add(_status);

            if (_useWeb)
            {
                Controls.Add(_bar);
                _ = InitializeWebAsync();
            }
            else
            {
                _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                _pic = new PictureBox { SizeMode = PictureBoxSizeMode.StretchImage, Location = Point.Empty };
                _scroll.Controls.Add(_pic);
                _scroll.Resize += (_, _) => { if (_fit) ApplyZoom(); };
                _scroll.MouseWheel += OnWheel;
                _pic.MouseWheel += OnWheel;
                _pic.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _dragFrom = e.Location; _scroll.Focus(); } };
                _pic.MouseMove += OnDrag;
                _pic.MouseDoubleClick += (_, _) => SetFit();
                Controls.Add(_scroll);
                Controls.Add(_bar);
            }
            ApplyPalette(palette);
            Reload();
            try
            {
                _watcher = new DebouncedFileWatcher(Path.GetDirectoryName(path)!, p => string.Equals(p, _path, StringComparison.OrdinalIgnoreCase));
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
            foreach (ToolStripItem item in _bar.Items) if (item is ToolStripButton) item.ForeColor = palette.Text;
            if (_scroll is not null) _scroll.BackColor = palette.Surface;
            if (_web is not null) _web.DefaultBackgroundColor = palette.Surface;
        }

        private double CurrentZoom => _useWeb ? _webZoom : _zoom;

        // ── 읽기 ─────────────────────────────────────────────────────────────────

        private void Reload()
        {
            if (_useWeb) { ReloadWeb(); return; }
            try
            {
                byte[]? bytes = ViewerSupport.ReadShared(_path, MaxBytes);
                if (bytes == null) { _status.Text = ViewerSupport.LT("The file is larger than 100 MB.", "파일이 100 MB를 넘습니다."); return; }
                var data = new MemoryStream(bytes);
                Image image = Image.FromStream(data); // GIF 애니메이션을 위해 스트림을 유지한다
                var old = _imageData;
                var oldImage = _pic!.Image;
                _imageData = data;
                _pic.Image = image;
                oldImage?.Dispose();
                old?.Dispose();
                ApplyZoom();
                _status.Text = $"{image.Width} × {image.Height} px · {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException)
            {
                _status.Text = ViewerSupport.LT("Cannot read the picture: ", "그림을 읽을 수 없습니다: ") + ex.Message;
            }
        }

        // ── 비트맵 확대/축소 ─────────────────────────────────────────────────────

        private void SetFit()
        {
            if (_useWeb) { _webZoom = 1; ApplyWebZoom(); return; }
            _fit = true;
            ApplyZoom();
        }

        private void SetZoom(double zoom)
        {
            zoom = Math.Clamp(zoom, 0.05, 16);
            if (_useWeb) { _webZoom = zoom; ApplyWebZoom(); return; }
            _fit = false;
            _zoom = zoom;
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            if (_pic?.Image is not { } img || _scroll is null) return;
            if (_fit)
            {
                double fit = Math.Min((double)Math.Max(1, _scroll.ClientSize.Width) / img.Width, (double)Math.Max(1, _scroll.ClientSize.Height) / img.Height);
                _zoom = Math.Min(1, fit); // 작은 그림은 키우지 않는다
            }
            var size = new Size(Math.Max(1, (int)Math.Round(img.Width * _zoom)), Math.Max(1, (int)Math.Round(img.Height * _zoom)));
            _pic.Size = size;
            // 창보다 작으면 가운데에.
            int x = Math.Max(0, (_scroll.ClientSize.Width - size.Width) / 2), y = Math.Max(0, (_scroll.ClientSize.Height - size.Height) / 2);
            _pic.Location = new Point(x - _scroll.HorizontalScroll.Value, y - _scroll.VerticalScroll.Value);
            _status.Text = $"{img.Width} × {img.Height} px · {(int)Math.Round(_zoom * 100)}%";
        }

        private void OnWheel(object? sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == 0) return;
            SetZoom(CurrentZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
            if (e is HandledMouseEventArgs h) h.Handled = true;
        }

        private void OnDrag(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _scroll is null) return;
            int dx = e.X - _dragFrom.X, dy = e.Y - _dragFrom.Y;
            _scroll.AutoScrollPosition = new Point(-_scroll.AutoScrollPosition.X - dx, -_scroll.AutoScrollPosition.Y - dy);
        }

        // ── 웹 모드(svg·webp) ────────────────────────────────────────────────────

        private bool _pageReady;

        private async Task InitializeWebAsync()
        {
            try
            {
                if (!AgentChatPanel.IsWebView2Available(out _))
                {
                    _status.Text = ViewerSupport.LT("svg/webp need the WebView2 runtime.", "svg/webp는 WebView2 런타임이 필요합니다.");
                    return;
                }
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
                core.Settings.IsStatusBarEnabled = false;
                core.SetVirtualHostNameToFolderMapping(ImageHost, Path.GetDirectoryName(_path)!, CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (_, e) =>
                {
                    if (_pageReady && !e.Uri.StartsWith("https://" + ImageHost + "/", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        e.Cancel = true;
                };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                _pageReady = true;
                ReloadWeb();
            }
            catch (Exception ex)
            {
                _status.Text = ViewerSupport.LT("Cannot show the picture: ", "그림을 표시할 수 없습니다: ") + ex.Message;
            }
        }

        private void ReloadWeb()
        {
            var core = _web?.CoreWebView2;
            if (core is null) return;
            string name = Uri.EscapeDataString(Path.GetFileName(_path));
            string bg = ViewerSupport.IsDark(_palette) ? "#1e1e1e" : "#ffffff";
            string html = "<!doctype html><meta charset=utf-8><style>html,body{margin:0;height:100%;background:" + bg + "}" +
                          "body{display:flex;align-items:center;justify-content:center}" +
                          "img{max-width:100%;max-height:100vh;object-fit:contain}</style>" +
                          $"<img src=\"https://{ImageHost}/{name}?v={DateTime.UtcNow.Ticks}\">";
            core.NavigateToString(html);
            ApplyWebZoom();
            _status.Text = ViewerSupport.LT("Reloaded ", "다시 읽음 ") + DateTime.Now.ToString("HH:mm:ss");
        }

        private void ApplyWebZoom()
        {
            if (_web is null) return;
            try { _web.ZoomFactor = _webZoom; } catch (Exception) { }
        }

        // ── 단추 ─────────────────────────────────────────────────────────────────

        private void CopyImage()
        {
            if (_pic?.Image is not { } img) return;
            try { Clipboard.SetImage(img); _status.Text = ViewerSupport.LT("Copied.", "복사했습니다."); }
            catch (Exception) { _status.Text = ViewerSupport.LT("The clipboard is busy.", "클립보드를 사용할 수 없습니다."); }
        }

        private void SaveAs()
        {
            string ext = Path.GetExtension(_path);
            using var dlg = new SaveFileDialog
            {
                Title = ViewerSupport.LT("Save picture as", "그림을 다른 이름으로 저장"),
                FileName = Path.GetFileName(_path),
                InitialDirectory = Path.GetDirectoryName(_path),
                Filter = $"{ext.TrimStart('.').ToUpperInvariant()} (*{ext})|*{ext}|{ViewerSupport.LT("All files (*.*)|*.*", "모든 파일 (*.*)|*.*")}",
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string target = Path.GetFullPath(dlg.FileName);
                if (!string.Equals(target, _path, StringComparison.OrdinalIgnoreCase)) File.Copy(_path, target, overwrite: true);
                _status.Text = ViewerSupport.LT("Saved.", "저장했습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C && !_useWeb) { CopyImage(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) Close();
            base.OnKeyDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watcher?.Dispose();
                s_open.Remove(_path);
                _pic?.Image?.Dispose();
                _imageData?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
