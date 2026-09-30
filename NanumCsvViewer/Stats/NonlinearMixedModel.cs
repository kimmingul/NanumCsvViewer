using System.Globalization;

namespace NanumCsvViewer.Stats
{
    // 비선형 혼합모형. 내장 평균함수에 그룹 임의효과를 두고 Lindstrom–Bates(1990)의
    // 벌점 비선형최소제곱 / 선형혼합모형 교대(PNLS/LME)로 추정한다.
    // 안쪽 LMM은 ML 프로파일 우도(MixedModelEngine, ALGLIB L-BFGS). 임의효과가 없으면
    // ALGLIB Levenberg–Marquardt 비선형최소제곱으로 퇴화하고, 그 적합과 같아야 한다.
    // 표준오차는 최종 선형화 LMM의 고정효과 표준오차(선형화 점에 조건부인 근사)다.
    // 임의효과가 없으면 σ²(J'J)⁻¹ (Gauss–Newton).

    public enum NonlinearMean
    {
        ExponentialDecay,
        LogisticGrowth,
        MichaelisMenten,
        Emax,
    }

    public sealed class NonlinearMixedResult
    {
        public required NonlinearMean Model { get; init; }
        public required string Formula { get; init; }
        public required IReadOnlyList<string> ParameterNames { get; init; }
        public required double[] Estimates { get; init; }
        public required double[] StdErrors { get; init; }
        /// <summary>모수별 임의효과 여부. 순서 = ParameterNames.</summary>
        public required bool[] Random { get; init; }
        /// <summary>임의효과 공분산(추정된 척도). 임의효과가 없으면 0×0.</summary>
        public required double[,] RandomCovariance { get; init; }
        public required IReadOnlyList<string> RandomEffectNames { get; init; }
        public required double Scale { get; init; }
        public required bool Converged { get; init; }
        public required bool Singular { get; init; }
        public required int Iterations { get; init; }
        /// <summary>표준오차의 근사 방법. 결과 창에 그대로 적는다.</summary>
        public required string StandardErrorMethod { get; init; }
        public required int GroupCount { get; init; }
        public required int MinGroupSize { get; init; }
        public required int MaxGroupSize { get; init; }
        public required long RowsRead { get; init; }
        public required long RowsUsed { get; init; }
        public required long RowsDropped { get; init; }
    }

    public static class NonlinearMixedModel
    {
        public const string SeNls =
            "Approximate SEs from nonlinear least squares: σ² (J′J)⁻¹ at the solution (Gauss–Newton / ALGLIB Levenberg–Marquardt). No random effects were estimated.";
        public const string SeLindstromBates =
            "Approximate SEs from the final Lindstrom–Bates linear mixed-model step (ML), conditional on the linearization point. They are not the exact observed information of the nonlinear mixed-model likelihood.";

        public static int ParameterCount(NonlinearMean model) => model switch
        {
            NonlinearMean.ExponentialDecay => 2,
            NonlinearMean.LogisticGrowth => 3,
            NonlinearMean.MichaelisMenten => 2,
            NonlinearMean.Emax => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };

        public static string[] ParameterNames(NonlinearMean model) => model switch
        {
            NonlinearMean.ExponentialDecay => new[] { "a", "b" },
            NonlinearMean.LogisticGrowth => new[] { "a", "b", "c" },
            NonlinearMean.MichaelisMenten => new[] { "a", "b" },
            NonlinearMean.Emax => new[] { "a", "b", "c" },
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };

        public static string Formula(NonlinearMean model) => model switch
        {
            NonlinearMean.ExponentialDecay => "y = a · exp(−b · x)",
            NonlinearMean.LogisticGrowth => "y = a / (1 + exp((b − x) / c))",
            NonlinearMean.MichaelisMenten => "y = a · x / (b + x)",
            NonlinearMean.Emax => "y = a + b · x / (c + x)",
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };

        public static NonlinearMixedResult Fit(
            IReadOnlyList<string[]> rows,
            IReadOnlyList<string> headers,
            int responseColumn,
            int predictorColumn,
            int groupColumn,
            NonlinearMean model,
            IReadOnlyList<int> randomParameters,
            CancellationToken cancellation = default)
        {
            if (responseColumn < 0 || responseColumn >= headers.Count
                || predictorColumn < 0 || predictorColumn >= headers.Count
                || groupColumn < 0 || groupColumn >= headers.Count)
                throw new DesignMatrixException("A selected column is not in the table.");
            if (responseColumn == predictorColumn)
                throw new DesignMatrixException("The response and the predictor must be different columns.");
            int k = ParameterCount(model);
            var random = new bool[k];
            foreach (int j in randomParameters)
            {
                if (j < 0 || j >= k) throw new DesignMatrixException("A random-effect parameter index is outside the model.");
                random[j] = true;
            }

            var y = new List<double>();
            var x = new List<double>();
            var labels = new List<string>();
            long dropped = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                var row = rows[i];
                if (responseColumn >= row.Length || predictorColumn >= row.Length || groupColumn >= row.Length
                    || !StatValue.TryNumber(row[responseColumn], out double yv)
                    || !StatValue.TryNumber(row[predictorColumn], out double xv)
                    || StatValue.IsMissing(row[groupColumn]))
                {
                    dropped++;
                    continue;
                }
                y.Add(yv);
                x.Add(xv);
                labels.Add(row[groupColumn].Trim());
            }
            if (y.Count == 0)
                throw new DesignMatrixException("No complete rows: every row has a missing or non-numeric value in the model columns.");

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var codes = new int[y.Count];
            for (int i = 0; i < y.Count; i++)
            {
                if (!map.TryGetValue(labels[i], out int code))
                {
                    code = map.Count;
                    map[labels[i]] = code;
                }
                codes[i] = code;
            }
            var result = FitNumeric(y.ToArray(), x.ToArray(), codes, map.Count, model, random, cancellation);
            return new NonlinearMixedResult
            {
                Model = result.Model,
                Formula = result.Formula,
                ParameterNames = result.ParameterNames,
                Estimates = result.Estimates,
                StdErrors = result.StdErrors,
                Random = result.Random,
                RandomCovariance = result.RandomCovariance,
                RandomEffectNames = result.RandomEffectNames,
                Scale = result.Scale,
                Converged = result.Converged,
                Singular = result.Singular,
                Iterations = result.Iterations,
                StandardErrorMethod = result.StandardErrorMethod,
                GroupCount = result.GroupCount,
                MinGroupSize = result.MinGroupSize,
                MaxGroupSize = result.MaxGroupSize,
                RowsRead = rows.Count,
                RowsUsed = y.Count,
                RowsDropped = dropped,
            };
        }

        public static NonlinearMixedResult FitNumeric(
            double[] y, double[] x, int[] group, int groupCount, NonlinearMean model, bool[] random, CancellationToken cancellation = default)
        {
            if (y.Length != x.Length || y.Length != group.Length)
                throw new ArgumentException("y, x, and group must have the same length.");
            int k = ParameterCount(model);
            if (random.Length != k) throw new ArgumentException("Random-effect flags must match the parameter count.", nameof(random));
            if (y.Length <= k)
                throw new DesignMatrixException("Not enough complete rows for the number of mean parameters.");
            int q = 0;
            for (int j = 0; j < k; j++) if (random[j]) q++;
            if (q > 0 && groupCount < 2)
                throw new DesignMatrixException("A nonlinear mixed model needs at least two groups.");

            var nls = FitNls(model, x, y, cancellation);
            if (q == 0)
                return FromNls(model, random, nls, y, x, group, groupCount);

            var randIndex = new int[q];
            for (int j = 0, s = 0; j < k; j++) if (random[j]) randIndex[s++] = j;
            var names = ParameterNames(model);
            var reNames = new string[q];
            for (int s = 0; s < q; s++) reNames[s] = names[randIndex[s]];

            var theta = (double[])nls.Parameters.Clone();
            var b = InitializeRandomEffects(model, x, y, group, groupCount, theta, randIndex, cancellation);
            double[]? warm = null;
            bool converged = false;
            bool singular = false;
            var se = new double[k];
            for (int j = 0; j < k; j++) se[j] = double.NaN;
            var cov = new double[q, q];
            double scale = nls.Sigma2;
            int completed = 0;
            const int maxIter = 25;
            for (int iter = 0; iter < maxIter; iter++)
            {
                cancellation.ThrowIfCancellationRequested();
                BuildPseudo(model, x, y, group, groupCount, theta, b, randIndex, out var px, out var pz, out var w);
                var design = MixedDesign.AccumulateExplicit(px, w, group, groupCount, pz, cancellation);
                design.FixedNames = names;
                design.RandomNames = reNames;
                MixedSolution sol;
                try
                {
                    sol = MixedModelEngine.Fit(design, reml: false, cancellation, warm);
                }
                catch (InvalidOperationException)
                {
                    converged = false;
                    completed = iter + 1;
                    break;
                }
                warm = sol.Theta;
                var bNew = MixedModelEngine.RandomEffects(design, sol.Lambda, sol.Beta);
                double rel = RelativeChange(theta, sol.Beta);
                for (int g = 0; g < groupCount; g++)
                    rel = Math.Max(rel, RelativeChange(b[g], bNew[g]));
                theta = sol.Beta;
                b = bNew;
                scale = sol.Scale;
                cov = sol.RandomCovariance;
                singular = sol.Singular;
                for (int j = 0; j < k; j++)
                    se[j] = j < sol.ParameterStandardErrors.Length ? sol.ParameterStandardErrors[j] : double.NaN;
                completed = iter + 1;
                if (rel < 1e-5)
                {
                    converged = sol.Converged;
                    break;
                }
            }

            var sizes = new int[groupCount];
            for (int i = 0; i < group.Length; i++) sizes[group[i]]++;
            return new NonlinearMixedResult
            {
                Model = model,
                Formula = Formula(model),
                ParameterNames = names,
                Estimates = theta,
                StdErrors = se,
                Random = (bool[])random.Clone(),
                RandomCovariance = cov,
                RandomEffectNames = reNames,
                Scale = scale,
                Converged = converged,
                Singular = singular,
                Iterations = completed,
                StandardErrorMethod = SeLindstromBates,
                GroupCount = groupCount,
                MinGroupSize = sizes.Min(),
                MaxGroupSize = sizes.Max(),
                RowsRead = y.Length,
                RowsUsed = y.Length,
                RowsDropped = 0,
            };
        }

        private static NonlinearMixedResult FromNls(NonlinearMean model, bool[] random, NlsFit nls, double[] y, double[] x, int[] group, int groupCount)
        {
            var sizes = new int[groupCount];
            for (int i = 0; i < group.Length; i++) sizes[group[i]]++;
            return new NonlinearMixedResult
            {
                Model = model,
                Formula = Formula(model),
                ParameterNames = ParameterNames(model),
                Estimates = nls.Parameters,
                StdErrors = nls.StdErrors,
                Random = (bool[])random.Clone(),
                RandomCovariance = new double[0, 0],
                RandomEffectNames = Array.Empty<string>(),
                Scale = nls.Sigma2,
                Converged = nls.Converged,
                Singular = false,
                Iterations = nls.Iterations,
                StandardErrorMethod = SeNls,
                GroupCount = groupCount,
                MinGroupSize = sizes.Min(),
                MaxGroupSize = sizes.Max(),
                RowsRead = y.Length,
                RowsUsed = y.Length,
                RowsDropped = 0,
            };
        }

        private readonly struct NlsFit
        {
            public double[] Parameters { get; init; }
            public double[] StdErrors { get; init; }
            public double Sigma2 { get; init; }
            public bool Converged { get; init; }
            public int Iterations { get; init; }
            public double Rss { get; init; }
        }

        private static NlsFit FitNls(NonlinearMean model, double[] x, double[] y, CancellationToken ct)
        {
            int k = ParameterCount(model);
            NlsFit? best = null;
            foreach (var start in Starts(model, x, y))
            {
                ct.ThrowIfCancellationRequested();
                var fit = RunLm(model, x, y, start, ct);
                if (best is null || fit.Rss < best.Value.Rss) best = fit;
            }
            return best ?? throw new InvalidOperationException("Nonlinear least squares did not return a fit.");
        }

        private static NlsFit RunLm(NonlinearMean model, double[] x, double[] y, double[] start, CancellationToken ct)
        {
            int n = y.Length, k = start.Length;
            var x0 = (double[])start.Clone();
            alglib.minlmcreatevj(k, n, x0, out var state);
            alglib.minlmsetcond(state, 1e-10, 200);
            int evals = 0;
            alglib.minlmoptimize(state,
                (arg, fi, _) =>
                {
                    if ((++evals & 31) == 0) ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < n; i++) fi[i] = y[i] - Mean(model, x[i], arg);
                },
                (arg, fi, jac, _) =>
                {
                    if ((++evals & 31) == 0) ct.ThrowIfCancellationRequested();
                    var d = new double[k];
                    for (int i = 0; i < n; i++)
                    {
                        fi[i] = y[i] - MeanJacobian(model, x[i], arg, d);
                        for (int j = 0; j < k; j++) jac[i, j] = -d[j];
                    }
                },
                null, null);
            alglib.minlmresults(state, out var sol, out var rep);
            double rss = 0;
            for (int i = 0; i < n; i++)
            {
                double r = y[i] - Mean(model, x[i], sol);
                rss += r * r;
            }
            int df = Math.Max(1, n - k);
            double sigma2 = rss / df;
            var se = GaussNewtonSe(model, x, sol, sigma2);
            bool conv = rep.terminationtype is 2 or 7;
            return new NlsFit
            {
                Parameters = sol,
                StdErrors = se,
                Sigma2 = sigma2,
                Converged = conv,
                Iterations = rep.iterationscount,
                Rss = rss,
            };
        }

        private static double[] GaussNewtonSe(NonlinearMean model, double[] x, double[] theta, double sigma2)
        {
            int n = x.Length, k = theta.Length;
            var jtj = new double[k, k];
            var d = new double[k];
            for (int i = 0; i < n; i++)
            {
                MeanJacobian(model, x[i], theta, d);
                for (int a = 0; a < k; a++)
                    for (int b = 0; b <= a; b++)
                    {
                        double v = d[a] * d[b];
                        jtj[a, b] += v;
                        if (a != b) jtj[b, a] += v;
                    }
            }
            var se = new double[k];
            if (!Dense.InvertSpd(jtj, k))
            {
                for (int j = 0; j < k; j++) se[j] = double.NaN;
                return se;
            }
            for (int j = 0; j < k; j++)
                se[j] = jtj[j, j] > 0 && sigma2 >= 0 ? Math.Sqrt(sigma2 * jtj[j, j]) : double.NaN;
            return se;
        }

        private static double[][] InitializeRandomEffects(NonlinearMean model, double[] x, double[] y, int[] group, int groups, double[] theta, int[] randIndex, CancellationToken ct)
        {
            int k = theta.Length, q = randIndex.Length;
            var b = new double[groups][];
            var idx = new List<int>[groups];
            for (int g = 0; g < groups; g++) { b[g] = new double[q]; idx[g] = new List<int>(); }
            for (int i = 0; i < y.Length; i++) idx[group[i]].Add(i);
            for (int g = 0; g < groups; g++)
            {
                if (idx[g].Count < k + 1) continue;
                var xg = idx[g].Select(i => x[i]).ToArray();
                var yg = idx[g].Select(i => y[i]).ToArray();
                try
                {
                    var fit = RunLm(model, xg, yg, theta, ct);
                    if (!fit.Converged && fit.Rss > 1e6) continue;
                    for (int s = 0; s < q; s++)
                        b[g][s] = fit.Parameters[randIndex[s]] - theta[randIndex[s]];
                }
                catch (InvalidOperationException) { }
            }
            return b;
        }

        private static void BuildPseudo(
            NonlinearMean model, double[] x, double[] y, int[] group, int groups,
            double[] theta, double[][] b, int[] randIndex,
            out double[,] px, out double[,] pz, out double[] w)
        {
            int n = y.Length, k = theta.Length, q = randIndex.Length;
            px = new double[n, k];
            pz = new double[n, q];
            w = new double[n];
            var phi = new double[k];
            var jac = new double[k];
            var bg = new double[q];
            for (int i = 0; i < n; i++)
            {
                int g = group[i];
                Array.Copy(theta, phi, k);
                for (int s = 0; s < q; s++) phi[randIndex[s]] += b[g][s];
                double f = MeanJacobian(model, x[i], phi, jac);
                double xz = 0;
                for (int s = 0; s < q; s++)
                {
                    pz[i, s] = jac[randIndex[s]];
                    xz += pz[i, s] * b[g][s];
                    bg[s] = b[g][s];
                }
                double xt = 0;
                for (int j = 0; j < k; j++)
                {
                    px[i, j] = jac[j];
                    xt += jac[j] * theta[j];
                }
                w[i] = y[i] - f + xt + xz;
            }
        }

        private static double RelativeChange(double[] a, double[] b)
        {
            double rel = 0;
            for (int i = 0; i < a.Length; i++)
                rel = Math.Max(rel, Math.Abs(a[i] - b[i]) / Math.Max(1.0, Math.Abs(b[i])));
            return rel;
        }

        private static IEnumerable<double[]> Starts(NonlinearMean model, double[] x, double[] y)
        {
            double ymin = y.Min(), ymax = y.Max(), ymean = y.Average();
            double xmin = x.Min(), xmax = x.Max();
            double xspan = Math.Max(1e-3, xmax - xmin);
            double xmid = 0.5 * (xmin + xmax);
            switch (model)
            {
                case NonlinearMean.ExponentialDecay:
                    yield return new[] { ymean, 0.2 };
                    yield return new[] { ymax, 0.5 };
                    yield return new[] { Math.Max(ymin, 0.1), 0.1 };
                    if (y.All(v => v > 0))
                    {
                        // log y ≈ log a − b x
                        double sy = 0, sx = 0, sxx = 0, sxy = 0;
                        int n = y.Length;
                        for (int i = 0; i < n; i++)
                        {
                            double ly = Math.Log(y[i]);
                            sx += x[i]; sxx += x[i] * x[i]; sy += ly; sxy += x[i] * ly;
                        }
                        double den = n * sxx - sx * sx;
                        if (Math.Abs(den) > 1e-12)
                        {
                            double slope = (n * sxy - sx * sy) / den;
                            double intercept = (sy - slope * sx) / n;
                            yield return new[] { Math.Exp(Math.Clamp(intercept, -20, 20)), Math.Clamp(-slope, -5, 5) };
                        }
                    }
                    break;
                case NonlinearMean.LogisticGrowth:
                    yield return new[] { Math.Max(ymax, 1e-3), xmid, xspan / 4 };
                    yield return new[] { Math.Max(ymax, 1e-3), xmin + 0.3 * xspan, xspan / 6 };
                    yield return new[] { Math.Max(ymean, 1e-3), xmid, 1.0 };
                    break;
                case NonlinearMean.MichaelisMenten:
                    yield return new[] { Math.Max(ymax, 1e-3), Math.Max(xspan / 4, 0.1) };
                    yield return new[] { Math.Max(ymax * 1.5, 1e-3), Math.Max(Math.Abs(xmid), 0.1) };
                    yield return new[] { Math.Max(ymean, 1e-3), 1.0 };
                    break;
                case NonlinearMean.Emax:
                    double e0 = y[Array.IndexOf(x, xmin)];
                    yield return new[] { e0, ymax - e0, Math.Max(xspan / 4, 0.1) };
                    yield return new[] { ymin, Math.Max(ymax - ymin, 1e-3), Math.Max(Math.Abs(xmid), 0.1) };
                    yield return new[] { ymean, 1.0, 1.0 };
                    break;
            }
        }

        /// <summary>평균함수. 정의역 밖(분모 0·지수 폭주)이면 NaN을 내지 않고 큰 값을 돌려 최적화기가 그 점을 버리게 한다.</summary>
        internal static double Mean(NonlinearMean model, double x, double[] p)
        {
            switch (model)
            {
                case NonlinearMean.ExponentialDecay:
                    return ExponentialDecay(p[0], p[1], x, null);
                case NonlinearMean.LogisticGrowth:
                {
                    if (Math.Abs(p[2]) < 1e-8) return 1e6;
                    double z = (p[1] - x) / p[2];
                    if (z > 60) return 0;
                    if (z < -60) return p[0];
                    return p[0] / (1 + Math.Exp(z));
                }
                case NonlinearMean.MichaelisMenten:
                {
                    double den = p[1] + x;
                    if (Math.Abs(den) < 1e-8) return 1e6;
                    return p[0] * x / den;
                }
                case NonlinearMean.Emax:
                {
                    double den = p[2] + x;
                    if (Math.Abs(den) < 1e-8) return 1e6;
                    return p[0] + p[1] * x / den;
                }
                default:
                    return double.NaN;
            }
        }

        /// <summary>평균과 ∂f/∂p. 반환값은 평균. 폭주 구간에서는 기울기를 0으로 둔다.</summary>
        private static double MeanJacobian(NonlinearMean model, double x, double[] p, double[] jac)
        {
            for (int j = 0; j < jac.Length; j++) jac[j] = 0;
            switch (model)
            {
                case NonlinearMean.ExponentialDecay:
                    return ExponentialDecay(p[0], p[1], x, jac);
                case NonlinearMean.LogisticGrowth:
                {
                    if (Math.Abs(p[2]) < 1e-8) return 1e6;
                    double z = (p[1] - x) / p[2];
                    if (z > 60) { jac[0] = 0; return 0; }
                    if (z < -60) { jac[0] = 1; return p[0]; }
                    double ez = Math.Exp(z);
                    double den = 1 + ez;
                    double f = p[0] / den;
                    double s = ez / (den * den);
                    jac[0] = 1 / den;
                    jac[1] = -p[0] * s / p[2];
                    jac[2] = p[0] * s * z / p[2];
                    return f;
                }
                case NonlinearMean.MichaelisMenten:
                {
                    double den = p[1] + x;
                    if (Math.Abs(den) < 1e-8) return 1e6;
                    jac[0] = x / den;
                    jac[1] = -p[0] * x / (den * den);
                    return p[0] * x / den;
                }
                case NonlinearMean.Emax:
                {
                    double den = p[2] + x;
                    if (Math.Abs(den) < 1e-8) return 1e6;
                    jac[0] = 1;
                    jac[1] = x / den;
                    jac[2] = -p[1] * x / (den * den);
                    return p[0] + p[1] * x / den;
                }
                default:
                    return double.NaN;
            }
        }

        /// <summary>
        /// y = a·exp(−b·x). exp 언더플로(매우 작은 양수)는 0이다 — 1e6 대용값이 아니다.
        /// 오버플로·NaN만 비유한값으로 남겨 호출자가 거부한다. 평균과 야코비안이 같은 식을 쓴다.
        /// </summary>
        private static double ExponentialDecay(double a, double b, double x, double[]? jac)
        {
            double z = -b * x;
            // Math.Exp는 약 −745 아래에서 0으로 언더플로한다. 그 0이 함수값이다.
            double e = z < -745d || double.IsNegativeInfinity(z) ? 0d : Math.Exp(z);
            double mean = a * e;
            if (jac is { Length: >= 2 })
            {
                jac[0] = e;
                jac[1] = -a * x * e;
            }
            return mean;
        }
    }
}
