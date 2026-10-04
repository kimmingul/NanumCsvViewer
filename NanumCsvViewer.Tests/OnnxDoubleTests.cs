using System.Globalization;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // float64 ONNX 내보내기. 밀집 그래프(SVM·NB·LDA·다항 로지스틱·GLM·선형 점수)는 입력·상수·출력이 double이고
    // onnxruntime 1.20.1 CPU 실행(float64 입력)이 앱의 라벨과 정확히 같다. 결정 경계에 1e-9로 붙여 만든 행에서는
    // 같은 모형의 float32 그래프가 득표·라벨을 뒤집는다(기록값). 트리 계열은 float32뿐이라 거부한다.
    public class OnnxDoubleTests
    {
        internal sealed record DenseCase(string Name, ModelBundle Bundle, double[,] X);

        internal static DenseCase[] DenseCases()
        {
            var rbf = OnnxSvmNbTests.RbfMulticlass();
            var lin = OnnxSvmNbTests.LinearBinary();
            var dcd = OnnxSvmNbTests.DcdLinear(4);
            var nb = OnnxSvmNbTests.BayesNumeric(4);
            var nbCat = OnnxSvmNbTests.BayesCategorical();
            var lda = Lda();
            var mn = Multinomial();
            var ols = Ols();
            var glm = GlmLogit();
            var scoreLin = ScoreModel(logistic: false);
            var scoreLog = ScoreModel(logistic: true);
            return new[]
            {
                new DenseCase("svm_rbf", rbf.Bundle, rbf.X),
                new DenseCase("svm_linear", lin.Bundle, lin.X),
                new DenseCase("svm_dcd", dcd.Bundle, dcd.X),
                new DenseCase("nb_numeric", nb.Bundle, nb.X),
                new DenseCase("nb_categorical", nbCat.Bundle, nbCat.X),
                new DenseCase("lda", lda.Bundle, lda.X),
                new DenseCase("multinomial", mn.Bundle, mn.X),
                new DenseCase("ols", ols.Bundle, ols.X),
                new DenseCase("glm_logit", glm.Bundle, glm.X),
                new DenseCase("score_linear", scoreLin.Bundle, scoreLin.X),
                new DenseCase("score_logit", scoreLog.Bundle, scoreLog.X),
            };
        }

        static (ModelBundle Bundle, double[,] X) Lda()
        {
            var (fm, h, kind) = OnnxSvmNbTests.Data(36, 3, 61, 3, false);
            var lda = LinearDiscriminant.Fit(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, fm.FeatureNames, fm.ClassNames);
            return (OnnxSvmNbTests.Bundle(ModelTypes.Lda, fm, h, kind, lda, null), fm.X);
        }

        static (ModelBundle Bundle, double[,] X) Multinomial()
        {
            var (fm, h, kind) = OnnxSvmNbTests.Data(36, 3, 62, 3, false);
            var scaler = FeatureScaler.Fit(fm.X, ScalingMethod.ZScore);
            var fit = MultinomialLogistic.Fit(scaler.Transform(fm.X), fm.ClassLabels!, 3, 1.0);
            return (OnnxSvmNbTests.Bundle(MultinomialLogisticModel.ModelTypeName, fm, h, kind, fit, scaler), fm.X);
        }

        // 수치 y, x, 범주 g(a/b), 이진 cls. 결정적 잡음으로 완전 분리를 피한다.
        static (string[] Headers, List<string[]> Rows) Table(int n)
        {
            var rows = new List<string[]>();
            var rng = new Random(77);
            for (int i = 0; i < n; i++)
            {
                string g = i % 2 == 0 ? "a" : "b";
                double x = i * 0.37;
                double y = 1.25 + 1.5 * x + (g == "b" ? 0.75 : 0) + (rng.NextDouble() - 0.5);
                string cls = x + (g == "b" ? 2 : 0) + (rng.NextDouble() - 0.5) * 5 >= 5.5 ? "yes" : "no";
                rows.Add(new[]
                {
                    y.ToString("G17", CultureInfo.InvariantCulture),
                    x.ToString("G17", CultureInfo.InvariantCulture),
                    g,
                    cls,
                });
            }
            return (new[] { "y", "x", "g", "cls" }, rows);
        }

        static Func<int, VariableKind> Kind => c => c is 2 or 3 ? VariableKind.Categorical : VariableKind.Numeric;

        static (ModelBundle Bundle, double[,] X) Ols()
        {
            var (headers, rows) = Table(30);
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x + C(g)"), Kind);
            return (ModelBundle.FromFormula(ModelTypes.LinearModel, ModelTask.Regression, dm, LinearModel.Fit(dm), "ols"), dm.X);
        }

        static (ModelBundle Bundle, double[,] X) GlmLogit()
        {
            var (headers, rows) = Table(30);
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("cls ~ x + C(g)"), Kind,
                new DesignMatrixOptions { Response = ResponseKind.Binary });
            var fit = GeneralizedLinearModel.Fit(dm, GlmFamily.Binomial, GlmLink.Logit);
            return (ModelBundle.FromFormula(ModelTypes.Logistic, ModelTask.Classification, dm, fit, "logit"), dm.X);
        }

        // 두 수치 특성 x1, x2에 손으로 정한 계수의 선형 점수(첫 계수가 절편).
        static (ModelBundle Bundle, double[,] X) ScoreModel(bool logistic)
        {
            var headers = new[] { "x1", "x2", "y" };
            var rows = new List<string[]>();
            var rng = new Random(91);
            for (int i = 0; i < 24; i++)
                rows.Add(new[]
                {
                    (rng.NextDouble() * 4 - 2).ToString("F3", CultureInfo.InvariantCulture),
                    (rng.NextDouble() * 4 - 2).ToString("F3", CultureInfo.InvariantCulture),
                    (i % 2).ToString(CultureInfo.InvariantCulture),
                });
            Func<int, VariableKind> kind = _ => VariableKind.Numeric;
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 0, 1 }, kind, 2, logistic ? TargetKind.Categorical : TargetKind.Numeric);
            var score = new LinearScoreModel { Coefficients = new[] { -0.35, 0.8, -1.3 }, Logistic = logistic };
            return (ModelBundle.FromFeatures(logistic ? ModelTypes.Logistic : ModelTypes.LinearModel,
                logistic ? ModelTask.Classification : ModelTask.Regression, fm, headers, kind, "y", score, null, fm.RowCount, "score"), fm.X);
        }

        [Fact]
        public void Models_that_need_offset_exposure_or_trials_columns_are_refused_in_both_precisions()
        {
            var glm = Get("glm_logit").Bundle;
            foreach (var bundle in new[] { glm with { OffsetColumn = "off" }, glm with { ExposureColumn = "exp" }, glm with { TrialsColumn = "n" } })
                foreach (var precision in new[] { OnnxPrecision.Float32, OnnxPrecision.Float64 })
                {
                    Assert.False(OnnxExport.TryExport(bundle, precision, out var package, out var reason));
                    Assert.Null(package);
                    Assert.Equal("This GLzM model uses an offset, exposure or trials column, which an ONNX feature-vector input cannot represent.", reason);
                }
            Assert.True(OnnxExport.TryExport(glm, OnnxPrecision.Float64, out _, out _));
        }

        static readonly Lazy<DenseCase[]> Cached = new(DenseCases);

        static DenseCase Get(string name) => Cached.Value.Single(c => c.Name == name);

        public static IEnumerable<object[]> CaseNames() => DenseCases().Select(c => new object[] { c.Name });

        public static IEnumerable<object[]> ClassifierNearCaseNames()
            => Recorded.ByCase.Where(kv => kv.Value.Near.Length > 0).Select(kv => new object[] { kv.Key });

        // 앱 표본 + 결정 경계에 붙인 행(기록 리터럴).
        static double[,] Rows(DenseCase c, Recorded.Rec rec)
        {
            int n = c.X.GetLength(0), p = c.X.GetLength(1);
            var x = new double[n + rec.Near.Length, p];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++) x[i, j] = c.X[i, j];
            for (int i = 0; i < rec.Near.Length; i++)
            {
                Assert.Equal(p, rec.Near[i].Length);
                for (int j = 0; j < p; j++) x[n + i, j] = rec.Near[i][j];
            }
            return x;
        }

        // 기록한 출력 행: 앞 8개 앱 표본 행과 모든 경계 행.
        static int[] OutputRows(int regular, int near) => Enumerable.Range(0, Math.Min(8, regular)).Concat(Enumerable.Range(regular, near)).ToArray();

        // 그래프의 주 출력에 해당하는 앱 쪽 값(내보내기 코드와 무관한 double 산술).
        static double[,] AppOutput(DenseCase c, double[,] x)
        {
            int n = x.GetLength(0);
            switch (c.Name)
            {
                case "svm_rbf":
                case "svm_linear":
                {
                    var votes = OnnxSvmNbTests.AppVotes(c.Bundle, x);
                    var d = new double[n, votes.GetLength(1)];
                    for (int i = 0; i < n; i++) for (int k = 0; k < d.GetLength(1); k++) d[i, k] = votes[i, k];
                    return d;
                }
                case "svm_dcd":
                    return OnnxSvmNbTests.AppDcdScores(c.Bundle, x);
                case "ols":
                case "score_linear":
                case "glm_logit":
                case "score_logit":
                {
                    var value = ModelStore.PredictEncoded(c.Bundle, x).Value!;
                    var d = new double[n, 1];
                    for (int i = 0; i < n; i++) d[i, 0] = value[i];
                    return d;
                }
                default:
                    return ModelStore.PredictEncoded(c.Bundle, x).Probability!;
            }
        }

        // 행마다 가장 가까운 결정 경계까지의 앱 쪽 거리(SVM 쌍 점수, 클래스 점수 차, 확률 로그 차, 0.5와의 차).
        static double[] Margins(DenseCase c, double[,] x)
        {
            int n = x.GetLength(0);
            var m = new double[n];
            var pair = c.Name is "svm_rbf" or "svm_linear" ? OnnxSvmNbTests.AppPairScores(c.Bundle, x).Scores : null;
            var dcd = c.Name == "svm_dcd" ? OnnxSvmNbTests.AppDcdScores(c.Bundle, x) : null;
            var prob = c.Name is "nb_numeric" or "lda" or "multinomial" ? ModelStore.PredictEncoded(c.Bundle, x).Probability : null;
            var value = c.Name is "glm_logit" or "score_logit" ? ModelStore.PredictEncoded(c.Bundle, x).Value : null;
            for (int i = 0; i < n; i++)
            {
                double best = double.PositiveInfinity;
                if (pair != null)
                    for (int q = 0; q < pair.GetLength(1); q++) best = Math.Min(best, Math.Abs(pair[i, q]));
                if (dcd != null)
                    for (int a = 0; a < 3; a++) for (int b = a + 1; b < 3; b++) best = Math.Min(best, Math.Abs(dcd[i, a] - dcd[i, b]));
                if (prob != null)
                    for (int a = 0; a < 3; a++) for (int b = a + 1; b < 3; b++) best = Math.Min(best, Math.Abs(Math.Log(prob[i, a]) - Math.Log(prob[i, b])));
                if (value != null) best = Math.Abs(value[i] - 0.5);
                m[i] = best;
            }
            return m;
        }

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void Float64_graph_reproduces_the_app_exactly_as_recorded_from_onnxruntime(string name)
        {
            var c = Get(name);
            var rec = Recorded.ByCase[name];
            int regular = c.X.GetLength(0);
            var x = Rows(c, rec);
            int n = x.GetLength(0);

            // 라벨: 기록한 float64 그래프의 라벨이 앱과 모든 행(경계 행 포함)에서 같다.
            if (rec.Label64.Length > 0)
            {
                var pred = ModelStore.PredictEncoded(c.Bundle, x);
                Assert.Equal(n, rec.Label64.Length);
                for (int i = 0; i < n; i++) Assert.True(rec.Label64[i] == pred.ClassIndex![i], $"{name} row {i}: onnxruntime float64 {rec.Label64[i]}, app {pred.ClassIndex[i]}");
            }
            else Assert.Empty(rec.Near);

            // 수치 출력(득표 정수 · 점수 · 확률 · 예측값)
            var app = AppOutput(c, x);
            var rows = OutputRows(regular, rec.Near.Length);
            int width = app.GetLength(1);
            Assert.Equal(rows.Length * width, rec.Out.Length);
            for (int r = 0; r < rows.Length; r++)
                for (int k = 0; k < width; k++)
                {
                    double expected = rec.Out[r * width + k], actual = app[rows[r], k];
                    Assert.InRange(Math.Abs(actual - expected), 0, 1e-12 * Math.Max(1, Math.Abs(expected)));
                }

            // 경계 행은 정말 경계(앱의 double 산술로 1e-9 안쪽)이고 일반 행은 아니다.
            var margin = Margins(c, x);
            for (int i = regular; i < n; i++) Assert.True(margin[i] < 2e-9, $"{name} near row {i - regular} margin {margin[i]}");
        }

        [Theory]
        [MemberData(nameof(ClassifierNearCaseNames))]
        public void Float32_graph_flips_a_label_on_boundary_rows_that_float64_keeps(string name)
        {
            var c = Get(name);
            var rec = Recorded.ByCase[name];
            int regular = c.X.GetLength(0);
            var pred = ModelStore.PredictEncoded(c.Bundle, Rows(c, rec));
            Assert.Equal(rec.Near.Length, rec.Label32Near.Length);
            int flips = 0;
            for (int i = 0; i < rec.Near.Length; i++)
            {
                Assert.Equal(rec.Label64[regular + i], pred.ClassIndex![regular + i]); // float64는 앱과 같다
                if (rec.Label32Near[i] != pred.ClassIndex[regular + i]) flips++;     // float32는 다르다
            }
            Assert.True(flips >= 1, $"{name}: expected the recorded float32 graph to flip at least one boundary row");
        }

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void Float64_export_is_double_end_to_end_and_default_stays_float32(string name)
        {
            var c = Get(name);
            var p64 = OnnxExport.Export(c.Bundle, OnnxPrecision.Float64);
            var info64 = OnnxExport.Inspect(p64.Model);
            Assert.Equal("float64", p64.Precision);
            Assert.Equal(new long[] { 11 }, info64.InputElemTypes.ToArray());
            Assert.DoesNotContain(1L, info64.OutputElemTypes);
            Assert.Contains(11L, info64.OutputElemTypes);
            Assert.DoesNotContain(1L, info64.InitializerElemTypes); // 부동소수 상수는 모두 double(색인은 int64)
            Assert.Contains(11L, info64.InitializerElemTypes);
            Assert.All(info64.InitializerElemTypes, t => Assert.Contains(t, new[] { 7L, 11L }));
            Assert.DoesNotContain("ai.onnx.ml", info64.NodeDomains); // LinearRegressor 등 float 전용 연산이 없다
            Assert.Contains("float64", p64.Summary, StringComparison.Ordinal);

            using var side64 = System.Text.Json.JsonDocument.Parse(p64.SidecarJson);
            Assert.Equal("float64", side64.RootElement.GetProperty("precision").GetString());
            Assert.Equal("float64", side64.RootElement.GetProperty("input").GetProperty("dtype").GetString());

            var p32 = OnnxExport.Export(c.Bundle);
            var info32 = OnnxExport.Inspect(p32.Model);
            Assert.Equal("float32", p32.Precision);
            Assert.Equal(new long[] { 1 }, info32.InputElemTypes.ToArray());
            Assert.DoesNotContain(11L, info32.OutputElemTypes);
            Assert.DoesNotContain(11L, info32.InitializerElemTypes);
            Assert.Equal(p32.Model, OnnxExport.Export(c.Bundle, OnnxPrecision.Float32).Model);
            using var side32 = System.Text.Json.JsonDocument.Parse(p32.SidecarJson);
            Assert.Equal("float32", side32.RootElement.GetProperty("precision").GetString());
            Assert.Equal("float32", side32.RootElement.GetProperty("input").GetProperty("dtype").GetString());
            Assert.True(OnnxExport.SupportsFloat64(c.Bundle));
        }

        [Fact]
        public void Float64_is_refused_for_tree_models_with_a_clear_reason()
        {
            var (fm, h, kind) = OnnxSvmNbTests.Data(36, 3, 63, 2, false);
            var tree = DecisionTree.FitClassification(fm.X, fm.ClassLabels!, fm.ClassNames!.Count, new DecisionTreeOptions { MaxDepth = 2, MinSamplesLeaf = 1 });
            var bundle = OnnxSvmNbTests.Bundle(ModelTypes.DecisionTree, fm, h, kind, tree, null);
            Assert.False(OnnxExport.SupportsFloat64(bundle));
            Assert.False(OnnxExport.TryExport(bundle, OnnxPrecision.Float64, out var package, out var reason));
            Assert.Null(package);
            Assert.Contains("float64", reason, StringComparison.Ordinal);
            Assert.Contains("float32", reason, StringComparison.Ordinal);
            Assert.Contains("TreeEnsemble", reason, StringComparison.Ordinal);
            // 같은 모형을 float32로는 그대로 내보낸다.
            Assert.True(OnnxExport.TryExport(bundle, out var ok, out _));
            Assert.Equal("float32", ok!.Precision);
        }

        [Fact]
        public void Float64_keeps_values_that_overflow_float32_and_float32_still_refuses_them()
        {
            var (bundle, _) = Multinomial();
            var model = Assert.IsType<MultinomialLogisticModel>(bundle.Engine);
            model.Coefficients[0, 1] = 1e200;
            Assert.False(OnnxExport.TryExport(bundle, out _, out var reason));
            Assert.Contains("float32-overflowing", reason, StringComparison.Ordinal);
            var package = OnnxExport.Export(bundle, OnnxPrecision.Float64);
            Assert.Equal("float64", package.Precision);
            model.Coefficients[0, 1] = double.PositiveInfinity;
            Assert.False(OnnxExport.TryExport(bundle, OnnxPrecision.Float64, out _, out var infinite));
            Assert.Contains("non-finite", infinite, StringComparison.Ordinal);
        }

        [Fact]
        public void Saved_and_reloaded_models_export_identical_float64_graphs()
        {
            var created = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
            foreach (var c in Cached.Value)
            {
                var loaded = ModelStore.Load(ModelStore.Serialize(c.Bundle, "1.20.0", created)).Model;
                Assert.Equal(OnnxExport.Export(c.Bundle, OnnxPrecision.Float64).Model, OnnxExport.Export(loaded, OnnxPrecision.Float64).Model);
            }
        }

        internal static class Recorded
        {
            // Near: 앱의 double 산술로 결정 경계에 1e-9 이내인 원본 특성 행. Label64/Label32Near: onnxruntime 1.20.1 CPU 라벨
            // (float64 그래프는 전체 행, float32 그래프는 경계 행만). Out: 주 출력(득표·점수·확률·예측)의 float64 그래프 값.
            internal sealed record Rec(double[][] Near, long[] Label64, long[] Label32Near, double[] Out);

            internal static readonly Dictionary<string, Rec> ByCase = new()
            {
                ["svm_rbf"] = new(
                new double[][]
                {
                    new[] { 1.4270581259075552, 2.3027375056184827, 1.4776797515358775 },
                    new[] { 1.3831207628715783, 2.189998158236034, 1.6140633652508258 },
                    new[] { 1.2842444881275297, 2.325756156429648, 1.5065411281995473 },
                    new[] { 1.7535409450447186, 2.3703259102846497, 1.3120052237804047 },
                    new[] { 1.06169213934429, 2.810897905278951, 3.1677488380055876 },
                    new[] { 2.2652882217019794, 2.874578979961574, 2.078912404768169 },
                    new[] { 2.4441193517958744, 2.589880915611051, 2.188123555481434 },
                    new[] { 1.40253845708631, 3.1094200012767685, 2.567841934015043 },
                    new[] { 1.7231461409330366, 3.9072952003739774, 3.87595436129719 },
                    new[] { 2.12396462120302, 2.894144566265866, 4.269494492396713 },
                    new[] { 1.8121085109747948, 3.773215386482887, 3.9176713041267357 },
                    new[] { 1.5421059620445594, 3.5711262730332094, 4.329158080807887 },
                },
                new long[] { 0, 1, 2, 0, 1, 1, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2 },
                new long[] { 1, 1, 0, 0, 1, 1, 1, 1, 2, 2, 1, 2 },
                new[] { 2.0, 1.0, 0.0, 0.0, 2.0, 1.0, 0.0, 1.0, 2.0, 2.0, 1.0, 0.0, 1.0, 2.0, 0.0, 0.0, 2.0, 1.0, 2.0, 1.0, 0.0, 1.0, 2.0, 0.0, 2.0, 1.0, 0.0, 2.0, 1.0, 0.0, 2.0, 1.0, 0.0, 1.0, 2.0, 0.0, 1.0, 2.0, 0.0, 1.0, 2.0, 0.0, 0.0, 2.0, 1.0, 0.0, 2.0, 1.0, 0.0, 1.0, 2.0, 0.0, 1.0, 2.0, 0.0, 1.0, 2.0, 0.0, 1.0, 2.0 }),
                ["svm_linear"] = new(
                new double[][]
                {
                    new[] { 1.1990735507905483, 1.9571675295233728 },
                    new[] { 1.7108272657990455, 1.5093430035384374 },
                    new[] { 1.1182798058800398, 2.0278683747183535 },
                    new[] { 1.0817418642560952, 2.0598419312788177 },
                    new[] { 1.5589509652927518, 1.6422466446480248 },
                    new[] { 1.0490480538178235, 2.0884515719003973 },
                    new[] { 1.8197610306479037, 1.4140174382515251 },
                    new[] { 1.7710321505460889, 1.4566590209314598 },
                    new[] { 1.2369618457937614, 1.924012306424789 },
                    new[] { 1.0818489526826889, 2.0597482197675854 },
                },
                new long[] { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 1, 0, 0, 1, 0, 0 },
                new long[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
                new[] { 1.0, 0.0, 0.0, 1.0, 1.0, 0.0, 0.0, 1.0, 1.0, 0.0, 0.0, 1.0, 1.0, 0.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 0.0, 1.0, 0.0 }),
                ["svm_dcd"] = new(
                new double[][]
                {
                    new[] { 2.5934006424676626, 1.9524157427847386, 2.32921477900818 },
                    new[] { 2.2485465806028806, 1.9679371682303026, 2.486750654092524 },
                    new[] { 1.7177915561329573, 2.6750872151162475, 2.0361217005299403 },
                    new[] { 1.6091371136903763, 2.565273150451482, 2.2021112111359837 },
                    new[] { 2.690845315994229, 2.1301291779354217, 2.4320412382641807 },
                    new[] { 2.272537189056165, 2.1378508505430074, 2.6141463273642582 },
                    new[] { 2.3326299803396684, 2.3415459502460436, 2.456772975123022 },
                    new[] { 1.7616880357712508, 2.7407838085908445, 2.4562963178819044 },
                    new[] { 2.7868019665488974, 2.305128850862384, 2.5332974923159925 },
                    new[] { 2.2956731866626066, 2.3017117444816977, 2.7370038196044044 },
                    new[] { 1.6574107035696506, 3.210426793392748, 2.6338442755509166 },
                    new[] { 1.8281489646844564, 3.002464390650392, 2.647289486669004 },
                },
                new long[] { 0, 0, 2, 0, 0, 2, 0, 2, 2, 0, 0, 2, 0, 2, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 2, 2, 0, 2, 2, 0, 1, 2, 0, 1, 2, 0, 0, 2, 0, 2, 2, 0, 2, 2, 1, 0, 1, 0, 1, 1, 1, 1, 2, 2, 1, 2 },
                new long[] { 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2 },
                new[] { 0.2867277268498156, -1.0, -2.847360737557386, -1e+30, -0.623641474896624, -1.000000567535461, -1.8401686041212004, -1e+30, -4.437730840916215, -1.0000706575122456, 1.4158690439313897, -1e+30, 1.6399487904680774, -1.000004948615424, -5.019355516936486, -1e+30, -0.7689278571059613, -1.000021586272077, -2.450217120641046, -1e+30, -4.618560073200298, -1.0000394503975878, 2.16364656402411, -1e+30, 2.499932238712894, -0.9999926577401586, -5.529055507045616, -1e+30, -2.056056411575663, -1.0000289157765916, -0.4683047423269059, -1e+30, -1.0000158823900416, -1.0000158816128748, -1.5406952843996053, -1e+30, -1.0000048503239791, -1.0000048506746475, -1.5131079720735436, -1e+30, -1.0000145683378727, -1.0000145682092267, -2.001142253035142, -1e+30, -1.0000066108609178, -1.0000066111626227, -1.90339217744324, -1e+30, -1.26827890884373, -1.0000191926450837, -1.2682789098324436, -1e+30, -1.2519049628460674, -1.0000058002779382, -1.2519049624345862, -1e+30, -1.3160953834676725, -1.0000141307284462, -1.3160953836277343, -1e+30, -1.4111096949128972, -1.0000080107036022, -1.4111096958849487, -1e+30, -1.5324454410363422, -1.0000224531163762, -1.0000224529766042, -1e+30, -1.4948317069393524, -1.000006716053772, -1.000006715769869, -1e+30, -1.8970831853204002, -1.0000103036143015, -1.000010303872305, -1e+30, -1.8068311935653325, -1.000010268556609, -1.000010268146986, -1e+30 }),
                ["nb_numeric"] = new(
                new double[][]
                {
                    new[] { 2.361690767245367, 1.5764377508740872, 1.3591044187662191 },
                    new[] { 1.9012421676297673, 1.7563782377006718, 1.6933346359014976 },
                    new[] { 1.853555108397035, 1.8840272957345006, 1.6240941127231343 },
                    new[] { 1.6707900644803886, 1.562327072276268, 2.0739480033761355 },
                    new[] { 2.4581379489237443, 2.01681924582948, 2.6007058379151857 },
                    new[] { 1.8065396003464702, 1.5107519797966817, 3.3034787457904313 },
                    new[] { 2.472667165923107, 1.8738416924949852, 2.7103582247467712 },
                    new[] { 2.436293065157486, 2.754883401519153, 1.999735719369026 },
                    new[] { 3.1116677675955, 3.215105790395988, 3.19783321324992 },
                    new[] { 2.933584522328572, 3.1881425384155007, 3.195904927385272 },
                    new[] { 2.36990423198184, 3.187853826078819, 3.2050011528686153 },
                    new[] { 2.2296176415642255, 2.524116134299664, 3.793453231756809 },
                },
                new long[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 2, 2 },
                new long[] { 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1 },
                new[] { 0.9731067166449936, 0.026893282510236875, 8.447696313104513e-10, 5.40612305517918e-309, 0.056708074324656965, 0.9432914697343536, 4.5594098930344927e-07, 5.240483571901133e-309, 3.128245597736647e-14, 0.007499714379033133, 0.9925002856209355, 5.513864599420614e-309, 0.9674419954958673, 0.03255799491658757, 9.587545231847848e-09, 5.374652529817853e-309, 3.3592603283806056e-06, 0.9710963415381972, 0.028900299201474314, 5.394954356999925e-309, 2.8585953097474597e-11, 0.053191952679094164, 0.9468080472923199, 5.260020022206393e-309, 0.9765020519558549, 0.023497947956318484, 8.782662071527912e-11, 5.424985940605964e-309, 0.001287924840797194, 0.997548240986667, 0.0011638341725358772, 5.541908664287707e-309, 0.4999999459145405, 0.4999999458194073, 1.082660521956894e-07, 2.77776444141333e-309, 0.49999950550537814, 0.4999995055586208, 9.889360008830085e-07, 2.777761995003036e-309, 0.4999994200954387, 0.4999994205347432, 1.1593698179842868e-06, 2.777761522650375e-309, 0.4999990186206662, 0.49999901816620396, 1.963213129722643e-06, 2.77775928980487e-309, 0.0008704868253510325, 0.9982590263501645, 0.0008704868244843324, 5.545857453331254e-309, 0.0010520272212467343, 0.997895945557371, 0.0010520272213822449, 5.5438403472819e-309, 0.0007695898545828129, 0.9984608202908539, 0.0007695898545634196, 5.5469785255184e-309, 0.0008931135251327701, 0.9982137729497733, 0.0008931135250939329, 5.54560604673114e-309, 1.0467957305684206e-08, 0.4999999949172648, 0.4999999946147779, 2.77776471364941e-309, 2.293711727042392e-08, 0.4999999886126944, 0.4999999884501884, 2.77776467862418e-309, 1.7649892679464308e-07, 0.49999991152846085, 0.49999991197261234, 2.77776425284795e-309, 1.0725813110574539e-07, 0.4999999463537428, 0.49999994638812617, 2.77776444404435e-309 }),
                ["nb_categorical"] = new(
                Array.Empty<double[]>(),
                new long[] { 1, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 2, 2, 0, 1, 2 },
                Array.Empty<long>(),
                new[] { 0.43062473791600336, 0.5677755678814783, 0.0015996942025183144, 5.159203670855536e-05, 0.9970549902797256, 0.0028934176835659076, 1.1214806694012072e-15, 0.00037667208438143894, 0.9996233279156176, 0.997150486068042, 0.0028490989001233157, 4.1503183479747476e-07, 0.0005417315929744831, 0.9985698039947102, 0.0008884644123153932, 3.759631581167519e-08, 0.08688165127415487, 0.9131183111295293, 0.9997061733938455, 0.00029382252728178026, 4.0788728780621284e-09, 0.10978319672281822, 0.8901647769817843, 5.202629539760149e-05 }),
                ["lda"] = new(
                new double[][]
                {
                    new[] { 1.593043817922473, 2.060447774231434, 1.7250151612013578 },
                    new[] { 1.6606159087440464, 1.691049929623492, 1.7782500072398688 },
                    new[] { 1.963933478997671, 1.8068029918661341, 1.5400083379875869 },
                    new[] { 1.6897129687503911, 1.8990392035064287, 1.7024311404352774 },
                    new[] { 1.852759854062635, 2.501214411287801, 2.479542789895262 },
                    new[] { 2.0205085409184687, 1.8091597589696757, 2.6781955547580725 },
                    new[] { 2.560763712015352, 2.0173981861630454, 2.126912977831438 },
                    new[] { 2.0491525397130754, 2.2033730562727434, 2.461700268993212 },
                    new[] { 2.216171586725861, 3.000595276743174, 3.6266800246499478 },
                    new[] { 2.1615263123037294, 3.1394916334571317, 3.5615426046247594 },
                    new[] { 2.258680066920817, 3.1536718711578287, 3.440124168430455 },
                    new[] { 2.230789031660883, 3.183342525386019, 3.444327585943742 },
                },
                new long[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 1, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2 },
                new long[] { 0, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 1 },
                new[] { 0.998321784778089, 0.0016782144385797963, 7.833314248272933e-10, 1.6014391649013915e-06, 0.6606579809673678, 0.3393404175934672, 4.6715085684702616e-09, 0.16851452560019012, 0.8314854697283013, 0.9999683518716317, 3.164812795095085e-05, 4.1746165616685307e-13, 0.00014901650492312016, 0.9935385587362957, 0.006312424758781115, 2.207932828353351e-12, 0.004902387721797258, 0.9950976122759947, 0.8341542736905393, 0.16584462808289685, 1.098226563918436e-06, 0.0008511661054446419, 0.9917599463873791, 0.007388887507176324, 0.4999883882284053, 0.499988388650804, 2.32231207906654e-05, 0.499993613856612, 0.49999361433902095, 1.2771804367004644e-05, 0.4999884250717508, 0.49998842529301457, 2.314963523472358e-05, 0.4999902715712757, 0.49999027180644434, 1.945662227998631e-05, 0.0030082710055375707, 0.9939834579873179, 0.0030082710071444884, 0.0014963353060689316, 0.9970073293873207, 0.0014963353066102677, 0.0029977740846819484, 0.9940044518278509, 0.0029977740874673085, 0.0024668689860779212, 0.9950662620269463, 0.002466868986975797, 3.248725729861884e-07, 0.4999998375612726, 0.49999983756615446, 4.653634277480878e-07, 0.499999767149305, 0.49999976748726715, 6.176452994433231e-07, 0.4999996910312421, 0.4999996913234584, 6.414855029666151e-07, 0.49999967921385813, 0.4999996793006389 }),
                ["multinomial"] = new(
                new double[][]
                {
                    new[] { 1.759215371967759, 1.1638252056576313, 2.0567035356773995 },
                    new[] { 1.135476758708246, 2.193189388126135, 1.7691517681470141 },
                    new[] { 0.820699404411018, 1.7803756303340197, 2.267297590829432 },
                    new[] { 0.9168815296930262, 2.5472404824001713, 1.6729990001064725 },
                    new[] { 2.418279217657866, 1.665653274325654, 2.8572519469831605 },
                    new[] { 2.0895436716966795, 2.64453750939155, 2.2115391533835793 },
                    new[] { 1.3434636508151887, 2.7810427163149, 2.7880155705600047 },
                    new[] { 2.1121035671571735, 2.4425587321259083, 2.3878629677797436 },
                    new[] { 2.826969332891516, 2.4378446612250992, 3.4892180551956407 },
                    new[] { 2.8342435872098433, 2.5484181390050797, 3.325996209884994 },
                    new[] { 2.9447589342212304, 2.4834706075955184, 3.268143251524307 },
                    new[] { 2.7846459366083147, 2.486822085775435, 3.477915202897042 },
                },
                new long[] { 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 1, 1, 2, 0, 1, 2, 0, 0, 0, 1, 1, 1, 1, 1, 2, 2, 1, 2 },
                new long[] { 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2 },
                new[] { 0.9847914900172492, 0.015204221645368823, 4.288337381954054e-06, 0.061770498827201134, 0.7165524472822034, 0.22167705389059553, 9.390288620345979e-05, 0.045165859221392664, 0.9547402378924039, 0.6260256355236595, 0.3693167433854552, 0.004657621090885356, 0.1551876712222004, 0.7244522054890025, 0.12036012328879714, 3.7354682279127925e-05, 0.02266522475251811, 0.9772974205652027, 0.9659215252414959, 0.034044743333835004, 3.373142466913028e-05, 0.16237774263132898, 0.7639918692447082, 0.07363038812396276, 0.4942703773081352, 0.4942703770242782, 0.011459245667586609, 0.4926763942086341, 0.4926763939457191, 0.014647211845646676, 0.49508415909606507, 0.49508415873312184, 0.009831682170813054, 0.49204991715997487, 0.4920499174087578, 0.015900165431267405, 0.12146115424168524, 0.7570776915546444, 0.12146115420367037, 0.14298932335137576, 0.7140213532591937, 0.1429893233894305, 0.12444326456167136, 0.7511134708641455, 0.12444326457418317, 0.1369424707221752, 0.7261150584906363, 0.13694247078718863, 0.015087730771565522, 0.4924561345341915, 0.4924561346942428, 0.01655288149701365, 0.4917235590679112, 0.49172355943507506, 0.017080166833884088, 0.49145991670945355, 0.49145991645666226, 0.015193740284063824, 0.4924031298116193, 0.4924031299043168 }),
                ["ols"] = new(
                Array.Empty<double[]>(),
                Array.Empty<long>(),
                Array.Empty<long>(),
                new[] { 1.3028136675841218, 2.5219664723683595, 2.4117809988606482, 3.630933803644886, 3.5207483301371747, 4.739901134921412, 4.629715661413701, 5.848868466197939 }),
                ["glm_logit"] = new(
                new double[][]
                {
                    new[] { 1.0, 4.088097720118239, 0.8499163659289479 },
                    new[] { 1.0, 4.596224743747152, 0.6538015282712877 },
                    new[] { 1.0, 6.2902027698140595, 0.0 },
                    new[] { 1.0, 4.913819691573735, 0.5312237504404038 },
                    new[] { 1.0, 6.290202769469469, 0.0 },
                    new[] { 1.0, 3.699236012324691, 1.0 },
                    new[] { 1.0, 3.699236012324691, 1.0 },
                    new[] { 1.0, 5.6424383646808565, 0.25000876747071743 },
                    new[] { 1.0, 3.6992360137030484, 1.0 },
                    new[] { 1.0, 4.994610209139064, 0.5000421395525336 },
                },
                new long[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1 },
                new long[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 },
                new[] { 1.4915092523604123e-05, 0.0027817813662860402, 5.5131287045484534e-05, 0.010206271871528005, 0.00020376201144145512, 0.036716901243213584, 0.0007527908133961869, 0.12349668240273137, 0.5000000006225496, 0.4999999999807907, 0.500000000033196, 0.5000000000788656, 0.4999999998809961, 0.5000000001693938, 0.5000000001693938, 0.500000000272327, 0.500000000778192, 0.5000000002114914 }),
                ["score_linear"] = new(
                Array.Empty<double[]>(),
                Array.Empty<long>(),
                Array.Empty<long>(),
                new[] { -1.1918, -3.2506, 1.1478000000000002, 2.327, -2.9116000000000004, -0.9433, -2.9659, -1.6567000000000003 }),
                ["score_logit"] = new(
                new double[][]
                {
                    new[] { -0.1368880290333182, -0.35346955476142466 },
                    new[] { -1.1544079798161984, -0.9796356815695763 },
                    new[] { -0.027835856851190277, -0.2863605285845697 },
                    new[] { 1.1042101942300797, 0.4102831987440586 },
                    new[] { -0.7465350583270192, -0.728636960208416 },
                    new[] { 0.4597605077512563, 0.013698771838098756 },
                    new[] { -0.9471655930764973, -0.8521019029747694 },
                    new[] { -1.0728839663602412, -0.9294670560620725 },
                    new[] { -0.3986159745156765, -0.5145329075455665 },
                    new[] { -1.0055758332908153, -0.8880466668605804 },
                },
                new long[] { 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 1 },
                new long[] { 1, 0, 1, 1, 1, 1, 1, 1, 1, 1 },
                new[] { 0.2329371617845628, 0.037305333173802024, 0.7591088485760227, 0.9110886178193712, 0.05158310362301077, 0.28023424017697784, 0.04899039052272858, 0.1602054802119629, 0.4999999994907993, 0.5000000005468727, 0.5000000004197471, 0.4999999992541969, 0.5000000004023314, 0.5000000007028692, 0.49999999985150057, 0.4999999999481253, 0.5000000000491739, 0.5000000000715256 }),
            };
        }
    }
}
