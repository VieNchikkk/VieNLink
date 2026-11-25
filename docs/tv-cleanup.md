# Чистка телевизора Xiaomi (MiTV-MOOQ1, Android 11, 1,7 ГБ ОЗУ, 32-бит ARM)

Устройство: `MiTV-MOOQ1` (`tarzan`), Android 11 (SDK 30), ABI только `armeabi-v7a`,
ADB по Wi-Fi `192.168.3.10:5555`. Состояние на **23.11.2026, 23:40**.

> Вернуть всё назад: `powershell -File build\tv-restore.ps1` (см. ниже).

## Как это работает

| Команда | Что делает | Освобождает |
|---|---|---|
| `pm disable-user --user 0 <пакет>` | выключает приложение, APK и данные остаются | оперативную память, фоновые процессы |
| `pm uninstall --user 0 <пакет>` | убирает для пользователя, APK в `/system` или `/data` остаётся | ОЗУ **и** место |
| `pm enable <пакет>` | возврат отключённого | — |
| `pm install-existing --user 0 <пакет>` | возврат удалённого для пользователя | — |

## Реестр изменений (что сделано)

### Удалено для пользователя (`pm uninstall --user 0`)

| Пакет | Что это |
|---|---|
| `com.mitv.tvhome.atv` | PatchWall — лаунчер Xiaomi с лентами и рекламой |
| `com.mitv.tvhome.michannel` | каналы PatchWall |
| `com.mitv.tvhome.mitvplus` | MiTV+ (контент PatchWall) |
| `com.google.android.videos` | Google Play Фильмы и ТВ (плитка и вкладка «Покупки») |
| `com.google.android.play.games` | Google Play Игры |
| `com.amazon.amazonvideo.livingroom` | Amazon Prime Video |
| `com.netflix.ninja` | Netflix |

Вернуть: `pm install-existing --user 0 <пакет>`.

### Отключено (`pm disable-user --user 0`)

| Пакет | Что это | Цена |
|---|---|---|
| `com.google.android.katniss` | Google Ассистент (2 процесса, ~210 МБ ОЗУ) — **включён обратно 25.11.2026 по просьбе владельца: нужен микрофон** | нет голосового поиска (пока отключён) |
| `com.google.android.tts` | синтез речи Google (данные голосов очищены) — **включён обратно 25.11.2026 вместе с Ассистентом**, иначе голосовой поиск отвечает молча | нет озвучки (пока отключён) |
| `com.google.android.marvin.talkback` | TalkBack — экранный диктор | нет озвучки интерфейса |
| `com.google.android.backdrop` | фото-заставка Google TV | нет фото-заставки |
| `com.google.android.feedback` | отзывы/телеметрия Google | — |
| `com.google.android.tungsten.setupwraith` | мастер первой настройки Google TV | — |
| `com.google.android.tvrecommendations` | строка рекомендаций на главном | нет рекомендаций |
| `com.google.android.youtube.tvmusic` | YouTube Music | нет YouTube Music |
| `com.xiaomi.amazon.alexa` | Alexa | — |
| `com.android.settings.intelligence` | поиск внутри настроек Android | поиск по настройкам |
| `com.mitv.gallery` | галерея Xiaomi (~104 МБ ОЗУ) | нет галереи |
| `com.mitv.dream` | заставка Xiaomi | нет заставки |

Вернуть: `pm enable <пакет>`.

### Полностью удалено (сторонние, APK тоже нет)

| Пакет | Что это |
|---|---|
| `com.yandex.browser.tv` | Яндекс.Браузер TV |
| `tv.lfstrm.smotreshka` | Смотрешка (для пользователя её уже не было) |

Ставятся только заново (APK).

### Прочие изменения

| Что | Команда |
|---|---|
| Google Play Маркет отключался, затем **возвращён** и проверен | `pm install-existing --user 0 com.android.vending` |
| Данные лаунчера очищены (сброс раскладки, ушли мёртвая плитка Play Фильмов и вкладка «Покупки») | `pm clear com.google.android.tvlauncher` |
| Кэши приложений очищены | `pm trim-caches 4G` |
| Данные Google TTS очищены (голоса скачиваются заново) | `pm clear com.google.android.tts` |
| `com.xiaomi.floatingframe` удалён **не нами** (был удалён до чистки) | — |

VPN (`com.v2raytun.android`) и Zona (`mobi.zona`) не тронуты.

## Восстановление

```powershell
# всё сразу
powershell -File build\tv-restore.ps1

# или вручную, по одному
$adb = "$env:LOCALAPPDATA\android-sdk\platform-tools\adb.exe"
$s = '192.168.3.10:5555'
& $adb -s $s shell "pm enable com.google.android.katniss"
& $adb -s $s shell "pm install-existing --user 0 com.mitv.tvhome.atv"
& $adb -s $s shell "pm list packages -d"   # что ещё отключено
```

## Что ещё можно отключить (не сделано)

### Безопасно, функции ТВ не теряются

| Пакет | Что это | Цена | Место |
|---|---|---|---|
| `com.google.android.syncadapters.calendar` | синхронизация календаря Google | календарь не обновляется | 4 МБ |
| `com.google.android.tv.remote.service` | приложение Google TV на телефоне как пульт | телефон-пульт | 7 МБ |
| `com.google.android.partnersetup` | предустановка партнёрских приложений Google | — | 0,3 МБ |
| `com.google.android.onetimeinitializer` | разовая инициализация Google | — | 0,2 МБ |
| `com.google.android.boot.appsplashscreen` | заставка при старте | логотип на старте | 0,2 МБ |
| `com.xiaomi.mitv.tvmanager` | «Менеджер ТВ» Xiaomi | встроенная очистка памяти | 0,2 МБ |
| `com.xiaomi.mimusic2` | Mi Music | плеер Xiaomi | 0,4 МБ |
| `com.miui.tv.analytics` | аналитика Xiaomi | — | 3,7 МБ |
| `com.xiaomi.statistic` | статистика Xiaomi | — | 0,2 МБ |
| `com.xiaomo.tv.milegal` | юридические тексты Xiaomi | — | 0,3 МБ |
| `com.mstar.android.tv.disclaimercustomization` | стартовый дисклеймер | — | 0,2 МБ |
| `com.mediatek.wwtv.setupwizard` | мастер первичной настройки ТВ | мастер при сбросе | 0,2 МБ |
| `android.autoinstalls.config.xiaomi.amelie` | автопредустановка приложений | — | 0,2 МБ |
| `fusion.android.tv.demo` | демо-контент | — | 0,2 МБ |
| `com.android.printspooler` | печать | печать | 0,2 МБ |
| `com.android.htmlviewer`, `com.android.soundpicker`, `com.android.dreams.basic` | просмотр HTML, мелодия, заставка | мелочи | 0,3 МБ |
| `com.android.backupconfirm`, `com.android.sharedstoragebackup`, `com.android.wallpaperbackup`, `com.android.localtransport` | резервное копирование Android | бэкап | 0,2 МБ |
| `com.android.providers.calendar`, `.contacts`, `.userdictionary` | календарь, контакты, словарь | приложения не увидят контакты | 1,4 МБ |

### Только если не пользуетесь

| Пакет | Что это | Что перестанет работать | Выигрыш |
|---|---|---|---|
| `com.xiaomi.tvqs.overseas` | панель быстрых настроек Xiaomi | настройки картинки/звука с пульта | **~82 МБ ОЗУ** |
| `com.google.android.apps.mediashell` | Chromecast built-in | трансляция с телефона | 109 МБ, ~40 МБ ОЗУ |
| `com.google.android.youtube.tv` | YouTube для ТВ | приложение YouTube | 57 МБ |
| `com.xiaomi.mitv.smartshare`, `com.mitv.milinkservice`, `com.xiaomi.wfdsinkhelperservice` | трансляция экрана (Miracast/MiLink) | зеркалирование экрана | мелочь |
| `com.xiaomi.mitv.mediaexplorer`, `com.mitv.videoplayer` | файлы/USB-плеер Xiaomi | просмотр с флешки | 2 МБ |
| `com.mitv.tvlock` | родительский контроль | блокировка по PIN | — |
| `com.mitv.livetv.ci_mediatek`, `com.mediatek.tvinput`, `com.mediatek.tv.service`, `com.mediatek.wwtv.tvcenter`, `com.android.providers.tv` | эфирное/кабельное ТВ | приём каналов | 1 МБ |
| `com.xiaomi.mitv.updateservice` | обновление системы | OTA (отключать только после обновления) | 43 МБ |
| `com.mi.bluetooth` | Bluetooth | BT-пульт/наушники | — |

## Не трогать и почему

| Пакет | Почему |
|---|---|
| `com.android.vpndialogs` | без него не появится окно разрешения VPN — не заработает ни наш клиент, ни v2raytun |
| `com.google.android.gms`, `com.google.android.gsf` | Play Services: YouTube, аккаунт, лаунчер |
| `com.google.android.webview` | отрисовка интерфейсов приложений и системных экранов |
| `com.google.android.tvlauncher` | единственный лаунчер после удаления PatchWall |
| `com.google.android.packageinstaller` | установка APK |
| `com.android.providers.media`, `.downloads` | файлы и загрузки |
| `com.android.networkstack.*`, `com.android.wifi.*` | сеть и Wi-Fi |
| `com.android.inputmethod.latin` | клавиатура |
| `mitv.service` | служба Xiaomi: HDMI-CEC, пульт, настройки ТВ |
| `com.v2raytun.android`, `mobi.zona` | по требованию владельца |

## Про память

Всего 1,7 ГБ (`MemTotal 1740488 kB`), занято ~1,6 ГБ, swap 599 МБ (занято ~250 МБ).
«Свободно 50–90 МБ» — норма: Android держит кэш. На отзывчивость влияют число фоновых
процессов и swap. Клиент VieNLink с поднятым TUN добавит ~200–300 МБ, поэтому перед
включением VPN лучше держать выключенными Chromecast, YouTube-приложение и панель `tvqs`.

Свободное место: было 861 МБ → ~1,36 ГБ (31 % свободно).
