# Сторонние компоненты и лицензии

VieNLink распространяется под **GPL-3.0** (см. `LICENSE`). Ниже — компоненты, которые
используются или поставляются вместе с приложением, и что с ними нужно делать при релизе.

Колонка «статус» отражает проверку на 21.11.2026: `подтверждено` — лицензия сверена
с первоисточником, `уточнить` — перед публикацией обязательно посмотреть файл лицензии
в репозитории.

| Компонент | Где используется | Лицензия | Статус | Что требуется |
|---|---|---|---|---|
| **sing-box** (`sing-box.exe`, `libbox.aar`) | ядро прокси в Windows и Android | GPL-3.0 (с доп. условием о названии) | подтверждено | приложить текст лицензии и ссылку на исходники; не использовать имя sing-box и не намекать на связь с SagerNet |
| **WinDivert** (`WinDivert.dll`, `WinDivert64.sys`) | «Свой обход DPI» (WinDivert-движок) и zapret | LGPL-3.0 (двойная: GPL-2.0 / LGPL-3.0) | подтверждено | поставлять **отдельными неизменёнными DLL/SYS** (уже так), исходники — `https://reqrypt.org/windivert.html` |
| **zapret** (`winws.exe` и вспомогательные файлы) | «Обход запрет» | MIT, © 2016–2024 bol-van | **подтверждено 21.11.2026** (`docs/LICENSE.txt` в `bol-van/zapret`) | включить текст MIT с копирайтом (ниже); исходники — `https://github.com/bol-van/zapret` |
| **zapret-win-bundle** (откуда берутся готовые файлы) | источник `winws.exe`, `cygwin1.dll`, WinDivert и фильтров | не отдельная лицензия: набор zapret + минимальный Cygwin + WinDivert | подтверждено | у каждого файла своя лицензия; ниже перечислено, что именно скачивается |
| **Cygwin runtime** (`cygwin1.dll`) | нужен `winws.exe` (сборка zapret под Cygwin) | LGPL-3.0-or-later (Cygwin API library, `https://cygwin.com/licensing.html`) | подтверждено | поставлять отдельной неизменённой DLL; исходники — `https://cygwin.com/` |
| **.NET 10 / WPF / WinForms** | приложение Windows | MIT | подтверждено | текст MIT в поставке не обязателен, но желателен |
| **AndroidX / .NET for Android** | приложение Android | Apache-2.0 / MIT | подтверждено | сохранить уведомления об авторстве |
| **MAUI или Avalonia** (если появятся) | UI Android | MIT | — | добавить в этот список при подключении |

## Какие файлы zapret скачиваются

`ZapretService` берёт файлы по отдельности из `bol-van/zapret-win-bundle` (raw-ссылки,
без архивов): `winws.exe`, `WinDivert.dll`, `WinDivert64.sys`, `cygwin1.dll`,
`files/quic_initial_www_google_com.bin`, `files/stun.bin`, `files/wireguard_initiation.bin`,
`files/list-youtube.txt` и четыре фильтра `windivert.filter/*.txt`.

- `winws.exe` — MIT (zapret);
- `cygwin1.dll` — LGPL-3.0-or-later (Cygwin);
- `WinDivert.dll` / `WinDivert64.sys` — LGPL-3.0 (двойная GPL-2.0 / LGPL-3.0);
- файлы `files/*` и `windivert.filter/*` — части дистрибутива zapret (MIT).

Если релиз собирается с папкой `zapret\` (параметр `publish-release.ps1`), все эти файлы
попадают к пользователю; полный текст GPL-3.0 лежит в `LICENSE`, текст MIT для zapret —
ниже в этом файле.

## Текст лицензии zapret (MIT)

```text
MIT License

Copyright (c) 2016-2024 bol-van

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Почему LGPL соблюдена

WinDivert и `cygwin1.dll` лежат рядом с `winws.exe` отдельными файлами, не встраиваются
в `VieNLink.exe` и не изменяются. Пользователь может заменить их своими сборками —
это и есть требование LGPL о возможности повторной компоновки.

## Порядок комплектации релиза

1. `LICENSE` (GPL-3.0) — в корне репозитория и в поставке: `publish-release.ps1`
   копирует его в папку релиза рядом с `VieNLink.exe`.
2. Этот файл — в поставке рядом с `.exe` и внутри APK (раздел «О программе» →
   «Сторонние компоненты»). Текст GPL-3.0 в APK — `assets/LICENSE.txt`.
3. Если в поставку кладётся `sing-box.exe` (параметр `-Core` в `build\publish-release.ps1`),
   ссылка на исходники (`https://github.com/SagerNet/sing-box`) уже указана в этом файле,
   а полный текст GPL-3.0 — в `LICENSE`.
4. Если в поставку кладётся zapret — текст MIT и перечень файлов уже в этом файле.
5. `libbox.aar` в APK — это тот же sing-box: лицензия и ссылка на исходники обязательны
   и на Android. Экран «О программе» показывает и то, и другое.

## Почему приложение под GPL-3.0

Android-версия линкует `libbox` (sing-box, GPL-3.0) через JNI. Это делает всю сборку
производной работой, поэтому проект публикуется под GPL-3.0 целиком, включая Windows-часть.
Подробнее — `docs/plan-2026.md` («Решения») и `docs/android-design.md` §4.
