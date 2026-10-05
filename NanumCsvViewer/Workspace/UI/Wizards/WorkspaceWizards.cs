using NanumCsvViewer.Workspace;

namespace NanumCsvViewer
{
    /// <summary>
    /// 작업 공간 마법사 진입점. 각 메서드는 모달 대화상자를 열고, 확인하면 최종 SQL(편집됐을 수 있음)·뷰 이름 제안·요약을, 취소하면 null을 돌려준다.
    /// 대화상자는 엔진으로 읽기(진단·미리보기)만 하고 뷰를 만들지는 않는다 — 호출한 쪽이 <see cref="DataWorkspace.CreateView"/>를 부른다.
    /// UI 스레드에서 호출한다. <paramref name="preselect"/>는 먼저 고를 표·뷰의 표시 이름(없으면 null).
    /// </summary>
    internal static class WorkspaceWizards
    {
        public static WizardResult? ShowJoin(IWin32Window owner, DataWorkspace ws, ThemePalette palette, string? preselect)
            => Show(owner, ws, 1, () => new JoinWizardDialog(palette, ws, preselect));

        public static WizardResult? ShowAppend(IWin32Window owner, DataWorkspace ws, ThemePalette palette, string? preselect)
            => Show(owner, ws, 2, () => new AppendWizardDialog(palette, ws, preselect));

        public static WizardResult? ShowCompare(IWin32Window owner, DataWorkspace ws, ThemePalette palette, string? preselect)
            => Show(owner, ws, 2, () => new CompareWizardDialog(palette, ws, preselect));

        public static WizardResult? ShowGroup(IWin32Window owner, DataWorkspace ws, ThemePalette palette, string? preselect)
            => Show(owner, ws, 1, () => new GroupWizardDialog(palette, ws, preselect));

        private static WizardResult? Show(IWin32Window owner, DataWorkspace ws, int minRelations, Func<WizardForm> create)
        {
            if (WizardStyle.Relations(ws).Count < minRelations)
            {
                MessageBox.Show(owner, minRelations == 1
                    ? ViewerSupport.LT("Add a CSV file (or a view) to the workspace first.", "먼저 작업 공간에 CSV 파일(또는 뷰)을 추가하세요.")
                    : ViewerSupport.LT("This needs at least two tables or views. Add another file to the workspace first.", "표나 뷰가 2개 이상 필요합니다. 먼저 작업 공간에 파일을 더 추가하세요."),
                    ViewerSupport.LT("Workspace", "작업 공간"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            using var dlg = create();
            return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Result : null;
        }
    }
}
