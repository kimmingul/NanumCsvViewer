using System.Text;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 구조 편집(행·컬럼), 조건부 서식, 셀 주소 이동, Python 분석 보조(export_view, show_markdown, show_image) 도구.
    // 가짜 호스트(CsvHostToolsTests.FakeHost)와 임시 폴더로 승인·정책·파일 내용·경로 안전을 검증한다.
    public sealed partial class CsvHostToolsTests
    {
        private string OutputFolder => Path.Combine(_dir, "people_분석결과");

        private FakeHost HostInDir() => new() { Directory = _dir };

        // ------------------------------------------------------------------ 행 삽입

        [Fact]
        public async Task InsertRows_AsksFirst_AppliesOneTaggedStep_AndUndoRestores()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);

            var r = await Call(tools, "csv.insert_rows", """{"before_row":3,"count":2}""", approvals);

            Assert.False(r.IsError, r.Text);
            var card = Assert.Single(approvals.Calls);
            Assert.Contains(card.Lines, l => l.StartsWith("+ ") && l.Contains("3–4"));
            Assert.Equal(7, host.Rows.Count);
            Assert.All(host.Rows[2], v => Assert.Equal("", v));
            Assert.Equal("3", host.Rows[4][0]); // 원래 3번 행은 5번으로 밀렸다
            Assert.StartsWith(AgentEditTag.Prefix, host.Steps[^1].Description);
            var p = Payload(r);
            Assert.Equal(3, (int)p["first_new_row"]!);
            Assert.Equal(7, (int)p["total_rows"]!);
            Assert.Equal(1, (int)p["undo_steps_added"]!);

            var undo = await Call(tools, "csv.undo", "{}");
            Assert.False(undo.IsError, undo.Text);
            Assert.Equal(5, host.Rows.Count);
            Assert.Equal("3", host.Rows[2][0]);
        }

        [Fact]
        public async Task InsertRows_DefaultAppendsOneRow_BeyondEndAndDenialChangeNothing()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            var ok = await Call(tools, "csv.insert_rows", "{}");
            Assert.False(ok.IsError);
            Assert.Contains("insert:6:1", host.Calls);

            var beyond = new FakeApprovals(true);
            var bad = await Call(tools, "csv.insert_rows", """{"before_row":99}""", beyond);
            Assert.True(bad.IsError);
            Assert.Contains("beyond the end", bad.Text);
            Assert.Empty(beyond.Calls);

            int steps = host.Steps.Count;
            var denied = await Call(tools, "csv.insert_rows", """{"before_row":1}""", new FakeApprovals(false));
            Assert.True(denied.IsError);
            Assert.Contains("did not approve", denied.Text);
            Assert.Equal(steps, host.Steps.Count);
            Assert.Equal(6, host.Rows.Count);

            Assert.True((await Call(tools, "csv.insert_rows", """{"count":0}""")).IsError);
            Assert.True((await Call(tools, "csv.insert_rows", """{"count":10001}""")).IsError);
        }

        // ------------------------------------------------------------------ 행 삭제

        [Fact]
        public async Task DeleteRows_ListedRows_CardHasRangesAndPreview_ResultHasNoValues()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);

            var r = await Call(tools, "csv.delete_rows", """{"rows":[3,4,5,3]}""", approvals);

            Assert.False(r.IsError, r.Text);
            var card = Assert.Single(approvals.Calls);
            Assert.Contains(card.Lines, l => l.StartsWith("- ") && l.Contains("3–5"));
            Assert.Contains(card.Lines, l => l.Contains("SECRET_TOWN")); // 사용자에게 보이는 카드에는 값이 있다
            Assert.DoesNotContain("SECRET_TOWN", r.Text);                  // 모델에게 가는 결과에는 없다
            Assert.Equal(new[] { "1", "2" }, host.Rows.Select(x => x[0]));
            var p = Payload(r);
            Assert.Equal(3, (int)p["deleted_rows"]!);
            Assert.Equal(2, (int)p["total_rows"]!);
            Assert.Equal(3, (int)p["edits"]!["deleted_rows"]!);
            Assert.StartsWith(AgentEditTag.Prefix, host.Steps[^1].Description);

            await Call(tools, "csv.undo", "{}");
            Assert.Equal(5, host.Rows.Count);
        }

        [Fact]
        public async Task DeleteRows_RangeAndInView_UseTheRightRowNumbers()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            await Call(tools, "csv.delete_rows", """{"from":2,"to":3}""");
            Assert.Contains("delete:2,3", host.Calls);
            await Call(tools, "csv.undo", "{}");

            await Call(tools, "csv.set_filter", """{"expression":"age > 40"}""");
            var approvals = new FakeApprovals(true);
            var r = await Call(tools, "csv.delete_rows", """{"in_view":true}""", approvals);
            Assert.False(r.IsError, r.Text);
            Assert.Contains("delete:3,4", host.Calls);
            Assert.Contains(approvals.Calls[0].Lines, l => l.Contains("age > 40"));
            Assert.Equal(3, host.Rows.Count);
        }

        [Fact]
        public async Task DeleteRows_BadSelectors_OutOfRange_AndEverythingAreRefusedBeforeAsking()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);
            foreach (string args in new[]
            {
                "{}",
                """{"rows":[1],"in_view":true}""",
                """{"rows":[1],"from":2}""",
                """{"to":3}""",
                """{"from":4,"to":2}""",
                """{"rows":[9]}""",
                """{"from":1,"to":6}""",
                """{"from":1,"to":5}""",   // 모든 행
                """{"in_view":true}""",   // 필터 없이 = 모든 행
            })
            {
                var r = await Call(tools, "csv.delete_rows", args, approvals);
                Assert.True(r.IsError, args);
            }
            Assert.Empty(approvals.Calls);
            Assert.Equal(5, host.Rows.Count);
            Assert.Empty(host.Steps);

            var denied = await Call(tools, "csv.delete_rows", """{"rows":[1]}""", new FakeApprovals(false));
            Assert.True(denied.IsError);
            Assert.Equal(5, host.Rows.Count);
        }

        [Fact]
        public void RowRanges_CollapsesRunsAndCountsTheRest()
        {
            Assert.Equal("1, 3–5, 9", CsvHostTools.RowRanges(new long[] { 1, 3, 4, 5, 9 }, 12));
            Assert.Equal("1, 3, … (+2)", CsvHostTools.RowRanges(new long[] { 1, 3, 5, 7 }, 2));
        }

        // ------------------------------------------------------------------ 컬럼 추가·삭제

        [Fact]
        public async Task AddColumn_CardResultAndUndo_RejectsDuplicatesAndEmptyNames()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);

            var r = await Call(tools, "csv.add_column", """{"name":"flag","fill":"N"}""", approvals);
            Assert.False(r.IsError, r.Text);
            Assert.Contains(approvals.Calls[0].Lines, l => l.StartsWith("+ ") && l.Contains("flag"));
            Assert.Equal("flag", host.Headers[^1]);
            Assert.All(host.Rows, row => Assert.Equal("N", row[^1]));
            var p = Payload(r);
            Assert.Equal("flag", (string?)p["column"]);
            Assert.Equal(1, (int)p["edits"]!["added_columns"]!);
            Assert.StartsWith(AgentEditTag.Prefix, host.Steps[^1].Description);

            await Call(tools, "csv.undo", "{}");
            Assert.Equal(5, host.Headers.Length);

            var more = new FakeApprovals(true);
            var dup = await Call(tools, "csv.add_column", """{"name":"CITY"}""", more);
            Assert.True(dup.IsError);
            Assert.Contains("already exists", dup.Text);
            Assert.True((await Call(tools, "csv.add_column", """{"name":"  "}""", more)).IsError);
            Assert.Empty(more.Calls);

            var denied = await Call(tools, "csv.add_column", """{"name":"x"}""", new FakeApprovals(false));
            Assert.True(denied.IsError);
            Assert.Equal(5, host.Headers.Length);
        }

        [Fact]
        public async Task DeleteColumn_ByName_ShiftsLaterColumns_UndoRestores_LastColumnRefused()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(true);
            var tools = Tools(host);

            var r = await Call(tools, "csv.delete_column", """{"column":"city"}""", approvals);
            Assert.False(r.IsError, r.Text);
            Assert.Contains("delcol:2", host.Calls);
            Assert.Equal(new[] { "id", "age", "group", "score" }, host.Headers);
            Assert.Contains(approvals.Calls[0].Lines, l => l.StartsWith("- ") && l.Contains("city"));
            Assert.Equal(1, (int)Payload(r)["edits"]!["deleted_columns"]!);

            await Call(tools, "csv.undo", "{}");
            Assert.Equal(5, host.Headers.Length);

            var unknown = await Call(tools, "csv.delete_column", """{"column":"nope"}""", approvals);
            Assert.True(unknown.IsError);

            var single = new FakeHost { Headers = new[] { "a" }, Types = new[] { ColumnValueType.Integer }, Rows = new() { new[] { "1" } } };
            single.View = new List<int> { 0 };
            var refused = await Call(Tools(single), "csv.delete_column", """{"column":"a"}""");
            Assert.True(refused.IsError);
            Assert.Contains("only column", refused.Text);
        }

        [Fact]
        public async Task SaveEdits_CountsColumnStructureEditsAsEdits()
        {
            var host = new FakeHost { Directory = _dir };
            var tools = Tools(host);
            Assert.True((await Call(tools, "csv.save_edits_as", """{"path":"out.csv"}""")).IsError); // 편집 없음

            await Call(tools, "csv.add_column", """{"name":"flag"}""");
            var approvals = new FakeApprovals(true);
            var r = await Call(tools, "csv.save_edits_as", """{"path":"out.csv"}""", approvals);
            Assert.False(r.IsError, r.Text);
            Assert.Contains(approvals.Calls[0].Lines, l => l.Contains("added column"));
        }

        // ------------------------------------------------------------------ csv.goto 셀 주소

        [Fact]
        public async Task Goto_CellAddress_ParsesRowColumnAndNames_AndRejectsMixing()
        {
            var host = new FakeHost();
            var tools = Tools(host);

            Assert.False((await Call(tools, "csv.goto", """{"cell":"R3C2"}""")).IsError);
            Assert.Contains("goto:3:1", host.Calls);
            Assert.False((await Call(tools, "csv.goto", """{"cell":"city:2"}""")).IsError);
            Assert.Contains("goto:2:2", host.Calls);
            Assert.False((await Call(tools, "csv.goto", """{"cell":"C3"}""")).IsError);
            Assert.Contains("goto::2", host.Calls);
            Assert.False((await Call(tools, "csv.goto", """{"cell":"4"}""")).IsError);
            Assert.Contains("goto:4:", host.Calls);

            int before = host.Calls.Count;
            var mixed = await Call(tools, "csv.goto", """{"cell":"R3C2","row":2}""");
            Assert.True(mixed.IsError);
            var bad = await Call(tools, "csv.goto", """{"cell":"!!"}""");
            Assert.True(bad.IsError);
            Assert.Contains("Invalid cell address", bad.Text);
            var unknown = await Call(tools, "csv.goto", """{"cell":"nope:3"}""");
            Assert.True(unknown.IsError);
            Assert.Equal(before, host.Calls.Count);
        }

        // ------------------------------------------------------------------ 조건부 서식

        [Fact]
        public async Task FormatAdd_NeedsNoApproval_ReturnsIdAndMatchedRowsOfTheCurrentView()
        {
            var host = new FakeHost();
            var approvals = new FakeApprovals(false); // 물어보면 안 된다
            var tools = Tools(host);

            var r = await Call(tools, "csv.format_add", """{"expression":"age > 40","back_color":"#FFE0E0"}""", approvals);
            Assert.False(r.IsError, r.Text);
            Assert.Empty(approvals.Calls);
            var p = Payload(r);
            Assert.Equal("cf1", (string?)p["id"]);
            Assert.Equal(2, (int)p["rows_matched_in_view"]!);
            Assert.Equal(5, (int)p["rows_scanned"]!);
            var rule = Assert.Single(host.Rules);
            Assert.Equal(ConditionalFormatKind.Expression, rule.Kind);
            Assert.Equal(ConditionalFormatTarget.Row, rule.Target);
            Assert.Equal("#FFE0E0", rule.BackColor);
            Assert.Null(rule.Column);

            await Call(tools, "csv.set_filter", """{"expression":"age > 30"}""");
            var narrowed = await Call(tools, "csv.format_add", """{"expression":"city = \"Seoul\"","fore_color":"red","bold":true}""");
            var q = Payload(narrowed);
            Assert.Equal(1, (int)q["rows_matched_in_view"]!);
            Assert.Equal(4, (int)q["rows_scanned"]!);
        }

        [Fact]
        public async Task FormatAdd_CellTargetAndColorScale_ForwardTheirFields()
        {
            var host = new FakeHost();
            var tools = Tools(host);

            var cell = await Call(tools, "csv.format_add", """{"expression":"score > 3","target":"cell","column":"SCORE","fore_color":"red","bold":true,"name":"high"}""");
            Assert.False(cell.IsError, cell.Text);
            var c = host.Rules[0];
            Assert.Equal(ConditionalFormatTarget.Cell, c.Target);
            Assert.Equal("score", c.Column); // 정식 컬럼 이름으로
            Assert.True(c.Bold);
            Assert.Equal("high", c.Name);

            var scale = await Call(tools, "csv.format_add", """{"kind":"color_scale","column":"score"}""");
            Assert.False(scale.IsError, scale.Text);
            var s = host.Rules[1];
            Assert.Equal(ConditionalFormatKind.ColorScale, s.Kind);
            Assert.Equal("score", s.Column);
            Assert.NotNull(s.ScaleMinColor);
            Assert.NotNull(s.ScaleMaxColor);
            Assert.Null(s.ScaleMidColor);
            Assert.Equal(4, (int)Payload(scale)["rows_matched_in_view"]!); // 빈 score 1개 제외
        }

        [Fact]
        public async Task FormatAdd_InvalidCombinations_AreErrorsAndAddNoRule()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            foreach (string args in new[]
            {
                """{"expression":"age > 40"}""",                                              // 스타일 없음
                """{"back_color":"red"}""",                                                   // 식 없음
                """{"expression":"age > 40","back_color":"red!"}""",                          // 색 문법
                """{"expression":"age > 40","back_color":"red","target":"cell"}""",            // 셀인데 컬럼 없음
                """{"expression":"age > 40","back_color":"red","column":"age"}""",             // 행인데 컬럼
                """{"expression":"age >","back_color":"red"}""",                              // 식 오류
                """{"expression":"nope > 1","back_color":"red"}""",                           // 모르는 컬럼
                """{"expression":"age > 1","back_color":"red","scale_min_color":"blue"}""",   // 눈금 색은 color_scale 전용
                """{"kind":"color_scale"}""",                                                  // 컬럼 없음
                """{"kind":"color_scale","column":"city"}""",                                  // 숫자 아님
                """{"kind":"color_scale","column":"score","expression":"age > 1"}""",          // 식 불가
                """{"kind":"color_scale","column":"score","bold":true}""",
            })
            {
                var r = await Call(tools, "csv.format_add", args);
                Assert.True(r.IsError, args);
            }
            Assert.Empty(host.Rules);
        }

        [Fact]
        public async Task FormatAdd_CountFailure_StillAddsTheRuleAndSaysSo()
        {
            var host = new FakeHost { FailCount = true };
            var r = await Call(Tools(host), "csv.format_add", """{"expression":"age > 40","bold":true}""");
            Assert.False(r.IsError, r.Text);
            Assert.Single(host.Rules);
            Assert.NotNull(Payload(r)["count_unavailable"]);
        }

        [Fact]
        public async Task FormatListRemoveClear_RoundTrip()
        {
            var host = new FakeHost();
            var tools = Tools(host);
            await Call(tools, "csv.format_add", """{"expression":"age > 40","back_color":"gold"}""");
            await Call(tools, "csv.format_add", """{"kind":"color_scale","column":"score","scale_mid_color":"#FFFFFF"}""");

            var list = Payload(await Call(tools, "csv.format_list", "{}"))["rules"]!.AsArray();
            Assert.Equal(2, list.Count);
            Assert.Equal("cf1", (string?)list[0]!["id"]);
            Assert.Equal("age > 40", (string?)list[0]!["expression"]);
            Assert.Equal("color_scale", (string?)list[1]!["kind"]);
            Assert.Equal("#FFFFFF", (string?)list[1]!["scale_mid_color"]);
            Assert.Null(list[0]!["problem"]);

            host.RuleProblems["cf2"] = "Unknown column 'score'.";
            var withProblem = Payload(await Call(tools, "csv.format_list", "{}"))["rules"]!.AsArray();
            Assert.Contains("not applied", (string?)withProblem[1]!["problem"]);
            host.RuleProblems.Clear();

            Assert.False((await Call(tools, "csv.format_remove", """{"id":"cf1"}""")).IsError);
            var missing = await Call(tools, "csv.format_remove", """{"id":"cf1"}""");
            Assert.True(missing.IsError);
            Assert.Contains("cf2", missing.Text); // 남은 id를 알려 준다

            var cleared = await Call(tools, "csv.format_clear", "{}");
            Assert.Equal(1, (int)Payload(cleared)["removed"]!);
            Assert.Empty(host.Rules);
        }

        [Fact]
        public async Task FormatTools_NeedAnOpenFile()
        {
            var host = new FakeHost { NoDoc = true };
            var tools = Tools(host);
            foreach (string tool in new[] { "csv.format_list", "csv.format_clear" })
                Assert.True((await Call(tools, tool, "{}")).IsError);
            Assert.True((await Call(tools, "csv.format_remove", """{"id":"cf1"}""")).IsError);
            Assert.True((await Call(tools, "csv.format_add", """{"expression":"age > 1","bold":true}""")).IsError);
        }

        // ------------------------------------------------------------------ csv.export_view

        [Fact]
        public async Task ExportView_RefusedWhenLocalPythonIsOff_WithGuidanceAndNoFiles()
        {
            var host = HostInDir();
            var r = await Call(Tools(host, python: false), "csv.export_view", "{}");
            Assert.True(r.IsError);
            Assert.Contains("Allow local Python analysis", r.Text);
            Assert.False(Directory.Exists(OutputFolder));
        }

        [Fact]
        public async Task ExportView_WritesTheViewWithEditsAndSchema_ButReturnsNoCellValues()
        {
            var host = HostInDir();
            var tools = Tools(host, python: true);
            await Call(tools, "csv.set_filter", """{"expression":"age > 30"}""");
            await Call(tools, "csv.edit_cells", "{\"edits\":[{\"row\":4,\"column\":\"city\",\"value\":\"a,b \\\"q\\\"\\nline\"}]}");

            var r = await Call(tools, "csv.export_view", "{}");
            Assert.False(r.IsError, r.Text);
            string csvPath = Path.Combine(OutputFolder, "data", "people_view.csv");
            string schemaPath = Path.Combine(OutputFolder, "data", "people_view.schema.json");
            Assert.True(File.Exists(csvPath));
            Assert.True(File.Exists(schemaPath));
            Assert.False(File.Exists(csvPath + ".tmp"));

            byte[] raw = File.ReadAllBytes(csvPath);
            Assert.NotEqual(0xEF, raw[0]); // BOM 없음
            string csv = Encoding.UTF8.GetString(raw);
            Assert.StartsWith("id,age,city,group,score\n", csv);
            Assert.Contains("4,52,\"a,b \"\"q\"\"\nline\",B,4.5\n", csv);   // 편집 덮개 적용 + RFC 4180 인용
            Assert.Contains("3,47,SECRET_TOWN,A,3.5\n", csv);
            Assert.DoesNotContain("1,25,Seoul", csv);                        // 필터에 걸린 행 제외

            var p = Payload(r);
            Assert.Equal(csvPath, (string?)p["csv_path"]);
            Assert.Equal(schemaPath, (string?)p["schema_path"]);
            Assert.Equal(4, (int)p["rows"]!);
            Assert.Equal(5, (int)p["columns"]!);
            Assert.False((bool)p["replaced_existing"]!);
            Assert.DoesNotContain("SECRET_TOWN", r.Text);
            Assert.DoesNotContain("Seoul", r.Text);
            Assert.Contains("aggregates only", (string?)p["data_policy_note"]);

            var schema = JsonNode.Parse(File.ReadAllText(schemaPath))!;
            Assert.Equal("people.csv", (string?)schema["source_file"]);
            Assert.Equal("people_view.csv", (string?)schema["csv_file"]);
            Assert.Equal(4, (int)schema["row_count"]!);
            Assert.Equal(5, (int)schema["source_total_rows"]!);
            Assert.Equal(5, (int)schema["column_count"]!);
            Assert.Equal("age", (string?)schema["columns"]![1]!["name"]);
            Assert.Equal("Integer", (string?)schema["columns"]![1]!["type"]);
            Assert.True((bool)schema["columns"]![1]!["numeric"]!);
            Assert.Equal("expression", (string?)schema["filters"]![0]!["kind"]);
            Assert.Equal("age > 30", (string?)schema["filters"]![0]!["text"]);
            Assert.True((bool)schema["unsaved_edits"]!);

            var again = await Call(tools, "csv.export_view", "{}");
            Assert.True((bool)Payload(again)["replaced_existing"]!);
        }

        [Fact]
        public async Task ExportView_AppliesAddedAndDeletedColumns_AndColumnSubsets()
        {
            var host = HostInDir();
            var tools = Tools(host, python: true);
            await Call(tools, "csv.add_column", """{"name":"flag","fill":"N"}""");
            await Call(tools, "csv.delete_column", """{"column":"score"}""");

            var r = await Call(tools, "csv.export_view", """{"name":"all"}""");
            Assert.False(r.IsError, r.Text);
            string csv = File.ReadAllText(Path.Combine(OutputFolder, "data", "all.csv"));
            Assert.StartsWith("id,age,city,group,flag\n", csv);
            Assert.Contains("1,25,Seoul,A,N\n", csv);

            var sub = await Call(tools, "csv.export_view", """{"name":"sub","columns":["flag","id"]}""");
            Assert.False(sub.IsError, sub.Text);
            Assert.StartsWith("flag,id\nN,1\n", File.ReadAllText(Path.Combine(OutputFolder, "data", "sub.csv")));
            Assert.Equal(2, (int)Payload(sub)["columns"]!);

            Assert.True((await Call(tools, "csv.export_view", """{"columns":["score"]}""")).IsError); // 삭제된 컬럼
        }

        [Fact]
        public async Task ExportView_FilterTextWithCellValues_IsHiddenUnderSummaryOnly()
        {
            string Schema() => File.ReadAllText(Path.Combine(OutputFolder, "data", "people_view.schema.json"));
            var host = HostInDir();
            host.ExtraFilters.Add(new AgentFilterInfo(AgentFilterKind.CellValue, "city = SECRET_TOWN", "city"));

            await Call(Tools(host, AgentDataPolicy.SummaryOnly, python: true), "csv.export_view", "{}");
            Assert.DoesNotContain("SECRET_TOWN", Schema());
            Assert.Contains("cell_value", Schema());

            var allowed = await Call(Tools(host, AgentDataPolicy.RowsAllowed, python: true), "csv.export_view", "{}");
            Assert.Contains("SECRET_TOWN", Schema());
            Assert.DoesNotContain("aggregates only", (string?)Payload(allowed)["data_policy_note"]);
        }

        [Fact]
        public async Task ExportView_NameIsSanitized_AndStaysInsideTheDataFolder()
        {
            var host = HostInDir();
            var r = await Call(Tools(host, python: true), "csv.export_view", """{"name":"..\\..\\evil/x:y"}""");
            Assert.False(r.IsError, r.Text);
            string csvPath = (string)Payload(r)["csv_path"]!;
            Assert.Equal(Path.Combine(OutputFolder, "data"), Path.GetDirectoryName(csvPath));
            Assert.True(File.Exists(csvPath));
            Assert.False(File.Exists(Path.Combine(_dir, "evil")));

            Assert.Equal("a_b_c__.csv", CsvHostTools.SafeBaseName("a/b:c*?.csv", "x"));
            Assert.Equal("x", CsvHostTools.SafeBaseName("..", "x"));
            Assert.Equal("fb", CsvHostTools.SafeBaseName(null, "fb"));
            Assert.Equal("_CON", CsvHostTools.SafeBaseName("CON", "f"));
            Assert.Equal("분석 결과_1", CsvHostTools.SafeBaseName("분석 결과_1", "f"));
        }

        [Fact]
        public async Task ExportView_EmptyViewHugeViewAndNoDocument_AreErrors()
        {
            var empty = HostInDir();
            empty.View = new List<int>();
            Assert.True((await Call(Tools(empty, python: true), "csv.export_view", "{}")).IsError);

            var huge = HostInDir();
            huge.View = Enumerable.Repeat(0, (int)CsvHostTools.MaxExportRows + 1).ToList();
            var r = await Call(Tools(huge, python: true), "csv.export_view", "{}");
            Assert.True(r.IsError);
            Assert.Contains("limited", r.Text);
            Assert.False(Directory.Exists(Path.Combine(OutputFolder, "data")) && Directory.GetFiles(Path.Combine(OutputFolder, "data")).Length > 0);

            var none = new FakeHost { NoDoc = true };
            Assert.True((await Call(Tools(none, python: true), "csv.export_view", "{}")).IsError);
            Assert.Equal(0, Directory.Exists(Path.Combine(OutputFolder, "data")) ? Directory.GetFiles(Path.Combine(OutputFolder, "data")).Length : 0);
        }

        // ------------------------------------------------------------------ csv.show_markdown / csv.show_image

        private string WriteInOutput(string relative, string content = "x")
        {
            string full = Path.Combine(OutputFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return full;
        }

        [Fact]
        public async Task ShowMarkdown_OpensFilesInsideTheOutputFolderOrTheDataFolder()
        {
            string report = WriteInOutput("report.md", "# hi");
            string beside = Path.Combine(_dir, "notes.markdown");
            File.WriteAllText(beside, "# n");
            var host = HostInDir();
            var tools = Tools(host); // AllowLocalPython와 무관: 보기 전용

            var rel = await Call(tools, "csv.show_markdown", """{"path":"report.md"}""");
            Assert.False(rel.IsError, rel.Text);
            Assert.Equal(report, Assert.Single(host.ShownMarkdown));

            var abs = await Call(tools, "csv.show_markdown", new JsonObject { ["path"] = beside }.ToJsonString());
            Assert.False(abs.IsError, abs.Text);
            Assert.Equal(beside, host.ShownMarkdown[1]);
        }

        [Fact]
        public async Task ShowMarkdown_RefusesOutsideFoldersWrongTypesMissingFilesAndViewerFailures()
        {
            WriteInOutput("report.md");
            WriteInOutput("run.exe");
            WriteInOutput("notes.txt");
            File.WriteAllText(Path.Combine(_dir, "outside.md"), "x"); // 데이터 폴더에는 있지만 상대 경로로는 결과 폴더 밖
            string foreign = Path.Combine(Path.GetTempPath(), "csvtools-foreign-" + Guid.NewGuid().ToString("N") + ".md");
            File.WriteAllText(foreign, "x");
            try
            {
                var host = HostInDir();
                var tools = Tools(host);
                string Args(string p) => new JsonObject { ["path"] = p }.ToJsonString();

                Assert.Contains("Refused", (await Call(tools, "csv.show_markdown", Args(foreign))).Text);
                Assert.True((await Call(tools, "csv.show_markdown", Args("..\\outside.md"))).IsError);
                Assert.Contains("Unsupported", (await Call(tools, "csv.show_markdown", Args("run.exe"))).Text);
                Assert.Contains("Unsupported", (await Call(tools, "csv.show_markdown", Args("notes.txt"))).Text);
                Assert.Contains("not found", (await Call(tools, "csv.show_markdown", Args("missing.md"))).Text);
                Assert.True((await Call(tools, "csv.show_markdown", "{}")).IsError);
                Assert.Empty(host.ShownMarkdown);

                host.ViewerFailure = "viewer unavailable";
                var failed = await Call(tools, "csv.show_markdown", Args("report.md"));
                Assert.True(failed.IsError);
                Assert.Contains("viewer unavailable", failed.Text);
            }
            finally { File.Delete(foreign); }
        }

        [Fact]
        public async Task ShowImage_OpensViewerAndPostsInlinePreview_UnlessDisabledOrRefused()
        {
            string png = WriteInOutput(Path.Combine("figures", "hist.png"));
            WriteInOutput("plot.svg");
            var host = HostInDir();
            var tools = Tools(host);

            var r = await Call(tools, "csv.show_image", """{"path":"figures/hist.png","caption":"Histogram"}""");
            Assert.False(r.IsError, r.Text);
            Assert.Equal(png, Assert.Single(host.ShownImages));
            Assert.Equal((png, "Histogram"), Assert.Single(host.InlinePosts));
            Assert.True((bool)Payload(r)["inline_preview"]!);

            var quiet = await Call(tools, "csv.show_image", """{"path":"figures/hist.png","inline":false}""");
            Assert.False(quiet.IsError);
            Assert.Single(host.InlinePosts);

            host.InlineOk = false;
            var notPosted = await Call(tools, "csv.show_image", """{"path":"plot.svg"}""");
            Assert.False(notPosted.IsError);
            Assert.False((bool)Payload(notPosted)["inline_preview"]!);
            Assert.Contains("preview was not posted", notPosted.Text);

            Assert.True((await Call(tools, "csv.show_image", new JsonObject { ["path"] = "x.png", ["caption"] = new string('c', 301) }.ToJsonString())).IsError);
        }

        [Fact]
        public async Task ShowImage_PdfGoesToTheSystemViewerWithoutInlinePreview_AndExecutablesAreRefused()
        {
            string pdf = WriteInOutput("report.pdf");
            foreach (string name in new[] { "tool.exe", "x.lnk", "run.bat", "page.html", "script.ps1", "doc.md" })
                WriteInOutput(name);
            var host = HostInDir();
            var tools = Tools(host);

            var r = await Call(tools, "csv.show_image", """{"path":"report.pdf"}""");
            Assert.False(r.IsError, r.Text);
            Assert.Equal(pdf, Assert.Single(host.ShownImages));
            Assert.Empty(host.InlinePosts);
            Assert.Contains("PDF", r.Text);

            foreach (string name in new[] { "tool.exe", "x.lnk", "run.bat", "page.html", "script.ps1", "doc.md" })
            {
                var bad = await Call(tools, "csv.show_image", new JsonObject { ["path"] = name }.ToJsonString());
                Assert.True(bad.IsError, name);
                Assert.Contains("Unsupported", bad.Text);
            }
            Assert.Single(host.ShownImages);

            var outside = await Call(tools, "csv.show_image", new JsonObject { ["path"] = Path.Combine(Path.GetTempPath(), "evil.png") }.ToJsonString());
            Assert.True(outside.IsError);
            Assert.Contains("Refused", outside.Text);
        }

        // 실제 Form1 호스트: 구조 편집·조건부 서식·내보내기가 EditFormat의 실제 메서드와 한 덩어리로 동작하고 되돌려진다.
        [Fact]
        public void RealHost_StructureEdits_Format_AndExport_WorkTogetherAndUndo()
        {
            string path = Path.Combine(_dir, "people.csv");
            File.WriteAllText(path, "id,age,name\n1,30,Kim\n2,41,Lee\n3,52,Park\n4,28,Choi\n5,63,Jung\n", new UTF8Encoding(false));
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new Form1(new AppSettings());
                    _ = form.Handle;
                    const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    // 실제 열기 경로: 문서 열기 → 컬럼 만들기 → 인덱싱(행 수·컬럼 요약까지 끝날 때까지 메시지 펌프).
                    typeof(Form1).GetMethod("LoadDocument", Inst)!.Invoke(form, new object[] { path, "people" });
                    var doc = (VirtualCsvDocument)typeof(Form1).GetField("_doc", Inst)!.GetValue(form)!;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(30) &&
                           !(doc.IndexingComplete && !(bool)typeof(Form1).GetField("_indexing", Inst)!.GetValue(form)! && ((ICsvAgentHost)form).GetInfo()!.Busy == false && doc.DataRowsAvailable == 5))
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(5);
                    }
                    Assert.True(doc.IndexingComplete);
                    form.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                    var tools = Tools(form, AgentDataPolicy.SummaryOnly, python: true);
                    var approvals = new FakeApprovals(true);
                    async Task<HostToolResult> Run(string tool, string args) => await Call(tools, tool, args, approvals);
                    async Task<HostToolResult> Ok(string tool, string args) { var r = await Run(tool, args); Assert.False(r.IsError, tool + ": " + r.Text); return r; }
                    string ExportedCsv(string name) => File.ReadAllText(Path.Combine(OutputFolder, "data", name + ".csv"));

                    // 행 삽입 → 5행이 6행으로, 새 행 2번은 비어 있고 뒤 행이 밀린다. 되돌리면 원래대로.
                    var inserted = await Run("csv.insert_rows", """{"before_row":2}""");
                    Assert.False(inserted.IsError, inserted.Text);
                    Assert.Equal(6, doc.DataRowsAvailable);
                    Assert.Equal("", doc.GetDataRow(1)[0]);
                    Assert.Equal("2", doc.GetDataRow(2)[0]);
                    Assert.StartsWith(AgentEditTag.Prefix, doc.Edits.UndoDescription);
                    await Ok("csv.undo", "{}");
                    Assert.Equal(5, doc.DataRowsAvailable);
                    Assert.False(doc.Edits.CanUndo);

                    // 필터(age > 40) → in_view 삭제는 보이는 3행(2,3,5)만 지운다.
                    await Ok("csv.set_filter", """{"expression":"age > 40"}""");
                    var deleted = await Run("csv.delete_rows", """{"in_view":true}""");
                    Assert.False(deleted.IsError, deleted.Text);
                    Assert.Equal(2, doc.DataRowsAvailable);
                    Assert.Equal("1", doc.GetDataRow(0)[0]);
                    Assert.Equal("4", doc.GetDataRow(1)[0]);
                    await Ok("csv.undo", "{}");
                    Assert.Equal(5, doc.DataRowsAvailable);
                    await Ok("csv.clear_filter", "{}");

                    // 컬럼 추가 → 내보내기에 반영, 컬럼 삭제 → 나머지 컬럼 인덱스가 당겨진다. 모두 한 단계씩 되돌려진다.
                    var added = await Run("csv.add_column", """{"name":"flag","fill":"N"}""");
                    Assert.False(added.IsError, added.Text);
                    await Ok("csv.edit_cells", """{"edits":[{"row":2,"column":"flag","value":"Y"}]}""");
                    var export1 = await Run("csv.export_view", """{"name":"v1"}""");
                    Assert.False(export1.IsError, export1.Text);
                    Assert.StartsWith("id,age,name,flag\n1,30,Kim,N\n2,41,Lee,Y\n", ExportedCsv("v1"));
                    Assert.DoesNotContain("Kim", export1.Text);
                    await Ok("csv.delete_column", """{"column":"age"}""");
                    var export2 = await Run("csv.export_view", """{"name":"v2"}""");
                    Assert.False(export2.IsError, export2.Text);
                    Assert.StartsWith("id,name,flag\n1,Kim,N\n", ExportedCsv("v2"));
                    var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(OutputFolder, "data", "v2.schema.json")))!;
                    Assert.Equal(3, (int)schema["column_count"]!);
                    Assert.Equal(5, (int)schema["row_count"]!);
                    await Ok("csv.undo", "{}"); // 컬럼 삭제
                    await Ok("csv.undo", "{}"); // 셀 편집
                    await Ok("csv.undo", "{}"); // 컬럼 추가
                    Assert.False(doc.Edits.CanUndo);
                    Assert.Equal(3, doc.ColumnCount);

                    // 조건부 서식: 규칙 id와 현재 뷰의 일치 행 수. 필터를 걸면 그 뷰 기준.
                    var fmt = await Run("csv.format_add", """{"expression":"age > 40","back_color":"gold"}""");
                    Assert.False(fmt.IsError, fmt.Text);
                    var fp = Payload(fmt);
                    Assert.Equal("cf1", (string?)fp["id"]);
                    Assert.Equal(3, (int)fp["rows_matched_in_view"]!);
                    Assert.Single(((ICsvAgentHost)form).ListConditionalFormats());
                    await Ok("csv.set_filter", """{"expression":"name startswith \"J\""}""");
                    var narrowed = await Run("csv.format_add", """{"expression":"age > 40","bold":true}""");
                    Assert.False(narrowed.IsError, narrowed.Text);
                    Assert.Equal(1, (int)Payload(narrowed)["rows_matched_in_view"]!);
                    var cleared = await Run("csv.format_clear", "{}");
                    Assert.Equal(2, (int)Payload(cleared)["removed"]!);
                    Assert.Empty(((ICsvAgentHost)form).ListConditionalFormats());

                    // 원본 파일은 한 바이트도 바뀌지 않았다.
                    Assert.Equal("id,age,name\n1,30,Kim\n2,41,Lee\n3,52,Park\n4,28,Choi\n5,63,Jung\n", File.ReadAllText(path));
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { System.Windows.Forms.Application.ExitThread(); }
                    }));
                    System.Windows.Forms.Application.Run(); // 실제 메시지 루프: await 연속이 UI 스레드로 돌아온다
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
