using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace VieNLink;

/// <summary>Состояние узла после проверок.</summary>
internal enum NodeStatus
{
    /// <summary>Ещё не проверялся.</summary>
    Unknown = 0,

    /// <summary>
    /// Не открыл ни один проверочный сервис: непроверенный узел — сразу, уже работавший —
    /// после <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд.
    /// </summary>
    Dead = 1,

    /// <summary>Открывает хотя бы один проверочный сервис.</summary>
    Working = 2,
}

/// <summary>Один узел (сервер) из публичного списка или подписки.</summary>
internal sealed class ProxyNode
{
    public string RawLink { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;

    /// <summary>Узел пришёл из личной подписки — показывается отдельно и пробуется первым.</summary>
    public bool IsMine { get; set; }

    /// <summary>Задержка последней проверки в миллисекундах; null — ещё не проверялся.</summary>
    public int? DelayMs { get; set; }

    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>
    /// Когда узел последний раз пытались проверить. Нужна, чтобы партия, где ничего
    /// не открылось (откат), не проверялась заново теми же узлами по кругу.
    /// </summary>
    public DateTime? TriedAtUtc { get; set; }

    /// <summary>
    /// Когда узел последний раз встречался в источниках. Узлы, которые давно
    /// не приходят из списков, удаляются — в кэше остаются только свежие данные.
    /// </summary>
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Скорость последнего замера, Мбит/с; null — не замерялась. Задержка не показывает пропускную способность.</summary>
    public double? SpeedMbps { get; set; }

    /// <summary>Узел просит не проверять TLS-сертификат (insecure): трафик виден посреднику, выбираем его последним.</summary>
    public bool AllowInsecure { get; set; }

    // Результаты проверки по сервисам: null — не проверялся.
    public bool? YoutubeOk { get; set; }

    public bool? TelegramOk { get; set; }

    /// <summary>Универсальный доступ (Cloudflare): главный признак пригодности узла.</summary>
    public bool? UniversalOk { get; set; }

    public bool? DiscordOk { get; set; }

    /// <summary>Сколько заблокированных доменов открылось через узел — главный показатель качества.</summary>
    public int OpenDomains { get; set; }

    public NodeStatus Status { get; set; } = NodeStatus.Unknown;

    /// <summary>
    /// Сколько проверок подряд узел не открыл ни один сервис. Успешная проверка
    /// сбрасывает счётчик; после <see cref="NodeLimits.RemoveAfterFailures"/> узел удаляется.
    /// </summary>
    public int FailCount { get; set; }

    /// <summary>
    /// Ключ повтора: один физический сервер = один узел. Разные ссылки на тот же
    /// хост:порт (другой UUID, имя или камуфляж) склеиваются в одну строку.
    /// </summary>
    [JsonIgnore]
    public string EndpointKey =>
        Server.Trim().TrimEnd('.').ToLowerInvariant() + ":" +
        Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Готовый outbound-объект sing-box. Пересоздаётся при загрузке из кэша.</summary>
    [JsonIgnore]
    public string OutboundJson { get; set; } = string.Empty;

    /// <summary>Стабильный идентификатор: одинаковые узлы из разных источников склеиваются.</summary>
    [JsonIgnore]
    public string Identity { get; set; } = string.Empty;

    public string Id => string.IsNullOrEmpty(Identity) ? ComputeId() : Identity;

    /// <summary>Стабильный идентификатор: одинаковые узлы из разных источников склеиваются.</summary>
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
/// «одна строка на хост:порт» выбрасывало рабочие узлы с общих CDN: у Cloudflare под
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
        // разные узлы, но хранить десятки вариантов одного адреса тоже незачем.
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
                Absorb(keep[0], extra);
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
         NodeRanking.Score(node),
         (node.LastSeenUtc ?? DateTime.MinValue).Ticks);

    /// <summary>Какой из двух дублей оставить. Рабочий узел важнее красивого, но непроверенного.</summary>
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

        var candidateScore = NodeRanking.Score(candidate);
        var currentScore = NodeRanking.Score(current);
        if (candidateScore != currentScore)
        {
            return candidateScore > currentScore;
        }

        return (candidate.LastSeenUtc ?? DateTime.MinValue) > (current.LastSeenUtc ?? DateTime.MinValue);
    }

    /// <summary>Переносит на оставшийся узел всё полезное от дубля: проверки, метку «моя», источники.</summary>
    private static void Absorb(ProxyNode target, ProxyNode source)
    {
        if (target.DelayMs is null && source.DelayMs is not null)
        {
            target.DelayMs = source.DelayMs;
            target.CheckedAtUtc = source.CheckedAtUtc;
            target.YoutubeOk = source.YoutubeOk;
            target.TelegramOk = source.TelegramOk;
            target.UniversalOk = source.UniversalOk;
            target.DiscordOk = source.DiscordOk;
            target.Status = source.Status;
        }
        else
        {
            // Доступ по сервисам объединяем: у дублей он мог проверяться в разное время.
            target.YoutubeOk = MergeAccess(target.YoutubeOk, source.YoutubeOk);
            target.TelegramOk = MergeAccess(target.TelegramOk, source.TelegramOk);
            target.UniversalOk = MergeAccess(target.UniversalOk, source.UniversalOk);
            target.DiscordOk = MergeAccess(target.DiscordOk, source.DiscordOk);
        }

        if (target.SpeedMbps is null && source.SpeedMbps is not null)
        {
            target.SpeedMbps = source.SpeedMbps;
        }

        target.IsMine |= source.IsMine;
        target.FailCount = Math.Max(target.FailCount, source.FailCount);
        target.OpenDomains = Math.Max(target.OpenDomains, source.OpenDomains);
        if (source.TriedAtUtc is { } tried &&
            (target.TriedAtUtc is null || tried > target.TriedAtUtc))
        {
            target.TriedAtUtc = tried;
        }

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

    /// <summary>Названия источников объединяются без повторов: видно, что узел популярен.</summary>
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
/// Учёт неудачных проверок узла. Узел, который уже был рабочим, не удаляется с первого
/// провала: счётчик растёт, и только после <see cref="NodeLimits.RemoveAfterFailures"/>
/// провалов подряд такой узел убирается из списка. Непроверенные узлы удаляются сразу —
/// их держать незачем.
/// </summary>
internal static class NodeHealth
{
    /// <summary>Отмечает неудачную проверку. true — узел пора удалить.</summary>
    public static bool RegisterFailure(ProxyNode node)
    {
        node.CheckedAtUtc = DateTime.UtcNow;
        node.DelayMs = null;
        node.YoutubeOk = false;
        node.TelegramOk = false;
        node.UniversalOk = false;
        node.DiscordOk = false;
        node.OpenDomains = 0;

        // Узел ещё ни разу не открывал сайты — не храним его вовсе.
        if (node.Status != NodeStatus.Working)
        {
            node.FailCount = Math.Max(1, node.FailCount + 1);
            node.Status = NodeStatus.Dead;
            return true;
        }

        node.FailCount = Math.Max(1, node.FailCount + 1);
        if (node.FailCount >= NodeLimits.RemoveAfterFailures)
        {
            node.Status = NodeStatus.Dead;
            return true;
        }

        // Остаётся в «Рабочих» без задержки: при следующей проверке попробуем снова.
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
/// Жёсткий лимит списка узлов. Когда список заполнен, новые узлы не добавляются
/// и источники не ищутся, пока накопленные не проверены: нерабочие удаляются,
/// место освобождается и загрузка продолжается.
/// </summary>
internal static class NodeLimits
{
    public const int DefaultMaxTotal = 12000;

    /// <summary>
    /// Сколько вариантов одного сервера:порта оставлять. У бесплатных CDN под одним
    /// адресом живут разные аккаунты, поэтому один вариант — слишком грубо.
    /// </summary>
    public const int MaxPerEndpoint = 4;

    /// <summary>
    /// После скольких неудачных проверок подряд убирается узел, который раньше работал.
    /// Непроверенные узлы удаляются с первого провала — держать их в списке незачем.
    /// </summary>
    public const int RemoveAfterFailures = 5;

    /// <summary>
    /// Оставляет не больше <paramref name="max"/> узлов: сначала личные, затем проверенные
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
            .ThenByDescending(NodeRanking.Score)
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

    /// <summary>Одна ссылка на узел (vless://, vmess://…), вставленная вручную.</summary>
    SingleLink,
}

/// <summary>Источник узлов: raw-список на GitHub, подписка или публичный канал Telegram.</summary>
internal sealed class SourceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public SourceKind Kind { get; set; } = SourceKind.Auto;
    public int LastNodeCount { get; set; }
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

/// <summary>Что отправлять через узел: всё, только список сайтов или всё, кроме списка.</summary>
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
    public const int Settings = 1;

    /// <summary>2 — добавлены необязательные отметки UniversalOk/DiscordOk; старый кэш читается как есть.</summary>
    public const int NodeCache = 2;

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

    /// <summary>Порт Clash API для проверки задержек и переключения узла.</summary>
    public int ApiPort { get; set; } = 20801;

    /// <summary>Узлы с задержкой выше порога не предлагаются к подключению.</summary>
    public int MaxDelayMs { get; set; } = 2500;

    /// <summary>Адрес проверки узлов: важно, чтобы он открывался только через рабочий узел.</summary>
    public string TestUrl { get; set; } = "https://www.youtube.com/";

    /// <summary>При подключении без выбранного узла брать самый быстрый рабочий.</summary>
    public bool AutoSelectBest { get; set; } = true;

    /// <summary>При сбое узла автоматически переключаться на запасной другого камуфляжа.</summary>
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

    /// <summary>Подключаться только к узлам, прошедшим проверку (есть задержка).</summary>
    public bool ConnectOnlyVerified { get; set; } = true;

    /// <summary>Подключаться только к личным подпискам — свой сервер, без публичных узлов.</summary>
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

    /// <summary>Какие сайты заворачивать в узел.</summary>
    public SiteRouteMode SiteRouteMode { get; set; } = SiteRouteMode.All;

    /// <summary>Список доменов (по одному в строке) для режимов «только» и «кроме».</summary>
    public string SiteList { get; set; } = string.Empty;

    /// <summary>Ограничить проксирование списком процессов (старое поле, совместимость).</summary>
    public bool OnlySelectedApps { get; set; }

    /// <summary>Режим приложений: все, только из списка или все, кроме списка.</summary>
    public AppRouteMode AppRouteMode { get; set; } = AppRouteMode.All;

    /// <summary>Имена процессов (по одному в строке), которым разрешён прокси.</summary>
    public string AppList { get; set; } = string.Empty;

    /// <summary>SNI-маскировка: подставляется узлам Reality/TLS без собственного server_name.</summary>
    public string DefaultSni { get; set; } = string.Empty;

    /// <summary>
    /// Фрагментировать TLS-рукопожатие в outbound'ах sing-box (tls.fragment).
    /// Отдельный, «быстрый» обход DPI: без WinDivert и без внешних компонентов,
    /// но действует только на трафик, который идёт через прокси-узел.
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

    /// <summary>Проверять накопившиеся узлы в простое, когда компьютер не используется.</summary>
    public bool IdleTestEnabled { get; set; } = true;

    /// <summary>Сколько минут бездействия компьютера считать простоем.</summary>
    public int IdleTestMinutes { get; set; } = 5;

    /// <summary>Сколько узлов проверять за один проход в простое.</summary>
    public int IdleTestBatch { get; set; } = 40;

    /// <summary>Пауза между проходами проверки в простое, минут.</summary>
    public int IdleTestIntervalMinutes { get; set; } = 10;

    /// <summary>Рабочие узлы и MTProto старше этого возраста перепроверяются в простое, часов.</summary>
    public int IdleRecheckHours { get; set; } = 6;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Обновлять подписки и списки при запуске, если данные устарели.</summary>
    public bool AutoUpdateSubscriptions { get; set; } = true;

    /// <summary>Через сколько часов данные источника считаются устаревшими.</summary>
    public int AutoUpdateHours { get; set; } = 6;

    /// <summary>
    /// Сколько суток хранить узел, который перестал приходить из источников.
    /// Свежие данные важнее объёма: старые узлы удаляются из кэша.
    /// </summary>
    public int KeepUnseenNodeDays { get; set; } = 7;

    /// <summary>Периодическое обновление списков во время работы, в минутах (0 — выключено).</summary>
    public int AutoRefreshMinutes { get; set; } = 120;

    /// <summary>Искать новые источники узлов автоматически.</summary>
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

    /// <summary>Ограничение числа узлов, попадающих в конфиг ядра.</summary>
    public int MaxNodesInConfig { get; set; } = 400;

    /// <summary>Общий лимит списка узлов: больше не добавляем, пока накопленные не проверены.</summary>
    public int MaxTotalNodes { get; set; } = NodeLimits.DefaultMaxTotal;

    /// <summary>
    /// Копия настроек для скрытой проверки: она меняет режим на системный прокси/всё,
    /// не затрагивая настоящие настройки пользователя.
    /// </summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

/// <summary>Кэш разобранных узлов, чтобы приложение открывалось мгновенно.</summary>
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
    public int? DelayMs { get; set; }
    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>Последняя попытка проверки (в том числе неудачная партия с откатом).</summary>
    public DateTime? TriedAtUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }
    public bool IsMine { get; set; }

    /// <summary>Скорость последнего замера, Мбит/с.</summary>
    public double? SpeedMbps { get; set; }

    /// <summary>Узел без проверки TLS-сертификата.</summary>
    public bool AllowInsecure { get; set; }
    public bool? YoutubeOk { get; set; }
    public bool? TelegramOk { get; set; }
    public bool? UniversalOk { get; set; }
    public bool? DiscordOk { get; set; }

    /// <summary>Сколько заблокированных доменов открылось через узел — главный показатель качества.</summary>
    public int OpenDomains { get; set; }
    public NodeStatus Status { get; set; }

    /// <summary>Неудачных проверок подряд: узел удаляется, когда их накопится 5.</summary>
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
    public int? DelayMs { get; set; }
    public DateTime? CheckedAtUtc { get; set; }

    /// <summary>Не проверялся, отвечает или уже признан нерабочим.</summary>
    public MtProtoStatus Status { get; set; } = MtProtoStatus.Unknown;

    /// <summary>Когда прокси перестал отвечать; через 24 часа такие удаляются.</summary>
    public DateTime? DeadAtUtc { get; set; }

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

    /// <summary>Рабочие прокси проверяем чаще, чтобы быстро замечать умершие.</summary>
    public const int WorkingRecheckMinutes = 10;

    /// <summary>Нерабочие проверяем изредка — вдруг ожил.</summary>
    public const int DeadRecheckHours = 6;

    /// <summary>Сколько рабочих прокси проверяем за один проход.</summary>
    public const int WorkingBatch = 20;

    /// <summary>Сколько нерабочих прокси проверяем за один проход.</summary>
    public const int DeadBatch = 20;
}

internal sealed class MtProtoCache
{
    /// <summary>Версия схемы кэша: см. <see cref="SchemaVersions"/>.</summary>
    public int SchemaVersion { get; set; }

    public DateTime SavedAtUtc { get; set; }
    public List<MtProtoProxy> Items { get; set; } = new();
}

