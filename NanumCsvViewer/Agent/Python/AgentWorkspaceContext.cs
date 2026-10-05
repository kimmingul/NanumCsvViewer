namespace NanumCsvViewer.Agent
{
    /// <summary>에이전트가 아는 열린 탭 하나. <see cref="Kind"/>은 "file" · "sheet"(엑셀·SAS·SPSS·SQLite 워크북) · "view"(뷰 테이블) · "result"(질의 결과).</summary>
    /// <param name="Name">탭 이름(파일 이름, 워크북은 "파일 [시트]", 뷰는 뷰 이름).</param>
    /// <param name="Path">원본 파일 경로. 뷰·질의 결과는 임시 파일이라 null.</param>
    /// <param name="Active">지금 활성 탭인가.</param>
    public sealed record AgentTableEntry(string Name, string? Path, string Kind, bool Active)
    {
        public const string KindFile = "file", KindSheet = "sheet", KindView = "view", KindResult = "result";

        /// <summary>사용자가 연 원본 데이터 파일인가(뷰·결과가 아니고 경로가 있다).</summary>
        public bool IsDataFile => Kind is KindFile or KindSheet && !string.IsNullOrWhiteSpace(Path);
    }

    /// <summary>
    /// 에이전트 호스트가 컨트롤러에 알려 주는 작업 공간 상태: 작업 공간 파일(.ncvws, 저장했다면)과 열린 탭 목록, 작업 공간 메모.
    /// 탭을 바꾸거나 열고 닫아도 omp는 다시 시작하지 않는다 — 분석 폴더는 작업 공간 파일(있으면) 또는 이 세션에서 처음 연 데이터 파일로 정해진다.
    /// </summary>
    /// <param name="Notes">작업 공간 메모(자료 설명·핵심 관계·분석 목표). omp를 (다시) 시작할 때 가이드에 자료로 실린다. 없으면 null.</param>
    public sealed record AgentWorkspaceContext(string? WorkspaceFile, IReadOnlyList<AgentTableEntry> Tables, string? Notes = null)
    {
        public static AgentWorkspaceContext Empty { get; } = new(null, Array.Empty<AgentTableEntry>());

        /// <summary>파일 하나만 열린 상태.</summary>
        public static AgentWorkspaceContext ForFile(string? path, string? workspaceFile = null) =>
            string.IsNullOrWhiteSpace(path)
                ? new(workspaceFile, Array.Empty<AgentTableEntry>())
                : new(workspaceFile, new[] { new AgentTableEntry(System.IO.Path.GetFileName(path), path, AgentTableEntry.KindFile, true) });

        /// <summary>열린 탭 중 첫 데이터 파일의 경로(없으면 null).</summary>
        public string? FirstDataFile => Tables.FirstOrDefault(t => t.IsDataFile)?.Path;
    }
}
