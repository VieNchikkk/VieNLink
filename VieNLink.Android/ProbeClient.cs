using System.Text;
using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>
/// Основной процесс запускает проверку в :probe и применяет результаты.
/// Проверка не трогает основное ядро и VPN: у неё свой процесс, порты и папка кэша.
/// </summary>
internal static class ProbeClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    /// <summary>Проверяет накопившиеся/давно проверенные узлы небольшой партией.</summary>
    public static async Task<NodeTestSummary> RunBatchAsync(int max, CancellationToken cancellationToken)
    {
        if (ProbeScheduler.IsProbeProcess())
        {
            return new NodeTestSummary(0, 0, 0, null);
        }

        NodeStore.EnsureLoaded();
        if (TunnelService.IsBypassMode)
        {
            return new NodeTestSummary(0, 0, 0,
                "В TUN-обходе проверка узлов недоступна: он перехватывает весь трафик.");
        }

        var cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(NodeStore.Settings.IdleRecheckHours, 1, 168));
        var candidates = NodeStore.OrderForConfig(NodeStore.Snapshot())
            .Where(node => node.Status == NodeStatus.Unknown ||
                           (node.Status == NodeStatus.Working && node.CheckedAtUtc < cutoff))
            .OrderBy(node => node.Status == NodeStatus.Working)
            .ThenBy(node => node.CheckedAtUtc ?? DateTime.MinValue)
            .Take(Math.Clamp(max, 1, 200))
            .ToList();
        return await RunAsync(candidates, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Запускает :probe и ждёт файл результатов. null — занято или не удалось.</summary>
    public static async Task<NodeTestSummary> RunAsync(
        IReadOnlyList<ProxyNode> nodes,
        CancellationToken cancellationToken)
    {
        if (nodes.Count == 0)
        {
            return new NodeTestSummary(0, 0, 0, null);
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new NodeTestSummary(0, 0, 0, "Проверка уже идёт");
        }

        try
        {
            var request = new ProbeRequest
            {
                Nodes = nodes
                    .Select(node => new ProbeRequestNode { Id = node.Id, RawLink = node.RawLink })
                    .ToList(),
            };
            Directory.CreateDirectory(AppPaths.DataDirectory);
            await File.WriteAllTextAsync(
                ProbeProtocol.RequestPath,
                JsonSerializer.Serialize(request, ProbeProtocol.Json),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            try
            {
                File.Delete(ProbeProtocol.ResultPath);
            }
            catch (IOException)
            {
                // Прошлый файл перезапишется.
            }

            var context = Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(ProbeService))
                .PutExtra(ProbeProtocol.ExtraRequest, ProbeProtocol.RequestPath);
            try
            {
                context.StartService(intent);
            }
            catch (Exception exception)
            {
                AppLog.Write("probe", "Не удалось запустить отдельный процесс проверки", exception);
                return new NodeTestSummary(0, 0, 0, "Не удалось запустить фоновую проверку");
            }

            // Ждём результат до 10 минут. Сервис работает в своём процессе и не блокирует UI.
            ProbeResultFile? result = null;
            for (var attempt = 0; attempt < 1200; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new NodeTestSummary(0, 0, 0, "Проверка отменена");
                }

                if (File.Exists(ProbeProtocol.ResultPath))
                {
                    try
                    {
                        result = JsonSerializer.Deserialize<ProbeResultFile>(
                            await File.ReadAllTextAsync(ProbeProtocol.ResultPath, cancellationToken)
                                .ConfigureAwait(false),
                            ProbeProtocol.Json);
                        break;
                    }
                    catch (JsonException)
                    {
                        // Файл ещё дописывается — пробуем снова.
                    }
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            if (result is null)
            {
                return new NodeTestSummary(0, 0, 0, "Фоновая проверка не ответила");
            }

            if (result.Error is not null && result.Nodes.Count == 0)
            {
                return new NodeTestSummary(0, 0, 0, result.Error);
            }

            return Apply(result, nodes);
        }
        catch (OperationCanceledException)
        {
            return new NodeTestSummary(0, 0, 0, "Проверка отменена");
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Ошибка фоновой проверки", exception);
            return new NodeTestSummary(0, 0, 0, "Ошибка проверки: " + exception.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Переносит результаты из :probe в NodeStore и сохраняет кэш.</summary>
    private static NodeTestSummary Apply(ProbeResultFile result, IReadOnlyList<ProxyNode> requested)
    {
        var byId = NodeStore.Snapshot()
            .ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var working = 0;
        var universal = 0;
        var youtube = 0;
        var telegram = 0;
        var discord = 0;
        var bestDomains = 0;

        foreach (var item in result.Nodes)
        {
            if (!byId.TryGetValue(item.Id, out var node))
            {
                continue;
            }

            node.Status = item.Status;
            node.DelayMs = item.DelayMs;
            node.CheckedAtUtc = item.CheckedAtUtc;
            node.YoutubeOk = item.YoutubeOk;
            node.TelegramOk = item.TelegramOk;
            node.UniversalOk = item.UniversalOk;
            node.DiscordOk = item.DiscordOk;
            node.OpenDomains = item.OpenDomains;
            node.FailCount = item.FailCount;

            if (node.Status == NodeStatus.Working)
            {
                working++;
                if (node.UniversalOk == true)
                {
                    universal++;
                }

                if (node.YoutubeOk == true)
                {
                    youtube++;
                }

                if (node.TelegramOk == true)
                {
                    telegram++;
                }

                if (node.DiscordOk == true)
                {
                    discord++;
                }

                bestDomains = Math.Max(bestDomains, node.OpenDomains);
            }
        }

        var removed = NodeStore.DropDeadNodes(result.Nodes
            .Select(item => byId.TryGetValue(item.Id, out var node) ? node : null)
            .Where(node => node is not null)
            .Select(node => node!)
            .ToList());
        NodeStore.SaveNodes();
        var best = NodeStore.SelectBestWorking();
        AppLog.Write("probe",
            $"Результаты :probe: рабочих {working} из {result.Nodes.Count}, удалено {removed}" +
            (best is null ? ", живых нет" : ", выбран " + best.Name));

        return new NodeTestSummary(result.Nodes.Count, working, removed, result.Error,
            universal, youtube, telegram, discord, bestDomains);
    }
}
