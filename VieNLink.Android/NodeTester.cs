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

    public static async Task<NodeTestSummary> TestAsync(
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

            var ordered = NodeStore.OrderForConfig(NodeStore.Snapshot());
            if (ordered.Count == 0)
            {
                return new NodeTestSummary(0, 0, 0, "Список серверов пуст: обновите источники");
            }

            List<ProxyNode> candidates;
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
                    testSettings);

                CoreRuntime.EnsureSetup(Android.App.Application.Context);

                // TUN в этом конфиге нет: OpenTun не вызывается, служба не нужна.
                if (!CoreRuntime.Start(config))
                {
                    return new NodeTestSummary(0, 0, 0, "Ядро не создало сервер проверки");
                }

                startedTestCore = true;

                var loaded = CoreRuntime.LoadedTags;
                candidates = candidates.Where(node => loaded.Contains(node.Tag)).ToList();
                if (candidates.Count == 0)
                {
                    return new NodeTestSummary(0, 0, 0, "Ядро не приняло ни одного сервера");
                }
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

                    node.CheckedAtUtc = DateTime.UtcNow;
                    node.TriedAtUtc = DateTime.UtcNow;
                    node.DelayMs = universalDelay;
                    node.UniversalOk = universalDelay is not null;
                    node.YoutubeOk = Opened(results, "YouTube");
                    node.TelegramOk = Opened(results, "Telegram");
                    node.DiscordOk = Opened(results, "Discord");

                    // Сколько всего заблокированных доменов открывается: главный показатель
                    // качества сервера. Считаем только у живых, иначе это 18 таймаутов впустую.
                    node.OpenDomains = anyOpened
                        ? await CountOpenDomainsAsync(api, node.Tag, cancellationToken).ConfigureAwait(false)
                        : 0;

                    if (anyOpened)
                    {
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
                    else
                    {
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

        using var limiter = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = candidates.Select(async node =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Жёсткий предел на один сервер: зависший запрос не должен держать
                // слот партии и останавливать счётчик прогресса.
                using var nodeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                nodeLimit.CancelAfter(TimeSpan.FromSeconds(20));
                var nodeToken = nodeLimit.Token;

                // Статус до проверки: рабочему серверу даём вторую попытку, чтобы
                // разовая ошибка сети или занятость сервера не убирали его из счётчиков.
                var wasWorking = node.Status == NodeStatus.Working;

                async Task<(string Service, int? delay)[]> ProbeOnceAsync() =>
                    await Task.WhenAll(ProbeUrls.Select(async probe =>
                    {
                        var delay = await api.TestDelayAsync(node.Tag, timeoutMs, nodeToken, probe.Url)
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
                        node.OpenDomains = await CountOpenDomainsAsync(api, node.Tag, nodeToken, timeoutMs)
                            .ConfigureAwait(false);
                    }

                    NodeHealth.RegisterSuccess(node);
                    NodeStore.RegisterWorking(node);
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
                    // Рабочий сервер провалил проверку: результат последней удачной
                    // проверки оставляем, время успеха не обновляем. Сервер сам выпадет
                    // из «свежих» для выбора, но из рабочего списка не исчезнет.
                    NodeHealth.RegisterFailure(node);
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

        if (working == 0 && candidates.Count > 0)
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
        progress?.Report($"Готово: рабочих {working} из {candidates.Count} ({services}), " +
                         $"лучший открывает доменов {bestDomains}");
        return new NodeTestSummary(candidates.Count, working, 0, null,
            universal, youtube, telegram, discord, bestDomains, newWorking);
    }

    /// <summary>Открылся ли хотя бы один адрес указанного сервиса.</summary>
    private static bool Opened(IEnumerable<(string Service, int? delay)> results, string service) =>
        results.Any(result => result.Service == service && result.delay is not null);

    /// <summary>Есть ли прямой интернет (без сервера): отличает обрыв сети от мёртвых серверов.</summary>
    private static async Task<bool> HasDirectInternetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new System.Net.Http.SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(4),
            };
            using var client = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(6),
            };
            using var response = await client.GetAsync(
                "https://cp.cloudflare.com/generate_204",
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is System.Net.Http.HttpRequestException or System.OperationCanceledException
                or TaskCanceledException)
        {
            return false;
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
