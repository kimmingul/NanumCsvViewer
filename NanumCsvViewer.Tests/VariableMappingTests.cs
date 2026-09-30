using System.Globalization;
using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// 합성 명세. 열 이름은 공개 레이아웃을 따른다.
    /// OMOP: OHDSI CommonDataModel field-level CSV (cdmTableName, cdmFieldName, isRequired, cdmDatatype,
    /// userGuidance, etlConventions, 그리고 있으면 fkTableName, fkDomain).
    /// CDISC: SDTM/ADaM 변수 메타데이터 CSV (Dataset, Variable, Label, Type, Length, Codelist, Core).
    /// 값은 합성이다. OMOP/CDISC 본문은 포함하지 않는다.
    /// </summary>
    public class VariableMappingTests
    {
        private const string OmopSpec = """
            cdmTableName,cdmFieldName,isRequired,cdmDatatype,userGuidance,etlConventions,fkTableName,fkDomain
            PERSON,person_id,Yes,integer,"Unique identifier for the person",, ,
            PERSON,gender_concept_id,Yes,integer,"A foreign key that refers to a standard concept identifier for the gender of the person",,CONCEPT,Gender
            PERSON,year_of_birth,Yes,integer,"The year of birth of the person",,,
            PERSON,birth_datetime,No,datetime,"The date and time of birth of the person",,,
            VISIT_OCCURRENCE,visit_occurrence_id,Yes,integer,"Unique identifier for each visit",,,
            VISIT_OCCURRENCE,person_id,Yes,integer,"A foreign key identifier to the person for whom the visit is recorded",,PERSON,
            """;

        private const string CdiscSpec = """
            Dataset,Variable,Label,Type,Length,Codelist,Core
            DM,USUBJID,Unique Subject Identifier,Char,20,,Req
            DM,BRTHDTC,Date/Time of Birth,Char,19,,Perm
            DM,SEX,Sex,Char,1,SEX,Req
            DM,AGE,Age,Num,8,,Exp
            AE,AESEQ,Sequence Number,Num,8,,Req
            """;

        [Fact]
        public void Omop_layout_detects_required_type_and_foreign_key()
        {
            var spec = VariableMapping.Parse(OmopSpec, "omop.csv");
            Assert.Equal(SpecLayoutKind.OmopFieldLevel, spec.Layout);
            Assert.Equal(6, spec.Fields.Count);
            var gender = spec.Fields.Single(f => f.Name == "gender_concept_id");
            Assert.Equal("PERSON", gender.Table);
            Assert.True(gender.Required);
            Assert.Equal("integer", gender.DataType);
            Assert.Equal("CONCEPT", gender.FkTable);
            Assert.Equal("Gender", gender.FkDomain);
            Assert.Contains("gender", gender.Label, StringComparison.OrdinalIgnoreCase);
            Assert.False(spec.Fields.Single(f => f.Name == "birth_datetime").Required);
        }

        [Fact]
        public void Cdisc_layout_maps_core_and_length()
        {
            var spec = VariableMapping.Parse(CdiscSpec, "sdtm.csv");
            Assert.Equal(SpecLayoutKind.CdiscVariable, spec.Layout);
            Assert.True(spec.Fields.Single(f => f.Name == "USUBJID").Required);
            Assert.False(spec.Fields.Single(f => f.Name == "AGE").Required); // Exp is expected, not required
            Assert.False(spec.Fields.Single(f => f.Name == "BRTHDTC").Required);
            Assert.Equal(20, spec.Fields.Single(f => f.Name == "USUBJID").Length);
            Assert.Equal("SEX", spec.Fields.Single(f => f.Name == "SEX").Codelist);
            Assert.True(spec.Fields.Single(f => f.Name == "AESEQ").Required);
        }

        [Fact]
        public void Quoted_guidance_with_commas_stays_in_one_field()
        {
            const string csv = """
                cdmTableName,cdmFieldName,isRequired,cdmDatatype,userGuidance,etlConventions
                PERSON,person_id,Yes,varchar(32),"Identifier, unique, for the person","Keep it, stable"
                """;
            var spec = VariableMapping.Parse(csv);
            Assert.Single(spec.Fields);
            Assert.Equal(32, VariableMapping.ClassifyType(spec.Fields[0].DataType, out int? len) == SpecTypeKind.String ? len : null);
            Assert.Contains("unique", spec.Fields[0].Label, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stable", spec.Fields[0].Guidance, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Custom_roles_read_name_table_and_required()
        {
            const string csv = """
                col,tbl,kind,must
                widget_id,ITEM,integer,Yes
                note,ITEM,varchar(12),No
                """;
            var roles = new SpecColumnRoles(SpecLayoutKind.Custom, Name: 0, Table: 1, Type: 2, Required: 3);
            var spec = VariableMapping.Parse(csv, roles: roles);
            Assert.Equal(SpecLayoutKind.Custom, spec.Layout);
            Assert.Equal("widget_id", spec.Fields[0].Name);
            Assert.Equal("ITEM", spec.Fields[0].Table);
            Assert.True(spec.Fields[0].Required);
            Assert.False(spec.Fields[1].Required);
        }

        [Fact]
        public void Unknown_headers_without_roles_are_rejected()
        {
            var ex = Assert.Throws<MappingException>(() => VariableMapping.Parse("foo,bar\n1,2\n"));
            Assert.Contains("not recognized", ex.Message);
        }

        [Fact]
        public void Name_order_prefers_same_table_token_then_exact_field()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            var result = VariableMapping.Suggest(
            [
                new SourceColumn("person_id", ColumnValueType.Integer, Sample: ["1", "2", "3"]),
                new SourceColumn("PersonId", ColumnValueType.Identifier),
            ], spec);

            var exact = Top(result, "person_id");
            Assert.Equal("PERSON", exact.Table);
            Assert.Equal("person_id", exact.Field);
            Assert.Equal("person_id", result.Columns[0].Candidates[1].Field);
            Assert.Equal("VISIT_OCCURRENCE", result.Columns[0].Candidates[1].Table);
            Assert.True(exact.Score > result.Columns[0].Candidates[1].Score);
            Assert.Contains(exact.Reasons, r => r.Code == "table");
            Assert.Contains(exact.Reasons, r => r.Code == "name");

            Assert.Equal("person_id", Top(result, "PersonId").Field);
            Assert.Equal("PERSON", Top(result, "PersonId").Table);
        }

        [Fact]
        public void Table_filter_drops_other_tables_and_lists_required_gaps()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            var result = VariableMapping.Suggest(
                [new SourceColumn("person_id", ColumnValueType.Integer)],
                spec,
                new MappingSuggestOptions { TargetTable = "person" });

            Assert.Equal("person", result.TargetTable);
            Assert.All(result.Columns[0].Candidates, c => Assert.Equal("PERSON", c.Table));
            Assert.DoesNotContain(result.UnmatchedRequired, g => g.Table == "VISIT_OCCURRENCE");
            Assert.Contains(result.UnmatchedRequired, g => g.Field == "gender_concept_id");
            Assert.Contains(result.UnmatchedRequired, g => g.Field == "year_of_birth");
            Assert.DoesNotContain(result.UnmatchedRequired, g => g.Field == "birth_datetime");
            Assert.DoesNotContain(result.UnmatchedRequired, g => g.Field == "person_id");
            Assert.Contains(result.Accepted, a => a.Field == "person_id" && a.SourceColumn == "person_id");
        }

        [Fact]
        public void Unknown_table_filter_is_rejected_without_a_partial_result()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            var ex = Assert.Throws<MappingException>(() =>
                VariableMapping.Suggest([new SourceColumn("person_id")], spec, new MappingSuggestOptions { TargetTable = "NOT_A_TABLE" }));
            Assert.Contains("NOT_A_TABLE", ex.Message);
            Assert.Contains("PERSON", ex.Message);
        }

        [Fact]
        public void Label_and_concept_pattern_outrank_a_closer_wrong_type()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            var result = VariableMapping.Suggest(
            [
                new SourceColumn("gndr", ColumnValueType.Integer, VariableLabel: "Gender of the person", Sample: ["8507", "8532", "8507"]),
                new SourceColumn("dob", ColumnValueType.Date, VariableLabel: "Date of birth", Sample: ["1990-01-02", "1981-05-05", "2001-12-01"]),
            ], spec);

            var gender = Top(result, "gndr");
            Assert.Equal("gender_concept_id", gender.Field);
            Assert.Contains(gender.Reasons, r => r.Code == "label");
            Assert.Contains(gender.Reasons, r => r.Code == "pattern-concept");
            Assert.True(gender.Score > Candidate(result, "gndr", "year_of_birth").Score);

            var birth = Top(result, "dob");
            Assert.Equal("birth_datetime", birth.Field);
            Assert.Contains(birth.Reasons, r => r.Code is "label" or "pattern-date");
            Assert.True(birth.Score > Candidate(result, "dob", "person_id").Score);
        }

        [Fact]
        public void Cdisc_glued_names_and_labels_rank_above_other_required_fields()
        {
            var spec = VariableMapping.Parse(CdiscSpec);
            var result = VariableMapping.Suggest(
            [
                new SourceColumn("subjid", ColumnValueType.Identifier, VariableLabel: "Unique Subject Identifier"),
                new SourceColumn("birth_dt", ColumnValueType.Date, VariableLabel: "Date of birth", Sample: ["1990-01-02", "1981-05-05", "2001-12-01"]),
                new SourceColumn("sex", ColumnValueType.Categorical, Sample: ["M", "F", "M"]),
                new SourceColumn("age", ColumnValueType.Integer, Sample: ["40", "55", "12"]),
            ], spec);

            Assert.Equal("USUBJID", Top(result, "subjid").Field);
            Assert.Equal("DM", Top(result, "subjid").Table);
            Assert.Equal("BRTHDTC", Top(result, "birth_dt").Field);
            Assert.Equal("SEX", Top(result, "sex").Field);
            Assert.Equal("AGE", Top(result, "age").Field);
            Assert.True(Top(result, "subjid").Score >= VariableMapping.DefaultAcceptScore);
            Assert.Contains(result.UnmatchedRequired, g => g.Field == "AESEQ" && g.Table == "AE");
            Assert.DoesNotContain(result.UnmatchedRequired, g => g.Field == "USUBJID");
            Assert.DoesNotContain(result.UnmatchedRequired, g => g.Field == "AGE");
        }

        [Fact]
        public void Tie_break_is_table_then_field_and_repeatable()
        {
            const string csv = """
                Dataset,Variable,Label,Type,Length,Codelist,Core
                B,code,Code,Char,4,,Perm
                A,code,Code,Char,4,,Perm
                """;
            var spec = VariableMapping.Parse(csv);
            var options = new MappingSuggestOptions { AcceptScore = 50 };
            var once = VariableMapping.Suggest([new SourceColumn("code", ColumnValueType.String)], spec, options);
            var twice = VariableMapping.Suggest([new SourceColumn("code", ColumnValueType.String)], spec, options);
            Assert.Equal("A", once.Columns[0].Candidates[0].Table);
            Assert.Equal("B", once.Columns[0].Candidates[1].Table);
            Assert.Equal(once.Columns[0].Candidates[0].Score, twice.Columns[0].Candidates[0].Score);
            Assert.Equal(once.Columns[0].Candidates[1].Field, twice.Columns[0].Candidates[1].Field);
            Assert.Equal(once.Accepted[0].Table, twice.Accepted[0].Table);
        }

        [Fact]
        public void One_to_one_assignment_does_not_accept_the_same_target_twice()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            var result = VariableMapping.Suggest(
            [
                new SourceColumn("person_id", ColumnValueType.Integer),
                new SourceColumn("personid", ColumnValueType.Integer),
            ], spec, new MappingSuggestOptions { TargetTable = "PERSON" });

            Assert.Equal(1, result.Accepted.Count(a => a.Field == "person_id"));
            Assert.Equal("person_id", result.Accepted.Single(a => a.Field == "person_id").SourceColumn);
        }

        [Fact]
        public void Weak_column_stays_below_accept_and_required_field_stays_open()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            var result = VariableMapping.Suggest(
                [new SourceColumn("random_note", ColumnValueType.String, Sample: ["hello", "world", "note"])],
                spec,
                new MappingSuggestOptions { TargetTable = "PERSON" });

            Assert.Empty(result.Accepted);
            Assert.True(result.Columns[0].Candidates.Count <= 3);
            if (result.Columns[0].Candidates.Count > 0)
                Assert.True(result.Columns[0].Candidates[0].Score < VariableMapping.DefaultAcceptScore);
            Assert.Contains(result.UnmatchedRequired, g => g.Field == "person_id");
        }

        [Fact]
        public void Csv_export_and_profile_use_accepted_source_names()
        {
            var spec = VariableMapping.Parse(CdiscSpec);
            var result = VariableMapping.Suggest(
            [
                new SourceColumn("subjid", ColumnValueType.Identifier, VariableLabel: "Unique Subject Identifier"),
                new SourceColumn("sex", ColumnValueType.Categorical),
                new SourceColumn("age", ColumnValueType.Integer),
            ], spec, new MappingSuggestOptions { TargetTable = "DM" });

            string csv = VariableMapping.ExportCsv(result);
            Assert.StartsWith("source column,target table,target field,score,reasons", csv.Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Contains("subjid,DM,USUBJID,", csv);
            Assert.Contains("Suggestions, not assertions", VariableMapping.Format(result, spec));

            var profile = VariableMapping.BuildProfile(result, spec, "mapping-suggestions:DM");
            string json = ConformanceProfileJson.Serialize(profile);
            var roundTrip = ConformanceProfileJson.Deserialize(json);
            Assert.Equal(1, roundTrip.SchemaVersion);
            Assert.Equal("mapping-suggestions:DM", roundTrip.Name);

            var id = roundTrip.Columns.Single(c => c.Column == "subjid");
            Assert.True(id.Required);
            Assert.Equal("String", id.DeclaredType);
            Assert.Equal(20, id.MaxLength);
            Assert.Contains("subjid", roundTrip.RequiredColumns);

            var sex = roundTrip.Columns.Single(c => c.Column == "sex");
            Assert.True(sex.Required);
            Assert.Equal("String", sex.DeclaredType);
            Assert.Equal(1, sex.MaxLength);

            var age = roundTrip.Columns.Single(c => c.Column == "age");
            Assert.False(age.Required);
            Assert.Equal("Float", age.DeclaredType); // CDISC Num accepts integers
            Assert.Equal(8, age.MaxLength);
            Assert.DoesNotContain("age", roundTrip.RequiredColumns);
            Assert.DoesNotContain(roundTrip.Columns, c => c.Column == "USUBJID");
        }

        [Fact]
        public void Omop_profile_copies_integer_required_not_a_foreign_key_file()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            var result = VariableMapping.Suggest(
                [new SourceColumn("person_id", ColumnValueType.Integer)],
                spec,
                new MappingSuggestOptions { TargetTable = "PERSON" });
            var profile = ConformanceProfileJson.Deserialize(VariableMapping.ExportProfileJson(result, spec));
            var col = Assert.Single(profile.Columns);
            Assert.Equal("person_id", col.Column);
            Assert.True(col.Required);
            Assert.Equal("Integer", col.DeclaredType);
            Assert.Null(col.MaxLength);
            Assert.Null(col.ConceptRef);
            Assert.Equal(QualitySeverity.Warning, col.Severity);
        }

        [Fact]
        public void Spec_over_the_field_cap_throws_and_does_not_return_a_partial_spec()
        {
            var sb = new StringBuilder("cdmTableName,cdmFieldName,isRequired,cdmDatatype\n");
            for (int i = 0; i < VariableMapping.MaxSpecFields + 1; i++)
                sb.Append("T,f").Append(i.ToString(CultureInfo.InvariantCulture)).Append(",No,integer\n");
            var ex = Assert.Throws<MappingException>(() => VariableMapping.Parse(sb.ToString()));
            Assert.Contains(VariableMapping.MaxSpecFields.ToString(CultureInfo.InvariantCulture), ex.Message);
            Assert.Contains("No partial mapping", ex.Message);
        }

        [Fact]
        public void Cancelled_suggest_throws()
        {
            var spec = VariableMapping.Parse(OmopSpec);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                VariableMapping.Suggest([new SourceColumn("person_id")], spec, cancellation: cts.Token));
        }

        [Fact]
        public void Indexed_pool_still_finds_a_shared_token_match()
        {
            var sb = new StringBuilder("Dataset,Variable,Label,Type,Length,Codelist,Core\n");
            int extra = VariableMapping.FullScanFieldCap + 5;
            for (int i = 0; i < extra; i++)
                sb.Append("XX,filler").Append(i.ToString(CultureInfo.InvariantCulture)).Append(",Filler,Char,8,,Perm\n");
            sb.Append("DM,USUBJID,Unique Subject Identifier,Char,20,,Req\n");
            var spec = VariableMapping.Parse(sb.ToString());
            var result = VariableMapping.Suggest(
                [new SourceColumn("subjid", ColumnValueType.Identifier, VariableLabel: "Unique Subject Identifier")],
                spec);
            Assert.True(result.IndexedCandidatePool);
            Assert.Equal("USUBJID", Top(result, "subjid").Field);
        }

        private static MappingCandidate Top(MappingSuggestionResult result, string source)
            => result.Columns.Single(c => c.SourceColumn == source).Candidates[0];

        private static MappingCandidate Candidate(MappingSuggestionResult result, string source, string field)
            => result.Columns.Single(c => c.SourceColumn == source).Candidates.Single(c => c.Field == field);
    }
}
