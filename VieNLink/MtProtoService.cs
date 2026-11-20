using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VieNLink;

/// <summary>
/// MTProto-прокси для Telegram: собираем ссылки tg://proxy из каналов и списков,
/// проверяем доступность TCP и отдаём ссылку, которую Telegram открывает сам.
/// Через такие прокси браузер пустить нельзя — это протокол самого Telegram.
/// </summary>
internal static class MtProtoService
{
    private static readonly Regex ProxyRegex = new(
        @"(?:tg://proxy\?|https?://t\.me/proxy\?)[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Посты вида «IP/Server: … Port: … Secret: …» — так публикуют прокси многие каналы.
    /// Переход между полями ограничен несколькими строками и 300 символами: с прежним
    /// ленивым `.*?` на многомегабайтных списках регекс уходил в катастрофический откат
    /// и намертво подвешивал окно.
    /// </summary>
    private static readonly Regex BlockProxyRegex = new(
        @"(?i)\b(?:IP|Server|Host):\s*([^\s<>]+)[^\n]{0,300}(?:\n[^\n]{0,300}){0,8}?\bPort:\s*(\d{1,5})[^\n]{0,300}(?:\n[^\n]{0,300}){0,8}?\bSecret:\s*([A-Za-z0-9_\-+/=]+)",
        RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"(?s)<[^>]+>", RegexOptions.Compiled);

    private static readonly string[] RssMirrors =
    {
        "https://tg.i-c-a.su/rss/{0}",
        "https://rsshub.rssforever.com/telegram/channel/{0}",
        "https://rsshub.ktachibana.party/telegram/channel/{0}",
        "https://rsshub.liumingye.cn/telegram/channel/{0}",
    };

    public static List<MtProtoProxy> Extract(string text, string sourceName)
    {
        var result = new List<MtProtoProxy>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ProxyRegex.Matches(text))
        {
            var link = WebUtility.HtmlDecode(match.Value).TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'', '|');
            var queryIndex = link.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex < 0)
            {
                continue;
            }

            var query = ParseQuery(link[(queryIndex + 1)..]);
            if (!query.TryGetValue("server", out var server) ||
                !query.TryGetValue("port", out var portText) ||
                !query.TryGetValue("secret", out var secret))
            {
                continue;
            }

            if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(server) || server.Contains('/') || server.Contains(' ') || string.IsNullOrWhiteSpace(secret))
            {
                continue;
            }

            Add(result, seen, server.Trim(), port, secret.Trim(), sourceName);
        }

        // Посты в виде «IP: … Port: … Secret: …» (часто приходят через RSS-зеркала).
        // Сначала ищем слово «Secret» в исходном тексте: на многомегабайтных списках узлов
        // его нет, и тогда не тратим память на копию текста без HTML-тегов.
        if (text.IndexOf("Secret", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return result;
        }

        var plain = TagRegex.Replace(WebUtility.HtmlDecode(text), " ");

        // Если слова «Secret:» в тексте нет, блоков быть не может — не запускаем регекс
        // на многомегабайтных списках узлов вообще.
        if (plain.IndexOf("Secret:", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return result;
        }

        foreach (Match match in BlockProxyRegex.Matches(plain))
        {
            var server = match.Groups[1].Value.Trim();
            var secret = match.Groups[3].Value.Trim();
            if (!int.TryParse(match.Groups[2].Value, out var port) || port is <= 0 or > 65535)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(server) || server.Contains('/') || server.Contains(' ') || string.IsNullOrWhiteSpace(secret))
            {
                continue;
            }

            if (IsPlaceholderServer(server))
            {
                continue;
            }

            Add(result, seen, server, port, secret, sourceName);
        }

        return result;
    }

    /// <summary>Каналы публикуют заглушки вроде «Server: Unknown» или «— YOUR PROXY HERE —».</summary>
    private static bool IsPlaceholderServer(string server) =>
        server.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
        server.Contains("your", StringComparison.OrdinalIgnoreCase) ||
        server.Contains("here", StringComparison.OrdinalIgnoreCase) ||
        server.Trim('—', '-', '–').Length == 0;

    private static void Add(
        List<MtProtoProxy> result,
        HashSet<string> seen,
        string server,
        int port,
        string secret,
        string sourceName)
    {
        // У некоторых каналов сервер записан как FQDN с точкой на конце.
        server = server.TrimEnd('.');
        if (server.Length == 0)
        {
            return;
        }

        var proxy = new MtProtoProxy
        {
            Server = server,
            Port = port,
            Secret = secret,
            SourceName = sourceName,
        };
        if (seen.Add(proxy.Id))
        {
            result.Add(proxy);
        }
    }

    public static async Task<List<MtProtoProxy>> FetchAsync(
        SourceDefinition source,
        CancellationToken cancellationToken,
        bool forceRss = false)
    {
        if (source.Kind == SourceKind.Telegram)
        {
            // 1. Обычная веб-версия канала (если t.me доступен).
            if (!forceRss)
            {
                try
                {
                    var (text, _) = await NodeSourceService.FetchRawAsync(source, cancellationToken)
                        .ConfigureAwait(false);
                    // Разбор больших страниц — не в UI-потоке: иначе окно «зависает» на регексах.
                    var proxies = Extract(TelegramText.Extract(text), source.Name);
                    if (proxies.Count > 0)
                    {
                        return proxies;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Write("mtproto", $"{source.Name}: t.me недоступен, пробую RSS-зеркала", exception);
                }
            }

            // 2. t.me заблокирован или попросили принудительно — читаем через RSS-зеркала.
            return await FetchFromRssAsync(source, cancellationToken).ConfigureAwait(false);
        }

        var (rawText, _) = await NodeSourceService.FetchRawAsync(source, cancellationToken).ConfigureAwait(false);
        return Extract(rawText, source.Name);
    }

    /// <summary>Резервный способ: RSS-зеркала читают канал на своей стороне, VPN не нужен.</summary>
    private static async Task<List<MtProtoProxy>> FetchFromRssAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        var channel = ChannelName(source.Url);
        if (channel.Length == 0)
        {
            return new List<MtProtoProxy>();
        }

        foreach (var mirror in RssMirrors)
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
                var (raw, _) = await NodeSourceService.FetchRawAsync(mirrorSource, cancellationToken)
                    .ConfigureAwait(false);
                var proxies = Extract(TelegramText.Extract(WebUtility.HtmlDecode(raw)), source.Name);
                if (proxies.Count > 0)
                {
                    AppLog.Write("mtproto", $"{source.Name}: получено через RSS ({new Uri(mirrorSource.Url).Host}) — {proxies.Count}");
                    return proxies;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write("mtproto", $"{source.Name}: RSS-зеркало {mirror} не ответило", exception);
            }
        }

        return new List<MtProtoProxy>();
    }

    /// <summary>Имя канала из https://t.me/s/name или https://t.me/name.</summary>
    internal static string ChannelName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        return parts[0].Equals("s", StringComparison.OrdinalIgnoreCase) && parts.Length > 1
            ? parts[1]
            : parts[0];
    }

    /// <summary>Проверка TCP-доступности: MTProto-хендшейк без секрета не проверить, но мёртвые серверы отсеиваются.</summary>
    public static async Task TestAsync(
        IReadOnlyList<MtProtoProxy> proxies,
        IProgress<string>? progress,
        Action<MtProtoProxy>? onTested,
        CancellationToken cancellationToken,
        int concurrency = 32,
        int timeoutMs = 3000)
    {
        if (proxies.Count == 0)
        {
            return;
        }

        var done = 0;
        using var limiter = new SemaphoreSlim(concurrency);
        var tasks = proxies.Select(async proxy =>
        {
            await limiter.WaitAsync(cancellationToken);
            try
            {
                var alive = false;
                try
                {
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(timeoutMs);
                    var stopwatch = Stopwatch.StartNew();
                    await client.ConnectAsync(proxy.Server, proxy.Port, timeout.Token);
                    proxy.DelayMs = (int)stopwatch.ElapsedMilliseconds;
                    alive = true;
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    proxy.DelayMs = null;
                }
                finally
                {
                    if (alive)
                    {
                        proxy.Status = MtProtoStatus.Working;
                        proxy.DeadAtUtc = null;
                    }
                    else
                    {
                        proxy.Status = MtProtoStatus.Dead;
                        proxy.DeadAtUtc ??= DateTime.UtcNow;
                    }
                }
            }
            finally
            {
                proxy.CheckedAtUtc = DateTime.UtcNow;
                limiter.Release();
                onTested?.Invoke(proxy);
                var finished = Interlocked.Increment(ref done);
                if (finished % 20 == 0 || finished == proxies.Count)
                {
                    progress?.Report($"Проверено {finished} из {proxies.Count}");
                }
            }
        });

        await Task.WhenAll(tasks);
    }

    public static void SaveCache(IEnumerable<MtProtoProxy> proxies) =>
        JsonFileStore.Write(AppPaths.MtProtoPath, new MtProtoCache
        {
            SchemaVersion = SchemaVersions.MtProtoCache,
            SavedAtUtc = DateTime.UtcNow,
            Items = proxies.ToList(),
        });

    public static List<MtProtoProxy> LoadCache()
    {
        var cache = JsonFileStore.Read<MtProtoCache>(AppPaths.MtProtoPath);
        if (cache is null)
        {
            return new List<MtProtoProxy>();
        }

        if (cache.SchemaVersion > SchemaVersions.MtProtoCache)
        {
            AppLog.Write("mtproto",
                $"Кэш MTProto новее приложения (схема {cache.SchemaVersion}, поддерживается {SchemaVersions.MtProtoCache}).");
        }

        return MigrateLegacyStatuses(cache.Items);
    }

    /// <summary>Старый кэш не знал поля Status: выводим его из задержки и даты проверки.</summary>
    private static List<MtProtoProxy> MigrateLegacyStatuses(List<MtProtoProxy> proxies)
    {
        foreach (var proxy in proxies)
        {
            if (proxy.Status != MtProtoStatus.Unknown)
            {
                continue;
            }

            if (proxy.DelayMs is not null)
            {
                proxy.Status = MtProtoStatus.Working;
            }
            else if (proxy.CheckedAtUtc is not null)
            {
                proxy.Status = MtProtoStatus.Dead;
                proxy.DeadAtUtc ??= proxy.CheckedAtUtc;
            }
        }

        return proxies;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..index]);
            var value = Uri.UnescapeDataString(part[(index + 1)..]);
            result[key] = value;
        }

        return result;
    }
}
