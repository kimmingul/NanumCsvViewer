using NanumCsvViewer;
using System.Text.RegularExpressions;

namespace NanumCsvViewer.Tests
{
    public class AppInfoTests
    {
        // AppInfo는 메인 어셈블리(NanumCsvViewer.dll)의 버전·메타데이터를 읽는다.
        [Fact]
        public void Version_is_three_part_number()
        {
            Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), AppInfo.Version);
        }

        [Fact]
        public void Release_date_is_injected_and_well_formed()
        {
            // csproj의 AssemblyMetadata(ReleaseDate)가 빌드 시 주입된다(릴리즈 빌드는 release.ps1이 그날 날짜로 덮어씀).
            Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$"), AppInfo.ReleaseDate);
        }
    }
}
