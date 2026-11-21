using Android.App;
using Android.Content;

namespace VieNLink.Mobile;

/// <summary>
/// Уведомления об обновлении: ход загрузки APK и готовность к установке.
/// Нажатие открывает главный экран, где UpdateCenter повторно проверяет
/// и предлагает установку (Android всё равно спросит подтверждение).
/// </summary>
internal static class UpdateNotifier
{
    private const string ChannelId = "vienlink-updates";
    private const int NotificationId = 7101;

    public static void Progress(Context context, AndroidUpdate.Release release, int percent)
    {
        try
        {
            var builder = Builder(context, release, "Скачивание обновления")
                .SetContentText($"Версия {release.Tag}: {percent}%")
                .SetProgress(100, Math.Clamp(percent, 0, 100), false)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true);

            Notify(context, builder);
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось показать ход загрузки обновления", exception);
        }
    }

    public static void Done(Context context, AndroidUpdate.Release release)
    {
        try
        {
            var builder = Builder(context, release, "Обновление скачано")
                .SetContentText($"Версия {release.Tag} готова к установке — нажмите, чтобы продолжить.")
                .SetAutoCancel(true);
            Notify(context, builder);
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось показать готовность обновления", exception);
        }
    }

    public static void Available(Context context, AndroidUpdate.Release release)
    {
        try
        {
            var builder = Builder(context, release, "Доступно обновление VieNLink")
                .SetContentText($"Версия {release.Tag}. Нажмите, чтобы скачать и установить.")
                .SetAutoCancel(true);
            Notify(context, builder);
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось показать уведомление об обновлении", exception);
        }
    }

    public static void Cancel(Context context)
    {
        try
        {
            Manager(context)?.Cancel(NotificationId);
        }
        catch (Exception)
        {
            // Уведомления могли быть запрещены — отменять нечего.
        }
    }

    private static Notification.Builder Builder(
        Context context,
        AndroidUpdate.Release release,
        string title)
    {
        EnsureChannel(context);
        var intent = new Intent(context, typeof(MainActivity))
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop)
            .PutExtra("checkUpdate", true);
        var pending = PendingIntent.GetActivity(
            context, NotificationId + 1, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new Notification.Builder(context, ChannelId)
            .SetContentTitle(title)
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetWhen(Java.Lang.JavaSystem.CurrentTimeMillis())
            .SetShowWhen(true);
        if (pending is not null)
        {
            builder.SetContentIntent(pending);
        }

        return builder;
    }

    private static void Notify(Context context, Notification.Builder builder)
    {
        var manager = Manager(context);
        if (manager is null || !manager.AreNotificationsEnabled())
        {
            return;
        }

        manager.Notify(NotificationId, builder.Build());
    }

    private static NotificationManager? Manager(Context context) =>
        context.GetSystemService(Context.NotificationService) as NotificationManager;

    private static void EnsureChannel(Context context)
    {
        var manager = Manager(context);
        if (manager is null)
        {
            return;
        }

        var channel = new NotificationChannel(ChannelId, "Обновления VieNLink", NotificationImportance.Default)
        {
            Description = "Проверка новых версий и загрузка APK с GitHub",
        };
        manager.CreateNotificationChannel(channel);
    }
}
