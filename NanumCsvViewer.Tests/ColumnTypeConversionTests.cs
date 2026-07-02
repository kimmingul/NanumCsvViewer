using NanumCsvViewer.Csv;

namespace NanumCsvViewer.Tests
{
    // 이슈 #12 수동 타입 변경 규칙표(허용/제한적/차단)와 표본 검증.
    public class ColumnTypeConversionTests
    {
        // ---- 규칙표: 허용 ----

        [Fact]
        public void Int_to_float_is_allowed()
        {
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Integer, ColumnValueType.Float));
        }

        [Fact]
        public void Anything_to_string_is_allowed()
        {
            foreach (ColumnValueType from in Enum.GetValues<ColumnValueType>())
            {
                if (from == ColumnValueType.String) continue;
                Assert.Equal(TypeChangePolicy.Allowed,
                    ColumnTypeConversion.Classify(from, ColumnValueType.String));
            }
        }

        [Fact]
        public void Same_type_is_noop_allowed()
        {
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Date, ColumnValueType.Date));
        }

        [Fact]
        public void Within_numeric_family_is_allowed()
        {
            // 수치 표기(통화·퍼센트·지수)는 의미가 같아 자유 전환.
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Float, ColumnValueType.Currency));
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Percent, ColumnValueType.Scientific));
        }

        [Fact]
        public void Within_categorical_family_is_allowed()
        {
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Categorical, ColumnValueType.Identifier));
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Identifier, ColumnValueType.Ordinal));
        }

        [Fact]
        public void Empty_source_is_allowed_anywhere()
        {
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Empty, ColumnValueType.Integer));
            Assert.Equal(TypeChangePolicy.Allowed,
                ColumnTypeConversion.Classify(ColumnValueType.Empty, ColumnValueType.Date));
        }

        // ---- 규칙표: 차단 ----

        [Fact]
        public void Float_to_int_is_blocked()
        {
            // 소수부 손실 가능 → 기본 차단 (이슈 #12 규칙표)
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.Float, ColumnValueType.Integer));
        }

        [Fact]
        public void Temporal_to_numeric_and_bool_is_blocked()
        {
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.Date, ColumnValueType.Integer));
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.DateTime, ColumnValueType.Float));
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.Time, ColumnValueType.Boolean));
        }

        [Fact]
        public void Categorical_to_numeric_and_temporal_is_blocked()
        {
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.Categorical, ColumnValueType.Integer));
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.Identifier, ColumnValueType.Float));
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.Ordinal, ColumnValueType.Date));
        }

        [Fact]
        public void String_to_numeric_is_blocked()
        {
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.String, ColumnValueType.Integer));
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.String, ColumnValueType.Float));
        }

        [Fact]
        public void To_empty_is_blocked()
        {
            Assert.Equal(TypeChangePolicy.Blocked,
                ColumnTypeConversion.Classify(ColumnValueType.Integer, ColumnValueType.Empty));
        }

        // ---- 규칙표: 제한적(표본 검증) ----

        [Fact]
        public void Restricted_transitions_require_validation()
        {
            Assert.Equal(TypeChangePolicy.RequiresValidation,
                ColumnTypeConversion.Classify(ColumnValueType.Integer, ColumnValueType.Date));     // yyyyMMdd류
            Assert.Equal(TypeChangePolicy.RequiresValidation,
                ColumnTypeConversion.Classify(ColumnValueType.Integer, ColumnValueType.Boolean));  // 0/1
            Assert.Equal(TypeChangePolicy.RequiresValidation,
                ColumnTypeConversion.Classify(ColumnValueType.String, ColumnValueType.Date));
            Assert.Equal(TypeChangePolicy.RequiresValidation,
                ColumnTypeConversion.Classify(ColumnValueType.Boolean, ColumnValueType.Integer));
            Assert.Equal(TypeChangePolicy.RequiresValidation,
                ColumnTypeConversion.Classify(ColumnValueType.Categorical, ColumnValueType.Boolean));
            Assert.Equal(TypeChangePolicy.RequiresValidation,
                ColumnTypeConversion.Classify(ColumnValueType.Date, ColumnValueType.Time));        // 시간 계열 내부
            Assert.Equal(TypeChangePolicy.RequiresValidation,
                ColumnTypeConversion.Classify(ColumnValueType.Date, ColumnValueType.Categorical));
        }

        // ---- 표본 검증 ----

        [Fact]
        public void Validate_integer_counts_failures_with_examples()
        {
            var v = ColumnTypeConversion.Validate(ColumnValueType.Integer,
                new[] { "1", "2", "3.5", "abc" });
            Assert.Equal(4, v.SampleCount);
            Assert.Equal(2, v.ValidCount);
            Assert.Equal(2, v.FailCount);
            Assert.False(v.AllValid);
            Assert.Equal(new[] { "3.5", "abc" }, v.FailingExamples);
        }

        [Fact]
        public void Validate_skips_null_tokens()
        {
            var v = ColumnTypeConversion.Validate(ColumnValueType.Integer,
                new[] { "", "na", "N/A", "null", "7" });
            Assert.Equal(1, v.SampleCount);
            Assert.Equal(1, v.ValidCount);
            Assert.True(v.AllValid);
        }

        [Fact]
        public void Validate_date_target()
        {
            var v = ColumnTypeConversion.Validate(ColumnValueType.Date,
                new[] { "2024-01-02", "notadate" });
            Assert.Equal(2, v.SampleCount);
            Assert.Equal(1, v.ValidCount);
            Assert.Equal(new[] { "notadate" }, v.FailingExamples);
        }

        [Fact]
        public void Validate_time_target_rejects_pure_dates()
        {
            var v = ColumnTypeConversion.Validate(ColumnValueType.Time,
                new[] { "13:45:00", "2024-01-02" });
            Assert.Equal(1, v.ValidCount);
        }

        [Fact]
        public void Validate_boolean_target()
        {
            var v = ColumnTypeConversion.Validate(ColumnValueType.Boolean,
                new[] { "yes", "No", "TRUE", "2" });
            Assert.Equal(3, v.ValidCount);
            Assert.Equal(new[] { "2" }, v.FailingExamples);
        }

        [Fact]
        public void Validate_categorical_is_always_valid()
        {
            var v = ColumnTypeConversion.Validate(ColumnValueType.Categorical,
                new[] { "1.5", "🙂", "anything" });
            Assert.True(v.AllValid);
        }

        [Fact]
        public void Validate_limits_failing_examples()
        {
            var values = Enumerable.Range(0, 20).Select(i => $"x{i}").ToArray();
            var v = ColumnTypeConversion.Validate(ColumnValueType.Integer, values, maxExamples: 5);
            Assert.Equal(20, v.FailCount);
            Assert.Equal(5, v.FailingExamples.Count);
        }
    }
}
