using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 조인 마법사: 왼쪽·오른쪽 표, 키 쌍(여러 개·형 배지·자동 제안), 조인 종류, 결과 컬럼 선택(겹치는 이름은 접두어·별칭),
    /// 키 진단(행 수·일치·한쪽에만 있는 키·중복 키·NULL 키·예상 행 수와 행 증가 경고·형 불일치), 미리보기, 편집 가능한 SQL.
    /// </summary>
    internal sealed class JoinWizardDialog : WizardForm
    {
        private sealed record KindItem(JoinKind Kind, string Text)
        {
            public override string ToString() => Text;
        }

        private readonly IReadOnlyList<IWorkspaceRelation> _relations;
        private readonly ComboBox _cmbLeft, _cmbRight, _cmbKind;
        private readonly KeyPairEditor _keys;
        private readonly TextBox _tbLeftPrefix, _tbRightPrefix;
        private readonly CheckBox _cbOnlyClash;
        private readonly DataGridView _cols;
        private readonly Dictionary<(bool Left, string Column), bool> _include = new();
        private readonly Dictionary<(bool Left, string Column), string> _names = new();
        private List<WizardSql.JoinOutputColumn> _defaults = new();
        private bool _rebuilding;

        public JoinWizardDialog(ThemePalette palette, DataWorkspace ws, string? preselect)
            : base(palette, ws, LT("Join tables", "조인 (표 합치기)"), LT("Use this join", "이 조인 사용"), 350)
        {
            _relations = WizardStyle.Relations(ws);
            Name = "joinWizard";

            _cmbLeft = MakeRelationCombo(_relations, "joinLeft");
            _cmbRight = MakeRelationCombo(_relations, "joinRight");
            _cmbKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Name = "joinKind", Margin = new Padding(0, 3, 0, 0) };
            _cmbKind.Items.Add(new KindItem(JoinKind.Inner, LT("Inner — only rows with a match on both sides", "내부 — 양쪽에 짝이 있는 행만")));
            _cmbKind.Items.Add(new KindItem(JoinKind.Left, LT("Left — every row of the left table", "왼쪽 — 왼쪽 표의 모든 행")));
            _cmbKind.Items.Add(new KindItem(JoinKind.Right, LT("Right — every row of the right table", "오른쪽 — 오른쪽 표의 모든 행")));
            _cmbKind.Items.Add(new KindItem(JoinKind.Full, LT("Full — every row of both tables", "전체 — 양쪽 표의 모든 행")));
            _cmbKind.SelectedIndex = 0;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 4) };
            top.Controls.AddRange(new Control[]
            {
                MakeLabel(LT("Left table", "왼쪽 표")), _cmbLeft, MakeLabel(LT("Right table", "오른쪽 표")), _cmbRight, MakeLabel(LT("Join type", "조인 종류")), _cmbKind,
            });

            _keys = new KeyPairEditor(palette) { Dock = DockStyle.Top, Height = 92, Name = "joinKeys" };

            _tbLeftPrefix = new TextBox { Width = 90, Name = "joinLeftPrefix", Margin = new Padding(0, 3, 12, 0) };
            _tbRightPrefix = new TextBox { Width = 90, Name = "joinRightPrefix", Margin = new Padding(0, 3, 12, 0) };
            _cbOnlyClash = new CheckBox { Text = LT("only for clashing names", "이름이 겹치는 컬럼에만"), AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0), Name = "joinOnlyClash" };
            var prefixRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
            prefixRow.Controls.AddRange(new Control[]
            {
                MakeLabel(LT("Prefix for left columns", "왼쪽 컬럼 접두어")), _tbLeftPrefix, MakeLabel(LT("for right columns", "오른쪽 컬럼 접두어")), _tbRightPrefix, _cbOnlyClash,
            });

            _cols = new DataGridView
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, Name = "joinColumns", EditMode = DataGridViewEditMode.EditOnEnter,
            };
            _cols.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = LT("Use", "사용"), FillWeight = 8, Name = "use" });
            _cols.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = LT("Table", "표"), ReadOnly = true, FillWeight = 24, Name = "side" });
            _cols.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = LT("Column", "컬럼"), ReadOnly = true, FillWeight = 28, Name = "col" });
            _cols.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = LT("Type", "형"), ReadOnly = true, FillWeight = 12, Name = "type" });
            _cols.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = LT("Name in result", "결과 컬럼 이름"), FillWeight = 28, Name = "out" });

            OptionsHost.Controls.Add(_cols);
            OptionsHost.Controls.Add(prefixRow);
            OptionsHost.Controls.Add(new Label { Text = LT("Result columns", "결과 컬럼"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });
            OptionsHost.Controls.Add(_keys);
            OptionsHost.Controls.Add(new Label { Text = LT("Key pairs (rows match when all pairs are equal)", "키 쌍 (모든 쌍이 같으면 짝이 됩니다)"), Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft });
            OptionsHost.Controls.Add(top);

            var preLeft = FindByName(_relations, preselect) ?? _relations.FirstOrDefault();
            SelectRelation(_cmbLeft, preLeft);
            SelectRelation(_cmbRight, _relations.FirstOrDefault(r => !ReferenceEquals(r, preLeft)) ?? preLeft);
            _keys.SetRelations(Selected(_cmbLeft), Selected(_cmbRight), true);
            RebuildColumns();

            _cmbLeft.SelectedIndexChanged += (_, _) => OnTablesChanged();
            _cmbRight.SelectedIndexChanged += (_, _) => OnTablesChanged();
            _cmbKind.SelectedIndexChanged += (_, _) => { RebuildColumns(); Changed(); };
            _keys.PairsChanged += (_, _) => { RebuildColumns(); Changed(); };
            _tbLeftPrefix.TextChanged += (_, _) => { RebuildColumns(); Changed(); };
            _tbRightPrefix.TextChanged += (_, _) => { RebuildColumns(); Changed(); };
            _cbOnlyClash.CheckedChanged += (_, _) => { RebuildColumns(); Changed(); };
            _cols.CurrentCellDirtyStateChanged += (_, _) => { if (_cols.IsCurrentCellDirty && _cols.CurrentCell is DataGridViewCheckBoxCell) _cols.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            _cols.CellValueChanged += OnColumnEdited;
        }

        private JoinKind Kind => (_cmbKind.SelectedItem as KindItem)?.Kind ?? JoinKind.Inner;

        private void OnTablesChanged()
        {
            _include.Clear();
            _names.Clear();
            _keys.SetRelations(Selected(_cmbLeft), Selected(_cmbRight), true); // PairsChanged가 컬럼 목록·SQL을 다시 만든다.
        }

        private bool IsRightKey(string column)
            => _keys.Pairs.Any(p => string.Equals(p.RightColumn, column, StringComparison.OrdinalIgnoreCase));

        private void RebuildColumns()
        {
            _rebuilding = true;
            try
            {
                _cols.Rows.Clear();
                var l = Selected(_cmbLeft);
                var r = Selected(_cmbRight);
                if (l is null || r is null) { _defaults = new(); return; }
                _defaults = WizardSql.DefaultJoinColumns(l, r, _tbLeftPrefix.Text.Trim(), _tbRightPrefix.Text.Trim(), _cbOnlyClash.Checked).ToList();
                bool dropRightKeys = Kind is JoinKind.Inner or JoinKind.Left; // 안쪽·왼쪽 조인에서는 오른쪽 키가 왼쪽 키와 같은 값이다
                foreach (var d in _defaults)
                {
                    var rel = d.FromLeft ? l : r;
                    var col = rel.Columns.First(c => string.Equals(c.Name, d.Column, StringComparison.OrdinalIgnoreCase));
                    var key = (d.FromLeft, d.Column);
                    bool use = _include.TryGetValue(key, out bool u) ? u : !( !d.FromLeft && dropRightKeys && IsRightKey(d.Column));
                    string name = _names.TryGetValue(key, out string? n) ? n : d.OutputName;
                    _cols.Rows.Add(use, rel.DisplayName, d.Column, col.SqlType.ToLowerInvariant(), name);
                }
            }
            finally { _rebuilding = false; }
        }

        private void OnColumnEdited(object? sender, DataGridViewCellEventArgs e)
        {
            if (_rebuilding || e.RowIndex < 0 || e.RowIndex >= _defaults.Count) return;
            var d = _defaults[e.RowIndex];
            var key = (d.FromLeft, d.Column);
            var row = _cols.Rows[e.RowIndex];
            if (e.ColumnIndex == 0) _include[key] = row.Cells[0].Value is true;
            else if (e.ColumnIndex == 4)
            {
                string v = Convert.ToString(row.Cells[4].Value) ?? "";
                if (v == d.OutputName) _names.Remove(key); else _names[key] = v;
            }
            else return;
            Changed();
        }

        private JoinSpec? ReadSpec()
        {
            var l = Selected(_cmbLeft);
            var r = Selected(_cmbRight);
            if (l is null || r is null) throw new ArgumentException(LT("Choose the left and the right table.", "왼쪽 표와 오른쪽 표를 고르세요."));
            if (_keys.HasIncompleteRow) throw new ArgumentException(LT("Finish or remove the incomplete key pair.", "덜 고른 키 쌍을 마저 고르거나 지우세요."));
            var pairs = _keys.Pairs;
            if (pairs.Count == 0) throw new ArgumentException(LT("Choose at least one key pair.", "키 쌍을 하나 이상 고르세요."));
            return new JoinSpec(l, r, pairs, Kind);
        }

        protected override string BuildSql()
        {
            var spec = ReadSpec()!;
            var chosen = new List<WizardSql.JoinOutputColumn>();
            for (int i = 0; i < _defaults.Count && i < _cols.Rows.Count; i++)
            {
                var row = _cols.Rows[i];
                if (row.Cells[0].Value is true)
                    chosen.Add(new WizardSql.JoinOutputColumn(_defaults[i].FromLeft, _defaults[i].Column, (Convert.ToString(row.Cells[4].Value) ?? "").Trim()));
            }
            return WizardSql.JoinSelectSql(spec, chosen);
        }

        protected override string SuggestViewName()
        {
            var l = Selected(_cmbLeft)!; var r = Selected(_cmbRight)!;
            return SuggestName(SqlNames.Sanitize(l.DisplayName + "_" + r.DisplayName, "joined"), "joined");
        }

        protected override string Summarize()
        {
            var spec = ReadSpec()!;
            string keys = string.Join(", ", spec.Keys.Select(k => $"{k.LeftColumn} = {k.RightColumn}"));
            string kind = spec.Kind switch
            {
                JoinKind.Left => LT("left", "왼쪽"), JoinKind.Right => LT("right", "오른쪽"), JoinKind.Full => LT("full", "전체"), _ => LT("inner", "내부"),
            };
            return LT($"Join {spec.Left.DisplayName} with {spec.Right.DisplayName} ({kind}) on {keys}",
                      $"{spec.Left.DisplayName} ↔ {spec.Right.DisplayName} 조인({kind}) 키: {keys}");
        }

        protected override async Task<IReadOnlyList<DiagLine>> DiagnoseAsync(string sql, CancellationToken ct)
        {
            var spec = ReadSpec()!;
            var d = await Workspace.CheckJoinAsync(spec, ct);
            return Describe(d, spec);
        }

        // ---- 진단 문구 (순수 — 테스트에서 직접 검증) ---------------------------------------------------

        /// <summary>
        /// 행 증가 배율: 예상 행 수 ÷ 중복 키 없이 나올 행 수의 상한. 기준은 조인 종류별로 안쪽 = max(왼쪽, 오른쪽), 왼쪽 = 왼쪽 행 수,
        /// 오른쪽 = 오른쪽 행 수, 전체 = 왼쪽 + 오른쪽. 1보다 크면 중복 키 때문에 행이 불어난 것이다.
        /// </summary>
        internal static double GrowthFactor(JoinDiagnostics d)
        {
            double baseline = d.Kind switch
            {
                JoinKind.Left => d.LeftRows,
                JoinKind.Right => d.RightRows,
                JoinKind.Full => d.LeftRows + d.RightRows,
                _ => Math.Max(d.LeftRows, d.RightRows),
            };
            return baseline <= 0 ? 1 : d.ExpectedRows / baseline;
        }

        internal static IReadOnlyList<DiagLine> Describe(JoinDiagnostics d, JoinSpec spec)
        {
            var lines = new List<DiagLine>();
            string left = spec.Left.DisplayName, right = spec.Right.DisplayName;
            lines.Add(new DiagLine(LT($"Rows: {left} {N(d.LeftRows)} · {right} {N(d.RightRows)}", $"행 수: {left} {N(d.LeftRows)} · {right} {N(d.RightRows)}")));

            if (d.MatchedKeys == 0)
                lines.Add(new DiagLine(LT($"No keys match between the two tables — check the key columns.{(d.Kind == JoinKind.Inner ? " The result will be empty." : "")}",
                                          $"두 표 사이에 일치하는 키가 하나도 없습니다 — 키 컬럼을 확인하세요.{(d.Kind == JoinKind.Inner ? " 결과가 비게 됩니다." : "")}"), DiagLevel.Strong));
            else
                lines.Add(new DiagLine(LT($"Keys: {N(d.MatchedKeys)} match · {N(d.LeftOnlyKeys)} only in {left} · {N(d.RightOnlyKeys)} only in {right}",
                                          $"키: {N(d.MatchedKeys)}개 일치 · {left}에만 {N(d.LeftOnlyKeys)}개 · {right}에만 {N(d.RightOnlyKeys)}개"),
                    d.LeftOnlyKeys + d.RightOnlyKeys > d.MatchedKeys ? DiagLevel.Warn : DiagLevel.Good));

            if (d.LeftDuplicateKeys > 0 || d.RightDuplicateKeys > 0)
                lines.Add(new DiagLine(LT(
                    $"Duplicate keys: {left} {N(d.LeftDuplicateKeys)} (up to {N(d.LeftMaxKeyRows)} rows per key) · {right} {N(d.RightDuplicateKeys)} (up to {N(d.RightMaxKeyRows)} rows per key)",
                    $"중복 키: {left} {N(d.LeftDuplicateKeys)}개(키 하나에 최대 {N(d.LeftMaxKeyRows)}행) · {right} {N(d.RightDuplicateKeys)}개(키 하나에 최대 {N(d.RightMaxKeyRows)}행)"), DiagLevel.Warn));
            if (d.LeftNullKeyRows > 0 || d.RightNullKeyRows > 0)
                lines.Add(new DiagLine(LT(
                    $"Rows with an empty (NULL) key never match: {left} {N(d.LeftNullKeyRows)} · {right} {N(d.RightNullKeyRows)}",
                    $"키가 비어 있는(NULL) 행은 짝이 되지 않습니다: {left} {N(d.LeftNullKeyRows)}행 · {right} {N(d.RightNullKeyRows)}행"), DiagLevel.Warn));

            string card = d.Cardinality switch
            {
                JoinCardinality.OneToOne => LT("one-to-one", "일대일"),
                JoinCardinality.OneToMany => LT($"one-to-many (one {left} row ↔ several {right} rows)", $"일대다 ({left} 한 행 ↔ {right} 여러 행)"),
                JoinCardinality.ManyToOne => LT($"many-to-one (several {left} rows ↔ one {right} row)", $"다대일 ({left} 여러 행 ↔ {right} 한 행)"),
                _ => LT("many-to-many — rows multiply", "다대다 — 행이 곱으로 늘어납니다"),
            };
            lines.Add(new DiagLine(LT("Relationship: ", "관계: ") + card, d.Cardinality == JoinCardinality.ManyToMany ? DiagLevel.Bad : DiagLevel.Normal));

            double g = GrowthFactor(d);
            string gx = g.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (g > 1.5)
                lines.Add(new DiagLine(LT(
                    $"⚠ Expected result: {N(d.ExpectedRows)} rows — ×{gx} the row count it should have. Duplicate keys multiply rows; make a key unique or add key columns before using this join.",
                    $"⚠ 예상 결과: {N(d.ExpectedRows)}행 — 있어야 할 행 수의 {gx}배입니다. 중복 키 때문에 행이 크게 불어납니다. 키를 유일하게 만들거나 키 컬럼을 더하세요."), DiagLevel.Strong));
            else if (g > 1.0)
                lines.Add(new DiagLine(LT(
                    $"Expected result: {N(d.ExpectedRows)} rows (×{gx} — more rows than the inputs, caused by duplicate keys)",
                    $"예상 결과: {N(d.ExpectedRows)}행 (×{gx} — 중복 키 때문에 입력보다 행이 늘어남)"), DiagLevel.Bad));
            else
                lines.Add(new DiagLine(LT($"Expected result: {N(d.ExpectedRows)} rows", $"예상 결과: {N(d.ExpectedRows)}행"), DiagLevel.Good));

            foreach (var w in d.KeyTypeWarnings) lines.Add(new DiagLine("⚠ " + w, DiagLevel.Warn));
            return lines;
        }
    }
}
