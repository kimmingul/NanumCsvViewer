using System.Drawing;
using System.Windows.Forms;

namespace NanumCsvViewer
{
    /// <summary>Help ▸ How to Use 다이얼로그. 본문은 리소스(Usage_Text)에서, 색은 현재 테마에서.</summary>
    public sealed class UsageForm : Form
    {
        public UsageForm(ThemePalette palette)
        {
            Text = Loc.T("Usage_Title");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(620, 560);
            MinimumSize = new Size(420, 360);
            BackColor = palette.Window;
            ForeColor = palette.Text;

            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                Font = new Font("Segoe UI", 9.5f),
                Text = Loc.T("Usage_Text").Replace("\n", "\r\n"),
            };
            text.Select(0, 0);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = palette.Window };
            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Right,
                Size = new Size(88, 28),
            };
            ok.Location = new Point(bottom.Width - ok.Width - 12, 8);
            bottom.Controls.Add(ok);

            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0), BackColor = palette.Window };
            pad.Controls.Add(text);

            Controls.Add(pad);
            Controls.Add(bottom);
            AcceptButton = ok;
            CancelButton = ok;
        }
    }
}
