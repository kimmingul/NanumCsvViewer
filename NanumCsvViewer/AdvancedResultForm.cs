using System.Drawing;
using System.Windows.Forms;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    /// <summary>
    /// 고급 통계 결과 창. 고정폭 본문은 기존 결과 창과 같고, 보고서(HTML/Excel/PDF)와
    /// 모형이 있을 때만 모형 저장·ONNX 내보내기를 연다.
    /// </summary>
    internal sealed class AdvancedResultForm : Form
    {
        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        private readonly AdvancedReport _report;
        private readonly ReportContext _context;
        private readonly string _body;
        private readonly Font _mono = new(FontFamily.GenericMonospace, 9.5f);
        private readonly ContextMenuStrip _exportMenu;
        private ReportDocument? _document;

        public AdvancedResultForm(
            AdvancedReport report,
            ReportContext context,
            ThemePalette palette,
            Action? saveModel,
            Action? exportOnnx)
        {
            _report = report;
            _context = context;
            _body = report.Text ?? "";

            Text = report.Title ?? "";
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 640);
            MinimumSize = new Size(640, 400);
            BackColor = palette.Window;
            ForeColor = palette.Text;
            ShowInTaskbar = false;

            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = _mono,
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                BorderStyle = BorderStyle.None,
                Text = _body.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"),
            };

            var bottom = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Bottom,
                AutoSize = true,
                Padding = new Padding(6),
                BackColor = palette.Window,
            };

            var close = MakeButton(LT("Close", "닫기"), palette);
            close.DialogResult = DialogResult.OK;
            var copy = MakeButton(LT("Copy", "복사"), palette);
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(_body); }
                catch (Exception ex) { ShowError(ex.Message); }
            };
            var export = MakeButton(LT("Export Report…", "보고서 내보내기…"), palette);
            _exportMenu = new ContextMenuStrip
            {
                BackColor = palette.Surface,
                ForeColor = palette.Text,
                Renderer = new ThemeToolStripRenderer(palette),
            };
            _exportMenu.Items.Add(LT("HTML…", "HTML…"), null, (_, _) => ExportHtml());
            _exportMenu.Items.Add(LT("Excel…", "Excel…"), null, (_, _) => ExportExcel());
            _exportMenu.Items.Add(LT("PDF…", "PDF…"), null, (_, _) => ExportPdf());
            export.Click += (_, _) => _exportMenu.Show(export, new Point(0, export.Height));

            bottom.Controls.Add(close);
            bottom.Controls.Add(copy);
            bottom.Controls.Add(export);
            if (exportOnnx is not null)
            {
                var onnx = MakeButton(LT("Export ONNX…", "ONNX 내보내기…"), palette);
                onnx.Click += (_, _) => RunModelAction(exportOnnx);
                bottom.Controls.Add(onnx);
            }
            if (saveModel is not null)
            {
                var save = MakeButton(LT("Save Model…", "모형 저장…"), palette);
                save.Click += (_, _) => RunModelAction(saveModel);
                bottom.Controls.Add(save);
            }

            Controls.Add(text);
            Controls.Add(bottom);
            AcceptButton = close;
            CancelButton = close;

        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _exportMenu.Dispose();
                _mono.Dispose();
            }
            base.Dispose(disposing);
        }

        private ReportDocument Document => _document ??= ReportExport.Compose(_report, _context);

        private void ExportHtml() => Save(LT("HTML (*.html)|*.html", "HTML (*.html)|*.html"), ".html",
            path => ReportExport.WriteHtml(Document, path));

        private void ExportExcel() => Save(LT("Excel (*.xlsx)|*.xlsx", "Excel (*.xlsx)|*.xlsx"), ".xlsx",
            path => ReportExport.WriteXlsx(Document, path));

        private void ExportPdf()
        {
            if (!ReportExport.IsPdfPrinterAvailable())
            {
                OfferHtml();
                return;
            }
            Save(LT("PDF (*.pdf)|*.pdf", "PDF (*.pdf)|*.pdf"), ".pdf", path =>
            {
                try { ReportExport.WritePdf(Document, path); }
                catch (ReportPdfUnavailableException)
                {
                    OfferHtml();
                }
            });
        }

        private void OfferHtml()
        {
            var answer = MessageBox.Show(this,
                LT("Microsoft Print to PDF is not installed, so a PDF cannot be written. Export HTML instead?",
                   "Microsoft Print to PDF 프린터가 없어 PDF를 만들 수 없습니다. HTML로 내보낼까요?"),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) ExportHtml();
        }

        private void Save(string filter, string extension, Action<string> write)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = filter,
                FileName = DefaultFileName(extension),
                OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { write(dlg.FileName); }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void RunModelAction(Action action)
        {
            try { action(); }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        private void ShowError(string message)
            => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);

        private string DefaultFileName(string extension)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (char ch in _report.Title ?? "")
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            string name = sb.ToString().Trim();
            if (name.Length == 0) name = "report";
            if (name.Length > 80) name = name[..80].Trim();
            return name + extension;
        }

        private static Button MakeButton(string text, ThemePalette palette)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(80, 26),
                Margin = new Padding(4, 2, 0, 2),
                FlatStyle = FlatStyle.Flat,
                BackColor = palette.Surface,
                ForeColor = palette.Text,
            };
            button.FlatAppearance.BorderColor = palette.Border;
            return button;
        }
    }
}
