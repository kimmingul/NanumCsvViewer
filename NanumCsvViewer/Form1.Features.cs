using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // macOS 버전에서 이식한 고급 기능(검색 모드·내보내기·이동·분석·통계·컬럼숨김·저장된 뷰·
    // 드래그드롭·클립보드·성능). Designer를 건드리지 않고 메뉴를 코드로 구성한다.
    public partial class Form1
    {
        // 인덱싱 후 계산한 컬럼 추론 타입/요약(헤더 툴팁·분석 기본값에 사용)
        private ColumnSummary[] _columnSummaries = Array.Empty<ColumnSummary>();
        private long _lastIndexMs;

        // 그리드/인스펙터 복사 (그리드 향상)
        private ToolStripMenuItem? _copyCellsMenu, _copyRowMenu, _copyColMenu;
        private Button? _inspectorCopyText, _inspectorCopyJson;

        // 헤더 타입 배지 표시 토글(보기 메뉴 + 툴바 버튼 동기화)
        private bool _showTypeBadges = true;
        private ToolStripMenuItem? _showBadgesMenu;
        private ToolStripButton? _badgeToggleButton;
        private bool _syncingBadgeToggle;
        private readonly HashSet<int> _hiddenColumns = new();
        private readonly List<string> _tempImportFiles = new();

        // 구조화 컬럼 필터(헤더 깔때기 → 범주/날짜 필터)
        private readonly ColumnFilterState _columnFilters = new();
        // 활성 조건 결합 방식: false = 모두 만족(AND), true = 하나라도 만족(OR).
        private bool _filterMatchAny;

        // 언어 전환 시 다시 라벨링하기 위한 메뉴 참조
        private ToolStripMenuItem? _exportMenu, _clipboardOpenMenu, _gotoRowMenu, _advFilterMenu,
            _columnsMenu, _saveViewMenu, _restoreViewMenu, _perfMenu, _indexCacheMenu, _analysisMenu,
            _deleteIndexOnCloseMenu, _pivotTopMenu, _pivotTableMenu, _pivotChartMenu;
        private readonly List<(ToolStripMenuItem item, string en, string ko)> _featureLabels = new();

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        // 그리드 셀 표시용 길이 제한 미리보기(전체 값은 값 표시줄·상세 패널에 보존).
        private const int MaxCellPreviewChars = 1000;
        private static string PreviewCell(string value)
            => value.Length <= MaxCellPreviewChars ? value : value.Substring(0, MaxCellPreviewChars) + " …";

        // ---------------------------------------------------------------- 메뉴 구성

        private void BuildFeatureMenus()
        {
            // File ▸ 내보내기 + 클립보드 열기 (Quit 앞에 삽입)
            int quitIdx = fileToolStripMenuItem.DropDownItems.IndexOf(quitToolStripMenuItem);
            if (quitIdx < 0) quitIdx = fileToolStripMenuItem.DropDownItems.Count;

            _exportMenu = new ToolStripMenuItem();
            _exportMenu.DropDownItems.Add(MakeItem("Export as CSV…", "CSV로 내보내기…", (_, _) => ExportView(ExportFormat.Csv)));
            _exportMenu.DropDownItems.Add(MakeItem("Export as Markdown…", "Markdown으로 내보내기…", (_, _) => ExportView(ExportFormat.Markdown)));
            _exportMenu.DropDownItems.Add(MakeItem("Export as JSON…", "JSON으로 내보내기…", (_, _) => ExportView(ExportFormat.Json)));
            _exportMenu.DropDownItems.Add(MakeItem("Export as HTML…", "HTML로 내보내기…", (_, _) => ExportView(ExportFormat.Html)));
            RegisterLabel(_exportMenu, "Export View", "현재 보기 내보내기");

            _clipboardOpenMenu = MakeItem("Open from Clipboard", "클립보드에서 열기", async (_, _) => await OpenFromClipboardAsync());

            var fileSep = new ToolStripSeparator();
            fileToolStripMenuItem.DropDownItems.Insert(quitIdx, fileSep);
            fileToolStripMenuItem.DropDownItems.Insert(quitIdx, _clipboardOpenMenu);
            fileToolStripMenuItem.DropDownItems.Insert(quitIdx, _exportMenu);

            // Edit ▸ 이동 / 고급 필터
            editToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _gotoRowMenu = MakeItem("Go to Row…", "행으로 이동…", (_, _) => GoToRow());
            _gotoRowMenu.ShortcutKeys = Keys.Control | Keys.G;
            editToolStripMenuItem.DropDownItems.Add(_gotoRowMenu);
            _advFilterMenu = MakeItem("Advanced Filter…", "고급 필터…", (_, _) => ShowAdvancedFilter());
            editToolStripMenuItem.DropDownItems.Add(_advFilterMenu);

            // View ▸ 컬럼 / 저장된 뷰 / 성능 / 인덱스 캐시
            viewToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _columnsMenu = MakeItem("Columns…", "컬럼 표시…", (_, _) => ShowColumnChooser());
            viewToolStripMenuItem.DropDownItems.Add(_columnsMenu);

            _showTypeBadges = _settings.ShowTypeBadges;
            _showBadgesMenu = new ToolStripMenuItem { CheckOnClick = true, Checked = _showTypeBadges };
            _showBadgesMenu.CheckedChanged += (_, _) => { if (!_syncingBadgeToggle) SetShowTypeBadges(_showBadgesMenu.Checked); };
            RegisterLabel(_showBadgesMenu, "Show Type Badges", "타입 배지 표시");
            viewToolStripMenuItem.DropDownItems.Add(_showBadgesMenu);

            _saveViewMenu = MakeItem("Save Current View", "현재 보기 저장", (_, _) => SaveCurrentView());
            viewToolStripMenuItem.DropDownItems.Add(_saveViewMenu);
            _restoreViewMenu = MakeItem("Restore Saved View", "저장된 보기 복원", async (_, _) => await RestoreSavedViewAsync());
            viewToolStripMenuItem.DropDownItems.Add(_restoreViewMenu);
            viewToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            _perfMenu = MakeItem("Performance Dashboard", "성능 대시보드", (_, _) => ShowPerformanceDashboard());
            viewToolStripMenuItem.DropDownItems.Add(_perfMenu);

            _indexCacheMenu = new ToolStripMenuItem();
            _indexCacheMenu.DropDownItems.Add(MakeItem("Open Index Folder", "인덱스 폴더 열기", (_, _) => OpenIndexFolder()));
            _indexCacheMenu.DropDownItems.Add(MakeItem("Clear Index Cache", "인덱스 캐시 비우기", (_, _) => ClearIndexCache()));
            _deleteIndexOnCloseMenu = new ToolStripMenuItem { CheckOnClick = true, Checked = _settings.DeleteIndexOnClose };
            _deleteIndexOnCloseMenu.CheckedChanged += (_, _) =>
            {
                _settings.DeleteIndexOnClose = _deleteIndexOnCloseMenu.Checked;
                _settings.Save();
            };
            RegisterLabel(_deleteIndexOnCloseMenu, "Delete Index Cache on Close", "닫을 때 인덱스 캐시 삭제");
            _indexCacheMenu.DropDownItems.Add(_deleteIndexOnCloseMenu);
            RegisterLabel(_indexCacheMenu, "Index Cache", "인덱스 캐시");
            viewToolStripMenuItem.DropDownItems.Add(_indexCacheMenu);

            // 새 최상위 메뉴: 분석 (Help 앞에 삽입)
            _analysisMenu = new ToolStripMenuItem();
            _analysisMenu.DropDownItems.Add(MakeItem("Numeric Distribution…", "수치 분포…", (_, _) => AnalyzeDistribution()));
            _analysisMenu.DropDownItems.Add(MakeItem("Date Histogram…", "날짜 히스토그램…", (_, _) => AnalyzeDateHistogram()));
            _analysisMenu.DropDownItems.Add(MakeItem("Find Duplicates…", "중복 찾기…", (_, _) => AnalyzeDuplicates()));
            _analysisMenu.DropDownItems.Add(MakeItem("Group By…", "그룹별 집계…", (_, _) => AnalyzeGroupBy()));
            _analysisMenu.DropDownItems.Add(new ToolStripSeparator());
            _analysisMenu.DropDownItems.Add(MakeItem("Correlation…", "상관분석…", (_, _) => AnalyzeCorrelation()));
            _analysisMenu.DropDownItems.Add(MakeItem("Independent t-test…", "독립표본 t검정…", (_, _) => AnalyzeIndependentTTest()));
            _analysisMenu.DropDownItems.Add(MakeItem("Paired t-test…", "대응표본 t검정…", (_, _) => AnalyzePairedTTest()));
            _analysisMenu.DropDownItems.Add(MakeItem("Chi-square…", "카이제곱 검정…", (_, _) => AnalyzeChiSquare()));
            RegisterLabel(_analysisMenu, "Analysis", "분석");
            int helpIdx = menuStrip1.Items.IndexOf(helpToolStripMenuItem);
            if (helpIdx < 0) helpIdx = menuStrip1.Items.Count;
            menuStrip1.Items.Insert(helpIdx, _analysisMenu);

            // 새 최상위 메뉴: 피벗 ▸ 피벗테이블 / 피벗차트
            _pivotTopMenu = new ToolStripMenuItem();
            _pivotTableMenu = MakeItem("Pivot Table…", "피벗테이블…", (_, _) => OpenPivotBuilder(chartTab: false));
            _pivotChartMenu = MakeItem("Pivot Chart…", "피벗차트…", (_, _) => OpenPivotBuilder(chartTab: true));
            _pivotTopMenu.DropDownItems.Add(_pivotTableMenu);
            _pivotTopMenu.DropDownItems.Add(_pivotChartMenu);
            RegisterLabel(_pivotTopMenu, "Pivot", "피벗");
            int pivotIdx = menuStrip1.Items.IndexOf(helpToolStripMenuItem);
            if (pivotIdx < 0) pivotIdx = menuStrip1.Items.Count;
            menuStrip1.Items.Insert(pivotIdx, _pivotTopMenu);

            // 타입 배지 토글 툴바 버튼 — 우측 정렬로 추가하면 테마 토글 버튼 왼쪽에 놓인다.
            _badgeToggleButton = new ToolStripButton
            {
                CheckOnClick = true,
                Checked = _showTypeBadges,
                Alignment = ToolStripItemAlignment.Right,
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ImageScaling = ToolStripItemImageScaling.None,
                Image = UiIcons.TypeBadge(),
                Name = "badgeToggleButton",
            };
            _badgeToggleButton.CheckedChanged += (_, _) => { if (!_syncingBadgeToggle) SetShowTypeBadges(_badgeToggleButton.Checked); };
            toolStrip1.Items.Add(_badgeToggleButton);

            // 드래그앤드롭 가져오기
            AllowDrop = true;
            DragEnter += OnFeatureDragEnter;
            DragDrop += OnFeatureDragDrop;
            grid.AllowDrop = true;
            grid.DragEnter += OnFeatureDragEnter;
            grid.DragDrop += OnFeatureDragDrop;

            // 엑셀식 다중 셀 복사: 기본 Ctrl+C는 잘린 미리보기를 복사하므로 끄고 직접 처리(전체 값).
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
            grid.KeyDown += OnGridCopyKeyDown;

            // 컨텍스트 메뉴: 선택/행/열 복사
            gridContextMenu.Items.Insert(0, new ToolStripSeparator());
            _copyColMenu = MakeItem("Copy Entire Column", "열 전체 복사", async (_, _) => await CopyCurrentColumnAsync());
            gridContextMenu.Items.Insert(0, _copyColMenu);
            _copyRowMenu = MakeItem("Copy Entire Row", "행 전체 복사", (_, _) => CopyCurrentRow());
            gridContextMenu.Items.Insert(0, _copyRowMenu);
            _copyCellsMenu = MakeItem("Copy Selection", "선택 영역 복사", (_, _) => CopySelectedCells());
            gridContextMenu.Items.Insert(0, _copyCellsMenu);

            // 인스펙터(상세 패널) 복사 버튼 — 헤더 우측에 얹음
            _inspectorCopyText = InspectorButton("TEXT", 116, (_, _) => CopyInspectorText());
            _inspectorCopyJson = InspectorButton("JSON", 60, (_, _) => CopyInspectorJson());
            outerSplit.Panel2.Controls.Add(_inspectorCopyText);
            outerSplit.Panel2.Controls.Add(_inspectorCopyJson);
            _inspectorCopyText.BringToFront();
            _inspectorCopyJson.BringToFront();

            LocalizeFeatureMenus();
        }

        private ToolStripMenuItem MakeItem(string en, string ko, EventHandler handler)
        {
            var item = new ToolStripMenuItem();
            item.Click += handler;
            RegisterLabel(item, en, ko);
            return item;
        }

        private void RegisterLabel(ToolStripMenuItem item, string en, string ko)
            => _featureLabels.Add((item, en, ko));

        // Form1.ApplyLocalization() 끝에서 호출됨(부분 클래스 훅).
        private void LocalizeFeatureMenus()
        {
            foreach (var (item, en, ko) in _featureLabels)
                item.Text = LT(en, ko);
            if (_badgeToggleButton is not null)
                _badgeToggleButton.ToolTipText = LT("Toggle type badges", "타입 배지 표시 전환");
        }

        // 보기 메뉴 항목과 툴바 버튼을 함께 토글하고, 설정 저장 + 헤더 다시 그림.
        private void SetShowTypeBadges(bool show)
        {
            _showTypeBadges = show;
            _syncingBadgeToggle = true;
            if (_showBadgesMenu is not null) _showBadgesMenu.Checked = show;
            if (_badgeToggleButton is not null) _badgeToggleButton.Checked = show;
            _syncingBadgeToggle = false;

            _settings.ShowTypeBadges = show;
            _settings.Save();
            grid.Invalidate();
        }

        // Form1.UpdateFeatureState() 끝에서 호출됨.
        private void UpdateFeatureMenuState()
        {
            bool ready = _doc is not null && _doc.IndexingComplete && !_busy;
            if (_exportMenu is not null) _exportMenu.Enabled = ready;
            if (_gotoRowMenu is not null) _gotoRowMenu.Enabled = ready;
            if (_advFilterMenu is not null) _advFilterMenu.Enabled = ready;
            if (_columnsMenu is not null) _columnsMenu.Enabled = _doc is not null && !_busy;
            if (_saveViewMenu is not null) _saveViewMenu.Enabled = ready;
            if (_restoreViewMenu is not null) _restoreViewMenu.Enabled = ready;
            if (_perfMenu is not null) _perfMenu.Enabled = _doc is not null;
            if (_analysisMenu is not null) _analysisMenu.Enabled = ready;
            if (_pivotTopMenu is not null) _pivotTopMenu.Enabled = ready;

            bool open = _doc is not null && !_busy;
            if (_copyCellsMenu is not null) _copyCellsMenu.Enabled = open;
            if (_copyRowMenu is not null) _copyRowMenu.Enabled = open;
            if (_copyColMenu is not null) _copyColMenu.Enabled = open;
            if (_inspectorCopyText is not null) _inspectorCopyText.Enabled = open;
            if (_inspectorCopyJson is not null) _inspectorCopyJson.Enabled = open;
        }

        // ---------------------------------------------------------------- 컬럼 타입 태그 (A)

        // OnIndexingComplete()에서 호출. 표본 행으로 컬럼 추론 타입을 계산해 헤더 툴팁에 표시.
        private void ComputeColumnTypeTags()
        {
            if (_doc is null) { _columnSummaries = Array.Empty<ColumnSummary>(); return; }
            var doc = _doc;
            const int sampleCap = 10_000;
            int n = Math.Min(sampleCap, doc.DataRowsAvailable);
            var sample = new List<string[]>(n);
            for (int i = 0; i < n; i++)
            {
                try { sample.Add(doc.GetDataRowUncached(i)); } catch { }
            }

            var report = ColumnStatisticsBuilder.Summarize(doc.Header, sample);
            _columnSummaries = report.Columns.ToArray();

            for (int c = 0; c < grid.Columns.Count && c < _columnSummaries.Length; c++)
            {
                var s = _columnSummaries[c];
                grid.Columns[c].ToolTipText = LT(
                    $"Type: {s.InferredType.DisplayName()} · unique {s.UniqueCount:N0} · nulls {s.NullCount:N0}",
                    $"타입: {s.InferredType.DisplayName()} · 고유값 {s.UniqueCount:N0} · 빈값 {s.NullCount:N0}");
            }

            // 타입 배지가 생겼으니 헤더를 다시 그린다.
            grid.Invalidate();
        }

        // ---- 헤더 타입 배지 그리기 ----

        private int MeasureBadgeWidth(Graphics g, ColumnValueType type)
        {
            using var f = new Font(grid.Font.FontFamily, 6.75f, FontStyle.Bold);
            Size ts = TextRenderer.MeasureText(g, TypeAbbrev(type), f, Size.Empty, TextFormatFlags.NoPadding);
            return ts.Width + 12;
        }

        private void DrawTypeBadge(Graphics g, Point at, ColumnValueType type)
        {
            string label = TypeAbbrev(type);
            using var f = new Font(grid.Font.FontFamily, 6.75f, FontStyle.Bold);
            Size ts = TextRenderer.MeasureText(g, label, f, Size.Empty, TextFormatFlags.NoPadding);
            var rect = new Rectangle(at.X, at.Y, ts.Width + 12, 16);

            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = RoundedRect(rect, 4))
            using (var brush = new SolidBrush(TypeColor(type)))
                g.FillPath(brush, path);
            g.SmoothingMode = oldMode;

            TextRenderer.DrawText(g, label, f, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawSortArrow(Graphics g, Rectangle r, bool ascending)
        {
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Point[] pts = ascending
                ? new[] { new Point(r.Left, r.Bottom), new Point(r.Right, r.Bottom), new Point(r.Left + r.Width / 2, r.Top) }
                : new[] { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Left + r.Width / 2, r.Bottom) };
            using (var brush = new SolidBrush(_palette.HeaderText))
                g.FillPolygon(brush, pts);
            g.SmoothingMode = oldMode;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static string TypeAbbrev(ColumnValueType type) => type switch
        {
            ColumnValueType.Integer => "INT",
            ColumnValueType.Float => "FLT",
            ColumnValueType.Date => "DATE",
            ColumnValueType.DateTime => "DTTM",
            ColumnValueType.Time => "TIME",
            ColumnValueType.Boolean => "BOOL",
            ColumnValueType.Categorical => "CAT",
            ColumnValueType.Identifier => "ID",
            ColumnValueType.String => "STR",
            ColumnValueType.Empty => "—",
            _ => "STR"
        };

        private static Color TypeColor(ColumnValueType type) => type switch
        {
            ColumnValueType.Integer => Color.FromArgb(46, 111, 176),     // 파랑
            ColumnValueType.Float => Color.FromArgb(27, 158, 119),       // 청록
            ColumnValueType.Date => Color.FromArgb(123, 94, 167),        // 보라
            ColumnValueType.DateTime => Color.FromArgb(101, 79, 140),    // 진보라
            ColumnValueType.Time => Color.FromArgb(150, 111, 196),       // 연보라
            ColumnValueType.Boolean => Color.FromArgb(210, 105, 30),     // 주황
            ColumnValueType.Categorical => Color.FromArgb(184, 134, 11), // 황금
            ColumnValueType.Identifier => Color.FromArgb(96, 125, 139),  // 청회색
            ColumnValueType.String => Color.FromArgb(120, 120, 120),     // 회색
            ColumnValueType.Empty => Color.FromArgb(160, 160, 160),      // 연회색
            _ => Color.FromArgb(120, 120, 120)
        };

        private string ColumnLabel(int c)
        {
            string name = c < grid.Columns.Count ? grid.Columns[c].HeaderText : $"Column{c + 1}";
            if (c < _columnSummaries.Length) return $"{name}  [{_columnSummaries[c].InferredType.DisplayName()}]";
            return name;
        }

        private string[] ColumnLabels()
        {
            int n = _doc?.ColumnCount ?? 0;
            var labels = new string[n];
            for (int c = 0; c < n; c++) labels[c] = ColumnLabel(c);
            return labels;
        }

        private bool IsNumericColumn(int c)
            => c < _columnSummaries.Length &&
               (_columnSummaries[c].InferredType == ColumnValueType.Integer ||
                _columnSummaries[c].InferredType == ColumnValueType.Float);

        private int FirstNumericColumn()
        {
            for (int c = 0; c < _columnSummaries.Length; c++) if (IsNumericColumn(c)) return c;
            return 0;
        }

        private int FirstDateColumn()
        {
            for (int c = 0; c < _columnSummaries.Length; c++)
                if (_columnSummaries[c].InferredType.HasDateComponent()) return c;
            return 0;
        }

        // ---------------------------------------------------------------- 현재 뷰 행 수집

        private const int AnalysisRowCap = 2_000_000;

        private List<string[]> GatherViewRows(out bool truncated)
        {
            truncated = false;
            var rows = new List<string[]>();
            if (_doc is null) return rows;
            int total = _doc.DisplayRowCount;
            if (total > AnalysisRowCap) { total = AnalysisRowCap; truncated = true; }
            for (int i = 0; i < total; i++)
            {
                try { rows.Add(_doc.GetDisplayRow(i)); } catch { }
            }
            return rows;
        }

        private List<(string[] Fields, long SourceRow)> GatherViewRowsWithSource(out bool truncated)
        {
            truncated = false;
            var rows = new List<(string[], long)>();
            if (_doc is null) return rows;
            int total = _doc.DisplayRowCount;
            if (total > AnalysisRowCap) { total = AnalysisRowCap; truncated = true; }
            for (int i = 0; i < total; i++)
            {
                try { rows.Add((_doc.GetDisplayRow(i), _doc.GetSourceRowNumber(i))); } catch { }
            }
            return rows;
        }

        private static List<double> NumericColumn(IEnumerable<string[]> rows, int col)
        {
            var values = new List<double>();
            foreach (var row in rows)
                if (col < row.Length && double.TryParse(row[col].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    values.Add(d);
            return values;
        }

        private void ShowResult(string title, string body) => ShowResult(title, body, false);

        private void ShowResult(string title, string body, bool truncated)
        {
            if (truncated)
                body = LT($"(showing first {AnalysisRowCap:N0} rows)\n\n", $"(처음 {AnalysisRowCap:N0}행만 표시)\n\n") + body;
            using var form = new ResultForm(title, body, _palette);
            form.ShowDialog(this);
        }

        // ---------------------------------------------------------------- 내보내기 (E)

        private async void ExportView(ExportFormat format)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            string fileName;
            using (var dlg = new SaveFileDialog
            {
                Filter = CsvExporter.FilterString,
                FilterIndex = (int)format + 1,
                FileName = "export" + format switch
                {
                    ExportFormat.Markdown => ".md",
                    ExportFormat.Json => ".json",
                    ExportFormat.Html => ".html",
                    _ => ".csv"
                }
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                fileName = dlg.FileName;
            }

            var order = VisibleColumnOrder();
            var doc = _doc;
            int total = doc.DisplayRowCount;
            var fmt = CsvExporter.FormatFromExtension(fileName);

            SetBusy(true);
            progressBar.Visible = true;
            progressBar.Value = 0;
            progressLabel.Visible = true;
            progressLabel.Text = "0%";
            statusLabel.Text = LT("Exporting…", "내보내는 중…");
            var progress = new Progress<int>(p =>
            {
                progressBar.Value = Math.Clamp(p, 0, 100);
                progressLabel.Text = Math.Clamp(p, 0, 100) + "%";
            });

            try
            {
                await Task.Run(() =>
                {
                    var prog = (IProgress<int>)progress;
                    int done = 0;
                    IEnumerable<string[]> Rows()
                    {
                        for (int i = 0; i < total; i++)
                        {
                            string[] r;
                            try { r = doc.GetDisplayRow(i); } catch { yield break; }
                            if ((++done & 0x3FFF) == 0) prog.Report(total == 0 ? 100 : (int)(done * 100L / total));
                            yield return r;
                        }
                    }
                    CsvExporter.Export(fmt, fileName, doc.Header, Rows(), order);
                });
                statusLabel.Text = LT($"Exported {total:N0} rows", $"{total:N0}행 내보냄");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, LT("Export failed", "내보내기 실패"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                progressBar.Visible = false;
                progressLabel.Visible = false;
                SetBusy(false);
            }
        }

        private List<int> VisibleColumnOrder()
        {
            var order = new List<int>();
            int n = _doc?.ColumnCount ?? 0;
            for (int c = 0; c < n; c++)
                if (!_hiddenColumns.Contains(c)) order.Add(c);
            return order;
        }

        // ---------------------------------------------------------------- 그리드/인스펙터 복사 (그리드 향상)

        private void OnGridCopyKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { CopySelectedCells(); e.Handled = true; }
        }

        private void CopySelectedCells()
        {
            if (_doc is null) return;
            var sel = grid.SelectedCells;
            if (sel.Count == 0) return;
            var set = new HashSet<(int, int)>(sel.Count);
            foreach (DataGridViewCell c in sel)
                if (c.RowIndex >= 0 && c.ColumnIndex >= 0) set.Add((c.RowIndex, c.ColumnIndex));
            if (set.Count == 0) return;

            var doc = _doc;
            try
            {
                string tsv = GridCopyFormatter.SelectedCellsTsv(set, r => doc.GetDisplayRow(r));
                if (tsv.Length > 0) Clipboard.SetText(tsv);
                statusLabel.Text = LT($"Copied {set.Count} cells", $"{set.Count}개 셀 복사");
            }
            catch (Exception ex) { Debug.WriteLine($"[CopyCells] {ex}"); }
        }

        private void CopyCurrentRow()
        {
            if (_doc is null) return;
            int r = grid.CurrentCell?.RowIndex ?? -1;
            if (r < 0 || r >= _doc.DisplayRowCount) return;
            try
            {
                Clipboard.SetText(GridCopyFormatter.RowTsv(_doc.GetDisplayRow(r), VisibleColumnOrder()));
                statusLabel.Text = LT("Copied row", "행 복사");
            }
            catch (Exception ex) { Debug.WriteLine($"[CopyRow] {ex}"); }
        }

        private async Task CopyCurrentColumnAsync()
        {
            if (_doc is null || _busy) return;
            int col = grid.CurrentCell?.ColumnIndex ?? -1;
            if (col < 0) return;
            var doc = _doc;
            int total = doc.DisplayRowCount;
            string header = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";

            SetBusy(true);
            statusLabel.Text = LT("Copying column…", "열 복사 중…");
            try
            {
                string tsv = await Task.Run(() =>
                {
                    var vals = new List<string>(Math.Min(total, 1 << 16));
                    for (int i = 0; i < total; i++)
                    {
                        var row = doc.GetDisplayRow(i);
                        vals.Add(col < row.Length ? row[col] : string.Empty);
                    }
                    return GridCopyFormatter.ColumnTsv(header, vals);
                });
                Clipboard.SetText(tsv);
                statusLabel.Text = LT($"Copied column ({total:N0})", $"열 복사 ({total:N0}행)");
            }
            catch (Exception ex) { Debug.WriteLine($"[CopyCol] {ex}"); }
            finally { SetBusy(false); }
        }

        private Button InspectorButton(string text, int rightOffset, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                Size = new Size(50, 19),
                Font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold),
                BackColor = _palette.Surface,
                ForeColor = _palette.Accent,
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = _palette.Border;
            b.Click += onClick;
            void Reposition()
            {
                try { b.Top = 1; b.Left = Math.Max(0, outerSplit.Panel2.ClientSize.Width - rightOffset); } catch { }
            }
            outerSplit.Panel2.ClientSizeChanged += (_, _) => Reposition();
            Reposition();
            return b;
        }

        private void CopyInspectorText()
        {
            if (_doc is null || string.IsNullOrEmpty(detailRichText.Text)) return;
            try { Clipboard.SetText(detailRichText.Text); statusLabel.Text = LT("Copied inspector text", "상세 텍스트 복사"); }
            catch (Exception ex) { Debug.WriteLine($"[InspText] {ex}"); }
        }

        private void CopyInspectorJson()
        {
            if (_doc is null) return;
            int r = grid.CurrentCell?.RowIndex ?? -1;
            if (r < 0 || r >= _doc.DisplayRowCount) { statusLabel.Text = LT("Select a row first", "행을 먼저 선택하세요"); return; }
            try
            {
                Clipboard.SetText(CsvExporter.RowJson(_doc.Header, _doc.GetDisplayRow(r), VisibleColumnOrder()));
                statusLabel.Text = LT("Copied row as JSON", "행을 JSON으로 복사");
            }
            catch (Exception ex) { Debug.WriteLine($"[InspJson] {ex}"); }
        }

        // ---------------------------------------------------------------- 헤더 필터 (범주/날짜)

        // Empty(비-널 값 없음)를 제외한 모든 타입에 타입별 필터를 제공한다.
        private bool IsFilterableColumn(int c)
            => c < _columnSummaries.Length && _columnSummaries[c].InferredType != ColumnValueType.Empty;

        // 고유값이 임계 이하면 범위보다 체크박스 선택이 유용(상태 코드 등). 표본 기준이라 근사치.
        private bool IsLowCardinality(int c)
            => c < _columnSummaries.Length && _columnSummaries[c].UniqueCount is > 0 and <= 12;

        // 헤더 우측의 깔때기 아이콘. 활성 필터면 악센트로 채움.
        private void DrawFunnel(Graphics g, Rectangle r, bool active)
        {
            var pts = new[]
            {
                new Point(r.Left, r.Top), new Point(r.Right, r.Top),
                new Point(r.Left + r.Width * 3 / 5, r.Top + r.Height / 2),
                new Point(r.Left + r.Width * 3 / 5, r.Bottom),
                new Point(r.Left + r.Width * 2 / 5, r.Bottom - 2),
                new Point(r.Left + r.Width * 2 / 5, r.Top + r.Height / 2),
            };
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (active)
            {
                using var b = new SolidBrush(_palette.Accent);
                g.FillPolygon(b, pts);
            }
            else
            {
                using var p = new Pen(Color.FromArgb(150, _palette.HeaderText), 1f);
                g.DrawPolygon(p, pts);
            }
            g.SmoothingMode = old;
        }

        private async void OpenColumnFilter(int col)
        {
            if (_doc is null || !_doc.IndexingComplete) return;
            var doc = _doc;
            string name = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";
            Rectangle rect = grid.GetCellDisplayRectangle(col, -1, true);
            Point screenPt = grid.PointToScreen(new Point(rect.Left, rect.Bottom));

            var type = col < _columnSummaries.Length ? _columnSummaries[col].InferredType : ColumnValueType.String;

            // 숫자 범위(Integer / Float) — 고유값이 적으면 체크박스가 더 유용하므로 폴백.
            if (type is ColumnValueType.Integer or ColumnValueType.Float && !IsLowCardinality(col))
            {
                var existing = _columnFilters.NumericFilters.FirstOrDefault(f => f.Column == col);
                using var popup = new ColumnFilterPopup(name, existing?.Min, existing?.Max, _palette);
                if (!popup.ShowAt(this, screenPt)) return;
                _columnFilters.SetNumericRange(col, popup.NumMin, popup.NumMax);
                await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
                grid.Invalidate();
                return;
            }

            // 시간 범위(Date / DateTime / Time) — 정밀도 인식
            if (type.HasDateComponent() || type == ColumnValueType.Time)
            {
                var kind = type switch
                {
                    ColumnValueType.DateTime => TemporalFilterKind.DateTime,
                    ColumnValueType.Time => TemporalFilterKind.Time,
                    _ => TemporalFilterKind.Date
                };
                var existing = _columnFilters.DateFilters.FirstOrDefault(f => f.Column == col);
                using var popup = new ColumnFilterPopup(name, existing?.Start, existing?.End, kind, _palette);
                if (!popup.ShowAt(this, screenPt)) return;
                _columnFilters.SetDateRange(col, popup.RangeStart, popup.RangeEnd, kind);
                await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
                grid.Invalidate();
                return;
            }

            // 텍스트 술어(String / Identifier)
            if (type is ColumnValueType.String or ColumnValueType.Identifier)
            {
                var existing = _columnFilters.TextFilters.FirstOrDefault(f => f.Column == col);
                using var popup = new ColumnFilterPopup(name, existing, _palette);
                if (!popup.ShowAt(this, screenPt)) return;
                _columnFilters.SetText(col, popup.TextOp, popup.TextValue, popup.TextCaseSensitive);
                await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
                grid.Invalidate();
                return;
            }

            // 범주형 / 불리언: 전체 데이터에서 고유값 수집(백그라운드) 후 체크박스
            SetBusy(true);
            statusLabel.Text = LT("Loading values…", "값 불러오는 중…");
            IReadOnlyList<(string Value, int Count)> distinct;
            try { distinct = await Task.Run(() => doc.DistinctValues(col, withinCurrentView: false, CancellationToken.None)); }
            catch { distinct = Array.Empty<(string, int)>(); }
            finally { SetBusy(false); }

            // 로딩이 끝났으니 "값 불러오는 중…"을 현재 필터 상태로 되돌린다.
            // (팝업을 취소해도 상태줄에 메시지가 남지 않도록)
            UpdateFilterStatus();

            var current = _columnFilters.ValueFilters.FirstOrDefault(f => f.Column == col);
            using var pop = new ColumnFilterPopup(name, distinct, current, _palette);
            if (!pop.ShowAt(this, screenPt)) return;

            if (pop.SelectAll) _columnFilters.Remove(col); // 전체 선택 = 필터 없음
            else _columnFilters.SetValues(col, pop.SelectedValues, pop.IncludeBlanks);
            await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
            grid.Invalidate();
        }

        // ---------------------------------------------------------------- 활성 필터 칩 바

        private FlowLayoutPanel? _chipsBar;

        // 활성 필터를 (라벨, 제거, 편집?)으로 수집. 컬럼 필터는 클릭 시 해당 팝업을 다시 연다.
        private List<(string Label, Action Remove, Action? Edit)> ActiveFilterChips()
        {
            var list = new List<(string, Action, Action?)>();
            if (_textCondition is not null)
                list.Add((_textConditionDesc, () => { _textCondition = null; _textConditionDesc = ""; }, null));
            for (int i = 0; i < _valueConditions.Count; i++)
            {
                int idx = i;
                list.Add((_valueConditions[idx].desc, () => _valueConditions.RemoveAt(idx), null));
            }
            if (_doc is not null)
                foreach (var (col, text) in _columnFilters.DescribeEntries(_doc.Header))
                {
                    int c = col;
                    list.Add((text, () => _columnFilters.Remove(c), () => OpenColumnFilter(c)));
                }
            return list;
        }

        private void RebuildFilterChips()
        {
            var chips = ActiveFilterChips();
            if (_chipsBar is null)
            {
                if (chips.Count == 0) return; // 표시할 게 없으면 생성도 미룬다
                _chipsBar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    WrapContents = true,
                    Padding = new Padding(4, 3, 4, 3),
                    Margin = new Padding(0),
                    BackColor = _palette.Surface,
                };
                splitContainer1.Panel2.Controls.Add(_chipsBar);
                _chipsBar.BringToFront();
            }

            _chipsBar.SuspendLayout();
            var old = _chipsBar.Controls.Cast<Control>().ToArray();
            _chipsBar.Controls.Clear();
            foreach (var c in old) c.Dispose();
            // 조건이 둘 이상일 때만 결합 방식(AND/OR) 토글을 보여준다.
            if (chips.Count >= 2) _chipsBar.Controls.Add(MakeModeToggle());
            foreach (var (label, remove, edit) in chips)
                _chipsBar.Controls.Add(MakeFilterChip(label, remove, edit));
            _chipsBar.Visible = chips.Count > 0;
            _chipsBar.ResumeLayout();
        }

        private Control MakeModeToggle()
        {
            string label = _filterMatchAny ? LT("ANY (OR)", "하나라도(OR)") : LT("ALL (AND)", "모두(AND)");
            var b = new Button
            {
                Text = label,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = _palette.Surface,
                ForeColor = _palette.Accent,
                Margin = new Padding(2),
                Padding = new Padding(6, 2, 6, 2),
                Cursor = Cursors.Hand,
                TabStop = false,
                Font = new Font(Font, FontStyle.Bold),
            };
            b.FlatAppearance.BorderColor = _palette.Accent;
            b.Click += async (_, _) =>
            {
                _filterMatchAny = !_filterMatchAny;
                await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
                grid.Invalidate();
            };
            return b;
        }

        private Control MakeFilterChip(string label, Action remove, Action? edit)
        {
            var chip = new Panel { AutoSize = true, Margin = new Padding(2), BackColor = _palette.Accent };
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            var text = new Label { Text = label, AutoSize = true, ForeColor = Color.White, Margin = new Padding(0), Padding = new Padding(7, 3, 3, 3) };
            if (edit is not null) { text.Cursor = Cursors.Hand; text.Click += (_, _) => edit(); } // 클릭 시 해당 필터 팝업 재오픈
            var close = new Label { Text = "✕", AutoSize = true, ForeColor = Color.White, Cursor = Cursors.Hand, Margin = new Padding(0), Padding = new Padding(2, 3, 7, 3), Font = new Font(Font, FontStyle.Bold) };
            close.Click += async (_, _) =>
            {
                remove();
                await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
                grid.Invalidate();
                // RebuildFilterAsync → UpdateFilterStatus → RebuildFilterChips() 가 바를 다시 그린다.
            };
            flow.Controls.Add(text);
            flow.Controls.Add(close);
            chip.Controls.Add(flow);
            return chip;
        }

        // ---------------------------------------------------------------- 행으로 이동 (D)

        private async void GoToRow()
        {
            if (_doc is null || _doc.DisplayRowCount == 0) return;
            using var dlg = new ParamDialog(LT("Go to Row", "행으로 이동"), _palette);
            var input = dlg.AddText(LT("Source row #", "원본 행번호"));
            if (!dlg.ShowOk(this)) return;
            if (!long.TryParse(input.Text.Trim(), out long target) || target < 1)
            {
                statusLabel.Text = LT("Enter a valid row number", "유효한 행번호를 입력하세요");
                return;
            }

            var doc = _doc;
            int viewRow = -1;
            if (!doc.IsFiltered)
            {
                if (target <= doc.DataRowsAvailable) viewRow = (int)(target - 1);
            }
            else
            {
                int total = doc.DisplayRowCount;
                viewRow = await Task.Run(() =>
                {
                    for (int i = 0; i < total; i++)
                        if (doc.GetSourceRowNumber(i) == target) return i;
                    return -1;
                });
            }

            if (viewRow < 0)
            {
                statusLabel.Text = LT($"Row {target} not in current view", $"{target}행이 현재 보기에 없습니다");
                return;
            }
            try
            {
                int col = Math.Max(0, grid.CurrentCell?.ColumnIndex ?? 0);
                grid.CurrentCell = grid.Rows[viewRow].Cells[col];
                grid.FirstDisplayedScrollingRowIndex = viewRow;
            }
            catch { }
        }

        // ---------------------------------------------------------------- 고급(표현식) 필터 (C)

        private async void ShowAdvancedFilter()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            using var dlg = new ParamDialog(LT("Advanced Filter", "고급 필터"), _palette);
            dlg.AddNote(LT("e.g.  age > 30 AND city = \"서울\"", "예:  age > 30 AND city = \"서울\""));
            var input = dlg.AddText(LT("Expression", "표현식"));
            if (!dlg.ShowOk(this)) return;
            string expr = input.Text.Trim();
            if (expr.Length == 0) return;

            CompiledAdvancedFilter compiled;
            try { compiled = AdvancedFilterExpression.Compile(expr, _doc.Header); }
            catch (AdvancedFilterExpressionException ex)
            {
                MessageBox.Show(ex.Message, LT("Invalid filter", "잘못된 필터"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 현재 뷰를 표현식으로 좁힘(증분). 셀값 조건 목록에 설명을 추가.
            _valueConditions.Add(($"⨍ {Trunc(expr)}", compiled.Predicate));
            await RunViewOpAsync(p => _doc.FilterWithinViewAsync(compiled.Predicate, p, _opCts!.Token),
                LT("Applying expression…", "표현식 적용 중…"));
            UpdateFilterStatus();
        }

        // ---------------------------------------------------------------- 컬럼 표시/숨김 (G)

        private void ShowColumnChooser()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Columns", "컬럼 표시"), _palette);
            var list = dlg.AddCheckedList(LT("Visible columns", "표시할 컬럼"), ColumnLabels(), Math.Min(12, _doc.ColumnCount));
            for (int c = 0; c < list.Items.Count; c++) list.SetItemChecked(c, !_hiddenColumns.Contains(c));
            if (!dlg.ShowOk(this)) return;

            _hiddenColumns.Clear();
            for (int c = 0; c < list.Items.Count && c < grid.Columns.Count; c++)
            {
                bool visible = list.GetItemChecked(c);
                grid.Columns[c].Visible = visible;
                if (!visible) _hiddenColumns.Add(c);
            }
        }

        // ---------------------------------------------------------------- 저장된 뷰 (H)

        private CsvSearchQuery? CurrentSearchQuery()
        {
            try { return CsvSearchQuery.FromUserInput(findTextBox.Text, null); }
            catch { return null; }
        }

        private void SaveCurrentView()
        {
            if (_doc is null || _currentPath is null) return;
            int filterCol = filterColumnCombo.SelectedIndex - 1;
            var view = SavedCsvView.Create(
                "view", _textCondition is not null ? filterTextBox.Text : null,
                filterCol < 0 ? (int?)null : filterCol,
                _sortKeys, _hiddenColumns, CurrentSearchQuery(),
                grid.CurrentCell?.ColumnIndex ?? 0, _columnFilters);
            SavedViewStore.Save(_currentPath, view);
            statusLabel.Text = LT("View saved", "보기를 저장했습니다");
        }

        private async Task RestoreSavedViewAsync()
        {
            if (_doc is null || _currentPath is null) return;
            var view = SavedViewStore.Load(_currentPath);
            if (view is null) { statusLabel.Text = LT("No saved view", "저장된 보기가 없습니다"); return; }

            // 숨김 컬럼 복원(즉시)
            _hiddenColumns.Clear();
            foreach (int c in view.HiddenColumnIndexes)
                if (c >= 0 && c < grid.Columns.Count) { grid.Columns[c].Visible = false; _hiddenColumns.Add(c); }

            // 검색어 복원
            if (view.SearchText is not null) findTextBox.Text = view.SearchText;

            // 컬럼 필터(값/시간/숫자/텍스트) 전부 복원 — 시간 정밀도(Kind)·숫자·텍스트 포함.
            if (view.ColumnFilters is { } cf) _columnFilters.CopyFrom(cf);
            else _columnFilters.Clear();

            // 텍스트 필터 조건 구성(아직 적용 안 함)
            _textCondition = null;
            _textConditionDesc = "";
            if (!string.IsNullOrEmpty(view.FilterText))
            {
                int fcol = view.FilterColumn ?? -1;
                filterColumnCombo.SelectedIndex = fcol + 1;
                filterTextBox.Text = view.FilterText;
                _textCondition = BuildContainsPredicate(view.FilterText, fcol);
                string colName = fcol < 0 ? Loc.T("Filter_All")
                    : (fcol < grid.Columns.Count ? grid.Columns[fcol].HeaderText : Loc.F("ColShort_Fmt", fcol + 1));
                _textConditionDesc = Loc.F("Filter_ContainsFmt", colName, Trunc(view.FilterText));
            }

            // 텍스트+컬럼 필터를 결합 적용(RebuildFilterAsync가 정렬을 초기화하므로 정렬은 그 뒤에)
            if (HasAnyFilter) await RebuildFilterAsync(LT("Restoring…", "복원 중…"));
            else { _doc.ClearView(); grid.RowCount = 0; RefreshRowCount(); grid.Invalidate(); }

            // 정렬 복원(있으면 재적용)
            if (view.Sort.Count > 0)
            {
                _sortKeys.Clear();
                _sortKeys.AddRange(view.Sort);
                await SortAsync();
            }
            grid.Invalidate(); // 헤더 깔때기 활성 표시 갱신

            // 현재 컬럼 위치 복원
            int col = Math.Clamp(view.CurrentColumn, 0, Math.Max(0, grid.Columns.Count - 1));
            if (grid.RowCount > 0 && grid.Columns.Count > 0)
            {
                try { grid.CurrentCell = grid.Rows[0].Cells[col]; } catch { }
            }
            statusLabel.Text = LT("View restored", "보기를 복원했습니다");
        }

        // ---------------------------------------------------------------- 성능 대시보드 (L)

        private void ShowPerformanceDashboard()
        {
            if (_doc is null) return;
            double secs = _lastIndexMs / 1000.0;
            double gbps = secs > 0 ? _doc.FileLength / secs / 1_000_000_000.0 : 0;
            var sb = new StringBuilder();
            sb.AppendLine(LT("Rows", "행 수") + $": {_doc.DataRowsAvailable:N0}");
            sb.AppendLine(LT("Columns", "컬럼 수") + $": {_doc.ColumnCount:N0}");
            sb.AppendLine(LT("File size", "파일 크기") + $": {FormatBytes(_doc.FileLength)}");
            sb.AppendLine(LT("Storage mode", "저장 모드") + $": {(_doc.InMemory ? "RAM" : "Disk")}");
            sb.AppendLine(LT("Encoding", "인코딩") + $": {_doc.EncodingName}");
            sb.AppendLine(LT("Delimiter", "구분자") + $": '{_doc.Delimiter}'");
            sb.AppendLine(LT("Indexing time", "인덱싱 시간") + $": {_lastIndexMs:N0} ms");
            sb.AppendLine(LT("Throughput", "처리량") + $": {gbps:0.00} GB/s");
            if (_doc.IsFiltered)
                sb.AppendLine(LT("Visible rows", "표시 행") + $": {_doc.DisplayRowCount:N0}");
            ShowResult(LT("Performance Dashboard", "성능 대시보드"), sb.ToString());
        }

        // ---------------------------------------------------------------- 인덱스 캐시 (F)

        private void OpenIndexFolder()
        {
            try
            {
                Directory.CreateDirectory(IndexCache.FolderPath);
                Process.Start(new ProcessStartInfo { FileName = IndexCache.FolderPath, UseShellExecute = true });
            }
            catch (Exception ex) { Debug.WriteLine($"[IndexFolder] {ex}"); }
        }

        private void ClearIndexCache()
        {
            IndexCache.Clear();
            statusLabel.Text = LT("Index cache cleared", "인덱스 캐시를 비웠습니다");
        }

        // 설정이 켜져 있으면 현재 파일의 영속 인덱스를 삭제(파일을 닫거나 다른 파일을 열 때).
        private void DeleteCurrentIndexIfRequested()
        {
            if (_settings.DeleteIndexOnClose && _currentPath is not null)
                IndexCache.DeleteFor(_currentPath);
        }

        // ---------------------------------------------------------------- 분석 (M)

        private void AnalyzeDistribution()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Numeric Distribution", "수치 분포"), _palette);
            var col = dlg.AddCombo(LT("Column", "컬럼"), ColumnLabels(), FirstNumericColumn());
            var bins = dlg.AddNumeric(LT("Bins", "구간 수"), 1, 100, 10);
            if (!dlg.ShowOk(this)) return;

            var rows = GatherViewRows(out bool truncated);
            var values = NumericColumn(rows, col.SelectedIndex);
            if (values.Count == 0) { ShowResult(LT("Numeric Distribution", "수치 분포"), LT("No numeric values.", "수치 값이 없습니다.")); return; }

            var d = CsvAnalytics.NumericDistributionOf(values, col.SelectedIndex, (int)bins.Value);
            var sb = new StringBuilder();
            sb.AppendLine(ColumnLabel(col.SelectedIndex));
            sb.AppendLine(new string('─', 40));
            sb.AppendLine($"count  {d.Count:N0}");
            sb.AppendLine($"min    {d.Min:G6}");
            sb.AppendLine($"q1     {d.Q1:G6}");
            sb.AppendLine($"median {d.Median:G6}");
            sb.AppendLine($"mean   {d.Mean:G6}");
            sb.AppendLine($"q3     {d.Q3:G6}");
            sb.AppendLine($"max    {d.Max:G6}");
            sb.AppendLine($"std    {d.StandardDeviation:G6}");
            sb.AppendLine();
            sb.AppendLine(LT("Histogram", "히스토그램") + ":");
            int maxCount = d.Bins.Count > 0 ? d.Bins.Max(b => b.Count) : 0;
            foreach (var b in d.Bins)
            {
                int barLen = maxCount > 0 ? b.Count * 30 / maxCount : 0;
                sb.AppendLine($"[{b.LowerBound,10:G5} – {b.UpperBound,10:G5}) {b.Count,8:N0} {new string('█', barLen)}");
            }
            ShowResult(LT("Numeric Distribution", "수치 분포"), sb.ToString(), truncated);
        }

        private void AnalyzeDateHistogram()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Date Histogram", "날짜 히스토그램"), _palette);
            var dateCol = dlg.AddCombo(LT("Date column", "날짜 컬럼"), ColumnLabels(), FirstDateColumn());
            var valueCol = dlg.AddCombo(LT("Value column (optional)", "값 컬럼(선택)"),
                new[] { LT("(none)", "(없음)") }.Concat(ColumnLabels()), 0);
            var period = dlg.AddCombo(LT("Period", "주기"), new[] { "Day", "Week", "Month", "Year" }, 2);
            if (!dlg.ShowOk(this)) return;

            var rows = GatherViewRows(out bool truncated);
            int? vc = valueCol.SelectedIndex == 0 ? null : valueCol.SelectedIndex - 1;
            var p = (DateBinPeriod)period.SelectedIndex;
            var hist = CsvAnalytics.DateHistogramOf(rows, dateCol.SelectedIndex, vc, p);
            var sb = new StringBuilder();
            sb.AppendLine($"{ColumnLabel(dateCol.SelectedIndex)} · {p}");
            sb.AppendLine(new string('─', 40));
            foreach (var b in hist.Bins)
            {
                sb.Append($"{b.Label,-12} {b.Count,8:N0}");
                if (b.Sum is double s) sb.Append($"  sum={s:G6}  avg={b.Average:G6}");
                sb.AppendLine();
            }
            ShowResult(LT("Date Histogram", "날짜 히스토그램"), sb.ToString(), truncated);
        }

        private void AnalyzeDuplicates()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Find Duplicates", "중복 찾기"), _palette);
            var list = dlg.AddCheckedList(LT("Key columns", "키 컬럼"), ColumnLabels(), Math.Min(12, _doc.ColumnCount));
            if (!dlg.ShowOk(this)) return;
            var keys = CheckedIndexes(list);
            if (keys.Count == 0) { ShowResult(LT("Find Duplicates", "중복 찾기"), LT("Select at least one column.", "컬럼을 하나 이상 선택하세요.")); return; }

            var rows = GatherViewRowsWithSource(out bool truncated);
            var dups = CsvAnalytics.FindDuplicates(rows, keys);
            var sb = new StringBuilder();
            sb.AppendLine(LT($"Duplicate groups: {dups.Count:N0}", $"중복 그룹: {dups.Count:N0}"));
            sb.AppendLine(new string('─', 40));
            foreach (var g in dups.Take(1000))
                sb.AppendLine($"{string.Join(" | ", g.Key)}  ×{g.SourceRows.Count}  → " +
                    LT("rows ", "행 ") + string.Join(", ", g.SourceRows.Take(20)) + (g.SourceRows.Count > 20 ? " …" : ""));
            if (dups.Count > 1000) sb.AppendLine("…");
            ShowResult(LT("Find Duplicates", "중복 찾기"), sb.ToString(), truncated);
        }

        private void AnalyzeGroupBy()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Group By", "그룹별 집계"), _palette);
            var groupList = dlg.AddCheckedList(LT("Group columns", "그룹 컬럼"), ColumnLabels(), Math.Min(8, _doc.ColumnCount));
            var valueCol = dlg.AddCombo(LT("Value column", "값 컬럼"), ColumnLabels(), FirstNumericColumn());
            var funcList = dlg.AddCheckedList(LT("Functions", "집계 함수"),
                Enum.GetValues<AggregationFunction>().Select(f => f.DisplayName()), 8);
            funcList.SetItemChecked(0, true); // Count
            funcList.SetItemChecked((int)AggregationFunction.Sum, true);
            funcList.SetItemChecked((int)AggregationFunction.Mean, true);
            if (!dlg.ShowOk(this)) return;

            var groups = CheckedIndexes(groupList);
            if (groups.Count == 0) { ShowResult(LT("Group By", "그룹별 집계"), LT("Select group columns.", "그룹 컬럼을 선택하세요.")); return; }
            var funcs = CheckedIndexes(funcList).Select(i => (AggregationFunction)i).ToList();
            if (funcs.Count == 0) funcs.Add(AggregationFunction.Count);

            var rows = GatherViewRows(out bool truncated);
            var result = CsvAnalytics.GroupBy(rows, groups, valueCol.SelectedIndex, funcs);
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(" | ", groups.Select(ColumnLabel)) + "  →  " + string.Join(", ", funcs.Select(f => f.DisplayName())));
            sb.AppendLine(new string('─', 50));
            foreach (var r in result.Rows.Take(5000))
                sb.AppendLine($"{string.Join(" | ", r.Key),-30}  " +
                    string.Join("  ", funcs.Select(f => $"{f.DisplayName()}={r.Values[f]:G6}")));
            if (result.Rows.Count > 5000) sb.AppendLine("…");
            ShowResult(LT("Group By", "그룹별 집계"), sb.ToString(), truncated);
        }

        // ---------------------------------------------------------------- 통계 (N)

        private void AnalyzeCorrelation()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Correlation", "상관분석"), _palette);
            var x = dlg.AddCombo("X", ColumnLabels(), FirstNumericColumn());
            var y = dlg.AddCombo("Y", ColumnLabels(), Math.Min(FirstNumericColumn() + 1, Math.Max(0, _doc.ColumnCount - 1)));
            var method = dlg.AddCombo(LT("Method", "방법"), new[] { "Pearson", "Spearman" }, 0);
            if (!dlg.ShowOk(this)) return;

            var rows = GatherViewRows(out bool truncated);
            var pairs = new List<(double, double)>();
            foreach (var row in rows)
                if (x.SelectedIndex < row.Length && y.SelectedIndex < row.Length &&
                    double.TryParse(row[x.SelectedIndex].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double xv) &&
                    double.TryParse(row[y.SelectedIndex].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double yv))
                    pairs.Add((xv, yv));

            var r = CsvStatistics.Correlation(pairs, (CorrelationMethod)method.SelectedIndex);
            var sb = new StringBuilder();
            sb.AppendLine($"{ColumnLabel(x.SelectedIndex)}  vs  {ColumnLabel(y.SelectedIndex)}");
            sb.AppendLine(new string('─', 40));
            sb.AppendLine($"method       {r.Method}");
            sb.AppendLine($"coefficient  {r.Coefficient:0.0000}");
            sb.AppendLine($"p-value      {r.PValue:0.0000}");
            sb.AppendLine($"sample size  {r.SampleSize:N0}");
            sb.AppendLine($"→ {r.Interpretation}");
            ShowResult(LT("Correlation", "상관분석"), sb.ToString(), truncated);
        }

        private void AnalyzeIndependentTTest()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Independent t-test", "독립표본 t검정"), _palette);
            var valueCol = dlg.AddCombo(LT("Value column", "값 컬럼"), ColumnLabels(), FirstNumericColumn());
            var groupCol = dlg.AddCombo(LT("Group column", "그룹 컬럼"), ColumnLabels(), 0);
            if (!dlg.ShowOk(this)) return;

            var rows = GatherViewRows(out bool truncated);
            var groups = new Dictionary<string, List<double>>();
            int vc = valueCol.SelectedIndex, gc = groupCol.SelectedIndex;
            foreach (var row in rows)
            {
                if (vc >= row.Length || gc >= row.Length) continue;
                if (!double.TryParse(row[vc].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) continue;
                string g = row[gc];
                if (!groups.TryGetValue(g, out var listv)) { listv = new List<double>(); groups[g] = listv; }
                listv.Add(v);
            }
            var top = groups.OrderByDescending(kv => kv.Value.Count).Take(2).ToList();
            if (top.Count < 2) { ShowResult(LT("Independent t-test", "독립표본 t검정"), LT("Need at least 2 groups.", "그룹이 2개 이상 필요합니다.")); return; }

            var r = CsvStatistics.IndependentTTest(top[0].Key, top[0].Value, top[1].Key, top[1].Value);
            var sb = new StringBuilder();
            sb.AppendLine($"{ColumnLabel(vc)} by {ColumnLabel(gc)}");
            sb.AppendLine(new string('─', 40));
            sb.AppendLine($"group A      {r.GroupA} (mean {r.MeanA:G6}, n {top[0].Value.Count})");
            sb.AppendLine($"group B      {r.GroupB} (mean {r.MeanB:G6}, n {top[1].Value.Count})");
            sb.AppendLine($"t            {r.TStatistic:0.0000}");
            sb.AppendLine($"df           {r.DegreesOfFreedom:0.00}");
            sb.AppendLine($"p-value      {r.PValue:0.0000}");
            sb.AppendLine($"95% CI       [{r.ConfidenceIntervalLow:G6}, {r.ConfidenceIntervalHigh:G6}]");
            sb.AppendLine($"Cohen's d    {r.EffectSize:0.0000}");
            sb.AppendLine($"→ {r.Interpretation}");
            ShowResult(LT("Independent t-test", "독립표본 t검정"), sb.ToString(), truncated);
        }

        private void AnalyzePairedTTest()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Paired t-test", "대응표본 t검정"), _palette);
            var before = dlg.AddCombo(LT("Before column", "이전 컬럼"), ColumnLabels(), FirstNumericColumn());
            var after = dlg.AddCombo(LT("After column", "이후 컬럼"), ColumnLabels(), Math.Min(FirstNumericColumn() + 1, Math.Max(0, _doc.ColumnCount - 1)));
            if (!dlg.ShowOk(this)) return;

            var rows = GatherViewRows(out bool truncated);
            var b = new List<double>(); var a = new List<double>();
            foreach (var row in rows)
            {
                if (before.SelectedIndex >= row.Length || after.SelectedIndex >= row.Length) continue;
                if (double.TryParse(row[before.SelectedIndex].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double bv) &&
                    double.TryParse(row[after.SelectedIndex].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double av))
                { b.Add(bv); a.Add(av); }
            }
            if (b.Count < 2) { ShowResult(LT("Paired t-test", "대응표본 t검정"), LT("Need at least 2 paired values.", "쌍을 이룬 값이 2개 이상 필요합니다.")); return; }

            var r = CsvStatistics.PairedTTest(b, a);
            var sb = new StringBuilder();
            sb.AppendLine($"{ColumnLabel(before.SelectedIndex)} → {ColumnLabel(after.SelectedIndex)}");
            sb.AppendLine(new string('─', 40));
            sb.AppendLine($"mean diff    {r.MeanDifference:G6}");
            sb.AppendLine($"t            {r.TStatistic:0.0000}");
            sb.AppendLine($"df           {r.DegreesOfFreedom:0.00}");
            sb.AppendLine($"p-value      {r.PValue:0.0000}");
            sb.AppendLine($"95% CI       [{r.ConfidenceIntervalLow:G6}, {r.ConfidenceIntervalHigh:G6}]");
            sb.AppendLine($"→ {r.Interpretation}");
            ShowResult(LT("Paired t-test", "대응표본 t검정"), sb.ToString(), truncated);
        }

        private void AnalyzeChiSquare()
        {
            if (_doc is null) return;
            using var dlg = new ParamDialog(LT("Chi-square", "카이제곱 검정"), _palette);
            var rowCol = dlg.AddCombo(LT("Row column", "행 컬럼"), ColumnLabels(), 0);
            var colCol = dlg.AddCombo(LT("Column column", "열 컬럼"), ColumnLabels(), Math.Min(1, Math.Max(0, _doc.ColumnCount - 1)));
            if (!dlg.ShowOk(this)) return;

            var rows = GatherViewRows(out bool truncated);
            var pairs = new List<(string, string)>();
            foreach (var row in rows)
                if (rowCol.SelectedIndex < row.Length && colCol.SelectedIndex < row.Length)
                    pairs.Add((row[rowCol.SelectedIndex], row[colCol.SelectedIndex]));

            var r = CsvStatistics.ChiSquare(pairs);
            var sb = new StringBuilder();
            sb.AppendLine($"{ColumnLabel(rowCol.SelectedIndex)} × {ColumnLabel(colCol.SelectedIndex)}");
            sb.AppendLine(new string('─', 40));
            sb.AppendLine($"χ²           {r.Statistic:0.0000}");
            sb.AppendLine($"df           {r.DegreesOfFreedom}");
            sb.AppendLine($"p-value      {r.PValue:0.0000}");
            sb.AppendLine($"→ {r.Interpretation}");
            ShowResult(LT("Chi-square", "카이제곱 검정"), sb.ToString(), truncated);
        }

        // ---------------------------------------------------------------- 피벗 빌더 (O · P)

        private void OpenPivotBuilder(bool chartTab = false)
        {
            if (_doc is null || !_doc.IndexingComplete) return;
            var rows = GatherViewRows(out bool truncated);
            if (truncated)
                statusLabel.Text = LT($"Pivot uses first {AnalysisRowCap:N0} rows", $"피벗은 처음 {AnalysisRowCap:N0}행 사용");
            using var form = new PivotForm(_doc.Header, _columnSummaries, rows, _palette, _theme);
            if (chartTab) form.SelectChartTab();
            form.ShowDialog(this);
        }

        private static List<int> CheckedIndexes(CheckedListBox list)
        {
            var result = new List<int>();
            foreach (int i in list.CheckedIndices) result.Add(i);
            return result;
        }

        // ---------------------------------------------------------------- 드래그앤드롭 · 클립보드 (J · K)

        private void OnFeatureDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data is null) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
                e.Effect = DragDropEffects.Copy;
        }

        private async void OnFeatureDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data is null) return;
            try
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                {
                    await OpenFileAsync(files[0]);
                }
                else if (e.Data.GetData(DataFormats.Text) is string text && text.Length > 0)
                {
                    await OpenTextOrPathAsync(text);
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[DragDrop] {ex}"); }
        }

        private async Task OpenFromClipboardAsync()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    if (files.Count > 0 && files[0] is string f) { await OpenFileAsync(f); return; }
                }
                if (Clipboard.ContainsText())
                {
                    await OpenTextOrPathAsync(Clipboard.GetText());
                    return;
                }
                statusLabel.Text = LT("Clipboard has no CSV", "클립보드에 CSV가 없습니다");
            }
            catch (Exception ex) { Debug.WriteLine($"[Clipboard] {ex}"); }
        }

        // 텍스트가 파일 경로/URL이면 그 파일을, 아니면 임시 CSV로 저장해 연다.
        private async Task OpenTextOrPathAsync(string text)
        {
            string trimmed = text.Trim();
            if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                await OpenFileAsync(uri.LocalPath);
                return;
            }
            if (trimmed.Length < 260 && !trimmed.Contains('\n') && File.Exists(trimmed))
            {
                await OpenFileAsync(trimmed);
                return;
            }

            // 임시 CSV로 저장 후 열기(닫을 때 정리)
            string temp = Path.Combine(Path.GetTempPath(), "ncv_clip_" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                await File.WriteAllTextAsync(temp, text, new UTF8Encoding(true));
                _tempImportFiles.Add(temp);
                await OpenFileAsync(temp);
            }
            catch (Exception ex) { Debug.WriteLine($"[OpenText] {ex}"); }
        }

        // OnFormClosed()에서 호출 — 클립보드/드롭으로 만든 임시 파일 정리.
        private void CleanupTempImports()
        {
            foreach (var f in _tempImportFiles)
            {
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            _tempImportFiles.Clear();
        }
    }
}
