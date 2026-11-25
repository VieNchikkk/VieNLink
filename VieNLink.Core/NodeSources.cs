using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>Источник в карантин: дата не определяется, но удалять сразу рано.</summary>
    public bool Quarantine { get; init; }

    /// <summary>Хеш сырого текста: одинаковый хеш у разных источников — зеркала одного списка.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Сколько серверов источника пропущено по негативному кэшу (не проверялись).</summary>
    public int Suppressed { get; set; }
}

internal sealed class CollectResult
{
    public List<ProxyNode> Nodes { get; init; } = new();
    public List<MtProtoProxy> MtProto { get; init; } = new();
    public List<SourceDefinition> SourcesToRemove { get; init; } = new();

    /// <summary>«Бесплодные» источники для отключения (карантин), не удаления.</summary>
    public List<SourceDefinition> SourcesToDisable { get; init; } = new();
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
    /// Источники, которые уже проверялись, но не дали ни одного рабочего сервера.
    /// Продолжать их проверять бессмысленно: партия уходит на заведомо мёртвые адреса,
    /// а живые серверы из других источников ждут очереди. Непроверенные серверы в
    /// знаменатель не входят — свежий большой список не должен попадать в «мёртвые».
    /// </summary>
    public static HashSet<string> BarrenSources(IEnumerable<ProxyNode> nodes, int minChecked = 50)
    {
        var checkedCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var working = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var wasChecked = node.CheckedAtUtc is not null || node.Status != NodeStatus.Unknown;
            foreach (var source in NodeFailover.SplitSources(node.SourceName))
            {
                if (wasChecked)
                {
                    checkedCount[source] = checkedCount.GetValueOrDefault(source) + 1;
                }

                if (node.Status == NodeStatus.Working)
                {
                    working[source] = working.GetValueOrDefault(source) + 1;
                }
            }
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in checkedCount)
        {
            if (pair.Value >= minChecked && working.GetValueOrDefault(pair.Key) == 0)
            {
                result.Add(pair.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// Сервер пришёл только из источников, которые уже не дают рабочих серверов.
    /// Такой сервер в фоновую партию не берём; ручная перепроверка его не ограничивает.
    /// </summary>
    public static bool FromBarrenOnly(ProxyNode node, IReadOnlySet<string> barren)
    {
        if (barren.Count == 0)
        {
            return false;
        }

        var sources = NodeFailover.SplitSources(node.SourceName);
        return sources.Count > 0 && sources.All(barren.Contains);
    }

    /// <summary>
    /// Ротация «бесплодных» источников: давний источник, который отдал много серверов,
    /// но ни один не подтвердил работу, сначала отключается (карантин), а через срок —
    /// удаляется. На его место автопоиск подбирает новый. Личные подписки и MTProto-
    /// источники не трогаем. Возвращает источники на отключение и удаление; метку
    /// <see cref="SourceDefinition.BarrenSinceUtc"/> ставит сам.
    /// </summary>
    public static (List<SourceDefinition> Disable, List<SourceDefinition> Remove) RotateBarrenSources(
        IEnumerable<SourceDefinition> sources,
        DateTime nowUtc)
    {
        var disable = new List<SourceDefinition>();
        var remove = new List<SourceDefinition>();

        foreach (var source in sources)
        {
            if (source.IsMine || SourceKinds.IsProxy(source) || string.IsNullOrWhiteSpace(source.Name))
            {
                continue;
            }

            // Источник дал хотя бы один рабочий сервер — он не бесплодный, карантин снимаем.
            if (source.TotalWorkingNodeCount > 0)
            {
                source.BarrenSinceUtc = null;
                continue;
            }

            // Уже помечен бесплодным: отсчитываем отключение и удаление от этой метки.
            if (source.BarrenSinceUtc is { } barrenSince)
            {
                if (nowUtc - barrenSince >= NodeLimits.BarrenRemoveAfter)
                {
                    remove.Add(source);
                }
                else if (source.Enabled && nowUtc - barrenSince >= NodeLimits.BarrenDisableAfter)
                {
                    disable.Add(source);
                }

                continue;
            }

            // Кандидат: прожил достаточно, серверов много, рабочих ноль.
            if (source.Enabled &&
                source.TotalNodeCount >= NodeLimits.BarrenMinNodes &&
                source.FirstSeenUtc != default &&
                nowUtc - source.FirstSeenUtc >= NodeLimits.BarrenDisableAfter)
            {
                source.BarrenSinceUtc = nowUtc;
                disable.Add(source);
            }
        }

        return (disable, remove);
    }

    /// <summary>
    /// Удаление источников из карантина: дата публикации не определялась долго — источник
    /// «не вернулся». Возвращает источники на удаление.
    /// </summary>
    public static List<SourceDefinition> RotateQuarantinedSources(
        IEnumerable<SourceDefinition> sources,
        DateTime nowUtc)
    {
        var remove = new List<SourceDefinition>();
        foreach (var source in sources)
        {
            if (source.IsMine)
            {
                continue;
            }

            if (source.QuarantineSinceUtc is { } since &&
                nowUtc - since >= NodeLimits.QuarantineRemoveAfter)
            {
                remove.Add(source);
            }
        }

        return remove;
    }

    /// <summary>Какой из двух источников с одинаковым содержимым оставить.</summary>
    internal static bool IsBetterSource(SourceDefinition candidate, SourceDefinition current)
    {
        if (candidate.IsMine != current.IsMine)
        {
            return candidate.IsMine;
        }

        if (candidate.IsTrusted != current.IsTrusted)
        {
            return candidate.IsTrusted;
        }

        if (candidate.TotalWorkingNodeCount != current.TotalWorkingNodeCount)
        {
            return candidate.TotalWorkingNodeCount > current.TotalWorkingNodeCount;
        }

        var candidateSeen = candidate.FirstSeenUtc != default;
        var currentSeen = current.FirstSeenUtc != default;
        if (candidateSeen != currentSeen)
        {
            return candidateSeen;
        }

        if (candidate.FirstSeenUtc != current.FirstSeenUtc)
        {
            // Старше — провереннее, ему и оставаться.
            return candidate.FirstSeenUtc < current.FirstSeenUtc;
        }

        return string.CompareOrdinal(candidate.Url, current.Url) < 0;
    }

    /// <summary>
    /// Контент-дедуп источников: у разных источников один и тот же текст — оставляем
    /// лучший, остальные помечаем пропуском. Автоподобранные зеркала возвращаются на
    /// удаление; их узлы — те же, что у лучшего источника.
    /// </summary>
    public static (List<SourceDefinition> Remove, HashSet<FetchOutcome> Skip) DedupMirrors(
        IEnumerable<FetchOutcome> outcomes)
    {
        var remove = new List<SourceDefinition>();
        var skip = new HashSet<FetchOutcome>();
        var bestByHash = new Dictionary<string, FetchOutcome>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            if (string.IsNullOrEmpty(outcome.ContentHash))
            {
                continue;
            }

            if (!bestByHash.TryGetValue(outcome.ContentHash, out var current) ||
                IsBetterSource(outcome.Source, current.Source))
            {
                bestByHash[outcome.ContentHash] = outcome;
            }
        }

        foreach (var outcome in outcomes)
        {
            if (string.IsNullOrEmpty(outcome.ContentHash) ||
                bestByHash[outcome.ContentHash] == outcome)
            {
                continue;
            }

            if (outcome.Source.IsAutoDiscovered && !outcome.Source.IsMine)
            {
                remove.Add(outcome.Source);
            }

            skip.Add(outcome);
        }

        return (remove, skip);
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
        // Проверены 24.11.2026 по накопленной статистике приложения: собрали сотни-тысячи
        // серверов, но ни один не подтвердил работу. Убираем из сборки и у пользователей.
        "https://raw.githubusercontent.com/chenzhihan1751-cyber/node-sub-zixf/main/sub/v2ray.txt",
        "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/base64/all_sub.txt",
        "https://raw.githubusercontent.com/4n0nymou3/multi-proxy-config-fetcher/main/configs/proxy_configs.txt",
        "https://raw.githubusercontent.com/ShatakVPN/ConfigForge-V2Ray/main/configs/all.txt",
        "https://raw.githubusercontent.com/amirkma/proxykma/main/mix.txt",
        "https://raw.githubusercontent.com/Fl1pse/vless-subs/main/normal.txt",
        "https://raw.githubusercontent.com/ProblemTheCode/SylphNet-public/main/sub/sub.txt",
        "https://raw.githubusercontent.com/TigerVpnGit/TigerVpn-Sup-Free/main/Sup.txt",
        "https://raw.githubusercontent.com/V2HUBIR/V2HUBIR-Free-V2Ray-Configs-free-Sub/main/sub_base64.txt",
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

        // Крупные агрегаторы: серверов много, но и пользы больше — по умолчанию включены.
        new() { Name = "barry-far vless (GitHub)", Url = "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/vless.txt", Kind = SourceKind.PlainList },
        new() { Name = "MhdiTaheri mix (GitHub)", Url = "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/mix", Kind = SourceKind.PlainList },
        new() { Name = "Mineral nodes (GitHub)", Url = "https://raw.githubusercontent.com/LalatinaHub/Mineral/master/result/nodes", Kind = SourceKind.PlainList },

        // Проверены 23.11.2026: обновляются ежедневно или ежечасно, содержат сотни–тысячи
        // ссылок всех протоколов (VLESS/VMess/Trojan/SS/Hysteria2). Часть списков —
        // base64-подписки, поэтому Kind = Subscription.
        new() { Name = "Barabama FreeNodes", Url = "https://raw.githubusercontent.com/Barabama/FreeNodes/main/nodes/merged.txt", Kind = SourceKind.PlainList },
        new() { Name = "DukeMehdi configs", Url = "https://raw.githubusercontent.com/DukeMehdi/FreeList-V2ray-Configs/main/Configs/All-DukeMehdi-Configs.txt", Kind = SourceKind.PlainList },
        new() { Name = "rtwo2 FastNodes", Url = "https://raw.githubusercontent.com/rtwo2/FastNodes/main/sub/top.txt", Kind = SourceKind.PlainList },
        new() { Name = "FlareFeed Top500", Url = "https://raw.githubusercontent.com/svinakraft-maker/FlareFeed/main/public/Top500.txt", Kind = SourceKind.PlainList },
        new() { Name = "NoMoreWalls (подписка)", Url = "https://raw.githubusercontent.com/peasoft/NoMoreWalls/master/list.txt", Kind = SourceKind.Subscription },

        // Текущий избранный набор с рабочей установки (23.11.2026): эти источники
        // уже были включены у пользователя и давали серверы. Вшиваем их в приложение,
        // чтобы новая установка сразу была настроена так же. Два старых источника
        // (jafarm83/ConfigV2Ray/singbox.json и SoliSpirit/SolVPN/sub6.txt) не включены:
        // их файлы не обновлялись больше суток, сбор всё равно пропустит их по свежести.
        new() { Name = "3inker v2ray (без РФ)", Url = "https://raw.githubusercontent.com/3inker/v2ray-subscription/main/subs/all_not_ru.txt", Kind = SourceKind.PlainList },
        new() { Name = "farshid78 Free-v2ray (Clash)", Url = "https://raw.githubusercontent.com/farshid78/Free-v2ray-Configs/main/all/clash.yaml", Kind = SourceKind.PlainList },
        new() { Name = "iboxz collector mix", Url = "https://raw.githubusercontent.com/iboxz/free-v2ray-collector/main/main/mix", Kind = SourceKind.PlainList },
        new() { Name = "Mehinator Derafsh (подписка)", Url = "https://raw.githubusercontent.com/Mehinator/Derafsh/main/derafsh_sub.txt", Kind = SourceKind.Subscription },
        new() { Name = "NamiraNet V2Configs (подписка)", Url = "https://raw.githubusercontent.com/NamiraNet/V2Configs/main/results.txt", Kind = SourceKind.Subscription },
        new() { Name = "SoliSpirit all configs", Url = "https://raw.githubusercontent.com/SoliSpirit/v2ray-configs/main/all_configs.txt", Kind = SourceKind.PlainList },
        new() { Name = "zxcursedzxc vless", Url = "https://raw.githubusercontent.com/zxcursedzxc0721/vless-subscriptions/main/all/vless.txt", Kind = SourceKind.PlainList },

        // Проверены 24.11.2026: активные репозитории с ежедневным (или более частым)
        // обновлением, ориентированные на обход блокировок в РФ или проверяющие
        // серверы перед публикацией. Часть списков небольшая — это нормально:
        // ценность в свежести и в том, что серверы уже отфильтрованы автором.
        new() { Name = "igareck — чёрные списки РФ (VLESS)", Url = "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS.txt", Kind = SourceKind.PlainList },
        new() { Name = "igareck — чёрные списки РФ (SS/Hy2/VMess/Trojan)", Url = "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_SS+All_RUS.txt", Kind = SourceKind.PlainList },
        new() { Name = "igareck — белые списки РФ (Reality, моб.)", Url = "https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/Vless-Reality-White-Lists-Rus-Mobile.txt", Kind = SourceKind.PlainList },
        new() { Name = "kort0881 — VLESS для РФ (RU-SNI)", Url = "https://raw.githubusercontent.com/kort0881/vpn-vless-configs-russia/main/data/githubmirror/ru-sni/vless.txt", Kind = SourceKind.PlainList },
        new() { Name = "Dima198987 vless-neko", Url = "https://raw.githubusercontent.com/Dima198987/vless-neko-subscription/main/vless_configs.txt", Kind = SourceKind.PlainList },
        new() { Name = "mehrtat vless-collector", Url = "https://raw.githubusercontent.com/mehrtat/vless-collector/main/sub.txt", Kind = SourceKind.PlainList },
        new() { Name = "Mahdi0024 ProxyCollector", Url = "https://raw.githubusercontent.com/Mahdi0024/ProxyCollector/master/sub/proxies.txt", Kind = SourceKind.PlainList },
        new() { Name = "LeilaoMi — проверенные (sing-box)", Url = "https://raw.githubusercontent.com/LeilaoMi/AutoMergePublicNodes-Optimized/main/output/global.txt", Kind = SourceKind.PlainList },
        new() { Name = "awesome-vpn (ежедневно)", Url = "https://raw.githubusercontent.com/awesome-vpn/awesome-vpn/master/clash.yaml", Kind = SourceKind.PlainList },

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
            var defaults = DefaultSources()
                .Where(source => !SourceBlacklist.IsBlocked(source, out _))
                .ToList();
            SeedFirstSeen(defaults);
            SaveSources(defaults);
            return defaults;
        }

        // Убираем источники, которые больше не поставляем (давно не обновлялись).
        var obsoleteRemoved = saved.RemoveAll(source =>
            ObsoleteSourceUrls.Any(url => source.Url.Equals(url, StringComparison.OrdinalIgnoreCase)));

        // Чёрный список: и ручные, и автоматические блокировки. «Мои» подписки
        // не трогаем — пользователь добавил их осознанно.
        var blacklistedRemoved = saved.RemoveAll(source =>
            !source.IsMine && SourceBlacklist.IsBlocked(source, out _));
        if (blacklistedRemoved > 0)
        {
            AppLog.Write("sources", $"Убрано источников из чёрного списка: {blacklistedRemoved}");
        }

        // Источники серверов без даты публикации (не GitHub) больше не используем.
        // У авто-найденных сайтов дата есть в LastPublishedUtc — такие не трогаем.
        obsoleteRemoved += saved.RemoveAll(source =>
            !source.IsMine &&
            !SourceKinds.IsProxy(source) &&
            source.LastPublishedUtc is null &&
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
            if (SourceBlacklist.IsBlocked(defaultSource, out _))
            {
                continue;
            }

            if (saved.All(s => !s.Url.Equals(defaultSource.Url, StringComparison.OrdinalIgnoreCase)))
            {
                saved.Add(defaultSource);
                addedDefaults++;
            }
        }

        var firstSeenChanged = SeedFirstSeen(saved);

        if (obsoleteRemoved > 0 || blacklistedRemoved > 0 || addedDefaults > 0 || firstSeenChanged)
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

    /// <summary>Ставит источнику дату первого появления, если её нет (разовая миграция).</summary>
    private static bool SeedFirstSeen(List<SourceDefinition> sources)
    {
        var changed = false;
        var now = DateTime.UtcNow;
        foreach (var source in sources)
        {
            if (source.FirstSeenUtc == default)
            {
                source.FirstSeenUtc = now;
                changed = true;
            }
        }

        return changed;
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
        // Источники из чёрного списка не собираем вовсе: иначе автопоиск или старый
        // sources.dat продолжат тянуть заведомо мёртвый список.
        var allSources = sources.ToList();
        var blockedSources = allSources
            .Where(source => !source.IsMine && source.Enabled && SourceBlacklist.IsBlocked(source, out _))
            .ToList();
        foreach (var blocked in blockedSources)
        {
            blocked.LastError = "источник в чёрном списке — пропущен";
            AppLog.Write("sources", $"{blocked.Name}: в чёрном списке — пропущен");
        }

        // Каналы Telegram собираются и как серверы, и как MTProto-прокси: публичные
        // конфиг-каналы постят ссылки vless/vmess, а не только tg://proxy. Чистые
        // сайты/списки MTProto в сбор серверов не попадают.
        var enabled = allSources
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url))
            .Where(SourceKinds.IsServerSource)
            .Where(source => !blockedSources.Contains(source))
            .ToList();

        // Общий бюджет обращений к GitHub API на этот сбор: источники сверх лимита
        // собираются без проверки даты, а не остаются без серверов.
        var apiBudget = new GitHubSourceFreshness.ApiBudget(GitHubSourceFreshness.MaximumApiRequests);

        var batches = await Task.WhenAll(enabled.Select(async source =>
        {
            FetchOutcome outcome;
            try
            {
                // Сбор серверов — строго из источников, опубликованных не позже окна.
                // Если дату определить нельзя, источник не собираем; при временном
                // сбое GitHub API источник не удаляем, чтобы пережить недоступность API.
                var freshness = await GitHubSourceFreshness
                    .CheckAsync(source, maximumAge, cancellationToken, apiBudget).ConfigureAwait(false);
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
                    else if (!freshness.DateKnown && !source.IsMine)
                    {
                        // Дату определить нельзя, но API отвечает: источник мог «переехать»
                        // или временно сменить формат. Не удаляем сразу — карантин.
                        source.QuarantineSinceUtc ??= DateTime.UtcNow;
                        outcome = new FetchOutcome
                        {
                            Source = source,
                            Skipped = true,
                            Quarantine = true,
                            Error = "дату публикации определить нельзя — источник в карантине",
                        };
                        AppLog.Write("sources", $"{source.Name}: нет даты публикации — карантин");
                    }
                    else
                    {
                        // Дата известна, но источник не обновлялся в окне — просто пропускаем.
                        source.QuarantineSinceUtc = null;
                        outcome = new FetchOutcome
                        {
                            Source = source,
                            Skipped = true,
                            Error = "источник не обновлялся в выбранном окне — пропущен",
                        };
                        AppLog.Write("sources", $"{source.Name}: пропущен по свежести");
                    }
                }
                else
                {
                    // Дата определилась — источник здоров, карантин снимаем.
                    source.QuarantineSinceUtc = null;
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
                    ? $"{source.Name}: {outcome.Nodes.Count} серверов" +
                      (outcome.Suppressed > 0 ? $", пропущено по кэшу {outcome.Suppressed}" : string.Empty)
                    : $"{source.Name}: ошибка — {outcome.Error}");
            onSource?.Invoke(outcome);
            return outcome;
        })).ConfigureAwait(false);

        // Даты публикации узнали у GitHub API — сохраняем на диск, чтобы следующий
        // запуск не жег квоту повторно.
        GitHubSourceFreshness.PersistCache();

        // Контент-дедуп: у разных источников одно и то же содержимое — оставляем лучший,
        // а автоподобранные зеркала удаляем (их узлы те же, что у лучшего источника).
        var (mirrorRemoval, skipMirror) = NodeSourceStats.DedupMirrors(batches);
        foreach (var mirror in mirrorRemoval)
        {
            AppLog.Write("sources", $"Удаляю зеркало (то же содержимое): {mirror.Name}");
        }

        var unique = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
        var mtproto = new Dictionary<string, MtProtoProxy>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var outcome in batches)
        {
            if (skipMirror.Contains(outcome))
            {
                continue;
            }

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

        // Ротация «бесплодных» источников: давние и ни разу не давшие рабочего сервера
        // отключаются, а затем удаляются — на их место автопоиск подберёт новые.
        var (barrenDisable, barrenRemove) = NodeSourceStats.RotateBarrenSources(allSources, DateTime.UtcNow);

        // Карантин: источники без даты публикации отключаем, не вернувшихся — удаляем.
        var quarantine = batches.Where(outcome => outcome.Quarantine).Select(outcome => outcome.Source).ToList();
        var quarantineRemoval = NodeSourceStats.RotateQuarantinedSources(allSources, DateTime.UtcNow);

        // Закрываем цикл «удалили — автопоиск нашёл снова». Мягкая блокировка на 30 дней:
        // если источник оживёт, после срока его снова можно будет добавить.
        foreach (var source in barrenDisable.Concat(barrenRemove).Distinct())
        {
            if (!source.IsMine)
            {
                SourceBlacklist.BlockAuto(source, "barren",
                    $"0 рабочих на {source.TotalNodeCount} серверов — источник бесплоден",
                    TimeSpan.FromDays(30));
            }
        }

        foreach (var source in mirrorRemoval)
        {
            if (!source.IsMine)
            {
                SourceBlacklist.BlockAuto(source, "mirror",
                    "точное зеркало уже известного списка", TimeSpan.FromDays(30),
                    repositoryScope: false);
            }
        }

        foreach (var outcome in batches.Where(outcome => outcome.RemoveSource))
        {
            if (!outcome.Source.IsMine)
            {
                SourceBlacklist.BlockAuto(outcome.Source, "gone",
                    outcome.Error ?? "источник больше не отдаёт список", TimeSpan.FromDays(7),
                    repositoryScope: false);
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
                .Concat(mirrorRemoval)
                .Concat(barrenRemove)
                .Concat(quarantineRemoval)
                .Distinct()
                .ToList(),
            SourcesToDisable = barrenDisable.Concat(quarantine).Distinct().ToList(),
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

        // Каналы Telegram: прямой t.me, при неудаче или пустом ответе — RSS-зеркала.
        if (SourceKinds.IsTelegram(source))
        {
            return await FetchTelegramAsync(source, cancellationToken).ConfigureAwait(false);
        }

        // Разбор идёт в фоне: список на несколько мегабайт не должен подвешивать UI.
        var (rawText, subscriptionInfo) = await FetchRawAsync(source, cancellationToken).ConfigureAwait(false);
        return ParseText(source, rawText, subscriptionInfo);
    }

    /// <summary>Разбирает уже скачанный текст источника в серверы и MTProto-прокси.</summary>
    private static FetchOutcome ParseText(SourceDefinition source, string text, string? subscriptionInfo)
    {
        source.SubscriptionInfo = subscriptionInfo;
        var parsed = SubscriptionParser.Parse(text, source.Name);

        // Заглушки вида «Приложение не поддерживается» и группы с 127.0.0.1:1 — не серверы.
        var candidates = parsed.Where(node => !IsPlaceholderNode(node)).ToList();
        var nodes = new List<ProxyNode>(candidates.Count);
        var suppressed = 0;
        foreach (var node in candidates)
        {
            node.IsMine = source.IsMine;
            // Мёртвые адреса из ежедневных списков не гоняем по кругу: негативный кэш
            // помнит недавние подтверждённые провалы. «Мои» подписки не фильтруем.
            if (!node.IsMine && NodeNegativeCache.IsBlocked(node))
            {
                suppressed++;
                continue;
            }

            nodes.Add(node);
        }

        source.LastSuppressedCount = suppressed;
        source.SuppressedNodeCount += suppressed;

        // «Всего зарегистрировано» — уникальные серверы за всё время, а не за этот сбор.
        // Пропущенные по кэшу не регистрируем: иначе мёртвый источник продолжает
        // выглядеть большим, а его выход занижается.
        source.RegisterNodes(nodes);

        string? error = null;
        if (candidates.Count == 0 && parsed.Count > 0)
        {
            error = "Провайдер отдаёт только заглушки — серверы доступны лишь в его клиенте (Happ). " +
                    "Нужна универсальная подписка или ссылки на серверы.";
        }

        var mtproto = MtProtoService.Extract(text, source.Name);
        return new FetchOutcome
        {
            Source = source,
            Nodes = nodes,
            MtProto = mtproto,
            Error = error,
            ContentHash = Sha256(text),
            Suppressed = suppressed,
        };
    }

    /// <summary>
    /// Канал Telegram как источник серверов: сначала прямой t.me, при неудаче или пустом
    /// ответе — RSS-зеркала (читают канал на своей стороне, обход не нужен).
    /// </summary>
    private static async Task<FetchOutcome> FetchTelegramAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        var freshAfter = DateTime.UtcNow.AddHours(-TelegramText.FreshHours);

        // 1. Прямой t.me: открывается только через работающий обход.
        try
        {
            var (rawText, info) = await FetchRawAsync(source, cancellationToken).ConfigureAwait(false);
            var direct = ParseText(source, TelegramText.Extract(rawText, freshAfter), info);
            if (direct.Nodes.Count > 0)
            {
                return direct;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("sources", $"{source.Name}: t.me недоступен, пробую RSS-зеркала", exception);
        }

        // 2. RSS-зеркала: не требуют доступа к t.me.
        var channel = MtProtoService.ChannelName(source.Url);
        if (channel.Length > 0)
        {
            foreach (var mirror in TelegramRssMirrors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var mirrorSource = new SourceDefinition
                    {
                        Name = source.Name,
                        Url = string.Format(mirror, channel),
                        Kind = SourceKind.Telegram,
                    };
                    var (raw, _) = await FetchRawAsync(
                            mirrorSource, cancellationToken,
                            timeoutOverride: TimeSpan.FromSeconds(8),
                            retryDirect: false)
                        .ConfigureAwait(false);
                    var outcome = ParseText(source, TelegramText.Extract(WebUtility.HtmlDecode(raw), freshAfter), null);
                    if (outcome.Nodes.Count > 0)
                    {
                        AppLog.Write("sources",
                            $"{source.Name}: получено через RSS ({new Uri(mirrorSource.Url).Host}) — {outcome.Nodes.Count} серверов");
                        return outcome;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("sources", $"{source.Name}: RSS-зеркало {string.Format(mirror, channel)} не ответило", exception);
                }
            }
        }

        return new FetchOutcome { Source = source, Error = "канал не отдал серверы (t.me и RSS-зеркала недоступны)" };
    }

    /// <summary>Служебные «серверы»-маркеры: группы и сообщения провайдера.</summary>
    private static bool IsPlaceholderNode(ProxyNode node) =>
        node.Port == 1 && (node.Server is "0.0.0.0" or "127.0.0.1" or "::1" or "localhost");

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Зеркала для raw-контента GitHub: в РФ raw.githubusercontent.com часто режется.</summary>
    private static readonly string[] RawMirrors = { "https://ghfast.top/", "https://gh-proxy.com/" };

    /// <summary>RSS-зеркала Telegram-каналов: читают канал на своей стороне, t.me не нужен.</summary>
    internal static readonly string[] TelegramRssMirrors =
    {
        "https://tg.i-c-a.su/rss/{0}",
        "https://rsshub.rssforever.com/telegram/channel/{0}",
        "https://rsshub.ktachibana.party/telegram/channel/{0}",
        "https://rsshub.liumingye.cn/telegram/channel/{0}",
    };

    internal readonly record struct RawFetch(string? Text, string? SubscriptionInfo, string? Error, bool NotModified);

    /// <summary>Сырой ответ источника: используется и для серверов, и для MTProto-прокси.</summary>
    public static async Task<(string Text, string? SubscriptionInfo)> FetchRawAsync(
        SourceDefinition source,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null,
        bool retryDirect = true)
    {
        // Страницы t.me маленькие: либо открываются быстро, либо канал недоступен.
        var timeout = timeoutOverride ?? TimeSpan.FromSeconds(SourceKinds.IsTelegram(source) ? 15 : 40);

        // Порядок попыток: через системный прокси → напрямую → зеркала GitHub.
        var attempts = new List<(HttpClient Client, string Url)> { (Http, source.Url) };
        if (retryDirect)
        {
            attempts.Add((HttpDirect, source.Url));
        }

        if (IsGitHubRaw(source.Url))
        {
            foreach (var mirror in RawMirrors)
            {
                attempts.Add((HttpDirect, mirror + source.Url));
            }
        }

        string? lastError = null;
        foreach (var (client, url) in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await FetchOnceAsync(client, source, url, timeout, cancellationToken).ConfigureAwait(false);
            if (result.Text is not null)
            {
                return (result.Text, result.SubscriptionInfo);
            }

            if (result.NotModified)
            {
                // Контент не менялся: возвращаем закэшированный текст, иначе пересборка
                // списка потеряла бы серверы этого источника.
                if (RawContentStore.TryGetText(source.Url, out var cachedText))
                {
                    return (cachedText, result.SubscriptionInfo);
                }

                // Кэша текста нет (был очищен) — повторяем тот же URL без условного заголовка.
                var full = await FetchOnceAsync(client, source, url, timeout, cancellationToken, useEtag: false)
                    .ConfigureAwait(false);
                if (full.Text is not null)
                {
                    return (full.Text, full.SubscriptionInfo);
                }

                if (full.Error is not null)
                {
                    lastError = full.Error;
                }

                continue;
            }

            if (result.Error is not null)
            {
                lastError = result.Error;
            }
        }

        var hint = SourceKinds.IsTelegram(source)
            ? " Канал t.me открывается только через работающий обход — подключитесь к серверу и повторите."
            : string.Empty;
        throw new HttpRequestException(
            lastError is null
                ? $"Источник «{source.Name}» не ответил за {timeout.TotalSeconds:0} с.{hint}"
                : $"Источник «{source.Name}»: {lastError}.{hint}");
    }

    private static async Task<RawFetch> FetchOnceAsync(
        HttpClient client,
        SourceDefinition source,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool useEtag = true)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Многие панели отдают формат в зависимости от клиента; v2rayNG — самый совместимый.
            request.Headers.UserAgent.ParseAdd("v2rayNG/1.9.16 (VieNLink/1.0)");
            request.Headers.Accept.ParseAdd("*/*");

            if (useEtag && RawContentStore.GetETag(source.Url) is { } etag)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            var info = SubscriptionParser.ParseUserInfo(
                response.Headers.TryGetValues("subscription-userinfo", out var userInfo) ? userInfo : null);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new RawFetch(null, info, null, NotModified: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new RawFetch(null, null, $"HTTP {(int)response.StatusCode}", NotModified: false);
            }

            var text = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            // Запоминаем ETag и текст: при следующем сборе 304 вернём текст из кэша без скачивания.
            RawContentStore.Store(source.Url, text, response.Headers.ETag?.ToString());
            return new RawFetch(text, info, null, NotModified: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RawFetch(null, null, $"не ответил за {timeout.TotalSeconds:0} с", NotModified: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new RawFetch(null, null, exception.Message, NotModified: false);
        }
    }

    private static bool IsGitHubRaw(string url) =>
        url.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Кэш сырых ответов источников для условных GET: ETag в памяти (маленький файл),
    /// текст — отдельными файлами на диске. Когда источник возвращает 304, список
    /// пересобирается из закэшированного текста, и серверы источника не пропадают.
    /// </summary>
    private static class RawContentStore
    {
        private const int MaxCachedTextBytes = 4 * 1024 * 1024;

        private static readonly ConcurrentDictionary<string, string> Etags =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object Gate = new();
        private static bool _loaded;

        public static string? GetETag(string url)
        {
            EnsureLoaded();
            return Etags.TryGetValue(url, out var etag) ? etag : null;
        }

        public static void Store(string url, string text, string? etag)
        {
            if (string.IsNullOrWhiteSpace(etag) || text.Length > MaxCachedTextBytes)
            {
                return;
            }

            lock (Gate)
            {
                Etags[url] = etag;
                WriteText(url, text);
                SaveEtags();
            }
        }

        public static bool TryGetText(string url, out string text)
        {
            text = string.Empty;
            try
            {
                var path = TextPath(url);
                if (!File.Exists(path))
                {
                    return false;
                }

                text = File.ReadAllText(path);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (Gate)
            {
                if (_loaded)
                {
                    return;
                }

                var etags = JsonFileStore.Read<Dictionary<string, string>>(AppPaths.RawEtagsPath);
                if (etags is not null)
                {
                    foreach (var pair in etags)
                    {
                        Etags[pair.Key] = pair.Value;
                    }
                }

                _loaded = true;
            }
        }

        private static void SaveEtags()
        {
            try
            {
                JsonFileStore.Write(AppPaths.RawEtagsPath, Etags.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("sources", "Не удалось сохранить ETag источников", exception);
            }
        }

        private static void WriteText(string url, string text)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.RawCacheDirectory);
                File.WriteAllText(TextPath(url), text);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("sources", "Не удалось сохранить кэш ответа источника", exception);
            }
        }

        private static string TextPath(string url)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
            return Path.Combine(AppPaths.RawCacheDirectory, hash + ".txt");
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
    /// Встроенный стартовый набор серверов (seed): читается на первой установке, пока кэш
    /// ещё пуст, чтобы не ждать первый сбор и проверку. Файл — список ссылок по одной в строке,
    /// генерируется режимом SelfTest `exportseed` из рабочих серверов перед релизом.
    /// </summary>
    public static List<ProxyNode> LoadSeed(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new List<ProxyNode>();
            }

            return ParseSeedLinks(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("seed", "Не удалось прочитать встроенный набор серверов", exception);
            return new List<ProxyNode>();
        }
    }

    /// <summary>Разбирает текст seed-файла в серверы, помечая их «своими» (пробуются первыми).</summary>
    public static List<ProxyNode> ParseSeedLinks(string text)
    {
        var nodes = SubscriptionParser.Parse(text, "Seed (встроенный)");
        foreach (var node in nodes)
        {
            node.IsMine = true;
        }

        return nodes;
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
