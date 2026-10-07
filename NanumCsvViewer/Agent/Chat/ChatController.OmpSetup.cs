using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    // omp를 찾지 못했거나 쓸 수 없을 때의 채팅 안내와 그 버튼(찾아보기·이 파일로 설치·다운로드하여 설치·다시 시도).
    public sealed partial class ChatController
    {
        /// <summary>채팅 알림 버튼의 앱 내부 주소: nanumcsv://omp/{browse | install?src=…[&amp;path=1] | download[?path=1] | retry}.</summary>
        internal const string OmpUrlPrefix = "nanumcsv://omp/";

        /// <summary>설정 마법사를 여는 앱 내부 주소: nanumcsv://setup/{omp | login | open | webview}. SetupNeeded 이벤트로만 알린다.</summary>
        internal const string SetupUrlPrefix = "nanumcsv://setup/";

        private OmpDiscoveryResult? _lastDiscovery;
        private CancellationTokenSource? _ompActionCts;
        private bool _ompActionBusy;

        /// <summary>omp 찾기·검증 실패: 상태 표시줄에는 한 줄, 채팅에는 확인한 위치까지 담은 오류 알림과 버튼 알림, 그리고 마법사 신호.</summary>
        private void FailOmp(OmpDiscoveryResult r)
        {
            string full = r.Describe(Korean);
            Fail(full.Split('\n')[0], full);
            PostOmpActions(r);
            SetupNeeded?.Invoke(AgentSetupReason.Omp, r.ToDiagnosticText());
        }

        private void PostOmpActions(OmpDiscoveryResult r)
        {
            var actions = new List<ChatNoticeAction> { new(T("Browse…", "찾아보기…"), OmpUrlPrefix + "browse") };
            foreach (var c in r.Candidates)
            {
                string name = Path.GetFileName(c.Path);
                string why = c.ReasonText(Korean);
                actions.Add(new(T($"Install this file ({name}, {why})", $"이 파일로 설치 ({name}, {why})"),
                    OmpUrlPrefix + "install?src=" + Uri.EscapeDataString(c.Path), CarriesPathOption: true));
            }
            actions.Add(new(T("Download and install", "다운로드하여 설치"), OmpUrlPrefix + "download?", CarriesPathOption: true));
            actions.Add(new(T("Retry", "다시 시도"), OmpUrlPrefix + "retry"));
            actions.Add(new(T("Install guide", "설치 안내"), "https://github.com/can1357/oh-my-pi"));

            string text = r.Problem is OmpProblemKind.TooOld or OmpProblemKind.NotRunnable or OmpProblemKind.UnknownVersion
                ? T("The AI agent needs a working omp. Pick another omp.exe, install one, or retry after fixing this one. The rest of the app works without it.",
                    "AI 에이전트에는 동작하는 omp가 필요합니다. 다른 omp.exe를 고르거나 새로 설치하세요(이 파일을 고친 뒤 다시 시도해도 됩니다). 나머지 기능은 omp 없이도 그대로 동작합니다.")
                : T("The AI agent needs omp. The rest of the app works without it.", "AI 에이전트에는 omp가 필요합니다. 나머지 기능은 omp 없이도 그대로 동작합니다.");
            _page.Post(ChatPageMessages.ActionNotice("info", text, actions, T("Also add omp to my user PATH (off by default)", "사용자 PATH에도 추가(기본 꺼짐)")));
        }

        /// <summary>nanumcsv://omp/…·nanumcsv://setup/… 처리. 아니면 false.</summary>
        private bool TryHandleOmpUrl(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url.StartsWith(SetupUrlPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string kind = url[SetupUrlPrefix.Length..].Split('?')[0].Trim('/').ToLowerInvariant();
                var reason = kind switch { "login" => AgentSetupReason.Login, "webview" => AgentSetupReason.WebView, _ => AgentSetupReason.Omp };
                SetupNeeded?.Invoke(reason, _lastDiscovery?.ToDiagnosticText() ?? "");
                return true;
            }
            if (!url.StartsWith(OmpUrlPrefix, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = url[OmpUrlPrefix.Length..];
            int q = rest.IndexOf('?');
            string verb = (q < 0 ? rest : rest[..q]).Trim('/').ToLowerInvariant();
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (q >= 0)
                foreach (string pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = pair.IndexOf('=');
                    string key = eq < 0 ? pair : pair[..eq];
                    string value = eq < 0 ? "" : pair[(eq + 1)..];
                    try { args[key] = Uri.UnescapeDataString(value); } catch (UriFormatException) { }
                }
            bool addToPath = args.TryGetValue("path", out var p) && p == "1";

            switch (verb)
            {
                case "browse": _ = BrowseOmpAsync(); break;
                case "install": _ = InstallOmpFromCandidateAsync(args.GetValueOrDefault("src"), addToPath); break;
                case "download": _ = DownloadOmpAsync(addToPath); break;
                case "retry": _ = RetryOmpAsync(); break;
                default: return false;
            }
            return true;
        }

        /// <summary>같은 설정으로 처음부터 다시 시작한다(PATH는 레지스트리에서 다시 읽는다).</summary>
        private async Task RetryOmpAsync()
        {
            if (_disposed || _ompActionBusy) return;
            try { await StartCoreAsync(_baseDir, null, default); }
            catch (Exception ex) { _log.Note("omp retry failed: " + ex); }
        }

        private void ApplyOmpPath(string path)
        {
            _options = _options with { OmpPath = path };
            OmpPathPicked?.Invoke(path);
        }

        private async Task BrowseOmpAsync()
        {
            if (_disposed || _ompActionBusy) return;
            var picked = Dialogs.PickFiles(T("Choose omp", "omp 실행 파일 선택"),
                T("omp (omp*.exe;omp*.cmd)|omp*.exe;omp*.cmd|All files (*.*)|*.*", "omp (omp*.exe;omp*.cmd)|omp*.exe;omp*.cmd|모든 파일 (*.*)|*.*"));
            if (picked == null || picked.Count == 0) return;
            ApplyOmpPath(picked[0]);
            await RetryOmpAsync();
        }

        private async Task InstallOmpFromCandidateAsync(string? src, bool addToPath)
        {
            if (_disposed || _ompActionBusy) return;
            // 알림의 주소는 화면 쪽에서 오므로 방금 찾은 후보만 받는다(임의 파일을 실행해 보는 일을 막는다).
            if (string.IsNullOrEmpty(src) || _lastDiscovery == null
                || !_lastDiscovery.Candidates.Any(c => string.Equals(c.Path, src, StringComparison.OrdinalIgnoreCase)))
                return;
            await RunOmpInstallAsync(ct => _svc.InstallOmpFromFile(src, addToPath, ct), T("Installing omp…", "omp를 설치하는 중…"));
        }

        private async Task DownloadOmpAsync(bool addToPath)
        {
            if (_disposed || _ompActionBusy) return;
            bool ok = Dialogs.Confirm(T("Download omp", "omp 다운로드"),
                T("Download the latest omp release (about 240 MB) from github.com/can1357/oh-my-pi, verify its SHA-256 and install it to %LOCALAPPDATA%\\omp\\omp.exe?",
                  "github.com/can1357/oh-my-pi의 최신 omp 릴리스(약 240 MB)를 내려받아 SHA-256을 확인하고 %LOCALAPPDATA%\\omp\\omp.exe에 설치할까요?"));
            if (!ok) return;
            var ui = _svc.Ui ?? SynchronizationContext.Current;
            var progress = new UiProgress(ui, p => { if (!_disposed && _ompActionBusy) SetStatus(ProgressText(p), false); });
            await RunOmpInstallAsync(ct => _svc.DownloadOmp(progress, addToPath, ct), T("Downloading omp…", "omp를 내려받는 중…"));
        }

        private string ProgressText(OmpDownloadProgress p) => p.Stage switch
        {
            "query" => T("Looking up the latest omp release…", "최신 omp 릴리스를 찾는 중…"),
            "download" => p.Total is long t && t > 0
                ? T($"Downloading omp… {p.Received * 100 / t}% ({p.Received >> 20} / {t >> 20} MB)", $"omp를 내려받는 중… {p.Received * 100 / t}% ({p.Received >> 20} / {t >> 20} MB)")
                : T($"Downloading omp… {p.Received >> 20} MB", $"omp를 내려받는 중… {p.Received >> 20} MB"),
            "verify" => T("Verifying the download…", "내려받은 파일을 확인하는 중…"),
            _ => T("Installing omp…", "omp를 설치하는 중…"),
        };

        private async Task RunOmpInstallAsync(Func<CancellationToken, Task<OmpInstallResult>> install, string statusText)
        {
            _ompActionBusy = true;
            _ompActionCts = new CancellationTokenSource();
            SetStatus(statusText, false);
            OmpInstallResult result;
            try { result = await install(_ompActionCts.Token); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new OmpInstallResult(false, null, T("Installing omp failed: ", "omp 설치에 실패했습니다: ") + ex.Message, OmpInstallError.Io);
            }
            finally
            {
                _ompActionBusy = false;
                _ompActionCts?.Dispose();
                _ompActionCts = null;
            }
            if (_disposed) return;

            if (!result.Ok)
            {
                string text = result.Message;
                SetStatus(text, true);
                _stream.Emit(ChatPageMessages.Notice(result.Error == OmpInstallError.Cancelled ? "info" : "error", text));
                if (_lastDiscovery != null) PostOmpActions(_lastDiscovery);
                return;
            }

            _stream.Emit(ChatPageMessages.Notice("info", result.Message));
            // 설정에 적어 둔 경로가 쓸 수 없는 파일을 가리키고 있으면(자동 탐색보다 우선하므로) 새로 설치한 파일로 바꾼다.
            if (!string.IsNullOrWhiteSpace(_options.OmpPath) && result.InstalledPath != null
                && _lastDiscovery is { Checked: { Count: > 0 } c } && c[0].Status == OmpLocationStatus.Found)
                ApplyOmpPath(result.InstalledPath);
            await RetryOmpAsync();
        }

        /// <summary>호스트가 UI 스레드가 아닌 곳에서 Report해도 UI 스레드에서 처리한다.</summary>
        private sealed class UiProgress : IProgress<OmpDownloadProgress>
        {
            private readonly SynchronizationContext? _ui;
            private readonly Action<OmpDownloadProgress> _handler;

            public UiProgress(SynchronizationContext? ui, Action<OmpDownloadProgress> handler) { _ui = ui; _handler = handler; }

            public void Report(OmpDownloadProgress value)
            {
                if (_ui != null) _ui.Post(_ => _handler(value), null);
                else _handler(value);
            }
        }
    }
}
