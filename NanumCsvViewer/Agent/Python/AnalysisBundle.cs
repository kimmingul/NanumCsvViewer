using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NanumCsvViewer.Agent.Python
{
    /// <param name="AnalysisFolder">분석 결과 폴더(스크립트·보고서·그림이 있는 곳).</param>
    /// <param name="Destination">내보낼 폴더, 또는 .zip 파일 경로(확장자로 구분).</param>
    /// <param name="IncludeData">true면 데이터 파일(data\ 폴더·표 형식 파일·schema.json)도 넣는다. 기본 false(개인정보 보호).</param>
    /// <param name="LockText">관리 환경의 requirements.lock 본문. null이면 lock 없이 내보내고 README가 그 사실을 적는다.</param>
    /// <param name="PythonVersion">lock을 만든 Python 버전(README용). 모르면 null.</param>
    internal sealed record AnalysisBundleRequest(
        string AnalysisFolder, string Destination, bool IncludeData, string? LockText, string? PythonVersion, string AppVersion, DateTime? UtcNow = null);

    /// <param name="Files">내보낸 파일(상대 경로, README·lock 포함).</param>
    /// <param name="ExcludedData">데이터라서 뺀 파일 수(IncludeData=false일 때).</param>
    internal sealed record AnalysisBundleResult(string Destination, bool IsZip, IReadOnlyList<string> Files, int ExcludedData, int Scripts, int Reports, int Figures, int DataFiles);

    /// <summary>
    /// Python 분석 재현 패키지: 분석 폴더의 스크립트(*.py)·보고서(*.md)·그림, 환경 lock, README(환경 만들기·다시 실행·필요한 데이터 파일)를
    /// 폴더 또는 zip으로 복사한다. 폴더 안의 상대 경로를 그대로 유지해 스크립트가 쓰는 상대 경로(data\…)가 그대로 동작한다.
    /// 데이터는 기본으로 제외한다(IncludeData로만 포함).
    /// </summary>
    internal static class AnalysisBundle
    {
        private static readonly HashSet<string> ScriptExt = new(StringComparer.OrdinalIgnoreCase) { ".py" };
        private static readonly HashSet<string> ReportExt = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown" };
        private static readonly HashSet<string> FigureExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".pdf" };
        private static readonly HashSet<string> DataExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".csv", ".tsv", ".txt", ".xlsx", ".xls", ".xlsm", ".parquet", ".feather", ".sav", ".zsav", ".dta", ".sas7bdat", ".xpt", ".json", ".jsonl", ".pkl", ".pickle", ".db", ".duckdb", ".sqlite", ".ncvws",
        };
        private static readonly string[] SkipDirectories = { ".omp", ".venv", "venv", "__pycache__", ".git", ".ipynb_checkpoints", "node_modules" };

        public static string DefaultName(string analysisFolder) =>
            (Path.GetFileName(Path.TrimEndingDirectorySeparator(analysisFolder)) is { Length: > 0 } n ? n : "analysis") + "_python_bundle";

        private enum Kind { Skip, Script, Report, Figure, Data }

        private static Kind Classify(string relative)
        {
            string ext = Path.GetExtension(relative);
            string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            bool underData = relative.Contains(Path.DirectorySeparatorChar) && first.Equals("data", StringComparison.OrdinalIgnoreCase);
            if (ext.Equals(".pyc", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) return Kind.Skip;
            if (underData) return Kind.Data;
            if (ScriptExt.Contains(ext)) return Kind.Script;
            if (ReportExt.Contains(ext)) return Kind.Report;
            if (FigureExt.Contains(ext)) return Kind.Figure;
            if (DataExt.Contains(ext)) return Kind.Data;
            return Kind.Skip;
        }

        /// <summary>분석 폴더를 걸어 (상대 경로, 분류) 목록을 만든다. 환경·캐시 폴더는 들어가지 않는다.</summary>
        private static List<(string Full, string Relative, Kind Kind)> Scan(string folder)
        {
            var found = new List<(string, string, Kind)>();
            var root = new DirectoryInfo(folder);
            void Walk(DirectoryInfo dir)
            {
                foreach (var f in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    string rel = Path.GetRelativePath(folder, f.FullName);
                    var kind = Classify(rel);
                    if (kind != Kind.Skip) found.Add((f.FullName, rel, kind));
                }
                foreach (var d in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (SkipDirectories.Contains(d.Name, StringComparer.OrdinalIgnoreCase)) continue;
                    Walk(d);
                }
            }
            Walk(root);
            return found;
        }

        public static AnalysisBundleResult Export(AnalysisBundleRequest req)
        {
            if (!Directory.Exists(req.AnalysisFolder)) throw new DirectoryNotFoundException(req.AnalysisFolder);
            bool zip = req.Destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var all = Scan(req.AnalysisFolder);
            var copy = all.Where(f => f.Kind != Kind.Data || req.IncludeData).ToList();
            int excluded = all.Count - copy.Count;
            var dataExpected = all.Where(f => f.Kind == Kind.Data).ToList();

            string readme = BuildReadme(req, copy, dataExpected);
            var files = copy.Select(f => f.Relative).ToList();
            if (req.LockText != null) files.Add("requirements.lock");
            files.Add("README.md");

            if (zip)
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(req.Destination));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = req.Destination + ".tmp";
                try
                {
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    using (var za = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        foreach (var f in copy) za.CreateEntryFromFile(f.Full, f.Relative.Replace('\\', '/'), CompressionLevel.Optimal);
                        if (req.LockText != null) AddText(za, "requirements.lock", req.LockText);
                        AddText(za, "README.md", readme);
                    }
                    File.Move(tmp, req.Destination, overwrite: true);
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }
            else
            {
                Directory.CreateDirectory(req.Destination);
                foreach (var f in copy)
                {
                    string target = Path.Combine(req.Destination, f.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f.Full, target, overwrite: true);
                }
                if (req.LockText != null) File.WriteAllText(Path.Combine(req.Destination, "requirements.lock"), req.LockText, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(req.Destination, "README.md"), readme, new UTF8Encoding(false));
            }

            return new AnalysisBundleResult(req.Destination, zip, files, excluded,
                copy.Count(f => f.Kind == Kind.Script), copy.Count(f => f.Kind == Kind.Report), copy.Count(f => f.Kind == Kind.Figure), copy.Count(f => f.Kind == Kind.Data));
        }

        private static void AddText(ZipArchive za, string name, string text)
        {
            var e = za.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(text);
        }

        /// <summary>data\*.schema.json에서 열 이름·형을 읽는다(값은 없다). 못 읽으면 null.</summary>
        private static string? ColumnSummary(string schemaPath)
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(schemaPath)) is not JsonObject o || o["columns"] is not JsonArray cols) return null;
                return string.Join(", ", cols.OfType<JsonObject>().Select(c => $"{(string?)c["name"]} ({(string?)c["type"]})"));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
        }

        private static string BuildReadme(AnalysisBundleRequest req, List<(string Full, string Relative, Kind Kind)> copied,
            List<(string Full, string Relative, Kind Kind)> dataAll)
        {
            var now = req.UtcNow ?? DateTime.UtcNow;
            var scripts = copied.Where(f => f.Kind == Kind.Script).Select(f => f.Relative.Replace('\\', '/')).ToList();
            var reports = copied.Where(f => f.Kind == Kind.Report).Select(f => f.Relative.Replace('\\', '/')).ToList();
            var figures = copied.Where(f => f.Kind == Kind.Figure).Select(f => f.Relative.Replace('\\', '/')).ToList();
            var dataFiles = dataAll.Where(f => !f.Relative.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase)).ToList();
            string py = req.PythonVersion is { Length: > 0 } v ? v : "3.10-3.13";
            string pyShort = Version.TryParse(req.PythonVersion, out var pv) ? $"{pv.Major}.{pv.Minor}" : "3.12";

            var sb = new StringBuilder();
            sb.AppendLine("# Python analysis bundle / Python 분석 재현 패키지");
            sb.AppendLine();
            sb.AppendLine($"Created by Nanum CSV Viewer {req.AppVersion} on {now:yyyy-MM-dd HH:mm} UTC from `{Path.GetFileName(Path.TrimEndingDirectorySeparator(req.AnalysisFolder))}`.");
            sb.AppendLine($"만든 프로그램: 나눔 CSV 뷰어 {req.AppVersion} ({now:yyyy-MM-dd HH:mm} UTC).");
            sb.AppendLine();
            sb.AppendLine("## 1. Recreate the environment / 환경 만들기");
            sb.AppendLine();
            if (req.LockText != null)
            {
                sb.AppendLine($"Python {py} was used. Windows (PowerShell or cmd), in this folder:");
                sb.AppendLine($"분석은 Python {py}로 실행되었습니다. 이 폴더에서(Windows):");
                sb.AppendLine();
                sb.AppendLine("```bat");
                sb.AppendLine($"py -{pyShort} -m venv .venv");
                sb.AppendLine(@".venv\Scripts\python -m pip install --only-binary=:all: -r requirements.lock");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("`requirements.lock` is the exact `pip freeze` of the analysis environment (all versions pinned). / `requirements.lock`은 분석 환경의 `pip freeze` 그대로입니다(모든 버전 고정).");
            }
            else
            {
                sb.AppendLine("No managed analysis environment existed when this bundle was made, so there is **no `requirements.lock`**: the exact package versions are unknown. " +
                              "Import the packages the scripts use (pandas, numpy, scipy, statsmodels, matplotlib …) at current versions and re-check the results.");
                sb.AppendLine("이 패키지를 만들 때 앱 관리 분석 환경이 없어 **`requirements.lock`이 없습니다**(정확한 패키지 버전을 알 수 없음). 스크립트가 쓰는 패키지를 설치해 결과를 다시 확인하세요.");
            }
            sb.AppendLine();
            sb.AppendLine("## 2. Data files / 데이터 파일");
            sb.AppendLine();
            if (req.IncludeData)
            {
                sb.AppendLine("**This bundle includes the data files** (`data\\` and table files). Treat it as sensitive; do not share it if the data contain personal or patient information.");
                sb.AppendLine("**이 패키지에는 데이터 파일이 들어 있습니다**(`data\\`와 표 파일). 개인·환자 정보가 있다면 공유하지 마세요.");
            }
            else
            {
                sb.AppendLine("Data files were **not** included (privacy). Put the files below back into the same relative paths before running the scripts.");
                sb.AppendLine("데이터 파일은 개인정보 보호를 위해 **포함하지 않았습니다**. 스크립트를 실행하기 전에 아래 파일을 같은 상대 경로에 다시 넣으세요.");
            }
            sb.AppendLine();
            if (dataFiles.Count == 0) sb.AppendLine("- (none found in the analysis folder / 분석 폴더에 데이터 파일 없음)");
            foreach (var d in dataFiles)
            {
                string rel = d.Relative.Replace('\\', '/');
                string schema = Path.ChangeExtension(d.Full, null) + ".schema.json";
                string? cols = File.Exists(schema) ? ColumnSummary(schema) : null;
                sb.AppendLine($"- `{rel}`" + (cols != null ? $" — columns / 열: {cols}" : ""));
            }
            sb.AppendLine();
            sb.AppendLine("## 3. Run / 다시 실행");
            sb.AppendLine();
            if (scripts.Count == 0) sb.AppendLine("- (no scripts / 스크립트 없음)");
            else
            {
                sb.AppendLine("From this folder (file name order is not necessarily the run order; check the report). / 이 폴더에서 실행하세요(파일 이름 순서가 실행 순서와 같지는 않습니다. 보고서를 확인하세요):");
                sb.AppendLine();
                sb.AppendLine("```bat");
                foreach (string s in scripts) sb.AppendLine($@".venv\Scripts\python {s.Replace('/', '\\')}");
                sb.AppendLine("```");
            }
            sb.AppendLine();
            sb.AppendLine("## 4. Contents / 내용");
            sb.AppendLine();
            sb.AppendLine($"- Scripts / 스크립트 ({scripts.Count}): " + (scripts.Count == 0 ? "-" : string.Join(", ", scripts.Select(s => $"`{s}`"))));
            sb.AppendLine($"- Reports / 보고서 ({reports.Count}): " + (reports.Count == 0 ? "-" : string.Join(", ", reports.Select(s => $"`{s}`"))));
            sb.AppendLine($"- Figures / 그림 ({figures.Count}): " + (figures.Count == 0 ? "-" : string.Join(", ", figures.Select(s => $"`{s}`"))));
            sb.AppendLine();
            sb.AppendLine("Results may differ slightly with another Python or package version, or on another CPU (floating-point, random seeds). / 다른 Python·패키지 버전이나 CPU에서는 결과가 조금 다를 수 있습니다(부동소수점·난수).");
            return sb.ToString();
        }
    }
}
