using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // statsmodels 0.14.5 GLM.fit() / Logit.fit() 기준값.
    // 데이터: numpy Generator(20260327), n=48, X = [1, x1, x2].
    // 생성: sm.GLM(y, X, family=...).fit() 및 sm.Logit(y, X).fit(disp=0).
    public class GeneralizedLinearModelTests
    {
        private static readonly double[] X1 =
        {
            0.012225046399702363, -0.055494672050217318, 0.72585124183852545, 0.64648382635133084, 1.4736599048102406, -0.028762048625449781,
            -0.80693308485107429, 0.069585327773816846, 0.83600593668613843, -1.7094292144089418, -0.47701870937407576, 0.71900209473762078,
            -0.24214694155699357, 0.32808697060849806, -1.2094346185126157, -0.35251271427954634, 0.25547239721909371, 0.66285801234512165,
            -1.2866927916774282, -0.68815648882255886, -0.65120699993085129, 0.37773634855735866, -0.69693339314017277, -1.2247416316231869,
            0.46108683345803303, 0.86830495727781398, -1.0288307524724958, -0.73366477084038606, 0.40413234436356876, -0.61558684044473055,
            -0.59927570607966962, -0.74448347890880029, 2.6814617133425833, 0.097871089293398605, -0.19928824900988729, 0.75206205916623492,
            -0.51071362024811129, -0.021297740817856441, -0.73633753793722256, -1.3423832175652559, -1.0792137812450235, 1.0418672964633311,
            1.5925635758525685, -1.1691881348079936, 1.1542100455756636, 0.31972303370487465, 0.29599507125353414, -0.75433421765122299
        };

        private static readonly double[] X2 =
        {
            0.49033499192258789, 0.0055349696894813061, 0.69326075854818159, 0.50623126074133151, 0.54748385514409215, -0.88763076834482169,
            -0.31074713925563802, -0.56892007318408444, -0.55267068147588572, -0.83289682549867217, -0.65688024111643273, -0.92115033532151225,
            -0.063643862054570954, -0.24847489211209606, -0.86166906409839128, -0.27818886664307807, 0.43324491622693317, -0.51321167283570834,
            0.98236841747588466, 0.96643489168733354, -0.0029893326503442896, -0.96896895468713695, -0.47104569587071077, 0.27601221222503125,
            -0.81199166422025026, 0.96019984064340269, 0.039779420027909795, -0.88566355597806834, -0.79104922708875236, -0.69499112364126803,
            -0.95474377173314329, -0.23453108437100001, -0.76516807882104487, -0.6965728445237378, 0.78558111530499475, 0.021472929132762664,
            0.26102190864067665, 0.87768578140884035, 0.019087679502726829, 0.53723547259897297, 0.84788032820727799, -0.674587618544912,
            -0.61979724911243106, -0.91862700996757907, -0.73949317797655767, -0.028860809266471499, -0.81699207053564171, -0.97625400561835751
        };

        private static readonly double[] YBin =
        {
            1.0, 0.0, 0.0, 1.0, 1.0, 1.0,
            0.0, 1.0, 0.0, 0.0, 0.0, 1.0,
            0.0, 0.0, 0.0, 1.0, 0.0, 1.0,
            0.0, 0.0, 1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0, 1.0, 0.0,
            1.0, 0.0, 1.0, 0.0, 0.0, 1.0,
            1.0, 0.0, 0.0, 0.0, 0.0, 1.0,
            1.0, 0.0, 1.0, 1.0, 0.0, 1.0
        };

        private static readonly double[] YPois =
        {
            0.0, 2.0, 1.0, 3.0, 2.0, 1.0,
            2.0, 1.0, 1.0, 1.0, 0.0, 1.0,
            1.0, 0.0, 1.0, 2.0, 2.0, 2.0,
            1.0, 0.0, 1.0, 0.0, 1.0, 1.0,
            2.0, 2.0, 2.0, 0.0, 1.0, 2.0,
            0.0, 0.0, 6.0, 1.0, 1.0, 2.0,
            2.0, 1.0, 1.0, 0.0, 0.0, 1.0,
            6.0, 0.0, 3.0, 3.0, 1.0, 1.0
        };

        private static readonly double[] YGamma =
        {
            0.8346262250418417, 0.17513224321688467, 3.4658460233818911, 1.3569188086915078, 1.7291198845025693, 3.5370713340398909,
            1.9212712059395134, 0.79840970909664477, 1.4100838094764021, 0.204874797880052, 1.123752967152104, 2.1049336568781287,
            0.81820991040604762, 1.9620449165864564, 1.9129983832913242, 0.65754133724651309, 1.2889040687470186, 0.62261465556617823,
            0.18724770611151295, 0.71008188197360966, 0.445745275098198, 0.87567839731063435, 0.20314920155634505, 0.97086789732047329,
            0.95649688184387327, 1.1059152449157701, 0.72484179145484751, 0.5935606663978289, 1.1417069537665154, 0.78893979738935516,
            0.40432414283548979, 0.55403741536850237, 4.5066527299179837, 1.4787596743985996, 0.55280651933696023, 0.87191679611623019,
            0.78581171252291648, 0.65028599921245744, 1.6954785437654711, 0.07188640103595148, 0.60779575286174192, 0.89624282278907763,
            1.9667802409804653, 1.9499937799626157, 0.59067100871742129, 0.43680098805462958, 1.1264143323618028, 0.53632247075444561
        };

        private static double[,] Design()
        {
            var x = new double[X1.Length, 3];
            for (int i = 0; i < X1.Length; i++)
            {
                x[i, 0] = 1;
                x[i, 1] = X1[i];
                x[i, 2] = X2[i];
            }
            return x;
        }

        private static void Close(double actual, double expected, string label)
        {
            double tol = Math.Max(1e-8, 1e-6 * Math.Abs(expected));
            Assert.True(Math.Abs(actual - expected) <= tol, $"{label}: expected {expected}, got {actual}");
        }

        private static void CloseAll(double[] actual, double[] expected, string label)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Close(actual[i], expected[i], label + "[" + i + "]");
        }

        [Fact]
        public void Logistic_matches_statsmodels_glm_and_logit()
        {
            // sm.GLM(y, X, family=Binomial(Logit())).fit() — params, bse, llf, aic, deviance
            // sm.Logit(y, X).fit(disp=0) — prsquared, llr
            var fit = GeneralizedLinearModel.Fit(Design(), YBin, GlmFamily.Binomial, GlmLink.Logit, logisticExtras: true);
            Assert.True(fit.Converged);
            Assert.Equal(5, fit.Iterations);
            CloseAll(fit.Coefficients, [-0.3913779638975065, 1.595533952705966, -0.4047464114827934], "coef");
            CloseAll(fit.StdErrors, [0.3679853579891566, 0.5116848747356031, 0.5654064155489871], "bse");
            Close(fit.LogLikelihood, -24.98655015504738, "llf");
            Close(fit.Aic, 55.97310031009476, "aic");
            Close(fit.Deviance, 49.97310031009476, "deviance");
            Close(fit.Bic, 61.58670334281844, "bic_llf");
            Assert.NotNull(fit.Logistic);
            Close(fit.Logistic.McFaddenRSquared, 0.2335714232996323, "prsquared");
            Close(fit.LikelihoodRatio, 15.22945323408442, "llr");
            Assert.Equal(2, fit.DfModel);
            Assert.DoesNotContain(GlmDiagnostic.Separation, fit.Diagnostics);
            Assert.DoesNotContain(GlmDiagnostic.NotConverged, fit.Diagnostics);
        }

        [Fact]
        public void Poisson_log_matches_statsmodels()
        {
            // sm.GLM(y, X, family=Poisson(Log())).fit() — params, bse, deviance, aic
            var fit = GeneralizedLinearModel.Fit(Design(), YPois, GlmFamily.Poisson, GlmLink.Log);
            Assert.True(fit.Converged);
            CloseAll(fit.Coefficients, [0.2047855701019626, 0.5794762545200608, 0.0345038109824118], "coef");
            CloseAll(fit.StdErrors, [0.1406484861678405, 0.1241489461512344, 0.2033500425027945], "bse");
            Close(fit.Deviance, 36.12581659962565, "deviance");
            Close(fit.Aic, 129.7805974720964, "aic");
            Close(fit.Scale, 1, "scale");
        }

        [Fact]
        public void Gamma_log_matches_statsmodels_pearson_scale()
        {
            // sm.GLM(y, X, family=Gamma(Log())).fit() — params, bse (scale = Pearson χ²/df)
            var fit = GeneralizedLinearModel.Fit(Design(), YGamma, GlmFamily.Gamma, GlmLink.Log);
            Assert.True(fit.Converged);
            CloseAll(fit.Coefficients, [0.03435729307487623, 0.3615094615394518, -0.2235568631314627], "coef");
            CloseAll(fit.StdErrors, [0.1050627939054412, 0.1133660739421066, 0.1585826310158769], "bse");
            Close(fit.Scale, 0.4775536221038442, "scale");
            Close(fit.PearsonChi2, 21.48991299467298, "pearson");
        }

        [Fact]
        public void Probit_matches_statsmodels()
        {
            // sm.GLM(y, X, family=Binomial(Probit())).fit() — params, bse
            var fit = GeneralizedLinearModel.Fit(Design(), YBin, GlmFamily.Binomial, GlmLink.Probit);
            Assert.True(fit.Converged);
            CloseAll(fit.Coefficients, [-0.235199062787641, 0.9850912319897703, -0.2496832026296683], "coef");
            CloseAll(fit.StdErrors, [0.2179199179333241, 0.289500808779718, 0.3338741731180043], "bse");
            Close(fit.Deviance, 49.653017410784, "deviance");
        }

        [Fact]
        public void Complete_separation_warns_estimates_are_unreliable()
        {
            var x = new double[8, 2];
            double[] xs = [-2, -1.5, -1, -0.5, 0.5, 1, 1.5, 2];
            double[] y = [0, 0, 0, 0, 1, 1, 1, 1];
            for (int i = 0; i < 8; i++) { x[i, 0] = 1; x[i, 1] = xs[i]; }
            var fit = GeneralizedLinearModel.Fit(x, y, GlmFamily.Binomial, GlmLink.Logit, logisticExtras: true);
            Assert.Contains(GlmDiagnostic.Separation, fit.Diagnostics);
        }

        [Fact]
        public void Rejects_response_outside_family_domain()
        {
            var x = new double[,] { { 1, 0 }, { 1, 1 }, { 1, 2 }, { 1, 3 } };
            var poisson = Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(x, [1, 0, -1, 2], GlmFamily.Poisson, GlmLink.Log));
            Assert.Contains("non-negative", poisson.Message, StringComparison.Ordinal);

            var gamma = Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(x, [1, 0.5, 0, 2], GlmFamily.Gamma, GlmLink.Log));
            Assert.Contains("positive", gamma.Message, StringComparison.Ordinal);

            var binomial = Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(x, [0, 1, 0.5, 1], GlmFamily.Binomial, GlmLink.Logit));
            Assert.Contains("0/1", binomial.Message, StringComparison.Ordinal);

            var link = Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(x, [0, 1, 0, 1], GlmFamily.Poisson, GlmLink.Logit));
            Assert.Contains("not valid", link.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Auc_matches_mann_whitney_with_ties()
        {
            // sklearn.metrics.roc_auc_score (Mann–Whitney, midranks) on a tied score vector.
            double[] y = [0, 1, 1, 0, 1, 0, 1, 0];
            double[] score = [0.1, 0.2, 0.2, 0.4, 0.4, 0.4, 0.9, 0.9];
            Close(GeneralizedLinearModel.RocAuc(y, score), 0.46875, "auc");
        }

        [Fact]
        public void Hosmer_Lemeshow_matches_numpy_equal_size_groups()
        {
            // numpy: sort by mu (ties by index), 10 groups, first n%10 groups get one extra row.
            // χ² = Σ (O−E)²/E + (O−E)²/(n−E), df = g−2.
            double[] mu = [0.05, 0.1, 0.15, 0.2, 0.3, 0.35, 0.4, 0.55, 0.6, 0.7, 0.8, 0.9];
            double[] y = [0, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1, 1];
            var hl = GeneralizedLinearModel.HosmerLemeshow(y, mu);
            Assert.True(hl.Reliable);
            Assert.Equal(10, hl.Groups);
            Assert.Equal(8, hl.DegreesOfFreedom);
            Close(hl.ChiSquare, 6.8522776022776029, "hl");
        }

        [Fact]
        public void Logistic_auc_and_hosmer_lemeshow_match_numpy_on_fitted_probabilities()
        {
            // numpy midrank AUC and the decile grouping above, applied to sm.GLM binomial-logit fitted mu.
            // sklearn roc_auc_score(y, mu) = 0.8053571428571429; HL χ² = 4.503293134665567.
            var fit = GeneralizedLinearModel.Fit(Design(), YBin, GlmFamily.Binomial, GlmLink.Logit, logisticExtras: true);
            Assert.NotNull(fit.Logistic);
            Close(fit.Logistic.Auc, 0.8053571428571429, "auc");
            Close(fit.Logistic.HosmerLemeshow.ChiSquare, 4.503293134665567, "hl");
            Assert.Equal(8, fit.Logistic.HosmerLemeshow.DegreesOfFreedom);
        }
    }
}
