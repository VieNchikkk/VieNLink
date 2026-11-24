namespace VieNLink.Mobile;

/// <summary>
/// Автономное обслуживание MTProto, пока жив процесс приложения: раз в две минуты
/// проверяет, не пора ли собрать каналы (10 минут) или перепроверить прокси (10 минут).
/// Когда процесс убит, тот же проход делает <see cref="Maintenance"/> по будильнику.
/// </summary>
internal static class MtProtoScheduler
{
    private static TimeSpan TickInterval => ProbeProfile.MtProtoTick;
    private static Timer? _timer;
    private static int _busy;

    public static void Start(Android.Content.Context context)
    {
        // На телевизоре Telegram нет: не тратим процессор на сбор и проверку MTProto.
        if (DeviceProfile.IsTelevision)
        {
            return;
        }

        if (_timer is not null || ProbeScheduler.IsProbeProcess() || SettingsStore.Current.AutomationPaused)
        {
            return;
        }

        _timer = new Timer(_ => _ = TickAsync(context), null,
            TimeSpan.FromSeconds(90), TickInterval);
        AppLog.Write("mtproto", $"Автономный сбор MTProto запущен (проверка каждые {TickInterval.TotalMinutes:0} минуты)");
    }

    /// <summary>Останавливает фоновое обслуживание MTProto.</summary>
    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static async Task TickAsync(Android.Content.Context context)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (ProbeScheduler.IsProbeProcess() || MtProtoStore.IsCollecting ||
                SettingsStore.Current.AutomationPaused)
            {
                return;
            }

            var android = AndroidSettingsStore.Current;
            if (!android.AutoMtProto)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var collectMinutes = MtProtoStore.WorkingCount < MtProtoSchedule.MinWorking
                ? MtProtoSchedule.CollectMinutesWhenFew
                : MtProtoSchedule.CollectMinutes;
            if (now - android.LastMtProtoCollectUtc >= TimeSpan.FromMinutes(collectMinutes))
            {
                await MtProtoStore.CollectAsync(null, CancellationToken.None).ConfigureAwait(false);
            }

            android = AndroidSettingsStore.Current;
            if (now - android.LastMtProtoTestUtc >= TimeSpan.FromMinutes(MtProtoSchedule.WorkingRecheckMinutes))
            {
                await MtProtoStore.TestAsync(null, CancellationToken.None, includeDead: false)
                    .ConfigureAwait(false);
            }

            MtProtoStore.Prune();
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Автономная проверка MTProto не удалась", exception);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
