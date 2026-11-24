using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace VieNLink;

/// <summary>
/// Поиск, запуск и установка VPN-приложения VieNLink. Браузер — отдельная установка,
/// поэтому при первом запуске он проверяет, есть ли VPN, и предлагает поставить его.
/// </summary>
internal static class VpnInstaller
{
    private const string Repository = "VieNchikkk/VieNLink";
    private const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string ExeName = "VieNLink.exe";

    private static readonly string[] ApiUrls =
    {
        $"https://api.github.com/repos/{Repository}/releases?per_page=30",
        $"https://ghfast.top/https://api.github.com/repos/{Repository}/releases?per_page=30",
        $"https://gh-proxy.com/https://api.github.com/repos/{Repository}/releases?per_page=30",
    };

    private static readonly string[] DownloadPrefixes =
    {
        string.Empty,
        "https://ghfast.top/",
        "https://gh-proxy.com/",
    };

    private static string? _cachedExe;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>Минимальная версия VPN, умеющая локальный канал для панели браузера.</summary>
    public static readonly Version MinimumVersion = new(1, 3, 47);

    public static bool IsProcessRunning
    {
        get
        {
            try
            {
                return Process.GetProcessesByName("VieNLink").Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Установленная версия VPN старее той, что умеет отвечать браузеру.</summary>
    public static bool IsOutdated(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return false;
        }

        try
        {
            var text = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            return Version.TryParse(text, out var version) && version < MinimumVersion;
        }
        catch (Exception exception)
        {
            AppLog.Write("vpn", "Не удалось прочитать версию установленного VieNLink", exception);
            return false;
        }
    }

    /// <summary>Путь к установленному VieNLink.exe или null, если VPN не установлен.</summary>
    public static string? FindInstalledExe()
    {
        if (_cachedExe is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30))
        {
            return _cachedExe;
        }

        foreach (var candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                _cachedExe = candidate;
                _cachedAt = DateTime.UtcNow;
                return candidate;
            }
        }

        var fromRegistry = FindInRegistry();
        _cachedExe = fromRegistry;
        _cachedAt = DateTime.UtcNow;
        return fromRegistry;
    }

    public static void ResetCache() => _cachedAt = DateTime.MinValue;

    private static IEnumerable<string> Candidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return Path.Combine(AppContext.BaseDirectory, ExeName);
        yield return Path.Combine(local, "Programs", "VieNLink", ExeName);
        yield return Path.Combine(programs, "VieNLink", ExeName);
        yield return Path.Combine(programsX86, "VieNLink", ExeName);
    }

    private static string? FindInRegistry()
    {
        var roots = new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (root, path) in roots)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (var name in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(name);
                    if (app is null)
                    {
                        continue;
                    }

                    var display = app.GetValue("DisplayName") as string ?? string.Empty;
                    if (!display.StartsWith("VieNLink", StringComparison.OrdinalIgnoreCase) ||
                        display.Contains("Browser", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var location = app.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        var exe = Path.Combine(location, ExeName);
                        if (File.Exists(exe))
                        {
                            return exe;
                        }
                    }

                    var icon = app.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(icon))
                    {
                        var exe = icon.Split(',')[0].Trim('"');
                        if (File.Exists(exe) && Path.GetFileName(exe).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                        {
                            return exe;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("vpn", "Не удалось прочитать список установленных программ", exception);
            }
        }

        return null;
    }

    public static bool Launch(string? exe = null)
    {
        exe ??= FindInstalledExe();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = "--minimized" });
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write("vpn", "Не удалось запустить VieNLink", exception);
            return false;
        }
    }

    /// <summary>Установщик последней версии VPN: сначала пробуем GitHub API, затем зеркала.</summary>
    public static async Task<string?> FindInstallerUrlAsync(CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink-Browser");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        foreach (var url in ApiUrls)
        {
            try
            {
                var json = await client.GetStringAsync(url, token);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                string? bestUrl = null;
                Version? bestVersion = null;
                foreach (var release in document.RootElement.EnumerateArray())
                {
                    if (!release.TryGetProperty("assets", out var assets) ||
                        assets.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString() ?? string.Empty
                            : string.Empty;
                        if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var versionText = name.Replace("VieNLink-", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .Replace("-setup.exe", string.Empty, StringComparison.OrdinalIgnoreCase);
                        if (!Version.TryParse(versionText, out var version))
                        {
                            continue;
                        }

                        if (bestVersion is not null && version <= bestVersion)
                        {
                            continue;
                        }

                        bestVersion = version;
                        bestUrl = asset.TryGetProperty("browser_download_url", out var download)
                            ? download.GetString()
                            : null;
                    }
                }

                if (!string.IsNullOrWhiteSpace(bestUrl))
                {
                    return bestUrl;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or JsonException)
            {
                AppLog.Write("vpn", "Не удалось получить список релизов VieNLink", exception);
            }
        }

        // API может быть недоступен или упёрться в лимит — берём тег из atom-ленты релизов.
        foreach (var atomUrl in new[]
                 {
                     $"https://github.com/{Repository}/releases.atom",
                     $"https://ghfast.top/https://github.com/{Repository}/releases.atom",
                     $"https://gh-proxy.com/https://github.com/{Repository}/releases.atom",
                 })
        {
            try
            {
                var xml = await client.GetStringAsync(atomUrl, token);
                var match = System.Text.RegularExpressions.Regex.Match(
                    xml, "/tag/(v\\d+\\.\\d+\\.\\d+(-beta)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var tag = match.Groups[1].Value;
                var version = tag.TrimStart('v', 'V').Replace("-beta", string.Empty, StringComparison.OrdinalIgnoreCase);
                return $"https://github.com/{Repository}/releases/download/{tag}/VieNLink-{version}-setup.exe";
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                AppLog.Write("vpn", "Не удалось прочитать atom-ленту релизов VieNLink", exception);
            }
        }

        return null;
    }

    /// <summary>Скачивает установщик VPN во временную папку и возвращает путь к нему.</summary>
    public static async Task<string?> DownloadInstallerAsync(
        string url,
        IProgress<double>? progress,
        CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink-Browser");

        var target = Path.Combine(Path.GetTempPath(), "VieNLink-setup-download.exe");
        Exception? lastError = null;
        foreach (var prefix in DownloadPrefixes)
        {
            try
            {
                using var response = await client.GetAsync(
                    prefix + url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(target);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;
                    if (total > 0)
                    {
                        progress?.Report((double)copied / total);
                    }
                }

                if (total > 0 && copied != total)
                {
                    throw new InvalidDataException("Загрузка установщика оборвалась");
                }

                return target;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or IOException)
            {
                lastError = exception;
                AppLog.Write("vpn", "Скачивание установщика VieNLink не удалось", exception);
            }
        }

        AppLog.Write("vpn", "Установщик VieNLink не скачан", lastError);
        return null;
    }

    /// <summary>Запускает установщик Inno Setup в тихом режиме и ждёт завершения.</summary>
    public static bool RunInstaller(string path)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(path)
            {
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(TimeSpan.FromMinutes(10));
            ResetCache();
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write("vpn", "Установщик VieNLink не запустился", exception);
            return false;
        }
    }

    public static string ReleasesUrl => ReleasesPage;
}
