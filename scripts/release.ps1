<#
.SYNOPSIS
  NanumCsvViewer 릴리즈 빌드: 자체 포함 + 프레임워크 의존 2가지 단일 exe를 게시하고,
  SafeNet 토큰 인증서로 코드 서명(타임스탬프 포함)한 뒤 GitHub 릴리즈를 생성합니다.

.DESCRIPTION
  1) dotnet publish x2 (self-contained / framework-dependent)
  2) dist 폴더로 버전·변형명으로 복사
  3) signtool 로 서명 + 검증 (토큰 PIN 프롬프트는 SafeNet Authentication Client가 처리)
  4) gh release create 로 두 자산 업로드

  서명 단계는 토큰 PIN이 필요하므로 이 스크립트는 직접 실행하세요(자동화/CI 부적합).
  ※ 이 파일은 반드시 UTF-8 (BOM) 로 저장하세요. 아니면 PowerShell 5.1에서 한글이 깨집니다.

.PARAMETER Version
  릴리즈 버전(예: 1.4.0). 태그는 v$Version 으로 생성됩니다.

.PARAMETER Thumbprint
  서명할 인증서 thumbprint. 생략하면 코드사이닝 인증서를 목록에서 선택합니다.

.PARAMETER ShowAllCerts
  코드사이닝 필터를 끄고 저장소의 모든 개인키 인증서를 나열합니다.

.PARAMETER TimestampUrl
  RFC3161 타임스탬프 서버. 기본 DigiCert.

.PARAMETER SkipSign     서명 생략(테스트용 빌드만).
.PARAMETER SkipRelease  GitHub 릴리즈 생성 생략(로컬 산출물만).

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
    [switch] $SkipRelease
)

$ErrorActionPreference = 'Stop'

$repo  = Split-Path $PSScriptRoot -Parent
$proj  = Join-Path $repo 'NanumCsvViewer\NanumCsvViewer.csproj'
$dist  = Join-Path $repo 'dist'
$scExe = Join-Path $repo 'NanumCsvViewer\bin\Release\publish\NanumCsvViewer.exe'
$fdExe = Join-Path $repo 'NanumCsvViewer\bin\Release\publish-fd\NanumCsvViewer.exe'
$scOut = Join-Path $dist "NanumCsvViewer-v$Version-win-x64-selfcontained.exe"
$fdOut = Join-Path $dist "NanumCsvViewer-v$Version-win-x64-framework.exe"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# 인증서가 코드 서명용(EKU 1.3.6.1.5.5.7.3.3)인지 검사.
function Test-CodeSigning($cert) {
    foreach ($ext in $cert.Extensions) {
        if ($ext -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $ext.EnhancedKeyUsages) {
                if ($oid.Value -eq '1.3.6.1.5.5.7.3.3') { return $true }
            }
        }
    }
    return $false
}

# ---- 1) publish ---------------------------------------------------------
Step '자체 포함본 게시 중 (단일 파일)...'
dotnet publish $proj -p:PublishProfile=win-x64-singlefile
if ($LASTEXITCODE -ne 0) { throw '자체 포함본 publish 실패' }

Step '프레임워크 의존본 게시 중 (단일 파일)...'
dotnet publish $proj -p:PublishProfile=win-x64-framework
if ($LASTEXITCODE -ne 0) { throw '프레임워크 의존본 publish 실패' }

# ---- 2) dist 로 복사 ----------------------------------------------------
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $scExe $scOut -Force
Copy-Item $fdExe $fdOut -Force
Step '산출물:'
Get-Item $scOut, $fdOut | Format-Table Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} -AutoSize | Out-Host

# ---- 3) 서명 + 검증 -----------------------------------------------------
if (-not $SkipSign) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe',
                              'C:\Program Files\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) {
        $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($cmd) { $signtool = $cmd.Source }
    }
    if (-not $signtool) { throw 'signtool.exe 를 찾을 수 없습니다(Windows SDK 설치 확인).' }
    Write-Host "signtool: $signtool"

    if (-not $Thumbprint) {
        $all = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.HasPrivateKey })
        # 자체 서명(발급자=주체) 제외 + 코드사이닝 EKU 보유만(--ShowAllCerts로 해제)
        $certs = if ($ShowAllCerts) { $all }
                 else { @($all | Where-Object { ($_.Subject -ne $_.Issuer) -and (Test-CodeSigning $_) }) }
        if ($certs.Count -eq 0) {
            Write-Warning '코드사이닝 인증서를 찾지 못해 전체 목록을 표시합니다. (토큰 연결/SafeNet 클라이언트 확인)'
            $certs = $all
        }
        if ($certs.Count -eq 0) { throw '개인키 보유 인증서가 없습니다.' }

        if ($certs.Count -eq 1) {
            $Thumbprint = $certs[0].Thumbprint
            Step "코드사이닝 인증서 자동 선택: $($certs[0].Subject)"
        }
        else {
            Step '서명 인증서를 선택하세요:'
            for ($i = 0; $i -lt $certs.Count; $i++) {
                Write-Host ("  [{0}] {1} | 발급자: {2} | 만료: {3:yyyy-MM-dd} | {4}" -f `
                    $i, $certs[$i].Subject, $certs[$i].Issuer, $certs[$i].NotAfter, $certs[$i].Thumbprint)
            }
            $sel = Read-Host '번호'
            $Thumbprint = $certs[[int]$sel].Thumbprint
        }
    }
    Write-Host "Thumbprint: $Thumbprint"

    foreach ($f in @($scOut, $fdOut)) {
        Step "서명: $(Split-Path $f -Leaf)  (토큰 PIN 프롬프트가 뜨면 입력하세요)"
        # EV 토큰에서 인증서 인식이 안 되면 아래에 /csp "eToken Base Cryptographic Provider" /kc "<컨테이너>" 추가
        & $signtool sign /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /v $f
        if ($LASTEXITCODE -ne 0) { throw "서명 실패: $f" }
        & $signtool verify /pa /v $f
        if ($LASTEXITCODE -ne 0) { throw "검증 실패: $f (코드사이닝 인증서를 골랐는지 확인하세요)" }
    }
    Step '서명/검증 완료'
}

# ---- 4) GitHub 릴리즈 ---------------------------------------------------
if (-not $SkipRelease) {
    $notes = Join-Path $env:TEMP "relnotes-$Version.md"
    $body = @"
NanumCsvViewer v$Version

## 다운로드
- **NanumCsvViewer-v$Version-win-x64-selfcontained.exe** — .NET 설치 없이 실행되는 자체 포함본(x64).
- **NanumCsvViewer-v$Version-win-x64-framework.exe** — 용량이 작은 프레임워크 의존본.
  실행하려면 **.NET 10 Desktop Runtime (x64)** 이 필요합니다:
  https://dotnet.microsoft.com/download/dotnet/10.0

두 파일 모두 코드 서명되어 있습니다(타임스탬프 포함).
"@
    # BOM 없는 UTF-8로 저장(릴리즈 본문에 BOM 문자가 섞이지 않도록)
    [System.IO.File]::WriteAllText($notes, $body, (New-Object System.Text.UTF8Encoding($false)))

    Step "GitHub 릴리즈 생성: v$Version"
    gh release create "v$Version" --title "v$Version" --notes-file $notes `
        "$scOut#자체 포함본 (.NET 불필요, x64)" `
        "$fdOut#프레임워크 의존본 (.NET 10 Desktop Runtime 필요, x64)"
    if ($LASTEXITCODE -ne 0) { throw 'gh release 생성 실패' }
    Step '릴리즈 완료'
}

Step '끝.'
