using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 그룹 집계 마법사: 표 하나, 그룹 기준 컬럼들, 집계(개수·합계·평균·최소·최대·고유 개수·중앙값) 여러 개, 미리보기, 편집 가능한 SQL.
    /// 합계·평균·중앙값은 숫자 컬럼만 쓸 수 있고(아니면 이유를 알려 준다) NULL인 그룹 값은 하나의 그룹으로 나온다.
    /// </summary>
    internal sealed class GroupWizardDialog : WizardForm
    {
        private sealed record FuncItem(GroupFunction Value, string Text);
        private sealed record ColItem(string Name, string Text);

        private readonly IReadOnlyList<IWorkspaceRelation> _relations;
        private readonly ComboBox _cmbTable;
        private readonly CheckedListBox _groupBy;
        private readonly DataGridView _agg;
        private readonly DataGridViewComboBoxColumn _colFunc, _colColumn;
        private bool _loading;

        private static readonly (GroupFunction F, string En, string Ko)[] Functions =
        {
            (GroupFunction.Count, "Count", "개수"), (GroupFunction.Sum, "Sum", "합계"), (GroupFunction.Avg, "Average", "평균"),
            (GroupFunction.Min, "Min", "최소"), (GroupFunction.Max, "Max", "최대"), (GroupFunction.CountDistinct, "Count distinct", "고유 개수"),
            (GroupFunction.Median, "Median", "중앙값"),
        };

        public GroupWizardDialog(ThemePalette palette, DataWorkspace ws, string? preselect)
            : base(palette, ws, LT("Group & summarize", "그룹 집계"), LT("Use this summary", "이 집계 사용"), 360)
        {
            _relations = WizardStyle.Relations(ws);
            Name = "groupWizard";

            _cmbTable = MakeRelationCombo(_relations, "groupTable");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
            top.Controls.AddRange(new Control[] { MakeLabel(LT("Table", "표")), _cmbTable });

            _groupBy = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, Name = "groupBy", BorderStyle = BorderStyle.FixedSingle };
            var leftPanel = new Panel { Dock = DockStyle.Left, Width = 320, Padding = new Padding(0, 0, 10, 0) };
            leftPanel.Controls.Add(_groupBy);
            leftPanel.Controls.Add(new Label { Text = LT("Group by (check the columns)", "그룹 기준 컬럼 (체크)"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });

            _colFunc = new DataGridViewComboBoxColumn
            {
                HeaderText = LT("Calculation", "계산"), FlatStyle = FlatStyle.Flat, FillWeight = 24, DisplayMember = "Text", ValueMember = "Value",
                DataSource = Functions.Select(f => new FuncItem(f.F, LT(f.En, f.Ko))).ToList(),
            };
            _colColumn = new DataGridViewComboBoxColumn { HeaderText = LT("Column", "컬럼"), FlatStyle = FlatStyle.Flat, FillWeight = 40, DisplayMember = "Text", ValueMember = "Name" };
            _agg = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, Name = "groupAggregates", EditMode = DataGridViewEditMode.EditOnEnter,
            };
            _agg.Columns.Add(_colFunc);
            _agg.Columns.Add(_colColumn);
            _agg.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = LT("Name in result (optional)", "결과 컬럼 이름 (선택)"), FillWeight = 36, Name = "alias" });

            var add = new Button { Text = LT("+ Add calculation", "+ 계산 추가"), Size = new Size(140, 26), Name = "groupAdd" };
            var remove = new Button { Text = LT("Remove selected", "선택한 계산 삭제"), Size = new Size(140, 26), Name = "groupRemove" };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, WrapContents = false };
            bar.Controls.AddRange(new Control[] { add, remove });
            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(_agg);
            rightPanel.Controls.Add(bar);
            rightPanel.Controls.Add(new Label { Text = LT("Calculations (sum, average and median need a number column)", "계산 (합계·평균·중앙값은 숫자 컬럼만)"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });

            var bottom = new Panel { Dock = DockStyle.Fill };
            bottom.Controls.Add(rightPanel);
            bottom.Controls.Add(leftPanel);
            OptionsHost.Controls.Add(bottom);
            OptionsHost.Controls.Add(top);

            SelectRelation(_cmbTable, FindByName(_relations, preselect) ?? _relations.FirstOrDefault());
            LoadTable();

            _cmbTable.SelectedIndexChanged += (_, _) => { LoadTable(); Changed(); };
            _groupBy.ItemCheck += (_, _) => { if (!_loading) BeginInvoke(new Action(Changed)); };
            add.Click += (_, _) => { AddAggregate(GroupFunction.Count, ""); Changed(); };
            remove.Click += (_, _) =>
            {
                foreach (DataGridViewRow r in _agg.SelectedRows.Cast<DataGridViewRow>().ToList()) _agg.Rows.Remove(r);
                Changed();
            };
            _agg.CurrentCellDirtyStateChanged += (_, _) => { if (_agg.IsCurrentCellDirty) _agg.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _agg.CellValueChanged += (_, e) => { if (!_loading && e.RowIndex >= 0) Changed(); };
            _agg.DataError += (_, e) => e.ThrowException = false;
        }

        private void AddAggregate(GroupFunction f, string column) => _agg.Rows.Add(f, column, "");

        private void LoadTable()
        {
            _loading = true;
            try
            {
                _groupBy.Items.Clear();
                _agg.Rows.Clear();
                var t = Selected(_cmbTable);
                var items = new List<ColItem> { new("", LT("(all rows)", "(모든 행)")) };
                if (t is not null)
                {
                    foreach (var c in t.Columns)
                    {
                        _groupBy.Items.Add(c.Name);
                        items.Add(new ColItem(c.Name, $"{c.Name}  [{c.SqlType.ToLowerInvariant()}]"));
                    }
                }
                _colColumn.DataSource = items;
                AddAggregate(GroupFunction.Count, "");
                var numeric = t?.Columns.FirstOrDefault(c => TypedColumnSql.FromSqlType(c.SqlType) is Csv.ColumnValueType.Integer or Csv.ColumnValueType.Float);
                if (numeric is not null) AddAggregate(GroupFunction.Sum, numeric.Name);
            }
            finally { _loading = false; }
        }

        private GroupSpec ReadSpec()
        {
            var t = Selected(_cmbTable) ?? throw new ArgumentException(LT("Choose a table.", "표를 고르세요."));
            var groups = _groupBy.CheckedItems.Cast<string>().ToList();
            var aggs = new List<GroupAggregate>();
            foreach (DataGridViewRow r in _agg.Rows)
            {
                if (r.Cells[0].Value is not GroupFunction f) continue;
                string col = Convert.ToString(r.Cells[1].Value) ?? "";
                string alias = (Convert.ToString(r.Cells[2].Value) ?? "").Trim();
                aggs.Add(new GroupAggregate(f, col.Length == 0 ? null : col, alias.Length == 0 ? null : alias));
            }
            return new GroupSpec(t, groups, aggs);
        }

        protected override string BuildSql() => WizardSql.GroupSql(ReadSpec());

        protected override string SuggestViewName()
            => SuggestName(SqlNames.Sanitize(Selected(_cmbTable)!.DisplayName + "_summary", "summary"), "summary");

        protected override string Summarize()
        {
            var spec = ReadSpec();
            string by = spec.GroupBy.Count == 0 ? LT("all rows", "전체") : string.Join(", ", spec.GroupBy);
            return LT($"Summary of {spec.Table.DisplayName} by {by}", $"{spec.Table.DisplayName} 그룹 집계 (기준: {by})");
        }

        protected override async Task<IReadOnlyList<DiagLine>> DiagnoseAsync(string sql, CancellationToken ct)
        {
            var spec = ReadSpec();
            string body = SqlText.StripTerminator(sql);
            var rowsTask = Workspace.CountRowsAsync(spec.Table, ct);
            var groupsTask = Workspace.PreviewAsync($"SELECT count(*) FROM (\n{body}\n) AS q", 1, ct);
            long rows = await rowsTask;
            long groups = long.Parse((await groupsTask).Rows[0][0]!);
            var lines = new List<DiagLine>
            {
                new(LT($"{N(rows)} rows → {N(groups)} result rows", $"{N(rows)}행 → 결과 {N(groups)}행"), DiagLevel.Good),
            };
            if (spec.GroupBy.Count > 0 && groups == rows && rows > 1)
                lines.Add(new DiagLine(LT("Every row is its own group — the group columns are unique, so nothing is summarized.", "모든 행이 각각 하나의 그룹입니다 — 그룹 컬럼이 유일해서 집계되는 것이 없습니다."), DiagLevel.Warn));
            if (spec.GroupBy.Count > 0)
                lines.Add(new DiagLine(LT("Rows where a group column is empty (NULL) are summarized together as one NULL group.", "그룹 컬럼이 비어 있는(NULL) 행은 하나의 NULL 그룹으로 모아 집계합니다.")));
            return lines;
        }
    }
}
