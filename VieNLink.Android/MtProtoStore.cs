using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>
/// MTProto-прокси Telegram на телефоне: сбор из включённых Telegram-каналов через
/// RSS-зеркала, проверка TCP, хранение и сроки жизни нерабочих записей.
/// Логика сбора и проверки — в общем ядре (<see cref="MtProtoService"/>), здесь кэш.
/// </summary>
internal static class MtProtoStore
{
    private static readonly object Gate = new();
    private static List<MtProtoProxy>? _items;
    private static bool _collecting;
    private static int _testing;

    public static bool IsCollecting => _collecting;

    public static bool IsTesting => Volatile.Read(ref _testing) == 1;

    public static List<MtProtoProxy> Snapshot()
    {
        lock (Gate)
        {
            EnsureLoaded();
            PruneCore();
            return _items!
                .OrderBy(proxy => proxy.Status == MtProtoStatus.Working ? 0 : 1)
                .ThenBy(proxy => proxy.DelayMs ?? int.MaxValue)
                .ThenBy(proxy => proxy.Server)
                .ToList();
        }
    }

    public static int WorkingCount
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _items!.Count(proxy => proxy.Status == MtProtoStatus.Working);
            }
        }
    }

    public static MtProtoProxy? Best()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _items!
                .Where(proxy => proxy.Status == MtProtoStatus.Working && proxy.DelayMs is not null)
                .OrderBy(proxy => proxy.DelayMs)
                .FirstOrDefault();
        }
    }

    private static void EnsureLoaded()
    {
        if (_items is not null)
        {
            return;
        }

        try
        {
            _items = MtProtoService.LoadCache();
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Не удалось прочитать кэш MTProto", exception);
            _items = new List<MtProtoProxy>();
        }
    }

    /// <summary>Собирает прокси из включённых Telegram-источников. Работает без VPN: RSS-зеркала.</summary>
    public static async Task<string> CollectAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (_collecting)
        {
            return "Сбор MTProto уже идёт";
        }

        _collecting = true;
        try
        {
            NodeStore.EnsureLoaded();
            var sources = NodeStore.SnapshotSources()
                .Where(source => source.Enabled)
                .Where(source => source.Kind == SourceKind.Telegram ||
                                 source.Url.Contains("t.me", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sources.Count == 0)
            {
                return "Нет включённых Telegram-каналов: добавьте канал в «Источники»";
            }

            var collected = new List<MtProtoProxy>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Читаю канал: " + source.Name);
                try
                {
                    var proxies = await MtProtoService
                        .FetchAsync(source, cancellationToken, forceRss: true)
                        .ConfigureAwait(false);
                    collected.AddRange(proxies);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("mtproto", $"{source.Name}: не удалось собрать", exception);
                }
            }

            var added = Merge(collected);
            AndroidSettingsStore.Current.LastMtProtoCollectUtc = DateTime.UtcNow;
            AndroidSettingsStore.Save();
            Save();

            var summary = collected.Count == 0
                ? "Прокси в каналах не найдены"
                : $"Найдено {collected.Count}, новых {added}, всего {Snapshot().Count}";
            AppLog.Write("mtproto", "Сбор MTProto: " + summary);
            progress?.Report(summary);

            if (added > 0)
            {
                await TestAsync(progress, cancellationToken, includeDead: false).ConfigureAwait(false);
            }

            return summary;
        }
        finally
        {
            _collecting = false;
        }
    }

    /// <summary>Проверяет рабочие и часть нерабочих прокси: TCP-доступность.</summary>
    public static async Task<string> TestAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool includeDead = true)
    {
        if (Interlocked.CompareExchange(ref _testing, 1, 0) != 0)
        {
            return "Проверка MTProto уже идёт";
        }

        try
        {
            var snapshot = Snapshot();
            var working = snapshot
                .Where(proxy => proxy.Status != MtProtoStatus.Dead)
                .Take(MtProtoSchedule.WorkingBatch)
                .ToList();

            if (includeDead)
            {
                working.AddRange(snapshot
                    .Where(proxy => proxy.Status == MtProtoStatus.Dead)
                    .OrderByDescending(proxy => proxy.DeadAtUtc ?? DateTime.MinValue)
                    .Take(MtProtoSchedule.DeadBatch));
            }

            if (working.Count == 0)
            {
                return "MTProto-прокси не собраны";
            }

            await MtProtoService.TestAsync(working, progress, null, cancellationToken).ConfigureAwait(false);
            AndroidSettingsStore.Current.LastMtProtoTestUtc = DateTime.UtcNow;
            AndroidSettingsStore.Save();
            Save();

            var alive = working.Count(proxy => proxy.Status == MtProtoStatus.Working);
            var summary = $"Проверено {working.Count}: рабочих {alive}";
            progress?.Report(summary);
            return summary;
        }
        finally
        {
            Volatile.Write(ref _testing, 0);
        }
    }

    /// <summary>Добавляет прокси, найденные автопоиском источников.</summary>
    public static int AddDiscovered(IEnumerable<MtProtoProxy> proxies)
    {
        var list = proxies.ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        var added = Merge(list);
        if (added > 0)
        {
            Save();
        }

        return added;
    }

    private static int Merge(IEnumerable<MtProtoProxy> incoming)    {
        lock (Gate)
        {
            EnsureLoaded();
            var byId = _items!.ToDictionary(proxy => proxy.Id, StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var proxy in incoming)
            {
                if (byId.TryGetValue(proxy.Id, out var existing))
                {
                    existing.SourceName = MergeSource(existing.SourceName, proxy.SourceName);
                    continue;
                }

                _items.Add(proxy);
                byId[proxy.Id] = proxy;
                added++;
            }

            PruneCore();
            return added;
        }
    }

    private static string MergeSource(string current, string incoming)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return incoming;
        }

        if (string.IsNullOrWhiteSpace(incoming) ||
            current.Split(", ").Any(name => name.Equals(incoming, StringComparison.OrdinalIgnoreCase)))
        {
            return current;
        }

        return current + ", " + incoming;
    }

    /// <summary>Удаляет просроченные нерабочие и лишние записи. Возвращает число удалённых.</summary>
    public static int Prune()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var removed = PruneCore();
            if (removed > 0)
            {
                Save();
            }

            return removed;
        }
    }

    private static int PruneCore()
    {
        var now = DateTime.UtcNow;
        var removed = _items!.RemoveAll(proxy => MtProtoRetention.IsExpired(proxy, now));
        removed += MtProtoRetention.TrimDead(_items);
        return removed;
    }

    private static void Save()
    {
        try
        {
            MtProtoService.SaveCache(_items!);
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Не удалось сохранить кэш MTProto", exception);
        }
    }

    /// <summary>Время последнего сбора: для автоматики.</summary>
    public static DateTime LastCollectUtc => AndroidSettingsStore.Current.LastMtProtoCollectUtc;

    public static bool IsDue(DateTime nowUtc) =>
        nowUtc - LastCollectUtc >= TimeSpan.FromMinutes(MtProtoSchedule.CollectMinutes);
}
