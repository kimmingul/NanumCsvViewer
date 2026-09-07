namespace NanumCsvViewer.Csv;

/// <summary>The exact candidate list exceeds the UI's memory budget. No partial list is returned.</summary>
public sealed class DistinctValueLimitException : InvalidOperationException
{
    public DistinctValueLimitException() : base("Too many distinct values for a checklist. Use a text or list filter.") { }
}
