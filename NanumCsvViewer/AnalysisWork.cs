using NanumCsvViewer.Csv;

namespace NanumCsvViewer;

/// <summary>Worker-only analysis input/output. Never owns or accesses UI controls.</summary>
internal sealed class AnalysisWork(List<string[]> rows, string[] columnLabels,
    CancellationToken cancellation, List<(string[] Fields, long SourceRow)>? sourceRows = null)
{
    public List<string[]> Rows { get; } = rows;
    public List<(string[] Fields, long SourceRow)> SourceRows { get; } = sourceRows ?? new();
    public CancellationToken Cancellation { get; } = cancellation;
    public AnalysisReport? Report { get; private set; }
    public string ColumnLabel(int column) => columnLabels[column];
    public void SetResult(string title, string body) => Report = new(title, body, null, null);
    public void SetChartResult(string title, string body, ChartKind kind, int[]? columns)
        => Report = new(title, body, kind, columns);
}

internal sealed record AnalysisReport(string Title, string Body, ChartKind? Chart, int[]? Columns);
