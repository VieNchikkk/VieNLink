using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VieNLink;

/// <summary>
/// Свежесть публичного списка на GitHub. При сборе серверов пропускаем не только старые
/// репозитории, но и отдельные файлы: смотрим дату последнего коммита файла, а если
/// её получить не удалось — дату последнего push репозитория. Если даты нет вовсе,
/// источник не отсеиваем: важно не потерять сеть целиком.
/// </summary>
internal static class GitHubSourceFreshness
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan KnownCacheTime = TimeSpan.FromHours(1);
    // Недоступный API не должен опрашиваться для каждого источника на каждом сборе.
    private static readonly TimeSpan UnknownCacheTime = TimeSpan.FromMinutes(15);

    private static readonly Regex RawRegex = new(
        @"raw\.githubusercontent\.com/([^/?#\s]+)/([^/?#\s]+)/(?<rest>[^?#\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BlobRegex = new(
        @"github\.com/([^/?#\s]+)/([^/?#\s]+)/(?:blob|raw)/(?<rest>[^?#\s]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Client = CreateClient();

    internal sealed record SourceLocation(
        string Repository,
        string? Reference,
        string? Path)
    {
        public string CacheKey => Path is null
            ? Repository
            : Repository + "|" + (Reference ?? string.Empty) + "|" + Path;
    }

    public static async Task<bool> IsFreshAsync(
        SourceDefinition source,
        CancellationToken cancellationToken)
    {
        // Личные подписки и одиночные ссылки не фильтруем: это выбор пользователя.
        if (source.IsMine || source.Kind == SourceKind.SingleLink)
        {
            return true;
        }

        var location = TryGetLocation(source.Url);
        if (location is null)
        {
            return true;
        }

        if (Cache.TryGetValue(location.CacheKey, out var cached))
        {
            var lifetime = cached.Known ? KnownCacheTime : UnknownCacheTime;
            if (DateTime.UtcNow - cached.CheckedUtc < lifetime)
            {
                return cached.Fresh;
            }
        }

        var updatedAt = await FetchUpdatedAtAsync(location, cancellationToken).ConfigureAwait(false);
        var fresh = IsPushedAtFresh(updatedAt, DateTime.UtcNow);
        Cache[location.CacheKey] = new CacheEntry(DateTime.UtcNow, fresh, updatedAt is not null);
        return fresh;
    }

    /// <summary>Свежая ли дата публикации; null/нераспознанная дата не отсеивает источник.</summary>
    internal static bool IsPushedAtFresh(DateTimeOffset? pushedAt, DateTime nowUtc) =>
        pushedAt is null || nowUtc - pushedAt.Value.UtcDateTime <= MaximumAge;

    /// <summary>owner/repo из ссылки GitHub; null — источник не на GitHub.</summary>
    internal static string? TryGetRepository(string? url) => TryGetLocation(url)?.Repository;

    /// <summary>Разбирает ссылку GitHub в репозиторий, ветку и путь к файлу.</summary>
    internal static SourceLocation? TryGetLocation(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
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
                ? new SourceLocation(repo.Groups[1].Value + "/" + repo.Groups[2].Value, null, null)
                : null;
        }

        var repository = match.Groups[1].Value + "/" + match.Groups[2].Value;
        var parts = match.Groups["rest"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new SourceLocation(repository, null, null);
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
        return new SourceLocation(repository, reference, path);
    }

    private static async Task<DateTimeOffset?> FetchUpdatedAtAsync(
        SourceLocation location,
        CancellationToken cancellationToken)
    {
        if (location.Path is null)
        {
            return await FetchRepositoryPushedAtAsync(location.Repository, cancellationToken).ConfigureAwait(false);
        }

        var path = Uri.EscapeDataString(location.Path);
        var apiUrl = "https://api.github.com/repos/" + location.Repository + "/commits?path=" + path + "&per_page=1";
        if (!string.IsNullOrWhiteSpace(location.Reference))
        {
            apiUrl += "&sha=" + Uri.EscapeDataString(location.Reference);
        }

        var updatedAt = await ReadDateAsync(apiUrl, "commit", cancellationToken).ConfigureAwait(false);
        // Если коммиты файла получить не удалось, пробуем хотя бы push репозитория.
        return updatedAt ?? await FetchRepositoryPushedAtAsync(location.Repository, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<DateTimeOffset?> FetchRepositoryPushedAtAsync(
        string repository,
        CancellationToken cancellationToken) =>
        ReadDateAsync("https://api.github.com/repos/" + repository, "pushed_at", cancellationToken);

    private static async Task<DateTimeOffset?> ReadDateAsync(
        string apiUrl,
        string property,
        CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("VIENLINK_GITHUB_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        // Прямой GitHub, затем зеркала: API в РФ часто недоступен.
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
                        return null;
                    }

                    root = root[0];
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (property == "commit")
                {
                    if (!root.TryGetProperty("commit", out var commit) ||
                        commit.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    if (commit.TryGetProperty("committer", out var committer) &&
                        committer.ValueKind == JsonValueKind.Object &&
                        committer.TryGetProperty("date", out var committerDate))
                    {
                        return ParseDate(committerDate);
                    }

                    if (commit.TryGetProperty("author", out var author) &&
                        author.ValueKind == JsonValueKind.Object &&
                        author.TryGetProperty("date", out var authorDate))
                    {
                        return ParseDate(authorDate);
                    }

                    return null;
                }

                return root.TryGetProperty(property, out var value) ? ParseDate(value) : null;
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

        return null;
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

    private readonly record struct CacheEntry(DateTime CheckedUtc, bool Fresh, bool Known);
}
