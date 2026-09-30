using System.Globalization;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    // 선형 혼합모형 (한 그룹 요인, 임의 절편 + 선택적 임의 기울기, 비구조 공분산).
    //
    // 주변 모형 y = Xβ + Zb + ε, b ~ N(0, σ² ΛΛ'), ε ~ N(0, σ² I).
    // σ²와 β는 프로파일하고, Λ(공분산의 하삼각 촐레스키)만 ALGLIB L-BFGS로 최대화한다
    // (statsmodels MixedLM의 profile / square-root 매개화와 같은 우도).
    // 그룹별 충분통계(X'X, Z'Z, …)와 Woodbury로 평가해 비용은 O(n p² + 그룹·q³)이다.
    // 표준오차는 고정효과 + 비척도 공분산 하삼각에 대한 관측 정보(프로파일 로그우도의 수치 헤시안)로,
    // statsmodels MixedLMResults.bse와 같은 정규 근사(z, 양측 정규 신뢰구간)다.

    /// <summary>고정효과 한 계수. 별칭이면 추정·표준오차·검정은 NaN.</summary>
    public sealed record MixedCoefficient(
        string Name,
        double Estimate,
        double StdError,
        double Z,
        double PValue,
        double CiLow,
        double CiHigh,
        bool Aliased);

    /// <summary>선형 혼합모형 적합. 분산성분은 잔차 분산을 곱한 사용자 척도(cov_re).</summary>
    public sealed class MixedModelResult
    {
        public required IReadOnlyList<MixedCoefficient> Coefficients { get; init; }
        /// <summary>임의효과 공분산 G (q×q, 잔차 분산을 곱한 값). 이름 순서는 <see cref="RandomEffectNames"/>.</summary>
        public required double[,] RandomCovariance { get; init; }
        public required IReadOnlyList<string> RandomEffectNames { get; init; }
        /// <summary>잔차 분산 σ² (statsmodels scale).</summary>
        public required double Scale { get; init; }
        /// <summary>적합에 쓴 기준(REML 또는 ML)의 프로파일 로그우도.</summary>
        public required double LogLikelihood { get; init; }
        /// <summary>같은 분산성분에서 평가한 ML 프로파일 로그우도. AIC/BIC의 재료.</summary>
        public required double MlLogLikelihood { get; init; }
        public required double Aic { get; init; }
        public required double Bic { get; init; }
        /// <summary>임의 절편 모형만. τ² / (τ² + σ²). 기울기가 있으면 null.</summary>
        public double? Icc { get; init; }
        public required int GroupCount { get; init; }
        public required int MinGroupSize { get; init; }
        public required int MaxGroupSize { get; init; }
        public required long RowsRead { get; init; }
        public required long RowsUsed { get; init; }
        public required long RowsDropped { get; init; }
        public required bool Reml { get; init; }
        public required bool Converged { get; init; }
        /// <summary>G의 최소 고유값이 수치적으로 0 — 경계(특이) 적합.</summary>
        public required bool Singular { get; init; }
        /// <summary>관측 정보가 양의 정부호가 아니면 표준오차는 NaN.</summary>
        public required bool InformationPositiveDefinite { get; init; }
        public required int Iterations { get; init; }
        /// <summary>statsmodels params: 고정효과 뒤에 비척도 공분산의 하삼각(행 우선).</summary>
        public required double[] ParameterVector { get; init; }
        /// <summary>statsmodels bse. 정보가 특이면 NaN.</summary>
        public required double[] ParameterStandardErrors { get; init; }

        public int FixedCount => Coefficients.Count;
        public int CovarianceParameterCount => RandomEffectNames.Count * (RandomEffectNames.Count + 1) / 2;
    }

    /// <summary>선형 혼합모형. 엔진은 WinForms에 의존하지 않고 결정적이다.</summary>
    public static class MixedModel
    {
        /// <summary>임의 기울기(절편 제외) 상한. 비구조 공분산의 q³ 비용을 묶는다.</summary>
        public const int MaxRandomSlopes = 8;

        public static MixedModelResult Fit(
            IReadOnlyList<string[]> rows,
            IReadOnlyList<string> headers,
            ModelFormula formula,
            Func<int, VariableKind> kindOf,
            int groupColumn,
            IReadOnlyList<int> randomSlopeColumns,
            bool reml = true,
            CancellationToken cancellation = default,
            DesignMatrixOptions? options = null)
        {
            if (groupColumn < 0 || groupColumn >= headers.Count)
                throw new DesignMatrixException("The grouping column is not in the table.");
            if (randomSlopeColumns.Count > MaxRandomSlopes)
                throw new DesignMatrixException($"At most {MaxRandomSlopes} random slopes are supported (unstructured covariance).");
            LinearModel.EnsureNumericResponse(headers, formula, kindOf);
            int response = StatValue.ResolveColumn(headers, formula.Response);
            if (groupColumn == response)
                throw new DesignMatrixException("The grouping column cannot be the response.");
            var slopeNames = new string[randomSlopeColumns.Count];
            var seen = new HashSet<int>();
            for (int s = 0; s < randomSlopeColumns.Count; s++)
            {
                int c = randomSlopeColumns[s];
                if (c < 0 || c >= headers.Count) throw new DesignMatrixException("A random-slope column is not in the table.");
                if (c == response) throw new DesignMatrixException($"Random slope '{headers[c]}' is the response.");
                if (c == groupColumn) throw new DesignMatrixException($"Random slope '{headers[c]}' is the grouping column.");
                if (!seen.Add(c)) throw new DesignMatrixException($"Random slope '{headers[c]}' is listed twice.");
                if (kindOf(c) != VariableKind.Numeric)
                    throw new DesignMatrixException($"Random slope '{headers[c]}' must be numeric.");
                slopeNames[s] = headers[c];
            }

            // 그룹·기울기 결측 행은 반응을 비워 설계행렬의 목록별 삭제에 포함한다.
            var source = new DropIncompleteRows(rows, response, groupColumn, randomSlopeColumns);
            var dm = DesignMatrixBuilder.Build(source, headers, formula, kindOf, options, cancellation);
            return FitDesign(dm, rows, groupColumn, randomSlopeColumns, slopeNames, reml, cancellation, options);
        }

        private static MixedModelResult FitDesign(
            DesignMatrix dm, IReadOnlyList<string[]> rows, int groupColumn, IReadOnlyList<int> slopeColumns,
            IReadOnlyList<string> slopeNames, bool reml, CancellationToken cancellation, DesignMatrixOptions? options)
        {
            int n = dm.RowCount;
            int p = dm.ColumnCount;
            int q = 1 + slopeColumns.Count;
            if (reml && n <= p)
                throw new DesignMatrixException("REML is not defined when the number of complete rows does not exceed the number of fixed effects. Use ML, or remove terms.");

            var labels = new string[n];
            var slope = slopeColumns.Count == 0 ? null : new double[n, slopeColumns.Count];
            var view = dm.ViewRows;
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                var row = rows[view[i]];
                labels[i] = row[groupColumn].Trim();
                for (int s = 0; s < slopeColumns.Count; s++)
                {
                    if (!StatValue.TryNumber(row[slopeColumns[s]], out double v))
                        throw new DesignMatrixException($"Random slope '{slopeNames[s]}' is not numeric in a row the fixed effects kept.");
                    slope![i, s] = v;
                }
            }

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var codes = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (!map.TryGetValue(labels[i], out int code))
                {
                    code = map.Count;
                    map[labels[i]] = code;
                }
                codes[i] = code;
            }
            if (map.Count < 2)
                throw new DesignMatrixException("A mixed model needs at least two groups in the complete rows.");

            var design = MixedDesign.Accumulate(dm.X, dm.Y, codes, map.Count, slope, cancellation, options?.MemoryBudgetBytes ?? new DesignMatrixOptions().MemoryBudgetBytes);
            design.FixedNames = dm.ColumnNames.ToArray();
            design.RandomNames = new string[q];
            design.RandomNames[0] = "Intercept";
            for (int s = 0; s < slopeNames.Count; s++) design.RandomNames[s + 1] = slopeNames[s];
            design.RowsRead = dm.RowsRead;
            design.RowsUsed = n;
            design.RowsDropped = dm.RowsDropped;
            RejectDependentRandomColumns(design);
            RejectDependentFixedColumns(design);
            return MixedModelEngine.Fit(design, reml, cancellation).ToResult(design, reml);
        }

        private static void RejectDependentFixedColumns(MixedDesign d)
        {
            var xtx = new double[d.P, d.P];
            for (int g = 0; g < d.Groups; g++)
                for (int i = 0; i < d.P; i++)
                    for (int j = 0; j <= i; j++)
                        xtx[i, j] = xtx[j, i] = xtx[i, j] + d.XtX[(g * d.P + i) * d.P + j];
            var aliased = AliasMask(xtx, d.P);
            if (aliased.All(a => !a)) return;
            var names = new List<string>();
            for (int j = 0; j < d.P; j++) if (aliased[j]) names.Add(d.FixedNames[j]);
            throw new DesignMatrixException(
                "Fixed-effects columns are linearly dependent (" + string.Join(", ", names) + "). Remove the aliased terms; a different model is not fit in their place.");
        }

        private static void RejectDependentRandomColumns(MixedDesign d)
        {
            var ztz = new double[d.Q, d.Q];
            for (int g = 0; g < d.Groups; g++)
                for (int i = 0; i < d.Q; i++)
                    for (int j = 0; j <= i; j++)
                        ztz[i, j] = ztz[j, i] = ztz[i, j] + d.ZtZ[(g * d.Q + i) * d.Q + j];
            var aliased = AliasMask(ztz, d.Q);
            if (aliased.All(a => !a)) return;
            var names = new List<string>();
            for (int j = 0; j < d.Q; j++) if (aliased[j]) names.Add(d.RandomNames[j]);
            throw new DesignMatrixException(
                "Random-effects columns are linearly dependent (" + string.Join(", ", names) + "). A slope may be constant or a copy of the intercept.");
        }

        /// <summary>순차 허용오차 촐레스키. 잔여 대각이 원래 대각의 <see cref="LeastSquares.AliasTolerance"/> 미만이면 별칭.</summary>
        private static bool[] AliasMask(double[,] a, int p)
        {
            var aliased = new bool[p];
            var kept = new List<int>(p);
            var l = new double[p, p];
            for (int j = 0; j < p; j++)
            {
                double orig = a[j, j];
                double d = orig;
                for (int k = 0; k < kept.Count; k++) { int kk = kept[k]; d -= l[j, kk] * l[j, kk]; }
                if (!(orig > 0) || d <= LeastSquares.AliasTolerance * orig) { aliased[j] = true; continue; }
                double ljj = Math.Sqrt(d);
                l[j, j] = ljj;
                for (int i = j + 1; i < p; i++)
                {
                    double s = a[i, j];
                    for (int k = 0; k < kept.Count; k++) { int kk = kept[k]; s -= l[i, kk] * l[j, kk]; }
                    l[i, j] = s / ljj;
                }
                kept.Add(j);
            }
            return aliased;
        }

        private sealed class DropIncompleteRows : IReadOnlyList<string[]>
        {
            private readonly IReadOnlyList<string[]> _rows;
            private readonly int _response, _group;
            private readonly IReadOnlyList<int> _slopes;
            public DropIncompleteRows(IReadOnlyList<string[]> rows, int response, int group, IReadOnlyList<int> slopes)
            {
                _rows = rows;
                _response = response;
                _group = group;
                _slopes = slopes;
            }
            public int Count => _rows.Count;
            public IEnumerator<string[]> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            public string[] this[int i]
            {
                get
                {
                    var row = _rows[i];
                    if (Complete(row)) return row;
                    var copy = new string[row.Length];
                    Array.Copy(row, copy, row.Length);
                    if (_response < copy.Length) copy[_response] = "";
                    return copy;
                }
            }
            private bool Complete(string[] row)
            {
                if (_group >= row.Length || StatValue.IsMissing(row[_group])) return false;
                for (int s = 0; s < _slopes.Count; s++)
                {
                    int c = _slopes[s];
                    if (c >= row.Length || !StatValue.TryNumber(row[c], out _)) return false;
                }
                return true;
            }
        }
    }

    /// <summary>그룹별 충분통계. 행 단위 설계행렬은 누적 후 버린다.</summary>
    internal sealed class MixedDesign
    {
        public int P, Q, Groups, N;
        public int[] Size = Array.Empty<int>();
        public double[] XtX = Array.Empty<double>();
        public double[] Xty = Array.Empty<double>();
        public double[] Yty = Array.Empty<double>();
        public double[] ZtZ = Array.Empty<double>();
        public double[] ZtX = Array.Empty<double>();
        public double[] Zty = Array.Empty<double>();
        public string[] FixedNames = Array.Empty<string>();
        public string[] RandomNames = Array.Empty<string>();
        public long RowsRead, RowsUsed, RowsDropped;

        public static MixedDesign Accumulate(double[,] x, double[] y, int[] group, int groupCount, double[,]? slopes, CancellationToken ct, long budgetBytes)
        {
            int n = y.Length;
            int p = x.GetLength(1);
            int slopeCount = slopes?.GetLength(1) ?? 0;
            int q = 1 + slopeCount;
            long bytes = (long)groupCount * (p * (long)p + p + 1 + q * (long)q + q * (long)p + q) * 8L;
            if (bytes > budgetBytes) throw new AnalysisMemoryLimitException();
            var d = new MixedDesign
            {
                P = p, Q = q, Groups = groupCount, N = n,
                Size = new int[groupCount],
                XtX = new double[groupCount * p * p],
                Xty = new double[groupCount * p],
                Yty = new double[groupCount],
                ZtZ = new double[groupCount * q * q],
                ZtX = new double[groupCount * q * p],
                Zty = new double[groupCount * q],
            };
            var z = new double[q];
            var xr = new double[p];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                int g = group[i];
                d.Size[g]++;
                for (int c = 0; c < p; c++) xr[c] = x[i, c];
                z[0] = 1;
                for (int s = 0; s < slopeCount; s++) z[s + 1] = slopes![i, s];
                d.AddOuter(g, xr, y[i], z);
            }
            return d;
        }

        /// <summary>임의효과 설계 Z를 호출자가 전부 준다(절편 열을 자동으로 붙이지 않음). NLMM 선형화 단계.</summary>
        internal static MixedDesign AccumulateExplicit(double[,] x, double[] y, int[] group, int groupCount, double[,] z, CancellationToken ct)
        {
            int n = y.Length;
            int p = x.GetLength(1);
            int q = z.GetLength(1);
            var d = new MixedDesign
            {
                P = p, Q = q, Groups = groupCount, N = n,
                Size = new int[groupCount],
                XtX = new double[groupCount * p * p],
                Xty = new double[groupCount * p],
                Yty = new double[groupCount],
                ZtZ = new double[groupCount * q * q],
                ZtX = new double[groupCount * q * p],
                Zty = new double[groupCount * q],
                RowsUsed = n, RowsRead = n,
            };
            var xr = new double[p];
            var zr = new double[q];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                int g = group[i];
                d.Size[g]++;
                for (int c = 0; c < p; c++) xr[c] = x[i, c];
                for (int c = 0; c < q; c++) zr[c] = z[i, c];
                d.AddOuter(g, xr, y[i], zr);
            }
            return d;
        }

        private void AddOuter(int g, double[] x, double y, double[] z)
        {
            int p = P, q = Q;
            int xtx = g * p * p, xty = g * p, ztz = g * q * q, ztx = g * q * p, zty = g * q;
            Yty[g] += y * y;
            for (int i = 0; i < p; i++)
            {
                Xty[xty + i] += x[i] * y;
                for (int j = 0; j <= i; j++)
                {
                    double v = x[i] * x[j];
                    XtX[xtx + i * p + j] += v;
                    if (i != j) XtX[xtx + j * p + i] += v;
                }
            }
            for (int i = 0; i < q; i++)
            {
                Zty[zty + i] += z[i] * y;
                for (int j = 0; j < p; j++) ZtX[ztx + i * p + j] += z[i] * x[j];
                for (int j = 0; j <= i; j++)
                {
                    double v = z[i] * z[j];
                    ZtZ[ztz + i * q + j] += v;
                    if (i != j) ZtZ[ztz + j * q + i] += v;
                }
            }
        }
    }

    internal readonly struct MixedSolution
    {
        public double[] Theta { get; init; }
        public double[,] Lambda { get; init; }
        public double[] Beta { get; init; }
        public double Scale { get; init; }
        public double LogLikelihood { get; init; }
        public double MlLogLikelihood { get; init; }
        public double[,] RandomCovariance { get; init; }
        public double[,] UnscaledCovariance { get; init; }
        public double[] ParameterVector { get; init; }
        public double[] ParameterStandardErrors { get; init; }
        public bool Converged { get; init; }
        public bool Singular { get; init; }
        public bool InformationPositiveDefinite { get; init; }
        public int Iterations { get; init; }

        public MixedModelResult ToResult(MixedDesign d, bool reml)
        {
            double zcrit = Dist.NormalQuantile(0.975);
            var coefs = new MixedCoefficient[d.P];
            var se = ParameterStandardErrors;
            for (int j = 0; j < d.P; j++)
            {
                double est = Beta[j];
                double s = se[j];
                double z = s > 0 && double.IsFinite(s) ? est / s : double.NaN;
                coefs[j] = new MixedCoefficient(
                    d.FixedNames.Length == d.P ? d.FixedNames[j] : "b" + j.ToString(CultureInfo.InvariantCulture),
                    est, s, z,
                    Dist.NormalTwoSided(z),
                    double.IsFinite(z) ? est - zcrit * s : double.NaN,
                    double.IsFinite(z) ? est + zcrit * s : double.NaN,
                    false);
            }
            int min = d.Size.Min(), max = d.Size.Max();
            double den = d.Q == 1 ? RandomCovariance[0, 0] + Scale : 0;
            double? icc = d.Q == 1 && den > 0 && double.IsFinite(den) ? RandomCovariance[0, 0] / den : null;
            return new MixedModelResult
            {
                Coefficients = coefs,
                RandomCovariance = RandomCovariance,
                RandomEffectNames = d.RandomNames,
                Scale = Scale,
                LogLikelihood = LogLikelihood,
                MlLogLikelihood = MlLogLikelihood,
                Aic = -2 * MlLogLikelihood + 2 * (d.P + d.Q * (d.Q + 1) / 2 + 1),
                Bic = -2 * MlLogLikelihood + Math.Log(d.N) * (d.P + d.Q * (d.Q + 1) / 2 + 1),
                Icc = icc,
                GroupCount = d.Groups,
                MinGroupSize = min,
                MaxGroupSize = max,
                RowsRead = d.RowsRead,
                RowsUsed = d.RowsUsed,
                RowsDropped = d.RowsDropped,
                Reml = reml,
                Converged = Converged,
                Singular = Singular,
                InformationPositiveDefinite = InformationPositiveDefinite,
                Iterations = Iterations,
                ParameterVector = ParameterVector,
                ParameterStandardErrors = ParameterStandardErrors,
            };
        }
    }

    /// <summary>프로파일 우도·L-BFGS·관측 정보. NLMM 선형화 단계도 이 엔진을 쓴다.</summary>
    internal static class MixedModelEngine
    {
        private const double LogTwoPi = 1.8378770664093453;
        private const int LbfgsCorrections = 7;
        private const int LbfgsMaxIts = 80;

        internal static MixedSolution Fit(MixedDesign d, bool reml, CancellationToken ct, double[]? start = null)
        {
            if (d.Groups < 1) throw new DesignMatrixException("A mixed model needs at least one group.");
            if (reml && d.N <= d.P)
                throw new DesignMatrixException("REML is not defined when the number of rows does not exceed the number of fixed effects.");
            var eval = new ProfileEvaluator(d, reml);
            int k = d.Q * (d.Q + 1) / 2;
            var starts = Starts(d.Q, k, start);
            double bestLl = double.NegativeInfinity;
            double[]? bestTheta = null;
            int bestIters = 0;
            bool anyConverged = false;
            foreach (var s0 in starts)
            {
                ct.ThrowIfCancellationRequested();
                var (theta, iters, term) = RunLbfgs(eval, s0, ct);
                NewtonPolish(eval, theta, ct);
                if (!eval.TryEvaluate(theta, out double ll, null)) continue;
                bool conv = term is 1 or 2 or 4 || GradientNorm(eval, theta) < 1e-5 * (1 + Math.Abs(ll));
                if (ll > bestLl)
                {
                    bestLl = ll;
                    bestTheta = (double[])theta.Clone();
                    bestIters = iters;
                    anyConverged = conv;
                }
            }
            if (bestTheta is null)
                throw new InvalidOperationException("The mixed-model likelihood could not be evaluated. Check for collinear predictors or a grouping factor that does not vary.");

            NewtonPolish(eval, bestTheta, ct);
            if (!eval.TryEvaluate(bestTheta, out double llBest, null) || !double.IsFinite(llBest))
                throw new InvalidOperationException("The mixed-model likelihood is not finite at the best point found.");
            double gnorm = GradientNorm(eval, bestTheta);
            bool converged = anyConverged || gnorm < 1e-4 * (1 + Math.Abs(llBest));

            var packed = eval.Pack(bestTheta);
            var se = ObservedInformation(d, packed.Beta, packed.Unscaled, reml, ct, out bool infoPd);
            bool singular = IsSingular(packed.Unscaled, d.Q);
            var result = packed.ToSolution(d, reml, se, infoPd, converged, singular, bestIters);
            return result;
        }

        /// <summary>현재 적합의 그룹별 BLUP. b = Λ S⁻¹ Λ' Z' r.</summary>
        internal static double[][] RandomEffects(MixedDesign d, double[,] lambda, double[] beta)
        {
            int q = d.Q, p = d.P;
            var b = new double[d.Groups][];
            var s = new double[q, q];
            var rz = new double[q];
            var v = new double[q];
            for (int g = 0; g < d.Groups; g++)
            {
                FillS(d, g, lambda, s);
                if (!Dense.Cholesky(s, q, out _))
                {
                    b[g] = new double[q];
                    continue;
                }
                var zr = new double[q];
                for (int i = 0; i < q; i++)
                {
                    double acc = 0;
                    for (int j = 0; j < p; j++) acc += d.ZtX[(g * q + i) * p + j] * beta[j];
                    zr[i] = d.Zty[g * q + i] - acc;
                }
                for (int a = 0; a < q; a++)
                {
                    double t = 0;
                    for (int i = a; i < q; i++) t += lambda[i, a] * zr[i];
                    rz[a] = t;
                }
                Dense.SolveChol(s, q, rz, v);
                var bg = new double[q];
                for (int i = 0; i < q; i++)
                {
                    double t = 0;
                    for (int a = 0; a <= i; a++) t += lambda[i, a] * v[a];
                    bg[i] = t;
                }
                b[g] = bg;
            }
            return b;
        }

        private static List<double[]> Starts(int q, int k, double[]? warm)
        {
            var starts = new List<double[]>();
            if (warm is not null && warm.Length == k) starts.Add((double[])warm.Clone());
            foreach (double diag in new[] { 1.0, 0.1, 3.0, 0.01 })
            {
                var th = new double[k];
                int t = 0;
                for (int i = 0; i < q; i++)
                    for (int j = 0; j <= i; j++)
                        th[t++] = i == j ? diag : 0;
                starts.Add(th);
            }
            return starts;
        }

        private static (double[] Theta, int Iters, int Term) RunLbfgs(ProfileEvaluator eval, double[] start, CancellationToken ct)
        {
            int k = start.Length;
            var x = (double[])start.Clone();
            alglib.minlbfgscreate(k, Math.Min(LbfgsCorrections, k), x, out var state);
            alglib.minlbfgssetcond(state, 1e-8, 0, 1e-10, LbfgsMaxIts);
            int iters = 0;
            while (alglib.minlbfgsiteration(state))
            {
                if ((++iters & 15) == 0) ct.ThrowIfCancellationRequested();
                var inner = state.innerobj;
                if (inner.needfg)
                {
                    if (!eval.TryEvaluate(inner.x, out double ll, inner.g))
                    {
                        inner.f = Penalty(inner.x);
                        PenaltyGradient(inner.x, inner.g);
                    }
                    else
                    {
                        inner.f = -ll;
                        for (int i = 0; i < k; i++) inner.g[i] = -inner.g[i];
                    }
                }
                else if (inner.needf)
                {
                    inner.f = eval.TryEvaluate(inner.x, out double ll, null) ? -ll : Penalty(inner.x);
                }
            }
            alglib.minlbfgsresults(state, out var sol, out var rep);
            return (sol, rep.iterationscount, rep.terminationtype);
        }

        private static void NewtonPolish(ProfileEvaluator eval, double[] theta, CancellationToken ct)
        {
            int k = theta.Length;
            var g = new double[k];
            var step = new double[k];
            var trial = new double[k];
            for (int iter = 0; iter < 16; iter++)
            {
                ct.ThrowIfCancellationRequested();
                if (!eval.TryEvaluate(theta, out double ll, g)) return;
                double gnorm = 0;
                for (int i = 0; i < k; i++) gnorm += g[i] * g[i];
                if (Math.Sqrt(gnorm) < 1e-8 * (1 + Math.Abs(ll))) return;
                var h = HessianFromGradient(eval, theta, g);
                for (int i = 0; i < k; i++) step[i] = -g[i];
                if (!Dense.SolveSymmetric(h, k, step)) return;
                double alpha = 1;
                bool improved = false;
                for (int ls = 0; ls < 12; ls++)
                {
                    for (int i = 0; i < k; i++) trial[i] = theta[i] + alpha * step[i];
                    if (eval.TryEvaluate(trial, out double ll2, null) && ll2 > ll + 1e-12)
                    {
                        Array.Copy(trial, theta, k);
                        improved = true;
                        break;
                    }
                    alpha *= 0.5;
                }
                if (!improved) return;
            }
        }

        private static double[,] HessianFromGradient(ProfileEvaluator eval, double[] theta, double[] g0)
        {
            int k = theta.Length;
            var h = new double[k, k];
            var tp = (double[])theta.Clone();
            var tm = (double[])theta.Clone();
            var gp = new double[k];
            var gm = new double[k];
            for (int j = 0; j < k; j++)
            {
                double hj = 1e-7 * Math.Max(1.0, Math.Abs(theta[j]));
                tp[j] = theta[j] + hj;
                tm[j] = theta[j] - hj;
                bool okp = eval.TryEvaluate(tp, out _, gp);
                bool okm = eval.TryEvaluate(tm, out _, gm);
                tp[j] = theta[j];
                tm[j] = theta[j];
                for (int i = 0; i < k; i++)
                    h[i, j] = okp && okm ? (gp[i] - gm[i]) / (2 * hj) : 0;
            }
            for (int i = 0; i < k; i++)
                for (int j = i + 1; j < k; j++)
                {
                    double s = 0.5 * (h[i, j] + h[j, i]);
                    h[i, j] = h[j, i] = s;
                }
            return h;
        }

        private static double GradientNorm(ProfileEvaluator eval, double[] theta)
        {
            var g = new double[theta.Length];
            if (!eval.TryEvaluate(theta, out _, g)) return double.PositiveInfinity;
            double s = 0;
            for (int i = 0; i < g.Length; i++) s += g[i] * g[i];
            return Math.Sqrt(s);
        }

        private static double Penalty(double[] theta)
        {
            double s = 1e8;
            for (int i = 0; i < theta.Length; i++) s += theta[i] * theta[i];
            return s;
        }

        private static void PenaltyGradient(double[] theta, double[] g)
        {
            for (int i = 0; i < theta.Length; i++) g[i] = 2 * theta[i];
        }

        private static double[] ObservedInformation(MixedDesign d, double[] beta, double[,] gUnscaled, bool reml, CancellationToken ct, out bool positiveDefinite)
        {
            int p = d.P;
            int k2 = d.Q * (d.Q + 1) / 2;
            int m = p + k2;
            var pars = new double[m];
            for (int j = 0; j < p; j++) pars[j] = beta[j];
            int t = p;
            for (int i = 0; i < d.Q; i++)
                for (int j = 0; j <= i; j++)
                    pars[t++] = gUnscaled[i, j];
            double f0 = FullLogLik(d, pars, p, reml);
            var h = new double[m, m];
            var pp = new double[m];
            for (int i = 0; i < m; i++)
            {
                if ((i & 3) == 0) ct.ThrowIfCancellationRequested();
                double hi = 1e-5 * Math.Max(1.0, Math.Abs(pars[i]));
                for (int j = i; j < m; j++)
                {
                    double hj = 1e-5 * Math.Max(1.0, Math.Abs(pars[j]));
                    double val;
                    if (i == j)
                    {
                        Array.Copy(pars, pp, m);
                        pp[i] += hi;
                        double fp = FullLogLik(d, pp, p, reml);
                        pp[i] = pars[i] - hi;
                        double fm = FullLogLik(d, pp, p, reml);
                        val = (fp - 2 * f0 + fm) / (hi * hi);
                    }
                    else
                    {
                        double Acc(int si, int sj)
                        {
                            Array.Copy(pars, pp, m);
                            pp[i] += si * hi;
                            pp[j] += sj * hj;
                            return FullLogLik(d, pp, p, reml);
                        }
                        val = (Acc(1, 1) - Acc(1, -1) - Acc(-1, 1) + Acc(-1, -1)) / (4 * hi * hj);
                    }
                    h[i, j] = h[j, i] = val;
                }
            }
            var neg = new double[m, m];
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    neg[i, j] = -h[i, j];
            positiveDefinite = Dense.InvertSpd(neg, m);
            var se = new double[m];
            if (!positiveDefinite)
            {
                for (int i = 0; i < m; i++) se[i] = double.NaN;
                return se;
            }
            for (int i = 0; i < m; i++)
                se[i] = neg[i, i] > 0 ? Math.Sqrt(neg[i, i]) : double.NaN;
            return se;
        }

        /// <summary>β를 다시 프로파일하지 않는 로그우도(헤시안용). 공분산은 하삼각 vech.</summary>
        private static double FullLogLik(MixedDesign d, double[] pars, int p, bool reml)
        {
            int q = d.Q;
            var beta = new double[p];
            Array.Copy(pars, beta, p);
            var g = new double[q, q];
            int t = p;
            for (int i = 0; i < q; i++)
                for (int j = 0; j <= i; j++)
                {
                    g[i, j] = g[j, i] = pars[t++];
                }
            if (!Dense.CholeskyFactor(g, q, out var lambda)) return -1e300;
            var eval = new ProfileEvaluator(d, reml);
            return eval.LogLikGivenBeta(lambda, beta, out _, out _, out _);
        }

        private static bool IsSingular(double[,] g, int q)
        {
            var a = new double[q, q];
            for (int i = 0; i < q; i++)
                for (int j = 0; j < q; j++)
                    a[i, j] = g[i, j];
            // 대칭 고유값: 작은 q라 특성다항 대신 대각 우세 판정 + 촐레스키 실패.
            double scale = 0;
            for (int i = 0; i < q; i++) scale = Math.Max(scale, Math.Abs(a[i, i]));
            if (!(scale > 0)) return true;
            if (!Dense.Cholesky(a, q, out _)) return true;
            double minDiag = double.PositiveInfinity;
            for (int i = 0; i < q; i++) minDiag = Math.Min(minDiag, a[i, i]);
            // 촐레스키 대각이 척도 대비 극히 작으면 특이.
            return minDiag < 1e-8 * Math.Max(1.0, scale);
        }

        private static void FillS(MixedDesign d, int g, double[,] lambda, double[,] s)
        {
            int q = d.Q;
            // S = I + Λ' Z'Z Λ
            var az = new double[q, q];
            for (int i = 0; i < q; i++)
                for (int j = 0; j < q; j++)
                    az[i, j] = d.ZtZ[(g * q + i) * q + j];
            var lz = new double[q, q];
            for (int a = 0; a < q; a++)
                for (int j = 0; j < q; j++)
                {
                    double t = 0;
                    for (int i = a; i < q; i++) t += lambda[i, a] * az[i, j];
                    lz[a, j] = t;
                }
            for (int a = 0; a < q; a++)
                for (int b = 0; b < q; b++)
                {
                    double t = a == b ? 1 : 0;
                    for (int j = 0; j < q; j++) t += lz[a, j] * lambda[j, b];
                    s[a, b] = t;
                }
        }
    }

    /// <summary>한 설계에 대한 프로파일 로그우도와 촐레스키 매개화 점수.</summary>
    internal sealed class ProfileEvaluator
    {
        private readonly MixedDesign _d;
        private readonly bool _reml;
        private const double LogTwoPi = 1.8378770664093453;

        public ProfileEvaluator(MixedDesign d, bool reml) { _d = d; _reml = reml; }

        public bool TryEvaluate(double[] theta, out double ll, double[]? score)
        {
            ll = double.NaN;
            var lambda = Unpack(theta);
            if (!TryProfile(lambda, null, out ll, out _, out _, out _, score)) return false;
            return double.IsFinite(ll);
        }

        public double LogLikGivenBeta(double[,] lambda, double[] beta, out double qf, out double sumLogDet, out double logDetA)
        {
            if (!TryProfile(lambda, beta, out double ll, out qf, out sumLogDet, out logDetA, null)) return -1e300;
            return ll;
        }

        public PackedFit Pack(double[] theta)
        {
            var lambda = Unpack(theta);
            if (!TryProfile(lambda, null, out double ll, out double qf, out _, out _, null))
                throw new InvalidOperationException("Profile likelihood failed at the reported optimum.");
            var beta = LastBeta!;
            int fac = _reml ? _d.N - _d.P : _d.N;
            double scale = qf / fac;
            int q = _d.Q;
            var unscaled = new double[q, q];
            var cov = new double[q, q];
            for (int i = 0; i < q; i++)
                for (int j = 0; j < q; j++)
                {
                    double t = 0;
                    for (int a = 0; a <= Math.Min(i, j); a++) t += lambda[i, a] * lambda[j, a];
                    unscaled[i, j] = t;
                    cov[i, j] = scale * t;
                }
            // 대각이 음수인 행의 부호를 뒤집어 보고용 Λ를 유일하게 만든다(G는 불변).
            for (int i = 0; i < q; i++)
                if (lambda[i, i] < 0)
                    for (int a = 0; a <= i; a++) lambda[i, a] = -lambda[i, a];
            double ml = LogLikFormula(qf, LastSumLogDet, LastLogDetA, false);
            return new PackedFit(beta, lambda, scale, ll, ml, cov, unscaled);
        }

        private double[]? LastBeta;
        private double LastSumLogDet, LastLogDetA;

        private bool TryProfile(double[,] lambda, double[]? betaFixed, out double ll, out double qf, out double sumLogDet, out double logDetA, double[]? score)
        {
            ll = double.NaN; qf = 0; sumLogDet = 0; logDetA = 0;
            var d = _d;
            int p = d.P, q = d.Q, groups = d.Groups;
            var A = new double[p, p];
            var b = new double[p];
            for (int g = 0; g < groups; g++)
            {
                if (!GroupGlm(g, lambda, out var xtvix, out var xtviy, out double ld)) return false;
                sumLogDet += ld;
                for (int i = 0; i < p; i++)
                {
                    b[i] += xtviy[i];
                    for (int j = 0; j < p; j++) A[i, j] += xtvix[i, j];
                }
            }
            double[] beta;
            if (betaFixed is null)
            {
                var rhs = (double[])b.Clone();
                var factor = (double[,])A.Clone();
                if (!Dense.SolveSpd(factor, p, rhs)) return false;
                beta = rhs;
            }
            else beta = betaFixed;
            LastBeta = (double[])beta.Clone();

            if (!Dense.LogDetSpd(A, p, out logDetA)) return false;
            LastSumLogDet = sumLogDet;
            LastLogDetA = logDetA;

            int k2 = q * (q + 1) / 2;
            var dlv = score is null ? null : new double[k2];
            var rvavr = score is null ? null : new double[k2];
            var xtax = score is null ? null : new double[k2][,];
            if (xtax is not null)
                for (int t = 0; t < k2; t++) xtax[t] = new double[p, p];

            qf = 0;
            for (int g = 0; g < groups; g++)
            {
                if (!GroupQuadratic(g, lambda, beta, out double quad, out var m, out var bigP, out var gv)) return false;
                qf += quad;
                if (dlv is null || m is null || bigP is null || gv is null) continue;
                int t = 0;
                for (int j1 = 0; j1 < q; j1++)
                    for (int j2 = 0; j2 <= j1; j2++, t++)
                    {
                        if (j1 == j2)
                        {
                            dlv[t] += m[j1, j1];
                            rvavr![t] += gv[j1] * gv[j1];
                            for (int a = 0; a < p; a++)
                                for (int c = 0; c < p; c++)
                                    xtax![t][a, c] += bigP[j1, a] * bigP[j1, c];
                        }
                        else
                        {
                            dlv[t] += 2 * m[j1, j2];
                            rvavr![t] += 2 * gv[j1] * gv[j2];
                            for (int a = 0; a < p; a++)
                                for (int c = 0; c < p; c++)
                                    xtax![t][a, c] += bigP[j1, a] * bigP[j2, c] + bigP[j2, a] * bigP[j1, c];
                        }
                    }
            }
            if (!(qf > 0) || !double.IsFinite(qf)) return false;
            ll = LogLikFormula(qf, sumLogDet, logDetA, _reml);
            if (score is null) return true;

            int fac = _reml ? d.N - p : d.N;
            var scoreVech = new double[k2];
            for (int t = 0; t < k2; t++)
                scoreVech[t] = -0.5 * dlv![t] + 0.5 * fac * rvavr![t] / qf;
            if (_reml)
            {
                var inv = (double[,])A.Clone();
                if (!Dense.InvertSpd(inv, p)) return false;
                for (int t = 0; t < k2; t++)
                {
                    double fro = 0;
                    for (int a = 0; a < p; a++)
                        for (int c = 0; c < p; c++)
                            fro += inv[a, c] * xtax![t][a, c];
                    scoreVech[t] += 0.5 * fro;
                }
            }
            ApplyCholeskyJacobian(lambda, scoreVech, score);
            return true;
        }

        private double LogLikFormula(double qf, double sumLogDet, double logDetA, bool reml)
        {
            int n = _d.N, p = _d.P;
            int fac = reml ? n - p : n;
            double ll = -0.5 * sumLogDet - fac * Math.Log(qf) / 2 - fac * LogTwoPi / 2 + fac * Math.Log(fac) / 2 - fac / 2.0;
            if (reml) ll -= 0.5 * logDetA;
            return ll;
        }

        private bool GroupGlm(int g, double[,] lambda, out double[,] xtvix, out double[] xtviy, out double logDet)
        {
            var d = _d;
            int p = d.P, q = d.Q;
            xtvix = new double[p, p];
            xtviy = new double[p];
            logDet = 0;
            if (!BuildS(g, lambda, out var s, out var lztx, out var lzty)) return false;
            if (!Dense.Cholesky(s, q, out logDet)) return false;
            // W = S^{-1} T, T = Λ' Z'X = lztx; v = S^{-1} u
            var w = new double[q, p];
            var v = new double[q];
            if (!Dense.SolveCholMulti(s, q, lztx, p, w)) return false;
            if (!Dense.SolveChol(s, q, lzty, v)) return false;
            int xtx = g * p * p, xty = g * p;
            for (int i = 0; i < p; i++)
            {
                double acc = d.Xty[xty + i];
                for (int a = 0; a < q; a++) acc -= lztx[a, i] * v[a];
                xtviy[i] = acc;
                for (int j = 0; j < p; j++)
                {
                    double t = d.XtX[xtx + i * p + j];
                    for (int a = 0; a < q; a++) t -= lztx[a, i] * w[a, j];
                    xtvix[i, j] = t;
                }
            }
            return true;
        }

        private bool GroupQuadratic(int g, double[,] lambda, double[] beta, out double quad, out double[,]? m, out double[,]? bigP, out double[]? gv)
        {
            var d = _d;
            int p = d.P, q = d.Q;
            quad = 0; m = null; bigP = null; gv = null;
            if (!BuildS(g, lambda, out var s, out var lztx, out var lzty)) return false;
            if (!Dense.Cholesky(s, q, out _)) return false;
            var rz = new double[q];
            for (int a = 0; a < q; a++)
            {
                double t = lzty[a];
                for (int j = 0; j < p; j++) t -= lztx[a, j] * beta[j];
                rz[a] = t;
            }
            var solved = new double[q];
            if (!Dense.SolveChol(s, q, rz, solved)) return false;
            double betaXty = 0, betaXtX = 0;
            int xtx = g * p * p, xty = g * p;
            for (int i = 0; i < p; i++)
            {
                betaXty += beta[i] * d.Xty[xty + i];
                double row = 0;
                for (int j = 0; j < p; j++) row += d.XtX[xtx + i * p + j] * beta[j];
                betaXtX += beta[i] * row;
            }
            double rzS = 0;
            for (int a = 0; a < q; a++) rzS += rz[a] * solved[a];
            quad = d.Yty[g] - 2 * betaXty + betaXtX - rzS;

            var azL = new double[q, q];
            for (int i = 0; i < q; i++)
                for (int b = 0; b < q; b++)
                {
                    double t = 0;
                    for (int k = b; k < q; k++) t += d.ZtZ[(g * q + i) * q + k] * lambda[k, b];
                    azL[i, b] = t;
                }
            // S^{-1} (Λ' AZ) = S^{-1} (AZ Λ)'
            var ltAz = new double[q, q];
            for (int a = 0; a < q; a++)
                for (int i = 0; i < q; i++)
                    ltAz[a, i] = azL[i, a];
            var sInvLtAz = new double[q, q];
            if (!Dense.SolveCholMulti(s, q, ltAz, q, sInvLtAz)) return false;
            m = new double[q, q];
            for (int i = 0; i < q; i++)
                for (int j = 0; j < q; j++)
                {
                    double t = d.ZtZ[(g * q + i) * q + j];
                    for (int a = 0; a < q; a++) t -= azL[i, a] * sInvLtAz[a, j];
                    m[i, j] = t;
                }
            // W = S^{-1} Λ' Z'X, P = Z'X - AZ Λ W
            var w = new double[q, p];
            if (!Dense.SolveCholMulti(s, q, lztx, p, w)) return false;
            bigP = new double[q, p];
            for (int i = 0; i < q; i++)
                for (int j = 0; j < p; j++)
                {
                    double t = d.ZtX[(g * q + i) * p + j];
                    for (int a = 0; a < q; a++) t -= azL[i, a] * w[a, j];
                    bigP[i, j] = t;
                }
            gv = new double[q];
            var zr = new double[q];
            for (int i = 0; i < q; i++)
            {
                double acc = 0;
                for (int j = 0; j < p; j++) acc += d.ZtX[(g * q + i) * p + j] * beta[j];
                zr[i] = d.Zty[g * q + i] - acc;
            }
            for (int i = 0; i < q; i++)
            {
                double t = zr[i];
                for (int a = 0; a < q; a++) t -= azL[i, a] * solved[a];
                gv[i] = t;
            }
            return true;
        }

        private bool BuildS(int g, double[,] lambda, out double[,] s, out double[,] lztx, out double[] lzty)
        {
            var d = _d;
            int p = d.P, q = d.Q;
            s = new double[q, q];
            lztx = new double[q, p];
            lzty = new double[q];
            // Λ' Z'Z Λ + I, Λ' Z'X, Λ' Z'y
            for (int a = 0; a < q; a++)
            {
                double uy = 0;
                for (int i = a; i < q; i++) uy += lambda[i, a] * d.Zty[g * q + i];
                lzty[a] = uy;
                for (int j = 0; j < p; j++)
                {
                    double t = 0;
                    for (int i = a; i < q; i++) t += lambda[i, a] * d.ZtX[(g * q + i) * p + j];
                    lztx[a, j] = t;
                }
            }
            // (Λ' Z'Z)_a,k = Σ_{i>=a} Λ_ia ZtZ_ik
            var ltZ = new double[q, q];
            for (int a = 0; a < q; a++)
                for (int k = 0; k < q; k++)
                {
                    double t = 0;
                    for (int i = a; i < q; i++) t += lambda[i, a] * d.ZtZ[(g * q + i) * q + k];
                    ltZ[a, k] = t;
                }
            for (int a = 0; a < q; a++)
                for (int b = 0; b < q; b++)
                {
                    double t = a == b ? 1.0 : 0.0;
                    for (int k = b; k < q; k++) t += ltZ[a, k] * lambda[k, b];
                    s[a, b] = t;
                }
            return true;
        }

        private double[,] Unpack(double[] theta)
        {
            int q = _d.Q;
            var l = new double[q, q];
            int t = 0;
            for (int i = 0; i < q; i++)
                for (int j = 0; j <= i; j++)
                    l[i, j] = theta[t++];
            return l;
        }

        private static void ApplyCholeskyJacobian(double[,] lambda, double[] scoreVech, double[] score)
        {
            int q = lambda.GetLength(0);
            int k2 = scoreVech.Length;
            // J_{t,k} = ∂ vech(G)_t / ∂ θ_k, G = Λ Λ', θ는 하삼각.
            // score_chol = J' score_vech
            for (int k = 0; k < k2; k++) score[k] = 0;
            int col = 0;
            for (int a = 0; a < q; a++)
                for (int b = 0; b <= a; b++, col++)
                {
                    int row = 0;
                    for (int i = 0; i < q; i++)
                        for (int j = 0; j <= i; j++, row++)
                        {
                            // ∂G_ij / ∂Λ_ab = δ_ia Λ_jb + δ_ja Λ_ib
                            double deriv = 0;
                            if (i == a) deriv += j >= b ? lambda[j, b] : 0;
                            if (j == a) deriv += i >= b ? lambda[i, b] : 0;
                            score[col] += deriv * scoreVech[row];
                        }
                }
        }
    }

    internal readonly struct PackedFit
    {
        public double[] Beta { get; }
        public double[,] Lambda { get; }
        public double Scale { get; }
        public double LogLikelihood { get; }
        public double MlLogLikelihood { get; }
        public double[,] Covariance { get; }
        public double[,] Unscaled { get; }
        public PackedFit(double[] beta, double[,] lambda, double scale, double ll, double ml, double[,] cov, double[,] unscaled)
        {
            Beta = beta; Lambda = lambda; Scale = scale; LogLikelihood = ll; MlLogLikelihood = ml; Covariance = cov; Unscaled = unscaled;
        }

        public MixedSolution ToSolution(MixedDesign d, bool reml, double[] se, bool infoPd, bool converged, bool singular, int iters)
        {
            int p = d.P, q = d.Q;
            int k2 = q * (q + 1) / 2;
            var pars = new double[p + k2];
            for (int j = 0; j < p; j++) pars[j] = Beta[j];
            int t = p;
            for (int i = 0; i < q; i++)
                for (int j = 0; j <= i; j++)
                    pars[t++] = Unscaled[i, j];
            // ToResult이 Reml을 false로 두므로 여기서 결과 객체를 직접 만들지 않고 MixedSolution에 담는다.
            return new MixedSolution
            {
                Theta = PackTheta(Lambda, q),
                Lambda = Lambda,
                Beta = Beta,
                Scale = Scale,
                LogLikelihood = LogLikelihood,
                MlLogLikelihood = MlLogLikelihood,
                RandomCovariance = Covariance,
                UnscaledCovariance = Unscaled,
                ParameterVector = pars,
                ParameterStandardErrors = se,
                Converged = converged,
                Singular = singular,
                InformationPositiveDefinite = infoPd,
                Iterations = iters,
            };
        }

        private static double[] PackTheta(double[,] lambda, int q)
        {
            var th = new double[q * (q + 1) / 2];
            int t = 0;
            for (int i = 0; i < q; i++)
                for (int j = 0; j <= i; j++)
                    th[t++] = lambda[i, j];
            return th;
        }
    }

    /// <summary>작은 밀집 행렬. q·p가 수십 이하인 혼합모형 내부 연산.</summary>
    internal static class Dense
    {
        public static bool Cholesky(double[,] a, int n, out double logDet)
        {
            logDet = 0;
            for (int j = 0; j < n; j++)
            {
                double d = a[j, j];
                for (int k = 0; k < j; k++) d -= a[j, k] * a[j, k];
                if (!(d > 0) || !double.IsFinite(d)) return false;
                double ljj = Math.Sqrt(d);
                a[j, j] = ljj;
                logDet += 2 * Math.Log(ljj);
                for (int i = j + 1; i < n; i++)
                {
                    double s = a[i, j];
                    for (int k = 0; k < j; k++) s -= a[i, k] * a[j, k];
                    a[i, j] = s / ljj;
                }
            }
            return true;
        }

        /// <summary>G의 하삼각 촐레스키를 새 행렬로. 실패하면 false.</summary>
        public static bool CholeskyFactor(double[,] g, int n, out double[,] lambda)
        {
            lambda = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    lambda[i, j] = g[i, j];
            if (!Cholesky(lambda, n, out _)) return false;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    lambda[i, j] = 0;
            return true;
        }

        public static bool SolveChol(double[,] l, int n, double[] b, double[] x)
        {
            // L y = b, L' x = y. l의 하삼각만 사용.
            var y = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = b[i];
                for (int k = 0; k < i; k++) s -= l[i, k] * y[k];
                if (l[i, i] == 0) return false;
                y[i] = s / l[i, i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = y[i];
                for (int k = i + 1; k < n; k++) s -= l[k, i] * x[k];
                x[i] = s / l[i, i];
            }
            return true;
        }

        /// <summary>S X = B, B는 n×nrhs를 [row, col]로, S는 이미 촐레스키된 하삼각.</summary>
        public static bool SolveCholMulti(double[,] l, int n, double[,] b, int nrhs, double[,] x)
        {
            var col = new double[n];
            var sol = new double[n];
            for (int j = 0; j < nrhs; j++)
            {
                for (int i = 0; i < n; i++) col[i] = b[i, j];
                if (!SolveChol(l, n, col, sol)) return false;
                for (int i = 0; i < n; i++) x[i, j] = sol[i];
            }
            return true;
        }

        public static bool SolveSpd(double[,] a, int n, double[] rhs)
        {
            if (!Cholesky(a, n, out _)) return false;
            var x = new double[n];
            if (!SolveChol(a, n, rhs, x)) return false;
            Array.Copy(x, rhs, n);
            return true;
        }

        public static bool LogDetSpd(double[,] a, int n, out double logDet)
        {
            var c = (double[,])a.Clone();
            return Cholesky(c, n, out logDet);
        }

        public static bool InvertSpd(double[,] a, int n)
        {
            if (n == 0) return true;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (!double.IsFinite(a[i, j])) return false;
            if (!alglib.spdmatrixcholesky(a, n, false)) return false;
            alglib.spdmatrixcholeskyinverse(a, n, false, out var rep);
            if (rep.terminationtype <= 0) return false;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    a[i, j] = a[j, i];
            return true;
        }

        /// <summary>대칭 연립 H x = rhs. rhs를 해로 덮어쓴다. H는 파괴된다.</summary>
        public static bool SolveSymmetric(double[,] h, int n, double[] rhs)
        {
            // 부호 불명 — 부분 피벗 가우스. 대칭을 쓰지 않는다.
            var a = new double[n, n + 1];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) a[i, j] = h[i, j];
                a[i, n] = rhs[i];
            }
            for (int k = 0; k < n; k++)
            {
                int piv = k;
                double best = Math.Abs(a[k, k]);
                for (int i = k + 1; i < n; i++)
                    if (Math.Abs(a[i, k]) > best) { best = Math.Abs(a[i, k]); piv = i; }
                if (!(best > 1e-14)) return false;
                if (piv != k)
                    for (int j = k; j <= n; j++)
                        (a[k, j], a[piv, j]) = (a[piv, j], a[k, j]);
                double div = a[k, k];
                for (int j = k; j <= n; j++) a[k, j] /= div;
                for (int i = 0; i < n; i++)
                {
                    if (i == k) continue;
                    double f = a[i, k];
                    if (f == 0) continue;
                    for (int j = k; j <= n; j++) a[i, j] -= f * a[k, j];
                }
            }
            for (int i = 0; i < n; i++) rhs[i] = a[i, n];
            return true;
        }
    }
}
