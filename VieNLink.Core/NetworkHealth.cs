using System.Net.Http;

namespace VieNLink;

/// <summary>
/// Проверка прямого интернета без прокси: отличает обрыв сети от «все серверы мертвы».
/// Используется откатом проверки и быстрым TCP-отсевом.
/// </summary>
internal static class NetworkHealth
{
    public static async Task<bool> HasDirectInternetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(4),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync(
                "https://cp.cloudflare.com/generate_204",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }
}
