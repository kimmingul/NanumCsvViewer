using System.Drawing.Drawing2D;

namespace NanumCsvViewer
{
    /// <summary>
    /// 외부 에셋 없이 코드로 그리는 16×16 툴바/메뉴 글리프 아이콘.
    /// 일관된 선 두께·색으로 그려 텍스트 라벨 없이도 기능을 식별할 수 있게 합니다.
    /// </summary>
    internal static class UiIcons
    {
        private const int S = 16;

        private static readonly Color Ink = Color.FromArgb(70, 70, 74);
        private static readonly Color Blue = Color.FromArgb(30, 110, 200);
        private static readonly Color Green = Color.FromArgb(40, 150, 70);
        private static readonly Color Red = Color.FromArgb(205, 65, 55);
        private static readonly Color Amber = Color.FromArgb(210, 150, 30);

        private static Bitmap New(out Graphics g)
        {
            var bmp = new Bitmap(S, S);
            g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return bmp;
        }

        private static Pen Stroke(Color c, float w = 1.6f) =>
            new(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        // ── File ──────────────────────────────────────────────
        public static Bitmap Open()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var body = new SolidBrush(Color.FromArgb(255, 200, 110));
                using var back = new SolidBrush(Amber);
                g.FillPolygon(back, new[] { new PointF(2, 5), new PointF(3, 3), new PointF(7, 3), new PointF(8, 5) });
                g.FillRectangle(back, 2, 5, 12, 8);
                g.FillPolygon(body, new[] { new PointF(3, 7), new PointF(15, 7), new PointF(13, 13), new PointF(1, 13) });
            }
            return bmp;
        }

        public static Bitmap Quit()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Red);
                g.DrawRectangle(p, 3, 3, 5, 10);          // door frame
                g.DrawLine(p, 8, 8, 14, 8);               // arrow shaft (exit →)
                g.DrawLine(p, 11, 5, 14, 8);
                g.DrawLine(p, 11, 11, 14, 8);
            }
            return bmp;
        }

        // ── Find ──────────────────────────────────────────────
        public static Bitmap Find()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Blue);
                g.DrawEllipse(p, 3, 3, 7, 7);
                g.DrawLine(p, 9.5f, 9.5f, 13.5f, 13.5f);
            }
            return bmp;
        }

        public static Bitmap FindNext()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Blue);
                g.DrawEllipse(p, 2.5f, 3, 6.5f, 6.5f);
                g.DrawLine(p, 8.5f, 9, 11.5f, 12);
                using var pr = Stroke(Green, 1.7f);
                g.DrawLine(pr, 11, 12.5f, 14.5f, 12.5f);  // ▶ next arrow
                g.DrawLine(pr, 12.5f, 10.7f, 14.5f, 12.5f);
                g.DrawLine(pr, 12.5f, 14.3f, 14.5f, 12.5f);
            }
            return bmp;
        }

        // ── Filter ────────────────────────────────────────────
        private static PointF[] Funnel => new[]
        {
            new PointF(2.5f, 3.5f), new PointF(13.5f, 3.5f),
            new PointF(9.5f, 8.5f), new PointF(9.5f, 13f),
            new PointF(6.5f, 11.5f), new PointF(6.5f, 8.5f),
        };

        public static Bitmap Filter()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var b = new SolidBrush(Green);
                g.FillPolygon(b, Funnel);
            }
            return bmp;
        }

        public static Bitmap FilterByCell()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var b = new SolidBrush(Green);
                g.FillPolygon(b, Funnel);
                using var cell = new SolidBrush(Blue);
                g.FillRectangle(cell, 11, 10, 4, 4);      // highlighted cell
            }
            return bmp;
        }

        public static Bitmap ClearFilter()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var b = new SolidBrush(Color.FromArgb(150, 150, 155));
                g.FillPolygon(b, Funnel);
                using var p = Stroke(Red, 1.8f);
                g.DrawLine(p, 10.5f, 9.5f, 15, 14);       // red X
                g.DrawLine(p, 15, 9.5f, 10.5f, 14);
            }
            return bmp;
        }

        // ── Sort ──────────────────────────────────────────────
        private static void Bars(Graphics g, Pen p)
        {
            g.DrawLine(p, 2.5f, 4, 7.5f, 4);
            g.DrawLine(p, 2.5f, 8, 9.5f, 8);
            g.DrawLine(p, 2.5f, 12, 11.5f, 12);
        }

        public static Bitmap SortAscending()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Ink);
                Bars(g, p);
                using var a = Stroke(Blue, 1.7f);
                g.DrawLine(a, 13.5f, 3, 13.5f, 13);
                g.DrawLine(a, 11.5f, 5, 13.5f, 3);
                g.DrawLine(a, 15.5f, 5, 13.5f, 3);
            }
            return bmp;
        }

        public static Bitmap SortDescending()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Ink);
                Bars(g, p);
                using var a = Stroke(Blue, 1.7f);
                g.DrawLine(a, 13.5f, 3, 13.5f, 13);
                g.DrawLine(a, 11.5f, 11, 13.5f, 13);
                g.DrawLine(a, 15.5f, 11, 13.5f, 13);
            }
            return bmp;
        }

        public static Bitmap ClearSort()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Color.FromArgb(150, 150, 155));
                Bars(g, p);
                using var r = Stroke(Red, 1.8f);
                g.DrawLine(r, 11, 2.5f, 15, 6.5f);        // red X
                g.DrawLine(r, 15, 2.5f, 11, 6.5f);
            }
            return bmp;
        }

        // ── View ──────────────────────────────────────────────
        public static Bitmap DetailPanel()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Ink);
                g.DrawRectangle(p, 2, 3, 12, 10);
                using var fill = new SolidBrush(Blue);
                g.FillRectangle(fill, 10, 4, 3, 8);       // right detail pane
                g.DrawLine(p, 9.5f, 3, 9.5f, 13);
            }
            return bmp;
        }

        public static Bitmap Encoding()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Blue, 1.3f);
                g.DrawEllipse(p, 2.5f, 2.5f, 11, 11);     // globe
                g.DrawLine(p, 2.5f, 8, 13.5f, 8);
                g.DrawEllipse(p, 5.5f, 2.5f, 5, 11);
            }
            return bmp;
        }

        // ── Help ──────────────────────────────────────────────
        public static Bitmap About()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var p = Stroke(Blue, 1.4f);
                g.DrawEllipse(p, 2.5f, 2.5f, 11, 11);
                using var b = new SolidBrush(Blue);
                g.FillEllipse(b, 7.2f, 5, 1.6f, 1.6f);    // dot of "i"
                g.FillRectangle(b, 7.3f, 7.5f, 1.4f, 4.5f);
            }
            return bmp;
        }

        // ── Type badge toggle ─────────────────────────────────
        public static Bitmap TypeBadge()
        {
            var bmp = New(out var g);
            using (g)
            {
                // 둥근 라벨(배지) 모양 + 흰 점 — 헤더 타입 배지를 상징
                using var b = new SolidBrush(Blue);
                using var path = new GraphicsPath();
                var r = new RectangleF(2.5f, 5f, 11f, 6f);
                const float d = 3f;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
                using var w = new SolidBrush(Color.White);
                g.FillEllipse(w, 4.2f, 7f, 2f, 2f);
            }
            return bmp;
        }

        // ── Theme toggle ──────────────────────────────────────
        public static Bitmap Sun()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var b = new SolidBrush(Amber);
                g.FillEllipse(b, 5.5f, 5.5f, 5, 5);       // core
                using var p = Stroke(Amber, 1.5f);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    float cx = 8, cy = 8;
                    g.DrawLine(p,
                        cx + (float)Math.Cos(a) * 6, cy + (float)Math.Sin(a) * 6,
                        cx + (float)Math.Cos(a) * 7.3f, cy + (float)Math.Sin(a) * 7.3f);
                }
            }
            return bmp;
        }

        public static Bitmap Moon()
        {
            var bmp = New(out var g);
            using (g)
            {
                using var b = new SolidBrush(Color.FromArgb(90, 120, 200));
                // 보름달에서 우상단을 배경색으로 도려내 초승달 모양 생성
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddEllipse(3, 3, 10, 10);
                g.FillPath(b, path);
                using var cut = new SolidBrush(Color.Transparent);
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                using var erase = new SolidBrush(Color.FromArgb(0, 0, 0, 0));
                g.FillEllipse(erase, 6.5f, 1.5f, 9, 9);
            }
            return bmp;
        }
    }
}
