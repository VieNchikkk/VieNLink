#Requires -Version 5.1
<#
.SYNOPSIS
    Собирает релиз VieNLink: self-contained один exe, ZIP и, если ядро уже скачано,
    папку core с sing-box.exe рядом с exe.

.DESCRIPTION
    Версия берётся из VieNLink/VieNLink.csproj. Сборка кладётся в release\VieNLink-<версия>-win-x64.
    Подпись Authenticode выполняется, если указан -PfxPath или -Thumbprint.

.EXAMPLE
    powershell -File build\publish-release.ps1

.EXAMPLE
    powershell -File build\publish-release.ps1 -PfxPath C:\certs\vien.pfx -PfxPassword secret
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$Thumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$WithoutCore,
    [switch]$AllowRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'version.ps1')
$project = Join-Path $root 'VieNLink\VieNLink.csproj'
$runtime = 'win-x64'

$shared = Get-VieNLinkVersion -Root $root
if (-not $Version -and $shared) {
    $Version = $shared.Version
}
if (-not $Version) {
    [xml]$projectXml = Get-Content -LiteralPath $project -Encoding UTF8
    $Version = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
    if (-not $Version) { throw 'Не удалось определить версию: задайте -Version.' }
}

foreach ($processName in @('VieNLink', 'MyrrProxy')) {
    if (-not $AllowRunning -and (Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
        throw "$processName.exe запущен. Закройте приложение и повторите сборку."
    }
}

$outDir = Join-Path $root "release\VieNLink-$Version-$runtime"
$zipPath = Join-Path $root "release\VieNLink-$Version-$runtime.zip"

if (Test-Path -LiteralPath $outDir) {
    Remove-Item -LiteralPath $outDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "Сборка VieNLink $Version ($runtime, self-contained)..." -ForegroundColor Cyan
$fileVersion = "$Version.0"
dotnet publish $project `
    -c Release `
    -r $runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$fileVersion `
    -p:FileVersion=$fileVersion `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE." }

$exePath = Join-Path $outDir 'VieNLink.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw "Не найден $exePath после сборки." }

# Ядро рядом с exe: тогда приложение не скачивает 33 МБ при первом запуске.
$coreSource = Join-Path $env:LOCALAPPDATA 'VieNLink\core\sing-box.exe'
if (-not $WithoutCore -and (Test-Path -LiteralPath $coreSource)) {
    $coreTarget = Join-Path $outDir 'core'
    New-Item -ItemType Directory -Path $coreTarget -Force | Out-Null
    Copy-Item -LiteralPath $coreSource -Destination (Join-Path $coreTarget 'sing-box.exe') -Force
    Write-Host 'Ядро sing-box добавлено в core\ рядом с exe.' -ForegroundColor Cyan
}
else {
    Write-Host 'Ядро не включено: приложение скачает его при первом подключении.' -ForegroundColor Yellow
}

# Обход DPI: zapret кладётся рядом с exe, чтобы релиз работал на другом ПК без скачивания.
$zapretSource = Join-Path $env:LOCALAPPDATA 'VieNLink\zapret'
if (Test-Path -LiteralPath (Join-Path $zapretSource 'winws.exe')) {
    Copy-Item -LiteralPath $zapretSource -Destination (Join-Path $outDir 'zapret') -Recurse -Force
    Write-Host 'zapret добавлен в zapret\ рядом с exe.' -ForegroundColor Cyan
}
else {
    Write-Host 'zapret не включён: приложение скачает его при первом включении обхода.' -ForegroundColor Yellow
}

# Лицензия и сторонние компоненты — рядом с exe и в установщике.
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $outDir 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $outDir 'THIRD-PARTY-NOTICES.md') -Force
Write-Host 'Лицензия и THIRD-PARTY-NOTICES добавлены в поставку.' -ForegroundColor Cyan

Get-ChildItem -LiteralPath $outDir -Recurse -File | Unblock-File

$certificate = $null
if (-not $Thumbprint -and -not $PfxPath) {
    $thumbprintFile = Join-Path $PSScriptRoot 'signing.thumbprint'
    if (Test-Path -LiteralPath $thumbprintFile) {
        $candidate = (Get-Content -LiteralPath $thumbprintFile -Raw).Trim()
        if ($candidate) {
            $existing = Get-ChildItem Cert:\CurrentUser\My |
                Where-Object { $_.Thumbprint -eq ($candidate -replace '\s', '') } |
                Select-Object -First 1
            if ($existing) {
                $Thumbprint = $candidate
                Write-Host "Подпись: сохранённый сертификат $($existing.Subject)" -ForegroundColor Cyan
            }
            else {
                Write-Host 'Сертификат из build\signing.thumbprint не найден: exe будет без подписи.' -ForegroundColor Yellow
            }
        }
    }
}

if ($Thumbprint) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq ($Thumbprint -replace '\s', '') } |
        Select-Object -First 1
    if (-not $certificate) { throw "Сертификат с отпечатком $Thumbprint не найден в CurrentUser\My." }
}
elseif ($PfxPath) {
    $resolvedPfx = (Resolve-Path -LiteralPath $PfxPath).Path
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $resolvedPfx,
        $PfxPassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
}

if ($certificate) {
    Write-Host "Подпись: $($certificate.Subject)" -ForegroundColor Cyan
    $signature = Set-AuthenticodeSignature -FilePath $exePath -Certificate $certificate `
        -TimestampServer $TimestampUrl -HashAlgorithm SHA256
    if ($signature.Status -ne 'Valid') {
        Write-Warning "Статус подписи: $($signature.Status) — $($signature.StatusMessage)"
    }
}
else {
    Write-Host 'Сертификат не задан: exe собран без цифровой подписи.' -ForegroundColor Yellow
}

$readme = @"
VieNLink $Version — клиент свободных узлов для Windows
Автор: VieN
Статус: бета-версия. Основные режимы проверены, но возможны сбои —
для стабильной работы используйте свою подписку или свой сервер.

Запуск: VieNLink.exe. .NET устанавливать не требуется.
Варианты поставки: портативный ZIP (распаковать и запустить) или установщик
VieNLink-$Version-setup.exe (ставится в профиль пользователя, ярлыки в меню «Пуск»).

Порядок работы:
  1) Нажмите «Обновить списки».
  2) Откройте «Инструменты → Узлы» → подвкладку «Непроверенные».
  3) Включите «Только маскированные»: останутся узлы, чей трафик похож
     на обычный HTTPS (REALITY/Vision, WS·TLS, Hysteria2·obfs).
  4) Нажмите «Партия 500» или «Все узлы» — узлы с отметками ✓ появятся в «Рабочих».
  5) В подвкладке «Рабочие» нажмите «Подключить» — приложение само выберет быстрый и маскированный.

Вкладки:
  • Подключение — цветные кнопки с иконками YouTube, Discord, Универсальный,
    Telegram и вкладка Telegram MTProtoProxies рядом с ними.
  • Обход блокировок — «Обход Zapret», «Обход DPI», «TUN-обход (Beta)».
  • Инструменты — «Узлы», «Источники», «Туннель», «Диагностика», «Настройки».
  • «Журнал»; «Общие»; «Виджет» — открывает плавающее окно обхода.

Колонки:
  • Маскировка — тип транспорта: REALITY·Vision, WS·TLS·uTLS, Hysteria2·obfs, TLS или «без TLS».
  • Камуфляж — под какой сайт маскируется рукопожатие: YouTube/Google, Yandex, Cloudflare.

Автопереключение:
  Если узел перестал открывать сайт, приложение само сменит его на другой
  камуфляж (YouTube → Yandex → Cloudflare) — без перезапуска и без вопросов.

Только проверенные узлы:
  По умолчанию приложение подключается только к узлам с задержкой
  (подвкладка «Рабочие»). Непроверенные и мёртвые серверы не пробуются.
  Отключить это можно в настройках: «Подключаться только к проверенным узлам».

Личный зашифрованный канал:
  Добавьте свой VPS или личную подписку в «Инструменты → Источники», отметьте
  «Это моя подписка» и включите «Подключаться только к моим подпискам».
  Важно: шифрование до сервера (REALITY · Vision, WS · TLS) само по себе
  не создаёт выход в интернет. Публичные бесплатные узлы нестабильны;
  свой сервер с REALITY — самый надёжный вариант.

Обход DPI (zapret):
  «Обход блокировок → Обход Zapret» → «Включить обход». Требуются права администратора (UAC) и WinDivert.
  Работает без прокси-узла: разрезает и подменяет TLS-пакеты, чтобы DPI
  не видел домен. Перед включением узел отключается; при подключении
  к узлу zapret останавливается автоматически.
  Стратегия подбирается сама при включении и запоминается; список стратегий
  в интерфейсе — только для информации, отсортирован по лучшему результату.
  Галочка «Блокировать QUIC (UDP/443)» на время обхода создаёт правило
  Windows Firewall, чтобы видео шло по TCP; при остановке правило снимается.
  Файлы zapret лежат в папке zapret\ рядом с программой — релиз можно переносить
  на другой компьютер целиком. Если каталог защищён от записи (Program Files),
  используется %LOCALAPPDATA%\VieNLink\zapret.

Свой обход DPI (WinDivert, без zapret):
  «Обход блокировок → Обход DPI» — собственный движок VieNLink, без winws.exe.
  Он подмешивает фейковый ClientHello с google SNI, badseq, TCP MD5 и TTL 128
  (11 повторов), а настоящий ClientHello режет на части и отправляет в обратном
  порядке. Рабочая стратегия проверена live: страница YouTube 204/200, Discord 200,
  Cloudflare 204. Видео YouTube на этом провайдере не загружается ни через наш движок,
  ни через zapret — видео-CDН блокируется по IP, для видео нужен прокси-узел.
  Десинк применяется только к целевым доменам (YouTube, Telegram, Discord, X,
  Instagram, Facebook), остальные сайты идут напрямую. Нужны права администратора;
  WinDivert копируется из папки zapret или скачивается автоматически.
  Есть блокировка QUIC (UDP/443), чтобы видео шло по TCP.
  Стратегия подбирается автоматически при включении: проверяются YouTube,
  видео и t.me, лучшая запоминается и поднимается в начало списка.
  Взаимоисключающий режим: при подключении к узлу или включении zapret
  движок останавливается.

Видео YouTube и Telegram через узел:
  Кнопка «YouTube» на «Подключении» подключает лучший проверенный узел с доступом
  к YouTube, кнопка «Telegram» — узел с доступом к Telegram. Нужен рабочий узел.
  Вывод по видео: локальный видео-узел Google
  у провайдера блокируется по IP — проверены все 29 режимов zapret, domain fronting,
  подмена IP/DNS и IPv6, ни один способ не помогает; работает только прокси-узел.

TUN-обход (без узла и без WinDivert):
  «Обход блокировок → TUN-обход (Beta)» — самый глубокий режим: sing-box поднимает TUN-адаптер
  VieNLink-Bypass, а TCP идёт в локальный SOCKS5-форвардер VieNLink. Форвардер
  открывает прямой канал до сайта и режет TLS ClientHello на сегменты.
  Нужны права администратора и wintun.dll. QUIC (UDP/443) блокируется,
  чтобы браузер перешёл на TCP. Работает по IPv4; IPv6 пока идёт напрямую.
  Трафик самого VieNLink.exe идёт напрямую,
  иначе форвардер замкнулся бы на собственный TUN. Это отдельный режим:
  при подключении к узлу, zapret или «Своему обходу DPI» он останавливается.
  Если Cloudflare открывается, а YouTube/t.me — нет, ТСПУ блокирует их
  по IP/протоколу: для них нужен прокси-узел или zapret.

Быстрый обход DPI (TLS-фрагментация sing-box):  «Настройки» → «Быстрый обход DPI» → «Фрагментировать TLS-рукопожатие».
  sing-box сам режет ClientHello на части (tls.fragment / record_fragment):
  DPI не видит SNI целиком. Без WinDivert и без прав администратора,
  но работает только для трафика через прокси-узел и применяется
  при следующем подключении.

Плавающий виджет (вкладка «Виджет» рядом с «Общие» или меню в трее):
  Три кнопки — «Обход запрет» (zapret), «Свой обход DPI» и «Видео YouTube».
  Нажатие включает способ, повторное — выключает. Стратегия подбирается сама
  при первом включении и запоминается. Внизу мелко подписано, какой режим
  работает и по какой стратегии; в свёрнутом виде остаются точка состояния
  (зелёная — включено, красная — выключено) и название режима.
  Одновременно работает только один способ: включение другого отключает прежний.

Подвкладки «Узлов»:
  • Непроверенные — новые узлы из источников;
  • Рабочие — открыли YouTube и/или Telegram.
  Непроверенный узел, не открывший ни один адрес, удаляется сразу.
  Рабочий узел, переставший отвечать, получает до 10 попыток подряд: в колонке
  «Провалы» видно, сколько израсходовано, — только после этого он удаляется.

Telegram MTProtoProxies:
  Открывается одноимённой вкладкой рядом с кнопками сервисов; кнопка Telegram
  в ряду — быстрое подключение через лучший узел.
  Сбор идёт сам: приложение автоматически читает каналы через RSS-зеркала,
  рабочие прокси проверяет каждые 10 минут, нерабочие — редко, на оживание.
  «Проверить соединение» запускает ручную проверку; неответившие уходят
  в «Нерабочие» и удаляются через 24 часа.

Проверка «это технология или просто сервер»:
  1) Подключитесь; «Инструменты → Диагностика» покажет активный
     и запасной камуфляж, а также счётчики переключений.
  2) Нажмите «Проверить туннель» — YouTube и t.me должны открыться.
  3) Заблокируйте IP:порт активного узла в брандмауэре Windows (или дождитесь,
     когда умрёт бесплатный узел).
  4) Через 45–60 секунд приложение переключится само: в журнале будет
     «Переключено без перезапуска ядра», в диагностике вырастет счётчик «тёплых».
  5) Выключите «Автопереключение при сбое узла» и повторите: соединение просто
     упадёт. Это и есть вклад нашей логики, а не сервера.

Кнопка «?» на подвкладке «Инструменты → Узлы» открывает эту памятку прямо в приложении.

Данные: %LOCALAPPDATA%\VieNLink (сам zapret — в папке zapret рядом с программой, если туда можно писать).

Лицензия и сторонние компоненты:
  LICENSE — GNU GPL-3.0 на весь проект (включая Android-версию).
  THIRD-PARTY-NOTICES.md — sing-box, zapret (MIT), WinDivert и cygwin1.dll (LGPL),
  ссылки на исходники и порядок комплектации релиза.

Узлы берутся из открытых источников и никем не проверяются: владелец узла видит ваш трафик.
Не входите через бесплатные узлы в банки и рабочие аккаунты.

Если Windows показала «Windows защитила ваш компьютер»:
  1) «Подробнее» → «Выполнить в любом случае»;
  или
  2) правой кнопкой по VieNLink.exe → «Свойства» → «Разблокировать» → «ОК».
"@
Set-Content -LiteralPath (Join-Path $outDir 'ЧИТАЙ.txt') -Value $readme -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
Get-ChildItem -LiteralPath $zipPath | Unblock-File

Write-Host ''
Write-Host "Готово: $outDir" -ForegroundColor Green
Write-Host "Архив: $zipPath" -ForegroundColor Green
