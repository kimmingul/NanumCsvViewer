namespace NanumCsvViewer.Csv;

/// <summary>Exact pivot state exceeded its estimated memory budget; no partial result is published.</summary>
public sealed class PivotMemoryLimitException : InvalidOperationException
{
    public PivotMemoryLimitException() : base("The pivot has too many groups or retained values. Filter the data, group dates, or use fewer dimensions/measures.") { }
}
