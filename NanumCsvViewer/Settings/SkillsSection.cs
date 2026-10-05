using NanumCsvViewer.Agent;

namespace NanumCsvViewer
{
    /// <summary>
    /// 설정 ▸ AI 에이전트 쪽의 "분석 스킬" 구역. 전체 켜기/끄기, 분류별 체크(스킬 수 표시), 스킬 목록(설명·토큰), 토큰 추정치, 제3자 안내·검증 범위 밖 표기 안내,
    /// 그리고 '요약만' + 로컬 Python일 때의 경고. 값은 쪽의 확인·적용 때 <see cref="WriteTo"/>로 설정에 쓴다.
    /// </summary>
    internal sealed class SkillsSection : FlowLayoutPanel
    {
        private readonly ThemePalette _p;
        private readonly SkillManifest _manifest = SkillPack.Manifest;
        private readonly Label _heading, _note, _tokens, _warning, _pythonOff;
        private readonly CheckBox _master;
        private readonly Dictionary<string, CheckBox> _categoryBoxes = new();
        private readonly ListView _list;
        private bool _loading;
        private bool _python, _summaryOnly = true;
        /// <summary>경고·안내 글이 보이도록 정했는가(Control.Visible은 창이 열리기 전에는 부모 때문에 항상 false라 따로 둔다).</summary>
        private bool _warningShown, _pythonOffShown;
        private SkillSelection _baseline = SkillSelection.Default;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;
        private bool Korean => Loc.CurrentLanguage == "ko";

        public SkillsSection(ThemePalette palette, int width)
        {
            _p = palette;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = Padding.Empty;
            Padding = Padding.Empty;

            _heading = new Label { AutoSize = true, UseMnemonic = false, ForeColor = palette.Text, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 14, 0, 2), MaximumSize = new Size(width, 0) };
            Controls.Add(_heading);

            _master = NewCheck(width);
            _master.CheckedChanged += (_, _) => { if (!_loading) { UpdateEnabledState(); UpdateSummary(); } };
            Controls.Add(_master);

            _pythonOff = NewNote(width);
            _pythonOff.ForeColor = Warn();
            Controls.Add(_pythonOff);

            foreach (var cat in _manifest.Categories.Where(c => c.Id != SkillCategories.App))
            {
                var box = NewCheck(width - 24);
                box.Margin = new Padding(24, 2, 0, 2);
                box.Tag = cat.Id;
                box.Click += (_, _) => OnCategoryClick(cat.Id, box);
                _categoryBoxes[cat.Id] = box;
                Controls.Add(box);
            }

            _list = new ListView
            {
                View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowGroups = true,
                MultiSelect = false, ShowItemToolTips = true, Width = width, Height = 250, BorderStyle = BorderStyle.FixedSingle,
                BackColor = palette.GridBg, ForeColor = palette.Text, Margin = new Padding(0, 6, 0, 4), HideSelection = false,
            };
            _list.Columns.Add("", 170);
            _list.Columns.Add("", width - 170 - 72 - 22);
            _list.Columns.Add("", 72, HorizontalAlignment.Right);
            foreach (var cat in _manifest.Categories)
                _list.Groups.Add(new ListViewGroup(cat.Id, cat.Id) { Name = cat.Id });
            foreach (var skill in _manifest.Skills)
            {
                var item = new ListViewItem(skill.Name) { Tag = skill, Group = _list.Groups[skill.Category], Checked = true };
                item.SubItems.Add("");
                item.SubItems.Add("");
                _list.Items.Add(item);
            }
            _list.ItemCheck += OnItemCheck;
            _list.ItemChecked += OnItemChecked;
            Controls.Add(_list);

            _tokens = NewNote(width);
            Controls.Add(_tokens);
            _warning = NewNote(width);
            _warning.ForeColor = Warn();
            Controls.Add(_warning);
            _note = NewNote(width);
            Controls.Add(_note);

            Relocalize();
        }

        private CheckBox NewCheck(int width) =>
            new() { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(width, 0), ForeColor = _p.Text, BackColor = _p.Window, Margin = new Padding(0, 4, 0, 4) };

        private Label NewNote(int width) =>
            new() { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(width, 0), ForeColor = Blend(_p.Text, _p.Window, 0.35), Margin = new Padding(0, 4, 0, 4) };

        private Color Warn() => _p.Window.GetBrightness() < 0.5f ? Color.FromArgb(0xE5, 0xB8, 0x4B) : Color.FromArgb(0x9A, 0x5B, 0x00);

        private static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t));

        // ---- 값 ---------------------------------------------------------------------------------------------

        /// <summary>설정에서 읽어 체크를 채운다.</summary>
        public void Load(AppSettings s) => Show(SkillSelection.FromSettings(s));

        private void Show(SkillSelection sel)
        {
            _loading = true;
            try
            {
                _baseline = sel;
                _master.Checked = sel.Enabled;
                foreach (ListViewItem item in _list.Items)
                {
                    var skill = (SkillInfo)item.Tag!;
                    item.Checked = skill.IsApp || (!sel.CategoryOff(skill.Category) && !sel.SkillOff(skill.Name));
                }
                SyncCategories();
                UpdateEnabledState();
            }
            finally { _loading = false; }
            UpdateSummary();
        }

        /// <summary>기본값(모두 켜짐)으로 되돌린다.</summary>
        public void ResetDefaults() => Show(SkillSelection.Default);

        /// <summary>지금 체크 상태가 가리키는 구성.</summary>
        public SkillSelection Current() => SkillSelection.FromChecks(_master.Checked, _manifest, s => CheckedOf(s.Name));

        public bool IsDirty => Current() != _baseline;

        /// <summary>쪽의 로컬 Python 체크와 데이터 공유 선택이 바뀔 때 경고·안내를 갱신한다.</summary>
        public void SetContext(bool localPython, AgentDataPolicy policy)
        {
            _python = localPython;
            _summaryOnly = policy == AgentDataPolicy.SummaryOnly;
            UpdateSummary();
        }

        /// <summary>체크를 설정에 쓴다. 바뀐 게 있었으면 true.</summary>
        public bool WriteTo(AppSettings s)
        {
            var cur = Current();
            bool changed = cur != SkillSelection.FromSettings(s);
            s.AgentSkillsEnabled = cur.Enabled;
            s.AgentSkillCategoriesOff = cur.CategoriesOff;
            s.AgentSkillsOff = cur.SkillsOff;
            _baseline = cur;
            return changed;
        }

        private bool CheckedOf(string name) =>
            _list.Items.Cast<ListViewItem>().First(i => ((SkillInfo)i.Tag!).Name == name).Checked;

        // ---- 화면 상태(테스트가 읽고 만진다) -----------------------------------------------------------------------

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool MasterChecked { get => _master.Checked; set => _master.Checked = value; }
        internal string CategoryText(string id) => _categoryBoxes[id].Text;
        internal void ToggleCategory(string id) => OnCategoryClick(id, _categoryBoxes[id]);
        internal bool SkillChecked(string name) => CheckedOf(name);
        internal void SetSkillChecked(string name, bool on) =>
            _list.Items.Cast<ListViewItem>().First(i => ((SkillInfo)i.Tag!).Name == name).Checked = on;
        internal string Description(string name) =>
            _list.Items.Cast<ListViewItem>().First(i => ((SkillInfo)i.Tag!).Name == name).SubItems[1].Text;
        internal bool WarningVisible => _warningShown;
        internal string WarningText => _warning.Text;
        internal string TokensText => _tokens.Text;
        internal string NoteText => _note.Text;
        internal bool PythonOffNoticeVisible => _pythonOffShown;
        internal bool ListEnabled => _list.Enabled;

        // ---- 동작 ---------------------------------------------------------------------------------------------

        private bool _refreshQueued;

        private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
        {
            if (_loading) return;
            // 앱 전용 스킬은 끌 수 없다(사용자 입력은 ItemCheck에서, 코드로 바꾼 경우는 여기서 되돌린다).
            if (e.Item is { Checked: false, Tag: SkillInfo { IsApp: true } } app)
            {
                _loading = true;
                try { app.Checked = true; }
                finally { _loading = false; }
            }
            // 핸들이 만들어질 때 항목마다 이 이벤트가 연달아 오므로 한 번에 모아 갱신한다(그때마다 배치를 다시 계산하지 않도록).
            if (_refreshQueued || !IsHandleCreated) return;
            _refreshQueued = true;
            BeginInvoke(() =>
            {
                _refreshQueued = false;
                SyncCategories();
                UpdateSummary();
            });
        }

        private void OnItemCheck(object? sender, ItemCheckEventArgs e)
        {
            // 앱 전용 스킬(nanum-python-analysis)은 끌 수 없다: 데이터 정책·표기 규칙을 담고 있다.
            if (!_loading && _list.Items[e.Index].Tag is SkillInfo { IsApp: true })
                e.NewValue = CheckState.Checked;
        }

        private void OnCategoryClick(string category, CheckBox box)
        {
            if (_loading) return;
            // 일부만 켜져 있거나 모두 꺼져 있으면 누르면 모두 켜고, 모두 켜져 있으면 모두 끈다(앱 전용 스킬은 분류에 없다).
            var items = _list.Items.Cast<ListViewItem>().Where(i => ((SkillInfo)i.Tag!).Category == category).ToList();
            bool target = items.Any(i => !i.Checked);
            _loading = true;
            try
            {
                foreach (var item in items) item.Checked = target;
                SyncCategories();
            }
            finally { _loading = false; }
            UpdateSummary();
        }

        private void SyncCategories()
        {
            bool was = _loading;
            _loading = true;
            try
            {
                foreach (var (id, box) in _categoryBoxes)
                {
                    var items = _list.Items.Cast<ListViewItem>().Where(i => ((SkillInfo)i.Tag!).Category == id).ToList();
                    int on = items.Count(i => i.Checked);
                    box.CheckState = on == 0 ? CheckState.Unchecked : on == items.Count ? CheckState.Checked : CheckState.Indeterminate;
                }
            }
            finally { _loading = was; }
        }

        private void UpdateEnabledState()
        {
            bool on = _master.Checked;
            foreach (var box in _categoryBoxes.Values) box.Enabled = on;
            _list.Enabled = on;
        }

        /// <summary>같은 값이면 쓰지 않는다(글자·표시가 바뀔 때만 배치가 다시 계산되도록).</summary>
        private static void SetText(Label l, string text) { if (l.Text != text) l.Text = text; }
        private static void SetVisible(Control c, bool visible) { if (c.Visible != visible) c.Visible = visible; }

        private void UpdateSummary()
        {
            var cur = Current();
            int count = SkillPack.Resolve(cur).Count;
            int tokens = SkillPack.EstimateTokens(cur);
            _pythonOffShown = !_python;
            SetText(_pythonOff, _python ? "" : LT(
                "Local Python analysis is off, so no skills are loaded and they cost nothing. Turn on 'Allow local Python analysis' above to use them.",
                "로컬 Python 분석이 꺼져 있어 스킬은 실리지 않고 비용도 없습니다. 쓰려면 위의 '로컬 Python 분석 허용'을 켜세요."));
            SetVisible(_pythonOff, _pythonOffShown);
            SetText(_tokens, cur.Enabled
                ? LT($"Estimated cost when loaded: about {tokens:N0} tokens at the start of each conversation ({count} skills; only name and a short description are loaded, a skill's text is read only when the agent uses it).",
                     $"실리면 대화를 시작할 때마다 약 {tokens:N0} 토큰이 더해집니다(스킬 {count}개; 이름과 짧은 설명만 실리고, 스킬 본문은 에이전트가 쓸 때만 읽습니다).")
                : LT("Skills are off: nothing is added.", "스킬이 꺼져 있어 추가되는 것이 없습니다."));
            _warningShown = _python && _summaryOnly && cur.Enabled;
            SetText(_warning, _warningShown ? LT(
                "Data sharing is 'Summary only', but Python output is read by the AI model. The skills instruct the agent to print aggregates only, which is guidance, not a hard guarantee: code the agent writes can still print rows.",
                "데이터 공유가 '요약만'이어도 Python 출력은 AI 모델이 읽습니다. 스킬은 집계만 출력하라고 지시하지만 이는 안내일 뿐 강제 보장이 아닙니다. 에이전트가 쓴 코드가 행을 출력할 수도 있습니다.") : "");
            SetVisible(_warning, _warningShown);
        }

        public void Relocalize()
        {
            _heading.Text = LT("Analysis skills", "분석 스킬");
            _master.Text = LT("Use analysis skills (clinical research, statistics, machine learning)", "분석 스킬 사용(임상 연구·통계·기계학습)");
            foreach (var cat in _manifest.Categories.Where(c => c.Id != SkillCategories.App))
            {
                _categoryBoxes[cat.Id].Text = $"{cat.Label(Korean)} ({_manifest.Count(cat.Id)})";
            }
            foreach (ListViewGroup g in _list.Groups)
            {
                var cat = _manifest.Categories.First(c => c.Id == g.Name);
                g.Header = $"{cat.Label(Korean)} ({_manifest.Count(cat.Id)})";
            }
            _list.Columns[0].Text = LT("Skill", "스킬");
            _list.Columns[1].Text = LT("What it is for", "용도");
            _list.Columns[2].Text = LT("≈ tokens", "≈ 토큰");
            foreach (ListViewItem item in _list.Items)
            {
                var skill = (SkillInfo)item.Tag!;
                item.SubItems[1].Text = skill.Summary(Korean);
                item.SubItems[2].Text = skill.Tokens.ToString();
                string deps = skill.PythonDeps.Count > 0 ? "\n" + LT("Python packages: ", "Python 패키지: ") + string.Join(", ", skill.PythonDeps) : "";
                string caveat = string.IsNullOrEmpty(skill.Caveat) ? "" : "\n" + LT("Note: ", "참고: ") + skill.Caveat;
                item.ToolTipText = skill.Summary(Korean) + "\n" + LT("Why included: ", "포함 이유: ") + skill.Reason + deps + caveat
                    + (skill.IsApp ? "\n" + LT("Always loaded with the pack.", "묶음과 함께 항상 실립니다.") : "");
            }
            _note.Text = LT(
                "The skills are third-party guidance (K-Dense scientific-agent-skills, MIT license, pinned to " + _manifest.UpstreamTag + ") plus the app's own rules (nanum-python-analysis). The app does not validate them: every Python result is labelled 'Python analysis (outside the app's validated tools)', and the agent uses the app's validated tools first. They are for research and aggregate analysis, not patient-specific diagnosis. Changes apply the next time the agent starts; if it is connected, the app restarts it on the same conversation.",
                "스킬은 제3자의 안내문(K-Dense scientific-agent-skills, MIT 라이선스, " + _manifest.UpstreamTag + " 고정)과 앱 자체 규칙(nanum-python-analysis)입니다. 앱이 검증하지 않으며, 모든 Python 결과에 'Python 분석 (앱 검증 범위 밖)' 표기가 붙고 에이전트는 앱의 검증된 도구를 먼저 씁니다. 연구·집계 분석용이며 환자 개별 진단용이 아닙니다. 바꾸면 다음에 에이전트를 시작할 때 반영되며, 연결되어 있으면 앱이 같은 대화로 다시 시작합니다.");
            UpdateSummary();
        }
    }
}
