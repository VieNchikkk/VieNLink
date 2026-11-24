# Android-версия: проектирование

Документ отвечает на три вопроса: **что именно можно сделать**, **какими методами**
и **какие для этого нужны библиотеки и код**. План и статус — в `plan-2026.md`, фаза 8.

- **Составлен:** 20.11.2026
- **Статус:** проектирование, код не начат
- **Связанные документы:** `plan-2026.md` (фаза 8), `VERSIONING.md`

---

## 1. Границы

Android-версия — **не порт** Windows-приложения. Общее у них — ядро (разбор ссылок,
источники, рейтинг, сборка конфига, проверка узлов). Различается всё, что касается
туннеля, хранения секретов, фона и интерфейса.

Главный вывод проектирования: **на Android доступны 4 из 6 наших режимов**, один
деградирует, один невозможен. При этом Android даёт три вещи, которых нет на Windows,
и они частично компенсируют потери.

---

## 2. Матрица возможностей платформы

| Возможность | Windows (сейчас) | Android | Следствие |
|---|---|---|---|
| Перехват всего трафика | TUN (`wintun.dll`) или системный прокси (реестр WinINET) | **только** `VpnService` (fd TUN), один VPN на устройство | системного прокси нет; TUN становится основным, а не альтернативным режимом |
| Разделение по приложениям | правило `process_name` в sing-box | нативно: `addAllowedApplication` / `addDisallowedApplication` | проще и надёжнее, чем на Windows |
| Свои пакеты (fake, TTL, badseq, QUIC-фейк) | WinDivert | **невозможно без root** — нет raw-сокетов | `DesyncService` не переносится |
| Фрагментация TLS в пользовательском потоке | `TlsFragmentForwarder` | **работает** (чистый .NET, сокеты) | TUN-обход переносится 1:1 |
| Запуск внешнего процесса-ядра | `sing-box.exe` + job-объект | **запрещено** (W^X с Android 10) | нужен `libbox.aar` через JNI |
| Системный kill switch | свой (не возвращаем прокси) | «Always-on VPN» + «Блокировать без VPN» (настройка ОС) | надёжнее, но управляется пользователем |
| Автозапуск | `HKCU\...\Run` | `BOOT_COMPLETED` + `startForegroundService` | требует foreground-сервис |
| Трей / быстрый выбор узла | `NotifyIcon` | foreground-уведомление + `TileService` (плитка) | плитка даже удобнее |
| Шифрование секретов | DPAPI (`CryptProtectData`) | Android Keystore (AES-GCM ключ в Keystore) | формат `sources.dat` за интерфейсом |
| Список приложений | перечисление процессов | `PackageManager` + `QUERY_ALL_PACKAGES` | вне Play — ограничение политики |
| MTProto-прокси Telegram | находим и показываем | `tg://proxy?...` — применяется в Telegram одним нажатием | на Android **лучше**, чем на Windows |
| Проверка узлов через Clash API | тестовое ядро + `ClashApiClient` | то же: libbox с `mixed`-inbound и `clash_api`, без TUN | `ProxyManager` почти не меняется |
| Смена сети (Wi-Fi ↔ LTE) | не отслеживается | `ConnectivityManager.NetworkCallback` + `setUnderlyingNetworks` | новая обязательная логика |
| Фон и сон | обычный процесс | Doze: foreground service + исключение из оптимизации батареи | новая обязательная логика |

---

## 3. Что переносится, что деградирует, что теряется

### Переносится без изменений (≈5 700 строк)

`LinkParser`, `SubscriptionParser`, `NodeSources`, `NodeDedup`, `NodeHealth`, `NodeRanking`,
`SingBoxConfigBuilder`, `ClashApiClient`, `JsonFileStore`, `AppLog`, `Base64Util`,
`SourceDiscoveryService`, `MtProtoService`, `TlsFragmentForwarder`, `TunBypassService`.

### Деградирует

- **«Свой обход DPI»** — вместо WinDivert остаётся только userspace-фрагментация внутри
  TUN. Против SNI-блокировки работает, против IP-блокировки — нет (как и на Windows).
- **«Обход блокировок» через узел** — работает полностью, включая `tls.fragment`
  (это поля конфига sing-box, от платформы не зависят).

### Теряется

- `DesyncService` (2 192 строки) — нет raw-сокетов.
- `ZapretService` (836) — нет `winws.exe`/WinDivert.
- `SystemProxy` (179) — нет системного прокси.
- `SingBoxRunner` (308) — нельзя запускать ELF; заменяется на in-process libbox.

**Важно:** потерю десинка частично закрывает то, что на Android **TUN есть всегда** —
значит режим «TUN-обход» становится основным путём «без узла», а не экзотикой.

---

## 4. Развилка ядра (главное решение)

На Windows ядро — внешний `sing-box.exe`, и вопрос лицензии решается разделением процессов.
На Android библиотека **линкуется** (JNI), поэтому лицензия становится ограничением.
Для личного использования она не срабатывает вовсе, но при публикации — сработает.

| Вариант | Лицензия | TUN | Clash API (проверки, selector, трафик) | Протоколы | xHTTP | Цена |
|---|---|---|---|---|---|---|
| **A. libbox (sing-box)** | **GPL-3.0** | есть (`PlatformInterface.openTun`) | есть (`CommandServer`) | VLESS/VMess/Trojan/SS/Hysteria2/TUIC/AnyTLS/ShadowTLS/WireGuard/SSH | **нет** | низкая: почти всё готово |
| **B. libXray (Xray-core)** | MPL-2.0 | **нет** — нужен tun2socks | **нет** — свой тестер | VLESS/VMess/Trojan/SS + xhttp/splithttp | **есть** | средняя: TUN и API пишем сами |
| **C. Свой стек + свои протоколы** | наша | свой (lwIP/`System.Net.Quic`) | свой | пишем сами | пишем сами | очень высокая: 2–4 месяца только на протоколы |
| **D. Гибрид: libbox + наш форвардер** | GPL-3.0 | из libbox | из libbox | из libbox | нет | низкая + TUN-обход наш |

### Распространение подтверждено (21.11.2026) — развилка обязательна

Изначально предполагалось личное использование, но **приложение будет распространяться**.
Значит `libbox` (GPL-3.0) влечёт обязательства на весь Android-клиент: JNI — это линковка,
а не «отдельный процесс». Три пути, из них законных два:

| Путь | Что делаем | Цена | Итог |
|---|---|---|---|
| **GPL-3.0, открытый код** | публичный репозиторий, `LICENSE`, `THIRD-PARTY-NOTICES` | **0 инженерных дней** | код открыт, пересборка разрешена, доверие выше |
| **MPL-2.0, закрытый код** | `libXray` + tun2socks + свой тестер узлов | **+10–20 дней**, зато появляется xHTTP | код закрыт |
| приватный репозиторий + закрытый APK + libbox | формально нарушение GPL | — | не рассматриваем |

**Принято (21.11.2026): путь 1 — публичный репозиторий + GPL-3.0.** Причины:

1. Цена нулевая: libbox уже даёт TUN, все протоколы и Clash API.
2. Ценность проекта не в коде, а в подборе узлов, цепочках и удобстве. Публикация кода
   не отдаёт ни подписки (`sources.dat` шифруется и в репозиторий не попадает), ни серверы.
3. Для инструмента обхода цензуры открытый код — норма: пользователь может проверить,
   что внутри нет «слива», а новые приёмы ТСПУ быстрее чинятся сообществом.
4. Закрытый путь стоит 10–20 дней ровно на том, что уже работает.

**Следствия, которые нужно принять:**

- лицензия GPL-3.0 распространяется на **весь проект**, включая Windows-часть
  (один репозиторий — одна лицензия). Для Windows это юридически не обязательно
  (`sing-box.exe` — отдельный процесс), но практически неизбежно;
- любой, кто распространяет изменённую сборку, обязан опубликовать исходники;
- как правообладатель автор сохраняет возможность позже лицензировать **свой** код иначе,
  но сборку с libbox всё равно нельзя сделать закрытой;
- **бонус:** публичный репозиторий открывает проверку обновлений через GitHub Releases API
  (сейчас в `docs/VERSIONING.md` записано, что её нет именно из-за приватности).

**Путь 2 (закрытый APK + `libXray`/MPL) отклонён**, но совместимость с ним сохраняем:
особенности libbox не выходят за пределы `VieNLink.Android`, `ICoreRuntime` тонкий.
Если когда-нибудь понадобится закрытая сборка — переезд останется возможным.

### Правовая комплектация (нужна при любом пути)

- `LICENSE` — GPL-3.0 для Android-части (или MPL-2.0 при варианте с libXray).
- `THIRD-PARTY-NOTICES.md` — sing-box (GPL-3.0, ссылка на исходники), MAUI/Avalonia (MIT),
  AndroidX (Apache-2.0), **WinDivert (LGPL — поэтому и поставляется отдельной DLL,
  а не статически; заменяемость сохраняется)**.
- Если `publish-release.ps1` кладёт `sing-box.exe` в поставку — это распространение
  GPL-программы: нужна ссылка на её исходники и текст лицензии в поставке.
- Никаких упоминаний связи с SagerNet/sing-box в названии и оформлении (доп. условие лицензии).

---

## 5. Архитектура

```
┌─────────────────────────────────────────────────────────┐
│ UI (MAUI или Avalonia)                                  │
│  Подключение · Узлы · Источники · Диагностика · Настройки│
└───────────────────────┬─────────────────────────────────┘
                        │ ViewModels (переносимые)
┌───────────────────────▼─────────────────────────────────┐
│ VieNLink.Core (net10.0, без Windows)                    │
│  LinkParser · NodeSources · NodeRanking · NodeHealth     │
│  SingBoxConfigBuilder · ClashApiClient · JsonFileStore   │
│  TlsFragmentForwarder · TunBypassService(логика)         │
└───────────────────────┬─────────────────────────────────┘
                        │ интерфейсы платформы
┌───────────────────────▼─────────────────────────────────┐
│ VieNLink.Android (net10.0-android)                      │
│  AndroidTunnelService : VpnService                      │
│  LibBoxPlatform : PlatformInterface (JNI)               │
│  KeystoreSecretStore : ISecretStore                     │
│  BootReceiver · QsTileService · NodeNotification        │
│  AppPicker (PackageManager) · DeepLinkImporter          │
└─────────────────────────────────────────────────────────┘
```

### Интерфейсы платформы (в Core)

```csharp
interface ISecretStore      { byte[]? Read(string name); void Write(string name, byte[] data); }
interface IAppPaths         { string DataDir { get; } string CoreDir { get; } }
interface ITunnelHost       { Task StartAsync(TunnelPlan plan, CancellationToken ct);
                              Task StopAsync(); TunnelState State { get; } }
interface ICoreRuntime      { Task<ICoreInstance> StartAsync(string configJson, bool withTun);
                              Task StopAsync(); }
interface INetworkMonitor   { event Action? Changed; bool IsMetered { get; } }
interface IAppCatalog       { Task<IReadOnlyList<AppEntry>> ListAsync(); }
```

`TunnelPlan` — то, что UI выбирает, а платформа исполняет: узел или цепочка, режим
(весь трафик / только домены / только приложения), список исключений, TUN-обход вкл/выкл.

---

## 6. Аналоги компонентов Windows → Android

| Windows | Android | Как именно |
|---|---|---|
| `SystemProxy` + `InternetSetOption` | `VpnService.Builder` | `addAddress`, `addRoute("0.0.0.0/0")`, `addDnsServer`, `setBlocking` |
| `SingBoxRunner` + job-объект | `libbox.BoxInstance` | старт в процессе; остановка в `onDestroy`/`onRevoke`; `ParcelFileDescriptor` из `openTun` |
| `TrayIconService` | `Notification` (foreground) + `TileService` | постоянное уведомление со статусом; плитка «вкл/выкл» |
| `ZapretWidget` | плитка + уведомление с быстрым выбором | свернутый вид = текст уведомления |
| `AutostartService` | `BootReceiver` | `RECEIVE_BOOT_COMPLETED` → `startForegroundService` |
| `SecretStore` (DPAPI) | Android Keystore | ключ AES-GCM в Keystore, шифрование файла; ключ не покидает TEE |
| `ProcessPickerWindow` | `PackageManager` | `getInstalledApplications`; фильтр интернет-приложений; `QUERY_ALL_PACKAGES` |
| «Исключить VieNLink.exe из TUN» | `addDisallowedApplication(packageName)` | наш процесс и форвардер ходят мимо VPN — без правил `process_name` |
| `WindowsIntegration.EnableStoreAppLoopback` | не нужно | на Android TUN виден всем приложениям нативно |
| `ExitInfoService` (IP/страна) | тот же код | запрос через локальный mixed-порт |
| Один экземпляр (`Mutex`) | один `VpnService` | ОС сама гарантирует единственность VPN |
| «Правила по доменам» | то же | `domain_suffix` в конфиге sing-box |

---

## 7. Какие методы обхода доступны на Android

| Метод | Работает | Как реализуем | Ограничение |
|---|---|---|---|
| Узел VLESS/REALITY/WS/gRPC | да | libbox | — |
| Hysteria2 / TUIC (QUIC) | да | libbox | узлы на популярных ASN банятся по IP |
| `tls.fragment` / `record_fragment` | да | поля конфига | только путь «через узел» |
| Цепочка «мост → выход» | да | `detour` в конфиге | нужен VPS в РФ |
| CDN-индирекция (ws/httpupgrade за CF) | да | конфиг | нужен домен за CF |
| TUN-обход без узла | **да** | libbox TUN → наш `TlsFragmentForwarder` | только TCP; UDP режем правилом |
| Блокировка QUIC | да | правило `reject` для UDP/443 | без root, но политикой, а не фейком |
| DNS поверх туннеля / DoH | да | DNS-блок конфига | — |
| Private DNS (DoT) системный | да | deep link в настройки | пользователь включает сам |
| MTProto-прокси | **лучше, чем на Windows** | `tg://proxy?server=&port=&secret=` | применяется в Telegram |
| Per-app split | да | `VpnService.Builder` | `QUERY_ALL_PACKAGES` вне Play |
| Kill switch | да | Always-on + «блокировать без VPN» | настройка ОС, не наша |
| Fake/TTL/badseq/QUIC-фейк | **нет** | — | нет raw-сокетов |
| Root-режим (iptables + raw) | теоретически | вне продукта | не поддерживаем |
| DNS-туннель (dnstt) | да, но медленно | отдельный код | фаза 7/8, низкий приоритет |

---

## 8. Библиотеки и зависимости

| Зависимость | Зачем | Тип | Лицензия | Риск |
|---|---|---|---|---|
| `Microsoft.Maui.Controls` (workload `maui-android`) | UI, одна кодовая база с Windows-логикой | workload + NuGet | MIT | `DataGrid` нет → `CollectionView`; тяжелее Avalonia |
| **или** `Avalonia.Android` + `Avalonia.Controls.DataGrid` | UI, ближе к текущему XAML | NuGet | MIT | на Android менее обкатано |
| `libbox.aar` (sing-box, vendored в репозиторий) | ядро: TUN, протоколы, Clash API | AAR, **не NuGet** | **GPL-3.0** | лицензия (см. §4); API меняется между версиями |
| **или** `libXray.aar` + tun2socks | ядро без GPL, с xhttp | AAR | MPL-2.0 | нет TUN и Clash API — пишем сами |
| `Mono.Android` (в составе .NET) | `VpnService`, `PackageManager`, `TileService`, `Notification`, `Keystore` | платформа | MIT | — |
| `AndroidX.Core` (транзитивно от MAUI) | `NotificationCompat`, совместимость | NuGet | Apache-2.0 | тянется автоматически |
| `ZXing.Net.Maui` (опционально) | QR для импорта/экспорта подписки | NuGet | MIT | можно обойтись share-intent и буфером |

**Сознательно не берём:**

- `CommunityToolkit.Mvvm` — хватает обычных классов;
- `SQLite`/Room — наш `JsonFileStore` достаточен для кэша узлов;
- `WorkManager` — для периодической проверки хватает `JobScheduler` из платформы;
- `Retrofit`/`OkHttp` — есть `HttpClient`.

**Правило:** всё, что можно сделать платформенным API из `Mono.Android`, делаем без NuGet.

---

## 9. Что придётся написать

| Компонент | Ответственность | Дни |
|---|---|---|
| `AndroidTunnelService : VpnService` | жизненный цикл VPN, fd, уведомление, revoke, `setUnderlyingNetworks` | 4–5 |
| `LibBoxPlatform : PlatformInterface` | JNI-обвязка: `openTun`, `protect`, `localDns`, колбэки трафика | 4–6 |
| `CoreRuntime` | старт/стоп libbox, тестовый инстанс без TUN, чтение логов | 2–3 |
| `KeystoreSecretStore` | AES-GCM ключ в Keystore, файл `sources.dat` | 1–2 |
| `NetworkWatcher` | `NetworkCallback`, пересборка туннеля при смене сети, учёт metered | 2 |
| `BootReceiver` | автозапуск после загрузки | 0,5 |
| `QsTileService` | плитка «вкл/выкл» + состояние | 1 |
| `NodeNotification` | статус, скорость, быстрый выбор узла | 2 |
| `AppPicker` | список приложений для split-tunnel | 1 |
| `DeepLinkImporter` | `vienlink://sub?...`, «Поделиться ссылкой» → импорт | 1,5 |
| `ForwarderHost` | запуск нашего `TlsFragmentForwarder` внутри процесса, `protect` сокетов | 2–3 |
| UI: 6 экранов | «Подключение», «Узлы», «Источники», «Диагностика», «Настройки», «Журнал» | 15–25 |
| Тесты на устройствах, полировка, APK | — | 5–8 |

Итого при варианте D: **≈40–55 дней** после извлечения ядра.

---

## 10. Особенности, которых нет на Windows

1. **`protect()` обязателен.** Наш форвардер и HTTP-клиент должны ходить мимо TUN, иначе
   петля. Два уровня: `addDisallowedApplication(packageName)` и `protect(fd)` для сокетов.
   Аналог правила `process_name: VieNLink.exe → direct`, но обязательный, а не страховочный.
2. **Смена сети.** Wi-Fi ↔ LTE рвёт туннель: нужен `NetworkCallback` и пересборка с
   `setUnderlyingNetworks`. На Windows такого класса проблем нет.
3. **Doze.** Без foreground-сервиса и исключения из оптимизации батареи туннель умрёт
   через несколько минут. Плюс `START_STICKY` и восстановление после `onRevoke`.
4. **ABI и размер.** `arm64-v8a` + `armeabi-v7a` + `x86_64` в AAR → APK 30–50 МБ.
   Использовать `AndroidUseSharedRuntime=false`, `AndroidLinkMode=SdkOnly`, раздельные APK.
5. **IPv6.** У операторов он часто основной: нужны `addAddress`/`addRoute` для IPv6 или
   явное отключение, иначе утечки и «полуживой» туннель.
6. **JNI-обвязка.** Официального .NET-биндинга libbox нет: писать `Java.Lang.Object`
   наследников и колбэки вручную, следить за `Dispose` (иначе утечки и падения на 50 МиБ
   памяти процесса).
7. **UDP в TUN.** Наш форвардер — только TCP. UDP (QUIC) либо отдаём libbox, либо режем
   правилом; иначе тихая утечка мимо туннеля.

---

## 11. Экраны и функции

| Экран | Что делаем | Отличие от Windows |
|---|---|---|
| Подключение | узел/цепочка/режим, кнопка «Подключить» | нет вкладки «Свой обход DPI»; «Туннель» становится основным режимом |
| Подключение → TUN-обход | наша фрагментация без узла | тот же код, другой хост TUN |
| Подключение → Видео/Telegram | split по доменам через узел | + `tg://proxy` для MTProto |
| Узлы | таблица, объём, задержка, роль, ★ | `CollectionView` с виртуализацией; долгий тап → меню |
| Источники | список, добавить, обновить | + приём ссылки из «Поделиться» |
| Диагностика | профиль сети, порог заморозки, причины | новый экран, общий с Windows-версией |
| Настройки | те же, что на Windows, минус WinDivert | + «исключённые приложения», «Always-on VPN» |
| Журнал | тот же | + «поделиться журналом» |

**Что выкидываем:** «Свой обход DPI» (WinDivert), выбор процессов (заменяется списком
приложений), системный прокси (не существует), трей (заменяется плиткой).

---

## 12. Этапы

| Этап | Что | Дни | Критерий |
|---|---|---|---|
| 8.0 | Экспорт профиля для SFA/Exclave/Karing | 2–3 | профиль импортируется и открывает YouTube |
| 8.1 | `VieNLink.Core` + интерфейсы платформы | 6–8 | Windows-версия и `SelfTest` собираются на новом ядре |
| 8.2 | Spike: MAUI + libbox + VpnService + один экран | 3–5 | один узел открывает youtube.com в Chrome |
| 8.2a | Проверка лицензии libbox и решение A/B/C из §4 | 0,5 | запись в «Решениях» |
| 8.3 | `AndroidTunnelService`, `LibBoxPlatform`, `CoreRuntime`, Keystore | 10–13 | подключение и отключение без утечек, проверка узлов в процессе |
| 8.4 | UI: 6 экранов, плитка, уведомление, boot, deep link, split | 20–30 | полный цикл на трёх устройствах, три оператора |
| 8.5 | TUN-обход на Android (форвардер в процессе) | 2–3 | Cloudflare/YouTube через свой форвардер без узла |

---

## 13. Риски

| Риск | Вероятность | Что делаем |
|---|---|---|
| GPL-3.0 у libbox несовместим с закрытым кодом | высокая | решение до 8.2; вариант B (MPL) |
| JNI-обвязка libbox окажется нестабильной | средняя | spike 8.2 это проверяет до вложений в UI |
| MAUI даст плохой UX с таблицей на 6 000 строк | средняя | виртуализация + постраничная подгрузка; запасной вариант — Avalonia |
| Doze/агрессивные прошивки убивают сервис | высокая | foreground service + исключение из оптимизации + инструкция |
| Политика Play по `QUERY_ALL_PACKAGES` и VPN | средняя | распространение APK сайдлоадом |
| Play Protect предупреждает, что APK «может быть вредоносным» | высокая (сайдлоад + VPN + `REQUEST_INSTALL_PACKAGES`) | инструкция в README: «Подробнее → Всё равно установить», временно отключить проверку Play Protect; в перспективе — RuStore/Google Play/F-Droid для репутации |
| Две UI-кодовые базы навсегда | высокая | принять осознанно; максимум логики — в Core |

---

## 14. Решения по вопросам

1. **Распространение и лицензия.** **Решено: публичный репозиторий + GPL-3.0** (см. §4).
   libbox остаётся, Android-клиент открыт, Windows-часть тоже под GPL-3.0.
2. **UI-фреймворк.** Открыто. Предпочтение — **MAUI** (официальная поддержка, меньше
   сюрпризов с `net10.0-android`), при проблемах с таблицей на 6 000 строк — Avalonia.
   Решается на spike 8.2.
3. **Объём.** Сначала **8.0 (экспорт профиля)**, чтобы телефон заработал через SFA за 2–3 дня.
   Полный клиент — после Windows-фаз 1–3.

### Что упрощается из-за личного использования

| Обычно нужно | Нам нужно | Экономия |
|---|---|---|
| Google Play, политика `QUERY_ALL_PACKAGES` | сайдлоад APK | нет |
| Онбординг, локализация, справка | без этого | 5–7 дней |
| Обновления APK (в сторе) | «поделиться файлом» | нет |
| QR-коды (ZXing) | share-intent + буфер обмена | 1 день, минус зависимость |
| Матрица тестов: 10 устройств × 5 операторов | 2–3 устройства × 2 оператора | 3–5 дней |
| Телеметрия, крашлитика | свой журнал + «поделиться журналом» | нет |

Итог: при варианте D полный клиент — **≈30–40 дней** вместо 40–55.

### Что остаётся обязательным даже для личного использования

- `protect()` и `addDisallowedApplication` — иначе петля в собственном TUN;
- foreground service + исключение из оптимизации батареи — иначе туннель умирает;
- смена сети Wi-Fi ↔ LTE — иначе «молча не работает» после выхода из дома;
- Keystore для `sources.dat` — токены подписок нельзя хранить открытым текстом.

### Почему телефон всё равно не заменяет Windows-версию

На мобильной сети белые списки работают **по IP-префиксам**, поэтому фрагментация TLS там
не помогает — против них нужен узел или цепочка, и с этим справляются существующие клиенты.
Уникальная ценность нашего Android-приложения — **один интерфейс, свой пул узлов, рейтинг,
цепочки и TUN-обход на домашнем Wi-Fi**. Это удобство, а не новая возможность; поэтому
Android идёт после Windows.

---

## 15. Сборка на этой машине (21.11.2026)

Toolchain поставлен **без прав администратора**, всё в профиль пользователя:
workload ставится в каталог SDK, а `C:\Program Files\dotnet` требует UAC.

**Проверено 21.11.2026:** APK собирается — `com.vienlink.app-Signed.apk`, 6,2 МБ.

**Грабли при установке:**
- Принять лицензии через `... | sdkmanager --licenses` **не работает**: PowerShell не доносит
  stdin до `.bat`. Нужно через `cmd` с файлом: `cmd /c ""sdkmanager.bat" --sdk_root=... --licenses < yes.txt""`.
- Без принятых лицензий `sdkmanager` пишет «Skipping following packages as the license is not
  accepted» и молча ничего не ставит.
- `VpnService.Builder` — вложенный класс: в наследнике `VpnService` создаётся как
  `new Builder(this)`, а не `new Builder()`.
- Нужен `using Android.Net;` иначе `VpnService.Prepare` не находится.

| Компонент | Путь |
|---|---|
| .NET SDK 11 + workload `android` | `%LOCALAPPDATA%\dotnet\dotnet.exe` |
| JDK 17 (Adoptium) | `%LOCALAPPDATA%\jdk17` |
| Android SDK (platform-tools, platforms;android-36, build-tools;36.0.0) | `%LOCALAPPDATA%\android-sdk` |
| Скрипт установки и лог | `%TEMP%\opencode\android-setup.ps1`, `android-setup.log` |

Сборка APK:

```powershell
$dotnet = "$env:LOCALAPPDATA\dotnet\dotnet.exe"
& $dotnet build VieNLink.Android\VieNLink.Android.csproj -c Debug `
    -p:AndroidSdkDirectory="$env:LOCALAPPDATA\android-sdk" `
    -p:JavaSdkDirectory="$env:LOCALAPPDATA\jdk17"
```

Установка на устройство (включённая отладка по USB):

```powershell
& "$env:LOCALAPPDATA\android-sdk\platform-tools\adb.exe" install -r `
    VieNLink.Android\bin\Debug\net10.0-android\com.vienlink.app-Signed.apk
```

**Важно:** обычный `dotnet build VieNLink.slnx` системным SDK не соберёт Android —
нужен локальный SDK с workload и явные пути `AndroidSdkDirectory`/`JavaSdkDirectory`.
Windows-проекты при этом собираются и системным SDK.

### Магазинная сборка (RuStore / Google Play)

Play и RuStore запрещают приложению ставить обновления самостоятельно, поэтому сборок две:

- `build\publish-android.ps1` — GitHub-APK с автообновлением и `REQUEST_INSTALL_PACKAGES`
  (overlay `Store/SideloadManifest.xml`);
- `build\publish-store.ps1` — `StoreBuild=true`: APK для RuStore и AAB для Play, без
  `REQUEST_INSTALL_PACKAGES` и без проверки обновлений (`StoreBuildInfo.IsStoreBuild`).

```powershell
powershell -File build\publish-android.ps1   # GitHub: release\VieNLink-<версия>-android.apk
powershell -File build\publish-store.ps1     # Магазины: *-store.apk и *-store.aab
```

Тексты карточки, Data safety и VPN-декларация — `docs/store-listing.md`; требования
кабинетов и шаги публикации — `docs/store-publication.md`; политика конфиденциальности —
`docs/privacy.md`; иконка и скриншоты — `store/`.

### Ядро libbox.aar — собрано 21.11.2026

`VieNLink.Android\Libs\libbox.aar` — **35,4 МБ**, arm64-v8a, из исходников `SagerNet/sing-box`
тега `v1.14.4`. Команда (без прав администратора):

```powershell
$go = "$env:LOCALAPPDATA\go-sdk\bin\go.exe"
$gobin = "$env:USERPROFILE\go\bin"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\android-sdk"
$env:ANDROID_NDK_HOME = "$env:LOCALAPPDATA\android-ndk"
$env:JAVA_HOME = "$env:LOCALAPPDATA\jdk17"
$env:CGO_ENABLED = 1
$env:PATH = "$($go | Split-Path);$gobin;$env:JAVA_HOME\bin;" + $env:PATH
Push-Location "$env:TEMP\opencode\sing-box-v1.14.4"
& "$gobin\gomobile.exe" bind "-target=android/arm64" -androidapi 26 `
  "-tags=with_gvisor,with_quic,with_utls,with_clash_api,with_wireguard" `
  "-ldflags=-checklinkname=0" -o libbox.aar ./experimental/libbox
```

**Грабли сборки (все проверены на практике):**

| Симптом | Причина и лечение |
|---|---|
| `gomobile bind requires golang.org/x/mobile in the current module` | Go 1.24+: выполнить `go get -tool golang.org/x/mobile/cmd/gobind` в каталоге sing-box |
| `this command requires the Android SDK ... Android SDK was not found at ...\Android\sdk` | задать `ANDROID_HOME` (gomobile ищет путь по умолчанию) |
| `link: invalid reference to os.checkPidfdOnce` | Go 1.23+ запрещает `//go:linkname`: добавить `-ldflags=-checklinkname=0` |
| `javac: executable file not found in %PATH%` | добавить `%LOCALAPPDATA%\jdk17\bin` в `PATH` (одного `JAVA_HOME` мало) |
| сборка падает на `armeabi-v7a` | собирать только нужные ABI: `-target=android/arm64` |
| `XA0141: 16 КБ страницы` | предупреждение Android 16 о выравнивании `libgojni.so`; на realme 16 Pro загружается, но при проблемах пересобрать NDK с `-Wl,-z,max-page-size=16384` |

### 32-битный ARM (телевизор Xiaomi, 23.11.2026)

MiTV-MOOQ1 (`tarzan`, Android 11) — **только `armeabi-v7a`**: `ro.product.cpu.abilist`
без 64-битных, `/system/lib64` пуст. APK с `arm64-v8a` на него не ставится
(`INSTALL_FAILED_NO_MATCHING_ABIS`), а в `libbox.aar` 32-битной `libgojni.so` нет.

Готовый `libbox.so` из официального SFA не подходит: он экспортирует
`Java_io_nekohasekai_libbox_*`, а наши биндинги — `libbox.*` (`Java_libbox_*`).
Поэтому 32-битную библиотеку собираем сами той же командой, меняя только `-target`:

```powershell
$go = "$env:LOCALAPPDATA\go-sdk\bin"
$gobin = "$env:USERPROFILE\go\bin"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\android-sdk"
$env:ANDROID_NDK_HOME = "$env:LOCALAPPDATA\android-ndk"
$env:JAVA_HOME = "$env:LOCALAPPDATA\jdk17"
$env:CGO_ENABLED = 1
$env:PATH = "$go;$gobin;$env:JAVA_HOME\bin;" + $env:PATH
Push-Location "$env:TEMP\sb-src\sing-box-1.14.4"   # исходники тега v1.14.4
& "$gobin\gomobile.exe" bind "-target=android/arm" -androidapi 26 `
  "-tags=with_gvisor,with_quic,with_utls,with_clash_api,with_wireguard" `
  "-ldflags=-checklinkname=0" -o libbox-arm32.aar ./experimental/libbox
```

Из полученного AAR берём `jni/armeabi-v7a/libgojni.so` → `VieNLink.Android\Libs\libgojni-armeabi-v7a.so`
(в репозиторий кладём отдельным файлом: собранный AAR с двумя ABI весит под 200 МБ).
`build\publish-android.ps1 -Abi armeabi-v7a` собирает APK с `RuntimeIdentifier=android-arm`
и добавляет эту библиотеку в `lib/armeabi-v7a/libgojni.so` перед `zipalign`/подписью.

Проверка совместимости биндингов: в 32-битной `.so` должны быть символы `Java_libbox_*`
(как в `classes.jar` нашего AAR), а не `Java_io_nekohasekai_*`.

### API libbox 1.14 (важно: отличается от старых примеров)

В версии 1.14 **нет** класса `libbox.BoxInstance`. Точки входа, которые реально
сгенерированы в биндингах:

- `Libbox.Libbox` — статические методы (`CheckConfig`, `AvailablePort`, …);
- `Libbox.CommandServer` — `StartOrReloadService(configJson, overrideOptions)`;
- `Libbox.IPlatformInterface` — реализует приложение: `OpenTun(ITunOptions)` возвращает
  `ParcelFileDescriptor` для TUN, плюс `AutoDetectInterfaceControl`/`StartDefaultInterfaceMonitor`;
- `Libbox.SetupOptions`, `Libbox.TunOptions` — параметры.

То есть правильный путь: реализовать `IPlatformInterface`, создать `CommandServer`
с обработчиком, вызвать `StartOrReloadService` с нашим конфигом sing-box (там же TUN-инбаунд),
а fd отдавать из `VpnService.Builder.Establish()`.

**Проверено на устройстве (realme 16 Pro 5G, Android 16, RMX5120):** APK 75,8 МБ
устанавливается, приложение запускается, показывает «Ядро: libbox найден», а `VpnService`
поднимает TUN (`fd=182`) без краша.

### Состояние spike: РАБОТАЕТ через узел (21.11.2026)

Проверено на realme 16 Pro 5G (Android 16) с реальным узлом из открытого списка:

| Проверка | Результат |
|---|---|
| `icanhazip.com` в Яндекс Браузере | **172.236.227.60** — IP узла, а не домашний |
| Лог ядра | `inbound/tun[tun-in]: inbound connection to …:443` → `outbound/vless[…]: outbound connection to …:443` |
| YouTube | страница, обложки, реклама, категории — грузится полностью |

**Корневая причина, из-за которой приложения ходили мимо узла:** в `VpnService.Builder`
не добавлялся маршрут по умолчанию. libbox передаёт итератор маршрутов, который может быть
**непустым по ссылке, но пустым по содержимому** — и тогда мы не добавляли ни одного
маршрута. Симптом обманчив: DNS работает (Android маршрутизирует DNS через VPN отдельно),
страницы открываются, но с домашним IP, а в логе ядра нет ни одного `:443`.

Лечение — считать добавленные маршруты и при нуле добавлять `0.0.0.0/0`:

```csharp
var routes = AddPrefixes(builder, options.Inet4RouteAddress, asRoute: true);
if (routes == 0) builder.AddRoute("0.0.0.0", 0);
```

**Как отличать подобное дальше:** проверять узел в обход TUN через собственный mixed-порт
приложения (`nc 127.0.0.1:20800`) — если там IP узла, а в браузере домашний, значит проблема
в маршрутах, а не в узле. В нашем случае mixed-порт сразу показал `172.236.227.60`.

### Экран узлов и источников (8.4, 21.11.2026)

Классы Android-клиента:

| Файл | Ответственность |
|---|---|
| `CoreRuntime.cs` | один `Libbox.Setup` и один `CommandServer` на процесс, теги загруженных узлов |
| `NodeStore.cs` | источники (`sources.dat` в Keystore), кэш узлов, слияние, выбранный узел |
| `NodeTester.cs` | задержка через Clash API 20801 по шести адресам (Cloudflare, YouTube, Telegram, Discord); при туннеле — без перезапуска ядра |
| `NodesActivity.cs` | сводка узлов без списка: обновление, проверка, подключение лучшего |
| `SourcesActivity.cs` | добавление подписки/ссылки, включение, удаление, обновление |
| `SettingsActivity.cs` | автозапуск, split-tunnel, переход к выбору приложений |
| `AppPickerActivity.cs` | список приложений с фильтром, сохранение пакетов |
| `DiagnosticsActivity.cs` | состояние, ядро, списки, проверка выхода через mixed-порт |
| `LogActivity.cs` | журнал приложения и stderr ядра, обновление и отправка |
| `ForwarderHost.cs` | форвардер TUN-обхода внутри процесса |
| `BypassActivity.cs` | выбор стратегии и запуск TUN-обхода (по умолчанию «Разрез TLS-записей») |
| `AndroidSettings.cs` | `android-settings.json`: автозапуск, split-tunnel, пакеты |
| `BootReceiver.cs` | автозапуск после `BOOT_COMPLETED` → `ActionAuto` |
| `TunnelTileService.cs` | плитка быстрых настроек: вкл/выкл без открытия приложения |
| `VpnStarter.cs` | разрешение VPN, foreground-запуск и остановка службы |
| `SettingsStore.cs` | `settings.json` общего формата |
| `UiMetrics.cs` | отступы строки состояния и навигации для темы без ActionBar |

**Узлы в интерфейсе скрыты (21.11.2026):** экран «Узлы» показывает только сводку —
сколько всего, рабочих, доступ по сервисам, лимит и время проверки; на главном экране
четыре кнопки быстрого подключения («YouTube», «Telegram», «Discord», «Универсальный»).
Узел выбирается скрыто по доступу и задержке (`NodeStore.PickForService`), имена узлов
не показываются ни в списке, ни в уведомлении. Лимит — 12 000 узлов, хранение — 7 дней.

**Проверено на устройстве:**

| Проверка | Результат |
|---|---|
| Импорт источников | 15 источников → 8 276 узлов, `sources.dat` (5,8 КБ) |
| Проверка задержки | 150 узлов → 2 рабочих (0,2–3 % — норма для открытых списков) |
| Подключение к выбранному узлу | `icanhazip.com` через mixed-порт: `172.236.227.60` |
| Переключение узла | Clash selector → `47.84.226.221`; новых `TUN поднят` в логе нет |
| Добавление подписки через диалог | 16 источников, импортировано 62 узла |
| Выбор приложений (split-tunnel) | 90 приложений в списке, Chrome сохранён в `android-settings.json` |
| Подключение в split-режиме | лог `split-tunnel: приложений 1`, TUN поднят, IP узла через mixed-порт |
| Автоподключение службы (`ActionAuto`) | конфиг собран службой, TUN поднят без передачи конфига в intent |
| Deep link `vienlink://sub?url=…` | источник добавлен, `example.net` → «ошибка — HTTP 404» без падения |
| Повторный запуск конфига | перезагрузка на одном `CommandServer`, без `cache-file: timeout` и ANR |
| Плитка и `BOOT_COMPLETED` | с adb не проверяются: broadcast защищённый, ColorOS игнорирует `cmd statusbar add-tile`; плитку добавляют вручную |
| Уведомление туннеля | показывает узел и задержку, действия «Узлы»/«Отключить»; отключение проверено |
| Журнал и диагностика | хвост журнала/stderr открывается; «Проверить выход» даёт IP узла против прямого IP |
| TUN-обход (бета) | «Середина SNI» (`tlsrec`) и OOB (`MSG_OOB`): страница `m.youtube.com` открывается без узла на Wi-Fi; варианты «перед/после SNI» и «пополам» на этом DPI не работают; видео `googlevideo` — IP-блок. На Tele2 (LTE) даже страница не открывается: Google/Cloudflare блокируются по IP/ASN |

**Грабли, которые уже стоили времени:**

| Симптом | Причина и лечение |
|---|---|
| Заголовок и кнопки уезжают под ActionBar | тема `Theme.Material.Light` + программный UI: контент не получает inset. Взять `.NoActionBar` и добавить отступы `status_bar_height`/`navigation_bar_height` |
| `ArrayAdapter` не показывает строки | адаптер создан с пустым списком. Пересоздавать адаптер на каждую перезагрузку или использовать `BaseAdapter` |
| `adb shell am start -n …NodesActivity` — «Activity does not exist» | Java-имя = `crc64701940d33722e542.NodesActivity`, а не C# namespace; смотреть `obj/…/android/AndroidManifest.xml` |
| Экран не открывается повторным `am start` | у активности нет `singleTop`, интент уходит в существующую задачу. Для проверки — `am force-stop` и запуск заново |
| `-e openNodes true` не срабатывает | `-e` кладёт строку; для bool нужен `--ez` |
| Список узлов пуст после установки | кэш читается с отсечкой по `KeepUnseenNodeDays`; первый запуск всегда требует «Обновить» |
| В выборе приложений только 5–10 системных | Android 11+ скрывает пакеты без `<queries>`; добавить intent `MAIN`/`LAUNCHER` в манифест |
| `start or reload service: initialize cache-file: timeout`, затем ANR | в процессе создавался второй `CommandServer`. Держать один сервер и перезагружать конфиг через `StartOrReloadService` |
| TUN остаётся после неудачного старта | при исключении закрывать `_tunDescriptor`, снимать foreground и `StopSelf` |
| `am broadcast -a BOOT_COMPLETED` → SecurityException | broadcast защищённый; автозапуск проверять реальной перезагрузкой либо отладочной командой `--ez autoConnect true` |
| `cmd statusbar add-tile` молча ничего не делает | прошивка (ColorOS) игнорирует команду; плитка добавляется вручную в редакторе шторки |
| Уведомление службы не видно, хотя `isForeground=true` | Android 13+ требует `POST_NOTIFICATIONS`; запрашивать разрешение при запуске и перезапускать туннель после выдачи |
| Действие уведомления не срабатывает | `PendingIntent` для `VpnService` — через `GetService` с `FLAG_IMMUTABLE`; `GetActivity` только для экранов |
| Разрез TCP-сегментов не помогает | DPI склеивает TCP и видит ClientHello целиком. Нужен разрез на уровне TLS-записей (аналог `tlsrec` в ByeDPI) — `RecordSplit` |
| Включилась не та стратегия | Активность передавала индекс своего списка, а служба брала индекс Core. Передавать имя режима (`StreamFragmentMode`) |
| DNS `1.1.1.1`/`8.8.8.8` отдаёт NXDOMAIN | ТСПУ перехватывает открытый UDP/53. В TUN-обходе Android по умолчанию Яндекс-DNS `77.88.8.8`; при блокировке IP это всё равно не спасёт |

**Что ещё обязательно для Android (сверх Windows-версии):**

| Симптом | Причина |
|---|---|
| паника Go при первом DNS-пакете | `FindConnectionOwner` вернул `null` — ядро разыменовывает результат |
| страницы не открываются при рабочем узле | локальный UDP/53 зарезан: нужен DNS через узел (`dnsThroughProxy: true`, `type: tcp` + `detour: proxy`) |
| DNS-запросы уходят в собственный TUN | сокет DNS обязан вызывать `protect(fd)` — иначе цикл |
| приложения ходят мимо туннеля | нет маршрута `0.0.0.0/0` (см. выше) |
| пустой лог ядра | `SetupOptions.Debug = false`; при `true` сообщения идут в `WriteDebugMessage` |
| падение при создании сервера | не вызван `Libbox.Setup`, передан `null` вместо `OverrideOptions` или `ICommandServerHandler` |

### Грабли Android-сборки приложения

| Симптом | Причина и лечение |
|---|---|
| `No assemblies found in .../.__override__` при запуске | Debug собирается с Fast Deployment: добавить `<EmbedAssembliesIntoApk>true</EmbedAssembliesIntoApk>` |
| `MissingForegroundServiceTypeException` | Android 14+: для `startForeground` нужен объявленный тип службы (`specialUse` + свойство в манифесте); пока уведомление отключено |
| `VpnService.Builder.Builder(VpnService?)` | `Builder` — вложенный класс: `new Builder(this)` |
| `VpnService.Prepare` не найден | нужен `using Android.Net;` |
| `Cipher.Init(int, IKey)` не компилируется | биндинги ждут `CipherMode` (enum) и `Java.Security.Spec.IAlgorithmParameterSpec` |
