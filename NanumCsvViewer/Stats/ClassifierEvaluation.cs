namespace NanumCsvViewer.Stats
{
    /// <summary>회귀 예측 성능(평가 행 기준). R²는 평가 행 평균 기준(sklearn r2_score).</summary>
    public sealed record RegressionMetrics(double Rmse, double Mae, double RSquared, long Total)
    {
        public static RegressionMetrics From(IReadOnlyList<double> actual, IReadOnlyList<double> predicted)
        {
            if (actual.Count != predicted.Count) throw new ArgumentException("Length mismatch.", nameof(predicted));
            int n = actual.Count;
            if (n == 0) return new RegressionMetrics(double.NaN, double.NaN, double.NaN, 0);
            double mean = 0;
            for (int i = 0; i < n; i++) mean += actual[i];
            mean /= n;
            double sse = 0, sae = 0, sst = 0;
            for (int i = 0; i < n; i++)
            {
                double e = actual[i] - predicted[i];
                sse += e * e;
                sae += Math.Abs(e);
                sst += (actual[i] - mean) * (actual[i] - mean);
            }
            return new RegressionMetrics(Math.Sqrt(sse / n), sae / n, sst == 0 ? double.NaN : 1 - sse / sst, n);
        }
    }
}

namespace NanumCsvViewer.Stats
{
    /// <summary>학습/평가 행 인덱스 분할.</summary>
    public sealed record DataSplit(int[] Train, int[] Test);

    /// <summary>분류 성능. Confusion[a, p] = 실제 a · 예측 p 건수.</summary>
    public sealed record ClassificationMetrics(
        long[,] Confusion,
        double Accuracy,
        double[] Precision,
        double[] Recall,
        double[] F1,
        long[] Support,
        double MacroF1,
        long Total);

    /// <summary>
    /// 분류기 평가 공용(이슈 #27): 층화 홀드아웃·층화 k-겹 분할과 혼동행렬 지표. 시드 고정으로 결정적.
    /// 스케일링 등 전처리는 반드시 Train 인덱스로만 적합해야 한다(누수 방지) — 호출자 책임.
    /// </summary>
    public static class ClassifierEvaluation
    {
        /// <summary>층화(stratified=true) 또는 단순 무작위 홀드아웃. 각 클래스에서 최소 1개는 학습에 남긴다.</summary>
        public static DataSplit Holdout(int[] labels, double testFraction, int seed, bool stratified = true)
        {
            if (!(testFraction > 0 && testFraction < 1)) throw new ArgumentOutOfRangeException(nameof(testFraction));
            var rng = new Random(seed);
            var train = new List<int>();
            var test = new List<int>();
            foreach (var group in Groups(labels, stratified))
            {
                var idx = group.ToArray();
                Shuffle(idx, rng);
                int nTest = (int)Math.Round(idx.Length * testFraction);
                nTest = Math.Min(nTest, idx.Length - 1);
                for (int i = 0; i < idx.Length; i++) (i < nTest ? test : train).Add(idx[i]);
            }
            train.Sort();
            test.Sort();
            if (test.Count == 0) throw new InvalidOperationException("The test split is empty; use a larger test fraction or more rows.");
            return new DataSplit(train.ToArray(), test.ToArray());
        }

        /// <summary>k-겹 분할(층화 시 각 클래스를 겹마다 고르게 배분).</summary>
        public static IReadOnlyList<DataSplit> KFold(int[] labels, int k, int seed, bool stratified = true)
        {
            if (k < 2) throw new ArgumentOutOfRangeException(nameof(k));
            if (labels.Length < k) throw new InvalidOperationException($"Need at least {k} rows for {k}-fold cross-validation.");
            var rng = new Random(seed);
            var foldOf = new int[labels.Length];
            int offset = 0;
            foreach (var group in Groups(labels, stratified))
            {
                var idx = group.ToArray();
                Shuffle(idx, rng);
                for (int i = 0; i < idx.Length; i++) foldOf[idx[i]] = (offset + i) % k;
                offset += idx.Length;
            }
            var result = new List<DataSplit>(k);
            for (int f = 0; f < k; f++)
            {
                var train = new List<int>();
                var test = new List<int>();
                for (int i = 0; i < labels.Length; i++) (foldOf[i] == f ? test : train).Add(i);
                result.Add(new DataSplit(train.ToArray(), test.ToArray()));
            }
            return result;
        }

        public static ClassificationMetrics Metrics(IReadOnlyList<int> actual, IReadOnlyList<int> predicted, int classCount)
        {
            if (actual.Count != predicted.Count) throw new ArgumentException("Length mismatch.", nameof(predicted));
            var cm = new long[classCount, classCount];
            for (int i = 0; i < actual.Count; i++) cm[actual[i], predicted[i]]++;
            return FromConfusion(cm);
        }

        /// <summary>혼동행렬에서 지표 계산(k-겹 합산 행렬에도 사용). 분모 0인 정밀도·재현율은 0(sklearn zero_division=0).</summary>
        public static ClassificationMetrics FromConfusion(long[,] cm)
        {
            int k = cm.GetLength(0);
            long total = 0, correct = 0;
            var precision = new double[k];
            var recall = new double[k];
            var f1 = new double[k];
            var support = new long[k];
            for (int a = 0; a < k; a++)
                for (int p = 0; p < k; p++)
                {
                    total += cm[a, p];
                    if (a == p) correct += cm[a, p];
                    support[a] += cm[a, p];
                }
            for (int c = 0; c < k; c++)
            {
                long predictedC = 0;
                for (int a = 0; a < k; a++) predictedC += cm[a, c];
                precision[c] = predictedC == 0 ? 0 : (double)cm[c, c] / predictedC;
                recall[c] = support[c] == 0 ? 0 : (double)cm[c, c] / support[c];
                f1[c] = precision[c] + recall[c] == 0 ? 0 : 2 * precision[c] * recall[c] / (precision[c] + recall[c]);
            }
            return new ClassificationMetrics(cm, total == 0 ? double.NaN : (double)correct / total,
                precision, recall, f1, support, k == 0 ? double.NaN : f1.Average(), total);
        }

        private static IEnumerable<IEnumerable<int>> Groups(int[] labels, bool stratified)
            => stratified
                ? Enumerable.Range(0, labels.Length).GroupBy(i => labels[i]).OrderBy(g => g.Key).Select(g => (IEnumerable<int>)g)
                : new[] { Enumerable.Range(0, labels.Length) };

        private static void Shuffle(int[] a, Random rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }
    }
}
