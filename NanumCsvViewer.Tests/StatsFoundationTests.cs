using NanumCsvViewer.Csv;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    // 고급 통계(이슈 #27) 공용 계층: 모형식 파서 · 설계행렬 · 특성행렬 · 최소제곱.
    public class StatsFoundationTests
    {
        // ---------------------------------------------------------------- 모형식

        [Fact]
        public void Star_expands_to_main_effects_and_interaction_in_order()
        {
            var f = ModelFormula.Parse("y ~ a * b");
            Assert.Equal("y", f.Response);
            Assert.Equal(new[] { "a", "b", "a:b" }, f.Terms.Select(t => t.Name));
            Assert.True(f.Intercept);
        }

        [Fact]
        public void Three_way_star_contains_every_subset_once()
        {
            var f = ModelFormula.Parse("y ~ a*b*c + a:b");
            Assert.Equal(7, f.Terms.Count);
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 2, 3 }, f.Terms.Select(t => t.Order));
        }

        [Fact]
        public void Quoted_names_categorical_marker_and_intercept_removal()
        {
            var f = ModelFormula.Parse("[blood pressure] ~ C(`site id`) + age - 1");
            Assert.Equal("blood pressure", f.Response);
            Assert.Equal(new[] { "site id", "age" }, f.PredictorVariables);
            Assert.Contains("site id", f.ForcedCategorical);
            Assert.False(f.Intercept);
            Assert.Equal("[blood pressure] ~ C([site id]) + age + 0", f.ToString());
        }

        [Fact]
        public void Minus_removes_a_previously_added_term()
        {
            var f = ModelFormula.Parse("y ~ a*b - a:b");
            Assert.Equal(new[] { "a", "b" }, f.Terms.Select(t => t.Name));
        }

        [Theory]
        [InlineData("y ~ log(x)")]
        [InlineData("y ~ (a + b)")]
        [InlineData("y ~ x + y")]
        [InlineData("~ x")]
        [InlineData("y x")]
        [InlineData("y ~ x +")]
        [InlineData("y ~ 2")]
        public void Invalid_formulas_raise_parse_errors(string text)
        {
            Assert.Throws<FormulaParseException>(() => ModelFormula.Parse(text));
        }

        // ---------------------------------------------------------------- 설계행렬

        private static readonly string[] Headers = { "y", "x", "g" };

        // 참조: statsmodels 0.14 smf.ols("y ~ x * C(g)", df).fit() — params / bse / ssr
        private static List<string[]> InteractionData() => new()
        {
            new[] { "2.3", "0.0", "b" }, new[] { "2.5", "0.75", "a" }, new[] { "3.2", "1.5", "c" },
            new[] { "5.4", "1.5", "b" }, new[] { "5.6", "2.25", "a" }, new[] { "5.8", "3.0", "c" },
            new[] { "8.5", "3.0", "b" }, new[] { "8.7", "3.75", "a" }, new[] { "8.9", "4.5", "c" },
            new[] { "11.6", "4.5", "b" }, new[] { "11.3", "5.25", "a" }, new[] { "12.0", "6.0", "c" },
        };

        private static VariableKind Kind(int c) => c == 2 ? VariableKind.Categorical : VariableKind.Numeric;

        [Fact]
        public void Interaction_design_matches_statsmodels_fit()
        {
            var dm = DesignMatrixBuilder.Build(InteractionData(), Headers, ModelFormula.Parse("y ~ x * C(g)"), Kind);
            Assert.Equal(new[] { "Intercept", "x", "g[T.b]", "g[T.c]", "x:g[T.b]", "x:g[T.c]" }, dm.ColumnNames);
            Assert.Equal(new[] { "a", "b", "c" }, dm.Factors["g"].Levels);
            // 2번째 행(g=a 기준 수준): 더미·상호작용 0
            Assert.Equal(new[] { 1.0, 0.75, 0, 0, 0, 0 }, Enumerable.Range(0, 6).Select(j => dm.X[1, j]));

            var fit = LeastSquares.Fit(dm.X, dm.Y);
            var expectedParams = new Dictionary<string, double>
            {
                ["Intercept"] = 1.1249999999999973, ["g[T.b]"] = 1.1749999999999972, ["g[T.c]"] = -1.0250000000000004,
                ["x"] = 1.9666666666666683, ["x:g[T.b]"] = 0.10000000000000164, ["x:g[T.c]"] = 0,
            };
            var expectedBse = new Dictionary<string, double>
            {
                ["Intercept"] = 0.16201851746019655, ["g[T.b]"] = 0.2091650066335188, ["g[T.c]"] = 0.252487623459052,
                ["x"] = 0.04714045207910317, ["x:g[T.b]"] = 0.06666666666666661, ["x:g[T.c]"] = 0.06666666666666667,
            };
            Assert.Equal(0.14999999999999986, fit.WeightedRss, 10);
            double sigma2 = fit.WeightedRss / (dm.RowCount - fit.Rank);
            for (int j = 0; j < dm.ColumnCount; j++)
            {
                string name = dm.ColumnNames[j];
                Assert.Equal(expectedParams[name], fit.Beta[j], 9);
                Assert.Equal(expectedBse[name], Math.Sqrt(sigma2 * fit.XtWXInverse[j, j]), 9);
            }
        }

        [Fact]
        public void Missing_and_unparsable_rows_are_dropped_listwise_and_counted()
        {
            var rows = InteractionData();
            rows.Insert(3, new[] { "NA", "1", "a" });
            rows.Insert(5, new[] { "4", "abc", "b" });
            rows.Insert(7, new[] { "4", "1", "" });
            rows.Add(new[] { "4" }); // 짧은 행
            var dm = DesignMatrixBuilder.Build(rows, Headers, ModelFormula.Parse("y ~ x + C(g)"), Kind);
            Assert.Equal(12, dm.RowCount);
            Assert.Equal(4, dm.RowsDropped);
            Assert.Equal(16, dm.RowsRead);
            Assert.DoesNotContain(3, dm.ViewRows);
            Assert.Equal(4, dm.ViewRows[3]); // 삭제된 3번 행 다음 원본 인덱스
        }

        [Fact]
        public void Numeric_looking_levels_sort_numerically_with_lowest_as_reference()
        {
            var rows = new List<string[]>
            {
                new[] { "1", "0", "10" }, new[] { "2", "1", "2" }, new[] { "3", "2", "1" }, new[] { "4", "3", "10" },
            };
            var dm = DesignMatrixBuilder.Build(rows, Headers, ModelFormula.Parse("y ~ C(g)"), Kind);
            Assert.Equal(new[] { "1", "2", "10" }, dm.Factors["g"].Levels);
            Assert.Equal(new[] { "Intercept", "g[T.2]", "g[T.10]" }, dm.ColumnNames);
        }

        // 참조: statsmodels smf.ols("y ~ C(g) - 1", df).fit() — params / rsquared / rsquared_adj / fvalue / df_model
        [Fact]
        public void No_intercept_first_factor_uses_all_levels_and_counts_as_constant()
        {
            var dm = DesignMatrixBuilder.Build(InteractionData(), Headers, ModelFormula.Parse("y ~ C(g) - 1"), Kind);
            Assert.Equal(new[] { "g[a]", "g[b]", "g[c]" }, dm.ColumnNames);
            Assert.True(dm.HasImplicitConstant);
            var fit = LinearModel.Fit(dm);
            Assert.Equal(new[] { 7.0249999999999995, 6.950000000000003, 7.4750000000000005 }, fit.Beta, new Tolerance(1e-9));
            Assert.Equal(0.004747184809008487, fit.RSquared, 9);
            Assert.Equal(-0.21642010745565643, fit.AdjustedRSquared, 9);
            Assert.Equal(0.02146422628951686, fit.FStatistic, 9);
            Assert.Equal(2, fit.DfModel);
        }

        private sealed class Tolerance(double eps) : IEqualityComparer<double>
        {
            public bool Equals(double a, double b) => Math.Abs(a - b) <= eps;
            public int GetHashCode(double v) => 0;
        }

        [Fact]
        public void Binary_response_maps_sorted_levels_and_honours_event_override()
        {
            var rows = new List<string[]>
            {
                new[] { "yes", "1", "a" }, new[] { "no", "2", "a" }, new[] { "yes", "3", "b" }, new[] { "no", "4", "b" },
            };
            var f = ModelFormula.Parse("y ~ x");
            var dm = DesignMatrixBuilder.Build(rows, Headers, f, Kind, new DesignMatrixOptions { Response = ResponseKind.Binary });
            Assert.Equal(new[] { "no", "yes" }, dm.ResponseLevels);
            Assert.Equal(new[] { 1.0, 0, 1, 0 }, dm.Y);

            var flipped = DesignMatrixBuilder.Build(rows, Headers, f, Kind,
                new DesignMatrixOptions { Response = ResponseKind.Binary, BinaryEventLevel = "no" });
            Assert.Equal(new[] { "yes", "no" }, flipped.ResponseLevels);
            Assert.Equal(new[] { 0.0, 1, 0, 1 }, flipped.Y);

            rows.Add(new[] { "maybe", "5", "a" });
            Assert.Throws<DesignMatrixException>(() => DesignMatrixBuilder.Build(rows, Headers, f, Kind,
                new DesignMatrixOptions { Response = ResponseKind.Binary }));
        }

        [Fact]
        public void Collinear_column_is_aliased_not_fitted()
        {
            var headers = new[] { "y", "x", "x2" };
            var rows = Enumerable.Range(0, 10)
                .Select(i => new[] { (3 + 2.0 * i + (i % 2) * 0.1).ToString(System.Globalization.CultureInfo.InvariantCulture), i.ToString(), (2 * i).ToString() })
                .ToList();
            var dm = DesignMatrixBuilder.Build(rows, headers, ModelFormula.Parse("y ~ x + x2"), _ => VariableKind.Numeric);
            var fit = LeastSquares.Fit(dm.X, dm.Y);
            Assert.Equal(2, fit.Rank);
            Assert.Equal(new[] { false, false, true }, fit.Aliased);
            Assert.True(double.IsNaN(fit.Beta[2]));
            Assert.True(double.IsNaN(fit.XtWXInverse[2, 2]));
            Assert.False(double.IsNaN(fit.Beta[1]));
        }

        [Fact]
        public void Input_errors_are_reported_as_design_matrix_exceptions()
        {
            var rows = InteractionData();
            Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(rows, Headers, ModelFormula.Parse("y ~ missing"), Kind));
            Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(rows, Headers, ModelFormula.Parse("y ~ C(g)"), Kind,
                    new DesignMatrixOptions { MaxLevelsPerFactor = 2 }));
            var oneLevel = rows.Select(r => new[] { r[0], r[1], "a" }).ToList();
            Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(oneLevel, Headers, ModelFormula.Parse("y ~ C(g)"), Kind));
            var allMissing = rows.Select(r => new[] { "", r[1], r[2] }).ToList();
            Assert.Throws<DesignMatrixException>(() =>
                DesignMatrixBuilder.Build(allMissing, Headers, ModelFormula.Parse("y ~ x"), Kind));
        }

        [Fact]
        public void Memory_budget_is_enforced_without_partial_result()
        {
            Assert.Throws<AnalysisMemoryLimitException>(() =>
                DesignMatrixBuilder.Build(InteractionData(), Headers, ModelFormula.Parse("y ~ x * C(g)"), Kind,
                    new DesignMatrixOptions { MemoryBudgetBytes = 256 }));
        }

        // ---------------------------------------------------------------- 특성행렬 · 스케일링

        [Fact]
        public void Feature_matrix_one_hot_encodes_all_levels_and_codes_sorted_classes()
        {
            var headers = new[] { "x", "g", "label" };
            var rows = new List<string[]>
            {
                new[] { "1", "b", "pos" }, new[] { "2", "a", "neg" }, new[] { "", "a", "neg" }, new[] { "4", "c", "pos" },
            };
            var fm = FeatureMatrixBuilder.Build(rows, headers, new[] { 0, 1 },
                c => c == 0 ? VariableKind.Numeric : VariableKind.Categorical, 2, TargetKind.Categorical);
            Assert.Equal(new[] { "x", "g=a", "g=b", "g=c" }, fm.FeatureNames);
            Assert.Equal(new[] { 0, 1, 1, 1 }, fm.SourceColumns);
            Assert.Equal(new[] { "neg", "pos" }, fm.ClassNames);
            Assert.Equal(new[] { 1, 0, 1 }, fm.ClassLabels);
            Assert.Equal(1, fm.RowsDropped);
            Assert.Equal(new[] { 4.0, 0, 0, 1 }, Enumerable.Range(0, 4).Select(j => fm.X[2, j]));
        }

        [Fact]
        public void Scaler_fits_on_given_rows_only()
        {
            var x = new double[,] { { 1, 5 }, { 3, 5 }, { 100, 7 } };
            var z = FeatureScaler.Fit(x, ScalingMethod.ZScore, new[] { 0, 1 });
            Assert.Equal(2, z.Center[0], 12);
            Assert.Equal(Math.Sqrt(2), z.Scale[0], 12);
            Assert.Equal(1, z.Scale[1]); // 학습 행에서 상수 → 1
            var t = z.Transform(x);
            Assert.Equal((100 - 2) / Math.Sqrt(2), t[2, 0], 10);
            Assert.Equal(2, t[2, 1], 12);

            var mm = FeatureScaler.Fit(x, ScalingMethod.MinMax);
            var m = mm.Transform(x);
            Assert.Equal(0, m[0, 0]);
            Assert.Equal(1, m[2, 0]);
            Assert.Equal(0, m[1, 1], 12); // 열 1: 최소 5·범위 2
        }

        [Fact]
        public void Stratified_holdout_keeps_class_proportions_and_is_seeded()
        {
            var labels = Enumerable.Range(0, 100).Select(i => i < 80 ? 0 : 1).ToArray();
            var a = ClassifierEvaluation.Holdout(labels, 0.25, 7);
            var b = ClassifierEvaluation.Holdout(labels, 0.25, 7);
            Assert.Equal(a.Test, b.Test);
            Assert.Equal(20, a.Test.Count(i => labels[i] == 0));
            Assert.Equal(5, a.Test.Count(i => labels[i] == 1));
            Assert.Empty(a.Train.Intersect(a.Test));

            var folds = ClassifierEvaluation.KFold(labels, 5, 1);
            Assert.Equal(100, folds.Sum(f => f.Test.Length));
            Assert.All(folds, f => Assert.Equal(4, f.Test.Count(i => labels[i] == 1)));
        }

        [Fact]
        public void Metrics_follow_confusion_matrix_with_zero_division_as_zero()
        {
            var m = ClassifierEvaluation.Metrics(new[] { 0, 0, 1, 1, 2 }, new[] { 0, 1, 1, 1, 1 }, 3);
            Assert.Equal(3.0 / 5, m.Accuracy, 12);
            Assert.Equal(1.0, m.Precision[0]);
            Assert.Equal(0.5, m.Precision[1], 12); // 예측 1: 4건 중 2건 정답
            Assert.Equal(0, m.Precision[2]);
            Assert.Equal(0.5, m.Recall[0]);
            Assert.Equal(new long[] { 2, 2, 1 }, m.Support);
        }
    }
}
