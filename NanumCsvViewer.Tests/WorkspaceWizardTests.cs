using System.Globalization;
using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Workspace;

namespace NanumCsvViewer.Tests
{
    /// <summary>마법사 SQL 생성기(이어 붙이기·비교·그룹 집계)를 실제 DuckDB에서 임시 CSV로 검증한다.</summary>
    public class WorkspaceWizardTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ncv_wiz_" + Guid.NewGuid().ToString("N"));
        private readonly DataWorkspace _ws;
        private static readonly UTF8Encoding Utf8 = new(false);

        public WorkspaceWizardTests()
        {
            Directory.CreateDirectory(_dir);
            _ws = new DataWorkspace(new DataWorkspaceOptions { TempRoot = Path.Combine(_dir, "duck"), MemoryLimitBytes = 1L << 30, Threads = 2 });
        }

        public void Dispose()
        {
            _ws.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private WorkspaceTable Table(string name, string csv)
        {
            string p = Path.Combine(_dir, name + ".csv");
            File.WriteAllText(p, csv, Utf8);
            return _ws.AddCsv(p).Tables[0];
        }

        private static string[][] Cells(QueryPreview p) => p.Rows.Select(r => r.Select(c => c ?? "<null>").ToArray()).ToArray();

        private async Task<QueryPreview> Run(string sql) => await _ws.PreviewAsync(sql, 1000);

        private static Dictionary<string, long> Summary(QueryPreview p)
            => p.Columns.Select((c, i) => (c.Name, v: Convert.ToInt64(p.Rows[0][i]))).ToDictionary(x => x.Name, x => x.v);

        // ---- 이어 붙이기 ----------------------------------------------------------------------------

        [Fact]
        public async Task Append_by_name_matches_case_insensitively_and_fills_missing_columns_with_null()
        {
            var a = Table("jan", "ID,Name,Amount\n1,Kim,10\n2,Lee,20\n");
            var b = Table("feb", "id,name,city\n3,Park,Seoul\n");
            var c = Table("mar", "NAME,amount\nChoi,5\nJung,6\n");
            var tables = new IWorkspaceRelation[] { a, b, c };

            var map = WizardSql.AutoMap(tables, UnionMode.ByName);
            Assert.Equal(new[] { "ID", "Name", "Amount", "city" }, map.Select(m => m.OutputName));
            Assert.Equal(new string?[] { "ID", "id", null }, map[0].Sources);
            Assert.Equal(new string?[] { "Amount", null, "amount" }, map[2].Sources);

            string sql = WizardSql.AppendSql(new AppendSpec(tables));
            var r = await Run(sql);
            Assert.Equal(new[] { "ID", "Name", "Amount", "city" }, r.Columns.Select(x => x.Name));
            Assert.Equal(5, r.Rows.Count);
            Assert.Equal(new[]
            {
                new[] { "1", "Kim", "10", "<null>" }, new[] { "2", "Lee", "20", "<null>" }, new[] { "3", "Park", "<null>", "Seoul" },
                new[] { "<null>", "Choi", "5", "<null>" }, new[] { "<null>", "Jung", "6", "<null>" },
            }, Cells(r));
        }

        [Fact]
        public async Task Append_with_source_column_labels_every_row_with_its_table()
        {
            var a = Table("north", "x\n1\n2\n");
            var b = Table("south", "x\n3\n");
            var r = await Run(WizardSql.AppendSql(new AppendSpec(new IWorkspaceRelation[] { a, b }, SourceColumnName: "source_table")));
            Assert.Equal(new[] { "x", "source_table" }, r.Columns.Select(x => x.Name));
            Assert.Equal(new[] { new[] { "1", "north" }, new[] { "2", "north" }, new[] { "3", "south" } }, Cells(r));
        }

        [Fact]
        public async Task Append_by_position_ignores_names()
        {
            var a = Table("p1", "a,b\n1,x\n");
            var b = Table("p2", "c,d,e\n2,y,z\n");
            var tables = new IWorkspaceRelation[] { a, b };
            var map = WizardSql.AutoMap(tables, UnionMode.ByPosition);
            Assert.Equal(new[] { "a", "b", "e" }, map.Select(m => m.OutputName));
            var r = await Run(WizardSql.AppendSql(new AppendSpec(tables, Mode: UnionMode.ByPosition)));
            Assert.Equal(new[] { new[] { "1", "x", "<null>" }, new[] { "2", "y", "z" } }, Cells(r));
        }

        [Fact]
        public async Task Append_honours_a_custom_mapping_and_renames()
        {
            var a = Table("m1", "id,val\n1,a\n");
            var b = Table("m2", "key,value\n2,b\n");
            var map = new[]
            {
                new AppendColumn("번호", new string?[] { "id", "key" }),
                new AppendColumn("값", new string?[] { "val", "value" }),
            };
            var r = await Run(WizardSql.AppendSql(new AppendSpec(new IWorkspaceRelation[] { a, b }, map)));
            Assert.Equal(new[] { "번호", "값" }, r.Columns.Select(x => x.Name));
            Assert.Equal(new[] { new[] { "1", "a" }, new[] { "2", "b" } }, Cells(r));
        }

        [Fact]
        public async Task Append_type_conflicts_are_reported_and_combined_as_text_from_the_raw_values()
        {
            var a = Table("t1", "k,v\n1,\"1,000\"\n2,\"2,500\"\n");
            var b = Table("t2", "k,v\n3,hello\n4,world\n");
            Assert.Equal(ColumnValueType.Integer, a.Columns[1].Type);
            var spec = new AppendSpec(new IWorkspaceRelation[] { a, b });
            var plan = WizardSql.PlanAppend(spec);
            Assert.Equal(AppendTypeIssue.ConvertedToText, plan.Columns[1].Issue);
            Assert.Equal("VARCHAR", plan.Columns[1].SqlType);
            Assert.Single(plan.Warnings);
            Assert.Contains("v", plan.Warnings[0]);
            var r = await Run(WizardSql.AppendSql(spec));
            Assert.Equal(new[] { "1,000", "2,500", "hello", "world" }, Cells(r).Select(x => x[1]));
            Assert.Equal(new[] { "1", "2", "3", "4" }, Cells(r).Select(x => x[0]));
        }

        [Fact]
        public async Task Append_widens_integer_and_float_without_a_warning()
        {
            var a = Table("w1", "n\n1\n2\n");
            var b = Table("w2", "n\n1.5\n2.5\n");
            var spec = new AppendSpec(new IWorkspaceRelation[] { a, b });
            var plan = WizardSql.PlanAppend(spec);
            Assert.Equal(AppendTypeIssue.Widened, plan.Columns[0].Issue);
            Assert.Empty(plan.Warnings);
            var r = await Run(WizardSql.AppendSql(spec));
            Assert.Equal(new[] { "1.0", "2.0", "1.5", "2.5" }, Cells(r).Select(x => x[0]));
        }

        [Fact]
        public void Append_rejects_bad_specs()
        {
            var a = Table("e1", "x\n1\n");
            var b = Table("e2", "x\n2\n");
            var two = new IWorkspaceRelation[] { a, b };
            Assert.Throws<ArgumentException>(() => WizardSql.AppendSql(new AppendSpec(new IWorkspaceRelation[] { a })));
            Assert.Throws<ArgumentException>(() => WizardSql.AppendSql(new AppendSpec(two, SourceColumnName: "X")));
            Assert.Throws<ArgumentException>(() => WizardSql.AppendSql(new AppendSpec(two,
                new[] { new AppendColumn("o", new string?[] { "x", "nope" }) })));
            Assert.Throws<ArgumentException>(() => WizardSql.AppendSql(new AppendSpec(two,
                new[] { new AppendColumn("o", new string?[] { "x", "x" }), new AppendColumn("O", new string?[] { "x", "x" }) })));
        }

        // ---- 비교 -----------------------------------------------------------------------------------

        private (WorkspaceTable L, WorkspaceTable R) CompareTables()
        {
            var l = Table("old",
                "id,name,score,city\n1,Alice,10,Seoul\n2,Bob,20,Busan\n3,Carol,,Daegu\n4,Dave,40,Ulsan\n5,Eve,50,Jeju\n5,Eve2,55,Jeju\n,NoKey,1,X\n");
            var r = Table("new",
                "id,name,score,city\n1,alice,10,Seoul\n2,Bob,25,Busan\n3,Carol,,Daegu\n5,Eve,50,Jeju\n6,Frank,60,Gwangju\n,NoKeyR,2,Y\n");
            return (l, r);
        }

        private static CompareSpec Spec(WorkspaceTable l, WorkspaceTable r, bool ignoreCase = false, bool trim = false, bool unchanged = false, bool @long = false)
            => new(l, r, new[] { new JoinKey("id", "id") }, null, ignoreCase, trim, unchanged, @long);

        [Fact]
        public async Task Compare_summary_counts_added_removed_changed_unchanged_duplicates_and_null_keys()
        {
            var (l, r) = CompareTables();
            var s = Summary(await Run(WizardSql.CompareSummarySql(Spec(l, r))));
            Assert.Equal(7, s["left_rows"]);
            Assert.Equal(6, s["right_rows"]);
            Assert.Equal(2, s["added"]);      // id 6 + 비어 있는 키(오른쪽)
            Assert.Equal(2, s["removed"]);    // id 4 + 비어 있는 키(왼쪽)
            Assert.Equal(2, s["changed"]);    // id 1(이름 대소문자), id 2(점수)
            Assert.Equal(1, s["unchanged"]);  // id 3: NULL = NULL
            Assert.Equal(1, s["duplicate_keys"]);
            Assert.Equal(2, s["duplicate_left_rows"]);
            Assert.Equal(1, s["duplicate_right_rows"]);
            Assert.Equal(1, s["null_key_left_rows"]);
            Assert.Equal(1, s["null_key_right_rows"]);
            // 비교 컬럼 = name, score, city 순서
            Assert.Equal(1, s["changed_cells_0"]);
            Assert.Equal(1, s["changed_cells_1"]);
            Assert.Equal(0, s["changed_cells_2"]);
        }

        [Fact]
        public async Task Compare_ignore_case_turns_a_case_only_difference_into_unchanged()
        {
            var (l, r) = CompareTables();
            var s = Summary(await Run(WizardSql.CompareSummarySql(Spec(l, r, ignoreCase: true))));
            Assert.Equal(1, s["changed"]);
            Assert.Equal(2, s["unchanged"]);
            Assert.Equal(0, s["changed_cells_0"]);
        }

        [Fact]
        public async Task Compare_ignore_whitespace_trims_ends_and_collapses_runs()
        {
            var l = Table("ws_old", "id,name\n1,Bob Lee\n2,Amy\n3,X\n");
            var r = Table("ws_new", "id,name\n1,\"  Bob   Lee \"\n2,\"Amy\t\"\n3,Y\n");
            var strict = Summary(await Run(WizardSql.CompareSummarySql(Spec(l, r))));
            Assert.Equal(3, strict["changed"]);
            var loose = Summary(await Run(WizardSql.CompareSummarySql(Spec(l, r, trim: true))));
            Assert.Equal(1, loose["changed"]);
            Assert.Equal(2, loose["unchanged"]);
        }

        [Fact]
        public async Task Compare_wide_rows_list_changes_old_and_new_values_and_report_duplicate_keys()
        {
            var (l, r) = CompareTables();
            var res = await Run(WizardSql.CompareSql(Spec(l, r)));
            Assert.Equal(new[] { "change_type", "id", "detail", "name_old", "name_new", "score_old", "score_new", "city_old", "city_new" },
                res.Columns.Select(c => c.Name));
            var rows = Cells(res);
            Assert.Equal(new[] { "added", "added", "removed", "removed", "changed", "changed", "duplicate_key" }, rows.Select(x => x[0]));
            var added6 = rows.Single(x => x[0] == "added" && x[1] == "6");
            Assert.Equal(new[] { "added", "6", "<null>", "<null>", "Frank", "<null>", "60", "<null>", "Gwangju" }, added6);
            var c2 = rows.Single(x => x[0] == "changed" && x[1] == "2");
            Assert.Equal("score", c2[2]);
            Assert.Equal(new[] { "20", "25" }, new[] { c2[5], c2[6] });
            var c1 = rows.Single(x => x[0] == "changed" && x[1] == "1");
            Assert.Equal("name", c1[2]);
            Assert.Equal(new[] { "Alice", "alice" }, new[] { c1[3], c1[4] });
            var dup = rows.Single(x => x[0] == "duplicate_key");
            Assert.Equal("5", dup[1]);
            Assert.Contains("2", dup[2]);
            Assert.Equal("<null>", dup[3]);
            // 키가 NULL인 행은 짝지을 수 없어 added/removed로 나오고 이유가 적힌다.
            var nullKey = rows.Where(x => x[1] == "<null>").ToArray();
            Assert.Equal(new[] { "added", "removed" }, nullKey.Select(x => x[0]).OrderBy(x => x));
            Assert.All(nullKey, x => Assert.NotEqual("<null>", x[2]));
        }

        [Fact]
        public async Task Compare_include_unchanged_adds_the_unchanged_rows()
        {
            var (l, r) = CompareTables();
            var rows = Cells(await Run(WizardSql.CompareSql(Spec(l, r, unchanged: true))));
            Assert.Equal(1, rows.Count(x => x[0] == "unchanged"));
            Assert.Equal("3", rows.Single(x => x[0] == "unchanged")[1]);
        }

        [Fact]
        public async Task Compare_long_format_has_one_row_per_changed_cell()
        {
            var (l, r) = CompareTables();
            var res = await Run(WizardSql.CompareSql(Spec(l, r, @long: true)));
            Assert.Equal(new[] { "change_type", "id", "column_name", "old_value", "new_value", "detail" }, res.Columns.Select(c => c.Name));
            var cells = Cells(res).Where(x => x[0] == "changed").ToArray();
            Assert.Equal(2, cells.Length);
            Assert.Contains(cells, x => x[1] == "2" && x[2] == "score" && x[3] == "20" && x[4] == "25");
            Assert.Contains(cells, x => x[1] == "1" && x[2] == "name" && x[3] == "Alice" && x[4] == "alice");
            Assert.Equal(1, Cells(res).Count(x => x[0] == "duplicate_key"));
        }

        [Fact]
        public async Task Compare_composite_keys_and_explicit_columns()
        {
            var l = Table("ck_old", "y,m,amt,memo\n2024,1,10,a\n2024,2,20,b\n2025,1,30,c\n");
            var r = Table("ck_new", "y,m,amt,memo\n2024,1,10,z\n2024,2,21,b\n");
            var spec = new CompareSpec(l, r, new[] { new JoinKey("y", "y"), new JoinKey("m", "m") },
                new[] { new JoinKey("amt", "amt") });
            var s = Summary(await Run(WizardSql.CompareSummarySql(spec)));
            Assert.Equal(new long[] { 1, 1, 1 }, new[] { s["removed"], s["changed"], s["unchanged"] });
            Assert.Equal(0, s["added"]);
        }

        [Fact]
        public async Task Compare_with_different_key_types_compares_as_text_and_warns()
        {
            var l = Table("kt_old", "n,v\nx1,a\n2,b\n");
            var r = Table("kt_new", "n,v\n1,a\n2,b\n");
            Assert.NotEqual(l.Columns[0].SqlType, r.Columns[0].SqlType);
            var spec = new CompareSpec(l, r, new[] { new JoinKey("n", "n") });
            var s = Summary(await Run(WizardSql.CompareSummarySql(spec)));
            // x1(문자열)과 1(정수)은 짝이 없다 → 한쪽에만 있음, 2는 글자로 비교해도 짝지어짐.
            Assert.Equal(1, s["added"]);
            Assert.Equal(1, s["removed"]);
            Assert.Equal(1, s["unchanged"]);
            Assert.Single(WizardSql.CompareWarnings(spec));
        }

        // ---- 그룹 집계 ------------------------------------------------------------------------------

        [Fact]
        public async Task Group_sql_matches_the_apps_group_by()
        {
            var sb = new StringBuilder("region,product,amount\n");
            var rng = new Random(7);
            var rows = new List<string[]>();
            string[] regions = { "서울", "부산", "대구", "Seoul" };
            for (int i = 0; i < 400; i++)
            {
                string region = regions[rng.Next(regions.Length)];
                string product = "P" + rng.Next(5);
                string amount = (rng.Next(1, 1000) / 4.0).ToString("0.00", CultureInfo.InvariantCulture);
                rows.Add(new[] { region, product, amount });
                sb.Append(region).Append(',').Append(product).Append(',').Append(amount).Append('\n');
            }
            var t = Table("sales", sb.ToString());

            var sql = WizardSql.GroupSql(new GroupSpec(t, new[] { "region" }, new[]
            {
                new GroupAggregate(GroupFunction.Count, null),
                new GroupAggregate(GroupFunction.Sum, "amount"),
                new GroupAggregate(GroupFunction.Avg, "amount"),
                new GroupAggregate(GroupFunction.Median, "amount"),
                new GroupAggregate(GroupFunction.Min, "amount"),
                new GroupAggregate(GroupFunction.Max, "amount"),
                new GroupAggregate(GroupFunction.CountDistinct, "product"),
            }));
            var res = await Run(sql);
            Assert.Equal(new[] { "region", "count", "sum_amount", "avg_amount", "median_amount", "min_amount", "max_amount", "count_distinct_product" },
                res.Columns.Select(c => c.Name));

            var funcs = new[] { AggregationFunction.Count, AggregationFunction.Sum, AggregationFunction.Mean, AggregationFunction.Median,
                AggregationFunction.Min, AggregationFunction.Max };
            var app = CsvAnalytics.GroupBy(rows, new[] { 0 }, 2, funcs);
            var appUnique = CsvAnalytics.GroupBy(rows, new[] { 0 }, 1, new[] { AggregationFunction.UniqueCount });
            Assert.Equal(app.Rows.Count, res.Rows.Count);
            foreach (var row in res.Rows)
            {
                var a = app.Rows.Single(x => x.Key[0] == row[0]);
                double D(int i) => double.Parse(row[i]!, CultureInfo.InvariantCulture);
                Assert.Equal(a.Values[AggregationFunction.Count], D(1));
                Assert.Equal(a.Values[AggregationFunction.Sum], D(2), 6);
                Assert.Equal(a.Values[AggregationFunction.Mean], D(3), 6);
                Assert.Equal(a.Values[AggregationFunction.Median], D(4), 6);
                Assert.Equal(a.Values[AggregationFunction.Min], D(5), 6);
                Assert.Equal(a.Values[AggregationFunction.Max], D(6), 6);
                Assert.Equal(appUnique.Rows.Single(x => x.Key[0] == row[0]).Values[AggregationFunction.UniqueCount], D(7));
            }
        }

        [Fact]
        public async Task Group_by_two_columns_without_aggregates_and_overall_aggregate()
        {
            var t = Table("g2", "a,b,n\nx,1,5\nx,1,6\nx,2,7\ny,1,8\n,1,9\n");
            var rows = Cells(await Run(WizardSql.GroupSql(new GroupSpec(t, new[] { "a", "b" }, new[] { new GroupAggregate(GroupFunction.Count, "n") }))));
            Assert.Equal(5 - 1, rows.Length);   // (x,1) (x,2) (y,1) (NULL,1)
            Assert.Equal("2", rows.Single(r => r[0] == "x" && r[1] == "1")[2]);
            Assert.Equal("1", rows.Single(r => r[0] == "<null>")[2]);
            var all = Cells(await Run(WizardSql.GroupSql(new GroupSpec(t, Array.Empty<string>(),
                new[] { new GroupAggregate(GroupFunction.Sum, "n", "합계") }))));
            Assert.Equal(new[] { new[] { "35" } }, all);
        }

        [Fact]
        public void Group_rejects_non_numeric_sum_and_unknown_columns()
        {
            var t = Table("g3", "a,n\nx,1\n");
            Assert.Throws<ArgumentException>(() => WizardSql.GroupSql(new GroupSpec(t, new[] { "n" }, new[] { new GroupAggregate(GroupFunction.Sum, "a") })));
            Assert.Throws<ArgumentException>(() => WizardSql.GroupSql(new GroupSpec(t, new[] { "zzz" }, Array.Empty<GroupAggregate>())));
            Assert.Throws<ArgumentException>(() => WizardSql.GroupSql(new GroupSpec(t, new[] { "a" }, new[] { new GroupAggregate(GroupFunction.Max, null) })));
            Assert.Throws<ArgumentException>(() => WizardSql.GroupSql(new GroupSpec(t, Array.Empty<string>(), Array.Empty<GroupAggregate>())));
        }

        [Fact]
        public async Task Group_aliases_stay_unique_and_quote_odd_names()
        {
            var t = Table("g4", "구분 x,\"va\"\"l\"\nA,1\nA,2\n");
            var res = await Run(WizardSql.GroupSql(new GroupSpec(t, new[] { "구분 x" }, new[]
            {
                new GroupAggregate(GroupFunction.Sum, "va\"l"), new GroupAggregate(GroupFunction.Sum, "va\"l"),
            })));
            Assert.Equal(new[] { "구분 x", "sum_va\"l", "sum_va\"l_2" }, res.Columns.Select(c => c.Name));
            Assert.Equal(new[] { new[] { "A", "3", "3" } }, Cells(res));
        }

        // ---- 조인 마법사 보조 ------------------------------------------------------------------------

        [Fact]
        public void Suggest_keys_prefers_id_like_same_name_columns_and_ignores_case_and_punctuation()
        {
            var a = Table("sk_a", "customer_id,name,city\n1,a,x\n");
            var b = Table("sk_b", "Customer_ID,name,amount\n1,a,5\n");
            Assert.Equal(new[] { new JoinKey("customer_id", "Customer_ID") }, WizardSql.SuggestKeys(a, b));

            var c = Table("sk_c", "x,note\n1,a\n");
            var d = Table("sk_d", "y,note\n1,b\n");
            Assert.Equal(new[] { new JoinKey("note", "note") }, WizardSql.SuggestKeys(c, d));

            var e = Table("sk_e", "고객번호,이름\n1,a\n");
            var f = Table("sk_f", "\"고객 번호\",이름\n1,b\n");
            Assert.Equal(new[] { new JoinKey("고객번호", "고객 번호") }, WizardSql.SuggestKeys(e, f));

            var g = Table("sk_g", "p\n1\n");
            var h = Table("sk_h", "q\n1\n");
            Assert.Empty(WizardSql.SuggestKeys(g, h));
        }

        [Fact]
        public async Task Join_select_sql_applies_prefixes_aliases_and_column_choice()
        {
            var a = Table("jc_a", "id,name,city\n1,Kim,Seoul\n2,Lee,Busan\n");
            var b = Table("jc_b", "id,name,total\n1,Kim2,100\n3,Park2,300\n");
            var spec = new JoinSpec(a, b, new[] { new JoinKey("id", "id") }, JoinKind.Left);

            var plain = WizardSql.DefaultJoinColumns(a, b);
            Assert.Equal(new[] { "id", "name", "city", "id_right", "name_right", "total" }, plain.Select(c => c.OutputName));

            var prefixed = WizardSql.DefaultJoinColumns(a, b, "c_", "o_", true);
            Assert.Equal(new[] { "c_id", "c_name", "city", "o_id", "o_name", "total" }, prefixed.Select(c => c.OutputName));

            var chosen = new[] { prefixed[0], prefixed[1], prefixed[5] };
            var r = await Run(WizardSql.JoinSelectSql(spec, chosen));
            Assert.Equal(new[] { "c_id", "c_name", "total" }, r.Columns.Select(c => c.Name));
            Assert.Equal(new[] { new[] { "1", "Kim", "100" }, new[] { "2", "Lee", "<null>" } }, Cells(r).OrderBy(x => x[0]).ToArray());

            Assert.Throws<ArgumentException>(() => WizardSql.JoinSelectSql(spec, Array.Empty<WizardSql.JoinOutputColumn>()));
            Assert.Throws<ArgumentException>(() => WizardSql.JoinSelectSql(spec, new[]
            {
                new WizardSql.JoinOutputColumn(true, "id", "x"), new WizardSql.JoinOutputColumn(false, "id", "X"),
            }));
        }

        [Fact]
        public async Task Join_diagnostics_expected_rows_equal_the_real_row_count_for_every_join_kind()
        {
            var a = Table("jd_a", "k,v\n1,a\n1,b\n2,c\n3,d\n,e\n");
            var b = Table("jd_b", "k,w\n1,x\n1,y\n2,z\n4,q\n,r\n");
            foreach (JoinKind kind in Enum.GetValues<JoinKind>())
            {
                var spec = new JoinSpec(a, b, new[] { new JoinKey("k", "k") }, kind);
                var diag = await _ws.CheckJoinAsync(spec);
                var cols = WizardSql.DefaultJoinColumns(a, b);
                long actual = long.Parse(Cells(await Run($"SELECT count(*) FROM (\n{WizardSql.JoinSelectSql(spec, cols)}\n) AS q"))[0][0]);
                Assert.Equal(diag.ExpectedRows, actual);
            }
        }

        [Fact]
        public async Task Join_growth_warning_levels_follow_the_row_multiplication()
        {
            // 다대다: 3 × 3 = 9행이 나오는데 입력은 3행씩 → ×3 (강한 경고)
            var a = Table("gr_a", "k,v\nx,1\nx,2\nx,3\n");
            var b = Table("gr_b", "k,w\nx,1\nx,2\nx,3\n");
            var spec = new JoinSpec(a, b, new[] { new JoinKey("k", "k") }, JoinKind.Inner);
            var d = await _ws.CheckJoinAsync(spec);
            Assert.Equal(3.0, JoinWizardDialog.GrowthFactor(d), 6);
            var lines = JoinWizardDialog.Describe(d, spec);
            Assert.Contains(lines, l => l.Level == DiagLevel.Strong && l.Text.Contains('9'));

            // 1.0 < 배율 ≤ 1.5 → 빨간(Bad) 경고: 왼쪽 4행(키 x ×2, y ×2)과 오른쪽 2행(x, y) 내부 조인 = 4행, 기준 max=4 → 1.0 (경고 없음)
            var c = Table("gr_c", "k,v\nx,1\nx,2\ny,3\ny,4\n");
            var e = Table("gr_e", "k,w\nx,1\ny,2\n");
            var d2 = await _ws.CheckJoinAsync(new JoinSpec(c, e, new[] { new JoinKey("k", "k") }, JoinKind.Inner));
            Assert.Equal(1.0, JoinWizardDialog.GrowthFactor(d2), 6);
            Assert.DoesNotContain(JoinWizardDialog.Describe(d2, new JoinSpec(c, e, new[] { new JoinKey("k", "k") })), l => l.Level is DiagLevel.Bad or DiagLevel.Strong);

            // 서로 안 겹치는 전체 조인은 L+R행이 나오지만 행이 불어난 것이 아니다.
            var f = Table("gr_f", "k\n1\n2\n");
            var g = Table("gr_g", "k\n3\n4\n5\n");
            var fullSpec = new JoinSpec(f, g, new[] { new JoinKey("k", "k") }, JoinKind.Full);
            var d3 = await _ws.CheckJoinAsync(fullSpec);
            Assert.Equal(5, d3.ExpectedRows);
            Assert.Equal(1.0, JoinWizardDialog.GrowthFactor(d3), 6);
            Assert.Contains(JoinWizardDialog.Describe(d3, fullSpec), l => l.Level == DiagLevel.Strong && l.Text.Length > 0); // 일치하는 키가 없다는 경고

            // 1.0 < 배율 ≤ 1.5: 왼쪽 키 x 2행 × 오른쪽 x 2행 = 4행, 나머지 키는 1:1 → 입력 각 5행, 결과 8행?
            var h = Table("gr_h", "k\nx\nx\ny\nz\nw\n");
            var i2 = Table("gr_i", "k\nx\nx\ny\nz\nw\n");
            var d4 = await _ws.CheckJoinAsync(new JoinSpec(h, i2, new[] { new JoinKey("k", "k") }, JoinKind.Inner));
            Assert.Equal(7, d4.ExpectedRows);
            Assert.Equal(1.4, JoinWizardDialog.GrowthFactor(d4), 6);
            var l4 = JoinWizardDialog.Describe(d4, new JoinSpec(h, i2, new[] { new JoinKey("k", "k") }));
            Assert.Contains(l4, l => l.Level == DiagLevel.Bad);
            Assert.DoesNotContain(l4, l => l.Level == DiagLevel.Strong);
        }

        [Fact]
        public void Compare_description_flags_duplicate_keys_and_lists_changed_cells_per_column()
        {
            var l = Table("cd_a", "id,name,score\n1,a,1\n");
            var r = Table("cd_b", "id,name,score\n1,b,2\n");
            var spec = new CompareSpec(l, r, new[] { new JoinKey("id", "id") });
            var v = new Dictionary<string, long>
            {
                ["left_rows"] = 10, ["right_rows"] = 9, ["added"] = 1, ["removed"] = 2, ["changed"] = 3, ["unchanged"] = 3,
                ["duplicate_keys"] = 1, ["duplicate_left_rows"] = 2, ["duplicate_right_rows"] = 1, ["null_key_left_rows"] = 0, ["null_key_right_rows"] = 1,
                ["changed_cells_0"] = 3, ["changed_cells_1"] = 1,
            };
            var lines = CompareWizardDialog.Describe(spec, WizardSql.ResolveCompareColumns(spec), v, Array.Empty<string>());
            Assert.Contains(lines, x => x.Level == DiagLevel.Bad && x.Text.Contains("duplicate_key"));
            Assert.Contains(lines, x => x.Level == DiagLevel.Warn && x.Text.Contains("NULL"));
            Assert.Contains(lines, x => x.Text.Contains("name 3") && x.Text.Contains("score 1"));
        }
    }
}
