using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 이름 한 줄을 묻는 작은 대화상자(작업 공간 이름 바꾸기·뷰로 저장). <paramref name="validate"/>는 입력이 쓸 수 있으면 null, 아니면 이유를 돌려준다.
    /// <see cref="ShowIncludeEditsOption"/>이 참이면 "저장 안 한 편집 포함" 체크 상자를 보인다(편집이 있는 탭이 없으면 비활성 + 이유 설명).
    /// </summary>
    internal sealed class NamePromptDialog : Form
    {
        private readonly TextBox _text;
        private readonly CheckBox? _includeEdits;
        private readonly Label _problem;
        private readonly Func<string, string?> _validate;

        public string Value => _text.Text.Trim();
        public bool IncludeUnsavedEdits => _includeEdits is { Enabled: true, Checked: true };

        private static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        public NamePromptDialog(string title, string prompt, string initial, ThemePalette palette, Func<string, string?> validate,
            bool showIncludeEditsOption = false, bool editsAvailable = false, bool includeEditsInitial = false, string? okText = null)
        {
            _validate = validate;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            Padding = new Padding(12);
            int height = showIncludeEditsOption ? 214 : 160;
            ClientSize = new Size(440, height);

            var label = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 40, ForeColor = palette.Text, Text = prompt };
            _text = new TextBox
            {
                Dock = DockStyle.Top, BackColor = palette.Surface, ForeColor = palette.Text, BorderStyle = BorderStyle.FixedSingle, Text = initial, Name = "nameText",
            };
            _problem = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 22, ForeColor = Color.FromArgb(200, 70, 70), Name = "nameProblem" };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 6, 0, 0) };
            var cancel = new Button { Text = LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(88, 28) };
            var ok = new Button { Text = okText ?? LT("OK", "확인"), Size = new Size(88, 28), Name = "nameOk" };
            ok.Click += (_, _) =>
            {
                string? problem = _validate(_text.Text.Trim());
                if (problem is not null) { _problem.Text = problem; return; }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            if (showIncludeEditsOption)
            {
                _includeEdits = new CheckBox
                {
                    Dock = DockStyle.Top, AutoSize = false, Height = 44, ForeColor = palette.Text, Name = "includeEdits",
                    Text = LT("Include unsaved edits (a snapshot of the edited tables is used each time the result is calculated)",
                              "저장 안 한 편집 포함 (결과를 계산할 때마다 편집한 표의 스냅숏을 씁니다)"),
                    Enabled = editsAvailable, Checked = editsAvailable && includeEditsInitial,
                };
                if (!editsAvailable)
                    new ToolTip().SetToolTip(_includeEdits, LT("No open tab has unsaved edits right now. The view reads the saved files.",
                                                               "지금 저장 안 한 편집이 있는 탭이 없습니다. 뷰는 저장된 파일을 읽습니다."));
                Controls.Add(_includeEdits);
            }
            Controls.Add(_problem);
            Controls.Add(_text);
            Controls.Add(label);
            Controls.Add(buttons);
            _text.TextChanged += (_, _) => _problem.Text = _validate(_text.Text.Trim()) ?? "";
            Shown += (_, _) => { _text.Focus(); _text.SelectAll(); _problem.Text = _validate(_text.Text.Trim()) ?? ""; };
        }
    }

    /// <summary>형 변환 컬럼의 변환 실패 보고(표별): 컬럼·타입·비어 있지 않은 값 수·실패 수·실패 예시.</summary>
    internal sealed class CastReportDialog : Form
    {
        private static string LT(string en, string ko) => ViewerSupport.LT(en, ko);

        public CastReportDialog(string title, IReadOnlyList<ColumnCastReport> reports, ThemePalette palette)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            ShowIcon = false;
            ShowInTaskbar = false;
            MinimizeBox = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(760, 380);

            long failures = reports.Sum(r => r.FailureCount);
            var summary = new Label
            {
                Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 6, 8, 0), ForeColor = palette.Text, Name = "castSummary",
                Text = reports.Count == 0
                    ? LT("This table has no converted (typed) columns, so there is nothing that could fail to convert.",
                         "이 표에는 형 변환한 컬럼이 없어 변환 실패가 생길 수 없습니다.")
                    : failures == 0
                        ? LT($"All {reports.Count} typed column(s) converted without failures.", $"형 변환 컬럼 {reports.Count}개 모두 실패 없이 변환되었습니다.")
                        : LT($"{failures:N0} value(s) could not be converted and became NULL in the typed columns. The original text stays available as <table>__raw.",
                             $"형 변환에 실패해 NULL이 된 값이 {failures:N0}개 있습니다. 원문은 <표>__raw 에 그대로 있습니다."),
            };
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = palette.GridBg, GridColor = palette.Border,
                BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, Name = "castGrid",
            };
            grid.DefaultCellStyle.BackColor = palette.GridBg;
            grid.DefaultCellStyle.ForeColor = palette.Text;
            grid.DefaultCellStyle.SelectionBackColor = palette.SelectionBg;
            grid.DefaultCellStyle.SelectionForeColor = palette.SelectionText;
            grid.ColumnHeadersDefaultCellStyle.BackColor = palette.HeaderBg;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.HeaderText;
            grid.Columns.Add("col", LT("Column", "컬럼"));
            grid.Columns.Add("type", LT("Type", "타입"));
            grid.Columns.Add("filled", LT("Non-empty", "값 있음"));
            grid.Columns.Add("fail", LT("Failed", "실패"));
            grid.Columns.Add("examples", LT("Failure examples", "실패 예시"));
            grid.Columns[0].FillWeight = 22; grid.Columns[1].FillWeight = 12; grid.Columns[2].FillWeight = 12; grid.Columns[3].FillWeight = 10; grid.Columns[4].FillWeight = 44;
            foreach (var r in reports)
            {
                int i = grid.Rows.Add(r.Column, r.Type.DisplayName(), r.NonEmptyCount.ToString("N0"), r.FailureCount.ToString("N0"), string.Join(" · ", r.FailureExamples));
                if (r.FailureCount > 0) grid.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(214, 90, 90);
            }
            var close = new Button { Text = LT("Close", "닫기"), DialogResult = DialogResult.OK, Size = new Size(88, 28) };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(8, 6, 8, 0) };
            bottom.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            Controls.Add(grid);
            Controls.Add(summary);
            Controls.Add(bottom);
        }
    }
}
