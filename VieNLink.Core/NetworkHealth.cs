using System.Net.Http;

namespace VieNLink;

/// <summary>
/// Проверка прямого интернета без прокси: отличает обрыв сети от «все серверы мертвы».
/// Используется откатом проверки и быстрым TCP-отсевом.
/// </summary>
internal static class NetworkHealth
{
    /// <summary>
    /// Несколько адресов вместо одного: если Cloudflare недоступен напрямую, ещё не значит,
    /// что интернета нет. Проверяем параллельно и верим первому ответившему.
    /// </summary>
    private static readonly string[] ProbeUrls =
    {
        "https://cp.cloudflare.com/generate_204",
        "https://connectivitycheck.gstatic.com/generate_204",
        "https://www.google.com/generate_204",
    };

    public static async Task<bool> HasDirectInternetAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

        // Общий предел на все адреса: при обрыве сети не ждём три таймаута подряд.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));

        var pending = ProbeUrls
            .Select(url => ProbeAsync(client, url, timeout.Token))
            .ToList();
        try
        {
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);
                if (await finished.ConfigureAwait(false))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена пользователем или общий таймаут — интернет не подтверждён.
        }

        return false;
    }

    private static async Task<bool> ProbeAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or OperationCanceledException
                or ObjectDisposedException)
        {
            return false;
        }
    }
}
