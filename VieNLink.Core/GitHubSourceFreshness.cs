using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace VieNLink;

/// <summary>
/// Свежесть публичного списка на GitHub. Для серверов берём только источники,
/// опубликованные не позже заданного окна (по умолчанию 24 часа). Смотрим дату
/// последнего коммита файла, затем push репозитория. Если дату определить нельзя,
/// источник не собираем; временный сбой GitHub API не должен удалять все источники.
/// </summary>
internal static class GitHubSourceFreshness
{
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan KnownCacheTime = TimeSpan.FromHours(1);
    // Недоступный API не должен опрашиваться для каждого источника на каждом сборе.
    private static readonly TimeSpan UnknownCacheTime = TimeSpan.FromMinutes(15);

    private static readonly Regex RawRegex = new(
        @"raw\.githubusercontent\.com/([^/?#\s]+)/([^/?#\s]+)/(?<rest>[^?#\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlobRegex = new(
        @"github\.com/([^/?#\s]+)/([^/?#\s]+)/(?:blob|raw)/(?<rest>[^?#\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GitLabRawRegex = new(
        @"gitlab\.com/([^/?#\s]+)/([^/?#\s]+)/-/raw/([^/?#\s]+)/(?<rest>[^?#\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GiteeRawRegex = new(
        @"gitee\.com/([^/?#\s]+)/([^/?#\s]+)/raw/([^/?#\s]+)/(?<rest>[^?#\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Client = CreateClient();

    /// <summary>Форма кэша на диске: переживает перезапуск, чтобы не жечь квоту GitHub API заново.</summary>
    private sealed class FreshnessSnapshot
    {
        public Dictionary<string, FreshnessEntry> Entries { get; set; } = new();
    }

    private sealed class FreshnessEntry
    {
        public DateTime CheckedUtc { get; set; }
        public bool Fresh { get; set; }
        public bool DateKnown { get; set; }
        public bool ApiReached { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    static GitHubSourceFreshness()
    {
        var snapshot = JsonFileStore.Read<FreshnessSnapshot>(AppPaths.FreshnessPath);
        if (snapshot?.Entries is null)
        {
            return;
        }

        foreach (var pair in snapshot.Entries)
        {
            Cache[pair.Key] = new CacheEntry(
                pair.Value.CheckedUtc,
                pair.Value.Fresh,
                pair.Value.DateKnown,
                pair.Value.ApiReached,
                pair.Value.UpdatedAt);
        }
    }

    /// <summary>
    /// Сохраняет кэш дат на диск. Вызывается после сбора: при следующем запуске
    /// известные даты публикации не запрашиваются у API повторно.
    /// </summary>
    public static void PersistCache()
    {
        if (Cache.IsEmpty)
        {
            return;
        }

        try
        {
            var snapshot = new FreshnessSnapshot();
            foreach (var pair in Cache)
            {
                snapshot.Entries[pair.Key] = new FreshnessEntry
                {
                    CheckedUtc = pair.Value.CheckedUtc,
                    Fresh = pair.Value.Fresh,
                    DateKnown = pair.Value.DateKnown,
                    ApiReached = pair.Value.ApiReached,
                    UpdatedAt = pair.Value.UpdatedAt,
                };
            }

            JsonFileStore.Write(AppPaths.FreshnessPath, snapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("sources", "Не удалось сохранить кэш дат публикации", exception);
        }
    }

    /// <summary>
    /// Fresh — можно собирать; DateKnown — дата публикации известна (для иконки);
    /// ApiReached=false — GitHub API недоступен, источник нельзя удалять из-за этого.
    /// </summary>
    internal sealed record SourceFreshness(
        bool Fresh,
        bool DateKnown,
        bool ApiReached,
        DateTimeOffset? UpdatedAt);

    internal sealed record SourceLocation(
        string Host,
        string Repository,
        string? Reference,
        string? Path)
    {
        public string CacheKey => Host + "|" + Repository + "|" + (Reference ?? string.Empty) + "|" + (Path ?? string.Empty);
    }

    /// <summary>Максимум обращений к GitHub API за один сбор (на все источники вместе).</summary>
    public const int MaximumApiRequests = 30;

    /// <summary>
    /// Счётчик оставшихся обращений к GitHub API на текущий сбор. Источники сверх лимита
    /// собираются без проверки даты (как при недоступном API), а не пропускаются.
    /// </summary>
    internal sealed class ApiBudget
    {
        private int _remaining;
        public ApiBudget(int limit) => _remaining = limit;

        /// <summary>Пытается занять одно обращение; false — лимит исчерпан.</summary>
        public bool TryTake() => Interlocked.Decrement(ref _remaining) >= 0;
    }

    public static async Task<SourceFreshness> CheckAsync(
        SourceDefinition source,
        TimeSpan maximumAge,
        CancellationToken cancellationToken,
        ApiBudget? budget = null)
    {
        // Личные подписки, одиночные ссылки и каналы Telegram не фильтруем по дате
        // публикации: у каналов нет даты коммита на GitHub, но они могут давать серверы.
        if (source.IsMine || source.Kind == SourceKind.SingleLink || SourceKinds.IsTelegram(source))
        {
            return new SourceFreshness(true, false, true, null);
        }

        var location = TryGetLocation(source.Url);
        if (location is null)
        {
            // Не GitHub. У авто-найденных сайтов дата публикации лежит в самом источнике
            // (SourceDiscoveryService ставит LastPublishedUtc по адресу статьи/файла) —
            // свежесть считаем по ней.
            if (source.LastPublishedUtc is { } published)
            {
                var isFresh = DateTime.UtcNow - published <= maximumAge;
                return new SourceFreshness(isFresh, true, true, published);
            }

            // Дату определить нельзя — такой источник не собираем (карантин).
            return new SourceFreshness(false, false, true, null);
        }

        if (Cache.TryGetValue(location.CacheKey, out var cached))
        {
            var lifetime = cached.DateKnown ? KnownCacheTime : UnknownCacheTime;
            if (DateTime.UtcNow - cached.CheckedUtc < lifetime)
            {
                return new SourceFreshness(
                    cached.Fresh, cached.DateKnown, cached.ApiReached, cached.UpdatedAt);
            }
        }

        // Бюджет исчерпан — ведём себя как при недоступном API: собираем без даты.
        if (budget is not null && !budget.TryTake())
        {
            return new SourceFreshness(false, false, false, null);
        }

        var (updatedAt, reached) = await FetchUpdatedAtAsync(location, cancellationToken)
            .ConfigureAwait(false);
        var known = updatedAt is not null;
        var fresh = known && DateTime.UtcNow - updatedAt!.Value.UtcDateTime <= maximumAge;
        Cache[location.CacheKey] = new CacheEntry(DateTime.UtcNow, fresh, known, reached, updatedAt);
        return new SourceFreshness(fresh, known, reached, updatedAt);
    }

    /// <summary>Свежая ли дата публикации; null не считается свежим.</summary>
    internal static bool IsPushedAtFresh(DateTimeOffset? pushedAt, DateTime nowUtc) =>
        pushedAt is not null && nowUtc - pushedAt.Value.UtcDateTime <= DefaultMaximumAge;

    /// <summary>owner/repo из ссылки GitHub; null — источник не на GitHub.</summary>
    internal static string? TryGetRepository(string? url) => TryGetLocation(url)?.Repository;

    /// <summary>Разбирает ссылку GitHub/GitLab/Gitee в хост, репозиторий, ветку и путь к файлу.</summary>
    internal static SourceLocation? TryGetLocation(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var gitlab = GitLabRawRegex.Match(url);
        if (gitlab.Success)
        {
            return new SourceLocation(
                "gitlab",
                gitlab.Groups[1].Value + "/" + gitlab.Groups[2].Value,
                gitlab.Groups[3].Value,
                gitlab.Groups["rest"].Value);
        }

        var gitee = GiteeRawRegex.Match(url);
        if (gitee.Success)
        {
            return new SourceLocation(
                "gitee",
                gitee.Groups[1].Value + "/" + gitee.Groups[2].Value,
                gitee.Groups[3].Value,
                gitee.Groups["rest"].Value);
        }

        var match = RawRegex.Match(url);
        if (!match.Success)
        {
            match = BlobRegex.Match(url);
        }

        if (!match.Success)
        {
            // Ссылка на сам репозиторий без файла: хватит pushed_at.
            var repo = Regex.Match(
                url,
                @"(?:^|/)(?:github\.com|api\.github\.com/repos)/([^/?#\s]+)/([^/?#\s]+)",
                RegexOptions.IgnoreCase);
            return repo.Success
                ? new SourceLocation("github", repo.Groups[1].Value + "/" + repo.Groups[2].Value, null, null)
                : null;
        }

        var repository = match.Groups[1].Value + "/" + match.Groups[2].Value;
        var parts = match.Groups["rest"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new SourceLocation("github", repository, null, null);
        }

        string reference;
        var index = 0;
        if (parts.Length >= 3 && parts[0].Equals("refs", StringComparison.OrdinalIgnoreCase) &&
            (parts[1].Equals("heads", StringComparison.OrdinalIgnoreCase) ||
             parts[1].Equals("tags", StringComparison.OrdinalIgnoreCase)))
        {
            reference = parts[2];
            index = 3;
        }
        else
        {
            reference = parts[0];
            index = 1;
        }

        var path = index < parts.Length ? string.Join('/', parts[index..]) : null;
        return new SourceLocation("github", repository, reference, path);
    }

    private static async Task<(DateTimeOffset? Date, bool Reached)> FetchUpdatedAtAsync(
        SourceLocation location,
        CancellationToken cancellationToken)
    {
        if (location.Path is null)
        {
            return await FetchRepositoryDateAsync(location, cancellationToken).ConfigureAwait(false);
        }

        return location.Host switch
        {
            "gitlab" => await FetchGitLabAsync(location, cancellationToken).ConfigureAwait(false),
            "gitee" => await FetchGiteeAsync(location, cancellationToken).ConfigureAwait(false),
            _ => await FetchGitHubAsync(location, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async Task<(DateTimeOffset? Date, bool Reached)> FetchGitHubAsync(
        SourceLocation location,
        CancellationToken cancellationToken)
    {
        var path = Uri.EscapeDataString(location.Path!);
        var apiUrl = "https://api.github.com/repos/" + location.Repository + "/commits?path=" + path + "&per_page=1";
        if (!string.IsNullOrWhiteSpace(location.Reference))
        {
            apiUrl += "&sha=" + Uri.EscapeDataString(location.Reference);
        }

        var file = await ReadDateAsync(apiUrl, "commit", cancellationToken).ConfigureAwait(false);
        if (file.Date is not null)
        {
            return file;
        }

        var repository = await FetchRepositoryDateAsync(location, cancellationToken).ConfigureAwait(false);
        return (repository.Date, file.Reached || repository.Reached);
    }

    /// <summary>Дата последней активности репозитория (для ссылок без файла).</summary>
    private static Task<(DateTimeOffset? Date, bool Reached)> FetchRepositoryDateAsync(
        SourceLocation location,
        CancellationToken cancellationToken) =>
        location.Host switch
        {
            "gitlab" => FetchDateAsync(
                "https://gitlab.com/api/v4/projects/" + Uri.EscapeDataString(location.Repository),
                root => ParseProp(root, "last_activity_at"), cancellationToken),
            "gitee" => FetchDateAsync(
                "https://gitee.com/api/v5/repos/" + location.Repository,
                root => ParseProp(root, "updated_at"), cancellationToken),
            _ => ReadDateAsync("https://api.github.com/repos/" + location.Repository, "pushed_at", cancellationToken),
        };

    private static async Task<(DateTimeOffset? Date, bool Reached)> FetchGitLabAsync(
        SourceLocation location,
        CancellationToken cancellationToken)
    {
        var commitsUrl = "https://gitlab.com/api/v4/projects/" + Uri.EscapeDataString(location.Repository) +
                         "/repository/commits?per_page=1&path=" + Uri.EscapeDataString(location.Path!);
        if (!string.IsNullOrWhiteSpace(location.Reference))
        {
            commitsUrl += "&ref_name=" + Uri.EscapeDataString(location.Reference);
        }

        var file = await FetchDateAsync(commitsUrl, root => ParseProp(root, "committed_date"), cancellationToken)
            .ConfigureAwait(false);
        if (file.Date is not null)
        {
            return file;
        }

        var repository = await FetchRepositoryDateAsync(location, cancellationToken).ConfigureAwait(false);
        return (repository.Date, file.Reached || repository.Reached);
    }

    private static async Task<(DateTimeOffset? Date, bool Reached)> FetchGiteeAsync(
        SourceLocation location,
        CancellationToken cancellationToken)
    {
        var commitsUrl = "https://gitee.com/api/v5/repos/" + location.Repository +
                         "/commits?per_page=1&path=" + Uri.EscapeDataString(location.Path!);
        if (!string.IsNullOrWhiteSpace(location.Reference))
        {
            commitsUrl += "&sha=" + Uri.EscapeDataString(location.Reference);
        }

        var file = await FetchDateAsync(commitsUrl, ParseCommitDate, cancellationToken).ConfigureAwait(false);
        if (file.Date is not null)
        {
            return file;
        }

        var repository = await FetchRepositoryDateAsync(location, cancellationToken).ConfigureAwait(false);
        return (repository.Date, file.Reached || repository.Reached);
    }

    /// <summary>
    /// Простой GET + разбор даты по переданному селектору (для GitLab/Gitee, без зеркал GitHub).
    /// </summary>
    private static async Task<(DateTimeOffset? Date, bool Reached)> FetchDateAsync(
        string apiUrl,
        Func<JsonElement, DateTimeOffset?> parse,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return (null, true);
                }

                root = root[0];
            }

            return root.ValueKind == JsonValueKind.Object ? (parse(root), true) : (null, true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (null, false);
        }
    }

    private static DateTimeOffset? ParseProp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? ParseDate(value) : null;

    private static DateTimeOffset? ParseCommitDate(JsonElement root)
    {
        if (!root.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (commit.TryGetProperty("committer", out var committer) && committer.ValueKind == JsonValueKind.Object &&
            committer.TryGetProperty("date", out var date))
        {
            return ParseDate(date);
        }

        return null;
    }

    private static async Task<(DateTimeOffset? Date, bool Reached)> ReadDateAsync(
        string apiUrl,
        string property,
        CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("VIENLINK_GITHUB_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        var urls = new[]
        {
            apiUrl,
            "https://ghfast.top/" + apiUrl,
            "https://gh-proxy.com/" + apiUrl,
        };

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(token) &&
                    url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
                }

                using var response = await Client.SendAsync(
                    request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    if (root.GetArrayLength() == 0)
                    {
                        return (null, true);
                    }

                    root = root[0];
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return (null, true);
                }

                if (property == "commit")
                {
                    if (!root.TryGetProperty("commit", out var commit) ||
                        commit.ValueKind != JsonValueKind.Object)
                    {
                        return (null, true);
                    }

                    if (commit.TryGetProperty("committer", out var committer) &&
                        committer.ValueKind == JsonValueKind.Object &&
                        committer.TryGetProperty("date", out var committerDate))
                    {
                        return (ParseDate(committerDate), true);
                    }

                    if (commit.TryGetProperty("author", out var author) &&
                        author.ValueKind == JsonValueKind.Object &&
                        author.TryGetProperty("date", out var authorDate))
                    {
                        return (ParseDate(authorDate), true);
                    }

                    return (null, true);
                }

                return (root.TryGetProperty(property, out var value) ? ParseDate(value) : null, true);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or JsonException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        return (null, false);
    }

    private static DateTimeOffset? ParseDate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            element.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink-SourceFreshness/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private readonly record struct CacheEntry(
        DateTime CheckedUtc,
        bool Fresh,
        bool DateKnown,
        bool ApiReached,
        DateTimeOffset? UpdatedAt);
}
