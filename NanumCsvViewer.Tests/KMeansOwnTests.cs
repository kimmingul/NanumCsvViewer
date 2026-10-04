using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 자체 K-means(k-means++ · 재시작 · 병렬 Lloyd). 기준은 throwaway Python(sklearn 1.7.2,
    // KMeans(algorithm="lloyd", init="k-means++", n_init=1, random_state=0..399)의 최소 inertia)으로 만든 값이다.
    // 난수열이 달라 군집 번호·반복 수는 sklearn과 다르므로, sklearn과는 최종 inertia·분할로만 비교하고
    // 이 구현이 낸 결정적 출력(반복 수 등)은 회귀 기준으로 따로 고정한다.
    public class KMeansOwnTests
    {
        // 파이썬과 같은 정수 LCG + Irwin–Hall(4개 합) 표준화 — 두 언어에서 같은 데이터가 나오도록 부동소수 라이브러리 함수를 쓰지 않는다.
        private sealed class Lcg
        {
            private long _s;
            public Lcg(long seed) { _s = seed; }
            public double Next()
            {
                _s = (_s * 1103515245L + 12345L) & 0x7fffffffL;
                return _s / 2147483648.0;
            }
        }

        private static double[,] Gen(long seed, double[][] centers, int per, double scale)
        {
            var g = new Lcg(seed);
            int p = centers[0].Length;
            var x = new double[centers.Length * per, p];
            int row = 0;
            foreach (var c in centers)
                for (int i = 0; i < per; i++, row++)
                    for (int j = 0; j < p; j++)
                    {
                        double a = g.Next(), b = g.Next(), d = g.Next(), e = g.Next();
                        x[row, j] = c[j] + scale * (a + b + d + e - 2.0);
                    }
            return x;
        }

        // F1: 겹치는 6개 덩어리(2차원). 단일 시작 400번 중 서로 다른 지역 최적이 18개 — 재시작이 필요한 자료.
        private static double[,] F1() => Gen(11,
            new[] { new[] { 0.0, 0 }, new[] { 4.0, 0 }, new[] { 8.0, 1 }, new[] { 0.0, 5 }, new[] { 4.0, 5.5 }, new[] { 8.0, 6 } }, 50, 1.2);

        // F2: 5차원 5개 덩어리, 중심은 LCG(5)로 [0,6)에서 뽑음. 서로 다른 지역 최적 110개.
        private static double[,] F2()
        {
            var g = new Lcg(5);
            var centers = new double[5][];
            for (int c = 0; c < 5; c++)
            {
                centers[c] = new double[5];
                for (int j = 0; j < 5; j++) centers[c][j] = 6 * g.Next();
            }
            return Gen(23, centers, 80, 1.5);
        }

        // sklearn 최소 inertia(단일 시작 400회, 시드 0..399)와 그 분할의 군집 크기·실루엣.
        private const double F1BestInertia = 287.2026454462987;
        private const double F1BestSilhouette = 0.6704768065008255;
        private const double F2BestInertia = 1382.314959427376;
        private const double F2BestSilhouette = 0.30625909133004403;
        // n_init=10, random_state=0 (sklearn 기본 설정)이 낸 inertia. F2에서는 전역 최적보다 나쁜 지역 최적에 머문다.
        private const double F2SklearnDefaultInertia = 1382.3630617254098;

        private static KMeansOptions Opt(int k, int restarts, int seed = 1) => new()
        {
            K = k, Restarts = restarts, Seed = seed, Scaling = ScalingMethod.None,
        };

        [Fact]
        public void F1_reaches_sklearn_best_inertia_and_partition()
        {
            var r = KMeansClustering.Fit(F1(), Opt(6, 10));
            Assert.True(r.TotalWss <= F1BestInertia * (1 + 1e-12), $"inertia {r.TotalWss:R} > sklearn best {F1BestInertia:R}");
            Assert.Equal(new[] { 50, 50, 50, 50, 50, 50 }, r.Clusters.Select(c => c.Size).OrderBy(s => s).ToArray());
            Assert.Equal(F1BestSilhouette, r.Silhouette, 9);
            Assert.Equal(1, r.TerminationType);
            Assert.True(r.Converged);
            Assert.Equal(10, r.ConvergedRestarts);
            Assert.True(r.LloydFixedPoint);
            Assert.Equal(0, r.EmptyClusters);
        }

        [Fact]
        public void F2_with_enough_restarts_reaches_sklearn_best_inertia_and_partition()
        {
            var r = KMeansClustering.Fit(F2(), Opt(5, 60));
            Assert.True(r.TotalWss <= F2BestInertia * (1 + 1e-12), $"inertia {r.TotalWss:R} > sklearn best {F2BestInertia:R}");
            Assert.True(r.TotalWss <= F2SklearnDefaultInertia);
            Assert.Equal(new[] { 74, 79, 80, 81, 86 }, r.Clusters.Select(c => c.Size).OrderBy(s => s).ToArray());
            Assert.Equal(F2BestSilhouette, r.Silhouette, 9);
        }

        // 재시작은 같은 시드 난수열을 순서대로 쓴다 → 재시작 수를 늘리면 선택된 WSS는 줄거나 같다(불변식).
        [Fact]
        public void More_restarts_never_increase_the_selected_wss()
        {
            var x = F1();
            double previous = double.PositiveInfinity;
            for (int restarts = 1; restarts <= 8; restarts++)
            {
                var r = KMeansClustering.Fit(x, Opt(6, restarts, seed: 4));
                Assert.True(r.TotalWss <= previous + 1e-9, $"restarts={restarts}: {r.TotalWss:R} > {previous:R}");
                previous = r.TotalWss;
            }
        }

        // 이 구현의 결정적 출력 고정(회귀). 군집 번호·반복 수는 시드·청크 구성에만 의존하고 코어 수에는 의존하지 않는다.
        [Fact]
        public void Recorded_deterministic_output_for_f1_seed_7()
        {
            var r = KMeansClustering.Fit(F1(), Opt(6, 3, seed: 7));
            Assert.Equal(RecordedF1Wss, r.TotalWss, 9);
            Assert.Equal(RecordedF1Iterations, r.Iterations);
            Assert.Equal(new[] { 50, 50, 50, 50, 50, 50 }, r.Clusters.Select(c => c.Size).ToArray());
            // 덩어리별 첫 행의 군집 번호(번호 붙이는 순서는 시드·k-means++ 초기화에 의해 결정적)
            Assert.Equal(RecordedF1BlobLabels, new[] { 0, 50, 100, 150, 200, 250 }.Select(i => r.Assignment[i]).ToArray());
        }

        private const double RecordedF1Wss = 287.20264544629879;
        private const int RecordedF1Iterations = 9;
        private static readonly int[] RecordedF1BlobLabels = { 2, 4, 0, 1, 5, 3 };

        [Fact]
        public void Repeated_parallel_runs_give_identical_results()
        {
            var x = Gen(3, new[] { new[] { 0.0, 0, 0 }, new[] { 2.0, 1, 0 }, new[] { 0.0, 3, 2 }, new[] { 3.0, 3, 3 } }, 20_000, 1.3);
            var o = Opt(4, 3, seed: 9);
            var a = KMeansClustering.Fit(x, o);
            for (int rep = 0; rep < 3; rep++)
            {
                var b = KMeansClustering.Fit(x, o);
                Assert.Equal(a.Assignment, b.Assignment);
                Assert.Equal(a.TotalWss, b.TotalWss);
                Assert.Equal(a.Iterations, b.Iterations);
            }
        }

        [Fact]
        public void Iteration_limit_is_reported_as_not_converged_while_the_partition_is_still_returned()
        {
            var r = KMeansClustering.Fit(F1(), new KMeansOptions { K = 6, Restarts = 4, Seed = 1, Scaling = ScalingMethod.None, MaxIterations = 1 });
            Assert.Equal(2, r.TerminationType);
            Assert.False(r.Converged);
            Assert.Equal(0, r.ConvergedRestarts);
            Assert.Equal(4, r.Iterations); // 재시작 4회 × 1회
            Assert.Equal(300, r.Assignment.Length);
            // 그래도 WSS는 그 배정의 평균 기준 관성이고 TSS = WSS + BSS.
            Assert.Equal(r.TotalSs, r.TotalWss + r.BetweenSs, 6);
        }

        [Fact]
        public void Exactly_k_distinct_points_with_duplicates_gives_zero_inertia()
        {
            var x = new double[,] { { 0 }, { 0 }, { 0 }, { 0 }, { 5 }, { 5 }, { 5 }, { 10 }, { 10 } };
            var r = KMeansClustering.Fit(x, Opt(3, 5));
            Assert.Equal(0.0, r.TotalWss, 12);
            Assert.Equal(new[] { 2, 3, 4 }, r.Clusters.Select(c => c.Size).OrderBy(s => s).ToArray());
            Assert.Equal(0, r.EmptyClusters);
        }

        [Fact]
        public void Fewer_distinct_points_than_k_is_an_error_not_a_partition()
        {
            var x = new double[,] { { 0 }, { 0 }, { 1 }, { 1 }, { 0 }, { 1 } };
            var ex = Assert.Throws<DesignMatrixException>(() => KMeansClustering.Fit(x, Opt(3, 2)));
            Assert.Contains("distinct", ex.Message);
        }

        [Fact]
        public void Elbow_with_exactly_k_distinct_points_reaches_zero_wss_at_k()
        {
            var x = new double[,] { { 0 }, { 0 }, { 5 }, { 5 }, { 10 }, { 10 } };
            var r = KMeansClustering.Fit(x, new KMeansOptions { K = 3, Restarts = 3, Scaling = ScalingMethod.None, Elbow = true });
            Assert.All(r.Elbow!, e => Assert.True(e.Succeeded));
            Assert.Equal(0.0, r.Elbow!.Single(e => e.K == 3).TotalWss, 12);
            Assert.True(r.Elbow!.Single(e => e.K == 2).TotalWss > 0);
        }

        // 빈 군집: 시작 중심이 모두 같으면 한 군집에 전부 몰린다. 자기 중심에서 가장 먼 행으로 옮겨 모두 채워야 한다.
        [Fact]
        public void Empty_clusters_are_refilled_from_the_farthest_rows()
        {
            var x = new double[,]
            {
                { 0, 0 }, { 0.1, 0.2 }, { -0.2, 0.1 }, { 0.2, -0.1 },
                { 20, 0 }, { 20.1, 0.2 }, { 19.8, 0.1 }, { 20.2, -0.1 },
                { 0, 20 }, { 0.1, 20.2 }, { -0.2, 20.1 }, { 0.2, 19.9 },
            };
            var start = new double[3 * 2]; // 세 중심 모두 (0,0)
            var (assignment, iterations, converged) = KMeansClustering.Refine(x, start, 3, CancellationToken.None);
            Assert.True(converged);
            Assert.True(iterations >= 2);
            var sizes = new int[3];
            foreach (var a in assignment) sizes[a]++;
            Assert.All(sizes, s => Assert.True(s > 0, "an empty cluster survived"));
            Assert.Equal(12, sizes.Sum());
        }

        [Fact]
        public void Non_finite_features_are_rejected()
        {
            var x = new double[,] { { 0, 0 }, { 1, double.NaN }, { 2, 2 } };
            var ex = Assert.Throws<DesignMatrixException>(() => KMeansClustering.Fit(x, Opt(2, 1)));
            Assert.Contains("finite", ex.Message);
        }

        [Fact]
        public void Large_path_initializes_on_a_sample_and_refines_on_all_rows()
        {
            var x = F1();
            var r = KMeansClustering.Fit(x, new KMeansOptions { K = 6, Restarts = 10, Seed = 1, Scaling = ScalingMethod.None, FullDataLimit = 150 });
            Assert.Equal(150, r.InitializationSampleRows);
            Assert.True(r.RefinementConverged);
            Assert.True(r.LloydFixedPoint);
            Assert.Equal(300, r.Assignment.Length);
            Assert.Equal(300, r.Clusters.Sum(c => c.Size));
            Assert.Equal(r.TotalSs, r.TotalWss + r.BetweenSs, 6);
        }
    }
}
