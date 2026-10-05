namespace NanumCsvViewer.Workspace
{
    public enum CompletionKind { Keyword, Function, Schema, Table, View, Column }

    /// <summary>자동완성 후보 하나. <see cref="InsertText"/>는 필요하면 큰따옴표를 이미 붙인 삽입 문자열.</summary>
    public sealed record CompletionItem(string Label, string InsertText, CompletionKind Kind, string Detail = "");

    /// <summary>후보 목록과 바꿀 범위(<paramref name="Start"/>부터 <paramref name="Length"/>글자를 InsertText로 바꾼다).</summary>
    public sealed record CompletionResult(int Start, int Length, IReadOnlyList<CompletionItem> Items);

    /// <summary>
    /// SQL 자동완성(WinForms 비의존). 커서 앞의 글자와 한정자(<c>스키마.</c>·<c>표.</c>·별칭)를 보고
    /// 스키마·표·뷰·컬럼·키워드·함수를 제안한다. 컬럼은 질의에 FROM/JOIN으로 이미 쓴 표(별칭 포함)의 것을 먼저, 없으면 작업 공간 전체에서 보여 준다.
    /// </summary>
    public static class SqlCompletion
    {
        public static readonly string[] Keywords =
        {
            "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "DISTINCT", "AS", "ON", "USING",
            "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "UNION", "UNION ALL", "INTERSECT", "EXCEPT",
            "WITH", "CASE", "WHEN", "THEN", "ELSE", "END", "AND", "OR", "NOT", "NULL", "IS", "IN", "BETWEEN", "LIKE", "ILIKE", "EXISTS",
            "ASC", "DESC", "NULLS FIRST", "NULLS LAST", "OVER", "PARTITION BY", "QUALIFY", "TRUE", "FALSE", "ALL", "ANY",
        };

        public static readonly string[] Functions =
        {
            "count", "sum", "avg", "min", "max", "median", "stddev", "coalesce", "nullif", "cast", "try_cast", "lower", "upper", "trim",
            "length", "replace", "substr", "concat", "regexp_matches", "regexp_replace", "strftime", "strptime", "try_strptime", "date_trunc",
            "date_diff", "year", "month", "day", "round", "floor", "ceil", "abs", "row_number", "rank", "dense_rank", "lag", "lead",
            "string_agg", "list", "unnest", "greatest", "least", "if", "ifnull",
        };

        private static readonly HashSet<string> KeywordWords = new(
            Keywords.SelectMany(k => k.Split(' ')).Concat(new[] { "BY", "FIRST", "LAST", "BETWEEN", "INTO", "VALUES", "TABLE", "CAST" }),
            StringComparer.OrdinalIgnoreCase);

        /// <summary>구문 색에서 키워드로 칠 단어인가.</summary>
        public static bool IsKeyword(string word) => KeywordWords.Contains(word);

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>
        /// 커서 위치(<paramref name="caret"/>, 0부터)에서의 후보를 만든다. 커서가 문자열·주석 안이면 null.
        /// <paramref name="force"/>가 거짓이면 입력한 글자가 하나도 없고 한정자도 없을 때 null(자동 팝업이 너무 시끄럽지 않게).
        /// </summary>
        public static CompletionResult? Suggest(string sql, int caret, IReadOnlyList<WorkspaceSource> sources, IReadOnlyList<WorkspaceView> views, bool force = false)
        {
            caret = Math.Clamp(caret, 0, sql.Length);
            var tokens = SqlText.Tokenize(sql);
            foreach (var t in tokens)
            {
                bool closed = t.Length >= 2 && sql[t.End - 1] == (t.Kind == SqlTokenKind.String ? '\'' : '"');
                bool inside = caret > t.Start && (caret < t.End || (caret == t.End && !closed && t.Kind != SqlTokenKind.Comment));
                if (!inside && !(t.Kind == SqlTokenKind.Comment && caret > t.Start && caret <= t.End)) continue;
                if (t.Kind is SqlTokenKind.Comment or SqlTokenKind.String) return null;
            }

            // 접두어: 커서 앞의 식별자 글자들(따옴표 식별자 안이면 여는 따옴표부터).
            int i = caret;
            while (i > 0 && IsIdentChar(sql[i - 1])) i--;
            int start = i;
            bool quoted = false;
            var q = tokens.FirstOrDefault(t => t.Kind == SqlTokenKind.QuotedIdentifier && caret > t.Start
                && (caret < t.End || (caret == t.End && !(t.Length >= 2 && sql[t.End - 1] == '"'))));
            if (q.Length > 0)
            {
                quoted = true;
                start = q.Start;
                // 접두어는 따옴표 다음부터 커서까지
            }
            string prefix = quoted ? sql.Substring(q.Start + 1, caret - q.Start - 1) : sql.Substring(start, caret - start);
            int replaceEnd = caret;
            if (quoted && caret < sql.Length && sql[caret] == '"') replaceEnd = caret + 1; // 닫는 따옴표까지 바꾼다

            // 한정자: 접두어 시작 바로 앞이 '.' 이면 그 앞의 식별자
            string? qualifier = null;
            int dot = start - 1;
            if (dot >= 0 && sql[dot] == '.')
            {
                int e = dot; // qualifier는 [qs, e)
                int qs;
                if (e > 0 && sql[e - 1] == '"')
                {
                    qs = sql.LastIndexOf('"', e - 2);
                    qualifier = qs >= 0 ? sql.Substring(qs + 1, e - qs - 2).Replace("\"\"", "\"") : null;
                }
                else
                {
                    qs = e;
                    while (qs > 0 && IsIdentChar(sql[qs - 1])) qs--;
                    qualifier = e > qs ? sql[qs..e] : null;
                }
            }

            if (!force && prefix.Length == 0 && qualifier is null) return null;

            var items = new List<CompletionItem>();
            var relations = ReferencedRelations(tokens, sql, sources, views);

            if (qualifier is not null)
            {
                // 스키마. → 표 목록
                var db = sources.FirstOrDefault(s => s.Kind == WorkspaceSourceKind.Database && string.Equals(s.Name, qualifier, StringComparison.OrdinalIgnoreCase));
                if (db is not null)
                    foreach (var t in db.Tables)
                        items.Add(new CompletionItem(t.Name, SqlNames.QuoteIfNeeded(t.Name), CompletionKind.Table, t.Columns.Count + " cols"));
                else
                {
                    // 표/뷰 이름 또는 별칭. → 컬럼 목록
                    IWorkspaceRelation? rel = null;
                    if (relations.AliasMap.TryGetValue(qualifier, out var viaAlias)) rel = viaAlias;
                    else rel = FindRelation(qualifier, sources, views);
                    if (rel is not null)
                        foreach (var c in rel.Columns)
                            items.Add(ColumnItem(c));
                }
            }
            else
            {
                foreach (var s in sources)
                {
                    if (s.Kind == WorkspaceSourceKind.Database)
                        items.Add(new CompletionItem(s.Name, SqlNames.QuoteIfNeeded(s.Name), CompletionKind.Schema, s.Tables.Count + " tables"));
                    else
                        items.Add(new CompletionItem(s.Name, SqlNames.QuoteIfNeeded(s.Name), CompletionKind.Table, s.Tables[0].Columns.Count + " cols"));
                }
                foreach (var v in views)
                    items.Add(new CompletionItem(v.Name, SqlNames.QuoteIfNeeded(v.Name), CompletionKind.View, v.Columns.Count + " cols"));

                var colSources = relations.Relations.Count > 0
                    ? relations.Relations
                    : sources.SelectMany(s => s.Tables).Cast<IWorkspaceRelation>().Concat(views).ToList();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rel in colSources)
                    foreach (var c in rel.Columns)
                        if (seen.Add(c.Name)) items.Add(ColumnItem(c));
                // 별칭도 후보
                foreach (var a in relations.AliasMap.Keys)
                    items.Add(new CompletionItem(a, SqlNames.QuoteIfNeeded(a), CompletionKind.Table, "alias"));

                foreach (var k in Keywords) items.Add(new CompletionItem(k, k, CompletionKind.Keyword));
                foreach (var f in Functions) items.Add(new CompletionItem(f, f + "(", CompletionKind.Function));
            }

            var ranked = items
                .Select(it => (Item: it, Score: Score(it.Label, prefix)))
                .Where(x => x.Score >= 0)
                .OrderBy(x => x.Score)
                .ThenBy(x => KindOrder(x.Item.Kind))
                .ThenBy(x => x.Item.Label, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Item)
                .DistinctBy(x => (x.Kind, x.Label))
                .Take(60)
                .ToList();
            if (ranked.Count == 0) return null;
            // 이미 완성된 한 단어와 같으면 팝업을 띄우지 않는다.
            if (!force && ranked.Count == 1 && string.Equals(ranked[0].Label, prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return new CompletionResult(start, replaceEnd - start, ranked);
        }

        private static CompletionItem ColumnItem(WorkspaceColumn c)
            => new(c.Name, SqlNames.QuoteIfNeeded(c.Name), CompletionKind.Column, c.IsConverted ? c.SqlType : c.Type.ToString());

        private static int KindOrder(CompletionKind k) => k switch
        {
            CompletionKind.Column => 0, CompletionKind.Table => 1, CompletionKind.View => 1, CompletionKind.Schema => 1,
            CompletionKind.Keyword => 2, _ => 3,
        };

        /// <summary>0 = 접두어로 시작, 1 = 포함, -1 = 불일치(대소문자 무시).</summary>
        private static int Score(string label, string prefix)
        {
            if (prefix.Length == 0) return 0;
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
            return label.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        }

        private static IWorkspaceRelation? FindRelation(string name, IReadOnlyList<WorkspaceSource> sources, IReadOnlyList<WorkspaceView> views)
        {
            foreach (var s in sources)
                if (s.Kind == WorkspaceSourceKind.Csv && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s.Tables[0];
            foreach (var v in views)
                if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }

        private sealed record Referenced(List<IWorkspaceRelation> Relations, Dictionary<string, IWorkspaceRelation> AliasMap);

        /// <summary>FROM·JOIN 뒤에 쓴 표(스키마.표 포함)와 별칭(<c>FROM 표 AS x</c>, <c>FROM 표 x</c>)을 모은다.</summary>
        private static Referenced ReferencedRelations(List<SqlToken> tokens, string sql, IReadOnlyList<WorkspaceSource> sources, IReadOnlyList<WorkspaceView> views)
        {
            var result = new Referenced(new List<IWorkspaceRelation>(), new Dictionary<string, IWorkspaceRelation>(StringComparer.OrdinalIgnoreCase));
            var sig = tokens.Where(t => t.Kind is not (SqlTokenKind.Whitespace or SqlTokenKind.Comment)).ToList();
            string Text(SqlToken t) => sql.Substring(t.Start, t.Length);
            string Ident(SqlToken t) => t.Kind == SqlTokenKind.QuotedIdentifier && t.Length >= 2 ? Text(t)[1..^1].Replace("\"\"", "\"") : Text(t);
            bool IsIdent(SqlToken t) => t.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;
            bool IsPunct(SqlToken t, char c) => t.Kind == SqlTokenKind.Punctuation && sql[t.Start] == c;

            for (int i = 0; i < sig.Count; i++)
            {
                string w = sig[i].Kind == SqlTokenKind.Word ? Text(sig[i]) : "";
                bool starts = w.Equals("FROM", StringComparison.OrdinalIgnoreCase) || w.Equals("JOIN", StringComparison.OrdinalIgnoreCase) || IsPunct(sig[i], ',');
                int j = i + 1;
                if (!starts || j >= sig.Count || !IsIdent(sig[j])) continue;
                string first = Ident(sig[j]);
                IWorkspaceRelation? rel;
                int next = j + 1;
                if (next + 1 < sig.Count && IsPunct(sig[next], '.') && IsIdent(sig[next + 1]))
                {
                    string tbl = Ident(sig[next + 1]);
                    var db = sources.FirstOrDefault(s => s.Kind == WorkspaceSourceKind.Database && string.Equals(s.Name, first, StringComparison.OrdinalIgnoreCase));
                    rel = db?.Tables.FirstOrDefault(t => string.Equals(t.Name, tbl, StringComparison.OrdinalIgnoreCase));
                    next += 2;
                }
                else rel = FindRelation(first, sources, views);
                if (rel is null) continue;
                if (!result.Relations.Contains(rel)) result.Relations.Add(rel);
                // 별칭: FROM 표 AS x / FROM 표 x ("FROM a x, b y" 의 쉼표 뒤는 위 for가 ','에서 다시 처리한다)
                int a = next;
                if (a < sig.Count && sig[a].Kind == SqlTokenKind.Word && Text(sig[a]).Equals("AS", StringComparison.OrdinalIgnoreCase)) a++;
                if (a < sig.Count && IsIdent(sig[a]) && !(sig[a].Kind == SqlTokenKind.Word && IsKeyword(Text(sig[a]))))
                    result.AliasMap[Ident(sig[a])] = rel;
            }
            return result;
        }
    }
}
