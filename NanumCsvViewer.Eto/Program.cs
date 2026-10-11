using System.Text;
using Eto.Forms;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Eto;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length >= 2 && args[0] == "--self-check")
{
    string log = Path.Combine(Path.GetTempPath(), "ncv-eto-check.log");
    void Mark(string step) => File.AppendAllText(log, step + Environment.NewLine);
    File.WriteAllText(log, "");
    var doc = VirtualCsvDocument.Open(args[1]);
    doc.RunIndexingAsync(new Progress<IndexProgress>(), CancellationToken.None).GetAwaiter().GetResult();
    Mark("indexed");
    int code = 1;
    new Application(Eto.Platforms.WinForms).Invoke(() =>
    {
        try
        {
            var form = new ViewerForm();
            Mark("attach");
            form.AttachIndexed(doc);
            string state = form.CheckState();
            Mark(state);
            code = state == "3|2|1|끝" ? 0 : 3;
        }
        catch (Exception ex)
        {
            Mark(ex.ToString());
            code = 4;
        }
    });
    Console.WriteLine(File.ReadAllText(log).Trim());
    return code;
}

new Application(Eto.Platforms.WinForms).Run(new ViewerForm());
return 0;
