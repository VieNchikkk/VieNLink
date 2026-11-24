# Передача задачи: YouTube не работает в VPN на Android (телефон/планшет)

Состояние на 24.11.2026, 02:20. Проект `C:\MyProg\MyrrProxy` (VieNLink).
Сборка/проверки — см. `AGENTS.md`; журнал решений — `docs/plan-2026.md`.

## Коротко

На **ПК всё работает**, на **телевизоре работает** (Zona грузится и играет видео через VPN).
На **телефоне и планшете** приложение подключается, показывает выход через сервер, но
**трафик приложений до туннеля не доходит**: Chrome/YouTube висят, в логе ядра нет
`inbound/tun`, счётчики `tun0` почти не растут. При этом собственный тест скорости внутри
ядра качает 669 КБ (`speed cdn.jsdelivr.net: 669884 байт за 0,6 с`) — то есть выход в прокси
живой, а вход из TUN — нет.

Пользователь подозревает переход на Java-платформу libbox (до него YouTube работает).
Это главная гипотеза, но есть и вторая, более вероятная (см. ниже).

## Устройства и доступ

| Устройство | Как подключён | Пакет | Версия сейчас |
|---|---|---|---|
| Телефон realme RMX5120, Android 16 | USB, `adb -s 3K6614003U100000` | `com.vienlink.app` | 1.3.43 |
| Планшет Lenovo TB-J706F, Android 11 | Wi-Fi ADB `192.168.3.14:<порт>` (порт меняется, сопряжение кодом) | `com.vienlink.app` | 1.3.38 |
| ТВ Xiaomi MiTV-MOOQ1, Android 11, 32-бит ARM, 1,7 ГБ | `adb -s 192.168.3.10:5555` | `com.vienlink.app.tv` | 1.3.37 (arm32) |
| ПК Windows | `%LOCALAPPDATA%\Programs\VieNLink\VieNLink.exe` | — | 1.3.37 |

Android SDK/ADB: `%LOCALAPPDATA%\android-sdk\platform-tools\adb.exe`.
Планшет: `adb mdns services` → `_adb-tls-connect._tcp`, при необходимости
`adb pair <ip>:<pairing-port> <код>` (код показывает планшет).

## Что именно проверено (факты, не догадки)

1. **Ядро и прокси работают**: при подключении UI показывает `Выход: 🇳🇱 45.12.255.56`,
   счётчик трафика растёт, тест скорости внутри ядра проходит.
2. **Трафик приложений не идёт в туннель**: снимал `/proc/net/dev` по `tun0` до/после
   загрузки YouTube и Chrome — прирост 90–200 байт вместо сотен килобайт. В logcat нет ни
   одной строки `inbound/tun` при загрузке YouTube (а при работе самого ядра они есть).
3. **VPN включает приложения**: после принудительного «Все приложения через сервер»
   `dumpsys connectivity` для VPN-сети показывает `Uids: <{0-10125, 10127-…, 10150-10330, …}>`
   — uid YouTube (10189) внутри. То есть allowlist не мешает (осторожно: в том же выводе
   есть `TRACK_DEFAULT`-запрос Wi-Fi с 6 uid — это не VPN).
4. **`curl` из adb shell не годится для тестов**: он ходит мимо VPN
   (exit IP = домашний 94.243.208.77), поэтому все «TUN-тесты» через curl недействительны.
   Проверять только приложением/внутренними запросами ядра.
5. **Напрямую (без VPN) YouTube в РФ не открывается**: TLS к youtube.com, rutracker, t.me
   виснет после ClientHello; google.com/ya.ru/github работают. Отсюда: «работает» для
   google/cloudflare ничего не доказывает.
6. **ТВ — контрольная точка**: та же схема TUN, `explicitDns` (UDP 77.88.8.8 + 1.1.1.1,
   `ipv4_only`), Zona-каталог и **воспроизведение фильма** идут через VPN.
7. На планшете (1.3.37) лента YouTube грузилась (заголовки), но превью `i.ytimg.com`,
   `yt3.ggpht.com` — нет. После 1.3.38 (блокировка QUIC) стало хуже (только спиннер),
   в 1.3.39 блокировку QUIC убрал.

## Гипотезы (в порядке приоритета)

### 1. arm64-ядро `libgojni.so` из старого `libbox.aar` не годится (наиболее вероятно)

В `VieNLink.Android\Libs\libbox.aar` лежит **только** `jni/arm64-v8a/libgojni.so` (118 МБ),
собранный 21.11.2026. Для ТВ мы сами собрали 32-битную библиотеку (sing-box 1.14.4,
`-target=android/arm`, теги `with_gvisor,with_quic,with_utls,with_clash_api,with_wireguard`,
`-ldflags=-checklinkname=0`) — и **на ТВ TUN работает**. На телефоне/планшете используется
старый arm64-бинарник — и TUN не передаёт данные.

Что сделать первым:

1. Собрать arm64 той же командой (см. `docs/android-design.md` §15 «32-битный ARM»,
   заменить `-target=android/arm` на `-target=android/arm64`).
2. Заменить `jni/arm64-v8a/libgojni.so` в `libbox.aar` (или подложить отдельным файлом и
   внедрять в APK, как сделано для `libgojni-armeabi-v7a.so` — см. `publish-android.ps1`,
   блок «32-битный ARM»).
3. Собрать `publish-android.ps1 -NoAot`, поставить на телефон, проверить YouTube.

Исходники sing-box 1.14.4: `%TEMP%\sb-src\sing-box-1.14.4` (уже распакованы, `go.mod`
поправлен: `go get -tool golang.org/x/mobile/cmd/gobind`, версия Go 1.27.4).
Toolchain: Go `%LOCALAPPDATA%\go-sdk\bin`, gomobile `%USERPROFILE%\go\bin`, NDK
`%LOCALAPPDATA%\android-ndk` (r27c), JDK `%LOCALAPPDATA%\jdk17`.

### 2. Java-платформа libbox (подозрение пользователя)

`AndroidJavaPlatform` с 24.11.2026 включён по умолчанию для всех сборок. Java-мост
(`VieNLink.Android\Java\VieNLinkJavaBridge.java`) отдаёт TUN как `tun.getFd()` (как SFA),
`protect(fd)` вызывает `service.protect(fd)`, монитор интерфейса исключает VPN-интерфейсы —
выглядит корректно, но **не проверено на живом трафике**.

Проверка: собрать с C#-платформой и сравнить. Параметр готов:
`publish-android.ps1 -NoAot -NoJava` (передаёт `-p:AndroidJavaPlatform=false`).
Поставить на телефон, проверить YouTube.
Если с C# работает — виноват мост, сравнить `TunnelService.OpenTun` (C#) и
`VieNLinkJavaBridge.openTun` (Java), а также `LibBoxPlatform.AutoDetectInterfaceControl`
и `VieNLinkJavaPlatform.autoDetectInterfaceControl`.

### 3. Что ещё проверять, если первые две не дали результата

- Добавить в ядро диагностику: считать и логировать `inbound/tun` соединения (сейчас при
  загрузке YouTube их нет вообще), вывести число активных подключений.
- Сравнить конфиг ТВ и телефона (ТВ: `explicitDns` + `ipv4_only`; телефон: то же с 1.3.39).
- Проверить `tun0` MTU (1400) и `route.auto_detect_interface`.
- Снять эффективный конфиг (без UUID) в файл и посмотреть `route.final`, `dns`, `rules`.

## Что уже сделано в этой итерации (не переделывать)

- **Java-платформа стала основной** (`VieNLink.Android.csproj`: `AndroidJavaPlatform=true`
  по умолчанию). Телефон/планшет/ТВ собраны с Java.
- **Сборки разделены по именам**: `-phone` (телефон и планшет, `-android` больше не
  используется), `-tv-arm32` (ТВ). Обновлятор `AndroidUpdate` берёт только «свой» APK
  (`BuildInfo.IsTelevisionBuild`, фильтр `-tv`/`-store`).
- **ТВ-сборка**: `publish-android.ps1 -TvBuild -Abi armeabi-v7a -NoAot`; 32-битная
  `libgojni.so` лежит сжатой в `VieNLink.Android\Libs\libgojni-armeabi-v7a.so.zip` и
  внедряется в APK перед подписью.
- **ТВ: только YouTube и Zona**, домены проверки `youtube.com`, `redirector.googlevideo.com`,
  `az1.zona.pub`, `sync.zonasearch.com`; Telegram/Discord/MTProto на ТВ отключены.
- **DNS**: для ТВ `explicitDns` (UDP 77.88.8.8 + 1.1.1.1, `ipv4_only`) — лечит
  `DNS query loopback`; с 1.3.39 то же включено для всех Android (`NodeStore.BuildConfig`).
- **Выбор сервера**: `NodeStore.PickForService` сортирует по `OpenDomains`, затем по задержке;
  `NodeFailover.TakeWithSourceCap` + `NodeSourceStats.BarrenSources` — поиск «от источника».
- **ТВ-профиль ресурсов** (`ProbeProfile`): лимит 1500, партии 60/6, квоты 6/4, такт 2 мин.
- **1.3.42–1.3.43**: одноразовый сброс маршрутизации приложений (`AndroidSettings.RoutingReset`)
  и принудительный `AppRouteMode.All` в `TunnelService.OpenTun` (C#-путь).
- **ПК**: `v1.3.37-beta` опубликован (setup.exe, ZIP, SHA256SUMS).
- **GitHub-релизы**: `v1.3.36-beta`, `v1.3.37-beta`, `v1.3.36-android`, `v1.3.37-android`,
  `v1.3.38-android` (только `-phone` APK). TV-ассеты из Android-релизов удалены.
  1.3.39–1.3.43 **не публиковались** (только локально в `release\`).

## Сборка и проверки

```powershell
# Android (телефон/планшет)  → release\VieNLink-<ver>-phone.apk
powershell -File build\publish-android.ps1 -NoAot

# Android TV (32-бит ARM)     → release\VieNLink-<ver>-tv-arm32.apk
powershell -File build\publish-android.ps1 -TvBuild -Abi armeabi-v7a -NoAot

# Магазинная (APK + AAB, не публикуем)
powershell -File build\publish-store.ps1

# ПК (приложение должно быть закрыто)
powershell -File build\publish-release.ps1
powershell -File build\publish-installer.ps1

# Проверки
dotnet run --project VieNLink.SelfTest -- checkconfigs   # системным dotnet (у локального нет рантайма 10)
```

Версия: `build\Version.props` (сейчас 1.3.43 / 10343).

## Полезные команды диагностики на устройстве

```powershell
$adb = "$env:LOCALAPPDATA\android-sdk\platform-tools\adb.exe"
& $adb -s <serial> logcat -c
& $adb -s <serial> logcat -d | Select-String "inbound/tun|JavaBridge|VieNLinkLog"

# трафик в туннеле (curl НЕ подходит — он вне VPN)
& $adb -s <serial> shell "cat /proc/net/dev | grep tun0"

# какие приложения в VPN
& $adb -s <serial> shell "dumpsys connectivity | grep -o 'Uids: <{[^}]*}>' | head -1"
```

## Осторожно

- Не удалять `Libs/libbox.aar` и `libgojni-armeabi-v7a.so.zip`.
- В `docs\handoff.md`, `docs\plan-2026.md`, `AGENTS.md` уже записаны ТВ-сборка, Zona, Java
  по умолчанию и правки DNS — не переписывать, только дополнять.
- На ПК приложение может быть запущено пользователем — сборка Windows падает на блокировке
  файлов, нужно закрыть `VieNLink.exe`.
- APK для ТВ на телефоне ставится как «несовместимо с устройством» — это не баг.
