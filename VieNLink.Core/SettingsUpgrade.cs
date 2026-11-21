namespace VieNLink;

/// <summary>
/// Приведение settings.json к текущей схеме. Файлы прежних версий читаются,
/// мигрируются и один раз перезаписываются — настройки пользователя не сбрасываются.
/// </summary>
internal static class SettingsUpgrade
{
    /// <summary>Возвращает true, если файл настроек стоит перезаписать.</summary>
    public static bool Apply(AppSettings settings)
    {
        var changed = false;

        if (settings.SchemaVersion < 1)
        {
            // Схема 1 — первая с явным номером. Все поля уже имеют значения
            // по умолчанию, поэтому достаточно зафиксировать номер.
            settings.SchemaVersion = 1;
            changed = true;
        }

        // Будущие миграции добавлять так:
        // if (settings.SchemaVersion < 2)
        // {
        //     ...
        //     settings.SchemaVersion = 2;
        //     changed = true;
        // }

        if (settings.SchemaVersion > SchemaVersions.Settings)
        {
            AppLog.Write("settings",
                $"Настройки новее приложения (схема {settings.SchemaVersion}, поддерживается {SchemaVersions.Settings}). " +
                "Читаю известные поля; незнакомые исчезнут только при следующем сохранении.");
        }

        return changed;
    }
}
