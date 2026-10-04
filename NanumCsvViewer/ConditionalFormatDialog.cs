using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    /// <summary>
    /// 조건부 서식 규칙 관리: 추가·편집·삭제·위/아래(우선순위)·켜기/끄기. 식 규칙은 편집하는 동안 현재 보기의 앞 10,000행에서
    /// 일치하는 행 수를 바로 보여 준다(백그라운드, 시간 예산 안에서만 — 정규식 시간 초과는 따로 센다).
    /// 확인을 누를 때까지 아무것도 바뀌지 않는다.
    /// </summary>
    internal sealed class ConditionalFormatDialog : Form
    {
        private const int PreviewRows = 10_000;
        private const int PreviewBudgetMs = 1500;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        private readonly List<ConditionalFormatRule> _rules;
        private readonly string[] _columnNames;
        private readonly string[] _headers;
        private readonly IReadOnlyList<string[]> _sample;
        private readonly ThemePalette _palette;

        private readonly CheckedListBox _list = new();
        private readonly TextBox _name = new();
        private readonly ComboBox _kind = new(), _target = new(), _column = new();
        private readonly TextBox _expression = new();
        private readonly ColorField _back, _fore, _min, _mid, _max;
        private readonly CheckBox _bold = new();
        private readonly Label _preview = new();
        private readonly Panel _editor = new();
        private readonly Label _targetLabel = new(), _columnLabel = new(), _exprLabel = new(), _backLabel = new(), _foreLabel = new(),
            _minLabel = new(), _midLabel = new(), _maxLabel = new(), _hint = new();
        private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 350 };
        private CancellationTokenSource? _previewCts;
        private bool _loading, _populating;

        public IReadOnlyList<ConditionalFormatRule> Rules => _rules;

        public ConditionalFormatDialog(List<ConditionalFormatRule> rules, string[] columnNames, string[] headers,
            IReadOnlyList<string[]> sample, ThemePalette palette)
        {
            _rules = rules;
            _columnNames = columnNames;
            _headers = headers;
            _sample = sample;
            _palette = palette;
            _back = new ColorField(palette); _fore = new ColorField(palette);
            _min = new ColorField(palette); _mid = new ColorField(palette); _max = new ColorField(palette);

            Text = LT("Conditional Formatting", "조건부 서식");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(760, 520);

            // ---- 왼쪽: 규칙 목록 + 버튼
            var left = new Panel { Dock = DockStyle.Left, Width = 300, Padding = new Padding(10, 10, 5, 10) };
            _list.Dock = DockStyle.Fill;
            _list.CheckOnClick = true;
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = true;
            _list.BackColor = palette.Surface;
            _list.ForeColor = palette.Text;
            _list.BorderStyle = BorderStyle.FixedSingle;
            var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0) };
            listButtons.Controls.Add(MakeButton(LT("Add", "추가"), (_, _) => AddRule(), 62));
            listButtons.Controls.Add(MakeButton(LT("Delete", "삭제"), (_, _) => DeleteRule(), 62));
            listButtons.Controls.Add(MakeButton("▲", (_, _) => MoveRule(-1), 40));
            listButtons.Controls.Add(MakeButton("▼", (_, _) => MoveRule(+1), 40));
            var listHint = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 54,
                Text = LT("Rules are checked top to bottom: for each of back color, text color and bold, the first matching rule wins. Edited cells and inserted rows keep their own highlight color.",
                          "규칙은 위에서 아래로 봅니다. 배경·글자색·굵게는 각각 처음 맞은 규칙이 정합니다. 편집한 셀과 삽입한 행은 자기 강조색을 유지합니다."),
            };
            left.Controls.Add(_list);
            left.Controls.Add(listButtons);
            left.Controls.Add(listHint);

            // ---- 오른쪽: 편집기
            _editor.Dock = DockStyle.Fill;
            _editor.Padding = new Padding(5, 10, 10, 10);
            BuildEditor();

            // ---- 아래: 확인/취소
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10, 8, 10, 4) };
            var cancel = new Button { Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(88, 28) };
            var ok = new Button { Text = LT("OK", "확인"), Size = new Size(88, 28) };
            ok.Click += (_, _) => { if (Commit()) DialogResult = DialogResult.OK; };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            AcceptButton = null;
            CancelButton = cancel;

            Controls.Add(_editor);
            Controls.Add(left);
            Controls.Add(bottom);

            _list.SelectedIndexChanged += (_, _) => LoadSelected();
            _list.ItemCheck += OnItemCheck;
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RunPreview(); };
            FormClosed += (_, _) => { _previewTimer.Stop(); _previewCts?.Cancel(); };

            RefreshList(_rules.Count > 0 ? 0 : -1);
        }

        private Button MakeButton(string text, EventHandler click, int width)
        {
            var b = new Button { Text = text, Size = new Size(width, 28), Margin = new Padding(0, 0, 6, 0) };
            b.Click += click;
            return b;
        }

        private TableLayoutPanel? _table;
        private readonly List<int> _rowHeights = new();

        // 편집기 행 번호(BuildEditor의 Row 호출 순서와 같다)
        private const int RowTarget = 2, RowColumn = 3, RowExpr = 4, RowHint = 5, RowBack = 6, RowFore = 7, RowBold = 8,
            RowMin = 9, RowMid = 10, RowMax = 11;
        private void BuildEditor()
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            _table = table;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            void Row(Label label, string text, Control input, int height = 28)
            {
                label.Text = text;
                label.AutoSize = false;
                label.Dock = DockStyle.Fill;
                label.TextAlign = ContentAlignment.MiddleLeft;
                input.Dock = DockStyle.Fill;
                input.Margin = new Padding(0, 3, 0, 3);
                int r = table.RowCount++;
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                _rowHeights.Add(height);
                table.Controls.Add(label, 0, r);
                table.Controls.Add(input, 1, r);
            }

            foreach (var tb in new[] { _name, _expression })
            {
                tb.BackColor = _palette.Surface;
                tb.ForeColor = _palette.Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
            }
            foreach (var cb in new[] { _kind, _target, _column })
            {
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
                cb.BackColor = _palette.Surface;
                cb.ForeColor = _palette.Text;
            }
            _kind.Items.AddRange(new object[] { LT("Condition (expression)", "조건(식)"), LT("Color scale (numeric column)", "색상 눈금(숫자 컬럼)") });
            _target.Items.AddRange(new object[] { LT("Cell in the column below", "아래 컬럼의 셀"), LT("Entire row", "행 전체") });
            foreach (string c in _columnNames) _column.Items.Add(c);
            _expression.Multiline = true;
            _expression.ScrollBars = ScrollBars.Vertical;
            _bold.Text = LT("Bold", "굵게");
            _bold.ForeColor = _palette.Text;
            _preview.AutoSize = false;
            _preview.Dock = DockStyle.Fill;
            _hint.AutoSize = false;
            _hint.ForeColor = _palette.Text;

            Row(new Label(), LT("Name", "이름"), _name);
            Row(new Label(), LT("Type", "종류"), _kind);
            Row(_targetLabel, LT("Apply to", "적용 대상"), _target);
            Row(_columnLabel, LT("Column", "컬럼"), _column);
            Row(_exprLabel, LT("Condition", "조건"), _expression, 74);
            Row(new Label(), "", _hint, 56);
            _hint.Text = LT("Filter expression syntax, e.g.  age >= 65 AND sex = \"M\"   ·   name matches \"^A\"   ·   NOT (score < 50)   ·   * contains \"error\"   ·   [end] < [start]",
                            "필터 식 문법 예:  age >= 65 AND sex = \"M\"   ·   name matches \"^A\"   ·   NOT (score < 50)   ·   * contains \"error\"   ·   [end] < [start]");
            Row(_backLabel, LT("Back color", "배경색"), _back);
            Row(_foreLabel, LT("Text color", "글자색"), _fore);
            Row(new Label(), "", _bold);
            Row(_minLabel, LT("Minimum color", "최소값 색"), _min);
            Row(_midLabel, LT("Middle color (optional)", "중간 색(선택)"), _mid);
            Row(_maxLabel, LT("Maximum color", "최대값 색"), _max);
            Row(new Label(), "", _preview, 60);

            _editor.Controls.Add(table);

            _name.TextChanged += (_, _) => Edit(r => r with { Name = _name.Text });
            _kind.SelectedIndexChanged += (_, _) => Edit(r => r with { Kind = _kind.SelectedIndex == 1 ? ConditionalFormatKind.ColorScale : ConditionalFormatKind.Expression }, relayout: true);
            _target.SelectedIndexChanged += (_, _) => Edit(r => r with { Target = _target.SelectedIndex == 1 ? ConditionalFormatTarget.Row : ConditionalFormatTarget.Cell }, relayout: true);
            _column.SelectedIndexChanged += (_, _) => Edit(r => r with { Column = _column.SelectedIndex >= 0 ? _columnNames[_column.SelectedIndex] : null });
            _expression.TextChanged += (_, _) => Edit(r => r with { Expression = _expression.Text });
            _bold.CheckedChanged += (_, _) => Edit(r => r with { Bold = _bold.Checked });
            _back.ValueChanged += (_, _) => Edit(r => r with { BackColor = NullIfEmpty(_back.Value) });
            _fore.ValueChanged += (_, _) => Edit(r => r with { ForeColor = NullIfEmpty(_fore.Value) });
            _min.ValueChanged += (_, _) => Edit(r => r with { ScaleMinColor = NullIfEmpty(_min.Value) });
            _mid.ValueChanged += (_, _) => Edit(r => r with { ScaleMidColor = NullIfEmpty(_mid.Value) });
            _max.ValueChanged += (_, _) => Edit(r => r with { ScaleMaxColor = NullIfEmpty(_max.Value) });
        }

        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private int Selected => _list.SelectedIndex;

        private void Edit(Func<ConditionalFormatRule, ConditionalFormatRule> change, bool relayout = false)
        {
            if (_loading || _populating || Selected < 0 || Selected >= _rules.Count) return;
            _rules[Selected] = change(_rules[Selected]);
            RefreshItemText(Selected);
            if (relayout) UpdateVisibility();
            SchedulePreview();
        }

        private string ItemText(int i)
        {
            var r = _rules[i];
            string name = string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name;
            string where = r.Kind == ConditionalFormatKind.ColorScale ? LT($"color scale: {r.Column}", $"색상 눈금: {r.Column}")
                : r.Target == ConditionalFormatTarget.Row ? LT("row", "행 전체") : LT($"cell: {r.Column}", $"셀: {r.Column}");
            return $"{i + 1}. {name}  ({where})";
        }

        private void RefreshList(int select)
        {
            _loading = true;
            _list.Items.Clear();
            for (int i = 0; i < _rules.Count; i++) _list.Items.Add(ItemText(i), _rules[i].Enabled);
            _loading = false;
            if (select >= 0 && select < _list.Items.Count) _list.SelectedIndex = select;
            else LoadSelected();
        }

        private void RefreshItemText(int i)
        {
            _loading = true;
            if (i >= 0 && i < _list.Items.Count)
            {
                bool chk = _list.GetItemChecked(i);
                int sel = _list.SelectedIndex;
                _list.Items[i] = ItemText(i);
                _list.SetItemChecked(i, chk);
                if (_list.SelectedIndex != sel) _list.SelectedIndex = sel;
            }
            _loading = false;
        }

        private void OnItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (_loading || e.Index < 0 || e.Index >= _rules.Count) return;
            _rules[e.Index] = _rules[e.Index] with { Enabled = e.NewValue == CheckState.Checked };
        }

        private void AddRule()
        {
            if (_rules.Count >= ConditionalFormatRule.MaxRules)
            {
                MessageBox.Show(this, LT($"At most {ConditionalFormatRule.MaxRules} rules are allowed.", $"규칙은 최대 {ConditionalFormatRule.MaxRules}개까지 둘 수 있습니다."), Text);
                return;
            }
            string id = ConditionalFormatRules.NextId(_rules);
            _rules.Add(new ConditionalFormatRule(id, LT("New rule", "새 규칙"), true, ConditionalFormatKind.Expression, "",
                ConditionalFormatTarget.Cell, _columnNames.Length > 0 ? _columnNames[0] : null,
                "#FFE699", null, false, "#FFFFFF", null, "#F8696B"));
            RefreshList(_rules.Count - 1);
            _expression.Focus();
        }

        private void DeleteRule()
        {
            int i = Selected;
            if (i < 0 || i >= _rules.Count) return;
            _rules.RemoveAt(i);
            RefreshList(Math.Min(i, _rules.Count - 1));
        }

        private void MoveRule(int delta)
        {
            int i = Selected, j = i + delta;
            if (i < 0 || j < 0 || j >= _rules.Count) return;
            (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
            RefreshList(j);
        }

        private void LoadSelected()
        {
            if (_loading) return;
            _populating = true;
            bool has = Selected >= 0 && Selected < _rules.Count;
            _editor.Enabled = has;
            if (has)
            {
                var r = _rules[Selected];
                _name.Text = r.Name;
                _kind.SelectedIndex = r.Kind == ConditionalFormatKind.ColorScale ? 1 : 0;
                _target.SelectedIndex = r.Target == ConditionalFormatTarget.Row ? 1 : 0;
                int ci = r.Column is null ? -1 : ConditionalFormatRules.ResolveColumn(_headers, r.Column);
                _column.SelectedIndex = ci >= 0 && ci < _column.Items.Count ? ci : -1;
                _expression.Text = r.Expression;
                _bold.Checked = r.Bold;
                _back.Value = r.BackColor ?? "";
                _fore.Value = r.ForeColor ?? "";
                _min.Value = r.ScaleMinColor ?? "";
                _mid.Value = r.ScaleMidColor ?? "";
                _max.Value = r.ScaleMaxColor ?? "";
            }
            _populating = false;
            UpdateVisibility();
            SchedulePreview();
        }

        private void UpdateVisibility()
        {
            bool scale = _kind.SelectedIndex == 1;
            bool row = !scale && _target.SelectedIndex == 1;
            foreach (var c in new Control[] { _targetLabel, _target, _exprLabel, _expression, _hint, _backLabel, _back, _foreLabel, _fore, _bold })
                c.Visible = !scale;
            foreach (var c in new Control[] { _minLabel, _min, _midLabel, _mid, _maxLabel, _max }) c.Visible = scale;
            _columnLabel.Visible = _column.Visible = !row;
            if (_table is null) return;
            // 숨긴 행이 빈 공간으로 남지 않게 높이를 0으로 줄인다.
            void Show(int r, bool visible) => _table.RowStyles[r].Height = visible ? _rowHeights[r] : 0;
            foreach (int r in new[] { RowTarget, RowExpr, RowHint, RowBack, RowFore, RowBold }) Show(r, !scale);
            foreach (int r in new[] { RowMin, RowMid, RowMax }) Show(r, scale);
            Show(RowColumn, !row);
        }

        // ---------------------------------------------------------------- 미리보기

        private void SchedulePreview()
        {
            _previewTimer.Stop();
            _previewCts?.Cancel();
            _preview.ForeColor = _palette.Text;
            _preview.Text = "";
            if (Selected < 0 || Selected >= _rules.Count) return;
            _previewTimer.Start();
        }

        private async void RunPreview()
        {
            if (Selected < 0 || Selected >= _rules.Count) return;
            var rule = _rules[Selected];
            string? problem = ConditionalFormatRules.Validate(rule, _headers);
            if (problem is not null)
            {
                _preview.ForeColor = Color.FromArgb(214, 76, 76);
                _preview.Text = problem;
                return;
            }
            if (rule.Kind == ConditionalFormatKind.ColorScale)
            {
                _preview.Text = LT("Colors run from the minimum to the maximum of the column in the current view (non-numeric cells stay uncolored).",
                                   "색은 현재 보기에서 이 컬럼의 최소~최대값에 따라 정해집니다(숫자가 아닌 셀은 색을 입히지 않습니다).");
                return;
            }
            _previewCts?.Cancel();
            var cts = _previewCts = new CancellationTokenSource();
            _preview.Text = LT("Counting…", "세는 중…");
            string expr = rule.Expression;
            try
            {
                var count = await Task.Run(() => ConditionalFormatSet.Count(expr, _headers, _sample, PreviewRows, cts.Token, PreviewBudgetMs), cts.Token);
                if (cts.IsCancellationRequested || IsDisposed) return;
                long total = Math.Min(PreviewRows, _sample.Count);
                string text = LT($"Matches {count.RowsMatched:N0} of the first {count.RowsScanned:N0} rows of the current view.",
                                 $"현재 보기 앞 {count.RowsScanned:N0}행 중 {count.RowsMatched:N0}행이 일치합니다.");
                if (count.RowsScanned < total)
                    text += LT($" (stopped after {PreviewBudgetMs / 1000.0:0.#} s; {total:N0} rows requested)", $" ({PreviewBudgetMs / 1000.0:0.#}초 후 중단, 요청 {total:N0}행)");
                if (count.TimedOut > 0)
                    text += LT($"\n⚠ {count.TimedOut:N0} cell(s) exceeded the regex time limit and were treated as NOT matching.",
                               $"\n⚠ {count.TimedOut:N0}개 셀이 정규식 시간 제한을 넘겨 일치하지 않는 것으로 처리했습니다.");
                _preview.ForeColor = count.TimedOut > 0 ? Color.FromArgb(200, 120, 0) : _palette.Text;
                _preview.Text = text;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is AdvancedFilterExpressionException or RegexPatternException)
            {
                _preview.ForeColor = Color.FromArgb(214, 76, 76);
                _preview.Text = ex.Message;
            }
        }

        // ---------------------------------------------------------------- 확인

        private bool Commit()
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                var r = _rules[i];
                if (!r.Enabled) continue;
                string? problem = ConditionalFormatRules.Validate(r, _headers);
                if (problem is null) continue;
                _list.SelectedIndex = i;
                MessageBox.Show(this, LT($"Rule {i + 1} ({r.Name}): {problem}\nFix it or uncheck the rule.", $"{i + 1}번 규칙({r.Name}): {problem}\n고치거나 규칙의 체크를 끄세요."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _previewTimer.Dispose(); _previewCts?.Dispose(); }
            base.Dispose(disposing);
        }

        /// <summary>16진 색 입력 + 색 선택 단추. 비워 두면 "정하지 않음".</summary>
        private sealed class ColorField : Panel
        {
            private readonly TextBox _text = new();
            private readonly Button _swatch = new();
            private bool _setting;

            public event EventHandler? ValueChanged;

            [System.ComponentModel.Browsable(false)]
            [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
            public string Value
            {
                get => _text.Text;
                set
                {
                    _setting = true;
                    _text.Text = value;
                    UpdateSwatch();
                    _setting = false;
                }
            }

            public ColorField(ThemePalette p)
            {
                Height = 28;
                _text.Dock = DockStyle.Left;
                _text.Width = 110;
                _text.BackColor = p.Surface;
                _text.ForeColor = p.Text;
                _text.BorderStyle = BorderStyle.FixedSingle;
                _swatch.Dock = DockStyle.Left;
                _swatch.Width = 60;
                _swatch.Text = "…";
                var clear = new Button { Dock = DockStyle.Left, Width = 60, Text = LT("None", "없음") };
                Controls.Add(clear);
                Controls.Add(_swatch);
                Controls.Add(_text);
                _text.TextChanged += (_, _) => { UpdateSwatch(); if (!_setting) ValueChanged?.Invoke(this, EventArgs.Empty); };
                _swatch.Click += (_, _) =>
                {
                    using var dlg = new ColorDialog { FullOpen = true, Color = ConditionalFormatRule.ParseColor(_text.Text) ?? Color.White };
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK) _text.Text = ConditionalFormatRule.ToHex(dlg.Color);
                };
                clear.Click += (_, _) => _text.Text = "";
            }

            private void UpdateSwatch()
            {
                var c = ConditionalFormatRule.ParseColor(_text.Text);
                _swatch.BackColor = c ?? SystemColors.Control;
                _swatch.ForeColor = c is { } cc ? ConditionalFormatSet.Contrast(cc) : SystemColors.ControlText;
                _swatch.UseVisualStyleBackColor = c is null;
            }
        }
    }
}
