using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    /// <summary>임시 폴더에 omp의 agent.db 흉내를 만든다. 실제 ~/.omp는 건드리지 않는다.</summary>
    internal sealed class FakeAgentDir : IDisposable
    {
        public const string OmpSchema = "CREATE TABLE model_usage (model_key TEXT PRIMARY KEY, last_used_at INTEGER NOT NULL DEFAULT (CAST(strftime('%s','now') AS INTEGER)))";

        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ncv-agentdir-" + Guid.NewGuid().ToString("N"));
        public string Db => Path.Combine(Dir, "agent.db");

        public FakeAgentDir() => Directory.CreateDirectory(Dir);

        public static SqliteConnection Open(string db)
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ConnectionString);
            conn.Open();
            return conn;
        }

        public static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        /// <summary>omp와 같은 스키마로 만들고 행을 넣는다(키, 유닉스 초). 닫은 뒤의 상태다.</summary>
        public FakeAgentDir WithRows(params (string Key, long Seconds)[] rows) => WithSchema(OmpSchema, rows.Select(r => (r.Key, (object)r.Seconds)).ToArray());

        public FakeAgentDir WithSchema(string createSql, params (string Key, object Value)[] rows)
        {
            using var conn = Open(Db);
            Exec(conn, createSql);
            foreach (var (key, value) in rows)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO model_usage VALUES ($k, $v)";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            return this;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    public class OmpModelUsageReaderTests
    {
        private const long T0 = 1_791_000_000;   // 2026-10 무렵

        [Fact]
        public void Reads_omp_schema_rows_as_utc_times()
        {
            using var dir = new FakeAgentDir().WithRows(("anthropic/claude-a", T0), ("openai/gpt-x", T0 + 60));
            var r = OmpModelUsage.Read(dir.Dir);

            Assert.Equal(OmpModelUsage.Status.Ok, r.Status);
            Assert.Equal(new[] { "openai/gpt-x", "anthropic/claude-a" }, r.Items.Select(i => i.Key));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(T0 + 60).UtcDateTime, r.Items[0].LastUsedUtc);
            Assert.Equal(DateTimeKind.Utc, r.Items[0].LastUsedUtc.Kind);
        }

        [Fact]
        public void An_empty_table_is_ok_with_no_items()
        {
            using var dir = new FakeAgentDir().WithRows();
            var r = OmpModelUsage.Read(dir.Dir);
            Assert.Equal(OmpModelUsage.Status.Ok, r.Status);
            Assert.Empty(r.Items);
        }

        [Fact]
        public void A_database_that_omp_still_has_open_in_wal_mode_is_readable_including_uncheckpointed_rows()
        {
            using var dir = new FakeAgentDir();
            using var omp = FakeAgentDir.Open(dir.Db);   // 쓰는 쪽(omp)은 계속 열려 있다
            FakeAgentDir.Exec(omp, "PRAGMA journal_mode = wal");
            FakeAgentDir.Exec(omp, FakeAgentDir.OmpSchema);
            FakeAgentDir.Exec(omp, $"INSERT INTO model_usage VALUES ('anthropic/claude-a', {T0})");
            Assert.True(File.Exists(dir.Db + "-wal"));

            var r = OmpModelUsage.Read(dir.Dir);
            Assert.Equal(OmpModelUsage.Status.Ok, r.Status);
            Assert.Equal("anthropic/claude-a", Assert.Single(r.Items).Key);

            // 그 뒤 omp가 더 쓴 행도 다음 읽기에 보인다(우리가 열어 둔 연결이 없으므로 낡은 스냅숏이 남지 않는다).
            FakeAgentDir.Exec(omp, $"INSERT INTO model_usage VALUES ('openai/gpt-x', {T0 + 5})");
            Assert.Equal(2, OmpModelUsage.Read(dir.Dir).Items.Count);
        }

        [Fact]
        public void Reading_writes_nothing()
        {
            using var dir = new FakeAgentDir().WithRows(("a/b", T0));
            byte[] before = SHA256.HashData(File.ReadAllBytes(dir.Db));
            DateTime stamp = File.GetLastWriteTimeUtc(dir.Db);

            Assert.Equal(OmpModelUsage.Status.Ok, OmpModelUsage.Read(dir.Dir).Status);

            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(dir.Db)));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(dir.Db));
            Assert.Equal(new[] { "agent.db" }, Directory.GetFiles(dir.Dir).Select(Path.GetFileName));
        }

        [Fact]
        public void A_missing_file_or_folder_is_NoDatabase()
        {
            using var dir = new FakeAgentDir();
            Assert.Equal(OmpModelUsage.Status.NoDatabase, OmpModelUsage.Read(dir.Dir).Status);
            Assert.Equal(OmpModelUsage.Status.NoDatabase, OmpModelUsage.Read(Path.Combine(dir.Dir, "nope")).Status);
            Assert.Equal(OmpModelUsage.Status.NoDatabase, OmpModelUsage.Read("").Status);
            Assert.False(File.Exists(dir.Db));   // 읽기가 파일을 만들지 않는다
        }

        [Theory]
        [InlineData("CREATE TABLE other (model_key TEXT, last_used_at INTEGER)", "table model_usage")]
        [InlineData("CREATE TABLE model_usage (model TEXT PRIMARY KEY, last_used_at INTEGER)", "model_key")]
        [InlineData("CREATE TABLE model_usage (model_key TEXT PRIMARY KEY, used_at INTEGER)", "last_used_at")]
        [InlineData("CREATE TABLE model_usage (model_key TEXT PRIMARY KEY, last_used_at TEXT)", "expected INTEGER")]
        [InlineData("CREATE TABLE model_usage (model_key INTEGER PRIMARY KEY, last_used_at INTEGER)", "expected TEXT")]
        public void A_changed_table_or_column_is_SchemaChanged(string createSql, string detailPart)
        {
            using var dir = new FakeAgentDir().WithSchema(createSql);
            var r = OmpModelUsage.Read(dir.Dir);
            Assert.Equal(OmpModelUsage.Status.SchemaChanged, r.Status);
            Assert.Contains(detailPart, r.Detail);
            Assert.Empty(r.Items);
        }

        [Fact]
        public void A_renamed_column_in_a_real_omp_copy_is_SchemaChanged()
        {
            using var dir = new FakeAgentDir().WithRows(("a/b", T0));
            using (var conn = FakeAgentDir.Open(dir.Db))
                FakeAgentDir.Exec(conn, "ALTER TABLE model_usage RENAME COLUMN last_used_at TO used_at");
            Assert.Equal(OmpModelUsage.Status.SchemaChanged, OmpModelUsage.Read(dir.Dir).Status);
        }

        [Fact]
        public void Millisecond_timestamps_are_SchemaChanged()
        {
            using var dir = new FakeAgentDir().WithRows(("a/b", T0 * 1000));
            var r = OmpModelUsage.Read(dir.Dir);
            Assert.Equal(OmpModelUsage.Status.SchemaChanged, r.Status);
            Assert.Contains("seconds", r.Detail);
        }

        [Fact]
        public void Text_or_real_time_values_are_SchemaChanged()
        {
            // 열 선언이 INTEGER여도 SQLite는 어떤 값이든 받는다: 실제 값 형식을 확인해야 한다.
            using var text = new FakeAgentDir().WithSchema("CREATE TABLE model_usage (model_key TEXT PRIMARY KEY, last_used_at INTEGER)", ("a/b", "2026-10-05T00:00:00Z"));
            var r = OmpModelUsage.Read(text.Dir);
            Assert.Equal(OmpModelUsage.Status.SchemaChanged, r.Status);
            Assert.Contains("expected integer", r.Detail);

            using var real = new FakeAgentDir().WithSchema("CREATE TABLE model_usage (model_key TEXT PRIMARY KEY, last_used_at INT)", ("a/b", 1_791_000_000.5));
            Assert.Equal(OmpModelUsage.Status.SchemaChanged, OmpModelUsage.Read(real.Dir).Status);
        }

        [Fact]
        public void Keys_without_a_slash_are_skipped_unless_none_has_one()
        {
            using var mixed = new FakeAgentDir().WithRows(("default", T0 + 9), ("openai/gpt-x", T0), ("/x", T0 + 3), ("x/", T0 + 4));
            var r = OmpModelUsage.Read(mixed.Dir);
            Assert.Equal(OmpModelUsage.Status.Ok, r.Status);
            Assert.Equal("openai/gpt-x", Assert.Single(r.Items).Key);

            using var none = new FakeAgentDir().WithRows(("gpt-x", T0), ("claude-a", T0));
            var bad = OmpModelUsage.Read(none.Dir);
            Assert.Equal(OmpModelUsage.Status.SchemaChanged, bad.Status);
            Assert.Contains("provider/id", bad.Detail);
        }

        [Fact]
        public void A_file_that_is_not_a_database_is_Error()
        {
            using var dir = new FakeAgentDir();
            File.WriteAllText(dir.Db, new string('x', 4096));
            var r = OmpModelUsage.Read(dir.Dir);
            Assert.Equal(OmpModelUsage.Status.Error, r.Status);
            Assert.NotEmpty(r.Detail);
        }

        [Fact]
        public void A_database_locked_by_a_writer_is_Busy_after_one_retry()
        {
            using var dir = new FakeAgentDir().WithRows(("a/b", T0));   // 기본 저널 방식: 배타 잠금이면 읽을 수 없다
            using var writer = FakeAgentDir.Open(dir.Db);
            FakeAgentDir.Exec(writer, "BEGIN EXCLUSIVE");

            var sw = Stopwatch.StartNew();
            var r = OmpModelUsage.Read(dir.Dir, retryDelayMs: 300);
            sw.Stop();

            Assert.Equal(OmpModelUsage.Status.Busy, r.Status);
            Assert.True(sw.ElapsedMilliseconds >= 300, "한 번 기다렸다 다시 시도해야 한다: " + sw.ElapsedMilliseconds);
            Assert.True(sw.ElapsedMilliseconds < 5000, "오래 붙잡혀 있으면 안 된다: " + sw.ElapsedMilliseconds);
        }

        [Fact]
        public async Task A_lock_that_clears_during_the_retry_delay_reads_fine()
        {
            using var dir = new FakeAgentDir().WithRows(("a/b", T0));
            using var writer = FakeAgentDir.Open(dir.Db);
            FakeAgentDir.Exec(writer, "BEGIN EXCLUSIVE");
            var release = Task.Run(async () => { await Task.Delay(500); FakeAgentDir.Exec(writer, "COMMIT"); });

            var r = OmpModelUsage.Read(dir.Dir, retryDelayMs: 900);
            await release;

            Assert.Equal(OmpModelUsage.Status.Ok, r.Status);
            Assert.Equal("a/b", Assert.Single(r.Items).Key);
        }

        [Fact]
        public void Recent_keeps_only_available_models_newest_first_and_at_most_eight()
        {
            var items = new List<OmpModelUsage.Use>();
            for (int i = 0; i < 12; i++) items.Add(new($"p/m{i:00}", DateTime.UnixEpoch.AddSeconds(T0 + i)));
            items.Add(new("gone/removed", DateTime.UnixEpoch.AddSeconds(T0 + 100)));   // 지금 목록에 없는 모델
            var available = Enumerable.Range(0, 12).Select(i => $"p/m{i:00}").Append("p/never-used").ToList();

            var recent = OmpModelUsage.Recent(items, available);

            Assert.Equal(Enumerable.Range(4, 8).Reverse().Select(i => $"p/m{i:00}"), recent);
            Assert.DoesNotContain("gone/removed", recent);
            Assert.DoesNotContain("p/never-used", recent);
        }

        [Fact]
        public void Recent_ties_break_by_key_and_unavailable_models_do_not_use_up_the_slots()
        {
            var same = DateTime.UnixEpoch.AddSeconds(T0);
            var items = new List<OmpModelUsage.Use> { new("b/y", same), new("a/x", same), new("z/gone", same.AddHours(1)) };
            Assert.Equal(new[] { "a/x", "b/y" }, OmpModelUsage.Recent(items, new[] { "b/y", "a/x" }));
            Assert.Empty(OmpModelUsage.Recent(items, Array.Empty<string>()));
        }
    }

    public class OmpAgentDirTests
    {
        private const string Home = @"C:\Users\u";
        private static Func<string, string?> Env(params (string, string)[] vars) =>
            name => vars.Where(v => v.Item1 == name).Select(v => v.Item2).FirstOrDefault();

        [Fact]
        public void The_default_is_dot_omp_agent_under_home()
        {
            Assert.Equal(Path.Combine(Home, ".omp", "agent"), OmpAgentDir.Resolve(null, Env(), Home));
        }

        [Fact]
        public void PI_CODING_AGENT_DIR_moves_the_default_profile_and_PI_CONFIG_DIR_moves_the_root()
        {
            Assert.Equal(@"D:\omp-agent", OmpAgentDir.Resolve(null, Env(("PI_CODING_AGENT_DIR", @"D:\omp-agent")), Home));
            Assert.Equal(Path.Combine(Home, "x", "agent"), OmpAgentDir.Resolve(null, Env(("PI_CONFIG_DIR", "x")), Home));
            Assert.Equal(Path.Combine(Home, "sub"), OmpAgentDir.Resolve(null, Env(("PI_CODING_AGENT_DIR", "~/sub")), Home));
        }

        [Fact]
        public void A_named_profile_from_the_arguments_or_environment_wins_and_ignores_PI_CODING_AGENT_DIR()
        {
            var profile = Path.Combine(Home, ".omp", "profiles", "work", "agent");
            Assert.Equal(profile, OmpAgentDir.Resolve("--profile work", Env(("PI_CODING_AGENT_DIR", @"D:\a")), Home));
            Assert.Equal(profile, OmpAgentDir.Resolve("--model a/b --profile=work", Env(), Home));
            Assert.Equal(profile, OmpAgentDir.Resolve(null, Env(("OMP_PROFILE", "work"), ("PI_CODING_AGENT_DIR", @"D:\a")), Home));
            Assert.Equal(profile, OmpAgentDir.Resolve(null, Env(("PI_PROFILE", "work")), Home));
            // 명령줄이 환경보다 우선하고, 마지막 --profile이 이긴다.
            Assert.Equal(Path.Combine(Home, ".omp", "profiles", "b", "agent"), OmpAgentDir.Resolve("--profile a --profile b", Env(("OMP_PROFILE", "env")), Home));
        }

        [Fact]
        public void OMP_PROFILE_wins_over_PI_PROFILE_even_when_empty_and_default_means_no_profile()
        {
            Assert.Equal(Path.Combine(Home, ".omp", "agent"), OmpAgentDir.Resolve(null, Env(("OMP_PROFILE", ""), ("PI_PROFILE", "legacy")), Home));
            Assert.Equal(Path.Combine(Home, ".omp", "agent"), OmpAgentDir.Resolve("--profile default", Env(), Home));
        }

        [Fact]
        public void An_unusable_profile_name_gives_no_folder_and_the_override_variable_wins()
        {
            Assert.Null(OmpAgentDir.Resolve("--profile ..", Env(), Home));
            Assert.Null(OmpAgentDir.Resolve("--profile a\\b", Env(), Home));
            Assert.Equal(@"E:\copy", OmpAgentDir.Resolve("--profile work", Env((OmpAgentDir.OverrideVariable, @"E:\copy")), Home));
        }
    }

    public class ChatControllerModelUsageTests
    {
        private const long T0 = 1_791_000_000;
        private static OmpModelUsage.Result Ok(params (string Key, long Seconds)[] rows) =>
            new(OmpModelUsage.Status.Ok, rows.Select(r => new OmpModelUsage.Use(r.Key, DateTime.UnixEpoch.AddSeconds(r.Seconds))).ToList(), "");
        private static OmpModelUsage.Result Fail(OmpModelUsage.Status status, string detail = "x") => OmpModelUsage.Result.Fail(status, detail);

        private static string[] RecentOf(JsonElement catalog) => catalog.Child("recent").Strings().ToArray();
        private static bool LogHas(ControllerRig rig, Func<string, bool> predicate) { lock (rig.Log.Lines) return rig.Log.Lines.Any(predicate); }
        private static List<JsonElement> Warnings(ControllerRig rig) => rig.Page.Parsed("notice").Where(n => n.Str("level") == "warn").ToList();

        private static ControllerRig Rig(Func<string, OmpModelUsage.Result> usage, AgentHostOptions? options = null)
        {
            var rig = new ControllerRig(options);
            rig.Usage = usage;
            return rig;
        }

        [Fact]
        public async Task The_catalog_carries_recent_models_that_are_available_newest_first()
        {
            using var rig = Rig(_ => Ok(("openai/gpt-x", T0), ("anthropic/claude-a", T0 + 10), ("gone/old", T0 + 99)));
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Any(c => RecentOf(c).Length > 0));

            var catalog = rig.Page.Parsed("catalog").Last(c => RecentOf(c).Length > 0);
            Assert.Equal(new[] { "anthropic/claude-a", "openai/gpt-x" }, RecentOf(catalog));
            Assert.Equal(new[] { "anthropic/claude-a", "openai/gpt-x" }, catalog.Child("models").Strings());   // 전체 목록은 그대로
            Assert.Empty(Warnings(rig));
            Assert.Contains(rig.UsageDirs, d => d.EndsWith("ncv-fake-omp-agent"));
        }

        [Fact]
        public async Task The_models_are_posted_at_once_without_waiting_for_the_usage_read()
        {
            var gate = new ManualResetEventSlim();
            using var rig = Rig(_ => { gate.Wait(5000); return Ok(("openai/gpt-x", T0)); });
            try
            {
                await rig.StartAsync();
                await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Any(c => c.Child("models").Strings().Count == 2));
                Assert.All(rig.Page.Parsed("catalog"), c => Assert.Empty(RecentOf(c)));   // 아직 못 읽음 = 비어 있음, 목록은 이미 나감
            }
            finally { gate.Set(); }
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Any(c => RecentOf(c).Length == 1));
        }

        [Fact]
        public async Task Switching_the_model_reads_the_record_again_and_refreshes_the_catalog()
        {
            var rows = new List<(string, long)> { ("openai/gpt-x", T0) };
            using var rig = Rig(_ => { lock (rows) return Ok(rows.ToArray()); });
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Any(c => RecentOf(c).SequenceEqual(new[] { "openai/gpt-x" })));
            int reads;
            lock (rig.UsageDirs) reads = rig.UsageDirs.Count;

            lock (rows) rows.Add(("anthropic/claude-a", T0 + 30));   // omp가 set_model 때 적는 행
            rig.FromPage("{\"t\":\"setModel\",\"value\":\"anthropic/claude-a\"}");

            await rig.WaitUntilAsync(() => RecentOf(rig.Page.Parsed("catalog").Last()).SequenceEqual(new[] { "anthropic/claude-a", "openai/gpt-x" }));
            lock (rig.UsageDirs) Assert.True(rig.UsageDirs.Count > reads);
        }

        [Fact]
        public async Task An_unchanged_record_does_not_repost_the_catalog()
        {
            using var rig = Rig(_ => Ok(("openai/gpt-x", T0)));
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Any(c => RecentOf(c).Length == 1));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Last().Child("levels").Strings().Count == 3);
            await Task.Delay(100);
            int before = rig.Page.Parsed("catalog").Count;
            int reads;
            lock (rig.UsageDirs) reads = rig.UsageDirs.Count;

            rig.FromPage("{\"t\":\"setModel\",\"value\":\"openai/gpt-x\"}");
            await rig.WaitUntilAsync(() => { lock (rig.UsageDirs) return rig.UsageDirs.Count > reads; });
            await Task.Delay(100);

            // set_model이 부르는 생각 수준 목록은 카탈로그를 다시 보내지만, 사용 기록 때문에 더 보내지는 않는다(recent는 그대로).
            Assert.All(rig.Page.Parsed("catalog").Skip(before), c => Assert.Equal(new[] { "openai/gpt-x" }, RecentOf(c)));
            Assert.True(rig.Page.Parsed("catalog").Count - before <= 1);
        }

        [Theory]
        [InlineData("SchemaChanged")]
        [InlineData("Error")]
        public async Task A_changed_format_or_read_error_falls_back_to_the_full_list_and_warns_once_per_omp_version(string statusName)
        {
            var status = Enum.Parse<OmpModelUsage.Status>(statusName);
            var shown = new List<string>();
            using var rig = Rig(_ => Fail(status, "column last_used_at not found"));
            rig.Controller.ModelUsageAlertShown += shown.Add;
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => Warnings(rig).Count == 1);

            var warn = Warnings(rig).Single().Str("text");
            Assert.Contains("omp's internal format has changed", warn);
            Assert.Contains("Showing the full model list", warn);
            Assert.Contains("Please update the app", warn);
            Assert.Contains("(column last_used_at not found)", warn);
            Assert.Equal(new[] { "18.4.4" }, shown);
            Assert.True(LogHas(rig, l => l.Contains("model usage") && l.Contains(status.ToString()) && l.Contains("last_used_at")));

            // 폴백: 전체 목록은 그대로, recent는 비어 있다.
            var catalog = rig.Page.Parsed("catalog").Last();
            Assert.Equal(new[] { "anthropic/claude-a", "openai/gpt-x" }, catalog.Child("models").Strings());
            Assert.Empty(RecentOf(catalog));

            // 다시 읽어도(모델 바꿈) 같은 omp 버전에서는 또 알리지 않는다.
            int reads;
            lock (rig.UsageDirs) reads = rig.UsageDirs.Count;
            rig.FromPage("{\"t\":\"setModel\",\"value\":\"openai/gpt-x\"}");
            await rig.WaitUntilAsync(() => { lock (rig.UsageDirs) return rig.UsageDirs.Count > reads; });
            await Task.Delay(100);
            Assert.Single(Warnings(rig));
            Assert.Single(shown);
        }

        [Fact]
        public async Task The_warning_is_in_korean_when_the_app_is_korean()
        {
            using var rig = Rig(_ => Fail(OmpModelUsage.Status.SchemaChanged, "열 없음"), new AgentHostOptions(Language: "ko", AppVersion: "1.2.3"));
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => Warnings(rig).Count == 1);
            Assert.Equal("omp 내부 형식이 바뀌어 최근 사용 모델을 표시할 수 없습니다. 전체 모델 목록을 표시합니다. 앱을 업데이트하세요. (열 없음)", Warnings(rig).Single().Str("text"));
        }

        [Fact]
        public async Task The_warning_is_not_repeated_for_a_version_that_was_already_warned_but_is_for_a_new_one()
        {
            using (var seen = Rig(_ => Fail(OmpModelUsage.Status.SchemaChanged), new AgentHostOptions(Language: "en", AppVersion: "1.2.3", ModelUsageAlertedVersion: "18.4.4")))
            {
                await seen.StartAsync();
                await seen.WaitUntilAsync(() => seen.Page.Parsed("catalog").Count >= 2);
                await seen.WaitUntilAsync(() => LogHas(seen, l => l.Contains("model usage")));
                Assert.Empty(Warnings(seen));
            }
            using var newer = Rig(_ => Fail(OmpModelUsage.Status.SchemaChanged), new AgentHostOptions(Language: "en", AppVersion: "1.2.3", ModelUsageAlertedVersion: "18.4.3"));
            await newer.StartAsync();
            await newer.WaitUntilAsync(() => Warnings(newer).Count == 1);
        }

        [Fact]
        public async Task A_missing_record_falls_back_silently()
        {
            using var rig = Rig(_ => Fail(OmpModelUsage.Status.NoDatabase, "agent.db not found"));
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => LogHas(rig, l => l.Contains("NoDatabase")));
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Count >= 2);
            Assert.Empty(Warnings(rig));
            Assert.All(rig.Page.Parsed("catalog"), c => Assert.Empty(RecentOf(c)));
        }

        [Fact]
        public async Task A_busy_record_keeps_the_last_good_list_and_warns_only_when_it_stays_busy()
        {
            var results = new Queue<OmpModelUsage.Result>(new[]
            {
                Ok(("openai/gpt-x", T0)),
                Fail(OmpModelUsage.Status.Busy, "database is locked"),
                Fail(OmpModelUsage.Status.Busy, "database is locked"),
                Fail(OmpModelUsage.Status.Busy, "database is locked"),
            });
            using var rig = Rig(_ => { lock (results) return results.Count > 0 ? results.Dequeue() : Fail(OmpModelUsage.Status.Busy, "database is locked"); });
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => rig.Page.Parsed("catalog").Any(c => RecentOf(c).Length == 1));

            for (int i = 1; i <= ChatController.PersistentBusyReads; i++)
            {
                int reads;
                lock (rig.UsageDirs) reads = rig.UsageDirs.Count;
                rig.FromPage("{\"t\":\"setModel\",\"value\":\"openai/gpt-x\"}");
                await rig.WaitUntilAsync(() => { lock (rig.UsageDirs) return rig.UsageDirs.Count > reads; });
                await Task.Delay(100);
                if (i < ChatController.PersistentBusyReads) Assert.Empty(Warnings(rig));
            }

            await rig.WaitUntilAsync(() => Warnings(rig).Count == 1);
            Assert.Contains("stays locked", Warnings(rig).Single().Str("text"));
            Assert.Equal(new[] { "openai/gpt-x" }, RecentOf(rig.Page.Parsed("catalog").Last()));   // 마지막으로 읽은 목록 유지
        }

        [Fact]
        public async Task A_reader_that_throws_is_an_Error_not_a_crash()
        {
            using var rig = Rig(_ => throw new InvalidOperationException("boom"));
            await rig.StartAsync();
            await rig.WaitUntilAsync(() => Warnings(rig).Count == 1);
            Assert.Contains("boom", Warnings(rig).Single().Str("text"));
            Assert.Equal(new[] { "anthropic/claude-a", "openai/gpt-x" }, rig.Page.Parsed("catalog").Last().Child("models").Strings());
        }

        [Fact]
        public void The_settings_status_line_names_the_reason()
        {
            string Line(OmpModelUsage.Result? r, bool ko) => ChatController.ModelUsageStatusLine(r, ko);
            Assert.Equal("최근 사용 모델: omp 기록 사용 중", Line(Ok(), true));
            Assert.Equal("Recently used models: using omp's usage record", Line(Ok(), false));
            Assert.Contains("읽을 수 없음 (omp 내부 형식이 바뀜: 열 없음)", Line(Fail(OmpModelUsage.Status.SchemaChanged, "열 없음"), true));
            Assert.Contains("unavailable (omp has no usage record yet)", Line(Fail(OmpModelUsage.Status.NoDatabase), false));
            Assert.Contains("아직 확인하지 않음", Line(null, true));
        }
    }
}
