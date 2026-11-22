# Материалы карточки магазина

| Файл | Назначение | Требования |
|---|---|---|
| `icon-512.png` | Иконка приложения | 512×512 PNG, без прозрачности |
| `feature-1024x500.png` | Feature graphic (Google Play) | 1024×500 PNG/JPEG |
| `screenshots/*.png` | Скриншоты телефона | 2–8 шт., портрет, 1080×1920 и выше |

Скриншоты сняты с реального устройства (realme, Android 16) через `adb`:

```powershell
adb shell screencap -p /sdcard/shot.png
adb pull /sdcard/shot.png store\screenshots\04-nodes.png
```

Перед публикацией уберите с экранов служебные надписи вроде «Поиск узлов остановлен»
и выберите кадры с понятным состоянием: главный экран с подключением, экран «Узлы»,
журнал, настройки.

Иконка и feature graphic собраны из `branding/vienlink-icon-src.png` и
`branding/vienlink-social.png`. Пересобрать можно любым редактором; главное — размеры
и отсутствие прозрачности у иконки.
