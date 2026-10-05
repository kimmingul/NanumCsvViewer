using System.Text;

namespace NanumCsvViewer.Agent.Python
{
    /// <summary>분석 환경 결과·상태의 사용자용(영/한) 문구와 에이전트 가이드 절.</summary>
    internal static class AnalysisMessages
    {
        public const string PythonDownloadUrl = "https://www.python.org/downloads/windows/";

        /// <summary>설치·제거 실패를 화면 문구로. detail은 영어 원문(마지막 줄들)이라 뒤에 붙인다.</summary>
        public static string Describe(AnalysisResult r, bool korean)
        {
            if (r.Ok) return r.Message;
            string head = r.Failure switch
            {
                AnalysisFailure.NoPython => korean
                    ? "이 PC에서 Python 3.10 이상을 찾지 못했습니다. python.org에서 64비트 Python 3.10~3.13을 설치하세요(설치 화면에서 \"Add python.exe to PATH\" 선택). 설치한 뒤 다시 시도하세요."
                    : "No Python 3.10 or newer was found on this PC. Install 64-bit Python 3.10-3.13 from python.org (tick \"Add python.exe to PATH\"), then try again.",
                AnalysisFailure.UnsupportedPlatform => korean
                    ? "이 Python은 64비트 Intel/AMD용(win-amd64)이 아닙니다. 고정한 데이터 분석 패키지는 win-amd64용 wheel만 있고 소스 빌드는 하지 않습니다. x64 Python 3.10~3.13을 설치하세요."
                    : "This Python is not 64-bit Intel/AMD (win-amd64). The pinned analysis packages only have win-amd64 wheels and the app never builds from source. Install an x64 Python 3.10-3.13.",
                AnalysisFailure.Offline => korean
                    ? "Python 패키지 서버(PyPI)에 연결하지 못했습니다. 인터넷·프록시 연결을 확인한 뒤 다시 시도하세요(이미 받은 파일은 이어서 쓰입니다)."
                    : "Could not reach the Python package server (PyPI). Check the internet/proxy connection and try again (files already downloaded are reused).",
                AnalysisFailure.NoWheel => korean
                    ? "이 Python·플랫폼용 미리 빌드된 wheel이 없는 패키지가 있습니다" + (r.Package != null ? $"({r.Package})" : "") + ". 소스 빌드는 하지 않으므로 다른 Python 버전(3.10~3.12 권장)을 설치해 보세요."
                    : "A package has no prebuilt wheel for this Python/platform" + (r.Package != null ? $" ({r.Package})" : "") + ". The app never builds from source; try another Python version (3.10-3.12 recommended).",
                AnalysisFailure.Cancelled => korean ? "취소했습니다. 받은 파일은 캐시에 남아 다음에 이어서 쓰입니다." : "Cancelled. Downloaded files stay cached and are reused next time.",
                AnalysisFailure.Busy => korean ? "다른 설치·제거가 진행 중입니다. 끝난 뒤 다시 시도하세요." : "Another install/remove is already running; try again when it finishes.",
                AnalysisFailure.VerifyFailed => korean
                    ? "설치는 됐지만 패키지를 불러오지 못했습니다. DLL 오류라면 Microsoft Visual C++ 재배포 가능 패키지(2015-2022, x64)를 설치하세요."
                    : "Installed, but a package failed to import. For DLL errors, install the Microsoft Visual C++ Redistributable 2015-2022 (x64).",
                AnalysisFailure.VenvFailed => korean ? "가상 환경을 만들지 못했습니다." : "Could not create the virtual environment.",
                AnalysisFailure.Io => korean ? "폴더를 읽거나 쓰지 못했습니다." : "Could not read or write the environment folder.",
                AnalysisFailure.UnknownGroup => korean ? "알 수 없는 패키지 묶음입니다." : "Unknown package group.",
                _ => korean ? "pip 실행이 실패했습니다." : "pip failed.",
            };
            bool showDetail = r.Failure is not (AnalysisFailure.Cancelled or AnalysisFailure.Busy or AnalysisFailure.Offline or AnalysisFailure.NoPython);
            return showDetail && r.Message.Length > 0 ? head + "\n" + r.Message : head;
        }

        public static string StateText(AnalysisGroupState state, bool korean) => state switch
        {
            AnalysisGroupState.Installed => korean ? "설치됨" : "Installed",
            AnalysisGroupState.Outdated => korean ? "업데이트 필요" : "Update available",
            AnalysisGroupState.Damaged => korean ? "손상됨 - 다시 설치" : "Damaged - reinstall",
            _ => korean ? "설치 안 됨" : "Not installed",
        };

        public static string FormatSize(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{bytes / 1024.0:0} KB";

        /// <summary>
        /// 가이드의 "Python environment" 절(영어, 모델용). 관리 환경을 쓰면(managed != null) 그 경로·설치 묶음·설치 규칙을, 아니면 사용자 Python을 쓴다는 것과
        /// 설정 안내를 쓴다. 에이전트는 스스로 pip install 하지 않고 py.ensure_packages를 부른다.
        /// </summary>
        public static string BuildGuide(AnalysisEnvInfo? managed, bool useManaged, string? userPython)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### Python environment");
            sb.AppendLine();
            bool active = useManaged && managed is { IsReady: true } && managed.Has(AnalysisGroups.Core);
            if (active)
            {
                var m = managed!;
                sb.AppendLine($"- `eval` (language `py`) runs in the **app-managed analysis environment** (Python {m.PythonVersion}, `{m.PythonPath}`), separate from the user's own Python. " +
                              "Use that exact interpreter for any script you run from a shell, too.");
                foreach (var g in m.Groups)
                {
                    string list = g.State is AnalysisGroupState.NotInstalled
                        ? "not installed"
                        : string.Join(", ", g.Group.PinsFor(m.PythonVersion ?? new Version(3, 10)).Select(p => g.Versions.TryGetValue(p.Name, out string? v) ? $"{p.Name} {v}" : $"{p.Name} (missing)"));
                    sb.AppendLine($"  - **{g.Group.Name}** — {g.Group.DescriptionEn} Status: {list}.");
                }
            }
            else
            {
                sb.AppendLine("- No app-managed analysis environment is in use; `eval` runs in the user's own Python" + (userPython != null ? $" (`{userPython}`)" : "") +
                              ", with whatever packages happen to be installed there. Tell the user **once** that Settings ▸ AI ▸ \"Python environment\" can create a managed environment " +
                              "with tested, pinned versions of pandas, scipy, statsmodels, matplotlib and more (core, stats, clinical, ml groups), or you can request it with `py.ensure_packages`.");
            }
            sb.AppendLine("- **Never** run `pip install`, `%pip`, `!pip` or `conda` yourself, and do not build packages from source. When an import fails or you need a package, " +
                          "call the host tool `py.ensure_packages` with the group names (`core`, `stats`, `clinical`, `ml`) that contain it. The app asks the user to approve, installs wheels only " +
                          "into the managed environment, and returns when done; then retry. If the user declines, or no group has the package, say so and use what is available.");
            if (!active)
                sb.AppendLine("- If `py.ensure_packages` creates the managed environment during this conversation, the app switches `eval` to it when the agent restarts (right after this turn). " +
                              "Until then run scripts with the interpreter path the tool returns.");
            return sb.ToString().TrimEnd();
        }
    }
}
