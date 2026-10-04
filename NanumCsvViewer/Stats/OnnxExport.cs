using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NanumCsvViewer.Stats
{
    /// <summary>ONNX로 내보낼 수 없는 모형. 메시지는 그대로 안내한다.</summary>
    public sealed class OnnxNotExportableException : InvalidOperationException
    {
        public OnnxNotExportableException(string message) : base(message) { }
    }

    /// <summary>
    /// 내보낸 ONNX와 특성 명세. 입력은 Features 순서의 float32 벡터다(원-핫은 그래프 밖, 사이드카에 적음).
    /// IR 8, ai.onnx opset 13, ai.onnx.ml opset 3.
    /// </summary>
    public sealed class OnnxPackage
    {
        public required byte[] Model { get; init; }
        public required string SidecarJson { get; init; }
        public required string Summary { get; init; }
    }

    /// <summary>내보낸 그래프의 구조(테스트·검증용). 수치 비교는 onnxruntime 기록값으로 한다.</summary>
    public sealed class OnnxGraphInfo
    {
        public required long IrVersion { get; init; }
        public required IReadOnlyList<(string Domain, long Version)> Opsets { get; init; }
        public required IReadOnlyList<string> NodeOps { get; init; }
        public required IReadOnlyList<string> NodeDomains { get; init; }
        public required IReadOnlyList<string> Inputs { get; init; }
        public required IReadOnlyList<string> Outputs { get; init; }
    }

    /// <summary>
    /// 패키지 없이 ONNX protobuf를 직접 쓴다. 지원: 선형(항등)·로지스틱(MatMul+Sigmoid, p ≥ 0.5),
    /// 결정트리·랜덤 포레스트·그래디언트 부스팅(TreeEnsemble, post_transform).
    /// 트리는 float32라 분할 경계에 아주 가까운 값은 C# double과 다를 수 있다.
    /// 분류 라벨은 점수 텐서의 ArgMax(첫 최댓값)다. onnxruntime 1.20의 TreeEnsemble 자체 라벨은 그 argmax와 다르다.
    /// </summary>
    public static class OnnxExport
    {
        public const long IrVersion = 8;
        public const long OnnxOpset = 13;
        public const long MlOpset = 3;
        public const string InputName = "features";
        public const string MlDomain = "ai.onnx.ml";

        const int FloatType = 1;
        const int Int64Type = 7;

        public static bool TryExport(ModelBundle bundle, out OnnxPackage? package, out string? reason)
        {
            try
            {
                package = Export(bundle);
                reason = null;
                return true;
            }
            catch (OnnxNotExportableException ex)
            {
                package = null;
                reason = ex.Message;
                return false;
            }
        }

        public static OnnxPackage Export(ModelBundle bundle)
        {
            if (bundle is null) throw new ArgumentNullException(nameof(bundle));
            var built = Build(bundle);
            var sidecar = Sidecar(bundle, built);
            return new OnnxPackage
            {
                Model = built.Bytes,
                SidecarJson = sidecar,
                Summary = built.Summary,
            };
        }

        public static OnnxGraphInfo Inspect(byte[] model)
        {
            var r = new ProtoReader(model);
            long ir = 0;
            var opsets = new List<(string, long)>();
            var ops = new List<string>();
            var domains = new List<string>();
            var inputs = new List<string>();
            var outputs = new List<string>();
            while (r.Remaining)
            {
                r.ReadKey(out int field, out int wire);
                if (field == 1 && wire == 0) ir = (long)r.ReadVarint();
                else if (field == 8 && wire == 2)
                {
                    var body = new ProtoReader(r.ReadBytes());
                    string domain = "";
                    long version = 0;
                    while (body.Remaining)
                    {
                        body.ReadKey(out int f, out int w);
                        if (f == 1 && w == 2) domain = body.ReadString();
                        else if (f == 2 && w == 0) version = (long)body.ReadVarint();
                        else body.Skip(w);
                    }
                    opsets.Add((domain, version));
                }
                else if (field == 7 && wire == 2) ReadGraph(new ProtoReader(r.ReadBytes()), ops, domains, inputs, outputs);
                else r.Skip(wire);
            }
            return new OnnxGraphInfo
            {
                IrVersion = ir,
                Opsets = opsets,
                NodeOps = ops,
                NodeDomains = domains,
                Inputs = inputs,
                Outputs = outputs,
            };
        }

        static void ReadGraph(ProtoReader g, List<string> ops, List<string> domains, List<string> inputs, List<string> outputs)
        {
            while (g.Remaining)
            {
                g.ReadKey(out int field, out int wire);
                if (field == 1 && wire == 2) ReadNode(new ProtoReader(g.ReadBytes()), ops, domains);
                else if (field == 11 && wire == 2) inputs.Add(ReadName(g.ReadBytes()));
                else if (field == 12 && wire == 2) outputs.Add(ReadName(g.ReadBytes()));
                else g.Skip(wire);
            }
        }

        static void ReadNode(ProtoReader n, List<string> ops, List<string> domains)
        {
            string op = "", domain = "";
            while (n.Remaining)
            {
                n.ReadKey(out int field, out int wire);
                if (field == 4 && wire == 2) op = n.ReadString();
                else if (field == 7 && wire == 2) domain = n.ReadString();
                else n.Skip(wire);
            }
            ops.Add(op);
            domains.Add(domain);
        }

        static string ReadName(byte[] message)
        {
            var r = new ProtoReader(message);
            while (r.Remaining)
            {
                r.ReadKey(out int field, out int wire);
                if (field == 1 && wire == 2) return r.ReadString();
                r.Skip(wire);
            }
            return "";
        }

        sealed class Built
        {
            public byte[] Bytes = Array.Empty<byte>();
            public string Summary = "";
            public string[] Outputs = Array.Empty<string>();
            public string PostTransform = "";
        }

        static Built Build(ModelBundle bundle)
        {
            int p = bundle.Features.Count;
            if (p < 1) throw new OnnxNotExportableException("The model has no features to export.");
            return bundle.Engine switch
            {
                LinearModelFit ols => Linear(bundle, Coefficients(ols.Beta), regressor: true, "LinearRegressor (identity). Input includes the intercept column of 1s when the formula has one."),
                GeneralizedLinearFit glm => Glm(bundle, glm),
                DecisionTreeModel tree => tree.Regression
                    ? RegressorEnsemble(bundle, new[] { FlattenTree(tree) }, aggregate: "AVERAGE", baseValue: null, "Decision tree regressor as TreeEnsembleRegressor (aggregate AVERAGE, post_transform NONE).")
                    : ClassifierEnsemble(bundle, new[] { FlattenTree(tree) }, ClassWeightsTree(tree), "NONE", null, "Decision tree classifier as TreeEnsembleClassifier (post_transform NONE)."),
                RandomForestModel forest => forest.Regression
                    ? RegressorEnsemble(bundle, NumberTrees(forest.Trees.Select(FlattenTree)), "AVERAGE", null, "Random forest regressor as TreeEnsembleRegressor (aggregate AVERAGE).")
                    : ClassifierEnsemble(bundle, NumberTrees(forest.Trees.Select(FlattenTree)), ClassWeightsForest(forest), "NONE", null, "Random forest classifier as TreeEnsembleClassifier (vote fractions, post_transform NONE)."),
                GradientBoostingModel gb => Boosting(bundle, gb),
                AdaBoostModel ada => ExportAda(bundle, ada),
                LinearScoreModel score => ExportScore(bundle, score),
                LinearDiscriminantModel lda => ExportLda(bundle, lda),
                _ => throw new OnnxNotExportableException(Refuse(bundle)),
            };
        }

        static string Refuse(ModelBundle bundle) => bundle.ModelType switch
        {
            ModelTypes.Knn => "KNN is not exportable to ONNX. It stores the training rows and has no fixed graph.",
            ModelTypes.NaiveBayes => "Naive Bayes is not exportable to ONNX. Gaussian and categorical likelihoods are not in the supported operator set.",
            ModelTypes.Lda => "LDA is not exportable to ONNX for this model.",
            ModelTypes.Svm => "SVM is not exportable to ONNX. Kernel and linear SVM decision functions are not in the supported operator set.",
            ModelTypes.AdaBoost => "AdaBoost regression is not exportable to ONNX. The prediction is a weighted median, which TreeEnsemble cannot reproduce.",
            _ => $"{bundle.ModelType} is not exportable to ONNX.",
        };

        // LDA: 클래스 점수 = x·Coefᵀ + Intercept(앱과 같은 판별식), 확률 = Softmax, 라벨 = ArgMax(첫 최댓값).
        static Built ExportLda(ModelBundle bundle, LinearDiscriminantModel lda)
        {
            int p = lda.FeatureCount, k = lda.ClassCount;
            if (p != bundle.Features.Count) throw new OnnxNotExportableException("The LDA feature count does not match the bundle.");
            var proto = NewModel();
            string x = MaybeScale(proto, bundle, p);
            var w = new float[p * k]; // [p, K] 행 우선
            for (int j = 0; j < p; j++)
                for (int c = 0; c < k; c++) w[j * k + c] = (float)lda.Coef[c, j];
            var b = new float[k];
            for (int c = 0; c < k; c++) b[c] = (float)lda.Intercept[c];
            proto.Tensor("lda_w", new[] { p, k }, w);
            proto.Tensor("lda_b", new[] { k }, b);
            proto.Node("", "MatMul", new[] { x, "lda_w" }, new[] { "lda_xw" }, _ => { });
            proto.Node("", "Add", new[] { "lda_xw", "lda_b" }, new[] { "scores" }, _ => { });
            proto.Node("", "Softmax", new[] { "scores" }, new[] { "probabilities" }, a => a.Int("axis", 1));
            proto.Node("", "ArgMax", new[] { "scores" }, new[] { "label" }, a => { a.Int("axis", 1); a.Int("keepdims", 0); });
            proto.ValueOut("label", Int64Type, -1);
            proto.ValueOut("probabilities", FloatType, -1, k);
            return Finish(proto, "LDA as MatMul + Add (class scores), Softmax probabilities, ArgMax label (first maximum). Same discriminant as the app (sklearn svd solver).", new[] { "label", "probabilities" }, "SOFTMAX");
        }

        static Built Glm(ModelBundle bundle, GeneralizedLinearFit glm)
        {
            var beta = Coefficients(glm.Coefficients);
            if (glm.Link == GlmLink.Identity)
                return Linear(bundle, beta, regressor: true, $"GLM {glm.Family}/identity as LinearRegressor. The output is the mean, which equals the linear predictor.");
            if (glm.Family == GlmFamily.Binomial && glm.Link == GlmLink.Logit)
                return Linear(bundle, beta, regressor: false, "Logistic regression as MatMul + Sigmoid. Class 1 if probability ≥ 0.5 (the same rule as the app). LinearClassifier is not used: its binary tie-break and post_transform do not match that rule.");
            throw new OnnxNotExportableException(
                $"This {glm.Family}/{glm.Link} model is not exportable to ONNX. Only the identity link and binomial logit are exported.");
        }

        static float[] Coefficients(double[] beta)
        {
            var c = new float[beta.Length];
            for (int i = 0; i < beta.Length; i++) c[i] = double.IsNaN(beta[i]) ? 0f : (float)beta[i];
            return c;
        }

        static Built Linear(ModelBundle bundle, float[] beta, bool regressor, string summary)
        {
            int p = beta.Length;
            var proto = NewModel();
            string x = MaybeScale(proto, bundle, p);
            if (regressor)
            {
                proto.Node(MlDomain, "LinearRegressor", new[] { x }, new[] { "prediction" }, a =>
                {
                    a.Floats("coefficients", beta);
                    a.Floats("intercepts", new[] { 0f });
                    a.String("post_transform", "NONE");
                    a.Int("targets", 1);
                });
                proto.ValueOut("prediction", FloatType, -1, 1);
                return Finish(proto, summary, new[] { "prediction" }, "NONE");
            }
            var coef = new float[p];
            for (int j = 0; j < p; j++) coef[j] = beta[j];
            proto.Tensor("coef", new[] { p, 1 }, coef);
            proto.Node("", "MatMul", new[] { x, "coef" }, new[] { "eta" }, _ => { });
            proto.Node("", "Sigmoid", new[] { "eta" }, new[] { "probability" }, _ => { });
            proto.Tensor("half", new[] { 1 }, new[] { 0.5f });
            proto.Node("", "GreaterOrEqual", new[] { "probability", "half" }, new[] { "ge" }, _ => { });
            proto.Node("", "Cast", new[] { "ge" }, new[] { "label" }, a => a.Int("to", Int64Type));
            proto.ValueOut("label", Int64Type, -1, 1);
            proto.ValueOut("probability", FloatType, -1, 1);
            return Finish(proto, summary, new[] { "label", "probability" }, "SIGMOID");
        }

        static Built ExportAda(ModelBundle bundle, AdaBoostModel model)
        {
            if (model.Regression)
                throw new OnnxNotExportableException(
                    "AdaBoost regression is not exportable to ONNX. The prediction is a weighted median of the trees, which TreeEnsembleRegressor cannot reproduce.");
            if (model.EstimatorsUsed < 1 || model.Stumps.Count < model.EstimatorsUsed || model.ClassCount < 2)
                throw new OnnxNotExportableException("This AdaBoost model has no usable stumps to export.");
            var flats = new FlatTree[model.EstimatorsUsed];
            for (int i = 0; i < flats.Length; i++)
            {
                flats[i] = FlattenStump(model.Stumps[i]);
                flats[i].TreeId = i;
            }
            return ClassifierEnsemble(bundle, flats, AdaVotes(model, flats), "NONE", null,
                "AdaBoost SAMME as TreeEnsembleClassifier. Each stump leaf adds the estimator weight to the predicted class and −w/(K−1) to the others. The 'scores' output is those SAMME decision scores (not probabilities). The label is ArgMax of those scores (first maximum), matching score > 0 for binary and the smallest index on a tie.",
                scoreOutput: "scores");
        }

        static ClassVotes AdaVotes(AdaBoostModel model, FlatTree[] trees)
        {
            int k = model.ClassCount;
            var w = model.EstimatorWeights;
            double wSum = 0;
            for (int m = 0; m < model.EstimatorsUsed; m++) wSum += w[m];
            bool firstOnly = k < 2 || (wSum == 0 && model.EstimatorsUsed == 1);
            int nTrees = firstOnly ? 1 : model.EstimatorsUsed;
            var votes = new ClassVotes();
            for (int m = 0; m < nTrees; m++)
            {
                float weight = firstOnly ? 1f : (float)w[m];
                foreach (var leaf in trees[m].Leaves)
                {
                    for (int c = 0; c < k; c++)
                    {
                        float cw = firstOnly
                            ? (c == leaf.Prediction ? 1f : 0f)
                            : (c == leaf.Prediction ? weight : (float)(-weight / (k - 1)));
                        votes.Tree.Add(trees[m].TreeId);
                        votes.Node.Add(leaf.Id);
                        votes.ClassId.Add(c);
                        votes.Weight.Add(cw);
                    }
                }
            }
            return votes;
        }

        static FlatTree FlattenStump(AdaBoostStump stump)
        {
            var tree = new FlatTree();
            void Walk(AdaBoostStump n)
            {
                int id = tree.Nodes.Count;
                tree.Nodes.Add(new FlatNode());
                bool leaf = n.Feature < 0 || n.Left == null || n.Right == null;
                if (leaf)
                {
                    tree.Nodes[id] = new FlatNode { Id = id, Leaf = true };
                    tree.Leaves.Add(new FlatLeaf { Id = id, Prediction = n.Prediction });
                    return;
                }
                int left = tree.Nodes.Count;
                Walk(n.Left!);
                int right = tree.Nodes.Count;
                Walk(n.Right!);
                tree.Nodes[id] = new FlatNode
                {
                    Id = id,
                    Feature = n.Feature,
                    Threshold = (float)n.Threshold,
                    TrueId = left,
                    FalseId = right,
                    MissingTracksTrue = 0,
                };
            }
            Walk(stump);
            return tree;
        }

        static Built ExportScore(ModelBundle bundle, LinearScoreModel model)
        {
            int p = bundle.Features.Count;
            if (model.Coefficients.Length != p + 1)
                throw new OnnxNotExportableException("The linear score coefficient count does not match the feature list (expected features + intercept).");
            float intercept = Finite(model.Coefficients[0]);
            var coef = new float[p];
            for (int j = 0; j < p; j++) coef[j] = Finite(model.Coefficients[j + 1]);
            var proto = NewModel();
            string x = MaybeScale(proto, bundle, p);
            if (!model.Logistic)
            {
                proto.Node(MlDomain, "LinearRegressor", new[] { x }, new[] { "prediction" }, a =>
                {
                    a.Floats("coefficients", coef);
                    a.Floats("intercepts", new[] { intercept });
                    a.String("post_transform", "NONE");
                    a.Int("targets", 1);
                });
                proto.ValueOut("prediction", FloatType, -1, 1);
                return Finish(proto, "Linear score as LinearRegressor. The intercept is the first coefficient and is not a feature column.", new[] { "prediction" }, "NONE");
            }
            proto.Tensor("coef", new[] { p, 1 }, coef);
            proto.Tensor("intercept", new[] { 1, 1 }, new[] { intercept });
            proto.Node("", "MatMul", new[] { x, "coef" }, new[] { "dot" }, _ => { });
            proto.Node("", "Add", new[] { "dot", "intercept" }, new[] { "eta" }, _ => { });
            proto.Node("", "Sigmoid", new[] { "eta" }, new[] { "probability" }, _ => { });
            proto.Tensor("half", new[] { 1 }, new[] { 0.5f });
            proto.Node("", "GreaterOrEqual", new[] { "probability", "half" }, new[] { "ge" }, _ => { });
            proto.Node("", "Cast", new[] { "ge" }, new[] { "label" }, a => a.Int("to", Int64Type));
            proto.ValueOut("label", Int64Type, -1, 1);
            proto.ValueOut("probability", FloatType, -1, 1);
            return Finish(proto, "Logistic linear score as MatMul + Add + Sigmoid. Class 1 if probability ≥ 0.5. The intercept is not a feature column.", new[] { "label", "probability" }, "SIGMOID");
        }

        static float Finite(double v) => double.IsFinite(v) ? (float)v : 0f;


        static Built RegressorEnsemble(ModelBundle bundle, FlatTree[] trees, string aggregate, double? baseValue, string summary)
        {
            int p = bundle.Features.Count;
            var proto = NewModel();
            string x = MaybeScale(proto, bundle, p);
            var nodes = Concat(trees);
            proto.Node(MlDomain, "TreeEnsembleRegressor", new[] { x }, new[] { "prediction" }, a =>
            {
                WriteNodes(a, nodes, missing: nodes.Missing);
                a.Ints("target_treeids", nodes.TargetTree);
                a.Ints("target_nodeids", nodes.TargetNode);
                a.Ints("target_ids", nodes.TargetId);
                a.Floats("target_weights", nodes.TargetWeight);
                a.String("aggregate_function", aggregate);
                a.Int("n_targets", 1);
                a.String("post_transform", "NONE");
                if (baseValue is double b) a.Floats("base_values", new[] { (float)b });
            });
            proto.ValueOut("prediction", FloatType, -1, 1);
            return Finish(proto, summary + " post_transform NONE.", new[] { "prediction" }, "NONE");
        }

        // scoreOutput: 점수 텐서 이름. 확률이 아닌 결정 점수(AdaBoost SAMME)는 "scores"로 내보내 이름이 의미를 속이지 않게 한다.
        static Built ClassifierEnsemble(ModelBundle bundle, FlatTree[] trees, ClassVotes votes, string post, float[]? baseValues, string summary,
            string scoreOutput = "probabilities")
        {
            int p = bundle.Features.Count;
            var labels = Labels(bundle);
            var proto = NewModel();
            string x = MaybeScale(proto, bundle, p);
            var nodes = Concat(trees);
            proto.Node(MlDomain, "TreeEnsembleClassifier", new[] { x }, new[] { "ensemble_label", scoreOutput }, a =>
            {
                WriteNodes(a, nodes, nodes.Missing);
                a.Ints("class_treeids", votes.Tree);
                a.Ints("class_nodeids", votes.Node);
                a.Ints("class_ids", votes.ClassId);
                a.Floats("class_weights", votes.Weight);
                a.Strings("classlabels_strings", labels);
                a.String("post_transform", post);
                if (baseValues != null) a.Floats("base_values", baseValues);
            });
            proto.Node("", "ArgMax", new[] { scoreOutput }, new[] { "label" }, a =>
            {
                a.Int("axis", 1);
                a.Int("keepdims", 0);
            });
            proto.ValueOut("label", Int64Type, -1);
            proto.ValueOut(scoreOutput, FloatType, -1, labels.Length);
            return Finish(proto, summary + " Label is ArgMax of the scores (first maximum).", new[] { "label", scoreOutput }, post);
        }

        static Built Boosting(ModelBundle bundle, GradientBoostingModel model)
        {
            var raw = ModelStore.BoostingTrees(model);
            if (model.Task == BoostingTask.Regression)
            {
                var trees = FlattenBoost(raw, 0, model, regression: true);
                double baseline = model.Baseline.Length > 0 ? model.Baseline[0] : 0;
                return RegressorEnsemble(bundle, trees, "SUM", baseline,
                    "Gradient boosting regressor as TreeEnsembleRegressor (aggregate SUM, base_values = training baseline, post_transform NONE).");
            }
            if (model.Task == BoostingTask.Binary)
            {
                var trees = FlattenBoost(raw, 0, model, regression: false);
                var votes = BinaryBoostVotes(trees, model.Baseline.Length > 0 ? model.Baseline[0] : 0, Labels(bundle).Length);
                return ClassifierEnsemble(bundle, trees, votes, "LOGISTIC", null,
                    "Gradient boosting binary classifier as TreeEnsembleClassifier (post_transform LOGISTIC). Class 1 if the raw score > 0. An exact score of 0 is a tie and may be labeled class 1 by ONNX; the app labels it class 0.");
            }
            var all = new List<FlatTree>();
            var votesM = new ClassVotes();
            int k = model.ClassCount;
            for (int c = 0; c < raw.Length && c < k; c++)
            {
                var part = FlattenBoost(raw, c, model, regression: false);
                int treeBase = all.Count;
                double baseline = c < model.Baseline.Length ? model.Baseline[c] : 0;
                for (int t = 0; t < part.Length; t++)
                {
                    var tree = part[t];
                    tree.TreeId = treeBase + t;
                    foreach (var leaf in tree.Leaves)
                    {
                        float w = (float)leaf.Value;
                        if (t == 0) w += (float)baseline;
                        for (int cls = 0; cls < k; cls++)
                        {
                            if (cls != c) continue;
                            votesM.Tree.Add(tree.TreeId);
                            votesM.Node.Add(leaf.Id);
                            votesM.ClassId.Add(cls);
                            votesM.Weight.Add(w);
                        }
                    }
                    all.Add(tree);
                }
            }
            return ClassifierEnsemble(bundle, all.ToArray(), votesM, "SOFTMAX", null,
                "Gradient boosting multiclass classifier as TreeEnsembleClassifier (post_transform SOFTMAX). An exact score tie may be labeled with the last class by ONNX; the app keeps the first.");
        }

        static ClassVotes BinaryBoostVotes(FlatTree[] trees, double baseline, int classCount)
        {
            var votes = new ClassVotes();
            int k = Math.Max(classCount, 2);
            for (int t = 0; t < trees.Length; t++)
            {
                foreach (var leaf in trees[t].Leaves)
                {
                    float s = (float)leaf.Value;
                    if (t == 0) s += (float)baseline;
                    votes.Tree.Add(trees[t].TreeId); votes.Node.Add(leaf.Id); votes.ClassId.Add(0); votes.Weight.Add(-s);
                    votes.Tree.Add(trees[t].TreeId); votes.Node.Add(leaf.Id); votes.ClassId.Add(1); votes.Weight.Add(s);
                    for (int c = 2; c < k; c++)
                    {
                        votes.Tree.Add(trees[t].TreeId); votes.Node.Add(leaf.Id); votes.ClassId.Add(c); votes.Weight.Add(0);
                    }
                }
            }
            return votes;
        }

        static FlatTree[] FlattenBoost(GradientBoostingModel.PredNode[][][] trees, int cls, GradientBoostingModel model, bool regression)
        {
            if (cls >= trees.Length || trees[cls].Length == 0)
            {
                var leaf = new FlatTree { TreeId = 0 };
                leaf.Nodes.Add(new FlatNode { Id = 0, Leaf = true });
                leaf.Leaves.Add(new FlatLeaf { Id = 0, Value = 0 });
                return new[] { leaf };
            }
            var list = new FlatTree[trees[cls].Length];
            for (int t = 0; t < list.Length; t++)
            {
                var src = trees[cls][t];
                var tree = new FlatTree { TreeId = t };
                for (int i = 0; i < src.Length; i++)
                {
                    var n = src[i];
                    tree.Nodes.Add(new FlatNode
                    {
                        Id = i,
                        Leaf = n.Leaf,
                        Feature = n.Leaf ? 0 : n.Feature,
                        Threshold = n.Leaf ? 0 : (float)n.Threshold,
                        TrueId = n.Leaf ? 0 : n.Left,
                        FalseId = n.Leaf ? 0 : n.Right,
                        MissingTracksTrue = !n.Leaf && n.MissingLeft ? 1 : 0,
                    });
                    if (n.Leaf) tree.Leaves.Add(new FlatLeaf { Id = i, Value = n.Value, Prediction = 0 });
                }
                list[t] = tree;
            }
            return list;
        }

        static FlatTree[] NumberTrees(IEnumerable<FlatTree> trees)
        {
            var numbered = trees.ToArray();
            for (int i = 0; i < numbered.Length; i++) numbered[i].TreeId = i;
            return numbered;
        }


        static FlatTree FlattenTree(DecisionTreeModel model)
        {
            var tree = new FlatTree { TreeId = 0 };
            void Walk(Node n)
            {
                int id = tree.Nodes.Count;
                tree.Nodes.Add(new FlatNode());
                bool leaf = n.Feature < 0 || n.Left == null || n.Right == null;
                if (leaf)
                {
                    tree.Nodes[id] = new FlatNode { Id = id, Leaf = true };
                    tree.Leaves.Add(new FlatLeaf { Id = id, Value = n.Value, Prediction = n.Prediction, Counts = n.ClassCounts, Count = n.Count });
                    return;
                }
                int left = tree.Nodes.Count;
                Walk(n.Left!);
                int right = tree.Nodes.Count;
                Walk(n.Right!);
                tree.Nodes[id] = new FlatNode
                {
                    Id = id,
                    Feature = n.Feature,
                    Threshold = (float)n.Threshold,
                    TrueId = left,
                    FalseId = right,
                    MissingTracksTrue = 0,
                };
            }
            Walk(model.Root);
            return tree;
        }

        static ClassVotes ClassWeightsTree(DecisionTreeModel model)
        {
            var tree = FlattenTree(model);
            var votes = new ClassVotes();
            int k = model.ClassCount;
            foreach (var leaf in tree.Leaves)
            {
                for (int c = 0; c < k; c++)
                {
                    float w = LeafWeight(leaf, c, k);
                    votes.Tree.Add(0);
                    votes.Node.Add(leaf.Id);
                    votes.ClassId.Add(c);
                    votes.Weight.Add(w);
                }
            }
            return votes;
        }

        static ClassVotes ClassWeightsForest(RandomForestModel model)
        {
            var votes = new ClassVotes();
            int k = model.ClassCount;
            int n = Math.Max(model.Trees.Length, 1);
            float share = 1f / n;
            for (int t = 0; t < model.Trees.Length; t++)
            {
                var flat = FlattenTree(model.Trees[t]);
                flat.TreeId = t;
                foreach (var leaf in flat.Leaves)
                {
                    int pred = leaf.Prediction;
                    for (int c = 0; c < k; c++)
                    {
                        float w = c == pred ? share : 0f;
                        votes.Tree.Add(t);
                        votes.Node.Add(leaf.Id);
                        votes.ClassId.Add(c);
                        votes.Weight.Add(w);
                    }
                }
            }
            return votes;
        }

        static float LeafWeight(FlatLeaf leaf, int cls, int classCount)
        {
            if (leaf.Counts != null && cls < leaf.Counts.Length && leaf.Count > 0)
                return leaf.Counts[cls] / (float)leaf.Count;
            return cls == leaf.Prediction ? 1f : 0f;
        }

        static string MaybeScale(ModelWriter proto, ModelBundle bundle, int p)
        {
            proto.ValueIn(InputName, FloatType, -1, p);
            if (bundle.Scaler is null || bundle.Scaler.Method == ScalingMethod.None) return InputName;
            var center = new float[p];
            var scale = new float[p];
            for (int j = 0; j < p; j++)
            {
                center[j] = (float)bundle.Scaler.Center[j];
                scale[j] = (float)bundle.Scaler.Scale[j];
            }
            proto.Tensor("center", new[] { 1, p }, center);
            proto.Tensor("scale", new[] { 1, p }, scale);
            proto.Node("", "Sub", new[] { InputName, "center" }, new[] { "centered" }, _ => { });
            proto.Node("", "Div", new[] { "centered", "scale" }, new[] { "scaled" }, _ => { });
            return "scaled";
        }

        static ModelWriter NewModel()
        {
            var w = new ModelWriter();
            w.Int(1, IrVersion);
            w.Message(8, op => { op.Str(1, ""); op.Int(2, OnnxOpset); });
            w.Message(8, op => { op.Str(1, MlDomain); op.Int(2, MlOpset); });
            w.Str(2, "Nanum CSV Viewer");
            w.Str(3, AppInfo.Version);
            w.Str(6, "Input '" + InputName + "' is a float32 matrix [N, P] of model features in the sidecar order. Categorical columns are already one-hot; unseen levels are not representable here.");
            return w;
        }

        static Built Finish(ModelWriter proto, string summary, string[] outputs, string post)
        {
            var bytes = proto.Finish();
            return new Built { Bytes = bytes, Summary = summary + " IR " + IrVersion + ", ai.onnx " + OnnxOpset + ", ai.onnx.ml " + MlOpset + ". Inputs are the numeric feature vector (one-hot already applied); see the sidecar JSON.", Outputs = outputs, PostTransform = post };
        }

        static string Sidecar(ModelBundle bundle, Built built)
        {
            var features = bundle.Features.Select(f => new { column = f.Column, kind = f.Kind.ToString(), level = f.Level }).ToArray();
            var doc = new
            {
                format = "nanum-onnx-features",
                version = 1,
                modelType = bundle.ModelType,
                irVersion = IrVersion,
                opsets = new Dictionary<string, long> { [""] = OnnxOpset, [MlDomain] = MlOpset },
                input = new { name = InputName, dtype = "float32", features = bundle.Features.Count },
                outputs = built.Outputs,
                postTransform = built.PostTransform,
                classNames = bundle.ClassNames,
                scalerIncluded = bundle.Scaler != null && bundle.Scaler.Method != ScalingMethod.None,
                note = "The graph input is the numeric feature vector in this order, not raw category strings. One-hot columns are 1 when the cell equals level and 0 for other training levels. An unseen level must not be encoded as all zeros — the app's Apply Saved Model path rejects that row. A constant Intercept column, when present, is 1.",
                features,
            };
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        static string[] Labels(ModelBundle bundle)
        {
            if (bundle.ClassNames is { Count: > 0 }) return bundle.ClassNames.ToArray();
            int k = bundle.Engine switch
            {
                DecisionTreeModel t => t.ClassCount,
                RandomForestModel f => f.ClassCount,
                GradientBoostingModel g => Math.Max(g.ClassCount, 2),
                _ => 2,
            };
            return Enumerable.Range(0, k).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        }

        static void WriteNodes(AttrWriter a, ConcatNodes nodes, int[] missing)
        {
            a.Ints("nodes_treeids", nodes.Tree);
            a.Ints("nodes_nodeids", nodes.Node);
            a.Ints("nodes_featureids", nodes.Feature);
            a.Floats("nodes_values", nodes.Value);
            a.Floats("nodes_hitrates", nodes.Hit);
            a.Ints("nodes_truenodeids", nodes.True);
            a.Ints("nodes_falsenodeids", nodes.False);
            a.Strings("nodes_modes", nodes.Mode);
            a.Ints("nodes_missing_value_tracks_true", missing);
        }

        static ConcatNodes Concat(FlatTree[] trees)
        {
            var c = new ConcatNodes();
            for (int t = 0; t < trees.Length; t++)
            {
                int treeId = trees[t].TreeId;
                foreach (var n in trees[t].Nodes)
                {
                    c.Tree.Add(treeId);
                    c.Node.Add(n.Id);
                    c.Feature.Add(n.Feature);
                    c.Value.Add(n.Threshold);
                    c.Hit.Add(1f);
                    c.True.Add(n.TrueId);
                    c.False.Add(n.FalseId);
                    c.Mode.Add(n.Leaf ? "LEAF" : "BRANCH_LEQ");
                    c.MissingList.Add(n.MissingTracksTrue);
                }
                foreach (var leaf in trees[t].Leaves)
                {
                    c.TargetTree.Add(treeId);
                    c.TargetNode.Add(leaf.Id);
                    c.TargetId.Add(0);
                    c.TargetWeight.Add((float)leaf.Value);
                }
            }
            return c;
        }

        sealed class FlatTree
        {
            public int TreeId;
            public List<FlatNode> Nodes = new();
            public List<FlatLeaf> Leaves = new();
        }

        sealed class FlatNode
        {
            public int Id;
            public bool Leaf;
            public int Feature;
            public float Threshold;
            public int TrueId;
            public int FalseId;
            public int MissingTracksTrue;
        }

        sealed class FlatLeaf
        {
            public int Id;
            public double Value;
            public int Prediction;
            public int[]? Counts;
            public int Count;
        }

        sealed class ClassVotes
        {
            public List<long> Tree = new();
            public List<long> Node = new();
            public List<long> ClassId = new();
            public List<float> Weight = new();
        }

        sealed class ConcatNodes
        {
            public List<long> Tree = new(), Node = new(), Feature = new(), True = new(), False = new();
            public List<float> Hit = new();
            public List<float> Value = new();
            public List<string> Mode = new();
            public List<long> MissingList = new();
            public int[] Missing => MissingList.Select(v => (int)v).ToArray();
            public void AddMissing(int v) => MissingList.Add(v);
            public List<long> TargetTree = new(), TargetNode = new(), TargetId = new();
            public List<float> TargetWeight = new();
        }

        sealed class ModelWriter
        {
            readonly Proto _root = new();
            readonly Proto _graph = new();
            bool _closed;

            public void Int(int field, long v) => _root.Int(field, v);
            public void Str(int field, string s) => _root.Str(field, s);
            public void Message(int field, Action<Proto> write) => _root.Message(field, write);

            public void ValueIn(string name, int elem, params int[] dims) => Value(_graph, 11, name, elem, dims);
            public void ValueOut(string name, int elem, params int[] dims) => Value(_graph, 12, name, elem, dims);

            public void Tensor(string name, int[] dims, float[] data)
            {
                _graph.Message(5, t =>
                {
                    foreach (int d in dims) t.Int(1, d);
                    t.Int(2, FloatType);
                    t.PackedFloats(4, data);
                    t.Str(8, name);
                });
            }

            public void Node(string domain, string op, string[] inputs, string[] outputs, Action<AttrWriter> attrs)
            {
                _graph.Message(1, n =>
                {
                    foreach (var i in inputs) n.Str(1, i);
                    foreach (var o in outputs) n.Str(2, o);
                    n.Str(4, op);
                    attrs(new AttrWriter(n));
                    if (domain.Length > 0) n.Str(7, domain);
                });
            }

            public byte[] Finish()
            {
                if (_closed) return _root.ToArray();
                _closed = true;
                _graph.Str(2, "graph");
                var bytes = _graph.ToArray();
                _root.Bytes(7, bytes);
                return _root.ToArray();
            }

            static void Value(Proto g, int field, string name, int elem, int[] dims)
            {
                g.Message(field, v =>
                {
                    v.Str(1, name);
                    v.Message(2, type => type.Message(1, tensor =>
                    {
                        tensor.Int(1, elem);
                        tensor.Message(2, shape =>
                        {
                            foreach (int d in dims)
                            {
                                if (d < 0) shape.Message(1, _ => { });
                                else shape.Message(1, dim => dim.Int(1, d));
                            }
                        });
                    }));
                });
            }
        }

        sealed class AttrWriter
        {
            readonly Proto _n;
            public AttrWriter(Proto n) => _n = n;
            public void Floats(string name, IReadOnlyList<float> values) => _n.Message(5, a =>
            {
                a.Str(1, name);
                for (int i = 0; i < values.Count; i++) a.Float32(7, values[i]);
                a.Int(20, 6);
            });
            public void Ints(string name, IReadOnlyList<long> values) => _n.Message(5, a =>
            {
                a.Str(1, name);
                for (int i = 0; i < values.Count; i++) a.Int(8, values[i]);
                a.Int(20, 7);
            });
            public void Ints(string name, IReadOnlyList<int> values) => Ints(name, values.Select(v => (long)v).ToArray());
            public void Strings(string name, IReadOnlyList<string> values) => _n.Message(5, a =>
            {
                a.Str(1, name);
                for (int i = 0; i < values.Count; i++) a.Str(9, values[i]);
                a.Int(20, 8);
            });
            public void String(string name, string value) => _n.Message(5, a =>
            {
                a.Str(1, name);
                a.Bytes(4, Encoding.UTF8.GetBytes(value));
                a.Int(20, 3);
            });
            public void Int(string name, long value) => _n.Message(5, a =>
            {
                a.Str(1, name);
                a.Int(3, value);
                a.Int(20, 2);
            });
        }

        sealed class Proto
        {
            readonly MemoryStream _ms = new();
            public byte[] ToArray() => _ms.ToArray();
            public void Int(int field, long value) { Key(field, 0); Varint((ulong)value); }
            public void Str(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
            public void Bytes(int field, byte[] value) { Key(field, 2); Varint((ulong)value.Length); _ms.Write(value); }
            public void Float32(int field, float value) { Key(field, 5); _ms.Write(BitConverter.GetBytes(value)); }
            public void PackedFloats(int field, float[] values)
            {
                var bytes = new byte[values.Length * 4];
                Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
                Bytes(field, bytes);
            }
            public void Message(int field, Action<Proto> write)
            {
                var inner = new Proto();
                write(inner);
                Bytes(field, inner.ToArray());
            }
            void Key(int field, int wire) => Varint((ulong)((field << 3) | wire));
            void Varint(ulong value)
            {
                while (value > 0x7F)
                {
                    _ms.WriteByte((byte)(value | 0x80));
                    value >>= 7;
                }
                _ms.WriteByte((byte)value);
            }
        }

        sealed class ProtoReader
        {
            readonly byte[] _b;
            int _i;
            public ProtoReader(byte[] b) { _b = b; }
            public bool Remaining => _i < _b.Length;
            public void ReadKey(out int field, out int wire)
            {
                ulong key = ReadVarint();
                field = (int)(key >> 3);
                wire = (int)(key & 7);
            }
            public ulong ReadVarint()
            {
                ulong v = 0;
                int shift = 0;
                while (true)
                {
                    if (_i >= _b.Length) throw new OnnxNotExportableException("Truncated ONNX protobuf.");
                    byte x = _b[_i++];
                    v |= (ulong)(x & 0x7F) << shift;
                    if ((x & 0x80) == 0) return v;
                    shift += 7;
                }
            }
            public byte[] ReadBytes()
            {
                int n = (int)ReadVarint();
                if (_i + n > _b.Length) throw new OnnxNotExportableException("Truncated ONNX protobuf.");
                var slice = new byte[n];
                Buffer.BlockCopy(_b, _i, slice, 0, n);
                _i += n;
                return slice;
            }
            public string ReadString() => Encoding.UTF8.GetString(ReadBytes());
            public void Skip(int wire)
            {
                switch (wire)
                {
                    case 0: ReadVarint(); break;
                    case 1: _i += 8; break;
                    case 2: ReadBytes(); break;
                    case 5: _i += 4; break;
                    default: throw new OnnxNotExportableException("Unsupported protobuf wire type " + wire + ".");
                }
            }
        }
    }
}
