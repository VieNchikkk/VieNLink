using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VieNLink;

/// <summary>Тип секрета MTProto-прокси: он же определяет протокол на проводе.</summary>
internal enum MtProtoSecretKind
{
    /// <summary><c>ee</c> + ключ + домен: поддельный TLS (FakeTLS) и padded intermediate.</summary>
    FakeTls,

    /// <summary><c>dd</c> + ключ: старый «защищённый» режим без TLS.</summary>
    PaddedIntermediate,

    /// <summary>16 байт без префикса: старый abridged.</summary>
    Abridged,
}

/// <summary>
/// Разобранный секрет: ключ, домен для FakeTLS и режим. Секреты приходят в hex,
/// base64 и base64url, с префиксом <c>ee</c>/<c>dd</c> и без него.
/// </summary>
internal sealed class MtProtoSecret
{
    private const int KeyLength = 16;

    public required byte[] Key { get; init; }
    public required MtProtoSecretKind Kind { get; init; }
    public string Host { get; init; } = string.Empty;
    public string Raw { get; init; } = string.Empty;

    public static MtProtoSecret? TryParse(string? text)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var bytes = TryHex(raw) ?? TryBase64(raw);
        if (bytes is null || bytes.Length < KeyLength)
        {
            return null;
        }

        // Префикс — это первый байт секрета, а не буквы: «ee»/«dd» в hex-записи
        // разбираются в тот же байт 0xee/0xdd, что и base64url-вариант.
        if (bytes[0] == 0xee && bytes.Length >= KeyLength + 1)
        {
            var host = Encoding.ASCII.GetString(bytes, KeyLength + 1, bytes.Length - KeyLength - 1)
                .TrimEnd('\0')
                .Trim();
            if (!IsValidHost(host))
            {
                return null;
            }

            return new MtProtoSecret
            {
                Key = bytes[1..(1 + KeyLength)],
                Kind = MtProtoSecretKind.FakeTls,
                Host = host,
                Raw = raw,
            };
        }

        if (bytes[0] == 0xdd && bytes.Length >= KeyLength + 1)
        {
            return new MtProtoSecret
            {
                Key = bytes[1..(1 + KeyLength)],
                Kind = MtProtoSecretKind.PaddedIntermediate,
                Raw = raw,
            };
        }

        return new MtProtoSecret
        {
            Key = bytes[..KeyLength],
            Kind = MtProtoSecretKind.Abridged,
            Raw = raw,
        };
    }

    /// <summary>Домен фронтинга обязан быть настоящим: иначе Telegram такой прокси не примет.</summary>
    public static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > 253 || host.Contains(':'))
        {
            return false;
        }

        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static byte[]? TryHex(string text)
    {
        if (text.Length < 2 || text.Length % 2 != 0)
        {
            return null;
        }

        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return null;
            }
        }

        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? TryBase64(string text)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Результат проверки MTProto-прокси. «Пинг» — время подключения к самому прокси
/// (как в Telegram), а рукопожатие отдельно подтверждает, что прокси действительно работает.
/// </summary>
internal sealed class MtProtoProbeResult
{
    /// <summary>Время TCP-подключения к прокси, мс; null — прокси не работает.</summary>
    public int? DelayMs { get; init; }

    /// <summary>Сколько заняло полное рукопожатие с Telegram (для журнала и диагностики).</summary>
    public int HandshakeMs { get; init; }

    /// <summary>TCP-соединение установилось: прокси мог просто не успеть ответить.</summary>
    public bool TcpConnected { get; init; }

    public string? Error { get; init; }

    public bool Alive => DelayMs is not null;
}

/// <summary>
/// Настоящая проверка MTProto-прокси: FakeTLS-рукопожатие, obfuscated2 и запрос
/// <c>req_pq_multi</c> к Telegram. Прокси считается рабочим только если Telegram ответил
/// <c>resPQ</c>. Простой TCP-коннект ничего не доказывает: открытый порт с давно мёртвым
/// или чужим прокси тоже «отвечает», из-за чего Telegram потом отказывается его добавлять.
/// </summary>
internal static class MtProtoProbe
{
    public const int DefaultTimeoutMs = 7000;

    /// <summary>Проверяет прокси и возвращает пинг подключения (как в Telegram) и итог рукопожатия.</summary>
    public static async Task<MtProtoProbeResult> CheckAsync(
        string server,
        int port,
        string secretText,
        int timeoutMs,
        CancellationToken cancellationToken,
        Action<string>? trace = null)
    {
        var secret = MtProtoSecret.TryParse(secretText);
        if (secret is null || port is <= 0 or > 65535 || string.IsNullOrWhiteSpace(server))
        {
            trace?.Invoke("секрет не разбирается или адрес некорректен");
            return new MtProtoProbeResult { Error = "секрет не разбирается" };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(1000, timeoutMs));
        var token = timeout.Token;
        var connectWatch = new Stopwatch();
        var tcpConnected = false;
        try
        {
            // DNS — отдельно: Telegram показывает пинг уже подключённого прокси,
            // и медленное разрешение имени не должно раздувать задержку.
            var host = server.Trim();
            var dnsWatch = Stopwatch.StartNew();
            var address = await ResolveAsync(host, token).ConfigureAwait(false);
            if (address is null)
            {
                trace?.Invoke($"DNS: адрес {host} не найден за {dnsWatch.ElapsedMilliseconds} мс");
                return new MtProtoProbeResult { Error = "DNS: адрес не найден" };
            }

            if (!IPAddress.TryParse(host, out _))
            {
                trace?.Invoke($"DNS: {host} → {address} за {dnsWatch.ElapsedMilliseconds} мс");
            }

            connectWatch.Start();
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            tcpConnected = true;
            var connectMs = (int)connectWatch.ElapsedMilliseconds;
            trace?.Invoke($"TCP подключён за {connectMs} мс (это и есть пинг, как в Telegram)");

            var handshakeWatch = Stopwatch.StartNew();
            await using var stream = client.GetStream();
            var wire = new MtProtoWire(stream, secret, token, trace);
            var alive = await wire.CheckAsync().ConfigureAwait(false);
            var handshakeMs = (int)handshakeWatch.ElapsedMilliseconds;
            if (!alive)
            {
                return new MtProtoProbeResult
                {
                    TcpConnected = true,
                    HandshakeMs = handshakeMs,
                    Error = "Telegram не ответил resPQ",
                };
            }

            return new MtProtoProbeResult
            {
                DelayMs = connectMs,
                TcpConnected = true,
                HandshakeMs = handshakeMs,
            };
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or EndOfStreamException or
                OperationCanceledException or CryptographicException or ObjectDisposedException)
        {
            trace?.Invoke("ошибка: " + exception.GetType().Name + ": " + exception.Message);
            return new MtProtoProbeResult
            {
                TcpConnected = tcpConnected,
                HandshakeMs = (int)connectWatch.ElapsedMilliseconds,
                Error = exception.GetType().Name + ": " + exception.Message,
            };
        }
    }

    /// <summary>
    /// Пинг «как в Telegram»: только TCP-подключение к прокси, без DNS и без рукопожатия.
    /// Три попытки и минимум: одна медленная попытка не должна показывать ложные 1000+ мс.
    /// </summary>
    public static async Task<int?> MeasurePingAsync(
        string server,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        int? best = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var ping = await MeasurePingOnceAsync(
                    server, port, Math.Min(timeoutMs, 2000), cancellationToken)
                .ConfigureAwait(false);
            if (ping is null)
            {
                continue;
            }

            if (best is null || ping.Value < best.Value)
            {
                best = ping;
            }
        }

        return best;
    }

    private static async Task<int?> MeasurePingOnceAsync(
        string server,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Max(500, timeoutMs));
        var token = timeout.Token;
        try
        {
            var address = await ResolveAsync(server.Trim(), token).ConfigureAwait(false);
            if (address is null)
            {
                return null;
            }

            var watch = Stopwatch.StartNew();
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(address, port, token).ConfigureAwait(false);
            return (int)watch.ElapsedMilliseconds;
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Разрешает имя один раз; для IP-адресов DNS не нужен.</summary>
    private static async Task<IPAddress?> ResolveAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        return addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault();
    }

    /// <summary>Поток MTProto-прокси: TLS-обёртка FakeTLS, obfuscated2 и проверка ответа.</summary>
    private sealed class MtProtoWire
    {
        private const int ObfuscatedHeaderLength = 64;
        private const int ObfuscatedKeyOffset = 8;
        private const int ObfuscatedKeyLength = 32;
        private const int ObfuscatedIvOffset = ObfuscatedKeyOffset + ObfuscatedKeyLength;
        private const int ObfuscatedIvLength = 16;
        private const int ObfuscatedTagOffset = ObfuscatedIvOffset + ObfuscatedIvLength;

        private static readonly byte[] CcsRecord = { 0x14, 0x03, 0x03, 0x00, 0x01, 0x01 };

        private readonly Stream _stream;
        private readonly MtProtoSecret _secret;
        private readonly CancellationToken _token;
        private readonly Action<string>? _trace;
        private AesCtr? _encrypt;
        private AesCtr? _decrypt;
        private bool _firstRecord = true;
        private readonly bool _tls;

        public MtProtoWire(Stream stream, MtProtoSecret secret, CancellationToken token, Action<string>? trace)
        {
            _stream = stream;
            _secret = secret;
            _token = token;
            _trace = trace;
            _tls = secret.Kind == MtProtoSecretKind.FakeTls;
        }

        public async Task<bool> CheckAsync()
        {
            if (_tls)
            {
                await FakeTlsHandshakeAsync().ConfigureAwait(false);
            }

            await SendObfuscatedHeaderAsync().ConfigureAwait(false);
            await SendFrameAsync(BuildReqPqMulti()).ConfigureAwait(false);
            return await ReadResPqAsync().ConfigureAwait(false);
        }

        /// <summary>FakeTLS: Chrome-подобный ClientHello с меткой времени и проверка ответа сервера.</summary>
        private async Task FakeTlsHandshakeAsync()
        {
            var hello = BuildClientHello(_secret, out var clientRandom);
            _trace?.Invoke($"ClientHello: {hello.Length} байт, домен {_secret.Host}");
            await _stream.WriteAsync(hello, _token).ConfigureAwait(false);

            var response = new List<byte>(4096);
            var header = new byte[5];
            await ReadExactAsync(header).ConfigureAwait(false);
            if (header[0] != 0x16)
            {
                _trace?.Invoke("ответ сервера: " + Convert.ToHexString(header));
                throw new IOException("FakeTLS: сервер не прислал ServerHello");
            }

            response.AddRange(header);
            response.AddRange(await ReadRecordPayloadAsync(header).ConfigureAwait(false));

            var ccs = new byte[CcsRecord.Length];
            await ReadExactAsync(ccs).ConfigureAwait(false);
            if (!ccs.AsSpan().SequenceEqual(CcsRecord))
            {
                throw new IOException("FakeTLS: сервер нарушил порядок рукопожатия");
            }

            response.AddRange(ccs);

            var applicationHeader = new byte[5];
            await ReadExactAsync(applicationHeader).ConfigureAwait(false);
            if (applicationHeader[0] != 0x17)
            {
                throw new IOException("FakeTLS: нет подтверждения рукопожатия");
            }

            response.AddRange(applicationHeader);
            response.AddRange(await ReadRecordPayloadAsync(applicationHeader).ConfigureAwait(false));

            // Сервер кладёт в random HMAC нашего ClientHello: так секрет проверяется
            // до единого байта MTProto. Неверный ключ — сразу отказ.
            var packet = response.ToArray();
            var serverRandom = packet.AsSpan(11, 32).ToArray();
            packet.AsSpan(11, 32).Clear();

            var signed = new byte[clientRandom.Length + packet.Length];
            clientRandom.CopyTo(signed, 0);
            packet.CopyTo(signed, clientRandom.Length);
            var computed = HMACSHA256.HashData(_secret.Key, signed);
            if (!CryptographicOperations.FixedTimeEquals(computed, serverRandom))
            {
                throw new CryptographicException("FakeTLS: сервер не подтвердил секрет");
            }

            _trace?.Invoke($"FakeTLS: сервер подтвердил секрет (домен {_secret.Host})");
        }

        /// <summary>Obfuscated2: 64-байтный заголовок, ключ которого смешан с секретом прокси.</summary>
        private async Task SendObfuscatedHeaderAsync()
        {
            var header = new byte[ObfuscatedHeaderLength];
            while (true)
            {
                RandomNumberGenerator.Fill(header);
                if (header[0] == 0xef)
                {
                    continue;
                }

                var first = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (first is 0x44414548 or 0x54534f50 or 0x20544547 or 0x4954504f or
                    0xdddddddd or 0xeeeeeeee or 0x02010316)
                {
                    continue;
                }

                if ((header[4] | header[5] | header[6] | header[7]) == 0)
                {
                    continue;
                }

                break;
            }

            if (_secret.Kind == MtProtoSecretKind.Abridged)
            {
                header[ObfuscatedTagOffset] = 0xef;
                header[ObfuscatedTagOffset + 1] = 0xef;
                header[ObfuscatedTagOffset + 2] = 0xef;
                header[ObfuscatedTagOffset + 3] = 0xef;
            }
            else
            {
                header[ObfuscatedTagOffset] = 0xdd;
                header[ObfuscatedTagOffset + 1] = 0xdd;
                header[ObfuscatedTagOffset + 2] = 0xdd;
                header[ObfuscatedTagOffset + 3] = 0xdd;
            }

            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(ObfuscatedTagOffset + 4), 2);

            var plainHeader = (byte[])header.Clone();
            var reversed = new byte[48];
            for (var i = 0; i < reversed.Length; i++)
            {
                reversed[i] = plainHeader[55 - i];
            }

            _encrypt = new AesCtr(
                MixKey(plainHeader, ObfuscatedKeyOffset, ObfuscatedKeyLength),
                plainHeader.AsSpan(ObfuscatedIvOffset, ObfuscatedIvLength));
            _decrypt = new AesCtr(MixKey(reversed, 0, 32), reversed.AsSpan(32, 16));

            // Весь заголовок проходит через шифр, но наружу уходят только последние 8 байт:
            // так поток шифра продолжается с позиции 64, как у клиента Telegram.
            var processed = (byte[])header.Clone();
            _encrypt.Transform(processed);
            Array.Copy(processed, ObfuscatedTagOffset, header, ObfuscatedTagOffset, 8);

            await WriteRecordAsync(header).ConfigureAwait(false);
            _trace?.Invoke("obfuscated2: заголовок отправлен");
        }

        private byte[] MixKey(byte[] source, int offset, int length)
        {
            var buffer = new byte[length + _secret.Key.Length];
            Array.Copy(source, offset, buffer, 0, length);
            _secret.Key.CopyTo(buffer, length);
            return SHA256.HashData(buffer);
        }

        /// <summary>Рамка транспорта: длина (LE) + сообщение + случайное заполнение, всё зашифровано.</summary>
        private async Task SendFrameAsync(byte[] payload)
        {
            var encrypt = _encrypt ?? throw new InvalidOperationException("нет потока шифрования");
            var padding = _secret.Kind == MtProtoSecretKind.Abridged
                ? 0
                : RandomNumberGenerator.GetInt32(0, 16);
            var frame = new byte[4 + payload.Length + padding];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(payload.Length + padding));
            payload.CopyTo(frame.AsSpan(4));
            if (padding > 0)
            {
                RandomNumberGenerator.Fill(frame.AsSpan(4 + payload.Length));
            }

            encrypt.Transform(frame);
            await WriteRecordAsync(frame).ConfigureAwait(false);
            _trace?.Invoke("req_pq_multi: отправлен");
        }

        /// <summary>Читает ответ Telegram и проверяет, что это действительно resPQ.</summary>
        private async Task<bool> ReadResPqAsync()
        {
            var decrypt = _decrypt ?? throw new InvalidOperationException("нет потока расшифровки");
            var buffer = new List<byte>(256);
            while (true)
            {
                _token.ThrowIfCancellationRequested();
                byte[] chunk;
                if (_tls)
                {
                    var header = new byte[5];
                    await ReadExactAsync(header).ConfigureAwait(false);
                    if (header[0] != 0x17)
                    {
                        // Служебные записи (CCS, alert) пропускаем, как клиент Telegram.
                        await ReadRecordPayloadAsync(header).ConfigureAwait(false);
                        continue;
                    }

                    chunk = await ReadRecordPayloadAsync(header).ConfigureAwait(false);
                }
                else
                {
                    chunk = new byte[8192];
                    var read = await _stream.ReadAsync(chunk, _token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        return false;
                    }

                    if (read < chunk.Length)
                    {
                        Array.Resize(ref chunk, read);
                    }
                }

                decrypt.Transform(chunk.AsSpan());
                buffer.AddRange(chunk);

                var frame = TryTakeFrame(buffer, out var consumed);
                if (frame is null)
                {
                    if (buffer.Count > 512 * 1024)
                    {
                        return false;
                    }

                    continue;
                }

                buffer.RemoveRange(0, consumed);
                if (frame.Length < 20)
                {
                    _trace?.Invoke($"ответ: служебный кадр {frame.Length} байт");
                    continue;
                }

                var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16));
                if (bodyLength is >= 4 and <= 4096 && 20 + bodyLength <= frame.Length)
                {
                    var constructor = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20));
                    if (constructor == 0x05162463) // resPQ#05162463
                    {
                        _trace?.Invoke("ответ Telegram: resPQ получен");
                        return true;
                    }

                    _trace?.Invoke($"ответ Telegram: конструктор 0x{constructor:x8}");
                }
                else
                {
                    _trace?.Invoke($"ответ: некорректная длина тела {bodyLength}");
                }
            }
        }

        /// <summary>Достаёт одну рамку из накопленного буфера и сообщает, сколько байт она заняла.</summary>
        private byte[]? TryTakeFrame(List<byte> buffer, out int consumed)
        {
            consumed = 0;
            if (_secret.Kind == MtProtoSecretKind.Abridged)
            {
                if (buffer.Count < 1)
                {
                    return null;
                }

                var first = buffer[0];
                int words;
                if (first < 0x7f)
                {
                    words = first;
                    consumed = 1;
                }
                else
                {
                    if (buffer.Count < 4)
                    {
                        return null;
                    }

                    words = buffer[1] | (buffer[2] << 8) | (buffer[3] << 16);
                    consumed = 4;
                }

                var length = words * 4;
                if (buffer.Count < consumed + length)
                {
                    consumed = 0;
                    return null;
                }

                var frame = buffer.GetRange(consumed, length).ToArray();
                consumed += length;
                return frame;
            }

            if (buffer.Count < 4)
            {
                return null;
            }

            var packed = BinaryPrimitives.ReadUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
            if ((packed & 0x80000000u) != 0)
            {
                // Быстрое подтверждение без тела: просто пропускаем.
                consumed = 4;
                return Array.Empty<byte>();
            }

            var total = (int)packed;
            if (total < 0 || total > 1024 * 1024)
            {
                return null;
            }

            if (buffer.Count < 4 + total)
            {
                return null;
            }

            var payload = buffer.GetRange(4, total).ToArray();
            consumed = 4 + total;
            return payload;
        }

        private async Task WriteRecordAsync(ReadOnlyMemory<byte> payload)
        {
            if (!_tls)
            {
                await _stream.WriteAsync(payload, _token).ConfigureAwait(false);
                return;
            }

            if (_firstRecord)
            {
                _firstRecord = false;
                await _stream.WriteAsync(CcsRecord, _token).ConfigureAwait(false);
            }

            var header = new byte[5];
            header[0] = 0x17;
            header[1] = 0x03;
            header[2] = 0x03;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3), (ushort)payload.Length);
            await _stream.WriteAsync(header, _token).ConfigureAwait(false);
            await _stream.WriteAsync(payload, _token).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadRecordPayloadAsync(byte[] header)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
            var payload = new byte[length];
            await ReadExactAsync(payload).ConfigureAwait(false);
            return payload;
        }

        private async Task ReadExactAsync(Memory<byte> buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await _stream.ReadAsync(buffer[offset..], _token).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("соединение закрыто");
                }

                offset += read;
            }
        }
    }

    /// <summary>Стандартный AES-CTR: у .NET нет потокового режима, но он нужен обоим направлениям.</summary>
    private sealed class AesCtr : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptor;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _keystreamOffset = 16;

        public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _aes.Key = key.ToArray();
            _encryptor = _aes.CreateEncryptor();
            iv.CopyTo(_counter);
        }

        public void Transform(Span<byte> data)
        {
            var processed = 0;
            while (processed < data.Length)
            {
                if (_keystreamOffset >= 16)
                {
                    _encryptor.TransformBlock(_counter, 0, 16, _keystream, 0);
                    IncrementCounter();
                    _keystreamOffset = 0;
                }

                var take = Math.Min(16 - _keystreamOffset, data.Length - processed);
                for (var i = 0; i < take; i++)
                {
                    data[processed + i] ^= _keystream[_keystreamOffset + i];
                }

                _keystreamOffset += take;
                processed += take;
            }
        }

        private void IncrementCounter()
        {
            for (var i = _counter.Length - 1; i >= 0; i--)
            {
                if (++_counter[i] != 0)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _encryptor.Dispose();
            _aes.Dispose();
        }
    }

    private static byte[] BuildReqPqMulti()
    {
        // Нешифрованное сообщение MTProto: auth_key_id=0, msg_id, длина и req_pq_multi.
        var body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xbe7e8ef1);
        RandomNumberGenerator.Fill(body.AsSpan(4));

        var message = new byte[20 + body.Length];
        var seconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var randomPart = (uint)RandomNumberGenerator.GetInt32(int.MaxValue) & 0xFFFFFFFCu;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(8), (seconds << 32) | randomPart);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), (uint)body.Length);
        body.CopyTo(message.AsSpan(20));
        return message;
    }

    private static byte[] BuildClientHello(MtProtoSecret secret, out byte[] clientRandom)
    {
        var host = Encoding.ASCII.GetBytes(secret.Host);
        var sessionId = new byte[32];
        RandomNumberGenerator.Fill(sessionId);

        var extensions = new List<byte>();
        var sni = new byte[5 + host.Length];
        BinaryPrimitives.WriteUInt16BigEndian(sni, (ushort)(3 + host.Length));
        sni[2] = 0; // DNS hostname
        BinaryPrimitives.WriteUInt16BigEndian(sni.AsSpan(3), (ushort)host.Length);
        host.CopyTo(sni, 5);
        AddExtension(extensions, 0x0000, sni);

        AddExtension(extensions, 0x000a, new byte[] { 0x00, 0x04, 0x00, 0x1d, 0x00, 0x17 });
        AddExtension(extensions, 0x000b, new byte[] { 0x01, 0x00 });
        AddExtension(extensions, 0x000d, new byte[]
        {
            0x00, 0x14,
            0x04, 0x03, 0x08, 0x04, 0x04, 0x01, 0x05, 0x03, 0x08, 0x05,
            0x08, 0x06, 0x06, 0x01, 0x02, 0x01, 0x04, 0x01, 0x05, 0x01,
        });
        AddExtension(extensions, 0x0010, new byte[]
        {
            0x00, 0x0c, 0x02, (byte)'h', (byte)'2',
            0x08, (byte)'h', (byte)'t', (byte)'t', (byte)'p', (byte)'/', (byte)'1', (byte)'.', (byte)'1',
        });
        AddExtension(extensions, 0x0012, Array.Empty<byte>());
        AddExtension(extensions, 0x0017, Array.Empty<byte>());
        AddExtension(extensions, 0x001b, new byte[] { 0x02, 0x00, 0x02 });
        AddExtension(extensions, 0x002b, new byte[] { 0x06, 0x03, 0x04, 0x03, 0x03 });
        AddExtension(extensions, 0x002d, new byte[] { 0x01, 0x01 });
        AddExtension(extensions, 0x0033, BuildKeyShare());

        var suites = new byte[]
        {
            0x13, 0x01, 0x13, 0x02, 0x13, 0x03,
            0xc0, 0x2b, 0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30,
            0xcc, 0xa9, 0xcc, 0xa8, 0xc0, 0x13, 0xc0, 0x14,
            0x00, 0x9c, 0x00, 0x9d, 0x00, 0x2f, 0x00, 0x35,
        };

        var body = new List<byte>(256);
        body.AddRange(new byte[] { 0x03, 0x03 });
        body.AddRange(new byte[32]); // random — заполняется после HMAC
        body.Add((byte)sessionId.Length);
        body.AddRange(sessionId);
        AddUInt16(body, (ushort)suites.Length);
        body.AddRange(suites);
        body.AddRange(new byte[] { 0x01, 0x00 });
        AddUInt16(body, (ushort)extensions.Count);
        body.AddRange(extensions);

        var handshake = new List<byte>(body.Count + 4) { 0x01 };
        handshake.Add((byte)((body.Count >> 16) & 0xFF));
        handshake.Add((byte)((body.Count >> 8) & 0xFF));
        handshake.Add((byte)(body.Count & 0xFF));
        handshake.AddRange(body);

        var record = new byte[5 + handshake.Count];
        record[0] = 0x16;
        record[1] = 0x03;
        record[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3), (ushort)handshake.Count);
        handshake.CopyTo(record, 5);

        // Random = HMAC(секрет, hello с нулевым random), младшие 4 байта — метка времени.
        var digest = HMACSHA256.HashData(secret.Key, record.AsSpan());
        var timestamp = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(timestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        for (var i = 0; i < 32; i++)
        {
            record[11 + i] = i >= 28 ? (byte)(digest[i] ^ timestamp[i - 28]) : digest[i];
        }

        clientRandom = record.AsSpan(11, 32).ToArray();
        return record;
    }

    private static byte[] BuildKeyShare()
    {
        // client_shares: длина списка (2), группа x25519 (2), длина ключа (2), ключ (32).
        var data = new byte[38];
        BinaryPrimitives.WriteUInt16BigEndian(data, 36);
        data[3] = 0x1d; // x25519
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 32);
        RandomNumberGenerator.Fill(data.AsSpan(6));
        return data;
    }

    private static void AddExtension(List<byte> extensions, ushort type, byte[] data)
    {
        AddUInt16(extensions, type);
        AddUInt16(extensions, (ushort)data.Length);
        extensions.AddRange(data);
    }

    private static void AddUInt16(List<byte> target, ushort value)
    {
        target.Add((byte)(value >> 8));
        target.Add((byte)(value & 0xFF));
    }
}
