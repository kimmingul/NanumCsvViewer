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
                Text = Loc.T("Usage_Text").Replace("{SHORTCUTS}", ShortcutList()).Replace("\n", "\r\n"),
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

        // 단축키 표(CommandShortcuts)에서 만든 목록 — 메뉴와 설정의 "단축키" 쪽과 같은 단일 소스. 키가 같은 명령은 한 줄에 묶지 않는다(한 줄 = 한 명령).
        private static string ShortcutList()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in CommandShortcuts.All.Where(x => x.Keys != Keys.None))
            {
                string keys = e.KeyText;
                if (e.Alternates is { Length: > 0 } alt) keys += " / " + string.Join(" / ", alt.Select(CommandShortcuts.Display));
                sb.Append("  ").Append(keys).Append("  —  ").Append(e.Name).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}
