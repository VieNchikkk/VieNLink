#Requires -Version 5.1
<#
.SYNOPSIS
    Собирает магазинную сборку Android: APK для RuStore и AAB для Google Play.
    В ней нет REQUEST_INSTALL_PACKAGES и самообновления (StoreBuild=true).

.DESCRIPTION
    Версия берётся из VieNLink.Android\VieNLink.Android.csproj.
    Подпись — долговременным ключом из %LOCALAPPDATA%\VieNLink\signing:
    тем же, что у GitHub-APK, чтобы обновления ставились поверх.
    Результат: release\VieNLink-<версия>-store.apk и release\VieNLink-<версия>-store.aab.

.EXAMPLE
    powershell -File build\publish-store.ps1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [int]$VersionCode = 0,
    [string]$Keystore,
    [string]$Alias = 'vienlink',
    [string]$StorePass
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$project = Join-Path $root 'VieNLink.Android\VieNLink.Android.csproj'

$shared = Get-VieNLinkVersion -Root $root
if (-not $Version -and $shared) {
    $Version = $shared.Version
}
if ($VersionCode -le 0 -and $shared -and $shared.VersionCode -gt 0) {
    $VersionCode = $shared.VersionCode
}
if (-not $Version) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Encoding UTF8
    $Version = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.ApplicationDisplayVersion } |
        Where-Object { $_ })[0]
}
if ($VersionCode -le 0) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Encoding UTF8
    $rawCode = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.ApplicationVersion } |
        Where-Object { $_ })[0]
    $VersionCode = if ($rawCode) { [int]$rawCode } else { 1 }
}
if (-not $Version) { throw 'Не удалось определить версию Android-приложения.' }

$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
$sdk = Join-Path $env:LOCALAPPDATA 'android-sdk'
$jdk = Join-Path $env:LOCALAPPDATA 'jdk17'
if (-not (Test-Path -LiteralPath $jdk)) { throw "Не найден JDK: $jdk" }
if (-not (Test-Path -LiteralPath $sdk)) { throw "Не найден Android SDK: $sdk" }
$env:JAVA_HOME = $jdk

$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $buildTools) { throw 'Не найден build-tools в Android SDK.' }
$zipalign = Join-Path $buildTools.FullName 'zipalign.exe'
$apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
$aapt = Join-Path $buildTools.FullName 'aapt.exe'
$jarsigner = Join-Path $jdk 'bin\jarsigner.exe'
foreach ($tool in @($zipalign, $apksigner, $aapt, $jarsigner)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Не найден инструмент: $tool" }
}

# ---------- Ключ и пароль ----------
$signingDir = Join-Path $env:LOCALAPPDATA 'VieNLink\signing'
if (-not $Keystore) { $Keystore = Join-Path $signingDir 'vienlink-android.jks' }
$passFile = Join-Path $signingDir 'android-signing.pass'
if (-not $StorePass) { $StorePass = $env:VIENLINK_ANDROID_KEYSTORE_PASSWORD }
if (-not $StorePass -and (Test-Path -LiteralPath $passFile)) {
    $StorePass = (Get-Content -LiteralPath $passFile -Raw).Trim()
}
if (-not $StorePass) { throw "Не найден пароль ключа: $passFile" }
if (-not (Test-Path -LiteralPath $Keystore)) { throw "Не найден ключ подписи: $Keystore" }

$releaseDir = Join-Path $root 'release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# ---------- APK для RuStore ----------
# StoreBuild меняет манифест (убирает REQUEST_INSTALL_PACKAGES). Инкрементальная сборка
# может переиспользовать GitHub-вариант, поэтому чистим obj/bin Release перед публикацией.
$objDir = Join-Path $root 'VieNLink.Android\obj\Release\net10.0-android'
$binDir = Join-Path $root 'VieNLink.Android\bin\Release\net10.0-android'
foreach ($dir in @($objDir, $binDir)) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

$apkPublish = Join-Path $root "VieNLink.Android\bin\Release\net10.0-android\store-apk"
if (Test-Path -LiteralPath $apkPublish) { Remove-Item -LiteralPath $apkPublish -Recurse -Force }

Write-Host "Сборка APK для RuStore $Version (versionCode $VersionCode)..." -ForegroundColor Cyan
& $dotnet publish $project `
    -c Release `
    -f net10.0-android `
    -p:StoreBuild=true `
    -p:AndroidSdkDirectory="$sdk" `
    -p:JavaSdkDirectory="$jdk" `
    -p:ApplicationDisplayVersion="$Version" `
    -p:ApplicationVersion="$VersionCode" `
    -p:AndroidPackageFormats=apk `
    -p:EmbedAssembliesIntoApk=true `
    -o $apkPublish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (APK) завершился с кодом $LASTEXITCODE." }

$builtApk = Get-ChildItem -LiteralPath $apkPublish -Filter '*.apk' |
    Sort-Object Length -Descending | Select-Object -First 1
if (-not $builtApk) { throw "APK не найден в $apkPublish." }

$apkPath = Join-Path $releaseDir "VieNLink-$Version-store.apk"
$aligned = Join-Path $env:TEMP "vienlink-store-$Version-aligned.apk"
& $zipalign -p -f 4 $builtApk.FullName $aligned
if ($LASTEXITCODE -ne 0) { throw "zipalign завершился с кодом $LASTEXITCODE." }
& $apksigner sign --ks $Keystore --ks-key-alias $Alias `
    --ks-pass "pass:$StorePass" --key-pass "pass:$StorePass" `
    --out $apkPath $aligned
if ($LASTEXITCODE -ne 0) { throw "apksigner завершился с кодом $LASTEXITCODE." }
Remove-Item -LiteralPath $aligned -Force -ErrorAction SilentlyContinue
& $apksigner verify --print-certs $apkPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Подпись APK не прошла проверку.' }

# Магазинная сборка обязана быть без установки пакетов: иначе Play/RuStore отклонят.
$badging = (& $aapt dump badging $apkPath 2>&1 | Out-String)
if ($badging -match 'REQUEST_INSTALL_PACKAGES') {
    throw 'В магазинном APK остался REQUEST_INSTALL_PACKAGES — сборка не годится для RuStore/Play.'
}

# ---------- AAB для Google Play ----------
$aabPublish = Join-Path $root "VieNLink.Android\bin\Release\net10.0-android\store-aab"
if (Test-Path -LiteralPath $aabPublish) { Remove-Item -LiteralPath $aabPublish -Recurse -Force }

Write-Host "Сборка AAB для Google Play $Version (versionCode $VersionCode)..." -ForegroundColor Cyan
& $dotnet publish $project `
    -c Release `
    -f net10.0-android `
    -p:StoreBuild=true `
    -p:AndroidSdkDirectory="$sdk" `
    -p:JavaSdkDirectory="$jdk" `
    -p:ApplicationDisplayVersion="$Version" `
    -p:ApplicationVersion="$VersionCode" `
    -p:AndroidPackageFormats=aab `
    -p:EmbedAssembliesIntoApk=true `
    -o $aabPublish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (AAB) завершился с кодом $LASTEXITCODE." }

$builtAab = Get-ChildItem -LiteralPath $aabPublish -Filter '*.aab' |
    Sort-Object Length -Descending | Select-Object -First 1
if (-not $builtAab) { throw "AAB не найден в $aabPublish." }

$aabPath = Join-Path $releaseDir "VieNLink-$Version-store.aab"
Copy-Item -LiteralPath $builtAab.FullName -Destination $aabPath -Force

# AAB подписывается JAR-подписью (apksigner для APK, jarsigner для AAB).
& $jarsigner -keystore $Keystore -storepass $StorePass -keypass $StorePass `
    -sigalg SHA256withRSA -digestalg SHA-256 $aabPath $Alias
if ($LASTEXITCODE -ne 0) { throw "jarsigner завершился с кодом $LASTEXITCODE." }
& $jarsigner -verify $aabPath | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Подпись AAB не прошла проверку.' }

# ---------- Итоги ----------
foreach ($file in @($apkPath, $aabPath)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    $shaFile = "$file.sha256"
    Set-Content -LiteralPath $shaFile -Value "$hash  $(Split-Path -Leaf $file)" -Encoding ASCII
    $sizeMb = [math]::Round((Get-Item -LiteralPath $file).Length / 1MB, 1)
    Write-Host "  $file ($sizeMb МБ)" -ForegroundColor Green
    Write-Host "  SHA256: $hash"
}
