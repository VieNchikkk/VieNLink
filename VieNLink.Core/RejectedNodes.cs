using System.IO;
using System.Text.Json.Serialization;

namespace VieNLink;

/// <summary>Рабочий сервер, который перестал работать. Хранится для ручной перепроверки.</summary>
internal sealed class RejectedNode
{
    /// <summary>Сериализуемая копия со всеми полями, включая OutboundJson.</summary>
    public CachedNode NodeData { get; set; } = new();

    /// <summary>Старый формат 1.3.19: ProxyNode без OutboundJson. Читаем и перестраиваем.</summary>
    [JsonPropertyName("Node")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProxyNode? LegacyNode { get; set; }

    public DateTime RejectedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = "перестал работать";

    [JsonIgnore]
    public ProxyNode Node { get; set; } = new();

    public static RejectedNode Create(ProxyNode node, string reason)
    {
        return new RejectedNode
        {
            NodeData = ToCached(node),
            Node = node,
            Reason = reason,
            RejectedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Человекочитаемая причина смерти: сколько провалов подряд и когда был последний
    /// успех. По ней в логе видно, добил сервер ложный провал или реальная смерть.
    /// </summary>
    public static string Describe(ProxyNode node)
    {
        var ageText = node.CheckedAtUtc is not { } checkedAt
            ? "успешной проверки не было"
            : FormatAge(DateTime.UtcNow - checkedAt);
        return $"{node.FailCount} провалов подряд, {ageText}";
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 1 ? $"последний успех {age.TotalHours:0} ч {age.Minutes} мин назад"
        : age.TotalMinutes >= 1 ? $"последний успех {age.TotalMinutes:0} мин назад"
        : $"последний успех {age.TotalSeconds:0} с назад";

    public static CachedNode ToCached(ProxyNode node) => new()
    {
        RawLink = node.RawLink,
        SourceName = node.SourceName,
        Protocol = node.Protocol,
        Name = node.Name,
        Server = node.Server,
        Port = node.Port,
        Tag = node.Tag,
        Identity = node.Identity,
        OutboundJson = node.OutboundJson,
        DelayMs = node.DelayMs,
        CheckedAtUtc = node.CheckedAtUtc,
        TriedAtUtc = node.TriedAtUtc,
        LastSeenUtc = node.LastSeenUtc,
        IsMine = node.IsMine,
        SpeedMbps = node.SpeedMbps,
        AllowInsecure = node.AllowInsecure,
        YoutubeOk = node.YoutubeOk,
        TelegramOk = node.TelegramOk,
        UniversalOk = node.UniversalOk,
        DiscordOk = node.DiscordOk,
        OpenDomains = node.OpenDomains,
        Status = node.Status,
        FailCount = node.FailCount,
    };

    public static ProxyNode? FromCached(CachedNode data)
    {
        if (string.IsNullOrWhiteSpace(data.RawLink))
        {
            return null;
        }

        var node = SubscriptionParser.Parse(data.RawLink, data.SourceName).FirstOrDefault();
        if (node is null)
        {
            return null;
        }

        node.Name = string.IsNullOrWhiteSpace(data.Name) ? node.Name : data.Name;
        node.Tag = string.IsNullOrWhiteSpace(data.Tag) ? node.Tag : data.Tag;
        node.Identity = data.Identity;
        node.OutboundJson = string.IsNullOrWhiteSpace(data.OutboundJson) ? node.OutboundJson : data.OutboundJson;
        node.DelayMs = data.DelayMs;
        node.CheckedAtUtc = data.CheckedAtUtc;
        node.TriedAtUtc = data.TriedAtUtc;
        node.LastSeenUtc = data.LastSeenUtc;
        node.IsMine = data.IsMine;
        node.SpeedMbps = data.SpeedMbps;
        node.AllowInsecure = data.AllowInsecure;
        node.YoutubeOk = data.YoutubeOk;
        node.TelegramOk = data.TelegramOk;
        node.UniversalOk = data.UniversalOk;
        node.DiscordOk = data.DiscordOk;
        node.OpenDomains = data.OpenDomains;
        node.Status = data.Status;
        node.FailCount = data.FailCount;
        return node;
    }
}

/// <summary>
/// Отдельный файл «отсева»: сюда попадают только серверы, которые раньше были рабочими,
/// но провалили проверку. Из общего кэша они удаляются, поэтому без этого файла
/// восстановить их для ручной перепроверки было бы нельзя.
/// </summary>
internal static class RejectedNodeStore
{
    public const int MaximumCount = 5000;

    /// <summary>
    /// Сколько живёт запись отсева. Сутки — по просьбе владельца: сервер, который не
    /// подтвердил работу за сутки, из отсева удаляется, и файл не растёт годами.
    /// Повторный провал того же сервера обновляет дату и продлевает срок.
    /// </summary>
    public const int RetentionHours = 24;

    public static List<RejectedNode> Load()
    {
        var items = JsonFileStore.Read<List<RejectedNode>>(AppPaths.RejectedNodesPath) ?? new();
        var cutoff = DateTime.UtcNow.AddHours(-RetentionHours);
        var expired = 0;
        var result = new List<RejectedNode>();
        foreach (var item in items.OrderByDescending(item => item.RejectedAtUtc))
        {
            if (item.RejectedAtUtc < cutoff)
            {
                expired++;
                continue;
            }

            var node = RejectedNode.FromCached(item.NodeData);
            if (node is null && item.LegacyNode is not null)
            {
                node = RejectedNode.FromCached(RejectedNode.ToCached(item.LegacyNode));
            }

            if (node is null)
            {
                continue;
            }

            item.Node = node;
            item.LegacyNode = null;
            result.Add(item);
            if (result.Count >= MaximumCount)
            {
                break;
            }
        }

        if (expired > 0)
        {
            // Переписываем файл один раз, когда что-то действительно истекло.
            AppLog.Write("nodes", $"Отсев: удалено по сроку {RetentionHours} ч — {expired}, осталось {result.Count}");
            Save(result);
        }

        return result;
    }

    public static void Save(IEnumerable<RejectedNode> items)
    {
        var list = items
            .Where(item => item.Node is not null)
            .OrderByDescending(item => item.RejectedAtUtc)
            .Take(MaximumCount)
            .Select(item => new RejectedNode
            {
                NodeData = RejectedNode.ToCached(item.Node),
                LegacyNode = null,
                RejectedAtUtc = item.RejectedAtUtc,
                Reason = item.Reason,
            })
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
            items.Add(RejectedNode.Create(node, reason));
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
