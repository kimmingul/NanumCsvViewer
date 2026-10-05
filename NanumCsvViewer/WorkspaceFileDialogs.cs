using System.Drawing;
using System.Windows.Forms;

namespace NanumCsvViewer
{
    /// <summary>
    /// 작업 공간을 열 때 찾지 못한 원본 파일 목록. 항목마다 "찾아보기…"(파일을 직접 고름 — 같은 폴더의 다른 누락 파일도 함께 찾아 본다)
    /// 또는 "건너뛰기". 확인을 누르면 찾지 못한 항목은 건너뛰고 나머지로 연다.
    /// </summary>
    internal sealed class WorkspaceMissingDialog : Form
    {
        internal sealed class Item
        {
            public Item(string label, string lastKnownPath, string? extension)
            {
                Label = label;
                LastKnownPath = lastKnownPath;
                Extension = extension;
            }

            public string Label { get; }
            public string LastKnownPath { get; }
            public string? Extension { get; }
            /// <summary>사용자가 찾아 준 파일(없으면 null).</summary>
            public string? Located { get; set; }
            public bool Skipped { get; set; }
        }

        private readonly IReadOnlyList<Item> _items;
        private readonly string _startFolder;
        private readonly ListView _list;
        private readonly Button _locate, _skip, _ok;

        public WorkspaceMissingDialog(ThemePalette palette, string workspaceName, IReadOnlyList<Item> items, string startFolder)
        {
            _items = items;
            _startFolder = startFolder;
            Text = ViewerSupport.LT("Files not found", "찾을 수 없는 파일");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 330);
            MinimumSize = new Size(560, 300);
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

            var message = new Label
            {
                Dock = DockStyle.Top,
                Height = 52,
                Padding = new Padding(12, 10, 12, 0),
                Text = ViewerSupport.LT(
                    $"{items.Count} file(s) of workspace '{workspaceName}' were not found. Locate each one, or skip it — skipped files are left out of the opened workspace (the workspace file itself is not changed until you save).",
                    $"작업 공간 '{workspaceName}'의 파일 {items.Count}개를 찾을 수 없습니다. 하나씩 찾아 주거나 건너뛰세요. 건너뛴 파일은 열린 작업 공간에서 빠지며, 저장하기 전에는 작업 공간 파일이 바뀌지 않습니다."),
                ForeColor = palette.Text,
            };

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
            _list.Columns.Add(ViewerSupport.LT("Source", "원본"), 150);
            _list.Columns.Add(ViewerSupport.LT("Last known path", "마지막 경로"), 400);
            _list.Columns.Add(ViewerSupport.LT("Status", "상태"), 160);
            foreach (var it in items) _list.Items.Add(new ListViewItem(new[] { it.Label, it.LastKnownPath, "" }) { Tag = it });
            _list.SelectedIndexChanged += (_, _) => UpdateButtons();
            _list.DoubleClick += (_, _) => Locate();

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8, 6, 8, 6),
                BackColor = palette.Window,
            };
            var cancel = MakeButton(ViewerSupport.LT("Cancel", "취소"), palette);
            cancel.DialogResult = DialogResult.Cancel;
            _ok = MakeButton(ViewerSupport.LT("Open without the missing files", "없는 파일 빼고 열기"), palette);
            _ok.AutoSize = true;
            _ok.DialogResult = DialogResult.OK;
            _skip = MakeButton(ViewerSupport.LT("Skip", "건너뛰기"), palette);
            _skip.Click += (_, _) => Skip();
            _locate = MakeButton(ViewerSupport.LT("Locate…", "찾아보기…"), palette);
            _locate.Click += (_, _) => Locate();
            bar.Controls.AddRange(new Control[] { cancel, _ok, _skip, _locate });

            Controls.Add(_list);
            Controls.Add(bar);
            Controls.Add(message);
            AcceptButton = _ok;
            CancelButton = cancel;
            RefreshRows(0);
            UpdateButtons();
        }

        private static Button MakeButton(string text, ThemePalette palette) => new()
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(88, 28),
            FlatStyle = FlatStyle.System,
            BackColor = palette.Surface,
            ForeColor = palette.Text,
            Margin = new Padding(6, 0, 0, 0),
        };

        private Item? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Item : null;

        private void UpdateButtons()
        {
            _locate.Enabled = _skip.Enabled = Selected is not null;
        }

        private void RefreshRows(int selectIndex)
        {
            foreach (ListViewItem row in _list.Items)
            {
                var it = (Item)row.Tag!;
                row.SubItems[2].Text = it.Located is not null
                    ? ViewerSupport.LT("Found: ", "찾음: ") + it.Located
                    : it.Skipped ? ViewerSupport.LT("Skipped", "건너뜀") : ViewerSupport.LT("Missing", "없음");
            }
            if (_list.Items.Count > 0 && _list.SelectedItems.Count == 0) _list.Items[Math.Clamp(selectIndex, 0, _list.Items.Count - 1)].Selected = true;
        }

        private void Skip()
        {
            if (Selected is not { } it) return;
            it.Located = null;
            it.Skipped = true;
            RefreshRows(0);
            SelectNextUnresolved();
        }

        private void Locate()
        {
            if (Selected is not { } it) return;
            using var dlg = new OpenFileDialog
            {
                Title = ViewerSupport.LT("Locate ", "찾을 파일: ") + it.Label,
                FileName = Path.GetFileName(it.LastKnownPath),
                Filter = (string.IsNullOrEmpty(it.Extension) ? "" : $"*{it.Extension}|*{it.Extension}|") + ViewerSupport.LT("All files (*.*)|*.*", "모든 파일 (*.*)|*.*"),
                CheckFileExists = true,
            };
            string? lastDir = Path.GetDirectoryName(it.LastKnownPath);
            dlg.InitialDirectory = !string.IsNullOrEmpty(lastDir) && Directory.Exists(lastDir) ? lastDir : _startFolder;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            it.Located = dlg.FileName;
            it.Skipped = false;
            // 파일을 폴더째 옮긴 경우가 많다: 같은 폴더에 다른 누락 파일의 이름이 있으면 그것도 찾은 것으로 한다.
            string? newDir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(newDir))
            {
                foreach (var other in _items)
                {
                    if (ReferenceEquals(other, it) || other.Located is not null) continue;
                    string guess = Path.Combine(newDir, Path.GetFileName(other.LastKnownPath));
                    if (File.Exists(guess)) { other.Located = guess; other.Skipped = false; }
                }
            }
            RefreshRows(0);
            SelectNextUnresolved();
        }

        private void SelectNextUnresolved()
        {
            for (int i = 0; i < _list.Items.Count; i++)
            {
                var it = (Item)_list.Items[i].Tag!;
                if (it.Located is null && !it.Skipped) { _list.SelectedItems.Clear(); _list.Items[i].Selected = true; _list.EnsureVisible(i); return; }
            }
        }
    }

    /// <summary>작업 공간을 여는 동안(원본 등록·탭 열기·뷰 계산) 진행 문구와 취소 단추를 보이는 작은 비모달 창.</summary>
    internal sealed class WorkspaceProgressForm : Form
    {
        private readonly Label _label;
        private readonly CancellationTokenSource _cts = new();

        public WorkspaceProgressForm(ThemePalette palette, string title)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 110);
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

            _label = new Label { Left = 14, Top = 12, Width = 430, Height = 36, AutoEllipsis = true, ForeColor = palette.Text };
            var bar = new ProgressBar { Left = 14, Top = 52, Width = 430, Height = 14, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            var cancel = new Button
            {
                Text = ViewerSupport.LT("Cancel", "취소"), Left = 360, Top = 74, Width = 84, Height = 26,
                FlatStyle = FlatStyle.System, BackColor = palette.Surface, ForeColor = palette.Text,
            };
            cancel.Click += (_, _) => { cancel.Enabled = false; _cts.Cancel(); };
            Controls.AddRange(new Control[] { _label, bar, cancel });
        }

        public CancellationToken Token => _cts.Token;

        public void Report(string text) => _label.Text = text;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts.Dispose();
            base.Dispose(disposing);
        }
    }
}
