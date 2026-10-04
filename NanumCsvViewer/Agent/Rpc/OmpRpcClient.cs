using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace NanumCsvViewer.Agent.Rpc
{
    /// <summary>ready 프레임이 알려 준 전송 정보.</summary>
    internal sealed record RpcReadyInfo(int ChosenProtocol, IReadOnlyList<int> SupportedProtocols, long MaxFrameBytes, long MaxReassembledFrameBytes);

    /// <summary>자식이 스스로 끝났을 때(우리가 Stop한 경우는 아님).</summary>
    internal sealed record OmpExitInfo(int? ExitCode, string? LastErrorLine, bool WasReady);

    internal sealed class OmpExitedException : Exception
    {
        public OmpExitedException(string message) : base(message) { }
    }

    /// <summary>
    /// omp 자식과의 JSONL 전송 계층. 한 번만 쓰는 객체: StartAsync → (Request/Send)* → Stop.
    /// 읽기 전용 스레드가 줄 단위로 읽어(1 MiB 초과 줄 버림, v2 rpc_chunk 복원) 프레임을 UI 컨텍스트로 넘긴다.
    /// 모든 콜백은 세대 번호(Stop 때 증가)로 걸러져 죽은 자식의 늦은 프레임이 새 상태에 닿지 않는다.
    /// 쓰기는 단일 소비자 큐로 직렬화한다(순서 보장, UI 스레드 비차단).
    /// </summary>
    internal sealed class OmpRpcClient : IDisposable
    {
        private readonly IOmpProcessFactory _factory;
        private readonly IRpcLog _log;
        private readonly TimeSpan _readyTimeout;
        private readonly SynchronizationContext? _fallbackUi;

        private SynchronizationContext? _ui;
        private IOmpProcess? _process;
        private Channel<string>? _outbox;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private TaskCompletionSource<RpcReadyInfo>? _readyTcs;
        private long _nextId;
        private int _generation;
        private volatile bool _outboundAllowed;
        private volatile bool _stopping;
        private volatile bool _everReady;
        private bool _started;
        private long _maxFrameBytes = RpcProtocol.MaxFrameBytes;

        public OmpRpcClient(IOmpProcessFactory factory, SynchronizationContext? ui = null, IRpcLog? log = null, TimeSpan? readyTimeout = null)
        {
            _factory = factory;
            _fallbackUi = ui;
            _log = log ?? NullRpcLog.Instance;
            _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(45);
        }

        /// <summary>세션 이벤트·extension_ui_request·host_tool_call/cancel 등 상관관계 없는 모든 프레임(UI 컨텍스트).</summary>
        public event Action<JsonElement>? Frame;
        /// <summary>자식이 스스로 끝남(UI 컨텍스트). 대기 중인 요청은 OmpExitedException으로 끝난다.</summary>
        public event Action<OmpExitInfo>? Exited;
        /// <summary>버려진 프레임·잘못된 청크 등 사용자에게 알릴 만한 진단(UI 컨텍스트).</summary>
        public event Action<string>? Diagnostic;

        public int Generation => Volatile.Read(ref _generation);
        public bool IsConnected => _outboundAllowed;
        public int ProtocolVersion { get; private set; } = 1;
        public int? ProcessId => _process?.ProcessId;
        public string? LastErrorLine => _process?.LastErrorLine;

        public string NextId() => "req-" + Interlocked.Increment(ref _nextId);

        // ---- 시작 ----------------------------------------------------------------------------------------------

        /// <summary>자식을 띄우고 ready → (v2 제공 시) negotiate_protocol까지 마친다. 그 뒤부터 명령을 보낼 수 있다.</summary>
        public async Task<RpcReadyInfo> StartAsync(OmpLaunchInfo launch, CancellationToken cancellation = default)
        {
            if (_started) throw new InvalidOperationException("OmpRpcClient is single-use");
            _started = true;
            _ui = SynchronizationContext.Current ?? _fallbackUi;
            int gen = Interlocked.Increment(ref _generation);
            _readyTcs = new TaskCompletionSource<RpcReadyInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

            var process = _factory.Start(launch);
            _process = process;
            _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            _log.Header($"omp rpc start pid={process.ProcessId} exe={launch.ExePath} args={string.Join(' ', launch.Arguments)}");
            _ = Task.Run(() => WriteLoopAsync(process, _outbox.Reader));
            _ = Task.Run(() => ReadLoopAsync(process, gen));

            RpcReadyInfo ready;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cts.CancelAfter(_readyTimeout);
                ready = await _readyTcs.Task.WaitAsync(cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                Stop(force: true);
                throw new TimeoutException("omp did not send the ready frame in time");
            }
            catch
            {
                Stop(force: true);
                throw;
            }

            if (ready.ChosenProtocol == 0)
            {
                Stop(force: true);
                throw new NotSupportedException("omp offers no supported RPC protocol version");
            }

            int chosen = 1;
            if (ready.ChosenProtocol == 2)
            {
                try
                {
                    var response = await RequestAsync(id => RpcProtocol.Negotiate(id, 2), cancellation).ConfigureAwait(true);
                    if (response.Bool("success") == true) chosen = 2;
                    else _log.Note("negotiate_protocol rejected: " + response.Str("error"));
                }
                catch (OperationCanceledException) { Stop(force: true); throw; }
            }
            ProtocolVersion = chosen;
            return ready with { ChosenProtocol = chosen };
        }

        // ---- 보내기 --------------------------------------------------------------------------------------------

        /// <summary>한 프레임을 큐에 넣는다. ready 전·종료 후·상한 초과면 false.</summary>
        public bool Send(string json)
        {
            var outbox = _outbox;
            if (outbox == null || !_outboundAllowed) return false;
            int bytes = RpcProtocol.Utf8Length(json);
            if (bytes > _maxFrameBytes)
            {
                _log.Note($"not sent: frame is {bytes} bytes (limit {_maxFrameBytes})");
                return false;
            }
            return outbox.Writer.TryWrite(json);
        }

        /// <summary>id를 받아 프레임을 만들고 보낸다. 보냈으면 true와 같은 id의 response로 완료되는 Task(UI 컨텍스트에서 완료).
        /// ready 전·종료 후·크기 상한 초과로 보내지 못하면 false.</summary>
        public bool TryRequest(Func<string, string> build, out Task<JsonElement> response, CancellationToken cancellation = default)
        {
            string id = NextId();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            if (!Send(build(id)))
            {
                _pending.TryRemove(id, out _);
                response = Task.FromException<JsonElement>(new OmpExitedException("omp is not connected"));
                return false;
            }
            if (cancellation.CanBeCanceled)
            {
                var reg = cancellation.Register(() =>
                {
                    if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(cancellation);
                });
                tcs.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
            }
            response = tcs.Task;
            return true;
        }

        public bool TryRequest(string type, Action<System.Text.Json.Nodes.JsonObject>? fill, out Task<JsonElement> response) =>
            TryRequest(id => RpcProtocol.Command(type, id, fill), out response);

        /// <summary>TryRequest의 Task 전용 형태: 보내지 못하면 OmpExitedException으로 실패한 Task.</summary>
        public Task<JsonElement> RequestAsync(Func<string, string> build, CancellationToken cancellation = default)
        {
            TryRequest(build, out var task, cancellation);
            return task;
        }

        public Task<JsonElement> RequestAsync(string type, Action<System.Text.Json.Nodes.JsonObject>? fill = null, CancellationToken cancellation = default) =>
            RequestAsync(id => RpcProtocol.Command(type, id, fill), cancellation);

        /// <summary>응답을 기다리지 않는 명령. 응답은 Frame 이벤트로 온다(실패 응답 표시용).</summary>
        public bool SendCommand(string type, Action<System.Text.Json.Nodes.JsonObject>? fill = null) =>
            Send(RpcProtocol.Command(type, NextId(), fill));

        // ---- 종료 ----------------------------------------------------------------------------------------------

        /// <summary>stdin을 닫아 정상 종료를 유도하고(2초 뒤에도 안 끝나면 강제 종료), 대기 요청을 취소한다. force면 즉시 kill.</summary>
        public void Stop(bool force = false)
        {
            var process = _process;
            if (process == null) return;
            _process = null;
            _stopping = true;
            _outboundAllowed = false;
            Interlocked.Increment(ref _generation);
            FailPending(null);
            _readyTcs?.TrySetException(new OmpExitedException("omp stopped"));
            _outbox?.Writer.TryComplete();
            _log.Note(force ? "stop (kill)" : "stop");
            if (force)
            {
                process.Kill();
                _ = Task.Run(() => { try { process.Dispose(); } catch { } });
                return;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await Task.WhenAny(process.Exited, Task.Delay(2000)).ConfigureAwait(false) != process.Exited)
                        process.Kill();
                }
                finally { try { process.Dispose(); } catch { } }
            });
        }

        public void Dispose() => Stop(force: true);

        // ---- 쓰기 루프 -----------------------------------------------------------------------------------------

        private async Task WriteLoopAsync(IOmpProcess process, ChannelReader<string> reader)
        {
            Stream stdin = process.StandardInput;
            try
            {
                await foreach (string json in reader.ReadAllAsync().ConfigureAwait(false))
                {
                    _log.Sent(json);
                    byte[] bytes = new byte[Encoding.UTF8.GetMaxByteCount(json.Length) + 1];
                    int n = Encoding.UTF8.GetBytes(json, 0, json.Length, bytes, 0);
                    bytes[n++] = (byte)'\n';
                    await stdin.WriteAsync(bytes.AsMemory(0, n)).ConfigureAwait(false);
                    await stdin.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Note("write failed: " + ex.Message);
            }
            finally
            {
                try { stdin.Close(); } catch { }
            }
        }

        // ---- 읽기 루프 -----------------------------------------------------------------------------------------

        private async Task ReadLoopAsync(IOmpProcess process, int gen)
        {
            var splitter = new RpcLineSplitter();
            var chunks = new RpcChunkAssembler();
            var buffer = new byte[8192];
            RpcLineHandler onLine = line => HandleLine(line, gen, chunks);
            Action<long> onDropped = n =>
            {
                _log.Note($"dropped oversize line ({n} bytes)");
                PostDiagnostic(gen, $"dropped an oversize omp frame ({n} bytes)");
            };
            try
            {
                Stream stdout = process.StandardOutput;
                while (true)
                {
                    int n = await stdout.ReadAsync(buffer).ConfigureAwait(false);
                    if (n == 0) break;
                    splitter.Feed(buffer.AsSpan(0, n), onLine, onDropped);
                }
                splitter.Complete(onLine, onDropped);
            }
            catch (Exception ex)
            {
                if (!_stopping) _log.Note("read failed: " + ex.Message);
            }
            await OnEofAsync(process, gen).ConfigureAwait(false);
        }

        private void HandleLine(ReadOnlySpan<byte> line, int gen, RpcChunkAssembler chunks)
        {
            string text = Encoding.UTF8.GetString(line);
            _log.Received(text);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line.ToArray());
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                PostDiagnostic(gen, "ignored a malformed omp frame");
                return;
            }
            if (!root.IsObject())
            {
                PostDiagnostic(gen, "ignored a non-object omp frame");
                return;
            }

            string type = root.Str("type");
            if (type == "rpc_chunk")
            {
                var result = chunks.Accept(root);
                if (result.Status == ChunkStatus.NeedMore) return;
                if (result.Status == ChunkStatus.Error)
                {
                    _log.Note(result.Error!);
                    PostDiagnostic(gen, result.Error!);
                    return;
                }
                string? json = RpcChunkAssembler.DecodeStrict(result.Payload!);
                if (json == null)
                {
                    PostDiagnostic(gen, "rpc_chunk: reassembled frame is not valid UTF-8");
                    return;
                }
                _log.Received($"[reassembled {result.Payload!.Length} bytes] " + (json.Length > 2048 ? json[..2048] + "…" : json));
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    PostDiagnostic(gen, "rpc_chunk: reassembled frame is not valid JSON");
                    return;
                }
                if (!root.IsObject() || root.Str("type") == "rpc_chunk")
                {
                    PostDiagnostic(gen, "rpc_chunk: reassembled frame is not a plain object");
                    return;
                }
                type = root.Str("type");
            }
            else
            {
                string? interrupted = chunks.NoteOtherFrame();
                if (interrupted != null)
                {
                    _log.Note(interrupted);
                    PostDiagnostic(gen, interrupted);
                }
            }

            Dispatch(type, root, gen);
        }

        private void Dispatch(string type, JsonElement root, int gen)
        {
            if (type == "ready")
            {
                HandleReady(root);
                return;
            }
            if (type == "response")
            {
                string id = root.Str("id");
                if (id.Length > 0 && _pending.TryRemove(id, out var tcs))
                {
                    Post(gen, () => tcs.TrySetResult(root), () => tcs.TrySetCanceled());
                    return;
                }
            }
            Post(gen, () => Frame?.Invoke(root));
        }

        private void HandleReady(JsonElement root)
        {
            if (_readyTcs == null || _readyTcs.Task.IsCompleted) return;
            var versions = new List<int>();
            foreach (var v in root.Child("supportedProtocolVersions").Items())
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) versions.Add(n);
            long maxFrame = root.Int("maxFrameBytes", RpcProtocol.MaxFrameBytes);
            long maxReassembled = root.Int("maxReassembledFrameBytes", RpcProtocol.MaxReassembledFrameBytes);
            _maxFrameBytes = Math.Min(Math.Max(maxFrame, 4096), RpcProtocol.MaxFrameBytes);
            _everReady = true;
            _outboundAllowed = true;
            _readyTcs.TrySetResult(new RpcReadyInfo(RpcProtocol.ChooseProtocol(root), versions, maxFrame, maxReassembled));
        }

        private async Task OnEofAsync(IOmpProcess process, int gen)
        {
            if (_stopping || gen != Generation) return;
            int? code = null;
            try
            {
                var done = await Task.WhenAny(process.Exited, Task.Delay(1500)).ConfigureAwait(false);
                if (done == process.Exited) code = process.Exited.Result;
            }
            catch { }
            string? lastError = process.LastErrorLine;
            _log.Note($"omp output closed (exit={code?.ToString() ?? "?"}) {lastError}");
            bool wasReady = _everReady;
            _outboundAllowed = false;
            _outbox?.Writer.TryComplete();
            var failure = new OmpExitedException(lastError ?? "omp exited");
            _readyTcs?.TrySetException(failure);
            Post(gen, () =>
            {
                FailPending(failure);
                Exited?.Invoke(new OmpExitInfo(code, lastError, wasReady));
            });
        }

        private void FailPending(Exception? error)
        {
            foreach (var key in _pending.Keys.ToArray())
            {
                if (!_pending.TryRemove(key, out var tcs)) continue;
                if (error != null) tcs.TrySetException(error); else tcs.TrySetCanceled();
            }
        }

        private void PostDiagnostic(int gen, string text) => Post(gen, () => Diagnostic?.Invoke(text));

        /// <summary>UI 컨텍스트로 순서대로 넘긴다. 세대가 바뀌었으면 버린다. 핸들러 예외는 로그에만 남긴다.</summary>
        private void Post(int gen, Action action, Action? dropped = null)
        {
            void Run()
            {
                if (gen != Generation) { dropped?.Invoke(); return; }
                try { action(); }
                catch (Exception ex) { _log.Note("handler failed: " + ex); }
            }
            var ui = _ui;
            if (ui == null) Run();
            else ui.Post(_ => Run(), null);
        }
    }
}
