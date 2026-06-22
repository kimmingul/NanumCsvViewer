<#
.SYNOPSIS
  NanumCsvViewer 릴리즈 빌드: Portable(단일 exe) + Install(setup.exe) 2가지를 만들고,
  SafeNet 토큰 인증서로 코드 서명(타임스탬프 포함)한 뒤 GitHub 릴리즈를 생성합니다.

.DESCRIPTION
  두 산출물 모두 프레임워크 의존(.NET 미포함):
   - Portable : framework-dependent 단일 exe (복사 후 실행, .NET 10 Desktop Runtime 필요)
   - Install  : Inno Setup setup.exe (설치 중 .NET 10 점검·자동 설치 + 시작메뉴/제거)

  흐름: publish(FD) → 앱 exe 서명 → portable 복사 → ISCC로 setup 컴파일 → setup 서명 → gh release
  서명 단계는 토큰 PIN이 필요하므로 직접 실행하세요(자동화/CI 부적합).
  ※ 이 파일은 UTF-8 (BOM) 로 저장하세요(PowerShell 5.1 한글 보존).

  요구: .NET SDK, Inno Setup 6.3+ (winget install JRSoftware.InnoSetup),
        Windows SDK(signtool), GitHub CLI(gh, 로그인됨).

.PARAMETER Version       릴리즈 버전(예: 1.4.0). 태그는 v$Version.
.PARAMETER Thumbprint    서명 인증서 thumbprint. 생략 시 코드사이닝 인증서 자동 필터/선택.
.PARAMETER ShowAllCerts  코드사이닝 필터를 끄고 모든 개인키 인증서 표시.
.PARAMETER TimestampUrl  RFC3161 타임스탬프 서버. 기본 DigiCert.
.PARAMETER SkipSign          서명 생략(테스트용 빌드만).
.PARAMETER SkipVerifyInstall 설치된 exe 서명 검증(무인 설치/제거) 생략.
.PARAMETER SkipRelease       GitHub 릴리즈 생성 생략(로컬 산출물만).

.EXAMPLE
  .\scripts\release.ps1 -Version 1.4.0
  .\scripts\release.ps1 -Version 1.4.0 -SkipSign -SkipRelease
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $Thumbprint,
    [switch] $ShowAllCerts,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $SkipSign,
    [switch] $SkipVerifyInstall,
    [switch] $SkipRelease
)

$ErrorActionPreference = 'Stop'

$repo     = Split-Path $PSScriptRoot -Parent
$proj     = Join-Path $repo 'NanumCsvViewer\NanumCsvViewer.csproj'
$iss      = Join-Path $repo 'installer\NanumCsvViewer.iss'
$dist     = Join-Path $repo 'dist'
$fdExe    = Join-Path $repo 'NanumCsvViewer\bin\Release\publish-fd\NanumCsvViewer.exe'
$portable = Join-Path $dist "NanumCsvViewer-v$Version-win-x64-portable.exe"
$setupOut = Join-Path $dist "NanumCsvViewer-v$Version-win-x64-setup.exe"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Test-CodeSigning($cert) {
    foreach ($ext in $cert.Extensions) {
        if ($ext -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $ext.EnhancedKeyUsages) { if ($oid.Value -eq '1.3.6.1.5.5.7.3.3') { return $true } }
        }
    }
    return $false
}

$script:SignTool = $null
function Resolve-SignTool() {
    $st = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe',
                        'C:\Program Files\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
          Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $st) { $c = Get-Command signtool.exe -ErrorAction SilentlyContinue; if ($c) { $st = $c.Source } }
    if (-not $st) { throw 'signtool.exe 를 찾을 수 없습니다(Windows SDK 설치 확인).' }
    $script:SignTool = $st
    Write-Host "signtool: $st"
}

function Resolve-Thumbprint() {
    if ($Thumbprint) { return }
    $all = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey })
    $certs = if ($ShowAllCerts) { $all }
             else { @($all | Where-Object { ($_.Subject -ne $_.Issuer) -and (Test-CodeSigning $_) }) }
    if ($certs.Count -eq 0) { Write-Warning '코드사이닝 인증서를 못 찾아 전체 표시.'; $certs = $all }
    if ($certs.Count -eq 0) { throw '개인키 보유 인증서가 없습니다(토큰 연결 확인).' }
    if ($certs.Count -eq 1) {
        $script:Thumbprint = $certs[0].Thumbprint
        Step "코드사이닝 인증서 자동 선택: $($certs[0].Subject)"
    } else {
        Step '서명 인증서를 선택하세요:'
        for ($i = 0; $i -lt $certs.Count; $i++) {
            Write-Host ("  [{0}] {1} | 발급자: {2} | 만료: {3:yyyy-MM-dd} | {4}" -f `
                $i, $certs[$i].Subject, $certs[$i].Issuer, $certs[$i].NotAfter, $certs[$i].Thumbprint)
        }
        $script:Thumbprint = $certs[[int](Read-Host '번호')].Thumbprint
    }
    Write-Host "Thumbprint: $script:Thumbprint"
}

function Sign($file) {
    Step "서명: $(Split-Path $file -Leaf)  (토큰 PIN 프롬프트가 뜨면 입력하세요)"
    # EV 인증서 인식이 안 되면 /csp "eToken Base Cryptographic Provider" /kc "<컨테이너>" 추가
    & $script:SignTool sign /sha1 $script:Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /v $file
    if ($LASTEXITCODE -ne 0) { throw "서명 실패: $file" }
    & $script:SignTool verify /pa /v $file
    if ($LASTEXITCODE -ne 0) { throw "검증 실패: $file (코드사이닝 인증서인지 확인)" }
}

function Resolve-ISCC() {
    $i = Get-ChildItem 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
                       'C:\Program Files\Inno Setup 6\ISCC.exe' -ErrorAction SilentlyContinue |
         Select-Object -First 1 -ExpandProperty FullName
    if (-not $i) { $c = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($c) { $i = $c.Source } }
    if (-not $i) { throw 'ISCC.exe(Inno Setup)를 찾을 수 없습니다. 설치: winget install JRSoftware.InnoSetup' }
    return $i
}

# ---- 1) publish (framework-dependent) -----------------------------------
Step '프레임워크 의존본 게시 중 (단일 파일)...'
# 바이너리 버전을 릴리즈 버전과 일치(X.Y.Z → 파일/어셈블리 버전 X.Y.Z.0)
$fileVer = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
dotnet publish $proj -p:PublishProfile=win-x64-framework `
    "-p:Version=$Version" "-p:FileVersion=$fileVer" "-p:AssemblyVersion=$fileVer"
if ($LASTEXITCODE -ne 0) { throw 'publish 실패' }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# ---- 2) 서명 준비 + 앱 exe 서명(설치본/포터블 공통) ----------------------
if (-not $SkipSign) {
    Resolve-SignTool
    Resolve-Thumbprint
    Sign $fdExe   # 설치 후 앱 exe + 포터블에 모두 반영됨
}

# ---- 3) Portable (서명된 FD exe 복사) -----------------------------------
Copy-Item $fdExe $portable -Force
Step "Portable: $(Split-Path $portable -Leaf)"

# ---- 4) Install (Inno Setup 컴파일) -------------------------------------
$iscc = Resolve-ISCC
Step "설치본 컴파일: ISCC"
& $iscc "/DMyAppVersion=$Version" "/DMyAppExe=$fdExe" "/O$dist" `
        "/FNanumCsvViewer-v$Version-win-x64-setup" $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 컴파일 실패' }
if (-not $SkipSign) { Sign $setupOut }   # 설치 관리자도 서명(SmartScreen)

# ---- 4b) 설치된 exe 서명 검증(무인 설치 → verify → 제거) -----------------
# 요구사항 증명: setup이 설치하는 앱 exe도 서명되어 있어야 한다.
# 관리자 권한 설치라 UAC가 뜰 수 있음. (.NET 10이 이미 있으면 자동설치 분기는 건너뜀)
if (-not $SkipSign -and -not $SkipVerifyInstall) {
    $testDir = Join-Path $env:TEMP "nctest-$Version"
    $instExe = Join-Path $testDir 'NanumCsvViewer.exe'
    $uninst  = Join-Path $testDir 'unins000.exe'
    if (Test-Path $testDir) { Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue }
    Step '설치된 exe 서명 검증: 임시 무인 설치...'
    Start-Process $setupOut -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART `"/DIR=$testDir`"" -Wait
    if (-not (Test-Path $instExe)) { throw "무인 설치 실패(설치된 exe 없음): $instExe" }
    & $script:SignTool verify /pa /v $instExe
    if ($LASTEXITCODE -ne 0) { throw "설치된 exe 서명 검증 실패: $instExe" }
    Step '설치된 exe 서명 확인됨 → 임시 설치 제거'
    if (Test-Path $uninst) { Start-Process $uninst -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -Wait }
}

Step '산출물:'
Get-Item $portable, $setupOut | Format-Table Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} -AutoSize | Out-Host

# ---- 5) GitHub 릴리즈 ---------------------------------------------------
if (-not $SkipRelease) {
    $notes = Join-Path $env:TEMP "relnotes-$Version.md"
    $body = @"
NanumCsvViewer v$Version

## 다운로드
- **NanumCsvViewer-v$Version-win-x64-portable.exe** — 설치 없이 실행하는 단일 실행 파일.
- **NanumCsvViewer-v$Version-win-x64-setup.exe** — 설치 관리자(시작 메뉴 등록·제거 지원).

두 버전 모두 **.NET 10 Desktop Runtime (x64)** 이 필요합니다. 설치 관리자는 없을 경우 자동으로 설치를 진행합니다.
포터블 버전은 런타임이 없으면 Windows가 안내합니다 — 수동 설치: https://dotnet.microsoft.com/download/dotnet/10.0

두 파일 모두 코드 서명되어 있습니다(타임스탬프 포함).
"@
    [System.IO.File]::WriteAllText($notes, $body, (New-Object System.Text.UTF8Encoding($false)))

    $p1 = "$portable#Portable (설치 불필요, .NET 10 런타임 필요, x64)"
    $p2 = "$setupOut#설치 관리자 (.NET 10 자동 설치, x64)"
    # cmd /c 로 감싸 stderr를 완전히 삼킴(PS 5.1의 NativeCommandError + Stop 종료 회피)
    cmd /c "gh release view v$Version 1>nul 2>nul"
    if ($LASTEXITCODE -eq 0) {
        Step "기존 릴리즈 v$Version 에 자산 업로드(덮어쓰기)"
        gh release upload "v$Version" $p1 $p2 --clobber
    } else {
        Step "GitHub 릴리즈 생성: v$Version"
        gh release create "v$Version" --title "v$Version" --notes-file $notes $p1 $p2
    }
    if ($LASTEXITCODE -ne 0) { throw 'gh release 실패' }
    Step '릴리즈 완료'
}

Step '끝.'
