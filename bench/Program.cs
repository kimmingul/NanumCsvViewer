using System.Diagnostics;
using System.Text;
using NanumCsvViewer.Csv;

// 사용법:
//   gen   <path> [gb]   : 약 gb GB 크기의 테스트 CSV 생성(기본 1GB)
//   index <path>        : Open + 인덱싱 시간 측정(2회: cold/warm)
if (args.Length < 2)
{
    Console.WriteLine("usage: gen <path> [gb] | index <path>");
    return;
}

string mode = args[0];
string path = args[1];
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (mode == "gen")
{
    double gb = args.Length > 2 ? double.Parse(args[2]) : 1.0;
    long target = (long)(gb * 1024L * 1024 * 1024);
    Generate(path, target);
    Console.WriteLine($"generated {path}  {new FileInfo(path).Length / 1024.0 / 1024:F1} MB");
}
else if (mode == "index")
{
    for (int run = 1; run <= 2; run++)
    {
        var sw = Stopwatch.StartNew();
        var doc = VirtualCsvDocument.Open(path);
        long openMs = sw.ElapsedMilliseconds;
        doc.RunIndexingAsync(new Progress<IndexProgress>(_ => { }), default).GetAwaiter().GetResult();
        sw.Stop();
        long bytes = new FileInfo(path).Length;
        double mbps = bytes / 1024.0 / 1024 / (sw.Elapsed.TotalSeconds);
        Console.WriteLine($"run{run}: rows={doc.DataRowsAvailable:N0} cols={doc.ColumnCount} " +
                          $"open={openMs}ms total={sw.ElapsedMilliseconds}ms ({mbps:F0} MB/s) mode={(doc.InMemory ? "RAM" : "Disk")}");
        doc.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

static void Generate(string path, long targetBytes)
{
    string[] cities = { "Seoul", "Busan", "Jeonju", "Daegu", "Incheon" };
    var sb = new StringBuilder();
    int row = 0;
    while (sb.Length < 1_000_000) // ~1MB 블록
    {
        row++;
        string name = (row % 3 == 0) ? "\"Lee, Min-gyeol\"" : $"User{row}";       // 따옴표+쉼표
        string note = (row % 7 == 0) ? "\"멀티\r\n라인 노트, 포함\"" : "\"메모, 값\""; // 따옴표 안 줄바꿈/쉼표
        sb.Append(row).Append(',').Append(name).Append(",user").Append(row)
          .Append("@example.com,").Append(1000 + row % 9000).Append('.').Append(row % 100)
          .Append(',').Append(note).Append(',').Append(cities[row % cities.Length]).Append('\n');
    }
    string block = sb.ToString();
    var enc = new UTF8Encoding(false);
    long blockLen = enc.GetByteCount(block);
    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
    using var w = new StreamWriter(fs, enc, 1 << 20);
    w.Write("id,name,email,amount,note,city\n");
    long written = 0;
    while (written < targetBytes) { w.Write(block); written += blockLen; }
    w.Flush();
}
