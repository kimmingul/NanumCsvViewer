using System.Drawing;

namespace NanumCsvViewer
{
    /// <summary>토글할 수 있는 화면 영역. 메뉴·도구 모음·설정·작업 공간 파일이 모두 이 목록으로 말한다.</summary>
    internal enum PanelKind { Detail, Agent, Explorer, Facets, Findings, CellBar }

    /// <summary>
    /// 패널 표시 상태 + 폭. 앱 시작 상태(<see cref="AppSettings.StartupPanels"/>)·마지막 상태·작업 공간 파일(.ncvws)의 layout 절이 모두 이 모양이다.
    /// 폭은 96 DPI 기준 논리 단위이고 0이면 "지정 안 함"(앱 기본 폭을 쓴다).
    /// </summary>
    public sealed class PanelLayout
    {
        /// <summary>AI 에이전트 패널(기본 켜짐 — omp는 첫 메시지를 보낼 때 시작한다).</summary>
        public bool Agent { get; set; } = true;
        /// <summary>선택 행 전체를 보여 주는 행 상세 패널.</summary>
        public bool Detail { get; set; }
        /// <summary>패싯 패널(문서가 열려야 보인다).</summary>
        public bool Facets { get; set; }
        /// <summary>작업 공간 탐색기.</summary>
        public bool Explorer { get; set; }
        /// <summary>데이터 품질 검사 결과 패널.</summary>
        public bool Findings { get; set; }
        /// <summary>그리드 위의 셀 주소·값 표시줄.</summary>
        public bool CellBar { get; set; } = true;

        public int DetailWidth { get; set; }
        public int ExplorerWidth { get; set; }
        public int AgentWidth { get; set; }

        public const int MinWidth = 120;
        public const int MaxWidth = 4000;

        internal bool Get(PanelKind kind) => kind switch
        {
            PanelKind.Detail => Detail,
            PanelKind.Agent => Agent,
            PanelKind.Explorer => Explorer,
            PanelKind.Facets => Facets,
            PanelKind.Findings => Findings,
            _ => CellBar,
        };

        internal void Set(PanelKind kind, bool visible)
        {
            switch (kind)
            {
                case PanelKind.Detail: Detail = visible; break;
                case PanelKind.Agent: Agent = visible; break;
                case PanelKind.Explorer: Explorer = visible; break;
                case PanelKind.Facets: Facets = visible; break;
                case PanelKind.Findings: Findings = visible; break;
                default: CellBar = visible; break;
            }
        }

        public PanelLayout Clone() => (PanelLayout)MemberwiseClone();

        /// <summary>손으로 고친 파일·설정에 견디도록 폭을 범위 안으로 다듬는다(0은 그대로 "지정 안 함").</summary>
        public PanelLayout Normalized()
        {
            static int W(int w) => w <= 0 ? 0 : Math.Clamp(w, MinWidth, MaxWidth);
            DetailWidth = W(DetailWidth);
            ExplorerWidth = W(ExplorerWidth);
            AgentWidth = W(AgentWidth);
            return this;
        }

        /// <summary>표시 상태(폭 제외)가 같은가 — 테스트·변경 감지용.</summary>
        public bool SameVisibility(PanelLayout other) =>
            Agent == other.Agent && Detail == other.Detail && Facets == other.Facets && Explorer == other.Explorer
            && Findings == other.Findings && CellBar == other.CellBar;
    }

    /// <summary>창 위치·크기·최대화 상태. 장치 픽셀 단위.</summary>
    public sealed class WindowGeometry
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }

        /// <summary>작은 창에서도 쓸 수 있는 최소 크기(폼의 최소 크기와 별개로 저장값 검증용).</summary>
        public const int MinWidth = 400;
        public const int MinHeight = 300;

        /// <summary>창 상단 띠가 보여야 하는 최소 폭·높이(이만큼 어느 모니터에 걸쳐야 끌어서 옮길 수 있다).</summary>
        private const int MinVisibleWidth = 120;
        private const int MinVisibleHeight = 40;

        /// <summary>
        /// 저장된 위치를 지금 모니터 구성에 맞춘다. 크기가 말이 안 되면 null(저장값 버림 → 기본 배치).
        /// 창의 윗부분이 어떤 모니터의 작업 영역에도 충분히 걸치지 않으면(분리된 모니터) 가장 가까운 작업 영역 안으로 옮기고,
        /// 작업 영역보다 크면 줄인다.
        /// </summary>
        public static Rectangle? Fit(WindowGeometry? saved, IReadOnlyList<Rectangle> workingAreas)
        {
            if (saved is null || workingAreas.Count == 0) return null;
            if (saved.Width < MinWidth || saved.Height < MinHeight || saved.Width > 20000 || saved.Height > 20000) return null;
            var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
            var strip = new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Min(bounds.Height, 60));
            foreach (var area in workingAreas)
            {
                var overlap = Rectangle.Intersect(area, strip);
                if (overlap.Width >= MinVisibleWidth && overlap.Height >= MinVisibleHeight) return Clamp(bounds, area);
            }

            // 보이는 곳이 없다: 중심이 가장 가까운 작업 영역으로.
            var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            Rectangle best = workingAreas[0];
            double bestDistance = double.MaxValue;
            foreach (var area in workingAreas)
            {
                int dx = Math.Max(Math.Max(area.Left - center.X, 0), center.X - area.Right);
                int dy = Math.Max(Math.Max(area.Top - center.Y, 0), center.Y - area.Bottom);
                double d = (double)dx * dx + (double)dy * dy;
                if (d < bestDistance) { bestDistance = d; best = area; }
            }
            return Clamp(bounds, best);
        }

        // 작업 영역보다 크면 줄이고, 삐져나온 만큼 안으로 밀어 넣는다.
        private static Rectangle Clamp(Rectangle b, Rectangle area)
        {
            int w = Math.Min(b.Width, area.Width), h = Math.Min(b.Height, area.Height);
            int x = Math.Clamp(b.X, area.Left, Math.Max(area.Left, area.Right - w));
            int y = Math.Clamp(b.Y, area.Top, Math.Max(area.Top, area.Bottom - h));
            return new Rectangle(x, y, w, h);
        }
    }
}
