namespace VieNLink.Mobile;

/// <summary>
/// Автономное обслуживание MTProto, пока жив процесс приложения: раз в две минуты
/// проверяет, не пора ли собрать каналы (10 минут) или перепроверить прокси (10 минут).
/// Когда процесс убит, тот же проход делает <see cref="Maintenance"/> по будильнику.
/// </summary>
internal static class MtProtoScheduler
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(2);
    private static Timer? _timer;
    private static int _busy;

    public static void Start(Android.Content.Context context)
    {
        if (_timer is not null || ProbeScheduler.IsProbeProcess())
        {
            return;
        }

        _timer = new Timer(_ => _ = TickAsync(context), null,
            TimeSpan.FromSeconds(90), TickInterval);
        AppLog.Write("mtproto", "Автономный сбор MTProto запущен (проверка каждые 2 минуты)");
    }

    private static async Task TickAsync(Android.Content.Context context)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (ProbeScheduler.IsProbeProcess() || MtProtoStore.IsCollecting)
            {
                return;
            }

            var android = AndroidSettingsStore.Current;
            if (!android.AutoMtProto)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - android.LastMtProtoCollectUtc >= TimeSpan.FromMinutes(MtProtoSchedule.CollectMinutes))
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
