using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    /// <summary>AI 패널이 시작할 때부터 떠 있어도 omp는 첫 메시지를 보낼 때 시작한다(지연 시작).</summary>
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
    }
}
