using System.Globalization;

namespace NanumCsvViewer.Stats
{
    // 일반화선형모형(GLzM) — statsmodels GLM.fit() 기본(IRLS)과 같은 정의.
    // 시작값 family.starting_mu, 수렴은 척도 반영 이탈도 |ΔD| ≤ 1e-8 (최대 100회),
    // 척도는 이항·포아송 1, 가우시안·감마는 Pearson χ²/df_resid.
    // Wald는 z·정규(use_t=False). 별칭 열은 계수 NaN.

    public enum GlmFamily { Gaussian, Binomial, Poisson, Gamma }

    public enum GlmLink { Identity, Log, Inverse, Logit, Probit, CLogLog, Sqrt }

    /// <summary>적합이 신뢰할 수 없는 이유. UI가 경고 문구로 옮긴다.</summary>
    public enum GlmDiagnostic
    {
        /// <summary>최대 반복 안에 이탈도 수렴 기준을 못 넘김. 추정치는 유효한 MLE가 아니다.</summary>
        NotConverged,
        /// <summary>이항 (준)완전 분리 — 적합 확률이 0/1이거나 계수가 발산. 추정·표준오차 불가.</summary>
        Separation,
        /// <summary>선형 종속(별칭) 열이 있어 해당 계수는 추정되지 않음.</summary>
        AliasedTerms,
        /// <summary>잔차 자유도가 작음(df &lt; 10). 표준오차·검정이 불안정할 수 있음.</summary>
        SmallSample,
        /// <summary>포아송 반응이 정수가 아님. 척도=1 가능도는 준가능도에 가깝다.</summary>
        NonIntegerPoisson,
    }

    /// <summary>Hosmer–Lemeshow 적합도. 그룹이 부족하거나 기대도수가 0이면 Reliable=false, p는 NaN.</summary>
    public sealed record HosmerLemeshowResult(
        double ChiSquare,
        int Groups,
        int DegreesOfFreedom,
        double PValue,
        bool Reliable);

    /// <summary>이항 로짓 추가 지표. 오즈비·McFadden·분류표·AUC·Hosmer–Lemeshow.</summary>
    public sealed class LogisticSummary
    {
        public required double[] OddsRatio { get; init; }
        public required double[] OddsRatioCiLow { get; init; }
        public required double[] OddsRatioCiHigh { get; init; }
        /// <summary>1 − llf/llnull (statsmodels Logit.prsquared).</summary>
        public required double McFaddenRSquared { get; init; }
        public required double Threshold { get; init; }
        public required long TruePositive { get; init; }
        public required long FalsePositive { get; init; }
        public required long TrueNegative { get; init; }
        public required long FalseNegative { get; init; }
        public required double Accuracy { get; init; }
        public required double Sensitivity { get; init; }
        public required double Specificity { get; init; }
        /// <summary>Mann–Whitney AUC(동점은 평균 순위). 한 클래스가 없으면 NaN.</summary>
        public required double Auc { get; init; }
        public required HosmerLemeshowResult HosmerLemeshow { get; init; }
    }

    public sealed class GeneralizedLinearFit
    {
        public required GlmFamily Family { get; init; }
        public required GlmLink Link { get; init; }
        public required string[] Names { get; init; }
        public required double[] Coefficients { get; init; }
        public required double[] StdErrors { get; init; }
        public required double[] Z { get; init; }
        public required double[] PValues { get; init; }
        public required double[] CiLow { get; init; }
        public required double[] CiHigh { get; init; }
        public required bool[] Aliased { get; init; }
        public required double[] Fitted { get; init; }
        public required double[] LinearPredictor { get; init; }
        public required int Iterations { get; init; }
        public required bool Converged { get; init; }
        /// <summary>이항·포아송은 1, 가우시안·감마는 Pearson χ²/df_resid.</summary>
        public required double Scale { get; init; }
        /// <summary>척도 1 기준 이탈도(statsmodels GLMResults.deviance).</summary>
        public required double Deviance { get; init; }
        public required double NullDeviance { get; init; }
        public required double PearsonChi2 { get; init; }
        public required double LogLikelihood { get; init; }
        public required double NullLogLikelihood { get; init; }
        public required double Aic { get; init; }
        /// <summary>statsmodels bic_llf = −2 llf + log(n)·(df_model+1).</summary>
        public required double Bic { get; init; }
        /// <summary>2(llf − llnull). 절편 포함 모형에서 귀무(절편만) 대비 가능도비.</summary>
        public required double LikelihoodRatio { get; init; }
        public required double LikelihoodRatioP { get; init; }
        public required int DfModel { get; init; }
        public required int DfResid { get; init; }
        public required int Rank { get; init; }
        public required int N { get; init; }
        public required bool HasIntercept { get; init; }
        public required IReadOnlyList<GlmDiagnostic> Diagnostics { get; init; }
        public LogisticSummary? Logistic { get; init; }
    }

    public static class GeneralizedLinearModel
    {
        public const int DefaultMaxIterations = 100;
        public const double DefaultTolerance = 1e-8;
        public const double LogisticThreshold = 0.5;
        public const int HosmerLemeshowGroups = 10;

        /// <summary>statsmodels families.links.FLOAT_EPS = np.finfo(float).eps. double.Epsilon이 아니다.</summary>
        private const double Eps = 2.2204460492503131e-16;

        public static bool IsValidLink(GlmFamily family, GlmLink link) => family switch
        {
            GlmFamily.Gaussian => link is GlmLink.Identity or GlmLink.Log or GlmLink.Inverse,
            GlmFamily.Binomial => link is GlmLink.Logit or GlmLink.Probit or GlmLink.CLogLog,
            GlmFamily.Poisson => link is GlmLink.Log or GlmLink.Identity or GlmLink.Sqrt,
            GlmFamily.Gamma => link is GlmLink.Inverse or GlmLink.Log or GlmLink.Identity,
            _ => false,
        };

        public static GeneralizedLinearFit Fit(
            DesignMatrix design,
            GlmFamily family,
            GlmLink link,
            bool logisticExtras = false,
            CancellationToken cancellation = default)
            => Fit(design.X, design.Y, family, link, design.ColumnNames, design.HasIntercept,
                logisticExtras, cancellation);

        public static GeneralizedLinearFit Fit(
            double[,] x,
            double[] y,
            GlmFamily family,
            GlmLink link,
            IReadOnlyList<string>? names = null,
            bool hasIntercept = true,
            bool logisticExtras = false,
            CancellationToken cancellation = default)
            => Fit(x, y, family, link, names, hasIntercept, logisticExtras,
                DefaultMaxIterations, DefaultTolerance, cancellation);

        public static GeneralizedLinearFit Fit(
            double[,] x,
            double[] y,
            GlmFamily family,
            GlmLink link,
            IReadOnlyList<string>? names,
            bool hasIntercept,
            bool logisticExtras,
            int maxIterations,
            double tolerance,
            CancellationToken cancellation = default)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (y.Length != n) throw new ArgumentException("Row count mismatch.", nameof(y));
            if (n == 0 || p == 0)
                throw new DesignMatrixException("No complete rows: every row has a missing or non-numeric value in the model columns.");
            if (!IsValidLink(family, link))
                throw new DesignMatrixException($"Link '{link}' is not valid for the {family} family.");
            ValidateResponse(y, family);

            cancellation.ThrowIfCancellationRequested();
            var structure = LeastSquares.Fit(x, y, cancellation: cancellation);
            int rank = structure.Rank;
            if (rank < 1 || n <= rank)
                throw new DesignMatrixException(
                    "Not enough complete rows to estimate the model (need more rows than parameters).");

            // statsmodels: df_model = matrix_rank(exog) − 1, df_resid = n − rank, AIC 모수 개수 = rank.
            int dfModel = rank - 1;
            int dfResid = n - rank;
            bool fixedScale = family is GlmFamily.Binomial or GlmFamily.Poisson;

            var mu = new double[n];
            var eta = new double[n];
            var weights = new double[n];
            var z = new double[n];
            double yMean = 0;
            for (int i = 0; i < n; i++) yMean += y[i];
            yMean /= n;
            for (int i = 0; i < n; i++)
                mu[i] = family == GlmFamily.Binomial ? (y[i] + 0.5) / 2.0 : (y[i] + yMean) / 2.0;
            for (int i = 0; i < n; i++) eta[i] = ApplyLink(mu[i], link);

            double scale = EstimateScale(y, mu, family, fixedScale, dfResid);
            double dev0 = Deviance(y, mu, family);
            if (!double.IsFinite(dev0))
                throw new DesignMatrixException(
                    "The model could not be started (deviance is undefined). Check that the response matches the family and link.");
            double devPrev = dev0 / ConvergenceDivisor(scale);

            var beta = new double[p];
            var cov = new double[p, p];
            var aliased = new bool[p];
            for (int j = 0; j < p; j++) beta[j] = double.NaN;
            bool converged = false;
            bool numericalFailure = false;
            int iterations = 0;

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellation.ThrowIfCancellationRequested();
                bool weightsOk = true;
                for (int i = 0; i < n; i++)
                {
                    double g = LinkDerivative(mu[i], link);
                    double v = Variance(mu[i], family);
                    double w = 1.0 / (g * g * v);
                    if (!(w > 0) || !double.IsFinite(w) || !double.IsFinite(g)) { weightsOk = false; break; }
                    weights[i] = w;
                    z[i] = eta[i] + g * (y[i] - mu[i]);
                    if (!double.IsFinite(z[i])) { weightsOk = false; break; }
                }
                if (!weightsOk)
                {
                    numericalFailure = true;
                    iterations = iteration + 1;
                    break;
                }

                LeastSquaresFit wls;
                try
                {
                    wls = LeastSquares.Fit(x, z, weights, cancellation);
                }
                catch (InvalidOperationException)
                {
                    numericalFailure = true;
                    iterations = iteration + 1;
                    break;
                }

                beta = wls.Beta;
                cov = wls.XtWXInverse;
                aliased = wls.Aliased;
                var lin = LeastSquares.Predict(x, beta);
                bool finite = true;
                for (int i = 0; i < n; i++)
                {
                    eta[i] = lin[i];
                    mu[i] = InverseLink(eta[i], link);
                    if (!double.IsFinite(mu[i]) || !double.IsFinite(eta[i])) { finite = false; break; }
                }
                if (!finite)
                {
                    numericalFailure = true;
                    iterations = iteration + 1;
                    break;
                }

                double dev = Deviance(y, mu, family);
                double devScaled = dev / ConvergenceDivisor(scale);
                scale = EstimateScale(y, mu, family, fixedScale, dfResid);
                iterations = iteration + 1;
                if (double.IsFinite(devScaled) && double.IsFinite(devPrev) && Math.Abs(devScaled - devPrev) <= tolerance)
                {
                    converged = true;
                    break;
                }
                devPrev = devScaled;
                if (!double.IsFinite(dev))
                {
                    numericalFailure = true;
                    break;
                }
            }

            if (!converged) numericalFailure = numericalFailure || !AllFinite(beta);

            double deviance = Deviance(y, mu, family);
            double pearson = PearsonChi2(y, mu, family);
            if (!fixedScale && double.IsFinite(pearson))
                scale = pearson / dfResid;

            var nullMu = new double[n];
            for (int i = 0; i < n; i++) nullMu[i] = yMean;
            double nullDev = Deviance(y, nullMu, family);
            double llf = LogLikelihood(y, mu, family, link, scale, n);
            double llnull = LogLikelihood(y, nullMu, family, link, scale, n, nullModel: true);
            int kParams = rank;
            double aic = -2 * llf + 2 * kParams;
            double bic = -2 * llf + Math.Log(n) * kParams;
            double lr = 2 * (llf - llnull);
            double lrP = dfModel > 0 && double.IsFinite(lr) ? Dist.ChiSquareUpper(lr, dfModel) : double.NaN;

            double zcrit = Dist.NormalQuantile(0.975);
            var se = new double[p];
            var zStat = new double[p];
            var pVal = new double[p];
            var ciLo = new double[p];
            var ciHi = new double[p];
            for (int j = 0; j < p; j++)
            {
                if (aliased[j] || !double.IsFinite(beta[j]) || !double.IsFinite(cov[j, j]) || !(cov[j, j] >= 0) || !(scale >= 0))
                {
                    se[j] = zStat[j] = pVal[j] = ciLo[j] = ciHi[j] = double.NaN;
                    continue;
                }
                se[j] = Math.Sqrt(scale * cov[j, j]);
                zStat[j] = se[j] > 0 ? beta[j] / se[j] : double.NaN;
                pVal[j] = Dist.NormalTwoSided(zStat[j]);
                ciLo[j] = beta[j] - zcrit * se[j];
                ciHi[j] = beta[j] + zcrit * se[j];
            }

            bool separation = family == GlmFamily.Binomial && (numericalFailure || IsSeparated(y, mu));
            var diagnostics = new List<GlmDiagnostic>();
            if (!converged) diagnostics.Add(GlmDiagnostic.NotConverged);
            if (separation) diagnostics.Add(GlmDiagnostic.Separation);
            if (aliased.Any(a => a)) diagnostics.Add(GlmDiagnostic.AliasedTerms);
            if (dfResid < 10) diagnostics.Add(GlmDiagnostic.SmallSample);
            if (family == GlmFamily.Poisson && HasNonInteger(y)) diagnostics.Add(GlmDiagnostic.NonIntegerPoisson);

            LogisticSummary? logistic = null;
            if (logisticExtras && family == GlmFamily.Binomial && link == GlmLink.Logit)
                logistic = BuildLogistic(y, mu, beta, se, llf, llnull, zcrit);

            return new GeneralizedLinearFit
            {
                Family = family,
                Link = link,
                Names = ResolveNames(names, p, hasIntercept),
                Coefficients = beta,
                StdErrors = se,
                Z = zStat,
                PValues = pVal,
                CiLow = ciLo,
                CiHigh = ciHi,
                Aliased = aliased,
                Fitted = mu,
                LinearPredictor = eta,
                Iterations = iterations,
                Converged = converged,
                Scale = scale,
                Deviance = deviance,
                NullDeviance = nullDev,
                PearsonChi2 = pearson,
                LogLikelihood = llf,
                NullLogLikelihood = llnull,
                Aic = aic,
                Bic = bic,
                LikelihoodRatio = lr,
                LikelihoodRatioP = lrP,
                DfModel = dfModel,
                DfResid = dfResid,
                Rank = rank,
                N = n,
                HasIntercept = hasIntercept,
                Diagnostics = diagnostics,
                Logistic = logistic,
            };
        }

        /// <summary>
        /// ROC AUC, Mann–Whitney. 점수를 오름차순 정렬하고 동점은 평균 순위(1-based).
        /// AUC = (Σ rank₊ − n₊(n₊+1)/2) / (n₊ n₋). 양성·음성 중 하나가 없으면 NaN.
        /// </summary>
        public static double RocAuc(double[] y, double[] score)
        {
            int n = y.Length;
            if (score.Length != n) throw new ArgumentException("Length mismatch.", nameof(score));
            int nPos = 0, nNeg = 0;
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                if (y[i] >= 0.5) nPos++;
                else nNeg++;
            }
            if (nPos == 0 || nNeg == 0) return double.NaN;
            Array.Sort(order, (a, b) =>
            {
                int c = score[a].CompareTo(score[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            double sumPos = 0;
            int cursor = 0;
            while (cursor < n)
            {
                int end = cursor + 1;
                while (end < n && score[order[end]] == score[order[cursor]]) end++;
                double avg = (cursor + 1 + end) / 2.0;
                for (int k = cursor; k < end; k++)
                    if (y[order[k]] >= 0.5) sumPos += avg;
                cursor = end;
            }
            return (sumPos - nPos * (nPos + 1.0) / 2.0) / (nPos * (double)nNeg);
        }

        /// <summary>
        /// Hosmer–Lemeshow. 적합 확률 오름차순(동점은 행 인덱스)으로 정렬한 뒤 g개 그룹으로
        /// 가능한 한 균등하게 자른다. 앞쪽 n%g개 그룹이 한 관측 더 갖는다.
        /// χ² = Σ (O−E)²/E + (O−E)²/(n_g−E), df = g−2.
        /// 기대도수가 0인 그룹은 빼고, 쓴 그룹 수로 df를 잡는다. 그룹이 3 미만이면 검정 불가.
        /// </summary>
        public static HosmerLemeshowResult HosmerLemeshow(double[] y, double[] mu, int groups = HosmerLemeshowGroups)
        {
            int n = y.Length;
            if (mu.Length != n) throw new ArgumentException("Length mismatch.", nameof(mu));
            if (groups < 2) throw new ArgumentOutOfRangeException(nameof(groups));
            int g = Math.Min(groups, n);
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = mu[a].CompareTo(mu[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            int baseSize = n / g;
            int rem = n % g;
            double chi = 0;
            int used = 0;
            int cursor = 0;
            for (int gi = 0; gi < g; gi++)
            {
                int size = baseSize + (gi < rem ? 1 : 0);
                double obs = 0, exp = 0;
                for (int k = 0; k < size; k++)
                {
                    int row = order[cursor++];
                    obs += y[row];
                    exp += mu[row];
                }
                double expFail = size - exp;
                if (!(exp > 0) || !(expFail > 0) || !double.IsFinite(exp)) continue;
                double d = obs - exp;
                chi += d * d / exp + d * d / expFail;
                used++;
            }
            int df = used - 2;
            bool reliable = used >= 3 && df > 0 && double.IsFinite(chi);
            double p = reliable ? Dist.ChiSquareUpper(chi, df) : double.NaN;
            return new HosmerLemeshowResult(reliable ? chi : chi, used, Math.Max(df, 0), p, reliable);
        }

        private static LogisticSummary BuildLogistic(double[] y, double[] mu, double[] beta, double[] se, double llf, double llnull, double zcrit)
        {
            int p = beta.Length;
            var or = new double[p];
            var lo = new double[p];
            var hi = new double[p];
            for (int j = 0; j < p; j++)
            {
                if (!double.IsFinite(beta[j]) || !double.IsFinite(se[j]))
                {
                    or[j] = lo[j] = hi[j] = double.NaN;
                    continue;
                }
                or[j] = Math.Exp(beta[j]);
                lo[j] = Math.Exp(beta[j] - zcrit * se[j]);
                hi[j] = Math.Exp(beta[j] + zcrit * se[j]);
            }
            long tp = 0, fp = 0, tn = 0, fn = 0;
            for (int i = 0; i < y.Length; i++)
            {
                bool ev = y[i] >= 0.5;
                bool pred = mu[i] >= LogisticThreshold;
                if (ev && pred) tp++;
                else if (!ev && pred) fp++;
                else if (!ev && !pred) tn++;
                else fn++;
            }
            long n = y.Length;
            double acc = n == 0 ? double.NaN : (tp + tn) / (double)n;
            double sens = tp + fn == 0 ? double.NaN : tp / (double)(tp + fn);
            double spec = tn + fp == 0 ? double.NaN : tn / (double)(tn + fp);
            double pr2 = double.IsFinite(llf) && double.IsFinite(llnull) && llnull != 0 ? 1 - llf / llnull : double.NaN;
            return new LogisticSummary
            {
                OddsRatio = or,
                OddsRatioCiLow = lo,
                OddsRatioCiHigh = hi,
                McFaddenRSquared = pr2,
                Threshold = LogisticThreshold,
                TruePositive = tp,
                FalsePositive = fp,
                TrueNegative = tn,
                FalseNegative = fn,
                Accuracy = acc,
                Sensitivity = sens,
                Specificity = spec,
                Auc = RocAuc(y, mu),
                HosmerLemeshow = HosmerLemeshow(y, mu),
            };
        }

        private static void ValidateResponse(double[] y, GlmFamily family)
        {
            if (family == GlmFamily.Binomial)
            {
                for (int i = 0; i < y.Length; i++)
                    if (!IsZeroOrOne(y[i]))
                        throw new DesignMatrixException(
                            "Binomial response must be 0/1. A binary outcome (two levels) is required.");
            }
            else if (family == GlmFamily.Poisson)
            {
                for (int i = 0; i < y.Length; i++)
                    if (!(y[i] >= 0) || !double.IsFinite(y[i]))
                        throw new DesignMatrixException("Poisson response must be non-negative.");
            }
            else if (family == GlmFamily.Gamma)
            {
                for (int i = 0; i < y.Length; i++)
                    if (!(y[i] > 0) || !double.IsFinite(y[i]))
                        throw new DesignMatrixException("Gamma response must be strictly positive.");
            }
            else
            {
                for (int i = 0; i < y.Length; i++)
                    if (!double.IsFinite(y[i]))
                        throw new DesignMatrixException("Gaussian response must be finite.");
            }
        }

        private static bool IsZeroOrOne(double y) => Math.Abs(y) <= 1e-8 || Math.Abs(y - 1) <= 1e-8;

        private static bool HasNonInteger(double[] y)
        {
            for (int i = 0; i < y.Length; i++)
                if (Math.Abs(y[i] - Math.Round(y[i])) > 1e-8) return true;
            return false;
        }

        // 분리 판정은 데이터 기반만 쓴다: 완전 예측 또는 적합 확률이 수치적으로 0/1.
        // 계수 크기는 예측변수 단위에 좌우되므로(x를 1/1000로 바꾸면 β가 1000배) 기준으로 쓰지 않는다.
        private static bool IsSeparated(double[] y, double[] mu)
        {
            bool perfect = true;
            for (int i = 0; i < y.Length; i++)
            {
                double diff = Math.Abs(mu[i] - y[i]);
                double tol = 1e-8 + 1e-5 * Math.Abs(y[i]);
                if (!(diff <= tol)) { perfect = false; break; }
            }
            if (perfect) return true;
            for (int i = 0; i < mu.Length; i++)
                if (mu[i] <= 1e-12 || mu[i] >= 1 - 1e-12) return true;
            return false;
        }

        private static bool AllFinite(double[] v)
        {
            for (int i = 0; i < v.Length; i++)
                if (!double.IsFinite(v[i])) return false;
            return true;
        }

        private static double ConvergenceDivisor(double scale) => scale > 0 && double.IsFinite(scale) ? scale : 1.0;

        private static double EstimateScale(double[] y, double[] mu, GlmFamily family, bool fixedScale, int dfResid)
        {
            if (fixedScale) return 1.0;
            double pearson = PearsonChi2(y, mu, family);
            if (!double.IsFinite(pearson) || dfResid <= 0) return double.NaN;
            return pearson / dfResid;
        }

        private static double PearsonChi2(double[] y, double[] mu, GlmFamily family)
        {
            double s = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double v = Variance(mu[i], family);
                if (!(v > 0) || !double.IsFinite(v)) return double.NaN;
                double r = y[i] - mu[i];
                s += r * r / v;
            }
            return s;
        }

        private static double Deviance(double[] y, double[] mu, GlmFamily family)
        {
            double s = 0;
            for (int i = 0; i < y.Length; i++)
            {
                double d = DevianceTerm(y[i], mu[i], family);
                if (!double.IsFinite(d)) return double.NaN;
                s += d;
            }
            return s;
        }

        private static double DevianceTerm(double y, double mu, GlmFamily family)
        {
            switch (family)
            {
                case GlmFamily.Gaussian:
                    return (y - mu) * (y - mu);
                case GlmFamily.Poisson:
                {
                    if (!(mu > 0) || !double.IsFinite(mu)) return double.NaN;
                    double ratio = Math.Max(y / mu, Eps);
                    return 2 * (y * Math.Log(ratio) - (y - mu));
                }
                case GlmFamily.Gamma:
                {
                    if (!(mu > 0) || !(y > 0) || !double.IsFinite(mu)) return double.NaN;
                    double ratio = Math.Max(y / mu, Eps);
                    return 2 * (-Math.Log(ratio) + (y - mu) / mu);
                }
                case GlmFamily.Binomial:
                {
                    double endogMu = Math.Max(y / (mu + 1e-20), Eps);
                    double nEndogMu = Math.Max((1 - y) / (1 - mu + 1e-20), Eps);
                    return 2 * (y * Math.Log(endogMu) + (1 - y) * Math.Log(nEndogMu));
                }
                default:
                    return double.NaN;
            }
        }

        /// <param name="nullModel">가우시안 항등 연결의 집중 가능도는 적합 모형에만 쓴다. 귀무 ll은 본 모형의 Pearson 척도.</param>
        private static double LogLikelihood(double[] y, double[] mu, GlmFamily family, GlmLink link, double scale, int n, bool nullModel = false)
        {
            switch (family)
            {
                case GlmFamily.Gaussian:
                {
                    double used = scale;
                    if (!nullModel && link == GlmLink.Identity)
                    {
                        double rss = 0;
                        for (int i = 0; i < n; i++)
                        {
                            double r = y[i] - mu[i];
                            rss += r * r;
                        }
                        used = rss / n;
                    }
                    if (!(used > 0) || !double.IsFinite(used)) return double.NaN;
                    double logTerm = Math.Log(used) + Math.Log(2 * Math.PI);
                    double ll = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double r = y[i] - mu[i];
                        ll += -0.5 * (r * r / used + logTerm);
                    }
                    return ll;
                }
                case GlmFamily.Poisson:
                {
                    double ll = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (!(mu[i] > 0)) return double.NaN;
                        ll += y[i] * Math.Log(mu[i]) - mu[i] - LogGamma(y[i] + 1);
                    }
                    return ll;
                }
                case GlmFamily.Gamma:
                {
                    if (!(scale > 0) || !double.IsFinite(scale)) return double.NaN;
                    double weight = 1.0 / scale;
                    double lg = LogGamma(weight);
                    double ll = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (!(mu[i] > 0) || !(y[i] > 0)) return double.NaN;
                        double ratio = Math.Max(y[i] / mu[i], Eps);
                        ll += weight * Math.Log(weight * ratio) - weight * ratio - lg - Math.Log(y[i]);
                    }
                    return ll;
                }
                case GlmFamily.Binomial:
                {
                    double ll = 0;
                    for (int i = 0; i < n; i++)
                    {
                        double m = mu[i];
                        if (m <= 0) ll += y[i] <= 0.5 ? 0 : double.NegativeInfinity;
                        else if (m >= 1) ll += y[i] >= 0.5 ? 0 : double.NegativeInfinity;
                        else ll += y[i] * Math.Log(m / (1 - m + 1e-20)) + Math.Log(1 - m + 1e-20);
                    }
                    return ll;
                }
                default:
                    return double.NaN;
            }
        }

        private static double LogGamma(double x)
        {
            double v = alglib.lngamma(x, out _);
            return v;
        }

        private static double Variance(double mu, GlmFamily family) => family switch
        {
            GlmFamily.Gaussian => 1.0,
            GlmFamily.Poisson => Math.Abs(mu),
            GlmFamily.Gamma => mu * mu,
            GlmFamily.Binomial => Clip01(mu) * (1 - Clip01(mu)),
            _ => double.NaN,
        };

        private static double ApplyLink(double mu, GlmLink link) => link switch
        {
            GlmLink.Identity => mu,
            GlmLink.Log => Math.Log(Math.Max(mu, Eps)),
            GlmLink.Inverse => 1.0 / mu,
            GlmLink.Sqrt => Math.Sqrt(Math.Max(mu, 0)),
            GlmLink.Logit => Logit(Clip01(mu)),
            GlmLink.Probit => Dist.NormalQuantile(Clip01(mu)),
            GlmLink.CLogLog => Math.Log(-Math.Log(1 - Clip01(mu))),
            _ => double.NaN,
        };

        private static double InverseLink(double eta, GlmLink link)
        {
            switch (link)
            {
                case GlmLink.Identity: return eta;
                case GlmLink.Log: return Math.Exp(eta);
                case GlmLink.Inverse: return 1.0 / eta;
                case GlmLink.Sqrt: return eta * eta;
                case GlmLink.Logit:
                    // statsmodels: 1/(1+exp(−z)). 오버플로는 0 또는 1.
                    double t = Math.Exp(-eta);
                    return 1.0 / (1.0 + t);
                case GlmLink.Probit: return Dist.NormalCdf(eta);
                case GlmLink.CLogLog: return 1.0 - Math.Exp(-Math.Exp(eta));
                default: return double.NaN;
            }
        }

        private static double LinkDerivative(double mu, GlmLink link)
        {
            switch (link)
            {
                case GlmLink.Identity: return 1.0;
                case GlmLink.Log: return 1.0 / Math.Max(mu, Eps);
                case GlmLink.Inverse: return -1.0 / (mu * mu);
                case GlmLink.Sqrt: return 0.5 / Math.Sqrt(mu);
                case GlmLink.Logit:
                {
                    double p = Clip01(mu);
                    return 1.0 / (p * (1 - p));
                }
                case GlmLink.Probit:
                {
                    double p = Clip01(mu);
                    double z = Dist.NormalQuantile(p);
                    return 1.0 / NormalPdf(z);
                }
                case GlmLink.CLogLog:
                {
                    double p = Clip01(mu);
                    return 1.0 / ((p - 1) * Math.Log(1 - p));
                }
                default: return double.NaN;
            }
        }

        private static double Logit(double p) => Math.Log(p / (1 - p));

        private static double Clip01(double p) => Math.Clamp(p, Eps, 1 - Eps);

        private static double NormalPdf(double z)
        {
            // scipy.stats.norm.pdf — 표준정규 밀도. ALGLIB CDF와 같은 분포.
            return Math.Exp(-0.5 * z * z) / Math.Sqrt(2 * Math.PI);
        }

        private static string[] ResolveNames(IReadOnlyList<string>? names, int p, bool hasIntercept)
        {
            if (names is not null && names.Count == p) return names.ToArray();
            var built = new string[p];
            for (int j = 0; j < p; j++)
                built[j] = hasIntercept && j == 0 ? "Intercept" : "x" + j.ToString(CultureInfo.InvariantCulture);
            return built;
        }
    }
}
