using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VieNLink;

/// <summary>
/// Ядро sing-box: поиск установленного exe или загрузка официального релиза.
/// Прямые ссылки GitHub в РФ часто недоступны, поэтому пробуем зеркала по очереди.
/// </summary>
internal static class SingBoxCore
{
    public const string Version = "1.14.4";

    /// <summary>Зеркала для скачивания конкретной версии: GitHub и два прокси-зеркала.</summary>
    public static IEnumerable<string> DownloadUrls(string version) => new[]
    {
        $"https://github.com/SagerNet/sing-box/releases/download/v{version}/sing-box-{version}-windows-amd64.zip",
        $"https://ghfast.top/https://github.com/SagerNet/sing-box/releases/download/v{version}/sing-box-{version}-windows-amd64.zip",
        $"https://gh-proxy.com/https://github.com/SagerNet/sing-box/releases/download/v{version}/sing-box-{version}-windows-amd64.zip",
    };

    /// <summary>Адреса проверки последней версии; GitHub API и зеркало, если первое закрыто.</summary>
    private static readonly string[] LatestVersionUrls =
    {
        "https://api.github.com/repos/SagerNet/sing-box/releases/latest",
        "https://ghfast.top/https://api.github.com/repos/SagerNet/sing-box/releases/latest",
    };

    /// <summary>
    /// Ядро ищется сначала рядом с приложением (папка core в релизе), затем в данных пользователя.
    /// </summary>
    public static string ExecutablePath
    {
        get
        {
            if (File.Exists(AppPaths.SingBoxPath))
            {
                return AppPaths.SingBoxPath;
            }

            var bundled = Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");
            return File.Exists(bundled) ? bundled : AppPaths.SingBoxPath;
        }
    }

    public static bool IsInstalled => File.Exists(ExecutablePath);

    /// <summary>TUN-режиму нужен wintun.dll рядом с sing-box.exe.</summary>
    public static string WintunPath => Path.Combine(Path.GetDirectoryName(ExecutablePath) ?? AppPaths.CoreDirectory, "wintun.dll");

    public static bool IsWintunInstalled => File.Exists(WintunPath);

    /// <summary>Скачивает wintun.dll (около 750 КБ) для TUN-режима.</summary>
    public static async Task EnsureWintunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (IsWintunInstalled)
        {
            return;
        }

        const string version = "0.14.1";
        var zipPath = Path.Combine(Path.GetTempPath(), $"vienlink-wintun-{version}.zip");
        progress?.Report("Скачивание wintun.dll для TUN…");

        using (var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) })
        using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) })
        {
            var bytes = await client.GetByteArrayAsync($"https://www.wintun.net/builds/wintun-{version}.zip", cancellationToken);
            await File.WriteAllBytesAsync(zipPath, bytes, cancellationToken);
        }

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("bin/amd64/wintun.dll", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidOperationException("В архиве wintun не найден bin/amd64/wintun.dll");
            }

            var target = WintunPath;
            var temporary = target + ".tmp";
            entry.ExtractToFile(temporary, overwrite: true);
            File.Move(temporary, target, overwrite: true);
            AppLog.Write("core", "wintun.dll установлен: " + target);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                "Не удалось положить wintun.dll рядом с sing-box.exe (нет прав на папку). " +
                "Скачайте https://www.wintun.net/builds/wintun-0.14.1.zip и распакуйте bin\\amd64\\wintun.dll в " +
                Path.GetDirectoryName(ExecutablePath),
                exception);
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    public static async Task<string> EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            return ExecutablePath;
        }

        AppPaths.EnsureDirectories();
        var zipPath = Path.Combine(Path.GetTempPath(), $"vienlink-sing-box-{Version}.zip");
        Exception? lastError = null;

        foreach (var url in DownloadUrls(Version))
        {
            try
            {
                progress?.Report($"Скачивание ядра sing-box {Version}…");
                await DownloadAsync(url, zipPath, progress, cancellationToken);
                ExtractExecutable(zipPath);
                await VerifyAsync(cancellationToken);
                WriteInstalledVersion(Version);
                progress?.Report("Ядро sing-box готово.");
                TryDelete(zipPath);
                return AppPaths.SingBoxPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                AppLog.Write("core", $"Не удалось скачать ядро с {new Uri(url).Host}", exception);
            }
        }

        TryDelete(zipPath);
        throw new InvalidOperationException(
            "Не удалось скачать sing-box ни с GitHub, ни с зеркал. Включите VPN или положите sing-box.exe в " + AppPaths.CoreDirectory,
            lastError);
    }

    public static async Task VerifyAsync(CancellationToken cancellationToken) => await VerifyAsync(ExecutablePath, cancellationToken);

    private static async Task VerifyAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(path, "version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить sing-box.exe");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("sing-box.exe не запускается: " + output.Trim());
        }

        AppLog.Write("core", output.Split('\n').FirstOrDefault()?.Trim() ?? "sing-box");
    }

    /// <summary>Последняя версия sing-box по данным GitHub; null — проверить не удалось.</summary>
    public static async Task<string?> CheckLatestVersionAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        foreach (var url in LatestVersionUrls)
        {
            try
            {
                var text = await client.GetStringAsync(url, cancellationToken);
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("tag_name", out var tag) &&
                    tag.GetString()?.TrimStart('v', 'V') is { Length: > 0 } version)
                {
                    return version;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or JsonException)
            {
                AppLog.Write("core", $"Не удалось проверить обновление через {new Uri(url).Host}", exception);
            }
        }

        return null;
    }

    /// <summary>
    /// Скачивает и подменяет ядро указанной версией. Ядро не должно быть запущено:
    /// на Windows занятый exe не перезаписать.
    /// </summary>
    public static async Task UpdateAsync(string version, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"vienlink-sing-box-{version}.zip");
        Exception? lastError = null;
        foreach (var url in DownloadUrls(version))
        {
            try
            {
                progress?.Report($"Скачивание ядра sing-box {version}…");
                await DownloadAsync(url, zipPath, progress, cancellationToken);
                ExtractExecutable(zipPath);
                await VerifyAsync(AppPaths.SingBoxPath, cancellationToken);
                WriteInstalledVersion(version);
                AppLog.Write("core", "Ядро обновлено до " + version);
                TryDelete(zipPath);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                AppLog.Write("core", $"Не удалось обновить ядро с {new Uri(url).Host}", exception);
            }
        }

        TryDelete(zipPath);
        throw new InvalidOperationException(
            "Не удалось скачать ядро " + version + " ни с GitHub, ни с зеркал.", lastError);
    }


    /// <summary>Версия установленного ядра: после обновления она отличается от встроенной константы.</summary>
    public static string InstalledVersion
    {
        get
        {
            try
            {
                var path = Path.Combine(AppPaths.CoreDirectory, "version.txt");
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path).Trim();
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Нет файла версии — считаем встроенную.
            }

            return Version;
        }
    }

    private static void WriteInstalledVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CoreDirectory);
            File.WriteAllText(Path.Combine(AppPaths.CoreDirectory, "version.txt"), version);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("core", "Не удалось записать версию ядра", exception);
        }
    }

    private static async Task DownloadAsync(string url, string destination, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = true,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VieNLink", "1.0"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long copied = 0;
        var lastReport = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            var percent = total > 0 ? (int)(copied * 100 / total) : 0;
            if (percent >= lastReport + 10)
            {
                lastReport = percent;
                progress?.Report($"Скачивание ядра: {percent}% ({copied / 1024 / 1024} МБ)");
            }
        }
    }

    private static void ExtractExecutable(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException("В архиве не найден sing-box.exe");
        }

        Directory.CreateDirectory(AppPaths.CoreDirectory);
        var temporary = AppPaths.SingBoxPath + ".tmp";
        entry.ExtractToFile(temporary, overwrite: true);
        File.Move(temporary, AppPaths.SingBoxPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Временный архив удалится позже.
        }
    }
}
