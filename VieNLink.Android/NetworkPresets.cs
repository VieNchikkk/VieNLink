using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.Telephony;

namespace VieNLink.Mobile;

/// <summary>Снимок настроек обхода для текущей сети — для журнала и диагностики.</summary>
internal sealed record NetworkSnapshot(
    string Label,
    string Key,
    string Details,
    string Page,
    string PageSource,
    string Video,
    string VideoSource,
    bool BlockQuic,
    string BlockQuicSource)
{
    public string Describe =>
        $"{Label} · ключ «{(Key.Length > 0 ? Key : "—")}» · сети: {Details} · " +
        $"страница {(string.IsNullOrWhiteSpace(Page) ? "—" : Page)} ({PageSource}) · " +
        $"видео {(string.IsNullOrWhiteSpace(Video) ? "как для страниц" : Video)} ({VideoSource}) · " +
        $"QUIC {(BlockQuic ? "блокируется" : "через релей")} ({BlockQuicSource})";
}

/// <summary>
/// Пресеты стратегий TUN-обхода по сети. У домашнего Wi-Fi и у каждого мобильного оператора
/// разные ТСПУ и разные рабочие стратегии, поэтому помним отдельно для каждой сети:
/// ключ — «wifi:&lt;SSID&gt;» или «cell:&lt;MCCMNC&gt;:&lt;оператор&gt;».
/// Приоритет: пресет этой сети → база по операторам (на устройстве, затем зашитая) →
/// пресет типа сети → общая стратегия. Wi-Fi SSID в базу/экспорт не попадают.
/// </summary>
internal static class NetworkPresets
{
    /// <summary>
    /// Ключ пресета текущей физической сети. Пустая строка — сеть не определена
    /// (только что включили VPN, нет соединения): тогда ничего не сохраняем,
    /// чтобы не записать стратегию в чужой пресет.
    /// </summary>
    public static string Key(Context context)
    {
        var (wifi, _) = Detect(context);
        if (wifi is null)
        {
            return string.Empty;
        }

        if (wifi.Value)
        {
            var ssid = WifiSsid(context);
            return string.IsNullOrWhiteSpace(ssid) ? "wifi" : "wifi:" + ssid;
        }

        var (code, _) = MobileProvider(context);
        // Ключ — только MCCMNC. Имя оператора зависит от локали и прошивки («МТС» / «MTS RUS»,
        // «МегаФон» / «MegaFon»), из-за него одна сеть распадалась на разные записи и не
        // совпадала с базой, собранной на другом телефоне. Имя хранится отдельно.
        return string.IsNullOrWhiteSpace(code) ? "cell" : "cell:" + code;
    }

    /// <summary>Имя оператора для записи в базе и журнала (пусто для Wi-Fi).</summary>
    public static string ProviderName(Context context)
    {
        var (wifi, _) = Detect(context);
        if (wifi is not false)
        {
            return string.Empty;
        }

        var (_, name) = MobileProvider(context);
        return name.Trim();
    }

    /// <summary>
    /// Ключ сети, на которой запущен обход: подтверждения пишем ей, даже если сеть
    /// переключилась уже после запуска (иначе запись LTE уезжала в пресет Wi-Fi).
    /// </summary>
    public static string RecordKey(Context context)
    {
        var started = ForwarderHost.NetworkKey;
        return string.IsNullOrWhiteSpace(started) ? Key(context) : started;
    }

    /// <summary>Ручной выбор пользователя: в базе помечается как manual.</summary>
    public static void RememberChoice(Context context, string page, string video, bool blockQuic) =>
        ProviderPresetStore.RecordChoice(RecordKey(context), page, video, blockQuic,
            ProviderName(context));

    /// <summary>Подбор подтвердил стратегию: страница и превью, либо реальный поток видео.</summary>
    public static void RememberSuccess(Context context, string page, string video, bool blockQuic,
        string transport = "", int videoKbps = 0) =>
        ProviderPresetStore.RecordSuccess(RecordKey(context), page, video, blockQuic,
            transport, videoKbps, ProviderName(context));

    /// <summary>Видео реально шло через обход: отмечаем транспорт, подтверждения не растут.</summary>
    public static void RememberVideo(Context context, string page, string video, bool blockQuic,
        string transport) =>
        ProviderPresetStore.RecordVideo(RecordKey(context), page, video, blockQuic,
            transport, ProviderName(context));

    public static bool IsWifi(Context context) => Detect(context).Wifi ?? true;

    /// <summary>Название текущей сети для интерфейса и журнала.</summary>
    public static string Label(Context context)
    {
        var (wifi, _) = Detect(context);
        if (wifi is null)
        {
            return "сеть не определена";
        }

        if (wifi.Value)
        {
            var ssid = WifiSsid(context);
            return string.IsNullOrWhiteSpace(ssid) ? "Wi-Fi" : "Wi-Fi · " + ssid;
        }

        var (_, name) = MobileProvider(context);
        return string.IsNullOrWhiteSpace(name) ? "мобильная сеть" : "мобильная сеть · " + name;
    }

    /// <summary>Полный снимок: что за сеть, чей пресет и что из него загружено.</summary>
    public static NetworkSnapshot Snapshot(Context context)
    {
        var settings = AndroidSettingsStore.Current;
        var (wifi, details) = Detect(context);
        var key = Key(context);
        var label = Label(context);

        var (page, pageSource) = ResolvePage(settings, key, wifi);
        var (video, videoSource) = ResolveVideo(settings, key, wifi);
        var (blockQuic, quicSource) = ResolveQuic(settings, key, wifi);

        return new NetworkSnapshot(label, key, details, page, pageSource, video, videoSource,
            blockQuic, quicSource);
    }

    /// <summary>Строка для журнала: сеть, ключ, найденные физические сети и источники значений.</summary>
    public static string Describe(Context context) => Snapshot(context).Describe;

    /// <summary>
    /// Сохранённая стратегия для текущей сети; если для этой сети её ещё нет — пресет типа
    /// сети, затем последняя использованная (общая).
    /// </summary>
    public static string ResolvePage(Context context)
    {
        var saved = GetPage(context);
        return string.IsNullOrWhiteSpace(saved) ? AndroidSettingsStore.Current.TunBypassStrategy : saved;
    }

    public static string GetPage(Context context) => Snapshot(context).Page;

    public static void SetPage(Context context, string mode)
    {
        var settings = AndroidSettingsStore.Current;
        var key = Key(context);
        if (key.Length > 0)
        {
            Preset(settings, key).Page = mode;
        }

        var (wifi, _) = Detect(context);
        if (wifi is true)
        {
            settings.TunBypassStrategyWifi = mode;
        }
        else if (wifi is false)
        {
            settings.TunBypassStrategyMobile = mode;
        }

        // Общая — последняя использованная: по ней стартует сеть, для которой пресета ещё нет.
        settings.TunBypassStrategy = mode;
        AndroidSettingsStore.Save();
    }

    public static string GetVideo(Context context) => Snapshot(context).Video;

    public static void SetVideo(Context context, string mode)
    {
        var settings = AndroidSettingsStore.Current;
        var key = Key(context);
        if (key.Length > 0)
        {
            Preset(settings, key).Video = mode;
        }

        var (wifi, _) = Detect(context);
        if (wifi is true)
        {
            settings.TunBypassVideoStrategyWifi = mode;
        }
        else if (wifi is false)
        {
            settings.TunBypassVideoStrategyMobile = mode;
        }

        AndroidSettingsStore.Save();
    }

    /// <summary>
    /// Блокировать ли QUIC на текущей сети: выбор для этой сети важнее базы оператора,
    /// база — важнее общей галочки. Разные операторы режут UDP/443 по-разному.
    /// </summary>
    public static bool GetBlockQuic(Context context) => Snapshot(context).BlockQuic;

    public static void SetBlockQuic(Context context, bool blockQuic)
    {
        var settings = AndroidSettingsStore.Current;
        var key = Key(context);
        if (key.Length > 0)
        {
            Preset(settings, key).BlockQuic = blockQuic;
        }

        AndroidSettingsStore.Save();

        // Общая галочка — значение по умолчанию для сетей, где решения ещё нет.
        var common = SettingsStore.Current;
        common.TunBypassBlockQuic = blockQuic;
        SettingsStore.Save();
    }

    private static (string Value, string Source) ResolvePage(
        AndroidSettings settings, string key, bool? wifi)
    {
        var local = FindPreset(settings, key);
        if (local is not null && !string.IsNullOrWhiteSpace(local.Page))
        {
            return (local.Page, "локальный пресет сети");
        }

        var provider = ProviderPresetStore.Find(key, out var origin);
        if (provider is not null && !string.IsNullOrWhiteSpace(provider.Page))
        {
            return (provider.Page, Base(origin));
        }

        var typeValue = (wifi ?? true) ? settings.TunBypassStrategyWifi : settings.TunBypassStrategyMobile;
        if (!string.IsNullOrWhiteSpace(typeValue))
        {
            return (typeValue, "пресет типа сети");
        }

        return !string.IsNullOrWhiteSpace(settings.TunBypassStrategy)
            ? (settings.TunBypassStrategy, "общая стратегия")
            : (string.Empty, "по умолчанию (Oob)");
    }

    private static (string Value, string Source) ResolveVideo(
        AndroidSettings settings, string key, bool? wifi)
    {
        // Пустое значение в пресете сети — осознанное «как для страниц», поэтому без записи
        // в пресет сети к запасным вариантам не откатываемся.
        var localVideo = FindPreset(settings, key);
        if (localVideo is not null)
        {
            return (localVideo.Video, "локальный пресет сети");
        }

        var provider = ProviderPresetStore.Find(key, out var origin);
        if (provider is not null)
        {
            return (provider.Video, Base(origin));
        }

        var typeValue = (wifi ?? true)
            ? settings.TunBypassVideoStrategyWifi
            : settings.TunBypassVideoStrategyMobile;
        return (typeValue, "пресет типа сети");
    }

    private static (bool Value, string Source) ResolveQuic(
        AndroidSettings settings, string key, bool? wifi)
    {
        var local = FindPreset(settings, key);
        if (local?.BlockQuic is bool localValue)
        {
            return (localValue, "локальный пресет сети");
        }

        var provider = ProviderPresetStore.Find(key, out var origin);
        if (provider is not null)
        {
            return (provider.BlockQuic, Base(origin));
        }

        _ = wifi;
        return (SettingsStore.Current.TunBypassBlockQuic, "общая настройка");
    }

    /// <summary>Откуда взята запись базы операторов: видно в «Данных обхода».</summary>
    private static string Base(PresetOrigin origin) => origin switch
    {
        PresetOrigin.Remote => "база операторов (обновляемая)",
        PresetOrigin.Shipped => "база операторов (зашитая)",
        _ => "база операторов (на устройстве)",
    };

    /// <summary>
    /// Пресет сети с учётом старых ключей: до 1.3.55 ключ мобильной сети содержал имя
    /// оператора («cell:25002:МегаФон»), поэтому ищем и по префиксу MCCMNC.
    /// </summary>
    private static BypassPreset? FindPreset(AndroidSettings settings, string key)
    {
        if (key.Length == 0 || settings.BypassPresets is null)
        {
            return null;
        }

        if (settings.BypassPresets.TryGetValue(key, out var preset))
        {
            return preset;
        }

        if (!key.StartsWith("cell:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in settings.BypassPresets)
        {
            if (pair.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static BypassPreset Preset(AndroidSettings settings, string key)
    {
        settings.BypassPresets ??= new Dictionary<string, BypassPreset>();
        if (settings.BypassPresets.TryGetValue(key, out var preset))
        {
            return preset;
        }

        // Старую запись с именем оператора в ключе переносим на чистый MCCMNC,
        // чтобы стратегия этой сети не потерялась после обновления.
        if (key.StartsWith("cell:", StringComparison.OrdinalIgnoreCase))
        {
            var legacyKey = settings.BypassPresets.Keys.FirstOrDefault(item =>
                item.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            if (legacyKey is not null)
            {
                var migrated = settings.BypassPresets[legacyKey];
                settings.BypassPresets.Remove(legacyKey);
                settings.BypassPresets[key] = migrated;
                AppLog.Write("tun-bypass", $"Пресет сети {legacyKey} перенесён на ключ {key}");
                return migrated;
            }
        }

        preset = new BypassPreset();
        settings.BypassPresets[key] = preset;
        return preset;
    }

    /// <summary>
    /// Тип физической сети и список найденных сетей для журнала. При поднятом VPN активная
    /// сеть — сам VPN (в выводе Android бывает «CELLULAR|VPN»), поэтому перебираем все сети,
    /// пропуская VPN и служебные (IMS без INTERNET). Валидированная сеть важнее: телефон
    /// может держать Wi-Fi без интернета и одновременно LTE.
    /// </summary>
    private static (bool? Wifi, string Details) Detect(Context context)
    {
        try
        {
            var manager = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
            if (manager is null)
            {
                return (null, "нет ConnectivityManager");
            }

            var seen = new List<string>();
            var vpnSeen = false;
            var unvalidatedWifi = false;
            var unvalidatedCellular = false;
            bool? validated = null;
            foreach (var network in manager.GetAllNetworks())
            {
                var capabilities = manager.GetNetworkCapabilities(network);
                if (capabilities is null)
                {
                    continue;
                }

                var transports = Transports(capabilities);
                if (capabilities.HasTransport(TransportType.Vpn))
                {
                    vpnSeen = true;
                    continue;
                }

                if (!capabilities.HasCapability(NetCapability.Internet))
                {
                    seen.Add(transports + " без интернета");
                    continue;
                }

                var wifi = capabilities.HasTransport(TransportType.Wifi) ||
                           capabilities.HasTransport(TransportType.Ethernet);
                if (!wifi && !capabilities.HasTransport(TransportType.Cellular))
                {
                    continue;
                }

                var valid = capabilities.HasCapability(NetCapability.Validated);
                seen.Add(transports + (valid ? " VALIDATED" : " (не проверена)"));

                // Проверенная сеть с настоящим интернетом решает: среди них Wi-Fi приоритетнее.
                if (valid && validated is null)
                {
                    validated = wifi;
                }

                if (wifi)
                {
                    unvalidatedWifi = true;
                }
                else
                {
                    unvalidatedCellular = true;
                }
            }

            if (vpnSeen)
            {
                seen.Add("VPN (пропущен)");
            }

            var details = seen.Count == 0 ? "физических сетей нет" : string.Join(", ", seen);
            if (validated is not null)
            {
                return (validated, details);
            }

            if (unvalidatedWifi)
            {
                return (true, details);
            }

            return unvalidatedCellular ? (false, details) : (null, details);
        }
        catch (Exception exception)
        {
            return (null, "ошибка определения: " + exception.Message);
        }
    }

    private static string Transports(NetworkCapabilities capabilities)
    {
        if (capabilities.HasTransport(TransportType.Wifi))
        {
            return "WIFI";
        }

        if (capabilities.HasTransport(TransportType.Ethernet))
        {
            return "ETHERNET";
        }

        if (capabilities.HasTransport(TransportType.Cellular))
        {
            return "CELLULAR";
        }

        return "OTHER";
    }

    private static string WifiSsid(Context context)
    {
        try
        {
            var manager = (WifiManager?)context.GetSystemService(Context.WifiService);
            var ssid = manager?.ConnectionInfo?.SSID;
            if (string.IsNullOrWhiteSpace(ssid) ||
                ssid.Contains("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return ssid.Trim().Trim('"');
        }
        catch (Exception)
        {
            // Android 10+ отдаёт SSID только с разрешением на геолокацию: без него — общий ключ.
            return string.Empty;
        }
    }

    private static (string Code, string Name) MobileProvider(Context context)
    {
        try
        {
            var telephony = (TelephonyManager?)context.GetSystemService(Context.TelephonyService);
            if (telephony is null)
            {
                return (string.Empty, string.Empty);
            }

            var name = telephony.SimOperatorName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = telephony.NetworkOperatorName;
            }

            var code = telephony.SimOperator;
            if (string.IsNullOrWhiteSpace(code))
            {
                code = telephony.NetworkOperator;
            }

            return (code ?? string.Empty, name ?? string.Empty);
        }
        catch (Exception)
        {
            return (string.Empty, string.Empty);
        }
    }
}
