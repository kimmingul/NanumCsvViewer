using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NanumCsvViewer
{
    /// <summary>
    /// 툴바·상태바 아이콘을 Windows 아이콘 글꼴(Segoe Fluent Icons → 없으면 Segoe MDL2 Assets)의 글리프로 런타임에 그린다.
    /// 장치 픽셀 크기(DPI)와 현재 테마의 글자색으로 비트맵을 만들므로 어떤 배율·테마에서도 같은 선명도·대비를 가진다.
    /// 테마·DPI가 바뀌면 <see cref="Refresh"/>가 등록된 항목의 비트맵을 다시 그린다.
    /// </summary>
    internal static class IconGlyphs
    {
        // Segoe MDL2 Assets 코드 포인트(Segoe Fluent Icons도 같은 값을 유지한다).
        public const string Open = "\uE838";
        public const string Save = "\uE74E";
        public const string Undo = "\uE7A7";
        public const string Redo = "\uE7A6";
        public const string Find = "\uE721";
        public const string FindNext = "\uE72A";
        public const string Filter = "\uE71C";
        public const string Clear = "\uE894";
        public const string SortAsc = "\uE74A";
        public const string SortDesc = "\uE74B";
        public const string Edit = "\uE70F";
        public const string Sheet = "\uE80A";
        public const string Palette = "\uE790";
        public const string Explorer = "\uE8B7";
        public const string Detail = "\uE8A0";
        public const string Chat = "\uE8BD";
        public const string Settings = "\uE713";
        public const string Globe = "\uE774";

        /// <summary>글꼴 이름. 둘 다 없으면 null(아이콘 없이 글자 버튼으로 대체).</summary>
        public static string? FontName => _fontName.Value;

        private static readonly Lazy<string?> _fontName = new(() =>
        {
            try
            {
                using var fonts = new InstalledFontCollection();
                var names = new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                if (names.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
                if (names.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[IconGlyphs] {ex.Message}"); }
            return null;
        });

        /// <summary>논리 16px 아이콘이 dpi에서 차지하는 장치 픽셀 수.</summary>
        public static int PixelSize(int dpi, int logical = 16) => Math.Max(logical, (int)Math.Round(logical * dpi / 96.0));

        /// <summary>글리프 하나를 정사각 비트맵으로 그린다. 글꼴이 없으면 null.</summary>
        public static Bitmap? Render(string glyph, Color color, int dpi, int logical = 16)
        {
            string? family = FontName;
            if (family is null) return null;
            int px = PixelSize(dpi, logical);
            var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // 글리프 사각형은 em 박스 안에서 약간 작다: 폰트 크기를 픽셀의 ~88%로 두고 가운데 정렬.
            using var font = new Font(family, px * 0.88f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            using var fmt = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };
            g.DrawString(glyph, font, brush, new RectangleF(0, 0, px, px), fmt);
            return bmp;
        }

        // ---- 등록된 항목의 다시 그리기 ---------------------------------------------------------------------------

        private sealed record Registration(WeakReference<ToolStripItem> Item, string Glyph);

        private static readonly List<Registration> Registered = new();

        /// <summary>항목에 글리프 이미지를 달고 등록한다(다음 <see cref="Refresh"/>에서 다시 그려진다).</summary>
        public static void Attach(ToolStripItem item, string glyph, Color color, int dpi)
        {
            lock (Registered)
            {
                Registered.RemoveAll(r => !r.Item.TryGetTarget(out var t) || ReferenceEquals(t, item));
                Registered.Add(new Registration(new WeakReference<ToolStripItem>(item), glyph));
            }
            Apply(item, glyph, color, dpi);
        }

        /// <summary>팔레트 글자색·DPI로 등록된 모든 항목의 이미지를 새로 그린다.</summary>
        public static void Refresh(Color color, int dpi)
        {
            Registration[] snapshot;
            lock (Registered) snapshot = Registered.ToArray();
            foreach (var r in snapshot)
                if (r.Item.TryGetTarget(out var item) && !item.IsDisposed)
                    Apply(item, r.Glyph, color, dpi);
        }

        private static void Apply(ToolStripItem item, string glyph, Color color, int dpi)
        {
            var bmp = Render(glyph, color, dpi); // 비활성 회색은 렌더러가 입힌다
            var old = item.Image;
            item.ImageScaling = ToolStripItemImageScaling.None;
            item.Image = bmp;
            if (bmp is null && item.DisplayStyle == ToolStripItemDisplayStyle.Image)
                item.DisplayStyle = ToolStripItemDisplayStyle.Text; // 글꼴이 없으면 글자 버튼으로 대체
            old?.Dispose();
        }
    }
}
