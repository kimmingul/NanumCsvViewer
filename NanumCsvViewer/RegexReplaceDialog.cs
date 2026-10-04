using System.Text;
using System.Text.RegularExpressions;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    /// <summary>
    /// 정규식 찾아 바꾸기 대화상자(시트 편집 모드). 패턴·바꿀 문자열($1, ${name}, $$)·대소문자·범위(선택한 컬럼 / 보이는 모든 컬럼)를 받고,
    /// 입력하는 동안 (1) 현재 뷰 앞 10,000행 시험(일치 셀 수·표본)과 (2) 현재 뷰 전체 미리보기(일치·변경·시간 초과 셀 수, 앞 20개 바뀌기 전 → 후)를 보여 준다.
    /// 대화상자는 값을 바꾸지 않는다: OK 후 호출자가 계획을 다시 만들어 한 단계로 적용한다.
    /// </summary>
    internal sealed class RegexReplaceDialog : Form
    {
        public const int PreviewSamples = 20;

        private readonly TextBox _pattern, _replacement;
        private readonly CheckBox _caseSensitive;
        private readonly RadioButton _scopeSelected, _scopeAll;
        private readonly RegexTesterPanel _tester;
        private readonly Label _previewSummary;
        private readonly TextBox _previewSamples;
        private readonly Button _ok;
        private readonly DebouncedRunner<TesterOutput> _previewRunner;
        private readonly ThemePalette _palette;

        private readonly Func<long, string[]> _rowAt;
        private readonly Func<IEnumerable<long>> _viewRows;
        private readonly long _viewRowCount;
        private readonly string[] _columnNames;
        private readonly IReadOnlyList<int> _selectedColumns, _visibleColumns;
        private readonly Func<long, long> _rowNumber;

        public string Pattern => _pattern.Text;
        public string Replacement => _replacement.Text;
        public bool CaseSensitive => _caseSensitive.Checked;
        public IReadOnlyList<int> Columns => ScopeColumns();
        public bool UsesSelectedColumns => _scopeSelected.Checked;

        public RegexReplaceDialog(ThemePalette palette, Func<long, string[]> rowAt, Func<IEnumerable<long>> viewRows, long viewRowCount,
            string[] columnNames, IReadOnlyList<int> selectedColumns, IReadOnlyList<int> visibleColumns, Func<long, long> rowNumber,
            string initialPattern = "")
        {
            _palette = palette;
            _rowAt = rowAt;
            _viewRows = viewRows;
            _viewRowCount = viewRowCount;
            _columnNames = columnNames;
            _selectedColumns = selectedColumns;
            _visibleColumns = visibleColumns;
            _rowNumber = rowNumber;

            Text = RegexUi.LT("Find & Replace (regex)", "찾아 바꾸기 (정규식)");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(660, 700);
            MinimumSize = new Size(600, 640);
            Padding = new Padding(14);

            var form = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _pattern = AddText(form, RegexUi.LT("Find (regex)", "찾을 정규식"), "regexReplacePattern");
            _replacement = AddText(form, RegexUi.LT("Replace with", "바꿀 내용"), "regexReplaceReplacement");
            _pattern.Text = initialPattern;

            var note = new Label
            {
                AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = palette.Text, Margin = new Padding(0, 0, 0, 6),
                Text = RegexUi.LT("Replacement syntax: $1, ${name}, $$ (a literal $). Matching is per cell; cells whose value would not change are left alone. Applies to the rows of the current view (respecting filters); one Undo reverts everything.",
                                  "바꿀 내용 문법: $1, ${이름}, $$($ 문자 그대로). 셀 단위로 일치시키며 값이 달라지지 않는 셀은 그대로 둡니다. 필터가 걸린 현재 뷰의 행에 적용하고, 되돌리기 한 번으로 전부 취소됩니다."),
            };
            form.Controls.Add(note, 0, form.RowCount);
            form.SetColumnSpan(note, 2);
            form.RowCount++;

            _caseSensitive = new CheckBox { Text = RegexUi.LT("Case-sensitive", "대소문자 구분"), AutoSize = true, ForeColor = palette.Text, Name = "regexReplaceCase" };
            form.Controls.Add(new Label { Text = "", Height = 4 }, 0, form.RowCount);
            form.Controls.Add(_caseSensitive, 1, form.RowCount);
            form.RowCount++;

            string selNames = selectedColumns.Count == 0 ? "" : " (" + Abbreviate(string.Join(", ", selectedColumns.Select(NameOf)), 70) + ")";
            _scopeSelected = new RadioButton
            {
                Text = RegexUi.LT("Selected columns", "선택한 컬럼") + selNames, AutoSize = true, ForeColor = palette.Text,
                Enabled = selectedColumns.Count > 0, Checked = selectedColumns.Count > 0, Name = "regexReplaceScopeSelected",
            };
            _scopeAll = new RadioButton
            {
                Text = RegexUi.LT($"All visible columns ({visibleColumns.Count:N0})", $"보이는 모든 컬럼 ({visibleColumns.Count:N0}개)"),
                AutoSize = true, ForeColor = palette.Text, Checked = selectedColumns.Count == 0, Name = "regexReplaceScopeAll",
            };
            var scope = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            scope.Controls.Add(_scopeSelected);
            scope.Controls.Add(_scopeAll);
            form.Controls.Add(new Label { Text = RegexUi.LT("Columns", "대상 컬럼"), AutoSize = true, ForeColor = palette.Text, Margin = new Padding(0, 6, 0, 0) }, 0, form.RowCount);
            form.Controls.Add(scope, 1, form.RowCount);
            form.RowCount++;

            _tester = new RegexTesterPanel(palette, RegexUi.LT("Pattern test (first 10,000 rows of the view)", "패턴 시험 (현재 뷰 앞 10,000행)"))
            { Dock = DockStyle.Top, Height = 150, Name = "regexReplaceTester" };

            var previewHeading = new Label
            {
                Text = RegexUi.LT("Preview (whole current view)", "미리보기 (현재 뷰 전체)"), Dock = DockStyle.Top, Height = 22, ForeColor = palette.Accent,
                Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 4, 0, 0),
            };
            _previewSummary = new Label { Dock = DockStyle.Top, Height = 58, ForeColor = palette.Text, Name = "regexReplacePreviewSummary" };
            _previewSamples = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = palette.Surface, ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle, Name = "regexReplacePreviewSamples",
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = RegexUi.LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(96, 28) };
            _ok = new Button { Text = RegexUi.LT("Replace All", "모두 바꾸기"), DialogResult = DialogResult.OK, Size = new Size(110, 28), Enabled = false, Name = "regexReplaceOk" };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_ok);
            AcceptButton = _ok;
            CancelButton = cancel;

            Controls.Add(_previewSamples);
            Controls.Add(_previewSummary);
            Controls.Add(previewHeading);
            Controls.Add(_tester);
            Controls.Add(buttons);
            Controls.Add(form);

            _previewRunner = new DebouncedRunner<TesterOutput>(this, 450);
            _pattern.TextChanged += (_, _) => Retest();
            _replacement.TextChanged += (_, _) => Retest();
            _caseSensitive.CheckedChanged += (_, _) => Retest();
            _scopeSelected.CheckedChanged += (_, _) => Retest();
            Shown += (_, _) => { _pattern.Focus(); _pattern.SelectAll(); Retest(); };
        }

        private string NameOf(int col) => col >= 0 && col < _columnNames.Length ? _columnNames[col] : $"Column{col + 1}";

        private static string Abbreviate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

        private TextBox AddText(TableLayoutPanel form, string label, string name)
        {
            var tb = new TextBox { Dock = DockStyle.Top, BackColor = _palette.Surface, ForeColor = _palette.Text, BorderStyle = BorderStyle.FixedSingle, Name = name };
            form.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = _palette.Text, Margin = new Padding(0, 6, 0, 0) }, 0, form.RowCount);
            form.Controls.Add(tb, 1, form.RowCount);
            form.RowCount++;
            return tb;
        }

        private IReadOnlyList<int> ScopeColumns() => _scopeSelected.Checked ? _selectedColumns : _visibleColumns;

        private void Retest()
        {
            _ok.Enabled = false;
            string pattern = _pattern.Text;
            if (pattern.Length == 0)
            {
                _tester.Clear(RegexUi.LT("Type a pattern to test it on the current view.", "패턴을 입력하면 현재 뷰에서 바로 시험합니다."));
                _previewRunner.Cancel();
                _previewSummary.ForeColor = _palette.Text;
                _previewSummary.Text = "";
                _previewSamples.Text = "";
                return;
            }

            string replacement = _replacement.Text;
            bool cs = _caseSensitive.Checked;
            var columns = ScopeColumns().ToArray();

            // 패턴 검증은 UI 스레드에서 즉시(실패하면 둘 다 오류 표시). 컴파일은 Compiled 옵션이라 약간 걸릴 수 있으나 입력 디바운스 뒤라 부담 없다.
            Regex regex;
            try { regex = RegexSafety.Compile(pattern, cs); }
            catch (RegexPatternException ex)
            {
                _tester.Show(TesterOutput.Fail(ex.Message));
                _previewRunner.Cancel();
                _previewSummary.ForeColor = RegexUi.ErrorColor;
                _previewSummary.Text = "⚠ " + ex.Message;
                _previewSamples.Text = "";
                return;
            }
            _ok.Enabled = columns.Length > 0;
            if (columns.Length == 0)
            {
                _tester.Clear(RegexUi.LT("No columns are selected.", "대상 컬럼이 없습니다."));
                _previewSummary.ForeColor = RegexUi.ErrorColor;
                _previewSummary.Text = RegexUi.LT("⚠ Select at least one column, or choose All visible columns.", "⚠ 컬럼을 하나 이상 선택하거나 '보이는 모든 컬럼'을 고르세요.");
                _previewSamples.Text = "";
                return;
            }

            _tester.Schedule(ct =>
            {
                var r = RegexReplace.TestPattern(_rowAt, _viewRows(), columns, regex, ct);
                return new TesterOutput(RegexUi.Describe(r, _viewRowCount), r.Samples.Select(s => "• " + RegexUi.OneLine(s)).ToList());
            });

            _previewSummary.ForeColor = _palette.Text;
            _previewSummary.Text = RegexUi.LT("Scanning the whole view…", "현재 뷰 전체를 훑는 중…");
            _previewRunner.Schedule(ct =>
            {
                var p = RegexReplace.Preview(_rowAt, _viewRows(), columns, regex, replacement, PreviewSamples, ct);
                string text = RegexUi.LT(
                    $"{p.RowsScanned:N0} rows scanned: {p.CellsMatched:N0} cell(s) match, {p.CellsChanged:N0} would change" +
                    (p.CellsMatched > p.CellsChanged ? $" ({p.CellsMatched - p.CellsChanged:N0} match but the replacement gives the same value)." : "."),
                    $"{p.RowsScanned:N0}행 검사: 일치 셀 {p.CellsMatched:N0}개, 바뀔 셀 {p.CellsChanged:N0}개" +
                    (p.CellsMatched > p.CellsChanged ? $" (일치하지만 같은 값이 되는 셀 {p.CellsMatched - p.CellsChanged:N0}개 제외)." : ".")) + RegexUi.TimeoutNote(p.CellsTimedOut);
                var lines = new List<string>();
                foreach (var c in p.Samples)
                    lines.Add($"#{_rowNumber(c.DataRow):N0} [{NameOf(c.Column)}]  {RegexUi.OneLine(c.OldValue, 60)}  →  {RegexUi.OneLine(c.NewValue, 60)}");
                return new TesterOutput(text, lines);
            }, o =>
            {
                _previewSummary.ForeColor = _palette.Text;
                _previewSummary.Text = o.Text;
                _previewSamples.Text = string.Join("\r\n", o.Samples);
            }, ex =>
            {
                _previewSummary.ForeColor = RegexUi.ErrorColor;
                _previewSummary.Text = "⚠ " + ex.Message;
                _previewSamples.Text = "";
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _previewRunner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 정규식 추출 대화상자: 원본 컬럼의 값에서 캡처 그룹을 뽑아 새 컬럼으로 만든다. 입력하는 동안 앞 10,000행에서
    /// 일치 행 수와 추출 값 표본을 보여 주고, 패턴·그룹·새 컬럼 이름이 틀리면 이유를 인라인으로 알린다(확인 불가).
    /// </summary>
    internal sealed class ExtractColumnDialog : Form
    {
        private readonly TextBox _pattern, _group, _newName;
        private readonly CheckBox _caseSensitive;
        private readonly ComboBox _source;
        private readonly Label _message;
        private readonly RegexTesterPanel _tester;
        private readonly Func<long, string[]> _rowAt;
        private readonly Func<IEnumerable<long>> _rows;
        private readonly Func<string, string?> _validateName;
        private readonly ThemePalette _palette;
        private bool _nameEdited;

        public string Pattern => _pattern.Text;
        public string GroupSpec => _group.Text.Trim();
        public bool CaseSensitive => _caseSensitive.Checked;
        public int SourceColumn => _source.SelectedIndex;
        public string NewColumnName => _newName.Text.Trim();

        public ExtractColumnDialog(ThemePalette palette, string[] columnNames, int defaultSource, Func<long, string[]> rowAt,
            Func<IEnumerable<long>> rows, Func<string, string?> validateName)
        {
            _palette = palette;
            _rowAt = rowAt;
            _rows = rows;
            _validateName = validateName;

            Text = RegexUi.LT("Extract to New Column (regex)", "정규식으로 새 컬럼에 추출");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(640, 560);
            MinimumSize = new Size(580, 520);
            Padding = new Padding(14);

            var form = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _source = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, BackColor = palette.Surface, ForeColor = palette.Text, Name = "extractSource",
            };
            for (int i = 0; i < columnNames.Length; i++) _source.Items.Add(string.IsNullOrEmpty(columnNames[i]) ? $"Column{i + 1}" : columnNames[i]);
            if (_source.Items.Count > 0) _source.SelectedIndex = Math.Clamp(defaultSource, 0, _source.Items.Count - 1);
            AddRow(form, RegexUi.LT("Source column", "원본 컬럼"), _source);
            _pattern = AddText(form, RegexUi.LT("Pattern (with a group)", "정규식(캡처 그룹 포함)"), "extractPattern");
            _group = AddText(form, RegexUi.LT("Group (optional)", "그룹(선택)"), "extractGroup");
            _newName = AddText(form, RegexUi.LT("New column name", "새 컬럼 이름"), "extractName");

            _caseSensitive = new CheckBox { Text = RegexUi.LT("Case-sensitive", "대소문자 구분"), AutoSize = true, ForeColor = palette.Text, Name = "extractCase" };
            AddRow(form, "", _caseSensitive);

            var note = new Label
            {
                AutoSize = true, MaximumSize = new Size(590, 0), ForeColor = palette.Text, Margin = new Padding(0, 4, 0, 6),
                Text = RegexUi.LT("The first capture group is extracted, e.g. (\\d{4})-(\\d\\d) → 2024. Name a group (?<year>…) and type year in Group to pick it; with no group the whole match is used. Rows that do not match (or time out) get an empty value. The new column covers ALL rows of the table, not just the filtered view, and is written when you save; Undo removes it in one step.",
                                  "첫 번째 캡처 그룹을 추출합니다. 예: (\\d{4})-(\\d\\d) → 2024. 그룹에 (?<year>…)처럼 이름을 붙이고 '그룹'에 year를 적으면 그 그룹을 뽑으며, 그룹이 없으면 전체 일치를 씁니다. 일치하지 않거나 시간 초과인 행은 빈 값입니다. 새 컬럼은 필터와 무관하게 표 전체 행에 채워지고 저장할 때 함께 기록되며, 되돌리기 한 번으로 사라집니다."),
            };
            form.Controls.Add(note, 0, form.RowCount);
            form.SetColumnSpan(note, 2);
            form.RowCount++;

            _message = new Label { Dock = DockStyle.Top, Height = 40, ForeColor = RegexUi.ErrorColor, Name = "extractMessage" };
            _tester = new RegexTesterPanel(palette, RegexUi.LT("Test (first 10,000 rows of the view)", "시험 (현재 뷰 앞 10,000행)")) { Dock = DockStyle.Fill, Name = "extractTester" };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = RegexUi.LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(96, 28) };
            var ok = new Button { Text = RegexUi.LT("Extract", "추출"), Size = new Size(96, 28), Name = "extractOk" };
            ok.Click += (_, _) => TryAccept();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(_tester);
            Controls.Add(buttons);
            Controls.Add(_message);
            Controls.Add(form);

            _newName.TextChanged += (_, _) => { if (_newName.Focused) _nameEdited = true; };
            _source.SelectedIndexChanged += (_, _) => { SuggestName(); Retest(); };
            _pattern.TextChanged += (_, _) => Retest();
            _group.TextChanged += (_, _) => Retest();
            _caseSensitive.CheckedChanged += (_, _) => Retest();
            SuggestName();
            Shown += (_, _) => { _pattern.Focus(); Retest(); };
        }

        private void SuggestName()
        {
            if (_nameEdited || _source.SelectedItem is not string src) return;
            string name = src + "_extract";
            for (int i = 2; _validateName(name) is not null && i < 1000; i++) name = $"{src}_extract{i}";
            _newName.Text = name;
        }

        private void AddRow(TableLayoutPanel form, string label, Control input)
        {
            form.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = _palette.Text, Margin = new Padding(0, 6, 0, 0) }, 0, form.RowCount);
            form.Controls.Add(input, 1, form.RowCount);
            form.RowCount++;
        }

        private TextBox AddText(TableLayoutPanel form, string label, string name)
        {
            var tb = new TextBox { Dock = DockStyle.Top, BackColor = _palette.Surface, ForeColor = _palette.Text, BorderStyle = BorderStyle.FixedSingle, Name = name };
            AddRow(form, label, tb);
            return tb;
        }

        /// <summary>패턴·그룹을 컴파일해 그룹 번호까지 확인한다. 틀리면 RegexPatternException.</summary>
        public static (Regex Regex, int Group) Compile(string pattern, string groupSpec, bool caseSensitive)
        {
            var regex = RegexSafety.Compile(pattern, caseSensitive);
            return (regex, RegexExtract.ResolveGroup(regex, groupSpec));
        }

        private void Retest()
        {
            _message.Text = "";
            if (_pattern.Text.Length == 0)
            {
                _tester.Clear(RegexUi.LT("Type a pattern to test it on the current view.", "패턴을 입력하면 현재 뷰에서 바로 시험합니다."));
                return;
            }
            int source = _source.SelectedIndex;
            string pattern = _pattern.Text, group = GroupSpec;
            bool cs = _caseSensitive.Checked;
            _tester.Schedule(ct =>
            {
                var (regex, g) = Compile(pattern, group, cs);
                var plan = RegexExtract.Plan(_rowAt, _rows().Take(RegexReplace.TestMaxRows), source, regex, g, int.MaxValue, ct);
                string text = RegexUi.LT(
                    $"{plan.RowsScanned:N0} rows tested: {plan.RowsMatched:N0} match, {plan.RowsNotMatched:N0} do not (empty value).",
                    $"{plan.RowsScanned:N0}행 시험: {plan.RowsMatched:N0}행 일치, {plan.RowsNotMatched:N0}행 불일치(빈 값).") + RegexUi.TimeoutNote(plan.CellsTimedOut);
                var samples = plan.Values.Take(RegexReplace.TestMaxSamples).Select(v => "• " + RegexUi.OneLine(v.Value)).ToList();
                return new TesterOutput(text, samples);
            });
        }

        private void TryAccept()
        {
            try { Compile(_pattern.Text, GroupSpec, _caseSensitive.Checked); }
            catch (RegexPatternException ex) { _message.Text = "⚠ " + ex.Message; return; }
            if (_source.SelectedIndex < 0) { _message.Text = "⚠ " + RegexUi.LT("Choose a source column.", "원본 컬럼을 고르세요."); return; }
            if (_validateName(NewColumnName) is { } problem) { _message.Text = "⚠ " + problem; return; }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
