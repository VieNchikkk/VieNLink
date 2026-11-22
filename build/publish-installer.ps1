#Requires -Version 5.1
<#
.SYNOPSIS
    Собирает два варианта поставки VieNLink:
      1) портативный — папка release\VieNLink-<версия>-win-x64 и ZIP;
      2) установщик — release\VieNLink-<версия>-setup.exe (Inno Setup).

.DESCRIPTION
    Сначала вызывается publish-release.ps1 (self-contained exe, core\, zapret\, ZIP),
    затем компилируется build\installer.iss. Оба варианта подписываются тем же
    сертификатом, что и exe (build\signing.thumbprint, -Thumbprint или без подписи).

.EXAMPLE
    powershell -File build\publish-installer.ps1

.EXAMPLE
    powershell -File build\publish-installer.ps1 -Version 0.9.1 -WithoutCore
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$IsccPath,
    [switch]$WithoutCore,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$NoSign
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$project = Join-Path $root 'VieNLink\VieNLink.csproj'

$shared = Get-VieNLinkVersion -Root $root
if (-not $Version -and $shared) {
    $Version = $shared.Version
}
if (-not $Version) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Encoding UTF8
    $Version = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'Не удалось определить версию: задайте -Version.' }
}

# ---------- 1. Портативный вариант: папка + ZIP ----------
$publishArgs = @{}
if ($Version) { $publishArgs.Version = $Version }
if ($WithoutCore) { $publishArgs.WithoutCore = $true }
& (Join-Path $PSScriptRoot 'publish-release.ps1') @publishArgs

$outDir = Join-Path $root "release\VieNLink-$Version-win-x64"
if (-not (Test-Path -LiteralPath (Join-Path $outDir 'VieNLink.exe'))) {
    throw "Не найдена портативная сборка: $outDir"
}

# ---------- 2. Установщик ----------
if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
}

if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw 'Не найден ISCC.exe (Inno Setup 6). Установите: winget install JRSoftware.InnoSetup'
}

$iss = Join-Path $PSScriptRoot 'installer.iss'
$setupPath = Join-Path $root "release\VieNLink-$Version-setup.exe"

Write-Host "Сборка установщика VieNLink $Version..." -ForegroundColor Cyan
& $IsccPath "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $setupPath)) { throw "Не найден $setupPath после сборки." }

# ---------- Подпись установщика ----------
if (-not $NoSign) {
    if (-not $Thumbprint) {
        $thumbprintFile = Join-Path $PSScriptRoot 'signing.thumbprint'
        if (Test-Path -LiteralPath $thumbprintFile) {
            $candidate = (Get-Content -LiteralPath $thumbprintFile -Raw).Trim()
            if ($candidate) { $Thumbprint = $candidate }
        }
    }

    $certificate = $null
    if ($Thumbprint) {
        $certificate = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Thumbprint -eq ($Thumbprint -replace '\s', '') } |
            Select-Object -First 1
    }

    if ($certificate) {
        Write-Host "Подпись установщика: $($certificate.Subject)" -ForegroundColor Cyan
        $signature = Set-AuthenticodeSignature -FilePath $setupPath -Certificate $certificate `
            -TimestampServer $TimestampUrl -HashAlgorithm SHA256
        if ($signature.Status -ne 'Valid') {
            Write-Warning "Статус подписи установщика: $($signature.Status) — $($signature.StatusMessage)"
        }
    }
    else {
        Write-Host 'Сертификат не задан: установщик собран без цифровой подписи.' -ForegroundColor Yellow
    }
}

$setupSize = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 1)
$zipPath = Join-Path $root "release\VieNLink-$Version-win-x64.zip"
$zipSize = if (Test-Path -LiteralPath $zipPath) {
    [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
}
else { 0 }

Write-Host ''
Write-Host 'Готово. Два варианта поставки:' -ForegroundColor Green
Write-Host "  1) Портативный: $outDir (ZIP: $zipPath, $zipSize МБ)"
Write-Host "  2) Установщик:  $setupPath ($setupSize МБ)"
