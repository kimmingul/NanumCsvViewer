using System.ComponentModel;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>마법사가 돌려주는 결과: 편집됐을 수 있는 최종 SQL, 뷰 이름 제안, 한 줄 요약.</summary>
    internal sealed record WizardResult(string Sql, string SuggestedViewName, string Summary);

    internal enum DiagLevel { Normal, Good, Warn, Bad, Strong }

    internal sealed record DiagLine(string Text, DiagLevel Level = DiagLevel.Normal);

    /// <summary>콤보 상자 항목: 표 또는 뷰.</summary>
    internal sealed class RelationItem(IWorkspaceRelation relation)
    {
        public IWorkspaceRelation Relation { get; } = relation;
        public override string ToString() => Relation is WorkspaceView ? Relation.DisplayName + ViewerSupport.LT("  (view)", "  (뷰)") : Relation.DisplayName;
    }

    internal static class WizardStyle
    {
        public static IReadOnlyList<IWorkspaceRelation> Relations(DataWorkspace ws)
            => ws.Sources.SelectMany(s => s.Tables).Cast<IWorkspaceRelation>()
                .Concat(ws.Views.Where(v => v.Error is null && v.Columns.Count > 0)).ToList();

        public static Color LevelColor(ThemePalette p, DiagLevel level)
        {
            bool dark = ViewerSupport.IsDark(p);
            return level switch
            {
                DiagLevel.Good => dark ? Color.FromArgb(110, 205, 130) : Color.FromArgb(30, 135, 60),
                DiagLevel.Warn => dark ? Color.FromArgb(235, 175, 70) : Color.FromArgb(185, 105, 0),
                DiagLevel.Bad => dark ? Color.FromArgb(245, 120, 120) : Color.FromArgb(200, 50, 50),
                DiagLevel.Strong => dark ? Color.FromArgb(255, 90, 90) : Color.FromArgb(170, 0, 0),
                _ => p.Text,
            };
        }

        public static void Apply(Control root, ThemePalette p)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case DataGridView g: StyleGrid(g, p); break;
                    case Button b:
                        b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
                        b.BackColor = p.Surface; b.ForeColor = p.Text; b.FlatAppearance.BorderColor = p.Border;
                        break;
                    case TextBox or RichTextBox or ComboBox or ListBox or CheckedListBox or NumericUpDown:
                        c.BackColor = p.Surface; c.ForeColor = p.Text;
                        if (c is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case SplitContainer sc:
                        sc.BackColor = p.Border; sc.Panel1.BackColor = p.Window; sc.Panel2.BackColor = p.Window;
                        break;
                    case ProgressBar: break;
                    default: c.BackColor = p.Window; c.ForeColor = p.Text; break;
                }
                if (c.HasChildren) Apply(c, p);
            }
        }

        public static void StyleGrid(DataGridView g, ThemePalette p)
        {
            g.EnableHeadersVisualStyles = false;
            g.BackgroundColor = p.GridBg;
            g.GridColor = p.Border;
            g.BorderStyle = BorderStyle.None;
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
        }

        /// <summary>두 컬럼 형이 조인·비교에서 그대로 비교되는가(JoinSql과 같은 규칙).</summary>
        public static bool TypesCompatible(WorkspaceColumn a, WorkspaceColumn b)
        {
            static bool Num(string t) => TypedColumnSql.FromSqlType(t) is Csv.ColumnValueType.Integer or Csv.ColumnValueType.Float;
            static bool Tmp(string t) => TypedColumnSql.FromSqlType(t) is Csv.ColumnValueType.Date or Csv.ColumnValueType.DateTime;
            return string.Equals(a.SqlType, b.SqlType, StringComparison.OrdinalIgnoreCase) || (Num(a.SqlType) && Num(b.SqlType)) || (Tmp(a.SqlType) && Tmp(b.SqlType));
        }
    }

    /// <summary>
    /// 마법사 공통 뼈대: 위쪽 옵션 영역(하위 클래스가 채움), 진단 상자, 편집 가능한 생성 SQL, 미리보기 표(앞 50행), 상태·진행·중지.
    /// 옵션이 바뀌면 하위 클래스가 <see cref="Changed"/>를 부른다 → SQL을 다시 만들고(손으로 고치지 않았다면 반영) 잠시 뒤 진단을 비동기로 돌린다.
    /// 모든 DuckDB 작업은 취소할 수 있고(중지 버튼·닫기) UI 스레드를 막지 않는다. 원본 파일은 읽기만 한다.
    /// </summary>
    internal abstract class WizardForm : Form
    {
        public const int PreviewRows = 50;

        protected static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        protected readonly DataWorkspace Workspace;
        protected readonly ThemePalette Palette;

        private readonly SplitContainer _outer, _inner, _left;
        private readonly Panel _options;
        private readonly RichTextBox _diag;
        private readonly TextBox _sql;
        private readonly Button _btnPreview, _btnStop, _btnOk, _btnCancel, _btnReset;
        private readonly Label _status, _sqlNote, _previewNote;
        private readonly ProgressBar _progress;
        private readonly DataGridView _grid;
        private readonly System.Windows.Forms.Timer _diagTimer;
        private readonly int _optionsHeight;
        private CancellationTokenSource? _diagCts, _previewCts;
        private bool _ready, _sqlEdited, _settingSql, _statusIsProblem;
        private string? _problem;
        private int _busy;

        /// <summary>확인을 누르면 채워진다.</summary>
        public WizardResult? Result { get; private set; }

        /// <summary>옵션 영역(하위 클래스가 컨트롤을 넣는다).</summary>
        protected Panel OptionsHost => _options;

        /// <summary>사용자가 SQL을 직접 고쳤는가.</summary>
        protected bool IsSqlEdited => _sqlEdited;

        protected string CurrentSql => _sql.Text;

        protected WizardForm(ThemePalette palette, DataWorkspace ws, string title, string okText, int optionsHeight)
        {
            Palette = palette;
            Workspace = ws;
            _optionsHeight = optionsHeight;

            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(1120, 800);
            MinimumSize = new Size(900, 640);
            Padding = new Padding(10);

            _options = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Name = "wizardOptions" };

            _diag = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, DetectUrls = false, Name = "wizardDiagnostics",
                ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false,
            };
            var diagPanel = new Panel { Dock = DockStyle.Fill };
            diagPanel.Controls.Add(_diag);
            diagPanel.Controls.Add(Heading(LT("Check", "진단")));

            _sql = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, AcceptsTab = true, WordWrap = false, ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.FixedSingle, Name = "wizardSql",
            };
            _btnReset = new Button { Text = LT("Reset to generated", "생성 SQL로 되돌리기"), Dock = DockStyle.Right, Width = 150, Visible = false, Name = "wizardResetSql" };
            _sqlNote = new Label { Dock = DockStyle.Right, AutoSize = true, Visible = false, TextAlign = ContentAlignment.MiddleRight, Name = "wizardSqlNote" };
            var sqlHead = Heading(LT("SQL (you can edit it)", "SQL (직접 고칠 수 있습니다)"));
            var sqlHeadRow = new Panel { Dock = DockStyle.Top, Height = 26 };
            sqlHeadRow.Controls.Add(sqlHead);
            sqlHeadRow.Controls.Add(_sqlNote);
            sqlHeadRow.Controls.Add(_btnReset);
            sqlHead.Dock = DockStyle.Fill;
            var sqlPanel = new Panel { Dock = DockStyle.Fill };
            sqlPanel.Controls.Add(_sql);
            sqlPanel.Controls.Add(sqlHeadRow);

            _left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
            _left.Panel1.Controls.Add(diagPanel);
            _left.Panel2.Controls.Add(sqlPanel);

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, Name = "wizardPreviewGrid",
            };
            _previewNote = new Label { Dock = DockStyle.Right, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Name = "wizardPreviewNote" };
            var previewHead = Heading(LT("Preview (first 50 rows)", "미리보기 (앞 50행)"));
            previewHead.Dock = DockStyle.Fill;
            var previewHeadRow = new Panel { Dock = DockStyle.Top, Height = 26 };
            previewHeadRow.Controls.Add(previewHead);
            previewHeadRow.Controls.Add(_previewNote);
            var previewPanel = new Panel { Dock = DockStyle.Fill };
            previewPanel.Controls.Add(_grid);
            previewPanel.Controls.Add(previewHeadRow);

            _inner = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
            _inner.Panel1.Controls.Add(_left);
            _inner.Panel2.Controls.Add(previewPanel);

            _outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, FixedPanel = FixedPanel.Panel1 };
            _outer.Panel1.Controls.Add(_options);
            _outer.Panel2.Controls.Add(_inner);

            _status = new Label { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Name = "wizardStatus" };
            _progress = new ProgressBar { Width = 110, Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false, Margin = new Padding(6, 8, 0, 0), Name = "wizardProgress" };
            _btnPreview = new Button { Text = LT("Preview (F5)", "미리보기 (F5)"), Size = new Size(120, 30), Name = "wizardPreview" };
            _btnStop = new Button { Text = LT("Stop", "중지"), Size = new Size(76, 30), Enabled = false, Name = "wizardStop" };
            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
            leftButtons.Controls.AddRange(new Control[] { _btnPreview, _btnStop, _progress });
            _btnCancel = new Button { Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(96, 30), Name = "wizardCancel" };
            _btnOk = new Button { Text = okText, Size = new Size(150, 30), Enabled = false, Name = "wizardOk" };
            var rightButtons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            rightButtons.Controls.AddRange(new Control[] { _btnCancel, _btnOk });
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(0, 6, 0, 0) };
            bar.Controls.Add(rightButtons);
            bar.Controls.Add(leftButtons);

            Controls.Add(_outer);
            Controls.Add(_status);
            Controls.Add(bar);
            CancelButton = _btnCancel;

            _diagTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _diagTimer.Tick += (_, _) => { _diagTimer.Stop(); _ = RunDiagnosticsAsync(); };

            _sql.TextChanged += (_, _) =>
            {
                if (_settingSql) return;
                _sqlEdited = true;
                SyncSqlUi();
            };
            _btnReset.Click += (_, _) => { _sqlEdited = false; Regenerate(); };
            _btnPreview.Click += (_, _) => _ = PreviewAsync();
            _btnStop.Click += (_, _) => CancelRunning();
            _btnOk.Click += (_, _) => Accept();
            _btnCancel.Click += (_, _) => CancelRunning();
            _grid.DataError += (_, e) => e.ThrowException = false;
        }

        private Label Heading(string text) => new()
        {
            Text = text, Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold), Name = "wizardHeading",
        };

        // ---- 하위 클래스가 구현 ---------------------------------------------------------------------

        /// <summary>현재 옵션으로 SQL을 만든다. 옵션이 불완전하면 사용자에게 보일 이유를 담은 <see cref="ArgumentException"/>.</summary>
        protected abstract string BuildSql();

        protected abstract string SuggestViewName();

        protected abstract string Summarize();

        /// <summary>진단(비동기·취소 가능). UI 상태는 첫 await 전에 읽는다. 기본은 없음.</summary>
        protected virtual Task<IReadOnlyList<DiagLine>> DiagnoseAsync(string sql, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiagLine>>(Array.Empty<DiagLine>());

        // ---- 수명 ---------------------------------------------------------------------------------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            WizardStyle.Apply(this, Palette);
            try
            {
                _outer.Panel1MinSize = 100;
                _outer.Panel2MinSize = 220;
                _outer.SplitterDistance = Math.Max(100, Math.Min(_optionsHeight, _outer.Height - 260));
                _inner.Panel1MinSize = 280;
                _inner.Panel2MinSize = 200;
                _inner.SplitterDistance = Math.Max(280, (int)(_inner.Width * 0.46));
                _left.Panel1MinSize = 60;
                _left.Panel2MinSize = 80;
                _left.SplitterDistance = Math.Max(60, (int)(_left.Height * 0.42));
            }
            catch (ArgumentException) { /* 창이 너무 작음 — 기본 분할 */ }
            _ready = true;
            Regenerate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { if (_btnPreview.Enabled) _ = PreviewAsync(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _diagTimer.Stop();
            CancelRunning();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _diagTimer.Dispose(); _diagCts?.Dispose(); _previewCts?.Dispose(); }
            base.Dispose(disposing);
        }

        // ---- SQL 재생성 · 진단 ----------------------------------------------------------------------

        /// <summary>옵션이 바뀌었다.</summary>
        protected void Changed()
        {
            if (_ready && !IsDisposed) Regenerate();
        }

        private void Regenerate()
        {
            string? sql = null;
            _problem = null;
            try { sql = BuildSql(); }
            catch (ArgumentException ex) { _problem = ex.Message; }

            if (!_sqlEdited)
            {
                _settingSql = true;
                try { _sql.Text = (sql ?? "").ReplaceLineEndings("\r\n"); } // 여러 줄 텍스트 상자는 \r\n으로 줄을 바꾼다
                finally { _settingSql = false; }
            }
            SyncSqlUi();

            _diagTimer.Stop();
            if (_problem is null) _diagTimer.Start();
            else
            {
                _diagCts?.Cancel();
                ShowDiag(new[] { new DiagLine(_problem, DiagLevel.Warn) });
            }
        }

        private void SyncSqlUi()
        {
            _btnReset.Visible = _sqlEdited;
            _sqlNote.Visible = _sqlEdited;
            _sqlNote.Text = LT("Edited by hand — option changes no longer update it.  ", "직접 고쳤습니다 — 옵션을 바꿔도 SQL은 그대로입니다.  ");
            bool hasSql = !string.IsNullOrWhiteSpace(_sql.Text);
            bool canRun = hasSql && (_sqlEdited || _problem is null);
            _btnPreview.Enabled = canRun;
            _btnOk.Enabled = canRun;
            bool showProblem = _problem is not null && !_sqlEdited;
            if (showProblem) SetStatus(_problem!, DiagLevel.Warn);
            else if (_statusIsProblem) SetStatus("", DiagLevel.Normal);
            _statusIsProblem = showProblem;
        }

        protected void SetStatus(string text, DiagLevel level)
        {
            _status.Text = text;
            _status.ForeColor = WizardStyle.LevelColor(Palette, level);
            _statusIsProblem = false;
        }

        private void ShowDiag(IReadOnlyList<DiagLine> lines)
        {
            if (IsDisposed) return;
            _diag.SuspendLayout();
            _diag.Clear();
            var baseFont = _diag.Font;
            using var bold = new Font(baseFont, FontStyle.Bold);
            foreach (var l in lines)
            {
                _diag.SelectionStart = _diag.TextLength;
                _diag.SelectionLength = 0;
                _diag.SelectionColor = WizardStyle.LevelColor(Palette, l.Level);
                _diag.SelectionFont = l.Level == DiagLevel.Strong ? bold : baseFont;
                _diag.AppendText(l.Text + "\n");
            }
            _diag.SelectionStart = 0;
            _diag.ScrollToCaret();
            _diag.ResumeLayout();
        }

        private async Task RunDiagnosticsAsync()
        {
            if (IsDisposed || _problem is not null) return;
            _diagCts?.Cancel();
            var cts = _diagCts = new CancellationTokenSource();
            string sql = _sql.Text;
            BeginBusy();
            ShowDiag(new[] { new DiagLine(LT("Checking…", "확인하는 중…")) });
            try
            {
                var lines = await DiagnoseAsync(sql, cts.Token);
                if (!cts.IsCancellationRequested) ShowDiag(lines);
            }
            catch (OperationCanceledException) { /* 새 진단이 대신함 */ }
            catch (WorkspaceQueryException ex) { if (!cts.IsCancellationRequested) ShowDiag(new[] { new DiagLine(ex.Message, DiagLevel.Bad) }); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                if (!cts.IsCancellationRequested) ShowDiag(new[] { new DiagLine(ex.Message, DiagLevel.Bad) });
            }
            finally { EndBusy(); }
        }

        // ---- 미리보기 ------------------------------------------------------------------------------

        private async Task PreviewAsync()
        {
            string sql = _sql.Text;
            if (string.IsNullOrWhiteSpace(sql)) return;
            _previewCts?.Cancel();
            var cts = _previewCts = new CancellationTokenSource();
            BeginBusy();
            SetStatus(LT("Running preview…", "미리보기를 실행하는 중…"), DiagLevel.Normal);
            try
            {
                var p = await Workspace.PreviewAsync(sql, PreviewRows, cts.Token);
                if (cts.IsCancellationRequested || IsDisposed) return;
                ShowPreview(p);
                SetStatus(LT($"Preview: {p.Rows.Count:N0} rows{(p.Truncated ? " (more rows exist)" : "")} · {p.Elapsed.TotalSeconds:0.0}s",
                             $"미리보기: {p.Rows.Count:N0}행{(p.Truncated ? " (더 있음)" : "")} · {p.Elapsed.TotalSeconds:0.0}초"), DiagLevel.Normal);
            }
            catch (OperationCanceledException) { if (ReferenceEquals(_previewCts, cts) && !IsDisposed) SetStatus(LT("Preview cancelled.", "미리보기를 취소했습니다."), DiagLevel.Normal); }
            catch (WorkspaceQueryException ex) { if (!cts.IsCancellationRequested && !IsDisposed) SetStatus(ex.Message, DiagLevel.Bad); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                if (!cts.IsCancellationRequested && !IsDisposed) SetStatus(ex.Message, DiagLevel.Bad);
            }
            finally { EndBusy(); }
        }

        private void ShowPreview(QueryPreview p)
        {
            _grid.SuspendLayout();
            _grid.Columns.Clear();
            _grid.Rows.Clear();
            for (int i = 0; i < p.Columns.Count; i++)
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    Name = "c" + i, HeaderText = p.Columns[i].Name, ToolTipText = p.Columns[i].SqlType, SortMode = DataGridViewColumnSortMode.NotSortable,
                    MinimumWidth = 40,
                });
            var grey = Color.Gray;
            foreach (var row in p.Rows)
            {
                var cells = new object[row.Length];
                for (int i = 0; i < row.Length; i++) cells[i] = row[i] ?? "NULL";
                int idx = _grid.Rows.Add(cells);
                for (int i = 0; i < row.Length; i++)
                    if (row[i] is null) _grid.Rows[idx].Cells[i].Style.ForeColor = grey;
            }
            _grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            foreach (DataGridViewColumn c in _grid.Columns) c.Width = Math.Min(c.Width, 320);
            _grid.ResumeLayout();
            _previewNote.Text = p.Columns.Count == 0 ? "" : LT($"{p.Columns.Count:N0} columns", $"{p.Columns.Count:N0}개 컬럼");
        }

        // ---- 작업 상태 ------------------------------------------------------------------------------

        private void BeginBusy()
        {
            _busy++;
            _progress.Visible = true;
            _progress.MarqueeAnimationSpeed = 30;
            _btnStop.Enabled = true;
        }

        private void EndBusy()
        {
            if (IsDisposed) return;
            if (--_busy > 0) return;
            _progress.MarqueeAnimationSpeed = 0;
            _progress.Visible = false;
            _btnStop.Enabled = false;
        }

        private void CancelRunning()
        {
            _diagTimer.Stop();
            _diagCts?.Cancel();
            _previewCts?.Cancel();
        }

        // ---- 확인 ----------------------------------------------------------------------------------

        private void Accept()
        {
            string sql = _sql.Text.Trim().ReplaceLineEndings("\n");
            if (sql.Length == 0) return;
            SqlAnalysis analysis;
            try { analysis = Workspace.Analyze(sql); }
            catch (Exception ex) when (ex is WorkspaceQueryException or InvalidOperationException or ObjectDisposedException)
            {
                SetStatus(ex.Message, DiagLevel.Bad);
                return;
            }
            if (!analysis.IsValid)
            {
                string where = analysis.Line is null ? "" : LT($" (line {analysis.Line})", $" ({analysis.Line}번째 줄)");
                SetStatus(analysis.Error + where, DiagLevel.Bad);
                return;
            }
            string summary;
            try { summary = Summarize(); }
            catch (ArgumentException) { summary = ""; }
            if (_sqlEdited) summary += LT(" (SQL edited by hand)", " (SQL을 직접 고침)");
            Result = new WizardResult(sql, SuggestViewName(), summary);
            CancelRunning();
            DialogResult = DialogResult.OK;
        }

        // ---- 하위 클래스 도우미 ----------------------------------------------------------------------

        protected Label MakeLabel(string text, int width = 0) => new()
        {
            Text = text, AutoSize = width == 0, Width = width, TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0),
        };

        protected ComboBox MakeRelationCombo(IReadOnlyList<IWorkspaceRelation> relations, string name, int width = 220)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Name = name, Margin = new Padding(0, 3, 12, 0) };
            foreach (var r in relations) combo.Items.Add(new RelationItem(r));
            return combo;
        }

        protected static IWorkspaceRelation? Selected(ComboBox combo) => (combo.SelectedItem as RelationItem)?.Relation;

        protected static void SelectRelation(ComboBox combo, IWorkspaceRelation? relation)
        {
            if (relation is null) return;
            for (int i = 0; i < combo.Items.Count; i++)
                if (ReferenceEquals(((RelationItem)combo.Items[i]!).Relation, relation)) { combo.SelectedIndex = i; return; }
        }

        /// <summary>표시 이름으로 미리 고를 표.</summary>
        protected static IWorkspaceRelation? FindByName(IReadOnlyList<IWorkspaceRelation> relations, string? name)
            => name is null ? null : relations.FirstOrDefault(r => string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase));

        protected static string N(long value) => value.ToString("N0");

        protected string SuggestName(string wanted, string fallback = "view") => Workspace.SuggestName(wanted, fallback);
    }
}
