using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Agent
{
    // 로컬 Python 분석 보조 도구: csv.export_view(현재 뷰 → 로컬 CSV + schema.json), csv.show_markdown, csv.show_image.
    // export_view는 설정 "로컬 Python 분석 허용"이 켜져 있을 때만 동작한다. 결과(모델로 가는 것)에는 경로와 개수만 있고 셀 값은 없다.
    // show_*는 보기 전용: 허용된 폴더 안의 허용된 확장자만 열어 임의 실행 파일을 띄우지 않는다.
    public sealed partial class CsvHostTools
    {
        /// <summary>내보내기 행 수 상한. 넘으면 정직하게 거절하고 필터를 안내한다(스냅샷 메모리·디스크 보호).</summary>
        internal const long MaxExportRows = 5_000_000;
        private const long MaxMarkdownBytes = 20L * 1024 * 1024;
        private const long MaxImageBytes = 200L * 1024 * 1024;

        private static readonly string[] MarkdownExtensions = { ".md", ".markdown" };
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg" };
        private const string PdfExtension = ".pdf";

        private static readonly Regex UnsafeNameChars = new(@"[^\p{L}\p{Nd}_.\- ]", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions SchemaJson = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private string? OpenFilePath(AgentDocumentInfo info)
            => string.IsNullOrEmpty(info.Directory) || string.IsNullOrEmpty(info.FileName) ? null : Path.Combine(info.Directory, info.FileName);


        private static string EnsureFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>파일 이름으로 쓸 수 있게: 경로 구분자·예약 문자 제거, 공백 정리, 길이 제한. 비면 fallback.</summary>
        internal static string SafeBaseName(string? requested, string fallback)
        {
            string s = UnsafeNameChars.Replace(requested ?? "", "_").Trim().Trim('.').Trim();
            if (s.Length > 80) s = s[..80].TrimEnd();
            if (s.Length == 0) s = UnsafeNameChars.Replace(fallback, "_").Trim().Trim('.').Trim();
            if (s.Length == 0) s = "view";
            // 예약 장치 이름(CON, NUL …)은 Windows에서 파일로 만들 수 없다.
            if (Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)) s = "_" + s;
            return s;
        }

        // ------------------------------------------------------------------ csv.export_view

        private async Task<HostToolResult> ExportViewAsync(ToolArgs args, CancellationToken ct)
        {
            var options = Options();
            if (!options.AllowLocalPython)
                throw new AgentToolException(
                    "Refused: local Python analysis is switched off, so the view is not exported to a file. " +
                    "Tell the user they can allow it in the AI agent settings (\"Allow local Python analysis\" / \"로컬 Python 분석 허용\"). " +
                    "Meanwhile use csv.run_analysis, csv.column_stats or csv.quality_scan, which return aggregates only.");

            string? requestedName = args.OptString("name");
            var requestedColumns = args.OptStringArray("columns", 500);
            var info = RequireReady();
            var names = Names(info);
            List<int> cols = requestedColumns is { Count: > 0 }
                ? ColumnNames.ResolveMany(names, requestedColumns, "columns")
                : Enumerable.Range(0, names.Length).ToList();

            if (info.ViewRows == 0) throw new AgentToolException("The current view has no rows (the filter matches nothing); nothing to export.");
            if (info.ViewRows > MaxExportRows)
                throw new AgentToolException($"Refused: the current view has {info.ViewRows:N0} rows; exports are limited to {MaxExportRows:N0}. Narrow it with csv.set_filter first.");

            string openPath = OpenFilePath(info) ?? "";
            string baseName = SafeBaseName(requestedName, !string.IsNullOrWhiteSpace(info.TabName) ? info.TabName! : Path.GetFileNameWithoutExtension(info.FileName) + "_view");
            string outputFolder = _host.AnalysisFolder is { Length: > 0 } hostFolder
                ? EnsureFolder(hostFolder)
                : AgentWorkspace.OutputFolderFor(string.IsNullOrEmpty(openPath) ? null : openPath);
            string dataDir = Path.Combine(outputFolder, "data");
            Directory.CreateDirectory(dataDir);
            string csvPath = Path.Combine(dataDir, baseName + ".csv");
            string schemaPath = Path.Combine(dataDir, baseName + ".schema.json");
            if (AgentSavePolicy.SameFile(csvPath, openPath) || (info.ProtectedPaths.Any(p => AgentSavePolicy.SameFile(csvPath, p) || AgentSavePolicy.SameFile(schemaPath, p))))
                throw new AgentToolException("Refused: that name would overwrite the source file. Choose another name.");
            bool replaced = File.Exists(csvPath);

            bool sharesValues = options.DataPolicy != AgentDataPolicy.SummaryOnly;
            var filters = new JsonArray();
            foreach (var f in info.Filters)
            {
                // 식 필터는 모델/사용자가 쓴 식이다. 셀값·검색어·컬럼 필터 설명에는 셀 값이 섞일 수 있어 요약만 정책에서는 종류만 남긴다.
                var fo = new JsonObject { ["kind"] = FilterKindName(f.Kind) };
                if (f.Column is not null) fo["column"] = f.Column;
                if (f.Kind == AgentFilterKind.Expression || sharesValues) fo["text"] = f.Description;
                filters.Add(fo);
            }
            var sort = new JsonArray();
            foreach (var s in info.Sort) sort.Add(new JsonObject { ["column"] = s.Column, ["order"] = s.Ascending ? "asc" : "desc" });

            string[] outNames = cols.Select(c => names[c].Length == 0 ? $"Column{c + 1}" : names[c]).ToArray();
            string tmp = csvPath + ".tmp";

            var written = await _host.RunOnViewRowsAsync(
                "Export view",
                (data, token) => WriteViewCsv(data, cols, outNames, tmp, token),
                ct);
            try
            {
                File.Move(tmp, csvPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }

            var columnsJson = new JsonArray();
            for (int i = 0; i < cols.Count; i++)
            {
                var type = written.Types[i];
                columnsJson.Add(new JsonObject
                {
                    ["name"] = outNames[i],
                    ["type"] = type.DisplayName(),
                    ["numeric"] = type.IsNumeric(),
                    ["dtype_hint"] = DtypeHint(type),
                });
            }
            var schema = new JsonObject
            {
                ["csv_file"] = Path.GetFileName(csvPath),
                ["source_file"] = info.FileName,
                ["sheet"] = info.SheetName,
                ["exported_at"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
                ["row_count"] = written.Rows,
                ["source_total_rows"] = info.TotalRows,
                ["column_count"] = cols.Count,
                ["format"] = new JsonObject { ["encoding"] = "utf-8 (no BOM)", ["delimiter"] = ",", ["header"] = true, ["missing_values"] = "empty cell", ["quoting"] = "RFC 4180" },
                ["filters"] = filters,
                ["filter_combination"] = info.FilterMatchAny ? "any (OR)" : "all (AND)",
                ["sort"] = sort,
                ["edits_applied"] = true,
                ["unsaved_edits"] = info.Edits.Unsaved,
                ["columns"] = columnsJson,
                ["note"] = "Exported from the current view of Nanum CSV Viewer: filters, sort, cell edits, inserted/deleted rows and added/deleted columns are applied. Types are inferred by the app; values are raw text (currency/percent symbols are kept).",
            };
            string schemaTmp = schemaPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(schemaTmp, schema.ToJsonString(SchemaJson), new UTF8Encoding(false), ct);
                File.Move(schemaTmp, schemaPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(schemaTmp)) File.Delete(schemaTmp);
            }

            long bytes = new FileInfo(csvPath).Length;
            var json = new JsonObject
            {
                ["csv_path"] = csvPath,
                ["schema_path"] = schemaPath,
                ["output_folder"] = outputFolder,
                ["rows"] = written.Rows,
                ["columns"] = cols.Count,
                ["bytes"] = bytes,
                ["replaced_existing"] = replaced,
                ["filters_active"] = info.Filters.Count > 0,
                ["read_with"] = "pd.read_csv(csv_path, encoding='utf-8')  # types and filters are described in schema_path",
            };
            if (!sharesValues)
                json["data_policy_note"] = "The data policy is SummaryOnly: in Python print aggregates only (counts, means, model summaries) — never raw rows, row-level values or identifiers. The app cannot enforce this for script output.";
            else
                json["data_policy_note"] = $"The data policy is {options.DataPolicy}; script output is read by the model, so still print only what the question needs.";
            return Reply(
                $"Exported {written.Rows:N0} row(s) × {cols.Count} column(s) to {csvPath} (schema: {schemaPath}). No cell values are included in this result.",
                json);
        }

        private sealed record ExportWritten(long Rows, IReadOnlyList<ColumnValueType> Types);

        private static ExportWritten WriteViewCsv(AgentViewData data, IReadOnlyList<int> cols, string[] outNames, string path, CancellationToken token)
        {
            long rows = 0;
            var types = cols.Select(c => c < data.Types.Count ? data.Types[c] : ColumnValueType.String).ToList();
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                using var w = new StreamWriter(fs, new UTF8Encoding(false), 1 << 16) { NewLine = "\n" };
                bool single = cols.Count == 1;
                for (int i = 0; i < outNames.Length; i++)
                {
                    if (i > 0) w.Write(',');
                    WriteCsvField(w, outNames[i], forceQuoteEmpty: single);
                }
                w.Write('\n');

                var snapshot = data.Rows;
                int n = snapshot.Count;
                for (int r = 0; r < n; r++)
                {
                    if ((r & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                    string[] fields = snapshot[r];
                    for (int i = 0; i < cols.Count; i++)
                    {
                        if (i > 0) w.Write(',');
                        int c = cols[i];
                        WriteCsvField(w, c < fields.Length ? fields[c] : "", forceQuoteEmpty: single);
                    }
                    w.Write('\n');
                    rows++;
                }
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { /* 정리 실패는 무시: 원래 오류가 중요하다 */ }
                throw;
            }
            return new ExportWritten(rows, types);
        }

        private static void WriteCsvField(TextWriter w, string value, bool forceQuoteEmpty)
        {
            bool needQuote = value.Length == 0 ? forceQuoteEmpty : value.AsSpan().IndexOfAny(",\"\r\n") >= 0;
            if (!needQuote) { w.Write(value); return; }
            w.Write('"');
            w.Write(value.Replace("\"", "\"\""));
            w.Write('"');
        }

        private static string DtypeHint(ColumnValueType t) => t switch
        {
            ColumnValueType.Integer => "Int64",
            ColumnValueType.Float or ColumnValueType.Scientific => "float64",
            ColumnValueType.Currency or ColumnValueType.Percent => "string (has currency/percent symbols; strip before converting to float)",
            ColumnValueType.Date or ColumnValueType.DateTime => "datetime64[ns] (convert with pd.to_datetime)",
            ColumnValueType.Time => "string (time of day)",
            ColumnValueType.Boolean => "boolean",
            ColumnValueType.Categorical or ColumnValueType.Ordinal => "category",
            ColumnValueType.Empty => "object (all empty)",
            _ => "string",
        };

        // ------------------------------------------------------------------ csv.show_markdown / csv.show_image

        /// <summary>
        /// 모델이 준 경로를 허용 폴더(결과 폴더, 열린 파일의 폴더) 안의 허용 확장자 파일로 확인한다.
        /// 상대 경로는 결과 폴더 기준. 실행 파일 같은 것은 확장자 목록에서 걸러진다.
        /// </summary>
        private string ResolveShowPath(AgentDocumentInfo info, string requested, IReadOnlyCollection<string> extensions, string what, long maxBytes)
        {
            if (requested.IndexOf('\0') >= 0) throw new AgentToolException("Invalid path.");
            string openPath = OpenFilePath(info) ?? "";
            string outputFolder = _host.AnalysisFolder is { Length: > 0 } hostFolder
                ? hostFolder
                : AgentWorkspace.ComputeOutputFolder(string.IsNullOrEmpty(openPath) ? null : openPath);

            string? full;
            try
            {
                full = AgentWorkspace.ResolveInside(outputFolder, requested);
                if (full is null && Path.IsPathRooted(requested) && !string.IsNullOrEmpty(info.Directory))
                    full = AgentWorkspace.ResolveInside(info.Directory, requested);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new AgentToolException($"Invalid path: {ex.Message}");
            }
            if (full is null)
                throw new AgentToolException($"Refused: the file must be inside the analysis output folder ({outputFolder}) or the data file's folder. Write it there and pass a path relative to the output folder.");

            string ext = Path.GetExtension(full).ToLowerInvariant();
            if (!extensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                throw new AgentToolException($"Unsupported {what} file type '{ext}'. Allowed: {string.Join(", ", extensions)}.");
            if (!File.Exists(full))
                throw new AgentToolException($"File not found: {full}. Write it first (relative paths resolve against {outputFolder}).");
            long size = new FileInfo(full).Length;
            if (size > maxBytes)
                throw new AgentToolException($"The file is too large to show ({size / (1024 * 1024):N0} MB; limit {maxBytes / (1024 * 1024):N0} MB).");
            return full;
        }

        private HostToolResult ShowMarkdown(ToolArgs args)
        {
            string requested = args.ReqString("path");
            var info = RequireOpen();
            string full = ResolveShowPath(info, requested, MarkdownExtensions, "Markdown", MaxMarkdownBytes);
            var shown = _host.ShowMarkdown(full);
            if (!shown.Ok) throw new AgentToolException(shown.Message);
            return Reply($"Opened {Path.GetFileName(full)} in the Markdown viewer for the user.",
                new JsonObject { ["path"] = full, ["viewer"] = "markdown", ["message"] = shown.Message });
        }

        private HostToolResult ShowImage(ToolArgs args)
        {
            string requested = args.ReqString("path");
            string? caption = args.OptString("caption");
            bool inline = args.OptBool("inline") ?? true;
            if (caption is { Length: > 300 }) throw new AgentToolException("'caption' is too long (max 300 characters).");
            var info = RequireOpen();
            string[] allowed = ImageExtensions.Append(PdfExtension).ToArray();
            string full = ResolveShowPath(info, requested, allowed, "image", MaxImageBytes);
            bool pdf = string.Equals(Path.GetExtension(full), PdfExtension, StringComparison.OrdinalIgnoreCase);

            var shown = _host.ShowImage(full);
            if (!shown.Ok) throw new AgentToolException(shown.Message);

            var json = new JsonObject
            {
                ["path"] = full,
                ["viewer"] = pdf ? "system PDF viewer" : "image viewer",
                ["message"] = shown.Message,
            };
            string tail = "";
            if (!pdf && inline)
            {
                bool posted = _host.PostInlineImage(full, string.IsNullOrWhiteSpace(caption) ? null : caption.Trim());
                json["inline_preview"] = posted;
                if (!posted) tail = " (The chat preview was not posted: the file is outside the output folder or the chat panel is not available; the viewer window is open.)";
            }
            else if (pdf) json["inline_preview"] = false;
            return Reply((pdf ? $"Opened {Path.GetFileName(full)} with the system PDF viewer (the image viewer cannot render PDF)." : $"Opened {Path.GetFileName(full)} in the image viewer for the user.") + tail, json);
        }
    }
}
