namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>
    /// omp에 등록하는 csv.* 도구 정의. 설명은 omp 시스템 프롬프트에 인라인되므로 짧게 유지한다.
    /// 스키마는 전부 additionalProperties:false(엄격)이다.
    /// </summary>
    internal static class ToolDefinitions
    {
        public const string Info = "csv.info";
        public const string ColumnStats = "csv.column_stats";
        public const string GetRows = "csv.get_rows";
        public const string SetFilter = "csv.set_filter";
        public const string ClearFilter = "csv.clear_filter";
        public const string Sort = "csv.sort";
        public const string Goto = "csv.goto";
        public const string RunAnalysis = "csv.run_analysis";
        public const string QualityScan = "csv.quality_scan";
        public const string EditCells = "csv.edit_cells";
        public const string Undo = "csv.undo";
        public const string SaveEditsAs = "csv.save_edits_as";

        private const string NoArgs = """{"type":"object","properties":{},"additionalProperties":false}""";

        public static readonly IReadOnlyList<HostToolDefinition> All = new[]
        {
            new HostToolDefinition(Info,
                "File, sheet, row/column counts, column names and inferred types, active filter/sort, edit state, data policy of the open CSV window.",
                NoArgs),

            new HostToolDefinition(ColumnStats,
                "Per-column missing, distinct and numeric summary (mean, sd, quartiles) over the current view. Aggregates only; top values need a data policy that allows rows.",
                """
                {"type":"object","properties":{
                "columns":{"type":"array","items":{"type":"string"},"maxItems":40,"description":"Column names. Default: first 40 columns."},
                "top_values":{"type":"integer","minimum":0,"maximum":20,"description":"Most frequent values per column (raw values; default 0)."}
                },"additionalProperties":false}
                """),

            new HostToolDefinition(GetRows,
                "Read raw rows of the current view. Subject to the user's data policy (may be refused or need approval; capped).",
                """
                {"type":"object","properties":{
                "from":{"type":"integer","minimum":1,"description":"First view row, 1-based. Default 1."},
                "count":{"type":"integer","minimum":1,"description":"Rows to read (capped by policy). Default 20."},
                "columns":{"type":"array","items":{"type":"string"},"maxItems":60,"description":"Column names. Default: all (first 60)."}
                },"additionalProperties":false}
                """),

            new HostToolDefinition(SetFilter,
                "Filter the grid with an expression, e.g. age > 30 AND city = \"Seoul\"; [col_a] >= [col_b] compares columns. Visible to the user.",
                """
                {"type":"object","properties":{
                "expression":{"type":"string","description":"Syntax: column op value; op is = != < <= > >= contains startswith endswith; combine with AND, OR and parentheses; quote text values; [col] on the right compares columns."},
                "mode":{"type":"string","enum":["replace","and"],"description":"replace (default) clears every existing filter first; and narrows the current view."}
                },"required":["expression"],"additionalProperties":false}
                """),

            new HostToolDefinition(ClearFilter,
                "Remove all filters (and the sort) so the grid shows every row again.",
                NoArgs),

            new HostToolDefinition(Sort,
                "Sort the current view by up to 5 columns in priority order. Empty keys clears the sort.",
                """
                {"type":"object","properties":{
                "keys":{"type":"array","maxItems":5,"items":{"type":"object","properties":{
                  "column":{"type":"string"},
                  "order":{"type":"string","enum":["asc","desc"],"description":"Default asc."}
                },"required":["column"],"additionalProperties":false}}
                },"required":["keys"],"additionalProperties":false}
                """),

            new HostToolDefinition(Goto,
                "Move the grid cursor to a row (the number shown in the row header, as in get_rows) and/or column.",
                """
                {"type":"object","properties":{
                "row":{"type":"integer","minimum":1,"description":"Row number as shown in the row header."},
                "column":{"type":"string"}
                },"additionalProperties":false}
                """),

            new HostToolDefinition(RunAnalysis,
                "Analyze the current view (filter first to subset); opens the result window and returns JSON (coefficients, tests, fit, n used/dropped). kind: describe | glm (OLS + Type II ANOVA) | ancova | glzm | logistic. Formula: y ~ x + C(group) + a:b — wrap categorical predictors in C(); [odd name].",
                """
                {"type":"object","properties":{
                "kind":{"type":"string","enum":["describe","glm","ancova","glzm","logistic"]},
                "formula":{"type":"string","description":"glm/glzm/logistic: response ~ terms."},
                "columns":{"type":"array","items":{"type":"string"},"maxItems":40,"description":"describe: columns (default numeric ones)."},
                "group_by":{"type":"string","description":"describe: split by this column."},
                "dependent":{"type":"string","description":"ancova: numeric response."},
                "factors":{"type":"array","items":{"type":"string"},"maxItems":6,"description":"ancova: categorical factors."},
                "covariates":{"type":"array","items":{"type":"string"},"maxItems":20,"description":"ancova: numeric covariates."},
                "interactions":{"type":"string","enum":["none","two_way","all"],"description":"ancova factor interactions (default none)."},
                "family":{"type":"string","enum":["gaussian","binomial","poisson","gamma"],"description":"glzm family (default gaussian)."},
                "link":{"type":"string","enum":["identity","log","inverse","logit","probit","cloglog","sqrt"],"description":"glzm link (default canonical)."},
                "event_level":{"type":"string","description":"binomial/logistic: value counted as the event (default: second sorted level, or 1)."},
                "offset":{"type":"string","description":"glzm: offset column."},
                "exposure":{"type":"string","description":"glzm: exposure column (adds ln)."},
                "var_weights":{"type":"string","description":"glzm: variance weights column (not binomial)."},
                "freq_weights":{"type":"string","description":"glzm: frequency weights column."},
                "trials":{"type":"string","description":"glzm binomial: trials column (response = successes)."},
                "show_window":{"type":"boolean","description":"Open the result window (default true)."}
                },"required":["kind"],"additionalProperties":false}
                """),

            new HostToolDefinition(QualityScan,
                "Run the data-quality scan (missingness, type conformance, duplicates, outliers, ...) and show it in the quality panel; returns the findings.",
                """
                {"type":"object","properties":{
                "min_severity":{"type":"string","enum":["info","warning","critical"],"description":"Default info."},
                "max_findings":{"type":"integer","minimum":1,"maximum":100,"description":"Default 40."}
                },"additionalProperties":false}
                """),

            new HostToolDefinition(EditCells,
                "Change cell values after the user approves a - old / + new card. Stored in the edit overlay as ONE undo step; the original file is untouched and sheet edit mode is not needed. Rows are numbered as in the row header.",
                """
                {"type":"object","properties":{
                "edits":{"type":"array","minItems":1,"maxItems":500,"items":{"type":"object","properties":{
                  "row":{"type":"integer","minimum":1},
                  "column":{"type":"string"},
                  "value":{"type":"string","description":"New cell text, stored exactly as given (\"\" clears the cell)."}
                },"required":["row","column","value"],"additionalProperties":false}}
                },"required":["edits"],"additionalProperties":false}
                """),

            new HostToolDefinition(Undo,
                "Undo the latest edit step if it was made by csv.edit_cells (the user's own edits are never undone by the agent).",
                NoArgs),

            new HostToolDefinition(SaveEditsAs,
                "Save the edited table to a NEW file (.csv, .tsv, .txt or .xlsx) after the user approves. Never overwrites the source file.",
                """
                {"type":"object","properties":{
                "path":{"type":"string","description":"Target path; relative paths resolve against the source file's folder."},
                "overwrite":{"type":"boolean","description":"Allow replacing an existing file other than the source (default false)."}
                },"required":["path"],"additionalProperties":false}
                """),
        };
    }
}
