using System.Runtime.CompilerServices;

namespace NanumCsvViewer.Tests
{
    /// <summary>
    /// 테스트 어셈블리가 로드될 때 한 번: 앱 설정 파일을 임시 폴더로 돌린다. Form1을 쓰는 테스트가 작업 공간을 열고 저장할 때 최근 목록·에이전트 설정을 저장하므로
    /// 그대로 두면 이 PC 사용자의 실제 %APPDATA%\NanumCsvViewer\settings.json을 덮어쓴다.
    /// </summary>
    internal static class TestEnvironment
    {
        [ModuleInitializer]
        internal static void IsolateAppSettings() =>
            AppSettings.DirectoryOverride = Path.Combine(Path.GetTempPath(), "ncv-test-appdata-" + Environment.ProcessId);
    }
}
