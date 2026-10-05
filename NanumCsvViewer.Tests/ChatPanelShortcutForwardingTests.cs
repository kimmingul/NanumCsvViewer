using System.Windows.Forms;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer.Tests
{
    /// <summary>채팅 입력창(WebView2)에 포커스가 있어도 앱 단축키는 폼으로 넘어가고, 입력창의 편집 키는 넘어가지 않는다.</summary>
    public class ChatPanelShortcutForwardingTests
    {
        [Theory]
        [InlineData(Keys.Control | Keys.Oemcomma)]            // 설정
        [InlineData(Keys.Control | Keys.Shift | Keys.W)]      // 탐색기
        [InlineData(Keys.Control | Keys.Shift | Keys.F)]      // 고급 필터
        [InlineData(Keys.Control | Keys.Alt | Keys.S)]        // 정렬 해제
        [InlineData(Keys.Control | Keys.S)]                   // 작업 공간 저장
        [InlineData(Keys.Control | Keys.Tab)]                 // 다음 탭
        [InlineData(Keys.F4)]                                 // 행 상세
        [InlineData(Keys.F1)]                                 // 사용법
        public void App_shortcuts_are_handed_to_the_form(Keys key) => Assert.True(AgentChatPanel.ForwardsToApp(key));

        [Theory]
        [InlineData(Keys.A)]                                  // 일반 글자
        [InlineData(Keys.Shift | Keys.A)]
        [InlineData(Keys.Enter)]
        [InlineData(Keys.Control | Keys.Enter)]               // 채팅의 턴 뒤 전송
        [InlineData(Keys.Control | Keys.A)]                   // 입력창의 편집 키
        [InlineData(Keys.Control | Keys.C)]
        [InlineData(Keys.Control | Keys.V)]
        [InlineData(Keys.Control | Keys.X)]
        [InlineData(Keys.Control | Keys.Z)]
        [InlineData(Keys.Control | Keys.Y)]
        [InlineData(Keys.Control | Keys.Shift | Keys.Z)]
        [InlineData(Keys.Control | Keys.Back)]
        [InlineData(Keys.Control | Keys.Left)]
        [InlineData(Keys.Control | Keys.ControlKey)]          // 수정 키만 눌림
        public void Typing_and_input_editing_keys_stay_with_the_chat_input(Keys key) => Assert.False(AgentChatPanel.ForwardsToApp(key));
    }
}
