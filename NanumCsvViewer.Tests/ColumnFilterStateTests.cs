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

        // ----- 숫자 범위 -----

        [Fact]
        public void Numeric_range_is_inclusive()
        {
            var s = new ColumnFilterState();
            s.SetNumericRange(0, 10, 20);
            var p = s.Predicate();
            Assert.True(p(new[] { "10" }));
            Assert.True(p(new[] { "15" }));
            Assert.True(p(new[] { "20" }));
            Assert.False(p(new[] { "9" }));
            Assert.False(p(new[] { "21" }));
            Assert.False(p(new[] { "abc" }));   // 숫자 아님 → 제외
        }

        [Fact]
        public void Numeric_range_open_ended()
        {
            var s = new ColumnFilterState();
            s.SetNumericRange(0, 10, null);
            var p = s.Predicate();
            Assert.True(p(new[] { "100" }));
            Assert.False(p(new[] { "9" }));
        }

        // ----- 텍스트 술어 -----

        [Fact]
        public void Text_contains_is_case_insensitive_by_default()
        {
            var s = new ColumnFilterState();
            s.SetText(0, TextFilterOp.Contains, "seo", caseSensitive: false);
            var p = s.Predicate();
            Assert.True(p(new[] { "Seoul" }));
            Assert.False(p(new[] { "Busan" }));
        }

        [Fact]
        public void Text_equals_and_starts_with()
        {
            var eq = new ColumnFilterState();
            eq.SetText(0, TextFilterOp.Equals, "A", caseSensitive: false);
            Assert.True(eq.Predicate()(new[] { "a" }));
            Assert.False(eq.Predicate()(new[] { "ab" }));

            var sw = new ColumnFilterState();
            sw.SetText(0, TextFilterOp.StartsWith, "user", caseSensitive: false);
            Assert.True(sw.Predicate()(new[] { "USER_01" }));
            Assert.False(sw.Predicate()(new[] { "admin" }));
        }

        [Fact]
        public void Text_regex()
        {
            var s = new ColumnFilterState();
            s.SetText(0, TextFilterOp.Regex, @"^0\d+$", caseSensitive: false);
            var p = s.Predicate();
            Assert.True(p(new[] { "012" }));
            Assert.False(p(new[] { "a12" }));
        }

        [Fact]
        public void Text_blank_and_not_blank()
        {
            var blank = new ColumnFilterState();
            blank.SetText(0, TextFilterOp.IsBlank, "", false);
            Assert.True(blank.Predicate()(new[] { "" }));
            Assert.False(blank.Predicate()(new[] { "x" }));

            var notBlank = new ColumnFilterState();
            notBlank.SetText(0, TextFilterOp.IsNotBlank, "", false);
            Assert.False(notBlank.Predicate()(new[] { "" }));
            Assert.True(notBlank.Predicate()(new[] { "x" }));
        }

        // ----- 시간 정밀도 범위 -----

        [Fact]
        public void Datetime_range_respects_time_of_day()
        {
            var s = new ColumnFilterState();
            s.SetDateRange(0, new DateTime(2024, 1, 1, 9, 0, 0), new DateTime(2024, 1, 1, 17, 0, 0),
                TemporalFilterKind.DateTime);
            var p = s.Predicate();
            Assert.False(p(new[] { "2024-01-01 08:00" }));
            Assert.True(p(new[] { "2024-01-01 12:00" }));
            Assert.False(p(new[] { "2024-01-01 18:00" }));
        }

        [Fact]
        public void Time_range_compares_time_of_day_only()
        {
            var s = new ColumnFilterState();
            s.SetDateRange(0, new DateTime(2024, 1, 1, 9, 0, 0), new DateTime(2024, 1, 1, 17, 0, 0),
                TemporalFilterKind.Time);
            var p = s.Predicate();
            Assert.False(p(new[] { "08:00" }));
            Assert.True(p(new[] { "12:00" }));
            Assert.False(p(new[] { "18:00:00" }));
        }

        [Fact]
        public void Different_filter_kinds_on_same_column_are_mutually_exclusive()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "A" }, false);
            s.SetNumericRange(0, 1, 10);   // 같은 컬럼 → 값 필터 대체
            Assert.Empty(s.ValueFilters);
            Assert.Single(s.NumericFilters);
            s.SetText(0, TextFilterOp.Contains, "x", false);
            Assert.Empty(s.NumericFilters);
            Assert.Single(s.TextFilters);
        }

        [Fact]
        public void Describe_entries_maps_each_filter_to_its_column()
        {
            var s = new ColumnFilterState();
            s.SetValues(0, new[] { "A" }, false);
            s.SetNumericRange(1, 0, 10);
            s.SetText(2, TextFilterOp.Contains, "x", false);
            var entries = s.DescribeEntries(new[] { "c0", "c1", "c2" }).ToList();
            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, e => e.Column == 0);
            Assert.Contains(entries, e => e.Column == 1);
            Assert.Contains(entries, e => e.Column == 2);
            // 설명 문자열은 DescribeEntries를 그대로 투영한다.
            Assert.Equal(entries.Select(e => e.Text), s.Descriptions(new[] { "c0", "c1", "c2" }));
        }

        [Fact]
        public void Copy_from_restores_all_filter_kinds()
        {
            var src = new ColumnFilterState();
            src.SetValues(0, new[] { "A" }, true);
            src.SetDateRange(1, new DateTime(2024, 1, 1, 9, 0, 0), new DateTime(2024, 1, 1, 17, 0, 0), TemporalFilterKind.DateTime);
            src.SetNumericRange(2, 0, 100);
            src.SetText(3, TextFilterOp.Contains, "x", true);

            var dst = new ColumnFilterState();
            dst.SetValues(9, new[] { "old" }, false); // 기존 내용은 대체되어야 함
            dst.CopyFrom(src);

            Assert.Single(dst.ValueFilters);
            Assert.Single(dst.DateFilters);
            Assert.Equal(TemporalFilterKind.DateTime, dst.DateFilters[0].Kind); // 정밀도 보존
            Assert.Single(dst.NumericFilters);
            Assert.Single(dst.TextFilters);
            Assert.False(dst.HasFilterFor(9));
        }

        [Fact]
        public void Text_in_list_matches_any_token()
        {
            var s = new ColumnFilterState();
            s.SetText(0, TextFilterOp.InList, "A\nB\nC", caseSensitive: false);
            var p = s.Predicate();
            Assert.True(p(new[] { "a" }));   // 대소문자 무시
            Assert.True(p(new[] { "B" }));
            Assert.False(p(new[] { "D" }));
        }

        [Fact]
        public void Individual_predicates_enable_or_semantics()
        {
            var s = new ColumnFilterState();
            s.SetNumericRange(0, 0, 10);
            s.SetText(1, TextFilterOp.Equals, "yes", false);
            var preds = s.IndividualPredicates();
            Assert.Equal(2, preds.Count);
            bool Any(string[] row) => preds.Any(pr => pr(row));
            Assert.True(Any(new[] { "5", "no" }));     // 첫 조건 충족
            Assert.True(Any(new[] { "99", "yes" }));   // 둘째 조건 충족
            Assert.False(Any(new[] { "99", "no" }));   // 둘 다 불충족
        }

        [Fact]
        public void New_filters_round_trip_through_json()
        {
            var s = new ColumnFilterState();
            s.SetNumericRange(1, 0, 100);
            s.SetText(2, TextFilterOp.Contains, "abc", caseSensitive: true);

            string json = JsonSerializer.Serialize(s);
            var loaded = JsonSerializer.Deserialize<ColumnFilterState>(json)!;

            Assert.Single(loaded.NumericFilters);
            Assert.Equal(100, loaded.NumericFilters[0].Max);
            Assert.Single(loaded.TextFilters);
            Assert.Equal(TextFilterOp.Contains, loaded.TextFilters[0].Op);
            Assert.True(loaded.TextFilters[0].CaseSensitive);
        }
    }
}
