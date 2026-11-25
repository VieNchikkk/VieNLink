using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace VieNLink;

/// <summary>Как резать TLS ClientHello в пользовательском TCP-потоке.</summary>
internal enum StreamFragmentMode
{
    SplitSni,
    Split1,
    MultiSplit,
    SplitSniStart,
    SplitSniMid,
    SplitRecord,

    /// <summary>Переписать ClientHello в две TLS-записи: SNI не виден в первой.</summary>
    RecordSplit,

    /// <summary>Граница записей ровно перед доменом: SNI целиком во второй записи.</summary>
    RecordSplitStart,

    /// <summary>Граница записей после домена: первая запись с полным SNI, вторая — хвост.</summary>
    RecordSplitEnd,

    /// <summary>Граница записей в середине TLS-записи, без привязки к SNI.</summary>
    RecordSplitHalf,

    /// <summary>Байт внутри ClientHello отправить как срочные данные (MSG_OOB).</summary>
    Oob,

    /// <summary>Разрез после домена: SNI целиком остаётся в первом сегменте.</summary>
    SplitSniEnd,

    /// <summary>Ступенчатый разрез: несколько мелких сегментов подряд (1, 6, 11 байт).</summary>
    SplitStagger,

    /// <summary>Разрезы перед доменом, в середине и после него — SNI размазан по сегментам.</summary>
    SplitSniBand,

    /// <summary>Разрез перед SNI с паузой: DPI успевает разобрать только начало.</summary>
    SplitSlow,

    /// <summary>Начало ClientHello уходит с TTL 1 (до сервера не доходит), хвост — сразу.</summary>
    DisorderSni,

    /// <summary>Несколько разрезов: всё, кроме последнего куска, с TTL 1 — сервер получает хвост первым.</summary>
    MultiDisorder,

    /// <summary>Поддельный ClientHello с безобидным SNI и TTL 8, затем настоящий (через splice).</summary>
    FakeSni,

    /// <summary>То же, но фейк помечен TCP MD5: сервер его отбрасывает, TTL не важен.</summary>
    FakeSniMd5,

    /// <summary>То же с TTL 4 — если DPI ближе к абоненту.</summary>
    FakeSni4,

    /// <summary>То же с TTL 2 — если DPI стоит у самого провайдера.</summary>
    FakeSni2,

    /// <summary>То же с TTL 1 — для видео-CDN GGC, который в 1–3 хопах от абонента.</summary>
    FakeSni1,

    /// <summary>Фейк SNI с TTL 3.</summary>
    FakeSni3,

    /// <summary>Фейк SNI с TTL 5.</summary>
    FakeSni5,

    /// <summary>Фейк SNI с TTL 6.</summary>
    FakeSni6,

    /// <summary>Фейк SNI с TTL 7.</summary>
    FakeSni7,

    /// <summary>Без фрагментации — контрольный режим для тестов.</summary>
    None,
}

/// <summary>
/// Описание стратегии фрагментации для TUN-обхода.
/// <paramref name="DelayMs"/> — пауза между сегментами (0 — минимальная),
/// <paramref name="Ttl"/> — TTL для «оборванных» первых сегментов (0 — обычная отправка).
/// </summary>
internal sealed record StreamFragmentStrategy(
    string Name,
    StreamFragmentMode Mode,
    string Description,
    int DelayMs = 0,
    int Ttl = 0,
    bool Md5 = false);

/// <summary>
/// Локальный SOCKS5-форвардер для TUN-обхода: принимает соединения от sing-box,
/// открывает прямой TCP-канал до сайта и режет TLS ClientHello на сегменты.
/// Никакого прокси-сервера и WinDivert: только пользовательские сокеты.
/// </summary>
internal sealed class TlsFragmentForwarder : IAsyncDisposable
{
    public static IReadOnlyList<StreamFragmentStrategy> Strategies { get; } = new List<StreamFragmentStrategy>
    {
        new("Фейк SNI + MD5", StreamFragmentMode.FakeSniMd5,
            "Поддельный ClientHello с google-SNI помечается TCP MD5: сервер сегмент отбрасывает, DPI " +
            "разбирает. TTL не важен — как «Фейк + multidisorder, TTL 64, seq настоящий» на ПК.", 0, 64, true),
        new("Фейк SNI (TTL 8)", StreamFragmentMode.FakeSni,
            "Поддельный ClientHello с google-SNI уходит с TTL 8: DPI его видит, сервер — нет; затем " +
            "настоящий ClientHello с тем же seq (очередь ядра перезаписывается). Как fake в zapret.", 0, 8),
        new("Фейк SNI (TTL 4)", StreamFragmentMode.FakeSni4,
            "То же с TTL 4 — если DPI ближе к абоненту.", 0, 4),
        new("Фейк SNI (TTL 2)", StreamFragmentMode.FakeSni2,
            "То же с TTL 2 — если DPI стоит у самого провайдера.", 0, 2),
        new("Фейк SNI (TTL 1)", StreamFragmentMode.FakeSni1,
            "То же с TTL 1 — для видео-CDN GGC (1–3 хопа): фейк умирает до узла, DPI видит безобидный SNI.", 0, 1),
        new("Фейк SNI (TTL 3)", StreamFragmentMode.FakeSni3,
            "То же с TTL 3 — перебор между «у провайдера» и «дальше».", 0, 3),
        new("Фейк SNI (TTL 5)", StreamFragmentMode.FakeSni5,
            "То же с TTL 5.", 0, 5),
        new("Фейк SNI (TTL 6)", StreamFragmentMode.FakeSni6,
            "То же с TTL 6.", 0, 6),
        new("Фейк SNI (TTL 7)", StreamFragmentMode.FakeSni7,
            "То же с TTL 7.", 0, 7),
        new("Мультиперестановка (TTL 1)", StreamFragmentMode.MultiDisorder,
            "Четыре разреза: начало, SNI, середина и хвост. Всё, кроме последнего куска, уходит с TTL 1 " +
            "и до сервера не доходит; хвост приходит первым, остальное — повторной доставкой. " +
            "Ближайший к ПК-стратегии «фейк + multidisorder» вариант без raw-сокетов.", 0, 1),
        new("Перестановка (TTL 1)", StreamFragmentMode.DisorderSni,
            "Начало ClientHello уходит с TTL 1 и не доходит, хвост от SNI — сразу. Сервер собирает " +
            "поток по seq, DPI видит хвост первым. Как --disorder 1+s в byedpi.", 0, 1),
        new("Разрез полосы SNI", StreamFragmentMode.SplitSniBand,
            "Разрезы перед доменом, в середине и после него: SNI размазан по четырём сегментам."),
        new("Медленный разрез SNI", StreamFragmentMode.SplitSlow,
            "Первый сегмент заканчивается перед доменом, пауза 40 мс — DPI разбирает только начало.", 40),
        new("Ступенчатый разрез", StreamFragmentMode.SplitStagger,
            "Три мелких сегмента подряд (1, 6 и 11 байт), затем хвост. Ломает DPI, который смотрит " +
            "только первые сегменты соединения."),
        new("Разрез после домена", StreamFragmentMode.SplitSniEnd,
            "Первый сегмент содержит SNI целиком, второй — хвост ClientHello. Проверка для DPI, " +
            "который читает только начало сегмента."),
        new("Разрез SNI", StreamFragmentMode.SplitSni,
            "ClientHello режется сразу после первого символа домена."),
        new("Разрез 1 байт", StreamFragmentMode.Split1,
            "Разрез после первого байта полезной нагрузки."),
        new("Мультиразрез", StreamFragmentMode.MultiSplit,
            "Три разреза: после 1-го байта, в середине и после первого символа SNI."),
        new("Разрез перед SNI", StreamFragmentMode.SplitSniStart,
            "Первый сегмент заканчивается прямо перед доменом, SNI остаётся во втором."),
        new("Разрез середины SNI", StreamFragmentMode.SplitSniMid,
            "Разрез в середине домена — DPI видит только половину имени."),
        new("Разрез заголовка записи", StreamFragmentMode.SplitRecord,
            "Разрез после 5 байт TLS-заголовка: DPI не видит начало handshake в первом сегменте."),
        new("Разрез TLS-записей", StreamFragmentMode.RecordSplit,
            "ClientHello переписывается в две TLS-записи: DPI должен собирать рукопожатие из записей."),
        new("Разрез записей перед SNI", StreamFragmentMode.RecordSplitStart,
            "Первая TLS-запись заканчивается перед доменом; SNI целиком во второй."),
        new("Разрез записей после SNI", StreamFragmentMode.RecordSplitEnd,
            "Граница записей после домена: проверяем DPI, который смотрит только начало записи."),
        new("Разрез записей пополам", StreamFragmentMode.RecordSplitHalf,
            "Граница записей в середине TLS-записи — без привязки к SNI."),
        new("OOB (срочный байт)", StreamFragmentMode.Oob,
            "Байт из середины ClientHello уходит как срочные данные (MSG_OOB) — часть DPI ломается на этом."),
    };

    private static readonly byte[] SocksSuccess = { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] SocksFailure = { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 };

    /// <summary>Отладочный вывод для self-test: куда идут соединения форвардера.</summary>
    internal static Action<string>? Trace { get; set; }

    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private StreamFragmentMode _mode;
    private int _delayMs;
    private int _ttl;
    private bool _md5;
    private StreamFragmentMode _videoMode = StreamFragmentMode.None;
    private int _videoDelayMs;
    private int _videoTtl;
    private bool _videoMd5;
    private bool _fragmentAllHosts;
    private long _acceptedConnections;
    private long _fragmentedConnections;
    private long _udpAssociations;
    private long _fakeQuicPackets;
    private long _videoBytes;
    private long _videoConnections;

    /// <summary>
    /// Чтобы не заливать один и тот же QUIC-сервер фейками: ключ — локальный порт
    /// и адрес назначения, окно 10 секунд (как в Windows-движке).
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _quicSeen = new();

    /// <summary>TTL, который возвращается сокету после «оборванного» сегмента.</summary>
    private const int DefaultTtl = 64;

    /// <summary>
    /// Сколько байт от медиасервера считаем настоящим потоком (подтверждение стратегии).
    /// 4 КБ — это страница-редирект, а не видео.
    /// </summary>
    private const int StreamConfirmBytes = 24 * 1024;

    private const byte SocksConnect = 1;
    private const byte SocksUdpAssociate = 3;

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public long AcceptedConnections => Interlocked.Read(ref _acceptedConnections);

    public long FragmentedConnections => Interlocked.Read(ref _fragmentedConnections);

    /// <summary>Сколько UDP-ассоциаций (QUIC) открыл sing-box.</summary>
    public long UdpAssociations => Interlocked.Read(ref _udpAssociations);

    /// <summary>Сколько фейковых QUIC Initial отправлено.</summary>
    public long FakeQuicPackets => Interlocked.Read(ref _fakeQuicPackets);

    /// <summary>Сколько байт пришло от видео-хостов (googlevideo/ytimg/ggpht).</summary>
    public long VideoBytes => Interlocked.Read(ref _videoBytes);

    /// <summary>Сколько видео-соединений реально отдали данные.</summary>
    public long VideoConnections => Interlocked.Read(ref _videoConnections);

    /// <summary>
    /// Уведомление о реальном видео-потоке (хост, байты). По нему подтверждаем стратегию
    /// в базе операторов: это доказательство, что видео действительно идёт.
    /// </summary>
    internal static Action<string, long>? VideoObserved { get; set; }

    /// <summary>
    /// Подменять QUIC Initial (фейк с безобидным SNI перед настоящим). Выключается
    /// в self-test и при диагностике: тогда UDP-релей работает как обычный SOCKS5.
    /// </summary>
    public bool QuicFakeEnabled { get; set; } = true;

    /// <param name="fragmentAllHosts">
    /// Резать любой TLS ClientHello, а не только домены из <see cref="TlsHello.TargetDomains"/>.
    /// Нужно, когда провайдер режет сайты вне встроенного списка.
    /// </param>
    /// <param name="videoMode">
    /// Отдельная стратегия для видео-CDN (<see cref="TlsHello.VideoDomains"/>); None — как основная.
    /// </param>
    public async Task StartAsync(
        StreamFragmentMode mode,
        int port = 0,
        bool fragmentAllHosts = false,
        StreamFragmentMode videoMode = StreamFragmentMode.None)
    {
        await StopAsync();
        var strategy = Strategies.FirstOrDefault(item => item.Mode == mode);
        _mode = mode;
        _delayMs = strategy?.DelayMs ?? 0;
        _ttl = strategy?.Ttl ?? 0;
        _md5 = strategy?.Md5 ?? false;

        var video = Strategies.FirstOrDefault(item => item.Mode == videoMode);
        _videoMode = videoMode;
        _videoDelayMs = video?.DelayMs ?? 0;
        _videoTtl = video?.Ttl ?? 0;
        _videoMd5 = video?.Md5 ?? false;
        _fragmentAllHosts = fragmentAllHosts;
        _acceptedConnections = 0;
        _fragmentedConnections = 0;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        AppLog.Write("tun-bypass",
            $"TCP-форвардер слушает 127.0.0.1:{Port}, режим {mode}, пауза {_delayMs} мс, TTL {_ttl}, " +
            $"видео {_videoMode}, " +
            (_fragmentAllHosts ? "все TLS-хосты" : "только целевые домены"));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                                  or SocketException)
            {
                break;
            }

            var task = HandleAsync(client, cancellationToken);
            _connections.TryAdd(task, 0);
            _ = task.ContinueWith(completed =>
            {
                _connections.TryRemove(completed, out _);
                _ = completed.Exception;
            }, TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        GCHandle? fakePin = null;
        try
        {
            using (client)
            using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                client.NoDelay = true;
                try
                {
                var clientStream = client.GetStream();
                var (host, port, command) = await ReadSocks5Async(clientStream, connection.Token)
                    .ConfigureAwait(false);
                if (command == SocksUdpAssociate)
                {
                    await UdpRelayAsync(clientStream, connection.Token).ConfigureAwait(false);
                    return;
                }

                Trace?.Invoke($"→ {host}:{port}");

                using var target = new TcpClient(AddressFamily.InterNetwork);
                target.NoDelay = true;
                try
                {
                    await target.ConnectAsync(host, port, connection.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                                      or IOException)
                {
                    await clientStream.WriteAsync(SocksFailure, connection.Token).ConfigureAwait(false);
                    return;
                }

                await clientStream.WriteAsync(SocksSuccess, connection.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _acceptedConnections);
                var targetStream = target.GetStream();

                var first = new byte[16 * 1024];
                var firstLength = await ReadFirstChunkAsync(clientStream, first, connection.Token)
                    .ConfigureAwait(false);
                string? sniHost = null;
                if (firstLength > 0)
                {
                    var chunk = await SendFirstChunkAsync(
                        targetStream, first.AsMemory(0, firstLength), connection.Token).ConfigureAwait(false);
                    fakePin = chunk.Pin;
                    sniHost = chunk.Host;
                }

                long downloadBytes = 0;
                var clientToTarget = RelayAsync(clientStream, targetStream, connection.Token);
                var targetToClient = RelayAsync(
                    targetStream, clientStream, connection.Token,
                    read => Interlocked.Add(ref downloadBytes, read));
                await Task.WhenAny(clientToTarget, targetToClient).ConfigureAwait(false);
                connection.Cancel();
                try
                {
                    await Task.WhenAll(clientToTarget, targetToClient).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException
                                                      or ObjectDisposedException or SocketException)
                {
                }

                if (sniHost is not null && TlsHello.IsVideoHost(sniHost))
                {
                    var bytes = Interlocked.Read(ref downloadBytes);
                    var stream = TlsHello.IsStreamHost(sniHost);

                    // Превью и служебные адреса видео не подтверждают: поток — это медиасервер
                    // (rr*), отдавший заметный кусок, а не 4-килобайтная страница-редирект.
                    var realStream = stream && bytes >= StreamConfirmBytes;
                    if (stream)
                    {
                        Interlocked.Add(ref _videoBytes, bytes);
                        if (realStream)
                        {
                            Interlocked.Increment(ref _videoConnections);
                        }
                    }

                    // Замер потока видео-CDN: видно, проходит ли троттлинг. 0 байт при успешном
                    // TCP-подключении — SNI-блок (DPI режет по имени rr*-узла), а не блок по IP.
                    var label = stream ? "видео" : sniHost.Contains("ytimg", StringComparison.OrdinalIgnoreCase) ||
                                          sniHost.Contains("ggpht", StringComparison.OrdinalIgnoreCase)
                        ? "превью"
                        : "медиа-редирект";
                    AppLog.Write("tun-bypass",
                        bytes == 0
                            ? $"{label} {sniHost}: 0 байт — TCP-подключение прошло, вероятен SNI-блок"
                            : $"{label} {sniHost}: {bytes} байт получено");

                    if (realStream)
                    {
                        try
                        {
                            VideoObserved?.Invoke(sniHost, bytes);
                        }
                        catch (Exception exception)
                        {
                            AppLog.Write("tun-bypass", "Обработчик видео-потока упал", exception);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                AppLog.Write("tun-bypass", "Соединение форвардера закрыто: " + exception.Message);
            }
            catch (Exception exception)
            {
                AppLog.Write("tun-bypass", "Ошибка в TCP-форвардере", exception);
            }
            }
        }
        finally
        {
            // Страницы фейка нужны ядру, пока сокет жив; после закрытия их можно отпускать.
            fakePin?.Free();
        }
    }

    private async Task<int> ReadFirstChunkAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (TlsHello.TryParseTlsClientHello(buffer.AsSpan(0, total), out _, out _))
                {
                    break;
                }

                if (total >= 5 && buffer[0] != 0x16)
                {
                    break; // не TLS — дальше обычная пересылка
                }

                if (total >= 5)
                {
                    var recordLength = (buffer[3] << 8) | buffer[4];
                    if (recordLength + 5 <= total || recordLength + 5 > buffer.Length)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Клиент не прислал данные за 1,5 с — начнём обычную пересылку.
        }

        return total;
    }

    /// <returns>
    /// Закреплённый буфер фейка: его нельзя освобождать, пока сокет жив (ядро держит страницы).
    /// </returns>
    private async Task<(GCHandle? Pin, string? Host)> SendFirstChunkAsync(
        NetworkStream target,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        // Десинк нужен только для целевых доменов (YouTube/Telegram/Discord/…), как в WinDivert-движке.
        // Чужие сайты пропускаем как есть, чтобы не ломать лишние TLS-соединения.
        var hasHello = TlsHello.TryParseTlsClientHello(data.Span, out var sniOffset, out var sniLength);
        var host = hasHello
            ? Encoding.ASCII.GetString(data.Span.Slice(sniOffset, sniLength))
            : null;
        if (hasHello && !_fragmentAllHosts && host is not null &&
            !TlsHello.IsTargetHost(host) && !TlsHello.IsVideoHost(host))
        {
            await target.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (null, host);
        }

        // Для видео-CDN (googlevideo/ytimg/ggpht) может быть отдельная стратегия:
        // у него другой маршрут и другая дистанция до DPI.
        var plan = PlanFor(host);

        // Фейк: поддельный ClientHello с безобидным SNI уходит с низким TTL, страницы буфера
        // остаются в очереди ядра и перезаписываются настоящими байтами для повторной доставки.
        if (IsFakeSni(plan.Mode) && hasHello && sniLength > 1 && plan.Ttl > 0)
        {
            var pin = TrySendFake(target, data, sniOffset, sniLength, plan);
            if (pin is not null)
            {
                Interlocked.Increment(ref _fragmentedConnections);
                Trace?.Invoke($"  первые {data.Length} байт → фейк SNI (TTL {plan.Ttl})");
                return (pin, host);
            }
        }

        // OOB: байт из середины ClientHello уходит как срочные данные, а сам ClientHello
        // продолжается обычным потоком. Часть DPI неверно собирает такие соединения.
        if (plan.Mode == StreamFragmentMode.Oob && hasHello && sniLength > 1)
        {
            var urgent = Math.Clamp(sniOffset + sniLength / 2, 1, data.Length - 1);
            if (await SendOobAsync(target, data, urgent, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _fragmentedConnections);
                Trace?.Invoke($"  первые {data.Length} байт → OOB на позиции {urgent}");
                return (null, host);
            }
        }

        // DPI, склеивающий TCP, всё равно увидит ClientHello. Разрез на уровне TLS-записей
        // требует от него собирать рукопожатие из двух записей — это умеют не все.
        if (IsRecordSplit(plan.Mode) && TrySplitTlsRecords(data.Span, plan.Mode, out var records))
        {
            Interlocked.Increment(ref _fragmentedConnections);
            Trace?.Invoke($"  первые {data.Length} байт → {records.Count} TLS-записей");
            for (var index = 0; index < records.Count; index++)
            {
                await target.WriteAsync(records[index], cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (index != records.Count - 1)
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                }
            }

            return (null, host);
        }

        var pieces = BuildStreamPieces(data.Span, plan.Mode);
        Trace?.Invoke($"  первые {data.Length} байт → {pieces.Count} кусков " +
                      $"({plan.Mode}, пауза {plan.DelayMs} мс)");
        if (pieces.Count <= 1)
        {
            await target.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (null, host);
        }

        Interlocked.Increment(ref _fragmentedConnections);

        // Порядок сегментов всегда естественный: ядро присваивает seq в порядке send.
        // «Перестановку» даёт TTL: первые куски умирают в пути, сервер получает хвост первым
        // и добирает остальное повторной доставкой — уже по правильным seq.
        var delay = plan.DelayMs > 0 ? plan.DelayMs : 1;
        for (var index = 0; index < pieces.Count; index++)
        {
            var ttl = plan.Ttl > 0 && index < pieces.Count - 1 ? plan.Ttl : 0;
            await SendPieceAsync(target, pieces[index], ttl, cancellationToken).ConfigureAwait(false);
            if (index != pieces.Count - 1)
            {
                // Пауза не даёт ядру склеить куски в один TCP-сегмент.
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        return (null, host);
    }

    private readonly record struct FragmentPlan(StreamFragmentMode Mode, int DelayMs, int Ttl, bool Md5);

    private FragmentPlan PlanFor(string? host)
    {
        if (host is not null && _videoMode != StreamFragmentMode.None && TlsHello.IsVideoHost(host))
        {
            return new FragmentPlan(_videoMode, _videoDelayMs, _videoTtl, _videoMd5);
        }

        return new FragmentPlan(_mode, _delayMs, _ttl, _md5);
    }

    /// <summary>
    /// Отправляет поддельный ClientHello (тот же размер, SNI заменён на безобидный,
    /// random и session id случайные) с TTL из стратегии. После отправки буфер
    /// перезаписывается настоящими байтами — повторная доставка понесёт уже их.
    /// </summary>
    private GCHandle? TrySendFake(
        NetworkStream target,
        ReadOnlyMemory<byte> data,
        int sniOffset,
        int sniLength,
        FragmentPlan plan)
    {
        var fake = BuildFakeHello(data.Span, sniOffset, sniLength);
        var socket = target.Socket;
        var ttlSet = TrySetTtl(socket, plan.Ttl);
        var md5Set = false;
        if (plan.Md5)
        {
            md5Set = TcpMd5.TrySet(socket, true, out var md5Error);
            if (!md5Set)
            {
                AppLog.Write("tun-bypass", "TCP MD5 недоступен: " + md5Error);
            }
        }

        GCHandle? pin;
        try
        {
            try
            {
                pin = SpliceSender.TryQueue(socket.Handle.ToInt32(), fake, fake.Length, out var error);
                if (pin is null)
                {
                    AppLog.Write("tun-bypass", "Фейк не отправлен: " + error);
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
                                                  or MarshalDirectiveException or TypeLoadException)
            {
                // В прошивке нет vmsplice/splice — работаем как обычный разрез.
                AppLog.Write("tun-bypass", "Фейк недоступен: " + exception.Message);
                pin = null;
            }
        }
        finally
        {
            if (md5Set)
            {
                TcpMd5.TrySet(socket, false, out _);
            }

            if (ttlSet)
            {
                TrySetTtl(socket, DefaultTtl);
            }
        }

        if (pin is null)
        {
            return null;
        }

        // Ядро держит страницы буфера: возвращаем в них настоящий ClientHello.
        data.Span.CopyTo(fake);
        return pin;
    }

    private static byte[] BuildFakeHello(ReadOnlySpan<byte> data, int sniOffset, int sniLength)
    {
        var fake = data.ToArray();
        var benign = TlsHello.BenignHost(sniLength);
        if (benign.Length == sniLength)
        {
            Encoding.ASCII.GetBytes(benign, fake.AsSpan(sniOffset, sniLength));
        }

        // Случайные random и session id, как fake-tls-mod=rnd в zapret:
        // DPI не должен связать фейк с настоящим ClientHello.
        if (fake.Length >= 75)
        {
            Random.Shared.NextBytes(fake.AsSpan(11, 32));
            var sessionLength = fake[43];
            if (sessionLength > 0 && 44 + sessionLength <= fake.Length)
            {
                Random.Shared.NextBytes(fake.AsSpan(44, sessionLength));
            }
        }

        return fake;
    }

    /// <summary>
    /// Отправляет кусок ClientHello. Если <paramref name="ttl"/> больше нуля, сегмент уходит
    /// с этим TTL (умирает по пути), а сокету сразу возвращается обычный TTL — иначе
    /// последующие данные тоже не дойдут до сервера.
    /// </summary>
    private static async Task SendPieceAsync(
        NetworkStream target,
        ReadOnlyMemory<byte> piece,
        int ttl,
        CancellationToken cancellationToken)
    {
        if (ttl <= 0 || !TrySetTtl(target.Socket, ttl))
        {
            await target.WriteAsync(piece, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await target.WriteAsync(piece, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TrySetTtl(target.Socket, DefaultTtl);
        }
    }

    /// <summary>Выставляет TTL для IPv4 и IPv6; на чужом семействе вариант просто игнорируется.</summary>
    private static bool TrySetTtl(Socket socket, int ttl)
    {
        var applied = false;
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
            applied = true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException
                                              or PlatformNotSupportedException)
        {
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, ttl);
            applied = true;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException
                                              or PlatformNotSupportedException)
        {
        }

        return applied;
    }

    /// <summary>Режет первый фрагмент TLS-потока; для тестов доступен отдельно.</summary>
    internal static List<byte[]> BuildStreamPieces(ReadOnlySpan<byte> data, StreamFragmentMode mode)
    {
        if (data.Length < 11 ||
            !TlsHello.TryParseTlsClientHello(data, out var sniOffset, out var sniLength))
        {
            return new List<byte[]> { data.ToArray() };
        }

        var max = data.Length - 1;
        if (max < 1)
        {
            return new List<byte[]> { data.ToArray() };
        }

        static int Clamp(int value, int max) => Math.Clamp(value, 1, max);

        var points = mode switch
        {
            StreamFragmentMode.Split1 => new[] { 1 },
            StreamFragmentMode.SplitSni or StreamFragmentMode.DisorderSni
                => new[] { Clamp(sniOffset + 1, max) },
            StreamFragmentMode.MultiSplit => new[]
            {
                Clamp(1, max),
                Clamp(sniOffset + sniLength / 2, max),
                Clamp(sniOffset + 1, max),
            }.Distinct().OrderBy(value => value).ToArray(),
            StreamFragmentMode.MultiDisorder => new[]
            {
                Clamp(1, max),
                Clamp(sniOffset + sniLength / 2, max),
                Clamp(sniOffset + sniLength, max),
            }.Distinct().OrderBy(value => value).ToArray(),
            StreamFragmentMode.SplitSniBand => new[]
            {
                Clamp(sniOffset, max),
                Clamp(sniOffset + sniLength / 2, max),
                Clamp(sniOffset + sniLength, max),
            }.Distinct().OrderBy(value => value).ToArray(),
            StreamFragmentMode.SplitStagger => new[] { 1, 6, 11 }
                .Select(value => Clamp(value, max))
                .Distinct()
                .OrderBy(value => value)
                .ToArray(),
            StreamFragmentMode.SplitSniStart or StreamFragmentMode.SplitSlow
                => new[] { Clamp(sniOffset, max) },
            StreamFragmentMode.SplitSniMid => new[] { Clamp(sniOffset + sniLength / 2, max) },
            StreamFragmentMode.SplitSniEnd => new[] { Clamp(sniOffset + sniLength, max) },
            StreamFragmentMode.SplitRecord => new[] { Clamp(5, max) },
            _ => Array.Empty<int>(),
        };
        if (points.Length == 0)
        {
            return new List<byte[]> { data.ToArray() };
        }

        var starts = new List<int> { 0 };
        starts.AddRange(points);
        var ends = new List<int>(points);
        ends.Add(data.Length);
        var result = new List<byte[]>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var length = ends[index] - starts[index];
            if (length > 0)
            {
                result.Add(data.Slice(starts[index], length).ToArray());
            }
        }

        return result;
    }

    private static bool IsRecordSplit(StreamFragmentMode mode) =>
        mode is StreamFragmentMode.RecordSplit or StreamFragmentMode.RecordSplitStart
            or StreamFragmentMode.RecordSplitEnd or StreamFragmentMode.RecordSplitHalf;

    private static bool IsFakeSni(StreamFragmentMode mode) =>
        mode is StreamFragmentMode.FakeSni or StreamFragmentMode.FakeSniMd5
            or StreamFragmentMode.FakeSni1 or StreamFragmentMode.FakeSni2 or StreamFragmentMode.FakeSni3
            or StreamFragmentMode.FakeSni4 or StreamFragmentMode.FakeSni5
            or StreamFragmentMode.FakeSni6 or StreamFragmentMode.FakeSni7;

    /// <summary>
    /// Переписывает первый TLS-ClientHello в две записи. Где проходит граница —
    /// зависит от стратегии: середина домена, перед доменом, после домена или середина записи.
    /// Сервер собирает handshake из записей, а DPI видит только часть первой записи.
    /// </summary>
    internal static bool TrySplitTlsRecords(
        ReadOnlySpan<byte> data,
        StreamFragmentMode mode,
        out List<byte[]> records)
    {
        records = new List<byte[]>();
        if (data.Length < 10 || data[0] != 0x16)
        {
            return false;
        }

        var length = (data[3] << 8) | data[4];
        if (length <= 5 || data.Length < 5 + length)
        {
            return false;
        }

        if (!TlsHello.TryParseTlsClientHello(data, out var sniOffset, out var sniLength) || sniLength < 2)
        {
            return false;
        }

        const int headerLength = 5;
        var end = headerLength + length;
        var split = mode switch
        {
            StreamFragmentMode.RecordSplit => sniOffset + sniLength / 2,
            StreamFragmentMode.RecordSplitStart => sniOffset,
            StreamFragmentMode.RecordSplitEnd => sniOffset + sniLength,
            StreamFragmentMode.RecordSplitHalf => headerLength + length / 2,
            _ => -1,
        };

        if (split <= headerLength + 1 || split >= end)
        {
            return false;
        }

        records.Add(BuildTlsRecord(data, headerLength, split));
        records.Add(BuildTlsRecord(data, split, end));
        if (data.Length > end)
        {
            records.Add(data[end..].ToArray());
        }

        return true;
    }

    private static byte[] BuildTlsRecord(ReadOnlySpan<byte> data, int from, int to)
    {
        var payload = to - from;
        var record = new byte[5 + payload];
        record[0] = 0x16;
        record[1] = data[1];
        record[2] = data[2];
        record[3] = (byte)(payload >> 8);
        record[4] = (byte)payload;
        data.Slice(from, payload).CopyTo(record.AsSpan(5));
        return record;
    }

    /// <summary>
    /// Отправляет ClientHello обычным потоком, но байт на позиции
    /// <paramref name="urgentIndex"/> — как срочные данные (MSG_OOB).
    /// Сервер OOB-байт отбрасывает, поэтому вместо него уходит заглушка 'a',
    /// а настоящий байт и остаток — следом обычным потоком.
    /// </summary>
    private static async Task<bool> SendOobAsync(
        NetworkStream target,
        ReadOnlyMemory<byte> data,
        int urgentIndex,
        CancellationToken cancellationToken)
    {
        if (urgentIndex <= 0 || urgentIndex >= data.Length)
        {
            return false;
        }

        var buffer = data.ToArray();
        var original = buffer[urgentIndex];
        buffer[urgentIndex] = (byte)'a';

        int sent;
        try
        {
            sent = target.Socket.Send(buffer, 0, urgentIndex + 1, SocketFlags.OutOfBand);
        }
        catch (Exception exception) when (exception is SocketException or NotSupportedException
                                              or PlatformNotSupportedException or ObjectDisposedException)
        {
            // Платформа не поддержала OOB — отправляем как обычно, соединение не ломаем.
            await target.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        buffer[urgentIndex] = original;

        var restart = Math.Min(sent, urgentIndex);
        if (restart < buffer.Length)
        {
            await target.WriteAsync(buffer.AsMemory(restart), cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task RelayAsync(
        NetworkStream from,
        NetworkStream to,
        CancellationToken cancellationToken,
        Action<int>? onRead = null)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                onRead?.Invoke(read);
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
                                              or ObjectDisposedException or SocketException)
        {
        }

        try
        {
            to.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task<(string Host, int Port, byte Command)> ReadSocks5Async(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        if (greeting[0] != 5)
        {
            throw new InvalidOperationException("SOCKS5: неверная версия " + greeting[0]);
        }

        var methods = new byte[greeting[1]];
        await stream.ReadExactlyAsync(methods, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);

        var request = new byte[4];
        await stream.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);
        if (request[0] != 5 || (request[1] != SocksConnect && request[1] != SocksUdpAssociate))
        {
            throw new InvalidOperationException("SOCKS5: поддерживаются только CONNECT и UDP ASSOCIATE");
        }

        string host;
        switch (request[3])
        {
            case 1:
            {
                var address = new byte[4];
                await stream.ReadExactlyAsync(address, cancellationToken).ConfigureAwait(false);
                host = new IPAddress(address).ToString();
                break;
            }
            case 3:
            {
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
                var name = new byte[length[0]];
                await stream.ReadExactlyAsync(name, cancellationToken).ConfigureAwait(false);
                host = Encoding.ASCII.GetString(name);
                break;
            }
            case 4:
            {
                var address = new byte[16];
                await stream.ReadExactlyAsync(address, cancellationToken).ConfigureAwait(false);
                host = new IPAddress(address).ToString();
                break;
            }
            default:
                throw new InvalidOperationException("SOCKS5: неизвестный тип адреса " + request[3]);
        }

        var portBytes = new byte[2];
        await stream.ReadExactlyAsync(portBytes, cancellationToken).ConfigureAwait(false);
        var port = (portBytes[0] << 8) | portBytes[1];
        // У UDP ASSOCIATE в запросе обычно 0.0.0.0:0 — адрес клиента релей узнаёт из датаграммы.
        if (request[1] != SocksUdpAssociate && port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("SOCKS5: неверный порт " + port);
        }

        return (host, port, request[1]);
    }

    /// <summary>
    /// UDP ASSOCIATE: sing-box присылает QUIC (UDP/443) сюда. Релей открывает свой UDP-сокет,
    /// шлёт перед настоящим Initial фейковый (безобидный SNI, малый TTL) и возвращает ответы
    /// клиенту в формате SOCKS5 UDP. Без фейка QUIC-блок по SNI не обойти: TLS-фрагментация
    /// работает только по TCP.
    /// </summary>
    private async Task UdpRelayAsync(NetworkStream control, CancellationToken cancellationToken)
    {
        using var association = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        local.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        upstream.Bind(new IPEndPoint(IPAddress.Any, 0));

        var relay = (IPEndPoint)local.LocalEndPoint!;
        var reply = new byte[10];
        reply[0] = 5;
        reply[1] = 0;
        reply[2] = 0;
        reply[3] = 1;
        relay.Address.GetAddressBytes().CopyTo(reply, 4);
        reply[8] = (byte)(relay.Port >> 8);
        reply[9] = (byte)relay.Port;
        await control.WriteAsync(reply, cancellationToken).ConfigureAwait(false);

        var associationIndex = Interlocked.Increment(ref _udpAssociations);
        Trace?.Invoke($"UDP ASSOCIATE → 127.0.0.1:{relay.Port}");
        if (associationIndex <= 20)
        {
            // Первые ассоциации — в журнал; дальше их может быть много, хватит счётчика в UI.
            AppLog.Write("tun-bypass", $"QUIC: UDP-ассоциация №{associationIndex}, релей 127.0.0.1:{relay.Port}" +
                                       (QuicFakeEnabled && QuicFake.IsAvailable ? string.Empty : ", фейк выключен"));
        }

        // Адрес клиента узнаём из первой датаграммы (в запросе sing-box обычно 0.0.0.0:0).
        var clientEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        var clientToUpstream = ClientToUpstreamAsync(local, upstream, clientEndPoint, association);
        var upstreamToClient = UpstreamToClientAsync(local, upstream, clientEndPoint, association);
        var controlWatch = WaitForCloseAsync(control, association);
        await Task.WhenAny(clientToUpstream, upstreamToClient, controlWatch).ConfigureAwait(false);
        association.Cancel();
        try
        {
            await Task.WhenAll(clientToUpstream, upstreamToClient, controlWatch).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
                                              or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ClientToUpstreamAsync(
        Socket local,
        Socket upstream,
        IPEndPoint clientEndPoint,
        CancellationTokenSource association)
    {
        var cancellationToken = association.Token;
        var buffer = new byte[65535];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await local.ReceiveFromAsync(buffer, SocketFlags.None, clientEndPoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                                  or ObjectDisposedException)
            {
                break;
            }

            if (received.ReceivedBytes <= 0)
            {
                continue;
            }

            // Адрес клиента для ответов: ReceiveFromAsync не обязан обновлять переданный
            // IPEndPoint, поэтому копируем из результата явно.
            if (received.RemoteEndPoint is IPEndPoint sender)
            {
                clientEndPoint.Address = sender.Address;
                clientEndPoint.Port = sender.Port;
            }

            if (!TryParseUdpHeader(buffer.AsSpan(0, received.ReceivedBytes), out var host, out var port,
                    out var offset))
            {
                continue;
            }

            IPEndPoint? destination;
            try
            {
                destination = await ResolveAsync(host, port, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (destination is null || destination.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var payload = buffer.AsMemory(offset, received.ReceivedBytes - offset);
            try
            {
                await SendUdpAsync(upstream, destination, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                                  or ObjectDisposedException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task UpstreamToClientAsync(
        Socket local,
        Socket upstream,
        IPEndPoint clientEndPoint,
        CancellationTokenSource association)
    {
        var cancellationToken = association.Token;
        var buffer = new byte[65535];
        var header = new byte[22];
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await upstream.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                                  or ObjectDisposedException)
            {
                break;
            }

            if (received.ReceivedBytes <= 0 || clientEndPoint.Port == 0)
            {
                continue;
            }

            var headerLength = BuildUdpHeader((IPEndPoint)received.RemoteEndPoint, header);
            var packet = new byte[headerLength + received.ReceivedBytes];
            header.AsSpan(0, headerLength).CopyTo(packet);
            buffer.AsSpan(0, received.ReceivedBytes).CopyTo(packet.AsSpan(headerLength));
            try
            {
                await local.SendToAsync(packet, SocketFlags.None, clientEndPoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException
                                                  or ObjectDisposedException)
            {
                break;
            }
        }
    }

    private static async Task WaitForCloseAsync(NetworkStream control, CancellationTokenSource association)
    {
        var buffer = new byte[256];
        try
        {
            while (await control.ReadAsync(buffer, association.Token).ConfigureAwait(false) > 0)
            {
                // После UDP ASSOCIATE управляющий канал данных не несёт: ждём закрытия.
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException
                                              or ObjectDisposedException or SocketException)
        {
        }
    }

    /// <summary>
    /// Отправляет датаграмму вверх. Перед первым QUIC Initial к этому назначению — серия
    /// фейковых Initial с безобидным SNI и малым TTL.
    /// </summary>
    private async Task SendUdpAsync(
        Socket upstream,
        IPEndPoint destination,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (QuicFakeEnabled && QuicFake.IsAvailable && QuicFake.IsInitial(payload.Span))
        {
            SendQuicFake(upstream, destination);
        }

        await upstream.SendToAsync(payload, SocketFlags.None, destination, cancellationToken)
            .ConfigureAwait(false);
    }

    private void SendQuicFake(Socket upstream, IPEndPoint destination)
    {
        var key = $"{upstream.LocalEndPoint}-{destination}";
        var now = DateTime.UtcNow;
        if (_quicSeen.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(10))
        {
            return;
        }

        if (_quicSeen.Count > 512)
        {
            _quicSeen.Clear();
        }

        _quicSeen[key] = now;
        var template = QuicFake.Template.Span;
        var ttlSet = TrySetTtl(upstream, QuicFake.Ttl);
        var sent = 0;
        try
        {
            for (var index = 0; index < QuicFake.Repeats; index++)
            {
                try
                {
                    upstream.SendTo(template, SocketFlags.None, destination);
                    sent++;
                }
                catch (SocketException)
                {
                    break;
                }
            }
        }
        finally
        {
            if (ttlSet)
            {
                TrySetTtl(upstream, DefaultTtl);
            }
        }

        Interlocked.Add(ref _fakeQuicPackets, sent);
        Trace?.Invoke($"QUIC: фейк Initial ×{sent} (TTL {QuicFake.Ttl}) → {destination}");
    }

    private static bool TryParseUdpHeader(
        ReadOnlySpan<byte> datagram, out string host, out int port, out int offset)
    {
        host = string.Empty;
        port = 0;
        offset = 0;
        if (datagram.Length < 10 || datagram[0] != 0 || datagram[1] != 0)
        {
            return false;
        }

        if (datagram[2] != 0)
        {
            return false; // FRAG != 0: сборку фрагментов SOCKS5 не поддерживаем
        }

        switch (datagram[3])
        {
            case 1:
                host = new IPAddress(datagram.Slice(4, 4)).ToString();
                port = (datagram[8] << 8) | datagram[9];
                offset = 10;
                break;
            case 3:
            {
                var length = datagram[4];
                if (length == 0 || datagram.Length < 5 + length + 2)
                {
                    return false;
                }

                host = Encoding.ASCII.GetString(datagram.Slice(5, length));
                port = (datagram[5 + length] << 8) | datagram[6 + length];
                offset = 7 + length;
                break;
            }
            case 4:
                if (datagram.Length < 22)
                {
                    return false;
                }

                host = new IPAddress(datagram.Slice(4, 16)).ToString();
                port = (datagram[20] << 8) | datagram[21];
                offset = 22;
                break;
            default:
                return false;
        }

        return port is > 0 and <= 65535 && offset < datagram.Length;
    }

    private static int BuildUdpHeader(IPEndPoint remote, Span<byte> header)
    {
        header[0] = 0;
        header[1] = 0;
        header[2] = 0;
        var address = remote.Address.GetAddressBytes();
        if (remote.AddressFamily == AddressFamily.InterNetwork)
        {
            header[3] = 1;
            address.CopyTo(header[4..]);
            header[8] = (byte)(remote.Port >> 8);
            header[9] = (byte)remote.Port;
            return 10;
        }

        header[3] = 4;
        address.CopyTo(header[4..]);
        header[20] = (byte)(remote.Port >> 8);
        header[21] = (byte)remote.Port;
        return 22;
    }

    private static async Task<IPEndPoint?> ResolveAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return new IPEndPoint(address, port);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken)
                .ConfigureAwait(false);
            return addresses.Length > 0 ? new IPEndPoint(addresses[0], port) : null;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        _cts = null;
        var listener = _listener;
        _listener = null;
        var acceptLoop = _acceptLoop;
        _acceptLoop = null;

        cts?.Cancel();
        listener?.Stop();

        var connections = _connections.Keys.ToArray();
        if (connections.Length > 0)
        {
            try
            {
                await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException
                                                  or IOException or SocketException)
            {
            }
        }

        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException
                                                  or IOException or SocketException)
            {
            }
        }

        cts?.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
