namespace NanumCsvViewer.Csv;

/// <summary>A detached, complete row snapshot for modeless analysis windows.</summary>
internal sealed record AnalysisSnapshot(List<string[]> Rows)
{
    /// <param name="columns">
    /// null = keep every cell. Otherwise only these column indexes are kept: each snapshot row has the source row's
    /// own length (so <c>col &lt; row.Length</c> checks behave as before) but every other cell is null, and only the
    /// kept cells count toward the memory estimate.
    /// </param>
    /// <param name="memoryBudgetBytes">null = <see cref="AnalysisMemoryBudget.Current"/>.</param>
    public static AnalysisSnapshot Collect(IReadOnlyList<string[]> source, CancellationToken cancellation,
        IReadOnlyCollection<int>? columns = null, long? memoryBudgetBytes = null)
    {
        long budget = memoryBudgetBytes ?? AnalysisMemoryBudget.Current;
        if (budget <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        int[]? keep = null;
        if (columns is not null)
        {
            keep = columns.Distinct().Order().ToArray();
            if (keep.Length > 0 && keep[0] < 0) throw new ArgumentOutOfRangeException(nameof(columns));
        }
        var rows = new List<string[]>();
        long estimatedBytes = 0;
        cancellation.ThrowIfCancellationRequested();
        for (int i = 0; i < source.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var row = source[i];
            long bytes = EstimateRowBytes(row, keep);
            if (bytes > budget - estimatedBytes) throw new AnalysisMemoryLimitException();
            estimatedBytes += bytes;
            rows.Add(keep is null ? row : Project(row, keep));
        }
        cancellation.ThrowIfCancellationRequested();
        return new AnalysisSnapshot(rows);
    }

    /// <summary>
    /// Bytes retained for one snapshot row: the array and its references, plus a string object and the UTF-16 payload
    /// for each kept cell. <paramref name="keep"/> is null (every cell) or a sorted, distinct column list.
    /// </summary>
    internal static long EstimateRowBytes(string[] row, int[]? keep)
    {
        if (keep is null)
        {
            long all = 48;
            foreach (var value in row) all += 40L + value.Length * 2L;
            return all;
        }
        // Array + one reference per cell; a kept cell adds its string object (40 includes the reference slot).
        long bytes = 48L + 8L * row.Length;
        foreach (int c in keep)
        {
            if (c >= row.Length) break;
            bytes += 32L + row[c].Length * 2L;
        }
        return bytes;
    }

    private static string[] Project(string[] row, int[] keep)
    {
        var copy = new string[row.Length];
        foreach (int c in keep)
        {
            if (c >= row.Length) break;
            copy[c] = row[c];
        }
        return copy;
    }
}

public sealed class AnalysisMemoryLimitException : InvalidOperationException
{
    public AnalysisMemoryLimitException() : base("This analysis exceeds its memory budget. Filter the data or select fewer columns and try again. The limit can be raised in Settings.") { }
}
