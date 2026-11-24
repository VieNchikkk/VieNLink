using System.Net;
using System.Net.Http;

namespace VieNLink.Mobile;

/// <summary>Что подбираем: страницу YouTube или поток видео-CDN.</summary>
internal enum AutotuneScope
{
    Page,
    Video,
}

/// <summary>Результат подбора стратегии для выбранного набора доменов.</summary>
internal sealed record AutotuneResult(
    AutotuneScope Scope,
    StreamFragmentStrategy? Strategy,
    int Score,
    int Total)
{
    public bool HasAny => Strategy is not null;
}

/// <summary>
/// Подбор стратегии десинка на самом устройстве по доменам приложения YouTube.
/// Приложение исключено из VPN, поэтому запросы идут напрямую в сеть провайдера, но через
/// отдельный SOCKS5-форвардер — DPI проверяет их так же, как реальный трафик приложения.
/// Форвардер поднимается свой, на свободном порту: подбор можно делать, не ломая текущий обход.
/// </summary>
internal static class StrategyAutotune
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Домены страницы: сам сайт, API приложения и превью.
    private static readonly (string Url, bool Strict)[] PageUrls =
    {
        ("https://www.youtube.com/generate_204", true),
        ("https://youtubei.googleapis.com/generate_204", false),
        ("https://i.ytimg.com/generate_204", false),
    };

    // Домены видео: редиректор, аватары/превью и реальные видео-узлы GGC.
    private static readonly (string Url, bool Strict)[] VideoUrls =
    {
        ("https://redirector.googlevideo.com/generate_204", false),
        ("https://yt3.ggpht.com/generate_204", false),
        ("https://rr2---sn-hxb54vo-238s.googlevideo.com/generate_204", false),
        ("https://rr3---sn-gvnuxaxjvh-axqz.googlevideo.com/generate_204", false),
    };

    /// <summary>Быстрая проверка через уже работающий обход: открывается ли страница.</summary>
    public static Task<bool> CheckPageAsync(int port, CancellationToken cancellationToken) =>
        ProbeAsync(port, "https://www.youtube.com/generate_204", true, cancellationToken);

    public static async Task<AutotuneResult> RunAsync(
        AutotuneScope scope,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(scope, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<AutotuneResult> RunCoreAsync(
        AutotuneScope scope,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var urls = scope == AutotuneScope.Video ? VideoUrls : PageUrls;
        var label = scope == AutotuneScope.Video ? "видео" : "страница";

        StreamFragmentStrategy? best = null;
        var bestScore = -1;
        var total = TlsFragmentForwarder.Strategies.Count;
        var index = 0;

        foreach (var strategy in TlsFragmentForwarder.Strategies)
        {
            index++;
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report($"[{index}/{total}] {strategy.Name}…");

            var forwarder = new TlsFragmentForwarder();
            int port;
            try
            {
                await forwarder.StartAsync(strategy.Mode, 0, fragmentAllHosts: true)
                    .ConfigureAwait(false);
                port = forwarder.Port;
            }
            catch (Exception exception)
            {
                progress.Report($"{strategy.Name}: не запустился ({exception.Message})");
                await forwarder.StopAsync().ConfigureAwait(false);
                continue;
            }

            try
            {
                var score = 0;
                foreach (var probe in urls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (await ProbeAsync(port, probe.Url, probe.Strict, cancellationToken).ConfigureAwait(false))
                    {
                        score++;
                    }
                }

                progress.Report($"{strategy.Name}: {score}/{urls.Length}");
                if (score > bestScore)
                {
                    bestScore = score;
                    best = strategy;
                }

                if (score == urls.Length)
                {
                    break;
                }
            }
            finally
            {
                await forwarder.StopAsync().ConfigureAwait(false);
            }
        }

        var result = bestScore > 0 ? best : null;
        progress.Report($"{label}: {result?.Name ?? "не найдена"} " +
                        $"({Math.Max(bestScore, 0)}/{urls.Length})");
        return new AutotuneResult(scope, result, Math.Max(bestScore, 0), urls.Length);
    }

    private static async Task<bool> ProbeAsync(
        int port,
        string url,
        bool strictStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.Zero,
                AllowAutoRedirect = true,
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(6),
            };
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return strictStatus ? code is >= 200 and < 400 : code < 500;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
