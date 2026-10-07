using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent.Setup
{
    /// <summary>[로그인]이 진행되는 동안 omp가 요청하는 화면 동작(UI 스레드에서 호출).</summary>
    /// <param name="OpenUrl">인증 주소(http/https)와 안내문. 브라우저로 연다.</param>
    /// <param name="Progress">진행 안내 한 줄.</param>
    /// <param name="AskInput">비밀이 아닌 입력(제목, 안내, 취소 토큰) → 값, 취소하면 null. 브라우저에서 로그인이 먼저 끝나면 토큰이 취소된다.</param>
    internal sealed record LoginUi(Action<string, string?> OpenUrl, Action<string> Progress, Func<string, string, CancellationToken, Task<string?>> AskInput);

    /// <summary>omp를 모델 호출 없이 잠깐 띄워 로그인 제공자·모델 목록을 읽고 로그인을 대신 실행한다(테스트는 가짜).</summary>
    internal interface IOmpAccountProbe : IDisposable
    {
        Task<AccountSnapshot> InspectAsync(string exePath, CancellationToken ct);
        Task<LoginResult> LoginAsync(string exePath, string providerId, LoginUi ui, CancellationToken ct);
    }

    /// <summary>
    /// 실제 구현: <c>omp --mode rpc --no-session</c>을 띄워 get_login_providers·get_available_models를 묻고, login RPC로 OAuth를 진행한다.
    /// omp RPC login은 인증 주소를 open_url extension_ui_request로 알리고, 비밀 입력(API 키)은 거절한다(터미널을 안내).
    /// 로그인이 성공하면 같은 프로세스가 새 자격 증명을 모를 수 있으니 다음 검사 때 프로세스를 새로 띄운다.
    /// </summary>
    internal sealed class OmpRpcAccountProbe : IOmpAccountProbe
    {
        private static readonly Regex NeedsTerminalPattern = new(@"secret|terminal|\bTUI\b|api[ _-]?key", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IOmpProcessFactory _factory;
        private readonly Func<bool> _korean;
        private readonly TimeSpan _loginTimeout;
        private readonly TimeSpan _requestTimeout;
        private OmpRpcClient? _client;
        private string? _exe;
        private bool _stale;

        public OmpRpcAccountProbe(IOmpProcessFactory? factory = null, Func<bool>? korean = null, TimeSpan? loginTimeout = null, TimeSpan? requestTimeout = null)
        {
            _factory = factory ?? new OmpProcessFactory();
            _korean = korean ?? (() => Loc.CurrentLanguage == "ko");
            _loginTimeout = loginTimeout ?? TimeSpan.FromMinutes(5);
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        }

        private string T(string en, string ko) => _korean() ? ko : en;

        private async Task<OmpRpcClient> EnsureStartedAsync(string exe, CancellationToken ct)
        {
            if (_client is { IsConnected: true } existing && !_stale && string.Equals(_exe, exe, StringComparison.OrdinalIgnoreCase)) return existing;
            DisposeClient();
            Directory.CreateDirectory(OmpLaunch.TempDirectory);
            var client = new OmpRpcClient(_factory, null, NullRpcLog.Instance, TimeSpan.FromSeconds(30));
            var launch = new OmpLaunchInfo(exe,
                new[] { "--mode", "rpc", "--no-session", "--no-extensions", "--cwd", OmpLaunch.TempDirectory },
                OmpLaunch.TempDirectory, Path.Combine(OmpLaunch.TempDirectory, "omp.stderr-setup.log"));
            try { await client.StartAsync(launch, ct); }
            catch
            {
                string? stderr = client.LastErrorLine;
                client.Dispose();
                if (!string.IsNullOrEmpty(stderr)) throw new InvalidOperationException(stderr);
                throw;
            }
            _client = client;
            _exe = exe;
            _stale = false;
            return client;
        }

        public async Task<AccountSnapshot> InspectAsync(string exePath, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var client = await EnsureStartedAsync(exePath, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_requestTimeout);
                var providersTask = client.RequestAsync("get_login_providers", null, timeout.Token);
                var modelsTask = client.RequestAsync("get_available_models", null, timeout.Token);

                var providers = new List<LoginProvider>();
                string? error = null;
                try
                {
                    var reply = await providersTask;
                    if (reply.Bool("success") == true)
                        foreach (var p in reply.Child("data").Child("providers").Items())
                        {
                            if (p.Bool("available") == false) continue;
                            string id = p.Str("id");
                            if (id.Length > 0) providers.Add(new LoginProvider(id, p.Str("name", id), p.Bool("authenticated") == true));
                        }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = T("omp did not list the sign-in providers in time.", "omp가 로그인 제공자 목록을 제때 주지 않았습니다."); }

                int models = 0;
                try
                {
                    var reply = await modelsTask;
                    if (reply.Bool("success") == true)
                    {
                        var set = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var m in reply.Child("data").Child("models").Items())
                        {
                            string provider = m.Str("provider"), id = m.Str("id");
                            if (provider.Length > 0 && id.Length > 0) set.Add(provider + "/" + id);
                        }
                        models = set.Count;
                    }
                    else error = reply.Str("error");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { error = T("omp did not list the models in time.", "omp가 모델 목록을 제때 주지 않았습니다."); }

                return new AccountSnapshot(true, client.ProtocolVersion, (int)sw.ElapsedMilliseconds, error, providers, models);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { DisposeClient(); throw; }
            catch (Exception ex)
            {
                DisposeClient();
                return AccountSnapshot.Failed(ex is TimeoutException ? T("omp did not become ready in time.", "omp가 제때 준비되지 않았습니다.") : ex.Message, (int)sw.ElapsedMilliseconds);
            }
        }

        public async Task<LoginResult> LoginAsync(string exePath, string providerId, LoginUi ui, CancellationToken ct)
        {
            OmpRpcClient client;
            try { client = await EnsureStartedAsync(exePath, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new LoginResult(false, T("Cannot start omp: ", "omp를 시작할 수 없습니다: ") + ex.Message, false); }

            var pending = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>();

            async Task AnswerInputAsync(string id, string title, string prompt, CancellationTokenSource cts)
            {
                string? value = null;
                try { value = await ui.AskInput(title, prompt, cts.Token); }
                catch (OperationCanceledException) { }
                pending.TryRemove(id, out _);
                // 로그인이 이미 끝났거나(omp가 취소를 보냄) 사용자가 작업을 취소했으면 답하지 않는다.
                if (cts.IsCancellationRequested) return;
                client.Send(value == null ? RpcProtocol.UiCancelled(id) : RpcProtocol.UiValue(id, value));
            }

            void OnFrame(JsonElement frame)
            {
                if (frame.Str("type") != "extension_ui_request") return;
                string id = frame.Str("id"), method = frame.Str("method");
                string message = frame.Str("message");
                if (message.Length == 0) message = frame.Str("instructions");
                switch (method)
                {
                    case "open_url":
                        string url = frame.Str("url");
                        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                            ui.OpenUrl(url, message.Length > 0 ? message : null);
                        break;
                    case "notify":
                    case "setStatus":
                        {
                            string text = message.Length > 0 ? message : frame.Str("statusText");
                            if (text.Length > 0) ui.Progress(text);
                            break;
                        }
                    case "input":
                        {
                            if (id.Length == 0) break;
                            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            if (pending.TryAdd(id, cts))
                                _ = AnswerInputAsync(id, frame.Str("title"), message.Length > 0 ? message : frame.Str("title"), cts);
                            break;
                        }
                    case "cancel":
                        if (pending.TryRemove(frame.Str("targetId"), out var target)) target.Cancel();
                        break;
                    default:
                        // 그 밖의 대화(확인·선택·편집기)는 로그인에 필요 없다: 기다리지 않게 취소로 답한다.
                        if (id.Length > 0 && method is "confirm" or "select" or "editor") client.Send(RpcProtocol.UiCancelled(id));
                        break;
                }
            }

            client.Frame += OnFrame;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_loginTimeout);
                JsonElement reply;
                try { reply = await client.RequestAsync("login", o => o["providerId"] = providerId, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    DisposeClient();
                    return new LoginResult(false, T("Sign-in timed out.", "로그인 시간이 초과되었습니다."), false);
                }
                catch (OperationCanceledException) { DisposeClient(); throw; }
                catch (Exception ex)
                {
                    DisposeClient();
                    return new LoginResult(false, ex.Message, false);
                }

                if (reply.Bool("success") == true)
                {
                    _stale = true;   // 새 자격 증명은 다음 검사에서 새 프로세스로 읽는다
                    return new LoginResult(true, T("Signed in.", "로그인했습니다."), false);
                }
                string error = reply.Str("error");
                bool terminal = NeedsTerminalPattern.IsMatch(error);
                string message = terminal
                    ? T("This provider needs a secret (API key) that cannot be entered here. Sign in from a terminal (omp login) or set the provider's environment variable.\n",
                        "이 제공자는 여기서 입력할 수 없는 비밀 값(API 키)이 필요합니다. 터미널에서 로그인(omp login)하거나 제공자의 환경 변수를 설정하세요.\n") + error
                    : error.Length > 0 ? error : T("Sign-in failed.", "로그인에 실패했습니다.");
                return new LoginResult(false, message, terminal);
            }
            finally
            {
                client.Frame -= OnFrame;
                foreach (var cts in pending.Values) cts.Cancel();   // 로그인이 끝났으니 열려 있는 입력 칸을 닫는다
                pending.Clear();
            }
        }

        private void DisposeClient()
        {
            var c = _client;
            _client = null;
            c?.Dispose();
        }

        public void Dispose() => DisposeClient();
    }
}
