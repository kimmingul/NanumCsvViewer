using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NanumCsvViewer
{
    internal enum PivotChartKind { Bar, GroupedBar, StackedBar, Line }

    internal sealed class ChartSeries
    {
        public string Name { get; }
        public double[] Values { get; }
        public ChartSeries(string name, double[] values) { Name = name; Values = values; }
    }

    /// <summary>외부 라이브러리 없이 GDI+로 막대/묶은막대/누적막대/꺾은선 차트를 그리는 컨트롤(피벗 차트용).</summary>
    internal sealed class ChartControl : Control
    {
        private string[] _categories = Array.Empty<string>();
        private List<ChartSeries> _series = new();
        private PivotChartKind _kind = PivotChartKind.Bar;
        private ThemePalette _palette = ThemePalette.Light;
        private string _xTitle = "", _yTitle = "";
        private Point _mouse = new(-1, -1);

        private static readonly Color[] SeriesColors =
        {
            Color.FromArgb(46, 111, 176), Color.FromArgb(27, 158, 119), Color.FromArgb(217, 95, 2),
            Color.FromArgb(117, 112, 179), Color.FromArgb(231, 41, 138), Color.FromArgb(102, 166, 30),
            Color.FromArgb(230, 171, 2), Color.FromArgb(166, 118, 29), Color.FromArgb(102, 102, 102),
            Color.FromArgb(0, 158, 115), Color.FromArgb(213, 94, 0), Color.FromArgb(86, 156, 230),
        };

        public ChartControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        [System.ComponentModel.Browsable(false)]
        public PivotChartKind Kind
        {
            get => _kind;
            set { _kind = value; Invalidate(); }
        }

        public void SetData(string[] categories, List<ChartSeries> series, ThemePalette palette, string xTitle, string yTitle)
        {
            _categories = categories;
            _series = series;
            _palette = palette;
            _xTitle = xTitle;
            _yTitle = yTitle;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.Location; Invalidate(); base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _mouse = new(-1, -1); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(_palette.Surface);

            if (_categories.Length == 0 || _series.Count == 0)
            {
                TextRenderer.DrawText(g, "No data", Font, ClientRectangle, _palette.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            using var font = new Font(Font.FontFamily, 8f);
            using var titleFont = new Font(Font.FontFamily, 8f, FontStyle.Bold);

            // 값 범위(0 기준선 포함). 누적이면 카테고리별 합 사용.
            double minV = 0, maxV = 0;
            if (_kind == PivotChartKind.StackedBar)
            {
                for (int c = 0; c < _categories.Length; c++)
                {
                    double pos = 0, neg = 0;
                    foreach (var s in _series)
                    {
                        double v = c < s.Values.Length ? s.Values[c] : 0;
                        if (v >= 0) pos += v; else neg += v;
                    }
                    maxV = Math.Max(maxV, pos);
                    minV = Math.Min(minV, neg);
                }
            }
            else
            {
                foreach (var s in _series)
                    foreach (var v in s.Values) { maxV = Math.Max(maxV, v); minV = Math.Min(minV, v); }
            }
            if (maxV == minV) maxV = minV + 1;

            // 축 눈금(nice numbers)
            double range = NiceNum(maxV - minV, false);
            double tick = NiceNum(range / 5, true);
            double axisMin = Math.Floor(minV / tick) * tick;
            double axisMax = Math.Ceiling(maxV / tick) * tick;

            // 범례 측정(상단)
            int legendH = 22;
            var plot = new Rectangle(58, 10 + legendH, Width - 58 - 16, Height - 10 - legendH - 46);
            if (plot.Width < 20 || plot.Height < 20) return;

            // 범례
            DrawLegend(g, font, new Rectangle(58, 8, Width - 58 - 8, legendH));

            // y축 격자 + 라벨
            using var gridPen = new Pen(_palette.Border) { DashStyle = DashStyle.Dot };
            using var axisPen = new Pen(_palette.Border, 1.2f);
            for (double v = axisMin; v <= axisMax + tick / 2; v += tick)
            {
                int y = ValueToY(v, axisMin, axisMax, plot);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, FormatTick(v), font, new Rectangle(0, y - 8, 54, 16), _palette.Text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            g.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
            int zeroY = ValueToY(0, axisMin, axisMax, plot);
            g.DrawLine(axisPen, plot.Left, zeroY, plot.Right, zeroY);

            // 카테고리 슬롯
            int n = _categories.Length;
            float slotW = plot.Width / (float)n;

            // 차트 본체
            if (_kind == PivotChartKind.Line)
                DrawLines(g, plot, slotW, axisMin, axisMax);
            else
                DrawBars(g, plot, slotW, axisMin, axisMax, zeroY);

            // x축 카테고리 라벨
            for (int c = 0; c < n; c++)
            {
                float cx = plot.Left + slotW * (c + 0.5f);
                var lr = new Rectangle((int)(cx - slotW / 2), plot.Bottom + 4, (int)slotW, 38);
                TextRenderer.DrawText(g, _categories[c], font, lr, _palette.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordEllipsis);
            }

            // 축 제목
            if (!string.IsNullOrEmpty(_xTitle))
                TextRenderer.DrawText(g, _xTitle, titleFont, new Rectangle(plot.Left, Height - 16, plot.Width, 14),
                    _palette.Text, TextFormatFlags.HorizontalCenter);

            // 호버 툴팁
            DrawHoverTooltip(g, font, plot, slotW, axisMin, axisMax, zeroY);
        }

        private void DrawLegend(Graphics g, Font font, Rectangle area)
        {
            int x = area.Left;
            using var font2 = new Font(font, FontStyle.Regular);
            for (int i = 0; i < _series.Count; i++)
            {
                Color col = SeriesColors[i % SeriesColors.Length];
                using var b = new SolidBrush(col);
                g.FillRectangle(b, x, area.Top + 4, 12, 12);
                Size ts = TextRenderer.MeasureText(g, _series[i].Name, font2);
                TextRenderer.DrawText(g, _series[i].Name, font2, new Point(x + 15, area.Top + 3), _palette.Text);
                x += 15 + ts.Width + 14;
                if (x > area.Right - 40) break; // 넘치면 생략
            }
        }

        private void DrawBars(Graphics g, Rectangle plot, float slotW, double axisMin, double axisMax, int zeroY)
        {
            int n = _categories.Length;
            bool single = _kind == PivotChartKind.Bar;
            int seriesCount = single ? 1 : _series.Count;

            for (int c = 0; c < n; c++)
            {
                float slotLeft = plot.Left + slotW * c;
                if (_kind == PivotChartKind.StackedBar)
                {
                    float barW = slotW * 0.6f;
                    float bx = slotLeft + (slotW - barW) / 2;
                    double accPos = 0, accNeg = 0;
                    for (int s = 0; s < _series.Count; s++)
                    {
                        double v = c < _series[s].Values.Length ? _series[s].Values[c] : 0;
                        double from = v >= 0 ? accPos : accNeg;
                        double to = from + v;
                        int y1 = ValueToY(from, axisMin, axisMax, plot);
                        int y2 = ValueToY(to, axisMin, axisMax, plot);
                        var rect = RectFrom(bx, y1, barW, y2);
                        using var b = new SolidBrush(SeriesColors[s % SeriesColors.Length]);
                        g.FillRectangle(b, rect);
                        if (v >= 0) accPos = to; else accNeg = to;
                    }
                }
                else
                {
                    float groupW = slotW * 0.72f;
                    float barW = groupW / seriesCount;
                    float gx = slotLeft + (slotW - groupW) / 2;
                    for (int s = 0; s < seriesCount; s++)
                    {
                        double v = c < _series[s].Values.Length ? _series[s].Values[c] : 0;
                        int y2 = ValueToY(v, axisMin, axisMax, plot);
                        var rect = RectFrom(gx + barW * s, zeroY, barW * 0.86f, y2);
                        using var b = new SolidBrush(SeriesColors[s % SeriesColors.Length]);
                        g.FillRectangle(b, rect);
                    }
                }
            }
        }

        private void DrawLines(Graphics g, Rectangle plot, float slotW, double axisMin, double axisMax)
        {
            for (int s = 0; s < _series.Count; s++)
            {
                var pts = new List<PointF>();
                for (int c = 0; c < _categories.Length; c++)
                {
                    double v = c < _series[s].Values.Length ? _series[s].Values[c] : 0;
                    pts.Add(new PointF(plot.Left + slotW * (c + 0.5f), ValueToY(v, axisMin, axisMax, plot)));
                }
                Color col = SeriesColors[s % SeriesColors.Length];
                using var pen = new Pen(col, 2f) { LineJoin = LineJoin.Round };
                if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                using var b = new SolidBrush(col);
                foreach (var p in pts) g.FillEllipse(b, p.X - 2.5f, p.Y - 2.5f, 5, 5);
            }
        }

        private void DrawHoverTooltip(Graphics g, Font font, Rectangle plot, float slotW, double axisMin, double axisMax, int zeroY)
        {
            if (_mouse.X < plot.Left || _mouse.X > plot.Right || _mouse.Y < plot.Top || _mouse.Y > plot.Bottom) return;
            int c = (int)((_mouse.X - plot.Left) / slotW);
            if (c < 0 || c >= _categories.Length) return;

            var lines = new List<string> { _categories[c] };
            for (int s = 0; s < _series.Count; s++)
            {
                double v = c < _series[s].Values.Length ? _series[s].Values[c] : 0;
                lines.Add($"{_series[s].Name}: {FormatTick(v)}");
                if (_kind == PivotChartKind.Bar) break;
            }

            using var tf = new Font(font, FontStyle.Regular);
            int w = 0, h = 6;
            foreach (var l in lines) { Size ts = TextRenderer.MeasureText(g, l, tf); w = Math.Max(w, ts.Width); h += ts.Height; }
            w += 14;
            int tx = Math.Min(_mouse.X + 14, Width - w - 4);
            int ty = Math.Min(_mouse.Y + 12, Height - h - 4);

            // 카테고리 강조선
            float cx = plot.Left + slotW * (c + 0.5f);
            using (var vp = new Pen(Color.FromArgb(80, _palette.Text))) g.DrawLine(vp, cx, plot.Top, cx, plot.Bottom);

            var box = new Rectangle(tx, ty, w, h);
            using (var bg = new SolidBrush(_palette.Window))
            using (var br = new Pen(_palette.Border))
            {
                g.FillRectangle(bg, box);
                g.DrawRectangle(br, box);
            }
            int yy = ty + 3;
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    Color col = SeriesColors[(i - 1) % SeriesColors.Length];
                    using var sb = new SolidBrush(col);
                    g.FillRectangle(sb, tx + 5, yy + 4, 8, 8);
                }
                TextRenderer.DrawText(g, lines[i], tf, new Point(tx + (i == 0 ? 6 : 16), yy),
                    _palette.Text);
                yy += TextRenderer.MeasureText(g, lines[i], tf).Height;
            }
        }

        private static Rectangle RectFrom(float x, int yA, float w, int yB)
        {
            int top = Math.Min(yA, yB), bot = Math.Max(yA, yB);
            return new Rectangle((int)x, top, Math.Max(1, (int)w), Math.Max(1, bot - top));
        }

        private static int ValueToY(double v, double axisMin, double axisMax, Rectangle plot)
        {
            double t = (v - axisMin) / (axisMax - axisMin);
            return (int)(plot.Bottom - t * plot.Height);
        }

        private static string FormatTick(double v)
            => v == Math.Truncate(v) && Math.Abs(v) < 1e15 ? v.ToString("#,##0") : v.ToString("#,##0.###");

        // Heckbert의 nice number 알고리즘(축 눈금 산정).
        private static double NiceNum(double x, bool round)
        {
            if (x <= 0) return 1;
            double exp = Math.Floor(Math.Log10(x));
            double f = x / Math.Pow(10, exp);
            double nf = round
                ? (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10)
                : (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10);
            return nf * Math.Pow(10, exp);
        }
    }
}
