using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Charting
{
    /// <summary>
    /// 시각화 계산 엔진(이슈 #19). 렌더러에서 수식을 배제하고 여기 모아 xUnit + scipy 참조값으로 검증한다.
    /// 대용량 원칙(3자 설계 합의): 통계는 항상 전수(뷰 cap 내), 축약은 렌더링 단계에서만.
    /// </summary>
    public static class PlotMath
    {
        // ---- 선형 회귀 (산점도 회귀선) ----

        public sealed record LinearFit(double Slope, double Intercept, double R, double RSquared, int Count);

        /// <summary>최소제곱 직선 적합. 유한 쌍 2개 미만이거나 x 분산 0이면 null.</summary>
        public static LinearFit? OlsFit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        {
            int n = Math.Min(xs.Count, ys.Count);
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                double x = xs[i], y = ys[i];
                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                m++; sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            }
            if (m < 2) return null;
            double varX = sxx - sx * sx / m;
            if (varX <= 0) return null;
            double covXy = sxy - sx * sy / m;
            double slope = covXy / varX;
            double intercept = (sy - slope * sx) / m;
            double varY = syy - sy * sy / m;
            double r = varY <= 0 ? 0 : covXy / Math.Sqrt(varX * varY);
            r = Math.Max(-1, Math.Min(1, r));
            return new LinearFit(slope, intercept, r, r * r, m);
        }

        // ---- 커널 밀도 추정 (히스토그램 KDE 오버레이) ----

        /// <summary>
        /// 교과서 Silverman rule-of-thumb: h = 0.9·min(σ, IQR/1.34)·n^(-1/5). scipy gaussian_kde 기본과 다른 정의임에 주의.
        /// sampleSd에 이미 계산된 표본 표준편차(Describe 등)를 넘기면 재순회를 생략(대용량 이중 계산·정의 이원화 방지).
        /// </summary>
        public static double SilvermanBandwidth(double[] sorted, double? sampleSd = null)
        {
            int n = sorted.Length;
            if (n < 2) return 1;
            double sd;
            if (sampleSd is { } given && given >= 0)
            {
                sd = given;
            }
            else
            {
                double mean = 0;
                foreach (double v in sorted) mean += v;
                mean /= n;
                double ss = 0;
                foreach (double v in sorted) ss += (v - mean) * (v - mean);
                sd = Math.Sqrt(ss / (n - 1));
            }
            double iqr = CsvAnalytics.Percentile(sorted, 0.75) - CsvAnalytics.Percentile(sorted, 0.25);
            double spread = iqr > 0 ? Math.Min(sd, iqr / 1.34) : sd;
            if (spread <= 0) spread = sd > 0 ? sd : 1;
            return 0.9 * spread * Math.Pow(n, -0.2);
        }

        /// <summary>정확 KDE 평가(가우시안 커널, O(n)). 검증·소규모용.</summary>
        public static double KdeAt(double x, IReadOnlyList<double> values, double bandwidth)
        {
            if (values.Count == 0 || bandwidth <= 0) return 0;
            const double inv = 0.3989422804014327; // 1/√(2π)
            double sum = 0;
            foreach (double v in values)
            {
                double z = (x - v) / bandwidth;
                sum += Math.Exp(-0.5 * z * z);
            }
            return sum * inv / (values.Count * bandwidth);
        }

        public sealed record KdeCurve(double[] X, double[] Density);

        /// <summary>
        /// binned KDE: 값들을 내부 미세 빈(기본 512)으로 집계한 뒤 격자점에 커널 합성 — O(bins×grid).
        /// 2M행에서도 즉시. 오차는 빈 폭 수준(테스트에서 exact 대비 상한 검증).
        /// </summary>
        public static KdeCurve BinnedKdeCurve(double[] sorted, double bandwidth, int gridPoints = 256, int bins = 512)
        {
            if (sorted.Length == 0 || bandwidth <= 0) return new KdeCurve(Array.Empty<double>(), Array.Empty<double>());
            double lo = sorted[0] - 3 * bandwidth, hi = sorted[^1] + 3 * bandwidth;
            if (hi <= lo) hi = lo + 1;

            // 1) 미세 빈 집계
            var counts = new int[bins];
            double binW = (hi - lo) / bins;
            foreach (double v in sorted)
            {
                int b = (int)((v - lo) / binW);
                if (b < 0) b = 0; else if (b >= bins) b = bins - 1;
                counts[b]++;
            }

            // 2) 격자점마다 빈 중심 기준 커널 합성
            var xs = new double[gridPoints];
            var ds = new double[gridPoints];
            const double inv = 0.3989422804014327;
            double scale = inv / (sorted.Length * bandwidth);
            for (int g = 0; g < gridPoints; g++)
            {
                double x = lo + (hi - lo) * g / (gridPoints - 1);
                double sum = 0;
                for (int b = 0; b < bins; b++)
                {
                    if (counts[b] == 0) continue;
                    double center = lo + (b + 0.5) * binW;
                    double z = (x - center) / bandwidth;
                    if (Math.Abs(z) > 6) continue; // 6σ 밖 기여 무시
                    sum += counts[b] * Math.Exp(-0.5 * z * z);
                }
                xs[g] = x;
                ds[g] = sum * scale;
            }
            return new KdeCurve(xs, ds);
        }

        /// <summary>정규분포 확률밀도.</summary>
        public static double NormalPdf(double x, double mean, double sd)
        {
            if (sd <= 0) return 0;
            double z = (x - mean) / sd;
            return Math.Exp(-0.5 * z * z) / (sd * Math.Sqrt(2 * Math.PI));
        }

        // ---- Q-Q 플롯 ----

        public sealed record QqPoints(double[] Theoretical, double[] Sample);

        /// <summary>
        /// 정규 Q-Q 점 생성. Blom형 위치 (i-0.375)/(n+0.25)에 표준정규 분위수(기존 Acklam 근사 재사용).
        /// n이 maxPoints를 넘으면 결정적 분위 시닝(무작위 아님 — 재현 가능, 꼬리 보존).
        /// </summary>
        public static QqPoints QqNormalPoints(double[] sorted, int maxPoints = 2000)
        {
            int n = sorted.Length;
            if (n == 0) return new QqPoints(Array.Empty<double>(), Array.Empty<double>());
            int m = Math.Min(n, Math.Max(3, maxPoints));
            var theo = new double[m];
            var samp = new double[m];
            for (int k = 0; k < m; k++)
            {
                // 원 표본에서의 순서(1-기반): m개 균등 배치, 양끝 포함
                double pos = m == 1 ? (n + 1) / 2.0 : 1 + (double)(n - 1) * k / (m - 1);
                int i = (int)Math.Round(pos);
                if (i < 1) i = 1; else if (i > n) i = n;
                double p = (i - 0.375) / (n + 0.25);
                theo[k] = CsvStatistics.StandardNormalQuantile(p);
                samp[k] = sorted[i - 1];
            }
            return new QqPoints(theo, samp);
        }

        // ---- 박스플롯 ----

        public sealed record BoxStatsResult(
            int Count, double Q1, double Median, double Q3,
            double WhiskerLow, double WhiskerHigh, double[] Outliers, double Mean);

        /// <summary>1.5×IQR 수염 규칙 박스 통계. 값이 없으면 null. 분위수는 R type-7(기존 Percentile).</summary>
        public static BoxStatsResult? ComputeBoxStats(IReadOnlyList<double> values)
        {
            if (values.Count == 0) return null;
            var sorted = values.ToArray();
            Array.Sort(sorted);
            double q1 = CsvAnalytics.Percentile(sorted, 0.25);
            double med = CsvAnalytics.Percentile(sorted, 0.5);
            double q3 = CsvAnalytics.Percentile(sorted, 0.75);
            double iqr = q3 - q1;
            double lo = q1 - 1.5 * iqr, hi = q3 + 1.5 * iqr;

            double whiskLo = sorted[^1], whiskHi = sorted[0];
            double sum = 0;
            var outliers = new List<double>();
            foreach (double v in sorted)
            {
                sum += v;
                if (v < lo || v > hi) outliers.Add(v);
                else
                {
                    if (v < whiskLo) whiskLo = v;
                    if (v > whiskHi) whiskHi = v;
                }
            }
            if (outliers.Count == sorted.Length) { whiskLo = q1; whiskHi = q3; } // 전부 이상치(퇴화) 가드
            return new BoxStatsResult(sorted.Length, q1, med, q3, whiskLo, whiskHi, outliers.ToArray(), sum / sorted.Length);
        }

        // ---- 히스토그램 빈 수 ----

        /// <summary>Freedman-Diaconis 빈 수: width = 2·IQR·n^(-1/3). IQR 0이면 Sturges 폴백. [min,max] 범위로 클램프.</summary>
        public static int FreedmanDiaconisBins(double[] sorted, int minBins = 5, int maxBins = 100)
        {
            int n = sorted.Length;
            if (n < 2) return minBins;
            double iqr = CsvAnalytics.Percentile(sorted, 0.75) - CsvAnalytics.Percentile(sorted, 0.25);
            double range = sorted[^1] - sorted[0];
            int bins;
            if (iqr <= 0 || range <= 0)
                bins = (int)Math.Ceiling(Math.Log2(n) + 1); // Sturges
            else
                bins = (int)Math.Ceiling(range / (2 * iqr * Math.Pow(n, -1.0 / 3)));
            return Math.Max(minBins, Math.Min(maxBins, bins));
        }

        // ---- 상관 행렬 (단일 패스 누적, pairwise-complete) ----

        public sealed record CorrelationMatrixResult(double[,] R, double[,] P, int[,] N);

        /// <summary>
        /// Pearson 상관 행렬. columns[c][row]에 비수치는 NaN. 쌍별 완전관측(pairwise-complete)으로
        /// 행 1회 순회당 모든 쌍의 적률을 누적 — O(rows·k²), 리스트 재할당 없음(Codex 단일패스 채택).
        /// 상수 컬럼·n&lt;3 쌍은 r=NaN. p는 Student-t 양측(기존 분포함수 재사용).
        /// </summary>
        public static CorrelationMatrixResult PearsonMatrix(IReadOnlyList<double[]> columns)
        {
            int k = columns.Count;
            int rows = k == 0 ? 0 : columns[0].Length;
            var n = new int[k, k];
            var sx = new double[k, k]; var sy = new double[k, k];
            var sxx = new double[k, k]; var syy = new double[k, k]; var sxy = new double[k, k];

            for (int r = 0; r < rows; r++)
            {
                for (int i = 0; i < k; i++)
                {
                    double x = columns[i][r];
                    if (!double.IsFinite(x)) continue;
                    for (int j = i; j < k; j++)
                    {
                        double y = columns[j][r];
                        if (!double.IsFinite(y)) continue;
                        n[i, j]++;
                        sx[i, j] += x; sy[i, j] += y;
                        sxx[i, j] += x * x; syy[i, j] += y * y; sxy[i, j] += x * y;
                    }
                }
            }

            var rm = new double[k, k];
            var pm = new double[k, k];
            var nm = new int[k, k];
            for (int i = 0; i < k; i++)
            {
                for (int j = i; j < k; j++)
                {
                    int m = n[i, j];
                    double rv = double.NaN, pv = double.NaN;
                    if (m >= 3)
                    {
                        double varX = sxx[i, j] - sx[i, j] * sx[i, j] / m;
                        double varY = syy[i, j] - sy[i, j] * sy[i, j] / m;
                        if (varX > 0 && varY > 0)
                        {
                            rv = (sxy[i, j] - sx[i, j] * sy[i, j] / m) / Math.Sqrt(varX * varY);
                            rv = Math.Max(-1, Math.Min(1, rv));
                            double t = Math.Abs(rv) >= 1
                                ? double.PositiveInfinity
                                : Math.Abs(rv) * Math.Sqrt((m - 2) / Math.Max(1e-12, 1 - rv * rv));
                            pv = CsvStatistics.StudentTTwoSidedPValue(t, m - 2);
                        }
                    }
                    rm[i, j] = rm[j, i] = rv;
                    pm[i, j] = pm[j, i] = pv;
                    nm[i, j] = nm[j, i] = m;
                }
                rm[i, i] = 1; // 자기상관(관측 있으면)
                if (n[i, i] < 1) rm[i, i] = double.NaN;
            }
            return new CorrelationMatrixResult(rm, pm, nm);
        }

        // ---- 이동평균 (시계열) ----

        /// <summary>후행(trailing) 이동평균: 각 위치에서 직전 window개(부족하면 있는 만큼)의 평균.</summary>
        public static double[] TrailingMovingAverage(IReadOnlyList<double> values, int window)
        {
            int n = values.Count;
            var result = new double[n];
            if (window < 1) window = 1;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += values[i];
                if (i >= window) sum -= values[i - window];
                result[i] = sum / Math.Min(i + 1, window);
            }
            return result;
        }

        // ---- 밀도 격자 (대용량 산점도) ----

        public sealed record DensityGrid(int[,] Counts, double XMin, double XMax, double YMin, double YMax, int MaxCount, int Total);

        /// <summary>
        /// 산점 (x,y)를 cols×rows 격자로 비닝(전수 카운트 — 무작위 샘플 아님). 렌더러가 명도 셀로 그린다.
        /// 범위 지정(줌 재비닝)이 없으면 데이터 범위 사용. 유효 쌍이 없으면 null.
        /// </summary>
        public static DensityGrid? ComputeDensityGrid(
            IReadOnlyList<double> xs, IReadOnlyList<double> ys, int cols, int rows,
            double? xMin = null, double? xMax = null, double? yMin = null, double? yMax = null)
        {
            int n = Math.Min(xs.Count, ys.Count);
            double lox = double.PositiveInfinity, hix = double.NegativeInfinity;
            double loy = double.PositiveInfinity, hiy = double.NegativeInfinity;
            if (xMin is null || xMax is null || yMin is null || yMax is null)
            {
                for (int i = 0; i < n; i++)
                {
                    double x = xs[i], y = ys[i];
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    if (x < lox) lox = x;
                    if (x > hix) hix = x;
                    if (y < loy) loy = y;
                    if (y > hiy) hiy = y;
                }
                if (lox > hix) return null;
            }
            lox = xMin ?? lox; hix = xMax ?? hix;
            loy = yMin ?? loy; hiy = yMax ?? hiy;
            if (hix <= lox) hix = lox + 1;
            if (hiy <= loy) hiy = loy + 1;

            var counts = new int[rows, cols];
            int max = 0, total = 0;
            double sxScale = cols / (hix - lox), syScale = rows / (hiy - loy);
            for (int i = 0; i < n; i++)
            {
                double x = xs[i], y = ys[i];
                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                if (x < lox || x > hix || y < loy || y > hiy) continue; // 줌 밖
                int cx = (int)((x - lox) * sxScale);
                int cy = (int)((y - loy) * syScale);
                if (cx >= cols) cx = cols - 1;
                if (cy >= rows) cy = rows - 1;
                int c = ++counts[cy, cx];
                if (c > max) max = c;
                total++;
            }
            return total == 0 ? null : new DensityGrid(counts, lox, hix, loy, hiy, max, total);
        }

        // ---- 선분 클리핑 (렌더 안전) ----

        /// <summary>
        /// Liang-Barsky 선분 클리핑. GDI+ 좌표 한계(±2²³ 고정소수점) 때문에 딥 줌 시
        /// 회귀선·KDE 곡선의 픽셀 좌표가 폭주하면 OverflowException으로 크래시한다 —
        /// 세그먼트를 안전 사각형으로 정확히 잘라(직선 위 보간) 시각 왜곡 없이 방지.
        /// 반환 false = 세그먼트가 사각형과 교차하지 않음(그리지 않음).
        /// </summary>
        public static bool ClipSegment(
            double x1, double y1, double x2, double y2,
            double left, double top, double right, double bottom,
            out double cx1, out double cy1, out double cx2, out double cy2)
        {
            cx1 = x1; cy1 = y1; cx2 = x2; cy2 = y2;
            double dx = x2 - x1, dy = y2 - y1;
            double t0 = 0, t1 = 1;

            // 각 경계에 대해 파라미터 t 구간을 좁힌다: p·t ≤ q
            Span<double> p = stackalloc double[] { -dx, dx, -dy, dy };
            Span<double> q = stackalloc double[] { x1 - left, right - x1, y1 - top, bottom - y1 };
            for (int i = 0; i < 4; i++)
            {
                if (p[i] == 0)
                {
                    if (q[i] < 0) return false; // 경계와 평행 + 밖
                    continue;
                }
                double t = q[i] / p[i];
                if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
            cx1 = x1 + t0 * dx; cy1 = y1 + t0 * dy;
            cx2 = x1 + t1 * dx; cy2 = y1 + t1 * dy;
            return true;
        }

        // ---- 파레토 ----

        /// <summary>빈도 배열(내림차순 정렬 전제)의 누적 백분율.</summary>
        public static double[] ParetoCumulativePercent(IReadOnlyList<int> counts)
        {
            long total = 0;
            foreach (int c in counts) total += c;
            var result = new double[counts.Count];
            if (total == 0) return result;
            long acc = 0;
            for (int i = 0; i < counts.Count; i++)
            {
                acc += counts[i];
                result[i] = 100.0 * acc / total;
            }
            return result;
        }
    }
}
