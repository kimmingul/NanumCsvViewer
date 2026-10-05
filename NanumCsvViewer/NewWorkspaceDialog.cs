using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>파일 ▸ 새 작업 공간…에서 고른 값. 이름은 확장자 없이 보관한다.</summary>
    internal sealed record NewWorkspaceRequest(string Folder, string Name, bool IncludeOpenFiles)
    {
        /// <summary>만들 작업 공간 파일(.ncvws)의 경로.</summary>
        public string FilePath => Path.Combine(Folder, Name + WorkspaceFile.Extension);

        /// <summary>에이전트의 분석 폴더(<c>&lt;위치&gt;\&lt;이름&gt;_분석결과</c>) — 에이전트가 쓰는 규칙 그대로.</summary>
        public string AnalysisFolder => AgentWorkspace.StableOutputFolder(FilePath, null);

        /// <summary>입력한 이름에서 공백과 (붙여 넣었을 수 있는) .ncvws 확장자를 뗀다.</summary>
        public static string NormalizeName(string? name)
        {
            string n = (name ?? "").Trim();
            if (n.EndsWith(WorkspaceFile.Extension, StringComparison.OrdinalIgnoreCase)) n = n[..^WorkspaceFile.Extension.Length].TrimEnd();
            return n;
        }

        /// <summary>위치·이름을 쓸 수 있으면 null, 아니면 이유(현재 언어).</summary>
        public static string? Validate(string? folder, string? name)
        {
            string f = (folder ?? "").Trim();
            string n = NormalizeName(name);
            if (f.Length == 0) return ViewerSupport.LT("Choose a location.", "위치를 고르세요.");
            string fullFolder;
            try { fullFolder = Path.GetFullPath(f); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { return ViewerSupport.LT("The location is not a valid path.", "위치가 올바른 경로가 아닙니다."); }
            if (!Path.IsPathRooted(f) || !Directory.Exists(fullFolder)) return ViewerSupport.LT("The location folder does not exist.", "위치 폴더가 없습니다.");
            if (n.Length == 0) return ViewerSupport.LT("Enter a name.", "이름을 입력하세요.");
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return ViewerSupport.LT("The name cannot contain \\ / : * ? \" < > |", "이름에는 \\ / : * ? \" < > | 문자를 쓸 수 없습니다.");
            if (n.EndsWith('.')) return ViewerSupport.LT("The name cannot end with a period.", "이름은 마침표로 끝날 수 없습니다.");
            string stem = n.Split('.')[0].TrimEnd();
            if (IsReservedDeviceName(stem)) return ViewerSupport.LT("That name is reserved by Windows.", "Windows에서 예약된 이름입니다.");
            var req = new NewWorkspaceRequest(fullFolder, n, false);
            if (req.FilePath.Length >= 240 || req.AnalysisFolder.Length >= 240) return ViewerSupport.LT("The path is too long.", "경로가 너무 깁니다.");
            if (Directory.Exists(req.FilePath)) return ViewerSupport.LT("A folder with that name already exists.", "같은 이름의 폴더가 이미 있습니다.");
            return null;
        }

        private static bool IsReservedDeviceName(string stem)
        {
            string s = stem.ToUpperInvariant();
            if (s is "CON" or "PRN" or "AUX" or "NUL") return true;
            return s.Length == 4 && (s.StartsWith("COM", StringComparison.Ordinal) || s.StartsWith("LPT", StringComparison.Ordinal)) && s[3] is >= '1' and <= '9';
        }
    }

    /// <summary>
    /// 새 작업 공간 만들기 대화 상자: 위치(폴더 선택)·이름·만들어질 .ncvws 경로와 분석 폴더 미리보기·"지금 열린 파일 포함".
    /// 확인은 위치·이름이 올바를 때만 닫힌다(덮어쓰기 확인은 호출한 쪽이 한다).
    /// </summary>
    internal sealed class NewWorkspaceDialog : Form
    {
        private readonly TextBox _folder, _name;
        private readonly Label _filePreview, _analysisPreview, _problem;
        private readonly CheckBox _include;
        private readonly Button _ok;

        private static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        /// <summary>대화 상자가 돌려주는 값(확인을 눌렀을 때만 의미가 있다).</summary>
        public NewWorkspaceRequest Result => new(Path.GetFullPath(_folder.Text.Trim()), NewWorkspaceRequest.NormalizeName(_name.Text), _include.Checked);

        public NewWorkspaceDialog(ThemePalette palette, NewWorkspaceRequest initial, bool openFilesAvailable)
        {
            Text = LT("New Workspace", "새 작업 공간");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            AutoScaleMode = AutoScaleMode.Font;
            Padding = new Padding(12);
            ClientSize = new Size(560, 270);

            Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = palette.Text, Margin = new Padding(0, 6, 8, 6) };
            Label Preview(string name) => new()
            {
                Name = name, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = palette.Text, Margin = new Padding(0, 4, 0, 4),
            };
            TextBox Edit(string name, string text) => new()
            {
                Name = name, Dock = DockStyle.Fill, Text = text, BackColor = palette.Surface, ForeColor = palette.Text, BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 3, 0, 3),
            };
            Button Btn(string text) => new()
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(88, 28), FlatStyle = FlatStyle.System,
                BackColor = palette.Surface, ForeColor = palette.Text, Margin = new Padding(6, 0, 0, 0),
            };

            _folder = Edit("locationText", initial.Folder);
            _name = Edit("nameText", initial.Name);
            _filePreview = Preview("filePreview");
            _analysisPreview = Preview("analysisPreview");
            _problem = new Label { Name = "problemLabel", Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.FromArgb(200, 70, 70), TextAlign = ContentAlignment.MiddleLeft };
            _include = new CheckBox
            {
                Name = "includeOpenFiles", AutoSize = true, ForeColor = palette.Text, Margin = new Padding(0, 8, 0, 0),
                Text = LT("Include currently open files", "지금 열린 파일 포함"),
                Checked = openFilesAvailable && initial.IncludeOpenFiles, Enabled = openFilesAvailable,
            };
            if (!openFilesAvailable)
                new ToolTip().SetToolTip(_include, LT("No file is open right now.", "지금 열린 파일이 없습니다."));

            var browse = Btn(LT("Browse…", "찾아보기…"));
            browse.Name = "browseButton";
            browse.Click += (_, _) => Browse();

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 6 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.Controls.Add(Caption(LT("Location:", "위치:")), 0, 0);
            grid.Controls.Add(_folder, 1, 0);
            grid.Controls.Add(browse, 2, 0);
            grid.Controls.Add(Caption(LT("Name:", "이름:")), 0, 1);
            grid.Controls.Add(_name, 1, 1);
            grid.SetColumnSpan(_name, 2);
            grid.Controls.Add(Caption(LT("Workspace file:", "작업 공간 파일:")), 0, 2);
            grid.Controls.Add(_filePreview, 1, 2);
            grid.SetColumnSpan(_filePreview, 2);
            grid.Controls.Add(Caption(LT("Analysis folder:", "분석 폴더:")), 0, 3);
            grid.Controls.Add(_analysisPreview, 1, 3);
            grid.SetColumnSpan(_analysisPreview, 2);
            grid.Controls.Add(_include, 0, 4);
            grid.SetColumnSpan(_include, 3);
            grid.Controls.Add(_problem, 0, 5);
            grid.SetColumnSpan(_problem, 3);
            _problem.Height = 40;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0), BackColor = palette.Window };
            var cancel = Btn(LT("Cancel", "취소"));
            cancel.DialogResult = DialogResult.Cancel;
            _ok = Btn(LT("Create", "만들기"));
            _ok.Name = "createButton";
            _ok.Click += (_, _) => { if (UpdateState() is null) DialogResult = DialogResult.OK; };
            bar.Controls.Add(cancel);
            bar.Controls.Add(_ok);
            AcceptButton = _ok;
            CancelButton = cancel;

            Controls.Add(grid);
            Controls.Add(bar);
            _folder.TextChanged += (_, _) => UpdateState();
            _name.TextChanged += (_, _) => UpdateState();
            Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
            UpdateState();
        }

        /// <summary>미리보기·문제 표시·확인 버튼 활성을 갱신하고 문제(없으면 null)를 돌려준다.</summary>
        private string? UpdateState()
        {
            string? problem = NewWorkspaceRequest.Validate(_folder.Text, _name.Text);
            _problem.Text = problem ?? "";
            _ok.Enabled = problem is null;
            string name = NewWorkspaceRequest.NormalizeName(_name.Text);
            string folder = _folder.Text.Trim();
            if (problem is null)
            {
                var req = new NewWorkspaceRequest(Path.GetFullPath(folder), name, false);
                _filePreview.Text = req.FilePath;
                _analysisPreview.Text = req.AnalysisFolder;
            }
            else
            {
                // 문제가 있어도 가능한 만큼 미리 보여 준다(이름이 비었을 때 등).
                string shown = name.Length == 0 ? "…" : name;
                _filePreview.Text = Path.Combine(folder, shown + WorkspaceFile.Extension);
                _analysisPreview.Text = Path.Combine(folder, shown + AgentWorkspace.OutputSuffix);
            }
            return problem;
        }

        private void Browse()
        {
            using var dlg = new FolderBrowserDialog { Description = LT("Choose where to create the workspace", "작업 공간을 만들 위치를 고르세요"), UseDescriptionForTitle = true };
            if (Directory.Exists(_folder.Text.Trim())) dlg.InitialDirectory = _folder.Text.Trim();
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        }
    }
}
