namespace NanumCsvViewer
{
    /// <summary>
    /// 단축키의 단일 소스. 메뉴 표시 문자열·툴바 툴팁·설정 대화상자의 "단축키" 쪽·사용법 도움말이 모두 이 표를 읽는다.
    /// 같은 키가 두 명령에 겹치면 <c>CommandShortcutTests</c>가 실패한다.
    /// </summary>
    internal static class CommandShortcuts
    {
        /// <param name="Id">명령 식별자(메뉴 항목 Tag와 같다).</param>
        /// <param name="En">영어 이름 — 메뉴 글자와 같다(대화상자를 여는 명령은 끝에 …).</param>
        /// <param name="Ko">한국어 이름.</param>
        /// <param name="Keys">기본 단축키.</param>
        /// <param name="Alternates">같은 명령의 보조 단축키(메뉴에는 표시하지 않는다).</param>
        /// <param name="DisplayOnly">true면 메뉴 항목에 키를 바인딩하지 않고 표시만 한다(ProcessCmdKey·그리드 키 처리기가 직접 받는 키).</param>
        internal sealed record Entry(string Id, string En, string Ko, Keys Keys, Keys[]? Alternates = null, bool DisplayOnly = false)
        {
            /// <summary>현재 언어의 이름(끝의 … 제외).</summary>
            public string Name => (Loc.CurrentLanguage == "ko" ? Ko : En).TrimEnd('…');

            /// <summary>화면에 보이는 키 문자열. 예: "Ctrl+Shift+F".</summary>
            public string KeyText => Keys == Keys.None ? "" : Display(Keys);
        }

        private const Keys C = Keys.Control, S = Keys.Shift, A = Keys.Alt;

        public static readonly IReadOnlyList<Entry> All = new Entry[]
        {
            new("file.open",          "Open…",                 "열기…",                 C | Keys.O),
            new("file.closeTab",      "Close Tab",             "탭 닫기",               C | Keys.W),
            new("file.openWorkspace", "Open Workspace…",       "작업 공간 열기…",       C | S | Keys.O),
            new("file.saveWorkspace", "Save Workspace",        "작업 공간 저장",        C | Keys.S),
            new("file.closeWorkspace", "Close Workspace",      "작업 공간 닫기",        Keys.None),   // 키 없음: 보편적인 키가 비어 있지 않다
            new("file.quit",          "Quit",                  "종료",                  C | Keys.Q),

            new("edit.undo",          "Undo",                  "되돌리기",              C | Keys.Z, DisplayOnly: true),
            new("edit.redo",          "Redo",                  "다시 실행",             C | Keys.Y, new[] { C | S | Keys.Z }, DisplayOnly: true),
            new("edit.copy",          "Copy",                  "복사",                  C | Keys.C, DisplayOnly: true),
            new("edit.paste",         "Paste Cells",           "셀 붙여넣기",           C | Keys.V, DisplayOnly: true),
            new("edit.clear",         "Clear Selected Cells",  "선택한 셀 지우기",      Keys.Delete, DisplayOnly: true),
            new("edit.cell",          "Edit Cell…",            "셀 편집…",              Keys.F2),
            new("edit.sheet",         "Sheet Edit Mode",       "시트 편집 모드",        C | S | Keys.E),
            new("edit.replace",       "Find & Replace (regex)…", "찾아 바꾸기 (정규식)…", C | Keys.H),

            new("data.find",          "Find…",                 "찾기…",                 C | Keys.F),
            new("data.findNext",      "Find Next",             "다음 찾기",             Keys.F3),
            new("data.filterByCell",  "Filter by This Cell Value", "이 셀 값으로 필터", C | Keys.B),
            new("data.advFilter",     "Advanced Filter…",      "고급 필터…",            C | S | Keys.F),
            new("data.clearFilter",   "Clear Filter",          "필터 해제",             C | S | Keys.L),
            new("data.clearSort",     "Clear Sort",            "정렬 해제",             C | A | Keys.S),
            new("data.goto",          "Go to Cell…",           "셀로 이동…",            C | Keys.G),

            new("view.detail",        "Row Detail Panel",      "행 상세 패널",          Keys.F4),
            new("view.facets",        "Facets Panel",          "패싯 패널",             Keys.F6),
            new("view.ai",            "AI Agent Panel",        "AI 에이전트 패널",      C | S | Keys.A),
            new("view.explorer",      "Workspace Explorer",    "작업 공간 탐색기",      C | S | Keys.W),
            new("view.nextTab",       "Next Tab",              "다음 탭",               C | Keys.Tab, DisplayOnly: true),
            new("view.prevTab",       "Previous Tab",          "이전 탭",               C | S | Keys.Tab, DisplayOnly: true),

            new("quality.profile",    "Run Quality Profile",   "품질 프로파일 실행",    C | S | Keys.Q),
            new("ws.newQuery",        "New Query…",            "새 질의…",              C | A | Keys.Q),
            new("tools.settings",     "Settings…",             "설정…",                 C | Keys.Oemcomma),
            new("tools.exportPythonBundle", "Export Python Analysis Bundle…", "Python 분석 재현 패키지 내보내기…", Keys.None),
            new("help.usage",         "How to Use",            "사용법",                Keys.F1),
        };

        private static readonly Dictionary<string, Entry> ById = All.ToDictionary(e => e.Id, StringComparer.Ordinal);

        public static Entry Get(string id) => ById.TryGetValue(id, out var e) ? e : throw new KeyNotFoundException("Unknown command id: " + id);

        public static string En(string id) => Get(id).En;

        public static string Ko(string id) => Get(id).Ko;

        public static Keys KeysOf(string id) => Get(id).Keys;

        /// <summary>단축키 문자열. 예: Ctrl+Shift+F · F4 · Ctrl+, · Del.</summary>
        public static string Display(Keys keys)
        {
            var parts = new List<string>(4);
            if ((keys & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keys & Keys.Alt) != 0) parts.Add("Alt");
            if ((keys & Keys.Shift) != 0) parts.Add("Shift");
            var key = keys & Keys.KeyCode;
            parts.Add(key switch
            {
                Keys.Oemcomma => ",",
                Keys.Delete => "Del",
                Keys.Return => "Enter",
                Keys.Escape => "Esc",
                _ => key.ToString(),
            });
            return string.Join("+", parts);
        }

        /// <summary>이름 + 단축키 툴팁. 예: "Open… (Ctrl+O)" — 키가 없으면 이름만.</summary>
        public static string Tip(string text, string id) => Get(id).Keys == Keys.None ? text.TrimEnd('…') : text.TrimEnd('…') + " (" + Get(id).KeyText + ")";

        /// <summary>모든 키(주키+보조키)를 (키, 명령 Id)로 펼친다 — 충돌 검사용.</summary>
        public static IEnumerable<(Keys Keys, string Id)> AllBindings()
        {
            foreach (var e in All)
            {
                if (e.Keys != Keys.None) yield return (e.Keys, e.Id);
                if (e.Alternates is { } alt) foreach (var k in alt) yield return (k, e.Id);
            }
        }

        /// <summary>눌린 키가 이 명령의 주키 또는 보조키인가.</summary>
        public static bool Matches(string id, Keys pressed)
        {
            var e = Get(id);
            return pressed == e.Keys || e.Alternates is { } alt && Array.IndexOf(alt, pressed) >= 0;
        }
    }
}
