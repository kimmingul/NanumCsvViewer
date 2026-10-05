using System.Globalization;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 비교 마법사: 이전(왼쪽)·이후(오른쪽) 표, 키 쌍, 비교할 컬럼, 옵션(대소문자·공백 무시, 변경 없음 포함, 긴 형식),
    /// 요약(추가·삭제·변경·변경 없음, 중복 키, NULL 키, 컬럼별 바뀐 셀 수), 미리보기(바뀐 셀 상세), 편집 가능한 SQL.
    /// 값 비교는 NULL 안전(NULL = NULL은 같음).
    /// </summary>
    internal sealed class CompareWizardDialog : WizardForm
    {
        private readonly IReadOnlyList<IWorkspaceRelation> _relations;
        private readonly ComboBox _cmbLeft, _cmbRight;
        private readonly KeyPairEditor _keys;
        private readonly CheckedListBox _columns;
        private readonly CheckBox _cbCase, _cbSpace, _cbUnchanged, _cbLong;
        private readonly HashSet<string> _unchecked = new(StringComparer.OrdinalIgnoreCase);
        private bool _loading;

        public CompareWizardDialog(ThemePalette palette, DataWorkspace ws, string? preselect)
            : base(palette, ws, LT("Compare tables", "비교 (두 표의 차이)"), LT("Use this comparison", "이 비교 사용"), 360)
        {
            _relations = WizardStyle.Relations(ws);
            Name = "compareWizard";

            _cmbLeft = MakeRelationCombo(_relations, "compareOld");
            _cmbRight = MakeRelationCombo(_relations, "compareNew");
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
            top.Controls.AddRange(new Control[] { MakeLabel(LT("Old table (before)", "이전 표")), _cmbLeft, MakeLabel(LT("New table (after)", "이후 표")), _cmbRight });

            _keys = new KeyPairEditor(palette) { Dock = DockStyle.Top, Height = 92, Name = "compareKeys" };

            _columns = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, Name = "compareColumns", BorderStyle = BorderStyle.FixedSingle };
            var colPanel = new Panel { Dock = DockStyle.Left, Width = 340, Padding = new Padding(0, 0, 10, 0) };
            colPanel.Controls.Add(_columns);
            colPanel.Controls.Add(new Label { Text = LT("Columns to compare (same names in both tables)", "비교할 컬럼 (두 표에 같은 이름이 있는 컬럼)"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });

            _cbCase = new CheckBox { Text = LT("Ignore upper/lower case in text", "글자의 대소문자 무시"), AutoSize = true, Name = "compareIgnoreCase" };
            _cbSpace = new CheckBox { Text = LT("Ignore extra spaces in text (leading/trailing, repeated)", "글자의 불필요한 공백 무시 (앞뒤·연속 공백)"), AutoSize = true, Name = "compareIgnoreSpace" };
            _cbUnchanged = new CheckBox { Text = LT("Include unchanged rows in the result", "결과에 변경 없는 행도 포함"), AutoSize = true, Name = "compareUnchanged" };
            _cbLong = new CheckBox { Text = LT("One row per changed cell (long format)", "바뀐 셀마다 한 행 (긴 형식)"), AutoSize = true, Name = "compareLong" };
            var note = new Label
            {
                AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 8, 0, 0),
                Text = LT("Rows are paired by the key columns. NULL = NULL counts as equal. Keys that repeat in either table are reported as duplicate_key and not compared.",
                          "키 컬럼으로 행을 짝지웁니다. NULL = NULL은 같은 값으로 봅니다. 어느 한쪽에서 키가 중복되면 비교하지 않고 duplicate_key로 알립니다."),
            };
            var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            opts.Controls.AddRange(new Control[] { _cbCase, _cbSpace, _cbUnchanged, _cbLong, note });

            var bottom = new Panel { Dock = DockStyle.Fill };
            bottom.Controls.Add(opts);
            bottom.Controls.Add(colPanel);

            OptionsHost.Controls.Add(bottom);
            OptionsHost.Controls.Add(_keys);
            OptionsHost.Controls.Add(new Label { Text = LT("Key columns (identify the same row in both tables)", "키 컬럼 (두 표에서 같은 행을 가리키는 컬럼)"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });
            OptionsHost.Controls.Add(top);

            var pre = FindByName(_relations, preselect) ?? _relations.FirstOrDefault();
            SelectRelation(_cmbLeft, pre);
            SelectRelation(_cmbRight, _relations.Where(r => !ReferenceEquals(r, pre))
                .OrderByDescending(r => pre is null ? 0 : r.Columns.Count(c => pre.Columns.Any(p => string.Equals(p.Name, c.Name, StringComparison.OrdinalIgnoreCase)))).FirstOrDefault() ?? pre);
            _keys.SetRelations(Selected(_cmbLeft), Selected(_cmbRight), true);
            RebuildColumns();

            _cmbLeft.SelectedIndexChanged += (_, _) => OnTablesChanged();
            _cmbRight.SelectedIndexChanged += (_, _) => OnTablesChanged();
            _keys.PairsChanged += (_, _) => { RebuildColumns(); Changed(); };
            _columns.ItemCheck += (_, e) =>
            {
                if (_loading) return;
                string name = (string)_columns.Items[e.Index];
                if (e.NewValue == CheckState.Unchecked) _unchecked.Add(name); else _unchecked.Remove(name);
                BeginInvoke(new Action(Changed));
            };
            foreach (var cb in new[] { _cbCase, _cbSpace, _cbUnchanged, _cbLong }) cb.CheckedChanged += (_, _) => Changed();
        }

        private void OnTablesChanged()
        {
            _unchecked.Clear();
            _keys.SetRelations(Selected(_cmbLeft), Selected(_cmbRight), true);
        }

        private void RebuildColumns()
        {
            _loading = true;
            try
            {
                _columns.Items.Clear();
                var l = Selected(_cmbLeft);
                var r = Selected(_cmbRight);
                if (l is null || r is null) return;
                var keyNames = new HashSet<string>(_keys.Pairs.SelectMany(k => new[] { k.LeftColumn, k.RightColumn }), StringComparer.OrdinalIgnoreCase);
                foreach (var c in l.Columns)
                {
                    if (keyNames.Contains(c.Name) || !r.Columns.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    _columns.Items.Add(c.Name, !_unchecked.Contains(c.Name));
                }
            }
            finally { _loading = false; }
        }

        private CompareSpec ReadSpec()
        {
            var l = Selected(_cmbLeft);
            var r = Selected(_cmbRight);
            if (l is null || r is null) throw new ArgumentException(LT("Choose the old and the new table.", "이전 표와 이후 표를 고르세요."));
            if (ReferenceEquals(l, r)) throw new ArgumentException(LT("Choose two different tables.", "서로 다른 두 표를 고르세요."));
            if (_keys.HasIncompleteRow) throw new ArgumentException(LT("Finish or remove the incomplete key pair.", "덜 고른 키 쌍을 마저 고르거나 지우세요."));
            var keys = _keys.Pairs;
            if (keys.Count == 0) throw new ArgumentException(LT("Choose at least one key column.", "키 컬럼을 하나 이상 고르세요."));
            var cols = new List<JoinKey>();
            foreach (var item in _columns.CheckedItems)
            {
                string name = (string)item;
                var rc = r.Columns.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                cols.Add(new JoinKey(name, rc.Name));
            }
            return new CompareSpec(l, r, keys, cols, _cbCase.Checked, _cbSpace.Checked, _cbUnchanged.Checked, _cbLong.Checked);
        }

        protected override string BuildSql() => WizardSql.CompareSql(ReadSpec());

        protected override string SuggestViewName()
        {
            var spec = ReadSpec();
            return SuggestName(SqlNames.Sanitize(spec.Left.DisplayName + "_vs_" + spec.Right.DisplayName, "compare"), "compare");
        }

        protected override string Summarize()
        {
            var spec = ReadSpec();
            string keys = string.Join(", ", spec.Keys.Select(k => k.LeftColumn == k.RightColumn ? k.LeftColumn : $"{k.LeftColumn}={k.RightColumn}"));
            return LT($"Compare {spec.Left.DisplayName} → {spec.Right.DisplayName} by {keys}", $"{spec.Left.DisplayName} → {spec.Right.DisplayName} 비교 (키: {keys})");
        }

        protected override async Task<IReadOnlyList<DiagLine>> DiagnoseAsync(string sql, CancellationToken ct)
        {
            var spec = ReadSpec();
            var warnings = WizardSql.CompareWarnings(spec);
            var names = WizardSql.ResolveCompareColumns(spec);
            var p = await Workspace.PreviewAsync(WizardSql.CompareSummarySql(spec), 1, ct);
            var v = new Dictionary<string, long>();
            for (int i = 0; i < p.Columns.Count; i++) v[p.Columns[i].Name] = long.Parse(p.Rows[0][i] ?? "0", CultureInfo.InvariantCulture);
            return Describe(spec, names, v, warnings);
        }

        /// <summary>요약 SQL 한 행(컬럼 이름 → 값)을 진단 문구로 바꾼다. 순수 — 테스트에서 직접 검증한다.</summary>
        internal static IReadOnlyList<DiagLine> Describe(CompareSpec spec, IReadOnlyList<JoinKey> compared, IReadOnlyDictionary<string, long> v, IReadOnlyList<string> warnings)
        {
            string o = spec.Left.DisplayName, n = spec.Right.DisplayName;
            var lines = new List<DiagLine>
            {
                new(LT($"Rows: {o} {N(v["left_rows"])} · {n} {N(v["right_rows"])}", $"행 수: {o} {N(v["left_rows"])} · {n} {N(v["right_rows"])}")),
                new(LT($"Added (only in {n}): {N(v["added"])}", $"추가됨 ({n}에만 있음): {N(v["added"])}")),
                new(LT($"Removed (only in {o}): {N(v["removed"])}", $"삭제됨 ({o}에만 있음): {N(v["removed"])}")),
                new(LT($"Changed: {N(v["changed"])}", $"변경됨: {N(v["changed"])}"), v["changed"] > 0 ? DiagLevel.Warn : DiagLevel.Normal),
                new(LT($"Unchanged: {N(v["unchanged"])}", $"변경 없음: {N(v["unchanged"])}"), DiagLevel.Good),
            };
            if (v["duplicate_keys"] > 0)
                lines.Add(new DiagLine(LT(
                    $"{N(v["duplicate_keys"])} key(s) repeat ({N(v["duplicate_left_rows"])} rows in {o}, {N(v["duplicate_right_rows"])} rows in {n}). They are NOT compared and appear as duplicate_key — add more key columns to make the key unique.",
                    $"중복된 키 {N(v["duplicate_keys"])}개({o} {N(v["duplicate_left_rows"])}행, {n} {N(v["duplicate_right_rows"])}행)는 비교하지 않고 duplicate_key로 표시합니다 — 키 컬럼을 더해 유일하게 만드세요."), DiagLevel.Bad));
            if (v["null_key_left_rows"] > 0 || v["null_key_right_rows"] > 0)
                lines.Add(new DiagLine(LT(
                    $"Rows with an empty (NULL) key cannot be paired and count as removed/added: {o} {N(v["null_key_left_rows"])} · {n} {N(v["null_key_right_rows"])}",
                    $"키가 비어 있는(NULL) 행은 짝지을 수 없어 삭제/추가로 셉니다: {o} {N(v["null_key_left_rows"])}행 · {n} {N(v["null_key_right_rows"])}행"), DiagLevel.Warn));

            var perColumn = new List<(string Name, long Count)>();
            for (int i = 0; i < compared.Count; i++)
                if (v.TryGetValue($"changed_cells_{i}", out long c) && c > 0) perColumn.Add((compared[i].LeftColumn, c));
            if (perColumn.Count > 0)
            {
                long cells = perColumn.Sum(x => x.Count);
                string list = string.Join(" · ", perColumn.OrderByDescending(x => x.Count).Take(8).Select(x => $"{x.Name} {N(x.Count)}"));
                lines.Add(new DiagLine(LT($"Changed cells: {N(cells)} — {list}{(perColumn.Count > 8 ? " …" : "")}", $"바뀐 셀 {N(cells)}개 — {list}{(perColumn.Count > 8 ? " …" : "")}")));
            }
            else if (compared.Count == 0)
                lines.Add(new DiagLine(LT("No columns are selected for comparison — only added/removed rows are found.", "비교할 컬럼이 없어 추가·삭제된 행만 찾습니다."), DiagLevel.Warn));

            foreach (var w in warnings) lines.Add(new DiagLine("⚠ " + w, DiagLevel.Warn));
            return lines;
        }
    }
}
