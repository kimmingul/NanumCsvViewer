using NanumCsvViewer.Agent;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    // 실제 Form1(보이지 않는 창)에서 채팅의 파일 추가가 쓰는 호스트: 작업 공간 파일 없이도 원본으로 올리고, 탭은 열지 않고, 같은 파일은 멱등이며,
    // DB 파일은 표 이름을 모두 돌려준다.
    [Collection("SavedViewStore")]
    public class ChatAttachFormTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_attach_" + Guid.NewGuid().ToString("N"));

        public ChatAttachFormTests() => Directory.CreateDirectory(_dir);

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static void Pump(Task task)
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(90)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
            Assert.True(task.IsCompleted, "task did not complete");
            task.GetAwaiter().GetResult();
        }

        private static T Await<T>(Task<T> task) { Pump(task); return task.Result; }

        private void OnForm(Action<Form1> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                Form1? form = null;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    form = new Form1(new AppSettings());
                    _ = form.Handle;
                    body(form);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { if (form is not null) typeof(Form1).GetMethod("DisposeWorkspaceUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null); } catch { }
                    try { form?.Dispose(); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(240)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private string Csv(string name, string content)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private string Db(string name)
        {
            string path = Path.Combine(_dir, name);
            var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE alpha (x INTEGER); CREATE TABLE beta (y INTEGER); INSERT INTO alpha VALUES (1);";
            cmd.ExecuteNonQuery();
            return path;
        }

        [Fact]
        public void Files_become_workspace_sources_without_tabs_or_a_workspace_file_and_adding_again_is_idempotent()
        {
            string a = Csv("orders.csv", "id,name\n1,kim\n2,lee\n");
            string b = Csv("people.csv", "id,city\n1,seoul\n");
            string db = Db("survey.db");
            string missing = Path.Combine(_dir, "gone.csv");
            OnForm(form =>
            {
                var host = form.CreateAgentAttachHost();
                Assert.Null(host.UnavailableReason);
                Assert.Null(form.WorkspaceFilePath);   // 저장하지 않은 작업 공간

                var outcome = Await(host.AddAsync(new[] { a, b, db, missing }, CancellationToken.None));

                Assert.Equal(new[] { "orders.csv", "people.csv", "survey.db" }, outcome.Added.Select(x => x.Name));
                Assert.Equal(new[] { "orders" }, outcome.Added[0].Tables);
                Assert.Equal(new[] { "survey.alpha", "survey.beta" }, outcome.Added[2].Tables);   // DB 파일은 표 이름을 모두
                Assert.Equal(Path.GetFullPath(a), outcome.Added[0].Path);
                Assert.Equal(("gone.csv", true), (Path.GetFileName(outcome.Failed.Single().File), outcome.Failed.Single().Error.Length > 0));
                Assert.Empty(form.Tabs);   // 탭을 열지 않는다
                Assert.Equal(3, form.Workspace!.Sources.Count);

                var again = Await(host.AddAsync(new[] { a, db }, CancellationToken.None));
                Assert.Equal(new[] { "orders.csv", "survey.db" }, again.Added.Select(x => x.Name));   // 칩은 다시 만든다
                Assert.Equal(outcome.Added[0].Tables, again.Added[0].Tables);
                Assert.Equal(3, form.Workspace.Sources.Count);                                           // 원본은 늘지 않는다
                Assert.Empty(form.Tabs);
            });
        }
    }
}
