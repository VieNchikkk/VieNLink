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

    /// <summary>Сколько UDP-ассоциаций (QUIC) обслужил релей.</summary>
    public static long UdpAssociations => _forwarder?.UdpAssociations ?? 0;

    /// <summary>Сколько фейковых QUIC Initial отправлено.</summary>
    public static long FakeQuicPackets => _forwarder?.FakeQuicPackets ?? 0;

    /// <summary>Сколько байт реально пришло от видео-хостов.</summary>
    public static long VideoBytes => _forwarder?.VideoBytes ?? 0;

    /// <summary>Сколько видео-соединений отдали данные.</summary>
    public static long VideoConnections => _forwarder?.VideoConnections ?? 0;

    /// <summary>
    /// Просьба перезапустить обход после смены режима QUIC (ставит TunnelService).
    /// Так «сначала с QUIC, потом без» происходит само, без нажатий.
    /// </summary>
    public static Action? RestartRequested { get; set; }

    /// <summary>Запускает форвардер на свободном порту и возвращает его.</summary>
    public static async Task<int> StartAsync(
        StreamFragmentMode mode,
        bool fragmentAllHosts = false,
        StreamFragmentMode videoMode = StreamFragmentMode.None)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var forwarder = new TlsFragmentForwarder();
            TlsFragmentForwarder.Trace = line => AppLog.Write("tun-bypass", line);
            TlsFragmentForwarder.VideoObserved = OnVideoObserved;
            TlsFragmentForwarder.QuicTrafficObserved = OnQuicTrafficObserved;
            await forwarder.StartAsync(mode, 0, fragmentAllHosts, videoMode).ConfigureAwait(false);
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
        TlsFragmentForwarder.VideoObserved = null;
        TlsFragmentForwarder.QuicTrafficObserved = null;

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

    private static DateTime _lastVideoPresetUtc;
    private static DateTime _lastQuicPresetUtc;

    /// <summary>
    /// Реальный видео-поток — лучшее доказательство, что стратегия работает: подтверждаем
    /// её в базе операторов (не чаще раза в 5 минут, чтобы не переписывать файл на каждом
    /// соединении). Так «звук прошёл, а видео пошло» тоже попадёт в статистику.
    /// </summary>
    private static void OnVideoObserved(string host, long bytes)
    {
        if (DateTime.UtcNow - _lastVideoPresetUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastVideoPresetUtc = DateTime.UtcNow;
        try
        {
            var context = Android.App.Application.Context;
            AppLog.Write("presets",
                $"Видео реально пошло ({host}: {bytes} байт) через «{Mode}» — подтверждаю для " +
                $"«{NetworkPresets.Label(context)}»");
            ProviderPresetStore.RecordSuccess(
                NetworkPresets.Key(context),
                Mode.ToString(),
                NetworkPresets.GetVideo(context),
                NetworkPresets.GetBlockQuic(context),
                transport: "tcp");
        }
        catch (Exception exception)
        {
            AppLog.Write("presets", "Не удалось подтвердить видео-стратегию", exception);
        }
    }

    /// <summary>
    /// Реальный QUIC-трафик: если пакеты уходят, а ответов нет — QUIC режут, блокируем его
    /// (видео пойдёт по TCP). Если ответы есть — QUIC работает, разрешаем. Синтетическая
    /// проба ненадёжна (Google может не ответить на чужой Initial), поэтому решает факт.
    /// </summary>
    private static void OnQuicTrafficObserved(int uplink, int downlink)
    {
        try
        {
            var context = Android.App.Application.Context;
            var blocked = NetworkPresets.GetBlockQuic(context);
            if (downlink > 0)
            {
                if (!blocked || DateTime.UtcNow - _lastQuicPresetUtc < TimeSpan.FromMinutes(5))
                {
                    return;
                }

                _lastQuicPresetUtc = DateTime.UtcNow;
                NetworkPresets.SetBlockQuic(context, false);
                AppLog.Write("presets",
                    $"QUIC реально работает ({uplink} вверх / {downlink} вниз) — разрешаю для " +
                    $"«{NetworkPresets.Label(context)}»");
                return;
            }

            if (uplink < 20 || !blocked ||
                DateTime.UtcNow - _lastQuicPresetUtc < TimeSpan.FromMinutes(5))
            {
                return;
            }

            _lastQuicPresetUtc = DateTime.UtcNow;
            NetworkPresets.SetBlockQuic(context, true);
            AppLog.Write("presets",
                $"QUIC молчит ({uplink} пакетов вверх, 0 вниз) — блокирую, видео пойдёт по TCP; " +
                "перезапускаю обход");
            RestartRequested?.Invoke();
        }
        catch (Exception exception)
        {
            AppLog.Write("presets", "Не удалось применить решение по QUIC", exception);
        }
    }
}
