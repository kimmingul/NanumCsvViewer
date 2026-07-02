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

        // ---- 기술통계 (#17): scipy 1.17.1 대조값 ----

        [Fact]
        public void Describe_reference_values_match_scipy()
        {
            // scipy: skew(bias=False)=0.8184876, kurtosis(bias=False)=0.940625,
            //        se=0.7559289, CI95=[3.2125121, 6.7874879]
            var d = CsvStatistics.Describe(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })!;
            Assert.Equal(8, d.Count);
            Assert.Equal(5.0, d.Mean, 6);
            Assert.Equal(2.138090, d.StandardDeviation, 5);
            Assert.Equal(0.755929, d.StandardError, 5);
            Assert.Equal(3.212512, d.ConfidenceIntervalLow, 4);
            Assert.Equal(6.787488, d.ConfidenceIntervalHigh, 4);
            Assert.Equal(0.818488, d.Skewness, 5);
            Assert.Equal(0.940625, d.ExcessKurtosis, 5);
            Assert.Equal(new double[] { 4 }, d.Modes);
            Assert.Equal(3, d.ModeFrequency);
        }

        [Fact]
        public void Describe_skewed_data_matches_scipy()
        {
            // scipy: skew=1.6970563, kurtosis=3.152, CI95=[-0.3899452, 8.3899452]
            var d = CsvStatistics.Describe(new double[] { 1, 2, 3, 4, 10 })!;
            Assert.Equal(1.697056, d.Skewness, 5);
            Assert.Equal(3.152, d.ExcessKurtosis, 5);
            Assert.Equal(-0.389945, d.ConfidenceIntervalLow, 4);
            Assert.Equal(8.389945, d.ConfidenceIntervalHigh, 4);
            Assert.Equal(3.0, d.Median, 6);
            Assert.Equal(2.0, d.Q1, 6);   // 선형보간(R type 7)
            Assert.Equal(4.0, d.Q3, 6);
            Assert.Equal(2.0, d.InterquartileRange, 6);
            Assert.Equal(9.0, d.Range, 6);
            Assert.Empty(d.Modes); // 모두 고유 → 최빈값 없음
        }

        [Fact]
        public void Describe_empty_returns_null()
        {
            Assert.Null(CsvStatistics.Describe(Array.Empty<double>()));
        }

        [Fact]
        public void Describe_constant_column_is_guarded()
        {
            var d = CsvStatistics.Describe(new double[] { 5, 5, 5, 5 })!;
            Assert.Equal(0, d.StandardDeviation, 6);
            Assert.True(double.IsNaN(d.Skewness));       // SD 0 → 미정의
            Assert.True(double.IsNaN(d.ExcessKurtosis));
            Assert.Equal(0, d.CoefficientOfVariation, 6);
        }

        // ---- 빈도표 (#17) ----

        [Fact]
        public void Frequency_counts_percent_and_cumulative()
        {
            var t = CsvStatistics.FrequencyTable(new[] { "a", " a ", "b", "c" });
            Assert.Equal(4, t.TotalCount);
            Assert.Equal(3, t.UniqueCount);
            Assert.Equal("a", t.Entries[0].Value);       // 트림 후 병합
            Assert.Equal(2, t.Entries[0].Count);
            Assert.Equal(50.0, t.Entries[0].Percent, 6);
            Assert.Equal(50.0, t.Entries[0].CumulativePercent, 6);
            Assert.Equal("b", t.Entries[1].Value);       // 동률은 값 오름차순
            Assert.Equal(75.0, t.Entries[1].CumulativePercent, 6);
            Assert.Equal(100.0, t.Entries[2].CumulativePercent, 6);
        }

        // ---- 일원배치 분산분석 (#17) ----

        [Fact]
        public void Anova_textbook_case_is_exact()
        {
            // 그룹 평균 2/3/4, SSB=6, SSW=6 → F=3, P(F(2,6)>3)=(1+1)⁻³=0.125 (정확값)
            var obs = new List<(string, double)>();
            foreach (double v in new double[] { 1, 2, 3 }) obs.Add(("g1", v));
            foreach (double v in new double[] { 2, 3, 4 }) obs.Add(("g2", v));
            foreach (double v in new double[] { 3, 4, 5 }) obs.Add(("g3", v));
            var r = CsvStatistics.OneWayAnova(obs)!;
            Assert.Equal(3.0, r.FStatistic, 6);
            Assert.Equal(2, r.DfBetween);
            Assert.Equal(6, r.DfWithin);
            Assert.Equal(0.125, r.PValue, 6);
        }

        [Fact]
        public void Anova_reference_values_match_scipy()
        {
            // scipy.stats.f_oneway: F=44.4756447, p=2.8210855e-06; η²=0.8811308
            var obs = new List<(string, double)>();
            foreach (double v in new double[] { 23, 25, 18, 29, 22 }) obs.Add(("a", v));
            foreach (double v in new double[] { 31, 28, 35, 30, 33 }) obs.Add(("b", v));
            foreach (double v in new double[] { 45, 42, 39, 48, 44 }) obs.Add(("c", v));
            var r = CsvStatistics.OneWayAnova(obs)!;
            Assert.Equal(44.475645, r.FStatistic, 4);
            Assert.Equal(2.8210855e-06, r.PValue, 9);
            Assert.Equal(0.881131, r.EtaSquared, 5);
        }

        [Fact]
        public void Anova_single_group_returns_null()
        {
            var obs = new List<(string, double)> { ("only", 1), ("only", 2), ("only", 3) };
            Assert.Null(CsvStatistics.OneWayAnova(obs));
        }

        [Fact]
        public void Anova_identical_groups_no_variation_between()
        {
            var obs = new List<(string, double)>
            {
                ("a", 1), ("a", 2), ("b", 1), ("b", 2),
            };
            var r = CsvStatistics.OneWayAnova(obs)!;
            Assert.Equal(0, r.FStatistic, 6);
            Assert.Equal(1.0, r.PValue, 6);
        }

        // ---- Shapiro-Wilk 정규성 검정 (#17): scipy 1.17.1 대조값 ----

        [Fact]
        public void ShapiroWilk_n3_symmetric_is_perfect()
        {
            var r = CsvStatistics.ShapiroWilk(new double[] { 1, 2, 3 })!;
            Assert.Equal(1.0, r.W, 4);
            Assert.Equal(1.0, r.PValue, 3);
        }

        [Fact]
        public void ShapiroWilk_skewed_n11_matches_scipy()
        {
            // scipy.stats.shapiro: W=0.7888147, p=0.0067038
            var r = CsvStatistics.ShapiroWilk(
                new double[] { 148, 154, 158, 160, 161, 162, 166, 170, 182, 195, 236 })!;
            Assert.Equal(0.788815, r.W, 3);
            Assert.Equal(0.006704, r.PValue, 3);
        }

        [Fact]
        public void ShapiroWilk_nearnormal_n20_matches_scipy()
        {
            // scipy: W=0.9874345, p=0.9927320
            var r = CsvStatistics.ShapiroWilk(new[]
            {
                4.1, 5.2, 5.6, 6.1, 6.3, 6.6, 7.0, 7.5, 8.1, 9.9,
                3.8, 5.9, 6.4, 7.2, 6.8, 5.5, 6.0, 7.8, 8.5, 4.9,
            })!;
            Assert.Equal(0.987435, r.W, 3);
            Assert.Equal(0.992732, r.PValue, 2);
        }

        [Fact]
        public void ShapiroWilk_uniform_n30_matches_scipy()
        {
            // 1..30 균등 수열. scipy: W=0.9574506, p=0.2662327
            var values = new double[30];
            for (int i = 0; i < 30; i++) values[i] = i + 1;
            var r = CsvStatistics.ShapiroWilk(values)!;
            Assert.Equal(0.957451, r.W, 3);
            Assert.Equal(0.266233, r.PValue, 2);
        }

        [Fact]
        public void ShapiroWilk_extreme_outlier_rejects_normality()
        {
            // scipy: W=0.3657206, p=1.0e-07
            var r = CsvStatistics.ShapiroWilk(new double[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 20 })!;
            Assert.Equal(0.365721, r.W, 3);
            Assert.True(r.PValue < 1e-5);
        }

        [Fact]
        public void ShapiroWilk_guards_small_and_constant_input()
        {
            Assert.Null(CsvStatistics.ShapiroWilk(new double[] { 1, 2 }));
            Assert.Null(CsvStatistics.ShapiroWilk(new double[] { 7, 7, 7, 7 }));
        }

        // ---- 분포 함수 추가 래퍼 (#17) ----

        [Fact]
        public void F_distribution_zero_statistic_is_one()
        {
            Assert.Equal(1.0, CsvStatistics.FDistributionUpperTailProbability(0, 3, 10), 6);
        }

        [Fact]
        public void Normal_quantile_known_values()
        {
            Assert.Equal(0.0, CsvStatistics.StandardNormalQuantile(0.5), 9);
            Assert.Equal(1.959964, CsvStatistics.StandardNormalQuantile(0.975), 5);
            Assert.Equal(-2.326348, CsvStatistics.StandardNormalQuantile(0.01), 5);
        }
    }
}
