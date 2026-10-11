using System.Text;
using Eto.Forms;
using NanumCsvViewer.Eto;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
new Application().Run(new ViewerForm());
