namespace NanumCsvViewer.Stats
{
    /// <summary>분석 중 렌더된 표 1개: 렌더 문자열(본문 안 위치 찾기용)과 구조화된 헤더·행.</summary>
    public sealed record CapturedTable(string Rendered, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

    /// <summary>
    /// 고급 통계 결과 보고서(이슈 #27 Phase 3). Text는 결과창 본문, Tables는 compute 중 렌더된 TextTable을
    /// 순서대로 모은 것 — 내보내기(HTML/Excel/PDF)가 텍스트 표를 실제 표로 바꾸는 데 쓴다.
    /// Model은 저장·ONNX 내보내기 가능한 적합 모형(없으면 null).
    /// </summary>
    public sealed record AdvancedReport(
        string Title,
        string Text,
        IReadOnlyList<CapturedTable> Tables,
        object? Model,
        DateTime CreatedAt);

    /// <summary>분석 compute의 반환값: 결과 텍스트 + 선택적 적합 모형.</summary>
    public sealed record AdvancedOutput(string Text, object? Model = null);

    /// <summary>
    /// 현재 비동기 흐름에서 렌더되는 TextTable을 모은다. RunAdvancedAsync가 compute 전후로 범위를 연다.
    /// 범위 밖(기존 분석·테스트)에서는 아무것도 기록하지 않는다.
    /// </summary>
    public sealed class ReportCapture : IDisposable
    {
        private static readonly AsyncLocal<ReportCapture?> Current = new();
        private readonly ReportCapture? _previous;
        private readonly List<CapturedTable> _tables = new();
        private readonly object _gate = new();

        private ReportCapture(ReportCapture? previous) => _previous = previous;

        public static ReportCapture Begin()
        {
            var capture = new ReportCapture(Current.Value);
            Current.Value = capture;
            return capture;
        }

        public IReadOnlyList<CapturedTable> Tables
        {
            get { lock (_gate) return _tables.ToArray(); }
        }

        internal static void Record(CapturedTable table)
        {
            var capture = Current.Value;
            if (capture is null) return;
            lock (capture._gate) capture._tables.Add(table);
        }

        public void Dispose()
        {
            if (ReferenceEquals(Current.Value, this)) Current.Value = _previous;
        }
    }
}
