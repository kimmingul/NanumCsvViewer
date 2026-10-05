using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// AI 패널이 시작할 때부터 떠 있어도 omp는 바로 시작하지 않는다(지연 시작): 첫 메시지를 보낼 때, 또는 호스트가 앱이 한가해진 뒤
    /// <see cref="ChatController.StartDeferredNow"/>로 미리 데울 때 시작한다.
    /// </summary>
    public class AgentLazyStartTests
    {
        [Fact]
        public async Task Nothing_is_started_until_the_first_message_and_the_page_may_already_send()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));
            await Task.Delay(300);

            Assert.Empty(rig.Factory.Processes);                      // omp 프로세스 없음
            Assert.False(rig.OnUi(() => rig.Controller.IsRunning));
            Assert.True(rig.OnUi(() => rig.Controller.IsStartDeferred));
            var status = rig.Page.Parsed("status").Last();
            Assert.False(status.Bool("connected"));
            Assert.True(status.Bool("ready"));                        // 입력창은 보내기를 허용한다
        }

        [Fact]
        public async Task The_first_message_starts_omp_and_is_sent_once_connected()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));

            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"summarize\"}");
            var ack = await rig.Page.WaitForTypeAsync("submitted");
            Assert.True(ack.Bool("ok"));                              // 페이지는 보냈다고 알고 입력창을 비운다

            var prompt = await rig.Proc.WaitForTypeAsync("prompt");
            Assert.Equal("summarize", prompt.Str("message"));
            Assert.Single(rig.Factory.Processes);
            Assert.False(rig.OnUi(() => rig.Controller.IsStartDeferred));
            Assert.Equal(rig.WorkDir, rig.Proc.Launch.WorkingDirectory);
        }

        [Fact]
        public async Task The_working_folder_is_resolved_when_omp_starts_not_when_the_panel_opened()
        {
            using var rig = new ControllerRig();
            string later = Path.Combine(Path.GetTempPath(), "ncv-lazy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(later);
            try
            {
                string dir = rig.WorkDir;
                rig.OnUi(() => rig.Controller.StartOnFirstUse(() => dir));
                dir = later;                                          // 패널을 연 뒤 다른 폴더의 파일을 열었다
                rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"hi\"}");
                await rig.Proc.WaitForTypeAsync("prompt");
                Assert.Equal(later, rig.Proc.Launch.WorkingDirectory);
            }
            finally { try { Directory.Delete(later); } catch { } }
        }

        [Fact]
        public async Task A_failed_start_drops_the_message_with_a_notice_and_does_not_retry_silently()
        {
            using var rig = new ControllerRig { OmpPath = null };     // omp 없음
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));

            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"hello\"}");
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("text").Contains("not sent")));
            Assert.Empty(rig.Factory.Processes);
            Assert.False(rig.OnUi(() => rig.Controller.IsStartDeferred));
            Assert.False(rig.Page.Parsed("status").Last().Bool("ready"));
        }

        [Fact]
        public async Task Restarting_for_changed_settings_does_not_start_a_deferred_agent()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));

            await rig.Ui.InvokeAsync(() => rig.Controller.RestartAsync());   // 설정에서 omp 경로를 바꿨을 때 부르는 경로
            rig.OnUi(() => rig.Controller.Options = rig.Controller.Options with { MaxRowsPerRequest = 77 });
            await Task.Delay(300);

            Assert.Empty(rig.Factory.Processes);
            Assert.True(rig.OnUi(() => rig.Controller.IsStartDeferred));
        }

        [Fact]
        public async Task Switching_workspace_before_first_use_keeps_it_deferred()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));

            await rig.Ui.InvokeAsync(() => rig.Controller.SwitchWorkspaceAsync(
                rig.Controller.Options, AgentWorkspaceContext.ForFile(null, Path.Combine(rig.WorkDir, "w.ncvws")), WorkspaceConversation.None));
            await Task.Delay(300);

            Assert.Empty(rig.Factory.Processes);
            Assert.True(rig.OnUi(() => rig.Controller.IsStartDeferred));
        }

        [Fact]
        public async Task StartDeferredNow_connects_without_a_message_so_the_models_are_requested_and_nothing_is_prompted()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));

            Assert.True(rig.OnUi(() => rig.Controller.StartDeferredNow()));
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            await rig.Proc.WaitForTypeAsync("get_available_models");        // 선택이 채워질 목록은 연결 직후 요청된다

            Assert.Single(rig.Factory.Processes);
            Assert.Equal(rig.WorkDir, rig.Proc.Launch.WorkingDirectory);
            Assert.False(rig.OnUi(() => rig.Controller.IsStartDeferred));
            Assert.True(rig.Page.Parsed("status").Last().Bool("connected"));
            Assert.DoesNotContain(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "prompt");
        }

        [Fact]
        public async Task StartDeferredNow_starts_only_once_and_does_nothing_when_not_deferred()
        {
            using var rig = new ControllerRig();
            Assert.False(rig.OnUi(() => rig.Controller.StartDeferredNow()));    // StartOnFirstUse 전: 미뤄 둔 것이 없다
            Assert.Empty(rig.Factory.Processes);

            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));
            Assert.True(rig.OnUi(() => rig.Controller.StartDeferredNow()));
            Assert.False(rig.OnUi(() => rig.Controller.StartDeferredNow()));    // 시작 중: 다시 시작하지 않는다
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));
            Assert.False(rig.OnUi(() => rig.Controller.StartDeferredNow()));    // 연결 뒤에도
            await Task.Delay(300);

            Assert.Single(rig.Factory.Processes);
        }

        [Fact]
        public async Task A_first_message_that_starts_omp_makes_a_later_prewarm_a_no_op()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"first\"}");

            Assert.False(rig.OnUi(() => rig.Controller.StartDeferredNow()));    // 메시지로 이미 시작함
            var prompt = await rig.Proc.WaitForTypeAsync("prompt");
            await Task.Delay(300);

            Assert.Equal("first", prompt.Str("message"));
            Assert.Single(rig.Factory.Processes);
            Assert.Single(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "prompt");
        }

        [Fact]
        public async Task A_message_sent_while_the_prewarmed_omp_is_still_connecting_is_kept_and_sent_once()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));

            bool accepted = false;
            rig.OnUi(() =>
            {
                Assert.True(rig.Controller.StartDeferredNow());
                accepted = rig.Controller.Submit("while connecting");          // 아직 연결 전
            });
            Assert.True(accepted);
            Assert.True(rig.Page.Parsed("status").Last().Bool("ready"));         // 연결 중에도 입력창은 보내기를 허용한다

            var prompt = await rig.Proc.WaitForTypeAsync("prompt");
            await Task.Delay(300);

            Assert.Equal("while connecting", prompt.Str("message"));
            Assert.Single(rig.Factory.Processes);
            Assert.Single(rig.Proc.ReceivedSnapshot(), f => f.Str("type") == "prompt");
            Assert.DoesNotContain(rig.Page.Parsed("notice"), n => n.Str("text").Contains("not connected"));
        }

        [Fact]
        public async Task A_failed_prewarm_reports_in_the_chat_without_a_dialog_and_leaves_the_page_not_ready()
        {
            using var rig = new ControllerRig { OmpPath = null };             // omp 없음
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));

            Assert.True(rig.OnUi(() => rig.Controller.StartDeferredNow()));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("notice").Any(n => n.Str("text").Contains("omp (oh-my-pi) was not found")));

            Assert.Empty(rig.Factory.Processes);
            Assert.Empty(rig.Dialogs.ConfirmTitles);
            Assert.False(rig.OnUi(() => rig.Controller.IsStartDeferred));
            var status = rig.Page.Parsed("status").Last();
            Assert.True(status.Bool("error"));
            Assert.False(status.Bool("ready"));
        }

        [Fact]
        public async Task Switching_workspace_after_a_prewarm_restarts_once_on_the_workspace_conversation()
        {
            using var rig = new ControllerRig();
            rig.OnUi(() => rig.Controller.StartOnFirstUse(() => rig.WorkDir));
            rig.OnUi(() => rig.Controller.StartDeferredNow());
            await rig.WaitUntilAsync(() => rig.OnUi(() => rig.Controller.IsRunning));

            await rig.Ui.InvokeAsync(() => rig.Controller.SwitchWorkspaceAsync(
                rig.Controller.Options, AgentWorkspaceContext.ForFile(null, Path.Combine(rig.WorkDir, "w.ncvws")), WorkspaceConversation.None));
            await rig.WaitUntilAsync(() => rig.Factory.Processes.Count == 2 && rig.OnUi(() => rig.Controller.IsRunning));
            await Task.Delay(300);

            Assert.Equal(2, rig.Factory.Processes.Count);                       // 열린 것을 다시 시작한 한 번뿐
            Assert.True(rig.Factory.Processes[0].Killed);
        }
    }
}
