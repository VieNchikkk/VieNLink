namespace VieNLink.Mobile;

/// <summary>
/// Настройки, которых нет в общем <c>AppSettings</c>: автозапуск, плитка, split-tunnel.
/// Лежат отдельным файлом, чтобы не менять схему Windows-настроек.
/// </summary>
internal sealed class AndroidSettings
{
    /// <summary>Поднимать туннель после загрузки устройства (нужно разрешение VPN).</summary>
    public bool AutoConnectOnBoot { get; set; }

    /// <summary>Через туннель идут только выбранные приложения (устарело: см. <see cref="AppRouteMode"/>).</summary>
    public bool SplitTunnel { get; set; }

    /// <summary>
    /// Что делать с приложениями: все, только из списка или все, кроме списка.
    /// Управляется на экране «Туннелирование», применяется в VpnService.
    /// </summary>
    public AppRouteMode AppRouteMode { get; set; } = AppRouteMode.All;

    /// <summary>Пакеты выбранных приложений (split-tunnel).</summary>
    public List<string> AllowedPackages { get; set; } = new();

    /// <summary>Последнее обновление источников (автоматика в фоне).</summary>
    public DateTime LastSourceRefreshUtc { get; set; }

    /// <summary>Последний сбор MTProto-прокси.</summary>
    public DateTime LastMtProtoCollectUtc { get; set; }

    /// <summary>Последняя проверка MTProto-прокси.</summary>
    public DateTime LastMtProtoTestUtc { get; set; }

    /// <summary>Стратегия TUN-обхода, выбранная пользователем (имя <see cref="StreamFragmentMode"/>).</summary>
    public string TunBypassStrategy { get; set; } = "RecordSplit";

    /// <summary>
    /// Последний режим запуска: true — TUN-обход, false — прокси-сервер. Нужен, чтобы
    /// после перезапуска службы системой (START_STICKY без intent) восстановить
    /// именно тот режим, а не поднять прямой TUN без прокси.
    /// </summary>
    public bool LastRunWasBypass { get; set; }

    /// <summary>Автоматически собирать MTProto из каналов.</summary>
    public bool AutoMtProto { get; set; } = true;

    /// <summary>Автоматически проверять серверы в фоне (в дополнение к :probe в процессе).</summary>
    public bool AutoProbe { get; set; } = true;
}

internal static class AndroidSettingsStore
{
    private static readonly object Gate = new();
    private static AndroidSettings? _current;

    public static string Path => System.IO.Path.Combine(AppPaths.DataDirectory, "android-settings.json");

    public static AndroidSettings Current
    {
        get
        {
            lock (Gate)
            {
                if (_current is not null)
                {
                    return _current;
                }

                AppPaths.EnsureDirectories();
                var loaded = JsonFileStore.Read<AndroidSettings>(Path) ?? new AndroidSettings();
                // Старый файл знал только «только выбранные» — переносим в новый режим.
                if (loaded.SplitTunnel && loaded.AppRouteMode == AppRouteMode.All)
                {
                    loaded.AppRouteMode = AppRouteMode.OnlyListed;
                }

                // Автоподключение после загрузки не нужно и переключателя в интерфейсе нет.
                if (loaded.AutoConnectOnBoot)
                {
                    loaded.AutoConnectOnBoot = false;
                    JsonFileStore.Write(Path, loaded);
                }

                _current = loaded;
                return _current;
            }
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            if (_current is not null)
            {
                JsonFileStore.Write(Path, _current);
            }
        }
    }

    public static bool IsAllowed(string packageName)
    {
        lock (Gate)
        {
            return Current.AllowedPackages.Contains(packageName, StringComparer.Ordinal);
        }
    }

    public static void SetAllowed(string packageName, bool allowed)
    {
        lock (Gate)
        {
            var settings = Current;
            var present = settings.AllowedPackages.Contains(packageName, StringComparer.Ordinal);
            if (allowed && !present)
            {
                settings.AllowedPackages.Add(packageName);
            }
            else if (!allowed && present)
            {
                settings.AllowedPackages.RemoveAll(item => item.Equals(packageName, StringComparison.Ordinal));
            }
            else
            {
                return;
            }

            JsonFileStore.Write(Path, settings);
        }
    }
}
