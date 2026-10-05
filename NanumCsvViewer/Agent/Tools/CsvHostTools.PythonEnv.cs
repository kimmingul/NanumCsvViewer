using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Python;
using NanumCsvViewer.Agent.Tools;

namespace NanumCsvViewer.Agent
{
    /// <summary>호스트 도구가 사용자에게 보일 만한 진행 알림(예: 패키지 설치 단계)을 올리는 통로. level은 채팅 알림 수준(info·warn).</summary>
    public interface IToolNotices
    {
        event Action<string, string>? Notice;
    }

    // py.ensure_packages: 에이전트는 스스로 pip install 하지 않고, 앱 관리 분석 환경에 묶음 설치를 사용자 승인(FileSave 종류: 승인 모드를 따른다) 뒤에 요청한다.
    public sealed partial class CsvHostTools : IToolNotices
    {
        /// <summary>패키지 설치 대상. 테스트는 가짜 실행기를 쓰는 인스턴스를 넣는다.</summary>
        internal AnalysisEnvironment PythonEnvironment { get; init; } = AnalysisEnvironment.Default;

        public event Action<string, string>? Notice;

        private async Task<HostToolResult> EnsurePackagesAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            var options = Options();
            if (!options.AllowLocalPython)
                throw new AgentToolException("Local Python analysis is turned off in the app settings, so packages cannot be installed. Tell the user to turn it on if Python is needed.");
            if (!options.UseManagedPython)
                throw new AgentToolException("The user turned off \"Use the managed Python environment for the agent\" in the settings. Do not install anything; use the packages that are already available or say what is missing.");

            var requested = args.OptStringArray("groups", 4) ?? throw new AgentToolException("'groups' is required (core, stats, clinical, ml).");
            var groups = AnalysisGroups.Resolve(requested, out var unknown);
            if (unknown.Count > 0)
                throw new AgentToolException($"Unknown group(s): {string.Join(", ", unknown)}. Valid groups: {string.Join(", ", AnalysisGroups.Names)}.");
            string reason = (args.OptString("reason") ?? "").Trim();

            var env = PythonEnvironment;
            var info = env.Inspect();
            bool missing = !info.IsReady || groups.Any(g => info.Group(g.Name)?.State != AnalysisGroupState.Installed);
            if (!missing)
                return Reply($"Already installed: {string.Join(", ", groups.Select(g => g.Name))}.", EnvJson(env.Inspect(), groups, installedNow: false));

            var py = info.PythonVersion ?? new Version(3, 10);
            var lines = new List<string>();
            foreach (var g in groups.Where(g => info.Group(g.Name)?.State != AnalysisGroupState.Installed))
                lines.Add("+ " + g.Name + ": " + string.Join(", ", g.PinsFor(py).Select(p => $"{p.Name} {p.Version}")));
            foreach (var g in groups.Where(g => info.Group(g.Name)?.State == AnalysisGroupState.Installed))
                lines.Add("  " + g.Name + ": " + L("already installed", "이미 설치됨"));
            int mb = groups.Where(g => info.Group(g.Name)?.State != AnalysisGroupState.Installed).Sum(g => g.ApproxMb);
            if (mb > 0) lines.Add("  " + L($"Disk: about {mb} MB", $"디스크: 약 {mb} MB"));
            lines.Add("  " + L("Where: ", "위치: ") + env.VenvDir);
            lines.Add("  " + L("Source: PyPI over the internet; prebuilt wheels only, no source builds; nothing outside this folder is changed.",
                              "출처: 인터넷의 PyPI. 미리 빌드된 wheel만 쓰고 소스 빌드는 없으며, 이 폴더 밖은 바꾸지 않음."));
            if (!info.IsReady)
                lines.Add("  " + L("A new isolated Python environment is created first (needs Python 3.10+ on this PC).", "먼저 독립된 새 Python 환경을 만듭니다(이 PC에 Python 3.10 이상 필요)."));
            if (reason.Length > 0) lines.Add("  " + L("Why: ", "이유: ") + reason);

            bool approved = await approvals.ApproveAsync(
                L("Install Python packages: " + string.Join(", ", groups.Select(g => g.Name)), "Python 패키지 설치: " + string.Join(", ", groups.Select(g => g.Name))),
                L("Install the analysis package groups " + string.Join(", ", groups.Select(g => g.Name)) + " into the app-managed Python environment",
                  "앱 관리 Python 환경에 분석 패키지 묶음 " + string.Join(", ", groups.Select(g => g.Name)) + " 설치"),
                lines, ct, ApprovalKind.FileSave);
            if (!approved)
                throw new AgentToolException("The user did not approve installing. Nothing was installed. Use the packages that are already available, or tell the user what is missing and why.");

            var ui = SynchronizationContext.Current;
            string lastStep = "";
            void Progress(AnalysisProgress p)
            {
                if (p.Step == lastStep) return;
                lastStep = p.Step;
                string text = p.Step switch
                {
                    "venv" => L("Python packages: creating the analysis environment…", "Python 패키지: 분석 환경을 만드는 중…"),
                    "install" => L("Python packages: downloading and installing (this can take several minutes)…", "Python 패키지: 내려받아 설치하는 중(몇 분 걸릴 수 있음)…"),
                    "verify" => L("Python packages: checking the installation…", "Python 패키지: 설치 확인 중…"),
                    _ => "",
                };
                if (text.Length == 0) return;
                if (ui != null) ui.Post(_ => Notice?.Invoke("info", text), null); else Notice?.Invoke("info", text);
            }

            var result = await env.InstallAsync(groups.Select(g => g.Name), Progress, ct);
            if (!result.Ok)
            {
                string hint = result.Failure switch
                {
                    AnalysisFailure.NoPython => " Tell the user to install Python 3.10-3.13 (64-bit) from python.org and try again.",
                    AnalysisFailure.Offline => " The first install needs the internet; ask the user to check the connection, then call this tool again.",
                    AnalysisFailure.NoWheel => " Do not try to build from source; continue without that package.",
                    _ => "",
                };
                throw new AgentToolException(AnalysisMessages.Describe(result, false) + hint);
            }
            Notice?.Invoke("info", L("Python packages are ready.", "Python 패키지 준비 완료."));
            return Reply($"Installed: {string.Join(", ", groups.Select(g => g.Name))}.", EnvJson(env.Inspect(), groups, installedNow: true));
        }

        private JsonObject EnvJson(AnalysisEnvInfo info, IReadOnlyList<AnalysisGroup> asked, bool installedNow)
        {
            var packages = new JsonObject();
            foreach (var g in asked)
                if (info.Group(g.Name) is { } s) foreach (var kv in s.Versions) packages[kv.Key] = kv.Value;
            var json = new JsonObject
            {
                ["python"] = info.PythonPath,
                ["python_version"] = info.PythonVersion?.ToString(),
                ["groups_installed"] = ToolJson.Strings(info.InstalledGroupNames),
                ["packages"] = packages,
            };
            if (installedNow)
                json["note"] = "If eval's sys.executable differs from the 'python' path above, the app switches eval to this environment when the agent restarts right after this turn; until then run scripts with the 'python' path returned here (subprocess).";
            return json;
        }
    }
}
