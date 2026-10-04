namespace NanumCsvViewer.Agent.Tools
{
    /// <summary>모델이 준 컬럼 이름을 실제 컬럼으로 해석한다. 정확 일치 → 대소문자 무시 유일 일치 → "Column&lt;N&gt;"(1-based) 순.</summary>
    internal static class ColumnNames
    {
        public static int Resolve(IReadOnlyList<string> headers, string name, string argument = "column")
        {
            string key = name.Trim();
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], key, StringComparison.Ordinal)) return i;

            int hit = -1, hits = 0;
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i].Trim(), key, StringComparison.OrdinalIgnoreCase)) { hit = i; hits++; }
            if (hits == 1) return hit;
            if (hits > 1) throw new AgentToolException($"Column name '{name}' is ambiguous (matches {hits} columns that differ only by case).");

            if (key.StartsWith("Column", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key.AsSpan(6), out int n) && n >= 1 && n <= headers.Count && string.IsNullOrEmpty(headers[n - 1]))
                return n - 1;

            throw new AgentToolException($"Unknown column '{name}' for '{argument}'. Columns: {Preview(headers)}");
        }

        public static List<int> ResolveMany(IReadOnlyList<string> headers, IEnumerable<string> names, string argument)
        {
            var result = new List<int>();
            foreach (string n in names)
            {
                int c = Resolve(headers, n, argument);
                if (result.Contains(c)) throw new AgentToolException($"Column '{headers[c]}' is listed twice in '{argument}'.");
                result.Add(c);
            }
            return result;
        }

        private static string Preview(IReadOnlyList<string> headers)
        {
            const int Max = 30;
            string list = string.Join(", ", headers.Take(Max).Select(h => h.Length == 0 ? "(blank)" : h));
            return headers.Count > Max ? $"{list}, … ({headers.Count} total; see csv.info)" : list;
        }
    }
}
