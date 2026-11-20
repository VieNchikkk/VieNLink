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
}

internal sealed class CollectResult
{
    public List<ProxyNode> Nodes { get; init; } = new();
    public List<MtProtoProxy> MtProto { get; init; } = new();
}

/// <summary>
/// Загрузка публичных списков узлов: raw-файлы GitHub, base64-подписки и публичные каналы Telegram.
/// Сайты и каналы часто недоступны напрямую, поэтому ошибка источника не роняет остальные.
/// </summary>
internal static class NodeSourceService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient HttpDirect = CreateClient(useProxy: false);

    public static List<SourceDefinition> DefaultSources() => new()
    {
        new() { Name = "v2rayNG-Config (GitHub)", Url = "https://raw.githubusercontent.com/ALIILAPRO/v2rayNG-Config/main/server.txt", Kind = SourceKind.PlainList },
        new() { Name = "V2RayAggregator (GitHub)", Url = "https://raw.githubusercontent.com/mahdibland/V2RayAggregator/master/sub/sub_merge.txt", Kind = SourceKind.PlainList },
        new() { Name = "ShadowsocksAggregator (GitHub)", Url = "https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/Eternity.txt", Kind = SourceKind.PlainList },
        new() { Name = "get_subscribe (GitHub)", Url = "https://raw.githubusercontent.com/ermaozi/get_subscribe/main/subscribe/v2ray.txt", Kind = SourceKind.PlainList },
        new() { Name = "Free-servers (подписка)", Url = "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub", Kind = SourceKind.Subscription },
        new() { Name = "freenode (подписка)", Url = "https://raw.githubusercontent.com/ripaojiedian/freenode/main/sub", Kind = SourceKind.Subscription },

        // Небольшие списки с более высокой долей живых узлов.
        new() { Name = "openproxylist V2RAY (GitHub)", Url = "https://raw.githubusercontent.com/roosterkid/openproxylist/main/V2RAY_RAW.txt", Kind = SourceKind.PlainList },
        new() { Name = "Kwinshadow mix (GitHub)", Url = "https://raw.githubusercontent.com/Kwinshadow/TelegramV2rayCollector/main/sublinks/mix.txt", Kind = SourceKind.PlainList },

        // Крупные агрегаторы: узлов много, живых мало — по умолчанию выключены, чтобы не тормозить обновление.
        new() { Name = "barry-far vless (GitHub)", Url = "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/vless.txt", Kind = SourceKind.PlainList, Enabled = false },
        new() { Name = "MhdiTaheri mix (GitHub)", Url = "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/mix", Kind = SourceKind.PlainList, Enabled = false },
        new() { Name = "Mineral nodes (GitHub)", Url = "https://raw.githubusercontent.com/LalatinaHub/Mineral/master/result/nodes", Kind = SourceKind.PlainList, Enabled = false },

        // Каналы Telegram открываются только при уже работающем обходе блокировок.
        // @proxy_mtproto включён: после первого успешного подключения приложение само прочитает его.
        new() { Name = "Telegram: @proxy_mtproto", Url = "https://t.me/s/proxy_mtproto", Kind = SourceKind.Telegram },
        new() { Name = "Telegram: @ProxyMTProto", Url = "https://t.me/s/ProxyMTProto", Kind = SourceKind.Telegram },
        new() { Name = "Telegram: @MTProtoProxies", Url = "https://t.me/s/MTProtoProxies", Kind = SourceKind.Telegram },
        new() { Name = "Telegram: @v2rayng_config", Url = "https://t.me/s/v2rayng_config", Kind = SourceKind.Telegram, Enabled = false },
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

        if (!fromPlain)
        {
            // Зашифрованный файл прочитан: открытых копий от старых версий быть не должно.
            DeletePlainSources();
        }

        // Новые источники из обновлений добавляются, но пользовательские настройки не трогаются.
        foreach (var defaultSource in DefaultSources())
        {
            if (saved.All(s => !s.Url.Equals(defaultSource.Url, StringComparison.OrdinalIgnoreCase)))
            {
                defaultSource.Enabled = false;
                saved.Add(defaultSource);
            }
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
        Action<FetchOutcome>? onSource = null)
    {
        var enabled = sources.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)).ToList();
        var batches = await Task.WhenAll(enabled.Select(async source =>
        {
            FetchOutcome outcome;
            try
            {
                outcome = await FetchAsync(source, cancellationToken).ConfigureAwait(false);
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
            source.LastNodeCount = outcome.Nodes.Count;
            progress?.Report(outcome.Error is null
                ? $"{source.Name}: {outcome.Nodes.Count} узлов"
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
                // Узел пришёл из источника сейчас — значит, он свежий.
                node.LastSeenUtc = now;
                if (unique.TryAdd(node.Id, node))
                {
                    continue;
                }

                // Дубликат из другого источника: оставляем первый, но помним, что узел популярен.
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
                return new FetchOutcome { Source = source, Error = "Не удалось разобрать ссылку на узел" };
            }

            foreach (var node in imported)
            {
                node.IsMine = source.IsMine;
            }

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

        // Заглушки вида «Приложение не поддерживается» и группы с 127.0.0.1:1 — не узлы.
        var nodes = parsed.Where(node => !IsPlaceholderNode(node)).ToList();
        foreach (var node in nodes)
        {
            node.IsMine = source.IsMine;
        }

        string? error = null;
        if (nodes.Count == 0 && parsed.Count > 0)
        {
            error = "Провайдер отдаёт только заглушки — узлы доступны лишь в его клиенте (Happ). " +
                    "Нужна универсальная подписка или ссылки на серверы.";
        }

        var mtproto = MtProtoService.Extract(text, source.Name);
        return new FetchOutcome { Source = source, Nodes = nodes, MtProto = mtproto, Error = error };
    }

    /// <summary>Служебные «узлы»-маркеры: группы и сообщения провайдера.</summary>
    private static bool IsPlaceholderNode(ProxyNode node) =>
        node.Port == 1 && (node.Server is "0.0.0.0" or "127.0.0.1" or "::1" or "localhost");

    /// <summary>Сырой ответ источника: используется и для узлов, и для MTProto-прокси.</summary>
    public static async Task<(string Text, string? SubscriptionInfo)> FetchRawAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        // Страницы t.me маленькие: либо открываются быстро, либо канал недоступен.
        var timeout = TimeSpan.FromSeconds(source.Kind == SourceKind.Telegram ? 15 : 40);
        try
        {
            return await FetchOnceAsync(Http, source, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            // Системный прокси мог указывать на мёртвый узел: прямой запрос часто проходит.
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
                ? " Канал t.me открывается только через работающий обход — подключитесь к узлу и повторите."
                : string.Empty;
            throw new TimeoutException($"Источник не ответил за {timeout.TotalSeconds:0} с.{hint}");
        }
    }

    public static List<string> ExtractLinks(string text) => SubscriptionParser.ExtractLinks(text);

    /// <summary>Кэш узлов, чтобы список не пропадал между запусками.</summary>
    public static void SaveCache(IEnumerable<ProxyNode> nodes)
    {
        // Даже если узел пришёл из старого кэша без отметки, при сохранении она появится.
        var now = DateTime.UtcNow;
        JsonFileStore.Write(AppPaths.NodesPath, new NodeCache
        {
            SavedAtUtc = now,
            Items = nodes.Select(n => new CachedNode
            {
                RawLink = n.RawLink,
                SourceName = n.SourceName,
                DelayMs = n.DelayMs,
                CheckedAtUtc = n.CheckedAtUtc,
                LastSeenUtc = n.LastSeenUtc ?? now,
                IsMine = n.IsMine,
                SpeedMbps = n.SpeedMbps,
                AllowInsecure = n.AllowInsecure,
                YoutubeOk = n.YoutubeOk,
                TelegramOk = n.TelegramOk,
                Status = n.Status,
                FailCount = n.FailCount,
            }).ToList(),
        });
    }

    public static List<ProxyNode> LoadCache() => LoadCache(out _, out _);

    public static List<ProxyNode> LoadCache(out int dropped) => LoadCache(out dropped, out _);

    /// <summary>
    /// Читает кэш и оставляет только свежие узлы: не встречавшиеся в источниках дольше
    /// <paramref name="freshAfterUtc"/> выбрасываются, повторы «один сервер:порт» склеиваются.
    /// </summary>
    public static List<ProxyNode> LoadCache(out int dropped, out int duplicates, DateTime? freshAfterUtc = null)
    {
        dropped = 0;
        duplicates = 0;
        var cache = JsonFileStore.Read<NodeCache>(AppPaths.NodesPath);
        if (cache is null)
        {
            return new List<ProxyNode>();
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

            // Старые версии хранили нерабочие узлы в статусе, номер которого совпал
            // с новым Working. Рабочий узел обязан иметь успешную проверку.
            if (item.Status == NodeStatus.Working &&
                item.DelayMs is null && item.YoutubeOk != true && item.TelegramOk != true)
            {
                dropped++;
                continue;
            }

            // В старом кэше нет отметки LastSeen: возраст считаем от времени сохранения,
            // иначе первый запуск после обновления выбросил бы разом все узлы.
            var lastSeen = item.LastSeenUtc ?? cacheSavedAt;
            if (freshAfterUtc is { } cutoff && lastSeen < cutoff)
            {
                dropped++;
                continue;
            }

            // Обычные ссылки разбирает LinkParser; sing-box JSON-узлы — SubscriptionParser.
            var node = LinkParser.Parse(item.RawLink, item.SourceName)
                       ?? SubscriptionParser.Parse(item.RawLink, item.SourceName).FirstOrDefault();
            if (node is null)
            {
                dropped++;
                continue;
            }

            node.DelayMs = item.DelayMs;
            node.CheckedAtUtc = item.CheckedAtUtc;
            node.LastSeenUtc = lastSeen;
            node.IsMine = item.IsMine;
            node.SpeedMbps = item.SpeedMbps;
            node.AllowInsecure = item.AllowInsecure || node.AllowInsecure;
            node.YoutubeOk = item.YoutubeOk;
            node.TelegramOk = item.TelegramOk;
            node.Status = item.Status;
            node.FailCount = item.FailCount;
            nodes.Add(node);
        }

        var collapsed = NodeDedup.Collapse(nodes);
        duplicates = nodes.Count - collapsed.Count;
        return collapsed;
    }

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
