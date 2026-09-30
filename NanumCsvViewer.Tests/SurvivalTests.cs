using NanumCsvViewer;
using NanumCsvViewer.Charting;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 기준값은 throwaway Python(statsmodels 0.14.5)으로 생성했다.
    // SurvfuncRight / survdiff / PHReg(...).fit(ties='efron'|'breslow').
    public class SurvivalTests
    {
        // 그룹 0·1·2. 동점·중도절단 포함.
        private static readonly double[] KmTime =
        {
            5, 6, 6, 8, 10, 12, 12, 15, 18, 20, 22, 25,
            4, 6, 7, 9, 9, 11, 14, 16, 16, 19, 21, 24,
            3, 5, 8, 8, 13, 17, 17, 23,
        };
        private static readonly int[] KmStatus =
        {
            1, 1, 0, 1, 1, 1, 0, 1, 0, 1, 1, 0,
            1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0, 1,
            1, 0, 1, 1, 1, 0, 1, 1,
        };
        private static readonly string[] KmGroup =
        {
            "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0", "0",
            "1", "1", "1", "1", "1", "1", "1", "1", "1", "1", "1", "1",
            "2", "2", "2", "2", "2", "2", "2", "2",
        };

        // SurvfuncRight(group 0): surv_prob, surv_prob_se, n_risk, n_events, times
        private static readonly double[] G0Prob =
        {
            0.9166666666666666, 0.8333333333333333, 0.7407407407407406, 0.648148148148148,
            0.5555555555555556, 0.4444444444444444, 0.29629629629629634, 0.14814814814814817,
        };
        private static readonly double[] G0Se =
        {
            0.07978559231302818, 0.10758287072798378, 0.12948257384816683, 0.14261129694009775,
            0.1493010694129361, 0.1553790886178002, 0.1592544740404652, 0.13158432192073313,
        };

        [Fact]
        public void KaplanMeier_matches_SurvfuncRight_and_logrank_matches_survdiff()
        {
            var result = KaplanMeierAnalysis.Fit(KmTime, KmStatus, KmGroup);
            Assert.Equal(3, result.Curves.Count);
            Assert.Equal(new[] { "0", "1", "2" }, result.Curves.Select(c => c.Group).ToArray());

            var g0 = result.Curves[0];
            Assert.Equal(new[] { 5d, 6, 8, 10, 12, 15, 20, 22 }, g0.Rows.Select(r => r.Time).ToArray());
            Assert.Equal(new long[] { 12, 11, 9, 8, 7, 5, 3, 2 }, g0.Rows.Select(r => r.AtRisk).ToArray());
            Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1, 1 }, g0.Rows.Select(r => r.Events).ToArray());
            // 시각 6과 12에 중도절단이 하나씩 겹친다. 18·25는 사건이 없어 표 행이 아니다.
            Assert.Equal(1, g0.Rows.Single(r => r.Time == 6).Censored);
            Assert.Equal(1, g0.Rows.Single(r => r.Time == 12).Censored);
            Assert.Equal(4, g0.Censored);
            for (int i = 0; i < G0Prob.Length; i++)
            {
                Close(G0Prob[i], g0.Rows[i].Survival);
                Close(G0Se[i], g0.Rows[i].StdError);
            }
            // SurvfuncRight.quantile(0.5) / quantile_ci(0.5, method='cloglog')
            Assert.Equal(15, g0.Median);
            Assert.Equal(6, g0.MedianCiLow);
            Assert.Equal(22, g0.MedianCiHigh);
            // ∫ S(t) dt to the last observed time (25). Point estimate, no SE.
            Close(15.287037037037038, g0.RestrictedMean);

            var g1 = result.Curves[1];
            Assert.Equal(14, g1.Median);
            Assert.Equal(6, g1.MedianCiLow);
            Assert.Equal(24, g1.MedianCiHigh);
            Assert.Equal(0, g1.Rows[^1].Survival);
            Assert.True(double.IsNaN(g1.Rows[^1].StdError));
            Assert.True(double.IsNaN(g1.Rows[^1].CiLow));

            var g2 = result.Curves[2];
            Assert.Equal(new long[] { 8, 6, 4, 3, 1 }, g2.Rows.Select(r => r.AtRisk).ToArray());
            Assert.Equal(2, g2.Rows.Single(r => r.Time == 8).Events);
            Assert.Equal(1, g2.Rows.Single(r => r.Time == 17).Censored);
            Close(0.5833333333333334, g2.Rows.Single(r => r.Time == 8).Survival);
            Assert.Equal(13, g2.Median);
            Assert.Equal(3, g2.MedianCiLow);
            Assert.Equal(23, g2.MedianCiHigh);

            // survdiff(time, status, group) and weight_type='gb'
            Assert.Equal(LogRankIssue.None, result.LogRank.Issue);
            Close(0.2939087585178193, result.LogRank.ChiSquare);
            Assert.Equal(2, result.LogRank.DegreesOfFreedom);
            Close(0.8633333624257279, result.LogRank.P);
            Assert.Equal("0", result.LogRank.ReferenceGroup);
            Close(0.17026192021761538, result.GehanBreslow.ChiSquare);
            Close(0.9183920038089026, result.GehanBreslow.P);

            var two = KaplanMeierAnalysis.Fit(
                KmTime.Take(24).ToArray(), KmStatus.Take(24).ToArray(), KmGroup.Take(24).ToArray());
            Close(0.11842615278911103, two.LogRank.ChiSquare);
            Close(0.7307477621054255, two.LogRank.P);
            Close(0.06468068723230185, two.GehanBreslow.ChiSquare);
            Close(0.7992452746997059, two.GehanBreslow.P);
        }

        [Fact]
        public void KaplanMeier_single_group_skips_logrank_and_plot_is_a_step()
        {
            var one = KaplanMeierAnalysis.Fit(KmTime.Take(12).ToArray(), KmStatus.Take(12).ToArray());
            Assert.Equal(LogRankIssue.TooFewGroups, one.LogRank.Issue);
            Assert.True(double.IsNaN(one.LogRank.ChiSquare));
            var plot = KaplanMeierPlot.Build(one, "KM", "t", "S", "note");
            var line = plot.Series.Single(s => s.Kind == PlotSeriesKind.Line && s.Name == "(all)");
            Assert.True(line.Xs.Length >= 4);
            // 계단: 같은 시각에 수직 구간이 있다.
            bool vertical = false;
            for (int i = 1; i < line.Xs.Length; i++)
                if (line.Xs[i] == line.Xs[i - 1] && line.Ys[i] != line.Ys[i - 1]) vertical = true;
            Assert.True(vertical);
            Assert.Contains(plot.Series, s => s.Kind == PlotSeriesKind.Points && s.Xs.Length == 4);
        }

        private static readonly double[] CoxTime =
        {
            4, 6, 6, 8, 9, 9, 11, 12, 12, 15,
            3, 5, 7, 7, 10, 14, 16, 16, 18, 20,
            2, 4, 4, 8, 13, 13, 17, 19, 21, 22,
            1, 5, 9, 11, 11, 15, 18, 23, 24, 26,
        };
        private static readonly int[] CoxStatus =
        {
            1, 1, 0, 1, 1, 0, 1, 1, 1, 0,
            1, 1, 1, 0, 1, 1, 0, 1, 1, 1,
            1, 0, 1, 1, 1, 0, 1, 0, 1, 1,
            1, 1, 0, 1, 1, 1, 0, 1, 0, 1,
        };
        private static readonly double[] X1 =
        {
            0.2, -0.4, 1.1, 0.0, 0.7, -1.2, 0.5, 1.4, -0.8, 0.3,
            0.9, -0.1, 1.6, -1.5, 0.4, 0.8, -0.6, 1.2, 0.1, -0.9,
            0.6, 1.0, -0.3, 0.2, -1.1, 1.3, 0.0, -0.7, 0.5, 1.5,
            -0.2, 0.4, 0.8, -1.4, 0.3, 1.1, -0.5, 0.7, 0.0, -0.8,
        };
        private static readonly double[] X2 =
        {
            0, 1, 0, 1, 1, 0, 0, 1, 0, 1,
            1, 0, 1, 0, 0, 1, 1, 0, 1, 0,
            0, 1, 0, 1, 1, 0, 1, 1, 0, 1,
            1, 0, 0, 1, 0, 1, 1, 0, 1, 0,
        };

        private static double[,] CoxX()
        {
            var x = new double[40, 2];
            for (int i = 0; i < 40; i++) { x[i, 0] = X1[i]; x[i, 1] = X2[i]; }
            return x;
        }

        [Fact]
        public void Cox_Efron_and_Breslow_match_PHReg()
        {
            // PHReg(time, exog, status, ties='efron').fit()
            var efron = CoxRegression.Fit(CoxTime, CoxStatus, CoxX(), new[] { "x1", "x2" }, CoxTies.Efron);
            Assert.True(efron.Converged);
            Assert.False(efron.MonotoneLikelihood);
            Close(0.20216748198441173, efron.Coefficients[0]);
            Close(-0.07442620312570153, efron.Coefficients[1]);
            Close(0.23274787150292825, efron.StdErrors[0]);
            Close(0.38367789206047953, efron.StdErrors[1]);
            Close(-80.72139596314884, efron.LogLikelihood);
            Close(-81.11314673978542, efron.NullLogLikelihood);
            Close(0.7808161341001382, efron.Score);
            Close(0.7763166492588512, efron.Wald);
            Close(0.7835015532731688, efron.LikelihoodRatio);
            // 독립 Python 이중 루프(시각 i < j 이고 i가 사건). 동점 위험은 0.5.
            Close(0.5340314136125655, efron.Concordance);
            Assert.Equal(573, efron.ComparablePairs);
            Assert.Equal(303, efron.ConcordantPairs);
            Assert.Equal(264, efron.DiscordantPairs);
            Assert.Equal(6, efron.TiedRiskPairs);

            // PHReg(..., ties='breslow').fit()
            var breslow = CoxRegression.Fit(CoxTime, CoxStatus, CoxX(), ties: CoxTies.Breslow);
            Assert.True(breslow.Converged);
            Close(0.20481558398381108, breslow.Coefficients[0]);
            Close(-0.06577641647709742, breslow.Coefficients[1]);
            Close(0.23297304184232415, breslow.StdErrors[0]);
            Close(0.3836063633958054, breslow.StdErrors[1]);
            Close(-81.0046513776579, breslow.LogLikelihood);
            Close(-81.4026637380036, breslow.NullLogLikelihood);
            Close(0.7928324237309343, breslow.Score);
            Close(0.7960247206914062, breslow.LikelihoodRatio);
            Assert.NotEqual(efron.LogLikelihood, breslow.LogLikelihood);
        }

        [Fact]
        public void Cox_without_ties_Efron_equals_Breslow()
        {
            // PHReg on 20 untied times. efron params == breslow params.
            double[] time = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
            int[] status = { 1, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0, 1 };
            double[] a = { 0.1, 0.4, -0.2, 0.8, -0.5, 0.3, 1.1, -0.7, 0.6, 0.0, -0.4, 0.9, 0.2, -1.0, 0.5, 0.7, -0.3, 0.4, 1.2, -0.6 };
            double[] b = { 1, 0, 1, 0, 0, 1, 1, 0, 1, 0, 1, 0, 0, 1, 1, 0, 1, 0, 1, 1 };
            var x = new double[20, 2];
            for (int i = 0; i < 20; i++) { x[i, 0] = a[i]; x[i, 1] = b[i]; }
            var efron = CoxRegression.Fit(time, status, x, ties: CoxTies.Efron);
            var breslow = CoxRegression.Fit(time, status, x, ties: CoxTies.Breslow);
            Close(-0.2953068878060271, efron.Coefficients[0]);
            Close(-0.07158917043417938, efron.Coefficients[1]);
            Close(0.43756475849314447, efron.StdErrors[0]);
            Close(0.6019471251111559, efron.StdErrors[1]);
            Close(-29.399286357802776, efron.LogLikelihood);
            Close(efron.Coefficients[0], breslow.Coefficients[0], 1e-12);
            Close(efron.LogLikelihood, breslow.LogLikelihood, 1e-12);
        }

        [Fact]
        public void Cox_formula_path_matches_treatment_coded_PHReg()
        {
            // 같은 행을 t ~ x + C(g)로 넣으면 처치 코딩(기준 a) 열이 PHReg(exog=[x, g==b, g==c])와 같다.
            string[] g = { "b", "a", "c", "a", "b", "c", "b", "a", "c", "b" };
            var rows = new string[40][];
            for (int i = 0; i < 40; i++)
                rows[i] = new[]
                {
                    CoxTime[i].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    CoxStatus[i].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    X1[i].ToString(System.Globalization.CultureInfo.InvariantCulture),
                    g[i % 10],
                };
            var formula = ModelFormula.Parse("t ~ x + C(g)");
            var fit = CoxRegression.FromFormula(rows, new[] { "t", "e", "x", "g" }, formula, 1, null,
                c => c == 3 ? VariableKind.Categorical : VariableKind.Numeric, CoxTies.Efron);
            Assert.Equal(new[] { "x", "g[T.b]", "g[T.c]" }, fit.Names);
            Assert.True(fit.Converged);
            Assert.Equal(40, fit.RowsUsed);
            Assert.Equal(0, fit.RowsDropped);
            // PHReg(ctime, column_stack([x1, g=='b', g=='c']), status, ties='efron').fit()
            Close(0.21627251886467935, fit.Coefficients[0]);
            Close(-0.18922008461383988, fit.Coefficients[1]);
            Close(-0.5461522988016682, fit.Coefficients[2]);
            Close(0.23375176266669512, fit.StdErrors[0]);
            Close(0.4520906788281836, fit.StdErrors[1]);
            Close(0.5060436311824058, fit.StdErrors[2]);
            Close(-80.12761249368506, fit.LogLikelihood);
        }

        [Fact]
        public void Cox_separation_is_flagged_and_not_reported_as_converged()
        {
            double[] time = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            int[] status = { 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
            var x = new double[10, 1];
            for (int i = 0; i < 5; i++) x[i, 0] = 1;
            var fit = CoxRegression.Fit(time, status, x);
            Assert.False(fit.Converged);
            Assert.True(fit.MonotoneLikelihood);
            Assert.True(double.IsNaN(fit.LikelihoodRatio));
            Assert.True(double.IsFinite(fit.Score));
        }

        [Fact]
        public void Event_level_and_invalid_rows_are_counted()
        {
            var rows = new[]
            {
                new[] { "5", "dead", "A" },
                new[] { "6", "alive", "A" },
                new[] { "-1", "dead", "A" },
                new[] { "7", "", "B" },
                new[] { "8", "dead", "B" },
            };
            var km = KaplanMeierAnalysis.FromRows(rows, 0, 1, "dead", 2);
            Assert.Equal(5, km.RowsRead);
            Assert.Equal(3, km.RowsUsed);
            Assert.Equal(2, km.RowsDropped);
            Assert.Equal(3, km.Curves.Single(c => c.Group == "A").N + km.Curves.Single(c => c.Group == "B").N);
            Assert.Equal(2, km.Curves.Single(c => c.Group == "A").N);

            var ex = Assert.Throws<DesignMatrixException>(() =>
                KaplanMeierAnalysis.FromRows(new[] { new[] { "1", "dead" } }, 0, 1, null, null));
            Assert.Contains("event level", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        [Fact]
        public void LogRank_variance_does_not_overflow_when_tied_events_are_large()
        {
            // 10만 명, 같은 시각에 사건 5만. ev*(n-d) = 50000*50000 = 2.5e9 > Int32.MaxValue.
            // 정수 곱이면 공분산이 음수가 되어 검정이 특이하거나 χ²가 틀린다.
            const int nA = 60_000, eventsA = 40_000, nB = 40_000, eventsB = 10_000;
            int n = nA + nB;
            var time = new double[n];
            var status = new int[n];
            var group = new string[n];
            for (int i = 0; i < n; i++) time[i] = 1;
            for (int i = 0; i < nA; i++)
            {
                group[i] = "A";
                status[i] = i < eventsA ? 1 : 0;
            }
            for (int i = 0; i < nB; i++)
            {
                group[nA + i] = "B";
                status[nA + i] = i < eventsB ? 1 : 0;
            }

            var result = KaplanMeierAnalysis.Fit(time, status, group);
            int evTot = eventsA + eventsB;
            double scalar = (double)evTot * (n - evTot) / (n - 1);
            double rB = nB / (double)n;
            double oe = eventsB - rB * evTot;
            double variance = scalar * rB * (1 - rB);
            double expected = oe * oe / variance;

            Assert.Equal(LogRankIssue.None, result.LogRank.Issue);
            Assert.True(result.LogRank.ChiSquare > 0);
            Close(expected, result.LogRank.ChiSquare, 1e-6);
            Close(expected, result.GehanBreslow.ChiSquare, 1e-4);
        }

        [Fact]
        public void Restricted_mean_uses_each_groups_own_last_time()
        {
            // A의 추적 종료는 2. 다른 그룹의 10까지 적분하면 RMST가 5.5로 늘어난다.
            var result = KaplanMeierAnalysis.Fit(
                new double[] { 1, 2, 10 },
                new[] { 1, 0, 0 },
                new[] { "A", "A", "B" });
            var a = result.Curves.Single(c => c.Group == "A");
            Assert.Equal(2, a.RestrictedMeanTau);
            Close(1.5, a.RestrictedMean);
            var b = result.Curves.Single(c => c.Group == "B");
            Assert.Equal(10, b.RestrictedMeanTau);
            Close(10, b.RestrictedMean);
        }

        [Fact]
        public void Default_event_column_prefers_boolean_else_fallback()
        {
            Assert.Equal(2, Form1.DefaultEventColumn(
                new[] { ColumnValueType.Integer, ColumnValueType.Float, ColumnValueType.Boolean }, 1));
            Assert.Equal(1, Form1.DefaultEventColumn(
                new[] { ColumnValueType.Integer, ColumnValueType.Categorical }, 1));
        }

        private static void Close(double expected, double actual, double tol = 1e-6)
            => Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected} actual {actual} diff {actual - expected}");
    }
}
