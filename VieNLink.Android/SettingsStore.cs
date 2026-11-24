namespace VieNLink.Mobile;

/// <summary>
/// Настройки Android-версии: тот же файл <c>settings.json</c>, что у Windows-части,
/// поэтому общие поля (адрес проверки, лимит серверов, TLS-фрагментация) работают одинаково.
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
            // На телефоне конфиг ограничен 200 серверами (память и старт), поэтому
            // сразу пишем это значение, а не виндовые 400. Схему фиксируем сразу,
            // чтобы повторные миграции (партия 200, все источники) не запускались.
            settings = new AppSettings { MaxNodesInConfig = NodeStore.MobileMaxNodesInConfig };
            SettingsUpgrade.Apply(settings);
            JsonFileStore.Write(AppPaths.SettingsPath, settings);
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

        // Фоновая проверка и автосбор включаются переключателями в «Настройках»;
        // проверка задержек после обновления списков и показ трафика — по умолчанию.
        var changed = false;
        if (!settings.TestAfterRefresh) { settings.TestAfterRefresh = true; changed = true; }
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
        // Телевизор: 1500 серверов более чем достаточно (всего 1,7 ГБ ОЗУ), кэш и старт легче.
        Use(() => settings.MaxTotalNodes, value => settings.MaxTotalNodes = value,
            DeviceProfile.IsTelevision ? Math.Min(defaults.MaxTotalNodes, 1500) : defaults.MaxTotalNodes);
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
