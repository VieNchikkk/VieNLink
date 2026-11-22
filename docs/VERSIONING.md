# Версии и совместимость

## Схема версий

VieNLink использует Semantic Versioning: `MAJOR.MINOR.PATCH[-beta.N]`.

- **MAJOR** — несовместимое изменение данных или поведения, требующее ручной миграции.
- **MINOR** — новые возможности без поломки совместимости.
- **PATCH** — исправления и небольшие улучшения.
- **`-beta.N`** — предрелизная сборка. На GitHub такие релизы помечаются как **Pre-release**.

Текущие версии: Windows и Android — **1.3.2** (единая линейка).

## Единая версия Windows и Android

С версии **1.3.0** номер общий. Он задаётся один раз в `build/Version.props`
(`VieNLinkVersion`), оба проекта импортируют файл:

- Windows: `Version`, `FileVersion`, `InformationalVersion` (`1.3.0-beta`);
- Android: `ApplicationDisplayVersion` (`1.3.0`), `ApplicationVersion`
  (`VieNLinkVersionCode`).

`versionCode` Android вычисляется по номеру: `major*10000 + minor*100 + patch`
(1.3.0 → 10300, 1.3.1 → 10301, 1.3.2 → 10302, 1.4.0 → 10400) и обязан только расти.
Тег Windows — `v1.3.0-beta`, Android — `v1.3.0-android`: числовая часть одинаковая,
суффикс платформы остаётся только в теге.

Платформы версионируются отдельно: у Windows- и Android-клиента свои номера, потому что
объём изменений разный. Тег релиза содержит платформу, если она не Windows:
`v1.0.14-beta` (Windows) и `v1.1.5-android` (Android). Оба обновлятора различают релизы
по вложенным файлам: Windows ищет `setup.exe`/ZIP, Android — APK.

## Где хранится версия

| Место | Что показывает |
|---|---|
| `VieNLink/VieNLink.csproj` | `Version`, `InformationalVersion`, `FileVersion` — версия Windows |
| `VieNLink.Android/VieNLink.Android.csproj` | `ApplicationDisplayVersion`, `ApplicationVersion` (versionCode) — версия Android |
| Приложение → «Общие» / «О программе» | версия приложения (и ядра — в Windows) |
| Заголовок окна / трей | `VieNLink (бета)` |
| `%LOCALAPPDATA%\VieNLink\core\version.txt` | версия установленного ядра sing-box |
| GitHub → Releases | теги `vX.Y.Z[-beta]` и `vX.Y.Z-android`, описание, контрольные суммы |
| `CHANGELOG.md` | что изменилось между версиями |

## Поддерживаемые версии

| Версия | Поддержка |
|---|---|
| Windows 1.3.2-beta (текущая) | Да |
| Android 1.3.2 (текущая) | Да |
| Windows 1.3.0 и старше | Нет — обновитесь до текущей беты |
| Android 1.3.0 и старше | Нет |
| < 1.0 | Нет |

Пока приложение в бете, поддерживается только последняя сборка. После стабилизации
будет действовать правило «текущая и предыдущая MINOR-ветка».

## Совместимость данных

Приложение не сбрасывает настройки при обновлении. За это отвечают версии схем:

- **Настройки** — `AppSettings.SchemaVersion`, миграция в `SettingsUpgrade.Apply`.
  Файл без номера схемы (старая версия) получает текущий номер и перезаписывается один раз.
- **Кэш узлов** — `NodeCache.SchemaVersion` (`SchemaVersions.NodeCache`).
  Старый кэш читается как обычно; кэш новее приложения читается по известным полям,
  а в журнал пишется предупреждение.
- **MTProto** — `MtProtoCache.SchemaVersion`, та же логика.
- **Папки данных** — `%LOCALAPPDATA%\DomVPN` и `%LOCALAPPDATA%\MyrrProxy`
  автоматически переносятся в `%LOCALAPPDATA%\VieNLink`.
- **Ядро sing-box** — обновляется отдельно; версия хранится в `core\version.txt`
  и не связана с версией приложения.

При откате на более старую версию незнакомые поля файлов сохраняются, пока приложение
не перезапишет файл; после первой перезаписи они будут потеряны. Поэтому перед откатом
сделайте копию `%LOCALAPPDATA%\VieNLink`.

## Как выпустить новую версию

### Windows

1. Обновите `Version`, `AssemblyVersion`, `FileVersion` и `InformationalVersion`
   в `VieNLink/VieNLink.csproj` (`1.0.10`, `1.0.10-beta` и т.п.).
2. Добавьте раздел в `CHANGELOG.md`.
3. Прогоните `dotnet build VieNLink.slnx`, `SelfTest -- parsers` и `SelfTest -- checkconfigs`.
4. Соберите поставку: `powershell -File build\publish-installer.ps1`
   (портативный ZIP + `setup.exe`).
5. Посчитайте SHA-256 для ZIP и `setup.exe`, положите в `SHA256SUMS.txt`.
6. Создайте тег `vX.Y.Z[-beta]` и GitHub Release; для беты включите **Pre-release**.
   Приложите `setup.exe`, ZIP и `SHA256SUMS.txt`.

### Android

1. Обновите `ApplicationDisplayVersion` и `ApplicationVersion` (versionCode обязан
   расти) в `VieNLink.Android/VieNLink.Android.csproj`.
2. Добавьте раздел в `CHANGELOG.md`.
3. Соберите подписанный APK: `powershell -File build\publish-android.ps1`.
   Скрипт сам создаёт долговременный ключ в `%LOCALAPPDATA%\VieNLink\signing`,
   если его ещё нет, и пишет `release\VieNLink-X.Y.Z-android.apk` и `.apk.sha256`.
4. Создайте тег `vX.Y.Z-android` и GitHub Release (для беты — **Pre-release**).
   Приложите APK и `.apk.sha256`.
5. **Сразу соберите магазинную сборку** (не откладывать на потом):
   `powershell -File build\publish-store.ps1` → `release\VieNLink-X.Y.Z-store.apk`
   (RuStore) и `release\VieNLink-X.Y.Z-store.aab` (Google Play), плюс `.sha256`.
   **В RuStore/Play пока не публикуем** (решение владельца, 22.11.2026) — артефакты
   держим готовыми в `release\` и загружаем позже. versionCode магазинной сборки
   тот же, что у GitHub-APK, и обязан только расти.
6. **Ключ подписи нельзя терять:** обновление устанавливается только поверх APK,
   подписанного тем же ключом. Сделайте резервную копию `%LOCALAPPDATA%\VieNLink\signing`.

Старые теги и релизы не удаляйте и не перемещайте — это история версий.

## Обновление у пользователя

**Windows.** При запуске приложение само проверяет релизы на GitHub (`VieNchikkk/VieNLink`)
и рассматривает только те, где есть `setup.exe` или ZIP. Если версия новее, оно предлагает
скачать и установить `setup.exe`; настройки, источники и кэш сохраняются.

**Android.** При запуске и раз в сутки в фоне приложение сравнивает свою версию с релизами,
в которых есть APK. Если на GitHub версия выше, APK скачивается, проверяется по SHA-256
и устанавливается через `PackageInstaller` поверх текущей версии; последний шаг — системное
подтверждение установки. Проверка идёт не чаще раза в 6 часов, кнопка «Проверить обновление»
запускает её вручную.

Для публичного репозитория ничего настраивать не нужно. Для приватного можно задать
переменную окружения `VIENLINK_GITHUB_TOKEN` (или `GITHUB_TOKEN`) с токеном чтения
релизов — в поставку токен не встраивается (Windows). Android-клиент рассчитан на публичный
репозиторий и токен не использует.

Ручной вариант остаётся: скачать `VieNLink-X.Y.Z-setup.exe` из Releases (или ZIP
для портативной версии) либо APK для Android, установить поверх текущей версии
и при желании сверить контрольные суммы.

