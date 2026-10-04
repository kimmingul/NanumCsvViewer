using System.Globalization;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // LDA ONNX 내보내기: 그래프 구조 + onnxruntime 1.20.1 CPU 실행 결과(기록값)와 1e-5 일치.
    public class OnnxLdaTests
    {
        // 기록: sess.run(None, {"features": X.astype("float32")}) → (label, probabilities[:,0])
        static readonly long[] Label = { 0, 1, 2, 0, 1, 2, 0, 1, 2, 1, 1, 2, 0, 1, 2, 0, 2, 2 };
        static readonly double[] Prob0 =
        {
            0.9171386957168579, 0.20751774311065674, 0.0013802334433421493, 0.6253917813301086, 0.2437572479248047,
            4.970939244230976e-06, 0.99403977394104, 0.031664758920669556, 8.783254452282563e-05, 0.20995979011058807,
            0.43421217799186707, 3.570825057863658e-08, 0.620520830154419, 0.20597437024116516, 2.1992680558469146e-05,
            0.9924300312995911, 0.0027192914858460426, 2.2684710074827308e-06,
        };

        [Fact]
        public void Lda_graph_matches_recorded_onnxruntime_outputs()
        {
            var headers = new[] { "id", "x1", "x2", "cls" };
            var rows = new List<string[]>();
            var rng = new Random(11);
            for (int i = 0; i < 18; i++)
            {
                int c = i % 3;
                rows.Add(new[]
                {
                    i.ToString(), (c * 0.6 + rng.NextDouble()).ToString("F3", CultureInfo.InvariantCulture),
                    (rng.NextDouble() * 2 + c * 0.3).ToString("F3", CultureInfo.InvariantCulture), "ABC"[c].ToString(),
                });
            }
            Func<int, VariableKind> kind = _ => VariableKind.Numeric;
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 1, 2 }, kind, 3, TargetKind.Categorical);
            var lda = LinearDiscriminant.Fit(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, fm.FeatureNames, fm.ClassNames);
            var bundle = ModelBundle.FromFeatures(ModelTypes.Lda, ModelTask.Classification, fm, headers, kind, "cls", lda, null, fm.RowCount, "lda");

            var info = OnnxExport.Inspect(OnnxExport.Export(bundle).Model);
            Assert.Contains("MatMul", info.NodeOps);
            Assert.Contains("Softmax", info.NodeOps);
            Assert.Contains("ArgMax", info.NodeOps);
            Assert.Contains("probabilities", info.Outputs);

            var pred = ModelStore.PredictEncoded(bundle, fm.X);
            for (int i = 0; i < Label.Length; i++)
            {
                Assert.Equal(Label[i], pred.ClassIndex![i]);
                Assert.InRange(Math.Abs(pred.Probability![i, 0] - Prob0[i]), 0, 1e-5);
            }
        }
    }
}
