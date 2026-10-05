using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 이어 붙이기 마법사: 표 2개 이상(순서 지정), 컬럼 맞추기(이름 기준 대소문자 무시 / 위치 기준)와 직접 고칠 수 있는 매핑 표,
    /// 형 충돌 경고, 선택 사항인 출처 컬럼(표 이름), 행 수·미리보기, 편집 가능한 SQL.
    /// </summary>
    internal sealed class AppendWizardDialog : WizardForm
    {
        private sealed record ModeItem(UnionMode Mode, string Text)
        {
            public override string ToString() => Text;
        }

        private static string NoneText => LT("— (leave empty)", "— (비워 둠)");

        private readonly IReadOnlyList<IWorkspaceRelation> _relations;
        private readonly CheckedListBox _tables;
        private readonly ComboBox _mode;
        private readonly CheckBox _cbSource;
        private readonly TextBox _tbSource;
        private readonly DataGridView _map;
        private bool _loading, _updatingTypes;
        private List<IWorkspaceRelation> _gridTables = new();

        public AppendWizardDialog(ThemePalette palette, DataWorkspace ws, string? preselect)
            : base(palette, ws, LT("Append tables", "이어 붙이기 (표 아래에 붙이기)"), LT("Use this append", "이 이어 붙이기 사용"), 350)
        {
            _relations = WizardStyle.Relations(ws);
            Name = "appendWizard";

            _tables = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, Name = "appendTables", BorderStyle = BorderStyle.FixedSingle };
            foreach (var r in _relations) _tables.Items.Add(new RelationItem(r));
            var up = new Button { Text = "▲", Size = new Size(40, 26), Name = "appendUp" };
            var down = new Button { Text = "▼", Size = new Size(40, 26), Name = "appendDown" };
            var order = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, WrapContents = false };
            order.Controls.AddRange(new Control[] { up, down, MakeLabel(LT("order of rows", "행 순서")) });
            var leftPanel = new Panel { Dock = DockStyle.Left, Width = 290, Padding = new Padding(0, 0, 8, 0) };
            leftPanel.Controls.Add(_tables);
            leftPanel.Controls.Add(order);
            leftPanel.Controls.Add(new Label { Text = LT("Tables (check two or more)", "표 (2개 이상 체크)"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });

            _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Name = "appendMode", Margin = new Padding(0, 3, 12, 0) };
            _mode.Items.Add(new ModeItem(UnionMode.ByName, LT("Match columns by name (ignoring case)", "컬럼 이름으로 맞추기 (대소문자 무시)")));
            _mode.Items.Add(new ModeItem(UnionMode.ByPosition, LT("Match columns by position", "컬럼 위치(순서)로 맞추기")));
            _mode.SelectedIndex = 0;
            _cbSource = new CheckBox { Text = LT("Add a column with the table name:", "표 이름 컬럼 추가:"), AutoSize = true, Margin = new Padding(0, 6, 4, 0), Name = "appendSourceOn" };
            _tbSource = new TextBox { Text = "source_table", Width = 130, Enabled = false, Margin = new Padding(0, 3, 12, 0), Name = "appendSourceName" };
            var remap = new Button { Text = LT("Re-match columns", "컬럼 다시 맞추기"), Size = new Size(140, 26), Name = "appendRemap" };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
            top.Controls.AddRange(new Control[] { _mode, _cbSource, _tbSource, remap });

            _map = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, Name = "appendMap", EditMode = DataGridViewEditMode.EditOnEnter,
            };
            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(_map);
            rightPanel.Controls.Add(new Label { Text = LT("Column mapping — pick which column of each table fills a result column; empty = NULL", "컬럼 맞추기 — 결과 컬럼마다 각 표의 어느 컬럼을 쓸지 고릅니다 (비우면 NULL)"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });
            rightPanel.Controls.Add(top);

            OptionsHost.Controls.Add(rightPanel);
            OptionsHost.Controls.Add(leftPanel);

            // 미리 고를 표: 지정한 표 + 컬럼 이름이 겹치는 다른 표(없으면 첫 번째 다른 표)
            var pre = FindByName(_relations, preselect) ?? _relations.FirstOrDefault();
            var partner = _relations.Where(r => !ReferenceEquals(r, pre)).OrderByDescending(r => pre is null ? 0 : r.Columns.Count(c => pre.Columns.Any(p => string.Equals(p.Name, c.Name, StringComparison.OrdinalIgnoreCase)))).FirstOrDefault();
            for (int i = 0; i < _relations.Count; i++)
                if (ReferenceEquals(_relations[i], pre) || ReferenceEquals(_relations[i], partner)) _tables.SetItemChecked(i, true);
            RebuildMapping();

            _tables.ItemCheck += (_, _) => { if (!_loading) BeginInvoke(new Action(() => { RebuildMapping(); Changed(); })); };
            up.Click += (_, _) => Move(-1);
            down.Click += (_, _) => Move(1);
            _mode.SelectedIndexChanged += (_, _) => { RebuildMapping(); Changed(); };
            remap.Click += (_, _) => { RebuildMapping(); Changed(); };
            _cbSource.CheckedChanged += (_, _) => { _tbSource.Enabled = _cbSource.Checked; Changed(); };
            _tbSource.TextChanged += (_, _) => Changed();
            _map.CurrentCellDirtyStateChanged += (_, _) => { if (_map.IsCurrentCellDirty) _map.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _map.CellValueChanged += (_, e) => { if (!_loading && !_updatingTypes && e.RowIndex >= 0) Changed(); };
            _map.DataError += (_, e) => e.ThrowException = false;
        }

        private UnionMode Mode => (_mode.SelectedItem as ModeItem)?.Mode ?? UnionMode.ByName;

        private List<IWorkspaceRelation> CheckedTables()
        {
            var list = new List<IWorkspaceRelation>();
            for (int i = 0; i < _tables.Items.Count; i++)
                if (_tables.GetItemChecked(i)) list.Add(((RelationItem)_tables.Items[i]).Relation);
            return list;
        }

        private void Move(int delta)
        {
            int i = _tables.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= _tables.Items.Count) return;
            var item = _tables.Items[i];
            bool isChecked = _tables.GetItemChecked(i);
            bool otherChecked = _tables.GetItemChecked(j);
            var other = _tables.Items[j];
            _loading = true;
            try
            {
                _tables.Items[i] = other; _tables.SetItemChecked(i, otherChecked);
                _tables.Items[j] = item; _tables.SetItemChecked(j, isChecked);
                _tables.SelectedIndex = j;
            }
            finally { _loading = false; }
            RebuildMapping();
            Changed();
        }

        private void RebuildMapping()
        {
            _loading = true;
            try
            {
                _map.Columns.Clear();
                _map.Rows.Clear();
                _gridTables = CheckedTables();
                if (_gridTables.Count < 2) return;
                _map.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = LT("Result column", "결과 컬럼"), Name = "out", FillWeight = 26 });
                _map.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = LT("Type", "형"), Name = "type", ReadOnly = true, FillWeight = 14 });
                foreach (var t in _gridTables)
                {
                    var col = new DataGridViewComboBoxColumn { HeaderText = t.DisplayName, FlatStyle = FlatStyle.Flat, FillWeight = 30, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox };
                    col.Items.Add(NoneText);
                    foreach (var c in t.Columns) col.Items.Add(c.Name);
                    _map.Columns.Add(col);
                }
                foreach (var m in WizardSql.AutoMap(_gridTables, Mode))
                {
                    var cells = new object[2 + _gridTables.Count];
                    cells[0] = m.OutputName;
                    cells[1] = "";
                    for (int t = 0; t < _gridTables.Count; t++) cells[2 + t] = m.Sources[t] ?? NoneText;
                    _map.Rows.Add(cells);
                }
            }
            finally { _loading = false; }
        }

        private AppendSpec ReadSpec(out AppendPlan plan)
        {
            var tables = CheckedTables();
            if (tables.Count < 2) throw new ArgumentException(LT("Check at least two tables to append.", "이어 붙일 표를 2개 이상 체크하세요."));
            if (!tables.SequenceEqual(_gridTables)) RebuildMapping();
            var mapping = new List<AppendColumn>();
            foreach (DataGridViewRow row in _map.Rows)
            {
                var sources = new string?[tables.Count];
                for (int t = 0; t < tables.Count; t++)
                {
                    string? v = Convert.ToString(row.Cells[2 + t].Value);
                    sources[t] = string.IsNullOrEmpty(v) || v == NoneText ? null : v;
                }
                mapping.Add(new AppendColumn((Convert.ToString(row.Cells[0].Value) ?? "").Trim(), sources));
            }
            string? source = _cbSource.Checked ? _tbSource.Text.Trim() : null;
            var spec = new AppendSpec(tables, mapping, source, Mode);
            plan = WizardSql.PlanAppend(spec);
            return spec;
        }

        protected override string BuildSql()
        {
            var spec = ReadSpec(out var plan);
            _updatingTypes = true;
            try
            {
                for (int i = 0; i < plan.Columns.Count && i < _map.Rows.Count; i++)
                {
                    var c = plan.Columns[i];
                    var cell = _map.Rows[i].Cells[1];
                    cell.Value = c.SqlType.ToLowerInvariant() + c.Issue switch
                    {
                        AppendTypeIssue.Widened => LT(" (widened)", " (넓힘)"),
                        AppendTypeIssue.ConvertedToText => LT(" ⚠ as text", " ⚠ 글자로"),
                        _ => "",
                    };
                    cell.Style.ForeColor = c.Issue == AppendTypeIssue.ConvertedToText ? WizardStyle.LevelColor(Palette, DiagLevel.Warn) : Palette.Text;
                }
            }
            finally { _updatingTypes = false; }
            return WizardSql.AppendSql(spec);
        }

        protected override string SuggestViewName()
        {
            var first = CheckedTables().First();
            return SuggestName(SqlNames.Sanitize(first.DisplayName + "_append", "appended"), "appended");
        }

        protected override string Summarize()
        {
            var names = string.Join(" + ", CheckedTables().Select(t => t.DisplayName));
            return LT($"Append {names} ({(Mode == UnionMode.ByName ? "by column name" : "by column position")})",
                      $"이어 붙이기: {names} ({(Mode == UnionMode.ByName ? "컬럼 이름 기준" : "컬럼 위치 기준")})");
        }

        protected override async Task<IReadOnlyList<DiagLine>> DiagnoseAsync(string sql, CancellationToken ct)
        {
            var spec = ReadSpec(out var plan);
            var tables = plan.Tables;
            bool edited = IsSqlEdited;
            string body = SqlText.StripTerminator(sql);
            var counts = await Task.WhenAll(tables.Select(t => Workspace.CountRowsAsync(t, ct)));
            long total = counts.Sum();
            if (edited)
            {
                var q = await Workspace.PreviewAsync($"SELECT count(*) FROM (\n{body}\n) AS q", 1, ct);
                total = long.Parse(q.Rows[0][0]!);
            }

            var lines = new List<DiagLine>();
            lines.Add(new DiagLine(LT(
                $"Result: {N(total)} rows = {string.Join(" + ", tables.Select((t, i) => $"{t.DisplayName} {N(counts[i])}"))}",
                $"결과: {N(total)}행 = {string.Join(" + ", tables.Select((t, i) => $"{t.DisplayName} {N(counts[i])}"))}"), DiagLevel.Good));
            if (edited && total != counts.Sum())
                lines.Add(new DiagLine(LT($"The edited SQL returns {N(total)} rows, not the sum of the tables ({N(counts.Sum())}).", $"직접 고친 SQL은 표 행 수의 합({N(counts.Sum())})이 아닌 {N(total)}행을 돌려줍니다."), DiagLevel.Warn));

            int all = plan.Columns.Count(c => c.Sources.All(s => s is not null));
            lines.Add(new DiagLine(LT($"Columns: {plan.Columns.Count} in the result · {all} present in every table", $"컬럼: 결과 {plan.Columns.Count}개 · 모든 표에 있는 컬럼 {all}개")));

            var partial = plan.Columns.Where(c => c.Sources.Any(s => s is null)).ToList();
            if (partial.Count > 0)
            {
                string list = string.Join(", ", partial.Take(6).Select(c =>
                    $"{c.OutputName} ({LT("missing in", "없는 표")}: {string.Join(", ", Enumerable.Range(0, tables.Count).Where(t => c.Sources[t] is null).Select(t => tables[t].DisplayName))})"));
                lines.Add(new DiagLine(LT($"{partial.Count} column(s) are missing in some tables and will be NULL there: {list}{(partial.Count > 6 ? " …" : "")}",
                                          $"일부 표에 없는 컬럼 {partial.Count}개는 그 표의 행에서 NULL이 됩니다: {list}{(partial.Count > 6 ? " …" : "")}"), DiagLevel.Warn));
            }

            for (int t = 0; t < tables.Count; t++)
            {
                var used = new HashSet<string>(plan.Columns.Select(c => c.Sources[t]?.Name ?? "").Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
                var dropped = tables[t].Columns.Where(c => !used.Contains(c.Name)).Select(c => c.Name).ToList();
                if (dropped.Count > 0)
                    lines.Add(new DiagLine(LT($"Not included from {tables[t].DisplayName}: {string.Join(", ", dropped.Take(8))}{(dropped.Count > 8 ? " …" : "")}",
                                              $"{tables[t].DisplayName}에서 빠지는 컬럼: {string.Join(", ", dropped.Take(8))}{(dropped.Count > 8 ? " …" : "")}"), DiagLevel.Warn));
            }

            foreach (var w in plan.Warnings) lines.Add(new DiagLine("⚠ " + w, DiagLevel.Warn));
            foreach (var c in plan.Columns.Where(c => c.Issue == AppendTypeIssue.Widened))
                lines.Add(new DiagLine(LT($"'{c.OutputName}': number/date types differ and were widened to {c.SqlType.ToLowerInvariant()}.", $"'{c.OutputName}': 숫자·날짜 형이 달라 {c.SqlType.ToLowerInvariant()}(으)로 넓혔습니다.")));
            return lines;
        }
    }
}
