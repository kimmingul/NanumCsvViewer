using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Python;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncv-py-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    internal sealed class FakeRunner : IProcessRunner
    {
        public List<(string Exe, string[] Args, string? Cwd)> Calls { get; } = new();
        public Func<string, string[], ProcessResult> OnRun { get; set; } = (_, _) => new ProcessResult(0, "");

        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string? workingDirectory, TimeSpan timeout,
            Action<string>? onLine, CancellationToken cancellation)
        {
            var arr = args.ToArray();
            lock (Calls) Calls.Add((exe, arr, workingDirectory));
            var result = OnRun(exe, arr);
            foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine?.Invoke(line);
            return Task.FromResult(result);
        }
    }

    public class AgentWorkspaceTests
    {
        [Fact]
        public void Output_folder_is_next_to_the_file_named_after_it_without_extension()
        {
            Assert.Equal(@"C:\data\sales_분석결과", AgentWorkspace.ComputeOutputFolder(@"C:\data\sales.csv"));
            Assert.Equal(@"C:\data\a.b_분석결과", AgentWorkspace.ComputeOutputFolder(@"C:\data\a.b.xlsx"));
            Assert.Equal(@"C:\d\x_분석결과", AgentWorkspace.ComputeOutputFolder(@"C:\d\sub\..\x.sav"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void No_file_falls_back_to_the_documents_folder(string? path)
        {
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NanumCsvViewer", "분석결과");
            Assert.Equal(expected, AgentWorkspace.ComputeOutputFolder(path));
        }

        [Fact]
        public void OutputFolderFor_creates_the_folder_on_demand_and_is_idempotent()
        {
            using var tmp = new TempFolder();
            string csv = tmp.Combine("my data.csv");
            string expected = tmp.Combine("my data_분석결과");
            Assert.False(Directory.Exists(expected));
            Assert.Equal(expected, AgentWorkspace.OutputFolderFor(csv));
            Assert.True(Directory.Exists(expected));
            File.WriteAllText(Path.Combine(expected, "keep.txt"), "x");
            Assert.Equal(expected, AgentWorkspace.OutputFolderFor(csv));
            Assert.True(File.Exists(Path.Combine(expected, "keep.txt")));
            Assert.False(File.Exists(csv));   // 원본(여기선 없음)을 만들지 않는다
        }

        [Fact]
        public void IsInside_means_strictly_below_the_folder()
        {
            string root = @"C:\data\out";
            Assert.True(AgentWorkspace.IsInside(root, @"C:\data\out\fig.png"));
            Assert.True(AgentWorkspace.IsInside(root + "\\", @"c:\DATA\OUT\sub\x.md"));
            Assert.False(AgentWorkspace.IsInside(root, root));                      // 폴더 자체는 아님
            Assert.False(AgentWorkspace.IsInside(root, @"C:\data\out2\fig.png"));   // 접두만 같은 형제
            Assert.False(AgentWorkspace.IsInside(root, @"C:\data\out\..\secret.txt"));
            Assert.False(AgentWorkspace.IsInside(root, @"D:\data\out\fig.png"));
            Assert.False(AgentWorkspace.IsInside(root, ""));
        }

        [Fact]
        public void ResolveInside_accepts_relative_and_absolute_paths_below_the_folder_only()
        {
            string root = @"C:\data\out";
            Assert.Equal(@"C:\data\out\fig.png", AgentWorkspace.ResolveInside(root, "fig.png"));
            Assert.Equal(@"C:\data\out\sub\a.md", AgentWorkspace.ResolveInside(root, "sub/a.md"));
            Assert.Equal(@"C:\data\out\fig.png", AgentWorkspace.ResolveInside(root, "\"C:\\data\\out\\fig.png\""));
            Assert.Null(AgentWorkspace.ResolveInside(root, "..\\secret.txt"));
            Assert.Null(AgentWorkspace.ResolveInside(root, @"C:\Windows\win.ini"));
            Assert.Null(AgentWorkspace.ResolveInside(root, "sub/../../x.png"));
            Assert.Null(AgentWorkspace.ResolveInside(root, " "));
        }

        [Fact]
        public void ToUrlPath_escapes_each_segment_and_refuses_outside_paths()
        {
            string root = @"C:\data\out";
            Assert.Equal("a%20b/%EC%B0%A8%ED%8A%B8%231.png", AgentWorkspace.ToUrlPath(root, @"C:\data\out\a b\차트#1.png"));
            Assert.Null(AgentWorkspace.ToUrlPath(root, @"C:\data\other\x.png"));
        }

        [Theory]
        [InlineData("a.PNG", true)]
        [InlineData("a.jpeg", true)]
        [InlineData("a.svg", true)]
        [InlineData("a.pdf", false)]
        [InlineData("a.csv", false)]
        public void Image_extensions(string name, bool expected) => Assert.Equal(expected, AgentWorkspace.IsImageFile(name));
    }

    public class PythonLocatorTests
    {
        private const string Py = @"C:\Python311\python.exe";

        [Fact]
        public void Omp_check_output_is_parsed_even_with_surrounding_text()
        {
            Assert.Equal(Py, PythonLocator.ParseOmpCheck("{\"available\":true,\"pythonPath\":\"C:\\\\Python311\\\\python.exe\",\"usingManagedEnv\":false}"));
            Assert.Equal(Py, PythonLocator.ParseOmpCheck("note\n{\"available\": true, \"pythonPath\": \"C:\\\\Python311\\\\python.exe\"}\n"));
            Assert.Null(PythonLocator.ParseOmpCheck("{\"available\":false,\"pythonPath\":\"x\"}"));
            Assert.Null(PythonLocator.ParseOmpCheck("{\"available\":true}"));
            Assert.Null(PythonLocator.ParseOmpCheck("not json"));
            Assert.Null(PythonLocator.ParseOmpCheck(null));
        }

        [Fact]
        public void Info_output_needs_an_absolute_path_and_a_version()
        {
            var info = PythonLocator.ParseInfo("C:\\Python311\\python.exe\r\n3.11.2\r\n", "omp");
            Assert.NotNull(info);
            Assert.Equal(new Version(3, 11, 2), info!.Version);
            Assert.True(info.IsSupported);
            Assert.False(PythonLocator.ParseInfo("python\n3.11.2", "x") is not null);   // 상대 경로
            Assert.Null(PythonLocator.ParseInfo("C:\\p\\python.exe\nnot-a-version", "x"));
            Assert.False(PythonLocator.ParseInfo("C:\\p\\python.exe\n3.9.18", "x")!.IsSupported);
        }

        [Fact]
        public async Task Omp_reported_interpreter_wins_and_is_verified()
        {
            var runner = new FakeRunner();
            runner.OnRun = (exe, args) =>
                exe.EndsWith("omp.exe") ? new ProcessResult(0, "{\"available\":true,\"pythonPath\":\"C:\\\\Python311\\\\python.exe\"}")
                : exe == Py ? new ProcessResult(0, "C:\\Python311\\python.exe\n3.11.2\n")
                : new ProcessResult(9009, "");
            var result = await PythonLocator.LocateAsync(@"C:\omp\omp.exe", @"C:\work", runner, default);

            Assert.True(result.Found);
            Assert.Equal("omp", result.Interpreter!.Source);
            Assert.Equal(new[] { "setup", "python", "--check", "--json" }, runner.Calls[0].Args);
            Assert.Equal(@"C:\work", runner.Calls[0].Cwd);   // omp는 작업 폴더 기준으로 .venv 등을 본다
        }

        [Fact]
        public async Task Falls_back_to_the_python_launcher_when_omp_has_none()
        {
            var runner = new FakeRunner();
            runner.OnRun = (exe, args) =>
                exe == "py" && args.Contains("-3") ? new ProcessResult(0, "C:\\Python312\\python.exe\n3.12.1\n") : new ProcessResult(9009, "");
            var result = await PythonLocator.LocateAsync(@"C:\omp\omp.exe", @"C:\work", runner, default);
            Assert.True(result.Found);
            Assert.Equal("py -3", result.Interpreter!.Source);
        }

        [Fact]
        public async Task Old_or_missing_python_is_reported_not_thrown()
        {
            var old = new FakeRunner { OnRun = (exe, _) => exe == "python" ? new ProcessResult(0, "C:\\Python39\\python.exe\n3.9.1\n") : new ProcessResult(9009, "") };
            var r1 = await PythonLocator.LocateAsync(null, @"C:\work", old, default);
            Assert.False(r1.Found);
            Assert.Contains("3.10", r1.Problem);

            var none = new FakeRunner { OnRun = (_, _) => new ProcessResult(9009, "") };
            var r2 = await PythonLocator.LocateAsync(@"C:\omp\omp.exe", @"C:\work", none, default);
            Assert.False(r2.Found);
            Assert.Null(r2.Interpreter);
            Assert.False(string.IsNullOrEmpty(r2.Problem));
        }
    }

    public class PythonLspConfigTests
    {
        private const string Server = @"C:\tools\venv\Scripts\basedpyright-langserver.exe";
        private const string Ruff = @"C:\tools\venv\Scripts\ruff.exe";
        private const string Py = @"C:\Python311\python.exe";

        [Fact]
        public void Lsp_json_uses_absolute_commands_the_interpreter_and_disables_duplicates()
        {
            using var doc = JsonDocument.Parse(PythonLspConfig.BuildLspJson(Server, Ruff, Py));
            var servers = doc.RootElement.GetProperty("servers");

            var based = servers.GetProperty("basedpyright");
            Assert.Equal(Server, based.GetProperty("command").GetString());
            Assert.Equal(new[] { "--stdio" }, based.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(new[] { ".py", ".pyi" }, based.GetProperty("fileTypes").EnumerateArray().Select(a => a.GetString()));
            Assert.Contains("pyproject.toml", based.GetProperty("rootMarkers").EnumerateArray().Select(a => a.GetString()));
            Assert.Equal(Py, based.GetProperty("settings").GetProperty("python").GetProperty("pythonPath").GetString());
            Assert.True(based.GetProperty("settings").GetProperty("basedpyright").GetProperty("analysis").GetProperty("useLibraryCodeForTypes").GetBoolean());

            var ruff = servers.GetProperty("ruff");
            Assert.Equal(Ruff, ruff.GetProperty("command").GetString());
            Assert.Equal(new[] { "server" }, ruff.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
            Assert.Contains("pyproject.toml", ruff.GetProperty("rootMarkers").EnumerateArray().Select(a => a.GetString()));
            Assert.True(ruff.GetProperty("isLinter").GetBoolean());

            foreach (string other in new[] { "pyright", "pylsp", "ty" })
                Assert.True(servers.GetProperty(other).GetProperty("disabled").GetBoolean());
            // omp는 servers 밖의 키를 서버로 읽지 않는다.
            Assert.Equal(PythonLspConfig.GeneratedBy, doc.RootElement.GetProperty("generatedBy").GetString());
        }

        [Fact]
        public void Pyproject_is_a_root_marker_for_both_servers_and_points_at_a_venv_only_when_there_is_one()
        {
            string plain = PythonLspConfig.BuildPyProject(Py);
            Assert.StartsWith(PythonLspConfig.PyProjectMarker, plain);
            Assert.Contains("[tool.pyright]", plain);
            Assert.Contains("[tool.ruff]", plain);
            Assert.DoesNotContain("venvPath", plain);

            using var tmp = new TempFolder();
            string venv = tmp.Combine("envs", "analysis");
            Directory.CreateDirectory(Path.Combine(venv, "Scripts"));
            File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), "home = x");
            string withVenv = PythonLspConfig.BuildPyProject(Path.Combine(venv, "Scripts", "python.exe"));
            Assert.Contains($"venvPath = '{tmp.Combine("envs")}'", withVenv);
            Assert.Contains("venv = 'analysis'", withVenv);
        }

        [Fact]
        public void Write_creates_both_files_leaves_unchanged_files_alone_and_never_overwrites_user_files()
        {
            using var tmp = new TempFolder();
            var first = PythonLspConfig.Write(tmp.Path, Server, Ruff, Py);
            Assert.True(first.LspWritten && first.ProjectWritten && first.Changed);
            string lsp = PythonLspConfig.LspJsonPath(tmp.Path), proj = PythonLspConfig.PyProjectPath(tmp.Path);
            Assert.True(File.Exists(lsp) && File.Exists(proj));

            var stamp = File.GetLastWriteTimeUtc(lsp);
            var again = PythonLspConfig.Write(tmp.Path, Server, Ruff, Py);
            Assert.False(again.Changed);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(lsp));

            // 앱이 만든 파일은 새 경로로 갱신된다.
            var moved = PythonLspConfig.Write(tmp.Path, @"D:\other\basedpyright-langserver.exe", Ruff, Py);
            Assert.True(moved.LspWritten);
            Assert.Contains("D:\\\\other", File.ReadAllText(lsp));

            // 사용자가 직접 만든 파일은 그대로 둔다.
            File.WriteAllText(lsp, "{\"servers\":{}}");
            File.WriteAllText(proj, "[project]\nname='mine'\n");
            var kept = PythonLspConfig.Write(tmp.Path, Server, Ruff, Py);
            Assert.True(kept.LspKeptUserFile && kept.ProjectKeptUserFile);
            Assert.False(kept.Changed);
            Assert.Equal("{\"servers\":{}}", File.ReadAllText(lsp));
            Assert.Equal("[project]\nname='mine'\n", File.ReadAllText(proj));
        }
    }

    public class PythonToolsEnvironmentTests
    {
        private static readonly PythonInterpreter Base = new(@"C:\Python311\python.exe", new Version(3, 11, 2), "omp");

        private static FakeRunner SimulatedPip(string root, Func<string[], ProcessResult?>? intercept = null)
        {
            var env = new PythonToolsEnvironment(root, new FakeRunner());
            var runner = new FakeRunner();
            runner.OnRun = (exe, args) =>
            {
                var hooked = intercept?.Invoke(args);
                if (hooked != null) return hooked;
                if (args.Length >= 3 && args[0] == "-m" && args[1] == "venv")
                {
                    Directory.CreateDirectory(env.ScriptsDir);
                    File.WriteAllText(env.VenvPython, "");
                    return new ProcessResult(0, "");
                }
                if (args.Length >= 3 && args[0] == "-m" && args[1] == "pip")
                {
                    File.WriteAllText(env.LangServerPath, "");
                    File.WriteAllText(env.BasedPyrightCliPath, "");
                    File.WriteAllText(env.RuffPath, "");
                    return new ProcessResult(0, "Collecting basedpyright==1.40.2\nSuccessfully installed basedpyright-1.40.2 ruff-0.16.10\n");
                }
                if (exe == env.RuffPath) return new ProcessResult(0, "ruff 0.16.10\n");
                if (exe == env.BasedPyrightCliPath) return new ProcessResult(0, "basedpyright 1.40.2\n");
                return new ProcessResult(1, "unexpected");
            };
            return runner;
        }

        [Fact]
        public async Task First_run_creates_the_venv_installs_pinned_versions_verifies_and_writes_the_marker()
        {
            using var tmp = new TempFolder();
            string root = tmp.Combine("python-tools");
            var runner = SimulatedPip(root);
            var env = new PythonToolsEnvironment(root, runner);
            var messages = new List<string>();

            Assert.Equal(PythonToolsState.Missing, env.Inspect());
            var result = await env.EnsureAsync(Base, messages.Add, default);

            Assert.True(result.Ok, result.Message);
            Assert.True(result.Installed);
            Assert.Equal(env.LangServerPath, result.LangServer);
            Assert.Equal(env.RuffPath, result.Ruff);
            Assert.Equal(PythonToolsState.Ready, env.Inspect());
            Assert.Equal(2, messages.Count);

            // 명령: 기반 인터프리터로 venv, venv의 python으로 pip(버전 고정), 두 실행 파일 확인.
            Assert.Equal(Base.Path, runner.Calls[0].Exe);
            Assert.Equal(new[] { "-m", "venv", env.VenvDir }, runner.Calls[0].Args);
            Assert.Equal(env.VenvPython, runner.Calls[1].Exe);
            Assert.Equal(PythonToolsEnvironment.PipInstallArguments(), runner.Calls[1].Args);
            Assert.Contains("basedpyright==" + PythonToolsEnvironment.BasedPyrightVersion, runner.Calls[1].Args);
            Assert.Contains("ruff==" + PythonToolsEnvironment.RuffVersion, runner.Calls[1].Args);
            Assert.Equal(new[] { env.RuffPath, env.BasedPyrightCliPath }, runner.Calls.Skip(2).Select(c => c.Exe));
            Assert.Equal(4, runner.Calls.Count);

            using var marker = JsonDocument.Parse(File.ReadAllText(env.MarkerPath));
            Assert.Equal(PythonToolsEnvironment.SchemaVersion, marker.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal(PythonToolsEnvironment.RuffVersion, marker.RootElement.GetProperty("ruff").GetString());
            Assert.Equal(Base.Path, marker.RootElement.GetProperty("basePython").GetString());
        }

        [Fact]
        public async Task A_ready_environment_is_reused_without_starting_any_process()
        {
            using var tmp = new TempFolder();
            string root = tmp.Combine("python-tools");
            var runner = SimulatedPip(root);
            var env = new PythonToolsEnvironment(root, runner);
            Assert.True((await env.EnsureAsync(Base, null, default)).Ok);
            runner.Calls.Clear();

            var again = await env.EnsureAsync(Base, null, default);
            Assert.True(again.Ok);
            Assert.False(again.Installed);
            Assert.Empty(runner.Calls);
        }

        [Theory]
        [InlineData("outdated")]
        [InlineData("deleted-exe")]
        public async Task Outdated_or_damaged_environments_are_rebuilt(string kind)
        {
            using var tmp = new TempFolder();
            string root = tmp.Combine("python-tools");
            var runner = SimulatedPip(root);
            var env = new PythonToolsEnvironment(root, runner);
            Assert.True((await env.EnsureAsync(Base, null, default)).Ok);
            File.WriteAllText(Path.Combine(env.VenvDir, "stale.txt"), "old venv");

            if (kind == "outdated") File.WriteAllText(env.MarkerPath, "{\"schema\":1,\"basedpyright\":\"0.0.1\",\"ruff\":\"0.0.1\"}");
            else File.Delete(env.RuffPath);
            Assert.Equal(kind == "outdated" ? PythonToolsState.Outdated : PythonToolsState.Broken, env.Inspect());

            runner.Calls.Clear();
            var messages = new List<string>();
            var rebuilt = await env.EnsureAsync(Base, messages.Add, default);

            Assert.True(rebuilt.Ok, rebuilt.Message);
            Assert.True(rebuilt.Installed);
            Assert.False(File.Exists(Path.Combine(env.VenvDir, "stale.txt")));   // 지우고 다시 만들었다
            Assert.StartsWith("Repairing", messages[0]);
            Assert.Equal(PythonToolsState.Ready, env.Inspect());
        }

        [Fact]
        public async Task An_offline_pip_failure_is_reported_honestly_and_leaves_no_marker_so_the_next_call_retries()
        {
            using var tmp = new TempFolder();
            string root = tmp.Combine("python-tools");
            bool offline = true;
            var runner = SimulatedPip(root, args =>
                offline && args.Length > 1 && args[1] == "pip"
                    ? new ProcessResult(1, "WARNING: Retrying (Retry(total=4)) after connection broken by 'NewConnectionError: Failed to establish a new connection: getaddrinfo failed'\nERROR: No matching distribution found for basedpyright==1.40.2\n")
                    : null);
            var env = new PythonToolsEnvironment(root, runner);

            var failed = await env.EnsureAsync(Base, null, default);
            Assert.False(failed.Ok);
            Assert.True(failed.Offline);
            Assert.Contains("pip install failed", failed.Message);
            Assert.Contains("No matching distribution", failed.Message);
            Assert.False(File.Exists(env.MarkerPath));
            Assert.NotEqual(PythonToolsState.Ready, env.Inspect());

            offline = false;                                   // 연결이 돌아오면 같은 호출이 복구한다
            var retried = await env.EnsureAsync(Base, null, default);
            Assert.True(retried.Ok, retried.Message);
            Assert.Equal(PythonToolsState.Ready, env.Inspect());
        }

        [Fact]
        public async Task A_tool_that_does_not_run_after_install_fails_the_setup()
        {
            using var tmp = new TempFolder();
            string root = tmp.Combine("python-tools");
            var runner = SimulatedPip(root);
            var env = new PythonToolsEnvironment(root, runner);
            var inner = runner.OnRun;
            runner.OnRun = (exe, args) => exe == env.RuffPath ? new ProcessResult(1, "The code execution cannot proceed") : inner(exe, args);

            var result = await env.EnsureAsync(Base, null, default);
            Assert.False(result.Ok);
            Assert.Contains("ruff does not run", result.Message);
            Assert.False(File.Exists(env.MarkerPath));
        }

        [Fact]
        public async Task A_failing_venv_step_stops_before_pip()
        {
            using var tmp = new TempFolder();
            var runner = new FakeRunner { OnRun = (_, _) => new ProcessResult(1, "No module named venv\n") };
            var env = new PythonToolsEnvironment(tmp.Combine("pt"), runner);
            var result = await env.EnsureAsync(Base, null, default);
            Assert.False(result.Ok);
            Assert.Contains("venv failed", result.Message);
            Assert.Single(runner.Calls);
        }
    }

    public class EvalApprovalMemoryTests
    {
        [Theory]
        [InlineData("Allow tool: eval\nLanguage: python\nCode:\nprint(1)", true)]
        [InlineData("Allow tool: eval\nLanguage: py\nCode:\nprint(1)", true)]
        [InlineData("Allow tool: EVAL\nLanguage: Python\nCode:\nx", true)]
        [InlineData("Allow tool: eval\nLanguage: javascript\nCode:\n1", false)]
        [InlineData("Allow tool: eval\nCode:\nno language", false)]
        [InlineData("Allow tool: bash\nLanguage: python\nrm x", false)]
        [InlineData("Allow tool: evaluate\nLanguage: python", false)]
        [InlineData("Something else", false)]
        public void Only_python_eval_prompts_are_recognised(string title, bool expected) =>
            Assert.Equal(expected, OmpApprovalPrompt.IsPythonEval(title));

        [Fact]
        public void Memory_state_machine()
        {
            var m = new EvalApprovalMemory();
            Assert.False(m.Approved);
            m.Remember();
            Assert.True(m.Approved);
            Assert.False(m.NoticePosted);
            m.MarkNoticePosted();
            Assert.True(m.NoticePosted);
            m.Remember();                       // 새 승인이면 알림을 다시 한 번
            Assert.False(m.NoticePosted);

            m.ObserveSessionFile("a.jsonl");
            Assert.True(m.Approved);            // 첫 세션 파일을 알게 되는 것만으로는 지우지 않는다
            m.ObserveSessionFile("A.JSONL");
            Assert.True(m.Approved);            // 같은 파일
            m.ObserveSessionFile("");
            Assert.True(m.Approved);            // 모르는 값은 무시
            m.ObserveSessionFile("b.jsonl");
            Assert.False(m.Approved);           // 다른 세션 → 다시 묻는다

            m.Remember();
            m.Reset();
            Assert.False(m.Approved);
        }
    }

    public class AgentLocalPythonSettingsTests
    {
        [Fact]
        public void Local_python_is_off_by_default_in_settings_and_host_options()
        {
            Assert.False(new AppSettings().AgentAllowLocalPython);
            Assert.False(new AgentHostOptions().AllowLocalPython);
        }

        [Fact]
        public void Setting_round_trips_through_json_and_old_files_default_to_off()
        {
            var on = new AppSettings { AgentAllowLocalPython = true, AgentDataPolicy = "RowsAllowed" };
            var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(on))!;
            Assert.True(back.AgentAllowLocalPython);
            Assert.Equal("RowsAllowed", back.AgentDataPolicy);

            var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"Dark\",\"AgentMaxRows\":50}")!;
            Assert.False(legacy.AgentAllowLocalPython);
            Assert.Equal(50, legacy.AgentMaxRows);
        }
    }

    public class PythonGuideTests
    {
        private static PythonGuideContext Context(AgentDataPolicy policy, LspStatus lsp = LspStatus.Ready, bool interpreter = true) =>
            new(AgentWorkspaceContext.ForFile(@"C:\data\sales.csv"), @"C:\data\sales_분석결과",
                interpreter ? new PythonInterpreter(@"C:\Python311\python.exe", new Version(3, 11, 2), "omp") : null,
                new[] { "pandas", "numpy" }, lsp, policy);

        [Fact]
        public void Guide_names_the_file_the_folder_the_tools_and_the_interpreter()
        {
            string g = PythonGuide.Build(Context(AgentDataPolicy.RowsAllowed));
            Assert.Contains(@"C:\data\sales.csv", g);
            Assert.Contains(@"C:\data\sales_분석결과", g);
            Assert.Contains("csv.export_view", g);
            Assert.Contains("csv.show_markdown", g);
            Assert.Contains("csv.show_image", g);
            Assert.Contains("Python 3.11.2", g);
            Assert.Contains("pandas, numpy", g);
            Assert.Contains("`lsp` tool", g);
        }

        [Fact]
        public void Guide_lists_every_open_table_with_its_path_the_workspace_file_and_the_active_tab_and_warns_it_is_a_snapshot()
        {
            var ws = new AgentWorkspaceContext(@"C:\proj\survey.ncvws", new[]
            {
                new AgentTableEntry("sales.csv", @"C:\data\sales.csv", AgentTableEntry.KindFile, false),
                new AgentTableEntry("book [Sheet2]", @"C:\data\book.xlsx", AgentTableEntry.KindSheet, true),
                new AgentTableEntry("joined", null, AgentTableEntry.KindView, false),
                new AgentTableEntry("Query 1", null, AgentTableEntry.KindResult, false),
            });
            string g = PythonGuide.Build(new PythonGuideContext(ws, @"C:\proj\survey_분석결과", null, Array.Empty<string>(), LspStatus.Unavailable, AgentDataPolicy.SummaryOnly));

            Assert.Contains(@"`C:\proj\survey.ncvws`", g);
            Assert.Contains(@"`sales.csv` (file) — `C:\data\sales.csv`", g);
            Assert.Contains(@"`book [Sheet2]` (workbook sheet) — `C:\data\book.xlsx` — **active tab**", g);
            Assert.Contains("`joined` (view table", g);
            Assert.Contains("`Query 1` (query result", g);
            Assert.DoesNotContain(@"`C:\data\sales.csv` — **active tab**", g);
            Assert.Contains("snapshot", g);
            Assert.Contains("stays the same while the user switches tabs", g);
            Assert.Contains(@"data\<table name>.csv", g);

            string empty = PythonGuide.Build(new PythonGuideContext(AgentWorkspaceContext.Empty, @"C:\x", null, Array.Empty<string>(), LspStatus.Unavailable, AgentDataPolicy.SummaryOnly));
            Assert.Contains("(none open)", empty);
            Assert.Contains("(not saved yet)", empty);
        }

        [Fact]
        public void Summary_only_tells_the_model_to_print_aggregates_only_and_admits_it_cannot_be_enforced()
        {
            string g = PythonGuide.Build(Context(AgentDataPolicy.SummaryOnly));
            Assert.Contains("summary only", g);
            Assert.Contains("cannot enforce", g);
            Assert.Contains("print(df)", g);
            Assert.DoesNotContain("lets row values reach you", g);
            Assert.Contains("approval card", PythonGuide.Build(Context(AgentDataPolicy.RowsWithApproval)));
        }

        [Fact]
        public void Diagnostics_and_missing_interpreter_are_stated_honestly()
        {
            Assert.Contains("still being set up", PythonGuide.Build(Context(AgentDataPolicy.SummaryOnly, LspStatus.Preparing)));
            Assert.Contains("not available", PythonGuide.Build(Context(AgentDataPolicy.SummaryOnly, LspStatus.Unavailable)));
            string none = PythonGuide.Build(Context(AgentDataPolicy.SummaryOnly, LspStatus.Unavailable, interpreter: false));
            Assert.Contains("none was found", none);
            Assert.DoesNotContain("Installed:", none);
        }
    }

    public class ViewerSupportTests
    {
        [Fact]
        public void Image_refs_keep_only_relative_picture_paths_under_the_folder()
        {
            const string md = "![a](fig1.png) ![b](sub/dir/fig%202.JPG) ![c](https://x.com/a.png) ![d](/abs.png) ![e](C:\\x\\a.png) " +
                              "![f](../up.png) ![g](notes.txt) ![h](fig1.png) ![i](.\\win\\chart.svg) [link](page.md)";
            Assert.Equal(new[] { "fig1.png", "sub/dir/fig 2.JPG", "win/chart.svg" }, ViewerSupport.ImageRefs(md));
        }

        [Fact]
        public void Copying_referenced_pictures_keeps_relative_paths_and_never_overwrites()
        {
            using var src = new TempFolder();
            using var dst = new TempFolder();
            Directory.CreateDirectory(src.Combine("figs"));
            File.WriteAllText(src.Combine("figs", "a.png"), "A");
            File.WriteAllText(src.Combine("b.png"), "B");
            Directory.CreateDirectory(dst.Combine("figs"));
            File.WriteAllText(dst.Combine("figs", "a.png"), "existing");

            var (copied, missing) = ViewerSupport.CopyReferencedImages("![](figs/a.png) ![](b.png) ![](gone.png)", src.Path, dst.Path);
            Assert.Equal(1, copied);
            Assert.Equal(1, missing);
            Assert.Equal("B", File.ReadAllText(dst.Combine("b.png")));
            Assert.Equal("existing", File.ReadAllText(dst.Combine("figs", "a.png")));
            Assert.Equal((0, 0), ViewerSupport.CopyReferencedImages("![](b.png)", src.Path, src.Path));
        }

        [Fact]
        public void Text_decoding_handles_boms()
        {
            Assert.Equal("가나", ViewerSupport.DecodeText(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("가나")).ToArray()));
            Assert.Equal("가나", ViewerSupport.DecodeText(System.Text.Encoding.UTF8.GetBytes("가나")));
            Assert.Equal("가나", ViewerSupport.DecodeText(new byte[] { 0xFF, 0xFE }.Concat(System.Text.Encoding.Unicode.GetBytes("가나")).ToArray()));
        }

        [Fact]
        public void Shared_read_works_while_another_writer_holds_the_file_and_honours_the_size_cap()
        {
            using var tmp = new TempFolder();
            string file = tmp.Combine("r.md");
            using (var writer = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                writer.Write("hello"u8);
                writer.Flush();
                Assert.Equal("hello", ViewerSupport.DecodeText(ViewerSupport.ReadShared(file, 100)!));
                Assert.Null(ViewerSupport.ReadShared(file, 3));
            }
        }

        [Fact]
        public void Viewers_refuse_missing_or_unsupported_files_without_opening_a_window()
        {
            using var tmp = new TempFolder();
            string txtLike = tmp.Combine("a.csv");
            File.WriteAllText(txtLike, "x");
            var palette = ThemePalette.Light;

            Assert.False(MarkdownViewerForm.ShowFile(null!, tmp.Combine("nope.md"), palette).Ok);
            var notMd = MarkdownViewerForm.ShowFile(null!, txtLike, palette);
            Assert.False(notMd.Ok);
            Assert.False(ImageViewerForm.ShowFile(null!, tmp.Combine("nope.png"), palette).Ok);
            Assert.False(ImageViewerForm.ShowFile(null!, txtLike, palette).Ok);
            Assert.False(ImageViewerForm.ShowFile(null!, tmp.Combine("nope.pdf"), palette).Ok);
        }
    }
}

namespace NanumCsvViewer.Tests
{
    internal sealed class FakePythonSetup : IPythonSetup
    {
        public static readonly PythonInterpreter Interpreter = new(@"C:\Python311\python.exe", new Version(3, 11, 2), "omp");
        public static readonly PythonToolsResult ReadyTools = new(true, "ready", @"C:\tools\venv\Scripts\basedpyright-langserver.exe", @"C:\tools\venv\Scripts\ruff.exe");

        public PythonLocateResult Locate { get; set; } = new(Interpreter, null);
        public IReadOnlyList<string> Packages { get; set; } = new[] { "pandas", "numpy", "matplotlib" };
        public PythonToolsResult? Ready { get; set; }
        public Func<Task<PythonToolsResult>>? Ensure { get; set; }
        public int EnsureCalls;
        public List<string?> LocateCwds { get; } = new();

        public Task<PythonLocateResult> LocateAsync(string? ompExe, string workingDirectory, CancellationToken ct)
        {
            lock (LocateCwds) LocateCwds.Add(workingDirectory);
            return Task.FromResult(Locate);
        }

        public Task<IReadOnlyList<string>> PackagesAsync(PythonInterpreter python, CancellationToken ct) => Task.FromResult(Packages);
        PythonToolsResult? IPythonSetup.ReadyTools() => Ready;

        public Task<PythonToolsResult> EnsureToolsAsync(PythonInterpreter python, Action<string>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref EnsureCalls);
            progress?.Invoke("Creating the Python tools environment (one-time)…");
            return Ensure!();
        }

        public PythonLspConfig.WriteResult WriteConfig(string outputFolder, PythonToolsResult tools, PythonInterpreter python) =>
            PythonLspConfig.Write(outputFolder, tools.LangServer!, tools.Ruff!, python.Path);
    }

    public class ChatControllerLocalPythonTests
    {
        private static AgentHostOptions On(AgentDataPolicy policy = AgentDataPolicy.SummaryOnly) =>
            new(Language: "en", AppVersion: "1.2.3", AllowLocalPython: true, DataPolicy: policy);

        private static string Guide(FakeOmpProcess p)
        {
            var args = p.Launch.Arguments.ToList();
            return File.ReadAllText(args[args.IndexOf("--append-system-prompt") + 1]);
        }

        private static string Cwd(FakeOmpProcess p)
        {
            var args = p.Launch.Arguments.ToList();
            return args[args.IndexOf("--cwd") + 1];
        }

        private static async Task SettledAsync(ControllerRig rig, int processes)
        {
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count >= processes);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            await rig.Factory.Processes[processes - 1].WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("model").Length > 0);
        }

        private static bool HasNotice(ControllerRig rig, string fragment) =>
            rig.Page.Parsed("notice").Any(n => n.Str("text").Contains(fragment, StringComparison.Ordinal));

        [Fact]
        public async Task Python_on_runs_omp_in_the_output_folder_and_adds_the_guide_section_the_config_and_image_base()
        {
            using var tmp = new TempFolder();
            string csv = tmp.Combine("sales.csv");
            string expected = tmp.Combine("sales_분석결과");
            var python = new FakePythonSetup { Ready = FakePythonSetup.ReadyTools };
            using var rig = new ControllerRig(On(), python: python, dataFile: csv);
            await rig.StartAsync();

            Assert.Equal(expected, Cwd(rig.Proc));
            Assert.Equal(expected, rig.Proc.Launch.WorkingDirectory);
            Assert.True(Directory.Exists(expected));
            Assert.Equal(expected, rig.OnUi(() => rig.Controller.OutputFolder));
            Assert.Equal(new[] { expected }, python.LocateCwds);               // omp setup python --check는 결과 폴더 기준

            string guide = Guide(rig.Proc);
            Assert.Contains("# guide", guide);
            Assert.Contains("Local Python analysis", guide);
            Assert.Contains(csv, guide);
            Assert.Contains(expected, guide);
            Assert.Contains("basedpyright and ruff are active", guide);
            Assert.True(File.Exists(Path.Combine(expected, ".omp", "lsp.json")));
            Assert.True(File.Exists(Path.Combine(expected, "pyproject.toml")));
            Assert.Equal("https://nanumcsv-out.local/", rig.Page.Parsed("imageBase").Last().Str("url"));
            Assert.Equal(expected, rig.Page.Parsed("status").Last().Str("cwd"));
        }

        [Fact]
        public async Task Python_off_keeps_the_file_folder_and_adds_nothing()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(new AgentHostOptions(Language: "en"), dataFile: tmp.Combine("sales.csv"));
            await rig.StartAsync();

            Assert.Equal(rig.WorkDir, Cwd(rig.Proc));
            Assert.DoesNotContain("Local Python", Guide(rig.Proc));
            Assert.Null(rig.OnUi(() => rig.Controller.OutputFolder));
            Assert.False(Directory.Exists(tmp.Combine("sales_분석결과")));
            Assert.Empty(rig.Page.Parsed("imageBase"));        // 꺼져 있으면 페이지에 아무것도 더 보내지 않는다
        }

        [Fact]
        public async Task Without_a_file_the_documents_fallback_folder_is_used()
        {
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools });
            await rig.StartAsync();
            Assert.Equal(AgentWorkspace.FallbackFolder(), Cwd(rig.Proc));
            Assert.Contains("(none open)", Guide(rig.Proc));
        }

        [Fact]
        public async Task Missing_python_is_reported_with_install_guidance_and_the_agent_still_starts()
        {
            using var tmp = new TempFolder();
            var python = new FakePythonSetup { Locate = new PythonLocateResult(null, "No Python interpreter was found on PATH.") };
            using var rig = new ControllerRig(On(), python: python, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();

            var notice = rig.Page.Parsed("notice").Single(n => n.Str("text").Contains("no usable Python"));
            Assert.Equal("warn", notice.Str("level"));
            Assert.Contains("Python 3.10", notice.Str("text"));
            Assert.Contains("/restart", notice.Str("text"));
            Assert.Equal("https://www.python.org/downloads/", notice.Str("url"));
            Assert.Contains("none was found", Guide(rig.Proc));
            Assert.Equal(0, python.EnsureCalls);                                 // 도구 환경은 시도하지 않는다
            Assert.False(File.Exists(Path.Combine(tmp.Combine("a_분석결과"), ".omp", "lsp.json")));
        }

        [Fact]
        public async Task Tools_prepared_in_the_background_write_the_config_then_restart_idle_omp_to_apply_it()
        {
            using var tmp = new TempFolder();
            string expected = tmp.Combine("a_분석결과");
            var done = new TaskCompletionSource<PythonToolsResult>();
            var python = new FakePythonSetup { Ensure = () => done.Task };
            using var rig = new ControllerRig(On(), python: python, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();

            Assert.Contains("still being set up", Guide(rig.Proc));
            Assert.False(File.Exists(Path.Combine(expected, ".omp", "lsp.json")));
            await rig.WaitUntilAsync(() => HasNotice(rig, "Setting up Python code diagnostics"));
            await rig.WaitUntilAsync(() => HasNotice(rig, "Creating the Python tools environment"));
            Assert.Equal(1, python.EnsureCalls);

            python.Ready = FakePythonSetup.ReadyTools;          // 준비가 끝나면 다음 시작은 바로 준비 상태로 읽는다
            done.SetResult(new PythonToolsResult(true, "ready", FakePythonSetup.ReadyTools.LangServer, FakePythonSetup.ReadyTools.Ruff, Installed: true));
            await SettledAsync(rig, 2);

            Assert.True(File.Exists(Path.Combine(expected, ".omp", "lsp.json")));
            Assert.True(HasNotice(rig, "Python code diagnostics are ready (basedpyright + ruff)."));
            Assert.True(HasNotice(rig, "picks up the Python code diagnostics"));
            Assert.Contains("basedpyright and ruff are active", Guide(rig.Factory.Processes[1]));
            // 같은 폴더의 재시작은 RPC switch_session으로 이어받는다(명령줄 --resume 없음).
            Assert.DoesNotContain("--resume", rig.Factory.Processes[1].Launch.Arguments);
            Assert.Equal("C:\\sessions\\s1.jsonl", (await rig.Factory.Processes[1].WaitForTypeAsync("switch_session")).Str("sessionPath"));
            Assert.Equal(1, python.EnsureCalls);                // 다시 시작해도 다시 만들지 않는다
            Assert.True(rig.Factory.Processes[0].Killed);
            Assert.Equal(2, rig.Factory.Processes.Count);       // 한 번만 다시 시작(반복 없음)
        }

        [Fact]
        public async Task A_failed_preparation_is_reported_with_the_internet_hint_and_does_not_restart()
        {
            using var tmp = new TempFolder();
            var done = new TaskCompletionSource<PythonToolsResult>();
            var python = new FakePythonSetup { Ensure = () => done.Task };
            using var rig = new ControllerRig(On(), python: python, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();

            done.SetResult(new PythonToolsResult(false, "pip install failed (exit code 1). No matching distribution found", Offline: true));
            await rig.WaitUntilAsync(() => HasNotice(rig, "could not be prepared"));
            var warn = rig.Page.Parsed("notice").Single(n => n.Str("text").Contains("could not be prepared"));
            Assert.Equal("warn", warn.Str("level"));
            Assert.Contains("No matching distribution", warn.Str("text"));
            Assert.Contains("internet", warn.Str("text"));
            Assert.Contains("/restart", warn.Str("text"));

            await Task.Delay(150);
            Assert.Single(rig.Factory.Processes);
            Assert.False(File.Exists(Path.Combine(tmp.Combine("a_분석결과"), ".omp", "lsp.json")));
        }

        private static AgentTableEntry Tab(string name, string? path, string kind = AgentTableEntry.KindFile, bool active = false) => new(name, path, kind, active);

        private static AgentWorkspaceContext Ctx(string? workspaceFile, params AgentTableEntry[] tabs) => new(workspaceFile, tabs);

        private static async Task StartWithTwoTabsAsync(ControllerRig rig, TempFolder tmp)
        {
            rig.OnUi(() => rig.Controller.SetWorkspaceContext(Ctx(null,
                Tab("a.csv", tmp.Combine("a.csv"), active: true), Tab("b.csv", tmp.Combine("b.csv")))));
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("model").Length > 0);
        }

        [Fact]
        public async Task Switching_opening_and_closing_tabs_or_sheets_never_restarts_omp_and_keeps_the_folder_of_the_first_data_file()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools });
            await StartWithTwoTabsAsync(rig, tmp);
            string expected = tmp.Combine("a_분석결과");
            Assert.Equal(expected, Cwd(rig.Proc));
            // 시작할 때의 가이드는 열린 탭 전부와 활성 탭을 보여 준다.
            string guide = Guide(rig.Proc);
            Assert.Contains(tmp.Combine("a.csv"), guide);
            Assert.Contains(tmp.Combine("b.csv"), guide);
            Assert.Contains("**active tab**", guide);

            string book = tmp.Combine("book.xlsx");
            var steps = new[]
            {
                Ctx(null, Tab("a.csv", tmp.Combine("a.csv")), Tab("b.csv", tmp.Combine("b.csv"), active: true)),                          // 탭 전환
                Ctx(null, Tab("a.csv", tmp.Combine("a.csv")), Tab("b.csv", tmp.Combine("b.csv")), Tab("book [S1]", book, AgentTableEntry.KindSheet, true)),  // 새 탭(워크북)
                Ctx(null, Tab("a.csv", tmp.Combine("a.csv")), Tab("b.csv", tmp.Combine("b.csv")), Tab("book [S2]", book, AgentTableEntry.KindSheet, true)),  // 시트 전환
                Ctx(null, Tab("b.csv", tmp.Combine("b.csv")), Tab("book [S2]", book, AgentTableEntry.KindSheet, true)),                   // 처음 탭 닫기
                Ctx(null, Tab("v", null, AgentTableEntry.KindView, true)),                                                               // 뷰 탭만
                Ctx(null),                                                                                                                // 모두 닫기
                Ctx(null, Tab("c.csv", tmp.Combine("sub", "c.csv"), active: true)),                                                       // 다른 폴더의 새 파일
            };
            foreach (var step in steps)
            {
                rig.OnUi(() => rig.Controller.SetWorkspaceContext(step));
                Assert.Equal(expected, rig.OnUi(() => rig.Controller.AnalysisFolder));
            }
            await Task.Delay(250);

            Assert.Single(rig.Factory.Processes);
            Assert.False(rig.Proc.Killed);
            Assert.Equal(expected, rig.OnUi(() => rig.Controller.OutputFolder));
            Assert.False(HasNotice(rig, "Switching the analysis folder"));
        }

        [Fact]
        public async Task The_first_data_file_of_an_empty_session_restarts_once_into_its_folder_on_the_same_conversation()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools });
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("model").Length > 0);
            var first = rig.Proc;
            Assert.Equal(AgentWorkspace.FallbackFolder(), Cwd(first));

            rig.OnUi(() => rig.Controller.SetWorkspaceContext(Ctx(null, Tab("a.csv", tmp.Combine("a.csv"), active: true))));
            await SettledAsync(rig, 2);

            Assert.True(first.Killed);
            Assert.Equal(tmp.Combine("a_분석결과"), Cwd(rig.Proc));
            Assert.Contains(tmp.Combine("a.csv"), Guide(rig.Proc));
            // 다른 폴더의 세션은 RPC switch_session을 omp가 거절하므로(실제 omp로 확인) 명령줄 --resume으로 이어받는다.
            var args = rig.Proc.Launch.Arguments.ToList();
            Assert.Equal("C:\\sessions\\s1.jsonl", args[args.IndexOf("--resume") + 1]);
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "switch_session");
            Assert.True(HasNotice(rig, "Switching the analysis folder"));
            Assert.Equal(tmp.Combine("a_분석결과"), rig.OnUi(() => rig.Controller.OutputFolder));

            // 이후 다른 파일을 열어도 폴더는 처음 파일 기준으로 고정.
            rig.OnUi(() => rig.Controller.SetWorkspaceContext(Ctx(null, Tab("b.csv", tmp.Combine("other", "b.csv"), active: true))));
            await Task.Delay(250);
            Assert.Equal(2, rig.Factory.Processes.Count);
        }

        [Fact]
        public async Task Saving_a_workspace_file_moves_the_analysis_folder_beside_it_and_a_busy_agent_finishes_its_turn_first()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools });
            await StartWithTwoTabsAsync(rig, tmp);
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsBusy));

            string ws = tmp.Combine("projects", "survey.ncvws");
            rig.OnUi(() => rig.Controller.SetWorkspaceContext(Ctx(ws,
                Tab("a.csv", tmp.Combine("a.csv"), active: true), Tab("b.csv", tmp.Combine("b.csv")))));
            await Task.Delay(200);
            Assert.Single(rig.Factory.Processes);               // 작업 중에는 다시 시작하지 않는다
            Assert.Equal(tmp.Combine("a_분석결과"), Cwd(rig.Proc));

            rig.Proc.Emit("{\"type\":\"agent_end\",\"isTerminal\":true}");
            await SettledAsync(rig, 2);
            string folder = tmp.Combine("projects", "survey_분석결과");
            Assert.Equal(folder, Cwd(rig.Factory.Processes[1]));
            Assert.Contains(ws, Guide(rig.Factory.Processes[1]));
            Assert.Equal(folder, rig.OnUi(() => rig.Controller.AnalysisFolder));

            // 작업 공간 파일이 있으면 탭 전환·첫 탭 닫기에도 그대로.
            rig.OnUi(() => rig.Controller.SetWorkspaceContext(Ctx(ws, Tab("b.csv", tmp.Combine("b.csv"), active: true))));
            await Task.Delay(250);
            Assert.Equal(2, rig.Factory.Processes.Count);
        }

        [Fact]
        public async Task The_same_context_or_python_off_never_restarts()
        {
            using var tmp = new TempFolder();
            using var on = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools }, dataFile: tmp.Combine("a.csv"));
            await on.StartAsync();
            on.OnUi(() => on.Controller.SetWorkspaceContext(AgentWorkspaceContext.ForFile(tmp.Combine("A.CSV"))));      // 같은 파일(대소문자만 다름)
            using var off = new ControllerRig(new AgentHostOptions(Language: "en"), dataFile: tmp.Combine("a.csv"));
            await off.StartAsync();
            off.OnUi(() => off.Controller.SetWorkspaceContext(AgentWorkspaceContext.ForFile(tmp.Combine("b.csv"), tmp.Combine("w.ncvws"))));
            await Task.Delay(200);
            Assert.Single(on.Factory.Processes);
            Assert.Single(off.Factory.Processes);
            Assert.Null(off.OnUi(() => off.Controller.AnalysisFolder));    // 꺼져 있으면 내보낼 폴더도 없다
        }

        [Fact]
        public async Task Turning_python_off_or_changing_the_policy_restarts_idle_omp_live()
        {
            using var tmp = new TempFolder();
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools }, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");

            rig.OnUi(() => rig.Controller.Options = On(AgentDataPolicy.RowsAllowed));
            await SettledAsync(rig, 2);
            Assert.Contains("lets row values reach you", Guide(rig.Proc));
            Assert.Contains("new data policy", rig.Page.Parsed("notice").Last(n => n.Str("text").Contains("Restarting")).Str("text"));

            rig.OnUi(() => rig.Controller.Options = new AgentHostOptions(Language: "en", AppVersion: "1.2.3"));
            await SettledAsync(rig, 3);
            Assert.Equal(rig.WorkDir, Cwd(rig.Proc));
            Assert.DoesNotContain("Local Python", Guide(rig.Proc));
            Assert.Null(rig.OnUi(() => rig.Controller.OutputFolder));
            Assert.Equal("", rig.Page.Parsed("imageBase").Last().Str("url"));
        }

        [Fact]
        public async Task PostImage_shows_only_picture_files_inside_the_output_folder()
        {
            using var tmp = new TempFolder();
            string expected = tmp.Combine("a_분석결과");
            using var rig = new ControllerRig(On(), python: new FakePythonSetup { Ready = FakePythonSetup.ReadyTools }, dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();
            File.WriteAllBytes(Path.Combine(expected, "fig 1.png"), new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.Combine(expected, "notes.txt"), "x");
            File.WriteAllBytes(tmp.Combine("outside.png"), new byte[] { 1 });

            Assert.True(rig.OnUi(() => rig.Controller.PostImage("fig 1.png", "Scatter plot")));
            var msg = rig.Page.Parsed("image").Single();
            Assert.Equal("https://nanumcsv-out.local/fig%201.png", msg.Str("url"));
            Assert.Equal("fig 1.png", msg.Str("name"));
            Assert.Equal(Path.Combine(expected, "fig 1.png"), msg.Str("path"));
            Assert.Equal("Scatter plot", msg.Str("caption"));

            Assert.True(rig.OnUi(() => rig.Controller.PostImage(Path.Combine(expected, "fig 1.png"))));   // 절대 경로도 폴더 안이면 허용
            Assert.False(rig.OnUi(() => rig.Controller.PostImage("notes.txt")));                           // 그림이 아님
            Assert.False(rig.OnUi(() => rig.Controller.PostImage("missing.png")));
            Assert.False(rig.OnUi(() => rig.Controller.PostImage(tmp.Combine("outside.png"))));            // 폴더 밖
            Assert.False(rig.OnUi(() => rig.Controller.PostImage("..\\outside.png")));
            Assert.Equal(2, rig.Page.Parsed("image").Count);
        }

        [Fact]
        public async Task PostImage_is_refused_when_python_is_off()
        {
            using var tmp = new TempFolder();
            File.WriteAllBytes(tmp.Combine("fig.png"), new byte[] { 1 });
            using var rig = new ControllerRig(new AgentHostOptions(Language: "en"), dataFile: tmp.Combine("a.csv"));
            await rig.StartAsync();
            Assert.False(rig.OnUi(() => rig.Controller.PostImage(tmp.Combine("fig.png"))));
            Assert.Empty(rig.Page.Parsed("image"));
        }
    }

    public class ChatControllerEvalApprovalTests
    {
        private static string EvalTitle(string language = "python", string code = "print(1+1)") =>
            $"Allow tool: eval\nLanguage: {language}\nCode:\n{code}";

        private static JsonObject Select(string id, string title) => new()
        {
            ["type"] = "extension_ui_request", ["id"] = id, ["method"] = "select",
            ["title"] = title, ["options"] = new JsonArray("Approve", "Deny"),
        };

        private static async Task<ControllerRig> ConnectedAsync()
        {
            var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            return rig;
        }

        private static Task<JsonElement> Reply(FakeOmpProcess p, string id) =>
            p.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == id);

        private static async Task AnswerCardAsync(ControllerRig rig, int index, bool ok)
        {
            await rig.WaitForCountAsync("approval", index + 1);
            rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{rig.Page.Parsed("approval")[index].Str("id")}\",\"ok\":{(ok ? "true" : "false")}}}");
        }

        private static int Notices(ControllerRig rig) =>
            rig.Page.Parsed("notice").Count(n => n.Str("text") == "Python run approved (this conversation)");

        [Fact]
        public async Task The_first_python_eval_shows_a_card_and_later_ones_in_the_same_conversation_pass_silently()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit(Select("ui_1", EvalTitle()));
            var card = await rig.Page.WaitForTypeAsync("approval");
            Assert.Equal("Allow tool: eval", card.Str("target"));
            Assert.Contains("Language: python", card.Child("lines").Items().Select(l => l.Str("t")));
            Assert.Contains("later Python runs", card.Str("summary"));       // 승인이 이후 실행까지 허용함을 카드에서 알린다
            await AnswerCardAsync(rig, 0, true);
            Assert.Equal("Approve", (await Reply(rig.Proc, "ui_1")).Str("value"));
            Assert.Equal(0, Notices(rig));

            rig.Proc.Emit(Select("ui_2", EvalTitle(code: "print(2)")));
            Assert.Equal("Approve", (await Reply(rig.Proc, "ui_2")).Str("value"));
            rig.Proc.Emit(Select("ui_3", EvalTitle(code: "print(3)")));
            Assert.Equal("Approve", (await Reply(rig.Proc, "ui_3")).Str("value"));

            Assert.Single(rig.Page.Parsed("approval"));                       // 카드는 처음 한 번뿐
            Assert.Equal(1, Notices(rig));                                    // 알림도 한 번
        }

        [Fact]
        public async Task A_denied_first_eval_is_not_remembered()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit(Select("ui_1", EvalTitle()));
            await AnswerCardAsync(rig, 0, false);
            Assert.Equal("Deny", (await Reply(rig.Proc, "ui_1")).Str("value"));

            rig.Proc.Emit(Select("ui_2", EvalTitle()));
            await rig.WaitForCountAsync("approval", 2);                       // 다시 카드
            Assert.Equal(0, Notices(rig));
        }

        [Fact]
        public async Task Other_tools_and_javascript_eval_keep_asking_every_time()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit(Select("ui_1", EvalTitle()));
            await AnswerCardAsync(rig, 0, true);
            await Reply(rig.Proc, "ui_1");

            rig.Proc.Emit(Select("ui_b", "Allow tool: bash\nrm -rf data"));
            await AnswerCardAsync(rig, 1, true);
            await Reply(rig.Proc, "ui_b");
            rig.Proc.Emit(Select("ui_b2", "Allow tool: bash\nrm -rf data"));
            await rig.WaitForCountAsync("approval", 3);                       // bash는 매번

            rig.Proc.Emit(Select("ui_js", EvalTitle("javascript", "1+1")));
            await rig.WaitForCountAsync("approval", 4);                       // JS eval은 해당 없음
            Assert.Equal(0, Notices(rig));
        }

        [Fact]
        public async Task A_new_conversation_asks_again()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit(Select("ui_1", EvalTitle()));
            await AnswerCardAsync(rig, 0, true);
            await Reply(rig.Proc, "ui_1");
            rig.Proc.Emit(Select("ui_2", EvalTitle()));
            await Reply(rig.Proc, "ui_2");
            Assert.Single(rig.Page.Parsed("approval"));

            rig.FromPage("{\"t\":\"newSession\"}");                           // FakeDialogs가 확인에 예
            await rig.Page.WaitForTypeAsync("clear");
            rig.Proc.Emit(Select("ui_3", EvalTitle()));
            await rig.WaitForCountAsync("approval", 2);
        }

        [Fact]
        public async Task Restarting_omp_asks_again()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit(Select("ui_1", EvalTitle()));
            await AnswerCardAsync(rig, 0, true);
            await Reply(rig.Proc, "ui_1");

            rig.OnUi(() => _ = rig.Controller.RestartAsync());
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            rig.Factory.Processes[1].Emit(Select("ui_2", EvalTitle()));
            await rig.WaitForCountAsync("approval", 2);
        }
    }
}

namespace NanumCsvViewer.Tests
{
    public class ChatControllerResumeTests
    {
        [Fact]
        public async Task The_agent_is_connected_only_after_the_session_resume_finished_and_a_cancelled_resume_is_reported()
        {
            using var rig = new ControllerRig(configure: p => p.Handlers["switch_session"] = _ => "");   // 응답은 테스트가 직접 보낸다
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("model").Length > 0);   // 세션 파일을 알게 됨

            rig.OnUi(() => _ = rig.Controller.RestartAsync());
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            var second = rig.Factory.Last;
            var resume = await second.WaitForTypeAsync("switch_session");

            // 이어받는 중에 입력을 받으면 omp가 전환을 취소한다(실제 omp로 확인): 연결됨으로 보지 않는다.
            Assert.False(rig.OnUi(() => rig.Controller.IsRunning));
            Assert.False(rig.OnUi(() => rig.Controller.Submit("hello")));
            Assert.DoesNotContain(second.ReceivedSnapshot(), f => f.Str("type") == "prompt");

            second.Emit(FakeOmpProcess.Ok(resume.Str("id"), "switch_session", new JsonObject { ["cancelled"] = true }));
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("level") == "warn" && n.Str("text").Contains("did not resume"));
            Assert.True(rig.OnUi(() => rig.Controller.Submit("hello")));
        }
    }
}
