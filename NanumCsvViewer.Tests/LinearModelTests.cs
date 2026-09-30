using System.Globalization;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 일반선형모형·Type II·ANCOVA. 참조값은 statsmodels 0.14.5 / scipy 1.15.
    // 자료: i=0..119, g=a(40)/b(50)/c(30), x=(i%17)*0.5-4, z=(i%13)*0.25-1.5,
    // noise=((i*17+3)%97-48)/64, y = base[g] + slope[g]*x + 0.375*z + noise
    // (base a/b/c = 3/4.5/2.25, slope = 1.25/1.75/0.75). 이진 유리수라 C#·Python 값이 같다.
    public class LinearModelTests
    {
        private static readonly string[] Headers = { "y", "x", "z", "g" };

        private static VariableKind Kind(int c) => c == 3 ? VariableKind.Categorical : VariableKind.Numeric;

        // statsmodels.formula.api.ols(...).fit() / anova_lm(..., typ=2) 로 생성.
        private const double Intercept = 3.00283623547873;
        private const double GB = 1.4667587971186937;
        private const double GC = -0.7555815856963762;
        private const double X = 1.277709055706004;
        private const double Xb = 0.485789857713266;
        private const double Xc = -0.5259035551124173;
        private const double SeIntercept = 0.09166370463020766;
        private const double SeGB = 0.12224849924590764;
        private const double SeGC = 0.13987759586401294;
        private const double SeX = 0.03639354621839117;
        private const double SeXb = 0.04900447418041313;
        private const double SeXc = 0.057148663135759115;

        private static List<string[]> ReferenceRows()
        {
            var rows = new List<string[]>(120);
            for (int i = 0; i < 120; i++)
            {
                string g = i < 40 ? "a" : i < 90 ? "b" : "c";
                double x = (i % 17) * 0.5 - 4.0;
                double z = (i % 13) * 0.25 - 1.5;
                double noise = ((i * 17 + 3) % 97 - 48) / 64.0;
                double b0 = g == "a" ? 3.0 : g == "b" ? 4.5 : 2.25;
                double slope = g == "a" ? 1.25 : g == "b" ? 1.75 : 0.75;
                double y = b0 + slope * x + 0.375 * z + noise;
                rows.Add(new[] { Num(y), Num(x), Num(z), g });
            }
            return rows;
        }

        private static string Num(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

        private static void Close(double expected, double actual, double rel = 1e-8)
        {
            if (double.IsNaN(expected))
            {
                Assert.True(double.IsNaN(actual), $"expected NaN, got {actual}");
                return;
            }
            if (double.IsPositiveInfinity(expected))
            {
                Assert.True(double.IsPositiveInfinity(actual), $"expected +Inf, got {actual}");
                return;
            }
            double denom = Math.Max(Math.Abs(expected), 1e-12);
            double err = Math.Abs(actual - expected) / denom;
            Assert.True(err <= rel, $"expected {expected:G17} actual {actual:G17} relerr {err:G3}");
        }

        private static DesignMatrix Build(string formula, List<string[]>? rows = null)
            => DesignMatrixBuilder.Build(rows ?? ReferenceRows(), Headers, ModelFormula.Parse(formula), Kind);

        [Fact]
        public void Reference_rows_match_the_python_generator()
        {
            var rows = ReferenceRows();
            Assert.Equal("-3.265625", rows[0][0]);
            Assert.Equal("-4", rows[0][1]);
            Assert.Equal("-1.5", rows[0][2]);
            Assert.Equal("a", rows[0][3]);
            Assert.Equal("b", rows[40][3]);
        }

        [Fact]
        public void Coefficients_standard_errors_and_intervals_match_statsmodels_ols()
        {
            // smf.ols("y ~ x * C(g)", df).fit() — params, bse, conf_int(0.05)
            var dm = Build("y ~ x * C(g)");
            var fit = LinearModel.Fit(dm);
            Assert.Equal(120, fit.N);
            Assert.Equal(6, fit.Rank);
            Assert.Equal(114, fit.DfResidual);
            Assert.Equal(5, fit.DfModel);

            var expected = new Dictionary<string, (double B, double Se, double Lo, double Hi)>
            {
                ["Intercept"] = (Intercept, SeIntercept, 2.8212511426058704, 3.18442132835159),
                ["g[T.b]"] = (GB, SeGB, 1.2245854616781315, 1.708932132559256),
                ["g[T.c]"] = (GC, SeGC, -1.0326780257570012, -0.4784851456357514),
                ["x"] = (X, SeX, 1.2056137209527384, 1.3498043904592696),
                ["x:g[T.b]"] = (Xb, SeXb, 0.3887123717973902, 0.5828673436291418),
                ["x:g[T.c]"] = (Xc, SeXc, -0.6391146166217812, -0.4126924936030534),
            };
            Assert.Equal(expected.Count, fit.Coefficients.Count);
            foreach (var c in fit.Coefficients)
            {
                Assert.False(c.Aliased);
                var e = expected[c.Name];
                Close(e.B, c.Estimate);
                Close(e.Se, c.StdError);
                Close(e.B / e.Se, c.T);
                Close(e.Lo, c.CiLow);
                Close(e.Hi, c.CiHigh);
                Assert.InRange(c.PValue, 0, 1);
            }
        }

        [Fact]
        public void Fit_statistics_match_statsmodels_ols()
        {
            // smf.ols("y ~ x * C(g)", df).fit() — rsquared, rsquared_adj, fvalue, f_pvalue, llf, aic, bic, ssr, mse_resid
            var fit = LinearModel.Fit(Build("y ~ x * C(g)"));
            Close(37.28650193696008, fit.Rss);
            Close(0.9766955179874132, fit.RSquared);
            Close(0.9756733915833523, fit.AdjustedRSquared);
            Close(955.5525755983599, fit.FStatistic);
            Close(2.839575051286243e-91, fit.FPValue, 1e-6);
            Close(-100.14100238505664, fit.LogLikelihood);
            Close(212.28200477011328, fit.Aic);
            Close(229.00695522680556, fit.Bic);
            Close(0.5719043437449891, fit.ResidualSe);
            // numpy.quantile(resid, [0, .25, .5, .75, 1], method='linear')
            Close(-1.157625012654714, fit.Residuals.Min, 1e-6);
            Close(-0.40931391842001474, fit.Residuals.Q1, 1e-6);
            Close(0.01368130943600887, fit.Residuals.Median, 1e-6);
            Close(0.3877466800637074, fit.Residuals.Q3, 1e-6);
            Close(1.3656506210796562, fit.Residuals.Max, 1e-6);
            Assert.True(Math.Abs(fit.Residuals.Mean) < 1e-8);
        }

        [Fact]
        public void TypeII_sums_of_squares_match_anova_lm()
        {
            // statsmodels.stats.anova.anova_lm(fit, typ=2) on y ~ x * C(g)
            // Type I SS for C(g) is 119.819, so a sequential table would fail this check.
            var fit = LinearModel.Fit(Build("y ~ x * C(g)"));
            var table = LinearModel.TypeII(Build("y ~ x * C(g)"), fit);
            var byName = table.ToDictionary(t => t.Name);

            Close(120.45342086731523, byName["g"].SumOfSquares);
            Assert.Equal(2, byName["g"].Df);
            Close(184.13754663939739, byName["g"].F);
            Close(1.9759274983868977e-36, byName["g"].PValue, 1e-6);

            Close(1329.4310493389949, byName["x"].SumOfSquares);
            Assert.Equal(1, byName["x"].Df);
            Close(4064.611367429377, byName["x"].F);
            Close(5.286769417228821e-91, byName["x"].PValue, 1e-6);

            Close(113.43471597664683, byName["x:g"].SumOfSquares);
            Assert.Equal(2, byName["x:g"].Df);
            Close(173.40802903958377, byName["x:g"].F);
            Close(2.645471372752046e-35, byName["x:g"].PValue, 1e-6);

            Close(37.28650193696008, fit.Rss);
            Assert.Equal(114, fit.DfResidual);
        }

        [Fact]
        public void Duplicate_predictor_is_aliased_and_kept_coefficients_match_reduced_ols()
        {
            // smf.ols("y ~ x", df).fit() — the duplicate column is dropped by the sequential Cholesky, like R.
            var rows = ReferenceRows().Select(r => new[] { r[0], r[1], r[1] }).ToList();
            var headers = new[] { "y", "x", "x2" };
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x + x2"), _ => VariableKind.Numeric);
            var fit = LinearModel.Fit(dm);
            Assert.Equal(new[] { "Intercept", "x", "x2" }, dm.ColumnNames);
            Assert.Equal(2, fit.Rank);
            Assert.False(fit.Coefficients[0].Aliased);
            Assert.False(fit.Coefficients[1].Aliased);
            Assert.True(fit.Coefficients[2].Aliased);
            Assert.True(double.IsNaN(fit.Coefficients[2].Estimate));
            Assert.True(double.IsNaN(fit.Coefficients[2].StdError));
            Assert.True(double.IsNaN(fit.Coefficients[2].PValue));
            Close(3.3853411439303986, fit.Coefficients[0].Estimate);
            Close(0.13839887940100534, fit.Coefficients[0].StdError);
            Close(1.3492968179119464, fit.Coefficients[1].Estimate);
            Close(0.05611277756391715, fit.Coefficients[1].StdError);
            Close(0.8305128085647671, fit.RSquared);

            var text = Form1_Format(dm, fit);
            Assert.Contains("x2", text);
            Assert.True(text.Contains("aliased", StringComparison.OrdinalIgnoreCase) || text.Contains("별칭", StringComparison.Ordinal));
        }

        [Fact]
        public void No_intercept_r_squared_uses_uncentered_total_sum_of_squares()
        {
            // smf.ols("y ~ x - 1", df).fit() — k_constant 0, rsquared = 1 - ssr/sum(y²)
            var dm = Build("y ~ x + 0");
            Assert.False(dm.HasIntercept);
            var fit = LinearModel.Fit(dm);
            Assert.Equal(1, fit.Rank);
            Assert.Equal(119, fit.DfResidual);
            Assert.Equal(1, fit.DfModel);
            Close(1646.1876082746949, fit.Rss);
            Close(0.43986944183750376, fit.RSquared);
            Close(0.4351624623571466, fit.AdjustedRSquared);
            Close(93.45046938767015, fit.FStatistic);
            Close(1.1490675060884841e-16, fit.FPValue, 1e-6);
            Close(-327.39616059989794, fit.LogLikelihood);
            Close(656.7923211997959, fit.Aic);
            Close(659.579812942578, fit.Bic);
            Close(1.3307470034246582, fit.Coefficients.Single(c => c.Name == "x").Estimate);
            Close(0.13765904531796325, fit.Coefficients.Single(c => c.Name == "x").StdError);
        }

        [Fact]
        public void Ancova_adjusted_means_and_pairwise_match_manual_statsmodels_contrasts()
        {
            // smf.ols("y ~ C(g) + x + z", df).fit()
            // EMM at covariate means (−1/30, −0.03125): Intercept + 1_{level}·β_g + mean_x·β_x + mean_z·β_z
            // t critical from scipy.stats.t.ppf(0.975, 115) = 1.9808075410672
            var dm = Build("y ~ C(g) + x + z");
            var result = LinearModel.Ancova(dm, "g");
            var fit = result.Additive;
            Assert.Equal(5, fit.Rank);
            Assert.Equal(115, fit.DfResidual);
            Close(140.81118874966717, fit.Rss);
            Close(0.9119914273284299, fit.RSquared);
            Close(0.9089302595833317, fit.AdjustedRSquared);
            Close(297.92272206866807, fit.FStatistic);
            Close(369.73662756947624, fit.Aic);
            Close(383.6740862833865, fit.Bic);

            Close(3.047130738006129, Coef(fit, "Intercept"));
            Close(1.4346468633130218, Coef(fit, "g[T.b]"));
            Close(-0.9990992651497584, Coef(fit, "g[T.c]"));
            Close(1.3574132893615858, Coef(fit, "x"));
            Close(0.3044401638526683, Coef(fit, "z"));

            var anova = result.TypeII.ToDictionary(t => t.Name);
            Close(118.1272097206287, anova["g"].SumOfSquares);
            Close(48.2370372642153, anova["g"].F);
            Close(0.45619811668905363, anova["g"].PartialEtaSquared);
            Close(1324.5314854782553, anova["x"].SumOfSquares);
            Close(1081.7401811783184, anova["x"].F);
            Close(0.9039056247891917, anova["x"].PartialEtaSquared);
            Close(9.910029163939658, anova["z"].SumOfSquares);
            Close(8.093485780303482, anova["z"].F);
            Close(0.005260488634771432, anova["z"].PValue);
            Close(0.06575072376087135, anova["z"].PartialEtaSquared);

            Close(-0.03333333333333333, result.CovariateMeans[result.Covariates.ToList().IndexOf("x")]);
            Close(-0.03125, result.CovariateMeans[result.Covariates.ToList().IndexOf("z")]);

            var means = result.AdjustedMeans.ToDictionary(m => m.Level);
            Assert.Equal(40, means["a"].Count);
            Assert.Equal(50, means["b"].Count);
            Assert.Equal(30, means["c"].Count);
            Close(2.9923698732403468, means["a"].Estimate);
            Close(0.17565892396869753, means["a"].StdError);
            Close(2.6444233519874007, means["a"].CiLow);
            Close(3.3403163944932928, means["a"].CiHigh);
            Close(4.4270167365533695, means["b"].Estimate);
            Close(0.1565457820495467, means["b"].StdError);
            Close(1.9932706080905886, means["c"].Estimate);
            Close(0.20276158973310487, means["c"].StdError);

            var pairs = result.Pairwise.ToDictionary(d => d.LevelA + "-" + d.LevelB);
            Close(1.4346468633130218, pairs["a-b"].Difference);
            Close(0.23547152957679351, pairs["a-b"].StdError);
            Close(1.5150933946936516e-08, pairs["a-b"].PValue, 1e-6);
            Close(4.545280184080955e-08, pairs["a-b"].BonferroniP, 1e-6);
            Close(-0.9990992651497584, pairs["a-c"].Difference);
            Close(-2.43374612846278, pairs["b-c"].Difference);
            Close(0.2560576238094456, pairs["b-c"].StdError);
            Close(-9.504681377008868, pairs["b-c"].T);
            Close(3.692849090489771e-16, pairs["b-c"].PValue, 1e-6);
            Close(1.1078547271469313e-15, pairs["b-c"].BonferroniP, 1e-6);

            var text = NanumCsvViewer.Form1.FormatAncovaResult(dm, result);
            Assert.Contains("Type II", text);
            Assert.Contains("η²p", text);
            Assert.Contains("Shapiro", text);
            Assert.Contains(dm.Formula.ToString(), text);
        }

        [Fact]
        public void Slopes_homogeneity_matches_nested_anova_lm()
        {
            // anova_lm(ols("y ~ C(g)+x+z"), ols("y ~ C(g)+x+z+C(g):x+C(g):z"))
            var result = LinearModel.Ancova(Build("y ~ C(g) + x + z"), "g");
            Assert.Equal(4, result.Slopes.DfNumerator);
            Assert.Equal(111, result.Slopes.DfDenominator);
            Close(140.81118874966717, result.Slopes.RssReduced);
            Close(22.16161380649769, result.Slopes.RssFull);
            Close(118.64957494316948, result.Slopes.RssReduced - result.Slopes.RssFull);
            Close(148.56886025635907, result.Slopes.F);
            Close(1.29010453834331e-43, result.Slopes.PValue, 1e-6);
            Assert.Equal(5, result.Slopes.RankReduced);
            Assert.Equal(9, result.Slopes.RankFull);
        }

        [Fact]
        public void Non_numeric_response_and_single_level_factor_are_rejected_with_a_message()
        {
            var formula = ModelFormula.Parse("y ~ x");
            var ex = Assert.Throws<DesignMatrixException>(() =>
                LinearModel.EnsureNumericResponse(Headers, formula, c => c == 0 ? VariableKind.Categorical : VariableKind.Numeric));
            Assert.Contains("numeric", ex.Message, StringComparison.OrdinalIgnoreCase);

            var oneLevel = ReferenceRows().Select(r => new[] { r[0], r[1], r[2], "a" }).ToList();
            var factor = Assert.Throws<DesignMatrixException>(() => Build("y ~ C(g) + x", oneLevel));
            Assert.Contains("one level", factor.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Ancova_rejects_a_model_that_already_has_interactions()
        {
            var ex = Assert.Throws<DesignMatrixException>(() => LinearModel.Ancova(Build("y ~ x * C(g)"), "g"));
            Assert.Contains("main effects", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Residual_normality_uses_only_the_first_5000_residuals()
        {
            var rows = new List<string[]>(5001);
            for (int i = 0; i < 5001; i++)
                rows.Add(new[] { Num(i + (i % 5) * 0.2), Num(i * 0.5) });
            var dm = DesignMatrixBuilder.Build(rows, new[] { "y", "x" }, ModelFormula.Parse("y ~ x"), _ => VariableKind.Numeric);
            var fit = LinearModel.Fit(dm);
            Assert.NotNull(fit.Normality);
            Assert.True(fit.Normality!.Capped);
            Assert.Equal(5000, fit.Normality.SampleSize);
            Assert.InRange(fit.Normality.W, 0, 1);
            Assert.InRange(fit.Normality.PValue, 0, 1);

            var small = LinearModel.Fit(Build("y ~ x * C(g)"));
            Assert.NotNull(small.Normality);
            Assert.False(small.Normality!.Capped);
            Assert.Equal(120, small.Normality.SampleSize);
        }

        [Fact]
        public void Result_text_includes_scope_coefficients_anova_and_normality()
        {
            var dm = Build("y ~ x * C(g)");
            var fit = LinearModel.Fit(dm);
            var text = Form1_Format(dm, fit);
            Assert.Contains("120", text);
            Assert.Contains("R²", text);
            Assert.Contains("AIC", text);
            Assert.Contains("Type II", text);
            Assert.Contains("Shapiro", text);
            Assert.Contains("x:g", text);
            Assert.Contains(dm.Formula.ToString(), text);
        }

        private static double Coef(LinearModelFit fit, string name)
            => fit.Coefficients.Single(c => c.Name == name).Estimate;

        // 결과 텍스트는 Form1의 정적 포맷터. 메뉴 핸들러와 같은 표를 쓴다.
        private static string Form1_Format(DesignMatrix dm, LinearModelFit fit)
            => NanumCsvViewer.Form1.FormatGlmResult(dm, fit, LinearModel.TypeII(dm, fit));
    }
}
