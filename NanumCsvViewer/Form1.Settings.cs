using Microsoft.Win32;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    // v3.2: 설정 대화 상자(Settings/SettingsDialog.cs)가 부르는 적용 지점. 설정 값은 AppSettings에 저장하고 화면에 즉시 반영한다.
    public partial class Form1
    {
        /// <summary>설정 대화 상자를 연다. page: general · panels · grid · files · ai · shortcuts (null이면 마지막으로 본 쪽). owner: 모달 대화 상자 위에서 열 때 그 대화 상자(null이면 이 창).</summary>
        internal void ShowSettings(string? page = null, IWin32Window? owner = null)
        {
            bool again;
            do
            {
                using var dlg = new SettingsDialog(this, page);
                dlg.ShowDialog(owner ?? this);
                again = dlg.ReopenRequested;   // 테마를 바꿔 적용했다: 새 색으로 같은 쪽을 다시 연다
                page = dlg.CurrentPage?.Id;
            } while (again && !IsDisposed);
        }

        /// <summary>
        /// 분석 메모리 예산 초과 안내. 본문 뒤에 설정에서 상한을 올릴 수 있다는 문장을 붙이고, 예(Yes)를 누르면 설정의 '파일과 데이터' 쪽을 연다.
        /// </summary>
        internal void ShowMemoryBudgetExceeded(IWin32Window? owner, string message, string? caption = null)
        {
            if (IsDisposed || _closing) return;
            string text = message + "\n\n" + LT(
                "The analysis memory cap can be raised in Settings (Files & Data). Open Settings now?",
                "설정에서 상한을 올릴 수 있습니다. 지금 설정(파일과 데이터)을 여시겠습니까?");
            var answer = MessageBox.Show(owner ?? this, text, caption ?? Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes && !IsDisposed) ShowSettings("files", owner);
        }

        internal AppSettings AppSettingsRef => _settings;
        internal ThemePalette PaletteRef => _palette;
        internal string? WorkspaceFileName => _wfFilePath is null ? null : Path.GetFileName(_wfFilePath);

        // ---- 테마: 시스템 / 밝게 / 어둡게 ---------------------------------------------------------------------

        private static AppTheme ResolveTheme(string setting) => setting switch
        {
            "Dark" => AppTheme.Dark,
            "Light" => AppTheme.Light,
            _ => ThemeManager.DetectSystem(),
        };

        /// <summary>테마 설정을 적용한다: ""(Windows 시스템 테마를 따름) | "Light" | "Dark".</summary>
        internal void ApplyThemeSetting(string setting)
        {
            _settings.Theme = setting is "Light" or "Dark" ? setting : "";
            _settings.Save();
            var theme = ResolveTheme(_settings.Theme);
            if (theme != _theme) ApplyTheme(theme);
        }

        // 설정이 "시스템"일 때 Windows 색 모드가 바뀌면 따라간다(실행 중에도).
        private void WatchSystemTheme()
        {
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        }

        private void UnwatchSystemTheme()
        {
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        }

        private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || !string.IsNullOrEmpty(_settings.Theme)) return;
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || !string.IsNullOrEmpty(_settings.Theme)) return;
                    var theme = ThemeManager.DetectSystem();
                    if (theme != _theme) ApplyTheme(theme);
                }));
            }
            catch (InvalidOperationException) { /* 닫는 중 */ }
        }

        // ---- 언어: 자동 / English / 한국어 ---------------------------------------------------------------------


        /// <summary>언어 설정을 적용한다: "auto"(Windows 언어를 따름) | "en" | "ko". 메뉴·도구 모음·열린 창의 글자를 바로 바꾼다.</summary>
        internal void ApplyLanguageSetting(string setting)
        {
            setting = setting is "en" or "ko" ? setting : "auto";
            _settings.Language = setting;
            _settings.Save();
            Loc.Apply(setting);
            ApplyLocalization();         // 모든 정적 텍스트 즉시 갱신
            RefreshDynamicTexts();       // 상태바 등 동적 텍스트 갱신
        }
        /// <summary>SPSS·SAS 필드 라벨이 지금 켜져 있는가(라벨 대상 파일이 열려 있으면 그 파일의 상태, 아니면 설정).</summary>
        internal bool FieldLabelsOn => _workbook is { SupportsFieldLabels: true } wb ? wb.ShowLabels : _settings.ShowFieldLabels;

        /// <summary>설정 대화 상자 그리드 쪽의 적용: 타입 배지·필드 라벨·셀 최대 줄 수·글꼴 크기.</summary>
        internal void ApplyGridSettings(bool typeBadges, bool fieldLabels, int maxCellLines, float fontSize)
        {
            int lines = Math.Clamp(maxCellLines, 1, 20);
            float size = fontSize <= 0 ? 0f : Math.Clamp(fontSize, 7f, 24f);
            bool metrics = lines != _settings.MaxCellLines || size != _settings.GridFontSize;
            _settings.MaxCellLines = lines;
            _settings.GridFontSize = size;
            _settings.Save();
            if (typeBadges != _settings.ShowTypeBadges) SetShowTypeBadges(typeBadges);
            if (fieldLabels != FieldLabelsOn) SetShowFieldLabels(fieldLabels);
            if (metrics) RefreshGridMetrics();
        }

        // ---- 그리드 --------------------------------------------------------------------------------------------

        private Font? _gridCustomFont;

        /// <summary>설정의 그리드 글꼴 크기를 그리드에 반영한다(0이면 창 글꼴). 행 높이 계산값도 함께 갱신한다.</summary>
        private void ApplyGridFont()
        {
            float size = _settings.GridFontSize;
            var old = _gridCustomFont;
            if (size > 0)
            {
                _gridCustomFont = new Font(Font.FontFamily, size, Font.Style);
                grid.Font = _gridCustomFont;
            }
            else
            {
                _gridCustomFont = null;
                grid.Font = Font;
            }
            old?.Dispose();
            grid.ColumnHeadersHeight = Math.Max(LogicalToDeviceUnits(30), grid.Font.Height + LogicalToDeviceUnits(12));
        }

        /// <summary>글꼴 크기·최대 줄 수가 바뀌었다: 행 높이를 다시 계산하게 한다(현재 셀·스크롤 위치는 유지).</summary>
        internal void RefreshGridMetrics()
        {
            ApplyGridFont();
            _lineHeight = grid.Font.Height + 2;
            _singleLineHeight = Math.Max(grid.RowTemplate.Height, _lineHeight + 6);
            int rows = grid.RowCount;
            if (rows <= 0) { grid.Invalidate(); return; }
            int cur = grid.CurrentCell?.RowIndex ?? -1, col = grid.CurrentCell?.ColumnIndex ?? -1, top = grid.FirstDisplayedScrollingRowIndex;
            grid.RowCount = 0;
            grid.RowCount = rows;
            try
            {
                if (top >= 0 && top < rows) grid.FirstDisplayedScrollingRowIndex = top;
                if (cur >= 0 && cur < rows && col >= 0 && col < grid.ColumnCount) grid.CurrentCell = grid[col, cur];
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
            grid.Invalidate();
        }

        // ---- 파일 ----------------------------------------------------------------------------------------------

        /// <summary>
        /// 문서를 연다. 설정의 기본 인코딩이 "auto"가 아니면, BOM 없이 감지된 UTF-8/CP949 파일에 한해 감지 결과 대신 그 인코딩을 쓴다
        /// (BOM이 있는 파일·UTF-16은 BOM이 정확하므로 그대로). 파일 단위로는 인코딩 메뉴·상태바로 언제든 바꿀 수 있다.
        /// </summary>
        private VirtualCsvDocument OpenDocument(string path)
        {
            var doc = VirtualCsvDocument.Open(path);
            string want = _settings.DefaultEncoding;
            if (want != AppSettings.AutoEncoding && want != doc.EncodingName
                && doc.EncodingName is EncodingDetector.Utf8 or EncodingDetector.Cp949)
            {
                try { doc.ChangeEncoding(want); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
            }
            return doc;
        }

        /// <summary>'닫을 때 인덱스 캐시 삭제' 설정을 적용한다(보기 메뉴의 체크도 맞춘다).</summary>
        internal void ApplyDeleteIndexOnClose(bool value)
        {
            _settings.DeleteIndexOnClose = value;
            _settings.Save();
            if (_deleteIndexOnCloseMenu is not null && _deleteIndexOnCloseMenu.Checked != value) _deleteIndexOnCloseMenu.Checked = value;
        }
    }
}
