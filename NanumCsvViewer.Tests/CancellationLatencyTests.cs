using System.Diagnostics;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // PCA·K-means가 취소 요청 뒤 빠르게 OperationCanceledException으로 빠져나오는지.
    public class CancellationLatencyTests
    {
        static double[,] Data(int n, int p)
        {
            var rng = new Random(5);
            var x = new double[n, p];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++) x[i, j] = rng.NextDouble() + (i % 7) * 0.3 * (j % 3);
            return x;
        }

        [Fact]
        public void Pca_cancels_during_covariance_pass()
        {
            var x = Data(600_000, 24);
            var names = Enumerable.Range(0, 24).Select(i => "x" + i).ToList();
            using var cts = new CancellationTokenSource(25);
            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() => PrincipalComponents.Fit(x, names, PcaScale.Correlation, 3, cts.Token));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"cancel took {sw.Elapsed}");
        }

        // 취소가 요청되면 호출이 곧바로 OperationCanceledException으로 끝나야 하고, 그 뒤 백그라운드에서 계산이 계속되면 안 된다.
        // (이전 구현은 ALGLIB 호출을 별도 Task에서 돌리고 포기해 계산이 계속 남았다.)
        // ChunkProbe는 이 스레드의 K-means가 실행한 청크 본문 수다. 타이머 대신 이 수가 기준에 닿으면 취소해
        // 계산이 실제로 진행 중일 때 취소했음을 보장하고, 취소 뒤 값이 더 늘면 일이 남아 있다는 뜻이다.
        private static void AssertCancelsPromptlyAndStops(Action<CancellationToken> fit, long cancelAfterChunks)
        {
            var probe = new long[1];
            KMeansClustering.ChunkProbe = probe;
            using var cts = new CancellationTokenSource();
            long cancelledAt = 0;
            bool finished = false;
            var canceller = Task.Run(() =>
            {
                var spin = new SpinWait();
                while (!Volatile.Read(ref finished) && Interlocked.Read(ref probe[0]) < cancelAfterChunks) spin.SpinOnce(-1);
                if (Volatile.Read(ref finished)) return;
                // Cancel()은 플래그를 먼저 세우고 콜백을 나중에 돌린다. 등록 콜백으로 시각을 찍으면 작업이 먼저 반환해
                // 0을 읽는 경합이 있으므로 Cancel 직전에 찍는다.
                Interlocked.Exchange(ref cancelledAt, Stopwatch.GetTimestamp());
                cts.Cancel();
            });
            try
            {
                Assert.ThrowsAny<OperationCanceledException>(() => fit(cts.Token));
                long returnedAt = Stopwatch.GetTimestamp();
                long cancelStamp = Interlocked.Read(ref cancelledAt);
                Assert.NotEqual(0, cancelStamp);
                var latency = Stopwatch.GetElapsedTime(cancelStamp, returnedAt);
                Assert.True(latency < TimeSpan.FromSeconds(1), $"cancel took {latency}");
                long afterReturn = Interlocked.Read(ref probe[0]);
                Assert.True(afterReturn >= cancelAfterChunks);
                Thread.Sleep(400);
                Assert.Equal(afterReturn, Interlocked.Read(ref probe[0]));
            }
            finally
            {
                Volatile.Write(ref finished, true);
                canceller.Wait();
                KMeansClustering.ChunkProbe = null;
            }
        }

        [Fact]
        public void KMeans_cancels_within_a_second_and_leaves_no_background_work()
        {
            var x = Data(150_000, 12);
            AssertCancelsPromptlyAndStops(ct =>
                KMeansClustering.Fit(x, new KMeansOptions { K = 12, Restarts = 40, Seed = 3, Elbow = false }, null, ct), 300);
        }

        [Fact]
        public void KMeans_large_path_refinement_also_cancels_within_a_second()
        {
            var x = Data(600_000, 12);
            AssertCancelsPromptlyAndStops(ct =>
                KMeansClustering.Fit(x, new KMeansOptions { K = 12, Restarts = 2, Seed = 3, Elbow = false, FullDataLimit = 5_000 }, null, ct), 1500);
        }

        [Fact]
        public void KMeans_with_an_already_cancelled_token_does_no_work()
        {
            var x = Data(20_000, 4);
            var probe = new long[1];
            KMeansClustering.ChunkProbe = probe;
            try
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => KMeansClustering.Fit(x, new KMeansOptions { K = 4 }, null, cts.Token));
                Assert.Equal(0, probe[0]);
            }
            finally { KMeansClustering.ChunkProbe = null; }
        }
    }
}
