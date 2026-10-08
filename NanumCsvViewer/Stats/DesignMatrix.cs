using System.Globalization;
using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Stats
{
    // 고급 통계(이슈 #27) 공용 입력 계층. 뷰 행(지연 읽기 목록)을 한 번만 순회하며 필요한 컬럼만
    // double/범주 코드로 압축 보관한다 — 문자열 행 스냅샷을 만들지 않아 큰 파일도 예산 안에서 다룬다.
    // 결측(빈 값·널 토큰)·수치 해석 불가 값이 있는 행은 목록별 삭제(listwise)하고 건수를 정직하게 보고한다.

    public enum VariableKind { Numeric, Categorical }

    public enum ResponseKind { Numeric, Binary }

    /// <summary>사용자 입력 문제(없는 컬럼·수준 과다·이진 아님·유효 행 없음 등). 메시지를 그대로 안내한다.</summary>
    public sealed class DesignMatrixException : InvalidOperationException
    {
        public DesignMatrixException(string message) : base(message) { }
    }

    /// <summary>범주 변수의 수준. Levels[0]이 기준(reference) 수준.</summary>
    public sealed record FactorLevels(string Variable, IReadOnlyList<string> Levels);

    /// <summary>항이 차지하는 설계행렬 열 범위(절편 제외).</summary>
    public sealed record TermColumns(FormulaTerm Term, int Start, int Count);

    public sealed record DesignMatrixOptions
    {
        public ResponseKind Response { get; init; } = ResponseKind.Numeric;
        /// <summary>이진 응답이 범주형일 때 1로 볼 수준. null이면 정렬상 두 번째 수준.</summary>
        public string? BinaryEventLevel { get; init; }
        /// <summary>압축 보관 + 설계행렬의 보수적 예상 바이트(기본: 설정의 공용 분석 메모리 예산). 초과 시 AnalysisMemoryLimitException(부분 결과 없음).</summary>
        public long MemoryBudgetBytes { get; init; } = AnalysisMemoryBudget.Current;
        public int MaxLevelsPerFactor { get; init; } = 500;
        /// <summary>설계행렬 최대 열 수. XᵀX 누적(p²)과 역행렬 비용을 묶는다.</summary>
        public int MaxColumns { get; init; } = 1000;
        /// <summary>GLM 선택 열(컬럼 이름). 지정하면 그 값이 없거나 수치가 아닌 행도 목록별 삭제한다.</summary>
        public string? OffsetColumn { get; init; }
        /// <summary>노출(exposure) 열. 오프셋에 ln(값)을 더한다(값은 양수여야 함).</summary>
        public string? ExposureColumn { get; init; }
        public string? VarianceWeightColumn { get; init; }
        public string? FrequencyWeightColumn { get; init; }
        public string? TrialsColumn { get; init; }
    }

    public sealed class DesignMatrix
    {
        /// <summary>n×p 설계행렬(절편 열 포함 시 0번 열).</summary>
        public required double[,] X { get; init; }
        public required double[] Y { get; init; }
        /// <summary>statsmodels 형식 열 이름: Intercept, x, g[T.b], x:g[T.b].</summary>
        public required IReadOnlyList<string> ColumnNames { get; init; }
        public required IReadOnlyList<TermColumns> Terms { get; init; }
        public required bool HasIntercept { get; init; }
        /// <summary>절편 없이 첫 범주를 전체 수준으로 코딩해 열 공간에 상수가 포함됨(statsmodels k_constant=1과 같은 판정).</summary>
        public bool HasImplicitConstant { get; init; }
        public required IReadOnlyDictionary<string, FactorLevels> Factors { get; init; }
        /// <summary>이진 응답: [0에 해당하는 값, 1에 해당하는 값]. 수치 응답이면 null.</summary>
        public IReadOnlyList<string>? ResponseLevels { get; init; }
        /// <summary>사용한 각 행의 원본 목록(뷰) 인덱스.</summary>
        public required int[] ViewRows { get; init; }
        public required long RowsRead { get; init; }
        /// <summary>결측·해석 불가로 제외한 행.</summary>
        public required long RowsDropped { get; init; }
        public required ModelFormula Formula { get; init; }
        /// <summary>옵션으로 지정한 오프셋·가중치·시행 수 열(행 순서는 X와 같다). 지정이 없으면 null.</summary>
        public GlmExtras? GlmExtras { get; init; }

        public int RowCount => Y.Length;
        public int ColumnCount => X.GetLength(1);
    }

    /// <summary>값 해석 공통 규칙(결측·수치). 앱 전체와 같은 NumericAffix·널 토큰 기준.</summary>
    public static class StatValue
    {
        public static bool IsMissing(string? raw)
        {
            if (raw is null) return true;
            string t = raw.Trim();
            return t.Length == 0 || ColumnStatisticsBuilder.IsNullToken(t);
        }

        public static bool TryNumber(string? raw, out double value)
        {
            value = 0;
            if (IsMissing(raw)) return false;
            return NumericAffix.TryParseNumber(raw!, out value) && double.IsFinite(value);
        }

        /// <summary>수준 정렬: 모두 수치로 읽히면 수치 순, 아니면 서수(Ordinal) 문자열 순.</summary>
        public static List<string> SortLevels(IEnumerable<string> levels)
        {
            var list = levels.ToList();
            var parsed = new double[list.Count];
            bool allNumeric = true;
            for (int i = 0; i < list.Count; i++)
                if (!double.TryParse(list[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i])) { allNumeric = false; break; }
            if (allNumeric)
            {
                var idx = Enumerable.Range(0, list.Count).OrderBy(i => parsed[i]).ThenBy(i => list[i], StringComparer.Ordinal).ToList();
                return idx.Select(i => list[i]).ToList();
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>식 변수 이름 → 헤더 인덱스. 정확 일치 우선, 없으면 대소문자 무시 유일 일치.</summary>
        public static int ResolveColumn(IReadOnlyList<string> headers, string name)
        {
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], name, StringComparison.Ordinal)) return i;
            int found = -1;
            for (int i = 0; i < headers.Count; i++)
            {
                if (!string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found >= 0) throw new DesignMatrixException($"Column name '{name}' is ambiguous (differs only by case).");
                found = i;
            }
            if (found < 0) throw new DesignMatrixException($"Unknown column '{name}'.");
            return found;
        }
    }

    public static class DesignMatrixBuilder
    {
        public static DesignMatrix Build(
            IReadOnlyList<string[]> rows,
            IReadOnlyList<string> headers,
            ModelFormula formula,
            Func<int, VariableKind> kindOfColumn,
            DesignMatrixOptions? options = null,
            CancellationToken cancellation = default)
        {
            options ??= new DesignMatrixOptions();
            if (options.MemoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options));

            int responseCol = StatValue.ResolveColumn(headers, formula.Response);
            var predictorNames = formula.PredictorVariables;
            var predictorCols = predictorNames.Select(n => StatValue.ResolveColumn(headers, n)).ToArray();
            int offsetCol = options.OffsetColumn is null ? -1 : StatValue.ResolveColumn(headers, options.OffsetColumn);
            int exposureCol = options.ExposureColumn is null ? -1 : StatValue.ResolveColumn(headers, options.ExposureColumn);
            int varWeightCol = options.VarianceWeightColumn is null ? -1 : StatValue.ResolveColumn(headers, options.VarianceWeightColumn);
            int freqWeightCol = options.FrequencyWeightColumn is null ? -1 : StatValue.ResolveColumn(headers, options.FrequencyWeightColumn);
            int trialsCol = options.TrialsColumn is null ? -1 : StatValue.ResolveColumn(headers, options.TrialsColumn);
            var offsetList = offsetCol >= 0 || exposureCol >= 0 ? new List<double>() : null;
            var varWeightList = varWeightCol >= 0 ? new List<double>() : null;
            var freqWeightList = freqWeightCol >= 0 ? new List<double>() : null;
            var trialsList = trialsCol >= 0 ? new List<double>() : null;
            var kinds = predictorNames.Select((n, k) =>
                formula.ForcedCategorical.Contains(n) ? VariableKind.Categorical : kindOfColumn(predictorCols[k])).ToArray();
            RequireHierarchy(formula, predictorNames, kinds);

            // 1패스: 압축 보관(수치 → double, 범주 → 첫 등장 코드).
            var raw = new ColumnStore(predictorNames.Count, kinds, options.MaxLevelsPerFactor, predictorNames);
            bool binary = options.Response == ResponseKind.Binary;
            var yNumeric = new List<double>();
            var yCodes = new List<int>();
            var yLevels = new Dictionary<string, int>(StringComparer.Ordinal);
            var yLevelNames = new List<string>();
            var viewRows = new List<int>();
            long dropped = 0;
            long bytesPerRow = 8L + 4L + raw.BytesPerRow;
            long budget = options.MemoryBudgetBytes;

            var parsedNum = new double[predictorNames.Count];
            var parsedCat = new string[predictorNames.Count];
            for (int r = 0; r < rows.Count; r++)
            {
                if ((r & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                var row = rows[r];
                if (!TryParseRow(row, responseCol, predictorCols, kinds, binary, parsedNum, parsedCat,
                        out double yNum, out string? yCat))
                {
                    dropped++;
                    continue;
                }
                if (!TryExtra(row, offsetCol, false, out double offRaw)
                    || !TryExtra(row, exposureCol, true, out double expRaw)
                    || !TryExtra(row, varWeightCol, false, out double varWeightValue)
                    || !TryExtra(row, freqWeightCol, false, out double freqWeightValue)
                    || !TryExtra(row, trialsCol, false, out double trialsValue))
                {
                    dropped++;
                    continue;
                }
                if ((long)(viewRows.Count + 1) * bytesPerRow > budget) throw new AnalysisMemoryLimitException();
                offsetList?.Add(offRaw + expRaw);
                varWeightList?.Add(varWeightValue);
                freqWeightList?.Add(freqWeightValue);
                trialsList?.Add(trialsValue);

                raw.Add(parsedNum, parsedCat);
                if (binary && yCat is not null)
                {
                    if (!yLevels.TryGetValue(yCat, out int code))
                    {
                        code = yLevels.Count;
                        if (code >= 64) throw new DesignMatrixException(
                            $"Response '{formula.Response}' has more than two distinct values; a binary outcome is required.");
                        yLevels[yCat] = code;
                        yLevelNames.Add(yCat);
                    }
                    yCodes.Add(code);
                }
                else yNumeric.Add(yNum);
                viewRows.Add(r);
            }
            cancellation.ThrowIfCancellationRequested();

            int n = viewRows.Count;
            if (n == 0) throw new DesignMatrixException("No complete rows: every row has a missing or non-numeric value in the model columns.");

            // 응답
            double[] y = new double[n];
            IReadOnlyList<string>? responseLevels = null;
            if (!binary)
            {
                yNumeric.CopyTo(y);
            }
            else
            {
                responseLevels = MapBinary(formula.Response, yLevelNames, yCodes, options.BinaryEventLevel, y);
            }

            // 범주 수준 정렬·재코딩
            var factors = new Dictionary<string, FactorLevels>(StringComparer.Ordinal);
            var remaps = new int[predictorNames.Count][];
            for (int k = 0; k < predictorNames.Count; k++)
            {
                if (kinds[k] != VariableKind.Categorical) continue;
                var firstSeen = raw.LevelNames(k);
                var sorted = StatValue.SortLevels(firstSeen);
                if (sorted.Count < 2)
                    throw new DesignMatrixException($"'{predictorNames[k]}' has only one level in the complete rows; it cannot be a categorical predictor.");
                var pos = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < sorted.Count; i++) pos[sorted[i]] = i;
                remaps[k] = firstSeen.Select(l => pos[l]).ToArray();
                factors[predictorNames[k]] = new FactorLevels(predictorNames[k], sorted);
            }

            // 전개 전에 열 수를 계산해 상한을 확인한다(범주 상호작용의 곱이 폭증해도 할당하지 않음).
            // 아래 전개와 같은 규칙: 절편이 없으면 첫 범주 주효과만 전체 수준.
            long width = formula.Intercept ? 1 : 0;
            bool fullRankProbe = !formula.Intercept;
            foreach (var term in formula.Terms)
            {
                bool full = fullRankProbe && term.Order == 1
                    && kinds[IndexOf(predictorNames, term.Variables[0])] == VariableKind.Categorical;
                if (full) fullRankProbe = false;
                long w = 1;
                foreach (string v in term.Variables)
                    if (factors.TryGetValue(v, out var f))
                        w = Math.Min(w * (f.Levels.Count - (full ? 0 : 1)), long.MaxValue / 1024);
                width += w;
                if (width > options.MaxColumns)
                    throw new DesignMatrixException(
                        $"The model would have more than {options.MaxColumns:N0} columns (term '{term.Name}'). Reduce categorical levels or interactions.");
            }

            // 열 전개
            var colNames = new List<string>();
            var termCols = new List<TermColumns>();
            var columnSpecs = new List<(int Var, int Level)[]>(); // 곱 성분: (변수, 수준 또는 -1=수치)
            if (formula.Intercept) { colNames.Add("Intercept"); columnSpecs.Add(Array.Empty<(int, int)>()); }
            var varIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < predictorNames.Count; k++) varIndex[predictorNames[k]] = k;

            // 절편이 없으면 첫 범주 주효과는 모든 수준을 쓴다(patsy/R 규칙) — 그 수준 열들이 절편 역할.
            bool fullRankPending = !formula.Intercept;
            foreach (var term in formula.Terms)
            {
                int start = colNames.Count;
                bool fullRank = fullRankPending && term.Order == 1 && kinds[varIndex[term.Variables[0]]] == VariableKind.Categorical;
                if (fullRank) fullRankPending = false;
                var combos = new List<(int Var, int Level)[]> { Array.Empty<(int, int)>() };
                var nameParts = new List<string[]> { Array.Empty<string>() };
                foreach (string v in term.Variables)
                {
                    int k = varIndex[v];
                    var nextCombos = new List<(int, int)[]>();
                    var nextNames = new List<string[]>();
                    for (int c = 0; c < combos.Count; c++)
                    {
                        if (kinds[k] == VariableKind.Numeric)
                        {
                            nextCombos.Add(combos[c].Append((k, -1)).ToArray());
                            nextNames.Add(nameParts[c].Append(v).ToArray());
                        }
                        else
                        {
                            var levels = factors[v].Levels;
                            for (int l = fullRank ? 0 : 1; l < levels.Count; l++)
                            {
                                nextCombos.Add(combos[c].Append((k, l)).ToArray());
                                nextNames.Add(nameParts[c].Append(fullRank ? $"{v}[{levels[l]}]" : $"{v}[T.{levels[l]}]").ToArray());
                            }
                        }
                    }
                    combos = nextCombos;
                    nameParts = nextNames;
                }
                for (int c = 0; c < combos.Count; c++)
                {
                    columnSpecs.Add(combos[c]);
                    colNames.Add(string.Join(":", nameParts[c]));
                }
                termCols.Add(new TermColumns(term, start, colNames.Count - start));
            }

            int p = colNames.Count;
            if (p == 0) throw new DesignMatrixException("The model has no columns (no intercept and no terms).");
            if ((long)n * p > AnalysisMemoryBudget.MaxArrayElements || (long)n * p * 8L + (long)n * bytesPerRow > budget) throw new AnalysisMemoryLimitException();

            var x = new double[n, p];
            for (int i = 0; i < n; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                for (int j = 0; j < p; j++)
                {
                    double value = 1.0;
                    foreach (var (k, level) in columnSpecs[j])
                    {
                        if (level < 0) value *= raw.Numeric(k, i);
                        else if (remaps[k][raw.Code(k, i)] != level) { value = 0; break; }
                    }
                    x[i, j] = value;
                }
            }

            return new DesignMatrix
            {
                X = x,
                Y = y,
                ColumnNames = colNames,
                Terms = termCols,
                HasIntercept = formula.Intercept,
                HasImplicitConstant = !formula.Intercept && !fullRankPending,
                Factors = factors,
                ResponseLevels = responseLevels,
                ViewRows = viewRows.ToArray(),
                RowsRead = rows.Count,
                RowsDropped = dropped,
                Formula = formula,
                GlmExtras = offsetList is null && varWeightList is null && freqWeightList is null && trialsList is null
                    ? null
                    : new GlmExtras
                    {
                        Offset = offsetList?.ToArray(),
                        VarianceWeights = varWeightList?.ToArray(),
                        FrequencyWeights = freqWeightList?.ToArray(),
                        Trials = trialsList?.ToArray(),
                    },
            };
        }

        /// <summary>
        /// 범주 변수를 포함한 항은 그 범주를 뺀 하위 항이 모형에 있어야 한다(없으면 절편이 대신). 처치 코딩이
        /// patsy/R과 같은 모형이 되는 조건이며, 어기면(예: y ~ x:C(g)) 다른 모형을 몰래 적합하지 않고 거부한다.
        /// </summary>
        private static void RequireHierarchy(ModelFormula formula, IReadOnlyList<string> predictors, VariableKind[] kinds)
        {
            foreach (var term in formula.Terms)
            {
                if (term.Order < 2) continue;
                foreach (string v in term.Variables)
                {
                    if (kinds[IndexOf(predictors, v)] != VariableKind.Categorical) continue;
                    var rest = new FormulaTerm(term.Variables.Where(x => !string.Equals(x, v, StringComparison.Ordinal)).ToArray());
                    if (!formula.Terms.Contains(rest))
                        throw new DesignMatrixException(
                            $"Interaction '{term.Name}' needs the lower-order term '{rest.Name}' in the model. Write it with '*' (e.g. {string.Join("*", term.Variables)}) or add '{rest.Name}'.");
                }
            }
        }

        private static int IndexOf(IReadOnlyList<string> names, string name)
        {
            for (int i = 0; i < names.Count; i++)
                if (string.Equals(names[i], name, StringComparison.Ordinal)) return i;
            return -1;
        }

        // 선택 열 하나. 열이 없으면(col<0) 통과. 노출 열은 ln(값)을 돌려주며 양수여야 한다. 해석 불가·결측이면 행 삭제.
        private static bool TryExtra(string[] row, int col, bool logOfValue, out double value)
        {
            value = 0;
            if (col < 0) return true;
            if (col >= row.Length || !StatValue.TryNumber(row[col], out double v)) return false;
            if (logOfValue)
            {
                if (!(v > 0)) return false;
                v = Math.Log(v);
            }
            value = v;
            return true;
        }

        private static bool TryParseRow(string[] row, int responseCol, int[] predictorCols, VariableKind[] kinds,
            bool binary, double[] num, string[] cat, out double yNum, out string? yCat)
        {
            yNum = 0; yCat = null;
            if (responseCol >= row.Length) return false;
            string yRaw = row[responseCol];
            if (binary)
            {
                if (StatValue.IsMissing(yRaw)) return false;
                yCat = yRaw.Trim();
            }
            else if (!StatValue.TryNumber(yRaw, out yNum)) return false;

            for (int k = 0; k < predictorCols.Length; k++)
            {
                int c = predictorCols[k];
                if (c >= row.Length) return false;
                if (kinds[k] == VariableKind.Numeric)
                {
                    if (!StatValue.TryNumber(row[c], out num[k])) return false;
                }
                else
                {
                    if (StatValue.IsMissing(row[c])) return false;
                    cat[k] = row[c].Trim();
                }
            }
            return true;
        }

        private static IReadOnlyList<string> MapBinary(string response, List<string> levelNames, List<int> codes,
            string? eventLevel, double[] y)
        {
            if (levelNames.Count != 2)
                throw new DesignMatrixException(levelNames.Count < 2
                    ? $"Response '{response}' has only one value in the complete rows; a binary outcome needs two."
                    : $"Response '{response}' has {levelNames.Count} distinct values; a binary outcome is required.");

            // 0/1(또는 0.0/1.0)이면 수치 의미를 따른다.
            string[] ordered;
            if (levelNames.All(l => double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && (d == 0 || d == 1)))
                ordered = levelNames.OrderBy(l => double.Parse(l, CultureInfo.InvariantCulture)).ToArray();
            else
                ordered = StatValue.SortLevels(levelNames).ToArray();

            if (eventLevel is not null)
            {
                int e = Array.FindIndex(ordered, l => string.Equals(l, eventLevel, StringComparison.Ordinal));
                if (e < 0) throw new DesignMatrixException($"Event level '{eventLevel}' does not occur in '{response}'.");
                if (e == 0) ordered = new[] { ordered[1], ordered[0] };
            }
            int oneCode = levelNames.IndexOf(ordered[1]);
            for (int i = 0; i < codes.Count; i++) y[i] = codes[i] == oneCode ? 1.0 : 0.0;
            return ordered;
        }

        /// <summary>변수별 압축 열 저장소.</summary>
        private sealed class ColumnStore
        {
            private readonly VariableKind[] _kinds;
            private readonly List<double>?[] _num;
            private readonly List<int>?[] _codes;
            private readonly Dictionary<string, int>?[] _levels;
            private readonly List<string>?[] _levelNames;
            private readonly int _maxLevels;
            private readonly IReadOnlyList<string> _names;

            public ColumnStore(int count, VariableKind[] kinds, int maxLevels, IReadOnlyList<string> names)
            {
                _kinds = kinds;
                _maxLevels = maxLevels;
                _names = names;
                _num = new List<double>?[count];
                _codes = new List<int>?[count];
                _levels = new Dictionary<string, int>?[count];
                _levelNames = new List<string>?[count];
                for (int k = 0; k < count; k++)
                {
                    if (kinds[k] == VariableKind.Numeric) _num[k] = new List<double>();
                    else
                    {
                        _codes[k] = new List<int>();
                        _levels[k] = new Dictionary<string, int>(StringComparer.Ordinal);
                        _levelNames[k] = new List<string>();
                    }
                }
            }

            public long BytesPerRow => _kinds.Sum(k => k == VariableKind.Numeric ? 8L : 4L);

            public void Add(double[] num, string[] cat)
            {
                for (int k = 0; k < _kinds.Length; k++)
                {
                    if (_kinds[k] == VariableKind.Numeric) { _num[k]!.Add(num[k]); continue; }
                    var map = _levels[k]!;
                    if (!map.TryGetValue(cat[k], out int code))
                    {
                        code = map.Count;
                        if (code >= _maxLevels)
                            throw new DesignMatrixException(
                                $"'{_names[k]}' has more than {_maxLevels:N0} levels. Use it as numeric, filter rows, or choose another column.");
                        map[cat[k]] = code;
                        _levelNames[k]!.Add(cat[k]);
                    }
                    _codes[k]!.Add(code);
                }
            }

            public double Numeric(int k, int row) => _num[k]![row];
            public int Code(int k, int row) => _codes[k]![row];
            public List<string> LevelNames(int k) => _levelNames[k]!;
        }
    }
}
