using System.Text;

namespace NanumCsvViewer.Agent.Python
{
    internal enum LspStatus
    {
        /// <summary>도구 환경 준비 중(이번 omp 실행에는 아직 반영되지 않을 수 있음).</summary>
        Preparing,
        /// <summary>lsp.json이 써져 있고 omp가 읽는다.</summary>
        Ready,
        /// <summary>준비하지 못함(Python 없음·설치 실패 등).</summary>
        Unavailable,
    }

    /// <summary>가이드의 "Local Python analysis" 절을 만드는 입력.</summary>
    internal sealed record PythonGuideContext(
        string? DataFile,
        string OutputFolder,
        PythonInterpreter? Interpreter,
        IReadOnlyList<string> Packages,
        LspStatus Lsp,
        AgentDataPolicy Policy);

    /// <summary>
    /// 로컬 Python 분석이 켜졌을 때만 omp 시스템 프롬프트(가이드)에 덧붙이는 절. 파일 경로·결과 폴더·개인정보 정책에 따른 출력 규칙처럼
    /// 실행 때마다 달라지는 내용을 담는다(고정 내용은 AgentGuide.md). 도구 이름: csv.export_view, csv.show_markdown, csv.show_image.
    /// </summary>
    internal static class PythonGuide
    {
        public static string Build(PythonGuideContext c)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## Local Python analysis (the user turned this on)");
            sb.AppendLine();
            sb.AppendLine("Beyond the `csv.*` analysis tools you may analyse the data with **local Python** through omp's `eval` tool (language `py`). " +
                          "It runs on the user's PC in the analysis folder below. Use it for what the app lacks (custom models, plots, tests, reshaping); " +
                          "use `csv.run_analysis` and `csv.column_stats` first when they already answer the question.");
            sb.AppendLine();
            sb.AppendLine("- **Data file** (open in the app): " + (string.IsNullOrEmpty(c.DataFile) ? "(none open)" : "`" + c.DataFile + "`") +
                          ". Never modify or overwrite it, and do not read it directly to get around the data policy.");
            sb.AppendLine($"- **Analysis folder** (your working directory; Python runs here too): `{c.OutputFolder}`. " +
                          "Write every script, table, figure and report here (relative paths are fine). Do not write anywhere else.");
            sb.AppendLine("- **Get the data**: call `csv.export_view`. It writes the *current view* (filter, sort, hidden columns and pending edits applied) " +
                          "as a CSV (+ schema) under the analysis folder and returns the path; then load it with pandas in `eval`.");
            if (c.Interpreter != null)
            {
                string pk = c.Packages.Count > 0 ? string.Join(", ", c.Packages) : "none of pandas/numpy/matplotlib/scipy/statsmodels/scikit-learn";
                sb.AppendLine($"- **Interpreter**: `{c.Interpreter.Path}` (Python {c.Interpreter.Version}). Installed: {pk}. " +
                              "Install a missing package with `%pip install <name>` only when needed, and say what you installed.");
            }
            else
            {
                sb.AppendLine("- **Interpreter**: none was found on this PC, so `eval` will fail. Tell the user to install Python 3.10+ (python.org) and restart the agent; do not try to work around it.");
            }
            sb.AppendLine("- **Show results to the user**: matplotlib figures in `eval` are captured, but the user only sees what you put in front of them: " +
                          "save a figure as PNG/SVG in the analysis folder and call `csv.show_image`, or write a Markdown report (tables, code blocks, " +
                          "images as `![title](figure.png)` with relative paths) and call `csv.show_markdown`. Both viewers let the user save the file. " +
                          "Prefer one clear report over many small windows, and name linked files simply (letters, digits, `_`, `-`; no spaces).");
            sb.AppendLine(c.Lsp switch
            {
                LspStatus.Ready => "- **Code diagnostics**: basedpyright and ruff are active for `.py` files in the analysis folder. After writing a script, call the `lsp` tool (action `diagnostics`) on it and fix the errors before running it; the write result alone may not show them.",
                LspStatus.Preparing => "- **Code diagnostics**: the Python language servers are still being set up (one-time) and may not report in this conversation.",
                _ => "- **Code diagnostics**: not available in this conversation (Python tools could not be prepared); check your code carefully.",
            });
            sb.AppendLine("- **Approval**: the first `eval` of a conversation asks the user; later `eval` calls in the same conversation run without asking. " +
                          "Other tools (`bash`, `write`, ...) still ask each time.");
            sb.AppendLine("- Keep each cell focused and its printed output short (about 60 lines at most); save big tables to files instead of printing them.");
            sb.AppendLine();
            sb.AppendLine("### Privacy rule for Python output");
            sb.AppendLine();
            switch (c.Policy)
            {
                case AgentDataPolicy.SummaryOnly:
                    sb.AppendLine("The data policy is **summary only**: raw rows must not reach you. Everything a script prints is read by you, and the app " +
                                  "**cannot enforce this for code you run**. So print aggregates and model results only (counts, means, quantiles, coefficients, " +
                                  "p-values, plot descriptions) and never raw cell values: no `print(df)`, `df.head()`, `df.sample()`, `df.to_string()`, no value " +
                                  "lists of identifying columns, no exception messages that echo cell contents. If a request cannot be answered that way, say so " +
                                  "and ask the user to change the data policy.");
                    break;
                case AgentDataPolicy.RowsWithApproval:
                    sb.AppendLine("The data policy is **rows with approval**. Printing rows from a script sends them to you without an approval card, so print " +
                                  "aggregates unless the user explicitly asked to see specific rows; then keep it to the few rows needed.");
                    break;
                default:
                    sb.AppendLine("The data policy lets row values reach you (up to a small limit). Still print only what you need, never whole tables.");
                    break;
            }
            return sb.ToString().TrimEnd();
        }
    }
}
