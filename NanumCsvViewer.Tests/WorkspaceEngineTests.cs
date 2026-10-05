using System.Diagnostics;
using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    public class WorkspaceEngineTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncv_ws_" + Guid.NewGuid().ToString("N"));
        private readonly DataWorkspace _ws;
        private static readonly UTF8Encoding Utf8 = new(false);

        static WorkspaceEngineTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        public WorkspaceEngineTests()
        {
            Directory.CreateDirectory(_dir);
            _ws = new DataWorkspace(new DataWorkspaceOptions { TempRoot = Path.Combine(_dir, "duck"), MemoryLimitBytes = 1L << 30, Threads = 2 });
        }

        public void Dispose()
        {
            _ws.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string Write(string name, string content, Encoding? enc = null)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllText(p, content, enc ?? Utf8);
            return p;
        }

        private static string[][] Cells(QueryPreview p) => p.Rows.Select(r => r.Select(c => c ?? "<null>").ToArray()).ToArray();

        private Task<QueryPreview> Q(string sql, int max = 100) => _ws.PreviewAsync(sql, max);

        private async Task<string> Scalar(string sql) => Cells(await Q(sql))[0][0];

        // ---- 엔진·이름 ------------------------------------------------------------------------------

        [Fact]
        public void Engine_is_available_in_this_process()
        {
            Assert.True(DataWorkspace.IsEngineAvailable(out string? reason), reason);
            Assert.Null(reason);
        }

        [Fact]
        public void Names_follow_the_documented_rule_and_stay_unique()
        {
            string a = Write("설문 결과 (최종).csv", "x\n1\n");
            Directory.CreateDirectory(Path.Combine(_dir, "sub"));
            string b = Write(Path.Combine("sub", "설문 결과 (최종).csv"), "x\n2\n");
            string c = Write("2024 매출.csv", "x\n3\n");
            string d = Write("!!!.csv", "x\n4\n");
            Assert.Equal("설문_결과_최종", _ws.AddCsv(a).Name);
            Assert.Equal("설문_결과_최종_2", _ws.AddCsv(b).Name);
            Assert.Equal("_2024_매출", _ws.AddCsv(c).Name);
            Assert.Equal("data", _ws.AddCsv(d).Name);
            Assert.False(SqlNames.NeedsQuoting("설문_결과_최종"));
            Assert.True(SqlNames.NeedsQuoting("select"));
            Assert.True(SqlNames.NeedsQuoting("a b"));
            Assert.Equal("\"a\"\"b\"", SqlNames.Quote("a\"b"));
        }

        [Fact]
        public void Raw_companion_name_is_reserved_so_names_never_collide()
        {
            string p1 = Write("t.csv", "x\n1\n");
            string p2 = Write("t__raw.csv", "x\n2\n");
            var s1 = _ws.AddCsv(p1);
            var s2 = _ws.AddCsv(p2);
            Assert.Equal("t", s1.Name);
            Assert.Equal("t__raw_2", s2.Name);
        }

        // ---- CSV 등록: UTF-8 · CP949 · 한글 컬럼 ---------------------------------------------------------

        [Fact]
        public async Task Utf8_csv_with_bom_and_korean_columns_is_queryable()
        {
            string p = Write("명단.csv", "이름,나이\n홍길동,30\n김철수,25\n", new UTF8Encoding(true));
            var src = _ws.AddCsv(p);
            var t = src.Tables[0];
            Assert.Equal("명단", t.DisplayName);
            Assert.False(t.IsConvertedCopy);
            Assert.Equal(new[] { "이름", "나이" }, t.Columns.Select(c => c.Name));
            Assert.Equal(ColumnValueType.Integer, t.Columns[1].Type);
            var r = await Q("SELECT 이름, 나이 * 2 AS 두배 FROM 명단 ORDER BY 나이 DESC");
            Assert.Equal(new[] { "이름", "두배" }, r.Columns.Select(c => c.Name));
            Assert.Equal(new[] { new[] { "홍길동", "60" }, new[] { "김철수", "50" } }, Cells(r));
            Assert.Equal(2, await _ws.CountRowsAsync(t));
        }

        [Fact]
        public async Task Cp949_csv_gets_a_reusable_utf8_copy()
        {
            string p = Write("한글.csv", "이름,도시\n홍길동,서울\n김철수,부산\n", Encoding.GetEncoding(949));
            var src = _ws.AddCsv(p);
            var t = src.Tables[0];
            Assert.True(t.IsConvertedCopy);
            Assert.NotEqual(p, t.ReadPath);
            Assert.StartsWith("CP949", t.Options.EncodingName);
            Assert.Equal("서울", await Scalar("SELECT 도시 FROM 한글 WHERE 이름 = '홍길동'"));
            // 원본은 그대로(CP949 바이트).
            Assert.Contains((byte)0xC8, File.ReadAllBytes(p));
            // 같은 파일·시각이면 사본을 재사용, 수정하면 새로 만든다.
            var again = _ws.AddCsv(p).Tables[0];
            Assert.Equal(t.ReadPath, again.ReadPath);
            File.WriteAllText(p, "이름,도시\n홍길동,제주\n", Encoding.GetEncoding(949));
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(5));
            var fresh = _ws.AddCsv(p).Tables[0];
            Assert.NotEqual(t.ReadPath, fresh.ReadPath);
        }

        [Fact]
        public async Task Paths_and_columns_with_quotes_and_spaces_are_escaped()
        {
            string p = Write("it's a test file.csv", "a b,say \"hi\"\n1,2\n");
            var src = _ws.AddCsv(p);
            Assert.Equal("it_s_a_test_file", src.Name);
            Assert.Equal("1", await Scalar("SELECT \"a b\" FROM it_s_a_test_file"));
            Assert.Equal("2", await Scalar("SELECT \"say \"\"hi\"\"\" FROM it_s_a_test_file"));
            string output = Write("o'ut.csv", "");
            File.Delete(output);
            var info = await _ws.RunToCsvAsync("SELECT * FROM it_s_a_test_file", output);
            Assert.Equal(1, info.RowCount);
            Assert.True(File.Exists(output));
        }

        [Fact]
        public async Task Explicit_encoding_option_converts_even_without_detection()
        {
            string p = Write("e.csv", "이름\n가나다\n", Encoding.GetEncoding(949));
            var t = _ws.AddCsv(p, new CsvSourceOptions { EncodingName = EncodingDetector.Cp949 }).Tables[0];
            Assert.True(t.IsConvertedCopy);
            Assert.Equal("가나다", await Scalar("SELECT 이름 FROM e"));
        }

        [Fact]
        public async Task Semicolon_delimiter_and_headerless_files()
        {
            string p = Write("sep.csv", "a;b\n1;2\n3;4\n");
            var t = _ws.AddCsv(p, new CsvSourceOptions { Delimiter = ';' }).Tables[0];
            Assert.Equal(new[] { "a", "b" }, t.Columns.Select(c => c.Name));
            Assert.Equal("4", await Scalar("SELECT max(b) FROM sep"));

            string h = Write("nohead.csv", "1,x\n2,y\n");
            var t2 = _ws.AddCsv(h, new CsvSourceOptions { HasHeader = false }).Tables[0];
            Assert.Equal(new[] { "column0", "column1" }, t2.Columns.Select(c => c.Name));
            Assert.Equal("2", await Scalar("SELECT count(*) FROM nohead"));
        }

        [Fact]
        public async Task Ragged_rows_are_padded_and_quoted_newlines_survive()
        {
            string p = Write("rag.csv", "a,b,c\n1,2,3\n4,5\n\"x\ny\",6,7\n");
            var t = _ws.AddCsv(p).Tables[0];
            var r = await Q("SELECT a, b, c FROM rag");
            Assert.Equal(3, r.Rows.Count);
            Assert.Equal("<null>", Cells(r)[1][2]);
            Assert.Contains("x\ny", Cells(r).Select(x => x[0]));
            // 예약어와 같은 이름의 파일은 따옴표 없이 쓸 수 있게 '_'가 붙는다.
            Assert.Equal("order_", _ws.AddCsv(Write("order.csv", "x\n1\n")).Name);
        }

        [Fact]
        public void Missing_or_empty_file_is_reported_honestly()
        {
            Assert.Throws<FileNotFoundException>(() => _ws.AddCsv(Path.Combine(_dir, "none.csv")));
            string empty = Write("empty.csv", "");
            Assert.ThrowsAny<Exception>(() => _ws.AddCsv(empty));
            Assert.Empty(_ws.Sources);
        }

        // ---- 타입 열 · TRY_CAST ----------------------------------------------------------------------

        private const string TypedCsv =
            "이름,나이,가입일,금액,id,활성,비율,비고\n" +
            "홍길동,30,2024-01-05,\"1,234\",001,yes,12%,a\n" +
            "김철수,x,2024/1/6,$5,002,no,5%,b\n" +
            "이영희,,2024.01.07,300,003,y,N/A,c\n" +
            "박민수,41,not a date,12abc,004,maybe,7%,d\n";

        private WorkspaceTable AddTyped()
        {
            string p = Write("가입자.csv", TypedCsv);
            return _ws.AddCsv(p, new CsvSourceOptions
            {
                ColumnTypes = new[]
                {
                    ColumnValueType.String, ColumnValueType.Integer, ColumnValueType.Date, ColumnValueType.Currency,
                    ColumnValueType.Identifier, ColumnValueType.Boolean, ColumnValueType.Percent, ColumnValueType.String
                }
            }).Tables[0];
        }

        [Fact]
        public async Task Typed_columns_replace_names_and_raw_stays_available_as_companion_table()
        {
            var t = AddTyped();
            Assert.True(t.Columns[1].IsConverted);
            Assert.Equal("BIGINT", t.Columns[1].SqlType);
            Assert.Equal("VARCHAR", t.Columns[4].SqlType);   // 식별자는 변환하지 않는다
            Assert.Equal("가입자__raw", t.RawSqlReference.Trim('"').Split("\".\"")[1]);

            var r = Cells(await Q("SELECT 나이, 가입일, 금액, id, 활성, 비율 FROM 가입자 ORDER BY id"));
            Assert.Equal(new[] { "30", "2024-01-05", "1234.0", "001", "true", "12.0" }, r[0]);
            Assert.Equal(new[] { "<null>", "2024-01-06", "5.0", "002", "false", "5.0" }, r[1]);
            Assert.Equal(new[] { "<null>", "2024-01-07", "300.0", "003", "true", "<null>" }, r[2]);
            Assert.Equal(new[] { "41", "<null>", "<null>", "004", "<null>", "7.0" }, r[3]);

            // 원문은 __raw에서 그대로.
            Assert.Equal("x", await Scalar("SELECT 나이 FROM 가입자__raw WHERE id = '002'"));
            // SELECT * 는 표 그대로(컬럼 수·이름·순서 동일).
            var star = await Q("SELECT * FROM 가입자", 1);
            Assert.Equal(t.Columns.Select(c => c.Name), star.Columns.Select(c => c.Name));
        }

        [Fact]
        public async Task Cast_failures_are_counted_with_examples_and_null_tokens_are_not_failures()
        {
            var t = AddTyped();
            var reports = (await _ws.CheckTypedColumnsAsync(t)).ToDictionary(x => x.Column);
            Assert.Equal(new[] { "나이", "가입일", "금액", "활성", "비율" }, reports.Keys.OrderBy(k => Array.IndexOf(t.Columns.Select(c => c.Name).ToArray(), k)));

            Assert.Equal(3, reports["나이"].NonEmptyCount);              // 30, x, 41 (빈 값은 제외)
            Assert.Equal(1, reports["나이"].FailureCount);
            Assert.Equal(new[] { "x" }, reports["나이"].FailureExamples);
            Assert.Equal(1, reports["가입일"].FailureCount);
            Assert.Equal(new[] { "not a date" }, reports["가입일"].FailureExamples);
            Assert.Equal(1, reports["금액"].FailureCount);
            Assert.Equal(1, reports["활성"].FailureCount);               // maybe
            Assert.Equal(0, reports["비율"].FailureCount);               // N/A는 널 토큰
            Assert.Equal(3, reports["비율"].NonEmptyCount);
        }

        [Fact]
        public async Task Types_are_inferred_with_the_apps_inferrer_when_not_given()
        {
            var sb = new StringBuilder("번호,수량,비율,단가,등록일,활성,메모\n");
            for (int i = 1; i <= 50; i++)
                sb.Append($"{i:D3},{i},{i / 4.0:0.00},{i * 10},2024-01-{(i % 28) + 1:D2},{(i % 2 == 0 ? "yes" : "no")},메모 {i}\n");
            var t = _ws.AddCsv(Write("inf.csv", sb.ToString())).Tables[0];
            var types = t.Columns.ToDictionary(c => c.Name, c => c.Type);
            Assert.Equal(ColumnValueType.Identifier, types["번호"]);
            Assert.Equal(ColumnValueType.Integer, types["수량"]);
            Assert.Equal(ColumnValueType.Float, types["비율"]);
            Assert.Equal(ColumnValueType.Date, types["등록일"]);
            Assert.Equal(ColumnValueType.Boolean, types["활성"]);
            Assert.Equal("001", await Scalar("SELECT 번호 FROM inf ORDER BY 수량 LIMIT 1"));
            Assert.Equal("1275", await Scalar("SELECT sum(수량) FROM inf"));
        }

        [Fact]
        public async Task Zero_one_flag_columns_stay_numeric_so_they_can_be_summed()
        {
            var t = _ws.AddCsv(Write("flag.csv", "g\n0\n1\n1\n1\n")).Tables[0];
            Assert.Equal(ColumnValueType.Integer, t.Columns[0].Type);
            Assert.Equal("3", await Scalar("SELECT sum(g) FROM flag"));
        }

        [Fact]
        public async Task Date_formats_of_the_app_parser_convert()
        {
            string p = Write("d.csv", "d\n2024-01-05\n2024/1/6\n2024.01.07\n20240108\n2024년 1월 9일\n2024-03\n");
            var t = _ws.AddCsv(p, new CsvSourceOptions { ColumnTypes = new[] { ColumnValueType.Date } }).Tables[0];
            var r = Cells(await Q("SELECT d FROM d"));
            Assert.Equal(new[] { "2024-01-05", "2024-01-06", "2024-01-07", "2024-01-08", "2024-01-09", "2024-03-01" }, r.Select(x => x[0]));
            Assert.Equal(0, (await _ws.CheckTypedColumnsAsync(t))[0].FailureCount);
        }

        // ---- 미리보기 · 오류 ----------------------------------------------------------------------------

        [Fact]
        public async Task Preview_truncates_and_reports_it()
        {
            var sb = new StringBuilder("n\n");
            for (int i = 0; i < 500; i++) sb.Append(i).Append('\n');
            _ws.AddCsv(Write("big.csv", sb.ToString()));
            var r = await _ws.PreviewAsync("SELECT n FROM big ORDER BY n;", 200);
            Assert.Equal(200, r.Rows.Count);
            Assert.True(r.Truncated);
            Assert.Equal("0", r.Rows[0][0]);
            Assert.Equal("199", r.Rows[^1][0]);
            var all = await _ws.PreviewAsync("SELECT n FROM big", 500);
            Assert.False(all.Truncated);
            Assert.Equal(500, all.Rows.Count);
            Assert.True(all.Elapsed >= TimeSpan.Zero);
            Assert.Equal("BIGINT", all.Columns[0].SqlType);
        }

        [Fact]
        public async Task Preview_handles_trailing_comment_and_semicolon()
        {
            Assert.Equal("1", await Scalar("SELECT 1 -- 한 줄 주석\n;\n-- 끝"));
            Assert.Equal("2", await Scalar("/* a */ SELECT 2 /* b */;"));
        }

        [Fact]
        public async Task Syntax_error_reports_line_and_column_in_the_users_sql()
        {
            var ex = await Assert.ThrowsAsync<WorkspaceQueryException>(() => Q("SELECT 1,\n  2 FROM FROM"));
            Assert.Equal(QueryErrorKind.Syntax, ex.Kind);
            Assert.Equal(2, ex.Line);
            Assert.Equal(10, ex.Column);
        }

        [Fact]
        public async Task Syntax_error_column_counts_characters_not_bytes_for_korean_sql()
        {
            var ex = await Assert.ThrowsAsync<WorkspaceQueryException>(() => Q("SELECT 이름,\n  나이 FRM 명단"));
            Assert.Equal(QueryErrorKind.Syntax, ex.Kind);
            Assert.Equal(2, ex.Line);
            Assert.Equal(10, ex.Column);  // "  나이 FRM 명단": 명단은 10번째 글자
            var end = await Assert.ThrowsAsync<WorkspaceQueryException>(() => Q("SELECT 이름,\n  나이 FROM 명단 WHERE 도시 = 서울 AND"));
            Assert.Equal(2, end.Line);
            Assert.Equal(31, end.Column);  // 입력 끝
        }

        [Fact]
        public async Task Unknown_table_reports_a_readable_name_error_with_position()
        {
            _ws.AddCsv(Write("known.csv", "a\n1\n"));
            var ex = await Assert.ThrowsAsync<WorkspaceQueryException>(() => Q("SELECT *\nFROM unknwon"));
            Assert.Equal(QueryErrorKind.Name, ex.Kind);
            Assert.Equal(2, ex.Line);
            Assert.Equal(6, ex.Column);
            Assert.Contains("unknwon", ex.Message);
            Assert.DoesNotContain("LINE 2", ex.Message);
        }

        [Theory]
        [InlineData("DROP VIEW x")]
        [InlineData("CREATE TABLE z AS SELECT 1")]
        [InlineData("COPY (SELECT 1) TO 'x.csv'")]
        [InlineData("SELECT 1; SELECT 2")]
        [InlineData("")]
        public async Task Only_a_single_select_can_run(string sql)
        {
            var ex = await Assert.ThrowsAsync<WorkspaceQueryException>(() => Q(sql));
            Assert.Equal(QueryErrorKind.NotSelect, ex.Kind);
            Assert.False(_ws.Analyze(sql).IsValid);
        }

        [Fact]
        public void Analyze_collects_referenced_tables_including_subqueries_and_ctes()
        {
            var a = _ws.Analyze("WITH c AS (SELECT * FROM a) SELECT * FROM c JOIN s.b ON c.x = b.x WHERE c.y IN (SELECT y FROM d)");
            Assert.True(a.IsValid);
            var names = a.Tables.Select(t => (t.Schema, t.Name)).ToHashSet();
            Assert.Contains(("", "a"), names);
            Assert.Contains(("s", "b"), names);
            Assert.Contains(("", "d"), names);
        }

        // ---- 취소 ------------------------------------------------------------------------------------

        [Fact]
        public async Task A_long_query_is_cancelled_promptly()
        {
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var task = _ws.PreviewAsync("SELECT count(*) FROM range(300000) a(x), range(300000) b(y) WHERE (x * y) % 7 = 3", 10, cts.Token);
            await Task.Delay(400);
            Assert.False(task.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
            // 취소 뒤에도 엔진은 정상.
            Assert.Equal("1", await Scalar("SELECT 1"));
        }

        [Fact]
        public async Task Cancelled_export_leaves_no_partial_file()
        {
            string output = Path.Combine(_dir, "out.csv");
            using var cts = new CancellationTokenSource();
            var task = _ws.RunToCsvAsync("SELECT x, y FROM range(100000000) a(x), range(1000) b(y) ORDER BY x * y % 97, y", output, null, cts.Token);
            await Task.Delay(500);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(_dir, "out.csv*"));
        }

        [Fact]
        public async Task Already_cancelled_token_never_starts()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _ws.PreviewAsync("SELECT 1", 5, cts.Token));
        }

        // ---- CSV로 내보내기 ----------------------------------------------------------------------------

        [Fact]
        public async Task Run_to_csv_output_is_parsed_by_the_apps_own_reader_with_identical_values()
        {
            string src = Write("원본.csv",
                "id,이름,메모\n" +
                "001,\"홍,길동\",\"줄1\n줄2\"\n" +
                "002,\"He said \"\"hi\"\"\",\n" +
                "003,김철수,  앞뒤 공백  \n" +
                "010,😀 이모지,끝\n");
            var t = _ws.AddCsv(src, new CsvSourceOptions { ColumnTypes = new[] { ColumnValueType.Identifier, ColumnValueType.String, ColumnValueType.String } }).Tables[0];
            string output = Path.Combine(_dir, "result", "out.csv");
            var progressValues = new List<long>();
            var info = await _ws.RunToCsvAsync("SELECT * FROM 원본 ORDER BY id", output, new Progress<long>(progressValues.Add));
            Assert.Equal(4, info.RowCount);
            Assert.Equal(new[] { "id", "이름", "메모" }, info.Columns.Select(c => c.Name));
            Assert.Equal(output, info.OutputPath);

            // BOM 없는 UTF-8, 헤더 있음.
            byte[] bytes = File.ReadAllBytes(output);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

            static async Task<(string[] Header, List<string[]> Rows)> ReadAll(string path)
            {
                using var doc = VirtualCsvDocument.Open(path);
                await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
                var rows = new List<string[]>();
                for (int i = 0; i < doc.DataRowsAvailable; i++) rows.Add(doc.GetDataRow(i));
                return (doc.Header, rows);
            }
            var a = await ReadAll(src);
            var b = await ReadAll(output);
            Assert.Equal(a.Header, b.Header);
            Assert.Equal(a.Rows.Count, b.Rows.Count);
            for (int i = 0; i < a.Rows.Count; i++) Assert.Equal(a.Rows[i], b.Rows[i]);
            Assert.Equal("001", b.Rows[0][0]);
            Assert.Equal("홍,길동", b.Rows[0][1]);
            Assert.Equal("  앞뒤 공백  ", b.Rows[2][2]);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.tmp-*"));
        }

        [Fact]
        public async Task Run_to_csv_refuses_to_overwrite_a_registered_original()
        {
            string src = Write("keep.csv", "a\n1\n");
            _ws.AddCsv(src);
            await Assert.ThrowsAsync<InvalidOperationException>(() => _ws.RunToCsvAsync("SELECT 1 AS a", src));
            Assert.Equal("a\n1\n", File.ReadAllText(src));
            await Assert.ThrowsAsync<WorkspaceQueryException>(() => _ws.RunToCsvAsync("SELECT * FROM nope", Path.Combine(_dir, "o.csv")));
            Assert.False(File.Exists(Path.Combine(_dir, "o.csv")));
        }

        // ---- 조인 진단 ---------------------------------------------------------------------------------

        private (WorkspaceTable Orders, WorkspaceTable Customers) AddJoinTables()
        {
            var o = _ws.AddCsv(Write("주문.csv", "주문번호,고객코드,금액\n1,A,100\n2,A,200\n3,B,300\n4,C,400\n5,,500\n")).Tables[0];
            var c = _ws.AddCsv(Write("고객.csv", "고객코드,이름\nA,김\nA,김2\nB,박\nD,최\n")).Tables[0];
            return (o, c);
        }

        [Fact]
        public async Task Join_diagnostics_report_duplicate_key_explosion_and_match_the_real_join()
        {
            var (o, c) = AddJoinTables();
            var keys = new[] { new JoinKey("고객코드", "고객코드") };
            long expectedInner = 0;
            foreach (var kind in Enum.GetValues<JoinKind>())
            {
                var spec = new JoinSpec(o, c, keys, kind);
                var d = await _ws.CheckJoinAsync(spec);
                Assert.Equal(5, d.LeftRows);
                Assert.Equal(4, d.RightRows);
                Assert.Equal(1, d.LeftNullKeyRows);
                Assert.Equal(0, d.RightNullKeyRows);
                Assert.Equal(3, d.LeftDistinctKeys);
                Assert.Equal(3, d.RightDistinctKeys);
                Assert.Equal(2, d.MatchedKeys);
                Assert.Equal(1, d.LeftOnlyKeys);
                Assert.Equal(1, d.RightOnlyKeys);
                Assert.Equal(1, d.LeftDuplicateKeys);
                Assert.Equal(1, d.RightDuplicateKeys);
                Assert.Equal(2, d.LeftMaxKeyRows);
                Assert.Equal(2, d.RightMaxKeyRows);
                Assert.Equal(JoinCardinality.ManyToMany, d.Cardinality);
                Assert.Equal(5, d.MatchedRowsInner);
                Assert.Empty(d.KeyTypeWarnings);

                string sql = JoinSql.Build(spec);
                long actual = long.Parse(await Scalar($"SELECT count(*) FROM ({sql})"));
                Assert.Equal(actual, d.ExpectedRows);
                if (kind == JoinKind.Inner) expectedInner = actual;
            }
            Assert.Equal(5, expectedInner);
            var inner = await _ws.CheckJoinAsync(new JoinSpec(o, c, keys));
            Assert.True(inner.GrowthFactor > 0.99 && inner.GrowthFactor < 1.01); // 5 / max(5,4)
            var left = await _ws.CheckJoinAsync(new JoinSpec(o, c, keys, JoinKind.Left));
            Assert.Equal(7, left.ExpectedRows);
        }

        [Fact]
        public async Task Join_diagnostics_detect_a_row_explosion_and_zero_matches()
        {
            var a = _ws.AddCsv(Write("a.csv", "k,v\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => "x," + i)) + "\n")).Tables[0];
            var b = _ws.AddCsv(Write("b.csv", "k,w\n" + string.Join("\n", Enumerable.Range(0, 40).Select(i => "x," + i)) + "\n")).Tables[0];
            var d = await _ws.CheckJoinAsync(new JoinSpec(a, b, new[] { new JoinKey("k", "k") }));
            Assert.Equal(1200, d.ExpectedRows);
            Assert.True(d.GrowthFactor >= 30);
            Assert.Equal(30, d.LeftMaxKeyRows);
            Assert.Equal(40, d.RightMaxKeyRows);

            var none = _ws.AddCsv(Write("n.csv", "k\ny\n")).Tables[0];
            var d2 = await _ws.CheckJoinAsync(new JoinSpec(a, none, new[] { new JoinKey("k", "k") }));
            Assert.True(d2.NoMatches);
            Assert.Equal(0, d2.ExpectedRows);
        }

        [Fact]
        public async Task Join_keys_of_different_types_compare_as_text_and_warn()
        {
            var ids = _ws.AddCsv(Write("ids.csv", "id,x\n1,a\n2,b\n"), new CsvSourceOptions
                { ColumnTypes = new[] { ColumnValueType.Integer, ColumnValueType.String } }).Tables[0];     // 정수
            var codes = _ws.AddCsv(Write("codes.csv", "id,y\n001,a\n2,b\n"), new CsvSourceOptions
                { ColumnTypes = new[] { ColumnValueType.Identifier, ColumnValueType.String } }).Tables[0];
            var spec = new JoinSpec(ids, codes, new[] { new JoinKey("id", "id") });
            var d = await _ws.CheckJoinAsync(spec);
            Assert.Single(d.KeyTypeWarnings);
            Assert.Equal(1, d.MatchedKeys);          // 2만 일치, 1 ≠ '001'
            long actual = long.Parse(await Scalar($"SELECT count(*) FROM ({JoinSql.Build(spec)})"));
            Assert.Equal(actual, d.ExpectedRows);
        }

        [Fact]
        public async Task Multi_column_keys_and_unknown_columns()
        {
            var a = _ws.AddCsv(Write("m1.csv", "a,b,v\n1,x,1\n1,y,2\n2,x,3\n")).Tables[0];
            var b = _ws.AddCsv(Write("m2.csv", "a,b,w\n1,x,9\n2,y,8\n")).Tables[0];
            var d = await _ws.CheckJoinAsync(new JoinSpec(a, b, new[] { new JoinKey("a", "a"), new JoinKey("b", "b") }));
            Assert.Equal(1, d.MatchedKeys);
            Assert.Equal(2, d.LeftOnlyKeys);
            Assert.Equal(1, d.RightOnlyKeys);
            await Assert.ThrowsAsync<ArgumentException>(() => _ws.CheckJoinAsync(new JoinSpec(a, b, new[] { new JoinKey("zzz", "a") })));
            var sql = JoinSql.Build(new JoinSpec(a, b, new[] { new JoinKey("a", "a") }));
            var cols = (await Q(sql, 1)).Columns.Select(c => c.Name).ToArray();
            Assert.Equal(new[] { "a", "b", "v", "a_right", "b_right", "w" }, cols);
        }

        // ---- DB 원본 -----------------------------------------------------------------------------------

        [Fact]
        public async Task Database_source_is_a_schema_with_one_table_per_sheet()
        {
            string names = Write("sheet1.csv", "이름,점수\n홍길동,90\n김철수,80\n");
            string answers = Write("sheet2.csv", "이름,응답\n홍길동,예\n홍길동,아니오\n김철수,예\n");
            string origin = Write("설문 데이터.xlsx", "fake");
            var src = _ws.AddDatabase("설문 데이터", new[]
            {
                new DbTableInput("명단", names),
                new DbTableInput("응답 1", answers),
            }, origin);
            Assert.Equal(WorkspaceSourceKind.Database, src.Kind);
            Assert.Equal("설문_데이터", src.Name);
            Assert.Equal(new[] { "명단", "응답_1" }, src.Tables.Select(t => t.Name));
            Assert.Equal("설문_데이터.응답_1", src.Tables[1].DisplayName);
            Assert.Equal(origin, src.Path);

            var r = Cells(await Q("SELECT n.이름, count(*) AS 응답수 FROM 설문_데이터.명단 n JOIN 설문_데이터.응답_1 a ON n.이름 = a.이름 GROUP BY n.이름 ORDER BY 1"));
            Assert.Equal(new[] { new[] { "김철수", "1" }, new[] { "홍길동", "2" } }, r);

            var view = _ws.CreateView("응답통계", "SELECT 이름, count(*) AS n FROM 설문_데이터.응답_1 GROUP BY 이름");
            Assert.Equal(new[] { "설문_데이터" }, view.Dependencies);
            Assert.False(_ws.IsSourceChanged(src));
            File.SetLastWriteTimeUtc(origin, DateTime.UtcNow.AddMinutes(10));
            Assert.True(_ws.IsSourceChanged(src));

            // 이름 충돌: 같은 이름의 DB는 _2.
            Assert.Equal("설문_데이터_2", _ws.AddDatabase("설문 데이터", new[] { new DbTableInput("t", names) }).Name);
            // 스키마 제거 시 표도 사라진다.
            Assert.Throws<InvalidOperationException>(() => _ws.Remove(src));
            _ws.Remove(src, cascade: true);
            await Assert.ThrowsAsync<WorkspaceQueryException>(() => Q("SELECT * FROM 설문_데이터.명단"));
            Assert.Empty(_ws.Views);
        }

        [Fact]
        public void Failed_database_registration_leaves_nothing_behind()
        {
            string good = Write("g.csv", "a\n1\n");
            Assert.Throws<FileNotFoundException>(() => _ws.AddDatabase("db", new[] { new DbTableInput("a", good), new DbTableInput("b", Path.Combine(_dir, "missing.csv")) }));
            Assert.Empty(_ws.Sources);
            string empty = Write("emp.csv", "");
            Assert.ThrowsAny<Exception>(() => _ws.AddDatabase("db", new[] { new DbTableInput("a", good), new DbTableInput("b", empty) }));
            Assert.Empty(_ws.Sources);
            Assert.Equal("db", _ws.AddDatabase("db", new[] { new DbTableInput("a", good) }).Name); // 이름이 남아 있지 않다
        }

        // ---- 이름 바꾸기 · 제거 · 다시 읽기 ---------------------------------------------------------------

        [Fact]
        public async Task Rename_moves_the_table_and_is_refused_while_views_use_it()
        {
            var src = _ws.AddCsv(Write("old.csv", "a\n1\n2\n"));
            _ws.Rename(src, "새 이름");
            Assert.Equal("새_이름", src.Name);
            Assert.Equal("새_이름", src.Tables[0].Name);
            Assert.Equal("2", await Scalar("SELECT count(*) FROM 새_이름"));
            Assert.Equal("1", await Scalar("SELECT min(a) FROM 새_이름__raw"));
            await Assert.ThrowsAsync<WorkspaceQueryException>(() => Q("SELECT * FROM old"));

            _ws.CreateView("v", "SELECT * FROM 새_이름");
            Assert.Throws<InvalidOperationException>(() => _ws.Rename(src, "other"));
            // 같은 이름 규칙: 다른 원본과 겹치면 _2
            var other = _ws.AddCsv(Write("other.csv", "a\n9\n"));
            _ws.Rename(other, "v2");
            Assert.Equal("v2", other.Name);
        }

        [Fact]
        public async Task Reload_picks_up_a_changed_file_and_keeps_known_types()
        {
            string p = Write("r.csv", "x,y\n1,a\n2,b\n");
            var src = _ws.AddCsv(p);
            Assert.Equal(ColumnValueType.Integer, src.Tables[0].Columns[0].Type);
            Assert.False(_ws.IsSourceChanged(src));
            File.WriteAllText(p, "x,y,z\n1,a,t\n2,b,u\n3,c,v\n", Utf8);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(3));
            Assert.True(_ws.IsSourceChanged(src));
            _ws.Reload(src);
            Assert.False(_ws.IsSourceChanged(src));
            Assert.Equal(new[] { "x", "y", "z" }, src.Tables[0].Columns.Select(c => c.Name));
            Assert.Equal(ColumnValueType.Integer, src.Tables[0].Columns[0].Type);
            Assert.Equal("3", await Scalar("SELECT count(*) FROM r"));
        }

        // ---- 뷰 · 뷰 위의 뷰 · 오래됨 ------------------------------------------------------------------

        [Fact]
        public async Task Views_on_views_track_dependencies_and_staleness_after_touching_a_source()
        {
            string p = Write("매출.csv", "지점,금액\n서울,100\n서울,200\n부산,50\n");
            var src = _ws.AddCsv(p);
            var v1 = _ws.CreateView("서울매출", "SELECT * FROM 매출 WHERE 지점 = '서울'");
            var v2 = _ws.CreateView("서울합계", "SELECT sum(금액) AS 합계 FROM 서울매출");
            Assert.Equal(new[] { "매출" }, v1.Dependencies);
            Assert.Equal(new[] { "서울매출" }, v2.Dependencies);
            Assert.Equal(new[] { "합계" }, v2.Columns.Select(c => c.Name));
            Assert.Equal(new[] { "서울매출", "서울합계" }, _ws.DependentViews("매출").Select(v => v.Name).OrderBy(x => x));
            Assert.Equal("300", await Scalar("SELECT 합계 FROM 서울합계"));

            // 결과 계산(임시 CSV) — 아직 오래되지 않음.
            Assert.False(v2.HasResult);
            Assert.False(_ws.IsStale(v2));
            await _ws.MaterializeViewAsync(v2);
            Assert.True(v2.HasResult);
            Assert.Equal(1, v2.ResultRowCount);
            Assert.Equal("합계\n300\n", File.ReadAllText(v2.ResultPath!));
            Assert.False(_ws.IsStale(v2));
            string firstResult = v2.ResultPath!;
            await _ws.MaterializeViewAsync(v2);                    // 재사용
            Assert.Equal(firstResult, v2.ResultPath);

            // 원본 변경 → 아래 뷰까지 오래됨.
            File.WriteAllText(p, "지점,금액\n서울,100\n서울,200\n서울,700\n부산,50\n", Utf8);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(2));
            Assert.True(_ws.IsStale(v2));
            await _ws.MaterializeViewAsync(v2);
            Assert.Equal("합계\n1000\n", File.ReadAllText(v2.ResultPath!));
            Assert.False(_ws.IsStale(v2));
            Assert.NotEqual(firstResult, v2.ResultPath);
            Assert.False(File.Exists(firstResult));                // 이전 결과 정리

            // 아래 뷰의 정의를 바꾸면 위 뷰의 결과도 오래됨, 컬럼 정보는 갱신됨.
            _ws.UpdateView(v1, "SELECT * FROM 매출 WHERE 지점 = '부산'", false);
            Assert.True(_ws.IsStale(v2));
            await _ws.MaterializeViewAsync(v2);
            Assert.Equal("합계\n50\n", File.ReadAllText(v2.ResultPath!));

            // 제거 규칙
            Assert.Throws<InvalidOperationException>(() => _ws.RemoveView(v1));
            Assert.Throws<InvalidOperationException>(() => _ws.RenameView(v1, "x"));
            string result = v2.ResultPath!;
            _ws.RemoveView(v1, cascade: true);
            Assert.Empty(_ws.Views);
            Assert.False(File.Exists(result));
            Assert.Equal("4", await Scalar("SELECT count(*) FROM 매출"));        // 파일은 바뀐 대로(4행)
        }

        [Fact]
        public void View_names_must_follow_the_rule_and_be_unique_and_sql_must_be_one_select()
        {
            _ws.AddCsv(Write("t.csv", "a\n1\n"));
            Assert.Throws<ArgumentException>(() => _ws.CreateView("t", "SELECT 1"));            // 원본과 겹침
            Assert.Throws<ArgumentException>(() => _ws.CreateView("a b", "SELECT 1"));          // 규칙 위반
            Assert.Throws<ArgumentException>(() => _ws.CreateView("t__raw", "SELECT 1"));        // 보조 뷰 이름
            Assert.Throws<WorkspaceQueryException>(() => _ws.CreateView("bad", "DROP VIEW t"));
            Assert.Throws<WorkspaceQueryException>(() => _ws.CreateView("bad", "SELECT * FROM missing_table"));
            Assert.Empty(_ws.Views);
            var v = _ws.CreateView("ok", "SELECT * FROM t;");
            Assert.Throws<ArgumentException>(() => _ws.CreateView("OK", "SELECT 2"));            // 대소문자만 다름
            Assert.Equal("ok", v.Name);
            Assert.Equal("ok_2", _ws.SuggestName("ok", "view"));
        }

        [Fact]
        public void A_view_cannot_depend_on_itself_even_through_another_view()
        {
            _ws.AddCsv(Write("t.csv", "a\n1\n"));
            var v1 = _ws.CreateView("v1", "SELECT * FROM t");
            var v2 = _ws.CreateView("v2", "SELECT * FROM v1");
            Assert.Throws<WorkspaceQueryException>(() => _ws.UpdateView(v1, "SELECT * FROM v2", false));
            Assert.Throws<WorkspaceQueryException>(() => _ws.UpdateView(v1, "SELECT * FROM v1", false));
            Assert.Equal("SELECT * FROM t", v1.Sql);
        }

        [Fact]
        public async Task A_broken_dependent_view_reports_its_error_instead_of_throwing()
        {
            var src = _ws.AddCsv(Write("t.csv", "a,b\n1,2\n"));
            var v1 = _ws.CreateView("v1", "SELECT a, b FROM t");
            var v2 = _ws.CreateView("v2", "SELECT b FROM v1");
            _ws.UpdateView(v1, "SELECT a FROM t", false);
            Assert.NotNull(v2.Error);
            Assert.Null(v1.Error);
            await Assert.ThrowsAsync<WorkspaceQueryException>(() => _ws.MaterializeViewAsync(v2));
            _ws.UpdateView(v1, "SELECT a, b FROM t", false);
            Assert.Null(v2.Error);
        }

        private sealed class FakeSnapshots(Func<WorkspaceTable, string?> f) : IEditSnapshotProvider
        {
            public string? GetEditedSnapshotPath(WorkspaceTable table, CancellationToken ct) => f(table);
        }

        [Fact]
        public async Task Include_unsaved_edits_computes_from_the_edited_snapshot_only_for_that_view()
        {
            string p = Write("e.csv", "a\n1\n2\n");
            string snapshot = Write("e.edited.csv", "a\n1\n2\n3\n");
            var src = _ws.AddCsv(p);
            _ws.EditSnapshotProvider = new FakeSnapshots(t => t.Name == "e" ? snapshot : null);
            var saved = _ws.CreateView("saved", "SELECT sum(a) AS s FROM e", includeUnsavedEdits: false);
            var edited = _ws.CreateView("edited", "SELECT sum(a) AS s FROM e", includeUnsavedEdits: true);
            var onTop = _ws.CreateView("on_top", "SELECT s * 10 AS s10 FROM edited", includeUnsavedEdits: true);
            await _ws.MaterializeViewAsync(saved);
            await _ws.MaterializeViewAsync(edited);
            await _ws.MaterializeViewAsync(onTop);
            Assert.Equal("s\n3\n", File.ReadAllText(saved.ResultPath!));
            Assert.Equal("s\n6\n", File.ReadAllText(edited.ResultPath!));
            Assert.Equal("s10\n60\n", File.ReadAllText(onTop.ResultPath!));
            // 라이브 질의는 저장된 파일 기준.
            Assert.Equal("3", await Scalar("SELECT s FROM edited"));
            Assert.Equal(File.ReadAllText(p), "a\n1\n2\n");
        }

        // ---- 도우미 ----------------------------------------------------------------------------------

        [Fact]
        public void Sql_text_helpers_strip_terminators_without_touching_strings()
        {
            Assert.Equal("SELECT 1", SqlText.StripTerminator("SELECT 1;  ;\n-- done"));
            Assert.Equal("SELECT ';'", SqlText.StripTerminator("SELECT ';';"));
            Assert.Equal("SELECT 1; SELECT 2", SqlText.StripTerminator("SELECT 1; SELECT 2;"));
            var tokens = SqlText.Tokenize("SELECT \"한 글\" /* c */ FROM t -- x");
            Assert.Contains(tokens, t => t.Kind == SqlTokenKind.QuotedIdentifier);
            Assert.Equal(2, tokens.Count(t => t.Kind == SqlTokenKind.Comment));
        }

        [Fact]
        public void Memory_limit_is_configurable_and_defaults_to_a_sane_value()
        {
            Assert.Equal(1L << 30, _ws.MemoryLimitBytes);
            _ws.SetMemoryLimit(512L << 20);
            Assert.Equal(512L << 20, _ws.MemoryLimitBytes);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ws.SetMemoryLimit(1024));
            using var def = new DataWorkspace(new DataWorkspaceOptions { TempRoot = Path.Combine(_dir, "duck2") });
            Assert.InRange(def.MemoryLimitBytes, 512L << 20, 32L << 30);
        }

        [Fact]
        public async Task Disposing_removes_session_temp_files_and_blocks_use()
        {
            string tempRoot = Path.Combine(_dir, "duck3");
            string p = Write("z.csv", "a\n1\n");
            var ws = new DataWorkspace(new DataWorkspaceOptions { TempRoot = tempRoot });
            ws.AddCsv(p);
            var v = ws.CreateView("zz", "SELECT * FROM z");
            await ws.MaterializeViewAsync(v);
            string result = v.ResultPath!;
            Assert.True(File.Exists(result));
            ws.Dispose();
            Assert.False(File.Exists(result));
            Assert.Throws<ObjectDisposedException>(() => ws.AddCsv(p));
            await Assert.ThrowsAnyAsync<Exception>(() => ws.PreviewAsync("SELECT 1", 1));
            Assert.True(File.Exists(p));
        }
    }
}
