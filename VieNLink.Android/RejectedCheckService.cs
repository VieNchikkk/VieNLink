namespace VieNLink.Mobile;

/// <summary>Итог перепроверки отсева.</summary>
internal sealed record RejectedCheckResult(int Tested, int Working, int Restored, string? Error);

/// <summary>
/// Перепроверка отсева через отдельный процесс :probe: он не трогает VpnService
/// и текущее подключение. DNS идёт через сам проверяемый сервер, серверы
/// проверяются по одному. Открывшиеся возвращаются в рабочие, остальные остаются.
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
            var items = RejectedNodeStore.Load();
            if (items.Count == 0)
            {
                return new RejectedCheckResult(0, 0, 0, "В отсеве нет серверов для проверки");
            }

            var batch = items
                .Take(NodeTester.MaxBatch)
                .ToList();
            progress?.Report($"Перепроверяю отсев: {batch.Count} серверов");
            await ProbeClient.RecheckRejectedAsync(batch, progress, cancellationToken);

            // Что вернулось: записи, которых больше нет в отсеве.
            var remaining = RejectedNodeStore.Load()
                .Select(item => item.Node.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var restored = batch.Count(item => !remaining.Contains(item.Node.Id));
            return new RejectedCheckResult(batch.Count, restored, restored, null);
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
