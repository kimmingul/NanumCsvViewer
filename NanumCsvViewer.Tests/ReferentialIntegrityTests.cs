using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Tests
{
    // 교차 테이블 참조 무결성(FK) 스캐너. 후보 검사 — 빈 키는 기본으로 위반이 아니다.
    public class ReferentialIntegrityTests
    {
        private static QualityScanSource Child(string[] headers, string[][] rows) => new()
        {
            Headers = headers,
            RowAt = i => rows[i],
            RowCount = rows.Length,
        };

        private static ReferentialParentSource Parent(string[][] rows) => new()
        {
            RowAt = i => rows[i],
            RowCount = rows.Length,
            Name = "parent",
        };

        private static ReferentialIntegrityOptions Opt => new() { DegreeOfParallelism = 2, MaxExamples = 20 };

        [Fact]
        public void Single_key_counts_orphans_and_predicate_matches_exactly()
        {
            var childRows = new[]
            {
                new[] { "A", "x" },
                new[] { "B", "y" }, // 고아
                new[] { "A", "z" },
                new[] { "C", "w" }, // 고아
                new[] { "B", "v" }, // 고아 (같은 키, 두 번째)
            };
            var parentRows = new[] { new[] { "A" }, new[] { "A" }, new[] { "D" } };

            var result = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id", "v" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt, null, CancellationToken.None);

            Assert.Equal(3, result.OrphanRows);
            Assert.Equal(0, result.BlankSkipped);
            Assert.Equal(2, result.ParentDistinctKeys); // A, D
            var f = result.Finding!;
            Assert.Equal(QualityCheckKind.ForeignKeyOrphan, f.Kind);
            Assert.Equal(QualityDimension.Conformance, f.Dimension);
            Assert.Equal(QualitySeverity.Critical, f.Severity);
            Assert.Equal(3, f.ViolationCount);
            Assert.Equal(5, f.EvaluatedRows);
            Assert.Equal(new[] { 2L, 4L }, f.Examples.Select(e => e.SourceRow).ToArray());
            Assert.Equal("B", f.Examples[0].Value);
            Assert.Equal("C", f.Examples[1].Value);
            Assert.Equal(2, f.Breakdown[0].Count); // B 두 행
            Assert.Equal(1, f.Breakdown[1].Count);

            for (int i = 0; i < childRows.Length; i++)
            {
                bool orphan = childRows[i][0] is "B" or "C";
                Assert.Equal(orphan, f.ViolationPredicate!(childRows[i]));
            }
            Assert.False(f.ViolationPredicate!(new[] { "A", "nope" }));
            Assert.False(f.ViolationPredicate(new[] { "D", "only-in-parent" }));
            Assert.True(f.ViolationPredicate(new[] { "Z" }));
        }

        [Fact]
        public void Composite_key_requires_every_part_to_match()
        {
            var childRows = new[]
            {
                new[] { "P01", "V1", "a" }, // 일치
                new[] { "P01", "V2", "b" }, // 고아 (visit만 다름)
                new[] { "P02", "V1", "c" }, // 고아 (person만 다름)
                new[] { "P01", "V1", "d" }, // 일치
            };
            var parentRows = new[]
            {
                new[] { "P01", "V1" },
                new[] { "P09", "V9" },
            };

            var result = ReferentialIntegrityScanner.Scan(
                Child(new[] { "person", "visit", "val" }, childRows), new[] { 0, 1 },
                Parent(parentRows), new[] { 1, 0 }, // 부모 컬럼 순서가 반대여도 호출자가 맞추면 된다
                Opt, null, CancellationToken.None);

            // 부모 컬럼을 (visit, person)으로 주면 (V1, P01)은 자식 (P01, V1)과 다르다 — 전부 고아.
            Assert.Equal(4, result.OrphanRows);

            var aligned = ReferentialIntegrityScanner.Scan(
                Child(new[] { "person", "visit", "val" }, childRows), new[] { 0, 1 },
                Parent(parentRows), new[] { 0, 1 },
                Opt, null, CancellationToken.None);

            Assert.Equal(2, aligned.OrphanRows);
            Assert.Equal(new[] { 2L, 3L }, aligned.Finding!.Examples.Select(e => e.SourceRow).ToArray());
            Assert.Equal("P01 | V2", aligned.Finding.Examples[0].Value);
            Assert.True(aligned.Finding.ViolationPredicate!(new[] { "P01", "V2", "zzz" }));
            Assert.False(aligned.Finding.ViolationPredicate(new[] { "P01", "V1", "zzz" }));
            Assert.False(aligned.Finding.ViolationPredicate(new[] { "P09", "V9" }));
        }

        [Fact]
        public void Blank_and_null_token_keys_are_skipped_by_default_and_counted()
        {
            var childRows = new[]
            {
                new[] { "" },
                new[] { "  " },
                new[] { "NA" },
                new[] { "null" },
                new[] { "n/a" },
                new[] { "MISSING" },
                new[] { "B" }, // 고아
                new[] { "A" }, // 일치
            };
            var parentRows = new[] { new[] { "A" }, new[] { "" } };

            var result = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt, null, CancellationToken.None);

            Assert.Equal(1, result.OrphanRows);
            Assert.Equal(6, result.BlankSkipped);
            Assert.Equal(6, result.Finding!.SkippedRows);
            Assert.Equal("B", Assert.Single(result.Finding.Examples).Value);
            Assert.False(result.Finding.ViolationPredicate!(new[] { "" }));
            Assert.False(result.Finding.ViolationPredicate(new[] { "  " }));
            Assert.False(result.Finding.ViolationPredicate(new[] { "NA" }));
            Assert.False(result.Finding.ViolationPredicate(new[] { "nil" }));
            Assert.True(result.Finding.ViolationPredicate(new[] { "B" }));
            Assert.False(result.Finding.ViolationPredicate(new[] { "A" }));
        }

        [Fact]
        public void Blank_keys_are_violations_when_skip_disabled_unless_parent_has_them()
        {
            var childRows = new[]
            {
                new[] { "" },      // 부모가 빈 키를 가지므로 고아 아님
                new[] { "NA" },    // 부모에 없음 → 고아
                new[] { "  " },    // 트림 없음 → 빈 문자열과 다름 → 고아
                new[] { "A" },
            };
            var parentRows = new[] { new[] { "" }, new[] { "A" } };
            var opt = Opt with { SkipBlankChildKeys = false };

            var result = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                opt, null, CancellationToken.None);

            Assert.Equal(0, result.BlankSkipped);
            Assert.Equal(2, result.OrphanRows);
            var pred = result.Finding!.ViolationPredicate!;
            Assert.False(pred(new[] { "" }));
            Assert.True(pred(new[] { "NA" }));
            Assert.True(pred(new[] { "  " }));
            Assert.False(pred(new[] { "A" }));
            Assert.Equal(new[] { 2L, 3L }, result.Finding.Examples.Select(e => e.SourceRow).ToArray());
        }

        [Fact]
        public void Partial_composite_blank_is_checked_not_skipped()
        {
            var childRows = new[]
            {
                new[] { "P01", "" },    // 일부만 빔 — 건너뛰지 않음, 부모에 없으면 고아
                new[] { "", "NA" },     // 전부 빈/널 토큰 — 건너뜀
                new[] { "P01", "V1" },  // 일치
            };
            var parentRows = new[] { new[] { "P01", "V1" } };

            var result = ReferentialIntegrityScanner.Scan(
                Child(new[] { "person", "visit" }, childRows), new[] { 0, 1 },
                Parent(parentRows), new[] { 0, 1 },
                Opt, null, CancellationToken.None);

            Assert.Equal(1, result.BlankSkipped);
            Assert.Equal(1, result.OrphanRows);
            Assert.Equal(1L, Assert.Single(result.Finding!.Examples).SourceRow);
            Assert.True(result.Finding.ViolationPredicate!(new[] { "P01", "" }));
            Assert.False(result.Finding.ViolationPredicate(new[] { "", "null" }));
            Assert.False(result.Finding.ViolationPredicate(new[] { "P01", "V1" }));
        }

        [Fact]
        public void Trim_option_matches_padded_keys_and_predicate_agrees()
        {
            var childRows = new[]
            {
                new[] { " ab " },
                new[] { "ab" },
                new[] { "ab " },
                new[] { "zz" },
            };
            var parentRows = new[] { new[] { "ab" } };

            var untrimmed = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt, null, CancellationToken.None);
            Assert.Equal(3, untrimmed.OrphanRows); // " ab ", "ab ", "zz" — "ab"만 일치
            Assert.False(untrimmed.Finding!.ViolationPredicate!(new[] { "ab" }));
            Assert.True(untrimmed.Finding.ViolationPredicate(new[] { " ab " }));

            var trimmed = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt with { Trim = true }, null, CancellationToken.None);
            Assert.Equal(1, trimmed.OrphanRows);
            Assert.Equal("zz", Assert.Single(trimmed.Finding!.Examples).Value);
            Assert.Equal(4L, Assert.Single(trimmed.Finding.Examples).SourceRow);
            var pred = trimmed.Finding.ViolationPredicate!;
            Assert.False(pred(new[] { " ab " }));
            Assert.False(pred(new[] { "ab" }));
            Assert.False(pred(new[] { "  ab" }));
            Assert.True(pred(new[] { "zz" }));
            Assert.True(pred(new[] { " zz " }));
        }

        [Fact]
        public void Comparison_is_ordinal_by_default_and_ignore_case_is_optional()
        {
            var childRows = new[] { new[] { "abc" }, new[] { "ABC" }, new[] { "Abc" }, new[] { "zzz" } };
            var parentRows = new[] { new[] { "ABC" } };

            var sensitive = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt, null, CancellationToken.None);
            Assert.Equal(3, sensitive.OrphanRows);
            Assert.False(sensitive.Finding!.ViolationPredicate!(new[] { "ABC" }));
            Assert.True(sensitive.Finding.ViolationPredicate(new[] { "abc" }));

            var ignore = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt with { CaseSensitive = false }, null, CancellationToken.None);
            Assert.Equal(1, ignore.OrphanRows);
            Assert.Equal("zzz", Assert.Single(ignore.Finding!.Examples).Value);
            Assert.False(ignore.Finding.ViolationPredicate!(new[] { "abc" }));
            Assert.False(ignore.Finding.ViolationPredicate(new[] { "AbC" }));
            Assert.True(ignore.Finding.ViolationPredicate(new[] { "zzz" }));
        }

        [Fact]
        public void Examples_are_deterministic_earliest_distinct_keys()
        {
            // 파티션 클램프(rows/4096+1)가 DOP 4를 허용하려면 12288행을 넘어야 한다.
            var rows = new string[13_000][];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new[] { i % 2 == 0 ? "keep" : $"o{i:00}" };
            var parentRows = new[] { new[] { "keep" } };
            var opt = Opt with { MaxExamples = 3 };

            var a = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, rows), new[] { 0 }, Parent(parentRows), new[] { 0 },
                opt with { DegreeOfParallelism = 1 }, null, CancellationToken.None);
            var b = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, rows), new[] { 0 }, Parent(parentRows), new[] { 0 },
                opt with { DegreeOfParallelism = 4 }, null, CancellationToken.None);

            Assert.Equal(6_500, a.OrphanRows);
            Assert.Equal(6_500, b.OrphanRows);
            Assert.Equal(new[] { 2L, 4L, 6L }, a.Finding!.Examples.Select(e => e.SourceRow).ToArray());
            Assert.Equal(
                a.Finding.Examples.Select(e => (e.SourceRow, e.Value)).ToArray(),
                b.Finding!.Examples.Select(e => (e.SourceRow, e.Value)).ToArray());
            Assert.Equal(
                a.Finding.Breakdown.Select(v => (v.Value, v.Count)).ToArray(),
                b.Finding.Breakdown.Select(v => (v.Value, v.Count)).ToArray());
            Assert.All(a.Finding.Breakdown, v => Assert.Equal(1, v.Count));
        }

        [Fact]
        public void Clean_reference_returns_null_finding_and_reports_blanks()
        {
            var childRows = new[] { new[] { "A" }, new[] { "" }, new[] { "NA" } };
            var parentRows = new[] { new[] { "A" }, new[] { "B" } };
            var result = ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt, null, CancellationToken.None);

            Assert.Null(result.Finding);
            Assert.Equal(0, result.OrphanRows);
            Assert.Equal(2, result.BlankSkipped);
            Assert.Equal(2, result.ParentDistinctKeys);
        }

        [Fact]
        public void Budget_exceeded_throws_and_does_not_scan_the_child()
        {
            var parentRows = Enumerable.Range(0, 50).Select(i => new[] { new string('k', 40) + i }).ToArray();
            bool childRead = false;
            var child = new QualityScanSource
            {
                Headers = new[] { "id" },
                RowCount = 10,
                RowAt = i => { childRead = true; return new[] { "x" }; },
            };

            Assert.Throws<ReferentialIntegrityBudgetException>(() => ReferentialIntegrityScanner.Scan(
                child, new[] { 0 }, Parent(parentRows), new[] { 0 },
                Opt with { ParentKeyMemoryBudgetBytes = 64 }, null, CancellationToken.None));
            Assert.False(childRead);
        }

        [Fact]
        public void Cancellation_before_start_and_mid_scan_throw()
        {
            var parentRows = new[] { new[] { "A" } };
            var childRows = Enumerable.Range(0, 8).Select(i => new[] { "x" + i }).ToArray();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => ReferentialIntegrityScanner.Scan(
                Child(new[] { "id" }, childRows), new[] { 0 },
                Parent(parentRows), new[] { 0 },
                Opt, null, cancelled.Token));

            using var cts = new CancellationTokenSource();
            int calls = 0;
            var child = new QualityScanSource
            {
                Headers = new[] { "id" },
                RowCount = 20_000,
                RowAt = i =>
                {
                    if (Interlocked.Increment(ref calls) == 50) cts.Cancel();
                    return new[] { "orphan" + i };
                },
            };
            Assert.Throws<OperationCanceledException>(() => ReferentialIntegrityScanner.Scan(
                child, new[] { 0 }, Parent(parentRows), new[] { 0 },
                Opt with { DegreeOfParallelism = 2 }, null, cts.Token));
        }

        [Fact]
        public void Mismatched_or_empty_column_lists_throw()
        {
            var child = Child(new[] { "a" }, new[] { new[] { "1" } });
            var parent = Parent(new[] { new[] { "1" } });
            Assert.Throws<ArgumentException>(() => ReferentialIntegrityScanner.Scan(
                child, Array.Empty<int>(), parent, new[] { 0 }, Opt, null, CancellationToken.None));
            Assert.Throws<ArgumentException>(() => ReferentialIntegrityScanner.Scan(
                child, new[] { 0, 1 }, parent, new[] { 0 }, Opt, null, CancellationToken.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReferentialIntegrityScanner.Scan(
                child, new[] { 0 }, parent, new[] { 0 },
                Opt with { ParentKeyMemoryBudgetBytes = 0 }, null, CancellationToken.None));
        }
    }
}
