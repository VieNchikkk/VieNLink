using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>Сведения о выходе через сервер: IP, страна и провайдер.</summary>
internal sealed record ExitInfo(string Ip, string CountryCode, string Country, string City, string Org)
{
    /// <summary>Флаг страны эмодзи: две региональные буквы из ISO-кода.</summary>
    public string Flag
    {
        get
        {
            if (CountryCode.Length != 2 ||
                CountryCode[0] is < 'A' or > 'Z' ||
                CountryCode[1] is < 'A' or > 'Z')
            {
                return string.Empty;
            }

            return char.ConvertFromUtf32(0x1F1E6 + CountryCode[0] - 'A') +
                   char.ConvertFromUtf32(0x1F1E6 + CountryCode[1] - 'A');
        }
    }

    public string Short => CountryCode.Length == 0
        ? Ip
        : $"{Flag} {Ip} · {Country}";
}

/// <summary>
/// Сетевые измерения через локальный mixed-порт sing-box: реальный выходной IP,
/// страна и скорость. Прямой запрос идёт мимо VPN — процесс приложения исключён из TUN.
/// </summary>
internal static class NetworkProbe
{
    private static ExitInfo? _exit;
    private static DateTime _exitAtUtc;
    private static double? _speed;
    private static DateTime _speedAtUtc;

    // Один клиент на направление: раньше на каждый адрес создавался новый HttpClient,
    // и в памяти копились пулы соединений. Сбрасываем при смене сервера (Invalidate).
    private static readonly object ClientGate = new();
    private static HttpClient? _tunnelClient;
    private static HttpClient? _directClient;

    public static ExitInfo? CachedExit =>
        _exit is not null && DateTime.UtcNow - _exitAtUtc < TimeSpan.FromMinutes(5) ? _exit : null;

    public static double? CachedSpeed =>
        _speed is not null && DateTime.UtcNow - _speedAtUtc < TimeSpan.FromMinutes(5) ? _speed : null;

    /// <summary>Сбрасывает кэш выхода и скорости: после смены сервера это уже другие данные.</summary>
    public static void Invalidate()
    {
        _exit = null;
        _speed = null;
        _exitAtUtc = DateTime.MinValue;
        _speedAtUtc = DateTime.MinValue;
        lock (ClientGate)
        {
            _tunnelClient?.Dispose();
            _tunnelClient = null;
            _directClient?.Dispose();
            _directClient = null;
        }
    }

    private static HttpClient Client(bool throughTunnel)
    {
        lock (ClientGate)
        {
            if (throughTunnel)
            {
                return _tunnelClient ??= CreateClient(throughTunnel: true);
            }

            return _directClient ??= CreateClient(throughTunnel: false);
        }
    }

    private static HttpClient CreateClient(bool throughTunnel)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };

        if (throughTunnel)
        {
            handler.Proxy = new WebProxy("http://127.0.0.1:" + CoreRuntime.MixedPort)
            {
                BypassProxyOnLocal = false,
            };
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VieNLink/1.1");
        return client;
    }

    /// <summary>Выходной IP и страна через сервер. null — туннель не запущен или запрос не прошёл.</summary>
    public static async Task<ExitInfo?> LookupExitAsync(CancellationToken cancellationToken)
    {
        if (!CoreRuntime.IsRunning)
        {
            return null;
        }

        // Кэш свежий — в сеть не идём: экран подключения опрашивает выход по таймеру.
        if (CachedExit is { } cached)
        {
            return cached;
        }

        foreach (var url in new[]
                 {
                     "https://cp.cloudflare.com/cdn-cgi/trace",
                     "https://www.cloudflare.com/cdn-cgi/trace",
                     "https://ipinfo.io/json",
                     "https://api.myip.com",
                 })
        {
            try
            {
                var client = Client(throughTunnel: true);
                var text = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var info = text.Contains("ip=", StringComparison.Ordinal)
                    ? ParseTrace(text)
                    : Parse(text);
                if (info is not null)
                {
                    _exit = info;
                    _exitAtUtc = DateTime.UtcNow;
                    return info;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException or ObjectDisposedException)
            {
                // Сервис недоступен — пробуем следующий.
            }
        }

        return null;
    }

    /// <summary>Ответ Cloudflare cdn-cgi/trace: строки вида ip=… и loc=….</summary>
    private static ExitInfo? ParseTrace(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf('=');
            if (index > 0)
            {
                values[line[..index].Trim()] = line[(index + 1)..].Trim();
            }
        }

        if (!values.TryGetValue("ip", out var ip) || string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var code = values.TryGetValue("loc", out var loc) ? loc : string.Empty;
        return new ExitInfo(ip, code.ToUpperInvariant(), CityCountry(code), string.Empty, "Cloudflare");
    }

    private static ExitInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var ip = root.TryGetProperty("ip", out var ipValue) ? ipValue.GetString() ?? string.Empty : string.Empty;
            var code = root.TryGetProperty("country", out var countryValue)
                ? countryValue.GetString() ?? string.Empty
                : root.TryGetProperty("cc", out var ccValue)
                    ? ccValue.GetString() ?? string.Empty
                    : string.Empty;
            var city = root.TryGetProperty("city", out var cityValue) ? cityValue.GetString() ?? string.Empty : string.Empty;
            var org = root.TryGetProperty("org", out var orgValue)
                ? orgValue.GetString() ?? string.Empty
                : root.TryGetProperty("name", out var nameValue)
                    ? nameValue.GetString() ?? string.Empty
                    : string.Empty;

            if (string.IsNullOrWhiteSpace(ip))
            {
                return null;
            }

            var country = CityCountry(code);
            return new ExitInfo(ip.Trim(), code.Trim().ToUpperInvariant(), country, city, org);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Название страны на русском: общая таблица ядра.</summary>
    private static string CityCountry(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        try
        {
            return VieNLink.CountryNames.FromCode(code);
        }
        catch (Exception)
        {
            return code.ToUpperInvariant();
        }
    }

    /// <summary>Прямой IP (мимо туннеля) — чтобы показать, что сервер действительно меняет выход.</summary>
    public static async Task<string?> DirectIpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = Client(throughTunnel: false);
            var text = await client.GetStringAsync("https://api.myip.com", cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("ip", out var ip) ? ip.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Замер скорости через сервер, Мбит/с. Результат сохраняется в сервере.</summary>
    public static async Task<double?> MeasureSpeedAsync(CancellationToken cancellationToken)
    {
        if (!CoreRuntime.IsRunning)
        {
            return null;
        }

        foreach (var url in new[]
                 {
                     "https://speed.cloudflare.com/__down?bytes=1500000",
                     "https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.min.js",
                     "https://raw.githubusercontent.com/torvalds/linux/master/kernel/sched/core.c",
                     "https://speedtest.selectel.ru/1MB",
                     "https://www.youtube.com/",
                 })
        {
            try
            {
                var client = Client(throughTunnel: true);
                // Свой предел на попытку: общий HttpClient теперь переиспользуется,
                // менять его Timeout после первого запроса нельзя.
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromSeconds(20));
                var stopwatch = Stopwatch.StartNew();
                using var response = await client.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    var read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total >= 1_500_000)
                    {
                        break;
                    }
                }

                AppLog.Write("speed",
                    $"{new Uri(url).Host}: {total} байт за {stopwatch.Elapsed.TotalSeconds:0.0} с");

                if (total >= 120_000 && stopwatch.Elapsed.TotalSeconds > 0.15)
                {
                    var mbps = Math.Round(total * 8.0 / 1_000_000 / stopwatch.Elapsed.TotalSeconds, 1);
                    _speed = mbps;
                    _speedAtUtc = DateTime.UtcNow;

                    var selected = NodeStore.Selected;
                    if (selected is not null)
                    {
                        selected.SpeedMbps = mbps;
                        NodeStore.SaveNodes();
                    }

                    return mbps;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                  or OperationCanceledException or ObjectDisposedException)
            {
                // Медленный сервер или сервис недоступен — пробуем другой адрес.
                AppLog.Write("speed", $"{new Uri(url).Host}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return null;
    }
}
