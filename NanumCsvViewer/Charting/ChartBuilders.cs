using System.Globalization;
using System.Text;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Charting
{
    /// <summary>
    /// 7종 차트 빌더(이슈 #19): (뷰 행, 컬럼 선택, 옵션) → PlotModel + 통계 배지.
    /// 기존 통계 엔진(CsvStatistics·CsvAnalytics)을 재사용하고, 배지 상세는 분석 메뉴와 같은 텍스트 포맷.
    /// 축 제목 등 표시 문자열은 호출자(UI)가 현지화해 넘긴다. 배지 라벨은 수식 표기(언어 중립).
    /// </summary>
    public static class ChartBuilders
    {
        /// <summary>수치 파싱(통화·퍼센트 접사 인식 — 그리드 표시와 동일 기준).</summary>
        private static bool TryNumber(string raw, out double value)
            => NumericAffix.TryParseNumber(raw.Trim(), out value);

        private static List<double> NumericColumn(List<string[]> rows, int col)
        {
            var values = new List<double>(Math.Min(rows.Count, 1024));
            foreach (var row in rows)
                if (col < row.Length && TryNumber(row[col], out double v) && double.IsFinite(v))
                    values.Add(v);
            return values;
        }

        private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        private static string PVal(double p) => p < 0.0001 ? "p<0.0001" : $"p={p.ToString("0.0000", CultureInfo.InvariantCulture)}";

        // ---------------------------------------------------------------- 1. 히스토그램(+정규곡선+KDE)

        public sealed record HistogramOptions(int? Bins = null, bool ShowNormal = true, bool ShowKde = true);

        public static PlotModel? Histogram(List<string[]> rows, int col, string colName, HistogramOptions opt)
        {
            var values = NumericColumn(rows, col);
            if (values.Count == 0) return null;
            var sorted = values.ToArray();
            Array.Sort(sorted);

            int bins = opt.Bins ?? PlotMath.FreedmanDiaconisBins(sorted);
            var dist = CsvAnalytics.NumericDistributionOf(values, col, bins);
            var d = CsvStatistics.Describe(values)!;

            var series = new List<PlotSeries>();
            var barLeft = new double[dist.Bins.Count];
            var barRight = new double[dist.Bins.Count];
            var barHeight = new double[dist.Bins.Count];
            for (int i = 0; i < dist.Bins.Count; i++)
            {
                barLeft[i] = dist.Bins[i].LowerBound;
                barRight[i] = dist.Bins[i].UpperBound;
                barHeight[i] = dist.Bins[i].Count;
            }
            series.Add(new PlotSeries { Kind = PlotSeriesKind.Bars, Name = colName, BarLeft = barLeft, BarRight = barRight, BarHeight = barHeight, PaletteIndex = 0 });

            // 오버레이는 밀도 → 기대빈도(밀도×n×빈폭) 스케일로 겹친다.
            double binWidth = dist.Bins.Count > 0 ? dist.Bins[0].UpperBound - dist.Bins[0].LowerBound : 1;
            double overlayScale = values.Count * binWidth;
            double maxY = barHeight.Length > 0 ? barHeight.Max() : 1;

            if (opt.ShowNormal && d.StandardDeviation > 0)
            {
                var (xs, ys) = CurveOver(sorted[0], sorted[^1], 128,
                    x => PlotMath.NormalPdf(x, d.Mean, d.StandardDeviation) * overlayScale);
                series.Add(new PlotSeries { Kind = PlotSeriesKind.Line, Name = "Normal", Xs = xs, Ys = ys, PaletteIndex = 2, Dashed = true });
                maxY = Math.Max(maxY, ys.Max());
            }
            if (opt.ShowKde && sorted.Length >= 3 && sorted[^1] > sorted[0])
            {
                double h = PlotMath.SilvermanBandwidth(sorted);
                var kde = PlotMath.BinnedKdeCurve(sorted, h);
                var ys = new double[kde.Density.Length];
                for (int i = 0; i < ys.Length; i++) ys[i] = kde.Density[i] * overlayScale;
                series.Add(new PlotSeries { Kind = PlotSeriesKind.Line, Name = "KDE", Xs = kde.X, Ys = ys, PaletteIndex = 1 });
                maxY = Math.Max(maxY, ys.Max());
            }

            var badges = new List<StatBadge>
            {
                new($"n={values.Count:N0}", DescribeDetail(colName, d, rows.Count - values.Count)),
                new($"μ={Num(d.Mean)} σ={Num(d.StandardDeviation)}", DescribeDetail(colName, d, rows.Count - values.Count)),
            };
            // Shapiro-Wilk: Royston 근사 신뢰범위(n≤5000) 준수 — 초과 시 왜도/첨도 배지로 대체.
            if (values.Count <= 5000)
            {
                var sw = CsvStatistics.ShapiroWilk(values);
                if (sw is not null)
                    badges.Add(new StatBadge($"SW W={sw.W:0.000} {PVal(sw.PValue)}",
                        $"Shapiro-Wilk (n={sw.SampleSize:N0})\nW = {sw.W:0.0000}\np-value = {sw.PValue:0.0000}\n→ {sw.Interpretation}"));
            }
            else if (!double.IsNaN(d.Skewness))
            {
                badges.Add(new StatBadge($"skew={d.Skewness:0.00} kurt={d.ExcessKurtosis:0.00}",
                    $"n>5,000: Shapiro-Wilk 근사 신뢰범위 초과 → 왜도/첨도 표시\nskewness = {d.Skewness:0.0000}\nexcess kurtosis = {d.ExcessKurtosis:0.0000}"));
            }

            double xPad = (sorted[^1] - sorted[0]) * 0.02;
            if (xPad <= 0) xPad = 0.5;
            return new PlotModel
            {
                Title = colName,
                XAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = colName, Min = sorted[0] - xPad, Max = sorted[^1] + xPad },
                YAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = "count", Min = 0, Max = maxY * 1.05 },
                Series = series,
                Badges = badges,
                ReferenceLines = new[]
                {
                    new ReferenceLine(d.Mean, Vertical: true, $"mean {Num(d.Mean)}", 2),
                    new ReferenceLine(d.Median, Vertical: true, $"median {Num(d.Median)}", 3),
                },
                RenderNote = $"N={values.Count:N0}",
                AllowZoom = true,
            };
        }

        private static string DescribeDetail(string name, DescriptiveStatisticsResult d, int missing)
        {
            var sb = new StringBuilder();
            sb.AppendLine(name);
            sb.AppendLine($"N={d.Count:N0}  missing={missing:N0}");
            sb.AppendLine($"mean={Num(d.Mean)}  sd={Num(d.StandardDeviation)}  se={Num(d.StandardError)}");
            sb.AppendLine($"95% CI [{Num(d.ConfidenceIntervalLow)}, {Num(d.ConfidenceIntervalHigh)}]");
            sb.AppendLine($"min={Num(d.Min)}  Q1={Num(d.Q1)}  median={Num(d.Median)}  Q3={Num(d.Q3)}  max={Num(d.Max)}");
            if (!double.IsNaN(d.Skewness)) sb.AppendLine($"skew={d.Skewness:0.0000}  kurtosis={d.ExcessKurtosis:0.0000}");
            return sb.ToString();
        }

        private static (double[] Xs, double[] Ys) CurveOver(double lo, double hi, int points, Func<double, double> f)
        {
            if (hi <= lo) hi = lo + 1;
            var xs = new double[points];
            var ys = new double[points];
            for (int i = 0; i < points; i++)
            {
                xs[i] = lo + (hi - lo) * i / (points - 1);
                ys[i] = f(xs[i]);
            }
            return (xs, ys);
        }

        // ---------------------------------------------------------------- 2. 박스플롯(그룹)

        /// <summary>그룹 컬럼 없이(groupCol&lt;0) 단일 박스도 지원. 그룹은 표본수 상위 maxGroups개만(생략 수 배지 표기).</summary>
        public static PlotModel? BoxPlot(List<string[]> rows, int valueCol, string valueName,
            int groupCol, string groupName, int maxGroups = 50)
        {
            var groups = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (valueCol >= row.Length || !TryNumber(row[valueCol], out double v) || !double.IsFinite(v)) continue;
                string g = groupCol >= 0 && groupCol < row.Length ? row[groupCol].Trim() : valueName;
                if (!groups.TryGetValue(g, out var list)) { list = new List<double>(); groups[g] = list; }
                list.Add(v);
            }
            if (groups.Count == 0) return null;

            var ordered = groups.OrderByDescending(kv => kv.Value.Count)
                                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            int omitted = Math.Max(0, ordered.Count - maxGroups);
            if (omitted > 0) ordered = ordered.Take(maxGroups).ToList();

            var boxes = new List<PlotBox>();
            double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
            foreach (var (name, vals) in ordered.Select(kv => (kv.Key, kv.Value)))
            {
                var b = PlotMath.ComputeBoxStats(vals);
                if (b is null) continue;
                boxes.Add(new PlotBox(name.Length == 0 ? "(빈값)" : name, b.Count, b.Q1, b.Median, b.Q3,
                    b.WhiskerLow, b.WhiskerHigh, b.Outliers, b.Mean));
                double lo = b.Outliers.Length > 0 ? Math.Min(b.WhiskerLow, b.Outliers.Min()) : b.WhiskerLow;
                double hi = b.Outliers.Length > 0 ? Math.Max(b.WhiskerHigh, b.Outliers.Max()) : b.WhiskerHigh;
                yMin = Math.Min(yMin, lo);
                yMax = Math.Max(yMax, hi);
            }
            if (boxes.Count == 0) return null;
            if (yMax <= yMin) yMax = yMin + 1;

            // 검정 배지: 그룹≥3 → 일원배치 ANOVA, 그룹=2 → Welch t검정 (설계 합의)
            var badges = new List<StatBadge>();
            if (ordered.Count >= 3)
            {
                var obs = new List<(string, double)>();
                foreach (var (name, vals) in ordered.Select(kv => (kv.Key, kv.Value)))
                    foreach (double v in vals) obs.Add((name, v));
                var a = CsvStatistics.OneWayAnova(obs);
                if (a is not null)
                    badges.Add(new StatBadge($"ANOVA F={a.FStatistic:0.00} {PVal(a.PValue)} η²={a.EtaSquared:0.000}",
                        $"One-way ANOVA\nF({a.DfBetween}, {a.DfWithin}) = {a.FStatistic:0.0000}\np-value = {a.PValue:0.0000}\nη² = {a.EtaSquared:0.0000}\n→ {a.Interpretation}"));
            }
            else if (ordered.Count == 2)
            {
                var t = CsvStatistics.IndependentTTest(ordered[0].Key, ordered[0].Value, ordered[1].Key, ordered[1].Value);
                badges.Add(new StatBadge($"t={t.TStatistic:0.00} {PVal(t.PValue)} d={t.EffectSize:0.00}",
                    $"Welch t-test: {t.GroupA} vs {t.GroupB}\nt = {t.TStatistic:0.0000}  df = {t.DegreesOfFreedom:0.00}\np-value = {t.PValue:0.0000}\n95% CI [{Num(t.ConfidenceIntervalLow)}, {Num(t.ConfidenceIntervalHigh)}]\nCohen's d = {t.EffectSize:0.0000}\n→ {t.Interpretation}"));
            }
            if (omitted > 0)
                badges.Add(new StatBadge($"+{omitted:N0} groups omitted",
                    $"표본수 상위 {maxGroups}개 그룹만 표시. {omitted:N0}개 그룹 생략(검정도 표시 그룹 기준)."));

            double pad = (yMax - yMin) * 0.05;
            return new PlotModel
            {
                Title = groupCol >= 0 ? $"{valueName} by {groupName}" : valueName,
                XAxis = new PlotAxis { Kind = PlotAxisKind.Category, Title = groupCol >= 0 ? groupName : "", Categories = boxes.Select(b => b.Label).ToArray() },
                YAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = valueName, Min = yMin - pad, Max = yMax + pad },
                Series = new[] { new PlotSeries { Kind = PlotSeriesKind.Boxes, Name = valueName, Boxes = boxes, PaletteIndex = 0 } },
                Badges = badges,
                RenderNote = $"N={groups.Values.Sum(v => v.Count):N0}",
                AllowZoom = false,
            };
        }

        // ---------------------------------------------------------------- 3. 산점도(+회귀, 대용량 밀도)

        public sealed record ScatterOptions(bool ShowRegression = true, int DensityThreshold = 50_000);

        public static PlotModel? Scatter(List<string[]> rows, int xCol, string xName, int yCol, string yName, ScatterOptions opt)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var row in rows)
                if (xCol < row.Length && yCol < row.Length &&
                    TryNumber(row[xCol], out double x) && TryNumber(row[yCol], out double y) &&
                    double.IsFinite(x) && double.IsFinite(y))
                { xs.Add(x); ys.Add(y); }
            if (xs.Count < 2) return null;

            double xMin = xs.Min(), xMax = xs.Max(), yMin = ys.Min(), yMax = ys.Max();
            if (xMax <= xMin) xMax = xMin + 1;
            if (yMax <= yMin) yMax = yMin + 1;

            var series = new List<PlotSeries>();
            bool density = xs.Count > opt.DensityThreshold;
            series.Add(new PlotSeries
            {
                Kind = density ? PlotSeriesKind.Density : PlotSeriesKind.Points,
                Name = $"{xName} × {yName}",
                Xs = xs.ToArray(),
                Ys = ys.ToArray(),
                PaletteIndex = 0,
                DensityThreshold = opt.DensityThreshold,
            });

            var badges = new List<StatBadge>();
            // 통계는 항상 전수(렌더 모드와 무관) — 설계 원칙.
            var pairs = new List<(double, double)>(xs.Count);
            for (int i = 0; i < xs.Count; i++) pairs.Add((xs[i], ys[i]));
            var corr = CsvStatistics.Correlation(pairs, CorrelationMethod.Pearson);
            badges.Add(new StatBadge($"r={corr.Coefficient:0.000} {PVal(corr.PValue)}",
                $"Pearson correlation (n={corr.SampleSize:N0})\nr = {corr.Coefficient:0.0000}\np-value = {corr.PValue:0.0000}\n→ {corr.Interpretation}"));

            if (opt.ShowRegression && PlotMath.OlsFit(xs, ys) is { } fit)
            {
                series.Add(new PlotSeries
                {
                    Kind = PlotSeriesKind.Line,
                    Name = "OLS",
                    Xs = new[] { xMin, xMax },
                    Ys = new[] { fit.Intercept + fit.Slope * xMin, fit.Intercept + fit.Slope * xMax },
                    PaletteIndex = 2,
                });
                badges.Add(new StatBadge($"y={Num(fit.Slope)}x{(fit.Intercept >= 0 ? "+" : "")}{Num(fit.Intercept)} R²={fit.RSquared:0.000}",
                    $"OLS regression (n={fit.Count:N0}, 전수 계산)\nslope = {Num(fit.Slope)}\nintercept = {Num(fit.Intercept)}\nR² = {fit.RSquared:0.0000}"));
            }

            double px = (xMax - xMin) * 0.03, py = (yMax - yMin) * 0.03;
            return new PlotModel
            {
                Title = $"{yName} vs {xName}",
                XAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = xName, Min = xMin - px, Max = xMax + px },
                YAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = yName, Min = yMin - py, Max = yMax + py },
                Series = series,
                Badges = badges,
                RenderNote = density
                    ? $"통계 N={xs.Count:N0}(전수) · 렌더=밀도 격자"
                    : $"N={xs.Count:N0}",
                AllowZoom = true,
            };
        }

        // ---------------------------------------------------------------- 4. 상관 히트맵

        /// <summary>수치 컬럼들의 Pearson 행렬. 유효 컬럼 2개 미만이면 null. 셀 클릭 드릴다운은 UI가 축 카테고리로 역참조.</summary>
        public static PlotModel? CorrelationHeatmap(List<string[]> rows, IReadOnlyList<int> cols, IReadOnlyList<string> names)
        {
            if (cols.Count < 2) return null;
            var columns = new List<double[]>(cols.Count);
            foreach (int c in cols)
            {
                var arr = new double[rows.Count];
                for (int r = 0; r < rows.Count; r++)
                    arr[r] = c < rows[r].Length && TryNumber(rows[r][c], out double v) && double.IsFinite(v) ? v : double.NaN;
                columns.Add(arr);
            }
            var m = PlotMath.PearsonMatrix(columns);

            return new PlotModel
            {
                Title = "Correlation (Pearson)",
                XAxis = new PlotAxis { Kind = PlotAxisKind.Category, Categories = names.ToArray() },
                YAxis = new PlotAxis { Kind = PlotAxisKind.Category, Categories = names.ToArray() },
                Series = new[] { new PlotSeries { Kind = PlotSeriesKind.HeatCells, Name = "r", Cells = m.R, CellsP = m.P } },
                Badges = new[]
                {
                    new StatBadge($"{cols.Count}×{cols.Count}",
                        "Pearson 상관 행렬(쌍별 완전관측·단일 패스).\n비유의(p≥0.05) 셀은 흐리게 표시.\n셀 클릭 → 해당 두 컬럼 산점도."),
                },
                RenderNote = $"N={rows.Count:N0}",
                AllowZoom = false,
            };
        }

        // ---------------------------------------------------------------- 5. Q-Q 플롯

        public static PlotModel? QqPlot(List<string[]> rows, int col, string colName)
        {
            var values = NumericColumn(rows, col);
            if (values.Count < 3) return null;
            var sorted = values.ToArray();
            Array.Sort(sorted);
            if (sorted[^1] <= sorted[0]) return null; // 상수 컬럼

            var qq = PlotMath.QqNormalPoints(sorted);
            var d = CsvStatistics.Describe(values)!;

            // 기준선: y = μ + σ·z (완전 정규면 점들이 이 선 위)
            double zLo = qq.Theoretical[0], zHi = qq.Theoretical[^1];
            var series = new List<PlotSeries>
            {
                new() { Kind = PlotSeriesKind.Points, Name = colName, Xs = qq.Theoretical, Ys = qq.Sample, PaletteIndex = 0 },
                new()
                {
                    Kind = PlotSeriesKind.Line, Name = "Normal ref",
                    Xs = new[] { zLo, zHi },
                    Ys = new[] { d.Mean + d.StandardDeviation * zLo, d.Mean + d.StandardDeviation * zHi },
                    PaletteIndex = 2, Dashed = true,
                },
            };

            var badges = new List<StatBadge>();
            if (values.Count <= 5000)
            {
                var sw = CsvStatistics.ShapiroWilk(values);
                if (sw is not null)
                    badges.Add(new StatBadge($"SW W={sw.W:0.000} {PVal(sw.PValue)}",
                        $"Shapiro-Wilk (n={sw.SampleSize:N0})\nW = {sw.W:0.0000}\np-value = {sw.PValue:0.0000}\n→ {sw.Interpretation}"));
            }
            bool thinned = values.Count > qq.Sample.Length;
            if (thinned)
                badges.Add(new StatBadge($"{qq.Sample.Length:N0} quantiles",
                    $"n={values.Count:N0} > 10,000 → 결정적 분위 시닝 {qq.Sample.Length:N0}점(무작위 아님·꼬리 보존)."));

            double px = (zHi - zLo) * 0.05;
            double sLo = Math.Min(qq.Sample[0], d.Mean + d.StandardDeviation * zLo);
            double sHi = Math.Max(qq.Sample[^1], d.Mean + d.StandardDeviation * zHi);
            double py = (sHi - sLo) * 0.05;
            if (py <= 0) py = 1;
            return new PlotModel
            {
                Title = $"Q-Q: {colName}",
                XAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = "theoretical quantiles (z)", Min = zLo - px, Max = zHi + px },
                YAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = colName, Min = sLo - py, Max = sHi + py },
                Series = series,
                Badges = badges,
                RenderNote = thinned ? $"통계 N={values.Count:N0}(전수) · 렌더={qq.Sample.Length:N0}분위" : $"N={values.Count:N0}",
                AllowZoom = true,
            };
        }

        // ---------------------------------------------------------------- 6. 시계열(+이동평균)

        public sealed record TimeSeriesOptions(DateBinPeriod Period = DateBinPeriod.Month, int MovingAverageWindow = 0, int? ValueCol = null);

        public static PlotModel? TimeSeries(List<string[]> rows, int dateCol, string dateName, string? valueName, TimeSeriesOptions opt)
        {
            var hist = CsvAnalytics.DateHistogramOf(rows, dateCol, opt.ValueCol, opt.Period);
            if (hist.Bins.Count == 0) return null;

            bool useSum = opt.ValueCol is not null;
            var labels = new string[hist.Bins.Count];
            var xs = new double[hist.Bins.Count];
            var ys = new double[hist.Bins.Count];
            for (int i = 0; i < hist.Bins.Count; i++)
            {
                labels[i] = hist.Bins[i].Label;
                xs[i] = i;
                ys[i] = useSum ? hist.Bins[i].Sum ?? 0 : hist.Bins[i].Count;
            }
            string yTitle = useSum ? $"sum({valueName})" : "count";

            var series = new List<PlotSeries>
            {
                new() { Kind = PlotSeriesKind.Line, Name = yTitle, Xs = xs, Ys = ys, PaletteIndex = 0 },
            };
            var badges = new List<StatBadge> { new($"{hist.Bins.Count:N0} bins · {opt.Period}", $"{dateName} · {opt.Period} 비닝") };
            if (opt.MovingAverageWindow >= 2 && ys.Length >= 2)
            {
                var ma = PlotMath.TrailingMovingAverage(ys, opt.MovingAverageWindow);
                series.Add(new PlotSeries { Kind = PlotSeriesKind.Line, Name = $"MA({opt.MovingAverageWindow})", Xs = xs, Ys = ma, PaletteIndex = 2, Dashed = true });
            }

            double yMin = Math.Min(0, ys.Min()), yMax = ys.Max();
            if (yMax <= yMin) yMax = yMin + 1;
            return new PlotModel
            {
                Title = $"{yTitle} by {dateName}",
                XAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = dateName, Min = -0.5, Max = hist.Bins.Count - 0.5, TickLabels = labels },
                YAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = yTitle, Min = yMin, Max = yMax * 1.05 },
                Series = series,
                Badges = badges,
                RenderNote = $"N={rows.Count:N0}",
                AllowZoom = true,
            };
        }

        // ---------------------------------------------------------------- 7. 파레토

        public static PlotModel? Pareto(List<string[]> rows, int col, string colName, int topN = 50)
        {
            var values = new List<string>(rows.Count);
            foreach (var row in rows) values.Add(col < row.Length ? row[col] : string.Empty);
            var freq = CsvStatistics.FrequencyTable(values);
            if (freq.TotalCount == 0 || freq.UniqueCount == 0) return null;

            var top = freq.Entries.Take(topN).ToList();
            int otherCount = freq.Entries.Skip(topN).Sum(e => e.Count);
            var labels = new List<string>(top.Count + 1);
            var counts = new List<int>(top.Count + 1);
            foreach (var e in top)
            {
                labels.Add(e.Value.Length == 0 ? "(빈값)" : e.Value);
                counts.Add(e.Count);
            }
            if (otherCount > 0) { labels.Add("(기타)"); counts.Add(otherCount); }

            var cum = PlotMath.ParetoCumulativePercent(counts);
            var xs = new double[counts.Count];
            for (int i = 0; i < xs.Length; i++) xs[i] = i;

            var badges = new List<StatBadge>
            {
                new($"unique={freq.UniqueCount:N0}", $"{colName}\n전체 {freq.TotalCount:N0} · 고유값 {freq.UniqueCount:N0}"),
            };
            if (otherCount > 0)
                badges.Add(new StatBadge($"top {top.Count}+기타",
                    $"빈도 상위 {top.Count}개 + 나머지 {freq.UniqueCount - top.Count:N0}개 값을 (기타)로 합침."));

            return new PlotModel
            {
                Title = $"Pareto: {colName}",
                XAxis = new PlotAxis { Kind = PlotAxisKind.Category, Title = colName, Categories = labels },
                YAxis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = "count", Min = 0, Max = counts.Max() * 1.05 },
                Y2Axis = new PlotAxis { Kind = PlotAxisKind.Numeric, Title = "cum %", Min = 0, Max = 105, IsPercent = true },
                Series = new[]
                {
                    new PlotSeries { Kind = PlotSeriesKind.CategoryBars, Name = "count", Ys = counts.Select(c => (double)c).ToArray(), PaletteIndex = 0 },
                    new PlotSeries { Kind = PlotSeriesKind.Line, Name = "cum %", Xs = xs, Ys = cum, PaletteIndex = 2, UseSecondaryAxis = true },
                },
                Badges = badges,
                RenderNote = $"N={freq.TotalCount:N0}",
                AllowZoom = false,
            };
        }
    }
}
