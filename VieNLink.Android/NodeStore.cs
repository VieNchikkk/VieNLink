namespace VieNLink.Mobile;

/// <summary>Выбранный узел переживает перезапуск приложения.</summary>
internal sealed class SelectedNodeState
{
    public string? Id { get; set; }
}

/// <summary>
/// Узлы и источники Android-версии. Логика сбора, склейки повторов и лимитов живёт
/// в общем ядре (<c>NodeSourceService</c>, <c>NodeDedup</c>, <c>NodeLimits</c>),
/// здесь только хранение в памяти, сохранение кэша и выделение узла для подключения.
/// </summary>
internal static class NodeStore
{
    /// <summary>Сколько узлов максимум кладём в конфиг телефона: больше — дольше старт и больше памяти.</summary>
    public const int MobileMaxNodesInConfig = 200;

    private static readonly object Gate = new();
    private static bool _loaded;
    private static string? _selectedId;

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

    /// <summary>Читает источники, кэш узлов и выбранный узел. Тяжёлая операция — вызывать из фона.</summary>
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

            var sources = NodeSourceService.LoadSources();
            Sources.Clear();
            Sources.AddRange(sources);

            var settings = Settings;
            var freshAfter = DateTime.UtcNow.AddDays(-Math.Max(1, settings.KeepUnseenNodeDays));
            var cached = NodeSourceService.LoadCache(out var dropped, out var duplicates, freshAfter);
            Nodes.Clear();
            Nodes.AddRange(cached);

            _selectedId = JsonFileStore.Read<SelectedNodeState>(SelectedPath)?.Id;
            _loaded = true;

            Android.Util.Log.Info("VieNLink",
                $"узлы: {Nodes.Count} из кэша, источников: {Sources.Count}, отброшено: {dropped}, дублей: {duplicates}");
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
    /// Счётчики для шапки и экрана «Узлы» без копирования всего списка: шапка обновляется
    /// каждые несколько секунд, и полный Snapshot из 12 000 узлов на каждый тик давал бы
    /// лишние аллокации и давление на сборщик мусора.
    /// </summary>
    public static (int Total, int Working, int Universal, int Youtube, int Telegram, int Discord) Summary()
    {
        lock (Gate)
        {
            var working = 0;
            var universal = 0;
            var youtube = 0;
            var telegram = 0;
            var discord = 0;
            foreach (var node in Nodes)
            {
                if (node.Status != NodeStatus.Working)
                {
                    continue;
                }

                working++;
                if (node.UniversalOk == true || node.DelayMs is not null)
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
            }

            return (Nodes.Count, working, universal, youtube, telegram, discord);
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
                return _selectedId is null
                    ? null
                    : Nodes.FirstOrDefault(node => node.Id.Equals(_selectedId, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public static void Select(ProxyNode node)
    {
        lock (Gate)
        {
            _selectedId = node.Id;
            JsonFileStore.Write(SelectedPath, new SelectedNodeState { Id = node.Id });
        }
    }

    /// <summary>Добавляет источник или одиночную ссылку. Возвращает сообщение для интерфейса.</summary>
    public static string AddSource(string input, string? name, bool mine)
    {
        var url = (input ?? string.Empty).Trim();
        if (url.Length == 0)
        {
            return "Укажите ссылку на подписку или на узел";
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
                var nodeName = string.IsNullOrWhiteSpace(name) ? "Мой узел" : name.Trim();
                var single = new SourceDefinition
                {
                    Name = nodeName,
                    Url = url,
                    Kind = SourceKind.SingleLink,
                    IsMine = mine,
                };
                Sources.Add(single);
                NodeSourceService.SaveSources(Sources);
                return "Добавлен узел: " + nodeName;
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
                Kind = uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ? SourceKind.Telegram : SourceKind.Auto,
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
            ? $"{source.Name}: {outcome.Nodes.Count} узлов"
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
        var summary = $"Источники: {collected.Nodes.Count} узлов, в списке {Nodes.Count}, рабочих {working}";
        AppLog.Write("sources", summary);
        progress?.Report(summary);
        return summary;
    }

    /// <summary>
    /// Добавляет узлы к уже собранным, сохраняя задержки и статусы проверок
    /// (как в Windows-версии: иначе после каждого обновления статусы терялись бы).
    /// </summary>
    private static void Merge(IReadOnlyCollection<ProxyNode> incoming)
    {
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

            NodeSourceService.SaveCache(Nodes);
        }
    }

    /// <summary>
    /// Подрезает список до текущего <see cref="AppSettings.MaxTotalNodes"/> сразу после
    /// изменения лимита, не дожидаясь обновления источников. Возвращает число удалённых.
    /// </summary>
    public static int TrimToLimit()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var (trimmed, removed) = NodeLimits.Trim(Nodes, Settings.MaxTotalNodes);
            if (removed <= 0)
            {
                return 0;
            }

            Nodes.Clear();
            Nodes.AddRange(trimmed);
            NodeSourceService.SaveCache(Nodes);
            AppLog.Write("nodes", $"Лимит списка: удалено {removed}, осталось {Nodes.Count}");
            return removed;
        }
    }

    /// <summary>Убирает весь кэш узлов (источники остаются).</summary>
    public static int ClearCache()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var removed = Nodes.Count;
            Nodes.Clear();
            _selectedId = null;
            try
            {
                if (File.Exists(SelectedPath))
                {
                    File.Delete(SelectedPath);
                }
            }
            catch (IOException)
            {
                // Файл выбора перезапишется при следующем подключении.
            }

            NodeSourceService.SaveCache(Nodes);
            return removed;
        }
    }

    /// <summary>Сохраняет результаты проверки в кэш (задержки и статусы не теряются между запусками).</summary>
    public static void SaveNodes()
    {
        lock (Gate)
        {
            NodeSourceService.SaveCache(Nodes);
        }
    }

    /// <summary>
    /// Добавляет источники и узлы, найденные автопоиском. Возвращает число новых источников.
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
    /// Убирает мёртвые узлы: непроверенный узел удаляется с первого провала,
    /// рабочий — после <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд.
    /// Возвращает число удалённых.
    /// </summary>
    public static int DropDeadNodes(IEnumerable<ProxyNode> tested)
    {
        lock (Gate)
        {
            var dead = tested
                .Where(node => node.Status == NodeStatus.Dead)
                .Select(node => node.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (dead.Count == 0)
            {
                return 0;
            }

            var removed = Nodes.RemoveAll(node => dead.Contains(node.Id));
            if (removed > 0)
            {
                NodeSourceService.SaveCache(Nodes);
                AppLog.Write("nodes", $"Убрано нерабочих узлов: {removed}");
            }

            return removed;
        }
    }

    /// <summary>Узлы в порядке выбора: личные, маскированные, быстрые.</summary>
    public static List<ProxyNode> OrderForConfig(IEnumerable<ProxyNode> nodes) =>
        nodes
            .Where(node => node.Status != NodeStatus.Dead && !string.IsNullOrWhiteSpace(node.OutboundJson))
            .OrderByDescending(node => node.IsMine)
            .ThenByDescending(NodeRanking.Score)
            .ThenByDescending(node => node.DelayMs is not null)
            .ThenByDescending(node => node.OpenDomains)
            .ThenBy(node => node.DelayMs ?? int.MaxValue)
            .ThenBy(node => node.Protocol)
            .ToList();

    /// <summary>
    /// Лучший проверенный узел по тем же правилам, что в Windows: если включён
    /// Telegram-канал — сначала с доступом к Telegram, затем личные, больше доменов,
    /// маскировка, задержка; предпочитается узел в пределах <see cref="AppSettings.MaxDelayMs"/>.
    /// </summary>
    private static List<ProxyNode> RankedVerified()
    {
        var settings = Settings;
        var telegramNeeded = Sources.Any(source => source.Enabled && source.Kind == SourceKind.Telegram);
        return Nodes
            .Where(node => node.Status == NodeStatus.Working &&
                           node.DelayMs is not null &&
                           !string.IsNullOrWhiteSpace(node.OutboundJson) &&
                           (!settings.ConnectOnlyMine || node.IsMine))
            .OrderByDescending(node => telegramNeeded && node.TelegramOk == true)
            .ThenByDescending(node => node.IsMine)
            .ThenByDescending(node => node.OpenDomains)
            .ThenByDescending(NodeRanking.Score)
            .ThenBy(node => node.DelayMs)
            .ToList();
    }

    private static ProxyNode? BestVerified(ProxyNode? fallback = null)
    {
        var settings = Settings;
        var ranked = RankedVerified();
        var quick = ranked.FirstOrDefault(node => node.DelayMs <= settings.MaxDelayMs);
        return quick ?? ranked.FirstOrDefault() ?? fallback;
    }

    /// <summary>
    /// Узел для автоподключения: проверенный рабочий, открывающий больше заблокированных
    /// доменов, затем самый быстрый. Задержка null означает недавний провал
    /// (см. <c>NodeHealth</c>), поэтому такие узлы не берём. null — здоровых нет.
    /// </summary>
    private static ProxyNode? PickAutoNode(ProxyNode? selected) => BestVerified(selected);

    /// <summary>
    /// Запоминает лучший проверенный узел по правилам Windows (Telegram, личные,
    /// домены, маскировка, задержка). Вызывается после проверки, чтобы вручную
    /// не выбирать «Лучший». Возвращает выбранный узел или null, если живых нет.
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
                AppLog.Write("nodes", "Автоматически выбран лучший узел: " + best.Name);
            }

            return best;
        }
    }

    /// <summary>Сервис, для которого кнопка главного экрана ищет уже проверенный узел.</summary>
    public enum QuickService
    {
        Universal,
        YouTube,
        Telegram,
        Discord,
    }

    /// <summary>
    /// Узел для быстрого подключения к сервису: только уже проверенные рабочие узлы,
    /// отсортированные скрытно от пользователя — сначала с универсальным доступом,
    /// затем по задержке. null — подходящих узлов нет.
    /// </summary>
    public static ProxyNode? PickForService(QuickService service)
    {
        lock (Gate)
        {
            var mine = Settings.ConnectOnlyMine;
            return Nodes
                .Where(node => node.Status == NodeStatus.Working &&
                               OpenedFor(node, service) &&
                               (!mine || node.IsMine))
                .OrderByDescending(node => node.OpenDomains)
                .ThenBy(node => node.DelayMs ?? int.MaxValue)
                .ThenByDescending(node => node.IsMine)
                .FirstOrDefault();
        }
    }

    /// <summary>Есть ли у узла подтверждённый доступ к сервису.</summary>
    private static bool OpenedFor(ProxyNode node, QuickService service) => service switch
    {
        QuickService.YouTube => node.YoutubeOk == true,
        QuickService.Telegram => node.TelegramOk == true,
        QuickService.Discord => node.DiscordOk == true,
        // Старый кэш не знает UniversalOk: тогда универсальным считаем узел с задержкой.
        _ => node.UniversalOk == true || node.DelayMs is not null,
    };

    /// <summary>
    /// Узел, который прямо сейчас реально открывает сайт. Кэш мог устареть (узел умер,
    /// пока телефон лежал), поэтому перед подключением проверяем 1–3 кандидата в :probe
    /// и берём первый живой. Возвращает null, если живых нет.
    /// </summary>
    public static async Task<ProxyNode?> PickVerifiedAsync(
        QuickService? service,
        ProxyNode? preferred,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        EnsureLoaded();

        List<ProxyNode> unique;
        lock (Gate)
        {
            var candidates = new List<ProxyNode>();
            if (preferred is not null &&
                (!Settings.ConnectOnlyMine || preferred.IsMine))
            {
                candidates.Add(preferred);
            }

            if (service is { } wanted)
            {
                var picked = PickForService(wanted);
                if (picked is not null)
                {
                    candidates.Add(picked);
                }

                // Для конкретного сервиса не подставляем узлы без его доступа:
                // иначе кнопка Telegram подключила бы узел, где Telegram не открывается.
                candidates.AddRange(RankedVerified()
                    .Where(node => OpenedFor(node, wanted))
                    .Take(4));
            }
            else
            {
                candidates.AddRange(RankedVerified().Take(4));
            }

            unique = candidates
                .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(3)
                .ToList();
        }

        if (unique.Count == 0)
        {
            return null;
        }

        foreach (var node in unique)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TunnelService.IsBypassMode)
            {
                // В TUN-обходе проверка узлов недоступна: берём как есть.
                Select(node);
                return node;
            }

            progress?.Report("Проверяю узел перед подключением…");
            var summary = await ProbeClient.RunAsync(new[] { node }, cancellationToken)
                .ConfigureAwait(false);
            if (summary.Working > 0)
            {
                Select(node);
                return node;
            }

            progress?.Report("Узел не открылся — пробую следующий…");
        }

        return null;
    }

    /// <summary>
    /// Готовый конфиг TUN для VPN-службы: все живые узлы в selector, выбранный — по умолчанию.
    /// <paramref name="preferWorking"/> — для автозапуска после загрузки: человек узел не
    /// выбирает, поэтому вместо прежнего (возможно, уже мёртвого) берём проверенный рабочий.
    /// null и текст ошибки — если подключать нечего.
    /// </summary>
    public static string? BuildTunnelConfig(out string? error, bool preferWorking = false)
    {
        EnsureLoaded();

        var selected = Selected;

        // Автозапуск: прежний выбор мог умереть, пока телефон был выключен. Рабочим
        // считаем только узел с задержкой и без провалов; иначе оставляем как есть.
        if (preferWorking)
        {
            var auto = PickAutoNode(selected);
            if (auto is not null && (selected is null || auto.Id != selected.Id))
            {
                AppLog.Write("android", "Автоподключение: выбран рабочий узел " + auto.Name);
                Select(auto);
                selected = auto;
            }
        }

        var ordered = OrderForConfig(Nodes);
        if (Settings.ConnectOnlyVerified)
        {
            ordered = ordered.Where(node => node.DelayMs is not null).ToList();
        }

        if (Settings.ConnectOnlyMine)
        {
            ordered = ordered.Where(node => node.IsMine).ToList();
        }

        if (ordered.Count == 0)
        {
            error = Settings.ConnectOnlyMine
                ? "Нет узлов в личных подписках: добавьте свою подписку или выключите «Только мои»"
                : Settings.ConnectOnlyVerified
                    ? "Нет проверенных узлов: обновите источники и нажмите «Проверить»"
                    : "Список узлов пуст: обновите источники";
            return null;
        }

        var cap = Math.Min(Math.Max(1, Settings.MaxNodesInConfig), MobileMaxNodesInConfig);
        // Защита от повторов: в конфиг не попадают два узла с одинаковым outbound'ом (тегом).
        var candidates = ordered
            .DistinctBy(node => node.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(cap)
            .ToList();

        // Выбранный узел обязан попасть в конфиг, даже если рейтинг вытеснил его за лимит.
        if (selected is not null && candidates.All(node => node.Id != selected.Id) &&
            (!Settings.ConnectOnlyVerified || selected.DelayMs is not null) &&
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
                // На Android локальный UDP/53 часто зарезан или подменяется — DNS идёт через узел.
                dnsThroughProxy: true);
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
            return null;
        }
    }
}
