using System.Text;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Setup
{
    /// <summary>
    /// AI 설정 도우미 창: 네 단계(WebView2 · omp · 모델 로그인 · 연결 시험)를 상태 아이콘과 고치기 버튼으로 보여 주고, 고칠 때마다 다시 검사한다.
    /// 모델에는 메시지를 보내지 않는다. 호스트(Form1)가 해야 할 일(설정 저장·에이전트 다시 시작·채팅 화면 다시 시도)은 이벤트로 알린다.
    /// </summary>
    internal sealed class AiSetupWizard : Form
    {
        private static readonly Color OkColor = Color.FromArgb(0x2E, 0x9E, 0x5B);
        private static readonly Color FailColor = Color.FromArgb(0xD6, 0x45, 0x45);
        private static readonly Color WarnColor = Color.FromArgb(0xD4, 0x9A, 0x00);

        private readonly AiSetupRunner _runner;
        private readonly AiSetupOps _ops;
        private readonly AppSettings _settings;
        private readonly Action<AppSettings> _save;
        private readonly Func<CancellationToken, Task<string>> _diagnostics;
        private readonly ThemePalette _p;
        private readonly bool _ko;
        private readonly StepView[] _views;
        private readonly Panel _scroll;
        private readonly Label _status;
        private readonly Button _recheck, _copy, _saveAs, _close, _cancelOp;
        private readonly CheckBox _dontShow;
        private readonly SetupStepId? _focus;
        private CancellationTokenSource? _cts;
        private bool _busy;
        private string _lastDiagnostics = "";

        /// <summary>[다시 시도] 뒤 채팅 화면이 다시 뜰 시간을 기다린 다음 WebView2를 다시 검사한다(테스트는 0으로).</summary>
        internal TimeSpan WebViewRetryWait = TimeSpan.FromSeconds(3.5);

        /// <summary>찾아보기로 고른 omp 경로(호스트가 설정에 저장하고 에이전트를 다시 시작한다).</summary>
        public event Action<string>? OmpPathChosen;
        /// <summary>omp를 설치했다(복사·내려받기). 인수는 설치된 경로.</summary>
        public event Action<string?>? OmpInstalled;
        /// <summary>로그인에 성공했다(실행 중인 에이전트가 새 자격 증명을 읽도록 호스트가 다시 시작한다).</summary>
        public event Action? CredentialsChanged;
        /// <summary>채팅 화면(WebView2) 초기화를 다시 시도해 달라는 요청.</summary>
        public event Action? WebViewRetryRequested;

        private string T(string en, string ko) => _ko ? ko : en;

        public AiSetupWizard(AiSetupRunner runner, AiSetupOps ops, AppSettings settings, ThemePalette palette, bool korean,
            Func<CancellationToken, Task<string>> collectDiagnostics, Action<AppSettings> saveSettings, SetupStepId? focus = null)
        {
            _runner = runner;
            _ops = ops;
            _settings = settings;
            _save = saveSettings;
            _diagnostics = collectDiagnostics;
            _p = palette;
            _ko = korean;
            _focus = focus;
            _ops.Owner = this;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = T("AI Setup Assistant", "AI 환경 설정 도우미");
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            BackColor = _p.Window;
            ForeColor = _p.Text;
            ClientSize = new Size(780, 680);
            MinimumSize = new Size(640, 480);

            var intro = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 58, Padding = new Padding(16, 12, 16, 0), UseMnemonic = false,
                Text = T("The AI chat needs the four things below. Use the button of a step that has a problem to fix it; the checks run again after every fix. Nothing is sent to an AI model.",
                    "AI 채팅이 동작하려면 아래 네 가지가 준비되어야 합니다. 문제가 있는 단계의 버튼으로 바로 고칠 수 있고, 고칠 때마다 다시 검사합니다. AI 모델에는 아무것도 보내지 않습니다."),
            };

            _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 4, 12, 4), BackColor = _p.Window };
            var titles = new[]
            {
                T("1. Chat screen (WebView2)", "1. 채팅 화면 (WebView2)"),
                T("2. AI agent program (omp)", "2. AI 에이전트 프로그램 (omp)"),
                T("3. Model sign-in", "3. 모델 로그인"),
                T("4. Connection test", "4. 연결 시험"),
            };
            _views = new StepView[4];
            for (int i = 3; i >= 0; i--)   // Dock=Top은 나중에 넣은 것이 위로 가므로 거꾸로 넣는다
            {
                _views[i] = new StepView(this, (SetupStepId)i, titles[i]) { Dock = DockStyle.Top };
                _scroll.Controls.Add(_views[i]);
            }
            _views[(int)SetupStepId.Omp].AddToPath.Text = T("Also add it to my user PATH (to run `omp` in a terminal)", "내 사용자 PATH에도 추가 (터미널에서 omp 명령을 쓰려면)");
            _views[(int)SetupStepId.Login].ProvidersCaption.Text = T("Sign in to a provider (opens the authorization page in your browser):", "제공자에 로그인 (브라우저에 인증 페이지가 열립니다):");

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 112, BackColor = _p.Window, Padding = new Padding(16, 6, 16, 8) };
            _status = new Label { Dock = DockStyle.Top, Height = 40, AutoSize = false, UseMnemonic = false, ForeColor = Blend(_p.Text, _p.Window, 0.25) };
            _dontShow = new CheckBox { AutoSize = true, UseMnemonic = false, ForeColor = _p.Text, BackColor = _p.Window, Checked = settings.AiSetupDisabled,
                Text = T("Don't show this automatically again", "다시 표시하지 않기 (자동으로 열지 않음)") };
            _dontShow.CheckedChanged += (_, _) => { AiSetupPolicy.SetDismissed(_settings, _dontShow.Checked); _save(_settings); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = _p.Window };
            _recheck = MakeButton(T("Check again", "다시 검사"), () => _ = RunGuardedAsync(ct => _runner.RunAllAsync(ct)));
            _copy = MakeButton(T("Copy diagnostics", "진단 정보 복사"), () => _ = CopyDiagnosticsAsync());
            _saveAs = MakeButton(T("Save as…", "파일로 저장…"), () => _ = SaveDiagnosticsAsync());
            _cancelOp = MakeButton(T("Cancel", "작업 취소"), () => _cts?.Cancel());
            _cancelOp.Visible = false;
            _close = MakeButton(T("Close", "닫기"), Close);
            buttons.Controls.AddRange(new Control[] { _recheck, _copy, _saveAs, _cancelOp, _close });
            _dontShow.Dock = DockStyle.Top;
            bottom.Controls.Add(buttons);
            bottom.Controls.Add(_dontShow);
            bottom.Controls.Add(_status);

            Controls.Add(_scroll);
            Controls.Add(bottom);
            Controls.Add(intro);
            CancelButton = _close;
            _runner.StepChanged += OnStepChanged;
            FormClosing += (_, _) => { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } };
            FormClosed += (_, _) => { _runner.StepChanged -= OnStepChanged; _runner.Dispose(); };
            Shown += async (_, _) => await StartAsync();
            ResumeLayout(true);
            for (int i = 0; i < 4; i++) _views[i].Apply(_runner.Step((SetupStepId)i));
        }

        internal IReadOnlyList<StepView> Views => _views;
        internal string StatusText => _status.Text;
        internal bool Busy => _busy;
        internal string LastDiagnostics => _lastDiagnostics;
        internal CheckBox DontShowAgain => _dontShow;

        internal Button MakeButton(string text, Action click)
        {
            var b = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat, BackColor = _p.Surface, ForeColor = _p.Text,
                MinimumSize = new Size(96, 28), UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 8, 0), Text = text, UseMnemonic = false };
            b.FlatAppearance.BorderColor = _p.Border;
            b.Click += (_, _) => click();
            return b;
        }

        internal static Color Blend(Color a, Color b, double t) =>
            Color.FromArgb((int)(a.R * (1 - t) + b.R * t), (int)(a.G * (1 - t) + b.G * t), (int)(a.B * (1 - t) + b.B * t));

        internal ThemePalette Palette => _p;
        internal bool Korean => _ko;

        // ---- 실행 -----------------------------------------------------------------------------------------------

        private async Task StartAsync()
        {
            await RunGuardedAsync(ct => _runner.RunAllAsync(ct));
            if (_focus is { } id && !IsDisposed) _scroll.ScrollControlIntoView(_views[(int)id]);
        }

        /// <summary>네 단계를 처음부터 다시 검사한다(테스트·"다시 검사").</summary>
        internal Task RecheckAsync() => RunGuardedAsync(ct => _runner.RunAllAsync(ct));

        private void OnStepChanged(SetupStep step)
        {
            if (IsDisposed) return;
            _views[(int)step.Id].Apply(step);
            if (step.Id == SetupStepId.Login || step.Id == SetupStepId.Connection) RefreshProviders();
        }

        private void RefreshProviders() =>
            _views[(int)SetupStepId.Login].ShowProviders(_runner.Account?.Providers ?? Array.Empty<LoginProvider>());

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            if (status is not null) _status.Text = status;
            _cancelOp.Visible = busy;
            _recheck.Enabled = !busy;
            foreach (var v in _views) v.SetEnabled(!busy);
        }

        /// <summary>한 번에 하나만 실행한다. 닫기·작업 취소로 취소된다. 예외는 대화 상자로 알린다.</summary>
        private async Task RunGuardedAsync(Func<CancellationToken, Task> body, string? status = null)
        {
            if (_busy || IsDisposed) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetBusy(true, status ?? T("Checking…", "검사하는 중…"));
            try
            {
                await body(_cts.Token);
                if (!IsDisposed) _status.Text = _runner.AllOk ? T("Everything is ready.", "모두 준비되었습니다.") : status is null ? "" : _status.Text;
            }
            catch (OperationCanceledException) { if (!IsDisposed) _status.Text = T("Cancelled.", "취소했습니다."); }
            catch (Exception ex)
            {
                if (!IsDisposed) { _status.Text = ex.Message; _ops.ShowMessage(Text, ex.Message, error: true); }
            }
            finally
            {
                if (!IsDisposed) { SetBusy(false); RefreshProviders(); }
            }
        }

        internal Task ExecuteFixAsync(SetupFix fix) => fix.Kind switch
        {
            SetupFixKind.InstallWebView => Run(() => _ops.OpenUrl(fix.Arg as string ?? WebViewDiagnostics.RuntimeInstallUrl)),
            SetupFixKind.OpenWebViewLog => Run(() => _ops.OpenFolder(Path.GetDirectoryName(WebViewDiagnostics.LogPath)!)),
            SetupFixKind.OpenTerminalLogin => Run(() =>
            {
                if (_runner.Omp?.ChosenExe is { } exe) _ops.OpenTerminalLogin(exe, null);
            }),
            SetupFixKind.Recheck => RecheckAsync(),
            SetupFixKind.RetryWebView => RunGuardedAsync(async ct => { await RetryWebViewAsync(ct); }, T("Restarting the chat screen…", "채팅 화면을 다시 시작하는 중…")),
            SetupFixKind.RemoveCompatLayer => RemoveLayerAsync((CompatLayerEntry)fix.Arg!),
            SetupFixKind.BrowseOmp => BrowseAsync(),
            SetupFixKind.InstallCandidate => InstallAsync((OmpCandidate)fix.Arg!),
            SetupFixKind.DownloadOmp => DownloadAsync(),
            _ => Task.CompletedTask,
        };

        private static Task Run(Action a) { a(); return Task.CompletedTask; }

        private async Task RetryWebViewAsync(CancellationToken ct)
        {
            WebViewRetryRequested?.Invoke();
            if (WebViewRetryWait > TimeSpan.Zero) await Task.Delay(WebViewRetryWait, ct);
            await _runner.RunWebViewAsync();
        }

        private Task RemoveLayerAsync(CompatLayerEntry entry)
        {
            string backup = _ops.DefaultBackupPath();
            string message = T(
                $"Remove this compatibility setting?\n\n  {entry.ExePath}\n  = {entry.Flags}\n\nA backup (.reg) is written first:\n  {backup}\nOnly this value is removed. To undo, double-click the backup file.",
                $"이 호환성 설정을 해제할까요?\n\n  {entry.ExePath}\n  = {entry.Flags}\n\n먼저 백업(.reg)을 만듭니다:\n  {backup}\n이 값 하나만 지웁니다. 되돌리려면 백업 파일을 두 번 클릭하세요.");
            if (!_ops.Confirm(T("Remove compatibility setting", "호환성 설정 해제"), message)) return Task.CompletedTask;
            return RunGuardedAsync(async ct =>
            {
                var result = _ops.RemoveCompatLayer(entry, backup);
                if (!result.Ok)
                {
                    _ops.ShowMessage(Text, result.Message, error: true);
                    await _runner.RunWebViewAsync();
                    return;
                }
                _status.Text = result.Message;
                await RetryWebViewAsync(ct);
            }, T("Removing the setting…", "설정을 해제하는 중…"));
        }

        private async Task BrowseAsync()
        {
            string? path = _ops.PickOmpFile();
            if (string.IsNullOrEmpty(path)) return;
            OmpPathChosen?.Invoke(path);
            await RunGuardedAsync(ct => _runner.RunOmpAsync(ct));
        }

        private bool AddToPath => _views[(int)SetupStepId.Omp].AddToPath.Checked;

        private async Task InstallAsync(OmpCandidate candidate)
        {
            string message = T(
                $"Copy this file to {_ops.OmpTargetPath} and remove its \"downloaded from the internet\" mark?\n\n  {candidate.Path}",
                $"이 파일을 {_ops.OmpTargetPath}로 복사하고 \"인터넷에서 받은 파일\" 표시를 지울까요?\n\n  {candidate.Path}");
            if (!_ops.Confirm(T("Install omp", "omp 설치"), message)) return;
            bool addToPath = AddToPath;
            await RunGuardedAsync(async ct =>
            {
                var result = await _ops.InstallFromFileAsync(candidate.Path, addToPath, ct);
                await AfterInstallAsync(result, ct);
            }, T("Installing omp…", "omp를 설치하는 중…"));
        }

        private async Task DownloadAsync()
        {
            string message = T(
                $"Download the latest stable omp release from GitHub, verify it, and install it to {_ops.OmpTargetPath}?",
                $"GitHub에서 omp 최신 안정 릴리스를 내려받아 검증한 뒤 {_ops.OmpTargetPath}에 설치할까요?");
            if (!_ops.Confirm(T("Download omp", "omp 내려받기"), message)) return;
            bool addToPath = AddToPath;
            await RunGuardedAsync(async ct =>
            {
                var progress = new Progress<OmpDownloadProgress>(p => _status.Text = DescribeProgress(p));
                var result = await _ops.DownloadAndInstallAsync(progress, addToPath, ct);
                await AfterInstallAsync(result, ct);
            }, T("Downloading omp…", "omp를 내려받는 중…"));
        }

        private string DescribeProgress(OmpDownloadProgress p)
        {
            string stage = p.Stage switch
            {
                "query" => T("Looking up the latest release…", "최신 릴리스를 찾는 중…"),
                "verify" => T("Verifying…", "검증하는 중…"),
                "install" => T("Installing…", "설치하는 중…"),
                _ => T("Downloading…", "내려받는 중…"),
            };
            if (p.Stage == "download" && p.Total is > 0)
                stage += $" {p.Received * 100 / p.Total}% ({p.Received / 1048576} / {p.Total / 1048576} MB)";
            return stage;
        }

        private async Task AfterInstallAsync(OmpInstallResult result, CancellationToken ct)
        {
            if (!result.Ok)
            {
                _status.Text = result.Message;
                _ops.ShowMessage(Text, result.Message, error: true);
                return;
            }
            _status.Text = result.Message;
            OmpInstalled?.Invoke(result.InstalledPath);
            await _runner.RunOmpAsync(ct);
        }

        // ---- 로그인 ---------------------------------------------------------------------------------------------

        internal async Task LoginAsync(string providerId)
        {
            var login = _views[(int)SetupStepId.Login];
            string? exe = _runner.Omp?.ChosenExe;
            var probe = _runner.Probe;
            if (exe is null || probe is null) return;
            var ui = new LoginUi(
                (url, instructions) =>
                {
                    login.SetLoginUrl(url);
                    _ops.OpenUrl(url);
                    _status.Text = T("Finish signing in in your browser…", "브라우저에서 로그인을 마치세요…") + (instructions is null ? "" : " " + instructions);
                },
                text => _status.Text = text,
                (title, prompt, ct) => login.AskAsync(prompt, ct));
            LoginResult? result = null;
            await RunGuardedAsync(async ct =>
            {
                result = await probe.LoginAsync(exe, providerId, ui, ct);
                if (result.Ok)
                {
                    CredentialsChanged?.Invoke();
                    await _runner.RunAccountAsync(ct);
                }
            }, T("Waiting for the sign-in to finish (you can cancel)…", "로그인이 끝나기를 기다리는 중입니다(취소할 수 있습니다)…"));
            login.HideInput();
            if (IsDisposed) return;
            if (result is null) { login.ShowLoginMessage(T("Sign-in was cancelled.", "로그인을 취소했습니다."), false, null); return; }
            login.ShowLoginMessage(result.Message, result.Ok, result.NeedsTerminal ? providerId : null);
        }

        internal void OpenTerminalFor(string providerId)
        {
            if (_runner.Omp?.ChosenExe is { } exe) _ops.OpenTerminalLogin(exe, providerId);
        }

        // ---- 진단 정보 ------------------------------------------------------------------------------------------

        internal async Task CopyDiagnosticsAsync()
        {
            string text = "";
            await RunGuardedAsync(async ct => text = await _diagnostics(ct), T("Collecting diagnostics…", "진단 정보를 모으는 중…"));
            if (text.Length == 0 || IsDisposed) return;
            _lastDiagnostics = text;
            _ops.SetClipboard(text);
            _status.Text = T($"Copied the diagnostics ({text.Count(c => c == '\n') + 1} lines). Secrets and your user name are masked; no data contents are included.",
                $"진단 정보를 복사했습니다({text.Count(c => c == '\n') + 1}줄). 비밀 값과 사용자 이름은 가렸고 데이터 내용은 포함되지 않습니다.");
        }

        internal async Task SaveDiagnosticsAsync()
        {
            string text = "";
            await RunGuardedAsync(async ct => text = await _diagnostics(ct), T("Collecting diagnostics…", "진단 정보를 모으는 중…"));
            if (text.Length == 0 || IsDisposed) return;
            _lastDiagnostics = text;
            string? path = _ops.PickSavePath($"NanumCsvViewer-ai-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt");
            if (path is null) return;
            try { _ops.WriteFile(path, text); _status.Text = T("Saved: ", "저장했습니다: ") + path; }
            catch (Exception ex) { _status.Text = ex.Message; _ops.ShowMessage(Text, ex.Message, error: true); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _cts?.Dispose(); _cts = null; }
            base.Dispose(disposing);
        }

        // ---- 한 단계의 화면 -------------------------------------------------------------------------------------

        internal sealed class StepView : Panel
        {
            private readonly AiSetupWizard _wizard;
            private readonly Label _icon, _title, _summary, _detail, _loginMessage;
            private readonly FlowLayoutPanel _fixes;
            private readonly FlowLayoutPanel _providers;
            private readonly Panel _providersHost;
            private readonly Button _terminalForProvider;
            private string? _terminalProvider;
            private readonly FlowLayoutPanel _inputPanel;
            private readonly Label _inputLabel;
            private readonly Button _copyUrl;
            private string? _loginUrl;
            private Action? _submitInput;
            private readonly ToolTip _tip = new();

            public Label ProvidersCaption { get; }
            public CheckBox AddToPath { get; }
            public SetupStepId Id { get; }
            public SetupStep Current { get; private set; } = new(SetupStepId.WebView, SetupState.Pending, "", null, SetupStep.NoFixes);
            internal IReadOnlyList<Button> FixButtons => _fixes.Controls.OfType<Button>().ToList();
            internal IReadOnlyList<Button> ProviderButtons => _providers.Controls.OfType<Control>().SelectMany(c => c.Controls.OfType<Button>()).ToList();
            internal string IconText => _icon.Text;
            internal Color IconColor => _icon.ForeColor;
            internal string SummaryText => _summary.Text;
            internal string DetailText => _detail.Text;
            internal string LoginMessageText => _loginMessage.Text;
            internal bool TerminalButtonVisible => _terminalForProvider.Visible;
            internal Button TerminalButton => _terminalForProvider;
            internal TextBox InputBox { get; }
            internal Button InputOk { get; }
            internal bool InputVisible => _inputPanel.Visible;
            internal Button CopyUrlButton => _copyUrl;

            public StepView(AiSetupWizard wizard, SetupStepId id, string title)
            {
                _wizard = wizard;
                Id = id;
                var p = wizard._p;
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                Padding = new Padding(0, 6, 0, 10);
                BackColor = p.Window;

                _icon = new Label { Left = 4, Top = 8, Width = 36, Height = 32, Font = new Font("Segoe UI Symbol", 16F), TextAlign = ContentAlignment.TopCenter, UseMnemonic = false };
                var body = new FlowLayoutPanel
                {
                    Left = 46, Top = 6, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                    BackColor = p.Window,
                };
                _title = new Label { AutoSize = true, UseMnemonic = false, Font = new Font(wizard.Font, FontStyle.Bold), ForeColor = p.Text, Margin = new Padding(0, 0, 0, 2) };
                _title.Text = title;
                _summary = new Label { AutoSize = true, UseMnemonic = false, ForeColor = p.Text, Margin = new Padding(0, 0, 0, 2) };
                _detail = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Blend(p.Text, p.Window, 0.3), Margin = new Padding(0, 0, 0, 4) };
                _fixes = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = p.Window, Margin = new Padding(0, 2, 0, 2) };
                AddToPath = new CheckBox { AutoSize = true, UseMnemonic = false, ForeColor = p.Text, BackColor = p.Window, Visible = false, Margin = new Padding(0, 2, 0, 2) };
                ProvidersCaption = new Label { AutoSize = true, UseMnemonic = false, ForeColor = p.Text, Visible = false, Margin = new Padding(0, 6, 0, 2) };
                _providers = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = p.Surface };
                _providersHost = new Panel { Width = 560, Height = 150, AutoScroll = true, BackColor = p.Surface, BorderStyle = BorderStyle.FixedSingle, Visible = false };
                _providersHost.Controls.Add(_providers);
                _loginMessage = new Label { AutoSize = true, UseMnemonic = false, ForeColor = p.Text, Visible = false, Margin = new Padding(0, 4, 0, 2) };
                _terminalForProvider = wizard.MakeButton(wizard.T("Open a terminal for this provider", "이 제공자로 터미널에서 로그인"), () =>
                {
                    if (_terminalProvider is not null) wizard.OpenTerminalFor(_terminalProvider);
                });
                _terminalForProvider.Visible = false;
                _inputLabel = new Label { AutoSize = true, UseMnemonic = false, ForeColor = p.Text, Margin = new Padding(0, 4, 0, 2) };
                InputBox = new TextBox { Width = 420, BackColor = p.Surface, ForeColor = p.Text, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 8, 0) };
                InputOk = wizard.MakeButton(wizard.T("OK", "확인"), () => _submitInput?.Invoke());
                InputBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _submitInput?.Invoke(); } };
                var inputRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = p.Window, Margin = new Padding(0) };
                inputRow.Controls.Add(InputBox);
                inputRow.Controls.Add(InputOk);
                _inputPanel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = p.Window, Visible = false };
                _inputPanel.Controls.Add(_inputLabel);
                _inputPanel.Controls.Add(inputRow);
                _copyUrl = wizard.MakeButton(wizard.T("Copy the sign-in address", "로그인 주소 복사"), () => { if (_loginUrl is not null) wizard._ops.SetClipboard(_loginUrl); });
                _copyUrl.Visible = false;

                body.Controls.AddRange(new Control[] { _title, _summary, _detail, _fixes, AddToPath, ProvidersCaption, _providersHost, _loginMessage, _copyUrl, _inputPanel, _terminalForProvider });
                Controls.Add(_icon);
                Controls.Add(body);
                Resize += (_, _) => FitLabels();
                FitLabels();
            }

            private static Color Blend(Color a, Color b, double t) => AiSetupWizard.Blend(a, b, t);

            private void FitLabels()
            {
                int w = Math.Max(240, Width - 70);
                var max = new Size(w, 0);
                _summary.MaximumSize = _detail.MaximumSize = _loginMessage.MaximumSize = _title.MaximumSize = ProvidersCaption.MaximumSize = AddToPath.MaximumSize = _inputLabel.MaximumSize = max;
                _fixes.MaximumSize = new Size(w, 0);
                _providersHost.Width = Math.Min(w, 620);
            }

            /// <summary>omp가 요청한 비밀이 아닌 입력(인증 코드·주소)을 이 단계 아래의 입력 칸으로 받는다. 모달 창이 아니라 브라우저에서 로그인이 먼저 끝나면 칸이 사라진다.</summary>
            public Task<string?> AskAsync(string prompt, CancellationToken ct)
            {
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inputLabel.Text = prompt;
                InputBox.Text = "";
                _inputPanel.Visible = true;
                _submitInput = () =>
                {
                    string value = InputBox.Text.Trim();
                    if (value.Length == 0) return;
                    HideInput();
                    tcs.TrySetResult(value);
                };
                ct.Register(() => { HideInput(); tcs.TrySetResult(null); });
                return tcs.Task;
            }

            public void HideInput()
            {
                _submitInput = null;
                if (!IsDisposed) _inputPanel.Visible = false;
            }

            public void SetLoginUrl(string url)
            {
                _loginUrl = url;
                _copyUrl.Visible = true;
            }

            public void Apply(SetupStep s)
            {
                Current = s;
                (string glyph, Color color) = s.State switch
                {
                    SetupState.Ok => ("\u2714", OkColor),
                    SetupState.Fail => ("\u2716", FailColor),
                    SetupState.Warn => ("\u26A0", WarnColor),
                    SetupState.Checking => ("\u27F3", _wizard._p.Accent),
                    SetupState.Blocked => ("\u2013", Blend(_wizard._p.Text, _wizard._p.Window, 0.5)),
                    _ => ("\u25CB", Blend(_wizard._p.Text, _wizard._p.Window, 0.5)),
                };
                _icon.Text = glyph;
                _icon.ForeColor = color;
                _summary.Text = s.Summary;
                _detail.Text = s.Detail ?? "";
                _detail.Visible = !string.IsNullOrEmpty(s.Detail);
                _fixes.SuspendLayout();
                foreach (Control c in _fixes.Controls.OfType<Control>().ToList()) { _fixes.Controls.Remove(c); c.Dispose(); }
                _tip.RemoveAll();
                foreach (var fix in s.Fixes)
                {
                    var b = _wizard.MakeButton(fix.Label, () => _ = _wizard.ExecuteFixAsync(fix));
                    b.Enabled = !_wizard._busy;
                    if (fix.Tooltip is not null) _tip.SetToolTip(b, fix.Tooltip);
                    _fixes.Controls.Add(b);
                }
                _fixes.ResumeLayout(true);
                _fixes.Visible = s.Fixes.Count > 0;
                if (Id == SetupStepId.Omp) AddToPath.Visible = s.Fixes.Any(f => f.Kind is SetupFixKind.InstallCandidate or SetupFixKind.DownloadOmp);
            }

            public void SetEnabled(bool enabled)
            {
                foreach (var b in FixButtons) b.Enabled = enabled;
                foreach (var b in ProviderButtons) b.Enabled = enabled;
                _terminalForProvider.Enabled = enabled;
            }

            public void ShowProviders(IReadOnlyList<LoginProvider> providers)
            {
                if (Id != SetupStepId.Login) return;
                _providers.SuspendLayout();
                foreach (Control c in _providers.Controls.OfType<Control>().ToList()) { _providers.Controls.Remove(c); c.Dispose(); }
                foreach (var provider in providers)
                {
                    var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = _wizard._p.Surface, Margin = new Padding(2) };
                    var name = new Label
                    {
                        Width = 380, Height = 28, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, ForeColor = _wizard._p.Text,
                        Text = provider.Name + (provider.Authenticated ? "   \u2714 " + _wizard.T("signed in", "로그인됨") : ""),
                    };
                    var id = provider.Id;
                    var b = _wizard.MakeButton(provider.Authenticated ? _wizard.T("Sign in again", "다시 로그인") : _wizard.T("Sign in", "로그인"), () => _ = _wizard.LoginAsync(id));
                    b.Enabled = !_wizard._busy;
                    row.Controls.Add(name);
                    row.Controls.Add(b);
                    _providers.Controls.Add(row);
                }
                _providers.ResumeLayout(true);
                bool any = providers.Count > 0;
                ProvidersCaption.Visible = _providersHost.Visible = any;
            }

            public void ShowLoginMessage(string text, bool ok, string? terminalProvider)
            {
                _loginMessage.Text = text;
                _loginMessage.ForeColor = ok ? OkColor : FailColor;
                _loginMessage.Visible = text.Length > 0;
                _terminalProvider = terminalProvider;
                _terminalForProvider.Visible = terminalProvider is not null;
            }
        }
    }
}
