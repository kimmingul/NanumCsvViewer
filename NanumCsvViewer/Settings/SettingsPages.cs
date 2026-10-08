using NanumCsvViewer.Agent;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    /// <summary>설정 대화 상자의 한 쪽. 값 읽기·적용·기본값·다시 번역은 쪽마다 구현하고, 입력 칸 만들기는 여기서 한다.</summary>
    internal abstract class SettingsPage : Panel
    {
        protected readonly Form1 Host;
        protected readonly ThemePalette P;
        protected AppSettings S => Host.AppSettingsRef;
        private readonly TableLayoutPanel _table;
        private readonly List<Action> _relabel = new();

        protected static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;
        protected const int PageWidth = 560;

        public abstract string Id { get; }
        public abstract string Title { get; }
        /// <summary>이 쪽에 "기본값으로"가 의미 있는가.</summary>
        public virtual bool CanReset => true;

        protected SettingsPage(Form1 host, ThemePalette palette)
        {
            Host = host;
            P = palette;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            AutoScroll = true;
            Padding = new Padding(22, 18, 18, 12);
            _table = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = palette.Window,
            };
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(_table);
        }

        /// <summary>설정 값을 읽어 입력 칸에 채운다.</summary>
        public abstract void LoadValues();
        /// <summary>입력 칸의 값을 설정에 적용한다. 거절하면 false.</summary>
        public abstract bool Commit();
        /// <summary>입력 칸을 기본값으로 되돌린다(적용은 확인·적용 때).</summary>
        public abstract void ResetDefaults();

        /// <summary>다시 번역하는 동안 true. 콤보 항목을 비웠다 다시 채우며 SelectedIndexChanged가 나므로, 그 변경은 사용자 조작이 아니다.</summary>
        protected bool Relocalizing { get; private set; }

        public void Relocalize()
        {
            bool was = Relocalizing;
            Relocalizing = true;
            try { foreach (var a in _relabel) a(); }
            finally { Relocalizing = was; }
        }

        /// <summary>콤보의 선택을 정한다. 항목이 아직 없거나(다시 번역 전) 범위를 벗어나면 가장 가까운 항목, 항목이 없으면 아무것도 하지 않는다.</summary>
        protected static void SelectIndex(ComboBox cb, int index)
        {
            if (cb.Items.Count > 0) cb.SelectedIndex = Math.Clamp(index, 0, cb.Items.Count - 1);
        }

        // ---- 입력 칸 만들기 ------------------------------------------------------------------------------------

        private void AddRow(Control? label, Control input)
        {
            int row = _table.RowCount++;
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label is null)
            {
                input.Margin = new Padding(0, 6, 0, 6);
                _table.Controls.Add(input, 0, row);
                _table.SetColumnSpan(input, 2);
            }
            else
            {
                label.Margin = new Padding(0, 9, 8, 6);
                input.Margin = new Padding(0, 5, 0, 5);
                _table.Controls.Add(label, 0, row);
                _table.Controls.Add(input, 1, row);
            }
        }

        protected Label Heading(Func<string> text)
        {
            var l = new Label { AutoSize = true, UseMnemonic = false, ForeColor = P.Text, Font = new Font(Font, FontStyle.Bold), MaximumSize = new Size(PageWidth, 0) };
            l.Margin = new Padding(0, 12, 0, 2);
            Relabel(() => l.Text = text());
            AddRow(null, l);
            return l;
        }

        protected Label Note(Func<string> text)
        {
            var l = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(PageWidth, 0), ForeColor = Blend(P.Text, P.Window, 0.35) };
            Relabel(() => l.Text = text());
            AddRow(null, l);
            return l;
        }

        protected CheckBox Check(Func<string> text)
        {
            var c = new CheckBox { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(PageWidth, 0), ForeColor = P.Text, BackColor = P.Window };
            Relabel(() => c.Text = text());
            AddRow(null, c);
            return c;
        }

        protected ComboBox Combo(Func<string> label, params Func<string>[] items)
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 320, DropDownWidth = 400,
                BackColor = P.Surface, ForeColor = P.Text,
            };
            var l = NewLabel();
            Relabel(() =>
            {
                l.Text = label();
                int index = cb.SelectedIndex;
                cb.BeginUpdate();
                cb.Items.Clear();
                foreach (var item in items) cb.Items.Add(item());
                cb.EndUpdate();
                SelectIndex(cb, index);
            });
            AddRow(l, cb);
            return cb;
        }

        /// <summary>항목이 실행 중에 바뀌는 콤보(승인 모드처럼 언어마다 글자가 다른 열거형).</summary>
        protected ComboBox Combo(Func<string> label, Func<IReadOnlyList<string>> items)
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 320, DropDownWidth = 400,
                BackColor = P.Surface, ForeColor = P.Text,
            };
            var l = NewLabel();
            Relabel(() =>
            {
                l.Text = label();
                int index = cb.SelectedIndex;
                cb.BeginUpdate();
                cb.Items.Clear();
                foreach (var item in items()) cb.Items.Add(item);
                cb.EndUpdate();
                SelectIndex(cb, index);
            });
            AddRow(l, cb);
            return cb;
        }

        protected NumericUpDown Number(Func<string> label, int min, int max)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Width = 100, BackColor = P.Surface, ForeColor = P.Text, BorderStyle = BorderStyle.FixedSingle };
            var l = NewLabel();
            Relabel(() => l.Text = label());
            AddRow(l, n);
            return n;
        }

        /// <summary>왼쪽에 이름표, 오른쪽에 임의의 컨트롤(여러 입력 칸을 한 줄에 묶은 패널 등)을 놓는다.</summary>
        protected void LabeledRow(Func<string> label, Control input)
        {
            var l = NewLabel();
            Relabel(() => l.Text = label());
            AddRow(l, input);
        }

        protected TextBox Text(Func<string> label)
        {
            var t = new TextBox { Width = 360, BackColor = P.Surface, ForeColor = P.Text, BorderStyle = BorderStyle.FixedSingle };
            var l = NewLabel();
            Relabel(() => l.Text = label());
            AddRow(l, t);
            return t;
        }

        protected Button ActionButton(Func<string> text, Action click)
        {
            var b = new Button { AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = P.Surface, ForeColor = P.Text, MinimumSize = new Size(120, 28), UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = P.Border;
            b.Click += (_, _) => click();
            Relabel(() => b.Text = text());
            AddRow(null, b);
            return b;
        }

        /// <summary>표 전체 폭을 쓰는 임의의 컨트롤을 놓는다.</summary>
        protected void AddWide(Control c) => AddRow(null, c);

        protected void Relabel(Action a)
        {
            _relabel.Add(a);
        }

        private Label NewLabel() => new() { AutoSize = false, UseMnemonic = false, Width = 182, Height = 24, TextAlign = ContentAlignment.MiddleLeft, ForeColor = P.Text };

        private static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t));
    }

    // ================================================================ 일반
    internal sealed class GeneralPage : SettingsPage
    {
        private readonly ComboBox _language, _theme;
        private readonly CheckBox _reopen;

        public override string Id => "general";
        public override string Title => LT("General", "일반");

        public GeneralPage(Form1 host, ThemePalette p) : base(host, p)
        {
            Heading(() => LT("Appearance", "화면"));
            _language = Combo(() => LT("Language", "언어"),
                () => LT("Auto (follow Windows)", "자동 (Windows 언어 따름)"), () => "English", () => "한국어");
            _theme = Combo(() => LT("Theme", "테마"),
                () => LT("System (follow Windows)", "시스템 (Windows 설정 따름)"), () => LT("Light", "밝게"), () => LT("Dark", "어둡게"));
            Note(() => LT("'System' follows the Windows light/dark setting, including while the app is running.",
                "'시스템'은 Windows의 밝게/어둡게 설정을 따르며 실행 중에 바뀌어도 따라갑니다."));
            Heading(() => LT("Startup", "시작"));
            _reopen = Check(() => LT("Reopen the last workspace on start", "시작할 때 마지막 작업 공간 다시 열기"));
            Note(() => LT("A file or workspace given on the command line (double-click in Explorer) is opened instead.",
                "탐색기에서 더블클릭하는 등 명령줄로 받은 파일·작업 공간이 있으면 그쪽을 엽니다."));
        }

        public override void LoadValues()
        {
            SelectIndex(_language, S.Language switch { "en" => 1, "ko" => 2, _ => 0 });
            SelectIndex(_theme, S.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 });
            _reopen.Checked = S.ReopenLastWorkspace;
        }

        public override bool Commit()
        {
            S.ReopenLastWorkspace = _reopen.Checked;
            string theme = _theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "" };
            if (theme != S.Theme) Host.ApplyThemeSetting(theme);
            string language = _language.SelectedIndex switch { 1 => "en", 2 => "ko", _ => "auto" };
            if (language != S.Language) Host.ApplyLanguageSetting(language);
            return true;
        }

        public override void ResetDefaults()
        {
            var d = new AppSettings();
            SelectIndex(_language, 0);
            SelectIndex(_theme, 0);
            _reopen.Checked = d.ReopenLastWorkspace;
        }
    }

    // ================================================================ 패널과 배치
    internal sealed class PanelsPage : SettingsPage
    {
        private readonly Dictionary<PanelKind, CheckBox> _startup = new();
        private readonly CheckBox _remember, _window;

        public override string Id => "panels";
        public override string Title => LT("Panels & Layout", "패널과 배치");

        private static readonly (PanelKind Kind, string En, string Ko)[] Items =
        {
            (PanelKind.Agent, "AI agent panel", "AI 에이전트 패널"),
            (PanelKind.Detail, "Row detail panel", "행 상세 패널"),
            (PanelKind.Facets, "Facets panel", "패싯 패널"),
            (PanelKind.Explorer, "Workspace explorer", "작업 공간 탐색기"),
            (PanelKind.Findings, "Quality findings panel", "품질 검사 결과 패널"),
            (PanelKind.CellBar, "Cell value bar", "셀 값 표시줄"),
        };

        public PanelsPage(Form1 host, ThemePalette p) : base(host, p)
        {
            Heading(() => LT("Panels shown at startup", "시작할 때 보이는 패널"));
            foreach (var (kind, en, ko) in Items)
            {
                var k = kind; string e = en, c = ko;
                _startup[k] = Check(() => LT(e, c));
            }
            _remember = Check(() => LT("Remember the panels as they were when I closed the app (instead of the choices above)",
                "앱을 닫을 때의 패널 상태를 기억해 다음에 그대로 열기 (위 선택 대신)"));
            _remember.CheckedChanged += (_, _) => SyncEnabled();
            ActionButton(() => LT("Show these panels now", "지금 이 패널로 바꾸기"), ShowNow);
            Note(() => LT("The AI panel can be on at startup without starting the AI agent; it starts with your first message.",
                "AI 패널을 켜 둬도 AI 에이전트(omp)는 시작하지 않습니다. 첫 메시지를 보낼 때 시작합니다."));
            Note(() => LT("The facets panel appears once a file is open. The findings panel stays open until you close it.",
                "패싯 패널은 파일이 열리면 나타납니다. 검사 결과 패널은 닫을 때까지 열려 있습니다."));

            Heading(() => LT("Window", "창"));
            _window = Check(() => LT("Remember window size and position", "창 크기와 위치 기억"));

            Heading(() => LT("Workspaces", "작업 공간"));
            Note(() => LT("A workspace file (.ncvws) stores its own panel layout (which panels are open and their widths) each time you save it and when you close it. Opening the workspace restores that layout instead of the choices above; a workspace without a saved layout uses the choices above. Layout changes never raise the 'save changes?' prompt.",
                "작업 공간 파일(.ncvws)은 저장할 때와 닫을 때 자기 패널 배치(열린 패널과 폭)를 함께 저장합니다. 그 작업 공간을 열면 위 선택 대신 저장된 배치가 복원되고, 배치가 저장되지 않은 작업 공간은 위 선택을 따릅니다. 배치만 바뀐 것은 '변경 사항을 저장할까요?' 확인을 띄우지 않습니다."));
        }

        private void SyncEnabled()
        {
            foreach (var c in _startup.Values) c.Enabled = !_remember.Checked;
        }

        private PanelLayout Draft()
        {
            var l = new PanelLayout();
            foreach (var (kind, cb) in _startup) l.Set(kind, cb.Checked);
            return l;
        }

        private void ShowNow()
        {
            var layout = _remember.Checked && S.LastPanels is { } last ? last : Draft();
            Host.ApplyLayout(layout);
        }

        public override void LoadValues()
        {
            foreach (var (kind, cb) in _startup) cb.Checked = S.StartupPanels.Get(kind);
            _remember.Checked = S.RememberLastPanels;
            _window.Checked = S.RememberWindow;
            SyncEnabled();
        }

        public override bool Commit()
        {
            var layout = Draft();
            layout.DetailWidth = S.StartupPanels.DetailWidth;
            layout.ExplorerWidth = S.StartupPanels.ExplorerWidth;
            layout.AgentWidth = S.StartupPanels.AgentWidth;
            S.StartupPanels = layout;
            S.RememberLastPanels = _remember.Checked;
            S.RememberWindow = _window.Checked;
            if (!S.RememberWindow) S.Window = null;
            return true;
        }

        public override void ResetDefaults()
        {
            var d = new AppSettings();
            foreach (var (kind, cb) in _startup) cb.Checked = d.StartupPanels.Get(kind);
            _remember.Checked = d.RememberLastPanels;
            _window.Checked = d.RememberWindow;
            SyncEnabled();
        }
    }

    // ================================================================ 그리드
    internal sealed class GridPage : SettingsPage
    {
        private readonly CheckBox _badges, _labels;
        private readonly NumericUpDown _lines, _font;

        public override string Id => "grid";
        public override string Title => LT("Grid", "그리드");

        public GridPage(Form1 host, ThemePalette p) : base(host, p)
        {
            Heading(() => LT("Column headers", "열 머리글"));
            _badges = Check(() => LT("Show column type badges", "열 타입 배지 표시"));
            _labels = Check(() => LT("Show variable/value labels for SPSS and SAS files", "SPSS·SAS 파일의 변수/값 라벨 표시"));
            Note(() => LT("Turning field labels on or off re-reads the SPSS/SAS file that is open.", "필드 라벨을 켜거나 끄면 열려 있는 SPSS·SAS 파일을 다시 읽습니다."));
            Heading(() => LT("Cells", "셀"));
            _lines = Number(() => LT("Max lines per cell", "셀 최대 줄 수"), 1, 20);
            _font = Number(() => LT("Font size (pt, 0 = default)", "글꼴 크기 (pt, 0 = 기본)"), 0, 24);
            Note(() => LT("Cells with line breaks grow up to the maximum number of lines. A font size of 0 uses the window font.",
                "줄바꿈이 있는 셀은 최대 줄 수까지 높아집니다. 글꼴 크기 0은 창 기본 글꼴을 씁니다."));
        }

        public override void LoadValues()
        {
            _badges.Checked = S.ShowTypeBadges;
            _labels.Checked = Host.FieldLabelsOn;
            _lines.Value = Math.Clamp(S.MaxCellLines, 1, 20);
            _font.Value = (decimal)Math.Clamp((int)Math.Round(S.GridFontSize), 0, 24);
        }

        public override bool Commit()
        {
            Host.ApplyGridSettings(_badges.Checked, _labels.Checked, (int)_lines.Value, (float)_font.Value);
            return true;
        }

        public override void ResetDefaults()
        {
            var d = new AppSettings();
            _badges.Checked = d.ShowTypeBadges;
            _labels.Checked = d.ShowFieldLabels;
            _lines.Value = d.MaxCellLines;
            _font.Value = 0;
        }
    }

    // ================================================================ 파일과 데이터
    internal sealed class FilesPage : SettingsPage
    {
        private readonly ComboBox _encoding;
        private readonly CheckBox _deleteIndex;
        private readonly NumericUpDown _recent;
        private readonly ComboBox _memMode;
        private readonly NumericUpDown _memGb;
        private readonly Label _memInfo, _memWarn;
        private bool _memLoading;
        private double _lastManualGb;

        public override string Id => "files";
        public override string Title => LT("Files & Data", "파일과 데이터");

        public FilesPage(Form1 host, ThemePalette p) : base(host, p)
        {
            Heading(() => LT("Opening files", "파일 열기"));
            _encoding = Combo(() => LT("Default encoding", "기본 인코딩"),
                () => LT("Auto-detect", "자동 감지"), () => EncodingDetector.Utf8, () => EncodingDetector.Cp949);
            Note(() => LT("Used for UTF-8/CP949 text files without a byte-order mark instead of the detected encoding. You can still change the encoding of an open file from the View menu or the status bar.",
                "BOM이 없는 UTF-8/CP949 텍스트 파일에서 감지 결과 대신 쓰는 인코딩입니다. 열린 파일의 인코딩은 언제든 보기 메뉴나 상태 표시줄에서 바꿀 수 있습니다."));
            Heading(() => LT("Index cache", "인덱스 캐시"));
            _deleteIndex = Check(() => LT("Delete a file's index cache when it is closed", "파일을 닫을 때 그 파일의 인덱스 캐시 삭제"));
            Note(() => LT("The index cache makes reopening a large file fast. Turn this on to leave no cache files behind.",
                "인덱스 캐시는 큰 파일을 다시 열 때 빠르게 해 줍니다. 캐시 파일을 남기지 않으려면 켜세요."));
            Heading(() => LT("Analysis memory", "분석 메모리"));
            _memMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 190,
                BackColor = P.Surface, ForeColor = P.Text, Margin = new Padding(0, 0, 8, 0),
            };
            _memGb = new NumericUpDown
            {
                DecimalPlaces = 1, Increment = 0.5m, Minimum = (decimal)AnalysisMemoryBudget.MinimumManualGb, Maximum = MaxManualGb(),
                Width = 80, BackColor = P.Surface, ForeColor = P.Text, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 6, 0),
            };
            var unit = new Label { AutoSize = true, Text = "GB", ForeColor = P.Text, Margin = new Padding(0, 4, 0, 0) };
            var memRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = P.Window, Margin = new Padding(0) };
            memRow.Controls.Add(_memMode);
            memRow.Controls.Add(_memGb);
            memRow.Controls.Add(unit);
            LabeledRow(() => LT("Analysis memory cap", "분석 메모리 상한"), memRow);
            Relabel(() =>
            {
                int index = _memMode.SelectedIndex;
                _memMode.BeginUpdate();
                _memMode.Items.Clear();
                _memMode.Items.Add(LT("Auto (50% of memory)", "자동 (메모리의 50%)"));
                _memMode.Items.Add(LT("Manual", "직접 지정"));
                _memMode.EndUpdate();
                SelectIndex(_memMode, index);
            });
            _memInfo = Note(MemoryInfoText);
            _memWarn = Note(MemoryWarningText);
            _memWarn.ForeColor = Color.FromArgb(205, 120, 20);
            Note(() => LT("Applies to the in-memory analyses: basic statistics, charts, group-by, duplicates, chi-square, pivot, data-quality reference sets and the advanced statistics. Auto uses 50% of this PC's memory (at least 512 MB, no upper limit). DuckDB workspace queries have their own limit and are not affected.",
                "메모리 안에서 계산하는 분석(기본 통계·차트·그룹별 집계·중복 찾기·카이제곱·피벗·품질 참조 집합·고급 통계)에 적용됩니다. 자동은 이 PC 메모리의 50%(최소 512 MB, 상한 없음)입니다. DuckDB 작업 공간 질의는 별도 상한을 쓰며 영향받지 않습니다."));
            _memMode.SelectedIndexChanged += (_, _) => { if (!Relocalizing && !_memLoading) SyncMemoryUi(); };
            _memGb.ValueChanged += (_, _) =>
            {
                if (_memLoading) return;
                if (_memMode.SelectedIndex == 1) _lastManualGb = (double)_memGb.Value;
                RefreshMemoryTexts();
            };

            Heading(() => LT("Recent workspaces", "최근 작업 공간"));
            _recent = Number(() => LT("Number to remember", "기억할 개수"), 1, AppSettings.MaxRecentWorkspaces);
        }

        private static decimal MaxManualGb()
        {
            decimal max = Math.Floor((decimal)(AnalysisMemoryBudget.PhysicalBytes / AnalysisMemoryBudget.BytesPerGb) * 10m) / 10m;
            return Math.Min(Math.Max(max, (decimal)AnalysisMemoryBudget.MinimumManualGb), (decimal)AppSettings.MaxAnalysisMemoryGb);
        }

        private static decimal AutoGb()
            => Math.Clamp(Math.Round((decimal)(AnalysisMemoryBudget.ForPhysicalMemory(AnalysisMemoryBudget.PhysicalBytes) / AnalysisMemoryBudget.BytesPerGb), 1),
                (decimal)AnalysisMemoryBudget.MinimumManualGb, MaxManualGb());

        private static string Gb(long bytes) => (bytes / AnalysisMemoryBudget.BytesPerGb).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>지금 입력 칸이 뜻하는 상한(바이트).</summary>
        private long EffectiveBytes()
        {
            long physical = AnalysisMemoryBudget.PhysicalBytes;
            return _memMode.SelectedIndex == 1
                ? AnalysisMemoryBudget.ForManual((double)_memGb.Value, physical)
                : AnalysisMemoryBudget.ForPhysicalMemory(physical);
        }

        private string MemoryInfoText()
            => LT($"This PC's memory: {Gb(AnalysisMemoryBudget.PhysicalBytes)} GB, currently available: {Gb(AnalysisMemoryBudget.AvailableBytes)} GB",
                $"이 PC 메모리: {Gb(AnalysisMemoryBudget.PhysicalBytes)} GB, 현재 사용 가능: {Gb(AnalysisMemoryBudget.AvailableBytes)} GB");

        private string MemoryWarningText()
        {
            long available = AnalysisMemoryBudget.AvailableBytes;
            if (available <= 0 || EffectiveBytes() <= available) return "";
            return LT($"This cap ({Gb(EffectiveBytes())} GB) is more than the memory available right now ({Gb(available)} GB). Large analyses may page to disk and slow down, or the app may crash. You can still save it.",
                $"이 상한({Gb(EffectiveBytes())} GB)이 현재 사용 가능한 메모리({Gb(available)} GB)보다 큽니다. 큰 분석은 디스크 페이징으로 매우 느려지거나 앱이 중단될 수 있습니다. 그래도 저장할 수 있습니다.");
        }

        private void RefreshMemoryTexts()
        {
            _memInfo.Text = MemoryInfoText();
            string warning = MemoryWarningText();
            _memWarn.Text = warning;
            _memWarn.Visible = warning.Length > 0;
        }

        /// <summary>자동이면 값 칸에 자동 값을 보이고 잠근다. 직접 지정이면 마지막으로 정한 값(없으면 자동 값)을 보이고 푼다.</summary>
        private void SyncMemoryUi()
        {
            bool manual = _memMode.SelectedIndex == 1;
            bool was = _memLoading;
            _memLoading = true;
            try
            {
                _memGb.Enabled = manual;
                _memGb.Value = manual && _lastManualGb > 0
                    ? Math.Clamp((decimal)_lastManualGb, _memGb.Minimum, _memGb.Maximum)
                    : AutoGb();
                if (manual) _lastManualGb = (double)_memGb.Value;
            }
            finally { _memLoading = was; }
            RefreshMemoryTexts();
        }

        public override void LoadValues()
        {
            SelectIndex(_encoding, S.DefaultEncoding switch { EncodingDetector.Utf8 => 1, EncodingDetector.Cp949 => 2, _ => 0 });
            _deleteIndex.Checked = S.DeleteIndexOnClose;
            _recent.Value = Math.Clamp(S.RecentCount, 1, AppSettings.MaxRecentWorkspaces);
            _memLoading = true;
            try
            {
                _lastManualGb = S.AnalysisMemoryManualGb;
                SelectIndex(_memMode, S.AnalysisMemoryAuto ? 0 : 1);
            }
            finally { _memLoading = false; }
            SyncMemoryUi();
        }

        public override bool Commit()
        {
            S.DefaultEncoding = _encoding.SelectedIndex switch { 1 => EncodingDetector.Utf8, 2 => EncodingDetector.Cp949, _ => AppSettings.AutoEncoding };
            S.RecentCount = (int)_recent.Value;
            S.TrimRecent();
            if (_deleteIndex.Checked != S.DeleteIndexOnClose) Host.ApplyDeleteIndexOnClose(_deleteIndex.Checked);
            bool manual = _memMode.SelectedIndex == 1;
            if (manual) _lastManualGb = (double)_memGb.Value;
            S.AnalysisMemoryAuto = !manual;
            S.AnalysisMemoryManualGb = _lastManualGb;
            AnalysisMemoryBudget.Configure(S.AnalysisMemoryAuto, S.AnalysisMemoryManualGb);
            return true;
        }

        public override void ResetDefaults()
        {
            var d = new AppSettings();
            SelectIndex(_encoding, 0);
            _deleteIndex.Checked = d.DeleteIndexOnClose;
            _recent.Value = d.RecentCount;
            _lastManualGb = d.AnalysisMemoryManualGb;
            SelectIndex(_memMode, d.AnalysisMemoryAuto ? 0 : 1);
            SyncMemoryUi();
        }
    }

    // ================================================================ AI 에이전트
    internal sealed class AgentPage : SettingsPage
    {
        private readonly ComboBox? _scope;
        private readonly ComboBox _policy, _approval;
        private readonly NumericUpDown _maxRows;
        private readonly TextBox _omp, _extra;
        private readonly CheckBox _python;
        private readonly PythonEnvSection _pyEnv;
        private readonly SkillsSection _skills;
        internal SkillsSection Skills => _skills;
        private readonly Label _locked;
        private bool _loading;
        private (AgentApprovalMode Mode, AgentDataPolicy Policy, bool Python) _baseline;

        public override string Id => "ai";
        public override string Title => LT("AI Agent", "AI 에이전트");

        public AgentPage(Form1 host, ThemePalette p) : base(host, p)
        {
            bool hasWorkspace = host.WorkspaceFileName is not null;
            // 작업 공간 파일이 열려 있으면 승인 모드·데이터 공유·로컬 Python을 어디에 적용할지 먼저 고른다(기본: 이 작업 공간).
            if (hasWorkspace)
            {
                _scope = Combo(() => LT("Apply settings to", "설정 적용 대상"),
                    () => LT("This workspace", "이 작업 공간") + " (" + Host.WorkspaceFileName + ")",
                    () => LT("App default (all workspaces)", "앱 기본값 (모든 작업 공간)"));
                Note(() => LT(
                    "Data sharing, approval mode and local Python can be set per workspace. A workspace setting is saved in the workspace file and can only make the app default stricter: the stricter of the two always applies. To loosen a setting, choose 'App default'. The other settings are app-wide.",
                    "데이터 공유·승인 모드·로컬 Python은 작업 공간별로 정할 수 있습니다. 작업 공간 설정은 작업 공간 파일에 저장되며 앱 기본값을 더 엄격하게만 바꿀 수 있습니다. 둘 중 더 엄격한 쪽이 항상 적용됩니다. 풀려면 '앱 기본값'을 고르세요. 나머지 설정은 앱 전체에 적용됩니다."));
            }
            _policy = Combo(() => LT("Data sharing with the AI", "AI와의 데이터 공유"),
                () => LT("Summary only (no raw rows)", "요약만 (원시 행 보내지 않음)"),
                () => LT("Rows with approval", "행 값 — 요청마다 승인"),
                () => LT("Rows allowed (up to the limit)", "행 값 — 승인 없이(상한까지)"));
            _maxRows = Number(() => LT("Row limit per request", "요청당 행 상한"), 1, 5000);
            _omp = Text(() => LT("omp path (blank = auto)", "omp 경로 (비우면 자동)"));
            _extra = Text(() => LT("Extra omp arguments", "omp 추가 인자"));
            ActionButton(() => LT("AI Setup Assistant…", "AI 환경 설정 도우미…"), () =>
            {
                Host.ShowAiSetupAssistant(modal: true, owner: FindForm());
                _omp.Text = S.AgentOmpPath ?? "";   // 도우미가 omp 경로를 바꿨을 수 있다
            });
            _approval = Combo(() => LT("Approval mode", "승인 모드"),
                () => Enum.GetValues<AgentApprovalMode>().Select(m => ApprovalTexts.Label(m, Loc.CurrentLanguage == "ko")).ToArray());
            // omp 추가 인자(--approval-mode·--yolo·--auto-approve)가 모드를 고정하면 선택을 막고 이유를 보여 준다.
            _locked = Note(() => AgentApprovalPolicy.ForcedByArgs(S.AgentExtraArgs) is { } forced ? ApprovalTexts.Locked(forced.Flag, Loc.CurrentLanguage == "ko") : "");
            _python = Check(() => LT("Allow local Python analysis", "로컬 Python 분석 허용"));
            // 적용 대상을 바꾸면 세 항목이 그 범위의 값을 보여 준다.
            if (_scope is not null)
                _scope.SelectedIndexChanged += (_, _) =>
                {
                    if (_loading || Relocalizing || _scope.SelectedIndex < 0) return;
                    ShowBaseline(Host.ScopeBaseline(_scope.SelectedIndex == 0));
                };
            Note(() => LT(
                "When on, the agent may export the current view to a file in the analysis folder (<workspace name>_분석결과 next to the workspace file, or <first file name>_분석결과 next to the first data file you opened; it stays the same when you switch tabs) and run Python on it (omp's eval tool, needs Python 3.10+). Everything a script prints is read by the AI model; with 'Summary only' the agent is told to print aggregates only, but that cannot be fully enforced for code it writes. The first Python run of each conversation asks for your approval.",
                "켜면 에이전트가 현재 보기를 분석 폴더(작업 공간 파일 옆의 <작업 공간 이름>_분석결과, 없으면 처음 연 데이터 파일 옆의 <파일 이름>_분석결과 — 탭을 바꿔도 그대로)에 파일로 내보내 Python(omp eval 도구, Python 3.10 이상 필요)으로 분석할 수 있습니다. 스크립트가 출력하는 모든 내용은 AI 모델이 읽습니다. '요약만'이면 집계만 출력하라고 지시하지만, 에이전트가 쓰는 코드에는 완전히 강제할 수 없습니다. 대화마다 첫 Python 실행은 승인을 묻습니다."));
            _pyEnv = new PythonEnvSection(host, p);
            AddWide(_pyEnv);
            Relabel(() => _pyEnv.Relocalize());
            _skills = new SkillsSection(P, PageWidth);
            AddWide(_skills);
            Relabel(() => _skills.Relocalize());
            _python.CheckedChanged += (_, _) => SyncSkillsContext();
            _policy.SelectedIndexChanged += (_, _) => SyncSkillsContext();
            Note(() => LT(
                "Approval mode: 'Always ask' asks for every write or run, in the app and in omp. 'Auto-approve edits' lets undoable data edits run without a card and lets omp write files, but still asks for Python/shell runs, saving to a new file and sharing raw rows. 'Allow everything' also skips those (raw-row sharing still follows the data sharing setting). Changing it restarts the agent on the same conversation.",
                "승인 모드: '항상 묻기'는 앱과 omp 모두 쓰기·실행마다 묻습니다. '편집 자동 승인'은 되돌릴 수 있는 데이터 편집을 카드 없이 실행하고 omp의 파일 쓰기도 허용하지만 Python·셸 실행, 새 파일 저장, 원시 행 공유는 묻습니다. '모두 허용'은 그것들도 묻지 않습니다(원시 행 공유는 데이터 공유 설정을 따름). 바꾸면 같은 대화로 에이전트를 다시 시작합니다."));
            Note(() => LT(
                "The agent is omp (oh-my-pi), which uses the models you configured in omp. 'Summary only' sends the schema, aggregates and analysis results, never raw cell values. Edits are stored in an undoable overlay; the original file is never written. omp starts when you send your first message.",
                "에이전트는 omp(oh-my-pi)이며 omp에 설정한 모델을 씁니다. '요약만'은 스키마·집계·분석 결과만 보내고 원시 셀 값은 보내지 않습니다. 편집은 되돌릴 수 있는 덮개에 쌓이고 원본 파일은 쓰지 않습니다. omp는 첫 메시지를 보낼 때 시작합니다."));
            // 채팅 모델 선택기의 "최근 사용" 그룹이 쓰는 omp 기록을 읽을 수 있는지(읽기만 하는 상태 줄).
            Note(() => Host.AgentModelUsageStatus());
        }

        private bool WorkspaceScope => _scope is not null && _scope.SelectedIndex == 0;

        private void ShowBaseline((AgentApprovalMode Mode, AgentDataPolicy Policy, bool Python) b)
        {
            _baseline = b;
            SelectIndex(_policy, (int)b.Policy);
            if (_approval.Enabled) SelectIndex(_approval, (int)b.Mode);
            _python.Checked = b.Python;
        }

        public override void LoadValues()
        {
            _loading = true;
            try
            {
                if (_scope is not null) SelectIndex(_scope, 0);
                _maxRows.Value = Math.Clamp(S.AgentMaxRows, 1, 5000);
                _omp.Text = S.AgentOmpPath ?? "";
                _extra.Text = S.AgentExtraArgs ?? "";
                var forced = AgentApprovalPolicy.ForcedByArgs(S.AgentExtraArgs);
                _approval.Enabled = forced is null;
                ShowBaseline(Host.ScopeBaseline(WorkspaceScope));
                if (forced is { } f) SelectIndex(_approval, (int)f.Mode);
                Relocalize();   // 고정 안내 글자
                _pyEnv.Reload();
                _skills.Load(S);
                SyncSkillsContext();
            }
            finally { _loading = false; }
        }

        /// <summary>분석 스킬 구역에 로컬 Python 체크와 데이터 공유 선택을 알린다('요약만' 경고·Python 꺼짐 안내).</summary>
        private void SyncSkillsContext() =>
            _skills.SetContext(_python.Checked, (AgentDataPolicy)Math.Clamp(_policy.SelectedIndex, 0, 2));

        /// <summary>칸의 값이 불러온 값과 다른가. 건드리지 않은 쪽은 적용하지 않는다(확인 대화 상자·재시작이 괜히 뜨지 않도록).</summary>
        internal bool IsDirty =>
            (AgentDataPolicy)Math.Clamp(_policy.SelectedIndex, 0, 2) != _baseline.Policy
            || (_approval.Enabled && (AgentApprovalMode)Math.Clamp(_approval.SelectedIndex, 0, 2) != _baseline.Mode)
            || _python.Checked != _baseline.Python
            || (int)_maxRows.Value != Math.Clamp(S.AgentMaxRows, 1, 5000)
            || _omp.Text.Trim() != (S.AgentOmpPath ?? "").Trim()
            || _extra.Text.Trim() != (S.AgentExtraArgs ?? "").Trim();

        public override bool Commit()
        {
            // 스킬 선택은 설정 개체에 먼저 쓰고, 다른 값이 바뀌었으면 ApplyAgentSettings가 함께 저장·반영한다(다시 시작이 한 번만 일어나도록).
            bool skillsDirty = _skills.IsDirty;
            if (skillsDirty) _skills.WriteTo(S);
            _pyEnv.Commit();
            if (!IsDirty)
            {
                if (skillsDirty) Host.ApplyAgentSkillSettings();
                return true;
            }
            Host.ApplyAgentSettings(new Form1.AgentSettingsInput(
                WorkspaceScope,
                (AgentApprovalMode)Math.Clamp(_approval.SelectedIndex, 0, 2),
                (AgentDataPolicy)Math.Clamp(_policy.SelectedIndex, 0, 2),
                _python.Checked,
                _approval.Enabled,
                (int)_maxRows.Value,
                _omp.Text,
                _extra.Text));
            return true;
        }

        public override void ResetDefaults()
        {
            var d = new AppSettings();
            SelectIndex(_policy, (int)(Enum.TryParse<AgentDataPolicy>(d.AgentDataPolicy, out var pol) ? pol : AgentDataPolicy.SummaryOnly));
            _maxRows.Value = d.AgentMaxRows;
            _omp.Text = "";
            _extra.Text = "";
            if (_approval.Enabled) SelectIndex(_approval, (int)AgentApprovalPolicy.Parse(d.AgentApprovalMode));
            _python.Checked = d.AgentAllowLocalPython;
            _pyEnv.ResetDefaults();
            _skills.ResetDefaults();
        }
    }

    // ================================================================ 단축키
    internal sealed class ShortcutsPage : SettingsPage
    {
        private readonly DataGridView _grid;

        public override string Id => "shortcuts";
        public override string Title => LT("Shortcuts", "단축키");
        public override bool CanReset => false;

        public ShortcutsPage(Form1 host, ThemePalette p) : base(host, p)
        {
            Note(() => LT("These keyboard shortcuts are built in and cannot be changed in this version.",
                "이 버전의 키보드 단축키는 고정되어 있으며 바꿀 수 없습니다."));
            _grid = new DataGridView
            {
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                BorderStyle = BorderStyle.FixedSingle,
                Width = PageWidth,
                Height = 440,
                EnableHeadersVisualStyles = false,
                BackgroundColor = p.GridBg,
                GridColor = p.Border,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            };
            _grid.DefaultCellStyle.BackColor = p.GridBg;
            _grid.DefaultCellStyle.ForeColor = p.Text;
            _grid.DefaultCellStyle.SelectionBackColor = p.SelectionBg;
            _grid.DefaultCellStyle.SelectionForeColor = p.SelectionText;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = p.AltRow;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = p.HeaderBg;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = p.HeaderText;
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.HeaderBg;
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.HeaderText;
            _grid.Columns.Add("command", "");
            _grid.Columns.Add("keys", "");
            _grid.Columns[0].FillWeight = 60;
            _grid.Columns[1].FillWeight = 40;
            AddWide(_grid);
            Relabel(Fill);
        }

        /// <summary>쪽에 보이는 (명령, 단축키) 목록 — 단축키 표의 모든 항목.</summary>
        internal IReadOnlyList<(string Command, string Keys)> Rows() =>
            CommandShortcuts.All.Select(e =>
            {
                string keys = e.KeyText;
                if (e.Alternates is { Length: > 0 } alt) keys += " / " + string.Join(" / ", alt.Select(CommandShortcuts.Display));
                return (e.Name, keys);
            }).ToList();

        private void Fill()
        {
            _grid.Columns[0].HeaderText = LT("Command", "명령");
            _grid.Columns[1].HeaderText = LT("Shortcut", "단축키");
            _grid.Rows.Clear();
            foreach (var (command, keys) in Rows()) _grid.Rows.Add(command, keys);
            _grid.ClearSelection();
        }

        public override void LoadValues() { }
        public override bool Commit() => true;
        public override void ResetDefaults() { }
    }
}
