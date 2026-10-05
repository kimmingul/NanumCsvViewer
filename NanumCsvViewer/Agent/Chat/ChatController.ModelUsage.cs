using NanumCsvViewer.Agent.Chat;

namespace NanumCsvViewer.Agent
{
    // 모델 선택기의 "최근 사용" 그룹: omp가 agent.db에 적어 둔 모델별 마지막 사용 시각을 읽어 카탈로그의 recent로 보낸다.
    // 못 읽으면(없음·잠김·형식 변경·오류) recent는 비고 선택기는 전체 목록만 보인다. 형식 변경·오류는 omp 버전마다 한 번 채팅에 알린다.
    public sealed partial class ChatController
    {
        /// <summary>연속 이만큼 잠겨 있으면(읽을 때마다 재시도 포함) 일시적이 아닌 것으로 보고 한 번 알린다.</summary>
        internal const int PersistentBusyReads = 3;

        private List<OmpModelUsage.Use> _usageItems = new();
        private List<string> _postedRecent = new();
        private OmpModelUsage.Result? _usageResult;
        private int _usageGeneration;
        private int _usageBusyStreak;
        private string _usageLogged = "";

        /// <summary>"최근 사용 모델을 읽을 수 없음" 경고를 채팅에 보였을 때(omp 버전을 넘긴다). 호스트가 설정에 저장해 같은 버전에서 다시 알리지 않는다.</summary>
        public event Action<string>? ModelUsageAlertShown;

        /// <summary>설정 화면의 한 줄 상태("최근 사용 모델: …"). 아직 읽지 않았으면 안내 문구.</summary>
        public string ModelUsageStatusText => ModelUsageStatusLine(_usageResult, Korean);

        internal static string ModelUsageStatusLine(OmpModelUsage.Result? result, bool korean)
        {
            string head = korean ? "최근 사용 모델: " : "Recently used models: ";
            if (result is null)
                return head + (korean ? "아직 확인하지 않음 (omp를 시작하면 확인합니다)" : "not checked yet (checked when omp starts)");
            if (result.IsOk)
                return head + (korean ? "omp 기록 사용 중" : "using omp's usage record");
            string reason = result.Status switch
            {
                OmpModelUsage.Status.NoDatabase => korean ? "omp 기록 파일이 없음" : "omp has no usage record yet",
                OmpModelUsage.Status.Busy => korean ? "기록 파일이 잠겨 있음" : "the record is locked",
                OmpModelUsage.Status.SchemaChanged => korean ? "omp 내부 형식이 바뀜" : "omp's internal format changed",
                _ => korean ? "읽기 오류" : "read error",
            };
            if (result.Detail.Length > 0 && result.Status != OmpModelUsage.Status.NoDatabase) reason += ": " + result.Detail;
            return head + (korean ? $"읽을 수 없음 ({reason})" : $"unavailable ({reason})");
        }

        /// <summary>사용 기록을 UI 스레드 밖에서 읽고, 끝나면 UI 스레드에서 적용한다(목록이 달라졌을 때만 카탈로그를 다시 보낸다).</summary>
        private void RefreshModelUsage()
        {
            if (_disposed) return;
            _ = RefreshModelUsageAsync();
        }

        private async Task RefreshModelUsageAsync()
        {
            int generation = ++_usageGeneration;
            string? dir = _svc.AgentDirectory ?? OmpAgentDir.Resolve(_options.ExtraArgs);
            OmpModelUsage.Result result;
            try
            {
                result = dir == null
                    ? OmpModelUsage.Result.Fail(OmpModelUsage.Status.NoDatabase, "agent directory unknown")
                    : await Task.Run(() => _svc.ReadModelUsage(dir));
            }
            catch (Exception ex)
            {
                result = OmpModelUsage.Result.Fail(OmpModelUsage.Status.Error, ex.GetType().Name + ": " + ex.Message);
            }
            if (_disposed || generation != _usageGeneration) return;   // 더 새 읽기가 이어받는다
            ApplyModelUsage(result);
        }

        private void ApplyModelUsage(OmpModelUsage.Result result)
        {
            _usageResult = result;
            _usageBusyStreak = result.Status == OmpModelUsage.Status.Busy ? _usageBusyStreak + 1 : 0;
            // 잠겨서 못 읽은 때는 직전에 읽은 값을 유지한다(없는 것보다 조금 오래된 쪽이 낫다). 그 밖의 실패는 비운다 = 전체 목록으로 되돌아감.
            if (result.IsOk) _usageItems = result.Items.ToList();
            else if (result.Status != OmpModelUsage.Status.Busy) _usageItems = new();

            LogModelUsage(result);
            AlertModelUsageOnce(result);
            if (!OmpModelUsage.Recent(_usageItems, _modelList).SequenceEqual(_postedRecent)) PostCatalog();
        }

        private void LogModelUsage(OmpModelUsage.Result result)
        {
            string line = result.IsOk ? "ok" : $"{result.Status}: {result.Detail}";
            if (line == _usageLogged) return;
            _usageLogged = line;
            _log.Note("model usage (omp agent.db): " + line);
        }

        /// <summary>형식 변경·읽기 오류(또는 계속 잠김)면 omp 버전마다 한 번 채팅에 경고한다. NoDatabase와 일시적인 잠김은 알리지 않는다.</summary>
        private void AlertModelUsageOnce(OmpModelUsage.Result result)
        {
            bool persistentBusy = result.Status == OmpModelUsage.Status.Busy && _usageBusyStreak >= PersistentBusyReads;
            if (result.Status is not (OmpModelUsage.Status.SchemaChanged or OmpModelUsage.Status.Error) && !persistentBusy) return;
            string version = _ompVersion?.ToString() ?? "?";
            if (string.Equals(_options.ModelUsageAlertedVersion, version, StringComparison.Ordinal)) return;
            _options = _options with { ModelUsageAlertedVersion = version };

            string text = persistentBusy
                ? T("omp's usage record stays locked, so recently used models cannot be shown. Showing the full model list.",
                    "omp 사용 기록이 계속 잠겨 있어 최근 사용 모델을 표시할 수 없습니다. 전체 모델 목록을 표시합니다.")
                : T("omp's internal format has changed, so recently used models cannot be shown. Showing the full model list. Please update the app.",
                    "omp 내부 형식이 바뀌어 최근 사용 모델을 표시할 수 없습니다. 전체 모델 목록을 표시합니다. 앱을 업데이트하세요.");
            if (result.Detail.Length > 0) text += $" ({result.Detail})";
            _stream.Emit(ChatPageMessages.Notice("warn", text));
            ModelUsageAlertShown?.Invoke(version);
        }
    }
}
