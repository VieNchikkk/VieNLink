namespace VieNLink.Mobile;

/// <summary>
/// Фоновая проверка серверов: такт раз в минуту. Пока живы прошлые результаты, каждый
/// такт перепроверяет только рабочие серверы, у которых истекает окно свежести (5 минут);
/// поиск новых серверов идёт отдельно, реже (2–5 минут). Основной туннель не трогается.
/// </summary>
internal static class ProbeScheduler
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>Как часто искать новые серверы: чаще при недоборе квоты, реже когда всё в порядке.</summary>
    private static readonly TimeSpan SearchInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SearchIntervalWhenFew = TimeSpan.FromMinutes(2);

    /// <summary>Сколько партий по 150 серверов проверять за такт, пока нет сервера для YouTube.</summary>
    private const int YouTubeBatches = 2;

    /// <summary>Сколько рабочих серверов перепроверять за минутный такт.</summary>
    private const int RecheckBatch = 20;

    private static Timer? _timer;
    private static DateTime _lastSearchUtc = DateTime.MinValue;
    private static int _busy;

    /// <summary>Отсев перепроверяем раз в 5 минут — тем же путём, что и кнопка.</summary>
    private static DateTime _lastRejectedAutoCheckUtc = DateTime.MinValue;

    /// <summary>
    /// Первый такт после запуска: сначала перепроверяем рабочие серверы из их файла,
    /// чтобы счётчики показывали текущее состояние, и только потом ищем новые.
    /// </summary>
    private static int _startupRecheckDone;

    /// <summary>Один проход стартовой перепроверки: параллельные вызовы ждут первого.</summary>
    private static readonly SemaphoreSlim StartupRecheckGate = new(1, 1);

    public static void Start(Android.Content.Context context)
    {
        if (_timer is not null || IsProbeProcess() || SettingsStore.Current.AutomationPaused)
        {
            return;
        }

        // Первый такт через 2 секунды: после запуска счётчики должны наполниться
        // свежими проверками, а не показывать ноль до первой минуты.
        _timer = new Timer(_ => Tick(context), null, TimeSpan.FromSeconds(2), TickInterval);
        AppLog.Write("probe", "Фоновая проверка серверов запущена (такт 1 минута, серверы — каждые 5 минут)");

        // Результаты, записанные :probe перед тем, как основной процесс убили,
        // применяем при старте: фоновая работа не должна пропадать.
        _ = Task.Run(ProbeClient.ApplyPendingResults);
    }

    /// <summary>Останавливает фоновую проверку (кнопка «Остановить всю автоматику»).</summary>
    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Один раз при запуске перепроверяет ВСЕХ рабочих из их файла и ждёт конца. Поиск новых
    /// (Maintenance, квоты) должен дождаться её — иначе он занимает :probe первым, и рабочие
    /// долго висят с устаревшими счётчиками. Идемпотентна и потокобезопасна: параллельные
    /// вызовы (планировщик, Maintenance, быстрые счётчики) ждут первого, а не запускают своё.
    /// </summary>
    public static async Task EnsureStartupRecheckAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref _startupRecheckDone) != 0)
        {
            return;
        }

        await StartupRecheckGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _startupRecheckDone) != 0)
            {
                return;
            }

            // Если :probe вдруг занят чужой партией, ждём и пробуем снова: иначе речек был бы
            // помечен сделанным, хотя ничего не проверил.
            NodeTestSummary startup;
            for (var attempt = 0; ; attempt++)
            {
                startup = await ProbeClient
                    .RunBatchAsync(NodeTester.MaxBatch, token, workingOnly: true, allWorking: true)
                    .ConfigureAwait(false);
                if (startup.Error != ProbeClient.BusyError || attempt >= 10 || token.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(600, token).ConfigureAwait(false);
            }

            AppLog.Write("probe",
                $"Старт: рабочие перепроверены — {startup.Tested}, живых {startup.Working}");
            Volatile.Write(ref _startupRecheckDone, 1);
        }
        finally
        {
            StartupRecheckGate.Release();
        }
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

    /// <summary>
    /// Автоматически «нажимает» кнопку «Перепроверить всё» раз в 5 минут: тот же
    /// RejectedCheckService, что и у кнопки. Успешные серверы возвращаются в рабочие,
    /// остальные остаются в отсеве. При включённом VPN проверка откладывается.
    /// </summary>
    private static async Task AutoRecheckRejectedAsync()
    {
        if (DateTime.UtcNow - _lastRejectedAutoCheckUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastRejectedAutoCheckUtc = DateTime.UtcNow;
        if (SettingsStore.Current.AutomationPaused ||
            !NodeStore.Settings.IdleTestEnabled ||
            ProbeClient.IsPaused ||
            ProbeClient.IsRunning ||
            NodeTester.IsRunning ||
            RejectedCheckService.IsRunning)
        {
            return;
        }

        if (TunnelService.State == TunnelState.Running || CoreRuntime.IsRunning)
        {
            return;
        }

        if (RejectedNodeStore.Load().Count == 0)
        {
            return;
        }

        var result = await RejectedCheckService
            .RecheckAsync(null, CancellationToken.None)
            .ConfigureAwait(false);
        AppLog.Write("probe", result.Error ??
            $"Автоперепроверка отсева: проверено {result.Tested}, возвращено {result.Restored}");
    }

    private static async void Tick(Android.Content.Context context)
    {
        // Такт длиннее минуты (партия 150 через :probe) не должен наслаиваться
        // на следующий: иначе два прохода одновременно тратят трафик.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (IsProbeProcess() || ProbeClient.IsRunning || TunnelService.IsBypassMode ||
                ProbeClient.IsPaused || ProbeClient.IsManualRun || SettingsStore.Current.AutomationPaused)
            {
                return;
            }

            NodeStore.EnsureLoaded();
            if (!NodeStore.Settings.IdleTestEnabled ||
                !AndroidSettingsStore.Current.AutoProbe ||
                NodeStore.Nodes.Count == 0)
            {
                return;
            }

            // Порядок на запуске: сначала рабочие из своего файла — живы ли они сейчас.
            // Только после этого имеет смысл искать новые и считать квоты.
            if (Volatile.Read(ref _startupRecheckDone) == 0)
            {
                await EnsureStartupRecheckAsync().ConfigureAwait(false);
                return;
            }

            await AutoRecheckRejectedAsync().ConfigureAwait(false);

            var counts = NodeStore.Summary();
            var quotaMissing =
                counts.Working < NodeLimits.MinWorkingNodes ||
                counts.Youtube < NodeLimits.MinWorkingPerService ||
                counts.Telegram < NodeLimits.MinWorkingPerService ||
                counts.Discord < NodeLimits.MinWorkingPerService ||
                counts.Universal < NodeLimits.MinWorkingPerService;

            // Поиск новых серверов — реже; в остальные минуты только освежаем счётчики:
            // перепроверяем рабочие, у которых истекает окно свежести (5 минут).
            var now = DateTime.UtcNow;
            var searchDue = now - _lastSearchUtc >=
                            (quotaMissing ? SearchIntervalWhenFew : SearchInterval);
            if (!searchDue)
            {
                var recheck = await ProbeClient
                    .RunBatchAsync(RecheckBatch, CancellationToken.None, workingOnly: true)
                    .ConfigureAwait(false);
                if (recheck.Tested > 0)
                {
                    AppLog.Write("probe",
                        $"Перепроверка рабочих: {recheck.Tested}, живых {recheck.Working}");
                }

                return;
            }

            _lastSearchUtc = now;
            var summary = await ProbeClient.RunBatchAsync(max: 10, CancellationToken.None)
                .ConfigureAwait(false);
            if (summary.Tested > 0)
            {
                AppLog.Write("probe",
                    $"Фон :probe: рабочих {summary.Working} из {summary.Tested}, " +
                    $"лучший открывает доменов {summary.BestDomains}");
            }

            // Рабочих серверов должно быть не меньше десяти: пока их мало, проверяем
            // партии подряд, не ожидая следующего такта.
            if (NodeStore.Summary().Working < NodeLimits.MinWorkingNodes)
            {
                var working = await ProbeClient
                    .EnsureWorkingQuotaAsync(4, NodeTester.MaxBatch, CancellationToken.None)
                    .ConfigureAwait(false);
                AppLog.Write("probe",
                    $"Минимум рабочих серверов: {working} из {NodeLimits.MinWorkingNodes}");
            }

            // Кнопки сервисов должны иметь живые серверы: если для YouTube, Telegram,
            // Discord или универсального доступа меньше двух проверенных, ищем под этот
            // сервис. Фокус меняется по кругу: иначе первый недостающий сервис забирал
            // все такты, а остальные не искались вовсе.
            var serviceCounts = NodeStore.Summary();
            var missing = new List<NodeStore.QuickService>();
            if (serviceCounts.Youtube < NodeLimits.MinWorkingPerService)
            {
                missing.Add(NodeStore.QuickService.YouTube);
            }

            if (serviceCounts.Telegram < NodeLimits.MinWorkingPerService)
            {
                missing.Add(NodeStore.QuickService.Telegram);
            }

            if (serviceCounts.Discord < NodeLimits.MinWorkingPerService)
            {
                missing.Add(NodeStore.QuickService.Discord);
            }

            if (serviceCounts.Universal < NodeLimits.MinWorkingPerService)
            {
                missing.Add(NodeStore.QuickService.Universal);
            }

            if (missing.Count > 0 && !ProbeClient.IsRunning && !TunnelService.IsBypassMode)
            {
                var focus = missing[(int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute) % missing.Count];
                var found = await ProbeClient
                    .EnsureServiceNodeAsync(focus, 1, CancellationToken.None)
                    .ConfigureAwait(false);
                AppLog.Write("probe", found is null
                    ? $"{focus}: рабочих серверов меньше {NodeLimits.MinWorkingPerService}, поиск продолжится"
                    : $"{focus}: рабочий сервер найден автоматически");
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Фоновая проверка в :probe не удалась", exception);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
