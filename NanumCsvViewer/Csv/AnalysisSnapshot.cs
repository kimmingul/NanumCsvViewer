namespace NanumCsvViewer.Csv;

/// <summary>A detached, complete row snapshot for modeless analysis windows.</summary>
internal sealed record AnalysisSnapshot(List<string[]> Rows)
{
    public static AnalysisSnapshot Collect(IReadOnlyList<string[]> source, CancellationToken cancellation,
        long memoryBudgetBytes = 128L * 1024 * 1024)
    {
        if (memoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
        var rows = new List<string[]>();
        long estimatedBytes = 0;
        cancellation.ThrowIfCancellationRequested();
        for (int i = 0; i < source.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var row = source[i];
            // Array, list growth, references, string objects and UTF-16 payload.
            long bytes = 48L + row.Sum(value => 40L + value.Length * 2L);
            if (bytes > memoryBudgetBytes - estimatedBytes) throw new AnalysisMemoryLimitException();
            estimatedBytes += bytes;
            rows.Add(row);
        }
        cancellation.ThrowIfCancellationRequested();
        return new AnalysisSnapshot(rows);
    }
}

public sealed class AnalysisMemoryLimitException : InvalidOperationException
{
    public AnalysisMemoryLimitException() : base("This analysis exceeds its memory budget. Filter the data or select fewer columns and try again.") { }
}
