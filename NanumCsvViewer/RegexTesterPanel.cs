using NanumCsvViewer.Csv;

namespace NanumCsvViewer
{
    /// <summary>정규식 대화상자 공용 보조(번역·표시용 한 줄 변환·시험 결과 문구).</summary>
    internal static class RegexUi
    {
        public static string LT(string en, string ko) => Loc.CurrentLanguage == "ko" ? ko : en;

        public static readonly Color ErrorColor = Color.FromArgb(214, 76, 76);

        /// <summary>목록에 보여 줄 한 줄 값(줄바꿈·탭 표시, 너무 길면 자름).</summary>
        public static string OneLine(string value, int max = 80)
        {
            string s = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return s.Length <= max ? s : s[..(max - 1)] + "…";
        }

        /// <summary>셀 기준 시험 결과의 요약 문구(시간 초과는 반드시 표시).</summary>
        public static string Describe(RegexTestResult r, long totalRows)
        {
            string scope = r.RowsScanned >= RegexReplace.TestMaxRows && totalRows > r.RowsScanned
                ? LT($"First {r.RowsScanned:N0} of {totalRows:N0} rows", $"전체 {totalRows:N0}행 중 앞 {r.RowsScanned:N0}행")
                : LT($"All {r.RowsScanned:N0} rows of the view", $"현재 뷰 {r.RowsScanned:N0}행 전체");
            string text = LT($"{scope}: {r.CellsMatched:N0} cell(s) match in {r.RowsMatched:N0} row(s).",
                             $"{scope}: {r.RowsMatched:N0}행의 셀 {r.CellsMatched:N0}개가 일치합니다.");
            return text + TimeoutNote(r.CellsTimedOut);
        }

        public static string TimeoutNote(long timedOut) => timedOut <= 0 ? "" : LT(
            $"\r\n⚠ {timedOut:N0} cell(s) took longer than {RegexSafety.MatchTimeout.TotalMilliseconds:N0} ms and were treated as NOT matching.",
            $"\r\n⚠ {timedOut:N0}개 셀이 {RegexSafety.MatchTimeout.TotalMilliseconds:N0}ms를 넘겨 일치하지 않는 것으로 처리했습니다.");
    }

    /// <summary>시험 한 번의 결과: 요약 문구 + 표본 줄. Error면 요약이 오류 메시지다.</summary>
    internal sealed record TesterOutput(string Text, IReadOnlyList<string> Samples, bool Error = false)
    {
        public static TesterOutput Fail(string message) => new(message, Array.Empty<string>(), true);
    }

    /// <summary>
    /// 입력이 멈춘 뒤(디바운스) 백그라운드에서 작업을 돌리고 결과를 UI 스레드로 돌려준다.
    /// 새 요청이 오면 진행 중이던 작업은 취소되고 그 결과는 버려진다.
    /// </summary>
    internal sealed class DebouncedRunner<T> : IDisposable
    {
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Control _owner;
        private CancellationTokenSource? _cts;
        private Func<CancellationToken, T>? _work;
        private Action<T>? _onResult;
        private Action<Exception>? _onError;
        private int _generation;
        private bool _disposed;

        public DebouncedRunner(Control owner, int delayMs = 350)
        {
            _owner = owner;
            _timer = new System.Windows.Forms.Timer { Interval = delayMs };
            _timer.Tick += (_, _) => { _timer.Stop(); Start(); };
        }

        /// <summary>작업을 예약한다(이전 예약·진행 중 작업은 취소). onResult/onError는 UI 스레드에서 호출된다.</summary>
        public void Schedule(Func<CancellationToken, T> work, Action<T> onResult, Action<Exception> onError)
        {
            if (_disposed) return;
            _cts?.Cancel();
            _work = work;
            _onResult = onResult;
            _onError = onError;
            _generation++;
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>예약·진행 중 작업을 취소하고 결과를 버린다.</summary>
        public void Cancel()
        {
            _timer.Stop();
            _cts?.Cancel();
            _generation++;
        }

        private void Start()
        {
            var work = _work;
            if (work is null || _disposed) return;
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            int generation = _generation;
            var onResult = _onResult!;
            var onError = _onError!;
            Task.Run(() =>
            {
                try
                {
                    var result = work(cts.Token);
                    Post(() => { if (generation == _generation && !cts.IsCancellationRequested) onResult(result); });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Post(() => { if (generation == _generation && !cts.IsCancellationRequested) onError(ex); });
                }
            });
        }

        private void Post(Action action)
        {
            if (_disposed || _owner.IsDisposed || !_owner.IsHandleCreated) return;
            try { _owner.BeginInvoke(action); } catch (InvalidOperationException) { /* 창이 닫히는 중(ObjectDisposedException 포함) */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// 패턴 시험 패널: 입력할 때마다(디바운스·취소 가능) 현재 뷰 앞쪽 행에서 일치 수와 표본 값을 보여 주고,
    /// 잘못된 패턴이면 이유를 그 자리에서 보여 준다. 고급 필터·바꾸기 대화상자가 함께 쓴다.
    /// </summary>
    internal sealed class RegexTesterPanel : Panel
    {
        private readonly Label _summary;
        private readonly TextBox _samples;
        private readonly DebouncedRunner<TesterOutput> _runner;
        private readonly ThemePalette _palette;

        public RegexTesterPanel(ThemePalette palette, string title)
        {
            _palette = palette;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Height = 150;

            var heading = new Label
            {
                Text = title, Dock = DockStyle.Top, AutoSize = false, Height = 20,
                Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold), ForeColor = palette.Accent,
            };
            _summary = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 40, ForeColor = palette.Text, Name = "testerSummary" };
            _samples = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
                Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = palette.Surface, ForeColor = palette.Text,
                BorderStyle = BorderStyle.FixedSingle, Name = "testerSamples",
            };
            Controls.Add(_samples);
            Controls.Add(_summary);
            Controls.Add(heading);
            _runner = new DebouncedRunner<TesterOutput>(this);
            Clear(RegexUi.LT("Type a pattern to test it on the current view.", "패턴을 입력하면 현재 뷰에서 바로 시험합니다."));
        }

        /// <summary>안내 문구만 보이고 표본은 비운다(작업은 취소).</summary>
        public void Clear(string message)
        {
            _runner.Cancel();
            Show(new TesterOutput(message, Array.Empty<string>()));
        }

        /// <summary>work를 디바운스 뒤 백그라운드에서 실행하고 결과를 표시. work가 던진 오류는 인라인 오류로 표시된다.</summary>
        public void Schedule(Func<CancellationToken, TesterOutput> work)
        {
            _summary.ForeColor = _palette.Text;
            _summary.Text = RegexUi.LT("Testing…", "시험 중…");
            _runner.Schedule(work, Show, ex => Show(TesterOutput.Fail(ex.Message)));
        }

        public void Show(TesterOutput output)
        {
            _summary.ForeColor = output.Error ? RegexUi.ErrorColor : _palette.Text;
            _summary.Text = (output.Error ? "⚠ " : "") + output.Text;
            _samples.Text = output.Samples.Count == 0 ? "" : string.Join("\r\n", output.Samples);
        }

        public string SummaryText => _summary.Text;
        public string SamplesText => _samples.Text;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _runner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 고급(표현식) 필터 입력 대화상자: 연산자 안내 + 표현식 입력 + 시험 패널(현재 뷰 앞 10,000행에서 일치 행 수와 표본).
    /// 표현식이 틀리거나 정규식이 잘못되면 시험 패널에 그 이유가 바로 나온다.
    /// </summary>
    internal sealed class AdvancedFilterDialog : Form
    {
        private readonly TextBox _input;
        private readonly RegexTesterPanel _tester;
        private readonly Func<string, CancellationToken, TesterOutput> _test;

        public string Expression => _input.Text.Trim();

        public AdvancedFilterDialog(ThemePalette palette, string? initial, Func<string, CancellationToken, TesterOutput> test)
        {
            _test = test;
            Text = RegexUi.LT("Advanced Filter", "고급 필터");
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;
            BackColor = palette.Window;
            ForeColor = palette.Text;
            Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
            ClientSize = new Size(600, 470);
            MinimumSize = new Size(520, 430);
            Padding = new Padding(14);

            var help = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 168, ForeColor = palette.Text, Name = "advancedFilterHelp",
                Text = RegexUi.LT(
                    "e.g.  age > 30 AND city = \"Seoul\"\r\n" +
                    "Operators: = != < <= > >= contains startswith endswith matches (regex, ignores case) matches_cs (regex, case-sensitive)\r\n" +
                    "Put ! before a text operator or use NOT to negate: name !matches \"^test\".  Combine with AND, OR, NOT and ( ).\r\n" +
                    "Use * as the column to test any column: * matches \"error|fail\".\r\n" +
                    "Regex tip (Hangul): name matches \"^\\p{IsHangulSyllables}+$\" — only Korean syllables. Wrap values containing [ ] in double quotes.",
                    "예:  age > 30 AND city = \"서울\"\r\n" +
                    "연산자: = != < <= > >= contains startswith endswith matches(정규식, 대소문자 무시) matches_cs(정규식, 대소문자 구분)\r\n" +
                    "텍스트 연산자 앞에 !를 붙이거나 NOT을 쓰면 부정: name !matches \"^test\".  AND, OR, NOT, ( )로 조합합니다.\r\n" +
                    "컬럼 자리에 *를 쓰면 아무 컬럼이나 검사: * matches \"error|fail\".\r\n" +
                    "정규식 팁(한글): name matches \"^\\p{IsHangulSyllables}+$\" — 한글 음절만. [ ]가 들어간 값은 큰따옴표로 감싸세요."),
            };
            var exprLabel = new Label { Text = RegexUi.LT("Expression", "표현식"), Dock = DockStyle.Top, Height = 22, ForeColor = palette.Text };
            _input = new TextBox
            {
                Dock = DockStyle.Top, BackColor = palette.Surface, ForeColor = palette.Text, BorderStyle = BorderStyle.FixedSingle,
                Text = initial ?? "", Name = "advancedFilterExpression",
            };
            _tester = new RegexTesterPanel(palette, RegexUi.LT("Test on the current view", "현재 뷰에서 시험")) { Dock = DockStyle.Fill, Name = "advancedFilterTester" };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = RegexUi.LT("Cancel", "취소"), DialogResult = DialogResult.Cancel, Size = new Size(88, 28) };
            var ok = new Button { Text = RegexUi.LT("Apply", "적용"), DialogResult = DialogResult.OK, Size = new Size(88, 28) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(_tester);
            Controls.Add(buttons);
            Controls.Add(_input);
            Controls.Add(exprLabel);
            Controls.Add(help);

            _input.TextChanged += (_, _) => Retest();
            Shown += (_, _) => { _input.Focus(); _input.SelectionStart = _input.TextLength; Retest(); };
        }

        private void Retest()
        {
            string expr = _input.Text.Trim();
            if (expr.Length == 0) { _tester.Clear(RegexUi.LT("Type an expression to test it on the current view.", "표현식을 입력하면 현재 뷰에서 바로 시험합니다.")); return; }
            _tester.Schedule(ct => _test(expr, ct));
        }
    }
}
