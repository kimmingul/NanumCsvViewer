using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Tests
{
    // 품질 프로파일러(이슈 #26) — 합성 데이터로 계수·판정·술어·결정론을 검증.
    public class QualityProfilerTests
    {
        private static readonly DateTime FixedToday = new(2026, 1, 15);

        private static QualityScanSource Source(string[] headers, string[][] rows,
            ColumnValueType[]? types = null, IReadOnlyList<IReadOnlySet<string>?>? codes = null) => new()
        {
            Headers = headers,
            RowAt = i => rows[i],
            RowCount = rows.Length,
            ColumnTypes = types,
            AllowedCodes = codes,
        };

        private static QualityScanOptions Opt(int dop = 2) => new()
        {
            DegreeOfParallelism = dop,
            Today = FixedToday,
            SentinelMinCount = 2,
            SentinelMinShare = 0.0,
        };

        private static QualityReport Scan(QualityScanSource src, QualityScanOptions? opt = null)
            => QualityProfiler.Scan(src, opt ?? Opt(), null, CancellationToken.None);

        private static QualityFinding One(QualityReport r, QualityCheckKind kind, int column = -1)
            => Assert.Single(r.Findings, f => f.Kind == kind && f.Column == column);

        // ---------------------------------------------------------------- 완전성

        [Fact]
        public void Missing_counts_distinguish_empty_whitespace_and_null_tokens()
        {
            var rows = new[]
            {
                new[] { "" }, new[] { "  " }, new[] { "NA" }, new[] { "null" }, new[] { "x" },
            };
            var r = Scan(Source(new[] { "c" }, rows));
            var p = r.Columns[0];
            Assert.Equal(1, p.EmptyCount);
            Assert.Equal(1, p.WhitespaceCount);
            Assert.Equal(2, p.NullTokenCount);
            Assert.Equal(1, p.NonNullCount);
            Assert.Equal(4, p.MissingCount);

            var f = One(r, QualityCheckKind.MissingRate, 0);
            Assert.Equal(QualitySeverity.Critical, f.Severity); // 4/5 = 80% ≥ 60%
            Assert.Equal(4, f.ViolationCount);
            Assert.NotNull(f.ViolationPredicate);
            Assert.True(f.ViolationPredicate!(new[] { "NA" }));
            Assert.True(f.ViolationPredicate(new[] { "  " }));
            Assert.False(f.ViolationPredicate(new[] { "x" }));
        }

        [Fact]
        public void Empty_column_reported_without_missing_rate()
        {
            var rows = Enumerable.Range(0, 3).Select(_ => new[] { "", "v" }).ToArray();
            var r = Scan(Source(new[] { "a", "b" }, rows));
            var f = One(r, QualityCheckKind.EmptyColumn, 0);
            // 100% 결측은 Critical — 60~99.9% MissingRate가 Critical인데 100%가 낮으면 역전(리뷰 수정).
            Assert.Equal(QualitySeverity.Critical, f.Severity);
            Assert.DoesNotContain(r.Findings, f => f.Kind == QualityCheckKind.MissingRate && f.Column == 0);
        }

        [Fact]
        public void Non_finite_numeric_tokens_agree_between_engine_and_predicate()
        {
            // "nan"/"Infinity"는 double.TryParse가 성공하지만 비유한 — 엔진 계수와 칩 술어가 같은 판정(위반)이어야.
            var rows = new[] { "1", "2", "nan", "Infinity", "3" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "v" }, rows, new[] { ColumnValueType.Float }));
            var f = One(r, QualityCheckKind.TypeConformance, 0);
            Assert.Equal(2, f.ViolationCount);            // 엔진 계수
            Assert.True(f.ViolationPredicate!(new[] { "nan" }));       // 술어도 위반 판정 — 빈 그리드 방지
            Assert.True(f.ViolationPredicate(new[] { "Infinity" }));
            Assert.False(f.ViolationPredicate(new[] { "3" }));
        }

        // ---------------------------------------------------------------- 위장결측 후보

        [Fact]
        public void Numeric_sentinel_at_distribution_edge_is_flagged_as_candidate()
        {
            var rows = new[] { "1", "2", "3", "999", "999" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "score" }, rows, new[] { ColumnValueType.Integer }));
            var f = One(r, QualityCheckKind.DisguisedMissing, 0);
            Assert.Equal(2, f.ViolationCount);
            var b = Assert.Single(f.Breakdown);
            Assert.Equal("999", b.Value);
            Assert.Equal(2, b.Count);
            Assert.True(f.ViolationPredicate!(new[] { "999" }));
            Assert.False(f.ViolationPredicate(new[] { "3" }));
        }

        [Fact]
        public void Numeric_sentinel_in_mid_range_is_not_flagged()
        {
            var rows = new[] { "1", "999", "999", "2000" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "score" }, rows, new[] { ColumnValueType.Integer }));
            Assert.DoesNotContain(r.Findings, f => f.Kind == QualityCheckKind.DisguisedMissing);
        }

        [Fact]
        public void Text_and_date_sentinels_are_flagged()
        {
            var textRows = new[] { "-", "-", "a", "b" }.Select(v => new[] { v }).ToArray();
            var rt = Scan(Source(new[] { "memo" }, textRows));
            var ft = One(rt, QualityCheckKind.DisguisedMissing, 0);
            Assert.Equal("-", Assert.Single(ft.Breakdown).Value);

            var dateRows = new[] { "2020-01-01", "2021-05-05", "1900-01-01", "1900-01-01" }
                .Select(v => new[] { v }).ToArray();
            var rd = Scan(Source(new[] { "birth" }, dateRows, new[] { ColumnValueType.Date }));
            var fd = One(rd, QualityCheckKind.DisguisedMissing, 0);
            Assert.Equal("1900-01-01", Assert.Single(fd.Breakdown).Value);
            Assert.True(fd.ViolationPredicate!(new[] { "1900-01-01" }));
        }

        // ---------------------------------------------------------------- 적합성

        [Fact]
        public void Type_violations_counted_with_examples()
        {
            var rows = new[] { "1", "2", "abc", "1.5", "3" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "n" }, rows, new[] { ColumnValueType.Integer }));
            var f = One(r, QualityCheckKind.TypeConformance, 0);
            Assert.Equal(2, f.ViolationCount); // "abc", "1.5"
            Assert.Equal(2, f.Examples.Count);
            Assert.Contains(f.Examples, e => e.Value == "abc");
            Assert.Contains(f.Examples, e => e.Value == "1.5");
            Assert.True(f.ViolationPredicate!(new[] { "abc" }));
            Assert.False(f.ViolationPredicate(new[] { "7" }));
            Assert.False(f.ViolationPredicate(new[] { "NA" })); // 결측은 결측 검사가 담당
        }

        [Fact]
        public void Currency_affix_is_valid_for_numeric_columns()
        {
            var rows = new[] { "₩1,000", "$2.50", "3" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "amt" }, rows, new[] { ColumnValueType.Currency }));
            Assert.DoesNotContain(r.Findings, f => f.Kind == QualityCheckKind.TypeConformance);
            Assert.Equal(3, r.Columns[0].NonNullCount);
            Assert.Equal(1000, r.Columns[0].NumericMax);
        }

        [Fact]
        public void Date_violations_and_bounds()
        {
            var rows = new[] { "2020-01-01", "2021-06-15", "not-a-date" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "d" }, rows, new[] { ColumnValueType.Date }));
            var f = One(r, QualityCheckKind.TypeConformance, 0);
            Assert.Equal(1, f.ViolationCount);
            Assert.StartsWith("2020-01-01", r.Columns[0].TemporalMin);
            Assert.StartsWith("2021-06-15", r.Columns[0].TemporalMax);
        }

        [Fact]
        public void Codebook_violations_are_critical_and_skip_null_tokens()
        {
            var rows = new[] { "1", "2", "3", "NA" }.Select(v => new[] { v }).ToArray();
            var codes = new IReadOnlySet<string>?[] { new HashSet<string> { "1", "2" } };
            var r = Scan(Source(new[] { "sex" }, rows, new[] { ColumnValueType.Categorical }, codes));
            var f = One(r, QualityCheckKind.CodebookConformance, 0);
            Assert.Equal(QualitySeverity.Critical, f.Severity);
            Assert.Equal(1, f.ViolationCount);
            Assert.Equal("3", Assert.Single(f.Examples).Value);
            Assert.True(f.ViolationPredicate!(new[] { "9" }));
            Assert.False(f.ViolationPredicate(new[] { "1" }));
            Assert.False(f.ViolationPredicate(new[] { "NA" }));
        }

        [Fact]
        public void Ragged_rows_are_critical()
        {
            var rows = new[] { new[] { "a", "b" }, new[] { "only-one" }, new[] { "c", "d" } };
            var r = Scan(Source(new[] { "x", "y" }, rows));
            var f = One(r, QualityCheckKind.RaggedRows);
            Assert.Equal(QualitySeverity.Critical, f.Severity);
            Assert.Equal(1, f.ViolationCount);
            Assert.Equal(2, Assert.Single(f.Examples).SourceRow); // 원본 행번호 1-based
            Assert.True(f.ViolationPredicate!(new[] { "z" }));
            Assert.False(f.ViolationPredicate(new[] { "z", "w" }));
        }

        // ---------------------------------------------------------------- 타당성

        [Fact]
        public void Constant_and_near_constant_columns()
        {
            var constRows = Enumerable.Range(0, 5).Select(_ => new[] { "A" }).ToArray();
            var rc = Scan(Source(new[] { "c" }, constRows));
            var fc = One(rc, QualityCheckKind.ConstantColumn, 0);
            Assert.Equal("A", fc.Label);
            Assert.Null(fc.ViolationPredicate); // 상수 컬럼은 칩 변환이 무의미

            var nearRows = Enumerable.Range(0, 300).Select(_ => new[] { "A" })
                .Append(new[] { "B" }).ToArray();
            var rn = Scan(Source(new[] { "c" }, nearRows));
            var fn = One(rn, QualityCheckKind.ConstantColumn, 0);
            Assert.Equal("A", fn.Label);
            Assert.Equal(1, fn.ViolationCount);
            Assert.True(fn.ViolationPredicate!(new[] { "B" }));
            Assert.False(fn.ViolationPredicate(new[] { "A" }));
        }

        [Fact]
        public void Duplicate_rows_detected_via_hash()
        {
            var rows = new[]
            {
                new[] { "a", "1" }, new[] { "b", "2" }, new[] { "a", "1" }, new[] { "c", "3" },
            };
            var r = Scan(Source(new[] { "x", "y" }, rows));
            var f = One(r, QualityCheckKind.DuplicateRows);
            Assert.Equal(1, f.ViolationCount); // 초과 사본 1
            Assert.Equal("1", f.Label);        // 그룹 1개
            Assert.True(f.ViolationPredicate!(new[] { "a", "1" }));
            Assert.False(f.ViolationPredicate(new[] { "b", "2" }));
        }

        [Fact]
        public void Duplicate_check_skipped_over_cap_is_reported_honestly()
        {
            var rows = new[] { new[] { "a" }, new[] { "a" }, new[] { "b" } };
            var opt = Opt() with { DuplicateRowCap = 2 };
            var r = Scan(Source(new[] { "x" }, rows), opt);
            Assert.True(r.DuplicateRowCheckSkipped);
            Assert.DoesNotContain(r.Findings, f => f.Kind == QualityCheckKind.DuplicateRows);
        }

        [Fact]
        public void Outliers_flagged_with_exact_fences_on_small_data()
        {
            var rows = Enumerable.Range(1, 100).Select(i => new[] { i.ToString() })
                .Append(new[] { "1000" }).ToArray();
            var r = Scan(Source(new[] { "v" }, rows, new[] { ColumnValueType.Integer }));
            var f = One(r, QualityCheckKind.Outliers, 0);
            Assert.Equal(1, f.ViolationCount);
            Assert.False(f.Approximate); // 표본 축약 없음 → 정확
            Assert.True(f.FenceHigh < 1000);
            Assert.True(f.ViolationPredicate!(new[] { "1000" }));
            Assert.False(f.ViolationPredicate(new[] { "50" }));
        }

        [Fact]
        public void Future_dates_flagged_against_injected_today()
        {
            var rows = new[] { "2020-01-01", "2030-01-01", "2026-01-15" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "d" }, rows, new[] { ColumnValueType.Date }));
            var f = One(r, QualityCheckKind.FutureDate, 0);
            Assert.Equal(1, f.ViolationCount);
            Assert.Equal("2030-01-01", Assert.Single(f.Examples).Value);
            Assert.True(f.ViolationPredicate!(new[] { "2027-03-01" }));
            Assert.False(f.ViolationPredicate(new[] { "2026-01-15" })); // 기준일 당일은 미래 아님
        }

        // ---------------------------------------------------------------- 프로파일·결정론·직렬화

        [Fact]
        public void Numeric_profile_matches_hand_computed_stats()
        {
            var rows = new[] { "2", "4", "4", "4", "5", "5", "7", "9" }.Select(v => new[] { v }).ToArray();
            var r = Scan(Source(new[] { "v" }, rows, new[] { ColumnValueType.Integer }));
            var p = r.Columns[0];
            Assert.Equal(2, p.NumericMin);
            Assert.Equal(9, p.NumericMax);
            Assert.Equal(5, p.NumericMean);
            Assert.Equal(2, p.NumericStdDev!.Value, 10); // 모표준편차
            Assert.Equal(5, p.DistinctCount);
            Assert.False(p.DistinctIsLowerBound);
        }

        [Fact]
        public void Parallelism_does_not_change_results()
        {
            var rng = new Random(42); // 고정 시드 — 결정적 데이터
            var rows = Enumerable.Range(0, 5000).Select(i => new[]
            {
                (i % 97 == 0 ? "" : rng.Next(0, 200).ToString()),
                (i % 41 == 0 ? "999" : rng.Next(1, 50).ToString()),
            }).ToArray();
            var src1 = Source(new[] { "a", "b" }, rows, new[] { ColumnValueType.Integer, ColumnValueType.Integer });

            var r1 = Scan(src1, Opt(dop: 1));
            var r8 = Scan(src1, Opt(dop: 8));

            Assert.Equal(r1.Findings.Count, r8.Findings.Count);
            for (int i = 0; i < r1.Findings.Count; i++)
            {
                Assert.Equal(r1.Findings[i].Kind, r8.Findings[i].Kind);
                Assert.Equal(r1.Findings[i].Column, r8.Findings[i].Column);
                Assert.Equal(r1.Findings[i].ViolationCount, r8.Findings[i].ViolationCount);
            }
            for (int c = 0; c < r1.Columns.Count; c++)
            {
                Assert.Equal(r1.Columns[c].MissingCount, r8.Columns[c].MissingCount);
                Assert.Equal(r1.Columns[c].NumericMin, r8.Columns[c].NumericMin);
                Assert.Equal(r1.Columns[c].NumericMax, r8.Columns[c].NumericMax);
                Assert.Equal(r1.Columns[c].NumericMean!.Value, r8.Columns[c].NumericMean!.Value, 9);
            }
        }

        [Fact]
        public void Empty_input_produces_empty_report()
        {
            var r = Scan(Source(new[] { "a" }, Array.Empty<string[]>()));
            Assert.Equal(0, r.RowsScanned);
            Assert.Empty(r.Findings);
            Assert.False(r.DuplicateRowCheckSkipped);
        }

        [Fact]
        public void Report_serializes_to_stable_json()
        {
            var rows = new[] { new[] { "1" }, new[] { "" }, new[] { "abc" } };
            var r = Scan(Source(new[] { "n" }, rows, new[] { ColumnValueType.Integer }));
            string json = QualityReportJson.Serialize(r);
            Assert.Contains("\"SchemaVersion\": 1", json);
            Assert.Contains("\"RowsScanned\": 3", json);
            Assert.Contains("TypeConformance", json);          // enum은 문자열로
            Assert.DoesNotContain("ViolationPredicate", json); // 술어는 직렬화 제외
        }

        [Fact]
        public void Findings_are_sorted_severity_then_column()
        {
            var rows = new[]
            {
                new[] { "1", "x" }, new[] { "abc", "x" }, new[] { "", "x" }, new[] { "", "x" },
            };
            var r = Scan(Source(new[] { "n", "c" }, rows, new[] { ColumnValueType.Integer, ColumnValueType.String }));
            var severities = r.Findings.Select(f => f.Severity).ToArray();
            var sorted = severities.OrderByDescending(s => s).ToArray();
            Assert.Equal(sorted, severities);
        }
    }
}
