using System.Drawing;
using System.Windows.Forms;

namespace NanumCsvViewer
{
    /// <summary>
    /// 한 컬럼의 분포(패싯)를 가로 막대로 그리는 컴팩트 컨트롤.
    /// 각 행은 (라벨 · 막대 · 개수)이며 클릭하면 해당 행의 필터 액션을 실행한다.
    /// </summary>
    internal sealed class FacetView : Panel
    {
        // 논리 픽셀(96 DPI 기준). 현재 모니터 DPI로 바꾼 값은 아래 속성을 쓴다.
        public const int WidthLogical = 214;
        private int TitleH => S(19);
        private int RowH => S(17);
        private int LabelW => S(86);
        private int CountW => S(36);
        private int BottomPad => S(6);
        private int S(int logical) => LogicalToDeviceUnits(logical);

        private readonly string _title;
        private readonly ThemePalette _palette;
        private readonly (string Label, int Count, Action OnClick)[] _rows;
        private readonly int _maxCount;

        public FacetView(string title, ThemePalette palette, IReadOnlyList<(string Label, int Count, Action OnClick)> rows)
        {
            _title = title;
            _palette = palette;
            _rows = rows.ToArray();
            _maxCount = _rows.Length == 0 ? 1 : Math.Max(1, _rows.Max(r => r.Count));

            ApplySize();
            Margin = new Padding(LogicalToDeviceUnits(4), LogicalToDeviceUnits(3), LogicalToDeviceUnits(4), LogicalToDeviceUnits(1));
            BackColor = _palette.Surface;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using var titleFont = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(g, _title, titleFont, new Rectangle(S(2), S(1), Width - S(4), TitleH - S(2)), _palette.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int barX = LabelW + S(2);
            int barMaxW = Math.Max(S(8), Width - LabelW - CountW - S(8));
            using var barBrush = new SolidBrush(Color.FromArgb(90, _palette.Accent));

            for (int i = 0; i < _rows.Length; i++)
            {
                int y = TitleH + i * RowH;
                var (label, count, _) = _rows[i];
                TextRenderer.DrawText(g, label, Font, new Rectangle(S(2), y, LabelW - S(4), RowH), _palette.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int w = (int)(barMaxW * (count / (double)_maxCount));
                g.FillRectangle(barBrush, barX, y + S(2), Math.Max(1, w), RowH - S(5));
                TextRenderer.DrawText(g, count.ToString("N0"), Font, new Rectangle(Width - CountW - S(2), y, CountW, RowH), _palette.Text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Y < TitleH) { base.OnMouseClick(e); return; }
            int i = (e.Y - TitleH) / RowH;
            if (i >= 0 && i < _rows.Length) _rows[i].OnClick();
            base.OnMouseClick(e);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplySize();
            Invalidate();
        }

        private void ApplySize()
        {
            Width = LogicalToDeviceUnits(WidthLogical);
            Height = TitleH + _rows.Length * RowH + BottomPad;
        }
    }
}
