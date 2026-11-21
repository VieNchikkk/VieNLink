using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Service.QuickSettings;

namespace VieNLink.Mobile;

/// <summary>
/// Плитка быстрых настроек: включить или выключить туннель одним касанием.
/// Если разрешение VPN ещё не выдано, открываем главный экран.
/// </summary>
[Service(Permission = "android.permission.BIND_QUICK_SETTINGS_TILE", Exported = true)]
[IntentFilter(new[] { "android.service.quicksettings.action.QS_TILE" })]
public sealed class TunnelTileService : TileService
{
    public override void OnStartListening() => UpdateTile();

    public override void OnClick()
    {
        if (TunnelService.State == TunnelState.Running)
        {
            VpnStarter.Stop(this);
        }
        else if (Android.Net.VpnService.Prepare(this) is not null)
        {
            OpenMainActivity();
        }
        else
        {
            VpnStarter.StartAuto(this);
        }

        UpdateTile();
    }

    private void OpenMainActivity()
    {
        var intent = new Intent(this, typeof(MainActivity))
            .AddFlags(ActivityFlags.NewTask);

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            {
                var pending = PendingIntent.GetActivity(
                    this, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
                if (pending is not null)
                {
                    StartActivityAndCollapse(pending);
                }
            }
            else
            {
                StartActivityAndCollapse(intent);
            }
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "плитка: не удалось открыть экран: " + exception.Message);
        }
    }

    private void UpdateTile()
    {
        var tile = QsTile;
        if (tile is null)
        {
            return;
        }

        var running = TunnelService.State == TunnelState.Running;
        tile.State = running ? TileState.Active : TileState.Inactive;
        tile.Label = running ? "VieNLink: включён" : "VieNLink";
        tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.ic_notification);
        tile.UpdateTile();
    }
}
