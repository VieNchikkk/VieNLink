using System.IO;

namespace VieNLink;

/// <summary>Рабочий сервер, который перестал работать. Хранится для ручной перепроверки.</summary>
internal sealed class RejectedNode
{
    public required ProxyNode Node { get; set; }
    public DateTime RejectedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = "перестал работать";
}

/// <summary>
/// Отдельный файл «отсева»: сюда попадают только серверы, которые раньше были рабочими,
/// но провалили проверку. Из общего кэша они удаляются, поэтому без этого файла
/// восстановить их для ручной перепроверки было бы нельзя.
/// </summary>
internal static class RejectedNodeStore
{
    public const int MaximumCount = 2000;

    public static List<RejectedNode> Load()
    {
        var items = JsonFileStore.Read<List<RejectedNode>>(AppPaths.RejectedNodesPath) ?? new();
        return items
            .Where(item => item.Node is not null && !string.IsNullOrWhiteSpace(item.Node.RawLink))
            .OrderByDescending(item => item.RejectedAtUtc)
            .Take(MaximumCount)
            .ToList();
    }

    public static void Save(IEnumerable<RejectedNode> items)
    {
        var list = items
            .Where(item => item.Node is not null)
            .OrderByDescending(item => item.RejectedAtUtc)
            .Take(MaximumCount)
            .ToList();
        try
        {
            JsonFileStore.WriteCompact(AppPaths.RejectedNodesPath, list);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("nodes", "Не удалось сохранить файл отсева", exception);
        }
    }

    /// <summary>Добавляет или обновляет сервер в отсеве.</summary>
    public static void Add(ProxyNode node, string reason = "перестал работать")
    {
        var items = Load();
        var existing = items.FirstOrDefault(item =>
            item.Node.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            items.Add(new RejectedNode { Node = node, RejectedAtUtc = DateTime.UtcNow, Reason = reason });
        }
        else
        {
            existing.Node = node;
            existing.RejectedAtUtc = DateTime.UtcNow;
            existing.Reason = reason;
        }

        Save(items);
        AppLog.Write("nodes", $"В отсев: {node.Name} ({reason})");
    }

    public static void Remove(IEnumerable<string> nodeIds)
    {
        var ids = nodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = Load();
        if (items.RemoveAll(item => ids.Contains(item.Node.Id)) > 0)
        {
            Save(items);
        }
    }
}
