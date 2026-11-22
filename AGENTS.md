# AGENTS.md — VieNLink

## Статус
Бета-версия: основные режимы проверены, но возможны сбои. Статус виден в интерфейсе
(значок «БЕТА» в шапке, «Общие», заголовок окна) и в установщике; снимается
константой `AppInfo.IsBeta` в `VieNLink.Core/AppInfo.cs`.

Проект **личный** по происхождению, но **распространяется публично**. Лицензия —
**GPL-3.0** на весь репозиторий: Android-клиент линкует `libbox` (sing-box, GPL-3.0),
поэтому GPL неизбежна, а открытая публикация — самый дешёвый законный путь.
Альтернатива на случай, если когда-нибудь понадобится закрытая сборка, — `libXray`
(MPL-2.0) с собственным TUN; готовность к ней обеспечивается тем, что libbox не протекает
в `VieNLink.Core`. Подробности — `docs/plan-2026.md` (раздел «Решения»)
и `docs/android-design.md` §4.

## Назначение
Клиент прокси для Windows: собирает публичные узлы (VLESS/VMess/Trojan/Shadowsocks/Hysteria2)
из открытых списков, подписок и каналов Telegram, проверяет задержку через sing-box (Clash API)
и включает системный прокси Windows, чтобы открывались заблокированные сайты.

## Стек
- .NET 10, TFM `net10.0-windows10.0.26100.0`, WPF (`UseWPF`, `WinExe`), Nullable + ImplicitUsings.
- Внешних NuGet-пакетов нет — не добавлять без явной просьбы.
- Ядро — внешний `sing-box.exe` (не библиотека): скачивается с GitHub/зеркал или берётся из `core\` рядом с exe.
- Системный прокси — реестр WinINET + `InternetSetOption`; трей — WinForms `NotifyIcon`.

## Карта файлов
- `VieNLink.Core/` — общее ядро (`net10.0`, без Windows): `Models`, `LinkParser`, `SubscriptionParser`, `NodeSources`, `NodeRanking`, `SingBoxConfigBuilder`, `ClashApiClient`, `TlsHello`, `TlsFragmentForwarder`, `MtProtoService`, `SourceDiscoveryService`, `JsonFileStore`, `AppLog`, `AppPaths`, `SecretProtector`. Используется Windows- и Android-версиями.
- `VieNLink.Android/` — Android-клиент (`net10.0-android`, без MAUI): `MainActivity` (+ `MainActivity.Pages`) — шапка и вкладки «Подключение / Обход / Инструменты / Журнал»; `ThemeManager` + `Ui` — палитра Windows, карточки и кнопки; `TelegramActivity` + `MtProtoStore` — MTProto через RSS; `TunnelActivity` — «Туннелирование», только маршрутизация по доменам; `NodesActivity` (сводка узлов без списка), `SourcesActivity` (подписки и ссылки), `SettingsActivity`, `AppPickerActivity` (список приложений с иконками), `LogActivity`, `AboutActivity`, `LicenseActivity` (тексты GPL-3.0 и сторонних компонентов из assets), `ForwarderHost`, `BypassActivity`, `TunnelService : VpnService` (автопереключение, действия уведомления «Лучший»/«Узлы»/«Отключить»), `TunnelWidget` (виджет), `Maintenance` + `MaintenanceReceiver` (фон каждые 30 минут), `MtProtoScheduler` (сбор/проверка MTProto каждые 2 минуты в процессе), `NetworkProbe` (выход/скорость), `AndroidUpdate` (поиск релиза с APK, загрузка и SHA-256), `UpdateCenter` (автопроверка, диалог установки), `UpdateNotifier` (уведомления), `ApkInstaller` + `UpdateInstallReceiver` (`PackageInstaller`), `LogReader`, `CoreRuntime` (единый `CommandServer` на процесс), `ProbeService` (проверка узлов в отдельном процессе `:probe`), `ProbeClient`, `ProbeScheduler`, `ProbeModels` (схемы запроса/результата/хода `:probe`), `NodeStore` (в т.ч. `PickVerifiedAsync`), `NodeTester` (Clash API 20801), `AndroidSettings`, `BootReceiver`, `TunnelTileService`, `VpnStarter`, `SettingsStore`, `UiMetrics`, `VieNLinkApp` (точка входа Application: регистрация Keystore, перенаправление stderr ядра, запуск планировщиков), `LibBoxPlatform`, `LibBoxServerHandler`, `LocalDnsTransport`, `DefaultNetworkCallback` + `NetworkInterfaceIterator` (смена сети и список интерфейсов для ядра), `KeystoreSecretStore`; ресурсы — `Resources/values`, `Resources/drawable`, `Resources/mipmap-*`, `Resources/layout/widget.xml`, `Resources/xml/widget_info.xml`; ядро подключается как `Libs\libbox.aar`.
- `VieNLink.slnx` — решение: `VieNLink.Core`, `VieNLink`, `VieNLink.SelfTest`, `VieNLink.Android`.
- `VieNLink/App.xaml(.cs)` — запуск, один экземпляр, тема, журнал, перехват ошибок.
- `VieNLink/app.manifest` — запуск без прав администратора, Windows 10/11, длинные пути.
- `VieNLink/MainWindow.xaml(.cs)` — весь UI. Верхний уровень: «Подключение» (в одном ряду цветные кнопки с иконками YouTube/Discord/Универсальный/Telegram и вкладка «Telegram MTProtoProxies», открывающая страницу MTProto), «Обход блокировок» (подвкладки «Обход Zapret», «Обход DPI», «TUN-обход (Beta)»), «Инструменты» (Узлы / Источники / Туннель / Диагностика / Настройки), «Журнал», «Общие»; плюс трей и кнопка подключения в шапке. На вкладке «Узлы» список скрыт: только сводка и проверка.
- `VieNLink/MainWindow.*.cs` — partial-классы того же окна по темам: `Loading`, `SourcesRefresh`, `IdleTest`, `Connect`, `Zapret`, `Desync`, `TunBypass`, `Telegram`, `Nodes`, `Settings`, `SourceList`, `CoreLog`, `TrayState`, `ProductFeatures`, `BypassProfiles`, `Layout` (стартовый размер окна, изменение размера и запоминание позиций).
- `VieNLink/AppDialog.xaml(.cs)`, `VieNLink/MessageBox.cs` — тематические диалоги вместо системного `MessageBox`: класс `MessageBox` в `namespace VieNLink` перекрывает `System.Windows.MessageBox`, поэтому все прежние вызовы показывают кастомное окно.
- `VieNLink.Core/BypassProfiles.cs`, `VieNLink/MainWindow.BypassProfiles.cs` — память удачных конфигов обходов: режим, стратегия, флаги, качество; лучшие по `Quality` применяются первыми, при отсутствии записей — обычный выбор/автоподбор.
- `VieNLink/AutostartService.cs` — автозапуск при входе в Windows через ключ `HKCU\...\Run` (аргумент `--minimized`).
- `VieNLink/SecretStore.cs` — шифрование DPAPI (P/Invoke `CryptProtectData`) для `sources.dat`.
- `VieNLink/WindowsIntegration.cs` — выходной IP/страна через узел и снятие loopback-изоляции приложений Магазина (`CheckNetIsolation`).
- `VieNLink.Core/Models.cs` — `ProxyNode`, `SourceDefinition`, `AppSettings`, `NodeCache`, `SystemProxyBackup`.
- `VieNLink.Core/ViewModels.cs` — строки таблиц `NodeRow` и `SourceRow`.
- `VieNLink.Core/NodeRanking.cs` — оценка маскировки узлов (REALITY, uTLS, Vision, WS/gRPC, Hysteria2), семейства камуфляжа и пометки для таблиц.
- `VieNLink/ZapretService.cs` — обход DPI через zapret: скачивание winws/WinDivert в папку `zapret` рядом с exe (при запрете записи — в `%LOCALAPPDATA%`), запуск и остановка, профиль fake/multidisorder.
- `VieNLink/DesyncService.cs` — собственный обход DPI на WinDivert (без winws): split/disorder/fake для TLS ClientHello, QUIC-фейк, автоподбор; подвкладка «Обход DPI» в «Обходе блокировок».
- `VieNLink.Core/TlsFragmentForwarder.cs` — локальный SOCKS5-форвардер для TUN-обхода: прямой TCP-канал и фрагментация TLS ClientHello в пользовательском потоке.
- `VieNLink/TunBypassService.cs` — TUN-обход без узла и WinDivert: sing-box TUN → SOCKS5-форвардер, конфиг, запуск/остановка.
- `VieNLink.Core/LinkParser.cs` — разбор ссылок в outbound-объекты sing-box (vless/vmess/trojan/ss/hysteria2).
- `VieNLink.Core/NodeSources.cs` — загрузка источников, извлечение ссылок, base64-подписки, текст Telegram, кэш.
- `VieNLink.Core/SubscriptionParser.cs` — форматы подписок: base64, Xray/sing-box JSON, Clash YAML, `subscription-userinfo`.
- `VieNLink.Core/MtProtoService.cs` — MTProto-прокси Telegram: `tg://proxy` из HTML каналов и RSS-зеркал, статусы «рабочий/нерабочий», кэш; нерабочие хранятся 24 часа. Проверка — настоящая (`MtProtoProbe`): FakeTLS-рукопожатие, obfuscated2 и `req_pq_multi` к Telegram, рабочий только при ответе `resPQ`. Сбор и проверки идут автономно (`MainWindow.Telegram.cs`, `MtProtoSchedule`), кнопок сбора в UI нет.
- `VieNLink.Core/MtProtoProbe.cs` — проверка MTProto-прокси «как Telegram»: разбор секрета (hex/base64url, `ee` FakeTLS / `dd` / plain), SNI из секрета и HMAC ответа сервера, obfuscated2, транспорт padded intermediate/abridged, `req_pq_multi` → `resPQ`. Секреты с испорченным доменом отбрасываются.
- `VieNLink.Core/SourceDiscoveryService.cs` — автопоиск источников: GitHub search + дерево файлов, обход публичных сайтов с ежедневными подписками и проверка кандидатов.
- `VieNLink/SingBoxCore.cs` — поиск/скачивание ядра и проверка `sing-box version`.
- `VieNLink.Core/SingBoxConfigBuilder.cs` — сборка конфига: mixed inbound, TUN, правила по процессам и доменам, Clash API.
- `VieNLink/SingBoxRunner.cs` — запуск/остановка ядра, `sing-box check`, чтение stderr.
- `VieNLink.Core/ClashApiClient.cs` — `/proxies/{tag}/delay` и переключение selector.
- `VieNLink/HiddenProbeService.cs` — скрытый пробный туннель: отдельный sing-box на своих портах
  (22000+), без TUN и системного прокси; используется фоновой проверкой, чтобы не мешать
  текущему подключению.
- `VieNLink/ProxyManager.cs` — оркестрация: проверка узлов по списку заблокированных доменов (`TlsHello.TargetDomains`) и `OpenDomains`, выбор узла, системный прокси, health-check. Есть `TestLoadedNodesAsync` — проверка только уже загруженных узлов без перезапуска ядра (для простоя).
- `VieNLink/SystemProxy.cs` — включение/восстановление системного прокси и список исключений.
- `VieNLink/TrayIconService.cs` — значок в трее; в меню только «Подключить лучший» без списка адресов.
- `VieNLink/ZapretWidget.xaml(.cs)` — плавающий виджет с тремя способами: «Zapret», «Свой обход DPI» (WinDivert) и «Видео YouTube» (через узел). Работающий режим подсвечен, ниже мелко подписаны режим и стратегия; в свёрнутом виде — точка состояния (зелёная/красная) и название включённого режима. Место и свёрнутое состояние запоминаются в `settings.json`. Виджет по умолчанию **поверх всех окон**; закреп «📌» в шапке и галочка «Виджет поверх всех окон» в «Общих» переключают `AppSettings.WidgetTopmost` (по умолчанию `true`). Сам виджет показывается при каждом запуске (`AppSettings.WidgetVisible`, по умолчанию `true`); закрытие крестиком выключает его до следующего включения вручную.
- `VieNLink/WindowChromeTheme.cs` — цвет системного заголовка окна под текущую тему через `DwmSetWindowAttribute` (Windows 10/11).
- `VieNLink/TaskbarIdentity.cs` — единый `AppUserModelID` (`VieN.VieNLink`) для панели задач: закреплённый ярлык и ярлык с рабочего стола открывают одну кнопку; тот же ID прописан в ярлыках `build/installer.iss`.
- `VieNLink/ProcessPickerWindow.xaml(.cs)` — выбор приложений из запущенных процессов для правила «только для приложений из списка».
- `VieNLink/Themes/*.xaml` — палитры тёмной/светлой темы и стили контролов.
- `VieNLink.SelfTest/Program.cs` — self-test: `parsers`, `liveness`, `seed`, `desync`, `forwarder`, `mtprotocheck`.
- Автообновление Android: `AndroidUpdate.FindLatestAsync` читает `releases?per_page=30`
  (не `releases/latest`: тот игнорирует Pre-release), выбирает релиз с максимальной версией
  **среди содержащих APK**, скачивает файл, сверяет SHA-256 из `*.apk.sha256` и отдаёт
  `ApkInstaller` (`PackageInstaller`, `REQUEST_INSTALL_PACKAGES`). `UpdateCenter` проверяет
  при запуске (не чаще 6 ч) и в фоне, `UpdateNotifier` показывает ход/готовность. Совсем
  без подтверждения Android APK не ставит — системное окно установки обязательно.
  Если JSON API GitHub не ответил (блокировка/лимит), читается atom-лента релизов; APK
  скачивается с GitHub, затем с зеркал `ghfast.top`/`gh-proxy.com`. Неудачная проверка
  **не** записывает время последней проверки — иначе одна ошибка сети откладывала бы
  обновление на 6 часов (это и выглядело как «новая версия не скачивается»).
- Магазины: Play и RuStore запрещают приложению ставить обновления самому. Поэтому версии
  две: GitHub-APK (`publish-android.ps1`) с автообновлением и `REQUEST_INSTALL_PACKAGES`,
  и магазинная (`publish-store.ps1`, `StoreBuild=true`) без этого разрешения и без
  самообновления. Разрешение добавляет `Store/SideloadManifest.xml` только в GitHub-сборке;
  `StoreBuildInfo.IsStoreBuild` отключает проверку обновлений и меняет подпись шапки.
  Для Play собирается AAB, для RuStore — APK. Ключ подписи один и тот же.
- **Правило релиза:** при каждом выпуске новой версии сразу собирать и магазинную сборку
  (`publish-store.ps1` → `release\VieNLink-<версия>-store.apk` и `-store.aab`), чтобы она
  не отставала от GitHub и `versionCode` рос последовательно. **В RuStore/Play пока не
  публикуем** (решение владельца, 22.11.2026): артефакты просто лежат в `release\`
  с `.sha256` и готовы к загрузке позже. Ключ RuStore в репозиторий/поставку не кладётся.
- Предупреждение Play Protect о «вредоносном» APK — ожидаемое для сайдлоада с собственным
  ключом (VPN + установка пакетов + foreground). Инструкция для пользователя — в README,
  риск и план (RuStore/Play) — в `docs/android-design.md` §13.
- Windows-обновлятор (`AppUpdateService`) пропускает релизы без `setup.exe` и ZIP:
  Android-релиз с одним APK не должен предлагаться Windows-версии.
- Скачанный Android-APK переиспользуется: рядом лежит маркер «тег|размер», который
  пишется только после проверки SHA-256. Повторная проверка не качает 46 МБ заново,
  а сразу предлагает установку; если кэш очищен — скачает снова.
- Повторный запуск Windows-версии (закреплённая кнопка, ярлык, второй экземпляр)
  будит первый через `Local\VieNLink.ShowWindow` и показывает окно из трея.
  Аргументы повышения прав (`--tunnel`, `--tun-bypass`, `--tun-bypass-tune`,
  `--store-loopback`, `--desync`, `--zapret`) исключены: там старый экземпляр
  завершается, а работу продолжает новый.
- `build/Version.props` — **единственная версия Windows и Android** (`VieNLinkVersion`,
  `VieNLinkVersionCode`, `VieNLinkIsBeta`); оба csproj импортируют файл.
  `versionCode` Android = major*10000 + minor*100 + patch (1.3.0 → 10300), только растёт.
- `build/version.ps1` — чтение общей версии для `publish-release/installer/android/store`.
- `build/publish-release.ps1` — релиз: self-contained один exe, опционально ядро в `core\`, zapret в `zapret\`, ZIP.
- `build/publish-installer.ps1` — оба варианта поставки: портативный ZIP + `setup.exe` (Inno Setup).
- `build/publish-android.ps1` — подписанный Release APK для GitHub (с автообновлением) и `.apk.sha256`; долговременный ключ
  в `%LOCALAPPDATA%\VieNLink\signing` (в репозиторий не попадает). Перед сборкой чистит
  `obj/bin Release`: `StoreBuild` меняет манифест, инкрементальная сборка иначе может
  унаследовать магазинный вариант без `REQUEST_INSTALL_PACKAGES`.
- `build/publish-store.ps1` — магазинная сборка (`StoreBuild=true`): APK для RuStore и
  AAB для Google Play без `REQUEST_INSTALL_PACKAGES`/самообновления; кладёт файлы и
  `.sha256` в `release\`. Чистит `obj/bin Release` и проверяет через `aapt`, что в APK
  не осталось `REQUEST_INSTALL_PACKAGES`.
- `VieNLink.Android/Store/SideloadManifest.xml` — overlay с `REQUEST_INSTALL_PACKAGES`
  только для GitHub-сборки; магазинная сборка его не подключает (`StoreBuildInfo`).
- `build/installer.iss` — сценарий Inno Setup: установка в профиль пользователя, ярлыки, деинсталлятор.
- `docs/plan-2026.md` — план работ 2026–2027 и журнал прогресса: фазы, чек-боксы, решения, грабли.
- `docs/handoff.md` — передача контекста новому агенту: состояние, следующий шаг, грабли.
- `docs/android-design.md` — проектирование Android-версии: матрица возможностей, развилка ядра (GPL/MPL), библиотеки, аналоги Windows-компонентов.
- `docs/VERSIONING.md` — схема версий, совместимость данных, порядок выпуска релиза.
- `docs/privacy.md` — политика конфиденциальности для магазинов (RU/EN).
- `docs/store-listing.md` — тексты карточки, Data safety, VPN-декларация, RuStore.
- `docs/store-publication.md` — требования и шаги публикации в RuStore/Google Play.
- `docs/rustore-moderation.md` — поля карточки RuStore, комментарий для модерации и юридическая записка (152-ФЗ, 408-ФЗ, GPL).
- `store/` — иконка 512×512, feature graphic и скриншоты для карточки магазина.

## План работ (обязательно к прочтению)

Актуальный план и фактический прогресс — в **`docs/plan-2026.md`**. Там фазы 0–8,
чек-боксы по задачам, «Решения», «Журнал работ» и раздел «Как продолжать».

**Текущая точка остановки — `docs/plan-2026.md` §0 «Точка остановки»**: Android-версия
работает (VPN → узел → YouTube), сделаны узлы, источники, настройки, автозапуск, плитка,
deep link, split-tunnel (маршрутизация по приложениям в «Туннелировании»), журнал, уведомление и TUN-обход (бета; страница
YouTube открывается без узла, видео требует узел). Правовые задачи 0.4–0.5 закрыты
(лицензии и поставка, проверка на секреты). Дальше — тест на втором операторе, ручное
добавление плитки и улучшение обхода DPI.

Правила:

- перед началом любой работы прочитать раздел «Как продолжать» в плане;
- перед передачей сессии другому агенту (или при приближении к лимиту контекста)
  обновить `docs/handoff.md` — краткое состояние, следующий шаг и грабли;
- после закрытой задачи отметить чек-бокс, добавить строку в «Журнал работ»
  и обновить шапку («Текущая фаза / Ближайшая задача»);
- архитектурные решения фиксировать в «Решениях» с датой и причиной;
- задачу считать сделанной только после прогона её команды проверки
  (`dotnet build`, `SelfTest -- <режим>`), а не после написания кода.

## План развития обхода DPI

- **Быстрый шаг (сделано, тестируется):** TLS-фрагментация средствами sing-box —
  `tls.fragment` / `tls.record_fragment` в outbound'ах узлов. Галочки в «Настройках»,
  без WinDivert и внешних компонентов. Работает только для трафика через прокси-узел.
- **Средний шаг (сделано, тестируется):** собственный обход DPI на WinDivert в подвкладке
  «Обход DPI» («Обход блокировок»): split/disorder/fake для TLS ClientHello и QUIC-фейк, без winws.exe.
  Требует прав администратора; своя автоподборка стратегий.
- **Долгий шаг (сделано, проверено live):** TUN + собственный user-space TCP-форвардер
  с фрагментацией — обход без прокси-узла и без WinDivert. sing-box поднимает TUN
  `VieNLink-Bypass`, TCP идёт в локальный SOCKS5-форвардер `TlsFragmentForwarder`;
  трафик самого VieNLink.exe исключён из TUN правилом `process_name`, чтобы не было петли.
  DNS и `resolve` принудительно `ipv4_only`: IPv6-маршруты ломали TUN. QUIC по умолчанию
  блокируется (UDP/443). Live-проверка: Cloudflare открывается через TUN во всех режимах
  разреза, YouTube/t.me на текущем провайдере блокируются по IP/протоколу — для них узел/zapret.

## Ключевые решения
- Узлы не проверяются «пингом»: задержка измеряется HTTP-запросом через сам узел (`TestUrl`,
  по умолчанию `https://www.youtube.com/`), поэтому «рабочий» = «сайт реально открылся».
- Если конфиг не проходит `sing-box check`, `ProxyManager.FilterBrokenNodesAsync` находит
  несовместимые узлы делением списка пополам и выбрасывает только их.
- `flow=xtls-rprx-vision-udp443` нормализуется до `xtls-rprx-vision`: суффикс Xray ломает sing-box.
- Системный прокси — WinINET: покрывает браузеры и приложения, уважающие настройки Windows.
  TUN-режим (весь трафик) требует прав администратора и `wintun.dll`; приложение предлагает
  перезапуск с UAC и скачивает wintun само.
- Маршрутизация задаётся правилами sing-box: `process_name` (приложения), `domain_suffix`
  (домены), `ip_is_private` → direct. В TUN перед правилами идут `action: sniff` и `hijack-dns`.
- Режим маршрутизации входит в подпись конфига (`_loadedSignature`), иначе смена TUN/правил
  не пересобирала бы конфиг.
- Узлы проверяются по двум базовым адресам (`ProxyManager.YoutubeTestUrl`, `TelegramTestUrl`),
  а у живого узла — ещё по списку заблокированных доменов (`TlsHello.TargetDomains`) и
  универсальному Cloudflare. Результаты хранятся в `ProxyNode.YoutubeOk/TelegramOk/
  UniversalOk/DiscordOk` и `OpenDomains`; ни одного успеха → `NodeHealth.RegisterFailure`:
  у рабочего узла `ProxyNode.FailCount` растёт (он остаётся в «Рабочих» без задержки), непроверенный
  удаляется сразу, успех обнуляет счётчик → `Working`. Рабочий узел удаляется только после
  `NodeLimits.RemoveAfterFailures` (5) провалов подряд.
- Android проверяет узел сразу по шести адресам четырёх сервисов: универсальный
  `cp.cloudflare.com` (приоритет — его задержка идёт в список и в выбор лучшего), YouTube
  (`www.youtube.com`, `redirector.googlevideo.com`), Telegram (`t.me`, `web.telegram.org`),
  Discord (`discord.com`). Узел рабочий, если открылся хотя бы один адрес; лучший узел
  выбирается и сохраняется автоматически после проверки и при подключении.
- Android не показывает список узлов: на экране «Узлы» только сводка (всего, рабочих,
  доступ по сервисам, лимит, время проверки). На главном экране — кнопки «YouTube»,
  «Telegram», «Discord», «Универсальный»: каждая берёт уже проверенный узел с нужным
  доступом, сортировка скрыта (`NodeStore.PickForService`). Имена узлов не показываются
  ни в списке, ни в уведомлении — только задержка.
- Android-проверка узлов изолирована в процессе `:probe` (`ProbeService`): у него свой
  libbox, порты 21800+ и папка `files/probe`, поэтому VPN и основное ядро не перезапускаются.
  `ProbeScheduler` такт раз в минуту: перепроверка рабочих с истекающим окном свежести
  (5 минут, партия 20), а поиск новых — раз в 2–5 минут; ручная проверка — до 150.
  Результаты пишутся в `probe-results.json`, основной процесс применяет их к `NodeStore`.
  При TUN-обходе проверка недоступна (он перехватывает весь трафик).
- Android: поиск можно остановить — кнопка шапки во время проверки называется
  «Остановить поиск» (`ProbeClient.Cancel`): она отменяет ожидание, посылает `:probe`
  команду `STOP_PROBE` (процесс сразу завершается и не тратит трафик) и приостанавливает
  фоновый поиск до явного действия. `ProbeClient.Resume` вызывают «Проверить узлы»,
  «Проверить» на экране «Узлы» и кнопки сервисов. «Отключить» во время поиска узла
  тоже гасит `:probe`. Пока идёт подключение кнопка в шапке называется «Отмена»;
  на экране «Узлы» кнопка проверки становится «Остановить поиск»; в Telegram
  «Подключить Telegram через узел» превращается в «Отменить подключение» (повторное
  нажатие отменяет `PickVerifiedAsync` и гасит `:probe`). На ПК это `CancelButton`
  («Отменить») — он есть у любой операции `BeginOperation`, включая «Сменить сервер» и проверки.
- Android-шапка: три равные кнопки в один ряд — «Проверить серверы», «Сменить сервер»,
  «Поделиться»; отдельной кнопки «Скорость» нет (скорость всегда в строке метрик). При
  отключённом VPN пинг, скорость, выход и трафик не показываются («Сервер не подключён»,
  «—»). Большая кнопка Telegram MTProtoProxies — синяя в цвет Telegram. В пользовательском
  тексте слово «узел» заменено на «сервер» (интерфейс, подсказки, сообщения ядра);
  в коде остаются `ProxyNode`/`NodeStore`.
- Android: нижних вкладок четыре — «Подключение / Обход / Инструменты / Журнал».
  Вкладка «Общие» удалена: «Проверить обновление» — в «Настройках» → «Обновления»,
  «Очистить кэш серверов» и «Настройки VPN Android» — в «Данные и VPN», памятка о
  виджете — к плитке; версия, лицензии, документы и пути остались в «О программе»
  без дубликатов. Deep-link `tab` ограничен 0..3.
- Счётчики после простоя не висят нулями: Android `ProbeScheduler` делает первый такт
  через 2 секунды, а `MainActivity.OnResume` вызывает `EnsureFreshCountersAsync`
  (перепроверка до 20 ранее рабочих серверов, `workingOnly`); Windows после загрузки
  кэша и при `Activated` запускает `RunIdleTestAsync(searchNew:false)`, если свежих
  серверов меньше `MinWorkingNodes`. Полная пятиминутная перепроверка остаётся, но
  первые числа появляются через секунды.
- Windows проверяет узлы так же: базовые YouTube/Telegram, затем полный список
  `TlsHello.TargetDomains` (18 заблокированных доменов) плюс универсальный Cloudflare.
  `ProxyNode.OpenDomains` — сколько доменов открылось; узел считается лучше по числу
  доменов, затем по задержке (`PickBestNode`, `OrderForConfig`, `PickNodeForService`).
- Windows тоже скрывает список узлов: на вкладке «Узлы» сводка (всего, рабочих, доступ
  по сервисам, макс. доменов), поиск и таблицы скрыты. На «Подключении» — кнопки
  «YouTube», «Discord», «Универсальный» и вкладка «Telegram» в одном ряду;
  в трее — только «Подключить лучший».
  Имена серверов не показываются в статусе, заголовке и уведомлениях.
- `NodeRanking` задаёт порядок выбора и состав конфига: личные узлы → маскированные
  (REALITY/Vision, WS·TLS, Hysteria2·obfs) → быстрые. При сбое `ProxyManager.NodeUnhealthy`
  выбирается узел другого камуфляжного семейства (YouTube → Yandex → Cloudflare),
  и если он уже загружен в ядро — переключение идёт через Clash selector без перезапуска.
- Адрес проверки подключения зависит от режима: `MainWindow.ConnectAsync(..., probeUrls)` передаёт
  список URL в `ProxyManager.ConnectAsync`, и по ним же идёт health-check; в виджете «Видео YouTube» —
  YouTube. Отметка «Telegram» у узла ставится по `TelegramTestUrls` (`t.me`, `web.telegram.org`,
  `telegram.org` — достаточно одного).
- MTProto Telegram обслуживается автономно: `AutoCollectMtProtoAsync` каждые 10 минут
  читает включённые источники **только через RSS-зеркала** (t.me не нужен) для каналов
  и напрямую — для сайтов/JSON (`SourceKind.MtProto`, `SourceKinds.IsProxy`). Сайты
  (`tgmtproxy`, `SoliSpirit/mtproto`, `suryadev-sm`, JSON `Airuop`) добавляются
  включёнными и читаются первыми: RSS-зеркала могут тормозить. `AutoCheckMtProtoAsync`
  проверяет рабочие прокси каждые 10 минут партией 20, нерабочие — раз в 6 часов партией 20;
  если рабочих меньше `NodeLimits.MinWorkingNodes` (10), партия увеличивается втрое.
  Проверка настоящая: FakeTLS + obfuscated2 + `req_pq_multi`, «рабочий» = Telegram ответил
  `resPQ`; простой TCP-коннект не считается. **Пинг в списке — время TCP-подключения к прокси,
  как в Telegram** (без DNS; меряется отдельным проходом с малой параллельностью, минимум
  из трёх подключений, чтобы одиночная медленная попытка не показывала 1000+ мс); полное
  рукопожатие хранится только для диагностики (`HandshakeMs`). Если TCP подключился,
  а рукопожатие не прошло, или прокси недавно работал — один повтор, чтобы заминка
  не выбросила рабочий прокси. После двух провалов он уходит в «Нерабочие», но
  перепроверяется уже через 20 минут (`MtProtoSchedule.QuickRecheckMinutes`).
  Секреты с испорченным доменом фронтинга (`cdns.g]oogle.com` и подобные) отбрасываются
  при разборе: Telegram их не принимает.
  `PruneExpiredMtProto` удаляет нерабочие через 24 часа (`MtProtoRetention.DeadHours`).
  Страница называется «Telegram MTProtoProxies»; кнопка Telegram в ряду сервисов использует
  оригинальную иконку и стиль `TelegramSmallButton`, а рядом стоит вкладка `TelegramTabRadio`
  «Telegram MTProtoProxies», открывающая страницу.
  В UI остались только ручные «Проверить соединение» и «Открыть в Telegram».
- Иерархия кнопок задаёт интуитивность: `PrimaryButton` (зелёная заливка) — главное действие
  экрана, `AccentSmallButton` (тонированная плашка с зелёным контуром) — важный следующий шаг
  («Подключить», «Партия 500», «Открыть в Telegram», «Подобрать стратегию»).
  Остальные действия — обычный `SmallButton`, чтобы не спорить с главным.
- Обход DPI (zapret) — отдельный режим без прокси-узла: `winws.exe` + WinDivert разрезает
  и подменяет TLS-пакеты. Требует прав администратора; при подключении к узлу останавливается,
  при выходе/падении приложения снимается через job-объект. Десинк ограничен целевыми
  доменами (`TlsHello.TargetDomains` через hostlist): соединения к серверам узлов и служебный
  трафик скрытой проверки он не затрагивает.
- `DesyncService` — свой обход DPI без `winws.exe`: WinDivert + P/Invoke, split/disorder/fake
  для TLS ClientHello и QUIC-фейк. Подвкладка «Обход DPI» в «Обходе блокировок»,
  автоподбор, права администратора.
  Взаимоисключающий с zapret и прокси-узлом; на время проверки узлов приостанавливается.
  Рабочая стратегия — фиксированный fake google ClientHello + blind multidisorder
  (TTL 128/64, badseq −10000, TCP MD5, 11 повторов); применяется только к целевым доменам
  (YouTube/Telegram/Discord/X/Instagram/Facebook), остальные пропускаются.
  Проверено live: страница YouTube `204/200`, Discord `200`, Cloudflare `204` (не ломается).
  Видео YouTube на этом провайдере не загружается ни через наш движок, ни через zapret:
  видео-CDН блокируется по IP, для видео нужен прокси-узел. Есть блокировка QUIC (UDP/443).
- Разбор больших ClientHello: Chrome и Schannel (curl) присылают рукопожатие больше одного
  TCP-сегмента, полный разбор не проходит. `TryBuildPartialClientHello` находит SNI прямо
  в полезной нагрузке (`TryLocateHost`), и стратегии режут его по границам (перед доменом,
  после первого символа, в середине, после домена). Это важно: раньше «слепой» режим резал
  только по фиксированным 1/5/10/20/40/80, и `googlevideo.com` уходил к DPI целым куском.
- Видео YouTube у этого провайдера отдаётся локальным GGC (`rr2---sn-hxb54vo-238s.googlevideo.com`
  → `178.176.143.237`): TCP открывается за 16 мс, но TLS не проходит ни с одной стратегией,
  тогда как `redirector.googlevideo.com` через тот же движок отвечает `404`, а другие узлы
  Google — тоже `404` (URL привязан к локальному узлу). Значит блок по IP, а не по SNI.
  В истории zapret: страница — 10/10, «страница + видео» — 0/10. Для видео в программе есть
  отдельной вкладки «Видео YouTube» больше нет: видео открывается кнопкой «YouTube» на
  «Подключении» (быстрое подключение через узел с доступом к YouTube). Кнопка «Telegram
  через узел» есть на подвкладке «Telegram»: через узел идут только домены Telegram.
- Перепроверено, что локальный GGC не открывается ничем клиентским (`zapretvideo`, `videofront`):
  все 29 пресетов zapret (fake/split/multidisorder/ipfrag1/ipfrag2/hopbyhop/destopt/synack/
  syndata/rst/rstack/tamper/wssize) дают `корень 0, поток 0`; domain fronting к тому же IP
  с SNI `redirector.googlevideo.com` или `www.google.com` — тоже `0`, а чужие узлы Google —
  `404`. Зеркала Invidious/Piped с этого провайдера недоступны (`000`, одно `403`), IPv6
  роутер не выдаёт. Вывод: видео без узла не вернуть — нужен прокси-узел либо другая сеть
  (мобильный интернет/другой провайдер).
- TLS-фрагментация (`tls.fragment`/`record_fragment`) — отдельный быстрый режим: поля в
  outbound'ах sing-box для vless/vmess/trojan. Не требует WinDivert, но действует только
  на трафик через узел; флаги входят в подпись конфига, иначе смена не перезапускала бы ядро.
- TUN-обход (`TunBypassService`) — подвкладка «TUN-обход (Beta)» в «Обходе блокировок»;
  отдельный режим без узла и без WinDivert: TUN →
  локальный SOCKS5 `TlsFragmentForwarder` → прямой TCP. В конфиге sing-box правило
  `process_name: ["VieNLink.exe"] → direct` обязательно: иначе форвардер уйдёт в собственный TUN.
  Проверка TUN-обхода идёт отдельным процессом `curl.exe` (запросы VieNLink.exe намеренно прямые).
  Взаимоисключающий с узлом, zapret и WinDivert-обходом; при проверке узлов приостанавливается.
  «Автоподключение» (`TunBypassService.AutoTuneAsync`) по очереди запускает TUN с каждой
  стратегией `TlsFragmentForwarder.Strategies`, проверяет Cloudflare/YouTube/t.me через
  `curl.exe` и выбирает лучшую (YouTube → Telegram → Cloudflare → быстрейшие). Кнопка
  «Автоподключение» в UI поднимает UAC с `--tun-bypass-tune`, автоподбор показывает итоги
  в `TunBypassResultsPanel`; если ни одна не открыла ни сайта — `DesyncService.DiagnoseAsync`
  отличает IP-блок от SNI/TLS-блока.
- Правило «5 провалов» действует только на узлы, которые уже были рабочими:
  `NodeHealth.RegisterFailure` увеличивает `ProxyNode.FailCount` и оставляет узел в «Рабочих»
  без задержки; после `NodeLimits.RemoveAfterFailures` (5) провалов подряд узел получает
  `NodeStatus.Dead` и убирается из списка (`MainWindow.DropNode`). Непроверенный узел удаляется
  с первого провала — его незачем хранить. Успешная проверка обнуляет счётчик и возвращает
  задержку. `FailCount` хранится в кэше; при откате всей проверки узлы и счётчики возвращаются обратно.
- Повторы склеиваются в два шага: `NodeDedup.Collapse` убирает точные дубли по тегу
  (одинаковый outbound), затем оставляет не больше `NodeLimits.MaxPerEndpoint` (4) вариантов
  на один `ProxyNode.EndpointKey` (хост:порт). Жёсткое «одна строка на адрес» выбрасывало
  рабочие узлы с общих CDN: у Cloudflare под одним IP живут десятки аккаунтов с разными
  UUID/SNI. Проверки, скорость и `OpenDomains` переносятся только между точными дублями
  (тот же outbound/тег); при выбросе лишних вариантов по лимиту `MaxPerEndpoint` у них
  остаются их собственные результаты — успех одного аккаунта не доказывает доступ другого.
  Метка «моя подписка» и названия источников объединяются у всех. Склейка идёт при сборе
  источников, чтении кэша, слиянии и перестройке таблицы. Те же правила действуют при
  переносе статусов после обновления источников: по `ProxyNode.Id`, а не по хост:порту.
- Свежесть данных: `ProxyNode.LastSeenUtc` отмечает, когда узел последний раз приходил из
  источника. При чтении кэша узлы без встречи дольше `AppSettings.KeepUnseenNodeDays` (по
  умолчанию 7 суток) удаляются. Лимит списка — `NodeLimits.DefaultMaxTotal` (12 000); при
  переполнении сначала отбрасываются старые, затем менее маскированные. Из Telegram-каналов
  берутся только посты за `TelegramText.FreshHours` (48 ч), иначе в список попадает мёртвая
  история канала.
- Счётчики сервисов и выбор узла — одно правило (`VieNLink.Core/NodeAvailability.cs`):
  узел показывается и предлагается кнопке, только если он `Working`, с задержкой, со свежей
  проверкой (`CheckedAtUtc` не старше `IdleRecheckHours`) и проходит фильтр «Только мои».
  «Универсальный» — только подтверждённый `UniversalOk` (Cloudflare), а не любая задержка.
  Фоновая проверка сначала перепроверяет именно рабочие узлы с устаревшей проверкой —
  иначе они висят в значках часами, а кнопка не находит ни одного живого. Ход фоновой
  проверки пишется в строку состояния (Windows `StatusDetail`, Android
  `ProbeClient.ActivityText`), без разбивки по сервисам: «проверено N из M, рабочих K».
- Фоновая проверка: работает и в простое, и при активной работе. Узлы проверяются через
  скрытое ядро `HiddenProbeService` на отдельных портах (от 22000, без TUN и системного
  прокси), поэтому не мешают текущему подключению. В простое — партии по `IdleTestBatch`
  не реже раза в 5 минут, при работе — маленькие партии (до 10–30) каждые 1–2 минуты.
  Пока не набрана квота (10 рабочих и по 2 на каждый сервис), такт сокращается до
  1 минуты в простое и 2 минут при работе. Рабочие узлы перепроверяются каждые
  `NodeLimits.WorkingRecheckMinutes` (5 минут), а не раз в `IdleRecheckHours`: иначе
  «рабочий» из кэша висел в значках часами. Помимо поиска новых узлов такт раз в минуту
  делает отдельный маленький проход только по рабочим, у которых истекает окно свежести
  (`NodeAvailability.RecheckCutoffUtc`, за `RecheckLeadMinutes` = 1 мин до истечения);
  поэтому счётчики не «мигают» между редкими проходами поиска. Сами счётчики в шапке
  и на вкладке «Узлы» обновляются таймером раз в минуту (`_counterTimer`), даже когда
  ничего не проверяется.
  При активном TUN скрытое ядро не используется (его трафик ушёл бы в туннель) — проверяются
  только уже загруженные узлы. При zapret/своём обходе DPI проверка идёт: оба ограничены
  целевыми доменами и не трогают соединения к серверам узлов, а партия уменьшается до 5 узлов
  с одним запросом одновременно, чтобы не отнимать канал у видео. Пропускается только TUN-обход,
  который перехватывает весь трафик.
- Ручные проверки («Партия 500», «Все узлы») тоже идут через `HiddenProbeService`: основное
  ядро и текущее подключение не пересобираются, видео не прерывается. При TUN-подключении
  проверяются только загруженные узлы; TUN-обход на время ручной проверки снимается и
  возвращается. Фоновая проверка при занятом пробном ядре такт пропускает, а не копится в очередь.
- Взаимоисключающие режимы: TUN и системный прокси (галочка прокси отключается в TUN), zapret и
  прокси-узел (при подключении обход DPI снимается). При активном TUN любые проверки идут
  только по уже загруженным узлам — конфиг ядра не пересобирается и соединение не рвётся.
- Статусы узлов переносятся при обновлении источников (иначе после каждой загрузки рабочие узлы
  выглядели бы непроверенными).
- Личные подписки (`SourceDefinition.IsMine`) выносятся в группу «Мои подписки», их узлы
  (`ProxyNode.IsMine`) помечаются ★ и идут первыми в `OrderForConfig` и `PickBestNode`.
  Флаг хранится и в кэше (`CachedNode.IsMine`).
- `SourceKind.SingleLink` — источник из одной ссылки на узел; `NodeSourceService` разбирает её
  без HTTP. Узлы-заглушки (`0.0.0.0:1`, `127.0.0.1:1` — группы и «приложение не поддерживается»)
  отфильтровываются, а если после фильтра узлов не осталось, источник получает понятную ошибку.
- Перед изменением реестра прежние настройки пишутся в `proxy-backup.json`; при следующем запуске
  `SystemProxy.RecoverIfNeeded` возвращает их, если порт уже не слушается (аварийное завершение).
- sing-box запускается внутри job-объекта с `KILL_ON_JOB_CLOSE`: ядро не остаётся висеть после
  выхода или падения приложения.
- Список исключений `ProxyBypass` обязателен на практике: иначе служебные сервисы (в т.ч. API
  ассистента) уходят через случайный бесплатный узел и связь рвётся.
- Шапка окна: логотип и текущая версия (`HeaderVersionText`, `AppInfo.Version` без «+хэша»)
  стоят внутри левой карточки статуса, рядом с «Отключено/Подключено» — один блок с фоном.
  Под ними — компактные счётчики сервисов как на Android: тонированные плашки
  «иконка + число» для YouTube, Discord, Telegram и универсального доступа
  (`HeaderYoutubeText` и т.п.); число — рабочие узлы с доступом к сервису и **свежей**
  проверкой (не старше `IdleRecheckHours`), посчитанные тем же правилом
  (`NodeAvailability.IsUsable`), что и кнопки быстрого подключения. Устаревшая запись
  из кэша в значке не показывается, иначе число обещает узел, которого кнопка не найдёт.
  Для единой кнопки на панели задач `TaskbarIdentity.Apply()` задаёт процессу
  `AppUserModelID` до создания окон, установщик пишет тот же ID в ярлыки.
  Карточка `HeaderConnectionPanel` рядом с кнопками показывает, к чему
  подключаемся или уже подключены — сервер (host:port), домен (SNI из `NodeRanking.CamouflageHost`),
  пинг, скорость и оценку качества звёздами (`RateConnection`). Флаг страны берётся из названия
  узла (`ExtractCountryFlag`, включая экранированные `\U0001F1XX`), а подпись страны на русском
  даёт `CountryNames.FromCode` (ISO 3166-1 alpha-2, таблица в `VieNLink/CountryNames.cs`). Фон карточки — градиент
  `HeaderInfoBackgroundBrush`, слева цветная полоса по оценке; левая карточка статуса
  использует тот же градиент, шапка — `HeaderBackgroundBrush`. Если прокси-узла нет, правая
  карточка показывает активный обход: Zapret / свой DPI / TUN, стратегию, WinDivert/TUN,
  скорость и оценку `RateBypassSpeed`. Скорость TUN меряется `curl.exe` через туннель
  (`TunBypassService.MeasureSpeedAsync`), zapret/desync — напрямую по каналу. Кандидат на подключение
  хранится в `_headerConnectingNode`, состояние обновляет `UpdateHeaderConnectionInfo`;
  узел, выход и трафик лежат в подсказке карточки. Скорость и трафик приходят из
  `ClashApiClient.StreamTrafficAsync` → `TrafficSample`, страна и IP выхода — из
  `ExitInfoService` через тот же mixed-порт. Кнопка «Подключить» убрана; вместо неё две
  контекстные кнопки: `CancelButton` («Отменить», предупреждающий стиль) видна только
  во время `BeginOperation` и прерывает текущее действие, `HeaderDisconnectButton`
  («Отключить», красная) видна, когда что-то активно, и снимает узел, kill switch, zapret,
  свой обход DPI и TUN-обход (`DisconnectEverythingAsync`). Рядом — `HeaderNextNodeButton`
  («Сменить сервер»): очередь свежих проверенных серверов, текущий сервер уходит в конец,
  следующий берётся из начала (круговая ротация, `_nodeRotation`), тёплое переключение через
  Clash selector без перезапуска ядра. Ручное переключение не регистрирует провал сервера
  (`ConnectAsync(..., markFailures: false)`), поэтому рабочий сервер не выпадает из списка
  и счётчиков. Рядом `HeaderShareNodeButton` («Поделиться») — копирует `RawLink`
  подключённого сервера в буфер обмена. Обе скрыты по умолчанию,
  подсказки обновляет `UpdateHeaderState`. Постоянная строка `HeaderNodesText` показывает
  загруженные серверы и счётчики проверенных по сервисам (YouTube, Discord, Telegram,
  Универсальные); её обновляет `UpdateNodesSummary`, ход операций выводится отдельной строкой
  `StatusDetail`. Трей дублирует быстрый выбор из восьми лучших серверов.
- Все окна сообщений — `AppDialog` через класс `VieNLink.MessageBox`: тёмная/светлая тема,
  скруглённое окно, иконка, кнопки Да/Нет/ОК. Системный `System.Windows.MessageBox` в UI не используется.
- Удачные конфиги обходов хранятся в `%LOCALAPPDATA%\VieNLink\bypass-profiles.json`
  (`BypassProfileStore`): режим, стратегия, игра/QUIC, качество 0–100, доступные сервисы,
  задержка, число использований. При старте обхода лучший по качеству профиль применяется
  первым (`TryApplyBestZapretProfile` / `Desync` / `Tun`); записи нет — обычный выбор/автоподбор.
  При старте без проверки сохраняется базовый профиль (качество 30), автотюн и ручные
  проверки повышают качество. Левая карточка статуса использует абсолютный градиент
  `HeaderStatusBackgroundBrush`, чтобы фон не «прыгал» при смене ширины.
- Проверка живости узла: базовый интервал 90 с, после сбоя 25–120 с (`_nextHealthCheckUtc`).
  Каждый запрос идёт через узел и тратит его трафик, поэтому «раз в 45 секунд» было слишком часто.
- Приложение держит минимум `NodeLimits.MinWorkingNodes` (10) рабочих узлов: пока их меньше,
  фоновая проверка (`RunIdleTestAsync` в Windows, `ProbeClient.EnsureWorkingQuotaAsync`
  в Android) идёт несколькими партиями подряд, не дожидаясь следующего такта; при пустом
  или бедном списке источники обновляются чаще (15–30 минут). Дополнительно требуется по
  `NodeLimits.MinWorkingPerService` (2) проверенных узла для каждого сервиса — YouTube,
  Telegram, Discord и универсального доступа (`MissingServiceQuota` в Windows,
  `ProbeScheduler` в Android): список дефицитов пишется в журнал, поиск продолжается
  в фоне теми же партиями, а фокус сервиса меняется по кругу — иначе первый недостающий
  сервис забирал бы все раунды. На Android за такт ищется один сервис из дефицитных
  (партия 150 узлов), поэтому все четыре получают внимание без конкуренции за канал.
  Выбор узла для сервиса и «Лучший» сортируются по пингу, таблица «Рабочие» и список
  MTProto — по задержке.
- Источники разделены: каналы Telegram (`SourceKind.Telegram`) — только источники MTProto,
  их читает `MtProtoService`, а `NodeSourceService.CollectAsync` пропускает; в UI — два блока
  («Источники узлов» и «Telegram-прокси (каналы)»), «Обновить» у канала собирает прокси
  (`LoadMtProtoSourceAsync` в Windows, ветка в `SourcesActivity` на Android).
- MTProto держит минимум `MtProtoSchedule.MinWorking` (5) рабочих прокси: пока их меньше,
  каналы читаются чаще (раз в 5 минут), а проверка идёт до трёх партий подряд.
- Поиск узлов под сервис: при нехватке YouTube, Telegram или Discord первыми проверяются
  узлы из источников, которые уже давали доступ именно к этому сервису
  (`BuildServiceSourceScores` в Windows, `ProbeClient.EnsureServiceNodeAsync` в Android).
- Первый запуск и пустой список не ждут таймеров: Windows `AutoRefreshIfStaleAsync` /
  `AutoTickAsync`, Android `Maintenance` сразу обновляют источники, собирают MTProto
  и проверяют партии (`StartupProbeBatch`), чтобы у пользователя изначально были узлы и прокси.
- Параллельность проверки адаптивная: старт 8 узлов, после 40 живых — 24, после 120 — до 32
  (`TestWithApiAsync`). Фиксированные 64 душили слабые каналы и давали ложные провалы.
- Замер скорости — отдельный шаг (`ProxyManager.MeasureSpeedAsync`, ~1 МБ через локальный порт):
  задержка не показывает пропускную способность, а для видео важна именно скорость. Замер
  разрешён только для текущего узла при активном соединении; результат `ProxyNode.SpeedMbps`
  хранится в кэше и учитывается в `PickVideoNode`.
- `AppSettings.KillSwitch`: при падении ядра системный прокси не возвращается — остаётся на
  закрытом локальном порту, трафик не уходит напрямую. Явное «Отключиться» и выход из программы
  возвращают настройки; `RecoverIfNeeded` страхует следующий запуск.
- Источники хранятся в `sources.dat`, зашифрованном DPAPI (`SecretStore`, `JsonFileStore.WriteProtected`):
  ссылки подписок содержат токены. Старый открытый `sources.json` читается один раз и удаляется
  после успешной записи зашифрованного.
- **Все источники по умолчанию включены** (и ПК, и телефон): `DefaultSources` больше не
  выключает крупные агрегаторы и `@v2rayng_config`. Разовая миграция схемы настроек 3
  (`SettingsUpgrade` → `NodeSourceService.EnableAllSourcesOnce`) включает и те источники,
  которые старые установки держали выключенными; новые источники из обновлений добавляются
  включёнными.
- Надёжность фона. Android: будильник обслуживания — одноразовый `SetAndAllowWhileIdle`
  (+ перестановка при каждом срабатывании), а не `SetInexactRepeating`, который ColorOS/Doze
  откладывал на часы; результаты `:probe`, оставшиеся после убийства основного процесса,
  применяются при следующем запуске (`ProbeClient.ApplyPendingResults`); такт `ProbeScheduler`
  не наслаивается (`_busy`); полный провал партии при отсутствии прямого интернета
  откатывает статусы (`NodeTester.HasDirectInternetAsync`), иначе обрыв сети в метро
  вычищал непроверенные узлы. ПК: фоновый такт `OnIdleTick` обёрнут в try/catch; откат
  партии при недоступном прямом интернете работает для любого размера партии
  (`ProxyManager.TestWithApiAsync`), а не только для 20+.
- Кэш узлов (`nodes.json`) хранит и разобранные поля (`Protocol/Name/Server/Port/Tag/Identity/OutboundJson`):
  холодный старт не разбирает заново 12 000 ссылок (раньше это выглядело как «приложение
  долго грузится»), старый кэш читается прежним путём. Пустым списком кэш не перезаписывается
  (`SaveCache` проверяет существующий файл), а при пустом/битом `nodes.json` узлы
  восстанавливаются из `nodes.json.bak`. Явная очистка удаляет оба файла.
- Производительность: `ProxyNode.RankScore` кэширует `NodeRanking.Score` (сортировка
  12 000 узлов больше не ищет подстроки в outbound-JSON сотни тысяч раз); кэш узлов
  пишется компактно (`JsonFileStore.WriteCompact`) — файл примерно вдвое меньше и быстрее.
  На Android тяжёлые операции вынесены с UI-потока (`Task.Run`): отбор узлов для «Дальше»
  и кнопок сервисов, `ProbeClient.RunBatchAsync`, холодная сборка конфига — это устранило
  ANR, из-за которого ColorOS закрывал приложение при втором нажатии «Дальше». Сводки
  считаются одним проходом без копии списка (`NodeStore.DetailedSummary`,
  `UpdateNodesSummary`), `NodeStore.Selected` кэширует ссылку. Поиск новых узлов в
  стабильном состоянии — раз в 10 минут; рабочие всё равно перепроверяются каждые 5 минут.
- `ShareLink.Build` собирает нормальную `vless://`/`vmess://`/`trojan://`/`ss://`/`hysteria2://`
  ссылку из `OutboundJson`, если `RawLink` — это JSON из sing-box/Xray-подписки
  (иначе «Поделиться» копировало JSON-объект). Используется кнопкой «Поделиться сервером»
  на Android (системный лист) и на ПК (буфер обмена).
- Глобальный выключатель автоматики — `AppSettings.AutomationPaused` и кнопка
  «Остановить все процессы»/«Запустить все процессы» в «Настройках» (Android) и
  на вкладке настроек ПК. Останавливает проверку серверов, MTProto, обслуживание по
  будильнику, автообновления и автопереключение; туннель и ручные кнопки продолжают
  работать. Android: `Automation.Apply` гасит `ProbeScheduler`/`MtProtoScheduler`,
  снимает будильник `Maintenance` и процесс `:probe`; ПК: останавливает `_idleTimer`,
  `_autoTimer`, `_probe` и проверки обновлений.
- Конфиг ядра (`runtime\sing-box-config.json`) с UUID удаляется после отключения и выхода.
- `NodeSourceService.FetchRawAsync` при ошибке прокси повторяет запрос напрямую (`HttpDirect`):
  иначе включённый прокси на мёртвом узле блокировал обновление GitHub-списков.
- Событие сбоя узла не теряется во время проверки: `OnNodeUnhealthy` запоминает узел в
  `_pendingFailoverName`, а `EndOperation` запускает переключение сразу после операции.
- Android-автопереключение (`TunnelService.HealthTickAsync`) проверяет выбранный узел раз в
  20 с и после двух провалов переключается на загруженный. Перед проверкой `NodeStore`
  догружается, а если выбранный узел не входит в работающий конфиг (его могла выбрать
  фоновая проверка) — сразу берётся загруженный, не дожидаясь двух провалов. Провал
  регистрируется в `NodeHealth`, поэтому счётчики сразу перестают показывать мёртвый узел.
  Если у устройства нет Wi-Fi/мобильной сети (Doze, экран выключен), проверка пропускается —
  иначе приложение меняло бы живые узлы из-за отсутствия сети. При перезапуске службы
  системой (`START_STICKY` без intent) восстанавливается последний режим (`LastRunWasBypass`):
  TUN-обход по сохранённой стратегии или прокси-узел из кэша, а не прямой TUN без прокси.
- `NodeRanking.Score` штрафует узлы с `tls.insecure` (−40) и помечает их «⚠»: такой узел видит
  трафик посредника, поэтому автоматически первым не выбирается.
- `AppLog.Write` пишет стек исключения, а не только текст: без него разбор падений у пользователя
  невозможен. Журналы ротируются по 2 МБ и хранятся 14 дней.
- Автозапуск приложения — ключ `HKCU\...\Run` с `--minimized` (`AutostartService`): окно стартует в трее.
- `WindowsIntegration.EnableStoreAppLoopbackAsync` вызывает `CheckNetIsolation LoopbackExempt -is`:
  без этого приложения Microsoft Store не видят прокси на 127.0.0.1.
- Ядро обновляется отдельно: `SingBoxCore.CheckLatestVersionAsync` (GitHub API + зеркало) и
  `UpdateAsync`; фактическая версия пишется в `core\version.txt` (`SingBoxCore.InstalledVersion`).
- `MainWindow` разбит на partial-классы по темам: монолит на 4 900 строк заменён файлами
  `MainWindow.*.cs`; новые функции живут в `MainWindow.ProductFeatures.cs`.
- `VieNLink.SelfTest -- parsers` — golden-тесты: мусорные ссылки, порт вне диапазона, длинный хост,
  битый base64, неверный Reality-ключ, точные дубли и мягкий лимит на сервер:порт.
  `.github/workflows/build.yml` собирает решение и запускает `parsers` на каждый push.

## Проверено

- Сквозной путь: узел из открытого списка → sing-box → системный прокси → YouTube отвечает `HTTP 200`.
- Склейка повторов: точные дубли (одинаковый outbound) убираются, на один сервер:порт
  остаётся до `NodeLimits.MaxPerEndpoint` вариантов — общие CDN-адреса больше не теряют
  рабочие аккаунты; `parsers` проверяет лимит, перенос проверок и отсев старых постов Telegram.
- Свой обход DPI (WinDivert): рабочая стратегия «Фейк + multidisorder, TTL 128/64» открывает
  страницу YouTube (`204/200`) и Discord (`200`), Cloudflare остаётся `204`; десинк только для целевых доменов.
  Видео YouTube не загружается ни через наш движок, ни через zapret — видео-CDН блокируется по IP; нужен узел.
- **Совместимость обхода и проверки узлов (21.11.2026, live, от админа):** при работающем
  `desynclive` (WinDivert, стратегия fake+multidisorder) одновременно запущен
  `SelfTest -- hiddenprobe 3` — 2 рабочих из 3, лучший 12 доменов; YouTube-видео
  открылось и играло (плеер `0:00 / 3:33`), обход не прервался. Системный прокси всё
  время был выключен.
- TUN-обход (live, от админа): Cloudflare `204` во всех режимах разреза; YouTube/t.me — TCP timeout
  по IP; после остановки прямой интернет `204`, адаптер и sing-box не остаются.
- TUN (от админа): адаптер `sing-tun Tunnel` создаётся, обычный трафик идёт (HTTP 204),
  правило `reject` для домена и правило по процессу применяются к системному трафику.
- Правила по процессам (без админа): `process_name: ["curl.exe"]` пропускает curl и режет остальных.
- Системный прокси: включение, трафик через локальное ядро, точное восстановление прежних настроек реестра.
- Все варианты конфига (системный прокси/TUN × сайты/приложения) проходят `sing-box check`.
- Подписка: Clash YAML → узлы, заголовок `subscription-userinfo` → «4,0 ГБ из 100 ГБ · до 15.01.2027».
- Разбор: vless/vmess/trojan/ss/hysteria2, Xray JSON, sing-box JSON, Clash YAML, base64-список.

## Ограничения, которые нужно помнить
- Живых узлов в открытых списках 0,2–3 %; один и тот же список за час может полностью «умереть».
- Каналы `t.me` в РФ недоступны напрямую — их можно использовать только при уже работающем обходе.
- Скачивание с GitHub Releases часто заблокировано: используются зеркала `ghfast.top`, `gh-proxy.com`.
- sing-box не понимает BOM в JSON — конфиг писать только UTF-8 без BOM.
- sing-box не поддерживает xHTTP/splithttp (только HTTP, WS, QUIC, gRPC, HTTPUpgrade):
  узел с `type=xhttp` собрать в конфиг нельзя, нужен второй движок (см. фазу 6 плана).
- `LinkParser.ApplyTransport` не имеет `default`: неизвестный транспорт не ставится вовсе,
  и узел молча превращается в «VLESS поверх голого TCP».
- Clash API отдаёт только задержку. Объём, RST и заморозку TCP так не увидеть — нужен
  прямой замер через локальный порт (как в `MeasureSpeedAsync`).
- ТСПУ 2026 «морозит» TCP-соединение после ~25 пакетов (≈4,8–33 КБ), не присылая RST:
  проверка «сайт открылся» не означает, что узел годится для видео.
- Кастомный шаблон `TabItem` не отдаёт содержимое вкладок в UI Automation — автоклики по элементам
  внутри вкладок не работают (на приложение не влияет).

## Сборка и проверка
```
dotnet build VieNLink.slnx
dotnet run --project VieNLink.SelfTest -- parsers
dotnet run --project VieNLink.SelfTest -- sources
dotnet run --project VieNLink.SelfTest -- cache
dotnet run --project VieNLink.SelfTest -- hiddenprobe 5
dotnet run --project VieNLink.SelfTest -- fetch http://127.0.0.1:8123/sub
dotnet run --project VieNLink.SelfTest -- discover
dotnet run --project VieNLink.SelfTest -- checkconfigs
dotnet run --project VieNLink.SelfTest -- procmatch
dotnet run --project VieNLink.SelfTest -- tun
dotnet run --project VieNLink.SelfTest -- proxytest
dotnet run --project VieNLink.SelfTest -- mtprotocheck 40
dotnet run --project VieNLink.SelfTest -- mtprotohost 95.217.135.241 443 <секрет>
dotnet run --project VieNLink.SelfTest -- liveness 60 https://www.youtube.com/
dotnet run --project VieNLink.SelfTest -- desync
dotnet run --project VieNLink.SelfTest -- desyncinstall
dotnet run --project VieNLink.SelfTest -- desyncstart
dotnet run --project VieNLink.SelfTest -- forwarder
dotnet run --project VieNLink.SelfTest -- forwarder-e2e
dotnet run --project VieNLink.SelfTest -- tunbypassstart
dotnet run --project VieNLink.SelfTest -- tunbypasslive
dotnet run --project VieNLink.SelfTest -- tunbypassautotune
dotnet run --project VieNLink.SelfTest -- desynclive
dotnet run --project VieNLink.SelfTest -- video
dotnet run --project VieNLink.SelfTest -- videohost [хост]
dotnet run --project VieNLink.SelfTest -- videolive
dotnet run --project VieNLink.SelfTest -- videofallback
dotnet run --project VieNLink.SelfTest -- videomulti
dotnet run --project VieNLink.SelfTest -- zapretvideo
dotnet run --project VieNLink.SelfTest -- videofront
dotnet run --project VieNLink.SelfTest -- blocktype
dotnet run --project VieNLink/VieNLink.csproj
powershell -File build\publish-release.ps1
powershell -File build\publish-installer.ps1
powershell -File build\publish-store.ps1
```
### Android (отдельный toolchain, без прав администратора)

```powershell
$dotnet = "$env:LOCALAPPDATA\dotnet\dotnet.exe"   # SDK 11 + workload android
& $dotnet build VieNLink.Android\VieNLink.Android.csproj -c Debug `
    -p:AndroidSdkDirectory="$env:LOCALAPPDATA\android-sdk" `
    -p:JavaSdkDirectory="$env:LOCALAPPDATA\jdk17"
```
- Системный `dotnet` не соберёт Android: workload поставлен в пользовательский SDK.
- Ядро — `VieNLink.Android\Libs\libbox.aar`; без него приложение собирается, но пишет «libbox не подключён».
- Подробности и установка на устройство — `docs/android-design.md` §15.

- `parsers` — разбор ссылок, форматов подписок и сборка конфига (без сети).
- `fetch <url>` — загрузка подписки: формат, узлы, `subscription-userinfo`.
- `sources` — список источников из зашифрованного `sources.dat` (проверка DPAPI и миграции).
- `cache` — размеры кэшей узлов, MTProto и источников.
- `mtprotocheck [N] [фильтр]` — живая проверка N прокси из кэша настоящим хендшейком
  (FakeTLS + `req_pq_multi` → `resPQ`); показывает, какие из «рабочих» действительно годятся
  для Telegram (нужна сеть).
- `mtprotohost <сервер> <порт> <секрет>` — проверка одного MTProto-прокси: разбор секрета,
  FakeTLS, obfuscated2, ответ Telegram или причина отказа (нужна сеть).
- `hiddenprobe [N]` — скрытая проверка N уже рабочих узлов отдельным ядром на своих портах:
  проверяет, что фоновый туннель не зависит от основного подключения и видит домены.
- `proxytest` — системный прокси без внешнего узла: ядро с `direct`, включение, запрос, восстановление реестра.
- `checkconfigs` — все варианты конфига через настоящий `sing-box check`.
- `procmatch` — правило `process_name`: `curl.exe` проходит, остальные процессы режутся (без админа).
- `tun` — TUN-режим (нужен админ): адаптер, обычный трафик, правило домена и процесс
- `statuslogic` — без внешних узлов (выход `direct`): отметки YouTube/Telegram, уход узла
  в нерабочие, сроки хранения нерабочих MTProto
- `discover` — автопоиск источников на GitHub (тратит лимит API, нужна сеть).
- `liveness [N]` — живой прогон источников и подключение к лучшему узлу (нужна сеть).
- `seed` — наполнить кэш приложения узлами из источников.
- `desync` — разбор TLS ClientHello и сборка split/fake/QUIC-пакетов без WinDivert.
- `desyncinstall` — скопировать/скачать WinDivert и шаблон QUIC-фейка.
- `desyncstart` — загрузить WinDivert; без прав администратора проверяет понятную ошибку.
- `forwarder` — SOCKS5-форвардер TUN-обхода: локальный сервер, разрез ClientHello, ответ.
- `forwarder-e2e` — реальный TLS через SOCKS5-форвардер (нужна сеть).
- `tunbypassstart` — поднять TUN-обход; без прав администратора проверяет ошибку и остановку.
- `tunbypasslive` — живой TUN-обход (нужен админ): через форвардер идёт только `curl.exe`,
  проверяются YouTube/Telegram и счётчики разрезанных соединений.
- `tunbypassautotune` — автоподключение TUN-обхода (нужен админ): перебирает все стратегии
  фрагментации, проверяет Cloudflare/YouTube/Telegram и выбирает лучшую.
- `desynclive` — живой WinDivert-обход (нужен админ): YouTube/Discord/Cloudflare/видео по стратегиям.
- `video` — видео YouTube через SOCKS5-форвардер без админа: страница, поиск `videoplayback`, загрузка по стратегиям.
- `videohost [хост]` — конкретный видео-узел через форвардер: TCP и TLS с разными разрезами (проверка «IP или SNI»).
- `videolive` — живой обход (нужен админ): страница, видео, корень видео-хоста, `redirector`,
  TLS-диагноз; с блокировкой QUIC и без.
- `videofallback` — живой обход (нужен админ): видео через другие узлы Google (`--resolve`) — уйти с локального GGC.
- `videomulti` — все видео-хосты из страницы: какие узлы googlevideo отдают поток.
- `zapretvideo` — все 29 пресетов zapret против настоящего видео-узла (нужен админ).
- `videofront` — domain fronting: тот же IP с чужим SNI, настоящий Host внутри TLS (нужен админ).
- `blocktype` — TCP/TLS-диагностика: IP-блок или SNI/TLS-блок.

Перед сборкой закрыть запущенный `VieNLink.exe`, иначе файлы в `bin` заблокированы.

## Правила работы
- Только точечные правки; не переписывать архитектуру без просьбы.
- UI — в XAML (кроме трея), работа с ядром — через `ProxyManager`.
- Тексты UI, логи и комментарии — на русском; 4 пробела, `namespace VieNLink`.
- После изменений обязательно `dotnet build` и сообщать результат.
- Не добавлять NuGet-пакеты.

