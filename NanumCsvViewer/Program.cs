using System.Text;

namespace NanumCsvViewer
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // CP949/EUC-KR(코드페이지 949) 등 비기본 인코딩을 사용하려면 .NET에서 이 등록이 필수입니다.
            // 어떤 인코딩이 쓰이기 전에 단 한 번 호출되도록 진입점에서 등록합니다.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // 설정 로드 후 언어 결정(기본 영어, OS가 한국어면 한국어). UI 생성 전에 적용해야 함.
            var settings = AppSettings.Load();
            Loc.Apply(settings.Language);

            ApplicationConfiguration.Initialize();
            // 탐색기에서 파일을 열면("exe" "%1") 인수로 경로가 전달된다 — 창 표시 직후 연다.
            Application.Run(new Form1(settings, StartupArgs.ResolveFilePath(args)));
        }
    }
}
