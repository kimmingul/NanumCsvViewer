using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Agent
{
    /// <summary>작업 공간 설정이 앱 설정을 실제로 조여진 항목(채팅 표시·알림용).</summary>
    public readonly record struct WorkspaceLimits(bool Approval, bool DataPolicy, bool LocalPython)
    {
        public bool Any => Approval || DataPolicy || LocalPython;
    }

    /// <summary>
    /// 작업 공간 파일(.ncvws)의 에이전트 설정(승인 모드·데이터 정책·로컬 Python)과 앱 설정을 합치는 규칙.
    /// <b>보안 규칙: 실제 값은 둘 중 더 엄격한 쪽이다.</b> 받은 작업 공간 파일은 앱 설정을 조일 수만 있고 풀 수 없다.
    /// 엄격한 순서 — 승인 모드: always-ask &gt; write &gt; yolo · 데이터 정책: SummaryOnly &gt; RowsWithApproval &gt; RowsAllowed · 로컬 Python: 끔 &gt; 켬.
    /// omp 추가 인자(--yolo 등)가 승인 모드를 고정하면 그 고정이 여전히 이긴다(<see cref="AgentHostOptions.EffectiveApprovalMode"/>).
    /// </summary>
    public static class WorkspaceAgentPolicy
    {
        /// <summary>클수록 엄격.</summary>
        public static int Strictness(AgentApprovalMode mode) => mode switch
        {
            AgentApprovalMode.AlwaysAsk => 2,
            AgentApprovalMode.Write => 1,
            _ => 0,
        };

        public static int Strictness(AgentDataPolicy policy) => policy switch
        {
            AgentDataPolicy.SummaryOnly => 2,
            AgentDataPolicy.RowsWithApproval => 1,
            _ => 0,
        };

        public static AgentApprovalMode Stricter(AgentApprovalMode a, AgentApprovalMode b) => Strictness(b) > Strictness(a) ? b : a;

        public static AgentDataPolicy Stricter(AgentDataPolicy a, AgentDataPolicy b) => Strictness(b) > Strictness(a) ? b : a;

        /// <summary>로컬 Python: 둘 다 켜져 있을 때만 켠다.</summary>
        public static bool Stricter(bool appAllows, bool workspaceAllows) => appAllows && workspaceAllows;

        public static AgentApprovalMode? ParseApproval(WorkspaceFileAgent? ws) =>
            ws is not null && AgentApprovalPolicy.TryParse(ws.ApprovalMode, out var m) ? m : null;

        public static AgentDataPolicy? ParseDataPolicy(WorkspaceFileAgent? ws) =>
            ws is not null && Enum.TryParse<AgentDataPolicy>(ws.DataPolicy, ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : null;

        /// <summary>
        /// 앱 설정으로 만든 옵션에 작업 공간 설정을 합친다. 작업 공간 값이 없으면(또는 앱 설정보다 느슨하면) 앱 값 그대로다.
        /// 돌려주는 옵션의 <see cref="AgentHostOptions.Limits"/>는 작업 공간이 실제로 조여진 항목이다.
        /// </summary>
        public static AgentHostOptions Apply(AgentHostOptions app, WorkspaceFileAgent? ws)
        {
            if (ws is null) return app with { Limits = default };
            var mode = app.ApprovalMode;
            var policy = app.DataPolicy;
            bool python = app.AllowLocalPython;

            if (ParseApproval(ws) is { } wm) mode = Stricter(app.ApprovalMode, wm);
            if (ParseDataPolicy(ws) is { } wp) policy = Stricter(app.DataPolicy, wp);
            if (ws.AllowLocalPython is { } wpy) python = Stricter(app.AllowLocalPython, wpy);

            var limits = new WorkspaceLimits(
                Approval: mode != app.ApprovalMode && app.ForcedApproval is null,   // 추가 인자가 고정했으면 작업 공간 제한은 의미가 없다(고정이 이긴다)
                DataPolicy: policy != app.DataPolicy,
                LocalPython: python != app.AllowLocalPython);
            return app with { ApprovalMode = mode, DataPolicy = policy, AllowLocalPython = python, Limits = limits };
        }

        /// <summary>채팅 표시줄 짧은 글.</summary>
        public static string LimitLabel(bool korean) => korean ? "🔒 작업 공간" : "🔒 Workspace";

        /// <summary>제한이 걸려 있으면 채팅 말풍선(툴팁) 글, 없으면 빈 문자열.</summary>
        public static string LimitTooltip(AgentHostOptions options, bool korean)
        {
            var l = options.Limits;
            if (!l.Any) return "";
            var parts = new List<string>();
            if (l.Approval) parts.Add(korean ? $"승인 모드 {ApprovalTexts.Label(options.ApprovalMode, true)}" : $"approval mode: {ApprovalTexts.Label(options.ApprovalMode, false)}");
            if (l.DataPolicy) parts.Add(korean ? $"데이터 공유 {PolicyLabel(options.DataPolicy, true)}" : $"data sharing: {PolicyLabel(options.DataPolicy, false)}");
            if (l.LocalPython) parts.Add(korean ? "로컬 Python 분석 꺼짐" : "local Python analysis off");
            return korean
                ? "작업 공간 설정으로 제한됨: " + string.Join(", ", parts) + ". 작업 공간 파일은 앱 설정을 더 엄격하게만 바꿀 수 있습니다."
                : "Limited by the workspace settings: " + string.Join(", ", parts) + ". A workspace file can only make the app settings stricter.";
        }

        public static string PolicyLabel(AgentDataPolicy policy, bool korean) => policy switch
        {
            AgentDataPolicy.SummaryOnly => korean ? "요약만" : "summary only",
            AgentDataPolicy.RowsWithApproval => korean ? "행 값 — 요청마다 승인" : "rows with approval",
            _ => korean ? "행 값 — 승인 없이" : "rows allowed",
        };
    }
}
