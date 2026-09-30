using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // F·df·p: statsmodels.stats.anova.AnovaRM(data, 'y', 'subj', within=['cond']).fit().anova_table
    // Mauchly·GG·HF: numpy 교과서 공식. 정규직교 Helmert 대비 C, Σ = Cᵀ S C (S = 불편 공분산),
    //   W = det(Σ) / (tr(Σ)/p)^p,  p = k−1,
    //   d = 1 − (2p²+p+2)/(6p(n−1)),  χ² = −(n−1)·d·ln(W),  df = p(p+1)/2 − 1,
    //   ε_GG = tr(Σ)² / (p·‖Σ‖²_F),
    //   ε_HF = (n·p·ε_GG − 2) / (p·(n−1 − p·ε_GG)), 1 초과면 1.
    public class RepeatedMeasuresAnovaTests
    {
        private static void AssertStat(double expected, double actual)
        {
            double diff = Math.Abs(expected - actual);
            double tol = Math.Max(1e-8, Math.Abs(expected) * 1e-9);
            Assert.True(diff <= tol, $"expected {expected:G12} actual {actual:G12} diff {diff:G10}");
        }

        private static void AssertP(double expected, double actual)
        {
            double diff = Math.Abs(expected - actual);
            double tol = Math.Max(1e-6, Math.Abs(expected) * 1e-5);
            Assert.True(diff <= tol, $"p expected {expected:G12} actual {actual:G12} diff {diff:G10}");
        }

        // 정수 행렬. AnovaRM F=37.50842266462482, p=1.286170604182705e-08, df=3, 21.
        // SS_subjects=36.46875, SS_conditions=109.34375, SS_error=20.40625, partial η²=0.8427263969171483
        // Mauchly W=0.2650164003438281, χ²=7.598902632364533, df=5, p=0.1797703481830828
        // ε_GG=0.5485496031975752, p_GG=1.538763546296543e-05
        // ε_HF=0.6950851796164765 (not capped), p_HF=1.521441950236695e-06
        [Fact]
        public void Fit_matches_statsmodels_and_textbook_sphericity()
        {
            double[][] y =
            [
                [5, 6, 9, 8],
                [4, 5, 7, 9],
                [6, 7, 10, 9],
                [3, 4, 6, 8],
                [5, 8, 9, 11],
                [4, 6, 8, 10],
                [6, 5, 9, 7],
                [5, 7, 11, 12],
            ];
            var r = RepeatedMeasuresAnova.Fit(y);

            Assert.Equal(8, r.N);
            Assert.Equal(4, r.K);
            Assert.False(r.SphericityTrivial);
            AssertStat(36.46875, r.SsSubjects);
            AssertStat(109.34375, r.SsConditions);
            AssertStat(20.40625, r.SsError);
            AssertStat(37.50842266462482, r.F);
            AssertP(1.286170604182705e-08, r.P);
            AssertStat(0.8427263969171483, r.PartialEtaSquared);
            AssertStat(4.75, r.Conditions[0].Mean);
            AssertStat(6, r.Conditions[1].Mean);
            AssertStat(8.625, r.Conditions[2].Mean);
            AssertStat(9.25, r.Conditions[3].Mean);
            AssertStat(1.035098339013531, r.Conditions[0].StandardDeviation);

            Assert.True(r.MauchlyDefined);
            AssertStat(0.2650164003438281, r.MauchlyW);
            AssertStat(7.598902632364533, r.MauchlyChiSquare);
            AssertStat(5, r.MauchlyDf);
            AssertP(0.1797703481830828, r.MauchlyP);
            AssertStat(0.5485496031975752, r.GreenhouseGeisser);
            AssertP(1.538763546296543e-05, r.GgP);
            AssertStat(0.5485496031975752 * 3, r.GgDfNum);
            AssertStat(0.5485496031975752 * 21, r.GgDfDen);
            Assert.True(r.HuynhFeldtDefined);
            Assert.False(r.HuynhFeldtCapped);
            AssertStat(0.6950851796164765, r.HuynhFeldt);
            AssertP(1.521441950236695e-06, r.HfP);
        }

        // 같은 행렬의 앞 두 열. AnovaRM F=9.210526315789467, p=0.01898333709653264, df=1, 7.
        // SS_subjects=14.75, SS_conditions=6.25, SS_error=4.75, partial η²=0.5681818181818182
        [Fact]
        public void Fit_two_conditions_sphericity_is_trivial()
        {
            double[][] y =
            [
                [5, 6],
                [4, 5],
                [6, 7],
                [3, 4],
                [5, 8],
                [4, 6],
                [6, 5],
                [5, 7],
            ];
            var r = RepeatedMeasuresAnova.Fit(y);

            Assert.True(r.SphericityTrivial);
            Assert.False(r.MauchlyDefined);
            Assert.True(double.IsNaN(r.MauchlyW));
            AssertStat(1, r.GreenhouseGeisser);
            AssertStat(1, r.HuynhFeldt);
            Assert.False(r.HuynhFeldtCapped);
            AssertStat(14.75, r.SsSubjects);
            AssertStat(6.25, r.SsConditions);
            AssertStat(4.75, r.SsError);
            AssertStat(9.210526315789467, r.F);
            AssertP(0.01898333709653264, r.P);
            AssertStat(0.5681818181818182, r.PartialEtaSquared);
            AssertP(r.P, r.GgP);
            AssertP(r.P, r.HfP);
        }

        // numpy Generator(4) 근사 복합대칭. ε_HF 원값 1.250031619548374 → 1로 자름.
        // AnovaRM F=208.6407500490265, p=3.798135409339327e-11
        // W=0.923094884069447, χ²=0.4801395007256097, pW=0.786572995401324
        // ε_GG=0.9285869156038877, p_GG=1.756310226614187e-10
        // 자른 HF의 p는 보정 전 p와 같다.
        [Fact]
        public void Fit_caps_huynh_feldt_at_one()
        {
            double[][] y =
            [
                [-0.8930193202396479, 0.3844746289198381, 1.3835159851989285],
                [0.0616266123889422, 0.8727794601450082, 1.901864706928869],
                [1.439756488646562, 3.0016333600998006, 3.3763771576974517],
                [0.8244180282056276, 1.6096621387511774, 2.5270507721430855],
                [-1.7398400462240013, -0.7421994966834451, 0.4156310692527021],
                [-0.0217120684636397, 1.2171824102831466, 1.7203561323112655],
                [-0.623925885688271, 0.2427278907633679, 1.4929170361303155],
                [-0.1690774830757339, 1.0970773707371042, 2.180157462352442],
            ];
            var r = RepeatedMeasuresAnova.Fit(y);

            AssertStat(208.6407500490265, r.F);
            AssertP(3.798135409339327e-11, r.P);
            AssertStat(20.0359352315743, r.SsSubjects);
            AssertStat(16.286470689818, r.SsConditions);
            AssertStat(0.5464191189973135, r.SsError);
            Assert.True(r.MauchlyDefined);
            AssertStat(0.923094884069447, r.MauchlyW);
            AssertStat(0.4801395007256097, r.MauchlyChiSquare);
            AssertP(0.786572995401324, r.MauchlyP);
            AssertStat(0.9285869156038877, r.GreenhouseGeisser);
            AssertP(1.756310226614187e-10, r.GgP);
            Assert.True(r.HuynhFeldtCapped);
            AssertStat(1, r.HuynhFeldt);
            AssertP(r.P, r.HfP);
        }

        [Fact]
        public void Fit_singular_covariance_does_not_report_mauchly()
        {
            double[][] y =
            [
                [1, 2, 5, 3],
                [2, 3, 4, 6],
                [1, 4, 6, 5],
            ];
            var r = RepeatedMeasuresAnova.Fit(y);

            AssertStat(8.8, r.F);
            AssertP(0.01288854098017145, r.P);
            AssertStat(25.66666666666667, r.SsConditions);
            Assert.False(r.MauchlyDefined);
            Assert.True(double.IsNaN(r.MauchlyW));
            AssertStat(0.5149222362337117, r.GreenhouseGeisser);
            Assert.True(r.HuynhFeldtCapped);
            AssertStat(1, r.HuynhFeldt);
            AssertP(r.P, r.HfP);
        }

        [Fact]
        public void Fit_rejects_fewer_than_two_complete_rows()
        {
            string[][] rows =
            [
                ["1", "2", "3"],
                ["4", "", "5"],
                ["x", "1", "2"],
            ];
            var ex = Assert.Throws<DesignMatrixException>(() =>
                RepeatedMeasuresAnova.Fit(rows, [0, 1, 2], ["a", "b", "c"]));
            Assert.Contains("2 complete", ex.Message, StringComparison.OrdinalIgnoreCase);

            string[][] ok =
            [
                ["1", "2", "4"],
                ["2", "3", "5"],
                ["1", "", "9"],
                ["3", "4", "6"],
            ];
            var r = RepeatedMeasuresAnova.Fit(ok, [0, 1, 2], ["a", "b", "c"]);
            Assert.Equal(4L, r.RowsRead);
            Assert.Equal(3L, r.RowsUsed);
            Assert.Equal(1L, r.RowsDropped);
            Assert.Equal("a", r.Conditions[0].Name);
            Assert.False(r.SphericityTrivial);
        }
    }
}
