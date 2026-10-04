using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Agent.Tools;

namespace NanumCsvViewer.Tests
{
    // 컬럼 표시 순서(덮개의 순열): 이동·중간 삽입, 저장 결과, 되돌리기/다시 실행, 저널, 삭제·이름 변경·추가 컬럼·행 편집과의 상호작용.
    public class ColumnOrderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_colorder_" + Guid.NewGuid().ToString("N"));

        public ColumnOrderTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private async Task<VirtualCsvDocument> OpenAsync(string text, string name = "src.csv")
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
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

        private static void Move(VirtualCsvDocument doc, int from, int to, string? description = null)
            => doc.Edits.MoveColumn(from, to, doc.ColumnCount, doc.RawColumnCount, description);

        private static int Insert(VirtualCsvDocument doc, string name, int position, string? fill = null, string? description = null)
        {
            var e = doc.Edits;
            int col;
            using (e.BeginStep(description))
            {
                col = e.InsertColumn(name, position, doc.ColumnCount, doc.RawColumnCount);
                if (!string.IsNullOrEmpty(fill))
                    for (int r = 0; r < doc.DataRowsAvailable; r++) e.Set(doc.GetRowId(r), col, fill, "");
            }
            return col;
        }

        private static void DeleteCol(VirtualCsvDocument doc, int col)
            => doc.Edits.DeleteColumn(col, doc.ColumnCount, doc.RawColumnCount);

        // ------------------------------------------------------------ 이동

        [Fact]
        public async Task Move_reorders_header_rows_original_row_and_saved_csv_and_one_undo_restores_the_original_bytes()
        {
            string original = "a,b,c,d\n1,2,3,4\n5,6,7,8\n";
            using var doc = await OpenAsync(original);
            Move(doc, 0, 2, "move a");

            Assert.Equal(new[] { "b", "c", "a", "d" }, doc.Header);
            Assert.Equal(new[] { "2", "3", "1", "4" }, doc.GetDisplayRow(0));
            Assert.Equal(new[] { "b", "c", "a", "d" }, doc.OriginalHeader);
            Assert.Equal(new[] { "6", "7", "5", "8" }, doc.GetOriginalRow(doc.GetRowId(1)));
            Assert.True(doc.Edits.HasColumnOrder);
            Assert.False(doc.Edits.IsEmpty);
            Assert.Equal("move a", doc.Edits.UndoDescription);
            Assert.Equal("b,c,a,d\n2,3,1,4\n6,7,5,8\n", File.ReadAllText(Save(doc)));

            Assert.True(doc.Edits.Undo());                       // 한 단계
            Assert.False(doc.Edits.CanUndo);
            Assert.True(doc.Edits.IsEmpty);
            Assert.False(doc.Edits.HasColumnOrder);
            Assert.Equal(new[] { "a", "b", "c", "d" }, doc.Header);
            Assert.Equal(original, File.ReadAllText(Save(doc, "back.csv")));

            Assert.True(doc.Edits.Redo());
            Assert.Equal(new[] { "b", "c", "a", "d" }, doc.Header);
        }

        [Fact]
        public async Task Move_to_the_end_to_the_front_and_validation()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n");
            Move(doc, 1, 2);
            Assert.Equal(new[] { "a", "c", "b" }, doc.Header);
            Move(doc, 2, 0);
            Assert.Equal(new[] { "b", "a", "c" }, doc.Header);
            Assert.Equal(new[] { "2", "1", "3" }, doc.GetDisplayRow(0));

            int steps = 0;
            while (doc.Edits.CanUndo) { doc.Edits.Undo(); steps++; }
            Assert.Equal(2, steps);

            int notifications = 0;
            doc.Edits.Changed += () => notifications++;
            Move(doc, 1, 1);                                     // 같은 자리 = 아무 일도 없다(단계도 알림도 없음)
            Assert.Equal(0, notifications);
            Assert.False(doc.Edits.CanUndo);
            Assert.Throws<ArgumentOutOfRangeException>(() => Move(doc, 3, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Move(doc, 0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => Move(doc, -1, 1));
            Move(doc, 0, 1);
            Assert.Equal(1, notifications);                      // 한 번 이동 = 알림 한 번
        }

        [Fact]
        public async Task Moving_a_column_back_to_its_place_leaves_no_order_but_both_steps_stay_in_history()
        {
            using var doc = await OpenAsync("a,b\n1,2\n");
            Move(doc, 0, 1);
            Move(doc, 1, 0);
            Assert.False(doc.Edits.HasColumnOrder);
            Assert.True(doc.Edits.IsEmpty);
            Assert.Empty(doc.Edits.ColumnOrder());
            Assert.True(doc.Edits.Undo());
            Assert.Equal(new[] { "b", "a" }, doc.Header);
            Assert.True(doc.Edits.Undo());
            Assert.Equal(new[] { "a", "b" }, doc.Header);
        }

        [Fact]
        public async Task Cell_edits_and_renames_follow_the_column_across_moves()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n4,5,6\n");
            var e = doc.Edits;
            e.Set(0, 2, "C!", "3");                              // c
            e.SetHeader(1, "bee", doc.OriginalHeader[1]);
            Move(doc, 2, 0);                                     // c,a,bee
            Assert.Equal(new[] { "c", "a", "bee" }, doc.Header);
            Assert.Equal(new[] { "C!", "1", "2" }, doc.GetDisplayRow(0));
            Assert.True(e.TryGet(0, 0, out var v) && v == "C!"); // 같은 셀이 이제 보이는 0
            Assert.False(e.Contains(0, 2));
            Assert.True(e.TryGetHeader(2, out var h) && h == "bee");

            e.Set(1, 1, "A!", "4");                              // 이동 뒤 편집(보이는 1 = a)
            e.SetHeader(0, "see", doc.OriginalHeader[0]);        // 보이는 0 = c의 새 이름
            Assert.Equal(new[] { "see", "a", "bee" }, doc.Header);
            Assert.Equal(new[] { "c", "a", "b" }, doc.OriginalHeader);
            Assert.Equal("see,a,bee\nC!,1,2\n6,A!,5\n", File.ReadAllText(Save(doc)));

            // 이동 되돌리기는 이동 전에 만든 편집을 그대로 두고 그 뒤 편집도 같은 컬럼에 남아 있다
            while (e.CanUndo) e.Undo();
            Assert.True(e.IsEmpty);
            Assert.Equal(new[] { "a", "b", "c" }, doc.Header);
            while (e.CanRedo) e.Redo();
            Assert.Equal("see,a,bee\nC!,1,2\n6,A!,5\n", File.ReadAllText(Save(doc, "redo.csv")));
        }

        [Fact]
        public async Task Move_with_a_deleted_column_keeps_visible_positions_and_undoing_the_delete_restores_its_slot()
        {
            using var doc = await OpenAsync("a,b,c,d\n1,2,3,4\n");
            var e = doc.Edits;
            DeleteCol(doc, 1);                                   // a,c,d
            Move(doc, 0, 2);                                     // c,d,a
            Assert.Equal(new[] { "c", "d", "a" }, doc.Header);
            Assert.Equal(new[] { "3", "4", "1" }, doc.GetDisplayRow(0));
            Assert.Equal(new[] { 1 }, e.DeletedColumns());
            Assert.Equal("c,d,a\n3,4,1\n", File.ReadAllText(Save(doc)));

            e.Undo();                                            // 이동 취소 → a,c,d
            Assert.Equal(new[] { "a", "c", "d" }, doc.Header);
            e.Undo();                                            // 삭제 취소 → a,b,c,d
            Assert.Equal(new[] { "a", "b", "c", "d" }, doc.Header);
            e.Redo(); e.Redo();
            Assert.Equal(new[] { "c", "d", "a" }, doc.Header);

            // 이동한 뒤 삭제한 컬럼을 되살리면 이동한 순서 안의 원래 자리로 돌아온다
            DeleteCol(doc, 0);                                   // d,a  (c 삭제)
            Assert.Equal(new[] { "d", "a" }, doc.Header);
            e.Undo();
            Assert.Equal(new[] { "c", "d", "a" }, doc.Header);
        }

        [Fact]
        public async Task Appended_columns_join_the_end_of_the_display_order_and_can_be_moved_and_removed_by_undo()
        {
            using var doc = await OpenAsync("id,v\n1,a\n2,b\n");
            var e = doc.Edits;
            Move(doc, 1, 0);                                     // v,id
            int x = e.AppendColumn("x", doc.RawColumnCount);     // 맨 끝
            Assert.Equal(2, x);
            Assert.Equal(new[] { "v", "id", "x" }, doc.Header);
            e.Set(1, x, "X!", "");
            Move(doc, 2, 0);                                     // x,v,id
            Assert.Equal(new[] { "x", "v", "id" }, doc.Header);
            Assert.Equal(new[] { "X!", "b", "2" }, doc.GetDisplayRow(1));
            Assert.True(e.IsAppendedColumn(0));
            Assert.False(e.IsAppendedColumn(1));
            Assert.Equal("x,v,id\n,a,1\nX!,b,2\n", File.ReadAllText(Save(doc)));

            e.Undo();                                            // 이동 취소
            e.Undo();                                            // 셀
            e.Undo();                                            // 컬럼 추가
            Assert.Equal(new[] { "v", "id" }, doc.Header);
            Assert.Equal(0, e.AppendedColumnCount);
            e.Undo();
            Assert.Equal(new[] { "id", "v" }, doc.Header);
            Assert.True(e.IsEmpty);
            while (e.CanRedo) e.Redo();
            Assert.Equal(new[] { "x", "v", "id" }, doc.Header);
            Assert.Equal("x,v,id\n,a,1\nX!,b,2\n", File.ReadAllText(Save(doc, "again.csv")));
        }

        [Fact]
        public async Task Row_insert_and_delete_use_the_display_column_order()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n4,5,6\n7,8,9\n");
            var e = doc.Edits;
            Move(doc, 0, 2);                                     // b,c,a
            int added = e.AddRow(doc.GetRowId(0), doc.ColumnCount, doc.BaseRowCount);
            e.Set(added, 0, "B", "");                            // 보이는 0 = b
            e.Set(added, 2, "A", "");
            e.DeleteRows(new[] { doc.GetRowId(2) });             // 원본 두 번째 데이터 행(4,5,6) 삭제
            Assert.Equal(new[] { "B", "", "A" }, doc.GetDisplayRow(1));
            Assert.Equal("b,c,a\n2,3,1\nB,,A\n8,9,7\n", File.ReadAllText(Save(doc)));

            // 컬럼을 옮긴 뒤 추가한 행은 삭제한 컬럼이 있어도 너비가 맞는다
            DeleteCol(doc, 1);                                   // b,a
            int more = e.AddRow(added, doc.ColumnCount, doc.BaseRowCount);
            e.Set(more, 1, "A2", "");
            Assert.Equal("b,a\n2,1\nB,A\n,A2\n8,7\n", File.ReadAllText(Save(doc, "more.csv")));
        }

        [Fact]
        public async Task Short_and_long_rows_keep_every_field_in_a_reordered_table()
        {
            using var doc = await OpenAsync("a,b,c\n1\n4,5,6,EXTRA\n");
            Move(doc, 0, 2);                                     // b,c,a
            Assert.Equal(new[] { "", "", "1" }, doc.GetDisplayRow(0));       // 모자란 칸은 빈 값
            Assert.Equal(new[] { "5", "6", "4", "EXTRA" }, doc.GetDisplayRow(1)); // 헤더보다 긴 행의 남는 필드는 뒤에 보존
        }

        [Fact]
        public async Task Discarding_all_edits_clears_the_order_and_one_undo_brings_everything_back()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n4,5,6\n");
            var e = doc.Edits;
            Move(doc, 0, 2);                                     // b,c,a
            int x = e.AppendColumn("x", doc.RawColumnCount);
            e.Set(0, x, "X", "");
            DeleteCol(doc, 1);                                   // b,a,x
            Move(doc, 2, 0);                                     // x,b,a
            string[] before = doc.Header;
            int[] orderBefore = e.ColumnOrder();

            e.Clear();
            Assert.True(e.IsEmpty);
            Assert.Equal(new[] { "a", "b", "c" }, doc.Header);
            Assert.Equal("a,b,c\n1,2,3\n4,5,6\n", File.ReadAllText(Save(doc)));

            Assert.True(e.Undo());
            Assert.Equal(before, doc.Header);
            Assert.Equal(orderBefore, e.ColumnOrder());
            Assert.Equal(new[] { "X", "2", "1" }, doc.GetDisplayRow(0));
            Assert.True(e.Redo());
            Assert.True(e.IsEmpty);
        }

        [Fact]
        public async Task Sort_and_filter_use_display_positions_after_a_move()
        {
            using var doc = await OpenAsync("id,name,score\n1,a,30\n2,b,10\n3,c,20\n");
            Move(doc, 2, 0);                                     // score,id,name
            await doc.RebuildViewAsync(r => double.Parse(r[0]) >= 20, new[] { new SortKey(0, true) }, null, CancellationToken.None);
            Assert.Equal(new[] { "20", "30" }, Enumerable.Range(0, doc.DisplayRowCount).Select(i => doc.GetDisplayRow(i)[0]).ToArray());
            Assert.Equal(new[] { "c", "a" }, Enumerable.Range(0, doc.DisplayRowCount).Select(i => doc.GetDisplayRow(i)[2]).ToArray());
            var distinct = doc.DistinctValues(2, false, CancellationToken.None);
            Assert.Equal(new[] { "a", "b", "c" }, distinct.Select(d => d.Value).OrderBy(s => s).ToArray());
        }

        [Fact]
        public async Task Xlsx_export_uses_the_display_order()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n");
            Move(doc, 2, 0);
            string dest = Path.Combine(_dir, "out.xlsx");
            doc.SaveAsXlsx(dest, "S", null, CancellationToken.None);
            var sheets = Import.TabularImporter.Import(dest, Path.Combine(_dir, "imp"));
            using var back = VirtualCsvDocument.Open(sheets[0].CsvPath);
            await back.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            Assert.Equal(new[] { "c", "a", "b" }, back.Header);
            Assert.Equal(new[] { "3", "1", "2" }, back.GetDataRow(0));
        }

        // ------------------------------------------------------------ 중간 삽입

        [Fact]
        public async Task Insert_in_the_middle_with_a_fill_is_one_undo_step_and_saves_in_position()
        {
            string original = "a,b,c\n1,2,3\n4,5,6\n";
            using var doc = await OpenAsync(original);
            var e = doc.Edits;
            int col = Insert(doc, "mid", 1, fill: "M", description: "insert mid");
            Assert.Equal(1, col);
            Assert.Equal(new[] { "a", "mid", "b", "c" }, doc.Header);
            Assert.Equal(new[] { "1", "M", "2", "3" }, doc.GetDisplayRow(0));
            Assert.Equal(new[] { "a", "mid", "b", "c" }, doc.OriginalHeader);
            Assert.True(e.IsAppendedColumn(1));
            Assert.False(e.IsAppendedColumn(2));
            Assert.Equal("insert mid", e.UndoDescription);
            Assert.Equal("a,mid,b,c\n1,M,2,3\n4,M,5,6\n", File.ReadAllText(Save(doc)));

            Assert.True(e.Undo());                               // 컬럼·위치·채운 값이 한 번에 사라진다
            Assert.False(e.CanUndo);
            Assert.True(e.IsEmpty);
            Assert.Equal(original, File.ReadAllText(Save(doc, "back.csv")));
            Assert.True(e.Redo());
            Assert.Equal(new[] { "a", "mid", "b", "c" }, doc.Header);
            Assert.Equal(new[] { "4", "M", "5", "6" }, doc.GetDisplayRow(1));
        }

        [Theory]
        [InlineData(0, "n,a,b")]
        [InlineData(1, "a,n,b")]
        [InlineData(2, "a,b,n")]
        public async Task Insert_position_zero_middle_and_end(int position, string expected)
        {
            using var doc = await OpenAsync("a,b\n1,2\n");
            Assert.Equal(position, Insert(doc, "n", position));
            Assert.Equal(expected.Split(','), doc.Header);
            Assert.Equal(expected + "\n" + string.Join(",", expected.Split(',').Select(c => c switch { "a" => "1", "b" => "2", _ => "" })) + "\n",
                File.ReadAllText(Save(doc)));
        }

        [Fact]
        public async Task Insert_rejects_positions_outside_the_table_and_bad_names_without_leaving_a_step()
        {
            using var doc = await OpenAsync("a,b\n1,2\n");
            Assert.Throws<ArgumentOutOfRangeException>(() => doc.Edits.InsertColumn("n", 3, doc.ColumnCount, doc.RawColumnCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => doc.Edits.InsertColumn("n", -1, doc.ColumnCount, doc.RawColumnCount));
            Assert.Throws<ArgumentException>(() => doc.Edits.InsertColumn(" ", 1, doc.ColumnCount, doc.RawColumnCount));
            Assert.False(doc.Edits.CanUndo);
            Assert.True(doc.Edits.IsEmpty);
        }

        [Fact]
        public async Task Insert_into_a_moved_and_deleted_table_counts_positions_over_visible_columns_only()
        {
            using var doc = await OpenAsync("a,b,c,d\n1,2,3,4\n");
            DeleteCol(doc, 1);                                   // a,c,d
            Move(doc, 2, 0);                                     // d,a,c
            Insert(doc, "n", 2);                                 // d,a,n,c
            Assert.Equal(new[] { "d", "a", "n", "c" }, doc.Header);
            Assert.Equal(new[] { "4", "1", "", "3" }, doc.GetDisplayRow(0));
            Assert.Equal("d,a,n,c\n4,1,,3\n", File.ReadAllText(Save(doc)));
            doc.Edits.Undo();
            Assert.Equal(new[] { "d", "a", "c" }, doc.Header);
            doc.Edits.Undo(); doc.Edits.Undo();
            Assert.Equal(new[] { "a", "b", "c", "d" }, doc.Header);
        }

        // ------------------------------------------------------------ 저널

        [Fact]
        public async Task Journal_round_trips_the_display_order_and_old_journals_without_it_still_load()
        {
            using var doc = await OpenAsync("id,v,w\n1,a,z\n2,b,y\n");
            var e = doc.Edits;
            Insert(doc, "x", 1, fill: "Q");
            Move(doc, 0, 3);                                     // id,x,v,w → x,v,w,id
            e.Set(0, 0, "Q!", "");
            e.SetHeader(2, "vee", doc.OriginalHeader[2]);
            string[] header = doc.Header;
            var snap = e.Snapshot();
            Assert.NotNull(snap.ColumnOrder);

            string dir = Path.Combine(_dir, "journal");
            EditJournal.Write(dir, "k", "src.csv", 0, snap);
            using var doc2 = await OpenAsync("id,v,w\n1,a,z\n2,b,y\n");
            doc2.Edits.Restore(EditJournal.TryRead(dir, "k")!, doc2.BaseRowCount, doc2.ColumnCount);
            Assert.Equal(header, doc2.Header);
            Assert.Equal(doc.GetDisplayRow(0), doc2.GetDisplayRow(0));
            Assert.Equal(doc.GetDisplayRow(1), doc2.GetDisplayRow(1));
            Assert.True(doc2.Edits.IsDirty);
            Assert.False(doc2.Edits.CanUndo);                    // 복구분은 되돌릴 수 없다
            Assert.Equal(File.ReadAllText(Save(doc)), File.ReadAllText(Save(doc2, "restored.csv")));
            Assert.Equal(e.ColumnOrder(), doc2.Edits.ColumnOrder());

            // 이전 버전 저널(순서 정보 없음)
            File.WriteAllText(EditJournal.FilePath(dir, "old"), "{\"Version\":1,\"BaseRows\":-1,\"Cells\":[],\"Headers\":[],\"Deleted\":[],\"Added\":[]}");
            using var doc3 = await OpenAsync("id,v,w\n1,a,z\n");
            doc3.Edits.Restore(EditJournal.TryRead(dir, "old")!, doc3.BaseRowCount, doc3.ColumnCount);
            Assert.Equal(new[] { "id", "v", "w" }, doc3.Header);
            Assert.False(doc3.Edits.HasColumnOrder);
        }

        [Fact]
        public async Task Journal_with_an_invalid_order_is_rejected_and_changes_nothing()
        {
            using var doc = await OpenAsync("a,b,c\n1,2,3\n");
            EditSnapshot Make(int[] order) => new(-1, Array.Empty<(int, int, string)>(), Array.Empty<(int, string)>(),
                Array.Empty<int>(), Array.Empty<AddedRow>(), null, -1, null, order);
            Assert.Throws<InvalidDataException>(() => doc.Edits.Restore(Make(new[] { 0, 1 }), doc.BaseRowCount, doc.ColumnCount));        // 길이
            Assert.Throws<InvalidDataException>(() => doc.Edits.Restore(Make(new[] { 0, 1, 1 }), doc.BaseRowCount, doc.ColumnCount));     // 중복
            Assert.Throws<InvalidDataException>(() => doc.Edits.Restore(Make(new[] { 0, 1, 3 }), doc.BaseRowCount, doc.ColumnCount));     // 범위
            Assert.True(doc.Edits.IsEmpty);
            Assert.Equal(new[] { "a", "b", "c" }, doc.Header);

            doc.Edits.Restore(Make(new[] { 2, 0, 1 }), doc.BaseRowCount, doc.ColumnCount);
            Assert.Equal(new[] { "c", "a", "b" }, doc.Header);
        }

        // ------------------------------------------------------------ 모델 기반: 임의의 편집 순서

        private sealed class Col
        {
            public string Name = "", Orig = "";
            public string[] Cells = Array.Empty<string>(), OrigCells = Array.Empty<string>();
            public Col Clone() => new() { Name = Name, Orig = Orig, Cells = (string[])Cells.Clone(), OrigCells = OrigCells };
        }

        private sealed class Model
        {
            public List<Col> Cols = new();
            public Model Clone() => new() { Cols = Cols.Select(c => c.Clone()).ToList() };
            public string Csv(int rows)
            {
                var sb = new StringBuilder();
                sb.Append(string.Join(",", Cols.Select(c => c.Name))).Append('\n');
                for (int r = 0; r < rows; r++) sb.Append(string.Join(",", Cols.Select(c => c.Cells[r]))).Append('\n');
                return sb.ToString();
            }
        }

        private static void AssertMatches(VirtualCsvDocument doc, Model m, int rows, string where)
        {
            Assert.True(m.Cols.Select(c => c.Name).SequenceEqual(doc.Header), where + ": header " + string.Join("|", doc.Header) + " vs " + string.Join("|", m.Cols.Select(c => c.Name)));
            Assert.True(m.Cols.Select(c => c.Orig).SequenceEqual(doc.OriginalHeader), where + ": original header");
            for (int r = 0; r < rows; r++)
                Assert.True(m.Cols.Select(c => c.Cells[r]).SequenceEqual(doc.GetDisplayRow(r)), where + $": row {r}");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task Random_edit_sequences_match_a_model_survive_undo_redo_save_and_journal_round_trip(int seed)
        {
            const int rows = 3;
            string source = "c0,c1,c2,c3\nr0a,r0b,r0c,r0d\nr1a,r1b,r1c,r1d\nr2a,r2b,r2c,r2d\n";
            using var doc = await OpenAsync(source, $"model{seed}.csv");
            var e = doc.Edits;
            var model = new Model();
            for (int c = 0; c < 4; c++)
            {
                string[] cells = Enumerable.Range(0, rows).Select(r => $"r{r}{(char)('a' + c)}").ToArray();
                model.Cols.Add(new Col { Name = $"c{c}", Orig = $"c{c}", Cells = cells, OrigCells = cells });
            }
            AssertMatches(doc, model, rows, "initial");

            var rnd = new Random(seed * 7919);
            var history = new Stack<Model>();
            int counter = 0;
            for (int step = 0; step < 300; step++)
            {
                int n = model.Cols.Count;
                int op = rnd.Next(7);
                string what;
                if (op <= 1 && n > 1)           // 이동
                {
                    int from = rnd.Next(n), to = rnd.Next(n);
                    if (from == to) continue;
                    history.Push(model.Clone());
                    Move(doc, from, to);
                    var col = model.Cols[from]; model.Cols.RemoveAt(from); model.Cols.Insert(to, col);
                    what = $"move {from}->{to}";
                }
                else if (op == 2 && n > 1)      // 삭제
                {
                    int i = rnd.Next(n);
                    history.Push(model.Clone());
                    DeleteCol(doc, i);
                    model.Cols.RemoveAt(i);
                    what = $"delete {i}";
                }
                else if (op == 3 && n < 9)      // 삽입
                {
                    int pos = rnd.Next(n + 1);
                    string name = $"n{counter++}";
                    history.Push(model.Clone());
                    e.InsertColumn(name, pos, n, doc.RawColumnCount);
                    model.Cols.Insert(pos, new Col { Name = name, Orig = name, Cells = new string[rows].Select(_ => "").ToArray(), OrigCells = new string[rows].Select(_ => "").ToArray() });
                    what = $"insert {pos}";
                }
                else if (op == 4)               // 셀 편집
                {
                    int i = rnd.Next(n), r = rnd.Next(rows);
                    string value = $"v{counter++}";
                    history.Push(model.Clone());
                    e.Set(r, i, value, model.Cols[i].OrigCells[r]);
                    model.Cols[i].Cells[r] = value;
                    what = $"set {r},{i}";
                }
                else if (op == 5)               // 이름 변경
                {
                    int i = rnd.Next(n);
                    string name = $"h{counter++}";
                    history.Push(model.Clone());
                    e.SetHeader(i, name, model.Cols[i].Orig);
                    model.Cols[i].Name = name;
                    what = $"rename {i}";
                }
                else if (op == 6 && history.Count > 0)   // 되돌리기
                {
                    Assert.True(e.Undo());
                    model = history.Pop();
                    what = "undo";
                }
                else continue;

                AssertMatches(doc, model, rows, $"seed {seed} step {step} ({what})");
                {
                    string[] h0 = doc.Header; int[] o0 = e.ColumnOrder();
                    int undone = 0;
                    while (e.CanUndo) { e.Undo(); undone++; }
                    string[] hu = doc.Header;
                    for (int k = 0; k < undone; k++) Assert.True(e.Redo()); // 모델의 되돌리기로 이미 쌓인 다시 실행 가지는 건드리지 않는다
                    Assert.True(h0.SequenceEqual(doc.Header) && o0.SequenceEqual(e.ColumnOrder()),
                        $"seed {seed} step {step} ({what}) undo-all/redo-all: {string.Join("|", h0)} -> {string.Join("|", doc.Header)} order {string.Join(",", o0)} -> {string.Join(",", e.ColumnOrder())} (undone {undone}, after undo {string.Join("|", hu)})");
                    AssertMatches(doc, model, rows, $"seed {seed} step {step} after undo-all/redo-all");
                }
                if (step % 25 == 0)
                {
                    Assert.Equal(model.Csv(rows), File.ReadAllText(Save(doc, $"m{seed}.csv")));
                    // 저널 왕복: 새 문서에 복구하면 같은 표
                    string dir = Path.Combine(_dir, "mj" + seed);
                    EditJournal.Write(dir, "k", "src.csv", 0, e.Snapshot());
                    using var fresh = await OpenAsync(source, $"fresh{seed}.csv");
                    fresh.Edits.Restore(EditJournal.TryRead(dir, "k")!, fresh.BaseRowCount, fresh.ColumnCount);
                    AssertMatches(fresh, model, rows, $"seed {seed} step {step} journal");
                }
            }

            // 끝 상태를 기억하고 전부 되돌리면 원본 바이트, 다시 실행하면 같은 끝 상태
            string finalCsv = model.Csv(rows);
            Assert.Equal(finalCsv, File.ReadAllText(Save(doc, $"final{seed}.csv")));
            string[] finalHeader = doc.Header;
            int finalUndone = 0;
            while (e.CanUndo) { e.Undo(); finalUndone++; }
            Assert.True(e.IsEmpty);
            Assert.Equal(source, File.ReadAllText(Save(doc, $"undone{seed}.csv")));
            for (int k = 0; k < finalUndone; k++) Assert.True(e.Redo());
            Assert.Equal(finalHeader, doc.Header);
            Assert.Equal(finalCsv, File.ReadAllText(Save(doc, $"redone{seed}.csv")));
        }
    }

    // 폼 통합: 그리드 컬럼 객체 이동(폭·숨김 유지), 정렬 키·컬럼 필터·숨김·타입 지정·식 필터의 번호 대응, 에이전트 진입점.
    [Collection("SavedViewStore")]
    public class FormColumnOrderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_formcolorder_" + Guid.NewGuid().ToString("N"));
        private const System.Reflection.BindingFlags Inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        public FormColumnOrderTests() => Directory.CreateDirectory(_dir);
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

        private static void Settle(Form1 form)
        {
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (settle.ElapsedMilliseconds < 600 || Get<bool>(form, "_busy")) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(10); if (settle.Elapsed.TotalSeconds > 20) break; }
        }

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

        private static System.Windows.Forms.DataGridView GridOf(Form1 f) => f.Controls.Find("grid", true).OfType<System.Windows.Forms.DataGridView>().Single();

        // 그리드는 DisplayIndex 순서로 그린다: 컬렉션 순서(= 문서 컬럼 번호)와 화면 순서가 같아야 한다.
        private static string[] GridHeaders(Form1 f)
        {
            var cols = GridOf(f).Columns.Cast<System.Windows.Forms.DataGridViewColumn>().ToList();
            Assert.Equal(Enumerable.Range(0, cols.Count).ToArray(), cols.Select(c => c.DisplayIndex).ToArray());
            return cols.Select(c => c.HeaderText).ToArray();
        }

        private static string[] GridNames(Form1 f) => GridOf(f).Columns.Cast<System.Windows.Forms.DataGridViewColumn>().Select(c => c.Name).ToArray();

        private static void Undo(Form1 form) => typeof(Form1).GetMethod("UndoEdit", Inst)!.Invoke(form, null);
        private static void Redo(Form1 form) => typeof(Form1).GetMethod("RedoEdit", Inst)!.Invoke(form, null);

        [Fact]
        public void Moving_a_column_carries_grid_state_sort_filters_hidden_and_type_overrides_with_it()
        {
            OnForm("id,name,score,flag\n1,a,30,x\n2,b,10,y\n3,c,20,z\n", (form, doc) =>
            {
                var grid = GridOf(form);
                var hidden = Get<HashSet<int>>(form, "_hiddenColumns");
                var sort = Get<List<SortKey>>(form, "_sortKeys");
                var filters = Get<ColumnFilterState>(form, "_columnFilters");
                var types = Get<Dictionary<int, ColumnValueType>>(form, "_manualTypeOverrides");

                grid.Columns[3].Visible = false; hidden.Add(3);                  // flag 숨김
                grid.Columns[2].Width = 217;                                     // score 폭
                sort.Add(new SortKey(2, true));                                  // score 오름차순
                Pump(doc.SortAsync(sort, null, CancellationToken.None));
                filters.SetNumericRange(2, 15, null);                            // score >= 15
                types[1] = ColumnValueType.String;                               // name을 문자열로 지정
                grid.CurrentCell = grid[2, 1];

                Assert.Equal("name", form.AgentMoveColumn(1, 3, "AI: move name"));  // id,score,flag,name
                Assert.Equal(new[] { "id", "score", "flag", "name" }, GridHeaders(form));
                Assert.Equal(new[] { "col0", "col2", "col3", "col1" }, GridNames(form));
                Assert.Equal(new[] { "id", "score", "flag", "name" }, doc.Header);
                Assert.Equal(new[] { 2 }, hidden.ToArray());                      // flag는 이제 2번
                Assert.False(grid.Columns[2].Visible);
                Assert.True(grid.Columns[1].Visible && grid.Columns[3].Visible);
                Assert.Equal(217, grid.Columns[1].Width);                         // score의 폭이 그대로
                Assert.Equal(new SortKey(1, true), Assert.Single(sort));          // score는 이제 1번
                Assert.Equal(1, Assert.Single(filters.NumericFilters).Column);
                Assert.Equal(ColumnValueType.String, types[3]);                   // name은 이제 3번
                Assert.Single(types);
                Assert.Equal("AI: move name", doc.Edits.UndoDescription);
                Assert.Equal(3, grid.CurrentCell!.ColumnIndex);                   // 옮긴 컬럼의 셀이 선택된다

                Settle(form);
                Assert.Equal(2, doc.DisplayRowCount);                             // 필터(score >= 15)와 정렬이 새 번호로 다시 계산되어 유지된다
                Assert.Equal("20", doc.GetDisplayRow(0)[1]);
                Assert.Equal("30", doc.GetDisplayRow(1)[1]);
                Undo(form);
                Settle(form);
                Assert.Equal(new[] { "id", "name", "score", "flag" }, GridHeaders(form));
                Assert.Equal(new[] { 3 }, hidden.ToArray());
                Assert.Equal(new SortKey(2, true), Assert.Single(sort));
                Assert.Equal(2, Assert.Single(filters.NumericFilters).Column);
                Assert.Equal(ColumnValueType.String, types[1]);
                Assert.Equal(217, grid.Columns[2].Width);
                Assert.True(doc.Edits.IsEmpty);

                Redo(form);
                Assert.Equal(new[] { "id", "score", "flag", "name" }, GridHeaders(form));
                Assert.Equal(new[] { 2 }, hidden.ToArray());
            });
        }

        [Fact]
        public void An_expression_filter_follows_its_column_and_the_view_keeps_the_same_rows_after_a_move()
        {
            OnForm("id,name,score\n1,a,30\n2,b,10\n3,c,20\n4,d,40\n", (form, doc) =>
            {
                Await(((ICsvAgentHost)form).SetFilterAsync("score >= 20", true, CancellationToken.None));
                Assert.Equal(3, doc.DisplayRowCount);

                form.AgentMoveColumn(2, 0, "AI: move score");                    // score,id,name
                Settle(form);
                Assert.Equal(new[] { "score", "id", "name" }, doc.Header);
                Assert.Equal(3, doc.DisplayRowCount);
                Assert.Equal(new[] { "30", "20", "40" }, Enumerable.Range(0, doc.DisplayRowCount).Select(i => doc.GetDisplayRow(i)[0]).ToArray());

                Undo(form);
                Settle(form);
                Assert.Equal(3, doc.DisplayRowCount);
                Assert.Equal(new[] { "id", "name", "score" }, doc.Header);
            });
        }

        [Fact]
        public void Agent_insert_places_the_column_in_the_middle_with_one_undo_and_rejects_bad_arguments()
        {
            OnForm("id,name,score\n1,a,30\n2,b,10\n", (form, doc) =>
            {
                var grid = GridOf(form);
                var hidden = Get<HashSet<int>>(form, "_hiddenColumns");
                grid.Columns[2].Visible = false; hidden.Add(2);                  // score 숨김

                Assert.Equal(1, form.AgentInsertColumn("mid", 1, "M", "AI: insert mid"));
                Assert.Equal(new[] { "id", "mid", "name", "score" }, GridHeaders(form));
                Assert.Equal(new[] { "id", "mid", "name", "score" }, doc.Header);
                Assert.Equal(new[] { "1", "M", "a", "30" }, doc.GetDisplayRow(0));
                Assert.Equal(new[] { "col0", "col3", "col1", "col2" }, GridNames(form));
                Assert.Equal(new[] { 3 }, hidden.ToArray());                      // 숨긴 score는 이제 3번
                Assert.False(grid.Columns[3].Visible);
                Assert.True(grid.Columns[1].Visible);
                Assert.Equal("AI: insert mid", doc.Edits.UndoDescription);

                Assert.Equal(4, form.AgentInsertColumn("last", 4, null, "AI: insert last"));
                Assert.Equal(0, form.AgentInsertColumn("first", 0, "F", "AI: insert first"));
                Assert.Equal(new[] { "first", "id", "mid", "name", "score", "last" }, GridHeaders(form));
                Assert.Equal(new[] { 4 }, hidden.ToArray());

                Assert.Throws<ArgumentException>(() => form.AgentInsertColumn("bad", 7, null, "x"));
                Assert.Throws<ArgumentException>(() => form.AgentInsertColumn("bad", -1, null, "x"));
                Assert.Throws<ArgumentException>(() => form.AgentInsertColumn("MID", 1, null, "x"));   // 이름 중복(대소문자 무시)
                Assert.Throws<ArgumentException>(() => form.AgentInsertColumn("  ", 1, null, "x"));
                Assert.Throws<ArgumentException>(() => form.AgentMoveColumn(0, 0, "x"));
                Assert.Throws<ArgumentException>(() => form.AgentMoveColumn(0, 6, "x"));
                Assert.Throws<ArgumentException>(() => form.AgentMoveColumn(-1, 1, "x"));
                Assert.Equal(new[] { "first", "id", "mid", "name", "score", "last" }, GridHeaders(form));

                Undo(form); Undo(form); Undo(form);
                Assert.Equal(new[] { "id", "name", "score" }, GridHeaders(form));
                Assert.Equal(new[] { 2 }, hidden.ToArray());
                Assert.False(grid.Columns[2].Visible);
                Assert.True(doc.Edits.IsEmpty);
            });
        }

        [Fact]
        public void Move_left_and_right_skip_hidden_columns_and_each_move_is_one_undo_step()
        {
            OnForm("a,b,c,d\n1,2,3,4\n", (form, doc) =>
            {
                var grid = GridOf(form);
                var hidden = Get<HashSet<int>>(form, "_hiddenColumns");
                typeof(Form1).GetField("_sheetEditing", Inst)!.SetValue(form, true);
                grid.Columns[1].Visible = false; hidden.Add(1);                  // b 숨김
                grid.CurrentCell = grid[0, 0];

                typeof(Form1).GetMethod("MoveCurrentColumn", Inst)!.Invoke(form, new object[] { +1 }); // a는 보이는 다음 컬럼 c 자리로
                Assert.Equal(new[] { "b", "c", "a", "d" }, doc.Header);
                Assert.Equal(2, grid.CurrentCell!.ColumnIndex);
                Assert.Equal(new[] { 0 }, hidden.ToArray());                      // b는 맨 앞
                typeof(Form1).GetMethod("MoveCurrentColumn", Inst)!.Invoke(form, new object[] { +1 });
                Assert.Equal(new[] { "b", "c", "d", "a" }, doc.Header);
                typeof(Form1).GetMethod("MoveCurrentColumn", Inst)!.Invoke(form, new object[] { +1 }); // 더 갈 곳 없음
                Assert.Equal(new[] { "b", "c", "d", "a" }, doc.Header);
                typeof(Form1).GetMethod("MoveCurrentColumn", Inst)!.Invoke(form, new object[] { -1 });
                Assert.Equal(new[] { "b", "c", "a", "d" }, doc.Header);

                Undo(form);
                Settle(form);
                Assert.Equal(new[] { "b", "c", "d", "a" }, doc.Header);
                Undo(form);
                Undo(form);
                Assert.Equal(new[] { "a", "b", "c", "d" }, doc.Header);
                Assert.True(doc.Edits.IsEmpty);
            });
        }
    }
}
