using System.Globalization;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 선형 혼합모형. 참조값은 statsmodels 0.14.5
    // smf.mixedlm(...).fit(reml=True/False, method="bfgs") 의 fe_params, bse, cov_re, scale, llf.
    // 자료는 이진 유리수라 C#·Python 부동소수 값이 같다.
    public class MixedModelsTests
    {
        private static readonly string[] RiHeaders = { "y", "x", "g" };
        private static readonly string[] RsHeaders = { "y", "x", "z", "g" };

        [Fact]
        public void Random_intercept_REML_matches_statsmodels()
        {
            // smf.mixedlm("y ~ x", df, groups=df["g"]).fit(reml=True)
            var fit = FitRi(reml: true);
            Close(2.0836660620827594, fit.Coefficients[0].Estimate);
            Close(0.7293204355433482, fit.Coefficients[1].Estimate);
            Close(0.20167844940246987, fit.Coefficients[0].StdError);
            Close(0.07864950177873886, fit.Coefficients[1].StdError);
            Close(0.5186156074140765, fit.RandomCovariance[0, 0]);
            Close(0.7917244576380352, fit.Scale);
            Close(-139.07784081367035, fit.LogLikelihood);
            Close(0.32725113728370986, fit.ParameterStandardErrors[2]);
            Assert.True(fit.Reml);
            Assert.True(fit.Converged);
            Assert.False(fit.Singular);
            Assert.Equal(16, fit.GroupCount);
            Assert.Equal(6, fit.MinGroupSize);
            Assert.Equal(6, fit.MaxGroupSize);
            Assert.NotNull(fit.Icc);
            Close(0.5186156074140765 / (0.5186156074140765 + 0.7917244576380352), fit.Icc!.Value);
        }

        [Fact]
        public void Random_intercept_ML_matches_statsmodels()
        {
            // smf.mixedlm("y ~ x", df, groups=df["g"]).fit(reml=False)
            var fit = FitRi(reml: false);
            Close(2.083649571847678, fit.Coefficients[0].Estimate);
            Close(0.7289686438615448, fit.Coefficients[1].Estimate);
            Close(0.19489058506596377, fit.Coefficients[0].StdError);
            Close(0.07805509407148399, fit.Coefficients[1].StdError);
            Close(0.4771009653345143, fit.RandomCovariance[0, 0]);
            Close(0.7824137048093048, fit.Scale);
            Close(-136.7501271408605, fit.LogLikelihood);
            Close(0.3008656876430665, fit.ParameterStandardErrors[2]);
            Close(281.500254281721, fit.Aic);
            Close(291.7576470475924, fit.Bic);
            Assert.False(fit.Reml);
        }

        [Fact]
        public void Random_slope_REML_matches_statsmodels()
        {
            // smf.mixedlm("y ~ x + z", df, groups=df["g"], re_formula="~x").fit(reml=True)
            var fit = FitRs(reml: true);
            Close(1.4885828534452872, Coef(fit, "Intercept"));
            Close(0.4768490956546972, Coef(fit, "x"));
            Close(-0.27173758831865324, Coef(fit, "z"));
            Close(0.15795805098345636, Se(fit, "Intercept"));
            Close(0.08250468166165757, Se(fit, "x"));
            Close(0.2019511419598165, Se(fit, "z"));
            Close(0.4026067182797198, fit.RandomCovariance[0, 0]);
            Close(0.10276015508436433, fit.RandomCovariance[0, 1]);
            Close(0.038391713866443564, fit.RandomCovariance[1, 1]);
            Close(0.5741726068092635, fit.Scale);
            Close(-156.842988413672, fit.LogLikelihood);
            Close(0.3133483038572515, fit.ParameterStandardErrors[3]);
            Close(0.11515385704128783, fit.ParameterStandardErrors[4]);
            Close(0.08656715433650984, fit.ParameterStandardErrors[5]);
            Assert.Null(fit.Icc);
            Assert.True(fit.Converged);
        }

        [Fact]
        public void Random_slope_ML_matches_statsmodels()
        {
            // smf.mixedlm("y ~ x + z", df, groups=df["g"], re_formula="~x").fit(reml=False)
            var fit = FitRs(reml: false);
            Close(1.489066577100635, Coef(fit, "Intercept"));
            Close(0.4762014066563449, Coef(fit, "x"));
            Close(-0.2744902272080517, Coef(fit, "z"));
            Close(0.15400092663140036, Se(fit, "Intercept"));
            Close(0.07982278378222231, Se(fit, "x"));
            Close(0.1996983442693042, Se(fit, "z"));
            Close(0.37879910981315346, fit.RandomCovariance[0, 0]);
            Close(0.09868109358134683, fit.RandomCovariance[1, 0]);
            // 가장 작은 분산성분은 우도가 평탄하다. 우리 llf가 statsmodels보다 3.6e-8 더 높다
            // (같은 극대). 상대오차 2.2e-4는 그 평탄성이지 다른 모형이 아니다.
            Close(0.03057854834707854, fit.RandomCovariance[1, 1], 3e-4);
            Close(0.5697555648987446, fit.Scale);
            Close(-153.52766614207957, fit.LogLikelihood);
            Close(321.05533228415914, fit.Aic);
            Close(340.56777448363346, fit.Bic);
            Close(0.2938065895063956, fit.ParameterStandardErrors[3], 2e-4);
        }

        [Fact]
        public void Factor_fixed_effect_REML_matches_statsmodels_treatment_coding()
        {
            // smf.mixedlm("y ~ C(fac) + x", df, groups=df["g"]).fit(reml=True)
            // patsy 열 순서 Intercept, C(fac)[T.b], C(fac)[T.c], x. 설계행렬은 fac[T.b] 이름.
            var rows = FactorRows();
            var fit = MixedModel.Fit(rows, new[] { "y", "x", "fac", "g" }, ModelFormula.Parse("y ~ C(fac) + x"),
                c => c == 2 || c == 3 ? VariableKind.Categorical : VariableKind.Numeric, 3, Array.Empty<int>(), reml: true);
            Assert.Equal("Intercept", fit.Coefficients[0].Name);
            Assert.Equal("fac[T.b]", fit.Coefficients[1].Name);
            Assert.Equal("fac[T.c]", fit.Coefficients[2].Name);
            Assert.Equal("x", fit.Coefficients[3].Name);
            Close(2.9382345916370634, fit.Coefficients[0].Estimate);
            Close(1.3016139875574793, fit.Coefficients[1].Estimate);
            Close(-0.5101192854389779, fit.Coefficients[2].Estimate);
            Close(0.5096946507235478, fit.Coefficients[3].Estimate);
            Close(0.24873577783660203, fit.Coefficients[0].StdError);
            Close(0.8589325175919266, fit.RandomCovariance[0, 0]);
            Close(0.4084049306722539, fit.Scale);
            Close(-111.67040715522654, fit.LogLikelihood);
        }

        [Fact]
        public void One_group_and_constant_slope_are_rejected()
        {
            var rows = RiRows().Select(r => new[] { r[0], r[1], "only" }).ToList();
            var ex = Assert.Throws<DesignMatrixException>(() =>
                MixedModel.Fit(rows, RiHeaders, ModelFormula.Parse("y ~ x"), _ => VariableKind.Numeric, 2, Array.Empty<int>()));
            Assert.Contains("two groups", ex.Message, StringComparison.OrdinalIgnoreCase);

            var flat = RsRows().Select(r => new[] { r[0], "3", r[2], r[3] }).ToList();
            var slope = Assert.Throws<DesignMatrixException>(() =>
                MixedModel.Fit(flat, RsHeaders, ModelFormula.Parse("y ~ z"), _ => VariableKind.Numeric, 3, new[] { 1 }));
            Assert.Contains("dependent", slope.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Result_text_states_scope_criterion_and_REML_AIC_note()
        {
            var fit = FitRi(reml: true);
            var text = NanumCsvViewer.Form1.FormatLmmResult(ModelFormula.Parse("y ~ x"), "g", Array.Empty<string>(), fit);
            Assert.Contains("REML", text);
            Assert.Contains("96", text);
            Assert.Contains("ICC", text);
            Assert.Contains("AIC", text);
            Assert.Contains("statsmodels", text);
        }

        [Fact]
        public void Nls_exponential_matches_scipy_and_zero_random_effects_use_that_fit()
        {
            // scipy.optimize.least_squares, method="lm", y = 8*exp(-0.5*x) + ((i*7+3)%11-5)/8, x = i%8, i=0..47
            var (y, x, g) = ExponentialRows();
            var none = NonlinearMixedModel.FitNumeric(y, x, g, 6, NonlinearMean.ExponentialDecay, new[] { false, false });
            Close(8.000529690993305, none.Estimates[0], 1e-4);
            Close(0.49997042917820433, none.Estimates[1], 1e-4);
            Assert.True(none.Converged);
            Assert.Contains("nonlinear least squares", none.StandardErrorMethod, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(none.RandomEffectNames);

            // 임의효과 분산이 0인 자료: 임의 절편(a)을 켜도 고정효과는 NLS와 가깝다.
            var mixed = NonlinearMixedModel.FitNumeric(y, x, g, 6, NonlinearMean.ExponentialDecay, new[] { true, false });
            Close(none.Estimates[0], mixed.Estimates[0], 0.02);
            Close(none.Estimates[1], mixed.Estimates[1], 0.02);
            Assert.Contains("Lindstrom", mixed.StandardErrorMethod);
        }

        [Fact]
        public void Logistic_growth_recovers_simulated_parameters()
        {
            // 그룹 24, x=0..7, a=10, b=3.5, c=1.25, 임의효과 sd(a)=0.4, 잔차 sd=0.15. 시드 11.
            var rng = new Random(11);
            var y = new List<double>();
            var x = new List<double>();
            var g = new List<int>();
            for (int group = 0; group < 24; group++)
            {
                double ba = NextGaussian(rng) * 0.4;
                for (int t = 0; t < 8; t++)
                {
                    double xv = t;
                    double mean = (10 + ba) / (1 + Math.Exp((3.5 - xv) / 1.25));
                    y.Add(mean + NextGaussian(rng) * 0.15);
                    x.Add(xv);
                    g.Add(group);
                }
            }
            var fit = NonlinearMixedModel.FitNumeric(y.ToArray(), x.ToArray(), g.ToArray(), 24,
                NonlinearMean.LogisticGrowth, new[] { true, false, false });
            Assert.True(fit.Converged);
            Close(10, fit.Estimates[0], 0.08);
            Close(3.5, fit.Estimates[1], 0.08);
            Close(1.25, fit.Estimates[2], 0.12);
            Assert.True(fit.RandomCovariance[0, 0] > 0.02);
            Assert.True(fit.RandomCovariance[0, 0] < 0.5);
        }

        [Fact]
        public void Exponential_decay_underflow_is_zero_and_recovers_unit_rate()
        {
            // (0, 1), (1, e^-1), (2, e^-2), (1000, 0) 은 a=1, b=1의 정확한 값.
            // x=1000에서 exp(-1000)은 언더플로 0이지 1e6이 아니다. 야코비안도 같은 식이어야 이 해에 닿는다.
            double[] p = { 1, 1 };
            Assert.Equal(1, NonlinearMixedModel.Mean(NonlinearMean.ExponentialDecay, 0, p));
            Assert.Equal(Math.Exp(-1), NonlinearMixedModel.Mean(NonlinearMean.ExponentialDecay, 1, p));
            Assert.Equal(Math.Exp(-2), NonlinearMixedModel.Mean(NonlinearMean.ExponentialDecay, 2, p));
            Assert.Equal(0, NonlinearMixedModel.Mean(NonlinearMean.ExponentialDecay, 1000, p));
            Assert.False(double.IsFinite(NonlinearMixedModel.Mean(NonlinearMean.ExponentialDecay, -1000, p)));

            double[] x = { 0, 1, 2, 1000 };
            double[] y = { 1, Math.Exp(-1), Math.Exp(-2), 0 };
            int[] g = { 0, 0, 0, 0 };
            var fit = NonlinearMixedModel.FitNumeric(y, x, g, 1, NonlinearMean.ExponentialDecay, new[] { false, false });
            Assert.True(fit.Converged);
            Close(1, fit.Estimates[0], 1e-6);
            Close(1, fit.Estimates[1], 1e-6);
        }

        [Fact]
        public void Nlmm_result_text_does_not_rewrite_the_x_inside_exp()
        {
            var text = NanumCsvViewer.Form1.FormatNlmmResult("conc", "time", "id", BareNlmm(NonlinearMean.ExponentialDecay));
            Assert.Contains("exp(", text);
            Assert.Contains("time", text);
            Assert.DoesNotContain("etimep", text);
            Assert.DoesNotContain("exptime", text);

            var mm = NanumCsvViewer.Form1.FormatNlmmResult("y", "dose", "g", BareNlmm(NonlinearMean.MichaelisMenten));
            Assert.Contains("dose", mm);
            Assert.Contains("dose / (b + dose)", mm);
        }

        private static NonlinearMixedResult BareNlmm(NonlinearMean model) => new()
        {
            Model = model,
            Formula = NonlinearMixedModel.Formula(model),
            ParameterNames = NonlinearMixedModel.ParameterNames(model),
            Estimates = new double[NonlinearMixedModel.ParameterCount(model)],
            StdErrors = new double[NonlinearMixedModel.ParameterCount(model)],
            Random = new bool[NonlinearMixedModel.ParameterCount(model)],
            RandomCovariance = new double[0, 0],
            RandomEffectNames = Array.Empty<string>(),
            Scale = 1,
            Converged = true,
            Singular = false,
            Iterations = 0,
            StandardErrorMethod = NonlinearMixedModel.SeNls,
            GroupCount = 1,
            MinGroupSize = 1,
            MaxGroupSize = 1,
            RowsRead = 1,
            RowsUsed = 1,
            RowsDropped = 0,
        };

        private static MixedModelResult FitRi(bool reml) =>
            MixedModel.Fit(RiRows(), RiHeaders, ModelFormula.Parse("y ~ x"), _ => VariableKind.Numeric, 2, Array.Empty<int>(), reml);

        private static MixedModelResult FitRs(bool reml) =>
            MixedModel.Fit(RsRows(), RsHeaders, ModelFormula.Parse("y ~ x + z"), _ => VariableKind.Numeric, 3, new[] { 1 }, reml);

        private static List<string[]> RiRows()
        {
            var rows = new List<string[]>();
            for (int i = 0; i < 96; i++)
            {
                int g = i / 6;
                double x = (i % 9) * 0.5 - 2.0;
                double u = ((g * 17 + 3) % 11 - 5) / 4.0;
                double e = ((i * 13 + 7) % 23 - 11) / 8.0;
                double y = 2.0 + 0.75 * x + u + e;
                rows.Add(new[] { Num(y), Num(x), g.ToString(CultureInfo.InvariantCulture) });
            }
            return rows;
        }

        private static List<string[]> RsRows()
        {
            var rows = new List<string[]>();
            for (int i = 0; i < 120; i++)
            {
                int g = i / 6;
                double x = (i % 7) * 0.5 - 1.5;
                double z = (i % 5) * 0.25 - 0.5;
                double u0 = ((g * 5 + 1) % 9 - 4) / 4.0;
                double u1 = ((g * 3 + 2) % 7 - 3) / 8.0;
                double e = ((i * 11 + 4) % 19 - 9) / 8.0;
                double y = 1.5 + 0.5 * x - 0.25 * z + u0 + u1 * x + e;
                rows.Add(new[] { Num(y), Num(x), Num(z), g.ToString(CultureInfo.InvariantCulture) });
            }
            return rows;
        }

        private static List<string[]> FactorRows()
        {
            var rows = new List<string[]>();
            for (int i = 0; i < 90; i++)
            {
                int g = i / 5;
                double x = (i % 8) * 0.5 - 1.5;
                string fac = new[] { "a", "b", "c" }[i % 3];
                double u = ((g * 7 + 2) % 13 - 6) / 4.0;
                double e = ((i * 9 + 1) % 17 - 8) / 8.0;
                double level = fac == "b" ? 1.25 : fac == "c" ? -0.5 : 0;
                double y = 3.0 + level + 0.5 * x + u + e;
                rows.Add(new[] { Num(y), Num(x), fac, g.ToString(CultureInfo.InvariantCulture) });
            }
            return rows;
        }

        private static (double[] Y, double[] X, int[] G) ExponentialRows()
        {
            var y = new double[48];
            var x = new double[48];
            var g = new int[48];
            for (int i = 0; i < 48; i++)
            {
                x[i] = i % 8;
                double e = ((i * 7 + 3) % 11 - 5) / 8.0;
                y[i] = 8.0 * Math.Exp(-0.5 * x[i]) + e;
                g[i] = i / 8;
            }
            return (y, x, g);
        }

        private static double NextGaussian(Random rng)
        {
            double u1 = 1 - rng.NextDouble();
            double u2 = 1 - rng.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Sin(2 * Math.PI * u2);
        }

        private static string Num(double v) => v.ToString("G17", CultureInfo.InvariantCulture);
        private static double Coef(MixedModelResult fit, string name) => fit.Coefficients.Single(c => c.Name == name).Estimate;
        private static double Se(MixedModelResult fit, string name) => fit.Coefficients.Single(c => c.Name == name).StdError;

        private static void Close(double expected, double actual, double rel = 1e-4)
        {
            double tol = Math.Max(1e-8, rel * Math.Abs(expected));
            Assert.True(Math.Abs(actual - expected) <= tol, $"expected {expected:G17} actual {actual:G17} tol {tol:G3}");
        }
    }
}
