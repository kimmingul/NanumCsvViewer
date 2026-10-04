using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NanumCsvViewer.Stats
{
    /// <summary>저장 형식·스키마가 맞지 않거나 엔진을 재구성할 수 없을 때. 메시지는 영어로 안내한다.</summary>
    public sealed class ModelStoreException : InvalidOperationException
    {
        public ModelStoreException(string message) : base(message) { }
    }

    /// <summary>파일에 적힌 앱 버전·작성 시각과 함께 복원한 모형.</summary>
    public sealed record StoredModel(ModelBundle Model, string AppVersion, DateTime CreatedAt);

    /// <summary>
    /// 한 행의 적용 결과. Scorable이 아니면 예측을 만들지 않는다(결측·학습에 없던 범주 수준을 0으로 두지 않음).
    /// </summary>
    public sealed class RowPrediction
    {
        public bool Scorable { get; init; }
        public string? Reason { get; init; }
        /// <summary>분류면 0..K-1. 회귀·불가 행은 -1.</summary>
        public int ClassIndex { get; init; } = -1;
        public string? ClassLabel { get; init; }
        /// <summary>회귀 값, 또는 분류에서 사건(클래스 1) 확률. 없으면 NaN.</summary>
        public double Value { get; init; } = double.NaN;
        public double[]? Probability { get; init; }
        /// <summary>시행 수 열을 쓴 이항 GLzM만: 시행 수 × 사건 확률(기대 성공 횟수). 그 외는 NaN.</summary>
        public double ExpectedSuccesses { get; init; } = double.NaN;
    }

    /// <summary>이미 인코딩된 특성 행렬(스케일 전)에 대한 예측. 로드 후 같은 입력이면 비트 단위로 같다.</summary>
    public sealed class EncodedPredictions
    {
        public required int Count { get; init; }
        public int[]? ClassIndex { get; init; }
        public double[]? Value { get; init; }
        /// <summary>시행 수 열을 쓴 이항 GLzM만: 행별 시행 수 × 확률.</summary>
        public double[]? ExpectedSuccesses { get; init; }
        public double[,]? Probability { get; init; }
    }

    /// <summary>현재 뷰 헤더에 모형의 열을 이름(Ordinal, 없으면 대소문자 무시 유일 일치)으로 붙인 결과.</summary>
    public sealed class ModelBinding
    {
        public bool Ready { get; init; }
        public IReadOnlyList<string> MissingColumns { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> AmbiguousColumns { get; init; } = Array.Empty<string>();
        /// <summary>적용 데이터에 없거나 모호해 모형이 요구하는 오프셋·노출·시행 수 열(MissingColumns에도 포함).</summary>
        public IReadOnlyList<string> MissingPredictionColumns { get; init; } = Array.Empty<string>();
        public string? Error { get; init; }
        internal int Width;
        internal bool FormulaMode;
        internal ModelStore.FormulaLayout? Formula;
        internal ModelStore.FeatureLayout? Features;
        internal ModelStore.ExtrasLayout? Extras;
    }

    /// <summary>
    /// K-최근접 이웃의 저장 가능 적합. 학습 행(스케일 적용 후)과 k를 보관하고 ALGLIB kd-tree로 예측한다.
    /// 확률은 같은 질의의 클래스 확률(동률이면 작은 인덱스)이다.
    /// </summary>
    public sealed class KnnModel
    {
        public required double[,] TrainX { get; init; }
        public required int[] Labels { get; init; }
        public required int ClassCount { get; init; }
        public required int Neighbors { get; init; }

        public int[] Predict(double[,] x, CancellationToken cancellation = default)
            => KnnClassifier.Predict(TrainX, Labels, ClassCount, x, Neighbors, cancellation);

        /// <summary>클래스와 확률을 한 번의 kd-tree 구축으로 채운다. 클래스는 <see cref="KnnClassifier.Predict"/>와 같은 규칙.</summary>
        public void Predict(double[,] x, int[] classes, double[,]? probabilities, CancellationToken cancellation = default)
        {
            int n = TrainX.GetLength(0), p = TrainX.GetLength(1), m = x.GetLength(0);
            if (x.GetLength(1) != p) throw new ArgumentException("Feature count does not match.", nameof(x));
            if (classes.Length != m) throw new ArgumentException("Class buffer length must match rows.", nameof(classes));
            if (probabilities != null && (probabilities.GetLength(0) != m || probabilities.GetLength(1) != ClassCount))
                throw new ArgumentException("Probability buffer shape must be rows × classes.", nameof(probabilities));
            if (n < 1 || Neighbors < 1 || Neighbors > n || ClassCount < 2)
                throw new ModelStoreException("The stored KNN model is incomplete.");

            var xy = new double[n, p + 1];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++) xy[i, j] = TrainX[i, j];
                xy[i, p] = Labels[i];
            }
            alglib.knnmodel built;
            try
            {
                alglib.knnbuildercreate(out var builder);
                alglib.knnbuildersetdatasetcls(builder, xy, n, p, ClassCount);
                alglib.knnbuildersetnorm(builder, 2);
                alglib.knnbuilderbuildknnmodel(builder, Neighbors, 0.0, out built, out _);
            }
            catch (alglib.alglibexception ex)
            {
                throw new ModelStoreException(string.IsNullOrWhiteSpace(ex.msg) ? "KNN model build failed." : ex.msg);
            }
            int parts = Math.Clamp(m / 2048, 1, Environment.ProcessorCount);
            Parallel.For(0, parts, new ParallelOptions { CancellationToken = cancellation }, part =>
            {
                int from = (int)((long)m * part / parts), to = (int)((long)m * (part + 1) / parts);
                alglib.knncreatebuffer(built, out var buffer);
                var row = new double[p];
                var probs = new double[ClassCount];
                for (int i = from; i < to; i++)
                {
                    if (((i - from) & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    for (int j = 0; j < p; j++) row[j] = x[i, j];
                    alglib.knntsprocess(built, buffer, row, ref probs);
                    int cls = 0;
                    for (int c = 1; c < probs.Length; c++) if (probs[c] > probs[cls]) cls = c;
                    classes[i] = cls;
                    if (probabilities != null)
                        for (int c = 0; c < ClassCount; c++) probabilities[i, c] = probs[c];
                }
            });
        }
    }

    /// <summary>
    /// 적합 모형의 버전 있는 JSON 저장·로드와, 저장된 모형을 다른 표에 적용하는 인코딩.
    /// 형식 이름 <c>nanum-model</c>, 버전 1. 큰 수치 배열은 little-endian float64 base64라 로드 후 예측이 비트 단위로 같다.
    /// </summary>
    public static class ModelStore
    {
        /// <summary>이 빌드가 읽는 가장 높은 형식 버전. 2부터 GLzM 오프셋·노출·시행 수 열을 담는다.</summary>
        public const int FormatVersion = 2;
        /// <summary>그런 열이 없는 모형을 쓰는 형식 버전(이전 빌드와 호환).</summary>
        public const int BaseFormatVersion = 1;
        public const string FormatName = "nanum-model";
        public const int ApplyBatchSize = 4096;
        /// <summary>한 수치 덩어리·파일의 상한. 파일의 차원 값이 int 곱셈으로 넘쳐도 이 한도를 넘기면 할당하지 않는다.</summary>
        public const long MaxPayloadBytes = 1L << 30;
        const int MaxTreeDepth = 256;
        const int MaxNodeCount = 2_000_000;

        static readonly JsonSerializerOptions JsonOpt = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = false,
        };

        static readonly ConstructorInfo ScalerCtor = typeof(FeatureScaler).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(ScalingMethod), typeof(double[]), typeof(double[]) },
            modifiers: null) ?? throw new InvalidOperationException("FeatureScaler constructor is not available.");

        static readonly ConstructorInfo BayesCtor = typeof(NaiveBayesModel).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic).Single();

        public static string Serialize(ModelBundle bundle, string appVersion, DateTime createdAt)
        {
            if (bundle is null) throw new ArgumentNullException(nameof(bundle));
            if (string.IsNullOrWhiteSpace(appVersion)) throw new ArgumentException("App version is required.", nameof(appVersion));
            var dto = new FileDto
            {
                Format = FormatName,
                // 오프셋·노출·시행 수 열이 필요한 모형은 버전 2로 쓴다 — 이 열을 모르는 이전 빌드가 읽고 조용히 틀린 예측을 내지 않게 한다.
                Version = bundle.UsesPredictionColumns ? FormatVersion : BaseFormatVersion,
                AppVersion = appVersion,
                CreatedAt = createdAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ModelType = bundle.ModelType,
                Task = bundle.Task.ToString(),
                Target = bundle.Target,
                ClassNames = bundle.ClassNames?.ToArray(),
                TrainingRows = bundle.TrainingRows,
                EvaluationSummary = bundle.EvaluationSummary,
                Formula = bundle.Formula,
                HasIntercept = bundle.HasIntercept,
                ForcedCategorical = bundle.ForcedCategorical?.ToArray() ?? Array.Empty<string>(),
                ResponseLevels = bundle.ResponseLevels?.ToArray(),
                OffsetColumn = bundle.OffsetColumn,
                ExposureColumn = bundle.ExposureColumn,
                TrialsColumn = bundle.TrialsColumn,
                VarianceWeightColumn = bundle.VarianceWeightColumn,
                FrequencyWeightColumn = bundle.FrequencyWeightColumn,
                Parameters = new Dictionary<string, string>(bundle.Parameters, StringComparer.Ordinal),
                Features = bundle.Features.Select(f => new FeatureDto { Column = f.Column, Kind = f.Kind.ToString(), Level = f.Level }).ToArray(),
                Factors = bundle.Factors?.Select(f => new FactorDto { Variable = f.Variable, Levels = f.Levels.ToArray() }).ToArray(),
                Scaler = WriteScaler(bundle.Scaler),
                Engine = WriteEngine(bundle),
            };
            return JsonSerializer.Serialize(dto, JsonOpt);
        }

        public static void Save(ModelBundle bundle, string path, string appVersion, DateTime createdAt)
        {
            var json = Serialize(bundle, appVersion, createdAt);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public static StoredModel Load(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ModelStoreException("The model file is empty.");
            FileDto dto;
            try { dto = JsonSerializer.Deserialize<FileDto>(json, JsonOpt) ?? throw new ModelStoreException("The model file is empty."); }
            catch (ModelStoreException) { throw; }
            catch (JsonException ex) { throw new ModelStoreException("The model file is not valid JSON: " + ex.Message); }
            return Read(dto);
        }

        public static StoredModel LoadFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new ModelStoreException("The model file was not found.");
            RequireFileSize(info.Length);
            return Load(File.ReadAllText(path));
        }

        /// <summary>파일 길이 검사. 읽기 전에 호출한다.</summary>
        internal static void RequireFileSize(long length)
        {
            if (length < 0 || length > MaxPayloadBytes)
                throw new ModelStoreException(
                    $"The model file is {length.ToString("N0", CultureInfo.InvariantCulture)} bytes, over the {MaxPayloadBytes.ToString("N0", CultureInfo.InvariantCulture)} byte limit. The model was not loaded.");
        }

        public static ModelBinding Bind(ModelBundle model, IReadOnlyList<string> headers)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (headers is null) throw new ArgumentNullException(nameof(headers));
            if (model.Features is null || model.Features.Count == 0)
                return FailBind("The model has no features.");
            if (!string.IsNullOrEmpty(model.Formula))
                return BindFormula(model, headers);
            return BindFeatures(model, headers);
        }

        public static bool EncodeRow(ModelBinding binding, string[] row, double[] destination, out string? reason)
        {
            if (!binding.Ready) throw new ModelStoreException(binding.Error ?? "The model is not bound.");
            if (destination.Length != binding.Width) throw new ArgumentException("Destination width does not match the model.", nameof(destination));
            return TryEncode(binding, row, destination, out reason);
        }

        public static RowPrediction Score(ModelBundle model, ModelBinding binding, string[] row)
        {
            var dest = new RowPrediction[1];
            ScoreMany(model, binding, new[] { row }, 0, 1, dest, default);
            return dest[0];
        }

        public static void ScoreMany(
            ModelBundle model, ModelBinding binding, IReadOnlyList<string[]> rows,
            int offset, int count, RowPrediction[] dest, CancellationToken cancellation)
        {
            if (!binding.Ready)
                throw new ModelStoreException(binding.Error ?? "The model is not bound to these columns.");
            if (offset < 0 || count < 0 || offset + count > rows.Count) throw new ArgumentOutOfRangeException(nameof(count));
            if (dest.Length < count) throw new ArgumentException("Destination is shorter than the row count.", nameof(dest));
            int p = binding.Width;
            var encoded = new double[count, p];
            var ok = new bool[count];
            var reasons = new string?[count];
            var buf = new double[p];
            double[]? rowOffset = null, rowTrials = null;
            for (int i = 0; i < count; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                if (TryEncode(binding, rows[offset + i], buf, out var reason)
                    && TryRowExtras(binding, rows[offset + i], i, ref rowOffset, ref rowTrials, count, out reason))
                {
                    ok[i] = true;
                    for (int j = 0; j < p; j++) encoded[i, j] = buf[j];
                }
                else reasons[i] = reason;
            }
            int nOk = 0;
            for (int i = 0; i < count; i++) if (ok[i]) nOk++;
            EncodedPredictions? pred = null;
            int[] map = Array.Empty<int>();
            if (nOk > 0)
            {
                var compact = new double[nOk, p];
                map = new int[nOk];
                var cOffset = binding.Extras is { NeedsOffset: true } ? new double[nOk] : null;
                var cTrials = binding.Extras is { TrialsCol: >= 0 } ? new double[nOk] : null;
                int w = 0;
                for (int i = 0; i < count; i++)
                {
                    if (!ok[i]) continue;
                    map[w] = i;
                    for (int j = 0; j < p; j++) compact[w, j] = encoded[i, j];
                    if (cOffset != null) cOffset[w] = rowOffset![i];
                    if (cTrials != null) cTrials[w] = rowTrials![i];
                    w++;
                }
                pred = PredictEncoded(model, compact, cancellation, cOffset, cTrials);
            }
            var names = model.ClassNames;
            for (int i = 0; i < count; i++)
            {
                if (!ok[i])
                {
                    dest[i] = new RowPrediction { Scorable = false, Reason = reasons[i], Value = double.NaN };
                    continue;
                }
            }
            if (pred is null) return;
            for (int t = 0; t < map.Length; t++)
            {
                int i = map[t];
                int cls = pred.ClassIndex?[t] ?? -1;
                double[]? proba = null;
                if (pred.Probability != null)
                {
                    int k = pred.Probability.GetLength(1);
                    proba = new double[k];
                    for (int c = 0; c < k; c++) proba[c] = pred.Probability[t, c];
                }
                string? label = cls >= 0 && names != null && cls < names.Count ? names[cls] : (cls >= 0 ? cls.ToString(CultureInfo.InvariantCulture) : null);
                double value = pred.Value?[t] ?? double.NaN;
                bool finite = model.Task == ModelTask.Classification ? cls >= 0 : double.IsFinite(value);
                dest[i] = finite
                    ? new RowPrediction { Scorable = true, ClassIndex = cls, ClassLabel = label, Value = value, Probability = proba, ExpectedSuccesses = pred.ExpectedSuccesses?[t] ?? double.NaN }
                    : new RowPrediction { Scorable = false, Reason = "The model mean is undefined for this row.", Value = double.NaN };
            }
        }

        /// <param name="offset">행별 오프셋 + ln(노출). 모형이 오프셋·노출 열을 쓰면 필요하다.</param>
        /// <param name="trials">행별 시행 수. 모형이 시행 수 열을 쓰면 필요하다.</param>
        public static EncodedPredictions PredictEncoded(ModelBundle model, double[,] rawFeatures, CancellationToken cancellation = default,
            double[]? offset = null, double[]? trials = null)
        {
            if (rawFeatures.GetLength(1) != model.Features.Count)
                throw new ModelStoreException($"Feature count {rawFeatures.GetLength(1)} does not match the model ({model.Features.Count}).");
            int rowsIn = rawFeatures.GetLength(0);
            if ((model.OffsetColumn != null || model.ExposureColumn != null) && (offset is null || offset.Length != rowsIn))
                throw new ModelStoreException("This model was fitted with an offset or exposure column; per-row offsets (offset + ln exposure) are required to predict.");
            if (model.TrialsColumn != null && (trials is null || trials.Length != rowsIn))
                throw new ModelStoreException("This model was fitted with a trials column; per-row trials are required to predict.");
            var x = model.Scaler is null ? rawFeatures : model.Scaler.Transform(rawFeatures);
            cancellation.ThrowIfCancellationRequested();
            return model.Engine switch
            {
                KnnModel knn => PredictKnn(knn, x, model, cancellation),
                NaiveBayesModel nb => PredictBayes(nb, x, cancellation),
                LinearDiscriminantModel lda => PredictLda(lda, x, cancellation),
                MultinomialLogisticModel mn => PredictMultinomial(mn, x, cancellation),
                DecisionTreeModel tree => PredictTree(tree, x, cancellation),
                RandomForestModel forest => PredictForest(forest, x, cancellation),
                SvmModel svm => PredictSvm(svm, x, model, cancellation),
                GradientBoostingModel gb => PredictBoosting(gb, x, model, cancellation),
                LinearModelFit ols => PredictLinear(ols.Beta, x, regression: true, link: null, null, cancellation),
                GeneralizedLinearFit glm => PredictGlm(glm, x, model, offset, trials, cancellation),
                AdaBoostModel ada => PredictAda(ada, x, cancellation),
                LinearScoreModel score => PredictScore(score, x, cancellation),
                _ => throw new ModelStoreException($"Model type '{model.ModelType}' cannot be scored (engine {model.Engine?.GetType().Name ?? "null"})."),
            };
        }

        /// <summary>목표 열이 뷰에 있을 때 채점 행만으로 지표를 누적한다. 채점 불가 행은 지표에 넣지 않는다.</summary>
        public static ApplyMetrics ScoreView(
            ModelBundle model, ModelBinding binding, IReadOnlyList<string[]> rows,
            int targetColumn, CancellationToken cancellation = default)
        {
            var metrics = new ApplyMetrics(model);
            int n = rows.Count;
            var batch = new RowPrediction[Math.Min(ApplyBatchSize, Math.Max(n, 1))];
            for (int offset = 0; offset < n; offset += ApplyBatchSize)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = Math.Min(ApplyBatchSize, n - offset);
                if (batch.Length < count) batch = new RowPrediction[count];
                ScoreMany(model, binding, rows, offset, count, batch, cancellation);
                for (int i = 0; i < count; i++)
                    metrics.Add(batch[i], targetColumn >= 0 ? Cell(rows[offset + i], targetColumn) : null, targetColumn >= 0);
            }
            metrics.Finish();
            return metrics;
        }

        internal static GradientBoostingModel.PredNode[][][] BoostingTrees(GradientBoostingModel model)
        {
            var field = typeof(GradientBoostingModel).GetField("_trees", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new ModelStoreException("Gradient boosting tree storage is not available.");
            return (GradientBoostingModel.PredNode[][][])field.GetValue(model)!;
        }

        static StoredModel Read(FileDto dto)
        {
            if (!string.Equals(dto.Format, FormatName, StringComparison.Ordinal))
                throw new ModelStoreException($"Unrecognized model format '{dto.Format ?? ""}'. Expected {FormatName}.");
            if (dto.Version < BaseFormatVersion || dto.Version > FormatVersion)
                throw new ModelStoreException($"Unsupported model format version {dto.Version}. This build reads versions {BaseFormatVersion} to {FormatVersion}.");
            if (string.IsNullOrWhiteSpace(dto.ModelType)) throw new ModelStoreException("The model file has no model type.");
            if (string.IsNullOrWhiteSpace(dto.Target)) throw new ModelStoreException("The model file has no target name.");
            if (dto.Features is null || dto.Features.Length == 0) throw new ModelStoreException("The model file has no features.");
            if (!Enum.TryParse<ModelTask>(dto.Task, out var task))
                throw new ModelStoreException($"Unknown model task '{dto.Task}'.");
            if (!DateTime.TryParse(dto.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
                throw new ModelStoreException("The model file has no valid created-at timestamp.");
            if (string.IsNullOrWhiteSpace(dto.AppVersion)) throw new ModelStoreException("The model file has no app version.");
            var features = new ModelFeature[dto.Features.Length];
            for (int i = 0; i < features.Length; i++)
            {
                var f = dto.Features[i] ?? throw new ModelStoreException("A feature entry is missing.");
                if (string.IsNullOrEmpty(f.Column)) throw new ModelStoreException("A feature has no column name.");
                if (!Enum.TryParse<VariableKind>(f.Kind, out var kind))
                    throw new ModelStoreException($"Unknown feature kind '{f.Kind}'.");
                features[i] = new ModelFeature(f.Column, kind, f.Level);
            }
            if (!string.IsNullOrEmpty(dto.Formula))
            {
                try { _ = ModelFormula.Parse(dto.Formula); }
                catch (FormulaParseException ex) { throw new ModelStoreException("The stored formula cannot be parsed: " + ex.Message); }
                var parsed = ModelFormula.Parse(dto.Formula);
                if (parsed.Intercept != dto.HasIntercept)
                    throw new ModelStoreException("The stored intercept flag does not match the formula.");
            }
            var factors = dto.Factors?.Select(f =>
            {
                if (f is null || string.IsNullOrEmpty(f.Variable) || f.Levels is null || f.Levels.Length < 2)
                    throw new ModelStoreException("A stored factor is missing its levels.");
                return new ModelFactor(f.Variable, f.Levels);
            }).ToArray();
            var engine = ReadEngine(dto.ModelType, dto.Engine, features.Length, task);
            ValidatePredictionColumns(dto, engine, task);
            var bundle = new ModelBundle
            {
                ModelType = dto.ModelType,
                Task = task,
                Features = features,
                Scaler = ReadScaler(dto.Scaler, features.Length),
                Target = dto.Target,
                ClassNames = dto.ClassNames,
                Engine = engine,
                Formula = dto.Formula,
                Factors = factors,
                HasIntercept = dto.HasIntercept,
                ForcedCategorical = dto.ForcedCategorical ?? Array.Empty<string>(),
                ResponseLevels = dto.ResponseLevels,
                OffsetColumn = dto.OffsetColumn,
                ExposureColumn = dto.ExposureColumn,
                TrialsColumn = dto.TrialsColumn,
                VarianceWeightColumn = dto.VarianceWeightColumn,
                FrequencyWeightColumn = dto.FrequencyWeightColumn,
                TrainingRows = dto.TrainingRows,
                EvaluationSummary = dto.EvaluationSummary,
                Parameters = dto.Parameters ?? new Dictionary<string, string>(),
            };
            return new StoredModel(bundle, dto.AppVersion, created);
        }

        /// <summary>
        /// 오프셋·노출·시행 수 열은 식 GLzM만 갖고(버전 2부터), 이름이 있어야 하며, 시행 수는 이항 + 회귀(기대 성공 횟수) 모형에만 있다.
        /// 조건을 어긴 파일은 예측이 조용히 달라질 수 있으므로 불러오지 않는다.
        /// </summary>
        static void ValidatePredictionColumns(FileDto dto, object engine, ModelTask task)
        {
            bool any = dto.OffsetColumn != null || dto.ExposureColumn != null || dto.TrialsColumn != null;
            if (!any) return;
            if (dto.Version < 2)
                throw new ModelStoreException("The model file declares an offset, exposure or trials column but is format version 1. The model was not loaded.");
            if (!string.Equals(dto.ModelType, ModelTypes.Glzm, StringComparison.Ordinal) || engine is not GeneralizedLinearFit glm || string.IsNullOrEmpty(dto.Formula))
                throw new ModelStoreException("Offset, exposure and trials columns are only valid for a formula GLzM model. The model was not loaded.");
            foreach (var name in new[] { dto.OffsetColumn, dto.ExposureColumn, dto.TrialsColumn })
                if (name != null && string.IsNullOrWhiteSpace(name))
                    throw new ModelStoreException("A stored offset, exposure or trials column name is empty. The model was not loaded.");
            if (dto.TrialsColumn != null && (glm.Family != GlmFamily.Binomial || task != ModelTask.Regression))
                throw new ModelStoreException("A trials column is only valid for a binomial model that predicts probability and expected successes. The model was not loaded.");
        }

        internal sealed class ExtrasLayout
        {
            public int OffsetCol = -1, ExposureCol = -1, TrialsCol = -1;
            public string? OffsetName, ExposureName, TrialsName;
            public bool NeedsOffset => OffsetCol >= 0 || ExposureCol >= 0;
        }

        /// <summary>모형이 요구하는 오프셋·노출·시행 수 열을 뷰 헤더에 붙인다. 없거나 모호한 열 이름은 missing/ambiguous에 더한다.</summary>
        static ExtrasLayout? BindExtras(ModelBundle model, IReadOnlyList<string> headers, List<string> missing, List<string> ambiguous, List<string> needed)
        {
            if (!model.UsesPredictionColumns) return null;
            var layout = new ExtrasLayout { OffsetName = model.OffsetColumn, ExposureName = model.ExposureColumn, TrialsName = model.TrialsColumn };
            int Resolve(string? name)
            {
                if (name is null) return -1;
                if (TryResolve(headers, name, out int idx, out bool amb)) return idx;
                (amb ? ambiguous : missing).Add(name);
                needed.Add(name);
                return -1;
            }
            layout.OffsetCol = Resolve(model.OffsetColumn);
            layout.ExposureCol = Resolve(model.ExposureColumn);
            layout.TrialsCol = Resolve(model.TrialsColumn);
            return layout;
        }

        /// <summary>행의 오프셋(오프셋 + ln 노출)과 시행 수를 읽는다. 결측·비수치·노출 ≤ 0·시행 수가 양의 정수가 아니면 채점 불가.</summary>
        static bool TryExtras(ExtrasLayout layout, string[] row, out double offset, out double trials, out string? reason)
        {
            offset = 0;
            trials = double.NaN;
            reason = null;
            if (layout.OffsetCol >= 0)
            {
                if (layout.OffsetCol >= row.Length || !StatValue.TryNumber(row[layout.OffsetCol], out double o))
                {
                    reason = $"missing or non-numeric offset value in '{layout.OffsetName}'";
                    return false;
                }
                offset += o;
            }
            if (layout.ExposureCol >= 0)
            {
                if (layout.ExposureCol >= row.Length || !StatValue.TryNumber(row[layout.ExposureCol], out double e))
                {
                    reason = $"missing or non-numeric exposure value in '{layout.ExposureName}'";
                    return false;
                }
                if (!(e > 0))
                {
                    reason = $"exposure must be greater than 0 in '{layout.ExposureName}'";
                    return false;
                }
                offset += Math.Log(e);
            }
            if (layout.TrialsCol >= 0)
            {
                if (layout.TrialsCol >= row.Length || !StatValue.TryNumber(row[layout.TrialsCol], out double t))
                {
                    reason = $"missing or non-numeric trials value in '{layout.TrialsName}'";
                    return false;
                }
                if (t < 1 || Math.Abs(t - Math.Round(t)) > 1e-8)
                {
                    reason = $"trials must be a positive integer in '{layout.TrialsName}'";
                    return false;
                }
                trials = Math.Round(t);
            }
            if (!double.IsFinite(offset))
            {
                reason = "offset plus ln(exposure) is not finite";
                return false;
            }
            return true;
        }

        static ModelBinding BindFormula(ModelBundle model, IReadOnlyList<string> headers)
        {
            ModelFormula formula;
            try { formula = ModelFormula.Parse(model.Formula!); }
            catch (FormulaParseException ex) { return FailBind("The stored formula cannot be parsed: " + ex.Message); }
            var predictors = formula.PredictorVariables;
            var missing = new List<string>();
            var ambiguous = new List<string>();
            var index = new int[predictors.Count];
            var categorical = new bool[predictors.Count];
            var levels = new string[predictors.Count][];
            var factorByName = new Dictionary<string, ModelFactor>(StringComparer.Ordinal);
            if (model.Factors != null)
                foreach (var f in model.Factors) factorByName[f.Variable] = f;
            for (int k = 0; k < predictors.Count; k++)
            {
                string name = predictors[k];
                if (!TryResolve(headers, name, out index[k], out bool amb))
                {
                    if (amb) ambiguous.Add(name);
                    else missing.Add(name);
                    continue;
                }
                if (factorByName.TryGetValue(name, out var factor))
                {
                    categorical[k] = true;
                    levels[k] = factor.Levels.ToArray();
                }
                else if (formula.ForcedCategorical.Contains(name) || model.ForcedCategorical.Contains(name, StringComparer.Ordinal))
                    missing.Add(name + " (categorical levels were not stored)");
            }
            var neededExtras = new List<string>();
            var extras = BindExtras(model, headers, missing, ambiguous, neededExtras);
            if (missing.Count > 0 || ambiguous.Count > 0)
            {
                string error = DescribeBind(missing, ambiguous);
                if (neededExtras.Count > 0)
                    error += " The model was fitted with an offset, exposure or trials column; the new data needs the same numeric column(s): "
                        + string.Join(", ", neededExtras.Distinct(StringComparer.Ordinal)) + ".";
                return new ModelBinding
                {
                    MissingColumns = missing, AmbiguousColumns = ambiguous, Error = error,
                    MissingPredictionColumns = neededExtras.Distinct(StringComparer.Ordinal).ToArray(),
                };
            }
            var columns = ExpandFormula(formula, predictors, categorical, levels);
            if (columns.Length != model.Features.Count)
                return FailBind($"The formula expands to {columns.Length} columns but the model has {model.Features.Count}.");
            return new ModelBinding
            {
                Ready = true,
                Width = columns.Length,
                FormulaMode = true,
                Formula = new FormulaLayout(predictors.ToArray(), index, categorical, levels, columns),
                Extras = extras,
            };
        }

        static ModelBinding BindFeatures(ModelBundle model, IReadOnlyList<string> headers)
        {
            var missing = new List<string>();
            var ambiguous = new List<string>();
            var groups = new List<FeatureGroupEnc>();
            var seen = new Dictionary<string, FeatureGroupEnc>(StringComparer.Ordinal);
            for (int j = 0; j < model.Features.Count; j++)
            {
                var f = model.Features[j];
                if (!seen.TryGetValue(f.Column, out var group))
                {
                    if (!TryResolve(headers, f.Column, out int col, out bool amb))
                    {
                        if (amb) ambiguous.Add(f.Column);
                        else missing.Add(f.Column);
                        col = -1;
                    }
                    group = new FeatureGroupEnc(f.Column, col);
                    seen[f.Column] = group;
                    groups.Add(group);
                }
                group.Indexes.Add(j);
                group.Levels.Add(f.Level);
                if (f.Level != null || f.Kind == VariableKind.Categorical) group.Categorical = true;
            }
            if (missing.Count > 0 || ambiguous.Count > 0)
                return new ModelBinding { MissingColumns = missing.Distinct(StringComparer.Ordinal).ToArray(), AmbiguousColumns = ambiguous.Distinct().ToArray(), Error = DescribeBind(missing, ambiguous) };
            return new ModelBinding
            {
                Ready = true,
                Width = model.Features.Count,
                Features = new FeatureLayout(groups),
            };
        }

        static bool TryEncode(ModelBinding binding, string[] row, double[] dest, out string? reason)
        {
            Array.Clear(dest, 0, dest.Length);
            if (binding.FormulaMode) return EncodeFormula(binding.Formula!, row, dest, out reason);
            return EncodeFeatures(binding.Features!, row, dest, out reason);
        }

        static bool EncodeFormula(FormulaLayout layout, string[] row, double[] dest, out string? reason)
        {
            int kCount = layout.HeaderIndex.Length;
            var numbers = new double[kCount];
            var codes = new int[kCount];
            for (int k = 0; k < kCount; k++)
            {
                int col = layout.HeaderIndex[k];
                string raw = col < row.Length ? row[col] : "";
                if (layout.Categorical[k])
                {
                    if (col >= row.Length || StatValue.IsMissing(raw))
                    {
                        reason = $"missing value in '{ColumnName(layout, k)}'";
                        return false;
                    }
                    string value = raw.Trim();
                    int level = IndexOf(layout.Levels[k], value);
                    if (level < 0)
                    {
                        reason = $"unseen level '{value}' in '{ColumnName(layout, k)}'";
                        return false;
                    }
                    codes[k] = level;
                }
                else if (col >= row.Length || !StatValue.TryNumber(raw, out numbers[k]))
                {
                    reason = $"missing or non-numeric value in '{ColumnName(layout, k)}'";
                    return false;
                }
            }
            for (int j = 0; j < layout.Columns.Length; j++)
            {
                double value = 1;
                foreach (var (var, level) in layout.Columns[j])
                {
                    if (level < 0) value *= numbers[var];
                    else if (codes[var] != level) { value = 0; break; }
                }
                dest[j] = value;
            }
            reason = null;
            return true;
        }

        static bool EncodeFeatures(FeatureLayout layout, string[] row, double[] dest, out string? reason)
        {
            foreach (var group in layout.Groups)
            {
                string raw = group.Header >= 0 && group.Header < row.Length ? row[group.Header] : "";
                if (!group.Categorical)
                {
                    if (!StatValue.TryNumber(raw, out double number))
                    {
                        reason = $"missing or non-numeric value in '{group.Column}'";
                        return false;
                    }
                    dest[group.Indexes[0]] = number;
                    continue;
                }
                if (group.Header >= row.Length || StatValue.IsMissing(raw))
                {
                    reason = $"missing value in '{group.Column}'";
                    return false;
                }
                string value = raw.Trim();
                int match = -1;
                for (int i = 0; i < group.Levels.Count; i++)
                    if (string.Equals(group.Levels[i], value, StringComparison.Ordinal)) { match = i; break; }
                if (match < 0)
                {
                    reason = $"unseen level '{value}' in '{group.Column}'";
                    return false;
                }
                for (int i = 0; i < group.Indexes.Count; i++) dest[group.Indexes[i]] = i == match ? 1 : 0;
            }
            reason = null;
            return true;
        }

        static (int Var, int Level)[][] ExpandFormula(ModelFormula formula, IReadOnlyList<string> predictors, bool[] categorical, string[][] levels)
        {
            var specs = new List<(int Var, int Level)[]>();
            if (formula.Intercept) specs.Add(Array.Empty<(int, int)>());
            var varIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < predictors.Count; k++) varIndex[predictors[k]] = k;
            bool fullRankPending = !formula.Intercept;
            foreach (var term in formula.Terms)
            {
                if (!varIndex.TryGetValue(term.Variables[0], out int first))
                    throw new ModelStoreException($"Formula term '{term.Name}' is not in the predictor list.");
                bool fullRank = fullRankPending && term.Order == 1 && categorical[first];
                if (fullRank) fullRankPending = false;
                var combos = new List<(int, int)[]> { Array.Empty<(int, int)>() };
                foreach (string v in term.Variables)
                {
                    int k = varIndex[v];
                    var next = new List<(int, int)[]>();
                    foreach (var combo in combos)
                    {
                        if (!categorical[k]) next.Add(Append(combo, (k, -1)));
                        else
                        {
                            int start = fullRank ? 0 : 1;
                            for (int l = start; l < levels[k].Length; l++) next.Add(Append(combo, (k, l)));
                        }
                    }
                    combos = next;
                }
                specs.AddRange(combos);
            }
            return specs.ToArray();
        }

        static (int, int)[] Append((int, int)[] combo, (int, int) item)
        {
            var next = new (int, int)[combo.Length + 1];
            combo.CopyTo(next, 0);
            next[^1] = item;
            return next;
        }

        static EncodedPredictions PredictKnn(KnnModel knn, double[,] x, ModelBundle model, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            var cls = new int[n];
            var proba = new double[n, knn.ClassCount];
            knn.Predict(x, cls, proba, cancellation);
            return new EncodedPredictions { Count = n, ClassIndex = cls, Probability = proba, Value = EventProb(proba) };
        }

        static EncodedPredictions PredictBayes(NaiveBayesModel model, double[,] x, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            var cls = model.Predict(x, cancellation);
            var proba = new double[n, model.ClassCount];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var p = model.PredictProba(x, i);
                for (int c = 0; c < p.Length; c++) proba[i, c] = p[c];
            }
            return new EncodedPredictions { Count = n, ClassIndex = cls, Probability = proba, Value = EventProb(proba) };
        }

        static EncodedPredictions PredictLda(LinearDiscriminantModel model, double[,] x, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var cls = model.Predict(x);
            var proba = model.PredictProba(x);
            return new EncodedPredictions { Count = x.GetLength(0), ClassIndex = cls, Probability = proba, Value = EventProb(proba) };
        }

        static EncodedPredictions PredictMultinomial(MultinomialLogisticModel model, double[,] x, CancellationToken cancellation)
        {
            var proba = model.PredictProbabilities(x, cancellation);
            int n = proba.GetLength(0), k = proba.GetLength(1);
            var cls = new int[n];
            for (int i = 0; i < n; i++)
            {
                int best = 0;
                for (int c = 1; c < k; c++) if (proba[i, c] > proba[i, best]) best = c;
                cls[i] = best;
            }
            return new EncodedPredictions { Count = n, ClassIndex = cls, Probability = proba, Value = EventProb(proba) };
        }

        static EncodedPredictions PredictTree(DecisionTreeModel model, double[,] x, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            if (model.Regression)
            {
                var value = model.PredictValue(x, cancellation);
                return new EncodedPredictions { Count = n, Value = value };
            }
            var cls = new int[n];
            var proba = new double[n, model.ClassCount];
            for (int i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var leaf = WalkTree(model, x, i);
                cls[i] = leaf.Prediction;
                FillLeafProba(leaf, model.ClassCount, proba, i);
            }
            return new EncodedPredictions { Count = n, ClassIndex = cls, Probability = proba, Value = EventProb(proba) };
        }

        static EncodedPredictions PredictForest(RandomForestModel model, double[,] x, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            if (model.Regression)
                return new EncodedPredictions { Count = n, Value = model.PredictValue(x, cancellation) };
            var cls = new int[n];
            var proba = new double[n, model.ClassCount];
            var votes = new int[model.ClassCount];
            int trees = model.Trees.Length;
            for (int i = 0; i < n; i++)
            {
                if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                Array.Clear(votes, 0, votes.Length);
                for (int t = 0; t < trees; t++) votes[model.Trees[t].PredictClass(x, i)]++;
                int best = 0;
                for (int c = 1; c < votes.Length; c++) if (votes[c] > votes[best]) best = c;
                cls[i] = best;
                for (int c = 0; c < votes.Length; c++) proba[i, c] = trees == 0 ? double.NaN : votes[c] / (double)trees;
            }
            return new EncodedPredictions { Count = n, ClassIndex = cls, Probability = proba, Value = EventProb(proba) };
        }

        static EncodedPredictions PredictSvm(SvmModel model, double[,] x, ModelBundle bundle, CancellationToken cancellation)
        {
            var cls = model.Predict(x, cancellation);
            return new EncodedPredictions { Count = x.GetLength(0), ClassIndex = cls };
        }

        static EncodedPredictions PredictBoosting(GradientBoostingModel model, double[,] x, ModelBundle bundle, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            if (model.Task == BoostingTask.Regression)
                return new EncodedPredictions { Count = n, Value = model.PredictValues(x, cancellation) };
            var cls = model.PredictClasses(x, cancellation);
            var trees = BoostingTrees(model);
            int k = model.ClassCount;
            var proba = new double[n, k];
            var scores = new double[k];
            for (int i = 0; i < n; i++)
            {
                if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                if (model.Task == BoostingTask.Binary)
                {
                    double s = model.Baseline[0];
                    var seq = trees[0];
                    for (int t = 0; t < seq.Length; t++) s += WalkBoost(seq[t], x, i);
                    double p = Sigmoid(s);
                    proba[i, 0] = 1 - p;
                    if (k > 1) proba[i, 1] = p;
                }
                else
                {
                    for (int c = 0; c < k; c++)
                    {
                        double s = c < model.Baseline.Length ? model.Baseline[c] : 0;
                        var seq = trees[c];
                        for (int t = 0; t < seq.Length; t++) s += WalkBoost(seq[t], x, i);
                        scores[c] = s;
                    }
                    SoftmaxInto(scores, k, proba, i);
                }
            }
            return new EncodedPredictions { Count = n, ClassIndex = cls, Probability = proba, Value = EventProb(proba) };
        }

        static EncodedPredictions PredictGlm(GeneralizedLinearFit fit, double[,] x, ModelBundle model, double[]? offset, double[]? trials, CancellationToken cancellation)
        {
            // 시행 수 열을 쓴 이항 모형은 분류가 아니라 확률 + 기대 성공 횟수(시행 수 × 확률)를 낸다.
            bool logistic = fit.Family == GlmFamily.Binomial && model.TrialsColumn is null;
            var result = PredictLinear(fit.Coefficients, x, regression: !logistic, fit.Link, offset, cancellation);
            if (trials is null || result.Value is null) return result;
            var expected = new double[result.Count];
            for (int i = 0; i < expected.Length; i++) expected[i] = trials[i] * result.Value[i];
            return new EncodedPredictions { Count = result.Count, ClassIndex = result.ClassIndex, Value = result.Value, Probability = result.Probability, ExpectedSuccesses = expected };
        }

        /// <summary>행의 오프셋·시행 수를 읽어 (행 인덱스 기준) 배열에 쓴다. 지연 할당 — 모형에 해당 열이 없으면 아무것도 하지 않는다.</summary>
        static bool TryRowExtras(ModelBinding binding, string[] row, int i, ref double[]? offsets, ref double[]? trials, int count, out string? reason)
        {
            reason = null;
            var layout = binding.Extras;
            if (layout is null) return true;
            if (!TryExtras(layout, row, out double off, out double tr, out reason)) return false;
            if (layout.NeedsOffset) (offsets ??= new double[count])[i] = off;
            if (layout.TrialsCol >= 0) (trials ??= new double[count])[i] = tr;
            return true;
        }

        static EncodedPredictions PredictAda(AdaBoostModel model, double[,] x, CancellationToken cancellation)
        {
            int n = x.GetLength(0);
            if (model.Regression)
                return new EncodedPredictions { Count = n, Value = model.PredictValue(x, cancellation) };
            return new EncodedPredictions { Count = n, ClassIndex = model.Predict(x, cancellation) };
        }

        static EncodedPredictions PredictScore(LinearScoreModel model, double[,] x, CancellationToken cancellation)
        {
            var value = model.PredictValues(x, cancellation);
            if (!model.Logistic)
                return new EncodedPredictions { Count = value.Length, Value = value };
            var cls = model.PredictClasses(x, cancellation);
            var proba = new double[value.Length, 2];
            for (int i = 0; i < value.Length; i++)
            {
                proba[i, 1] = value[i];
                proba[i, 0] = 1 - value[i];
            }
            return new EncodedPredictions { Count = value.Length, ClassIndex = cls, Value = value, Probability = proba };
        }


        static EncodedPredictions PredictLinear(double[] beta, double[,] x, bool regression, GlmLink? link, double[]? offset, CancellationToken cancellation)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (beta.Length != p) throw new ModelStoreException($"Coefficient count {beta.Length} does not match features ({p}).");
            var value = new double[n];
            int[]? cls = regression ? null : new int[n];
            double[,]? proba = regression ? null : new double[n, 2];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                double eta = 0;
                for (int j = 0; j < p; j++)
                    if (!double.IsNaN(beta[j])) eta += x[i, j] * beta[j];
                if (offset != null) eta += offset[i];
                double mu = link is null ? eta : InverseLink(eta, link.Value);
                value[i] = mu;
                if (!regression && proba != null && cls != null)
                {
                    proba[i, 1] = mu;
                    proba[i, 0] = 1 - mu;
                    cls[i] = mu >= GeneralizedLinearModel.LogisticThreshold ? 1 : 0;
                }
            }
            return new EncodedPredictions { Count = n, ClassIndex = cls, Value = value, Probability = proba };
        }

        static double InverseLink(double eta, GlmLink link) => link switch
        {
            GlmLink.Identity => eta,
            GlmLink.Log => Math.Exp(eta),
            GlmLink.Inverse => 1.0 / eta,
            GlmLink.Sqrt => eta * eta,
            GlmLink.Logit => Sigmoid(eta),
            GlmLink.Probit => Dist.NormalCdf(eta),
            GlmLink.CLogLog => 1.0 - Math.Exp(-Math.Exp(eta)),
            _ => double.NaN,
        };

        static double Sigmoid(double eta)
        {
            double t = Math.Exp(-eta);
            return 1.0 / (1.0 + t);
        }

        static void SoftmaxInto(double[] scores, int k, double[,] dest, int row)
        {
            double max = double.NegativeInfinity;
            for (int c = 0; c < k; c++) if (scores[c] > max) max = scores[c];
            double sum = 0;
            for (int c = 0; c < k; c++)
            {
                double e = Math.Exp(scores[c] - max);
                dest[row, c] = e;
                sum += e;
            }
            if (sum == 0) { for (int c = 0; c < k; c++) dest[row, c] = double.NaN; return; }
            for (int c = 0; c < k; c++) dest[row, c] /= sum;
        }

        static Node WalkTree(DecisionTreeModel model, double[,] x, int row)
        {
            var node = model.Root;
            while (node.Feature >= 0)
            {
                double v = x[row, node.Feature];
                node = (double.IsNaN(v) || v > node.Threshold ? node.Right : node.Left) ?? node;
                if (node.Feature < 0) break;
            }
            return node;
        }

        static void FillLeafProba(Node leaf, int classCount, double[,] dest, int row)
        {
            if (leaf.ClassCounts is { } counts && counts.Length == classCount)
            {
                double n = leaf.Count > 0 ? leaf.Count : counts.Sum();
                for (int c = 0; c < classCount; c++) dest[row, c] = n == 0 ? double.NaN : counts[c] / n;
                return;
            }
            for (int c = 0; c < classCount; c++) dest[row, c] = c == leaf.Prediction ? 1 : 0;
        }

        static double WalkBoost(GradientBoostingModel.PredNode[] tree, double[,] x, int row)
        {
            int node = 0;
            while (true)
            {
                var nd = tree[node];
                if (nd.Leaf) return nd.Value;
                double v = x[row, nd.Feature];
                node = double.IsNaN(v)
                    ? (nd.MissingLeft ? nd.Left : nd.Right)
                    : (v <= nd.Threshold ? nd.Left : nd.Right);
            }
        }

        static double[] EventProb(double[,] proba)
        {
            int n = proba.GetLength(0), k = proba.GetLength(1);
            var v = new double[n];
            int col = k == 2 ? 1 : 0;
            for (int i = 0; i < n; i++) v[i] = k == 0 ? double.NaN : proba[i, Math.Min(col, k - 1)];
            return v;
        }

        static JsonElement WriteEngine(ModelBundle bundle) => bundle.Engine switch
        {
            KnnModel m => ToElement(new EngineDto { Type = ModelTypes.Knn, Knn = WriteKnn(m) }),
            NaiveBayesModel m => ToElement(new EngineDto { Type = ModelTypes.NaiveBayes, Bayes = WriteBayes(m) }),
            LinearDiscriminantModel m => ToElement(new EngineDto { Type = ModelTypes.Lda, Lda = WriteLda(m) }),
            DecisionTreeModel m => ToElement(new EngineDto { Type = ModelTypes.DecisionTree, Tree = WriteTree(m) }),
            RandomForestModel m => ToElement(new EngineDto { Type = ModelTypes.RandomForest, Forest = WriteForest(m) }),
            SvmModel m => ToElement(new EngineDto { Type = ModelTypes.Svm, Svm = WriteSvm(m) }),
            GradientBoostingModel m => ToElement(new EngineDto { Type = ModelTypes.GradientBoosting, Boosting = WriteBoosting(m) }),
            LinearModelFit m => ToElement(new EngineDto { Type = ModelTypes.LinearModel, Linear = WriteLinear(m) }),
            GeneralizedLinearFit m => ToElement(new EngineDto { Type = bundle.ModelType, Glm = WriteGlm(m) }),
            AdaBoostModel m => ToElement(new EngineDto { Type = ModelTypes.AdaBoost, Ada = WriteAda(m) }),
            MultinomialLogisticModel m => ToElement(new EngineDto { Type = ModelTypes.MultinomialLogistic, Multinomial = WriteMultinomial(m) }),
            LinearScoreModel m => ToElement(new EngineDto { Type = bundle.ModelType, LinearScore = WriteScore(m) }),
            _ => throw new ModelStoreException($"'{bundle.ModelType}' has no save adapter (engine {bundle.Engine?.GetType().Name ?? "null"})."),
        };

        static object ReadEngine(string modelType, JsonElement engine, int featureCount, ModelTask task)
        {
            if (engine.ValueKind != JsonValueKind.Object) throw new ModelStoreException("The model file has no engine.");
            string type = engine.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            if (!string.Equals(type, modelType, StringComparison.Ordinal))
                throw new ModelStoreException($"Engine type '{type}' does not match model type '{modelType}'.");
            return modelType switch
            {
                ModelTypes.Knn => ReadKnn(Require(engine, "knn"), featureCount),
                ModelTypes.NaiveBayes => ReadBayes(Require(engine, "bayes")),
                ModelTypes.Lda => ReadLda(Require(engine, "lda")),
                ModelTypes.DecisionTree => ReadTree(Require(engine, "tree")),
                ModelTypes.RandomForest => ReadForest(Require(engine, "forest")),
                ModelTypes.Svm => ReadSvm(Require(engine, "svm"), featureCount),
                ModelTypes.GradientBoosting => ReadBoosting(Require(engine, "boosting"), featureCount),
                ModelTypes.LinearModel => Has(engine, "linearScore") ? ReadScore(Require(engine, "linearScore"), featureCount) : ReadLinear(Require(engine, "linear"), featureCount),
                ModelTypes.Logistic => Has(engine, "linearScore") ? ReadScore(Require(engine, "linearScore"), featureCount) : Has(engine, "glm") ? ReadGlm(Require(engine, "glm"), featureCount) : throw new ModelStoreException("The model file is missing engine.glm or engine.linearScore."),
                ModelTypes.Glzm => ReadGlm(Require(engine, "glm"), featureCount),
                ModelTypes.AdaBoost => ReadAda(Require(engine, "ada"), featureCount),
                ModelTypes.MultinomialLogistic => ReadMultinomial(Require(engine, "multinomial"), featureCount),
                _ => throw new ModelStoreException($"Model type '{modelType}' cannot be loaded."),
            };
        }

        static JsonElement Require(JsonElement engine, string name)
        {
            if (!engine.TryGetProperty(name, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new ModelStoreException($"The model file is missing engine.{name}.");
            return el;
        }

        static bool Has(JsonElement engine, string name)
            => engine.TryGetProperty(name, out var el) && el.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;


        static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOpt);

        static T Parse<T>(JsonElement el)
        {
            try { return el.Deserialize<T>(JsonOpt) ?? throw new ModelStoreException("An engine section is empty."); }
            catch (ModelStoreException) { throw; }
            catch (JsonException ex) { throw new ModelStoreException("An engine section is invalid: " + ex.Message); }
        }

        static ScalerDto? WriteScaler(FeatureScaler? scaler)
        {
            if (scaler is null || scaler.Method == ScalingMethod.None) return null;
            return new ScalerDto { Method = scaler.Method.ToString(), Center = Pack(scaler.Center), Scale = Pack(scaler.Scale) };
        }

        static FeatureScaler? ReadScaler(ScalerDto? dto, int featureCount)
        {
            if (dto is null) return null;
            if (!Enum.TryParse<ScalingMethod>(dto.Method, out var method) || method == ScalingMethod.None)
                throw new ModelStoreException($"Unknown scaler method '{dto.Method}'.");
            var center = Unpack(dto.Center, featureCount, "scaler center");
            var scale = Unpack(dto.Scale, featureCount, "scaler scale");
            return (FeatureScaler)ScalerCtor.Invoke(new object[] { method, center, scale });
        }

        static KnnDto WriteKnn(KnnModel m) => new()
        {
            Neighbors = m.Neighbors,
            ClassCount = m.ClassCount,
            Rows = m.TrainX.GetLength(0),
            Cols = m.TrainX.GetLength(1),
            TrainX = Pack(m.TrainX),
            Labels = m.Labels,
        };

        static KnnModel ReadKnn(JsonElement el, int featureCount)
        {
            var dto = Parse<KnnDto>(el);
            if (dto.Cols != featureCount) throw new ModelStoreException("Stored KNN feature count does not match the feature list.");
            if (dto.Labels is null || dto.Labels.Length != dto.Rows) throw new ModelStoreException("Stored KNN labels do not match the training row count.");
            return new KnnModel
            {
                TrainX = Unpack(dto.TrainX, dto.Rows, dto.Cols, "KNN training matrix"),
                Labels = dto.Labels,
                ClassCount = dto.ClassCount,
                Neighbors = dto.Neighbors,
            };
        }

        static BayesDto WriteBayes(NaiveBayesModel m) => new()
        {
            ClassCount = m.ClassCount,
            ClassCounts = m.ClassCounts,
            Prior = Pack(m.Prior),
            LogPrior = Pack(m.LogPrior),
            Epsilon = m.Epsilon,
            VarSmoothing = m.VarSmoothing,
            Alpha = m.Alpha,
            NumericColumns = m.NumericColumns,
            MeanRows = m.Mean.GetLength(0),
            MeanCols = m.Mean.GetLength(1),
            Mean = Pack(m.Mean),
            VarRows = m.Variance.GetLength(0),
            VarCols = m.Variance.GetLength(1),
            Variance = Pack(m.Variance),
            Categorical = m.Categorical.Select(f => new CatDto
            {
                SourceColumn = f.SourceColumn,
                Columns = f.Columns,
                CountRows = f.Counts.GetLength(0),
                CountCols = f.Counts.GetLength(1),
                Counts = PackLongs(f.Counts),
                LogRows = f.LogProb.GetLength(0),
                LogCols = f.LogProb.GetLength(1),
                LogProb = Pack(f.LogProb),
            }).ToArray(),
        };

        static NaiveBayesModel ReadBayes(JsonElement el)
        {
            var dto = Parse<BayesDto>(el);
            var cat = new CategoricalFeature[dto.Categorical?.Length ?? 0];
            for (int i = 0; i < cat.Length; i++)
            {
                var f = dto.Categorical![i];
                cat[i] = new CategoricalFeature(f.SourceColumn, f.Columns ?? Array.Empty<int>(),
                    UnpackLongs(f.Counts, f.CountRows, f.CountCols, "categorical counts"),
                    Unpack(f.LogProb, f.LogRows, f.LogCols, "categorical log-probability"));
            }
            return (NaiveBayesModel)BayesCtor.Invoke(new object[]
            {
                dto.ClassCount,
                dto.ClassCounts ?? Array.Empty<int>(),
                Unpack(dto.Prior, dto.ClassCount, "prior"),
                Unpack(dto.LogPrior, dto.ClassCount, "log prior"),
                dto.Epsilon, dto.VarSmoothing, dto.Alpha,
                dto.NumericColumns ?? Array.Empty<int>(),
                Unpack(dto.Mean, dto.MeanRows, dto.MeanCols, "mean"),
                Unpack(dto.Variance, dto.VarRows, dto.VarCols, "variance"),
                cat,
            });
        }

        static LdaDto WriteLda(LinearDiscriminantModel m) => new()
        {
            ClassCount = m.ClassCount,
            FeatureCount = m.FeatureCount,
            ClassNames = m.ClassNames.ToArray(),
            FeatureNames = m.FeatureNames.ToArray(),
            Priors = Pack(m.Priors),
            ClassCounts = m.ClassCounts,
            MeansRows = m.ClassMeans.GetLength(0),
            MeansCols = m.ClassMeans.GetLength(1),
            ClassMeans = Pack(m.ClassMeans),
            ExplainedVarianceRatio = Pack(m.ExplainedVarianceRatio),
            DirRows = m.Directions.GetLength(0),
            DirCols = m.Directions.GetLength(1),
            Directions = Pack(m.Directions),
            UsedFisherDirections = m.UsedFisherDirections,
            WithinRank = m.WithinRank,
            Tolerance = m.Tolerance,
            CoefRows = m.Coef.GetLength(0),
            CoefCols = m.Coef.GetLength(1),
            Coef = Pack(m.Coef),
            Intercept = Pack(m.Intercept),
        };

        static LinearDiscriminantModel ReadLda(JsonElement el)
        {
            var dto = Parse<LdaDto>(el);
            return new LinearDiscriminantModel
            {
                ClassCount = dto.ClassCount,
                FeatureCount = dto.FeatureCount,
                ClassNames = dto.ClassNames ?? Array.Empty<string>(),
                FeatureNames = dto.FeatureNames ?? Array.Empty<string>(),
                Priors = Unpack(dto.Priors, dto.ClassCount, "LDA priors"),
                ClassCounts = dto.ClassCounts ?? Array.Empty<int>(),
                ClassMeans = Unpack(dto.ClassMeans, dto.MeansRows, dto.MeansCols, "LDA means"),
                ExplainedVarianceRatio = Unpack(dto.ExplainedVarianceRatio, dto.ExplainedVarianceRatio is null ? 0 : CountPacked(dto.ExplainedVarianceRatio), "LDA explained variance"),
                Directions = Unpack(dto.Directions, dto.DirRows, dto.DirCols, "LDA directions"),
                UsedFisherDirections = dto.UsedFisherDirections,
                WithinRank = dto.WithinRank,
                Tolerance = dto.Tolerance,
                Coef = Unpack(dto.Coef, dto.CoefRows, dto.CoefCols, "LDA coefficients"),
                Intercept = Unpack(dto.Intercept, dto.ClassCount, "LDA intercept"),
            };
        }

        static int CountPacked(string? b64)
        {
            if (string.IsNullOrEmpty(b64)) return 0;
            if ((long)b64.Length > (MaxPayloadBytes / 3 + 1) * 4)
                throw new ModelStoreException($"A stored numeric payload is over the {MaxPayloadBytes.ToString("N0", CultureInfo.InvariantCulture)} byte limit. The model was not loaded.");
            int bytes;
            try { bytes = Convert.FromBase64String(b64).Length; }
            catch (FormatException) { throw new ModelStoreException("A stored numeric payload is not valid base64."); }
            if (bytes % 8 != 0) throw new ModelStoreException("A stored numeric payload is not a multiple of 8 bytes.");
            return RequireElements(bytes / 8, "numeric payload");
        }

        static TreeDto WriteTree(DecisionTreeModel m) => new()
        {
            FeatureCount = m.FeatureCount,
            ClassCount = m.ClassCount,
            Regression = m.Regression,
            Criterion = m.Criterion.ToString(),
            Importances = Pack(m.Importances),
            Depth = m.Depth,
            LeafCount = m.LeafCount,
            NodeCount = m.NodeCount,
            ApproximateSplits = m.ApproximateSplits,
            BinCount = m.BinCount,
            Root = WriteNode(m.Root),
        };

        static NodeDto WriteNode(Node n) => new()
        {
            Feature = n.Feature,
            Threshold = n.Threshold,
            Impurity = n.Impurity,
            Count = n.Count,
            Prediction = n.Prediction,
            Value = n.Value,
            ClassCounts = n.ClassCounts,
            Left = n.Left is null ? null : WriteNode(n.Left),
            Right = n.Right is null ? null : WriteNode(n.Right),
        };

        static DecisionTreeModel ReadTree(JsonElement el)
        {
            var dto = Parse<TreeDto>(el);
            if (!Enum.TryParse<TreeCriterion>(dto.Criterion, out var criterion))
                throw new ModelStoreException($"Unknown tree criterion '{dto.Criterion}'.");
            var model = new DecisionTreeModel
            {
                Root = ReadNode(dto.Root, new int[1]),
                FeatureCount = dto.FeatureCount,
                ClassCount = dto.ClassCount,
                Regression = dto.Regression,
                Criterion = criterion,
                Importances = Unpack(dto.Importances, dto.FeatureCount, "tree importances"),
                Depth = dto.Depth,
                LeafCount = dto.LeafCount,
                NodeCount = dto.NodeCount,
            };
            model.ApproximateSplits = dto.ApproximateSplits;
            model.BinCount = dto.BinCount;
            return model;
        }

        static Node ReadNode(NodeDto? dto, int[] seen, int depth = 0)
        {
            if (dto is null) throw new ModelStoreException("A tree node is missing.");
            if (depth > MaxTreeDepth)
                throw new ModelStoreException($"A stored tree is deeper than {MaxTreeDepth}. The model was not loaded.");
            if (++seen[0] > MaxNodeCount)
                throw new ModelStoreException($"A stored tree has more than {MaxNodeCount:N0} nodes. The model was not loaded.");
            if (dto.ClassCounts != null && dto.ClassCounts.Length > 100_000)
                throw new ModelStoreException("A stored tree leaf has too many class counts. The model was not loaded.");
            return new Node
            {
                Feature = dto.Feature,
                Threshold = dto.Threshold,
                Impurity = dto.Impurity,
                Count = dto.Count,
                Prediction = dto.Prediction,
                Value = dto.Value,
                ClassCounts = dto.ClassCounts,
                Left = dto.Left is null ? null : ReadNode(dto.Left, seen, depth + 1),
                Right = dto.Right is null ? null : ReadNode(dto.Right, seen, depth + 1),
            };
        }

        static ForestDto WriteForest(RandomForestModel m) => new()
        {
            FeatureCount = m.FeatureCount,
            ClassCount = m.ClassCount,
            Regression = m.Regression,
            Importances = Pack(m.Importances),
            OobScore = m.OobScore,
            OobError = m.OobError,
            OobRows = m.OobRows,
            TrainingRows = m.TrainingRows,
            SourceRows = m.SourceRows,
            Sampled = m.Sampled,
            ApproximateSplits = m.ApproximateSplits,
            BinCount = m.BinCount,
            Seed = m.Seed,
            MaxFeatures = m.MaxFeatures,
            TreeCount = m.TreeCount,
            Trees = m.Trees.Select(WriteTree).ToArray(),
        };

        static RandomForestModel ReadForest(JsonElement el)
        {
            var dto = Parse<ForestDto>(el);
            var trees = (dto.Trees ?? Array.Empty<TreeDto>()).Select(t => ReadTree(JsonSerializer.SerializeToElement(t, JsonOpt))).ToArray();
            if (trees.Length != dto.TreeCount) throw new ModelStoreException("Stored forest tree count does not match the tree list.");
            return new RandomForestModel
            {
                Trees = trees,
                FeatureCount = dto.FeatureCount,
                ClassCount = dto.ClassCount,
                Regression = dto.Regression,
                Importances = Unpack(dto.Importances, dto.FeatureCount, "forest importances"),
                OobScore = dto.OobScore,
                OobError = dto.OobError,
                OobRows = dto.OobRows,
                TrainingRows = dto.TrainingRows,
                SourceRows = dto.SourceRows,
                Sampled = dto.Sampled,
                ApproximateSplits = dto.ApproximateSplits,
                BinCount = dto.BinCount,
                Seed = dto.Seed,
                MaxFeatures = dto.MaxFeatures,
                TreeCount = dto.TreeCount,
            };
        }

        static SvmDto WriteSvm(SvmModel m)
        {
            int n = m.X.GetLength(0), p = m.X.GetLength(1);
            return new SvmDto
            {
                Rows = n,
                Cols = p,
                X = Pack(m.X),
                Labels = m.Labels,
                ClassCount = m.ClassCount,
                Kernel = m.Kernel.ToString(),
                Gamma = m.Gamma,
                C = m.C,
                Pairs = m.Pairs.Select(pair => new PairDto
                {
                    Rows = pair.Rows,
                    Y = pair.Y.Select(v => (int)v).ToArray(),
                    Alpha = Pack(pair.Alpha),
                    Rho = pair.Rho,
                    Iterations = pair.Iterations,
                    Converged = pair.Converged,
                }).ToArray(),
                LinearWeights = m.LinearWeights?.Select(w => w is null ? null : Pack(w)).ToArray(),
                LinearBias = m.LinearBias is null ? null : Pack(m.LinearBias),
                SupportPerClass = m.SupportPerClass,
                Converged = m.Converged,
                Iterations = m.Iterations,
                Solver = m.Solver,
                Multiclass = m.Multiclass,
                Sampled = m.Sampled,
                RowsPresented = m.RowsPresented,
                RowsUsed = m.RowsUsed,
            };
        }

        static SvmModel ReadSvm(JsonElement el, int featureCount)
        {
            var dto = Parse<SvmDto>(el);
            if (dto.Cols != featureCount) throw new ModelStoreException("Stored SVM feature count does not match the feature list.");
            if (!Enum.TryParse<SvmKernel>(dto.Kernel, out var kernel))
                throw new ModelStoreException($"Unknown SVM kernel '{dto.Kernel}'.");
            var pairs = (dto.Pairs ?? Array.Empty<PairDto>()).Select(pair =>
            {
                var y = (pair.Y ?? Array.Empty<int>()).Select(v => (sbyte)v).ToArray();
                return new BinarySvm(pair.Rows ?? Array.Empty<int>(), y, Unpack(pair.Alpha, y.Length, "SVM alpha"), pair.Rho, pair.Iterations, pair.Converged);
            }).ToArray();
            double[][]? weights = dto.LinearWeights?.Select(w => w is null ? null! : Unpack(w, featureCount, "SVM weight")).ToArray();
            return new SvmModel
            {
                X = Unpack(dto.X, dto.Rows, dto.Cols, "SVM training matrix"),
                Labels = dto.Labels ?? Array.Empty<int>(),
                ClassCount = dto.ClassCount,
                Kernel = kernel,
                Gamma = dto.Gamma,
                C = dto.C,
                Pairs = pairs,
                LinearWeights = weights,
                LinearBias = dto.LinearBias is null ? null : Unpack(dto.LinearBias, weights?.Length ?? dto.ClassCount, "SVM bias"),
                SupportPerClass = dto.SupportPerClass ?? Array.Empty<int>(),
                Converged = dto.Converged,
                Iterations = dto.Iterations,
                Solver = dto.Solver ?? "",
                Multiclass = dto.Multiclass ?? "",
                Sampled = dto.Sampled,
                RowsPresented = dto.RowsPresented,
                RowsUsed = dto.RowsUsed,
            };
        }

        static BoostDto WriteBoosting(GradientBoostingModel m)
        {
            var trees = BoostingTrees(m);
            var thresholds = (double[][])typeof(GradientBoostingModel).GetField("_thresholds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!;
            return new BoostDto
            {
                Task = m.Task.ToString(),
                Loss = m.Loss.ToString(),
                ClassCount = m.ClassCount,
                FeatureCount = m.FeatureCount,
                Baseline = Pack(m.Baseline),
                Thresholds = thresholds.Select(Pack).ToArray(),
                BinsPerFeature = m.BinsPerFeature,
                Gain = Pack(m.Gain),
                IterationsUsed = m.IterationsUsed,
                MaxIterations = m.MaxIterations,
                EarlyStopped = m.EarlyStopped,
                TrainLoss = Pack(m.TrainLoss),
                ValidationLoss = m.ValidationLoss is null ? null : Pack(m.ValidationLoss),
                BestValidationIndex = m.BestValidationIndex,
                BinEdgesSubsampled = m.BinEdgesSubsampled,
                BinEdgeRows = m.BinEdgeRows,
                RowsFit = m.RowsFit,
                RowsValidation = m.RowsValidation,
                LearningRate = m.LearningRate,
                MaxDepth = m.MaxDepth,
                MaxLeafNodes = m.MaxLeafNodes,
                MinSamplesLeaf = m.MinSamplesLeaf,
                L2Regularization = m.L2Regularization,
                MaxBins = m.MaxBins,
                Seed = m.Seed,
                EarlyStopping = m.EarlyStopping,
                ValidationFraction = m.ValidationFraction,
                IterationsNoChange = m.IterationsNoChange,
                EarlyStoppingTolerance = m.EarlyStoppingTolerance,
                Trees = trees.Select(cls => cls.Select(tree => tree.Select(n => new PredDto
                {
                    Leaf = n.Leaf, Feature = n.Feature, Threshold = n.Threshold, Value = n.Value,
                    Left = n.Left, Right = n.Right, MissingLeft = n.MissingLeft, Gain = n.Gain,
                }).ToArray()).ToArray()).ToArray(),
            };
        }

        static GradientBoostingModel ReadBoosting(JsonElement el, int featureCount)
        {
            var dto = Parse<BoostDto>(el);
            if (dto.FeatureCount != featureCount) throw new ModelStoreException("Stored boosting feature count does not match the feature list.");
            if (!Enum.TryParse<BoostingTask>(dto.Task, out var task)) throw new ModelStoreException($"Unknown boosting task '{dto.Task}'.");
            if (!Enum.TryParse<BoostingLoss>(dto.Loss, out var loss)) throw new ModelStoreException($"Unknown boosting loss '{dto.Loss}'.");
            var trees = (dto.Trees ?? Array.Empty<PredDto[][]>()).Select(cls => cls.Select(tree => tree.Select(n => new GradientBoostingModel.PredNode
            {
                Leaf = n.Leaf, Feature = n.Feature, Threshold = n.Threshold, Value = n.Value,
                Left = n.Left, Right = n.Right, MissingLeft = n.MissingLeft, Gain = n.Gain,
            }).ToArray()).ToArray()).ToArray();
            var thresholds = (dto.Thresholds ?? Array.Empty<string>()).Select((b, i) => Unpack(b, CountPacked(b), "boosting thresholds")).ToArray();
            var options = new GradientBoostingOptions
            {
                MaxIterations = dto.MaxIterations,
                LearningRate = dto.LearningRate,
                MaxDepth = dto.MaxDepth,
                MaxLeafNodes = dto.MaxLeafNodes,
                MinSamplesLeaf = dto.MinSamplesLeaf,
                L2Regularization = dto.L2Regularization,
                MaxBins = dto.MaxBins,
                EarlyStopping = dto.EarlyStopping,
                ValidationFraction = dto.ValidationFraction,
                IterationsNoChange = dto.IterationsNoChange,
                EarlyStoppingTolerance = dto.EarlyStoppingTolerance,
                Seed = dto.Seed,
            };
            int gainCount = dto.Gain is null ? featureCount : CountPacked(dto.Gain);
            return new GradientBoostingModel(
                task, loss, dto.ClassCount, dto.FeatureCount,
                Unpack(dto.Baseline, CountPacked(dto.Baseline ?? ""), "boosting baseline"),
                trees, thresholds, dto.BinsPerFeature ?? Array.Empty<int>(),
                Unpack(dto.Gain, gainCount, "boosting gain"),
                dto.IterationsUsed, dto.MaxIterations, dto.EarlyStopped,
                Unpack(dto.TrainLoss, dto.TrainLoss is null ? 0 : CountPacked(dto.TrainLoss), "boosting train loss"),
                dto.ValidationLoss is null ? null : Unpack(dto.ValidationLoss, CountPacked(dto.ValidationLoss), "boosting validation loss"),
                dto.BestValidationIndex, dto.BinEdgesSubsampled, dto.BinEdgeRows, dto.RowsFit, dto.RowsValidation, options);
        }

        static LinearDto WriteLinear(LinearModelFit m) => new()
        {
            Names = m.Coefficients.Select(c => c.Name).ToArray(),
            Estimates = Pack(m.Coefficients.Select(c => c.Estimate).ToArray()),
            StdErrors = Pack(m.Coefficients.Select(c => c.StdError).ToArray()),
            T = Pack(m.Coefficients.Select(c => c.T).ToArray()),
            PValues = Pack(m.Coefficients.Select(c => c.PValue).ToArray()),
            CiLow = Pack(m.Coefficients.Select(c => c.CiLow).ToArray()),
            CiHigh = Pack(m.Coefficients.Select(c => c.CiHigh).ToArray()),
            CoefAliased = m.Coefficients.Select(c => c.Aliased).ToArray(),
            Beta = Pack(m.Beta),
            XtXRows = m.XtXInverse.GetLength(0),
            XtXCols = m.XtXInverse.GetLength(1),
            XtXInverse = Pack(m.XtXInverse),
            Aliased = m.Aliased,
            ResidualMin = m.Residuals.Min,
            ResidualQ1 = m.Residuals.Q1,
            ResidualMedian = m.Residuals.Median,
            ResidualQ3 = m.Residuals.Q3,
            ResidualMax = m.Residuals.Max,
            ResidualMean = m.Residuals.Mean,
            NormalityW = m.Normality?.W,
            NormalityP = m.Normality?.PValue,
            NormalityN = m.Normality?.SampleSize,
            NormalityCapped = m.Normality?.Capped,
            N = m.N,
            Rank = m.Rank,
            DfResidual = m.DfResidual,
            DfModel = m.DfModel,
            HasIntercept = m.HasIntercept,
            Rss = m.Rss,
            Sigma2 = m.Sigma2,
            ResidualSe = m.ResidualSe,
            RSquared = m.RSquared,
            AdjustedRSquared = m.AdjustedRSquared,
            FStatistic = m.FStatistic,
            FPValue = m.FPValue,
            LogLikelihood = m.LogLikelihood,
            Aic = m.Aic,
            Bic = m.Bic,
        };

        static LinearModelFit ReadLinear(JsonElement el, int featureCount)
        {
            var dto = Parse<LinearDto>(el);
            int p = dto.Names?.Length ?? 0;
            if (p != featureCount) throw new ModelStoreException("Stored linear coefficient count does not match the feature list.");
            var est = Unpack(dto.Estimates, p, "estimates");
            var se = Unpack(dto.StdErrors, p, "std errors");
            var t = Unpack(dto.T, p, "t");
            var pv = Unpack(dto.PValues, p, "p");
            var lo = Unpack(dto.CiLow, p, "ci low");
            var hi = Unpack(dto.CiHigh, p, "ci high");
            var aliasedCoef = dto.CoefAliased ?? new bool[p];
            var coefs = new Coefficient[p];
            for (int j = 0; j < p; j++)
                coefs[j] = new Coefficient(dto.Names![j], est[j], se[j], t[j], pv[j], lo[j], hi[j], aliasedCoef[j]);
            return new LinearModelFit
            {
                Coefficients = coefs,
                Beta = Unpack(dto.Beta, p, "beta"),
                XtXInverse = Unpack(dto.XtXInverse, dto.XtXRows, dto.XtXCols, "XtX inverse"),
                Aliased = dto.Aliased ?? new bool[p],
                Residuals = new ResidualSummary(dto.ResidualMin, dto.ResidualQ1, dto.ResidualMedian, dto.ResidualQ3, dto.ResidualMax, dto.ResidualMean),
                Normality = dto.NormalityW is null ? null : new ResidualNormality(dto.NormalityW.Value, dto.NormalityP ?? double.NaN, dto.NormalityN ?? 0, dto.NormalityCapped ?? false),
                N = dto.N,
                Rank = dto.Rank,
                DfResidual = dto.DfResidual,
                DfModel = dto.DfModel,
                HasIntercept = dto.HasIntercept,
                Rss = dto.Rss,
                Sigma2 = dto.Sigma2,
                ResidualSe = dto.ResidualSe,
                RSquared = dto.RSquared,
                AdjustedRSquared = dto.AdjustedRSquared,
                FStatistic = dto.FStatistic,
                FPValue = dto.FPValue,
                LogLikelihood = dto.LogLikelihood,
                Aic = dto.Aic,
                Bic = dto.Bic,
            };
        }

        static GlmDto WriteGlm(GeneralizedLinearFit m) => new()
        {
            Family = m.Family.ToString(),
            Link = m.Link.ToString(),
            Names = m.Names,
            Coefficients = Pack(m.Coefficients),
            StdErrors = Pack(m.StdErrors),
            Z = Pack(m.Z),
            PValues = Pack(m.PValues),
            CiLow = Pack(m.CiLow),
            CiHigh = Pack(m.CiHigh),
            Aliased = m.Aliased,
            Iterations = m.Iterations,
            Converged = m.Converged,
            Scale = m.Scale,
            Deviance = m.Deviance,
            NullDeviance = m.NullDeviance,
            PearsonChi2 = m.PearsonChi2,
            LogLikelihood = m.LogLikelihood,
            NullLogLikelihood = m.NullLogLikelihood,
            Aic = m.Aic,
            Bic = m.Bic,
            LikelihoodRatio = m.LikelihoodRatio,
            LikelihoodRatioP = m.LikelihoodRatioP,
            DfModel = m.DfModel,
            DfResid = m.DfResid,
            Rank = m.Rank,
            N = m.N,
            HasIntercept = m.HasIntercept,
            Diagnostics = m.Diagnostics.Select(d => d.ToString()).ToArray(),
        };

        static GeneralizedLinearFit ReadGlm(JsonElement el, int featureCount)
        {
            var dto = Parse<GlmDto>(el);
            if (!Enum.TryParse<GlmFamily>(dto.Family, out var family)) throw new ModelStoreException($"Unknown GLM family '{dto.Family}'.");
            if (!Enum.TryParse<GlmLink>(dto.Link, out var link)) throw new ModelStoreException($"Unknown GLM link '{dto.Link}'.");
            int p = dto.Names?.Length ?? 0;
            if (p != featureCount) throw new ModelStoreException("Stored GLM coefficient count does not match the feature list.");
            var diagnostics = (dto.Diagnostics ?? Array.Empty<string>()).Select(name =>
            {
                if (!Enum.TryParse<GlmDiagnostic>(name, out var d))
                    throw new ModelStoreException($"Unknown GLM diagnostic '{name}'.");
                return d;
            }).ToArray();
            return new GeneralizedLinearFit
            {
                Family = family,
                Link = link,
                Names = dto.Names ?? Array.Empty<string>(),
                Coefficients = Unpack(dto.Coefficients, p, "GLM coefficients"),
                StdErrors = Unpack(dto.StdErrors, p, "GLM std errors"),
                Z = Unpack(dto.Z, p, "GLM z"),
                PValues = Unpack(dto.PValues, p, "GLM p"),
                CiLow = Unpack(dto.CiLow, p, "GLM ci low"),
                CiHigh = Unpack(dto.CiHigh, p, "GLM ci high"),
                Aliased = dto.Aliased ?? new bool[p],
                Fitted = Array.Empty<double>(),
                LinearPredictor = Array.Empty<double>(),
                Iterations = dto.Iterations,
                Converged = dto.Converged,
                Scale = dto.Scale,
                Deviance = dto.Deviance,
                NullDeviance = dto.NullDeviance,
                PearsonChi2 = dto.PearsonChi2,
                LogLikelihood = dto.LogLikelihood,
                NullLogLikelihood = dto.NullLogLikelihood,
                Aic = dto.Aic,
                Bic = dto.Bic,
                LikelihoodRatio = dto.LikelihoodRatio,
                LikelihoodRatioP = dto.LikelihoodRatioP,
                DfModel = dto.DfModel,
                DfResid = dto.DfResid,
                Rank = dto.Rank,
                N = dto.N,
                HasIntercept = dto.HasIntercept,
                Diagnostics = diagnostics,
            };
        }
        static string Pack(double[] values)
        {
            var bytes = new byte[checked(values.Length * 8)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        static string Pack(double[,] values)
        {
            int n = values.GetLength(0), p = values.GetLength(1);
            var bytes = new byte[checked(n * p * 8)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        static string PackLongs(long[,] values)
        {
            int n = values.GetLength(0), p = values.GetLength(1);
            var bytes = new byte[checked(n * p * 8)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        static AdaDto WriteAda(AdaBoostModel m) => new()
        {
            Regression = m.Regression,
            ClassCount = m.ClassCount,
            FeatureCount = m.FeatureCount,
            EstimatorsUsed = m.EstimatorsUsed,
            EstimatorsRequested = m.EstimatorsRequested,
            LearningRate = m.LearningRate,
            MaxDepth = m.MaxDepth,
            Seed = m.Seed,
            Loss = m.Loss.ToString(),
            Criterion = m.Criterion.ToString(),
            EstimatorWeights = Pack(m.EstimatorWeights),
            EstimatorErrors = Pack(m.EstimatorErrors),
            Importances = Pack(m.Importances),
            Sampled = m.Sampled,
            RowsFit = m.RowsFit,
            SourceRows = m.SourceRows,
            StoppedEarly = m.StoppedEarly,
            StopReason = m.StopReason,
            TimeBudgetHit = m.TimeBudgetHit,
            Stumps = m.Stumps.Select(WriteStump).ToArray(),
            RegressionTrees = m.RegressionTrees.Select(WriteTree).ToArray(),
        };

        static StumpDto WriteStump(AdaBoostStump s) => new()
        {
            Feature = s.Feature,
            Threshold = s.Threshold,
            Prediction = s.Prediction,
            Importances = s.Importances.Length == 0 ? null : Pack(s.Importances),
            Left = s.Left is null ? null : WriteStump(s.Left),
            Right = s.Right is null ? null : WriteStump(s.Right),
        };

        static AdaBoostModel ReadAda(JsonElement el, int featureCount)
        {
            var dto = Parse<AdaDto>(el);
            if (dto.FeatureCount != featureCount)
                throw new ModelStoreException("Stored AdaBoost feature count does not match the feature list.");
            if (dto.EstimatorsUsed < 0 || dto.Stumps != null && dto.Stumps.Length > MaxNodeCount)
                throw new ModelStoreException("Stored AdaBoost estimator count is not usable. The model was not loaded.");
            if (!Enum.TryParse<AdaBoostLoss>(dto.Loss, out var loss))
                throw new ModelStoreException($"Unknown AdaBoost loss '{dto.Loss}'.");
            if (!Enum.TryParse<TreeCriterion>(dto.Criterion, out var criterion))
                throw new ModelStoreException($"Unknown AdaBoost criterion '{dto.Criterion}'.");
            int weightCount = string.IsNullOrEmpty(dto.EstimatorWeights) ? 0 : CountPacked(dto.EstimatorWeights);
            if (weightCount < dto.EstimatorsUsed)
                throw new ModelStoreException("Stored AdaBoost weights are shorter than the estimator count. The model was not loaded.");
            var seen = new int[1];
            var stumps = (dto.Stumps ?? Array.Empty<StumpDto>()).Select(s => ReadStump(s, seen)).ToArray();
            if (stumps.Length < dto.EstimatorsUsed && !dto.Regression)
                throw new ModelStoreException("Stored AdaBoost stumps are shorter than the estimator count. The model was not loaded.");
            var trees = (dto.RegressionTrees ?? Array.Empty<TreeDto>()).Select(t => ReadTree(JsonSerializer.SerializeToElement(t, JsonOpt))).ToArray();
            if (dto.Regression && trees.Length < dto.EstimatorsUsed)
                throw new ModelStoreException("Stored AdaBoost regression trees are shorter than the estimator count. The model was not loaded.");
            return new AdaBoostModel
            {
                Regression = dto.Regression,
                ClassCount = dto.ClassCount,
                FeatureCount = dto.FeatureCount,
                EstimatorsUsed = dto.EstimatorsUsed,
                EstimatorsRequested = dto.EstimatorsRequested,
                LearningRate = dto.LearningRate,
                MaxDepth = dto.MaxDepth,
                Seed = dto.Seed,
                Loss = loss,
                Criterion = criterion,
                EstimatorWeights = Unpack(dto.EstimatorWeights, weightCount, "AdaBoost weights"),
                EstimatorErrors = Unpack(dto.EstimatorErrors, string.IsNullOrEmpty(dto.EstimatorErrors) ? 0 : CountPacked(dto.EstimatorErrors), "AdaBoost errors"),
                Importances = Unpack(dto.Importances, featureCount, "AdaBoost importances"),
                Sampled = dto.Sampled,
                RowsFit = dto.RowsFit,
                SourceRows = dto.SourceRows,
                StoppedEarly = dto.StoppedEarly,
                StopReason = dto.StopReason,
                TimeBudgetHit = dto.TimeBudgetHit,
                Stumps = stumps,
                RegressionTrees = trees,
            };
        }

        static AdaBoostStump ReadStump(StumpDto? dto, int[] seen, int depth = 0)
        {
            if (dto is null) throw new ModelStoreException("A stored AdaBoost stump is missing.");
            if (depth > MaxTreeDepth)
                throw new ModelStoreException($"A stored AdaBoost stump is deeper than {MaxTreeDepth}. The model was not loaded.");
            if (++seen[0] > MaxNodeCount)
                throw new ModelStoreException($"A stored AdaBoost model has more than {MaxNodeCount:N0} nodes. The model was not loaded.");
            return new AdaBoostStump
            {
                Feature = dto.Feature,
                Threshold = dto.Threshold,
                Prediction = dto.Prediction,
                Importances = string.IsNullOrEmpty(dto.Importances) ? Array.Empty<double>() : Unpack(dto.Importances, CountPacked(dto.Importances), "stump importances"),
                Left = dto.Left is null ? null : ReadStump(dto.Left, seen, depth + 1),
                Right = dto.Right is null ? null : ReadStump(dto.Right, seen, depth + 1),
            };
        }

        static ScoreDto WriteScore(LinearScoreModel m) => new()
        {
            Logistic = m.Logistic,
            Coefficients = Pack(m.Coefficients),
        };

        static LinearScoreModel ReadScore(JsonElement el, int featureCount)
        {
            var dto = Parse<ScoreDto>(el);
            int n = string.IsNullOrEmpty(dto.Coefficients) ? 0 : CountPacked(dto.Coefficients);
            if (n != featureCount + 1)
                throw new ModelStoreException($"Stored linear score has {n} coefficients; expected {featureCount + 1} (features plus intercept). The model was not loaded.");
            return new LinearScoreModel { Coefficients = Unpack(dto.Coefficients, n, "linear score coefficients"), Logistic = dto.Logistic };
        }

        static MultinomialDto WriteMultinomial(MultinomialLogisticModel m) => new()
        {
            ClassCount = m.ClassCount,
            FeatureCount = m.FeatureCount,
            ClassPresent = m.ClassPresent,
            Coefficients = Pack(m.Coefficients),
            C = m.C,
            Converged = m.Converged,
            Iterations = m.Iterations,
            RowsFit = m.RowsFit,
        };

        static MultinomialLogisticModel ReadMultinomial(JsonElement el, int featureCount)
        {
            var dto = Parse<MultinomialDto>(el);
            if (dto.FeatureCount != featureCount)
                throw new ModelStoreException("Stored multinomial logistic feature count does not match the feature list. The model was not loaded.");
            if (dto.ClassCount < 2)
                throw new ModelStoreException("Stored multinomial logistic model needs at least two classes. The model was not loaded.");
            if (dto.ClassPresent is null || dto.ClassPresent.Length != dto.ClassCount)
                throw new ModelStoreException("Stored multinomial logistic class-presence flags do not match the class count. The model was not loaded.");
            if (!dto.ClassPresent.Any(b => b))
                throw new ModelStoreException("Stored multinomial logistic model has no fitted class. The model was not loaded.");
            if (!double.IsFinite(dto.C) || dto.C <= 0)
                throw new ModelStoreException("Stored multinomial logistic C must be a positive finite number. The model was not loaded.");
            var coef = Unpack(dto.Coefficients, dto.ClassCount, featureCount + 1, "multinomial logistic coefficients");
            for (int k = 0; k < dto.ClassCount; k++)
                for (int j = 0; j <= featureCount; j++)
                    if (!double.IsFinite(coef[k, j]))
                        throw new ModelStoreException("Stored multinomial logistic coefficients contain a non-finite value. The model was not loaded.");
            return new MultinomialLogisticModel
            {
                Coefficients = coef,
                ClassPresent = dto.ClassPresent,
                C = dto.C,
                Converged = dto.Converged,
                Iterations = Math.Max(dto.Iterations, 0),
                RowsFit = Math.Max(dto.RowsFit, 0),
            };
        }

        static double[] Unpack(string? b64, int count, string what)
        {
            count = RequireElements(count, what);
            if (count == 0) return Array.Empty<double>();
            var bytes = Decode(b64, (long)count * 8, what);
            var values = new double[count];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        static double[,] Unpack(string? b64, int rows, int cols, string what)
        {
            int n = RequireProduct(rows, cols, what);
            var flat = Unpack(b64, n, what);
            var m = new double[rows, cols];
            if (flat.Length > 0) Buffer.BlockCopy(flat, 0, m, 0, flat.Length * 8);
            return m;
        }

        static long[,] UnpackLongs(string? b64, int rows, int cols, string what)
        {
            int n = RequireProduct(rows, cols, what);
            if (n == 0) return new long[rows, cols];
            var bytes = Decode(b64, (long)n * 8, what);
            var m = new long[rows, cols];
            Buffer.BlockCopy(bytes, 0, m, 0, bytes.Length);
            return m;
        }

        static int RequireElements(int count, string what)
        {
            if (count < 0)
                throw new ModelStoreException($"Stored {what} has a negative length. The model was not loaded.");
            long bytes;
            try { bytes = checked((long)count * 8); }
            catch (OverflowException)
            {
                throw new ModelStoreException($"Stored {what} is over the {MaxPayloadBytes.ToString("N0", CultureInfo.InvariantCulture)} byte limit. The model was not loaded.");
            }
            if (bytes > MaxPayloadBytes)
                throw new ModelStoreException($"Stored {what} asks for {count.ToString("N0", CultureInfo.InvariantCulture)} values ({bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes), over the {MaxPayloadBytes.ToString("N0", CultureInfo.InvariantCulture)} byte limit. The model was not loaded.");
            return count;
        }

        static int RequireProduct(int rows, int cols, string what)
        {
            if (rows < 0 || cols < 0)
                throw new ModelStoreException($"Stored {what} has a negative dimension. The model was not loaded.");
            long n;
            try { n = checked((long)rows * cols); }
            catch (OverflowException)
            {
                throw new ModelStoreException($"Stored {what} dimensions overflow. The model was not loaded.");
            }
            if (n > int.MaxValue)
                throw new ModelStoreException($"Stored {what} is too large. The model was not loaded.");
            RequireElements((int)n, what);
            return (int)n;
        }

        static byte[] Decode(string? b64, long expectedBytes, string what)
        {
            if (string.IsNullOrEmpty(b64)) throw new ModelStoreException($"Stored {what} is missing.");
            if (expectedBytes < 0 || expectedBytes > MaxPayloadBytes)
                throw new ModelStoreException($"Stored {what} is over the {MaxPayloadBytes.ToString("N0", CultureInfo.InvariantCulture)} byte limit. The model was not loaded.");
            if ((long)b64.Length > (MaxPayloadBytes / 3 + 1) * 4)
                throw new ModelStoreException($"Stored {what} is over the {MaxPayloadBytes.ToString("N0", CultureInfo.InvariantCulture)} byte limit. The model was not loaded.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new ModelStoreException($"Stored {what} is not valid base64."); }
            if (bytes.LongLength != expectedBytes)
                throw new ModelStoreException($"Stored {what} has {bytes.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes, expected {expectedBytes.ToString("N0", CultureInfo.InvariantCulture)}.");
            return bytes;
        }


        static bool TryResolve(IReadOnlyList<string> headers, string name, out int index, out bool ambiguous)
        {
            index = -1;
            ambiguous = false;
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], name, StringComparison.Ordinal)) { index = i; return true; }
            int found = -1;
            for (int i = 0; i < headers.Count; i++)
            {
                if (!string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found >= 0) { ambiguous = true; return false; }
                found = i;
            }
            if (found < 0) return false;
            index = found;
            return true;
        }

        static ModelBinding FailBind(string error) => new() { Error = error };

        static string DescribeBind(IReadOnlyList<string> missing, IReadOnlyList<string> ambiguous)
        {
            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("Missing columns: " + string.Join(", ", missing.Distinct(StringComparer.Ordinal)));
            if (ambiguous.Count > 0) parts.Add("Ambiguous columns: " + string.Join(", ", ambiguous.Distinct(StringComparer.Ordinal)));
            return string.Join(" ", parts);
        }

        static string ColumnName(FormulaLayout layout, int k)
            => k >= 0 && k < layout.Names.Length ? layout.Names[k] : "x" + k.ToString(CultureInfo.InvariantCulture);

        static string Cell(string[] row, int col) => col >= 0 && col < row.Length ? row[col] : "";

        static int IndexOf(string[] levels, string value)
        {
            for (int i = 0; i < levels.Length; i++)
                if (string.Equals(levels[i], value, StringComparison.Ordinal)) return i;
            return -1;
        }

        internal sealed class FormulaLayout
        {
            public FormulaLayout(string[] names, int[] headerIndex, bool[] categorical, string[][] levels, (int Var, int Level)[][] columns)
            {
                Names = names;
                HeaderIndex = headerIndex;
                Categorical = categorical;
                Levels = levels;
                Columns = columns;
            }
            public string[] Names { get; }
            public int[] HeaderIndex { get; }
            public bool[] Categorical { get; }
            public string[][] Levels { get; }
            public (int Var, int Level)[][] Columns { get; }
        }

        internal sealed class FeatureLayout
        {
            public FeatureLayout(List<FeatureGroupEnc> groups) => Groups = groups;
            public List<FeatureGroupEnc> Groups { get; }
        }

        internal sealed class FeatureGroupEnc
        {
            public FeatureGroupEnc(string column, int header)
            {
                Column = column;
                Header = header;
            }
            public string Column { get; }
            public int Header { get; }
            public List<int> Indexes { get; } = new();
            public List<string?> Levels { get; } = new();
            public bool Categorical { get; set; }
        }

        sealed class FileDto
        {
            public string? Format { get; set; }
            public int Version { get; set; }
            public string? AppVersion { get; set; }
            public string? CreatedAt { get; set; }
            public string? ModelType { get; set; }
            public string? Task { get; set; }
            public string? Target { get; set; }
            public string[]? ClassNames { get; set; }
            public int TrainingRows { get; set; }
            public string? EvaluationSummary { get; set; }
            public string? Formula { get; set; }
            public bool HasIntercept { get; set; }
            public string[]? ForcedCategorical { get; set; }
            public string[]? ResponseLevels { get; set; }
            public string? OffsetColumn { get; set; }
            public string? ExposureColumn { get; set; }
            public string? TrialsColumn { get; set; }
            public string? VarianceWeightColumn { get; set; }
            public string? FrequencyWeightColumn { get; set; }
            public Dictionary<string, string>? Parameters { get; set; }
            public FeatureDto[]? Features { get; set; }
            public FactorDto[]? Factors { get; set; }
            public ScalerDto? Scaler { get; set; }
            public JsonElement Engine { get; set; }
        }

        sealed class FeatureDto
        {
            public string Column { get; set; } = "";
            public string Kind { get; set; } = "";
            public string? Level { get; set; }
        }

        sealed class FactorDto
        {
            public string Variable { get; set; } = "";
            public string[]? Levels { get; set; }
        }

        sealed class ScalerDto
        {
            public string Method { get; set; } = "";
            public string Center { get; set; } = "";
            public string Scale { get; set; } = "";
        }

        sealed class EngineDto
        {
            public string Type { get; set; } = "";
            public KnnDto? Knn { get; set; }
            public BayesDto? Bayes { get; set; }
            public LdaDto? Lda { get; set; }
            public TreeDto? Tree { get; set; }
            public ForestDto? Forest { get; set; }
            public SvmDto? Svm { get; set; }
            public BoostDto? Boosting { get; set; }
            public LinearDto? Linear { get; set; }
            public GlmDto? Glm { get; set; }
            public AdaDto? Ada { get; set; }
            public ScoreDto? LinearScore { get; set; }
            public MultinomialDto? Multinomial { get; set; }
        }

        sealed class AdaDto
        {
            public bool Regression { get; set; }
            public int ClassCount { get; set; }
            public int FeatureCount { get; set; }
            public int EstimatorsUsed { get; set; }
            public int EstimatorsRequested { get; set; }
            public double LearningRate { get; set; }
            public int MaxDepth { get; set; }
            public int Seed { get; set; }
            public string Loss { get; set; } = "";
            public string Criterion { get; set; } = "";
            public string EstimatorWeights { get; set; } = "";
            public string EstimatorErrors { get; set; } = "";
            public string Importances { get; set; } = "";
            public bool Sampled { get; set; }
            public int RowsFit { get; set; }
            public int SourceRows { get; set; }
            public bool StoppedEarly { get; set; }
            public string? StopReason { get; set; }
            public bool TimeBudgetHit { get; set; }
            public StumpDto[]? Stumps { get; set; }
            public TreeDto[]? RegressionTrees { get; set; }
        }

        sealed class StumpDto
        {
            public int Feature { get; set; } = -1;
            public double Threshold { get; set; }
            public int Prediction { get; set; }
            public string? Importances { get; set; }
            public StumpDto? Left { get; set; }
            public StumpDto? Right { get; set; }
        }

        sealed class ScoreDto
        {
            public bool Logistic { get; set; }
            public string Coefficients { get; set; } = "";
        }

        sealed class MultinomialDto
        {
            public int ClassCount { get; set; }
            public int FeatureCount { get; set; }
            public bool[]? ClassPresent { get; set; }
            public string Coefficients { get; set; } = "";
            public double C { get; set; }
            public bool Converged { get; set; }
            public int Iterations { get; set; }
            public int RowsFit { get; set; }
        }


        sealed class KnnDto
        {
            public int Neighbors { get; set; }
            public int ClassCount { get; set; }
            public int Rows { get; set; }
            public int Cols { get; set; }
            public string TrainX { get; set; } = "";
            public int[]? Labels { get; set; }
        }

        sealed class BayesDto
        {
            public int ClassCount { get; set; }
            public int[]? ClassCounts { get; set; }
            public string Prior { get; set; } = "";
            public string LogPrior { get; set; } = "";
            public double Epsilon { get; set; }
            public double VarSmoothing { get; set; }
            public double Alpha { get; set; }
            public int[]? NumericColumns { get; set; }
            public int MeanRows { get; set; }
            public int MeanCols { get; set; }
            public string Mean { get; set; } = "";
            public int VarRows { get; set; }
            public int VarCols { get; set; }
            public string Variance { get; set; } = "";
            public CatDto[]? Categorical { get; set; }
        }

        sealed class CatDto
        {
            public int SourceColumn { get; set; }
            public int[]? Columns { get; set; }
            public int CountRows { get; set; }
            public int CountCols { get; set; }
            public string Counts { get; set; } = "";
            public int LogRows { get; set; }
            public int LogCols { get; set; }
            public string LogProb { get; set; } = "";
        }

        sealed class LdaDto
        {
            public int ClassCount { get; set; }
            public int FeatureCount { get; set; }
            public string[]? ClassNames { get; set; }
            public string[]? FeatureNames { get; set; }
            public string Priors { get; set; } = "";
            public int[]? ClassCounts { get; set; }
            public int MeansRows { get; set; }
            public int MeansCols { get; set; }
            public string ClassMeans { get; set; } = "";
            public string ExplainedVarianceRatio { get; set; } = "";
            public int DirRows { get; set; }
            public int DirCols { get; set; }
            public string Directions { get; set; } = "";
            public bool UsedFisherDirections { get; set; }
            public int WithinRank { get; set; }
            public double Tolerance { get; set; }
            public int CoefRows { get; set; }
            public int CoefCols { get; set; }
            public string Coef { get; set; } = "";
            public string Intercept { get; set; } = "";
        }

        sealed class TreeDto
        {
            public int FeatureCount { get; set; }
            public int ClassCount { get; set; }
            public bool Regression { get; set; }
            public string Criterion { get; set; } = "";
            public string Importances { get; set; } = "";
            public int Depth { get; set; }
            public int LeafCount { get; set; }
            public int NodeCount { get; set; }
            public bool ApproximateSplits { get; set; }
            public int BinCount { get; set; }
            public NodeDto? Root { get; set; }
        }

        sealed class NodeDto
        {
            public int Feature { get; set; }
            public double Threshold { get; set; }
            public double Impurity { get; set; }
            public int Count { get; set; }
            public int Prediction { get; set; }
            public double Value { get; set; }
            public int[]? ClassCounts { get; set; }
            public NodeDto? Left { get; set; }
            public NodeDto? Right { get; set; }
        }

        sealed class ForestDto
        {
            public int FeatureCount { get; set; }
            public int ClassCount { get; set; }
            public bool Regression { get; set; }
            public string Importances { get; set; } = "";
            public double OobScore { get; set; }
            public double OobError { get; set; }
            public int OobRows { get; set; }
            public int TrainingRows { get; set; }
            public int SourceRows { get; set; }
            public bool Sampled { get; set; }
            public bool ApproximateSplits { get; set; }
            public int BinCount { get; set; }
            public int Seed { get; set; }
            public int MaxFeatures { get; set; }
            public int TreeCount { get; set; }
            public TreeDto[]? Trees { get; set; }
        }

        sealed class SvmDto
        {
            public int Rows { get; set; }
            public int Cols { get; set; }
            public string X { get; set; } = "";
            public int[]? Labels { get; set; }
            public int ClassCount { get; set; }
            public string Kernel { get; set; } = "";
            public double Gamma { get; set; }
            public double C { get; set; }
            public PairDto[]? Pairs { get; set; }
            public string?[]? LinearWeights { get; set; }
            public string? LinearBias { get; set; }
            public int[]? SupportPerClass { get; set; }
            public bool Converged { get; set; }
            public int Iterations { get; set; }
            public string? Solver { get; set; }
            public string? Multiclass { get; set; }
            public bool Sampled { get; set; }
            public int RowsPresented { get; set; }
            public int RowsUsed { get; set; }
        }

        sealed class PairDto
        {
            public int[]? Rows { get; set; }
            public int[]? Y { get; set; }
            public string Alpha { get; set; } = "";
            public double Rho { get; set; }
            public int Iterations { get; set; }
            public bool Converged { get; set; }
        }

        sealed class BoostDto
        {
            public string Task { get; set; } = "";
            public string Loss { get; set; } = "";
            public int ClassCount { get; set; }
            public int FeatureCount { get; set; }
            public string? Baseline { get; set; }
            public string[]? Thresholds { get; set; }
            public int[]? BinsPerFeature { get; set; }
            public string? Gain { get; set; }
            public int IterationsUsed { get; set; }
            public int MaxIterations { get; set; }
            public bool EarlyStopped { get; set; }
            public string? TrainLoss { get; set; }
            public string? ValidationLoss { get; set; }
            public int BestValidationIndex { get; set; }
            public bool BinEdgesSubsampled { get; set; }
            public int BinEdgeRows { get; set; }
            public int RowsFit { get; set; }
            public int RowsValidation { get; set; }
            public double LearningRate { get; set; }
            public int MaxDepth { get; set; }
            public int MaxLeafNodes { get; set; }
            public int MinSamplesLeaf { get; set; }
            public double L2Regularization { get; set; }
            public int MaxBins { get; set; }
            public int Seed { get; set; }
            public bool EarlyStopping { get; set; }
            public double ValidationFraction { get; set; }
            public int IterationsNoChange { get; set; }
            public double EarlyStoppingTolerance { get; set; }
            public PredDto[][][]? Trees { get; set; }
        }

        sealed class PredDto
        {
            public bool Leaf { get; set; }
            public int Feature { get; set; }
            public double Threshold { get; set; }
            public double Value { get; set; }
            public int Left { get; set; }
            public int Right { get; set; }
            public bool MissingLeft { get; set; }
            public double Gain { get; set; }
        }

        sealed class LinearDto
        {
            public string[]? Names { get; set; }
            public string Estimates { get; set; } = "";
            public string StdErrors { get; set; } = "";
            public string T { get; set; } = "";
            public string PValues { get; set; } = "";
            public string CiLow { get; set; } = "";
            public string CiHigh { get; set; } = "";
            public bool[]? CoefAliased { get; set; }
            public string Beta { get; set; } = "";
            public int XtXRows { get; set; }
            public int XtXCols { get; set; }
            public string XtXInverse { get; set; } = "";
            public bool[]? Aliased { get; set; }
            public double ResidualMin { get; set; }
            public double ResidualQ1 { get; set; }
            public double ResidualMedian { get; set; }
            public double ResidualQ3 { get; set; }
            public double ResidualMax { get; set; }
            public double ResidualMean { get; set; }
            public double? NormalityW { get; set; }
            public double? NormalityP { get; set; }
            public int? NormalityN { get; set; }
            public bool? NormalityCapped { get; set; }
            public int N { get; set; }
            public int Rank { get; set; }
            public int DfResidual { get; set; }
            public int DfModel { get; set; }
            public bool HasIntercept { get; set; }
            public double Rss { get; set; }
            public double Sigma2 { get; set; }
            public double ResidualSe { get; set; }
            public double RSquared { get; set; }
            public double AdjustedRSquared { get; set; }
            public double FStatistic { get; set; }
            public double FPValue { get; set; }
            public double LogLikelihood { get; set; }
            public double Aic { get; set; }
            public double Bic { get; set; }
        }

        sealed class GlmDto
        {
            public string Family { get; set; } = "";
            public string Link { get; set; } = "";
            public string[]? Names { get; set; }
            public string Coefficients { get; set; } = "";
            public string StdErrors { get; set; } = "";
            public string Z { get; set; } = "";
            public string PValues { get; set; } = "";
            public string CiLow { get; set; } = "";
            public string CiHigh { get; set; } = "";
            public bool[]? Aliased { get; set; }
            public int Iterations { get; set; }
            public bool Converged { get; set; }
            public double Scale { get; set; }
            public double Deviance { get; set; }
            public double NullDeviance { get; set; }
            public double PearsonChi2 { get; set; }
            public double LogLikelihood { get; set; }
            public double NullLogLikelihood { get; set; }
            public double Aic { get; set; }
            public double Bic { get; set; }
            public double LikelihoodRatio { get; set; }
            public double LikelihoodRatioP { get; set; }
            public int DfModel { get; set; }
            public int DfResid { get; set; }
            public int Rank { get; set; }
            public int N { get; set; }
            public bool HasIntercept { get; set; }
            public string[]? Diagnostics { get; set; }
        }
    }

    /// <summary>적용 중 채점 가능 행만으로 누적한 지표. 결측·미지 수준은 제외하고 건수를 따로 센다.</summary>
    public sealed class ApplyMetrics
    {
        readonly ModelBundle _model;
        readonly long[,] _cm;
        double _sse, _sae, _sum, _sumSq;
        public int RowsRead { get; private set; }
        public int Scorable { get; private set; }
        public int NotScorable { get; private set; }
        public int UnseenLevels { get; private set; }
        public int MissingOrNonNumeric { get; private set; }
        public int TargetUsed { get; private set; }
        public int TargetSkipped { get; private set; }
        public ClassificationMetrics? Classification { get; private set; }
        public RegressionMetrics? Regression { get; private set; }

        internal ApplyMetrics(ModelBundle model)
        {
            _model = model;
            int k = model.ClassNames?.Count ?? 0;
            _cm = k > 0 ? new long[k, k] : new long[0, 0];
        }

        internal void Add(RowPrediction pred, string? actualRaw, bool haveTarget)
        {
            RowsRead++;
            if (!pred.Scorable)
            {
                NotScorable++;
                if (pred.Reason != null && pred.Reason.Contains("unseen level", StringComparison.Ordinal)) UnseenLevels++;
                else MissingOrNonNumeric++;
                return;
            }
            Scorable++;
            if (!haveTarget) return;
            if (_model.Task == ModelTask.Classification)
            {
                if (!TryClass(actualRaw, out int actual)) { TargetSkipped++; return; }
                if (pred.ClassIndex < 0 || pred.ClassIndex >= _cm.GetLength(0)) { TargetSkipped++; return; }
                _cm[actual, pred.ClassIndex]++;
                TargetUsed++;
            }
            else
            {
                // 시행 수 열을 쓴 이항 모형의 목표는 성공 횟수이므로 시행 수 × 확률과 비교한다(확률과 비교하지 않음).
                double predicted = _model.TrialsColumn != null ? pred.ExpectedSuccesses : pred.Value;
                if (!StatValue.TryNumber(actualRaw, out double y) || !double.IsFinite(predicted)) { TargetSkipped++; return; }
                double e = y - predicted;
                _sse += e * e;
                _sae += Math.Abs(e);
                _sum += y;
                _sumSq += y * y;
                TargetUsed++;
            }
        }

        internal void Finish()
        {
            if (_model.Task == ModelTask.Classification && TargetUsed > 0)
                Classification = ClassifierEvaluation.FromConfusion(_cm);
            else if (_model.Task == ModelTask.Regression && TargetUsed > 0)
            {
                double n = TargetUsed;
                double sst = _sumSq - _sum * _sum / n;
                double r2 = sst == 0 ? double.NaN : 1 - _sse / sst;
                Regression = new RegressionMetrics(Math.Sqrt(_sse / n), _sae / n, r2, TargetUsed);
            }
        }

        bool TryClass(string? raw, out int index)
        {
            index = -1;
            if (raw is null || StatValue.IsMissing(raw) || _model.ClassNames is null) return false;
            string value = raw.Trim();
            var names = _model.ClassNames;
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], value, StringComparison.Ordinal)) { index = i; return true; }
            return false;
        }

        public string Format()
        {
            var sb = new StringBuilder();
            sb.Append("Rows read ").Append(RowsRead.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(" · scored ").Append(Scorable.ToString("N0", CultureInfo.InvariantCulture));
            sb.Append(" · not scored ").Append(NotScorable.ToString("N0", CultureInfo.InvariantCulture));
            if (UnseenLevels > 0)
                sb.Append(" (unseen level ").Append(UnseenLevels.ToString("N0", CultureInfo.InvariantCulture)).Append(')');
            sb.AppendLine();
            if (Classification is { } cls)
            {
                sb.Append("Target metrics on ").Append(TargetUsed.ToString("N0", CultureInfo.InvariantCulture)).Append(" scored rows");
                if (TargetSkipped > 0) sb.Append(" · target skipped ").Append(TargetSkipped.ToString("N0", CultureInfo.InvariantCulture));
                sb.Append(" · accuracy ").Append(cls.Accuracy.ToString("G6", CultureInfo.InvariantCulture));
                sb.Append(" · macro F1 ").Append(cls.MacroF1.ToString("G6", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
            else if (Regression is { } reg)
            {
                sb.Append("Target metrics on ").Append(TargetUsed.ToString("N0", CultureInfo.InvariantCulture)).Append(" scored rows");
                if (TargetSkipped > 0) sb.Append(" · target skipped ").Append(TargetSkipped.ToString("N0", CultureInfo.InvariantCulture));
                sb.Append(" · RMSE ").Append(reg.Rmse.ToString("G6", CultureInfo.InvariantCulture));
                sb.Append(" · MAE ").Append(reg.Mae.ToString("G6", CultureInfo.InvariantCulture));
                sb.Append(" · R² ").Append(reg.RSquared.ToString("G6", CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
            else sb.AppendLine("The view has no matching target column, so metrics were not computed.");
            return sb.ToString();
        }
    }
}
