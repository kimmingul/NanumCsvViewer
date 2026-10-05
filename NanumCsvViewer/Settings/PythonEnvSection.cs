using System.Diagnostics;
using NanumCsvViewer.Agent.Python;

namespace NanumCsvViewer
{
    /// <summary>
    /// 설정 ▸ AI 쪽의 "Python 환경" 구역: 앱 관리 분석 환경의 상태(경로·Python 버전·묶음별 설치 버전·디스크 크기)와 묶음별 설치/업데이트/제거,
    /// 폴더 열기, "에이전트에 관리 환경 사용" 토글. 설치·제거는 <see cref="AnalysisEnvironment.Default"/>가 백그라운드로 하며(진행·취소 가능)
    /// 설정 창을 닫아도 계속된다. 토글만 확인·적용 때(<see cref="Commit"/>) 저장한다.
    /// </summary>
    internal sealed class PythonEnvSection : Panel
    {
        private const int Width0 = 560;

        private readonly Form1 _host;
        private readonly ThemePalette _p;
        private readonly AnalysisEnvironment _env;
        private readonly Label _heading, _status, _note, _progressText;
        private readonly TableLayoutPanel _groups;
        private readonly Button _open, _cancel;
        private readonly CheckBox _useManaged;
        private readonly ProgressBar _bar;
        private readonly Dictionary<string, (Label Title, Label State, Button Install, Button Remove)> _rows = new();
        private long _sizeBytes = -1;
        private int _sizeToken;

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;
        private static bool Korean => Loc.CurrentLanguage == "ko";

        public PythonEnvSection(Form1 host, ThemePalette palette, AnalysisEnvironment? env = null)
        {
            _host = host;
            _p = palette;
            _env = env ?? AnalysisEnvironment.Default;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0);
            MinimumSize = new Size(Width0, 0);

            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top, BackColor = palette.Window, Margin = new Padding(0),
            };
            Controls.Add(flow);

            _heading = new Label { AutoSize = true, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold), ForeColor = palette.Text, Margin = new Padding(0, 14, 0, 2) };
            _status = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(Width0, 0), ForeColor = palette.Text, Margin = new Padding(0, 2, 0, 4) };
            _note = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(Width0, 0), ForeColor = Blend(palette.Text, palette.Window, 0.35), Margin = new Padding(0, 2, 0, 6) };
            flow.Controls.Add(_heading);
            flow.Controls.Add(_status);

            _groups = new TableLayoutPanel
            {
                ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = palette.Window, Margin = new Padding(0, 2, 0, 4),
            };
            _groups.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            _groups.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            _groups.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _groups.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var g in AnalysisGroups.All)
            {
                int row = _groups.RowCount++;
                _groups.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var title = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(240, 0), ForeColor = palette.Text, Margin = new Padding(0, 6, 6, 6) };
                var state = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(104, 0), ForeColor = palette.Text, Margin = new Padding(0, 6, 6, 6) };
                var install = NewButton();
                var remove = NewButton();
                string name = g.Name;
                install.Click += (_, _) => _ = RunInstallAsync(name);
                remove.Click += (_, _) => _ = RunRemoveAsync(name);
                _groups.Controls.Add(title, 0, row);
                _groups.Controls.Add(state, 1, row);
                _groups.Controls.Add(install, 2, row);
                _groups.Controls.Add(remove, 3, row);
                _rows[g.Name] = (title, state, install, remove);
            }
            flow.Controls.Add(_groups);

            var progressRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 2, 0, 2), BackColor = palette.Window };
            _bar = new ProgressBar { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 160, Height = 14, Margin = new Padding(0, 6, 8, 0), Visible = false };
            _progressText = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(300, 0), ForeColor = palette.Text, Margin = new Padding(0, 4, 8, 0) };
            _cancel = NewButton();
            _cancel.Visible = false;
            _cancel.Click += (_, _) => _env.Cancel();
            progressRow.Controls.Add(_bar);
            progressRow.Controls.Add(_progressText);
            progressRow.Controls.Add(_cancel);
            flow.Controls.Add(progressRow);

            _open = NewButton();
            _open.Click += (_, _) => OpenFolder();
            _useManaged = new CheckBox { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(Width0, 0), ForeColor = palette.Text, BackColor = palette.Window, Margin = new Padding(0, 6, 0, 4) };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0, 2, 0, 2), BackColor = palette.Window };
            buttons.Controls.Add(_open);
            flow.Controls.Add(buttons);
            flow.Controls.Add(_useManaged);
            flow.Controls.Add(_note);

            _env.Changed += OnEnvChanged;
            Relocalize();
            Reload();
        }

        private Button NewButton()
        {
            var b = new Button { AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = _p.Surface, ForeColor = _p.Text, MinimumSize = new Size(88, 26), UseVisualStyleBackColor = false, Margin = new Padding(0, 3, 6, 3) };
            b.FlatAppearance.BorderColor = _p.Border;
            return b;
        }

        private static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _env.Changed -= OnEnvChanged;
            base.Dispose(disposing);
        }

        // ---- 값·글자 ---------------------------------------------------------------------------------------------

        public void Relocalize()
        {
            _heading.Text = LT("Python environment", "Python 환경");
            _open.Text = LT("Open folder", "폴더 열기");
            _cancel.Text = LT("Cancel", "취소");
            _useManaged.Text = LT("Use the managed environment for the agent (when it exists)", "에이전트에 관리 환경 사용 (환경이 있을 때)");
            _note.Text = LT(
                "The managed environment (%LOCALAPPDATA%\\NanumCsvViewer\\python-analysis) is separate from your own Python. It needs Python 3.10-3.13 (64-bit Intel/AMD) on this PC and the internet for the first install; " +
                "only prebuilt wheels with pinned versions are installed (never built from source) and nothing outside that folder is changed. The agent is told not to pip install by itself; it asks you to install a group. " +
                "Changing this restarts the agent on the same conversation.",
                "관리 환경(%LOCALAPPDATA%\\NanumCsvViewer\\python-analysis)은 사용자의 Python과 분리되어 있습니다. 이 PC에 Python 3.10~3.13(64비트 Intel/AMD)이 있어야 하고 처음 설치에는 인터넷이 필요합니다. " +
                "버전을 고정한 미리 빌드된 wheel만 설치하며(소스 빌드 없음) 이 폴더 밖은 바꾸지 않습니다. 에이전트는 스스로 pip install 하지 않고 묶음 설치를 요청해 승인을 받습니다. " +
                "바꾸면 같은 대화로 에이전트를 다시 시작합니다.");
            Refresh2();
        }

        /// <summary>설정 값(토글)과 환경 상태를 다시 읽는다.</summary>
        public void Reload()
        {
            _useManaged.Checked = _host.AppSettingsRef.AgentUseManagedPython;
            Refresh2();
            StartSizeProbe();
        }

        /// <summary>토글을 기본값(켬)으로 되돌린다(적용은 확인·적용 때).</summary>
        public void ResetDefaults() => _useManaged.Checked = new AppSettings().AgentUseManagedPython;

        /// <summary>토글을 설정에 저장하고 에이전트에 알린다(바뀌었을 때만).</summary>
        public void Commit()
        {
            if (_useManaged.Checked == _host.AppSettingsRef.AgentUseManagedPython) return;
            _host.ApplyManagedPythonSetting(_useManaged.Checked);
        }

        private void Refresh2()
        {
            var info = _env.Inspect();
            bool busy = _env.IsBusy;
            string stateLine = info.State switch
            {
                AnalysisEnvState.Ready => LT("Ready", "준비됨"),
                AnalysisEnvState.Broken => LT("Damaged - installing again repairs it", "손상됨 - 다시 설치하면 복구합니다"),
                _ => LT("Not created yet", "아직 만들지 않음"),
            };
            string detail = info.IsReady
                ? $"\n{LT("Python", "Python")}: {info.PythonVersion}   {LT("Folder", "폴더")}: {_env.Root}" +
                  (_sizeBytes >= 0 ? $"\n{LT("Disk size", "디스크 크기")}: {AnalysisMessages.FormatSize(_sizeBytes)}" : "")
                : $"\n{LT("Folder", "폴더")}: {_env.Root}";
            _status.Text = LT("Status: ", "상태: ") + stateLine + detail;

            foreach (var g in AnalysisGroups.All)
            {
                var row = _rows[g.Name];
                var st = info.Group(g.Name);
                var state = st?.State ?? AnalysisGroupState.NotInstalled;
                string versions = st != null && st.Versions.Count > 0
                    ? "\n" + string.Join(", ", st.Versions.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key} {kv.Value}"))
                    : "";
                row.Title.Text = g.Title(Korean) + " (" + g.Name + ")" + (g.ApproxMb > 0 ? " ~" + g.ApproxMb + " MB" : "") + "\n" + g.Description(Korean) + versions;
                row.Title.MaximumSize = new Size(240, 0);
                row.State.Text = AnalysisMessages.StateText(state, Korean);
                row.Install.Text = state switch
                {
                    AnalysisGroupState.Installed => LT("Reinstall", "다시 설치"),
                    AnalysisGroupState.Outdated => LT("Update", "업데이트"),
                    AnalysisGroupState.Damaged => LT("Repair", "복구"),
                    _ => LT("Install", "설치"),
                };
                row.Install.Enabled = !busy;
                bool removable = state != AnalysisGroupState.NotInstalled || (g.Name == AnalysisGroups.Core && info.State != AnalysisEnvState.Missing);
                row.Remove.Text = g.Name == AnalysisGroups.Core ? LT("Remove all", "모두 제거") : LT("Remove", "제거");
                row.Remove.Enabled = !busy && removable;
            }
            _bar.Visible = busy;
            _cancel.Visible = busy;
            _progressText.Text = busy ? _env.Activity : "";
            _open.Enabled = Directory.Exists(_env.Root);
        }

        private void StartSizeProbe()
        {
            int token = ++_sizeToken;
            Task.Run(() => _env.GetSizeBytes()).ContinueWith(t =>
            {
                if (t.IsFaulted || token != _sizeToken) return;
                _sizeBytes = t.Result;
                if (IsDisposed || !IsHandleCreated) return;      // 아직 화면이 없으면 OnHandleCreated가 보여 준다
                try { BeginInvoke(new Action(() => { if (!IsDisposed && token == _sizeToken) Refresh2(); })); }
                catch (InvalidOperationException) { /* 닫히는 중 */ }
            });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Refresh2();
        }

        private int _queued;

        /// <summary>작업 스레드에서 불린다. pip 줄마다 오므로 UI로는 한 번에 하나만 넘기고, 진행 문구만 갱신한다(끝나면 전체 새로고침).</summary>
        private void OnEnvChanged(AnalysisProgress? p)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (p != null && Interlocked.Exchange(ref _queued, 1) == 1) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    Interlocked.Exchange(ref _queued, 0);
                    if (IsDisposed) return;
                    if (p != null && _bar.Visible)
                    {
                        string line = _env.Activity;
                        _progressText.Text = line.Length > 120 ? line[..120] + "…" : line;
                        return;
                    }
                    Refresh2();
                    if (p == null) StartSizeProbe();     // 작업이 끝났다: 크기를 다시 잰다
                }));
            }
            catch (InvalidOperationException) { /* 닫히는 중 */ }
        }

        // ---- 동작 ------------------------------------------------------------------------------------------------

        private async Task RunInstallAsync(string group)
        {
            var result = await Task.Run(() => _env.InstallAsync(new[] { group }, null, CancellationToken.None));
            if (IsDisposed) return;
            Refresh2();
            if (result.Ok) _host.NotifyPythonEnvironmentChanged();
            else ShowFailure(result);
        }

        private async Task RunRemoveAsync(string group)
        {
            bool all = group == AnalysisGroups.Core;
            string text = all
                ? LT("Delete the whole Python analysis environment (all groups)? You can install it again later.", "Python 분석 환경 전체(모든 묶음)를 삭제할까요? 나중에 다시 설치할 수 있습니다.")
                : LT($"Remove the '{group}' packages from the environment?", $"환경에서 '{group}' 패키지를 제거할까요?");
            if (MessageBox.Show(FindForm(), text, LT("Python environment", "Python 환경"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var result = await Task.Run(() => _env.RemoveGroupAsync(group, null, CancellationToken.None));
            if (IsDisposed) return;
            Refresh2();
            if (result.Ok) _host.NotifyPythonEnvironmentChanged();
            else ShowFailure(result);
        }

        private void ShowFailure(AnalysisResult result)
        {
            string text = AnalysisMessages.Describe(result, Korean);
            if (result.Failure == AnalysisFailure.Cancelled) return;
            if (result.Failure == AnalysisFailure.NoPython)
            {
                var answer = MessageBox.Show(FindForm(), text + "\n\n" + LT("Open the Python download page?", "Python 내려받기 페이지를 열까요?"),
                    LT("Python environment", "Python 환경"), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo(AnalysisMessages.PythonDownloadUrl) { UseShellExecute = true }); } catch { }
                }
                return;
            }
            MessageBox.Show(FindForm(), text, LT("Python environment", "Python 환경"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OpenFolder()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _env.Root + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, LT("Python environment", "Python 환경")); }
        }
    }
}
