using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    /// <summary>계수 한 행. 별칭(선형 종속) 열은 Aliased이고 추정·표준오차·검정은 NaN.</summary>
    public sealed record Coefficient(
        string Name,
        double Estimate,
        double StdError,
        double T,
        double PValue,
        double CiLow,
        double CiHigh,
        bool Aliased);

    /// <summary>잔차 요약(최소·사분위·최대·평균). 사분위는 numpy/R type 7(선형 보간).</summary>
    public sealed record ResidualSummary(double Min, double Q1, double Median, double Q3, double Max, double Mean);

    /// <summary>
    /// 잔차 정규성(Shapiro–Wilk, Royston). SampleSize는 사용한 잔차 수(최대 5,000, 행 순서의 앞부분).
    /// Capped면 전체가 아니라 앞 5,000개만 검정한 근사다.
    /// </summary>
    public sealed record ResidualNormality(double W, double PValue, int SampleSize, bool Capped);

    /// <summary>
    /// Type II 분산분석 한 항. PartialEtaSquared = SS / (SS + 완전모형 잔차 SS) — SPSS형 부분 η².
    /// 추정 불가면 Df 0, F·p·부분 η²는 NaN.
    /// </summary>
    public sealed record AnovaTerm(
        string Name,
        double SumOfSquares,
        int Df,
        double MeanSquare,
        double F,
        double PValue,
        double PartialEtaSquared);

    /// <summary>
    /// 내포 모형 F검정. F = ((RSS_reduced − RSS_full) / (rank_full − rank_reduced)) / (RSS_full / df_full).
    /// 분모는 완전모형 잔차 평균제곱(statsmodels anova_lm(reduced, full)과 같음).
    /// </summary>
    public sealed record NestedFTest(
        double F,
        double PValue,
        int DfNumerator,
        int DfDenominator,
        double RssReduced,
        double RssFull,
        int RankReduced,
        int RankFull);

    /// <summary>공변량 평균에서의 보정(주변) 평균.</summary>
    public sealed record AdjustedMean(
        string Level,
        int Count,
        double Estimate,
        double StdError,
        double CiLow,
        double CiHigh);

    /// <summary>보정 평균의 쌍별 차이(LevelB − LevelA). BonferroniP = min(1, p × 비교 수).</summary>
    public sealed record AdjustedMeanDifference(
        string LevelA,
        string LevelB,
        double Difference,
        double StdError,
        double T,
        double PValue,
        double BonferroniP);

    /// <summary>ANCOVA: 가법 모형 적합, Type II, 기울기 동질성, 보정 평균, 쌍별 비교.</summary>
    public sealed record AncovaResult(
        LinearModelFit Additive,
        IReadOnlyList<AnovaTerm> TypeII,
        NestedFTest Slopes,
        IReadOnlyList<string> Covariates,
        IReadOnlyList<double> CovariateMeans,
        IReadOnlyList<AdjustedMean> AdjustedMeans,
        IReadOnlyList<AdjustedMeanDifference> Pairwise);

    /// <summary>
    /// 일반선형모형(OLS) 적합. 계수·적합 통계·잔차 요약.
    /// R²는 절편이 있으면 중심화 TSS, 없으면 비중심 TSS(statsmodels 정의).
    /// AIC/BIC = −2·llf + k·{2, log n}, k = 계수 순위(절편 포함, 척도 모수 제외 — statsmodels OLS).
    /// </summary>
    public sealed class LinearModelFit
    {
        public required IReadOnlyList<Coefficient> Coefficients { get; init; }
        public required double[] Beta { get; init; }
        /// <summary>(XᵀX)⁻¹. 별칭 열의 행·열은 NaN.</summary>
        public required double[,] XtXInverse { get; init; }
        public required bool[] Aliased { get; init; }
        public required ResidualSummary Residuals { get; init; }
        /// <summary>잔차가 상수이거나 3개 미만이면 null(검정을 유효한 것처럼 보고하지 않음).</summary>
        public ResidualNormality? Normality { get; init; }

        public int N { get; init; }
        public int Rank { get; init; }
        /// <summary>n − rank.</summary>
        public int DfResidual { get; init; }
        /// <summary>rank − (상수 있으면 1, 없으면 0).</summary>
        public int DfModel { get; init; }
        /// <summary>열 공간에 상수가 있음(명시 절편 또는 전체 수준 범주의 암묵 상수). false면 R²는 비중심.</summary>
        public bool HasIntercept { get; init; }
        public double Rss { get; init; }
        /// <summary>RSS / df_resid. df_resid가 0이면 NaN.</summary>
        public double Sigma2 { get; init; }
        public double ResidualSe { get; init; }
        public double RSquared { get; init; }
        public double AdjustedRSquared { get; init; }
        public double FStatistic { get; init; }
        public double FPValue { get; init; }
        public double LogLikelihood { get; init; }
        public double Aic { get; init; }
        public double Bic { get; init; }

        public bool HasAliased => Aliased.Any(a => a);
    }

    /// <summary>OLS 일반선형모형·Type II 분산분석·ANCOVA. WinForms 없음, 결정적.</summary>
    public static class LinearModel
    {
        public const double Confidence = 0.95;
        /// <summary>Shapiro–Wilk는 Royston 근사가 n≤5,000에서 검증되어 있어 앞 5,000잔차만 쓴다.</summary>
        public const int ShapiroSampleCap = 5000;

        /// <summary>반응 열이 수치형이 아니면 사용자 안내 예외. 식 대화상자·실행 경로가 같이 쓴다.</summary>
        public static void EnsureNumericResponse(IReadOnlyList<string> headers, ModelFormula formula, Func<int, VariableKind> kindOf)
        {
            int col = StatValue.ResolveColumn(headers, formula.Response);
            if (kindOf(col) != VariableKind.Numeric)
                throw new DesignMatrixException(
                    $"Response '{formula.Response}' is not numeric. A general linear model needs a numeric response.");
        }

        public static LinearModelFit Fit(DesignMatrix design, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var ls = LeastSquares.Fit(design.X, design.Y, cancellation: cancellation);
            if (ls.Rank == 0)
                throw new DesignMatrixException("The model has no estimable coefficients (every column is aliased).");

            int n = design.RowCount;
            int dfResid = n - ls.Rank;
            // 명시 절편 또는 전체 수준 범주(암묵 상수)면 중심화 TSS·상수 자유도(statsmodels k_constant).
            bool hasConstant = design.HasIntercept || design.HasImplicitConstant;
            int kConst = hasConstant ? 1 : 0;
            int dfModel = ls.Rank - kConst;
            double rss = ls.WeightedRss;
            if (rss < 0 && rss > -1e-8) rss = 0;
            double sigma2 = dfResid > 0 ? rss / dfResid : double.NaN;
            double sigma = sigma2 >= 0 ? Math.Sqrt(sigma2) : double.NaN;

            double tss = hasConstant ? CenteredSumOfSquares(design.Y, cancellation) : SumOfSquares(design.Y, cancellation);
            double r2 = tss == 0 ? double.NaN : 1 - rss / tss;
            double adj = double.IsNaN(r2) || dfResid <= 0
                ? double.NaN
                : 1 - ((double)(n - kConst) / dfResid) * (1 - r2);

            double ess = double.IsNaN(tss) ? double.NaN : tss - rss;
            double f = double.NaN, fp = double.NaN;
            if (dfModel > 0 && dfResid > 0 && !double.IsNaN(ess))
            {
                if (rss == 0)
                {
                    f = ess > 0 ? double.PositiveInfinity : double.NaN;
                    fp = ess > 0 ? 0 : double.NaN;
                }
                else
                {
                    f = (ess / dfModel) / (rss / dfResid);
                    fp = Dist.FUpper(f, dfModel, dfResid);
                }
            }

            double llf = LogLikelihood(n, rss);
            int kParams = ls.Rank;
            double aic = double.IsFinite(llf) ? -2 * llf + 2 * kParams : (double.IsPositiveInfinity(llf) ? double.NegativeInfinity : double.NaN);
            double bic = double.IsFinite(llf) ? -2 * llf + Math.Log(n) * kParams : (double.IsPositiveInfinity(llf) ? double.NegativeInfinity : double.NaN);

            double crit = Dist.TQuantile(1 - (1 - Confidence) / 2, dfResid);
            var coefs = new Coefficient[design.ColumnCount];
            for (int j = 0; j < design.ColumnCount; j++)
            {
                bool aliased = ls.Aliased[j] || double.IsNaN(ls.Beta[j]);
                double est = aliased ? double.NaN : ls.Beta[j];
                double se = double.NaN;
                if (!aliased && sigma2 >= 0)
                {
                    double v = ls.XtWXInverse[j, j];
                    if (v < 0 && v > -1e-10) v = 0;
                    if (v >= 0 && !double.IsNaN(v)) se = Math.Sqrt(sigma2 * v);
                }
                double t = se > 0 ? est / se : double.NaN;
                double p = Dist.TTwoSided(t, dfResid);
                double lo = double.NaN, hi = double.NaN;
                if (!double.IsNaN(est) && !double.IsNaN(se) && !double.IsNaN(crit))
                {
                    lo = est - crit * se;
                    hi = est + crit * se;
                }
                coefs[j] = new Coefficient(design.ColumnNames[j], est, se, t, p, lo, hi, aliased);
            }

            var resid = Residuals(design.X, design.Y, ls.Beta, ls.Aliased, cancellation);
            var summary = Summarize(resid);
            var normality = Shapiro(resid, cancellation);

            return new LinearModelFit
            {
                Coefficients = coefs,
                Beta = ls.Beta,
                XtXInverse = ls.XtWXInverse,
                Aliased = ls.Aliased,
                Residuals = summary,
                Normality = normality,
                N = n,
                Rank = ls.Rank,
                DfResidual = dfResid,
                DfModel = dfModel,
                HasIntercept = hasConstant,
                Rss = rss,
                Sigma2 = sigma2,
                ResidualSe = sigma,
                RSquared = r2,
                AdjustedRSquared = adj,
                FStatistic = f,
                FPValue = fp,
                LogLikelihood = llf,
                Aic = aic,
                Bic = bic,
            };
        }

        /// <summary>
        /// Type II 제곱합. 항 T에 대해
        /// SS = RSS(T와 T를 포함하는 고차항을 뺀 모형) − RSS(T를 포함하는 고차항만 뺀 모형).
        /// F는 완전모형 잔차 평균제곱을 분모로 한다(statsmodels anova_lm typ=2).
        /// 부분 모형은 설계행렬 열을 골라 다시 적합한다(행을 다시 읽지 않음).
        /// </summary>
        public static IReadOnlyList<AnovaTerm> TypeII(DesignMatrix design, CancellationToken cancellation = default)
            => TypeII(design, Fit(design, cancellation), cancellation);

        public static IReadOnlyList<AnovaTerm> TypeII(DesignMatrix design, LinearModelFit full, CancellationToken cancellation = default)
        {
            if (full.N != design.RowCount) throw new ArgumentException("Fit does not belong to this design matrix.", nameof(full));
            double mse = full.DfResidual > 0 ? full.Rss / full.DfResidual : double.NaN;
            var cache = new Dictionary<string, (double Rss, int Rank)>();
            var rows = new List<AnovaTerm>(design.Terms.Count);
            foreach (var term in design.Terms)
            {
                cancellation.ThrowIfCancellationRequested();
                var keep = new List<TermColumns>();
                var without = new List<TermColumns>();
                foreach (var other in design.Terms)
                {
                    if (IsStrictlyHigherContaining(other.Term, term.Term)) continue;
                    keep.Add(other);
                    if (!other.Term.Equals(term.Term)) without.Add(other);
                }
                var withFit = Submodel(design, keep, cache, cancellation);
                var withoutFit = Submodel(design, without, cache, cancellation);
                double ss = withoutFit.Rss - withFit.Rss;
                if (ss < 0 && ss > -1e-8 * (1 + Math.Abs(withoutFit.Rss))) ss = 0;
                int df = withFit.Rank - withoutFit.Rank;
                if (df < 0) df = 0;
                double ms = df > 0 ? ss / df : double.NaN;
                double f = double.NaN, p = double.NaN;
                if (df > 0 && full.DfResidual > 0 && mse > 0)
                {
                    f = ss == 0 ? 0 : ms / mse;
                    p = Dist.FUpper(f, df, full.DfResidual);
                }
                else if (df > 0 && full.DfResidual > 0 && mse == 0 && ss > 0)
                {
                    f = double.PositiveInfinity;
                    p = 0;
                }
                double eta = !double.IsNaN(ss) && ss + full.Rss > 0 ? ss / (ss + full.Rss) : double.NaN;
                rows.Add(new AnovaTerm(term.Term.Name, ss, df, ms, f, p, eta));
            }
            return rows;
        }

        /// <summary>축소 모형이 완전 모형에 내포된다고 보고 F를 계산한다. n이 다르면 예외.</summary>
        public static NestedFTest NestedF(LinearModelFit reduced, LinearModelFit full)
        {
            if (reduced.N != full.N) throw new ArgumentException("Nested models must use the same rows.");
            return NestedF(reduced.N, reduced.Rss, reduced.Rank, full.Rss, full.Rank);
        }

        public static NestedFTest NestedF(int n, double rssReduced, int rankReduced, double rssFull, int rankFull)
        {
            int df1 = rankFull - rankReduced;
            int df2 = n - rankFull;
            double ss = rssReduced - rssFull;
            if (ss < 0 && ss > -1e-8 * (1 + Math.Abs(rssReduced))) ss = 0;
            double f = double.NaN, p = double.NaN;
            if (df1 > 0 && df2 > 0)
            {
                if (rssFull == 0)
                {
                    f = ss > 0 ? double.PositiveInfinity : double.NaN;
                    p = ss > 0 ? 0 : double.NaN;
                }
                else if (ss >= 0)
                {
                    f = (ss / df1) / (rssFull / df2);
                    p = Dist.FUpper(f, df1, df2);
                }
            }
            return new NestedFTest(f, p, df1, df2, rssReduced, rssFull, rankReduced, rankFull);
        }

        /// <summary>
        /// 공분산분석. additive는 y ~ C(요인) + 수치 공변량(주효과만, 절편 있음).
        /// 기울기 동질성은 요인×공변량 상호작용 열을 설계행렬에서 만들어 내포 F로 검정한다(행을 다시 읽지 않음).
        /// 보정 평균은 각 공변량의 사용 행 평균에서 평가한 선형 결합이다.
        /// </summary>
        public static AncovaResult Ancova(DesignMatrix additive, string factor, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!additive.HasIntercept)
                throw new DesignMatrixException("ANCOVA requires an intercept so adjusted means are estimable.");
            if (additive.Terms.Any(t => t.Term.Order != 1))
                throw new DesignMatrixException(
                    "ANCOVA expects main effects only (one factor + covariates). Interactions are added internally for the slopes test.");
            if (!additive.Factors.TryGetValue(factor, out var levels))
                throw new DesignMatrixException(
                    $"'{factor}' must be a categorical factor with at least two levels in the complete rows.");
            var factorTerm = additive.Terms.FirstOrDefault(t => t.Term.Variables.Count == 1 && t.Term.Variables[0] == factor);
            if (factorTerm is null)
                throw new DesignMatrixException($"'{factor}' is not a term in the model.");

            var covariates = new List<TermColumns>();
            foreach (var t in additive.Terms)
            {
                if (t.Term.Equals(factorTerm.Term)) continue;
                if (t.Count != 1 || additive.Factors.ContainsKey(t.Term.Variables[0]))
                    throw new DesignMatrixException($"'{t.Term.Name}' is not a numeric covariate. ANCOVA covariates must be numeric.");
                covariates.Add(t);
            }
            if (covariates.Count == 0)
                throw new DesignMatrixException("ANCOVA requires at least one numeric covariate.");

            var additiveFit = Fit(additive, cancellation);
            var type2 = TypeII(additive, additiveFit, cancellation);

            int n = additive.RowCount;
            int nInteract = factorTerm.Count * covariates.Count;
            int pFull = additive.ColumnCount + nInteract;
            var xFull = new double[n, pFull];
            for (int i = 0; i < n; i++)
            {
                if ((i & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                for (int j = 0; j < additive.ColumnCount; j++) xFull[i, j] = additive.X[i, j];
            }
            int col = additive.ColumnCount;
            foreach (var cov in covariates)
            {
                for (int d = 0; d < factorTerm.Count; d++)
                {
                    int srcD = factorTerm.Start + d;
                    int srcC = cov.Start;
                    for (int i = 0; i < n; i++)
                    {
                        if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
                        xFull[i, col] = additive.X[i, srcD] * additive.X[i, srcC];
                    }
                    col++;
                }
            }
            var fullLs = LeastSquares.Fit(xFull, additive.Y, cancellation: cancellation);
            var slopes = NestedF(n, additiveFit.Rss, additiveFit.Rank, fullLs.WeightedRss, fullLs.Rank);

            var means = new double[covariates.Count];
            for (int k = 0; k < covariates.Count; k++)
                means[k] = ColumnMean(additive.X, covariates[k].Start, cancellation);

            var counts = LevelCounts(additive, factorTerm, levels.Levels.Count);
            var adjusted = new List<AdjustedMean>(levels.Levels.Count);
            var contrasts = new double[levels.Levels.Count][];
            for (int L = 0; L < levels.Levels.Count; L++)
            {
                var c = MeanContrast(additive.ColumnCount, factorTerm, covariates, means, L);
                contrasts[L] = c;
                if (!TryLinearCombination(additiveFit, c, out double est, out double se))
                    adjusted.Add(new AdjustedMean(levels.Levels[L], counts[L], double.NaN, double.NaN, double.NaN, double.NaN));
                else
                {
                    double crit = Dist.TQuantile(1 - (1 - Confidence) / 2, additiveFit.DfResidual);
                    double lo = double.NaN, hi = double.NaN;
                    if (!double.IsNaN(est) && !double.IsNaN(se) && !double.IsNaN(crit))
                    {
                        lo = est - crit * se;
                        hi = est + crit * se;
                    }
                    adjusted.Add(new AdjustedMean(levels.Levels[L], counts[L], est, se, lo, hi));
                }
            }

            int pairs = levels.Levels.Count * (levels.Levels.Count - 1) / 2;
            var diffs = new List<AdjustedMeanDifference>(pairs);
            for (int a = 0; a < levels.Levels.Count; a++)
            {
                for (int b = a + 1; b < levels.Levels.Count; b++)
                {
                    var c = new double[additive.ColumnCount];
                    for (int j = 0; j < c.Length; j++) c[j] = contrasts[b][j] - contrasts[a][j];
                    if (!TryLinearCombination(additiveFit, c, out double diff, out double se) || !(se > 0))
                    {
                        diffs.Add(new AdjustedMeanDifference(levels.Levels[a], levels.Levels[b], diff, se, double.NaN, double.NaN, double.NaN));
                        continue;
                    }
                    double t = diff / se;
                    double p = Dist.TTwoSided(t, additiveFit.DfResidual);
                    double bonf = double.IsNaN(p) ? double.NaN : Math.Min(1, p * pairs);
                    diffs.Add(new AdjustedMeanDifference(levels.Levels[a], levels.Levels[b], diff, se, t, p, bonf));
                }
            }

            return new AncovaResult(
                additiveFit,
                type2,
                slopes,
                covariates.Select(t => t.Term.Name).ToArray(),
                means,
                adjusted,
                diffs);
        }

        /// <summary>c′β 와 표준오차. 별칭 열에 0이 아닌 가중이면 추정 불가(false).</summary>
        public static bool TryLinearCombination(LinearModelFit fit, double[] contrast, out double estimate, out double se)
        {
            estimate = 0;
            se = double.NaN;
            int p = contrast.Length;
            if (p != fit.Beta.Length) throw new ArgumentException("Contrast length must match the coefficient count.", nameof(contrast));
            for (int j = 0; j < p; j++)
            {
                if (contrast[j] == 0) continue;
                if (fit.Aliased[j] || double.IsNaN(fit.Beta[j])) return false;
                estimate += contrast[j] * fit.Beta[j];
            }
            if (!(fit.Sigma2 >= 0)) return true;
            double quad = 0;
            var inv = fit.XtXInverse;
            for (int i = 0; i < p; i++)
            {
                if (contrast[i] == 0) continue;
                for (int j = 0; j < p; j++)
                {
                    if (contrast[j] == 0) continue;
                    double v = inv[i, j];
                    if (double.IsNaN(v)) return false;
                    quad += contrast[i] * v * contrast[j];
                }
            }
            if (quad < 0)
            {
                if (quad > -1e-8) quad = 0;
                else return false;
            }
            se = Math.Sqrt(fit.Sigma2 * quad);
            return true;
        }

        private static bool IsStrictlyHigherContaining(FormulaTerm candidate, FormulaTerm term)
            => candidate.Contains(term) && !candidate.Equals(term);

        private static (double Rss, int Rank) Submodel(
            DesignMatrix design, List<TermColumns> terms, Dictionary<string, (double Rss, int Rank)> cache, CancellationToken ct)
        {
            var cols = new List<int>();
            if (design.HasIntercept) cols.Add(0);
            foreach (var t in terms)
                for (int j = 0; j < t.Count; j++) cols.Add(t.Start + j);
            string key = string.Join(",", cols);
            if (cache.TryGetValue(key, out var hit)) return hit;

            (double Rss, int Rank) result;
            if (cols.Count == 0)
            {
                result = (SumOfSquares(design.Y, ct), 0);
            }
            else
            {
                // 열 부분집합을 복사하지 않고 적합(수백만 행 × 여러 부분모형에서 n×p 복사 회피).
                var fit = LeastSquares.Fit(design.X, design.Y, cancellation: ct, columns: cols);
                double rss = fit.WeightedRss;
                if (double.IsNaN(rss)) rss = SumOfSquares(design.Y, ct);
                result = (rss, fit.Rank);
            }
            cache[key] = result;
            return result;
        }

        private static double[] MeanContrast(int p, TermColumns factor, List<TermColumns> covariates, double[] means, int levelIndex)
        {
            var c = new double[p];
            c[0] = 1; // 절편은 ANCOVA에서 필수
            if (levelIndex > 0) c[factor.Start + levelIndex - 1] = 1;
            for (int k = 0; k < covariates.Count; k++) c[covariates[k].Start] = means[k];
            return c;
        }

        private static int[] LevelCounts(DesignMatrix dm, TermColumns factor, int levelCount)
        {
            var counts = new int[levelCount];
            int n = dm.RowCount;
            for (int i = 0; i < n; i++)
            {
                int level = 0;
                for (int d = 0; d < factor.Count; d++)
                {
                    if (dm.X[i, factor.Start + d] != 0) { level = d + 1; break; }
                }
                counts[level]++;
            }
            return counts;
        }

        private static double ColumnMean(double[,] x, int col, CancellationToken ct)
        {
            int n = x.GetLength(0);
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
                s += x[i, col];
            }
            return s / n;
        }

        private static double[] Residuals(double[,] x, double[] y, double[] beta, bool[] aliased, CancellationToken ct)
        {
            int n = y.Length, p = x.GetLength(1);
            var resid = new double[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 8191) == 0) ct.ThrowIfCancellationRequested();
                double f = 0;
                for (int j = 0; j < p; j++)
                    if (!aliased[j] && !double.IsNaN(beta[j])) f += x[i, j] * beta[j];
                resid[i] = y[i] - f;
            }
            return resid;
        }

        private static ResidualSummary Summarize(double[] resid)
        {
            int n = resid.Length;
            double min = double.PositiveInfinity, max = double.NegativeInfinity, sum = 0;
            for (int i = 0; i < n; i++)
            {
                double v = resid[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            var sorted = (double[])resid.Clone();
            Array.Sort(sorted);
            return new ResidualSummary(min, Quantile(sorted, 0.25), Quantile(sorted, 0.5), Quantile(sorted, 0.75), max, n == 0 ? double.NaN : sum / n);
        }

        /// <summary>정렬된 표본의 분위수. h = (n−1)p 선형 보간(numpy percentile method='linear', R quantile type 7).</summary>
        internal static double Quantile(double[] sorted, double p)
        {
            int n = sorted.Length;
            if (n == 0) return double.NaN;
            if (n == 1 || p <= 0) return sorted[0];
            if (p >= 1) return sorted[n - 1];
            double h = (n - 1) * p;
            int lo = (int)Math.Floor(h);
            int hi = (int)Math.Ceiling(h);
            if (lo == hi) return sorted[lo];
            double w = h - lo;
            return sorted[lo] * (1 - w) + sorted[hi] * w;
        }

        private static ResidualNormality? Shapiro(double[] resid, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int n = resid.Length;
            if (n < 3) return null;
            bool capped = n > ShapiroSampleCap;
            int m = capped ? ShapiroSampleCap : n;
            IReadOnlyList<double> sample = resid;
            if (capped)
            {
                var head = new double[m];
                Array.Copy(resid, head, m);
                sample = head;
            }
            var sw = CsvStatistics.ShapiroWilk(sample);
            if (sw is null) return null;
            return new ResidualNormality(sw.W, sw.PValue, sw.SampleSize, capped);
        }

        private static double LogLikelihood(int n, double rss)
        {
            if (n <= 0) return double.NaN;
            if (rss <= 0) return double.PositiveInfinity;
            double nobs2 = n / 2.0;
            return -nobs2 * Math.Log(2 * Math.PI) - nobs2 * Math.Log(rss / n) - nobs2;
        }

        private static double SumOfSquares(double[] y, CancellationToken ct)
        {
            double s = 0;
            for (int i = 0; i < y.Length; i++)
            {
                if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
                s += y[i] * y[i];
            }
            return s;
        }

        private static double CenteredSumOfSquares(double[] y, CancellationToken ct)
        {
            double mean = 0;
            for (int i = 0; i < y.Length; i++) mean += y[i];
            mean /= y.Length;
            double s = 0;
            for (int i = 0; i < y.Length; i++)
            {
                if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
                double d = y[i] - mean;
                s += d * d;
            }
            return s;
        }
    }
}
