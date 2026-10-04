using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 셀 편집 덮개·저장: 원본 불변, 선행 0 보존, 편집 안 한 행은 바이트 동일.
    public class CellEditTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nanum_edit_" + Guid.NewGuid().ToString("N"));

        public CellEditTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private async Task<(VirtualCsvDocument Doc, string Path)> OpenAsync(byte[] bytes)
        {
            string path = Path.Combine(_dir, "src.csv");
            File.WriteAllBytes(path, bytes);
            var doc = VirtualCsvDocument.Open(path);
            await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            return (doc, path);
        }

        private static byte[] Utf8(string s, bool bom = false) =>
            (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(s)).ToArray();

        [Fact]
        public async Task Edit_is_visible_to_every_read_path_and_keeps_leading_zeros()
        {
            var (doc, _) = await OpenAsync(Utf8("id,code\n1,5\n2,6\n3,7\n"));
            using (doc)
            {
                doc.Edits.Set(1, 1, "001", "6");
                Assert.Equal("001", doc.GetDataRow(1)[1]);
                Assert.Equal("001", doc.GetDataRowUncached(1)[1]);
                Assert.Equal("001", doc.SnapshotViewRows()[1][1]);
                Assert.Equal("6", doc.GetOriginalRow(1)[1]);
                Assert.Equal("5", doc.GetDataRow(0)[1]);
            }
        }

        [Fact]
        public async Task Setting_the_original_value_removes_the_edit()
        {
            var (doc, _) = await OpenAsync(Utf8("a,b\n1,2\n"));
            using (doc)
            {
                doc.Edits.Set(0, 1, "x", "2");
                Assert.False(doc.Edits.IsEmpty);
                doc.Edits.Set(0, 1, "2", "2");
                Assert.True(doc.Edits.IsEmpty);
                Assert.Equal("2", doc.GetDataRow(0)[1]);
            }
        }

        [Fact]
        public async Task Save_writes_a_new_file_and_never_touches_the_original()
        {
            byte[] original = Utf8("id,code\r\n1,5\r\n2,6\r\n3,7\r\n");
            var (doc, path) = await OpenAsync(original);
            using (doc)
            {
                doc.Edits.Set(1, 1, "007", "6");
                string dest = Path.Combine(_dir, "out.csv");
                doc.SaveWithEdits(dest, null, CancellationToken.None);
                Assert.Equal(original, File.ReadAllBytes(path));
                Assert.Equal("id,code\r\n1,5\r\n2,007\r\n3,7\r\n", File.ReadAllText(dest));
                Assert.Throws<InvalidOperationException>(() => doc.SaveWithEdits(path, null, CancellationToken.None));
                Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
            }
        }

        [Fact]
        public async Task Untouched_rows_are_byte_identical_and_bom_header_survive()
        {
            // BOM + 따옴표 안 줄바꿈/쉼표가 있는 편집 안 한 행 + 마지막 행 줄바꿈 없음.
            byte[] original = Utf8("a,b\n\"x,1\",\"line1\nline2\"\n2,\"q\"\"q\"\n3,end", bom: true);
            var (doc, _) = await OpenAsync(original);
            using (doc)
            {
                doc.Edits.Set(2, 1, "last, \"edited\"", "end");
                string dest = Path.Combine(_dir, "o.csv");
                doc.SaveWithEdits(dest, null, CancellationToken.None);
                byte[] saved = File.ReadAllBytes(dest);
                int editedStart = Encoding.UTF8.GetByteCount("a,b\n\"x,1\",\"line1\nline2\"\n2,\"q\"\"q\"\n") + 3;
                Assert.Equal(original.Take(editedStart), saved.Take(editedStart)); // BOM·헤더·앞 행 바이트 동일
                Assert.Equal("3,\"last, \"\"edited\"\"\"", Encoding.UTF8.GetString(saved, editedStart, saved.Length - editedStart));
            }
        }

        [Fact]
        public async Task Round_trip_through_a_reopened_file_preserves_text_cells()
        {
            var (doc, _) = await OpenAsync(Utf8("id,zip\n1,10\n2,20\n"));
            string dest = Path.Combine(_dir, "re.csv");
            using (doc)
            {
                doc.Edits.Set(0, 1, "00123", "10");
                doc.Edits.Set(1, 1, " 04 ", "20");
                doc.SaveWithEdits(dest, null, CancellationToken.None);
            }
            using var reopened = VirtualCsvDocument.Open(dest);
            await reopened.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
            Assert.Equal("00123", reopened.GetDataRow(0)[1]);
            Assert.Equal(" 04 ", reopened.GetDataRow(1)[1]);
        }

        [Fact]
        public void Quoting_rules_do_not_reinterpret_values()
        {
            Assert.Equal("001", CellEdits.QuoteField("001", ','));
            Assert.Equal("\"a,b\"", CellEdits.QuoteField("a,b", ','));
            Assert.Equal("\"a;b\"", CellEdits.QuoteField("a;b", ';'));
            Assert.Equal("\"\"\"q\"\"\"", CellEdits.QuoteField("\"q\"", ','));
            Assert.Equal("", CellEdits.QuoteField("", ','));
        }
    }
}
