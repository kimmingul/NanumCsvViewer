using System.Text.Json;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Tests
{
    /// <summary>채팅 입력창 + 메뉴·끌어놓기에서 올린 파일을 받는 가짜 호스트: 받은 파일을 기록하고 "파일 이름 표"로 답한다.</summary>
    internal sealed class FakeAttachHost : IChatAttachHost
    {
        public string? Reason { get; set; }
        public string? Refuse { get; set; }
        public HashSet<string> FailNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<List<string>> Calls { get; } = new();

        public string? UnavailableReason => Reason;

        public Task<ChatAttachOutcome> AddAsync(IReadOnlyList<string> files, CancellationToken cancellation)
        {
            Calls.Add(files.ToList());
            if (Refuse != null) return Task.FromResult(new ChatAttachOutcome(Array.Empty<ChatAttachment>(), Array.Empty<(string, string)>(), Refuse));
            var added = new List<ChatAttachment>();
            var failed = new List<(string File, string Error)>();
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (FailNames.Contains(name)) { failed.Add((f, "boom")); continue; }
                string stem = Path.GetFileNameWithoutExtension(f);
                bool db = Path.GetExtension(f).ToLowerInvariant() is ".xlsx" or ".db";
                added.Add(new ChatAttachment(name, f, db ? new[] { stem + ".Sheet1", stem + ".Sheet2" } : new[] { stem }));
            }
            return Task.FromResult(new ChatAttachOutcome(added, failed));
        }
    }

    public class ChatAttachTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncv-attach-" + Guid.NewGuid().ToString("N"));

        public ChatAttachTests() => Directory.CreateDirectory(_dir);

        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Touch(string rel, string content = "a,b\n1,2\n")
        {
            string path = Path.Combine(_dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private static ControllerRig NewRig(FakeAttachHost host, bool korean = true)
        {
            var rig = new ControllerRig(new AgentHostOptions(Language: korean ? "ko" : "en", AppVersion: "1.2.3"));
            rig.OnUi(() => rig.Controller.AttachHost = host);
            return rig;
        }

        private static string NoticeText(ControllerRig rig, string level) =>
            string.Join("\n", rig.Page.Parsed("notice").Where(n => n.Str("level") == level).Select(n => n.Str("text")));

        private static Task WaitAttachedAsync(ControllerRig rig, int count = 1) => rig.WaitForCountAsync("attached", count);

        // ---- 파일 대화 상자 -------------------------------------------------------------------------------------

        [Fact]
        public async Task Files_dialog_registers_only_supported_types_and_lists_the_rest_in_a_notice()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host);
            rig.Dialogs.PickedFiles = new[]
            {
                Touch("orders.csv"), Touch("book.xlsx"), Touch("memo.pdf"), Touch("logo.png"), Touch("legacy.XLS"),
                Touch("s.sav"), Touch("t.sas7bdat"), Touch("x.sqlite3"), Touch("n.txt"), Touch("v.tsv"), Touch("m.xlsm"), Touch("d.sqlite"),
            };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            await WaitAttachedAsync(rig);

            var call = Assert.Single(host.Calls);
            Assert.Equal(new[] { "orders.csv", "book.xlsx", "legacy.XLS", "s.sav", "t.sas7bdat", "x.sqlite3", "n.txt", "v.tsv", "m.xlsm", "d.sqlite" },
                call.Select(Path.GetFileName));
            Assert.Equal("지원하지 않는 형식: memo.pdf, logo.png", NoticeText(rig, "warn"));
            Assert.Empty(rig.Dialogs.ConfirmTitles);   // 직접 고른 파일은 개수와 상관없이 묻지 않는다
            Assert.Contains("*.csv;*.tsv", rig.Dialogs.PickFilters.Single());
            Assert.Contains("*.sqlite3", rig.Dialogs.PickFilters.Single());
        }

        [Fact]
        public async Task Attached_chips_carry_the_display_name_the_path_and_the_workspace_table_names()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host);
            string csv = Touch("orders.csv"), xlsx = Touch("book.xlsx");
            rig.Dialogs.PickedFiles = new[] { csv, xlsx };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            var msg = await rig.Page.WaitForTypeAsync("attached");

            var items = msg.Child("items").Items().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal("📎 orders.csv", items[0].Str("label"));
            Assert.Equal("orders.csv", items[0].Str("name"));
            Assert.Equal(csv, items[0].Str("path"));
            Assert.Equal(new[] { "orders" }, items[0].Child("tables").Items().Select(t => t.GetString()));
            Assert.Equal(new[] { "book.Sheet1", "book.Sheet2" }, items[1].Child("tables").Items().Select(t => t.GetString()));
        }

        [Fact]
        public async Task Cancelling_the_dialog_adds_nothing_and_says_nothing()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host);
            rig.Dialogs.PickedFiles = null;
            rig.Dialogs.PickedFolder = null;
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"folder\"}");
            await Task.Delay(50);
            Assert.Empty(host.Calls);
            Assert.Empty(rig.Page.Parsed("attached"));
            Assert.Empty(rig.Page.Parsed("notice"));
        }

        [Fact]
        public async Task Adding_the_same_file_again_answers_with_the_same_chip_again()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host);
            rig.Dialogs.PickedFiles = new[] { Touch("orders.csv") };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            await WaitAttachedAsync(rig, 1);
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            await WaitAttachedAsync(rig, 2);

            var both = rig.Page.Parsed("attached");
            Assert.Equal(both[0].GetRawText(), both[1].GetRawText());
            Assert.Empty(rig.Page.Parsed("notice"));
        }

        // ---- 폴더 -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task Folder_registers_only_the_data_files_directly_in_it()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host);
            Touch("data\\b.csv"); Touch("data\\a.xlsx"); Touch("data\\notes.pdf"); Touch("data\\pic.png");
            Touch("data\\sub\\deep.csv");
            rig.Dialogs.PickedFolder = Path.Combine(_dir, "data");
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"folder\"}");
            await WaitAttachedAsync(rig);

            Assert.Equal(new[] { "a.xlsx", "b.csv" }, Assert.Single(host.Calls).Select(Path.GetFileName));
            Assert.Empty(rig.Page.Parsed("notice"));   // 폴더 안의 다른 파일은 알리지 않는다
        }

        [Fact]
        public async Task Folder_with_more_than_ten_files_asks_first_and_declining_adds_nothing()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host);
            for (int i = 0; i < 11; i++) Touch($"many\\f{i:00}.csv");
            rig.Dialogs.PickedFolder = Path.Combine(_dir, "many");
            string? asked = null;
            rig.Dialogs.OnConfirm = (_, message) => { asked = message; return false; };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"folder\"}");
            await rig.WaitUntilAsync(() => asked != null);

            Assert.Equal("11개 파일을 추가할까요?", asked);
            await Task.Delay(50);
            Assert.Empty(host.Calls);
            Assert.Empty(rig.Page.Parsed("attached"));

            rig.Dialogs.OnConfirm = (_, message) => { asked = message; return true; };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"folder\"}");
            await WaitAttachedAsync(rig);
            Assert.Equal(11, Assert.Single(host.Calls).Count);
        }

        [Fact]
        public async Task Folder_with_ten_files_is_added_without_asking()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host, korean: false);
            for (int i = 0; i < 10; i++) Touch($"ten\\f{i}.csv");
            rig.Dialogs.PickedFolder = Path.Combine(_dir, "ten");
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"folder\"}");
            await WaitAttachedAsync(rig);
            Assert.Empty(rig.Dialogs.ConfirmTitles);
            Assert.Equal(10, Assert.Single(host.Calls).Count);
        }

        [Fact]
        public async Task Folder_without_data_files_says_so()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host, korean: false);
            Touch("none\\readme.pdf");
            rig.Dialogs.PickedFolder = Path.Combine(_dir, "none");
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"folder\"}");
            await rig.WaitForCountAsync("notice", 1);
            Assert.Contains("no supported data files", NoticeText(rig, "info"));
            Assert.Empty(host.Calls);
        }

        // ---- 끌어놓기 -------------------------------------------------------------------------------------------

        [Fact]
        public async Task Dropped_files_and_folders_follow_the_same_rules()
        {
            var host = new FakeAttachHost();
            using var rig = NewRig(host);
            string csv = Touch("drop\\a.csv"), pdf = Touch("drop\\b.pdf");
            Touch("dropdir\\c.csv"); Touch("dropdir\\sub\\d.csv");
            string json = JsonSerializer.Serialize(new { t = "attachPaths", paths = new[] { csv, pdf, Path.Combine(_dir, "dropdir"), csv, Path.Combine(_dir, "gone.csv") } });
            rig.FromPage(json);
            await WaitAttachedAsync(rig);

            Assert.Equal(new[] { "a.csv", "c.csv" }, Assert.Single(host.Calls).Select(Path.GetFileName));   // 중복·하위 폴더 제외
            Assert.Equal("지원하지 않는 형식: b.pdf", NoticeText(rig, "warn").Split('\n')[0]);
            Assert.Contains("gone.csv", NoticeText(rig, "warn"));
        }

        // ---- 오류 · 엔진 없음 -----------------------------------------------------------------------------------

        [Fact]
        public async Task Engine_unavailable_is_reported_before_any_dialog_opens()
        {
            var host = new FakeAttachHost { Reason = "native library missing" };
            using var rig = NewRig(host, korean: false);
            rig.Dialogs.PickedFiles = new[] { Touch("orders.csv") };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            await rig.WaitForCountAsync("notice", 1);

            Assert.Contains("native library missing", NoticeText(rig, "error"));
            Assert.Empty(rig.Dialogs.PickFilters);
            Assert.Empty(host.Calls);
            Assert.Empty(rig.Page.Parsed("attached"));
        }

        [Fact]
        public async Task Without_a_host_adding_files_says_it_is_unavailable()
        {
            using var rig = new ControllerRig(new AgentHostOptions(Language: "en", AppVersion: "1.2.3"));
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            await rig.WaitForCountAsync("notice", 1);
            Assert.Contains("not available", NoticeText(rig, "error"));
        }

        [Fact]
        public async Task A_failing_file_is_reported_and_the_others_still_get_chips()
        {
            var host = new FakeAttachHost();
            host.FailNames.Add("bad.csv");
            using var rig = NewRig(host, korean: false);
            rig.Dialogs.PickedFiles = new[] { Touch("good.csv"), Touch("bad.csv") };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            var chips = await rig.Page.WaitForTypeAsync("attached");
            await rig.WaitForCountAsync("notice", 1);

            Assert.Equal("good.csv", chips.Child("items").Items().Single().Str("name"));
            Assert.Contains("bad.csv: boom", NoticeText(rig, "error"));
        }

        [Fact]
        public async Task A_refused_run_is_explained_and_adds_no_chips()
        {
            var host = new FakeAttachHost { Refuse = "another workspace operation is still running" };
            using var rig = NewRig(host, korean: false);
            rig.Dialogs.PickedFiles = new[] { Touch("a.csv") };
            rig.FromPage("{\"t\":\"attach\",\"kind\":\"files\"}");
            await rig.WaitForCountAsync("notice", 1);
            Assert.Contains("another workspace operation", NoticeText(rig, "warn"));
            Assert.Empty(rig.Page.Parsed("attached"));
        }

        // ---- 전송 ----------------------------------------------------------------------------------------------

        private static async Task<ControllerRig> ConnectedAsync()
        {
            var rig = new ControllerRig();
            await rig.StartAsync();
            await rig.Proc.WaitForTypeAsync("get_available_thinking_levels");
            return rig;
        }

        private const string OrdersChip = "{\"name\":\"orders.csv\",\"path\":\"C:\\\\secret\\\\orders.csv\",\"tables\":[\"orders\"]}";

        [Fact]
        public async Task Submit_prefixes_table_names_but_no_data_and_the_bubble_shows_chips_instead_of_the_prefix()
        {
            using var rig = await ConnectedAsync();
            string csv = Touch("orders.csv", "id,secret\n1,TOPSECRETVALUE\n");
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"요약해 줘\",\"attachments\":[" + OrdersChip +
                         ",{\"name\":\"book.xlsx\",\"tables\":[\"book.Sheet1\",\"book.Sheet2\"]}]}");

            var prompt = await rig.Proc.WaitForTypeAsync("prompt");
            Assert.Equal("[Attached workspace tables: orders (orders.csv), book.Sheet1 (book.xlsx), book.Sheet2 (book.xlsx)] Use ws.* tools to inspect them.\n\n요약해 줘",
                prompt.Str("message"));
            Assert.DoesNotContain("TOPSECRETVALUE", prompt.Str("message"));
            Assert.DoesNotContain("secret", prompt.Str("message"), StringComparison.OrdinalIgnoreCase);   // 페이지가 보낸 경로도 쓰지 않는다
            Assert.DoesNotContain(csv, prompt.Str("message"));

            var user = await rig.Page.WaitForTypeAsync("user");
            Assert.Equal("요약해 줘", user.Str("text"));
            var chips = user.Child("attachments").Items().ToList();
            Assert.Equal(new[] { "orders.csv", "book.xlsx" }, chips.Select(c => c.Str("name")));
            Assert.Equal("📎 orders.csv", chips[0].Str("label"));
            Assert.Equal(new[] { "book.Sheet1", "book.Sheet2" }, chips[1].Child("tables").Items().Select(t => t.GetString()));
            Assert.True((await rig.Page.WaitForTypeAsync("submitted")).Bool("ok"));
            Assert.Equal("요약해 줘", rig.OnUi(() => rig.Controller.CurrentUserRequest));   // 뷰 출처에도 머리말은 안 들어간다
        }

        [Fact]
        public async Task Submit_without_attachments_sends_the_text_unchanged()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"hello\",\"attachments\":[]}");
            Assert.Equal("hello", (await rig.Proc.WaitForTypeAsync("prompt")).Str("message"));
            var user = await rig.Page.WaitForTypeAsync("user");
            Assert.Equal(JsonValueKind.Undefined, user.Child("attachments").ValueKind);
        }

        [Fact]
        public async Task Slash_and_shell_commands_never_carry_the_prefix()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"/compact focus\",\"attachments\":[" + OrdersChip + "]}");
            Assert.Equal("/compact focus", (await rig.Proc.WaitForTypeAsync("prompt")).Str("message"));
            Assert.Equal(JsonValueKind.Undefined, (await rig.Page.WaitForTypeAsync("user")).Child("attachments").ValueKind);
        }

        [Fact]
        public async Task A_message_queued_while_busy_keeps_the_prefix_for_omp_and_chips_for_the_bubble()
        {
            using var rig = await ConnectedAsync();
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s1\",\"text\":\"first\"}");
            await rig.Proc.WaitForTypeAsync("prompt");
            rig.FromPage("{\"t\":\"submit\",\"id\":\"s2\",\"text\":\"also this\",\"attachments\":[" + OrdersChip + "]}");
            var steer = await rig.Proc.WaitForTypeAsync("steer");

            Assert.StartsWith("[Attached workspace tables: orders (orders.csv)] Use ws.* tools to inspect them.", steer.Str("message"));
            var user = rig.Page.Parsed("user").Last();
            Assert.Equal("also this", user.Str("text"));
            Assert.Equal("steer", user.Str("queue"));
            Assert.Equal(steer.Str("message"), user.Str("sent"));   // 취소는 omp가 가진 원문으로 찾는다
            Assert.Equal("orders.csv", user.Child("attachments").Items().Single().Str("name"));
        }

        // ---- 머리말 읽기 · 기록 ---------------------------------------------------------------------------------

        [Fact]
        public void Prefix_round_trips_and_groups_tables_by_file()
        {
            var items = new[]
            {
                new ChatAttachment("orders (1).csv", "", new[] { "orders_1" }),
                new ChatAttachment("book.xlsx", "", new[] { "book.Sheet1", "book.한글" }),
            };
            string full = AttachmentContext.Compose(items, "질문\n두 줄");
            Assert.True(AttachmentContext.TryParse(full, out var back, out string text));
            Assert.Equal("질문\n두 줄", text);
            Assert.Equal(new[] { "orders (1).csv", "book.xlsx" }, back.Select(a => a.Name));
            Assert.Equal(new[] { "book.Sheet1", "book.한글" }, back[1].Tables);
        }

        [Fact]
        public void Prefix_lists_at_most_forty_tables_and_still_parses()
        {
            var many = new ChatAttachment("big.db", "", Enumerable.Range(1, 60).Select(i => "big.t" + i).ToList());
            string full = AttachmentContext.Compose(new[] { many }, "q");
            Assert.Contains(", +20 more]", full);
            Assert.DoesNotContain("big.t41 ", full);
            Assert.True(AttachmentContext.TryParse(full, out var back, out string text));
            Assert.Equal("q", text);
            Assert.Equal(40, back.Single().Tables.Count);
        }

        [Theory]
        [InlineData("plain question")]
        [InlineData("[Attached workspace tables: ] Use ws.* tools to inspect them.\n\nq")]
        [InlineData("[Attached workspace tables: broken] Use ws.* tools to inspect them.\n\nq")]
        [InlineData("[Attached workspace tables: a (b.csv)")]
        public void Unreadable_or_missing_prefix_leaves_the_text_alone(string text)
        {
            Assert.False(AttachmentContext.TryParse(text, out var items, out string visible));
            Assert.Empty(items);
            Assert.Equal(text, visible);
        }

        [Fact]
        public void Reloaded_history_shows_chips_for_the_prefix_instead_of_the_raw_line()
        {
            string full = AttachmentContext.Compose(new[] { new ChatAttachment("orders.csv", "", new[] { "orders" }) }, "hi");
            var json = JsonDocument.Parse(ChatPageMessages.History(new[]
            {
                new HistoryItem { Role = "user", Text = full, Timestamp = 5 },
                new HistoryItem { Role = "assistant", Text = "ok", CompletedAt = 9 },
            })).RootElement;
            var first = json.Child("items").Items().First();
            Assert.Equal("hi", first.Str("text"));
            Assert.Equal("orders.csv", first.Child("attachments").Items().Single().Str("name"));
            Assert.Equal(JsonValueKind.Undefined, json.Child("items").Items().Last().Child("attachments").ValueKind);
        }

        [Fact]
        public void Session_list_titles_come_from_the_text_without_the_prefix()
        {
            string file = Path.Combine(_dir, "session.jsonl");
            string full = AttachmentContext.Compose(new[] { new ChatAttachment("orders.csv", "", new[] { "orders" }) }, "매출 요약");
            File.WriteAllText(file, JsonSerializer.Serialize(new { type = "message", message = new { role = "user", content = full } }) + "\n");
            Assert.Equal("매출 요약", SessionCatalog.Read(new FileInfo(file)).Title);
        }

        [Fact]
        public void Supported_types_are_the_workspace_data_types()
        {
            foreach (string ok in new[] { "a.csv", "a.TSV", "a.txt", "a.xlsx", "a.xlsm", "a.xls", "a.sas7bdat", "a.sav", "a.db", "a.sqlite", "a.sqlite3" })
                Assert.True(ChatController.IsAttachable(ok), ok);
            foreach (string no in new[] { "a.pdf", "a.png", "a.docx", "a.md", "a", "a.csv.bak", "a.json" })
                Assert.False(ChatController.IsAttachable(no), no);
        }
    }
}
