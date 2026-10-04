using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Import;

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
    }
}
