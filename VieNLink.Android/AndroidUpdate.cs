using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace VieNLink.Mobile;

/// <summary>
/// Проверка обновлений Android-версии на GitHub. В отличие от Windows-релиза,
/// APK прикладывается к релизу отдельным файлом, поэтому обновление идёт
/// не открытием страницы, а скачиванием и установкой через PackageInstaller.
/// </summary>
internal static class AndroidUpdate
{
    public const string Repository = "VieNchikkk/VieNLink";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>Релиз с APK для Android.</summary>
    public sealed record Release(
        string Tag,
        string PageUrl,
        string Body,
        DateTime PublishedUtc,
        string ApkUrl,
        string ApkName,
        long ApkSize,
        string? Sha256Url);

    // /releases/latest не подходит: все наши релизы помечены Pre-release,
    // а этот метод предрелизы игнорирует. Забираем список и выбираем сами.
    private static readonly string[] ApiUrls =
    {
        $"https://api.github.com/repos/{Repository}/releases?per_page=30",
        $"https://ghfast.top/https://api.github.com/repos/{Repository}/releases?per_page=30",
        $"https://gh-proxy.com/https://api.github.com/repos/{Repository}/releases?per_page=30",
    };

    /// <summary>
    /// Резервный источник, когда JSON API недоступен: atom-лента релизов есть и через
    /// зеркала, а адреса APK у нас строятся по одному правилу из тега.
    /// </summary>
    private static readonly string[] AtomUrls =
    {
        $"https://github.com/{Repository}/releases.atom",
        $"https://ghfast.top/https://github.com/{Repository}/releases.atom",
        $"https://gh-proxy.com/https://github.com/{Repository}/releases.atom",
    };

    /// <summary>
    /// Зеркала для скачивания APK. Сам github.com/releases/download... часто отдаёт
    /// редирект на objects.githubusercontent.com, который у операторов блокируется —
    /// поэтому после прямой попытки пробуем проксирующие зеркала.
    /// </summary>
    private static readonly string[] DownloadPrefixes =
    {
        string.Empty,
        "https://ghfast.top/",
        "https://gh-proxy.com/",
    };

    public static async Task<Release?> FindLatestAsync(CancellationToken cancellationToken)
    {
        using var handler = CreateHandler();
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        Prepare(client);

        foreach (var url in ApiUrls)
        {
            try
            {
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                Release? best = null;
                int[]? bestVersion = null;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    var tag = item.TryGetProperty("tag_name", out var tagElement)
                        ? tagElement.GetString()?.Trim() ?? string.Empty
                        : string.Empty;
                    var version = ParseVersion(tag);
                    if (version is null)
                    {
                        continue;
                    }

                    string? apkUrl = null;
                    string? apkName = null;
                    string? shaUrl = null;
                    long apkSize = 0;
                    if (item.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.TryGetProperty("name", out var nameElement)
                                ? nameElement.GetString() ?? string.Empty
                                : string.Empty;
                            var download = asset.TryGetProperty("browser_download_url", out var downloadElement)
                                ? downloadElement.GetString()
                                : null;
                            if (string.IsNullOrWhiteSpace(download))
                            {
                                continue;
                            }

                            if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                            {
                                apkUrl = download;
                                apkName = Path.GetFileName(name);
                                if (asset.TryGetProperty("size", out var sizeElement) &&
                                    sizeElement.TryGetInt64(out var size))
                                {
                                    apkSize = size;
                                }
                            }
                            else if (name.EndsWith(".apk.sha256", StringComparison.OrdinalIgnoreCase))
                            {
                                shaUrl = download;
                            }
                        }
                    }

                    // Релизы Windows не содержат APK — они Android-версии не подходят.
                    if (apkUrl is null || string.IsNullOrWhiteSpace(apkName))
                    {
                        continue;
                    }

                    var published = item.TryGetProperty("published_at", out var publishedElement) &&
                                    publishedElement.TryGetDateTimeOffset(out var publishedAt)
                        ? publishedAt.UtcDateTime
                        : DateTime.MinValue;
                    var pageUrl = item.TryGetProperty("html_url", out var html)
                        ? html.GetString() ?? ReleasesPage
                        : ReleasesPage;
                    var body = item.TryGetProperty("body", out var bodyElement)
                        ? bodyElement.GetString() ?? string.Empty
                        : string.Empty;

                    if (bestVersion is not null)
                    {
                        var comparison = Compare(version, bestVersion);
                        if (comparison < 0 ||
                            (comparison == 0 && published <= (best?.PublishedUtc ?? DateTime.MinValue)))
                        {
                            continue;
                        }
                    }

                    bestVersion = version;
                    best = new Release(tag, pageUrl, body, published, apkUrl, apkName, apkSize, shaUrl);
                }

                if (best is not null)
                {
                    return best;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or JsonException)
            {
                AppLog.Write("update", $"Не удалось получить релизы через {new Uri(url).Host}", exception);
            }
        }

        // JSON API не ответил (блокировка или лимит): читаем atom-ленту релизов.
        // Этого достаточно, потому что имена APK в наших релизах строятся из версии.
        foreach (var url in AtomUrls)
        {
            try
            {
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var release = ParseAtom(xml);
                if (release is not null)
                {
                    AppLog.Write("update", "Релизы получены через atom-ленту " + new Uri(url).Host);
                    return release;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or JsonException or XmlException)
            {
                AppLog.Write("update", $"Не удалось получить atom-ленту через {new Uri(url).Host}", exception);
            }
        }

        return null;
    }

    /// <summary>
    /// Разбирает atom-ленту релизов. Тег берётся из ссылки, версия — из тега;
    /// адрес APK собирается по правилу сборки: <c>VieNLink-&lt;версия&gt;-android.apk</c>.
    /// </summary>
    private static Release? ParseAtom(string xml)
    {
        Release? best = null;
        int[]? bestVersion = null;
        foreach (Match entry in Regex.Matches(xml, "<entry>(.*?)</entry>", RegexOptions.Singleline))
        {
            var block = entry.Groups[1].Value;
            var link = Regex.Match(block, "<link[^>]*href=\"([^\"]+)\"");
            if (!link.Success)
            {
                continue;
            }

            var href = link.Groups[1].Value;
            var tagIndex = href.LastIndexOf("/tag/", StringComparison.OrdinalIgnoreCase);
            if (tagIndex < 0)
            {
                continue;
            }

            var tag = href[(tagIndex + "/tag/".Length)..].Trim();
            // Только Android-релизы: у Windows-релиза APK нет, и попытка скачать
            // придуманный по версии файл закончилась бы «не удалось скачать».
            if (!tag.Contains("android", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = ParseVersion(tag);
            var versionText = VersionText(tag);
            if (version is null || versionText is null)
            {
                continue;
            }

            if (bestVersion is not null && Compare(version, bestVersion) <= 0)
            {
                continue;
            }

            var apkName = $"VieNLink-{versionText}-android.apk";
            var apkUrl = $"{ReleasesPage}/download/{tag}/{apkName}";
            var updated = Regex.Match(block, "<updated>([^<]+)</updated>");
            var published = updated.Success &&
                            DateTimeOffset.TryParse(updated.Groups[1].Value, out var publishedAt)
                ? publishedAt.UtcDateTime
                : DateTime.MinValue;

            bestVersion = version;
            best = new Release(
                tag,
                $"{ReleasesPage}/tag/{tag}",
                string.Empty,
                published,
                apkUrl,
                apkName,
                0,
                apkUrl + ".sha256");
        }

        return best;
    }

    /// <summary>Числовая часть версии из тега: «v1.3.1-android» → «1.3.1».</summary>
    private static string? VersionText(string tag)
    {
        var match = Regex.Match(tag, @"(\d+(?:\.\d+)+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Скачивает APK во временный файл, проверяет размер и SHA-256 (если релиз
    /// содержит файл <c>*.apk.sha256</c>) и возвращает путь к готовому файлу.
    /// </summary>
    public static async Task<string> DownloadAsync(
        Release release,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // Общий предел на скачивание: без него поток может «повиснуть» после
        // последнего байта, и уведомление навсегда останется на 100%.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        var token = timeout.Token;

        using var handler = CreateHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Prepare(client);

        // Прямой адрес GitHub, затем зеркала: у операторов редирект на
        // objects.githubusercontent.com часто заблокирован, и APK «не скачивается».
        Exception? lastError = null;
        foreach (var prefix in DownloadPrefixes)
        {
            var apkUrl = prefix + release.ApkUrl;
            var shaUrl = release.Sha256Url is null ? null : prefix + release.Sha256Url;
            try
            {
                return await DownloadOnceAsync(client, release, apkUrl, shaUrl, targetPath, progress, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or InvalidDataException or IOException)
            {
                lastError = exception;
                AppLog.Write("update",
                    $"Скачивание APK через {(prefix.Length == 0 ? "GitHub" : new Uri(prefix).Host)} не удалось",
                    exception);
            }
        }

        throw lastError ?? new InvalidOperationException("Не удалось скачать APK ни с одного адреса.");
    }

    private static async Task<string> DownloadOnceAsync(
        HttpClient client,
        Release release,
        string apkUrl,
        string? shaUrl,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken token)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("Не удалось определить папку для APK.");
        Directory.CreateDirectory(directory);
        var tempPath = fullPath + ".part";

        try
        {
            using var response = await client.GetAsync(
                apkUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.ApkSize;
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(
                             tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    copied += read;
                    if (total > 0)
                    {
                        progress?.Report(Math.Clamp((double)copied / total, 0, 1));
                    }
                }

                await output.FlushAsync(token).ConfigureAwait(false);
            }

            if (total > 0 && new FileInfo(tempPath).Length != total)
            {
                throw new InvalidDataException("Загрузка APK оборвалась: размер файла не совпал.");
            }

            var expected = await ReadExpectedSha256Async(client, shaUrl, token)
                .ConfigureAwait(false);
            if (expected is not null)
            {
                var actual = await ComputeSha256Async(tempPath, token).ConfigureAwait(false);
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Контрольная сумма APK не совпала: файл повреждён или подменён.");
                }
            }

            await EnsureZipAsync(tempPath, token).ConfigureAwait(false);
            File.Move(tempPath, fullPath, overwrite: true);
            progress?.Report(1);
            return fullPath;
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception)
            {
                // Временный файл уберёт система очистки кэша.
            }

            throw;
        }
    }

    /// <summary>Сравнивает версии вида 1.2.3-beta или v1.1.0-android. true — latest новее.</summary>
    public static bool IsNewer(string current, string latest) => Compare(Parse(current), Parse(latest)) < 0;

    private static SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AllowAutoRedirect = true,
        };

        // Если поднят VPN-сервер, GitHub и загрузка идут через него: прямой доступ
        // к api.github.com у части провайдеров блокируется.
        if (CoreRuntime.IsRunning)
        {
            handler.Proxy = new System.Net.WebProxy("http://127.0.0.1:" + CoreRuntime.MixedPort);
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        return handler;
    }

    private static void Prepare(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink-Android");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private static async Task<string?> ReadExpectedSha256Async(
        HttpClient client,
        string? shaUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shaUrl))
        {
            return null;
        }

        // Файл контрольной суммы маленький; если GitHub не ответил за 15 секунд,
        // не держим установку — продолжаем без проверки (APK всё равно пришёл по HTTPS).
        using var shaTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shaTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var text = await client.GetStringAsync(shaUrl, shaTimeout.Token).ConfigureAwait(false);
            var match = Regex.Match(text, "[0-9a-fA-F]{64}");
            return match.Success ? match.Value : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AppLog.Write("update", "Не удалось получить SHA-256 обновления", exception);
            return null;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task EnsureZipAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken).ConfigureAwait(false);
        if (read < 4 || header[0] != (byte)'P' || header[1] != (byte)'K')
        {
            throw new InvalidDataException("Скачанный файл не похож на APK.");
        }
    }

    private static int[]? ParseVersion(string version)
    {
        var cleaned = new string(version.Trim().TrimStart('v', 'V')
            .TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray());
        return cleaned.Length == 0 ? null : Parse(version);
    }

    private static int[] Parse(string version)
    {
        var cleaned = new string(version.Trim().TrimStart('v', 'V')
            .TakeWhile(character => char.IsDigit(character) || character == '.')
            .ToArray());
        return cleaned.Length == 0
            ? new[] { 0 }
            : cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out var value) ? value : 0)
                .ToArray();
    }

    private static int Compare(int[] left, int[] right)
    {
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var a = index < left.Length ? left[index] : 0;
            var b = index < right.Length ? right[index] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }
}
