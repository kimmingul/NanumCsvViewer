using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // statsmodels 0.14.5 GLM 대조: offset / freq_weights / var_weights / 이항 2열(시행 수). 자동 생성 기준값.
    public class GlmExtrasTests
    {
        static readonly double[] X = new double[] { 2.0409, -2.5557, 0.4181, -0.5678, -0.4526, -0.2156, -2.02, -0.2319, -0.8652, 3.323, 0.2258, -0.3526, -0.2813, -0.668, -1.0552, -0.3908, 0.4819, -0.2386, 0.9578, -0.1998, 0.0243, 1.5458, 0.5451, -0.5052, -0.1828, 0.5405, 1.9351, -0.2696, -0.2436, 1.0023 };
        static readonly double[] G = new double[] { 1.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 1.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0 };
        static readonly double[] Succ = new double[] { 10.0, 2.0, 3.0, 5.0, 4.0, 0.0, 2.0, 3.0, 5.0, 6.0, 6.0, 5.0, 2.0, 3.0, 0.0, 3.0, 9.0, 1.0, 6.0, 4.0, 3.0, 4.0, 4.0, 4.0, 1.0, 3.0, 4.0, 3.0, 5.0, 4.0 };
        static readonly double[] Trials = new double[] { 11.0, 9.0, 5.0, 10.0, 6.0, 3.0, 8.0, 10.0, 8.0, 6.0, 11.0, 7.0, 6.0, 4.0, 4.0, 9.0, 10.0, 5.0, 8.0, 10.0, 4.0, 5.0, 7.0, 8.0, 6.0, 6.0, 4.0, 8.0, 11.0, 4.0 };
        static readonly double[] Exposure = new double[] { 2.1997, 1.4207, 1.9743, 2.1738, 2.1728, 1.8076, 1.8868, 0.9954, 1.738, 0.8135, 1.7019, 1.8406, 2.4353, 1.4842, 0.549, 1.8194, 1.0131, 2.3531, 1.4717, 1.4512, 2.7736, 1.4824, 1.3721, 1.37, 1.7019, 0.7332, 1.8669, 2.8036, 1.9073, 2.3598 };
        static readonly double[] Cnt = new double[] { 13.0, 0.0, 2.0, 1.0, 3.0, 2.0, 1.0, 1.0, 1.0, 5.0, 2.0, 3.0, 0.0, 0.0, 0.0, 3.0, 1.0, 5.0, 5.0, 2.0, 7.0, 4.0, 5.0, 1.0, 3.0, 0.0, 5.0, 3.0, 4.0, 4.0 };
        static readonly double[] Fw = new double[] { 2.0, 3.0, 3.0, 3.0, 1.0, 1.0, 2.0, 2.0, 1.0, 3.0, 3.0, 1.0, 2.0, 2.0, 2.0, 3.0, 2.0, 3.0, 1.0, 2.0, 1.0, 2.0, 2.0, 3.0, 3.0, 2.0, 3.0, 2.0, 3.0, 2.0 };
        static readonly double[] Vw = new double[] { 1.5461, 1.2675, 1.0046, 1.1516, 1.8837, 0.7813, 0.7225, 0.5182, 1.7782, 1.983, 0.8547, 0.8991, 0.7976, 1.4003, 1.2277, 1.7309, 0.8146, 1.8169, 1.1959, 1.7118, 1.7031, 1.8661, 1.2482, 1.4144, 1.3415, 0.7855, 1.2523, 1.8899, 1.8633, 0.5323 };
        static readonly double[] Yg = new double[] { 3.9841, 0.7916, 2.4402, 1.1395, 1.2277, 0.4458, 1.4517, 2.8147, 0.8625, 1.6121, 3.7029, 3.1349, 2.2427, 0.2805, 1.2141, 0.6028, 1.2186, 0.9618, 1.556, 2.8873, 0.7893, 2.3702, 2.0418, 1.0207, 1.1066, 0.8275, 2.0764, 0.806, 1.3277, 3.1969 };
        static readonly double[] Yn = new double[] { 6.8598, -3.7546, 1.1505, 0.967, 0.3195, -1.4896, -1.2459, 0.9189, 1.7556, 6.9963, 1.2702, 2.8433, -0.6381, -0.1326, 0.3393, 1.3329, 1.8832, 1.6252, 4.7547, -0.2064, 1.6592, 5.6687, 5.4229, 0.6531, -0.2804, 3.8167, 4.473, 1.7499, 1.6454, 4.173 };
        static readonly double[] Off = new double[] { 0.6123, -0.7667, 0.1254, -0.1703, -0.1358, -0.0647, -0.606, -0.0696, -0.2596, 0.9969, 0.0677, -0.1058, -0.0844, -0.2004, -0.3166, -0.1172, 0.1446, -0.0716, 0.2873, -0.0599, 0.0073, 0.4637, 0.1635, -0.1516, -0.0548, 0.1621, 0.5805, -0.0809, -0.0731, 0.3007 };

        static double[,] Design()
        {
            int n = X.Length;
            var m = new double[n, 3];
            for (int i = 0; i < n; i++) { m[i, 0] = 1; m[i, 1] = X[i]; m[i, 2] = G[i]; }
            return m;
        }

        static double[] Log(double[] a) => a.Select(v => Math.Log(v)).ToArray();

        static void Check(GeneralizedLinearFit f, string key, double dfResid)
        {
            var e = Expected[key];
            for (int j = 0; j < 3; j++)
            {
                Assert.InRange(Math.Abs(f.Coefficients[j] - e.Params[j]), 0, 1e-6);
                Assert.InRange(Math.Abs(f.StdErrors[j] - e.Bse[j]), 0, 1e-6);
            }
            Assert.InRange(Math.Abs(f.Deviance - e.Deviance), 0, 1e-6);
            Assert.InRange(Math.Abs(f.NullDeviance - e.NullDeviance), 0, 1e-5);
            Assert.InRange(Math.Abs(f.LogLikelihood - e.Llf), 0, 1e-5);
            Assert.InRange(Math.Abs(f.NullLogLikelihood - e.Llnull), 0, 1e-5);
            Assert.InRange(Math.Abs(f.Aic - e.Aic), 0, 1e-5);
            Assert.InRange(Math.Abs(f.Bic - e.Bic), 0, 1e-5);
            Assert.InRange(Math.Abs(f.Scale - e.Scale), 0, 1e-6);
            Assert.InRange(Math.Abs(f.PearsonChi2 - e.Pearson), 0, 1e-6);
            Assert.Equal(dfResid, f.DfResid);
        }

        record Ref(double[] Params, double[] Bse, double Deviance, double NullDeviance, double Llf, double Llnull, double Aic, double Bic, double Scale, double Pearson);

        static readonly Dictionary<string, Ref> Expected = new()
        {
            ["bin_trials"] = new Ref(new double[] { 0.35567967112226406, 0.8665947618612981, -0.2707031083077359 }, new double[] { 0.2487913016213417, 0.1783891305848853, 0.30991597345492566 }, 33.01961248807188, 67.24303731354546, -45.28893642863622, -62.400648841373, 96.57787285727244, 100.7814650022589, 1.0, 28.372431545616266),
            ["bin_trials_fw"] = new Ref(new double[] { 0.3241803758236246, 0.8960728787864334, -0.34422411765333727 }, new double[] { 0.16332808145131072, 0.12006861406634162, 0.20717027959137152 }, 62.814172027947315, 149.29489525978772, -94.34837099846774, -137.58873261438794, 194.6967419969355, 201.2199038066224, 1.0, 54.28853416708714),
            ["pois_offset"] = new Ref(new double[] { 5.31312690302127e-05, 0.5704740906282133, 0.5022726515585246 }, new double[] { 0.20583464528936468, 0.09202264576107728, 0.2310473984637887 }, 22.330806737255788, 59.81486840514557, -47.58418024697128, -66.32621108091617, 101.16836049394256, 105.37195263892903, 1.0, 19.194711371100578),
            ["pois_fw"] = new Ref(new double[] { 0.42893655459403174, 0.47975635400021616, 0.6783280990516367 }, new double[] { 0.14597879788545787, 0.05921534929011536, 0.16361593470230545 }, 76.53016929737969, 147.4737103558832, -116.61918165422993, -152.09095218348168, 239.23836330845987, 245.76152511814678, 1.0, 71.13298544095622),
            ["pois_vw"] = new Ref(new double[] { 0.6176828367524994, 0.42628315687096024, 0.5749587662287042 }, new double[] { 0.1822056947550596, 0.07020077174139668, 0.20061054238407403 }, 50.82028585279206, 89.7198379590708, -75.74417502505382, -95.19395107819322, 157.48835005010764, 161.6919421950941, 1.0, 51.722782083664995),
            ["gamma_vw_fw"] = new Ref(new double[] { 0.3609547722144396, 0.21624906519117315, 0.03491423405696498 }, new double[] { 0.10052610649711786, 0.051595594551955413, 0.1348296055378174 }, 22.611477777871855, 28.732969049598125, -75.34198025032433, -83.3839566429357, 156.68396050064865, 163.20712231033556, 0.3805961975560173, 23.596964248473082),
            ["gauss_vw_off"] = new Ref(new double[] { 0.982345096121036, 1.6214280000818055, 1.3247942456912283 }, new double[] { 0.28139340915613725, 0.1503610112429975, 0.366359241935014 }, 34.155806318331436, 192.01515337889109, -41.60288907625402, -104.07682268650946, 89.20577815250805, 93.40937029749452, 1.265029863641905, 34.155806318331436),
            ["gauss_fw"] = new Ref(new double[] { 0.9772533926225457, 1.9067309755700994, 1.2082079031348885 }, new double[] { 0.16944126700619805, 0.09537268206204955, 0.2337005223466583 }, 54.59150954959719, 418.33465730861536, -86.55946534358925, -293.14813846284585, 179.1189306871785, 185.64209249686542, 0.8805082185418905, 54.59150954959719),
        };

        [Fact]
        public void Binomial_trials_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Succ, GlmFamily.Binomial, GlmLink.Logit, extras: new GlmExtras { Trials = Trials });
            Assert.True(f.Converged);
            Check(f, "bin_trials", 27.0);
        }

        [Fact]
        public void Binomial_trials_with_frequency_weights_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Succ, GlmFamily.Binomial, GlmLink.Logit, extras: new GlmExtras { Trials = Trials, FrequencyWeights = Fw });
            Assert.True(f.Converged);
            Check(f, "bin_trials_fw", 62.0);
        }

        [Fact]
        public void Poisson_offset_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Cnt, GlmFamily.Poisson, GlmLink.Log, extras: new GlmExtras { Offset = Log(Exposure) });
            Assert.True(f.Converged);
            Check(f, "pois_offset", 27.0);
        }

        [Fact]
        public void Poisson_frequency_weights_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Cnt, GlmFamily.Poisson, GlmLink.Log, extras: new GlmExtras { FrequencyWeights = Fw });
            Assert.True(f.Converged);
            Check(f, "pois_fw", 62.0);
        }

        [Fact]
        public void Poisson_variance_weights_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Cnt, GlmFamily.Poisson, GlmLink.Log, extras: new GlmExtras { VarianceWeights = Vw });
            Assert.True(f.Converged);
            Check(f, "pois_vw", 27.0);
        }

        [Fact]
        public void Gamma_variance_and_frequency_weights_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Yg, GlmFamily.Gamma, GlmLink.Log, extras: new GlmExtras { VarianceWeights = Vw, FrequencyWeights = Fw });
            Assert.True(f.Converged);
            Check(f, "gamma_vw_fw", 62.0);
        }

        [Fact]
        public void Gaussian_variance_weights_with_offset_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Yn, GlmFamily.Gaussian, GlmLink.Identity, extras: new GlmExtras { VarianceWeights = Vw, Offset = Off });
            Assert.True(f.Converged);
            Check(f, "gauss_vw_off", 27.0);
        }

        [Fact]
        public void Gaussian_frequency_weights_match_statsmodels()
        {
            var f = GeneralizedLinearModel.Fit(Design(), Yn, GlmFamily.Gaussian, GlmLink.Identity, extras: new GlmExtras { FrequencyWeights = Fw });
            Assert.True(f.Converged);
            Check(f, "gauss_fw", 62.0);
        }

        [Fact]
        public void Invalid_extras_are_rejected_with_a_reason()
        {
            var y = Cnt;
            Assert.Contains("positive integers", Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(Design(), y, GlmFamily.Poisson, GlmLink.Log, extras: new GlmExtras { FrequencyWeights = Fw.Select(v => v + 0.5).ToArray() })).Message);
            Assert.Contains("positive", Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(Design(), y, GlmFamily.Poisson, GlmLink.Log, extras: new GlmExtras { VarianceWeights = Vw.Select(v => -v).ToArray() })).Message);
            Assert.Contains("binomial", Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(Design(), y, GlmFamily.Poisson, GlmLink.Log, extras: new GlmExtras { Trials = Trials })).Message);
            Assert.Contains("Successes", Assert.Throws<DesignMatrixException>(() =>
                GeneralizedLinearModel.Fit(Design(), Trials.Select(v => v + 1).ToArray(), GlmFamily.Binomial, GlmLink.Logit, extras: new GlmExtras { Trials = Trials })).Message);
        }
    }
}
namespace NanumCsvViewer.Tests
{
    public class DesignMatrixExtrasTests
    {
        [Fact]
        public void Extra_columns_are_row_aligned_and_missing_values_drop_the_row()
        {
            var headers = new[] { "y", "x", "expo", "w" };
            var rows = new List<string[]>
            {
                new[] { "3", "1", "2", "1" },
                new[] { "4", "2", "", "1" },      // 노출 결측 → 삭제
                new[] { "5", "3", "4", "2" },
                new[] { "6", "4", "0", "1" },     // 노출 0 → ln 불가 → 삭제
                new[] { "7", "5", "8", "3" },
            };
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x"), _ => VariableKind.Numeric,
                new DesignMatrixOptions { ExposureColumn = "expo", FrequencyWeightColumn = "w" });
            Assert.Equal(3, dm.RowCount);
            Assert.Equal(2, dm.RowsDropped);
            Assert.Equal(new[] { 0, 2, 4 }, dm.ViewRows);
            Assert.Equal(new[] { Math.Log(2), Math.Log(4), Math.Log(8) }, dm.GlmExtras!.Offset!);
            Assert.Equal(new double[] { 1, 2, 3 }, dm.GlmExtras.FrequencyWeights!);
            Assert.Null(dm.GlmExtras.Trials);
        }
    }
}
