using System.Diagnostics;
using System.Text.Json;
using DuckDB.NET.Data;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Workspace
{
    public sealed partial class DataWorkspace
    {
        // ---- 연결·취소·진행률 ------------------------------------------------------------------------

        private DuckDBConnection OpenQueryConnection()
        {
            DuckDBConnection c;
            lock (_gate)
            {
                ThrowIfDisposed();
                c = _root.Duplicate();
                c.Open();
            }
            // 진행률 폴링(duckdb_query_progress)에는 연결별 진행 막대 설정이 필요하다.
            TryExec(c, "SET enable_progress_bar = true");
            TryExec(c, "SET enable_progress_bar_print = false"); // 콘솔에 진행 막대를 그리지 않는다(폴링으로만 읽는다)
            TryExec(c, "SET progress_bar_time = 0");
            return c;
        }

        /// <summary>취소 토큰이 울리면 질의가 끝날 때까지 주기적으로 <c>duckdb_interrupt</c>를 보낸다(질의 시작 직전에 보낸 신호가 유실되는 경주를 막는다).</summary>
        private sealed class InterruptGuard : IDisposable
        {
            private readonly DuckDBConnection _con;
            private readonly CancellationTokenRegistration _registration;
            private readonly object _lock = new();
            private Thread? _thread;
            private volatile bool _done;

            public InterruptGuard(DuckDBConnection con, CancellationToken ct)
            {
                _con = con;
                _registration = ct.Register(Start);
            }

            private void Start()
            {
                lock (_lock)
                {
                    if (_done || _thread is not null) return;
                    _thread = new Thread(() =>
                    {
                        while (!_done)
                        {
                            try { _con.NativeConnection.Interrupt(); } catch (Exception) { return; }
                            Thread.Sleep(15);
                        }
                    }) { IsBackground = true, Name = "duck-interrupt" };
                    _thread.Start();
                }
            }

            public void Dispose()
            {
                Thread? t;
                lock (_lock) { _done = true; t = _thread; }
                _registration.Dispose();
                t?.Join();
            }
        }

        private sealed class ProgressPoller : IDisposable
        {
            private readonly Thread _thread;
            private volatile bool _done;

            public ProgressPoller(DuckDBConnection con, IProgress<long> progress)
            {
                _thread = new Thread(() =>
                {
                    long last = -1;
                    while (!_done)
                    {
                        try
                        {
                            long rows = (long)con.GetQueryProgress().RowsProcessed;
                            if (rows > last) { last = rows; progress.Report(rows); }
                        }
                        catch (Exception) { return; }
                        Thread.Sleep(120);
                    }
                }) { IsBackground = true, Name = "duck-progress" };
                _thread.Start();
            }

            public void Dispose() { _done = true; _thread.Join(); }
        }

        /// <summary>호출마다 별도 연결에서 실행하고, 취소되면 <see cref="OperationCanceledException"/>, DuckDB 오류는 <see cref="WorkspaceQueryException"/>으로 바꾼다.</summary>
        private Task<T> RunAsync<T>(Func<DuckDBConnection, T> work, CancellationToken ct)
            => Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var con = OpenQueryConnection();
                using var guard = new InterruptGuard(con, ct);
                try { return work(con); }
                catch (Exception ex) when (ct.IsCancellationRequested && (ex is DuckDBException || ex is WorkspaceQueryException))
                {
                    throw new OperationCanceledException(ct);
                }
                catch (DuckDBException ex) { throw WorkspaceQueryException.From(ex); }
            }, ct);

        private static T Db<T>(string executedSql, int prefixLength, Func<T> f)
        {
            try { return f(); }
            catch (DuckDBException ex) { throw WorkspaceQueryException.From(ex, executedSql, prefixLength); }
        }

        private static long ScalarLong(DuckDBConnection con, string sql, int prefixLength = 0)
            => Db(sql, prefixLength, () =>
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                object? v = cmd.ExecuteScalar();
                return v is null || v is DBNull ? 0L : Convert.ToInt64(v);
            });

        // ---- SQL 분석 --------------------------------------------------------------------------------

        /// <summary>
        /// SQL이 한 문장 SELECT인지 확인하고(문법 오류는 줄·열과 함께) FROM에서 참조한 표를 모은다. 실행하지 않으며, DuckDB 파서만 쓴다.
        /// 작업 공간에서 실행할 수 있는 것은 SELECT 한 문장뿐이다(원본 파일을 바꾸는 문장을 막는다).
        /// </summary>
        public SqlAnalysis Analyze(string sql)
        {
            using var con = OpenQueryConnection();
            return AnalyzeCore(con, sql);
        }

        private static SqlAnalysis AnalyzeCore(DuckDBConnection con, string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return new SqlAnalysis(false, ViewerSupport.LT("Enter a SQL query.", "SQL을 입력하세요."), null, null, Array.Empty<SqlTableRef>());

            string json = Db("json_serialize_sql", 0, () =>
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT json_serialize_sql(" + SqlNames.Literal(sql) + ")";
                return (string)cmd.ExecuteScalar()!;
            });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.GetBoolean())
            {
                string message = root.TryGetProperty("error_message", out var em) ? em.GetString() ?? "" : "";
                bool notSelect = message.Contains("Only SELECT", StringComparison.OrdinalIgnoreCase);
                if (notSelect)
                    return new SqlAnalysis(false,
                        ViewerSupport.LT("Only a single SELECT query can be run here (no CREATE/INSERT/COPY/DROP…).",
                                         "여기서는 SELECT 질의 한 문장만 실행할 수 있습니다(CREATE·INSERT·COPY·DROP 등은 불가)."),
                        null, null, Array.Empty<SqlTableRef>());
                int? line = null, col = null;
                if (root.TryGetProperty("position", out var pos) && long.TryParse(pos.ToString(), out long offset))
                {
                    var lc = WorkspaceQueryException.LineColumnFromOffset(sql, offset);
                    line = lc.Line; col = lc.Column;
                }
                return new SqlAnalysis(false, message, line, col, Array.Empty<SqlTableRef>());
            }

            var statements = root.GetProperty("statements");
            if (statements.GetArrayLength() != 1)
                return new SqlAnalysis(false,
                    ViewerSupport.LT($"Run one statement at a time (found {statements.GetArrayLength()}).",
                                     $"한 번에 한 문장만 실행할 수 있습니다(지금 {statements.GetArrayLength()}문장)."),
                    null, null, Array.Empty<SqlTableRef>());

            var tables = new List<SqlTableRef>();
            var seen = new HashSet<(string, string)>();
            CollectTables(statements[0], tables, seen);
            return new SqlAnalysis(true, null, null, null, tables);
        }

        private static void CollectTables(JsonElement e, List<SqlTableRef> into, HashSet<(string, string)> seen)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "BASE_TABLE" &&
                        e.TryGetProperty("table_name", out var tn) && tn.ValueKind == JsonValueKind.String)
                    {
                        string schema = e.TryGetProperty("schema_name", out var sn) && sn.ValueKind == JsonValueKind.String ? sn.GetString() ?? "" : "";
                        string name = tn.GetString() ?? "";
                        if (seen.Add((schema.ToLowerInvariant(), name.ToLowerInvariant()))) into.Add(new SqlTableRef(schema, name));
                    }
                    foreach (var p in e.EnumerateObject()) CollectTables(p.Value, into, seen);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) CollectTables(item, into, seen);
                    break;
            }
        }

        private static string RequireSelect(DuckDBConnection con, string sql)
        {
            var a = AnalyzeCore(con, sql);
            if (!a.IsValid)
                throw new WorkspaceQueryException(a.Error! + WorkspaceQueryException.Where(a.Line, a.Column),
                    a.Line is not null ? QueryErrorKind.Syntax : QueryErrorKind.NotSelect, a.Line, a.Column);
            return SqlText.StripTerminator(sql);
        }

        private static IReadOnlyList<QueryColumn> DescribeBody(DuckDBConnection con, string body)
        {
            const string prefix = "DESCRIBE SELECT * FROM (";
            string sql = SqlText.Wrap(prefix, body, ") AS __q");
            return Db(sql, prefix.Length, () =>
            {
                var cols = new List<QueryColumn>();
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string type = r.GetString(1);
                    cols.Add(new QueryColumn(r.GetString(0), type, TypedColumnSql.FromSqlType(type)));
                }
                return cols;
            });
        }

        // ---- 미리보기·CSV로 실행 -------------------------------------------------------------------------

        /// <summary>
        /// SQL 결과의 앞 <paramref name="maxRows"/>행을 문자열로 돌려준다(<c>LIMIT</c>으로 감싸 실행하므로 큰 결과도 빠르다). 더 있으면 Truncated.
        /// 값은 DuckDB의 VARCHAR 표현(CSV로 쓸 때와 같은 글자), NULL은 null. 문법·이름 오류는 <see cref="WorkspaceQueryException"/>(줄·열 포함).
        /// </summary>
        public Task<QueryPreview> PreviewAsync(string sql, int maxRows, CancellationToken ct = default)
        {
            if (maxRows < 1) throw new ArgumentOutOfRangeException(nameof(maxRows));
            return RunAsync(con =>
            {
                var sw = Stopwatch.StartNew();
                string body = RequireSelect(con, sql);
                var columns = DescribeBody(con, body);
                const string prefix = "SELECT COLUMNS(*)::VARCHAR FROM (";
                string wrapped = SqlText.Wrap(prefix, body, $") AS __q LIMIT {maxRows + 1L}");
                var rows = Db(wrapped, prefix.Length, () =>
                {
                    var list = new List<string?[]>();
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = wrapped;
                    using var r = cmd.ExecuteReader();
                    int n = r.FieldCount;
                    while (r.Read())
                    {
                        var row = new string?[n];
                        for (int i = 0; i < n; i++) row[i] = r.IsDBNull(i) ? null : r.GetString(i);
                        list.Add(row);
                    }
                    return list;
                });
                bool truncated = rows.Count > maxRows;
                if (truncated) rows.RemoveRange(maxRows, rows.Count - maxRows);
                return new QueryPreview(columns, rows, truncated, sw.Elapsed);
            }, ct);
        }

        /// <summary>
        /// SQL 결과를 UTF-8(BOM 없음)·헤더 있는 CSV로 쓴다(<c>COPY … TO</c>). 임시 파일에 쓴 뒤 옮기므로 실패·취소 시 부분 파일이 남지 않는다.
        /// <paramref name="rowsProgress"/>에는 지금까지 처리한 행 수(입력 기준 근사치)를 보고한다. 등록된 원본 파일 위에는 쓰지 않는다.
        /// </summary>
        public Task<QueryResultInfo> RunToCsvAsync(string sql, string outputPath, IProgress<long>? rowsProgress = null, CancellationToken ct = default)
        {
            string full = Path.GetFullPath(outputPath);
            EnsureNotSourceFile(full);
            return RunAsync(con => CopyToCsv(con, RequireSelect(con, sql), full, rowsProgress, ct), ct);
        }

        private void EnsureNotSourceFile(string fullPath)
        {
            lock (_gate)
            {
                foreach (var s in _sources)
                {
                    if (SamePath(s.Path, fullPath) || s.Tables.Any(t => SamePath(t.FilePath, fullPath) || SamePath(t.ReadPath, fullPath)))
                        throw new InvalidOperationException(ViewerSupport.LT(
                            "The original file is never overwritten. Choose a different file name.", "원본 파일은 덮어쓰지 않습니다. 다른 파일 이름을 고르세요."));
                }
            }
        }

        private static bool SamePath(string a, string b)
            => a.Length > 0 && string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase);

        private static QueryResultInfo CopyToCsv(DuckDBConnection con, string body, string fullOutput, IProgress<long>? progress, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var columns = DescribeBody(con, body);
            string dir = Path.GetDirectoryName(fullOutput) ?? ".";
            Directory.CreateDirectory(dir);
            string tmp = fullOutput + ".tmp-" + Guid.NewGuid().ToString("N");
            const string prefix = "COPY (";
            string sql = SqlText.Wrap(prefix, body, $") TO {SqlNames.Literal(tmp)} (FORMAT CSV, HEADER true, DELIMITER ',')");
            try
            {
                long count;
                using (progress is null ? null : new ProgressPoller(con, progress))
                    count = Db(sql, prefix.Length, () =>
                    {
                        using var cmd = con.CreateCommand();
                        cmd.CommandText = sql;
                        return (long)cmd.ExecuteNonQuery(); // COPY는 결과 집합 없이 쓴 행 수를 돌려준다.
                    });
                ct.ThrowIfCancellationRequested();
                File.Move(tmp, fullOutput, overwrite: true);
                progress?.Report(count);
                return new QueryResultInfo(count, columns, sw.Elapsed, fullOutput);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
                throw;
            }
        }

        // ---- 행 수·형 변환 실패 -------------------------------------------------------------------------

        /// <summary>표·뷰의 행 수(전체 읽기).</summary>
        public Task<long> CountRowsAsync(IWorkspaceRelation relation, CancellationToken ct = default)
            => RunAsync(con => ScalarLong(con, "SELECT count(*) FROM " + relation.SqlReference), ct);

        /// <summary>
        /// 형 변환 컬럼마다 비어 있지 않은 값 수와 변환 실패(NULL이 된) 수·예시를 센다. 파일 전체를 한 번 읽는다.
        /// 값이 비었거나 na·n/a·null·nil·missing이면 실패로 세지 않는다.
        /// </summary>
        public Task<IReadOnlyList<ColumnCastReport>> CheckTypedColumnsAsync(WorkspaceTable table, CancellationToken ct = default)
            => RunAsync<IReadOnlyList<ColumnCastReport>>(con =>
            {
                var converted = table.Columns.Where(c => c.IsConverted).ToList();
                if (converted.Count == 0) return Array.Empty<ColumnCastReport>();
                string raw = table.RawSqlReference;
                var parts = new List<string>();
                foreach (var c in converted)
                {
                    string q = SqlNames.Quote(c.Name);
                    parts.Add($"count(*) FILTER (WHERE NOT {TypedColumnSql.IsNullToken(q)})");
                    parts.Add($"count(*) FILTER (WHERE NOT {TypedColumnSql.IsNullToken(q)} AND {TypedColumnSql.Expression(c.Type, q)} IS NULL)");
                }
                string sql = $"SELECT {string.Join(", ", parts)} FROM {raw}";
                var counts = new long[parts.Count];
                Db(sql, 0, () =>
                {
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = sql;
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                        for (int i = 0; i < counts.Length; i++) counts[i] = r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
                    return 0;
                });

                var reports = new List<ColumnCastReport>();
                for (int i = 0; i < converted.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var c = converted[i];
                    var examples = new List<string>();
                    if (counts[2 * i + 1] > 0)
                    {
                        string q = SqlNames.Quote(c.Name);
                        string exSql = $"SELECT DISTINCT {q} FROM {raw} WHERE NOT {TypedColumnSql.IsNullToken(q)} AND {TypedColumnSql.Expression(c.Type, q)} IS NULL LIMIT 5";
                        Db(exSql, 0, () =>
                        {
                            using var cmd = con.CreateCommand();
                            cmd.CommandText = exSql;
                            using var r = cmd.ExecuteReader();
                            while (r.Read()) examples.Add(r.GetString(0));
                            return 0;
                        });
                    }
                    reports.Add(new ColumnCastReport(table.DisplayName, c.Name, c.Type, counts[2 * i], counts[2 * i + 1], examples));
                }
                return reports;
            }, ct);

        // ---- 조인 진단 -----------------------------------------------------------------------------------

        /// <summary>
        /// 조인하기 전에 키를 진단한다: 양쪽 행 수, 일치하는 키·한쪽에만 있는 키, 키가 겹치는(중복) 수, NULL 키 행, 예상 결과 행 수·행 증가 배율.
        /// 키 식은 <see cref="JoinSql.Build"/>가 만드는 실제 조인과 같다.
        /// </summary>
        public Task<JoinDiagnostics> CheckJoinAsync(JoinSpec spec, CancellationToken ct = default)
        {
            var plan = JoinSql.PlanKeys(spec);
            return RunAsync(con =>
            {
                string lk = string.Join(", ", plan.Select((p, i) => $"{p.LeftExpr} AS k{i}"));
                string rk = string.Join(", ", plan.Select((p, i) => $"{p.RightExpr} AS k{i}"));
                string nonNullL = string.Join(" AND ", plan.Select((_, i) => $"k{i} IS NOT NULL"));
                string anyNullL = string.Join(" OR ", plan.Select((_, i) => $"k{i} IS NULL"));
                string keys = string.Join(", ", plan.Select((_, i) => $"k{i}"));
                string eq(string a, string b) => string.Join(" AND ", plan.Select((_, i) => $"{a}.k{i} = {b}.k{i}"));

                string sql = $@"WITH lt AS (SELECT {lk} FROM {spec.Left.SqlReference} AS l),
rt AS (SELECT {rk} FROM {spec.Right.SqlReference} AS r),
lk AS (SELECT {keys}, count(*) AS c FROM lt WHERE {nonNullL} GROUP BY {keys}),
rk AS (SELECT {keys}, count(*) AS c FROM rt WHERE {nonNullL} GROUP BY {keys}),
j AS (SELECT lk.c AS lc, rk.c AS rc FROM lk JOIN rk ON {eq("lk", "rk")}),
lo AS (SELECT lk.c FROM lk WHERE NOT EXISTS (SELECT 1 FROM rk WHERE {eq("lk", "rk")})),
ro AS (SELECT rk.c FROM rk WHERE NOT EXISTS (SELECT 1 FROM lk WHERE {eq("lk", "rk")}))
SELECT
 (SELECT count(*) FROM lt), (SELECT count(*) FROM rt),
 (SELECT count(*) FROM lt WHERE {anyNullL}), (SELECT count(*) FROM rt WHERE {anyNullL}),
 (SELECT count(*) FROM lk), (SELECT count(*) FROM rk),
 (SELECT count(*) FROM j), (SELECT count(*) FROM lo), (SELECT count(*) FROM ro),
 (SELECT count(*) FROM lk WHERE c > 1), (SELECT count(*) FROM rk WHERE c > 1),
 (SELECT coalesce(max(c), 0) FROM lk), (SELECT coalesce(max(c), 0) FROM rk),
 (SELECT coalesce(TRY_CAST(coalesce(sum(lc * rc), 0) AS BIGINT), 9223372036854775807) FROM j),
 (SELECT coalesce(sum(c), 0)::BIGINT FROM lo), (SELECT coalesce(sum(c), 0)::BIGINT FROM ro)";

                var v = new long[16];
                Db(sql, 0, () =>
                {
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = sql;
                    using var r = cmd.ExecuteReader();
                    if (!r.Read()) throw new InvalidOperationException("No diagnostics row.");
                    for (int i = 0; i < v.Length; i++) v[i] = r.IsDBNull(i) ? 0 : Convert.ToInt64(r.GetValue(i));
                    return 0;
                });
                long leftRows = v[0], rightRows = v[1], leftNull = v[2], rightNull = v[3];
                long inner = v[13], leftOnlyRows = v[14], rightOnlyRows = v[15];
                long expected = spec.Kind switch
                {
                    JoinKind.Inner => inner,
                    JoinKind.Left => inner + leftOnlyRows + leftNull,
                    JoinKind.Right => inner + rightOnlyRows + rightNull,
                    _ => inner + leftOnlyRows + leftNull + rightOnlyRows + rightNull,
                };
                bool leftMany = v[9] > 0, rightMany = v[10] > 0;
                var cardinality = (leftMany, rightMany) switch
                {
                    (false, false) => JoinCardinality.OneToOne,
                    (false, true) => JoinCardinality.OneToMany,
                    (true, false) => JoinCardinality.ManyToOne,
                    _ => JoinCardinality.ManyToMany,
                };
                return new JoinDiagnostics(leftRows, rightRows, leftNull, rightNull, v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12],
                    inner, expected, spec.Kind, cardinality, plan.Where(p => p.Warning is not null).Select(p => p.Warning!).ToArray());
            }, ct);
        }
    }

    /// <summary>조인 SQL 생성. 키 식을 진단(<see cref="DataWorkspace.CheckJoinAsync"/>)과 공유한다.</summary>
    public static class JoinSql
    {
        internal sealed record KeyPlan(string LeftExpr, string RightExpr, string? Warning);

        private static bool IsNumeric(WorkspaceColumn c) => TypedColumnSql.FromSqlType(c.SqlType) is ColumnValueType.Integer or ColumnValueType.Float;
        private static bool IsTemporal(WorkspaceColumn c) => TypedColumnSql.FromSqlType(c.SqlType) is ColumnValueType.Date or ColumnValueType.DateTime;

        internal static List<KeyPlan> PlanKeys(JoinSpec spec)
        {
            if (spec.Keys.Count == 0) throw new ArgumentException("At least one join key is required.", nameof(spec));
            var plan = new List<KeyPlan>();
            foreach (var k in spec.Keys)
            {
                var lc = Find(spec.Left, k.LeftColumn);
                var rc = Find(spec.Right, k.RightColumn);
                string l = "l." + SqlNames.Quote(lc.Name), r = "r." + SqlNames.Quote(rc.Name);
                bool compatible = string.Equals(lc.SqlType, rc.SqlType, StringComparison.OrdinalIgnoreCase) ||
                                  (IsNumeric(lc) && IsNumeric(rc)) || (IsTemporal(lc) && IsTemporal(rc));
                if (compatible) plan.Add(new KeyPlan(l, r, null));
                else plan.Add(new KeyPlan($"CAST({l} AS VARCHAR)", $"CAST({r} AS VARCHAR)",
                    ViewerSupport.LT(
                        $"{spec.Left.DisplayName}.{lc.Name} ({lc.SqlType}) and {spec.Right.DisplayName}.{rc.Name} ({rc.SqlType}) have different types, so they are compared as text (for example 001 and 1 do not match).",
                        $"{spec.Left.DisplayName}.{lc.Name}({lc.SqlType})과 {spec.Right.DisplayName}.{rc.Name}({rc.SqlType})은 타입이 달라 글자로 비교합니다(예: 001과 1은 다른 값).")));
            }
            return plan;
        }

        private static WorkspaceColumn Find(IWorkspaceRelation rel, string name)
        {
            foreach (var c in rel.Columns)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            throw new ArgumentException(ViewerSupport.LT(
                $"'{rel.DisplayName}' has no column '{name}'.", $"'{rel.DisplayName}'에 '{name}' 컬럼이 없습니다."));
        }

        /// <summary>
        /// 조인 SELECT 문: 왼쪽 컬럼 전부 + 오른쪽 컬럼 전부(이름이 겹치면 "이름_right"). 키가 NULL인 행은 어느 쪽과도 일치하지 않는다.
        /// </summary>
        public static string Build(JoinSpec spec)
        {
            var plan = PlanKeys(spec);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<string>();
            foreach (var c in spec.Left.Columns)
            {
                used.Add(c.Name);
                items.Add($"l.{SqlNames.Quote(c.Name)} AS {SqlNames.Quote(c.Name)}");
            }
            foreach (var c in spec.Right.Columns)
            {
                string alias = c.Name;
                for (int n = 1; !used.Add(alias); n++) alias = n == 1 ? c.Name + "_right" : c.Name + "_right" + n;
                items.Add($"r.{SqlNames.Quote(c.Name)} AS {SqlNames.Quote(alias)}");
            }
            string kind = spec.Kind switch { JoinKind.Left => "LEFT", JoinKind.Right => "RIGHT", JoinKind.Full => "FULL", _ => "INNER" };
            string on = string.Join(" AND ", plan.Select(p => $"{p.LeftExpr} = {p.RightExpr}"));
            return $"SELECT {string.Join(", ", items)}\nFROM {spec.Left.SqlReference} AS l\n{kind} JOIN {spec.Right.SqlReference} AS r ON {on}";
        }
    }
}
