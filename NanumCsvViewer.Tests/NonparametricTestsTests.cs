using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // scipy 1.15 / 아래 주석의 생성 호출과 대조. 통계량 ~1e-8, p ~1e-6 (아주 작은 p는 상대 1e-5도 허용).
    public class NonparametricTestsTests
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

        // scipy.stats.mannwhitneyu(x, y, method='asymptotic', use_continuity=True)
        // and method='exact'. z = (max(U1,U2) - n1*n2/2 - 0.5) / sigma, tie term 0.
        [Fact]
        public void MannWhitney_asymptotic_and_exact_match_scipy_without_ties()
        {
            double[] x = [1, 3, 5, 7, 9];
            double[] y = [2, 4, 6, 8, 10];
            var r = NonparametricTests.MannWhitney(x, y);

            AssertStat(10, r.U1);
            AssertStat(15, r.U2);
            AssertStat(0.4177863742936748, r.Z);
            AssertP(0.6761033140231469, r.PAsymptotic);
            Assert.True(r.ExactComputed);
            AssertP(0.6904761904761905, r.PExact);
            AssertStat(-0.2, r.RankBiserial);
            Assert.Equal(5.0, r.Median1);
            Assert.Equal(6.0, r.Median2);
            Assert.False(r.HasTies);

            // n1=1, method='exact' p=0.4, asymptotic p=0.2888443663464849
            var small = NonparametricTests.MannWhitney(new[] { 1.0 }, new[] { 2.0, 3, 4, 5 });
            AssertStat(0, small.U1);
            AssertP(0.2888443663464849, small.PAsymptotic);
            AssertP(0.4, small.PExact);
            Assert.Equal(3.5, small.Median2);
        }

        // scipy.stats.mannwhitneyu([1,2,2,3,5,5,5,8], [2,4,5,5,7,9], method='asymptotic', use_continuity=True)
        [Fact]
        public void MannWhitney_tie_correction_matches_scipy_and_skips_exact()
        {
            double[] x = [1, 2, 2, 3, 5, 5, 5, 8];
            double[] y = [2, 4, 5, 5, 7, 9];
            var r = NonparametricTests.MannWhitney(x, y);

            AssertStat(16, r.U1);
            AssertStat(0.9948387690144102, r.Z);
            AssertP(0.31981468712628547, r.PAsymptotic);
            Assert.True(r.HasTies);
            Assert.False(r.ExactComputed);
            Assert.True(double.IsNaN(r.PExact));
            AssertStat(-1.0 / 3.0, r.RankBiserial);
            Assert.Equal(4.0, r.Median1);
            Assert.Equal(5.0, r.Median2);
        }

        // x = 0..49, y = 0.5..49.5. product 2500 > limit, no ties.
        // scipy.stats.mannwhitneyu(x, y, method='asymptotic', use_continuity=True) → U=1225, p=0.8658764106823897
        [Fact]
        public void MannWhitney_exact_skipped_when_product_exceeds_limit()
        {
            var x = Enumerable.Range(0, 50).Select(i => (double)i).ToArray();
            var y = Enumerable.Range(0, 50).Select(i => i + 0.5).ToArray();
            var r = NonparametricTests.MannWhitney(x, y);

            Assert.False(r.HasTies);
            Assert.False(r.ExactComputed);
            AssertStat(1225, r.U1);
            AssertStat(0.1688985869486993, r.Z);
            AssertP(0.8658764106823897, r.PAsymptotic);
            AssertStat(-0.02, r.RankBiserial);
        }

        [Fact]
        public void MannWhitney_variance_zero_does_not_report_p()
        {
            var r = NonparametricTests.MannWhitney(new[] { 2.0, 2, 2, 2, 2 }, new[] { 2.0, 2, 2 });
            Assert.True(r.VarianceZero);
            Assert.True(r.HasTies);
            Assert.True(double.IsNaN(r.Z));
            Assert.True(double.IsNaN(r.PAsymptotic));
            Assert.False(r.ExactComputed);
            AssertStat(7.5, r.U1);
            AssertStat(0, r.RankBiserial);
        }

        [Fact]
        public void MannWhitney_rows_drop_missing_and_reject_one_group()
        {
            string[][] rows =
            [
                ["1", "a"],
                ["2", "a"],
                ["", "a"],
                ["3", "b"],
                ["4", "b"],
                ["x", "b"],
                ["5", ""],
            ];
            var r = NonparametricTests.MannWhitney(rows, 0, 1, null, null);
            Assert.Equal(7L, r.RowsRead);
            Assert.Equal(4L, r.RowsUsed);
            Assert.Equal(3L, r.RowsDropped);
            Assert.Equal("a", r.Group1);
            Assert.Equal("b", r.Group2);
            Assert.Equal(2, r.N1);
            Assert.Equal(2, r.N2);

            string[][] oneGroup = [["1", "a"], ["2", "a"], ["3", "a"]];
            var ex = Assert.Throws<DesignMatrixException>(() => NonparametricTests.MannWhitney(oneGroup, 0, 1, null, null));
            Assert.Contains("two groups", ex.Message, StringComparison.OrdinalIgnoreCase);

            string[][] shortRows = [["1", "a"], ["", "b"]];
            Assert.Throws<DesignMatrixException>(() => NonparametricTests.MannWhitney(shortRows, 0, 1, "a", "b"));
        }

        [Fact]
        public void MannWhitney_rows_require_named_groups_when_more_than_two()
        {
            string[][] rows =
            [
                ["1", "b"],
                ["3", "a"],
                ["5", "c"],
                ["2", "a"],
                ["9", "b"],
                ["NA", "c"],
            ];
            var ex = Assert.Throws<DesignMatrixException>(() => NonparametricTests.MannWhitney(rows, 0, 1, null, null));
            Assert.Contains("a", ex.Message);
            Assert.Contains("b", ex.Message);

            var r = NonparametricTests.MannWhitney(rows, 0, 1, "b", "A");
            Assert.Equal("b", r.Group1);
            Assert.Equal("a", r.Group2);
            Assert.Equal(2, r.N1);
            Assert.Equal(2, r.N2);
            Assert.Equal(1L, r.OtherGroupsExcluded);
            Assert.Equal(4L, r.RowsUsed);
        }

        // d = [1,-2,3,-4,5,-6,8,-9]
        // scipy.stats.wilcoxon(d, zero_method='wilcox', method='exact') → W=16, p=0.84375
        // method='asymptotic', correction=True → zstatistic=-0.21004201260420147, p=0.8336348830246822
        // r = z_signed / sqrt(n) = -0.07426106572325057
        [Fact]
        public void Wilcoxon_exact_matches_scipy_for_n_at_most_50_without_ties()
        {
            double[] d = [1, -2, 3, -4, 5, -6, 8, -9];
            var zeros = new double[d.Length];
            var r = NonparametricTests.WilcoxonSignedRank(d, zeros);

            Assert.True(r.UsedExact);
            Assert.False(r.HasTies);
            AssertStat(16, r.W);
            AssertStat(16, r.TPlus);
            AssertStat(20, r.TMinus);
            AssertP(0.84375, r.P);
            AssertStat(-0.21004201260420147, r.Z);
            AssertStat(-0.07426106572325057, r.EffectSizeR);

            // n=50, unique |d|, method='exact' p=0.9085978224870299. W=625.
            var d50 = Enumerable.Range(0, 50).Select(i => (i + 1) * (i % 2 == 0 ? 1.0 : -1.0)).ToArray();
            var base50 = new double[50];
            var exact = NonparametricTests.WilcoxonSignedRank(d50, base50);
            Assert.True(exact.UsedExact);
            AssertStat(625, exact.W);
            AssertP(0.9085978224870299, exact.P);
        }

        // d = [1.2, -0.3, 0.5, 0, 2.1, -0.3, 0.8, 0, 1.5, -1]
        // scipy.stats.wilcoxon(d, zero_method='wilcox', correction=True, method='asymptotic')
        // → statistic=8, p=0.18289327757982765, zstatistic=-1.331899310742285
        [Fact]
        public void Wilcoxon_asymptotic_matches_scipy_with_zeros_and_ties()
        {
            double[] x = [1.2, -0.3, 0.5, 0, 2.1, -0.3, 0.8, 0, 1.5, -1];
            var y = new double[x.Length];
            var r = NonparametricTests.WilcoxonSignedRank(x, y);

            Assert.False(r.UsedExact);
            Assert.True(r.HasTies);
            Assert.Equal(2, r.ZerosDropped);
            Assert.Equal(8, r.N);
            AssertStat(8, r.W);
            AssertStat(28, r.TPlus);
            AssertP(0.18289327757982765, r.P);
            AssertStat(1.331899310742285, r.Z);
            AssertStat(0.47089751724177914, r.EffectSizeR);
            Assert.Equal(-1.331899310742285, -Math.Abs(r.Z), 12);
        }

        // d_i = (i+1) * ±1, i=0..50. n=51 > 50, no ties.
        // scipy.stats.wilcoxon(..., method='asymptotic', correction=True) → W=650, p=0.9067266583852887, zstatistic=-abs(z)=-0.1171683358482538
        // This engine's Z is the T+ direction (+0.1171683358482538).
        [Fact]
        public void Wilcoxon_uses_asymptotic_when_n_exceeds_50()
        {
            var d = Enumerable.Range(0, 51).Select(i => (i + 1) * (i % 2 == 0 ? 1.0 : -1.0)).ToArray();
            var baseline = new double[d.Length];
            var r = NonparametricTests.WilcoxonSignedRank(d, baseline);

            Assert.False(r.UsedExact);
            Assert.False(r.HasTies);
            Assert.Equal(51, r.N);
            AssertStat(650, r.W);
            AssertP(0.9067266583852887, r.P);
            AssertStat(0.1171683358482538, r.Z);
        }

        [Fact]
        public void Wilcoxon_rejects_all_zero_differences_and_short_pairs()
        {
            string[][] allZero = [["1", "1"], ["2", "2"], ["3", "3"]];
            var zero = Assert.Throws<DesignMatrixException>(() => NonparametricTests.WilcoxonSignedRank(allZero, 0, 1));
            Assert.Contains("zero", zero.Message, StringComparison.OrdinalIgnoreCase);

            string[][] one = [["1", "2"], ["", "3"]];
            var few = Assert.Throws<DesignMatrixException>(() => NonparametricTests.WilcoxonSignedRank(one, 0, 1));
            Assert.Contains("2 complete", few.Message, StringComparison.OrdinalIgnoreCase);

            string[][] mixed =
            [
                ["1", "1"],
                ["2", "0"],
                ["x", "1"],
                ["4", "1"],
            ];
            var r = NonparametricTests.WilcoxonSignedRank(mixed, 0, 1);
            Assert.Equal(4L, r.RowsRead);
            Assert.Equal(3L, r.RowsUsed);
            Assert.Equal(1L, r.RowsDropped);
            Assert.Equal(1, r.ZerosDropped);
            Assert.Equal(2, r.N);
        }

        // scipy.stats.binomtest(k, n, 0.5).pvalue
        [Fact]
        public void SignTest_matches_binomtest_and_drops_zeros()
        {
            AssertP(0.0078125, NonparametricTests.BinomialTwoSidedHalf(0, 8));
            AssertP(0.0654296875, NonparametricTests.BinomialTwoSidedHalf(9, 11));
            AssertP(0.021484375, NonparametricTests.BinomialTwoSidedHalf(1, 10));
            AssertP(0.1538599441628321, NonparametricTests.BinomialTwoSidedHalf(15, 40));
            AssertP(1.115908905725195e-09, NonparametricTests.BinomialTwoSidedHalf(20, 100));
            Assert.Equal(1.0, NonparametricTests.BinomialTwoSidedHalf(5, 10));


            double[] x = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
            double[] y = [0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 21];
            var paired = NonparametricTests.SignTestPaired(x, y);
            Assert.Equal(9, paired.Positive);
            Assert.Equal(2, paired.Negative);
            Assert.Equal(0, paired.Zeros);
            AssertP(0.0654296875, paired.P);

            double[] sample = [1, 1, 1, 1, 1, 1, 8, 9, 5, 5];
            var one = NonparametricTests.SignTestMedian(sample, 5);
            Assert.Equal(2, one.Positive);
            Assert.Equal(6, one.Negative);
            Assert.Equal(2, one.Zeros);
            AssertP(NonparametricTests.BinomialTwoSidedHalf(2, 8), one.P);

            string[][] rows = [["1", "1"], ["3", "1"], ["", "2"], ["4", "9"]];
            var scanned = NonparametricTests.SignTestPaired(rows, 0, 1);
            Assert.Equal(4, scanned.RowsRead);
            Assert.Equal(3, scanned.RowsUsed);
            Assert.Equal(1, scanned.RowsDropped);
            Assert.Equal(1, scanned.Zeros);
            Assert.Equal(1, scanned.Positive);
            Assert.Equal(1, scanned.Negative);

            string[][] few = [["1", "2"]];
            Assert.Throws<DesignMatrixException>(() => NonparametricTests.SignTestPaired(few, 0, 1));
            string[][] tied = [["5", "a"], ["5", "a"]];
            Assert.Throws<DesignMatrixException>(() => NonparametricTests.SignTestMedian(tied, 0, 5));
        }

        // groups = [[1,2,2,3,4], [2,3,4,4,5,6], [1,1,2,3,6,7,8], [5,5,6,9]]
        // scipy.stats.kruskal(*groups) → H=6.194514646754741, p=0.1025207786201853
        // ε² = H*(N+1)/(N²-1) = 0.2949768879407019
        // Dunn: z = (meanRank_i - meanRank_j) / sqrt((N(N+1)/12 - sum(t³-t)/(12(N-1))) * (1/ni+1/nj))
        // Bonferroni × 6. numpy rankdata + this formula.
        [Fact]
        public void KruskalWallis_and_Dunn_match_scipy_and_numpy()
        {
            IReadOnlyList<double>[] groups =
            [
                [1, 2, 2, 3, 4],
                [2, 3, 4, 4, 5, 6],
                [1, 1, 2, 3, 6, 7, 8],
                [5, 5, 6, 9],
            ];
            var r = NonparametricTests.KruskalWallis(groups);

            AssertStat(6.194514646754741, r.H);
            Assert.Equal(3, r.Df);
            AssertP(0.1025207786201853, r.P);
            AssertStat(0.2949768879407019, r.EpsilonSquared);
            Assert.Equal(6, r.Comparisons);
            Assert.Equal(6, r.Dunn.Count);
            Assert.True(r.SmallSample);

            AssertDunn(r, 0, 1, -1.312431572720647, 0.1893745598252573, 1);
            AssertDunn(r, 0, 2, -1.13303103828459, 0.2572012192269874, 1);
            AssertDunn(r, 0, 3, -2.47743791862238, 0.01323294276563952, 0.07939765659383712);
            AssertDunn(r, 1, 2, 0.2359702710228765, 0.813455739097515, 1);
            AssertDunn(r, 1, 3, -1.343459062115237, 0.1791233733607893, 1);
            AssertDunn(r, 2, 3, -1.593024321684498, 0.1111547349524049, 0.6669284097144292);
        }

        [Fact]
        public void KruskalWallis_rejects_identical_values_and_one_group()
        {
            string[][] identical = [["1", "a"], ["1", "a"], ["1", "b"], ["1", "b"]];
            var same = Assert.Throws<DesignMatrixException>(() => NonparametricTests.KruskalWallis(identical, 0, 1));
            Assert.Contains("identical", same.Message, StringComparison.OrdinalIgnoreCase);

            string[][] one = [["1", "a"], ["2", "a"], ["3", "a"]];
            var groups = Assert.Throws<DesignMatrixException>(() => NonparametricTests.KruskalWallis(one, 0, 1));
            Assert.Contains("2 groups", groups.Message, StringComparison.OrdinalIgnoreCase);

            string[][] few = [["1", "a"], ["", "b"]];
            Assert.Throws<DesignMatrixException>(() => NonparametricTests.KruskalWallis(few, 0, 1));
        }

        // blocks (rows) with ties. scipy.stats.friedmanchisquare on the columns
        // → statistic=15.9056603773585, p=0.001185619395074021. W = χ²/(n(k-1)) = 0.8836477987421392
        [Fact]
        public void Friedman_tie_correction_matches_scipy()
        {
            double[][] blocks =
            [
                [1, 2, 3, 3],
                [2, 2, 4, 5],
                [1, 3, 3, 4],
                [2, 4, 4, 6],
                [3, 3, 5, 5],
                [1, 2, 2, 4],
            ];
            var r = NonparametricTests.Friedman(blocks);
            Assert.True(r.HasTies);
            AssertStat(15.9056603773585, r.ChiSquare);
            Assert.Equal(3, r.Df);
            AssertP(0.001185619395074021, r.P);
            AssertStat(0.8836477987421392, r.KendallsW);
            Assert.Equal(6, r.N);

            double[][] noTies =
            [
                [1, 2, 4, 3],
                [2, 1, 3, 5],
                [1, 3, 5, 4],
                [2, 3, 4, 6],
                [1, 2, 6, 5],
            ];
            var plain = NonparametricTests.Friedman(noTies);
            Assert.False(plain.HasTies);
            AssertStat(12.6, plain.ChiSquare);
            AssertP(0.005586546097302402, plain.P);
            AssertStat(0.84, plain.KendallsW);

            string[][] shortRows = [["1", "2", "3"], ["", "1", "2"]];
            Assert.Throws<DesignMatrixException>(() => NonparametricTests.Friedman(shortRows, [0, 1, 2], ["a", "b", "c"]));
            string[][] tied =
            [
                ["1", "1", "1"],
                ["2", "2", "2"],
                ["3", "3", "3"],
            ];
            Assert.Throws<DesignMatrixException>(() => NonparametricTests.Friedman(tied, [0, 1, 2], ["a", "b", "c"]));
        }

        private static void AssertDunn(KruskalWallisResult r, int i, int j, double z, double p, double pAdj)
        {
            var pair = Assert.Single(r.Dunn, d => d.IndexI == i && d.IndexJ == j);
            AssertStat(z, pair.Z);
            AssertP(p, pair.P);
            AssertP(pAdj, pair.PBonferroni);
        }
    }
}
