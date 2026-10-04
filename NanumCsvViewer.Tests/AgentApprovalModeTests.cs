using System.Text.Json;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    /// <summary>승인 모드(always-ask | write | yolo): 정책 표, host.yml, 모드 변경 재시작, yolo 확인, 앱 카드 자동 승인, 기본 모드 안내.</summary>
    public class AgentApprovalModeTests
    {
        private static AgentHostOptions Options(AgentApprovalMode mode, bool noticePending = false) =>
            new(Language: "en", AppVersion: "1.2.3", ApprovalMode: mode, ApprovalNoticePending: noticePending);

        private static async Task<ControllerRig> ConnectedAsync(AgentApprovalMode mode, bool noticePending = false)
        {
            var rig = new ControllerRig(Options(mode, noticePending));
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            return rig;
        }

        private static string HostYaml(FakeOmpProcess proc)
        {
            var args = proc.Launch.Arguments.ToList();
            return File.ReadAllText(args[args.IndexOf("--config") + 1]);
        }

        private static string ApprovalValue(string hostYaml)
        {
            using var doc = JsonDocument.Parse(hostYaml);
            return doc.RootElement.GetProperty("tools").GetProperty("approvalMode").GetString()!;
        }

        private static List<string> Notices(ControllerRig rig) => rig.Page.Parsed("notice").Select(n => n.Str("text")).ToList();

        // ---- 정책 표 --------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(AgentApprovalMode.AlwaysAsk, ApprovalKind.DataEdit, false)]
        [InlineData(AgentApprovalMode.AlwaysAsk, ApprovalKind.FileSave, false)]
        [InlineData(AgentApprovalMode.AlwaysAsk, ApprovalKind.RowSharing, false)]
        [InlineData(AgentApprovalMode.Write, ApprovalKind.DataEdit, true)]
        [InlineData(AgentApprovalMode.Write, ApprovalKind.FileSave, false)]
        [InlineData(AgentApprovalMode.Write, ApprovalKind.RowSharing, false)]
        [InlineData(AgentApprovalMode.Yolo, ApprovalKind.DataEdit, true)]
        [InlineData(AgentApprovalMode.Yolo, ApprovalKind.FileSave, true)]
        [InlineData(AgentApprovalMode.Yolo, ApprovalKind.RowSharing, false)]
        public void Policy_matrix(AgentApprovalMode mode, ApprovalKind kind, bool auto) =>
            Assert.Equal(auto, AgentApprovalPolicy.AutoApproves(mode, kind));

        [Fact]
        public void Parse_defaults_to_yolo_but_TryParse_rejects_unknown_values()
        {
            Assert.Equal(AgentApprovalMode.Yolo, AgentApprovalPolicy.Default);
            Assert.Equal(AgentApprovalMode.Yolo, AgentApprovalPolicy.Parse(null));
            Assert.Equal(AgentApprovalMode.Write, AgentApprovalPolicy.Parse(" Write "));
            Assert.Equal(AgentApprovalMode.AlwaysAsk, AgentApprovalPolicy.Parse("always-ask"));
            Assert.False(AgentApprovalPolicy.TryParse("bypass", out _));
            Assert.Equal("yolo", new AppSettings().AgentApprovalMode);
            Assert.False(new AppSettings().AgentApprovalNoticeShown);
        }

        // ---- host.yml --------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(AgentApprovalMode.AlwaysAsk, "always-ask")]
        [InlineData(AgentApprovalMode.Write, "write")]
        [InlineData(AgentApprovalMode.Yolo, "yolo")]
        public async Task The_launched_host_config_carries_the_mode_and_the_status_reports_it(AgentApprovalMode mode, string expected)
        {
            using var rig = await ConnectedAsync(mode);
            Assert.Equal(expected, ApprovalValue(HostYaml(rig.Proc)));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("approval") == expected);
        }

        // ---- 모드 변경 → 같은 세션으로 재시작 -------------------------------------------------------------------------

        [Fact]
        public async Task Changing_the_mode_while_idle_restarts_omp_with_the_new_host_config_and_reports_the_change()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.Yolo);
            var changes = new List<AgentApprovalMode>();
            rig.OnUi(() => rig.Controller.ApprovalModeChanged += changes.Add);
            Assert.Equal("yolo", ApprovalValue(HostYaml(rig.Proc)));

            Assert.True(rig.OnUi(() => rig.Controller.TrySetApprovalMode(AgentApprovalMode.AlwaysAsk)));   // yolo에서 내려가는 쪽은 확인 없음
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));

            Assert.Equal("always-ask", ApprovalValue(HostYaml(rig.Factory.Processes[1])));
            Assert.Equal(new[] { AgentApprovalMode.AlwaysAsk }, changes);
            Assert.Contains(Notices(rig), t => t.Contains("always-ask") && t.Contains("restarting"));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Last().Str("approval") == "always-ask");
        }

        [Fact]
        public async Task Changing_the_mode_while_the_agent_works_restarts_after_the_turn()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.AlwaysAsk);
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsBusy));

            Assert.True(rig.OnUi(() => rig.Controller.TrySetApprovalMode(AgentApprovalMode.Write)));
            await Task.Delay(200);
            Assert.Single(rig.Factory.Processes);                    // 작업 중에는 다시 시작하지 않는다
            Assert.Equal("always-ask", ApprovalValue(HostYaml(rig.Proc)));

            rig.Proc.Emit("{\"type\":\"agent_end\",\"isTerminal\":true}");
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Equal("write", ApprovalValue(HostYaml(rig.Factory.Processes[1])));
        }

        [Fact]
        public async Task Choosing_the_same_mode_does_not_restart()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.Write);
            Assert.True(rig.OnUi(() => rig.Controller.TrySetApprovalMode(AgentApprovalMode.Write)));
            await Task.Delay(200);
            Assert.Single(rig.Factory.Processes);
        }

        // ---- 채팅 선택(setApproval)과 yolo 확인 ----------------------------------------------------------------------

        [Fact]
        public async Task Yolo_asks_for_confirmation_and_cancel_keeps_the_old_mode()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.Write);
            var asked = new List<(string Title, string Message)>();
            rig.Dialogs.OnConfirm = (title, message) => { asked.Add((title, message)); return false; };
            var changes = new List<AgentApprovalMode>();
            rig.OnUi(() => rig.Controller.ApprovalModeChanged += changes.Add);
            int statusBefore = rig.Page.Parsed("status").Count;

            rig.FromPage("{\"t\":\"setApproval\",\"value\":\"yolo\"}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("status").Count > statusBefore);   // 선택을 이전 값으로 되돌리는 status

            Assert.Single(asked);
            Assert.Contains("Python", asked[0].Message);
            Assert.Equal(AgentApprovalMode.Write, rig.OnUi(() => rig.Controller.ApprovalMode));
            Assert.Equal("write", rig.Page.Parsed("status").Last().Str("approval"));
            Assert.Empty(changes);
            await Task.Delay(200);
            Assert.Single(rig.Factory.Processes);
        }

        [Fact]
        public async Task Yolo_confirmed_applies_the_mode_and_restarts()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.Write);
            int asked = 0;
            rig.Dialogs.OnConfirm = (_, _) => { asked++; return true; };
            var changes = new List<AgentApprovalMode>();
            rig.OnUi(() => rig.Controller.ApprovalModeChanged += changes.Add);

            rig.FromPage("{\"t\":\"setApproval\",\"value\":\"yolo\"}");
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);

            Assert.Equal(1, asked);
            Assert.Equal(new[] { AgentApprovalMode.Yolo }, changes);
            Assert.Equal("yolo", ApprovalValue(HostYaml(rig.Factory.Processes[1])));
        }

        [Fact]
        public async Task An_unknown_page_value_is_ignored_and_never_becomes_yolo()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.AlwaysAsk);
            int asked = 0;
            rig.Dialogs.OnConfirm = (_, _) => { asked++; return true; };
            rig.FromPage("{\"t\":\"setApproval\",\"value\":\"bypass\"}");
            await Task.Delay(200);
            Assert.Equal(0, asked);
            Assert.Equal(AgentApprovalMode.AlwaysAsk, rig.OnUi(() => rig.Controller.ApprovalMode));
            Assert.Single(rig.Factory.Processes);
        }

        // ---- 앱 카드 정책 ---------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(AgentApprovalMode.AlwaysAsk, ApprovalKind.DataEdit, false)]
        [InlineData(AgentApprovalMode.AlwaysAsk, ApprovalKind.FileSave, false)]
        [InlineData(AgentApprovalMode.AlwaysAsk, ApprovalKind.RowSharing, false)]
        [InlineData(AgentApprovalMode.Write, ApprovalKind.DataEdit, true)]
        [InlineData(AgentApprovalMode.Write, ApprovalKind.FileSave, false)]
        [InlineData(AgentApprovalMode.Write, ApprovalKind.RowSharing, false)]
        [InlineData(AgentApprovalMode.Yolo, ApprovalKind.DataEdit, true)]
        [InlineData(AgentApprovalMode.Yolo, ApprovalKind.FileSave, true)]
        [InlineData(AgentApprovalMode.Yolo, ApprovalKind.RowSharing, false)]
        public async Task App_cards_follow_the_mode(AgentApprovalMode mode, ApprovalKind kind, bool auto)
        {
            using var rig = await ConnectedAsync(mode);
            Task<bool> answer = default!;
            rig.OnUi(() => answer = rig.Controller.ApproveAsync("Edit 2 cells", "2 changes", new[] { "- a", "+ b" }, CancellationToken.None, kind));

            string modeName = AgentApprovalPolicy.ToOmp(mode);
            if (auto)
            {
                Assert.True(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Empty(rig.Page.Parsed("approval"));
                Assert.Contains(Notices(rig), t => t == $"Auto-approved: Edit 2 cells (mode: {modeName})");
                lock (rig.Log.Lines) Assert.Contains(rig.Log.Lines, n => n.StartsWith("! ") && n.Contains("Edit 2 cells") && n.Contains(modeName));
            }
            else
            {
                var card = await rig.Page.WaitForTypeAsync("approval");
                Assert.False(answer.IsCompleted);
                Assert.DoesNotContain(Notices(rig), t => t.StartsWith("Auto-approved"));
                rig.FromPage($"{{\"t\":\"approval\",\"id\":\"{card.Str("id")}\",\"ok\":false}}");
                Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public async Task The_card_default_kind_is_the_strictest_so_it_always_asks()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.Yolo);
            Task<bool> answer = default!;
            rig.OnUi(() => answer = rig.Controller.ApproveAsync("t", "s", new[] { "x" }, CancellationToken.None));
            await rig.Page.WaitForTypeAsync("approval");
            Assert.False(answer.IsCompleted);
        }

        [Fact]
        public async Task A_mode_change_applies_to_app_cards_immediately_even_before_the_restart_finishes()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.AlwaysAsk);
            rig.Proc.Emit("{\"type\":\"agent_start\"}");
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsBusy));
            rig.OnUi(() => rig.Controller.TrySetApprovalMode(AgentApprovalMode.Write));

            Task<bool> answer = default!;
            rig.OnUi(() => answer = rig.Controller.ApproveAsync("Delete column", "", new[] { "x" }, CancellationToken.None, ApprovalKind.DataEdit));
            Assert.True(await answer.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        // ---- 기본 모드 안내(한 번) -----------------------------------------------------------------------------------

        [Fact]
        public async Task The_default_mode_notice_is_posted_once_without_a_dialog()
        {
            int dialogs = 0;
            using var rig = new ControllerRig(Options(AgentApprovalMode.Yolo, noticePending: true));
            rig.Dialogs.OnConfirm = (_, _) => { dialogs++; return true; };
            int shown = 0;
            rig.OnUi(() => rig.Controller.ApprovalNoticeShown += () => shown++);
            await rig.StartAsync();

            string notice = Assert.Single(Notices(rig), t => t.StartsWith("Approval mode: allow everything (yolo)"));
            Assert.Contains("without asking", notice);
            Assert.Contains("settings", notice);
            Assert.Equal(1, shown);
            Assert.Equal(0, dialogs);

            rig.OnUi(() => _ = rig.Controller.RestartAsync());
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2);
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            Assert.Single(Notices(rig), t => t.StartsWith("Approval mode: allow everything (yolo)"));
            Assert.Equal(1, shown);
        }

        [Fact]
        public async Task No_notice_when_the_user_already_chose_a_mode()
        {
            using var rig = await ConnectedAsync(AgentApprovalMode.Yolo, noticePending: false);
            Assert.DoesNotContain(Notices(rig), t => t.StartsWith("Approval mode:"));
        }
    }
}
