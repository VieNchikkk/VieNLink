#Requires -Version 5.1
<#
.SYNOPSIS
    Собирает подписанный Release APK Android-версии и кладёт его в release\.

.DESCRIPTION
    Версия берётся из VieNLink.Android\VieNLink.Android.csproj
    (ApplicationDisplayVersion / ApplicationVersion). APK подписывается
    долговременным ключом из %LOCALAPPDATA%\VieNLink\signing, чтобы следующие
    версии устанавливались поверх предыдущих. Путь и пароль можно переопределить
    параметрами или переменной VIENLINK_ANDROID_KEYSTORE_PASSWORD.

.EXAMPLE
    powershell -File build\publish-android.ps1

.EXAMPLE
    powershell -File build\publish-android.ps1 -Version 1.1.1 -VersionCode 3
#>
[CmdletBinding()]
param(
    [string]$Version,
    [int]$VersionCode = 0,
    [string]$Keystore,
    [string]$Alias = 'vienlink',
    [string]$StorePass,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'VieNLink.Android\VieNLink.Android.csproj'

if (-not $Version -or $VersionCode -le 0) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Encoding UTF8
    if (-not $Version) {
        $Version = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.ApplicationDisplayVersion } |
            Where-Object { $_ })[0]
    }
    if ($VersionCode -le 0) {
        $rawCode = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.ApplicationVersion } |
            Where-Object { $_ })[0]
        $VersionCode = if ($rawCode) { [int]$rawCode } else { 1 }
    }
}

if (-not $Version) { throw 'Не удалось определить версию Android-приложения.' }

$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
$sdk = Join-Path $env:LOCALAPPDATA 'android-sdk'
$jdk = Join-Path $env:LOCALAPPDATA 'jdk17'
if (-not (Test-Path -LiteralPath $jdk)) { throw "Не найден JDK: $jdk" }
if (-not (Test-Path -LiteralPath $sdk)) { throw "Не найден Android SDK: $sdk" }
# apksigner.bat запускает java и ищет её по JAVA_HOME/PATH.
$env:JAVA_HOME = $jdk

$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $buildTools) { throw 'Не найден build-tools в Android SDK.' }
$zipalign = Join-Path $buildTools.FullName 'zipalign.exe'
$apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
$keytool = Join-Path $jdk 'bin\keytool.exe'
if (-not (Test-Path -LiteralPath $zipalign) -or -not (Test-Path -LiteralPath $apksigner)) {
    throw "Не найдены zipalign/apksigner в $($buildTools.FullName)."
}

# ---------- Долговременный ключ подписи ----------
$signingDir = Join-Path $env:LOCALAPPDATA 'VieNLink\signing'
if (-not $Keystore) { $Keystore = Join-Path $signingDir 'vienlink-android.jks' }
$passFile = Join-Path $signingDir 'android-signing.pass'
if (-not $StorePass) {
    $StorePass = $env:VIENLINK_ANDROID_KEYSTORE_PASSWORD
}
if (-not $StorePass -and (Test-Path -LiteralPath $passFile)) {
    $StorePass = (Get-Content -LiteralPath $passFile -Raw).Trim()
}
if (-not $StorePass) {
    $bytes = New-Object byte[] 24
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $StorePass = ([Convert]::ToBase64String($bytes) -replace '[^A-Za-z0-9]', '')
    New-Item -ItemType Directory -Path $signingDir -Force | Out-Null
    Set-Content -LiteralPath $passFile -Value $StorePass -Encoding ASCII -NoNewline
    Write-Host "Создан пароль ключа подписи: $passFile" -ForegroundColor Cyan
}

if (-not (Test-Path -LiteralPath $Keystore)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Keystore) -Force | Out-Null
    Write-Host "Создаю ключ подписи Android: $Keystore" -ForegroundColor Cyan
    & $keytool -genkeypair -keystore $Keystore -alias $Alias -keyalg RSA -keysize 4096 `
        -validity 10000 -storepass $StorePass -keypass $StorePass `
        -dname 'CN=VieNLink, O=VieN, C=RU'
    if ($LASTEXITCODE -ne 0) { throw "keytool завершился с кодом $LASTEXITCODE." }
}
else {
    Write-Host "Ключ подписи: $Keystore (alias $Alias)" -ForegroundColor Cyan
}

# ---------- Сборка Release APK ----------
$publishDir = Join-Path $root "VieNLink.Android\bin\Release\net10.0-android\publish"
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

Write-Host "Сборка Android $Version (versionCode $VersionCode)..." -ForegroundColor Cyan
& $dotnet publish $project `
    -c Release `
    -f net10.0-android `
    -p:AndroidSdkDirectory="$sdk" `
    -p:JavaSdkDirectory="$jdk" `
    -p:ApplicationDisplayVersion="$Version" `
    -p:ApplicationVersion="$VersionCode" `
    -p:AndroidPackageFormat=apk `
    -p:EmbedAssembliesIntoApk=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE." }

$builtApk = Get-ChildItem -LiteralPath $publishDir -Filter '*-Signed.apk' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $builtApk) {
    $builtApk = Get-ChildItem -LiteralPath $publishDir -Filter '*.apk' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $builtApk) { throw "APK не найден в $publishDir." }

$releaseDir = Join-Path $root 'release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
if (-not $OutputPath) {
    $OutputPath = Join-Path $releaseDir "VieNLink-$Version-android.apk"
}

$aligned = Join-Path $env:TEMP "vienlink-android-$Version-aligned.apk"
Write-Host 'Выравнивание и подпись APK...' -ForegroundColor Cyan
& $zipalign -p -f 4 $builtApk.FullName $aligned
if ($LASTEXITCODE -ne 0) { throw "zipalign завершился с кодом $LASTEXITCODE." }

& $apksigner sign --ks $Keystore --ks-key-alias $Alias `
    --ks-pass "pass:$StorePass" --key-pass "pass:$StorePass" `
    --out $OutputPath $aligned
if ($LASTEXITCODE -ne 0) { throw "apksigner завершился с кодом $LASTEXITCODE." }

& $apksigner verify --print-certs $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'Подпись APK не прошла проверку.' }

Remove-Item -LiteralPath $aligned -Force -ErrorAction SilentlyContinue

$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
$shaFile = "$OutputPath.sha256"
Set-Content -LiteralPath $shaFile -Value "$hash  $(Split-Path -Leaf $OutputPath)" -Encoding ASCII
$sizeMb = [math]::Round((Get-Item -LiteralPath $OutputPath).Length / 1MB, 1)

Write-Host ''
Write-Host 'Готово:' -ForegroundColor Green
Write-Host "  APK:    $OutputPath ($sizeMb МБ)"
Write-Host "  SHA256: $shaFile"
Write-Host "  $hash"
