using System.Security.Cryptography;
using System.Text;

namespace NanumCsvViewer.Csv;

/// <summary>
/// 파일 경로 동일성. Windows 캐시 키는 기존 소문자 해시를 유지한다.
/// 대소문자를 구분하는 볼륨에서는 다른 파일을 같은 파일로 합치지 않는다.
/// </summary>
internal static class FilePathIdentity
{
    public static bool SameFile(string left, string right)
    {
        string a = Path.GetFullPath(left);
        string b = Path.GetFullPath(right);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(a, b, cmp);
    }

    /// <summary>인덱스·뷰·편집 저널이 공유하는 경로 토큰. 형식 버전과 무관한 키 재료.</summary>
    public static string CacheToken(string path)
    {
        string full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    public static string Hash(string material, int hexChars)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, Math.Min(hash.Length, (hexChars + 1) / 2))[..hexChars];
    }
}
