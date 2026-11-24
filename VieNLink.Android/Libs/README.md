# Ядро libbox.aar

Сюда кладётся `libbox.aar` — сборка sing-box для Android (gomobile-библиотека).
Без неё проект собирается и запускается, но на экране написано «libbox.aar не подключён»
(проверка по имени класса `libbox.BoxInstance`).

Как получить — `docs/android-design.md` §15. Кратко:

1. Собрать из исходников sing-box: нужны Go, `gomobile` и Android NDK.
   Версия AAR должна совпадать с версией ядра, которое тестируется.
2. Либо взять готовый AAR из доверенной сборки, проверив происхождение:
   это бинарник с доступом ко всему трафику устройства.

Класть файл сюда: `VieNLink.Android/Libs/libbox.aar`.
Проект подхватывает его через `<AndroidAarLibrary Include="Libs\*.aar" />`.

## 32-битный ARM (`libgojni-armeabi-v7a.so`)

В `libbox.aar` только `jni/arm64-v8a/libgojni.so`. Для 32-битных телевизоров
(Xiaomi MiTV-MOOQ1, Android 11) рядом лежит отдельная библиотека
`libgojni-armeabi-v7a.so` — она собирается из тех же исходников sing-box
`-target=android/arm` (см. `docs/android-design.md` §15) и добавляется в APK
скриптом `build\publish-android.ps1 -Abi armeabi-v7a`.
