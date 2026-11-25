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

    /// <summary>
    /// Одноразовый сброс маршрутизации приложений на «все через сервер» (1.3.42):
    /// старые настройки split-tunnel оставляли YouTube и браузер вне туннеля.
    /// </summary>
    public bool RoutingReset { get; set; }

    /// <summary>Последнее обновление источников (автоматика в фоне).</summary>
    public DateTime LastSourceRefreshUtc { get; set; }

    /// <summary>Последний сбор MTProto-прокси.</summary>
    public DateTime LastMtProtoCollectUtc { get; set; }

    /// <summary>Последняя проверка MTProto-прокси.</summary>
    public DateTime LastMtProtoTestUtc { get; set; }

    /// <summary>Стратегия TUN-обхода, выбранная пользователем (имя <see cref="StreamFragmentMode"/>).</summary>
    public string TunBypassStrategy { get; set; } = "Oob";

    /// <summary>
    /// Отдельная стратегия для видео-CDN (googlevideo/ytimg/ggpht). Пусто — как основная.
    /// </summary>
    public string TunBypassVideoStrategy { get; set; } = string.Empty;

    /// <summary>Пресет стратегии для Wi-Fi (страницы и видео-CDN).</summary>
    public string TunBypassStrategyWifi { get; set; } = "Oob";

    /// <summary>Пресет стратегии для мобильной сети.</summary>
    public string TunBypassStrategyMobile { get; set; } = "Oob";

    /// <summary>Пресет видео-стратегии для Wi-Fi.</summary>
    public string TunBypassVideoStrategyWifi { get; set; } = "Oob";

    /// <summary>Пресет видео-стратегии для мобильной сети.</summary>
    public string TunBypassVideoStrategyMobile { get; set; } = "Oob";

    /// <summary>
    /// Стратегии обхода по конкретной сети: ключ — «wifi:&lt;SSID&gt;» или
    /// «cell:&lt;MCCMNC&gt;:&lt;оператор&gt;». Заполняется при первом запуске обхода в этой сети.
    /// </summary>
    public Dictionary<string, BypassPreset> BypassPresets { get; set; } = new();

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

    /// <summary>
    /// Пользователь разрешил делиться подобранными параметрами обхода: отправляются только
    /// оператор и стратегии (без Wi-Fi SSID, адресов и идентификаторов устройства).
    /// По умолчанию выключено — согласие даётся галочкой в настройках.
    /// </summary>
    public bool ShareProviderPresets { get; set; }

    /// <summary>Итог последнего автоподбора (ручного или фонового): виден на экране TUN после перезапуска.</summary>
    public DateTime LastAutotuneUtc { get; set; }

    public string LastAutotuneSummary { get; set; } = string.Empty;

    /// <summary>Результат последней пробы QUIC — синтетической, до реального трафика.</summary>
    public DateTime LastQuicProbeUtc { get; set; }

    public string LastQuicProbe { get; set; } = string.Empty;
}

/// <summary>Стратегии TUN-обхода, сохранённые для одной сети (Wi-Fi или оператора).</summary>
internal sealed class BypassPreset
{
    /// <summary>Стратегия страниц (имя <see cref="StreamFragmentMode"/>).</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>Стратегия видео-CDN; пусто — как для страниц.</summary>
    public string Video { get; set; } = string.Empty;

    /// <summary>Блокировать ли QUIC на этой сети; null — не задано, берём из базы/общих настроек.</summary>
    public bool? BlockQuic { get; set; }
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
                // Старый split-tunnel (SplitTunnel=true) ломал подключение: приложения вне
                // списка ходили напрямую, и YouTube/браузер «не работали при рабочем сервере».
                // Сбрасываем в «все приложения через сервер» один раз при загрузке.
                var healed = false;
                if (!loaded.RoutingReset)
                {
                    // Один раз после обновления: в старых настройках мог остаться список
                    // приложений, из-за которого весь трафик шёл напрямую.
                    loaded.RoutingReset = true;
                    loaded.SplitTunnel = false;
                    loaded.AppRouteMode = AppRouteMode.All;
                    loaded.AllowedPackages.Clear();
                    healed = true;
                }

                // Автоподключение после загрузки не нужно и переключателя в интерфейсе нет.
                if (loaded.AutoConnectOnBoot)
                {
                    loaded.AutoConnectOnBoot = false;
                    healed = true;
                }

                if (healed)
                {
                    JsonFileStore.Write(Path, loaded);
                }

                _current = loaded;
                // Согласие на отправку подборок живёт в этих настройках, а очередь — в ядре.
                ProviderPresetShare.Enabled = loaded.ShareProviderPresets;
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

    /// <summary>
    /// Запомнить итог автоподбора. Экран TUN показывает его и после перезапуска приложения,
    /// иначе фоновый подбор выглядел как «ещё не запускался».
    /// </summary>
    public static void RecordAutotune(string summary, string? quicProbe)
    {
        var settings = Current;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            settings.LastAutotuneSummary = summary;
            settings.LastAutotuneUtc = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(quicProbe))
        {
            settings.LastQuicProbe = quicProbe;
            settings.LastQuicProbeUtc = DateTime.UtcNow;
        }

        Save();
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
