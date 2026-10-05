using System.Drawing.Drawing2D;

namespace NanumCsvViewer
{
    /// <summary>
    /// 열린 문서 탭 띠(소유자 그리기, 테마 팔레트). 탭마다 닫기 단추·저장 안 한 편집 표시·인덱싱 진행 막대를 그리고,
    /// 가운데 클릭 닫기·끌어서 순서 바꾸기·오른쪽 클릭 메뉴·휠/화살표 스크롤을 지원한다. 포커스를 가져가지 않는다(그리드 키 입력 유지).
    /// 탭 목록의 소유자는 Form1이며 이 컨트롤은 <see cref="SetTabs"/>로 받은 목록을 그리기만 한다.
    /// </summary>
    internal sealed class TabStrip : Control
    {
        private const int MinTabWidth = 96, MaxTabWidth = 240, CloseSize = 14, ArrowWidth = 22;

        private readonly List<DocumentTab> _tabs = new();
        private readonly List<Rectangle> _bounds = new();   // 스크롤 적용 전 좌표
        private readonly ToolTip _tip = new() { ShowAlways = true };
        private DocumentTab? _active;
        private ThemePalette _pal = ThemePalette.Light;
        private int _scroll;
        private int _hover = -1;
        private bool _hoverClose;
        private int _pressIndex = -1;
        private Point _pressPoint;
        private bool _dragging;
        private int _dropIndex = -1;
        private string _tipText = "";

        public event Action<DocumentTab>? TabActivateRequested;
        public event Action<DocumentTab>? TabCloseRequested;
        public event Action<DocumentTab, Point>? TabContextRequested;
        public event Action<DocumentTab, int>? TabMoveRequested;

        public TabStrip()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Height = 30;
        }

        public IReadOnlyList<DocumentTab> Tabs => _tabs;
        public DocumentTab? Active => _active;

        public void ApplyPalette(ThemePalette palette)
        {
            _pal = palette;
            BackColor = palette.Window;
            Invalidate();
        }

        public void SetTabs(IReadOnlyList<DocumentTab> tabs, DocumentTab? active)
        {
            _tabs.Clear();
            _tabs.AddRange(tabs);
            _active = active;
            Relayout();
            EnsureVisible(active);
            AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            Invalidate();
        }

        // ------------------------------------------------------------------ 배치

        private int ContentRight => ClientSize.Width - (NeedsArrows ? ArrowWidth * 2 : 0);
        private int TotalWidth => _bounds.Count == 0 ? 0 : _bounds[^1].Right;
        private bool NeedsArrows => TotalWidth > ClientSize.Width;
        private Rectangle LeftArrow => new(ClientSize.Width - ArrowWidth * 2, 0, ArrowWidth, ClientSize.Height);
        private Rectangle RightArrow => new(ClientSize.Width - ArrowWidth, 0, ArrowWidth, ClientSize.Height);

        private void Relayout()
        {
            _bounds.Clear();
            int x = 2;
            foreach (var tab in _tabs)
            {
                int w = Math.Clamp(MeasureText(tab) + 12 + 10 + CloseSize + 12, MinTabWidth, MaxTabWidth);
                _bounds.Add(new Rectangle(x, 3, w, ClientSize.Height - 3));
                x += w + 1;
            }
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, TotalWidth - ContentRight));
        }

        private int MeasureText(DocumentTab tab) => TextRenderer.MeasureText(Decorated(tab), Font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width;

        private static string Decorated(DocumentTab tab) => tab.Kind switch
        {
            TabKind.View => "▤ " + tab.DisplayName,
            TabKind.Result => "▶ " + tab.DisplayName,
            _ => tab.DisplayName,
        };

        private void EnsureVisible(DocumentTab? tab)
        {
            int i = tab is null ? -1 : _tabs.IndexOf(tab);
            if (i < 0 || i >= _bounds.Count) return;
            var b = _bounds[i];
            int right = ContentRight;
            if (b.Left - _scroll < 0) _scroll = Math.Max(0, b.Left - 2);
            else if (b.Right - _scroll > right) _scroll = Math.Min(b.Right - right + 2, Math.Max(0, TotalWidth - right));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
            EnsureVisible(_active);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Relayout();
            Invalidate();
        }

        private Rectangle Shown(int index) => new(_bounds[index].X - _scroll, _bounds[index].Y, _bounds[index].Width, _bounds[index].Height);
        private static Rectangle CloseRect(Rectangle tab) => new(tab.Right - CloseSize - 7, tab.Top + (tab.Height - CloseSize) / 2 + 1, CloseSize, CloseSize);

        private int HitTest(Point p, out bool onClose)
        {
            onClose = false;
            if (NeedsArrows && p.X >= ContentRight) return -1;
            for (int i = 0; i < _bounds.Count; i++)
            {
                var r = Shown(i);
                if (!r.Contains(p)) continue;
                onClose = CloseRect(r).Contains(p);
                return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ 그리기

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_pal.Window);
            int right = ContentRight;
            g.SetClip(new Rectangle(0, 0, right, ClientSize.Height));
            for (int i = 0; i < _tabs.Count; i++)
            {
                var r = Shown(i);
                if (r.Right < 0 || r.Left > right) continue;
                DrawTab(g, _tabs[i], r, i);
            }
            if (_dragging && _dropIndex >= 0 && _dropIndex <= _bounds.Count)
            {
                int x = (_dropIndex < _bounds.Count ? Shown(_dropIndex).Left : Shown(_bounds.Count - 1).Right) - 1;
                using var pen = new Pen(_pal.Accent, 2);
                g.DrawLine(pen, x, 4, x, ClientSize.Height - 2);
            }
            g.ResetClip();
            if (NeedsArrows)
            {
                DrawArrow(g, LeftArrow, "‹", _scroll > 0);
                DrawArrow(g, RightArrow, "›", _scroll < TotalWidth - ContentRight);
            }
            using var line = new Pen(_pal.Border);
            g.DrawLine(line, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);
        }

        private void DrawArrow(Graphics g, Rectangle r, string glyph, bool enabled)
        {
            TextRenderer.DrawText(g, glyph, Font, r, enabled ? _pal.Text : _pal.Border,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawTab(Graphics g, DocumentTab tab, Rectangle r, int index)
        {
            bool active = ReferenceEquals(tab, _active);
            bool hover = index == _hover;
            Color back = active ? _pal.Surface : hover ? _pal.MenuHighlight : _pal.HeaderBg;
            using (var br = new SolidBrush(back)) g.FillRectangle(br, r);
            using (var pen = new Pen(_pal.Border))
            {
                g.DrawLine(pen, r.Left, r.Top, r.Left, r.Bottom);
                g.DrawLine(pen, r.Right - 1, r.Top, r.Right - 1, r.Bottom);
                g.DrawLine(pen, r.Left, r.Top, r.Right - 1, r.Top);
            }
            if (active)
            {
                using var accent = new SolidBrush(_pal.Accent);
                g.FillRectangle(accent, r.Left, r.Top, r.Width, 2);
                using var bridge = new SolidBrush(_pal.Surface);
                g.FillRectangle(bridge, r.Left + 1, r.Bottom - 1, r.Width - 2, 1);   // 아래 구분선을 끊어 본문과 이어 보이게
            }

            int textLeft = r.Left + 8;
            bool dirty = tab.HasUnsavedEdits;
            if (dirty)
            {
                TextRenderer.DrawText(g, "●", Font, new Rectangle(textLeft, r.Top, 14, r.Height), Color.FromArgb(214, 140, 0),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                textLeft += 14;
            }
            var close = CloseRect(r);
            var textRect = new Rectangle(textLeft, r.Top, Math.Max(10, close.Left - 4 - textLeft), r.Height);
            using (var font = tab.IsReadOnly ? new Font(Font, FontStyle.Italic) : null)
                TextRenderer.DrawText(g, Decorated(tab), font ?? Font, textRect, active ? _pal.Text : _pal.HeaderText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int percent = tab.IndexingPercent;
            if (percent >= 0)
            {
                using var track = new SolidBrush(Color.FromArgb(60, _pal.Accent));
                g.FillRectangle(track, r.Left + 1, r.Bottom - 3, r.Width - 2, 2);
                using var bar = new SolidBrush(_pal.Accent);
                g.FillRectangle(bar, r.Left + 1, r.Bottom - 3, (r.Width - 2) * percent / 100, 2);
            }

            bool closeHot = hover && _hoverClose;
            if (closeHot)
            {
                using var hot = new SolidBrush(Color.FromArgb(active ? 70 : 90, 128, 128, 128));
                g.FillRectangle(hot, close);
            }
            if (active || hover)
            {
                var old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(_pal.Text, 1.4f);
                int pad = 4;
                g.DrawLine(pen, close.Left + pad, close.Top + pad, close.Right - pad - 1, close.Bottom - pad - 1);
                g.DrawLine(pen, close.Right - pad - 1, close.Top + pad, close.Left + pad, close.Bottom - pad - 1);
                g.SmoothingMode = old;
            }
        }

        // ------------------------------------------------------------------ 마우스

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (NeedsArrows && LeftArrow.Contains(e.Location)) { Scroll(-120); return; }
            if (NeedsArrows && RightArrow.Contains(e.Location)) { Scroll(120); return; }
            int i = HitTest(e.Location, out bool onClose);
            if (i < 0) return;
            var tab = _tabs[i];
            if (e.Button == MouseButtons.Middle) { TabCloseRequested?.Invoke(tab); return; }
            if (e.Button == MouseButtons.Right) { TabActivateRequested?.Invoke(tab); TabContextRequested?.Invoke(tab, PointToScreen(e.Location)); return; }
            if (e.Button != MouseButtons.Left) return;
            if (onClose) { TabCloseRequested?.Invoke(tab); return; }
            _pressIndex = i;
            _pressPoint = e.Location;
            _dragging = false;
            TabActivateRequested?.Invoke(tab);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pressIndex >= 0 && e.Button == MouseButtons.Left && _pressIndex < _tabs.Count)
            {
                if (!_dragging && Math.Abs(e.X - _pressPoint.X) > SystemInformation.DragSize.Width) _dragging = true;
                if (_dragging)
                {
                    _dropIndex = DropIndexAt(e.X);
                    Cursor = Cursors.SizeWE;
                    Invalidate();
                }
                return;
            }
            int i = HitTest(e.Location, out bool onClose);
            if (i != _hover || onClose != _hoverClose)
            {
                _hover = i;
                _hoverClose = onClose;
                string tip = i < 0 ? "" : onClose ? Loc.CurrentLanguage == "ko" ? "닫기 (Ctrl+W)" : "Close (Ctrl+W)" : _tabs[i].Path;
                if (tip != _tipText) { _tipText = tip; _tip.SetToolTip(this, tip.Length == 0 ? null : tip); }
                Invalidate();
            }
        }

        private int DropIndexAt(int x)
        {
            for (int i = 0; i < _bounds.Count; i++)
                if (x < Shown(i).Left + Shown(i).Width / 2) return i;
            return _bounds.Count;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging && _pressIndex >= 0 && _pressIndex < _tabs.Count && _dropIndex >= 0)
            {
                int from = _pressIndex, to = _dropIndex > from ? _dropIndex - 1 : _dropIndex;
                var tab = _tabs[from];
                _pressIndex = -1;
                _dragging = false;
                _dropIndex = -1;
                Cursor = Cursors.Default;
                if (to != from) TabMoveRequested?.Invoke(tab, to);
                Invalidate();
                return;
            }
            _pressIndex = -1;
            _dragging = false;
            _dropIndex = -1;
            Cursor = Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; _hoverClose = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (NeedsArrows) Scroll(-Math.Sign(e.Delta) * 120);
        }

        private void Scroll(int delta)
        {
            _scroll = Math.Clamp(_scroll + delta, 0, Math.Max(0, TotalWidth - ContentRight));
            Invalidate();
        }

        // ------------------------------------------------------------------ 접근성(UI 자동화가 탭을 찾고 누를 수 있게)

        protected override AccessibleObject CreateAccessibilityInstance() => new StripAccessible(this);

        private sealed class StripAccessible : ControlAccessibleObject
        {
            private readonly TabStrip _strip;
            public StripAccessible(TabStrip strip) : base(strip) => _strip = strip;
            public override AccessibleRole Role => AccessibleRole.PageTabList;
            public override int GetChildCount() => _strip._tabs.Count;
            public override AccessibleObject? GetChild(int index) => index >= 0 && index < _strip._tabs.Count ? new TabAccessible(_strip, this, _strip._tabs[index]) : null;
        }

        private sealed class TabAccessible : AccessibleObject
        {
            private readonly TabStrip _strip;
            private readonly AccessibleObject _parent;
            private readonly DocumentTab _tab;
            public TabAccessible(TabStrip strip, AccessibleObject parent, DocumentTab tab) { _strip = strip; _parent = parent; _tab = tab; }
            public override string? Name { get => _tab.DisplayName; set { } }
            public override AccessibleRole Role => AccessibleRole.PageTab;
            public override AccessibleObject? Parent => _parent;
            public override AccessibleStates State =>
                AccessibleStates.Selectable | AccessibleStates.Focusable | (ReferenceEquals(_tab, _strip._active) ? AccessibleStates.Selected | AccessibleStates.Focused : 0);
            public override string? DefaultAction => "Switch";
            public override void DoDefaultAction() => _strip.TabActivateRequested?.Invoke(_tab);
            public override Rectangle Bounds
            {
                get
                {
                    int i = _strip._tabs.IndexOf(_tab);
                    return i < 0 ? Rectangle.Empty : _strip.RectangleToScreen(_strip.Shown(i));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
