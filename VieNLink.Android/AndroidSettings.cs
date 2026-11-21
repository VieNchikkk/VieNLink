namespace VieNLink.Mobile;

/// <summary>
/// Настройки, которых нет в общем <c>AppSettings</c>: автозапуск, плитка, split-tunnel.
/// Лежат отдельным файлом, чтобы не менять схему Windows-настроек.
/// </summary>
internal sealed class AndroidSettings
{
    /// <summary>Поднимать туннель после загрузки устройства (нужно разрешение VPN).</summary>
    public bool AutoConnectOnBoot { get; set; }

    /// <summary>Через туннель идут только выбранные приложения.</summary>
    public bool SplitTunnel { get; set; }

    /// <summary>Пакеты выбранных приложений (split-tunnel).</summary>
    public List<string> AllowedPackages { get; set; } = new();
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
                _current = JsonFileStore.Read<AndroidSettings>(Path) ?? new AndroidSettings();
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
