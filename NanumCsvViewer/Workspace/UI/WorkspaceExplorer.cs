using System.Diagnostics;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 작업 공간 탐색기(왼쪽 도킹): 열린 파일(아직 올리지 않은 것) · 원본(CSV 표 / DB → 표) · 뷰 · 질의 결과를 트리로 보여 준다.
    /// 컬럼은 타입 배지와 함께 펼쳐 보이고, 행 수는 펼칠 때 한 번 센다(느린 파일에서 UI가 멈추지 않도록 백그라운드). 뷰는 원본이 바뀌었으면 ⚠, 깨졌으면 오류 표시.
    /// 더블클릭 = 탭 열기, 오른쪽 클릭 = 메뉴, 파일을 끌어다 놓으면 올린다. 명령은 모두 <see cref="Form1"/>이 실행한다.
    /// </summary>
    internal sealed class WorkspaceExplorer : Panel
    {
        private static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        internal enum NodeKind { Group, Tab, Source, Table, View, Column, Result, Hint }

        internal sealed record NodeInfo(NodeKind Kind, object? Item, string Key);

        private readonly Form1 _host;
        private readonly TreeView _tree;
        private readonly ToolStrip _bar;
        private readonly HashSet<string> _collapsedGroups = new();
        private readonly ToolStripButton _btnFiles, _btnFolder, _btnRefresh, _btnQuery;
        private readonly Label _banner;
        private readonly Panel _statusPanel;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Button _cancel;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private readonly Dictionary<IWorkspaceRelation, long> _counts = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<IWorkspaceRelation> _counting = new(ReferenceEqualityComparer.Instance);
        private readonly CancellationTokenSource _lifetime = new();
        private ContextMenuStrip? _menu;
        private ThemePalette _palette;

        public WorkspaceExplorer(Form1 host, ThemePalette palette)
        {
            _host = host;
            _palette = palette;
            Name = "workspaceExplorer";
            AllowDrop = true;

            _tree = new TreeView
            {
                Dock = DockStyle.Fill, Name = "workspaceTree", ShowLines = false, ShowRootLines = false, FullRowSelect = true, HideSelection = false,
                ShowNodeToolTips = true, DrawMode = TreeViewDrawMode.OwnerDrawText, BorderStyle = BorderStyle.None, ItemHeight = 22, AllowDrop = true,
                HotTracking = false, ShowPlusMinus = true, Indent = 16,
            };
            _tree.DrawNode += OnDrawNode;
            _tree.BeforeExpand += OnBeforeExpand;
            _tree.NodeMouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenNode(e.Node); };
            _tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) { _tree.SelectedNode = e.Node; ShowMenu(e.Node, e.Location); } };
            _tree.KeyDown += OnTreeKeyDown;
            _tree.AfterSelect += (_, e) => { if (e.Node?.Tag is NodeInfo { Item: WorkspaceTable t }) StartCount(t); };
            _tree.DragEnter += OnDragEnter;
            _tree.DragDrop += OnDragDrop;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            _bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Name = "workspaceBar", RenderMode = ToolStripRenderMode.ManagerRenderMode };
            _btnFiles = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Text, Name = "wsAddFiles" };
            _btnFolder = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Text, Name = "wsAddFolder" };
            _btnRefresh = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Text, Name = "wsRefresh" };
            _btnQuery = new ToolStripButton { DisplayStyle = ToolStripItemDisplayStyle.Text, Name = "wsNewQuery" };
            _btnFiles.Click += (_, _) => _host.AddFilesCommand();
            _btnFolder.Click += (_, _) => _host.AddFolderCommand();
            _btnRefresh.Click += (_, _) => _host.RefreshWorkspaceCommand();
            _btnQuery.Click += (_, _) => _host.NewQueryCommand(null, null);
            _bar.Items.AddRange(new ToolStripItem[] { _btnFiles, _btnFolder, _btnRefresh, new ToolStripSeparator(), _btnQuery });

            _banner = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 0, Visible = false, Name = "workspaceBanner", Padding = new Padding(6, 4, 6, 4) };

            _status = new Label { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Name = "workspaceStatus" };
            _progress = new ProgressBar { Dock = DockStyle.Right, Width = 70, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Name = "workspaceProgress", Visible = false };
            _cancel = new Button { Dock = DockStyle.Right, Width = 64, FlatStyle = FlatStyle.Flat, Name = "workspaceCancel", Visible = false };
            _cancel.Click += (_, _) => _host.CancelWorkspaceOperation();
            _statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 26, Name = "workspaceStatusPanel", Visible = false };
            _statusPanel.Controls.Add(_status);
            _statusPanel.Controls.Add(_progress);
            _statusPanel.Controls.Add(_cancel);

            Controls.Add(_tree);
            Controls.Add(_banner);
            Controls.Add(_bar);
            Controls.Add(_statusPanel);

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 80 };
            _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); _ = RefreshAsync(); };

            Relocalize();
            ApplyPalette(palette);
            RefreshTree();
        }

        // ---------------------------------------------------------------- 테마 · 언어

        public void ApplyPalette(ThemePalette palette)
        {
            _palette = palette;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            _tree.BackColor = palette.Surface;
            _tree.ForeColor = palette.Text;
            _banner.BackColor = Blend(palette.Surface, Color.FromArgb(214, 160, 60), 0.25);
            _banner.ForeColor = palette.Text;
            _status.ForeColor = palette.Text;
            _statusPanel.BackColor = palette.Window;
            _cancel.BackColor = palette.Surface;
            _cancel.ForeColor = palette.Text;
            _cancel.FlatAppearance.BorderColor = palette.Border;
            _bar.BackColor = palette.ToolStrip;
            _bar.ForeColor = palette.Text;
            _tree.Invalidate();
        }

        public void Relocalize()
        {
            _btnFiles.Text = LT("＋ Files", "＋ 파일");
            _btnFolder.Text = LT("＋ Folder", "＋ 폴더");
            _btnRefresh.Text = LT("Refresh", "새로 고침");
            _btnQuery.Text = LT("Query", "질의");
            _btnFiles.ToolTipText = LT("Add CSV, Excel, SAS, SPSS or SQLite files to the workspace (or drop them here)", "CSV·엑셀·SAS·SPSS·SQLite 파일을 작업 공간에 추가합니다(여기로 끌어다 놓아도 됩니다)");
            _btnFolder.ToolTipText = LT("Add every supported file in a folder", "폴더의 지원되는 파일을 모두 추가합니다");
            _btnRefresh.ToolTipText = LT("Re-read source files that changed on disk and refresh the list (views whose sources changed are marked ⚠)", "디스크에서 바뀐 원본 파일을 다시 읽고 목록을 갱신합니다(원본이 바뀐 뷰는 ⚠ 표시)");
            _btnQuery.ToolTipText = LT("Open a SQL editor", "SQL 편집기를 엽니다");
            _cancel.Text = LT("Cancel", "취소");
            RefreshTree();
        }

        private static Color Blend(Color a, Color b, double t)
            => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        // ---------------------------------------------------------------- 상태 줄

        /// <summary>작업이 도는 중이면 메시지(진행 막대·취소 버튼 표시), 끝나면 null.</summary>
        public void SetBusy(string? message)
        {
            bool busy = message is not null;
            IsBusyShown = busy;
            _statusPanel.Visible = busy;
            _status.Text = message ?? "";
            _progress.Visible = busy;
            _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
            _cancel.Visible = busy;
        }

        internal string StatusText => _status.Text;
        internal bool IsBusyShown { get; private set; }

        // ---------------------------------------------------------------- 트리 만들기

        public void ScheduleRefresh()
        {
            if (IsDisposed) return;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        private static string KeyOf(TreeNode n) => n.Tag is NodeInfo i ? i.Key : "";

        /// <summary>엔진 상태 스냅숏: 원본·뷰 목록과 "파일이 바뀜"·"결과가 오래됨" 표시. 엔진 잠금을 잡는 호출이라 UI 스레드 밖에서 만든다(큰 파일을 등록하는 중에도 화면이 멈추지 않도록).</summary>
        private sealed record Capture(DataWorkspace? Workspace, IReadOnlyList<WorkspaceSource> Sources, IReadOnlyList<WorkspaceView> Views,
            HashSet<Guid> ChangedSources, HashSet<Guid> StaleViews);

        private Capture _cap = new(null, Array.Empty<WorkspaceSource>(), Array.Empty<WorkspaceView>(), new(), new());
        private int _generation;

        private static Capture CaptureState(DataWorkspace? ws)
        {
            if (ws is null) return new Capture(null, Array.Empty<WorkspaceSource>(), Array.Empty<WorkspaceView>(), new(), new());
            var sources = ws.Sources;
            var views = ws.Views;
            var changed = new HashSet<Guid>();
            var stale = new HashSet<Guid>();
            foreach (var s in sources)
            {
                try { if (ws.IsSourceChanged(s)) changed.Add(s.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 파일 접근 실패는 "바뀜 아님"으로 */ }
            }
            foreach (var v in views)
            {
                try { if (ws.IsStale(v)) stale.Add(v.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return new Capture(ws, sources, views, changed, stale);
        }

        /// <summary>트리를 지금 다시 만든다(펼침·선택 보존). 엔진은 만들지 않고, 이미 있는 것만 읽는다.</summary>
        public void RefreshTree()
        {
            if (IsDisposed) return;
            _generation++;
            Apply(CaptureState(_host.ExistingWorkspace));
        }

        private async Task RefreshAsync()
        {
            if (IsDisposed) return;
            int generation = ++_generation;
            var ws = _host.ExistingWorkspace;
            Capture cap;
            try { cap = ws is null ? CaptureState(null) : await Task.Run(() => CaptureState(ws)); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return; }
            if (IsDisposed || generation != _generation) return;
            Apply(cap);
        }

        private void Apply(Capture cap)
        {
            if (IsDisposed) return;
            var expanded = new HashSet<string>();
            void Collect(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.IsExpanded) expanded.Add(KeyOf(n));
                    if (n.Tag is NodeInfo { Kind: NodeKind.Group } gi) { if (n.IsExpanded) _collapsedGroups.Remove(gi.Key); else _collapsedGroups.Add(gi.Key); }
                    Collect(n.Nodes);
                }
            }
            Collect(_tree.Nodes);
            string selected = _tree.SelectedNode is { } sn ? KeyOf(sn) : "";

            _cap = cap;
            string? reason = _host.WorkspaceUnavailableReason;
            _banner.Visible = reason is not null;
            _banner.Height = reason is null ? 0 : Math.Min(120, Math.Max(40, TextRenderer.MeasureText(LT("Query engine unavailable: ", "질의 엔진을 쓸 수 없습니다: ") + reason,
                _banner.Font, new Size(Math.Max(100, Width - 16), int.MaxValue), TextFormatFlags.WordBreak).Height + 10));
            _banner.Text = reason is null ? "" : LT("Query engine unavailable: ", "질의 엔진을 쓸 수 없습니다: ") + reason;
            _btnFiles.Enabled = _btnFolder.Enabled = _btnQuery.Enabled = reason is null;

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();
                var tabs = _host.Tabs;

                var openFiles = tabs.Where(t => t.Kind is TabKind.File or TabKind.Sheet && Form1.FindSourceByPath(cap.Sources, t.Path) is null).ToList();
                if (openFiles.Count > 0)
                {
                    var g = Group("grp:open", LT("Open files (not in the workspace yet)", "열린 파일 (아직 작업 공간에 없음)"), openFiles.Count);
                    foreach (var t in openFiles) g.Nodes.Add(TabNode(t));
                }

                if (cap.Sources.Count > 0)
                {
                    var g = Group("grp:src", LT("Sources", "원본"), cap.Sources.Count);
                    foreach (var s in cap.Sources) g.Nodes.Add(SourceNode(s, tabs));
                }
                if (cap.Views.Count > 0)
                {
                    var g = Group("grp:views", LT("Views", "뷰"), cap.Views.Count);
                    foreach (var v in cap.Views) g.Nodes.Add(ViewNode(cap.Workspace!, v, tabs));
                }

                var results = tabs.Where(t => t.Kind == TabKind.Result).ToList();
                if (results.Count > 0)
                {
                    var g = Group("grp:results", LT("Query results", "질의 결과"), results.Count);
                    foreach (var t in results)
                        g.Nodes.Add(new TreeNode(t.DisplayName) { Tag = new NodeInfo(NodeKind.Result, t, "res:" + t.Id), ToolTipText = t.Path });
                }

                if (_tree.Nodes.Count == 0)
                    _tree.Nodes.Add(new TreeNode(LT("Open a file, or add files to build a workspace.", "파일을 열거나 추가해 작업 공간을 만드세요.")) { Tag = new NodeInfo(NodeKind.Hint, null, "hint") });

                foreach (TreeNode n in _tree.Nodes) if (!_collapsedGroups.Contains(KeyOf(n))) n.Expand();
                void Restore(TreeNodeCollection nodes)
                {
                    foreach (TreeNode n in nodes)
                    {
                        if (expanded.Contains(KeyOf(n))) { PopulateColumns(n); n.Expand(); }
                        if (selected.Length > 0 && KeyOf(n) == selected) _tree.SelectedNode = n;
                        if (n.IsExpanded || n.Nodes.Count > 0 && n.Nodes[0].Tag is null) Restore(n.Nodes);
                    }
                }
                Restore(_tree.Nodes);
            }
            finally { _tree.EndUpdate(); }
        }

        private TreeNode Group(string key, string title, int count)
        {
            var n = new TreeNode(title) { Tag = new NodeInfo(NodeKind.Group, count, key) };
            _tree.Nodes.Add(n);
            return n;
        }

        private static TreeNode Placeholder() => new("…");

        private TreeNode TabNode(DocumentTab tab)
            => new(tab.DisplayName) { Tag = new NodeInfo(NodeKind.Tab, tab, "tab:" + tab.Id), ToolTipText = tab.Path };

        private TreeNode SourceNode(WorkspaceSource s, IReadOnlyList<DocumentTab> tabs)
        {
            if (s.Kind == WorkspaceSourceKind.Csv)
            {
                var t = s.Tables[0];
                var node = new TreeNode(t.Name) { Tag = new NodeInfo(NodeKind.Table, t, "tbl:" + t.SqlReference), ToolTipText = t.FilePath };
                node.Nodes.Add(Placeholder());
                return node;
            }
            var db = new TreeNode(s.Name) { Tag = new NodeInfo(NodeKind.Source, s, "src:" + s.Id), ToolTipText = s.Path };
            foreach (var t in s.Tables)
            {
                var tn = new TreeNode(t.Name) { Tag = new NodeInfo(NodeKind.Table, t, "tbl:" + t.SqlReference), ToolTipText = t.DisplayName };
                tn.Nodes.Add(Placeholder());
                db.Nodes.Add(tn);
            }
            return db;
        }

        private TreeNode ViewNode(DataWorkspace ws, WorkspaceView v, IReadOnlyList<DocumentTab> tabs)
        {
            string tip = v.Error is not null ? "⚠ " + v.Error : v.Sql;
            if (ProvenanceText(v.Provenance) is { } made) tip = made + "\n\n" + tip;
            var node = new TreeNode(v.Name) { Tag = new NodeInfo(NodeKind.View, v, "view:" + v.Id), ToolTipText = tip };
            if (v.Columns.Count > 0) node.Nodes.Add(Placeholder());
            return node;
        }

        private void OnBeforeExpand(object? sender, TreeViewCancelEventArgs e)
        {
            if (e.Node is { } node) PopulateColumns(node);
        }

        // 표·뷰 노드의 자리표시 자식을 컬럼 노드로 바꾼다(처음 펼칠 때 한 번). 표는 이때 행 수도 세기 시작한다.
        private void PopulateColumns(TreeNode node)
        {
            if (node.Nodes.Count != 1 || node.Nodes[0].Tag is not null) return;
            if (node.Tag is not NodeInfo { Item: IWorkspaceRelation rel }) return;
            node.Nodes.Clear();
            foreach (var c in rel.Columns)
                node.Nodes.Add(new TreeNode(c.Name) { Tag = new NodeInfo(NodeKind.Column, c, "col:" + rel.SqlReference + ":" + c.Name), ToolTipText = c.SqlType + (c.IsConverted ? LT(" (converted from text)", " (텍스트에서 변환)") : "") });
            if (rel is WorkspaceTable t) StartCount(t);
        }

        // ---------------------------------------------------------------- 행 수 (느리게, 백그라운드)

        private async void StartCount(WorkspaceTable table)
        {
            var ws = _host.ExistingWorkspace;
            if (ws is null || _counts.ContainsKey(table) || !_counting.Add(table)) return;
            try
            {
                long n = await ws.CountRowsAsync(table, _lifetime.Token);
                _counts[table] = n;
            }
            catch (OperationCanceledException) { _counting.Remove(table); return; }
            catch (Exception ex) // async void — 어떤 오류도 UI 스레드의 처리되지 않은 예외가 되지 않게 한다(행 수는 "?"로 표시)
            {
                _counts[table] = -1;
                Debug.WriteLine($"[Explorer] count {table.DisplayName}: {ex.Message}");
            }
            _counting.Remove(table);
            if (!IsDisposed) _tree.Invalidate();
        }

        /// <summary>이 표의 행 수(센 적이 있으면). 모르면 null, 세지 못했으면 -1.</summary>
        internal long? KnownRowCount(IWorkspaceRelation rel) => _counts.TryGetValue(rel, out long n) ? n : null;

        // ---------------------------------------------------------------- 그리기

        private string DetailOf(NodeInfo info, out Color color)
        {
            color = Blend(_palette.Text, _palette.Surface, 0.45);
            _ = _cap; // 엔진 상태는 마지막으로 만든 스냅숏(_cap)에서 읽는다 — 그리는 중에 엔진 잠금을 잡지 않는다.
            switch (info.Kind)
            {
                case NodeKind.Group:
                    return info.Item is int n ? n.ToString() : "";
                case NodeKind.Tab when info.Item is DocumentTab tab:
                {
                    string kind = tab.Kind == TabKind.Sheet ? LT("workbook", "통합 문서") : "CSV";
                    if (tab.Document is { IndexingComplete: true } d) return $"{kind} · {d.DataRowsAvailable:N0}" + LT(" rows", "행");
                    return tab.IsIndexing ? $"{kind} · {tab.IndexingPercent}%" : kind;
                }
                case NodeKind.Result when info.Item is DocumentTab res:
                    return res.Document is { IndexingComplete: true } rd ? $"{rd.DataRowsAvailable:N0}" + LT(" rows", "행") : "";
                case NodeKind.Source when info.Item is WorkspaceSource s:
                    return $"{s.Tables.Count}" + LT(" tables", "개 표") + (_cap.ChangedSources.Contains(s.Id) ? "  ⚠" : "");
                case NodeKind.Table when info.Item is WorkspaceTable t:
                {
                    string cols = $"{t.Columns.Count}" + LT(" cols", "열");
                    string changed = t.Source.Kind == WorkspaceSourceKind.Csv && _cap.ChangedSources.Contains(t.Source.Id) ? "  ⚠" : "";
                    if (_counts.TryGetValue(t, out long rows)) return (rows < 0 ? "?" : rows.ToString("N0") + LT(" rows", "행")) + " · " + cols + changed;
                    return (_counting.Contains(t) ? "… · " : "") + cols + changed;
                }
                case NodeKind.View when info.Item is WorkspaceView v:
                {
                    if (v.Error is not null) { color = Color.FromArgb(214, 90, 90); return LT("⚠ broken", "⚠ 깨짐"); }
                    bool stale = _cap.StaleViews.Contains(v.Id) || _host.FindViewTab(v) is { } vt && v.ResultPath is not null && !Form1.SamePath(vt.Path, v.ResultPath);
                    if (stale) { color = Color.FromArgb(214, 150, 40); return LT("⚠ source changed — refresh", "⚠ 원본 변경됨 — 새로 고침"); }
                    return v.ResultRowCount is long r ? r.ToString("N0") + LT(" rows", "행") : LT("not computed", "계산 전");
                }
                case NodeKind.Column when info.Item is WorkspaceColumn c:
                    return c.SqlType + (c.IsConverted ? " ←" : "");
                default:
                    return "";
            }
        }

        /// <summary>에이전트가 만든 뷰의 배지 글리프(탐색기 ✦ — 툴바의 "✦ AI"와 같은 표지).</summary>
        internal const string AgentGlyph = "✦";

        /// <summary>
        /// 뷰의 출처 설명(툴팁 머리): 누가 언제 만들었는지, 에이전트 뷰는 그 턴의 사용자 요청도. 출처를 모르면(작업 공간 파일 v1에서 온 뷰) null.
        /// </summary>
        internal static string? ProvenanceText(ViewProvenance? p)
        {
            if (p is null) return null;
            string when = p.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            if (p.IsAgent)
            {
                string head = LT($"{AgentGlyph} Created by the AI agent · {when}", $"{AgentGlyph} AI 에이전트가 만듦 · {when}");
                return p.Request is { Length: > 0 } req ? head + "\n" + LT("Request: ", "요청: ") + req : head;
            }
            if (p.WizardName is { } wizard)
            {
                string name = wizard switch
                {
                    "join" => LT("Join wizard", "조인 마법사"),
                    "append" => LT("Append wizard", "이어 붙이기 마법사"),
                    "compare" => LT("Compare wizard", "비교 마법사"),
                    "group" => LT("Group wizard", "그룹 집계 마법사"),
                    _ => LT("Wizard", "마법사"),
                };
                return LT($"Created with the {name} · {when}", $"{name}로 만듦 · {when}");
            }
            return LT($"Created by you · {when}", $"직접 만듦 · {when}");
        }

        /// <summary>노드 앞에 그려지는 글리프와 툴팁 글. 테스트·자동화용.</summary>
        internal static string GlyphText(NodeInfo info) => GlyphOf(info);

        internal static string TooltipText(NodeInfo info, string fallback = "") =>
            info.Item is WorkspaceView v && ProvenanceText(v.Provenance) is { } made ? made + "\n\n" + (v.Error is not null ? "⚠ " + v.Error : v.Sql) : fallback;

        private static string GlyphOf(NodeInfo info) => info.Kind switch
        {
            NodeKind.Source => "▣",
            NodeKind.Table => "▦",
            NodeKind.View when info.Item is WorkspaceView { Provenance.IsAgent: true } => AgentGlyph,
            NodeKind.View => "◈",
            NodeKind.Tab => "▤",
            NodeKind.Result => "▷",
            _ => "",
        };

        private void OnDrawNode(object? sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node is null || e.Node.Tag is not NodeInfo info || e.Bounds.Width <= 0) { e.DrawDefault = true; return; }
            var g = e.Graphics;
            var b = e.Bounds;
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            Color back = sel ? _palette.SelectionBg : _tree.BackColor;
            Color fore = sel ? _palette.SelectionText : _palette.Text;
            int left = Math.Max(0, b.Left - 2);
            using (var br = new SolidBrush(back)) g.FillRectangle(br, left, b.Y, Math.Max(0, _tree.ClientSize.Width - left), b.Height);

            int x = b.Left;
            if (info.Kind == NodeKind.Column && info.Item is WorkspaceColumn col)
            {
                x += DrawBadge(g, new Point(x, b.Y + (b.Height - 15) / 2), col.Type) + 5;
            }
            else
            {
                string glyph = GlyphOf(info);
                if (glyph.Length > 0)
                {
                    TextRenderer.DrawText(g, glyph, _tree.Font, new Rectangle(x, b.Y, 18, b.Height), sel ? fore : Blend(_palette.Accent, fore, 0.2),
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += 18;
                }
            }

            string detail = DetailOf(info, out Color detailColor);
            int detailW = detail.Length == 0 ? 0 : TextRenderer.MeasureText(g, detail, _tree.Font, Size.Empty, TextFormatFlags.NoPadding).Width + 6;
            int rightEdge = _tree.ClientSize.Width - 4;
            int nameW = Math.Max(20, rightEdge - x - detailW);
            bool bold = info.Kind is NodeKind.Group;
            using (var f = bold ? new Font(_tree.Font, FontStyle.Bold) : null)
            {
                Color nameColor = fore;
                if (info.Kind == NodeKind.View && info.Item is WorkspaceView { Error: not null } && !sel) nameColor = Color.FromArgb(214, 90, 90);
                TextRenderer.DrawText(g, e.Node.Text, f ?? _tree.Font, new Rectangle(x, b.Y, nameW, b.Height), nameColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            if (detail.Length > 0)
                TextRenderer.DrawText(g, detail, _tree.Font, new Rectangle(rightEdge - detailW, b.Y, detailW, b.Height), sel ? fore : detailColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        private int DrawBadge(Graphics g, Point at, ColumnValueType type)
        {
            string label = Form1.TypeAbbrev(type);
            using var f = new Font(_tree.Font.FontFamily, 6.75f, FontStyle.Bold);
            Size ts = TextRenderer.MeasureText(g, label, f, Size.Empty, TextFormatFlags.NoPadding);
            var rect = new Rectangle(at.X, at.Y, ts.Width + 10, 15);
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Form1.TypeColor(type))) g.FillRectangle(brush, rect);
            g.SmoothingMode = old;
            TextRenderer.DrawText(g, label, f, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return rect.Width;
        }

        // ---------------------------------------------------------------- 열기 · 키 · 끌어다 놓기

        /// <summary>선택한 노드의 관계(표·뷰). 없으면 null.</summary>
        internal IWorkspaceRelation? SelectedRelation => (_tree.SelectedNode?.Tag as NodeInfo)?.Item as IWorkspaceRelation;

        internal NodeInfo? SelectedInfo => _tree.SelectedNode?.Tag as NodeInfo;

        /// <summary>테스트·자동화용: 트리의 모든 노드(깊이 우선).</summary>
        internal IEnumerable<(NodeInfo Info, string Text, int Depth)> Snapshot()
        {
            IEnumerable<(NodeInfo, string, int)> Walk(TreeNodeCollection nodes, int depth)
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.Tag is NodeInfo i) yield return (i, n.Text, depth);
                    foreach (var c in Walk(n.Nodes, depth + 1)) yield return c;
                }
            }
            return Walk(_tree.Nodes, 0).ToList();
        }

        /// <summary>테스트·자동화용: 노드를 찾아 선택한다(필요하면 부모를 펼친다).</summary>
        internal bool Select(Func<NodeInfo, bool> match)
        {
            TreeNode? Find(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.Tag is NodeInfo i && match(i)) return n;
                    if (Find(n.Nodes) is { } hit) return hit;
                }
                return null;
            }
            // 컬럼처럼 아직 만들지 않은 자식을 찾을 수 있게, 표·뷰를 먼저 펼친다.
            void ExpandAll(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.Tag is NodeInfo { Kind: NodeKind.Table or NodeKind.View or NodeKind.Source or NodeKind.Group }) { PopulateColumns(n); n.Expand(); }
                    ExpandAll(n.Nodes);
                }
            }
            ExpandAll(_tree.Nodes);
            var found = Find(_tree.Nodes);
            if (found is null) return false;
            _tree.SelectedNode = found;
            return true;
        }

        /// <summary>노드 오른쪽에 그려지는 설명 글자(행 수·⚠ 표시 등). 테스트·자동화용.</summary>
        internal string DetailText(NodeInfo info) => DetailOf(info, out _);

        internal void OpenSelected() { if (_tree.SelectedNode is { } n) OpenNode(n); }

        private void OpenNode(TreeNode node)
        {
            if (node.Tag is not NodeInfo info) return;
            switch (info.Kind)
            {
                case NodeKind.Tab or NodeKind.Result when info.Item is DocumentTab tab:
                    _host.ActivateTab(tab);
                    break;
                case NodeKind.Table or NodeKind.View when info.Item is IWorkspaceRelation rel:
                    _ = _host.OpenRelationCommandAsync(rel);
                    break;
                case NodeKind.Source when info.Item is WorkspaceSource src:
                    _ = _host.OpenSourceCommandAsync(src);
                    break;
                case NodeKind.Group or NodeKind.Column:
                    node.Toggle();
                    break;
            }
        }

        private void OnTreeKeyDown(object? sender, KeyEventArgs e)
        {
            if (_tree.SelectedNode is not { } node) return;
            switch (e.KeyCode)
            {
                case Keys.Enter: OpenNode(node); e.Handled = true; break;
                case Keys.F5: _host.RefreshWorkspaceCommand(); e.Handled = true; break;
                case Keys.F2: if (node.Tag is NodeInfo { Item: WorkspaceSource or WorkspaceTable or WorkspaceView } ri) { _host.RenameCommand(RelationOrSource(ri)); e.Handled = true; } break;
                case Keys.Delete: if (node.Tag is NodeInfo { Item: WorkspaceSource or WorkspaceTable or WorkspaceView } di) { _ = _host.RemoveCommandAsync(RelationOrSource(di)); e.Handled = true; } break;
            }
        }

        // CSV 표의 이름 바꾸기·제거는 원본(WorkspaceSource) 단위, DB는 원본 노드에서만.
        private static object RelationOrSource(NodeInfo info)
            => info.Item is WorkspaceTable { Source.Kind: WorkspaceSourceKind.Csv } t ? t.Source : info.Item!;

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && _host.WorkspaceUnavailableReason is null) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = _host.AddFilesUiAsync(files);
        }

        // ---------------------------------------------------------------- 오른쪽 클릭 메뉴

        private ToolStripMenuItem Item(ContextMenuStrip m, string en, string ko, Action action, bool enabled = true)
        {
            var item = new ToolStripMenuItem(LT(en, ko)) { Enabled = enabled };
            item.Click += (_, _) => action();
            m.Items.Add(item);
            return item;
        }

        private void ShowMenu(TreeNode node, Point at)
        {
            if (node.Tag is not NodeInfo info) return;
            _menu?.Dispose();
            var m = new ContextMenuStrip { Name = "workspaceMenu" };
            bool engine = _host.WorkspaceUnavailableReason is null;
            switch (info.Kind)
            {
                case NodeKind.Tab when info.Item is DocumentTab tab:
                    Item(m, "Open", "열기", () => _host.ActivateTab(tab));
                    Item(m, "Add to workspace", "작업 공간에 추가", () => _ = _host.RegisterTabCommandAsync(tab), engine);
                    m.Items.Add(new ToolStripSeparator());
                    WizardItems(m, null, engine);
                    break;
                case NodeKind.Result when info.Item is DocumentTab res:
                    Item(m, "Open", "열기", () => _host.ActivateTab(res));
                    Item(m, "Close tab", "탭 닫기", () => _host.CloseTab(res, true));
                    break;
                case NodeKind.Source when info.Item is WorkspaceSource src:
                    Item(m, "Open", "열기", () => _ = _host.OpenSourceCommandAsync(src));
                    Item(m, "Refresh (re-read file)", "새로 고침 (파일 다시 읽기)", () => _ = _host.RefreshSourceCommandAsync(src), engine);
                    m.Items.Add(new ToolStripSeparator());
                    Item(m, "Rename…", "이름 바꾸기…", () => _host.RenameCommand(src), engine);
                    Item(m, "Remove from workspace", "작업 공간에서 제거", () => _ = _host.RemoveCommandAsync(src), engine);
                    break;
                case NodeKind.Table when info.Item is WorkspaceTable t:
                    Item(m, "Open", "열기", () => _ = _host.OpenRelationCommandAsync(t));
                    Item(m, "New query from here", "여기서 새 질의", () => _host.NewQueryCommand("SELECT * FROM " + t.SqlReference + " LIMIT 100", null), engine);
                    m.Items.Add(new ToolStripSeparator());
                    WizardItems(m, t.DisplayName, engine);
                    m.Items.Add(new ToolStripSeparator());
                    Item(m, "Show cast report…", "형 변환 보고…", () => _ = _host.ShowCastReportCommandAsync(t), engine && t.Columns.Any(c => c.IsConverted));
                    Item(m, "Save as file…", "파일로 저장…", () => _ = _host.SaveRelationCommandAsync(t), engine);
                    Item(m, "Refresh (re-read file)", "새로 고침 (파일 다시 읽기)", () => _ = _host.RefreshSourceCommandAsync(t.Source), engine);
                    if (t.Source.Kind == WorkspaceSourceKind.Csv)
                    {
                        Item(m, "Rename…", "이름 바꾸기…", () => _host.RenameCommand(t.Source), engine);
                        Item(m, "Remove from workspace", "작업 공간에서 제거", () => _ = _host.RemoveCommandAsync(t.Source), engine);
                    }
                    break;
                case NodeKind.View when info.Item is WorkspaceView v:
                    Item(m, "Open", "열기", () => _ = _host.OpenRelationCommandAsync(v), engine && v.Error is null);
                    Item(m, "New query from here", "여기서 새 질의", () => _host.NewQueryCommand("SELECT * FROM " + v.SqlReference + " LIMIT 100", null), engine);
                    Item(m, "Edit SQL…", "SQL 편집…", () => _host.NewQueryCommand(v.Sql, v), engine);
                    m.Items.Add(new ToolStripSeparator());
                    WizardItems(m, v.DisplayName, engine);
                    m.Items.Add(new ToolStripSeparator());
                    Item(m, "Refresh (recompute)", "새로 고침 (다시 계산)", () => _ = _host.RefreshViewCommandAsync(v), engine && v.Error is null);
                    Item(m, "Open source tabs", "원본 탭 열기", () => _ = _host.OpenSourceTabsCommandAsync(v), engine);
                    Item(m, "Save as file…", "파일로 저장…", () => _ = _host.SaveRelationCommandAsync(v), engine && v.Error is null);
                    Item(m, "Rename…", "이름 바꾸기…", () => _host.RenameCommand(v), engine);
                    Item(m, "Remove", "제거", () => _ = _host.RemoveCommandAsync(v), engine);
                    break;
                case NodeKind.Column when info.Item is WorkspaceColumn c:
                    Item(m, "Copy name", "이름 복사", () => { try { Clipboard.SetText(c.Name); } catch (Exception ex) { Debug.WriteLine($"[Explorer] clipboard: {ex.Message}"); } });
                    break;
                default:
                    Item(m, "Add files…", "파일 추가…", _host.AddFilesCommand, engine);
                    Item(m, "Add folder…", "폴더 추가…", _host.AddFolderCommand, engine);
                    break;
            }
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Add files…", "파일 추가…", _host.AddFilesCommand, engine);
            Item(m, "Add folder…", "폴더 추가…", _host.AddFolderCommand, engine);
            Item(m, "Refresh", "새로 고침", _host.RefreshWorkspaceCommand);
            if (!engine)
                foreach (ToolStripItem i in m.Items) i.ToolTipText = _host.WorkspaceUnavailableReason;
            _menu = m;
            m.Show(_tree, at);
        }

        private void WizardItems(ContextMenuStrip m, string? preselect, bool enabled)
        {
            Item(m, "Join with…", "조인…", () => _ = _host.RunWizardAsync(WizardKind.Join, preselect), enabled);
            Item(m, "Append…", "이어 붙이기…", () => _ = _host.RunWizardAsync(WizardKind.Append, preselect), enabled);
            Item(m, "Compare…", "비교…", () => _ = _host.RunWizardAsync(WizardKind.Compare, preselect), enabled);
            Item(m, "Group…", "그룹 집계…", () => _ = _host.RunWizardAsync(WizardKind.Group, preselect), enabled);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _lifetime.Cancel();
                _refreshTimer.Dispose();
                _menu?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal enum WizardKind { Join, Append, Compare, Group }
}
