using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Agent
{
    // csv.format_add / format_list / format_remove / format_clear: 조건부 서식. 보기 상태일 뿐이고(원본·편집 덮개 불변)
    // 규칙을 지우면 되돌려지므로 승인 카드는 없다. 규칙 id는 앱이 정한다(cf1, cf2…).
    public sealed partial class CsvHostTools
    {
        private const string DefaultScaleMin = "#63BE7B";
        private const string DefaultScaleMax = "#F8696B";
        private static readonly Regex ColorSyntax = new("^(#[0-9A-Fa-f]{6}|#[0-9A-Fa-f]{3}|[A-Za-z]{3,30})$", RegexOptions.Compiled);

        private AgentDocumentInfo RequireOpen()
            => _host.GetInfo() ?? throw new AgentToolException("No file is open in Nanum CSV Viewer. Ask the user to open one.");

        private static string? Color(ToolArgs args, string name)
        {
            string? c = args.OptString(name)?.Trim();
            if (string.IsNullOrEmpty(c)) return null;
            if (!ColorSyntax.IsMatch(c))
                throw new AgentToolException($"'{name}' must be a theme colour (red, orange, yellow, green, blue, purple, gray), #RRGGBB (e.g. #FFE0E0) or a CSS colour name (e.g. gold); got '{c}'.");
            return c;
        }

        private async Task<HostToolResult> FormatAddAsync(ToolArgs args, CancellationToken ct)
        {
            string kindText = args.OptEnum("kind", "expression", "color_scale") ?? "expression";
            bool scale = kindText == "color_scale";
            string? expression = args.OptString("expression")?.Trim();
            string? targetText = args.OptEnum("target", "row", "cell");
            string? columnArg = args.OptString("column");
            string? name = args.OptString("name")?.Trim();
            string? back = Color(args, "back_color"), fore = Color(args, "fore_color");
            bool? boldArg = args.OptBool("bold");
            string? sMin = Color(args, "scale_min_color"), sMid = Color(args, "scale_mid_color"), sMax = Color(args, "scale_max_color");
            if (name is { Length: > 100 }) throw new AgentToolException("'name' is too long (max 100 characters).");

            var info = RequireReady();
            var names = Names(info);
            string? column = null;
            ConditionalFormatTarget target;

            if (scale)
            {
                if (!string.IsNullOrEmpty(expression) || targetText is not null || back is not null || fore is not null || boldArg is not null)
                    throw new AgentToolException("color_scale uses only 'column' and the scale_*_color arguments (not expression, target, back_color, fore_color or bold).");
                if (string.IsNullOrWhiteSpace(columnArg)) throw new AgentToolException("color_scale needs 'column' (a numeric column).");
                int c = ColumnNames.Resolve(names, columnArg, "column");
                if (!info.Columns[c].Type.IsNumeric())
                    throw new AgentToolException($"Column '{names[c]}' is {info.Columns[c].Type.DisplayName()}, not numeric; a colour scale needs a numeric column.");
                column = names[c];
                target = ConditionalFormatTarget.Cell;
                sMin ??= DefaultScaleMin;
                sMax ??= DefaultScaleMax;
                expression = "";
            }
            else
            {
                if (string.IsNullOrEmpty(expression)) throw new AgentToolException("'expression' is required for kind expression (same syntax as csv.set_filter).");
                if (expression.Length > 4000) throw new AgentToolException("The expression is too long (max 4000 characters).");
                if (sMin is not null || sMid is not null || sMax is not null)
                    throw new AgentToolException("scale_*_color applies only to kind color_scale.");
                if (back is null && fore is null && boldArg is not true)
                    throw new AgentToolException("Give at least one of back_color, fore_color or bold:true; otherwise the rule would not change how anything looks.");
                try { AdvancedFilterExpression.Compile(expression, names); }
                catch (AdvancedFilterExpressionException ex)
                {
                    throw new AgentToolException("Invalid expression: " + ex.Message + " (same syntax as csv.set_filter). Columns: " + string.Join(", ", names.Take(30)) + (names.Length > 30 ? ", …" : ""));
                }
                target = targetText == "cell" ? ConditionalFormatTarget.Cell : ConditionalFormatTarget.Row;
                if (target == ConditionalFormatTarget.Cell)
                {
                    if (string.IsNullOrWhiteSpace(columnArg)) throw new AgentToolException("target cell needs 'column' (the cell of which column gets the style).");
                    column = names[ColumnNames.Resolve(names, columnArg, "column")];
                }
                else if (!string.IsNullOrWhiteSpace(columnArg))
                    throw new AgentToolException("'column' applies only to target cell (and color_scale). Remove it, or set target:\"cell\".");
            }

            var draft = new ConditionalFormatRule(
                Id: "", Name: string.IsNullOrEmpty(name) ? (scale ? $"Color scale: {column}" : ToolJson.Clip(expression!, 60)) : name,
                Enabled: true,
                Kind: scale ? ConditionalFormatKind.ColorScale : ConditionalFormatKind.Expression,
                Expression: expression!, Target: target, Column: column,
                BackColor: back, ForeColor: fore, Bold: boldArg ?? false,
                ScaleMinColor: sMin, ScaleMidColor: sMid, ScaleMaxColor: sMax);

            var rule = _host.AddConditionalFormat(draft);

            var json = RuleJson(rule);
            string count;
            try
            {
                var m = await _host.CountConditionalFormatAsync(rule.Id, null, ct);
                json["rows_matched_in_view"] = m.RowsMatched;
                json["rows_scanned"] = m.RowsScanned;
                if (m.TimedOut > 0) json["regex_cells_timed_out"] = m.TimedOut;
                count = scale ? $"{m.RowsMatched:N0} numeric row(s) coloured" : $"{m.RowsMatched:N0} of {m.RowsScanned:N0} view rows match";
                if (!scale && m.RowsMatched == 0) json["warning"] = "No row of the current view matches the expression. Check column names and value spelling.";
            }
            catch (AgentToolException ex)
            {
                // 규칙은 이미 적용됐다. 개수만 못 셌다는 사실을 그대로 알린다.
                json["rows_matched_in_view"] = null;
                json["count_unavailable"] = ex.Message;
                count = "match count unavailable";
            }
            json["note"] = "View only: the data and edits are unchanged. Earlier rules win per style property; edited-cell amber wins over the background colour. Remove with csv.format_remove / csv.format_clear.";
            return Reply($"Added format rule {rule.Id}: {count}. The grid shows it now.", json);
        }

        private HostToolResult FormatList()
        {
            RequireOpen();
            var rules = _host.ListConditionalFormats();
            var problems = _host.ConditionalFormatProblems();
            var arr = new JsonArray();
            foreach (var r in rules)
            {
                var o = RuleJson(r);
                if (problems.TryGetValue(r.Id, out string? problem)) o["problem"] = problem + " (the rule is not applied)";
                arr.Add(o);
            }
            return Reply($"{rules.Count} conditional-format rule(s) (priority order).", new JsonObject { ["rules"] = arr });
        }

        private HostToolResult FormatRemove(ToolArgs args)
        {
            string id = args.ReqString("id").Trim();
            RequireOpen();
            if (!_host.RemoveConditionalFormat(id))
            {
                string known = string.Join(", ", _host.ListConditionalFormats().Select(r => r.Id));
                throw new AgentToolException($"No format rule with id '{id}'. Existing ids: {(known.Length == 0 ? "(none)" : known)}.");
            }
            return Reply($"Removed format rule {id}.", new JsonObject { ["removed"] = id, ["remaining"] = _host.ListConditionalFormats().Count });
        }

        private HostToolResult FormatClear()
        {
            RequireOpen();
            int n = _host.ClearConditionalFormats();
            return Reply($"Removed {n:N0} format rule(s).", new JsonObject { ["removed"] = n });
        }

        private HostToolResult FormatUndo()
        {
            RequireOpen();
            var r = _host.UndoConditionalFormat()
                ?? throw new AgentToolException("There is no format change to undo.");
            return Reply($"Undid the last format change ({r.Description}); {r.RuleCount:N0} rule(s) now.",
                new JsonObject { ["undone"] = r.Description, ["rule_count"] = r.RuleCount, ["rules"] = new JsonArray(_host.ListConditionalFormats().Select(x => (JsonNode)RuleJson(x)).ToArray()) });
        }

        private static JsonObject RuleJson(ConditionalFormatRule r)
        {
            var o = new JsonObject
            {
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["enabled"] = r.Enabled,
                ["kind"] = r.Kind == ConditionalFormatKind.ColorScale ? "color_scale" : "expression",
            };
            if (r.Kind == ConditionalFormatKind.Expression) o["expression"] = r.Expression;
            o["target"] = r.Target == ConditionalFormatTarget.Row ? "row" : "cell";
            if (r.Column is not null) o["column"] = r.Column;
            if (r.BackColor is not null) o["back_color"] = r.BackColor;
            if (r.ForeColor is not null) o["fore_color"] = r.ForeColor;
            if (r.Bold) o["bold"] = true;
            if (r.Kind == ConditionalFormatKind.ColorScale)
            {
                o["scale_min_color"] = r.ScaleMinColor;
                if (r.ScaleMidColor is not null) o["scale_mid_color"] = r.ScaleMidColor;
                o["scale_max_color"] = r.ScaleMaxColor;
            }
            return o;
        }
    }
}
