using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Rpc;
using NanumCsvViewer.Agent.Chat;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// AI 패널이 보이면 앱이 한가해진 뒤 omp를 미리 시작하고(<c>Form1.TryPrewarmAgent</c>), 숨겨져 있으면 계속 미룬다.
    /// 진짜 패널(WebView2) 대신 가짜 omp를 붙인 컨트롤러를 넣고, 패널 표시는 분할의 접힘 상태로 만든다.
    /// </summary>
    [Collection("SavedViewStore")]
    public class AgentPrewarmFormTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_prewarm_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        static AgentPrewarmFormTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public AgentPrewarmFormTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static AppSettings Settings(bool python = false) => new()
        {
            StartupPanels = new PanelLayout { Agent = false },   // 진짜 패널이 만들어지지 않게
            AgentAllowLocalPython = python,
        };

        private string MakeCsv(string name)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, "id,name\n1,kim\n2,lee\n", new UTF8Encoding(false));
            return path;
        }

        private static void OnForm(AppSettings settings, Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Form1? form = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    form = new Form1(settings);
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
            Assert.True(thread.Join(TimeSpan.FromSeconds(240)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void PumpUntil(Func<bool> condition, string what, int seconds = 30)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) { Application.DoEvents(); Thread.Sleep(2); }
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Assert.True(condition(), "timed out waiting for: " + what);
        }

        private static void PumpFor(int ms)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(2); }
        }

        private static DocumentTab Open(Form1 f, string path)
        {
            var task = f.OpenFileTabAsync(path);
            PumpUntil(() => task.IsCompleted, "open " + Path.GetFileName(path));
            var tab = task.GetAwaiter().GetResult();
            Assert.NotNull(tab);
            PumpUntil(() => tab!.Document is { IndexingComplete: true } && !tab.IsIndexing, "indexing");
            return tab!;
        }

        private static object? Call(Form1 f, string method, params object[] args) => typeof(Form1).GetMethod(method, Inst)!.Invoke(f, args);
        private static void SetField(Form1 f, string field, object? value) => typeof(Form1).GetField(field, Inst)!.SetValue(f, value);
        private static T Field<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;
        private static bool TryPrewarm(Form1 f) => f.TryPrewarmAgent();

        private static void ShowPanel(Form1 f, bool visible) => Field<SplitContainer>(f, "_agentSplit").Panel2Collapsed = !visible;

        /// <summary>가짜 omp에 붙은 컨트롤러를 폼에 넣는다(폼이 만든 것처럼 지연 시작 상태).</summary>
        private ChatController Attach(Form1 form, FakeOmpFactory factory, bool python = false, string? omp = "C:\\fake\\omp.exe")
        {
            var options = new AgentHostOptions(Language: "en", AppVersion: "1.2.3", AllowLocalPython: python);
            var controller = new ChatController(new FakePage(), new FakeTools(), options,
                new ChatControllerServices
                {
                    ProcessFactory = factory,
                    Clock = new ManualClock(),
                    Dialogs = new FakeDialogs(),
                    Log = new CapturingLog(),
                    Ui = SynchronizationContext.Current,
                    AutoTick = false,
                    LocateOmp = _ => omp,
                    RunVersion = (_, _) => Task.FromResult<string?>("omp/18.4.4"),
                    ReadGuide = () => "# guide",
                    LocalPython = new FakePythonSetup(),
                    SkillRoot = Path.Combine(_dir, "skills"),
                });
            controller.StartOnFirstUse(() => (string)Call(form, "AgentWorkingDirectory")!);
            SetField(form, "_agentController", controller);
            return controller;
        }

        [Fact]
        public void A_hidden_panel_never_prewarms()
        {
            OnForm(Settings(), form =>
            {
                var factory = new FakeOmpFactory();
                var controller = Attach(form, factory);
                ShowPanel(form, false);

                Assert.False(TryPrewarm(form));
                PumpFor(200);

                Assert.True(controller.IsStartDeferred);
                Assert.Empty(factory.Processes);
                SetField(form, "_agentController", null);
                controller.Dispose();
            });
        }

        [Fact]
        public void A_visible_idle_panel_starts_omp_once_without_sending_anything()
        {
            OnForm(Settings(), form =>
            {
                var factory = new FakeOmpFactory();
                var controller = Attach(form, factory);
                ShowPanel(form, true);

                Assert.True(TryPrewarm(form));
                PumpUntil(() => controller.IsRunning, "omp connected");
                Assert.False(TryPrewarm(form));                                   // 이미 시작함: 두 번째는 아무것도 하지 않는다
                PumpFor(200);

                Assert.Single(factory.Processes);
                Assert.DoesNotContain(factory.Last.ReceivedSnapshot(), f => f.Str("type") == "prompt");
                SetField(form, "_agentController", null);
                controller.Dispose();
            });
        }

        [Fact]
        public void Prewarm_waits_while_a_workspace_open_or_a_file_open_is_in_progress_and_then_starts()
        {
            OnForm(Settings(), form =>
            {
                var factory = new FakeOmpFactory();
                var controller = Attach(form, factory);
                ShowPanel(form, true);

                SetField(form, "_wfBusy", true);                                  // 작업 공간 열기·저장 중
                Assert.False(TryPrewarm(form));
                Assert.True(controller.IsStartDeferred);
                Assert.True(Field<System.Windows.Forms.Timer>(form, "_agentPrewarmTimer").Enabled);   // 끝날 때까지 다시 잡는다
                SetField(form, "_wfBusy", false);

                var gate = Field<SemaphoreSlim>(form, "_openGate");               // 파일 열기 중
                gate.Wait();
                Assert.False(TryPrewarm(form));
                Assert.True(controller.IsStartDeferred);
                gate.Release();

                Assert.True(TryPrewarm(form));
                PumpUntil(() => controller.IsRunning, "omp connected");
                Assert.Single(factory.Processes);
                SetField(form, "_agentController", null);
                controller.Dispose();
            });
        }

        [Fact]
        public void Prewarm_uses_the_open_file_folder_and_opening_another_file_afterwards_does_not_restart_omp()
        {
            OnForm(Settings(), form =>
            {
                var factory = new FakeOmpFactory();
                var controller = Attach(form, factory);
                ShowPanel(form, true);
                Open(form, MakeCsv("a.csv"));

                Assert.True(TryPrewarm(form));
                PumpUntil(() => controller.IsRunning, "omp connected");
                Assert.Equal(_dir, factory.Last.Launch.WorkingDirectory);          // 시작하는 순간 열린 파일의 폴더(지연 시작과 같은 규칙)

                Open(form, MakeCsv("b.csv"));
                PumpFor(500);

                Assert.Single(factory.Processes);
                SetField(form, "_agentController", null);
                controller.Dispose();
            });
        }

        [Fact]
        public void With_local_python_the_prewarm_waits_for_the_first_data_file_so_opening_it_does_not_restart_omp()
        {
            OnForm(Settings(python: true), form =>
            {
                var factory = new FakeOmpFactory();
                var controller = Attach(form, factory, python: true);
                ShowPanel(form, true);

                Assert.False(TryPrewarm(form));                                   // 분석 폴더가 아직 정해지지 않았다
                Assert.True(controller.IsStartDeferred);

                Open(form, MakeCsv("a.csv"));
                Assert.True(TryPrewarm(form));
                PumpUntil(() => controller.IsRunning, "omp connected");
                Assert.Equal(AgentWorkspace.StableOutputFolder(null, Path.Combine(_dir, "a.csv")), factory.Last.Launch.WorkingDirectory);

                Open(form, MakeCsv("b.csv"));                                     // 분석 폴더는 처음 연 파일 기준으로 고정
                PumpFor(500);

                Assert.Single(factory.Processes);
                SetField(form, "_agentController", null);
                controller.Dispose();
            });
        }

        [Fact]
        public void A_message_sent_right_after_the_prewarm_is_delivered_by_the_same_omp()
        {
            OnForm(Settings(), form =>
            {
                var factory = new FakeOmpFactory();
                var controller = Attach(form, factory);
                ShowPanel(form, true);

                Assert.True(TryPrewarm(form));
                Assert.True(controller.Submit("hello"));                          // 연결 전에 들어온 첫 메시지
                PumpUntil(() => factory.Processes.Count > 0 && factory.Last.ReceivedSnapshot().Any(f => f.Str("type") == "prompt"), "prompt sent");
                PumpFor(200);

                Assert.Single(factory.Processes);
                Assert.Single(factory.Last.ReceivedSnapshot(), f => f.Str("type") == "prompt");
                SetField(form, "_agentController", null);
                controller.Dispose();
            });
        }
    }
}
