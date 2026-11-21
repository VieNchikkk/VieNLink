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
            // На телефоне конфиг ограничен 200 узлами (память и старт), поэтому
            // сразу пишем это значение, а не виндовые 400.
            settings = new AppSettings { MaxNodesInConfig = NodeStore.MobileMaxNodesInConfig };
        }
        else if (SettingsUpgrade.Apply(settings))
        {
            JsonFileStore.Write(AppPaths.SettingsPath, settings);
        }

        // Если файл пришёл с Windows, лимит конфига 400 на телефоне всё равно
        // обрезается до 200 — фиксируем фактическое значение, чтобы UI не врал.
        if (settings.MaxNodesInConfig > NodeStore.MobileMaxNodesInConfig)
        {
            settings.MaxNodesInConfig = NodeStore.MobileMaxNodesInConfig;
            JsonFileStore.Write(AppPaths.SettingsPath, settings);
        }

        // Переключатели убраны из «Настроек»: работают значения по умолчанию.
        // Фоновая проверка, автообновление, автопоиск, проверка после обновления,
        // показ трафика и выхода — включены.
        var changed = false;
        if (!settings.AutoUpdateSubscriptions) { settings.AutoUpdateSubscriptions = true; changed = true; }
        if (!settings.TestAfterRefresh) { settings.TestAfterRefresh = true; changed = true; }
        if (!settings.AutoDiscoverSources) { settings.AutoDiscoverSources = true; changed = true; }
        if (!settings.IdleTestEnabled) { settings.IdleTestEnabled = true; changed = true; }
        if (!settings.ShowTraffic) { settings.ShowTraffic = true; changed = true; }
        if (!settings.ShowExitInfo) { settings.ShowExitInfo = true; changed = true; }

        // Всё, что убрано из «Настроек», тоже работает по умолчанию и автоматически.
        var defaults = new AppSettings();
        void Use<T>(Func<T> get, Action<T> set, T value)
        {
            if (!EqualityComparer<T>.Default.Equals(get(), value))
            {
                set(value);
                changed = true;
            }
        }

        Use(() => settings.MaxNodesInConfig, value => settings.MaxNodesInConfig = value,
            NodeStore.MobileMaxNodesInConfig);
        Use(() => settings.MaxTotalNodes, value => settings.MaxTotalNodes = value, defaults.MaxTotalNodes);
        Use(() => settings.MaxDelayMs, value => settings.MaxDelayMs = value, defaults.MaxDelayMs);
        Use(() => settings.KeepUnseenNodeDays, value => settings.KeepUnseenNodeDays = value,
            defaults.KeepUnseenNodeDays);
        Use(() => settings.AutoUpdateHours, value => settings.AutoUpdateHours = value, defaults.AutoUpdateHours);
        Use(() => settings.AutoRefreshMinutes, value => settings.AutoRefreshMinutes = value,
            defaults.AutoRefreshMinutes);
        Use(() => settings.AutoDiscoverHours, value => settings.AutoDiscoverHours = value,
            defaults.AutoDiscoverHours);
        Use(() => settings.IdleRecheckHours, value => settings.IdleRecheckHours = value,
            defaults.IdleRecheckHours);
        Use(() => settings.IdleTestBatch, value => settings.IdleTestBatch = value, defaults.IdleTestBatch);
        Use(() => settings.ConnectOnlyVerified, value => settings.ConnectOnlyVerified = value,
            defaults.ConnectOnlyVerified);
        Use(() => settings.ConnectOnlyMine, value => settings.ConnectOnlyMine = value, defaults.ConnectOnlyMine);
        Use(() => settings.AutoFailover, value => settings.AutoFailover = value, defaults.AutoFailover);
        Use(() => settings.TlsFragment, value => settings.TlsFragment = value, defaults.TlsFragment);
        Use(() => settings.TlsRecordFragment, value => settings.TlsRecordFragment = value,
            defaults.TlsRecordFragment);
        Use(() => settings.TunStrictRoute, value => settings.TunStrictRoute = value, defaults.TunStrictRoute);

        if (changed)
        {
            JsonFileStore.Write(AppPaths.SettingsPath, settings);
        }

        return settings;
    }
}
