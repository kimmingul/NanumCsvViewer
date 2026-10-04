using System.Globalization;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // SVM·나이브 베이즈 ONNX 내보내기: 그래프 구조 + onnxruntime 1.20.1 CPU 실행 결과(기록값)와 일치.
    // 기록: sess.run(None, {"features": X.astype("float32")}) — 라벨은 정확히, 실수는 1e-4 이내.
    // 입력 X는 같은 시드의 합성 데이터이고 앱 예측(ModelStore.PredictEncoded)과 기록값을 비교한다.
    public class OnnxSvmNbTests
    {
        static readonly string[] Levels = { "red", "green", "blue" };

        // 결정적 합성 데이터. 수치 열 x1..xP(클래스별 이동 + 겹침), 선택적 범주 열 col, 마지막 열이 목표.
        static (FeatureMatrix Fm, string[] Headers, Func<int, VariableKind> Kind) Data(int n, int classes, int seed, int numeric, bool categorical)
        {
            var headers = new List<string> { "id" };
            for (int j = 1; j <= numeric; j++) headers.Add("x" + j);
            if (categorical) headers.Add("col");
            headers.Add("cls");
            var rows = new List<string[]>();
            var rng = new Random(seed);
            for (int i = 0; i < n; i++)
            {
                int c = i % classes;
                var row = new List<string> { i.ToString(CultureInfo.InvariantCulture) };
                for (int j = 0; j < numeric; j++)
                    row.Add((c * (0.9 + 0.3 * j) + rng.NextDouble() * 2.2 + (j == 1 ? c * 0.2 : 0)).ToString("F3", CultureInfo.InvariantCulture));
                if (categorical)
                    row.Add(Levels[(c + (rng.Next(4) == 0 ? rng.Next(3) : 0)) % 3]);
                row.Add("ABCD"[c].ToString());
                rows.Add(row.ToArray());
            }
            int catCol = numeric + 1;
            Func<int, VariableKind> kind = col => categorical && col == catCol ? VariableKind.Categorical : VariableKind.Numeric;
            var featureCols = Enumerable.Range(1, numeric + (categorical ? 1 : 0)).ToArray();
            var fm = FeatureMatrixBuilder.Build(rows, headers, featureCols, kind, headers.Count - 1, TargetKind.Categorical);
            return (fm, headers.ToArray(), kind);
        }

        static ModelBundle Bundle(string type, FeatureMatrix fm, string[] headers, Func<int, VariableKind> kind, object engine, FeatureScaler? scaler)
            => ModelBundle.FromFeatures(type, ModelTask.Classification, fm, headers, kind, "cls", engine, scaler, fm.RowCount, type);

        static (ModelBundle Bundle, double[,] X) RbfMulticlass()
        {
            var (fm, h, kind) = Data(36, 3, 21, 3, false);
            var scaler = FeatureScaler.Fit(fm.X, ScalingMethod.ZScore);
            var svm = SupportVectorMachine.Fit(scaler.Transform(fm.X), fm.ClassLabels!, 3, new SvmOptions { Kernel = SvmKernel.Rbf, C = 2 });
            return (Bundle(ModelTypes.Svm, fm, h, kind, svm, scaler), fm.X);
        }

        static (ModelBundle Bundle, double[,] X) LinearBinary()
        {
            var (fm, h, kind) = Data(30, 2, 5, 2, false);
            var svm = SupportVectorMachine.Fit(fm.X, fm.ClassLabels!, 2, new SvmOptions { Kernel = SvmKernel.Linear, C = 1 });
            return (Bundle(ModelTypes.Svm, fm, h, kind, svm, null), fm.X);
        }

        // MaxTrainingRows(20) < n(45)이면 선형 SVM은 DCD(one-vs-rest)로 푼다. classCount 4는 데이터에 없는 클래스(널 가중치)를 만든다.
        static (ModelBundle Bundle, double[,] X) DcdLinear(int classCount)
        {
            var (fm, h, kind) = Data(45, 3, 33, 3, false);
            var scaler = FeatureScaler.Fit(fm.X, ScalingMethod.ZScore);
            var svm = SupportVectorMachine.Fit(scaler.Transform(fm.X), fm.ClassLabels!, classCount, new SvmOptions { Kernel = SvmKernel.Linear, MaxTrainingRows = 20 });
            return (Bundle(ModelTypes.Svm, fm, h, kind, svm, scaler), fm.X);
        }

        // classCount 4: 데이터에 없는 클래스 → 빈 클래스(로그 확률 −∞).
        static (ModelBundle Bundle, double[,] X) BayesNumeric(int classCount)
        {
            var (fm, h, kind) = Data(36, 3, 44, 3, false);
            var scaler = FeatureScaler.Fit(fm.X, ScalingMethod.ZScore);
            var groups = FeatureGroups.FromMatrix(fm.SourceColumns, fm.FeatureNames);
            var nb = NaiveBayesModel.Fit(scaler.Transform(fm.X), fm.ClassLabels!, classCount, groups);
            return (Bundle(ModelTypes.NaiveBayes, fm, h, kind, nb, scaler), fm.X);
        }

        static (ModelBundle Bundle, double[,] X) BayesCategorical()
        {
            var (fm, h, kind) = Data(42, 3, 55, 2, true);
            var groups = FeatureGroups.FromMatrix(fm.SourceColumns, fm.FeatureNames);
            var nb = NaiveBayesModel.Fit(fm.X, fm.ClassLabels!, 3, groups);
            return (Bundle(ModelTypes.NaiveBayes, fm, h, kind, nb, null), fm.X);
        }

        // 앱 쪽 득표를 Pairs·X·Labels에서 독립적으로 다시 계산한다(내보내기 코드와 무관한 double 산술).
        static int[,] AppVotes(ModelBundle bundle, double[,] raw)
        {
            var svm = (SvmModel)bundle.Engine;
            var x = bundle.Scaler is null ? raw : bundle.Scaler.Transform(raw);
            int n = x.GetLength(0), p = x.GetLength(1);
            var votes = new int[n, svm.ClassCount];
            foreach (var pair in svm.Pairs)
            {
                int pos = svm.Labels[pair.Rows[0]], neg = pos;
                for (int s = 0; s < pair.Rows.Length; s++)
                    if (pair.Y[s] > 0) pos = svm.Labels[pair.Rows[s]]; else neg = svm.Labels[pair.Rows[s]];
                for (int i = 0; i < n; i++)
                {
                    double sum = 0;
                    for (int s = 0; s < pair.Rows.Length; s++)
                    {
                        if (pair.Alpha[s] == 0) continue;
                        int r = pair.Rows[s];
                        double dot = 0, qn = 0, sn = 0;
                        for (int j = 0; j < p; j++) { dot += x[i, j] * svm.X[r, j]; qn += x[i, j] * x[i, j]; sn += svm.X[r, j] * svm.X[r, j]; }
                        double k = svm.Kernel == SvmKernel.Rbf ? Math.Exp(-svm.Gamma * Math.Max(qn + sn - 2 * dot, 0)) : dot;
                        sum += pair.Alpha[s] * pair.Y[s] * k;
                    }
                    if (sum - pair.Rho > 0) votes[i, pos]++; else votes[i, neg]++;
                }
            }
            return votes;
        }

        static double[,] AppDcdScores(ModelBundle bundle, double[,] raw)
        {
            var svm = (SvmModel)bundle.Engine;
            var x = bundle.Scaler!.Transform(raw);
            int n = x.GetLength(0);
            var scores = new double[n, svm.ClassCount];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < svm.ClassCount; c++)
                {
                    var w = svm.LinearWeights![c];
                    if (w is null) { scores[i, c] = -1e30; continue; }
                    double s = svm.LinearBias![c];
                    for (int j = 0; j < w.Length; j++) s += w[j] * x[i, j];
                    scores[i, c] = s;
                }
            return scores;
        }

        static void AssertVotes(ModelBundle bundle, double[,] x, long[] label, int[] votes)
        {
            var pred = ModelStore.PredictEncoded(bundle, x);
            var app = AppVotes(bundle, x);
            int k = app.GetLength(1);
            Assert.Equal(label.Length, pred.ClassIndex!.Length);
            for (int i = 0; i < label.Length; i++)
            {
                Assert.Equal(label[i], pred.ClassIndex[i]);
                for (int c = 0; c < k; c++) Assert.Equal(votes[i * k + c], app[i, c]);
            }
        }

        static void AssertProbabilities(ModelBundle bundle, double[,] x, long[] label, double[] recorded, int recordedWidth)
        {
            var pred = ModelStore.PredictEncoded(bundle, x);
            int k = pred.Probability!.GetLength(1);
            for (int i = 0; i < label.Length; i++)
            {
                Assert.Equal(label[i], pred.ClassIndex![i]);
                for (int c = 0; c < k; c++)
                    Assert.InRange(Math.Abs(pred.Probability[i, c] - recorded[i * recordedWidth + c]), 0, 1e-4);
            }
        }

        [Fact]
        public void Rbf_multiclass_svm_matches_recorded_onnxruntime_votes()
        {
            var (bundle, x) = RbfMulticlass();
            var package = OnnxExport.Export(bundle);
            var info = OnnxExport.Inspect(package.Model);
            Assert.Contains("Exp", info.NodeOps);
            Assert.Contains("Greater", info.NodeOps);
            Assert.Contains("ArgMax", info.NodeOps);
            Assert.Equal(new[] { "label", "votes" }, info.Outputs.OrderBy(o => o, StringComparer.Ordinal).ToArray());
            Assert.Contains("votes", package.SidecarJson, StringComparison.Ordinal);
            AssertVotes(bundle, x, RbfLabel, RbfVotes);
        }

        [Fact]
        public void Linear_binary_svm_matches_recorded_onnxruntime_votes()
        {
            var (bundle, x) = LinearBinary();
            var info = OnnxExport.Inspect(OnnxExport.Export(bundle).Model);
            Assert.DoesNotContain("Exp", info.NodeOps);
            Assert.Contains("Greater", info.NodeOps);
            AssertVotes(bundle, x, LinBinLabel, LinBinVotes);
        }

        [Fact]
        public void Dcd_linear_svm_matches_recorded_onnxruntime_scores()
        {
            foreach (int classCount in new[] { 3, 4 })
            {
                var (bundle, x) = DcdLinear(classCount);
                var svm = Assert.IsType<SvmModel>(bundle.Engine);
                Assert.Equal("DCD", svm.Solver);
                var info = OnnxExport.Inspect(OnnxExport.Export(bundle).Model);
                Assert.Contains("MatMul", info.NodeOps);
                Assert.DoesNotContain("Greater", info.NodeOps);
                Assert.Contains("scores", info.Outputs);
                if (classCount == 4) Assert.Null(svm.LinearWeights![3]); // 널 클래스는 선택되지 않는다
                var scores = AppDcdScores(bundle, x);
                var pred = ModelStore.PredictEncoded(bundle, x);
                for (int i = 0; i < DcdLabel.Length; i++)
                {
                    Assert.Equal(DcdLabel[i], pred.ClassIndex![i]);
                    for (int c = 0; c < classCount; c++)
                        Assert.InRange(Math.Abs(scores[i, c] - DcdOut[i * 4 + c]) / Math.Max(1, Math.Abs(DcdOut[i * 4 + c])), 0, 1e-4);
                }
            }
        }

        [Fact]
        public void Gaussian_naive_bayes_matches_recorded_onnxruntime_probabilities()
        {
            var (bundle, x) = BayesNumeric(4);
            var info = OnnxExport.Inspect(OnnxExport.Export(bundle).Model);
            Assert.Contains("Softmax", info.NodeOps);
            Assert.Contains("ReduceSum", info.NodeOps);
            Assert.Contains("probabilities", info.Outputs);
            AssertProbabilities(bundle, x, NbNumLabel, NbNumOut, 4);

            // 데이터에 없는 클래스가 없으면 같은 확률(앞 3열)
            var (bundle3, x3) = BayesNumeric(3);
            AssertProbabilities(bundle3, x3, NbNumLabel, NbNumOut, 4);
        }

        [Fact]
        public void Categorical_naive_bayes_matches_recorded_onnxruntime_probabilities()
        {
            var (bundle, x) = BayesCategorical();
            var info = OnnxExport.Inspect(OnnxExport.Export(bundle).Model);
            Assert.Contains("MatMul", info.NodeOps);
            Assert.Contains("Gather", info.NodeOps);
            Assert.Contains("Softmax", info.NodeOps);
            AssertProbabilities(bundle, x, NbCatLabel, NbCatOut, 3);
        }

        [Fact]
        public void Saved_and_reloaded_models_export_identically()
        {
            var created = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
            foreach (var (bundle, _) in new[] { RbfMulticlass(), LinearBinary(), DcdLinear(3), BayesNumeric(3), BayesCategorical() })
            {
                var loaded = ModelStore.Load(ModelStore.Serialize(bundle, "1.20.0", created)).Model;
                Assert.Equal(OnnxExport.Export(bundle).Model, OnnxExport.Export(loaded).Model);
            }
        }

        [Fact]
        public void Naive_bayes_with_scaled_categorical_columns_is_refused()
        {
            var (fm, h, kind) = Data(42, 3, 55, 2, true);
            var groups = FeatureGroups.FromMatrix(fm.SourceColumns, fm.FeatureNames);
            var scaler = FeatureScaler.Fit(fm.X, ScalingMethod.ZScore);
            var nb = NaiveBayesModel.Fit(fm.X, fm.ClassLabels!, 3, groups);
            Assert.False(OnnxExport.TryExport(Bundle(ModelTypes.NaiveBayes, fm, h, kind, nb, scaler), out var package, out var reason));
            Assert.Null(package);
            Assert.Contains("scaled one-hot categorical", reason, StringComparison.Ordinal);
            Assert.Contains("scaling set to None", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Naive_bayes_with_zero_variance_is_refused()
        {
            var (fm, h, kind) = Data(36, 3, 44, 3, false);
            var x = (double[,])fm.X.Clone();
            for (int i = 0; i < x.GetLength(0); i++) x[i, 0] = 1.5; // 모든 클래스에서 분산 0
            var groups = FeatureGroups.FromMatrix(fm.SourceColumns, fm.FeatureNames);
            var nb = NaiveBayesModel.Fit(x, fm.ClassLabels!, 3, groups, varSmoothing: 0);
            Assert.False(OnnxExport.TryExport(Bundle(ModelTypes.NaiveBayes, fm, h, kind, nb, null), out _, out var reason));
            Assert.Contains("variance", reason, StringComparison.Ordinal);
        }

        static readonly long[] RbfLabel =
        {
            0, 1, 2, 0, 1, 1, 0, 1, 2, 0, 1, 2,
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
        };

        static readonly int[] RbfVotes =
        {
            2, 1, 0, 0, 2, 1, 0, 1, 2, 2, 1, 0,
            1, 2, 0, 0, 2, 1, 2, 1, 0, 1, 2, 0,
            0, 1, 2, 2, 1, 0, 0, 2, 1, 0, 1, 2,
            2, 1, 0, 0, 2, 1, 0, 1, 2, 2, 1, 0,
            0, 2, 1, 0, 1, 2, 2, 1, 0, 1, 2, 0,
            0, 1, 2, 2, 1, 0, 1, 2, 0, 0, 1, 2,
            2, 1, 0, 0, 2, 1, 0, 1, 2, 2, 1, 0,
            1, 2, 0, 0, 1, 2, 2, 1, 0, 1, 2, 0,
            0, 1, 2, 2, 1, 0, 1, 2, 0, 0, 1, 2,
        };

        static readonly long[] LinBinLabel =
        {
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
            1, 1, 0, 1, 0, 1,
        };

        static readonly int[] LinBinVotes =
        {
            1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1,
            1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1,
            1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1,
            1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1,
            0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1,
        };

        static readonly long[] DcdLabel =
        {
            0, 0, 2, 0, 0, 2, 0, 2, 2, 0, 0, 2,
            0, 2, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
            0, 2, 2, 0, 2, 2, 0, 1, 2, 0, 1, 2,
            0, 0, 2, 0, 2, 2, 0, 2, 2,
        };

        static readonly double[] DcdOut =
        {
            0.286727667, -1, -2.84736085, -1e30, -0.623641551, -1.0000006, -1.84016848, -1e30, -4.43773079, -1.00007069, 1.41586912, -1e30,
            1.63994861, -1.00000501, -5.0193553, -1e30, -0.768927932, -1.00002158, -2.45021701, -1e30, -4.61855984, -1.00003946, 2.1636467, -1e30,
            2.49993229, -0.999992669, -5.5290556, -1e30, -2.0560565, -1.00002897, -0.468304753, -1e30, -4.64778709, -1.00006211, 2.1055584, -1e30,
            1.05132866, -0.999971926, -4.11501884, -1e30, -0.7916857, -0.999982595, -1.58729923, -1e30, -3.28474498, -1.00003815, 0.994125247, -1e30,
            -0.129261374, -0.999976575, -2.67449474, -1e30, -2.89475608, -1.00001204, 0.451618195, -1e30, -3.36513543, -1.00003612, 1.34333313, -1e30,
            0.020996809, -0.999990523, -2.51204395, -1e30, -1.00566173, -1.00001466, -1.99742842, -1e30, -3.90447688, -1.00004303, 1.27784288, -1e30,
            1.12331247, -0.99996835, -3.30458021, -1e30, -1.26672542, -1.00000799, -1.7077229, -1e30, -3.59772635, -1.00000989, 1.00000012, -1e30,
            2.2297492, -0.999971569, -5.02848959, -1e30, -1.528404, -1.00001609, -1.11326528, -1e30, -4.73590708, -1.00001538, 3.07173967, -1e30,
            1.09447646, -1.00001562, -4.63034153, -1e30, -2.17396045, -1.00001168, -0.765791655, -1e30, -2.61514044, -1.0000062, 0.232737541, -1e30,
            1.7195456, -1.00001264, -5.38994265, -1e30, -1.92736924, -1.00001085, -0.89364779, -1e30, -2.51173425, -1.00001085, 0.155612588, -1e30,
            2.7735548, -0.999976873, -5.88112354, -1e30, -1.35154819, -1.00002754, -1.55784583, -1e30, -5.14419746, -1.00000739, 3.24581432, -1e30,
            2.32082462, -1.00001729, -5.4885354, -1e30, -1.05780852, -1.00001442, -1.77776694, -1e30, -3.78277588, -0.999989986, 1.94602668, -1e30,
            1.66654849, -1.00003374, -4.98812914, -1e30, -0.601809204, -0.999976516, -1.66830325, -1e30, -4.84166813, -1.00000083, 2.59118032, -1e30,
            0.999999762, -1.00000775, -4.62279129, -1e30, -1.43080747, -1.0000205, -0.996474981, -1e30, -3.65911889, -1.00003195, 0.919096351, -1e30,
            0.982713461, -1.00002277, -4.4105587, -1e30, -2.06049967, -1.00004804, -0.809869945, -1e30, -4.7830081, -1.0000062, 2.98305798, -1e30,
        };

        static readonly long[] NbNumLabel =
        {
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
        };

        static readonly double[] NbNumOut =
        {
            0.973106682, 0.0268932711, 8.44770476e-10, 0, 0.0567080043, 0.943291545, 4.55940182e-07, 0, 3.12824243e-14, 0.00749971205, 0.992500246, 0,
            0.967442095, 0.0325579792, 9.58753077e-09, 0, 3.35925733e-06, 0.971096337, 0.0289002769, 0, 2.85859756e-11, 0.0531919748, 0.946807981, 0,
            0.976502061, 0.023497941, 8.78266349e-11, 0, 0.00128792529, 0.997548282, 0.00116383319, 0, 8.47927709e-18, 0.000194569788, 0.99980551, 0,
            0.859996796, 0.140003294, 6.16275209e-09, 0, 8.06935568e-05, 0.995144784, 0.00477451552, 0, 3.03653318e-11, 0.0332144871, 0.966785491, 0,
            0.994740844, 0.00525914552, 5.31012664e-11, 0, 3.99729259e-07, 0.944325328, 0.0556742139, 0, 3.53161589e-08, 0.339936852, 0.660063148, 0,
            0.999930978, 6.90253364e-05, 6.17238931e-17, 0, 0.0360123105, 0.963933825, 5.39443536e-05, 0, 6.2651982e-15, 0.00351886894, 0.996481061, 0,
            0.999999642, 3.14882641e-07, 1.80154808e-21, 0, 0.00438939547, 0.993866146, 0.00174446648, 0, 2.34270412e-18, 0.000152116118, 0.999847889, 0,
            0.99240464, 0.00759537425, 7.62962013e-13, 0, 1.70618951e-05, 0.986602306, 0.0133805657, 0, 3.71907053e-15, 0.0026924042, 0.997307658, 0,
            0.99902761, 0.000972343318, 1.26998252e-13, 0, 0.0115895374, 0.988358617, 5.18130328e-05, 0, 7.33020686e-14, 0.00376487756, 0.996235192, 0,
            0.992986977, 0.00701307086, 6.59154953e-11, 0, 6.34191053e-07, 0.944134951, 0.055864308, 0, 1.43247764e-16, 0.00197348394, 0.998026431, 0,
            0.999926925, 7.30158354e-05, 1.72552544e-15, 0, 1.0832644e-05, 0.920780957, 0.079208158, 0, 4.09313294e-09, 0.120747283, 0.879252672, 0,
            0.997729838, 0.00227021589, 1.50521904e-11, 0, 0.0295636337, 0.970429957, 6.50532593e-06, 0, 5.15519837e-14, 0.00842523761, 0.991574705, 0,
        };

        static readonly long[] NbCatLabel =
        {
            1, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
            0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2,
            0, 2, 2, 0, 1, 2,
        };

        static readonly double[] NbCatOut =
        {
            0.430624723, 0.567775607, 0.00159969472, 5.15921311e-05, 0.997055054, 0.002893419, 1.12148263e-15, 0.000376672193, 0.999623299, 0.9971506, 0.00284909853, 4.15032019e-07,
            0.00054173189, 0.998569727, 0.000888465147, 3.75964468e-08, 0.0868816376, 0.913118303, 0.999706209, 0.000293822377, 4.07887768e-09, 0.10978321, 0.890164852, 5.20263457e-05,
            4.70007571e-16, 0.000253367412, 0.999746621, 0.997887909, 0.00211194926, 1.48134902e-07, 0.00117272767, 0.997941911, 0.00088535127, 4.33659673e-13, 0.00268938416, 0.997310638,
            0.999745786, 0.000254166545, 8.71857164e-09, 1.25511667e-06, 0.958350301, 0.0416484438, 2.26454376e-11, 0.00997325033, 0.990026832, 0.997153282, 0.0028464566, 2.96993505e-07,
            1.89261918e-05, 0.991277814, 0.00870327558, 5.49454285e-07, 0.267242342, 0.732757151, 0.933636904, 0.0663627386, 3.44822553e-07, 0.000575105718, 0.99780792, 0.00161701452,
            3.30807692e-17, 0.00011165638, 0.999888301, 0.999900699, 9.92760033e-05, 6.55310306e-10, 0.00720895547, 0.992388248, 0.000402871228, 2.99328391e-20, 0.000180184448, 0.999819815,
            0.998706222, 0.00129366969, 1.17418317e-07, 5.66779734e-09, 0.625387311, 0.374612719, 4.63656423e-21, 4.64134246e-06, 0.999995351, 0.999828339, 0.000171637148, 4.49722704e-09,
            1.20438181e-05, 0.989211082, 0.0107769119, 5.21490682e-18, 5.04678283e-05, 0.999949574, 0.999806821, 0.000193180196, 5.31995825e-09, 0.02672638, 0.973177969, 9.56938602e-05,
            2.18845798e-07, 0.21581015, 0.784189582, 0.999829531, 0.000170450498, 4.27751168e-09, 0.0108784325, 0.98880136, 0.00032027415, 9.06089739e-17, 0.000139894604, 0.999860048,
            0.997994661, 0.00200524624, 1.15698683e-07, 1.85358147e-06, 0.489644855, 0.510353267, 1.19753781e-18, 3.25832916e-05, 0.999967456, 0.999697924, 0.0003020695, 1.23269954e-08,
            0.00391527265, 0.995530903, 0.000553860446, 6.43795455e-14, 0.00127228035, 0.998727739,
        };
    }
}
