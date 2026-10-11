using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NanumCsvViewer.Csv.DataQuality
{
    /// <summary>
    /// OHDSI DataQualityDashboard 결과 JSON을 발견 목록으로 읽는다.
    /// 스키마 출처(필드명·CheckResults 위치, 의료 규칙 팩이 아님):
    /// https://github.com/OHDSI/DataQualityDashboard/blob/main/R/recordResult.R
    /// https://github.com/OHDSI/DataQualityDashboard/blob/main/R/evaluateThresholds.R
    /// https://github.com/OHDSI/DataQualityDashboard/blob/main/R/executeDqChecks.R
    /// https://github.com/OHDSI/DataQualityDashboard/blob/main/R/writeResultsTo.R
    /// (.writeResultsToJson → jsonlite::toJSON, 기본 auto_unbox=false 이므로 길이 1 배열도 허용).
    /// 없는 필드는 건너뛴다. 행 술어는 만들지 않는다 — 이 파일의 행이 아니다.
    /// </summary>
    public static class DqdResultsImport
    {
        public sealed record Options
        {
            /// <summary>null이면 모든 테이블. 있으면 cdmTableName과 대소문자 무시 일치만 가져온다.</summary>
            public string? TableName { get; init; }
        }

        public sealed record Result
        {
            public required IReadOnlyList<QualityFinding> Findings { get; init; }
            public int ChecksRead { get; init; }
            public int ChecksImported { get; init; }
            public int ChecksFilteredOut { get; init; }
            public int FailedChecks { get; init; }
            public int ErrorChecks { get; init; }
            public int NotApplicableChecks { get; init; }
            public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        }

        public static Result Import(string json, Options? options = null, IReadOnlyList<string>? headers = null)
        {
            ArgumentNullException.ThrowIfNull(json);
            if (string.IsNullOrWhiteSpace(json))
                throw new JsonException("DQD results JSON is empty.");

            options ??= new Options();
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch (JsonException)
            {
                throw;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!TryGetCheckResults(root, out var results, out string? why))
                    throw new DqdImportException(why ?? "DQD results JSON has no CheckResults array.");

                var rows = EnumerateRows(results, out string? shapeWarning);
                var warnings = new List<string>();
                if (shapeWarning is not null) warnings.Add(shapeWarning);

                string? tableFilter = string.IsNullOrWhiteSpace(options.TableName) ? null : options.TableName.Trim();
                var findings = new List<QualityFinding>();
                int read = 0, filtered = 0, failed = 0, errors = 0, na = 0, unnamedTable = 0, skipped = 0;

                foreach (var row in rows)
                {
                    if (!row.IsObject)
                    {
                        skipped++;
                        continue;
                    }
                    read++;
                    string? table = row.ReadString("cdmtablename");
                    if (tableFilter is not null)
                    {
                        if (string.IsNullOrWhiteSpace(table))
                        {
                            unnamedTable++;
                            filtered++;
                            continue;
                        }
                        if (!string.Equals(table.Trim(), tableFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            filtered++;
                            continue;
                        }
                    }

                    var finding = ToFinding(row, headers);
                    findings.Add(finding);
                    if (finding.Severity == QualitySeverity.Critical) failed++;
                    else if (row.IsError) errors++;
                    if (row.NotApplicable) na++;
                }

                if (skipped > 0)
                    warnings.Add($"{skipped} CheckResults entr{(skipped == 1 ? "y was" : "ies were")} not an object and were skipped.");
                if (unnamedTable > 0)
                    warnings.Add($"{unnamedTable} check(s) had no cdmTableName and were excluded by the table filter.");

                return new Result
                {
                    Findings = findings,
                    ChecksRead = read,
                    ChecksImported = findings.Count,
                    ChecksFilteredOut = filtered,
                    FailedChecks = failed,
                    ErrorChecks = errors,
                    NotApplicableChecks = na,
                    Warnings = warnings,
                };
            }
        }

        private static QualityFinding ToFinding(DqdRow row, IReadOnlyList<string>? headers)
        {
            string? checkName = row.ReadString("checkname");
            string? field = row.ReadString("cdmfieldname");
            string? table = row.ReadString("cdmtablename");
            string? concept = row.ReadString("conceptid");
            string? level = row.ReadString("checklevel");
            string? category = row.ReadString("category");
            string? subcategory = row.ReadString("subcategory");
            string? description = row.ReadString("checkdescription");
            string? error = row.ReadString("error");
            string? naReason = row.ReadString("notapplicablereason");
            long? violated = row.ReadLong("numviolatedrows");
            long? denom = row.ReadLong("numdenominatorrows");
            double? pct = row.ReadDouble("pctviolatedrows");
            double? threshold = row.ReadDouble("thresholdvalue");
            bool? failedFlag = row.ReadBool("failed");

            string label = string.IsNullOrWhiteSpace(checkName) ? "(unnamed check)" : checkName.Trim();
            if (!IsBlankToken(concept))
                label += "#" + concept!.Trim();

            string columnName = FormatColumn(table, field);
            int column = MatchField(headers, field);

            var note = new StringBuilder("imported from DQD");
            Append(note, level);
            if (!IsBlankToken(category) || !IsBlankToken(subcategory))
                Append(note, JoinSlash(category, subcategory));
            if (threshold is double t)
                Append(note, "threshold " + t.ToString("G", CultureInfo.InvariantCulture));
            if (pct is double p)
                Append(note, "pctViolatedRows " + p.ToString("G", CultureInfo.InvariantCulture));
            if (!IsBlankToken(description))
                Append(note, Clip(description!.Trim(), 180));
            if (row.IsError)
                Append(note, "error: " + (IsBlankToken(error) ? "(no message)" : Clip(error!.Trim(), 180)));
            if (row.NotApplicable)
                Append(note, "not applicable" + (IsBlankToken(naReason) ? "" : ": " + Clip(naReason!.Trim(), 120)));
            if (violated is null)
                Append(note, "violated-row count not reported");
            if (denom is null)
                Append(note, "denominator not reported");
            if (failedFlag is null && !row.IsError && !row.NotApplicable)
                Append(note, "failed flag not reported");
            if (IsBlankToken(category))
                Append(note, "category not reported");

            return new QualityFinding
            {
                Kind = QualityCheckKind.DqdImported,
                Dimension = MapDimension(category),
                Severity = row.Severity,
                Column = column,
                ColumnName = columnName,
                Label = label,
                ViolationCount = violated ?? 0,
                EvaluatedRows = denom ?? 0,
                // 건수가 소스에 없으면 0은 추정이 아니라 "없음". Approximate로 정직하게 표시한다.
                Approximate = violated is null,
                Breakdown = new[] { new ValueCount(note.ToString(), 0) },
            };
        }

        private static QualityDimension MapDimension(string? category)
        {
            if (IsBlankToken(category)) return QualityDimension.Conformance;
            string c = category!.Trim();
            if (c.Equals("conformance", StringComparison.OrdinalIgnoreCase)) return QualityDimension.Conformance;
            if (c.Equals("completeness", StringComparison.OrdinalIgnoreCase)) return QualityDimension.Completeness;
            if (c.Equals("plausibility", StringComparison.OrdinalIgnoreCase)) return QualityDimension.Plausibility;
            return QualityDimension.Conformance;
        }

        private static int MatchField(IReadOnlyList<string>? headers, string? field)
        {
            if (headers is null || IsBlankToken(field)) return -1;
            string name = field!.Trim();
            for (int i = 0; i < headers.Count; i++)
            {
                if (string.Equals(headers[i]?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string FormatColumn(string? table, string? field)
        {
            bool hasTable = !IsBlankToken(table);
            bool hasField = !IsBlankToken(field);
            if (hasTable && hasField) return table!.Trim() + "." + field!.Trim();
            if (hasField) return field!.Trim();
            if (hasTable) return table!.Trim();
            return "";
        }

        private static bool TryGetCheckResults(JsonElement root, out JsonElement results, out string? why)
        {
            results = default;
            why = null;
            if (root.ValueKind == JsonValueKind.Array)
            {
                results = root;
                return true;
            }
            if (root.ValueKind != JsonValueKind.Object)
            {
                why = "DQD results JSON must be an object with CheckResults, or an array of checks.";
                return false;
            }
            foreach (var prop in root.EnumerateObject())
            {
                if (Normalize(prop.Name) != "checkresults") continue;
                results = prop.Value;
                if (results.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    return true;
                why = "CheckResults is not an array or object.";
                return false;
            }
            why = "DQD results JSON has no CheckResults array.";
            return false;
        }

        private static List<DqdRow> EnumerateRows(JsonElement results, out string? warning)
        {
            warning = null;
            var rows = new List<DqdRow>();
            if (results.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in results.EnumerateArray())
                    rows.Add(new DqdRow(el, columnar: null, index: 0));
                return rows;
            }
            if (results.ValueKind != JsonValueKind.Object)
                return rows;

            // jsonlite dataframe="columns": 속성마다 배열. 한 행 객체가 길이 1 배열로 박스된 경우도 같은 경로.
            int n = 0;
            bool anyArray = false;
            foreach (var prop in results.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                anyArray = true;
                n = Math.Max(n, prop.Value.GetArrayLength());
            }
            if (!anyArray)
            {
                rows.Add(new DqdRow(results, columnar: null, index: 0));
                return rows;
            }
            if (n == 0)
            {
                warning = "CheckResults had array columns of length 0.";
                return rows;
            }
            for (int i = 0; i < n; i++)
                rows.Add(new DqdRow(default, results, i));
            return rows;
        }

        private static string Normalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == '_' || c == ' ') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static bool IsBlankToken(string? s)
            => string.IsNullOrWhiteSpace(s)
               || s.Equals("NA", StringComparison.OrdinalIgnoreCase)
               || s.Equals("null", StringComparison.OrdinalIgnoreCase)
               || s.Equals("NaN", StringComparison.OrdinalIgnoreCase);

        private static void Append(StringBuilder sb, string? part)
        {
            if (string.IsNullOrWhiteSpace(part)) return;
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append(part);
        }

        private static string JoinSlash(string? a, string? b)
        {
            bool ha = !IsBlankToken(a);
            bool hb = !IsBlankToken(b);
            if (ha && hb) return a!.Trim() + "/" + b!.Trim();
            return ha ? a!.Trim() : b!.Trim();
        }

        private static string Clip(string s, int max)
            => s.Length <= max ? s : s[..max] + "…";

        private readonly struct DqdRow
        {
            private readonly JsonElement _obj;
            private readonly JsonElement _columns;
            private readonly int _index;
            private readonly bool _columnar;
            public bool IsObject { get; }

            public DqdRow(JsonElement obj, JsonElement? columnar, int index)
            {
                if (columnar is JsonElement cols)
                {
                    _columns = cols;
                    _index = index;
                    _columnar = true;
                    _obj = default;
                    IsObject = true;
                }
                else
                {
                    _obj = obj;
                    _columns = default;
                    _index = 0;
                    _columnar = false;
                    IsObject = obj.ValueKind == JsonValueKind.Object;
                }
            }

            public bool IsError
            {
                get
                {
                    bool? flag = ReadBool("iserror");
                    if (flag == true) return true;
                    if (flag == false) return false;
                    return !IsBlankToken(ReadString("error"));
                }
            }

            public bool NotApplicable => ReadBool("notapplicable") == true;

            public QualitySeverity Severity
            {
                get
                {
                    if (IsError) return QualitySeverity.Warning;
                    if (NotApplicable) return QualitySeverity.Info;
                    if (ReadBool("failed") == true) return QualitySeverity.Critical;
                    return QualitySeverity.Info;
                }
            }

            public string? ReadString(string canonical)
            {
                if (!TryGet(canonical, out var el)) return null;
                el = Unwrap(el);
                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => null,
                };
            }

            public long? ReadLong(string canonical)
            {
                if (!TryGet(canonical, out var el)) return null;
                el = Unwrap(el);
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long n)) return n;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
                    return (long)Math.Round(d);
                if (el.ValueKind == JsonValueKind.String
                    && long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long parsed))
                    return parsed;
                return null;
            }

            public double? ReadDouble(string canonical)
            {
                if (!TryGet(canonical, out var el)) return null;
                el = Unwrap(el);
                if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d))
                    return d;
                if (el.ValueKind == JsonValueKind.String
                    && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                    && double.IsFinite(parsed))
                    return parsed;
                return null;
            }

            public bool? ReadBool(string canonical)
            {
                if (!TryGet(canonical, out var el)) return null;
                el = Unwrap(el);
                switch (el.ValueKind)
                {
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.Number:
                        return el.TryGetDouble(out double d) ? d != 0 : null;
                    case JsonValueKind.String:
                        string? s = el.GetString();
                        if (IsBlankToken(s)) return null;
                        if (s!.Equals("1", StringComparison.Ordinal) || s.Equals("true", StringComparison.OrdinalIgnoreCase))
                            return true;
                        if (s.Equals("0", StringComparison.Ordinal) || s.Equals("false", StringComparison.OrdinalIgnoreCase))
                            return false;
                        return null;
                    default:
                        return null;
                }
            }

            private bool TryGet(string canonical, out JsonElement el)
            {
                el = default;
                var obj = _columnar ? _columns : _obj;
                if (obj.ValueKind != JsonValueKind.Object) return false;
                foreach (var prop in obj.EnumerateObject())
                {
                    if (Normalize(prop.Name) != canonical) continue;
                    el = prop.Value;
                    if (_columnar)
                    {
                        if (el.ValueKind != JsonValueKind.Array || _index < 0 || _index >= el.GetArrayLength())
                            return false;
                        el = el[_index];
                    }
                    return el.ValueKind != JsonValueKind.Undefined;
                }
                return false;
            }

            private static JsonElement Unwrap(JsonElement el)
            {
                while (el.ValueKind == JsonValueKind.Array)
                {
                    int n = el.GetArrayLength();
                    if (n == 0) return default;
                    if (n > 1) return el;
                    el = el[0];
                }
                return el;
            }
        }
    }

    /// <summary>DQD JSON이 결과 파일이 아니다. 필드 누락은 오류가 아니다.</summary>
    public sealed class DqdImportException : Exception
    {
        public DqdImportException(string message) : base(message) { }
    }
}
