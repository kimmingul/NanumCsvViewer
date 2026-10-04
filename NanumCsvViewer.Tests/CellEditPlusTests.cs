using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Import;
using NanumCsvViewer.Agent.Tools;

namespace NanumCsvViewer.Tests
{
    // 시트 편집 확장: 되돌리기/다시 실행, 붙여넣기 해석, 컬럼 이름 변경, 행 삽입/삭제(매핑·저장), xlsx 저장, 복구 저널, 편집 후 뷰 재평가.
    public class CellEditPlusTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_editplus_" + Guid.NewGuid().ToString("N"));

        public CellEditPlusTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private async Task<VirtualCsvDocument> OpenAsync(string text, bool bom = false, string name = "src.csv")
        {
            string path = Path.Combine(_dir, name);
            var bytes = (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            File.WriteAllBytes(path, bytes);
            var doc = VirtualCsvDocument.Open(path);
            await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            return doc;
        }

        private string Save(VirtualCsvDocument doc, string name = "out.csv")
        {
            string dest = Path.Combine(_dir, name);
            doc.SaveWithEdits(dest, null, CancellationToken.None);
            return dest;
        }

        private static string[] Column(VirtualCsvDocument doc, int col)
            => Enumerable.Range(0, doc.DisplayRowCount).Select(i => doc.GetDisplayRow(i)[col]).ToArray();

        // ------------------------------------------------------------ E2 되돌리기 / 다시 실행

        [Fact]
        public void Undo_and_redo_walk_single_edits_and_a_new_edit_drops_the_redo_branch()
        {
            var e = new CellEdits();
            e.Set(0, 0, "a", "o");
            e.Set(0, 0, "b", "o");
            Assert.True(e.TryGet(0, 0, out var v) && v == "b");

            Assert.True(e.Undo());
            Assert.True(e.TryGet(0, 0, out v) && v == "a");
            Assert.True(e.Undo());
            Assert.False(e.Contains(0, 0));
            Assert.False(e.Undo());
            Assert.True(e.Redo());
            Assert.True(e.TryGet(0, 0, out v) && v == "a");

            e.Set(1, 1, "z", ""); // 새 편집 → 다시 실행 가지 폐기
            Assert.False(e.CanRedo);
            Assert.False(e.Redo());
        }

        [Fact]
        public void A_step_is_one_undo_and_raises_one_change_notification()
        {
            var e = new CellEdits();
            int notifications = 0;
            e.Changed += () => notifications++;
            using (e.BeginStep("paste"))
            {
                e.Set(0, 0, "1", "");
                e.Set(0, 1, "2", "");
                e.Set(1, 0, "3", "");
            }
            Assert.Equal(1, notifications);
            Assert.Equal(3, e.Count);
            Assert.Equal("paste", e.UndoDescription);

            e.Undo();
            Assert.True(e.IsEmpty);
            Assert.Equal(2, notifications);
            e.Redo();
            Assert.Equal(3, e.Count);
            Assert.Equal(3, notifications);
        }

        [Fact]
        public void Empty_step_and_unchanged_value_leave_no_history()
        {
            var e = new CellEdits();
            using (e.BeginStep("nothing")) { }
            e.Set(0, 0, "x", "x"); // 원래 값과 같음
            e.Set(0, 0, "y", "x");
            e.Set(0, 0, "y", "x"); // 같은 편집값 다시
            Assert.True(e.Undo());
            Assert.False(e.CanUndo);
        }

        [Fact]
        public void Dirty_tracks_the_saved_point_through_undo_and_redo()
        {
            var e = new CellEdits();
            Assert.False(e.IsDirty);
            e.Set(0, 0, "a", "");
            Assert.True(e.IsDirty);
            e.MarkSaved();
            Assert.False(e.IsDirty);
            e.Set(0, 1, "b", "");
            Assert.True(e.IsDirty);
            e.Undo();
            Assert.False(e.IsDirty);   // 저장 시점으로 돌아옴
            e.Undo();
            Assert.True(e.IsDirty);    // 저장 이전 상태 — 저장 파일과 다름
            e.Redo();
            Assert.False(e.IsDirty);
            e.Undo();
            e.Set(5, 5, "q", "");      // 저장 시점이 있던 재실행 가지가 사라진다
            e.Redo();
            Assert.True(e.IsDirty);
            e.Undo();
            Assert.True(e.IsDirty);    // 저장 시점에 다시 갈 수 없다
        }

        [Fact]
        public void History_is_capped_and_the_saved_point_falls_out_of_range()
        {
            var e = new CellEdits();
            e.Set(0, 0, "x", "");
            e.MarkSaved();
            for (int i = 0; i < CellEdits.MaxUndoSteps + 10; i++) e.Set(1, 0, "v" + i, "");
            int undone = 0;
            while (e.Undo()) undone++;
            Assert.Equal(CellEdits.MaxUndoSteps, undone);
            Assert.True(e.IsDirty); // 저장 시점은 이력 밖
        }

        [Fact]
        public void Discarding_everything_is_one_undoable_step_for_cells_headers_and_rows()
        {
            var e = new CellEdits();
            e.Set(0, 0, "a", "");
            e.SetHeader(1, "name", "v");
            e.DeleteRows(new[] { 1 });
            int added = e.AddRow(0, 2, 3);
            e.Set(added, 0, "new", "");

            e.Clear();
            Assert.True(e.IsEmpty);
            Assert.Empty(e.BuildLiveOrder(3).Except(new[] { 0, 1, 2 }));

            e.Undo();
            Assert.Equal(2, e.Count);
            Assert.True(e.IsDeleted(1));
            Assert.Equal(1, e.AddedCount);
            Assert.True(e.TryGetHeader(1, out var n) && n == "name");
            Assert.Equal(new[] { 0, 3, 2 }, e.BuildLiveOrder(3));
        }

        // ------------------------------------------------------------ E3 붙여넣기 해석

        [Fact]
        public void Clipboard_block_parses_excel_tsv_with_quotes_newlines_and_trailing_terminator()
        {
            var b = ClipboardGrid.Parse("a\tb\r\n\"x\ty\"\t\"line1\r\nline2\"\r\n\"q\"\"q\"\t001\r\n");
            Assert.Equal(3, b.Length);
            Assert.Equal(new[] { "a", "b" }, b[0]);
            Assert.Equal(new[] { "x\ty", "line1\r\nline2" }, b[1]);
            Assert.Equal(new[] { "q\"q", "001" }, b[2]);
        }

        [Theory]
        [InlineData("single", 1, 1)]
        [InlineData("a\nb\nc", 3, 1)]
        [InlineData("a\tb\n", 1, 2)]
        [InlineData("a\t\n", 1, 2)]          // 끝의 탭 = 빈 마지막 셀
        [InlineData("a\n\n", 2, 1)]          // 끝의 줄바꿈 하나만 행 종결, 빈 줄은 빈 행
        [InlineData("a\tb\nc\n", 2, 2)]      // 짧은 행은 빈 셀로 채움
        public void Clipboard_block_shapes(string text, int rows, int cols)
        {
            var b = ClipboardGrid.Parse(text);
            Assert.Equal(rows, b.Length);
            Assert.All(b, r => Assert.Equal(cols, r.Length));
        }

        [Fact]
        public void Clipboard_block_keeps_text_and_treats_stray_quotes_as_literals()
        {
            var b = ClipboardGrid.Parse("00123\t\"unterminated\t5\n 12 \tab\"cd\n");
            Assert.Equal("00123", b[0][0]);
            Assert.Equal("\"unterminated", b[0][1]); // 닫히지 않은 따옴표는 글자 그대로
            Assert.Equal(" 12 ", b[1][0]);
            Assert.Equal("ab\"cd", b[1][1]);
            Assert.Empty(ClipboardGrid.Parse(""));
            Assert.Empty(ClipboardGrid.Parse(null));
        }

        // ------------------------------------------------------------ E4 컬럼 이름

        [Fact]
        public async Task Renamed_header_is_what_every_reader_sees_and_original_is_kept()
        {
            using var doc = await OpenAsync("id,v\n1,a\n");
            doc.Edits.SetHeader(1, "value", doc.OriginalHeader[1]);
            Assert.Equal(new[] { "id", "value" }, doc.Header);
            Assert.Equal(new[] { "id", "v" }, doc.OriginalHeader);

            doc.Edits.Undo();
            Assert.Equal(new[] { "id", "v" }, doc.Header);
            doc.Edits.Redo();
            doc.Edits.SetHeader(1, "v", doc.OriginalHeader[1]); // 원래 이름으로 → 변경 제거
            Assert.Equal(0, doc.Edits.HeaderEditCount);
            Assert.Equal(new[] { "id", "v" }, doc.Header);
        }

        [Fact]
        public async Task Save_writes_the_new_header_keeping_bom_and_line_ending()
        {
            using var doc = await OpenAsync("id,v\r\n1,a\r\n", bom: true);
            doc.Edits.SetHeader(1, "a,b", "v");
            string dest = Save(doc);
            byte[] bytes = File.ReadAllBytes(dest);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            Assert.Equal("id,\"a,b\"\r\n1,a\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }

        // ------------------------------------------------------------ E5 행 삽입 / 삭제

        [Fact]
        public void Live_order_places_added_rows_after_their_anchor_nearest_first_without_recursion_limits()
        {
            var e = new CellEdits();
            Assert.Equal(3, e.AddRow(0, 2, 3));   // 0 아래
            Assert.Equal(4, e.AddRow(0, 2, 3));   // 0 아래, 먼저 넣은 3보다 앵커에 가깝다
            Assert.Equal(5, e.AddRow(3, 2, 3));   // 3 아래
            Assert.Equal(6, e.AddRow(-1, 2, 3));  // 맨 위
            Assert.Equal(new[] { 6, 0, 4, 3, 5, 1, 2 }, e.BuildLiveOrder(3));

            e.DeleteRows(new[] { 4, 1 });
            Assert.Equal(new[] { 6, 0, 3, 5, 2 }, e.BuildLiveOrder(3));

            var chain = new CellEdits();
            int anchor = 0;
            for (int i = 0; i < 20_000; i++) anchor = chain.AddRow(anchor, 1, 1); // 계속 아래에 추가
            var order = chain.BuildLiveOrder(1);
            Assert.Equal(20_001, order.Length);
            Assert.Equal(Enumerable.Range(0, 20_001), order);
        }

        [Fact]
        public async Task Display_mapping_follows_deletes_and_inserts_and_numbers_rows_by_position()
        {
            using var doc = await OpenAsync("k,v\nr1,a\nr2,b\nr3,c\n");
            var edits = doc.Edits;
            edits.DeleteRows(new[] { 1 });
            Assert.Equal(2, doc.DisplayRowCount);
            Assert.Equal(new[] { "r1", "r3" }, Column(doc, 0));
            Assert.Equal(2L, doc.GetSourceRowNumber(1)); // r3은 이제 2번째 행

            int added = edits.AddRow(doc.GetRowId(0), doc.ColumnCount, doc.BaseRowCount); // r1 아래
            Assert.Equal(3, doc.DisplayRowCount);
            Assert.True(doc.IsAddedRow(1));
            Assert.Equal(new[] { "r1", "", "r3" }, Column(doc, 0));
            Assert.Equal(1, doc.FindViewIndex(added));
            Assert.Equal(-1, doc.FindViewIndex(1)); // 삭제된 행
            Assert.Equal(new[] { "r1", "", "r3" }, Enumerable.Range(0, doc.DataRowsAvailable).Select(i => doc.GetDataRow(i)[0]).ToArray());

            edits.Set(added, 0, "new", "");
            Assert.Equal("new", doc.GetDisplayRow(1)[0]);
            Assert.Equal("", doc.GetOriginalRow(added)[0]);
            Assert.Equal(added, doc.GetRowId(1));
            Assert.Equal(added, doc.PredecessorId(2));

            edits.Undo(); edits.Undo(); edits.Undo(); // 편집, 추가, 삭제 순으로 취소
            Assert.Equal(3, doc.DisplayRowCount);
            Assert.Equal(new[] { "r1", "r2", "r3" }, Column(doc, 0));
            Assert.False(doc.Edits.HasStructureEdits);
        }

        [Fact]
        public async Task Snapshot_rows_and_distinct_values_see_the_edited_structure()
        {
            using var doc = await OpenAsync("k\nx\ny\nx\n");
            doc.Edits.DeleteRows(new[] { 0 });
            int added = doc.Edits.AddRow(2, doc.ColumnCount, doc.BaseRowCount);
            doc.Edits.Set(added, 0, "z", "");
            var rows = doc.SnapshotViewRows();
            Assert.Equal(new[] { "y", "x", "z" }, rows.Select(r => r[0]).ToArray());
            var distinct = doc.DistinctValues(0, withinCurrentView: false, CancellationToken.None);
            Assert.Equal(new[] { "x", "y", "z" }, distinct.Select(d => d.Value).OrderBy(s => s).ToArray());
        }

        [Fact]
        public async Task Deleting_a_row_removes_it_from_an_active_filter_view_at_once()
        {
            using var doc = await OpenAsync("k,v\na,1\nb,1\nc,2\nd,1\n");
            await doc.ApplyFilterAsync(r => r[1] == "1", null, CancellationToken.None);
            Assert.Equal(3, doc.DisplayRowCount);
            doc.Edits.DeleteRows(new[] { doc.GetRowId(1) }); // b
            Assert.Equal(new[] { "a", "d" }, Column(doc, 0));
            Assert.Equal(new[] { 1L, 3L }, new[] { doc.GetSourceRowNumber(0), doc.GetSourceRowNumber(1) }); // c가 빠졌으므로 d = 3번째
            doc.Edits.Undo();
            // 되돌려도 뷰는 재평가 전까지 그대로(재평가가 채운다) — 그러나 총 행 수는 복원된다.
            Assert.Equal(4, doc.DataRowsAvailable);
        }

        [Fact]
        public async Task Reset_view_order_after_structure_edits_uses_screen_order()
        {
            using var doc = await OpenAsync("k\nc\na\nb\n");
            int added = doc.Edits.AddRow(doc.GetRowId(0), 1, doc.BaseRowCount); // c 아래
            doc.Edits.Set(added, 0, "z", "");
            await doc.RebuildViewAsync(null, new[] { new SortKey(0, true) }, null, CancellationToken.None);
            Assert.Equal(new[] { "a", "b", "c", "z" }, Column(doc, 0));
            doc.ResetViewOrder();
            Assert.Equal(new[] { "c", "z", "a", "b" }, Column(doc, 0));
        }

        [Fact]
        public async Task Save_skips_deleted_rows_and_writes_added_rows_in_place_with_the_file_line_ending()
        {
            using var doc = await OpenAsync("id,v\r\n1,a\r\n2,b\r\n3,c\r\n");
            var e = doc.Edits;
            e.SetHeader(1, "val", "v");
            e.Set(0, 1, "A", "a");
            e.DeleteRows(new[] { 1 });
            int added = e.AddRow(2, doc.ColumnCount, doc.BaseRowCount);
            e.Set(added, 0, "9", "");
            e.Set(added, 1, "z,1", "");
            int top = e.AddRow(-1, doc.ColumnCount, doc.BaseRowCount);
            e.Set(top, 0, "0", "");
            string dest = Save(doc);
            Assert.Equal("id,val\r\n0,\r\n1,A\r\n3,c\r\n9,\"z,1\"\r\n", File.ReadAllText(dest));
        }

        [Theory]
        [InlineData("id,v\n1,a\n2,b", "id,v\n1,a\n2,b\nX,y\n", true)]    // 마지막 행에 줄바꿈이 없던 파일 뒤에 추가
        [InlineData("id,v\n1,a\n2,b", "id,v\n1,a\n", false)]            // 줄바꿈 없는 마지막 행 삭제
        [InlineData("id,v", "id,v\r\nX,y\r\n", true)]                   // 헤더만 있고 줄바꿈 없는 파일(줄바꿈 정보 없음 → CRLF)
        public async Task Save_handles_a_missing_final_line_break(string source, string expected, bool add)
        {
            using var doc = await OpenAsync(source);
            if (add)
            {
                int anchor = doc.BaseRowCount > 0 ? doc.BaseRowCount - 1 : -1;
                int id = doc.Edits.AddRow(anchor, doc.ColumnCount, doc.BaseRowCount);
                doc.Edits.Set(id, 0, "X", "");
                doc.Edits.Set(id, 1, "y", "");
            }
            else doc.Edits.DeleteRows(new[] { doc.BaseRowCount - 1 });
            Assert.Equal(expected, File.ReadAllText(Save(doc)));
        }

        [Fact]
        public async Task Save_round_trips_structure_edits_through_a_reopened_file()
        {
            using var doc = await OpenAsync("a,b\n1,x\n2,y\n3,z\n");
            doc.Edits.DeleteRows(new[] { 0, 2 });
            int id = doc.Edits.AddRow(1, 2, doc.BaseRowCount);
            doc.Edits.Set(id, 0, "007", "");
            doc.Edits.Set(id, 1, "q\"r", "");
            string dest = Save(doc);
            using var reopened = VirtualCsvDocument.Open(dest);
            await reopened.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            Assert.Equal(2, reopened.DataRowsAvailable);
            Assert.Equal(new[] { "2", "y" }, reopened.GetDataRow(0));
            Assert.Equal(new[] { "007", "q\"r" }, reopened.GetDataRow(1));
        }

        // ------------------------------------------------------------ E1 편집 후 뷰 재평가

        [Fact]
        public async Task Rebuilding_the_view_after_an_edit_applies_filter_and_sort_to_the_new_values()
        {
            using var doc = await OpenAsync("k,v\na,3\nb,1\nc,2\nd,9\n");
            Func<string[], bool> pred = r => r[0] != "d";
            await doc.RebuildViewAsync(pred, new[] { new SortKey(1, true) }, null, CancellationToken.None);
            Assert.Equal(new[] { "b", "c", "a" }, Column(doc, 0));

            doc.Edits.Set(0, 1, "0", "3");   // a: 3 → 0, 정렬 위치가 바뀐다
            doc.Edits.Set(3, 0, "e", "d");   // d → e: 필터를 통과하기 시작한다
            Assert.Equal(new[] { "b", "c", "a" }, Column(doc, 0)); // 재평가 전에는 옛 뷰
            await doc.RebuildViewAsync(pred, new[] { new SortKey(1, true) }, null, CancellationToken.None);
            Assert.Equal(new[] { "a", "b", "c", "e" }, Column(doc, 0).Select(s => s).ToArray());
            Assert.Equal(new[] { "0", "1", "2", "9" }, Column(doc, 1));

            await doc.RebuildViewAsync(null, Array.Empty<SortKey>(), null, CancellationToken.None);
            Assert.False(doc.IsFiltered);
            Assert.Equal(4, doc.DisplayRowCount);
        }

        // ------------------------------------------------------------ E6 xlsx 저장

        [Fact]
        public async Task Xlsx_save_keeps_every_value_as_text_and_reopens_through_the_importer()
        {
            using var doc = await OpenAsync("id,code,note\n1,007,\"two\nlines\"\n2,1E5,\" pad \"\n");
            doc.Edits.SetHeader(1, "zip", "code");
            doc.Edits.Set(1, 0, "00042", "2");
            doc.Edits.DeleteRows(new[] { 0 });
            int id = doc.Edits.AddRow(1, 3, doc.BaseRowCount);
            doc.Edits.Set(id, 1, "_x0041_", "");
            string dest = Path.Combine(_dir, "out.xlsx");
            doc.SaveAsXlsx(dest, "My: Sheet/1", null, CancellationToken.None);

            string tmp = Path.Combine(_dir, "imp");
            var sheets = TabularImporter.Import(dest, tmp);
            Assert.Single(sheets);
            Assert.Equal("My_ Sheet_1", sheets[0].Name);
            using var back = VirtualCsvDocument.Open(sheets[0].CsvPath);
            await back.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            Assert.Equal(new[] { "id", "zip", "note" }, back.Header);
            Assert.Equal(2, back.DataRowsAvailable);
            Assert.Equal(new[] { "00042", "1E5", " pad " }, back.GetDataRow(0));
            Assert.Equal("_x0041_", back.GetDataRow(1)[1]); // 글자 그대로의 _x0041_ 도 보존
        }

        [Fact]
        public void Xlsx_text_escape_handles_controls_surrogates_and_literal_escape_lookalikes()
        {
            Assert.Equal("plain", XlsxDataWriter.EscapeXmlString("plain"));
            Assert.Equal("a_x0001_b", XlsxDataWriter.EscapeXmlString("a\u0001b"));
            Assert.Equal("_x005F_x0041_", XlsxDataWriter.EscapeXmlString("_x0041_"));
            Assert.Equal("_x_", XlsxDataWriter.EscapeXmlString("_x_"));
            Assert.Equal("a\tb\nc\rd", XlsxDataWriter.EscapeXmlString("a\tb\nc\rd"));
            Assert.Equal("_xD800_", XlsxDataWriter.EscapeXmlString("\ud800"));
            Assert.Equal("😀", XlsxDataWriter.EscapeXmlString("😀"));
        }

        [Fact]
        public async Task Xlsx_save_refuses_cells_over_the_excel_limit_and_leaves_no_file()
        {
            using var doc = await OpenAsync("a\nx\n");
            doc.Edits.Set(0, 0, new string('x', XlsxDataWriter.MaxCellCharacters + 1), "x");
            string dest = Path.Combine(_dir, "big.xlsx");
            var ex = Assert.Throws<InvalidOperationException>(() => doc.SaveAsXlsx(dest, "s", null, CancellationToken.None));
            Assert.Contains("32,767", ex.Message);
            Assert.False(File.Exists(dest));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
            Assert.Throws<InvalidOperationException>(() => doc.SaveAsXlsx(Path.Combine(_dir, "src.csv"), "s", null, CancellationToken.None));
        }

        // ------------------------------------------------------------ E7 복구 저널

        [Fact]
        public async Task Journal_round_trips_all_edit_kinds_and_restores_them_as_unsaved()
        {
            using var doc = await OpenAsync("id,v\n1,a\n2,b\n");
            var e = doc.Edits;
            e.Set(0, 1, "A", "a");
            e.SetHeader(0, "key", "id");
            e.DeleteRows(new[] { 1 });
            int id = e.AddRow(0, 2, doc.BaseRowCount);
            e.Set(id, 1, "새 값", "");

            string dir = Path.Combine(_dir, "journal");
            string? key = EditJournal.KeyFor(Path.Combine(_dir, "src.csv"));
            Assert.NotNull(key);
            EditJournal.Write(dir, key!, "src.csv", 0, e.Snapshot());
            Assert.True(EditJournal.Exists(dir, key!));

            using var doc2 = await OpenAsync("id,v\n1,a\n2,b\n");
            var snap = EditJournal.TryRead(dir, key!)!;
            doc2.Edits.Restore(snap, doc2.BaseRowCount, doc2.ColumnCount);
            Assert.True(doc2.Edits.IsDirty);
            Assert.False(doc2.Edits.CanUndo);
            Assert.Equal(new[] { "key", "v" }, doc2.Header);
            Assert.Equal(new[] { "1", "A" }, doc2.GetDataRow(0));
            Assert.Equal(new[] { "", "새 값" }, doc2.GetDataRow(1));
            Assert.Equal(2, doc2.DataRowsAvailable);

            doc2.Edits.MarkSaved();
            EditJournal.Delete(dir, key!);
            Assert.False(EditJournal.Exists(dir, key!));
            Assert.Null(EditJournal.TryRead(dir, key!));
        }

        [Fact]
        public async Task Journal_key_changes_when_the_file_changes_and_names_the_sheet()
        {
            string path = Path.Combine(_dir, "k.csv");
            File.WriteAllText(path, "a\n1\n");
            string? k1 = EditJournal.KeyFor(path);
            Assert.Equal(k1, EditJournal.KeyFor(path));
            Assert.NotEqual(k1, EditJournal.KeyFor(path, sheetIndex: 1));
            File.WriteAllText(path, "a\n1\n2\n");
            Assert.NotEqual(k1, EditJournal.KeyFor(path));
            Assert.Null(EditJournal.KeyFor(Path.Combine(_dir, "missing.csv")));
            await Task.CompletedTask;
        }

        [Fact]
        public async Task Restore_rejects_data_that_does_not_fit_and_changes_nothing()
        {
            using var doc = await OpenAsync("a,b\n1,2\n");
            Assert.Throws<InvalidDataException>(() => doc.Edits.Restore(
                new EditSnapshot(-1, new[] { (5, 0, "x") }, Array.Empty<(int, string)>(), Array.Empty<int>(), Array.Empty<AddedRow>()),
                doc.BaseRowCount, doc.ColumnCount));
            Assert.Throws<InvalidDataException>(() => doc.Edits.Restore(
                new EditSnapshot(-1, Array.Empty<(int, int, string)>(), new[] { (9, "n") }, Array.Empty<int>(), Array.Empty<AddedRow>()),
                doc.BaseRowCount, doc.ColumnCount));
            Assert.Throws<InvalidDataException>(() => doc.Edits.Restore(
                new EditSnapshot(7, Array.Empty<(int, int, string)>(), Array.Empty<(int, string)>(), Array.Empty<int>(), Array.Empty<AddedRow>()),
                doc.BaseRowCount, doc.ColumnCount));
            Assert.True(doc.Edits.IsEmpty);
        }

        [Fact]
        public void Corrupt_or_foreign_journal_is_reported_not_applied()
        {
            string dir = Path.Combine(_dir, "bad");
            Directory.CreateDirectory(dir);
            File.WriteAllText(EditJournal.FilePath(dir, "k1"), "{ not json");
            Assert.Throws<InvalidDataException>(() => EditJournal.TryRead(dir, "k1"));
            File.WriteAllText(EditJournal.FilePath(dir, "k2"), "{\"Version\":99}");
            Assert.Throws<InvalidDataException>(() => EditJournal.TryRead(dir, "k2"));
            Assert.Null(EditJournal.TryRead(dir, "absent"));
        }

        // ------------------------------------------------------------ 추가 컬럼(정규식 추출 등)

        private static void Extract(VirtualCsvDocument doc, string name, params (int Row, string Value)[] values)
        {
            int col = doc.ColumnCount;
            using (doc.Edits.BeginStep("extract"))
            {
                doc.Edits.AppendColumn(name, doc.RawColumnCount);
                foreach (var (row, value) in values) doc.Edits.Set(row, col, value, "");
            }
        }

        [Fact]
        public async Task Appended_column_widens_every_row_is_written_on_save_and_undo_removes_it_in_one_step()
        {
            string original = "id,v\n1,a\n2,b\n3,c\n";
            using var doc = await OpenAsync(original);
            Extract(doc, "x", (0, "A"), (2, "C"));

            Assert.Equal(new[] { "id", "v", "x" }, doc.Header);
            Assert.Equal(3, doc.ColumnCount);
            Assert.Equal(2, doc.RawColumnCount);
            Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(3, doc.GetDisplayRow(i).Length));
            Assert.Equal(new[] { "A", "", "C" }, Column(doc, 2));
            Assert.Equal(2, doc.Edits.Count); // O(비어 있지 않은 값)
            Assert.True(doc.Edits.IsAppendedColumn(2));
            Assert.False(doc.Edits.IsAppendedColumn(1));
            Assert.Equal("id,v,x\n1,a,A\n2,b,\n3,c,C\n", File.ReadAllText(Save(doc)));
            Assert.Equal(new[] { "x" }, doc.Edits.AppendedColumnNames());

            Assert.True(doc.Edits.Undo()); // 컬럼과 값이 한 번에
            Assert.False(doc.Edits.CanUndo);
            Assert.True(doc.Edits.IsEmpty);
            Assert.Equal(new[] { "id", "v" }, doc.Header);
            Assert.Equal(2, doc.GetDisplayRow(0).Length);
            Assert.Equal(original, File.ReadAllText(Save(doc, "back.csv")));

            Assert.True(doc.Edits.Redo());
            Assert.Equal(new[] { "id", "v", "x" }, doc.Header);
            Assert.Equal(new[] { "A", "", "C" }, Column(doc, 2));
        }

        [Fact]
        public async Task Appended_column_on_ragged_rows_pads_short_rows_and_keeps_extra_fields_after_the_new_column()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n4\n5,6,7,8\n");
            Extract(doc, "n", (0, "N"));
            Assert.Equal(new[] { "1", "2", "3", "N" }, doc.GetDisplayRow(0));
            Assert.Equal(new[] { "4", "", "", "" }, doc.GetDisplayRow(1));
            Assert.Equal(new[] { "5", "6", "7", "", "8" }, doc.GetDisplayRow(2));
            Assert.Equal("a,b,c,n\n1,2,3,N\n4,,,\n5,6,7,,8\n", File.ReadAllText(Save(doc)));
        }

        [Fact]
        public async Task Appended_column_works_with_row_insert_delete_and_rename_and_everything_undoes()
        {
            string original = "id,v\n1,a\n2,b\n3,c\n";
            using var doc = await OpenAsync(original);
            var e = doc.Edits;
            int early = e.AddRow(0, doc.ColumnCount, doc.BaseRowCount); // 컬럼보다 먼저 만든 행(너비 2)
            Extract(doc, "x", (0, "A"), (1, "B"));
            Assert.Equal(new[] { "", "", "" }, doc.GetRowById(early)); // 새 너비로 채워진다

            int late = e.AddRow(2, doc.ColumnCount, doc.BaseRowCount); // 컬럼 뒤에 만든 행(너비 3)
            e.Set(late, 2, "Z", "");
            e.DeleteRows(new[] { 1 });
            e.SetHeader(2, "renamed", doc.OriginalHeader[2]);

            Assert.Equal(new[] { "id", "v", "x" }, doc.OriginalHeader);
            Assert.Equal(new[] { "id", "v", "renamed" }, doc.Header);
            // 화면 순서: 1(A), 빈 행(early, 1 아래), [2 삭제], 3, late(3 아래) — 추가 행 앵커: early=0 아래, late=2 아래
            var shown = Enumerable.Range(0, doc.DisplayRowCount).Select(i => string.Join("|", doc.GetDisplayRow(i))).ToArray();
            Assert.Equal(new[] { "1|a|A", "||", "3|c|", "||Z" }, shown);
            Assert.Equal("id,v,renamed\n1,a,A\n,,\n3,c,\n,,Z\n", File.ReadAllText(Save(doc)));

            // 이름을 원래 이름으로 돌리면 이름 변경이 사라진다
            e.SetHeader(2, "x", doc.OriginalHeader[2]);
            Assert.Equal(new[] { "id", "v", "x" }, doc.Header);
            Assert.Equal(0, e.HeaderEditCount);

            while (e.Undo()) { }
            Assert.True(e.IsEmpty);
            Assert.Equal(new[] { "id", "v" }, doc.Header);
            Assert.Equal(original, File.ReadAllText(Save(doc, "all-undone.csv")));
        }

        [Fact]
        public async Task Discard_all_removes_appended_columns_in_one_undoable_step()
        {
            using var doc = await OpenAsync("id,v\n1,a\n2,b\n");
            Extract(doc, "x", (0, "A"));
            Extract(doc, "y", (1, "B"));
            Assert.Equal(new[] { "id", "v", "x", "y" }, doc.Header);

            doc.Edits.Clear();
            Assert.True(doc.Edits.IsEmpty);
            Assert.Equal(new[] { "id", "v" }, doc.Header);

            Assert.True(doc.Edits.Undo());
            Assert.Equal(new[] { "id", "v", "x", "y" }, doc.Header);
            Assert.Equal(new[] { "A", "" }, Column(doc, 2));
            Assert.Equal(new[] { "", "B" }, Column(doc, 3));
        }

        [Fact]
        public async Task Appended_columns_survive_the_recovery_journal_and_foreign_ones_are_rejected()
        {
            using var doc = await OpenAsync("id,v\n1,a\n2,b\n");
            Extract(doc, "x", (1, "B"));
            doc.Edits.SetHeader(2, "ex", doc.OriginalHeader[2]);
            string dir = Path.Combine(_dir, "journal-cols");
            EditJournal.Write(dir, "k", "src.csv", 0, doc.Edits.Snapshot());

            using var doc2 = await OpenAsync("id,v\n1,a\n2,b\n");
            doc2.Edits.Restore(EditJournal.TryRead(dir, "k")!, doc2.BaseRowCount, doc2.ColumnCount);
            Assert.Equal(new[] { "id", "v", "ex" }, doc2.Header);
            Assert.Equal(new[] { "", "B" }, Column(doc2, 2));
            Assert.True(doc2.Edits.IsDirty);
            Assert.Equal("id,v,ex\n1,a,\n2,b,B\n", File.ReadAllText(Save(doc2, "restored.csv")));

            using var doc3 = await OpenAsync("id,v,w\n1,a,z\n");
            Assert.Throws<InvalidDataException>(() => doc3.Edits.Restore(EditJournal.TryRead(dir, "k")!, doc3.BaseRowCount, doc3.ColumnCount));
            Assert.True(doc3.Edits.IsEmpty);
        }

        // ------------------------------------------------------------ 컬럼 삭제(덮개)

        private static void DeleteCol(VirtualCsvDocument doc, int visibleColumn, string? description = null)
            => doc.Edits.DeleteColumn(visibleColumn, doc.ColumnCount, doc.RawColumnCount, description);

        [Fact]
        public async Task Deleted_column_vanishes_from_rows_header_and_saves_and_one_undo_restores_the_original_bytes()
        {
            string original = "id,name,v\n1,a,x\n2,b,y\n";
            using var doc = await OpenAsync(original);
            DeleteCol(doc, 1, "drop name");

            Assert.Equal(new[] { "id", "v" }, doc.Header);
            Assert.Equal(2, doc.ColumnCount);
            Assert.Equal(new[] { "1", "x" }, doc.GetDisplayRow(0));
            Assert.Equal(new[] { "id", "v" }, doc.OriginalHeader);       // 이름 변경 기준도 보이는 컬럼
            Assert.Equal(new[] { "2", "y" }, doc.GetOriginalRow(doc.GetRowId(1)));
            Assert.Equal(1, doc.Edits.DeletedColumnCount);
            Assert.Equal("drop name", doc.Edits.UndoDescription);
            Assert.Equal("id,v\n1,x\n2,y\n", File.ReadAllText(Save(doc)));

            Assert.True(doc.Edits.Undo());                                 // 한 단계
            Assert.False(doc.Edits.CanUndo);
            Assert.True(doc.Edits.IsEmpty);
            Assert.Equal(new[] { "id", "name", "v" }, doc.Header);
            Assert.Equal(original, File.ReadAllText(Save(doc, "back.csv")));

            Assert.True(doc.Edits.Redo());
            Assert.Equal(new[] { "id", "v" }, doc.Header);
        }

        [Fact]
        public async Task Cell_edits_keep_their_column_across_delete_undo_and_edits_in_the_deleted_column_come_back_on_undo()
        {
            using var doc = await OpenAsync("a,b,c,d\n1,2,3,4\n5,6,7,8\n");
            var e = doc.Edits;
            e.Set(0, 2, "C!", "3");   // 삭제 전 편집(보이는 2 = c)
            e.Set(1, 1, "B!", "6");   // 곧 삭제될 컬럼의 편집
            DeleteCol(doc, 1);        // b 삭제 → a,c,d
            Assert.Equal(new[] { "a", "c", "d" }, doc.Header);
            Assert.Equal(1, e.Count);                                      // b의 편집은 버려진다(삭제 단계에 묶임)
            Assert.Equal(new[] { "1", "C!", "4" }, doc.GetDisplayRow(0));
            Assert.True(e.TryGet(0, 1, out var cv) && cv == "C!");         // 같은 셀이 이제 보이는 1
            Assert.False(e.Contains(0, 2));

            e.Set(1, 2, "D!", "8");   // 삭제 후 편집(보이는 2 = d)
            Assert.Equal("a,c,d\n1,C!,4\n5,7,D!\n", File.ReadAllText(Save(doc)));

            e.Undo();                 // D! 편집
            e.Undo();                 // 컬럼 삭제
            Assert.Equal(new[] { "a", "b", "c", "d" }, doc.Header);
            Assert.Equal(new[] { "5", "B!", "7", "8" }, doc.GetDisplayRow(1)); // b의 편집이 돌아온다
            Assert.Equal(new[] { "1", "2", "C!", "4" }, doc.GetDisplayRow(0));
            Assert.Equal(2, e.Count);
        }

        [Fact]
        public async Task Rename_and_delete_interplay_follows_the_column_not_the_position()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n");
            var e = doc.Edits;
            e.SetHeader(2, "gamma", doc.OriginalHeader[2]);
            DeleteCol(doc, 0);                                             // a 삭제 → b,gamma
            Assert.Equal(new[] { "b", "gamma" }, doc.Header);
            e.SetHeader(1, "G2", doc.OriginalHeader[1]);                   // 보이는 1 = c의 새 이름
            Assert.Equal(new[] { "b", "G2" }, doc.Header);
            Assert.Equal("b,G2\n2,3\n", File.ReadAllText(Save(doc)));

            // 삭제한 컬럼의 이름 변경은 삭제와 함께 버려지고 되돌리면 돌아온다
            DeleteCol(doc, 0);                                             // b 삭제 → G2
            Assert.Equal(new[] { "G2" }, doc.Header);
            e.Undo(); e.Undo(); e.Undo();
            Assert.Equal(new[] { "a", "b", "gamma" }, doc.Header);
            Assert.Equal(1, e.HeaderEditCount);
        }

        [Fact]
        public async Task Appended_columns_can_be_deleted_and_appending_after_a_delete_returns_the_visible_index()
        {
            using var doc = await OpenAsync("id,v,w\n1,a,z\n2,b,y\n");
            var e = doc.Edits;
            DeleteCol(doc, 0);                                             // id 삭제 → v,w
            int x = e.AppendColumn("x", doc.RawColumnCount);               // 보이는 번호 2
            Assert.Equal(2, x);
            e.Set(0, x, "X1", "");
            Assert.Equal(new[] { "v", "w", "x" }, doc.Header);
            Assert.True(e.IsAppendedColumn(2));
            Assert.False(e.IsAppendedColumn(1));
            Assert.Equal(new[] { "a", "z", "X1" }, doc.GetDisplayRow(0));
            Assert.Equal("v,w,x\na,z,X1\nb,y,\n", File.ReadAllText(Save(doc)));

            DeleteCol(doc, 2);                                             // 추가 컬럼 자체를 삭제
            Assert.Equal(new[] { "v", "w" }, doc.Header);
            Assert.Equal("v,w\na,z\nb,y\n", File.ReadAllText(Save(doc, "x-gone.csv")));
            e.Undo();
            Assert.Equal(new[] { "a", "z", "X1" }, doc.GetDisplayRow(0));

            while (e.Undo()) { }
            Assert.True(e.IsEmpty);
            Assert.Equal(new[] { "id", "v", "w" }, doc.Header);
        }

        [Fact]
        public async Task Delete_column_works_with_inserted_and_deleted_rows_in_either_order()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n4,5,6\n7,8,9\n");
            var e = doc.Edits;
            int early = e.AddRow(0, doc.ColumnCount, doc.BaseRowCount);    // 컬럼 삭제 전에 만든 행(물리 너비 3)
            e.Set(early, 2, "E", "");
            DeleteCol(doc, 1);                                             // b 삭제
            int late = e.AddRow(2, doc.ColumnCount, doc.BaseRowCount);     // 삭제 뒤에 만든 행(보이는 2열 + 삭제 칸 1)
            e.Set(late, 1, "L", "");
            e.DeleteRows(new[] { 1 });

            var shown = Enumerable.Range(0, doc.DisplayRowCount).Select(i => string.Join("|", doc.GetDisplayRow(i))).ToArray();
            Assert.Equal(new[] { "1|3", "|E", "7|9", "|L" }, shown);
            Assert.Equal("a,c\n1,3\n,E\n7,9\n,L\n", File.ReadAllText(Save(doc)));

            while (e.Undo()) { }
            Assert.Equal("a,b,c\n1,2,3\n4,5,6\n7,8,9\n", File.ReadAllText(Save(doc, "all-undone.csv")));
        }

        [Fact]
        public async Task Deleted_columns_survive_the_recovery_journal_and_old_journals_without_them_still_load()
        {
            using var doc = await OpenAsync("id,v,w\n1,a,z\n2,b,y\n");
            Extract(doc, "x", (1, "B"));
            DeleteCol(doc, 1);                                             // v 삭제 → id,w,x
            doc.Edits.Set(0, 1, "Z!", "z");
            string dir = Path.Combine(_dir, "journal-del");
            var snap = doc.Edits.Snapshot();
            Assert.Equal(new[] { 1 }, snap.DeletedColumns);                // 물리 번호
            EditJournal.Write(dir, "k", "src.csv", 0, snap);

            using var doc2 = await OpenAsync("id,v,w\n1,a,z\n2,b,y\n");
            doc2.Edits.Restore(EditJournal.TryRead(dir, "k")!, doc2.BaseRowCount, doc2.ColumnCount);
            Assert.Equal(new[] { "id", "w", "x" }, doc2.Header);
            Assert.Equal(new[] { "1", "Z!", "" }, doc2.GetDisplayRow(0));
            Assert.Equal("id,w,x\n1,Z!,\n2,y,B\n", File.ReadAllText(Save(doc2, "restored.csv")));
            Assert.True(doc2.Edits.IsDirty);

            // 삭제 컬럼 정보가 없는 이전 저널
            string oldPath = EditJournal.FilePath(dir, "old");
            File.WriteAllText(oldPath, "{\"Version\":1,\"BaseRows\":-1,\"Cells\":[],\"Headers\":[],\"Deleted\":[],\"Added\":[]}");
            using var doc3 = await OpenAsync("id,v,w\n1,a,z\n");
            doc3.Edits.Restore(EditJournal.TryRead(dir, "old")!, doc3.BaseRowCount, doc3.ColumnCount);
            Assert.Equal(new[] { "id", "v", "w" }, doc3.Header);

            // 범위 밖/전부 삭제 같은 이상한 저널은 거부하고 아무것도 바꾸지 않는다
            using var doc4 = await OpenAsync("a,b\n1,2\n");
            Assert.Throws<InvalidDataException>(() => doc4.Edits.Restore(
                new EditSnapshot(-1, Array.Empty<(int, int, string)>(), Array.Empty<(int, string)>(), Array.Empty<int>(), Array.Empty<AddedRow>(), null, -1, new[] { 5 }),
                doc4.BaseRowCount, doc4.ColumnCount));
            Assert.Throws<InvalidDataException>(() => doc4.Edits.Restore(
                new EditSnapshot(-1, Array.Empty<(int, int, string)>(), Array.Empty<(int, string)>(), Array.Empty<int>(), Array.Empty<AddedRow>(), null, -1, new[] { 0, 1 }),
                doc4.BaseRowCount, doc4.ColumnCount));
            Assert.True(doc4.Edits.IsEmpty);
        }

        [Fact]
        public async Task Last_remaining_column_cannot_be_deleted_and_bad_indexes_are_rejected()
        {
            using var doc = await OpenAsync("a,b\n1,2\n");
            Assert.Throws<ArgumentOutOfRangeException>(() => DeleteCol(doc, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => DeleteCol(doc, -1));
            DeleteCol(doc, 0);
            Assert.Throws<InvalidOperationException>(() => DeleteCol(doc, 0));
            Assert.Equal(new[] { "b" }, doc.Header);
        }

        [Fact]
        public async Task Filters_sorts_and_view_snapshots_see_the_shrunken_table()
        {
            using var doc = await OpenAsync("id,junk,score\n1,x,30\n2,y,10\n3,z,20\n");
            DeleteCol(doc, 1);
            // 필터 술어는 삭제 후 행(id,score)을 본다 — 번호 2가 score
            await doc.ApplyFilterAsync(r => r.Length == 2 && double.Parse(r[1]) >= 20, null, CancellationToken.None);
            Assert.Equal(2, doc.DisplayRowCount);
            await doc.SortAsync(1, true, null, CancellationToken.None);
            var rows = doc.SnapshotViewRows();
            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "3", "20" }, rows[0]);
            Assert.Equal(new[] { "1", "30" }, rows[1]);
        }

        [Fact]
        public async Task Xlsx_export_omits_deleted_columns()
        {
            using var doc = await OpenAsync("keepme,secret,v\n1,hidden-value,x\n");
            DeleteCol(doc, 1);
            string dest = Path.Combine(_dir, "out.xlsx");
            doc.SaveAsXlsx(dest, "S", null, CancellationToken.None);
            using var zip = System.IO.Compression.ZipFile.OpenRead(dest);
            string all = string.Concat(zip.Entries.Where(en => en.FullName.StartsWith("xl/worksheets/") || en.FullName.Contains("sharedStrings"))
                .Select(en => { using var r = new StreamReader(en.Open()); return r.ReadToEnd(); }));
            Assert.DoesNotContain("hidden-value", all);
            Assert.DoesNotContain("secret", all);
            Assert.Contains("keepme", all);
        }

        [Fact]
        public async Task Discarding_everything_restores_deleted_columns_in_one_undoable_step()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n");
            Extract(doc, "x", (0, "X"));
            DeleteCol(doc, 0);
            Assert.Equal(new[] { "b", "c", "x" }, doc.Header);
            doc.Edits.Clear();
            Assert.True(doc.Edits.IsEmpty);
            Assert.Equal(new[] { "a", "b", "c" }, doc.Header);
            Assert.True(doc.Edits.Undo());
            Assert.Equal(new[] { "b", "c", "x" }, doc.Header);
            Assert.Equal(new[] { "2", "3", "X" }, doc.GetDisplayRow(0));
        }
    }
}

namespace NanumCsvViewer.Tests
{
    // 실제 Form1(보이지 않는 창)에서: 컬럼 삽입/삭제 → 그리드 컬럼·정렬·숨김 상태가 같이 움직이고 되돌려지는지,
    // 주소 상자/Ctrl+G 이동, 조건부 서식 그리기(편집 강조와의 우선순위), 저장 뷰 자동 저장.
    [Collection("SavedViewStore")]
    public class FormEditIntegrationTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_formedit_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        public FormEditIntegrationTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { SavedViewStore.DirectoryOverride = null; try { Directory.Delete(_dir, true); } catch { } }

        private static T Get<T>(Form1 f, string field) => (T)typeof(Form1).GetField(field, Inst)!.GetValue(f)!;

        private static void Pump(Task task)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(60)) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(1); }
            Assert.True(task.IsCompleted);
            task.GetAwaiter().GetResult();
        }

        private static T Await<T>(Task<T> task) { Pump(task); return task.Result; }

        private void OnForm(string csv, Action<Form1, VirtualCsvDocument> body)
        {
            string path = Path.Combine(_dir, "grid.csv");
            File.WriteAllText(path, csv, new UTF8Encoding(false));
            SavedViewStore.DirectoryOverride = Path.Combine(_dir, "views");
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    using var form = new Form1(new AppSettings());
                    _ = form.Handle;
                    typeof(Form1).GetMethod("LoadDocument", Inst)!.Invoke(form, new object[] { path, "grid" });
                    var doc = Get<VirtualCsvDocument>(form, "_doc");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(30) && !(doc.IndexingComplete && !Get<bool>(form, "_indexing") && !Get<bool>(form, "_busy")))
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(5);
                    }
                    Assert.True(doc.IndexingComplete);
                    // 로드 대기 중 DoEvents가 스레드의 동기화 컨텍스트를 기본값으로 되돌리는 경우가 있어, await 연속이 UI 스레드로 돌아오도록 다시 설치한다.
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    body(form, doc);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "UI test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static string[] GridHeaders(Form1 f) => f.Controls.Find("grid", true).OfType<System.Windows.Forms.DataGridView>().Single()
            .Columns.Cast<System.Windows.Forms.DataGridViewColumn>().Select(c => c.HeaderText).ToArray();

        private static System.Windows.Forms.DataGridView GridOf(Form1 f) => f.Controls.Find("grid", true).OfType<System.Windows.Forms.DataGridView>().Single();

        [Fact]
        public void Deleting_and_restoring_a_middle_column_moves_grid_columns_and_view_state_together()
        {
            OnForm("id,name,score,flag\n1,a,30,x\n2,b,10,y\n3,c,20,z\n", (form, doc) =>
            {
                var grid = GridOf(form);
                var hidden = Get<HashSet<int>>(form, "_hiddenColumns");
                var sort = Get<List<SortKey>>(form, "_sortKeys");
                Assert.Equal(new[] { "id", "name", "score", "flag" }, GridHeaders(form));

                grid.Columns[3].Visible = false; hidden.Add(3);           // flag 숨김
                sort.Add(new SortKey(2, true));                           // score 정렬 표시
                Pump(doc.SortAsync(sort, null, CancellationToken.None));
                Assert.Equal("10", doc.GetDisplayRow(0)[2]);

                Assert.Equal("name", form.AgentDeleteColumn(1, "AI: drop name"));
                Assert.Equal(new[] { "id", "score", "flag" }, GridHeaders(form));
                Assert.Equal(new[] { "col0", "col2", "col3" }, grid.Columns.Cast<System.Windows.Forms.DataGridViewColumn>().Select(c => c.Name).ToArray());
                Assert.Equal(new[] { 2 }, hidden.ToArray());                // flag는 이제 2번
                Assert.False(grid.Columns[2].Visible);
                Assert.Equal(new SortKey(1, true), Assert.Single(sort));    // score는 이제 1번
                Assert.Equal(new[] { "id", "score", "flag" }, doc.Header);
                Assert.Equal("AI: drop name", doc.Edits.UndoDescription);

                typeof(Form1).GetMethod("UndoEdit", Inst)!.Invoke(form, null);
                Assert.Equal(new[] { "id", "name", "score", "flag" }, GridHeaders(form));
                Assert.Equal(new[] { 3 }, hidden.ToArray());
                Assert.Equal(new SortKey(2, true), Assert.Single(sort));
                Assert.False(grid.Columns[3].Visible);
                Assert.True(grid.Columns[1].Visible);

                // 새 컬럼: 끝에 추가, 값 채움, 삭제/되돌림
                int added = form.AgentAddColumn("tag", "T", "AI: add tag");
                Assert.Equal(4, added);
                Assert.Equal(new[] { "id", "name", "score", "flag", "tag" }, GridHeaders(form));
                Assert.Equal("T", doc.GetDisplayRow(0)[4]);
                Assert.Throws<ArgumentException>(() => form.AgentAddColumn("Tag", null, "dup"));   // 이름 중복(대소문자 무시)
                form.AgentDeleteColumn(0, "AI: drop id");
                Assert.Equal(new[] { "name", "score", "flag", "tag" }, GridHeaders(form));
                Assert.Single(doc.Edits.DeletedColumns());
                typeof(Form1).GetMethod("UndoEdit", Inst)!.Invoke(form, null);
                typeof(Form1).GetMethod("UndoEdit", Inst)!.Invoke(form, null);
                Assert.Equal(new[] { "id", "name", "score", "flag" }, GridHeaders(form));
                Assert.True(doc.Edits.IsEmpty);

                // 행 삽입/삭제 진입점: 번호는 편집 후 순서(필터와 무관). 편집 뒤 재평가 작업이 끝나기를 기다린다.
                var settle = System.Diagnostics.Stopwatch.StartNew();
                while (settle.ElapsedMilliseconds < 800 || Get<bool>(form, "_busy")) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); if (settle.Elapsed.TotalSeconds > 20) break; }
                Assert.Equal(2, form.AgentInsertRows(2, 2, "AI: insert"));
                Assert.Equal(5, doc.DataRowsAvailable);
                Assert.Equal("", doc.GetDataRow(1)[0]);
                Assert.Equal("2", doc.GetDataRow(3)[0]);
                Assert.Equal(2, form.AgentDeleteRows(new long[] { 2, 3 }, "AI: delete"));
                Assert.Equal(3, doc.DataRowsAvailable);
                Assert.Throws<ArgumentException>(() => form.AgentDeleteRows(new long[] { 9 }, "x"));
                Assert.Throws<ArgumentException>(() => form.AgentInsertRows(9, 1, "x"));
                Assert.Equal(4, form.AgentInsertRows(4, 1, "AI: append"));   // 끝 다음 번호 = 맨 끝에 추가
                Assert.Equal(4, doc.DataRowsAvailable);
            });
        }

        [Fact]
        public void Address_box_syntax_jumps_and_invalid_input_reports_in_the_status_bar()
        {
            OnForm("id,name,score\n1,a,30\n2,b,10\n3,c,20\n4,d,40\n", (form, doc) =>
            {
                var grid = GridOf(form);
                Assert.True(Await(form.GoToAddressAsync("3")));
                Assert.Equal(2, grid.CurrentCell!.RowIndex);
                Assert.True(Await(form.GoToAddressAsync("R4C3")));
                Assert.Equal((3, 2), (grid.CurrentCell!.RowIndex, grid.CurrentCell.ColumnIndex));
                Assert.True(Await(form.GoToAddressAsync("C2")));                                   // 컬럼만: 행 유지
                Assert.Equal((3, 1), (grid.CurrentCell!.RowIndex, grid.CurrentCell.ColumnIndex));
                Assert.True(Await(form.GoToAddressAsync("score:2")));
                Assert.Equal((1, 2), (grid.CurrentCell!.RowIndex, grid.CurrentCell.ColumnIndex));
                Assert.True(Await(form.GoToAddressAsync("[name]1")));
                Assert.Equal((0, 1), (grid.CurrentCell!.RowIndex, grid.CurrentCell.ColumnIndex));
                var box = form.Controls.Find("cellAddressBox", true).OfType<System.Windows.Forms.TextBox>().Single();
                Assert.Contains("R1", box.Text);

                // 주소 상자가 보여 주는 글자 그대로 Enter를 눌러도 같은 셀
                Assert.True(Await(form.GoToAddressAsync(box.Text)));
                Assert.Equal((0, 1), (grid.CurrentCell!.RowIndex, grid.CurrentCell.ColumnIndex));

                foreach (string bad in new[] { "0", "99", "C9", "nosuch:2", "-3", "" })
                {
                    Assert.False(Await(form.GoToAddressAsync(bad)), bad);
                    Assert.Equal((0, 1), (grid.CurrentCell!.RowIndex, grid.CurrentCell.ColumnIndex)); // 커서는 그대로
                }

                // 필터 중에는 현재 보기에 있는 행만
                Await(((ICsvAgentHost)form).SetFilterAsync("score >= 20", true, CancellationToken.None));
                Assert.Equal(3, doc.DisplayRowCount);
                Assert.True(Await(form.GoToAddressAsync("R4")));
                Assert.Equal(2, grid.CurrentCell!.RowIndex);          // 4번 행은 보기의 3번째
                Assert.False(Await(form.GoToAddressAsync("R2")));     // 10점 행은 걸러짐
            });
        }

        [Fact]
        public void Conditional_format_paints_rules_but_edit_highlights_keep_the_background_and_rules_persist_per_file()
        {
            OnForm("id,name,score\n1,a,30\n2,b,10\n3,c,20\n", (form, doc) =>
            {
                var grid = GridOf(form);
                var paint = typeof(Form1).GetMethod("OnEditCellFormatting", Inst)!;
                System.Windows.Forms.DataGridViewCellStyle Paint(int row, int col)
                {
                    var style = new System.Windows.Forms.DataGridViewCellStyle();
                    paint.Invoke(form, new object[] { grid, new System.Windows.Forms.DataGridViewCellFormattingEventArgs(col, row, "", typeof(string), style) });
                    return style;
                }
                var red = System.Drawing.Color.FromArgb(255, 255, 0, 0);

                var rule = form.AgentAddConditionalFormat(new ConditionalFormatRule("", "high", true, ConditionalFormatKind.Expression,
                    "score >= 20", ConditionalFormatTarget.Row, null, "#FF0000", "#FFFFFF", true, null, null, null));
                Assert.Equal("cf1", rule.Id);
                Assert.Equal(red, Paint(0, 1).BackColor);                 // score 30
                Assert.NotEqual(red, Paint(1, 1).BackColor);              // score 10
                Assert.Equal(System.Drawing.Color.FromArgb(255, 255, 255, 255), Paint(0, 1).ForeColor);
                Assert.NotNull(Paint(0, 1).Font);                          // 굵게
                Assert.True(Paint(0, 1).Font!.Bold);

                var scale = form.AgentAddConditionalFormat(new ConditionalFormatRule("", "scale", true, ConditionalFormatKind.ColorScale,
                    "", ConditionalFormatTarget.Cell, "score", null, null, false, "#FFFFFF", null, "#00AA00"));
                Assert.Equal("cf2", scale.Id);
                var white = System.Drawing.Color.FromArgb(255, 255, 255, 255).ToArgb();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && Paint(1, 2).BackColor.ToArgb() != white)   // 눈금 범위는 백그라운드에서 한 번 계산된다
                {
                    System.Windows.Forms.Application.DoEvents();
                    Thread.Sleep(10);
                }
                Assert.Equal(white, Paint(1, 2).BackColor.ToArgb());                           // score 10 = 최소값 → 흰색(규칙 1에 안 걸린 행)
                Assert.Equal(System.Drawing.Color.Black.ToArgb(), Paint(1, 2).ForeColor.ToArgb()); // 밝은 배경 → 자동 검정
                Assert.Equal(red, Paint(2, 2).BackColor);                                       // score 20 = 규칙 1이 눈금보다 앞

                // 편집한 셀의 앰버가 규칙 배경보다 우선, 규칙 글자색은 유지
                doc.Edits.Set(0, 1, "edited", "a");
                var amber = Paint(0, 1);
                Assert.Contains(amber.BackColor.ToArgb(), new[]
                {
                    System.Drawing.Color.FromArgb(255, 255, 238, 186).ToArgb(), System.Drawing.Color.FromArgb(255, 96, 78, 16).ToArgb(),
                });
                Assert.Equal(System.Drawing.Color.FromArgb(255, 255, 255, 255), amber.ForeColor);

                // 저장 뷰에 자동 저장되고 열 때 불러온다
                string csv = Path.Combine(_dir, "grid.csv");
                Assert.Equal(2, SavedViewStore.LoadConditionalFormats(csv).Count);
                Assert.Equal(1, form.AgentRemoveConditionalFormat("cf1") ? 1 : 0);
                Assert.Single(SavedViewStore.LoadConditionalFormats(csv));
                Assert.Single(form.AgentListConditionalFormats());
                Assert.Equal(1, form.AgentClearConditionalFormats());
                Assert.Empty(SavedViewStore.LoadConditionalFormats(csv));
                Assert.Throws<ArgumentException>(() => form.AgentAddConditionalFormat(new ConditionalFormatRule("", "bad", true,
                    ConditionalFormatKind.Expression, "nosuchcol > 1", ConditionalFormatTarget.Row, null, "red", null, false, null, null, null)));
            });
        }

        [Fact]
        public void Rule_manager_dialog_edits_reorders_deletes_and_previews_matches()
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
                    var sample = new List<string[]> { new[] { "1", "30" }, new[] { "2", "10" }, new[] { "3", "20" }, new[] { "4", "40" } };
                    using var dlg = new ConditionalFormatDialog(new List<ConditionalFormatRule>(), new[] { "id", "score" }, new[] { "id", "score" }, sample, ThemePalette.Light);
                    _ = dlg.Handle;
                    var t = typeof(ConditionalFormatDialog);
                    T F<T>(string name) => (T)t.GetField(name, Inst)!.GetValue(dlg)!;
                    void Call(string name, params object[] args) => t.GetMethod(name, Inst)!.Invoke(dlg, args);

                    Call("AddRule"); Call("AddRule");
                    Assert.Equal(new[] { "cf1", "cf2" }, dlg.Rules.Select(r => r.Id).ToArray());
                    F<System.Windows.Forms.TextBox>("_name").Text = "first";            // 선택된 규칙(2번째, 방금 추가) 편집
                    F<System.Windows.Forms.TextBox>("_expression").Text = "score >= 20";
                    Assert.Equal(("first", "score >= 20"), (dlg.Rules[1].Name, dlg.Rules[1].Expression));

                    Call("MoveRule", -1);                                                // 위로
                    Assert.Equal(new[] { "cf2", "cf1" }, dlg.Rules.Select(r => r.Id).ToArray());
                    Assert.Equal("first", dlg.Rules[0].Name);

                    // 미리보기: 편집이 멈추면(타이머) 앞 10,000행에서 일치 수를 센다
                    var preview = F<System.Windows.Forms.Label>("_preview");
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(10) && !System.Text.RegularExpressions.Regex.IsMatch(preview.Text, @"(?<!\d)3(?!\d)"))
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(20);
                    }
                    Assert.Matches(@"(?<!\d)3(?!\d)", preview.Text);                     // score >= 20 → 30, 20, 40

                    // 잘못된 식은 미리보기에 이유를 보여 준다
                    F<System.Windows.Forms.TextBox>("_expression").Text = "score >> 2";
                    watch.Restart();
                    while (watch.Elapsed < TimeSpan.FromSeconds(10) && preview.Text.Length == 0)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(20);
                    }
                    Assert.NotEmpty(preview.Text);
                    Assert.DoesNotMatch(@"(?<!\d)3(?!\d)", preview.Text);

                    // 색상 눈금으로 바꾸면 조건 식 없이도 유효
                    F<System.Windows.Forms.ComboBox>("_kind").SelectedIndex = 1;
                    Assert.Equal(ConditionalFormatKind.ColorScale, dlg.Rules[0].Kind);
                    F<System.Windows.Forms.ComboBox>("_column").SelectedIndex = 1;
                    Assert.Equal("score", dlg.Rules[0].Column);
                    Assert.Null(ConditionalFormatRules.Validate(dlg.Rules[0], new[] { "id", "score" }));

                    Call("DeleteRule");
                    Assert.Single(dlg.Rules);
                    Assert.Equal("cf1", dlg.Rules[0].Id);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "dialog test did not complete");
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
