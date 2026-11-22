namespace VieNLink;

/// <summary>
/// Порядок серверов при автоматическом переключении и кнопке «Дальше».
/// Выбранный сервис важнее скорости: сначала идут живые серверы, которые его открывают,
/// затем остальные — «и так далее», пока не найдётся рабочий. Без этого при падении
/// «Универсального» приложение могло переключиться на сервер без универсального доступа.
/// </summary>
internal static class NodeFailover
{
    /// <summary>
    /// Перемежает серверы по источникам: один большой список мёртвых серверов не должен
    /// занимать всю партию проверки. Порядок источников — как в уже отсортированном списке
    /// (сильные источники первыми), внутри источника порядок сохраняется.
    /// </summary>
    public static List<ProxyNode> InterleaveBySource(IEnumerable<ProxyNode> nodes, int take)
    {
        if (take <= 0)
        {
            return new List<ProxyNode>();
        }

        var groups = new List<List<ProxyNode>>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var key = FirstSource(node.SourceName);
            if (!index.TryGetValue(key, out var groupIndex))
            {
                groupIndex = groups.Count;
                index[key] = groupIndex;
                groups.Add(new List<ProxyNode>());
            }

            groups[groupIndex].Add(node);
        }

        var result = new List<ProxyNode>(Math.Min(take, groups.Sum(group => group.Count)));
        for (var round = 0; result.Count < take; round++)
        {
            var added = false;
            foreach (var group in groups)
            {
                if (round >= group.Count)
                {
                    continue;
                }

                result.Add(group[round]);
                added = true;
                if (result.Count >= take)
                {
                    break;
                }
            }

            if (!added)
            {
                break;
            }
        }

        return result;
    }

    private static string FirstSource(string? sourceName) =>
        SplitSources(sourceName).FirstOrDefault() ?? string.Empty;

    /// <summary>Названия источников сервера (в кэше они склеены через «, »).</summary>
    public static IReadOnlyList<string> SplitSources(string? sourceName) =>
        string.IsNullOrWhiteSpace(sourceName)
            ? Array.Empty<string>()
            : sourceName.Split(", ", StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Лучший результат среди источников сервера (0 — данных нет).</summary>
    public static int BestSourceScore(IReadOnlyDictionary<string, int> scores, string? sourceName)
    {
        var best = 0;
        foreach (var source in SplitSources(sourceName))
        {
            if (scores.TryGetValue(source, out var score) && score > best)
            {
                best = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Рабочие, свежие и разрешённые настройками серверы в порядке приоритета:
    /// выбранный сервис → меньше задержка → другое семейство камуфляжа → личные →
    /// больше открытых доменов → лучше оценка маскировки.
    /// <paramref name="service"/> — ключ как у кнопок (youtube/telegram/discord/universal);
    /// null — приоритета сервиса нет.
    /// <paramref name="failedFamily"/> — семейство упавшего сервера: по возможности
    /// берём другое, чтобы не наступить на тот же заблокированный шаблон.
    /// </summary>
    public static List<ProxyNode> Rank(
        IEnumerable<ProxyNode> nodes,
        string? service,
        DateTime nowUtc,
        bool onlyMine,
        string? failedFamily = null,
        bool preferTelegram = false,
        Func<ProxyNode, bool>? filter = null)
    {
        return nodes
            .Where(node => NodeAvailability.IsUsable(
                node, NodeLimits.WorkingRecheckMinutes, onlyMine, nowUtc))
            .Where(node => filter is null || filter(node))
            .OrderByDescending(node => service is not null &&
                                       NodeAvailability.OpenedFor(node, service))
            .ThenBy(node => node.DelayMs ?? int.MaxValue)
            .ThenByDescending(node => failedFamily is not null &&
                                      NodeRanking.Family(node) != failedFamily)
            .ThenByDescending(node => preferTelegram && node.TelegramOk == true)
            .ThenByDescending(node => node.IsMine)
            .ThenByDescending(node => node.OpenDomains)
            .ThenByDescending(node => node.RankScore)
            .ToList();
    }
}
