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

1. Тест Android на втором операторе (другая сеть/провайдер): подключение, автосмена сервера,
   MTProto.
2. Ручное добавление плитки «Туннель» на рабочий стол (сейчас только через меню виджетов).
3. Дальше улучшать обход DPI (Windows): качество стратегий, автоподбор под провайдера.
4. При следующем релизе: поднять версию в `build/Version.props`, добавить раздел в
   `CHANGELOG.md`, собрать Windows + GitHub APK **и магазинную сборку** (`publish-store.ps1`),
   обновить теги/релизы и этот файл.
5. Проверить вживую приоритет переключения: «Универсальный» → обрыв сервера → следующий
   универсальный; если их нет — запасной из другого сервиса; при появлении нового
   универсального — плавный возврат (ПК и Android).

## Грабли, проверенные на практике

- ПК нельзя автоматизировать через UI Automation: кастомный `TabItem` не отдаёт содержимое
  вкладок. Проверять через `SelfTest` (`parsers`, `checkconfigs`, `statuslogic`, `hiddenprobe`)
  и журнал `%LOCALAPPDATA%\VieNLink\logs`.
- Android Activity не экспортированы: `am start` не откроет Telegram/Nodes и т.п. — только
  запуск главного экрана и тапы по координатам (скриншот 1080×2354, координаты `input tap`
  в пикселях устройства).
- Вывод `adb screencap` в перенаправленный файл на Windows может испортиться — снимать в
  `/sdcard`, затем `adb pull`.
- `dotnet build VieNLink.slnx` падает на Android-проекте без workload — собирать
  `VieNLink\VieNLink.csproj` и Android отдельным SDK.
- Перед сборкой закрыть `VieNLink.exe`, иначе файлы в `bin` заблокированы.
- Живых серверов в открытых списках 0,2–3 %; видео YouTube у некоторых провайдеров требует
  прокси-сервер (локальный GGC блокируется по IP).
