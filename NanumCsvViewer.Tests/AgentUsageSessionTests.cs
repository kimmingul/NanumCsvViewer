using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    // `omp usage --json`의 실제 모양(축약): 5시간/7일 한도, tier로 좁힌 7일 한도, 중복 한도, 요금제.
    internal static class UsageSamples
    {
        public const string Anthropic = """
        {"generatedAt":1,"reports":[
          {"provider":"openai","limits":[]},
          {"provider":"anthropic","metadata":{"planType":"max"},"limits":[
            {"id":"a:5h","label":"Claude 5 Hour","scope":{"windowId":"5h"},"window":{"id":"5h","label":"5 Hour","resetsAt":1000},"amount":{"usedFraction":0.25},"status":"ok"},
            {"id":"a:7d","label":"Claude 7 Day","scope":{"windowId":"7d"},"window":{"id":"7d","label":"7 Day","resetsAt":2000},"amount":{"usedFraction":0.04},"status":"ok"},
            {"id":"a:7d:fable","label":"Claude 7 Day (Fable)","scope":{"windowId":"7d","tier":"fable"},"window":{"id":"7d","label":"7 Day","resetsAt":2001},"amount":{"usedFraction":0.9},"status":"warn"},
            {"id":"a:5h:dup","label":"Claude 5 Hour","scope":{"windowId":"5h"},"window":{"id":"5h","label":"5 Hour","resetsAt":1000},"amount":{"usedFraction":0.25},"status":"ok"},
            {"id":"a:none","label":"No amount","window":{"id":"1d","label":"1 Day","resetsAt":3}}
          ]}]}
        """;
    }

    public class UsageReportTests
    {
        [Fact]
        public void Limits_of_one_provider_are_shaped_for_the_panel_deduplicated_and_grouped()
        {
            var limits = UsageReport.Limits(UsageSamples.Anthropic, "anthropic")!;
            Assert.Equal("Max", (string?)limits["plan"]);
            var rows = limits["limits"]!.AsArray().Select(r => r!.AsObject()).ToList();
            Assert.Equal(3, rows.Count); // 중복 한도와 amount 없는 한도는 빠진다
            Assert.Equal(("5h", "5 Hour", ""), ((string?)rows[0]["window"], (string?)rows[0]["windowLabel"], (string?)rows[0]["group"]));
            Assert.Equal(0.25, (double)rows[0]["used"]!);
            Assert.Equal(1000, (long)rows[0]["resetsAt"]!);
            Assert.Equal("", (string?)rows[1]["group"]);       // 라벨이 창 이름을 포함 → 그룹 없음
            Assert.Equal("Fable", (string?)rows[2]["group"]);  // tier가 그룹
            Assert.Equal("warn", (string?)rows[2]["status"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{\"reports\":[{\"provider\":\"openai\"}]}")]
        public void Missing_or_unparsable_reports_give_no_limits(string? json)
        {
            Assert.Null(UsageReport.Limits(json, "anthropic"));
        }
    }

    public class SessionCatalogTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncv-sessions-" + Guid.NewGuid().ToString("N"));

        public SessionCatalogTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        internal static string Write(string dir, string name, DateTime modified, string? title, string? firstUser)
        {
            var lines = new List<string>
            {
                new JsonObject { ["type"] = "title", ["v"] = 1, ["title"] = title ?? "", ["pad"] = "   " }.ToJsonString(),
                "{\"type\":\"session\",\"version\":3,\"id\":\"x\",\"cwd\":\"C:\\\\tmp\"}",
                "{\"type\":\"model_change\",\"model\":\"a/b\"}",
            };
            if (firstUser != null)
                lines.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["message"] = new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = firstUser }),
                    },
                }.ToJsonString());
            string path = Path.Combine(dir, name + ".jsonl");
            File.WriteAllLines(path, lines);
            File.SetLastWriteTime(path, modified);
            return path;
        }

        [Fact]
        public void Directory_names_follow_omps_encoding_of_the_working_folder()
        {
            Assert.Equal("--C--tmp--", SessionCatalog.DirectoryNameFor("C:\\tmp"));
            Assert.Equal("--C--tmp--", SessionCatalog.DirectoryNameFor("C:\\tmp\\"));
            Assert.Equal("--C--Program Files (x86)-Embarcadero-Studio-37.0-bin64--",
                SessionCatalog.DirectoryNameFor("C:\\Program Files (x86)\\Embarcadero\\Studio\\37.0\\bin64"));
        }

        [Fact]
        public void The_directory_comes_from_the_session_file_else_from_the_encoded_cwd_under_the_root()
        {
            string root = Path.Combine(_dir, "root");
            string cwdDir = Path.Combine(root, "--C--data--");
            Directory.CreateDirectory(cwdDir);
            string other = Path.Combine(_dir, "other");
            Directory.CreateDirectory(other);

            Assert.Equal(other, SessionCatalog.FindDirectory(Path.Combine(other, "s.jsonl"), "C:\\data", root));
            Assert.Equal(cwdDir, SessionCatalog.FindDirectory(null, "C:\\data", root));
            Assert.Equal(cwdDir, SessionCatalog.FindDirectory(Path.Combine(_dir, "gone", "s.jsonl"), "C:\\data", root));
            Assert.Null(SessionCatalog.FindDirectory(null, "C:\\nowhere", root));
            Assert.Null(SessionCatalog.FindDirectory(null, "", root));
        }

        [Fact]
        public void Sessions_list_newest_first_prefer_omps_title_and_skip_empty_ones_except_the_current()
        {
            var now = new DateTime(2026, 10, 1, 12, 0, 0);
            string older = Write(_dir, "a-old", now.AddDays(-3), null, "load  the\nfile");
            string named = Write(_dir, "b-named", now.AddDays(-1), "Sales regression", "ignored question");
            string newest = Write(_dir, "c-new", now, null, new string('x', 100));
            string emptyOld = Write(_dir, "d-empty", now.AddDays(-2), null, null);
            string emptyCurrent = Write(_dir, "e-current-empty", now.AddHours(-1), null, null);
            File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not a session");

            var list = SessionCatalog.List(_dir, emptyCurrent);
            Assert.Equal(new[] { newest, emptyCurrent, named, older }, list.Select(s => s.Path));
            Assert.Equal("Sales regression", list[2].Title);
            Assert.Equal("load the file", list[3].Title);         // 공백 정리
            Assert.Equal(60, list[0].Title.Length);              // 길면 줄임표로 자름
            Assert.EndsWith("…", list[0].Title);
            Assert.False(list[1].HasMessages);
            Assert.DoesNotContain(list, s => s.Path == emptyOld);
            Assert.Equal("2026-10-01 12:00  load the file  (x)",
                SessionCatalog.Label(list[3] with { Modified = now }, true, "  (x)"));
        }

        [Fact]
        public void The_list_is_capped()
        {
            var now = DateTime.Now;
            for (int i = 0; i < 6; i++) Write(_dir, "s" + i, now.AddMinutes(-i), null, "q" + i);
            var list = SessionCatalog.List(_dir, null, max: 4);
            Assert.Equal(4, list.Count);
            Assert.Equal("q0", list[0].Title);
        }
    }

    public class ChatControllerUsageTests
    {
        private static async Task<ControllerRig> ConnectedAsync(Func<string, IReadOnlyList<string>, string, CancellationToken, Task<string?>>? cli = null)
        {
            var rig = new ControllerRig(configure: p =>
            {
                p.Handlers["get_session_stats"] = cmd => FakeOmpProcess.Ok(cmd.Str("id"), "get_session_stats", new JsonObject
                {
                    ["contextUsage"] = new JsonObject { ["tokens"] = 2000, ["contextWindow"] = 200000 },
                    ["tokens"] = new JsonObject { ["input"] = 1500, ["output"] = 500, ["cacheRead"] = 10, ["cacheWrite"] = 5, ["total"] = 2015 },
                    ["cost"] = 0.42,
                });
            });
            if (cli != null) rig.Cli = cli;
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("model") == "anthropic/claude-a");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Count >= 3); // 로그인 제공자 이름까지 도착
            return rig;
        }

        [Fact]
        public async Task The_ring_click_answers_at_once_then_fills_in_stats_and_plan_limits_without_blocking()
        {
            var cliStarted = new TaskCompletionSource<(string Exe, string[] Args, string Cwd)>();
            var cliResult = new TaskCompletionSource<string?>();
            using var rig = await ConnectedAsync((exe, args, cwd, _) =>
            {
                cliStarted.TrySetResult((exe, args.ToArray(), cwd));
                return cliResult.Task;
            });

            rig.FromPage("{\"t\":\"usage\"}");
            var first = await rig.Page.WaitForTypeAsync("usage");
            Assert.True(first.Bool("loading"));                       // 한도 보고서를 읽는 중
            Assert.Equal("Anthropic (Claude Pro/Max)", first.Str("provider"));
            var cli = await cliStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("C:\\fake\\omp.exe", cli.Exe);
            Assert.Equal(new[] { "usage", "--json", "--provider", "anthropic" }, cli.Args);
            Assert.Equal(rig.WorkDir, cli.Cwd);

            // CLI가 아직 끝나지 않았어도 세션 통계는 도착해 표시된다.
            await rig.WaitUntilAsync(() => rig.Page.Parsed("usage").Any(u => u.Child("stats").IsObject()));
            var withStats = rig.Page.Parsed("usage").First(u => u.Child("stats").IsObject());
            Assert.Equal(200000, withStats.Child("stats").Child("contextUsage").Int("contextWindow"));
            Assert.Equal(0.42, withStats.Child("stats").Num("cost"));
            Assert.Equal(2015, withStats.Child("stats").Child("tokens").Int("total"));
            Assert.True(withStats.Bool("loading"));

            cliResult.SetResult(UsageSamples.Anthropic);
            await rig.WaitUntilAsync(() => rig.Page.Parsed("usage").Any(u => u.Bool("loading") == false && u.Child("limits").IsObject()));
            var done = rig.Page.Parsed("usage").Last(u => u.Bool("loading") == false);
            Assert.Equal("", done.Str("error"));
            Assert.Equal("Max", done.Child("limits").Str("plan"));
            Assert.Equal(3, done.Child("limits").Child("limits").Items().Count());
            Assert.True(done.Child("stats").IsObject());
        }

        [Fact]
        public async Task The_report_is_cached_for_a_minute_and_failures_show_an_error_but_keep_the_stats()
        {
            int calls = 0;
            string? answer = UsageSamples.Anthropic;
            using var rig = await ConnectedAsync((_, _, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult(answer); });

            rig.FromPage("{\"t\":\"usage\"}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("usage").Any(u => u.Child("limits").IsObject()));
            rig.FromPage("{\"t\":\"usage\"}");
            await rig.WaitForCountAsync("usage", 4);
            Assert.Equal(1, calls);

            rig.Clock.Advance(59_000);
            rig.FromPage("{\"t\":\"usage\"}");
            await Task.Delay(100);
            Assert.Equal(1, calls);

            // 1분이 지나면 다시 읽는다. 이번에는 omp usage가 없거나 실패한 경우.
            rig.Clock.Advance(2_000);
            answer = null;
            int before = rig.Page.Parsed("usage").Count;
            rig.FromPage("{\"t\":\"usage\"}");
            await rig.WaitUntilAsync(() => calls == 2);
            await rig.WaitUntilAsync(() => rig.Page.Parsed("usage").Skip(before).Any(u => u.Bool("loading") == false && u.Str("error").Length > 0));
            var failed = rig.Page.Parsed("usage").Last();
            Assert.Contains("not available", failed.Str("error"));
            Assert.Equal(JsonValueKind.Undefined, failed.Child("limits").ValueKind);
            Assert.True(failed.Child("stats").IsObject());
        }

        [Fact]
        public async Task A_throwing_cli_runner_is_reported_as_unavailable_not_as_a_crash()
        {
            using var rig = await ConnectedAsync((_, _, _, _) => throw new InvalidOperationException("no process"));
            rig.FromPage("{\"t\":\"usage\"}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("usage").Any(u => u.Bool("loading") == false && u.Str("error").Length > 0));
        }

        [Fact]
        public async Task Usage_while_disconnected_reports_an_error_in_the_page_shape()
        {
            using var rig = new ControllerRig();
            rig.FromPage("{\"t\":\"usage\"}");
            var u = await rig.Page.WaitForTypeAsync("usage");
            Assert.Contains("not connected", u.Str("error"));
            Assert.False(u.Bool("loading"));
        }
    }

    public class ChatControllerSessionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncv-ctrl-sessions-" + Guid.NewGuid().ToString("N"));

        public ChatControllerSessionTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private async Task<(ControllerRig Rig, string Current, string Old, string Named)> ConnectedAsync()
        {
            var now = DateTime.Now;
            string current = SessionCatalogTests.Write(_dir, "cur", now, null, "current question");
            string old = SessionCatalogTests.Write(_dir, "old", now.AddDays(-2), null, "older question");
            string named = SessionCatalogTests.Write(_dir, "named", now.AddDays(-1), "Quality review", "whatever");
            var rig = new ControllerRig(configure: p => p.State["sessionFile"] = current);
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("model") == "anthropic/claude-a");
            return (rig, current, old, named);
        }

        [Fact]
        public async Task The_list_offers_new_conversation_first_then_this_folders_sessions_newest_first()
        {
            var (rig, _, _, _) = await ConnectedAsync();
            using var owner = rig;
            IReadOnlyList<string>? offered = null;
            rig.Dialogs.OnSelect = (_, options) => { offered = options; return null; };
            rig.FromPage("{\"t\":\"sessions\"}");

            Assert.NotNull(offered);
            Assert.Equal(4, offered!.Count);
            Assert.Equal("+ New conversation", offered[0]);
            Assert.EndsWith("current question  (current)", offered[1]);
            Assert.EndsWith("Quality review", offered[2]);
            Assert.EndsWith("older question", offered[3]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}  ", offered[2]);
        }

        [Fact]
        public async Task Choosing_a_session_switches_to_it_and_replays_its_history_across_pages()
        {
            var (rig, current, old, named) = await ConnectedAsync();
            using var owner = rig;
            rig.Proc.Handlers["get_messages_page"] = cmd =>
            {
                bool second = cmd.Str("cursor") == "c1";
                var messages = second
                    ? new JsonArray(new JsonObject { ["role"] = "assistant", ["timestamp"] = 1100, ["completedAt"] = 1500, ["provider"] = "anthropic", ["model"] = "claude-a", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "answer" }) })
                    : new JsonArray(new JsonObject { ["role"] = "user", ["timestamp"] = 1000, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "older question" }) });
                var data = new JsonObject { ["messages"] = messages };
                if (!second) data["nextCursor"] = "c1";
                return FakeOmpProcess.Ok(cmd.Str("id"), "get_messages_page", data);
            };
            rig.Dialogs.OnSelect = (_, options) => options.First(o => o.EndsWith("older question"));
            rig.FromPage("{\"t\":\"sessions\"}");

            var sw = await rig.Proc.WaitForTypeAsync("switch_session");
            Assert.Equal(old, sw.Str("sessionPath"));
            await rig.Page.WaitForTypeAsync("clear");
            var history = await rig.Page.WaitForTypeAsync("history");
            var items = history.Child("items").Items().ToList();
            Assert.Equal(new[] { "user", "assistant" }, items.Select(i => i.Str("role")));
            Assert.Equal("older question", items[0].Str("text"));
            Assert.Equal("answer", items[1].Str("text"));
            Assert.Equal("anthropic/claude-a", items[1].Str("model"));
            Assert.Equal(1000, items[1].Int("started"));
            Assert.Equal(1500, items[1].Int("ended"));
            var pages = rig.Proc.ReceivedSnapshot().Where(f => f.Str("type") == "get_messages_page").ToList();
            Assert.Equal(2, pages.Count);
            Assert.Equal(256, pages[0].Int("limit"));
            Assert.Equal("c1", pages[1].Str("cursor"));
            _ = current; _ = named;
        }

        [Fact]
        public async Task The_current_session_and_a_cancelled_dialog_change_nothing()
        {
            var (rig, _, _, _) = await ConnectedAsync();
            using var owner = rig;
            rig.Dialogs.OnSelect = (_, options) => options.First(o => o.EndsWith("(current)"));
            rig.FromPage("{\"t\":\"sessions\"}");
            rig.Dialogs.OnSelect = (_, _) => null;
            rig.FromPage("{\"t\":\"sessions\"}");
            await Task.Delay(150);
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "switch_session");
            Assert.Empty(rig.Page.Parsed("clear"));
        }

        [Fact]
        public async Task New_conversation_in_the_list_asks_to_confirm_and_starts_a_new_session()
        {
            var (rig, _, _, _) = await ConnectedAsync();
            using var owner = rig;
            rig.Dialogs.OnSelect = (_, options) => options[0];
            rig.FromPage("{\"t\":\"sessions\"}");
            await rig.Proc.WaitForTypeAsync("new_session");
            Assert.Single(rig.Dialogs.ConfirmTitles);
            await rig.Page.WaitForTypeAsync("clear");
        }

        [Fact]
        public async Task The_list_is_not_offered_while_the_agent_is_working()
        {
            var (rig, _, _, _) = await ConnectedAsync();
            using var owner = rig;
            bool asked = false;
            rig.Dialogs.OnSelect = (_, _) => { asked = true; return null; };
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsBusy));
            rig.FromPage("{\"t\":\"sessions\"}");
            await Task.Delay(100);
            Assert.False(asked);
        }
    }
}
