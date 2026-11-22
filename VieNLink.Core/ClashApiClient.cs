using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VieNLink;

/// <summary>
/// Клиент Clash API, который поднимает сам sing-box: проверка задержки сервера, переключение
/// selector, поток статистики трафика и версия ядра.
/// </summary>
internal sealed class ClashApiClient : IDisposable
{
    public const string TestUrl = "https://www.gstatic.com/generate_204";

    private readonly HttpClient _http;

    /// <summary>Отдельный клиент для потока /traffic: у обычного клиента таймаут оборвал бы поток.</summary>
    private readonly HttpClient _streamHttp;

    public ClashApiClient(int port, string secret)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, // локальный API нельзя гнать через системный прокси
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            // Локальный API отвечает мгновенно; 30 с держали очередь проверки,
            // когда ядро зависало на одном сервере.
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        var streamHandler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        _streamHttp = new HttpClient(streamHandler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _streamHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("version", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Задержка сервера в миллисекундах или null, если сервер не ответил.</summary>
    public async Task<int?> TestDelayAsync(string tag, int timeoutMs, CancellationToken cancellationToken, string? testUrl = null)
    {
        var url = $"proxies/{Uri.EscapeDataString(tag)}/delay?timeout={timeoutMs}&url={Uri.EscapeDataString(testUrl ?? TestUrl)}";
        // Свой предел на запрос: ядро обязано ответить за timeoutMs, иначе один
        // зависший сервер держал бы слот партии и проверка «замирала» на счётчике.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 10000) + 2500));
        try
        {
            using var response = await _http.GetAsync(url, limit.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: limit.Token);
            if (document.RootElement.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out var value) && value > 0)
            {
                return value;
            }

            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task SelectAsync(string selectorTag, string tag, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { name = tag });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync($"proxies/{Uri.EscapeDataString(selectorTag)}", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Какой сервер выбран в selector сейчас; null — API не ответил.</summary>
    public async Task<string?> GetSelectedAsync(string selectorTag, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"proxies/{Uri.EscapeDataString(selectorTag)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.String
                ? now.GetString()
                : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Версия запущенного ядра, например 1.14.4.</summary>
    public async Task<string?> VersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("version", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Поток /traffic: sing-box присылает по строке JSON с байтами за последнюю секунду.
    /// Возвращается, когда поток закрылся или запрос отменён.
    /// </summary>
    public async Task StreamTrafficAsync(Action<long, long> onSample, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "traffic");
        using var response = await _streamHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("up", out var up) &&
                    document.RootElement.TryGetProperty("down", out var down) &&
                    up.TryGetInt64(out var upBytes) &&
                    down.TryGetInt64(out var downBytes))
                {
                    onSample(upBytes, downBytes);
                }
            }
            catch (JsonException)
            {
                // Служебная строка потока — пропускаем.
            }
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _streamHttp.Dispose();
    }
}

/// <summary>Один отсчёт статистики: байты в секунду и накопленный итог за сессию.</summary>
internal readonly record struct TrafficSample(long UpBytesPerSecond, long DownBytesPerSecond, long TotalUp, long TotalDown);
