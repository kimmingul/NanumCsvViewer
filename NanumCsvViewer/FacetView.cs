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
        private const int TitleH = 19, RowH = 17, LabelW = 86, CountW = 36, BottomPad = 6;

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

            Width = 214;
            Height = TitleH + _rows.Length * RowH + BottomPad;
            Margin = new Padding(4, 3, 4, 1);
            BackColor = _palette.Surface;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using var titleFont = new Font(Font, FontStyle.Bold);
            TextRenderer.DrawText(g, _title, titleFont, new Rectangle(2, 1, Width - 4, TitleH - 2), _palette.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int barX = LabelW + 2;
            int barMaxW = Math.Max(8, Width - LabelW - CountW - 8);
            using var barBrush = new SolidBrush(Color.FromArgb(90, _palette.Accent));

            for (int i = 0; i < _rows.Length; i++)
            {
                int y = TitleH + i * RowH;
                var (label, count, _) = _rows[i];
                TextRenderer.DrawText(g, label, Font, new Rectangle(2, y, LabelW - 4, RowH), _palette.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int w = (int)(barMaxW * (count / (double)_maxCount));
                g.FillRectangle(barBrush, barX, y + 2, Math.Max(1, w), RowH - 5);
                TextRenderer.DrawText(g, count.ToString("N0"), Font, new Rectangle(Width - CountW - 2, y, CountW, RowH), _palette.Text,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = (e.Y - TitleH) / RowH;
            if (i >= 0 && i < _rows.Length) _rows[i].OnClick();
            base.OnMouseClick(e);
        }
    }
}
