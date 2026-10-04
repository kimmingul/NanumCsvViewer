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
    public sealed record AgentHostOptions(
        string? OmpPath = null,
        string? ExtraArgs = null,
        string Language = "ko",
        AgentDataPolicy DataPolicy = AgentDataPolicy.SummaryOnly,
        int MaxRowsPerRequest = 200,
        string AppVersion = "",
        bool AllowLocalPython = false)
    {
        public bool IsKorean => !string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
    }
}
