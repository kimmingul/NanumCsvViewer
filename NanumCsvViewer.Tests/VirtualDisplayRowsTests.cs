using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests;

public sealed class VirtualDisplayRowsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nanumcsv_rows_" + Guid.NewGuid().ToString("N") + ".csv");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* 무시 */ }
    }

    [Fact]
    public async Task Count_and_ends_do_not_enumerate_or_read_the_middle()
    {
        var body = new StringBuilder("id,city\n");
        for (int i = 0; i < 5_000; i++) body.Append(i).Append(",서울\n");
        File.WriteAllText(_path, body.ToString(), new UTF8Encoding(false));
        using var doc = VirtualCsvDocument.Open(_path);
        await doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None);
        var rows = new VirtualDisplayRows(doc);

        Assert.Equal(5_000, rows.Count);
        Assert.Equal(0, rows.IndexReads);
        Assert.Equal("0", rows[0][0]);
        Assert.Equal("4999", rows[4999][0]);
        Assert.Equal(2, rows.IndexReads);
        Assert.Equal(0, rows.EnumerationAttempts);
        Assert.Throws<NotSupportedException>(() => rows.GetEnumerator());
        Assert.Equal(1, rows.EnumerationAttempts);
    }
}
