using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace NanumCsvViewer
{
    /// <summary>
    /// 패널 머리글·분할선의 단일 규격. 탐색기 머리글(도구 띠)·표 위 셀 주소/값 줄·행 상세 머리글·채팅 페이지 상단 바가 모두 이 값을 쓴다.
    /// WinForms 쪽은 논리 px(96 DPI)를 <c>LogicalToDeviceUnits</c>로 바꿔 쓰고, 채팅 페이지에는 <see cref="CssVars"/>를 테마 메시지(ChatTheme.ThemeMessage)로 보낸다.
    /// 분할선은 모두 팔레트 <see cref="ThemePalette.Border"/> 색의 장치 1px 선이고, 끌 수 있는 면(<see cref="SplitterWidth"/>)은 선 둘레의 배경색 띠다.
    /// </summary>
    internal static class PanelChrome
    {
        /// <summary>머리글 높이(논리 px). 아래쪽 1px 경계선을 포함한다.</summary>
        public const int HeaderHeight = 30;

        /// <summary>머리글 글자 크기. WinForms는 9pt(= 96 DPI에서 12px), 채팅 페이지 CSS는 <see cref="FontPx"/>.</summary>
        public const float FontPoints = 9f;
        public const int FontPx = 12;

        /// <summary>머리글 글자 굵기(CSS font-weight). 보통.</summary>
        public const int FontWeight = 400;

        /// <summary>끌 수 있는 분할선 면의 폭(논리 px). 그 가운데에 1px 선을 그린다.</summary>
        public const int SplitterWidth = 5;

        /// <summary>머리글 배경색 — 도구 모음·탭 띠와 같은 띠 색.</summary>
        public static Color HeaderBackground(ThemePalette p) => p.ToolStrip;

        /// <summary>머리글 글자색.</summary>
        public static Color HeaderText(ThemePalette p) => p.Text;

        /// <summary>머리글 아래 경계선·분할선 색.</summary>
        public static Color LineColor(ThemePalette p) => p.Border;

        /// <summary>팔레트 색에서 채팅 페이지 CSS 변수(접두 "--" 없이)로: chromeH·chromeBg·chromeFg·chromeBorder·chromeFs·chromeFw.</summary>
        public static IReadOnlyDictionary<string, string> CssVars(ThemePalette p) => new Dictionary<string, string>
        {
            ["chromeH"] = HeaderHeight.ToString(CultureInfo.InvariantCulture) + "px",
            ["chromeBg"] = Hex(HeaderBackground(p)),
            ["chromeFg"] = Hex(HeaderText(p)),
            ["chromeBorder"] = Hex(LineColor(p)),
            ["chromeFs"] = FontPx.ToString(CultureInfo.InvariantCulture) + "px",
            ["chromeFw"] = FontWeight.ToString(CultureInfo.InvariantCulture),
        };

        public static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        /// <summary>머리글 아래쪽 경계선(장치 1px)을 그린다.</summary>
        public static void PaintHeaderBottomLine(Graphics g, Rectangle bounds, ThemePalette p)
        {
            using var br = new SolidBrush(LineColor(p));
            g.FillRectangle(br, bounds.Left, bounds.Bottom - 1, bounds.Width, 1);
        }

        // ---- SplitContainer ------------------------------------------------------------------------------------

        /// <summary>선을 분할 면의 어디에 그리나.</summary>
        internal enum DividerLine
        {
            /// <summary>가운데(좌우·상하 패널 사이의 일반 분할선).</summary>
            Center,
            /// <summary>위 패널 쪽 끝이 아니라 아래/오른쪽 끝 — 표 위 셀 줄처럼 분할 면이 머리글의 일부이고 선이 머리글의 아래 경계일 때.</summary>
            Far,
        }

        private sealed class DividerState
        {
            public ThemePalette Palette;
            public DividerLine Line;
            public DividerState(ThemePalette palette, DividerLine line) { Palette = palette; Line = line; }
        }

        private static readonly ConditionalWeakTable<SplitContainer, DividerState> States = new();

        /// <summary>
        /// SplitContainer의 분할 면을 규격대로 칠한다: 배경(<see cref="DividerLine.Center"/>면 창 배경, <see cref="DividerLine.Far"/>면 머리글 배경) + 장치 1px 선.
        /// 같은 분할에 다시 부르면 색만 바꾼다(line을 생략하면 이전 선 위치를 유지). 폭은 호출한 쪽이 정한다(DPI에 따라).
        /// </summary>
        public static void StyleSplitter(SplitContainer sc, ThemePalette p, DividerLine? line = null)
        {
            var state = States.GetValue(sc, s =>
            {
                var st = new DividerState(p, line ?? DividerLine.Center);
                s.Paint += (_, e) => PaintSplitter(s, e.Graphics, st);
                s.SplitterMoved += (_, _) => s.Invalidate(s.SplitterRectangle);
                s.SizeChanged += (_, _) => s.Invalidate(s.SplitterRectangle);
                return st;
            });
            state.Palette = p;
            if (line is { } l) state.Line = l;
            sc.BackColor = state.Line == DividerLine.Far ? HeaderBackground(p) : p.Window;
            sc.Invalidate();
        }

        private static void PaintSplitter(SplitContainer sc, Graphics g, DividerState st)
        {
            var r = sc.SplitterRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            using var br = new SolidBrush(LineColor(st.Palette));
            if (sc.Orientation == Orientation.Vertical)
            {
                int x = st.Line == DividerLine.Far ? r.Right - 1 : r.Left + (r.Width - 1) / 2;
                g.FillRectangle(br, x, r.Top, 1, r.Height);
            }
            else
            {
                int y = st.Line == DividerLine.Far ? r.Bottom - 1 : r.Top + (r.Height - 1) / 2;
                g.FillRectangle(br, r.Left, y, r.Width, 1);
            }
        }
    }

    /// <summary>탐색기와 표 사이의 분할 막대(<see cref="Splitter"/>): 끌 수 있는 면은 창 배경색, 가운데에 팔레트 경계색 1px 선.</summary>
    internal sealed class DividerSplitter : Splitter
    {
        private ThemePalette? _palette;

        public void ApplyPalette(ThemePalette p)
        {
            _palette = p;
            BackColor = p.Window;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_palette is null) return;
            using var br = new SolidBrush(PanelChrome.LineColor(_palette));
            e.Graphics.FillRectangle(br, (Width - 1) / 2, 0, 1, Height);
        }
    }

    /// <summary>
    /// 셀 주소 상자를 담는 머리글 칸. 평소에는 머리글 배경 위의 글자뿐이고(테두리 없음), 상자에 포커스가 있는 동안에만 아래쪽에 강조색 밑줄을 그린다.
    /// 상자는 칸 안에서 세로 가운데에 놓인다.
    /// </summary>
    internal sealed class ChromeAddressHost : Panel
    {
        private ThemePalette? _palette;
        private TextBox? _box;
        private int _gapRight;
        private int _inset;

        public ChromeAddressHost()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        /// <summary>칸 오른쪽 끝의 빈 폭(장치 px) — 밑줄도 이만큼 짧게 그린다.</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int GapRight { get => _gapRight; set { _gapRight = value; LayoutBox(); Invalidate(); } }

        /// <summary>상자 왼쪽 여백(장치 px).</summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int TextInset { get => _inset; set { _inset = value; LayoutBox(); Invalidate(); } }

        /// <summary>밑줄이 지금 보이는가(상자에 포커스가 있다).</summary>
        public bool Underlined => _box is { Focused: true };

        public void ApplyPalette(ThemePalette p)
        {
            _palette = p;
            BackColor = PanelChrome.HeaderBackground(p);
            if (_box is not null)
            {
                _box.BackColor = BackColor;
                _box.ForeColor = p.Text;
            }
            Invalidate();
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control is TextBox tb && _box is null)
            {
                _box = tb;
                tb.BorderStyle = BorderStyle.None;
                tb.Enter += (_, _) => Invalidate();
                tb.Leave += (_, _) => Invalidate();
                tb.GotFocus += (_, _) => Invalidate();
                tb.LostFocus += (_, _) => Invalidate();
                tb.FontChanged += (_, _) => LayoutBox();
                LayoutBox();
            }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutBox();
        }

        private void LayoutBox()
        {
            if (_box is null) return;
            int w = Math.Max(10, Width - _gapRight - _inset);
            int h = _box.Height;
            _box.SetBounds(_inset, Math.Max(0, (Height - h) / 2), w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_palette is null || !Underlined) return;
            using var br = new SolidBrush(_palette.Accent);
            e.Graphics.FillRectangle(br, 0, Height - 1, Math.Max(0, Width - _gapRight), 1);
        }
    }
}
