# AGENTS.md — VieNLink

## Статус
Бета-версия: основные режимы проверены, но возможны сбои. Статус виден в интерфейсе
(значок «БЕТА» в шапке, «Общие», заголовок окна) и в установщике; снимается
константой `AppInfo.IsBeta` в `VieNLink/AppInfo.cs`.

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
- `VieNLink.slnx` — решение: `VieNLink` и `VieNLink.SelfTest`.
- `VieNLink/App.xaml(.cs)` — запуск, один экземпляр, тема, журнал, перехват ошибок.
- `VieNLink/app.manifest` — запуск без прав администратора, Windows 10/11, длинные пути.
- `VieNLink/MainWindow.xaml(.cs)` — весь UI. Верхний уровень: «Подключение» (подвкладки «Обход блокировок», «Свой обход DPI», «Видео YouTube», «Telegram», «Туннель», «TUN-обход»), «Инструменты» (Узлы / Источники / Диагностика / Настройки), «Журнал», «Общие»; плюс трей и кнопка подключения в шапке.
- `VieNLink/MainWindow.*.cs` — partial-классы того же окна по темам: `Loading`, `SourcesRefresh`, `IdleTest`, `Connect`, `Zapret`, `Desync`, `TunBypass`, `Telegram`, `Nodes`, `Settings`, `SourceList`, `CoreLog`, `TrayState`, `ProductFeatures`.
- `VieNLink/AutostartService.cs` — автозапуск при входе в Windows через ключ `HKCU\...\Run` (аргумент `--minimized`).
- `VieNLink/SecretStore.cs` — шифрование DPAPI (P/Invoke `CryptProtectData`) для `sources.dat`.
- `VieNLink/WindowsIntegration.cs` — выходной IP/страна через узел и снятие loopback-изоляции приложений Магазина (`CheckNetIsolation`).
- `VieNLink/Models.cs` — `ProxyNode`, `SourceDefinition`, `AppSettings`, `NodeCache`, `SystemProxyBackup`.
- `VieNLink/ViewModels.cs` — строки таблиц `NodeRow` и `SourceRow`.
- `VieNLink/NodeRanking.cs` — оценка маскировки узлов (REALITY, uTLS, Vision, WS/gRPC, Hysteria2), семейства камуфляжа и пометки для таблиц.
- `VieNLink/ZapretService.cs` — обход DPI через zapret: скачивание winws/WinDivert в папку `zapret` рядом с exe (при запрете записи — в `%LOCALAPPDATA%`), запуск и остановка, профиль fake/multidisorder.
- `VieNLink/DesyncService.cs` — собственный обход DPI на WinDivert (без winws): split/disorder/fake для TLS ClientHello, QUIC-фейк, автоподбор; подвкладка «Свой обход DPI» в «Подключении».
- `VieNLink/TlsFragmentForwarder.cs` — локальный SOCKS5-форвардер для TUN-обхода: прямой TCP-канал и фрагментация TLS ClientHello в пользовательском потоке.
- `VieNLink/TunBypassService.cs` — TUN-обход без узла и WinDivert: sing-box TUN → SOCKS5-форвардер, конфиг, запуск/остановка.
- `VieNLink/LinkParser.cs` — разбор ссылок в outbound-объекты sing-box (vless/vmess/trojan/ss/hysteria2).
- `VieNLink/NodeSources.cs` — загрузка источников, извлечение ссылок, base64-подписки, текст Telegram, кэш.
- `VieNLink/SubscriptionParser.cs` — форматы подписок: base64, Xray/sing-box JSON, Clash YAML, `subscription-userinfo`.
- `VieNLink/MtProtoService.cs` — MTProto-прокси Telegram: `tg://proxy` из HTML каналов, TCP-проверка, статусы «рабочий/нерабочий», кэш; нерабочие хранятся 14 дней.
- `VieNLink/SourceDiscoveryService.cs` — автопоиск источников: GitHub search + дерево файлов, обход публичных сайтов с ежедневными подписками и проверка кандидатов.
- `VieNLink/SingBoxCore.cs` — поиск/скачивание ядра и проверка `sing-box version`.
- `VieNLink/SingBoxConfigBuilder.cs` — сборка конфига: mixed inbound, TUN, правила по процессам и доменам, Clash API.
- `VieNLink/SingBoxRunner.cs` — запуск/остановка ядра, `sing-box check`, чтение stderr.
- `VieNLink/ClashApiClient.cs` — `/proxies/{tag}/delay` и переключение selector.
- `VieNLink/ProxyManager.cs` — оркестрация: проверка узлов, выбор узла, системный прокси, health-check. Есть `TestLoadedNodesAsync` — проверка только уже загруженных узлов без перезапуска ядра (для простоя).
- `VieNLink/SystemProxy.cs` — включение/восстановление системного прокси и список исключений.
- `VieNLink/TrayIconService.cs` — значок в трее.
- `VieNLink/ZapretWidget.xaml(.cs)` — плавающий виджет с тремя способами: «Обход запрет» (zapret), «Свой обход DPI» (WinDivert) и «Видео YouTube» (через узел). Работающий режим подсвечен, ниже мелко подписаны режим и стратегия; в свёрнутом виде — точка состояния (зелёная/красная) и название включённого режима.
- `VieNLink/ProcessPickerWindow.xaml(.cs)` — выбор приложений из запущенных процессов для правила «только для приложений из списка».
- `VieNLink/Themes/*.xaml` — палитры тёмной/светлой темы и стили контролов.
- `VieNLink.SelfTest/Program.cs` — self-test: `parsers`, `liveness`, `seed`, `desync`, `forwarder`.
- `build/publish-release.ps1` — релиз: self-contained один exe, опционально ядро в `core\`, zapret в `zapret\`, ZIP.
- `build/publish-installer.ps1` — оба варианта поставки: портативный ZIP + `setup.exe` (Inno Setup).
- `build/installer.iss` — сценарий Inno Setup: установка в профиль пользователя, ярлыки, деинсталлятор.

## План развития обхода DPI

- **Быстрый шаг (сделано, тестируется):** TLS-фрагментация средствами sing-box —
  `tls.fragment` / `tls.record_fragment` в outbound'ах узлов. Галочки в «Настройках»,
  без WinDivert и внешних компонентов. Работает только для трафика через прокси-узел.
- **Средний шаг (сделано, тестируется):** собственный обход DPI на WinDivert в подвкладке
  «Свой обход DPI» («Подключение»): split/disorder/fake для TLS ClientHello и QUIC-фейк, без winws.exe.
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
- Узлы проверяются по двум адресам (`ProxyManager.YoutubeTestUrl`, `TelegramTestUrl`). Результаты
  хранятся в `ProxyNode.YoutubeOk/TelegramOk`; ни одного успеха → `NodeHealth.RegisterFailure`:
  у рабочего узла `ProxyNode.FailCount` растёт (он остаётся в «Рабочих» без задержки), непроверенный
  удаляется сразу, успех обнуляет счётчик → `Working`. Рабочий узел удаляется только после
  `NodeLimits.RemoveAfterFailures` (10) провалов подряд.
- `NodeRanking` задаёт порядок выбора и состав конфига: личные узлы → маскированные
  (REALITY/Vision, WS·TLS, Hysteria2·obfs) → быстрые. При сбое `ProxyManager.NodeUnhealthy`
  выбирается узел другого камуфляжного семейства (YouTube → Yandex → Cloudflare),
  и если он уже загружен в ядро — переключение идёт через Clash selector без перезапуска.
- Адрес проверки подключения зависит от режима: `MainWindow.ConnectAsync(..., probeUrls)` передаёт
  список URL в `ProxyManager.ConnectAsync`, и по ним же идёт health-check. В «Telegram через узел» —
  `t.me`, `web.telegram.org`, `telegram.org` (достаточно одного), в «Видео через узел» — YouTube.
  Отметка «Telegram» у узла ставится по любому из этих адресов (`TelegramTestUrls`).
- Иерархия кнопок задаёт интуитивность: `PrimaryButton` (зелёная заливка) — главное действие
  экрана, `AccentSmallButton` (тонированная плашка с зелёным контуром) — важный следующий шаг
  («Подключить», «Партия 500», «Собрать из каналов», «Открыть в Telegram», «Подобрать стратегию»).
  Остальные действия — обычный `SmallButton`, чтобы не спорить с главным.
- Обход DPI (zapret) — отдельный режим без прокси-узла: `winws.exe` + WinDivert разрезает
  и подменяет TLS-пакеты. Требует прав администратора; при подключении к узлу останавливается,
  при выходе/падении приложения снимается через job-объект.
- `DesyncService` — свой обход DPI без `winws.exe`: WinDivert + P/Invoke, split/disorder/fake
  для TLS ClientHello и QUIC-фейк. Подвкладка «Свой обход DPI» в «Подключении»,
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
  подвкладка «Видео YouTube» («Подключение») с кнопкой «Видео через узел»: `SiteRouteMode.OnlyListed`
  со списком доменов YouTube, остальные сайты идут напрямую. Такая же кнопка «Telegram через узел»
  есть на подвкладке «Telegram»: через узел идут только домены Telegram.
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
- TUN-обход (`TunBypassService`) — подвкладка «TUN-обход (бета)» в «Подключении»;
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
- Правило «10 попыток» действует только на узлы, которые уже были рабочими:
  `NodeHealth.RegisterFailure` увеличивает `ProxyNode.FailCount` и оставляет узел в «Рабочих»
  без задержки; после `NodeLimits.RemoveAfterFailures` провалов подряд узел получает
  `NodeStatus.Dead` и убирается из списка (`MainWindow.DropNode`). Непроверенный узел удаляется
  с первого провала — его незачем хранить. Успешная проверка обнуляет счётчик и возвращает
  задержку. `FailCount` хранится в кэше; при откате всей проверки узлы и счётчики возвращаются обратно.
- Повторы склеиваются в два шага: `NodeDedup.Collapse` убирает точные дубли по тегу
  (одинаковый outbound), затем оставляет не больше `NodeLimits.MaxPerEndpoint` (4) вариантов
  на один `ProxyNode.EndpointKey` (хост:порт). Жёсткое «одна строка на адрес» выбрасывало
  рабочие узлы с общих CDN: у Cloudflare под одним IP живут десятки аккаунтов с разными
  UUID/SNI. Проверки, скорость, метка «моя подписка» и названия источников переносятся на
  оставшиеся узлы. Склейка идёт при сборе источников, чтении кэша, слиянии и перестройке таблицы.
- Свежесть данных: `ProxyNode.LastSeenUtc` отмечает, когда узел последний раз приходил из
  источника. При чтении кэша узлы без встречи дольше `AppSettings.KeepUnseenNodeDays` (по
  умолчанию 3 суток) удаляются. Из Telegram-каналов берутся только посты за
  `TelegramText.FreshHours` (48 ч), иначе в список попадает мёртвая история канала.
- Проверка в простое: когда окно свёрнуто и компьютер бездействует, проверяются накопившиеся узлы
  по 1–2 одновременно; при подключённом туннеле — только уже загруженные узлы (без перезапуска ядра),
  а в TUN без подключения проверка не запускается, чтобы не перехватывать чужой трафик.
- Взаимоисключающие режимы: TUN и системный прокси (галочка прокси отключается в TUN), zapret и
  прокси-узел (при подключении обход DPI снимается). При активном туннеле любые проверки идут
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
- Шапка окна — главное действие: кнопка «Подключить/Отключиться» (`OnHeaderConnectClick`),
  скорость и трафик (`ClashApiClient.StreamTrafficAsync` → `TrafficSample`), страна и IP выхода
  (`ExitInfoService` через тот же mixed-порт). Трей дублирует быстрый выбор из восьми лучших узлов.
- Проверка живости узла: базовый интервал 90 с, после сбоя 25–120 с (`_nextHealthCheckUtc`).
  Каждый запрос идёт через узел и тратит его трафик, поэтому «раз в 45 секунд» было слишком часто.
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
- Конфиг ядра (`runtime\sing-box-config.json`) с UUID удаляется после отключения и выхода.
- `NodeSourceService.FetchRawAsync` при ошибке прокси повторяет запрос напрямую (`HttpDirect`):
  иначе включённый прокси на мёртвом узле блокировал обновление GitHub-списков.
- Событие сбоя узла не теряется во время проверки: `OnNodeUnhealthy` запоминает узел в
  `_pendingFailoverName`, а `EndOperation` запускает переключение сразу после операции.
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
- Кастомный шаблон `TabItem` не отдаёт содержимое вкладок в UI Automation — автоклики по элементам
  внутри вкладок не работают (на приложение не влияет).

## Сборка и проверка
```
dotnet build VieNLink.slnx
dotnet run --project VieNLink.SelfTest -- parsers
dotnet run --project VieNLink.SelfTest -- sources
dotnet run --project VieNLink.SelfTest -- cache
dotnet run --project VieNLink.SelfTest -- fetch http://127.0.0.1:8123/sub
dotnet run --project VieNLink.SelfTest -- discover
dotnet run --project VieNLink.SelfTest -- checkconfigs
dotnet run --project VieNLink.SelfTest -- procmatch
dotnet run --project VieNLink.SelfTest -- tun
dotnet run --project VieNLink.SelfTest -- proxytest
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
```
- `parsers` — разбор ссылок, форматов подписок и сборка конфига (без сети).
- `fetch <url>` — загрузка подписки: формат, узлы, `subscription-userinfo`.
- `sources` — список источников из зашифрованного `sources.dat` (проверка DPAPI и миграции).
- `cache` — размеры кэшей узлов, MTProto и источников.
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
