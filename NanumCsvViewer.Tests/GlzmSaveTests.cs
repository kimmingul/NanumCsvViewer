using System.Globalization;
using NanumCsvViewer.Stats;
using Xunit;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// 오프셋·노출·시행 수를 쓴 GLzM의 저장 → 불러오기 → 적용. 참조값은 statsmodels 0.14 GLM
    /// (<c>predict(newdata, offset=…, exposure=…)</c>; 이항은 2열 응답)으로 같은 학습 자료를 적합해 새 자료에 예측한 값이다.
    /// </summary>
    public class GlzmSaveTests
    {
        // 학습 40행, 새 자료 8행 (numpy seed 7). 열: x, g(a/b/c), expo, off, t(시행 수), y(포아송 횟수), s(성공 횟수), b(0/1)
        static readonly double[] TrX = [0.001, 0.299, -0.274, -0.891, -0.455, -0.992, 0.06, 1.34, -0.492, -0.62, 0.49, 0.357, 0.105, -0.93, -0.029, 0.695, -1.344, -0.458, -1.901, -1.29, -1.842, -0.235, -1.267, 0.271, 0.157, -0.187, -2.517, -0.539, -0.049, 0.113, -1.53, -0.478, -0.979, -0.809, 1.061, -0.808, -0.033, 0.884, -0.584, -0.112];
        static readonly double[] TeX = [-0.671, -1.054, 0.337, 1.407, -1.454, -0.209, -0.632, -1.761];
        static readonly double[] TrExpo = [2.73, 2.87, 1.03, 2.04, 1.34, 1.91, 0.84, 3.89, 1.25, 2.85, 1.55, 3.56, 2.82, 0.96, 3.46, 3.81, 3.66, 2.49, 1.01, 1.17, 3.75, 2.43, 1.13, 3.59, 2.75, 2.49, 1.82, 1.94, 1.34, 0.63, 3.57, 2.14, 2.42, 1.63, 3.13, 0.59, 1.8, 0.61, 0.93, 3.89];
        static readonly double[] TeExpo = [0.72, 1.94, 3.17, 3.35, 3.05, 0.9, 3.7, 3.31];
        static readonly double[] TrOff = [-0.09, -0.27, 0.05, 0.67, -0.25, -0.19, 0.06, 0.15, -0.05, -0.06, 0.21, 0.16, -0.31, -0.02, 0.01, -0.32, 0.08, -0.26, 0.29, 0.06, 0.03, -0.18, -0.04, -0.6, -0.34, 0.11, -0.64, 0.25, -0.52, 0.23, -0.25, 0.23, 0.04, -0.46, 0.37, 0.43, -0.02, -0.08, -0.05, -0.29];
        static readonly double[] TeOff = [-0.01, -0.13, -0.15, 0.19, -0.09, -0.05, 0.01, 0.35];
        static readonly string[] TrG = ["c", "c", "a", "b", "c", "c", "c", "b", "a", "c", "b", "a", "a", "b", "c", "b", "b", "c", "c", "b", "b", "b", "a", "a", "b", "b", "b", "a", "a", "a", "b", "c", "b", "b", "b", "c", "b", "b", "b", "b"];
        static readonly string[] TeG = ["c", "b", "a", "b", "b", "b", "a", "a"];
        static readonly int[] TrT = [19, 19, 25, 15, 28, 26, 14, 15, 22, 28, 7, 6, 11, 15, 9, 17, 18, 28, 23, 11, 24, 25, 21, 21, 12, 22, 8, 20, 28, 29, 12, 13, 20, 14, 23, 10, 9, 6, 16, 10];
        static readonly int[] TrY = [2, 5, 0, 5, 0, 0, 0, 19, 1, 1, 6, 9, 4, 0, 2, 8, 2, 2, 0, 1, 1, 2, 0, 3, 5, 5, 0, 4, 0, 5, 3, 4, 1, 2, 16, 0, 6, 3, 0, 6];
        static readonly int[] TrS = [8, 12, 12, 4, 12, 4, 9, 12, 9, 12, 6, 0, 6, 7, 6, 9, 3, 9, 3, 3, 2, 7, 7, 7, 3, 11, 0, 8, 7, 15, 2, 7, 5, 4, 14, 7, 2, 5, 9, 6];
        static readonly int[] TrB = [1, 0, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 0, 1, 0, 1, 0];
        static readonly int[] TeT = [28, 9, 29, 19, 11, 5, 12, 19];
        static readonly double[] Pois = [0.41339129047695683, 1.4389620743498337, 5.846781072615936, 20.589056784027587, 1.7589112834636111, 1.3391686075564402, 3.95071310956636, 2.179821082549635];
        static readonly double[] BinTrials = [0.39286011306117463, 0.2292280540923169, 0.49543895973327096, 0.7792256781566874, 0.17905693871544764, 0.4029610931697584, 0.33041675776291357, 0.20515412617330972];
        static readonly double[] BinOff = [0.3438293116358555, 0.23130660787778703, 0.5884880839873156, 0.8712523906834984, 0.16590748152323984, 0.4596020315280879, 0.3584693226465071, 0.17897125168173642];
        static readonly double[] BinTrialsExp = [11.00008316571289, 2.063052486830852, 14.367729832264859, 14.80528788497706, 1.969626325869924, 2.014805465848792, 3.965001093154963, 3.8979283972928847];

        static readonly string[] TrainHeaders = ["x", "g", "expo", "off", "t", "y", "s", "b"];

        static List<string[]> TrainRows()
        {
            var rows = new List<string[]>();
            for (int i = 0; i < TrX.Length; i++)
                rows.Add([F(TrX[i]), TrG[i], F(TrExpo[i]), F(TrOff[i]), TrT[i].ToString(CultureInfo.InvariantCulture),
                    TrY[i].ToString(CultureInfo.InvariantCulture), TrS[i].ToString(CultureInfo.InvariantCulture), TrB[i].ToString(CultureInfo.InvariantCulture)]);
            return rows;
        }

        // 새 자료: 열 순서를 학습과 다르게 둬 이름으로 연결하는지 본다(g, off, x, t, expo).
        static readonly string[] NewHeaders = ["g", "off", "x", "t", "expo"];

        static List<string[]> NewRows()
        {
            var rows = new List<string[]>();
            for (int i = 0; i < TeX.Length; i++)
                rows.Add([TeG[i], F(TeOff[i]), F(TeX[i]), TeT[i].ToString(CultureInfo.InvariantCulture), F(TeExpo[i])]);
            return rows;
        }

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        static VariableKind Kind(int col) => col == 1 ? VariableKind.Categorical : VariableKind.Numeric;

        static ModelBundle FitBundle(string formula, GlmFamily family, GlmLink link, DesignMatrixOptions options, bool classification)
        {
            var design = DesignMatrixBuilder.Build(TrainRows(), TrainHeaders, ModelFormula.Parse(formula), Kind, options);
            var fit = GeneralizedLinearModel.Fit(design, family, link, false, default, design.GlmExtras);
            Assert.True(fit.Converged);
            var bundle = ModelBundle.FromFormula(ModelTypes.Glzm, classification ? ModelTask.Classification : ModelTask.Regression,
                design, fit, null, new Dictionary<string, string> { ["family"] = family.ToString(), ["link"] = link.ToString() });
            return bundle with
            {
                OffsetColumn = options.OffsetColumn,
                ExposureColumn = options.ExposureColumn,
                TrialsColumn = options.TrialsColumn,
                VarianceWeightColumn = options.VarianceWeightColumn,
                FrequencyWeightColumn = options.FrequencyWeightColumn,
            };
        }

        static ModelBundle RoundTrip(ModelBundle bundle, out string json)
        {
            json = ModelStore.Serialize(bundle, "1.19.0", new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
            return ModelStore.Load(json).Model;
        }

        static RowPrediction[] Apply(ModelBundle model, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
        {
            var binding = ModelStore.Bind(model, headers);
            Assert.True(binding.Ready, binding.Error);
            var dest = new RowPrediction[rows.Count];
            ModelStore.ScoreMany(model, binding, rows, 0, rows.Count, dest, default);
            return dest;
        }

        static void AssertClose(double expected, double actual, double rel = 1e-5)
            => Assert.True(Math.Abs(expected - actual) <= rel * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

        [Fact]
        public void PoissonOffsetAndExposure_RoundTripMatchesStatsmodelsPredict()
        {
            var original = FitBundle("y ~ x + C(g)", GlmFamily.Poisson, GlmLink.Log,
                new DesignMatrixOptions { OffsetColumn = "off", ExposureColumn = "expo" }, classification: false);
            var loaded = RoundTrip(original, out string json);
            Assert.Contains("\"version\":2", json);
            Assert.Equal("off", loaded.OffsetColumn);
            Assert.Equal("expo", loaded.ExposureColumn);
            Assert.Null(loaded.TrialsColumn);

            var rows = NewRows();
            var fromLoaded = Apply(loaded, NewHeaders, rows);
            var fromOriginal = Apply(original, NewHeaders, rows);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.True(fromLoaded[i].Scorable);
                AssertClose(Pois[i], fromLoaded[i].Value);
                Assert.Equal(fromOriginal[i].Value, fromLoaded[i].Value); // 저장 후 비트 동일
                Assert.True(double.IsNaN(fromLoaded[i].ExpectedSuccesses));
            }
        }

        [Fact]
        public void BinomialTrials_PredictsProbabilityAndExpectedSuccesses()
        {
            var original = FitBundle("s ~ x + C(g)", GlmFamily.Binomial, GlmLink.Logit,
                new DesignMatrixOptions { OffsetColumn = "off", TrialsColumn = "t" }, classification: false);
            Assert.Null(original.ClassNames);
            var loaded = RoundTrip(original, out string json);
            Assert.Contains("\"version\":2", json);
            Assert.Equal("t", loaded.TrialsColumn);
            Assert.Equal(ModelTask.Regression, loaded.Task);

            var pred = Apply(loaded, NewHeaders, NewRows());
            for (int i = 0; i < pred.Length; i++)
            {
                Assert.True(pred[i].Scorable);
                AssertClose(BinTrials[i], pred[i].Value);
                AssertClose(BinTrialsExp[i], pred[i].ExpectedSuccesses);
            }
        }

        [Fact]
        public void BinomialTrials_ApplyMetricsCompareSuccessesToExpectedSuccesses()
        {
            var loaded = RoundTrip(FitBundle("s ~ x + C(g)", GlmFamily.Binomial, GlmLink.Logit,
                new DesignMatrixOptions { OffsetColumn = "off", TrialsColumn = "t" }, false), out _);
            int[] actual = [3, 2, 10, 15, 1, 2, 4, 5];
            var headers = NewHeaders.Append("s").ToArray();
            var rows = NewRows().Select((r, i) => r.Append(actual[i].ToString(CultureInfo.InvariantCulture)).ToArray()).ToList();
            var binding = ModelStore.Bind(loaded, headers);
            var metrics = ModelStore.ScoreView(loaded, binding, rows, targetColumn: 5);
            Assert.Equal(8, metrics.Scorable);
            double sse = 0;
            for (int i = 0; i < actual.Length; i++) sse += Math.Pow(actual[i] - BinTrialsExp[i], 2);
            Assert.NotNull(metrics.Regression);
            AssertClose(Math.Sqrt(sse / 8), metrics.Regression!.Rmse);
        }

        [Fact]
        public void BinaryLogisticWithOffset_ClassifiesWithOffsetIncluded()
        {
            var original = FitBundle("b ~ x", GlmFamily.Binomial, GlmLink.Logit,
                new DesignMatrixOptions { Response = ResponseKind.Binary, OffsetColumn = "off" }, classification: true);
            var loaded = RoundTrip(original, out _);
            Assert.Equal(ModelTask.Classification, loaded.Task);
            var pred = Apply(loaded, NewHeaders, NewRows());
            for (int i = 0; i < pred.Length; i++)
            {
                AssertClose(BinOff[i], pred[i].Value);
                AssertClose(BinOff[i], pred[i].Probability![1]);
                Assert.Equal(BinOff[i] >= 0.5 ? 1 : 0, pred[i].ClassIndex);
            }
        }

        [Fact]
        public void ModelWithoutPredictionColumns_StillWritesVersion1_AndFilesStayReadable()
        {
            // 가중치 열만 쓴 모형은 예측에 영향이 없으므로 이전 빌드와 호환되는 버전 1로 쓰고, 열 이름은 기록용으로 남긴다.
            var bundle = FitBundle("y ~ x + C(g)", GlmFamily.Poisson, GlmLink.Log,
                new DesignMatrixOptions { FrequencyWeightColumn = "t" }, classification: false);
            var loaded = RoundTrip(bundle, out string json);
            Assert.Contains("\"version\":1", json);
            Assert.Equal("t", loaded.FrequencyWeightColumn);
            Assert.False(loaded.UsesPredictionColumns);
            var binding = ModelStore.Bind(loaded, ["x", "g"]); // t 열이 없어도 적용 가능
            Assert.True(binding.Ready, binding.Error);

            // 옛 파일(오프셋 필드 없음)을 그대로 읽는다.
            var old = ModelStore.Load(json);
            Assert.Equal("Glzm", old.Model.ModelType);
            Assert.Null(old.Model.OffsetColumn);
        }

        [Fact]
        public void FileValidation_RejectsUnknownVersionAndInconsistentColumns()
        {
            var bundle = FitBundle("y ~ x + C(g)", GlmFamily.Poisson, GlmLink.Log,
                new DesignMatrixOptions { OffsetColumn = "off" }, false);
            string json = ModelStore.Serialize(bundle, "1.19.0", DateTime.UtcNow);
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(json.Replace("\"version\":2", "\"version\":3")));
            var v1 = Assert.Throws<ModelStoreException>(() => ModelStore.Load(json.Replace("\"version\":2", "\"version\":1")));
            Assert.Contains("version 1", v1.Message);
            // 선형 모형 등 GLzM이 아닌 파일에 오프셋 필드가 있으면 불러오지 않는다.
            string linear = ModelStore.Serialize(FitLinear(), "1.19.0", DateTime.UtcNow);
            string tampered = linear.Replace("\"version\":1", "\"version\":2").Replace("\"engine\":", "\"offsetColumn\":\"off\",\"engine\":");
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(tampered));
        }

        static ModelBundle FitLinear()
        {
            var design = DesignMatrixBuilder.Build(TrainRows(), TrainHeaders, ModelFormula.Parse("y ~ x"), Kind);
            return ModelBundle.FromFormula(ModelTypes.LinearModel, ModelTask.Regression, design,
                LinearModel.Fit(design), null);
        }

        [Theory]
        [InlineData("off")]
        [InlineData("expo")]
        [InlineData("t")]
        public void MissingPredictionColumn_IsRefusedWithColumnName(string dropped)
        {
            var model = RoundTrip(FitBundle("s ~ x + C(g)", GlmFamily.Binomial, GlmLink.Logit,
                new DesignMatrixOptions { OffsetColumn = "off", ExposureColumn = "expo", TrialsColumn = "t" }, false), out _);
            var headers = NewHeaders.Where(h => h != dropped).ToArray();
            var binding = ModelStore.Bind(model, headers);
            Assert.False(binding.Ready);
            Assert.Contains(dropped, binding.MissingPredictionColumns);
            Assert.Contains(dropped, binding.MissingColumns);
            Assert.Contains(dropped, binding.Error);
            var ex = Assert.Throws<ModelStoreException>(() => ModelStore.ScoreMany(model, binding, NewRows(), 0, 1, new RowPrediction[1], default));
            Assert.Contains(dropped, ex.Message);
        }

        [Fact]
        public void InvalidRowValues_AreMarkedNotScorableWithReason_OthersStayScored()
        {
            var model = RoundTrip(FitBundle("s ~ x + C(g)", GlmFamily.Binomial, GlmLink.Logit,
                new DesignMatrixOptions { OffsetColumn = "off", ExposureColumn = "expo", TrialsColumn = "t" }, false), out _);
            var rows = NewRows();
            // NewHeaders = g, off, x, t, expo
            rows[0][4] = "0";       // 노출 ≤ 0
            rows[1][1] = "n/a";     // 오프셋 비수치
            rows[2][3] = "2.5";     // 시행 수가 정수가 아님
            rows[3][3] = "";        // 시행 수 결측
            rows[4][4] = "-1";      // 노출 < 0
            var pred = Apply(model, NewHeaders, rows);
            Assert.Contains("exposure", pred[0].Reason);
            Assert.Contains("offset", pred[1].Reason);
            Assert.Contains("trials", pred[2].Reason);
            Assert.Contains("trials", pred[3].Reason);
            Assert.Contains("exposure", pred[4].Reason);
            for (int i = 0; i < 5; i++) { Assert.False(pred[i].Scorable); Assert.True(double.IsNaN(pred[i].Value)); }

            // 나머지 정상 행은 그대로 채점한다.
            Assert.True(pred[5].Scorable);
            Assert.True(pred[6].Scorable);
            Assert.True(pred[7].Scorable);
        }

        [Fact]
        public void ExposureEnters_AsLogOfValue()
        {
            // 노출 열을 두 배로 하면 포아송 로그 연결 평균이 정확히 두 배가 된다.
            var model = RoundTrip(FitBundle("y ~ x + C(g)", GlmFamily.Poisson, GlmLink.Log,
                new DesignMatrixOptions { ExposureColumn = "expo" }, false), out _);
            var rows = NewRows();
            var doubled = rows.Select(r => { var c = (string[])r.Clone(); c[4] = F(double.Parse(c[4], CultureInfo.InvariantCulture) * 2); return c; }).ToList();
            var a = Apply(model, NewHeaders, rows);
            var b = Apply(model, NewHeaders, doubled);
            for (int i = 0; i < rows.Count; i++) AssertClose(2 * a[i].Value, b[i].Value, 1e-12);
        }
    }
}
