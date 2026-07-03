using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Tests
{
    // 사용자 규칙 실행기·규칙 세트 JSON·키 유일성 스캐너(이슈 #26).
    public class QualityRulesTests
    {
        private static QualityScanSource Source(string[] headers, string[][] rows) => new()
        {
            Headers = headers,
            RowAt = i => rows[i],
            RowCount = rows.Length,
        };

        private static QualityScanOptions Opt => new() { DegreeOfParallelism = 2, Today = new DateTime(2026, 1, 15) };

        // ---------------------------------------------------------------- 규칙 실행

        [Fact]
        public void Rule_counts_violations_with_examples_and_predicate()
        {
            var rows = new[]
            {
                new[] { "50" }, new[] { "-3" }, new[] { "130" }, new[] { "80" },
            };
            var rules = new[]
            {
                new QualityRule { Name = "나이 범위", Expression = "age < 0 OR age > 120", Severity = QualitySeverity.Critical },
            };
            var findings = QualityRuleRunner.Run(rules, Source(new[] { "age" }, rows), Opt, null, CancellationToken.None);

            var f = Assert.Single(findings);
            Assert.Equal(QualityCheckKind.Rule, f.Kind);
            Assert.Equal("나이 범위", f.Label);
            Assert.Equal(QualitySeverity.Critical, f.Severity);
            Assert.Equal(2, f.ViolationCount);
            Assert.Equal(new long[] { 2, 3 }, f.Examples.Select(e => e.SourceRow).ToArray());
            Assert.True(f.ViolationPredicate!(new[] { "999" }));
            Assert.False(f.ViolationPredicate(new[] { "60" }));
        }

        [Fact]
        public void Passing_rule_reports_info_with_zero_count()
        {
            var rows = new[] { new[] { "10" }, new[] { "20" } };
            var rules = new[] { new QualityRule { Name = "r", Expression = "age > 100" } };
            var f = Assert.Single(QualityRuleRunner.Run(rules, Source(new[] { "age" }, rows), Opt, null, CancellationToken.None));
            Assert.Equal(0, f.ViolationCount);
            Assert.Equal(QualitySeverity.Info, f.Severity); // 통과한 규칙은 정보로 강등
            Assert.Null(f.ViolationPredicate);
        }

        [Fact]
        public void Cross_column_rule_finds_reversed_dates()
        {
            var rows = new[]
            {
                new[] { "2024-01-01", "2024-02-01" },
                new[] { "2024-03-01", "2024-02-01" }, // 역전
                new[] { "", "2024-02-01" },           // 빈 값 → 비교 불가 → 위반 아님
            };
            var rules = new[] { new QualityRule { Name = "날짜 역전", Expression = "[end] < [start]" } };
            var f = Assert.Single(QualityRuleRunner.Run(rules, Source(new[] { "start", "end" }, rows), Opt, null, CancellationToken.None));
            Assert.Equal(1, f.ViolationCount);
            Assert.Equal(2, Assert.Single(f.Examples).SourceRow);
        }

        [Fact]
        public void Disabled_rules_are_skipped()
        {
            var rows = new[] { new[] { "1" } };
            var rules = new[] { new QualityRule { Name = "off", Expression = "age > 0", Enabled = false } };
            Assert.Empty(QualityRuleRunner.Run(rules, Source(new[] { "age" }, rows), Opt, null, CancellationToken.None));
        }

        [Fact]
        public void Compile_error_names_the_offending_rule()
        {
            var rows = new[] { new[] { "1" } };
            var rules = new[] { new QualityRule { Name = "고장난 규칙", Expression = "zzz > 1" } };
            var ex = Assert.Throws<QualityRuleCompileException>(() =>
                QualityRuleRunner.Run(rules, Source(new[] { "age" }, rows), Opt, null, CancellationToken.None));
            Assert.Equal("고장난 규칙", ex.RuleName);
            Assert.Contains("고장난 규칙", ex.Message);
        }

        // ---------------------------------------------------------------- 규칙 세트 JSON

        [Fact]
        public void Rule_set_round_trips_through_json()
        {
            var set = new QualityRuleSet
            {
                Name = "병원 QC 기본",
                Rules = new[]
                {
                    new QualityRule { Name = "나이", Expression = "age < 0 OR age > 120", Severity = QualitySeverity.Critical },
                    new QualityRule { Name = "퇴원일", Expression = "[discharge] < [admission]", Enabled = false },
                },
            };
            var restored = QualityRuleSet.Deserialize(set.Serialize());
            Assert.Equal("병원 QC 기본", restored.Name);
            Assert.Equal(2, restored.Rules.Count);
            Assert.Equal("나이", restored.Rules[0].Name);
            Assert.Equal(QualitySeverity.Critical, restored.Rules[0].Severity);
            Assert.False(restored.Rules[1].Enabled);
            Assert.Equal("[discharge] < [admission]", restored.Rules[1].Expression);
        }

        // ---------------------------------------------------------------- 키 유일성

        [Fact]
        public void Key_uniqueness_finds_duplicate_composite_keys()
        {
            var rows = new[]
            {
                new[] { "P01", "V1", "a" },
                new[] { "P01", "V2", "b" },
                new[] { "P01", "V1", "c" }, // (P01,V1) 중복
                new[] { "P02", "V1", "d" },
                new[] { "P01", "V1", "e" }, // (P01,V1) 3번째
            };
            var result = KeyUniquenessScanner.Scan(
                Source(new[] { "subj", "visit", "val" }, rows), new[] { 0, 1 }, Opt, null, CancellationToken.None);

            Assert.False(result.Skipped);
            Assert.Equal(1, result.Groups);
            Assert.Equal(2, result.ExtraCopies);
            var f = result.Finding!;
            Assert.Equal(QualityCheckKind.KeyUniqueness, f.Kind);
            Assert.Equal(QualitySeverity.Critical, f.Severity);
            var b = Assert.Single(f.Breakdown);
            Assert.Equal("P01 | V1", b.Value);
            Assert.Equal(3, b.Count);
            Assert.Equal(1, Assert.Single(f.Examples).SourceRow); // 첫 등장 행
            Assert.True(f.ViolationPredicate!(new[] { "P01", "V1", "zzz" }));
            Assert.False(f.ViolationPredicate(new[] { "P02", "V1", "zzz" }));
        }

        [Fact]
        public void Key_uniqueness_returns_null_when_all_unique()
        {
            var rows = new[] { new[] { "1" }, new[] { "2" }, new[] { "3" } };
            var result = KeyUniquenessScanner.Scan(Source(new[] { "id" }, rows), new[] { 0 }, Opt, null, CancellationToken.None);
            Assert.Null(result.Finding);
            Assert.False(result.Skipped);
        }

        [Fact]
        public void Key_uniqueness_skips_over_row_cap()
        {
            var rows = new[] { new[] { "1" }, new[] { "1" }, new[] { "2" } };
            var opt = Opt with { DuplicateRowCap = 2 };
            var result = KeyUniquenessScanner.Scan(Source(new[] { "id" }, rows), new[] { 0 }, opt, null, CancellationToken.None);
            Assert.True(result.Skipped);
            Assert.Null(result.Finding);
        }
    }
}
