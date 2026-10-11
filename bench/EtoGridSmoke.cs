using System.Text;
using Eto.Forms;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.EtoGridSmoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(Path.GetTempPath(), "ncv-eto-grid-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var body = new StringBuilder("id,city\n");
            for (int i = 0; i < 20_000; i++) body.Append(i).Append(",서울\n");
            File.WriteAllText(path, body.ToString(), new UTF8Encoding(false));
            using var doc = VirtualCsvDocument.Open(path);
            doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None).GetAwaiter().GetResult();
            var rows = new VirtualDisplayRows(doc);
            int code = 1;
            new Eto.Forms.Application(Eto.Platforms.WinForms).Invoke(() =>
            {
                var grid = new GridView { DataStore = rows, ShowHeader = false };
                grid.Columns.Add(new GridColumn { HeaderText = "id", DataCell = new TextBoxCell(0) });
                using var form = new Form { Content = grid, ClientSize = new Eto.Drawing.Size(240, 180) };
                form.Show();
                string first = ((string[])((System.Collections.IList)grid.DataStore!)[0])[0];
                string last = ((string[])((System.Collections.IList)grid.DataStore!)[19_999])[0];
                bool ok = first == "0" && last == "19999" && rows.EnumerationAttempts == 0 && rows.IndexReads < 500;
                Console.WriteLine($"first={first} last={last} indexReads={rows.IndexReads} enumerations={rows.EnumerationAttempts}");
                code = ok ? 0 : 3;
                form.Close();
            });
            return code;
        }
        finally
        {
            try { File.Delete(path); } catch { /* 무시 */ }
        }
    }
}
