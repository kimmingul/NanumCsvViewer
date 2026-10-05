namespace NanumCsvViewer.Agent
{
    /// <summary>
    /// 에이전트 호스트(ChatController, CsvHostTools)가 읽는 설정. 앱 설정이 바뀔 때마다 새 인스턴스를 만들어 넘긴다
    /// (CsvHostTools는 Func로 그때그때 읽는다).
    /// </summary>
    /// <param name="OmpPath">omp 실행 파일 경로. null/빈 문자열이면 자동 탐색(PATH → %LOCALAPPDATA%\omp\omp.exe).
    /// 값이 있고 파일이 있으면 그 경로를 먼저 쓴다.</param>
    /// <param name="ExtraArgs">omp 명령줄에 그대로 덧붙일 인자(공백 구분, "..." 인용 가능). 예: --model anthropic/claude-sonnet-4-5</param>
    /// <param name="Language">"ko" 또는 "en". 가이드·알림 문구의 언어.</param>
    /// <param name="DataPolicy">모델로 보낼 수 있는 데이터 범위.</param>
    /// <param name="MaxRowsPerRequest">csv.get_rows 한 번에 돌려주는 행 상한.</param>
    /// <param name="AppVersion">앱 버전(가이드·rpc.log 머리말·/version 표시용).</param>
    /// <param name="AllowLocalPython">로컬 Python 분석 허용(기본 꺼짐). 켜면 omp 작업 폴더가 분석 결과 폴더가 되고, 에이전트가
    /// 현재 뷰를 로컬 파일로 내보내 omp eval(Python)로 분석하며 가이드에 Python 절이 추가된다.</param>
    /// <param name="ApprovalMode">승인 모드. host.yml의 tools.approvalMode와 앱 승인 카드 정책을 정한다. 바뀌면 같은 대화로 omp를 다시 시작한다.</param>
    /// <param name="ApprovalNoticePending">true면 다음 연결 때 기본 모드(yolo) 안내를 채팅에 한 번 보이고 ApprovalNoticeShown을 올린다.</param>
    /// <param name="Limits">작업 공간 파일(.ncvws)의 설정이 앱 설정보다 엄격해서 실제로 조여진 항목(<see cref="WorkspaceAgentPolicy.Apply"/>가 채운다).
    /// ApprovalMode·DataPolicy·AllowLocalPython은 이미 합쳐진(더 엄격한) 값이다.</param>
    /// <param name="SkillsEnabled">분석 스킬 묶음 전체 켜기/끄기. AllowLocalPython이 꺼져 있으면 어느 쪽이든 아무것도 싣지 않는다(토큰 비용 0).</param>
    /// <param name="SkillCategoriesOff">끈 스킬 분류(쉼표 목록: clinical, stats, ml).</param>
    /// <param name="SkillsOff">끈 개별 스킬 이름(쉼표 목록).</param>
    /// <param name="UseManagedPython">앱이 관리하는 Python 분석 환경(%LOCALAPPDATA%\NanumCsvViewer\python-analysis)이 있으면 에이전트의 Python을 그것으로 한다(기본 켬). 없으면 사용자 Python.</param>
    /// <param name="PythonEnvNoticePending">true면 관리 환경 없이 로컬 Python을 처음 켠 연결에서 설정 안내를 채팅에 한 번 보이고 PythonEnvNoticeShown을 올린다.</param>
    /// <param name="ModelUsageAlertedVersion">"최근 사용 모델을 읽을 수 없음" 경고를 이미 보인 omp 버전(없으면 빈 문자열). omp 버전마다 한 번만 경고하고 ModelUsageAlertShown이 새 값을 알린다.</param>
    public sealed record AgentHostOptions(
        string? OmpPath = null,
        string? ExtraArgs = null,
        string Language = "ko",
        AgentDataPolicy DataPolicy = AgentDataPolicy.SummaryOnly,
        int MaxRowsPerRequest = 200,
        string AppVersion = "",
        bool AllowLocalPython = false,
        AgentApprovalMode ApprovalMode = AgentApprovalPolicy.Default,
        bool ApprovalNoticePending = false,
        WorkspaceLimits Limits = default,
        bool SkillsEnabled = true,
        string SkillCategoriesOff = "",
        string SkillsOff = "",
        bool UseManagedPython = true,
        bool PythonEnvNoticePending = false,
        string ModelUsageAlertedVersion = "")
    {
        /// <summary>분석 스킬 구성(설정). 로컬 Python이 켜져 있을 때만 실제로 실린다(<see cref="SkillSelection"/>).</summary>
        internal SkillSelection Skills => SkillSelection.Create(SkillsEnabled, (SkillCategoriesOff ?? "").Split(','), (SkillsOff ?? "").Split(','));

        public bool IsKorean => !string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);

        /// <summary>추가 인자(--approval-mode·--yolo·--auto-approve)가 승인 모드를 고정하면 그 모드와 원인 인자, 아니면 null.</summary>
        public (AgentApprovalMode Mode, string Flag)? ForcedApproval => AgentApprovalPolicy.ForcedByArgs(ExtraArgs);

        /// <summary>실제로 적용되는 모드: 추가 인자가 고정하면 그 값(omp 명령줄이 host.yml보다 우선), 아니면 선택한 모드.</summary>
        public AgentApprovalMode EffectiveApprovalMode => ForcedApproval?.Mode ?? ApprovalMode;
    }
}
