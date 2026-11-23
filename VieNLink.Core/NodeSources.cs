using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace VieNLink;

internal sealed class FetchOutcome
{
    public required SourceDefinition Source { get; init; }
    public List<ProxyNode> Nodes { get; init; } = new();
    public List<MtProtoProxy> MtProto { get; init; } = new();
    public string? Error { get; init; }
    public bool Skipped { get; init; }

    /// <summary>Дату публикации определить нельзя — источник пора удалить.</summary>
    public bool RemoveSource { get; init; }
}

internal sealed class CollectResult
{
    public List<ProxyNode> Nodes { get; init; } = new();
    public List<MtProtoProxy> MtProto { get; init; } = new();
    public List<SourceDefinition> SourcesToRemove { get; init; } = new();
}

/// <summary>
/// Статистика источников по реальным результатам: сколько сейчас рабочих серверов
/// пришло из каждого источника. Нужна, чтобы видеть бесполезные источники и удалять их.
/// </summary>
internal static class NodeSourceStats
{
    /// <summary>
    /// Считает рабочие серверы по названиям источников. Сервер, найденный в нескольких
    /// источниках, учитывается у каждого — видно, какой источник даёт живые серверы,
    /// а какой только мусор. Названия склеены в <see cref="ProxyNode.SourceName"/> через «, ».
    /// </summary>
    public static Dictionary<string, int> WorkingBySource(IEnumerable<ProxyNode> nodes)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node.Status != NodeStatus.Working)
            {
                continue;
            }

            foreach (var source in NodeFailover.SplitSources(node.SourceName))
            {
                counts.TryGetValue(source, out var count);
                counts[source] = count + 1;
            }
        }

        return counts;
    }

    /// <summary>
    /// Учитывает сервер как «хотя бы раз рабочий» у всех источников, из которых он пришёл.
    /// Вызывается после проверки, когда сервер открыл сайт через себя.
    /// </summary>
    public static bool RegisterWorking(IEnumerable<SourceDefinition> sources, ProxyNode node)
    {
        if (node.Status != NodeStatus.Working)
        {
            return false;
        }

        var changed = false;
        foreach (var name in NodeFailover.SplitSources(node.SourceName))
        {
            foreach (var source in sources)
            {
                if (!string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var before = source.TotalWorkingNodeCount;
                source.RegisterWorkingId(node.Id);
                changed |= source.TotalWorkingNodeCount != before;
            }
        }

        return changed;
    }

    /// <summary>
    /// Первичное заполнение «всего зарегистрировано» по уже загруженному кэшу:
    /// без этого у старых источников счётчик начинался бы с нуля до следующего сбора.
    /// Возвращает true, если что-то добавилось и источники стоит сохранить.
    /// </summary>
    public static bool SeedTotals(
        IEnumerable<SourceDefinition> sources,
        IEnumerable<ProxyNode> nodes)
    {
        var byName = new Dictionary<string, List<SourceDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name))
            {
                continue;
            }

            if (!byName.TryGetValue(source.Name, out var list))
            {
                list = new List<SourceDefinition>();
                byName[source.Name] = list;
            }

            list.Add(source);
        }

        var changed = false;
        foreach (var node in nodes)
        {
            foreach (var name in NodeFailover.SplitSources(node.SourceName))
            {
                if (!byName.TryGetValue(name, out var matches))
                {
                    continue;
                }

                foreach (var source in matches)
                {
                    var before = source.TotalNodeCount;
                    source.RegisterId(node.Id);
                    changed |= source.TotalNodeCount != before;

                    if (node.Status == NodeStatus.Working)
                    {
                        var beforeWorking = source.TotalWorkingNodeCount;
                        source.RegisterWorkingId(node.Id);
                        changed |= source.TotalWorkingNodeCount != beforeWorking;
                    }
                }
            }
        }

        return changed;
    }
}

/// <summary>
/// Загрузка публичных списков серверов: raw-файлы GitHub, base64-подписки и публичные каналы Telegram.
/// Сайты и каналы часто недоступны напрямую, поэтому ошибка источника не роняет остальные.
/// </summary>
internal static class NodeSourceService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient HttpDirect = CreateClient(useProxy: false);

    /// <summary>
    /// Источники, которые больше не поставляем: репозиторий давно не обновлялся.
    /// Удаляются и у существующих пользователей при загрузке списка.
    /// </summary>
    private static readonly string[] ObsoleteSourceUrls =
    {
        "https://raw.githubusercontent.com/Kwinshadow/TelegramV2rayCollector/main/sublinks/mix.txt",
        // Проверены на ПК: за всё время не дали ни одного рабочего сервера
        // либо дают единицы мёртвых. Держать их в сборке бессмысленно.
        "https://raw.githubusercontent.com/mahdibland/V2RayAggregator/master/sub/sub_merge.txt",
        "https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/Eternity.txt",
        "https://raw.githubusercontent.com/ermaozi/get_subscribe/main/subscribe/v2ray.txt",
        "https://raw.githubusercontent.com/ripaojiedian/freenode/main/sub",
        "https://raw.githubusercontent.com/roosterkid/openproxylist/main/V2RAY_RAW.txt",
        "https://raw.githubusercontent.com/free-nodes/v2rayfree/main/sub",
        "https://raw.githubusercontent.com/jafarm83/ConfigV2Ray/main/jafar.txt",
        "https://raw.githubusercontent.com/zhuhaiuk/free-nodes/main/nodes.txt",
    };

    /// <summary>
    /// Разовая миграция: включить все ранее выключенные источники. Ставится
    /// <see cref="SettingsUpgrade"/> для схем ниже 3 и сбрасывается после применения.
    /// </summary>
    public static bool EnableAllSourcesOnce { get; set; }

    public static List<SourceDefinition> DefaultSources() => new()
    {
        new() { Name = "v2rayNG-Config (GitHub)", Url = "https://raw.githubusercontent.com/ALIILAPRO/v2rayNG-Config/main/server.txt", Kind = SourceKind.PlainList },
        new() { Name = "Free-servers (подписка)", Url = "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub", Kind = SourceKind.Subscription },

        // Проверены на ПК: обновляются каждый день/час и давали рабочие серверы.
        new() { Name = "0xRadikal (Clash)", Url = "https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/all/clash.yaml", Kind = SourceKind.PlainList },
        new() { Name = "SoliSpirit configs", Url = "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/Protocols/vless.txt", Kind = SourceKind.PlainList },
        new() { Name = "hamedcode port vless", Url = "https://raw.githubusercontent.com/hamedcode/port-based-v2ray-configs/main/sub/vless.txt", Kind = SourceKind.PlainList },
        new() { Name = "AutoAiVPN", Url = "https://raw.githubusercontent.com/penhandev/AutoAiVPN/main/allConfigs.txt", Kind = SourceKind.PlainList },
        new() { Name = "V2ray-Collector (Bahemmat)", Url = "https://raw.githubusercontent.com/MohammadBahemmat/V2ray-Collector/main/servers/vless_servers.txt", Kind = SourceKind.PlainList },
        new() { Name = "node-sub-zixf", Url = "https://raw.githubusercontent.com/chenzhihan1751-cyber/node-sub-zixf/main/sub/v2ray.txt", Kind = SourceKind.PlainList },

        // Крупные агрегаторы: серверов много, но и пользы больше — по умолчанию включены.
        new() { Name = "barry-far vless (GitHub)", Url = "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/vless.txt", Kind = SourceKind.PlainList },
        new() { Name = "MhdiTaheri mix (GitHub)", Url = "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/mix", Kind = SourceKind.PlainList },
        new() { Name = "Mineral nodes (GitHub)", Url = "https://raw.githubusercontent.com/LalatinaHub/Mineral/master/result/nodes", Kind = SourceKind.PlainList },

        // Проверены 23.11.2026: обновляются ежедневно или ежечасно, содержат сотни–тысячи
        // ссылок всех протоколов (VLESS/VMess/Trojan/SS/Hysteria2). Часть списков —
        // base64-подписки, поэтому Kind = Subscription.
        new() { Name = "MatinGhanbari (подписка)", Url = "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/base64/all_sub.txt", Kind = SourceKind.Subscription },
        new() { Name = "Barabama FreeNodes", Url = "https://raw.githubusercontent.com/Barabama/FreeNodes/main/nodes/merged.txt", Kind = SourceKind.PlainList },
        new() { Name = "DukeMehdi configs", Url = "https://raw.githubusercontent.com/DukeMehdi/FreeList-V2ray-Configs/main/Configs/All-DukeMehdi-Configs.txt", Kind = SourceKind.PlainList },
        new() { Name = "rtwo2 FastNodes", Url = "https://raw.githubusercontent.com/rtwo2/FastNodes/main/sub/top.txt", Kind = SourceKind.PlainList },
        new() { Name = "4n0nymou3 configs", Url = "https://raw.githubusercontent.com/4n0nymou3/multi-proxy-config-fetcher/main/configs/proxy_configs.txt", Kind = SourceKind.PlainList },
        new() { Name = "FlareFeed Top500", Url = "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/public/Top500.txt", Kind = SourceKind.PlainList },
        new() { Name = "NoMoreWalls (подписка)", Url = "https://raw.githubusercontent.com/peasoft/NoMoreWalls/master/list.txt", Kind = SourceKind.Subscription },
        new() { Name = "ConfigForge V2Ray", Url = "https://raw.githubusercontent.com/ShatakVPN/ConfigForge-V2Ray/main/configs/all.txt", Kind = SourceKind.PlainList },

        // Текущий избранный набор с рабочей установки (23.11.2026): эти источники
        // уже были включены у пользователя и давали серверы. Вшиваем их в приложение,
        // чтобы новая установка сразу была настроена так же. Два старых источника
        // (jafarm83/ConfigV2Ray/singbox.json и SoliSpirit/SolVPN/sub6.txt) не включены:
        // их файлы не обновлялись больше суток, сбор всё равно пропустит их по свежести.
        new() { Name = "3inker v2ray (без РФ)", Url = "https://raw.githubusercontent.com/3inker/v2ray-subscription/main/subs/all_not_ru.txt", Kind = SourceKind.PlainList },
        new() { Name = "amirkma proxykma", Url = "https://raw.githubusercontent.com/amirkma/proxykma/main/mix.txt", Kind = SourceKind.PlainList },
        new() { Name = "farshid78 Free-v2ray (Clash)", Url = "https://raw.githubusercontent.com/farshid78/Free-v2ray-Configs/main/all/clash.yaml", Kind = SourceKind.PlainList },
        new() { Name = "Fl1pse vless (подписка)", Url = "https://raw.githubusercontent.com/Fl1pse/vless-subs/main/normal.txt", Kind = SourceKind.Subscription },
        new() { Name = "iboxz collector mix", Url = "https://raw.githubusercontent.com/iboxz/free-v2ray-collector/main/main/mix", Kind = SourceKind.PlainList },
        new() { Name = "Mehinator Derafsh (подписка)", Url = "https://raw.githubusercontent.com/Mehinator/Derafsh/main/derafsh_sub.txt", Kind = SourceKind.Subscription },
        new() { Name = "NamiraNet V2Configs (подписка)", Url = "https://raw.githubusercontent.com/NamiraNet/V2Configs/main/results.txt", Kind = SourceKind.Subscription },
        new() { Name = "SylphNet (подписка)", Url = "https://raw.githubusercontent.com/ProblemTheCode/SylphNet-public/main/sub/sub.txt", Kind = SourceKind.Subscription },
        new() { Name = "SoliSpirit all configs", Url = "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/all_configs.txt", Kind = SourceKind.PlainList },
        new() { Name = "TigerVpn Sup", Url = "https://raw.githubusercontent.com/TigerVpnGit/TigerVpn-Sup-Free/main/Sup.txt", Kind = SourceKind.PlainList },
        new() { Name = "V2HUBIR (подписка)", Url = "https://raw.githubusercontent.com/V2HUBIR/V2HUBIR-Free-V2Ray-Configs-free-Sub/main/sub_base64.txt", Kind = SourceKind.Subscription },
        new() { Name = "zxcursedzxc vless", Url = "https://raw.githubusercontent.com/zxcursedzxc0721/vless-subscriptions/main/all/vless.txt", Kind = SourceKind.PlainList },

        // Каналы Telegram открываются только при уже работающем обходе блокировок.
        // @proxy_mtproto включён: после первого успешного подключения приложение само прочитает его.
        new() { Name = "Telegram: @proxy_mtproto", Url = "https://t.me/s/proxy_mtproto", Kind = SourceKind.Telegram },
        new() { Name = "Telegram: @ProxyMTProto", Url = "https://t.me/s/ProxyMTProto", Kind = SourceKind.Telegram },
        new() { Name = "Telegram: @MTProtoProxies", Url = "https://t.me/s/MTProtoProxies", Kind = SourceKind.Telegram },
        new() { Name = "Telegram: @v2rayng_config", Url = "https://t.me/s/v2rayng_config", Kind = SourceKind.Telegram },

        // Сайты и GitHub-списки MTProto: не требуют t.me и часто доступны напрямую.
        new() { Name = "MTProto: tgmtproxy (GitHub)", Url = "https://raw.githubusercontent.com/tgmtproxy/telegram-mtproto-proxy-list/main/proxies.txt", Kind = SourceKind.MtProto },
        new() { Name = "MTProto: SoliSpirit (GitHub)", Url = "https://raw.githubusercontent.com/SoliSpirit/mtproto/master/all_proxies.txt", Kind = SourceKind.MtProto },
        new() { Name = "MTProto: suryadev-sm (GitHub)", Url = "https://raw.githubusercontent.com/suryadev-sm/free-mtproto-proxy-list/main/proxies.txt", Kind = SourceKind.MtProto },
        new() { Name = "MTProto: Airuop JSON (GitHub)", Url = "https://raw.githubusercontent.com/Airuop/MTProtoCollector/refs/heads/main/proxy/mtproto.json", Kind = SourceKind.MtProto },
    };

    public static List<SourceDefinition> LoadSources()
    {
        // Новый формат — зашифрованный: в ссылках подписок лежат токены доступа.
        var saved = JsonFileStore.ReadProtected<List<SourceDefinition>>(AppPaths.SourcesProtectedPath);
        var fromPlain = false;
        if ((saved is null || saved.Count == 0) && File.Exists(AppPaths.SourcesPath))
        {
            // Открытый sources.json от прежних версий: читаем и сразу шифруем.
            saved = JsonFileStore.Read<List<SourceDefinition>>(AppPaths.SourcesPath);
            fromPlain = saved is { Count: > 0 };
        }

        if (saved is null || saved.Count == 0)
        {
            var defaults = DefaultSources();
            SaveSources(defaults);
            return defaults;
        }

        // Убираем источники, которые больше не поставляем (давно не обновлялись).
        var obsoleteRemoved = saved.RemoveAll(source =>
            ObsoleteSourceUrls.Any(url => source.Url.Equals(url, StringComparison.OrdinalIgnoreCase)));

        // Источники серверов без даты публикации (не GitHub) больше не используем.
        obsoleteRemoved += saved.RemoveAll(source =>
            !source.IsMine &&
            !SourceKinds.IsProxy(source) &&
            GitHubSourceFreshness.TryGetLocation(source.Url) is null);

        if (!fromPlain)
        {
            // Зашифрованный файл прочитан: открытых копий от старых версий быть не должно.
            DeletePlainSources();
        }

        // Новые источники из обновлений добавляются включёнными: пользователь просил
        // держать все источники включёнными по умолчанию (и на ПК, и на телефоне).
        var addedDefaults = 0;
        foreach (var defaultSource in DefaultSources())
        {
            if (saved.All(s => !s.Url.Equals(defaultSource.Url, StringComparison.OrdinalIgnoreCase)))
            {
                saved.Add(defaultSource);
                addedDefaults++;
            }
        }

        if (obsoleteRemoved > 0 || addedDefaults > 0)
        {
            SaveSources(saved);
        }

        // Разовая миграция со старых версий: включить и те источники, которые раньше
        // были выключены «по умолчанию» (крупные агрегаторы и один Telegram-канал).
        if (EnableAllSourcesOnce)
        {
            EnableAllSourcesOnce = false;
            foreach (var source in saved)
            {
                source.Enabled = true;
            }

            SaveSources(saved);
        }

        if (fromPlain)
        {
            SaveSources(saved);
        }

        return saved;
    }

    public static void SaveSources(List<SourceDefinition> sources)
    {
        if (!JsonFileStore.WriteProtected(AppPaths.SourcesProtectedPath, sources))
        {
            // Шифрованная запись не удалась — открытую копию не трогаем, чтобы не потерять источники.
            return;
        }

        DeletePlainSources();
    }

    /// <summary>Убирает открытые копии sources.json и .bak: в них те же токены подписок.</summary>
    private static void DeletePlainSources()
    {
        try
        {
            foreach (var plain in new[] { AppPaths.SourcesPath, AppPaths.SourcesPath + ".bak" })
            {
                if (File.Exists(plain))
                {
                    File.Delete(plain);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("sources", "Не удалось удалить открытый sources.json", exception);
        }
    }

    public static async Task<CollectResult> CollectAsync(
        IEnumerable<SourceDefinition> sources,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        Action<FetchOutcome>? onSource = null,
        int freshnessHours = 24)
    {
        var maximumAge = TimeSpan.FromHours(Math.Clamp(freshnessHours, 1, 168 * 24));
        // Каналы Telegram — источники MTProto-прокси, а не серверов: их читает
        // MtProtoService, и в общий сбор серверов они не попадают.
        var enabled = sources
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url))
            .Where(s => !SourceKinds.IsProxy(s))
            .ToList();
        var batches = await Task.WhenAll(enabled.Select(async source =>
        {
            FetchOutcome outcome;
            try
            {
                // Сбор серверов — строго из источников, опубликованных не позже окна.
                // Если дату определить нельзя, источник не собираем; при временном
                // сбое GitHub API источник не удаляем, чтобы пережить недоступность API.
                var freshness = await GitHubSourceFreshness
                    .CheckAsync(source, maximumAge, cancellationToken).ConfigureAwait(false);
                source.LastPublishedUtc = freshness.UpdatedAt?.UtcDateTime;
                source.IsTrusted = freshness.DateKnown && freshness.Fresh;
                if (!freshness.Fresh)
                {
                    // GitHub API недоступен (блокировка, лимит, зеркала не ответили):
                    // дату проверить нельзя, но и выбрасывать все источники нельзя —
                    // иначе при недоступном API приложение остаётся без серверов.
                    // Собираем без проверки даты; при следующем сборе дата проверится.
                    var apiUnavailable = !freshness.DateKnown && !freshness.ApiReached;
                    if (apiUnavailable)
                    {
                        AppLog.Write("sources",
                            $"{source.Name}: GitHub API недоступен — собираю без проверки даты");
                        outcome = await FetchAsync(source, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var remove = !freshness.DateKnown && freshness.ApiReached && !source.IsMine;
                        outcome = new FetchOutcome
                        {
                            Source = source,
                            Skipped = true,
                            RemoveSource = remove,
                            Error = remove
                                ? "дату публикации определить нельзя — источник удаляется"
                                : "источник не обновлялся в выбранном окне — пропущен",
                        };
                        AppLog.Write("sources",
                            $"{source.Name}: {(remove ? "нет даты публикации, удаляю" : "пропущен по свежести")}");
                    }
                }
                else
                {
                    outcome = await FetchAsync(source, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Таймаут одного источника — это его проблема, а не повод отменять всю загрузку.
                outcome = new FetchOutcome { Source = source, Error = exception.Message };
            }

            source.LastCheckedUtc = DateTime.UtcNow;
            source.LastError = outcome.Error;
            if (!outcome.Skipped)
            {
                source.LastNodeCount = outcome.Nodes.Count;
            }

            progress?.Report(outcome.Skipped
                ? $"{source.Name}: пропущен — {outcome.Error}"
                : outcome.Error is null
                    ? $"{source.Name}: {outcome.Nodes.Count} серверов"
                    : $"{source.Name}: ошибка — {outcome.Error}");
            onSource?.Invoke(outcome);
            return outcome;
        })).ConfigureAwait(false);

        var unique = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
        var mtproto = new Dictionary<string, MtProtoProxy>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var outcome in batches)
        {
            foreach (var node in outcome.Nodes)
            {
                // Сервер пришёл из источника сейчас — значит, он свежий.
                node.LastSeenUtc = now;
                if (unique.TryAdd(node.Id, node))
                {
                    continue;
                }

                // Дубликат из другого источника: оставляем первый, но помним, что сервер популярен.
                var existing = unique[node.Id];
                NodeDedup.MergeSource(existing, node.SourceName);
            }

            foreach (var proxy in outcome.MtProto)
            {
                mtproto.TryAdd(proxy.Id, proxy);
            }
        }

        return new CollectResult
        {
            // Один сервер:порт — одна строка, даже если источников и ссылок несколько.
            Nodes = NodeDedup.Collapse(unique.Values),
            MtProto = mtproto.Values.ToList(),
            SourcesToRemove = batches
                .Where(outcome => outcome.RemoveSource)
                .Select(outcome => outcome.Source)
                .ToList(),
        };
    }

    public static async Task<FetchOutcome> FetchAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        if (source.Kind == SourceKind.SingleLink)
        {
            // В поле можно вставить одну ссылку или несколько (по одной в строке).
            var imported = SubscriptionParser.Parse(source.Url, source.Name);

            if (imported.Count == 0)
            {
                return new FetchOutcome { Source = source, Error = "Не удалось разобрать ссылку на сервер" };
            }

            foreach (var node in imported)
            {
                node.IsMine = source.IsMine;
            }

            source.RegisterNodes(imported);
            return new FetchOutcome { Source = source, Nodes = imported };
        }

        // Разбор идёт в фоне: список на несколько мегабайт не должен подвешивать UI.
        var (rawText, subscriptionInfo) = await FetchRawAsync(source, cancellationToken).ConfigureAwait(false);
        source.SubscriptionInfo = subscriptionInfo;

        // В каналах Telegram лежит история: берём только недавние посты, старые прокси
        // почти всегда мертвы и только засоряют список.
        var text = source.Kind == SourceKind.Telegram
            ? TelegramText.Extract(rawText, DateTime.UtcNow.AddHours(-TelegramText.FreshHours))
            : rawText;
        var parsed = SubscriptionParser.Parse(text, source.Name);

        // Заглушки вида «Приложение не поддерживается» и группы с 127.0.0.1:1 — не серверы.
        var nodes = parsed.Where(node => !IsPlaceholderNode(node)).ToList();
        foreach (var node in nodes)
        {
            node.IsMine = source.IsMine;
        }

        // «Всего зарегистрировано» — уникальные серверы за всё время, а не за этот сбор.
        source.RegisterNodes(nodes);

        string? error = null;
        if (nodes.Count == 0 && parsed.Count > 0)
        {
            error = "Провайдер отдаёт только заглушки — серверы доступны лишь в его клиенте (Happ). " +
                    "Нужна универсальная подписка или ссылки на серверы.";
        }

        var mtproto = MtProtoService.Extract(text, source.Name);
        return new FetchOutcome { Source = source, Nodes = nodes, MtProto = mtproto, Error = error };
    }

    /// <summary>Служебные «серверы»-маркеры: группы и сообщения провайдера.</summary>
    private static bool IsPlaceholderNode(ProxyNode node) =>
        node.Port == 1 && (node.Server is "0.0.0.0" or "127.0.0.1" or "::1" or "localhost");

    /// <summary>Сырой ответ источника: используется и для серверов, и для MTProto-прокси.</summary>
    public static async Task<(string Text, string? SubscriptionInfo)> FetchRawAsync(
        SourceDefinition source,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null,
        bool retryDirect = true)
    {
        // Страницы t.me маленькие: либо открываются быстро, либо канал недоступен.
        var timeout = timeoutOverride ?? TimeSpan.FromSeconds(source.Kind == SourceKind.Telegram ? 15 : 40);
        try
        {
            return await FetchOnceAsync(Http, source, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (retryDirect && exception is HttpRequestException or TimeoutException)
        {
            // Системный прокси мог указывать на мёртвый сервер: прямой запрос часто проходит.
            AppLog.Write("sources", $"Источник «{source.Name}»: повтор без прокси", exception);
            return await FetchOnceAsync(HttpDirect, source, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(string Text, string? SubscriptionInfo)> FetchOnceAsync(
        HttpClient client,
        SourceDefinition source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            // Многие панели отдают формат в зависимости от клиента; v2rayNG — самый совместимый.
            request.Headers.UserAgent.ParseAdd("v2rayNG/1.9.16 (VieNLink/1.0)");
            request.Headers.Accept.ParseAdd("*/*");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }

            var text = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            var info = SubscriptionParser.ParseUserInfo(
                response.Headers.TryGetValues("subscription-userinfo", out var userInfo) ? userInfo : null);
            return (text, info);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var hint = source.Kind == SourceKind.Telegram
                ? " Канал t.me открывается только через работающий обход — подключитесь к серверу и повторите."
                : string.Empty;
            throw new TimeoutException($"Источник не ответил за {timeout.TotalSeconds:0} с.{hint}");
        }
    }

    public static List<string> ExtractLinks(string text) => SubscriptionParser.ExtractLinks(text);

    /// <summary>
    /// Кэш серверов, чтобы список не пропадал между запусками.
    /// Пустым списком кэш не затирается: если чтение кэша или источников разово не удалось,
    /// следующее сохранение не должно стереть уже найденные серверы (это выглядело как
    /// «после обновления версии всё сбросилось»). Очистка — только явная.
    /// </summary>
    public static void SaveCache(IEnumerable<ProxyNode> nodes, bool allowEmpty = false, string? path = null)
    {
        var target = path ?? AppPaths.NodesPath;
        var list = nodes as IList<ProxyNode> ?? nodes.ToList();
        if (list.Count == 0 && !allowEmpty)
        {
            var existing = JsonFileStore.Read<NodeCache>(target);
            if (existing is { Items.Count: > 0 })
            {
                AppLog.Write("cache",
                    $"Пустой список серверов не сохраняю: в кэше {existing.Items.Count} записей");
                return;
            }
        }

        // Даже если сервер пришёл из старого кэша без отметки, при сохранении она появится.
        var now = DateTime.UtcNow;
        JsonFileStore.WriteCompact(target, new NodeCache
        {
            SchemaVersion = SchemaVersions.NodeCache,
            SavedAtUtc = now,
            Items = list.Select(n => new CachedNode
            {
                RawLink = n.RawLink,
                SourceName = n.SourceName,
                Protocol = n.Protocol,
                Name = n.Name,
                Server = n.Server,
                Port = n.Port,
                Tag = n.Tag,
                Identity = n.Identity,
                OutboundJson = n.OutboundJson,
                DelayMs = n.DelayMs,
                CheckedAtUtc = n.CheckedAtUtc,
                TriedAtUtc = n.TriedAtUtc,
                LastSeenUtc = n.LastSeenUtc ?? now,
                IsMine = n.IsMine,
                SpeedMbps = n.SpeedMbps,
                AllowInsecure = n.AllowInsecure,
                YoutubeOk = n.YoutubeOk,
                TelegramOk = n.TelegramOk,
                UniversalOk = n.UniversalOk,
                DiscordOk = n.DiscordOk,
                OpenDomains = n.OpenDomains,
                Status = n.Status,
                FailCount = n.FailCount,
            }).ToList(),
        });
    }

    public static List<ProxyNode> LoadCache() => LoadCache(out _, out _);

    public static List<ProxyNode> LoadCache(out int dropped) => LoadCache(out dropped, out _);

    /// <summary>
    /// Читает кэш и оставляет только свежие серверы: не встречавшиеся в источниках дольше
    /// <paramref name="freshAfterUtc"/> выбрасываются, повторы «один сервер:порт» склеиваются.
    /// </summary>
    public static List<ProxyNode> LoadCache(out int dropped, out int duplicates, DateTime? freshAfterUtc = null,
        string? path = null)
    {
        dropped = 0;
        duplicates = 0;
        var cache = JsonFileStore.Read<NodeCache>(path ?? AppPaths.NodesPath);
        // Пустой или нечитаемый основной файл не должен означать «серверов нет»: рядом
        // лежит копия, которую JsonFileStore делает перед каждой перезаписью. Это
        // страховка от сброса списка после сбоя записи или обновления версии.
        if (cache is null || cache.Items.Count == 0)
        {
            var backup = JsonFileStore.Read<NodeCache>(AppPaths.NodesPath + ".bak");
            if (backup is { Items.Count: > 0 })
            {
                AppLog.Write("cache",
                    $"Основной кэш пуст — восстанавливаю {backup.Items.Count} серверов из резервной копии");
                cache = backup;
            }
        }

        if (cache is null)
        {
            return new List<ProxyNode>();
        }

        if (cache.SchemaVersion > SchemaVersions.NodeCache)
        {
            AppLog.Write("cache",
                $"Кэш серверов новее приложения (схема {cache.SchemaVersion}, поддерживается {SchemaVersions.NodeCache}). " +
                "Читаю известные поля; полная перезапись произойдёт после следующей проверки.");
        }

        var nodes = new List<ProxyNode>(cache.Items.Count);
        // Нет времени сохранения — считаем кэш свежим, чтобы не стереть список целиком.
        var cacheSavedAt = cache.SavedAtUtc == default ? DateTime.UtcNow : cache.SavedAtUtc;
        foreach (var item in cache.Items)
        {
            // В старом кэше остались «мусор» и «найденные» — их больше не храним.
            if (item.Status is not (NodeStatus.Unknown or NodeStatus.Working))
            {
                dropped++;
                continue;
            }

            // Старые версии хранили нерабочие серверы в статусе, номер которого совпал
            // с новым Working. Рабочий сервер обязан иметь успешную проверку.
            if (item.Status == NodeStatus.Working &&
                item.DelayMs is null && item.YoutubeOk != true && item.TelegramOk != true &&
                item.UniversalOk != true && item.DiscordOk != true)
            {
                // В текущей схеме это рабочий сервер, проваливший проверку: он остаётся
                // в списке до RemoveAfterFailures провалов подряд (счётчик FailCount),
                // и его судьбу решает следующая перепроверка. Не выбрасываем его молча:
                // иначе «рабочие» исчезают из кэша, не попадая в отсев.
                // Убираем только записи старой схемы или совсем без счётчика/отметки.
                if (cache.SchemaVersion < SchemaVersions.NodeCache &&
                    item.FailCount <= 0 && item.OpenDomains <= 0 && item.CheckedAtUtc is null)
                {
                    dropped++;
                    continue;
                }
            }

            // В старом кэше нет отметки LastSeen: возраст считаем от времени сохранения,
            // иначе первый запуск после обновления выбросил бы разом все серверы.
            var lastSeen = item.LastSeenUtc ?? cacheSavedAt;
            // Рабочий сервер не выбрасываем по возрасту источника: он уже проверен,
            // а источник мог не обновиться, временно исчезнуть или перестать отдавать
            // этот сервер. Его судьбу решает перепроверка, а не LastSeen — иначе после
            // обновления/долгого простоя рабочие серверы пропадали бы из кэша.
            if (IsExpiredBySourceAge(item.Status, lastSeen, freshAfterUtc))
            {
                dropped++;
                continue;
            }

            // Новый кэш хранит разобранный сервер: холодный старт не тратит время на разбор
            // и хеширование каждой ссылки. Старый кэш читается прежним путём.
            ProxyNode? node;
            if (!string.IsNullOrWhiteSpace(item.OutboundJson) &&
                !string.IsNullOrWhiteSpace(item.Tag) &&
                !string.IsNullOrWhiteSpace(item.Identity) &&
                !string.IsNullOrWhiteSpace(item.Server) &&
                item.Port is > 0 and <= 65535)
            {
                node = new ProxyNode
                {
                    RawLink = item.RawLink,
                    Protocol = item.Protocol,
                    Name = string.IsNullOrWhiteSpace(item.Name) ? item.Server : item.Name,
                    Server = item.Server,
                    Port = item.Port,
                    Tag = item.Tag,
                    Identity = item.Identity,
                    OutboundJson = item.OutboundJson,
                    AllowInsecure = item.AllowInsecure,
                    SourceName = item.SourceName,
                };
            }
            else
            {
                // Обычные ссылки разбирает LinkParser; sing-box JSON-серверы — SubscriptionParser.
                node = LinkParser.Parse(item.RawLink, item.SourceName)
                       ?? SubscriptionParser.Parse(item.RawLink, item.SourceName).FirstOrDefault();
            }

            if (node is null)
            {
                dropped++;
                continue;
            }

            node.DelayMs = item.DelayMs;
            node.CheckedAtUtc = item.CheckedAtUtc;
            node.TriedAtUtc = item.TriedAtUtc;
            node.LastSeenUtc = lastSeen;
            node.IsMine = item.IsMine;
            node.SpeedMbps = item.SpeedMbps;
            node.AllowInsecure = item.AllowInsecure || node.AllowInsecure;
            node.YoutubeOk = item.YoutubeOk;
            node.TelegramOk = item.TelegramOk;
            node.UniversalOk = item.UniversalOk;
            node.DiscordOk = item.DiscordOk;
            node.OpenDomains = item.OpenDomains;
            node.Status = item.Status;
            node.FailCount = item.FailCount;
            nodes.Add(node);
        }

        var collapsed = NodeDedup.Collapse(nodes);
        duplicates = nodes.Count - collapsed.Count;
        return collapsed;
    }

    /// <summary>
    /// Отбрасывается ли запись кэша по возрасту источника. Рабочие серверы не отбрасываются:
    /// они уже проверены, и их судьбу решает перепроверка, а не LastSeen источника.
    /// </summary>
    internal static bool IsExpiredBySourceAge(
        NodeStatus status,
        DateTime lastSeenUtc,
        DateTime? freshAfterUtc) =>
        status != NodeStatus.Working &&
        freshAfterUtc is { } cutoff &&
        lastSeenUtc < cutoff;

    private static HttpClient CreateClient(bool useProxy = true)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseProxy = useProxy,
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return client;
    }
}

/// <summary>Достаёт текст постов из HTML-страницы t.me/s/&lt;канал&gt;.</summary>
internal static class TelegramText
{
    /// <summary>Сколько часов постов Telegram считаются свежими.</summary>
    public const int FreshHours = 48;

    private static readonly Regex ScriptStyleRegex = new(@"(?is)<(script|style)[^>]*>.*?</\1>", RegexOptions.Compiled);
    private static readonly Regex BreakRegex = new(@"(?i)<br\s*/?>|</p>|</div>", RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"(?s)<[^>]+>", RegexOptions.Compiled);

    // Ссылка может быть только в href, а текст — «Прокси»: заменяем весь тег <a> на его адрес.
    private static readonly Regex AnchorRegex = new(@"(?is)<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.Compiled);

    // Каждый пост виджета t.me начинается с такого блока; внутри есть время публикации.
    private static readonly Regex MessageStartRegex = new(
        @"(?i)(?=<div\b[^>]*class\s*=\s*[""'][^""']*tgme_widget_message_wrap\b)", RegexOptions.Compiled);

    private static readonly Regex DateTimeRegex = new(
        @"(?is)<time\b[^>]*datetime\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled);

    public static string Extract(string html, DateTime? freshAfterUtc = null)
    {
        var source = freshAfterUtc is null ? html : KeepFreshMessages(html, freshAfterUtc.Value);
        var cleaned = ScriptStyleRegex.Replace(source, " ");
        cleaned = BreakRegex.Replace(cleaned, "\n");
        cleaned = AnchorRegex.Replace(cleaned, " $1 ");
        cleaned = TagRegex.Replace(cleaned, " ");
        return WebUtility.HtmlDecode(cleaned);
    }

    /// <summary>
    /// Отбрасывает старые посты канала: у каждого поста в разметке есть время публикации.
    /// Старые прокси в Telegram почти всегда мертвы, их незачем тащить в список.
    /// </summary>
    private static string KeepFreshMessages(string html, DateTime freshAfterUtc)
    {
        var parts = MessageStartRegex.Split(html);
        if (parts.Length <= 1)
        {
            // Незнакомая разметка — не рискуем и разбираем страницу целиком.
            return html;
        }

        var kept = new System.Text.StringBuilder(html.Length / 2);
        foreach (var part in parts)
        {
            var match = DateTimeRegex.Match(part);
            if (match.Success &&
                DateTimeOffset.TryParse(
                    match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var posted) &&
                posted.UtcDateTime < freshAfterUtc)
            {
                continue;
            }

            kept.Append(part);
        }

        return kept.ToString();
    }
}
