using System.Text;

namespace NanumCsvViewer.Agent
{
    /// <summary>WebView2 초기화 실패 안내문(원인별). 화면 없이 테스트할 수 있도록 언어를 인자로 받는다.</summary>
    internal static class WebViewFailureText
    {
        /// <summary>설치 링크는 런타임이 실제로 없을 때만 보인다.</summary>
        public static bool ShowsInstallLink(WebViewProblem problem) => problem == WebViewProblem.RuntimeMissing;

        public static string Build(WebViewProblem problem, int hresult, string message, string userDataFolder,
            string logPath, IReadOnlyList<CompatLayerEntry> layers, bool ko)
        {
            string T(string en, string kr) => ko ? kr : en;
            string code = "0x" + hresult.ToString("X8");
            var sb = new StringBuilder();
            switch (problem)
            {
                case WebViewProblem.RuntimeMissing:
                    sb.Append(T("The AI chat needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.",
                                "AI 채팅에는 Microsoft Edge WebView2 런타임이 필요하지만 이 PC에는 설치되어 있지 않습니다."));
                    sb.Append("\r\n\r\n").Append(T("Install it, then press Retry.", "설치한 뒤 [다시 시도]를 누르세요."));
                    return sb.ToString();

                case WebViewProblem.CompatLayer:
                case WebViewProblem.StateMismatch:
                    sb.Append(T($"The chat page could not be started: WebView2 is in a state that does not match this app ({code}).",
                                $"채팅 화면을 시작하지 못했습니다: WebView2의 설정이 이 앱과 맞지 않습니다 ({code})."));
                    sb.Append("\r\n\r\n");
                    var dpi = layers.Where(l => l.DpiRelated).ToList();
                    if (dpi.Count > 0)
                    {
                        sb.Append(T("Compatibility settings force a DPI mode on WebView2 (msedgewebview2.exe) or on this app. Found:",
                                    "WebView2(msedgewebview2.exe) 또는 이 앱에 고해상도 DPI 호환성 설정이 걸려 있습니다. 발견된 설정:"));
                        foreach (var l in dpi)
                            sb.Append("\r\n  [").Append(l.Hive).Append("] ").Append(l.ExePath).Append(" = ").Append(l.Flags);
                        sb.Append("\r\n\r\n");
                        if (dpi.Any(l => l.Removable))
                            sb.Append(T("Press [Remove setting] to remove the current-user entries (a backup .reg is saved first), then press Retry.",
                                        "[설정 해제]를 누르면 현재 사용자(HKCU) 항목을 해제합니다(먼저 백업 .reg를 저장). 그다음 [다시 시도]를 누르세요."));
                        if (dpi.Any(l => !l.Removable))
                        {
                            if (dpi.Any(l => l.Removable)) sb.Append("\r\n");
                            sb.Append(T("[HKLM] entries apply to all users and need administrator rights: clear the exe's Properties → Compatibility → Change high DPI settings as an administrator, or remove the value with an elevated regedit.",
                                        "[HKLM] 항목은 모든 사용자에게 적용되어 관리자 권한이 필요합니다: 관리자 권한으로 해당 exe의 속성 → 호환성 → '높은 DPI 설정 변경'을 해제하거나 관리자 권한 regedit에서 값을 삭제하세요."));
                        }
                    }
                    else
                    {
                        sb.Append(T("No DPI compatibility setting was found. Another program may be using the same WebView2 data folder with different settings.",
                                    "DPI 호환성 설정은 발견되지 않았습니다. 다른 프로그램이 같은 WebView2 데이터 폴더를 다른 설정으로 쓰고 있을 수 있습니다."));
                    }
                    sb.Append("\r\n\r\n").Append(T("If it still fails, close Nanum CSV Viewer completely and start it again.",
                                                    "그래도 안 되면 Nanum CSV Viewer를 완전히 종료한 뒤 다시 실행하세요."));
                    break;

                case WebViewProblem.AccessDenied:
                    sb.Append(T($"The chat page could not be started: access was denied ({code}).",
                                $"채팅 화면을 시작하지 못했습니다: 접근이 거부되었습니다 ({code})."));
                    sb.Append("\r\n\r\n").Append(T("WebView2 data folder: ", "WebView2 데이터 폴더: ")).Append(userDataFolder);
                    sb.Append("\r\n").Append(T("Check that your account can write to this folder (and that security software is not blocking it), then press Retry.",
                                               "이 폴더에 현재 계정이 쓸 수 있는지(보안 프로그램이 막고 있지 않은지) 확인한 뒤 [다시 시도]를 누르세요."));
                    break;

                case WebViewProblem.BinaryOrArch:
                    sb.Append(T($"The chat page could not be started: the WebView2 Runtime files are missing or do not match this PC ({code}).",
                                $"채팅 화면을 시작하지 못했습니다: WebView2 런타임 파일이 없거나 이 PC와 맞지 않습니다 ({code})."));
                    sb.Append("\r\n\r\n").Append(T("Repair or reinstall \"Microsoft Edge WebView2 Runtime\" (Windows Settings → Apps), then press Retry.",
                                                    "Windows 설정 → 앱에서 \"Microsoft Edge WebView2 런타임\"을 복구하거나 다시 설치한 뒤 [다시 시도]를 누르세요."));
                    break;

                default:
                    sb.Append(T("The chat page could not be started: ", "채팅 화면을 시작하지 못했습니다: ")).Append(message).Append(" (").Append(code).Append(')');
                    break;
            }
            sb.Append("\r\n\r\n").Append(T("Details: ", "자세한 내용: ")).Append(logPath);
            return sb.ToString();
        }
    }
}
