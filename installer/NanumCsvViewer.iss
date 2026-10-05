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
; 파일 연결 등록/해제 후 탐색기에 변경을 알린다.
ChangesAssociations=yes

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

; 탐색기 "연결 프로그램" 등록. 명령은 반드시 "%1"을 따옴표로 감싸 공백 경로가 한 인수로 전달되게 한다.
; Windows 10/11은 기본 앱을 프로그램이 직접 바꿀 수 없으므로(UserChoice), 연결 프로그램 목록과
; 설정 ▸ 기본 앱(RegisteredApplications/Capabilities)에 나타나게만 한다.
#define ProgId "NanumCsvViewer.DataFile"
#define AppRegKey "Software\NanumCsvViewer"

[Registry]
; ProgID
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\NanumCsvViewer.exe,0"
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\NanumCsvViewer.exe"" ""%1"""
; Applications\<exe> — "연결 프로그램 ▸ 다른 앱 선택"에서 exe를 고른 경우에도 같은 명령을 쓰게 한다.
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\NanumCsvViewer.exe"" ""%1"""
; 설정 ▸ 기본 앱에 표시
Root: HKA; Subkey: "{#AppRegKey}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "{#AppRegKey}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{#MyAppName}"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "NanumCsvViewer"; ValueData: "{#AppRegKey}\Capabilities"; Flags: uninsdeletevalue
; 확장자별: 연결 프로그램 목록(OpenWithProgids) · Applications 지원 형식 · 기본 앱 연결 후보
Root: HKA; Subkey: "Software\Classes\.csv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".csv"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".csv"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.tsv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".tsv"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tsv"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".txt"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.xlsx\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".xlsx"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".xlsx"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.xlsm\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".xlsm"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".xlsm"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.xls\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".xls"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".xls"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.sas7bdat\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".sas7bdat"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".sas7bdat"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.sav\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".sav"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".sav"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.db\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".db"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".db"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.sqlite\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".sqlite"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".sqlite"; ValueData: "{#ProgId}"
Root: HKA; Subkey: "Software\Classes\.sqlite3\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".sqlite3"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".sqlite3"; ValueData: "{#ProgId}"
; 작업 공간 파일(.ncvws)은 앱 전용 형식이므로 기본 연결로 등록한다.
Root: HKA; Subkey: "Software\Classes\NanumCsvViewer.Workspace"; ValueType: string; ValueName: ""; ValueData: "{#MyAppName} Workspace"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\NanumCsvViewer.Workspace\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\NanumCsvViewer.exe,0"
Root: HKA; Subkey: "Software\Classes\NanumCsvViewer.Workspace\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\NanumCsvViewer.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.ncvws"; ValueType: string; ValueName: ""; ValueData: "NanumCsvViewer.Workspace"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.ncvws\OpenWithProgids"; ValueType: string; ValueName: "NanumCsvViewer.Workspace"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\NanumCsvViewer.exe\SupportedTypes"; ValueType: string; ValueName: ".ncvws"; ValueData: ""
Root: HKA; Subkey: "{#AppRegKey}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ncvws"; ValueData: "NanumCsvViewer.Workspace"

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
