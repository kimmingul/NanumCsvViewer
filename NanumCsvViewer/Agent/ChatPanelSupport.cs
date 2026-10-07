using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NanumCsvViewer.Agent
{
    // AgentChatPanel(WebView2)에서 떼어 낸 순수 로직. WebView2 없이 테스트한다.

    /// <summary>
    /// 페이지가 {t:'ready'}를 보내기 전에 Post된 메시지를 쌓아 두었다가, ready 뒤에 순서대로 보낸다.
    /// UI 스레드 전용(잠금 없음). 페이지가 다시 로드되면 <see cref="Reset"/>으로 ready 상태를 되돌린다.
    /// </summary>
    internal sealed class ChatPostQueue
    {
        /// <summary>페이지가 끝내 ready하지 않아도 메모리가 무한히 늘지 않게 하는 상한(넘으면 가장 오래된 것부터 버린다).</summary>
        public const int MaxPending = 10000;

        private readonly Action<string> _send;
        private readonly Queue<string> _pending = new();
        private bool _flushing;

        public ChatPostQueue(Action<string> send) => _send = send;

        public bool IsReady { get; private set; }
        public int PendingCount => _pending.Count;

        public void Post(string json)
        {
            if (IsReady) { _send(json); return; }
            _pending.Enqueue(json);
            while (_pending.Count > MaxPending) _pending.Dequeue();
        }

        /// <summary>
        /// 쌓인 메시지를 순서대로 보내고 이후 Post는 바로 보낸다. 보내는 도중 Post된 메시지도 뒤에 붙어 순서가 깨지지 않는다.
        /// </summary>
        public void MarkReady()
        {
            if (IsReady || _flushing) return;
            _flushing = true;
            try
            {
                while (_pending.Count > 0) _send(_pending.Dequeue());
                IsReady = true;
            }
            finally { _flushing = false; }
        }

        /// <summary>페이지가 다시 로드됨: 다음 ready까지 다시 쌓는다.</summary>
        public void Reset() => IsReady = false;

        public void Clear() => _pending.Clear();
    }

    /// <summary>exe에 내장한 채팅 페이지 자산(ChatAssets/**)을 폴더로 풀어 WebView2 가상 호스트에 매핑할 수 있게 한다.</summary>
    internal static class ChatAssetStore
    {
        public const string ResourcePrefix = "ChatAssets/";

        /// <summary>내장 자산의 논리 이름("ChatAssets/lang/ko.json" 형식), 정렬됨.</summary>
        public static IReadOnlyList<string> ResourceNames(Assembly assembly) =>
            assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

        /// <summary>"ChatAssets/lang/ko.json" → "lang/ko.json".</summary>
        public static string RelativePath(string resourceName) => resourceName[ResourcePrefix.Length..];

        public static byte[] ReadBytes(Assembly assembly, string resourceName)
        {
            using var s = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException("embedded resource not found: " + resourceName);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        public static string ReadText(Assembly assembly, string resourceName)
        {
            var bytes = ReadBytes(assembly, resourceName);
            // UTF-8 BOM 제거.
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }

        /// <summary>이름과 내용에 대한 SHA-256 앞 12자리(16진수). 자산이 바뀌면 폴더가 달라져 오래된 파일을 쓰지 않는다.</summary>
        public static string ContentHash(Assembly assembly)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var name in ResourceNames(assembly))
            {
                sha.AppendData(Encoding.UTF8.GetBytes(name));
                sha.AppendData(new byte[] { 0 });
                sha.AppendData(ReadBytes(assembly, name));
                sha.AppendData(new byte[] { 0 });
            }
            return Convert.ToHexString(sha.GetHashAndReset())[..12].ToLowerInvariant();
        }

        /// <summary>
        /// <paramref name="root"/>\&lt;version&gt;-&lt;hash&gt;에 자산을 풀고 그 폴더를 돌려준다. 이미 있으면 다시 풀지 않는다.
        /// 임시 폴더에 풀고 폴더째 옮겨(원자적) 도중에 끊기거나 두 인스턴스가 동시에 시작해도 반쯤 풀린 폴더를 쓰지 않는다.
        /// </summary>
        public static string Extract(Assembly assembly, string root, string version)
        {
            var dir = Path.Combine(root, version + "-" + ContentHash(assembly));
            if (File.Exists(Path.Combine(dir, "chat.html"))) return dir;

            Directory.CreateDirectory(root);
            var tmp = dir + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var name in ResourceNames(assembly))
                {
                    var target = Path.Combine(tmp, RelativePath(name).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, ReadBytes(assembly, name));
                }
                try { Directory.Move(tmp, dir); }
                catch (IOException) when (File.Exists(Path.Combine(dir, "chat.html"))) { /* 다른 인스턴스가 먼저 풀었다. */ }
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* 임시 폴더 정리는 최선 */ }
            }
            return dir;
        }
    }

    /// <summary>{t:'theme',vars:{...}} 메시지. 어두운 팔레트는 RAD Agent 기본값, 밝은 팔레트는 같은 Blend 식으로 만든다.</summary>
    internal static class ChatTheme
    {
        public const string HostName = "nanumcsv.local";

        private readonly record struct Rgb(int R, int G, int B)
        {
            public string Hex => $"#{R:x2}{G:x2}{B:x2}";
            public static Rgb Parse(string hex) => new(
                int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            /// <summary>from에서 to 쪽으로 t만큼(0~1) 섞는다.</summary>
            public static Rgb Blend(Rgb from, Rgb to, double t) => new(
                (int)Math.Round(from.R + (to.R - from.R) * t),
                (int)Math.Round(from.G + (to.G - from.G) * t),
                (int)Math.Round(from.B + (to.B - from.B) * t));
        }

        /// <summary>CSS 변수(접두 "--" 없이) → 값. 순서 유지.</summary>
        public static IReadOnlyDictionary<string, string> Palette(bool dark)
        {
            var vars = new Dictionary<string, string>();
            if (dark)
            {
                // RAD Agent 기본(어두움)
                vars["bg"] = "#1e1e1e"; vars["fg"] = "#d4d4d4"; vars["muted"] = "#858585"; vars["accent"] = "#007acc";
                vars["userBg"] = "#264f78"; vars["assistantBg"] = "#252526"; vars["codeBg"] = "#181818"; vars["border"] = "#3e3e42";
                vars["error"] = "#f48771"; vars["success"] = "#4ec9b0"; vars["warn"] = "#cca700"; vars["link"] = "#3794ff";
                vars["tokKeyword"] = "#569cd6"; vars["tokString"] = "#ce9178"; vars["tokComment"] = "#6a9955"; vars["tokNumber"] = "#b5cea8";
            }
            else
            {
                var bg = Rgb.Parse("#ffffff");
                var fg = Rgb.Parse("#1f1f1f");
                vars["bg"] = bg.Hex; vars["fg"] = fg.Hex;
                vars["muted"] = Rgb.Blend(fg, bg, 0.45).Hex;
                vars["accent"] = "#0066b8";
                vars["userBg"] = Rgb.Blend(bg, fg, 0.10).Hex;
                vars["assistantBg"] = bg.Hex;
                vars["codeBg"] = Rgb.Blend(bg, fg, 0.07).Hex;
                vars["border"] = Rgb.Blend(bg, fg, 0.20).Hex;
                vars["error"] = "#be1e1e"; vars["success"] = "#148232"; vars["warn"] = "#aa6e00"; vars["link"] = "#005ac8";
                vars["tokKeyword"] = "#0000c0"; vars["tokString"] = "#a31515"; vars["tokComment"] = "#008000"; vars["tokNumber"] = "#098658";
            }
            vars["scheme"] = dark ? "dark" : "light";
            return vars;
        }

        /// <summary>
        /// WinForms 글꼴(pt) → 페이지 px. 기본 9pt가 RAD Agent의 13px이 되도록 한다(약 1.44배). 9~24px로 제한.
        /// </summary>
        public static int FontPx(float points) => Math.Clamp((int)Math.Round(points * 1.444f), 9, 24);

        public static string FontStack(string? uiFontName)
        {
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(uiFontName)) names.Add(uiFontName.Trim());
            foreach (var n in new[] { "Segoe UI", "Malgun Gothic" })
                if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            return string.Join(", ", names.Select(n => "'" + n.Replace("'", "") + "'")) + ", sans-serif";
        }

        public const string MonoStack = "Consolas, 'D2Coding', 'Malgun Gothic', monospace";

        public static string ThemeMessage(bool dark, string? uiFontName, float uiFontPoints)
        {
            var vars = new Dictionary<string, object>();
            foreach (var kv in Palette(dark)) vars[kv.Key] = kv.Value;
            // 패널 머리글 공통 규격(상단 바의 높이·배경·글자·아래 1px 경계선): 앱 팔레트에서 온다.
            foreach (var kv in PanelChrome.CssVars(ThemePalette.For(dark ? AppTheme.Dark : AppTheme.Light))) vars[kv.Key] = kv.Value;
            vars["font"] = FontStack(uiFontName);
            vars["monoFont"] = MonoStack;
            vars["fontSize"] = FontPx(uiFontPoints);
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["t"] = "theme", ["vars"] = vars });
        }
    }

    /// <summary>{t:'strings',lang,items} 메시지. 문자열 표는 내장 lang/{ko,en}.json.</summary>
    internal static class ChatStrings
    {
        public static string Normalize(string? language) =>
            string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ko";

        public static string Message(Assembly assembly, string? language)
        {
            var lang = Normalize(language);
            var items = ChatAssetStore.ReadText(assembly, ChatAssetStore.ResourcePrefix + "lang/" + lang + ".json");
            // items는 검증된 JSON 객체 원문 그대로 끼워 넣는다(다시 직렬화하지 않는다).
            using (JsonDocument.Parse(items)) { }
            return "{\"t\":\"strings\",\"lang\":\"" + lang + "\",\"items\":" + items + "}";
        }
    }
}
