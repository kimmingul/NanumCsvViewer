using System.Drawing.Drawing2D;

namespace NanumCsvViewer.Charting
{
    /// <summary>
    /// PlotModel을 그리는 얇은 GDI+ 렌더러(이슈 #19). 계산은 전부 PlotMath/ChartBuilders에 있고
    /// 여기는 좌표 변환·페인트·상호작용(툴팁·드래그/휠 줌·히트맵 셀 클릭)만 담당한다.
    /// 밀도 산점은 줌 시 PlotMath.ComputeDensityGrid로 재비닝하며, 가시 점이 임계 이하로 줄면 점 렌더로 복귀.
    /// </summary>
    internal sealed class PlotControl : Control
    {
        private PlotModel? _model;
        private ThemePalette _palette = ThemePalette.Light;

        // 뷰(줌) 상태 — 수치축 범위. 카테고리축은 줌 없음.
        private double _vxMin, _vxMax, _vyMin, _vyMax;
        private bool _zoomed;

        // 상호작용 상태
        private Point _mouse = new(-1, -1);
        private Point? _dragStart;
        private Rectangle _dragRect;

        // 밀도 격자 캐시(뷰포트 단위)
        private PlotMath.DensityGrid? _densityCache;
        private (double, double, double, double)? _densityCacheKey;
        private int _visiblePointCache = -1;

        /// <summary>히트맵 셀 클릭(rowIndex, colIndex) — 상관 히트맵 → 산점도 드릴다운용.</summary>
        public event Action<int, int>? HeatCellClicked;

        public PlotControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        public PlotModel? Model => _model;

        public void SetModel(PlotModel? model, ThemePalette palette)
        {
            _model = model;
            _palette = palette;
            ResetView();
        }

        public void ResetView()
        {
            if (_model is not null)
            {
                _vxMin = _model.XAxis.Min; _vxMax = _model.XAxis.Max;
                _vyMin = _model.YAxis.Min; _vyMax = _model.YAxis.Max;
                // 0-범위 가드: 카테고리 축이 Min/Max를 안 채웠거나 상수 데이터일 때 0-나눗셈(GDI 오버플로) 방지.
                if (_vxMax <= _vxMin) _vxMax = _vxMin + Math.Max(1, _model.XAxis.Categories.Count);
                if (_vyMax <= _vyMin) _vyMax = _vyMin + 1;
            }
            _zoomed = false;
            _densityCache = null;
            _densityCacheKey = null;
            _visiblePointCache = -1;
            Invalidate();
        }

        /// <summary>PNG 저장·클립보드용 고해상도 렌더.</summary>
        public Bitmap RenderBitmap(int scale = 2)
        {
            int w = Math.Max(320, Width) * scale, h = Math.Max(240, Height) * scale;
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.ScaleTransform(scale, scale);
            RenderTo(g, new Rectangle(0, 0, w / scale, h / scale), interactive: false);
            return bmp;
        }

        // ---------------------------------------------------------------- 페인트

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderTo(e.Graphics, ClientRectangle, interactive: true);
        }

        private void RenderTo(Graphics g, Rectangle area, bool interactive)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(_palette.Surface);

            var m = _model;
            if (m is null || m.Series.Count == 0)
            {
                TextRenderer.DrawText(g, "No data", Font, area, _palette.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            using var font = new Font(Font.FontFamily, 8f);
            using var titleFont = new Font(Font.FontFamily, 8f, FontStyle.Bold);

            bool hasY2 = m.Y2Axis is not null;
            int legendH = m.Series.Count(s => !string.IsNullOrEmpty(s.Name)) > 1 ? 20 : 4;
            var plot = new Rectangle(area.Left + 62, area.Top + 8 + legendH,
                area.Width - 62 - (hasY2 ? 52 : 18), area.Height - 8 - legendH - 48);
            if (plot.Width < 30 || plot.Height < 30) return;

            if (legendH > 4) DrawLegend(g, font, new Rectangle(plot.Left, area.Top + 6, plot.Width, legendH), m);

            // 축 + 격자
            if (m.XAxis.Kind == PlotAxisKind.Category && m.YAxis.Kind == PlotAxisKind.Category)
                DrawHeatAxes(g, font, plot, m);
            else
            {
                DrawYAxis(g, font, plot, m);
                if (m.XAxis.Kind == PlotAxisKind.Numeric) DrawXAxisNumeric(g, font, plot, m);
                else DrawXAxisCategories(g, font, plot, m.XAxis.Categories);
                if (hasY2) DrawY2Axis(g, font, plot, m.Y2Axis!);
            }

            // 시리즈
            var oldClip = g.Clip;
            g.SetClip(plot);
            foreach (var s in m.Series)
            {
                switch (s.Kind)
                {
                    case PlotSeriesKind.Bars: DrawHistBars(g, plot, s); break;
                    case PlotSeriesKind.CategoryBars: DrawCategoryBars(g, plot, m, s); break;
                    case PlotSeriesKind.Points: DrawPoints(g, plot, s); break;
                    case PlotSeriesKind.Line: DrawLine(g, plot, m, s); break;
                    case PlotSeriesKind.Boxes: DrawBoxes(g, plot, m, s); break;
                    case PlotSeriesKind.HeatCells: DrawHeatCells(g, font, plot, m, s); break;
                    case PlotSeriesKind.Density: DrawDensity(g, plot, s); break;
                }
            }

            // 기준선(평균·중앙값)
            foreach (var r in m.ReferenceLines)
            {
                using var pen = new Pen(ChartCommon.SeriesColor(r.PaletteIndex)) { DashStyle = r.Dashed ? DashStyle.Dash : DashStyle.Solid };
                if (r.Vertical)
                {
                    int x = XToPx(r.Value, plot);
                    g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
                    TextRenderer.DrawText(g, r.Label, font, new Point(Math.Min(x + 3, plot.Right - 60), plot.Top + 2),
                        ChartCommon.SeriesColor(r.PaletteIndex));
                }
                else
                {
                    int y = YToPx(r.Value, plot);
                    g.DrawLine(pen, plot.Left, y, plot.Right, y);
                    TextRenderer.DrawText(g, r.Label, font, new Point(plot.Left + 3, Math.Max(y - 15, plot.Top)),
                        ChartCommon.SeriesColor(r.PaletteIndex));
                }
            }
            g.Clip = oldClip;

            // 렌더 정직성 노트(우하단) + 줌 표시
            string note = m.RenderNote + (_zoomed ? "  ·  zoom (더블클릭=초기화)" : "");
            if (!string.IsNullOrEmpty(note))
                TextRenderer.DrawText(g, note, font,
                    new Rectangle(plot.Left, area.Bottom - 16, plot.Width, 14), Color.FromArgb(150, _palette.Text),
                    TextFormatFlags.Right);

            // 축 제목
            if (!string.IsNullOrEmpty(m.XAxis.Title))
                TextRenderer.DrawText(g, m.XAxis.Title, titleFont,
                    new Rectangle(plot.Left, plot.Bottom + 30, plot.Width, 14), _palette.Text, TextFormatFlags.HorizontalCenter);

            if (interactive)
            {
                if (_dragStart is not null && _dragRect.Width > 2 && _dragRect.Height > 2)
                {
                    using var db = new SolidBrush(Color.FromArgb(40, _palette.Accent));
                    using var dp = new Pen(_palette.Accent) { DashStyle = DashStyle.Dash };
                    g.FillRectangle(db, _dragRect);
                    g.DrawRectangle(dp, _dragRect);
                }
                DrawTooltip(g, font, plot, m);
            }
        }

        // ---------------------------------------------------------------- 좌표 변환

        private int XToPx(double x, Rectangle plot)
            => plot.Left + (int)((x - _vxMin) / (_vxMax - _vxMin) * plot.Width);

        private int YToPx(double y, Rectangle plot)
            => plot.Bottom - (int)((y - _vyMin) / (_vyMax - _vyMin) * plot.Height);

        private double PxToX(int px, Rectangle plot)
            => _vxMin + (px - plot.Left) / (double)plot.Width * (_vxMax - _vxMin);

        private double PxToY(int py, Rectangle plot)
            => _vyMin + (plot.Bottom - py) / (double)plot.Height * (_vyMax - _vyMin);

        private int Y2ToPx(double y, Rectangle plot, PlotAxis y2)
            => plot.Bottom - (int)((y - y2.Min) / (y2.Max - y2.Min) * plot.Height);

        private Rectangle PlotArea()
        {
            var m = _model!;
            bool hasY2 = m.Y2Axis is not null;
            int legendH = m.Series.Count(s => !string.IsNullOrEmpty(s.Name)) > 1 ? 20 : 4;
            return new Rectangle(62, 8 + legendH, Width - 62 - (hasY2 ? 52 : 18), Height - 8 - legendH - 48);
        }

        // ---------------------------------------------------------------- 축

        private void DrawYAxis(Graphics g, Font font, Rectangle plot, PlotModel m)
        {
            using var gridPen = new Pen(_palette.Border) { DashStyle = DashStyle.Dot };
            using var axisPen = new Pen(_palette.Border, 1.2f);
            double range = ChartCommon.NiceNum(_vyMax - _vyMin, false);
            double tick = ChartCommon.NiceNum(range / 5, true);
            double start = Math.Ceiling(_vyMin / tick) * tick;
            for (double v = start; v <= _vyMax + tick / 1e6; v += tick)
            {
                int y = YToPx(v, plot);
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, ChartCommon.FormatTick(v), font, new Rectangle(0, y - 8, 58, 16),
                    _palette.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            g.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
            g.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        }

        private void DrawY2Axis(Graphics g, Font font, Rectangle plot, PlotAxis y2)
        {
            using var axisPen = new Pen(_palette.Border, 1.2f);
            g.DrawLine(axisPen, plot.Right, plot.Top, plot.Right, plot.Bottom);
            double tick = ChartCommon.NiceNum((y2.Max - y2.Min) / 5, true);
            for (double v = y2.Min; v <= y2.Max + tick / 1e6; v += tick)
            {
                int y = Y2ToPx(v, plot, y2);
                string label = y2.IsPercent ? $"{v:0}%" : ChartCommon.FormatTick(v);
                TextRenderer.DrawText(g, label, font, new Rectangle(plot.Right + 3, y - 8, 46, 16),
                    _palette.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawXAxisNumeric(Graphics g, Font font, Rectangle plot, PlotModel m)
        {
            if (m.XAxis.TickLabels is { } labels)
            {
                // 인덱스 라벨(시계열 빈): 최대 ~12개만 표시
                int count = labels.Count;
                int step = Math.Max(1, (int)Math.Ceiling(count / 12.0));
                for (int i = 0; i < count; i += step)
                {
                    if (i < _vxMin - 0.5 || i > _vxMax + 0.5) continue;
                    int x = XToPx(i, plot);
                    TextRenderer.DrawText(g, labels[i], font, new Rectangle(x - 42, plot.Bottom + 4, 84, 26),
                        _palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordEllipsis);
                }
                return;
            }
            double range = ChartCommon.NiceNum(_vxMax - _vxMin, false);
            double tick = ChartCommon.NiceNum(range / 6, true);
            double start = Math.Ceiling(_vxMin / tick) * tick;
            using var gridPen = new Pen(_palette.Border) { DashStyle = DashStyle.Dot };
            for (double v = start; v <= _vxMax + tick / 1e6; v += tick)
            {
                int x = XToPx(v, plot);
                g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                TextRenderer.DrawText(g, ChartCommon.FormatTick(v), font, new Rectangle(x - 40, plot.Bottom + 4, 80, 16),
                    _palette.Text, TextFormatFlags.HorizontalCenter);
            }
        }

        private void DrawXAxisCategories(Graphics g, Font font, Rectangle plot, IReadOnlyList<string> cats)
        {
            int n = Math.Max(1, cats.Count);
            float slotW = plot.Width / (float)n;
            int step = Math.Max(1, (int)Math.Ceiling(n / Math.Max(1.0, plot.Width / 70.0)));
            for (int i = 0; i < n; i += step)
            {
                float cx = plot.Left + slotW * (i + 0.5f);
                TextRenderer.DrawText(g, cats[i], font,
                    new Rectangle((int)(cx - Math.Max(slotW, 68) / 2), plot.Bottom + 4, (int)Math.Max(slotW, 68), 26),
                    _palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordEllipsis);
            }
        }

        private void DrawHeatAxes(Graphics g, Font font, Rectangle plot, PlotModel m)
        {
            using var axisPen = new Pen(_palette.Border, 1.2f);
            g.DrawRectangle(axisPen, plot);
            var xs = m.XAxis.Categories; var ys = m.YAxis.Categories;
            float cw = plot.Width / (float)Math.Max(1, xs.Count);
            float ch = plot.Height / (float)Math.Max(1, ys.Count);
            for (int c = 0; c < xs.Count; c++)
                TextRenderer.DrawText(g, xs[c], font,
                    new Rectangle((int)(plot.Left + cw * c), plot.Bottom + 4, (int)Math.Max(cw, 40), 26),
                    _palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordEllipsis);
            for (int r = 0; r < ys.Count; r++)
                TextRenderer.DrawText(g, ys[r], font,
                    new Rectangle(0, (int)(plot.Top + ch * r + ch / 2 - 8), 58, 16),
                    _palette.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.WordEllipsis);
        }

        private void DrawLegend(Graphics g, Font font, Rectangle area, PlotModel m)
        {
            int x = area.Left;
            foreach (var s in m.Series)
            {
                if (string.IsNullOrEmpty(s.Name)) continue;
                var col = ChartCommon.SeriesColor(s.PaletteIndex);
                using var b = new SolidBrush(col);
                g.FillRectangle(b, x, area.Top + 4, 12, 12);
                Size ts = TextRenderer.MeasureText(g, s.Name, font);
                TextRenderer.DrawText(g, s.Name, font, new Point(x + 15, area.Top + 3), _palette.Text);
                x += 15 + ts.Width + 14;
                if (x > area.Right - 40) break;
            }
        }

        // ---------------------------------------------------------------- 시리즈 렌더

        private void DrawHistBars(Graphics g, Rectangle plot, PlotSeries s)
        {
            var col = ChartCommon.SeriesColor(s.PaletteIndex);
            using var fill = new SolidBrush(Color.FromArgb(180, col));
            using var pen = new Pen(_palette.Surface);
            int zero = YToPx(Math.Max(0, _vyMin), plot);
            for (int i = 0; i < s.BarHeight.Length; i++)
            {
                int x1 = XToPx(s.BarLeft[i], plot);
                int x2 = XToPx(s.BarRight[i], plot);
                int y = YToPx(s.BarHeight[i], plot);
                if (x2 <= x1) x2 = x1 + 1;
                var rect = Rectangle.FromLTRB(x1, Math.Min(y, zero), x2, Math.Max(y, zero));
                g.FillRectangle(fill, rect);
                g.DrawRectangle(pen, rect);
            }
        }

        private void DrawCategoryBars(Graphics g, Rectangle plot, PlotModel m, PlotSeries s)
        {
            int n = Math.Max(1, m.XAxis.Categories.Count);
            float slotW = plot.Width / (float)n;
            var col = ChartCommon.SeriesColor(s.PaletteIndex);
            using var fill = new SolidBrush(Color.FromArgb(190, col));
            int zero = YToPx(Math.Max(0, _vyMin), plot);
            for (int i = 0; i < s.Ys.Length && i < n; i++)
            {
                float bw = slotW * 0.7f;
                float bx = plot.Left + slotW * i + (slotW - bw) / 2;
                int y = YToPx(s.Ys[i], plot);
                g.FillRectangle(fill, bx, Math.Min(y, zero), bw, Math.Abs(zero - y));
            }
        }

        private void DrawPoints(Graphics g, Rectangle plot, PlotSeries s)
        {
            var col = ChartCommon.SeriesColor(s.PaletteIndex);
            using var b = new SolidBrush(Color.FromArgb(110, col));
            for (int i = 0; i < s.Xs.Length; i++)
            {
                double x = s.Xs[i], y = s.Ys[i];
                if (x < _vxMin || x > _vxMax || y < _vyMin || y > _vyMax) continue;
                g.FillEllipse(b, XToPx(x, plot) - 2.4f, YToPx(y, plot) - 2.4f, 4.8f, 4.8f);
            }
        }

        private void DrawLine(Graphics g, Rectangle plot, PlotModel m, PlotSeries s)
        {
            var col = ChartCommon.SeriesColor(s.PaletteIndex);
            using var pen = new Pen(col, 2f) { LineJoin = LineJoin.Round };
            if (s.Dashed) pen.DashStyle = DashStyle.Dash;
            var pts = new List<PointF>(s.Xs.Length);
            for (int i = 0; i < s.Xs.Length; i++)
            {
                float px = plot.Left + (float)((s.Xs[i] - _vxMin) / (_vxMax - _vxMin) * plot.Width);
                float py = s.UseSecondaryAxis && m.Y2Axis is { } y2
                    ? plot.Bottom - (float)((s.Ys[i] - y2.Min) / (y2.Max - y2.Min) * plot.Height)
                    : plot.Bottom - (float)((s.Ys[i] - _vyMin) / (_vyMax - _vyMin) * plot.Height);
                pts.Add(new PointF(px, py));
            }
            if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
            else if (pts.Count == 1)
            {
                using var single = new SolidBrush(col);
                g.FillEllipse(single, pts[0].X - 2, pts[0].Y - 2, 4, 4);
            }
            // 점 마커는 시계열(라벨 축)에서만 — 곡선(KDE·정규)은 매끈하게 선만.
            if (m.XAxis.TickLabels is not null && pts.Count <= 120)
            {
                using var mb = new SolidBrush(col);
                foreach (var p in pts) g.FillEllipse(mb, p.X - 2.5f, p.Y - 2.5f, 5, 5);
            }
        }

        private void DrawBoxes(Graphics g, Rectangle plot, PlotModel m, PlotSeries s)
        {
            int n = Math.Max(1, m.XAxis.Categories.Count);
            float slotW = plot.Width / (float)n;
            var col = ChartCommon.SeriesColor(s.PaletteIndex);
            using var fill = new SolidBrush(Color.FromArgb(90, col));
            using var pen = new Pen(col, 1.6f);
            using var medianPen = new Pen(ChartCommon.SeriesColor(2), 2f);
            using var outlierBrush = new SolidBrush(Color.FromArgb(150, ChartCommon.SeriesColor(4)));

            for (int i = 0; i < s.Boxes.Count && i < n; i++)
            {
                var b = s.Boxes[i];
                float cx = plot.Left + slotW * (i + 0.5f);
                float bw = Math.Min(slotW * 0.55f, 60);
                int yQ1 = YToPx(b.Q1, plot), yQ3 = YToPx(b.Q3, plot);
                int yMed = YToPx(b.Median, plot);
                int yLo = YToPx(b.WhiskerLow, plot), yHi = YToPx(b.WhiskerHigh, plot);

                g.DrawLine(pen, cx, yHi, cx, yQ3);                                   // 위 수염
                g.DrawLine(pen, cx, yQ1, cx, yLo);                                   // 아래 수염
                g.DrawLine(pen, cx - bw / 4, yHi, cx + bw / 4, yHi);
                g.DrawLine(pen, cx - bw / 4, yLo, cx + bw / 4, yLo);
                var rect = Rectangle.FromLTRB((int)(cx - bw / 2), Math.Min(yQ3, yQ1), (int)(cx + bw / 2), Math.Max(yQ3, yQ1));
                g.FillRectangle(fill, rect);
                g.DrawRectangle(pen, rect);
                g.DrawLine(medianPen, cx - bw / 2, yMed, cx + bw / 2, yMed);         // 중앙값
                int yMean = YToPx(b.Mean, plot);                                     // 평균 마커(◇)
                g.DrawPolygon(pen, new[]
                {
                    new PointF(cx, yMean - 4), new PointF(cx + 4, yMean),
                    new PointF(cx, yMean + 4), new PointF(cx - 4, yMean),
                });
                foreach (double o in b.Outliers)
                {
                    int yo = YToPx(o, plot);
                    if (yo >= plot.Top && yo <= plot.Bottom)
                        g.FillEllipse(outlierBrush, cx - 2.2f, yo - 2.2f, 4.4f, 4.4f);
                }
            }
        }

        private void DrawHeatCells(Graphics g, Font font, Rectangle plot, PlotModel m, PlotSeries s)
        {
            if (s.Cells is not { } cells) return;
            int rows = cells.GetLength(0), cols = cells.GetLength(1);
            float cw = plot.Width / (float)cols, ch = plot.Height / (float)rows;
            using var border = new Pen(_palette.Surface);
            bool showText = cw > 34 && ch > 16;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var rect = new RectangleF(plot.Left + cw * c, plot.Top + ch * r, cw, ch);
                    double v = cells[r, c];
                    if (double.IsNaN(v))
                    {
                        using var hatch = new SolidBrush(Color.FromArgb(24, _palette.Text));
                        g.FillRectangle(hatch, rect);
                    }
                    else
                    {
                        bool significant = s.CellsP is not { } ps || double.IsNaN(ps[r, c]) || ps[r, c] < 0.05 || r == c;
                        using var cellBrush = new SolidBrush(HeatColor(v, significant));
                        g.FillRectangle(cellBrush, rect);
                        if (showText)
                            TextRenderer.DrawText(g, v.ToString("0.00"), font, Rectangle.Round(rect),
                                Math.Abs(v) > 0.55 ? Color.White : _palette.Text,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    g.DrawRectangle(border, rect.X, rect.Y, rect.Width, rect.Height);
                }
            }
        }

        // r∈[-1,1] → 파랑(음) ~ 회백(0) ~ 주황(양). 비유의 셀은 채도 감쇠.
        private Color HeatColor(double r, bool significant)
        {
            r = Math.Max(-1, Math.Min(1, r));
            Color pos = ChartCommon.SeriesColor(2), neg = ChartCommon.SeriesColor(0);
            Color baseColor = r >= 0 ? pos : neg;
            double t = Math.Abs(r);
            int cr = (int)(_palette.Surface.R + (baseColor.R - _palette.Surface.R) * t);
            int cg = (int)(_palette.Surface.G + (baseColor.G - _palette.Surface.G) * t);
            int cb = (int)(_palette.Surface.B + (baseColor.B - _palette.Surface.B) * t);
            var c = Color.FromArgb(cr, cg, cb);
            if (!significant)
            {
                int gray = (c.R + c.G + c.B) / 3;
                c = Color.FromArgb((c.R + 2 * gray) / 3, (c.G + 2 * gray) / 3, (c.B + 2 * gray) / 3);
            }
            return c;
        }

        private void DrawDensity(Graphics g, Rectangle plot, PlotSeries s)
        {
            // 뷰포트 기준 재비닝(줌 시 자동 갱신). 가시 점이 임계 이하면 점 렌더로 복귀.
            var key = (_vxMin, _vxMax, _vyMin, _vyMax);
            if (_densityCache is null || _densityCacheKey != key)
            {
                int cols = Math.Max(32, Math.Min(320, plot.Width / 3));
                int rows = Math.Max(24, Math.Min(240, plot.Height / 3));
                _densityCache = PlotMath.ComputeDensityGrid(s.Xs, s.Ys, cols, rows, _vxMin, _vxMax, _vyMin, _vyMax);
                _densityCacheKey = key;
                _visiblePointCache = _densityCache?.Total ?? 0;
            }
            var grid = _densityCache;
            if (grid is null) return;

            if (grid.Total <= s.DensityThreshold)
            {
                DrawPoints(g, plot, s); // 줌인으로 점이 줄면 산점 복귀
                return;
            }

            int gr = grid.Counts.GetLength(0), gc = grid.Counts.GetLength(1);
            float cw = plot.Width / (float)gc, ch = plot.Height / (float)gr;
            var col = ChartCommon.SeriesColor(s.PaletteIndex);
            double logMax = Math.Log(1 + grid.MaxCount);
            for (int r = 0; r < gr; r++)
            {
                for (int c = 0; c < gc; c++)
                {
                    int count = grid.Counts[r, c];
                    if (count == 0) continue;
                    int alpha = (int)(30 + 225 * Math.Log(1 + count) / logMax); // 로그 명도(설계 합의)
                    using var b = new SolidBrush(Color.FromArgb(Math.Min(255, alpha), col));
                    // 격자 row 0 = yMin(아래) → 화면은 아래에서 위로
                    float y = plot.Bottom - ch * (r + 1);
                    g.FillRectangle(b, plot.Left + cw * c, y, cw + 0.5f, ch + 0.5f);
                }
            }
        }

        // ---------------------------------------------------------------- 툴팁

        private void DrawTooltip(Graphics g, Font font, Rectangle plot, PlotModel m)
        {
            if (!plot.Contains(_mouse)) return;
            var lines = BuildTooltip(plot, m);
            if (lines.Count == 0) return;

            int w = 0, h = 6;
            foreach (var l in lines) { Size ts = TextRenderer.MeasureText(g, l, font); w = Math.Max(w, ts.Width); h += ts.Height; }
            w += 12;
            int tx = Math.Min(_mouse.X + 14, Width - w - 4);
            int ty = Math.Min(_mouse.Y + 12, Height - h - 4);
            var box = new Rectangle(tx, ty, w, h);
            using (var bg = new SolidBrush(_palette.Window))
            using (var br = new Pen(_palette.Border))
            {
                g.FillRectangle(bg, box);
                g.DrawRectangle(br, box);
            }
            int yy = ty + 3;
            foreach (var l in lines)
            {
                TextRenderer.DrawText(g, l, font, new Point(tx + 6, yy), _palette.Text);
                yy += TextRenderer.MeasureText(g, l, font).Height;
            }
        }

        private List<string> BuildTooltip(Rectangle plot, PlotModel m)
        {
            var lines = new List<string>();
            foreach (var s in m.Series)
            {
                switch (s.Kind)
                {
                    case PlotSeriesKind.Bars:
                        for (int i = 0; i < s.BarHeight.Length; i++)
                        {
                            int x1 = XToPx(s.BarLeft[i], plot), x2 = XToPx(s.BarRight[i], plot);
                            if (_mouse.X >= x1 && _mouse.X < x2)
                            {
                                lines.Add($"[{ChartCommon.FormatTick(s.BarLeft[i])} – {ChartCommon.FormatTick(s.BarRight[i])})");
                                lines.Add($"count: {s.BarHeight[i]:N0}");
                                return lines;
                            }
                        }
                        break;

                    case PlotSeriesKind.CategoryBars:
                    {
                        int n = Math.Max(1, m.XAxis.Categories.Count);
                        int i = (int)((_mouse.X - plot.Left) / (plot.Width / (float)n));
                        if (i >= 0 && i < s.Ys.Length)
                        {
                            lines.Add(m.XAxis.Categories[i]);
                            lines.Add($"count: {s.Ys[i]:N0}");
                            var cum = m.Series.FirstOrDefault(x => x.UseSecondaryAxis);
                            if (cum is not null && i < cum.Ys.Length) lines.Add($"cum: {cum.Ys[i]:0.0}%");
                            return lines;
                        }
                        break;
                    }

                    case PlotSeriesKind.Boxes:
                    {
                        int n = Math.Max(1, m.XAxis.Categories.Count);
                        int i = (int)((_mouse.X - plot.Left) / (plot.Width / (float)n));
                        if (i >= 0 && i < s.Boxes.Count)
                        {
                            var b = s.Boxes[i];
                            lines.Add($"{b.Label}  (n={b.Count:N0})");
                            lines.Add($"median {ChartCommon.FormatTick(b.Median)} · mean {ChartCommon.FormatTick(b.Mean)}");
                            lines.Add($"Q1 {ChartCommon.FormatTick(b.Q1)} · Q3 {ChartCommon.FormatTick(b.Q3)}");
                            lines.Add($"whiskers [{ChartCommon.FormatTick(b.WhiskerLow)}, {ChartCommon.FormatTick(b.WhiskerHigh)}]");
                            if (b.Outliers.Count > 0) lines.Add($"outliers: {b.Outliers.Count:N0}");
                            return lines;
                        }
                        break;
                    }

                    case PlotSeriesKind.HeatCells when s.Cells is { } cells:
                    {
                        int rows = cells.GetLength(0), cols = cells.GetLength(1);
                        int c = (int)((_mouse.X - plot.Left) / (plot.Width / (float)cols));
                        int r = (int)((_mouse.Y - plot.Top) / (plot.Height / (float)rows));
                        if (r >= 0 && r < rows && c >= 0 && c < cols)
                        {
                            lines.Add($"{m.YAxis.Categories[r]} × {m.XAxis.Categories[c]}");
                            double v = cells[r, c];
                            lines.Add(double.IsNaN(v) ? "r: n/a" : $"r = {v:0.000}");
                            if (s.CellsP is { } ps && !double.IsNaN(ps[r, c])) lines.Add($"p = {ps[r, c]:0.0000}");
                            if (r != c) lines.Add("클릭 → 산점도");
                            return lines;
                        }
                        break;
                    }

                    case PlotSeriesKind.Points:
                    {
                        // 가장 가까운 점(12px 이내)
                        double best = 12 * 12;
                        int bi = -1;
                        for (int i = 0; i < s.Xs.Length; i++)
                        {
                            double dx = XToPx(s.Xs[i], plot) - _mouse.X;
                            double dy = YToPx(s.Ys[i], plot) - _mouse.Y;
                            double dd = dx * dx + dy * dy;
                            if (dd < best) { best = dd; bi = i; }
                        }
                        if (bi >= 0)
                        {
                            lines.Add($"({ChartCommon.FormatTick(s.Xs[bi])}, {ChartCommon.FormatTick(s.Ys[bi])})");
                            return lines;
                        }
                        break;
                    }

                    case PlotSeriesKind.Density:
                    {
                        if (_densityCache is { } grid && grid.Total > s.DensityThreshold)
                        {
                            int gr = grid.Counts.GetLength(0), gc = grid.Counts.GetLength(1);
                            int c = (int)((_mouse.X - plot.Left) / (plot.Width / (float)gc));
                            int r = (int)((plot.Bottom - _mouse.Y) / (plot.Height / (float)gr));
                            if (r >= 0 && r < gr && c >= 0 && c < gc && grid.Counts[r, c] > 0)
                            {
                                lines.Add($"x ≈ {ChartCommon.FormatTick(PxToX(_mouse.X, plot))}");
                                lines.Add($"y ≈ {ChartCommon.FormatTick(PxToY(_mouse.Y, plot))}");
                                lines.Add($"points: {grid.Counts[r, c]:N0}");
                                return lines;
                            }
                        }
                        break;
                    }

                    case PlotSeriesKind.Line when m.XAxis.TickLabels is { } labels:
                    {
                        int idx = (int)Math.Round(PxToX(_mouse.X, plot));
                        if (idx >= 0 && idx < labels.Count)
                        {
                            lines.Add(labels[idx]);
                            foreach (var ls in m.Series)
                            {
                                int j = Array.IndexOf(ls.Xs, idx);
                                if (j >= 0) lines.Add($"{ls.Name}: {ChartCommon.FormatTick(ls.Ys[j])}");
                            }
                            return lines;
                        }
                        break;
                    }
                }
            }
            return lines;
        }

        // ---------------------------------------------------------------- 상호작용

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _mouse = e.Location;
            if (_dragStart is { } start)
                _dragRect = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y),
                                               Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _mouse = new Point(-1, -1);
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _model is { AllowZoom: true })
            {
                _dragStart = e.Location;
                _dragRect = Rectangle.Empty;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var m = _model;
            if (m is null) { base.OnMouseUp(e); return; }
            var plot = PlotArea();

            if (_dragStart is not null)
            {
                var rect = _dragRect;
                _dragStart = null;
                if (rect.Width > 8 && rect.Height > 8 && m.AllowZoom)
                {
                    double x1 = PxToX(rect.Left, plot), x2 = PxToX(rect.Right, plot);
                    double y1 = PxToY(rect.Bottom, plot), y2 = PxToY(rect.Top, plot);
                    if (x2 > x1 && y2 > y1)
                    {
                        _vxMin = x1; _vxMax = x2; _vyMin = y1; _vyMax = y2;
                        _zoomed = true;
                        _densityCache = null;
                    }
                    Invalidate();
                    base.OnMouseUp(e);
                    return;
                }
            }

            // 히트맵 셀 클릭 → 드릴다운
            if (e.Button == MouseButtons.Left &&
                m.Series.FirstOrDefault(s => s.Kind == PlotSeriesKind.HeatCells) is { Cells: { } cells } &&
                plot.Contains(e.Location))
            {
                int rows = cells.GetLength(0), cols = cells.GetLength(1);
                int c = (int)((e.X - plot.Left) / (plot.Width / (float)cols));
                int r = (int)((e.Y - plot.Top) / (plot.Height / (float)rows));
                if (r >= 0 && r < rows && c >= 0 && c < cols && r != c)
                    HeatCellClicked?.Invoke(r, c);
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_model is { AllowZoom: true }) ResetView();
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_model is { AllowZoom: true })
            {
                var plot = PlotArea();
                if (plot.Contains(e.Location))
                {
                    double factor = e.Delta > 0 ? 0.85 : 1.18;
                    double cx = PxToX(e.X, plot), cy = PxToY(e.Y, plot);
                    _vxMin = cx - (cx - _vxMin) * factor;
                    _vxMax = cx + (_vxMax - cx) * factor;
                    _vyMin = cy - (cy - _vyMin) * factor;
                    _vyMax = cy + (_vyMax - cy) * factor;
                    _zoomed = true;
                    _densityCache = null;
                    Invalidate();
                }
            }
            base.OnMouseWheel(e);
        }
    }
}
