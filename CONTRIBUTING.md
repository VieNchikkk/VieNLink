# Разработка

## Требования

- Windows 10/11 x64.
- .NET SDK 10.0 (проект собирается под `net10.0-windows10.0.26100.0`).
- Visual Studio 2022/2026 или Rider — по желанию; достаточно `dotnet` CLI.
- **Внешние NuGet-пакеты не используются.** Не добавляйте их без обсуждения:
  приложение должно собираться «из коробки».

## Сборка и проверки

```
dotnet build VieNLink.slnx
dotnet run --project VieNLink.SelfTest -- parsers
dotnet run --project VieNLink.SelfTest -- checkconfigs
dotnet run --project VieNLink.SelfTest -- sources
dotnet run --project VieNLink.SelfTest -- cache
```

- `parsers` — разбор ссылок, подписок и конфига без сети; основной набор регрессий.
- `checkconfigs` — все варианты конфига через настоящий `sing-box check`
  (ядро должно быть установлено, иначе self-test скачает его).
- `sources` — чтение зашифрованного `sources.dat` (проверка DPAPI и миграции).
- Живые прогоны (`liveness`, `tun`, `desync*`, `tunbypass*`) требуют сети,
  а часть — прав администратора.

Полный список режимов: `dotnet run --project VieNLink.SelfTest -- --help`
или раздел «Сборка и проверка» в `AGENTS.md`.

## Стиль

- C#: 4 пробела, `namespace VieNLink;`, Nullable включён.
- UI — в XAML; работа с ядром — через `ProxyManager`.
- Тексты интерфейса, журналы и комментарии — на русском.
- Не переписывайте архитектуру без необходимости: правки должны быть точечными.
- После изменений обязательны `dotnet build` и `parsers`.

## Pull request

1. Ветка от `main`, осмысленное имя (`fix/tun-timeout`, `feature/...`).
2. Опишите, что и зачем изменено, и как проверяли.
3. Приложите вывод `dotnet build` и `SelfTest -- parsers`.
4. Не коммитьте `bin/`, `obj/`, `release/`, сертификаты и локальные журналы:
   они уже в `.gitignore`.

## Структура

Краткая карта файлов — в `AGENTS.md`. Окно разбито на partial-классы
`VieNLink/MainWindow.*.cs`; новые функции лучше добавлять в тематический файл,
а не в `MainWindow.xaml.cs`.

## Релиз

```
powershell -File build\publish-release.ps1
powershell -File build\publish-installer.ps1
```

Первый скрипт собирает self-contained `VieNLink.exe`, ZIP и кладёт рядом `core\`
и `zapret\`, если они уже скачаны. Второй дополнительно собирает `setup.exe`
(Inno Setup 6). Подпись Authenticode включается через `-PfxPath`/`-Thumbprint`
или файл `build/signing.thumbprint`.
