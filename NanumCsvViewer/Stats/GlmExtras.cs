namespace NanumCsvViewer.Stats
{
    /// <summary>
    /// GLM 선택 입력(행마다 하나, 설계행렬 행과 같은 순서). 모두 null이면 기존 적합과 같다.
    /// 의미는 statsmodels GLM과 같다: offset은 선형 예측자에 더하고, 빈도 가중치는 행을 그 횟수만큼 반복한 것(자유도·가능도),
    /// 분산 가중치는 분산을 1/w로 두는 것(자유도는 그대로). 이항의 시행 수는 응답을 성공 횟수로 바꾸고 분산 가중치를 대신한다.
    /// </summary>
    public sealed class GlmExtras
    {
        public double[]? Offset { get; init; }
        public double[]? VarianceWeights { get; init; }
        public double[]? FrequencyWeights { get; init; }
        /// <summary>이항 전용. 설정하면 <c>y</c>는 성공 횟수(0..trials)다.</summary>
        public double[]? Trials { get; init; }

        public bool Any => Offset is not null || VarianceWeights is not null || FrequencyWeights is not null || Trials is not null;
    }
}
