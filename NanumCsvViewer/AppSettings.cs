using System.IO;
using System.Text.Json;

namespace NanumCsvViewer
{
    /// <summary>%AppData%\NanumCsvViewer\settings.json 에 저장되는 사용자 설정(테마·언어).</summary>
    public sealed class AppSettings
    {
        /// <summary>"Light" | "Dark" | "" (빈 값이면 Windows 시스템 테마 따름).</summary>
        public string Theme { get; set; } = "";

        /// <summary>"auto"(OS 언어 따름) | "en" | "ko".</summary>
        public string Language { get; set; } = "auto";

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NanumCsvViewer");
        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch { /* 손상/접근 불가 시 기본값 */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 저장 실패는 무시(다음 실행에 영향만) */ }
        }
    }
}
