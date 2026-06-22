; NanumCsvViewer 설치 프로그램 (Inno Setup 6.3+)
; 프레임워크 의존 앱(.NET 미포함)을 설치하고, 설치 중 .NET 10 Desktop Runtime을 점검·설치합니다.
; 컴파일: ISCC.exe /DMyAppVersion=1.4.0 "/DMyAppExe=...\publish-fd\NanumCsvViewer.exe" NanumCsvViewer.iss
; (release.ps1 이 자동으로 호출합니다. DownloadTemporaryFile 사용 → Inno Setup 6.3 이상 필요.)

#define MyAppName "Nanum CSV Viewer"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyAppExe
  #error MyAppExe must be defined (path to framework-dependent NanumCsvViewer.exe)
#endif
#define MyPublisher "Nanum Space Co,. Ltd"
#ifndef Arch
  #define Arch "x64"
#endif
#if Arch == "arm64"
  #define ArchAllowed "arm64"
  #define DotNetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-arm64.exe"
#else
  #define ArchAllowed "x64compatible"
  #define DotNetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#endif
#define DotNetPage "https://dotnet.microsoft.com/download/dotnet/10.0"
#ifndef IconFile
  #define IconFile "..\NanumCsvViewer\app.ico"
#endif

[Setup]
AppId={{8F3A1C2E-5B7D-4E9A-9C61-2D4F6A8B0E13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\NanumCsvViewer
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\NanumCsvViewer.exe
SetupIconFile={#IconFile}
OutputBaseFilename=NanumCsvViewer-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
PrivilegesRequired=admin
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#MyAppExe}"; DestDir: "{app}"; DestName: "NanumCsvViewer.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\NanumCsvViewer.exe"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\NanumCsvViewer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NanumCsvViewer.exe"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
{ C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.* 존재 여부로 런타임 점검 }
function IsDotNet10DesktopInstalled(): Boolean;
var
  fr: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), fr) then
  begin
    try
      Result := True;
    finally
      FindClose(fr);
    end;
  end;
end;

{ 공식 런타임 설치 관리자를 받아 조용히 설치. 실패(오프라인 등) 시 다운로드 페이지 안내. }
procedure InstallDotNet();
var
  rc: Integer;
begin
  try
    DownloadTemporaryFile('{#DotNetUrl}', 'windowsdesktop-runtime.exe', '', nil);
    Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'), '/install /quiet /norestart',
         '', SW_SHOW, ewWaitUntilTerminated, rc);
  except
    if MsgBox('이 프로그램을 실행하려면 .NET 10 Desktop Runtime(x64)이 필요합니다.' + #13#10 +
              '다운로드 페이지를 여시겠습니까?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', '{#DotNetPage}', '', '', SW_SHOW, ewNoWait, rc);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsDotNet10DesktopInstalled() then
  begin
    InstallDotNet();
    if not IsDotNet10DesktopInstalled() then
      Result := '.NET 10 Desktop Runtime(x64)이 설치되지 않아 설치를 계속할 수 없습니다.' + #13#10 +
                '런타임 설치 후 다시 실행해 주세요: {#DotNetPage}';
  end;
end;
