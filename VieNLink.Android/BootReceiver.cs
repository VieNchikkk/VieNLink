using Android.App;
using Android.Content;

namespace VieNLink.Mobile;

/// <summary>
/// Автозапуск после загрузки устройства: если в настройках включено, поднимаем туннель
/// без открытия приложения. Конфиг собирает сама служба (ActionAuto), чтобы приёмник
/// не держал процесс ради чтения кэша узлов.
/// </summary>
[BroadcastReceiver(Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted })]
public sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionBootCompleted)
        {
            return;
        }

        if (!AndroidSettingsStore.Current.AutoConnectOnBoot)
        {
            return;
        }

        try
        {
            // Без ранее выданного разрешения VPN служба всё равно не поднимется.
            if (Android.Net.VpnService.Prepare(context) is not null)
            {
                AppLog.Write("android", "Автозапуск: разрешение VPN не выдано");
                return;
            }

            AppLog.Write("android", "Автозапуск: поднимаю туннель после загрузки");
            VpnStarter.StartAuto(context);
        }
        catch (Exception exception)
        {
            // На части прошивок запуск foreground-службы из BOOT_COMPLETED запрещён —
            // приложение просто останется выключенным до ручного запуска.
            AppLog.Write("android", "Автозапуск после загрузки не удался", exception);
            Android.Util.Log.Warn("VieNLink", "автозапуск: " + exception.Message);
        }
    }
}
