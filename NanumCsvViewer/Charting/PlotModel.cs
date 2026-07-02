namespace NanumCsvViewer.Charting
{
    // 시각화(이슈 #19)의 순수 데이터 모델. WinForms/GDI+ 의존 없음 — 빌더(계산)가 만들고 렌더러가 그린다.
    // 설계 논쟁(Fable5·Codex·Grok 3자 합의) 결과: 계산/모델/렌더 3분리로 계산부를 xUnit 검증 가능하게 유지.

    public enum PlotAxisKind { Numeric, Category }

    /// <summary>플롯 축. Numeric은 Min/Max 범위, Category는 라벨 배열. TickLabels는 수치축의 인덱스 기반 라벨(시계열 빈).</summary>
    public sealed class PlotAxis
    {
        public PlotAxisKind Kind { get; init; } = PlotAxisKind.Numeric;
        public string Title { get; init; } = string.Empty;
        public double Min { get; set; }
        public double Max { get; set; }
        /// <summary>Category축의 카테고리(박스 그룹·히트맵 행/열·파레토 항목).</summary>
        public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
        /// <summary>수치축이지만 눈금 라벨이 인덱스 매핑일 때(시계열 빈 라벨). null이면 숫자 포맷.</summary>
        public IReadOnlyList<string>? TickLabels { get; init; }
        /// <summary>보조(우측) 축에서 %로 표기(파레토 누적%).</summary>
        public bool IsPercent { get; init; }
    }

    public enum PlotSeriesKind
    {
        Points,      // 산점도 점 (Xs/Ys)
        Line,        // 꺾은선/곡선 (Xs/Ys) — 회귀선·KDE·정규곡선·이동평균·누적%
        Bars,        // 수치축 막대 (BarLeft/BarRight/BarHeight) — 히스토그램
        CategoryBars,// 카테고리축 막대 (Ys, X=카테고리 인덱스) — 파레토·빈도
        Boxes,       // 박스플롯 (Boxes, X=카테고리 인덱스)
        HeatCells,   // 히트맵 셀 (Cells[row,col], NaN=빈 셀) — 상관행렬
        Density,     // 밀도 산점 (Xs/Ys 원자료 → 렌더 시 격자 비닝, 줌 시 재비닝)
    }

    /// <summary>박스플롯 한 상자의 통계(1.5×IQR 수염).</summary>
    public sealed record PlotBox(
        string Label, int Count, double Q1, double Median, double Q3,
        double WhiskerLow, double WhiskerHigh, IReadOnlyList<double> Outliers, double Mean);

    public sealed class PlotSeries
    {
        public PlotSeriesKind Kind { get; init; }
        public string Name { get; init; } = string.Empty;
        /// <summary>팔레트 색 인덱스(렌더러가 매핑).</summary>
        public int PaletteIndex { get; init; }
        public bool Dashed { get; init; }              // 정규곡선 등 보조선
        public bool UseSecondaryAxis { get; init; }    // 파레토 누적% 등

        public double[] Xs { get; init; } = Array.Empty<double>();
        public double[] Ys { get; init; } = Array.Empty<double>();

        // Bars (히스토그램: 빈 경계·높이)
        public double[] BarLeft { get; init; } = Array.Empty<double>();
        public double[] BarRight { get; init; } = Array.Empty<double>();
        public double[] BarHeight { get; init; } = Array.Empty<double>();

        public IReadOnlyList<PlotBox> Boxes { get; init; } = Array.Empty<PlotBox>();

        /// <summary>HeatCells: [행,열] 값(상관 r). NaN은 계산 불가 셀(상수 컬럼 등).</summary>
        public double[,]? Cells { get; init; }
        /// <summary>HeatCells 보조 행렬(p값) — 비유의 셀 채도 감쇠·툴팁용.</summary>
        public double[,]? CellsP { get; init; }

        /// <summary>Density/Points 전환 임계(초과 시 밀도 렌더). Points에는 미사용.</summary>
        public int DensityThreshold { get; init; } = 50_000;
    }

    /// <summary>차트 상단 통계 배지. Label=요약("SW p=0.0031"), Detail=클릭 시 보여줄 전체 결과 텍스트.</summary>
    public sealed record StatBadge(string Label, string Detail);

    /// <summary>기준선(평균·중앙값 등). Vertical=true면 x=Value 수직선.</summary>
    public sealed record ReferenceLine(double Value, bool Vertical, string Label, int PaletteIndex, bool Dashed = true);

    public sealed class PlotModel
    {
        public string Title { get; init; } = string.Empty;
        public PlotAxis XAxis { get; init; } = new();
        public PlotAxis YAxis { get; init; } = new();
        /// <summary>보조 Y축(파레토 누적%). null이면 없음.</summary>
        public PlotAxis? Y2Axis { get; init; }
        public IReadOnlyList<PlotSeries> Series { get; init; } = Array.Empty<PlotSeries>();
        public IReadOnlyList<StatBadge> Badges { get; init; } = Array.Empty<StatBadge>();
        public IReadOnlyList<ReferenceLine> ReferenceLines { get; init; } = Array.Empty<ReferenceLine>();
        /// <summary>렌더 정직성 배지: "통계 N=1,842,003(전수) · 렌더=밀도 격자" 등. 항상 표시.</summary>
        public string RenderNote { get; init; } = string.Empty;
        /// <summary>수치축 차트만 줌 허용(산점도·히스토그램·시계열·Q-Q).</summary>
        public bool AllowZoom { get; init; }
    }
}
