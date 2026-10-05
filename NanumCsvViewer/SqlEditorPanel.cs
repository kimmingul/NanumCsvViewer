using System.Diagnostics;
using System.Runtime.InteropServices;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>"실행" 결과(임시 CSV로 쓴 질의 결과)가 준비됐을 때 호스트가 받는 이벤트 인자.</summary>
    internal sealed class SqlRunResultEventArgs(QueryResultInfo info, string sql) : EventArgs
    {
        public QueryResultInfo Info { get; } = info;
        /// <summary>실제로 실행한 SQL(선택 영역이 있었으면 그 부분).</summary>
        public string Sql { get; } = sql;
    }

    /// <summary>
    /// 작업 공간 SQL 편집기(재사용 컨트롤): 간단한 구문 색, 스키마·표·컬럼 자동완성(Ctrl+Space 또는 입력 중),
    /// 실행(Ctrl+Enter — 선택 영역이 있으면 그 부분만) → 결과를 임시 CSV로 쓰고 <see cref="ResultReady"/>,
    /// 미리보기(F5 — 앞 200행을 작은 표로), 취소, 진행 표시, 오류(DuckDB가 알려 주면 줄·열과 함께 편집기에서 해당 위치 선택).
    /// 엔진(<see cref="DataWorkspace"/>)은 <see cref="Workspace"/>로 연결한다. UI 스레드에서 쓴다.
    /// </summary>
    internal sealed class SqlEditorPanel : Panel
    {
        public const int PreviewRows = 200;

        private static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        private readonly RichTextBox _editor;
        private readonly Button _btnRun, _btnPreview, _btnCancel, _btnSaveView;
        private readonly Label _status, _error;
        private readonly ProgressBar _progress;
        private readonly DataGridView _grid;
        private readonly ListBox _popup;
        private readonly System.Windows.Forms.Timer _recolorTimer;
        private readonly ToolTip _tip = new();
        private ThemePalette _palette;
        private DataWorkspace? _workspace;
        private CancellationTokenSource? _cts;
        private CompletionResult? _completion;
        private bool _recoloring;
        private int _runCounter;

        /// <summary>실행 결과 CSV를 두는 폴더. 기본 %TEMP%\NanumCsvViewer\duck\results (하루 지난 파일은 시작할 때 정리).</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string ResultDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "NanumCsvViewer", "duck", "results");

        /// <summary>실행이 끝나 결과 CSV가 만들어졌다(호스트가 새 탭으로 연다).</summary>
        public event EventHandler<SqlRunResultEventArgs>? ResultReady;

        /// <summary>"뷰로 저장…" 요청(호스트가 이름을 묻고 <see cref="DataWorkspace.CreateView"/>를 부른다). 인자는 현재 SQL.</summary>
        public event EventHandler<string>? SaveAsViewRequested;

        /// <summary>실행·미리보기 직전에 호스트가 할 준비(바뀐 원본 다시 읽기, 새로 열린 탭 올리기). 취소 가능. 실패는 질의 오류와 같은 방식으로 보인다.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<CancellationToken, Task>? PrepareAsync { get; set; }

        public bool IsBusy { get; private set; }

        /// <summary>마지막 실행·미리보기의 오류 메시지(없으면 null).</summary>
        public string? LastError { get; private set; }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DataWorkspace? Workspace
        {
            get => _workspace;
            set { _workspace = value; UpdateButtons(); }
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Sql
        {
            get => _editor.Text;
            set { HideCompletion(); _editor.Text = value; Recolor(); }
        }

        public SqlEditorPanel(ThemePalette palette)
        {
            _palette = palette;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

            _editor = new RichTextBox
            {
                Dock = DockStyle.Fill, Name = "sqlEditor", AcceptsTab = true, DetectUrls = false, WordWrap = false, HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10f), ScrollBars = RichTextBoxScrollBars.Both,
                EnableAutoDragDrop = false, ShowSelectionMargin = false,
            };
            _error = new Label { Dock = DockStyle.Bottom, Height = 0, AutoSize = false, Name = "sqlError", Padding = new Padding(4, 2, 4, 2), Visible = false };
            var top = new Panel { Dock = DockStyle.Fill };
            top.Controls.Add(_editor);
            top.Controls.Add(_error);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, Name = "sqlPreviewGrid", ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            };
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, Name = "sqlSplit" };
            split.Panel1.Controls.Add(top);
            split.Panel2.Controls.Add(_grid);
            split.Panel1MinSize = 60;
            split.Panel2MinSize = 40;

            _btnRun = MakeButton("sqlRun", (_, _) => _ = RunAsync());
            _btnPreview = MakeButton("sqlPreview", (_, _) => _ = PreviewAsync());
            _btnCancel = MakeButton("sqlCancel", (_, _) => Cancel());
            _btnSaveView = MakeButton("sqlSaveView", (_, _) => SaveAsViewRequested?.Invoke(this, _editor.Text));
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2, 3, 2, 3), WrapContents = false, Name = "sqlToolbar" };
            bar.Controls.AddRange(new Control[] { _btnRun, _btnPreview, _btnCancel, _btnSaveView });

            _status = new Label { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Name = "sqlStatus", AutoEllipsis = true };
            _progress = new ProgressBar { Dock = DockStyle.Right, Width = 120, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false, Name = "sqlProgress" };
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 24, Name = "sqlStatusBar" };
            statusBar.Controls.Add(_status);
            statusBar.Controls.Add(_progress);

            _popup = new ListBox
            {
                Visible = false, Name = "sqlCompletion", DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 18, IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle, TabStop = false,
            };
            _popup.DrawItem += DrawCompletionItem;
            _popup.MouseDoubleClick += (_, _) => AcceptCompletion();
            _popup.MouseDown += (_, e) => { int i = _popup.IndexFromPoint(e.Location); if (i >= 0) _popup.SelectedIndex = i; };

            Controls.Add(split);
            Controls.Add(bar);
            Controls.Add(statusBar);
            Controls.Add(_popup);
            _popup.BringToFront();

            _recolorTimer = new System.Windows.Forms.Timer { Interval = 120 };
            _recolorTimer.Tick += (_, _) => { _recolorTimer.Stop(); Recolor(); };
            _editor.TextChanged += (_, _) => { _recolorTimer.Stop(); _recolorTimer.Start(); };
            _editor.KeyDown += EditorKeyDown;
            _editor.KeyPress += EditorKeyPress;
            _editor.LostFocus += (_, _) => { if (!_popup.Focused) HideCompletion(); };
            _editor.MouseDown += (_, _) => HideCompletion();

            ApplyTexts();
            ApplyPalette(palette);
            UpdateButtons();
            SweepOldResults();
            // 처음 제대로 된 높이가 생겼을 때 편집기:결과 = 55:45로 나눈다. 창이 아직 작으면(기본 크기) 최소 크기 때문에 예외가 나므로 범위 안으로만 설정한다.
            bool placed = false;
            void Place()
            {
                if (placed || split.Height < 200) return;
                int max = split.Height - split.Panel2MinSize - split.SplitterWidth;
                if (max < split.Panel1MinSize) return;
                placed = true;
                split.SplitterDistance = Math.Clamp((int)(split.Height * 0.55), split.Panel1MinSize, max);
            }
            split.SizeChanged += (_, _) => Place();
            HandleCreated += (_, _) => Place();
        }

        private Button MakeButton(string name, EventHandler click)
        {
            var b = new Button { Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 0, 2, 0), MinimumSize = new Size(72, 26) };
            b.Click += click;
            return b;
        }

        /// <summary>언어 전환 시 텍스트를 다시 적용한다.</summary>
        public void Relocalize() => ApplyTexts();

        private void ApplyTexts()
        {
            _btnRun.Text = LT("Run  Ctrl+Enter", "실행  Ctrl+Enter");
            _btnPreview.Text = LT("Preview  F5", "미리보기  F5");
            _btnCancel.Text = LT("Cancel", "취소");
            _btnSaveView.Text = LT("Save as view…", "뷰로 저장…");
            _tip.SetToolTip(_btnRun, LT("Run the query (or the selected text) and open the result as a table tab.", "질의(선택 영역이 있으면 그 부분)를 실행해 결과를 표 탭으로 엽니다."));
            _tip.SetToolTip(_btnPreview, LT($"Show the first {PreviewRows} rows below without writing a file.", $"파일을 만들지 않고 앞 {PreviewRows}행만 아래에 보여 줍니다."));
            _tip.SetToolTip(_btnCancel, LT("Stop the running query.", "실행 중인 질의를 멈춥니다."));
            _tip.SetToolTip(_btnSaveView, LT("Keep this query as a view table in the workspace.", "이 질의를 작업 공간의 뷰 테이블로 보관합니다."));
            if (!IsBusy && _status.Text.Length == 0)
                _status.Text = LT("Ctrl+Space: suggestions · Ctrl+Enter: run · F5: preview", "Ctrl+Space: 자동완성 · Ctrl+Enter: 실행 · F5: 미리보기");
        }

        public void ApplyPalette(ThemePalette palette)
        {
            _palette = palette;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            _editor.BackColor = palette.Surface;
            _editor.ForeColor = palette.Text;
            _status.ForeColor = palette.Text;
            _error.BackColor = ColorBlend(palette.Surface, Color.FromArgb(214, 76, 76), 0.18);
            _error.ForeColor = IsDark ? Color.FromArgb(255, 160, 160) : Color.FromArgb(160, 30, 30);
            _grid.BackgroundColor = palette.GridBg;
            _grid.GridColor = palette.Border;
            _grid.DefaultCellStyle.BackColor = palette.GridBg;
            _grid.DefaultCellStyle.ForeColor = palette.Text;
            _grid.DefaultCellStyle.SelectionBackColor = palette.SelectionBg;
            _grid.DefaultCellStyle.SelectionForeColor = palette.SelectionText;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = palette.HeaderBg;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.HeaderText;
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = palette.HeaderBg;
            _popup.BackColor = palette.Surface;
            _popup.ForeColor = palette.Text;
            foreach (var b in new[] { _btnRun, _btnPreview, _btnCancel, _btnSaveView })
            {
                b.BackColor = palette.Surface;
                b.ForeColor = palette.Text;
                b.FlatAppearance.BorderColor = palette.Border;
            }
            Recolor();
        }

        private bool IsDark => ReferenceEquals(_palette, ThemePalette.Dark);

        private static Color ColorBlend(Color a, Color b, double t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // ---- 구문 색 ----------------------------------------------------------------------------------

        private const int WM_SETREDRAW = 0x000B, EM_SETEVENTMASK = 0x0445 /* WM_USER + 69 */, EM_GETSCROLLPOS = 0x04DD, EM_SETSCROLLPOS = 0x04DE;

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

        private Color TokenColor(SqlTokenKind kind, string text)
        {
            bool dark = IsDark;
            return kind switch
            {
                SqlTokenKind.Comment => dark ? Color.FromArgb(106, 153, 85) : Color.FromArgb(0, 128, 0),
                SqlTokenKind.String => dark ? Color.FromArgb(206, 145, 120) : Color.FromArgb(163, 21, 21),
                SqlTokenKind.QuotedIdentifier => dark ? Color.FromArgb(220, 180, 255) : Color.FromArgb(111, 66, 193),
                SqlTokenKind.Number => dark ? Color.FromArgb(181, 206, 168) : Color.FromArgb(9, 134, 88),
                SqlTokenKind.Word when SqlCompletion.IsKeyword(text) => dark ? Color.FromArgb(86, 156, 214) : Color.FromArgb(0, 0, 255),
                _ => _palette.Text,
            };
        }

        /// <summary>토큰별 글자색을 다시 칠한다(선택·스크롤 보존, 깜박임 없이). 아주 긴 SQL은 건너뛴다.</summary>
        private void Recolor()
        {
            if (_recoloring || !_editor.IsHandleCreated && _editor.TextLength == 0) return;
            string text = _editor.Text;
            _recoloring = true;
            IntPtr h = _editor.IsHandleCreated ? _editor.Handle : IntPtr.Zero;
            int selStart = _editor.SelectionStart, selLen = _editor.SelectionLength;
            var scroll = new Point();
            IntPtr oldMask = IntPtr.Zero;
            try
            {
                if (h != IntPtr.Zero)
                {
                    SendMessage(h, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
                    SendMessage(h, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                    oldMask = SendMessage(h, EM_SETEVENTMASK, IntPtr.Zero, IntPtr.Zero);
                }
                _editor.SelectAll();
                _editor.SelectionColor = _palette.Text;
                _editor.SelectionBackColor = _palette.Surface;
                _editor.SelectionFont = _editor.Font;
                if (text.Length <= 200_000)
                {
                    foreach (var t in SqlText.Tokenize(text))
                    {
                        if (t.Kind is SqlTokenKind.Whitespace or SqlTokenKind.Punctuation) continue;
                        var color = TokenColor(t.Kind, text.Substring(t.Start, t.Length));
                        if (color == _palette.Text) continue;
                        _editor.Select(t.Start, t.Length);
                        _editor.SelectionColor = color;
                    }
                }
                _editor.Select(selStart, selLen);
            }
            finally
            {
                if (h != IntPtr.Zero)
                {
                    SendMessage(h, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
                    SendMessage(h, EM_SETEVENTMASK, IntPtr.Zero, oldMask);
                    SendMessage(h, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                    _editor.Invalidate();
                }
                _recoloring = false;
            }
        }

        // ---- 자동완성 -----------------------------------------------------------------------------------

        private void EditorKeyPress(object? sender, KeyPressEventArgs e)
        {
            if (char.IsLetterOrDigit(e.KeyChar) || e.KeyChar is '_' or '.' or '"')
                BeginInvoke(() => ShowCompletion(force: false));
            else if (!char.IsControl(e.KeyChar)) HideCompletion();
        }

        private void EditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; e.Handled = true; HideCompletion(); _ = RunAsync(); return; }
            if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; e.Handled = true; HideCompletion(); _ = PreviewAsync(); return; }
            if (e.Control && e.KeyCode == Keys.Space) { e.SuppressKeyPress = true; e.Handled = true; ShowCompletion(force: true); return; }
            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsText())
            {
                // 서식 없이 글자만 붙인다(색은 다시 칠한다).
                e.SuppressKeyPress = true; e.Handled = true;
                _editor.SelectedText = Clipboard.GetText();
                return;
            }
            if (_popup.Visible)
            {
                switch (e.KeyCode)
                {
                    case Keys.Down: e.Handled = true; e.SuppressKeyPress = true; _popup.SelectedIndex = Math.Min(_popup.Items.Count - 1, _popup.SelectedIndex + 1); break;
                    case Keys.Up: e.Handled = true; e.SuppressKeyPress = true; _popup.SelectedIndex = Math.Max(0, _popup.SelectedIndex - 1); break;
                    case Keys.Enter when !e.Control:
                    case Keys.Tab: e.Handled = true; e.SuppressKeyPress = true; AcceptCompletion(); break;
                    case Keys.Escape: e.Handled = true; e.SuppressKeyPress = true; HideCompletion(); break;
                    case Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown: HideCompletion(); break;
                }
            }
            else if (e.KeyCode == Keys.Tab && !e.Control)
            {
                e.SuppressKeyPress = true; e.Handled = true;
                _editor.SelectedText = "  ";
            }
        }

        private void ShowCompletion(bool force)
        {
            if (_workspace is null) { HideCompletion(); return; }
            var result = SqlCompletion.Suggest(_editor.Text, _editor.SelectionStart, _workspace.Sources, _workspace.Views, force);
            if (result is null) { HideCompletion(); return; }
            _completion = result;
            _popup.BeginUpdate();
            _popup.Items.Clear();
            foreach (var it in result.Items) _popup.Items.Add(it);
            _popup.EndUpdate();
            _popup.SelectedIndex = 0;

            Point caretPt = _editor.GetPositionFromCharIndex(Math.Min(result.Start, Math.Max(0, _editor.TextLength - 1)));
            Point onPanel = PointToClient(_editor.PointToScreen(new Point(caretPt.X, caretPt.Y + _editor.Font.Height + 4)));
            int w = 340, h = Math.Min(result.Items.Count, 9) * _popup.ItemHeight + 4;
            int x = Math.Max(0, Math.Min(onPanel.X, Width - w - 2));
            int y = onPanel.Y;
            if (y + h > Height - 4) y = Math.Max(0, onPanel.Y - _editor.Font.Height - 8 - h);
            _popup.SetBounds(x, y, w, h);
            _popup.Visible = true;
            _popup.BringToFront();
        }

        private void HideCompletion()
        {
            if (!_popup.Visible) return;
            _popup.Visible = false;
            _completion = null;
        }

        private void AcceptCompletion()
        {
            if (_completion is not { } c || _popup.SelectedItem is not CompletionItem item) { HideCompletion(); return; }
            _editor.Select(c.Start, c.Length);
            _editor.SelectedText = item.InsertText;
            HideCompletion();
            _editor.Focus();
        }

        private void DrawCompletionItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _popup.Items.Count) return;
            var item = (CompletionItem)_popup.Items[e.Index]!;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var back = new SolidBrush(sel ? _palette.SelectionBg : _popup.BackColor);
            e.Graphics.FillRectangle(back, e.Bounds);
            Color fore = sel ? _palette.SelectionText : _palette.Text;
            string glyph = item.Kind switch
            {
                CompletionKind.Column => "▪", CompletionKind.Table => "▦", CompletionKind.View => "◈", CompletionKind.Schema => "▣",
                CompletionKind.Function => "ƒ", _ => "·",
            };
            TextRenderer.DrawText(e.Graphics, glyph, e.Font, new Rectangle(e.Bounds.X + 2, e.Bounds.Y, 16, e.Bounds.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, item.Label, e.Font, new Rectangle(e.Bounds.X + 20, e.Bounds.Y, 200, e.Bounds.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            Color dim = sel ? fore : ColorBlend(fore, _popup.BackColor, 0.45);
            TextRenderer.DrawText(e.Graphics, item.Detail, e.Font, new Rectangle(e.Bounds.X + 222, e.Bounds.Y, e.Bounds.Width - 224, e.Bounds.Height), dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        // ---- 실행·미리보기·취소 ---------------------------------------------------------------------------

        private (string Sql, int Offset) CurrentSql()
        {
            string all = _editor.Text;
            if (_editor.SelectionLength > 0 && !string.IsNullOrWhiteSpace(_editor.SelectedText))
                return (_editor.SelectedText, _editor.SelectionStart);
            return (all, 0);
        }

        private void UpdateButtons()
        {
            bool ready = _workspace is not null && !IsBusy;
            _btnRun.Enabled = ready;
            _btnPreview.Enabled = ready;
            _btnSaveView.Enabled = _workspace is not null && !IsBusy;
            _btnCancel.Enabled = IsBusy;
        }

        private void SetBusy(bool busy, string? message = null)
        {
            IsBusy = busy;
            _progress.Visible = busy;
            _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
            if (message is not null) _status.Text = message;
            UpdateButtons();
        }

        /// <summary>실행 중인 질의를 취소한다(DuckDB 중단 신호). 실행 중이 아니면 아무 일도 없다.</summary>
        public void Cancel() => _cts?.Cancel();

        private void ClearError()
        {
            LastError = null;
            _error.Visible = false;
            _error.Height = 0;
        }

        private void ShowError(Exception ex, string sql, int offset)
        {
            string message;
            int? line = null, col = null;
            if (ex is WorkspaceQueryException q) { message = q.Message; line = q.Line; col = q.Column; }
            else message = ex.Message;
            LastError = message;
            _error.Text = "⚠ " + message;
            _error.Height = Math.Min(90, Math.Max(22, TextRenderer.MeasureText(_error.Text, _error.Font, new Size(Math.Max(200, _error.ClientSize.Width - 8), int.MaxValue), TextFormatFlags.WordBreak).Height + 8));
            _error.Visible = true;
            _status.Text = LT("Failed", "실패");

            if (line is int l && col is int c)
            {
                int index = IndexOf(sql, l, c);
                if (index >= 0)
                {
                    int len = 1;
                    while (index + len < sql.Length && (char.IsLetterOrDigit(sql[index + len]) || sql[index + len] == '_') && char.IsLetterOrDigit(sql[index])) len++;
                    int abs = Math.Clamp(offset + index, 0, _editor.TextLength);
                    _editor.Select(abs, Math.Min(len, _editor.TextLength - abs));
                    _editor.ScrollToCaret();
                }
            }
        }

        private static int IndexOf(string sql, int line, int col)
        {
            int idx = 0;
            for (int l = 1; l < line; l++)
            {
                int nl = sql.IndexOf('\n', idx);
                if (nl < 0) return -1;
                idx = nl + 1;
            }
            idx += col - 1;
            return idx <= sql.Length ? idx : -1;
        }

        /// <summary>앞 200행을 아래 표에 보여 준다. 성공하면 결과, 실패·취소면 null(오류는 편집기 아래에 표시).</summary>
        public async Task<QueryPreview?> PreviewAsync()
        {
            if (_workspace is null || IsBusy) return null;
            var (sql, offset) = CurrentSql();
            ClearError();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _cts = new CancellationTokenSource();
            SetBusy(true, LT("Running preview…", "미리보기 실행 중…"));
            try
            {
                if (PrepareAsync is { } prepare) await prepare(_cts.Token);
                var preview = await _workspace.PreviewAsync(sql, PreviewRows, _cts.Token);
                ShowPreview(preview);
                _status.Text = LT(
                    $"Preview: {preview.Rows.Count:N0} row(s){(preview.Truncated ? " (more rows not shown)" : "")}, {preview.Columns.Count} column(s) · {preview.Elapsed.TotalSeconds:0.00}s",
                    $"미리보기: {preview.Rows.Count:N0}행{(preview.Truncated ? "(더 있음)" : "")}, {preview.Columns.Count}컬럼 · {preview.Elapsed.TotalSeconds:0.00}초");
                return preview;
            }
            catch (OperationCanceledException) { _status.Text = LT("Cancelled", "취소했습니다"); return null; }
            catch (Exception ex) when (ex is WorkspaceQueryException or InvalidOperationException or ArgumentException or IOException)
            {
                ShowError(ex, sql, offset);
                return null;
            }
            finally
            {
                _cts?.Dispose(); _cts = null;
                SetBusy(false);
            }
        }

        /// <summary>질의를 실행해 결과를 임시 CSV로 쓰고 <see cref="ResultReady"/>를 일으킨다. 실패·취소면 null.</summary>
        public async Task<QueryResultInfo?> RunAsync()
        {
            if (_workspace is null || IsBusy) return null;
            var (sql, offset) = CurrentSql();
            ClearError();
            HideCompletion();
            Directory.CreateDirectory(ResultDirectory);
            string path = Path.Combine(ResultDirectory, $"query-{DateTime.Now:yyyyMMdd-HHmmss}-{++_runCounter}.csv");
            _cts = new CancellationTokenSource();
            SetBusy(true, LT("Running…", "실행 중…"));
            var sw = Stopwatch.StartNew();
            var progress = new Progress<long>(rows =>
            {
                if (IsBusy) _status.Text = LT($"Running… {rows:N0} row(s) processed · {sw.Elapsed.TotalSeconds:0}s", $"실행 중… {rows:N0}행 처리 · {sw.Elapsed.TotalSeconds:0}초");
            });
            try
            {
                if (PrepareAsync is { } prepare) await prepare(_cts.Token);
                var info = await _workspace.RunToCsvAsync(sql, path, progress, _cts.Token);
                _status.Text = LT($"Done: {info.RowCount:N0} row(s), {info.Columns.Count} column(s) · {info.Elapsed.TotalSeconds:0.00}s",
                                  $"완료: {info.RowCount:N0}행, {info.Columns.Count}컬럼 · {info.Elapsed.TotalSeconds:0.00}초");
                SetBusy(false);
                ResultReady?.Invoke(this, new SqlRunResultEventArgs(info, sql));
                return info;
            }
            catch (OperationCanceledException) { _status.Text = LT("Cancelled", "취소했습니다"); return null; }
            catch (Exception ex) when (ex is WorkspaceQueryException or InvalidOperationException or ArgumentException or IOException)
            {
                ShowError(ex, sql, offset);
                return null;
            }
            finally
            {
                _cts?.Dispose(); _cts = null;
                if (IsBusy) SetBusy(false);
            }
        }

        // ---- 미리보기 표 -----------------------------------------------------------------------------------

        private void ShowPreview(QueryPreview p)
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            for (int i = 0; i < p.Columns.Count; i++)
            {
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = p.Columns[i].Name, SortMode = DataGridViewColumnSortMode.NotSortable, ToolTipText = p.Columns[i].SqlType,
                    MinimumWidth = 50,
                };
                if (p.Columns[i].Type.IsNumeric()) col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                _grid.Columns.Add(col);
            }
            var rows = new List<DataGridViewRow>(p.Rows.Count);
            foreach (var r in p.Rows)
            {
                var row = new DataGridViewRow();
                row.CreateCells(_grid);
                for (int i = 0; i < r.Length; i++)
                {
                    row.Cells[i].Value = r[i] is null ? "NULL" : r[i];
                    if (r[i] is null)
                        row.Cells[i].Style = new DataGridViewCellStyle { ForeColor = ColorBlend(_palette.Text, _palette.GridBg, 0.55), Font = new Font(_grid.Font, FontStyle.Italic) };
                }
                rows.Add(row);
            }
            _grid.Rows.AddRange(rows.ToArray());
            // 너비: 머리글과 앞 50행의 글자 폭 중 큰 값(최대 280px).
            for (int c = 0; c < _grid.Columns.Count; c++)
            {
                int w = TextRenderer.MeasureText(_grid.Columns[c].HeaderText, _grid.Font).Width + 24;
                for (int r = 0; r < Math.Min(50, p.Rows.Count); r++)
                    w = Math.Max(w, TextRenderer.MeasureText(p.Rows[r][c] ?? "NULL", _grid.Font).Width + 14);
                _grid.Columns[c].Width = Math.Min(280, w);
            }
            _grid.ResumeLayout();
        }

        /// <summary>미리보기 표의 값(테스트·자동화용): 행·열 위치의 표시 글자.</summary>
        internal string? PreviewCell(int row, int col) => _grid.Rows[row].Cells[col].Value as string;
        internal int PreviewRowCount => _grid.Rows.Count;
        internal string ErrorText => _error.Visible ? _error.Text : "";
        internal string StatusText => _status.Text;

        private void SweepOldResults()
        {
            try
            {
                if (!Directory.Exists(ResultDirectory)) return;
                foreach (string f in Directory.EnumerateFiles(ResultDirectory, "query-*.csv"))
                {
                    try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(1)) File.Delete(f); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();
                _recolorTimer.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
