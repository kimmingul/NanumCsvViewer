using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Setup
{
    /// <summary>
    /// 마법사가 OS·파일·네트워크에 닿는 동작을 모은 곳. 기본 구현이 실제 동작이고, 테스트·스크린샷 하니스는 필요한 멤버만 바꾼다.
    /// 모든 대화 상자는 <see cref="Owner"/> 위에 뜬다.
    /// </summary>
    internal class AiSetupOps
    {
        private static readonly Regex SafeProvider = new(@"^[A-Za-z0-9._\-]+$", RegexOptions.Compiled);
        private readonly IChatDialogs _dialogs;
        private readonly Func<bool> _korean;

        public AiSetupOps(Func<bool>? korean = null)
        {
            _korean = korean ?? (() => Loc.CurrentLanguage == "ko");
            _dialogs = new WinFormsChatDialogs(_korean);
        }

        public IWin32Window? Owner { get; set; }
        protected string T(string en, string ko) => _korean() ? ko : en;

        public virtual void OpenUrl(string url) => _dialogs.OpenUrl(url);

        public virtual void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception) { /* 탐색기를 못 열면 안내만 사라진다 */ }
        }

        /// <summary>cmd 창에서 `omp login [제공자]`를 실행한다(API 키 입력 등 앱에서 못 하는 로그인용). 창은 로그인 뒤에도 열려 있다.</summary>
        public virtual void OpenTerminalLogin(string exe, string? providerId)
        {
            string provider = providerId is not null && SafeProvider.IsMatch(providerId) ? " " + providerId : "";
            // cmd /k ""C:\a b\omp.exe" login x" — 바깥 따옴표 한 쌍은 cmd가 벗긴다.
            string args = "/k \"\"" + exe + "\" login" + provider + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
        }

        public virtual bool Confirm(string title, string message, bool yesNo = false) =>
            yesNo
                ? MessageBox.Show(Owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes
                : MessageBox.Show(Owner, message, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

        public virtual void ShowMessage(string title, string message, bool error = false) =>
            MessageBox.Show(Owner, message, title, MessageBoxButtons.OK, error ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

        public virtual string? PickOmpFile()
        {
            using var dlg = new OpenFileDialog
            {
                Title = T("Select omp", "omp 실행 파일 선택"),
                Filter = T("omp executable (*.exe;*.cmd)|*.exe;*.cmd|All files (*.*)|*.*", "omp 실행 파일 (*.exe;*.cmd)|*.exe;*.cmd|모든 파일 (*.*)|*.*"),
                CheckFileExists = true,
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            };
            return dlg.ShowDialog(Owner) == DialogResult.OK ? dlg.FileName : null;
        }

        public virtual string DefaultBackupPath() => NanumCsvViewer.Agent.CompatLayers.DefaultBackupPath();

        public virtual RemovalResult RemoveCompatLayer(CompatLayerEntry entry, string backupPath) =>
            NanumCsvViewer.Agent.CompatLayers.RemoveUserLayer(entry, backupPath);

        public virtual Task<OmpInstallResult> InstallFromFileAsync(string source, bool addToPath, CancellationToken ct) =>
            OmpInstaller.InstallFromFileAsync(source, addToPath, ct);

        public virtual Task<OmpInstallResult> DownloadAndInstallAsync(IProgress<OmpDownloadProgress> progress, bool addToPath, CancellationToken ct) =>
            OmpInstaller.DownloadAndInstallAsync(progress, ct, addToPath);

        public virtual string OmpTargetPath => OmpInstaller.TargetPath ?? "%LOCALAPPDATA%\\omp\\omp.exe";

        public virtual void SetClipboard(string text) => _dialogs.SetClipboard(text);

        public virtual string? PickSavePath(string suggestedFileName)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = T("Text (*.txt)|*.txt", "텍스트 (*.txt)|*.txt"),
                DefaultExt = "txt",
                FileName = suggestedFileName,
                OverwritePrompt = true,
            };
            return dlg.ShowDialog(Owner) == DialogResult.OK ? dlg.FileName : null;
        }

        public virtual void WriteFile(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(true));
    }
}
