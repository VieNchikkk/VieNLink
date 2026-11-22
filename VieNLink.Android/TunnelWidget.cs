using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Виджет главного экрана: состояние туннеля и две кнопки — «Лучший» и «Отключить».
/// Обновляется из службы при подключении, переключении сервера и остановке.
/// </summary>
[BroadcastReceiver(Name = "com.vienlink.app.TunnelWidget", Exported = false)]
public sealed class TunnelWidget : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
    {
        if (context is null)
        {
            return;
        }

        Update(context);
    }

    /// <summary>Обновляет все экземпляры виджета. Вызывается из службы и экранов.</summary>
    public static void Update(Context context)
    {
        try
        {
            var manager = AppWidgetManager.GetInstance(context);
            if (manager is null)
            {
                return;
            }

            var component = new ComponentName(context.PackageName!, "com.vienlink.app.TunnelWidget");
            var ids = manager.GetAppWidgetIds(component);
            if (ids is null || ids.Length == 0)
            {
                return;
            }

            foreach (var id in ids)
            {
                manager.UpdateAppWidget(id, Build(context));
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("widget", "Не удалось обновить виджет", exception);
        }
    }

    private static RemoteViews Build(Context context)
    {
        var views = new RemoteViews(context.PackageName!, Resource.Layout.widget);
        var state = TunnelService.State;
        var running = state == TunnelState.Running;
        var bypass = TunnelService.IsBypassMode;

        var selected = NodeStore.Selected;
        var exit = NetworkProbe.CachedExit;
        var server = selected is null
            ? "—"
            : TunnelService.ShortServerName(selected) + " · " + selected.DisplayDelay +
              (exit is null ? string.Empty : " · " + exit.Flag + " " + exit.Country);
        var status = bypass
            ? running ? "TUN-обход (бета)" : "Запускаю TUN-обход (бета)…"
            : running
                ? "Подключено · " + server
                : state == TunnelState.Starting
                    ? "Подключаюсь…"
                    : "VPN выключен";
        views.SetTextViewText(Resource.Id.widget_status, status);

        var bestIntent = PendingIntent.GetService(
            context, 10,
            new Intent(context, typeof(TunnelService)).SetAction(TunnelService.ActionBest),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (bestIntent is not null)
        {
            views.SetOnClickPendingIntent(Resource.Id.widget_best, bestIntent);
        }

        var stopIntent = PendingIntent.GetService(
            context, 11,
            new Intent(context, typeof(TunnelService)).SetAction(TunnelService.ActionStop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (stopIntent is not null)
        {
            views.SetOnClickPendingIntent(Resource.Id.widget_stop, stopIntent);
        }

        var openIntent = PendingIntent.GetActivity(
            context, 12,
            new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        if (openIntent is not null)
        {
            views.SetOnClickPendingIntent(Resource.Id.widget_title, openIntent);
        }

        return views;
    }
}
