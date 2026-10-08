using System.Reflection;
using System.Text.Json;
using NanumCsvViewer.Charting;
using NanumCsvViewer.Csv;
using NanumCsvViewer.Csv.DataQuality;
using NanumCsvViewer.Stats;

namespace NanumCsvViewer.Tests
{
    /// <summary>예산을 전역으로 바꾸는 검사(Configure)가 있으므로 다른 테스트와 겹치지 않게 혼자 실행한다.</summary>
    [CollectionDefinition("AnalysisBudget", DisableParallelization = true)]
    public sealed class AnalysisBudgetCollection { }

    // 분석 메모리 상한: 자동 = 물리 메모리의 50%(최소 512 MB, 상한 없음), 수동 = 설정 GB(물리 메모리까지).
    [Collection("AnalysisBudget")]
    public class AnalysisMemoryBudgetTests
    {
        private const long MB = 1024L * 1024;
        private const long GB = 1024L * MB;

        // ---------------------------------------------------------------- 계산식

        [Theory]
        [InlineData(1 * GB, 512 * MB)]        // 정확히 하한
        [InlineData(512 * MB, 512 * MB)]      // 절반(256 MB)이 하한보다 작다 → 하한
        [InlineData(600 * MB, 512 * MB)]
        [InlineData(2 * GB, 1 * GB)]
        [InlineData(16 * GB, 8 * GB)]
        [InlineData(64 * GB, 32 * GB)]        // 예전 4 GB 상한이 없다
        [InlineData(1024 * GB, 512 * GB)]
        public void Auto_is_half_of_physical_memory_with_a_512_MB_floor_and_no_cap(long physical, long expected)
            => Assert.Equal(expected, AnalysisMemoryBudget.ForPhysicalMemory(physical));

        [Theory]
        [InlineData(8.0, 16 * GB, 8 * GB)]
        [InlineData(16.0, 16 * GB, 16 * GB)]    // 물리 메모리와 같으면 그대로
        [InlineData(64.0, 16 * GB, 16 * GB)]    // 물리 메모리를 넘으면 물리 메모리로
        [InlineData(0.5, 16 * GB, 512 * MB)]
        [InlineData(0.1, 16 * GB, 512 * MB)]    // 하한 아래는 512 MB로
        [InlineData(1.5, 16 * GB, 1536 * MB)]
        public void Manual_value_is_clamped_to_physical_memory(double gb, long physical, long expected)
            => Assert.Equal(expected, AnalysisMemoryBudget.ForManual(gb, physical));

        [Fact]
        public void Resolve_uses_auto_unless_a_positive_manual_value_is_chosen()
        {
            const long physical = 32 * GB;
            Assert.Equal(16 * GB, AnalysisMemoryBudget.Resolve(true, 4, physical));      // 자동이면 수동 값은 무시
            Assert.Equal(4 * GB, AnalysisMemoryBudget.Resolve(false, 4, physical));
            Assert.Equal(16 * GB, AnalysisMemoryBudget.Resolve(false, 0, physical));     // 값을 안 정했으면 자동 계산
            Assert.Equal(16 * GB, AnalysisMemoryBudget.Resolve(false, double.NaN, physical));
        }

        [Fact]
        public void Physical_size_is_the_real_installed_memory_not_a_process_limit()
        {
            long physical = AnalysisMemoryBudget.PhysicalBytes;
            Assert.True(physical >= 256 * MB);
            Assert.True(AnalysisMemoryBudget.AvailableBytes <= physical);
        }

        [Fact]
        public void Current_follows_the_configured_mode()
        {
            long physical = AnalysisMemoryBudget.PhysicalBytes;
            try
            {
                AnalysisMemoryBudget.Configure(true, 0);
                Assert.Equal(AnalysisMemoryBudget.ForPhysicalMemory(physical), AnalysisMemoryBudget.Current);
                AnalysisMemoryBudget.Configure(false, 1);
                Assert.Equal(AnalysisMemoryBudget.ForManual(1, physical), AnalysisMemoryBudget.Current);
                AnalysisMemoryBudget.Configure(false, 1_000_000);
                Assert.Equal(physical, AnalysisMemoryBudget.Current);                     // 물리 메모리 이상은 못 준다
            }
            finally { AnalysisMemoryBudget.Configure(true, 0); }
        }

        // ---------------------------------------------------------------- 설정 저장

        [Fact]
        public void Settings_round_trip_and_default_to_auto()
        {
            var d = new AppSettings();
            Assert.True(d.AnalysisMemoryAuto);
            Assert.Equal(0, d.AnalysisMemoryManualGb);

            var back = JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(new AppSettings { AnalysisMemoryAuto = false, AnalysisMemoryManualGb = 12.5 }))!.Normalize();
            Assert.False(back.AnalysisMemoryAuto);
            Assert.Equal(12.5, back.AnalysisMemoryManualGb);

            var old = JsonSerializer.Deserialize<AppSettings>("""{ "Theme": "Light" }""")!.Normalize();
            Assert.True(old.AnalysisMemoryAuto);                                          // 옛 설정 파일은 자동
        }

        [Theory]
        [InlineData(-3.0, 0.0)]
        [InlineData(0.0, 0.0)]
        [InlineData(0.01, AnalysisMemoryBudget.MinimumManualGb)]
        [InlineData(2.0, 2.0)]
        [InlineData(1e12, AppSettings.MaxAnalysisMemoryGb)]
        public void Hand_edited_manual_values_are_pulled_into_range(double raw, double expected)
        {
            var s = new AppSettings { AnalysisMemoryAuto = false, AnalysisMemoryManualGb = raw }.Normalize();
            Assert.Equal(expected, s.AnalysisMemoryManualGb);
        }

        [Fact]
        public void Files_reset_restores_auto()
        {
            var s = new AppSettings { AnalysisMemoryAuto = false, AnalysisMemoryManualGb = 9, DeleteIndexOnClose = true };
            s.ResetFiles();
            Assert.True(s.AnalysisMemoryAuto);
            Assert.Equal(0, s.AnalysisMemoryManualGb);
        }

        // ---------------------------------------------------------------- 소비자가 공급자를 쓴다

        [Fact]
        public void Option_defaults_of_every_consumer_come_from_the_provider()
        {
            using (AnalysisMemoryBudget.Override(7 * MB))
            {
                Assert.Equal(7 * MB, new DesignMatrixOptions().MemoryBudgetBytes);
                Assert.Equal(7 * MB, new FeatureMatrixOptions().MemoryBudgetBytes);
                Assert.Equal(7 * MB, new GradientBoostingOptions().MemoryBudgetBytes);
                Assert.Equal(7 * MB, KaplanMeierAnalysis.MemoryBudgetBytes);
                Assert.Equal(7 * MB, new ReferentialIntegrityOptions().ParentKeyMemoryBudgetBytes);
                Assert.Equal(7 * MB, ConformanceProfileJson.DefaultReferenceBudgetBytes);
            }
            Assert.NotEqual(7 * MB, new DesignMatrixOptions().MemoryBudgetBytes);          // 범위를 벗어나면 원래대로
        }

        [Fact]
        public void Basic_analysis_snapshot_uses_the_provider()
        {
            var rows = Enumerable.Range(0, 200).Select(i => new[] { "value" + i }).ToArray();
            using (AnalysisMemoryBudget.Override(1000))
                Assert.Throws<AnalysisMemoryLimitException>(() => AnalysisSnapshot.Collect(rows, default));
            using (AnalysisMemoryBudget.Override(1 * GB))
                Assert.Equal(200, AnalysisSnapshot.Collect(rows, default).Rows.Count);
        }

        [Fact]
        public void Group_by_duplicates_and_chi_square_use_the_provider()
        {
            var rows = Enumerable.Range(0, 2000).Select(i => new[] { "k" + i, "g" + (i % 7) }).ToArray();
            var source = rows.Select((r, i) => (r, (long)i + 1)).ToList();
            using (AnalysisMemoryBudget.Override(2048))
            {
                Assert.Throws<AnalysisMemoryLimitException>(() => CsvAnalytics.GroupBy(rows, new[] { 0 }, 1, new[] { AggregationFunction.Count }));
                Assert.Throws<AnalysisMemoryLimitException>(() => CsvAnalytics.FindDuplicates(source, new[] { 0 }));
                Assert.Throws<AnalysisMemoryLimitException>(() => CsvStatistics.ChiSquare(rows.Select(r => (r[0], r[1])).ToList()));
            }
            using (AnalysisMemoryBudget.Override(1 * GB))
            {
                Assert.Equal(2000, CsvAnalytics.GroupBy(rows, new[] { 0 }, 1, new[] { AggregationFunction.Count }).Rows.Count);
            }
        }

        [Fact]
        public void Heatmap_chart_uses_the_provider()
        {
            var rows = Enumerable.Range(0, 50).Select(i => new[] { i.ToString(), (i * 2).ToString(), (i % 5).ToString() }).ToList();
            var cols = new[] { 0, 1, 2 };
            var names = new[] { "a", "b", "c" };
            using (AnalysisMemoryBudget.Override(1000))
                Assert.Throws<AnalysisMemoryLimitException>(() => ChartBuilders.CorrelationHeatmap(rows, cols, names));
            using (AnalysisMemoryBudget.Override(1 * GB))
                Assert.NotNull(ChartBuilders.CorrelationHeatmap(rows, cols, names));
        }

        [Fact]
        public void Pivot_uses_the_provider_unless_a_budget_is_passed()
        {
            var rows = Enumerable.Range(0, 10000).Select(i => new[] { i.ToString() }).ToArray();
            using (AnalysisMemoryBudget.Override(4096))
            {
                Assert.Throws<PivotMemoryLimitException>(() => CsvAnalytics.PivotTable(rows, new[] { 0 },
                    Array.Empty<int>(), 0, AggregationFunction.Count));
                // 명시한 예산이 공급자보다 우선한다(피벗 화면이 측정값 수로 나눠 넘긴다).
                Assert.Equal(10000, CsvAnalytics.PivotTable(rows, new[] { 0 }, Array.Empty<int>(), 0,
                    AggregationFunction.Count, memoryBudgetBytes: 1 * GB).RowKeys.Count);
            }
            using (AnalysisMemoryBudget.Override(1 * GB))
                Assert.Equal(10000, CsvAnalytics.PivotTable(rows, new[] { 0 }, Array.Empty<int>(), 0,
                    AggregationFunction.Count).RowKeys.Count);
        }

        private static readonly string[] Headers = { "y", "x", "g" };
        private static VariableKind Kind(int c) => c == 2 ? VariableKind.Categorical : VariableKind.Numeric;
        private static List<string[]> DesignRows() => Enumerable.Range(0, 60)
            .Select(i => new[] { (i * 0.37 + (i % 3)).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), (i % 11).ToString(), "ghi"[i % 3].ToString() })
            .ToList();

        [Fact]
        public void Design_matrix_uses_the_provider_by_default()
        {
            var formula = ModelFormula.Parse("y ~ x * C(g)");
            using (AnalysisMemoryBudget.Override(256))
                Assert.Throws<AnalysisMemoryLimitException>(() => DesignMatrixBuilder.Build(DesignRows(), Headers, formula, Kind));
            using (AnalysisMemoryBudget.Override(1 * GB))
                Assert.Equal(60, DesignMatrixBuilder.Build(DesignRows(), Headers, formula, Kind).RowCount);
        }

        [Fact]
        public void Conformance_reference_sets_use_the_provider_by_default()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ncv-budget-provider-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var sb = new System.Text.StringBuilder("id\n");
                for (int i = 0; i < 20; i++) sb.Append(i).Append('\n');
                File.WriteAllText(Path.Combine(dir, "reference.csv"), sb.ToString());
                var profile = new ConformanceProfile
                {
                    SchemaVersion = 1, Name = "t", CaseInsensitiveColumnMatch = true,
                    Columns = new[]
                    {
                        new ConformanceColumnSpec
                        {
                            Column = "ref_id",
                            ConceptRef = new ConformanceConceptRef { File = "reference.csv", KeyColumn = "id" },
                        },
                    },
                };
                var rows = new[] { new[] { "3" }, new[] { "99" } };
                var source = new QualityScanSource { Headers = new[] { "ref_id" }, RowAt = i => rows[i], RowCount = rows.Length, CoversAllRows = true };
                var options = new ConformanceRunOptions { ProfileDirectory = dir, DegreeOfParallelism = 1 };   // 예산을 따로 정하지 않았다

                using (AnalysisMemoryBudget.Override(1000))
                    Assert.Throws<ConformanceBudgetException>(() => ConformanceProfileRunner.Run(profile, source, options));
                using (AnalysisMemoryBudget.Override(1 * GB))
                    Assert.Single(ConformanceProfileRunner.Run(profile, source, options).Findings, f => f.ViolationCount == 1);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Referential_integrity_parent_keys_use_the_provider_by_default()
        {
            var parentRows = Enumerable.Range(0, 50).Select(i => new[] { new string('k', 40) + i }).ToArray();
            var parent = new ReferentialParentSource { RowAt = i => parentRows[i], RowCount = parentRows.Length, Name = "parent" };
            var child = new QualityScanSource { Headers = new[] { "id" }, RowCount = 1, RowAt = _ => new[] { "x" } };
            var options = new ReferentialIntegrityOptions { DegreeOfParallelism = 1 };

            using (AnalysisMemoryBudget.Override(64))
                Assert.Throws<ReferentialIntegrityBudgetException>(() =>
                    ReferentialIntegrityScanner.Scan(child, new[] { 0 }, parent, new[] { 0 }, new ReferentialIntegrityOptions { DegreeOfParallelism = 1 }, null, CancellationToken.None));
            using (AnalysisMemoryBudget.Override(1 * GB))
                Assert.Equal(1, ReferentialIntegrityScanner.Scan(child, new[] { 0 }, parent, new[] { 0 }, options, null, CancellationToken.None).OrphanRows);
        }

        [Fact]
        public void Survival_reads_the_provider_when_it_checks_its_budget()
        {
            var rows = Enumerable.Range(0, 10).Select(i => new[] { (i + 1).ToString(), (i % 2).ToString() }).ToList();
            using (AnalysisMemoryBudget.Override(100))
                Assert.Throws<AnalysisMemoryLimitException>(() => KaplanMeierAnalysis.FromRows(rows, 0, 1, null, null, default));
        }

        // ---------------------------------------------------------------- 설정 화면

        private static T Field<T>(object o, string name) =>
            (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    }
}
