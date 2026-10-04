using System.Text.Json;

namespace NanumCsvViewer.Agent
{
    // v2 에이전트 통합의 공용 계약. omp(oh-my-pi)를 RPC 모드 자식 프로세스로 띄우고,
    // 앱 기능을 host tool(csv.*)로 등록해 실행 중인 창을 조작한다. 설계: docs/AGENT_INTEGRATION_PLAN.md

    /// <summary>omp `set_host_tools`에 보내는 도구 정의. ParametersSchema는 JSON Schema(object) 원문.</summary>
    public sealed record HostToolDefinition(string Name, string Description, string ParametersSchema);

    /// <summary>omp `host_tool_call` 프레임. Id는 결과를 돌려줄 때 쓰는 호출 id(host_N).</summary>
    public sealed record HostToolCall(string Id, string ToolCallId, string ToolName, JsonElement Arguments);

    /// <summary>omp `host_tool_result`의 내용. ImagePngBase64가 있으면 image 콘텐츠로 함께 보낸다.</summary>
    public sealed record HostToolResult(string Text, bool IsError = false, string? ImagePngBase64 = null)
    {
        public static HostToolResult Ok(string text) => new(text);
        public static HostToolResult Error(string text) => new(text, true);
    }

    /// <summary>앱이 사용자에게 묻는 승인(채팅 창의 승인 카드로 표시). 거부·중지·창 닫힘이면 false.</summary>
    public interface IAgentApprovals
    {
        /// <param name="target">무엇을 바꾸는지(예: "셀 편집 12개", "파일 저장: C:\a.csv").</param>
        /// <param name="summary">한 줄 요약.</param>
        /// <param name="lines">카드 본문 행. 접두 "+ "/"- "/"  "는 추가/삭제/문맥으로 색칠된다.</param>
        Task<bool> ApproveAsync(string target, string summary, IReadOnlyList<string> lines, CancellationToken cancellation);
    }

    /// <summary>csv.* host tool 실행기. 모든 호출은 UI 스레드에서 들어온다(구현이 필요하면 내부에서 백그라운드로 넘긴다).</summary>
    public interface ICsvToolExecutor
    {
        IReadOnlyList<HostToolDefinition> Definitions { get; }
        Task<HostToolResult> ExecuteAsync(HostToolCall call, IAgentApprovals approvals, CancellationToken cancellation);
    }

    /// <summary>모델로 보낼 수 있는 데이터 범위. 기본은 요약만(원시 행 없음).</summary>
    public enum AgentDataPolicy
    {
        /// <summary>스키마·타입·집계 통계·분석 결과만. 원시 행 값은 보내지 않는다.</summary>
        SummaryOnly,
        /// <summary>요청마다 승인 후 행 값 전송(상한 MaxRowsPerRequest).</summary>
        RowsWithApproval,
        /// <summary>승인 없이 상한까지 행 값 전송.</summary>
        RowsAllowed,
    }

    /// <summary>
    /// 채팅 페이지(WebView2) 경계. 메시지 형식은 RAD Agent 채팅 페이지와 같다: 객체마다 문자열 필드 "t"가 종류
    /// (host→page: strings, theme, user, assistantDelta, assistantEnd, toolStart, toolUpdate, toolEnd, status, catalog,
    /// commands, submitted, notice, approval, approvalResult, thinkingDelta, thinkingEnd, todos, queue, turnEnd, clear, history …;
    /// page→host: ready, submit, abort, newSession, setModel, setThinking, approval, copy, openUrl, runCommand, cancelQueued …).
    /// 페이지가 "ready"를 보내기 전에 Post된 메시지는 패널이 쌓아 두었다가 ready 뒤에 순서대로 보낸다.
    /// </summary>
    public interface IChatPage
    {
        /// <summary>host→page JSON 객체 1개(직렬화된 문자열). UI 스레드에서 호출.</summary>
        void Post(string json);
        /// <summary>page→host 메시지. UI 스레드에서 발생.</summary>
        event Action<JsonElement> Received;
    }
}
