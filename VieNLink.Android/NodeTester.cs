namespace VieNLink.Mobile;

/// <summary>Итог проверки серверов для интерфейса.</summary>
internal sealed record NodeTestSummary(
    int Tested,
    int Working,
    int Removed,
    string? Error,
    int Universal = 0,
    int Youtube = 0,
    int Telegram = 0,
    int Discord = 0,
    int BestDomains = 0,
    int NewWorking = 0);

/// <summary>
/// Проверка задержки серверов через Clash API внутри libbox (127.0.0.1:20801).
/// Если туннель уже поднят — опрашиваются только серверы его конфига, ядро не перезапускается.
/// Если нет — поднимается временный конфиг без TUN, и после проверки ядро останавливается.
/// </summary>
internal static class NodeTester
{
    /// <summary>
    /// За один прогон проверяем не весь список: мобильный канал слабее, а каждый запрос
    /// идёт через сервер и тратит его трафик. 200 — крупнее партия, значит меньше
    /// холодных запусков процесса :probe и меньше нагрузки на телефон.
    /// </summary>
    public const int MaxBatch = 200;

    public const int Concurrency = 12;
    public const int TimeoutMs = 4000;

    /// <summary>
    /// Рабочему серверу даём больше времени: он уже доказал, что умеет открывать
    /// адреса, а мобильная сеть под нагрузкой отвечает медленнее. Три секунды
    /// на слабом канале — частая причина ложного провала.
    /// </summary>
    public const int WorkingTimeoutMs = 6000;

    /// <summary>
    /// Проверочные адреса по сервисам. Универсальный (Cloudflare) — приоритетный:
    /// именно его задержка показывается и учитывается при выборе лучшего сервера.
    /// YouTube и Telegram отмечены отдельно, Discord нужен, чтобы не браковать сервер
    /// из-за блокировки одного домена. Несколько адресов на сервис — чтобы блокировка
    /// одного домена не прятала рабочий сервер.
    /// </summary>
    private static readonly (string Service, string Url)[] ProbeUrls =
    {
        ("Cloudflare", "https://cp.cloudflare.com/generate_204"),
        ("YouTube", "https://www.youtube.com/"),
        ("YouTube", "https://redirector.googlevideo.com/generate_204"),
        ("Telegram", "https://t.me/"),
        ("Telegram", "https://web.telegram.org/"),
        ("Discord", "https://discord.com/"),
    };

    /// <summary>
    /// Полный список заблокированных доменов (тот же, что у zapret/десинка).
    /// Прогоняется только для серверов, которые прошли базовую проверку: у мёртвых серверов
    /// это была бы пустая трата времени и трафика.
    /// </summary>
    private static readonly (string Service, string Url)[] FullProbes = BuildFullProbes();

    private static (string Service, string Url)[] BuildFullProbes() =>
        TlsHello.TargetDomains
            .Select(domain => (domain, "https://" + domain + "/"))
            .ToArray();

    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    public static Task<NodeTestSummary> TestAsync(
        IProgress<string>? progress,
        Action<ProxyNode>? onTested,
        CancellationToken cancellationToken) =>
        TestAsync(null, progress, onTested, cancellationToken);

    /// <summary>Проверка произвольного набора (например, серверов из отсева).</summary>
    public static Task<NodeTestSummary> TestNodesAsync(
        IReadOnlyList<ProxyNode> nodes,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        TestAsync(nodes, progress, null, cancellationToken);

    private static async Task<NodeTestSummary> TestAsync(
        IReadOnlyList<ProxyNode>? explicitNodes,
        IProgress<string>? progress,
        Action<ProxyNode>? onTested,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new NodeTestSummary(0, 0, 0, "Проверка уже идёт");
        }

        var startedTestCore = false;
        ClashApiClient? api = null;
        try
        {
            NodeStore.EnsureLoaded();

            List<ProxyNode> candidates;
            if (explicitNodes is not null)
            {
                if (TunnelService.State == TunnelState.Running || CoreRuntime.IsRunning)
                {
                    return new NodeTestSummary(0, 0, 0,
                        "Остановите VPN перед перепроверкой отсева");
                }

                candidates = explicitNodes
                    .Where(node => !string.IsNullOrWhiteSpace(node.Tag))
                    .Take(MaxBatch)
                    .ToList();
                if (candidates.Count == 0)
                {
                    return new NodeTestSummary(0, 0, 0, "В отсеве нет серверов для проверки");
                }

                progress?.Report($"Перепроверяю отсев: {candidates.Count} серверов");
            }
            else
            {
                var ordered = NodeStore.OrderForConfig(NodeStore.Snapshot());
                if (ordered.Count == 0)
                {
                    return new NodeTestSummary(0, 0, 0, "Список серверов пуст: обновите источники");
                }

                if (TunnelService.State == TunnelState.Running && CoreRuntime.IsRunning)
                {
                    // Работающий туннель не трогаем: проверяем только то, что уже загружено в ядро.
                    var loaded = CoreRuntime.LoadedTags;
                    candidates = ordered.Where(node => loaded.Contains(node.Tag)).Take(MaxBatch).ToList();
                    if (candidates.Count == 0)
                    {
                        return new NodeTestSummary(0, 0, 0,
                            "Туннель запущен с другим конфигом — сначала выберите сервер в списке");
                    }

                    progress?.Report($"Проверяю серверы работающего туннеля: {candidates.Count}");
                }
                else
                {
                    if (CoreRuntime.IsRunning)
                    {
                        return new NodeTestSummary(0, 0, 0, "Туннель ещё запускается, подождите");
                    }

                    candidates = ordered.Take(MaxBatch).ToList();
                    progress?.Report($"Поднимаю ядро для проверки: {candidates.Count} серверов");
                }
            }

            var testSettings = new AppSettings
            {
                // Временный конфиг без TUN: нужен только mixed-порт и Clash API.
                TunnelMode = TunnelMode.SystemProxy,
                SiteRouteMode = SiteRouteMode.All,
                OnlySelectedApps = false,
                AppRouteMode = AppRouteMode.All,
                AppList = string.Empty,
            };

            var config = SingBoxConfigBuilder.Build(
                candidates,
                candidates[0].Tag,
                CoreRuntime.MixedPort,
                CoreRuntime.ApiPort,
                CoreRuntime.ApiSecret,
                testSettings,
                // Как кнопка «Подключение»: DNS идёт через проверяемый сервер, а не
                // через локальный UDP/53 или 1.1.1.1, которые режет мобильный оператор.
                dnsThroughProxy: true);

            CoreRuntime.EnsureSetup(Android.App.Application.Context);

            // TUN в этом конфиге нет: OpenTun не вызывается, служба не нужна.
            if (!CoreRuntime.Start(config))
            {
                return new NodeTestSummary(0, 0, 0, "Ядро не создало сервер проверки");
            }

            startedTestCore = true;

            var loadedTags = CoreRuntime.LoadedTags;
            candidates = candidates.Where(node => loadedTags.Contains(node.Tag)).ToList();
            if (candidates.Count == 0)
            {
                return new NodeTestSummary(0, 0, 0, "Ядро не приняло ни одного сервера");
            }

            api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            if (!await WaitForApiAsync(api, cancellationToken).ConfigureAwait(false))
            {
                return new NodeTestSummary(0, 0, 0, "Clash API не ответил: ядро не поднялось");
            }

            // Снимок: если не открылся ни один сервер из большой партии, это проблема сети,
            // а не серверов — статусы не меняем, иначе можно вычистить весь список.
            var snapshot = candidates.ToDictionary(
                node => node,
                node => (node.Status, node.DelayMs, node.CheckedAtUtc,
                         node.YoutubeOk, node.TelegramOk, node.UniversalOk, node.DiscordOk,
                         node.OpenDomains, node.FailCount));

            var working = 0;
            var universal = 0;
            var youtube = 0;
            var telegram = 0;
            var discord = 0;
            var done = 0;

            using var limiter = new SemaphoreSlim(Concurrency);
            var tasks = candidates.Select(async node =>
            {
                await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Все адреса проверяем параллельно: у мёртвого сервера это одна
                    // задержка по таймауту, а не шесть подряд.
                    var results = await Task.WhenAll(ProbeUrls.Select(async probe =>
                    {
                        var delay = await api.TestDelayAsync(node.Tag, TimeoutMs, cancellationToken, probe.Url)
                            .ConfigureAwait(false);
                        return (probe.Service, delay);
                    })).ConfigureAwait(false);

                    var anyOpened = results.Any(result => result.delay is not null);

                    // Приоритет — универсальный адрес: его задержка идёт в список и в выбор
                    // лучшего сервера. Если он не открылся, сервер может быть рабочим для
                    // отдельных сервисов, но автоматически первым не выбирается.
                    var universalDelay = results
                        .FirstOrDefault(result => result.Service == "Cloudflare").delay;

                    var wasWorking = node.Status == NodeStatus.Working;
                    node.TriedAtUtc = DateTime.UtcNow;

                    if (anyOpened)
                    {
                        node.CheckedAtUtc = DateTime.UtcNow;
                        node.DelayMs = universalDelay;
                        node.UniversalOk = universalDelay is not null;
                        node.YoutubeOk = Opened(results, "YouTube");
                        node.TelegramOk = Opened(results, "Telegram");
                        node.DiscordOk = Opened(results, "Discord");

                        // Сколько всего заблокированных доменов открывается: главный показатель
                        // качества сервера. Считаем только у живых, иначе это 18 таймаутов впустую.
                        node.OpenDomains = await CountOpenDomainsAsync(api, node.Tag, cancellationToken)
                            .ConfigureAwait(false);

                        NodeHealth.RegisterSuccess(node);
                        NodeStore.RegisterWorking(node);
                        Interlocked.Increment(ref working);
                        if (universalDelay is not null)
                        {
                            Interlocked.Increment(ref universal);
                        }

                        if (node.YoutubeOk == true)
                        {
                            Interlocked.Increment(ref youtube);
                        }

                        if (node.TelegramOk == true)
                        {
                            Interlocked.Increment(ref telegram);
                        }

                        if (Opened(results, "Discord"))
                        {
                            Interlocked.Increment(ref discord);
                        }
                    }
                    else if (wasWorking)
                    {
                        // Рабочий сервер провалил проверку: результат последней удачной
                        // проверки сохраняем. Раньше флаги обнулялись до RegisterFailure,
                        // сервер терял HasSuccess и удалялся вместо отсева.
                        NodeHealth.RegisterFailure(node);
                        ProtectActiveNode(node);
                    }
                    else
                    {
                        node.CheckedAtUtc = DateTime.UtcNow;
                        node.DelayMs = null;
                        node.UniversalOk = false;
                        node.YoutubeOk = false;
                        node.TelegramOk = false;
                        node.DiscordOk = false;
                        node.OpenDomains = 0;
                        NodeHealth.RegisterFailure(node);
                    }

                    onTested?.Invoke(node);
                }
                finally
                {
                    limiter.Release();
                    var finished = Interlocked.Increment(ref done);
                    if (finished % 10 == 0 || finished == candidates.Count)
                    {
                        progress?.Report(
                            $"Проверено {finished} из {candidates.Count}: рабочих {Volatile.Read(ref working)}");
                    }
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var workingBefore = snapshot.Count(pair => pair.Value.Status == NodeStatus.Working);
            if (working == 0 && workingBefore >= 3 &&
                await HasDirectInternetAsync(cancellationToken).ConfigureAwait(false))
            {
                RestoreWorkingFromSnapshot(snapshot);
            }

            if (working == 0 && candidates.Count >= 20)
            {
                foreach (var (node, state) in snapshot)
                {
                    node.Status = state.Status;
                    node.DelayMs = state.DelayMs;
                    node.CheckedAtUtc = state.CheckedAtUtc;
                    node.YoutubeOk = state.YoutubeOk;
                    node.TelegramOk = state.TelegramOk;
                    node.UniversalOk = state.UniversalOk;
                    node.DiscordOk = state.DiscordOk;
                    node.OpenDomains = state.OpenDomains;
                    node.FailCount = state.FailCount;
                }

                MarkTried(candidates);
                NodeStore.SaveNodes();
                AppLog.Write("test", $"Откат проверки: ни один из {candidates.Count} серверов не открылся");
                return new NodeTestSummary(candidates.Count, 0, 0,
                    "Ни один сервер не открылся — проверьте сеть. Статусы серверов не изменены.");
            }

            var removed = NodeStore.DropDeadNodes(candidates);
            NodeStore.SaveNodes();

            // Лучший сервер выбираем сами и запоминаем: кнопка «Лучший» больше не нужна,
            // а прежний выбор мог умереть, пока шла проверка.
            var best = NodeStore.SelectBestWorking();

            var services = $"универсальных {universal}, YouTube {youtube}, " +
                           $"Telegram {telegram}, Discord {discord}";
            var bestDomains = candidates.Count == 0 ? 0 : candidates.Max(node => node.OpenDomains);
            AppLog.Write("test",
                $"Проверка серверов: рабочих {working} из {candidates.Count} ({services}), " +
                $"лучший открывает доменов {bestDomains}, удалено {removed}" +
                (best is null ? ", живых серверов нет" : ", выбран " + best.Name));
            progress?.Report($"Готово: рабочих {working} из {candidates.Count} ({services}), " +
                             $"лучший открывает доменов {bestDomains}, удалено {removed}" +
                             (best is null ? "" : "; выбран " + best.Name + " · " + best.DisplayDelay));

            return new NodeTestSummary(candidates.Count, working, removed, null,
                universal, youtube, telegram, discord, bestDomains);
        }
        catch (OperationCanceledException)
        {
            return new NodeTestSummary(0, 0, 0, "Проверка отменена");
        }
        catch (Exception exception)
        {
            AppLog.Write("test", "Не удалось проверить серверы", exception);
            Android.Util.Log.Error("VieNLink", "Проверка серверов: " + exception);
            return new NodeTestSummary(0, 0, 0, "Ошибка проверки: " + exception.Message);
        }
        finally
        {
            api?.Dispose();
            if (startedTestCore)
            {
                CoreRuntime.Stop();
            }

            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>
    /// Сервер, через который идёт трафик, не помечаем мёртвым из-за скрытой проверки:
    /// занятость или лимит соединений не доказывают, что он не работает. Смерть
    /// определит health-check через активный туннель и переключит на запасной.
    /// </summary>
    private static void ProtectActiveNode(ProxyNode node)
    {
        if (node.Status != NodeStatus.Dead || TunnelService.State != TunnelState.Running)
        {
            return;
        }

        if (NodeStore.Selected?.Id is not { } selectedId ||
            !selectedId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        node.Status = NodeStatus.Working;
        node.FailCount = 0;
        node.TriedAtUtc = DateTime.UtcNow;
        AppLog.Write("test",
            $"«{node.Name}»: проверка не открыла адрес, но трафик идёт через него — оставляю");
    }

    /// <summary>
    /// Проверка готового списка через переданный Clash API. Используется отдельным
    /// процессом :probe: основное ядро и VPN не трогаются. Сохранение результатов —
    /// на стороне вызывающего.
    /// </summary>
    internal static async Task<NodeTestSummary> TestBatchAsync(
        ClashApiClient api,
        IReadOnlyList<ProxyNode> candidates,
        IProgress<string>? progress,
        Action<ProxyNode, bool>? onTested,
        CancellationToken cancellationToken,
        int concurrency = 12,
        int timeoutMs = TimeoutMs,
        bool fullDomains = true)
    {
        if (candidates.Count == 0)
        {
            return new NodeTestSummary(0, 0, 0, null);
        }

        var snapshot = candidates.ToDictionary(
            node => node,
            node => (node.Status, node.DelayMs, node.CheckedAtUtc,
                     node.YoutubeOk, node.TelegramOk, node.UniversalOk, node.DiscordOk,
                     node.OpenDomains, node.FailCount));

        var working = 0;
        var newWorking = 0;
        var universal = 0;
        var youtube = 0;
        var telegram = 0;
        var discord = 0;
        var done = 0;
        // Отдельно считаем именно ранее рабочие серверы: если в партии ни один
        // из них не открылся, это почти наверняка сбой проверки, а не смерть всех.
        var workingTested = 0;
        var workingSucceeded = 0;
        var failedWorking = new List<ProxyNode>();

        using var limiter = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = candidates.Select(async node =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Жёсткий предел на один сервер: зависший запрос не должен держать
                // слот партии и останавливать счётчик прогресса. Рабочему серверу
                // нужно больше времени: две попытки по 6 с и подсчёт доменов
                // (18 адресов по 4 параллельно) не должны обрезать результат.
                var wasWorking = node.Status == NodeStatus.Working;
                using var nodeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                nodeLimit.CancelAfter(TimeSpan.FromSeconds(wasWorking ? 50 : 25));
                var nodeToken = nodeLimit.Token;

                // Статус до проверки: рабочему серверу даём вторую попытку, чтобы
                // разовая ошибка сети или занятость сервера не убирали его из счётчиков.
                if (wasWorking)
                {
                    Interlocked.Increment(ref workingTested);
                }

                // Рабочему серверу — больше времени на ответ; остальным хватает базового.
                var probeTimeout = wasWorking ? Math.Max(timeoutMs, WorkingTimeoutMs) : timeoutMs;

                async Task<(string Service, int? delay)[]> ProbeOnceAsync() =>
                    await Task.WhenAll(ProbeUrls.Select(async probe =>
                    {
                        var delay = await api.TestDelayAsync(node.Tag, probeTimeout, nodeToken, probe.Url)
                            .ConfigureAwait(false);
                        return (probe.Service, delay);
                    })).ConfigureAwait(false);

                var results = await ProbeOnceAsync().ConfigureAwait(false);

                var anyOpened = results.Any(result => result.delay is not null);
                var universalDelay = results
                    .FirstOrDefault(result => result.Service == "Cloudflare").delay;

                if (!anyOpened && wasWorking && !nodeToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(600, nodeToken).ConfigureAwait(false);
                    }
                    catch (System.OperationCanceledException)
                    {
                        // Предел партии: вторую попытку не делаем.
                    }

                    if (!nodeToken.IsCancellationRequested)
                    {
                        results = await ProbeOnceAsync().ConfigureAwait(false);
                        anyOpened = results.Any(result => result.delay is not null);
                        universalDelay = results
                            .FirstOrDefault(result => result.Service == "Cloudflare").delay;
                    }
                }

                node.TriedAtUtc = DateTime.UtcNow;
                if (anyOpened)
                {
                    node.CheckedAtUtc = DateTime.UtcNow;
                    node.DelayMs = universalDelay;
                    node.UniversalOk = universalDelay is not null;
                    node.YoutubeOk = Opened(results, "YouTube");
                    node.TelegramOk = Opened(results, "Telegram");
                    node.DiscordOk = Opened(results, "Discord");
                    if (fullDomains)
                    {
                        node.OpenDomains = await CountOpenDomainsAsync(api, node.Tag, nodeToken, probeTimeout)
                            .ConfigureAwait(false);
                    }

                    NodeHealth.RegisterSuccess(node);
                    NodeStore.RegisterWorking(node);
                    if (wasWorking)
                    {
                        Interlocked.Increment(ref workingSucceeded);
                    }

                    Interlocked.Increment(ref working);
                    if (!wasWorking)
                    {
                        Interlocked.Increment(ref newWorking);
                    }
                    if (universalDelay is not null)
                    {
                        Interlocked.Increment(ref universal);
                    }

                    if (node.YoutubeOk == true)
                    {
                        Interlocked.Increment(ref youtube);
                    }

                    if (node.TelegramOk == true)
                    {
                        Interlocked.Increment(ref telegram);
                    }

                    if (Opened(results, "Discord"))
                    {
                        Interlocked.Increment(ref discord);
                    }
                }
                else if (wasWorking)
                {
                    // Рабочий сервер провалил партию: провал пока не засчитываем.
                    // После партии проверим его отдельно, без нагрузки: на мобильном
                    // канале 8 узлов × 6 адресов дают ложные провалы, и живой сервер
                    // уходил в отсев после нескольких таких партий.
                    lock (failedWorking)
                    {
                        failedWorking.Add(node);
                    }
                }
                else
                {
                    // Непроверенный: данных нет, отметку проверки и провал фиксируем.
                    node.CheckedAtUtc = DateTime.UtcNow;
                    node.DelayMs = null;
                    node.UniversalOk = false;
                    node.YoutubeOk = false;
                    node.TelegramOk = false;
                    node.DiscordOk = false;
                    if (fullDomains || anyOpened)
                    {
                        node.OpenDomains = 0;
                    }

                    NodeHealth.RegisterFailure(node);
                }

                onTested?.Invoke(node, !wasWorking && anyOpened);
            }
            finally
            {
                limiter.Release();
                var finished = Interlocked.Increment(ref done);
                if (finished % 10 == 0 || finished == candidates.Count)
                {
                    progress?.Report(
                        $"Проверено {finished} из {candidates.Count}: рабочих {Volatile.Read(ref working)}");
                }
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Рабочие серверы, не открывшие адреса в общей партии, проверяем ещё раз
        // поодиночке, без нагрузки: на мобильном канале партия из 8 узлов × 6 адресов
        // сама даёт ложные провалы. Провал засчитывается только после этой проверки.
        List<ProxyNode> failed;
        lock (failedWorking)
        {
            failed = failedWorking.Distinct().ToList();
        }

        if (failed.Count > 0)
        {
            var (recovered, stillFailed) = await ConfirmFailedWorkingAsync(
                api, failed, cancellationToken, progress).ConfigureAwait(false);

            foreach (var node in recovered)
            {
                Interlocked.Increment(ref working);
                Interlocked.Increment(ref workingSucceeded);
                if (node.UniversalOk == true)
                {
                    Interlocked.Increment(ref universal);
                }

                if (node.YoutubeOk == true)
                {
                    Interlocked.Increment(ref youtube);
                }

                if (node.TelegramOk == true)
                {
                    Interlocked.Increment(ref telegram);
                }

                if (node.DiscordOk == true)
                {
                    Interlocked.Increment(ref discord);
                }
            }

            lock (failedWorking)
            {
                failedWorking.Clear();
                failedWorking.AddRange(stillFailed);
            }
        }

        var workingBefore = snapshot.Count(pair => pair.Value.Status == NodeStatus.Working);
        var testedWorking = Volatile.Read(ref workingTested);
        var succeededWorking = Volatile.Read(ref workingSucceeded);
        var massRollback = false;

        // Ни один из ранее рабочих серверов не открылся. Если их было хотя бы три,
        // это сбой проверки/сети, а не смерть всех сразу: провалы не засчитываем.
        // Порог 3, а не 2: пара действительно мёртвых серверов должна дойти до
        // RemoveAfterFailures, иначе массовый откат будет вечно возвращать их FailCount.
        if (testedWorking >= 3 && succeededWorking == 0 &&
            await HasDirectInternetAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (failedWorking)
            {
                RestoreWorkingFromSnapshot(snapshot, failedWorking);
            }

            massRollback = true;
            AppLog.Write("test",
                $"Рабочие серверы разом не открылись ({testedWorking}) — провалы не засчитаны");
            progress?.Report(
                $"Рабочих {testedWorking}: ни один не открылся — провалы не засчитаны");
        }

        if (!massRollback && working == 0 && workingBefore >= 3 &&
            await HasDirectInternetAsync(cancellationToken).ConfigureAwait(false))
        {
            RestoreWorkingFromSnapshot(snapshot);
        }

        if (!massRollback && working == 0 && candidates.Count > 0)
        {
            // Полный провал партии при отсутствии интернета — это не мёртвые серверы.
            // Без этой проверки фоновая проверка в метро/при обрыве сети вычищала
            // непроверенные серверы после первого же провала.
            var network = await HasDirectInternetAsync(cancellationToken).ConfigureAwait(false);
            if (network && candidates.Count < 20)
            {
                // Сеть есть, серверы честно не открылись — обрабатываем как обычно:
                // партия маленькая, массового вычищения не будет.
            }
            else
            {
                foreach (var (node, state) in snapshot)
                {
                    node.Status = state.Status;
                    node.DelayMs = state.DelayMs;
                    node.CheckedAtUtc = state.CheckedAtUtc;
                    node.YoutubeOk = state.YoutubeOk;
                    node.TelegramOk = state.TelegramOk;
                    node.UniversalOk = state.UniversalOk;
                    node.DiscordOk = state.DiscordOk;
                    node.OpenDomains = state.OpenDomains;
                    node.FailCount = state.FailCount;
                }

                MarkTried(candidates);
                return new NodeTestSummary(candidates.Count, 0, 0,
                    network
                        ? "Ни один сервер не открылся — статусы не изменены."
                        : "Нет сети: статусы серверов не изменены.");
            }
        }

        var services = $"универсальных {universal}, YouTube {youtube}, " +
                       $"Telegram {telegram}, Discord {discord}";
        var bestDomains = candidates.Max(node => node.OpenDomains);
        if (massRollback)
        {
            return new NodeTestSummary(candidates.Count, workingBefore + working, 0,
                "Рабочие серверы не открылись — провалы не засчитаны",
                universal, youtube, telegram, discord, bestDomains, newWorking);
        }

        progress?.Report($"Готово: рабочих {working} из {candidates.Count} ({services}), " +
                         $"лучший открывает доменов {bestDomains}");
        return new NodeTestSummary(candidates.Count, working, 0, null,
            universal, youtube, telegram, discord, bestDomains, newWorking);
    }

    /// <summary>Открылся ли хотя бы один адрес указанного сервиса.</summary>
    private static bool Opened(IEnumerable<(string Service, int? delay)> results, string service) =>
        results.Any(result => result.Service == service && result.delay is not null);

    /// <summary>Есть ли прямой интернет (без сервера): отличает обрыв сети от мёртвых серверов.</summary>
    private static Task<bool> HasDirectInternetAsync(CancellationToken cancellationToken) =>
        NetworkHealth.HasDirectInternetAsync(cancellationToken);

    /// <summary>
    /// Все серверы, которые уже были рабочими, разом не открылись — это почти наверняка
    /// проблема проверки (DNS, сеть, пробное ядро), а не смерть всех серверов.
    /// Возвращаем им прежние отметки, чтобы одна плохая партия не отправила живые в отсев.
    /// <paramref name="only"/> — восстановить только эти серверы (остальные в партии
    /// могли открыться, их успех откатывать нельзя).
    /// </summary>
    private static void RestoreWorkingFromSnapshot(
        Dictionary<ProxyNode, (NodeStatus Status, int? DelayMs, DateTime? CheckedAtUtc,
            bool? YoutubeOk, bool? TelegramOk, bool? UniversalOk, bool? DiscordOk,
            int OpenDomains, int FailCount)> snapshot,
        IEnumerable<ProxyNode>? only = null)
    {
        var restored = 0;
        var candidates = only ?? snapshot.Keys;
        foreach (var node in candidates)
        {
            if (!snapshot.TryGetValue(node, out var state) || state.Status != NodeStatus.Working)
            {
                continue;
            }

            node.Status = state.Status;
            node.DelayMs = state.DelayMs;
            node.CheckedAtUtc = state.CheckedAtUtc;
            node.YoutubeOk = state.YoutubeOk;
            node.TelegramOk = state.TelegramOk;
            node.UniversalOk = state.UniversalOk;
            node.DiscordOk = state.DiscordOk;
            node.OpenDomains = state.OpenDomains;
            node.FailCount = state.FailCount;
            restored++;
        }

        if (restored > 0)
        {
            AppLog.Write("test",
                $"Массовый провал: {restored} рабочих серверов не открылись — статусы не изменены");
        }
    }

    /// <summary>
    /// Отмечает попытку проверки, не меняя статус и счётчик провалов. Без этого партия,
    /// где не открылся ни один сервер, откатывалась бы и следующая проверка снова брала
    /// бы ровно те же серверы — список не двигался бы с места никогда.
    /// </summary>
    private static void MarkTried(IEnumerable<ProxyNode> nodes)
    {
        var now = DateTime.UtcNow;
        foreach (var node in nodes)
        {
            node.TriedAtUtc = now;
        }
    }

    /// <summary>
    /// Подтверждает провал рабочего сервера отдельной проверкой после партии.
    /// Адреса проверяются по одному, без общей нагрузки: если сервер открыл хотя бы
    /// один, провал не засчитывается. Только после этой проверки растёт FailCount.
    /// </summary>
    private static async Task<(List<ProxyNode> Recovered, List<ProxyNode> Failed)> ConfirmFailedWorkingAsync(
        ClashApiClient api,
        IReadOnlyList<ProxyNode> nodes,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        var recovered = new List<ProxyNode>();
        var failed = new List<ProxyNode>();
        var timeoutMs = Math.Max(WorkingTimeoutMs, 6000);
        var urls = new (string Service, string Url)[]
        {
            ("Cloudflare", "https://cp.cloudflare.com/generate_204"),
            ("YouTube", "https://www.youtube.com/"),
        };

        progress?.Report($"Подтверждаю провал {nodes.Count} рабочих серверов…");
        AppLog.Write("test", $"Подтверждаю провал {nodes.Count} рабочих серверов");
        using var limiter = new SemaphoreSlim(10);
        var tasks = nodes.Select(async node =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (string Service, int? Delay)? opened = null;
                foreach (var probe in urls)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var delay = await api.TestDelayAsync(node.Tag, timeoutMs, cancellationToken, probe.Url)
                        .ConfigureAwait(false);
                    if (delay is not null)
                    {
                        opened = (probe.Service, delay);
                        break;
                    }
                }

                if (opened is { } hit)
                {
                    node.CheckedAtUtc = DateTime.UtcNow;
                    node.TriedAtUtc = DateTime.UtcNow;
                    node.DelayMs = hit.Delay;
                    node.UniversalOk = node.UniversalOk == true || hit.Service == "Cloudflare";
                    node.YoutubeOk = node.YoutubeOk == true || hit.Service == "YouTube";
                    node.TelegramOk = node.TelegramOk == true || hit.Service == "Telegram";
                    NodeHealth.RegisterSuccess(node);
                    lock (recovered)
                    {
                        recovered.Add(node);
                    }

                    AppLog.Write("test",
                        $"«{node.Name}»: подтверждён после провала партии ({hit.Service}, {hit.Delay} мс)");
                }
                else
                {
                    node.TriedAtUtc = DateTime.UtcNow;
                    NodeHealth.RegisterFailure(node);
                    lock (failed)
                    {
                        failed.Add(node);
                    }

                    AppLog.Write("test", $"«{node.Name}»: провал подтверждён");
                }
            }
            catch (OperationCanceledException)
            {
                // Партию остановили: провал не засчитываем.
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return (recovered, failed);
    }

    /// <summary>Сколько доменов из списка заблокированных открывает сервер.</summary>
    private static async Task<int> CountOpenDomainsAsync(
        ClashApiClient api,
        string tag,
        CancellationToken cancellationToken,
        int timeoutMs = TimeoutMs)
    {
        // Не больше четырёх запросов одновременно на сервер: 18 параллельных соединений
        // перегружают слабый бесплатный сервер, и он ложно показывает ноль доменов.
        using var limiter = new SemaphoreSlim(4);
        var tasks = FullProbes.Select(async probe =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await api.TestDelayAsync(tag, timeoutMs, cancellationToken, probe.Url)
                    .ConfigureAwait(false);
            }
            finally
            {
                limiter.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Count(delay => delay is not null);
    }

    /// <summary>Clash API поднимается через мгновение после старта службы — ждём до ~4,5 с.</summary>
    private static async Task<bool> WaitForApiAsync(ClashApiClient api, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await api.PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
