using System.Text.Json;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Tests
{
    // 기준선 스냅샷 diff — JSON 라운드트립, 컬럼 증감, 타입·결측률 임계, 발견 짝짓기, 부분 스캔, 스키마 거부.
    public class QualitySnapshotDiffTests
    {
        private static QualityColumnProfile Col(int index, string name, ColumnValueType type,
            long missing = 0, long distinct = 1, double? min = null, double? max = null, double? mean = null,
            bool distinctLowerBound = false) => new()
        {
            Index = index,
            Name = name,
            ExpectedType = type,
            EmptyCount = missing,
            NonNullCount = 0,
            DistinctCount = distinct,
            DistinctIsLowerBound = distinctLowerBound,
            NumericMin = min,
            NumericMax = max,
            NumericMean = mean,
        };

        private static QualityFinding Finding(QualityCheckKind kind, string column, string? rule = null,
            long count = 1, QualitySeverity severity = QualitySeverity.Warning, string? example = null) => new()
        {
            Kind = kind,
            Dimension = QualityDimension.Plausibility,
            Severity = severity,
            Column = column.Length == 0 ? -1 : 0,
            ColumnName = column,
            Label = rule,
            ViolationCount = count,
            EvaluatedRows = 100,
            Examples = example is null
                ? Array.Empty<QualityExample>()
                : new[] { new QualityExample(1, example) },
        };

        private static QualityReport Report(QualityColumnProfile[] columns, QualityFinding[]? findings = null,
            long rows = 100, bool full = true, bool dupSkipped = false, int schema = 1, string name = "a.csv") => new()
        {
            SchemaVersion = schema,
            RowsScanned = rows,
            ScannedFully = full,
            DuplicateRowCheckSkipped = dupSkipped,
            ElapsedSeconds = 0.1,
            Columns = columns,
            Findings = findings ?? Array.Empty<QualityFinding>(),
            SourceName = name,
            ScanTimestamp = "2026-01-01T00:00:00+09:00",
        };

        private static QualitySnapshotDiffOptions Tight() => new()
        {
            MissingRateDeltaPoints = 5,
            UniqueCountChangeRatio = 0.10,
            NumericRelativeThreshold = 0,
        };

        private static IEnumerable<QualitySnapshotDiffItem> Of(QualitySnapshotDiffResult d, QualitySnapshotDiffKind kind)
            => d.Items.Where(i => i.Kind == kind);

        [Fact]
        public void Json_round_trip_loads_as_identical_baseline()
        {
            var report = Report(new[]
            {
                Col(0, "age", ColumnValueType.Integer, missing: 2, distinct: 8, min: 1, max: 10, mean: 3.25),
            }, new[]
            {
                Finding(QualityCheckKind.Rule, "", "age_range", count: 4, example: "row text that must not be the match key"),
                Finding(QualityCheckKind.MissingRate, "age", count: 2),
            });

            string json = QualityReportJson.Serialize(report);
            var baseline = QualityReportJson.Deserialize(json);

            Assert.Equal(1, baseline.SchemaVersion);
            Assert.Equal(100, baseline.RowsScanned);
            Assert.Equal("age", Assert.Single(baseline.Columns).Name);
            Assert.Equal(ColumnValueType.Integer, baseline.Columns[0].ExpectedType);
            Assert.Equal(2, baseline.Columns[0].MissingCount);
            Assert.Equal(3.25, baseline.Columns[0].NumericMean);
            Assert.Equal(2, baseline.Findings.Count);
            Assert.Equal("age_range", baseline.Findings[0].Label);
            Assert.DoesNotContain("ViolationPredicate", json);

            var diff = QualitySnapshotDiff.Compare(baseline, report, Tight());
            Assert.True(diff.Comparable);
            Assert.Equal(0, diff.RowCountDelta);
            Assert.DoesNotContain(diff.Items, i => i.Kind != QualitySnapshotDiffKind.FindingPersisting);
            Assert.Equal(2, Of(diff, QualitySnapshotDiffKind.FindingPersisting).Count());
        }

        [Fact]
        public void Deserialize_tolerates_unknown_fields()
        {
            var report = Report(new[] { Col(0, "id", ColumnValueType.Identifier) });
            string json = QualityReportJson.Serialize(report);
            json = json.Replace("\"SchemaVersion\": 1",
                "\"SchemaVersion\": 1,\n  \"FutureField\": { \"nested\": true },\n  \"extra\": 1");

            var loaded = QualityReportJson.Deserialize(json);
            Assert.Equal("id", Assert.Single(loaded.Columns).Name);
            Assert.Equal(QualityReportJson.SupportedSchemaVersion, loaded.SchemaVersion);
        }

        [Fact]
        public void Deserialize_rejects_unsupported_schema_version()
        {
            var report = Report(new[] { Col(0, "id", ColumnValueType.Identifier) });
            string json = QualityReportJson.Serialize(report)
                .Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99");

            var ex = Assert.Throws<QualitySnapshotSchemaException>(() => QualityReportJson.Deserialize(json));
            Assert.Equal(99, ex.SchemaVersion);
            Assert.Equal(1, ex.SupportedVersion);
        }

        [Fact]
        public void Deserialize_rejects_wrong_document_shape()
        {
            Assert.Throws<JsonException>(() => QualityReportJson.Deserialize(""));
            Assert.Throws<JsonException>(() => QualityReportJson.Deserialize("[]"));
            Assert.Throws<JsonException>(() => QualityReportJson.Deserialize("{ \"SchemaVersion\": 1 }"));
            Assert.Throws<JsonException>(() => QualityReportJson.Deserialize("not json"));
        }

        [Fact]
        public void Added_and_removed_columns_follow_header_name_not_position()
        {
            // 같은 위치(index 0)의 age → years 는 개명이 아니라 삭제+추가. id는 이름이 같아 유지.
            var baseline = Report(new[]
            {
                Col(0, "age", ColumnValueType.Integer),
                Col(1, "id", ColumnValueType.Identifier, distinct: 10),
            });
            var current = Report(new[]
            {
                Col(0, "years", ColumnValueType.Integer),
                Col(1, "id", ColumnValueType.Identifier, distinct: 10),
            });

            var diff = QualitySnapshotDiff.Compare(baseline, current, Tight());

            var removed = Assert.Single(Of(diff, QualitySnapshotDiffKind.ColumnRemoved));
            Assert.Equal("age", removed.ColumnName);
            Assert.Equal(0, removed.BaselineColumnIndex);
            var added = Assert.Single(Of(diff, QualitySnapshotDiffKind.ColumnAdded));
            Assert.Equal("years", added.ColumnName);
            Assert.Equal(0, added.CurrentColumnIndex);
            Assert.DoesNotContain(diff.Items, i => i.Kind == QualitySnapshotDiffKind.TypeChanged);
        }

        [Fact]
        public void Type_change_on_common_column_is_reported()
        {
            var baseline = Report(new[] { Col(0, "score", ColumnValueType.Integer, min: 1, max: 9, mean: 4) });
            var current = Report(new[] { Col(0, "score", ColumnValueType.Float, min: 1, max: 9, mean: 4) });

            var diff = QualitySnapshotDiff.Compare(baseline, current, Tight());
            var type = Assert.Single(Of(diff, QualitySnapshotDiffKind.TypeChanged));
            Assert.Equal("score", type.ColumnName);
            Assert.Equal("Integer", type.BaselineText);
            Assert.Equal("Float", type.CurrentText);
            Assert.Equal(QualitySeverity.Warning, type.Severity);
            // 양쪽 수치가 같으면 타입 변화와 별도로 이동을 보고하지 않는다.
            Assert.DoesNotContain(diff.Items, i => i.Kind == QualitySnapshotDiffKind.NumericMeanChanged);
        }

        [Fact]
        public void Null_rate_threshold_is_strict_percentage_points()
        {
            var options = Tight(); // 5.0pp 초과만
            var baseline = Report(new[] { Col(0, "age", ColumnValueType.Integer, missing: 10) }, rows: 100);

            var below = QualitySnapshotDiff.Compare(baseline,
                Report(new[] { Col(0, "age", ColumnValueType.Integer, missing: 14) }, rows: 100), options);
            Assert.DoesNotContain(below.Items, i => i.Kind == QualitySnapshotDiffKind.MissingRateChanged);

            var at = QualitySnapshotDiff.Compare(baseline,
                Report(new[] { Col(0, "age", ColumnValueType.Integer, missing: 15) }, rows: 100), options);
            Assert.DoesNotContain(at.Items, i => i.Kind == QualitySnapshotDiffKind.MissingRateChanged);

            var above = QualitySnapshotDiff.Compare(baseline,
                Report(new[] { Col(0, "age", ColumnValueType.Integer, missing: 16) }, rows: 100), options);
            var item = Assert.Single(Of(above, QualitySnapshotDiffKind.MissingRateChanged));
            Assert.Equal(6d, item.Delta);
            Assert.Equal(QualitySeverity.Warning, item.Severity);

            // 같은 폭의 감소는 정보 — 경계를 넘는 개선을 경고로 올리지 않는다.
            var down = QualitySnapshotDiff.Compare(
                Report(new[] { Col(0, "age", ColumnValueType.Integer, missing: 16) }, rows: 100),
                baseline, options);
            Assert.Equal(QualitySeverity.Info,
                Assert.Single(Of(down, QualitySnapshotDiffKind.MissingRateChanged)).Severity);
        }

        [Fact]
        public void Findings_match_by_kind_column_and_rule_name_not_message()
        {
            var baseline = Report(new[] { Col(0, "age", ColumnValueType.Integer) }, new[]
            {
                Finding(QualityCheckKind.Rule, "", "age_range", count: 2, example: "old message"),
                Finding(QualityCheckKind.Rule, "", "retired_rule", count: 1, example: "gone"),
                Finding(QualityCheckKind.MissingRate, "age", count: 3, example: "blank"),
                Finding(QualityCheckKind.ConstantColumn, "age", rule: "0", count: 90),
                Finding(QualityCheckKind.Outliers, "age", count: 4),
            });
            var current = Report(new[] { Col(0, "age", ColumnValueType.Integer) }, new[]
            {
                // 건수·예시(메시지)가 달라도 같은 규칙명은 지속.
                Finding(QualityCheckKind.Rule, "", "age_range", count: 9, severity: QualitySeverity.Critical,
                    example: "completely different message"),
                Finding(QualityCheckKind.Rule, "", "new_rule", count: 1),
                Finding(QualityCheckKind.MissingRate, "age", count: 8, example: "other blanks"),
                // 상수값(Label)은 규칙명이 아니다 — 종류+컬럼이 같으면 지속.
                Finding(QualityCheckKind.ConstantColumn, "age", rule: "1", count: 80),
            });

            var diff = QualitySnapshotDiff.Compare(baseline, current, Tight());
            var persisting = Of(diff, QualitySnapshotDiffKind.FindingPersisting).ToArray();
            Assert.Equal(3, persisting.Length);
            Assert.Contains(persisting, i => i.CheckKind == QualityCheckKind.Rule && i.RuleName == "age_range"
                && i.BaselineNumber == 2 && i.CurrentNumber == 9);
            Assert.Contains(persisting, i => i.CheckKind == QualityCheckKind.MissingRate && i.ColumnName == "age"
                && i.RuleName == "" && i.BaselineNumber == 3 && i.CurrentNumber == 8);
            Assert.Contains(persisting, i => i.CheckKind == QualityCheckKind.ConstantColumn && i.RuleName == "");

            // 사용자 실행 검사(규칙)가 현재 세션에 없으면 해소가 아니라 재검사 안 됨.
            Assert.DoesNotContain(diff.Items, i => i.Kind == QualitySnapshotDiffKind.FindingResolved
                && i.CheckKind == QualityCheckKind.Rule);
            var notRechecked = Assert.Single(Of(diff, QualitySnapshotDiffKind.FindingNotRechecked));
            Assert.Equal(QualityCheckKind.Rule, notRechecked.CheckKind);
            Assert.Equal("retired_rule", notRechecked.RuleName);
            // 프로파일(자동) 검사가 현재에 없으면 해소.
            var resolved = Assert.Single(Of(diff, QualitySnapshotDiffKind.FindingResolved));
            Assert.Equal(QualityCheckKind.Outliers, resolved.CheckKind);

            var added = Assert.Single(Of(diff, QualitySnapshotDiffKind.FindingNew));
            Assert.Equal("new_rule", added.RuleName);
            Assert.Equal(QualitySeverity.Warning, added.Severity);
        }

        [Fact]
        public void Partial_scan_is_flagged_not_comparable()
        {
            var columns = new[] { Col(0, "age", ColumnValueType.Integer, missing: 10, distinct: 5, min: 1, max: 2, mean: 1.5) };
            var findings = new[] { Finding(QualityCheckKind.MissingRate, "age", count: 10) };
            var baseline = Report(columns, findings, full: false);
            var current = Report(columns, findings, full: true);

            var diff = QualitySnapshotDiff.Compare(baseline, current, Tight());
            Assert.False(diff.Comparable);
            Assert.False(diff.BaselineScannedFully);
            Assert.True(diff.CurrentScannedFully);
            Assert.NotEmpty(diff.Items); // 지속 발견은 남기되
            Assert.All(diff.Items, i => Assert.True(i.Approximate));

            // 부분 스캔이어도 타입 변화 자체는 보고하고, 근사로 라벨한다.
            var changed = QualitySnapshotDiff.Compare(baseline,
                Report(new[] { Col(0, "age", ColumnValueType.Float, missing: 10, distinct: 5) }, findings, full: true),
                Tight());
            var type = Assert.Single(Of(changed, QualitySnapshotDiffKind.TypeChanged));
            Assert.True(type.Approximate);
            Assert.False(changed.Comparable);
        }

        [Fact]
        public void Sentinel_candidates_report_appearance_not_assertion_and_numeric_shift()
        {
            var baseline = Report(
                new[] { Col(0, "score", ColumnValueType.Integer, distinct: 4, min: 1, max: 10, mean: 4) },
                new[]
                {
                    new QualityFinding
                    {
                        Kind = QualityCheckKind.DisguisedMissing,
                        Dimension = QualityDimension.Completeness,
                        Severity = QualitySeverity.Warning,
                        Column = 0, ColumnName = "score",
                        Breakdown = new[] { new ValueCount("999", 3) },
                    },
                });
            var current = Report(
                new[] { Col(0, "score", ColumnValueType.Integer, distinct: 5, min: 0, max: 10, mean: 5) },
                new[]
                {
                    new QualityFinding
                    {
                        Kind = QualityCheckKind.DisguisedMissing,
                        Dimension = QualityDimension.Completeness,
                        Severity = QualitySeverity.Warning,
                        Column = 0, ColumnName = "score",
                        Breakdown = new[] { new ValueCount("NA", 2) },
                    },
                });

            var diff = QualitySnapshotDiff.Compare(baseline, current, Tight());
            var appeared = Assert.Single(Of(diff, QualitySnapshotDiffKind.SentinelAppeared));
            Assert.Equal("NA", appeared.CurrentText);
            Assert.Equal(QualitySeverity.Info, appeared.Severity); // 후보 — 단정 금지
            var gone = Assert.Single(Of(diff, QualitySnapshotDiffKind.SentinelDisappeared));
            Assert.Equal("999", gone.BaselineText);

            Assert.Equal(0d, Assert.Single(Of(diff, QualitySnapshotDiffKind.NumericMinChanged)).CurrentNumber);
            Assert.Equal(1d, Assert.Single(Of(diff, QualitySnapshotDiffKind.NumericMeanChanged)).Delta);
            Assert.DoesNotContain(diff.Items, i => i.Kind == QualitySnapshotDiffKind.NumericMaxChanged);
        }

        [Fact]
        public void Duplicate_check_skip_is_not_reported_as_resolved()
        {
            var baseline = Report(new[] { Col(0, "id", ColumnValueType.Identifier) }, new[]
            {
                Finding(QualityCheckKind.DuplicateRows, "", count: 4),
            });
            var current = Report(new[] { Col(0, "id", ColumnValueType.Identifier) },
                Array.Empty<QualityFinding>(), dupSkipped: true);

            var diff = QualitySnapshotDiff.Compare(baseline, current, Tight());
            Assert.DoesNotContain(diff.Items, i => i.Kind is QualitySnapshotDiffKind.FindingResolved
                or QualitySnapshotDiffKind.FindingNew);
            var note = Assert.Single(Of(diff, QualitySnapshotDiffKind.CheckNotComparable));
            Assert.Equal(QualityCheckKind.DuplicateRows, note.CheckKind);
            Assert.True(note.Approximate);
        }

        [Fact]
        public void Row_count_change_and_diff_json_round_trip_kind()
        {
            var baseline = Report(new[] { Col(0, "id", ColumnValueType.Identifier) }, rows: 100, name: "old.csv");
            var current = Report(new[] { Col(0, "id", ColumnValueType.Identifier) }, rows: 140, name: "new.csv");
            var diff = QualitySnapshotDiff.Compare(baseline, current, Tight());
            Assert.Equal(40, diff.RowCountDelta);
            var row = Assert.Single(Of(diff, QualitySnapshotDiffKind.RowCountChanged));
            Assert.Equal(100d, row.BaselineNumber);
            Assert.Equal(140d, row.CurrentNumber);

            string json = QualitySnapshotDiff.Serialize(diff);
            Assert.Contains("\"RowCountChanged\"", json);
            Assert.Contains("\"Comparable\": true", json);
        }
    }
}
