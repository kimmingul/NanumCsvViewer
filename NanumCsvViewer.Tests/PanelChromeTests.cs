using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer.Tests
{
    /// <summary>패널 머리글·분할선의 공통 규격(PanelChrome): 채팅 페이지로 가는 값과 메인 창에 실제로 적용된 크기.</summary>
    [Collection("SavedViewStore")]
    public class PanelChromeTests
    {
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_chat_page_gets_the_same_header_spec_as_the_winforms_panels(bool dark)
        {
            var p = ThemePalette.For(dark ? AppTheme.Dark : AppTheme.Light);
            using var doc = JsonDocument.Parse(ChatTheme.ThemeMessage(dark, "Segoe UI", 9f));
            var vars = doc.RootElement.GetProperty("vars");

            Assert.Equal(PanelChrome.HeaderHeight + "px", vars.GetProperty("chromeH").GetString());
            Assert.Equal(PanelChrome.Hex(p.ToolStrip), vars.GetProperty("chromeBg").GetString());
            Assert.Equal(PanelChrome.Hex(p.Border), vars.GetProperty("chromeBorder").GetString());
            Assert.Equal(PanelChrome.Hex(p.Text), vars.GetProperty("chromeFg").GetString());
            Assert.Equal(PanelChrome.FontPx + "px", vars.GetProperty("chromeFs").GetString());
            Assert.Equal(PanelChrome.FontWeight.ToString(), vars.GetProperty("chromeFw").GetString());
            // 머리글 글자는 9pt(= 96 DPI에서 12px).
            Assert.Equal((int)Math.Round(PanelChrome.FontPoints * 96f / 72f), PanelChrome.FontPx);
        }

        [Fact]
        public void Light_and_dark_headers_use_different_palette_colours_but_the_same_geometry()
        {
            var light = PanelChrome.CssVars(ThemePalette.Light);
            var dark = PanelChrome.CssVars(ThemePalette.Dark);
            Assert.NotEqual(light["chromeBg"], dark["chromeBg"]);
            Assert.NotEqual(light["chromeBorder"], dark["chromeBorder"]);
            Assert.Equal(light["chromeH"], dark["chromeH"]);
            Assert.Equal(light["chromeFs"], dark["chromeFs"]);
        }

        private static void OnForm(Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Form1? form = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    form = new Form1(new AppSettings { StartupPanels = new PanelLayout { Agent = false } });
                    _ = form.Handle;
                    form.ClientSize = new Size(1200, 700);
                    form.LayoutReady = true;
                    body(form);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { if (form is not null) typeof(Form1).GetMethod("DisposeWorkspaceUi", Inst)!.Invoke(form, null); } catch { }
                    try { form?.Dispose(); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static T Get<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;

        [Fact]
        public void The_explorer_header_the_cell_bar_and_the_detail_header_are_the_same_height_with_one_divider_width()
        {
            OnForm(form =>
            {
                form.SetPanelVisible(PanelKind.Explorer, true);
                form.SetPanelVisible(PanelKind.Detail, true);
                int header = form.LogicalToDeviceUnits(PanelChrome.HeaderHeight);
                int split = form.LogicalToDeviceUnits(PanelChrome.SplitterWidth);

                var explorerBar = form.Controls.Find("workspaceBar", true).Single();
                Assert.Equal(header, explorerBar.Height);

                // 셀 줄: 위 칸 + 분할 면(맨 아래 1px이 머리글의 경계선) = 머리글 높이.
                var cellBar = Get<SplitContainer>(form, "splitContainer1");
                Assert.Equal(header, cellBar.SplitterDistance + cellBar.SplitterWidth);

                Assert.Equal(header, Get<Label>(form, "detailHeaderLabel").Height);

                foreach (var sc in new[] { Get<SplitContainer>(form, "outerSplit"), cellBar })
                    Assert.Equal(split, sc.SplitterWidth);
                Assert.Equal(split, Get<Splitter>(form, "workspaceSplitter").Width);
            });
        }

        [Fact]
        public void The_ai_splitter_uses_the_same_width_once_the_split_exists()
        {
            OnForm(form =>
            {
                var agent = Get<SplitContainer>(form, "_agentSplit");
                Assert.Equal(form.LogicalToDeviceUnits(PanelChrome.SplitterWidth), agent.SplitterWidth);
            });
        }

        [Fact]
        public void The_address_box_has_no_border_and_the_value_bar_keeps_its_input_look()
        {
            OnForm(form =>
            {
                var address = Get<TextBox>(form, "cellAddressBox");
                var value = Get<TextBox>(form, "cellValueTextBox");
                Assert.Equal(BorderStyle.None, address.BorderStyle);
                Assert.Equal(BorderStyle.FixedSingle, value.BorderStyle);
                Assert.IsType<ChromeAddressHost>(address.Parent);

                // 테마를 바꿔도(ThemeManager.Apply 경로) 주소 상자는 테두리가 없고 머리글 배경이다.
                typeof(Form1).GetMethod("ApplyTheme", Inst)!.Invoke(form, new object[] { AppTheme.Dark });
                Assert.Equal(BorderStyle.None, address.BorderStyle);
                Assert.Equal(ThemePalette.Dark.ToolStrip, address.BackColor);
                Assert.Equal(BorderStyle.FixedSingle, value.BorderStyle);
                Assert.Equal(ThemePalette.Dark.Surface, value.BackColor);
            });
        }

        [Fact]
        public void The_address_underline_shows_only_while_the_box_has_focus()
        {
            OnForm(form =>
            {
                // 포커스는 창이 보여야 의미가 있으므로 투명한 창을 실제로 띄운다.
                form.Opacity = 0;
                form.ShowInTaskbar = false;
                form.Show();
                try
                {
                    var host = (ChromeAddressHost)Get<ChromeAddressHost>(form, "cellAddressHost");
                    var address = Get<TextBox>(form, "cellAddressBox");
                    Assert.False(host.Underlined);

                    form.Activate();
                    address.Focus();
                    Application.DoEvents();
                    if (!address.Focused) return;   // 창 활성화가 막힌 환경(원격 세션 등)에서는 확인할 수 없다
                    Assert.True(host.Underlined);

                    Get<DataGridView>(form, "grid").Focus();
                    Application.DoEvents();
                    Assert.False(host.Underlined);
                }
                finally { form.Hide(); }
            });
        }
    }
}
