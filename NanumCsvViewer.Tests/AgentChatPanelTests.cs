using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer.Tests
{
    // 채팅 패널의 WebView2 밖 로직: ready 전 큐, 내장 자산 추출·일관성, 문구·테마 메시지.
    public class AgentChatPanelTests
    {
        private static readonly Assembly App = typeof(AgentChatPanel).Assembly;

        private static string Text(string relative) => ChatAssetStore.ReadText(App, ChatAssetStore.ResourcePrefix + relative);

        // ── ChatPostQueue ──────────────────────────────────────────────────────────

        [Fact]
        public void Queue_HoldsUntilReady_ThenFlushesInOrder()
        {
            var sent = new List<string>();
            var q = new ChatPostQueue(sent.Add);
            q.Post("a"); q.Post("b");
            Assert.Empty(sent);
            Assert.False(q.IsReady);

            q.MarkReady();
            Assert.Equal(new[] { "a", "b" }, sent);
            Assert.True(q.IsReady);

            q.Post("c");
            Assert.Equal(new[] { "a", "b", "c" }, sent);
            Assert.Equal(0, q.PendingCount);
        }

        [Fact]
        public void Queue_PostDuringFlush_KeepsOrder()
        {
            var sent = new List<string>();
            ChatPostQueue? q = null;
            q = new ChatPostQueue(json =>
            {
                sent.Add(json);
                if (json == "a") q!.Post("late");   // 보내는 중 다시 Post: "b" 뒤에 가야 한다.
            });
            q.Post("a"); q.Post("b");
            q.MarkReady();
            Assert.Equal(new[] { "a", "b", "late" }, sent);
        }

        [Fact]
        public void Queue_ResetQueuesAgainUntilNextReady()
        {
            var sent = new List<string>();
            var q = new ChatPostQueue(sent.Add);
            q.MarkReady();
            q.Post("1");
            q.Reset();                       // 페이지 다시 로드
            q.Post("2");
            Assert.Equal(new[] { "1" }, sent);
            q.MarkReady();
            Assert.Equal(new[] { "1", "2" }, sent);
        }

        [Fact]
        public void Queue_BeforeReady_DropsOldestBeyondCap()
        {
            var sent = new List<string>();
            var q = new ChatPostQueue(sent.Add);
            for (int i = 0; i < ChatPostQueue.MaxPending + 5; i++) q.Post(i.ToString());
            Assert.Equal(ChatPostQueue.MaxPending, q.PendingCount);
            q.MarkReady();
            Assert.Equal("5", sent[0]);
            Assert.Equal((ChatPostQueue.MaxPending + 4).ToString(), sent[^1]);
        }

        // ── 내장 자산 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Assets_EmbeddedWithForwardSlashNames()
        {
            var names = ChatAssetStore.ResourceNames(App).Select(ChatAssetStore.RelativePath).ToHashSet();
            foreach (var must in new[] { "chat.html", "chat.js", "composer.js", "markdown.js", "lang/ko.json", "lang/en.json",
                                         "brands/brands.json", "brands/claude.svg", "NOTICE.txt" })
                Assert.Contains(must, names);
            Assert.DoesNotContain(names, n => n.Contains('\\'));
        }

        [Fact]
        public void Assets_ChatHtmlReferencesOnlyExistingFiles()
        {
            var names = ChatAssetStore.ResourceNames(App).Select(ChatAssetStore.RelativePath).ToHashSet();
            var html = Text("chat.html");
            var refs = Regex.Matches(html, "(?:src|href)=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(refs);
            foreach (var r in refs) Assert.Contains(r, names);
        }

        [Fact]
        public void Assets_ViewerHtmlReferencesOnlyExistingFilesAndReusesTheChatRenderer()
        {
            var names = ChatAssetStore.ResourceNames(App).Select(ChatAssetStore.RelativePath).ToHashSet();
            var html = Text("viewer.html");
            var refs = Regex.Matches(html, "(?:src|href)=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            Assert.Contains("markdown.js", refs);
            Assert.Contains("viewer.js", refs);
            foreach (var r in refs) Assert.Contains(r, names);
        }

        // ── 결과 폴더 그림(요청 가로채기) ─────────────────────────────────────────

        [Fact]
        public void OutputPictures_AreServedOnlyFromTheOutputFolder()
        {
            var root = Path.Combine(Path.GetTempPath(), "ncv-out-" + Guid.NewGuid().ToString("N"));
            var dir = Path.Combine(root, "a_분석결과");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "sub dir"));
                File.WriteAllBytes(Path.Combine(dir, "fig.png"), new byte[] { 1, 2, 3 });
                File.WriteAllBytes(Path.Combine(dir, "sub dir", "차트.SVG"), new byte[] { 4 });
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
                File.WriteAllBytes(Path.Combine(root, "outside.png"), new byte[] { 9 });
                var host = "https://" + ChatController.OutputHost + "/";

                var png = AgentChatPanel.ReadOutputPicture(dir, host + "fig.png?v=123");
                Assert.Equal((200, "image/png"), (png.Status, png.ContentType));
                Assert.Equal(new byte[] { 1, 2, 3 }, png.Body);
                var svg = AgentChatPanel.ReadOutputPicture(dir, host + "sub%20dir/" + Uri.EscapeDataString("차트.SVG"));
                Assert.Equal((200, "image/svg+xml"), (svg.Status, svg.ContentType));

                // 그림이 아닌 파일, 없는 파일, 폴더 밖(인코딩된 ..도 포함), 다른 호스트, 폴더 없음은 모두 404.
                foreach (var uri in new[]
                {
                    host + "notes.txt", host + "missing.png", host + "..%2Foutside.png", host + "%2e%2e%2Foutside.png",
                    host + "sub%20dir/..%2F..%2Foutside.png", host + "C%3A%5CWindows%5Cwin.ini", "https://example.com/fig.png",
                })
                    Assert.Equal(404, AgentChatPanel.ReadOutputPicture(dir, uri).Status);
                Assert.Equal(404, AgentChatPanel.ReadOutputPicture(null, host + "fig.png").Status);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Assets_EveryBrandRuleHasItsSvg()
        {
            var names = ChatAssetStore.ResourceNames(App).Select(ChatAssetStore.RelativePath).ToHashSet();
            using var doc = JsonDocument.Parse(Text("brands/brands.json"));
            var brands = doc.RootElement.GetProperty("brands").EnumerateObject().Select(p => p.Name).ToList();
            Assert.NotEmpty(brands);
            foreach (var b in brands) Assert.Contains($"brands/{b}.svg", names);
            // 규칙이 가리키는 브랜드는 모두 정의되어 있다.
            foreach (var p in doc.RootElement.GetProperty("providers").EnumerateObject())
                Assert.Contains(p.Value.GetString(), brands);
            foreach (var rule in doc.RootElement.GetProperty("models").EnumerateArray())
                Assert.Contains(rule[1].GetString(), brands);
        }

        [Fact]
        public void Assets_RadSpecificFeaturesAreGone()
        {
            var names = ChatAssetStore.ResourceNames(App).Select(ChatAssetStore.RelativePath).ToList();
            foreach (var gone in new[] { "plusmenu.js", "btw.js", "btw.css", "checkpoints.js" })
                Assert.DoesNotContain(gone, names);
            var all = string.Concat(names.Where(n => n.EndsWith(".js") || n.EndsWith(".html")).Select(Text));
            Assert.DoesNotContain("highlightPascal", all);
            Assert.DoesNotContain("listFiles", all);
            Assert.DoesNotContain("ChatBtw", all);
            Assert.DoesNotContain("ChatPlusMenu", all);
            Assert.DoesNotContain("ChatCheckpoints", all);
        }

        [Fact]
        public void Assets_ContentHashIsStable()
        {
            var h = ChatAssetStore.ContentHash(App);
            Assert.Matches("^[0-9a-f]{12}$", h);
            Assert.Equal(h, ChatAssetStore.ContentHash(App));
        }

        [Fact]
        public void Assets_ExtractWritesAllFilesOnceAndReusesTheFolder()
        {
            var root = Path.Combine(Path.GetTempPath(), "nanumcsv-chat-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var dir = ChatAssetStore.Extract(App, root, "9.9.9");
                Assert.StartsWith(root, dir);
                Assert.EndsWith("9.9.9-" + ChatAssetStore.ContentHash(App), dir);
                foreach (var name in ChatAssetStore.ResourceNames(App))
                {
                    var file = Path.Combine(dir, ChatAssetStore.RelativePath(name).Replace('/', Path.DirectorySeparatorChar));
                    Assert.True(File.Exists(file), file);
                    Assert.Equal(ChatAssetStore.ReadBytes(App, name), File.ReadAllBytes(file));
                }

                var stamp = File.GetLastWriteTimeUtc(Path.Combine(dir, "chat.html"));
                File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
                Assert.Equal(dir, ChatAssetStore.Extract(App, root, "9.9.9"));
                Assert.True(File.Exists(Path.Combine(dir, "marker.txt")));          // 다시 풀지 않았다
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dir, "chat.html")));
                Assert.Empty(Directory.GetDirectories(root, "*.tmp-*"));             // 임시 폴더가 남지 않는다

                Assert.NotEqual(dir, ChatAssetStore.Extract(App, root, "9.9.10"));    // 버전이 다르면 새 폴더
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        // ── 문구 ───────────────────────────────────────────────────────────────────

        private static Dictionary<string, string> Lang(string lang) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(Text($"lang/{lang}.json"))!;

        [Fact]
        public void Lang_KoAndEnHaveTheSameKeys()
        {
            var ko = Lang("ko"); var en = Lang("en");
            Assert.Equal(en.Keys.OrderBy(k => k, StringComparer.Ordinal), ko.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(ko.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
            // 같은 자리표시자({0},{1})를 가진다.
            foreach (var key in en.Keys)
                Assert.Equal(Placeholders(en[key]), Placeholders(ko[key]));
            Assert.Equal("메시지를 입력하세요 · / 명령", ko["page.composer.placeholder"]);
        }

        private static string Placeholders(string s) =>
            string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().OrderBy(x => x, StringComparer.Ordinal));

        [Fact]
        public void Lang_EveryKeyUsedByThePageIsTranslated()
        {
            var ko = Lang("ko"); var en = Lang("en");
            var used = new HashSet<string>();
            foreach (var name in ChatAssetStore.ResourceNames(App).Select(ChatAssetStore.RelativePath)
                         .Where(n => n.EndsWith(".js") || n.EndsWith(".html")))
            {
                var src = Text(name);
                foreach (Match m in Regex.Matches(src, "['\"](page\\.[A-Za-z0-9_.]+)['\"]")) used.Add(m.Groups[1].Value);
            }
            Assert.NotEmpty(used);
            foreach (var key in used)
            {
                Assert.True(en.ContainsKey(key), "en missing " + key);
                Assert.True(ko.ContainsKey(key), "ko missing " + key);
            }
        }

        [Theory]
        [InlineData("ko", "ko")]
        [InlineData("en", "en")]
        [InlineData("EN", "en")]
        [InlineData("fr", "ko")]
        [InlineData(null, "ko")]
        public void Strings_MessageIsValidJsonWithLanguageAndItems(string? requested, string expected)
        {
            using var doc = JsonDocument.Parse(ChatStrings.Message(App, requested));
            Assert.Equal("strings", doc.RootElement.GetProperty("t").GetString());
            Assert.Equal(expected, doc.RootElement.GetProperty("lang").GetString());
            var items = doc.RootElement.GetProperty("items");
            Assert.Equal(Lang(expected)["page.composer.placeholder"], items.GetProperty("page.composer.placeholder").GetString());
        }

        // ── 테마 ───────────────────────────────────────────────────────────────────

        [Fact]
        public void Theme_DarkIsRadAgentDefaultsAndLightHasSameKeys()
        {
            var dark = ChatTheme.Palette(true); var light = ChatTheme.Palette(false);
            Assert.Equal(dark.Keys.ToList(), light.Keys.ToList());
            Assert.Equal("#1e1e1e", dark["bg"]);
            Assert.Equal("#252526", dark["assistantBg"]);
            Assert.Equal("#ffffff", light["bg"]);
            foreach (var kv in dark.Concat(light).Where(kv => kv.Key != "scheme"))
                Assert.Matches("^#[0-9a-f]{6}$", kv.Value);
            // 밝은 팔레트: 글자색이 배경보다 어둡고, 코드 배경·테두리는 배경에서 글자색 쪽으로 섞인 회색.
            Assert.Equal("#1f1f1f", light["fg"]);
            Assert.Equal("#efefef", light["codeBg"]);   // blend(#fff → #1f1f1f, 0.07)
            Assert.Equal("#d2d2d2", light["border"]);   // blend(#fff → #1f1f1f, 0.20)
            Assert.Equal("light", light["scheme"]);
        }

        [Fact]
        public void Theme_MessageCarriesFontsAndSize()
        {
            using var doc = JsonDocument.Parse(ChatTheme.ThemeMessage(false, "Malgun Gothic", 9f));
            Assert.Equal("theme", doc.RootElement.GetProperty("t").GetString());
            var vars = doc.RootElement.GetProperty("vars");
            Assert.Equal(13, vars.GetProperty("fontSize").GetInt32());
            var font = vars.GetProperty("font").GetString()!;
            Assert.StartsWith("'Malgun Gothic', 'Segoe UI', ", font);   // 이름이 겹치면 한 번만
            Assert.EndsWith("sans-serif", font);
            Assert.Contains("Consolas", vars.GetProperty("monoFont").GetString());
            Assert.Contains("D2Coding", vars.GetProperty("monoFont").GetString());
            Assert.StartsWith("'Segoe UI', 'Malgun Gothic'", ChatTheme.FontStack(null));
        }

        [Theory]
        [InlineData(6f, 9)]
        [InlineData(9f, 13)]
        [InlineData(10f, 14)]
        [InlineData(40f, 24)]
        public void Theme_FontPxIsClamped(float points, int px) => Assert.Equal(px, ChatTheme.FontPx(points));

        // ── 링크 열기 ──────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("file:///C:/Windows/System32/calc.exe")]
        [InlineData("javascript:alert(1)")]
        [InlineData("C:\\Windows\\notepad.exe")]
        [InlineData("")]
        [InlineData(null)]
        public void OpenInBrowser_RefusesNonHttp(string? url) => Assert.False(AgentChatPanel.OpenInBrowser(url));
    }
}
