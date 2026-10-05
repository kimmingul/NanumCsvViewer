using System.IO;
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
        /// <summary>승인 모드(always-ask | write | yolo). 기본 yolo. host.yml tools.approvalMode와 앱 승인 카드 정책.</summary>
        public string AgentApprovalMode { get; set; } = "yolo";
        /// <summary>승인 모드를 사용자가 직접 골랐거나 기본 모드 안내를 이미 보였으면 true(안내는 한 번만).</summary>
        public bool AgentApprovalNoticeShown { get; set; } = false;

        // ---- v3 작업 공간
        /// <summary>최근에 열거나 저장한 작업 공간 파일(.ncvws) 전체 경로, 가장 최근이 앞.</summary>
        public List<string> RecentWorkspaces { get; set; } = new();

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
            if (RecentWorkspaces.Count > MaxRecentWorkspaces) RecentWorkspaces.RemoveRange(MaxRecentWorkspaces, RecentWorkspaces.Count - MaxRecentWorkspaces);
        }

        /// <summary>최근 목록에서 뺀다(파일이 없어졌을 때).</summary>
        public void RemoveRecentWorkspace(string path)
            => RecentWorkspaces?.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

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
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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
