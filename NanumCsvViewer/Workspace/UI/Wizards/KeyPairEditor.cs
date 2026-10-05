using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 키 쌍 편집기(조인·비교 마법사 공용): "왼쪽 컬럼 [형] = 오른쪽 컬럼 [형]" 줄을 여러 개. 형이 서로 다르면 배지가 주황색이 되고
    /// (글자로 비교됨) 안내가 뜬다. "자동 제안"은 같은 이름·id처럼 보이는 컬럼 쌍을 채운다.
    /// </summary>
    internal sealed class KeyPairEditor : Panel
    {
        private static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        private sealed class Row
        {
            public required FlowLayoutPanel Panel;
            public required ComboBox Left, Right;
            public required Label LeftBadge, RightBadge;
        }

        private readonly ThemePalette _palette;
        private readonly FlowLayoutPanel _rows;
        private readonly Button _add, _suggest;
        private readonly Label _hint;
        private readonly ToolTip _tip = new();
        private readonly List<Row> _list = new();
        private IWorkspaceRelation? _leftRel, _rightRel;
        private bool _loading;

        public event EventHandler? PairsChanged;

        public KeyPairEditor(ThemePalette palette)
        {
            _palette = palette;
            _rows = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            _add = new Button { Text = LT("+ Add key pair", "+ 키 쌍 추가"), Size = new Size(130, 26), Name = "keyAdd" };
            _suggest = new Button { Text = LT("Suggest keys", "키 자동 제안"), Size = new Size(120, 26), Name = "keySuggest" };
            _hint = new Label { AutoSize = true, Margin = new Padding(8, 6, 0, 0), Name = "keyHint" };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
            bar.Controls.AddRange(new Control[] { _add, _suggest, _hint });
            Controls.Add(_rows);
            Controls.Add(bar);
            _add.Click += (_, _) => { AddRow(null, null); Raise(); };
            _suggest.Click += (_, _) => { Suggest(); };
            Height = 130;
        }

        /// <summary>점검: 완성되지 않은 줄(한쪽만 고른 줄)이 있는가.</summary>
        public bool HasIncompleteRow => _list.Any(r => (r.Left.SelectedItem is null) != (r.Right.SelectedItem is null));

        public IReadOnlyList<JoinKey> Pairs => _list
            .Where(r => r.Left.SelectedItem is not null && r.Right.SelectedItem is not null)
            .Select(r => new JoinKey((string)r.Left.SelectedItem!, (string)r.Right.SelectedItem!)).ToList();

        public void SetRelations(IWorkspaceRelation? left, IWorkspaceRelation? right, bool suggest)
        {
            _leftRel = left; _rightRel = right;
            _loading = true;
            try
            {
                foreach (var r in _list.ToList()) RemoveRowCore(r);
                if (left is not null && right is not null)
                {
                    var keys = suggest ? WizardSql.SuggestKeys(left, right) : Array.Empty<JoinKey>();
                    if (keys.Count == 0) AddRow(null, null);
                    foreach (var k in keys) AddRow(k.LeftColumn, k.RightColumn);
                }
            }
            finally { _loading = false; }
            UpdateHint();
            Raise();
        }

        private void Suggest()
        {
            if (_leftRel is null || _rightRel is null) return;
            _loading = true;
            try
            {
                foreach (var r in _list.ToList()) RemoveRowCore(r);
                var keys = WizardSql.SuggestKeys(_leftRel, _rightRel);
                if (keys.Count == 0) AddRow(null, null);
                foreach (var k in keys) AddRow(k.LeftColumn, k.RightColumn);
            }
            finally { _loading = false; }
            UpdateBadges();
            if (Pairs.Count == 0)
                _hint.Text = LT("No same-named columns found — pick the key columns yourself.", "이름이 같은 컬럼이 없습니다 — 키 컬럼을 직접 고르세요.");
            Raise();
        }

        private void AddRow(string? left, string? right)
        {
            var lc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 2, 4, 2) };
            var rc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(4, 2, 4, 2) };
            if (_leftRel is not null) foreach (var c in _leftRel.Columns) lc.Items.Add(c.Name);
            if (_rightRel is not null) foreach (var c in _rightRel.Columns) rc.Items.Add(c.Name);
            if (left is not null) lc.SelectedItem = left;
            if (right is not null) rc.SelectedItem = right;
            var lb = new Label { AutoSize = false, Width = 74, Height = 22, TextAlign = ContentAlignment.MiddleCenter, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 0, 2) };
            var rb = new Label { AutoSize = false, Width = 74, Height = 22, TextAlign = ContentAlignment.MiddleCenter, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 0, 2) };
            var eq = new Label { Text = "=", AutoSize = true, Margin = new Padding(6, 6, 6, 0) };
            var remove = new Button { Text = "×", Size = new Size(28, 26), Margin = new Padding(8, 1, 0, 0) };
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            panel.Controls.AddRange(new Control[] { lc, lb, eq, rc, rb, remove });
            var row = new Row { Panel = panel, Left = lc, Right = rc, LeftBadge = lb, RightBadge = rb };
            _list.Add(row);
            _rows.Controls.Add(panel);
            WizardStyle.Apply(panel, _palette);
            WizardStyle.Apply(_rows, _palette);
            lc.SelectedIndexChanged += (_, _) => { if (_loading) return; UpdateBadges(); Raise(); };
            rc.SelectedIndexChanged += (_, _) => { if (_loading) return; UpdateBadges(); Raise(); };
            remove.Click += (_, _) => { RemoveRowCore(row); if (_list.Count == 0) AddRow(null, null); UpdateBadges(); Raise(); };
            UpdateBadges();
        }

        private void RemoveRowCore(Row r)
        {
            _list.Remove(r);
            _rows.Controls.Remove(r.Panel);
            r.Panel.Dispose();
        }

        private static string Badge(WorkspaceColumn? c) => c is null ? "" : c.SqlType.ToLowerInvariant();

        private void UpdateBadges()
        {
            int mismatches = 0;
            foreach (var r in _list)
            {
                var lcol = r.Left.SelectedItem is string ln ? _leftRel?.Columns.FirstOrDefault(c => c.Name == ln) : null;
                var rcol = r.Right.SelectedItem is string rn ? _rightRel?.Columns.FirstOrDefault(c => c.Name == rn) : null;
                r.LeftBadge.Text = Badge(lcol);
                r.RightBadge.Text = Badge(rcol);
                bool mismatch = lcol is not null && rcol is not null && !WizardStyle.TypesCompatible(lcol, rcol);
                var color = mismatch ? WizardStyle.LevelColor(_palette, DiagLevel.Warn) : _palette.Text;
                r.LeftBadge.ForeColor = color; r.RightBadge.ForeColor = color;
                r.LeftBadge.BackColor = _palette.Surface; r.RightBadge.BackColor = _palette.Surface;
                _tip.SetToolTip(r.LeftBadge, mismatch ? LT("Different types — compared as text (001 ≠ 1).", "형이 달라 글자로 비교합니다 (001 ≠ 1).") : "");
                _tip.SetToolTip(r.RightBadge, mismatch ? LT("Different types — compared as text (001 ≠ 1).", "형이 달라 글자로 비교합니다 (001 ≠ 1).") : "");
                if (mismatch) mismatches++;
            }
            UpdateHint(mismatches);
        }

        private void UpdateHint(int mismatches = -1)
        {
            if (mismatches < 0) mismatches = _list.Count(r => r.LeftBadge.ForeColor != _palette.Text);
            _hint.ForeColor = WizardStyle.LevelColor(_palette, DiagLevel.Warn);
            _hint.Text = mismatches > 0 ? LT("Orange type = different types, compared as text.", "주황색 형 = 형이 달라 글자로 비교됩니다.") : "";
        }

        private void Raise() => PairsChanged?.Invoke(this, EventArgs.Empty);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
