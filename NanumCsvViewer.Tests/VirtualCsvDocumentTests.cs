using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class VirtualCsvDocumentTests : IDisposable
    {
        private readonly string _path;

        public VirtualCsvDocumentTests()
        {
            _path = Path.Combine(Path.GetTempPath(), "nanumcsv_test_" + Guid.NewGuid().ToString("N") + ".csv");
        }

        public void Dispose()
        {
            try { File.Delete(_path); } catch { /* 무시 */ }
        }

        private async Task<VirtualCsvDocument> OpenIndexedAsync(string content)
        {
            File.WriteAllText(_path, content, new UTF8Encoding(false));
            var doc = VirtualCsvDocument.Open(_path);
            await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            return doc;
        }

        [Fact]
        public async Task Reads_header_and_rows()
        {
            using var doc = await OpenIndexedAsync("name,age,city\nAlice,30,NY\nBob,25,LA\n");
            Assert.Equal(new[] { "name", "age", "city" }, doc.Header);
            Assert.Equal(3, doc.ColumnCount);
            Assert.Equal(2, doc.DataRowsAvailable);
            Assert.Equal(new[] { "Alice", "30", "NY" }, doc.GetDisplayRow(0));
            Assert.Equal(new[] { "Bob", "25", "LA" }, doc.GetDisplayRow(1));
            Assert.Equal(1, doc.GetSourceRowNumber(0));
        }

        [Fact]
        public async Task Filter_narrows_visible_rows_and_preserves_source_numbers()
        {
            using var doc = await OpenIndexedAsync("name,city\nAlice,NY\nBob,LA\nCarol,NY\n");
            await doc.ApplyFilterAsync(r => r.Length > 1 && r[1] == "NY", null, CancellationToken.None);

            Assert.True(doc.IsFiltered);
            Assert.Equal(2, doc.DisplayRowCount);
            Assert.Equal("Alice", doc.GetDisplayRow(0)[0]);
            Assert.Equal("Carol", doc.GetDisplayRow(1)[0]);
            Assert.Equal(3, doc.GetSourceRowNumber(1)); // Carol = 원본 3행

            doc.ClearView();
            Assert.False(doc.IsFiltered);
            Assert.Equal(3, doc.DisplayRowCount);
        }

        [Fact]
        public async Task Numeric_sort_uses_value_order_not_lexicographic()
        {
            using var doc = await OpenIndexedAsync("n\n2\n10\n1\n");
            await doc.SortAsync(0, ascending: true, null, CancellationToken.None);
            Assert.Equal("1", doc.GetDisplayRow(0)[0]);
            Assert.Equal("2", doc.GetDisplayRow(1)[0]);
            Assert.Equal("10", doc.GetDisplayRow(2)[0]); // 사전식이면 "10"<"2"지만 수치 정렬이면 맨 끝
        }

        [Fact]
        public async Task Descending_sort_reverses_order()
        {
            using var doc = await OpenIndexedAsync("n\napple\ncherry\nbanana\n");
            await doc.SortAsync(0, ascending: false, null, CancellationToken.None);
            Assert.Equal(new[] { "cherry", "banana", "apple" },
                new[] { doc.GetDisplayRow(0)[0], doc.GetDisplayRow(1)[0], doc.GetDisplayRow(2)[0] });
        }

        [Fact]
        public async Task Sort_is_stable_for_equal_keys()
        {
            // key 컬럼이 모두 같으면 원래(파일) 순서가 유지되어야 함
            using var doc = await OpenIndexedAsync("key,id\nx,1\nx,2\nx,3\n");
            await doc.SortAsync(0, ascending: true, null, CancellationToken.None);
            Assert.Equal(new[] { "1", "2", "3" },
                new[] { doc.GetDisplayRow(0)[1], doc.GetDisplayRow(1)[1], doc.GetDisplayRow(2)[1] });
        }

        [Fact]
        public async Task Multi_column_sort_orders_by_priority()
        {
            // 1차: dept 오름차순, 2차: age 내림차순
            using var doc = await OpenIndexedAsync(
                "dept,age\nB,30\nA,40\nB,20\nA,25\n");
            await doc.SortAsync(
                new[] { new SortKey(0, true), new SortKey(1, false) }, null, CancellationToken.None);

            // A40, A25, B30, B20 순서여야 함
            Assert.Equal(new[] { "A", "A", "B", "B" },
                new[] { doc.GetDisplayRow(0)[0], doc.GetDisplayRow(1)[0], doc.GetDisplayRow(2)[0], doc.GetDisplayRow(3)[0] });
            Assert.Equal(new[] { "40", "25", "30", "20" },
                new[] { doc.GetDisplayRow(0)[1], doc.GetDisplayRow(1)[1], doc.GetDisplayRow(2)[1], doc.GetDisplayRow(3)[1] });
        }

        [Fact]
        public async Task Secondary_key_only_breaks_primary_ties()
        {
            // 1차 dept 오름차순, 2차 name 오름차순 — 1차가 다르면 2차는 영향 없음
            using var doc = await OpenIndexedAsync(
                "dept,name\nB,zoe\nA,tom\nA,amy\n");
            await doc.SortAsync(
                new[] { new SortKey(0, true), new SortKey(1, true) }, null, CancellationToken.None);

            Assert.Equal(new[] { "amy", "tom", "zoe" },
                new[] { doc.GetDisplayRow(0)[1], doc.GetDisplayRow(1)[1], doc.GetDisplayRow(2)[1] });
        }

        [Fact]
        public async Task ResetViewOrder_restores_file_order_after_sort()
        {
            using var doc = await OpenIndexedAsync("n\n3\n1\n2\n");
            await doc.SortAsync(0, ascending: true, null, CancellationToken.None);
            doc.ResetViewOrder(); // 정렬만 했어도 뷰맵이 존재 → 파일 순서로 복원
            Assert.Equal(new[] { "3", "1", "2" },
                new[] { doc.GetDisplayRow(0)[0], doc.GetDisplayRow(1)[0], doc.GetDisplayRow(2)[0] });
        }
    }
}
