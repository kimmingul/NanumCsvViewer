using System.Text;
using NanumCsvViewer.Agent;

namespace NanumCsvViewer.Tests
{
    // AppCompatFlags\Layers 감지·HKCU 해제·백업 .reg 형식(가짜 레지스트리).
    public class CompatLayersTests
    {
        private const string Wv = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\149.0.4022.98\msedgewebview2.exe";

        private sealed class FakeRegistry : ILayerRegistry
        {
            public readonly List<(string Hive, string Name, string Data)> Rows = new();
            public IEnumerable<(string Hive, string Name, string Data)> ReadAll() => Rows.ToList();
            public void SetUserValue(string name, string data)
            {
                int i = Rows.FindIndex(r => r.Hive == "HKCU" && r.Name == name);
                Rows[i] = ("HKCU", name, data);
            }
            public void DeleteUserValue(string name) => Rows.RemoveAll(r => r.Hive == "HKCU" && r.Name == name);
        }

        private static string TempReg() => Path.Combine(Path.GetTempPath(), "nanum-compat-" + Guid.NewGuid().ToString("N"), "b.reg");

        private static string ReadBackup(string path) => File.ReadAllText(path, Encoding.Unicode);

        private static void Cleanup(string path) { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (Exception) { } }

        [Fact]
        public void Find_matches_only_webview2_and_app_exe_and_flags_dpi_tokens()
        {
            var reg = new FakeRegistry();
            reg.Rows.Add(("HKCU", Wv, "HIGHDPIAWARE"));
            reg.Rows.Add(("HKCU", @"C:\x\Other.exe", "HIGHDPIAWARE"));
            reg.Rows.Add(("HKLM64", @"C:\Apps\NanumCsvViewer.EXE", "~ WINXPSP3"));
            reg.Rows.Add(("HKLM32", Wv, "~ GDIDPISCALING DPIUNAWARE"));

            var found = CompatLayers.Find(reg);

            Assert.Equal(3, found.Count);
            var user = found.Single(f => f.Hive == "HKCU");
            Assert.True(user.DpiRelated); Assert.True(user.Removable); Assert.True(user.IsWebView2);
            var app = found.Single(f => f.Hive == "HKLM64");
            Assert.False(app.DpiRelated); Assert.False(app.Removable); Assert.False(app.IsWebView2);
            var m32 = found.Single(f => f.Hive == "HKLM32");
            Assert.True(m32.DpiRelated); Assert.False(m32.Removable);
        }

        [Theory]
        [InlineData("HIGHDPIAWARE", null)]
        [InlineData("~ HIGHDPIAWARE", null)]
        [InlineData("DPIUNAWARE GDIDPISCALING", null)]
        [InlineData("~ WINXPSP3 HIGHDPIAWARE", "~ WINXPSP3")]
        [InlineData("~ PERPROCESSSYSTEMDPIFORCEON RUNASADMIN", "~ RUNASADMIN")]
        [InlineData("~ highdpiaware WIN7RTM", "~ WIN7RTM")]
        public void StripDpiTokens_removes_only_dpi_tokens(string flags, string? expected) =>
            Assert.Equal(expected, CompatLayers.StripDpiTokens(flags));

        [Fact]
        public void Remove_deletes_dpi_only_value_and_backs_up_the_whole_hkcu_key_first()
        {
            var reg = new FakeRegistry();
            reg.Rows.Add(("HKCU", Wv, "HIGHDPIAWARE"));
            reg.Rows.Add(("HKCU", @"C:\Apps\Other.exe", "~ WIN7RTM"));
            reg.Rows.Add(("HKLM64", @"C:\Apps\HklmOnly.exe", "WIN7RTM"));
            var entry = CompatLayers.Find(reg).Single();
            var backup = TempReg();
            try
            {
                var r = CompatLayers.RemoveUserLayer(reg, entry, backup);

                Assert.True(r.Ok);
                Assert.Equal(backup, r.BackupPath);
                Assert.DoesNotContain(reg.Rows, x => x.Name == Wv);
                Assert.Contains(reg.Rows, x => x.Name == @"C:\Apps\Other.exe");   // 다른 값은 그대로
                Assert.Contains(reg.Rows, x => x.Hive == "HKLM64");

                var text = ReadBackup(backup);
                Assert.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers]\r\n", text);
                Assert.Contains("\"C:\\\\Program Files (x86)\\\\Microsoft\\\\EdgeWebView\\\\Application\\\\149.0.4022.98\\\\msedgewebview2.exe\"=\"HIGHDPIAWARE\"\r\n", text);
                Assert.Contains("\"C:\\\\Apps\\\\Other.exe\"=\"~ WIN7RTM\"", text);
                Assert.DoesNotContain("HklmOnly", text);
                // regedit 형식: UTF-16 LE BOM
                var bytes = File.ReadAllBytes(backup);
                Assert.Equal(0xFF, bytes[0]); Assert.Equal(0xFE, bytes[1]);
            }
            finally { Cleanup(backup); }
        }

        [Fact]
        public void Remove_rewrites_value_without_dpi_tokens_when_other_flags_exist()
        {
            var reg = new FakeRegistry();
            reg.Rows.Add(("HKCU", Wv, "~ WINXPSP3 HIGHDPIAWARE"));
            var backup = TempReg();
            try
            {
                var r = CompatLayers.RemoveUserLayer(reg, CompatLayers.Find(reg).Single(), backup);

                Assert.True(r.Ok);
                Assert.Equal("~ WINXPSP3", reg.Rows.Single(x => x.Name == Wv).Data);
                Assert.Contains("\"~ WINXPSP3 HIGHDPIAWARE\"", ReadBackup(backup));   // 백업은 원래 값
            }
            finally { Cleanup(backup); }
        }

        [Fact]
        public void Remove_refuses_hklm_entries_and_changes_nothing()
        {
            var reg = new FakeRegistry();
            reg.Rows.Add(("HKLM64", Wv, "HIGHDPIAWARE"));
            var backup = TempReg();
            var r = CompatLayers.RemoveUserLayer(reg, CompatLayers.Find(reg).Single(), backup);

            Assert.False(r.Ok);
            Assert.Single(reg.Rows);
            Assert.False(File.Exists(backup));
        }

        [Fact]
        public void Remove_leaves_registry_untouched_when_the_backup_cannot_be_written()
        {
            var reg = new FakeRegistry();
            reg.Rows.Add(("HKCU", Wv, "HIGHDPIAWARE"));
            var blocker = Path.Combine(Path.GetTempPath(), "nanum-compat-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blocker, "x");   // 폴더가 와야 할 자리에 파일
            try
            {
                var r = CompatLayers.RemoveUserLayer(reg, CompatLayers.Find(reg).Single(), Path.Combine(blocker, "b.reg"));

                Assert.False(r.Ok);
                Assert.Equal("HIGHDPIAWARE", reg.Rows.Single().Data);
            }
            finally { File.Delete(blocker); }
        }
    }
}
