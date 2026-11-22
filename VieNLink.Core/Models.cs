using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VieNLink;

/// <summary>Состояние сервера после проверок.</summary>
internal enum NodeStatus
{
    /// <summary>Ещё не проверялся.</summary>
    Unknown = 0,

    /// <summary>
    /// Не открыл ни один проверочный сервис: непроверенный сервер — сразу, уже работавший —
    /// после <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд.
    /// </summary>
    Dead = 1,

    /// <summary>Открывает хотя бы один проверочный сервис.</summary>
    Working = 2,
}

/// <summary>Один сервер (сервер) из публичного списка или подписки.</summary>
internal sealed class ProxyNode
{
    public string RawLink { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Сервер пришёл из личной подписки — показывается отдельно и пробуется первым.</summary>
    public bool IsMine { get; set; }

    /// <summary>Задержка последней проверки в миллисекундах; null — ещё не проверялся.</summary>
    public int? DelayMs { get; set; }

    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>
    /// Когда сервер последний раз пытались проверить. Нужна, чтобы партия, где ничего
    /// не открылось (откат), не проверялась заново теми же серверами по кругу.
    /// </summary>
    public DateTime? TriedAtUtc { get; set; }

    /// <summary>
    /// Когда сервер последний раз встречался в источниках. Серверы, которые давно
    /// не приходят из списков, удаляются — в кэше остаются только свежие данные.
    /// </summary>
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Скорость последнего замера, Мбит/с; null — не замерялась. Задержка не показывает пропускную способность.</summary>
    public double? SpeedMbps { get; set; }

    /// <summary>Сервер просит не проверять TLS-сертификат (insecure): трафик виден посреднику, выбираем его последним.</summary>
    public bool AllowInsecure { get; set; }

    // Результаты проверки по сервисам: null — не проверялся.
    public bool? YoutubeOk { get; set; }

    public bool? TelegramOk { get; set; }

    /// <summary>Универсальный доступ (Cloudflare): главный признак пригодности сервера.</summary>
    public bool? UniversalOk { get; set; }

    public bool? DiscordOk { get; set; }

    /// <summary>Сколько заблокированных доменов открылось через сервер — главный показатель качества.</summary>
    public int OpenDomains { get; set; }

    public NodeStatus Status { get; set; } = NodeStatus.Unknown;

    /// <summary>
    /// Сколько проверок подряд сервер не открыл ни один сервис. Успешная проверка
    /// сбрасывает счётчик; после <see cref="NodeLimits.RemoveAfterFailures"/> сервер удаляется.
    /// </summary>
    public int FailCount { get; set; }

    /// <summary>
    /// Ключ повтора: один физический сервер = один сервер. Разные ссылки на тот же
    /// хост:порт (другой UUID, имя или камуфляж) склеиваются в одну строку.
    /// </summary>
    [JsonIgnore]
    public string EndpointKey =>
        Server.Trim().TrimEnd('.').ToLowerInvariant() + ":" +
        Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Готовый outbound-объект sing-box. Пересоздаётся при загрузке из кэша.</summary>
    [JsonIgnore]
    public string OutboundJson { get; set; } = string.Empty;

    /// <summary>
    /// Оценка маскировки, посчитанная один раз: <see cref="NodeRanking.Score"/> ищет
    /// подстроки в outbound-JSON, а сортировка тысяч серверов вызывала её сотни тысяч раз.
    /// Поля, от которых зависит оценка, после разбора ссылки не меняются.
    /// </summary>
    [JsonIgnore]
    public int RankScore => _rankScore ??= NodeRanking.Score(this);

    private int? _rankScore;

    /// <summary>Стабильный идентификатор: одинаковые серверы из разных источников склеиваются.</summary>
    [JsonIgnore]
    public string Identity { get; set; } = string.Empty;

    public string Id => string.IsNullOrEmpty(Identity) ? ComputeId() : Identity;

    /// <summary>Стабильный идентификатор: одинаковые серверы из разных источников склеиваются.</summary>
    public string ComputeId()
    {
        var key = Protocol.ToLowerInvariant() + "|" + Server.ToLowerInvariant() + "|" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + OutboundJson;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..20];
    }

    public string DisplayDelay => DelayMs is null ? "—" : DelayMs + " мс";
}

/// <summary>
/// Склейка повторов. Сначала убираются буквальные дубли (одинаковый outbound — тот же
/// тег), затем на один сервер:порт остаётся несколько лучших вариантов. Жёсткое
/// «одна строка на хост:порт» выбрасывало рабочие серверы с общих CDN: у Cloudflare под
/// одним адресом живут десятки аккаунтов с разными UUID и SNI, и оставшийся мог быть мёртвым.
/// </summary>
internal static class NodeDedup
{
    public static List<ProxyNode> Collapse(IEnumerable<ProxyNode> nodes)
    {
        var unique = new Dictionary<string, ProxyNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Server))
            {
                continue;
            }

            // Тег считается по всему outbound'у: одинаковые ссылки дают одинаковый тег.
            var key = string.IsNullOrWhiteSpace(node.Tag)
                ? node.EndpointKey + "|" + node.OutboundJson
                : node.Tag;
            if (!unique.TryGetValue(key, out var kept))
            {
                unique[key] = node;
                continue;
            }

            var winner = IsBetter(node, kept) ? node : kept;
            var loser = ReferenceEquals(winner, node) ? kept : node;
            Absorb(winner, loser);
            unique[key] = winner;
        }

        // Мягкий лимит на физический сервер: разные аккаунты на одном CDN-адресе — это
        // разные серверы, но хранить десятки вариантов одного адреса тоже незачем.
        var result = new List<ProxyNode>(unique.Count);
        foreach (var group in unique.Values.GroupBy(node => node.EndpointKey, StringComparer.OrdinalIgnoreCase))
        {
            // У большинства адресов один вариант: не считаем для них оценку маскировки.
            if (group.Count() == 1)
            {
                result.Add(group.First());
                continue;
            }

            var ordered = group.OrderByDescending(Preference).ToList();
            var keep = ordered.Take(NodeLimits.MaxPerEndpoint).ToList();
            foreach (var extra in ordered.Skip(NodeLimits.MaxPerEndpoint))
            {
                // Другой аккаунт на том же адресе — не дубль: его успехи нельзя
                // приписывать оставшемуся серверу, иначе кнопка получит «рабочий»
                // сервер, который этот сервис не открывает.
                Absorb(keep[0], extra, mergeChecks: false);
            }

            result.AddRange(keep);
        }

        return result;
    }

    /// <summary>Порядок предпочтения: личный, рабочий, проверенный без insecure, быстрый, маскированный, свежий.</summary>
    private static (int Mine, int Working, int Tested, int Secure, double Speed, int Rank, long Seen) Preference(ProxyNode node) =>
        (node.IsMine ? 1 : 0,
         node.Status == NodeStatus.Working ? 1 : 0,
         node.DelayMs is not null ? 1 : 0,
         node.AllowInsecure ? 0 : 1,
         node.SpeedMbps ?? -1,
         node.RankScore,
         (node.LastSeenUtc ?? DateTime.MinValue).Ticks);

    /// <summary>Какой из двух дублей оставить. Рабочий сервер важнее красивого, но непроверенного.</summary>
    private static bool IsBetter(ProxyNode candidate, ProxyNode current)
    {
        if (candidate.IsMine != current.IsMine)
        {
            return candidate.IsMine;
        }

        var candidateWorking = candidate.Status == NodeStatus.Working;
        var currentWorking = current.Status == NodeStatus.Working;
        if (candidateWorking != currentWorking)
        {
            return candidateWorking;
        }

        var candidateTested = candidate.DelayMs is not null;
        var currentTested = current.DelayMs is not null;
        if (candidateTested != currentTested)
        {
            return candidateTested;
        }

        if (candidateTested && currentTested && candidate.DelayMs != current.DelayMs)
        {
            return candidate.DelayMs < current.DelayMs;
        }

        if (candidate.AllowInsecure != current.AllowInsecure)
        {
            return !candidate.AllowInsecure;
        }

        var candidateScore = candidate.RankScore;
        var currentScore = current.RankScore;
        if (candidateScore != currentScore)
        {
            return candidateScore > currentScore;
        }

        return (candidate.LastSeenUtc ?? DateTime.MinValue) > (current.LastSeenUtc ?? DateTime.MinValue);
    }

    /// <summary>
    /// Переносит на оставшийся сервер всё полезное от дубля: проверки, метку «моя», источники.
    /// <paramref name="mergeChecks"/> — только для настоящих дублей (тот же outbound).
    /// У разных аккаунтов на общем CDN-адресе результаты проверок не переносятся:
    /// они относятся к другому UUID/SNI и могут не открывать тот же сервис.
    /// </summary>
    private static void Absorb(ProxyNode target, ProxyNode source, bool mergeChecks = true)
    {
        if (mergeChecks && target.DelayMs is null && source.DelayMs is not null)
        {
            target.DelayMs = source.DelayMs;
            target.CheckedAtUtc = source.CheckedAtUtc;
            target.YoutubeOk = source.YoutubeOk;
            target.TelegramOk = source.TelegramOk;
            target.UniversalOk = source.UniversalOk;
            target.DiscordOk = source.DiscordOk;
            target.Status = source.Status;
        }
        else if (mergeChecks)
        {
            // Доступ по сервисам объединяем: у дублей он мог проверяться в разное время.
            target.YoutubeOk = MergeAccess(target.YoutubeOk, source.YoutubeOk);
            target.TelegramOk = MergeAccess(target.TelegramOk, source.TelegramOk);
            target.UniversalOk = MergeAccess(target.UniversalOk, source.UniversalOk);
            target.DiscordOk = MergeAccess(target.DiscordOk, source.DiscordOk);
        }

        if (mergeChecks)
        {
            if (target.SpeedMbps is null && source.SpeedMbps is not null)
            {
                target.SpeedMbps = source.SpeedMbps;
            }

            target.FailCount = Math.Max(target.FailCount, source.FailCount);
            target.OpenDomains = Math.Max(target.OpenDomains, source.OpenDomains);
            if (source.TriedAtUtc is { } tried &&
                (target.TriedAtUtc is null || tried > target.TriedAtUtc))
            {
                target.TriedAtUtc = tried;
            }
        }

        target.IsMine |= source.IsMine;
        if (source.LastSeenUtc is { } seen &&
            (target.LastSeenUtc is null || seen > target.LastSeenUtc))
        {
            target.LastSeenUtc = seen;
        }

        MergeSource(target, source.SourceName);
    }

    /// <summary>Доступ считается открытым, если он подтверждён хотя бы у одного дубля.</summary>
    private static bool? MergeAccess(bool? target, bool? source) =>
        target == true || source == true ? true : target ?? source;

    /// <summary>Названия источников объединяются без повторов: видно, что сервер популярен.</summary>
    public static void MergeSource(ProxyNode node, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return;
        }

        if (node.SourceName.Length == 0)
        {
            node.SourceName = sourceName;
            return;
        }

        var known = node.SourceName.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        if (known.Any(name => name.Equals(sourceName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        node.SourceName = node.SourceName + ", " + sourceName;
    }
}

/// <summary>
/// Учёт неудачных проверок сервера. Сервер, который уже был рабочим, не удаляется с первого
/// провала: счётчик растёт, и только после <see cref="NodeLimits.RemoveAfterFailures"/>
/// провалов подряд такой сервер убирается из списка. Непроверенные серверы удаляются сразу —
/// их держать незачем.
/// </summary>
internal static class NodeHealth
{
    /// <summary>
    /// Сколько первых провалов рабочего сервера не стирают его результат: разовая ошибка
    /// сети или занятость бесплатного сервера не должна сразу убирать его из счётчиков.
    /// </summary>
    public const int GraceFailures = 2;

    /// <summary>
    /// Сколько провалов переживает непроверенный сервер. Раньше он удалялся с первого,
    /// и одна сетевая ошибка вычищала целую партию найденных серверов.
    /// </summary>
    public const int UnverifiedFailures = 2;

    /// <summary>Отмечает неудачную проверку. true — сервер пора удалить.</summary>
    public static bool RegisterFailure(ProxyNode node)
    {
        var wasWorking = node.Status == NodeStatus.Working;
        node.FailCount = Math.Max(1, node.FailCount + 1);

        if (!wasWorking)
        {
            // Непроверенный: данные чистить нечего, второй провал подряд — не случайность.
            node.CheckedAtUtc = DateTime.UtcNow;
            node.DelayMs = null;
            node.YoutubeOk = false;
            node.TelegramOk = false;
            node.UniversalOk = false;
            node.DiscordOk = false;
            node.OpenDomains = 0;
            if (node.FailCount == UnverifiedFailures || node.FailCount >= NodeLimits.RemoveAfterFailures)
            {
                node.Status = NodeStatus.Dead;
                return true;
            }

            node.TriedAtUtc = DateTime.UtcNow;
            return false;
        }

        // Рабочий сервер: результат последней удачной проверки сохраняем целиком.
        // Счётчик провалов только растёт; удаляем после RemoveAfterFailures провалов подряд.
        // Свежесть проверки (CheckedAtUtc) не обновляем — сервер сам выпадет из «свежих»,
        // и кнопки сервисов перестанут его предлагать, но из рабочего списка он не исчезнет.
        if (node.FailCount >= NodeLimits.RemoveAfterFailures)
        {
            node.Status = NodeStatus.Dead;
            return true;
        }

        node.Status = NodeStatus.Working;
        return false;
    }

    /// <summary>Успешная проверка: счётчик провалов сбрасывается.</summary>
    public static void RegisterSuccess(ProxyNode node)
    {
        node.FailCount = 0;
        node.Status = NodeStatus.Working;
    }
}

/// <summary>
/// Жёсткий лимит списка серверов. Когда список заполнен, новые серверы не добавляются
/// и источники не ищутся, пока накопленные не проверены: нерабочие удаляются,
/// место освобождается и загрузка продолжается.
/// </summary>
internal static class NodeLimits
{
    public const int DefaultMaxTotal = 5000;

    /// <summary>
    /// Сколько вариантов одного сервера:порта оставлять. У бесплатных CDN под одним
    /// адресом живут разные аккаунты, поэтому один вариант — слишком грубо.
    /// </summary>
    public const int MaxPerEndpoint = 4;

    /// <summary>
    /// После скольких неудачных проверок подряд убирается сервер, который раньше работал.
    /// Непроверенные серверы удаляются с первого провала — держать их в списке незачем.
    /// </summary>
    public const int RemoveAfterFailures = 5;

    /// <summary>
    /// Сколько рабочих серверов приложение старается держать наготове. Если их меньше,
    /// фоновая проверка идёт без пауз, пока минимум не наберётся.
    /// </summary>
    public const int MinWorkingNodes = 10;

    /// <summary>Сколько рабочих серверов нужно на каждый сервис: YouTube, Telegram, Discord
    /// и универсальный доступ. Пока сервис ниже этого порога, автопоиск ищет под него.</summary>
    public const int MinWorkingPerService = 5;

    /// <summary>
    /// Как часто перепроверять сервер, который уже считается рабочим. Бесплатные серверы живут
    /// минуты: «рабочий» из кэша без свежей перепроверки — главная причина, почему значок
    /// сервиса обещал сервер, которого кнопка не находила. Этот же интервал — окно свежести
    /// для значков и выбора сервера: счётчик не должен обещать сервер, проверенный дольше этого.
    /// </summary>
    public const int WorkingRecheckMinutes = 5;

    /// <summary>
    /// Насколько раньше окна свежести начинать перепроверку. Фон проверяет не мгновенно
    /// (такт раз в минуту, партия ограничена), поэтому сервер берётся в работу за минуту
    /// до истечения окна: иначе счётчик на секунды обнулялся бы между проверками.
    /// </summary>
    public const int RecheckLeadMinutes = 1;

    /// <summary>
    /// Оставляет не больше <paramref name="max"/> серверов: сначала личные, затем проверенные
    /// (рабочие), затем самые свежие, затем лучшие по маскировке и задержке.
    /// Возвращает число отброшенных.
    /// </summary>
    public static (List<ProxyNode> Nodes, int Trimmed) Trim(IEnumerable<ProxyNode> nodes, int max)
    {
        var list = nodes as List<ProxyNode> ?? nodes.ToList();
        var cap = Math.Max(1000, max);
        if (list.Count <= cap)
        {
            return (list, 0);
        }

        var kept = list
            .OrderByDescending(node => node.IsMine)
            .ThenByDescending(node => node.Status == NodeStatus.Working || node.DelayMs is not null)
            .ThenByDescending(node => node.OpenDomains)
            .ThenByDescending(node => node.LastSeenUtc ?? DateTime.MinValue)
            .ThenByDescending(node => node.RankScore)
            .ThenBy(node => node.DelayMs ?? int.MaxValue)
            .Take(cap)
            .ToList();
        return (kept, list.Count - kept.Count);
    }
}

internal enum SourceKind
{
    Auto,
    PlainList,
    Subscription,
    Telegram,

    /// <summary>Одна ссылка на сервер (vless://, vmess://…), вставленная вручную.</summary>
    SingleLink,

    /// <summary>Сайт или GitHub-список с MTProto-прокси для Telegram (не серверы).</summary>
    MtProto,
}

/// <summary>
/// Общее правило: источник относится к Telegram-прокси (канал или сайт с MTProto),
/// а не к списку серверов. Им пользуются сбор MTProto и фильтры источников серверов.
/// </summary>
internal static class SourceKinds
{
    /// <summary>Источник нужен для MTProto: канал Telegram или сайт/список с mtproto-прокси.</summary>
    public static bool IsProxy(SourceDefinition source) =>
        source.Kind is SourceKind.Telegram or SourceKind.MtProto ||
        LooksLikeTelegram(source.Url) ||
        LooksLikeMtProtoUrl(source.Url);

    /// <summary>Какой тип поставить источнику при добавлении по ссылке.</summary>
    public static SourceKind Classify(string url) =>
        LooksLikeTelegram(url) ? SourceKind.Telegram
        : LooksLikeMtProtoUrl(url) ? SourceKind.MtProto
        : SourceKind.Auto;

    public static bool LooksLikeTelegram(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Сайты и GitHub-репозитории со списками MTProto обычно содержат в адресе
    /// «mtproto», «mtproxy» или «mtpro».
    /// </summary>
    public static bool LooksLikeMtProtoUrl(string url) =>
        url.Contains("mtproto", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("mtproxy", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("mtpro", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Источник серверов: raw-список на GitHub, подписка или публичный канал Telegram.</summary>
internal sealed class SourceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public SourceKind Kind { get; set; } = SourceKind.Auto;
    public int LastNodeCount { get; set; }

    /// <summary>
    /// Сколько уникальных серверов когда-либо пришло из источника. Считается по
    /// <see cref="RegisteredNodeIds"/>, поэтому повторные сборы не раздувают число.
    /// </summary>
    public int TotalNodeCount { get; set; }

    /// <summary>
    /// Сколько уникальных серверов из источника хотя бы раз подтвердили работу
    /// (открыли сайт через себя). Считается по <see cref="RegisteredWorkingNodeIds"/>.
    /// </summary>
    public int TotalWorkingNodeCount { get; set; }

    /// <summary>
    /// Id уже засчитанных серверов: нужен, чтобы «всего зарегистрировано» не росло
    /// при каждом обновлении одного и того же списка. Файл источников шифруется.
    /// </summary>
    public HashSet<string> RegisteredNodeIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Id серверов, которые хотя бы раз были рабочими; повторные проверки не считаются.</summary>
    public HashSet<string> RegisteredWorkingNodeIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Учитывает серверы источника в общем счётчике (повторы не считаются).</summary>
    public void RegisterNodes(IEnumerable<ProxyNode> nodes)
    {
        foreach (var node in nodes)
        {
            RegisterId(node.Id);
        }
    }

    /// <summary>Учитывает один сервер по стабильному Id.</summary>
    public void RegisterId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        RegisteredNodeIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (RegisteredNodeIds.Add(id))
        {
            TotalNodeCount++;
        }
    }

    /// <summary>Учитывает сервер, который хотя бы раз был рабочим.</summary>
    public void RegisterWorkingId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        RegisteredWorkingNodeIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (RegisteredWorkingNodeIds.Add(id))
        {
            TotalWorkingNodeCount++;
        }
    }

    public DateTime? LastCheckedUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>Остаток трафика из заголовка subscription-userinfo, если провайдер его отдаёт.</summary>
    public string? SubscriptionInfo { get; set; }

    /// <summary>Источник найден автоматическим поиском, а не добавлен вручную.</summary>
    public bool IsAutoDiscovered { get; set; }

    /// <summary>Когда источник появился: для автоисточников держим только свежие.</summary>
    public DateTime AddedAtUtc { get; set; }

    /// <summary>Личная (обычно платная) подписка: показывается отдельно и имеет приоритет.</summary>
    public bool IsMine { get; set; }
}

/// <summary>Как заворачивать трафик: только системный прокси или полноценный TUN.</summary>
internal enum TunnelMode
{
    /// <summary>Системный прокси Windows: браузеры и приложения, уважающие настройки.</summary>
    SystemProxy,

    /// <summary>TUN-адаптер: весь трафик компьютера, нужны права администратора.</summary>
    Tun,
}

/// <summary>Что отправлять через сервер: всё, только список сайтов или всё, кроме списка.</summary>
internal enum SiteRouteMode
{
    All,
    OnlyListed,
    AllExceptListed,
}

/// <summary>Что делать с приложениями: все, только из списка или все, кроме списка.</summary>
internal enum AppRouteMode
{
    All,
    OnlyListed,
    AllExceptListed,
}

/// <summary>
/// Версии схем файлов данных. Увеличиваются при изменении формата, которое
/// требует миграции; по ним приложение понимает, что файл от старой или новой версии.
/// </summary>
internal static class SchemaVersions
{
    /// <summary>
    /// 2 — партия проверки в простое 40 → 200.
    /// 3 — разово включить все источники, которые раньше были выключены по умолчанию.
    /// 4 — список серверов ограничен 5000: 12 000 тяжело для памяти и старта.
    /// </summary>
    public const int Settings = 4;

    /// <summary>
    /// 2 — добавлены необязательные отметки UniversalOk/DiscordOk; старый кэш читается как есть.
    /// 3 — добавлены разобранные поля сервера (Protocol/Name/Server/Port/Tag/Identity/OutboundJson):
    /// новый кэш читается без повторного разбора ссылок, старый — прежним путём.
    /// </summary>
    public const int NodeCache = 3;

    public const int MtProtoCache = 1;
}

/// <summary>Настройки приложения. Хранятся в settings.json и переживают перезапуск.</summary>
internal sealed class AppSettings
{
    /// <summary>
    /// Версия схемы настроек. 0 — файл от версии, где поля ещё не было;
    /// приведение к текущей схеме делает <see cref="SettingsUpgrade"/>.
    /// </summary>
    public int SchemaVersion { get; set; }

    /// <summary>Локальный mixed-порт (HTTP+SOCKS5), который слушает sing-box.</summary>
    public int MixedPort { get; set; } = 20800;

    /// <summary>Порт Clash API для проверки задержек и переключения сервера.</summary>
    public int ApiPort { get; set; } = 20801;

    /// <summary>Серверы с задержкой выше порога не предлагаются к подключению.</summary>
    public int MaxDelayMs { get; set; } = 2500;

    /// <summary>Адрес проверки серверов: важно, чтобы он открывался только через рабочий сервер.</summary>
    public string TestUrl { get; set; } = "https://www.youtube.com/";

    /// <summary>При подключении без выбранного сервера брать самый быстрый рабочий.</summary>
    public bool AutoSelectBest { get; set; } = true;

    /// <summary>При сбое сервера автоматически переключаться на запасной другого камуфляжа.</summary>
    public bool AutoFailover { get; set; } = true;

    /// <summary>
    /// Kill switch: если ядро упало, системный прокси остаётся на закрытом локальном порту.
    /// Сайты не откроются, но трафик не уйдёт напрямую — так блокировка не «протекает».
    /// </summary>
    public bool KillSwitch { get; set; }

    /// <summary>Запускать VieNLink при входе в Windows (ключ HKCU Run).</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>Показывать скорость и суммарный трафик в шапке.</summary>
    public bool ShowTraffic { get; set; } = true;

    /// <summary>Показывать страну и IP выхода после подключения.</summary>
    public bool ShowExitInfo { get; set; } = true;

    /// <summary>Подключаться только к серверам, прошедшим проверку (есть задержка).</summary>
    public bool ConnectOnlyVerified { get; set; } = true;

    /// <summary>Подключаться только к личным подпискам — свой сервер, без публичных серверов.</summary>
    public bool ConnectOnlyMine { get; set; }

    /// <summary>Запускать обход DPI (zapret) при старте приложения.</summary>
    public bool ZapretAutoStart { get; set; }

    /// <summary>Выбранная стратегия zapret; -1 — ещё не подбиралась.</summary>
    public int ZapretPreset { get; set; } = -1;

    /// <summary>Добавлять обход UDP для игр: QUIC, STUN, WireGuard.</summary>
    public bool ZapretGameMode { get; set; }

    /// <summary>Блокировать QUIC (UDP/443) на время работы zapret, чтобы видео шло по TCP.</summary>
    public bool ZapretBlockQuic { get; set; } = true;

    /// <summary>Прописывать системный прокси Windows (WinINET).</summary>
    public bool ApplySystemProxy { get; set; } = true;

    /// <summary>Режим перехвата трафика.</summary>
    public TunnelMode TunnelMode { get; set; } = TunnelMode.SystemProxy;

    /// <summary>Строгая маршрутизация TUN: блокировать попытки обойти туннель.</summary>
    public bool TunStrictRoute { get; set; }

    /// <summary>Какие сайты заворачивать в сервер.</summary>
    public SiteRouteMode SiteRouteMode { get; set; } = SiteRouteMode.All;

    /// <summary>Список доменов (по одному в строке) для режимов «только» и «кроме».</summary>
    public string SiteList { get; set; } = string.Empty;

    /// <summary>Ограничить проксирование списком процессов (старое поле, совместимость).</summary>
    public bool OnlySelectedApps { get; set; }

    /// <summary>Режим приложений: все, только из списка или все, кроме списка.</summary>
    public AppRouteMode AppRouteMode { get; set; } = AppRouteMode.All;

    /// <summary>Имена процессов (по одному в строке), которым разрешён прокси.</summary>
    public string AppList { get; set; } = string.Empty;

    /// <summary>SNI-маскировка: подставляется серверам Reality/TLS без собственного server_name.</summary>
    public string DefaultSni { get; set; } = string.Empty;

    /// <summary>
    /// Фрагментировать TLS-рукопожатие в outbound'ах sing-box (tls.fragment).
    /// Отдельный, «быстрый» обход DPI: без WinDivert и без внешних компонентов,
    /// но действует только на трафик, который идёт через прокси-сервер.
    /// </summary>
    public bool TlsFragment { get; set; }

    /// <summary>Дополнительно фрагментировать TLS-записи (tls.record_fragment).</summary>
    public bool TlsRecordFragment { get; set; }

    /// <summary>Выбранная стратегия собственного обхода DPI (WinDivert), -1 — ещё не подбиралась.</summary>
    public int DesyncStrategy { get; set; } = -1;

    /// <summary>Запускать собственный обход DPI при старте приложения.</summary>
    public bool DesyncAutoStart { get; set; }

    /// <summary>Блокировать QUIC (UDP/443) в своём обходе, чтобы видео YouTube шло по TCP.</summary>
    public bool DesyncBlockQuic { get; set; } = true;

    /// <summary>Выбранный режим TUN-обхода (StreamFragmentMode): разрез SNI, 1 байт, перестановка, мультиразрез.</summary>
    public int TunBypassStrategy { get; set; }

    /// <summary>Блокировать QUIC (UDP/443) в TUN-обходе, чтобы браузер перешёл на TCP.</summary>
    public bool TunBypassBlockQuic { get; set; } = true;

    /// <summary>Запускать TUN-обход при старте приложения (нужны права администратора).</summary>
    public bool TunBypassAutoStart { get; set; }

    /// <summary>Домены, которые не должны идти через прокси (иначе рвётся связь с рабочими сервисами).</summary>
    public string ProxyBypass { get; set; } = "*.deepseek.com;localhost;127.0.0.1;::1";

    /// <summary>Проверять задержки сразу после обновления списков.</summary>
    public bool TestAfterRefresh { get; set; } = true;

    /// <summary>Проверять накопившиеся серверы в простое, когда компьютер не используется.</summary>
    public bool IdleTestEnabled { get; set; } = true;

    /// <summary>Периодически собирать серверы из источников (интервал — <see cref="AutoRefreshMinutes"/>).</summary>
    public bool AutoCollectServers { get; set; } = true;

    /// <summary>Автоматически собирать и проверять MTProto-прокси из каналов Telegram.</summary>
    public bool AutoMtProto { get; set; } = true;

    /// <summary>Автоматически обновлять ядро sing-box при запуске.</summary>
    public bool AutoUpdateCore { get; set; } = true;

    /// <summary>Автоматически проверять обновления самого приложения на GitHub.</summary>
    public bool AutoCheckAppUpdates { get; set; } = true;

    /// <summary>
    /// Вся фоновая автоматика выключена пользователем: проверка серверов, MTProto,
    /// обслуживание, автообновления и автопереключение не запускаются. Туннель
    /// и ручные действия продолжают работать.
    /// </summary>
    public bool AutomationPaused { get; set; }

    /// <summary>Сколько минут бездействия компьютера считать простоем.</summary>
    public int IdleTestMinutes { get; set; } = 5;

    /// <summary>Сколько серверов проверять за один проход в простое.</summary>
    /// <summary>Сколько серверов проверять за один проход в простое. 200 — на ПК партия по 10 слишком мала.</summary>
    public int IdleTestBatch { get; set; } = 200;

    /// <summary>Пауза между проходами проверки в простое, минут.</summary>
    public int IdleTestIntervalMinutes { get; set; } = 10;

    /// <summary>Рабочие серверы и MTProto старше этого возраста перепроверяются в простое, часов.</summary>
    public int IdleRecheckHours { get; set; } = 6;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Обновлять подписки и списки при запуске, если данные устарели.</summary>
    public bool AutoUpdateSubscriptions { get; set; } = true;

    /// <summary>Через сколько часов данные источника считаются устаревшими.</summary>
    public int AutoUpdateHours { get; set; } = 6;

    /// <summary>
    /// Сколько суток хранить сервер, который перестал приходить из источников.
    /// Свежие данные важнее объёма: старые серверы удаляются из кэша.
    /// </summary>
    public int KeepUnseenNodeDays { get; set; } = 7;

    /// <summary>Периодическое обновление списков во время работы, в минутах (0 — выключено).</summary>
    public int AutoRefreshMinutes { get; set; } = 120;

    /// <summary>Искать новые источники серверов автоматически.</summary>
    public bool AutoDiscoverSources { get; set; } = true;

    /// <summary>Как часто искать новые источники, в часах.</summary>
    public int AutoDiscoverHours { get; set; } = 24;

    public DateTime LastAutoRefreshUtc { get; set; }

    public DateTime LastDiscoveryUtc { get; set; }

    /// <summary>Тема оформления: Dark или Light.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Положение главного окна; null — открыть по центру экрана.</summary>
    public double? WindowLeft { get; set; }

    /// <summary>Положение главного окна; null — открыть по центру экрана.</summary>
    public double? WindowTop { get; set; }

    /// <summary>Положение плавающего виджета; null — правый нижний угол рабочей области.</summary>
    public double? WidgetLeft { get; set; }

    /// <summary>Положение плавающего виджета; null — правый нижний угол рабочей области.</summary>
    public double? WidgetTop { get; set; }

    /// <summary>Виджет свёрнут в компактную строку с названием режима.</summary>
    public bool WidgetCollapsed { get; set; }

    /// <summary>
    /// Виджет остаётся поверх всех окон (игр, браузера). Можно отключить кнопкой-закрепом
    /// в самом виджете или галочкой в «Общих»; выбор запоминается.
    /// </summary>
    public bool WidgetTopmost { get; set; } = true;

    /// <summary>Показывать плавающий виджет при запуске (закрытие крестиком выключает).</summary>
    public bool WidgetVisible { get; set; } = true;

    /// <summary>Ограничение числа серверов, попадающих в конфиг ядра.</summary>
    public int MaxNodesInConfig { get; set; } = 400;

    /// <summary>Общий лимит списка серверов: больше не добавляем, пока накопленные не проверены.</summary>
    public int MaxTotalNodes { get; set; } = NodeLimits.DefaultMaxTotal;

    /// <summary>
    /// Копия настроек для скрытой проверки: она меняет режим на системный прокси/всё,
    /// не затрагивая настоящие настройки пользователя.
    /// </summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>Кэш разобранных серверов, чтобы приложение открывалось мгновенно.</summary>
internal sealed class NodeCache
{
    /// <summary>Версия схемы кэша: см. <see cref="SchemaVersions"/>.</summary>
    public int SchemaVersion { get; set; }

    public DateTime SavedAtUtc { get; set; }
    public List<CachedNode> Items { get; set; } = new();
}

internal sealed class CachedNode
{
    public string RawLink { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;

    // Разобранные поля сервера. Старый кэш их не знает — тогда ссылка разбирается заново;
    // новый кэш читается без LinkParser и SHA-256 на каждый сервер, иначе холодный старт
    // Android с 12 000 серверов занимал секунды (его и было видно как «приложение грузится»).
    public string Protocol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string OutboundJson { get; set; } = string.Empty;
    public int? DelayMs { get; set; }
    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>Последняя попытка проверки (в том числе неудачная партия с откатом).</summary>
    public DateTime? TriedAtUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }
    public bool IsMine { get; set; }

    /// <summary>Скорость последнего замера, Мбит/с.</summary>
    public double? SpeedMbps { get; set; }

    /// <summary>Сервер без проверки TLS-сертификата.</summary>
    public bool AllowInsecure { get; set; }
    public bool? YoutubeOk { get; set; }
    public bool? TelegramOk { get; set; }
    public bool? UniversalOk { get; set; }
    public bool? DiscordOk { get; set; }

    /// <summary>Сколько заблокированных доменов открылось через сервер — главный показатель качества.</summary>
    public int OpenDomains { get; set; }
    public NodeStatus Status { get; set; }

    /// <summary>Неудачных проверок подряд: сервер удаляется, когда их накопится 5.</summary>
    public int FailCount { get; set; }
}

/// <summary>Черновик системных настроек прокси до подключения — чтобы вернуть как было.</summary>
internal sealed class SystemProxyBackup
{
    public int ProxyEnable { get; set; }
    public string? ProxyServer { get; set; }
    public string? ProxyOverride { get; set; }
    public string? AutoConfigUrl { get; set; }
    public bool Captured { get; set; }

    /// <summary>Адрес, который мы прописали; нужен, чтобы отличить свои настройки при восстановлении.</summary>
    public string? AppliedServer { get; set; }
}

/// <summary>
/// MTProto-прокси для Telegram. Через него нельзя пустить браузер — sing-box такой протокол
/// не поддерживает; ссылку можно открыть в Telegram, и он добавит прокси сам.
/// </summary>
internal sealed class MtProtoProxy
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Secret { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;

    /// <summary>
    /// Пинг подключения к прокси, как в Telegram. Полное рукопожатие с Telegram
    /// нужно только для проверки «рабочий/нерабочий» и в списке не показывается.
    /// </summary>
    public int? DelayMs { get; set; }
    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>Не проверялся, отвечает или уже признан нерабочим.</summary>
    public MtProtoStatus Status { get; set; } = MtProtoStatus.Unknown;

    /// <summary>Когда прокси перестал отвечать; через 24 часа такие удаляются.</summary>
    public DateTime? DeadAtUtc { get; set; }

    /// <summary>Сколько настоящих проверок подряд прокси не ответил Telegram.</summary>
    public int FailCount { get; set; }

    public string Link => $"tg://proxy?server={Server}&port={Port}&secret={Secret}";

    public string Id => Server.ToLowerInvariant() + ":" + Port + ":" + Secret;

    public string SecretShort => Secret.Length <= 12 ? Secret : Secret[..8] + "…";
}

/// <summary>Состояние MTProto-прокси после проверки соединения.</summary>
internal enum MtProtoStatus
{
    Unknown,
    Working,
    Dead,
}

/// <summary>Сроки хранения нерабочих MTProto-прокси.</summary>
internal static class MtProtoRetention
{
    /// <summary>Нерабочий прокси удаляется через сутки: за это время он ещё может ожить.</summary>
    public const int DeadHours = 24;

    /// <summary>Сколько нерабочих MTProto-прокси держим максимум.</summary>
    public const int MaxDead = 500;

    public static bool IsExpired(MtProtoProxy proxy, DateTime nowUtc) =>
        proxy.Status == MtProtoStatus.Dead &&
        proxy.DeadAtUtc is { } dead &&
        nowUtc - dead > TimeSpan.FromHours(DeadHours);

    /// <summary>Оставляет не больше max самых свежих нерабочих прокси; возвращает число удалённых.</summary>
    public static int TrimDead(List<MtProtoProxy> proxies, int max = MaxDead)
    {
        var dead = proxies
            .Where(p => p.Status == MtProtoStatus.Dead)
            .OrderByDescending(p => p.DeadAtUtc ?? DateTime.MinValue)
            .ToList();
        if (dead.Count <= max)
        {
            return 0;
        }

        var drop = dead.Skip(max).ToHashSet();
        proxies.RemoveAll(p => drop.Contains(p));
        return drop.Count;
    }
}

/// <summary>
/// Автономное обслуживание MTProto: сбор каналов через RSS, проверка рабочих и редкая
/// проверка нерабочих. Интервалы не настраиваются — приложение работает без ручных кнопок.
/// </summary>
internal static class MtProtoSchedule
{
    /// <summary>Как часто читаем каналы через RSS.</summary>
    public const int CollectMinutes = 10;

    /// <summary>Если рабочих прокси меньше пяти, каналы читаем чаще.</summary>
    public const int CollectMinutesWhenFew = 5;

    /// <summary>Рабочие прокси проверяем чаще, чтобы быстро замечать умершие.</summary>
    public const int WorkingRecheckMinutes = 10;

    /// <summary>Нерабочие проверяем изредка — вдруг ожил.</summary>
    public const int DeadRecheckHours = 6;

    /// <summary>Только что упавший рабочий прокси перепроверяем быстро: возможно, была заминка.</summary>
    public const int QuickRecheckMinutes = 20;

    /// <summary>Сколько рабочих прокси проверяем за один проход.</summary>
    public const int WorkingBatch = 20;

    /// <summary>Сколько нерабочих прокси проверяем за один проход.</summary>
    public const int DeadBatch = 20;

    /// <summary>Сколько рабочих MTProto-прокси приложение старается держать наготове.</summary>
    public const int MinWorking = 5;
}

internal sealed class MtProtoCache
{
    /// <summary>Версия схемы кэша: см. <see cref="SchemaVersions"/>.</summary>
    public int SchemaVersion { get; set; }

    public DateTime SavedAtUtc { get; set; }
    public List<MtProtoProxy> Items { get; set; } = new();
}

