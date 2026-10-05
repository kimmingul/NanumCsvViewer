namespace NanumCsvViewer
{
    /// <summary>
    /// 설정 대화 상자: 왼쪽 범주 목록, 오른쪽 쪽(일반·패널과 배치·그리드·파일과 데이터·AI 에이전트·단축키),
    /// 아래 확인·취소·적용·기본값으로. 쪽마다 값을 읽고(<see cref="SettingsPage.LoadValues"/>) 적용(<see cref="SettingsPage.Commit"/>)한다.
    /// 언어를 바꿔 적용하면 이 대화 상자도 그 자리에서 새 언어로 바뀐다.
    /// </summary>
    internal sealed class SettingsDialog : Form
    {
        // 마지막으로 본 쪽(같은 실행 안에서 다음에 열 때 그 쪽에서 시작)
        private static string? s_lastPage;

        private readonly Form1 _host;
        private readonly ThemePalette _p;
        private readonly ListBox _nav;
        private readonly Panel _pageHost;
        private readonly Button _ok, _cancel, _apply, _reset;
        private readonly List<SettingsPage> _pages;
        private SettingsPage? _current;

        internal IReadOnlyList<SettingsPage> Pages => _pages;
        internal SettingsPage? CurrentPage => _current;

        /// <summary>테마가 바뀌어 새 팔레트로 다시 열어야 한다(적용 직후 닫힌다).</summary>
        internal bool ReopenRequested { get; private set; }

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        internal SettingsDialog(Form1 host, string? page)
        {
            _host = host;
            _p = host.PaletteRef;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            BackColor = _p.Window;
            ForeColor = _p.Text;
            ClientSize = new Size(800, 620);

            _nav = new ListBox
            {
                Dock = DockStyle.Left,
                Width = 180,
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 34,
                IntegralHeight = false,
                BackColor = _p.Surface,
                ForeColor = _p.Text,
            };
            _nav.DrawItem += DrawNavItem;
            _nav.SelectedIndexChanged += (_, _) => { if (_nav.SelectedIndex >= 0) ShowPage(_pages[_nav.SelectedIndex]); };

            _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = _p.Window, Padding = new Padding(0) };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = _p.Window };
            _apply = MakeButton(bottom, DialogResult.None);
            _cancel = MakeButton(bottom, DialogResult.Cancel);
            _ok = MakeButton(bottom, DialogResult.None);
            _reset = MakeButton(bottom, DialogResult.None);
            _ok.Click += (_, _) => { if (ApplyAll()) { ReopenRequested = false; DialogResult = DialogResult.OK; Close(); } };
            _apply.Click += (_, _) =>
            {
                // 테마를 바꿨다면 이 창의 색도 새 테마여야 하므로 닫았다가 같은 쪽에서 다시 연다.
                if (ApplyAll() && !ReferenceEquals(_host.PaletteRef, _p)) { ReopenRequested = true; DialogResult = DialogResult.OK; Close(); }
            };
            _reset.Click += (_, _) => _current?.ResetDefaults();
            bottom.Resize += (_, _) => LayoutButtons(bottom);
            var separator = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = _p.Border };

            _pages = new List<SettingsPage>
            {
                new GeneralPage(host, _p),
                new PanelsPage(host, _p),
                new GridPage(host, _p),
                new FilesPage(host, _p),
                new AgentPage(host, _p),
                new ShortcutsPage(host, _p),
            };

            Controls.Add(_pageHost);
            Controls.Add(_nav);
            Controls.Add(separator);
            Controls.Add(bottom);
            LayoutButtons(bottom);
            AcceptButton = _ok;
            CancelButton = _cancel;
            ResumeLayout(false);
            foreach (var p in _pages) _nav.Items.Add(new NavEntry(p));   // 화면 읽기·UI 자동화는 항목의 ToString()을 이름으로 쓴다

            Relocalize();
            foreach (var p in _pages) p.LoadValues();
            SelectPage(page ?? s_lastPage);
        }

        private Button MakeButton(Control parent, DialogResult result)
        {
            var b = new Button
            {
                DialogResult = result,
                FlatStyle = FlatStyle.Flat,
                BackColor = _p.Surface,
                ForeColor = _p.Text,
                Size = new Size(96, 30),
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = _p.Border;
            parent.Controls.Add(b);
            return b;
        }

        private void LayoutButtons(Control bottom)
        {
            int y = (bottom.Height - _ok.Height) / 2, right = bottom.Width - 12;
            foreach (var b in new[] { _apply, _cancel, _ok })
            {
                b.Location = new Point(right - b.Width, y);
                right -= b.Width + 8;
            }
            _reset.Width = 130;
            _reset.Location = new Point(12, y);
        }

        private void DrawNavItem(object? sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var back = new SolidBrush(selected ? _p.SelectionBg : _p.Surface);
            e.Graphics.FillRectangle(back, e.Bounds);
            var rect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, _pages[e.Index].Title, Font, rect, selected ? _p.SelectionText : _p.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>쪽 id(general · panels · grid · files · ai · shortcuts)로 이동한다. 모르는 id·null이면 첫 쪽.</summary>
        internal void SelectPage(string? id)
        {
            int index = Math.Max(0, _pages.FindIndex(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));
            if (_nav.SelectedIndex == index) ShowPage(_pages[index]);
            else _nav.SelectedIndex = index;
        }

        private void ShowPage(SettingsPage page)
        {
            if (ReferenceEquals(_current, page)) return;
            _current = page;
            s_lastPage = page.Id;
            _pageHost.SuspendLayout();
            _pageHost.Controls.Clear();
            page.Dock = DockStyle.Fill;
            _pageHost.Controls.Add(page);
            _pageHost.ResumeLayout(true);
            _reset.Enabled = page.CanReset;
        }

        /// <summary>모든 글자를 현재 언어로 다시 정한다(언어를 적용한 직후, 시작할 때).</summary>
        internal void Relocalize()
        {
            Text = LT("Settings", "설정");
            _ok.Text = "OK";
            _cancel.Text = LT("Cancel", "취소");
            _apply.Text = LT("Apply", "적용");
            _reset.Text = LT("Reset to Defaults", "기본값으로");
            foreach (var p in _pages) p.Relocalize();
            _nav.Invalidate();
            if (_current is not null) _reset.Enabled = _current.CanReset;
        }

        /// <summary>모든 쪽을 적용하고(설정 저장) 쪽들을 다시 읽는다. 어느 쪽이 거절하면 거기서 멈추고 false.</summary>
        internal bool ApplyAll()
        {
            string before = Loc.CurrentLanguage;
            foreach (var p in _pages)
                if (!p.Commit()) { SelectPage(p.Id); return false; }
            _host.AppSettingsRef.Save();
            foreach (var p in _pages) p.LoadValues();
            if (Loc.CurrentLanguage != before) Relocalize();
            return true;
        }
    }

    /// <summary>범주 목록의 한 항목. 글자는 쪽 제목(현재 언어)이다.</summary>
    internal sealed class NavEntry
    {
        private readonly SettingsPage _page;
        public NavEntry(SettingsPage page) => _page = page;
        public override string ToString() => _page.Title;
    }
}
