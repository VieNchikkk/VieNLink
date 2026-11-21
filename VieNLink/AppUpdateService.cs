using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VieNLink;

/// <summary>Релиз VieNLink на GitHub и разбор версии для автообновления.</summary>
internal sealed record AppRelease(
    string Tag,
    string PageUrl,
    string? SetupUrl,
    string? ZipUrl,
    DateTime PublishedAt);

/// <summary>
/// Проверка обновлений самого приложения через GitHub Releases.
/// Репозиторий может быть приватным — тогда проверка просто не находит релизов.
/// </summary>
internal static class AppUpdateService
{
    public const string Repository = "VieNchikkk/VieNLink";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    private static readonly string[] ApiUrls =
    {
        $"https://api.github.com/repos/{Repository}/releases?per_page=20",
        $"https://ghfast.top/https://api.github.com/repos/{Repository}/releases?per_page=20",
    };

    public static async Task<AppRelease?> FindLatestAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // Для приватного репозитория можно задать токен в переменной окружения.
        // В поставку он не встраивается.
        var token = Environment.GetEnvironmentVariable("VIENLINK_GITHUB_TOKEN")
                    ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        foreach (var url in ApiUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(token) &&
                    url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
                }

                using var response = await client.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                AppRelease? best = null;
                Version? bestVersion = null;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    var tag = item.TryGetProperty("tag_name", out var tagElement)
                        ? tagElement.GetString()?.Trim()
                        : null;
                    var version = ParseVersion(tag);
                    if (version is null)
                    {
                        continue;
                    }

                    if (bestVersion is not null && version < bestVersion)
                    {
                        continue;
                    }

                    var pageUrl = item.TryGetProperty("html_url", out var html)
                        ? html.GetString() ?? ReleasesPage
                        : ReleasesPage;
                    string? setupUrl = null;
                    string? zipUrl = null;
                    if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.TryGetProperty("name", out var nameElement)
                                ? nameElement.GetString() ?? string.Empty
                                : string.Empty;
                            var downloadUrl = asset.TryGetProperty("browser_download_url", out var downloadElement)
                                ? downloadElement.GetString()
                                : null;
                            if (string.IsNullOrWhiteSpace(downloadUrl))
                            {
                                continue;
                            }

                            if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                setupUrl = downloadUrl;
                            }
                            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                     !name.Contains("Android", StringComparison.OrdinalIgnoreCase))
                            {
                                zipUrl = downloadUrl;
                            }
                        }
                    }

                    var published = item.TryGetProperty("published_at", out var publishedElement) &&
                                    publishedElement.TryGetDateTimeOffset(out var publishedAt)
                        ? publishedAt.UtcDateTime
                        : DateTime.MinValue;

                    if (bestVersion is null || version > bestVersion ||
                        (version == bestVersion && published > (best?.PublishedAt ?? DateTime.MinValue)))
                    {
                        bestVersion = version;
                        best = new AppRelease(tag!, pageUrl, setupUrl, zipUrl, published);
                    }
                }

                if (best is not null)
                {
                    return best;
                }
            }
            catch (HttpRequestException exception) when (exception.StatusCode is
                         System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            {
                AppLog.Write("update",
                    $"Релизы GitHub недоступны ({(int?)exception.StatusCode}): репозиторий приватный или нет доступа.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or JsonException)
            {
                AppLog.Write("update", $"Не удалось проверить релизы через {new Uri(url).Host}", exception);
            }
        }

        return null;
    }

    /// <summary>Версия из тега вида v1.0.6-beta или 1.0.6+hash.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimStart('v', 'V');
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            value = value[..dash];
        }

        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        return Version.TryParse(value, out var version) ? version : null;
    }
}
