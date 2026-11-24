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

    /// <summary>Сколько партий по 150 серверов проверять за проход, пока нет сервера для YouTube.</summary>
    private const int MaintenanceYouTubeBatches = 3;

    /// <summary>Сколько серверов проверять сразу после первого заполнения списка.</summary>
    private static int StartupProbeBatch => ProbeProfile.StartupBatch;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void EnsureScheduled(Context context)
    {
        try
        {
            if (SettingsStore.Current.AutomationPaused)
            {
                return;
            }

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

            // Один будильник на 30 минут вместо SetInexactRepeating: SetAndAllowWhileIdle
            // срабатывает и в Doze (ColorOS иначе откладывал обслуживание на часы),
            // а следующий ставится при каждом запуске приёмника — цепочка не рвётся,
            // даже если процесс убьют посередине работы.
            var minutes = ProbeProfile.MaintenanceIntervalMinutes;
            var trigger = Java.Lang.JavaSystem.CurrentTimeMillis() + minutes * 60_000L;
            alarm.SetAndAllowWhileIdle(AlarmType.RtcWakeup, trigger, pending);
            AppLog.Write("maintenance", $"Следующее фоновое обслуживание через {minutes} минут");
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
        if (SettingsStore.Current.AutomationPaused)
        {
            return;
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await Task.Run(NodeStore.EnsureLoaded, cancellationToken).ConfigureAwait(false);

            // Сначала применяем результаты, которые :probe успел записать до прошлой
            // остановки процесса — иначе работа в фоне пропадала бы молча.
            await Task.Run(ProbeClient.ApplyPendingResults, cancellationToken).ConfigureAwait(false);

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
            var emptyList = NodeStore.Nodes.Count == 0;
            var counts = NodeStore.Summary();
            // Живых серверов мало (не набрана квота) — списки обновляем чаще: бесплатные
            // серверы вымирают, и старый список уже не даёт рабочих адресов.
            var poorList = !emptyList &&
                           (counts.Working < ProbeProfile.MinWorking ||
                            counts.Youtube < ProbeProfile.MinWorkingPerService ||
                            (!DeviceProfile.IsTelevision &&
                             (counts.Telegram < ProbeProfile.MinWorkingPerService ||
                              counts.Discord < ProbeProfile.MinWorkingPerService)) ||
                            counts.Universal < ProbeProfile.MinWorkingPerService);

            var sourceInterval = settings.AutoRefreshMinutes > 0
                ? TimeSpan.FromMinutes(Math.Clamp(settings.AutoRefreshMinutes, 15, 1440))
                : TimeSpan.FromHours(Math.Clamp(settings.AutoUpdateHours, 1, 168));
            if (poorList)
            {
                sourceInterval = ProbeProfile.SourceRefreshWhenFew;
            }

            if (settings.AutoCollectServers &&
                (emptyList || now - android.LastSourceRefreshUtc >= sourceInterval))
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

            // Первый запуск (или список вычистили): не ждём двухминутного тика,
            // сразу проверяем партии, пока не наберём минимум рабочих серверов.
            if (emptyList && settings.IdleTestEnabled && android.AutoProbe &&
                !TunnelService.IsBypassMode && NodeStore.Nodes.Count > 0 &&
                !ProbeClient.IsManualRun && !ProbeClient.IsPaused)
            {
                try
                {
                    var working = await ProbeClient
                        .EnsureWorkingQuotaAsync(3, StartupProbeBatch, cancellationToken)
                        .ConfigureAwait(false);
                    AppLog.Write("maintenance",
                        $"Стартовая проверка: рабочих {working} из {ProbeProfile.MinWorking}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("maintenance", "Стартовая проверка серверов не удалась", exception);
                }
            }

            var discoveryDue = now - settings.LastDiscoveryUtc >=
                TimeSpan.FromHours(Math.Clamp(settings.AutoDiscoverHours, 1, 168));
            if (!discoveryDue && emptyList &&
                now - settings.LastDiscoveryUtc >= TimeSpan.FromMinutes(30))
            {
                // Пустой список: пробуем искать источники чаще, чем раз в сутки.
                discoveryDue = true;
            }

            if (settings.AutoDiscoverSources && discoveryDue)
            {
                try
                {
                    var discovery = await SourceDiscoveryService.DiscoverAsync(
                        NodeStore.SnapshotSources(), null, cancellationToken,
                        settings.SourceFreshnessHours,
                        includeWeb: !DeviceProfile.IsTelevision).ConfigureAwait(false);
                    var added = NodeStore.AddDiscovered(discovery, settings.AutoCollectServers);
                    var mtproto = MtProtoStore.AddDiscovered(discovery.MtProto);
                    settings.LastDiscoveryUtc = DateTime.UtcNow;
                    SettingsStore.Save();
                    AppLog.Write("maintenance",
                        $"Автопоиск источников: новых {added}, серверов {discovery.Nodes.Count}, MTProto {mtproto}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("maintenance", "Автопоиск источников не удался", exception);
                }
            }

            if (android.AutoMtProto && !DeviceProfile.IsTelevision)
            {
                var shouldCollect = now - android.LastMtProtoCollectUtc >=
                                    TimeSpan.FromMinutes(
                                        MtProtoStore.WorkingCount < MtProtoSchedule.MinWorking
                                            ? MtProtoSchedule.CollectMinutesWhenFew
                                            : MtProtoSchedule.CollectMinutes) ||
                                    MtProtoStore.Snapshot().Count == 0;
                if (shouldCollect)
                {
                    try
                    {
                        await MtProtoStore.CollectAsync(null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppLog.Write("maintenance", "Фоновый сбор MTProto не удался", exception);
                    }
                }

                // Нет ни одного проверенного прокси — проверяем, не дожидаясь интервала.
                var needTest = now - android.LastMtProtoTestUtc >=
                                   TimeSpan.FromMinutes(MtProtoSchedule.WorkingRecheckMinutes) ||
                               MtProtoStore.WorkingCount == 0;
                if (needTest)
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

            if (android.AutoProbe && settings.IdleTestEnabled && !TunnelService.IsBypassMode &&
                !ProbeClient.IsPaused && !ProbeClient.IsManualRun)
            {
                try
                {
                    // Сначала стартовая перепроверка рабочих: поиск не должен её вытеснять,
                    // иначе счётчики долго висят с устаревшими данными.
                    await ProbeScheduler.EnsureStartupRecheckAsync(cancellationToken).ConfigureAwait(false);

                    // Минимум десять рабочих серверов: пока их меньше, проверяем партии подряд.
                    var working = await ProbeClient
                        .EnsureWorkingQuotaAsync(3, NodeTester.MaxBatch, cancellationToken)
                        .ConfigureAwait(false);
                    AppLog.Write("maintenance",
                        $"Фоновая проверка: рабочих {working} из {ProbeProfile.MinWorking}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("maintenance", "Фоновая проверка серверов не удалась", exception);
                }

                // YouTube-сервера нет — проверяем партии дальше, чтобы кнопка YouTube
                // не осталась без живого сервера. Основная причина: бесплатные серверы умирают.
                if (NodeStore.PickForService(NodeStore.QuickService.YouTube) is null)
                {
                    try
                    {
                        var youtube = await ProbeClient
                            .EnsureServiceNodeAsync(NodeStore.QuickService.YouTube, MaintenanceYouTubeBatches,
                                cancellationToken)
                            .ConfigureAwait(false);
                        AppLog.Write("maintenance", youtube is null
                            ? "YouTube: рабочего сервера пока нет, проверка продолжится в фоне"
                            : "YouTube: рабочий сервер найден автоматически");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppLog.Write("maintenance", "Автопоиск сервера для YouTube не удался", exception);
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

        // В OnReceive нельзя читать настройки, планировать будильник и тем более
        // запускать обслуживание: это главный поток, а полный проход (источники,
        // MTProto, пробная партия) идёт дольше лимита broadcast. Из-за этого система
        // показывала ANR «Broadcast of Intent … MAINTENANCE», и приложение зависало
        // после возврата из фона. Приёмник сразу уходит, работа — в фоне.
        var appContext = context.ApplicationContext;
        _ = Task.Run(async () =>
        {
            try
            {
                if (SettingsStore.Current.AutomationPaused)
                {
                    // Автоматика выключена пользователем: не будим процесс
                    // и не ставим следующий будильник.
                    return;
                }

                // Следующий будильник ставим сразу: если процесс убьют во время
                // долгой проверки, цепочка обслуживания продолжится.
                Maintenance.EnsureScheduled(appContext);
                await Maintenance.RunAsync(appContext).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                VieNLink.AppLog.Write("maintenance", "Ошибка фонового обслуживания", exception);
            }
        });
    }
}
