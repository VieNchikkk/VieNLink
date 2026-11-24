using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VieNLink;

internal sealed class DiscoveryResult
{
    public List<SourceDefinition> Sources { get; } = new();
    public List<ProxyNode> Nodes { get; } = new();
    public List<MtProtoProxy> MtProto { get; } = new();
    public int RepositoriesSeen { get; set; }
    public int FilesChecked { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Автопоиск новых источников: ищет на GitHub недавно обновлённые репозитории с конфигами,
/// затем обходит публичные сайты с ежедневными подписками, смотрит дерево файлов и пробует
/// те, что похожи на списки серверов. Ничего не добавляет, пока файл реально не дал серверы.
/// </summary>
internal static class SourceDiscoveryService
{
    private const int RepositoriesPerRun = 8;
    private const int FilesPerRepository = 3;
    private const int MinimumNodes = 25;
    private const long MaximumFileBytes = 6L * 1024 * 1024;
    private const int MaximumApiRequests = 45;
    private const int WebSitesPerRun = 4;
    private const int ArticlesPerSite = 2;
    private const int SubscriptionLinksPerArticle = 3;

    /// <summary>
    /// Свежесть источника на GitHub: смотрим время последнего push репозитория.
    /// Если даты нет — источник не отсеиваем.
    /// </summary>
    private static readonly TimeSpan MaximumRepositoryAge = TimeSpan.FromHours(24);

    private static readonly HttpClient Api = CreateClient();
    private static readonly HttpClient Raw = CreateClient();

    private static readonly string[] Queries =
    {
        "v2ray configs",
        "vless subscription",
        "free v2ray nodes",
        "clash subscription",
        "xray configs",
    };

    /// <summary>Публичные сайты, которые выкладывают свежие подписки каждый день.</summary>
    private static readonly string[] WebSeeds =
    {
        "https://nodefree.org/f/freenode",
        "https://www.freeclashnode.com/free-node/",
        "https://v2rayshare.com/",
        "https://clashnode.xyz/",
    };

    private static readonly Regex AnchorRegex = new(
        @"(?is)<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>",
        RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"(?s)<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex SubscriptionUrlRegex = new(
        @"(?i)https?://[^\s""'<>]+?\.(?:txt|yaml|yml|json)(?:\?[^\s""'<>]*)?",
        RegexOptions.Compiled);

    private static readonly Regex ArticlePathRegex = new(@"^/p/\d+(?:\.html?)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Дата в адресе статьи/подписки: 2026-11-19 или 2026/11/19.</summary>
    private static readonly Regex UrlDateRegex = new(
        @"20\d{2}[-/]\d{1,2}[-/]\d{1,2}",
        RegexOptions.Compiled);

    /// <summary>Дата в имени файла без разделителей: 20261119.</summary>
    private static readonly Regex UrlDateCompactRegex = new(@"20\d{6}", RegexOptions.Compiled);

    private static readonly string[] ArticleKeywords =
    {
        "node", "clash", "v2ray", "vpn", "ssr", "free", "proxy", "sub", "节点", "订阅",
    };

    private static readonly string[] SkippedSegments =
    {
        ".github", ".git", "docs", "doc", "image", "images", "img", "avatar", "assets", "license", "readme",
    };

    public static async Task<DiscoveryResult> DiscoverAsync(
        IReadOnlyList<SourceDefinition> existing,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        int freshnessHours = 24,
        bool includeWeb = true)
    {
        var maximumAge = TimeSpan.FromHours(Math.Clamp(freshnessHours, 1, 168 * 24));
        var result = new DiscoveryResult();
        var knownUrls = new HashSet<string>(existing.Select(s => s.Url), StringComparer.OrdinalIgnoreCase);
        var knownRepositories = new HashSet<string>(
            existing.Select(s => ExtractRepository(s.Url)).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        var budget = MaximumApiRequests;
        var candidates = new List<RepositoryCandidate>();

        foreach (var query in Queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                break;
            }

            var url = "https://api.github.com/search/repositories?q=" +
                      Uri.EscapeDataString(query + " in:name,description") +
                      "&sort=updated&order=desc&per_page=10";
            budget--;
            using var document = await GetJsonAsync(url, progress, cancellationToken);
            if (document is null)
            {
                continue;
            }

            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                var fullName = GetString(item, "full_name");
                var branch = GetString(item, "default_branch") ?? "main";
                var pushedAt = GetString(item, "pushed_at");
                if (fullName is null || knownRepositories.Contains(fullName))
                {
                    continue;
                }

                if (item.TryGetProperty("fork", out var fork) && fork.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (!TryParsePushedAt(pushedAt, out var pushed) ||
                    DateTime.UtcNow - pushed > maximumAge)
                {
                    continue;
                }

                if (candidates.Any(c => c.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidates.Add(new RepositoryCandidate("github", fullName, branch, pushed));
            }
        }

        // GitLab: те же запросы, но через свой API.
        foreach (var query in Queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                break;
            }

            var url = "https://gitlab.com/api/v4/projects?search=" + Uri.EscapeDataString(query) +
                      "&order_by=last_activity_at&sort=desc&per_page=10";
            budget--;
            using var document = await GetJsonAsync(url, progress, cancellationToken, acceptJson: true);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var fullName = GetString(item, "path_with_namespace");
                var branch = GetString(item, "default_branch") ?? "main";
                var updatedAt = GetString(item, "last_activity_at");
                if (fullName is null || knownRepositories.Contains(fullName))
                {
                    continue;
                }

                if (item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                if (!TryParsePushedAt(updatedAt, out var pushed) ||
                    DateTime.UtcNow - pushed > maximumAge)
                {
                    continue;
                }

                if (candidates.Any(c => c.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidates.Add(new RepositoryCandidate("gitlab", fullName, branch, pushed));
            }
        }

        // Gitee: свой поиск и дерево.
        foreach (var query in Queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                break;
            }

            var url = "https://gitee.com/api/v5/search/repositories?q=" + Uri.EscapeDataString(query) +
                      "&sort=updated_at&order=desc&per_page=10";
            budget--;
            using var document = await GetJsonAsync(url, progress, cancellationToken, acceptJson: true);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var fullName = GetString(item, "full_name");
                var branch = GetString(item, "default_branch") ?? "master";
                var updatedAt = GetString(item, "updated_at");
                if (fullName is null || knownRepositories.Contains(fullName))
                {
                    continue;
                }

                if (!TryParsePushedAt(updatedAt, out var pushed) ||
                    DateTime.UtcNow - pushed > maximumAge)
                {
                    continue;
                }

                if (candidates.Any(c => c.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidates.Add(new RepositoryCandidate("gitee", fullName, branch, pushed));
            }
        }

        result.RepositoriesSeen = candidates.Count;
        progress?.Report($"Найдено репозиториев: {candidates.Count} (обновлены за 24 ч)");

        foreach (var candidate in candidates.Take(RepositoriesPerRun))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget <= 0)
            {
                result.Error = "Закончился лимит запросов к API";
                break;
            }

            progress?.Report($"Проверяю {candidate.FullName}…");
            budget--;
            using var tree = candidate.Host switch
            {
                "gitlab" => await GetJsonAsync(
                    "https://gitlab.com/api/v4/projects/" + Uri.EscapeDataString(candidate.FullName) +
                    "/repository/tree?recursive=true&ref=" + Uri.EscapeDataString(candidate.Branch),
                    progress, cancellationToken, acceptJson: true),
                "gitee" => await GetJsonAsync(
                    "https://gitee.com/api/v5/repos/" + candidate.FullName + "/git/trees/" +
                    Uri.EscapeDataString(candidate.Branch) + "?recursive=1",
                    progress, cancellationToken, acceptJson: true),
                _ => await GetJsonAsync(
                    "https://api.github.com/repos/" + candidate.FullName + "/git/trees/" +
                    Uri.EscapeDataString(candidate.Branch) + "?recursive=1",
                    progress, cancellationToken),
            };
            if (tree is null)
            {
                continue;
            }

            var paths = candidate.Host == "gitlab"
                ? SelectCandidatePaths(ReadGitLabTreeFiles(tree.RootElement))
                : SelectCandidatePaths(ReadTreeFiles(tree.RootElement));
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawUrl = candidate.Host switch
                {
                    "gitlab" => $"https://gitlab.com/{candidate.FullName}/-/raw/{candidate.Branch}/{path}",
                    "gitee" => $"https://gitee.com/{candidate.FullName}/raw/{candidate.Branch}/{path}",
                    _ => $"https://raw.githubusercontent.com/{candidate.FullName}/{candidate.Branch}/{path}",
                };
                if (!knownUrls.Add(rawUrl))
                {
                    continue;
                }

                result.FilesChecked++;
                var nodes = await TryLoadAsync(rawUrl, $"{Shorten(candidate.FullName)} · {Shorten(path, 28)}", cancellationToken);
                if (nodes is null || nodes.Count < MinimumNodes)
                {
                    progress?.Report($"  {candidate.FullName}/{Shorten(path, 28)}: серверов мало, пропускаю");
                    continue;
                }

                var source = new SourceDefinition
                {
                    Name = $"{candidate.FullName} · {Shorten(path, 24)}",
                    Url = rawUrl,
                    Kind = SourceKind.PlainList,
                    IsAutoDiscovered = true,
                    AddedAtUtc = DateTime.UtcNow,
                    LastCheckedUtc = DateTime.UtcNow,
                    LastNodeCount = nodes.Count,
                    LastPublishedUtc = candidate.PublishedAt,
                    IsTrusted = true,
                };
                source.RegisterNodes(nodes);

                result.Sources.Add(source);
                result.Nodes.AddRange(nodes);
                progress?.Report($"  + {source.Name}: {nodes.Count} серверов");
                break;
            }
        }

        // Публичные сайты публикуют ежедневно: дату берём из адреса статьи/подписки,
        // поэтому такие источники снова собираем (см. DiscoverWebAsync). На ТВ не нужны.
        if (includeWeb)
        {
            await DiscoverWebAsync(knownUrls, result, progress, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Обход публичных сайтов: берём свежие статьи со страниц-каталогов и вытаскиваем
    /// из них ссылки на подписки (.txt/.yaml/.json). Добавляем только рабочие списки.
    /// </summary>
    private static async Task DiscoverWebAsync(
        HashSet<string> knownUrls,
        DiscoveryResult result,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var seed in WebSeeds.Take(WebSitesPerRun))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(seed, UriKind.Absolute, out var seedUri))
            {
                continue;
            }

            progress?.Report($"Публичные сайты: {seedUri.Host}…");
            var index = await TryGetTextAsync(seed, cancellationToken);
            if (index is null)
            {
                continue;
            }

            var articles = SelectArticleLinks(index, seedUri).Take(ArticlesPerSite).ToList();
            if (articles.Count == 0)
            {
                articles.Add(seed);
            }

            var added = 0;
            foreach (var article in articles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (added >= 2)
                {
                    break;
                }

                var html = await TryGetTextAsync(article, cancellationToken);
                if (html is null)
                {
                    continue;
                }

                foreach (var candidate in ExtractSubscriptionUrls(html).Take(SubscriptionLinksPerArticle))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!knownUrls.Add(candidate))
                    {
                        continue;
                    }

                    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri))
                    {
                        continue;
                    }

                    var shortName = Shorten(Path.GetFileName(candidateUri.AbsolutePath), 24);
                    result.FilesChecked++;
                    var nodes = await TryLoadAsync(candidate, $"{candidateUri.Host} · {shortName}", cancellationToken);
                    if (nodes is null || nodes.Count < MinimumNodes)
                    {
                        progress?.Report($"  {candidateUri.Host}/{shortName}: серверов мало, пропускаю");
                        continue;
                    }

                    var source = new SourceDefinition
                    {
                        Name = $"{candidateUri.Host} · {shortName}",
                        Url = candidate,
                        Kind = SourceKind.PlainList,
                        IsAutoDiscovered = true,
                        AddedAtUtc = DateTime.UtcNow,
                        LastCheckedUtc = DateTime.UtcNow,
                        LastNodeCount = nodes.Count,
                        // Дату публикации берём из адреса: подписки ежедневные, а файл
                        // назван датой (…/2026-11-19-vpn-free.htm или …/0-20261119.txt).
                        LastPublishedUtc = ExtractPublishedDate(candidate) ?? ExtractPublishedDate(article) ?? DateTime.UtcNow,
                        IsTrusted = true,
                    };
                    source.RegisterNodes(nodes);

                    result.Sources.Add(source);
                    result.Nodes.AddRange(nodes);
                    progress?.Report($"  + {source.Name}: {nodes.Count} серверов");
                    added++;
                    break;
                }
            }
        }
    }

    /// <summary>Ссылки на статьи со страницы-каталога; реклама, теги и разделы отбрасываются.</summary>
    internal static List<string> SelectArticleLinks(string html, Uri baseUri)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');

        foreach (Match match in AnchorRegex.Matches(html))
        {
            var href = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (href.Length == 0 || href.StartsWith('#') ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, href, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !uri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length == 0 || path.Equals(basePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (path.Contains("/tag/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/date/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/page/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/category/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/news/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/paid", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/client", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/go/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = System.Net.WebUtility.HtmlDecode(TagRegex.Replace(match.Groups[2].Value, " "));
            var haystack = (path + " " + text).ToLowerInvariant();
            var looksLikeArticle = ArticleKeywords.Any(keyword => haystack.Contains(keyword, StringComparison.Ordinal));
            if (!looksLikeArticle && !ArticlePathRegex.IsMatch(path))
            {
                continue;
            }

            if (seen.Add(path))
            {
                result.Add(uri.GetLeftPart(UriPartial.Path));
            }
        }

        return result;
    }

    /// <summary>Прямые ссылки на файлы подписок внутри статьи.</summary>
    internal static List<string> ExtractSubscriptionUrls(string html)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SubscriptionUrlRegex.Matches(html))
        {
            var url = System.Net.WebUtility.HtmlDecode(match.Value)
                .Trim()
                .TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
            if (url.Length > 12 && seen.Add(url))
            {
                result.Add(url);
            }
        }

        return result;
    }

    private static async Task<string?> TryGetTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VieNLink/0.9");
            using var response = await Raw.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            return text.Length > MaximumFileBytes ? null : text;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            AppLog.Write("discovery", $"Не удалось прочитать {url}", exception);
            return null;
        }
    }

    /// <summary>Отбор файлов по имени и размеру; вынесено отдельно, чтобы покрыть self-test.</summary>
    internal static List<string> SelectCandidatePaths(IEnumerable<(string Path, long Size)> files)
    {
        var scored = new List<(string Path, int Score, long Size)>();
        foreach (var (path, size) in files)
        {
            if (size is <= 200 or > MaximumFileBytes)
            {
                continue;
            }

            var score = ScorePath(path);
            if (score == 0)
            {
                continue;
            }

            scored.Add((path, score, size));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Size)
            .Take(FilesPerRepository)
            .Select(item => item.Path)
            .ToList();
    }

    /// <summary>Отбор файлов без размера (GitLab не отдаёт размер в дереве репозитория).</summary>
    internal static List<string> SelectCandidatePaths(IEnumerable<string> paths)
    {
        var scored = new List<(string Path, int Score)>();
        foreach (var path in paths)
        {
            var score = ScorePath(path);
            if (score == 0)
            {
                continue;
            }

            scored.Add((path, score));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .Take(FilesPerRepository)
            .Select(item => item.Path)
            .ToList();
    }

    private static int ScorePath(string path)
    {
        var lower = path.ToLowerInvariant();
        if (SkippedSegments.Any(segment => lower.Contains(segment, StringComparison.Ordinal)))
        {
            return 0;
        }

        var extension = Path.GetExtension(lower);
        if (extension is ".md" or ".png" or ".jpg" or ".ico" or ".svg" or ".exe" or ".zip" or ".gz" or ".log")
        {
            return 0;
        }

        var score = 0;
        if (lower.Contains("all_config", StringComparison.Ordinal) || lower.Contains("allconfig", StringComparison.Ordinal))
        {
            score += 6;
        }

        if (lower.Contains("sub", StringComparison.Ordinal))
        {
            score += 3;
        }

        if (lower.Contains("node", StringComparison.Ordinal))
        {
            score += 3;
        }

        if (lower.Contains("mix", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (lower.Contains("merge", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (lower.Contains("v2ray", StringComparison.Ordinal) || lower.Contains("vless", StringComparison.Ordinal) ||
            lower.Contains("clash", StringComparison.Ordinal) || lower.Contains("xray", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (extension is ".txt" or ".yaml" or ".yml" or ".json" || extension.Length == 0)
        {
            score += 1;
        }

        return score;
    }

    /// <summary>
    /// Свежий ли репозиторий: время последнего push не старше 24 часов.
    /// Без даты публикации источник не берём.
    /// </summary>
    internal static bool IsRepositoryFresh(string? pushedAt, DateTime nowUtc) =>
        TryParsePushedAt(pushedAt, out var pushed) &&
        nowUtc - pushed <= MaximumRepositoryAge;

    private static bool TryParsePushedAt(string? pushedAt, out DateTime pushed)
    {
        if (string.IsNullOrWhiteSpace(pushedAt))
        {
            pushed = default;
            return false;
        }

        return DateTime.TryParse(
            pushedAt,
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out pushed);
    }

    private static async Task<List<ProxyNode>?> TryLoadAsync(string url, string sourceName, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("v2rayNG/1.9.16 (VieNLink/0.9)");
            using var response = await Raw.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (text.Length > MaximumFileBytes)
            {
                return null;
            }

            return SubscriptionParser.Parse(text, sourceName);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    private static List<(string Path, long Size)> ReadTreeFiles(JsonElement root)
    {
        var files = new List<(string, long)>();
        if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
        {
            return files;
        }

        foreach (var item in tree.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "blob")
            {
                continue;
            }

            var path = GetString(item, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            long size = 0;
            if (item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed))
            {
                size = parsed;
            }

            files.Add((path, size));
        }

        return files;
    }

    /// <summary>Дерево GitLab: массив {path, type} без размера.</summary>
    private static List<string> ReadGitLabTreeFiles(JsonElement root)
    {
        var files = new List<string>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return files;
        }

        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "blob")
            {
                continue;
            }

            var path = GetString(item, "path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, IProgress<string>? progress, CancellationToken cancellationToken, bool acceptJson = false)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("VieNLink/0.9");
            request.Headers.Accept.ParseAdd(acceptJson ? "application/json" : "application/vnd.github+json");

            using var response = await Api.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 403)
                {
                    progress?.Report("Ограничение частоты запросов");
                }

                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            AppLog.Write("discovery", "Ошибка запроса к API", exception);
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ExtractRepository(string url)
    {
        const string prefix = "https://raw.githubusercontent.com/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = url[prefix.Length..].Split('/');
        return parts.Length >= 2 ? parts[0] + "/" + parts[1] : null;
    }

    private static string Shorten(string text, int limit = 40) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "…";

    /// <summary>
    /// Дата публикации из адреса: «…/2026-11-19-vpn-free.htm» или «…/0-20261119.txt».
    /// Её нет — null (источник посчитаем свежим на день находки).
    /// </summary>
    internal static DateTime? ExtractPublishedDate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = UrlDateRegex.Match(url);
        if (match.Success &&
            DateTime.TryParse(match.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            return date;
        }

        match = UrlDateCompactRegex.Match(url);
        if (match.Success &&
            DateTime.TryParseExact(match.Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
        {
            return date;
        }

        return null;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private readonly record struct RepositoryCandidate(string Host, string FullName, string Branch, DateTime PublishedAt);
}
