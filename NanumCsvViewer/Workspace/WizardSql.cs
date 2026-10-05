using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Workspace
{
    // 마법사(이어 붙이기·비교·그룹 집계)가 만드는 SELECT 문. 순수 문자열 생성 — DuckDB에 연결하지 않으므로 UI·에이전트·테스트가 같이 쓴다.
    // 모든 식별자는 큰따옴표로 감싸고 값은 SqlNames.Literal로 이스케이프한다. 조인 키 식은 JoinSql.PlanKeys와 공유한다.

    public enum UnionMode { ByName, ByPosition }

    /// <summary>이어 붙인 결과의 컬럼 하나: 출력 이름과, 입력 표마다 어느 컬럼을 쓸지(없으면 null → NULL).</summary>
    public sealed record AppendColumn(string OutputName, IReadOnlyList<string?> Sources);

    /// <param name="Tables">이어 붙일 표·뷰(2개 이상, 순서대로).</param>
    /// <param name="Columns">컬럼 매핑. null이면 <paramref name="Mode"/>로 자동 매핑.</param>
    /// <param name="SourceColumnName">null이 아니면 이 이름의 마지막 컬럼에 입력 표의 표시 이름을 넣는다.</param>
    public sealed record AppendSpec(IReadOnlyList<IWorkspaceRelation> Tables, IReadOnlyList<AppendColumn>? Columns = null,
        string? SourceColumnName = null, UnionMode Mode = UnionMode.ByName);

    public enum AppendTypeIssue
    {
        None,
        /// <summary>정수 ↔ 실수, 날짜 ↔ 날짜시각처럼 값을 잃지 않는 넓히기.</summary>
        Widened,
        /// <summary>서로 호환되지 않는 형 → 글자(VARCHAR)로 합침.</summary>
        ConvertedToText,
    }

    public sealed record AppendPlanColumn(string OutputName, IReadOnlyList<WorkspaceColumn?> Sources, string SqlType, AppendTypeIssue Issue);

    /// <summary>매핑을 분석한 결과: 컬럼별 결과 형·형 충돌, 사람이 읽을 경고.</summary>
    public sealed record AppendPlan(IReadOnlyList<IWorkspaceRelation> Tables, IReadOnlyList<AppendPlanColumn> Columns,
        string? SourceColumnName, IReadOnlyList<string> Warnings);

    /// <param name="Keys">행을 짝짓는 키(왼쪽 컬럼, 오른쪽 컬럼). 1개 이상.</param>
    /// <param name="Columns">값을 비교할 컬럼 쌍. null이면 키가 아닌 같은 이름 컬럼 전부.</param>
    /// <param name="IgnoreCase">글자 값·키를 비교할 때 대소문자를 무시.</param>
    /// <param name="TrimWhitespace">글자 값·키를 비교할 때 앞뒤 공백을 없애고 연속 공백(탭·줄바꿈·전각 공백 포함)을 하나로 본다.</param>
    /// <param name="IncludeUnchanged">결과에 변경 없음 행도 포함.</param>
    /// <param name="Long">true면 바뀐 셀마다 한 행(긴 형식), false면 행마다 컬럼별 이전·이후 값(넓은 형식).</param>
    public sealed record CompareSpec(IWorkspaceRelation Left, IWorkspaceRelation Right, IReadOnlyList<JoinKey> Keys,
        IReadOnlyList<JoinKey>? Columns = null, bool IgnoreCase = false, bool TrimWhitespace = false, bool IncludeUnchanged = false, bool Long = false);

    public enum GroupFunction { Count, Sum, Avg, Min, Max, CountDistinct, Median }

    /// <param name="Column">대상 컬럼. <see cref="GroupFunction.Count"/>만 null(= 행 수 count(*))을 허용한다.</param>
    public sealed record GroupAggregate(GroupFunction Function, string? Column, string? Alias = null);

    public sealed record GroupSpec(IWorkspaceRelation Table, IReadOnlyList<string> GroupBy, IReadOnlyList<GroupAggregate> Aggregates);

    public static class WizardSql
    {
        private static string Q(string name) => SqlNames.Quote(name);

        private static WorkspaceColumn? TryFind(IWorkspaceRelation rel, string name)
        {
            foreach (var c in rel.Columns)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        private static WorkspaceColumn Find(IWorkspaceRelation rel, string name)
            => TryFind(rel, name) ?? throw new ArgumentException(ViewerSupport.LT(
                $"'{rel.DisplayName}' has no column '{name}'.", $"'{rel.DisplayName}'에 '{name}' 컬럼이 없습니다."));

        private static bool IsNumeric(string sqlType) => TypedColumnSql.FromSqlType(sqlType) is ColumnValueType.Integer or ColumnValueType.Float;
        private static bool IsTemporal(string sqlType) => TypedColumnSql.FromSqlType(sqlType) is ColumnValueType.Date or ColumnValueType.DateTime;
        private static bool IsText(string sqlType) => TypedColumnSql.FromSqlType(sqlType) == ColumnValueType.String;

        private static string Unique(string baseName, HashSet<string> taken)
        {
            string name = baseName;
            for (int n = 2; !taken.Add(name); n++) name = baseName + "_" + n;
            return name;
        }

        // =====================================================================================
        // 이어 붙이기
        // =====================================================================================

        /// <summary>
        /// 자동 매핑. ByName: 이름(대소문자 무시)이 같은 컬럼끼리, 처음 나온 순서·철자로 합집합. ByPosition: 위치(n번째 컬럼)끼리,
        /// 이름은 그 위치 컬럼이 있는 첫 표의 것.
        /// </summary>
        public static IReadOnlyList<AppendColumn> AutoMap(IReadOnlyList<IWorkspaceRelation> tables, UnionMode mode)
        {
            var names = new List<string>();
            var sources = new List<string?[]>();
            if (mode == UnionMode.ByPosition)
            {
                int max = tables.Count == 0 ? 0 : tables.Max(t => t.Columns.Count);
                for (int i = 0; i < max; i++)
                {
                    var row = new string?[tables.Count];
                    string? name = null;
                    for (int t = 0; t < tables.Count; t++)
                        if (i < tables[t].Columns.Count) { row[t] = tables[t].Columns[i].Name; name ??= row[t]; }
                    names.Add(name!);
                    sources.Add(row);
                }
            }
            else
            {
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int t = 0; t < tables.Count; t++)
                    foreach (var c in tables[t].Columns)
                    {
                        if (!index.TryGetValue(c.Name, out int at))
                        {
                            at = names.Count;
                            index[c.Name] = at;
                            names.Add(c.Name);
                            sources.Add(new string?[tables.Count]);
                        }
                        sources[at][t] ??= c.Name;
                    }
            }
            // 위치 모드에서 이름이 겹칠 수 있다(서로 다른 위치에 같은 이름) — 출력 이름은 유일해야 한다.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return names.Select((n, i) => new AppendColumn(Unique(n, taken), sources[i])).ToArray();
        }

        /// <summary>매핑을 검증하고 컬럼별 결과 형을 정한다(호환되지 않는 형은 글자로 합치고 경고).</summary>
        public static AppendPlan PlanAppend(AppendSpec spec)
        {
            var tables = spec.Tables;
            if (tables.Count < 2) throw new ArgumentException(ViewerSupport.LT("Choose at least two tables to append.", "이어 붙일 표를 2개 이상 고르세요."));
            var mapping = spec.Columns ?? AutoMap(tables, spec.Mode);
            if (mapping.Count == 0) throw new ArgumentException(ViewerSupport.LT("There are no columns to append.", "이어 붙일 컬럼이 없습니다."));

            var outNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columns = new List<AppendPlanColumn>();
            var warnings = new List<string>();
            foreach (var m in mapping)
            {
                if (string.IsNullOrWhiteSpace(m.OutputName))
                    throw new ArgumentException(ViewerSupport.LT("A result column has no name.", "이름이 없는 결과 컬럼이 있습니다."));
                if (!outNames.Add(m.OutputName))
                    throw new ArgumentException(ViewerSupport.LT($"Duplicate result column name '{m.OutputName}'.", $"결과 컬럼 이름 '{m.OutputName}'이 겹칩니다."));
                if (m.Sources.Count != tables.Count)
                    throw new ArgumentException($"Column '{m.OutputName}' maps {m.Sources.Count} sources for {tables.Count} tables.");
                var src = new WorkspaceColumn?[tables.Count];
                for (int t = 0; t < tables.Count; t++)
                    if (m.Sources[t] is { } sc) src[t] = Find(tables[t], sc);

                var present = src.Where(c => c is not null).Select(c => c!).ToList();
                var distinct = present.Select(c => c.SqlType.ToUpperInvariant()).Distinct().ToList();
                string type;
                var issue = AppendTypeIssue.None;
                if (distinct.Count == 0) type = "VARCHAR";
                else if (distinct.Count == 1) type = present[0].SqlType;
                else if (distinct.All(IsNumeric))
                {
                    type = distinct.All(d => TypedColumnSql.FromSqlType(d) == ColumnValueType.Integer) ? "BIGINT" : "DOUBLE";
                    issue = AppendTypeIssue.Widened;
                }
                else if (distinct.All(IsTemporal)) { type = "TIMESTAMP"; issue = AppendTypeIssue.Widened; }
                else
                {
                    type = "VARCHAR";
                    issue = AppendTypeIssue.ConvertedToText;
                    string detail = string.Join(", ", Enumerable.Range(0, tables.Count).Where(t => src[t] is not null)
                        .Select(t => $"{tables[t].DisplayName}: {src[t]!.SqlType}"));
                    warnings.Add(ViewerSupport.LT(
                        $"Column '{m.OutputName}' has incompatible types ({detail}); the values are combined as text.",
                        $"'{m.OutputName}' 컬럼의 형이 서로 달라({detail}) 글자로 합칩니다."));
                }
                columns.Add(new AppendPlanColumn(m.OutputName, src, type, issue));
            }
            if (spec.SourceColumnName is { } sn)
            {
                if (string.IsNullOrWhiteSpace(sn)) throw new ArgumentException(ViewerSupport.LT("The source column needs a name.", "출처 컬럼에 이름을 지정하세요."));
                if (outNames.Contains(sn))
                    throw new ArgumentException(ViewerSupport.LT($"The source column name '{sn}' is already a result column.", $"출처 컬럼 이름 '{sn}'이 이미 결과 컬럼에 있습니다."));
            }
            return new AppendPlan(tables, columns, spec.SourceColumnName, warnings);
        }

        /// <summary>UNION ALL 문. 매핑에 없는 입력 컬럼은 버리고, 입력에 없는 컬럼은 NULL로 채운다.</summary>
        public static string AppendSql(AppendSpec spec)
        {
            var plan = PlanAppend(spec);
            var parts = new List<string>();
            for (int t = 0; t < plan.Tables.Count; t++)
            {
                var table = plan.Tables[t];
                // 글자로 합쳐지는 컬럼이 숫자·날짜로 변환된 표 컬럼이면 변환 전 원문을 쓴다(1,000 → 1000으로 바뀌지 않게).
                bool useRaw = table is WorkspaceTable && plan.Columns.Any(pc =>
                    pc.Issue == AppendTypeIssue.ConvertedToText && pc.Sources[t] is { IsConverted: true });
                var items = new List<string>();
                foreach (var pc in plan.Columns)
                {
                    var c = pc.Sources[t];
                    string expr;
                    if (c is null) expr = $"CAST(NULL AS {pc.SqlType})";
                    else
                    {
                        string col = "t." + Q(c.Name);
                        if (useRaw && c.IsConverted)
                        {
                            // 원문(VARCHAR) 뷰에서 읽는 표: 글자로 합치는 컬럼은 원문 그대로, 나머지는 표와 같은 변환 식.
                            expr = pc.Issue == AppendTypeIssue.ConvertedToText ? col : TypedColumnSql.Expression(c.Type, col)!;
                            if (pc.Issue != AppendTypeIssue.ConvertedToText && !string.Equals(c.SqlType, pc.SqlType, StringComparison.OrdinalIgnoreCase))
                                expr = $"CAST({expr} AS {pc.SqlType})";
                        }
                        else expr = string.Equals(c.SqlType, pc.SqlType, StringComparison.OrdinalIgnoreCase) ? col : $"CAST({col} AS {pc.SqlType})";
                    }
                    items.Add($"{expr} AS {Q(pc.OutputName)}");
                }
                if (plan.SourceColumnName is { } sn) items.Add($"{SqlNames.Literal(table.DisplayName)} AS {Q(sn)}");
                string from = useRaw ? ((WorkspaceTable)table).RawSqlReference : table.SqlReference;
                parts.Add($"SELECT {string.Join(", ", items)}\nFROM {from} AS t");
            }
            return string.Join("\nUNION ALL\n", parts);
        }

        // =====================================================================================
        // 비교
        // =====================================================================================

        /// <summary>비교할 컬럼 쌍. <see cref="CompareSpec.Columns"/>가 null이면 키가 아닌 같은 이름 컬럼 전부(왼쪽 표 순서).</summary>
        public static IReadOnlyList<JoinKey> ResolveCompareColumns(CompareSpec spec)
        {
            if (spec.Columns is not null) return spec.Columns;
            var keyNames = new HashSet<string>(spec.Keys.SelectMany(k => new[] { k.LeftColumn, k.RightColumn }), StringComparer.OrdinalIgnoreCase);
            var list = new List<JoinKey>();
            foreach (var c in spec.Left.Columns)
            {
                if (keyNames.Contains(c.Name)) continue;
                if (TryFind(spec.Right, c.Name) is { } rc) list.Add(new JoinKey(c.Name, rc.Name));
            }
            return list;
        }

        /// <summary>키·비교 컬럼의 형이 달라 글자로 비교하는 경우의 경고.</summary>
        public static IReadOnlyList<string> CompareWarnings(CompareSpec spec)
        {
            var w = JoinSql.PlanKeys(new JoinSpec(spec.Left, spec.Right, spec.Keys)).Where(p => p.Warning is not null).Select(p => p.Warning!).ToList();
            var cols = ResolveCompareColumns(spec);
            if (cols.Count > 0)
                w.AddRange(JoinSql.PlanKeys(new JoinSpec(spec.Left, spec.Right, cols)).Where(p => p.Warning is not null).Select(p => p.Warning!));
            return w;
        }

        public const string ChangeAdded = "added", ChangeRemoved = "removed", ChangeChanged = "changed",
            ChangeUnchanged = "unchanged", ChangeDuplicateKey = "duplicate_key";

        private sealed record CompareKey(string OutName, string LeftDisplay, string RightDisplay, string LeftNorm, string RightNorm, string SqlType);
        private sealed record CompareValue(string Label, string OutOld, string OutNew, string LeftExpr, string RightExpr, bool Text, string LeftType, string RightType);
        private sealed record CompareParts(string Ctes, List<CompareKey> Keys, List<CompareValue> Values, string NullKeyLeft, string NullKeyRight);

        private static string Norm(string expr, bool text, CompareSpec spec)
        {
            if (!text) return expr;
            if (spec.TrimWhitespace) expr = $"trim(regexp_replace({expr}, '[\\s\\x{{00A0}}\\x{{3000}}]+', ' ', 'g'))";
            if (spec.IgnoreCase) expr = $"lower({expr})";
            return expr;
        }

        private static CompareParts BuildCompare(CompareSpec spec, HashSet<string> reservedNames)
        {
            if (spec.Keys.Count == 0) throw new ArgumentException(ViewerSupport.LT("Choose at least one key column.", "키 컬럼을 하나 이상 고르세요."));
            var keyPlan = JoinSql.PlanKeys(new JoinSpec(spec.Left, spec.Right, spec.Keys));
            var cols = ResolveCompareColumns(spec);
            var colPlan = cols.Count == 0 ? new List<JoinSql.KeyPlan>() : JoinSql.PlanKeys(new JoinSpec(spec.Left, spec.Right, cols));

            var taken = new HashSet<string>(reservedNames, StringComparer.OrdinalIgnoreCase);
            var keys = new List<CompareKey>();
            for (int i = 0; i < spec.Keys.Count; i++)
            {
                var lc = Find(spec.Left, spec.Keys[i].LeftColumn);
                var rc = Find(spec.Right, spec.Keys[i].RightColumn);
                bool text = keyPlan[i].Warning is not null || IsText(lc.SqlType);
                string type = keyPlan[i].Warning is not null ? "VARCHAR" : lc.SqlType;
                keys.Add(new CompareKey(Unique(lc.Name, taken), keyPlan[i].LeftExpr, keyPlan[i].RightExpr,
                    Norm(keyPlan[i].LeftExpr, text, spec), Norm(keyPlan[i].RightExpr, text, spec), type));
            }
            var values = new List<CompareValue>();
            for (int j = 0; j < cols.Count; j++)
            {
                var lc = Find(spec.Left, cols[j].LeftColumn);
                var rc = Find(spec.Right, cols[j].RightColumn);
                bool cast = colPlan[j].Warning is not null;
                string oldName = Unique(lc.Name + "_old", taken), newName = Unique(lc.Name + "_new", taken);
                values.Add(new CompareValue(lc.Name, oldName, newName, colPlan[j].LeftExpr, colPlan[j].RightExpr,
                    cast || IsText(lc.SqlType), cast ? "VARCHAR" : lc.SqlType, cast ? "VARCHAR" : rc.SqlType));
            }

            int nk = keys.Count;
            string kcols(string prefix) => string.Join(", ", Enumerable.Range(0, nk).Select(i => prefix + i));
            string eq(string a, string b) => string.Join(" AND ", Enumerable.Range(0, nk).Select(i => $"{a}.k{i} = {b}.k{i}"));
            string nonNull = string.Join(" AND ", Enumerable.Range(0, nk).Select(i => $"k{i} IS NOT NULL"));
            string anyNull = string.Join(" OR ", Enumerable.Range(0, nk).Select(i => $"k{i} IS NULL"));

            string side(string cte, string rel, string alias, bool left)
            {
                var items = new List<string>();
                for (int i = 0; i < nk; i++)
                {
                    items.Add($"{(left ? keys[i].LeftDisplay : keys[i].RightDisplay)} AS kd{i}");
                    items.Add($"{(left ? keys[i].LeftNorm : keys[i].RightNorm)} AS k{i}");
                }
                for (int j = 0; j < values.Count; j++) items.Add($"{(left ? values[j].LeftExpr : values[j].RightExpr)} AS v{j}");
                items.Add("1 AS p");
                return $"{cte} AS (SELECT {string.Join(", ", items)} FROM {rel} AS {alias})";
            }
            string grp(string cte, string src)
                => $"{cte} AS (SELECT {kcols("k")}, {string.Join(", ", Enumerable.Range(0, nk).Select(i => $"any_value(kd{i}) AS kd{i}"))}, count(*) AS n FROM {src} WHERE {nonNull} GROUP BY {kcols("k")})";

            var dkKeys = string.Join(", ", Enumerable.Range(0, nk).Select(i => $"coalesce(lg.k{i}, rg.k{i}) AS k{i}, coalesce(lg.kd{i}, rg.kd{i}) AS kd{i}"));
            var mItems = new List<string>();
            for (int i = 0; i < nk; i++) mItems.Add($"coalesce(l1.kd{i}, r1.kd{i}) AS kd{i}");
            for (int j = 0; j < values.Count; j++) mItems.Add($"l1.v{j} AS lv{j}");
            for (int j = 0; j < values.Count; j++) mItems.Add($"r1.v{j} AS rv{j}");
            mItems.Add("l1.p IS NOT NULL AS has_l");
            mItems.Add("r1.p IS NOT NULL AS has_r");
            for (int j = 0; j < values.Count; j++)
                mItems.Add($"({Norm($"l1.v{j}", values[j].Text, spec)} IS DISTINCT FROM {Norm($"r1.v{j}", values[j].Text, spec)}) AS d{j}");

            string anyChanged = values.Count == 0 ? "FALSE" : string.Join(" OR ", Enumerable.Range(0, values.Count).Select(j => $"d{j}"));
            string changedNames = values.Count == 0 ? "NULL"
                : "concat_ws(', ', " + string.Join(", ", values.Select((v, j) => $"CASE WHEN d{j} THEN {SqlNames.Literal(v.Label)} END")) + ")";
            string anyKeyNull = string.Join(" OR ", Enumerable.Range(0, nk).Select(i => $"kd{i} IS NULL"));
            string nullKeyText = SqlNames.Literal(ViewerSupport.LT("key is empty (NULL) — cannot be matched", "키가 비어 있어(NULL) 짝지을 수 없음"));

            var ctes = new List<string>
            {
                side("lt", spec.Left.SqlReference, "l", true),
                side("rt", spec.Right.SqlReference, "r", false),
                grp("lg", "lt"),
                grp("rg", "rt"),
                $"dk AS (SELECT {dkKeys}, coalesce(lg.n, 0) AS ln, coalesce(rg.n, 0) AS rn FROM lg FULL JOIN rg ON {eq("lg", "rg")} WHERE coalesce(lg.n, 0) > 1 OR coalesce(rg.n, 0) > 1)",
                $"l1 AS (SELECT * FROM lt WHERE NOT EXISTS (SELECT 1 FROM dk WHERE {eq("dk", "lt")}))",
                $"r1 AS (SELECT * FROM rt WHERE NOT EXISTS (SELECT 1 FROM dk WHERE {eq("dk", "rt")}))",
                $"m AS (SELECT {string.Join(", ", mItems)} FROM l1 FULL JOIN r1 ON {eq("l1", "r1")})",
                $"c AS (SELECT m.*, CASE WHEN has_l AND NOT has_r THEN '{ChangeRemoved}' WHEN has_r AND NOT has_l THEN '{ChangeAdded}' " +
                $"WHEN {anyChanged} THEN '{ChangeChanged}' ELSE '{ChangeUnchanged}' END AS ct FROM m)",
                $"cd AS (SELECT c.*, CASE WHEN ct = '{ChangeChanged}' THEN {changedNames} WHEN ct IN ('{ChangeAdded}', '{ChangeRemoved}') AND ({anyKeyNull}) THEN {nullKeyText} END AS detail FROM c)",
            };
            return new CompareParts("WITH " + string.Join(",\n", ctes), keys, values, anyNull, anyNull);
        }

        private static string ChangeRank(string col) => $"CASE {col} WHEN '{ChangeAdded}' THEN 1 WHEN '{ChangeRemoved}' THEN 2 WHEN '{ChangeChanged}' THEN 3 WHEN '{ChangeDuplicateKey}' THEN 4 ELSE 5 END";

        /// <summary>
        /// 두 표를 키로 짝지어 비교한다. 키가 한쪽에만 있으면 added(오른쪽만)/removed(왼쪽만), 둘 다 있고 비교 컬럼 중 하나라도 다르면 changed.
        /// 값 비교는 NULL 안전(NULL = NULL은 같음, NULL ≠ 값은 다름)이다. 키가 NULL인 행은 짝지을 수 없어 added/removed로 나온다.
        /// 어느 한쪽에서 키가 중복되면 그 키는 비교하지 않고 duplicate_key 행 하나로 보고한다(행을 조용히 섞지 않음).
        /// </summary>
        public static string CompareSql(CompareSpec spec)
        {
            var reserved = spec.Long
                ? new HashSet<string> { "change_type", "column_name", "old_value", "new_value", "detail" }
                : new HashSet<string> { "change_type", "detail" };
            var p = BuildCompare(spec, reserved);
            int nk = p.Keys.Count;
            string kd(string alias) => string.Join(", ", p.Keys.Select((k, i) => $"{alias}kd{i} AS {Q(k.OutName)}"));
            string dkDisplay = string.Join(", ", p.Keys.Select((k, i) => $"kd{i} AS {Q(k.OutName)}"));
            string dkDetail = SqlNames.Literal(ViewerSupport.LT("left ", "왼쪽 ")) + " || CAST(ln AS VARCHAR) || " + SqlNames.Literal(ViewerSupport.LT(" rows, right ", "행, 오른쪽 ")) +
                              " || CAST(rn AS VARCHAR) || " + SqlNames.Literal(ViewerSupport.LT(" rows — duplicate key, not compared", "행 — 키가 중복되어 비교하지 않음"));
            string filter = spec.IncludeUnchanged ? "" : $" WHERE ct <> '{ChangeUnchanged}'";
            string keyOrder = string.Join(", ", p.Keys.Select(k => Q(k.OutName)));

            string body;
            if (!spec.Long)
            {
                var cs = new List<string> { "ct AS change_type", kd(""), "detail" };
                var ds = new List<string> { $"'{ChangeDuplicateKey}'", dkDisplay, $"({dkDetail})" };
                for (int j = 0; j < p.Values.Count; j++)
                {
                    var v = p.Values[j];
                    cs.Add($"lv{j} AS {Q(v.OutOld)}");
                    cs.Add($"rv{j} AS {Q(v.OutNew)}");
                    ds.Add($"CAST(NULL AS {v.LeftType})");
                    ds.Add($"CAST(NULL AS {v.RightType})");
                }
                body = $"SELECT {string.Join(", ", cs)} FROM cd{filter}\nUNION ALL\nSELECT {string.Join(", ", ds)} FROM dk";
            }
            else
            {
                string keyCols = kd("");
                var parts = new List<string>();
                for (int j = 0; j < p.Values.Count; j++)
                    parts.Add($"SELECT '{ChangeChanged}' AS change_type, {keyCols}, {SqlNames.Literal(p.Values[j].Label)} AS column_name, " +
                              $"CAST(lv{j} AS VARCHAR) AS old_value, CAST(rv{j} AS VARCHAR) AS new_value, CAST(NULL AS VARCHAR) AS detail FROM cd WHERE ct = '{ChangeChanged}' AND d{j}");
                string others = spec.IncludeUnchanged ? $" WHERE ct <> '{ChangeChanged}'" : $" WHERE ct NOT IN ('{ChangeChanged}', '{ChangeUnchanged}')";
                parts.Add($"SELECT ct AS change_type, {keyCols}, CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), detail FROM cd{others}");
                parts.Add($"SELECT '{ChangeDuplicateKey}', {dkDisplay}, CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), CAST(NULL AS VARCHAR), ({dkDetail}) FROM dk");
                body = string.Join("\nUNION ALL\n", parts);
            }
            string order = ChangeRank("change_type") + ", " + keyOrder + (spec.Long ? ", column_name" : "");
            return $"{p.Ctes}\nSELECT * FROM (\n{body}\n) AS u\nORDER BY {order}";
        }

        /// <summary>
        /// 비교 요약 한 행. 컬럼 순서(고정): left_rows, right_rows, added, removed, changed, unchanged, duplicate_keys, duplicate_left_rows,
        /// duplicate_right_rows, null_key_left_rows, null_key_right_rows, 그다음 <see cref="ResolveCompareColumns"/> 순서의 컬럼별 바뀐 셀 수(changed_cells_N).
        /// 중복 키의 행은 added/removed/changed/unchanged에 세지 않는다.
        /// </summary>
        public static string CompareSummarySql(CompareSpec spec)
        {
            var p = BuildCompare(spec, new HashSet<string>());
            var items = new List<string>
            {
                "(SELECT count(*) FROM lt) AS left_rows",
                "(SELECT count(*) FROM rt) AS right_rows",
                $"(SELECT count(*) FROM c WHERE ct = '{ChangeAdded}') AS added",
                $"(SELECT count(*) FROM c WHERE ct = '{ChangeRemoved}') AS removed",
                $"(SELECT count(*) FROM c WHERE ct = '{ChangeChanged}') AS changed",
                $"(SELECT count(*) FROM c WHERE ct = '{ChangeUnchanged}') AS unchanged",
                "(SELECT count(*) FROM dk) AS duplicate_keys",
                "(SELECT coalesce(sum(ln), 0)::BIGINT FROM dk) AS duplicate_left_rows",
                "(SELECT coalesce(sum(rn), 0)::BIGINT FROM dk) AS duplicate_right_rows",
                $"(SELECT count(*) FROM lt WHERE {p.NullKeyLeft}) AS null_key_left_rows",
                $"(SELECT count(*) FROM rt WHERE {p.NullKeyRight}) AS null_key_right_rows",
            };
            for (int j = 0; j < p.Values.Count; j++)
                items.Add($"(SELECT count(*) FROM c WHERE ct = '{ChangeChanged}' AND d{j}) AS changed_cells_{j}");
            return $"{p.Ctes}\nSELECT {string.Join(",\n ", items)}";
        }

        // =====================================================================================
        // 그룹 집계
        // =====================================================================================

        /// <summary>집계 결과 컬럼의 기본 이름(count, sum_매출, count_distinct_도시…).</summary>
        public static string DefaultAggregateAlias(GroupAggregate a) => a.Function switch
        {
            GroupFunction.Count => a.Column is null ? "count" : "count_" + a.Column,
            GroupFunction.Sum => "sum_" + a.Column,
            GroupFunction.Avg => "avg_" + a.Column,
            GroupFunction.Min => "min_" + a.Column,
            GroupFunction.Max => "max_" + a.Column,
            GroupFunction.CountDistinct => "count_distinct_" + a.Column,
            GroupFunction.Median => "median_" + a.Column,
            _ => a.Function.ToString().ToLowerInvariant(),
        };

        public static bool RequiresNumeric(GroupFunction f) => f is GroupFunction.Sum or GroupFunction.Avg or GroupFunction.Median;

        /// <summary>
        /// GROUP BY 문. 그룹 컬럼 → 집계 순서이며 그룹 컬럼으로 정렬한다. NULL인 그룹 값은 하나의 그룹으로 나온다.
        /// 합계·평균·중앙값은 숫자 컬럼만 허용한다(중앙값은 앱의 그룹별 집계와 같은 선형 보간).
        /// </summary>
        public static string GroupSql(GroupSpec spec)
        {
            if (spec.GroupBy.Count == 0 && spec.Aggregates.Count == 0)
                throw new ArgumentException(ViewerSupport.LT("Choose group columns or aggregates.", "그룹 컬럼이나 집계를 고르세요."));
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<string>();
            var groupExprs = new List<string>();
            foreach (var g in spec.GroupBy)
            {
                var c = Find(spec.Table, g);
                if (!taken.Add(c.Name)) throw new ArgumentException(ViewerSupport.LT($"Group column '{c.Name}' is listed twice.", $"그룹 컬럼 '{c.Name}'이 두 번 들어 있습니다."));
                items.Add(Q(c.Name));
                groupExprs.Add(Q(c.Name));
            }
            foreach (var a in spec.Aggregates)
            {
                WorkspaceColumn? col = null;
                if (a.Column is null)
                {
                    if (a.Function != GroupFunction.Count)
                        throw new ArgumentException(ViewerSupport.LT($"{a.Function} needs a column.", $"{a.Function}에는 컬럼이 필요합니다."));
                }
                else
                {
                    col = Find(spec.Table, a.Column);
                    if (RequiresNumeric(a.Function) && !IsNumeric(col.SqlType))
                        throw new ArgumentException(ViewerSupport.LT(
                            $"{a.Function} needs a numeric column, but '{col.Name}' is {col.SqlType}.",
                            $"{a.Function}은(는) 숫자 컬럼에만 쓸 수 있는데 '{col.Name}'은(는) {col.SqlType}입니다."));
                }
                string q = col is null ? "*" : Q(col.Name);
                string expr = a.Function switch
                {
                    GroupFunction.Count => $"count({q})",
                    GroupFunction.Sum => $"sum({q})",
                    GroupFunction.Avg => $"avg({q})",
                    GroupFunction.Min => $"min({q})",
                    GroupFunction.Max => $"max({q})",
                    GroupFunction.CountDistinct => $"count(DISTINCT {q})",
                    _ => $"median({q})",
                };
                string alias = a.Alias is { Length: > 0 } al
                    ? (taken.Add(al) ? al : throw new ArgumentException(ViewerSupport.LT($"Duplicate result column name '{al}'.", $"결과 컬럼 이름 '{al}'이 겹칩니다.")))
                    : Unique(DefaultAggregateAlias(a with { Column = col?.Name }), taken);
                items.Add($"{expr} AS {Q(alias)}");
            }
            string sql = $"SELECT {string.Join(", ", items)}\nFROM {spec.Table.SqlReference}";
            if (groupExprs.Count > 0) sql += $"\nGROUP BY {string.Join(", ", groupExprs)}\nORDER BY {string.Join(", ", groupExprs)}";
            return sql;
        }

        // =====================================================================================
        // 조인 마법사 보조: 키 제안 · 출력 컬럼 선택
        // =====================================================================================

        private static string NormalizeName(string name)
            => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>id·번호·코드처럼 식별자로 보이는 이름인가(키 제안의 우선순위용).</summary>
        public static bool LooksLikeKey(string name)
        {
            string n = NormalizeName(name);
            if (n.Length == 0) return false;
            string[] suffixes = { "id", "key", "no", "num", "code", "번호", "코드", "키", "아이디", "순번", "학번", "사번" };
            return suffixes.Any(s => n == s || n.EndsWith(s, StringComparison.Ordinal));
        }

        /// <summary>
        /// 조인 키 후보. 이름이 같은(대소문자·구두점 무시) 컬럼 쌍 중에서 id·번호·코드처럼 보이는 것이 있으면 그것들(최대 3개),
        /// 없으면 같은 이름 첫 쌍 하나. 같은 이름이 없으면 빈 목록.
        /// </summary>
        public static IReadOnlyList<JoinKey> SuggestKeys(IWorkspaceRelation left, IWorkspaceRelation right)
        {
            var pairs = new List<JoinKey>();
            var usedRight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pass in new[] { 0, 1 })
                foreach (var lc in left.Columns)
                {
                    if (pairs.Any(p => string.Equals(p.LeftColumn, lc.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    var rc = pass == 0
                        ? right.Columns.FirstOrDefault(c => string.Equals(c.Name, lc.Name, StringComparison.OrdinalIgnoreCase) && !usedRight.Contains(c.Name))
                        : right.Columns.FirstOrDefault(c => NormalizeName(c.Name) == NormalizeName(lc.Name) && NormalizeName(c.Name).Length > 0 && !usedRight.Contains(c.Name));
                    if (rc is null) continue;
                    usedRight.Add(rc.Name);
                    pairs.Add(new JoinKey(lc.Name, rc.Name));
                }
            var keyLike = pairs.Where(p => LooksLikeKey(p.LeftColumn) || LooksLikeKey(p.RightColumn)).Take(3).ToList();
            if (keyLike.Count > 0) return keyLike;
            return pairs.Take(1).ToList();
        }

        /// <summary>조인 결과 컬럼 하나: 어느 쪽의 어느 컬럼을 어떤 이름으로 내보낼지.</summary>
        public sealed record JoinOutputColumn(bool FromLeft, string Column, string OutputName);

        /// <summary>
        /// 조인 결과의 기본 컬럼 목록(왼쪽 전부 → 오른쪽 전부). 접두어는 겹치는 이름에만(<paramref name="prefixOnlyClashes"/>) 또는 전부에 붙이고,
        /// 그래도 겹치면 오른쪽은 "이름_right", 왼쪽은 "이름_2"처럼 번호를 붙여 이름을 유일하게 만든다.
        /// </summary>
        public static IReadOnlyList<JoinOutputColumn> DefaultJoinColumns(IWorkspaceRelation left, IWorkspaceRelation right,
            string leftPrefix = "", string rightPrefix = "", bool prefixOnlyClashes = true)
        {
            var rightNames = new HashSet<string>(right.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var leftNames = new HashSet<string>(left.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<JoinOutputColumn>();
            foreach (var c in left.Columns)
            {
                bool prefix = leftPrefix.Length > 0 && (!prefixOnlyClashes || rightNames.Contains(c.Name));
                list.Add(new JoinOutputColumn(true, c.Name, Unique(prefix ? leftPrefix + c.Name : c.Name, used)));
            }
            foreach (var c in right.Columns)
            {
                bool prefix = rightPrefix.Length > 0 && (!prefixOnlyClashes || leftNames.Contains(c.Name));
                string name = prefix ? rightPrefix + c.Name : c.Name;
                string alias = name;
                for (int n = 1; !used.Add(alias); n++) alias = n == 1 ? name + "_right" : name + "_right" + n;
                list.Add(new JoinOutputColumn(false, c.Name, alias));
            }
            return list;
        }

        /// <summary>선택한 컬럼만 내보내는 조인 문. ON 절은 <see cref="JoinSql.Build"/>·진단과 같은 키 식을 쓴다.</summary>
        public static string JoinSelectSql(JoinSpec spec, IReadOnlyList<JoinOutputColumn> columns)
        {
            var plan = JoinSql.PlanKeys(spec);
            if (columns.Count == 0) throw new ArgumentException(ViewerSupport.LT("Select at least one column.", "내보낼 컬럼을 하나 이상 고르세요."));
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<string>();
            foreach (var c in columns)
            {
                if (string.IsNullOrWhiteSpace(c.OutputName))
                    throw new ArgumentException(ViewerSupport.LT($"Column '{c.Column}' needs a result name.", $"'{c.Column}' 컬럼의 결과 이름을 입력하세요."));
                if (!names.Add(c.OutputName))
                    throw new ArgumentException(ViewerSupport.LT($"Duplicate result column name '{c.OutputName}'.", $"결과 컬럼 이름 '{c.OutputName}'이 겹칩니다."));
                var src = Find(c.FromLeft ? spec.Left : spec.Right, c.Column);
                items.Add($"{(c.FromLeft ? "l" : "r")}.{Q(src.Name)} AS {Q(c.OutputName)}");
            }
            string kind = spec.Kind switch { JoinKind.Left => "LEFT", JoinKind.Right => "RIGHT", JoinKind.Full => "FULL", _ => "INNER" };
            string on = string.Join(" AND ", plan.Select(p => $"{p.LeftExpr} = {p.RightExpr}"));
            return $"SELECT {string.Join(", ", items)}\nFROM {spec.Left.SqlReference} AS l\n{kind} JOIN {spec.Right.SqlReference} AS r ON {on}";
        }
    }
}
