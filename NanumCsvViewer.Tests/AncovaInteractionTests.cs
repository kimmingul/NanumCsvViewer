using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // statsmodels 0.14.5: ols("y ~ C(g)*C(h)+x1+x2" / "y ~ C(a)*C(b)*C(c)+x"), anova_lm(typ=2),
    // 3요인 Type II는 statsmodels anova_lm(typ=2)가 열 기반 가설 검정이라 다중 상호작용에서 SAS 정의(RSS 차)와 달라질 수 있어,
    // 정의 그대로 중첩 OLS RSS 차 + 완전모형 MSE로 만든 값을 쓴다(2요인은 anova_lm와 일치). 아래 emmeans = patsy 설계행의 동일 가중 평균 대비(공변량은 평균), Bonferroni = min(1, p·쌍 수).
    public class AncovaInteractionTests
    {
        static readonly List<string[]> Rows2 = new()
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

        static readonly List<string[]> Rows3 = new()
        {
            new[] { "1.027", "p", "v", "r", "-0.222" },
            new[] { "0.92", "p", "w", "r", "-1.285" },
            new[] { "-0.377", "q", "w", "s", "-0.486" },
            new[] { "2.329", "q", "w", "s", "1.206" },
            new[] { "2.033", "p", "w", "s", "-0.191" },
            new[] { "0.941", "p", "w", "r", "-1.44" },
            new[] { "3.653", "p", "w", "s", "1.334" },
            new[] { "2.887", "p", "u", "r", "0.53" },
            new[] { "3.281", "q", "w", "r", "2.108" },
            new[] { "1.434", "q", "u", "s", "0.063" },
            new[] { "1.726", "q", "u", "r", "-0.461" },
            new[] { "-1.056", "q", "v", "s", "-1.448" },
            new[] { "2.276", "p", "u", "s", "1.324" },
            new[] { "2.888", "p", "v", "s", "2.57" },
            new[] { "-1.178", "q", "v", "s", "-0.821" },
            new[] { "1.824", "p", "v", "r", "-0.647" },
            new[] { "1.048", "p", "v", "s", "0.596" },
            new[] { "1.367", "q", "v", "r", "-0.83" },
            new[] { "0.426", "p", "u", "s", "-0.271" },
            new[] { "0.807", "q", "v", "s", "-0.35" },
            new[] { "1.439", "q", "v", "s", "0.192" },
            new[] { "1.272", "p", "w", "s", "1.095" },
            new[] { "2.21", "p", "w", "s", "0.022" },
            new[] { "1.354", "q", "u", "r", "0.919" },
            new[] { "2.682", "p", "w", "r", "-0.42" },
            new[] { "1.242", "q", "w", "r", "0.328" },
            new[] { "1.017", "q", "v", "r", "-2.138" },
            new[] { "0.88", "q", "u", "r", "-1.45" },
            new[] { "1.22", "q", "u", "s", "0.796" },
            new[] { "1.337", "q", "v", "r", "-0.59" },
            new[] { "2.55", "q", "v", "r", "0.58" },
            new[] { "3.062", "p", "v", "s", "0.542" },
            new[] { "0.887", "p", "u", "r", "1.322" },
            new[] { "2.289", "q", "u", "s", "0.812" },
            new[] { "2.574", "q", "w", "s", "1.017" },
            new[] { "1.65", "p", "u", "r", "-0.112" },
            new[] { "0.444", "p", "v", "r", "-0.698" },
            new[] { "2.491", "p", "w", "s", "-0.732" },
            new[] { "0.094", "q", "w", "s", "-0.488" },
            new[] { "1.002", "p", "u", "s", "-1.13" },
            new[] { "2.629", "q", "w", "s", "-0.547" },
            new[] { "-0.646", "q", "v", "s", "-0.093" },
            new[] { "0.823", "p", "v", "s", "0.252" },
            new[] { "0.121", "q", "w", "r", "-0.339" },
            new[] { "0.882", "q", "w", "r", "-1.924" },
            new[] { "3.878", "p", "w", "r", "-0.072" },
            new[] { "3.936", "p", "u", "r", "0.225" },
            new[] { "-0.236", "p", "u", "s", "1.084" },
            new[] { "1.514", "p", "v", "r", "0.578" },
            new[] { "1.382", "q", "u", "r", "-0.644" },
            new[] { "2.217", "q", "v", "r", "-0.724" },
            new[] { "2.426", "q", "u", "s", "2.011" },
            new[] { "0.633", "p", "u", "s", "0.757" },
            new[] { "3.012", "p", "u", "r", "1.831" },
            new[] { "2.047", "q", "u", "s", "2.129" },
            new[] { "0.387", "q", "u", "r", "-0.818" },
            new[] { "1.058", "q", "w", "r", "0.385" },
            new[] { "1.616", "p", "v", "s", "0.458" },
            new[] { "2.988", "p", "w", "r", "0.56" },
            new[] { "1.978", "p", "v", "r", "0.542" },
        };

        static readonly Dictionary<string, double[]> T2Anova = new()
        {
            ["g"] = new[] { 9.780334691068786, 2.0, 8.246411812763565, 0.0010030767927440211 },
            ["h"] = new[] { 3.219598013690411, 1.0, 5.429288859959319, 0.0249328893123513 },
            ["g:h"] = new[] { 1.3371262975910496, 2.0, 1.1274148016305439, 0.3339432983441293 },
            ["x1"] = new[] { 10.587266286199894, 1.0, 17.85357260771848, 0.00013406616418890971 },
            ["x2"] = new[] { 4.3074093722516915, 1.0, 7.26369243011318, 0.010239353300418747 },
        };
        static readonly (string L, double Est, double Se)[] T2Marg_g =
        {
            ("a", 0.8078700563148652, 0.24171202046163554),
            ("b", 1.2622766786734956, 0.2144467788719173),
            ("c", 0.17774805838847052, 0.17964678106704132),
        };
        static readonly (string A, string B, double Diff, double Se, double T, double P, double Bonf)[] T2Pair_g =
        {
            ("a", "b", 0.4544066223586302, 0.3409965267160754, 1.332584313203236, 0.19021004985914874, 0.5706301495774462),
            ("a", "c", -0.6301219979263946, 0.3047520393300049, -2.0676547376408476, 0.045183305923696196, 0.1355499177710886),
            ("b", "c", -1.0845286202850248, 0.2775792015132556, -3.9070961166131677, 0.0003513879307122418, 0.0010541637921367255),
        };
        static readonly (string L, double Est, double Se)[] T2Marg_h =
        {
            ("u", 0.49325099841606523, 0.15998150006622255),
            ("v", 1.0053455305018222, 0.17482314467613524),
        };
        static readonly (string A, string B, double Diff, double Se, double T, double P, double Bonf)[] T2Pair_h =
        {
            ("u", "v", 0.5120945320857572, 0.2393090440930708, 2.139887917844827, 0.0385160821076614, 0.0385160821076614),
        };
        static readonly (string L, double Est, double Se)[] T2Cell_g_h =
        {
            ("a × u", 0.46556136755139954, 0.29117142869918),
            ("a × v", 1.150178745078331, 0.3874698640953246),
            ("b × u", 1.2508617725690487, 0.32919885606455246),
            ("b × v", 1.273691584777942, 0.2645196648640472),
            ("c × u", -0.23667014487225263, 0.20904284618262017),
            ("c × v", 0.5921662616491942, 0.29874166162480903),
        };

        static readonly Dictionary<string, double[]> T3Anova = new()
        {
            ["a"] = new[] { 2.742006755556318, 1.0, 3.5229326108190047, 0.0667405006050205 },
            ["b"] = new[] { 3.9854996254261934, 2.0, 2.5602866536286486, 0.08802050056425953 },
            ["c"] = new[] { 6.427424641084478, 1.0, 8.257960643522956, 0.006073922438466297 },
            ["a:b"] = new[] { 3.221451269949661, 2.0, 2.069461665269931, 0.13760604921409084 },
            ["a:c"] = new[] { 0.01796289446356525, 1.0, 0.02307874207901251, 0.8799027351145341 },
            ["b:c"] = new[] { 1.9981272824797074, 2.0, 1.283597815678383, 0.286571153863152 },
            ["a:b:c"] = new[] { 6.7051686475547925, 2.0, 4.307403189588308, 0.01915687062864459 },
            ["x"] = new[] { 12.01490629182576, 1.0, 15.436761818925449, 0.00027839573925973423 },
        };
        static readonly (string L, double Est, double Se)[] T3Marg_a =
        {
            ("p", 1.7685730546037357, 0.16270254730821085),
            ("q", 1.3179936120629283, 0.16270254730821043),
        };
        static readonly (string A, string B, double Diff, double Se, double T, double P, double Bonf)[] T3Pair_a =
        {
            ("p", "q", -0.45057944254080795, 0.23237848312723508, -1.9389895160564434, 0.0585196828439466, 0.0585196828439466),
        };
        static readonly (string L, double Est, double Se)[] T3Marg_b =
        {
            ("u", 1.4009206253851925, 0.20252146046438627),
            ("v", 1.3261171256906183, 0.19971020010295182),
            ("w", 1.902812248924185, 0.19781980578868147),
        };
        static readonly (string A, string B, double Diff, double Se, double T, double P, double Bonf)[] T3Pair_b =
        {
            ("u", "v", -0.074803499694574, 0.2893941935301243, -0.2584830703826384, 0.797162737585887, 1.0),
            ("u", "w", 0.5018916235389922, 0.2854724747857179, 1.7581086369735766, 0.0852398401994932, 0.2557195205984796),
            ("v", "w", 0.5766951232335662, 0.2794677084922004, 2.063548330306151, 0.044607233536686886, 0.13382170061006066),
        };
        static readonly (string L, double Est, double Se)[] T3Marg_c =
        {
            ("r", 1.8621084310402576, 0.16552400752726468),
            ("s", 1.2244582356264062, 0.16552400752726465),
        };
        static readonly (string A, string B, double Diff, double Se, double T, double P, double Bonf)[] T3Pair_c =
        {
            ("r", "s", -0.6376501954138514, 0.24021671900543554, -2.6544788308403415, 0.010806281669237519, 0.010806281669237519),
        };
        static readonly (string L, double Est, double Se)[] T3Cell_a_b =
        {
            ("p × u", 1.4077093350760186, 0.28557259939889246),
            ("p × v", 1.4688032890490368, 0.28171150279801044),
            ("p × w", 2.429206539686152, 0.28071997216440486),
            ("q × u", 1.3941319156943663, 0.28066286518815675),
            ("q × v", 1.1834309623322006, 0.29680995509870717),
            ("q × w", 1.376417958162217, 0.27899129455165506),
        };
        static readonly (string L, double Est, double Se)[] T3Cell_a_c =
        {
            ("p × r", 2.0742070946672206, 0.2279786545752007),
            ("p × s", 1.4629390145402508, 0.23438270955949211),
            ("q × r", 1.6500097674132945, 0.23744249354778962),
            ("q × s", 0.9859774567125613, 0.2287626875193651),
        };
        static readonly (string L, double Est, double Se)[] T3Cell_b_c =
        {
            ("u × r", 1.7987802588382482, 0.2790006720642461),
            ("u × s", 1.0030609919321367, 0.29275771629753533),
            ("v × r", 1.8133437586908807, 0.28831591373913673),
            ("v × s", 0.8388904926903558, 0.279184806771897),
            ("w × r", 1.9742012755916436, 0.28251502091721714),
            ("w × s", 1.8314232222567257, 0.2793948178715821),
        };
        static readonly (string L, double Est, double Se)[] T3Cell_a_b_c =
        {
            ("p × u × r", 2.124840981096681, 0.4044524278758054),
            ("p × u × s", 0.6905776890553561, 0.3959224489661459),
            ("p × v × r", 1.4670887428430754, 0.3955319987423677),
            ("p × v × s", 1.4705178352549981, 0.4085638096988923),
            ("p × w × r", 2.630691560061906, 0.4044150919803308),
            ("p × w × s", 2.2277215193103985, 0.39543377637973887),
            ("q × u × r", 1.4727195365798154, 0.4032240644417618),
            ("q × u × s", 1.3155442948089173, 0.4201672754908906),
            ("q × v × r", 2.1595987745386864, 0.4116953586665475),
            ("q × v × s", 0.20726315012571433, 0.4036031797126209),
            ("q × w × r", 1.3177109911213811, 0.39454556429772486),
            ("q × w × s", 1.435124925203053, 0.39456317557099974),
        };

        static MultiAncovaResult Fit(List<string[]> rows, string[] headers, string formula, int factors)
        {
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse(formula),
                c => c >= 1 && c <= factors ? VariableKind.Categorical : VariableKind.Numeric);
            return LinearModel.AncovaMulti(dm);
        }

        static MultiAncovaResult Fit2() => Fit(Rows2, new[] { "y", "g", "h", "x1", "x2" }, "y ~ C(g)*C(h) + x1 + x2", 2);
        static MultiAncovaResult Fit3() => Fit(Rows3, new[] { "y", "a", "b", "c", "x" }, "y ~ C(a)*C(b)*C(c) + x", 3);

        static void Near(double expected, double actual, double tol = 1e-7)
            => Assert.InRange(Math.Abs(expected - actual), 0, tol * Math.Max(1, Math.Abs(expected)));

        static void CheckAnova(MultiAncovaResult r, Dictionary<string, double[]> expected)
        {
            foreach (var (name, e) in expected)
            {
                var row = r.TypeII.Single(t => t.Name == name);
                Assert.True(Math.Abs(e[0] - row.SumOfSquares) <= 1e-7 * Math.Max(1, Math.Abs(e[0])), $"{name}: SS expected {e[0]} actual {row.SumOfSquares}");
                Assert.Equal((int)e[1], row.Df);
                Near(e[2], row.F);
                Near(e[3], row.PValue, 1e-6);
            }
        }

        static void CheckMeans(IReadOnlyList<AdjustedMean> actual, (string L, double Est, double Se)[] expected)
        {
            Assert.Equal(expected.Length, actual.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].L, actual[i].Level);
                Near(expected[i].Est, actual[i].Estimate);
                Near(expected[i].Se, actual[i].StdError);
            }
        }

        static void CheckPairs(IReadOnlyList<AdjustedMeanDifference> actual, (string A, string B, double Diff, double Se, double T, double P, double Bonf)[] expected)
        {
            Assert.Equal(expected.Length, actual.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i].A, actual[i].LevelA);
                Assert.Equal(expected[i].B, actual[i].LevelB);
                Near(expected[i].Diff, actual[i].Difference);
                Near(expected[i].Se, actual[i].StdError);
                Near(expected[i].T, actual[i].T);
                Near(expected[i].P, actual[i].PValue, 1e-6);
                Near(expected[i].Bonf, actual[i].BonferroniP, 1e-6);
            }
        }

        [Fact]
        public void Two_way_interaction_type_ii_and_factor_means_match_statsmodels()
        {
            var r = Fit2();
            CheckAnova(r, T2Anova);
            CheckMeans(r.Factors.Single(f => f.Factor == "g").AdjustedMeans, T2Marg_g);
            CheckMeans(r.Factors.Single(f => f.Factor == "h").AdjustedMeans, T2Marg_h);
            CheckPairs(r.Factors.Single(f => f.Factor == "g").Pairwise, T2Pair_g);
            CheckPairs(r.Factors.Single(f => f.Factor == "h").Pairwise, T2Pair_h);
        }

        [Fact]
        public void Two_way_cell_means_match_statsmodels()
        {
            var r = Fit2();
            var cell = Assert.Single(r.Cells);
            Assert.Equal("g:h", cell.Term);
            CheckMeans(cell.AdjustedMeans, T2Cell_g_h);
            Assert.Equal(Rows2.Count, cell.AdjustedMeans.Sum(m => m.Count));
        }

        [Fact]
        public void Three_way_all_orders_match_statsmodels()
        {
            var r = Fit3();
            CheckAnova(r, T3Anova);
            CheckMeans(r.Factors.Single(f => f.Factor == "a").AdjustedMeans, T3Marg_a);
            CheckMeans(r.Factors.Single(f => f.Factor == "b").AdjustedMeans, T3Marg_b);
            CheckMeans(r.Factors.Single(f => f.Factor == "c").AdjustedMeans, T3Marg_c);
            CheckPairs(r.Factors.Single(f => f.Factor == "b").Pairwise, T3Pair_b);
            Assert.Equal(4, r.Cells.Count);
            CheckMeans(r.Cells.Single(c => c.Term == "a:b").AdjustedMeans, T3Cell_a_b);
            CheckMeans(r.Cells.Single(c => c.Term == "a:c").AdjustedMeans, T3Cell_a_c);
            CheckMeans(r.Cells.Single(c => c.Term == "b:c").AdjustedMeans, T3Cell_b_c);
            CheckMeans(r.Cells.Single(c => c.Term == "a:b:c").AdjustedMeans, T3Cell_a_b_c);
        }

        [Fact]
        public void Main_effects_only_model_has_no_cells()
        {
            var r = Fit(Rows2, new[] { "y", "g", "h", "x1", "x2" }, "y ~ C(g) + C(h) + x1 + x2", 2);
            Assert.Empty(r.Cells);
        }

        [Fact]
        public void Factor_by_covariate_interaction_is_rejected()
        {
            var ex = Assert.Throws<DesignMatrixException>(() =>
                Fit(Rows2, new[] { "y", "g", "h", "x1", "x2" }, "y ~ C(g) + C(h) + x1 + x2 + C(g):x1", 2));
            Assert.Contains("covariate", ex.Message);
        }

        [Fact]
        public void Empty_cell_is_reported_as_not_estimable()
        {
            // g=c, h=v 셀을 비운다 → 그 셀의 보정 평균은 추정 불가(NaN), 나머지는 유한.
            var rows = Rows2.Where(r => !(r[1] == "c" && r[2] == "v")).ToList();
            var res = Fit(rows, new[] { "y", "g", "h", "x1", "x2" }, "y ~ C(g)*C(h) + x1 + x2", 2);
            var cell = Assert.Single(res.Cells).AdjustedMeans;
            var empty = cell.Single(m => m.Level == "c × v");
            Assert.Equal(0, empty.Count);
            Assert.True(double.IsNaN(empty.Estimate));
            Assert.All(cell.Where(m => m.Level != "c × v"), m => Assert.True(double.IsFinite(m.Estimate)));
        }
    }
}
