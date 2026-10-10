using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;

namespace NanumCsvViewer
{
    // macOS 버전에서 이식한 고급 기능(검색 모드·내보내기·이동·분석·통계·컬럼숨김·저장된 뷰·
    // 드래그드롭·클립보드·성능). Designer를 건드리지 않고 메뉴를 코드로 구성한다.
    public partial class Form1
    {
        // 인덱싱 후 계산한 컬럼 추론 타입/요약(헤더 툴팁·분석 기본값에 사용)
        private ColumnSummary[] _columnSummaries = Array.Empty<ColumnSummary>();
        private long _lastIndexMs;

        // 수동 지정 컬럼 타입(이슈 #12). 추론·선언 힌트보다 우선. 문서/시트 전환(ResetView) 시 초기화.
        private Dictionary<int, ColumnValueType> _manualTypeOverrides = new();

        // 시각화(이슈 #19): 모델리스 차트 빌더 창들. 문서/시트 전환 시 일괄 닫음(뷰 스냅샷이 낡기 때문).
        private List<ChartForm> _chartForms = new();
        private ToolStripMenuItem? _vizMenu;

        // 그리드/인스펙터 복사 (그리드 향상)
        private ToolStripMenuItem? _copyRowMenu, _copyColMenu;
        private Button? _inspectorCopyText, _inspectorCopyJson;

        // 헤더 타입 배지 표시 토글(보기 메뉴)
        private bool _showTypeBadges = true;
        private ToolStripMenuItem? _showBadgesMenu;
        private bool _syncingBadgeToggle;

        // SPSS·SAS 필드 라벨 표시 토글(보기 메뉴). 현재 워크북을 재임포트·재로드한다.
        private ToolStripMenuItem? _fieldLabelsMenu;
        private int _currentSheetIndex;
        private bool _reimporting;        // 라벨 모드 재임포트 진행 중(재진입 직렬화)
        private bool _syncingFieldLabels; // 체크 상태를 코드로 되돌릴 때 CheckedChanged 억제
        private HashSet<int> _hiddenColumns = new();
        private readonly List<string> _tempImportFiles = new();

        // 구조화 컬럼 필터(헤더 깔때기 → 범주/날짜 필터)
        private ColumnFilterState _columnFilters = new();
        // 활성 조건 결합 방식: false = 모두 만족(AND), true = 하나라도 만족(OR).
        private bool _filterMatchAny;

        // 언어 전환 시 다시 라벨링하기 위한 메뉴 참조
        private ToolStripMenuItem? _exportMenu, _clipboardOpenMenu, _gotoRowMenu, _advFilterMenu,
            _columnsMenu, _saveViewMenu, _restoreViewMenu, _perfMenu, _indexCacheMenu, _analysisMenu,
            _deleteIndexOnCloseMenu, _pivotTableMenu, _pivotChartMenu;
        private readonly List<(ToolStripMenuItem item, string en, string ko)> _featureLabels = new();

        private static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        // 그리드 셀 표시용 길이 제한 미리보기(전체 값은 값 표시줄·상세 패널에 보존).
        private const int MaxCellPreviewChars = 1000;
        private static string PreviewCell(string value)
            => value.Length <= MaxCellPreviewChars ? value : value.Substring(0, MaxCellPreviewChars) + " …";

        // ---------------------------------------------------------------- 메뉴 구성

        // 항목만 만들어 필드에 둔다. 메뉴 트리 조립(어느 메뉴에 놓일지)은 Ui/Form1.MainMenu.cs의 ComposeMainMenu가 한다.
        private void BuildFeatureMenus()
        {
            _exportMenu = new ToolStripMenuItem();
            _exportMenu.DropDownItems.Add(MakeItem("Export as CSV…", "CSV로 내보내기…", (_, _) => ExportView(ExportFormat.Csv)));
            _exportMenu.DropDownItems.Add(MakeItem("Export as Markdown…", "Markdown으로 내보내기…", (_, _) => ExportView(ExportFormat.Markdown)));
            _exportMenu.DropDownItems.Add(MakeItem("Export as JSON…", "JSON으로 내보내기…", (_, _) => ExportView(ExportFormat.Json)));
            _exportMenu.DropDownItems.Add(MakeItem("Export as HTML…", "HTML로 내보내기…", (_, _) => ExportView(ExportFormat.Html)));
            RegisterLabel(_exportMenu, "Export View", "현재 보기 내보내기");

            _clipboardOpenMenu = MakeItem("Open from Clipboard", "클립보드에서 열기", async (_, _) => await OpenFromClipboardAsync());

            _gotoRowMenu = MakeCmd("data.goto", (_, _) => GoToRow());
            _advFilterMenu = MakeCmd("data.advFilter", (_, _) => ShowAdvancedFilter());
            BuildEditFeatures();
            BuildAgentFeatures();

            _columnsMenu = MakeItem("Columns…", "컬럼 표시…", (_, _) => ShowColumnChooser());

            _showTypeBadges = _settings.ShowTypeBadges;
            _showBadgesMenu = new ToolStripMenuItem { CheckOnClick = true, Checked = _showTypeBadges };
            _showBadgesMenu.CheckedChanged += (_, _) => { if (!_syncingBadgeToggle) SetShowTypeBadges(_showBadgesMenu.Checked); };
            RegisterLabel(_showBadgesMenu, "Type Badges", "타입 배지");

            // SPSS·SAS 필드 라벨 표시 토글. 라벨 대상 파일이 열렸을 때만 활성.
            _fieldLabelsMenu = new ToolStripMenuItem { CheckOnClick = true, Checked = _settings.ShowFieldLabels, Enabled = false };
            _fieldLabelsMenu.CheckedChanged += (_, _) => { if (!_syncingFieldLabels) SetShowFieldLabels(_fieldLabelsMenu.Checked); };
            RegisterLabel(_fieldLabelsMenu, "Field Labels (SPSS/SAS)", "필드 라벨 (SPSS·SAS)");

            _saveViewMenu = MakeItem("Save Current View", "현재 보기 저장", (_, _) => SaveCurrentView());
            _restoreViewMenu = MakeItem("Restore Saved View", "저장된 보기 복원", async (_, _) => await RestoreSavedViewAsync());
            _perfMenu = MakeItem("Performance Dashboard", "성능 대시보드", (_, _) => ShowPerformanceDashboard());

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

            // 통계 ▸ 기본 분석(하위 메뉴) — 아래 고급 통계 항목들과 같은 "통계" 메뉴에 놓인다.
            _analysisMenu = new ToolStripMenuItem();
            _analysisMenu.DropDownItems.Add(MakeItem("Descriptive Statistics…", "기술통계…", (_, _) => AnalyzeDescriptives()));
            _analysisMenu.DropDownItems.Add(MakeItem("Frequency Table…", "빈도분석…", (_, _) => AnalyzeFrequency()));
            _analysisMenu.DropDownItems.Add(MakeItem("Numeric Distribution…", "수치 분포…", (_, _) => AnalyzeDistribution()));
            _analysisMenu.DropDownItems.Add(MakeItem("Date Histogram…", "날짜 히스토그램…", (_, _) => AnalyzeDateHistogram()));
            _analysisMenu.DropDownItems.Add(MakeItem("Find Duplicates…", "중복 찾기…", (_, _) => AnalyzeDuplicates()));
            _analysisMenu.DropDownItems.Add(MakeItem("Group By…", "그룹별 집계…", (_, _) => AnalyzeGroupBy()));
            _analysisMenu.DropDownItems.Add(new ToolStripSeparator());
            _analysisMenu.DropDownItems.Add(MakeItem("Correlation…", "상관분석…", (_, _) => AnalyzeCorrelation()));
            _analysisMenu.DropDownItems.Add(MakeItem("Independent t-test…", "독립표본 t검정…", (_, _) => AnalyzeIndependentTTest()));
            _analysisMenu.DropDownItems.Add(MakeItem("Paired t-test…", "대응표본 t검정…", (_, _) => AnalyzePairedTTest()));
            _analysisMenu.DropDownItems.Add(MakeItem("One-way ANOVA…", "일원배치 분산분석…", (_, _) => AnalyzeOneWayAnova()));
            _analysisMenu.DropDownItems.Add(MakeItem("Chi-square…", "카이제곱 검정…", (_, _) => AnalyzeChiSquare()));
            _analysisMenu.DropDownItems.Add(MakeItem("Normality Test (Shapiro-Wilk)…", "정규성 검정(Shapiro-Wilk)…", (_, _) => AnalyzeNormality()));
            RegisterLabel(_analysisMenu, "Basics", "기본 분석");

            _pivotTableMenu = MakeItem("Pivot Table…", "피벗테이블…", (_, _) => OpenPivotBuilder(chartTab: false));
            _pivotChartMenu = MakeItem("Pivot Chart…", "피벗차트…", (_, _) => OpenPivotBuilder(chartTab: true));

            // 시각화(이슈 #19): 차트 빌더 + 차트 종류 프리셋(분포 · 관계 · 범주/시계열 순) + 피벗차트. 항목 하나짜리 하위 메뉴는 두지 않는다.
            _vizMenu = new ToolStripMenuItem { Name = "visualizationMenu" };
            _vizMenu.DropDownItems.Add(MakeItem("Chart Builder…", "차트 빌더…", (_, _) => OpenChartBuilder(ChartKind.Histogram)));
            _vizMenu.DropDownItems.Add(new ToolStripSeparator());
            _vizMenu.DropDownItems.Add(MakeItem("Histogram · Density…", "히스토그램·밀도…", (_, _) => OpenChartBuilder(ChartKind.Histogram)));
            _vizMenu.DropDownItems.Add(MakeItem("Box Plot (groups)…", "박스플롯(그룹)…", (_, _) => OpenChartBuilder(ChartKind.BoxPlot)));
            _vizMenu.DropDownItems.Add(MakeItem("Q-Q Plot…", "Q-Q 플롯…", (_, _) => OpenChartBuilder(ChartKind.QqPlot)));
            _vizMenu.DropDownItems.Add(new ToolStripSeparator());
            _vizMenu.DropDownItems.Add(MakeItem("Scatter · Regression…", "산점도·회귀…", (_, _) => OpenChartBuilder(ChartKind.Scatter)));
            _vizMenu.DropDownItems.Add(MakeItem("Correlation Heatmap…", "상관 히트맵…", (_, _) => OpenChartBuilder(ChartKind.CorrelationHeatmap)));
            _vizMenu.DropDownItems.Add(new ToolStripSeparator());
            _vizMenu.DropDownItems.Add(MakeItem("Pareto…", "파레토…", (_, _) => OpenChartBuilder(ChartKind.Pareto)));
            _vizMenu.DropDownItems.Add(MakeItem("Time Series · Moving Avg…", "시계열·이동평균…", (_, _) => OpenChartBuilder(ChartKind.TimeSeries)));
            _vizMenu.DropDownItems.Add(new ToolStripSeparator());
            _vizMenu.DropDownItems.Add(_pivotChartMenu);
            RegisterLabel(_vizMenu, "Visualization", "시각화");

            // 데이터 품질(이슈 #26): 전수 프로파일 + 발견 패널(필터 칩·행 점프 루프) + 키 유일성 + 사용자 규칙(JSON) + 증거 보고서.
            // 점수 게이지·내장 의료 규칙 팩은 두지 않는다.
            _qualityMenu = new ToolStripMenuItem { Name = "qualityMenu" };
            _qualityMenu.DropDownItems.Add(MakeCmd("quality.profile", async (_, _) => await RunQualityProfileAsync()));
            _qualityPanelMenu = MakeItem("Findings Panel", "검사 결과 패널", (_, _) => ToggleQualityPanel());
            _qualityMenu.DropDownItems.Add(_qualityPanelMenu);
            _qualityMenu.DropDownItems.Add(new ToolStripSeparator());
            _qualityMenu.DropDownItems.Add(MakeItem("Key Uniqueness…", "키 유일성 검사…", async (_, _) => await RunKeyUniquenessAsync()));
            _qualityMenu.DropDownItems.Add(MakeItem("Referential Integrity Check…", "참조 무결성 검사…", async (_, _) => await RunReferentialIntegrityAsync()));
            _qualityMenu.DropDownItems.Add(MakeItem("Validation Rules…", "타당성 규칙…", async (_, _) => await ShowQualityRulesAsync()));
            _qualityMenu.DropDownItems.Add(MakeItem("Conformance Profile…", "적합성 프로파일…", async (_, _) => await ShowConformanceProfileAsync()));
            _qualityMenu.DropDownItems.Add(MakeItem("Import DQD Results…", "DQD 결과 가져오기…", async (_, _) => await ImportDqdResultsAsync()));
            _qualityMenu.DropDownItems.Add(new ToolStripSeparator());
            _qualityMenu.DropDownItems.Add(MakeItem("Export Quality Report…", "품질 보고서 내보내기…", (_, _) => ExportQualityReport()));
            _qualityMenu.DropDownItems.Add(MakeItem("Compare with Baseline Snapshot…", "기준선 스냅샷과 비교…",
                async (_, _) => await CompareQualityBaselineAsync()));
            RegisterLabel(_qualityMenu, "Quality", "품질");

            // 통계 메뉴 = 기본 분석 하위 메뉴 + 고급 통계 하위 메뉴들(BuildAdvancedStatsMenu가 _advMenu에 채운다).
            BuildAdvancedStatsMenu();
            _advMenu!.DropDownItems.Insert(0, new ToolStripSeparator());
            _advMenu.DropDownItems.Insert(0, _analysisMenu);

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

            // 우클릭 메뉴(셀 · 컬럼 헤더 · 행 헤더)는 Ui/Form1.ContextMenus.cs가 위치별로 새로 만든다.
            grid.CellContextMenuStripNeeded += OnCellContextMenuStripNeeded;

            // 행/열 복사(편집 ▸ 행·컬럼 하위 메뉴)
            _copyColMenu = MakeItem("Copy Column", "컬럼 복사", async (_, _) => await CopyCurrentColumnAsync());
            _copyRowMenu = MakeItem("Copy Row", "행 복사", (_, _) => CopyCurrentRow());

            // 인스펙터(상세 패널) 복사 버튼 — 헤더 우측에 얹음
            _inspectorCopyText = InspectorButton("TEXT", 116, (_, _) => CopyInspectorText());
            _inspectorCopyJson = InspectorButton("JSON", 60, (_, _) => CopyInspectorJson());
            outerSplit.Panel2.Controls.Add(_inspectorCopyText);
            outerSplit.Panel2.Controls.Add(_inspectorCopyJson);
            _inspectorCopyText.BringToFront();
            _inspectorCopyJson.BringToFront();
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

        // Form1.ApplyLocalization()에서 호출됨(부분 클래스 훅). 등록된 모든 메뉴 항목 + 툴바를 현재 언어로 다시 라벨링한다.
        private void LocalizeFeatureMenus()
        {
            foreach (var (item, en, ko) in _featureLabels)
                item.Text = LT(en, ko);
            LocalizeToolbar();
            _qualityPanel?.Relocalize(); // 1회 생성·캐시되는 패널은 언어 전환 시 수동 재현지화(이슈 #26)
            LocalizeEditButtons();
            LocalizeAgentUi();
            LocalizeWorkspaceUi();
        }

        // 보기 메뉴 항목을 토글하고, 설정 저장 + 헤더 다시 그림.
        private void SetShowTypeBadges(bool show)
        {
            _showTypeBadges = show;
            _syncingBadgeToggle = true;
            if (_showBadgesMenu is not null) _showBadgesMenu.Checked = show;
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
            if (_pivotTableMenu is not null) _pivotTableMenu.Enabled = ready;
            if (_perfMenu is not null) _perfMenu.Enabled = _doc is not null;
            if (_analysisMenu is not null) _analysisMenu.Enabled = ready;
            if (_vizMenu is not null) _vizMenu.Enabled = ready;
            if (_qualityMenu is not null) _qualityMenu.Enabled = ready;
            if (_advMenu is not null) _advMenu.Enabled = ready;
            UpdateEditState();
            // 필드 라벨 토글: 문서 준비 + SPSS·SAS + 재임포트 중이 아닐 때만.
            bool labelToggleReady = ready && _workbook?.SupportsFieldLabels == true && !_reimporting;
            if (_fieldLabelsMenu is not null) _fieldLabelsMenu.Enabled = labelToggleReady;

            bool open = _doc is not null && !_busy;
            if (_copyMenu is not null) _copyMenu.Enabled = open;
            if (_copyRowMenu is not null) _copyRowMenu.Enabled = open;
            if (_copyColMenu is not null) _copyColMenu.Enabled = open;
            if (_inspectorCopyText is not null) _inspectorCopyText.Enabled = open;
            if (_inspectorCopyJson is not null) _inspectorCopyJson.Enabled = open;
        }

        // ---------------------------------------------------------------- 컬럼 타입 태그 (A)

        // 타입 추론·수동 타입 검증용 표본(최대 10k행) 수집.
        private List<string[]> CollectTypeSampleRows() => _doc is null ? new List<string[]>() : CollectTypeSampleRows(_doc);

        private static List<string[]> CollectTypeSampleRows(VirtualCsvDocument doc)
        {
            const int sampleCap = 10_000;
            int n = Math.Min(sampleCap, doc.DataRowsAvailable);
            var sample = new List<string[]>(n);
            for (int i = 0; i < n; i++)
            {
                try { sample.Add(doc.GetDataRowUncached(i)); } catch { }
            }
            return sample;
        }

        // OnIndexingComplete()에서 호출. 표본 행으로 컬럼 추론 타입을 계산해 헤더 툴팁에 표시.
        private void ComputeColumnTypeTags()
        {
            if (_doc is null) { _columnSummaries = Array.Empty<ColumnSummary>(); return; }
            _columnSummaries = ComputeColumnSummaries(_doc, _workbook, _currentSheetIndex, _manualTypeOverrides, SourceColumnOf);
            ApplyColumnTooltips();

            // 타입 배지가 생겼으니 헤더를 다시 그린다.
            grid.Invalidate();
        }

        /// <summary>
        /// 문서의 컬럼 요약(추론 타입 포함). 화면 상태에 의존하지 않아 백그라운드 탭도 계산할 수 있다.
        /// sourceOf = 표시 컬럼 → 파일 컬럼 번호(컬럼 이동·삭제·삽입이 없으면 항등).
        /// </summary>
        private static ColumnSummary[] ComputeColumnSummaries(VirtualCsvDocument doc, Import.WorkbookSession? wb, int sheetIndex,
            IReadOnlyDictionary<int, ColumnValueType> manualOverrides, Func<int, int> sourceOf)
        {
            var sample = CollectTypeSampleRows(doc);
            var summaries = ColumnStatisticsBuilder.Summarize(doc.Header, sample).Columns.ToArray();

            // SAS/SPSS가 파일에 명시한 선언 타입이 있으면 추론을 오버라이드(지정된 타입으로 매칭).
            var hints = wb?.ColumnHints(sheetIndex);
            if (hints is not null)
            {
                for (int c = 0; c < summaries.Length; c++)
                {
                    int source = sourceOf(c); // 컬럼을 옮기거나 삭제·삽입했어도 파일의 그 컬럼의 선언 타입을 쓴다
                    if (source < 0 || source >= hints.Count) continue;
                    var hint = hints[source];
                    if (hint is null) continue;
                    summaries[c] = summaries[c] with
                    {
                        InferredType = hint.Type,
                        CurrencySymbol = hint.CurrencySymbol,
                        PercentIsFraction = hint.PercentIsFraction,
                        // 범주/순서형 등 비숫자로 재지정되면 코드의 평균 등 무의미한 수치 통계를 제거.
                        Numeric = hint.Type.IsNumeric() ? summaries[c].Numeric : null,
                    };
                }
            }

            // 사용자가 수동 지정한 타입(이슈 #12)은 추론·선언 힌트 모두보다 우선.
            foreach (var kv in manualOverrides)
            {
                int c = kv.Key;
                if (c < 0 || c >= summaries.Length) continue;
                summaries[c] = summaries[c] with
                {
                    InferredType = kv.Value,
                    Numeric = kv.Value.IsNumeric() ? summaries[c].Numeric : null,
                };
            }
            return summaries;
        }

        // 헤더 툴팁(타입·고유값·빈값). 탭을 바꿔 컬럼을 다시 만든 뒤에도 쓴다.
        private void ApplyColumnTooltips()
        {
            for (int c = 0; c < grid.Columns.Count && c < _columnSummaries.Length; c++)
            {
                var s = _columnSummaries[c];
                string manual = _manualTypeOverrides.ContainsKey(c) ? LT(" (manual)", " (수동)") : "";
                grid.Columns[c].ToolTipText = LT(
                    $"Type: {s.InferredType.DisplayName()}{manual} · unique {s.UniqueCount:N0} · nulls {s.NullCount:N0}",
                    $"타입: {s.InferredType.DisplayName()}{manual} · 고유값 {s.UniqueCount:N0} · 빈값 {s.NullCount:N0}");
            }
        }

        // ---------------------------------------------------------------- 컬럼 타입 수동 변경 (이슈 #12)

        // 수동 지정 가능한 대상 타입(Empty 제외).
        private static readonly ColumnValueType[] ManualTypeTargets =
        {
            ColumnValueType.Integer, ColumnValueType.Float, ColumnValueType.Currency, ColumnValueType.Percent,
            ColumnValueType.Scientific, ColumnValueType.Date, ColumnValueType.DateTime, ColumnValueType.Time,
            ColumnValueType.Boolean, ColumnValueType.Categorical, ColumnValueType.Ordinal,
            ColumnValueType.Identifier, ColumnValueType.String,
        };

        // 변환 규칙(허용/제한적/차단)에 따라 수동 타입을 적용. 제한적 전환은 표본 검증 결과를 보여주고 확인받는다.
        private async void ApplyManualType(int col, ColumnValueType target)
        {
            if (_doc is null || _busy || col >= _columnSummaries.Length) return;
            var current = _columnSummaries[col].InferredType;
            if (target == current) return;
            var policy = ColumnTypeConversion.Classify(current, target);
            if (policy == TypeChangePolicy.Blocked) return; // 메뉴에서 비활성이므로 방어적 경로

            string header = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";
            if (policy == TypeChangePolicy.RequiresValidation)
            {
                var sample = CollectTypeSampleRows();
                var values = new List<string>(sample.Count);
                foreach (var row in sample) if (col < row.Length) values.Add(row[col]);
                var v = ColumnTypeConversion.Validate(target, values);

                var sb = new StringBuilder();
                sb.AppendLine(LT(
                    $"Change \"{header}\": {current.DisplayName()} → {target.DisplayName()}?",
                    $"\"{header}\" 타입 변경: {current.DisplayName()} → {target.DisplayName()}?"));
                sb.AppendLine();
                sb.AppendLine(LT(
                    $"Sample check: {v.ValidCount:N0} of {v.SampleCount:N0} values parse as {target.DisplayName()}.",
                    $"표본 검증: {v.SampleCount:N0}개 중 {v.ValidCount:N0}개가 {target.DisplayName()}(으)로 해석됩니다."));
                if (!v.AllValid)
                {
                    sb.AppendLine(LT(
                        $"{v.FailCount:N0} value(s) will not parse. Examples:",
                        $"{v.FailCount:N0}개 값은 해석되지 않습니다. 예시:"));
                    foreach (var ex in v.FailingExamples)
                        sb.AppendLine("  · " + (ex.Length > 40 ? ex[..39] + "…" : ex));
                }
                if (MessageBox.Show(this, sb.ToString(), LT("Change Column Type", "컬럼 타입 변경"),
                        MessageBoxButtons.YesNo,
                        v.AllValid ? MessageBoxIcon.Question : MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            _manualTypeOverrides[col] = target;
            // 타입이 바뀌면 그 컬럼의 기존 필터는 의미가 달라지므로 해제하고 뷰를 재구성.
            bool hadFilter = _columnFilters.HasFilterFor(col);
            if (hadFilter) _columnFilters.Remove(col);
            ComputeColumnTypeTags();
            if (hadFilter) await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
            statusLabel.Text = LT(
                $"Column type changed: {header} → {target.DisplayName()}",
                $"컬럼 타입 변경: {header} → {target.DisplayName()}");
        }

        private void ResetManualType(int col)
        {
            if (!_manualTypeOverrides.Remove(col)) return;
            ComputeColumnTypeTags();
            string header = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";
            statusLabel.Text = LT(
                $"Column type reset to auto-detected: {header}",
                $"컬럼 타입 자동 감지로 복원: {header}");
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

        internal static string TypeAbbrev(ColumnValueType type) => type switch
        {
            ColumnValueType.Integer => "INT",
            ColumnValueType.Float => "FLT",
            ColumnValueType.Currency => "CUR",
            ColumnValueType.Percent => "PCT",
            ColumnValueType.Scientific => "SCI",
            ColumnValueType.Date => "DATE",
            ColumnValueType.DateTime => "DTTM",
            ColumnValueType.Time => "TIME",
            ColumnValueType.Boolean => "BOOL",
            ColumnValueType.Categorical => "CAT",
            ColumnValueType.Ordinal => "ORD",
            ColumnValueType.Identifier => "ID",
            ColumnValueType.String => "STR",
            ColumnValueType.Empty => "—",
            _ => "STR"
        };

        internal static Color TypeColor(ColumnValueType type) => type switch
        {
            ColumnValueType.Integer => Color.FromArgb(46, 111, 176),     // 파랑
            ColumnValueType.Float => Color.FromArgb(27, 158, 119),       // 청록
            ColumnValueType.Currency => Color.FromArgb(0, 137, 123),     // 짙은 청록(통화)
            ColumnValueType.Percent => Color.FromArgb(56, 142, 60),      // 초록(퍼센트)
            ColumnValueType.Scientific => Color.FromArgb(0, 121, 145),   // 하늘청록(지수)
            ColumnValueType.Date => Color.FromArgb(123, 94, 167),        // 보라
            ColumnValueType.DateTime => Color.FromArgb(101, 79, 140),    // 진보라
            ColumnValueType.Time => Color.FromArgb(150, 111, 196),       // 연보라
            ColumnValueType.Boolean => Color.FromArgb(210, 105, 30),     // 주황
            ColumnValueType.Categorical => Color.FromArgb(184, 134, 11), // 황금
            ColumnValueType.Ordinal => Color.FromArgb(160, 120, 30),     // 짙은 황금(순서형)
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
            => c < _columnSummaries.Length && _columnSummaries[c].InferredType.IsNumeric();

        // 통화·퍼센트 컬럼의 표시 스킨(정렬·통계·복사는 밑값 사용, 표시 전용). 로직은 CellDisplay에서 검증.
        private string FormatDisplayCell(int col, string raw)
        {
            if (col >= _columnSummaries.Length) return raw;
            var s = _columnSummaries[col];
            return CellDisplay.Format(s.InferredType, s.CurrencySymbol, s.PercentIsFraction, raw);
        }

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

        private CancellationTokenSource? _analysisCts;
        private Task? _analysisTask;

        private Task<AnalysisSnapshot?> GatherAnalysisSnapshotAsync(VirtualCsvDocument doc)
            => RunAnalysisOperationAsync(doc, (source, cancellation) => AnalysisSnapshot.Collect(source, cancellation));

        /// <param name="columns">
        /// The columns the analysis reads. Only these cells are kept in <see cref="AnalysisWork.Rows"/>; every other
        /// cell is null (rows keep their original width so column indexes stay valid).
        /// </param>
        private async Task RunAnalysisAsync(IReadOnlyCollection<int> columns, Action<AnalysisWork> compute, bool withSourceRows = false)
        {
            if (_doc is null || _closing || _busy) return;
            var doc = _doc;
            var labels = ColumnLabels().ToArray();
            var report = await RunAnalysisOperationAsync(doc, (source, cancellation) =>
            {
                var snapshot = AnalysisSnapshot.Collect(source, cancellation, columns);
                List<(string[], long)>? sourceRows = null;
                if (withSourceRows)
                {
                    sourceRows = new(snapshot.Rows.Count);
                    for (int i = 0; i < snapshot.Rows.Count; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        sourceRows.Add((snapshot.Rows[i], doc.GetSourceRowNumber(i)));
                    }
                }
                var work = new AnalysisWork(snapshot.Rows, labels, cancellation, sourceRows);
                compute(work);
                cancellation.ThrowIfCancellationRequested();
                return work.Report is { } result ? result with
                {
                    Body = LT($"Scope: entire current view ({snapshot.Rows.Count:N0} rows)\n\n",
                        $"분석 범위: 현재 뷰 전체 ({snapshot.Rows.Count:N0}행)\n\n") + result.Body
                } : null;
            });
            if (report is null || _closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;
            if (report.Chart is { } chart) ShowResultWithChart(report.Title, report.Body, chart, report.Columns);
            else ShowResult(report.Title, report.Body);
        }

        private async Task<T?> RunAnalysisOperationAsync<T>(VirtualCsvDocument doc,
            Func<IReadOnlyList<string[]>, CancellationToken, T?> operation) where T : class
        {
            if (_closing || _drainDepth > 0 || _busy || !ReferenceEquals(doc, _doc)) return null;
            var source = doc.SnapshotViewRows();
            using var cancellation = new CancellationTokenSource();
            _analysisCts = cancellation;
            SetBusy(true);
            statusLabel.Text = LT("Reading analysis data…", "분석 데이터 읽는 중…");
            try
            {
                var worker = Task.Run(() => operation(source, cancellation.Token));
                _analysisTask = worker;
                var result = await worker;
                return _closing || IsDisposed || cancellation.IsCancellationRequested || !ReferenceEquals(doc, _doc)
                    ? null : result;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                if (!_closing && !IsDisposed && !cancellation.IsCancellationRequested)
                {
                    if (ex is AnalysisMemoryLimitException)
                    {
                        // 바쁨 표시를 푼 뒤(finally 다음) 안내를 띄운다: 설정 대화 상자가 분석 중 상태에서 열리지 않게.
                        BeginInvoke(() => ShowMemoryBudgetExceeded(this,
                            LT("The complete analysis data exceeds the memory budget. Filter the rows and retry. No partial result was produced.",
                                "전체 분석 데이터가 메모리 예산을 초과했습니다. 행 필터를 적용한 뒤 다시 시도하세요. 일부 행만 분석한 결과는 생성하지 않았습니다.")));
                    }
                    else MessageBox.Show(this, Stats.ErrorText.Localize(ex.Message), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return null;
            }
            finally
            {
                if (ReferenceEquals(_analysisCts, cancellation)) _analysisCts = null;
                if (!_closing && !IsDisposed) { SetBusy(false); UpdateFilterStatus(); }
            }
        }

        private void ShowResult(string title, string body)
        {
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
                try { b.Top = Math.Max(0, (detailHeaderLabel.Height - b.Height) / 2); b.Left = Math.Max(0, outerSplit.Panel2.ClientSize.Width - rightOffset); } catch { }
            }
            outerSplit.Panel2.ClientSizeChanged += (_, _) => Reposition();
            detailHeaderLabel.SizeChanged += (_, _) => Reposition();
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
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;
            string name = col < grid.Columns.Count ? grid.Columns[col].HeaderText : $"Column{col + 1}";
            Rectangle rect = grid.GetCellDisplayRectangle(col, -1, true);
            Point screenPt = grid.PointToScreen(new Point(rect.Left, rect.Bottom));

            var type = col < _columnSummaries.Length ? _columnSummaries[col].InferredType : ColumnValueType.String;

            // 숫자 범위(Integer/Float/Currency/Percent/Scientific) — 고유값이 적으면 체크박스가 더 유용하므로 폴백.
            if (type.IsNumeric() && !IsLowCardinality(col))
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
            bool useTextFilter = false;
            _opCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _opCts = cts;
            try
            {
                var load = Task.Run(() => doc.DistinctValues(col, withinCurrentView: false, cts.Token,
                    maxDistinctValues: 100_000, maxValueCharacters: 8_000_000));
                _opTask = load;
                distinct = await load;
            }
            catch (DistinctValueLimitException)
            {
                distinct = Array.Empty<(string, int)>();
                useTextFilter = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!IsDisposed) MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                if (ReferenceEquals(_opCts, cts)) _opCts = null;
                if (!IsDisposed) { SetBusy(false); UpdateFilterStatus(); }
            }
            if (IsDisposed || cts.IsCancellationRequested || !ReferenceEquals(doc, _doc)) return;

            if (useTextFilter)
            {
                MessageBox.Show(this, LT("This column has too many values for a checklist. Use contains, equals, or a list of values; the filter still scans all rows.",
                    "이 컬럼은 고유값 목록이 너무 큽니다. 포함·일치·값 목록 조건을 사용하세요. 필터는 전체 행에 적용됩니다."),
                    name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                using var popup = new ColumnFilterPopup(name, _columnFilters.TextFilters.FirstOrDefault(f => f.Column == col), _palette);
                if (!popup.ShowAt(this, screenPt)) return;
                _columnFilters.SetText(col, popup.TextOp, popup.TextValue, popup.TextCaseSensitive);
                await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
                grid.Invalidate();
                return;
            }

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
                Action? edit = _valueConditions[idx].expr is not null ? () => EditValueCondition(idx) : null;
                string label = _valueConditions[idx].desc;
                // 정규식 시간 초과로 불일치 처리된 셀이 있으면 ⚠ 표시(이미 오래된 식으로 ⚠가 붙은 칩은 그대로).
                if ((AdvancedFilterExpression.TimeoutsOf(_valueConditions[idx].pred)?.Count ?? 0) > 0
                    && !label.EndsWith("⚠", StringComparison.Ordinal))
                    label += " ⚠";
                list.Add((label, () => _valueConditions.RemoveAt(idx), edit));
            }
            if (_doc is not null)
                foreach (var (col, text) in _columnFilters.DescribeEntries(_doc.Header))
                {
                    int c = col;
                    // 정규식 필터가 시간 초과(셀이 불일치로 처리됨)이거나 컴파일 실패(어떤 행도 통과 못 함)면 ⚠.
                    bool warn = _columnFilters.TimeoutCount(c) > 0 || _columnFilters.RegexErrorFor(c) is not null;
                    list.Add((warn ? text + " ⚠" : text, () => _columnFilters.Remove(c), () => OpenColumnFilter(c)));
                }
            return list;
        }

        private void RebuildFilterChips()
        {
            var chips = ActiveFilterChips();
            if (_chipsBar is null)
            {
                if (chips.Count == 0) return; // 표시할 게 없으면 생성도 미룬다
                // 그리드 패널 안에서 Fill 위에 Top을 얹으면 런타임 재배치가 불안정해 헤더를 덮는다.
                // 그래서 폼 최상위에서 outerSplit 바로 위에 도킹한다(툴바·메뉴와 동일한 검증된 방식).
                _chipsBar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    Height = 34,
                    AutoSize = false,
                    WrapContents = false,
                    AutoScroll = true,
                    Padding = new Padding(4, 4, 4, 4),
                    Margin = new Padding(0),
                    BackColor = _palette.Window, // 앱 기본 배경(테마 전환 시에도 ApplyToControls와 일치)
                };
                Controls.Add(_chipsBar);
                // outerSplit(=Fill) 바로 앞에 두어, 메뉴·툴바 아래·콘텐츠 위의 띠가 되게 한다.
                Controls.SetChildIndex(_chipsBar, Controls.GetChildIndex(MainContent) + 1);
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
            PerformLayout(); // 칩 바 표시/숨김에 따라 콘텐츠가 즉시 재배치되도록 강제.
        }

        // 칩과 토글이 같은 높이를 쓰도록 공용 계산.
        private int ChipRowHeight() => TextRenderer.MeasureText("Ag", Font).Height + LogicalToDeviceUnits(7);

        private Control MakeModeToggle()
        {
            string label = _filterMatchAny ? LT("ANY (OR)", "하나라도(OR)") : LT("ALL (AND)", "모두(AND)");
            var boldFont = new Font(Font, FontStyle.Bold);
            int h = ChipRowHeight();                                  // 칩과 동일 높이
            int w = TextRenderer.MeasureText(label, boldFont).Width + 16;
            var b = new Button
            {
                Text = label,
                AutoSize = false,
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = _palette.Surface,
                ForeColor = _palette.Accent,
                Margin = new Padding(2),
                Padding = new Padding(0),
                Cursor = Cursors.Hand,
                TabStop = false,
                Font = boldFont,
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
            // 중첩 AutoSize 컨테이너는 측정 버그(기본 100px 잔존)를 유발하므로, 텍스트 폭으로
            // 측정한 고정 크기 Panel + 좌표 지정 라벨로 결정적으로 그린다.
            var boldFont = new Font(Font, FontStyle.Bold);
            int h = ChipRowHeight();
            int textW = TextRenderer.MeasureText(label, Font).Width;
            int xW = TextRenderer.MeasureText("✕", boldFont).Width;
            int padL = LogicalToDeviceUnits(8), gap = LogicalToDeviceUnits(5), padR = LogicalToDeviceUnits(8);

            var chip = new Panel
            {
                Size = new Size(padL + textW + gap + xW + padR, h),
                Margin = new Padding(2),
                BackColor = _palette.Accent,
            };

            var text = new Label
            {
                Text = label,
                AutoSize = false,
                Bounds = new Rectangle(padL, 0, textW + 2, h),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            if (edit is not null) { text.Cursor = Cursors.Hand; text.Click += (_, _) => edit(); } // 클릭 시 해당 필터 팝업 재오픈

            var close = new Label
            {
                Text = "✕",
                AutoSize = false,
                Bounds = new Rectangle(padL + textW + gap, 0, xW + padR, h),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Font = boldFont,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            close.Click += async (_, _) =>
            {
                remove();
                await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
                grid.Invalidate();
                // RebuildFilterAsync → UpdateFilterStatus → RebuildFilterChips() 가 바를 다시 그린다.
            };

            chip.Controls.Add(close);
            chip.Controls.Add(text);
            return chip;
        }

        // ---------------------------------------------------------------- 멀티시트(엑셀/SAS) 워크북

        private Import.WorkbookSession? _workbook;
        private FlowLayoutPanel? _sheetTabs;

        private void DisposeWorkbook()
        {
            _workbook?.Dispose();
            _workbook = null;
            HideSheetTabs();
        }

        // 시트 전환: 현재 문서를 닫고 해당 시트의 임시 CSV를 연다.
        private async void SwitchSheet(int index) => await SwitchSheetAsync(index);

        // 같은 일을 기다릴 수 있게 한 형태(작업 공간 탐색기가 "이 시트 열기"에 쓴다).
        internal async Task SwitchSheetAsync(int index)
        {
            if (_workbook is null || _busy) return;
            if (index < 0 || index >= _workbook.SheetNames.Count) return;
            if (index != _currentSheetIndex && !ConfirmDiscardEdits()) return;
            await CancelAndDrainAsync();
            var old = _doc; _doc = null; old?.Dispose();
            ResetView();
            _hiddenColumns.Clear();
            LoadSheet(index);
        }

        private void LoadSheet(int index)
        {
            if (_workbook is null) return;
            _currentSheetIndex = index;
            string title = $"{Path.GetFileName(_workbook.SourcePath)}  [{_workbook.SheetNames[index]}]";
            LoadDocument(_workbook.CsvPath(index), title);
            HighlightSheetTab(index);
        }

        // SPSS·SAS 필드 라벨 표시 토글. 라벨 대상 워크북이면 그 모드로 재임포트·재로드하고,
        // 성공했을 때만 설정을 영속화한다. 실패·무의미한 토글에는 체크 상태를 실제 모드로 되돌린다.
        private async void SetShowFieldLabels(bool show)
        {
            // 라벨 대상 파일이 아니면 설정만 저장(다음 열기 기본값). 메뉴는 보통 비활성이라 방어적 경로.
            if (_workbook is null || !_workbook.SupportsFieldLabels)
            {
                _settings.ShowFieldLabels = show;
                _settings.Save();
                return;
            }
            if (_workbook.ShowLabels == show) return; // 변화 없음
            if (_reimporting || _busy)                // 진행 중이면 체크를 실제 모드로 되돌리고 무시
            {
                SyncFieldLabelsChecked(_workbook.ShowLabels);
                return;
            }

            _reimporting = true;
            UpdateFieldLabelsMenu();
            string path = _workbook.SourcePath;
            int sheet = _currentSheetIndex;
            try
            {
                statusLabel.Text = LT("Importing…", "불러오는 중…");
                // 새 워크북을 먼저 만든 뒤에야 기존 문서를 교체 → 실패해도 현재 화면이 보존된다.
                var wb = await Task.Run(() => Import.WorkbookSession.Create(path, show));
                await CancelAndDrainAsync();
                var old = _doc; _doc = null; old?.Dispose();
                ResetView();
                _hiddenColumns.Clear();
                DisposeWorkbook();
                _workbook = wb;
                BuildSheetTabs(wb);
                LoadSheet(Math.Min(sheet, wb.SheetNames.Count - 1));
                _settings.ShowFieldLabels = show;   // 성공 시에만 영속화
                _settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Loc.T("Title_OpenFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                statusLabel.Text = Loc.T("Status_OpenFailed");
                SyncFieldLabelsChecked(_workbook?.ShowLabels ?? show); // 체크 되돌림
            }
            finally
            {
                _reimporting = false;
                UpdateFieldLabelsMenu();
            }
        }

        // 체크 상태를 코드로 설정(핸들러 재진입 억제).
        private void SyncFieldLabelsChecked(bool value)
        {
            _syncingFieldLabels = true;
            if (_fieldLabelsMenu is not null) _fieldLabelsMenu.Checked = value;
            _syncingFieldLabels = false;
        }

        // 라벨 토글 메뉴의 체크 상태를 현재 워크북(없으면 설정)에 맞춘다. 활성/비활성은 UpdateFeatureMenuState가 담당.
        private void UpdateFieldLabelsMenu()
        {
            bool capable = _workbook?.SupportsFieldLabels == true;
            SyncFieldLabelsChecked(capable ? _workbook!.ShowLabels : _settings.ShowFieldLabels);
            UpdateFeatureMenuState();
        }

        private void BuildSheetTabs(Import.WorkbookSession wb)
        {
            EnsureSheetTabs();
            foreach (Control c in _sheetTabs!.Controls.Cast<Control>().ToArray()) c.Dispose();
            _sheetTabs.SuspendLayout();
            _sheetTabs.Controls.Clear();
            for (int i = 0; i < wb.SheetNames.Count; i++)
            {
                int idx = i;
                var b = new Button
                {
                    Text = wb.SheetNames[i],
                    AutoSize = true,
                    FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(1),
                    Padding = new Padding(8, 1, 8, 1),
                    BackColor = _palette.Surface,
                    ForeColor = _palette.Text,
                    TabStop = false,
                    Cursor = Cursors.Hand,
                };
                b.FlatAppearance.BorderColor = _palette.Border;
                b.Click += (_, _) => SwitchSheet(idx);
                _sheetTabs.Controls.Add(b);
            }
            _sheetTabs.Visible = wb.SheetNames.Count > 1; // 시트가 하나면 탭을 숨긴다
            _sheetTabs.ResumeLayout();
            PerformLayout();
        }

        private void EnsureSheetTabs()
        {
            if (_sheetTabs is not null) return;
            _sheetTabs = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                AutoScroll = true,
                WrapContents = false,
                BackColor = _palette.Window,
                Padding = new Padding(2, 1, 2, 1),
            };
            // 그리드가 있는 영역(splitContainer1.Panel2) 안에 둔다: 폼 최상위에 두면 왼쪽 탐색기·오른쪽 AI 패널 밑까지 창 폭 전체로 펼쳐진다.
            splitContainer1.Panel2.Controls.Add(_sheetTabs);
            OrderGridAreaControls();
        }

        // 그리드 영역의 도킹 순서(앞 = 안쪽): 그리드(Fill) · 시트 탭(바로 아래) · 패싯(오른쪽, 검사 결과 위쪽 전체 높이) · 검사 결과(맨 아래, 영역 전체 폭).
        // 도킹은 z-순서의 뒤쪽 컨트롤부터 자리를 잡으므로, 패싯이 시트 탭보다 뒤에 있어야 시트 탭이 그리드 아래에만 놓인다.
        private void OrderGridAreaControls()
        {
            var panel = splitContainer1.Panel2;
            int i = 0;
            foreach (Control? c in new Control?[] { grid, _sheetTabs, _facetsPanel, _qualityPanel })
                if (c is not null && ReferenceEquals(c.Parent, panel)) panel.Controls.SetChildIndex(c, i++);
        }

        private void HideSheetTabs()
        {
            if (_sheetTabs is not null) _sheetTabs.Visible = false;
        }

        private void HighlightSheetTab(int index)
        {
            if (_sheetTabs is null) return;
            for (int i = 0; i < _sheetTabs.Controls.Count; i++)
            {
                var b = _sheetTabs.Controls[i];
                bool active = i == index;
                b.BackColor = active ? _palette.Accent : _palette.Surface;
                b.ForeColor = active ? Color.White : _palette.Text;
            }
        }

        // ---------------------------------------------------------------- 패싯 분석 패널 (방안 C)

        private FlowLayoutPanel? _facetsPanel;
        private bool _facetsVisible;
        private CancellationTokenSource? _facetCts;
        private Task? _facetTask;
        private const int FacetSampleCap = 50_000;

        private void BuildFacetsMenuItem()
            => _facetsMenu = MakeCmd("view.facets", (_, _) => ToggleFacets());

        private void ToggleFacets() => SetFacetsVisible(!_facetsVisible);

        /// <summary>
        /// 패싯 패널을 켜거나 끈다. 켜 둔 상태는 문서와 별개로 기억하고, 패널 자체는 문서가 있을 때만 보인다
        /// (시작 때 켜져 있어도 문서가 열릴 때까지 빈 패널을 보이지 않는다).
        /// </summary>
        internal void SetFacetsVisible(bool visible)
        {
            _facetsVisible = visible;
            if (visible) EnsureFacetsPanel();
            SyncFacetsPanel();
            if (visible) BuildFacets();
            else _facetCts?.Cancel();
            RaisePanelChanged(PanelKind.Facets);
        }

        private void SyncFacetsPanel()
        {
            if (_facetsPanel is null) return;
            bool show = _facetsVisible && _doc is not null;
            if (_facetsPanel.Visible != show) _facetsPanel.Visible = show;
        }

        private void EnsureFacetsPanel()
        {
            if (_facetsPanel is not null) return;
            _facetsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 232,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = _palette.Surface,
                Padding = new Padding(2, 2, 2, 2),
                Visible = false,
            };
            splitContainer1.Panel2.Controls.Add(_facetsPanel);
            OrderGridAreaControls();
        }

        // 현재(필터된) 뷰의 표본으로 컬럼별 분포를 다시 계산 → 크로스필터링.
        private async void BuildFacets()
        {
            SyncFacetsPanel();
            if (_closing || _drainDepth > 0 || _facetsPanel is null || !_facetsVisible || _doc is null) return;
            _facetCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _facetCts = cts;
            var doc = _doc;
            var view = doc.SnapshotViewRows();
            var columns = Enumerable.Range(0, doc.ColumnCount)
                .Where(c => !_hiddenColumns.Contains(c) && c < _columnSummaries.Length)
                .Select(c => (Index: c, Type: _columnSummaries[c].InferredType,
                    Name: c < grid.Columns.Count ? grid.Columns[c].HeaderText : $"Column{c + 1}")).ToArray();
            _facetsPanel.Enabled = false;
            try
            {
                var worker = Task.Run(() =>
                {
                    var rows = new List<string[]>();
                    long bytes = 0;
                    for (int i = 0; i < Math.Min(view.Count, FacetSampleCap); i++)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var row = view[i];
                        bytes += 32L + row.Sum(v => 32L + v.Length * 2L);
                        if (bytes > 32L * 1024 * 1024) break;
                        rows.Add(row);
                    }
                    var facets = new List<(string Name, List<(string, int, Action)> Rows)>();
                    foreach (var column in columns)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var result = BuildFacetRows(column.Index, column.Type, rows);
                        if (result.Count > 0) facets.Add((column.Name, result));
                    }
                    return (facets, count: rows.Count);
                }, cts.Token);
                // Include superseded workers: a new file must not dispose the old
                // document until every outstanding reader has finished.
                _facetTask = _facetTask is null || _facetTask.IsCompleted ? worker : Task.WhenAll(_facetTask, worker);
                var result = await worker;
                if (IsDisposed || cts.IsCancellationRequested || !ReferenceEquals(doc, _doc)) return;
                _facetsPanel.SuspendLayout();
                try
                {
                    var old = _facetsPanel.Controls.Cast<Control>().ToArray();
                    _facetsPanel.Controls.Clear();
                    foreach (var control in old) control.Dispose();
                    _facetsPanel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(FacetView.WidthLogical), 0),
                        Text = LT($"Facets: first {result.count:N0} of {view.Count:N0} rows", $"패싯: 전체 {view.Count:N0}행 중 처음 {result.count:N0}행"), ForeColor = _palette.Text });
                    foreach (var facet in result.facets)
                        _facetsPanel.Controls.Add(new FacetView(facet.Name, _palette, facet.Rows));
                }
                finally { _facetsPanel.ResumeLayout(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Facets] {ex}");
                if (!IsDisposed && !cts.IsCancellationRequested && ReferenceEquals(doc, _doc))
                {
                    var old = _facetsPanel.Controls.Cast<Control>().ToArray();
                    _facetsPanel.Controls.Clear();
                    foreach (var control in old) control.Dispose();
                    _facetsPanel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(FacetView.WidthLogical), 0),
                        Text = LT("Unable to calculate facets. Toggle the panel to retry.", "패싯을 계산하지 못했습니다. 패널을 다시 열어 재시도하세요."), ForeColor = _palette.Text });
                }
            }
            finally
            {
                if (ReferenceEquals(_facetCts, cts))
                {
                    _facetCts = null;
                    if (!IsDisposed) _facetsPanel.Enabled = true;
                }
            }
        }

        private List<(string, int, Action)> BuildFacetRows(int col, ColumnValueType type, List<string[]> rows)
        {
            var list = new List<(string, int, Action)>();
            if (type is ColumnValueType.Integer or ColumnValueType.Float)
            {
                var vals = new List<double>();
                foreach (var r in rows)
                    if (col < r.Length && double.TryParse(r[col].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                        vals.Add(d);
                if (vals.Count == 0) return list;
                var dist = CsvAnalytics.NumericDistributionOf(vals, col, 6);
                foreach (var b in dist.Bins)
                {
                    double lo = b.LowerBound, hi = b.UpperBound;
                    list.Add(($"{ShortNum(lo)}–{ShortNum(hi)}", b.Count, () =>
                    {
                        _columnFilters.SetNumericRange(col, lo, hi);
                        _ = ApplyFacetFilterAsync();
                    }));
                }
            }
            else
            {
                var freq = new Dictionary<string, int>();
                foreach (var r in rows)
                {
                    string v = col < r.Length ? r[col] : string.Empty;
                    freq[v] = freq.TryGetValue(v, out int f) ? f + 1 : 1;
                }
                foreach (var kv in freq.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Take(6))
                {
                    string val = kv.Key;
                    string label = val.Length == 0 ? LT("(blank)", "(빈 값)") : val;
                    list.Add((label, kv.Value, () =>
                    {
                        if (val.Length == 0) _columnFilters.SetValues(col, Array.Empty<string>(), includeBlanks: true);
                        else _columnFilters.SetValues(col, new[] { val }, includeBlanks: false);
                        _ = ApplyFacetFilterAsync();
                    }));
                }
            }
            return list;
        }

        private async Task ApplyFacetFilterAsync()
        {
            await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
            grid.Invalidate();
            // RebuildFilterAsync → UpdateFilterStatus → (보이면) BuildFacets 로 크로스필터 갱신.
        }

        private static string ShortNum(double v) => Math.Abs(v) >= 1000 ? v.ToString("N0") : v.ToString("0.##");

        // ---------------------------------------------------------------- 행으로 이동 (D)

        private async void GoToRow()
        {
            if (_doc is null || _doc.DisplayRowCount == 0) return;
            using var dlg = new ParamDialog(LT("Go to Cell", "셀로 이동"), _palette);
            var input = dlg.AddText(LT("Cell (row, R12C3, C3, Name:12)", "셀 (행, R12C3, C3, 이름:12)"));
            dlg.AddNote(LT("120 = row 120 (row-header number) · R120C3 = row 120, column 3 · C3 = column 3 · Name:120 or [Name]120 = column by name + row",
                           "120 = 120행(행 머리글 번호) · R120C3 = 120행 3열 · C3 = 3열 · 이름:120 또는 [이름]120 = 컬럼 이름 + 행"));
            if (!dlg.ShowOk(this)) return;
            try { await GoToAddressAsync(input.Text); } // 잘못된 입력은 상태 표시줄에 이유를 알린다
            catch (Exception ex) { statusLabel.Text = ex.Message; }
        }

        /// <summary>원본 행번호(1-based)로 그리드 이동. 필터 중이면 뷰맵을 스캔해 찾는다.</summary>
        private async Task JumpToSourceRowAsync(long target)
        {
            if (_doc is null || _doc.DisplayRowCount == 0 || target < 1) return;
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

        private async void ShowAdvancedFilter() => await ShowAdvancedFilterCore(null, -1);

        // 칩에서 기존 식 조건을 다시 편집(원본 식을 미리 채우고 교체).
        private async void EditValueCondition(int index)
        {
            if (index >= 0 && index < _valueConditions.Count && _valueConditions[index].expr is not null)
                await ShowAdvancedFilterCore(_valueConditions[index].expr, index);
        }

        private async Task ShowAdvancedFilterCore(string? initial, int replaceIndex)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;
            var header = doc.Header;
            string expr;
            using (var dlg = new AdvancedFilterDialog(_palette, initial, (text, ct) => TestAdvancedFilter(doc, header, text, ct)))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                expr = dlg.Expression;
            }
            if (expr.Length == 0) return;

            CompiledAdvancedFilter compiled;
            try { compiled = AdvancedFilterExpression.Compile(expr, _doc.Header); }
            catch (AdvancedFilterExpressionException ex)
            {
                MessageBox.Show(ex.Message, LT("Invalid filter", "잘못된 필터"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var entry = ($"⨍ {Trunc(expr)}", compiled.Predicate, (string?)expr);
            if (replaceIndex >= 0 && replaceIndex < _valueConditions.Count)
            {
                // 편집: 기존 조건 교체 후 전체 재적용(넓어질 수도 있어 증분 불가).
                _valueConditions[replaceIndex] = entry;
                await RebuildFilterAsync(LT("Applying expression…", "표현식 적용 중…"));
            }
            else
            {
                // 신규: 현재 뷰를 표현식으로 좁힘(증분).
                _valueConditions.Add(entry);
                await RunViewOpAsync(p => _doc.FilterWithinViewAsync(compiled.Predicate, p, _opCts!.Token),
                    LT("Applying expression…", "표현식 적용 중…"));
                UpdateFilterStatus();
            }
        }

        // 고급 필터 대화상자의 시험: 현재 뷰 앞 10,000행에서 일치 행 수와 표본 행. 잘못된 식·정규식은 오류 문구로 돌려준다.
        private static TesterOutput TestAdvancedFilter(VirtualCsvDocument doc, string[] header, string expression, CancellationToken ct)
        {
            CompiledAdvancedFilter compiled;
            try { compiled = AdvancedFilterExpression.Compile(expression, header); }
            catch (AdvancedFilterExpressionException ex) { return TesterOutput.Fail(ex.Message); }
            catch (RegexPatternException ex) { return TesterOutput.Fail(ex.Message); }

            int total = doc.DisplayRowCount;
            int n = Math.Min(total, RegexReplace.TestMaxRows);
            long matched = 0;
            var samples = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
                int id = doc.GetRowId(i);
                if (id < 0) continue;
                var row = doc.GetRowByIdUncached(id);
                if (!compiled.Predicate(row)) continue;
                matched++;
                if (samples.Count < RegexReplace.TestMaxSamples)
                    samples.Add($"• #{doc.GetSourceRowNumber(i):N0}  " + RegexUi.OneLine(string.Join(" | ", row.Take(6)), 100));
            }
            string scope = n < total
                ? RegexUi.LT($"First {n:N0} of {total:N0} rows", $"전체 {total:N0}행 중 앞 {n:N0}행")
                : RegexUi.LT($"All {n:N0} rows of the view", $"현재 뷰 {n:N0}행 전체");
            string text = RegexUi.LT($"{scope}: {matched:N0} row(s) match.", $"{scope}: {matched:N0}행이 일치합니다.")
                          + RegexUi.TimeoutNote(compiled.Timeouts.Count);
            return new TesterOutput(text, samples);
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
                grid.CurrentCell?.ColumnIndex ?? 0, _columnFilters, _filterMatchAny);
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
            _filterMatchAny = view.MatchAny;   // 조건 결합 방식(AND/OR) 복원

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

        private async void AnalyzeDistribution(int? preselect = null)
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Numeric Distribution", "수치 분포"), _palette);
            var col = dlg.AddCombo(LT("Column", "컬럼"), ColumnLabels(), preselect ?? FirstNumericColumn());
            var bins = dlg.AddNumeric(LT("Bins", "구간 수"), 1, 100, 10);
            if (!dlg.ShowOk(this)) return;

            var colSelection = col.SelectedIndex;
            var binsInput = bins.Value;
            await RunAnalysisAsync(BasicAnalyses.DistributionColumns(colSelection),
                work => BasicAnalyses.Distribution(work, colSelection, (int)binsInput));
        }

        private async void AnalyzeDateHistogram()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Date Histogram", "날짜 히스토그램"), _palette);
            var dateCol = dlg.AddCombo(LT("Date column", "날짜 컬럼"), ColumnLabels(), FirstDateColumn());
            var valueCol = dlg.AddCombo(LT("Value column (optional)", "값 컬럼(선택)"),
                new[] { LT("(none)", "(없음)") }.Concat(ColumnLabels()), 0);
            var period = dlg.AddCombo(LT("Period", "주기"), new[] { "Day", "Week", "Month", "Year" }, 2);
            if (!dlg.ShowOk(this)) return;

            var valueColSelection = valueCol.SelectedIndex;
            var periodSelection = period.SelectedIndex;
            var dateColSelection = dateCol.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.DateHistogramColumns(dateColSelection, valueColSelection),
                work => BasicAnalyses.DateHistogram(work, dateColSelection, valueColSelection, periodSelection));
        }

        private async void AnalyzeDuplicates()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Find Duplicates", "중복 찾기"), _palette);
            var list = dlg.AddCheckedList(LT("Key columns", "키 컬럼"), ColumnLabels(), Math.Min(12, _doc.ColumnCount));
            if (!dlg.ShowOk(this)) return;
            var keys = CheckedIndexes(list);
            if (keys.Count == 0) { ShowResult(LT("Find Duplicates", "중복 찾기"), LT("Select at least one column.", "컬럼을 하나 이상 선택하세요.")); return; }

            await RunAnalysisAsync(BasicAnalyses.DuplicatesColumns(keys),
                work => BasicAnalyses.Duplicates(work, keys), withSourceRows: true);
        }

        private async void AnalyzeGroupBy()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
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

            var valueColSelection = valueCol.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.GroupByColumns(groups, valueColSelection),
                work => BasicAnalyses.GroupBy(work, groups, valueColSelection, funcs));
        }

        // ---------------------------------------------------------------- 통계 (N)

        private async void AnalyzeCorrelation()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Correlation", "상관분석"), _palette);
            var x = dlg.AddCombo("X", ColumnLabels(), FirstNumericColumn());
            var y = dlg.AddCombo("Y", ColumnLabels(), Math.Min(FirstNumericColumn() + 1, Math.Max(0, _doc.ColumnCount - 1)));
            var method = dlg.AddCombo(LT("Method", "방법"), new[] { "Pearson", "Spearman" }, 0);
            if (!dlg.ShowOk(this)) return;

            var xSelection = x.SelectedIndex;
            var ySelection = y.SelectedIndex;
            var methodSelection = method.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.CorrelationColumns(xSelection, ySelection),
                work => BasicAnalyses.Correlation(work, xSelection, ySelection, methodSelection));
        }

        private async void AnalyzeIndependentTTest()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Independent t-test", "독립표본 t검정"), _palette);
            var valueCol = dlg.AddCombo(LT("Value column", "값 컬럼"), ColumnLabels(), FirstNumericColumn());
            var groupCol = dlg.AddCombo(LT("Group column", "그룹 컬럼"), ColumnLabels(), 0);
            if (!dlg.ShowOk(this)) return;

            var valueColSelection = valueCol.SelectedIndex;
            var groupColSelection = groupCol.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.IndependentTTestColumns(valueColSelection, groupColSelection),
                work => BasicAnalyses.IndependentTTest(work, valueColSelection, groupColSelection));
        }

        private async void AnalyzePairedTTest()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Paired t-test", "대응표본 t검정"), _palette);
            var before = dlg.AddCombo(LT("Before column", "이전 컬럼"), ColumnLabels(), FirstNumericColumn());
            var after = dlg.AddCombo(LT("After column", "이후 컬럼"), ColumnLabels(), Math.Min(FirstNumericColumn() + 1, Math.Max(0, _doc.ColumnCount - 1)));
            if (!dlg.ShowOk(this)) return;

            var beforeSelection = before.SelectedIndex;
            var afterSelection = after.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.PairedTTestColumns(beforeSelection, afterSelection),
                work => BasicAnalyses.PairedTTest(work, beforeSelection, afterSelection));
        }

        private async void AnalyzeChiSquare()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Chi-square", "카이제곱 검정"), _palette);
            var rowCol = dlg.AddCombo(LT("Row column", "행 컬럼"), ColumnLabels(), 0);
            var colCol = dlg.AddCombo(LT("Column column", "열 컬럼"), ColumnLabels(), Math.Min(1, Math.Max(0, _doc.ColumnCount - 1)));
            if (!dlg.ShowOk(this)) return;

            var rowColSelection = rowCol.SelectedIndex;
            var colColSelection = colCol.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.ChiSquareColumns(rowColSelection, colColSelection),
                work => BasicAnalyses.ChiSquare(work, rowColSelection, colColSelection));
        }

        // ---------------------------------------------------------------- 기본통계 (이슈 #17)

        private async void AnalyzeDescriptives(int? preselect = null)
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Descriptive Statistics", "기술통계"), _palette);
            var list = dlg.AddCheckedList(LT("Columns", "컬럼"), ColumnLabels(), Math.Min(12, _doc.ColumnCount));
            if (preselect is { } only && only >= 0 && only < list.Items.Count) list.SetItemChecked(only, true);
            else
                for (int c = 0; c < _columnSummaries.Length && c < list.Items.Count; c++)
                    if (IsNumericColumn(c)) list.SetItemChecked(c, true);
            if (!dlg.ShowOk(this)) return;
            var cols = CheckedIndexes(list);
            if (cols.Count == 0) { ShowResult(LT("Descriptive Statistics", "기술통계"), LT("Select at least one column.", "컬럼을 하나 이상 선택하세요.")); return; }

            await RunAnalysisAsync(BasicAnalyses.DescriptivesColumns(cols),
                work => BasicAnalyses.Descriptives(work, cols));
        }

        // preselect: 헤더 우클릭 ▸ 빠른 분석이 컬럼을 미리 골라 대화상자를 연다(그 외에는 기본 컬럼).
        private async void AnalyzeFrequency(int? preselect = null)
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Frequency Table", "빈도분석"), _palette);
            var col = dlg.AddCombo(LT("Column", "컬럼"), ColumnLabels(), preselect ?? 0);
            var topN = dlg.AddNumeric(LT("Max rows", "최대 행 수"), 1, 10_000, 100);
            if (!dlg.ShowOk(this)) return;

            var colSelection = col.SelectedIndex;
            var topNInput = topN.Value;
            await RunAnalysisAsync(BasicAnalyses.FrequencyColumns(colSelection),
                work => BasicAnalyses.Frequency(work, colSelection, (int)topNInput));
        }

        private async void AnalyzeOneWayAnova()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("One-way ANOVA", "일원배치 분산분석"), _palette);
            var valueCol = dlg.AddCombo(LT("Value column", "값 컬럼"), ColumnLabels(), FirstNumericColumn());
            var groupCol = dlg.AddCombo(LT("Group column", "그룹 컬럼"), ColumnLabels(), 0);
            if (!dlg.ShowOk(this)) return;

            var valueColSelection = valueCol.SelectedIndex;
            var groupColSelection = groupCol.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.OneWayAnovaColumns(valueColSelection, groupColSelection),
                work => BasicAnalyses.OneWayAnova(work, valueColSelection, groupColSelection));
        }

        private async void AnalyzeNormality()
        {
            if (_doc is null || _closing || _busy || !_doc.IndexingComplete) return;
            using var dlg = new ParamDialog(LT("Normality Test (Shapiro-Wilk)", "정규성 검정(Shapiro-Wilk)"), _palette);
            var col = dlg.AddCombo(LT("Column", "컬럼"), ColumnLabels(), FirstNumericColumn());
            if (!dlg.ShowOk(this)) return;

            var colSelection = col.SelectedIndex;
            await RunAnalysisAsync(BasicAnalyses.NormalityColumns(colSelection),
                work => BasicAnalyses.Normality(work, colSelection));
        }

        // ---------------------------------------------------------------- 시각화 (이슈 #19)

        /// <summary>
        /// 차트 빌더 창을 연다. shareCtx가 있으면(히트맵→산점도 드릴다운) 행 스냅샷 List만 참조 공유하고
        /// 컨텍스트 객체는 창마다 복제한다 — 한 창의 "현재 뷰로 새로고침"이 다른 창의 데이터 소스를
        /// 몰래 바꾸지 않도록(스냅샷 List 자체는 어디서도 변경하지 않는 읽기 전용 계약).
        /// </summary>
        private async void OpenChartBuilder(ChartKind kind, int[]? presetCols = null, ChartContext? shareCtx = null)
        {
            if (_closing || _doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;

            List<string[]> rows;
            if (shareCtx is not null)
            {
                rows = shareCtx.Rows; // 드릴다운: 부모 스냅샷 재사용(재수집 비용 0)
            }
            else
            {
                var snapshot = await GatherAnalysisSnapshotAsync(doc);
                if (snapshot is null || _closing || IsDisposed || !ReferenceEquals(doc, _doc)) return;
                rows = snapshot.Rows;
            }

            var names = shareCtx?.ColumnNames;
            if (names is null)
            {
                names = new string[_doc.ColumnCount];
                for (int c = 0; c < names.Length; c++)
                    names[c] = c < grid.Columns.Count ? grid.Columns[c].HeaderText : $"Column{c + 1}";
            }

            var ctx = new ChartContext
            {
                Rows = rows,
                ColumnNames = names,
                Summaries = shareCtx?.Summaries ?? _columnSummaries,
                Palette = _palette,
                RefreshRowsAsync = () => GatherAnalysisSnapshotAsync(doc),
                OpenMemorySettings = () => ShowSettings("files"),
            };
            ctx.OpenChart = (k, p) => OpenChartBuilder(k, p, ctx);

            var f = new ChartForm(ctx, kind, presetCols) { Owner = this };
            var ownerList = _chartForms; // 이 차트가 속한 탭의 목록(탭을 바꾼 뒤 닫혀도 그 목록에서 빠지게)
            ownerList.Add(f);
            f.FormClosed += (_, _) => ownerList.Remove(f);
            f.Show(this);
        }

        // 문서/시트 전환 시 호출: 열린 차트는 낡은 스냅샷을 보므로 모두 닫는다.
        private void CloseAllChartForms()
        {
            foreach (var f in _chartForms.ToArray())
            {
                try { f.Close(); } catch { /* 이미 닫힘 */ }
            }
            _chartForms.Clear();
        }

        /// <summary>통계 결과창 + "차트로 보기" 버튼(이슈 #19 역방향 진입): 결과를 해당 차트로 이어본다.
        /// 차트는 모달이 완전히 닫힌 뒤 연다.</summary>
        private void ShowResultWithChart(string title, string body, ChartKind kind, int[]? presetCols)
        {
            bool requested;
            using (var form = new ResultForm(title, body, _palette,
                       LT("View as Chart", "차트로 보기")))
            {
                form.ShowDialog(this);
                requested = form.ActionRequested;
            }
            if (requested) OpenChartBuilder(kind, presetCols); // 모달 언와인드 후 — z-order/예외 안전
        }

        // ---------------------------------------------------------------- 피벗 빌더 (O · P)

        private PivotForm? _pivotForm;
        private Task? _pivotDrainTask;

        private async void OpenPivotBuilder(bool chartTab = false)
        {
            if (_closing || _doc is null || !_doc.IndexingComplete || _busy) return;
            var rows = _doc.SnapshotViewRows();
            using var form = new PivotForm(_doc.Header, _columnSummaries, rows, _palette, _theme, this);
            _pivotForm = form;
            if (chartTab) form.SelectChartTab();
            try { form.ShowDialog(this); }
            finally
            {
                form.CancelReaders();
                _pivotDrainTask = form.ReaderCompletion;
                _pivotForm = null;
                if (!_closing && !IsDisposed) SetBusy(true);
                try { await _pivotDrainTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Debug.WriteLine($"[Pivot drain] {ex}"); }
                finally { if (!_closing && !IsDisposed) SetBusy(false); }
            }
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
                    var existing = files.Where(File.Exists).ToArray();
                    await OpenFilesAsync(existing.Length > 0 ? existing : files);
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
                    var paths = files.Cast<string?>().Where(p => p is not null && File.Exists(p)).Select(p => p!).ToArray();
                    if (paths.Length > 0) { await OpenFilesAsync(paths); return; }
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

        // ---------------------------------------------------------------- 데이터 품질 (이슈 #26)
        //
        // 4-모델 설계 논쟁(Fable 5·Codex·Grok·Opus 4.8) 합의 구현:
        //  - 전수 스트리밍 프로파일(뷰 200만 행 상한 우회, 검사 범위 정직 표기)
        //  - 발견 → 기존 필터 칩 변환 → 원본 행 점프 루프(하단 도킹 패널)
        //  - 점수 게이지·내장 의료 규칙 팩·자동 수정 없음. 규칙은 사용자 소유 JSON.

        private ToolStripMenuItem? _qualityMenu, _qualityPanelMenu;
        private QualityPanel? _qualityPanel;
        private QualityReport? _qualityReport;                       // 마지막 프로파일(스냅샷·보고서 기반)
        private List<QualityFinding> _qualityFindings = new(); // 표시 대상: 프로파일 + 키 + 규칙
        private VirtualCsvDocument? _qualityFindingsDoc;             // 발견이 캡처한 문서(프로버넌스 가드)
        private List<QualityRule> _qualityRules = new();             // 세션 규칙(문서 전환에도 유지)
        private ConformanceProfile? _conformanceProfile;             // 마지막으로 불러온 적합성 프로파일
        private string? _conformanceProfilePath;
        private CancellationTokenSource? _qualityCts;
        private Task? _qualityTask;                                  // CancelAndDrainAsync가 함께 대기

        // 문서/시트 전환 시 호출(ResetView): 발견은 이전 문서 기준이므로 비운다. 규칙은 세션 자산으로 유지.
        private void ResetQualityState()
        {
            _qualityCts?.Cancel();
            _qualityReport = null;
            _qualityFindings.Clear();
            _qualityFindingsDoc = null;
            // 패널 내부 목록·Tag(옛 발견)도 함께 비운다 — 그러지 않으면 메뉴로 패널을 다시 열 때
            // 옛 문서의 ViolationPredicate가 새 문서에 조용히 잘못 적용된다(리뷰 확정 결함).
            if (_qualityPanel is not null)
            {
                _qualityPanel.ShowFindings(Array.Empty<QualityFinding>(), "");
                if (!_findingsPinned) SetQualityPanelVisible(false);   // 사용자가 켜 둔(고정한) 패널은 문서가 바뀌어도 그대로 둔다
            }
        }

        private void EnsureQualityPanel()
        {
            if (_qualityPanel is not null) return;
            _qualityPanel = new QualityPanel(_palette) { Visible = false };
            _qualityPanel.ApplyFilterRequested += async f => await ApplyQualityFindingFilterAsync(f);
            _qualityPanel.JumpRequested += async row => await JumpToSourceRowAsync(row);
            _qualityPanel.ExportRequested += ExportQualityReport;
            _qualityPanel.AdvancedStatsRequested += ShowAdvancedStatsFromQuality;
            _qualityPanel.CloseRequested += () => SetFindingsVisible(false);
            // 그리드 영역(splitContainer1.Panel2) 안, 시트 탭 아래에 도킹한다(폼 최상위에 두면 탐색기·AI 패널 밑까지 펼쳐진다).
            splitContainer1.Panel2.Controls.Add(_qualityPanel);
            OrderGridAreaControls();
        }

        private bool _findingsPinned;   // 사용자가 직접(메뉴·설정·레이아웃) 켠 검사 결과 패널: 문서·탭이 바뀌어도 자동으로 숨기지 않는다
        private bool _findingsShown;    // 패널을 보이게 했는가(창이 아직 안 보일 때도 맞는 값 — Control.Visible은 부모가 안 보이면 false)

        private void SetQualityPanelVisible(bool visible)
        {
            EnsureQualityPanel();
            _qualityPanel!.Visible = visible;
            _findingsShown = visible;
            if (_qualityPanelMenu is not null) _qualityPanelMenu.Checked = visible;
            PerformLayout();
            RaisePanelChanged(PanelKind.Findings);
        }

        /// <summary>사용자가 검사 결과 패널을 직접 켜거나 끈다(켜면 문서·탭이 바뀌어도 유지).</summary>
        internal void SetFindingsVisible(bool visible)
        {
            _findingsPinned = visible;
            SetQualityPanelVisible(visible);
        }

        private void ToggleQualityPanel()
        {
            EnsureQualityPanel();
            SetFindingsVisible(!_findingsShown);
        }

        // 발견 목록을 결정적 순서(심각도↓·컬럼·종류)로 패널에 반영하고 표시한다.
        private void ShowQualityFindings()
        {
            EnsureQualityPanel();
            var sorted = _qualityFindings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.Kind)
                .ToArray();
            _qualityFindings.Clear();
            _qualityFindings.AddRange(sorted);
            _qualityFindingsDoc = _doc; // 발견은 현재 문서 기준 — 필터/점프 시 동일성 확인용
            _qualityPanel!.ShowFindings(sorted, QualitySummaryText());
            SetQualityPanelVisible(true);
        }

        private string QualitySummaryText()
        {
            long crit = _qualityFindings.Count(f => f.Severity == QualitySeverity.Critical);
            long warn = _qualityFindings.Count(f => f.Severity == QualitySeverity.Warning);
            long info = _qualityFindings.Count(f => f.Severity == QualitySeverity.Info);
            string counts = LT($"Critical {crit} · Warning {warn} · Info {info}",
                               $"심각 {crit} · 경고 {warn} · 정보 {info}");
            if (_qualityReport is not { } r) return counts;
            string scope = r.ScannedFully ? LT("full scan", "전수") : LT("partial scan", "일부");
            string dup = r.DuplicateRowCheckSkipped ? LT(" · dup check skipped", " · 중복검사 생략") : "";
            return LT($"{r.RowsScanned:N0} rows ({scope}) · {r.ElapsedSeconds:0.0}s · {counts}{dup}",
                      $"{r.RowsScanned:N0}행 ({scope}) · {r.ElapsedSeconds:0.0}초 · {counts}{dup}");
        }

        // 현재 문서에 대한 스캔 입력(전수 경로). 기대 타입은 추론+선언+수동 오버라이드가 반영된 요약에서.
        private QualityScanSource BuildQualityScanSource(VirtualCsvDocument doc, bool withTypes)
        {
            ColumnValueType[]? types = withTypes && _columnSummaries.Length == doc.ColumnCount
                ? _columnSummaries.Select(s => s.InferredType).ToArray() : null;
            return new QualityScanSource
            {
                Headers = doc.Header,
                RowAt = doc.GetDataRowUncached,
                RowCount = doc.DataRowsAvailable,
                CoversAllRows = !doc.RowCountTruncated,
                ColumnTypes = types,
                AllowedCodes = withTypes ? AlignToDisplayColumns(_workbook?.AllowedCodes(_currentSheetIndex)) : null,
                SourceName = Path.GetFileName(_workbook?.SourcePath ?? _currentPath ?? ""),
                SourceBytes = doc.FileLength,
            };
        }

        private async Task RunQualityProfileAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;

            _qualityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _qualityCts = cts;

            var src = BuildQualityScanSource(doc, withTypes: true);
            var options = new QualityScanOptions();

            SetBusy(true);
            statusLabel.Text = LT("Quality scan…", "품질 스캔 중…");
            var progress = new Progress<int>(p =>
            {
                if (!cts.IsCancellationRequested)
                    statusLabel.Text = LT($"Quality scan… {p}%", $"품질 스캔 중… {p}%");
            });

            QualityReport report;
            var task = Task.Run(() => QualityProfiler.Scan(src, options, progress, cts.Token), cts.Token);
            _qualityTask = task;
            try { report = await task; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                statusLabel.Text = LT("Quality scan failed", "품질 스캔 실패");
                MessageBox.Show(this, ex.Message, LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                if (_qualityTask == task) _qualityTask = null;
                SetBusy(false);
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_doc, doc)) return; // 문서가 바뀌었으면 낡은 결과

            _qualityReport = report with
            {
                ScanTimestamp = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                AppVersion = AppInfo.Version,
            };
            _qualityFindings.RemoveAll(f => !QualitySessionChecks.IsUserRun(f.Kind));
            _qualityFindings.InsertRange(0, report.Findings);
            ShowQualityFindings();
            statusLabel.Text = QualitySummaryText();
        }

        private async Task ApplyQualityFindingFilterAsync(QualityFinding f)
        {
            if (_doc is null || !_doc.IndexingComplete || _busy || f.ViolationPredicate is null) return;
            // 프로버넌스 가드: 발견이 캡처한 문서가 현재 문서와 다르면(옛 컬럼 인덱스·해시셋) 적용 금지.
            if (!ReferenceEquals(_doc, _qualityFindingsDoc))
            {
                statusLabel.Text = LT("Findings are from a different file — rerun the scan",
                                      "다른 파일의 검사 결과입니다 — 다시 스캔하세요");
                return;
            }
            _valueConditions.Add((QualityText.ChipLabel(f), f.ViolationPredicate, null));
            await RebuildFilterAsync(LT("Applying filter…", "필터 적용 중…"));
            grid.Invalidate();
        }

        private async Task RunKeyUniquenessAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;

            using var dlg = new ParamDialog(LT("Key Uniqueness", "키 유일성 검사"), _palette);
            dlg.AddNote(LT("Checks duplicates of the selected (composite) key across the whole file.",
                           "선택한 (복합)키의 중복을 파일 전체에서 검사합니다."));
            var list = dlg.AddCheckedList(LT("Key columns", "키 컬럼"), ColumnLabels(), Math.Min(12, doc.ColumnCount));
            if (!dlg.ShowOk(this)) return;
            var keys = CheckedIndexes(list);
            if (keys.Count == 0)
            {
                ShowResult(LT("Key Uniqueness", "키 유일성 검사"), LT("Select at least one column.", "컬럼을 하나 이상 선택하세요."));
                return;
            }

            _qualityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _qualityCts = cts;
            var src = BuildQualityScanSource(doc, withTypes: false);
            var options = new QualityScanOptions();

            SetBusy(true);
            statusLabel.Text = LT("Key scan…", "키 검사 중…");
            var progress = new Progress<int>(p =>
            {
                if (!cts.IsCancellationRequested)
                    statusLabel.Text = LT($"Key scan… {p}%", $"키 검사 중… {p}%");
            });

            KeyUniquenessScanner.Result result;
            var task = Task.Run(() => KeyUniquenessScanner.Scan(src, keys, options, progress, cts.Token), cts.Token);
            _qualityTask = task;
            try { result = await task; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                statusLabel.Text = LT("Key scan failed", "키 검사 실패");
                MessageBox.Show(this, ex.Message, LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                if (_qualityTask == task) _qualityTask = null;
                SetBusy(false);
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_doc, doc)) return;

            string keyNames = string.Join(" + ", keys.Select(ColumnLabel));
            if (result.Skipped)
            {
                MessageBox.Show(this,
                    LT("Too many rows for the key scan (memory guard). The check was skipped.",
                       "행 수가 상한을 넘어 키 검사를 생략했습니다(메모리 가드)."),
                    LT("Key Uniqueness", "키 유일성 검사"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (result.Finding is null)
            {
                statusLabel.Text = LT($"Key ({keyNames}) is unique — {src.RowCount:N0} rows checked",
                                      $"키({keyNames})는 유일합니다 — {src.RowCount:N0}행 전수 확인");
                return;
            }

            var finding = result.Finding with { ColumnName = keyNames };
            _qualityFindings.RemoveAll(f => f.Kind == QualityCheckKind.KeyUniqueness && f.ColumnName == keyNames);
            _qualityFindings.Add(finding);
            ShowQualityFindings();
            statusLabel.Text = QualitySummaryText();
        }

        // 다른 시트 또는 외부 파일의 (복합)키에 자식 키가 있는지 검사. 부모 문서는 검사 후 닫는다.
        private async Task RunReferentialIntegrityAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;

            var sheetChoices = new List<(int Index, string Name)>();
            if (_workbook is not null)
            {
                for (int i = 0; i < _workbook.SheetNames.Count; i++)
                    if (i != _currentSheetIndex)
                        sheetChoices.Add((i, _workbook.SheetNames[i]));
            }

            int picked = -1; // sheetChoices 인덱스. -1 = 파일 찾아보기
            if (sheetChoices.Count > 0)
            {
                using var dlg = new ParamDialog(LT("Referential Integrity", "참조 무결성 검사"), _palette);
                dlg.AddNote(LT(
                    "Checks whether child key values exist in a parent table. Blank and null-token keys are skipped by default — a candidate check, not a declared constraint.",
                    "자식 키 값이 부모 테이블에 있는지 검사합니다. 빈 값·널 토큰 키는 기본으로 건너뜁니다. 후보 검사이며 선언된 제약이 아닙니다."));
                var items = sheetChoices.Select(s => LT($"Sheet: {s.Name}", $"시트: {s.Name}"))
                    .Append(LT("Browse file…", "파일 찾아보기…")).ToArray();
                var combo = dlg.AddCombo(LT("Parent table", "부모 테이블"), items, 0);
                if (!dlg.ShowOk(this)) return;
                if (combo.SelectedIndex >= 0 && combo.SelectedIndex < sheetChoices.Count)
                    picked = combo.SelectedIndex;
            }

            ReferentialParentHold? hold = null;
            try
            {
                if (picked >= 0)
                {
                    var choice = sheetChoices[picked];
                    hold = await OpenReferentialParentAsync(_workbook!.CsvPath(choice.Index), choice.Name, owned: null);
                }
                else
                {
                    using var ofd = new OpenFileDialog { Filter = openFileDialog1.Filter, RestoreDirectory = true };
                    if (ofd.ShowDialog(this) != DialogResult.OK) return;
                    hold = await OpenExternalReferentialParentAsync(ofd.FileName);
                }
                if (hold is null || !ReferenceEquals(_doc, doc) || _busy) return;

                string[] parentHeaders = hold.Doc.Header;
                if (parentHeaders.Length == 0)
                {
                    ShowResult(LT("Referential Integrity", "참조 무결성 검사"),
                        LT("The parent table has no columns.", "부모 테이블에 컬럼이 없습니다."));
                    return;
                }

                int[] childCols;
                int[] parentCols;
                // 예산(AnalysisMemoryBudget.Current)은 읽을 때 예외를 던질 수 있어 UI 스레드에서 옵션을 만들지 않고 검사 작업 안에서 만든다.
                bool skipBlankChildKeys, caseSensitive, trimKeys;
                using (var dlg = new ParamDialog(LT("Referential Integrity", "참조 무결성 검사"), _palette))
                {
                    dlg.AddNote(LT(
                        $"Parent: {hold.DisplayName}. Pair columns in the order you check them (same count). External files are compared as stored text.",
                        $"부모: {hold.DisplayName}. 체크한 순서대로 짝을 맞춥니다(개수 동일). 외부 파일은 저장된 텍스트 그대로 비교합니다."));
                    var childList = dlg.AddCheckedList(LT("Child columns", "자식 컬럼"), ColumnLabels(), Math.Min(8, doc.ColumnCount));
                    var parentLabels = new string[parentHeaders.Length];
                    for (int i = 0; i < parentLabels.Length; i++)
                    {
                        string h = parentHeaders[i];
                        parentLabels[i] = string.IsNullOrEmpty(h) ? $"Column{i + 1}" : h;
                    }
                    var parentList = dlg.AddCheckedList(LT("Parent columns", "부모 컬럼"), parentLabels, Math.Min(8, parentLabels.Length));
                    var blank = dlg.AddCombo(LT("Blank child keys", "빈 자식 키"),
                        new[] { LT("Skip (default)", "건너뜀(기본)"), LT("Treat as violations", "위반으로 셈") }, 0);
                    var cmp = dlg.AddCombo(LT("Comparison", "비교"),
                        new[] { LT("Case-sensitive", "대소문자 구분"), LT("Ignore case", "대소문자 무시") }, 0);
                    var trim = dlg.AddCombo(LT("Whitespace", "공백"),
                        new[] { LT("No trim", "트림 안 함"), LT("Trim", "트림") }, 0);
                    var childOrder = TrackCheckOrder(childList);
                    var parentOrder = TrackCheckOrder(parentList);
                    if (!dlg.ShowOk(this)) return;

                    childCols = childOrder.ToArray();
                    parentCols = parentOrder.ToArray();
                    if (childCols.Length == 0 || parentCols.Length == 0)
                    {
                        ShowResult(LT("Referential Integrity", "참조 무결성 검사"),
                            LT("Select at least one column on each side.", "양쪽에서 컬럼을 하나 이상 선택하세요."));
                        return;
                    }
                    if (childCols.Length != parentCols.Length)
                    {
                        ShowResult(LT("Referential Integrity", "참조 무결성 검사"),
                            LT("Child and parent column counts must match.", "자식·부모 컬럼 개수가 같아야 합니다."));
                        return;
                    }
                    skipBlankChildKeys = blank.SelectedIndex == 0;
                    caseSensitive = cmp.SelectedIndex == 0;
                    trimKeys = trim.SelectedIndex == 1;
                }

                if (!ReferenceEquals(_doc, doc) || _busy) return;

                _qualityCts?.Cancel();
                var cts = new CancellationTokenSource();
                _qualityCts = cts;
                var childSrc = BuildQualityScanSource(doc, withTypes: false);
                var parentDoc = hold.Doc;
                string parentName = hold.DisplayName;

                SetBusy(true);
                statusLabel.Text = LT("Indexing reference…", "참조 테이블 인덱싱 중…");
                var indexProgress = new Progress<IndexProgress>(p =>
                {
                    if (!cts.IsCancellationRequested)
                        statusLabel.Text = LT($"Indexing reference… {p.Percent}%", $"참조 테이블 인덱싱 중… {p.Percent}%");
                });
                var scanProgress = new Progress<int>(p =>
                {
                    if (!cts.IsCancellationRequested)
                        statusLabel.Text = LT($"Referential check… {p}%", $"참조 검사 중… {p}%");
                });

                ReferentialIntegrityScanner.Result result;
                var task = Task.Run(async () =>
                {
                    await parentDoc.RunIndexingAsync(indexProgress, cts.Token);
                    var parentSrc = new ReferentialParentSource
                    {
                        RowAt = parentDoc.GetDataRowUncached,
                        RowCount = parentDoc.DataRowsAvailable,
                        Name = parentName,
                    };
                    var options = new ReferentialIntegrityOptions
                    {
                        SkipBlankChildKeys = skipBlankChildKeys,
                        CaseSensitive = caseSensitive,
                        Trim = trimKeys,
                    };
                    return ReferentialIntegrityScanner.Scan(
                        childSrc, childCols, parentSrc, parentCols, options, scanProgress, cts.Token);
                }, cts.Token);
                _qualityTask = task;
                try { result = await task; }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is ReferentialIntegrityBudgetException or AnalysisMemoryLimitException)
                {
                    statusLabel.Text = LT("Referential check stopped", "참조 검사 중단");
                    BeginInvoke(() => ShowMemoryBudgetExceeded(this,
                        LT("The parent key set exceeds the memory budget. No partial result was kept. Narrow the parent table or key columns.",
                           "부모 키 집합이 메모리 예산을 초과했습니다. 부분 결과는 남기지 않았습니다. 부모 테이블이나 키 컬럼을 줄여 주세요."),
                        LT("Referential Integrity", "참조 무결성 검사")));
                    return;
                }
                catch (Exception ex)
                {
                    statusLabel.Text = LT("Referential check failed", "참조 검사 실패");
                    MessageBox.Show(this, Stats.ErrorText.Localize(ex.Message), LT("Data Quality", "데이터 품질"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                finally
                {
                    if (_qualityTask == task) _qualityTask = null;
                    SetBusy(false);
                }

                if (cts.IsCancellationRequested || !ReferenceEquals(_doc, doc)) return;

                string childNames = string.Join(" + ", childCols.Select(ColumnLabel));
                string parentColNames = string.Join(" + ", parentCols.Select(i =>
                    i >= 0 && i < parentHeaders.Length && !string.IsNullOrEmpty(parentHeaders[i]) ? parentHeaders[i] : $"Column{i + 1}"));
                string scope = childSrc.CoversAllRows ? "" : LT(" (partial)", " (일부)");
                if (result.Finding is null)
                {
                    statusLabel.Text = LT(
                        $"No orphan keys ({childNames} → {parentName}) — {childSrc.RowCount:N0} rows{scope}, {result.BlankSkipped:N0} blank skipped",
                        $"고아 키 없음 ({childNames} → {parentName}) — {childSrc.RowCount:N0}행{scope}, 빈 키 {result.BlankSkipped:N0}건 제외");
                    return;
                }

                var finding = result.Finding with { ColumnName = $"{childNames} → {parentName} ({parentColNames})" };
                _qualityFindings.RemoveAll(f => f.Kind == QualityCheckKind.ForeignKeyOrphan && f.ColumnName == finding.ColumnName);
                _qualityFindings.Add(finding);
                ShowQualityFindings();
                statusLabel.Text = QualitySummaryText();
            }
            finally
            {
                hold?.Dispose();
            }
        }

        private async Task<ReferentialParentHold?> OpenExternalReferentialParentAsync(string path)
        {
            if (!Import.TabularImporter.IsImportable(path))
                return await OpenReferentialParentAsync(path, Path.GetFileName(path), owned: null);

            _qualityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _qualityCts = cts;
            SetBusy(true);
            statusLabel.Text = LT("Importing reference…", "참조 파일 변환 중…");
            var task = Task.Run(() => Import.WorkbookSession.Create(path, showLabels: false), cts.Token);
            _qualityTask = task;
            Import.WorkbookSession owned;
            try { owned = await task; }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                statusLabel.Text = LT("Could not open the reference", "참조 테이블을 열지 못했습니다");
                MessageBox.Show(this, ex.Message, LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            finally
            {
                if (_qualityTask == task) _qualityTask = null;
                SetBusy(false);
            }

            int sheet = 0;
            if (owned.SheetNames.Count > 1)
            {
                using var dlg = new ParamDialog(LT("Referential Integrity", "참조 무결성 검사"), _palette);
                dlg.AddNote(LT("Choose the parent sheet.", "부모 시트를 선택하세요."));
                var combo = dlg.AddCombo(LT("Parent sheet", "부모 시트"), owned.SheetNames, 0);
                if (!dlg.ShowOk(this)) { owned.Dispose(); return null; }
                sheet = Math.Clamp(combo.SelectedIndex, 0, owned.SheetNames.Count - 1);
            }
            string name = $"{Path.GetFileName(path)} [{owned.SheetNames[sheet]}]";
            return await OpenReferentialParentAsync(owned.CsvPath(sheet), name, owned);
        }

        // Open은 헤더만 읽는다. 전수 인덱싱은 검사 태스크에서 UI 밖에서 한다.
        private async Task<ReferentialParentHold?> OpenReferentialParentAsync(
            string csvPath, string displayName, Import.WorkbookSession? owned)
        {
            _qualityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _qualityCts = cts;
            SetBusy(true);
            statusLabel.Text = LT("Opening reference…", "참조 테이블 여는 중…");
            var task = Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                return VirtualCsvDocument.Open(csvPath);
            }, cts.Token);
            _qualityTask = task;
            try
            {
                var parentDoc = await task;
                return new ReferentialParentHold { Doc = parentDoc, Owned = owned, DisplayName = displayName };
            }
            catch (OperationCanceledException)
            {
                owned?.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                owned?.Dispose();
                statusLabel.Text = LT("Could not open the reference", "참조 테이블을 열지 못했습니다");
                MessageBox.Show(this, ex.Message, LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            finally
            {
                if (_qualityTask == task) _qualityTask = null;
                SetBusy(false);
            }
        }

        // 체크한 순서 = 복합키 파트 순서. CheckedIndices는 인덱스 순이라 짝이 어긋난다.
        private static List<int> TrackCheckOrder(CheckedListBox list)
        {
            var order = new List<int>();
            list.ItemCheck += (_, e) =>
            {
                if (e.NewValue == CheckState.Checked)
                {
                    if (!order.Contains(e.Index)) order.Add(e.Index);
                }
                else order.Remove(e.Index);
            };
            return order;
        }

        private sealed class ReferentialParentHold : IDisposable
        {
            public required VirtualCsvDocument Doc { get; init; }
            public Import.WorkbookSession? Owned { get; init; }
            public required string DisplayName { get; init; }

            public void Dispose()
            {
                try { Doc.Dispose(); } catch { /* 참조 문서는 검사 후 항상 닫는다 */ }
                try { Owned?.Dispose(); } catch { /* 임시 변환 폴더 정리 실패는 무시 */ }
            }
        }


        private async Task ShowQualityRulesAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;

            bool run;
            using (var dlg = new QualityRulesDialog(_qualityRules, doc.Header, _palette))
            {
                dlg.ShowDialog(this);
                _qualityRules = dlg.Rules; // 닫기여도 편집 결과는 보존(사용자 소유 자산)
                run = dlg.RunRequested;
            }
            if (!run || _qualityRules.Count == 0) return;

            _qualityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _qualityCts = cts;
            var src = BuildQualityScanSource(doc, withTypes: false);
            var options = new QualityScanOptions();

            SetBusy(true);
            statusLabel.Text = LT("Running rules…", "규칙 검사 중…");
            var progress = new Progress<int>(p =>
            {
                if (!cts.IsCancellationRequested)
                    statusLabel.Text = LT($"Running rules… {p}%", $"규칙 검사 중… {p}%");
            });

            IReadOnlyList<QualityFinding> findings;
            var task = Task.Run(() => QualityRuleRunner.Run(_qualityRules, src, options, progress, cts.Token), cts.Token);
            _qualityTask = task;
            try { findings = await task; }
            catch (OperationCanceledException) { return; }
            catch (QualityRuleCompileException ex)
            {
                statusLabel.Text = LT("Rule compile error", "규칙 컴파일 오류");
                MessageBox.Show(this, ex.Message, LT("Validation Rules", "타당성 규칙"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex)
            {
                statusLabel.Text = LT("Rule run failed", "규칙 검사 실패");
                MessageBox.Show(this, ex.Message, LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                if (_qualityTask == task) _qualityTask = null;
                SetBusy(false);
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_doc, doc)) return;

            _qualityFindings.RemoveAll(f => f.Kind == QualityCheckKind.Rule); // 재실행 = 이전 규칙 결과 대체
            _qualityFindings.AddRange(findings);
            ShowQualityFindings();
            statusLabel.Text = QualitySummaryText();
        }

        private async Task ShowConformanceProfileAsync()
        {
            if (_doc is null || !_doc.IndexingComplete || _busy) return;
            var doc = _doc;

            bool run;
            using (var dlg = new ConformanceProfileDialog(_conformanceProfile, _conformanceProfilePath, _palette))
            {
                dlg.ShowDialog(this);
                _conformanceProfile = dlg.Profile;
                _conformanceProfilePath = dlg.ProfilePath;
                run = dlg.RunRequested;
            }
            if (!run || _conformanceProfile is null || !ReferenceEquals(_doc, doc) || _busy) return;

            _qualityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _qualityCts = cts;
            var src = BuildQualityScanSource(doc, withTypes: false);
            var options = new ConformanceRunOptions
            {
                ProfileDirectory = string.IsNullOrEmpty(_conformanceProfilePath)
                    ? null : Path.GetDirectoryName(_conformanceProfilePath),
            };

            SetBusy(true);
            statusLabel.Text = LT("Conformance profile…", "적합성 프로파일…");
            var progress = new Progress<int>(p =>
            {
                if (!cts.IsCancellationRequested)
                    statusLabel.Text = LT($"Conformance profile… {p}%", $"적합성 프로파일… {p}%");
            });

            ConformanceProfileRunner.Result result;
            var task = Task.Run(() => ConformanceProfileRunner.Run(_conformanceProfile, src, options, progress, cts.Token), cts.Token);
            _qualityTask = task;
            try { result = await task; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is ConformanceBudgetException or AnalysisMemoryLimitException)
            {
                statusLabel.Text = LT("Conformance profile stopped", "적합성 프로파일 중단");
                BeginInvoke(() => ShowMemoryBudgetExceeded(this,
                    LT("A reference key set exceeded the memory budget. No partial result was kept. Narrow the domain or the reference file.",
                       "참조 키 집합이 메모리 예산을 초과했습니다. 부분 결과는 남기지 않았습니다. 도메인이나 참조 파일을 줄여 주세요."),
                    LT("Conformance Profile", "적합성 프로파일")));
                return;
            }
            catch (ConformanceSchemaException ex)
            {
                statusLabel.Text = LT("Conformance profile stopped", "적합성 프로파일 중단");
                MessageBox.Show(this,
                    LT($"This profile uses schema version {ex.SchemaVersion}, which this version cannot run (supported: {ex.SupportedVersion}).",
                       $"이 프로파일의 스키마 버전({ex.SchemaVersion})은 이 버전이 실행할 수 없습니다(지원: {ex.SupportedVersion})."),
                    LT("Conformance Profile", "적합성 프로파일"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex)
            {
                statusLabel.Text = LT("Conformance profile failed", "적합성 프로파일 실패");
                MessageBox.Show(this, Stats.ErrorText.Localize(ex.Message), LT("Conformance Profile", "적합성 프로파일"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                if (_qualityTask == task) _qualityTask = null;
                SetBusy(false);
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_doc, doc)) return;

            _qualityFindings.RemoveAll(f => QualitySessionChecks.IsConformance(f.Kind));
            _qualityFindings.AddRange(result.Findings);
            ShowQualityFindings();
            string scope = result.ScannedFully ? LT("full", "전수") : LT("partial", "일부");
            int failing = result.Findings.Count(f => f.ViolationCount > 0 && f.Severity != QualitySeverity.Info);
            statusLabel.Text = LT(
                $"Conformance profile: {result.RowsScanned:N0} rows ({scope}) · {failing} failing check(s) · {result.ChecksNotRun} not run",
                $"적합성 프로파일: {result.RowsScanned:N0}행 ({scope}) · 실패 검사 {failing}건 · 미실행 {result.ChecksNotRun}건");
        }

        private async Task ImportDqdResultsAsync()
        {
            if (_doc is null || _busy) return;
            var doc = _doc;

            using var ofd = new OpenFileDialog
            {
                Title = LT("Import DQD Results", "DQD 결과 가져오기"),
                Filter = "JSON (*.json)|*.json",
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            string? table = CurrentQualityTableName();
            string? filter = null;
            using (var dlg = new ParamDialog(LT("Import DQD Results", "DQD 결과 가져오기"), _palette))
            {
                dlg.AddNote(LT(
                    "Imported checks are labelled imported from DQD. They are not a scan of this file, so Filter Rows stays disabled.",
                    "가져온 검사는 'DQD에서 가져옴'으로 표시됩니다. 이 파일의 스캔이 아니므로 '위반 행만 보기'는 꺼집니다."));
                var choices = new List<string> { LT("All tables in the file", "파일의 모든 테이블") };
                if (!string.IsNullOrEmpty(table))
                    choices.Add(LT($"Current table only ({table})", $"현재 테이블만 ({table})"));
                var combo = dlg.AddCombo(LT("Tables", "테이블"), choices, 0);
                if (!dlg.ShowOk(this)) return;
                if (combo.SelectedIndex == 1) filter = table;
            }

            _qualityCts?.Cancel();
            var cts = new CancellationTokenSource();
            _qualityCts = cts;
            SetBusy(true);
            statusLabel.Text = LT("Importing DQD results…", "DQD 결과 가져오는 중…");
            DqdResultsImport.Result imported;
            var task = Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                string json = File.ReadAllText(ofd.FileName);
                return DqdResultsImport.Import(json, new DqdResultsImport.Options { TableName = filter }, doc.Header);
            }, cts.Token);
            _qualityTask = task;
            try { imported = await task; }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                statusLabel.Text = LT("DQD import failed", "DQD 가져오기 실패");
                MessageBox.Show(this, Stats.ErrorText.Localize(ex.Message), LT("Import DQD Results", "DQD 결과 가져오기"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            finally
            {
                if (_qualityTask == task) _qualityTask = null;
                SetBusy(false);
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_doc, doc)) return;

            _qualityFindings.RemoveAll(f => f.Kind == QualityCheckKind.DqdImported);
            _qualityFindings.AddRange(imported.Findings);
            ShowQualityFindings();
            string tableNote = filter is null
                ? LT("all tables", "모든 테이블")
                : LT($"table {filter}", $"테이블 {filter}");
            statusLabel.Text = LT(
                $"Imported {imported.ChecksImported:N0} DQD check(s) ({tableNote}) — not a scan of this file",
                $"DQD 검사 {imported.ChecksImported:N0}건 가져옴 ({tableNote}) — 이 파일의 스캔이 아님");
        }

        private string? CurrentQualityTableName()
        {
            if (_workbook is not null && _currentSheetIndex >= 0 && _currentSheetIndex < _workbook.SheetNames.Count)
            {
                string sheet = _workbook.SheetNames[_currentSheetIndex];
                if (!string.IsNullOrWhiteSpace(sheet)) return sheet;
            }
            string? path = _workbook?.SourcePath ?? _currentPath;
            return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
        }


        // ---------------------------------------------------------------- 품질 보고서 내보내기

        private void ExportQualityReport()
        {
            if (_doc is null) return;
            if (_qualityReport is null && _qualityFindings.Count == 0)
            {
                statusLabel.Text = LT("Run a quality profile first", "먼저 품질 프로파일을 실행하세요");
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Filter = "HTML (*.html)|*.html|Markdown (*.md)|*.md|JSON (*.json)|*.json",
                FileName = "quality-report.html",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var baseReport = _qualityReport ?? new QualityReport
                {
                    RowsScanned = _doc.DataRowsAvailable,
                    ScannedFully = !_doc.RowCountTruncated,
                    ElapsedSeconds = 0,
                    Columns = Array.Empty<QualityColumnProfile>(),
                    Findings = Array.Empty<QualityFinding>(),
                    SourceName = Path.GetFileName(_workbook?.SourcePath ?? _currentPath ?? ""),
                    SourceBytes = _doc.FileLength,
                    ScanTimestamp = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                    AppVersion = AppInfo.Version,
                };
                var report = baseReport with { Findings = _qualityFindings.ToArray() };

                string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                string content = ext switch
                {
                    ".json" => QualityReportJson.Serialize(report), // JSON = 안정 스키마 스냅샷(후속 diff 선행물)
                    ".md" => BuildQualityReportMarkdown(report),
                    _ => BuildQualityReportHtml(report),
                };
                File.WriteAllText(dlg.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                statusLabel.Text = LT("Quality report saved", "품질 보고서 저장됨");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, LT("Export failed", "내보내기 실패"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 기준선 JSON(품질 보고서 내보내기)과 현재 프로파일을 비교한다.
        // 차이는 행 술어가 없으므로 발견 패널에 넣지 않고 결과 창으로만 보여 준다.
        private async Task CompareQualityBaselineAsync()
        {
            if (_doc is null || _busy) return;
            if (_qualityReport is null)
            {
                var answer = MessageBox.Show(this,
                    LT("Run a quality profile first, then compare.\n\nRun the profile now?",
                       "먼저 품질 프로파일을 실행한 뒤 비교할 수 있습니다.\n\n지금 프로파일을 실행할까요?"),
                    LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return;
                await RunQualityProfileAsync();
                if (_qualityReport is null || _busy) return;
            }

            using var open = new OpenFileDialog
            {
                Title = LT("Baseline Snapshot", "기준선 스냅샷"),
                Filter = "JSON (*.json)|*.json",
            };
            if (open.ShowDialog(this) != DialogResult.OK) return;

            QualitySnapshotDiffResult diff;
            try
            {
                string json = File.ReadAllText(open.FileName);
                var baseline = QualityReportJson.Deserialize(json);
                // 내보내기와 같이 키·규칙 발견까지 현재 쪽으로 포함한다.
                var current = _qualityReport with { Findings = _qualityFindings.ToArray() };
                diff = QualitySnapshotDiff.Compare(baseline, current);
            }
            catch (QualitySnapshotSchemaException ex)
            {
                MessageBox.Show(this,
                    LT($"This snapshot uses schema version {ex.SchemaVersion}, which this version cannot compare (supported: {ex.SupportedVersion}).",
                       $"이 스냅샷의 스키마 버전({ex.SchemaVersion})은 이 버전이 비교할 수 없습니다(지원: {ex.SupportedVersion})."),
                    LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (System.Text.Json.JsonException)
            {
                MessageBox.Show(this,
                    LT("The file is not a quality snapshot JSON from Export Quality Report.",
                       "품질 보고서 내보내기(JSON)로 만든 스냅샷이 아닙니다."),
                    LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, LT("Data Quality", "데이터 품질"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string body = BuildQualityDiffMarkdown(diff);
            statusLabel.Text = diff.Comparable
                ? LT($"Baseline comparison: {diff.Items.Count:N0} item(s)", $"기준선 비교: {diff.Items.Count:N0}건")
                : LT("Baseline comparison (not comparable — partial scan)", "기준선 비교(비교 불가 — 일부 스캔)");

            bool export;
            using (var form = new ResultForm(LT("Baseline Comparison", "기준선 비교"), body, _palette,
                       LT("Export…", "내보내기…")))
            {
                form.ShowDialog(this);
                export = form.ActionRequested;
            }
            if (export) ExportQualityDiff(diff, body);
        }

        private void ExportQualityDiff(QualitySnapshotDiffResult diff, string markdown)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md|JSON (*.json)|*.json",
                FileName = "quality-diff.md",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                string content = ext == ".json" ? QualitySnapshotDiff.Serialize(diff) : markdown;
                File.WriteAllText(dlg.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                statusLabel.Text = LT("Baseline comparison saved", "기준선 비교 저장됨");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, LT("Export failed", "내보내기 실패"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string BuildQualityDiffMarkdown(QualitySnapshotDiffResult d)
        {
            static string Md(string s) => s.Replace("|", "\\|").Replace("\n", " ");
            string scopeOf(bool full) => full ? LT("full scan", "전수") : LT("partial scan", "일부");
            var sb = new StringBuilder();
            sb.AppendLine(LT("# Baseline comparison", "# 기준선 비교"));
            sb.AppendLine();
            sb.AppendLine($"- {LT("Baseline", "기준선")}: {Md(d.BaselineSourceName)} · {d.BaselineRows:N0} " +
                LT("rows", "행") + $" ({scopeOf(d.BaselineScannedFully)}) · {d.BaselineTimestamp}");
            sb.AppendLine($"- {LT("Current", "현재")}: {Md(d.CurrentSourceName)} · {d.CurrentRows:N0} " +
                LT("rows", "행") + $" ({scopeOf(d.CurrentScannedFully)}) · {d.CurrentTimestamp}");
            if (d.Comparable)
                sb.AppendLine($"- {LT("Scope: comparable (both full scans).", "범위: 비교 가능(양쪽 전수).")}");
            else
            {
                string which = !d.BaselineScannedFully && !d.CurrentScannedFully
                    ? LT("baseline and current are partial scans", "기준선과 현재 모두 일부 스캔")
                    : !d.BaselineScannedFully
                        ? LT("the baseline is a partial scan", "기준선이 일부 스캔")
                        : LT("the current profile is a partial scan", "현재 프로파일이 일부 스캔");
                sb.AppendLine($"- {LT($"Scope: NOT COMPARABLE — {which}. Differences below are approximate, not assertions.",
                    $"범위: 비교 불가 — {which}. 아래 차이는 근사이며 단정이 아닙니다.")}");
            }
            if (d.DistinctCountsCapped)
                sb.AppendLine($"- {LT("Unique counts are lower bounds on at least one side (tracking cap).",
                    "고유값 개수는 한쪽 이상이 하한입니다(추적 상한).")}");
            if (d.BaselineDuplicateCheckSkipped || d.CurrentDuplicateCheckSkipped)
                sb.AppendLine($"- {LT("Duplicate-row check was skipped on at least one side, so that finding is not classified as new or resolved.",
                    "중복 행 검사는 한쪽 이상에서 생략되어, 그 발견은 신규/해소로 분류하지 않습니다.")}");
            sb.AppendLine();
            sb.AppendLine(LT("## Differences", "## 차이"));
            if (d.Items.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine(LT("No differences beyond the thresholds.", "임계를 넘는 차이가 없습니다."));
                return sb.ToString();
            }
            sb.AppendLine();
            sb.AppendLine($"| {LT("Severity", "심각도")} | {LT("Change", "변화")} | {LT("Column", "컬럼")} | {LT("Detail", "세부")} |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var item in d.Items)
            {
                string approx = item.Approximate ? "≈ " : "";
                string column = item.ColumnName.Length > 0 ? item.ColumnName : "—";
                sb.AppendLine($"| {approx}{QualityText.SeverityName(item.Severity)} | {DiffKindName(item.Kind)} | {Md(column)} | {Md(DiffDetail(item))} |");
            }
            return sb.ToString();
        }

        private static string DiffKindName(QualitySnapshotDiffKind kind) => kind switch
        {
            QualitySnapshotDiffKind.RowCountChanged => LT("Row count", "행 수"),
            QualitySnapshotDiffKind.ColumnAdded => LT("Column added", "컬럼 추가"),
            QualitySnapshotDiffKind.ColumnRemoved => LT("Column removed", "컬럼 삭제"),
            QualitySnapshotDiffKind.TypeChanged => LT("Type changed", "타입 변경"),
            QualitySnapshotDiffKind.MissingRateChanged => LT("Missing rate", "결측률"),
            QualitySnapshotDiffKind.UniqueCountChanged => LT("Unique count", "고유값 수"),
            QualitySnapshotDiffKind.NumericMinChanged => LT("Numeric min", "수치 최소"),
            QualitySnapshotDiffKind.NumericMaxChanged => LT("Numeric max", "수치 최대"),
            QualitySnapshotDiffKind.NumericMeanChanged => LT("Numeric mean", "수치 평균"),
            QualitySnapshotDiffKind.SentinelAppeared => LT("Sentinel candidate appeared", "위장결측 후보 출현"),
            QualitySnapshotDiffKind.SentinelDisappeared => LT("Sentinel candidate disappeared", "위장결측 후보 소멸"),
            QualitySnapshotDiffKind.FindingNew => LT("Finding new", "발견 신규"),
            QualitySnapshotDiffKind.FindingResolved => LT("Finding resolved", "발견 해소"),
            QualitySnapshotDiffKind.FindingPersisting => LT("Finding persists", "발견 지속"),
            QualitySnapshotDiffKind.CheckNotComparable => LT("Check not comparable", "검사 비교 불가"),
            QualitySnapshotDiffKind.FindingNotRechecked => LT("Not rechecked", "재검사 안 됨"),
            _ => kind.ToString(),
        };

        private static string DiffDetail(QualitySnapshotDiffItem item)
        {
            static string N(double? v) => v is { } d ? d.ToString("0.##", CultureInfo.InvariantCulture) : "—";
            static string I(double? v) => v is { } d ? d.ToString("N0", CultureInfo.InvariantCulture) : "—";
            switch (item.Kind)
            {
                case QualitySnapshotDiffKind.RowCountChanged:
                    return $"{I(item.BaselineNumber)} → {I(item.CurrentNumber)} (Δ {N(item.Delta)})";
                case QualitySnapshotDiffKind.ColumnAdded:
                    return item.CurrentText ?? "";
                case QualitySnapshotDiffKind.ColumnRemoved:
                    return item.BaselineText ?? "";
                case QualitySnapshotDiffKind.TypeChanged:
                    return $"{item.BaselineText} → {item.CurrentText}";
                case QualitySnapshotDiffKind.MissingRateChanged:
                    return $"{N((item.BaselineNumber ?? 0) * 100)}% → {N((item.CurrentNumber ?? 0) * 100)}% (Δ {N(item.Delta)} pp)";
                case QualitySnapshotDiffKind.UniqueCountChanged:
                {
                    string ratio = item.Delta is { } r
                        ? r.ToString("+0.##%;-0.##%;0%", CultureInfo.InvariantCulture)
                        : LT("baseline unique count was 0", "기준 고유값이 0");
                    return $"{I(item.BaselineNumber)} → {I(item.CurrentNumber)} ({ratio})";
                }
                case QualitySnapshotDiffKind.NumericMinChanged:
                case QualitySnapshotDiffKind.NumericMaxChanged:
                case QualitySnapshotDiffKind.NumericMeanChanged:
                    return $"{N(item.BaselineNumber)} → {N(item.CurrentNumber)} (Δ {N(item.Delta)})";
                case QualitySnapshotDiffKind.SentinelAppeared:
                case QualitySnapshotDiffKind.SentinelDisappeared:
                    return LT($"candidate \"{item.CurrentText ?? item.BaselineText}\" — confirm, not an assertion",
                              $"후보 \"{item.CurrentText ?? item.BaselineText}\" — 확인 필요, 단정 아님");
                case QualitySnapshotDiffKind.FindingNew:
                case QualitySnapshotDiffKind.FindingResolved:
                case QualitySnapshotDiffKind.FindingPersisting:
                {
                    string check = item.CheckKind is { } k ? QualityText.KindName(k) : "";
                    string rule = item.RuleName.Length > 0 ? $" ({item.RuleName})" : "";
                    string counts = item.Kind == QualitySnapshotDiffKind.FindingPersisting
                        ? $"{I(item.BaselineNumber)} → {I(item.CurrentNumber)}"
                        : I(item.CurrentNumber ?? item.BaselineNumber);
                    return $"{check}{rule} · {counts}";
                }
                case QualitySnapshotDiffKind.CheckNotComparable:
                    return LT("duplicate-row check skipped on one side — not new or resolved",
                              "중복 행 검사를 한쪽에서 생략 — 신규/해소로 보지 않음");
                case QualitySnapshotDiffKind.FindingNotRechecked:
                {
                    string check = item.CheckKind is { } k ? QualityText.KindName(k) : "";
                    string rule = item.RuleName.Length > 0 ? $" ({item.RuleName})" : "";
                    return LT($"{check}{rule} · {I(item.BaselineNumber)} in baseline — run it again in this session to compare",
                              $"{check}{rule} · 기준선 {I(item.BaselineNumber)}건 — 이 세션에서 다시 실행해야 비교 가능");
                }
                default:
                    return "";
            }
        }


        private string QualityCheckDisplay(QualityFinding f) => QualityText.CheckTitle(f);

        private static string QualityExampleRows(QualityFinding f)
            => string.Join(", ", f.Examples.Take(5).Select(e => e.SourceRow));

        private string BuildQualityReportMarkdown(QualityReport r)
        {
            static string Md(string s) => s.Replace("|", "\\|").Replace("\n", " ");
            var sb = new StringBuilder();
            sb.AppendLine(LT("# Data Quality Report", "# 데이터 품질 보고서"));
            sb.AppendLine();
            sb.AppendLine($"- {LT("File", "파일")}: {r.SourceName} ({FormatBytes(r.SourceBytes)})");
            sb.AppendLine($"- {LT("Rows scanned", "검사 행수")}: {r.RowsScanned:N0} " +
                (r.ScannedFully ? LT("(full scan)", "(전수)") : LT("(partial)", "(일부)")));
            if (r.DuplicateRowCheckSkipped)
                sb.AppendLine($"- {LT("Duplicate-row check skipped (row-count guard)", "중복 행 검사 생략(행 수 가드)")}");
            sb.AppendLine($"- {LT("Scanned at", "검사 시각")}: {r.ScanTimestamp} · NanumCsvViewer {r.AppVersion}");
            sb.AppendLine();

            sb.AppendLine(LT("## Findings", "## 발견 항목"));
            sb.AppendLine($"| {LT("Severity", "심각도")} | {LT("Check", "검사")} | {LT("Column", "컬럼")} | {LT("Count", "건수")} | {LT("Dimension", "차원")} | {LT("Details", "세부")} | {LT("Example rows", "예시 행")} |");
            sb.AppendLine("|---|---|---|---:|---|---|---|");
            foreach (var f in r.Findings)
                sb.AppendLine($"| {QualityText.SeverityName(f.Severity)} | {Md(QualityCheckDisplay(f))} " +
                    $"| {Md(f.ColumnName.Length > 0 ? f.ColumnName : "—")} | {(f.Approximate ? "≈" : "")}{f.ViolationCount:N0} " +
                    $"| {QualityText.DimensionName(f.Dimension)} | {Md(QualityText.Detail(f))} | {QualityExampleRows(f)} |");
            sb.AppendLine();

            if (r.Columns.Count > 0)
            {
                sb.AppendLine(LT("## Column Profile", "## 컬럼 프로파일"));
                sb.AppendLine($"| # | {LT("Column", "컬럼")} | {LT("Type", "타입")} | {LT("Missing", "결측")} | {LT("Distinct", "고유값")} | {LT("Type viol.", "타입 위반")} | {LT("Codebook viol.", "코드북 위반")} | Min | Max |");
                sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---|---|");
                foreach (var c in r.Columns)
                {
                    string distinct = (c.DistinctIsLowerBound ? "≥" : "") + c.DistinctCount.ToString("N0");
                    string min = c.NumericMin?.ToString("G6", CultureInfo.InvariantCulture) ?? c.TemporalMin ?? "";
                    string max = c.NumericMax?.ToString("G6", CultureInfo.InvariantCulture) ?? c.TemporalMax ?? "";
                    sb.AppendLine($"| {c.Index + 1} | {Md(c.Name)} | {c.ExpectedType.DisplayName()} | {c.MissingCount:N0} " +
                        $"| {distinct} | {c.TypeViolationCount:N0} | {c.CodebookViolationCount:N0} | {min} | {max} |");
                }
            }
            return sb.ToString();
        }

        private string BuildQualityReportHtml(QualityReport r)
        {
            static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{H(LT("Data Quality Report", "데이터 품질 보고서"))} — {H(r.SourceName)}</title>");
            sb.AppendLine("<style>body{font-family:'Segoe UI',sans-serif;margin:24px;color:#222}" +
                "table{border-collapse:collapse;margin:12px 0;font-size:13px}" +
                "th,td{border:1px solid #ccc;padding:4px 10px;text-align:left}" +
                "th{background:#f0f0f0}td.num{text-align:right}" +
                ".crit{color:#c62828;font-weight:600}.warn{color:#e65100;font-weight:600}.info{color:#555}</style></head><body>");
            sb.AppendLine($"<h1>{H(LT("Data Quality Report", "데이터 품질 보고서"))}</h1>");
            sb.AppendLine("<ul>");
            sb.AppendLine($"<li>{H(LT("File", "파일"))}: {H(r.SourceName)} ({H(FormatBytes(r.SourceBytes))})</li>");
            sb.AppendLine($"<li>{H(LT("Rows scanned", "검사 행수"))}: {r.RowsScanned:N0} " +
                H(r.ScannedFully ? LT("(full scan)", "(전수)") : LT("(partial)", "(일부)")) + "</li>");
            if (r.DuplicateRowCheckSkipped)
                sb.AppendLine($"<li>{H(LT("Duplicate-row check skipped (row-count guard)", "중복 행 검사 생략(행 수 가드)"))}</li>");
            sb.AppendLine($"<li>{H(LT("Scanned at", "검사 시각"))}: {H(r.ScanTimestamp)} · NanumCsvViewer {H(r.AppVersion)}</li>");
            sb.AppendLine("</ul>");

            sb.AppendLine($"<h2>{H(LT("Findings", "발견 항목"))}</h2><table><tr>" +
                $"<th>{H(LT("Severity", "심각도"))}</th><th>{H(LT("Check", "검사"))}</th><th>{H(LT("Column", "컬럼"))}</th>" +
                $"<th>{H(LT("Count", "건수"))}</th><th>{H(LT("Dimension", "차원"))}</th><th>{H(LT("Details", "세부"))}</th>" +
                $"<th>{H(LT("Example rows", "예시 행"))}</th></tr>");
            foreach (var f in r.Findings)
            {
                string cls = f.Severity switch
                {
                    QualitySeverity.Critical => "crit",
                    QualitySeverity.Warning => "warn",
                    _ => "info",
                };
                sb.AppendLine($"<tr><td class=\"{cls}\">{H(QualityText.SeverityName(f.Severity))}</td>" +
                    $"<td>{H(QualityCheckDisplay(f))}</td><td>{H(f.ColumnName.Length > 0 ? f.ColumnName : "—")}</td>" +
                    $"<td class=\"num\">{(f.Approximate ? "≈" : "")}{f.ViolationCount:N0}</td>" +
                    $"<td>{H(QualityText.DimensionName(f.Dimension))}</td><td>{H(QualityText.Detail(f))}</td>" +
                    $"<td>{H(QualityExampleRows(f))}</td></tr>");
            }
            sb.AppendLine("</table>");

            if (r.Columns.Count > 0)
            {
                sb.AppendLine($"<h2>{H(LT("Column Profile", "컬럼 프로파일"))}</h2><table><tr>" +
                    $"<th>#</th><th>{H(LT("Column", "컬럼"))}</th><th>{H(LT("Type", "타입"))}</th>" +
                    $"<th>{H(LT("Missing", "결측"))}</th><th>{H(LT("Distinct", "고유값"))}</th>" +
                    $"<th>{H(LT("Type viol.", "타입 위반"))}</th><th>{H(LT("Codebook viol.", "코드북 위반"))}</th>" +
                    "<th>Min</th><th>Max</th></tr>");
                foreach (var c in r.Columns)
                {
                    string distinct = (c.DistinctIsLowerBound ? "≥" : "") + c.DistinctCount.ToString("N0");
                    string min = c.NumericMin?.ToString("G6", CultureInfo.InvariantCulture) ?? c.TemporalMin ?? "";
                    string max = c.NumericMax?.ToString("G6", CultureInfo.InvariantCulture) ?? c.TemporalMax ?? "";
                    sb.AppendLine($"<tr><td class=\"num\">{c.Index + 1}</td><td>{H(c.Name)}</td>" +
                        $"<td>{H(c.ExpectedType.DisplayName())}</td><td class=\"num\">{c.MissingCount:N0}</td>" +
                        $"<td class=\"num\">{H(distinct)}</td><td class=\"num\">{c.TypeViolationCount:N0}</td>" +
                        $"<td class=\"num\">{c.CodebookViolationCount:N0}</td><td>{H(min)}</td><td>{H(max)}</td></tr>");
                }
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }
    }
}
