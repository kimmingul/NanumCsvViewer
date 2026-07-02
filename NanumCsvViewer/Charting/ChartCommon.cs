namespace NanumCsvViewer.Charting
{
    /// <summary>
    /// 차트 공통 상수·헬퍼. ChartControl(피벗차트)의 검증된 로직과 동일 구현 —
    /// 회귀 리스크를 피하기 위해 ChartControl은 수정하지 않고 여기 복제해 시각화 계열이 공유한다(설계 논쟁 합의).
    /// </summary>
    internal static class ChartCommon
    {
        /// <summary>시리즈 팔레트(ChartControl과 동일 12색 — 색약 친화 계열).</summary>
        public static readonly Color[] SeriesColors =
        {
            Color.FromArgb(46, 111, 176), Color.FromArgb(27, 158, 119), Color.FromArgb(217, 95, 2),
            Color.FromArgb(117, 112, 179), Color.FromArgb(231, 41, 138), Color.FromArgb(102, 166, 30),
            Color.FromArgb(230, 171, 2), Color.FromArgb(166, 118, 29), Color.FromArgb(102, 102, 102),
            Color.FromArgb(0, 158, 115), Color.FromArgb(213, 94, 0), Color.FromArgb(86, 156, 230),
        };

        public static Color SeriesColor(int index) => SeriesColors[((index % SeriesColors.Length) + SeriesColors.Length) % SeriesColors.Length];

        /// <summary>Heckbert의 nice number(축 눈금 산정) — ChartControl과 동일.</summary>
        public static double NiceNum(double x, bool round)
        {
            if (x <= 0) return 1;
            double exp = Math.Floor(Math.Log10(x));
            double f = x / Math.Pow(10, exp);
            double nf = round
                ? (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10)
                : (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10);
            return nf * Math.Pow(10, exp);
        }

        public static string FormatTick(double v)
            => v == Math.Truncate(v) && Math.Abs(v) < 1e15 ? v.ToString("#,##0") : v.ToString("#,##0.###");
    }
}
