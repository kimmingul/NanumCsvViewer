using System.Text;
using NanumCsvViewer.Csv;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length < 2 || args[0] != "index")
{
    Console.WriteLine("usage: index <path>");
    return 2;
}

string path = args[1];
for (int run = 1; run <= 2; run++)
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var doc = VirtualCsvDocument.Open(path);
    long openMs = watch.ElapsedMilliseconds;
    await doc.RunIndexingAsync(new Progress<IndexProgress>(_ => { }), CancellationToken.None);
    Console.WriteLine($"run={run} rows={doc.DataRowsAvailable} cols={doc.ColumnCount} open={openMs}ms total={watch.ElapsedMilliseconds}ms mode={(doc.InMemory ? "RAM" : "Disk")}");
    if (doc.DataRowsAvailable > 0)
        Console.WriteLine($"last={string.Join(",", doc.GetDataRow(doc.DataRowsAvailable - 1))}");
}
return 0;
