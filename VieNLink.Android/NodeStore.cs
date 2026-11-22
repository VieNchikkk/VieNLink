namespace VieNLink.Mobile;

/// <summary>Выбранный сервер переживает перезапуск приложения.</summary>
internal sealed class SelectedNodeState
{
    public string? Id { get; set; }
}

/// <summary>
/// Серверы и источники Android-версии. Логика сбора, склейки повторов и лимитов живёт
/// в общем ядре (<c>NodeSourceService</c>, <c>NodeDedup</c>, <c>NodeLimits</c>),
/// здесь только хранение в памяти, сохранение кэша и выделение сервера для подключения.
/// </summary>
internal static class NodeStore
{
    /// <summary>Сколько серверов максимум кладём в конфиг телефона: больше — дольше старт и больше памяти.</summary>
    public const int MobileMaxNodesInConfig = 200;

    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _selectedId;
    private static ProxyNode? _selected;

    public static List<SourceDefinition> Sources { get; } = new();
    public static List<ProxyNode> Nodes { get; } = new();

    public static string SelectedPath => Path.Combine(AppPaths.DataDirectory, "selected-node.json");

    public static AppSettings Settings => SettingsStore.Current;

    /// <summary>Кэш уже прочитан: тяжёлую загрузку не повторяем и не держим UI-поток.</summary>
    public static bool IsLoaded
    {
        get
        {
            lock (Gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>Добавляет сервер, введённый вручную, и возвращает его Id.</summary>
    public static string? AddManual(ProxyNode node)
    {
        Merge(new[] { node });
        return node.Id;
    }

    /// <summary>Читает источники, кэш серверов и выбранный сервер. Тяжёлая операция — вызывать из фона.</summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            AppPaths.EnsureDirectories();
            AppLog.Initialize();

            // Настройки читаются до источников: миграция схемы включает все источники
            // (`EnableAllSourcesOnce`), и LoadSources должен увидеть этот флаг.
            var settings = Settings;

            var sources = NodeSourceService.LoadSources();
            Sources.Clear();
            Sources.AddRange(sources);

            var freshAfter = DateTime.UtcNow.AddDays(-Math.Max(1, settings.KeepUnseenNodeDays));
            var cached = NodeSourceService.LoadCache(out var dropped, out var duplicates, freshAfter);
            Nodes.Clear();
            Nodes.AddRange(cached);

            _selectedId = JsonFileStore.Read<SelectedNodeState>(SelectedPath)?.Id;
            _selected = null;
            _loaded = true;

            Android.Util.Log.Info("VieNLink",
                $"серверы: {Nodes.Count} из кэша, источников: {Sources.Count}, отброшено: {dropped}, дублей: {duplicates}");
        }
    }

    /// <summary>Снимок списка для адаптера: UI не должен держать ссылку на изменяемый список.</summary>
    public static List<ProxyNode> Snapshot()
    {
        lock (Gate)
        {
            return Nodes.ToList();
        }
    }

    /// <summary>
    /// Счётчики для шапки и экрана «Серверы» без копирования всего списка: шапка обновляется
    /// каждые несколько секунд, и полный Snapshot из 12 000 серверов на каждый тик давал бы
    /// лишние аллокации и давление на сборщик мусора.
    /// </summary>
    /// <remarks>
    /// Считаются только свежие проверки: сервер, проверенный много часов назад, мог уже
    /// умереть. Иначе значок обещает сервер, которого кнопка сервиса не найдёт.
    /// </remarks>
    public static (int Total, int Working, int Universal, int Youtube, int Telegram, int Discord) Summary()
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var recheckMinutes = NodeLimits.WorkingRecheckMinutes;
            var onlyMine = Settings.ConnectOnlyMine;
            var working = 0;
            var universal = 0;
            var youtube = 0;
            var telegram = 0;
            var discord = 0;
            foreach (var node in Nodes)
            {
                if (!NodeAvailability.IsUsable(node, recheckMinutes, onlyMine, now))
                {
                    continue;
                }

                working++;
                if (NodeAvailability.OpenedFor(node, "universal"))
                {
                    universal++;
                }

                if (NodeAvailability.OpenedFor(node, "youtube"))
                {
                    youtube++;
                }

                if (NodeAvailability.OpenedFor(node, "telegram"))
                {
                    telegram++;
                }

                if (NodeAvailability.OpenedFor(node, "discord"))
                {
                    discord++;
                }
            }

            return (Nodes.Count, working, universal, youtube, telegram, discord);
        }
    }

    /// <summary>Подробная сводка для экрана «Серверы» — один проход без копии списка.</summary>
    public readonly record struct NodeSummary(
        int Total,
        int Working,
        int Fresh,
        int Universal,
        int Youtube,
        int Telegram,
        int Discord,
        int Mine,
        int BestDomains,
        DateTime? LastChecked);

    public static NodeSummary DetailedSummary()
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var recheckMinutes = NodeLimits.WorkingRecheckMinutes;
            var onlyMine = Settings.ConnectOnlyMine;
            var working = 0;
            var fresh = 0;
            var universal = 0;
            var youtube = 0;
            var telegram = 0;
            var discord = 0;
            var mine = 0;
            var bestDomains = 0;
            DateTime? lastChecked = null;
            foreach (var node in Nodes)
            {
                if (node.IsMine)
                {
                    mine++;
                }

                if (node.CheckedAtUtc is { } checkedAt &&
                    (lastChecked is null || checkedAt > lastChecked))
                {
                    lastChecked = checkedAt;
                }

                if (node.Status == NodeStatus.Working)
                {
                    working++;
                }

                if (!NodeAvailability.IsUsable(node, recheckMinutes, onlyMine, now))
                {
                    continue;
                }

                fresh++;
                bestDomains = Math.Max(bestDomains, node.OpenDomains);
                if (NodeAvailability.OpenedFor(node, "universal"))
                {
                    universal++;
                }

                if (NodeAvailability.OpenedFor(node, "youtube"))
                {
                    youtube++;
                }

                if (NodeAvailability.OpenedFor(node, "telegram"))
                {
                    telegram++;
                }

                if (NodeAvailability.OpenedFor(node, "discord"))
                {
                    discord++;
                }
            }

            return new NodeSummary(Nodes.Count, working, fresh, universal, youtube, telegram, discord,
                mine, bestDomains, lastChecked);
        }
    }

    public static List<SourceDefinition> SnapshotSources()
    {
        lock (Gate)
        {
            return Sources.ToList();
        }
    }

    public static ProxyNode? Selected
    {
        get
        {
            lock (Gate)
            {
                if (_selectedId is null)
                {
                    return null;
                }

                // Ссылка на объект кэшируется: шапка спрашивает выбранный сервер каждые
                // 3 секунды, а линейный поиск по 12 000 серверам на каждом такте не нужен.
                if (_selected is not null &&
                    _selected.Id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    return _selected;
                }

                _selected = Nodes.FirstOrDefault(
                    node => node.Id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase));
                return _selected;
            }
        }
    }

    public static void Select(ProxyNode node)
    {
        lock (Gate)
        {
            _selectedId = node.Id;
            _selected = node;
            JsonFileStore.Write(SelectedPath, new SelectedNodeState { Id = node.Id });
        }
    }

    /// <summary>Добавляет источник или одиночную ссылку. Возвращает сообщение для интерфейса.</summary>
    public static string AddSource(string input, string? name, bool mine)
    {
        var url = (input ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return "Укажите ссылку на подписку или на сервер";
        }

        // Happ умеет импортировать подписку ссылкой happ://add/… — поддержим тот же формат.
        if (url.StartsWith("happ://add/", StringComparison.OrdinalIgnoreCase))
        {
            var payload = Uri.UnescapeDataString(url["happ://add/".Length..]);
            if (!payload.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                Base64Util.TryDecode(payload, out var decoded) &&
                decoded.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                payload = decoded.Trim();
            }

            url = payload;
        }

        lock (Gate)
        {
            EnsureLoaded();

            var scheme = url.Contains("://", StringComparison.Ordinal)
                ? url[..url.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant()
                : string.Empty;

            if (scheme is "vless" or "vmess" or "trojan" or "ss" or "hysteria2" or "hy2")
            {
                var nodeName = string.IsNullOrWhiteSpace(name) ? "Мой сервер" : name.Trim();
                var single = new SourceDefinition
                {
                    Name = nodeName,
                    Url = url,
                    Kind = SourceKind.SingleLink,
                    IsMine = mine,
                };
                Sources.Add(single);
                NodeSourceService.SaveSources(Sources);
                return "Добавлен сервер: " + nodeName;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return "Нужна ссылка http(s) на подписку, список или канал t.me/s/…";
            }

            if (Sources.Any(source => source.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                return "Такой источник уже добавлен";
            }

            var sourceName = name?.Trim() ?? string.Empty;
            if (sourceName.Length == 0)
            {
                sourceName = uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
                    ? "Telegram " + uri.AbsolutePath.Trim('/')
                    : uri.Host;
            }

            Sources.Add(new SourceDefinition
            {
                Name = sourceName,
                Url = url,
                Kind = SourceKinds.Classify(url),
                IsMine = mine,
            });

            NodeSourceService.SaveSources(Sources);
            return "Источник добавлен: " + sourceName;
        }
    }

    public static void RemoveSource(SourceDefinition source)
    {
        lock (Gate)
        {
            Sources.Remove(source);
            NodeSourceService.SaveSources(Sources);
        }
    }

    public static void SetSourceEnabled(SourceDefinition source, bool enabled)
    {
        lock (Gate)
        {
            source.Enabled = enabled;
            NodeSourceService.SaveSources(Sources);
        }
    }

    /// <summary>Загружает только один источник (например, сразу после добавления).</summary>
    public static async Task<string> ImportSourceAsync(
        SourceDefinition source,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        EnsureLoaded();

        FetchOutcome outcome;
        try
        {
            outcome = await NodeSourceService.FetchAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Ошибка одного источника не должна ронять импорт: записываем её в источник.
            outcome = new FetchOutcome { Source = source, Error = exception.Message };
            AppLog.Write("sources", $"Источник «{source.Name}»: ошибка загрузки", exception);
        }

        source.LastCheckedUtc = DateTime.UtcNow;
        source.LastError = outcome.Error;
        source.LastNodeCount = outcome.Nodes.Count;

        if (outcome.Nodes.Count > 0)
        {
            Merge(outcome.Nodes);
        }

        lock (Gate)
        {
            NodeSourceService.SaveSources(Sources);
        }

        var message = outcome.Error is null
            ? $"{source.Name}: {outcome.Nodes.Count} серверов"
            : $"{source.Name}: ошибка — {outcome.Error}";
        progress?.Report(message);
        return message;
    }

    /// <summary>Полное обновление всех включённых источников.</summary>
    public static async Task<string> RefreshAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        EnsureLoaded();

        var collected = await NodeSourceService
            .CollectAsync(Sources, progress, cancellationToken)
            .ConfigureAwait(false);

        Merge(collected.Nodes);

        lock (Gate)
        {
            NodeSourceService.SaveSources(Sources);
        }

        var working = Nodes.Count(node => node.Status == NodeStatus.Working);
        var summary = $"Источники: {collected.Nodes.Count} серверов, в списке {Nodes.Count}, рабочих {working}";
        AppLog.Write("sources", summary);
        progress?.Report(summary);
        return summary;
    }

    /// <summary>
    /// Добавляет серверы к уже собранным, сохраняя задержки и статусы проверок
    /// (как в Windows-версии: иначе после каждого обновления статусы терялись бы).
    /// </summary>
    private static void Merge(IReadOnlyCollection<ProxyNode> incoming)
    {
        List<ProxyNode> snapshot;
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var map = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Nodes)
            {
                map[node.Id] = node;
            }

            foreach (var node in incoming)
            {
                node.LastSeenUtc ??= now;

                if (map.TryGetValue(node.Id, out var existing))
                {
                    node.DelayMs ??= existing.DelayMs;
                    node.CheckedAtUtc ??= existing.CheckedAtUtc;
                    node.TriedAtUtc ??= existing.TriedAtUtc;

                    if (node.Status == NodeStatus.Unknown)
                    {
                        node.YoutubeOk = existing.YoutubeOk;
                        node.TelegramOk = existing.TelegramOk;
                        node.UniversalOk = existing.UniversalOk;
                        node.DiscordOk = existing.DiscordOk;
                        node.OpenDomains = existing.OpenDomains;
                        node.FailCount = existing.FailCount;
                        node.SpeedMbps = existing.SpeedMbps;
                        node.Status = existing.Status;
                    }

                    if (existing.LastSeenUtc is { } seen &&
                        (node.LastSeenUtc is null || seen > node.LastSeenUtc))
                    {
                        node.LastSeenUtc = seen;
                    }
                }

                map[node.Id] = node;
            }

            var merged = NodeDedup.Collapse(map.Values);
            var (trimmed, _) = NodeLimits.Trim(merged, Settings.MaxTotalNodes);

            Nodes.Clear();
            Nodes.AddRange(trimmed);
            _selected = null;
            snapshot = Nodes.ToList();
        }

        // Сериализация и запись кэша — вне блокировки: UI не должен ждать диск,
        // когда открывает шапку или экран «Серверы» сразу после фоновой работы.
        NodeSourceService.SaveCache(snapshot);
    }

    /// <summary>
    /// Подрезает список до текущего <see cref="AppSettings.MaxTotalNodes"/> сразу после
    /// изменения лимита, не дожидаясь обновления источников. Возвращает число удалённых.
    /// </summary>
    public static int TrimToLimit()
    {
        List<ProxyNode> snapshot;
        int removed;
        lock (Gate)
        {
            EnsureLoaded();
            var (trimmed, dropped) = NodeLimits.Trim(Nodes, Settings.MaxTotalNodes);
            removed = dropped;
            if (removed <= 0)
            {
                return 0;
            }

            Nodes.Clear();
            Nodes.AddRange(trimmed);
            _selected = null;
            snapshot = Nodes.ToList();
        }

        NodeSourceService.SaveCache(snapshot);
        AppLog.Write("nodes", $"Лимит списка: удалено {removed}, осталось {snapshot.Count}");
        return removed;
    }

    /// <summary>Убирает весь кэш серверов (источники остаются).</summary>
    public static int ClearCache()
    {
        int removed;
        lock (Gate)
        {
            EnsureLoaded();
            removed = Nodes.Count;
            Nodes.Clear();
            _selectedId = null;
            _selected = null;
            // Удаляем и сам кэш, и резервную копию: иначе следующее чтение
            // восстановило бы только что очищенные серверы из nodes.json.bak.
            foreach (var path in new[] { SelectedPath, AppPaths.NodesPath, AppPaths.NodesPath + ".bak" })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                    // Файл освободится при следующем запуске.
                }
            }
        }

        return removed;
    }

    /// <summary>Сохраняет результаты проверки в кэш (задержки и статусы не теряются между запусками).</summary>
    public static void SaveNodes()
    {
        List<ProxyNode> snapshot;
        lock (Gate)
        {
            snapshot = Nodes.ToList();
        }

        NodeSourceService.SaveCache(snapshot);
    }

    /// <summary>
    /// Добавляет источники и серверы, найденные автопоиском. Возвращает число новых источников.
    /// Уже известные адреса не дублируются, проверенные статусы сохраняются.
    /// </summary>
    public static int AddDiscovered(DiscoveryResult result)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var known = Sources
                .Select(source => source.Url)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var source in result.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Url) || !known.Add(source.Url))
                {
                    continue;
                }

                source.IsAutoDiscovered = true;
                source.AddedAtUtc = DateTime.UtcNow;
                Sources.Add(source);
                added++;
            }

            if (result.Nodes.Count > 0)
            {
                Merge(result.Nodes);
            }

            if (added > 0)
            {
                NodeSourceService.SaveSources(Sources);
            }

            return added;
        }
    }

    /// <summary>
    /// Убирает мёртвые серверы: непроверенный сервер удаляется с первого провала,
    /// рабочий — после <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд.
    /// Возвращает число удалённых. Кэш не пишет: вызывающий делает это один раз
    /// через <see cref="SaveNodes"/> (иначе на партию приходилось две записи).
    /// </summary>
    public static int DropDeadNodes(IEnumerable<ProxyNode> tested)
    {
        var dead = tested
            .Where(node => node.Status == NodeStatus.Dead)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dead.Count == 0)
        {
            return 0;
        }

        int removed;
        lock (Gate)
        {
            removed = Nodes.RemoveAll(node => dead.Contains(node.Id));
            if (removed > 0)
            {
                _selected = null;
            }
        }

        if (removed > 0)
        {
            AppLog.Write("nodes", $"Убрано нерабочих серверов: {removed}");
        }

        return removed;
    }

    /// <summary>Серверы в порядке выбора: личные, маскированные, быстрые.</summary>
    public static List<ProxyNode> OrderForConfig(IEnumerable<ProxyNode> nodes) =>
        nodes
            .Where(node => node.Status != NodeStatus.Dead && !string.IsNullOrWhiteSpace(node.OutboundJson))
            .OrderByDescending(node => node.IsMine)
            .ThenByDescending(node => node.RankScore)
            .ThenByDescending(node => node.DelayMs is not null)
            .ThenByDescending(node => node.OpenDomains)
            .ThenBy(node => node.DelayMs ?? int.MaxValue)
            .ThenBy(node => node.Protocol)
            .ToList();

    /// <summary>
    /// Лучший проверенный сервер по тем же правилам, что в Windows: если включён
    /// Telegram-канал — сначала с доступом к Telegram, затем личные, больше доменов,
    /// маскировка, задержка; предпочитается сервер в пределах <see cref="AppSettings.MaxDelayMs"/>.
    /// </summary>
    private static List<ProxyNode> RankedVerified()
    {
        var settings = Settings;
        return Nodes
            .Where(node => node.Status == NodeStatus.Working &&
                           NodeAvailability.HasSuccess(node) &&
                           !string.IsNullOrWhiteSpace(node.OutboundJson) &&
                           (!settings.ConnectOnlyMine || node.IsMine))
            .OrderBy(node => node.DelayMs ?? int.MaxValue)
            .ThenByDescending(node => node.IsMine)
            .ThenByDescending(node => node.OpenDomains)
            .ThenByDescending(node => node.RankScore)
            .ToList();
    }

    private static ProxyNode? BestVerified(ProxyNode? fallback = null)
    {
        var settings = Settings;
        var now = DateTime.UtcNow;
        var ranked = RankedVerified()
            .Where(node => NodeAvailability.IsUsable(
                node, NodeLimits.WorkingRecheckMinutes, settings.ConnectOnlyMine, now))
            .ToList();
        var quick = ranked.FirstOrDefault(
            node => node.DelayMs is not null && node.DelayMs <= settings.MaxDelayMs);
        return quick ?? ranked.FirstOrDefault() ?? fallback;
    }

    /// <summary>
    /// Сервер для автоподключения: проверенный рабочий, открывающий больше заблокированных
    /// доменов, затем самый быстрый. Задержка null означает недавний провал
    /// (см. <c>NodeHealth</c>), поэтому такие серверы не берём. null — здоровых нет.
    /// </summary>
    private static ProxyNode? PickAutoNode(ProxyNode? selected) => BestVerified(selected);

    /// <summary>
    /// Запоминает лучший проверенный сервер по правилам Windows (Telegram, личные,
    /// домены, маскировка, задержка). Вызывается после проверки, чтобы вручную
    /// не выбирать «Лучший». Возвращает выбранный сервер или null, если живых нет.
    /// </summary>
    public static ProxyNode? SelectBestWorking()
    {
        lock (Gate)
        {
            var best = BestVerified();
            if (best is null)
            {
                return null;
            }

            if (!string.Equals(_selectedId, best.Id, StringComparison.OrdinalIgnoreCase))
            {
                _selectedId = best.Id;
                JsonFileStore.Write(SelectedPath, new SelectedNodeState { Id = best.Id });
                AppLog.Write("nodes", "Автоматически выбран лучший сервер: " + best.Name);
            }

            return best;
        }
    }

    /// <summary>Сервис, для которого кнопка главного экрана ищет уже проверенный сервер.</summary>
    public enum QuickService
    {
        Universal,
        YouTube,
        Telegram,
        Discord,
    }

    /// <summary>
    /// Сервер для быстрого подключения к сервису: только свежие проверенные серверы,
    /// отсортированные скрытно от пользователя — сначала самые быстрые. Правило то же,
    /// что у счётчиков: число на значке и результат кнопки не должны расходиться.
    /// null — подходящих серверов нет.
    /// </summary>
    public static ProxyNode? PickForService(QuickService service, string? avoidId = null)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var recheckMinutes = NodeLimits.WorkingRecheckMinutes;
            var onlyMine = Settings.ConnectOnlyMine;
            return Nodes
                .Where(node => (avoidId is null || !node.Id.Equals(avoidId, StringComparison.OrdinalIgnoreCase)) &&
                               NodeAvailability.IsUsable(node, recheckMinutes, onlyMine, now) &&
                               OpenedFor(node, service))
                .OrderBy(node => node.DelayMs ?? int.MaxValue)
                .ThenByDescending(node => node.OpenDomains)
                .ThenByDescending(node => node.IsMine)
                .FirstOrDefault();
        }
    }

    /// <summary>Есть ли у сервера подтверждённый доступ к сервису.</summary>
    private static bool OpenedFor(ProxyNode node, QuickService service) =>
        NodeAvailability.OpenedFor(node, service switch
        {
            QuickService.YouTube => "youtube",
            QuickService.Telegram => "telegram",
            QuickService.Discord => "discord",
            _ => "universal",
        });

    /// <summary>
    /// Сервер, который прямо сейчас реально открывает сайт. Кэш мог устареть (сервер умер,
    /// пока телефон лежал), поэтому перед подключением проверяем всех кандидатов сервиса
    /// одной партией в :probe и берём первый живой. Так значок сервиса и результат кнопки
    /// сходятся за одно нажатие: не открывшиеся серверы сразу перестают считаться рабочими.
    /// Возвращает null, если живых нет.
    /// </summary>
    public static async Task<ProxyNode?> PickVerifiedAsync(
        QuickService? service,
        ProxyNode? preferred,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        string? avoidId = null)
    {
        await Task.Run(() => EnsureLoaded(), cancellationToken);

        // Снимок и отбор кандидатов — в фоне: сортировка тысяч серверов на UI-потоке
        // давала ANR при нажатии кнопок сервисов.
        var unique = await Task.Run(() =>
        {
            var candidates = new List<ProxyNode>();
            lock (Gate)
            {
                if (preferred is not null &&
                    !preferred.Id.Equals(avoidId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    (!Settings.ConnectOnlyMine || preferred.IsMine))
                {
                    candidates.Add(preferred);
                }

                if (service is { } wanted)
                {
                    var picked = PickForService(wanted, avoidId);
                    if (picked is not null)
                    {
                        candidates.Add(picked);
                    }

                    // Для конкретного сервиса не подставляем серверы без его доступа:
                    // иначе кнопка Telegram подключила бы сервер, где Telegram не открывается.
                    candidates.AddRange(RankedVerified()
                        .Where(node => OpenedFor(node, wanted) &&
                                       (avoidId is null ||
                                        !node.Id.Equals(avoidId, StringComparison.OrdinalIgnoreCase)))
                        .Take(8));
                }
                else
                {
                    candidates.AddRange(RankedVerified()
                        .Where(node => avoidId is null ||
                                       !node.Id.Equals(avoidId, StringComparison.OrdinalIgnoreCase))
                        .Take(4));
                }

                return candidates
                    .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(8)
                    .ToList();
            }
        }, cancellationToken);

        if (unique.Count == 0)
        {
            return null;
        }

        if (TunnelService.IsBypassMode)
        {
            // В TUN-обходе проверка серверов недоступна: берём как есть.
            Select(unique[0]);
            return unique[0];
        }

        progress?.Report($"Проверяю серверы перед подключением: {unique.Count}…");
        var summary = await ProbeClient.RunAsync(unique, cancellationToken).ConfigureAwait(false);
        if (summary.Error is not null && summary.Tested == 0)
        {
            // Проверка занята фоновой партией или процесс не поднялся: кнопка не должна
            // отвечать «сервера нет», если сервер со свежей проверкой уже есть. Берём его как
            // есть — фоновая перепроверка всё равно обновит статус в ближайшую минуту.
            var fallback = service is { } wantedFallback
                ? PickForService(wantedFallback, avoidId)
                : Selected;
            if (fallback is not null)
            {
                Select(fallback);
                return fallback;
            }

            return null;
        }

        var now = DateTime.UtcNow;
        var recheckMinutes = NodeLimits.WorkingRecheckMinutes;
        var onlyMine = Settings.ConnectOnlyMine;
        foreach (var node in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NodeAvailability.IsUsable(node, recheckMinutes, onlyMine, now))
            {
                continue;
            }

            if (service is { } wanted && !OpenedFor(node, wanted))
            {
                continue;
            }

            Select(node);
            return node;
        }

        return null;
    }

    /// <summary>
    /// Готовый конфиг TUN для VPN-службы: все живые серверы в selector, выбранный — по умолчанию.
    /// <paramref name="preferWorking"/> — для автозапуска после загрузки: человек сервер не
    /// выбирает, поэтому вместо прежнего (возможно, уже мёртвого) берём проверенный рабочий.
    /// null и текст ошибки — если подключать нечего.
    /// </summary>
    public static string? BuildTunnelConfig(out string? error, bool preferWorking = false)
    {
        EnsureLoaded();

        var selected = Selected;

        // Автозапуск: прежний выбор мог умереть, пока телефон был выключен. Рабочим
        // считаем только сервер с задержкой и без провалов; иначе оставляем как есть.
        if (preferWorking)
        {
            var auto = PickAutoNode(selected);
            if (auto is not null && (selected is null || auto.Id != selected.Id))
            {
                AppLog.Write("android", "Автоподключение: выбран рабочий сервер " + auto.Name);
                Select(auto);
                selected = auto;
            }
        }

        var ordered = OrderForConfig(Nodes);
        if (Settings.ConnectOnlyVerified)
        {
            ordered = ordered.Where(NodeAvailability.HasSuccess).ToList();
        }

        if (Settings.ConnectOnlyMine)
        {
            ordered = ordered.Where(node => node.IsMine).ToList();
        }

        if (ordered.Count == 0)
        {
            error = Settings.ConnectOnlyMine
                ? "Нет серверов в личных подписках: добавьте свою подписку или выключите «Только мои»"
                : Settings.ConnectOnlyVerified
                    ? "Нет проверенных серверов: обновите источники и нажмите «Проверить»"
                    : "Список серверов пуст: обновите источники";
            return null;
        }

        var cap = Math.Min(Math.Max(1, Settings.MaxNodesInConfig), MobileMaxNodesInConfig);
        // Защита от повторов: в конфиг не попадают два сервера с одинаковым outbound'ом (тегом).
        var candidates = ordered
            .DistinctBy(node => node.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(cap)
            .ToList();

        // Выбранный сервер обязан попасть в конфиг, даже если рейтинг вытеснил его за лимит.
        if (selected is not null && candidates.All(node => node.Id != selected.Id) &&
            (!Settings.ConnectOnlyVerified || NodeAvailability.HasSuccess(selected)) &&
            (!Settings.ConnectOnlyMine || selected.IsMine))
        {
            candidates.Insert(0, selected);
        }

        var settings = Settings.Clone();
        settings.TunnelMode = TunnelMode.Tun;
        // На Android приложения разделяет VpnService (split-tunnel), а не process_name
        // из Windows-настроек: чужие имена процессов иначе увели бы весь трафик напрямую.
        settings.OnlySelectedApps = false;
        settings.AppRouteMode = AppRouteMode.All;
        settings.AppList = string.Empty;

        var defaultTag = selected is not null && candidates.Contains(selected)
            ? selected.Tag
            : candidates[0].Tag;

        try
        {
            error = null;
            return SingBoxConfigBuilder.Build(
                candidates,
                defaultTag,
                CoreRuntime.MixedPort,
                CoreRuntime.ApiPort,
                CoreRuntime.ApiSecret,
                settings,
                // На Android локальный UDP/53 часто зарезан или подменяется — DNS идёт через сервер.
                dnsThroughProxy: true);
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return null;
        }
    }
}
