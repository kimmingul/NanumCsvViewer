using NanumCsvViewer.Stats;

namespace NanumCsvViewer
{
    public partial class Form1
    {
        private void ShowAdvancedResult(AdvancedReport report)
        {
            var context = new ReportContext(
                FileName: string.IsNullOrEmpty(_currentPath)
                    ? LT("(unsaved)", "(저장 안 됨)")
                    : Path.GetFileName(_currentPath),
                AppVersion: AppInfo.Version,
                ViewScope: CurrentViewScope(),
                FileLabel: LT("File", "파일"),
                AnalyzedLabel: LT("Analyzed", "분석 시각"),
                VersionLabel: LT("App version", "앱 버전"),
                ScopeLabel: LT("View scope", "분석 범위"));
            bool hasModel = report.Model is ModelBundle;
            var form = new AdvancedResultForm(
                report,
                context,
                _palette,
                hasModel ? () => SaveAdvancedModel(report) : null,
                hasModel ? () => ExportOnnx(report) : null);
            form.Show(this);
        }


        /// <summary>보고서 머리말용 현재 뷰 범위. 분석 본문의 사용·제외 행과는 별개로 창에 보이는 행 수다.</summary>
        private string CurrentViewScope()
        {
            if (_doc is null) return LT("No file open", "열린 파일 없음");
            return LT(
                $"Current view · {_doc.DisplayRowCount:N0} of {_doc.DataRowsAvailable:N0} rows",
                $"현재 뷰 · {_doc.DisplayRowCount:N0} / {_doc.DataRowsAvailable:N0}행");
        }

    }
}
