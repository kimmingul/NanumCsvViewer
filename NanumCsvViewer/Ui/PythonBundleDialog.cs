namespace NanumCsvViewer
{
    /// <summary>
    /// Python 분석 재현 패키지 내보내기 대화상자: 내보낼 곳(폴더 또는 zip)과 "데이터 파일 포함"(기본 꺼짐, 켜면 개인정보 경고)을 받는다.
    /// 값만 돌려주며 파일은 쓰지 않는다(호출자가 <see cref="Agent.Python.AnalysisBundle.Export"/>를 부른다).
    /// </summary>
    internal sealed class PythonBundleDialog : Form
    {
        private readonly RadioButton _folder, _zip;
        private readonly TextBox _path;
        private readonly CheckBox _includeData;
        private readonly Label _warning;
        private readonly string _analysisFolder;
        private readonly string _baseName;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        public string Destination => _path.Text.Trim();
        public bool IncludeData => _includeData.Checked;

        public PythonBundleDialog(ThemePalette palette, string analysisFolder, string baseName)
        {
            _analysisFolder = analysisFolder;
            _baseName = baseName;
            Text = LT("Export Python Analysis Bundle", "Python 분석 재현 패키지 내보내기");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(600, 330);
            Padding = new Padding(14);

            var info = new Label
            {
                AutoSize = true, MaximumSize = new Size(570, 0), Dock = DockStyle.Top, ForeColor = palette.Text, Margin = new Padding(0, 0, 0, 8),
                Text = LT("Copies the analysis folder's scripts (*.py), reports (*.md), figures and the environment's requirements.lock, plus a README that explains how to recreate the environment and rerun, to a folder or a zip file.\nAnalysis folder: ",
                          "분석 폴더의 스크립트(*.py)·보고서(*.md)·그림과 환경의 requirements.lock, 그리고 환경을 다시 만들고 실행하는 방법을 적은 README를 폴더 또는 zip으로 복사합니다.\n분석 폴더: ") + analysisFolder,
            };

            _folder = new RadioButton { AutoSize = true, Checked = true, Text = LT("Folder", "폴더"), ForeColor = palette.Text, Margin = new Padding(0, 6, 16, 4) };
            _zip = new RadioButton { AutoSize = true, Text = LT("Zip file", "zip 파일"), ForeColor = palette.Text, Margin = new Padding(0, 6, 0, 4) };
            var kind = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, BackColor = palette.Window };
            kind.Controls.Add(_folder);
            kind.Controls.Add(_zip);

            _path = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = palette.Surface, ForeColor = palette.Text, BorderStyle = BorderStyle.FixedSingle };
            var browse = new Button { Text = LT("Browse…", "찾아보기…"), AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = palette.Surface, ForeColor = palette.Text, UseVisualStyleBackColor = false, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 0, 0), MinimumSize = new Size(88, 26) };
            browse.FlatAppearance.BorderColor = palette.Border;
            browse.Click += (_, _) => Browse();
            var pathRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, BackColor = palette.Window, Margin = new Padding(0, 4, 0, 4) };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pathRow.Controls.Add(_path, 0, 0);
            pathRow.Controls.Add(browse, 1, 0);

            _includeData = new CheckBox
            {
                AutoSize = true, MaximumSize = new Size(570, 0), ForeColor = palette.Text, Margin = new Padding(0, 10, 0, 2), Dock = DockStyle.Top,
                Text = LT("Include data files (data\\ folder and table files)", "데이터 파일 포함 (data\\ 폴더와 표 파일)"),
            };
            _warning = new Label
            {
                AutoSize = true, MaximumSize = new Size(570, 0), Dock = DockStyle.Top, ForeColor = Color.FromArgb(200, 60, 40), Visible = false,
                Text = LT("Privacy warning: the data files may contain personal or patient information. Anyone who receives the bundle can read them. Leave this off unless the recipient is allowed to see the data.",
                          "개인정보 경고: 데이터 파일에는 개인·환자 정보가 있을 수 있고, 패키지를 받는 사람은 누구나 읽을 수 있습니다. 받는 사람이 데이터를 봐도 되는 경우가 아니면 끄세요."),
            };
            _includeData.CheckedChanged += (_, _) => _warning.Visible = _includeData.Checked;
            _folder.CheckedChanged += (_, _) => SwapExtension();

            var ok = new Button { Text = LT("Export", "내보내기"), DialogResult = DialogResult.OK, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = palette.Surface, ForeColor = palette.Text, UseVisualStyleBackColor = false, MinimumSize = new Size(96, 28) };
            var cancel = new Button { Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = palette.Surface, ForeColor = palette.Text, UseVisualStyleBackColor = false, MinimumSize = new Size(96, 28) };
            ok.FlatAppearance.BorderColor = palette.Border;
            cancel.FlatAppearance.BorderColor = palette.Border;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, BackColor = palette.Window };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            // 위에서 아래 순서: Dock=Top은 나중에 추가한 것이 위로 올라오므로 거꾸로 넣는다.
            Controls.Add(buttons);
            Controls.Add(_warning);
            Controls.Add(_includeData);
            Controls.Add(pathRow);
            Controls.Add(kind);
            Controls.Add(info);

            string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(analysisFolder)) ?? analysisFolder;
            _path.Text = Path.Combine(parent, baseName);
            FormClosing += OnClosing;
        }

        private void SwapExtension()
        {
            string p = _path.Text.Trim();
            if (_zip.Checked && !p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) _path.Text = p + ".zip";
            else if (_folder.Checked && p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) _path.Text = p[..^4];
        }

        private void Browse()
        {
            if (_zip.Checked)
            {
                using var dlg = new SaveFileDialog { Filter = "ZIP (*.zip)|*.zip", DefaultExt = "zip", FileName = Path.GetFileName(_path.Text.Trim()), InitialDirectory = SafeDir(_path.Text) };
                if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
            }
            else
            {
                using var dlg = new FolderBrowserDialog { Description = LT("Choose the parent folder; the bundle is written to a sub-folder named after it.", "상위 폴더를 고르세요. 패키지는 그 안에 아래 이름의 하위 폴더로 쓰입니다."), SelectedPath = SafeDir(_path.Text) };
                if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = Path.Combine(dlg.SelectedPath, _baseName);
            }
        }

        private static string SafeDir(string path)
        {
            try { string? d = Path.GetDirectoryName(path.Trim()); return d != null && Directory.Exists(d) ? d : ""; }
            catch (ArgumentException) { return ""; }
        }

        private void OnClosing(object? sender, FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK) return;
            string dest = Destination;
            string? why = null;
            if (dest.Length == 0) why = LT("Choose where to export.", "내보낼 위치를 정하세요.");
            else if (!Path.IsPathRooted(dest)) why = LT("Enter a full path.", "전체 경로를 입력하세요.");
            else if (IsInside(dest, _analysisFolder)) why = LT("The destination must be outside the analysis folder.", "내보낼 위치는 분석 폴더 밖이어야 합니다.");
            else if (!_zip.Checked && Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            {
                if (MessageBox.Show(this, LT("The folder is not empty. Same-named files will be replaced. Continue?", "폴더가 비어 있지 않습니다. 같은 이름의 파일은 바뀝니다. 계속할까요?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
            }
            else if (_zip.Checked && !dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) _path.Text = dest + ".zip";
            if (_includeData.Checked && why == null && !e.Cancel &&
                MessageBox.Show(this, _warning.Text + "\n\n" + LT("Include the data files anyway?", "그래도 데이터 파일을 포함할까요?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                e.Cancel = true;
            if (why != null)
            {
                MessageBox.Show(this, why, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
            }
        }

        internal static bool IsInside(string path, string folder)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
                string f = Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\";
                return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }
    }
}
