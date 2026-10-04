using System.Text.Json;

namespace NanumCsvViewer.Agent.Rpc
{
    /// <summary>
    /// omp host_tool_call → ICsvToolExecutor → host_tool_result. UI 스레드에서 Handle*를 부르면 실행기도 UI 스레드에서 시작된다.
    /// host_tool_cancel이면 CancellationToken을 취소하고 결과를 보내지 않는다(omp가 이미 호출을 거절함).
    /// 결과 프레임이 1 MiB를 넘으면 이미지를 떼고, 그래도 크면 오류 문구로 바꿔 보낸다.
    /// </summary>
    internal sealed class HostToolDispatcher
    {
        private readonly ICsvToolExecutor _tools;
        private readonly IAgentApprovals _approvals;
        private readonly Func<string, bool> _send;
        private readonly IRpcLog _log;
        private readonly bool _korean;
        private readonly int _maxFrameBytes;
        private readonly Dictionary<string, CancellationTokenSource> _running = new();
        private readonly HashSet<string> _cancelledEarly = new();

        public HostToolDispatcher(ICsvToolExecutor tools, IAgentApprovals approvals, Func<string, bool> send,
            IRpcLog log, bool korean, int maxFrameBytes = RpcProtocol.MaxFrameBytes)
        {
            _tools = tools;
            _approvals = approvals;
            _send = send;
            _log = log;
            _korean = korean;
            _maxFrameBytes = maxFrameBytes;
        }

        public int RunningCount => _running.Count;

        /// <summary>호출이 끝나거나 취소되어 실행 목록에서 빠질 때(UI 스레드). 도구가 화면을 바꿨다면 상태 갱신용.</summary>
        public event Action<HostToolCall, HostToolResult?>? Finished;

        public void HandleCall(JsonElement frame)
        {
            string id = frame.Str("id");
            if (id.Length == 0) return;
            if (_cancelledEarly.Remove(id)) return;

            JsonElement args = frame.Child("arguments");
            if (args.ValueKind != JsonValueKind.Object)
            {
                using var empty = JsonDocument.Parse("{}");
                args = empty.RootElement.Clone();
            }
            var call = new HostToolCall(id, frame.Str("toolCallId"), frame.Str("toolName"), args);
            var cts = new CancellationTokenSource();
            _running[id] = cts;
            _ = RunAsync(call, cts);
        }

        public void HandleCancel(JsonElement frame)
        {
            string target = frame.Str("targetId");
            if (target.Length == 0) return;
            if (_running.TryGetValue(target, out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            else
            {
                // 취소가 호출보다 먼저 온 경우(드묾): 나중에 오는 호출을 건너뛴다. 무한히 쌓이지 않게 상한을 둔다.
                if (_cancelledEarly.Count > 256) _cancelledEarly.Clear();
                _cancelledEarly.Add(target);
            }
        }

        /// <summary>자식이 사라지거나 사용자가 중지: 실행 중인 모든 호출을 취소하고 결과를 보내지 않는다.</summary>
        public void CancelAll()
        {
            foreach (var cts in _running.Values.ToArray())
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            _cancelledEarly.Clear();
        }

        private async Task RunAsync(HostToolCall call, CancellationTokenSource cts)
        {
            HostToolResult? result;
            try
            {
                result = await _tools.ExecuteAsync(call, _approvals, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                result = null;
            }
            catch (Exception ex)
            {
                _log.Note($"host tool {call.ToolName} failed: {ex}");
                result = HostToolResult.Error((_korean ? "도구 실행 오류: " : "Tool error: ") + ex.Message);
            }

            bool cancelled = cts.IsCancellationRequested;
            _running.Remove(call.Id);
            cts.Dispose();
            if (cancelled || result == null)
            {
                Finished?.Invoke(call, null);
                return;
            }
            SendResult(call.Id, result);
            Finished?.Invoke(call, result);
        }

        private void SendResult(string id, HostToolResult result)
        {
            string json = RpcProtocol.HostToolResult(id, result);
            if (RpcProtocol.Utf8Length(json) > _maxFrameBytes && !string.IsNullOrEmpty(result.ImagePngBase64))
            {
                result = result with
                {
                    ImagePngBase64 = null,
                    Text = result.Text + (_korean ? "\n(이미지는 크기 때문에 생략되었습니다.)" : "\n(The image was omitted because it is too large.)"),
                };
                json = RpcProtocol.HostToolResult(id, result);
            }
            int bytes = RpcProtocol.Utf8Length(json);
            if (bytes > _maxFrameBytes)
            {
                string text = _korean
                    ? $"도구 결과가 너무 큽니다({bytes:N0}바이트, 상한 {_maxFrameBytes / 1024 / 1024} MiB). 행·열 수를 줄이거나 집계 도구를 사용해 다시 요청하세요."
                    : $"The tool result is too large ({bytes:N0} bytes; limit {_maxFrameBytes / 1024 / 1024} MiB). Request fewer rows/columns or use an aggregate tool.";
                json = RpcProtocol.HostToolResult(id, HostToolResult.Error(text));
            }
            if (!_send(json)) _log.Note($"host_tool_result {id} not sent");
        }
    }
}
