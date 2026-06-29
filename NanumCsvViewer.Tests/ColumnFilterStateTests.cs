using System.Text.Json;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class ColumnFilterStateTests
    {
        [Fact]
        public void Selected_values_predicate_matches_set()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "서울", "부산" }, includeBlanks: false);
            var p = s.Predicate();
            Assert.True(p(new[] { "서울" }));
            Assert.True(p(new[] { "부산" }));
            Assert.False(p(new[] { "대구" }));
            Assert.False(p(new[] { "" }));
        }

        [Fact]
        public void Include_blanks_matches_empty()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "서울" }, includeBlanks: true);
            var p = s.Predicate();
            Assert.True(p(new[] { "" }));
            Assert.True(p(new[] { "서울" }));
            Assert.False(p(new[] { "부산" }));
        }

        [Fact]
        public void Date_range_is_inclusive()
        {
            var s = new ColumnFilterState();
            s.SetDateRange(0, new DateTime(2024, 1, 1), new DateTime(2024, 1, 31));
            var p = s.Predicate();
            Assert.True(p(new[] { "2024-01-01" }));
            Assert.True(p(new[] { "2024-01-31" }));
            Assert.True(p(new[] { "2024-01-15" }));
            Assert.False(p(new[] { "2023-12-31" }));
            Assert.False(p(new[] { "2024-02-01" }));
        }

        [Fact]
        public void Date_range_open_ended()
        {
            var s = new ColumnFilterState();
            s.SetDateRange(0, new DateTime(2024, 1, 1), null);
            var p = s.Predicate();
            Assert.True(p(new[] { "2024-06-01" }));
            Assert.False(p(new[] { "2023-12-31" }));
        }

        [Fact]
        public void Unparseable_date_does_not_match_range()
        {
            var s = new ColumnFilterState();
            s.SetDateRange(0, new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));
            Assert.False(s.Predicate()(new[] { "not a date" }));
        }

        [Fact]
        public void Multiple_column_filters_combine_with_and()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "A" }, false);
            s.SetValues(1, new[] { "X" }, false);
            var p = s.Predicate();
            Assert.True(p(new[] { "A", "X" }));
            Assert.False(p(new[] { "A", "Y" }));
            Assert.False(p(new[] { "B", "X" }));
        }

        [Fact]
        public void Same_column_value_and_date_are_mutually_exclusive()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "A" }, false);
            s.SetDateRange(0, new DateTime(2024, 1, 1), null); // 같은 컬럼 → 값 필터 대체
            Assert.Empty(s.ValueFilters);
            Assert.Single(s.DateFilters);
        }

        [Fact]
        public void Remove_and_clear()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "A" }, false);
            s.SetValues(1, new[] { "X" }, false);
            s.Remove(0);
            Assert.False(s.HasFilterFor(0));
            Assert.True(s.HasFilterFor(1));
            s.Clear();
            Assert.True(s.IsEmpty);
        }

        [Fact]
        public void Round_trips_through_json()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "서울", "부산" }, includeBlanks: true);
            s.SetDateRange(2, new DateTime(2024, 1, 1), new DateTime(2024, 12, 31));

            string json = JsonSerializer.Serialize(s);
            var loaded = JsonSerializer.Deserialize<ColumnFilterState>(json)!;

            Assert.Single(loaded.ValueFilters);
            Assert.Equal(2, loaded.ValueFilters[0].Values.Count);
            Assert.True(loaded.ValueFilters[0].IncludeBlanks);
            Assert.Single(loaded.DateFilters);
            Assert.Equal(new DateTime(2024, 12, 31), loaded.DateFilters[0].End);
        }
    }
}
