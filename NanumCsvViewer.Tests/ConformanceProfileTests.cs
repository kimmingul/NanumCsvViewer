using System.Globalization;
using System.Text.Json;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Tests
{
    // 사용자 소유 적합성 프로파일 + OHDSI DQD 결과 JSON 가져오기.
    // 의료 규칙 팩은 없다. 아래 값은 스키마·계수 검증용 합성 데이터다.
    public class ConformanceProfileTests
    {
        private static QualityScanSource Src(string[] headers, string[][] rows, bool full = true) => new()
        {
            Headers = headers,
            RowAt = i => rows[i],
            RowCount = rows.Length,
            CoversAllRows = full,
        };

        private static ConformanceRunOptions Opt(string? dir = null, long? budget = null) => new()
        {
            ProfileDirectory = dir,
            DegreeOfParallelism = 2,
            MaxExamples = 20,
            ReferenceMemoryBudgetBytes = budget,
        };

        private static ConformanceProfile Profile(params ConformanceColumnSpec[] columns) => new()
        {
            SchemaVersion = 1,
            Name = "t",
            CaseInsensitiveColumnMatch = true,
            Columns = columns,
        };

        private static QualityFinding Fail(IEnumerable<QualityFinding> findings, QualityCheckKind kind)
            => Assert.Single(findings, f => f.Kind == kind && f.ViolationCount > 0);

        private static void AssertPredicateAgrees(QualityFinding f, string[][] rows)
        {
            Assert.NotNull(f.ViolationPredicate);
            long n = 0;
            foreach (var row in rows)
                if (f.ViolationPredicate!(row)) n++;
            Assert.Equal(f.ViolationCount, n);
        }

        [Fact]
        public void Required_values_count_blanks_and_null_tokens()
        {
            var rows = new[]
            {
                new[] { "1" },
                new[] { "" },
                new[] { "  " },
                new[] { "NA" },
                new[] { "x" },
            };
            var result = ConformanceProfileRunner.Run(
                Profile(new ConformanceColumnSpec { Column = "id", Required = true, Severity = QualitySeverity.Critical }),
                Src(new[] { "id" }, rows), Opt());

            var f = Fail(result.Findings, QualityCheckKind.ConformanceRequired);
            Assert.Equal(QualityDimension.Completeness, f.Dimension);
            Assert.Equal(QualitySeverity.Critical, f.Severity);
            Assert.Equal(3, f.ViolationCount);
            Assert.Equal(5, f.EvaluatedRows);
            Assert.Equal(new[] { 2L, 3L, 4L }, f.Examples.Select(e => e.SourceRow).ToArray());
            AssertPredicateAgrees(f, rows);
            Assert.False(f.ViolationPredicate!(new[] { "ok" }));
        }

        [Fact]
        public void Declared_type_matches_profiler_rules_and_skips_missing()
        {
            var rows = new[]
            {
                new[] { "1" },
                new[] { "1.5" },
                new[] { "abc" },
                new[] { "" },
                new[] { "NaN" },
                new[] { "2" },
            };
            var result = ConformanceProfileRunner.Run(
                Profile(new ConformanceColumnSpec { Column = "n", DeclaredType = "integer" }),
                Src(new[] { "n" }, rows), Opt());

            var f = Fail(result.Findings, QualityCheckKind.ConformanceType);
            Assert.Equal(QualityDimension.Conformance, f.Dimension);
            Assert.Equal(3, f.ViolationCount); // 1.5, abc, NaN
            Assert.Equal(1, f.SkippedRows);    // blank
            Assert.Equal("Integer", f.Label);
            AssertPredicateAgrees(f, rows);
            Assert.False(f.ViolationPredicate!(new[] { "3" }));
            Assert.False(f.ViolationPredicate(new[] { "NA" }));
        }

        [Fact]
        public void Max_length_uses_stored_text_and_skips_missing()
        {
            var rows = new[]
            {
                new[] { "abcd" },
                new[] { "abc" },
                new[] { "abc " }, // 저장 길이 4
                new[] { "   " },  // 결측
            };
            var result = ConformanceProfileRunner.Run(
                Profile(new ConformanceColumnSpec { Column = "code", MaxLength = 3 }),
                Src(new[] { "code" }, rows), Opt());

            var f = Fail(result.Findings, QualityCheckKind.ConformanceMaxLength);
            Assert.Equal(2, f.ViolationCount);
            Assert.Equal("len<=3", f.Label);
            AssertPredicateAgrees(f, rows);
        }

        [Fact]
        public void Inline_codelist_honors_case_and_trim()
        {
            var rows = new[]
            {
                new[] { "a" },
                new[] { " B " },
                new[] { "d" },
                new[] { "" },
            };
            var result = ConformanceProfileRunner.Run(
                Profile(new ConformanceColumnSpec
                {
                    Column = "code",
                    Codelist = new ConformanceCodelist
                    {
                        Values = new[] { "A", "B", "C" },
                        CaseInsensitive = true,
                        Trim = true,
                    },
                }),
                Src(new[] { "code" }, rows), Opt());

            var f = Fail(result.Findings, QualityCheckKind.ConformanceCodelist);
            Assert.Equal(1, f.ViolationCount);
            Assert.Equal("d", f.Examples[0].Value);
            Assert.Contains("3 codes", f.Breakdown[0].Value);
            AssertPredicateAgrees(f, rows);
        }

        [Fact]
        public void Codelist_file_loads_csv_column_and_text_list()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv-codelist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "codes.csv"), "label,code\nAlpha,A\nBeta,B\n");
                File.WriteAllText(Path.Combine(dir, "extra.txt"), "# comment\nC\n c \n");

                var rows = new[] { new[] { "A" }, new[] { "c" }, new[] { "Z" }, new[] { "NA" } };
                var result = ConformanceProfileRunner.Run(
                    Profile(new ConformanceColumnSpec
                    {
                        Column = "code",
                        Codelist = new ConformanceCodelist
                        {
                            File = "codes.csv",
                            ValueColumn = "code",
                            CaseInsensitive = true,
                            Trim = true,
                        },
                    }),
                    Src(new[] { "code" }, rows), Opt(dir));
                var csv = Fail(result.Findings, QualityCheckKind.ConformanceCodelist);
                Assert.Equal(2, csv.ViolationCount); // c, Z — A is allowed, NA skipped
                AssertPredicateAgrees(csv, rows);

                var textProfile = Profile(new ConformanceColumnSpec
                {
                    Column = "code",
                    Codelist = new ConformanceCodelist
                    {
                        File = "extra.txt",
                        CaseInsensitive = true,
                        Trim = true,
                    },
                });
                var text = ConformanceProfileRunner.Run(textProfile, Src(new[] { "code" }, rows), Opt(dir));
                var tf = Fail(text.Findings, QualityCheckKind.ConformanceCodelist);
                Assert.Equal(2, tf.ViolationCount); // A, Z — c matches, NA skipped
                AssertPredicateAgrees(tf, rows);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Pattern_and_range_agree_with_predicates()
        {
            var rows = new[]
            {
                new[] { "AB", "0" },
                new[] { "ab", "10" },
                new[] { "A-B", "-1" },
                new[] { "", "1000" },
                new[] { "OK", "nope" },
                new[] { "ZZ", "1000.1" },
            };
            var result = ConformanceProfileRunner.Run(new ConformanceProfile
            {
                SchemaVersion = 1,
                Columns = new[]
                {
                    new ConformanceColumnSpec { Column = "code", Pattern = "^[A-Z0-9]+$" },
                    new ConformanceColumnSpec
                    {
                        Column = "amount",
                        Min = 0,
                        Max = 1000,
                        MinInclusive = true,
                        MaxInclusive = false,
                    },
                },
            }, Src(new[] { "code", "amount" }, rows), Opt());

            var pattern = Fail(result.Findings, QualityCheckKind.ConformancePattern);
            Assert.Equal(2, pattern.ViolationCount); // ab, A-B; blank skipped
            AssertPredicateAgrees(pattern, rows);

            var range = Fail(result.Findings, QualityCheckKind.ConformanceRange);
            Assert.Equal(QualityDimension.Plausibility, range.Dimension);
            Assert.Equal("[0, 1000)", range.Label);
            // 0·10 통과. -1, 1000(상한 미포함), nope, 1000.1 위반.
            Assert.Equal(4, range.ViolationCount);
            AssertPredicateAgrees(range, rows);
        }

        [Fact]
        public void Concept_reference_filters_domain_and_skips_blank_keys()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv-concept-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "reference.csv"),
                    "id,domain,name\n1,Example,One\n2,Example,Two\n3,Other,Three\n");
                var rows = new[]
                {
                    new[] { "1" },
                    new[] { "3" },   // 다른 도메인 — 집합에 없음
                    new[] { "9" },   // 파일에 없음
                    new[] { "" },
                    new[] { "NA" },
                    new[] { "2" },
                };
                var result = ConformanceProfileRunner.Run(
                    Profile(new ConformanceColumnSpec
                    {
                        Column = "ref_id",
                        ConceptRef = new ConformanceConceptRef
                        {
                            File = "reference.csv",
                            KeyColumn = "id",
                            DomainColumn = "domain",
                            ExpectedDomain = "Example",
                            Trim = true,
                            SkipBlank = true,
                        },
                    }),
                    Src(new[] { "ref_id" }, rows), Opt(dir));

                var f = Fail(result.Findings, QualityCheckKind.ConformanceConcept);
                Assert.Equal(2, f.ViolationCount);
                Assert.Equal(2, f.SkippedRows);
                Assert.Contains("2 reference keys", f.Breakdown[0].Value);
                Assert.Contains("domain=Example", f.Label);
                Assert.Equal(new[] { 2L, 3L }, f.Examples.Select(e => e.SourceRow).ToArray());
                AssertPredicateAgrees(f, rows);
                Assert.False(f.ViolationPredicate!(new[] { "1" }));
                Assert.True(f.ViolationPredicate(new[] { "3" }));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        [Fact]
        public void Concept_cache_keeps_untrimmed_domains_apart()
        {
            // trim=false면 "D"와 " D "는 다른 비교 값이다. 캐시 키가 둘 다 Trim하면
            // 같은 파일을 두 검사가 한 집합으로 나눠 갖고, 맞는 키를 거부한다.
            string dir = Path.Combine(Path.GetTempPath(), "ncv-domain-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "reference.csv"), "id,domain\n10,D\n20, D \n");
                var rows = new[]
                {
                    new[] { "10", "20" },
                    new[] { "20", "10" },
                    new[] { "10", "10" },
                };
                var result = ConformanceProfileRunner.Run(new ConformanceProfile
                {
                    SchemaVersion = 1,
                    Columns = new[]
                    {
                        new ConformanceColumnSpec
                        {
                            Column = "exact",
                            ConceptRef = new ConformanceConceptRef
                            {
                                File = "reference.csv",
                                KeyColumn = "id",
                                DomainColumn = "domain",
                                ExpectedDomain = "D",
                                Trim = false,
                                CaseInsensitive = false,
                            },
                        },
                        new ConformanceColumnSpec
                        {
                            Column = "spaced",
                            ConceptRef = new ConformanceConceptRef
                            {
                                File = "reference.csv",
                                KeyColumn = "id",
                                DomainColumn = "domain",
                                ExpectedDomain = " D ",
                                Trim = false,
                                CaseInsensitive = false,
                            },
                        },
                    },
                }, Src(new[] { "exact", "spaced" }, rows), Opt(dir));

                var exact = Assert.Single(result.Findings, f =>
                    f.Kind == QualityCheckKind.ConformanceConcept && f.ColumnName == "exact" && f.ViolationCount > 0);
                var spaced = Assert.Single(result.Findings, f =>
                    f.Kind == QualityCheckKind.ConformanceConcept && f.ColumnName == "spaced" && f.ViolationCount > 0);

                Assert.Equal(1, exact.ViolationCount);   // "20"만 밖
                Assert.Equal(2, spaced.ViolationCount);  // "10" 두 행
                Assert.False(exact.ViolationPredicate!(new[] { "10", "20" }));
                Assert.True(exact.ViolationPredicate(new[] { "20", "10" }));
                Assert.False(spaced.ViolationPredicate!(new[] { "10", "20" }));
                Assert.True(spaced.ViolationPredicate(new[] { "20", "10" }));
                AssertPredicateAgrees(exact, rows);
                AssertPredicateAgrees(spaced, rows);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Concept_budget_throws_and_domain_filter_does_not_keep_other_keys()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var sb = new System.Text.StringBuilder("id,domain\n");
                for (int i = 0; i < 5; i++) sb.Append(i).Append(",Other\n");
                sb.Append("9,Example\n");
                File.WriteAllText(Path.Combine(dir, "reference.csv"), sb.ToString());

                var profile = Profile(new ConformanceColumnSpec
                {
                    Column = "ref_id",
                    ConceptRef = new ConformanceConceptRef
                    {
                        File = "reference.csv",
                        KeyColumn = "id",
                        DomainColumn = "domain",
                        ExpectedDomain = "Example",
                    },
                });
                var rows = new[] { new[] { "9" }, new[] { "1" } };

                // 키 1개(~610바이트 추정)는 들어가고 2개는 넘는다. 다른 도메인을 넣었다면 5+1개라 이 예산에서 실패한다.
                var ok = ConformanceProfileRunner.Run(profile, Src(new[] { "ref_id" }, rows), Opt(dir, budget: 1000));
                var f = Fail(ok.Findings, QualityCheckKind.ConformanceConcept);
                Assert.Equal(1, f.ViolationCount); // "1"은 Other라 집합 밖
                AssertPredicateAgrees(f, rows);

                var tooSmall = Assert.Throws<ConformanceBudgetException>(() =>
                    ConformanceProfileRunner.Run(profile, Src(new[] { "ref_id" }, rows), Opt(dir, budget: 200)));
                Assert.Contains("ref_id", tooSmall.Message);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Missing_required_column_is_table_level_without_predicate()
        {
            var result = ConformanceProfileRunner.Run(new ConformanceProfile
            {
                SchemaVersion = 1,
                CaseInsensitiveColumnMatch = false,
                RequiredColumns = new[] { "id", "gone" },
                Columns = new[]
                {
                    new ConformanceColumnSpec { Column = "gone", Required = true, DeclaredType = "Integer" },
                    new ConformanceColumnSpec { Column = "note", MaxLength = 4 },
                },
            }, Src(new[] { "id" }, new[] { new[] { "1" }, new[] { "2" } }), Opt());

            var missing = Assert.Single(result.Findings, f =>
                f.Kind == QualityCheckKind.ConformanceRequired && f.Column < 0 && f.ViolationCount > 0);
            Assert.Equal("gone", missing.ColumnName);
            Assert.Equal(QualitySeverity.Critical, missing.Severity);
            Assert.Equal(2, missing.ViolationCount);
            Assert.Null(missing.ViolationPredicate);
            Assert.Contains("not in this table", missing.Breakdown[0].Value);

            var notRun = Assert.Single(result.Findings, f => f.Label == "not in table");
            Assert.Equal("note", notRun.ColumnName);
            Assert.Equal(QualityCheckKind.ConformanceMaxLength, notRun.Kind);
            Assert.Equal(QualitySeverity.Info, notRun.Severity);
            Assert.Null(notRun.ViolationPredicate);
            Assert.Equal(2, result.ChecksNotRun);
        }

        [Fact]
        public void Column_match_respects_case_option()
        {
            var spec = new ConformanceColumnSpec { Column = "code", Required = true };
            var rows = new[] { new[] { "x" } };

            var loose = ConformanceProfileRunner.Run(
                new ConformanceProfile { SchemaVersion = 1, CaseInsensitiveColumnMatch = true, Columns = new[] { spec } },
                Src(new[] { "CODE" }, rows), Opt());
            Assert.Equal(0, FailOrZero(loose.Findings));
            Assert.DoesNotContain(loose.Findings, f => f.Label == "not in table");

            var strict = ConformanceProfileRunner.Run(
                new ConformanceProfile { SchemaVersion = 1, CaseInsensitiveColumnMatch = false, Columns = new[] { spec } },
                Src(new[] { "CODE" }, rows), Opt());
            var missing = Assert.Single(strict.Findings, f => f.Column < 0 && f.ViolationCount > 0);
            Assert.Equal("code", missing.ColumnName);
            Assert.Null(missing.ViolationPredicate);
        }

        [Fact]
        public void Schema_unknown_fields_and_validation_messages()
        {
            var withExtra = ConformanceProfileJson.Deserialize("""
                {
                  "schemaVersion": 1,
                  "futurePack": { "age": [0, 120] },
                  "columns": [ { "column": "id", "required": true, "vendorHint": "ignore" } ]
                }
                """);
            Assert.Equal("id", Assert.Single(withExtra.Columns).Column);
            Assert.True(withExtra.MatchColumnsIgnoreCase); // 생략 = true

            var omitted = ConformanceProfileJson.Deserialize("""{ "columns": [ { "column": "id", "maxLength": 2 } ] }""");
            Assert.Equal(1, omitted.SchemaVersion == 0 ? 1 : omitted.SchemaVersion);

            var tooNew = Assert.Throws<ConformanceSchemaException>(() =>
                ConformanceProfileJson.Deserialize("""{ "schemaVersion": 2, "columns": [] }"""));
            Assert.Equal(2, tooNew.SchemaVersion);
            Assert.Contains("not supported", tooNew.Message);

            var badPattern = Assert.Throws<ConformanceProfileException>(() =>
                ConformanceProfileJson.Deserialize("""
                    { "schemaVersion": 1, "columns": [ { "column": "note", "pattern": "(" } ] }
                    """));
            Assert.Contains("note", badPattern.Message);
            Assert.Contains("regular expression", badPattern.Message);

            var badType = Assert.Throws<ConformanceProfileException>(() =>
                ConformanceProfileJson.Deserialize("""
                    { "schemaVersion": 1, "columns": [ { "column": "n", "declaredType": "money" } ] }
                    """));
            Assert.Contains("n", badType.Message);
            Assert.Contains("money", badType.Message);

            string json = ConformanceProfileJson.Serialize(withExtra);
            var again = ConformanceProfileJson.Deserialize(json);
            Assert.True(again.Columns[0].Required);

            var example = ConformanceProfileJson.Deserialize(ConformanceProfileJson.ExampleJson());
            Assert.Equal("example-conformance", example.Name);
            Assert.Contains(example.Columns, c => c.ConceptRef is not null);

            var noDir = Assert.Throws<ConformanceProfileException>(() =>
                ConformanceProfileRunner.Run(example, Src(new[] { "ref_id" }, new[] { new[] { "1" } }), Opt()));
            Assert.Contains("reference.csv", noDir.Message);
        }

        [Fact]
        public void Absent_concept_column_does_not_require_the_reference_file()
        {
            var example = ConformanceProfileJson.Deserialize(ConformanceProfileJson.ExampleJson());
            var result = ConformanceProfileRunner.Run(example, Src(new[] { "other" }, new[] { new[] { "1" } }), Opt());
            Assert.Contains(result.Findings, f => f.ColumnName == "id" && f.Label == "column absent");
            Assert.Contains(result.Findings, f => f.ColumnName == "ref_id" && f.Label == "not in table");
            Assert.DoesNotContain(result.Findings, f => f.ViolationPredicate is not null);
        }

        [Fact]
        public void Dqd_fixture_parses_kahn_fields_and_has_no_row_predicate()
        {
            // 픽스처: NanumCsvViewer.Tests/Fixtures/dqd-check-results.sample.json
            // 출처는 파일의 _source. OHDSI DQD CheckResults 스키마의 합성 표본.
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dqd-check-results.sample.json");
            string json = File.ReadAllText(path);
            var result = DqdResultsImport.Import(json, headers: new[] { "gender_concept_id", "year_of_birth" });

            Assert.Equal(6, result.ChecksRead);
            Assert.Equal(6, result.ChecksImported);
            Assert.Equal(2, result.FailedChecks); // isRequired, cdmTable. measureValueCompleteness는 isError.
            Assert.Equal(1, result.ErrorChecks);
            Assert.Equal(1, result.NotApplicableChecks);

            var required = Assert.Single(result.Findings, f => f.Label == "isRequired");
            Assert.Equal(QualityCheckKind.DqdImported, required.Kind);
            Assert.Equal(QualityDimension.Completeness, required.Dimension);
            Assert.Equal(QualitySeverity.Critical, required.Severity);
            Assert.Equal(12, required.ViolationCount);
            Assert.Equal(1000, required.EvaluatedRows);
            Assert.Equal(0, required.Column); // gender_concept_id
            Assert.Equal("PERSON.gender_concept_id", required.ColumnName);
            Assert.Null(required.ViolationPredicate);
            Assert.Empty(required.Examples);
            Assert.Contains("imported from DQD", required.Breakdown[0].Value);
            Assert.Contains("threshold 0", required.Breakdown[0].Value);
            Assert.Contains("Completeness/Required fields", required.Breakdown[0].Value);

            var concept = Assert.Single(result.Findings, f => f.Label == "isStandardValidConcept#8507");
            Assert.Equal(QualityDimension.Conformance, concept.Dimension);
            Assert.Equal(QualitySeverity.Info, concept.Severity); // failed 0, threshold 5, 0.3% — 실패 플래그를 그대로 믿는다
            Assert.Equal("MEASUREMENT.measurement_concept_id", concept.ColumnName);

            var err = Assert.Single(result.Findings, f => f.Label == "measureValueCompleteness");
            Assert.Equal(QualitySeverity.Warning, err.Severity);
            Assert.True(err.Approximate); // 건수 필드 없음
            Assert.Contains("error:", err.Breakdown[0].Value);
            Assert.Contains("does not exist", err.Breakdown[0].Value);
            Assert.Null(err.ViolationPredicate);

            var na = Assert.Single(result.Findings, f => f.Label == "plausibleValueLow");
            Assert.Equal(QualityDimension.Plausibility, na.Dimension);
            Assert.Equal(QualitySeverity.Info, na.Severity);
            Assert.Equal(1, na.Column); // year_of_birth
            Assert.Contains("not applicable", na.Breakdown[0].Value);

            var table = Assert.Single(result.Findings, f => f.Label == "cdmTable");
            Assert.Equal("NOTE", table.ColumnName);
            Assert.Equal(-1, table.Column);
            Assert.Equal(QualitySeverity.Critical, table.Severity);
            Assert.True(table.Approximate);
            Assert.Contains("violated-row count not reported", table.Breakdown[0].Value);
        }

        [Fact]
        public void Dqd_tolerates_missing_fields_boxed_scalars_and_snake_case()
        {
            const string sparse = """
                { "CheckResults": [ { "checkName": "onlyName" }, { "notAnObject": true }, 1 ] }
                """;
            var sparseResult = DqdResultsImport.Import(sparse);
            Assert.Equal(2, sparseResult.ChecksRead); // 객체는 필드가 없어도 검사 행. 숫자 1만 건너뜀.
            Assert.NotEmpty(sparseResult.Warnings);
            var only = Assert.Single(sparseResult.Findings, f => f.Label == "onlyName");
            Assert.Equal(QualitySeverity.Info, only.Severity);
            Assert.True(only.Approximate);
            Assert.Null(only.ViolationPredicate);
            Assert.Contains("category not reported", only.Breakdown[0].Value);
            Assert.Contains("failed flag not reported", only.Breakdown[0].Value);
            Assert.Contains(sparseResult.Findings, f => f.Label == "(unnamed check)" && f.ViolationPredicate is null);

            // jsonlite::toJSON 기본(auto_unbox=false)은 스칼라를 길이 1 배열로 쓴다.
            const string boxed = """
                {
                  "CheckResults": [
                    {
                      "checkName": ["isRequired"],
                      "checkLevel": ["FIELD"],
                      "cdmTableName": ["PERSON"],
                      "cdmFieldName": ["gender_concept_id"],
                      "category": ["Completeness"],
                      "numViolatedRows": [4],
                      "numDenominatorRows": [8],
                      "pctViolatedRows": [0.5],
                      "failed": [1],
                      "isError": [0],
                      "notApplicable": [0],
                      "thresholdValue": [0],
                      "checkDescription": ["boxed"]
                    }
                  ]
                }
                """;
            var boxedFinding = Assert.Single(DqdResultsImport.Import(boxed).Findings);
            Assert.Equal(4, boxedFinding.ViolationCount);
            Assert.Equal(8, boxedFinding.EvaluatedRows);
            Assert.Equal(QualitySeverity.Critical, boxedFinding.Severity);
            Assert.Contains("boxed", boxedFinding.Breakdown[0].Value);
            Assert.Null(boxedFinding.ViolationPredicate);

            const string snake = """
                {
                  "check_results": [
                    {
                      "check_name": "cdmField",
                      "check_level": "FIELD",
                      "cdm_table_name": "VISIT_OCCURRENCE",
                      "cdm_field_name": "visit_start_date",
                      "num_violated_rows": "2",
                      "num_denominator_rows": "10",
                      "failed": "1",
                      "is_error": "0",
                      "not_applicable": "0",
                      "category": "conformance"
                    }
                  ]
                }
                """;
            var snakeFinding = Assert.Single(DqdResultsImport.Import(snake).Findings);
            Assert.Equal("cdmField", snakeFinding.Label);
            Assert.Equal(2, snakeFinding.ViolationCount);
            Assert.Equal("VISIT_OCCURRENCE.visit_start_date", snakeFinding.ColumnName);
            Assert.Equal(QualityDimension.Conformance, snakeFinding.Dimension);

            var filtered = DqdResultsImport.Import(snake, new DqdResultsImport.Options { TableName = "person" });
            Assert.Empty(filtered.Findings);
            Assert.Equal(1, filtered.ChecksFilteredOut);

            var kept = DqdResultsImport.Import(boxed, new DqdResultsImport.Options { TableName = "person" });
            Assert.Single(kept.Findings);

            var missing = Assert.Throws<DqdImportException>(() => DqdResultsImport.Import("""{ "Metadata": {} }"""));
            Assert.Contains("CheckResults", missing.Message);
        }

        [Fact]
        public void Session_checks_are_not_marked_resolved_when_not_rerun()
        {
            Assert.True(QualitySessionChecks.IsUserRun(QualityCheckKind.ConformanceConcept));
            Assert.True(QualitySessionChecks.IsUserRun(QualityCheckKind.DqdImported));
            Assert.False(QualitySessionChecks.IsUserRun(QualityCheckKind.MissingRate));

            var dqd = new QualityFinding
            {
                Kind = QualityCheckKind.DqdImported,
                Dimension = QualityDimension.Completeness,
                Severity = QualitySeverity.Critical,
                ColumnName = "PERSON.gender_concept_id",
                Label = "isRequired",
                ViolationCount = 4,
            };
            var conformance = new QualityFinding
            {
                Kind = QualityCheckKind.ConformanceCodelist,
                Dimension = QualityDimension.Conformance,
                Severity = QualitySeverity.Warning,
                Column = 0,
                ColumnName = "code",
                Label = "inline",
                ViolationCount = 1,
            };
            var baseline = Report(dqd, conformance);
            var current = Report(); // 프로파일만 있고 세션 검사를 다시 안 함
            var diff = QualitySnapshotDiff.Compare(baseline, current);
            var notRechecked = diff.Items.Where(i => i.Kind == QualitySnapshotDiffKind.FindingNotRechecked).ToArray();
            Assert.Equal(2, notRechecked.Length);
            Assert.Contains(notRechecked, i => i.CheckKind == QualityCheckKind.DqdImported && i.RuleName == "isRequired");
            Assert.Contains(notRechecked, i => i.CheckKind == QualityCheckKind.ConformanceCodelist && i.RuleName == "inline");
            Assert.DoesNotContain(diff.Items, i => i.Kind == QualitySnapshotDiffKind.FindingResolved);
        }

        private static int FailOrZero(IEnumerable<QualityFinding> findings)
            => findings.Count(f => f.ViolationCount > 0);

        private static QualityReport Report(params QualityFinding[] findings) => new()
        {
            RowsScanned = 10,
            ScannedFully = true,
            ElapsedSeconds = 0,
            Columns = Array.Empty<QualityColumnProfile>(),
            Findings = findings,
        };
    }
}
