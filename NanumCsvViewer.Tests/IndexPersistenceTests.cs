using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class IndexPersistenceTests
    {
        [Fact]
        public void RecordIndex_snapshot_and_bulkload_round_trip()
        {
            var original = new RecordIndex();
            long[] offsets = { 0, 17, 42, 100, 1 << 21, (1L << 21) + 5 }; // 세그먼트 경계 넘김
            foreach (long o in offsets) original.Add(o);
            original.Publish();

            var snapshot = original.SnapshotOffsets();
            Assert.Equal(offsets, snapshot);

            var loaded = new RecordIndex();
            loaded.BulkLoad(snapshot);
            Assert.Equal(original.Count, loaded.Count);
            for (long i = 0; i < loaded.Count; i++)
                Assert.Equal(original[i], loaded[i]);
        }

        [Fact]
        public void IndexCache_save_then_load_with_matching_metadata()
        {
            string csvPath = Path.Combine(Path.GetTempPath(), "ncv_cache_" + Guid.NewGuid().ToString("N") + ".csv");
            var index = new RecordIndex();
            long[] offsets = { 0, 10, 20, 35 };
            foreach (long o in offsets) index.Add(o);
            index.Publish();

            var when = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            IndexCache.Save(csvPath, fileSize: 1234, lastWriteUtc: when, encodingName: "UTF-8", delimiter: (byte)',', index);

            var loaded = IndexCache.TryLoad(csvPath, 1234, when, "UTF-8", (byte)',');
            Assert.NotNull(loaded);
            Assert.Equal(offsets, loaded);

            // 메타데이터 불일치 → 캐시 거부
            Assert.Null(IndexCache.TryLoad(csvPath, 9999, when, "UTF-8", (byte)','));
            Assert.Null(IndexCache.TryLoad(csvPath, 1234, when, "CP949", (byte)','));
        }

        [Fact]
        public void SavedView_round_trip()
        {
            string csvPath = Path.Combine(Path.GetTempPath(), "ncv_view_" + Guid.NewGuid().ToString("N") + ".csv");
            var query = new CsvSearchQuery("abc", CsvSearchMode.Fuzzy, 2);
            var view = SavedCsvView.Create("v1", "seoul", 1,
                new[] { new SortKey(0, true), new SortKey(2, false) },
                new[] { 3, 5 }, query, currentColumn: 2);

            SavedViewStore.Save(csvPath, view);
            var loaded = SavedViewStore.Load(csvPath);

            Assert.NotNull(loaded);
            Assert.Equal("seoul", loaded!.FilterText);
            Assert.Equal(1, loaded.FilterColumn);
            Assert.Equal(2, loaded.Sort.Count);
            Assert.Equal(0, loaded.Sort[0].Column);
            Assert.True(loaded.Sort[0].Ascending);
            Assert.Equal(new[] { 3, 5 }, loaded.HiddenColumnIndexes);
            Assert.Equal("abc", loaded.SearchText);
            Assert.Equal(CsvSearchMode.Fuzzy, loaded.SearchMode);
            Assert.Equal(2, loaded.SearchColumn);
        }
    }
}
