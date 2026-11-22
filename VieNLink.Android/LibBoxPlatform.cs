using Libbox;

namespace VieNLink.Mobile;

/// <summary>
/// Реализация <c>libbox.PlatformInterface</c>: ядро вызывает эти методы, когда ему нужны
/// возможности платформы. Нам реально нужны два: <see cref="OpenTun"/> (отдать fd от
/// VpnService) и <see cref="AutoDetectInterfaceControl"/> (защитить сокеты от петли).
/// Остальное — заглушки: соответствующие функции (Tailscale, SSH, уведомления ядра)
/// в приложении не используются.
/// </summary>
internal sealed class LibBoxPlatform : Java.Lang.Object, IPlatformInterface
{
    private LocalDnsTransport? _dns;

    /// <summary>
    /// Платформа одна на процесс. TUN и protect берутся у текущей службы
    /// (<see cref="TunnelService.Active"/>): в режиме проверки серверов её нет, TUN не запрашивается.
    /// </summary>
    public LibBoxPlatform()
    {
    }

    public LibBoxPlatform(IntPtr handle, Android.Runtime.JniHandleOwnership transfer) : base(handle, transfer)
    {
        // Конструктор нужен для Java-обёртки; состояние берётся из TunnelService.Active.
    }

    public INetworkInterfaceIterator? Interfaces =>
        new NetworkInterfaceIterator(NetworkInterfaceIterator.Snapshot());

    /// <summary>Сокеты ядра не должны уходить в собственный TUN — иначе петля.</summary>
    public void AutoDetectInterfaceControl(int fd) => TunnelService.Active?.ProtectSocket(fd);

    public void CancelNotification(string? tag, int id)
    {
    }

    public void CheckPlatformShell()
    {
    }

    public void ClearDNSCache()
    {
    }

    public void CloseNeighborMonitor(INeighborUpdateListener? listener)
    {
    }

    public IBridgeSession? CreateBridge(BridgeOptions? options) => null;

    /// <summary>
    /// Ядро спрашивает владельца соединения при обработке DNS из TUN и разыменовывает
    /// результат — возвращать null нельзя (паника Go). Правил по процессам у нас нет,
    /// поэтому отдаём сведения о собственном процессе.
    /// </summary>
    public ConnectionOwner? FindConnectionOwner(
        int ipProtocol,
        string? sourceAddress,
        int sourcePort,
        string? destinationAddress,
        int destinationPort)
    {
        return new ConnectionOwner
        {
            UserId = Android.OS.Process.MyUid(),
            UserName = string.Empty,
            ProcessPath = "unknown",
        };
    }

    public bool IncludeAllNetworks() => false;

    public ILocalDNSTransport? LocalDNSTransport() => _dns ??= new LocalDnsTransport();

    public string? LookupSFTPServer() => null;

    public PlatformUser? LookupUser(string? username) => null;

    public IShellSession? OpenShellSession(
        PlatformUser? user,
        string? command,
        IStringIterator? args,
        string? terminal,
        int width,
        int height) => null;

    public int OpenTun(ITunOptions? options) => TunnelService.Active?.OpenTun(options) ?? -1;

    public string? ReadSystemSSHHostKey() => null;

    public WIFIState? ReadWIFIState() => null;

    public void RegisterMyInterface(string? name)
    {
    }

    public void SendNotification(Libbox.Notification? notification)
    {
    }

    public void StartDefaultInterfaceMonitor(IInterfaceUpdateListener? listener)
        => TunnelService.Active?.StartDefaultInterfaceMonitor(listener);

    public void CloseDefaultInterfaceMonitor(IInterfaceUpdateListener? listener)
        => TunnelService.Active?.CloseDefaultInterfaceMonitor(listener);

    public void StartNeighborMonitor(INeighborUpdateListener? listener)
    {
    }

    public string? TailscaleHostname() => null;

    public bool UnderNetworkExtension() => false;

    /// <summary>Пускаем определение внешнего интерфейса через наш protect(fd).</summary>
    public bool UsePlatformAutoDetectInterfaceControl() => true;

    public bool UsePlatformBridge() => false;

    public bool UsePlatformShell() => false;
}
