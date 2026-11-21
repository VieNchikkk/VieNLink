namespace VieNLink.Mobile;

/// <summary>
/// Настройки Android-версии: тот же файл <c>settings.json</c>, что у Windows-части,
/// поэтому общие поля (адрес проверки, лимит узлов, TLS-фрагментация) работают одинаково.
/// </summary>
internal static class SettingsStore
{
    private static readonly object Gate = new();
    private static AppSettings? _current;

    public static AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Load();
            }
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            if (_current is not null)
            {
                JsonFileStore.Write(AppPaths.SettingsPath, _current);
            }
        }
    }

    private static AppSettings Load()
    {
        AppPaths.EnsureDirectories();

        var settings = JsonFileStore.Read<AppSettings>(AppPaths.SettingsPath);
        if (settings is null)
        {
            settings = new AppSettings();
        }
        else if (SettingsUpgrade.Apply(settings))
        {
            JsonFileStore.Write(AppPaths.SettingsPath, settings);
        }

        return settings;
    }
}
