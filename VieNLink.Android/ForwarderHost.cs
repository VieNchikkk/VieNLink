namespace VieNLink.Mobile;

/// <summary>
/// Держит <see cref="TlsFragmentForwarder"/> внутри процесса приложения.
/// Сокеты форвардера не попадают в собственный TUN: процесс исключён из VPN
/// через <c>addDisallowedApplication</c>, поэтому прямой канал не замыкается.
/// </summary>
internal static class ForwarderHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TlsFragmentForwarder? _forwarder;

    public static bool IsRunning => _forwarder?.IsRunning == true;

    public static int Port => _forwarder?.Port ?? 0;

    public static StreamFragmentMode Mode { get; private set; }

    public static long AcceptedConnections => _forwarder?.AcceptedConnections ?? 0;

    public static long FragmentedConnections => _forwarder?.FragmentedConnections ?? 0;

    /// <summary>Запускает форвардер на свободном порту и возвращает его.</summary>
    public static async Task<int> StartAsync(StreamFragmentMode mode)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var forwarder = new TlsFragmentForwarder();
            TlsFragmentForwarder.Trace = line => AppLog.Write("tun-bypass", line);
            await forwarder.StartAsync(mode).ConfigureAwait(false);
            _forwarder = forwarder;
            Mode = mode;
            return forwarder.Port;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task StopAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task StopCoreAsync()
    {
        var forwarder = _forwarder;
        _forwarder = null;
        TlsFragmentForwarder.Trace = null;

        if (forwarder is null)
        {
            return;
        }

        try
        {
            await forwarder.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Write("tun-bypass", "Не удалось остановить форвардер", exception);
        }
    }
}
