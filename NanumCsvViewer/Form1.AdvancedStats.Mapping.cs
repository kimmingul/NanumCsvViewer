using System.Globalization;
using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private async void AdvVariableMapping()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            string title = LT("Variable Mapping Suggestions", "변수 매핑 추천");
            if (_doc.ColumnCount == 0)
            {
                ShowResult(title, LT("This file has no columns.", "이 파일에는 컬럼이 없습니다."));
                return;
            }

            string specPath;
            using (var ofd = new OpenFileDialog
            {
                Title = LT("Target data-model specification (CSV)", "대상 데이터 모형 명세(CSV)"),
                Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
                RestoreDirectory = true,
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                specPath = ofd.FileName;
            }

            string[] specHeader;
            try
            {
                specHeader = VariableMapping.ReadHeader(specPath);
            }
            catch (Exception ex)
            {
                ShowResult(title, Stats.ErrorText.Localize(ex.Message));
                return;
            }

            SpecColumnRoles roles;
            if (VariableMapping.TryDetectLayout(specHeader, out var detected))
                roles = detected;
            else if (!TryPickSpecColumns(title, specHeader, out roles))
                return;

            TargetSpec spec;
            try
            {
                spec = VariableMapping.Load(specPath, roles);
            }
            catch (Exception ex)
            {
                ShowResult(title, Stats.ErrorText.Localize(ex.Message));
                return;
            }

            var tables = spec.Fields
                .Select(f => f.Table)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var tableItems = new[] { LT("(all tables)", "(모든 테이블)") }.Concat(tables).ToArray();

            using var dlg = new ParamDialog(title, _palette);
            var tableBox = dlg.AddCombo(LT("Target table", "대상 테이블"), tableItems, 0);
            var accept = dlg.AddNumeric(LT("Accept score (0–100)", "수용 점수(0–100)"), 0, 100, (int)VariableMapping.DefaultAcceptScore);
            var saveCsv = dlg.AddCombo(LT("Save suggestion CSV", "추천 CSV 저장"), new[] { LT("No", "아니오"), LT("Yes", "예") }, 0);
            var saveProfile = dlg.AddCombo(LT("Save conformance profile", "적합성 프로파일 저장"), new[] { LT("No", "아니오"), LT("Yes", "예") }, 0);
            dlg.AddNote(LT(
                "Suggestions, not assertions. A user-supplied specification is scored against the open file (name, SPSS/SAS label when the workbook has one, inferred type, a capped value sample, and the chosen table). No OMOP or CDISC content is bundled. The optional profile copies required/type/length onto the open file's column names for accepted suggestions only.",
                "추천일 뿐 단정이 아닙니다. 사용자가 준 명세를 열린 파일과 비교합니다(이름, 워크북에 SPSS/SAS 변수 라벨이 있으면 그 라벨, 추론 타입, 상한이 있는 값 표본, 고른 테이블). OMOP·CDISC 내용은 포함되어 있지 않습니다. 선택적 프로파일은 수용된 추천에 한해 열린 파일의 컬럼명에 required/type/length를 복사합니다."));
            if (!dlg.ShowOk(this)) return;

            string? targetTable = tableBox.SelectedIndex <= 0 ? null : tables[tableBox.SelectedIndex - 1];
            string? csvPath = null;
            string? profilePath = null;
            if (saveCsv.SelectedIndex == 1)
            {
                using var save = new SaveFileDialog
                {
                    Filter = "CSV (*.csv)|*.csv",
                    FileName = "mapping-suggestions.csv",
                    RestoreDirectory = true,
                };
                if (save.ShowDialog(this) == DialogResult.OK) csvPath = save.FileName;
            }
            if (saveProfile.SelectedIndex == 1)
            {
                using var save = new SaveFileDialog
                {
                    Filter = "JSON (*.json)|*.json",
                    FileName = "mapping-profile.json",
                    RestoreDirectory = true,
                };
                if (save.ShowDialog(this) == DialogResult.OK) profilePath = save.FileName;
            }

            var headers = AdvHeaders();
            var types = new ColumnValueType[headers.Length];
            var variableNames = new string?[headers.Length];
            var variableLabels = new string?[headers.Length];
            var names = _workbook?.VariableNames(_currentSheetIndex);
            var labels = _workbook?.VariableLabels(_currentSheetIndex);
            for (int c = 0; c < headers.Length; c++)
            {
                types[c] = c < _columnSummaries.Length ? _columnSummaries[c].InferredType : ColumnValueType.Empty;
                if (names is not null && c < names.Count) variableNames[c] = names[c];
                if (labels is not null && c < labels.Count) variableLabels[c] = labels[c];
            }

            double acceptScore = (double)accept.Value;
            await RunAdvancedAsync(title, input =>
            {
                var sources = CollectMappingSources(input, headers, types, variableNames, variableLabels, out int rowsRead, out bool capped);
                var opt = new MappingSuggestOptions
                {
                    TargetTable = targetTable,
                    AcceptScore = acceptScore,
                    SampleRowsRead = rowsRead,
                    SampleRowCap = VariableMapping.DefaultSampleRowCap,
                    SampleCapped = capped,
                };
                var result = VariableMapping.Suggest(sources, spec, opt, input.Cancellation);
                var text = new StringBuilder();
                text.AppendLine(LT(
                    "Suggestions, not assertions. Scores are explainable heuristics, not a claim that a column is the target field.",
                    "추천일 뿐 단정이 아닙니다. 점수는 설명 가능한 휴리스틱이며, 컬럼이 그 대상 필드라고 단정하지 않습니다."));
                text.AppendLine(LT(
                    $"Layout: {LayoutCaption(spec.Layout)}.",
                    $"레이아웃: {LayoutCaption(spec.Layout)}."));
                text.AppendLine();
                bool korean = Loc.CurrentLanguage == "ko";
                text.Append(VariableMapping.Format(result, spec, opt, korean));

                if (csvPath is not null)
                {
                    try
                    {
                        File.WriteAllText(csvPath, VariableMapping.ExportCsv(result, korean), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        text.AppendLine().AppendLine(LT($"Saved suggestion CSV: {csvPath}", $"추천 CSV 저장: {csvPath}"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        text.AppendLine().AppendLine(LT($"Suggestion CSV was not saved: {ex.Message}", $"추천 CSV를 저장하지 못했습니다: {ex.Message}"));
                    }
                }
                if (profilePath is not null)
                {
                    try
                    {
                        File.WriteAllText(profilePath, VariableMapping.ExportProfileJson(result, spec), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        text.AppendLine().AppendLine(LT(
                            $"Saved conformance profile skeleton (accepted suggestions only): {profilePath}",
                            $"적합성 프로파일 골격 저장(수용된 추천만): {profilePath}"));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        text.AppendLine().AppendLine(LT($"Conformance profile was not saved: {ex.Message}", $"적합성 프로파일을 저장하지 못했습니다: {ex.Message}"));
                    }
                }
                return text.ToString();
            });
        }

        private bool TryPickSpecColumns(string title, string[] header, out SpecColumnRoles roles)
        {
            roles = default;
            using var dlg = new ParamDialog(title, _palette);
            var none = new[] { LT("(none)", "(없음)") };
            string[] optional = none.Concat(header).ToArray();
            var name = dlg.AddCombo(LT("Field name column", "필드 이름 열"), header, GuessColumn(header, "name", "variable", "field", "column"));
            var table = dlg.AddCombo(LT("Table column", "테이블 열"), optional, GuessOptional(header, "table", "dataset", "domain"));
            var label = dlg.AddCombo(LT("Label column", "라벨 열"), optional, GuessOptional(header, "label", "description", "guidance"));
            var type = dlg.AddCombo(LT("Type column", "타입 열"), optional, GuessOptional(header, "type", "datatype", "data type"));
            var required = dlg.AddCombo(LT("Required column", "필수 열"), optional, GuessOptional(header, "required", "core", "isrequired"));
            var length = dlg.AddCombo(LT("Length column", "길이 열"), optional, GuessOptional(header, "length", "maxlength"));
            dlg.AddNote(LT(
                "Headers were not recognized as OHDSI OMOP field-level (cdmTableName, cdmFieldName) or CDISC variable metadata (Dataset, Variable). Pick the columns to use. This does not load any bundled model.",
                "헤더가 OHDSI OMOP 필드 레벨(cdmTableName, cdmFieldName)이나 CDISC 변수 메타데이터(Dataset, Variable)로 보이지 않습니다. 쓸 열을 고르세요. 내장 모형은 불러오지 않습니다."));
            if (!dlg.ShowOk(this)) return false;
            roles = new SpecColumnRoles(
                SpecLayoutKind.Custom,
                name.SelectedIndex,
                OptionalIndex(table.SelectedIndex),
                OptionalIndex(label.SelectedIndex),
                OptionalIndex(type.SelectedIndex),
                OptionalIndex(required.SelectedIndex),
                OptionalIndex(length.SelectedIndex));
            return true;
        }

        private static SourceColumn[] CollectMappingSources(
            AdvancedInput input,
            string[] headers,
            ColumnValueType[] types,
            string?[] variableNames,
            string?[] variableLabels,
            out int rowsRead,
            out bool capped)
        {
            int n = headers.Length;
            var buckets = new List<string>[n];
            for (int c = 0; c < n; c++) buckets[c] = new List<string>(VariableMapping.MaxSampleValuesPerColumn);
            int cap = VariableMapping.DefaultSampleRowCap;
            int limit = Math.Min(cap, input.Rows.Count);
            capped = input.Rows.Count > cap;
            rowsRead = 0;
            for (int i = 0; i < limit; i++)
            {
                if ((i & 31) == 0) input.Cancellation.ThrowIfCancellationRequested();
                var row = input.Rows[i];
                rowsRead++;
                for (int c = 0; c < n; c++)
                {
                    if (buckets[c].Count >= VariableMapping.MaxSampleValuesPerColumn) continue;
                    if (c >= row.Length) continue;
                    string cell = row[c];
                    if (string.IsNullOrWhiteSpace(cell)) continue;
                    string trimmed = cell.Trim();
                    if (ColumnStatisticsBuilder.IsNullToken(trimmed)) continue;
                    buckets[c].Add(trimmed.Length > 80 ? trimmed[..80] : trimmed);
                }
            }

            var sources = new SourceColumn[n];
            for (int c = 0; c < n; c++)
            {
                sources[c] = new SourceColumn(
                    headers[c],
                    c < types.Length ? types[c] : ColumnValueType.Empty,
                    c < variableNames.Length ? variableNames[c] : null,
                    c < variableLabels.Length ? variableLabels[c] : null,
                    buckets[c]);
            }
            return sources;
        }

        private static int? OptionalIndex(int selected) => selected <= 0 ? null : selected - 1;

        private static int GuessColumn(string[] header, params string[] hints)
        {
            for (int i = 0; i < header.Length; i++)
            {
                string h = header[i].Trim();
                foreach (string hint in hints)
                    if (h.Contains(hint, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return 0;
        }

        private static int GuessOptional(string[] header, params string[] hints)
        {
            int hit = GuessColumn(header, hints);
            if (header.Length == 0) return 0;
            string h = header[hit].Trim();
            foreach (string hint in hints)
                if (h.Contains(hint, StringComparison.OrdinalIgnoreCase)) return hit + 1;
            return 0;
        }

        private static string LayoutCaption(SpecLayoutKind layout) => layout switch
        {
            SpecLayoutKind.OmopFieldLevel => LT("OMOP field-level", "OMOP 필드 레벨"),
            SpecLayoutKind.CdiscVariable => LT("CDISC variable metadata", "CDISC 변수 메타데이터"),
            SpecLayoutKind.Custom => LT("custom columns", "사용자 지정 열"),
            _ => LT("unknown", "알 수 없음"),
        };
    }
}
