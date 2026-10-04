using System.Text.Json;
using System.Text.Json.Nodes;
using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    // 컨텍스트 링 클릭: 컨텍스트·세션 토큰(get_session_stats)과 현재 모델 제공자의 요금제 한도(`omp usage --json`, 1분 캐시).
    public sealed partial class ChatController
    {
        private const int UsageCacheMs = 60_000;

        private JsonNode? _usageStats;
        private string _usageProvider = "";
        private string? _usageJson;
        private long? _usageAt;
        private bool _usageLoading;
        private string _usageError = "";

        /// <summary>지금 아는 것을 바로 보내고, 모자란 것(세션 통계, 한도 보고서)을 가져오는 대로 다시 보낸다. 오래 걸리는 일은 모두 비동기.</summary>
        private void RequestUsage()
        {
            string provider = _model.Contains('/') ? _model[.._model.IndexOf('/')] : "";
            if (!string.Equals(provider, _usageProvider, StringComparison.Ordinal))
            {
                _usageProvider = provider;
                _usageJson = null;
                _usageAt = null;
                _usageError = "";
            }

            if (_client == null || !_connected)
            {
                _usageError = T("The agent is not connected.", "에이전트가 연결되어 있지 않습니다.");
                PostUsage();
                return;
            }

            if (provider.Length > 0 && !_usageLoading && (_usageAt == null || _clock.TickMs - _usageAt.Value > UsageCacheMs))
                _ = ReadLimitsAsync(provider);

            Ask("get_session_stats", null,
                data => { _usageStats = JsonNode.Parse(data.GetRawText()); PostUsage(); },
                _ => { /* 통계 없이도 한도 표시는 가능 */ });
            PostUsage();
        }

        private async Task ReadLimitsAsync(string provider)
        {
            string? exe = _ompExe;
            if (exe == null)
            {
                _usageAt = _clock.TickMs;
                _usageError = T("Plan limits are unavailable.", "요금제 한도를 확인할 수 없습니다.");
                PostUsage();
                return;
            }
            _usageLoading = true;
            string? json = null;
            try
            {
                json = await _svc.RunOmpCli(exe, new[] { "usage", "--json", "--provider", provider }, _workDir, CancellationToken.None);
            }
            catch (Exception ex) { _log.Note("omp usage failed: " + ex.Message); }
            if (_disposed) return;
            _usageLoading = false;
            if (!string.Equals(provider, _usageProvider, StringComparison.Ordinal)) return; // 그 사이 모델(제공자)이 바뀜
            _usageAt = _clock.TickMs;
            _usageJson = json;
            _usageError = UsageReport.Limits(json, provider) != null ? ""
                : T("The plan limits of this provider are not available.", "이 제공자의 요금제 한도를 확인할 수 없습니다.");
            PostUsage();
        }

        private void PostUsage()
        {
            string display = _providers.TryGetValue(_usageProvider, out string? name) ? name : _usageProvider;
            _page.Post(ChatPageMessages.Usage(_usageStats, display, _usageLoading, _usageError, UsageReport.Limits(_usageJson, _usageProvider)));
        }
    }
}
