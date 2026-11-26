namespace VieNLink.Mobile;

/// <summary>
/// Один раз при запуске приложения, после стандартной стартовой перепроверки:
/// подключается к нескольким серверам из отсева тем же путём, что и кнопка
/// «Подключение» (настоящий TUN/VpnService + DNS через сервер), и делает запрос
/// через туннель. Сервер возвращается в рабочие только если через него реально
/// прошёл трафик; одного факта «VPN поднялся» недостаточно — TUN может подняться
/// и на мёртвом сервере, через который ничего не открывается.
/// Если обычный VPN уже подключён, ничего не запускает: на Android одновременно
/// может быть только один VpnService, иначе проверка переключила бы пользователя.
/// </summary>
internal static class RejectedBootCheckService
{
    /// <summary>
    /// Сколько серверов проверяем за запуск. Раньше перебирался весь отсев: это
    /// десятки поднятий VPN подряд, минуты ожидания и заметный расход батареи.
    /// </summary>
    private const int MaxChecks = 4;

    private static int _done;
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    public static async Task RunAsync(Android.Content.Context context)
    {
        if (Volatile.Read(ref _done) == 1)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        var previous = NodeStore.Selected;
        var attempted = false;
        // Выбор на пробном сервере меняем только в памяти: поднимать туннель для
        // проверки и записывать это в selected-node.json — значит подменять выбор
        // пользователя. Файл не трогаем вовсе.
        var suppressBefore = NodeStore.PersistenceSuppressed;
        NodeStore.PersistenceSuppressed = true;
        try
        {
            var settings = SettingsStore.Current;
            var android = AndroidSettingsStore.Current;
            if (settings.AutomationPaused || !settings.IdleTestEnabled || !android.AutoProbe)
            {
                return;
            }

            // Обычный VPN уже поднят — не трогаем его. Проверка отсева в этом случае
            // доступна вручную через отдельный :probe на экране отсева.
            if (TunnelService.IsBypassMode ||
                TunnelService.State == TunnelState.Running ||
                CoreRuntime.IsRunning)
            {
                return;
            }

            var items = RejectedNodeStore.Load().Take(MaxChecks).ToList();
            attempted = items.Count > 0;
            foreach (var item in items)
            {
                if (TunnelService.IsBypassMode ||
                    TunnelService.State != TunnelState.Stopped ||
                    CoreRuntime.IsRunning)
                {
                    break;
                }

                var node = item.Node;
                var tunnel = settings.Clone();
                tunnel.TunnelMode = TunnelMode.Tun;
                tunnel.OnlySelectedApps = false;
                tunnel.AppRouteMode = AppRouteMode.All;
                tunnel.AppList = string.Empty;

                var config = SingBoxConfigBuilder.Build(
                    new[] { node },
                    node.Tag,
                    CoreRuntime.MixedPort,
                    CoreRuntime.ApiPort,
                    CoreRuntime.ApiSecret,
                    tunnel,
                    dnsThroughProxy: true);
                NodeStore.Select(node, persist: false);
                VpnStarter.Start(context, config);
                var started = await WaitForStateAsync(
                    TunnelState.Running, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                var stillOurs = NodeStore.Selected?.Id is { } selectedId &&
                                selectedId.Equals(node.Id, StringComparison.OrdinalIgnoreCase);
                // Поднятый VPN — ещё не доказательство: запрос через туннель должен
                // вернуть ответ. Только тогда сервер действительно рабочий.
                var hasTraffic = started && stillOurs &&
                                 await HasRealTrafficAsync(node).ConfigureAwait(false);

                if (hasTraffic)
                {
                    node.Status = NodeStatus.Working;
                    node.FailCount = 0;
                    // Свежая отметка проверки — иначе IsCountable не засчитает сервер
                    // в «рабочих» (последний успех старше часа).
                    node.CheckedAtUtc = DateTime.UtcNow;
                    node.LastSeenUtc = DateTime.UtcNow;
                    NodeStore.RestoreRejected(new[] { node });
                    AppLog.Write("nodes",
                        $"Автоподключение отсева: «{node.Name}» ответил через туннель — возвращён в рабочие");
                }
                else
                {
                    var stored = RejectedNodeStore.Load();
                    var entry = stored.FirstOrDefault(candidate =>
                        candidate.Node.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
                    if (entry is not null)
                    {
                        item.Node = node;
                        entry.Node = node;
                        entry.NodeData = RejectedNode.ToCached(node);
                        entry.NodeData.TriedAtUtc = DateTime.UtcNow;
                        entry.Reason = started
                            ? "трафик через туннель не прошёл"
                            : "не поднялся при автоподключении";
                        RejectedNodeStore.Save(stored);
                    }

                    AppLog.Write("nodes", started
                        ? $"Автоподключение отсева: «{node.Name}» поднялся, но запрос не прошёл — оставлен"
                        : $"Автоподключение отсева: «{node.Name}» не поднялся — оставлен");
                }

                // Останавливаем только свой VPN: если пользователь успел переключиться,
                // его подключение не трогаем.
                if (stillOurs)
                {
                    VpnStarter.Stop(context);
                    await WaitForStateAsync(
                        TunnelState.Stopped, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Автоподключение отсева не удалось", exception);
        }
        finally
        {
            if (previous is not null)
            {
                NodeStore.Select(previous, persist: false);
            }

            NodeStore.PersistenceSuppressed = suppressBefore;
            Volatile.Write(ref _running, 0);
            // «Проверено» ставим после проверок, а не в начале: ранний выход
            // (VPN занят, автоматика выключена) не должен навсегда отключать
            // проверку до перезапуска приложения.
            if (attempted)
            {
                Interlocked.Exchange(ref _done, 1);
            }
        }
    }

    /// <summary>
    /// Настоящий запрос через поднятый туннель. Clash API просит ядро соединиться
    /// с тестовым адресом через конкретный outbound — это и есть подтверждение,
    /// что сервер не только «поднял TUN», но и пропускает трафик.
    /// </summary>
    private static async Task<bool> HasRealTrafficAsync(ProxyNode node)
    {
        try
        {
            using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (await api.TestDelayAsync(node.Tag, 5000, CancellationToken.None)
                        .ConfigureAwait(false) is not null)
                {
                    return true;
                }

                await Task.Delay(700).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Проверка трафика отсева не удалась", exception);
        }

        return false;
    }

    private static async Task<bool> WaitForStateAsync(TunnelState state, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (TunnelService.State == state)
            {
                return true;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return TunnelService.State == state;
    }
}
