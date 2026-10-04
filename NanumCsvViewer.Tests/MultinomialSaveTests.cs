using System.Globalization;
using System.Text.Json.Nodes;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 다항 로지스틱: ModelStore 저장·로드·예측 + ONNX 내보내기(onnxruntime 1.20.1 기록값과 1e-5 일치).
    public class MultinomialSaveTests
    {
        // onnxruntime 1.20.1, CPUExecutionProvider, sess.run(None, {"features": X.astype("float32")}) on the exported graph (X = FixtureX()).
        static readonly long[] Label = { 3, 0, 1, 1, 0, 3, 0, 1, 3, 3, 1, 1, 3, 0, 0, 0, 0, 0, 0, 0, 3, 0, 1, 3 };
        static readonly double[] Prob =
        {
            0.353544652, 0.0499408208, 0, 0.596514523,
            0.861023366, 0.0988839641, 0, 0.0400926769,
            0.0588403568, 0.838019788, 0, 0.103139892,
            0.0408486165, 0.814528048, 0, 0.144623265,
            0.471986353, 0.126267835, 0, 0.401745707,
            0.0312377587, 0.283333629, 0, 0.685428619,
            0.921402812, 0.0137648601, 0, 0.0648322254,
            0.0538612567, 0.919000685, 0, 0.0271379985,
            0.0781851932, 0.190581903, 0, 0.731232882,
            0.234975889, 0.0565208085, 0, 0.708503306,
            0.0613985434, 0.617254138, 0, 0.321347266,
            0.319882393, 0.660216451, 0, 0.0199011583,
            0.058896061, 0.191092327, 0, 0.750011563,
            0.59003526, 0.00973282848, 0, 0.400231868,
            0.810885966, 0.0700340942, 0, 0.119079985,
            0.744816303, 0.15708366, 0, 0.0981001034,
            0.569812715, 0.345777899, 0, 0.0844093785,
            0.939203918, 0.0049439515, 0, 0.0558521561,
            0.509429634, 0.233985841, 0, 0.256584525,
            0.734831989, 0.2127662, 0, 0.0524018519,
            0.330937952, 0.00807936676, 0, 0.660982668,
            0.626506329, 0.0186174717, 0, 0.354876131,
            0.0373457484, 0.903862834, 0, 0.0587914623,
            0.169893771, 0.375650615, 0, 0.454455674,
        };

        // 클래스 2는 학습 행에 없던 클래스(계수 0, 확률 0). 계수는 고정값이라 적합 결과와 무관하게 결정적이다.
        static readonly double[,] Coef =
        {
            { 0.4, 1.1, -0.7, 0.3 },
            { -0.2, -0.6, 0.9, 0.5 },
            { 0, 0, 0, 0 },
            { 0.1, 0.2, 0.2, -1.2 },
        };

        static double[,] FixtureX()
        {
            var rng = new Random(5);
            var x = new double[24, 3];
            for (int i = 0; i < 24; i++)
                for (int j = 0; j < 3; j++)
                    x[i, j] = Math.Round(rng.NextDouble() * 4 - 1 + (i % 4) * 0.5 * (j + 1) * 0.3, 3);
            return x;
        }

        static ModelBundle Fixture(double[,] x, ScalingMethod scaling = ScalingMethod.ZScore)
        {
            var model = new MultinomialLogisticModel
            {
                Coefficients = (double[,])Coef.Clone(),
                ClassPresent = new[] { true, true, false, true },
                C = 0.5,
                Converged = true,
                Iterations = 17,
                RowsFit = 240,
            };
            return new ModelBundle
            {
                ModelType = ModelTypes.MultinomialLogistic,
                Task = ModelTask.Classification,
                Features = Enumerable.Range(1, 3).Select(j => new ModelFeature("x" + j, VariableKind.Numeric, null)).ToArray(),
                Scaler = scaling == ScalingMethod.None ? null : FeatureScaler.Fit(x, scaling),
                Target = "cls",
                ClassNames = new[] { "A", "B", "C", "D" },
                Engine = model,
                TrainingRows = 240,
            };
        }

        static ModelBundle RoundTrip(ModelBundle bundle)
            => ModelStore.Load(ModelStore.Serialize(bundle, "1.20.0", new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc))).Model;

        [Fact]
        public void Saved_model_predicts_bit_identically_and_keeps_absent_class_at_zero()
        {
            var x = FixtureX();
            var bundle = Fixture(x);
            var loaded = RoundTrip(bundle);
            var a = ModelStore.PredictEncoded(bundle, x);
            var b = ModelStore.PredictEncoded(loaded, x);
            Assert.Equal(a.ClassIndex, b.ClassIndex);
            Assert.Equal(a.Probability, b.Probability);
            Assert.Equal(a.Value, b.Value);
            for (int i = 0; i < 24; i++)
            {
                Assert.Equal(0.0, b.Probability![i, 2]);
                Assert.NotEqual(2, b.ClassIndex![i]);
                double sum = 0;
                for (int c = 0; c < 4; c++) sum += b.Probability[i, c];
                Assert.Equal(1.0, sum, 12);
            }
            var m = Assert.IsType<MultinomialLogisticModel>(loaded.Engine);
            Assert.Equal(new[] { true, true, false, true }, m.ClassPresent);
            Assert.Equal(0.5, m.C);
            Assert.True(m.Converged);
            Assert.Equal(17, m.Iterations);
            Assert.Equal(240, m.RowsFit);
            Assert.Equal(ModelTypes.MultinomialLogistic, loaded.ModelType);
            Assert.Equal(new[] { "A", "B", "C", "D" }, loaded.ClassNames);
            Assert.Equal(a.ClassIndex, ModelStore.PredictEncoded(RoundTrip(loaded), x).ClassIndex);
        }

        [Fact]
        public void Fitted_model_round_trips_with_the_same_predictions()
        {
            var x = FixtureX();
            var labels = Enumerable.Range(0, 24).Select(i => i % 3).ToArray();
            var fit = MultinomialLogistic.Fit(x, labels, 3, 1.0);
            var bundle = Fixture(x, ScalingMethod.None) with { Engine = fit, ClassNames = new[] { "A", "B", "C" }, TrainingRows = fit.RowsFit };
            var loaded = RoundTrip(bundle);
            var a = ModelStore.PredictEncoded(bundle, x);
            var b = ModelStore.PredictEncoded(loaded, x);
            Assert.Equal(a.ClassIndex, b.ClassIndex);
            Assert.Equal(a.Probability, b.Probability);
            Assert.Equal(fit.PredictClasses(x), b.ClassIndex);
        }

        static string Tampered(Action<JsonObject> edit)
        {
            var x = FixtureX();
            var root = JsonNode.Parse(ModelStore.Serialize(Fixture(x), "1.20.0", DateTime.UtcNow))!.AsObject();
            edit(root["engine"]!["multinomial"]!.AsObject());
            return root.ToJsonString();
        }

        static string Pack(params double[] v)
        {
            var bytes = new byte[v.Length * 8];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        [Fact]
        public void Untrusted_files_are_rejected()
        {
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["featureCount"] = 4)));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["classCount"] = 1)));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["classCount"] = 5)));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["classCount"] = int.MaxValue)));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["classCount"] = -4)));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["classPresent"] = new JsonArray(true, true)))); 
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["classPresent"] = new JsonArray(false, false, false, false))));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["c"] = 0)));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["c"] = -1)));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["coefficients"] = Pack(1, 2, 3))));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["coefficients"] = "")));
            var bad = new double[16];
            bad[5] = double.NaN;
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["coefficients"] = Pack(bad))));
            bad[5] = double.PositiveInfinity;
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m["coefficients"] = Pack(bad))));
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(Tampered(m => m.Remove("coefficients"))));
        }

        [Fact]
        public void Missing_engine_section_is_rejected()
        {
            var x = FixtureX();
            var root = JsonNode.Parse(ModelStore.Serialize(Fixture(x), "1.20.0", DateTime.UtcNow))!.AsObject();
            root["engine"]!.AsObject().Remove("multinomial");
            Assert.Throws<ModelStoreException>(() => ModelStore.Load(root.ToJsonString()));
        }

        [Fact]
        public void Onnx_graph_matches_recorded_onnxruntime_outputs()
        {
            var x = FixtureX();
            var bundle = Fixture(x);
            var package = OnnxExport.Export(bundle);
            var info = OnnxExport.Inspect(package.Model);
            Assert.Contains("MatMul", info.NodeOps);
            Assert.Contains("Add", info.NodeOps);
            Assert.Contains("Softmax", info.NodeOps);
            Assert.Contains("ArgMax", info.NodeOps);
            Assert.Contains("probabilities", info.Outputs);
            Assert.Equal(package.Model, OnnxExport.Export(RoundTrip(bundle)).Model);

            var pred = ModelStore.PredictEncoded(bundle, x);
            for (int i = 0; i < Label.Length; i++)
            {
                Assert.Equal(Label[i], pred.ClassIndex![i]);
                for (int c = 0; c < 4; c++)
                    Assert.InRange(Math.Abs(pred.Probability![i, c] - Prob[i * 4 + c]), 0, 1e-5);
            }
        }
    }
}
