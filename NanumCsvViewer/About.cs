using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NanumCsvViewer
{
    public partial class About : Form
    {
        public About()
        {
            InitializeComponent();

            // 버전·릴리즈 날짜는 어셈블리 메타데이터(AppInfo)에서 동적으로 채운다.
            label5.Text = $"Nanum CSV Viewer (64-bit)  ·  v{AppInfo.Version}";
            label6.Text = "This program is available for free use by anyone.";
            label7.Text = string.IsNullOrWhiteSpace(AppInfo.ReleaseDate)
                ? "" : $"Released  {AppInfo.ReleaseDate}";
        }

        private void button1_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
