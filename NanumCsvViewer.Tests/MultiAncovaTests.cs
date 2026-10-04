using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // statsmodels 0.14.5: ols("y ~ C(g)+C(h)+x1+x2"), anova_lm(typ=2), get_prediction 평균(수준 동일 가중), anova_lm(reduced, full).
    public class MultiAncovaTests
    {
        static readonly List<string[]> Rows = new()
        {
            new[] { "0.898", "a", "v", "0.079", "3.599" },
            new[] { "1.124", "c", "u", "-0.197", "0.052" },
            new[] { "1.335", "b", "v", "-0.268", "1.383" },
            new[] { "0.314", "b", "u", "1.34", "4.06" },
            new[] { "1.219", "b", "v", "1.424", "4.992" },
            new[] { "-0.649", "c", "v", "-1.216", "1.404" },
            new[] { "2.934", "b", "v", "1.174", "3.805" },
            new[] { "-0.052", "a", "v", "-1.723", "0.565" },
            new[] { "-0.408", "a", "v", "-0.734", "3.739" },
            new[] { "1.047", "b", "u", "-0.173", "1.783" },
            new[] { "2.972", "b", "u", "0.111", "0.387" },
            new[] { "-1.077", "c", "u", "-1.047", "0.879" },
            new[] { "1.082", "a", "u", "-0.722", "2.838" },
            new[] { "0.725", "b", "v", "0.077", "2.362" },
            new[] { "-1.578", "c", "u", "-0.693", "3.437" },
            new[] { "0.82", "a", "u", "-0.641", "1.742" },
            new[] { "0.756", "c", "u", "0.666", "2.982" },
            new[] { "0.627", "c", "v", "0.932", "1.878" },
            new[] { "-0.709", "c", "u", "-0.06", "3.841" },
            new[] { "-0.104", "c", "u", "0.018", "0.033" },
            new[] { "1.252", "c", "v", "-0.02", "2.945" },
            new[] { "0.995", "a", "v", "-0.454", "2.938" },
            new[] { "-0.609", "c", "u", "-0.511", "0.291" },
            new[] { "-0.98", "c", "u", "-1.038", "3.823" },
            new[] { "1.799", "b", "v", "0.648", "3.173" },
            new[] { "-0.964", "c", "u", "-0.084", "4.751" },
            new[] { "0.974", "c", "v", "-0.137", "0.936" },
            new[] { "-0.041", "a", "u", "0.201", "2.125" },
            new[] { "0.152", "c", "u", "0.531", "4.325" },
            new[] { "1.481", "c", "v", "1.421", "4.721" },
            new[] { "1.079", "b", "u", "-0.274", "3.426" },
            new[] { "0.855", "c", "u", "-0.401", "1.764" },
            new[] { "-0.627", "c", "u", "0.201", "3.719" },
            new[] { "-1.841", "a", "u", "-0.846", "2.271" },
            new[] { "0.727", "a", "u", "1.272", "2.673" },
            new[] { "0.926", "a", "u", "0.926", "3.386" },
            new[] { "1.782", "b", "v", "0.694", "4.65" },
            new[] { "1.769", "c", "v", "-0.12", "0.044" },
            new[] { "2.249", "b", "u", "0.74", "3.499" },
            new[] { "1.951", "b", "v", "1.05", "4.573" },
            new[] { "0.328", "c", "u", "0.5", "1.872" },
            new[] { "1.033", "b", "v", "-0.28", "1.184" },
            new[] { "0.53", "a", "v", "-1.685", "2.849" },
            new[] { "2.349", "b", "u", "0.947", "0.15" },
            new[] { "-1.973", "c", "u", "-0.353", "4.025" },
            new[] { "0.079", "b", "v", "-0.843", "2.441" },
            new[] { "0.603", "c", "v", "0.891", "3.318" },
            new[] { "1.656", "a", "u", "0.338", "3.898" },
        };

        static MultiAncovaResult Fit()
        {
            var dm = DesignMatrixBuilder.Build(Rows, new[] { "y", "g", "h", "x1", "x2" },
                ModelFormula.Parse("y ~ C(g) + C(h) + x1 + x2"), c => c is 1 or 2 ? VariableKind.Categorical : VariableKind.Numeric);
            return LinearModel.AncovaMulti(dm);
        }

        static void Near(double expected, double actual, double tol = 1e-8)
            => Assert.InRange(Math.Abs(expected - actual), 0, tol * Math.Max(1, Math.Abs(expected)));

        [Fact]
        public void Type_ii_table_matches_statsmodels()
        {
            var r = Fit();
            var expected = new (string Name, double Ss, int Df, double F, double P)[]
            {
            ("g", 9.78033469106879, 2, 8.196679513040557, 0.0009876486455912623),
            ("h", 3.219598013690412, 1, 5.3965460104634495, 0.025092638192020497),
            ("x1", 14.147608697147266, 1, 23.71358813974269, 1.623599942452142e-05),
            ("x2", 6.0111291133629425, 1, 10.075585429348807, 0.002811122151289297),
            };
            foreach (var e in expected)
            {
                var term = Assert.Single(r.TypeII, t => t.Name == e.Name);
                Near(e.Ss, term.SumOfSquares);
                Assert.Equal(e.Df, term.Df);
                Near(e.F, term.F);
                Near(e.P, term.PValue);
            }
        }

        [Fact]
        public void Adjusted_means_average_the_other_factor_with_equal_weights()
        {
            var r = Fit();
            var g = Assert.Single(r.Factors, f => f.Factor == "g");
            var h = Assert.Single(r.Factors, f => f.Factor == "h");
            var gExpected = new (string L, double Est, double Se)[]
            {
            ("a", 0.8106568688767767, 0.23355198525656287),
            ("b", 1.203393391581332, 0.210119212456933),
            ("c", 0.12616587275991686, 0.17354630347860092)
            };
            var hExpected = new (string L, double Est, double Se)[]
            {
            ("u", 0.4436942730394152, 0.15473724730014674),
            ("v", 0.9831164824392685, 0.1718864408077443)
            };
            Check(g.AdjustedMeans, gExpected);
            Check(h.AdjustedMeans, hExpected);
        }

        static void Check(IReadOnlyList<AdjustedMean> actual, (string L, double Est, double Se)[] expected)
        {
            Assert.Equal(expected.Length, actual.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].L, actual[i].Level);
                Near(expected[i].Est, actual[i].Estimate);
                Near(expected[i].Se, actual[i].StdError);
            }
        }

        [Fact]
        public void Pairwise_differences_use_bonferroni_per_factor()
        {
            var r = Fit();
            var g = Assert.Single(r.Factors, f => f.Factor == "g");
            var expected = new (string A, string B, double D, double Se, double T, double P, double Bonf)[]
            {
            ("a", "b", 0.39273652270455517, 0.327696891530789, 1.1984749713979368, 0.23745112884463623, 0.7123533865339087),
            ("a", "c", -0.6844909961168599, 0.28686199298268716, -2.3861334469574387, 0.02161044271605042, 0.06483132814815126),
            ("b", "c", -1.077227518821415, 0.27818866772385414, -3.8722911599358616, 0.0003707294794169069, 0.0011121884382507207)
            };
            Assert.Equal(expected.Length, g.Pairwise.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].A, g.Pairwise[i].LevelA);
                Assert.Equal(expected[i].B, g.Pairwise[i].LevelB);
                Near(expected[i].D, g.Pairwise[i].Difference);
                Near(expected[i].Se, g.Pairwise[i].StdError);
                Near(expected[i].T, g.Pairwise[i].T);
                Near(expected[i].P, g.Pairwise[i].PValue);
                Near(expected[i].Bonf, g.Pairwise[i].BonferroniP);
            }
        }

        [Fact]
        public void Slope_homogeneity_tests_match_nested_f()
        {
            var r = Fit();
            Near(0.8692720878425001, Assert.Single(r.Factors, f => f.Factor == "g").Slopes.F);
            Near(0.4912474042283945, Assert.Single(r.Factors, f => f.Factor == "g").Slopes.PValue);
            Near(0.12414064165575626, Assert.Single(r.Factors, f => f.Factor == "h").Slopes.F);
            Near(0.5701218289192822, r.AllSlopes.F);
            Near(0.7512974713009313, r.AllSlopes.PValue);
        }
    }
}
