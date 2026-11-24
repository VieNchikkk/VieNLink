using Android.Content;
using Android.Net;

namespace VieNLink.Mobile;

/// <summary>
/// Пресеты стратегий TUN-обхода по типу сети. У домашнего Wi-Fi и мобильного оператора
/// разные ТСПУ и разные рабочие стратегии, поэтому храним их отдельно (как советует ByeByeDPI).
/// </summary>
internal static class NetworkPresets
{
    public static bool IsWifi(Context context)
    {
        try
        {
            var manager = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
            var capabilities = manager?.GetNetworkCapabilities(manager.ActiveNetwork);
            return capabilities?.HasTransport(TransportType.Wifi) == true;
        }
        catch (Exception)
        {
            // Не смогли определить — считаем Wi-Fi, чтобы не менять привычное поведение.
            return true;
        }
    }

    public static string Label(Context context) => IsWifi(context) ? "Wi-Fi" : "мобильная сеть";

    public static string GetPage(Context context)
    {
        var settings = AndroidSettingsStore.Current;
        return IsWifi(context) ? settings.TunBypassStrategyWifi : settings.TunBypassStrategyMobile;
    }

    public static void SetPage(Context context, string mode)
    {
        var settings = AndroidSettingsStore.Current;
        if (IsWifi(context))
        {
            settings.TunBypassStrategyWifi = mode;
        }
        else
        {
            settings.TunBypassStrategyMobile = mode;
        }

        settings.TunBypassStrategy = mode;
        AndroidSettingsStore.Save();
    }

    public static string GetVideo(Context context)
    {
        var settings = AndroidSettingsStore.Current;
        return IsWifi(context)
            ? settings.TunBypassVideoStrategyWifi
            : settings.TunBypassVideoStrategyMobile;
    }

    public static void SetVideo(Context context, string mode)
    {
        var settings = AndroidSettingsStore.Current;
        if (IsWifi(context))
        {
            settings.TunBypassVideoStrategyWifi = mode;
        }
        else
        {
            settings.TunBypassVideoStrategyMobile = mode;
        }

        AndroidSettingsStore.Save();
    }
}
