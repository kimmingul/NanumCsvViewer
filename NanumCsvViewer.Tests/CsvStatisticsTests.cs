using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    public class CsvStatisticsTests
    {
        // ---- 분포 함수: 알려진 정답으로 대조 ----

        [Fact]
        public void Student_t_df1_is_cauchy()
        {
            // 자유도 1인 t분포 = 표준 코시. P(|T| > 1) = 0.5
            Assert.Equal(0.5, CsvStatistics.StudentTTwoSidedPValue(1, 1), 4);
        }

        [Fact]
        public void Student_t_zero_statistic_is_one()
        {
            Assert.Equal(1.0, CsvStatistics.StudentTTwoSidedPValue(0, 10), 6);
        }

        [Fact]
        public void Student_t_large_statistic_approaches_zero()
        {
            Assert.True(CsvStatistics.StudentTTwoSidedPValue(100, 5) < 1e-6);
        }

        [Fact]
        public void ChiSquare_critical_value_df1()
        {
            // χ² = 3.8415, df=1 → p ≈ 0.05
            Assert.Equal(0.05, CsvStatistics.ChiSquareUpperTailProbability(3.841459, 1), 3);
        }

        [Fact]
        public void ChiSquare_zero_statistic_is_one()
        {
            Assert.Equal(1.0, CsvStatistics.ChiSquareUpperTailProbability(0, 3), 6);
        }

        // ---- 고수준 검정 ----

        [Fact]
        public void Independent_ttest_identical_groups()
        {
            var r = CsvStatistics.IndependentTTest("a", new double[] { 1, 2, 3 }, "b", new double[] { 1, 2, 3 });
            Assert.Equal(0, r.TStatistic, 6);
            Assert.Equal(1.0, r.PValue, 6);
        }

        [Fact]
        public void Independent_ttest_clearly_different_groups_are_significant()
        {
            var a = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            var b = new double[] { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 };
            var r = CsvStatistics.IndependentTTest("a", a, "b", b);
            Assert.True(r.PValue < 0.05);
            Assert.True(r.ConfidenceIntervalLow <= r.ConfidenceIntervalHigh);
        }

        [Fact]
        public void Paired_ttest_consistent_positive_shift()
        {
            var before = new double[] { 10, 20, 30, 40, 50 };
            var after = new double[] { 13, 21, 34, 41, 53 }; // 차이 [3,1,4,1,3], 평균 +2.4 (분산 > 0)
            var r = CsvStatistics.PairedTTest(before, after);
            Assert.Equal(2.4, r.MeanDifference, 6);
            Assert.True(r.PValue < 0.05);
        }

        [Fact]
        public void Paired_ttest_zero_variance_is_guarded()
        {
            // 차이가 모두 동일(분산 0)하면 표준오차 0 → t=0, p=1 (0으로 나눔 방지)
            var before = new double[] { 10, 20, 30 };
            var after = new double[] { 12, 22, 32 };
            var r = CsvStatistics.PairedTTest(before, after);
            Assert.Equal(2, r.MeanDifference, 6);
            Assert.Equal(0, r.TStatistic, 6);
            Assert.Equal(1.0, r.PValue, 6);
        }

        [Fact]
        public void Correlation_perfect_positive()
        {
            var pairs = new List<(double, double)> { (1, 1), (2, 2), (3, 3), (4, 4), (5, 5) };
            var r = CsvStatistics.Correlation(pairs, CorrelationMethod.Pearson);
            Assert.Equal(1.0, r.Coefficient, 6);
        }

        [Fact]
        public void Correlation_negative()
        {
            var pairs = new List<(double, double)> { (1, 5), (2, 4), (3, 3), (4, 2), (5, 1) };
            var r = CsvStatistics.Correlation(pairs, CorrelationMethod.Pearson);
            Assert.Equal(-1.0, r.Coefficient, 6);
        }

        [Fact]
        public void ChiSquare_independent_table_not_significant()
        {
            // 완전 균등(독립) 2x2 → 통계량 0, p=1
            var rows = new List<(string, string)>
            {
                ("A", "X"), ("A", "Y"), ("B", "X"), ("B", "Y"),
            };
            var r = CsvStatistics.ChiSquare(rows);
            Assert.Equal(0, r.Statistic, 6);
            Assert.Equal(1.0, r.PValue, 6);
        }

        [Fact]
        public void ChiSquare_strong_association_is_significant()
        {
            var rows = new List<(string, string)>();
            for (int i = 0; i < 50; i++) { rows.Add(("A", "X")); rows.Add(("B", "Y")); }
            var r = CsvStatistics.ChiSquare(rows);
            Assert.True(r.PValue < 0.05);
            Assert.Equal(1, r.DegreesOfFreedom);
        }
    }
}
