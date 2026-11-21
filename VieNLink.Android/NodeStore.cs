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

    /// <summary>Сохраняет результаты проверки в кэш (задержки и статусы не теряются между запусками).</summary>
    public static void SaveNodes()
    {
        lock (Gate)
        {
            NodeSourceService.SaveCache(Nodes);
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
    /// Узел для автоподключения: проверенный рабочий, открывающий больше заблокированных
    /// доменов, затем самый быстрый. Задержка null означает недавний провал
    /// (см. <c>NodeHealth</c>), поэтому такие узлы не берём. null — здоровых нет.
    /// </summary>
    private static ProxyNode? PickAutoNode(ProxyNode? selected)
    {
        // Без списка узлов ручного выбора нет: всегда берём лучший проверенный.
        // «Лучший» — тот, что открывает больше заблокированных доменов, затем быстрейший.
        return Nodes
            .Where(node => node.Status == NodeStatus.Working &&
                           node.DelayMs is not null &&
                           node.FailCount == 0 &&
                           !string.IsNullOrWhiteSpace(node.OutboundJson))
            .OrderByDescending(node => node.OpenDomains)
            .ThenBy(node => node.DelayMs)
            .ThenByDescending(node => node.IsMine)
            .FirstOrDefault() ?? selected;
    }

    /// <summary>
    /// Запоминает лучший проверенный узел: открывает больше заблокированных доменов,
    /// затем самый быстрый. Вызывается после проверки, чтобы вручную не выбирать «Лучший».
    /// Возвращает выбранный узел или null, если живых нет.
    /// </summary>
    public static ProxyNode? SelectBestWorking()
    {
        lock (Gate)
        {
            var best = Nodes
                .Where(node => node.Status == NodeStatus.Working &&
                               node.DelayMs is not null &&
                               node.FailCount == 0 &&
                               !string.IsNullOrWhiteSpace(node.OutboundJson))
                .OrderByDescending(node => node.OpenDomains)
                .ThenBy(node => node.DelayMs)
                .ThenByDescending(node => node.IsMine)
                .FirstOrDefault();

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
            return Nodes
                .Where(node => node.Status == NodeStatus.Working && OpenedFor(node, service))
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
        if (ordered.Count == 0)
        {
            error = "Список узлов пуст: обновите источники";
            return null;
        }

        var cap = Math.Min(Math.Max(1, Settings.MaxNodesInConfig), MobileMaxNodesInConfig);
        var candidates = ordered.Take(cap).ToList();

        // Выбранный узел обязан попасть в конфиг, даже если рейтинг вытеснил его за лимит.
        if (selected is not null && candidates.All(node => node.Id != selected.Id))
        {
            candidates.Insert(0, selected);
        }

        var settings = Settings;
        settings.TunnelMode = TunnelMode.Tun;
        settings.SiteRouteMode = SiteRouteMode.All;
        settings.OnlySelectedApps = false;

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
