using System.Collections.Concurrent;
using System.Drawing;

namespace NanumCsvViewer.Csv
{
    /// <summary>색이 쓰이는 자리: 배경(연한 틴트) / 글자(틴트와 짝인 글자색) / 색상 눈금 끝점(진한 색).</summary>
    public enum ThemeColorRole { Back, Fore, Scale }

    /// <summary>
    /// 한 규칙 색의 라이트/다크 해석 결과. 이름 토큰(red…gray)은 테마별 짝 색이고, 직접 지정한 색(#RRGGBB·HTML 이름)은
    /// 라이트에서는 그대로, 다크에서는 <see cref="Adapt"/>이면 밝기를 조정한 색이다.
    /// 글자색 자리의 직접 지정 색은 배경이 정해진 뒤에야 대비를 맞출 수 있어 여기서는 원래 색 그대로 두고 <see cref="Resolve"/> 시점에 조정한다.
    /// </summary>
    internal readonly record struct ThemedColor(Color Light, Color Dark, Color? LightText, Color? DarkText, bool IsToken, bool Adapt)
    {
        public Color Get(bool dark) => dark ? Dark : Light;
        public Color? TextFor(bool dark) => dark ? DarkText : LightText;
    }

    /// <summary>
    /// 조건부 서식 색의 테마 처리. 이름 색(<see cref="Names"/>)은 라이트/다크마다 정해진 배경 틴트·글자색 짝을 쓰고,
    /// 직접 지정한 색은 다크 테마에서 명도를 조정(<see cref="AdaptBack"/>, <see cref="EnsureContrast"/>)해 읽기 좋게 한다.
    /// 규칙에는 이름 토큰 또는 "#RRGGBB"만 저장하므로 테마를 바꿔도 규칙을 다시 만들 필요가 없다.
    /// </summary>
    public static class ThemeColors
    {
        /// <summary>WCAG 보통 글자 대비 기준.</summary>
        public const double MinContrast = 4.5;

        /// <summary>격자 기본 배경(테마 팔레트 GridBg와 같은 값) — 규칙 배경이 없을 때 글자색 대비 기준.</summary>
        public static Color GridBackground(bool dark) => dark ? Color.FromArgb(37, 37, 38) : Color.White;

        private readonly record struct Swatch(Color LightBack, Color LightFore, Color DarkBack, Color DarkFore, Color LightScale, Color DarkScale);

        private static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        private static readonly (string Name, Swatch Swatch)[] s_table =
        {
            ("red",    new(C(0xFFC7CE), C(0x9C0006), C(0x6B2429), C(0xFFB3B8), C(0xF8696B), C(0x8E2F34))),
            ("orange", new(C(0xFFD8A8), C(0x8A4B00), C(0x6B4416), C(0xFFC47A), C(0xFFB35C), C(0x8F5A1C))),
            ("yellow", new(C(0xFFEB9C), C(0x6F5300), C(0x5E5214), C(0xF5DC6E), C(0xFFEB84), C(0x8A7B1E))),
            ("green",  new(C(0xC6EFCE), C(0x006100), C(0x1F4D2B), C(0x8FDB9E), C(0x63BE7B), C(0x2E7A47))),
            ("blue",   new(C(0xBDD7EE), C(0x1F4E79), C(0x1F3F66), C(0x8CC0F0), C(0x5A8AC6), C(0x2F5F99))),
            ("purple", new(C(0xE1D0F0), C(0x5B2C83), C(0x43296B), C(0xC9A8F0), C(0x9B6FD0), C(0x6B45A0))),
            ("gray",   new(C(0xE0E0E0), C(0x404040), C(0x4A4A4C), C(0xD0D0D0), C(0xA6A6A6), C(0x5C5C5E))),
        };

        /// <summary>이름 색 토큰(저장 형식: 소문자 이름 그대로).</summary>
        public static IReadOnlyList<string> Names { get; } = s_table.Select(t => t.Name).ToArray();

        private static bool TryToken(string? text, out Swatch swatch)
        {
            swatch = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string key = text.Trim();
            foreach (var (name, sw) in s_table)
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) { swatch = sw; return true; }
            return false;
        }

        public static bool IsToken(string? text) => TryToken(text, out _);

        /// <summary>토큰이면 그 이름(소문자)으로 정규화, 아니면 입력 그대로.</summary>
        public static string Normalize(string text)
            => TryToken(text, out _) ? text.Trim().ToLowerInvariant() : text.Trim();

        /// <summary>
        /// 규칙의 색 문자열을 테마에 맞는 색으로. 토큰이면 role에 맞는 짝 색, 직접 지정한 색이면 라이트=그대로,
        /// 다크+adapt=<see cref="AdaptBack"/>(글자색 자리는 그대로 — <see cref="EnsureContrast"/>는 배경을 알 때 호출). 해석할 수 없으면 null.
        /// </summary>
        public static Color? Resolve(string? text, ThemeColorRole role, bool dark, bool adapt)
            => Make(text, role, adapt) is { } t ? t.Get(dark) : null;

        internal static ThemedColor? Make(string? text, ThemeColorRole role, bool adapt)
        {
            if (TryToken(text, out var sw))
            {
                return role switch
                {
                    ThemeColorRole.Fore => new ThemedColor(sw.LightFore, sw.DarkFore, null, null, true, adapt),
                    ThemeColorRole.Scale => new ThemedColor(sw.LightScale, sw.DarkScale, null, null, true, adapt),
                    _ => new ThemedColor(sw.LightBack, sw.DarkBack, sw.LightFore, sw.DarkFore, true, adapt),
                };
            }
            if (ConditionalFormatRule.ParseColor(text) is not { } raw) return null;
            Color dark = role != ThemeColorRole.Fore && adapt ? AdaptBack(raw) : raw;
            return new ThemedColor(raw, dark, null, null, false, adapt);
        }

        // ------------------------------------------------------------ 명도 조정

        /// <summary>
        /// 라이트 테마용 배경색을 다크 테마용으로: 밝은 색(명도 &gt; 0.35)을 어둡게 뒤집고(흰색 → 격자 배경 근처, 연한 틴트 → 짙은 틴트) 채도를 낮춘다.
        /// 이미 어두운 색은 그대로. 색상(hue)은 유지한다.
        /// </summary>
        public static Color AdaptBack(Color c)
        {
            var (h, s, l) = ToHsl(c);
            if (l <= 0.35) return Color.FromArgb(255, c.R, c.G, c.B);
            return FromHsl(h, s * 0.55, 0.16 + (1 - l) * 0.30);
        }

        /// <summary>fore를 back 위에서 읽을 수 있게(대비 ≥ min) 명도만 최소한으로 옮긴다. 이미 충분하면 그대로.</summary>
        public static Color EnsureContrast(Color fore, Color back, double min = MinContrast)
        {
            fore = Color.FromArgb(255, fore.R, fore.G, fore.B);
            if (ContrastRatio(fore, back) >= min) return fore;
            var key = (fore.ToArgb(), back.ToArgb(), (int)(min * 100));
            if (s_contrastCache.TryGetValue(key, out int cached)) return Color.FromArgb(cached);

            bool lighten = ContrastRatio(Color.White, back) >= ContrastRatio(Color.Black, back);
            var (h, s, l) = ToHsl(fore);
            double extreme = lighten ? 1 : 0;
            Color result;
            if (ContrastRatio(FromHsl(h, s, extreme), back) < min) result = lighten ? Color.White : Color.Black;
            else
            {
                double bad = l, good = extreme; // good 쪽은 기준을 만족, bad 쪽은 불만족
                for (int i = 0; i < 24; i++)
                {
                    double mid = (bad + good) / 2;
                    if (ContrastRatio(FromHsl(h, s, mid), back) >= min) good = mid; else bad = mid;
                }
                result = FromHsl(h, s, good);
            }
            if (s_contrastCache.Count > 4096) s_contrastCache.Clear();
            s_contrastCache[key] = result.ToArgb();
            return result;
        }

        private static readonly ConcurrentDictionary<(int, int, int), int> s_contrastCache = new();

        /// <summary>WCAG 대비비(1~21).</summary>
        public static double ContrastRatio(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            if (la < lb) (la, lb) = (lb, la);
            return (la + 0.05) / (lb + 0.05);
        }

        public static double Luminance(Color c)
        {
            static double Lin(int v)
            {
                double x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        private static (double H, double S, double L) ToHsl(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2, d = max - min, h = 0, s = 0;
            if (d > 1e-9)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            return (h, s, l);
        }

        private static Color FromHsl(double h, double s, double l)
        {
            l = Math.Clamp(l, 0, 1);
            s = Math.Clamp(s, 0, 1);
            double r, g, b;
            if (s < 1e-9) r = g = b = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
                r = Hue(p, q, h + 1.0 / 3); g = Hue(p, q, h); b = Hue(p, q, h - 1.0 / 3);
            }
            return Color.FromArgb(255, (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
        }

        private static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }

    /// <summary>되돌리기/다시 실행 결과: 되돌린(다시 한) 변경의 설명(영어)과 그 뒤의 규칙 수.</summary>
    public sealed record ConditionalFormatUndoResult(string Description, int RuleCount);

    /// <summary>
    /// 조건부 서식 규칙 집합의 변경 이력(최대 <see cref="MaxEntries"/>개). 셀 편집 되돌리기(Ctrl+Z)와는 별개다.
    /// 변경마다 (설명, 변경 전 규칙, 변경 후 규칙)을 쌓는다. 새 변경을 기록하면 "다시 실행" 목록은 비운다. 규칙 목록이 그대로면 기록하지 않는다.
    /// </summary>
    public sealed class ConditionalFormatHistory
    {
        public const int MaxEntries = 20;

        public sealed record Entry(string Description, IReadOnlyList<ConditionalFormatRule> Before, IReadOnlyList<ConditionalFormatRule> After);

        private readonly List<Entry> _undo = new(), _redo = new();

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;
        public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
        public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

        /// <summary>변경을 기록한다. before와 after가 같으면 false(기록 안 함).</summary>
        public bool Record(string description, IReadOnlyList<ConditionalFormatRule> before, IReadOnlyList<ConditionalFormatRule> after)
        {
            if (before.SequenceEqual(after)) return false;
            _redo.Clear();
            _undo.Add(new Entry(description, before.ToArray(), after.ToArray()));
            if (_undo.Count > MaxEntries) _undo.RemoveAt(0);
            return true;
        }

        /// <summary>마지막 변경을 되돌린다. 돌아갈 규칙 집합(Before)을 담은 항목, 이력이 없으면 null.</summary>
        public Entry? Undo()
        {
            if (_undo.Count == 0) return null;
            var e = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(e);
            return e;
        }

        /// <summary>되돌린 변경을 다시 한다. 적용할 규칙 집합은 항목의 After, 없으면 null.</summary>
        public Entry? Redo()
        {
            if (_redo.Count == 0) return null;
            var e = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(e);
            return e;
        }

        public void Clear() { _undo.Clear(); _redo.Clear(); }
    }
}
