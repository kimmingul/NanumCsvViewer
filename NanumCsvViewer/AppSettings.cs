using System.IO;
using NanumCsvViewer.Csv;
using System.Text.Json;

namespace NanumCsvViewer
{
    /// <summary>%AppData%\NanumCsvViewer\settings.json 에 저장되는 사용자 설정(테마·언어).</summary>
    public sealed class AppSettings
    {
        /// <summary>"Light" | "Dark" | "" (빈 값이면 Windows 시스템 테마 따름).</summary>
        public string Theme { get; set; } = "";

        /// <summary>"auto"(OS 언어 따름) | "en" | "ko".</summary>
        public string Language { get; set; } = "auto";

        /// <summary>그리드 헤더에 컬럼 타입 배지를 표시할지 여부.</summary>
        public bool ShowTypeBadges { get; set; } = true;

        /// <summary>SPSS·SAS를 열 때 변수/값 라벨을 표시할지 여부(끄면 원값·변수명).</summary>
        public bool ShowFieldLabels { get; set; } = false;

        /// <summary>CSV를 닫을 때(다른 파일 열기·종료) 해당 파일의 영속 인덱스 캐시를 삭제할지 여부.</summary>
        public bool DeleteIndexOnClose { get; set; } = false;

        // ---- v2 AI 에이전트
        /// <summary>omp 실행 파일 경로. 비우면 자동 탐색.</summary>
        public string? AgentOmpPath { get; set; }
        /// <summary>omp 명령줄에 덧붙일 인자.</summary>
        public string? AgentExtraArgs { get; set; }
        /// <summary>AgentDataPolicy 이름(SummaryOnly | RowsWithApproval | RowsAllowed).</summary>
        public string AgentDataPolicy { get; set; } = "SummaryOnly";
        public int AgentMaxRows { get; set; } = 200;
        public int AgentPanelWidth { get; set; } = 460;
        /// <summary>로컬 Python 분석 허용(기본 꺼짐). 켜면 에이전트가 현재 뷰를 로컬 파일로 내보내 Python(omp eval)으로 분석할 수 있다.</summary>
        public bool AgentAllowLocalPython { get; set; } = false;
        /// <summary>앱이 관리하는 Python 분석 환경(python-analysis)이 있으면 에이전트 Python을 그것으로 한다(기본 켬). 바뀌면 다음 omp (재)시작부터 반영.</summary>
        public bool AgentUseManagedPython { get; set; } = true;
        /// <summary>관리 환경이 없어서 사용자 Python으로 분석한다는 안내를 이미 보였으면 true(한 번만).</summary>
        public bool AgentPythonEnvNoticeShown { get; set; } = false;
        /// <summary>분석 스킬 묶음 전체 켜기/끄기(기본 켜짐). 로컬 Python이 켜져 있을 때만 실린다. 바뀌면 다음 omp (재)시작부터 반영.</summary>
        public bool AgentSkillsEnabled { get; set; } = true;
        /// <summary>끈 스킬 분류(쉼표 목록: clinical, stats, ml). 비면 모두 켜짐.</summary>
        public string AgentSkillCategoriesOff { get; set; } = "";
        /// <summary>끈 개별 스킬 이름(쉼표 목록). 비면 모두 켜짐.</summary>
        public string AgentSkillsOff { get; set; } = "";
        /// <summary>승인 모드(always-ask | write | yolo). 기본 yolo. host.yml tools.approvalMode와 앱 승인 카드 정책.</summary>
        public string AgentApprovalMode { get; set; } = "yolo";
        /// <summary>승인 모드를 사용자가 직접 골랐거나 기본 모드 안내를 이미 보였으면 true(안내는 한 번만).</summary>
        public bool AgentApprovalNoticeShown { get; set; } = false;

        // ---- v3 작업 공간
        /// <summary>최근에 열거나 저장한 작업 공간 파일(.ncvws) 전체 경로, 가장 최근이 앞.</summary>
        public List<string> RecentWorkspaces { get; set; } = new();

        // ---- v3.2 설정 대화 상자
        /// <summary>시작할 때 처음 보이는 패널(기본: AI 켜짐, 나머지 꺼짐, 셀 값 표시줄 켜짐). <see cref="RememberLastPanels"/>가 켜져 있으면 무시된다.</summary>
        public PanelLayout StartupPanels { get; set; } = new();

        /// <summary>true면 시작할 때 <see cref="StartupPanels"/> 대신 마지막으로 종료할 때의 패널 상태(<see cref="LastPanels"/>)를 쓴다.</summary>
        public bool RememberLastPanels { get; set; } = false;

        /// <summary>마지막으로 종료할 때의 패널 상태(작업 공간 파일이 열려 있었다면 그 레이아웃은 작업 공간 파일에 있으므로 갱신하지 않는다).</summary>
        public PanelLayout? LastPanels { get; set; }

        /// <summary>패널 폭(96 DPI 기준 논리 단위, 0 = 기본). 사용자가 분할선을 끌 때마다 갱신된다.</summary>
        public int DetailPanelWidth { get; set; } = 0;
        public int ExplorerPanelWidth { get; set; } = 0;

        /// <summary>true면 종료할 때 창 위치·크기·최대화 상태를 저장해 다음에 복원한다.</summary>
        public bool RememberWindow { get; set; } = true;
        public WindowGeometry? Window { get; set; }

        /// <summary>true면 시작할 때 마지막으로 열어 둔 작업 공간(.ncvws)을 다시 연다(명령줄로 연 파일이 있으면 그쪽이 우선).</summary>
        public bool ReopenLastWorkspace { get; set; } = false;
        /// <summary>종료할 때 열려 있던 작업 공간 파일(없었으면 null).</summary>
        public string? LastWorkspace { get; set; }

        /// <summary>셀 한 칸에 보이는 최대 줄 수(줄바꿈이 있는 셀의 행 높이 상한).</summary>
        public int MaxCellLines { get; set; } = DefaultMaxCellLines;
        public const int DefaultMaxCellLines = 6;

        /// <summary>그리드 글꼴 크기(pt). 0이면 창 기본 글꼴 크기.</summary>
        public float GridFontSize { get; set; } = 0f;

        /// <summary>"auto"(감지) 또는 EncodingDetector.SelectableNames 중 하나. BOM 없는 UTF-8/CP949 파일을 열 때 감지 대신 이 인코딩을 쓴다.</summary>
        public string DefaultEncoding { get; set; } = AutoEncoding;
        public const string AutoEncoding = "auto";

        /// <summary>최근 작업 공간 목록에 담는 개수(1~<see cref="MaxRecentWorkspaces"/>).</summary>
        public int RecentCount { get; set; } = DefaultRecentCount;
        public const int DefaultRecentCount = 10;

        /// <summary>최근 작업 공간 목록에 담는 최대 개수.</summary>
        public const int MaxRecentWorkspaces = 10;

        /// <summary>작업 공간 파일을 최근 목록의 맨 앞에 올린다(이미 있으면 옮김, 대소문자 무시). 저장은 호출한 쪽이 한다.</summary>
        public void AddRecentWorkspace(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); } catch (ArgumentException) { return; }
            RecentWorkspaces ??= new();
            RecentWorkspaces.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
            RecentWorkspaces.Insert(0, full);
            TrimRecent();
        }

        /// <summary>최근 목록에서 뺀다(파일이 없어졌을 때).</summary>
        public void RemoveRecentWorkspace(string path)
            => RecentWorkspaces?.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>최근 목록을 <see cref="RecentCount"/>개로 자른다(개수 설정을 줄였을 때).</summary>
        public void TrimRecent()
        {
            RecentWorkspaces ??= new();
            int keep = Math.Clamp(RecentCount, 1, MaxRecentWorkspaces);
            if (RecentWorkspaces.Count > keep) RecentWorkspaces.RemoveRange(keep, RecentWorkspaces.Count - keep);
        }

        /// <summary>
        /// 손으로 고친 설정·옛 파일에 견디도록 범위 밖 값을 다듬는다: 알 수 없는 테마·언어·인코딩은 기본값, 숫자는 허용 범위로, 빈 패널 절은 기본 레이아웃.
        /// </summary>
        public AppSettings Normalize()
        {
            Theme = Theme is "Light" or "Dark" ? Theme : "";
            Language = Language is "en" or "ko" ? Language : "auto";
            StartupPanels = (StartupPanels ?? new()).Normalized();
            LastPanels = LastPanels?.Normalized();
            DetailPanelWidth = DetailPanelWidth <= 0 ? 0 : Math.Clamp(DetailPanelWidth, PanelLayout.MinWidth, PanelLayout.MaxWidth);
            ExplorerPanelWidth = ExplorerPanelWidth <= 0 ? 0 : Math.Clamp(ExplorerPanelWidth, PanelLayout.MinWidth, PanelLayout.MaxWidth);
            MaxCellLines = Math.Clamp(MaxCellLines, 1, 20);
            GridFontSize = GridFontSize <= 0 || float.IsNaN(GridFontSize) ? 0f : Math.Clamp(GridFontSize, 7f, 24f);
            DefaultEncoding = DefaultEncoding is EncodingDetector.Utf8 or EncodingDetector.Cp949 ? DefaultEncoding : AutoEncoding;
            RecentCount = Math.Clamp(RecentCount, 1, MaxRecentWorkspaces);
            RecentWorkspaces ??= new();
            TrimRecent();
            return this;
        }

        /// <summary>
        /// 설정 대화 상자의 "기본값으로" 대상(일반·패널·그리드·파일)을 기본값으로 되돌린다. 최근 작업 공간 목록·AI 승인 안내 표시·마지막 상태/창 위치는 건드리지 않는다.
        /// AI 설정은 대화 상자의 AI 쪽에서 따로 되돌린다.
        /// </summary>
        public void ResetGeneral()
        {
            var d = new AppSettings();
            Theme = d.Theme; Language = d.Language; ReopenLastWorkspace = d.ReopenLastWorkspace;
        }

        public void ResetPanels()
        {
            var d = new AppSettings();
            StartupPanels = d.StartupPanels; RememberLastPanels = d.RememberLastPanels; RememberWindow = d.RememberWindow;
        }

        public void ResetGrid()
        {
            var d = new AppSettings();
            ShowTypeBadges = d.ShowTypeBadges; ShowFieldLabels = d.ShowFieldLabels; MaxCellLines = d.MaxCellLines; GridFontSize = d.GridFontSize;
        }

        public void ResetFiles()
        {
            var d = new AppSettings();
            DefaultEncoding = d.DefaultEncoding; DeleteIndexOnClose = d.DeleteIndexOnClose; RecentCount = d.RecentCount;
        }

        /// <summary>테스트가 사용자의 실제 설정 파일(%APPDATA%\NanumCsvViewer\settings.json)을 건드리지 않도록 설정 폴더를 바꾸는 이음매. 앱은 쓰지 않는다.</summary>
        internal static string? DirectoryOverride { get; set; }

        private static string Dir =>
            DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NanumCsvViewer");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))?.Normalize() ?? new AppSettings();
            }
            catch { /* 손상/접근 불가 시 기본값 */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 저장 실패는 무시(다음 실행에 영향만) */ }
        }
    }
}
