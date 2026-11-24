#Requires -Version 5.1
<#
.SYNOPSIS
    Возвращает телевизор Xiaomi к состоянию до чистки (см. docs/tv-cleanup.md).

.DESCRIPTION
    Включает отключённые пакеты и возвращает удалённые для пользователя.
    Сторонние приложения, удалённые полностью (Яндекс.Браузер TV и др.),
    скрипт не вернёт — их нужно ставить заново из APK.

.EXAMPLE
    powershell -File build\tv-restore.ps1

.EXAMPLE
    powershell -File build\tv-restore.ps1 -Device 192.168.3.10:5555
#>
[CmdletBinding()]
param(
    [string]$Device = '192.168.3.10:5555',
    [string]$Adb = (Join-Path $env:LOCALAPPDATA 'android-sdk\platform-tools\adb.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Adb)) {
    throw "Не найден adb: $Adb"
}

# Отключённые через pm disable-user.
$disabled = @(
    'com.google.android.katniss',            # Google Ассистент
    'com.google.android.tts',                # синтез речи Google
    'com.google.android.marvin.talkback',    # TalkBack
    'com.google.android.backdrop',           # фото-заставка
    'com.google.android.feedback',           # отзывы/телеметрия Google
    'com.google.android.tungsten.setupwraith', # мастер настройки Google TV
    'com.google.android.tvrecommendations',  # рекомендации на главном
    'com.google.android.youtube.tvmusic',    # YouTube Music
    'com.xiaomi.amazon.alexa',               # Alexa
    'com.android.settings.intelligence',     # поиск в настройках
    'com.mitv.gallery',                      # галерея Xiaomi
    'com.mitv.dream'                         # заставка Xiaomi
)

# Удалённые через pm uninstall --user 0 (APK остались в системе).
$removed = @(
    'com.mitv.tvhome.atv',                   # PatchWall
    'com.mitv.tvhome.michannel',             # каналы PatchWall
    'com.mitv.tvhome.mitvplus',              # MiTV+
    'com.google.android.videos',             # Google Play Фильмы и ТВ
    'com.google.android.play.games',         # Google Play Игры
    'com.amazon.amazonvideo.livingroom',     # Amazon Prime Video
    'com.netflix.ninja'                      # Netflix
)

Write-Host "Телевизор: $Device" -ForegroundColor Cyan

& $Adb -s $Device shell 'echo ok' | Out-Null

Write-Host ''
Write-Host 'Включаю отключённые:' -ForegroundColor Cyan
foreach ($pkg in $disabled) {
    $out = & $Adb -s $Device shell "pm enable $pkg" 2>&1
    Write-Host ("  {0,-45} {1}" -f $pkg, $out)
}

Write-Host ''
Write-Host 'Возвращаю удалённые для пользователя:' -ForegroundColor Cyan
foreach ($pkg in $removed) {
    $out = & $Adb -s $Device shell "pm install-existing --user 0 $pkg" 2>&1
    Write-Host ("  {0,-45} {1}" -f $pkg, $out)
}

Write-Host ''
Write-Host 'Осталось отключённым:' -ForegroundColor Cyan
& $Adb -s $Device shell 'pm list packages -d'

Write-Host ''
Write-Host 'Полностью удалённые приложения (нужен APK):' -ForegroundColor Yellow
Write-Host '  com.yandex.browser.tv (Яндекс.Браузер TV), tv.lfstrm.smotreshka (Смотрешка)'
Write-Host ''
Write-Host 'Готово. Лаунчер можно перезапустить: am force-stop com.google.android.tvlauncher' -ForegroundColor Green
