# Передача контекста (handoff)

Обновлено: 22.11.2026. Читать в порядке: `AGENTS.md` → этот файл → `docs/plan-2026.md`
(журнал и архив решений). Перед передачей сессии — обновить этот файл.

## Где проект

- Репозиторий: `C:\MyProg\MyrrProxy`, ветка `main`; GitHub:
  https://github.com/VieNchikkk/VieNLink (публичный, GPL-3.0).
- Телефон: realme 16 Pro 5G (Android 16, RMX5120), `adb` по USB, оператор Tele2.
  Серийный номер и SSID в публичные файлы не писать.
- `adb`: `%LOCALAPPDATA%\android-sdk\platform-tools\adb.exe`; сборка Android —
  `%LOCALAPPDATA%\dotnet\dotnet.exe` (в системном SDK нет workload android).
- Ключ подписи Android: `%LOCALAPPDATA%\VieNLink\signing` (`vienlink-android.jks` +
  `android-signing.pass`) — не терять, обновление ставится только поверх той же подписи.

## Состояние на 22.11.2026

**Политика публикации (23.11.2026):** публичная версия — **1.3.22**, её assets не обновляем
задним числом. Сначала копим и исправляем ошибки локально; когда наберётся значимый
набор правок — поднимаем версию и выпускаем патч. `build/Version.props` без команды
владельца не поднимаем. В GitHub остаются релизы 1.3.7–1.3.22 (`v1.3.7-beta`,
`v1.3.7-android`, `v1.3.8-beta`, `v1.3.8-android`, `v1.3.9-beta`, `v1.3.9-android`,
`v1.3.10-beta`, `v1.3.10-android`, `v1.3.11-beta`, `v1.3.11-android`,
`v1.3.12-beta`, `v1.3.12-android`, `v1.3.13-beta`, `v1.3.13-android`,
`v1.3.14-beta`, `v1.3.14-android`, `v1.3.15-beta`, `v1.3.15-android`,
`v1.3.16-beta`, `v1.3.16-android`, `v1.3.17-beta`, `v1.3.17-android`,
`v1.3.18-beta`, `v1.3.18-android`, `v1.3.19-beta`, `v1.3.19-android`,
`v1.3.20-beta`, `v1.3.20-android`, `v1.3.21-beta`, `v1.3.21-android`,
`v1.3.22-beta`, `v1.3.22-android`).

**Что вошло в 1.3.8–1.3.9 (ранее локальные правки):**
- Android, причина зависаний: UI ждал `NodeStore.Gate`, пока фон разбирал кэш на 12 000
  серверов (системный ANR «Input dispatching timed out» на старте и при нажатиях).
  Теперь список публикуется неизменяемым снимком (`NodeStore._snapshot`), счётчики,
  выбранный сервер и сводка читаются без замка; тяжёлая работа — только в фоне.
- Android, причина потери рабочих серверов: `:probe` — отдельный процесс, он разбирал
  ссылки в новые объекты и не знал прежний `Status`/`FailCount`, поэтому рабочий сервер
  после **одного** провала помечался мёртвым и удалялся. Теперь история едет в запросе
  (`ProbeRequestNode.Status/FailCount`), правило «2 провала подряд» работает и на Android.
  Плюс при загрузке кэша рабочий сервер с провалом (schema ≥ 3, `FailCount ≥ 1`) не
  выбрасывается как «мусор старого кэша».
- Android: рабочее и непроверенное хранятся отдельно — `nodes-working.json` (маленький,
  пишется после каждой проверки) и `nodes.json` (куча, не чаще раза в минуту/5 минут).
- Android: на запуске сначала грузятся рабочие из своего файла и сразу перепроверяются
  (`Старт: рабочие перепроверены — N, живых M`), только потом поиск новых.
- Рабочий сервер переживает один провал и сохраняет результат последней удачной проверки;
  удаляется после 2 провалов подряд, непроверенный — после первой неудачной. Счётчики в шапке считают
  по одной базе (без учёта свежести), поэтому общее число и значки сервисов сходятся
  по смыслу: сумма значков больше общего числа, т.к. один сервер открывает несколько сервисов.
- Android: в шапке отдельный значок «рабочих серверов» — число берётся из списка рабочих
  (`nodes-working.json`), туда же сразу попадают найденные при проверке; рядом
  «+прибавилось −убыло». Во время ручного прогона фоновая проверка не запускается,
  кнопка «Остановить поиск» относится только к ручному прогону.
- Рабочие серверы проверяются мягче (обе платформы): первый провал не стирает результат,
  вторая попытка в том же проходе, TCP-отсев не наказывает рабочие; удаление — после 2 провалов.
- Clash API: таймаут 10 с (было 30) и свой предел на запрос задержки — проверка не замирает.
- Windows: то же разделение через `VieNLink\NodeCacheWriter.cs`; на ПК правило
  «2 провала» работало и раньше (`HiddenProbeService` + `ProxyManager` меняют настоящие
  объекты), общий для платформ остался только фильтр загрузки кэша.
- Windows, обновление приложения: в «Общих» появились кнопка «Проверить обновление»,
  строка статуса и кнопка «Скачать и установить» → «Запустить установщик». Автопроверка
  при запуске ведёт к тем же шагам: процент скачивания виден в «Общих» и в строке
  состояния, установщик запускается обычным окном (`/CLOSEAPPLICATIONS /NORESTART`),
  а не `/SILENT`, и после установки предлагает запустить новую версию.
- Статистика источников: в списке источников ПК колонки «Всего» (уникальные серверы
  за всё время, `TotalNodeCount` + `RegisteredNodeIds`) и «Рабочих» (текущие), на Android —
  `всего: N · рабочих: M`. Повторный сбор не раздувает «Всего», старый кэш заполняет его
  при запуске (`NodeSourceStats.SeedTotals`). По нулю рабочих видно, какой источник
  можно удалить.
- Автоматика: в настройках ПК раздел «Автоматические работы» — отдельные переключатели
  автопоиска источников, авто-сбора серверов, обновления подписок при запуске, MTProto,
  ядра sing-box и обновлений приложения; проверка серверов — рядом. На Android такие же
  переключатели в «Настройках»; `AutomationPaused` по-прежнему гасит всё сразу.
- Android: нижние вкладки «Подключение / Обход / Инструменты / Настройки». Журнал —
  плитка в «Инструментах» (`LogActivity`), четвёртая вкладка открывает `SettingsActivity`
  и после возврата показывает прежнюю вкладку.
- Android: одно нажатие «Проверить серверы» — 5 партий по 200 (до 1000 серверов),
  подпись «Партия N из 5»; рабочие проверяются отдельным проходом после поиска новых.
- Android: пока идёт ручной прогон, фоновая проверка партии не запускает (`IsManualRun`),
  иначе `:probe` переходил из рук в руки и счётчик замирал.
- Android: ожидание результата `:probe` прерывается, если счётчики прогресса не меняются
  100 с (раньше «живой» файл прогресса держал ожидание все 10 минут); второй запуск
  в том же процессе `:probe` игнорируется.
- Android: «Остановить поиск» реагирует сразу (снимает состояние, принудительно гасит
  `:probe`), повторное нажатие запускает новую партию, а не «ничего».
- Android: лимит списка 5000 (схема настроек 4; старый кэш подрезается при чтении),
  партия проверки 200, `OpenedFor` без `ToLowerInvariant` (меньше мусора для GC).
- Журнал Android дублируется в logcat: `adb logcat -s VieNLinkLog`.
- Замер на телефоне (VPN включён): PSS основного процесса 351 → **~150 МБ**, холодный
  старт ~0,9 с, новых ANR нет; `:probe` — отдельный процесс ~180–220 МБ на время партии.
  APK для проверки собран как `1.3.7`/`10308`
  (`release\VieNLink-1.3.7-android.apk`) — ставится поверх, в GitHub не выкладывать.

Выпущена **1.3.3** (Windows и Android, `versionCode` 10303):

- Windows: `release\VieNLink-1.3.3-setup.exe` (подпись `CN=VieN`) + `VieNLink-1.3.3-win-x64.zip`
  + `SHA256SUMS.txt` → https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.3-beta
- Android: `release\VieNLink-1.3.3-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.3-android
  (APK на телефоне установлен, проверено live: подключение, счётчики, «Поделиться» = `IP:порт`,
  «Добавить сервер» в шапке, остановка сбора/проверки MTProto).
- Магазинные артефакты (в RuStore/Play ещё не публикуем):
  `release\VieNLink-1.3.3-store.apk` и `-store.aab` с `.sha256`.

Опубликована **1.3.4** (`versionCode` 10304), релизы pre-release:

- Windows: `release\VieNLink-1.3.4-setup.exe` (подпись `CN=VieN`) + `VieNLink-1.3.4-win-x64.zip`
  + `SHA256SUMS.txt` → https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.4-beta
- Android: `release\VieNLink-1.3.4-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.4-android
  (APK установлен на телефон через `adb install -r`, `versionCode=10304`, `versionName=1.3.4`).
- Магазинные артефакты (в RuStore/Play ещё не публикуем):
  `release\VieNLink-1.3.4-store.apk`, `-store.aab` + `.sha256`.

Опубликована **1.3.5** (`versionCode` 10305), релизы pre-release:

- Windows: `release\VieNLink-1.3.5-setup.exe` (подпись `CN=VieN`) + `VieNLink-1.3.5-win-x64.zip`
  + `SHA256SUMS.txt` → https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.5-beta
- Android: `release\VieNLink-1.3.5-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.5-android
  (APK установлен на телефон, `versionCode=10305`, `versionName=1.3.5`).
- Магазинные артефакты: `release\VieNLink-1.3.5-store.apk`, `-store.aab` + `.sha256`.

Опубликована **1.3.6** (`versionCode` 10306), релизы pre-release:

- Windows: `release\VieNLink-1.3.6-setup.exe` (подпись `CN=VieN`) + `VieNLink-1.3.6-win-x64.zip`
  + `SHA256SUMS.txt` → https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.6-beta
- Android: `release\VieNLink-1.3.6-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.6-android
  (APK установлен на телефон, `versionCode=10306`, `versionName=1.3.6`).
- Магазинные артефакты: `release\VieNLink-1.3.6-store.apk`, `-store.aab` + `.sha256`.

Опубликована **1.3.7** (`versionCode` 10307), релизы pre-release:

- Windows: `release\VieNLink-1.3.7-setup.exe` (подпись `CN=VieN`) + `VieNLink-1.3.7-win-x64.zip`
  + `SHA256SUMS.txt` → https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.7-beta
- Android: `release\VieNLink-1.3.7-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.7-android
  (APK установлен, проверено live: имя сервера, IP:порт, задержка, выход `🇵🇱 144.31.0.101 · Польша`).
- Магазинные артефакты: `release\VieNLink-1.3.7-store.apk`, `-store.aab` + `.sha256`.
- Windows 1.3.7 установлен на ПК тихой установкой; при обновлении поверх 1.3.4
  был запущен VieNLink — перед установкой его надо закрыть (файлы блокируются).

Содержимое 1.3.7: Android — в шапке имя сервера, `IP:порт`, задержка, качество и выход
(флаг, IP, страна), уведомление/виджет обновляются; флаги-эмодзи из источников
(`\U0001F1EC...`) декодируются в нормальные символы на ПК и Android.

Опубликована **1.3.8** (`versionCode` 10308), релизы pre-release:

- Windows: `release\VieNLink-1.3.8-setup.exe` (подпись `CN=VieN`) + `VieNLink-1.3.8-win-x64.zip`
  + `SHA256SUMS.txt` → https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.8-beta
- Android: `release\VieNLink-1.3.8-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.8-android
- Магазинные артефакты: `release\VieNLink-1.3.8-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: `:probe` получает сеть; партия проверки 200; квота 5 на сервис; мягкая
  проверка рабочих (5 провалов); отдельный `nodes-working.json`; счётчики по одной базе.

Опубликована **1.3.9** (`versionCode` 10309), релизы pre-release:

- Windows: `release\VieNLink-1.3.9-setup.exe` (подпись `CN=VieN`, Authenticode — Valid)
  + `VieNLink-1.3.9-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.9-beta
- Android: `release\VieNLink-1.3.9-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.9-android
- Магазинные артефакты: `release\VieNLink-1.3.9-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: статистика источников «Всего / Рабочих»; отдельные переключатели автоматики
  на ПК и Android; ручная проверка и установка обновления на ПК; Android — журнал
  в «Инструментах», «Настройки» на месте четвёртой вкладки.
- APK на телефон **не устанавливался** в этой сессии: сначала проверить вкладки
  «Инструменты → Журнал» и «Настройки», затем `adb install -r` можно делать поверх 1.3.8.

Опубликована **1.3.10** (`versionCode` 10310), релизы pre-release:

- Windows: `release\VieNLink-1.3.10-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.10-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.10-beta
- Android: `release\VieNLink-1.3.10-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.10-android
- Магазинные артефакты: `release\VieNLink-1.3.10-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: в статистике источников добавлено «Рабочих всего» — уникальные серверы,
  которые хотя бы раз были рабочими (`TotalWorkingNodeCount` + `RegisteredWorkingNodeIds`).

Опубликована **1.3.11** (`versionCode` 10311), релизы pre-release:

- Windows: `release\VieNLink-1.3.11-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.11-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.11-beta
- Android: `release\VieNLink-1.3.11-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.11-android
- Магазинные артефакты: `release\VieNLink-1.3.11-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: автопоиск источников на GitHub берёт только репозитории, обновлённые
  за последние 24 часа; если даты публикации нет — не отсеивает.

Опубликована **1.3.12** (`versionCode` 10312), релизы pre-release:

- Windows: `release\VieNLink-1.3.12-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.12-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.12-beta
- Android: `release\VieNLink-1.3.12-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.12-android
- Магазинные артефакты: `release\VieNLink-1.3.12-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: фильтр свежести 24 ч теперь применяется и к **сбору серверов**
  (`NodeSourceService.CollectAsync`): проверяется дата последнего коммита файла, затем
  `pushed_at` репозитория; старый GitHub-источник пропускается, без даты/при недоступном
  API — собирается. Ручное «Обновить выбранный» не фильтруется.

Опубликована **1.3.13** (`versionCode` 10313), релизы pre-release:

- Windows: `release\VieNLink-1.3.13-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.13-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.13-beta
- Android: `release\VieNLink-1.3.13-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.13-android
- Магазинные артефакты: `release\VieNLink-1.3.13-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: при выключенном авто-сборе фоновый автопоиск добавляет только источники,
  серверы не подмешивает (`AddDiscovered(..., includeNodes)` / `DiscoverSourcesCoreAsync`);
  ручной автопоиск по-прежнему добавляет и серверы. Остановка авто-сбора не удаляет
  уже собранные серверы — они уходят после провалов проверки.

Опубликована **1.3.14** (`versionCode` 10314), релизы pre-release:

- Windows: `release\VieNLink-1.3.14-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.14-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.14-beta
- Android: `release\VieNLink-1.3.14-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.14-android
- Магазинные артефакты: `release\VieNLink-1.3.14-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: пересборка 1.3.13 без функциональных изменений; версия поднята для
  автообновления установленных копий.

Опубликована **1.3.15** (`versionCode` 10315), релизы pre-release:

- Windows: `release\VieNLink-1.3.15-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.15-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.15-beta
- Android: `release\VieNLink-1.3.15-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.15-android
- Магазинные артефакты: `release\VieNLink-1.3.15-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: `NodeLimits.RemoveAfterFailures = 2` — рабочий сервер переживает один
  провал, удаляется после второго; непроверенный тоже после двух. Тесты `SelfTest`
  обновлены под новый порог.

Опубликована **1.3.16** (`versionCode` 10316), релизы pre-release:

- Windows: `release\VieNLink-1.3.16-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.16-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.16-beta
- Android: `release\VieNLink-1.3.16-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.16-android
- Магазинные артефакты: `release\VieNLink-1.3.16-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: `NodeHealth.UnverifiedFailures = 1` — непроверенный сервер удаляется
  после первой неудачной проверки (включая TCP-отсев); рабочий по-прежнему после двух.

Опубликована **1.3.17** (`versionCode` 10317), релизы pre-release:

- Windows: `release\VieNLink-1.3.17-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.17-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.17-beta
- Android: `release\VieNLink-1.3.17-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.17-android
- Магазинные артефакты: `release\VieNLink-1.3.17-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: новые источники `free-nodes`, `0xRadikal`, `SoliSpirit`, `hamedcode`,
  `AutoAiVPN`, `V2ray-Collector`, `ConfigV2Ray`, `node-sub-zixf`, `zhuhaiuk`;
  `IsTrustedNow` и иконка ✓ при публикации за последние 24 ч; `Kwinshadow` удалён
  у существующих пользователей (`ObsoleteSourceUrls`). Источники без даты публикации
  удаляются, веб-автопоиск отключён; окно свежести настраивается
  (`SourceFreshnessHours`, по умолчанию 24 ч). Новая вкладка «Отсев — Перепроверка»:
  `nodes-rejected.json`, счётчики по сервисам, фильтр и кнопки подключения/возврата.
  Telegram/MTProto источники не изменялись.

Опубликована **1.3.18** (`versionCode` 10318), релизы pre-release:

- Windows: `release\VieNLink-1.3.18-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.18-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.18-beta
- Android: `release\VieNLink-1.3.18-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.18-android
- Магазинные артефакты: `release\VieNLink-1.3.18-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: Android-экран «Отсев — Перепроверка» (`RejectedActivity`) с тем же
  функционалом, что на ПК; `NodeTester.TestNodesAsync` для произвольного набора,
  `NodeStore.RestoreRejected`; источники и свежесть как в 1.3.17.

Опубликована **1.3.19** (`versionCode` 10319), релизы pre-release:

- Windows: `release\VieNLink-1.3.19-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.19-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.19-beta
- Android: `release\VieNLink-1.3.19-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.19-android
- Магазинные артефакты: `release\VieNLink-1.3.19-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: переработан экран отсева на Android (сводная карточка, метрики,
  горизонтальный фильтр, карточки с сервисами и действиями); функционал 1.3.17–1.3.18.

Опубликована **1.3.20** (`versionCode` 10320), релизы pre-release:

- Windows: `release\VieNLink-1.3.20-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.20-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.20-beta
- Android: `release\VieNLink-1.3.20-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.20-android
- Магазинные артефакты: `release\VieNLink-1.3.20-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: исправлено «Подключение» из отсева на Android — конфиг теперь с TUN
  (`TunnelMode.Tun`, DNS через сервер), а не SystemProxy; сервер убирается из отсева
  через 3 с только при состоянии VPN Running.

Опубликована **1.3.21** (`versionCode` 10321), релизы pre-release:

- Windows: `release\VieNLink-1.3.21-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.21-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.21-beta
- Android: `release\VieNLink-1.3.21-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.21-android
- Магазинные артефакты: `release\VieNLink-1.3.21-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: отсев хранится через `CachedNode` (исправлен краш «Нет серверов для
  запуска прокси» из-за `[JsonIgnore] OutboundJson`), старый формат 1.3.19 читается;
  подключение из отсева на Android собирает TUN-конфиг и возвращает сервер только
  после запуска VPN; новый вид экрана отсева.

Опубликована **1.3.22** (`versionCode` 10322), релизы pre-release:

- Windows: `release\VieNLink-1.3.22-setup.exe` (подпись `CN=VieN`)
  + `VieNLink-1.3.22-win-x64.zip` + `SHA256SUMS.txt` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.22-beta
- Android: `release\VieNLink-1.3.22-android.apk` + `.apk.sha256` →
  https://github.com/VieNchikkk/VieNLink/releases/tag/v1.3.22-android
- Магазинные артефакты: `release\VieNLink-1.3.22-store.apk`, `-store.aab` + `.sha256`.
- Содержимое: проверка через активный туннель при `SiteRouteMode.OnlyListed` идёт
  на первый сайт из списка, а не на Cloudflare/YouTube; провал health-check больше
  не меняет `Status`/`FailCount`; сервисное/ручное подключение на ПК не помечает
  сервер мёртвым.

Содержимое 1.3.6: Android — сторожевой таймер `:probe` (75 с без прогресса → процесс
завершается, следующий такт проверяет заново), партия уменьшена до 100, при сбое партии
серверы помечаются `TriedAtUtc`, чтобы не повторять тот же набор.

Содержимое 1.3.5: рабочие серверы не отбрасываются при загрузке кэша по возрасту источника
(`KeepUnseenNodeDays`), их судьбу решает только перепроверка — важно после обновления/простоя.

Содержимое 1.3.2–1.3.3: счётчики раз в минуту и перепроверка серверов каждые 5 минут с мгновенным
обновлением после запуска; «Поделиться» только `IP:порт`; кнопки сервисов действительно меняют
сервер; «Остановить/Запустить все процессы»; Android-шапка из трёх кнопок + «Добавить сервер»
строкой ниже; вкладка «Общие» удалена (4 вкладки); прогресс и отмена сбора/проверки MTProto;
защита от ANR и стабильность фона; зеркала обновлений; все источники и виджет включены.

1.3.4 (22.11.2026): проверка серверов во время VPN. Прямая проверка идёт мимо VPN
(скрытое ядро на ПК, `:probe` на Android). Исправлено: проверка через прокси — по универсальному
Cloudflare и открытому сервису, а не только по YouTube (иначе рабочий сервер удалялся после
5 подключений); провал health-check больше не понижает статус сервера; граница свежести в фоне
пересчитывается в каждом проходе. Переключение серверов (`NodeFailover`, ПК и Android): приоритет
выбранного сервиса, затем лучшие из других; сервис сохраняется при сбое и на «Сменить сервер»;
новый сервер выбранного сервиса встаёт первым, при работе на запасном — плавный возврат
(тёплое переключение без перезапуска ядра, если сервер уже в конфиге). Ускорена проверка
найденных серверов: TCP-отсев мёртвых `host:port` (`NodePrecheck`), выше параллельность,
перемешивание партии по источникам, фон-такт не дольше 6 минут. Android: убраны нереализованные
карточки «Обход Zapret» и «Свой обход DPI» (без root не работают, остаётся TUN-обход), экран
зафиксирован в портретной ориентации без изменения системного автоповорота. Сборки Windows/Android
и `SelfTest parsers/statuslogic` проходят.

## Следующие шаги

1. Проверить на телефоне: старт без зависаний, «Остановить поиск» на шапке и на экране
   «Серверы», память в простое и во время проверки (цель — PSS ≤ 150 МБ).
2. Тест Android на втором операторе (другая сеть/провайдер): подключение, автосмена сервера,
   MTProto.
3. Ручное добавление плитки «Туннель» на рабочий стол (сейчас только через меню виджетов).
4. Дальше улучшать обход DPI (Windows): качество стратегий, автоподбор под провайдера.
5. При следующем релизе: поднять версию в `build/Version.props`, добавить раздел в
   `CHANGELOG.md`, собрать Windows + GitHub APK **и магазинную сборку** (`publish-store.ps1`),
   обновить теги/релизы и этот файл.
6. Проверить вживую приоритет переключения: «Универсальный» → обрыв сервера → следующий
   универсальный; если их нет — запасной из другого сервиса; при появлении нового
   универсального — плавный возврат (ПК и Android).

## Грабли, проверенные на практике

- ПК нельзя автоматизировать через UI Automation: кастомный `TabItem` не отдаёт содержимое
  вкладок. Проверять через `SelfTest` (`parsers`, `checkconfigs`, `statuslogic`, `hiddenprobe`)
  и журнал `%LOCALAPPDATA%\VieNLink\logs`.
- Android Activity не экспортированы: `am start` не откроет Telegram/Nodes и т.п. — только
  запуск главного экрана и тапы по координатам (скриншот 1080×2354, координаты `input tap`
  в пикселях устройства).
- Зависания Android ищутся так: `adb shell dumpsys activity lastanr`,
  `adb shell dumpsys dropbox --print data_app_anr` (там причина и стек главного потока).
  Главный поток в `__rt_sigsuspend`/`pollOnce` — приложение было занято (или его держал GC),
  а не «мертво». Смотреть также `dumpsys meminfo` (PSS) и `ps -A` по `:probe`.
- UI-поток Android не должен трогать замки `NodeStore`: только снимок `_snapshot`
  (`Snapshot`, `Summary`, `DetailedSummary`, `Selected`). Всё тяжёлое — в `Task.Run`.
- Вывод `adb screencap` в перенаправленный файл на Windows может испортиться — снимать в
  `/sdcard`, затем `adb pull`.
- `dotnet build VieNLink.slnx` падает на Android-проекте без workload — собирать
  `VieNLink\VieNLink.csproj` и Android отдельным SDK.
- Перед сборкой закрыть `VieNLink.exe`, иначе файлы в `bin` заблокированы.
- Живых серверов в открытых списках 0,2–3 %; видео YouTube у некоторых провайдеров требует
  прокси-сервер (локальный GGC блокируется по IP).
