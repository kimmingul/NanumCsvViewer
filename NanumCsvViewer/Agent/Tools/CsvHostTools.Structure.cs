using System.Text;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Tools;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Agent
{
    // csv.insert_rows / delete_rows / add_column / delete_column: 편집 덮개에 한 단계로 쌓이는 구조 편집.
    // 승인 카드 필수, 설명은 "AI: "로 시작해 csv.undo가 되돌릴 수 있다. 원본 파일은 쓰지 않는다.
    public sealed partial class CsvHostTools
    {
        private const int MaxInsertRows = 10_000;
        private const int MaxDeleteRows = 200_000;
        private const int MaxColumnNameChars = 255;
        private const int DeleteCardRows = 5;
        private const int DeleteCardColumns = 4;
        private const int DeleteCardCellChars = 30;
        private const int FillRowLimit = 1_000_000;

        // ------------------------------------------------------------------ csv.insert_rows

        private async Task<HostToolResult> InsertRowsAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            long? before = args.OptInt("before_row", 1, long.MaxValue);
            int count = (int)(args.OptInt("count", 1, MaxInsertRows) ?? 1);
            var info = RequireReady();
            long total = info.TotalRows;
            long at = before ?? total + 1;
            if (at > total + 1)
                throw new AgentToolException($"'before_row' is beyond the end: the table has {total:N0} rows (use {total + 1:N0} or omit it to append).");

            string where = at == total + 1
                ? L("at the end", "맨 끝에")
                : L($"before row {at:N0}", $"{at:N0}번 행 앞에");
            var lines = new List<string>
            {
                "+ " + L($"{count:N0} empty row(s) {where} (new rows {at:N0}–{at + count - 1:N0})", $"빈 행 {count:N0}개를 {where} 추가(새 행 {at:N0}–{at + count - 1:N0}번)"),
            };
            if (at <= total) lines.Add(L($"  Rows {at:N0}–{total:N0} shift down by {count:N0}.", $"  {at:N0}–{total:N0}번 행은 {count:N0}행씩 아래로 밀립니다."));
            bool ok = await approvals.ApproveAsync(
                L($"Insert {count:N0} row(s) in {info.FileName}", $"{info.FileName}에 행 {count:N0}개 삽입"),
                L($"+{count:N0} row(s) {where} · one undo step (Ctrl+Z); the original file is not changed",
                  $"+{count:N0}행 · 되돌리기 1단계(Ctrl+Z), 원본 파일은 바뀌지 않음"),
                lines, ct);
            if (!ok) throw new AgentToolException("The user did not approve inserting rows. Nothing was changed.");

            RequireReady(); // 승인 대기 중 사용자가 다른 작업을 했을 수 있다
            var result = _host.InsertRows(at, count, AgentEditTag.Prefix + L($"insert {count:N0} row(s)", $"행 {count:N0}개 삽입"));
            var json = new JsonObject
            {
                ["inserted_rows"] = result.Count,
                ["first_new_row"] = result.FirstRow,
                ["last_new_row"] = result.FirstRow + result.Count - 1,
                ["total_rows"] = result.TotalRows,
                ["undo_steps_added"] = 1,
                ["edits"] = EditStateJson(result.State),
                ["note"] = "The new rows are empty; fill them with csv.edit_cells using the row numbers above. Rows below shifted down. csv.undo reverts this step.",
            };
            return Reply($"Inserted {result.Count:N0} empty row(s) as rows {result.FirstRow:N0}–{result.FirstRow + result.Count - 1:N0} (one undo step; original file untouched).", json);
        }

        // ------------------------------------------------------------------ csv.delete_rows

        private async Task<HostToolResult> DeleteRowsAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            var rowsArg = args.OptIntArray("rows", 2000, 1, long.MaxValue);
            long? from = args.OptInt("from", 1, long.MaxValue);
            long? to = args.OptInt("to", 1, long.MaxValue);
            bool inView = args.OptBool("in_view") ?? false;
            int selectors = (rowsArg is { Count: > 0 } ? 1 : 0) + (from is not null || to is not null ? 1 : 0) + (inView ? 1 : 0);
            if (selectors != 1)
                throw new AgentToolException("Give exactly one selector: 'rows', 'from' (+ optional 'to'), or in_view:true.");
            if (to is not null && from is null) throw new AgentToolException("'to' needs 'from'.");

            var info = RequireReady();
            long total = info.TotalRows;
            List<long> rows;
            string scope;
            if (rowsArg is { Count: > 0 })
            {
                rows = rowsArg.Distinct().OrderBy(r => r).ToList();
                scope = L("listed rows", "지정한 행");
                if (rows[^1] > total) throw new AgentToolException($"Row {rows[^1]:N0} does not exist (the table has {total:N0} rows).");
            }
            else if (from is not null)
            {
                long last = to ?? from.Value;
                if (last < from) throw new AgentToolException("'to' must be >= 'from'.");
                if (last > total) throw new AgentToolException($"Row {last:N0} does not exist (the table has {total:N0} rows).");
                if (last - from.Value + 1 > MaxDeleteRows)
                    throw new AgentToolException($"Refused: more than {MaxDeleteRows:N0} rows in one call. Delete in smaller ranges.");
                rows = new List<long>((int)(last - from.Value + 1));
                for (long r = from.Value; r <= last; r++) rows.Add(r);
                scope = L($"rows {from:N0}–{last:N0}", $"{from:N0}–{last:N0}행");
            }
            else
            {
                if (info.ViewRows == 0) throw new AgentToolException("The current view has no rows (the filter matches nothing).");
                var numbers = await _host.GetViewRowNumbersAsync(MaxDeleteRows, ct);
                if (numbers.Truncated)
                    throw new AgentToolException($"Refused: the current view has more than {MaxDeleteRows:N0} rows. Narrow it with csv.set_filter or delete a range with from/to.");
                rows = numbers.Rows.Distinct().OrderBy(r => r).ToList();
                scope = L("rows of the current view", "현재 뷰의 행");
            }
            if (rows.Count == 0) throw new AgentToolException("No rows selected.");
            if (rows.Count >= total)
                throw new AgentToolException("Refused: this would delete every row of the table. Keep at least one row, or ask the user to open another file.");

            var lines = new List<string>
            {
                "- " + L($"{rows.Count:N0} row(s) ({scope}): ", $"행 {rows.Count:N0}개({scope}): ") + RowRanges(rows, 12),
            };
            if (inView && info.Filters.Count > 0)
                foreach (var f in info.Filters.Where(f => f.Kind == AgentFilterKind.Expression))
                    lines.Add(L("  Filter: ", "  필터: ") + ToolJson.OneLine(f.Description, EditCard.ValueWidth));
            lines.AddRange(DeleteCardPreview(info, rows));
            lines.Add(L($"  Later row numbers shift up; {total - rows.Count:N0} row(s) remain.", $"  뒤 행 번호가 당겨지고 {total - rows.Count:N0}행이 남습니다."));

            bool ok = await approvals.ApproveAsync(
                L($"Delete {rows.Count:N0} row(s) in {info.FileName}", $"{info.FileName}의 행 {rows.Count:N0}개 삭제"),
                L($"-{rows.Count:N0} row(s) · one undo step (Ctrl+Z); the original file is not changed; saved files omit them",
                  $"-{rows.Count:N0}행 · 되돌리기 1단계(Ctrl+Z), 원본 파일은 바뀌지 않음, 저장 파일에서 빠짐"),
                lines, ct);
            if (!ok) throw new AgentToolException("The user did not approve deleting rows. Nothing was changed.");

            RequireReady();
            var result = _host.DeleteRows(rows, AgentEditTag.Prefix + L($"delete {rows.Count:N0} row(s)", $"행 {rows.Count:N0}개 삭제"));
            var json = new JsonObject
            {
                ["deleted_rows"] = result.Count,
                ["total_rows"] = result.TotalRows,
                ["undo_steps_added"] = 1,
                ["edits"] = EditStateJson(result.State),
                ["note"] = "Row numbers after the deleted rows shifted up. The source file is unchanged; saved files omit the deleted rows. csv.undo reverts this step.",
            };
            return Reply($"Deleted {result.Count:N0} row(s) as one undo step (overlay only; original file untouched). {result.TotalRows:N0} row(s) remain.", json);
        }

        /// <summary>승인 카드용 미리보기: 처음 몇 행의 앞쪽 몇 컬럼 값(사용자에게만 보인다. 모델 결과에는 넣지 않는다).</summary>
        private IEnumerable<string> DeleteCardPreview(AgentDocumentInfo info, List<long> rows)
        {
            int cols = Math.Min(DeleteCardColumns, info.Columns.Count);
            var picks = rows.Take(DeleteCardRows).ToList();
            var cells = new List<(long, int)>();
            foreach (long r in picks) for (int c = 0; c < cols; c++) cells.Add((r, c));
            IReadOnlyList<AgentCellState> states;
            try { states = _host.GetCellStates(cells); }
            catch (AgentToolException) { return Array.Empty<string>(); }

            var lines = new List<string> { L($"  First rows (first {cols} column(s)):", $"  처음 행(앞 {cols}개 컬럼):") };
            for (int i = 0; i < picks.Count; i++)
            {
                var values = new List<string>();
                for (int c = 0; c < cols; c++)
                {
                    var s = states[i * cols + c];
                    values.Add(s.Exists ? ToolJson.OneLine(s.Current, DeleteCardCellChars) : "");
                }
                lines.Add($"- {picks[i]:N0} · " + string.Join(" | ", values));
            }
            return lines;
        }

        /// <summary>정렬된 행 번호를 "3, 5, 9–12, … (+N)" 꼴로.</summary>
        internal static string RowRanges(IReadOnlyList<long> sorted, int maxParts)
        {
            var sb = new StringBuilder();
            int parts = 0, i = 0;
            while (i < sorted.Count && parts < maxParts)
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
                if (parts > 0) sb.Append(", ");
                sb.Append(i == j ? sorted[i].ToString("N0") : $"{sorted[i]:N0}–{sorted[j]:N0}");
                parts++;
                i = j + 1;
            }
            if (i < sorted.Count) sb.Append($", … (+{sorted.Count - i:N0})");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ csv.add_column

        private async Task<HostToolResult> AddColumnAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            string name = args.ReqString("name").Trim();
            string? fill = args.OptString("fill");
            if (name.Length == 0) throw new AgentToolException("'name' must not be empty.");
            if (name.Length > MaxColumnNameChars) throw new AgentToolException($"The column name is too long (max {MaxColumnNameChars} characters).");
            if (fill is { Length: > MaxEditValueChars }) throw new AgentToolException($"'fill' is too long (max {MaxEditValueChars:N0} characters).");

            var info = RequireReady();
            var names = Names(info);
            if (names.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                throw new AgentToolException($"A column named '{name}' already exists (names are compared case-insensitively). Choose another name.");
            bool hasFill = !string.IsNullOrEmpty(fill);
            if (hasFill && info.TotalRows > FillRowLimit)
                throw new AgentToolException($"Refused: 'fill' works up to {FillRowLimit:N0} rows (the table has {info.TotalRows:N0}). Add the column without fill.");

            var lines = new List<string>
            {
                "+ " + L($"column '{name}' at the end (column {names.Length + 1})", $"컬럼 '{name}'을(를) 맨 끝(컬럼 {names.Length + 1})에 추가"),
                hasFill
                    ? "+ " + L($"every row filled with: {ToolJson.OneLine(fill!, EditCard.ValueWidth)}", $"모든 행 값: {ToolJson.OneLine(fill!, EditCard.ValueWidth)}")
                    : "  " + L("cells start empty", "셀은 비어 있음"),
            };
            bool ok = await approvals.ApproveAsync(
                L($"Add column '{name}' to {info.FileName}", $"{info.FileName}에 컬럼 '{name}' 추가"),
                L("+1 column · one undo step (Ctrl+Z); the original file is not changed; included when edits are saved",
                  "+1컬럼 · 되돌리기 1단계(Ctrl+Z), 원본 파일은 바뀌지 않음, 편집 저장 시 포함"),
                lines, ct);
            if (!ok) throw new AgentToolException("The user did not approve adding the column. Nothing was changed.");

            RequireReady();
            var result = _host.AddColumn(name, hasFill ? fill : null, AgentEditTag.Prefix + L($"add column '{name}'", $"컬럼 '{name}' 추가"));
            var json = new JsonObject
            {
                ["column"] = result.Name,
                ["column_index"] = result.Column,
                ["column_count"] = result.ColumnCount,
                ["filled_with_constant"] = hasFill,
                ["undo_steps_added"] = 1,
                ["edits"] = EditStateJson(result.State),
                ["note"] = "Appended at the end; set individual cells with csv.edit_cells (column name as given). csv.undo reverts this step.",
            };
            return Reply($"Added column '{result.Name}' ({result.ColumnCount} columns now), one undo step.", json);
        }

        // ------------------------------------------------------------------ csv.delete_column

        private async Task<HostToolResult> DeleteColumnAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            string columnArg = args.ReqString("column");
            var info = RequireReady();
            var names = Names(info);
            int col = ColumnNames.Resolve(names, columnArg, "column");
            if (names.Length <= 1) throw new AgentToolException("Refused: this is the only column of the table.");
            string name = names[col];
            var type = info.Columns[col].Type;

            var lines = new List<string>
            {
                "- " + L($"column '{name}' (column {col + 1}, {type.DisplayName()})", $"컬럼 '{name}'(컬럼 {col + 1}, {type.DisplayName()}) 삭제"),
                "  " + L("Filters, sort, formatting rules and analyses no longer see it; later columns shift left.", "  필터·정렬·서식 규칙·분석에서 빠지고 뒤 컬럼이 왼쪽으로 당겨집니다."),
            };
            bool ok = await approvals.ApproveAsync(
                L($"Delete column '{name}' in {info.FileName}", $"{info.FileName}의 컬럼 '{name}' 삭제"),
                L("-1 column · one undo step (Ctrl+Z); the original file is not changed; saved files omit it",
                  "-1컬럼 · 되돌리기 1단계(Ctrl+Z), 원본 파일은 바뀌지 않음, 저장 파일에서 빠짐"),
                lines, ct);
            if (!ok) throw new AgentToolException("The user did not approve deleting the column. Nothing was changed.");

            RequireReady();
            // 승인을 기다리는 동안 컬럼 순서가 바뀌었을 수 있으니 이름으로 다시 찾는다.
            var fresh = Names(_host.GetInfo() ?? throw new AgentToolException("The file was closed while waiting for approval."));
            int again = Array.FindIndex(fresh, n => string.Equals(n, name, StringComparison.Ordinal));
            if (again < 0) throw new AgentToolException($"Column '{name}' is no longer in the table. Nothing was changed.");

            var result = _host.DeleteColumn(again, AgentEditTag.Prefix + L($"delete column '{name}'", $"컬럼 '{name}' 삭제"));
            var json = new JsonObject
            {
                ["deleted_column"] = result.Name,
                ["column_count"] = result.ColumnCount,
                ["undo_steps_added"] = 1,
                ["edits"] = EditStateJson(result.State),
                ["note"] = "Column positions after the deleted one shifted left (use csv.info for the current list). The source file is unchanged; saved files omit the column. csv.undo reverts this step.",
            };
            return Reply($"Deleted column '{result.Name}' ({result.ColumnCount} columns remain), one undo step.", json);
        }
    }
}
