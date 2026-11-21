using System.Text;
using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>
/// Основной процесс запускает проверку в :probe и применяет результаты.
/// Проверка не трогает основное ядро и VPN: у неё свой процесс, порты и папка кэша.
/// </summary>
internal static class ProbeClient
{
    /// <summary>Ответ на попытку проверки, когда :probe уже занят другой партией.</summary>
    public const string BusyError = "Проверка уже идёт";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    private static CancellationTokenSource? _current;
    private static volatile bool _paused;

    /// <summary>
    /// Поиск остановлен пользователем: фоновые партии не запускаются, пока он сам
    /// не нажмёт «Проверить узлы» или кнопку сервиса.
    /// </summary>
    public static bool IsPaused => _paused;

    /// <summary>
    /// Останавливает текущую проверку и приостанавливает фоновый поиск до следующего
    /// явного действия пользователя. Процесс :probe получает команду завершиться,
    /// чтобы не продолжать тратить трафик после отмены.
    /// </summary>
    public static void Cancel()
    {
        _paused = true;
        try
        {
            _current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Проверка уже завершилась.
        }

        StopProbeProcess();
        ReportActivity("Поиск узлов остановлен", 15);
        AppLog.Write("probe", "Поиск узлов остановлен пользователем");
    }

    /// <summary>Снова разрешает поиск: пользователь нажал «Проверить» или кнопку сервиса.</summary>
    public static void Resume()
    {
        if (_paused)
        {
            _paused = false;
            AppLog.Write("probe", "Поиск узлов возобновлён");
        }
    }

    /// <summary>Гасит отдельный процесс проверки: после отмены ему нечего делать.</summary>
    private static void StopProbeProcess()
    {
        try
        {
            var context = Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(ProbeService))
                .SetAction(ProbeProtocol.StopAction);
            context.StartService(intent);
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Не удалось остановить процесс проверки", exception);
        }
    }

    private static string? _activity;
    private static DateTime _activityUntilUtc;

    /// <summary>
    /// Что сейчас делает проверка (включая фоновую): строка для шапки и экрана «Узлы».
    /// Погасает сама, чтобы не застрять на экране после завершения.
    /// </summary>
    public static string? ActivityText =>
        _activity is not null && DateTime.UtcNow < _activityUntilUtc ? _activity : null;

    private static void ReportActivity(string text, int holdSeconds = 10)
    {
        _activity = text;
        _activityUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, holdSeconds));
    }

    /// <summary>
    /// Проверяет накопившиеся/давно проверенные узлы небольшой партией.
    /// <paramref name="recheckWorking"/> — ручная проверка: перепроверяются и рабочие
    /// узлы, иначе после первого прохода кнопка «Проверить» ничего бы не делала.
    /// </summary>
    public static async Task<NodeTestSummary> RunBatchAsync(
        int max,
        CancellationToken cancellationToken,
        IProgress<ProbeProgress>? progress = null,
        bool recheckWorking = false,
        Func<ProxyNode, int>? sourceScore = null)
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

        if (_paused)
        {
            return new NodeTestSummary(0, 0, 0, "Поиск узлов остановлен");
        }

        // Рабочие узлы перепроверяем чаще, чем раз в IdleRecheckHours — по
        // NodeLimits.WorkingRecheckMinutes (15 мин): бесплатный узел живёт минуты,
        // и «рабочий» из кэша без перепроверки обещает кнопке узел, которого уже нет.
        var workingCutoff = DateTime.UtcNow.AddMinutes(-NodeLimits.WorkingRecheckMinutes);
        var candidates = NodeStore.OrderForConfig(NodeStore.Snapshot())
            // Один узел — одна проверка: одинаковые outbound'ы не попадают в партию дважды.
            .DistinctBy(node => node.Tag, StringComparer.OrdinalIgnoreCase)
            .Where(node => node.Status == NodeStatus.Unknown ||
                           (node.Status == NodeStatus.Working &&
                            (recheckWorking || node.CheckedAtUtc is null ||
                             node.CheckedAtUtc < workingCutoff)))
            // Сначала непроверенные и самые свежие из источников: бесплатные узлы живут
            // недолго, и проверять вчерашний список полезнее, чем недельный. Затем — узлы
            // из источников, которые уже давали живые узлы (sourceScore).
            // Но первыми идут рабочие с устаревшей проверкой: именно они показаны
            // в счётчиках, и их смерть важнее поиска среди непроверенных.
            .OrderBy(node => node.Status == NodeStatus.Working ? 0 : 1)
            .ThenBy(node => node.TriedAtUtc is null ? 0 : 1)
            .ThenByDescending(node => node.LastSeenUtc)
            .ThenByDescending(node => sourceScore?.Invoke(node) ?? 0)
            .Take(Math.Clamp(max, 1, 200))
            .ToList();

        if (candidates.Count == 0)
        {
            return new NodeTestSummary(0, 0, 0, NodeStore.Nodes.Count == 0
                ? "Список узлов пуст: сначала обновите источники"
                : "Проверять нечего: все узлы проверены недавно");
        }

        return await RunAsync(candidates, cancellationToken, progress).ConfigureAwait(false);
    }

    /// <summary>
    /// Держит минимум рабочих узлов: проверяет партии, пока рабочих не станет
    /// не меньше <see cref="NodeLimits.MinWorkingNodes"/> или партии не кончатся.
    /// Возвращает итоговое число рабочих узлов.
    /// </summary>
    public static async Task<int> EnsureWorkingQuotaAsync(
        int maxBatches,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (ProbeScheduler.IsProbeProcess())
        {
            return 0;
        }

        NodeStore.EnsureLoaded();
        var working = NodeStore.Summary().Working;
        for (var batch = 1; batch <= Math.Max(1, maxBatches); batch++)
        {
            if (cancellationToken.IsCancellationRequested ||
                TunnelService.IsBypassMode ||
                working >= NodeLimits.MinWorkingNodes)
            {
                break;
            }

            var summary = await RunBatchAsync(batchSize, cancellationToken).ConfigureAwait(false);
            if (summary.Tested == 0)
            {
                break;
            }

            working = NodeStore.Summary().Working;
            AppLog.Write("probe",
                $"Партия {batch}: проверено {summary.Tested}, рабочих {working} из {NodeLimits.MinWorkingNodes}");
        }

        return working;
    }

    /// <summary>
    /// Заботится о сервисе: если рабочего узла с доступом к нему нет, проверяет партии
    /// непроверенных узлов, пока такой не появится. Сначала берутся узлы из источников,
    /// которые уже давали рабочий доступ к этому сервису. Нужно для гарантии
    /// «у кнопки YouTube всегда есть живой узел».
    /// </summary>
    public static async Task<ProxyNode?> EnsureServiceNodeAsync(
        NodeStore.QuickService service,
        int maxBatches,
        CancellationToken cancellationToken)
    {
        if (ProbeScheduler.IsProbeProcess())
        {
            return null;
        }

        NodeStore.EnsureLoaded();
        var existing = NodeStore.PickForService(service);
        if (existing is not null)
        {
            return existing;
        }

        for (var batch = 1; batch <= Math.Max(1, maxBatches); batch++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            // После каждой партии пересчитываем: источники, которые уже дали доступ
            // к сервису, проверяются первыми — так нужный узел находится быстрее.
            var scores = BuildSourceScores(NodeStore.Snapshot(), node => Reached(node, service));
            var batchStarted = DateTime.UtcNow;
            var summary = await RunBatchAsync(
                NodeTester.MaxBatch,
                cancellationToken,
                progress: null,
                recheckWorking: false,
                sourceScore: node => SourceScoreOf(scores, node.SourceName)).ConfigureAwait(false);

            if (summary.Tested == 0)
            {
                return null;
            }

            var found = PickFreshForService(service, batchStarted);
            if (found is not null)
            {
                AppLog.Write("probe", $"{service}: рабочий узел найден автоматически, партий {batch}");
                return found;
            }

            AppLog.Write("probe", $"{service}: в партии {batch} подходящих узлов нет");
        }

        return null;
    }

    /// <summary>
    /// Узел с доступом к сервису, проверенный после указанного момента. Именно он годится
    /// для подключения: старый «рабочий» узел мог уже умереть, а откат партии его не обновляет.
    /// </summary>
    public static ProxyNode? PickFreshForService(NodeStore.QuickService service, DateTime sinceUtc)
    {
        return NodeStore.Snapshot()
            .Where(node => node.Status == NodeStatus.Working &&
                           node.CheckedAtUtc is { } checkedAt && checkedAt >= sinceUtc &&
                           Reached(node, service))
            .OrderByDescending(node => node.OpenDomains)
            .ThenBy(node => node.DelayMs ?? int.MaxValue)
            .FirstOrDefault();
    }

    /// <summary>Доля узлов источника, у которых есть доступ к сервису (0–1000).</summary>
    private static Dictionary<string, int> BuildSourceScores(
        IReadOnlyList<ProxyNode> snapshot,
        Func<ProxyNode, bool> reached)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in snapshot.GroupBy(
                     node => node.SourceName ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase))
        {
            var total = group.Count();
            scores[group.Key] = total == 0 ? 0 : group.Count(reached) * 1000 / total;
        }

        return scores;
    }

    private static int SourceScoreOf(Dictionary<string, int> scores, string? sourceName) =>
        sourceName is not null && scores.TryGetValue(sourceName, out var score) ? score : 0;

    /// <summary>Есть ли у узла подтверждённый доступ к сервису.</summary>
    private static bool Reached(ProxyNode node, NodeStore.QuickService service) =>
        NodeAvailability.OpenedFor(node, service switch
        {
            NodeStore.QuickService.YouTube => "youtube",
            NodeStore.QuickService.Telegram => "telegram",
            NodeStore.QuickService.Discord => "discord",
            _ => "universal",
        });

    /// <summary>Запускает :probe и ждёт файл результатов. null — занято или не удалось.</summary>
    public static async Task<NodeTestSummary> RunAsync(
        IReadOnlyList<ProxyNode> nodes,
        CancellationToken cancellationToken,
        IProgress<ProbeProgress>? progress = null)
    {
        if (nodes.Count == 0)
        {
            return new NodeTestSummary(0, 0, 0, null);
        }

        if (_paused)
        {
            return new NodeTestSummary(0, 0, 0, "Поиск узлов остановлен");
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new NodeTestSummary(0, 0, 0, BusyError);
        }

        // Внутренняя отмена (кнопка «Остановить») должна прерывать и ожидание файла:
        // иначе интерфейс висел бы до конца партии в :probe.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _current = linked;
        var token = linked.Token;

        try
        {
            progress?.Report(new ProbeProgress("Готовлю проверку…", 0, nodes.Count, 0));
            ReportActivity("Проверяю узлы: готовлю проверку…", 30);
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
                token).ConfigureAwait(false);
            try
            {
                File.Delete(ProbeProtocol.ResultPath);
            }
            catch (IOException)
            {
                // Прошлый файл перезапишется.
            }

            try
            {
                File.Delete(ProbeProtocol.ProgressPath);
            }
            catch (IOException)
            {
                // Прошлый ход проверки перезапишется.
            }

            var context = Android.App.Application.Context;
            var intent = new Android.Content.Intent(context, typeof(ProbeService))
                .PutExtra(ProbeProtocol.ExtraRequest, ProbeProtocol.RequestPath);
            try
            {
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                {
                    context.StartForegroundService(intent);
                }
                else
                {
                    context.StartService(intent);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("probe", "Не удалось запустить отдельный процесс проверки", exception);
                return new NodeTestSummary(0, 0, 0, "Не удалось запустить фоновую проверку");
            }

            // Ждём результат до 10 минут. Сервис работает в своём процессе и не блокирует UI.
            // Параллельно читаем файл хода проверки: пользователь видит, что процесс идёт.
            ProbeResultFile? result = null;
            var started = DateTime.UtcNow;
            var lastProgressAt = DateTime.MinValue;
            ProbeProgressFile? lastProgress = null;
            for (var attempt = 0; attempt < 1200; attempt++)
            {
                if (token.IsCancellationRequested)
                {
                    return new NodeTestSummary(0, 0, 0, "Поиск узлов остановлен");
                }

                if (File.Exists(ProbeProtocol.ResultPath))
                {
                    try
                    {
                        result = JsonSerializer.Deserialize<ProbeResultFile>(
                            await File.ReadAllTextAsync(ProbeProtocol.ResultPath, token)
                                .ConfigureAwait(false),
                            ProbeProtocol.Json);
                        break;
                    }
                    catch (JsonException)
                    {
                        // Файл ещё дописывается — пробуем снова.
                    }
                }

                var state = TryReadProgress();
                if (state is not null)
                {
                    lastProgressAt = DateTime.UtcNow;
                    if (lastProgress is null ||
                        lastProgress.Tested != state.Tested ||
                        lastProgress.Working != state.Working ||
                        !string.Equals(lastProgress.Stage, state.Stage, StringComparison.Ordinal))
                    {
                        lastProgress = state;
                        var line = state.Total > 0
                            ? $"Проверяю узлы: {state.Tested} из {state.Total} · рабочих {state.Working}"
                            : "Проверяю узлы…";
                        progress?.Report(new ProbeProgress(
                            state.Stage, state.Tested, state.Total, state.Working));
                        // Фоновая проверка идёт без подписчика прогресса — строку для шапки
                        // пишем здесь, иначе снаружи не видно, что узлы проверяются.
                        ReportActivity(line);
                    }
                }
                else if (DateTime.UtcNow - started > TimeSpan.FromSeconds(20))
                {
                    return new NodeTestSummary(0, 0, 0,
                        "Отдельный процесс проверки не запустился — нажмите «Проверить» ещё раз");
                }
                else if (lastProgressAt != DateTime.MinValue &&
                         DateTime.UtcNow - lastProgressAt > TimeSpan.FromSeconds(90))
                {
                    return new NodeTestSummary(0, 0, 0,
                        "Проверка не отвечает: процесс проверки остановлен системой");
                }

                await Task.Delay(500, token).ConfigureAwait(false);
            }

            if (result is null)
            {
                ReportActivity("Фоновая проверка не ответила", 15);
                return new NodeTestSummary(0, 0, 0, "Фоновая проверка не ответила");
            }

            if (result.Error is not null && result.Nodes.Count == 0)
            {
                ReportActivity(result.Error, 15);
                return new NodeTestSummary(0, 0, 0, result.Error);
            }

            var summary = Apply(result, nodes);
            ReportActivity(summary.Error is null
                ? $"Проверено {summary.Tested}: рабочих {summary.Working}"
                : summary.Error, 20);
            return summary;
        }
        catch (OperationCanceledException)
        {
            ReportActivity("Поиск узлов остановлен", 15);
            return new NodeTestSummary(0, 0, 0, "Поиск узлов остановлен");
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Ошибка фоновой проверки", exception);
            ReportActivity("Ошибка проверки: " + exception.Message, 15);
            return new NodeTestSummary(0, 0, 0, "Ошибка проверки: " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_current, linked))
            {
                _current = null;
            }

            Gate.Release();
        }
    }

    /// <summary>Читает файл хода проверки. Неполный JSON — норма: процесс пишет его прямо сейчас.</summary>
    private static ProbeProgressFile? TryReadProgress()
    {
        try
        {
            if (!File.Exists(ProbeProtocol.ProgressPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProbeProgressFile>(
                File.ReadAllText(ProbeProtocol.ProgressPath), ProbeProtocol.Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
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
            node.TriedAtUtc = item.TriedAtUtc;
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
