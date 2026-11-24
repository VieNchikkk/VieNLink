# AGENTS.md — VieNLink

## Что это
Клиент прокси для Windows и Android: собирает публичные серверы (VLESS/VMess/Trojan/Shadowsocks/
Hysteria2) из открытых списков, подписок и Telegram-каналов, проверяет их запросом через сам
сервер и включает системный прокси Windows (или VPN на Android). Плюс обходы DPI, MTProto-прокси
для Telegram и TUN-режимы.

- Статус: бета (флаг `AppInfo.IsBeta` в `VieNLink.Core/AppInfo.cs`).
- Лицензия GPL-3.0 на весь репозиторий (Android линкует `libbox`, GPL). Репозиторий публичный.
- Версия одна на ПК и Android: `build/Version.props` (`VieNLinkVersion`, `VieNLinkVersionCode`,
  `VieNLinkIsBeta`). `versionCode` = major*10000 + minor*100 + patch, только растёт (1.3.3 → 10303).

## Стек и ограничения
- .NET 10, Windows: WPF, `net10.0-windows10.0.26100.0`. Android: `net10.0-android` без MAUI.
- `VieNLink.Core` — общее ядро `net10.0`, без Windows-зависимостей (важно для будущего перехода
  на libXray): модели, парсеры, конфиг sing-box, Clash API, MTProto, TLS/фрагментация, хранилища.
- Ядро — внешний `sing-box.exe` (Windows) и `Libs\libbox.aar` (Android), не NuGet.
- Внешних NuGet-пакетов нет — не добавлять без прямой просьбы.

## Карта файлов (сокращённо)
**Core:** `Models`, `LinkParser`, `SubscriptionParser`, `NodeSources`, `NodeRanking`,
`NodeAvailability`, `NodeFailover`, `NodeDedup`/`NodeHealth`/`NodeLimits` (в `NodeRanking`/`Models`), `ShareLink`,
`SingBoxConfigBuilder`, `ClashApiClient`, `TlsHello`, `TlsFragmentForwarder`, `MtProtoService`,
`MtProtoProbe`, `SourceDiscoveryService`, `JsonFileStore`, `AppLog`, `AppPaths`, `SecretProtector`,
`BypassProfiles`.

**Windows (`VieNLink/`):** `MainWindow.xaml(.cs)` + partial-классы `MainWindow.*.cs` (Loading,
SourcesRefresh, IdleTest, Connect, Zapret, Desync, TunBypass, Telegram, Nodes, Settings, SourceList,
CoreLog, TrayState, ProductFeatures, BypassProfiles, Layout); сервисы `SingBoxCore`, `SingBoxRunner`,
`HiddenProbeService`, `ProxyManager`, `SystemProxy`, `ZapretService`, `DesyncService`,
`TunBypassService`, `TrayIconService`, `ZapretWidget`, `AppDialog`/`MessageBox`, `UpdateCenter`
через `AppUpdateService`, `AutostartService`, `TaskbarIdentity`, `SecretStore` (DPAPI).

**Android (`VieNLink.Android/`):** `MainActivity` (+ `.Pages` — шапка и вкладки), `Ui`+`ThemeManager`,
`TelegramActivity`, `TunnelActivity`, `NodesActivity`, `SourcesActivity`, `SettingsActivity`,
`AppPickerActivity`, `LogActivity`, `AboutActivity`, `LicenseActivity`, `TunnelService` (VpnService),
`ForwarderHost`, `TunnelWidget`, `Maintenance`(+`Receiver`), `MtProtoScheduler`, `ProbeService`
(процесс `:probe`), `ProbeClient`, `ProbeScheduler`, `ProbeModels`, `NodeStore`, `NodeTester`,
`AndroidSettings`, `SettingsStore`, `Automation`, `UpdateCenter`/`AndroidUpdate`/`ApkInstaller`,
`StoreBuildInfo`, `CoreRuntime`, `LibBoxPlatform`, `KeystoreSecretStore`, `NetworkProbe`.

**Сборка:** `build/Version.props`, `version.ps1`, `publish-release.ps1`, `publish-installer.ps1`,
`publish-android.ps1` (GitHub APK), `publish-store.ps1` (APK для RuStore + AAB для Play),
`installer.iss`, `VieNLink.Android/Store/SideloadManifest.xml`, `tv-restore.ps1` (возврат
почищенного ТВ Xiaomi).

**Документы:** `docs/plan-2026.md` (журнал работ и решений, архив), `docs/handoff.md` (состояние
и следующий шаг), `docs/VERSIONING.md`, `docs/android-design.md`, `docs/privacy.md`,
`docs/store-listing.md`, `docs/store-publication.md`, `docs/rustore-moderation.md`,
`docs/tv-cleanup.md` (реестр отключённого/удалённого на ТВ Xiaomi, как вернуть), `CHANGELOG.md`.

## Как работает (ключевое)
- Сервер считается рабочим, только если HTTP-запрос **через него** открыл сайт. Узлы проверяются
  по Cloudflare (`cp.cloudflare.com` — универсальный доступ), YouTube, Telegram, Discord.
  Windows дополнительно по `TlsHello.TargetDomains` (18 заблокированных доменов) → `OpenDomains`.
  Выбор: больше доменов, затем меньше задержка; «мои» подписки первыми (`NodeRanking`).
  Перед полной проверкой мёртвые `host:port` отсеиваются TCP-опросом (`NodePrecheck`):
  в пробное ядро попадают только отвечающие адреса; UDP-протоколы не трогаются.
  Проверка через уже поднятый прокси (health-check, проверка после подключения) — только повод
  переключиться; `Working`/`FailCount` меняет лишь прямая проверка в скрытом ядре.
- Показ и выбор (23.11.2026): счётчики в шапке — `NodeAvailability.IsCountable` (статус
  `Working`, последняя удачная проверка прошла, подтверждённого провала не было, результат
  не старше `NodeLimits.WorkingDeadAfterMinutes` — 60 мин, фильтр «Только мои»); выбор для
  подключения — `NodeAvailability.IsUsable` (то же + проверка не старше
  `NodeLimits.WorkingRecheckMinutes`, 5 мин). Счётчики не обнуляются по таймеру 5 минут:
  партия перепроверки занимает минуты, и значки иначе падали бы в ноль на каждой проверке;
  уменьшает их только подтверждённый провал. Если свежих нет, кнопка сервиса проверяет
  серверы, которые показывают счётчики, скрытым ядром на месте (ПК —
  `FindNodeForServiceAsync`, Android — `PickVerifiedAsync`), поэтому число на значке и
  результат нажатия не расходятся. Фоновая проверка перепроверяет рабочие с истекающим
  окном (`RecheckCutoffUtc`). Счётчики в UI: ПК — каждые 3 с, Android — каждые 3 с и по
  ходу партии (`:probe` пишет `probe-live.json`, основной процесс применяет сразу).
- Переключение (`NodeFailover.Rank`): сначала серверы выбранного сервиса («Универсальный»,
  YouTube, Telegram, Discord), затем лучшие из других. Сервис сохраняется при сбое и на кнопке
  «Сменить сервер». Новый сервер выбранного сервиса встаёт первым; если работаем на запасном
  из другого сервиса, приложение плавно возвращается на него (тёплое переключение, если он
  уже в конфиге).
- Квоты: `NodeLimits.MinWorkingNodes` (20), `MinWorkingPerService` (20) для YouTube/Telegram/
  Discord/универсального; при дефиците поиск идёт несколькими партиями подряд, фокус сервиса
  меняется по кругу.
- Провалы: у рабочего сервера `FailCount` растёт и он остаётся в «Рабочих» без задержки; после
  `NodeLimits.RemoveAfterFailures` (5) — `Dead` и удаление, но только если с последнего
  успеха прошло `NodeLimits.WorkingDeadAfterMinutes` (60): «моргающий» VLESS/Reality-сервер,
  который иногда открывает сайт, в отсев не попадает. Непроверенный удаляется после
  первой неудачной проверки. Успех обнуляет счётчик. Ручное «Сменить сервер» провал не засчитывает.
  Провал рабочего сервера сначала подтверждается отдельной проверкой после партии
  (`ConfirmFailedWorkingAsync`): если сервер открыл Cloudflare/YouTube/Telegram, провал
  не засчитывается. После провала — пауза `NodeLimits.WorkingRetryMinutes` (3 мин) по
  `TriedAtUtc`; успешная проверка её снимает, ручная перепроверка обходит.
  `WorkingRecheckMinutes` (5 минут), вернули в 1.3.27 после временной проверки каждую минуту.
  Сервер, через который прямо сейчас идёт трафик, скрытая проверка не отсеивает; успешное
  подключение из отсева возвращает его в рабочие автоматически (реальный трафик — доказательство),
  а перепроверка без подключения — по-прежнему только кнопкой «Вернуть». При загрузке кэша
  из отсева убираются «фантомы» — записи, чей сервер уже в рабочем списке.
- Проверки идут во **вспомогательном** ядре (`HiddenProbeService`, порты 22000+ на Windows;
  процесс `:probe`, порты 21800+ на Android) — текущее подключение и TUN не перезапускаются.
  При активном TUN проверяются только уже загруженные серверы; при TUN-обходе проверка запрещена.
  Партия перемешивается по источникам; фон-такт ограничен 6 минутами, затем следующий такт.
- Дедупликация (`NodeDedup.Collapse`): точные дубли по outbound убираются, на один хост:порт
  остаётся до `NodeLimits.MaxPerEndpoint` (4) вариантов; результаты проверок переносятся только
  между точными дублями.
- Свежесть данных: `LastSeenUtc` при сборе; в кэше удаляются не встречавшиеся дольше
  `KeepUnseenNodeDays` (7). Лимит списка `NodeLimits.DefaultMaxTotal` (12000), из Telegram —
  посты за `TelegramText.FreshHours` (48 ч).
- MTProto: сбор только реальных `tg://proxy`, проверка как у Telegram — FakeTLS/obfuscated2 +
  `req_pq_multi` → `resPQ`. Пинг = время TCP-подключения (min из трёх), `HandshakeMs` только
  для диагностики. Нерабочие живут 24 ч, быстрая перепроверка через 20 мин
  (`MtProtoSchedule`). Держим `MinWorking` (5) рабочих: чаще сбор и до трёх партий проверки.
- Обходы DPI (Windows): zapret (`winws.exe` + WinDivert), свой движок на WinDivert
  (`DesyncService`), TUN-обход без узла (`TunBypassService` → SOCKS5 `TlsFragmentForwarder`).
  Все взаимоисключающие с прокси-узлом и друг с другом; требуют админа. В TUN-обходе обязательно
  правило `process_name: VieNLink.exe → direct` (иначе петля), DNS/`resolve` — `ipv4_only`.
  Удачные конфиги копятся в `bypass-profiles.json` и применяются первыми.
  Автоподбор проверяет видео по нескольким хостам googlevideo (до 2) и требует ≥64 КБ
  потока; если curl/HttpClient получают 0 (YouTube подписывает поток в JS, n-sig/PO-токен),
  вызывается **yt-dlp** (`YtDlpVideoProbe`, клиент `android`), который качает реальные
  байты. `yt-dlp.exe` скачивается один раз в `%LOCALAPPDATA%\VieNLink`. При ≥64 КБ
  стратегия получает `VideoOk`, перебор останавливается, профиль пишется с Quality 100.
  Список стратегий — кликабельные плашки (`StrategyRadioButton`), ручной выбор сохраняется
  (`DesyncManual`); «Только QUIC-фейк» проверяется QUIC-рукопожатием. Проверено
  23.11.2026: видео идёт по TCP со стратегией №2, yt-dlp скачал 629 172 байта.
- TUN на Windows требует `wintun.dll` и админа; системный прокси — WinINET (реестр +
  `InternetSetOption`), прежние настройки в `proxy-backup.json`, восстановление при старте.
- `AppSettings.KillSwitch`: при падении ядра прокси не снимается (трафик не уходит напрямую).
- sing-box запускается в job-объекте с `KILL_ON_JOB_CLOSE`; конфиг с UUID удаляется после
  отключения и выхода (`runtime\sing-box-config.json`, UTF-8 без BOM).
- Источники: `sources.dat` шифруется DPAPI (`SecretStore`). Все источники включены по умолчанию
  (миграция схемы настроек 3). Каналы Telegram — только источники MTProto (читаются через RSS-
  зеркала), их `NodeSourceService.CollectAsync` пропускает.
- Автоматику можно выключить целиком: `AppSettings.AutomationPaused` («Остановить все процессы» /
  «Запустить все процессы») — гаснут проверки, MTProto, будильник, автообновления,
  автопереключение; туннель и ручные кнопки работают. Отдельные работы выключаются
  переключателями в настройках: `AutoDiscoverSources`, `AutoCollectServers`,
  `IdleTestEnabled`, `AutoMtProto`, `AutoUpdateCore`, `AutoCheckAppUpdates`.
- Отказы: при недоступном прямом интернете партия откатывается (`HasDirectInternetAsync`),
  иначе обрыв сети чистил бы серверы; неудачная проверка обновления не сдвигает следующую.
- `NodeSourceService.FetchRawAsync` при ошибке прокси повторяет запрос напрямую.

## UI-конвенции
- Русский язык во всём: интерфейс, логи, комментарии. В пользовательском тексте Android слово
  «узел» заменено на «сервер».
- Windows: верхний уровень «Подключение» / «Обход блокировок» / «Инструменты» /
  «Отсев — Перепроверка» / «Журнал» / «Общие». Кнопки шапки: «Отключить» (видна при активности),
  «Сменить сервер», «Поделиться» (копирует только `IP:порт`), «Отменить» во время операции.
  Список серверов скрыт — только сводка и проверка.
- Android, форм-фактор: `DeviceProfile` — телефон (портрет), планшет `sw600dp+`
  (`FullSensor`, две колонки), ТВ (`UiMode.TypeTelevision`, landscape, `Ui.LayoutScale` 1.35,
  подсветка фокуса для пульта). Не фиксировать портрет на больших экранах: Android 16
  всё равно игнорирует ограничение. ТВ-сборка — `publish-android.ps1 -TvBuild`
  (`com.vienlink.app.tv`, `LEANBACK_LAUNCHER`, баннер, без обязательного тача).
- Android: 4 нижние вкладки «Подключение / Обход / Инструменты / Настройки». Журнал —
  плитка в «Инструментах» (отдельный экран `LogActivity`). В шапке —
  счётчики сервисов, «Проверить серверы» (во время поиска «Остановить поиск»), рядом
  «Сменить сервер» и «Поделиться сервером», под ними на всю ширину «Добавить сервер»
  (ссылка с ключом или `IP:порт` известного сервера; проверяется вне общей очереди).
  Пока идёт подключение кнопка называется «Отмена». Экран портретный (`BaseActivity`),
  приложение не включает автоповорот и не меняет системную настройку.
- Все диалоги — `AppDialog` через класс `VieNLink.MessageBox` (перекрывает системный), не
  `System.Windows.MessageBox`.
- Долгие операции обязаны показывать ход и отменяться: `BeginOperation`/`EndOperation`,
  `TryTakeOverAutoAsync` (ручное действие вытесняет автообновление), в MTProto — кнопки
  «Остановить сбор»/«Остановить проверку».
- Иерархия кнопок: `PrimaryButton` — главное действие, `AccentSmallButton` — следующий шаг,
  остальное `SmallButton`.

## Релизы
- Перед сборкой закрыть `VieNLink.exe`.
- Windows: `build\publish-installer.ps1` → портативный ZIP + `setup.exe` (подпись `CN=VieN`).
- Android: `build\publish-android.ps1` (подписанный APK + `.apk.sha256`), магазинная сборка
  `build\publish-store.ps1` (`StoreBuild=true`: APK для RuStore и AAB для Play, без
  `REQUEST_INSTALL_PACKAGES` и самообновления). **Правило: магазинную сборку собирать при каждом
  релизе**, чтобы `versionCode` не отставал; в RuStore/Play пока не публикуем (решение владельца).
- Теги: `v<версия>-beta` (Windows) и `v<версия>-android`; релизы — pre-release с assets
  (`setup.exe`, ZIP, `SHA256SUMS.txt`; APK + `.apk.sha256`).
- GitHub в РФ часто недоступен: зеркала `ghfast.top`, `gh-proxy.com`; API-лимит — резервная
  atom-лента. Windows-обновлятор пропускает релизы без `setup.exe`.
- Android-обновление: `releases?per_page=30` (не `latest`), выбор максимальной версии с APK,
  проверка SHA-256, установка через `PackageInstaller`. Кэш APK переиспользуется по маркеру.
- Play Protect предупреждает о сайдлоаде — ожидаемо (VPN + установка пакетов), инструкция в README.

## Грабли
- sing-box не понимает BOM; `xhttp/splithttp` не поддерживается (нужен второй движок).
- Без секции `dns` sing-box не разрешает доменные адреса серверов: в Android/libbox
  `local`-DNS отдаёт `empty result`, рабочие серверы с доменным адресом бракуются.
  Пробному ядру Android задаётся явный `udp 1.1.1.1` + `ipv4_only`
  (`SingBoxConfigBuilder.Build(..., explicitDns: true)`); ПК оставлен на системном DNS.
- `flow=xtls-rprx-vision-udp443` нормализуется до `xtls-rprx-vision`.
- Если `sing-box check` падает, `ProxyManager.FilterBrokenNodesAsync` делит список пополам и
  выбрасывает несовместимые серверы.
- `LinkParser.ApplyTransport` без `default`: неизвестный транспорт молча даёт «VLESS поверх TCP».
- Clash API даёт только задержку; объём/скорость — прямым замером (`MeasureSpeedAsync`).
- ТСПУ «морозит» TCP после ~25 пакетов: «сайт открылся» ≠ «годится для видео».
- Локальный GGC YouTube у некоторых провайдеров блокируется по IP: видео без прокси-сервера
  не поднять (проверено всеми стратегиями zapret и нашим движком).
- Кастомный `TabItem` не отдаёт содержимое вкладок в UI Automation — автоклики по вкладкам ПК
  не работают; проверять через `SelfTest`, журнал или скриншоты устройства.
- Android может зависнуть после фона из-за **дедлока остановки мира MonoVM** (главный поток
  и все управляемые потоки в `sigsuspend`, ANR `Broadcast … MAINTENANCE`, окно не рисуется,
  `reportedDrawn=false`). В коде не лечится; обход — `VieNLink.Android/Environment.txt`
  (`MONO_GC_PARAMS=nursery-size=16m`, `MONO_THREADS_SUSPEND=coop`), тяжёлая работа после
  возврата — только в фоне с задержкой (см. `docs/handoff.md` от 23.11.2026).
- Android 11 (Lenovo TB-J706F): Release **с AOT** падает SIGSEGV через ~1 с после старта
  (Debug и no-AOT работают). `RunAOTCompilation=false`/`AndroidEnableProfiledAot=false`
  в csproj; `publish-android.ps1 -NoAot` — для планшета/ТВ обязательно.
- Java-платформа (`com.vienlink.platform`) вызывается из C# по имени: `Class.forName` нужен
  **загрузчик приложения** (`Application.Context.ClassLoader`), иначе boot loader не находит
  класс; в Release обязателен `keep-java.txt` (`-keep class com.vienlink.platform.**`).
- Android Activity не экспортированы: `am start` их не откроет, навигация — тапами.

## Сборка и проверки
```
dotnet build VieNLink.slnx                     # Android-проект требует workload android
dotnet build VieNLink\VieNLink.csproj
dotnet run --project VieNLink.SelfTest -- parsers      # golden-тесты разбора (без сети)
dotnet run --project VieNLink.SelfTest -- checkconfigs  # все варианты конфига через sing-box check
dotnet run --project VieNLink.SelfTest -- cache|sources
dotnet run --project VieNLink.SelfTest -- statuslogic
dotnet run --project VieNLink.SelfTest -- hiddenprobe 5 # скрытая проверка рабочих серверов
dotnet run --project VieNLink.SelfTest -- videoprobe   # проба видео автоподбора (нужен админ)
dotnet run --project VieNLink.SelfTest -- liveness 60 <url>
dotnet run --project VieNLink.SelfTest -- mtprotocheck 40
```
С 24.11.2026 Java-платформа libbox включена во **все** Android-сборки по умолчанию
(обычная, тестовая, ТВ): на телефоне она не даёт зависаний MonoVM после фона.
Аварийный откат — `-p:AndroidJavaPlatform=false`.
Тестовая сборка (отдельный package, ставится рядом с обычной):
`build\publish-android.ps1 -TestBuild` → `release\VieNLink-<версия>-test.apk`
(`com.vienlink.app.test`, «VieNLink Test»).
Android (отдельный SDK, без админа):
```powershell
$dotnet = "$env:LOCALAPPDATA\dotnet\dotnet.exe"
& $dotnet build VieNLink.Android\VieNLink.Android.csproj -c Debug `
    -p:AndroidSdkDirectory="$env:LOCALAPPDATA\android-sdk" `
    -p:JavaSdkDirectory="$env:LOCALAPPDATA\jdk17"
```
Устройство: `adb` из того же SDK. `CI` — `.github/workflows/build.yml` гоняет `parsers`.

## Правила работы
- Только точечные правки, без переписывания архитектуры без просьбы. UI — в XAML (кроме трея).
- Задачу считать сделанной только после прогона проверки (`dotnet build`, нужный режим `SelfTest`),
  а не после написания кода. Результат сборки сообщать.
- Архитектурные решения фиксировать в `docs/plan-2026.md` («Решения») с датой и причиной.
- Перед передачей сессии обновлять `docs/handoff.md` (состояние, следующий шаг, грабли).
- Секреты (ключи подписи, токены подписок) в репозиторий не попадают.
