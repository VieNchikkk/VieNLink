using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;

namespace VieNLink.Mobile;

/// <summary>
/// Запуск VPN одинаково из всех экранов: разрешение <c>VpnService.Prepare</c>,
/// foreground-служба и передача готового конфига sing-box.
/// </summary>
internal static class VpnStarter
{
    /// <summary>Намерение запроса разрешения VPN; null — разрешение уже выдано.</summary>
    public static Intent? PrepareRequest(Activity activity) => VpnService.Prepare(activity);

    public static void Start(Context context, string configJson)
    {
        var intent = new Intent(context, typeof(TunnelService)).SetAction(TunnelService.ActionStart);
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            intent.PutExtra(TunnelService.ExtraConfig, configJson);
        }

        StartForeground(context, intent);
    }

    /// <summary>Подключение без готового конфига: служба сама выберет узел и соберёт конфиг.</summary>
    public static void StartAuto(Context context) =>
        StartForeground(context, new Intent(context, typeof(TunnelService)).SetAction(TunnelService.ActionAuto));

    /// <summary>TUN-обход без узла: служба поднимет форвардер и TUN-конфиг.</summary>
    public static void StartBypass(Context context, StreamFragmentMode mode) =>
        StartForeground(context, new Intent(context, typeof(TunnelService))
            .SetAction(TunnelService.ActionBypass)
            .PutExtra(TunnelService.ExtraStrategy, mode.ToString()));

    private static void StartForeground(Context context, Intent intent)
    {
        // Начиная с Android 8 службу с уведомлением запускают через StartForegroundService.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public static void Stop(Context context)
    {
        var intent = new Intent(context, typeof(TunnelService)).SetAction(TunnelService.ActionStop);

        // Остановка не показывает уведомление: StartForegroundService здесь привёл бы
        // к падению «не вызван startForeground». Приложение на экране — StartService можно.
        context.StartService(intent);
    }

    /// <summary>Человеческое описание состояния службы для экранов.</summary>
    public static string StateLabel => TunnelService.State switch
    {
        TunnelState.Running => "Туннель работает",
        TunnelState.Starting => "Подключаюсь…",
        _ => "VPN не подключён",
    };
}
