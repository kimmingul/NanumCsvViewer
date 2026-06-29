using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class GridCopyFormatterTests
    {
        private static readonly string[][] Rows =
        {
            new[] { "A1", "B1", "C1" },
            new[] { "A2", "B2", "C2" },
        };

        [Fact]
        public void Copies_rectangle_as_tsv()
        {
            var selection = new HashSet<(int, int)> { (0, 1), (0, 2), (1, 1), (1, 2) };
            string tsv = GridCopyFormatter.SelectedCellsTsv(selection, r => Rows[r]);
            Assert.Equal("B1\tC1\nB2\tC2\n", tsv);
        }

        [Fact]
        public void Sparse_selection_leaves_gaps_empty()
        {
            // (0,0) 과 (1,2) 만 선택 → 경계 사각형 2x3, 나머지는 빈칸
            var selection = new HashSet<(int, int)> { (0, 0), (1, 2) };
            string tsv = GridCopyFormatter.SelectedCellsTsv(selection, r => Rows[r]);
            Assert.Equal("A1\t\t\n\t\tC2\n", tsv);
        }

        [Fact]
        public void Empty_selection_is_empty_string()
        {
            Assert.Equal("", GridCopyFormatter.SelectedCellsTsv(new HashSet<(int, int)>(), r => Rows[r]));
        }

        [Fact]
        public void Row_tsv_uses_visible_column_order()
        {
            // 컬럼 2,0 순서만
            Assert.Equal("C1\tA1\n", GridCopyFormatter.RowTsv(Rows[0], new[] { 2, 0 }));
        }

        [Fact]
        public void Column_tsv_includes_header_first()
        {
            string tsv = GridCopyFormatter.ColumnTsv("col", new[] { "x", "y" });
            Assert.Equal("col\nx\ny\n", tsv);
        }

        [Fact]
        public void Tabs_and_newlines_in_cells_become_spaces()
        {
            var selection = new HashSet<(int, int)> { (0, 0) };
            string tsv = GridCopyFormatter.SelectedCellsTsv(selection, _ => new[] { "a\tb\nc" });
            Assert.Equal("a b c\n", tsv);
        }
    }
}
