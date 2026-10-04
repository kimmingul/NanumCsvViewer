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
                lines, ct, ApprovalKind.DataEdit);
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
                lines, ct, ApprovalKind.DataEdit);
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

        /// <summary>"position"/"to" 인자 해석 보조: 정수(1-based) 또는 컬럼 이름 또는 키워드. 이름이 정수·키워드보다 우선한다.</summary>
        private static (int? Number, int? NameIndex, string? Keyword) ParsePlace(string[] names, string text, string argument, params string[] keywords)
        {
            string key = text.Trim();
            if (key.Length == 0) throw new AgentToolException($"'{argument}' must not be empty.");
            int byName = Array.FindIndex(names, n => string.Equals(n, key, StringComparison.Ordinal));
            if (byName < 0)
            {
                int ci = Array.FindAll(names, n => string.Equals(n.Trim(), key, StringComparison.OrdinalIgnoreCase)).Length;
                if (ci == 1) byName = Array.FindIndex(names, n => string.Equals(n.Trim(), key, StringComparison.OrdinalIgnoreCase));
            }
            if (byName >= 0) return (null, byName, null);
            foreach (string k in keywords)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return (null, null, k);
            if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)) return (n, null, null);
            string kw = keywords.Length > 0 ? ", " + string.Join(" / ", keywords.Select(k => "\"" + k + "\"")) : "";
            throw new AgentToolException($"Unknown position '{text}' for '{argument}': give a column name{kw} or a 1-based column number. Columns: {string.Join(", ", names.Take(30))}{(names.Length > 30 ? ", …" : "")}");
        }

        // ------------------------------------------------------------------ csv.add_column

        private async Task<HostToolResult> AddColumnAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            string name = args.ReqString("name").Trim();
            string? fill = args.OptString("fill");
            string? positionArg = args.OptScalarAsString("position");
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

            // position: 0-based index the new column has afterwards (names.Length = end).
            int position = names.Length;
            if (!string.IsNullOrWhiteSpace(positionArg))
            {
                var place = ParsePlace(names, positionArg, "position", "end", "start");
                if (place.NameIndex is int ni) position = ni;
                else if (place.Keyword is not null) position = string.Equals(place.Keyword, "start", StringComparison.Ordinal) ? 0 : names.Length;
                else
                {
                    int n = place.Number!.Value;
                    if (n < 1 || n > names.Length + 1)
                        throw new AgentToolException($"'position' {n} is out of range: the table has {names.Length} column(s), so use 1..{names.Length + 1} (or a column name).");
                    position = n - 1;
                }
            }
            bool atEnd = position == names.Length;
            string whereEn = atEnd ? $"at the end (column {position + 1})" : $"before '{names[position].Trim()}' as column {position + 1}";
            string whereKo = atEnd ? $"맨 끝(컬럼 {position + 1})에" : $"'{names[position].Trim()}' 앞 컬럼 {position + 1}번 자리에";

            var lines = new List<string>
            {
                "+ " + L($"column '{name}' {whereEn}", $"컬럼 '{name}'을(를) {whereKo} 추가"),
                hasFill
                    ? "+ " + L($"every row filled with: {ToolJson.OneLine(fill!, EditCard.ValueWidth)}", $"모든 행 값: {ToolJson.OneLine(fill!, EditCard.ValueWidth)}")
                    : "  " + L("cells start empty", "셀은 비어 있음"),
            };
            if (!atEnd) lines.Add("  " + L($"Columns {position + 1}–{names.Length} shift right.", $"컬럼 {position + 1}–{names.Length}번은 오른쪽으로 밀립니다."));
            bool ok = await approvals.ApproveAsync(
                L($"Add column '{name}' to {info.FileName}", $"{info.FileName}에 컬럼 '{name}' 추가"),
                L("+1 column · one undo step (Ctrl+Z); the original file is not changed; included when edits are saved",
                  "+1컬럼 · 되돌리기 1단계(Ctrl+Z), 원본 파일은 바뀌지 않음, 편집 저장 시 포함"),
                lines, ct, ApprovalKind.DataEdit);
            if (!ok) throw new AgentToolException("The user did not approve adding the column. Nothing was changed.");

            var fresh = Names(RequireReady());
            if (!fresh.SequenceEqual(names, StringComparer.Ordinal))
                throw new AgentToolException("The columns changed while waiting for approval. Nothing was changed; re-read csv.info and retry.");

            string description = AgentEditTag.Prefix + L($"add column '{name}'", $"컬럼 '{name}' 추가");
            var result = atEnd
                ? _host.AddColumn(name, hasFill ? fill : null, description)
                : _host.InsertColumn(name, position, hasFill ? fill : null, description);
            var json = new JsonObject
            {
                ["column"] = result.Name,
                ["column_index"] = result.Column,
                ["column_count"] = result.ColumnCount,
                ["filled_with_constant"] = hasFill,
                ["undo_steps_added"] = 1,
                ["edits"] = EditStateJson(result.State),
                ["note"] = (atEnd ? "Appended at the end" : $"Inserted as column {result.Column + 1}; later columns shifted right (re-read csv.info)")
                    + "; set individual cells with csv.edit_cells (column name as given). csv.undo reverts this step.",
            };
            return Reply($"Added column '{result.Name}' as column {result.Column + 1} ({result.ColumnCount} columns now), one undo step.", json);
        }

        // ------------------------------------------------------------------ csv.move_column

        private async Task<HostToolResult> MoveColumnAsync(ToolArgs args, IAgentApprovals approvals, CancellationToken ct)
        {
            string columnArg = args.ReqString("column");
            string toArg = args.OptScalarAsString("to") ?? throw new AgentToolException("'to' is required.");
            var info = RequireReady();
            var names = Names(info);
            int from = ColumnNames.Resolve(names, columnArg, "column");
            if (names.Length < 2) throw new AgentToolException("Refused: the table has only one column; there is nothing to reorder.");

            // to: 이동 후 0-based 인덱스.
            int to;
            var place = ParsePlace(names, toArg, "to", "end", "start");
            if (place.NameIndex is int ni) to = ni > from ? ni - 1 : ni;   // 그 컬럼 "앞"으로
            else if (place.Keyword is not null) to = string.Equals(place.Keyword, "start", StringComparison.Ordinal) ? 0 : names.Length - 1;
            else
            {
                int n = place.Number!.Value;
                if (n < 1 || n > names.Length)
                    throw new AgentToolException($"'to' {n} is out of range: the table has {names.Length} column(s), so use 1..{names.Length} (or a column name / \"end\" / \"start\").");
                to = n - 1;
            }
            string name = names[from];
            if (to == from)
                throw new AgentToolException($"Column '{name.Trim()}' is already at column {from + 1}; nothing to move.");

            var after = names.ToList();
            after.RemoveAt(from);
            after.Insert(to, name);
            string Order(IEnumerable<string> l) => string.Join(" | ", l.Take(12).Select(h => h.Trim().Length == 0 ? "(blank)" : h.Trim())) + (names.Length > 12 ? " | …" : "");
            var lines = new List<string>
            {
                "  " + L($"column '{name.Trim()}': {from + 1} → {to + 1}", $"컬럼 '{name.Trim()}': {from + 1}번 → {to + 1}번"),
                "- " + Order(names),
                "+ " + Order(after),
                "  " + L("Only the display order changes (no data is edited); filters, sort and formatting rules follow the column.",
                         "표시 순서만 바뀝니다(데이터 편집 아님). 필터·정렬·서식 규칙은 컬럼을 따라갑니다."),
            };
            bool ok = await approvals.ApproveAsync(
                L($"Move column '{name.Trim()}' in {info.FileName}", $"{info.FileName}의 컬럼 '{name.Trim()}' 이동"),
                L("column order · one undo step (Ctrl+Z); the original file is not changed; saved files use the new order",
                  "컬럼 순서 · 되돌리기 1단계(Ctrl+Z), 원본 파일은 바뀌지 않음, 저장 파일에 새 순서 반영"),
                lines, ct, ApprovalKind.DataEdit);
            if (!ok) throw new AgentToolException("The user did not approve moving the column. Nothing was changed.");

            var fresh = Names(RequireReady());
            if (!fresh.SequenceEqual(names, StringComparer.Ordinal))
                throw new AgentToolException("The columns changed while waiting for approval. Nothing was changed; re-read csv.info and retry.");

            var result = _host.MoveColumn(from, to, AgentEditTag.Prefix + L($"move column '{name.Trim()}'", $"컬럼 '{name.Trim()}' 이동"));
            var json = new JsonObject
            {
                ["column"] = result.Name,
                ["from_index"] = from,
                ["column_index"] = result.Column,
                ["column_count"] = result.ColumnCount,
                ["undo_steps_added"] = 1,
                ["edits"] = EditStateJson(result.State),
                ["note"] = "Display order changed only; positions of the columns between the old and new place shifted (re-read csv.info). csv.undo reverts this step.",
            };
            return Reply($"Moved column '{result.Name}' from {from + 1} to {to + 1}, one undo step.", json);
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
                lines, ct, ApprovalKind.DataEdit);
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
