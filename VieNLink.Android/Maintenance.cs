using Android.App;
using Android.Content;

namespace VieNLink.Mobile;

/// <summary>
/// Автономное обслуживание без открытого окна: обновление устаревших источников,
/// сбор MTProto-прокси через RSS и удаление просроченных записей. Запускается
/// неточным будильником раз в 30 минут — Android сам решает, когда дать процессу время.
/// </summary>
internal static class Maintenance
{
    private const int RequestCode = 4101;

    /// <summary>Сколько партий по 150 узлов проверять за проход, пока нет узла для YouTube.</summary>
    private const int MaintenanceYouTubeBatches = 4;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void EnsureScheduled(Context context)
    {
        try
        {
            var alarm = (AlarmManager?)context.GetSystemService(Context.AlarmService);
            if (alarm is null)
            {
                return;
            }

            var intent = new Intent(context, typeof(MaintenanceReceiver))
                .SetAction("com.vienlink.app.MAINTENANCE");
            var pending = PendingIntent.GetBroadcast(
                context, RequestCode, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            if (pending is null)
            {
                return;
            }

            alarm.SetInexactRepeating(
                AlarmType.RtcWakeup,
                Java.Lang.JavaSystem.CurrentTimeMillis() + 60_000,
                30 * 60_000,
                pending);
            AppLog.Write("maintenance", "Фоновое обслуживание запланировано (каждые 30 минут)");
        }
        catch (Exception exception)
        {
            AppLog.Write("maintenance", "Не удалось запланировать обслуживание", exception);
        }
    }

    public static void Cancel(Context context)
    {
        try
        {
            var alarm = (AlarmManager?)context.GetSystemService(Context.AlarmService);
            var intent = new Intent(context, typeof(MaintenanceReceiver))
                .SetAction("com.vienlink.app.MAINTENANCE");
            var pending = PendingIntent.GetBroadcast(
                context, RequestCode, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            if (alarm is not null && pending is not null)
            {
                alarm.Cancel(pending);
            }
        }
        catch (Exception)
        {
            // Отмена не критична: будильник просто не сработает.
        }
    }

    /// <summary>Один проход обслуживания. Ошибки журналируются, но не прерывают остальные шаги.</summary>
    public static async Task RunAsync(Context context, CancellationToken cancellationToken = default)
    {
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await Task.Run(NodeStore.EnsureLoaded, cancellationToken).ConfigureAwait(false);

            // Проверка новой версии на GitHub: раз в 6 часов, без загрузки APK.
            try
            {
                await UpdateCenter.CheckInBackgroundAsync(context).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write("maintenance", "Проверка обновления не удалась", exception);
            }

            var settings = NodeStore.Settings;
            var android = AndroidSettingsStore.Current;
            var now = DateTime.UtcNow;

            var sourceInterval = settings.AutoRefreshMinutes > 0
                ? TimeSpan.FromMinutes(Math.Clamp(settings.AutoRefreshMinutes, 15, 1440))
                : TimeSpan.FromHours(Math.Clamp(settings.AutoUpdateHours, 1, 168));

            if (settings.AutoUpdateSubscriptions &&
                now - android.LastSourceRefreshUtc >= sourceInterval)
            {
                try
                {
                    await NodeStore.RefreshAsync(null, cancellationToken).ConfigureAwait(false);
                    android.LastSourceRefreshUtc = DateTime.UtcNow;
                    AndroidSettingsStore.Save();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("maintenance", "Фоновое обновление источников не удалось", exception);
                }
            }

            if (settings.AutoDiscoverSources &&
                now - settings.LastDiscoveryUtc >= TimeSpan.FromHours(Math.Clamp(settings.AutoDiscoverHours, 1, 168)))
            {
                try
                {
                    var discovery = await SourceDiscoveryService.DiscoverAsync(
                        NodeStore.SnapshotSources(), null, cancellationToken).ConfigureAwait(false);
                    var added = NodeStore.AddDiscovered(discovery);
                    var mtproto = MtProtoStore.AddDiscovered(discovery.MtProto);
                    settings.LastDiscoveryUtc = DateTime.UtcNow;
                    SettingsStore.Save();
                    AppLog.Write("maintenance",
                        $"Автопоиск источников: новых {added}, узлов {discovery.Nodes.Count}, MTProto {mtproto}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("maintenance", "Автопоиск источников не удался", exception);
                }
            }

            if (android.AutoMtProto && now - android.LastMtProtoCollectUtc >=
                TimeSpan.FromMinutes(MtProtoSchedule.CollectMinutes))
            {
                try
                {
                    await MtProtoStore.CollectAsync(null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("maintenance", "Фоновый сбор MTProto не удался", exception);
                }

                if (now - android.LastMtProtoTestUtc >=
                    TimeSpan.FromMinutes(MtProtoSchedule.WorkingRecheckMinutes))
                {
                    try
                    {
                        await MtProtoStore.TestAsync(null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppLog.Write("maintenance", "Фоновая проверка MTProto не удалась", exception);
                    }
                }
            }

            MtProtoStore.Prune();

            if (android.AutoProbe && settings.IdleTestEnabled && !TunnelService.IsBypassMode)
            {
                try
                {
                    var tested = await ProbeClient.RunBatchAsync(10, cancellationToken).ConfigureAwait(false);
                    if (tested.Tested > 0)
                    {
                        AppLog.Write("maintenance",
                            $"Фоновая проверка: рабочих {tested.Working} из {tested.Tested}");
                    }
                    else if (tested.Error is not null)
                    {
                        AppLog.Write("maintenance", "Фоновая проверка: " + tested.Error);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("maintenance", "Фоновая проверка узлов не удалась", exception);
                }

                // YouTube-узла нет — проверяем партии дальше, чтобы кнопка YouTube
                // не осталась без живого узла. Основная причина: бесплатные узлы умирают.
                if (NodeStore.PickForService(NodeStore.QuickService.YouTube) is null)
                {
                    try
                    {
                        var youtube = await ProbeClient
                            .EnsureServiceNodeAsync(NodeStore.QuickService.YouTube, MaintenanceYouTubeBatches,
                                cancellationToken)
                            .ConfigureAwait(false);
                        AppLog.Write("maintenance", youtube is null
                            ? "YouTube: рабочего узла пока нет, проверка продолжится в фоне"
                            : "YouTube: рабочий узел найден автоматически");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppLog.Write("maintenance", "Автопоиск узла для YouTube не удался", exception);
                    }
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class MaintenanceReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
        {
            return;
        }

        var pending = GoAsync();
        _ = Task.Run(async () =>
        {
            try
            {
                await Maintenance.RunAsync(context).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                VieNLink.AppLog.Write("maintenance", "Ошибка фонового обслуживания", exception);
            }
            finally
            {
                pending?.Finish();
            }
        });
    }
}
