using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace VieNLink;

/// <summary>Страна и IP, через которые пользователь выходит в интернет.</summary>
internal sealed record ExitInfo(string Ip, string Country, string CountryCode)
{
    /// <summary>Подпись для шапки: «Нидерланды · 1.2.3.4».</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(Country)
            ? Ip
            : Country + (string.IsNullOrWhiteSpace(Ip) ? string.Empty : " · " + Ip);
}

/// <summary>
/// Определяет выходной IP и страну через тот же локальный прокси, что и браузер.
/// Пользователю важно видеть, куда его выпускает случайный бесплатный узел,
/// а заодно это ловит узлы, которые врут про свою локацию.
/// </summary>
internal static class ExitInfoService
{
    public static async Task<ExitInfo?> LookupAsync(int proxyPort, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink/1.0");

        var full = await TryLookupAsync(client, "https://ipwho.is/", cancellationToken)
                   ?? await TryLookupAsync(client, "https://ifconfig.co/json", cancellationToken)
                   ?? await TryLookupAsync(client, "https://ipapi.co/json/", cancellationToken);
        if (full is not null)
        {
            return full;
        }

        // Запасной вариант: только IP, страну оставляем пустой.
        try
        {
            var ip = (await client.GetStringAsync("https://api.ipify.org?format=json", cancellationToken)).Trim();
            using var document = JsonDocument.Parse(ip);
            var address = document.RootElement.TryGetProperty("ip", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(address) ? null : new ExitInfo(address!, string.Empty, string.Empty);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                             or JsonException)
        {
            return null;
        }
    }

    private static async Task<ExitInfo?> TryLookupAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            var text = await client.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                return null;
            }

            var ip = TryGetString(root, "ip");
            var country = TryGetString(root, "country") ?? TryGetString(root, "country_name");
            var code = TryGetString(root, "country_code") ?? TryGetString(root, "country_iso");
            return string.IsNullOrWhiteSpace(ip)
                ? null
                : new ExitInfo(ip!, country ?? string.Empty, code ?? string.Empty);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                             or JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Интеграция с Windows, для которой нужны права администратора или внешние утилиты.</summary>
internal static class WindowsIntegration
{
    /// <summary>
    /// Приложения из Microsoft Store не видят прокси на 127.0.0.1 из-за изоляции loopback.
    /// CheckNetIsolation снимает это ограничение — иначе «в браузере работает, в приложении нет».
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnableStoreAppLoopbackAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("CheckNetIsolation.exe", "LoopbackExempt -is")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "Не удалось запустить CheckNetIsolation.exe");
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var text = (output + " " + error).Trim();
            return process.ExitCode == 0
                ? (true, "Приложения Магазина теперь видят локальный прокси.")
                : (false, "CheckNetIsolation вернул код " + process.ExitCode + ". " + text);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return (false, "Не удалось выполнить CheckNetIsolation: " + exception.Message);
        }
    }
}
