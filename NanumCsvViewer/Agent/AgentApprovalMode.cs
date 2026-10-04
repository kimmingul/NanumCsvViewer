namespace NanumCsvViewer.Agent
{
    /// <summary>
    /// 에이전트 승인 모드. omp의 tools.approvalMode(host.yml)와 앱 승인 카드(csv.* 도구)에 같이 적용된다.
    /// always-ask: omp 쓰기·실행 도구와 앱 카드를 모두 묻는다. write: 앱의 되돌릴 수 있는 데이터 편집은 자동 승인, omp 쓰기 도구도 자동,
    /// omp 실행 도구(bash·Python)는 묻는다. yolo: omp는 아무것도 묻지 않고 앱도 파일 저장까지 자동 승인한다.
    /// 원시 행 공유 승인(RowsWithApproval 데이터 정책)은 어느 모드에서도 묻는다.
    /// </summary>
    public enum AgentApprovalMode
    {
        AlwaysAsk,
        Write,
        Yolo,
    }

    /// <summary>승인 모드 ↔ 설정 문자열·host.yml 값·앱 카드 정책.</summary>
    public static class AgentApprovalPolicy
    {
        /// <summary>설정에 값이 없거나 알 수 없으면 쓰는 기본 모드.</summary>
        public const AgentApprovalMode Default = AgentApprovalMode.Yolo;

        /// <summary>omp tools.approvalMode 값(= 채팅 페이지 approval-select의 option value).</summary>
        public static string ToOmp(AgentApprovalMode mode) => mode switch
        {
            AgentApprovalMode.AlwaysAsk => "always-ask",
            AgentApprovalMode.Write => "write",
            _ => "yolo",
        };

        /// <summary>"always-ask" | "write" | "yolo"(대소문자·공백 무시). 그 밖은 기본 모드.</summary>
        public static AgentApprovalMode Parse(string? text) => TryParse(text, out var mode) ? mode : Default;

        /// <summary>알려진 값만 받는다(페이지 메시지처럼 신뢰할 수 없는 입력용).</summary>
        public static bool TryParse(string? text, out AgentApprovalMode mode)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "always-ask": case "alwaysask": mode = AgentApprovalMode.AlwaysAsk; return true;
                case "write": mode = AgentApprovalMode.Write; return true;
                case "yolo": mode = AgentApprovalMode.Yolo; return true;
                default: mode = Default; return false;
            }
        }

        /// <summary>이 모드에서 앱 승인 카드를 묻지 않고 통과시키는 종류인가. RowSharing은 항상 false.</summary>
        public static bool AutoApproves(AgentApprovalMode mode, ApprovalKind kind) => (mode, kind) switch
        {
            (AgentApprovalMode.Write, ApprovalKind.DataEdit) => true,
            (AgentApprovalMode.Yolo, ApprovalKind.DataEdit) => true,
            (AgentApprovalMode.Yolo, ApprovalKind.FileSave) => true,
            _ => false,
        };
    }
}
