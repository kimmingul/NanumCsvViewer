using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NanumCsvViewer
{
    public enum AppTheme { Light, Dark }

    /// <summary>라이트/다크 색 팔레트.</summary>
    public sealed class ThemePalette
    {
        public Color Window, Surface, Text, Border, GridBg, HeaderBg, HeaderText, AltRow,
                     SelectionBg, SelectionText, Accent, ToolStrip, MenuHighlight;

        public static readonly ThemePalette Light = new()
        {
            Window = SystemColors.Control,
            Surface = Color.White,
            Text = Color.FromArgb(30, 30, 30),
            Border = Color.FromArgb(200, 200, 200),
            GridBg = Color.White,
            HeaderBg = Color.FromArgb(240, 240, 240),
            HeaderText = Color.FromArgb(30, 30, 30),
            AltRow = Color.FromArgb(247, 247, 247),
            SelectionBg = Color.FromArgb(0, 120, 215),
            SelectionText = Color.White,
            Accent = Color.FromArgb(0, 90, 158),
            ToolStrip = SystemColors.Control,
            MenuHighlight = Color.FromArgb(204, 228, 247),
        };

        public static readonly ThemePalette Dark = new()
        {
            Window = Color.FromArgb(32, 32, 32),
            Surface = Color.FromArgb(45, 45, 48),
            Text = Color.FromArgb(230, 230, 230),
            Border = Color.FromArgb(64, 64, 64),
            GridBg = Color.FromArgb(37, 37, 38),
            HeaderBg = Color.FromArgb(50, 50, 52),
            HeaderText = Color.FromArgb(230, 230, 230),
            AltRow = Color.FromArgb(43, 43, 45),
            SelectionBg = Color.FromArgb(0, 90, 158),
            SelectionText = Color.White,
            Accent = Color.FromArgb(86, 156, 230),
            ToolStrip = Color.FromArgb(45, 45, 48),
            MenuHighlight = Color.FromArgb(62, 62, 66),
        };

        public static ThemePalette For(AppTheme t) => t == AppTheme.Dark ? Dark : Light;
    }

    public static class ThemeManager
    {
        /// <summary>Windows 시스템 테마(밝게/어둡게) 감지. 실패 시 Light.</summary>
        public static AppTheme DetectSystem()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (k?.GetValue("AppsUseLightTheme") is int v) return v == 0 ? AppTheme.Dark : AppTheme.Light;
            }
            catch { /* 키 없음/접근 불가 */ }
            return AppTheme.Light;
        }

        /// <summary>폼 전체에 테마 적용. 반환값은 적용된 팔레트(커스텀 페인팅에서 색을 참조).</summary>
        public static ThemePalette Apply(Form form, AppTheme theme)
        {
            var p = ThemePalette.For(theme);
            ToolStripManager.Renderer = new ThemeToolStripRenderer(p); // 모든 툴바/메뉴/컨텍스트/드롭다운 공통
            form.BackColor = p.Window;
            form.ForeColor = p.Text;
            ApplyToControls(form, p);
            form.Invalidate(true);
            return p;
        }

        private static void ApplyToControls(Control parent, ThemePalette p)
        {
            foreach (Control c in parent.Controls)
            {
                switch (c)
                {
                    case DataGridView g: StyleGrid(g, p); break;
                    case ToolStrip ts: ts.BackColor = p.ToolStrip; ts.ForeColor = p.Text; break;
                    case TextBox tb: tb.BackColor = p.Surface; tb.ForeColor = p.Text; tb.BorderStyle = BorderStyle.FixedSingle; break;
                    case RichTextBox rtb: rtb.BackColor = p.Surface; rtb.ForeColor = p.Text; break;
                    case Label lb: lb.BackColor = p.Window; lb.ForeColor = p.Text; break;
                    case SplitContainer sc:
                        sc.BackColor = p.Border; // 스플리터 띠
                        sc.Panel1.BackColor = p.Window;
                        sc.Panel2.BackColor = p.Window;
                        break;
                    default:
                        c.BackColor = p.Window; c.ForeColor = p.Text; break;
                }
                if (c.HasChildren) ApplyToControls(c, p);
            }
        }

        private static void StyleGrid(DataGridView g, ThemePalette p)
        {
            g.EnableHeadersVisualStyles = false; // 헤더 색을 우리가 직접 지정
            g.BackgroundColor = p.GridBg;
            g.GridColor = p.Border;
            g.DefaultCellStyle.BackColor = p.GridBg;
            g.DefaultCellStyle.ForeColor = p.Text;
            g.DefaultCellStyle.SelectionBackColor = p.SelectionBg;
            g.DefaultCellStyle.SelectionForeColor = p.SelectionText;
            g.AlternatingRowsDefaultCellStyle.BackColor = p.AltRow;
            g.AlternatingRowsDefaultCellStyle.ForeColor = p.Text;
            g.AlternatingRowsDefaultCellStyle.SelectionBackColor = p.SelectionBg;
            g.AlternatingRowsDefaultCellStyle.SelectionForeColor = p.SelectionText;
            g.ColumnHeadersDefaultCellStyle.BackColor = p.HeaderBg;
            g.ColumnHeadersDefaultCellStyle.ForeColor = p.HeaderText;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.HeaderBg;
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.HeaderText;
            g.RowHeadersDefaultCellStyle.BackColor = p.HeaderBg;
            g.RowHeadersDefaultCellStyle.ForeColor = p.HeaderText;
            g.RowHeadersDefaultCellStyle.SelectionBackColor = p.HeaderBg;
            g.RowHeadersDefaultCellStyle.SelectionForeColor = p.HeaderText;
        }
    }

    /// <summary>팔레트 색으로 툴바/메뉴/상태바/컨텍스트 메뉴를 그리는 렌더러.</summary>
    internal sealed class ThemeToolStripRenderer : ToolStripProfessionalRenderer
    {
        private readonly ThemePalette _p;
        public ThemeToolStripRenderer(ThemePalette p) : base(new ThemeColorTable(p)) { _p = p; RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _p.Text : Color.Gray;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = _p.Text;
            base.OnRenderArrow(e);
        }
    }

    internal sealed class ThemeColorTable : ProfessionalColorTable
    {
        private readonly ThemePalette _p;
        public ThemeColorTable(ThemePalette p) { _p = p; UseSystemColors = false; }

        public override Color ToolStripGradientBegin => _p.ToolStrip;
        public override Color ToolStripGradientMiddle => _p.ToolStrip;
        public override Color ToolStripGradientEnd => _p.ToolStrip;
        public override Color ToolStripContentPanelGradientBegin => _p.ToolStrip;
        public override Color ToolStripContentPanelGradientEnd => _p.ToolStrip;
        public override Color MenuStripGradientBegin => _p.ToolStrip;
        public override Color MenuStripGradientEnd => _p.ToolStrip;
        public override Color StatusStripGradientBegin => _p.ToolStrip;
        public override Color StatusStripGradientEnd => _p.ToolStrip;
        public override Color ToolStripBorder => _p.Border;
        public override Color ToolStripDropDownBackground => _p.Surface;
        public override Color ImageMarginGradientBegin => _p.Surface;
        public override Color ImageMarginGradientMiddle => _p.Surface;
        public override Color ImageMarginGradientEnd => _p.Surface;
        public override Color MenuItemSelected => _p.MenuHighlight;
        public override Color MenuItemSelectedGradientBegin => _p.MenuHighlight;
        public override Color MenuItemSelectedGradientEnd => _p.MenuHighlight;
        public override Color MenuItemPressedGradientBegin => _p.ToolStrip;
        public override Color MenuItemPressedGradientEnd => _p.ToolStrip;
        public override Color MenuItemBorder => _p.Accent;
        public override Color MenuBorder => _p.Border;
        public override Color ButtonSelectedGradientBegin => _p.MenuHighlight;
        public override Color ButtonSelectedGradientMiddle => _p.MenuHighlight;
        public override Color ButtonSelectedGradientEnd => _p.MenuHighlight;
        public override Color ButtonSelectedBorder => _p.Accent;
        public override Color ButtonPressedGradientBegin => _p.Accent;
        public override Color ButtonPressedGradientMiddle => _p.Accent;
        public override Color ButtonPressedGradientEnd => _p.Accent;
        public override Color ButtonCheckedGradientBegin => _p.MenuHighlight;
        public override Color ButtonCheckedGradientMiddle => _p.MenuHighlight;
        public override Color ButtonCheckedGradientEnd => _p.MenuHighlight;
        public override Color CheckBackground => _p.Accent;
        public override Color CheckSelectedBackground => _p.Accent;
        public override Color SeparatorDark => _p.Border;
        public override Color SeparatorLight => _p.Border;
        public override Color GripDark => _p.Border;
        public override Color GripLight => _p.Border;
    }
}
