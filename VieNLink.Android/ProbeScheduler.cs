namespace VieNLink.Mobile;

/// <summary>
/// Фоновая проверка серверов: такт раз в минуту. Пока живы прошлые результаты, каждый
/// такт перепроверяет только рабочие серверы, у которых истекает окно свежести (5 минут);
/// поиск новых серверов идёт отдельно, реже (2–5 минут). Основной туннель не трогается.
/// </summary>
internal static class ProbeScheduler
{
    private static TimeSpan TickInterval => ProbeProfile.TickInterval;

    /// <summary>Как часто искать новые серверы: чаще при недоборе квоты, реже когда всё в порядке.</summary>
    private static TimeSpan SearchInterval => ProbeProfile.SearchInterval;
    private static TimeSpan SearchIntervalWhenFew => ProbeProfile.SearchIntervalWhenFew;

    /// <summary>Сколько партий по 150 серверов проверять за такт, пока нет сервера для YouTube.</summary>
    private const int YouTubeBatches = 2;

    /// <summary>Сколько рабочих серверов перепроверять за минутный такт
    /// (на телевизоре меньше — см. <see cref="ProbeProfile.RecheckBatch"/>).</summary>
    private static int RecheckBatch => ProbeProfile.RecheckBatch;

    private static Timer? _timer;
    private static DateTime _lastSearchUtc = DateTime.MinValue;
    private static int _busy;

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
        AppLog.Write("probe",
            $"Фоновая проверка серверов запущена (такт {TickInterval.TotalMinutes:0} мин, " +
            $"рабочие — каждые {NodeLimits.WorkingRecheckMinutes} мин)");

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

            await RejectedBootCheckService.RunAsync(context).ConfigureAwait(false);

            var counts = NodeStore.Summary();
            var quotaMissing =
                counts.Working < ProbeProfile.MinWorking ||
                counts.Youtube < ProbeProfile.MinWorkingPerService ||
                counts.Telegram < ProbeProfile.MinWorkingPerService ||
                counts.Discord < ProbeProfile.MinWorkingPerService ||
                counts.Universal < ProbeProfile.MinWorkingPerService;

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
            var summary = await ProbeClient.RunBatchAsync(max: ProbeProfile.ForegroundBatch, CancellationToken.None)
                .ConfigureAwait(false);
            if (summary.Tested > 0)
            {
                AppLog.Write("probe",
                    $"Фон :probe: рабочих {summary.Working} из {summary.Tested}, " +
                    $"лучший открывает доменов {summary.BestDomains}");
            }

            // Рабочих серверов должно быть не меньше десяти: пока их мало, проверяем
            // партии подряд, не ожидая следующего такта.
            if (NodeStore.Summary().Working < ProbeProfile.MinWorking)
            {
                var working = await ProbeClient
                    .EnsureWorkingQuotaAsync(4, NodeTester.MaxBatch, CancellationToken.None)
                    .ConfigureAwait(false);
                AppLog.Write("probe",
                    $"Минимум рабочих серверов: {working} из {ProbeProfile.MinWorking}");
            }

            // Кнопки сервисов должны иметь живые серверы: если для YouTube, Telegram,
            // Discord или универсального доступа меньше двух проверенных, ищем под этот
            // сервис. Фокус меняется по кругу: иначе первый недостающий сервис забирал
            // все такты, а остальные не искались вовсе.
            var serviceCounts = NodeStore.Summary();
            var missing = new List<NodeStore.QuickService>();
            if (serviceCounts.Youtube < ProbeProfile.MinWorkingPerService)
            {
                missing.Add(NodeStore.QuickService.YouTube);
            }

            if (serviceCounts.Telegram < ProbeProfile.MinWorkingPerService && !DeviceProfile.IsTelevision)
            {
                missing.Add(NodeStore.QuickService.Telegram);
            }

            if (serviceCounts.Discord < ProbeProfile.MinWorkingPerService && !DeviceProfile.IsTelevision)
            {
                missing.Add(NodeStore.QuickService.Discord);
            }

            if (serviceCounts.Universal < ProbeProfile.MinWorkingPerService)
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
                    ? $"{focus}: рабочих серверов меньше {ProbeProfile.MinWorkingPerService}, поиск продолжится"
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
