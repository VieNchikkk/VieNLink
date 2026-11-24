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
    [string]$OutputPath,
    # Тестовая сборка: package com.vienlink.app.test, имя VieNLink Test, Java-платформа.
    [switch]$TestBuild,
    # Телевизионная сборка: package com.vienlink.app.tv, LEANBACK_LAUNCHER, пульт.
    [switch]$TvBuild,
    # Отключить AOT: на Android 11 (Lenovo TB-J706F) Release с AOT падает SIGSEGV.
    [switch]$NoAot,
    # Целевой ABI. Пусто — как решит .NET (arm64-v8a + x86_64). armeabi-v7a — 32-битные
    # телевизоры (Xiaomi MiTV-MOOQ1, Android 11): нужен RuntimeIdentifier android-arm
    # и 32-битный libgojni.so (Libs\libgojni-armeabi-v7a.so), его в libbox.aar нет.
    [ValidateSet('', 'armeabi-v7a', 'arm64-v8a')]
    [string]$Abi = '',
    # Собрать с C#-платформой libbox (аварийный откат и проверка гипотез).
    [switch]$NoJava
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
# Чистая сборка: StoreBuild меняет манифест, иначе можно унаследовать магазинный вариант
# без REQUEST_INSTALL_PACKAGES (тогда GitHub-APK не сможет обновляться сам).
$objDir = Join-Path $root 'VieNLink.Android\obj\Release\net10.0-android'
$binDir = Join-Path $root 'VieNLink.Android\bin\Release\net10.0-android'
foreach ($dir in @($objDir, $binDir)) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

$publishDir = Join-Path $root "VieNLink.Android\bin\Release\net10.0-android\publish"
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

Write-Host "Сборка Android $Version (versionCode $VersionCode)$(if ($TestBuild) { ', тестовая' } else { '' })..." -ForegroundColor Cyan
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-f', 'net10.0-android',
    "-p:AndroidSdkDirectory=$sdk",
    "-p:JavaSdkDirectory=$jdk",
    "-p:ApplicationDisplayVersion=$Version",
    "-p:ApplicationVersion=$VersionCode",
    '-p:AndroidPackageFormat=apk',
    '-p:EmbedAssembliesIntoApk=true',
    '-o', $publishDir
)
if ($TestBuild) {
    $publishArgs += '-p:TestBuild=true'
}
if ($TvBuild) {
    $publishArgs += '-p:TvBuild=true'
}
if ($NoAot) {
    $publishArgs += '-p:RunAOTCompilation=false'
    $publishArgs += '-p:AndroidEnableProfiledAot=false'
}
if ($NoJava) {
    # Аварийный откат на C#-платформу libbox (проверка гипотез, диагностика).
    $publishArgs += '-p:AndroidJavaPlatform=false'
}
if ($Abi -eq 'armeabi-v7a') {
    # 32-битный ARM: в libbox.aar только arm64-v8a, 32-битную библиотеку
    # добавляем в APK после сборки (см. ниже).
    $publishArgs += '-r'
    $publishArgs += 'android-arm'
}
elseif ($Abi -eq 'arm64-v8a') {
    $publishArgs += '-r'
    $publishArgs += 'android-arm64'
}
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE." }

$builtApk = Get-ChildItem -LiteralPath $publishDir -Filter '*-Signed.apk' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $builtApk) {
    $builtApk = Get-ChildItem -LiteralPath $publishDir -Filter '*.apk' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $builtApk) { throw "APK не найден в $publishDir." }

# 32-битный ARM: в libbox.aar есть только jni/arm64-v8a/libgojni.so, поэтому
# 32-битную библиотеку ядра добавляем в готовый APK перед выравниванием и подписью.
if ($Abi -eq 'armeabi-v7a') {
    $libsDir = Join-Path $root 'VieNLink.Android\Libs'
    $armSo = Join-Path $libsDir 'libgojni-armeabi-v7a.so'
    $armZip = "$armSo.zip"
    if (-not (Test-Path -LiteralPath $armSo)) {
        # В репозитории библиотека лежит сжатой: 110 МБ — много для git (лимит 100 МБ).
        if (-not (Test-Path -LiteralPath $armZip)) {
            throw "Нет 32-битной библиотеки ядра: $armSo или $armZip (см. docs\android-design.md §15)."
        }
        $armSo = Join-Path $env:TEMP 'libgojni-armeabi-v7a.so'
        Write-Host 'Распаковываю 32-битную библиотеку ядра...' -ForegroundColor Cyan
        Expand-Archive -LiteralPath $armZip -DestinationPath (Split-Path -Parent $armSo) -Force
    }
    Write-Host 'Добавляю 32-битную библиотеку ядра (armeabi-v7a)...' -ForegroundColor Cyan
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $zip = [System.IO.Compression.ZipFile]::Open($builtApk.FullName, 'Update')
    try {
        $entryName = 'lib/armeabi-v7a/libgojni.so'
        $old = $zip.GetEntry($entryName)
        if ($old) { $old.Delete() }
        $entry = $zip.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $dst = $entry.Open()
        $src = [System.IO.File]::OpenRead($armSo)
        try { $src.CopyTo($dst) } finally { $src.Dispose(); $dst.Dispose() }
    }
    finally { $zip.Dispose() }
}

$releaseDir = Join-Path $root 'release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
if (-not $OutputPath) {
    $suffix = if ($TvBuild) { '-tv' } elseif ($TestBuild) { '-test' } else { '-phone' }
    if ($Abi -eq 'armeabi-v7a') { $suffix += '-arm32' }
    $OutputPath = Join-Path $releaseDir "VieNLink-$Version$suffix.apk"
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

# После сборки убираем артефакты прошлых версий: не копим мусор в release\.
& (Join-Path $PSScriptRoot 'clean.ps1') -Keep $Version