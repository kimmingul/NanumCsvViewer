using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // PCA·LDA를 sklearn과 대조한다.
    // PCA: sklearn.decomposition.PCA(svd_solver='full').fit(X).explained_variance_ / components_
    //      상관 옵션은 표본 표준편차(ddof=1)로 표준화한 Z에 같은 PCA.
    // LDA: sklearn.discriminant_analysis.LinearDiscriminantAnalysis(solver='svd').fit(X, y)
    public class DimensionReductionTests
    {
        // 고유값이 잘 갈라지도록 잡은 12×4. 상수 열 없음.
        private static double[,] PcaX() => new double[,]
        {
            { 2.1, 1.0, 0.4, 8.0 },
            { 2.4, 1.2, 0.3, 7.5 },
            { 2.0, 0.9, 0.5, 8.2 },
            { 5.1, 3.2, 1.1, 2.0 },
            { 5.4, 3.0, 1.4, 1.8 },
            { 4.8, 3.5, 1.0, 2.3 },
            { 1.1, 4.2, 2.5, 5.0 },
            { 1.4, 4.0, 2.8, 4.6 },
            { 0.9, 4.5, 2.2, 5.4 },
            { 3.3, 2.1, 3.6, 3.1 },
            { 3.8, 1.8, 3.2, 3.5 },
            { 3.0, 2.4, 3.9, 2.8 },
        };

        private static string[] PcaNames() => new[] { "a", "b", "c", "d" };

        // sklearn PCA(svd_solver='full').fit(X) — 성분은 행.
        private static readonly double[] CovEigen = { 7.510810916545094, 2.587737167415585, 1.0916582976496807, 0.006611800207830771 };
        private static readonly double[] CovRatio = { 0.6707986853570087, 0.2311136186544182, 0.09749718892661453, 0.0005905070619586606 };
        private static readonly double[,] CovComp =
        {
            { -0.418415223296, -0.219075918955, -0.22117239906, 0.853239246954 },
            { 0.658812847319, -0.516931468125, -0.544355482315, 0.049240210633 },
            { -0.011933828938, 0.717738893812, -0.696207099562, -0.002034344326 },
            { 0.625101525233, 0.411871920998, 0.412377925316, 0.519185949885 },
        };

        // Z = (X - mean) / std(ddof=1); PCA(svd_solver='full').fit(Z)
        private static readonly double[] CorrEigen = { 2.021752820014093, 1.3101031281593485, 0.6659310916741489, 0.0022129601524097093 };
        private static readonly double[] CorrRatio = { 0.5054382050035233, 0.3275257820398372, 0.16648277291853725, 0.0005532400381024274 };
        private static readonly double[,] CorrComp =
        {
            { -0.388451104151, -0.435459022278, -0.431394471151, 0.688026154925 },
            { 0.727933355202, -0.46240840104, -0.473673437559, -0.178675615523 },
            { -0.004926514265, 0.710633467158, -0.703520626852, 0.005876427962 },
            { 0.564973007785, 0.30257889206, 0.30755651904, 0.703321051975 },
        };

        [Fact]
        public void Pca_covariance_matches_sklearn_explained_variance_and_components()
        {
            var pca = PrincipalComponents.Fit(PcaX(), PcaNames(), PcaScale.Covariance, 4);
            Assert.True(pca.Exact);
            Assert.Equal(4, pca.ComponentCount);
            Assert.Equal(3, pca.KaiserCount); // 7.51, 2.59, 1.09 > 1; 0.0066은 아님
            Close(CovEigen, pca.Eigenvalues);
            Close(CovRatio, pca.ExplainedRatio);
            CloseColumns(CovComp, pca.Loadings);
            AssertSign(pca.Loadings);
            Close(1, pca.CumulativeRatio[^1], 1e-9, 1e-12);
        }

        [Fact]
        public void Pca_correlation_matches_sklearn_on_ddof1_standardized_data()
        {
            var pca = PrincipalComponents.Fit(PcaX(), PcaNames(), PcaScale.Correlation, 4);
            Assert.True(Math.Abs(pca.Eigenvalues[0] - CovEigen[0]) > 1);
            Close(CorrEigen, pca.Eigenvalues);
            Close(CorrRatio, pca.ExplainedRatio);
            CloseColumns(CorrComp, pca.Loadings);
            AssertSign(pca.Loadings);
            // 상관행렬의 대각합은 p.
            Close(4, pca.Eigenvalues.Sum(), 1e-9, 1e-9);
            Assert.Equal(2, pca.KaiserCount);
        }

        [Fact]
        public void Pca_clamps_requested_components_and_rejects_one_row()
        {
            var pca = PrincipalComponents.Fit(PcaX(), PcaNames(), PcaScale.Covariance, 99);
            Assert.Equal(4, pca.ComponentCount);
            Assert.Equal(99, pca.RequestedComponents);

            var one = new double[1, 2];
            var ex = Assert.Throws<DesignMatrixException>(() => PrincipalComponents.Fit(one, new[] { "a", "b" }, PcaScale.Covariance, 1));
            Assert.Contains("at least 2", ex.Message);
        }

        [Fact]
        public void Pca_constant_column_is_reported_and_adds_a_zero_eigenvalue()
        {
            var x = new double[,]
            {
                { 1, 5, 2 },
                { 2, 5, 3 },
                { 4, 5, 1 },
                { 3, 5, 4 },
            };
            var pca = PrincipalComponents.Fit(x, new[] { "a", "const", "b" }, PcaScale.Correlation, 3);
            Assert.Equal(1, pca.ConstantFeatures);
            Assert.Contains(pca.Eigenvalues, v => Math.Abs(v) < 1e-8);
        }

        // 20×3, 클래스 0/1/2. sklearn LinearDiscriminantAnalysis(solver='svd').fit(X, y)
        private static double[,] LdaX() => new double[,]
        {
            { 1.0, 2.1, 0.4 },
            { 1.2, 1.8, 0.6 },
            { 0.8, 2.4, 0.3 },
            { 1.1, 2.0, 0.5 },
            { 0.9, 2.3, 0.2 },
            { 1.3, 1.9, 0.7 },
            { 3.2, 0.4, 1.5 },
            { 3.5, 0.6, 1.8 },
            { 2.9, 0.3, 1.4 },
            { 3.4, 0.8, 1.6 },
            { 3.0, 0.5, 1.7 },
            { 3.6, 0.2, 1.9 },
            { 0.4, 3.1, 2.8 },
            { 0.6, 3.4, 2.5 },
            { 0.2, 2.9, 3.0 },
            { 0.5, 3.3, 2.6 },
            { 0.3, 3.0, 2.9 },
            { 0.7, 3.5, 2.4 },
            { 2.2, 1.5, 1.1 },
            { 2.8, 1.1, 1.3 },
        };

        private static int[] LdaY() => new[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 1, 2 };

        [Fact]
        public void Lda_predictions_probabilities_and_variance_ratio_match_sklearn_svd()
        {
            var x = LdaX();
            var y = LdaY();
            var model = LinearDiscriminant.Fit(x, y, 3, computeDirections: true);
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 1, 1 }, model.Predict(x));
            Close(new[] { 0.7340535211993247, 0.2659464788006753 }, model.ExplainedVarianceRatio);
            Assert.False(model.Singular);
            Assert.Equal(3, model.WithinRank);

            var proba = model.PredictProba(x);
            // 행 0은 거의 확실, 18·19는 경계. sklearn predict_proba.
            CloseRow(proba, 0, 9.999999997873e-01, 1.095206715700e-10, 1.031555352643e-10);
            CloseRow(proba, 18, 1.707416190804e-02, 9.523952474409e-01, 3.053059065103e-02);
            CloseRow(proba, 19, 4.988733809429e-07, 9.974213769012e-01, 2.578124225428e-03);
            Close(new[] { 0.3, 0.35, 0.35 }, model.Priors);

            Assert.True(model.UsedFisherDirections);
            Assert.Equal(2, model.DirectionCount); // K−1
            for (int c = 0; c < model.DirectionCount; c++)
            {
                double ss = 0, maxAbs = -1, signed = 0;
                for (int j = 0; j < 3; j++)
                {
                    ss += model.Directions[j, c] * model.Directions[j, c];
                    double a = Math.Abs(model.Directions[j, c]);
                    if (a > maxAbs) { maxAbs = a; signed = model.Directions[j, c]; }
                }
                Close(1, Math.Sqrt(ss), 1e-8, 1e-8);
                Assert.True(signed >= 0);
            }
        }

        [Fact]
        public void Lda_holdout_predictions_match_sklearn_fit_on_the_same_rows()
        {
            var x = LdaX();
            var y = LdaY();
            // 앞 16행 학습, 뒤 4행 예측. sklearn fit(X[:16], y[:16]).predict(X[16:]) == [2, 2, 0, 1]
            var train = Enumerable.Range(0, 16).ToArray();
            var test = new[] { 16, 17, 18, 19 };
            var (xt, yt) = Slice(x, y, train);
            var model = LinearDiscriminant.Fit(xt, yt, 3, computeDirections: false);
            Assert.Equal(new[] { 2, 2, 0, 1 }, model.Predict(x, test));
            var proba = model.PredictProba(x, test);
            CloseRow(proba, 2, 9.997574552210e-01, 2.425447790421e-04, 1.711647727950e-137);
            Close(new[] { 0.9029685804568411, 0.09703141954315882 }, model.ExplainedVarianceRatio);
        }

        [Fact]
        public void Lda_singular_covariance_is_flagged_and_still_matches_sklearn()
        {
            var raw = LdaX();
            int n = raw.GetLength(0);
            var x = new double[n, 3];
            for (int i = 0; i < n; i++)
            {
                x[i, 0] = raw[i, 0];
                x[i, 1] = raw[i, 1];
                x[i, 2] = raw[i, 0] + raw[i, 1]; // 완전 공선
            }
            var model = LinearDiscriminant.Fit(x, LdaY(), 3);
            Assert.True(model.Singular);
            Assert.Equal(2, model.WithinRank);
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 0, 2, 2, 2, 1, 1 }, model.Predict(x));
            Close(new[] { 0.8311933307214098, 0.16880666927859023 }, model.ExplainedVarianceRatio);
            CloseRow(model.PredictProba(x), 14, 6.261407419892e-01, 2.944703489212e-05, 3.738298109759e-01);
        }

        [Fact]
        public void Lda_evaluation_uses_training_rows_only_and_reports_majority_baseline()
        {
            var x = LdaX();
            var y = LdaY();
            var names = new[] { "a", "b", "c" };
            var eval = LinearDiscriminant.Evaluate(x, y, 3, names, LdaSplitKind.Holdout, 5, 0.25, seed: 7, stratified: true);
            var split = ClassifierEvaluation.Holdout(y, 0.25, 7, true);
            var counts = new int[3];
            foreach (int i in split.Train) counts[y[i]]++;
            int majority = 0;
            for (int c = 1; c < 3; c++) if (counts[c] > counts[majority]) majority = c;
            var (xt, yt) = Slice(x, y, split.Train);
            int present = counts.Count(c => c > 0);
            var model = LinearDiscriminant.Fit(xt, yt, present, computeDirections: false);
            var pred = model.Predict(x, split.Test);
            int correct = 0, baseCorrect = 0;
            for (int t = 0; t < split.Test.Length; t++)
            {
                if (pred[t] == y[split.Test[t]]) correct++;
                if (y[split.Test[t]] == majority) baseCorrect++;
            }
            Close((double)correct / split.Test.Length, eval.Metrics.Accuracy, 1e-12, 1e-12);
            Close((double)baseCorrect / split.Test.Length, eval.MajorityBaselineAccuracy, 1e-12, 1e-12);
            Assert.Equal(split.Test.Length, (int)eval.Metrics.Total);
            Assert.True(eval.Metrics.MacroF1 >= 0 && eval.Metrics.MacroF1 <= 1);

            var ex = Assert.Throws<DesignMatrixException>(() => LinearDiscriminant.Fit(x, new int[x.GetLength(0)], 1));
            Assert.Contains("2 classes", ex.Message);
        }

        // 회귀: 평가가 특성 이름을 클래스 수로 만들어 p ≠ K에서 ArgumentException이 나던 문제.
        [Fact]
        public void Lda_evaluation_accepts_feature_count_different_from_class_count()
        {
            var full = LdaX();
            int n = full.GetLength(0);
            var x = new double[n, 2];
            for (int i = 0; i < n; i++) { x[i, 0] = full[i, 0]; x[i, 1] = full[i, 1]; }
            var y = LdaY();
            var eval = LinearDiscriminant.Evaluate(x, y, 3, new[] { "a", "b", "c" }, LdaSplitKind.KFold, 4, 0.25, seed: 3, stratified: true);
            Assert.Equal(n, (int)eval.Metrics.Total);
            Assert.Equal(4, eval.SplitCount);
        }

        private static (double[,] x, int[] y) Slice(double[,] source, int[] labels, int[] rows)
        {
            int p = source.GetLength(1);
            var x = new double[rows.Length, p];
            var y = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                for (int j = 0; j < p; j++) x[i, j] = source[rows[i], j];
                y[i] = labels[rows[i]];
            }
            return (x, y);
        }

        private static void AssertSign(double[,] loadings)
        {
            int p = loadings.GetLength(0), m = loadings.GetLength(1);
            for (int c = 0; c < m; c++)
            {
                int maxI = 0;
                double maxAbs = -1;
                for (int j = 0; j < p; j++)
                {
                    double a = Math.Abs(loadings[j, c]);
                    if (a > maxAbs) { maxAbs = a; maxI = j; }
                }
                Assert.True(loadings[maxI, c] >= 0);
            }
        }

        private static void CloseColumns(double[,] sklearnRows, double[,] loadings)
        {
            int m = sklearnRows.GetLength(0), p = sklearnRows.GetLength(1);
            Assert.Equal(p, loadings.GetLength(0));
            Assert.Equal(m, loadings.GetLength(1));
            for (int c = 0; c < m; c++)
                for (int j = 0; j < p; j++)
                    Close(sklearnRows[c, j], loadings[j, c]);
        }

        private static void CloseRow(double[,] proba, int row, params double[] expected)
        {
            for (int c = 0; c < expected.Length; c++) Close(expected[c], proba[row, c], 1e-6, 1e-8);
        }

        private static void Close(double[] expected, double[] actual, double rel = 1e-8, double abs = 1e-8)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Close(expected[i], actual[i], rel, abs);
        }

        private static void Close(double expected, double actual, double rel = 1e-8, double abs = 1e-8)
        {
            double tol = Math.Max(abs, rel * Math.Max(Math.Abs(expected), Math.Abs(actual)));
            Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, actual {actual}, tol {tol}");
        }
    }
}
