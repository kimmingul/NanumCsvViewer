using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Python;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    /// <summary>컨트롤러 + 가짜 omp + 가짜 페이지를 한 묶음으로. 컨트롤러는 UI 스레드(TestUi)에서만 만진다.</summary>
    internal sealed class ControllerRig : IDisposable
    {
        public TestUi Ui { get; } = new();
        public FakePage Page { get; } = new();
        public FakeTools Tools { get; } = new();
        public FakeDialogs Dialogs { get; } = new();
        public ManualClock Clock { get; } = new();
        public FakeOmpFactory Factory { get; } = new();
        public CapturingLog Log { get; } = new();
        public ChatController Controller { get; }
        public string WorkDir { get; } = Path.GetTempPath();
        public string VersionText { get; set; } = "omp/18.4.4";
        public string? OmpPath { get; set; } = "C:\\fake\\omp.exe";
        public List<JsonElement> Unhandled { get; } = new();
        /// <summary>`omp usage` 같은 CLI 호출 가짜. 기본은 실패(null).</summary>
        public Func<string, IReadOnlyList<string>, string, CancellationToken, Task<string?>> Cli { get; set; } =
            (_, _, _, _) => Task.FromResult<string?>(null);

        public ControllerRig(AgentHostOptions? options = null, Action<FakeOmpProcess>? configure = null, IPythonSetup? python = null, string? dataFile = null)
        {
            Factory.Configure = configure;
            var services = new ChatControllerServices
            {
                ProcessFactory = Factory,
                Clock = Clock,
                Dialogs = Dialogs,
                Log = Log,
                Ui = Ui,
                AutoTick = false,
                LocateOmp = _ => OmpPath,
                RunVersion = (_, _) => Task.FromResult<string?>(VersionText),
                ReadGuide = () => "# guide",
                RunOmpCli = (exe, args, cwd, ct) => Cli(exe, args, cwd, ct),
                LocalPython = python ?? new FakePythonSetup(),
            };
            Controller = new ChatController(Page, Tools, options ?? new AgentHostOptions(Language: "en", AppVersion: "1.2.3"), services);
            Controller.PageMessageUnhandled += e => { lock (Unhandled) Unhandled.Add(e); };
            if (dataFile != null) Ui.Invoke(() => Controller.SetDataFile(dataFile));
        }

        public FakeOmpProcess Proc => Factory.Last;

        public async Task StartAsync()
        {
            await Ui.InvokeAsync(() => Controller.StartAsync(WorkDir));
            await WaitUntilAsync(() => Ui.Invoke(() => Controller.IsRunning));
        }

        public void OnUi(Action a) => Ui.Invoke(a);
        public T OnUi<T>(Func<T> f) => Ui.Invoke(f);

        public void FromPage(string json) => Page.Raise(Ui, json);

        public Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000) => OmpRpcClientTests.WaitUntilAsync(condition, timeoutMs);

        /// <summary>페이지 메시지 중 종류 t가 count개 이상 나올 때까지.</summary>
        public Task WaitForCountAsync(string t, int count) => WaitUntilAsync(() => Page.Parsed(t).Count >= count);

        public void Dispose()
        {
            try { Ui.Invoke(() => Controller.Dispose()); } catch { }
            Ui.Dispose();
        }
    }

    public class ChatControllerHandshakeTests
    {
        [Fact]
        public async Task Handshake_runs_in_the_documented_order_and_registers_the_csv_tools()
        {
            using var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");

            var types = rig.Proc.ReceivedSnapshot().Select(f => f.Str("type")).ToList();
            Assert.Equal(new[]
            {
                "negotiate_protocol", "set_host_tools", "get_state", "get_available_commands",
                "get_available_models", "get_available_thinking_levels",
            }, types.Take(6));

            var setTools = rig.Proc.ReceivedSnapshot().First(f => f.Str("type") == "set_host_tools");
            Assert.Equal("csv.test", setTools.Child("tools").Items().Single().Str("name"));
            Assert.Equal("object", setTools.Child("tools").Items().Single().Child("parameters").Str("type"));
            // 모든 요청은 고유한 req-N id를 가진다.
            var ids = rig.Proc.ReceivedSnapshot().Select(f => f.Str("id")).ToList();
            Assert.All(ids, id => Assert.StartsWith("req-", id));
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        [Fact]
        public async Task Launch_uses_rpc_ui_mode_the_working_directory_the_host_config_and_the_guide()
        {
            using var rig = new ControllerRig(new AgentHostOptions(ExtraArgs: "--model a/b", Language: "en"));
            await rig.StartAsync();

            var info = rig.Proc.Launch;
            Assert.Equal("C:\\fake\\omp.exe", info.ExePath);
            Assert.Equal(rig.WorkDir, info.WorkingDirectory);
            var args = info.Arguments;
            Assert.Equal(new[] { "--mode", "rpc-ui", "--cwd", rig.WorkDir }, args.Take(4));
            int cfg = args.ToList().IndexOf("--config");
            Assert.True(File.Exists(args[cfg + 1]));
            Assert.Equal(OmpLaunch.HostConfigJson(AgentApprovalPolicy.Default), File.ReadAllText(args[cfg + 1]));
            int guide = args.ToList().IndexOf("--append-system-prompt");
            Assert.Contains("# guide", File.ReadAllText(args[guide + 1]));
            Assert.Equal(new[] { "--model", "a/b" }, args.Skip(args.Count - 2));
            Assert.Contains($"omp.stderr-p{Environment.ProcessId}", info.StderrLogPath);
        }

        [Fact]
        public async Task After_the_handshake_the_page_gets_connected_status_catalog_and_merged_commands()
        {
            using var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.WaitForCountAsync("catalog", 2); // 모델 + 생각 수준(+ 로그인 제공자)

            var status = rig.Page.Parsed("status").Last();
            Assert.True(status.Bool("connected"));
            Assert.False(status.Bool("busy"));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("model") == "anthropic/claude-a");
            var last = rig.Page.Parsed("status").Last();
            Assert.Equal("low", last.Str("thinking"));
            Assert.Equal(12.5, last.Num("context"));

            var catalog = rig.Page.Parsed("catalog").Last();
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Last().Child("levels").Strings().Count == 3);
            catalog = rig.Page.Parsed("catalog").Last();
            Assert.Equal(new[] { "anthropic/claude-a", "openai/gpt-x" }, catalog.Child("models").Strings());
            Assert.Equal(new[] { "off", "low", "high" }, catalog.Child("levels").Strings());

            var commands = rig.Page.Parsed("commands").Last().Child("items").Items().Select(c => c.Str("name")).ToList();
            Assert.Contains("compact", commands);   // omp 목록
            Assert.Contains("new", commands);       // 앱 자체 명령
            Assert.Contains("hint", rig.Page.Snapshot().First(s => s.Contains("\"compact\"")));
        }

        [Fact]
        public async Task Missing_omp_reports_an_error_status_and_starts_nothing()
        {
            using var rig = new ControllerRig();
            rig.OmpPath = null;
            var summaries = new List<string>();
            rig.Controller.StatusChanged += summaries.Add;
            await rig.Ui.InvokeAsync(() => rig.Controller.StartAsync(rig.WorkDir));

            Assert.Empty(rig.Factory.Processes);
            Assert.False(rig.OnUi(() => rig.Controller.IsRunning));
            var status = rig.Page.Parsed("status").Last();
            Assert.True(status.Bool("error"));
            Assert.Contains("not found", status.Str("state"));
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("level") == "error");
            Assert.Contains(summaries, s => s.Contains("not found"));
        }

        [Fact]
        public async Task Too_old_omp_is_refused_before_launch()
        {
            using var rig = new ControllerRig();
            rig.VersionText = "omp/18.4.3";
            await rig.Ui.InvokeAsync(() => rig.Controller.StartAsync(rig.WorkDir));
            Assert.Empty(rig.Factory.Processes);
            Assert.Contains("too old", rig.Page.Parsed("status").Last().Str("state"));
        }

        [Fact]
        public async Task Rejected_host_tools_fail_the_start_and_stop_the_child()
        {
            using var rig = new ControllerRig(configure: p =>
                p.Handlers["set_host_tools"] = cmd => FakeOmpProcess.Fail(cmd.Str("id"), "set_host_tools", "nope"));
            await rig.Ui.InvokeAsync(() => rig.Controller.StartAsync(rig.WorkDir));
            await rig.WaitUntilAsync(() => rig.Proc.Killed);
            Assert.False(rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Contains("nope", rig.Page.Parsed("status").Last().Str("state"));
        }

        [Fact]
        public async Task Stop_closes_omp_and_marks_the_panel_disconnected()
        {
            using var rig = new ControllerRig();
            await rig.StartAsync();
            rig.OnUi(() => rig.Controller.Stop());
            await rig.Proc.Exited.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(rig.OnUi(() => rig.Controller.IsRunning));
            await rig.WaitUntilAsync(() => !rig.Page.Parsed("status").Last().Bool("connected").GetValueOrDefault());
        }
    }

    public class ChatControllerEventTests
    {
        private static async Task<ControllerRig> ConnectedAsync()
        {
            var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitForCountAsync("catalog", 2);
            return rig;
        }

        [Fact]
        public async Task A_turn_maps_to_the_golden_page_messages()
        {
            using var rig = await ConnectedAsync();
            var p = rig.Proc;
            long start = rig.Clock.UnixMs;

            p.Emit("{\"type\":\"agent_start\"}");
            p.Emit("{\"type\":\"message_start\",\"message\":{\"role\":\"assistant\",\"provider\":\"anthropic\",\"model\":\"claude-a\"}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"thinking_start\"}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"thinking_delta\",\"delta\":\"Hmm\"}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"thinking_end\"}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Hello \"}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"world\"}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_end\"}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"toolcall_start\",\"toolCall\":{\"id\":\"t1\",\"name\":\"csv.info\"}}}");
            p.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"toolcall_delta\",\"delta\":\"{\\\"i\\\":\\\"look\\\"}\",\"toolCall\":{\"id\":\"t1\",\"name\":\"csv.info\"}}}");
            p.Emit("{\"type\":\"tool_execution_start\",\"toolCallId\":\"t1\",\"toolName\":\"csv.info\",\"args\":{\"i\":\"look\"},\"intent\":\"Inspect the file\"}");
            p.Emit("{\"type\":\"tool_execution_update\",\"toolCallId\":\"t1\",\"toolName\":\"csv.info\",\"partialResult\":{\"content\":[{\"type\":\"text\",\"text\":\"part\"}]}}");
            await rig.WaitForCountAsync("toolUpdate", 1);

            rig.Clock.Advance(1500);
            p.Emit("{\"type\":\"tool_execution_end\",\"toolCallId\":\"t1\",\"toolName\":\"csv.info\",\"isError\":false,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}");
            p.Emit("{\"type\":\"agent_end\",\"isTerminal\":true}");
            await rig.WaitForCountAsync("turnEnd", 1);

            var actual = rig.Page.WithoutNoise("status", "catalog", "commands");
            Assert.Equal(new[]
            {
                "{\"t\":\"model\",\"model\":\"anthropic/claude-a\"}",
                "{\"t\":\"thinkingDelta\",\"text\":\"Hmm\"}",
                "{\"t\":\"thinkingEnd\"}",
                "{\"t\":\"assistantDelta\",\"text\":\"Hello \"}",
                "{\"t\":\"assistantDelta\",\"text\":\"world\"}",
                "{\"t\":\"assistantEnd\"}",
                "{\"t\":\"toolInputDelta\",\"id\":\"t1\",\"name\":\"csv.info\",\"text\":\"{\\\"i\\\":\\\"look\\\"}\"}",
                "{\"t\":\"toolStart\",\"id\":\"t1\",\"name\":\"csv.info\",\"detail\":\"Inspect the file\",\"input\":\"{\\\"i\\\":\\\"look\\\"}\"}",
                "{\"t\":\"toolUpdate\",\"id\":\"t1\",\"text\":\"part\"}",
                "{\"t\":\"toolEnd\",\"id\":\"t1\",\"ok\":true,\"ms\":1500,\"result\":\"done\"}",
                $"{{\"t\":\"turnEnd\",\"started\":{start},\"ended\":{start + 1500},\"stopped\":false}}",
            }, actual);
        }

        [Fact]
        public async Task The_page_sees_busy_activity_while_a_tool_runs_and_idle_after_the_turn()
        {
            using var rig = await ConnectedAsync();
            var p = rig.Proc;
            p.Emit("{\"type\":\"agent_start\"}");
            p.Emit("{\"type\":\"tool_execution_start\",\"toolCallId\":\"t9\",\"toolName\":\"csv.sort\",\"args\":{}}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("activity").StartsWith("Running tool: csv.sort"));
            Assert.True(rig.Page.Parsed("status").Last().Bool("busy"));
            Assert.True(rig.OnUi(() => rig.Controller.IsBusy));

            rig.Clock.Advance(4200);
            rig.OnUi(rig.Controller.Tick);
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("activity").EndsWith("· 4s"));

            p.Emit("{\"type\":\"tool_execution_end\",\"toolCallId\":\"t9\",\"toolName\":\"csv.sort\",\"isError\":true,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"bad\"}]}}");
            p.Emit("{\"type\":\"agent_end\",\"isTerminal\":true}");
            await rig.WaitUntilAsync(() => !rig.Page.Parsed("status").Last().Bool("busy").GetValueOrDefault(true));
            Assert.Equal("", rig.Page.Parsed("status").Last().Str("activity"));
            Assert.False(rig.Page.Parsed("toolEnd").Last().Bool("ok"));
        }

        [Fact]
        public async Task A_non_terminal_agent_end_keeps_the_turn_open()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            rig.Proc.Emit("{\"type\":\"agent_end\",\"isTerminal\":false}");
            await Task.Delay(100);
            Assert.Empty(rig.Page.Parsed("turnEnd"));
            Assert.True(rig.OnUi(() => rig.Controller.IsBusy));
            rig.Proc.Emit("{\"type\":\"agent_end\",\"isTerminal\":true}");
            await rig.WaitForCountAsync("turnEnd", 1);
        }

        [Fact]
        public async Task Notices_retries_compaction_and_subagents_are_mapped()
        {
            using var rig = await ConnectedAsync();
            var p = rig.Proc;
            p.Emit("{\"type\":\"notice\",\"message\":\"xd://: mounted csv.test\",\"level\":\"info\"}");
            p.Emit("{\"type\":\"notice\",\"message\":\"heads up\",\"level\":\"info\"}");
            p.Emit("{\"type\":\"notice\",\"message\":\"careful\",\"level\":\"warn\"}");
            p.Emit("{\"type\":\"auto_compaction_start\"}");
            p.Emit("{\"type\":\"auto_retry_start\",\"attempt\":1,\"maxAttempts\":3,\"delayMs\":1500,\"errorMessage\":\"overloaded\"}");
            p.Emit("{\"type\":\"retry_fallback_applied\",\"from\":\"a/x\",\"to\":\"b/y\"}");
            p.Emit("{\"type\":\"command_output\",\"text\":\"\\u001b[32mgreen\\u001b[0m text\"}");
            p.Emit("{\"type\":\"extension_error\",\"error\":\"ext failed\"}");
            p.Emit("{\"type\":\"subagent_progress\",\"payload\":{\"progress\":{\"id\":\"sa1\",\"agent\":\"task\",\"description\":\"Check  data\",\"lastIntent\":\"read\",\"status\":\"running\",\"toolCount\":2}}}");
            p.Emit("{\"type\":\"subagent_lifecycle\",\"payload\":{\"id\":\"sa1\",\"agent\":\"task\",\"status\":\"completed\",\"toolCount\":3}}");
            p.Emit("{\"type\":\"response\",\"id\":\"req-777\",\"command\":\"set_model\",\"success\":false,\"error\":\"Model not found\"}");
            await rig.WaitForCountAsync("subagent", 2);
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("text").StartsWith("set_model:")));

            var actual = rig.Page.WithoutNoise("status", "catalog", "commands");
            Assert.Equal(new[]
            {
                "{\"t\":\"notice\",\"level\":\"omp\",\"text\":\"heads up\"}",
                "{\"t\":\"notice\",\"level\":\"warn\",\"text\":\"careful\"}",
                "{\"t\":\"notice\",\"level\":\"info\",\"text\":\"Compacting the conversation…\"}",
                "{\"t\":\"notice\",\"level\":\"retry\",\"text\":\"Retry 1/3 in 2s: overloaded\"}",
                "{\"t\":\"notice\",\"level\":\"retry\",\"text\":\"Switched model: a/x → b/y\",\"models\":[\"a/x\",\"b/y\"]}",
                "{\"t\":\"notice\",\"level\":\"output\",\"text\":\"green text\"}",
                "{\"t\":\"notice\",\"level\":\"error\",\"text\":\"ext failed\"}",
                "{\"t\":\"subagent\",\"id\":\"sa1\",\"agent\":\"task\",\"description\":\"Check data\",\"intent\":\"read\",\"status\":\"running\",\"tools\":2}",
                "{\"t\":\"subagent\",\"id\":\"sa1\",\"agent\":\"task\",\"description\":\"\",\"intent\":\"\",\"status\":\"completed\",\"tools\":3}",
                "{\"t\":\"notice\",\"level\":\"error\",\"text\":\"set_model: Model not found\"}",
            }, actual);
        }

        [Fact]
        public async Task Queue_updates_and_todos_from_state_reach_the_page()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit("{\"type\":\"queue_update\",\"steering\":[\"use parser\"],\"followUp\":[\"then plot\"]}");
            var q = await rig.Page.WaitForTypeAsync("queue");
            Assert.Equal(new[] { "use parser" }, q.Child("steering").Strings());
            Assert.Equal(new[] { "then plot" }, q.Child("followUp").Strings());
            Assert.False(q.Bool("state"));

            // 할 일 도구가 끝나면 get_state로 목록을 다시 읽는다.
            rig.Proc.State["todoPhases"] = JsonNode.Parse("[{\"id\":\"p1\",\"name\":\"Plan\",\"tasks\":[{\"id\":\"t1\",\"content\":\"Load\",\"status\":\"completed\"},{\"id\":\"t2\",\"content\":\"Fit\",\"status\":\"in_progress\"}]}]");
            rig.Proc.State["queuedMessages"] = JsonNode.Parse("{\"steering\":[],\"followUp\":[\"then plot\"]}");
            int statesBefore = rig.Proc.ReceivedSnapshot().Count(f => f.Str("type") == "get_state");
            rig.Proc.Emit("{\"type\":\"tool_execution_end\",\"toolCallId\":\"td\",\"toolName\":\"todo_write\",\"isError\":false,\"result\":{\"content\":[]}}");
            var todos = await rig.Page.WaitForTypeAsync("todos");

            Assert.Equal(statesBefore + 1, rig.Proc.ReceivedSnapshot().Count(f => f.Str("type") == "get_state"));
            var items = todos.Child("items").Items().ToList();
            Assert.Equal(new[] { "Load", "Fit" }, items.Select(i => i.Str("content")));
            Assert.Equal("Plan", items[0].Str("phase"));
            Assert.Equal("in_progress", items[1].Str("status"));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("queue").Any(m => m.Bool("state") == true));
        }

        [Fact]
        public async Task A_local_slash_command_that_starts_no_turn_does_not_leave_the_panel_busy()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Handlers["prompt"] = cmd => FakeOmpProcess.Ok(cmd.Str("id"), "prompt", new JsonObject { ["agentInvoked"] = false });
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"/compact\"}");
            var ack = await rig.Page.WaitForTypeAsync("submitted");
            Assert.True(ack.Bool("ok"));
            await rig.WaitUntilAsync(() => !rig.OnUi(() => rig.Controller.IsBusy));
            Assert.Empty(rig.Page.Parsed("turnEnd"));
        }
    }

    public class ChatControllerSubmitTests
    {
        private static async Task<ControllerRig> ConnectedAsync(Action<FakeOmpProcess>? configure = null)
        {
            var rig = new ControllerRig(configure: configure);
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            return rig;
        }

        [Fact]
        public async Task Submit_when_idle_sends_a_prompt_echoes_the_user_text_and_acks_the_page()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"  요약해 줘  \"}");

            var prompt = await rig.Proc.WaitForTypeAsync("prompt");
            Assert.Equal("요약해 줘", prompt.Str("message"));
            var user = await rig.Page.WaitForTypeAsync("user");
            Assert.Equal("요약해 줘", user.Str("text"));
            Assert.Equal(rig.Clock.UnixMs, user.Int("ts"));
            Assert.Equal(JsonValueKind.Undefined, user.Child("queue").ValueKind);
            var ack = await rig.Page.WaitForTypeAsync("submitted");
            Assert.Equal("s1", ack.Str("id"));
            Assert.True(ack.Bool("ok"));
            Assert.True(rig.OnUi(() => rig.Controller.IsBusy));
        }

        [Fact]
        public async Task Submit_while_busy_steers_or_follows_up_and_marks_the_bubble_as_queued()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsBusy));

            rig.FromPage("{\"t\":\"submit\",\"id\":\"a\",\"text\":\"use the parser\"}");
            rig.FromPage("{\"t\":\"submit\",\"id\":\"b\",\"text\":\"then plot\",\"followUp\":true}");

            var steer = await rig.Proc.WaitForTypeAsync("steer");
            var follow = await rig.Proc.WaitForTypeAsync("follow_up");
            Assert.Equal("use the parser", steer.Str("message"));
            Assert.Equal("then plot", follow.Str("message"));
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "prompt");

            await rig.WaitForCountAsync("user", 2);
            var users = rig.Page.Parsed("user");
            Assert.Equal("steer", users[0].Str("queue"));
            Assert.Equal("use the parser", users[0].Str("sent"));
            Assert.Equal("followUp", users[1].Str("queue"));
        }

        [Fact]
        public async Task Empty_input_and_a_disconnected_controller_do_not_send_anything()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"submit\",\"id\":\"e\",\"text\":\"   \"}");
            var ack = await rig.Page.WaitForTypeAsync("submitted");
            Assert.False(ack.Bool("ok"));

            rig.OnUi(() => rig.Controller.Stop());
            rig.FromPage("{\"t\":\"submit\",\"id\":\"f\",\"text\":\"hello\"}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("submitted").Count == 2);
            Assert.False(rig.Page.Parsed("submitted")[1].Bool("ok"));
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "prompt");
        }

        [Fact]
        public async Task A_rejected_prompt_shows_the_error_and_frees_the_panel()
        {
            using var rig = await ConnectedAsync(p =>
                p.Handlers["prompt"] = cmd => FakeOmpProcess.Fail(cmd.Str("id"), "prompt", "No model selected"));
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s\",\"text\":\"hi\"}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("text") == "prompt: No model selected"));
            await rig.WaitUntilAsync(() => !rig.OnUi(() => rig.Controller.IsBusy));
        }

        [Fact]
        public async Task Cancelling_a_queued_message_sends_remove_queued_message_and_reports_the_answer()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"cancelQueued\",\"sent\":\"then plot\",\"queue\":\"followUp\"}");
            var cmd = await rig.Proc.WaitForTypeAsync("remove_queued_message");
            Assert.Equal("then plot", cmd.Str("message"));
            Assert.Equal("followUp", cmd.Str("queue"));
            var removed = await rig.Page.WaitForTypeAsync("queueRemoved");
            Assert.True(removed.Bool("removed"));
            Assert.Equal("then plot", removed.Str("sent"));
            Assert.Equal("followUp", removed.Str("queue"));

            rig.Proc.Handlers["remove_queued_message"] = c => FakeOmpProcess.Ok(c.Str("id"), "remove_queued_message", new JsonObject { ["removed"] = false });
            rig.FromPage("{\"t\":\"cancelQueued\",\"sent\":\"gone\",\"queue\":\"steer\"}");
            await rig.WaitForCountAsync("queueRemoved", 2);
            var second = rig.Page.Parsed("queueRemoved")[1];
            Assert.False(second.Bool("removed"));
            var sent = rig.Proc.ReceivedSnapshot().Last(f => f.Str("type") == "remove_queued_message");
            Assert.Equal("steering", sent.Str("queue"));
        }

        [Fact]
        public async Task Model_thinking_new_session_and_export_map_to_their_rpc_commands()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"setModel\",\"value\":\"openai/gpt-x\"}");
            var model = await rig.Proc.WaitForTypeAsync("set_model");
            Assert.Equal("openai", model.Str("provider"));
            Assert.Equal("gpt-x", model.Str("modelId"));
            // 모델이 바뀌면 생각 수준 목록을 다시 읽는다.
            await rig.Proc.WaitForAsync(f => f.Str("type") == "get_available_thinking_levels", startIndex: rig.Proc.ReceivedSnapshot().ToList().FindIndex(f => f.Str("type") == "set_model"));

            rig.FromPage("{\"t\":\"setThinking\",\"value\":\"high\"}");
            Assert.Equal("high", (await rig.Proc.WaitForTypeAsync("set_thinking_level")).Str("level"));

            rig.Dialogs.ExportPath = "C:\\out\\chat.html";
            rig.FromPage("{\"t\":\"export\"}");
            Assert.Equal("C:\\out\\chat.html", (await rig.Proc.WaitForTypeAsync("export_html")).Str("outputPath"));

            rig.Dialogs.OnConfirm = (_, _) => false;
            rig.FromPage("{\"t\":\"newSession\"}");
            await Task.Delay(100);
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "new_session");

            rig.Dialogs.OnConfirm = (_, _) => true;
            rig.FromPage("{\"t\":\"newSession\"}");
            await rig.Proc.WaitForTypeAsync("new_session");
            await rig.Page.WaitForTypeAsync("clear");
        }

        [Fact]
        public async Task Slash_commands_are_routed_locally_or_passed_to_omp()
        {
            using var rig = await ConnectedAsync();
            // 일반 슬래시 → omp로 prompt
            rig.FromPage("{\"t\":\"runCommand\",\"text\":\"/compact focus\"}");
            Assert.Equal("/compact focus", (await rig.Proc.WaitForTypeAsync("prompt")).Str("message"));

            // 터미널 전용 → 알림만
            rig.FromPage("{\"t\":\"runCommand\",\"text\":\"/resume\"}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("text").Contains("only works in the omp terminal")));

            // /model provider/id → set_model
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            rig.Proc.Emit("{\"type\":\"agent_end\",\"isTerminal\":true}");
            await rig.WaitForCountAsync("turnEnd", 1);
            rig.FromPage("{\"t\":\"runCommand\",\"text\":\"/model anthropic/claude-b\"}");
            var set = await rig.Proc.WaitForTypeAsync("set_model");
            Assert.Equal("claude-b", set.Str("modelId"));

            // /copy → 마지막 답변을 클립보드로
            rig.Proc.Handlers["get_last_assistant_text"] = c => FakeOmpProcess.Ok(c.Str("id"), "get_last_assistant_text", new JsonObject { ["text"] = "last answer" });
            rig.FromPage("{\"t\":\"runCommand\",\"text\":\"/copy\"}");
            await rig.WaitUntilAsync(() => rig.Dialogs.Clipboard.Contains("last answer"));

            // ! 셸 명령 → bash
            rig.Proc.Handlers["bash"] = c => FakeOmpProcess.Ok(c.Str("id"), "bash", new JsonObject { ["output"] = "hi\n", ["exitCode"] = 2 });
            rig.FromPage("{\"t\":\"submit\",\"id\":\"sh\",\"text\":\"!echo hi\"}");
            Assert.Equal("echo hi", (await rig.Proc.WaitForTypeAsync("bash")).Str("command"));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("level") == "output" && n.Str("text") == "hi\n(exit code 2)"));
        }

        [Fact]
        public async Task Page_messages_the_controller_does_not_own_are_forwarded_to_the_host()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"settings\"}");
            rig.FromPage("{\"t\":\"listColumns\"}");
            rig.FromPage("{\"t\":\"openFile\",\"path\":\"a.csv\",\"line\":3}");
            await rig.WaitUntilAsync(() => { lock (rig.Unhandled) return rig.Unhandled.Count == 3; });
            Assert.Equal(new[] { "settings", "listColumns", "openFile" }, rig.Unhandled.Select(e => e.Str("t")));
        }

        [Fact]
        public async Task A_reloaded_page_gets_the_transcript_replayed_but_the_first_ready_does_not()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            rig.Proc.Emit("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"partial\"}}");
            await rig.Page.WaitForTypeAsync("assistantDelta");

            int before = rig.Page.Snapshot().Count;
            rig.FromPage("{\"t\":\"ready\"}");
            await rig.WaitUntilAsync(() => rig.Page.Snapshot().Count > before);
            Assert.Empty(rig.Page.Snapshot().Skip(before).Where(s => s.Contains("\"t\":\"clear\"")));

            rig.FromPage("{\"t\":\"ready\"}");
            await rig.Page.WaitForTypeAsync("clear");
            var after = rig.Page.Snapshot();
            int clearAt = after.FindIndex(s => s.Contains("\"t\":\"clear\""));
            Assert.Contains("{\"t\":\"assistantDelta\",\"text\":\"partial\"}", after.Skip(clearAt));
        }
    }

    public class ChatControllerHostToolTests
    {
        private static async Task<ControllerRig> ConnectedAsync()
        {
            var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            return rig;
        }

        [Fact]
        public async Task A_host_tool_call_runs_the_executor_and_answers_with_host_tool_result()
        {
            using var rig = await ConnectedAsync();
            rig.Tools.OnExecute = (call, _, _) => Task.FromResult(new HostToolResult($"rows={call.Arguments.Int("n")}", false, "AAAA"));
            rig.Proc.Emit("{\"type\":\"host_tool_call\",\"id\":\"host_1\",\"toolCallId\":\"tc1\",\"toolName\":\"csv.get_rows\",\"arguments\":{\"n\":5}}");

            var result = await rig.Proc.WaitForTypeAsync("host_tool_result");
            Assert.Equal("host_1", result.Str("id"));
            var parts = result.Child("result").Child("content").Items().ToList();
            Assert.Equal("rows=5", parts[0].Str("text"));
            Assert.Equal("image", parts[1].Str("type"));
        }

        [Fact]
        public async Task The_executor_runs_on_the_ui_thread()
        {
            using var rig = await ConnectedAsync();
            int callThread = -1, uiThread = rig.OnUi(() => Environment.CurrentManagedThreadId);
            rig.Tools.OnExecute = (_, _, _) => { callThread = Environment.CurrentManagedThreadId; return Task.FromResult(HostToolResult.Ok("x")); };
            rig.Proc.Emit("{\"type\":\"host_tool_call\",\"id\":\"h\",\"toolCallId\":\"t\",\"toolName\":\"csv.info\",\"arguments\":{}}");
            await rig.Proc.WaitForTypeAsync("host_tool_result");
            Assert.Equal(uiThread, callThread);
        }

        [Fact]
        public async Task host_tool_cancel_cancels_the_token_and_suppresses_the_result()
        {
            using var rig = await ConnectedAsync();
            var started = new TaskCompletionSource<CancellationToken>();
            rig.Tools.OnExecute = async (_, _, ct) => { started.SetResult(ct); await Task.Delay(Timeout.Infinite, ct); return HostToolResult.Ok("no"); };
            rig.Proc.Emit("{\"type\":\"host_tool_call\",\"id\":\"host_2\",\"toolCallId\":\"tc\",\"toolName\":\"csv.run_analysis\",\"arguments\":{}}");
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            rig.Proc.Emit("{\"type\":\"host_tool_cancel\",\"id\":\"c1\",\"targetId\":\"host_2\"}");
            await rig.WaitUntilAsync(() => token.IsCancellationRequested);
            await Task.Delay(150);
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "host_tool_result");
        }

        [Fact]
        public async Task An_oversize_tool_result_becomes_an_error_text_of_at_most_one_mebibyte()
        {
            using var rig = await ConnectedAsync();
            rig.Tools.OnExecute = (_, _, _) => Task.FromResult(HostToolResult.Ok(new string('x', RpcProtocol.MaxFrameBytes + 1)));
            rig.Proc.Emit("{\"type\":\"host_tool_call\",\"id\":\"host_3\",\"toolCallId\":\"tc\",\"toolName\":\"csv.get_rows\",\"arguments\":{}}");
            var result = await rig.Proc.WaitForTypeAsync("host_tool_result");
            Assert.True(result.Bool("isError"));
            Assert.Contains("too large", result.Child("result").Child("content").ContentText());
        }
    }

    public class ChatControllerApprovalTests
    {
        private static async Task<ControllerRig> ConnectedAsync()
        {
            var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            return rig;
        }

        [Fact]
        public async Task Approval_round_trip_posts_a_card_and_resolves_with_the_users_answer()
        {
            using var rig = await ConnectedAsync();
            Task<bool> answer = default!;
            rig.OnUi(() => answer = rig.Controller.ApproveAsync("Edit 2 cells", "2 changes", new[] { "+ new", "- old", "  same", "plain" }, CancellationToken.None));

            var card = await rig.Page.WaitForTypeAsync("approval");
            string id = card.Str("id");
            Assert.Equal("Edit 2 cells", card.Str("target"));
            Assert.Equal("2 changes", card.Str("summary"));
            var lines = card.Child("lines").Items().Select(l => (l.Str("k"), l.Str("t"))).ToList();
            Assert.Equal(new[] { ("add", "new"), ("del", "old"), ("same", "same"), ("text", "plain") }, lines);
            Assert.False(answer.IsCompleted);

            rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{id}\",\"ok\":true}}");
            Assert.True(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
            var result = await rig.Page.WaitForTypeAsync("approvalResult");
            Assert.Equal(id, result.Str("id"));
            Assert.True(result.Bool("ok"));

            // 거절
            Task<bool> second = default!;
            rig.OnUi(() => second = rig.Controller.ApproveAsync("Save", "", new[] { "x" }, CancellationToken.None));
            await rig.WaitForCountAsync("approval", 2);
            string id2 = rig.Page.Parsed("approval")[1].Str("id");
            Assert.NotEqual(id, id2);
            rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{id2}\",\"ok\":false}}");
            Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task A_card_answers_once_and_a_cancelled_token_closes_it_as_refused()
        {
            using var rig = await ConnectedAsync();
            using var cts = new CancellationTokenSource();
            Task<bool> answer = default!;
            rig.OnUi(() => answer = rig.Controller.ApproveAsync("t", "s", new[] { "x" }, cts.Token));
            var card = await rig.Page.WaitForTypeAsync("approval");
            string id = card.Str("id");

            cts.Cancel();
            Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
            await rig.Page.WaitForTypeAsync("approvalResult");

            // 이미 닫힌 카드에 대한 늦은 승인은 무시된다.
            rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{id}\",\"ok\":true}}");
            await Task.Delay(100);
            Assert.Single(rig.Page.Parsed("approvalResult"));
        }

        [Fact]
        public async Task Stopping_the_turn_refuses_open_cards()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsBusy));
            Task<bool> answer = default!;
            rig.OnUi(() => answer = rig.Controller.ApproveAsync("t", "s", new[] { "x" }, CancellationToken.None));
            await rig.Page.WaitForTypeAsync("approval");

            rig.FromPage("{\"t\":\"abort\"}");
            Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False((await rig.Page.WaitForTypeAsync("approvalResult")).Bool("ok"));
            await rig.Proc.WaitForTypeAsync("abort");
        }

        [Fact]
        public async Task A_tool_executor_can_ask_for_approval_during_a_host_tool_call()
        {
            using var rig = await ConnectedAsync();
            rig.Tools.OnExecute = async (call, approvals, ct) =>
            {
                bool ok = await approvals.ApproveAsync("Edit cells", "1 change", new[] { "- a", "+ b" }, ct);
                return ok ? HostToolResult.Ok("applied") : HostToolResult.Error("denied by user");
            };
            rig.Proc.Emit("{\"type\":\"host_tool_call\",\"id\":\"host_9\",\"toolCallId\":\"tc\",\"toolName\":\"csv.edit_cells\",\"arguments\":{}}");
            var card = await rig.Page.WaitForTypeAsync("approval");
            rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{card.Str("id")}\",\"ok\":false}}");

            var result = await rig.Proc.WaitForTypeAsync("host_tool_result");
            Assert.True(result.Bool("isError"));
            Assert.Equal("denied by user", result.Child("result").Child("content").ContentText());
        }

        private const string ToolAskTitle = "Allow tool: bash\nPath: C:\\work\\x.csv\nrm -rf data";

        [Fact]
        public async Task Omp_tool_approval_selects_become_cards_and_the_choice_is_sent_back()
        {
            using var rig = await ConnectedAsync();
            var select = new JsonObject
            {
                ["type"] = "extension_ui_request", ["id"] = "ui_1", ["method"] = "select",
                ["title"] = ToolAskTitle, ["options"] = new JsonArray("Approve once", "Deny"),
            };
            rig.Proc.Emit(select);
            var card = await rig.Page.WaitForTypeAsync("approval");
            Assert.Equal("Allow tool: bash", card.Str("target"));
            Assert.Equal(new[] { "Path: C:\\work\\x.csv", "rm -rf data" }, card.Child("lines").Items().Select(l => l.Str("t")));

            rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{card.Str("id")}\",\"ok\":true}}");
            var reply = await rig.Proc.WaitForTypeAsync("extension_ui_response");
            Assert.Equal("ui_1", reply.Str("id"));
            Assert.Equal("Approve once", reply.Str("value"));

            select["id"] = "ui_2";
            rig.Proc.Emit(select);
            await rig.WaitForCountAsync("approval", 2);
            rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{rig.Page.Parsed("approval")[1].Str("id")}\",\"ok\":false}}");
            var denied = await rig.Proc.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "ui_2");
            Assert.Equal("Deny", denied.Str("value"));
        }

        [Fact]
        public async Task Omp_approvals_for_csv_tools_pass_without_a_second_card_and_cancel_closes_a_pending_card()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit(new JsonObject
            {
                ["type"] = "extension_ui_request", ["id"] = "ui_csv", ["method"] = "select",
                ["title"] = "Allow tool: csv.edit_cells\nPath: xd://csv.edit_cells", ["options"] = new JsonArray("Approve", "Deny"),
            });
            var reply = await rig.Proc.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "ui_csv");
            Assert.Equal("Approve", reply.Str("value"));
            Assert.Empty(rig.Page.Parsed("approval"));

            rig.Proc.Emit(new JsonObject
            {
                ["type"] = "extension_ui_request", ["id"] = "ui_w", ["method"] = "select",
                ["title"] = ToolAskTitle, ["options"] = new JsonArray("Allow", "Reject"),
            });
            await rig.Page.WaitForTypeAsync("approval");
            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"c\",\"method\":\"cancel\",\"targetId\":\"ui_w\"}");
            var closed = await rig.Proc.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "ui_w");
            Assert.Equal("Reject", closed.Str("value"));
            Assert.False((await rig.Page.WaitForTypeAsync("approvalResult")).Bool("ok"));
        }

        [Fact]
        public async Task Other_extension_ui_requests_use_dialogs_and_reply_with_the_protocol_shapes()
        {
            using var rig = await ConnectedAsync();
            rig.Dialogs.OnConfirm = (_, _) => false;
            rig.Dialogs.OnSelect = (_, o) => o[1];
            rig.Dialogs.OnInput = (title, _, _, multiline) => multiline ? "edited" : null;

            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"q1\",\"method\":\"confirm\",\"title\":\"Sure?\",\"message\":\"Continue?\"}");
            var confirm = await rig.Proc.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "q1");
            Assert.False(confirm.Bool("confirmed"));

            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"q2\",\"method\":\"select\",\"title\":\"Pick\",\"options\":[\"a\",\"b\"]}");
            var select = await rig.Proc.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "q2");
            Assert.Equal("b", select.Str("value"));

            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"q3\",\"method\":\"input\",\"title\":\"Name\"}");
            var input = await rig.Proc.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "q3");
            Assert.True(input.Bool("cancelled"));

            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"q4\",\"method\":\"editor\",\"title\":\"Text\",\"prefill\":\"x\"}");
            var editor = await rig.Proc.WaitForAsync(f => f.Str("type") == "extension_ui_response" && f.Str("id") == "q4");
            Assert.Equal("edited", editor.Str("value"));
        }

        [Fact]
        public async Task Notify_status_url_and_editor_text_requests_are_shown_not_answered()
        {
            using var rig = await ConnectedAsync();
            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"n1\",\"method\":\"notify\",\"message\":\"Saved\",\"notifyType\":\"info\"}");
            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"n2\",\"method\":\"setStatus\",\"statusKey\":\"k\",\"statusText\":\"Indexing\"}");
            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"n3\",\"method\":\"open_url\",\"url\":\"https://example.com/login\",\"message\":\"Sign in\"}");
            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"n4\",\"method\":\"open_url\",\"url\":\"file:///c:/windows/system32/calc.exe\"}");
            rig.Proc.Emit("{\"type\":\"extension_ui_request\",\"id\":\"n5\",\"method\":\"set_editor_text\",\"text\":\"draft\"}");
            await rig.Page.WaitForTypeAsync("setInput");

            var notices = rig.Page.Parsed("notice").Select(n => n.Str("text")).ToList();
            Assert.Contains("Saved", notices);
            Assert.Contains("Indexing", notices);
            Assert.Contains(notices, t => t.Contains("https://example.com/login"));
            Assert.Equal(new[] { "https://example.com/login" }, rig.Dialogs.Opened);
            Assert.Equal("draft", rig.Page.Parsed("setInput").Single().Str("text"));
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "extension_ui_response");
        }

        [Fact]
        public void Default_replies_when_no_dialog_can_be_shown()
        {
            static string Reply(string request) => ChatController_DefaultReply(request)!;
            Assert.True(JsonDocument.Parse(Reply("{\"id\":\"1\",\"method\":\"input\"}")).RootElement.Bool("cancelled"));
            Assert.Equal("first", JsonDocument.Parse(Reply("{\"id\":\"2\",\"method\":\"select\",\"options\":[\"first\",\"second\"]}")).RootElement.Str("value"));
            Assert.True(JsonDocument.Parse(Reply("{\"id\":\"3\",\"method\":\"confirm\"}")).RootElement.Bool("confirmed"));
            Assert.True(JsonDocument.Parse(Reply("{\"id\":\"5\",\"method\":\"select\"}")).RootElement.Bool("cancelled"));
            Assert.Null(ChatController_DefaultReply("{\"id\":\"4\",\"method\":\"notify\"}"));
            Assert.Null(ChatController_DefaultReply("{\"method\":\"confirm\"}"));
        }

        private static string? ChatController_DefaultReply(string request)
        {
            using var doc = JsonDocument.Parse(request);
            return ChatController.DefaultUiReply(doc.RootElement);
        }
    }

    public class ChatControllerStopAndRecoveryTests
    {
        private static async Task<ControllerRig> BusyAsync()
        {
            var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").LastOrDefault().Str("model") == "anthropic/claude-a");
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsBusy));
            return rig;
        }

        [Fact]
        public async Task Abort_sends_abort_and_a_turn_that_ends_in_time_is_not_killed()
        {
            using var rig = await BusyAsync();
            var first = rig.Proc;
            rig.FromPage("{\"t\":\"abort\"}");
            await first.WaitForTypeAsync("abort");
            await rig.Page.WaitForAsync(n => n.Str("t") == "notice" && n.Str("text") == "Stopping…");

            rig.Clock.Advance(ChatController.StopGraceMs - 1);
            rig.OnUi(rig.Controller.Tick);
            first.Emit("{\"type\":\"agent_end\",\"isTerminal\":true}");
            var end = await rig.Page.WaitForTypeAsync("turnEnd");
            Assert.True(end.Bool("stopped"));

            rig.Clock.Advance(10_000);
            rig.OnUi(rig.Controller.Tick);
            await Task.Delay(100);
            Assert.False(first.Killed);
            Assert.Single(rig.Factory.Processes);
        }

        [Fact]
        public async Task A_stuck_turn_is_killed_after_the_grace_period_and_resumed_on_the_same_session()
        {
            using var rig = await BusyAsync();
            var first = rig.Proc;
            rig.FromPage("{\"t\":\"abort\"}");
            await first.WaitForTypeAsync("abort");

            rig.Clock.Advance(ChatController.StopGraceMs - 1);
            rig.OnUi(rig.Controller.Tick);
            await Task.Delay(100);
            Assert.False(first.Killed);

            rig.Clock.Advance(1);
            rig.OnUi(rig.Controller.Tick);
            await rig.WaitUntilAsync(() => first.Killed);

            var end = await rig.Page.WaitForTypeAsync("turnEnd");
            Assert.True(end.Bool("stopped"));
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            var second = rig.Factory.Last;
            var switched = await second.WaitForTypeAsync("switch_session");
            Assert.Equal("C:\\sessions\\s1.jsonl", switched.Str("sessionPath"));
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            Assert.False(rig.OnUi(() => rig.Controller.IsBusy));
        }

        [Fact]
        public async Task Pressing_stop_twice_kills_immediately()
        {
            using var rig = await BusyAsync();
            var first = rig.Proc;
            rig.FromPage("{\"t\":\"abort\"}");
            await first.WaitForTypeAsync("abort");
            rig.FromPage("{\"t\":\"abort\"}");
            await rig.WaitUntilAsync(() => first.Killed);
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
        }

        [Fact]
        public async Task Abort_while_idle_just_sends_abort()
        {
            using var rig = new ControllerRig();
            await rig.StartAsync();
            rig.FromPage("{\"t\":\"abort\"}");
            await rig.Proc.WaitForTypeAsync("abort");
            rig.Clock.Advance(60_000);
            rig.OnUi(rig.Controller.Tick);
            await Task.Delay(100);
            Assert.False(rig.Proc.Killed);
        }

        [Fact]
        public async Task A_child_that_dies_is_restarted_once_per_minute_on_the_same_session()
        {
            using var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").LastOrDefault().Str("model") == "anthropic/claude-a");
            var first = rig.Proc;

            first.Crash(1, "Pipe closed");
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            var second = rig.Factory.Last;
            Assert.Equal("C:\\sessions\\s1.jsonl", (await second.WaitForTypeAsync("switch_session")).Str("sessionPath"));
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Contains(rig.Page.Parsed("notice"), n => n.Str("level") == "warn" && n.Str("text").Contains("Pipe closed"));

            // 1분 안에 또 죽으면 다시 시작하지 않고 오류로 남긴다.
            rig.Clock.Advance(ChatController.RecoverGapMs - 1);
            second.Crash(1, "again");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Bool("error") == true);
            await Task.Delay(150);
            Assert.Equal(2, rig.Factory.Processes.Count);
            Assert.False(rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Contains("again", rig.Page.Parsed("status").Last().Str("state"));
        }

        [Fact]
        public async Task After_the_gap_a_dead_child_is_restarted_again()
        {
            using var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            rig.Proc.Crash(1);
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));

            rig.Clock.Advance(ChatController.RecoverGapMs);
            rig.Factory.Last.Crash(1);
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 3);
        }

        [Fact]
        public async Task A_child_that_dies_mid_turn_ends_the_turn_as_stopped_and_refuses_cards()
        {
            using var rig = await BusyAsync();
            Task<bool> answer = default!;
            rig.OnUi(() => answer = rig.Controller.ApproveAsync("t", "s", new[] { "x" }, CancellationToken.None));
            await rig.Page.WaitForTypeAsync("approval");

            rig.Proc.Crash(137, "killed");
            Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
            var end = await rig.Page.WaitForTypeAsync("turnEnd");
            Assert.True(end.Bool("stopped"));
        }

        [Fact]
        public async Task A_child_that_dies_during_the_handshake_is_not_restarted()
        {
            using var rig = new ControllerRig(configure: p =>
            {
                p.Handlers["set_host_tools"] = _ => "";
                p.Handlers["negotiate_protocol"] = _ => null;
            });
            var start = rig.Ui.InvokeAsync(() => rig.Controller.StartAsync(rig.WorkDir));
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 1);
            await rig.Proc.WaitForTypeAsync("set_host_tools");
            rig.Proc.Crash(1, "boom");
            await start;
            await Task.Delay(200);
            Assert.Single(rig.Factory.Processes);
            Assert.False(rig.OnUi(() => rig.Controller.IsRunning));
            Assert.True(rig.Page.Parsed("status").Last().Bool("error"));
        }
    }

    public class SlashRoutesTests
    {
        private static readonly string[] Rpc = { "compact", "fast", "model", "settings" };

        [Theory]
        [InlineData("hello", "Prompt")]
        [InlineData("/", "Prompt")]
        [InlineData("/new", "NewSession")]
        [InlineData("/abort", "Abort")]
        [InlineData("/model", "ListModels")]
        [InlineData("/model anthropic claude-b", "SetModel")]
        [InlineData("/model anthropic/claude-b", "SetModel")]
        [InlineData("/model fuzzy match words", "PassThrough")]
        [InlineData("/fast", "Fast")]
        [InlineData("/fast ON", "Fast")]
        [InlineData("/fast ultra", "PassThrough")]
        [InlineData("/thinking high", "Thinking")]
        [InlineData("/effort", "Thinking")]
        [InlineData("/compact keep schema", "PassThrough")]
        [InlineData("/settings", "PassThrough")]   // omp가 목록에 갖고 있으면 omp가 처리
        [InlineData("/clear", "Clear")]
        [InlineData("/copy", "Copy")]
        [InlineData("/resume", "TerminalOnly")]
        [InlineData("/annotate", "TerminalOnly")]
        [InlineData("/unknown-skill arg", "PassThrough")]
        public void Routing(string input, string expected)
        {
            Assert.Equal(expected, SlashRoutes.Route(input, Rpc).Kind.ToString());
        }

        [Fact]
        public void Model_arguments_are_split_into_provider_and_id()
        {
            var r = SlashRoutes.Route("/model anthropic claude-b", Rpc);
            Assert.Equal(("anthropic", "claude-b"), (r.Arg1, r.Arg2));
            var r2 = SlashRoutes.Route("/model openai/gpt-x", Rpc);
            Assert.Equal(("openai", "gpt-x"), (r2.Arg1, r2.Arg2));
        }
    }
}
