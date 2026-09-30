using System.Globalization;
using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer.Stats
{
    /// <summary>
    /// 열린 파일의 컬럼을 사용자가 준 대상 데이터 모형 명세(OHDSI OMOP 필드 레벨 CSV, CDISC SDTM/ADaM
    /// 변수 메타데이터 CSV, 또는 컬럼을 직접 지정한 CSV)에 맞춰 추천한다.
    /// 명세 본문(OMOP/CDISC 내용)은 포함하지 않는다. 점수는 설명 가능한 휴리스틱이며 단정이 아니다.
    /// </summary>
    public static class VariableMapping
    {
        public const int MaxSpecFields = 50_000;
        public const int FullScanFieldCap = 2_500;
        public const int DefaultTopK = 3;
        public const double DefaultAcceptScore = 55;
        public const int DefaultSampleRowCap = 256;
        public const int MaxSampleValuesPerColumn = 48;

        /// <summary>결과·CSV에 붙이는 영어 고지. UI는 같은 뜻을 번역해 보여 준다.</summary>
        public const string SuggestionDisclaimer =
            "Suggestions, not assertions. Scores are explainable heuristics (name, label, type, value pattern, table context), not a claim that a column is the target field.";

        public static bool TryDetectLayout(IReadOnlyList<string> headers, out SpecColumnRoles roles)
        {
            ArgumentNullException.ThrowIfNull(headers);
            var index = HeaderIndex(headers);

            if (Has(index, "cdmTableName") && Has(index, "cdmFieldName"))
            {
                roles = new SpecColumnRoles(
                    Layout: SpecLayoutKind.OmopFieldLevel,
                    Name: index["cdmFieldName"],
                    Table: Optional(index, "cdmTableName"),
                    Label: Optional(index, "userGuidance"),
                    Type: Optional(index, "cdmDatatype"),
                    Required: Optional(index, "isRequired"),
                    Guidance: Optional(index, "etlConventions"),
                    FkTable: Optional(index, "fkTableName"),
                    FkDomain: Optional(index, "fkDomain"));
                return true;
            }

            int? dataset = Optional(index, "Dataset") ?? Optional(index, "Domain");
            int? variable = Optional(index, "Variable")
                ?? Optional(index, "Variable Name")
                ?? Optional(index, "VariableName");
            if (dataset is not null && variable is not null)
            {
                roles = new SpecColumnRoles(
                    Layout: SpecLayoutKind.CdiscVariable,
                    Name: variable.Value,
                    Table: dataset,
                    Label: Optional(index, "Label") ?? Optional(index, "Variable Label") ?? Optional(index, "VariableLabel"),
                    Type: Optional(index, "Type") ?? Optional(index, "Data Type"),
                    Required: Optional(index, "Core"),
                    Length: Optional(index, "Length"),
                    Codelist: Optional(index, "Codelist") ?? Optional(index, "Code List"));
                return true;
            }

            roles = default;
            return false;
        }

        public static string[] ReadHeader(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            using var reader = OpenSpec(path);
            var row = ReadRow(reader) ?? throw new MappingException($"Specification '{path}' is empty.");
            if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace))
                throw new MappingException($"Specification '{path}' has an empty header.");
            return row.ToArray();
        }

        public static TargetSpec Load(string path, SpecColumnRoles? roles = null, CancellationToken cancellation = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            using var reader = OpenSpec(path);
            return Parse(reader, Path.GetFileName(path), roles, cancellation);
        }

        public static TargetSpec Parse(string csv, string? sourceName = null, SpecColumnRoles? roles = null, CancellationToken cancellation = default)
        {
            ArgumentNullException.ThrowIfNull(csv);
            using var reader = new StringReader(csv);
            return Parse(reader, sourceName, roles, cancellation);
        }

        public static MappingSuggestionResult Suggest(
            IReadOnlyList<SourceColumn> sources,
            TargetSpec spec,
            MappingSuggestOptions? options = null,
            CancellationToken cancellation = default)
        {
            ArgumentNullException.ThrowIfNull(sources);
            ArgumentNullException.ThrowIfNull(spec);
            options ??= new MappingSuggestOptions();
            if (options.TopK < 1)
                throw new MappingException("TopK must be at least 1.");
            if (options.AcceptScore < 0 || options.AcceptScore > 100)
                throw new MappingException("Accept score must be between 0 and 100.");
            cancellation.ThrowIfCancellationRequested();

            string? tableFilter = string.IsNullOrWhiteSpace(options.TargetTable) ? null : options.TargetTable.Trim();
            var considered = new List<int>(spec.Fields.Count);
            if (tableFilter is null)
            {
                for (int i = 0; i < spec.Fields.Count; i++) considered.Add(i);
            }
            else
            {
                for (int i = 0; i < spec.Fields.Count; i++)
                    if (string.Equals(spec.Fields[i].Table, tableFilter, StringComparison.OrdinalIgnoreCase))
                        considered.Add(i);
                if (considered.Count == 0)
                {
                    string known = string.Join(", ", spec.Fields.Select(f => f.Table).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(12));
                    throw new MappingException($"Target table '{tableFilter}' is not in the specification. Known tables: {known}.");
                }
            }

            var prepared = new PreparedTarget[spec.Fields.Count];
            var tokenIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < spec.Fields.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                prepared[i] = PrepareTarget(spec.Fields[i], i);
                if (considered.Count > FullScanFieldCap)
                {
                    foreach (string token in prepared[i].NameTokens)
                        AddIndex(tokenIndex, token, i);
                    foreach (string token in prepared[i].LabelTokens)
                        AddIndex(tokenIndex, token, i);
                }
            }

            bool indexed = considered.Count > FullScanFieldCap;
            var columns = new ColumnSuggestions[sources.Count];
            var pool = new List<int>(indexed ? 128 : considered.Count);
            var scored = new List<MappingCandidate>(indexed ? 64 : considered.Count);
            for (int s = 0; s < sources.Count; s++)
            {
                if ((s & 15) == 0) cancellation.ThrowIfCancellationRequested();
                var source = sources[s] ?? new SourceColumn("");
                var prep = PrepareSource(source);
                pool.Clear();
                if (!indexed)
                {
                    pool.AddRange(considered);
                }
                else
                {
                    CollectIndexed(tokenIndex, prep, considered, pool);
                }

                scored.Clear();
                foreach (int ti in pool)
                {
                    var candidate = Score(prep, prepared[ti]);
                    if (candidate.Score > 0 || candidate.Reasons.Count > 0)
                        scored.Add(candidate);
                }
                scored.Sort(CompareCandidates);
                int take = Math.Min(options.TopK, scored.Count);
                var top = new MappingCandidate[take];
                for (int i = 0; i < take; i++) top[i] = scored[i];
                columns[s] = new ColumnSuggestions(s, DisplayName(source, s), top);
            }

            var accepted = Assign(columns, options.AcceptScore);
            var gaps = UnmatchedRequired(spec, considered, accepted);
            return new MappingSuggestionResult(
                columns,
                accepted,
                gaps,
                tableFilter,
                options.SampleRowsRead,
                options.SampleRowCap,
                options.SampleCapped,
                considered.Count,
                indexed);
        }

        public static string ExportCsv(MappingSuggestionResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            var sb = new StringBuilder();
            sb.AppendLine("source column,target table,target field,score,reasons");
            foreach (var col in result.Columns)
            {
                foreach (var c in col.Candidates)
                {
                    sb.Append(CsvEscape(col.SourceColumn)).Append(',');
                    sb.Append(CsvEscape(c.Table)).Append(',');
                    sb.Append(CsvEscape(c.Field)).Append(',');
                    sb.Append(c.Score.ToString("0.00", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(CsvEscape(string.Join("; ", c.Reasons.Select(r => r.Text))));
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 수용 점수 이상의 일대일 배정만으로 적합성 프로파일 골격을 만든다.
        /// 컬럼명은 대상 필드가 아니라 열린 파일의 소스 컬럼(헤더)이다. 제약은 명세의 required/type/length만 복사한다.
        /// </summary>
        public static ConformanceProfile BuildProfile(MappingSuggestionResult result, TargetSpec spec, string? name = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(spec);
            var columns = new List<ConformanceColumnSpec>();
            var required = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var map in result.Accepted.OrderBy(a => a.SourceIndex).ThenBy(a => a.TargetIndex))
            {
                if (!seen.Add(map.SourceColumn)) continue;
                if ((uint)map.TargetIndex >= (uint)spec.Fields.Count) continue;
                var field = spec.Fields[map.TargetIndex];
                var kind = ClassifyType(field.DataType, out int? fromType);
                int? length = field.Length ?? fromType;
                string? declared = DeclaredTypeName(kind);
                bool req = field.Required;
                if (!req && declared is null && length is null) continue;
                columns.Add(new ConformanceColumnSpec
                {
                    Column = map.SourceColumn,
                    Required = req,
                    DeclaredType = declared,
                    MaxLength = length,
                    Severity = QualitySeverity.Warning,
                });
                if (req) required.Add(map.SourceColumn);
            }

            string profileName = string.IsNullOrWhiteSpace(name)
                ? "mapping-suggestions" + (result.TargetTable is { Length: > 0 } t ? ":" + t : "")
                : name.Trim();
            return new ConformanceProfile
            {
                SchemaVersion = ConformanceProfileJson.SupportedSchemaVersion,
                Name = profileName,
                CaseInsensitiveColumnMatch = true,
                RequiredColumns = required,
                Columns = columns,
            };
        }

        public static string ExportProfileJson(MappingSuggestionResult result, TargetSpec spec, string? name = null)
            => ConformanceProfileJson.Serialize(BuildProfile(result, spec, name));

        public static string Format(MappingSuggestionResult result, TargetSpec spec, MappingSuggestOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(spec);
            options ??= new MappingSuggestOptions();
            var sb = new StringBuilder();
            sb.AppendLine(SuggestionDisclaimer);
            sb.AppendLine();
            sb.Append("Spec: ").Append(spec.SourceName ?? "(text)");
            sb.Append(" · layout ").Append(LayoutLabel(spec.Layout));
            sb.Append(" · ").Append(spec.Fields.Count.ToString(CultureInfo.InvariantCulture)).Append(" fields");
            if (result.TargetTable is { Length: > 0 } table)
                sb.Append(" · table filter ").Append(table);
            else
                sb.Append(" · all tables");
            sb.Append(" · considered ").Append(result.FieldsConsidered.ToString(CultureInfo.InvariantCulture));
            if (result.IndexedCandidatePool)
                sb.Append(" · candidate pool limited to shared tokens (spec above ").Append(FullScanFieldCap.ToString(CultureInfo.InvariantCulture)).Append(" fields; name-only matches may be missed)");
            sb.AppendLine();
            sb.Append("Source columns: ").Append(result.Columns.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(" · value-pattern sample: first ");
            sb.Append(result.SampleRowsRead.ToString(CultureInfo.InvariantCulture));
            sb.Append(" rows of the current view");
            if (result.SampleCapped)
                sb.Append(" (cap ").Append(result.SampleRowCap.ToString(CultureInfo.InvariantCulture)).Append(')');
            sb.Append(" · up to ").Append(MaxSampleValuesPerColumn.ToString(CultureInfo.InvariantCulture)).AppendLine(" non-empty values per column.");
            sb.Append("Accept score for the conformance skeleton: ");
            sb.AppendLine(options.AcceptScore.ToString("0.##", CultureInfo.InvariantCulture));
            sb.AppendLine();

            var suggestions = new TextTable("Source", "Rank", "Target table", "Target field", "Score", "Reasons");
            foreach (var col in result.Columns)
            {
                if (col.Candidates.Count == 0)
                {
                    suggestions.AddRow(col.SourceColumn, "—", "—", "—", "—", "no candidate");
                    continue;
                }
                for (int i = 0; i < col.Candidates.Count; i++)
                {
                    var c = col.Candidates[i];
                    suggestions.AddRow(
                        col.SourceColumn,
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        c.Table,
                        c.Field,
                        c.Score.ToString("0.00", CultureInfo.InvariantCulture),
                        JoinReasons(c.Reasons));
                }
            }
            sb.AppendLine(suggestions.Render());
            sb.AppendLine();

            var gaps = new TextTable("Target table", "Target field", "Type", "Status");
            if (result.UnmatchedRequired.Count == 0)
                gaps.AddRow("—", "—", "—", "no unmatched required field in scope");
            else
                foreach (var g in result.UnmatchedRequired)
                    gaps.AddRow(g.Table, g.Field, g.DataType ?? "—", "required, no accepted suggestion");
            sb.AppendLine("Unmatched required target fields (accepted suggestions only; not assertions):");
            sb.AppendLine(gaps.Render());
            return sb.ToString();
        }

        private static TargetSpec Parse(TextReader reader, string? sourceName, SpecColumnRoles? roles, CancellationToken cancellation)
        {
            var header = ReadRow(reader) ?? throw new MappingException("Specification is empty.");
            cancellation.ThrowIfCancellationRequested();
            if (header.Count == 0 || header.All(string.IsNullOrWhiteSpace))
                throw new MappingException("Specification has an empty header.");

            SpecColumnRoles used;
            if (roles is { } given)
            {
                if (given.Name < 0 || given.Name >= header.Count)
                    throw new MappingException("Field-name column index is outside the specification header.");
                used = given with { Layout = given.Layout == SpecLayoutKind.Unknown ? SpecLayoutKind.Custom : given.Layout };
            }
            else if (!TryDetectLayout(header, out used))
            {
                throw new MappingException(
                    "Specification headers were not recognized as an OHDSI OMOP field-level CSV (cdmTableName, cdmFieldName) or a CDISC variable-metadata CSV (Dataset, Variable). Pick the name, label, type, and table columns.");
            }

            var fields = new List<TargetField>();
            while (ReadRow(reader) is { } row)
            {
                cancellation.ThrowIfCancellationRequested();
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                if (fields.Count >= MaxSpecFields)
                    throw new MappingException(
                        $"Specification has more than {MaxSpecFields.ToString(CultureInfo.InvariantCulture)} fields. No partial mapping was produced.");

                string name = Cell(row, used.Name).Trim();
                if (name.Length == 0) continue;
                string table = used.Table is int t ? Cell(row, t).Trim() : "";
                string label = used.Label is int l ? Cell(row, l).Trim() : "";
                string type = used.Type is int ty ? Cell(row, ty).Trim() : "";
                string requiredRaw = used.Required is int r ? Cell(row, r).Trim() : "";
                string lengthRaw = used.Length is int len ? Cell(row, len).Trim() : "";
                string codelist = used.Codelist is int cl ? Cell(row, cl).Trim() : "";
                string guidance = used.Guidance is int g ? Cell(row, g).Trim() : "";
                string fkTable = used.FkTable is int fk ? Cell(row, fk).Trim() : "";
                string fkDomain = used.FkDomain is int fd ? Cell(row, fd).Trim() : "";
                fields.Add(new TargetField(
                    table,
                    name,
                    NullIfEmpty(label),
                    NullIfEmpty(type),
                    IsRequired(requiredRaw),
                    ParseLength(lengthRaw),
                    NullIfEmpty(codelist),
                    NullIfEmpty(guidance),
                    NullIfEmpty(fkTable),
                    NullIfEmpty(fkDomain)));
            }

            if (fields.Count == 0)
                throw new MappingException("Specification has no fields.");
            return new TargetSpec(used.Layout, sourceName, header.ToArray(), fields);
        }

        private static PreparedSource PrepareSource(SourceColumn source)
        {
            var nameTexts = DistinctTexts(source.VariableName, source.Name);
            var labelTexts = DistinctTexts(source.VariableLabel, source.Name, source.VariableName);
            var nameTokens = new HashSet<string>(StringComparer.Ordinal);
            var labelTokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (string text in nameTexts)
                foreach (string token in Tokenize(text, expand: true))
                    nameTokens.Add(token);
            foreach (string text in labelTexts)
                foreach (string token in Tokenize(text, expand: true))
                    if (!Stopwords.Contains(token))
                        labelTokens.Add(token);

            string joined = string.Join(' ', nameTokens.Order(StringComparer.Ordinal));
            string compact = Compact(source.VariableName ?? source.Name);
            int nonEmpty = 0, dates = 0, integers = 0;
            if (source.Sample is not null)
            {
                foreach (string raw in source.Sample)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string v = raw.Trim();
                    if (ColumnStatisticsBuilder.IsNullToken(v)) continue;
                    nonEmpty++;
                    if (CsvDateParser.ParseDetailed(v, true) is { } temporal && temporal.Kind != TemporalKind.Time)
                        dates++;
                    if (IsIntegerToken(v)) integers++;
                }
            }
            return new PreparedSource(source, nameTexts, labelTexts, nameTokens, labelTokens, joined, compact, nonEmpty, dates, integers);
        }

        private static PreparedTarget PrepareTarget(TargetField field, int index)
        {
            var nameTokens = Tokenize(field.Name, expand: true);
            var labelTokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in Tokenize(field.Label, expand: true))
                if (!Stopwords.Contains(token)) labelTokens.Add(token);
            var guidanceTokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in Tokenize(field.Guidance, expand: true))
                if (!Stopwords.Contains(token)) guidanceTokens.Add(token);
            var tableTokens = Tokenize(field.Table, expand: true);
            var kind = ClassifyType(field.DataType, out _);
            bool concept = nameTokens.Contains("concept") && nameTokens.Contains("identifier")
                || Compact(field.Name).Contains("conceptid", StringComparison.Ordinal);
            bool idField = nameTokens.Contains("identifier") || concept;
            bool dateHint = kind is SpecTypeKind.Date or SpecTypeKind.DateTime
                || nameTokens.Contains("date") || labelTokens.Contains("date") || guidanceTokens.Contains("date");
            return new PreparedTarget(
                field, index, nameTokens, labelTokens, guidanceTokens, tableTokens,
                string.Join(' ', nameTokens.Order(StringComparer.Ordinal)),
                Compact(field.Name),
                string.IsNullOrWhiteSpace(field.Label) ? "" : field.Label.Trim().ToLowerInvariant(),
                kind, concept, idField, dateHint);
        }

        private static MappingCandidate Score(PreparedSource source, PreparedTarget target)
        {
            double jw = 0, jaccard = 0;
            foreach (string text in source.NameTexts)
            {
                string joined = string.Join(' ', Tokenize(text, expand: true).Order(StringComparer.Ordinal));
                jw = Math.Max(jw, JaroWinkler(joined, target.NameJoined));
                jw = Math.Max(jw, JaroWinkler(Compact(text), target.Compact));
            }
            jaccard = Jaccard(source.NameTokens, target.NameTokens);
            double nameSim = 0.65 * jw + 0.35 * jaccard;
            double namePoints = nameSim * 48;

            double labelSim = LabelSimilarity(source.LabelTokens, source.LabelTexts, target.LabelTokens, target.LabelJoined);
            double guidanceSim = target.GuidanceTokens.Count == 0
                ? 0
                : 0.75 * Overlap(source.LabelTokens, target.GuidanceTokens);
            double labelPoints = Math.Max(labelSim, guidanceSim) * 22;

            int typePoints = TypePoints(source.Column.InferredType, target.Kind, out string? typeReason, out string typeCode);

            int pattern = 0;
            var reasons = new List<MappingReason>(6);
            if (namePoints >= 8)
                reasons.Add(new MappingReason("name", $"name similarity {nameSim.ToString("0.00", CultureInfo.InvariantCulture)} (Jaro-Winkler {jw.ToString("0.00", CultureInfo.InvariantCulture)}, token overlap {jaccard.ToString("0.00", CultureInfo.InvariantCulture)})"));
            if (labelPoints >= 6)
                reasons.Add(new MappingReason("label", $"label similarity {Math.Max(labelSim, guidanceSim).ToString("0.00", CultureInfo.InvariantCulture)}"));
            if (typeReason is not null)
                reasons.Add(new MappingReason(typeCode, typeReason));

            if (target.Concept
                && (source.Column.InferredType is ColumnValueType.Integer or ColumnValueType.Float or ColumnValueType.Identifier
                    || source.Column.InferredType.IsNumeric()
                    || (source.NonEmpty >= 3 && source.Integers >= source.NonEmpty * 0.7)))
            {
                pattern += 10;
                reasons.Add(new MappingReason("pattern-concept", "value pattern: numeric identifier for *_concept_id"));
            }
            else if (target.IdField && !target.Concept
                && (source.Column.InferredType == ColumnValueType.Identifier || source.NameTokens.Contains("identifier")))
            {
                pattern += 6;
                reasons.Add(new MappingReason("pattern-id", "value pattern: identifier"));
            }

            bool sampleDates = source.NonEmpty >= 3 && source.Dates >= source.NonEmpty * 0.7;
            bool inferredDate = source.Column.InferredType is ColumnValueType.Date or ColumnValueType.DateTime;
            if (target.DateHint && (inferredDate || sampleDates))
            {
                pattern += 8;
                reasons.Add(new MappingReason("pattern-date", "value pattern: dates"));
            }
            if (pattern > 15) pattern = 15;

            int tablePoints = 0;
            if (source.NameTokens.Overlaps(target.TableTokens) || source.LabelTokens.Overlaps(target.TableTokens))
            {
                tablePoints = 8;
                reasons.Add(new MappingReason("table", "table context: source tokens overlap " + target.Field.Table));
            }
            if ((target.Field.FkTable ?? target.Field.FkDomain) is not null && (namePoints >= 8 || labelPoints >= 6))
            {
                string fk = target.Field.FkTable ?? "";
                if (target.Field.FkDomain is { Length: > 0 } domain)
                    fk = fk.Length == 0 ? domain : fk + " / " + domain;
                reasons.Add(new MappingReason("fk", "foreign key hint: " + fk));
            }

            double score = namePoints + labelPoints + typePoints + pattern + tablePoints;
            if (score < 0) score = 0;
            if (score > 100) score = 100;
            return new MappingCandidate(target.Index, target.Field.Table, target.Field.Name, score, reasons);
        }

        private static double LabelSimilarity(HashSet<string> sourceTokens, IReadOnlyList<string> sourceTexts, HashSet<string> targetTokens, string targetJoined)
        {
            if (targetTokens.Count == 0 && targetJoined.Length == 0) return 0;
            double jaccard = Jaccard(sourceTokens, targetTokens);
            double overlap = Overlap(sourceTokens, targetTokens);
            double sim = Math.Max(jaccard, overlap * 0.85);
            if (targetJoined.Length is > 0 and <= 80)
            {
                foreach (string text in sourceTexts)
                {
                    string joined = string.Join(' ', Tokenize(text, expand: true).Where(t => !Stopwords.Contains(t)).Order(StringComparer.Ordinal));
                    sim = Math.Max(sim, JaroWinkler(joined, string.Join(' ', targetTokens.Order(StringComparer.Ordinal))));
                }
            }
            return sim;
        }

        private static int TypePoints(ColumnValueType inferred, SpecTypeKind spec, out string? reason, out string code)
        {
            reason = null;
            code = "type";
            if (spec == SpecTypeKind.Unknown || inferred == ColumnValueType.Empty) return 0;
            int level = Compatibility(inferred, spec);
            string left = inferred.DisplayName();
            string right = spec.ToString();
            if (level >= 2)
            {
                reason = $"type compatible ({left} ~ {right})";
                code = "type-ok";
                return 15;
            }
            if (level == 1)
            {
                reason = $"type partial ({left} ~ {right})";
                code = "type-partial";
                return 7;
            }
            reason = $"type mismatch ({left} vs {right})";
            code = "type-mismatch";
            return -10;
        }

        // 2 = compatible, 1 = partial, 0 = mismatch.
        private static int Compatibility(ColumnValueType inferred, SpecTypeKind spec) => (inferred, spec) switch
        {
            (ColumnValueType.Integer, SpecTypeKind.Integer) => 2,
            (ColumnValueType.Integer, SpecTypeKind.Float or SpecTypeKind.Numeric) => 1,
            (ColumnValueType.Float, SpecTypeKind.Float or SpecTypeKind.Numeric) => 2,
            (ColumnValueType.Float, SpecTypeKind.Integer) => 1,
            (ColumnValueType.Currency or ColumnValueType.Percent or ColumnValueType.Scientific, SpecTypeKind.Float or SpecTypeKind.Numeric or SpecTypeKind.Integer) => 1,
            (ColumnValueType.Date, SpecTypeKind.Date) => 2,
            (ColumnValueType.Date, SpecTypeKind.DateTime or SpecTypeKind.String) => 1,
            (ColumnValueType.DateTime, SpecTypeKind.DateTime) => 2,
            (ColumnValueType.DateTime, SpecTypeKind.Date or SpecTypeKind.String) => 1,
            (ColumnValueType.Time, SpecTypeKind.Time) => 2,
            (ColumnValueType.Time, SpecTypeKind.String) => 1,
            (ColumnValueType.Boolean, SpecTypeKind.Boolean) => 2,
            (ColumnValueType.Boolean, SpecTypeKind.Integer or SpecTypeKind.String) => 1,
            (ColumnValueType.Identifier, SpecTypeKind.Integer or SpecTypeKind.String or SpecTypeKind.Numeric or SpecTypeKind.Float) => 1,
            (ColumnValueType.Categorical or ColumnValueType.Ordinal, SpecTypeKind.String) => 1,
            (ColumnValueType.String, SpecTypeKind.String) => 2,
            (ColumnValueType.String, SpecTypeKind.Date or SpecTypeKind.DateTime) => 1,
            _ => 0,
        };

        private static List<AcceptedMapping> Assign(IReadOnlyList<ColumnSuggestions> columns, double acceptScore)
        {
            var pairs = new List<(double Score, int Source, int Target, MappingCandidate Candidate)>();
            for (int s = 0; s < columns.Count; s++)
            {
                foreach (var c in columns[s].Candidates)
                    if (c.Score >= acceptScore)
                        pairs.Add((c.Score, s, c.TargetIndex, c));
            }
            pairs.Sort(static (a, b) =>
            {
                int cmp = b.Score.CompareTo(a.Score);
                if (cmp != 0) return cmp;
                cmp = a.Source.CompareTo(b.Source);
                if (cmp != 0) return cmp;
                return a.Target.CompareTo(b.Target);
            });
            var usedSource = new HashSet<int>();
            var usedTarget = new HashSet<int>();
            var accepted = new List<AcceptedMapping>();
            foreach (var pair in pairs)
            {
                if (!usedSource.Add(pair.Source) || !usedTarget.Add(pair.Target)) continue;
                accepted.Add(new AcceptedMapping(pair.Source, columns[pair.Source].SourceColumn, pair.Target, pair.Candidate.Table, pair.Candidate.Field, pair.Score));
            }
            accepted.Sort(static (a, b) => a.SourceIndex.CompareTo(b.SourceIndex));
            return accepted;
        }

        private static List<RequiredGap> UnmatchedRequired(TargetSpec spec, List<int> considered, List<AcceptedMapping> accepted)
        {
            var used = new HashSet<int>(accepted.Select(a => a.TargetIndex));
            var gaps = new List<RequiredGap>();
            foreach (int i in considered)
            {
                var field = spec.Fields[i];
                if (!field.Required || used.Contains(i)) continue;
                gaps.Add(new RequiredGap(field.Table, field.Name, field.DataType));
            }
            gaps.Sort(static (a, b) =>
            {
                int cmp = string.Compare(a.Table, b.Table, StringComparison.OrdinalIgnoreCase);
                return cmp != 0 ? cmp : string.Compare(a.Field, b.Field, StringComparison.OrdinalIgnoreCase);
            });
            return gaps;
        }

        private static void CollectIndexed(Dictionary<string, List<int>> index, PreparedSource source, List<int> considered, List<int> pool)
        {
            var allowed = new HashSet<int>(considered);
            var seen = new HashSet<int>();
            void Take(HashSet<string> tokens)
            {
                foreach (string token in tokens)
                {
                    if (!index.TryGetValue(token, out var hits)) continue;
                    foreach (int i in hits)
                        if (allowed.Contains(i) && seen.Add(i))
                            pool.Add(i);
                }
            }
            Take(source.NameTokens);
            Take(source.LabelTokens);
        }

        private static int CompareCandidates(MappingCandidate a, MappingCandidate b)
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Table, b.Table, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            cmp = string.Compare(a.Field, b.Field, StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;
            return a.TargetIndex.CompareTo(b.TargetIndex);
        }

        internal static SpecTypeKind ClassifyType(string? raw, out int? length)
        {
            length = null;
            if (string.IsNullOrWhiteSpace(raw)) return SpecTypeKind.Unknown;
            string text = raw.Trim().ToLowerInvariant();
            int paren = text.IndexOf('(');
            if (paren >= 0)
            {
                int close = text.IndexOf(')', paren + 1);
                if (close > paren && int.TryParse(text.AsSpan(paren + 1, close - paren - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 0)
                    length = n;
                text = text[..paren].Trim();
            }
            text = text.Replace("varying", "", StringComparison.Ordinal).Trim();
            if (text is "datetime" or "timestamp" or "datetime2") return SpecTypeKind.DateTime;
            if (text is "date") return SpecTypeKind.Date;
            if (text is "time") return SpecTypeKind.Time;
            if (text is "bool" or "boolean") return SpecTypeKind.Boolean;
            if (text is "integer" or "int" or "bigint" or "smallint" or "tinyint") return SpecTypeKind.Integer;
            if (text is "float" or "double" or "decimal" or "numeric" or "real" or "number") return SpecTypeKind.Float;
            if (text is "num") return SpecTypeKind.Numeric;
            if (text is "varchar" or "nvarchar" or "char" or "nchar" or "character" or "text" or "string" or "clob")
                return SpecTypeKind.String;
            if (text.Contains("char", StringComparison.Ordinal) || text.Contains("text", StringComparison.Ordinal))
                return SpecTypeKind.String;
            return SpecTypeKind.Unknown;
        }

        private static string? DeclaredTypeName(SpecTypeKind kind) => kind switch
        {
            SpecTypeKind.Integer => nameof(ColumnValueType.Integer),
            SpecTypeKind.Float or SpecTypeKind.Numeric => nameof(ColumnValueType.Float),
            SpecTypeKind.String => nameof(ColumnValueType.String),
            SpecTypeKind.Date => nameof(ColumnValueType.Date),
            SpecTypeKind.DateTime => nameof(ColumnValueType.DateTime),
            SpecTypeKind.Time => nameof(ColumnValueType.Time),
            SpecTypeKind.Boolean => nameof(ColumnValueType.Boolean),
            _ => null,
        };

        internal static bool IsRequired(string raw)
        {
            if (raw.Length == 0) return false;
            if (raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("yes", StringComparison.OrdinalIgnoreCase) && (raw.Length == 3 || !char.IsLetter(raw[3])))
                return true;
            return raw.Equals("y", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("1", StringComparison.Ordinal)
                || raw.Equals("req", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("required", StringComparison.OrdinalIgnoreCase);
        }

        private static int? ParseLength(string raw)
            => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 0 ? n : null;

        internal static HashSet<string> Tokenize(string? text, bool expand)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(text)) return tokens;
            var parts = SplitParts(text);
            foreach (string part in parts)
            {
                if (part.Length == 0) continue;
                tokens.Add(part);
                if (!expand) continue;
                if (WholeWord.TryGetValue(part, out var whole))
                {
                    foreach (string w in whole) tokens.Add(w);
                }
                if (TrySegment(part, out var segmented))
                    foreach (string w in segmented) tokens.Add(w);
            }
            return tokens;
        }

        private static List<string> SplitParts(string text)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            char prev = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                bool split = !char.IsLetterOrDigit(c)
                    || (char.IsDigit(c) && char.IsLetter(prev))
                    || (char.IsLetter(c) && char.IsDigit(prev))
                    || (char.IsUpper(c) && char.IsLower(prev))
                    || (char.IsUpper(c) && char.IsUpper(prev) && char.IsLower(next));
                if (split)
                {
                    AddPart(parts, sb);
                    if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                }
                else sb.Append(char.ToLowerInvariant(c));
                prev = c;
            }
            AddPart(parts, sb);
            return parts;
        }

        private static void AddPart(List<string> parts, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            parts.Add(sb.ToString());
            sb.Clear();
        }

        private static bool TrySegment(string token, out List<string> parts)
        {
            parts = new List<string>();
            if (token.Length < 5) return false;
            int i = 0;
            bool root = false;
            while (i < token.Length)
            {
                string? hit = null;
                string[]? expansion = null;
                for (int k = 0; k < SegmentKeys.Length; k++)
                {
                    string key = SegmentKeys[k];
                    if (i + key.Length > token.Length) continue;
                    if (!token.AsSpan(i, key.Length).SequenceEqual(key)) continue;
                    hit = key;
                    expansion = SegmentExpansions[k];
                    break;
                }
                if (hit is null) return false;
                if (Roots.Contains(hit)) root = true;
                foreach (string e in expansion!) parts.Add(e);
                i += hit.Length;
            }
            if (parts.Count < 2 || !root) return false;
            return true;
        }

        private static double JaroWinkler(string s, string t)
        {
            if (s.Length == 0 && t.Length == 0) return 1;
            if (s.Length == 0 || t.Length == 0) return 0;
            double j = Jaro(s, t);
            int prefix = 0;
            int max = Math.Min(4, Math.Min(s.Length, t.Length));
            while (prefix < max && s[prefix] == t[prefix]) prefix++;
            return j + prefix * 0.1 * (1 - j);
        }

        private static double Jaro(string s, string t)
        {
            int matchDistance = Math.Max(0, Math.Max(s.Length, t.Length) / 2 - 1);
            var sMatches = new bool[s.Length];
            var tMatches = new bool[t.Length];
            int matches = 0;
            for (int i = 0; i < s.Length; i++)
            {
                int start = Math.Max(0, i - matchDistance);
                int end = Math.Min(i + matchDistance + 1, t.Length);
                for (int j = start; j < end; j++)
                {
                    if (tMatches[j] || s[i] != t[j]) continue;
                    sMatches[i] = true;
                    tMatches[j] = true;
                    matches++;
                    break;
                }
            }
            if (matches == 0) return 0;
            int k = 0;
            int transpositions = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (!sMatches[i]) continue;
                while (!tMatches[k]) k++;
                if (s[i] != t[k]) transpositions++;
                k++;
            }
            double m = matches;
            return (m / s.Length + m / t.Length + (m - transpositions / 2.0) / m) / 3.0;
        }

        private static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int inter = a.Count(b.Contains);
            int union = a.Count + b.Count - inter;
            return union == 0 ? 0 : (double)inter / union;
        }

        private static double Overlap(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int inter = a.Count(b.Contains);
            return (double)inter / Math.Min(a.Count, b.Count);
        }

        private static string Compact(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static bool IsIntegerToken(string v)
            => double.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out double d)
               && double.IsFinite(d) && Math.Truncate(d) == d && !v.Contains('.') && v.IndexOf('e', StringComparison.OrdinalIgnoreCase) < 0;

        private static string DisplayName(SourceColumn source, int index)
            => string.IsNullOrWhiteSpace(source.Name) ? "Column" + (index + 1).ToString(CultureInfo.InvariantCulture) : source.Name;

        private static string JoinReasons(IReadOnlyList<MappingReason> reasons)
        {
            string text = string.Join("; ", reasons.Select(r => r.Text));
            return text.Length <= 160 ? text : text[..157] + "...";
        }

        private static string LayoutLabel(SpecLayoutKind kind) => kind switch
        {
            SpecLayoutKind.OmopFieldLevel => "OMOP field-level",
            SpecLayoutKind.CdiscVariable => "CDISC variable metadata",
            SpecLayoutKind.Custom => "custom columns",
            _ => "unknown",
        };

        private static List<string> DistinctTexts(params string?[] texts)
        {
            var list = new List<string>(texts.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? text in texts)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (seen.Add(text.Trim())) list.Add(text.Trim());
            }
            return list;
        }

        private static Dictionary<string, int> HeaderIndex(IReadOnlyList<string> headers)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++)
            {
                string key = headers[i].Trim().TrimStart('\uFEFF');
                if (key.Length == 0 || index.ContainsKey(key)) continue;
                index.Add(key, i);
            }
            return index;
        }

        private static bool Has(Dictionary<string, int> index, string name) => index.ContainsKey(name);

        private static int? Optional(Dictionary<string, int> index, string name)
            => index.TryGetValue(name, out int i) ? i : null;

        private static string Cell(IReadOnlyList<string> row, int index)
            => index >= 0 && index < row.Count ? row[index] : "";

        private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

        private static void AddIndex(Dictionary<string, List<int>> index, string token, int field)
        {
            if (Stopwords.Contains(token) || token.Length < 2) return;
            if (!index.TryGetValue(token, out var list))
            {
                list = new List<int>();
                index.Add(token, list);
            }
            list.Add(field);
        }

        private static StreamReader OpenSpec(string path)
        {
            try
            {
                return new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new MappingException($"Could not read specification '{path}': {ex.Message}");
            }
        }

        private static List<string>? ReadRow(TextReader reader)
        {
            var row = new List<string>();
            var sb = new StringBuilder();
            bool any = false;
            bool inQuotes = false;
            while (true)
            {
                int n = reader.Read();
                if (n < 0)
                {
                    if (!any) return null;
                    row.Add(sb.ToString());
                    return row;
                }
                any = true;
                char c = (char)n;
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        int peek = reader.Peek();
                        if (peek == '"')
                        {
                            reader.Read();
                            sb.Append('"');
                        }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                    continue;
                }
                if (c == '"') { inQuotes = true; continue; }
                if (c == ',') { row.Add(sb.ToString()); sb.Clear(); continue; }
                if (c == '\n') { row.Add(sb.ToString()); return row; }
                if (c == '\r')
                {
                    if (reader.Peek() == '\n') reader.Read();
                    row.Add(sb.ToString());
                    return row;
                }
                sb.Append(c);
            }
        }

        private static string CsvEscape(string value)
        {
            if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
        {
            "a", "an", "the", "of", "for", "to", "and", "or", "in", "on", "with", "by", "is", "that", "this", "from", "as", "at", "be", "are",
        };

        private static readonly Dictionary<string, string[]> WholeWord = new(StringComparer.Ordinal)
        {
            ["id"] = ["identifier"],
            ["num"] = ["number"],
            ["nbr"] = ["number"],
            ["no"] = ["number"],
            ["dt"] = ["date"],
            ["cd"] = ["code"],
            ["desc"] = ["description"],
            ["qty"] = ["quantity"],
            ["amt"] = ["amount"],
            ["nm"] = ["name"],
            ["yr"] = ["year"],
            ["mo"] = ["month"],
            ["dob"] = ["date", "birth"],
            ["ts"] = ["timestamp"],
            ["cnt"] = ["count"],
            ["ct"] = ["count"],
            ["flg"] = ["flag"],
            ["src"] = ["source"],
            ["val"] = ["value"],
            ["cat"] = ["category"],
            ["typ"] = ["type"],
            ["std"] = ["standard"],
            ["seq"] = ["sequence"],
            ["subj"] = ["subject"],
            ["pt"] = ["patient"],
            ["pat"] = ["patient"],
        };

        // Longest-first pieces for glued abbreviations (BRTHDTC, USUBJID). Not an OMOP/CDISC field list.
        private static readonly string[] SegmentKeys =
        [
            "identifier", "occurrence", "datetime", "timestamp", "sequence", "subject", "concept", "patient", "number", "gender",
            "usubj", "birth", "visit", "person", "value", "count", "month", "start", "brth", "dtc", "subj", "date", "year", "time",
            "code", "name", "type", "flag", "seq", "num", "id", "dt",
        ];

        private static readonly string[][] SegmentExpansions =
        [
            ["identifier"], ["occurrence"], ["datetime"], ["timestamp"], ["sequence"], ["subject"], ["concept"], ["patient"], ["number"], ["gender"],
            ["subject"], ["birth"], ["visit"], ["person"], ["value"], ["count"], ["month"], ["start"], ["birth"], ["date"], ["subject"], ["date"], ["year"], ["time"],
            ["code"], ["name"], ["type"], ["flag"], ["sequence"], ["number"], ["identifier"], ["date"],
        ];

        private static readonly HashSet<string> Roots = new(StringComparer.Ordinal)
        {
            "identifier", "occurrence", "datetime", "timestamp", "sequence", "subject", "concept", "patient", "number", "gender",
            "usubj", "birth", "visit", "person", "brth", "dtc", "subj", "date", "year", "time", "code", "name",
        };

        private sealed class PreparedSource(
            SourceColumn column,
            IReadOnlyList<string> nameTexts,
            IReadOnlyList<string> labelTexts,
            HashSet<string> nameTokens,
            HashSet<string> labelTokens,
            string nameJoined,
            string compact,
            int nonEmpty,
            int dates,
            int integers)
        {
            public SourceColumn Column { get; } = column;
            public IReadOnlyList<string> NameTexts { get; } = nameTexts;
            public IReadOnlyList<string> LabelTexts { get; } = labelTexts;
            public HashSet<string> NameTokens { get; } = nameTokens;
            public HashSet<string> LabelTokens { get; } = labelTokens;
            public string NameJoined { get; } = nameJoined;
            public string Compact { get; } = compact;
            public int NonEmpty { get; } = nonEmpty;
            public int Dates { get; } = dates;
            public int Integers { get; } = integers;
        }

        private sealed class PreparedTarget(
            TargetField field,
            int index,
            HashSet<string> nameTokens,
            HashSet<string> labelTokens,
            HashSet<string> guidanceTokens,
            HashSet<string> tableTokens,
            string nameJoined,
            string compact,
            string labelJoined,
            SpecTypeKind kind,
            bool concept,
            bool idField,
            bool dateHint)
        {
            public TargetField Field { get; } = field;
            public int Index { get; } = index;
            public HashSet<string> NameTokens { get; } = nameTokens;
            public HashSet<string> LabelTokens { get; } = labelTokens;
            public HashSet<string> GuidanceTokens { get; } = guidanceTokens;
            public HashSet<string> TableTokens { get; } = tableTokens;
            public string NameJoined { get; } = nameJoined;
            public string Compact { get; } = compact;
            public string LabelJoined { get; } = labelJoined;
            public SpecTypeKind Kind { get; } = kind;
            public bool Concept { get; } = concept;
            public bool IdField { get; } = idField;
            public bool DateHint { get; } = dateHint;
        }
    }

    public enum SpecLayoutKind
    {
        Unknown = 0,
        OmopFieldLevel,
        CdiscVariable,
        Custom,
    }

    public enum SpecTypeKind
    {
        Unknown = 0,
        Integer,
        Float,
        Numeric,
        String,
        Date,
        DateTime,
        Time,
        Boolean,
    }

    /// <summary>명세 CSV에서 어느 열이 이름·라벨·타입·테이블인지. 자동 감지 결과이거나 사용자가 고른 열.</summary>
    public readonly record struct SpecColumnRoles(
        SpecLayoutKind Layout,
        int Name,
        int? Table = null,
        int? Label = null,
        int? Type = null,
        int? Required = null,
        int? Length = null,
        int? Codelist = null,
        int? Guidance = null,
        int? FkTable = null,
        int? FkDomain = null);

    public sealed record TargetField(
        string Table,
        string Name,
        string? Label,
        string? DataType,
        bool Required,
        int? Length,
        string? Codelist,
        string? Guidance,
        string? FkTable,
        string? FkDomain);

    public sealed record TargetSpec(
        SpecLayoutKind Layout,
        string? SourceName,
        IReadOnlyList<string> Headers,
        IReadOnlyList<TargetField> Fields);

    public sealed record SourceColumn(
        string Name,
        ColumnValueType InferredType = ColumnValueType.Empty,
        string? VariableName = null,
        string? VariableLabel = null,
        IReadOnlyList<string>? Sample = null);

    public sealed record MappingSuggestOptions
    {
        public string? TargetTable { get; init; }
        public int TopK { get; init; } = VariableMapping.DefaultTopK;
        public double AcceptScore { get; init; } = VariableMapping.DefaultAcceptScore;
        public int SampleRowsRead { get; init; }
        public int SampleRowCap { get; init; } = VariableMapping.DefaultSampleRowCap;
        public bool SampleCapped { get; init; }
    }

    public sealed record MappingReason(string Code, string Text);

    public sealed record MappingCandidate(
        int TargetIndex,
        string Table,
        string Field,
        double Score,
        IReadOnlyList<MappingReason> Reasons);

    public sealed record ColumnSuggestions(
        int SourceIndex,
        string SourceColumn,
        IReadOnlyList<MappingCandidate> Candidates);

    public sealed record AcceptedMapping(
        int SourceIndex,
        string SourceColumn,
        int TargetIndex,
        string Table,
        string Field,
        double Score);

    public sealed record RequiredGap(string Table, string Field, string? DataType);

    public sealed record MappingSuggestionResult(
        IReadOnlyList<ColumnSuggestions> Columns,
        IReadOnlyList<AcceptedMapping> Accepted,
        IReadOnlyList<RequiredGap> UnmatchedRequired,
        string? TargetTable,
        int SampleRowsRead,
        int SampleRowCap,
        bool SampleCapped,
        int FieldsConsidered,
        bool IndexedCandidatePool);

    public sealed class MappingException : InvalidOperationException
    {
        public MappingException(string message) : base(message) { }
    }
}
