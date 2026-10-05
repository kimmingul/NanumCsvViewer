using System.Diagnostics;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Python;

namespace NanumCsvViewer
{
    // Python 분석 환경: 설정 ▸ AI ▸ Python 환경과 도구 ▸ Python 분석 재현 패키지 내보내기.
    public partial class Form1
    {
        private ToolStripMenuItem? _pythonBundleMenu;

        /// <summary>설정 ▸ "에이전트에 관리 환경 사용" 토글을 저장하고 에이전트에 알린다(바뀌면 쉬는 대로 같은 대화로 다시 시작).</summary>
        internal void ApplyManagedPythonSetting(bool on)
        {
            _settings.AgentUseManagedPython = on;
            _settings.Save();
            if (_agentController is not null) _agentController.Options = AgentOptions();
        }

        /// <summary>관리 환경을 설치·제거했다: 에이전트가 쓰는 Python이 달라져야 하면 쉬는 대로 같은 대화로 다시 시작한다.</summary>
        internal void NotifyPythonEnvironmentChanged()
        {
            if (_agentController is not null) _agentController.Options = AgentOptions();
        }

        /// <summary>지금 분석 폴더(에이전트가 쓰는 폴더, 로컬 Python이 꺼져 있어도 같은 규칙으로 계산). 알 수 없으면 null.</summary>
        private string? CurrentAnalysisFolder()
        {
            if (_agentController?.AnalysisFolder is { Length: > 0 } active) return active;
            var ctx = BuildAgentWorkspaceContext();
            if (string.IsNullOrWhiteSpace(ctx.WorkspaceFile) && string.IsNullOrWhiteSpace(ctx.FirstDataFile)) return null;
            return AgentWorkspace.StableOutputFolder(ctx.WorkspaceFile, ctx.FirstDataFile);
        }

        private async void ExportPythonBundle()
        {
            string title = LT("Export Python Analysis Bundle", "Python 분석 재현 패키지 내보내기");
            string? folder = CurrentAnalysisFolder();
            if (folder is null || !Directory.Exists(folder))
            {
                MessageBox.Show(this, LT("There is no analysis folder yet. Open a data file (or a workspace) and let the agent run a Python analysis first; the scripts, reports and figures it writes are exported.",
                                         "아직 분석 폴더가 없습니다. 데이터 파일(또는 작업 공간)을 열고 에이전트가 Python 분석을 실행하면 만들어지는 스크립트·보고서·그림이 내보내집니다."),
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string destination;
            bool includeData;
            using (var dlg = new PythonBundleDialog(_palette, folder, AnalysisBundle.DefaultName(folder)))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                destination = dlg.Destination;
                includeData = dlg.IncludeData;
            }

            var env = AnalysisEnvironment.Default;
            var info = env.Inspect();
            string? lockText = null;
            try { if (info.IsReady && File.Exists(env.LockFilePath)) lockText = File.ReadAllText(env.LockFilePath); }
            catch (IOException) { }
            var request = new AnalysisBundleRequest(folder, destination, includeData, lockText, info.PythonVersion?.ToString(), AppInfo.Version);

            AnalysisBundleResult result;
            try
            {
                UseWaitCursor = true;
                result = await Task.Run(() => AnalysisBundle.Export(request));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidOperationException)
            {
                UseWaitCursor = false;
                MessageBox.Show(this, LT("Could not export: ", "내보내지 못했습니다: ") + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally { UseWaitCursor = false; }

            string summary = LT(
                $"Exported {result.Scripts} script(s), {result.Reports} report(s), {result.Figures} figure(s)" + (result.DataFiles > 0 ? $", {result.DataFiles} data file(s)" : "") +
                (lockText != null ? ", requirements.lock" : " (no requirements.lock: the managed environment does not exist)") + " and a README to:\n" + result.Destination +
                (result.ExcludedData > 0 ? $"\n\n{result.ExcludedData} data file(s) were not included." : "") + "\n\nShow it in Explorer?",
                $"스크립트 {result.Scripts}개, 보고서 {result.Reports}개, 그림 {result.Figures}개" + (result.DataFiles > 0 ? $", 데이터 파일 {result.DataFiles}개" : "") +
                (lockText != null ? ", requirements.lock" : " (requirements.lock 없음: 관리 환경이 없습니다)") + "와 README를 내보냈습니다:\n" + result.Destination +
                (result.ExcludedData > 0 ? $"\n\n데이터 파일 {result.ExcludedData}개는 포함하지 않았습니다." : "") + "\n\n탐색기에서 볼까요?");
            if (MessageBox.Show(this, summary, title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            try
            {
                string arg = result.IsZip ? "/select,\"" + result.Destination + "\"" : "\"" + result.Destination + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arg) { UseShellExecute = true });
            }
            catch (Exception) { /* 탐색기를 못 열어도 내보내기는 끝났다 */ }
        }
    }
}
