using System.Security.Cryptography;
using System.Text;

namespace VieNLink;

internal sealed class NegativeCacheItem
{
    public string Key { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}

internal sealed class NegativeCacheFile
{
    public int Version { get; set; } = 1;
    public DateTime SavedAtUtc { get; set; }
    public List<NegativeCacheItem> Items { get; set; } = new();
}

/// <summary>
/// Негативный кэш серверов: недавно подтверждённые провалы, которые не надо проверять
/// повторно. Экономит дорогие проверки ядром, когда источники каждый день присылают
/// одни и те же мёртвые ссылки.
///
/// Ключи разные по цене проверки:
/// • узел (ядро) — полный Id сервера, хешированный; не подавляет соседние UUID на том же
///   CDN-адресе, потому что они могут работать;
/// • TCP — host:port, короткий TTL: TCP-опрос дешёвый, адрес может ожить;
/// • успешная проверка снимает обе записи.
/// </summary>
internal static class NodeNegativeCache
{
    /// <summary>TCP-опрос не прошёл: адрес может ожить, поэтому держим недолго.</summary>
    public const int TcpDeadMinutes = 6 * 60;

    /// <summary>Непроверенный сервер не открыл сайт: сутки, чтобы не гонять его каждый сбор.</summary>
    public const int UnknownDeadHours = 24;

    /// <summary>Рабочий сервер подтверждённо умер: держим двое суток.</summary>
    public const int ConfirmedDeadHours = 48;

    /// <summary>Предел размера: один «шумный» источник не должен раздувать файл.</summary>
    public const int MaxEntries = 60_000;

    private static readonly object Gate = new();
    private static Dictionary<string, DateTime>? _items;
    private static bool _dirty;
    private static DateTime _lastSaveUtc = DateTime.MinValue;

    internal static string? PathOverride { get; set; }
    internal static DateTime? NowOverride { get; set; }

    /// <summary>
    /// Процесс :probe не пишет кэш: файл общий с основным процессом, и две записи
    /// затирали бы друг друга. Результаты применяет основной процесс.
    /// </summary>
    public static bool SuppressWrites { get; set; }

    private static string FilePath => PathOverride ?? AppPaths.ServerBlacklistPath;
    private static DateTime UtcNow => NowOverride ?? DateTime.UtcNow;

    /// <summary>Сколько серверов пропущено по кэшу за время работы процесса.</summary>
    public static int SuppressedCount { get; private set; }

    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return LoadLocked().Count;
            }
        }
    }

    /// <summary>
    /// Отбирает серверы, которые можно добавлять в кэш: «мои» подписки и seed не
    /// фильтруются, недавно провалившиеся — пропускаются. Возвращает число пропущенных.
    /// </summary>
    public static List<ProxyNode> FilterCollected(IEnumerable<ProxyNode> nodes, out int suppressed)
    {
        var kept = new List<ProxyNode>();
        suppressed = 0;
        foreach (var node in nodes)
        {
            if (!node.IsMine && IsBlocked(node))
            {
                suppressed++;
                continue;
            }

            kept.Add(node);
        }

        return kept;
    }

    /// <summary>Сервер (полный Id) или его TCP-эндпоинт недавно провалился.</summary>
    public static bool IsBlocked(ProxyNode node)
    {        if (node.IsMine || string.IsNullOrWhiteSpace(node.Server))
        {
            return false;
        }

        lock (Gate)
        {
            var now = UtcNow;
            var items = LoadLocked();
            if (PruneLocked(items, now))
            {
                SaveLocked();
            }

            var blocked = FindLocked(items, NodeKey(node), now) ||
                          FindLocked(items, TcpKey(node), now);
            if (blocked)
            {
                SuppressedCount++;
            }

            return blocked;
        }
    }

    /// <summary>Проверка ядром не прошла. Рабочий сервер держим дольше непроверенного.</summary>
    public static void RecordDead(ProxyNode node)
    {
        if (SuppressWrites || node.IsMine || string.IsNullOrWhiteSpace(node.Server))
        {
            return;
        }

        var hours = NodeAvailability.HasSuccess(node)
            ? ConfirmedDeadHours
            : UnknownDeadHours;
        Add(NodeKey(node), UtcNow.AddHours(hours));
    }

    /// <summary>TCP-отсев: адрес не ответил. Не считается провалом рабочего сервера.</summary>
    public static void RecordTcpDead(ProxyNode node)
    {
        if (SuppressWrites || node.IsMine || string.IsNullOrWhiteSpace(node.Server))
        {
            return;
        }

        Add(TcpKey(node), UtcNow.AddMinutes(TcpDeadMinutes));
    }

    /// <summary>Успешная проверка снимает блокировку и для узла, и для его эндпоинта.</summary>
    public static void Forget(ProxyNode node)
    {
        if (SuppressWrites || string.IsNullOrWhiteSpace(node.Server))
        {
            return;
        }

        lock (Gate)
        {
            var items = LoadLocked();
            var changed = items.Remove(NodeKey(node));
            changed |= items.Remove(TcpKey(node));
            if (changed)
            {
                _dirty = true;
                // Не переписываем файл на каждый рабочий сервер: хватит записи не чаще
                // раза в 30 секунд, а в конце партии вызывается Flush.
                SaveLocked(force: false);
            }
        }
    }

    /// <summary>Сбросить изменения на диск (в конце партии проверки).</summary>
    public static void Flush(bool force = true)
    {
        if (SuppressWrites)
        {
            return;
        }

        lock (Gate)
        {
            if (_items is null || !_dirty)
            {
                return;
            }

            if (!force && UtcNow - _lastSaveUtc < TimeSpan.FromMinutes(1))
            {
                return;
            }

            var now = UtcNow;
            if (PruneLocked(_items, now))
            {
                _dirty = true;
            }

            SaveLocked(force);
        }
    }

    internal static void ResetForTests(string? path = null, DateTime? now = null)
    {
        lock (Gate)
        {
            PathOverride = path;
            NowOverride = now;
            _items = null;
            _dirty = false;
            _lastSaveUtc = DateTime.MinValue;
            SuppressedCount = 0;
        }
    }

    internal static string NodeKey(ProxyNode node)
    {
        var id = node.Id ?? string.Empty;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        return "n:" + hash[..24];
    }

    internal static string TcpKey(ProxyNode node) =>
        "t:" + (node.EndpointKey ?? string.Empty).ToLowerInvariant();

    private static Dictionary<string, DateTime> LoadLocked()
    {
        if (_items is not null)
        {
            return _items;
        }

        _items = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var file = JsonFileStore.Read<NegativeCacheFile>(FilePath);
        var now = UtcNow;
        foreach (var item in file?.Items ?? new List<NegativeCacheItem>())
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.ExpiresUtc <= now)
            {
                continue;
            }

            if (_items.Count >= MaxEntries)
            {
                break;
            }

            _items[item.Key] = item.ExpiresUtc;
        }

        return _items;
    }

    private static bool FindLocked(Dictionary<string, DateTime> items, string key, DateTime now)
    {
        if (!items.TryGetValue(key, out var expires))
        {
            return false;
        }

        if (expires > now)
        {
            return true;
        }

        items.Remove(key);
        _dirty = true;
        return false;
    }

    private static void Add(string key, DateTime expiresUtc)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        lock (Gate)
        {
            var items = LoadLocked();
            if (items.TryGetValue(key, out var current) && current >= expiresUtc)
            {
                return;
            }

            items[key] = expiresUtc;
            _dirty = true;
            TrimLocked(items);
        }
    }

    private static bool PruneLocked(Dictionary<string, DateTime> items, DateTime now)
    {
        var expired = items
            .Where(pair => pair.Value <= now)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in expired)
        {
            items.Remove(key);
        }

        return expired.Count > 0;
    }

    private static void TrimLocked(Dictionary<string, DateTime> items)
    {
        if (items.Count <= MaxEntries)
        {
            return;
        }

        foreach (var pair in items
                     .OrderBy(pair => pair.Value)
                     .Take(items.Count - MaxEntries)
                     .ToList())
        {
            items.Remove(pair.Key);
        }
    }

    private static void SaveLocked(bool force = false)
    {
        if (!_dirty)
        {
            return;
        }

        var now = UtcNow;
        if (!force && now - _lastSaveUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }

        JsonFileStore.WriteCompact(FilePath, new NegativeCacheFile
        {
            Version = 1,
            SavedAtUtc = now,
            Items = _items!
                .Select(pair => new NegativeCacheItem { Key = pair.Key, ExpiresUtc = pair.Value })
                .OrderBy(item => item.ExpiresUtc)
                .ToList(),
        });
        _dirty = false;
        _lastSaveUtc = now;
    }
}
