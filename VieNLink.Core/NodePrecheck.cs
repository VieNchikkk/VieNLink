using System.Net.Sockets;

namespace VieNLink;

/// <summary>
/// Быстрый TCP-отсев перед полной проверкой. Мёртвый хост:порт незачем тащить в пробное
/// ядро и ждать трёхсекундный таймаут: один TCP-опрос отсеивает такие адреса за секунды.
/// Опрос идёт по endpoint, а не по каждому аккаунту, поэтому четыре варианта на одном
/// сервере:порту не проверяются четыре раза. UDP-протоколы (Hysteria2, TUIC) и direct
/// не трогаем — для них TCP ничего не значит.
/// </summary>
internal static class NodePrecheck
{
    public const int DefaultConcurrency = 96;
    public const int DefaultTimeoutMs = 2000;

    /// <summary>Мелкие партии (ручная проверка одного сервера) не тормозим отсевом.</summary>
    public const int MinBatch = 20;

    public static bool Supports(ProxyNode node) => node.Protocol.ToLowerInvariant() switch
    {
        "vless" or "vmess" or "trojan" or "shadowsocks" or "ss" or "anytls" or "socks" or "http" => true,
        _ => false,
    };

    /// <summary>
    /// Оставляет серверы, чей host:port принял TCP-соединение. Серверы без TCP
    /// (UDP-протоколы, direct) остаются всегда. Порядок исходного списка сохраняется.
    /// </summary>
    public static async Task<List<ProxyNode>> FilterAliveAsync(
        IReadOnlyList<ProxyNode> nodes,
        int concurrency,
        int timeoutMs,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (nodes.Count < MinBatch)
        {
            return nodes.ToList();
        }

        var endpoints = new List<(string Host, int Port, List<ProxyNode> Nodes)>();
        var endpointIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!Supports(node) || string.IsNullOrWhiteSpace(node.Server) || node.Port <= 0)
            {
                continue;
            }

            var key = node.EndpointKey;
            if (!endpointIndex.TryGetValue(key, out var index))
            {
                index = endpoints.Count;
                endpointIndex[key] = index;
                endpoints.Add((node.Server, node.Port, new List<ProxyNode>()));
            }

            endpoints[index].Nodes.Add(node);
        }

        var alive = new bool[endpoints.Count];
        var aliveCount = 0;
        var done = 0;
        using var limiter = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = endpoints.Select(async (endpoint, index) =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(timeoutMs);
                await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
                alive[index] = true;
                Interlocked.Increment(ref aliveCount);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Таймаут — адрес не ответил.
            }
            catch (SocketException)
            {
                // Закрыт, не резолвится, сеть недоступна — отсеиваем.
            }
            catch (ArgumentException)
            {
                // Некорректный адрес.
            }
            finally
            {
                limiter.Release();
                var finished = Interlocked.Increment(ref done);
                if (progress is not null && (finished % 100 == 0 || finished == endpoints.Count))
                {
                    progress.Report(
                        $"TCP: проверено {finished} из {endpoints.Count}, живых {Volatile.Read(ref aliveCount)}");
                }
            }
        }).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var result = new List<ProxyNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (!Supports(node) || string.IsNullOrWhiteSpace(node.Server) || node.Port <= 0)
            {
                result.Add(node);
                continue;
            }

            if (endpointIndex.TryGetValue(node.EndpointKey, out var index) && alive[index])
            {
                result.Add(node);
            }
        }

        return result;
    }
}
