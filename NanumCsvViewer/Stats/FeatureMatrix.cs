using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    // 분류·군집·차원축소(이슈 #27) 공용 입력. 선택한 특성 컬럼(수치 그대로, 범주는 원-핫)과
    // 선택적 목표(범주 → 클래스 코드, 수치 → double)를 한 번 순회로 압축 수집한다. 결측 행은 목록별 삭제.

    public enum TargetKind { None, Categorical, Numeric }

    public sealed record FeatureMatrixOptions
    {
        public long MemoryBudgetBytes { get; init; } = AnalysisMemoryBudget.Current;
        /// <summary>범주 특성 원-핫 전개와 범주 목표의 최대 수준 수.</summary>
        public int MaxLevels { get; init; } = 200;
    }

    public sealed class FeatureMatrix
    {
        /// <summary>n×p 특성 행렬.</summary>
        public required double[,] X { get; init; }
        /// <summary>열 이름: 수치 컬럼명 또는 "컬럼=수준"(원-핫).</summary>
        public required IReadOnlyList<string> FeatureNames { get; init; }
        /// <summary>각 특성 열이 온 원본 컬럼 인덱스.</summary>
        public required IReadOnlyList<int> SourceColumns { get; init; }
        /// <summary>범주 목표: 0..K-1 클래스 코드. 클래스 이름은 <see cref="ClassNames"/>(정렬됨).</summary>
        public int[]? ClassLabels { get; init; }
        public IReadOnlyList<string>? ClassNames { get; init; }
        public double[]? NumericTarget { get; init; }
        public required int[] ViewRows { get; init; }
        public required long RowsRead { get; init; }
        public required long RowsDropped { get; init; }

        public int RowCount => X.GetLength(0);
        public int FeatureCount => X.GetLength(1);
    }

    public static class FeatureMatrixBuilder
    {
        public static FeatureMatrix Build(
            IReadOnlyList<string[]> rows,
            IReadOnlyList<string> headers,
            IReadOnlyList<int> featureColumns,
            Func<int, VariableKind> kindOfColumn,
            int? targetColumn,
            TargetKind targetKind,
            FeatureMatrixOptions? options = null,
            CancellationToken cancellation = default)
        {
            options ??= new FeatureMatrixOptions();
            if (featureColumns.Count == 0) throw new DesignMatrixException("Select at least one feature column.");
            if (targetKind != TargetKind.None && targetColumn is null) throw new ArgumentException("Target column required.", nameof(targetColumn));
            if (targetColumn is { } tc && featureColumns.Contains(tc))
                throw new DesignMatrixException("The target column cannot also be a feature.");

            int f = featureColumns.Count;
            var kinds = featureColumns.Select(kindOfColumn).ToArray();
            var num = new List<double>[f];
            var codes = new List<int>[f];
            var levelMaps = new Dictionary<string, int>[f];
            var levelNames = new List<string>[f];
            for (int k = 0; k < f; k++)
            {
                if (kinds[k] == VariableKind.Numeric) num[k] = new List<double>();
                else { codes[k] = new List<int>(); levelMaps[k] = new(StringComparer.Ordinal); levelNames[k] = new List<string>(); }
            }
            var tNum = new List<double>();
            var tCodes = new List<int>();
            var tMap = new Dictionary<string, int>(StringComparer.Ordinal);
            var tNames = new List<string>();
            var viewRows = new List<int>();
            long dropped = 0;
            long bytesPerRow = 4L + kinds.Sum(k => k == VariableKind.Numeric ? 8L : 4L) + (targetKind == TargetKind.None ? 0 : 8L);

            var rowNum = new double[f];
            var rowCat = new string[f];
            for (int r = 0; r < rows.Count; r++)
            {
                if ((r & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var row = rows[r];
                bool ok = true;
                for (int k = 0; k < f && ok; k++)
                {
                    int c = featureColumns[k];
                    if (c >= row.Length) { ok = false; break; }
                    if (kinds[k] == VariableKind.Numeric) ok = StatValue.TryNumber(row[c], out rowNum[k]);
                    else if (StatValue.IsMissing(row[c])) ok = false;
                    else rowCat[k] = row[c].Trim();
                }
                double tv = 0;
                string? tl = null;
                if (ok && targetColumn is { } t)
                {
                    if (t >= row.Length) ok = false;
                    else if (targetKind == TargetKind.Numeric) ok = StatValue.TryNumber(row[t], out tv);
                    else if (StatValue.IsMissing(row[t])) ok = false;
                    else tl = row[t].Trim();
                }
                if (!ok) { dropped++; continue; }
                if ((long)(viewRows.Count + 1) * bytesPerRow > options.MemoryBudgetBytes) throw new AnalysisMemoryLimitException();

                for (int k = 0; k < f; k++)
                {
                    if (kinds[k] == VariableKind.Numeric) { num[k].Add(rowNum[k]); continue; }
                    if (!levelMaps[k].TryGetValue(rowCat[k], out int code))
                    {
                        code = levelMaps[k].Count;
                        if (code >= options.MaxLevels)
                            throw new DesignMatrixException($"'{headers[featureColumns[k]]}' has more than {options.MaxLevels:N0} levels; it cannot be one-hot encoded.");
                        levelMaps[k][rowCat[k]] = code;
                        levelNames[k].Add(rowCat[k]);
                    }
                    codes[k].Add(code);
                }
                if (targetKind == TargetKind.Numeric) tNum.Add(tv);
                else if (targetKind == TargetKind.Categorical)
                {
                    if (!tMap.TryGetValue(tl!, out int code))
                    {
                        code = tMap.Count;
                        if (code >= options.MaxLevels)
                            throw new DesignMatrixException($"Target '{headers[targetColumn!.Value]}' has more than {options.MaxLevels:N0} classes.");
                        tMap[tl!] = code;
                        tNames.Add(tl!);
                    }
                    tCodes.Add(code);
                }
                viewRows.Add(r);
            }
            cancellation.ThrowIfCancellationRequested();

            int n = viewRows.Count;
            if (n == 0) throw new DesignMatrixException("No complete rows: every row has a missing or non-numeric value in the selected columns.");

            // 열 전개(범주 → 정렬 수준 원-핫, 수준 전부)
            var names = new List<string>();
            var sources = new List<int>();
            var spec = new List<(int K, int Level)>();
            var remap = new int[f][];
            for (int k = 0; k < f; k++)
            {
                string h = headers[featureColumns[k]];
                if (kinds[k] == VariableKind.Numeric) { names.Add(h); sources.Add(featureColumns[k]); spec.Add((k, -1)); continue; }
                var sorted = StatValue.SortLevels(levelNames[k]);
                var pos = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < sorted.Count; i++) pos[sorted[i]] = i;
                remap[k] = levelNames[k].Select(l => pos[l]).ToArray();
                for (int l = 0; l < sorted.Count; l++) { names.Add($"{h}={sorted[l]}"); sources.Add(featureColumns[k]); spec.Add((k, l)); }
            }
            int p = names.Count;
            if ((long)n * p > AnalysisMemoryBudget.MaxArrayElements || (long)n * p * 8L + (long)n * bytesPerRow > options.MemoryBudgetBytes) throw new AnalysisMemoryLimitException();

            var x = new double[n, p];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++)
                {
                    var (k, level) = spec[j];
                    x[i, j] = level < 0 ? num[k][i] : (remap[k][codes[k][i]] == level ? 1.0 : 0.0);
                }
            }

            int[]? labels = null;
            IReadOnlyList<string>? classNames = null;
            if (targetKind == TargetKind.Categorical)
            {
                var sorted = StatValue.SortLevels(tNames);
                if (sorted.Count < 2) throw new DesignMatrixException("The target has only one class in the complete rows.");
                var pos = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < sorted.Count; i++) pos[sorted[i]] = i;
                var codeToClass = tNames.Select(l => pos[l]).ToArray();
                labels = tCodes.Select(c => codeToClass[c]).ToArray();
                classNames = sorted;
            }

            return new FeatureMatrix
            {
                X = x,
                FeatureNames = names,
                SourceColumns = sources,
                ClassLabels = labels,
                ClassNames = classNames,
                NumericTarget = targetKind == TargetKind.Numeric ? tNum.ToArray() : null,
                ViewRows = viewRows.ToArray(),
                RowsRead = rows.Count,
                RowsDropped = dropped,
            };
        }
    }

    public enum ScalingMethod { None, ZScore, MinMax }

    /// <summary>특성 스케일링. 학습 데이터로 적합한 뒤 같은 변환을 검증/예측 데이터에 적용한다(누수 방지).</summary>
    public sealed class FeatureScaler
    {
        public ScalingMethod Method { get; }
        /// <summary>열별 중심(Z: 평균, MinMax: 최소).</summary>
        public double[] Center { get; }
        /// <summary>열별 나눗수(Z: 표준편차, MinMax: 범위). 0이면 1로 둔다(상수 열은 0으로 변환).</summary>
        public double[] Scale { get; }

        private FeatureScaler(ScalingMethod method, double[] center, double[] scale)
        {
            Method = method; Center = center; Scale = scale;
        }

        /// <summary>rows(null이면 전체) 행으로 적합.</summary>
        public static FeatureScaler Fit(double[,] x, ScalingMethod method, IReadOnlyList<int>? rows = null)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            var center = new double[p];
            var scale = new double[p];
            for (int j = 0; j < p; j++) scale[j] = 1;
            if (method == ScalingMethod.None) return new FeatureScaler(method, center, scale);
            int count = rows?.Count ?? n;
            if (count == 0) throw new ArgumentException("No rows to fit the scaler.", nameof(rows));

            for (int j = 0; j < p; j++)
            {
                if (method == ScalingMethod.ZScore)
                {
                    double mean = 0, m2 = 0;
                    for (int t = 0; t < count; t++)
                    {
                        double v = x[rows?[t] ?? t, j];
                        double d = v - mean;
                        mean += d / (t + 1);
                        m2 += d * (v - mean);
                    }
                    double sd = count > 1 ? Math.Sqrt(m2 / (count - 1)) : 0;
                    center[j] = mean;
                    scale[j] = sd > 0 ? sd : 1;
                }
                else
                {
                    double min = double.PositiveInfinity, max = double.NegativeInfinity;
                    for (int t = 0; t < count; t++)
                    {
                        double v = x[rows?[t] ?? t, j];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    center[j] = min;
                    scale[j] = max > min ? max - min : 1;
                }
            }
            return new FeatureScaler(method, center, scale);
        }

        /// <summary>변환한 새 행렬.</summary>
        public double[,] Transform(double[,] x)
        {
            int n = x.GetLength(0), p = x.GetLength(1);
            if (p != Center.Length) throw new ArgumentException("Column count mismatch.", nameof(x));
            var result = new double[n, p];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++)
                    result[i, j] = (x[i, j] - Center[j]) / Scale[j];
            return result;
        }

        /// <summary>mask가 false인 열은 항등 변환(중심 0, 나눗수 1)으로 바꾼 사본. 범주(원-핫) 열을 스케일하지 않는 모형이 저장 모형에 쓴다.</summary>
        public FeatureScaler WithIdentityOutside(bool[] mask)
        {
            if (mask.Length != Center.Length) throw new ArgumentException("Mask length must match the columns.", nameof(mask));
            var center = (double[])Center.Clone();
            var scale = (double[])Scale.Clone();
            for (int j = 0; j < mask.Length; j++)
                if (!mask[j]) { center[j] = 0; scale[j] = 1; }
            return new FeatureScaler(Method, center, scale);
        }
    }
}
