using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VieNLink;

/// <summary>Как резать TLS ClientHello в пользовательском TCP-потоке.</summary>
internal enum StreamFragmentMode
{
    SplitSni,
    Split1,
    DisorderSni,
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

    /// <summary>Без фрагментации — контрольный режим для тестов.</summary>
    None,
}

/// <summary>Описание стратегии фрагментации для TUN-обхода.</summary>
internal sealed record StreamFragmentStrategy(string Name, StreamFragmentMode Mode, string Description);

/// <summary>
/// Локальный SOCKS5-форвардер для TUN-обхода: принимает соединения от sing-box,
/// открывает прямой TCP-канал до сайта и режет TLS ClientHello на сегменты.
/// Никакого прокси-сервера и WinDivert: только пользовательские сокеты.
/// </summary>
internal sealed class TlsFragmentForwarder : IAsyncDisposable
{
    public static IReadOnlyList<StreamFragmentStrategy> Strategies { get; } = new List<StreamFragmentStrategy>
    {        new("Разрез SNI", StreamFragmentMode.SplitSni,
            "ClientHello режется сразу после первого символа домена."),
        new("Разрез 1 байт", StreamFragmentMode.Split1,
            "Разрез после первого байта полезной нагрузки."),
        new("Перестановка SNI", StreamFragmentMode.DisorderSni,
            "Вторая половина ClientHello уходит первой — DPI не собирает SNI."),
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
    private long _acceptedConnections;
    private long _fragmentedConnections;

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public long AcceptedConnections => Interlocked.Read(ref _acceptedConnections);

    public long FragmentedConnections => Interlocked.Read(ref _fragmentedConnections);

    public async Task StartAsync(StreamFragmentMode mode, int port = 0)
    {
        await StopAsync();
        _mode = mode;
        _acceptedConnections = 0;
        _fragmentedConnections = 0;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
        AppLog.Write("tun-bypass", $"TCP-форвардер слушает 127.0.0.1:{Port}, режим {mode}");
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
        using (client)
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            client.NoDelay = true;
            try
            {
                var clientStream = client.GetStream();
                var (host, port) = await ReadSocks5Async(clientStream, connection.Token).ConfigureAwait(false);
                Trace?.Invoke($"→ {host}:{port}");

                using var target = new TcpClient();
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
                if (firstLength > 0)
                {
                    await SendFirstChunkAsync(targetStream, first.AsMemory(0, firstLength), connection.Token)
                        .ConfigureAwait(false);
                }

                var clientToTarget = RelayAsync(clientStream, targetStream, connection.Token);
                var targetToClient = RelayAsync(targetStream, clientStream, connection.Token);
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

    private async Task SendFirstChunkAsync(NetworkStream target, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // Десинк нужен только для целевых доменов (YouTube/Telegram/Discord/…), как в WinDivert-движке.
        // Чужие сайты пропускаем как есть, чтобы не ломать лишние TLS-соединения.
        var hasHello = TlsHello.TryParseTlsClientHello(data.Span, out var sniOffset, out var sniLength);
        if (hasHello)
        {
            var host = Encoding.ASCII.GetString(data.Span.Slice(sniOffset, sniLength));
            if (!TlsHello.IsTargetHost(host))
            {
                await target.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // OOB: байт из середины ClientHello уходит как срочные данные, а сам ClientHello
        // продолжается обычным потоком. Часть DPI неверно собирает такие соединения.
        if (_mode == StreamFragmentMode.Oob && hasHello && sniLength > 1)
        {
            var urgent = Math.Clamp(sniOffset + sniLength / 2, 1, data.Length - 1);
            if (await SendOobAsync(target, data, urgent, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _fragmentedConnections);
                Trace?.Invoke($"  первые {data.Length} байт → OOB на позиции {urgent}");
                return;
            }
        }

        // DPI, склеивающий TCP, всё равно увидит ClientHello. Разрез на уровне TLS-записей
        // требует от него собирать рукопожатие из двух записей — это умеют не все.
        if (IsRecordSplit(_mode) && TrySplitTlsRecords(data.Span, _mode, out var records))
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

            return;
        }

        var pieces = BuildStreamPieces(data.Span, _mode);
        Trace?.Invoke($"  первые {data.Length} байт → {pieces.Count} кусков ({_mode})");
        if (pieces.Count <= 1)
        {
            await target.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _fragmentedConnections);

        if (_mode == StreamFragmentMode.DisorderSni)
        {
            for (var index = pieces.Count - 1; index >= 0; index--)
            {
                await target.WriteAsync(pieces[index], cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (index > 0)
                {
                    // Для перестановки пауза нужна больше: иначе ядро склеит куски
                    // в один сегмент, и байты ClientHello придут в неверном порядке.
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        for (var index = 0; index < pieces.Count; index++)
        {
            await target.WriteAsync(pieces[index], cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (index != pieces.Count - 1)
            {
                // Небольшая пауза не даёт ядру склеить куски в один TCP-сегмент.
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
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
            StreamFragmentMode.SplitSniStart => new[] { Clamp(sniOffset, max) },
            StreamFragmentMode.SplitSniMid => new[] { Clamp(sniOffset + sniLength / 2, max) },
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

    private static async Task RelayAsync(NetworkStream from, NetworkStream to, CancellationToken cancellationToken)
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

    private static async Task<(string Host, int Port)> ReadSocks5Async(NetworkStream stream, CancellationToken cancellationToken)
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
        if (request[0] != 5 || request[1] != 1)
        {
            throw new InvalidOperationException("SOCKS5: поддерживается только CONNECT");
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
        if (port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("SOCKS5: неверный порт " + port);
        }

        return (host, port);
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
