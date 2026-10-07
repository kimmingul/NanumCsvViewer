using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    // ==== 테스트 보조: 가짜 omp 프로세스(메모리 스트림), UI 스레드, 페이지, 시계 ===================================

    /// <summary>omp stdout 쪽: 테스트가 줄을 밀어 넣고 호스트가 읽는다. Complete()가 EOF.</summary>
    internal sealed class OutPipe : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _position;

        public void Push(byte[] data) => _channel.Writer.TryWrite(data);
        public void PushLine(string json) => Push(Encoding.UTF8.GetBytes(json + "\n"));
        public void Complete() => _channel.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_current != null && _position < _current.Length)
                {
                    int n = Math.Min(buffer.Length, _current.Length - _position);
                    _current.AsMemory(_position, n).CopyTo(buffer);
                    _position += n;
                    return n;
                }
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (_channel.Reader.TryRead(out var next)) { _current = next; _position = 0; }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>omp stdin 쪽: 호스트가 쓴 바이트를 줄로 잘라 Lines에 쌓는다. Close()가 EOF.</summary>
    internal sealed class InCapture : Stream
    {
        private readonly List<byte> _buffer = new();
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_buffer)
            {
                for (int i = 0; i < count; i++)
                {
                    byte b = buffer[offset + i];
                    if (b == (byte)'\n')
                    {
                        Lines.Writer.TryWrite(Encoding.UTF8.GetString(_buffer.ToArray()));
                        _buffer.Clear();
                    }
                    else _buffer.Add(b);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            Lines.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override void Close()
        {
            Lines.Writer.TryComplete();
            base.Close();
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>가짜 omp: 받은 명령을 기록하고 기본 응답을 돌려준다. Handlers로 명령별 응답을 바꾼다.</summary>
    internal sealed class FakeOmpProcess : IOmpProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<JsonElement> _received = new();
        private readonly string _readyLine;

        public FakeOmpProcess(OmpLaunchInfo launch, string? readyLine = null)
        {
            Launch = launch;
            _readyLine = readyLine ?? "{\"type\":\"ready\",\"protocolVersion\":1,\"supportedProtocolVersions\":[1,2],\"maxFrameBytes\":1048576,\"maxReassembledFrameBytes\":67108864}";
        }

        public OmpLaunchInfo Launch { get; }
        public OutPipe Out { get; } = new();
        public InCapture In { get; } = new();
        public bool Killed { get; private set; }
        public bool AutoReady { get; set; } = true;
        public int Pid { get; set; } = 4242;

        /// <summary>명령 type → 응답 줄(id 포함)을 직접 만들거나, null이면 기본 응답, ""이면 응답 없음.</summary>
        public Dictionary<string, Func<JsonElement, string?>> Handlers { get; } = new();
        public JsonObject State { get; } = new()
        {
            ["model"] = new JsonObject { ["provider"] = "anthropic", ["id"] = "claude-a" },
            ["thinkingLevel"] = "low",
            ["sessionFile"] = "C:\\sessions\\s1.jsonl",
            ["sessionName"] = "",
            ["contextUsage"] = new JsonObject { ["percent"] = 12.5 },
        };

        public Stream StandardInput => In;
        public Stream StandardOutput => Out;
        public int? ProcessId => Pid;
        public string? LastErrorLine { get; set; }
        public Task<int> Exited => _exit.Task;

        public IReadOnlyList<JsonElement> ReceivedSnapshot()
        {
            lock (_received) return _received.ToArray();
        }

        public void Begin()
        {
            _ = Task.Run(ReadCommandsAsync);
            if (AutoReady) Out.PushLine(_readyLine);
        }

        public void Emit(string json) => Out.PushLine(json);
        public void Emit(JsonNode node) => Out.PushLine(node.ToJsonString());

        /// <summary>호스트가 요청하지 않았는데 죽은 경우.</summary>
        public void Crash(int code, string? stderrLine = null)
        {
            LastErrorLine = stderrLine;
            _exit.TrySetResult(code);
            Out.Complete();
        }

        public void Kill()
        {
            Killed = true;
            _exit.TrySetResult(-1);
            Out.Complete();
        }

        public void Dispose() { }

        private async Task ReadCommandsAsync()
        {
            await foreach (string line in In.Lines.Reader.ReadAllAsync())
            {
                JsonElement cmd;
                using (var doc = JsonDocument.Parse(line)) cmd = doc.RootElement.Clone();
                lock (_received) _received.Add(cmd);
                string? reply = Respond(cmd);
                if (!string.IsNullOrEmpty(reply)) Out.PushLine(reply);
            }
            _exit.TrySetResult(0);
            Out.Complete();
        }

        private string? Respond(JsonElement cmd)
        {
            string type = cmd.Str("type");
            string id = cmd.Str("id");
            if (type is "host_tool_result" or "extension_ui_response" or "host_tool_update") return null;
            if (Handlers.TryGetValue(type, out var handler))
            {
                string? custom = handler(cmd);
                if (custom != null) return custom;
            }
            return Ok(id, type, DefaultData(type));
        }

        private JsonNode? DefaultData(string type) => type switch
        {
            "set_host_tools" => new JsonObject { ["toolNames"] = new JsonArray("csv.test") },
            "get_state" => JsonNode.Parse(State.ToJsonString()),
            "get_available_commands" => new JsonObject
            {
                ["commands"] = new JsonArray(new JsonObject { ["name"] = "/compact", ["description"] = "Compact the context", ["input"] = new JsonObject { ["hint"] = "[focus]" } }),
            },
            "get_available_models" => new JsonObject
            {
                ["models"] = new JsonArray(
                    new JsonObject { ["provider"] = "openai", ["id"] = "gpt-x" },
                    new JsonObject { ["provider"] = "anthropic", ["id"] = "claude-a" },
                    new JsonObject { ["provider"] = "anthropic", ["id"] = "claude-a" }),
            },
            "get_available_thinking_levels" => new JsonObject { ["levels"] = new JsonArray("off", "low", "high") },
            "get_login_providers" => new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject { ["id"] = "anthropic", ["name"] = "Anthropic (Claude Pro/Max)", ["authenticated"] = true }),
            },
            "remove_queued_message" => new JsonObject { ["removed"] = true },
            _ => new JsonObject(),
        };

        public static string Ok(string id, string command, JsonNode? data)
        {
            var o = new JsonObject { ["id"] = id, ["type"] = "response", ["command"] = command, ["success"] = true };
            if (data != null) o["data"] = data;
            return o.ToJsonString();
        }

        public static string Fail(string id, string command, string error) =>
            new JsonObject { ["id"] = id, ["type"] = "response", ["command"] = command, ["success"] = false, ["error"] = error }.ToJsonString();

        public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> predicate, int timeoutMs = 5000, int startIndex = 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var all = ReceivedSnapshot();
                for (int i = startIndex; i < all.Count; i++)
                    if (predicate(all[i])) return all[i];
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException("frame not received; got: " + string.Join(", ", all.Select(f => f.Str("type"))));
                await Task.Delay(5);
            }
        }

        public Task<JsonElement> WaitForTypeAsync(string type, int timeoutMs = 5000) => WaitForAsync(f => f.Str("type") == type, timeoutMs);
    }

    internal sealed class FakeOmpFactory : IOmpProcessFactory
    {
        public List<FakeOmpProcess> Processes { get; } = new();
        public Action<FakeOmpProcess>? Configure { get; set; }
        public Func<OmpLaunchInfo, FakeOmpProcess>? Create { get; set; }

        public FakeOmpProcess Last => Processes[^1];

        public IOmpProcess Start(OmpLaunchInfo info)
        {
            var p = Create?.Invoke(info) ?? new FakeOmpProcess(info);
            lock (Processes) Processes.Add(p);
            Configure?.Invoke(p);
            p.Begin();
            return p;
        }
    }

    /// <summary>WinForms처럼 한 스레드에서 콜백을 순서대로 실행하는 SynchronizationContext.</summary>
    internal sealed class TestUi : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public TestUi()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "TestUi" };
            _thread.Start();
        }

        private void Loop()
        {
            SetSynchronizationContext(this);
            foreach (var (cb, state) in _queue.GetConsumingEnumerable())
            {
                try { cb(state); } catch { /* 테스트 핸들러 예외는 단언에서 드러난다 */ }
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            try { _queue.Add((d, state)); }
            catch (InvalidOperationException) { /* 해제된 뒤 도착한 늦은 콜백 */ }
        }
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == _thread) { d(state); return; }
            using var done = new ManualResetEventSlim();
            Exception? error = null;
            _queue.Add((s => { try { d(s); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, state));
            done.Wait();
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        public void Invoke(Action action) => Send(_ => action(), null);
        public T Invoke<T>(Func<T> func)
        {
            T result = default!;
            Send(_ => result = func(), null);
            return result;
        }

        /// <summary>UI 스레드에서 비동기 함수를 시작해 끝날 때까지 기다린다(await는 UI 컨텍스트로 돌아온다).</summary>
        public Task InvokeAsync(Func<Task> func)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await func(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    internal sealed class FakePage : IChatPage
    {
        private readonly List<string> _messages = new();
        public event Action<JsonElement>? Received;

        public void Post(string json)
        {
            lock (_messages) _messages.Add(json);
        }

        public List<string> Snapshot()
        {
            lock (_messages) return _messages.ToList();
        }

        public List<JsonElement> Parsed(string? type = null)
        {
            var list = new List<JsonElement>();
            foreach (string json in Snapshot())
            {
                using var doc = JsonDocument.Parse(json);
                var e = doc.RootElement.Clone();
                if (type == null || e.Str("t") == type) list.Add(e);
            }
            return list;
        }

        /// <summary>status를 뺀 나머지 메시지 원문(골든 비교용).</summary>
        public List<string> WithoutNoise(params string[] drop)
        {
            var skip = new HashSet<string>(drop);
            return Snapshot().Where(j =>
            {
                using var doc = JsonDocument.Parse(j);
                return !skip.Contains(doc.RootElement.Str("t"));
            }).ToList();
        }

        public void Raise(TestUi ui, string json) =>
            ui.Invoke(() =>
            {
                using var doc = JsonDocument.Parse(json);
                Received?.Invoke(doc.RootElement.Clone());
            });

        public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> predicate, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                foreach (var e in Parsed()) if (predicate(e)) return e;
                if (sw.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException("page message not seen; got: " + string.Join(" | ", Snapshot().Select(s => s.Length > 120 ? s[..120] : s)));
                await Task.Delay(5);
            }
        }

        public Task<JsonElement> WaitForTypeAsync(string type, int timeoutMs = 5000) => WaitForAsync(e => e.Str("t") == type, timeoutMs);
    }

    internal sealed class ManualClock : IChatClock
    {
        public long TickMs { get; set; } = 1_000_000;
        public long UnixMs { get; set; } = 1_700_000_000_000;
        public void Advance(int ms) { TickMs += ms; UnixMs += ms; }
    }

    internal sealed class FakeTools : ICsvToolExecutor
    {
        public IReadOnlyList<HostToolDefinition> Definitions { get; } = new[]
        {
            new HostToolDefinition("csv.test", "Test tool", "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}}}"),
        };

        public Func<HostToolCall, IAgentApprovals, CancellationToken, Task<HostToolResult>> OnExecute { get; set; } =
            (call, _, _) => Task.FromResult(HostToolResult.Ok("ok " + call.ToolName));

        public List<HostToolCall> Calls { get; } = new();

        public Task<HostToolResult> ExecuteAsync(HostToolCall call, IAgentApprovals approvals, CancellationToken cancellation)
        {
            lock (Calls) Calls.Add(call);
            return OnExecute(call, approvals, cancellation);
        }
    }

    internal sealed class FakeDialogs : IChatDialogs
    {
        public Func<string, string, bool> OnConfirm { get; set; } = (_, _) => true;
        public Func<string, IReadOnlyList<string>, string?> OnSelect { get; set; } = (_, o) => o.Count > 0 ? o[0] : null;
        public Func<string, string, string, bool, string?> OnInput { get; set; } = (_, _, initial, _) => initial;
        public string? ExportPath { get; set; }
        public IReadOnlyList<string>? PickedFiles { get; set; }
        public string? PickedFolder { get; set; }
        public List<string> PickFilters { get; } = new();
        public List<string> Opened { get; } = new();
        public List<string> Clipboard { get; } = new();
        public List<string> ConfirmTitles { get; } = new();

        public bool Confirm(string title, string message) { ConfirmTitles.Add(title); return OnConfirm(title, message); }
        public string? Select(string title, IReadOnlyList<string> options) => OnSelect(title, options);
        public string? Input(string title, string prompt, string initial, bool multiline) => OnInput(title, prompt, initial, multiline);
        public string? PickExportPath(string suggestedFileName) => ExportPath;
        public IReadOnlyList<string>? PickFiles(string title, string filter) { PickFilters.Add(filter); return PickedFiles; }
        public string? PickFolder(string description) => PickedFolder;
        public void OpenUrl(string url) => Opened.Add(url);
        public void SetClipboard(string text) => Clipboard.Add(text);
    }

    internal sealed class CapturingLog : IRpcLog
    {
        public List<string> Lines { get; } = new();
        public void Header(string text) { lock (Lines) Lines.Add("# " + text); }
        public void Received(string line) { lock (Lines) Lines.Add("< " + line); }
        public void Sent(string line) { lock (Lines) Lines.Add("> " + line); }
        public void Note(string text) { lock (Lines) Lines.Add("! " + text); }
    }

    // ==== 순수 로직: 버전·탐색·명령줄 ================================================================================

    public class OmpLocatorTests
    {
        [Theory]
        [InlineData("omp/18.4.4", 18, 4, 4)]
        [InlineData("18.4.4", 18, 4, 4)]
        [InlineData("v19.0.1-beta.2", 19, 0, 1)]
        [InlineData("omp/18.10.0\r\n", 18, 10, 0)]
        [InlineData("omp/18.4", 18, 4, 0)]
        public void Version_parses_the_part_after_the_last_slash(string text, int major, int minor, int patch)
        {
            Assert.True(OmpVersion.TryParse(text, out var v));
            Assert.Equal(new OmpVersion(major, minor, patch), v);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("omp/dev")]
        [InlineData("command not found")]
        public void Version_rejects_text_that_does_not_start_with_a_digit(string? text)
        {
            Assert.False(OmpVersion.TryParse(text, out _));
        }

        [Fact]
        public void Version_comparison_is_numeric_and_minimum_is_18_4_4()
        {
            Assert.True(new OmpVersion(18, 10, 0) > new OmpVersion(18, 4, 4));
            Assert.True(new OmpVersion(18, 4, 3) < OmpVersion.Minimum);
            Assert.True(new OmpVersion(18, 4, 4) >= OmpVersion.Minimum);
            Assert.True(new OmpVersion(9, 99, 99) < OmpVersion.Minimum);
        }

        [Fact]
        public void Arguments_follow_the_documented_order_and_keep_quoted_extras_whole()
        {
            var args = OmpLaunch.BuildArguments("D:\\data dir", "C:\\t\\host.yml", "C:\\t\\guide.md", "--model anthropic/claude-a --note \"two words\"");
            Assert.Equal(new[]
            {
                "--mode", "rpc-ui", "--cwd", "D:\\data dir", "--config", "C:\\t\\host.yml",
                "--append-system-prompt", "C:\\t\\guide.md", "--model", "anthropic/claude-a", "--note", "two words",
            }, args);

            Assert.Equal(new[] { "--mode", "rpc-ui", "--cwd", "X" }, OmpLaunch.BuildArguments("X", null, null, "  "));
        }

        [Theory]
        [InlineData(AgentApprovalMode.AlwaysAsk, "always-ask")]
        [InlineData(AgentApprovalMode.Write, "write")]
        [InlineData(AgentApprovalMode.Yolo, "yolo")]
        public void Host_config_inlines_csv_devices_and_carries_the_approval_mode(AgentApprovalMode mode, string expected)
        {
            using var doc = JsonDocument.Parse(OmpLaunch.HostConfigJson(mode));
            var tools = doc.RootElement.Child("tools");
            Assert.Equal(new[] { "csv.*" }, tools.Child("xdevInlineDevices").Strings());
            Assert.Equal(expected, tools.Str("approvalMode"));
        }
    }

    // ==== 프레임 읽기: 줄 자르기·청크 복원 ===========================================================================

    public class RpcFramingTests
    {
        private static List<string> Split(int max, params byte[][] feeds)
        {
            var lines = new List<string>();
            var splitter = new RpcLineSplitter(max);
            foreach (var feed in feeds)
                splitter.Feed(feed, line => lines.Add(Encoding.UTF8.GetString(line)), n => lines.Add($"<dropped {n}>"));
            splitter.Complete(line => lines.Add(Encoding.UTF8.GetString(line)), n => lines.Add($"<dropped {n}>"));
            return lines;
        }

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public void Lines_are_cut_at_LF_across_reads_and_CR_and_blank_lines_are_ignored()
        {
            var lines = Split(1024, B("{\"a\":1}\r\n\n{\"b\""), B(":2}\n{\"c\":3}"));
            Assert.Equal(new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" }, lines);
        }

        [Fact]
        public void Multibyte_characters_split_across_reads_survive()
        {
            byte[] all = B("{\"t\":\"한글\"}\n");
            var lines = Split(1024, all[..8], all[8..11], all[11..]);
            Assert.Equal(new[] { "{\"t\":\"한글\"}" }, lines);
        }

        [Fact]
        public void Oversize_line_is_dropped_whole_and_reading_resumes_at_the_next_line()
        {
            string big = new string('x', 50);
            var lines = Split(20, B(big.Substring(0, 30)), B(big.Substring(30) + "\nshort\n"), B(big + "\nlast\n"));
            Assert.Equal(new[] { "<dropped 50>", "short", "<dropped 50>", "last" }, lines);
        }

        [Fact]
        public void Line_exactly_at_the_limit_is_kept()
        {
            var lines = Split(5, B("12345\n123456\n"));
            Assert.Equal(new[] { "12345", "<dropped 6>" }, lines);
        }

        // ---- 청크 -----------------------------------------------------------------------------------------------

        private static JsonElement Chunk(string id, int index, int count, long byteLength, byte[] data)
        {
            var o = new JsonObject
            {
                ["type"] = "rpc_chunk", ["chunkId"] = id, ["index"] = index, ["count"] = count,
                ["byteLength"] = byteLength, ["data"] = Convert.ToBase64String(data),
            };
            using var doc = JsonDocument.Parse(o.ToJsonString());
            return doc.RootElement.Clone();
        }

        private static List<JsonElement> Chunks(string id, byte[] payload, params int[] cuts)
        {
            var list = new List<JsonElement>();
            int start = 0;
            var ends = cuts.Append(payload.Length).ToArray();
            for (int i = 0; i < ends.Length; i++)
            {
                list.Add(Chunk(id, i, ends.Length, payload.Length, payload[start..ends[i]]));
                start = ends[i];
            }
            return list;
        }

        [Fact]
        public void Chunks_reassemble_in_order_even_when_a_character_is_split_between_chunks()
        {
            byte[] payload = B("{\"type\":\"response\",\"id\":\"r\",\"data\":\"가나다\"}");
            int mid = Array.IndexOf(payload, (byte)0xEA) + 1; // '가'의 첫 바이트 뒤에서 자른다
            var chunks = Chunks("rpc-1", payload, mid, mid + 4);
            var asm = new RpcChunkAssembler();

            Assert.Equal(ChunkStatus.NeedMore, asm.Accept(chunks[0]).Status);
            Assert.True(asm.Active);
            Assert.Equal(ChunkStatus.NeedMore, asm.Accept(chunks[1]).Status);
            var done = asm.Accept(chunks[2]);
            Assert.Equal(ChunkStatus.Complete, done.Status);
            Assert.False(asm.Active);
            Assert.Equal("{\"type\":\"response\",\"id\":\"r\",\"data\":\"가나다\"}", RpcChunkAssembler.DecodeStrict(done.Payload!));
        }

        [Fact]
        public void Single_chunk_sequences_are_complete_immediately()
        {
            var asm = new RpcChunkAssembler();
            var result = asm.Accept(Chunk("c", 0, 1, 2, B("{}")));
            Assert.Equal(ChunkStatus.Complete, result.Status);
            Assert.Equal(B("{}"), result.Payload);
        }

        public static IEnumerable<object[]> InvalidSequences()
        {
            byte[] p = B("0123456789");
            // 0: 순서가 틀림(index 1부터 시작)
            yield return new object[] { new Func<List<JsonElement>>(() => new() { Chunk("a", 1, 2, 10, p[..5]) }), "start" };
            // 1: 건너뜀(0 다음 2)
            yield return new object[] { new Func<List<JsonElement>>(() => new() { Chunk("a", 0, 3, 10, p[..3]), Chunk("a", 2, 3, 10, p[3..]) }), "out of order" };
            // 2: 끼어듦(다른 chunkId)
            yield return new object[] { new Func<List<JsonElement>>(() => new() { Chunk("a", 0, 2, 10, p[..5]), Chunk("b", 1, 2, 10, p[5..]) }), "interleaved" };
            // 3: count 변경
            yield return new object[] { new Func<List<JsonElement>>(() => new() { Chunk("a", 0, 2, 10, p[..5]), Chunk("a", 1, 3, 10, p[5..]) }), "changed" };
            // 4: byteLength가 실제보다 큼
            yield return new object[] { new Func<List<JsonElement>>(() => new() { Chunk("a", 0, 2, 12, p[..5]), Chunk("a", 1, 2, 12, p[5..]) }), "mismatch" };
            // 5: byteLength를 넘는 데이터
            yield return new object[] { new Func<List<JsonElement>>(() => new() { Chunk("a", 0, 2, 6, p[..5]), Chunk("a", 1, 2, 6, p[5..]) }), "more data" };
            // 6: 같은 청크 반복
            yield return new object[] { new Func<List<JsonElement>>(() => new() { Chunk("a", 0, 2, 10, p[..5]), Chunk("a", 0, 2, 10, p[..5]) }), "out of order" };
        }

        [Theory]
        [MemberData(nameof(InvalidSequences))]
        public void Invalid_sequences_are_rejected_with_an_error_and_leave_the_assembler_reusable(Func<List<JsonElement>> build, string expected)
        {
            var asm = new RpcChunkAssembler();
            ChunkResult last = default;
            foreach (var chunk in build())
            {
                last = asm.Accept(chunk);
                if (last.Status == ChunkStatus.Error) break;
            }
            Assert.Equal(ChunkStatus.Error, last.Status);
            Assert.Contains(expected, last.Error);
            Assert.False(asm.Active);
            // 오류 뒤에는 새 시퀀스를 받을 수 있다.
            Assert.Equal(ChunkStatus.Complete, asm.Accept(Chunk("ok", 0, 1, 2, B("{}"))).Status);
        }

        [Fact]
        public void Reassembly_limit_and_bad_base64_and_missing_fields_are_errors()
        {
            var small = new RpcChunkAssembler(maxReassembledBytes: 10);
            Assert.Equal(ChunkStatus.Error, small.Accept(Chunk("a", 0, 2, 11, B("abcde"))).Status);

            var asm = new RpcChunkAssembler();
            var bad = JsonDocument.Parse("{\"type\":\"rpc_chunk\",\"chunkId\":\"a\",\"index\":0,\"count\":1,\"byteLength\":3,\"data\":\"***\"}").RootElement;
            Assert.Contains("base64", asm.Accept(bad).Error);

            var noId = JsonDocument.Parse("{\"type\":\"rpc_chunk\",\"index\":0,\"count\":1,\"byteLength\":0,\"data\":\"\"}").RootElement;
            Assert.Equal(ChunkStatus.Error, asm.Accept(noId).Status);

            var negative = JsonDocument.Parse("{\"type\":\"rpc_chunk\",\"chunkId\":\"a\",\"index\":0,\"count\":0,\"byteLength\":0,\"data\":\"\"}").RootElement;
            Assert.Equal(ChunkStatus.Error, asm.Accept(negative).Status);
        }

        [Fact]
        public void A_foreign_frame_in_the_middle_of_a_sequence_interrupts_it()
        {
            var asm = new RpcChunkAssembler();
            Assert.Null(asm.NoteOtherFrame());
            Assert.Equal(ChunkStatus.NeedMore, asm.Accept(Chunk("a", 0, 2, 4, B("ab"))).Status);
            Assert.NotNull(asm.NoteOtherFrame());
            Assert.False(asm.Active);
            Assert.Equal(ChunkStatus.Error, asm.Accept(Chunk("a", 1, 2, 4, B("cd"))).Status);
        }

        [Fact]
        public void Strict_decoding_rejects_invalid_utf8()
        {
            Assert.Null(RpcChunkAssembler.DecodeStrict(new byte[] { 0x7B, 0xFF, 0x7D }));
            Assert.Equal("{}", RpcChunkAssembler.DecodeStrict(B("{}")));
        }

        [Fact]
        public void Protocol_choice_prefers_v2_and_tolerates_old_ready_frames()
        {
            static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
            Assert.Equal(2, RpcProtocol.ChooseProtocol(J("{\"supportedProtocolVersions\":[1,2]}")));
            Assert.Equal(1, RpcProtocol.ChooseProtocol(J("{\"supportedProtocolVersions\":[1]}")));
            Assert.Equal(1, RpcProtocol.ChooseProtocol(J("{\"type\":\"ready\"}")));
            Assert.Equal(0, RpcProtocol.ChooseProtocol(J("{\"supportedProtocolVersions\":[3]}")));
        }

        [Fact]
        public void Host_tool_result_frame_carries_text_error_flag_and_optional_image()
        {
            using var ok = JsonDocument.Parse(RpcProtocol.HostToolResult("host_1", HostToolResult.Ok("hi")));
            var r = ok.RootElement;
            Assert.Equal("host_tool_result", r.Str("type"));
            Assert.Equal("host_1", r.Str("id"));
            Assert.Null(r.Bool("isError"));
            Assert.Equal("hi", r.Child("result").Child("content").ContentText());

            using var err = JsonDocument.Parse(RpcProtocol.HostToolResult("host_2", new HostToolResult("bad", true, "AAAA")));
            Assert.True(err.RootElement.Bool("isError"));
            var content = err.RootElement.Child("result").Child("content").Items().ToList();
            Assert.Equal(2, content.Count);
            Assert.Equal("image", content[1].Str("type"));
            Assert.Equal("image/png", content[1].Str("mimeType"));
            Assert.Equal("AAAA", content[1].Str("data"));
        }

        [Fact]
        public void Set_host_tools_embeds_the_parameter_schema_as_an_object_and_korean_stays_unescaped()
        {
            string json = RpcProtocol.SetHostTools("req-3", new[] { new HostToolDefinition("csv.x", "한글 설명", "{\"type\":\"object\",\"required\":[\"a\"]}") });
            Assert.Contains("한글 설명", json);
            using var doc = JsonDocument.Parse(json);
            var tool = doc.RootElement.Child("tools").Items().Single();
            Assert.Equal("csv.x", tool.Str("name"));
            Assert.Equal("object", tool.Child("parameters").Str("type"));
        }
    }

    // ==== 클라이언트: 핸드셰이크·상관관계·종료 =======================================================================

    public class OmpRpcClientTests : IDisposable
    {
        private readonly TestUi _ui = new();
        private readonly FakeOmpFactory _factory = new();
        private readonly List<OmpRpcClient> _clients = new();

        public void Dispose()
        {
            foreach (var c in _clients) c.Dispose();
            _ui.Dispose();
        }

        private OmpRpcClient NewClient(CapturingLog? log = null, TimeSpan? readyTimeout = null)
        {
            var c = new OmpRpcClient(_factory, _ui, log, readyTimeout);
            _clients.Add(c);
            return c;
        }

        private static OmpLaunchInfo Launch() => new("C:\\omp.exe", new[] { "--mode", "rpc-ui" }, "C:\\", Path.Combine(Path.GetTempPath(), "omp-test.stderr.log"));

        [Fact]
        public async Task Nothing_is_written_before_ready_and_negotiate_is_first_afterwards()
        {
            var client = NewClient();
            _factory.Configure = p => p.AutoReady = false;
            var start = _ui.Invoke(() => client.StartAsync(Launch()));

            await Task.Delay(150);
            var proc = _factory.Last;
            Assert.Empty(proc.ReceivedSnapshot());
            Assert.False(client.IsConnected);
            Assert.False(_ui.Invoke(() => client.Send("{\"type\":\"get_state\"}")));

            proc.Emit("{\"type\":\"ready\",\"protocolVersion\":1,\"supportedProtocolVersions\":[1,2],\"maxFrameBytes\":1048576,\"maxReassembledFrameBytes\":67108864}");
            var first = await proc.WaitForAsync(_ => true);
            Assert.Equal("negotiate_protocol", first.Str("type"));
            Assert.Equal(2, first.Int("protocolVersion"));
            Assert.StartsWith("req-", first.Str("id"));

            var ready = await start;
            Assert.Equal(2, ready.ChosenProtocol);
            Assert.Equal(2, client.ProtocolVersion);
        }

        [Fact]
        public async Task V1_only_omp_is_not_asked_to_negotiate()
        {
            var client = NewClient();
            _factory.Create = info => new FakeOmpProcess(info, "{\"type\":\"ready\",\"protocolVersion\":1}");
            var ready = await _ui.Invoke(() => client.StartAsync(Launch()));
            Assert.Equal(1, ready.ChosenProtocol);
            Assert.Empty(_factory.Last.ReceivedSnapshot());
            Assert.True(client.IsConnected);
        }

        [Fact]
        public async Task Rejected_negotiation_falls_back_to_v1()
        {
            var client = NewClient();
            _factory.Configure = p => p.Handlers["negotiate_protocol"] = cmd => FakeOmpProcess.Fail(cmd.Str("id"), "negotiate_protocol", "no");
            var ready = await _ui.Invoke(() => client.StartAsync(Launch()));
            Assert.Equal(1, ready.ChosenProtocol);
        }

        [Fact]
        public async Task Start_times_out_when_omp_never_sends_ready()
        {
            var client = NewClient(readyTimeout: TimeSpan.FromMilliseconds(150));
            _factory.Configure = p => p.AutoReady = false;
            await Assert.ThrowsAsync<TimeoutException>(() => _ui.Invoke(() => client.StartAsync(Launch())));
            Assert.True(_factory.Last.Killed);
        }

        private async Task<(OmpRpcClient Client, FakeOmpProcess Proc)> StartedAsync()
        {
            var client = NewClient();
            await _ui.Invoke(() => client.StartAsync(Launch()));
            return (client, _factory.Last);
        }

        [Fact]
        public async Task Responses_resolve_the_request_with_the_same_id_regardless_of_arrival_order()
        {
            var (client, proc) = await StartedAsync();
            proc.Handlers["get_state"] = _ => "";
            proc.Handlers["get_available_models"] = _ => "";

            Task<JsonElement> a = default!, b = default!;
            _ui.Invoke(() =>
            {
                client.TryRequest("get_state", null, out a);
                client.TryRequest("get_available_models", null, out b);
            });
            var stateCmd = await proc.WaitForTypeAsync("get_state");
            var modelsCmd = await proc.WaitForTypeAsync("get_available_models");
            Assert.NotEqual(stateCmd.Str("id"), modelsCmd.Str("id"));

            // 두 번째 요청의 응답이 먼저 온다.
            proc.Emit(FakeOmpProcess.Ok(modelsCmd.Str("id"), "get_available_models", new JsonObject { ["which"] = "models" }));
            var second = await b.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("models", second.Child("data").Str("which"));
            Assert.False(a.IsCompleted);

            proc.Emit(FakeOmpProcess.Fail(stateCmd.Str("id"), "get_state", "boom"));
            var first = await a.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.Bool("success"));
            Assert.Equal("boom", first.Str("error"));
        }

        [Fact]
        public async Task Uncorrelated_frames_and_late_error_responses_go_to_the_Frame_event_in_order()
        {
            var (client, proc) = await StartedAsync();
            var seen = new List<string>();
            client.Frame += f => seen.Add(f.Str("type") + ":" + f.Str("id") + f.Str("command"));

            proc.Emit("{\"type\":\"agent_start\"}");
            proc.Emit("{\"type\":\"response\",\"id\":\"req-99\",\"command\":\"prompt\",\"success\":false,\"error\":\"late\"}");
            proc.Emit("{\"type\":\"agent_end\"}");
            await WaitUntilAsync(() => seen.Count == 3);
            Assert.Equal(new[] { "agent_start:", "response:req-99prompt", "agent_end:" }, seen);
        }

        [Fact]
        public async Task V2_chunked_response_is_reassembled_and_correlated()
        {
            var (client, proc) = await StartedAsync();
            proc.Handlers["get_state"] = _ => "";
            Task<JsonElement> task = default!;
            _ui.Invoke(() => client.TryRequest("get_state", null, out task));
            var cmd = await proc.WaitForTypeAsync("get_state");

            string big = new string('가', 4000);
            byte[] payload = Encoding.UTF8.GetBytes(FakeOmpProcess.Ok(cmd.Str("id"), "get_state", new JsonObject { ["text"] = big }));
            int third = payload.Length / 3;
            foreach (var (from, to, i) in new[] { (0, third + 1, 0), (third + 1, 2 * third + 2, 1), (2 * third + 2, payload.Length, 2) })
                proc.Emit(new JsonObject
                {
                    ["type"] = "rpc_chunk", ["chunkId"] = "rpc-1", ["index"] = i, ["count"] = 3,
                    ["byteLength"] = payload.Length, ["data"] = Convert.ToBase64String(payload[from..to]),
                });

            var reply = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(big, reply.Child("data").Str("text"));
        }

        [Fact]
        public async Task Broken_chunk_sequences_and_oversize_lines_become_diagnostics_and_do_not_stop_the_stream()
        {
            var (client, proc) = await StartedAsync();
            var diagnostics = new List<string>();
            var frames = new List<string>();
            client.Diagnostic += diagnostics.Add;
            client.Frame += f => frames.Add(f.Str("type"));

            proc.Emit("{\"type\":\"rpc_chunk\",\"chunkId\":\"x\",\"index\":1,\"count\":2,\"byteLength\":4,\"data\":\"YWI=\"}");
            proc.Out.Push(Encoding.UTF8.GetBytes(new string('z', RpcProtocol.MaxFrameBytes + 10) + "\n"));
            proc.Emit("not json");
            proc.Emit("{\"type\":\"agent_start\"}");

            await WaitUntilAsync(() => frames.Contains("agent_start"));
            Assert.Equal(3, diagnostics.Count);
            Assert.Contains("start at index 0", diagnostics[0]);
            Assert.Contains("oversize", diagnostics[1]);
            Assert.Contains("malformed", diagnostics[2]);
        }

        [Fact]
        public async Task Child_exit_raises_Exited_with_stderr_and_fails_pending_requests()
        {
            var (client, proc) = await StartedAsync();
            proc.Handlers["get_state"] = _ => "";
            Task<JsonElement> pending = default!;
            _ui.Invoke(() => client.TryRequest("get_state", null, out pending));
            await proc.WaitForTypeAsync("get_state");

            OmpExitInfo? exit = null;
            client.Exited += e => exit = e;
            proc.Crash(3, "Pipe closed: provider unreachable");

            await Assert.ThrowsAsync<OmpExitedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitUntilAsync(() => exit != null);
            Assert.Equal(3, exit!.ExitCode);
            Assert.Equal("Pipe closed: provider unreachable", exit.LastErrorLine);
            Assert.True(exit.WasReady);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public async Task Stop_closes_stdin_cancels_pending_and_drops_late_frames()
        {
            var (client, proc) = await StartedAsync();
            proc.Handlers["get_state"] = _ => "";
            Task<JsonElement> pending = default!;
            _ui.Invoke(() => client.TryRequest("get_state", null, out pending));
            await proc.WaitForTypeAsync("get_state");

            var seen = new List<string>();
            var exited = false;
            client.Frame += f => seen.Add(f.Str("type"));
            client.Exited += _ => exited = true;

            // UI 스레드를 붙잡아 둔 채 Stop하고, 그 사이에 도착한 프레임이 새 세대에 닿지 않는지 본다.
            using var gate = new ManualResetEventSlim();
            _ui.Post(_ => { gate.Wait(); client.Stop(); }, null);
            proc.Emit("{\"type\":\"agent_start\"}");
            await Task.Delay(100);
            gate.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            await proc.Exited.WaitAsync(TimeSpan.FromSeconds(5)); // stdin이 닫혀 가짜 omp가 정상 종료
            await Task.Delay(100);
            Assert.Empty(seen);
            Assert.False(exited);
            Assert.False(_ui.Invoke(() => client.Send("{\"type\":\"get_state\"}")));
        }

        [Fact]
        public async Task Frames_are_logged_with_direction()
        {
            var log = new CapturingLog();
            var client = NewClient(log);
            await _ui.Invoke(() => client.StartAsync(Launch()));
            var proc = _factory.Last;
            _ui.Invoke(() => client.SendCommand("get_state"));
            await proc.WaitForTypeAsync("get_state");
            await WaitUntilAsync(() => { lock (log.Lines) return log.Lines.Any(l => l.StartsWith("> ") && l.Contains("get_state")) && log.Lines.Any(l => l.StartsWith("< ") && l.Contains("\"ready\"")); });
        }

        [Fact]
        public async Task Oversize_outbound_frames_are_refused()
        {
            var client = NewClient();
            await _ui.Invoke(() => client.StartAsync(Launch()));
            string big = "{\"type\":\"prompt\",\"message\":\"" + new string('a', RpcProtocol.MaxFrameBytes) + "\"}";
            Assert.False(_ui.Invoke(() => client.Send(big)));
            Assert.True(_ui.Invoke(() => client.Send("{\"type\":\"get_state\"}")));
        }

        internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
                await Task.Delay(5);
            }
        }
    }

    // ==== host tool 디스패처 =========================================================================================

    public class HostToolDispatcherTests
    {
        private static JsonElement Call(string id, string tool, string args = "{}")
        {
            using var doc = JsonDocument.Parse($"{{\"type\":\"host_tool_call\",\"id\":\"{id}\",\"toolCallId\":\"tc-{id}\",\"toolName\":\"{tool}\",\"arguments\":{args}}}");
            return doc.RootElement.Clone();
        }

        private static JsonElement Cancel(string target)
        {
            using var doc = JsonDocument.Parse($"{{\"type\":\"host_tool_cancel\",\"id\":\"c1\",\"targetId\":\"{target}\"}}");
            return doc.RootElement.Clone();
        }

        private sealed class NoApprovals : IAgentApprovals
        {
            public Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation, ApprovalKind kind = ApprovalKind.RowSharing) => Task.FromResult(true);
        }

        private static (HostToolDispatcher D, FakeTools T, List<string> Sent) Make(int maxFrame = RpcProtocol.MaxFrameBytes)
        {
            var tools = new FakeTools();
            var sent = new List<string>();
            var d = new HostToolDispatcher(tools, new NoApprovals(), s => { lock (sent) sent.Add(s); return true; }, NullRpcLog.Instance, korean: false, maxFrameBytes: maxFrame);
            return (d, tools, sent);
        }

        [Fact]
        public async Task A_call_runs_the_tool_with_its_arguments_and_answers_with_the_same_id()
        {
            var (d, tools, sent) = Make();
            d.HandleCall(Call("host_7", "csv.info", "{\"sheet\":2}"));
            await OmpRpcClientTests.WaitUntilAsync(() => { lock (sent) return sent.Count == 1; });

            Assert.Equal("csv.info", tools.Calls.Single().ToolName);
            Assert.Equal("tc-host_7", tools.Calls.Single().ToolCallId);
            Assert.Equal(2, tools.Calls.Single().Arguments.Int("sheet"));
            using var reply = JsonDocument.Parse(sent[0]);
            Assert.Equal("host_7", reply.RootElement.Str("id"));
            Assert.Equal("ok csv.info", reply.RootElement.Child("result").Child("content").ContentText());
            Assert.Equal(0, d.RunningCount);
        }

        [Fact]
        public async Task A_cancel_trips_the_token_and_no_result_is_sent()
        {
            var (d, tools, sent) = Make();
            var started = new TaskCompletionSource();
            CancellationToken token = default;
            tools.OnExecute = async (_, _, ct) =>
            {
                token = ct;
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return HostToolResult.Ok("never");
            };
            var finished = new TaskCompletionSource<HostToolResult?>();
            d.Finished += (_, r) => finished.TrySetResult(r);

            d.HandleCall(Call("host_1", "csv.run_analysis"));
            await started.Task;
            d.HandleCancel(Cancel("host_1"));

            Assert.Null(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(token.IsCancellationRequested);
            Assert.Empty(sent);
        }

        [Fact]
        public async Task A_tool_that_finishes_after_being_cancelled_still_sends_nothing()
        {
            var (d, tools, sent) = Make();
            var release = new TaskCompletionSource();
            var started = new TaskCompletionSource();
            tools.OnExecute = async (_, _, _) => { started.SetResult(); await release.Task; return HostToolResult.Ok("late"); };
            var finished = new TaskCompletionSource<HostToolResult?>();
            d.Finished += (_, r) => finished.TrySetResult(r);

            d.HandleCall(Call("host_2", "csv.x"));
            await started.Task;
            d.HandleCancel(Cancel("host_2"));
            release.SetResult();

            Assert.Null(await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(sent);
        }

        [Fact]
        public async Task A_cancel_that_arrives_before_its_call_skips_the_call()
        {
            var (d, tools, sent) = Make();
            d.HandleCancel(Cancel("host_3"));
            d.HandleCall(Call("host_3", "csv.x"));
            await Task.Delay(100);
            Assert.Empty(tools.Calls);
            Assert.Empty(sent);
            // 다른 호출은 영향받지 않는다.
            d.HandleCall(Call("host_4", "csv.x"));
            await OmpRpcClientTests.WaitUntilAsync(() => { lock (sent) return sent.Count == 1; });
        }

        [Fact]
        public async Task A_throwing_tool_becomes_an_error_result()
        {
            var (d, tools, sent) = Make();
            tools.OnExecute = (_, _, _) => throw new InvalidOperationException("kaput");
            d.HandleCall(Call("host_5", "csv.x"));
            await OmpRpcClientTests.WaitUntilAsync(() => { lock (sent) return sent.Count == 1; });
            using var reply = JsonDocument.Parse(sent[0]);
            Assert.True(reply.RootElement.Bool("isError"));
            Assert.Contains("kaput", reply.RootElement.Child("result").Child("content").ContentText());
        }

        [Fact]
        public async Task An_oversize_result_is_replaced_by_an_error_text_that_fits()
        {
            var (d, tools, sent) = Make(maxFrame: 2000);
            tools.OnExecute = (_, _, _) => Task.FromResult(HostToolResult.Ok(new string('x', 5000)));
            d.HandleCall(Call("host_6", "csv.get_rows"));
            await OmpRpcClientTests.WaitUntilAsync(() => { lock (sent) return sent.Count == 1; });

            Assert.True(RpcProtocol.Utf8Length(sent[0]) <= 2000);
            using var reply = JsonDocument.Parse(sent[0]);
            Assert.True(reply.RootElement.Bool("isError"));
            Assert.Contains("too large", reply.RootElement.Child("result").Child("content").ContentText());
        }

        [Fact]
        public async Task An_oversize_image_is_dropped_but_the_text_result_survives()
        {
            var (d, tools, sent) = Make(maxFrame: 2000);
            tools.OnExecute = (_, _, _) => Task.FromResult(new HostToolResult("chart", false, new string('A', 5000)));
            d.HandleCall(Call("host_8", "csv.run_analysis"));
            await OmpRpcClientTests.WaitUntilAsync(() => { lock (sent) return sent.Count == 1; });

            using var reply = JsonDocument.Parse(sent[0]);
            Assert.Null(reply.RootElement.Bool("isError"));
            var parts = reply.RootElement.Child("result").Child("content").Items().ToList();
            Assert.Single(parts);
            Assert.StartsWith("chart", parts[0].Str("text"));
        }
    }
}
