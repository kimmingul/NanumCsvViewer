using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using NanumCsvViewer.Csv;

// 사용법:
//   gen   <path> [gb]   : 약 gb GB 크기의 테스트 CSV 생성(기본 1GB)
//   index <path>        : Open + 인덱싱 시간 측정(2회: cold/warm)
//   icon  <path.ico>    : 앱 아이콘(.ico, 멀티 해상도) 생성
//   advstats <new-path> [rows] : 고급 통계(이슈 #27) 엔진 실측용 CSV 생성 후 각 분석 시간 측정(기본 500만 행)
if (args.Length < 2)
{
    Console.WriteLine("usage: gen <path> [gb] | index <path> | icon <path.ico> | cardinality <new-path> [rows] [padding-chars] | advstats <new-path> [rows]");
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
else if (mode == "icon")
{
    GenerateIcon(path);
    Console.WriteLine($"icon → {path}  ({new FileInfo(path).Length} bytes)");
}
else if (mode == "cardinality")
{
    int rowCount = args.Length > 2 ? int.Parse(args[2]) : 2_100_000;
    string padding = args.Length > 3 ? new string('x', int.Parse(args[3])) : "";
    using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew), new UTF8Encoding(false)))
    {
        writer.WriteLine("id,row,column,value,payload");
        for (int i = 0; i < rowCount; i++) writer.WriteLine($"{i},r{i % 20000:D5},c{i % 20000:D5},2,{padding}");
    }
    var watch = Stopwatch.StartNew();
    using var doc = VirtualCsvDocument.Open(path);
    long openMs = watch.ElapsedMilliseconds;
    await doc.RunIndexingAsync(new Progress<IndexProgress>(), default);
    Console.WriteLine($"index: bytes={new FileInfo(path).Length} rows={doc.DataRowsAvailable} open={openMs}ms total={watch.ElapsedMilliseconds}ms");
    var rows = doc.SnapshotViewRows();
    watch.Restart();
    var result = CsvAnalytics.PivotTable(rows, new[] { 1 }, new[] { 2 }, 3, AggregationFunction.Count);
    if (result.Values.Values.Sum() != rowCount) throw new Exception("Pivot lost rows");
    Console.WriteLine($"pivot: {watch.ElapsedMilliseconds}ms groups={result.Values.Count} total={result.Values.Values.Sum()}");
    watch.Restart();
    try
    {
        doc.DistinctValues(0, false, default, 100_000, 8_000_000);
        if (rowCount > 100_000) throw new Exception("Candidate budget was not enforced");
    }
    catch (DistinctValueLimitException) { Console.WriteLine($"distinct budget: handled in {watch.ElapsedMilliseconds}ms"); }
    watch.Restart();
    string last = (rowCount - 1).ToString();
    await doc.ApplyFilterAsync(row => row[0] == last, null, default);
    if (doc.DisplayRowCount != 1 || doc.GetDisplayRow(0)[0] != last) throw new Exception("Tail row filter failed");
    Console.WriteLine($"filter tail row: {watch.ElapsedMilliseconds}ms matched={doc.DisplayRowCount}");
    watch.Restart();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
    try
    {
        CsvAnalytics.PivotTable(rows, new[] { 1 }, new[] { 2 }, 3, AggregationFunction.Median, cancellation: cancellation.Token);
        throw new Exception("Cancellation did not interrupt the pivot");
    }
    catch (OperationCanceledException) { Console.WriteLine($"pivot cancellation: {watch.ElapsedMilliseconds}ms"); }
    Console.WriteLine($"peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MiB");
}
else if (mode == "advstats")
{
    // 고급 통계(이슈 #27) 수용 기준 실측: 약 500MB CSV에서 각 엔진을 UI와 같은 입력 경로(뷰 지연 목록)로 실행.
    int rowCount = args.Length > 2 ? int.Parse(args[2]) : 5_000_000;
    var rng = new Random(20260930);
    string[] levels = { "A", "B", "C", "D", "E" };
    using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew), new UTF8Encoding(false)))
    {
        writer.WriteLine("id,x1,x2,x3,x4,g,y,b,cls,t1,t2,t3,note");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < rowCount; i++)
        {
            double x1 = rng.NextDouble() * 10, x2 = rng.NextDouble() * 5, x3 = rng.NextDouble(), x4 = rng.NextDouble() * 100;
            int g = rng.Next(5);
            double y = 2 + 0.5 * x1 - 0.3 * x2 + 1.5 * x3 + 0.01 * x4 + g * 0.4 + Normal(rng);
            int b = rng.NextDouble() < 1 / (1 + Math.Exp(-(-2 + 0.3 * x1 + 0.2 * g))) ? 1 : 0;
            int cls = x1 + Normal(rng) < 3.3 ? 0 : x1 + Normal(rng) < 6.6 ? 1 : 2;
            double t1 = 50 + Normal(rng) * 5, t2 = t1 + 1 + Normal(rng), t3 = t1 + 2 + Normal(rng) * 1.5;
            writer.WriteLine(string.Create(ci, $"{i},{x1:F4},{x2:F4},{x3:F4},{x4:F3},{levels[g]},{y:F4},{b},{cls},{t1:F3},{t2:F3},{t3:F3},visit-{i % 997}"));
        }
    }
    var watch = Stopwatch.StartNew();
    using var doc = VirtualCsvDocument.Open(path);
    await doc.RunIndexingAsync(new Progress<IndexProgress>(), default);
    Console.WriteLine($"index: bytes={new FileInfo(path).Length:N0} rows={doc.DataRowsAvailable:N0} {watch.ElapsedMilliseconds}ms mode={(doc.InMemory ? "RAM" : "Disk")}");
    var rows = doc.SnapshotViewRows();
    var headers = doc.Header;
    NanumCsvViewer.Stats.VariableKind KindOf(int c) => c is 5 or 12 ? NanumCsvViewer.Stats.VariableKind.Categorical : NanumCsvViewer.Stats.VariableKind.Numeric;
    var numericFeatures = new[] { 1, 2, 3, 4 };

    void Time(string name, Func<string> run)
    {
        GC.Collect();
        var sw = Stopwatch.StartNew();
        try { string info = run(); Console.WriteLine($"{name,-22} {sw.Elapsed.TotalSeconds,7:F1}s  {info}"); }
        catch (Exception ex) { Console.WriteLine($"{name,-22} {sw.Elapsed.TotalSeconds,7:F1}s  FAILED {ex.GetType().Name}: {ex.Message}"); }
    }

    Time("GLM (OLS + Type II)", () =>
    {
        var dm = NanumCsvViewer.Stats.DesignMatrixBuilder.Build(rows, headers, NanumCsvViewer.Stats.ModelFormula.Parse("y ~ x1 + x2 + x3 + x4 + C(g)"), KindOf);
        var fit = NanumCsvViewer.Stats.LinearModel.Fit(dm);
        var anova = NanumCsvViewer.Stats.LinearModel.TypeII(dm, fit);
        return $"n={fit.N:N0} R²={fit.RSquared:F4} terms={anova.Count}";
    });
    Time("Logistic (IRLS)", () =>
    {
        var dm = NanumCsvViewer.Stats.DesignMatrixBuilder.Build(rows, headers, NanumCsvViewer.Stats.ModelFormula.Parse("b ~ x1 + C(g)"), KindOf,
            new NanumCsvViewer.Stats.DesignMatrixOptions { Response = NanumCsvViewer.Stats.ResponseKind.Binary });
        var fit = NanumCsvViewer.Stats.GeneralizedLinearModel.Fit(dm, NanumCsvViewer.Stats.GlmFamily.Binomial, NanumCsvViewer.Stats.GlmLink.Logit, logisticExtras: true);
        return $"iterations={fit.Iterations} converged={fit.Converged}";
    });
    Time("Mann-Whitney U", () => $"p={NanumCsvViewer.Stats.NonparametricTests.MannWhitney(rows, 6, 7, null, null).PAsymptotic:G4}");
    Time("Kruskal-Wallis", () => $"p={NanumCsvViewer.Stats.NonparametricTests.KruskalWallis(rows, 6, 5).P:G4}");
    Time("Friedman", () => $"p={NanumCsvViewer.Stats.NonparametricTests.Friedman(rows, new[] { 9, 10, 11 }, new[] { "t1", "t2", "t3" }).P:G4}");
    Time("RM-ANOVA", () => $"F={NanumCsvViewer.Stats.RepeatedMeasuresAnova.Fit(rows, new[] { 9, 10, 11 }, new[] { "t1", "t2", "t3" }).F:G6}");
    Time("Feature ranking", () =>
        $"features={NanumCsvViewer.Stats.FeatureRanking.Rank(rows, headers, new[] { 1, 2, 3, 4, 5 }, KindOf, 8, NanumCsvViewer.Stats.TargetKind.Categorical).Ranked.Count}");
    NanumCsvViewer.Stats.FeatureMatrix? fm = null;
    Time("Feature matrix", () =>
    {
        fm = NanumCsvViewer.Stats.FeatureMatrixBuilder.Build(rows, headers, numericFeatures, KindOf, 8, NanumCsvViewer.Stats.TargetKind.Categorical);
        return $"n={fm.RowCount:N0}";
    });
    Time("PCA", () => $"λ1={NanumCsvViewer.Stats.PrincipalComponents.Fit(fm!.X, fm.FeatureNames, NanumCsvViewer.Stats.PcaScale.Correlation, 4).Eigenvalues[0]:G4}");
    Time("LDA (holdout)", () => $"acc={NanumCsvViewer.Stats.LinearDiscriminant.Evaluate(fm!.X, fm.ClassLabels!, 3, fm.ClassNames!, NanumCsvViewer.Stats.LdaSplitKind.Holdout, 5, 0.3, 1, true).Metrics.Accuracy:F4}");
    Time("Naive Bayes (holdout)", () => $"acc={NanumCsvViewer.Stats.NaiveBayesClassifier.Evaluate(fm!.X, fm.ClassLabels!, 3, NanumCsvViewer.Stats.FeatureGroups.FromMatrix(fm.SourceColumns, fm.FeatureNames)).Evaluation.Metrics.Accuracy:F4}");
    Time("K-means (k=5)", () => $"BSS/TSS={NanumCsvViewer.Stats.KMeansClustering.Fit(fm!.X, new NanumCsvViewer.Stats.KMeansOptions { K = 5, Restarts = 3 }).BetweenTotalRatio:F4}");
    Time("KNN (holdout)", () => $"acc={NanumCsvViewer.Stats.KnnClassifier.Evaluate(fm!.X, fm.ClassLabels!, 3).Metrics.Accuracy:F4}");
    // Phase 2 (이슈 #27)
    var eval = new NanumCsvViewer.Stats.ClassifierOptions();
    Time("LMM (997 groups)", () =>
    {
        var r = NanumCsvViewer.Stats.MixedModel.Fit(rows, headers, NanumCsvViewer.Stats.ModelFormula.Parse("y ~ x1 + x2"), KindOf, 12, Array.Empty<int>());
        return $"converged={r.Converged}";
    });
    Time("Kaplan-Meier+logrank", () => $"done={NanumCsvViewer.Stats.KaplanMeierAnalysis.FromRows(rows, 9, 7, null, 5) is not null}");
    Time("Cox (Efron)", () => $"converged={NanumCsvViewer.Stats.CoxRegression.FromFormula(rows, headers, NanumCsvViewer.Stats.ModelFormula.Parse("t1 ~ x1 + x2 + C(g)"), 7, null, KindOf).Converged}");
    Time("Decision tree", () => $"done={NanumCsvViewer.Stats.DecisionTree.EvaluateClassification(fm!.X, fm.ClassLabels!, 3, eval) is not null}");
    Time("Random forest", () => $"done={NanumCsvViewer.Stats.RandomForest.EvaluateClassification(fm!.X, fm.ClassLabels!, 3, eval) is not null}");
    Time("SVM (RBF, capped)", () => $"done={NanumCsvViewer.Stats.SupportVectorMachine.Evaluate(fm!.X, fm.ClassLabels!, 3, eval) is not null}");
    Time("Gradient boosting", () => $"done={NanumCsvViewer.Stats.GradientBoosting.Evaluate(fm!.X, fm.ClassLabels!, 3) is not null}");
    Console.WriteLine($"peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MiB");

    static double Normal(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
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

// 멀티 해상도 .ico 생성: 파란 타일 + 흰 시트(스프레드시트/CSV) + 초록 헤더 + 그리드선.
static void GenerateIcon(string path)
{
    int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
    var blobs = new List<byte[]>();
    foreach (int s in sizes)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            DrawIconTile(g, s);
        }
        // 호환성: 256만 PNG, 그 외는 32bpp BMP(DIB) — 레거시 GDI+ 디코더도 읽음.
        if (s >= 256)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            blobs.Add(ms.ToArray());
            bmp.Dispose();
        }
        else
        {
            blobs.Add(BmpDib(bmp)); // bmp는 내부에서 Dispose
        }
    }

    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    bw.Write((short)0);            // reserved
    bw.Write((short)1);            // type = icon
    bw.Write((short)sizes.Length); // image count
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int sz = sizes[i];
        bw.Write((byte)(sz >= 256 ? 0 : sz)); // width  (0 = 256)
        bw.Write((byte)(sz >= 256 ? 0 : sz)); // height
        bw.Write((byte)0);                     // palette
        bw.Write((byte)0);                     // reserved
        bw.Write((short)1);                    // planes
        bw.Write((short)32);                   // bpp
        bw.Write(blobs[i].Length);             // bytes
        bw.Write(offset);                      // offset
        offset += blobs[i].Length;
    }
    foreach (var b in blobs) bw.Write(b);
}

// 32bpp 아이콘 DIB: BITMAPINFOHEADER(높이 2배) + XOR(BGRA, 상하반전) + AND 마스크(전부 0, 알파 사용).
static byte[] BmpDib(Bitmap bmp)
{
    int s = bmp.Width;
    var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    int stride = data.Stride; // 32bpp → s*4 (패딩 없음)
    var xor = new byte[s * 4 * s];
    var row = new byte[s * 4];
    for (int y = 0; y < s; y++)
    {
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * stride, row, 0, s * 4);
        Array.Copy(row, 0, xor, (s - 1 - y) * s * 4, s * 4); // 상하 반전(bottom-up)
    }
    bmp.UnlockBits(data);
    bmp.Dispose();

    int andStride = ((s + 31) / 32) * 4;
    var andMask = new byte[andStride * s]; // 전부 0

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write(40);            // biSize
    bw.Write(s);             // biWidth
    bw.Write(s * 2);         // biHeight (XOR + AND)
    bw.Write((short)1);      // biPlanes
    bw.Write((short)32);     // biBitCount
    bw.Write(0);             // biCompression = BI_RGB
    bw.Write(0);             // biSizeImage
    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); // ppm/clr
    bw.Write(xor);
    bw.Write(andMask);
    bw.Flush();
    return ms.ToArray();
}

static GraphicsPath Rounded(RectangleF r, float radius)
{
    float d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.Left, r.Top, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static void DrawIconTile(Graphics g, int s)
{
    float m = s * 0.055f;
    var bg = new RectangleF(m, m, s - 2 * m, s - 2 * m);
    using (var bgPath = Rounded(bg, s * 0.22f))
    using (var grad = new LinearGradientBrush(
        new RectangleF(0, 0, s, s),
        Color.FromArgb(58, 140, 225), Color.FromArgb(20, 82, 160), 55f))
    {
        g.FillPath(grad, bgPath);
    }

    // 흰 시트
    float pad = s * 0.19f;
    var sheet = new RectangleF(bg.Left + pad, bg.Top + pad, bg.Width - 2 * pad, bg.Height - 2 * pad);
    using var sheetPath = Rounded(sheet, Math.Max(1f, s * 0.045f));
    using (var white = new SolidBrush(Color.White)) g.FillPath(white, sheetPath);

    // 초록 헤더 행(시트 모양으로 클립)
    float headerH = sheet.Height * 0.27f;
    g.SetClip(sheetPath);
    using (var head = new SolidBrush(Color.FromArgb(40, 160, 95)))
        g.FillRectangle(head, sheet.Left, sheet.Top, sheet.Width, headerH);
    g.ResetClip();

    // 그리드선
    float lw = Math.Max(1f, s * 0.012f);
    using (var pen = new Pen(Color.FromArgb(170, 150, 152, 160), lw))
    {
        float c1 = sheet.Left + sheet.Width / 3f, c2 = sheet.Left + 2 * sheet.Width / 3f;
        g.SetClip(sheetPath);
        g.DrawLine(pen, c1, sheet.Top, c1, sheet.Bottom);
        g.DrawLine(pen, c2, sheet.Top, c2, sheet.Bottom);
        float r1 = sheet.Top + headerH;
        float rowH = (sheet.Bottom - r1) / 3f;
        g.DrawLine(pen, sheet.Left, r1 + rowH, sheet.Right, r1 + rowH);
        g.DrawLine(pen, sheet.Left, r1 + 2 * rowH, sheet.Right, r1 + 2 * rowH);
        g.ResetClip();
    }

    // 시트 테두리
    using (var border = new Pen(Color.FromArgb(120, 130, 140), lw))
        g.DrawPath(border, sheetPath);
}
