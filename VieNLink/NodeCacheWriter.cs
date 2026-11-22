namespace VieNLink;

/// <summary>
/// Запись кэша серверов: проверенные рабочие и куча непроверенных — разными файлами.
/// Рабочих единицы, их файл маленький и пишется после каждой проверки; кэш на десятки
/// тысяч непроверенных переписывается редко, чтобы не грузить диск и сборщик мусора.
/// </summary>
internal static class NodeCacheWriter
{
    /// <summary>Как часто можно переписывать кучу непроверенных (файл большой).</summary>
    private static readonly TimeSpan PoolInterval = TimeSpan.FromMinutes(1);

    private static readonly object Gate = new();
    private static DateTime _lastPoolUtc = DateTime.MinValue;

    /// <summary>
    /// Сохраняет серверы. <paramref name="force"/> — записать и кучу непроверенных сразу
    /// (обновление источников, смена лимита, ручное удаление).
    /// </summary>
    public static void Save(IEnumerable<ProxyNode> nodes, bool force = false)
    {
        var working = new List<ProxyNode>();
        var pool = new List<ProxyNode>();
        foreach (var node in nodes)
        {
            if (node.Status == NodeStatus.Working)
            {
                working.Add(node);
            }
            else
            {
                pool.Add(node);
            }
        }

        lock (Gate)
        {
            NodeSourceService.SaveCache(working, allowEmpty: true, AppPaths.WorkingNodesPath);

            var now = DateTime.UtcNow;
            if (!force && now - _lastPoolUtc < PoolInterval)
            {
                return;
            }

            NodeSourceService.SaveCache(pool, allowEmpty: true);
            _lastPoolUtc = now;
        }
    }

    /// <summary>Читает оба файла: рабочие первыми, затем непроверенные без повторов.</summary>
    public static List<ProxyNode> Load(DateTime? freshAfterUtc,
        out int dropped, out int duplicates, out int workingCount)
    {
        var pool = NodeSourceService.LoadCache(out dropped, out duplicates, freshAfterUtc);
        var working = NodeSourceService.LoadCache(out _, out _, freshAfterUtc, AppPaths.WorkingNodesPath);
        workingCount = working.Count;
        if (working.Count == 0)
        {
            return pool;
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<ProxyNode>(working.Count + pool.Count);
        foreach (var node in working)
        {
            if (known.Add(node.Id))
            {
                merged.Add(node);
            }
        }

        foreach (var node in pool)
        {
            if (known.Add(node.Id))
            {
                merged.Add(node);
            }
        }

        return merged;
    }
}
