#Requires -Version 5.1
<#
.SYNOPSIS
    Собирает отдельную поставку браузера: портативный ZIP и установщик setup.exe.

.DESCRIPTION
    Версия берётся из build\BrowserVersion.props — она не связана с версией VPN-приложения:
    браузер ставится и обновляется независимо.

.EXAMPLE
    powershell -File build\publish-browser.ps1

.EXAMPLE
    powershell -File build\publish-browser.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')

$shared = Get-VieNLinkBrowserVersion -Root $root
if (-not $Version -and $shared) {
    $Version = $shared.Version
}
if (-not $Version) {
    throw 'Не удалось определить версию браузера (build\BrowserVersion.props).'
}

$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

$project = Join-Path $root 'VieNLink.Browser\VieNLink.Browser.csproj'
$outDir = Join-Path $root "release\VieNLink-Browser-$Version-win-x64"
$zipPath = Join-Path $root "release\VieNLink-Browser-$Version-win-x64.zip"

Write-Host "Сборка VieNLink Браузер $Version..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $outDir) {
    Remove-Item -LiteralPath $outDir -Recurse -Force
}

& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $outDir `
    -p:Version=$Version -p:FileVersion=$Version -p:AssemblyVersion=$Version
if ($LASTEXITCODE -ne 0) {
    throw 'Сборка браузера завершилась ошибкой.'
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath

if (-not $IsccPath) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $IsccPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $IsccPath = $command.Source }
    }
}

if (-not $IsccPath) {
    Write-Host 'ISCC.exe (Inno Setup 6) не найден — установщик не собран, остался только ZIP.' -ForegroundColor Yellow
} else {
    $iss = Join-Path $PSScriptRoot 'browser-installer.iss'
    & $IsccPath "/DAppVersion=$Version" $iss
    if ($LASTEXITCODE -ne 0) {
        throw 'Inno Setup завершился ошибкой.'
    }
}

$setupPath = Join-Path $root "release\VieNLink-Browser-$Version-setup.exe"
$zipSize = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
Write-Host ''
Write-Host 'Готово:' -ForegroundColor Green
Write-Host "  Портативный: $zipPath ($zipSize МБ)"
if (Test-Path -LiteralPath $setupPath) {
    $setupSize = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 1)
    Write-Host "  Установщик:  $setupPath ($setupSize МБ)"
}
