namespace VieNLink.Mobile;

/// <summary>Итог перепроверки отсева.</summary>
internal sealed record RejectedCheckResult(int Tested, int Working, int Restored, string? Error);

/// <summary>
/// Перепроверка отсева тем же путём, что и кнопка «Перепроверить всё»:
/// временное ядро в основном процессе, серверы проверяются по одному.
/// Открывшиеся возвращаются в рабочие, остальные остаются в отсеве.
/// Автоматический вызов — из <see cref="ProbeScheduler"/> раз в 5 минут.
/// </summary>
internal static class RejectedCheckService
{
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    public static async Task<RejectedCheckResult> RecheckAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new RejectedCheckResult(0, 0, 0, "Перепроверка уже идёт");
        }

        try
        {
            NodeStore.EnsureLoaded();
            if (TunnelService.State == TunnelState.Running || CoreRuntime.IsRunning)
            {
                return new RejectedCheckResult(0, 0, 0,
                    "Остановите VPN перед перепроверкой отсева");
            }

            var items = RejectedNodeStore.Load();
            if (items.Count == 0)
            {
                return new RejectedCheckResult(0, 0, 0, "В отсеве нет серверов для проверки");
            }

            var nodes = items
                .Select(item => item.Node)
                .Take(NodeTester.MaxBatch)
                .ToList();
            progress?.Report($"Перепроверяю отсев: {nodes.Count} серверов");
            // По одному: тогда DNS и HTTP идут через тот же сервер, что и кнопка
            // «Подключение». Общий конфиг на много серверов отправлял DNS через
            // первый попавшийся сервер, и живые VLESS/Reality браковались.
            var tested = new List<ProxyNode>(nodes.Count);
            foreach (var node in nodes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                progress?.Report($"Проверяю из отсева: {node.Name}");
                await NodeTester.TestNodesAsync(new[] { node }, progress, cancellationToken);
                tested.Add(node);
            }

            var testedById = tested.ToDictionary(
                node => node.Id, StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            var stored = RejectedNodeStore.Load();
            var working = new List<ProxyNode>();
            foreach (var item in stored)
            {
                if (!testedById.TryGetValue(item.Node.Id, out var testedNode))
                {
                    continue;
                }

                item.Node = testedNode;
                item.RejectedAtUtc = now;
                if (testedNode.Status == NodeStatus.Working)
                {
                    testedNode.FailCount = 0;
                    testedNode.LastSeenUtc = now;
                    working.Add(testedNode);
                }
            }

            RejectedNodeStore.Save(stored);
            var restored = working.Count == 0 || NodeStore.RestoreRejected(working);
            return new RejectedCheckResult(
                tested.Count,
                tested.Count(node => node.Status == NodeStatus.Working),
                restored ? working.Count : 0,
                null);
        }
        catch (System.OperationCanceledException)
        {
            return new RejectedCheckResult(0, 0, 0, "Перепроверка отменена");
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Перепроверка отсева не удалась", exception);
            return new RejectedCheckResult(0, 0, 0,
                "Ошибка перепроверки: " + exception.Message);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
