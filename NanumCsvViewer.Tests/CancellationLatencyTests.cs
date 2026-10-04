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

        [Fact]
        public void KMeans_cancels_without_waiting_for_the_native_call()
        {
            var x = Data(150_000, 12);
            using var cts = new CancellationTokenSource(50);
            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                KMeansClustering.Fit(x, new KMeansOptions { K = 12, Restarts = 40, Seed = 3, Elbow = false }, null, cts.Token));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"cancel took {sw.Elapsed}");
        }
    }
}
