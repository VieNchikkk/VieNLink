using System.Net;
using System.Net.Sockets;

namespace VieNLink.Mobile;

/// <summary>
/// DNS-транспорт для ядра: sing-box на Android не может ходить в системный резолвер сам
/// и спрашивает платформу. Возвращать null нельзя — при первом DNS-запросе из TUN
/// Go-ядро падает.
/// </summary>
internal sealed class LocalDnsTransport : Java.Lang.Object, Libbox.ILocalDNSTransport
{
    private const string UpstreamServer = "1.1.1.1";
    private const int UpstreamPort = 53;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public LocalDnsTransport()
    {
    }

    /// <summary>Умеем отдавать сырой DNS-ответ, а не только имя → адрес.</summary>
    public bool Raw() => true;

    public void Lookup(Libbox.ExchangeContext? context, string? network, string? host)
    {
        if (context is null || string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        var preferIpv4 = string.Equals(network, "ip4", StringComparison.OrdinalIgnoreCase);
        var hostName = host;

        _ = Task.Run(async () =>
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(hostName).ConfigureAwait(false);
                var address = preferIpv4
                    ? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    : addresses.FirstOrDefault();

                if (address is null)
                {
                    context.ErrorCode(2); // ENOENT
                    return;
                }

                context.Success(address.ToString());
            }
            catch (Exception exception)
            {
                Android.Util.Log.Warn("VieNLink", "DNS lookup " + hostName + ": " + exception.Message);
                context.ErrorCode(1);
            }
        });
    }

    public void Exchange(Libbox.ExchangeContext? context, byte[]? message)
    {
        if (context is null || message is null || message.Length == 0)
        {
            return;
        }

        var query = message;

        _ = Task.Run(async () =>
        {
            var tunnel = TunnelService.Active;
            var servers = tunnel?.GetUpstreamDnsServers() ?? new List<string> { UpstreamServer };
            foreach (var server in servers)
            {
                try
                {
                    using var client = new UdpClient();

                    // Сокет обязан ходить мимо нашего же TUN: иначе запрос попадёт обратно
                    // в ядро и разрешение имени зациклится. В тестовом режиме TUN нет.
                    try
                    {
                        if (tunnel is not null)
                        {
                            tunnel.ProtectSocket(client.Client.Handle.ToInt32());
                        }
                    }
                    catch (Exception)
                    {
                        // Protect недоступен до установки туннеля — пробуем отправить как есть.
                    }

                    await client.SendAsync(query, query.Length, server, UpstreamPort).ConfigureAwait(false);

                    var receive = client.ReceiveAsync();
                    var completed = await Task.WhenAny(receive, Task.Delay(Timeout)).ConfigureAwait(false);
                    if (completed != receive)
                    {
                        continue;
                    }

                    context.RawSuccess(receive.Result.Buffer);
                    return;
                }
                catch (Exception exception)
                {
                    Android.Util.Log.Warn("VieNLink", "DNS " + server + ": " + exception.Message);
                }
            }

            context.ErrorCode(110); // ETIMEDOUT: ни один сервер не ответил
        });
    }
}
