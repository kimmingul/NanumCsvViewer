using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// 여러 시트 통합 문서의 시트 탭 띠(와 검사 결과·패싯 패널)는 그리드 영역 안에만 있어야 한다:
    /// 왼쪽 작업 공간 탐색기·오른쪽 AI 패널·행 상세 패널 밑으로 펼쳐지지 않고, 패널을 켜고 끄거나 시트를 바꿔도 그리드를 따라간다.
    /// </summary>
    [Collection("SavedViewStore")]
    public class SheetTabLayoutTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_sheetlayout_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        public SheetTabLayoutTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static T Get<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;
        private static object? Invoke(Form1 f, string method, params object[] args) => typeof(Form1).GetMethod(method, Inst)!.Invoke(f, args);

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 30)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { Application.DoEvents(); Thread.Sleep(2); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Assert.True(condition(), "timed out waiting for: " + what);
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
                    form = new Form1(new AppSettings { StartupPanels = new PanelLayout { Agent = false, Detail = false, Facets = false, Explorer = false } });
                    _ = form.Handle;
                    form.StartPosition = FormStartPosition.Manual;
                    form.ShowInTaskbar = false;
                    form.Location = new Point(-32000, -32000);                 // 화면 밖에서 실제로 보이게 해야 도킹·Visible이 실제 앱과 같다
                    form.Show();
                    form.ClientSize = new Size(1400, 800);
                    form.LayoutReady = true;
                    form.PerformLayout();
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
            Assert.True(thread.Join(TimeSpan.FromSeconds(240)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>폼 클라이언트 좌표의 경계(부모 사슬의 위치를 더한다 — 창이 보이지 않아도 같다).</summary>
        private static Rectangle InForm(Form f, Control c)
        {
            int x = 0, y = 0;
            for (Control? cur = c; cur is not null && !ReferenceEquals(cur, f); cur = cur.Parent) { x += cur.Left; y += cur.Top; }
            return new Rectangle(x, y, c.Width, c.Height);
        }

        private static void CreateThreeSheetXlsx(string path)
        {
            using var fs = File.Create(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            void W(string name, string content)
            {
                using var s = zip.CreateEntry(name).Open();
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
            const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            string Sheet(string header, string value) => Xml + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                $"<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{header}</t></is></c></row>" +
                $"<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>{value}</t></is></c></row></sheetData></worksheet>";
            W("[Content_Types].xml", Xml +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                string.Concat(Enumerable.Range(1, 3).Select(i => $"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");
            W("_rels/.rels", Xml +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            W("xl/workbook.xml", Xml +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Alpha\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Beta\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"Gamma\" sheetId=\"3\" r:id=\"rId3\"/></sheets></workbook>");
            W("xl/_rels/workbook.xml.rels", Xml +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(Enumerable.Range(1, 3).Select(i => $"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>")) +
                "</Relationships>");
            W("xl/worksheets/sheet1.xml", Sheet("a", "1"));
            W("xl/worksheets/sheet2.xml", Sheet("b", "2"));
            W("xl/worksheets/sheet3.xml", Sheet("c", "3"));
        }

        private static void Idle(Form1 f) => PumpUntil(() => !Get<bool>(f, "_busy") && !Get<bool>(f, "_indexing"), "idle");

        /// <summary>시트 탭 띠가 그리드 바로 아래, 그리드 영역 안에만 있고 어떤 옆 패널과도 겹치지 않는다.</summary>
        private static void AssertStripUnderGridOnly(Form1 form, string when)
        {
            form.PerformLayout();
            var strip = Get<FlowLayoutPanel>(form, "_sheetTabs");
            var gridArea = Get<SplitContainer>(form, "splitContainer1").Panel2;
            var grid = Get<DataGridView>(form, "grid");
            var outer = Get<SplitContainer>(form, "outerSplit");
            Assert.True(strip.Visible, when + ": strip visible");
            Assert.Same(gridArea, strip.Parent);
            Assert.Same(gridArea, grid.Parent);

            Rectangle s = InForm(form, strip), g = InForm(form, grid), area = InForm(form, gridArea);
            Assert.True(area.Contains(s), $"{when}: strip {s} inside grid area {area}");
            Assert.Equal(g.Left, s.Left);
            Assert.Equal(g.Width, s.Width);
            Assert.Equal(g.Bottom, s.Top);                                     // 그리드 바로 아래
            Assert.True(s.Width > 0 && strip.Height > 0);

            // 탐색기·AI 패널·행 상세 패널(보이는 것만)과 겹치지 않는다.
            var dock = Get<Panel>(form, "workspaceDockHost");
            if (dock.Visible) Assert.False(s.IntersectsWith(InForm(form, dock)), when + ": strip overlaps the explorer");
            var agent = Get<SplitContainer?>(form, "_agentSplit");
            if (agent is { Panel2Collapsed: false }) Assert.False(s.IntersectsWith(InForm(form, agent.Panel2)), when + ": strip overlaps the AI panel");
            if (!outer.Panel2Collapsed) Assert.False(s.IntersectsWith(InForm(form, outer.Panel2)), when + ": strip overlaps the row detail panel");
        }

        [Fact]
        public void Sheet_strip_stays_under_the_grid_while_panels_toggle_and_sheets_switch()
        {
            string xlsx = Path.Combine(_dir, "book.xlsx");
            CreateThreeSheetXlsx(xlsx);
            OnForm(form =>
            {
                var tab = form.OpenFileTabAsync(xlsx);
                PumpUntil(() => tab.IsCompleted, "open");
                Assert.NotNull(tab.Result);
                Idle(form);
                Assert.Equal(3, Get<FlowLayoutPanel>(form, "_sheetTabs").Controls.Count);

                AssertStripUnderGridOnly(form, "initial");

                form.SetPanelVisible(PanelKind.Explorer, true);
                Assert.True(Get<Panel>(form, "workspaceDockHost").Visible);
                AssertStripUnderGridOnly(form, "explorer on");

                var agent = Get<SplitContainer>(form, "_agentSplit");
                agent.Panel2Collapsed = false;                                   // 실제 채팅 패널(WebView2)은 만들지 않고 자리만 연다
                agent.SplitterDistance = Math.Max(agent.Panel1MinSize, agent.Width - 360);
                AssertStripUnderGridOnly(form, "AI panel on");

                form.SetPanelVisible(PanelKind.Detail, true);
                Assert.False(Get<SplitContainer>(form, "outerSplit").Panel2Collapsed);
                AssertStripUnderGridOnly(form, "detail on");

                // 크기 변경: 탐색기 폭과 AI 패널 폭을 바꿔도 따라간다.
                Get<Panel>(form, "workspaceDockHost").Width = 340;
                agent.SplitterDistance = Math.Max(agent.Panel1MinSize, agent.Width - 500);
                AssertStripUnderGridOnly(form, "resized panels");

                // 시트 전환(탭 전환 포함).
                Invoke(form, "SwitchSheet", 2);
                PumpUntil(() => Get<int>(form, "_currentSheetIndex") == 2 && !Get<bool>(form, "_busy") && !Get<bool>(form, "_indexing"), "sheet 3");
                AssertStripUnderGridOnly(form, "sheet switched");

                form.SetPanelVisible(PanelKind.Explorer, false);
                agent.Panel2Collapsed = true;
                form.SetPanelVisible(PanelKind.Detail, false);
                AssertStripUnderGridOnly(form, "all panels off");
            });
        }

        [Fact]
        public void Findings_and_facets_panels_also_live_inside_the_grid_area_with_the_strip_right_under_the_grid()
        {
            string xlsx = Path.Combine(_dir, "book2.xlsx");
            CreateThreeSheetXlsx(xlsx);
            OnForm(form =>
            {
                var tab = form.OpenFileTabAsync(xlsx);
                PumpUntil(() => tab.IsCompleted, "open");
                Idle(form);
                form.SetPanelVisible(PanelKind.Explorer, true);
                Get<SplitContainer>(form, "_agentSplit").Panel2Collapsed = false;
                form.SetPanelVisible(PanelKind.Facets, true);
                form.SetPanelVisible(PanelKind.Findings, true);
                form.PerformLayout();

                var gridArea = Get<SplitContainer>(form, "splitContainer1").Panel2;
                var strip = Get<FlowLayoutPanel>(form, "_sheetTabs");
                var quality = Get<Control>(form, "_qualityPanel");
                var facets = Get<Control>(form, "_facetsPanel");
                var grid = Get<DataGridView>(form, "grid");
                Assert.Same(gridArea, quality.Parent);
                Assert.Same(gridArea, facets.Parent);
                Assert.True(facets.Visible && quality.Visible);

                Rectangle s = InForm(form, strip), q = InForm(form, quality), f = InForm(form, facets), g = InForm(form, grid), area = InForm(form, gridArea);
                Assert.True(area.Contains(q) && area.Contains(f) && area.Contains(s));
                Assert.Equal(g.Bottom, s.Top);                                   // 시트 탭은 그리드 바로 아래
                Assert.True(q.Top >= s.Bottom);                                  // 검사 결과는 그 아래
                Assert.True(s.Right <= f.Left && g.Right <= f.Left);             // 패싯은 그리드 오른쪽(시트 탭 띠는 패싯 밑으로 가지 않는다)
                Assert.True(f.Bottom <= q.Top && q.Left <= g.Left && q.Right >= f.Right);   // 검사 결과는 맨 아래, 그리드+패싯 폭 전체
                Assert.False(q.IntersectsWith(InForm(form, Get<Panel>(form, "workspaceDockHost"))));
                Assert.False(q.IntersectsWith(InForm(form, Get<SplitContainer>(form, "_agentSplit").Panel2)));
            });
        }
    }
}
