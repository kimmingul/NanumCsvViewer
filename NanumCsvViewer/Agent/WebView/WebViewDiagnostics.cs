using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace NanumCsvViewer.Agent
{
    /// <summary>WebView2 초기화 실패의 원인 분류. 안내 문구·버튼이 이 값으로 갈린다("설치" 안내는 RuntimeMissing에서만).</summary>
    internal enum WebViewProblem
    {
        None,
        /// <summary>런타임이 없음(GetAvailableBrowserVersionString 실패 / WebView2RuntimeNotFoundException).</summary>
        RuntimeMissing,
        /// <summary>0x8007139F이고 msedgewebview2.exe/앱 exe에 DPI 호환성 레이어가 걸려 있음.</summary>
        CompatLayer,
        /// <summary>0x8007139F인데 레이어는 없음(다른 프로세스가 같은 UDF를 다른 인자로 사용 등) — 앱 재시작.</summary>
        StateMismatch,
        /// <summary>0x80070005 — 사용자 데이터 폴더 권한.</summary>
        AccessDenied,
        /// <summary>0x8007007E / 0x800700C1 — 바이너리 누락·아키텍처 불일치: 재설치.</summary>
        BinaryOrArch,
        Other,
    }

    internal sealed record WebViewReport(
        string? RuntimeVersion,
        string UserDataFolder,
        string DpiAwareness,
        int DpiScalePercent,
        IReadOnlyList<CompatLayerEntry> CompatLayers,
        int? LastInitHResult,
        string? LastInitMessage,
        string LogPath,
        WebViewProblem Problem)
    {
        /// <summary>지원 요청에 붙여 넣는 진단 텍스트(로그·"진단 복사"와 같은 형식).</summary>
        public string ToDiagnosticText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("WebView2 runtime : " + (RuntimeVersion ?? "(not installed)"));
            sb.AppendLine("User data folder : " + UserDataFolder);
            sb.AppendLine($"App DPI awareness: {DpiAwareness} (system scale {DpiScalePercent}%)");
            sb.AppendLine("Last init error  : " + (LastInitHResult is { } hr
                ? $"0x{hr:X8} {LastInitMessage}" : "(none)"));
            sb.AppendLine("Problem          : " + Problem);
            sb.AppendLine("Compat layers    : " + (CompatLayers.Count == 0 ? "(none)" : ""));
            foreach (var l in CompatLayers)
                sb.AppendLine($"  [{l.Hive}] {l.ExePath} = {l.Flags}{(l.DpiRelated ? "  <DPI>" : "")}");
            sb.AppendLine("Init log         : " + LogPath);
            return sb.ToString();
        }
    }

    /// <summary>
    /// WebView2 환경 만들기(앱 전체가 같은 옵션)·초기화 실패 분류·진단 로그·진단 보고서.
    /// 채팅·마크다운·이미지 뷰어가 같은 사용자 데이터 폴더(UDF)를 쓰므로 환경은 반드시 <see cref="CreateEnvironmentAsync"/>로만 만든다:
    /// 같은 UDF를 여는 환경의 AdditionalBrowserArguments가 서로 다르면 그 자체가 0x8007139F의 원인이다.
    /// </summary>
    internal static class WebViewDiagnostics
    {
        public const string RuntimeInstallUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>지원용: 환경변수 NANUMCSV_WEBVIEW2_LOG=1이면 브라우저 로그(--enable-logging)를 agent\webview2-browser.log에 남긴다. 프로세스 시작 시 한 번만 읽는다.</summary>
        private static readonly bool BrowserLogEnabled =
            Environment.GetEnvironmentVariable("NANUMCSV_WEBVIEW2_LOG") == "1";

        private static readonly object LogLock = new();
        private static int? _lastHResult;
        private static string? _lastMessage;

        /// <summary>WebView2 초기화가 실패했고 아직 성공하지 못했다.</summary>
        public static bool LastInitFailed { get; private set; }

        /// <summary>초기화 실패 시(UI 스레드) 한 줄 오류 문구와 함께 발생. 마법사가 자동으로 열리는 데 쓴다.</summary>
        public static event Action<string>? InitFailed;

        private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static string UserDataFolder => Path.Combine(Local, "NanumCsvViewer", "WebView2");
        public static string LogPath => Path.Combine(Local, "NanumCsvViewer", "agent", "webview2-init.log");
        public static string BrowserLogPath => Path.Combine(Local, "NanumCsvViewer", "agent", "webview2-browser.log");

        /// <summary>브라우저 인자(null이면 옵션 없음). 경로에 공백이 있으면 따옴표로 감싼다.</summary>
        internal static string? BrowserArguments(bool logEnabled, string logPath) =>
            logEnabled ? $"--enable-logging --v=0 --log-file={(logPath.Contains(' ') ? "\"" + logPath + "\"" : logPath)}" : null;

        /// <summary>앱의 모든 WebView2가 쓰는 환경(같은 UDF·같은 옵션).</summary>
        public static Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            CoreWebView2EnvironmentOptions? options = null;
            if (BrowserArguments(BrowserLogEnabled, BrowserLogPath) is { } args)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BrowserLogPath)!);
                options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = args };
            }
            return CoreWebView2Environment.CreateAsync(null, UserDataFolder, options);
        }

        // ── 분류 ───────────────────────────────────────────────────────────────────

        private const int ErrInvalidState = unchecked((int)0x8007139F);
        private const int ErrAccessDenied = unchecked((int)0x80070005);
        private const int ErrModNotFound = unchecked((int)0x8007007E);
        private const int ErrBadExeFormat = unchecked((int)0x800700C1);

        /// <summary>예외(와 내부 예외)에서 알려진 HRESULT를 찾는다. 없으면 바깥 예외의 HResult.</summary>
        internal static int HResultOf(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
                if (e.HResult is ErrInvalidState or ErrAccessDenied or ErrModNotFound or ErrBadExeFormat) return e.HResult;
            return ex.HResult;
        }

        internal static WebViewProblem Classify(Exception ex, string? runtimeVersion, IReadOnlyList<CompatLayerEntry> layers)
        {
            if (string.IsNullOrEmpty(runtimeVersion) || ex is WebView2RuntimeNotFoundException) return WebViewProblem.RuntimeMissing;
            return ClassifyHResult(HResultOf(ex), layers);
        }

        internal static WebViewProblem ClassifyHResult(int hr, IReadOnlyList<CompatLayerEntry> layers) => hr switch
        {
            ErrInvalidState => layers.Any(l => l.DpiRelated) ? WebViewProblem.CompatLayer : WebViewProblem.StateMismatch,
            ErrAccessDenied => WebViewProblem.AccessDenied,
            ErrModNotFound or ErrBadExeFormat => WebViewProblem.BinaryOrArch,
            _ => WebViewProblem.Other,
        };

        // ── 보고서·로그 ────────────────────────────────────────────────────────────

        private static string? RuntimeVersion()
        {
            try { var v = CoreWebView2Environment.GetAvailableBrowserVersionString(); return string.IsNullOrEmpty(v) ? null : v; }
            catch (Exception) { return null; }
        }

        public static WebViewReport Collect()
        {
            string? version = RuntimeVersion();
            var layers = CompatLayers.Find();
            var problem = WebViewProblem.None;
            if (version is null) problem = WebViewProblem.RuntimeMissing;
            else if (_lastHResult is { } hr) problem = ClassifyHResult(hr, layers);
            return new WebViewReport(version, UserDataFolder, DpiAwarenessName(), SystemDpi() * 100 / 96,
                layers, _lastHResult, _lastMessage, LogPath, problem);
        }

        /// <summary>초기화 실패를 기록(로그 파일 + 마지막 오류)하고 <see cref="InitFailed"/>를 올린다. 로그 쓰기 실패는 무시한다.</summary>
        public static void RecordFailure(Exception ex)
        {
            _lastHResult = HResultOf(ex);
            _lastMessage = ex.Message;
            LastInitFailed = true;
            try
            {
                var report = Collect();
                var sb = new StringBuilder();
                sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} WebView2 init failed (app {AppInfo.Version}) ===");
                sb.Append(report.ToDiagnosticText());
                sb.AppendLine("Exception        : " + ex.GetType().FullName + ": " + ex.Message);
                sb.AppendLine();
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception) { /* 진단 로그 실패가 안내 화면을 막지 않는다 */ }
            InitFailed?.Invoke($"{ex.Message} (0x{_lastHResult:X8})");
        }

        public static void RecordSuccess()
        {
            LastInitFailed = false;
            _lastHResult = null;
            _lastMessage = null;
        }

        // ── DPI ────────────────────────────────────────────────────────────────────

        [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
        [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

        private static int SystemDpi()
        {
            try { return (int)GetDpiForSystem(); }
            catch (Exception) { return 96; }
        }

        /// <summary>호출 스레드의 DPI 인식 모드("Unaware"|"GdiScaled"|"System"|"PerMonitor"|"PerMonitorV2"|"Unknown").</summary>
        public static string DpiAwarenessName()
        {
            try
            {
                var ctx = GetThreadDpiAwarenessContext();
                if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-4))) return "PerMonitorV2";
                if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-5))) return "GdiScaled";
                return GetAwarenessFromDpiAwarenessContext(ctx) switch
                {
                    0 => "Unaware",
                    1 => "System",
                    2 => "PerMonitor",
                    _ => "Unknown",
                };
            }
            catch (Exception) { return "Unknown"; }
        }
    }
}
