using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Agent.Tools
{
    public enum AgentAnalysisKind { Describe, Glm, Ancova, Glzm, Logistic }

    /// <summary>csv.run_analysis 요청. 컬럼 이름은 호출자가 아직 해석하기 전의 원문이다(Run이 해석·검증).</summary>
    public sealed record AgentAnalysisRequest
    {
        public required AgentAnalysisKind Kind { get; init; }
        /// <summary>glm·glzm·logistic: "반응 ~ 항 + C(요인)" 형태의 모형식.</summary>
        public string? Formula { get; init; }
        /// <summary>describe: 대상 컬럼(없으면 수치 컬럼 전부, 최대 40).</summary>
        public IReadOnlyList<string>? Columns { get; init; }
        public string? GroupBy { get; init; }
        public string? Dependent { get; init; }
        public IReadOnlyList<string>? Factors { get; init; }
        public IReadOnlyList<string>? Covariates { get; init; }
        /// <summary>ancova: "none" | "two_way" | "all".</summary>
        public string Interactions { get; init; } = "none";
        public GlmFamily Family { get; init; } = GlmFamily.Gaussian;
        public GlmLink? Link { get; init; }
        public string? EventLevel { get; init; }
        public string? Offset { get; init; }
        public string? Exposure { get; init; }
        public string? VarianceWeights { get; init; }
        public string? FrequencyWeights { get; init; }
        public string? Trials { get; init; }
        public bool ShowWindow { get; init; } = true;
    }

    public sealed record AgentDescribeColumn(string Name, long Rows, long Missing, long NonNumeric, DescriptiveStatisticsResult? Stats);

    public sealed record AgentDescribeGroup(string? Group, long Rows, IReadOnlyList<AgentDescribeColumn> Columns);

    public sealed record AgentDescribeResult(string? GroupBy, IReadOnlyList<AgentDescribeGroup> Groups, bool GroupsTruncated, long RowsInView);

    /// <summary>
    /// 분석 결과: 모델에 줄 JSON + 결과 창을 그릴 때 쓰는 적합 객체. 창 본문은 일반 메뉴와 같은 포매터가
    /// 이 객체들로 만든다(Form1.Agent) — 모델이 보는 숫자와 사용자가 보는 숫자는 같은 적합에서 나온다.
    /// </summary>
    public sealed record AgentAnalysisOutcome(
        AgentAnalysisRequest Request,
        string Title,
        JsonObject Json,
        DesignMatrix? Design = null,
        LinearModelFit? Linear = null,
        IReadOnlyList<AnovaTerm>? Anova = null,
        AncovaResult? Ancova = null,
        MultiAncovaResult? MultiAncova = null,
        GeneralizedLinearFit? Glm = null,
        AgentDescribeResult? Describe = null);

    /// <summary>현재 뷰 행으로 분석을 실행해 구조화 JSON을 만든다. WinForms 의존 없음 — 작업 스레드에서 호출한다.</summary>
    public static class AgentAnalysis
    {
        public const int MaxDescribeColumns = 40;
        public const int MaxDescribeGroups = 30;
        public const int MaxCoefficients = 200;
        public const int MaxPairwiseRows = 100;
        public const int MaxLevelsListed = 100;
        private const long DescribeValueBudget = 20_000_000;

        public static GlmLink DefaultLink(GlmFamily family) => family switch
        {
            GlmFamily.Binomial => GlmLink.Logit,
            GlmFamily.Poisson => GlmLink.Log,
            GlmFamily.Gamma => GlmLink.Inverse,
            _ => GlmLink.Identity,
        };

        public static AgentAnalysisOutcome Run(AgentAnalysisRequest request, AgentViewData data, CancellationToken cancellation)
        {
            return request.Kind switch
            {
                AgentAnalysisKind.Describe => RunDescribe(request, data, cancellation),
                AgentAnalysisKind.Glm => RunGlm(request, data, cancellation),
                AgentAnalysisKind.Ancova => RunAncova(request, data, cancellation),
                AgentAnalysisKind.Glzm => RunGlzm(request, data, cancellation, logistic: false),
                AgentAnalysisKind.Logistic => RunGlzm(request, data, cancellation, logistic: true),
                _ => throw new AgentToolException("Unknown analysis kind."),
            };
        }

        private static Func<int, VariableKind> KindOf(AgentViewData data)
            => c => c >= 0 && c < data.Types.Count && data.Types[c].IsNumeric() ? VariableKind.Numeric : VariableKind.Categorical;

        private static ModelFormula ParseFormula(AgentAnalysisRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Formula))
                throw new AgentToolException("'formula' is required, e.g. \"y ~ x1 + C(group)\" (wrap factors in C(); names with spaces in [brackets]).");
            try { return ModelFormula.Parse(request.Formula); }
            catch (FormulaParseException ex) { throw new AgentToolException("Invalid formula: " + ex.Message); }
        }

        // ------------------------------------------------------------------ describe

        private static AgentAnalysisOutcome RunDescribe(AgentAnalysisRequest request, AgentViewData data, CancellationToken ct)
        {
            var headers = data.Headers;
            List<int> cols;
            if (request.Columns is { Count: > 0 })
                cols = ColumnNames.ResolveMany(headers, request.Columns, "columns");
            else
                cols = Enumerable.Range(0, headers.Count).Where(c => data.Types[c].IsNumeric()).Take(MaxDescribeColumns).ToList();
            if (cols.Count == 0)
                throw new AgentToolException("No numeric columns to describe. Pass 'columns' explicitly.");
            if (cols.Count > MaxDescribeColumns)
                throw new AgentToolException($"At most {MaxDescribeColumns} columns per describe request (got {cols.Count}).");
            int groupCol = request.GroupBy is { Length: > 0 } g ? ColumnNames.Resolve(headers, g, "group_by") : -1;
            if (groupCol >= 0 && cols.Contains(groupCol))
                throw new AgentToolException("'group_by' must not also be one of the described columns.");

            long n = data.Rows.Count;
            if (n * cols.Count > DescribeValueBudget)
                throw new AgentToolException($"Too many values ({n:N0} rows × {cols.Count} columns). Filter the rows or describe fewer columns.");

            // 그룹 키 → 컬럼별 수치 목록
            var groupKeys = new List<string>();
            var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var groupRows = new List<long>();
            var values = new List<List<double>[]>();
            var missing = new List<long[]>();
            var nonNumeric = new List<long[]>();

            int Slot(string key)
            {
                if (groupIndex.TryGetValue(key, out int i)) return i;
                i = groupKeys.Count;
                groupIndex[key] = i;
                groupKeys.Add(key);
                groupRows.Add(0);
                values.Add(cols.Select(_ => new List<double>()).ToArray());
                missing.Add(new long[cols.Count]);
                nonNumeric.Add(new long[cols.Count]);
                return i;
            }
            if (groupCol < 0) Slot("");

            long rowNo = 0;
            foreach (var row in data.Rows)
            {
                if ((rowNo++ & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                int slot = 0;
                if (groupCol >= 0)
                {
                    string raw = groupCol < row.Length ? row[groupCol] : "";
                    slot = Slot(StatValue.IsMissing(raw) ? "(missing)" : raw.Trim());
                }
                groupRows[slot]++;
                for (int k = 0; k < cols.Count; k++)
                {
                    int c = cols[k];
                    string v = c < row.Length ? row[c] : "";
                    if (StatValue.IsMissing(v)) { missing[slot][k]++; continue; }
                    if (NumericAffix.TryParseNumber(v, out double d) && double.IsFinite(d)) values[slot][k].Add(d);
                    else nonNumeric[slot][k]++;
                }
            }

            var order = Enumerable.Range(0, groupKeys.Count).ToList();
            bool truncated = false;
            if (groupCol >= 0)
            {
                if (order.Count > MaxDescribeGroups)
                {
                    order = order.OrderByDescending(i => groupRows[i]).ThenBy(i => groupKeys[i], StringComparer.Ordinal).Take(MaxDescribeGroups).ToList();
                    truncated = true;
                }
                var sortedNames = StatValue.SortLevels(order.Select(i => groupKeys[i]));
                order = sortedNames.Select(nm => order.First(i => groupKeys[i] == nm)).ToList();
            }

            var groups = new List<AgentDescribeGroup>();
            foreach (int gi in order)
            {
                var columns = new List<AgentDescribeColumn>();
                for (int k = 0; k < cols.Count; k++)
                    columns.Add(new AgentDescribeColumn(headers[cols[k]], groupRows[gi], missing[gi][k], nonNumeric[gi][k],
                        CsvStatistics.Describe(values[gi][k])));
                groups.Add(new AgentDescribeGroup(groupCol >= 0 ? groupKeys[gi] : null, groupRows[gi], columns));
            }
            var result = new AgentDescribeResult(groupCol >= 0 ? headers[groupCol] : null, groups, truncated, n);

            var json = new JsonObject { ["kind"] = "describe", ["rows_in_view"] = n };
            if (groupCol >= 0)
            {
                json["group_by"] = headers[groupCol];
                var ga = new JsonArray();
                foreach (var grp in groups)
                    ga.Add(new JsonObject { ["group"] = grp.Group, ["rows"] = grp.Rows, ["columns"] = DescribeColumnsJson(grp.Columns) });
                json["groups"] = ga;
                if (truncated) json["groups_truncated"] = $"Only the {MaxDescribeGroups} largest groups are listed.";
            }
            else
            {
                json["columns"] = DescribeColumnsJson(groups[0].Columns);
            }
            return new AgentAnalysisOutcome(request, "Descriptive Statistics", json, Describe: result);
        }

        private static JsonArray DescribeColumnsJson(IReadOnlyList<AgentDescribeColumn> columns)
        {
            var arr = new JsonArray();
            foreach (var c in columns)
            {
                var o = new JsonObject { ["name"] = c.Name, ["missing"] = c.Missing, ["non_numeric"] = c.NonNumeric };
                if (c.Stats is { } s)
                {
                    o["n"] = s.Count;
                    o["mean"] = ToolJson.Num(s.Mean);
                    o["sd"] = ToolJson.Num(s.StandardDeviation);
                    o["se"] = ToolJson.Num(s.StandardError);
                    o["ci95_low"] = ToolJson.Num(s.ConfidenceIntervalLow);
                    o["ci95_high"] = ToolJson.Num(s.ConfidenceIntervalHigh);
                    o["min"] = ToolJson.Num(s.Min);
                    o["q1"] = ToolJson.Num(s.Q1);
                    o["median"] = ToolJson.Num(s.Median);
                    o["q3"] = ToolJson.Num(s.Q3);
                    o["max"] = ToolJson.Num(s.Max);
                    o["skewness"] = ToolJson.Num(s.Skewness);
                    o["excess_kurtosis"] = ToolJson.Num(s.ExcessKurtosis);
                }
                else o["n"] = 0;
                arr.Add(o);
            }
            return arr;
        }

        /// <summary>describe 결과 창 본문(일반 메뉴의 기술통계와 같은 항목).</summary>
        public static string DescribeText(AgentDescribeResult result, bool korean)
        {
            string L(string en, string ko) => korean ? ko : en;
            string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.AppendLine(L($"Scope: current view ({result.RowsInView:N0} rows)", $"분석 범위: 현재 뷰({result.RowsInView:N0}행)"));
            sb.AppendLine();
            foreach (var group in result.Groups)
            {
                if (group.Group is not null)
                {
                    sb.AppendLine($"═ {result.GroupBy} = {group.Group}  ({group.Rows:N0} {L("rows", "행")})");
                    sb.AppendLine();
                }
                foreach (var c in group.Columns)
                {
                    sb.AppendLine(c.Name);
                    sb.AppendLine(new string('─', 44));
                    if (c.Stats is not { } d) { sb.AppendLine(L("No numeric values.", "수치 값이 없습니다.")); sb.AppendLine(); continue; }
                    sb.AppendLine($"N (valid)    {d.Count:N0}");
                    sb.AppendLine(L($"missing      {c.Missing:N0}", $"결측         {c.Missing:N0}"));
                    if (c.NonNumeric > 0) sb.AppendLine(L($"non-numeric  {c.NonNumeric:N0}", $"비수치       {c.NonNumeric:N0}"));
                    sb.AppendLine($"mean         {G(d.Mean)}");
                    sb.AppendLine($"sd           {G(d.StandardDeviation)}");
                    sb.AppendLine($"se           {G(d.StandardError)}");
                    sb.AppendLine($"95% CI       [{G(d.ConfidenceIntervalLow)}, {G(d.ConfidenceIntervalHigh)}]");
                    sb.AppendLine($"min          {G(d.Min)}");
                    sb.AppendLine($"q1           {G(d.Q1)}");
                    sb.AppendLine($"median       {G(d.Median)}");
                    sb.AppendLine($"q3           {G(d.Q3)}");
                    sb.AppendLine($"max          {G(d.Max)}");
                    if (!double.IsNaN(d.Skewness)) sb.AppendLine($"skewness     {d.Skewness:0.0000}");
                    if (!double.IsNaN(d.ExcessKurtosis)) sb.AppendLine($"kurtosis     {d.ExcessKurtosis:0.0000}");
                    sb.AppendLine();
                }
            }
            if (result.GroupsTruncated)
                sb.AppendLine(L($"Only the {MaxDescribeGroups} largest groups are shown.", $"가장 큰 {MaxDescribeGroups}개 그룹만 표시합니다."));
            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ glm

        private static AgentAnalysisOutcome RunGlm(AgentAnalysisRequest request, AgentViewData data, CancellationToken ct)
        {
            var formula = ParseFormula(request);
            var kindOf = KindOf(data);
            LinearModel.EnsureNumericResponse(data.Headers, formula, kindOf);
            var dm = DesignMatrixBuilder.Build(data.Rows, data.Headers, formula, kindOf, cancellation: ct);
            var fit = LinearModel.Fit(dm, ct);
            var anova = LinearModel.TypeII(dm, fit, ct);

            var json = new JsonObject { ["kind"] = "glm", ["model"] = "OLS linear model, treatment (dummy) coding, Type II ANOVA" };
            AddDesign(json, dm);
            json["fit"] = LinearFitJson(fit);
            json["coefficients"] = CoefficientsJson(fit, out bool truncated);
            if (truncated) json["coefficients_truncated"] = $"Only the first {MaxCoefficients} coefficients are listed.";
            json["anova_type2"] = AnovaJson(anova);
            AddLinearNotes(json, fit);
            return new AgentAnalysisOutcome(request, "General Linear Model (GLM)", json, Design: dm, Linear: fit, Anova: anova);
        }

        // ------------------------------------------------------------------ ancova

        private static AgentAnalysisOutcome RunAncova(AgentAnalysisRequest request, AgentViewData data, CancellationToken ct)
        {
            var headers = data.Headers;
            if (string.IsNullOrWhiteSpace(request.Dependent)) throw new AgentToolException("'dependent' is required for ancova.");
            if (request.Factors is not { Count: > 0 }) throw new AgentToolException("'factors' needs at least one categorical column for ancova.");
            if (request.Covariates is not { Count: > 0 }) throw new AgentToolException("'covariates' needs at least one numeric column for ancova.");

            int dep = ColumnNames.Resolve(headers, request.Dependent, "dependent");
            var factorCols = ColumnNames.ResolveMany(headers, request.Factors, "factors");
            var covCols = ColumnNames.ResolveMany(headers, request.Covariates, "covariates");
            if (!data.Types[dep].IsNumeric()) throw new AgentToolException($"Dependent '{headers[dep]}' must be numeric (its type is {data.Types[dep].DisplayName()}).");
            foreach (int c in covCols)
                if (!data.Types[c].IsNumeric()) throw new AgentToolException($"Covariate '{headers[c]}' must be numeric (its type is {data.Types[c].DisplayName()}).");
            if (factorCols.Contains(dep)) throw new AgentToolException("A factor must be a different column from the dependent variable.");
            if (covCols.Contains(dep) || covCols.Any(factorCols.Contains))
                throw new AgentToolException("A covariate cannot also be the dependent variable or a factor.");

            var factorNames = factorCols.Select(c => headers[c]).ToHashSet(StringComparer.Ordinal);
            var predictors = factorCols.Select(c => headers[c]).Concat(covCols.Select(c => headers[c])).ToList();
            string formulaText = FormulaText.MainEffects(headers[dep], predictors, name => factorNames.Contains(name));

            string interactions = request.Interactions;
            if (interactions != "none" && factorCols.Count >= 2)
            {
                var fnames = factorCols.Select(c => $"C({FormulaText.QuoteName(headers[c])})").ToList();
                int maxOrder = interactions == "two_way" ? 2 : fnames.Count;
                var extra = new List<string>();
                for (int mask = 1; mask < (1 << fnames.Count); mask++)
                {
                    int bits = System.Numerics.BitOperations.PopCount((uint)mask);
                    if (bits < 2 || bits > maxOrder) continue;
                    extra.Add(string.Join(":", Enumerable.Range(0, fnames.Count).Where(k => (mask & (1 << k)) != 0).Select(k => fnames[k])));
                }
                formulaText += " + " + string.Join(" + ", extra);
            }
            var formula = ModelFormula.Parse(formulaText);
            var kindOf = KindOf(data);
            LinearModel.EnsureNumericResponse(headers, formula, kindOf);
            var dm = DesignMatrixBuilder.Build(data.Rows, headers, formula, kindOf, cancellation: ct);

            var json = new JsonObject { ["kind"] = "ancova", ["model"] = "ANCOVA, treatment coding, Type II SS, adjusted means at covariate means" };
            AddDesign(json, dm);

            if (factorNames.Count == 1)
            {
                var r = LinearModel.Ancova(dm, factorNames.First(), ct);
                json["fit"] = LinearFitJson(r.Additive);
                json["anova_type2"] = AnovaJson(r.TypeII);
                json["slopes_homogeneity"] = NestedJson(r.Slopes);
                json["covariate_means"] = CovariateMeans(r.Covariates, r.CovariateMeans);
                json["adjusted_means"] = new JsonArray(new JsonObject
                {
                    ["factor"] = factorNames.First(),
                    ["levels"] = AdjustedMeansJson(r.AdjustedMeans),
                    ["pairwise"] = PairwiseJson(r.Pairwise),
                });
                json["coefficients"] = CoefficientsJson(r.Additive, out bool t1);
                if (t1) json["coefficients_truncated"] = $"Only the first {MaxCoefficients} coefficients are listed.";
                AddLinearNotes(json, r.Additive);
                return new AgentAnalysisOutcome(request, "ANCOVA", json, Design: dm, Linear: r.Additive, Anova: r.TypeII, Ancova: r);
            }

            var m = LinearModel.AncovaMulti(dm, ct);
            json["fit"] = LinearFitJson(m.Additive);
            json["anova_type2"] = AnovaJson(m.TypeII);
            var slopes = new JsonArray();
            foreach (var f in m.Factors)
            {
                var o = NestedJson(f.Slopes);
                o["factor"] = f.Factor;
                slopes.Add(o);
            }
            json["slopes_homogeneity_by_factor"] = slopes;
            json["slopes_homogeneity_all"] = NestedJson(m.AllSlopes);
            json["covariate_means"] = CovariateMeans(m.Covariates, m.CovariateMeans);
            var means = new JsonArray();
            foreach (var f in m.Factors)
                means.Add(new JsonObject
                {
                    ["factor"] = f.Factor,
                    ["levels"] = AdjustedMeansJson(f.AdjustedMeans),
                    ["pairwise"] = PairwiseJson(f.Pairwise),
                });
            json["adjusted_means"] = means;
            if (m.Cells.Count > 0)
            {
                var cells = new JsonArray();
                foreach (var cell in m.Cells)
                    cells.Add(new JsonObject { ["term"] = cell.Term, ["cells"] = AdjustedMeansJson(cell.AdjustedMeans) });
                json["interaction_cell_means"] = cells;
            }
            json["coefficients"] = CoefficientsJson(m.Additive, out bool t2);
            if (t2) json["coefficients_truncated"] = $"Only the first {MaxCoefficients} coefficients are listed.";
            AddLinearNotes(json, m.Additive);
            return new AgentAnalysisOutcome(request, "ANCOVA", json, Design: dm, Linear: m.Additive, Anova: m.TypeII, MultiAncova: m);
        }

        // ------------------------------------------------------------------ glzm / logistic

        private static AgentAnalysisOutcome RunGlzm(AgentAnalysisRequest request, AgentViewData data, CancellationToken ct, bool logistic)
        {
            var formula = ParseFormula(request);
            var headers = data.Headers;
            var kindOf = KindOf(data);

            GlmFamily family = logistic ? GlmFamily.Binomial : request.Family;
            GlmLink link = logistic ? GlmLink.Logit : request.Link ?? DefaultLink(family);
            if (!GeneralizedLinearModel.IsValidLink(family, link))
                throw new AgentToolException($"Link '{link}' is not valid for the {family} family. Valid: {string.Join(", ", ValidLinks(family))}.");
            string? eventLevel = string.IsNullOrWhiteSpace(request.EventLevel) ? null : request.EventLevel.Trim();

            string? Canon(string? name, string arg) => name is { Length: > 0 } ? headers[ColumnNames.Resolve(headers, name, arg)] : null;
            string? offset = logistic ? null : Canon(request.Offset, "offset");
            string? exposure = logistic ? null : Canon(request.Exposure, "exposure");
            string? varW = logistic ? null : Canon(request.VarianceWeights, "var_weights");
            string? freqW = logistic ? null : Canon(request.FrequencyWeights, "freq_weights");
            string? trials = logistic ? null : Canon(request.Trials, "trials");
            if (trials is not null && family != GlmFamily.Binomial) throw new AgentToolException("'trials' applies only to the binomial family.");
            if (varW is not null && family == GlmFamily.Binomial) throw new AgentToolException("'var_weights' is not allowed with the binomial family (use freq_weights or trials).");

            bool binaryResponse = family == GlmFamily.Binomial && trials is null;
            var options = new DesignMatrixOptions
            {
                Response = binaryResponse ? ResponseKind.Binary : ResponseKind.Numeric,
                BinaryEventLevel = binaryResponse ? eventLevel : null,
                OffsetColumn = offset,
                ExposureColumn = exposure,
                VarianceWeightColumn = varW,
                FrequencyWeightColumn = freqW,
                TrialsColumn = trials,
            };
            var design = DesignMatrixBuilder.Build(data.Rows, headers, formula, kindOf, options, ct);
            var extras = design.GlmExtras;
            bool logisticExtras = family == GlmFamily.Binomial && link == GlmLink.Logit && extras is null;
            var fit = GeneralizedLinearModel.Fit(design, family, link, logisticExtras, ct, extras);

            var json = new JsonObject
            {
                ["kind"] = logistic ? "logistic" : "glzm",
                ["model"] = "Generalized linear model (IRLS), Wald z tests and normal 95% CI",
                ["family"] = family.ToString().ToLowerInvariant(),
                ["link"] = link.ToString().ToLowerInvariant(),
            };
            AddDesign(json, design);
            if (design.ResponseLevels is { Count: 2 } levels)
            {
                json["response_reference_level"] = levels[0];
                json["response_event_level"] = levels[1];
            }
            var inputs = new List<string>();
            if (fit.HasOffset) inputs.Add("offset");
            if (fit.HasTrials) inputs.Add("trials");
            if (fit.HasVarianceWeights) inputs.Add("variance_weights");
            if (fit.HasFrequencyWeights) inputs.Add("frequency_weights");
            if (inputs.Count > 0) json["extra_inputs"] = ToolJson.Strings(inputs);

            json["fit"] = new JsonObject
            {
                ["n"] = fit.N,
                ["rank"] = fit.Rank,
                ["df_model"] = fit.DfModel,
                ["df_resid"] = fit.DfResid,
                ["converged"] = fit.Converged,
                ["iterations"] = fit.Iterations,
                ["scale"] = ToolJson.Num(fit.Scale),
                ["deviance"] = ToolJson.Num(fit.Deviance),
                ["null_deviance"] = ToolJson.Num(fit.NullDeviance),
                ["pearson_chi2"] = ToolJson.Num(fit.PearsonChi2),
                ["log_likelihood"] = ToolJson.Num(fit.LogLikelihood),
                ["aic"] = ToolJson.Num(fit.Aic),
                ["bic"] = ToolJson.Num(fit.Bic),
                ["lr_chi2"] = ToolJson.Num(fit.LikelihoodRatio),
                ["lr_p"] = ToolJson.Num(fit.LikelihoodRatioP),
            };

            var coef = new JsonArray();
            int shown = Math.Min(fit.Names.Length, MaxCoefficients);
            for (int j = 0; j < shown; j++)
            {
                if (fit.Aliased[j]) { coef.Add(new JsonObject { ["term"] = fit.Names[j], ["aliased"] = true }); continue; }
                var o = new JsonObject
                {
                    ["term"] = fit.Names[j],
                    ["estimate"] = ToolJson.Num(fit.Coefficients[j]),
                    ["se"] = ToolJson.Num(fit.StdErrors[j]),
                    ["z"] = ToolJson.Num(fit.Z[j]),
                    ["p"] = ToolJson.Num(fit.PValues[j]),
                    ["ci_low"] = ToolJson.Num(fit.CiLow[j]),
                    ["ci_high"] = ToolJson.Num(fit.CiHigh[j]),
                };
                if (fit.Logistic is { } lg)
                {
                    o["odds_ratio"] = ToolJson.Num(lg.OddsRatio[j]);
                    o["or_ci_low"] = ToolJson.Num(lg.OddsRatioCiLow[j]);
                    o["or_ci_high"] = ToolJson.Num(lg.OddsRatioCiHigh[j]);
                }
                coef.Add(o);
            }
            json["coefficients"] = coef;
            if (fit.Names.Length > shown) json["coefficients_truncated"] = $"Only the first {MaxCoefficients} coefficients are listed.";

            if (fit.Logistic is { } log)
            {
                json["classification"] = new JsonObject
                {
                    ["threshold"] = ToolJson.Num(log.Threshold),
                    ["tp"] = log.TruePositive,
                    ["fp"] = log.FalsePositive,
                    ["tn"] = log.TrueNegative,
                    ["fn"] = log.FalseNegative,
                    ["accuracy"] = ToolJson.Num(log.Accuracy),
                    ["sensitivity"] = ToolJson.Num(log.Sensitivity),
                    ["specificity"] = ToolJson.Num(log.Specificity),
                    ["auc"] = ToolJson.Num(log.Auc),
                    ["mcfadden_r2"] = ToolJson.Num(log.McFaddenRSquared),
                    ["hosmer_lemeshow"] = new JsonObject
                    {
                        ["chi2"] = ToolJson.Num(log.HosmerLemeshow.ChiSquare),
                        ["groups"] = log.HosmerLemeshow.Groups,
                        ["df"] = log.HosmerLemeshow.DegreesOfFreedom,
                        ["p"] = ToolJson.Num(log.HosmerLemeshow.PValue),
                        ["reliable"] = log.HosmerLemeshow.Reliable,
                    },
                };
            }
            if (fit.Diagnostics.Count > 0)
                json["warnings"] = ToolJson.Strings(fit.Diagnostics.Select(d => d.ToString()));
            if (!fit.Converged)
                json["warning_text"] = "NOT CONVERGED: the estimates are not valid maximum-likelihood estimates. Do not interpret them.";

            string title = logistic ? "Logistic Regression" : "Generalized Linear Model";
            return new AgentAnalysisOutcome(request, title, json, Design: design, Glm: fit);
        }

        private static IEnumerable<GlmLink> ValidLinks(GlmFamily family)
            => Enum.GetValues<GlmLink>().Where(l => GeneralizedLinearModel.IsValidLink(family, l));

        // ------------------------------------------------------------------ JSON 조각

        private static void AddDesign(JsonObject json, DesignMatrix dm)
        {
            json["formula"] = dm.Formula.ToString();
            json["rows_in_view"] = dm.RowsRead;
            json["n_used"] = dm.RowCount;
            json["n_dropped"] = dm.RowsDropped;
            json["dropped_reason"] = "rows with a missing or non-numeric value in any model variable (listwise deletion)";
            var factors = new JsonArray();
            foreach (var f in dm.Factors.Values)
            {
                var levels = new JsonArray();
                foreach (string l in f.Levels.Take(MaxLevelsListed)) levels.Add(l);
                var o = new JsonObject { ["variable"] = f.Variable, ["reference_level"] = f.Levels.Count > 0 ? f.Levels[0] : null, ["levels"] = levels };
                if (f.Levels.Count > MaxLevelsListed) o["levels_truncated"] = true;
                factors.Add(o);
            }
            if (factors.Count > 0) json["factors"] = factors;
        }

        private static JsonObject LinearFitJson(LinearModelFit fit) => new()
        {
            ["n"] = fit.N,
            ["rank"] = fit.Rank,
            ["df_model"] = fit.DfModel,
            ["df_resid"] = fit.DfResidual,
            ["r2"] = ToolJson.Num(fit.RSquared),
            ["adj_r2"] = ToolJson.Num(fit.AdjustedRSquared),
            ["f"] = ToolJson.Num(fit.FStatistic),
            ["f_p"] = ToolJson.Num(fit.FPValue),
            ["residual_se"] = ToolJson.Num(fit.ResidualSe),
            ["rss"] = ToolJson.Num(fit.Rss),
            ["log_likelihood"] = ToolJson.Num(fit.LogLikelihood),
            ["aic"] = ToolJson.Num(fit.Aic),
            ["bic"] = ToolJson.Num(fit.Bic),
            ["has_intercept"] = fit.HasIntercept,
        };

        private static JsonArray CoefficientsJson(LinearModelFit fit, out bool truncated)
        {
            var arr = new JsonArray();
            int shown = Math.Min(fit.Coefficients.Count, MaxCoefficients);
            for (int i = 0; i < shown; i++)
            {
                var c = fit.Coefficients[i];
                if (c.Aliased) { arr.Add(new JsonObject { ["term"] = c.Name, ["aliased"] = true }); continue; }
                arr.Add(new JsonObject
                {
                    ["term"] = c.Name,
                    ["estimate"] = ToolJson.Num(c.Estimate),
                    ["se"] = ToolJson.Num(c.StdError),
                    ["t"] = ToolJson.Num(c.T),
                    ["p"] = ToolJson.Num(c.PValue),
                    ["ci_low"] = ToolJson.Num(c.CiLow),
                    ["ci_high"] = ToolJson.Num(c.CiHigh),
                });
            }
            truncated = fit.Coefficients.Count > shown;
            return arr;
        }

        private static JsonArray AnovaJson(IReadOnlyList<AnovaTerm> terms)
        {
            var arr = new JsonArray();
            foreach (var t in terms)
                arr.Add(new JsonObject
                {
                    ["term"] = t.Name,
                    ["ss"] = ToolJson.Num(t.SumOfSquares),
                    ["df"] = t.Df,
                    ["ms"] = ToolJson.Num(t.MeanSquare),
                    ["f"] = ToolJson.Num(t.F),
                    ["p"] = ToolJson.Num(t.PValue),
                    ["partial_eta2"] = ToolJson.Num(t.PartialEtaSquared),
                });
            return arr;
        }

        private static JsonObject NestedJson(NestedFTest t) => new()
        {
            ["f"] = ToolJson.Num(t.F),
            ["df1"] = t.DfNumerator,
            ["df2"] = t.DfDenominator,
            ["p"] = ToolJson.Num(t.PValue),
        };

        private static JsonObject CovariateMeans(IReadOnlyList<string> names, IReadOnlyList<double> means)
        {
            var o = new JsonObject();
            for (int i = 0; i < names.Count; i++) o[names[i]] = ToolJson.Num(means[i]);
            return o;
        }

        private static JsonArray AdjustedMeansJson(IReadOnlyList<AdjustedMean> means)
        {
            var arr = new JsonArray();
            foreach (var m in means.Take(MaxLevelsListed))
                arr.Add(new JsonObject
                {
                    ["level"] = m.Level,
                    ["n"] = m.Count,
                    ["mean"] = ToolJson.Num(m.Estimate),
                    ["se"] = ToolJson.Num(m.StdError),
                    ["ci_low"] = ToolJson.Num(m.CiLow),
                    ["ci_high"] = ToolJson.Num(m.CiHigh),
                });
            return arr;
        }

        private static JsonObject PairwiseJson(IReadOnlyList<AdjustedMeanDifference> pairs)
        {
            var arr = new JsonArray();
            foreach (var d in pairs.Take(MaxPairwiseRows))
                arr.Add(new JsonObject
                {
                    ["a"] = d.LevelA,
                    ["b"] = d.LevelB,
                    ["b_minus_a"] = ToolJson.Num(d.Difference),
                    ["se"] = ToolJson.Num(d.StdError),
                    ["t"] = ToolJson.Num(d.T),
                    ["p"] = ToolJson.Num(d.PValue),
                    ["p_bonferroni"] = ToolJson.Num(d.BonferroniP),
                });
            var o = new JsonObject { ["comparisons"] = pairs.Count, ["rows"] = arr };
            if (pairs.Count > MaxPairwiseRows) o["truncated"] = true;
            return o;
        }

        private static void AddLinearNotes(JsonObject json, LinearModelFit fit)
        {
            var notes = new JsonArray();
            if (fit.HasAliased)
                notes.Add("Some terms are linearly dependent (aliased) and have no estimate.");
            if (fit.Normality is { } nm)
                json["residual_normality_shapiro_wilk"] = new JsonObject
                {
                    ["w"] = ToolJson.Num(nm.W),
                    ["p"] = ToolJson.Num(nm.PValue),
                    ["n"] = nm.SampleSize,
                    ["capped_to_first_5000_residuals"] = nm.Capped,
                };
            if (fit.DfResidual <= 0) notes.Add("Residual degrees of freedom are 0: F, p-values and standard errors are not defined.");
            if (notes.Count > 0) json["notes"] = notes;
        }
    }
}
