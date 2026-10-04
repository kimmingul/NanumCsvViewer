using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // K-means · KNN · 나이브 베이즈. 기준값은 throwaway Python(sklearn 1.7.2)으로 생성하고 여기에 고정했다.
    public class LearningTests
    {
        // 세 덩어리(중심 (0,0), (20,0), (0,20), 각 20점). 오프셋은 RNG가 아니라 고정 수열.
        private static double[,] Blobs()
        {
            double[] ox = { 0, 0.2, -0.1, 0.4, -0.3, 0.1, -0.4, 0.3, -0.2, -0.4, 0, 0.2, -0.5, 0.15, -0.15, 0.35, -0.25, 0.05, -0.05, 0.25 };
            double[] oy = { 0, 0.1, 0.3, -0.2, -0.1, 0.5, 0.2, 0.3, -0.4, 0.1, -0.3, -0.5, 0, 0.15, 0.25, -0.35, 0.4, 0.2, -0.2, 0 };
            double[] cx = { 0, 20, 0 };
            double[] cy = { 0, 0, 20 };
            var x = new double[60, 2];
            for (int b = 0; b < 3; b++)
                for (int i = 0; i < 20; i++)
                {
                    x[b * 20 + i, 0] = cx[b] + ox[i];
                    x[b * 20 + i, 1] = cy[b] + oy[i];
                }
            return x;
        }

        // sklearn: ((X[labels==k] - X[labels==k].mean(0))**2).sum() over k, and KMeans(n_clusters=3, n_init=10).inertia_ on this set.
        private const double BlobInertia = 8.45625;
        private const double BlobClusterInertia = 2.81875;
        // sklearn.metrics.silhouette_score(X, labels, metric="euclidean")
        private const double BlobSilhouette = 0.9749712900948418;

        [Fact]
        public void KMeans_recovers_separated_blobs_and_matches_assignment_inertia()
        {
            var result = KMeansClustering.Fit(Blobs(), new KMeansOptions
            {
                K = 3, Restarts = 5, Seed = 1, Scaling = ScalingMethod.None, Elbow = true,
            });

            Assert.True(result.LloydFixedPoint);
            Assert.Equal(1, result.TerminationType);
            Assert.Equal(5, result.ConvergedRestarts);
            Assert.Equal(0, result.EmptyClusters);
            AssertPartition(result.Assignment, 20);
            Assert.Equal(new[] { 20, 20, 20 }, result.Clusters.Select(c => c.Size).OrderBy(s => s).ToArray());
            Close(BlobInertia, result.TotalWss, 1e-9);
            foreach (var c in result.Clusters) Close(BlobClusterInertia, c.WithinSs, 1e-9);
            Close(BlobSilhouette, result.Silhouette, 1e-6);
            Assert.False(result.SilhouetteSampled);

            var elbowK = result.Elbow!.Single(e => e.K == 3);
            Assert.True(elbowK.Succeeded);
            Close(result.TotalWss, elbowK.TotalWss, 1e-12);
            var elbow2 = result.Elbow!.Single(e => e.K == 2);
            Assert.True(elbow2.Succeeded);
            Assert.True(elbow2.TotalWss > result.TotalWss);
        }

        [Fact]
        public void KMeans_is_deterministic_and_centers_are_original_unit_means()
        {
            var x = Blobs();
            for (int i = 0; i < 60; i++) x[i, 1] *= 50; // Z-점수가 항등이 아니게
            var options = new KMeansOptions { K = 3, Restarts = 4, Seed = 7, Scaling = ScalingMethod.ZScore };
            var a = KMeansClustering.Fit(x, options);
            var b = KMeansClustering.Fit(x, options);
            Assert.Equal(a.Assignment, b.Assignment);
            Close(a.TotalWss, b.TotalWss, 1e-12);

            AssertPartition(a.Assignment, 20);
            for (int c = 0; c < 3; c++)
            {
                var rows = Enumerable.Range(0, 60).Where(i => a.Assignment[i] == c).ToArray();
                Assert.Equal(20, rows.Length);
                for (int j = 0; j < 2; j++)
                    Close(rows.Average(i => x[i, j]), a.Clusters[c].CenterOriginal[j], 1e-9);
            }
        }

        [Fact]
        public void KMeans_rejects_k_above_the_row_count()
        {
            var x = new double[,] { { 0, 0 }, { 1, 1 }, { 2, 2 } };
            var ex = Assert.Throws<DesignMatrixException>(() =>
                KMeansClustering.Fit(x, new KMeansOptions { K = 4, Scaling = ScalingMethod.None }));
            Assert.Contains("exceeds", ex.Message);
        }

        // sklearn.neighbors.KNeighborsClassifier(n_neighbors=3, metric="euclidean", weights="uniform", algorithm="brute").predict
        // 질의와 학습 점 사이 k-최근접 거리에 동점이 없게 고른 집합. brute와 kd_tree 예측이 같았다.
        [Fact]
        public void Knn_predictions_match_sklearn_without_distance_ties()
        {
            var train = new double[,]
            {
                { 0.0, 0.0 }, { 0.4, 0.1 }, { 0.1, 0.5 }, { 1.2, 0.3 }, { 0.7, 1.1 },
                { 5.0, 5.0 }, { 5.4, 4.7 }, { 4.6, 5.3 }, { 6.1, 5.2 }, { 5.2, 6.4 },
                { 0.2, 5.5 }, { 0.6, 6.1 }, { 1.1, 5.0 }, { 8.0, 0.4 }, { 8.7, 1.1 },
            };
            var y = new[] { 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 0, 0, 0, 1, 1 };
            var queries = new double[,]
            {
                { 0.2, 0.2 }, { 5.1, 5.1 }, { 0.35, 5.8 }, { 8.2, 0.8 }, { 3.0, 3.0 }, { 1.0, 1.0 },
            };
            var pred = KnnClassifier.Predict(train, y, 2, queries, 3);
            Assert.Equal(new[] { 0, 1, 0, 1, 1, 0 }, pred);
        }

        [Fact]
        public void Scaling_is_fit_on_training_rows_only()
        {
            var labels = new[] { 0, 0, 0, 1, 1, 1 };
            var x = new double[,] { { 1 }, { 2 }, { 4 }, { 10 }, { 20 }, { 40 } };
            const double fraction = 0.34;
            const int seed = 1;
            var split = ClassifierEvaluation.Holdout(labels, fraction, seed, stratified: true);
            var eval = KnnClassifier.Evaluate(x, labels, 2, new ClassifierOptions
            {
                Scheme = EvalScheme.Holdout,
                TestFraction = fraction,
                Seed = seed,
                Scaling = ScalingMethod.ZScore,
                Neighbors = 1,
            });

            var trainOnly = FeatureScaler.Fit(x, ScalingMethod.ZScore, split.Train);
            var leaked = FeatureScaler.Fit(x, ScalingMethod.ZScore);
            Assert.NotNull(eval.HoldoutScaler);
            Close(trainOnly.Center[0], eval.HoldoutScaler!.Center[0], 1e-12);
            Close(trainOnly.Scale[0], eval.HoldoutScaler.Scale[0], 1e-12);
            Assert.NotEqual(leaked.Center[0], trainOnly.Center[0]);
        }

        // sklearn.naive_bayes.GaussianNB().fit(X, y).predict_proba(Q)
        [Fact]
        public void GaussianNB_predict_proba_matches_sklearn()
        {
            var x = new double[,]
            {
                { 1.0, 2.0 }, { 1.2, 1.8 }, { 0.8, 2.2 }, { 1.1, 2.1 },
                { 5.0, 5.0 }, { 5.2, 4.7 }, { 4.8, 5.3 }, { 5.1, 4.9 },
                { 0.0, 5.0 }, { 0.2, 5.4 }, { 9.0, 0.5 }, { 8.5, 0.2 },
            };
            var y = new[] { 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1 };
            var q = new double[,] { { 1.0, 2.0 }, { 5.0, 5.0 }, { 2.5, 3.5 }, { 8.0, 1.0 } };
            double[,] expected =
            {
                { 9.9736751540244561e-01, 2.6324845975544552e-03 },
                { 2.9708669551667096e-19, 1.0 },
                { 2.4640258557792380e-02, 9.7535974144220805e-01 },
                { 2.8007857188855478e-55, 1.0 },
            };
            var model = NaiveBayesModel.Fit(x, y, 2, NumericGroups(2));
            for (int i = 0; i < 4; i++)
            {
                var proba = model.PredictProba(q, i);
                Close(expected[i, 0], proba[0], 1e-9);
                Close(expected[i, 1], proba[1], 1e-9);
                Close(1.0, proba[0] + proba[1], 1e-12);
            }
            Close(0.5, model.Prior[0], 1e-12);
            Close(0.5, model.Prior[1], 1e-12);
        }

        // sklearn.naive_bayes.CategoricalNB(alpha=1).fit(codes, y).predict_proba(Q)
        // 정수 코드를 수준 순서 원-핫으로 펼친 뒤 같은 우도를 쓴다.
        [Fact]
        public void CategoricalNB_predict_proba_matches_sklearn()
        {
            var codes = new[,]
            {
                { 0, 0 }, { 0, 1 }, { 1, 0 }, { 1, 1 }, { 2, 0 }, { 2, 1 },
                { 0, 0 }, { 2, 1 }, { 1, 0 }, { 0, 1 }, { 2, 0 }, { 1, 1 },
            };
            var y = new[] { 0, 0, 0, 0, 1, 1, 0, 1, 0, 1, 1, 0 };
            var (x, groups) = OneHot(codes, new[] { 3, 2 });
            var qCodes = new[,] { { 0, 0 }, { 2, 1 }, { 1, 0 }, { 2, 0 } };
            var (q, _) = OneHot(qCodes, new[] { 3, 2 });
            double[,] expected =
            {
                { 0.7438330170777986, 0.25616698292220136 },
                { 0.14837244511733524, 0.8516275548826648 },
                { 0.8789237668161435, 0.1210762331838566 },
                { 0.22502870264064273, 0.7749712973593572 },
            };
            var model = NaiveBayesModel.Fit(x, y, 2, groups);
            Assert.Empty(model.NumericColumns);
            Assert.Equal(2, model.Categorical.Length);
            for (int i = 0; i < 4; i++)
            {
                var proba = model.PredictProba(q, i);
                Close(expected[i, 0], proba[0], 1e-10);
                Close(expected[i, 1], proba[1], 1e-10);
            }
        }

        // 혼합 결합 로그확률 = GaussianNB._joint_log_likelihood + CategoricalNB._joint_log_likelihood - log prior
        // (각 단일 모형이 사전확률을 한 번씩 포함하므로).
        [Fact]
        public void MixedNB_joint_log_probability_is_the_sum_of_components()
        {
            var num = new double[,]
            {
                { 0.5, 1.0 }, { 0.7, 1.2 }, { 0.4, 0.8 }, { 1.5, 0.2 },
                { 4.0, 3.5 }, { 4.2, 3.1 }, { 3.8, 3.8 }, { 5.0, 2.5 },
                { 0.2, 4.0 }, { 0.9, 3.6 }, { 4.5, 0.5 }, { 3.9, 1.1 },
            };
            var codes = new[,]
            {
                { 0, 1 }, { 0, 1 }, { 1, 1 }, { 0, 0 }, { 2, 0 }, { 2, 0 },
                { 1, 0 }, { 2, 1 }, { 0, 1 }, { 1, 1 }, { 2, 0 }, { 2, 1 },
            };
            var y = new[] { 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1 };
            var (hot, catGroups) = OneHot(codes, new[] { 3, 2 });
            var x = HStack(num, hot);
            var groups = NumericGroups(2).Concat(catGroups.Select(g =>
                new FeatureGroup(g.SourceColumn + 10, g.Columns.Select(c => c + 2).ToArray(), true))).ToArray();
            var qNum = new double[,] { { 0.6, 1.1 }, { 4.1, 3.2 }, { 1.0, 3.0 }, { 4.4, 0.8 } };
            var qCodes = new[,] { { 0, 1 }, { 2, 0 }, { 1, 1 }, { 2, 1 } };
            var (qHot, _) = OneHot(qCodes, new[] { 3, 2 });
            var q = HStack(qNum, qHot);
            double[,] expected =
            {
                { -3.056900528057951, -44.68293803529904 },
                { -38.80252176949185, -2.976096889422225 },
                { -4.019615925133895, -35.39212769605554 },
                { -43.50436315431104, -4.185548402430436 },
            };

            var model = NaiveBayesModel.Fit(x, y, 2, groups);
            Assert.Equal(2, model.NumericColumns.Length);
            Assert.Equal(2, model.Categorical.Length);
            for (int i = 0; i < 4; i++)
            {
                var joint = model.JointLogProbability(q, i);
                for (int c = 0; c < 2; c++)
                {
                    double sum = model.LogPrior[c] + model.NumericLogLikelihood(q, i, c) + model.CategoricalLogLikelihood(q, i, c);
                    Close(sum, joint[c], 1e-12);
                    Close(expected[i, c], joint[c], 1e-8);
                }
            }
        }

        [Fact]
        public void Classification_metrics_match_an_independent_confusion_recount()
        {
            var x = new double[,]
            {
                { 0.0, 0.0 }, { 0.2, 0.1 }, { 0.1, -0.2 }, { 0.3, 0.2 },
                { 5.0, 5.0 }, { 5.2, 4.8 }, { 4.7, 5.1 }, { 5.4, 5.3 },
            };
            var y = new[] { 0, 0, 0, 0, 1, 1, 1, 1 };
            var eval = KnnClassifier.Evaluate(x, y, 2, new ClassifierOptions
            {
                Scheme = EvalScheme.Holdout,
                TestFraction = 0.34,
                Seed = 2,
                Scaling = ScalingMethod.None,
                Neighbors = 1,
            });
            var actual = eval.HoldoutActual!;
            var pred = eval.HoldoutPredicted!;
            Assert.Equal(actual.Length, pred.Length);
            Assert.True(actual.Length >= 2);

            var cm = new long[2, 2];
            long correct = 0;
            var support = new long[2];
            for (int i = 0; i < actual.Length; i++)
            {
                cm[actual[i], pred[i]]++;
                support[actual[i]]++;
                if (actual[i] == pred[i]) correct++;
            }
            for (int a = 0; a < 2; a++)
                for (int p = 0; p < 2; p++)
                    Assert.Equal(cm[a, p], eval.Metrics.Confusion[a, p]);
            Close((double)correct / actual.Length, eval.Metrics.Accuracy, 1e-12);
            for (int c = 0; c < 2; c++)
            {
                long predictedC = cm[0, c] + cm[1, c];
                double precision = predictedC == 0 ? 0 : (double)cm[c, c] / predictedC;
                double recall = support[c] == 0 ? 0 : (double)cm[c, c] / support[c];
                double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
                Close(precision, eval.Metrics.Precision[c], 1e-12);
                Close(recall, eval.Metrics.Recall[c], 1e-12);
                Close(f1, eval.Metrics.F1[c], 1e-12);
            }
            Close(eval.Metrics.F1.Average(), eval.Metrics.MacroF1, 1e-12);
            long majority = support.Max();
            Close((double)majority / actual.Length, eval.MajorityBaseline, 1e-12);
        }

        [Fact]
        public void Feature_groups_treat_a_repeated_source_as_categorical()
        {
            var sources = new[] { 3, 7, 7, 7, 9 };
            var names = new[] { "age", "color=red", "color=blue", "color=green", "income" };
            var groups = FeatureGroups.FromMatrix(sources, names);
            Assert.Equal(3, groups.Count);
            Assert.False(groups[0].Categorical);
            Assert.Equal(new[] { 0 }, groups[0].Columns);
            Assert.True(groups[1].Categorical);
            Assert.Equal(new[] { 1, 2, 3 }, groups[1].Columns);
            Assert.Equal(7, groups[1].SourceColumn);
            Assert.False(groups[2].Categorical);
        }

        private static void AssertPartition(int[] assignment, int perBlob)
        {
            int blobs = assignment.Length / perBlob;
            var blobToCluster = new int[blobs];
            for (int b = 0; b < blobs; b++) blobToCluster[b] = -1;
            var clusterToBlob = new Dictionary<int, int>();
            for (int i = 0; i < assignment.Length; i++)
            {
                int b = i / perBlob;
                int c = assignment[i];
                if (blobToCluster[b] < 0) blobToCluster[b] = c;
                Assert.Equal(blobToCluster[b], c);
                if (clusterToBlob.TryGetValue(c, out int other)) Assert.Equal(other, b);
                else clusterToBlob[c] = b;
            }
            Assert.Equal(blobs, clusterToBlob.Count);
        }

        private static FeatureGroup[] NumericGroups(int p)
        {
            var g = new FeatureGroup[p];
            for (int j = 0; j < p; j++) g[j] = new FeatureGroup(j, new[] { j }, false);
            return g;
        }

        private static (double[,] X, FeatureGroup[] Groups) OneHot(int[,] codes, int[] levels)
        {
            int n = codes.GetLength(0), f = codes.GetLength(1), p = levels.Sum();
            var x = new double[n, p];
            var groups = new FeatureGroup[f];
            int col = 0;
            for (int j = 0; j < f; j++)
            {
                var cols = new int[levels[j]];
                for (int l = 0; l < levels[j]; l++)
                {
                    cols[l] = col + l;
                    for (int i = 0; i < n; i++)
                        if (codes[i, j] == l) x[i, col + l] = 1;
                }
                groups[j] = new FeatureGroup(j, cols, true);
                col += levels[j];
            }
            return (x, groups);
        }

        private static double[,] HStack(double[,] a, double[,] b)
        {
            int n = a.GetLength(0), pa = a.GetLength(1), pb = b.GetLength(1);
            var x = new double[n, pa + pb];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < pa; j++) x[i, j] = a[i, j];
                for (int j = 0; j < pb; j++) x[i, pa + j] = b[i, j];
            }
            return x;
        }

        private static void Close(double expected, double actual, double tol)
        {
            if (double.IsNaN(expected) || double.IsNaN(actual))
                Assert.True(double.IsNaN(expected) && double.IsNaN(actual), $"expected {expected}, actual {actual}");
            else
                Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, actual {actual}, tol {tol}");
        }
    }
}
