namespace VieNLink.Mobile;

/// <summary>
/// Фоновая проверка узлов: раз в две минуты запускает :probe, пока жив процесс
/// приложения (в том числе при работающем VPN). Основной туннель не трогается.
/// </summary>
internal static class ProbeScheduler
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static Timer? _timer;

    public static void Start(Android.Content.Context context)
    {
        if (_timer is not null || IsProbeProcess())
        {
            return;
        }

        _timer = new Timer(_ => Tick(context), null, TimeSpan.FromSeconds(30), Interval);
        AppLog.Write("probe", "Фоновая проверка узлов запущена (каждые 2 минуты)");
    }

    /// <summary>Определяем процесс по cmdline: ApplicationInfo.ProcessName на Android ненадёжен.</summary>
    public static bool IsProbeProcess()
    {
        try
        {
            var cmd = File.ReadAllText("/proc/self/cmdline").TrimEnd('\0');
            return cmd.EndsWith(":probe", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async void Tick(Android.Content.Context context)
    {
        try
        {
            if (IsProbeProcess() || ProbeClient.IsRunning || TunnelService.IsBypassMode)
            {
                return;
            }

            NodeStore.EnsureLoaded();
            if (!NodeStore.Settings.IdleTestEnabled || NodeStore.Nodes.Count == 0)
            {
                return;
            }

            var summary = await ProbeClient.RunBatchAsync(max: 10, CancellationToken.None)
                .ConfigureAwait(false);
            if (summary.Tested > 0)
            {
                AppLog.Write("probe",
                    $"Фон :probe: рабочих {summary.Working} из {summary.Tested}, " +
                    $"лучший открывает доменов {summary.BestDomains}");
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Фоновая проверка в :probe не удалась", exception);
        }
    }
}
