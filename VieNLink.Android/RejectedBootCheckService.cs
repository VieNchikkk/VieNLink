namespace VieNLink.Mobile;

/// <summary>
/// Один раз при запуске приложения, после стандартной стартовой перепроверки:
/// подключается по очереди к серверам из отсева тем же путём, что и кнопка
/// «Подключение» (настоящий TUN/VpnService + DNS через сервер). Если VPN поднялся —
/// сервер возвращается в рабочие; если нет — остаётся в отсеве. Если обычный VPN
/// уже подключён, ничего не запускает: на Android одновременно может быть только
/// один VpnService, иначе проверка переключила бы пользователя.
/// </summary>
internal static class RejectedBootCheckService
{
    private static int _done;
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    public static async Task RunAsync(Android.Content.Context context)
    {
        if (Interlocked.CompareExchange(ref _done, 1, 0) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        var previous = NodeStore.Selected;
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

            var items = RejectedNodeStore.Load();
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
                NodeStore.Select(node);
                VpnStarter.Start(context, config);
                var started = await WaitForStateAsync(
                    TunnelState.Running, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                var stillOurs = NodeStore.Selected?.Id is { } selectedId &&
                                selectedId.Equals(node.Id, StringComparison.OrdinalIgnoreCase);

                if (started && stillOurs)
                {
                    node.Status = NodeStatus.Working;
                    node.FailCount = 0;
                    // Свежая отметка проверки — иначе IsCountable не засчитает сервер
                    // в «рабочих» (последний успех старше часа).
                    node.CheckedAtUtc = DateTime.UtcNow;
                    node.LastSeenUtc = DateTime.UtcNow;
                    NodeStore.RestoreRejected(new[] { node });
                    AppLog.Write("nodes",
                        $"Автоподключение отсева: «{node.Name}» поднялся — возвращён в рабочие");
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
                        entry.Reason = "не поднялся при автоподключении";
                        RejectedNodeStore.Save(stored);
                    }

                    AppLog.Write("nodes",
                        $"Автоподключение отсева: «{node.Name}» не поднялся — оставлен");
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
                NodeStore.Select(previous);
            }

            Volatile.Write(ref _running, 0);
        }
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
