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

        if (settings.SchemaVersion < 2)
        {
            // Партия 40 в простое не успевала за мёртвыми бесплатными серверами:
            // на ПК можно смело проверять 200 за проход.
            if (settings.IdleTestBatch < 200)
            {
                settings.IdleTestBatch = 200;
            }
        }

        if (settings.SchemaVersion < 3)
        {
            // Все источники по умолчанию включены (и ПК, и телефон): разово включаем
            // и те, что старые версии держали выключенными.
            NodeSourceService.EnableAllSourcesOnce = true;
        }

        if (settings.SchemaVersion < 3)
        {
            settings.SchemaVersion = 3;
            changed = true;
        }

        if (settings.SchemaVersion > SchemaVersions.Settings)
        {
            AppLog.Write("settings",
                $"Настройки новее приложения (схема {settings.SchemaVersion}, поддерживается {SchemaVersions.Settings}). " +
                "Читаю известные поля; незнакомые исчезнут только при следующем сохранении.");
        }

        return changed;
    }
}
