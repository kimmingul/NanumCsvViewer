using System.Text;
using Microsoft.Win32;

namespace NanumCsvViewer.Agent
{
    /// <summary>AppCompatFlags\Layers 값 하나(예: msedgewebview2.exe = HIGHDPIAWARE).</summary>
    /// <param name="Hive">"HKCU" | "HKLM64" | "HKLM32"</param>
    /// <param name="ExePath">값 이름(대상 exe 전체 경로)</param>
    /// <param name="Flags">값 데이터(공백으로 구분된 토큰)</param>
    /// <param name="DpiRelated">DPI 토큰(HIGHDPIAWARE 등)을 포함하는가</param>
    /// <param name="Removable">관리자 권한 없이 해제할 수 있는가(HKCU만)</param>
    internal sealed record CompatLayerEntry(string Hive, string ExePath, string Flags, bool DpiRelated, bool Removable)
    {
        public bool IsWebView2 => ExePath.EndsWith("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><see cref="CompatLayers.RemoveUserLayer"/> 결과. 예외 대신 이 값으로 실패를 돌려준다.</summary>
    internal sealed record RemovalResult(bool Ok, string Message, string? BackupPath);

    /// <summary>레지스트리 접근 추상화(테스트에서 가짜로 바꾼다).</summary>
    internal interface ILayerRegistry
    {
        /// <summary>세 곳(HKCU, HKLM64, HKLM32)의 Layers 값을 모두 읽는다(문자열 값만).</summary>
        IEnumerable<(string Hive, string Name, string Data)> ReadAll();

        /// <summary>HKCU Layers 값 쓰기/삭제. 실패하면 예외.</summary>
        void SetUserValue(string name, string data);
        void DeleteUserValue(string name);
    }

    internal sealed class WindowsLayerRegistry : ILayerRegistry
    {
        public const string SubKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        public IEnumerable<(string Hive, string Name, string Data)> ReadAll()
        {
            foreach (var (label, hive, view) in new[]
            {
                ("HKCU", RegistryHive.CurrentUser, RegistryView.Default),
                ("HKLM64", RegistryHive.LocalMachine, RegistryView.Registry64),
                ("HKLM32", RegistryHive.LocalMachine, RegistryView.Registry32),
            })
            {
                var rows = new List<(string, string, string)>();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(SubKey);
                    if (key is null) continue;
                    foreach (var name in key.GetValueNames())
                        if (key.GetValue(name) is string data) rows.Add((label, name, data));
                }
                catch (Exception) { /* 읽을 수 없는 하이브는 건너뛴다 */ }
                foreach (var r in rows) yield return r;
            }
        }

        public void SetUserValue(string name, string data)
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: true) ?? throw new InvalidOperationException("Layers key not found");
            key.SetValue(name, data, RegistryValueKind.String);
        }

        public void DeleteUserValue(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// WebView2(msedgewebview2.exe)·앱(NanumCsvViewer.exe)에 걸린 응용 프로그램 호환성 레이어 감지와 HKCU 항목 해제.
    /// msedgewebview2.exe에 DPI 레이어(HIGHDPIAWARE 등)가 강제되면 호스트 앱의 DPI 모드와 어긋나 컨트롤러 생성이 0x8007139F로 실패한다.
    /// </summary>
    internal static class CompatLayers
    {
        /// <summary>컨트롤러 생성 실패와 관련된 DPI 토큰.</summary>
        private static readonly string[] DpiTokens =
        {
            "HIGHDPIAWARE", "DPIUNAWARE", "GDIDPISCALING",
            "PERPROCESSSYSTEMDPIFORCEON", "PERPROCESSSYSTEMDPIFORCEOFF",
        };

        internal static bool IsDpiToken(string token) =>
            DpiTokens.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));

        private static string[] Tokens(string flags) => flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>"~", "$", "#" 같은 접두 표지(그 자체로는 설정이 아님).</summary>
        private static bool IsMarker(string token) => token.Length == 1 && !char.IsLetterOrDigit(token[0]);

        public static IReadOnlyList<CompatLayerEntry> Find() => Find(new WindowsLayerRegistry());

        public static IReadOnlyList<CompatLayerEntry> Find(ILayerRegistry registry)
        {
            var list = new List<CompatLayerEntry>();
            foreach (var (hive, name, data) in registry.ReadAll())
            {
                if (!name.EndsWith("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("NanumCsvViewer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new CompatLayerEntry(hive, name, data, Tokens(data).Any(IsDpiToken), hive == "HKCU"));
            }
            return list;
        }

        /// <summary>
        /// 값에서 DPI 토큰만 뺀 새 데이터. DPI 말고 남는 설정이 없으면 null(값 전체 삭제).
        /// 예: "~ WINXPSP3 HIGHDPIAWARE" → "~ WINXPSP3", "HIGHDPIAWARE" → null.
        /// </summary>
        internal static string? StripDpiTokens(string flags)
        {
            var kept = Tokens(flags).Where(t => !IsDpiToken(t)).ToArray();
            return kept.All(IsMarker) ? null : string.Join(' ', kept);
        }

        public static string DefaultBackupPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NanumCsvViewer", "backup", $"appcompat-{DateTime.Now:yyyyMMdd-HHmmss}.reg");

        public static RemovalResult RemoveUserLayer(CompatLayerEntry entry, string backupPath) =>
            RemoveUserLayer(new WindowsLayerRegistry(), entry, backupPath);

        /// <summary>
        /// HKCU 항목의 DPI 설정을 해제한다. 먼저 HKCU Layers 전체를 regedit 형식(.reg, UTF-16 LE)으로 backupPath에 저장하고(실패하면 아무것도 바꾸지 않는다),
        /// 그다음 이 값만 고친다: DPI 토큰만 있으면 값을 삭제하고, 다른 호환성 토큰(예: WINXPSP3)도 있으면 DPI 토큰만 빼고 다시 쓴다.
        /// 백업 .reg를 더블클릭하면(병합) 원래 값으로 돌아간다.
        /// </summary>
        internal static RemovalResult RemoveUserLayer(ILayerRegistry registry, CompatLayerEntry entry, string backupPath)
        {
            if (!entry.Removable || entry.Hive != "HKCU")
                return new RemovalResult(false, "Only HKCU entries can be removed without administrator rights.", null);
            try
            {
                var current = registry.ReadAll().Where(r => r.Hive == "HKCU").ToList();
                if (!current.Any(r => string.Equals(r.Name, entry.ExePath, StringComparison.OrdinalIgnoreCase)))
                    return new RemovalResult(true, "Already removed.", null);

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
                File.WriteAllText(backupPath, BuildBackupReg(current.Select(r => (r.Name, r.Data))), new UnicodeEncoding(false, true));

                string? rest = StripDpiTokens(entry.Flags);
                if (rest is null) registry.DeleteUserValue(entry.ExePath);
                else registry.SetUserValue(entry.ExePath, rest);
                return new RemovalResult(true, rest is null ? "Removed." : "DPI settings removed; other compatibility settings kept.", backupPath);
            }
            catch (Exception ex)
            {
                return new RemovalResult(false, ex.Message, null);
            }
        }

        /// <summary>regedit 내보내기 형식(Version 5.00). 값 이름·데이터의 \ 와 "는 이스케이프한다.</summary>
        internal static string BuildBackupReg(IEnumerable<(string Name, string Data)> values)
        {
            static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var sb = new StringBuilder();
            sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
            sb.Append("[HKEY_CURRENT_USER\\").Append(WindowsLayerRegistry.SubKey).Append("]\r\n");
            foreach (var (name, data) in values)
                sb.Append('"').Append(Esc(name)).Append("\"=\"").Append(Esc(data)).Append("\"\r\n");
            sb.Append("\r\n");
            return sb.ToString();
        }
    }
}
