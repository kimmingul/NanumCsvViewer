using NanumCsvViewer.Agent.Chat;
using NanumCsvViewer.Agent.Rpc;

namespace NanumCsvViewer.Agent
{
    // 분석 스킬 묶음: 로컬 Python이 켜져 있을 때만 omp 시작 때 풀어 --config 덧씌우기로 건다. 꺼져 있으면 아무것도 싣지 않는다(토큰 비용 0).
    public sealed partial class ChatController
    {
        /// <summary>이번 omp 실행에 실린 스킬(없으면 null). 가이드의 Python 절이 이 목록으로 안내를 바꾼다.</summary>
        private SkillLaunch? _skills;
        /// <summary>마지막 시작 때 원했던 스킬 키(빈 문자열 = 싣지 않음). 풀기에 실패해도 같은 값이라 재시작을 되풀이하지 않는다.</summary>
        private string _launchedSkillsKey = "";
        private string? _skillsOverlay;

        /// <summary>지금 설정이 원하는 스킬 키. 로컬 Python이 꺼져 있으면 빈 문자열.</summary>
        private string DesiredSkillsKey() => _options.AllowLocalPython ? SkillPack.KeyFor(_options.Skills) : "";

        /// <summary>실행 중인 omp에 실린 스킬이 지금 설정과 다른가(분석 스킬 켜기/끄기·분류·개별 스킬·로컬 Python 변경).</summary>
        private bool SkillsStale() => !string.Equals(DesiredSkillsKey(), _launchedSkillsKey, StringComparison.Ordinal);

        /// <summary>
        /// omp 시작 전에: 켜진 스킬만 풀고, 사용자가 omp에 이미 지정한 skills.customDirectories를 읽어 덧씌우기 파일을 쓴다.
        /// 로컬 Python이 꺼져 있거나 실을 스킬이 없으면 아무것도 하지 않는다. 실패는 알림으로만 알리고 나머지는 계속된다.
        /// </summary>
        private async Task PrepareSkillsAsync(string exe, int launch, CancellationToken cancellation)
        {
            _skills = null;
            _skillsOverlay = null;
            _launchedSkillsKey = DesiredSkillsKey();
            if (_launchedSkillsKey.Length == 0)
            {
                OmpLaunch.WriteSkillsOverlay(_supportTag, null);
                return;
            }

            var selection = _options.Skills;
            string? root = _svc.SkillRoot;
            SkillLaunch? prepared = null;
            try { prepared = await Task.Run(() => SkillPack.Prepare(selection, root), cancellation); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Note("skill pack extraction failed: " + ex);
                _stream.Emit(ChatPageMessages.Notice("warn", T("Could not prepare the analysis skills: ", "분석 스킬을 준비하지 못했습니다: ") + ex.Message));
            }
            if (launch != _launchId || _disposed) return;

            IReadOnlyList<string> userDirs = Array.Empty<string>();
            if (prepared != null)
            {
                try { userDirs = SkillPack.ParseUserDirectories(await _svc.RunOmpCli(exe, new[] { "config", "get", "skills.customDirectories", "--json" }, _workDir, cancellation)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.Note("omp config get skills.customDirectories failed: " + ex.Message); }
                if (launch != _launchId || _disposed) return;
            }

            try
            {
                _skillsOverlay = OmpLaunch.WriteSkillsOverlay(_supportTag, prepared, userDirs);
                _skills = _skillsOverlay != null ? prepared : null;
            }
            catch (Exception ex)
            {
                _log.Note("skill overlay failed: " + ex);
                _stream.Emit(ChatPageMessages.Notice("warn", T("Could not write the analysis-skill settings: ", "분석 스킬 설정을 쓰지 못했습니다: ") + ex.Message));
            }
        }
    }
}
