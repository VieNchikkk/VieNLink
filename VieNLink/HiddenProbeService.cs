using System.IO;

namespace VieNLink;

/// <summary>
/// Скрытая проверка серверов: отдельный процесс sing-box на своих локальных портах,
/// без TUN и без системного прокси. Он не трогает текущее подключение пользователя,
/// поэтому проверять серверы можно и во время работы, и когда туннель уже поднят.
/// </summary>
internal sealed class HiddenProbeService : IAsyncDisposable
{
    private readonly SingBoxRunner _runner = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _running;

    public bool IsRunning => _running;

    /// <summary>
    /// Проверяет серверы через скрытое ядро. Возвращает итог; параллельный вызов
    /// не запускается — возвращается пустой итог.
    /// </summary>
    public async Task<TestSummary> TestAsync(
        IReadOnlyList<ProxyNode> nodes,
        AppSettings settings,
        IProgress<string>? progress,
        Action<ProxyNode>? onNodeTested,
        CancellationToken cancellationToken,
        int concurrency = 3,
        int timeoutMs = 3000,
        bool waitForGate = true,
        bool precheck = true)
    {
        if (nodes.Count == 0)
        {
            return new TestSummary(0, 0, 0, 0);
        }

        // Ручная проверка ждёт окончания фоновой; фоновая при занятом ядре
        // просто пропускает такт и не копит очередь.
        if (waitForGate)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new TestSummary(0, 0, 0, 0);
        }

        _running = true;
        var configPath = AppPaths.ProbeConfigPath;
        try
        {
            if (!File.Exists(AppPaths.SingBoxPath))
            {
                AppLog.Write("probe", "Скрытая проверка пропущена: ядро sing-box не скачано");
                return new TestSummary(0, 0, 0, 0);
            }

            var ordered = nodes
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (ordered.Count == 0)
            {
                return new TestSummary(0, 0, 0, 0);
            }

            // Мёртвые хост:порты отсеиваем TCP-опросом: не тащим их в пробное ядро
            // и не ждём трёхсекундный таймаут. Один опрос на endpoint, а не на аккаунт.
            // precheck=false — когда включён TUN: сырые TCP-соединения из приложения
            // ушли бы в туннель.
            var originalCount = ordered.Count;
            if (precheck)
            {
                var alive = await NodePrecheck.FilterAliveAsync(
                        ordered,
                        NodePrecheck.DefaultConcurrency,
                        NodePrecheck.DefaultTimeoutMs,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (alive.Count < originalCount)
                {
                    // Если не ответил вообще никто, сначала отличаем обрыв сети от мёртвых
                    // серверов: иначе сетевой сбой вычистил бы весь список.
                    if (alive.Count == 0 &&
                        !await NetworkHealth.HasDirectInternetAsync(cancellationToken).ConfigureAwait(false))
                    {
                        AppLog.Write("probe", "TCP-отсев: прямой интернет недоступен — статусы не изменены");
                        throw new InvalidOperationException(
                            "Нет прямого интернета: проверка серверов не выполнялась");
                    }

                    var aliveIds = new HashSet<string>(
                        alive.Select(node => node.Id), StringComparer.OrdinalIgnoreCase);
                    foreach (var node in ordered.Where(node => !aliveIds.Contains(node.Id)))
                    {
                        // Грубый TCP-опрос не должен наказывать уже проверенный сервер:
                        // мобильная/домашняя сеть может не дать соединения просто так.
                        if (node.Status == NodeStatus.Working)
                        {
                            node.TriedAtUtc = DateTime.UtcNow;
                            continue;
                        }

                        NodeHealth.RegisterFailure(node);
                        onNodeTested?.Invoke(node);
                    }

                    progress?.Report(
                        $"TCP-отсев: {originalCount - alive.Count} из {originalCount} не отвечают");
                    ordered = alive;
                    if (ordered.Count == 0)
                    {
                        return new TestSummary(originalCount, 0, 0, 0);
                    }
                }
            }

            // Копия настроек: проверка не должна менять режим подключения пользователя.
            var probeSettings = settings.Clone();
            probeSettings.TunnelMode = TunnelMode.SystemProxy;
            probeSettings.SiteRouteMode = SiteRouteMode.All;
            probeSettings.OnlySelectedApps = false;

            var mixedPort = ProxyManager.FindFreePort(Math.Max(settings.MixedPort, 22000));
            var apiPort = ProxyManager.FindFreePort(Math.Max(settings.ApiPort, mixedPort + 1));
            var secret = Guid.NewGuid().ToString("N");

            var json = SingBoxConfigBuilder.Build(
                ordered,
                ordered[0].Tag,
                mixedPort,
                apiPort,
                secret,
                probeSettings,
                // ПК тоже может стоять за Wi-Fi-роутером с фильтрацией DNS:
                // явный 77.88.8.8 не зависит от системного резолвера.
                explicitDns: true);
            SingBoxConfigBuilder.WriteToFile(configPath, json);
            try
            {
                await SingBoxRunner.CheckConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException checkError)
            {
                // Один битый сервер не должен отменять всю партию: находим его делением
                // списка пополам и проверяем остальные. Статусы исключённых не меняем.
                var valid = await FilterValidNodesAsync(ordered, probeSettings, cancellationToken)
                    .ConfigureAwait(false);
                if (valid.Count == 0)
                {
                    throw new InvalidOperationException(
                        "sing-box не принимает конфиг ни одного сервера из партии", checkError);
                }

                AppLog.Write("probe",
                    $"Из партии исключено {ordered.Count - valid.Count} серверов: sing-box не принимает конфиг");
                ordered = valid;
                json = SingBoxConfigBuilder.Build(
                    ordered,
                    ordered[0].Tag,
                    mixedPort,
                    apiPort,
                    secret,
                    probeSettings,
                    explicitDns: true);
                SingBoxConfigBuilder.WriteToFile(configPath, json);
                await SingBoxRunner.CheckConfigAsync(configPath, cancellationToken).ConfigureAwait(false);
            }

            AppLog.Write("probe", $"Скрытая проверка: серверов {ordered.Count} из {originalCount}, mixed {mixedPort}, API {apiPort} (напрямую, вне VPN)");
            await _runner.StartAsync(configPath, apiPort, TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);

            using var api = new ClashApiClient(apiPort, secret);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await api.PingAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            var summary = await ProxyManager.TestWithApiAsync(
                api,
                ordered,
                progress,
                onNodeTested,
                cancellationToken,
                concurrency,
                timeoutMs,
                youtubeUrl: null,
                telegramUrl: null).ConfigureAwait(false);
            return new TestSummary(
                originalCount,
                summary.YoutubeOk,
                summary.TelegramOk,
                summary.Working,
                summary.BestDomains);
        }
        finally
        {
            await _runner.StopAsync().ConfigureAwait(false);
            try
            {
                File.Delete(configPath);
            }
            catch (IOException)
            {
                // Следующая проверка перезапишет конфиг.
            }

            _running = false;
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _runner.StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>
    /// Делит список пополам, пока не останутся серверы, с которыми sing-box принимает
    /// конфиг. Исключённые отмечаются как попробованные и в статусах не меняются:
    /// проверка не должна доказывать «мёртвость» из-за ошибки разбора конфига.
    /// </summary>
    private static async Task<List<ProxyNode>> FilterValidNodesAsync(
        List<ProxyNode> nodes,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var result = new List<ProxyNode>(nodes.Count);
        var queue = new Queue<List<ProxyNode>>();
        queue.Enqueue(nodes);
        var checks = 0;
        var path = Path.Combine(AppPaths.RuntimeDirectory, "probe-check.json");
        while (queue.Count > 0 && !cancellationToken.IsCancellationRequested && checks < 80)
        {
            var batch = queue.Dequeue();
            if (batch.Count == 0)
            {
                continue;
            }

            var json = SingBoxConfigBuilder.Build(
                batch, batch[0].Tag, settings.MixedPort, settings.ApiPort, "check", settings,
                explicitDns: true);
            SingBoxConfigBuilder.WriteToFile(path, json);
            checks++;
            try
            {
                await SingBoxRunner.CheckConfigAsync(path, cancellationToken).ConfigureAwait(false);
                result.AddRange(batch);
            }
            catch (InvalidOperationException)
            {
                if (batch.Count == 1)
                {
                    batch[0].TriedAtUtc = DateTime.UtcNow;
                    AppLog.Write("probe", "Сервер исключён из конфига проверки: " + batch[0].Name);
                    continue;
                }

                var half = batch.Count / 2;
                queue.Enqueue(batch.Take(half).ToList());
                queue.Enqueue(batch.Skip(half).ToList());
            }
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный конфиг удалится при следующей проверке.
        }

        AppLog.Write("probe", $"Проверок конфига: {checks}, годных серверов {result.Count} из {nodes.Count}");
        return result;
    }

    /// <summary>Немедленная остановка скрытого ядра при выходе из приложения.</summary>
    public void StopNow() => _runner.StopNow();
}
