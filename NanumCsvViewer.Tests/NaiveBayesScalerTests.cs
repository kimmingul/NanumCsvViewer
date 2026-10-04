using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    public class NaiveBayesScalerTests
    {
        [Fact]
        public void Saved_scaler_leaves_one_hot_columns_untouched()
        {
            // 열 0: 수치, 열 1·2: 범주 원-핫. 저장 모형 예측은 같은 변환으로 점수를 내므로 원-핫 열이 바뀌면 안 된다.
            var x = new double[,] { { 1, 1, 0 }, { 3, 0, 1 }, { 5, 1, 0 }, { 9, 0, 1 }, { 2, 1, 0 }, { 8, 0, 1 } };
            var labels = new[] { 0, 1, 0, 1, 0, 1 };
            var groups = new List<FeatureGroup>
            {
                new FeatureGroup(0, new[] { 0 }, false),
                new FeatureGroup(1, new[] { 1, 2 }, true),
            };
            var fit = NaiveBayesClassifier.Evaluate(x, labels, 2, groups, new ClassifierOptions
            {
                Scheme = EvalScheme.KFold, Folds = 2, Scaling = ScalingMethod.ZScore,
            });
            var scaler = fit.ParameterScaler!;
            Assert.Equal(1.0, scaler.Scale[1]);
            Assert.Equal(1.0, scaler.Scale[2]);
            Assert.Equal(0, scaler.Center[1]);
            Assert.Equal(0, scaler.Center[2]);
            Assert.NotEqual(0, scaler.Center[0]);
            var z = scaler.Transform(x);
            for (int i = 0; i < 6; i++) { Assert.Equal(x[i, 1], z[i, 1]); Assert.Equal(x[i, 2], z[i, 2]); }
        }
    }
}
