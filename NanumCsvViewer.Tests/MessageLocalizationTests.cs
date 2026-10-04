using System.Globalization;
using System.Text.RegularExpressions;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// 변수 매핑·데이터 품질이 실제 엔진 경로에서 만드는 영어 문장이 한국어 화면에서 한국어로 나오는지.
    /// "변환 규칙이 있는가"만 보지 않고, 변환 결과에 영어 문장이 남지 않았는지(데이터 값·컬럼 이름·표준 코드 제외)를 검사한다.
    /// 남을 수 있는 영어는 호출자가 허용 목록으로 명시한다: 사용자 데이터(컬럼·필드·파일 이름, 값), 표준 코드(타입 이름·JSON 키), 프레임워크 원문.
    /// </summary>
    public class MessageLocalizationTests
    {
        private static readonly Regex Word = new("[A-Za-z][A-Za-z0-9]*", RegexOptions.Compiled);

        // 번역 대상이 아닌 고정 표기: 포맷·표준·알고리즘 이름.
        private static readonly string[] Standard =
        {
            "JSON", "CSV", "DQD", "OMOP", "CDISC", "OHDSI", "TopK", "NET", "Jaro", "Winkler",
            // 사용자가 쓰는 파일 형식의 키 이름(번역하면 안 되는 스키마 식별자)과 표기법.
            "CheckResults", "cdmTableName", "cdmFieldName", "len", "conceptRef", "expectedDomain", "domainColumn", "keyColumn",
            "valueColumn", "hasHeader", "maxLength", "requiredColumns", "referenceMemoryBudgetBytes", "codelist", "values",
        };

        private static readonly string[] TypeNames = Enum.GetNames<ColumnValueType>().Concat(Enum.GetNames<SpecTypeKind>()).ToArray();

        private static HashSet<string> Allowed(params string[] dataText)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string w in Standard.Concat(TypeNames)) set.Add(w);
            foreach (string text in dataText)
                foreach (Match m in Word.Matches(text)) set.Add(m.Value);
            return set;
        }

        // 근거 열은 160자에서 "..."로 잘리므로 데이터 낱말이 중간에서 끊긴 조각(예: CONC...)도 데이터로 본다.
        private static string[] Residue(string korean, HashSet<string> allowed)
            => Word.Matches(korean).Select(m => m.Value)
                .Where(w => w.Length >= 3 && !allowed.Contains(w) && !allowed.Any(a => a.StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                .Distinct().ToArray();

        private static void AssertKorean(string english, params string[] dataText)
        {
            string? ko = ErrorText.TryKorean(english);
            Assert.True(ko is not null, "한국어 규칙이 없는 메시지: " + english);
            Assert.True(Regex.IsMatch(ko!, "[가-힣]"), "한글이 없음: " + ko);
            var left = Residue(ko!, Allowed(dataText));
            Assert.True(left.Length == 0, $"영어가 남음 [{string.Join(", ", left)}]: {ko}  <= {english}");
        }

        private static void AssertKoreanDisplay(string korean, params string[] dataText)
        {
            var left = Residue(korean, Allowed(dataText));
            Assert.True(left.Length == 0, $"영어가 남음 [{string.Join(", ", left)}]:{Environment.NewLine}{korean}");
        }

        private static string MessageOf(Action action)
        {
            var ex = Record.Exception(action);
            Assert.NotNull(ex);
            return ex!.Message;
        }

        // Loc.CurrentLanguage는 CurrentUICulture를 따른다. 테스트 끝에 원복한다.
        private sealed class KoreanScope : IDisposable
        {
            private readonly CultureInfo _saved = CultureInfo.CurrentUICulture;
            public KoreanScope() => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
            public void Dispose() => CultureInfo.CurrentUICulture = _saved;
        }

        // ------------------------------------------------------------------ 변수 매핑

        private const string OmopSpec = """
            cdmTableName,cdmFieldName,isRequired,cdmDatatype,userGuidance,etlConventions,fkTableName,fkDomain
            PERSON,person_id,Yes,integer,"Unique identifier for the person",,,
            PERSON,gender_concept_id,Yes,integer,"A foreign key that refers to a standard concept identifier for the gender of the person",,CONCEPT,Gender
            PERSON,birth_datetime,No,datetime,"The date and time of birth of the person",,,
            VISIT_OCCURRENCE,visit_occurrence_id,Yes,integer,"Unique identifier for each visit",,,
            """;

        private const string CdiscSpec = """
            Dataset,Variable,Label,Type,Length,Codelist,Core
            DM,USUBJID,Unique Subject Identifier,Char,20,,Req
            DM,BRTHDTC,Date/Time of Birth,Char,19,,Perm
            DM,AGE,Age,Num,8,,Exp
            AE,AESEQ,Sequence Number,Num,8,,Req
            """;

        private static readonly SourceColumn[] MappingSources =
        {
            new("person_id", ColumnValueType.Integer, Sample: ["1", "2", "3"]),
            new("gndr", ColumnValueType.Integer, VariableLabel: "Gender of the person", Sample: ["8507", "8532", "8507"]),
            new("dob", ColumnValueType.Date, VariableLabel: "Date of birth", Sample: ["1990-01-02", "1981-05-05", "2001-12-01"]),
            new("subjid", ColumnValueType.Identifier, VariableLabel: "Unique Subject Identifier"),
            new("age", ColumnValueType.String, Sample: ["a", "b", "c"]),
            new("zzqx", ColumnValueType.Empty),
        };

        [Theory]
        [InlineData(OmopSpec, "omop-spec.csv")]
        [InlineData(CdiscSpec, "cdisc-spec.csv")]
        public void Mapping_korean_format_and_csv_have_no_untranslated_english(string specCsv, string specName)
        {
            var spec = VariableMapping.Parse(specCsv, specName);
            var opt = new MappingSuggestOptions { SampleRowsRead = 3, SampleRowCap = 3, SampleCapped = true };
            var result = VariableMapping.Suggest(MappingSources, spec, opt);

            // 모든 후보 근거가 규칙으로 번역된다(실제 엔진이 만든 문장).
            var reasons = result.Columns.SelectMany(c => c.Candidates).SelectMany(c => c.Reasons).ToList();
            Assert.NotEmpty(reasons);
            Assert.Contains(reasons, r => r.Code == "name");
            var data = MappingSources.Select(s => s.Name)
                .Concat(spec.Fields.SelectMany(f => new[] { f.Table, f.Name, f.DataType ?? "" }))
                .Append(specName).ToArray();
            foreach (var reason in reasons) AssertKorean(reason.Text, data);

            string ko = VariableMapping.Format(result, spec, opt, korean: true);
            AssertKoreanDisplay(ko, data);
            Assert.Contains("명세", ko);
            if (result.Columns.Any(c => c.Candidates.Count == 0)) Assert.Contains("후보 없음", ko);
            string en = VariableMapping.Format(result, spec, opt);
            Assert.Contains("Suggestions, not assertions", en);
            Assert.DoesNotMatch("[가-힣]", en);

            string csvKo = VariableMapping.ExportCsv(result, korean: true);
            Assert.StartsWith("source column,target table,target field,score,reasons", csvKo); // 스키마 머리글은 안정
            AssertKoreanDisplay(string.Join(Environment.NewLine, csvKo.Split('\n').Skip(1)), data);
            Assert.DoesNotMatch("[가-힣]", VariableMapping.ExportCsv(result));
        }

        [Fact]
        public void Mapping_errors_from_real_paths_have_korean_text()
        {
            var spec = VariableMapping.Parse(OmopSpec, "omop-spec.csv");
            string dir = Path.Combine(Path.GetTempPath(), "nanum-mapping-l10n-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string empty = Path.Combine(dir, "empty.csv");
                File.WriteAllText(empty, "");
                string blank = Path.Combine(dir, "blankheader.csv");
                File.WriteAllText(blank, ",,\n1,2,3\n");
                string headerOnly = Path.Combine(dir, "headeronly.csv");
                File.WriteAllText(headerOnly, "cdmTableName,cdmFieldName\n");
                string unknown = Path.Combine(dir, "unknown.csv");
                File.WriteAllText(unknown, "foo,bar\n1,2\n");
                string huge = Path.Combine(dir, "huge.csv");
                File.WriteAllText(huge, "cdmTableName,cdmFieldName\n" + string.Join("\n", Enumerable.Range(0, VariableMapping.MaxSpecFields + 1).Select(i => "T,f" + i)));

                var messages = new List<string>
                {
                    MessageOf(() => VariableMapping.ReadHeader(empty)),
                    MessageOf(() => VariableMapping.ReadHeader(blank)),
                    MessageOf(() => VariableMapping.Load(headerOnly)),
                    MessageOf(() => VariableMapping.Load(unknown)),
                    MessageOf(() => VariableMapping.Load(huge)),
                    MessageOf(() => VariableMapping.Parse("")),
                    MessageOf(() => VariableMapping.Parse(",,\n1,2,3\n")),
                    MessageOf(() => VariableMapping.Parse("a,b\n1,2\n", roles: new SpecColumnRoles(SpecLayoutKind.Custom, 9))),
                    MessageOf(() => VariableMapping.Suggest(MappingSources, spec, new MappingSuggestOptions { TopK = 0 })),
                    MessageOf(() => VariableMapping.Suggest(MappingSources, spec, new MappingSuggestOptions { AcceptScore = 101 })),
                    MessageOf(() => VariableMapping.Suggest(MappingSources, spec, new MappingSuggestOptions { TargetTable = "NOT_A_TABLE" })),
                };
                foreach (string message in messages)
                    AssertKorean(message, dir, "NOT_A_TABLE", "PERSON", "VISIT_OCCURRENCE", "Dataset", "Variable",
                        "empty", "blankheader", "headeronly", "unknown", "huge");

                // 파일이 없을 때 이어 붙는 OS/프레임워크 원문은 그대로 남는다(명세 경로는 한글 문장 안에 보존).
                string missing = Path.Combine(dir, "missing.csv");
                string notFound = MessageOf(() => VariableMapping.ReadHeader(missing));
                string koNotFound = ErrorText.TryKorean(notFound) ?? "";
                Assert.StartsWith("명세 '", koNotFound);
                Assert.Contains(missing, koNotFound);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ------------------------------------------------------------------ 적합성 프로파일 검증(JSON → 구조)

        public static TheoryData<string> ProfileJsonCases() => new()
        {
            """{"schemaVersion":2,"columns":[{"column":"a","required":true}]}""",
            """{"referenceMemoryBudgetBytes":0,"columns":[{"column":"a","required":true}]}""",
            """{"requiredColumns":[" "]}""",
            """{"columns":[null]}""",
            """{"columns":[{"required":true}]}""",
            """{"columns":[{"column":"alpha"}]}""",
            """{"columns":[{"column":"alpha","declaredType":"Banana"}]}""",
            """{"columns":[{"column":"alpha","maxLength":-1}]}""",
            """{"columns":[{"column":"alpha","min":5,"max":1}]}""",
            """{"columns":[{"column":"alpha","codelist":{}}]}""",
            """{"columns":[{"column":"alpha","codelist":{"file":"c.csv","hasHeader":false,"valueColumn":"x"}}]}""",
            """{"columns":[{"column":"alpha","conceptRef":{}}]}""",
            """{"columns":[{"column":"alpha","conceptRef":{"file":"r.csv"}}]}""",
            """{"columns":[{"column":"alpha","conceptRef":{"file":"r.csv","keyColumn":"k","expectedDomain":"D"}}]}""",
            "",
            "   ",
            "{",
            """{"columns":[{"column":"alpha","maxLength":"x"}]}""",
            """{"columns":[{"column":"alpha","required":true,"severity":"Bogus"}]}""",
            """{"columns": [ {"column":"alpha", "required": tru} ]}""",
            """{"columns":{"column":"alpha"}}""",
        };

        [Theory]
        [MemberData(nameof(ProfileJsonCases))]
        public void Conformance_profile_load_errors_have_korean_text(string json)
        {
            string message = MessageOf(() => ConformanceProfileJson.Deserialize(json));
            string? ko = ErrorText.TryKorean(message);
            Assert.True(ko is not null, "한국어 규칙이 없는 메시지: " + message);
            // 속성 이름(JSON 키), 사용자가 쓴 컬럼 이름, 프레임워크가 알려 준 형식 이름(열거형)은 영어 그대로 둔다.
            var left = Residue(ko!, Allowed(
                "alpha", "column", "columns", "requiredColumns", "referenceMemoryBudgetBytes", "maxLength", "min", "max", "values", "file",
                "keyColumn", "valueColumn", "hasHeader", "domainColumn", "expectedDomain", "codelist", "conceptRef", "QualitySeverity",
                "null", "false", "true", "Banana", "tru", "Bogus", "severity", "required"));
            Assert.True(left.Length == 0, $"영어가 남음 [{string.Join(", ", left)}]: {ko}  <= {message}");
        }

        [Fact]
        public void Conformance_invalid_regex_message_keeps_only_the_framework_detail_in_english()
        {
            string frameworkDetail = Record.Exception(() => new Regex("(", RegexOptions.CultureInvariant))!.Message;
            string message = MessageOf(() => ConformanceProfileJson.Deserialize(
                """{"columns":[{"column":"alpha","pattern":"("}]}"""));
            string ko = ErrorText.TryKorean(message) ?? "";
            Assert.StartsWith("컬럼 'alpha'의 패턴이 올바른 .NET 정규식이 아닙니다: ", ko);
            Assert.EndsWith(frameworkDetail, ko);
        }

        // ------------------------------------------------------------------ 적합성 프로파일 실행 경로(파일·예산·결과 표시)

        private static QualityScanSource Src(string[] headers, string[][] rows) => new()
        {
            Headers = headers,
            RowAt = i => rows[i],
            RowCount = rows.Length,
            CoversAllRows = false, // 잘림 안내 문구도 만든다
        };

        private static ConformanceRunOptions Opt(string? dir, long? budget = null) => new()
        {
            ProfileDirectory = dir,
            DegreeOfParallelism = 2,
            ReferenceMemoryBudgetBytes = budget,
        };

        private static ConformanceProfile Profile(params ConformanceColumnSpec[] columns) => new()
        {
            SchemaVersion = 1,
            Name = "t",
            CaseInsensitiveColumnMatch = true,
            Columns = columns,
        };

        [Fact]
        public void Conformance_run_errors_have_korean_text_including_multi_line_file_errors()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nanum-conf-l10n-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string[] headers = { "alpha", "beta", "gamma" };
                string[][] rows = { new[] { "1", "x", "y" }, new[] { "2", "z", "w" } };
                File.WriteAllText(Path.Combine(dir, "wrongheader.csv"), "other,domain\n1,D\n");
                File.WriteAllText(Path.Combine(dir, "emptyref.csv"), "");
                File.WriteAllText(Path.Combine(dir, "nodomain.csv"), "id\n1\n");

                ConformanceColumnSpec Codes(string col, string file, bool? hasHeader = null, string? valueColumn = null) => new()
                {
                    Column = col,
                    Codelist = new ConformanceCodelist { File = file, HasHeader = hasHeader, ValueColumn = valueColumn },
                };
                ConformanceColumnSpec Concept(string col, string file, string key = "id", string? domainColumn = null, string? expected = null) => new()
                {
                    Column = col,
                    ConceptRef = new ConformanceConceptRef { File = file, KeyColumn = key, DomainColumn = domainColumn, ExpectedDomain = expected },
                };

                var cases = new (ConformanceProfile Profile, string? Dir)[]
                {
                    (Profile(Codes("alpha", "gone.csv")), dir),
                    (Profile(Concept("alpha", "gone.csv")), dir),
                    (Profile(Codes("alpha", "relative.csv")), null),
                    (Profile(Concept("alpha", "wrongheader.csv", key: "id")), dir),
                    (Profile(Concept("alpha", "emptyref.csv")), dir),
                    (Profile(Concept("alpha", "nodomain.csv", expected: "D")), dir),
                    (Profile(Codes("alpha", "emptyref.csv", hasHeader: true, valueColumn: "code")), dir),
                    // 컬럼 두 개가 각각 실패하면 줄바꿈으로 이어진 한 메시지가 된다.
                    (Profile(Codes("alpha", "gone.csv"), Codes("beta", "gone2.csv")), dir),
                };
                int multiLine = 0;
                foreach (var (profile, d) in cases)
                {
                    string message = MessageOf(() => ConformanceProfileRunner.Run(profile, Src(headers, rows), Opt(d)));
                    if (message.Contains('\n')) multiLine++;
                    AssertKorean(message, dir, "alpha", "beta", "gamma", "gone", "gone2", "relative", "wrongheader", "emptyref", "nodomain", "csv", "keyColumn", "domainColumn", "expectedDomain", "valueColumn", "hasHeader", "id", "other", "domain");
                }
                Assert.Equal(1, multiLine);

                // 예산 초과.
                var budget = Profile(new ConformanceColumnSpec { Column = "alpha", Codelist = new ConformanceCodelist { Values = Enumerable.Range(0, 50).Select(i => "code" + i).ToArray() } });
                string budgetMessage = MessageOf(() => ConformanceProfileRunner.Run(budget, Src(headers, rows), Opt(dir, budget: 300)));
                AssertKorean(budgetMessage, "alpha", "codelist", "inline");

                // 정규식 시간 초과(치명적 역추적).
                string[][] catastrophic = { new[] { new string('a', 40) + "!", "x", "y" } };
                var slow = Profile(new ConformanceColumnSpec { Column = "alpha", Pattern = "^(a+)+$" });
                string timeout = MessageOf(() => ConformanceProfileRunner.Run(slow, Src(headers, catastrophic), Opt(dir)));
                AssertKorean(timeout, "alpha");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Conformance_findings_render_in_korean_without_engine_english()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nanum-conf-render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "ref.csv"), "id,domain\n1,D\n2,D\n");
                string[] headers = { "alpha", "Beta", "beta", "gamma" };
                string[][] rows = { new[] { "1", "x", "y", "7" }, new[] { "9", "z", "w", "8" } };
                var profile = new ConformanceProfile
                {
                    SchemaVersion = 1,
                    Name = "render",
                    CaseInsensitiveColumnMatch = true, // Beta/beta 모호 → 안내 문구
                    RequiredColumns = new[] { "absentreq" },
                    Columns = new[]
                    {
                        new ConformanceColumnSpec { Column = "alpha", Required = true, DeclaredType = "String", MaxLength = 1 },
                        new ConformanceColumnSpec { Column = "beta", Codelist = new ConformanceCodelist { Values = new[] { "y" } } },
                        new ConformanceColumnSpec { Column = "gamma", ConceptRef = new ConformanceConceptRef { File = "ref.csv", KeyColumn = "id" } },
                        new ConformanceColumnSpec { Column = "absentopt", DeclaredType = "Integer" },
                        new ConformanceColumnSpec { Column = "gamma", Min = 0, Max = 7.5 },
                    },
                };
                var result = ConformanceProfileRunner.Run(profile, Src(headers, rows), Opt(dir));
                // 안내(Notes)는 UI에 쓰이지 않아도 영어로 새지 않게 번역 규칙이 있다.
                Assert.NotEmpty(result.Notes);
                foreach (string note in result.Notes) AssertKorean(note, "Beta", "beta");
                Assert.Contains(result.Findings, f => f.Label == "column absent");
                Assert.Contains(result.Findings, f => f.Label == "not in table");

                using var _ = new KoreanScope();
                foreach (var f in result.Findings)
                {
                    string shown = QualityText.CheckTitle(f) + " | " + QualityText.Detail(f);
                    AssertKoreanDisplay(shown, "alpha", "beta", "gamma", "absentreq", "absentopt", "ref", "csv", "id", "inline");
                }
                Assert.Contains(result.Findings, f => QualityText.CheckTitle(f).Contains("컬럼 없음"));
                Assert.Contains(result.Findings, f => QualityText.Detail(f).Contains("참조 키 2개"));
                Assert.Contains(result.Findings, f => QualityText.Detail(f).Contains("결측이 아닌 모든 텍스트"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ------------------------------------------------------------------ DQD 가져오기

        [Fact]
        public void Dqd_import_notes_warnings_and_errors_display_in_korean_with_source_data_kept()
        {
            const string json = """
                { "CheckResults": [
                    { "checkName": "alphaCheck", "checkLevel": "FIELD", "category": "Conformance", "checkDescription": "betaDescription",
                      "cdmTableName": "PERSON", "cdmFieldName": "gamma", "numViolatedRows": 3, "numDenominatorRows": 10,
                      "pctViolatedRows": 0.3, "thresholdValue": 0.1, "failed": 1 },
                    { "checkName": "deltaCheck", "error": "epsilonError", "cdmTableName": "PERSON" },
                    { "checkName": "zetaCheck", "notApplicable": 1, "notApplicableReason": "etaReason" },
                    { "category": "Plausibility" },
                    { "cdmTableName": "PERSON", "checkName": "thetaCheck", "numViolatedRows": 0 },
                    5
                ] }
                """;
            var result = DqdResultsImport.Import(json);
            Assert.NotEmpty(result.Warnings);
            foreach (string warning in result.Warnings) AssertKorean(warning);

            using var _ = new KoreanScope();
            var data = new[] { "alphaCheck", "betaDescription", "PERSON", "gamma", "deltaCheck", "epsilonError", "zetaCheck", "etaReason", "thetaCheck", "FIELD", "pctViolatedRows", "Conformance", "Plausibility" };
            foreach (var f in result.Findings)
            {
                string shown = QualityText.CheckTitle(f) + " | " + QualityText.Detail(f);
                AssertKoreanDisplay(shown, data);
            }
            string all = string.Join("\n", result.Findings.Select(f => QualityText.Detail(f)));
            Assert.Contains("위반 행 수가 보고되지 않음", all);
            Assert.Contains("해당 없음: etaReason", all);
            Assert.Contains("오류: epsilonError", all);
            Assert.Contains("이름 없는 검사", string.Join("\n", result.Findings.Select(QualityText.CheckTitle)));

            foreach (string text in new[] { "", "   ", "{", """{ "Metadata": {} }""", "[1" })
            {
                string message = MessageOf(() => DqdResultsImport.Import(text));
                AssertKorean(message, "CheckResults", "Metadata");
            }
        }

        // ------------------------------------------------------------------ 참조 무결성

        [Fact]
        public void Referential_integrity_errors_have_korean_text()
        {
            var child = new QualityScanSource { Headers = new[] { "a" }, RowAt = _ => new[] { "1" }, RowCount = 1 };
            var parent = new ReferentialParentSource { RowAt = _ => new[] { "1" }, RowCount = 1, Name = "parent" };
            var opt = new ReferentialIntegrityOptions { DegreeOfParallelism = 1 };
            var messages = new[]
            {
                MessageOf(() => ReferentialIntegrityScanner.Scan(child, Array.Empty<int>(), parent, new[] { 0 }, opt, null, CancellationToken.None)),
                MessageOf(() => ReferentialIntegrityScanner.Scan(child, new[] { 0 }, parent, Array.Empty<int>(), opt, null, CancellationToken.None)),
                MessageOf(() => ReferentialIntegrityScanner.Scan(child, new[] { 0 }, parent, new[] { 0, 0 }, opt, null, CancellationToken.None)),
                MessageOf(() => ReferentialIntegrityScanner.Scan(child, new[] { 0 }, parent, new[] { 0 },
                    new ReferentialIntegrityOptions { DegreeOfParallelism = 1, ParentKeyMemoryBudgetBytes = 0 }, null, CancellationToken.None)),
                new ReferentialIntegrityBudgetException().Message,
            };
            foreach (string message in messages) AssertKorean(message);
        }

        // ------------------------------------------------------------------ Localize 동작

        [Fact]
        public void Localize_follows_ui_language_and_keeps_unknown_text_and_separators()
        {
            const string note = "imported from DQD · customDescription · denominator not reported";
            const string multi = "Column 'alpha' has no checks.\nColumn 'beta' has no checks.";
            var saved = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal(note, ErrorText.LocalizeNote(note));
                Assert.Equal(multi, ErrorText.Localize(multi));

                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
                Assert.Equal("DQD에서 가져옴 · customDescription · 분모가 보고되지 않음", ErrorText.LocalizeNote(note));
                // 줄마다 따로 변환한다(탐욕 매칭으로 두 컬럼 이름이 한 문장에 섞이지 않는다).
                Assert.Equal("컬럼 'alpha'에 검사가 하나도 지정되지 않았습니다.\n컬럼 'beta'에 검사가 하나도 지정되지 않았습니다.", ErrorText.Localize(multi));
                Assert.Equal("Nobody translated this.", ErrorText.Localize("Nobody translated this."));
            }
            finally
            {
                CultureInfo.CurrentUICulture = saved;
            }
        }
    }
}
