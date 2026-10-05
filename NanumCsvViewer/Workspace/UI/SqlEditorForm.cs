using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// SQL 편집기 창(비모달, 메인 창 소유): <see cref="SqlEditorPanel"/>을 작업 공간에 연결한다.
    /// 실행 → 읽기 전용 Result 탭("Query N"), "뷰로 저장…" → 이름 묻고 뷰 만들기(뷰 탭으로 열림).
    /// <see cref="EditingView"/>이 있으면 그 뷰의 SQL을 고치는 창이다(저장하면 뷰를 갱신).
    /// </summary>
    internal sealed class SqlEditorForm : Form
    {
        private readonly Form1 _host;

        public SqlEditorPanel Panel { get; }

        /// <summary>고치는 중인 뷰(새 질의면 null).</summary>
        public WorkspaceView? EditingView { get; }

        public SqlEditorForm(Form1 host, DataWorkspace workspace, ThemePalette palette, string? sql, WorkspaceView? editing)
        {
            _host = host;
            EditingView = editing;
            Name = "sqlEditorForm";
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(880, 600);
            MinimumSize = new Size(520, 360);
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

            Panel = new SqlEditorPanel(palette)
            {
                Dock = DockStyle.Fill,
                ResultDirectory = Form1.QueryResultDirectory,
                Workspace = workspace,
                PrepareAsync = host.PrepareForQueryAsync,
            };
            if (sql is not null) Panel.Sql = sql;
            Controls.Add(Panel);
            Panel.ResultReady += (_, e) => _host.OnQueryResult(e);
            Panel.SaveAsViewRequested += (_, text) => _ = _host.SaveEditorSqlAsViewAsync(text, EditingView);
            ApplyPalette(palette);
            Relocalize();
        }

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Panel.ApplyPalette(palette);
        }

        public void Relocalize()
        {
            Text = EditingView is null
                ? ViewerSupport.LT("SQL Query", "SQL 질의")
                : ViewerSupport.LT($"Edit view — {EditingView.Name}", $"뷰 편집 — {EditingView.Name}");
            Panel.Relocalize();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Panel.IsBusy) Panel.Cancel();
            base.OnFormClosing(e);
        }
    }
}
