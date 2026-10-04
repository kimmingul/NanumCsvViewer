using System.Drawing;
using System.Globalization;

namespace NanumCsvViewer.Csv
{
    /// <summary>셀 하나에 규칙이 정한 스타일. 속성마다 "처음 맞은 규칙"의 값이며 규칙이 정하지 않은 속성은 null/false.</summary>
    public readonly record struct CellFormatResult(Color? Back, Color? Fore, bool Bold, bool ForeIsAuto = false)
    {
        public bool IsEmpty => Back is null && Fore is null && !Bold;
    }

    /// <summary>색상 눈금 계산에 쓰는 숫자 컬럼의 최소/최대(현재 뷰 기준, 규칙을 다시 만들 때 한 번 계산).</summary>
    public readonly record struct ScaleRange(double Min, double Max, long Count);

    /// <summary>
    /// 컴파일된 조건부 서식 규칙 집합. 규칙 목록의 순서가 우선순위이며, 한 셀의 배경·글자색·굵게는 각각 처음 맞은 규칙이 정한다.
    /// 식 규칙은 행마다 한 번 평가해 결과(bool[])를 호출자가 행 캐시에 보관하고, 컬럼마다 <see cref="Resolve"/>로 스타일을 얻는다 —
    /// 그리기 때마다 전체를 훑지 않는다. 색상 눈금은 <see cref="ComputeRange"/>로 한 번 구한 범위를 <see cref="SetScaleRange"/>에 넣는다
    /// (범위가 없으면 그 규칙은 색을 입히지 않는다). 컬럼 이름을 못 찾거나 식이 잘못된 규칙은 <see cref="Problems"/>에 모이고 적용되지 않는다.
    /// 정규식 시간 초과는 <see cref="TimeoutCount"/>로 센다(시간 초과 셀은 "일치하지 않음").
    /// </summary>
    public sealed class ConditionalFormatSet
    {
        public static readonly ConditionalFormatSet Empty = new(Array.Empty<Item>(), Array.Empty<(string, string)>());

        internal sealed class Item
        {
            public required ConditionalFormatRule Rule { get; init; }
            public int Column = -1;                       // 대상 컬럼 번호(Row 대상 식 규칙은 -1)
            public Func<string[], bool>? Predicate;
            public RegexTimeoutCounter? Timeouts;
            public Color? Back, Fore;
            public bool Bold;
            public Color ScaleMin, ScaleMax;
            public Color? ScaleMid;
            public volatile ScaleBox? Range;              // 색상 눈금 범위(null = 아직 계산 전/숫자 없음)
        }

        internal sealed class ScaleBox(ScaleRange range) { public readonly ScaleRange Range = range; }

        private readonly Item[] _items;

        private ConditionalFormatSet(Item[] items, IReadOnlyList<(string Id, string Problem)> problems)
        {
            _items = items;
            Problems = problems;
            HasExpressionRules = items.Any(i => i.Rule.Kind == ConditionalFormatKind.Expression);
            HasColorScale = items.Any(i => i.Rule.Kind == ConditionalFormatKind.ColorScale);
        }

        /// <summary>적용되지 않는 규칙과 이유(id, 문제).</summary>
        public IReadOnlyList<(string Id, string Problem)> Problems { get; }

        /// <summary>적용되는(켜져 있고 문제 없는) 규칙 수.</summary>
        public int ActiveCount => _items.Length;

        public bool IsEmpty => _items.Length == 0;

        /// <summary>식 규칙이 하나라도 있는가(행마다 평가가 필요한가).</summary>
        public bool HasExpressionRules { get; }

        /// <summary>색상 눈금 규칙이 하나라도 있는가(셀 값을 읽어야 하는가).</summary>
        public bool HasColorScale { get; }

        /// <summary>색상 눈금 규칙의 (규칙 id, 컬럼 번호). 범위 계산 요청용.</summary>
        public IReadOnlyList<(string Id, int Column)> ScaleRequests
            => _items.Where(i => i.Rule.Kind == ConditionalFormatKind.ColorScale).Select(i => (i.Rule.Id, i.Column)).ToArray();

        /// <summary>정규식 시간 초과로 "일치하지 않음" 처리된 셀 수(컴파일 이후 누적).</summary>
        public long TimeoutCount => _items.Sum(i => i.Timeouts?.Count ?? 0);

        /// <summary>규칙을 헤더 기준으로 컴파일. 비활성/문제 있는 규칙은 건너뛰고 문제는 Problems에 담는다.</summary>
        public static ConditionalFormatSet Compile(IReadOnlyList<ConditionalFormatRule> rules, IReadOnlyList<string> headers)
        {
            var items = new List<Item>();
            var problems = new List<(string, string)>();
            foreach (var rule in rules)
            {
                if (!rule.Enabled) continue;
                string? problem = ConditionalFormatRules.Validate(rule, headers);
                if (problem is not null) { problems.Add((rule.Id, problem)); continue; }

                var item = new Item { Rule = rule };
                if (rule.Kind == ConditionalFormatKind.Expression)
                {
                    var compiled = AdvancedFilterExpression.Compile(rule.Expression, headers);
                    item.Predicate = compiled.Predicate;
                    item.Timeouts = compiled.Timeouts;
                    item.Column = rule.Target == ConditionalFormatTarget.Cell ? ConditionalFormatRules.ResolveColumn(headers, rule.Column) : -1;
                    item.Back = ConditionalFormatRule.ParseColor(rule.BackColor);
                    item.Fore = ConditionalFormatRule.ParseColor(rule.ForeColor);
                    item.Bold = rule.Bold;
                }
                else
                {
                    item.Column = ConditionalFormatRules.ResolveColumn(headers, rule.Column);
                    item.ScaleMin = ConditionalFormatRule.ParseColor(rule.ScaleMinColor)!.Value;
                    item.ScaleMax = ConditionalFormatRule.ParseColor(rule.ScaleMaxColor)!.Value;
                    item.ScaleMid = ConditionalFormatRule.ParseColor(rule.ScaleMidColor);
                }
                items.Add(item);
            }
            return new ConditionalFormatSet(items.ToArray(), problems);
        }

        /// <summary>색상 눈금 규칙(id)의 범위를 넣는다. 없는 id는 무시.</summary>
        public void SetScaleRange(string ruleId, ScaleRange range)
        {
            foreach (var item in _items)
                if (item.Rule.Kind == ConditionalFormatKind.ColorScale && item.Rule.Id == ruleId)
                    item.Range = range.Count > 0 ? new ScaleBox(range) : null;
        }

        public ScaleRange? GetScaleRange(string ruleId)
            => _items.FirstOrDefault(i => i.Rule.Id == ruleId)?.Range?.Range;

        /// <summary>한 행에 대해 식 규칙을 평가한다(규칙 순서와 같은 배열). 식 규칙이 없으면 null.</summary>
        public bool[]? Evaluate(string[] row)
        {
            bool[]? matched = null;
            for (int i = 0; i < _items.Length; i++)
            {
                var p = _items[i].Predicate;
                if (p is null) continue;
                matched ??= new bool[_items.Length];
                bool hit;
                try { hit = p(row); }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { hit = false; }
                matched[i] = hit;
            }
            return matched;
        }

        /// <summary>
        /// column 셀의 스타일. matched = <see cref="Evaluate"/> 결과. 규칙 순서대로 보며 속성마다 처음 정해진 값을 쓴다.
        /// 배경이 정해졌는데 글자색을 정한 규칙이 없으면 글자색은 대비가 되도록 검정/흰색으로 자동 지정한다.
        /// </summary>
        public CellFormatResult Resolve(string[] row, bool[]? matched, int column)
        {
            Color? back = null, fore = null;
            bool bold = false;
            for (int i = 0; i < _items.Length; i++)
            {
                var item = _items[i];
                if (item.Rule.Kind == ConditionalFormatKind.Expression)
                {
                    if (matched is null || !matched[i]) continue;
                    if (item.Column >= 0 && item.Column != column) continue;
                    if (back is null && item.Back is { } b) back = b;
                    if (fore is null && item.Fore is { } f) fore = f;
                    if (item.Bold) bold = true;
                }
                else
                {
                    if (back is not null || item.Column != column || item.Range is not { } box) continue;
                    if ((uint)column >= (uint)row.Length || !TryNumber(row[column], out double v)) continue;
                    back = Interpolate(item, box.Range, v);
                }
            }
            bool auto = false;
            if (fore is null && back is { } shown) { fore = Contrast(shown); auto = true; }
            return new CellFormatResult(back, fore, bold, auto);
        }

        private static Color Interpolate(Item item, ScaleRange range, double v)
        {
            double t = range.Max > range.Min ? (v - range.Min) / (range.Max - range.Min) : 0.5;
            t = Math.Clamp(t, 0, 1);
            if (item.ScaleMid is { } mid)
                return t < 0.5 ? Lerp(item.ScaleMin, mid, t * 2) : Lerp(mid, item.ScaleMax, (t - 0.5) * 2);
            return Lerp(item.ScaleMin, item.ScaleMax, t);
        }

        private static Color Lerp(Color a, Color b, double t)
            => Color.FromArgb(255,
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));

        /// <summary>배경 위에서 읽기 쉬운 글자색(밝기 기준 검정/흰색).</summary>
        public static Color Contrast(Color back)
        {
            double lum = (0.299 * back.R + 0.587 * back.G + 0.114 * back.B) / 255.0;
            return lum > 0.55 ? Color.Black : Color.White;
        }

        private static bool TryNumber(string s, out double v)
        {
            if (NumericAffix.TryParseNumber(s, out v) && double.IsFinite(v)) return true;
            v = 0;
            return false;
        }

        /// <summary>뷰 행들에서 column의 숫자 최소/최대를 구한다(숫자가 아닌 값은 건너뜀). 취소되면 OperationCanceledException.</summary>
        public static ScaleRange ComputeRange(IReadOnlyList<string[]> rows, int column, CancellationToken ct)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            long count = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                var row = rows[i];
                if ((uint)column >= (uint)row.Length || !TryNumber(row[column], out double v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                count++;
            }
            return count == 0 ? new ScaleRange(0, 0, 0) : new ScaleRange(min, max, count);
        }

        /// <summary>
        /// 식 한 개의 일치 행 수(미리보기·에이전트용). limit행까지만 훑는다(0 이하 = 전부). budgetMs(0 이하 = 무제한)를 넘기면
        /// 그때까지 훑은 행 수로 끝낸다(RowsScanned &lt; 요청한 행 수). 식이 잘못되면 AdvancedFilterExpressionException.
        /// </summary>
        public static ConditionalFormatCount Count(string expression, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows,
            int limit, CancellationToken ct, int budgetMs = 0)
        {
            var compiled = AdvancedFilterExpression.Compile(expression, headers);
            int n = limit > 0 ? Math.Min(limit, rows.Count) : rows.Count;
            long matched = 0;
            int i = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (; i < n; i++)
            {
                if ((i & 0x3F) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (budgetMs > 0 && clock.ElapsedMilliseconds > budgetMs) break;
                }
                bool hit;
                try { hit = compiled.Predicate(rows[i]); }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { hit = false; }
                if (hit) matched++;
            }
            return new ConditionalFormatCount(i, matched, compiled.Timeouts.Count);
        }
    }

    /// <summary>
    /// 그리기용 행 단위 서식 캐시. 보이는 행만 요청하므로 뷰 전체(100만 행)를 훑지 않는다 — 행마다 식을 한 번 평가해 두고(행 id 기준 LRU 대용 일괄 비우기)
    /// 셀마다 규칙 순서대로 스타일만 합친다. 편집·규칙 변경이면 <see cref="Invalidate"/>. 정규식 시간 초과가 난 행은 <see cref="TimedOutRows"/>에 모은다.
    /// UI 스레드 전용.
    /// </summary>
    public sealed class ConditionalFormatStyler
    {
        private const int MaxTimedOutRows = 100_000;
        private readonly Dictionary<int, bool[]?> _rows = new();
        private readonly HashSet<int> _timedOut = new();
        private readonly int _capacity;

        public ConditionalFormatStyler(ConditionalFormatSet set, int capacity = 8192)
        {
            Set = set;
            _capacity = Math.Max(16, capacity);
        }

        public ConditionalFormatSet Set { get; }

        /// <summary>식을 실제로 평가한 행 수(캐시 적중은 세지 않음). 테스트·진단용.</summary>
        public long RowsEvaluated { get; private set; }

        /// <summary>정규식 시간 초과가 난 서로 다른 행 수(상한 100,000).</summary>
        public int TimedOutRowCount => _timedOut.Count;

        /// <summary>캐시를 비운다(편집·규칙·컬럼 변경). 시간 초과 기록은 유지한다.</summary>
        public void Invalidate() => _rows.Clear();

        /// <summary>
        /// rowId 행의 column 셀 스타일. row는 필요할 때만(캐시에 없거나 색상 눈금이 있을 때) 호출한다.
        /// </summary>
        public CellFormatResult Style(int rowId, Func<string[]> row, int column)
        {
            if (Set.IsEmpty) return default;
            bool[]? matched = null;
            string[]? fields = null;
            if (Set.HasExpressionRules && !_rows.TryGetValue(rowId, out matched))
            {
                fields = row();
                long before = Set.TimeoutCount;
                matched = Set.Evaluate(fields);
                RowsEvaluated++;
                if (Set.TimeoutCount != before && _timedOut.Count < MaxTimedOutRows) _timedOut.Add(rowId);
                if (_rows.Count >= _capacity) _rows.Clear();
                _rows[rowId] = matched;
            }
            if (Set.HasColorScale) fields ??= row();
            return Set.Resolve(fields ?? Array.Empty<string>(), matched, column);
        }
    }
}
