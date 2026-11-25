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

    /// <summary>Подключение без готового конфига: служба сама выберет сервер и соберёт конфиг.</summary>
    public static void StartAuto(Context context) =>
        StartForeground(context, new Intent(context, typeof(TunnelService)).SetAction(TunnelService.ActionAuto));

    /// <summary>
    /// Включение «как в прошлый раз»: если последним был TUN-обход — поднимаем его
    /// с пресетом текущей сети, иначе обычное подключение к серверу.
    /// </summary>
    public static void StartPreferred(Context context)
    {
        if (AndroidSettingsStore.Current.LastRunWasBypass)
        {
            var mode = Enum.TryParse<StreamFragmentMode>(NetworkPresets.ResolvePage(context), out var parsed)
                ? parsed
                : StreamFragmentMode.Oob;
            StartBypass(context, mode);
            return;
        }

        StartAuto(context);
    }

    /// <summary>TUN-обход без сервера: служба поднимет форвардер и TUN-конфиг.</summary>
    public static void StartBypass(Context context, StreamFragmentMode mode) =>
        StartForeground(context, new Intent(context, typeof(TunnelService))
            .SetAction(TunnelService.ActionBypass)
            .PutExtra(TunnelService.ExtraStrategy, mode.ToString()));

    private static void StartForeground(Context context, Intent intent)
    {
        // Любой запуск (обход или сервер) отменяет идущий подбор стратегии: иначе автоподбор
        // продолжал бы крутить свои форвардеры поверх уже поднятого туннеля.
        StrategyAutotune.CancelActive();

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

    /// <summary>TUN-обход сейчас работает.</summary>
    public static bool IsBypassRunning =>
        TunnelService.State == TunnelState.Running && TunnelService.IsBypassMode;

    /// <summary>Прокси-сервер сейчас работает.</summary>
    public static bool IsServerRunning =>
        TunnelService.State == TunnelState.Running && !TunnelService.IsBypassMode;

    /// <summary>Режимы взаимоисключающие: обход нельзя включать поверх сервера.</summary>
    public static bool CanStartBypass(out string reason)
    {
        if (IsServerRunning)
        {
            reason = "Сначала отключите прокси-сервер";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Сервер нельзя включать поверх TUN-обхода.</summary>
    public static bool CanStartServer(out string reason)
    {
        if (IsBypassRunning)
        {
            reason = "Сначала выключите TUN-обход";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Человеческое описание состояния службы для экранов.</summary>
    public static string StateLabel => TunnelService.State switch
    {
        TunnelState.Running => "Туннель работает",
        TunnelState.Starting => "Подключаюсь…",
        _ => "VPN не подключён",
    };
}
