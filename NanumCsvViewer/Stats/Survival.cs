using System.Globalization;
using NanumCsvViewer.Charting;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    // 생존분석(Phase 2). Kaplan–Meier·로그순위는 statsmodels SurvfuncRight / survdiff,
    // Cox 부분가능도는 PHReg(ties='efron'|'breslow')의 Breslow·Efron 식과 같게 푼다.
    // 엔진은 WinForms를 쓰지 않는다. 곡선 그림은 PlotModel(순수 데이터)만 만든다.

    public enum CoxTies { Efron, Breslow }

    /// <summary>사건 지시 해석. 비어 있는 사건 수준은 1=사건, 0=중도절단만 인정한다.</summary>
    public static class SurvivalCoding
    {
        /// <summary>
        /// 결측이면 false. eventLevel이 있으면 그 값(트림, 수치로도 같으면 일치)이 사건이고 그 외 비결측은 중도절단.
        /// 비어 있으면 수치 1=사건, 0=중도절단, 그 외는 해석 불가로 false.
        /// </summary>
        public static bool TryMapEvent(string? raw, string? eventLevel, out bool occurred)
        {
            occurred = false;
            if (StatValue.IsMissing(raw)) return false;
            string cell = raw!.Trim();
            if (!string.IsNullOrEmpty(eventLevel))
            {
                string level = eventLevel.Trim();
                if (string.Equals(cell, level, StringComparison.Ordinal)
                    || (StatValue.TryNumber(cell, out double a) && StatValue.TryNumber(level, out double b) && a == b))
                    occurred = true;
                return true;
            }
            if (!StatValue.TryNumber(cell, out double v)) return false;
            if (v == 1) { occurred = true; return true; }
            if (v == 0) return true;
            return false;
        }
    }

    public sealed record SurvivalTableRow(
        double Time,
        long AtRisk,
        long Events,
        long Censored,
        double Survival,
        double StdError,
        double CiLow,
        double CiHigh);

    public sealed record SurvivalCurve(
        string Group,
        long N,
        long Events,
        long Censored,
        IReadOnlyList<SurvivalTableRow> Rows,
        double Median,
        double MedianCiLow,
        double MedianCiHigh,
        double RestrictedMean,
        double RestrictedMeanTau,
        IReadOnlyList<double> CensorTimes,
        bool CensorMarksSampled,
        long CensorMarkPopulation);

    public enum LogRankWeight { LogRank, GehanBreslow }

    public enum LogRankIssue { None, TooFewGroups, NoEvents, Singular }

    public sealed record GroupEventCount(string Group, long Observed, double Expected);

    public sealed record LogRankTest(
        LogRankWeight Weight,
        double ChiSquare,
        int DegreesOfFreedom,
        double P,
        string? ReferenceGroup,
        LogRankIssue Issue,
        IReadOnlyList<GroupEventCount> Counts);

    public sealed record KaplanMeierResult(
        IReadOnlyList<SurvivalCurve> Curves,
        LogRankTest LogRank,
        LogRankTest GehanBreslow,
        long RowsRead,
        long RowsUsed,
        long RowsDropped,
        string? EventLevel);

    public static class KaplanMeierAnalysis
    {
        public const int MaxGroups = 50;
        /// <summary>그림에 넣는 중도절단 표식 상한. 넘으면 시드 고정 저수지 표본이고 결과에 표시한다.</summary>
        public const int PlotCensorCap = 4000;
        public const int PlotStepCap = 4000;
        public const long MemoryBudgetBytes = 1024L * 1024 * 1024;

        /// <summary>
        /// Kaplan–Meier(사건 시각만, statsmodels SurvfuncRight compress=True)와 로그순위·Gehan–Breslow.
        /// group이 null이면 단일 곡선. 그룹 라벨은 서수 순(np.unique와 같음)이고 첫 그룹이 survdiff 기준 그룹이다.
        /// </summary>
        public static KaplanMeierResult Fit(
            double[] time, int[] status, string[]? group = null, CancellationToken cancellation = default)
        {
            int n = time.Length;
            if (status.Length != n || (group is not null && group.Length != n))
                throw new ArgumentException("time, status, and group must have the same length.");
            if (n == 0) throw new DesignMatrixException("No complete rows: every row has a missing or non-numeric value in the model columns.");
            for (int i = 0; i < n; i++)
            {
                if (!double.IsFinite(time[i]) || time[i] < 0)
                    throw new ArgumentException("Survival times must be finite and >= 0.", nameof(time));
                if (status[i] is not (0 or 1))
                    throw new ArgumentException("Status must be 1 (event) or 0 (censored).", nameof(status));
            }

            string[] labels;
            int[] gcode;
            if (group is null)
            {
                labels = new[] { "(all)" };
                gcode = new int[n];
            }
            else
            {
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                var names = new List<string>();
                gcode = new int[n];
                for (int i = 0; i < n; i++)
                {
                    string key = group[i] ?? "";
                    if (!map.TryGetValue(key, out int code))
                    {
                        if (names.Count >= MaxGroups)
                            throw new DesignMatrixException(
                                $"More than {MaxGroups} groups. Aggregate the group column or filter the view — a partial log-rank is not produced.");
                        code = names.Count;
                        map[key] = code;
                        names.Add(key);
                    }
                    gcode[i] = code;
                }
                var orderLabels = Enumerable.Range(0, names.Count).OrderBy(i => names[i], StringComparer.Ordinal).ToArray();
                var remap = new int[names.Count];
                labels = new string[names.Count];
                for (int i = 0; i < orderLabels.Length; i++)
                {
                    remap[orderLabels[i]] = i;
                    labels[i] = names[orderLabels[i]];
                }
                if (orderLabels.Length > 1 && !orderLabels.SequenceEqual(Enumerable.Range(0, names.Count)))
                    for (int i = 0; i < n; i++) gcode[i] = remap[gcode[i]];
            }

            return FitCoded(time, status, gcode, labels, n, 0, null, cancellation);
        }

        public static KaplanMeierResult FromRows(
            IReadOnlyList<string[]> rows,
            int timeColumn,
            int eventColumn,
            string? eventLevel,
            int? groupColumn,
            CancellationToken cancellation = default)
        {
            if ((long)rows.Count * 24 > MemoryBudgetBytes) throw new AnalysisMemoryLimitException();
            var time = new List<double>();
            var status = new List<int>();
            var groups = groupColumn is int ? new List<string>() : null;
            long dropped = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                if ((r & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                var row = rows[r];
                if (timeColumn >= row.Length || eventColumn >= row.Length
                    || (groupColumn is int gc0 && gc0 >= row.Length)
                    || !StatValue.TryNumber(row[timeColumn], out double t) || t < 0
                    || !SurvivalCoding.TryMapEvent(row[eventColumn], eventLevel, out bool ev)
                    || (groupColumn is int gc && StatValue.IsMissing(row[gc])))
                {
                    dropped++;
                    continue;
                }
                time.Add(t);
                status.Add(ev ? 1 : 0);
                if (groupColumn is int gcol) groups!.Add(row[gcol].Trim());
            }
            if (time.Count == 0)
                throw new DesignMatrixException(
                    "No complete rows. Time must be numeric and >= 0. Leave the event level blank only for a 0/1 event column; otherwise enter the event level.");
            var fit = Fit(time.ToArray(), status.ToArray(), groups?.ToArray(), cancellation);
            return fit with { RowsRead = rows.Count, RowsDropped = dropped, EventLevel = string.IsNullOrEmpty(eventLevel) ? null : eventLevel.Trim() };
        }

        private static KaplanMeierResult FitCoded(
            double[] time, int[] status, int[] gcode, string[] labels,
            long rowsRead, long rowsDropped, string? eventLevel, CancellationToken cancellation)
        {
            int n = time.Length;
            int g = labels.Length;
            var counts = new int[g];
            for (int r = 0; r < n; r++) counts[gcode[r]]++;

            var order = new int[n];
            for (int r = 0; r < n; r++) order[r] = r;
            Array.Sort(order, (a, b) =>
            {
                int c = time[a].CompareTo(time[b]);
                return c != 0 ? c : gcode[a].CompareTo(gcode[b]);
            });

            var atRisk = (int[])counts.Clone();
            int totalRisk = n;
            var rows = new List<SurvivalTableRow>[g];
            var censorTimes = new List<double>[g];
            var censorSeen = new long[g];
            var rng = new ulong[g];
            var logS = new double[g];
            var greenwood = new double[g];
            var tau = new double[g];
            var events = new long[g];
            var censored = new long[g];
            var obs = new long[g];
            var expected = new double[g];
            int df = g - 1;
            var oe = new double[Math.Max(df, 0)];
            var vari = new double[Math.Max(df, 0), Math.Max(df, 0)];
            var oeW = new double[Math.Max(df, 0)];
            var variW = new double[Math.Max(df, 0), Math.Max(df, 0)];
            long eventsTotalAll = 0;
            for (int k = 0; k < g; k++)
            {
                rows[k] = new List<SurvivalTableRow>();
                censorTimes[k] = new List<double>();
                rng[k] = 0x9E3779B97F4A7C15UL ^ (ulong)(k + 1);
            }

            int i = 0;
            while (i < n)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                double t = time[order[i]];
                var ev = new int[g];
                var leave = new int[g];
                while (i < n && time[order[i]] == t)
                {
                    int k = gcode[order[i]];
                    leave[k]++;
                    if (time[order[i]] > tau[k]) tau[k] = time[order[i]];
                    if (status[order[i]] == 1) ev[k]++;
                    else RememberCensor(censorTimes[k], ref censorSeen[k], ref rng[k], t);
                    i++;
                }
                int evTot = 0, leaveTot = 0;
                for (int k = 0; k < g; k++) { evTot += ev[k]; leaveTot += leave[k]; }
                if (evTot > 0 && totalRisk > 1 && df > 0)
                {
                    // evTot·(n−d)는 각각 int에 들어가도 곱은 2^31을 넘는다(10만 명·5만 동점 사건).
                    double scalar = (double)evTot * (totalRisk - evTot) / (totalRisk - 1);
                    double w = totalRisk; // Gehan–Breslow: 위험집합 크기
                    for (int a = 1; a < g; a++)
                    {
                        double ra = atRisk[a] / (double)totalRisk;
                        double diff = ev[a] - ra * evTot;
                        oe[a - 1] += diff;
                        oeW[a - 1] += w * diff;
                        for (int b = 1; b < g; b++)
                        {
                            double rb = atRisk[b] / (double)totalRisk;
                            double cell = scalar * rb * ((a == b ? 1.0 : 0.0) - ra);
                            vari[a - 1, b - 1] += cell;
                            variW[a - 1, b - 1] += w * w * cell;
                        }
                    }
                }
                for (int k = 0; k < g; k++)
                {
                    // τ는 그 그룹이 실제로 관측된 최대 시각만. 다른 그룹의 시각으로 늘리지 않는다.
                    obs[k] += ev[k];
                    events[k] += ev[k];
                    censored[k] += leave[k] - ev[k];
                    if (evTot > 0 && totalRisk > 0)
                        expected[k] += atRisk[k] / (double)totalRisk * evTot;
                    if (ev[k] > 0 && atRisk[k] > 0)
                    {
                        int d = ev[k];
                        int nr = atRisk[k];
                        double factor = 1.0 - d / (double)nr;
                        bool floored = factor < 1e-16;
                        logS[k] += Math.Log(floored ? 1e-16 : factor);
                        double s = floored ? 0 : Math.Exp(logS[k]);
                        double denom = nr * (double)(nr - d);
                        if (denom < 1e-12) denom = 1e-12;
                        double term = d / denom;
                        if (nr == d || nr == 0) term = double.NaN;
                        greenwood[k] += term;
                        double se = Math.Sqrt(greenwood[k]);
                        bool locs = double.IsFinite(se) || s != 0;
                        se = locs ? se * s : double.NaN;
                        LogLogCi(s, se, out double lo, out double hi);
                        rows[k].Add(new SurvivalTableRow(t, nr, d, leave[k] - d, s, se, lo, hi));
                    }
                    atRisk[k] -= leave[k];
                }
                eventsTotalAll += evTot;
                totalRisk -= leaveTot;
            }

            var curves = new SurvivalCurve[g];
            for (int k = 0; k < g; k++)
            {
                var table = rows[k];
                double median = double.NaN, mLo = double.NaN, mHi = double.NaN;
                for (int r = 0; r < table.Count; r++)
                    if (table[r].Survival < 0.5) { median = table[r].Time; break; }
                if (table.Count > 0) QuantileCi(table, 0.5, out mLo, out mHi);
                double rmst = RestrictedMean(table, tau[k]);
                curves[k] = new SurvivalCurve(
                    labels[k], counts[k], events[k], censored[k], table,
                    median, mLo, mHi, rmst, tau[k],
                    censorTimes[k], censorSeen[k] > censorTimes[k].Count, censorSeen[k]);
            }

            var countRows = new GroupEventCount[g];
            for (int k = 0; k < g; k++) countRows[k] = new GroupEventCount(labels[k], obs[k], expected[k]);
            var logRank = FinishTest(LogRankWeight.LogRank, oe, vari, df, labels, eventsTotalAll, countRows);
            var breslow = FinishTest(LogRankWeight.GehanBreslow, oeW, variW, df, labels, eventsTotalAll, countRows);
            return new KaplanMeierResult(curves, logRank, breslow, rowsRead == 0 ? n : rowsRead, n, rowsDropped, eventLevel);
        }

        private static void RememberCensor(List<double> kept, ref long seen, ref ulong state, double t)
        {
            seen++;
            if (kept.Count < PlotCensorCap) { kept.Add(t); return; }
            // 시드 고정 xorshift — 그림용. 추정에는 쓰이지 않는다.
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            if (state % (ulong)seen < (ulong)PlotCensorCap)
            {
                state ^= state << 13; state ^= state >> 7; state ^= state << 17;
                kept[(int)(state % (ulong)PlotCensorCap)] = t;
            }
        }

        private static LogRankTest FinishTest(
            LogRankWeight weight, double[] oe, double[,] vari, int df, string[] labels, long events, GroupEventCount[] counts)
        {
            if (df < 1) return new LogRankTest(weight, double.NaN, 0, double.NaN, null, LogRankIssue.TooFewGroups, counts);
            if (events == 0) return new LogRankTest(weight, double.NaN, df, double.NaN, labels[0], LogRankIssue.NoEvents, counts);
            if (!TryQuadratic(vari, oe, out double chi))
                return new LogRankTest(weight, double.NaN, df, double.NaN, labels[0], LogRankIssue.Singular, counts);
            return new LogRankTest(weight, chi, df, Dist.ChiSquareUpper(chi, df), labels[0], LogRankIssue.None, counts);
        }

        private static bool TryQuadratic(double[,] vari, double[] oe, out double chi)
        {
            chi = double.NaN;
            int k = oe.Length;
            if (k == 0) return false;
            var a = (double[,])vari.Clone();
            try
            {
                if (!alglib.spdmatrixcholesky(a, k, false)) return false;
                alglib.spdmatrixcholeskysolve(a, k, false, oe, out double[] x, out var rep);
                if (rep.r1 < 0 || x.Length != k) return false;
                double s = 0;
                for (int i = 0; i < k; i++) s += oe[i] * x[i];
                if (!double.IsFinite(s) || s < 0) return false;
                chi = s;
                return true;
            }
            catch (alglib.alglibexception)
            {
                return false;
            }
        }

        /// <summary>점별 95% CI. log-log(cloglog) — SurvfuncRight.quantile_ci 기본 변환과 같다. summary() 자체에는 구간이 없다.</summary>
        public static void LogLogCi(double s, double se, out double low, out double high)
        {
            low = high = double.NaN;
            if (!(s > 0) || !(s < 1) || !double.IsFinite(se) || !(se > 0)) return;
            double z = Dist.NormalQuantile(0.975);
            double seG = se / (s * Math.Abs(Math.Log(s)));
            double center = Math.Log(-Math.Log(s));
            if (!double.IsFinite(seG) || !double.IsFinite(center)) return;
            low = Math.Exp(-Math.Exp(center + z * seG));
            high = Math.Exp(-Math.Exp(center - z * seG));
        }

        /// <summary>분위수 CI. 한계는 관측된 사건 시각(statsmodels quantile_ci, method='cloglog'). 상한이 없으면 +∞.</summary>
        public static void QuantileCi(IReadOnlyList<SurvivalTableRow> rows, double p, out double low, out double high)
        {
            low = high = double.NaN;
            if (rows.Count == 0 || !(p > 0) || !(p < 1)) return;
            double tr = Dist.NormalQuantile(0.975);
            double gTarget = Math.Log(-Math.Log(1 - p));
            int first = -1, last = -1;
            for (int i = 0; i < rows.Count; i++)
            {
                double s = rows[i].Survival;
                double se = rows[i].StdError;
                if (!(s > 0) || !(s < 1) || !double.IsFinite(se) || !(se > 0)) continue;
                double gp = -1.0 / (s * Math.Log(s));
                double r = (Math.Log(-Math.Log(s)) - gTarget) / (gp * se);
                if (double.IsFinite(r) && Math.Abs(r) <= tr)
                {
                    if (first < 0) first = i;
                    last = i;
                }
            }
            if (first < 0) return;
            low = rows[first].Time;
            high = last == rows.Count - 1 ? double.PositiveInfinity : rows[last + 1].Time;
        }

        /// <summary>제한 평균 생존시간. τ는 그 그룹의 최대 관측 시각(중도절단 포함). 표준오차는 계산하지 않는다.</summary>
        public static double RestrictedMean(IReadOnlyList<SurvivalTableRow> rows, double tau)
        {
            if (!double.IsFinite(tau) || tau < 0) return double.NaN;
            double rmst = 0, prevT = 0, prevS = 1;
            foreach (var row in rows)
            {
                if (row.Time > tau) break;
                rmst += prevS * (row.Time - prevT);
                prevT = row.Time;
                prevS = row.Survival;
            }
            if (tau > prevT) rmst += prevS * (tau - prevT);
            return rmst;
        }
    }

    public sealed record CoxFit(
        CoxTies Ties,
        string[] Names,
        double[] Coefficients,
        double[] StdErrors,
        double[] Z,
        double[] PValues,
        double[] HazardRatio,
        double[] HrCiLow,
        double[] HrCiHigh,
        double LogLikelihood,
        double NullLogLikelihood,
        double LikelihoodRatio,
        double LikelihoodRatioP,
        double Score,
        double ScoreP,
        double Wald,
        double WaldP,
        int DfModel,
        int Iterations,
        bool Converged,
        bool MonotoneLikelihood,
        double Concordance,
        long ComparablePairs,
        long ConcordantPairs,
        long DiscordantPairs,
        long TiedRiskPairs,
        long Events,
        long RowsRead,
        long RowsUsed,
        long RowsDropped,
        string? EventLevel);

    public static class CoxRegression
    {
        public const int MaxColumns = 64;
        public const int MaxIterations = 100;
        /// <summary>statsmodels Newton tol. 이보다 작은 완전 뉴턴 스텝이면 수렴.</summary>
        public const double Tolerance = 1e-8;
        /// <summary>Newton 스텝의 정보행렬 대각에 더하는 능선. statsmodels ridge_factor 기본값.</summary>
        public const double Ridge = 1e-10;
        const double SeparationLimit = 20;

        public static CoxFit Fit(
            double[] time, int[] status, double[,] x, IReadOnlyList<string>? names = null,
            CoxTies ties = CoxTies.Efron, CancellationToken cancellation = default)
        {
            int n = time.Length;
            int p = x.GetLength(1);
            if (x.GetLength(0) != n || status.Length != n)
                throw new ArgumentException("time, status, and x must have the same number of rows.");
            if (p < 1) throw new DesignMatrixException("Cox models have no intercept. Specify at least one predictor.");
            if (p > MaxColumns)
                throw new DesignMatrixException(
                    $"Cox regression accepts at most {MaxColumns} predictors (each Newton step is O(n p²)). Reduce the formula.");
            var cols = Enumerable.Range(0, p).ToArray();
            var nameArr = new string[p];
            for (int j = 0; j < p; j++)
                nameArr[j] = names is not null && j < names.Count && !string.IsNullOrEmpty(names[j]) ? names[j] : "x" + j.ToString(CultureInfo.InvariantCulture);
            return FitCore(time, status, x, cols, nameArr, ties, n, n, 0, null, cancellation);
        }

        public static CoxFit FromFormula(
            IReadOnlyList<string[]> rows,
            IReadOnlyList<string> headers,
            ModelFormula formula,
            int eventColumn,
            string? eventLevel,
            Func<int, VariableKind> kindOf,
            CoxTies ties = CoxTies.Efron,
            CancellationToken cancellation = default)
        {
            if (formula.PredictorVariables.Count == 0)
                throw new DesignMatrixException("Cox models have no intercept. Specify at least one predictor.");
            var design = DesignMatrixBuilder.Build(rows, headers, formula, kindOf,
                new DesignMatrixOptions { MaxColumns = MaxColumns + 1 }, cancellation);
            if (design.HasImplicitConstant)
                throw new DesignMatrixException(
                    "The formula expands a categorical main effect to all levels and has no intercept, so the columns contain a constant. Cox has no intercept — keep the default intercept in the formula (it is dropped) or remove a level.");

            int start = design.HasIntercept ? 1 : 0;
            int p = design.ColumnCount - start;
            if (p < 1) throw new DesignMatrixException("Cox models have no intercept. Specify at least one predictor.");
            if (p > MaxColumns)
                throw new DesignMatrixException(
                    $"Cox regression accepts at most {MaxColumns} predictors (each Newton step is O(n p²)). Reduce the formula.");

            int n0 = design.RowCount;
            var keep = new List<int>(n0);
            var time = new List<double>(n0);
            var status = new List<int>(n0);
            long extra = 0;
            for (int i = 0; i < n0; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int view = design.ViewRows[i];
                var row = view >= 0 && view < rows.Count ? rows[view] : Array.Empty<string>();
                if (design.Y[i] < 0 || eventColumn >= row.Length
                    || !SurvivalCoding.TryMapEvent(row[eventColumn], eventLevel, out bool ev))
                {
                    extra++;
                    continue;
                }
                keep.Add(i);
                time.Add(design.Y[i]);
                status.Add(ev ? 1 : 0);
            }
            if (keep.Count == 0)
                throw new DesignMatrixException(
                    "No complete rows. Time must be numeric and >= 0. Leave the event level blank only for a 0/1 event column; otherwise enter the event level.");

            long bytes = (long)keep.Count * p * 8 + (long)keep.Count * 16;
            if (bytes > 1024L * 1024 * 1024) throw new AnalysisMemoryLimitException();

            // 절편을 빼고 사건·음수 시간으로 빠진 행만 남긴 압축 행렬. 예산 초과 시 부분 결과 없이 중단.
            var x = new double[keep.Count, p];
            for (int i = 0; i < keep.Count; i++)
                for (int j = 0; j < p; j++)
                    x[i, j] = design.X[keep[i], start + j];
            var nameList = new string[p];
            for (int j = 0; j < p; j++) nameList[j] = design.ColumnNames[start + j];
            var fit = FitCore(time.ToArray(), status.ToArray(), x, Enumerable.Range(0, p).ToArray(), nameList, ties,
                design.RowsRead, keep.Count, design.RowsDropped + extra,
                string.IsNullOrEmpty(eventLevel) ? null : eventLevel.Trim(), cancellation);
            return fit;
        }

        private static CoxFit FitCore(
            double[] time, int[] status, double[,] x, int[] cols, string[] names, CoxTies ties,
            long rowsRead, long rowsUsed, long rowsDropped, string? eventLevel, CancellationToken cancellation)
        {
            int n = time.Length;
            int p = cols.Length;
            long events = 0;
            for (int i = 0; i < n; i++)
            {
                if (!double.IsFinite(time[i]) || time[i] < 0)
                    throw new ArgumentException("Survival times must be finite and >= 0.", nameof(time));
                if (status[i] is not (0 or 1))
                    throw new ArgumentException("Status must be 1 (event) or 0 (censored).", nameof(status));
                if (status[i] == 1) events++;
            }
            if (events == 0)
                throw new DesignMatrixException("No events in the estimation sample. Check the event column and event level.");

            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => time[a].CompareTo(time[b]));

            var beta = new double[p];
            var state = new CoxWork(n, p);
            cancellation.ThrowIfCancellationRequested();
            Evaluate(time, status, x, cols, order, beta, ties, state, needDerivatives: true, cancellation);
            if (!InvertInformation(state.Info, state.Grad, ridge: false, out _, out _))
                throw new DesignMatrixException(
                    "Predictors are collinear or constant in the risk sets. Cox has no intercept — drop a redundant column or a category level.");
            double scoreStat = Quadratic(state.Info, state.Grad);
            double scoreP = Dist.ChiSquareUpper(scoreStat, p);
            double llNull = state.LogLik;

            bool converged = false, monotone = false;
            int iterations = 0;
            double ll = llNull;
            for (; iterations < MaxIterations; iterations++)
            {
                cancellation.ThrowIfCancellationRequested();
                Evaluate(time, status, x, cols, order, beta, ties, state, needDerivatives: true, cancellation);
                ll = state.LogLik;
                if (!SolveStep(state.Info, state.Grad, state.Step))
                {
                    monotone = MaxAbs(beta) > 5 || iterations > 0;
                    break;
                }
                if (MaxAbs(state.Step) <= Tolerance)
                {
                    converged = true;
                    iterations++;
                    break;
                }
                double alpha = 1;
                bool accepted = false;
                var trial = new double[p];
                for (int half = 0; half < 30; half++)
                {
                    bool tooBig = false;
                    for (int j = 0; j < p; j++)
                    {
                        trial[j] = beta[j] + alpha * state.Step[j];
                        if (Math.Abs(trial[j]) > SeparationLimit) tooBig = true;
                    }
                    if (tooBig) { alpha *= 0.5; continue; }
                    Evaluate(time, status, x, cols, order, trial, ties, state, needDerivatives: false, cancellation);
                    if (state.LogLik >= ll - 1e-8) { accepted = true; break; }
                    alpha *= 0.5;
                    if (alpha < 1e-8) break;
                }
                if (!accepted)
                {
                    monotone = MaxAbs(beta) > 5;
                    break;
                }
                Array.Copy(trial, beta, p);
                if (MaxAbs(beta) >= SeparationLimit) { monotone = true; break; }
            }
            if (!converged && MaxAbs(beta) >= SeparationLimit) monotone = true;

            Evaluate(time, status, x, cols, order, beta, ties, state, needDerivatives: true, cancellation);
            ll = state.LogLik;
            var se = new double[p];
            var z = new double[p];
            var pval = new double[p];
            var hr = new double[p];
            var hrLo = new double[p];
            var hrHi = new double[p];
            double wald = double.NaN, waldP = double.NaN;
            double zcrit = Dist.NormalQuantile(0.975);
            double[,]? cov = null;
            bool covOk = converged && InvertInformation(state.Info, null, ridge: false, out cov, out _);
            if (covOk && cov is not null)
            {
                wald = 0;
                var ib = new double[p];
                for (int j = 0; j < p; j++)
                {
                    double s = 0;
                    for (int k = 0; k < p; k++) s += state.Info[j, k] * beta[k];
                    ib[j] = s;
                    wald += beta[j] * s;
                }
                waldP = Dist.ChiSquareUpper(wald, p);
                for (int j = 0; j < p; j++)
                {
                    double v = cov[j, j];
                    se[j] = v > 0 && double.IsFinite(v) ? Math.Sqrt(v) : double.NaN;
                    z[j] = se[j] > 0 ? beta[j] / se[j] : double.NaN;
                    pval[j] = Dist.NormalTwoSided(z[j]);
                    hr[j] = Math.Exp(beta[j]);
                    hrLo[j] = Math.Exp(beta[j] - zcrit * se[j]);
                    hrHi[j] = Math.Exp(beta[j] + zcrit * se[j]);
                }
            }
            else
            {
                for (int j = 0; j < p; j++)
                {
                    se[j] = z[j] = pval[j] = hrLo[j] = hrHi[j] = double.NaN;
                    hr[j] = double.IsFinite(beta[j]) ? Math.Exp(beta[j]) : double.NaN;
                }
            }

            double lr = converged ? 2 * (ll - llNull) : double.NaN;
            double lrP = converged ? Dist.ChiSquareUpper(lr, p) : double.NaN;
            HarrellC(time, status, x, cols, order, beta, out double c, out long comp, out long conc, out long disc, out long tied, cancellation);

            return new CoxFit(
                ties, names, beta, se, z, pval, hr, hrLo, hrHi,
                ll, llNull, lr, lrP, scoreStat, scoreP, wald, waldP, p,
                iterations, converged, monotone,
                double.IsFinite(c) ? c : double.NaN, comp, conc, disc, tied, events,
                rowsRead, rowsUsed, rowsDropped, eventLevel);
        }

        private sealed class CoxWork
        {
            public readonly double[] Eta;
            public readonly double[] Grad;
            public readonly double[] Step;
            public readonly double[,] Info;
            public double LogLik;
            public CoxWork(int n, int p)
            {
                Eta = new double[n];
                Grad = new double[p];
                Step = new double[p];
                Info = new double[p, p];
            }
        }

        /// <summary>Breslow 또는 Efron 부분 로그가능도. 위험집합은 시각 내림차순 한 패스(결측 진입 없음).</summary>
        private static void Evaluate(
            double[] time, int[] status, double[,] x, int[] cols, int[] order, double[] beta, CoxTies ties,
            CoxWork work, bool needDerivatives, CancellationToken cancellation)
        {
            int n = time.Length;
            int p = beta.Length;
            double shift = double.NegativeInfinity;
            for (int row = 0; row < n; row++)
            {
                if ((row & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                double eta = 0;
                for (int j = 0; j < p; j++) eta += x[row, cols[j]] * beta[j];
                work.Eta[row] = eta;
                if (eta > shift) shift = eta;
            }
            if (!double.IsFinite(shift)) shift = 0;

            if (needDerivatives)
            {
                Array.Clear(work.Grad);
                Array.Clear(work.Info);
            }
            double ll = 0;
            var s1 = needDerivatives ? new double[p] : null;
            var s2 = needDerivatives ? new double[p, p] : null;
            var f1 = needDerivatives ? new double[p] : null;
            var f2 = needDerivatives ? new double[p, p] : null;
            var xv = new double[p];
            double s0 = 0;
            int i = n - 1;
            int seen = 0;
            while (i >= 0)
            {
                if ((seen & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                double t = time[order[i]];
                double f0 = 0;
                double failEta = 0;
                int m = 0;
                if (f1 is not null) Array.Clear(f1);
                if (f2 is not null) Array.Clear(f2);
                while (i >= 0 && time[order[i]] == t)
                {
                    int row = order[i];
                    double w = Math.Exp(work.Eta[row] - shift);
                    s0 += w;
                    for (int j = 0; j < p; j++) xv[j] = x[row, cols[j]];
                    if (needDerivatives)
                    {
                        for (int j = 0; j < p; j++)
                        {
                            s1![j] += w * xv[j];
                            for (int k = 0; k <= j; k++) s2![j, k] += w * xv[j] * xv[k];
                        }
                    }
                    if (status[row] == 1)
                    {
                        m++;
                        failEta += work.Eta[row];
                        f0 += w;
                        if (needDerivatives)
                        {
                            for (int j = 0; j < p; j++)
                            {
                                f1![j] += w * xv[j];
                                work.Grad[j] += xv[j];
                                for (int k = 0; k <= j; k++) f2![j, k] += w * xv[j] * xv[k];
                            }
                        }
                    }
                    i--;
                    seen++;
                }
                if (m == 0) continue;
                if (!(s0 > 0) || !double.IsFinite(s0))
                {
                    work.LogLik = double.NegativeInfinity;
                    return;
                }
                if (ties == CoxTies.Breslow || m == 1)
                {
                    ll += failEta - m * (Math.Log(s0) + shift);
                    if (needDerivatives)
                    {
                        for (int j = 0; j < p; j++) work.Grad[j] -= m * s1![j] / s0;
                        double inv = 1.0 / s0;
                        double inv2 = inv * inv;
                        for (int j = 0; j < p; j++)
                            for (int k = 0; k <= j; k++)
                            {
                                double sij = S2(s2!, j, k);
                                work.Info[j, k] += m * (sij * inv - s1![j] * s1[k] * inv2);
                            }
                    }
                }
                else
                {
                    ll += failEta - m * shift;
                    double sumInv = 0, sumJInv = 0;
                    for (int r = 0; r < m; r++)
                    {
                        double jfrac = r / (double)m;
                        double denom = s0 - jfrac * f0;
                        if (!(denom > 0) || !double.IsFinite(denom))
                        {
                            work.LogLik = double.NegativeInfinity;
                            return;
                        }
                        ll -= Math.Log(denom);
                        if (!needDerivatives) continue;
                        sumInv += 1.0 / denom;
                        sumJInv += jfrac / denom;
                        for (int j = 0; j < p; j++)
                        {
                            double numer = s1![j] - jfrac * f1![j];
                            work.Grad[j] -= numer / denom;
                        }
                        for (int j = 0; j < p; j++)
                        {
                            double nj = (s1![j] - jfrac * f1![j]) / denom;
                            for (int k = 0; k <= j; k++)
                            {
                                double nk = (s1[k] - jfrac * f1[k]) / denom;
                                work.Info[j, k] -= nj * nk;
                            }
                        }
                    }
                    if (needDerivatives)
                    {
                        for (int j = 0; j < p; j++)
                            for (int k = 0; k <= j; k++)
                                work.Info[j, k] += S2(s2!, j, k) * sumInv - S2(f2!, j, k) * sumJInv;
                    }
                }
            }
            if (needDerivatives)
            {
                for (int j = 0; j < p; j++)
                    for (int k = j + 1; k < p; k++)
                        work.Info[j, k] = work.Info[k, j];
            }
            work.LogLik = ll;
        }

        private static double S2(double[,] s, int j, int k) => j >= k ? s[j, k] : s[k, j];

        private static bool SolveStep(double[,] info, double[] grad, double[] step)
        {
            int p = grad.Length;
            var a = (double[,])info.Clone();
            for (int j = 0; j < p; j++) a[j, j] += Ridge;
            try
            {
                if (!alglib.spdmatrixcholesky(a, p, false)) return false;
                alglib.spdmatrixcholeskysolve(a, p, false, grad, out double[] x, out var rep);
                if (rep.r1 < 0 || x.Length != p) return false;
                for (int j = 0; j < p; j++)
                {
                    if (!double.IsFinite(x[j])) return false;
                    step[j] = x[j];
                }
                return true;
            }
            catch (alglib.alglibexception)
            {
                return false;
            }
        }

        private static bool InvertInformation(double[,] info, double[]? grad, bool ridge, out double[,]? inverse, out double[]? solved)
        {
            inverse = null;
            solved = null;
            int p = info.GetLength(0);
            var a = (double[,])info.Clone();
            if (ridge) for (int j = 0; j < p; j++) a[j, j] += Ridge;
            try
            {
                if (!alglib.spdmatrixcholesky(a, p, false)) return false;
                if (grad is not null)
                {
                    alglib.spdmatrixcholeskysolve(a, p, false, grad, out double[] x, out var rep);
                    if (rep.r1 < 0) return false;
                    solved = x;
                }
                alglib.spdmatrixcholeskyinverse(a, p, false, out _);
                for (int i = 0; i < p; i++)
                    for (int j = i + 1; j < p; j++)
                        a[i, j] = a[j, i];
                inverse = a;
                return true;
            }
            catch (alglib.alglibexception)
            {
                return false;
            }
        }

        private static double Quadratic(double[,] info, double[] grad)
        {
            if (!InvertInformation(info, grad, ridge: false, out _, out double[]? x) || x is null) return double.NaN;
            double s = 0;
            for (int j = 0; j < grad.Length; j++) s += grad[j] * x[j];
            return s;
        }

        private static double MaxAbs(double[] v)
        {
            double m = 0;
            foreach (double x in v) m = Math.Max(m, Math.Abs(x));
            return m;
        }

        /// <summary>Harrell's C. 시각이 같은 쌍은 비교하지 않고, 선형예측자가 같으면 0.5. 전체 비교 쌍, O(n log n).</summary>
        public static void HarrellC(
            double[] time, int[] status, double[,] x, int[] cols, int[] order, double[] beta,
            out double c, out long comparable, out long concordant, out long discordant, out long tied,
            CancellationToken cancellation = default)
        {
            int n = time.Length;
            int p = beta.Length;
            var eta = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int j = 0; j < p; j++) s += x[i, cols[j]] * beta[j];
                eta[i] = s;
            }
            var uniq = eta.Distinct().OrderBy(v => v).ToArray();
            var rankOf = new Dictionary<double, int>(n);
            for (int i = 0; i < uniq.Length; i++) rankOf[uniq[i]] = i + 1;
            var bit = new long[uniq.Length + 2];
            long total = 0;
            concordant = discordant = tied = 0;

            void Add(int rank)
            {
                total++;
                for (int k = rank; k < bit.Length; k += k & -k) bit[k]++;
            }
            long Sum(int rank)
            {
                long s = 0;
                for (int k = rank; k > 0; k -= k & -k) s += bit[k];
                return s;
            }

            int idx = n - 1;
            int seen = 0;
            while (idx >= 0)
            {
                if ((seen & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                double t = time[order[idx]];
                int hi = idx;
                while (idx >= 0 && time[order[idx]] == t) idx--;
                for (int k = idx + 1; k <= hi; k++)
                {
                    int row = order[k];
                    if (status[row] != 1) continue;
                    int rank = rankOf[eta[row]];
                    long less = Sum(rank - 1);
                    long leq = Sum(rank);
                    concordant += less;
                    tied += leq - less;
                    discordant += total - leq;
                }
                for (int k = idx + 1; k <= hi; k++) Add(rankOf[eta[order[k]]]);
                seen += hi - idx;
            }
            comparable = concordant + discordant + tied;
            c = comparable == 0 ? double.NaN : (concordant + 0.5 * tied) / comparable;
        }
    }

    /// <summary>Kaplan–Meier 계단 곡선 + 중도절단 표식. 렌더는 PlotControl.</summary>
    public static class KaplanMeierPlot
    {
        public static PlotModel Build(KaplanMeierResult result, string title, string xAxis, string yAxis, string renderNote)
        {
            var series = new List<PlotSeries>();
            double xmax = 0;
            bool thinned = false;
            for (int g = 0; g < result.Curves.Count; g++)
            {
                var curve = result.Curves[g];
                xmax = Math.Max(xmax, curve.RestrictedMeanTau);
                var (xs, ys, thin) = Steps(curve);
                thinned |= thin;
                series.Add(new PlotSeries
                {
                    Kind = PlotSeriesKind.Line,
                    Name = curve.Group,
                    PaletteIndex = g,
                    Xs = xs,
                    Ys = ys,
                });
                var (cx, cy) = CensorMarks(curve);
                if (cx.Length > 0)
                    series.Add(new PlotSeries
                    {
                        Kind = PlotSeriesKind.Points,
                        Name = "",
                        PaletteIndex = g,
                        Xs = cx,
                        Ys = cy,
                    });
                var (lx, ly, hx, hy) = CiSteps(curve);
                if (lx.Length > 1)
                {
                    series.Add(new PlotSeries { Kind = PlotSeriesKind.Line, Name = "", PaletteIndex = g, Dashed = true, Xs = lx, Ys = ly });
                    series.Add(new PlotSeries { Kind = PlotSeriesKind.Line, Name = "", PaletteIndex = g, Dashed = true, Xs = hx, Ys = hy });
                }
            }
            if (!(xmax > 0)) xmax = 1;
            return new PlotModel
            {
                Title = title,
                XAxis = new PlotAxis { Title = xAxis, Min = 0, Max = xmax * 1.02 },
                YAxis = new PlotAxis { Title = yAxis, Min = -0.02, Max = 1.05 },
                Series = series,
                ReferenceLines = new[] { new ReferenceLine(0.5, false, "0.5", 0) },
                RenderNote = renderNote + (thinned ? " · plot steps thinned" : ""),
                AllowZoom = true,
            };
        }

        /// <summary>오른쪽 연속 계단. (0,1)에서 시작해 마지막 관측 시각까지 수평으로 잇는다.</summary>
        public static (double[] Xs, double[] Ys, bool Thinned) Steps(SurvivalCurve curve)
        {
            var rows = curve.Rows;
            int n = rows.Count;
            int stride = n > KaplanMeierAnalysis.PlotStepCap ? (n + KaplanMeierAnalysis.PlotStepCap - 1) / KaplanMeierAnalysis.PlotStepCap : 1;
            var pick = new List<int>();
            for (int i = 0; i < n; i += stride) pick.Add(i);
            if (n > 0 && pick[^1] != n - 1) pick.Add(n - 1);
            var xs = new List<double> { 0 };
            var ys = new List<double> { 1 };
            double prevT = 0, prevS = 1;
            foreach (int i in pick)
            {
                double t = rows[i].Time;
                double s = rows[i].Survival;
                xs.Add(t); ys.Add(prevS);
                xs.Add(t); ys.Add(s);
                prevT = t; prevS = s;
            }
            double tau = curve.RestrictedMeanTau;
            if (tau > prevT) { xs.Add(tau); ys.Add(prevS); }
            return (xs.ToArray(), ys.ToArray(), stride > 1);
        }

        public static (double[] Xs, double[] Ys) CensorMarks(SurvivalCurve curve)
        {
            if (curve.CensorTimes.Count == 0) return (Array.Empty<double>(), Array.Empty<double>());
            var xs = new double[curve.CensorTimes.Count];
            var ys = new double[curve.CensorTimes.Count];
            int r = 0;
            double s = 1;
            for (int i = 0; i < curve.CensorTimes.Count; i++)
            {
                double t = curve.CensorTimes[i];
                while (r < curve.Rows.Count && curve.Rows[r].Time <= t)
                {
                    s = curve.Rows[r].Survival;
                    r++;
                }
                xs[i] = t;
                ys[i] = s;
            }
            return (xs, ys);
        }

        private static (double[] lx, double[] ly, double[] hx, double[] hy) CiSteps(SurvivalCurve curve)
        {
            var rows = curve.Rows;
            if (rows.Count == 0 || !double.IsFinite(rows[0].CiLow)) return (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
            int stride = rows.Count > KaplanMeierAnalysis.PlotStepCap
                ? (rows.Count + KaplanMeierAnalysis.PlotStepCap - 1) / KaplanMeierAnalysis.PlotStepCap : 1;
            var lxs = new List<double>();
            var lys = new List<double>();
            var hxs = new List<double>();
            var hys = new List<double>();
            double prevT = 0;
            for (int i = 0; i < rows.Count; i += stride)
            {
                if (!double.IsFinite(rows[i].CiLow) || !double.IsFinite(rows[i].CiHigh)) break;
                double t = rows[i].Time;
                if (lxs.Count == 0)
                {
                    lxs.Add(0); lys.Add(1);
                    hxs.Add(0); hys.Add(1);
                }
                lxs.Add(t); lys.Add(lys[^1]);
                lxs.Add(t); lys.Add(rows[i].CiLow);
                hxs.Add(t); hys.Add(hys[^1]);
                hxs.Add(t); hys.Add(rows[i].CiHigh);
                prevT = t;
            }
            if (lxs.Count > 0 && curve.RestrictedMeanTau > prevT && double.IsFinite(lys[^1]))
            {
                lxs.Add(curve.RestrictedMeanTau); lys.Add(lys[^1]);
                hxs.Add(curve.RestrictedMeanTau); hys.Add(hys[^1]);
            }
            return (lxs.ToArray(), lys.ToArray(), hxs.ToArray(), hys.ToArray());
        }
    }
}
