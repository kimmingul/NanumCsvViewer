using System.Reflection;

namespace NanumCsvViewer
{
    /// <summary>
    /// 앱 버전·릴리즈 날짜를 어셈블리 메타데이터에서 읽어 한 곳에서 제공한다.
    /// 버전은 AssemblyVersion(release.ps1의 -p:Version), 날짜는 AssemblyMetadata("ReleaseDate")
    /// (release.ps1의 -p:ReleaseDate, dev 빌드는 빌드일)에서 가져온다.
    /// </summary>
    public static class AppInfo
    {
        /// <summary>예: "1.9.1".</summary>
        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        /// <summary>릴리즈(또는 빌드) 날짜. 예: "2026-07-01". 없으면 빈 문자열.</summary>
        public static string ReleaseDate
        {
            get
            {
                var meta = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "ReleaseDate");
                return string.IsNullOrWhiteSpace(meta?.Value) ? "" : meta!.Value!;
            }
        }
    }
}
