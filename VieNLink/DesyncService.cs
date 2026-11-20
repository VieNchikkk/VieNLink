using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;

namespace VieNLink;

/// <summary>Как обрабатывать TLS ClientHello на TCP.</summary>
internal enum DesyncTcpMode
{
    /// <summary>Только UDP/QUIC (TCP не трогаем).</summary>
    None,

    /// <summary>Разрезать после первого байта полезной нагрузки.</summary>
    Split1,

    /// <summary>Разрезать сразу после первого символа домена в SNI.</summary>
    SplitSni,

    /// <summary>Разрезать SNI и отправить вторую половину первой.</summary>
    DisorderSni,

    /// <summary>Фейковый ClientHello с безобидным SNI, затем оригинал.</summary>
    FakeSni,

    /// <summary>Фейковый ClientHello, затем разрезанный настоящий.</summary>
    FakeSplit,

    /// <summary>Разрезать в нескольких местах: после 1-го байта, в середине SNI и после первого символа SNI.</summary>
    MultiSplit,

    /// <summary>11 фейковых ClientHello с badseq/TTL 2, затем настоящий multidisorder — как zapret.</summary>
    FakeMultiDisorder,
}

/// <summary>Описание стратегии собственного обхода DPI.</summary>
internal sealed record DesyncStrategy(
    string Name,
    DesyncTcpMode Tcp,
    bool Quic,
    string Description,
    byte FakeTtl = 2,
    bool BadSeq = true);

/// <summary>Результат проверки одной стратегии.</summary>
internal sealed record DesyncProbeResult(
    int Index,
    string Name,
    int Ok,
    int Total,
    int AverageMs,
    bool YoutubeOk,
    bool TelegramOk,
    bool VideoOk = false);

/// <summary>Тип блокировки на уровне сети/TLS.</summary>
internal enum BlockKind
{
    Ok,
    Ip,
    Tls,
    Dns,
}

/// <summary>Результат диагностики: IP-блок, SNI/TLS-блок или всё в порядке.</summary>
internal sealed record BlockDiagnosis(string Host, BlockKind Kind, string Detail, string? Address);

/// <summary>Разобранный TLS ClientHello: где лежит полезная нагрузка и SNI.</summary>
internal readonly record struct TlsClientHelloInfo(
    int PayloadOffset,
    int PayloadLength,
    int SniOffset,
    int SniLength,
    int ExtensionsLengthOffset);

/// <summary>
/// Собственный обход DPI на WinDivert: без winws.exe. Перехватывает исходящие
/// TLS ClientHello, режет их на TCP-сегменты (split/disorder) или подмешивает
/// фейковый ClientHello с безобидным SNI (fake), а для QUIC отправляет
/// поддельный Initial на google. Требует прав администратора и драйвера WinDivert.
/// </summary>
internal sealed class DesyncService : IDisposable
{
    private const int NetworkLayer = 0;
    private const int AddressSize = 128;
    private const string TcpFilter = "outbound and tcp and (tcp.DstPort == 443 or tcp.DstPort == 80)";
    private const string UdpFilter = "outbound and udp and udp.DstPort == 443";
    private const string QuicTemplateFile = "quic_initial_www_google_com.bin";

    private static readonly string[] QuicTemplateMirrors =
    {
        "https://raw.githubusercontent.com/bol-van/zapret-win-bundle/master/zapret-winws/files/" + QuicTemplateFile,
    };

    private static readonly string[] WinDivertZipUrls =
    {
        "https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip",
        "https://ghfast.top/https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip",
        "https://gh-proxy.com/https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip",
    };

    private static readonly string[] ProbeUrls =
    {
        "https://www.youtube.com/generate_204",
        "https://t.me/",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Отладочный вывод для self-test: какие пакеты видит WinDivert.</summary>
    internal static Action<string>? Trace { get; set; }

    private readonly object _sync = new();

    private WinDivertApi? _api;
    private IntPtr _tcpHandle;
    private IntPtr _udpHandle;
    private Thread? _tcpThread;
    private Thread? _udpThread;
    private volatile bool _running;
    private byte[]? _quicTemplate;
    private long _splitPackets;
    private long _fakePackets;
    private int _helloDumped;
    private bool _blockQuic;
    private byte _quicFakeTtl = 64;
    private readonly Dictionary<string, DateTime> _quicSeen = new(StringComparer.Ordinal);

    /// <summary>Сколько фейковых QUIC Initial отправлять перед настоящим — как в zapret (--dpi-desync-repeats=11).</summary>
    private const int QuicRepeats = 11;

    /// <summary>Домены, для которых включается десинк. Остальные идут напрямую.</summary>
    private static readonly string[] TargetDomains =
    {
        "youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com", "ytimg.com", "ggpht.com",
        "t.me", "telegram.org", "telegram.me",
        "discord.com", "discordapp.com", "discord.gg",
        "x.com", "twitter.com", "twimg.com",
        "instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net",
    };

    /// <summary>Десинк включается только для этих доменов (и поддоменов); остальные — напрямую.</summary>
    internal static bool IsTargetHost(string host) =>
        TargetDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                                    host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static bool IsTargetPayload(string payload) =>
        TargetDomains.Any(domain => payload.Contains(domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Находит в полезной нагрузке TCP-сегмента полное имя хоста, оканчивающееся на
    /// целевой домен. Нужно, когда ClientHello не влез в один сегмент и обычный разбор
    /// не сработал: SNI всё равно виден в первых байтах, и его надо разрезать.
    /// </summary>
    internal static bool TryLocateHost(string payload, out int offset, out int length)
    {
        static bool IsHostChar(char value) => char.IsLetterOrDigit(value) || value is '-' or '.' or '_';

        foreach (var domain in TargetDomains)
        {
            var index = payload.IndexOf(domain, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var end = index + domain.Length;
            while (end < payload.Length && IsHostChar(payload[end]))
            {
                end++;
            }

            // Имя хоста в DNS не длиннее 253 символов; заодно это не даёт
            // «съесть» соседние байты, если перед SNI случайно буква или цифра.
            var start = index;
            var lowerBound = Math.Max(0, end - 253);
            while (start > lowerBound && IsHostChar(payload[start - 1]))
            {
                start--;
            }

            offset = start;
            length = end - start;
            return true;
        }

        offset = 0;
        length = 0;
        return false;
    }

    /// <summary>
    /// Точки разреза для ClientHello, который не удалось разобрать целиком: режем SNI
    /// по границам (перед доменом, после первого символа, в середине, после домена),
    /// иначе DPI читает имя видео-CDН в одном сегменте и блокирует поток.
    /// </summary>
    internal static int[] LocateSniSplitPoints(
        string payload,
        int payloadLength,
        out int sniAt,
        out int sniLength,
        out string? host)
    {
        if (!TryLocateHost(payload, out sniAt, out sniLength))
        {
            host = null;
            return Array.Empty<int>();
        }

        host = payload.Substring(sniAt, sniLength);
        var max = payloadLength - 1;
        if (max < 1)
        {
            return Array.Empty<int>();
        }

        int Clamp(int value) => Math.Clamp(value, 1, max);
        var middle = sniAt + Math.Max(1, sniLength / 2);
        return new[]
            {
                Clamp(1),
                Clamp(5),
                Clamp(sniAt),
                Clamp(sniAt + 1),
                Clamp(middle),
                Clamp(sniAt + sniLength),
            }
            .Distinct()
            .OrderBy(point => point)
            .ToArray();
    }

    /// <summary>Стратегии по умолчанию: от самой мягкой к самой агрессивной.</summary>
    public static IReadOnlyList<DesyncStrategy> Strategies { get; } = new List<DesyncStrategy>
    {
        new("Фейк + multidisorder, TTL 128 (zapret-стиль)", DesyncTcpMode.FakeMultiDisorder, true,
            "11 фейковых ClientHello с google SNI, TTL 128, badseq и TCP MD5; затем настоящий ClientHello " +
            "рвётся на части и уходит в обратном порядке. Работает для YouTube.", 128),
        new("Фейк + multidisorder, TTL 64", DesyncTcpMode.FakeMultiDisorder, true,
            "То же, но фейки с TTL 64 — если DPI ближе.", 64),
        new("Фейк + multidisorder, TTL 64, seq настоящий", DesyncTcpMode.FakeMultiDisorder, true,
            "Фейки с TTL 64 и настоящим seq: сервер отбрасывает их из-за TCP MD5, DPI видит google SNI.", 64, false),
        new("Фейк + multidisorder, TTL 2", DesyncTcpMode.FakeMultiDisorder, true,
            "Фейки с TTL 2 — для DPI у самого провайдера.", 2),
        new("Фейк + multidisorder, TTL 4", DesyncTcpMode.FakeMultiDisorder, true,
            "То же, но фейки с TTL 4 — если DPI стоит дальше двух хопов.", 4),
        new("Фейк + multidisorder, TTL 8", DesyncTcpMode.FakeMultiDisorder, true,
            "То же, но фейки с TTL 8 — для DPI дальше по маршруту.", 8),
        new("Фейк + multidisorder (seq как у настоящего)", DesyncTcpMode.FakeMultiDisorder, true,
            "Фейки с TTL 2 и тем же seq: DPI видит безобидный SNI, сервер не получает из-за TTL.", 2, false),
        new("Разрез SNI", DesyncTcpMode.SplitSni, true,
            "ClientHello режется сразу после первого символа домена. Плюс QUIC-фейк."),
        new("Разрез 1 байт", DesyncTcpMode.Split1, true,
            "Разрез после первого байта полезной нагрузки. Плюс QUIC-фейк."),
        new("Перестановка SNI", DesyncTcpMode.DisorderSni, true,
            "Вторая половина ClientHello уходит первой — DPI не собирает SNI. Плюс QUIC-фейк."),
        new("Фейк + разрез", DesyncTcpMode.FakeSplit, true,
            "Сначала ClientHello с безобидным SNI и TTL 1, затем настоящий, разрезанный. Плюс QUIC-фейк."),
        new("Фейк SNI", DesyncTcpMode.FakeSni, false,
            "Только подмена SNI в фейковом пакете, настоящий ClientHello идёт целиком."),
        new("Мультиразрез", DesyncTcpMode.MultiSplit, true,
            "Три разреза: после 1-го байта, в середине и после первого символа SNI. Плюс QUIC-фейк."),
        new("Только QUIC-фейк", DesyncTcpMode.None, true,
            "TCP не трогаем, подменяем только QUIC Initial. Для видео YouTube, если TLS уже открывается."),
    };

    public static string Directory => AppPaths.DesyncDirectory;

    public static string DllPath => Path.Combine(Directory, "WinDivert.dll");

    public static string DriverPath => Path.Combine(Directory, "WinDivert64.sys");

    public static string QuicTemplatePath => Path.Combine(Directory, QuicTemplateFile);

    public static bool IsInstalled =>
        File.Exists(DllPath) && new FileInfo(DllPath).Length > 0 &&
        File.Exists(DriverPath) && new FileInfo(DriverPath).Length > 0;

    public bool IsRunning => _running;

    public DesyncStrategy? CurrentStrategy { get; private set; }

    public long FakePackets => Interlocked.Read(ref _fakePackets);

    public long SplitPackets => Interlocked.Read(ref _splitPackets);

    /// <summary>
    /// Скачивает WinDivert (DLL + драйвер) и шаблон QUIC-фейка. Если zapret уже
    /// установлен, готовые файлы просто копируются — второй раз не качаем.
    /// </summary>
    public static async Task EnsureAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);

        if (!IsInstalled)
        {
            progress?.Report("Копирую WinDivert из папки zapret…");
            // Портативный zapret рядом с exe, затем пользовательский %LOCALAPPDATA%\VieNLink\zapret.
            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "zapret"),
                         Path.Combine(AppPaths.DataDirectory, "zapret"),
                         ZapretService.Directory,
                     })
            {
                CopyIfExists(Path.Combine(candidate, "WinDivert.dll"), DllPath);
                CopyIfExists(Path.Combine(candidate, "WinDivert64.sys"), DriverPath);
                if (IsInstalled)
                {
                    break;
                }
            }
        }

        if (!IsInstalled)
        {
            await DownloadWinDivertAsync(progress, cancellationToken);
            if (!IsInstalled)
            {
                throw new InvalidOperationException(
                    "Не удалось скачать WinDivert. Скачайте WinDivert 2.2.2 (x64) вручную и положите " +
                    $"WinDivert.dll и WinDivert64.sys в папку {Directory}.");
            }
        }

        await EnsureQuicTemplateAsync(progress, cancellationToken);
        AppLog.Write("desync", "Движок обхода DPI готов: " + Directory);
    }

    private static void CopyIfExists(string source, string target)
    {
        try
        {
            if (!File.Exists(source) || new FileInfo(source).Length == 0)
            {
                return;
            }

            File.Copy(source, target, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("desync", $"Не удалось скопировать {Path.GetFileName(source)}", exception);
        }
    }

    private static async Task DownloadWinDivertAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var archive = Path.Combine(Path.GetTempPath(), "vienlink-windivert.zip");
        Exception? lastError = null;
        foreach (var url in WinDivertZipUrls)
        {
            try
            {
                progress?.Report("Скачиваю WinDivert…");
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = File.Create(archive))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                ExtractWinDivert(archive);
                File.Delete(archive);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                AppLog.Write("desync", $"Не удалось скачать WinDivert с {url}", exception);
            }
        }

        throw new InvalidOperationException("WinDivert не скачался: " + lastError?.Message, lastError);
    }

    private static void ExtractWinDivert(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WinDivert.dll"] = DllPath,
            ["WinDivert64.sys"] = DriverPath,
        };
        foreach (var entry in zip.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (!wanted.TryGetValue(name, out var target))
            {
                continue;
            }

            // В архиве есть x86 и x64; берём только x64.
            if (!entry.FullName.Contains("x64", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static async Task EnsureQuicTemplateAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(QuicTemplatePath) && new FileInfo(QuicTemplatePath).Length > 0)
        {
            return;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "zapret", "files", QuicTemplateFile),
                     Path.Combine(AppPaths.DataDirectory, "zapret", "files", QuicTemplateFile),
                     Path.Combine(ZapretService.Directory, "files", QuicTemplateFile),
                 })
        {
            if (File.Exists(candidate))
            {
                CopyIfExists(candidate, QuicTemplatePath);
                return;
            }
        }

        foreach (var url in QuicTemplateMirrors)
        {
            try
            {
                progress?.Report("Скачиваю шаблон QUIC-фейка…");
                using var response = await Http.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length > 200)
                {
                    await File.WriteAllBytesAsync(QuicTemplatePath, bytes, cancellationToken);
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write("desync", $"Шаблон QUIC не скачался с {url}", exception);
            }
        }
    }

    /// <summary>Запускает обработку пакетов. Бросает исключение, если нет прав или драйвера.</summary>
    public void Start(DesyncStrategy strategy, IProgress<string>? progress, bool blockQuic = false)
    {
        Stop();
        if (!IsInstalled)
        {
            throw new InvalidOperationException("WinDivert не установлен: нажмите «Установить WinDivert».");
        }

        _blockQuic = blockQuic;
        _quicFakeTtl = strategy.FakeTtl;
        _quicTemplate = strategy.Quic ? LoadQuicTemplate() : null;
        if (strategy.Quic && !blockQuic && _quicTemplate is null)
        {
            progress?.Report("Шаблон QUIC не найден — работает только TCP-часть стратегии");
        }

        var api = WinDivertApi.Load(DllPath);
        try
        {
            _api = api;
            if (strategy.Tcp != DesyncTcpMode.None)
            {
                _tcpHandle = Open(api, TcpFilter);
            }

            if (blockQuic || (strategy.Quic && _quicTemplate is not null))
            {
                _udpHandle = Open(api, UdpFilter);
            }

            if (_tcpHandle == IntPtr.Zero && _udpHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(strategy.Tcp == DesyncTcpMode.None
                    ? "Нет шаблона QUIC-фейка: переустановите WinDivert / повторите установку."
                    : "Стратегия не содержит ни TCP-, ни QUIC-обработки.");
            }

            CurrentStrategy = strategy;
            _splitPackets = 0;
            _fakePackets = 0;
            _running = true;

            AppLog.Write("desync", $"Запущен собственный обход DPI: «{strategy.Name}»");

            if (_tcpHandle != IntPtr.Zero)
            {
                var handle = _tcpHandle;
                _tcpThread = new Thread(() => RunTcp(api, handle, strategy))
                {
                    IsBackground = true,
                    Name = "VieNLink desync TCP",
                };
                _tcpThread.Start();
            }

            if (_udpHandle != IntPtr.Zero)
            {
                var handle = _udpHandle;
                _udpThread = new Thread(() => RunUdp(api, handle))
                {
                    IsBackground = true,
                    Name = "VieNLink desync QUIC",
                };
                _udpThread.Start();
            }
        }
        catch
        {
            // Stop сам закроет дескрипторы, дождётся потоков и освободит DLL.
            Stop();
            throw;
        }
    }

    private static IntPtr Open(WinDivertApi api, string filter)
    {
        var handle = api.Open(filter, NetworkLayer, 0, 0);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        throw error switch
        {
            5 => new InvalidOperationException(
                "WinDivert не открылся: нужны права администратора. Перезапустите VieNLink от имени администратора."),
            577 => new InvalidOperationException(
                "WinDivert не открылся: драйвер не прошёл проверку подписи Windows. " +
                "Проверьте целостность WinDivert64.sys или скачайте WinDivert заново."),
            2 => new InvalidOperationException(
                "WinDivert не открылся: рядом с WinDivert.dll нет WinDivert64.sys. Нажмите «Установить WinDivert»."),
            _ => new System.ComponentModel.Win32Exception(error,
                $"WinDivertOpen завершился с ошибкой {error}"),
        };
    }

    private static byte[]? LoadQuicTemplate()
    {
        try
        {
            return File.Exists(QuicTemplatePath) ? File.ReadAllBytes(QuicTemplatePath) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("desync", "Не удалось прочитать шаблон QUIC", exception);
            return null;
        }
    }

    private void RunTcp(WinDivertApi api, IntPtr handle, DesyncStrategy strategy)
    {
        var packetBuffer = Marshal.AllocHGlobal(65535);
        var addressBuffer = Marshal.AllocHGlobal(AddressSize);
        try
        {
            var zero = new byte[AddressSize];
            Marshal.Copy(zero, 0, addressBuffer, AddressSize);

            while (_running)
            {
                if (!api.Recv(handle, packetBuffer, 65535, out var length, addressBuffer))
                {
                    if (!_running)
                    {
                        break;
                    }

                    var error = Marshal.GetLastPInvokeError();
                    if (error is 232 or 995)
                    {
                        continue;
                    }

                    AppLog.Write("desync", $"WinDivert recv: ошибка {error}");
                    break;
                }

                var packet = new byte[length];
                Marshal.Copy(packetBuffer, packet, 0, (int)length);
                var address = new byte[AddressSize];
                Marshal.Copy(addressBuffer, address, 0, AddressSize);
                Trace?.Invoke($"recv {length} v{(packet.Length > 0 ? packet[0] >> 4 : 0)} " +
                              $"proto{(packet.Length > 9 ? packet[9] : -1)}");

                if (!HandleTcp(api, handle, packet, address, strategy))
                {
                    Send(api, handle, packet, address);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Write("desync", "Ошибка в TCP-движке обхода DPI", exception);
        }
        finally
        {
            Marshal.FreeHGlobal(packetBuffer);
            Marshal.FreeHGlobal(addressBuffer);
        }
    }

    private bool HandleTcp(
        WinDivertApi api,
        IntPtr handle,
        byte[] packet,
        byte[] address,
        DesyncStrategy strategy)
    {
        if (!IsExternalTcp(packet))
        {
            return false;
        }

        var parsed = TryParseClientHello(packet, out var hello);
        string host;
        if (parsed)
        {
            host = Encoding.ASCII.GetString(packet, hello.SniOffset, hello.SniLength);
        }
        else if (TryBuildPartialClientHello(packet, out hello, out host))
        {
            // ClientHello больше первого TCP-сегмента — современные шифрнаборы
            // Chrome и Windows. SNI при этом виден в полезной нагрузке, и его
            // можно резать так же, как у полностью разобранного рукопожатия.
            Trace?.Invoke($"ClientHello по частям: sni={host}");
        }
        else
        {
            // SNI не попал в этот сегмент: для zapret-стратегии остаётся слепой
            // фейк с фиксированными позициями разреза.
            if (strategy.Tcp == DesyncTcpMode.FakeMultiDisorder)
            {
                return ApplyBlindFakeMultiDisorder(api, handle, packet, address, strategy);
            }

            var tcpOffset = packet.Length >= 20 ? (packet[0] & 0x0F) * 4 : 0;
            var tcpHeader = packet.Length >= tcpOffset + 13 ? (packet[tcpOffset + 12] >> 4) * 4 : 0;
            var payloadOffset = tcpOffset + tcpHeader;
            var first = payloadOffset + 5 < packet.Length
                ? $"{packet[payloadOffset]:X2} {packet[payloadOffset + 1]:X2} {packet[payloadOffset + 5]:X2}"
                : "—";
            Trace?.Invoke($"skip len={packet.Length} v={(packet.Length > 0 ? packet[0] >> 4 : 0)} " +
                          $"proto={(packet.Length > 9 ? packet[9] : -1)} ihl={tcpOffset} thl={tcpHeader} " +
                          $"first={first} dst={(packet.Length > 19 ? $"{packet[16]}.{packet[17]}.{packet[18]}.{packet[19]}" : "?")}");
            if (payloadOffset + 5 < packet.Length && packet[payloadOffset] == 0x16 &&
                packet[payloadOffset + 5] == 0x01 && Interlocked.Exchange(ref _helloDumped, 1) == 0)
            {
                var dumpLength = Math.Min(200, packet.Length - payloadOffset);
                Trace?.Invoke("hello " + Convert.ToHexString(
                    packet.AsSpan(payloadOffset, dumpLength)));
            }

            return false;
        }

        Trace?.Invoke($"ClientHello mode={strategy.Tcp} sni={hello.SniLength} partial={!parsed}");
        if (!IsTargetHost(host))
        {
            Trace?.Invoke("skip: не целевой домен " + host);
            return false;
        }

        if (strategy.Tcp == DesyncTcpMode.FakeMultiDisorder)
        {
            var tcpOffset = (packet[0] & 0x0F) * 4;
            var sequence = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(tcpOffset + 4, 4));
            // zapret badseq: seq -= 10000 (выводит фейк из окна сервера, DPI видит).
            var fakeSequence = strategy.BadSeq ? sequence - 10000 : sequence;
            // Для неполного ClientHello длину SNI менять нельзя (неизвестны поля
            // расширений), поэтому берём безобидный домен ровно той же длины.
            var fake = BuildFakeClientHello(
                packet, hello, strategy.FakeTtl, fakeSequence,
                parsed ? "www.google.com" : null, md5Signature: true);

            // 11 фейков, как в zapret: DPI видит безобидный SNI и «пачкается».
            for (var index = 0; index < 11; index++)
            {
                if (Send(api, handle, fake, address))
                {
                    Interlocked.Increment(ref _fakePackets);
                }
            }

            var realPieces = BuildSplitPackets(packet, hello, SplitPoints(strategy.Tcp, hello, host));
            // multidisorder: каждый разрезанный сегмент отправляем 11 раз в обратном порядке.
            for (var index = realPieces.Count - 1; index >= 0; index--)
            {
                for (var repeat = 0; repeat < 11; repeat++)
                {
                    Send(api, handle, realPieces[index], address);
                }
            }

            Interlocked.Increment(ref _splitPackets);
            return true;
        }

        if (strategy.Tcp == DesyncTcpMode.FakeSni)
        {
            var fake = BuildFakeClientHello(packet, hello);
            if (Send(api, handle, fake, address))
            {
                Interlocked.Increment(ref _fakePackets);
            }

            return false; // оригинал отправляем без изменений
        }

        var splitPoints = SplitPoints(strategy.Tcp, hello);
        if (splitPoints.Length == 0)
        {
            return false;
        }

        if (strategy.Tcp == DesyncTcpMode.FakeSplit)
        {
            var fake = BuildFakeClientHello(packet, hello);
            if (Send(api, handle, fake, address))
            {
                Interlocked.Increment(ref _fakePackets);
            }
        }

        var pieces = BuildSplitPackets(packet, hello, splitPoints);
        if (pieces.Count == 0)
        {
            return false;
        }

        if (strategy.Tcp == DesyncTcpMode.DisorderSni)
        {
            for (var index = pieces.Count - 1; index >= 0; index--)
            {
                Send(api, handle, pieces[index], address);
            }
        }
        else
        {
            foreach (var piece in pieces)
            {
                Send(api, handle, piece, address);
            }
        }

        Interlocked.Increment(ref _splitPackets);
        return true; // оригинал уже не нужен
    }

    /// <summary>
    /// Zapret-подобный режим без разбора SNI: фейк — заранее собранный ClientHello
    /// с www.google.com, реальный ClientHello режется по фиксированным позициям.
    /// Нужен, когда настоящий ClientHello больше первого TCP-сегмента.
    /// </summary>
    private bool ApplyBlindFakeMultiDisorder(
        WinDivertApi api,
        IntPtr handle,
        byte[] packet,
        byte[] address,
        DesyncStrategy strategy)
    {
        if (packet.Length < 40 || (packet[0] >> 4) != 4 || packet[9] != 6)
        {
            return false;
        }

        var tcpOffset = (packet[0] & 0x0F) * 4;
        if (tcpOffset < 20 || packet.Length < tcpOffset + 20)
        {
            return false;
        }

        var tcpHeaderLength = (packet[tcpOffset + 12] >> 4) * 4;
        var payloadOffset = tcpOffset + tcpHeaderLength;
        if (tcpHeaderLength < 20 || payloadOffset + 11 > packet.Length)
        {
            return false;
        }

        if (packet[payloadOffset] != 0x16 || packet[payloadOffset + 1] != 0x03 ||
            packet[payloadOffset + 5] != 0x01)
        {
            return false;
        }

        var payloadLength = packet.Length - payloadOffset;
        var payloadText = Encoding.ASCII.GetString(packet, payloadOffset, payloadLength);
        if (!IsTargetPayload(payloadText))
        {
            Trace?.Invoke("blind skip: не целевой домен");
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(tcpOffset + 4, 4));
        var fakeSequence = strategy.BadSeq ? sequence - 10000 : sequence;
        var fakePayload = BuildMinimalGoogleClientHello();
        var fake = BuildPacketWithPayload(packet, payloadOffset, fakePayload, strategy.FakeTtl, fakeSequence);
        for (var index = 0; index < 11; index++)
        {
            if (Send(api, handle, fake, address))
            {
                Interlocked.Increment(ref _fakePackets);
            }
        }

        // ClientHello больше одного сегмента: разобрать целиком нельзя, но домен
        // в полезной нагрузке виден. Без разреза по SNI видео-CDН (googlevideo)
        // уходит к DPI целым куском и соединение блокируется.
        var points = LocateSniSplitPoints(payloadText, payloadLength, out var sniAt, out var sniLength, out var host);
        var blindSni = sniLength > 0
            ? new TlsClientHelloInfo(payloadOffset, payloadLength, payloadOffset + sniAt, sniLength, 0)
            : new TlsClientHelloInfo(payloadOffset, payloadLength, 0, 0, 0);
        if (points.Length == 0)
        {
            points = new[] { 1, 5, 10, 20, 40, 80 }
                .Where(point => point > 0 && point < payloadLength)
                .Distinct()
                .OrderBy(point => point)
                .ToArray();
        }

        Trace?.Invoke($"blind: sni={host ?? "—"} at {sniAt}, точки {string.Join(",", points)}");
        var pieces = BuildSplitPackets(packet, blindSni, points);
        for (var index = pieces.Count - 1; index >= 0; index--)
        {
            for (var repeat = 0; repeat < 11; repeat++)
            {
                Send(api, handle, pieces[index], address);
            }
        }

        Interlocked.Increment(ref _splitPackets);
        Trace?.Invoke($"blind: fake {fakePayload.Length}, pieces {pieces.Count}, payload {payloadLength}");
        return true;
    }

    private static byte[] BuildPacketWithPayload(
        byte[] packet,
        int payloadOffset,
        byte[] newPayload,
        byte ttl,
        uint sequence)
    {
        var result = new byte[payloadOffset + newPayload.Length];
        packet.AsSpan(0, payloadOffset).CopyTo(result);
        newPayload.CopyTo(result.AsSpan(payloadOffset));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2, 2), (ushort)result.Length);
        var tcpOffset = (packet[0] & 0x0F) * 4;
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(tcpOffset + 4, 4), sequence);
        result[8] = ttl;
        result[10] = 0;
        result[11] = 0;
        result[tcpOffset + 16] = 0;
        result[tcpOffset + 17] = 0;
        return AddTcpMd5Option(result, tcpOffset);
    }

    private static byte[] BuildMinimalGoogleClientHello()
    {
        const string host = "www.google.com";
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var body = new List<byte>
        {
            0x03, 0x03,
        };
        var random = new byte[32];
        Random.Shared.NextBytes(random);
        body.AddRange(random);
        body.Add(32); // session id length
        var session = new byte[32];
        Random.Shared.NextBytes(session);
        body.AddRange(session);
        body.AddRange(new byte[] { 0x00, 0x06, 0x13, 0x01, 0x13, 0x02, 0x13, 0x03 });
        body.AddRange(new byte[] { 0x01, 0x00 }); // compression

        var extensions = new List<byte>();
        var sniList = new List<byte> { 0x00, (byte)(hostBytes.Length >> 8), (byte)hostBytes.Length };
        sniList.AddRange(hostBytes);
        extensions.AddRange(new byte[] { 0x00, 0x00, (byte)(sniList.Count >> 8), (byte)sniList.Count });
        extensions.AddRange(sniList);
        extensions.AddRange(new byte[] { 0x00, 0x2b, 0x00, 0x03, 0x02, 0x03, 0x04 });
        extensions.AddRange(new byte[] { 0x00, 0x0a, 0x00, 0x04, 0x00, 0x02, 0x00, 0x1d });
        extensions.AddRange(new byte[] { 0x00, 0x0d, 0x00, 0x04, 0x00, 0x02, 0x04, 0x03 });
        body.AddRange(new byte[] { (byte)(extensions.Count >> 8), (byte)extensions.Count });
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01 };
        handshake.AddRange(new[]
        {
            (byte)(body.Count >> 16), (byte)(body.Count >> 8), (byte)body.Count,
        });
        handshake.AddRange(body);

        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.AddRange(new[] { (byte)(handshake.Count >> 8), (byte)handshake.Count });
        record.AddRange(handshake);
        return record.ToArray();
    }

    private void RunUdp(WinDivertApi api, IntPtr handle)
    {
        var packetBuffer = Marshal.AllocHGlobal(65535);
        var addressBuffer = Marshal.AllocHGlobal(AddressSize);
        try
        {
            var zero = new byte[AddressSize];
            Marshal.Copy(zero, 0, addressBuffer, AddressSize);

            while (_running)
            {
                if (!api.Recv(handle, packetBuffer, 65535, out var length, addressBuffer))
                {
                    if (!_running)
                    {
                        break;
                    }

                    var error = Marshal.GetLastPInvokeError();
                    if (error is 232 or 995)
                    {
                        continue;
                    }

                    AppLog.Write("desync", $"WinDivert recv (QUIC): ошибка {error}");
                    break;
                }

                var packet = new byte[length];
                Marshal.Copy(packetBuffer, packet, 0, (int)length);
                var address = new byte[AddressSize];
                Marshal.Copy(addressBuffer, address, 0, AddressSize);

                if (_blockQuic)
                {
                    // Не отправляем обратно — UDP/443 отбрасывается, браузер уйдёт в TCP.
                    continue;
                }

                TrySendQuicFake(api, handle, packet, address);
                Send(api, handle, packet, address);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Write("desync", "Ошибка в QUIC-движке обхода DPI", exception);
        }
        finally
        {
            Marshal.FreeHGlobal(packetBuffer);
            Marshal.FreeHGlobal(addressBuffer);
        }
    }

    private void TrySendQuicFake(WinDivertApi api, IntPtr handle, byte[] packet, byte[] address)
    {
        var template = _quicTemplate;
        if (template is null || !TryBuildQuicFake(packet, template, _quicFakeTtl, out var fake, out var key))
        {
            return;
        }

        lock (_quicSeen)
        {
            if (_quicSeen.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(10))
            {
                return;
            }

            if (_quicSeen.Count > 512)
            {
                _quicSeen.Clear();
            }

            _quicSeen[key] = DateTime.UtcNow;
        }

        // Несколько фейков, как в zapret: DPI видит безобидный google Initial несколько раз
        // и пропускает следующий за ними настоящий QUIC-рукопожатие к googlevideo.com.
        for (var index = 0; index < QuicRepeats; index++)
        {
            if (Send(api, handle, fake, address))
            {
                Interlocked.Increment(ref _fakePackets);
            }
        }
    }

    private bool Send(WinDivertApi api, IntPtr handle, byte[] packet, byte[] address)
    {
        var packetBuffer = Marshal.AllocHGlobal(packet.Length);
        var addressBuffer = Marshal.AllocHGlobal(address.Length);
        try
        {
            Marshal.Copy(packet, 0, packetBuffer, packet.Length);
            Marshal.Copy(address, 0, addressBuffer, address.Length);
            if (!api.CalcChecksums(packetBuffer, (uint)packet.Length, addressBuffer, 0))
            {
                return false;
            }

            if (!api.Send(handle, packetBuffer, (uint)packet.Length, out _, addressBuffer))
            {
                var error = Marshal.GetLastPInvokeError();
                if (_running)
                {
                    AppLog.Write("desync", $"WinDivert send: ошибка {error}");
                }

                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.Write("desync", "Не удалось отправить пакет через WinDivert", exception);
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(packetBuffer);
            Marshal.FreeHGlobal(addressBuffer);
        }
    }

    public void Stop()
    {
        var wasRunning = _running;
        _running = false;

        var api = _api;
        _api = null;
        if (api is not null)
        {
            CloseHandles(api);
        }

        // Сначала дожидаемся потоков, и только потом освобождаем DLL:
        // освободить библиотеку под работающим WinDivertRecv нельзя.
        var tcpAlive = _tcpThread is not null && !_tcpThread.Join(2000);
        var udpAlive = _udpThread is not null && !_udpThread.Join(2000);
        _tcpThread = null;
        _udpThread = null;

        if (api is not null)
        {
            if (tcpAlive || udpAlive)
            {
                AppLog.Write("desync", "Поток WinDivert не завершился за 2 с — DLL оставлена загруженной");
            }
            else
            {
                api.Dispose();
            }
        }

        if (wasRunning)
        {
            AppLog.Write("desync",
                $"Собственный обход DPI остановлен: разрезов {Interlocked.Read(ref _splitPackets)}, " +
                $"фейков {Interlocked.Read(ref _fakePackets)}");
        }

        CurrentStrategy = null;
        _tcpHandle = IntPtr.Zero;
        _udpHandle = IntPtr.Zero;
    }

    private void CloseHandles(WinDivertApi api)
    {
        lock (_sync)
        {
            if (_tcpHandle != IntPtr.Zero)
            {
                api.Close(_tcpHandle);
                _tcpHandle = IntPtr.Zero;
            }

            if (_udpHandle != IntPtr.Zero)
            {
                api.Close(_udpHandle);
                _udpHandle = IntPtr.Zero;
            }
        }
    }

    /// <summary>Последовательно перебирает стратегии и проверяет прямой доступ.</summary>
    public async Task<IReadOnlyList<DesyncProbeResult>> AutoTuneAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool blockQuic = false)
    {
        var results = new List<DesyncProbeResult>();
        for (var index = 0; index < Strategies.Count; index++)
        {
            var strategy = Strategies[index];
            // QUIC-фейк нельзя проверить обычным HTTPS-запросом — оставляем его для ручного выбора.
            if (strategy.Tcp == DesyncTcpMode.None)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Стратегия {index + 1}/{Strategies.Count}: «{strategy.Name}»…");
            try
            {
                Start(strategy, progress, blockQuic);
                await Task.Delay(250, cancellationToken);
                var result = await ProbeAsync(index, strategy.Name, cancellationToken);
                VideoProbeDetail? video = null;
                if (result.YoutubeOk)
                {
                    video = await ProbeVideoDetailAsync(cancellationToken);
                    result = result with { VideoOk = video.Ok };
                }

                results.Add(result);
                AppLog.Write("desync",
                    $"Автоподбор: {strategy.Name} — {result.Ok}/{result.Total} " +
                    $"(YouTube {(result.YoutubeOk ? "да" : "нет")}, Telegram {(result.TelegramOk ? "да" : "нет")}, " +
                    $"видео {(result.VideoOk ? "да" : "нет")}), {result.AverageMs} мс" +
                    (video is null ? "" : $" — {video.Detail}"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppLog.Write("desync", $"Стратегия «{strategy.Name}» не запустилась", exception);
                results.Add(new DesyncProbeResult(index, strategy.Name, 0, ProbeUrls.Length, 0, false, false));
            }
            finally
            {
                Stop();
            }
        }

        return results;
    }

    /// <summary>
    /// Проверяет, устанавливается ли TCP-соединение вообще. Если TCP до YouTube
    /// не открывается, а Cloudflare открывается — провайдер блокирует по IP,
    /// и обход по SNI (split/fake) здесь не поможет.
    /// </summary>
    public static async Task<bool> TcpReachableAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            await client.ConnectAsync(host, port, limit.Token);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Определяет тип блокировки: TCP timeout — блокировка по IP; TCP прошёл,
    /// а TLS обрывается — блокировка по SNI/TLS (её и пробуют обойти split/fake).
    /// </summary>
    public static async Task<BlockDiagnosis> DiagnoseAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return new BlockDiagnosis(host, BlockKind.Dns, "DNS не разрешился", null);
        }

        var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault();
        if (address is null)
        {
            return new BlockDiagnosis(host, BlockKind.Dns, "нет адреса", null);
        }

        using var client = new TcpClient();
        using (var tcpLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            tcpLimit.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                await client.ConnectAsync(address, 443, tcpLimit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new BlockDiagnosis(host, BlockKind.Ip, "TCP timeout", address.ToString());
            }
            catch (SocketException exception)
            {
                return new BlockDiagnosis(host, BlockKind.Ip, "TCP: " + exception.SocketErrorCode, address.ToString());
            }
        }

        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
            (_, _, _, _) => true);
        using (var tlsLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            tlsLimit.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = host }, tlsLimit.Token);
                return new BlockDiagnosis(host, BlockKind.Ok, "TLS в порядке", address.ToString());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new BlockDiagnosis(host, BlockKind.Tls, "TLS timeout", address.ToString());
            }
            catch (Exception exception) when (exception is AuthenticationException or IOException)
            {
                return new BlockDiagnosis(host, BlockKind.Tls, "TLS: " + exception.Message, address.ToString());
            }
        }
    }

    /// <summary>Проверка прямого доступа через запущенный движок (без прокси-узла).</summary>
    public static async Task<DesyncProbeResult> ProbeAsync(
        int index,
        string name,
        CancellationToken cancellationToken,
        bool probeVideo = false)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        // WinDivert-движок обрабатывает IPv4; не даём HttpClient уйти в IPv6,
        // иначе проба идёт мимо движка и всегда показывает 0.
        handler.ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault();
            if (address is null)
            {
                throw new HttpRequestException("DNS: нет адреса " + context.DnsEndPoint.Host);
            }

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

        var ok = 0;
        var youtube = false;
        var telegram = false;
        var times = new List<int>();
        foreach (var url in ProbeUrls)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var success = (int)response.StatusCode is >= 200 and < 400;
                if (success)
                {
                    ok++;
                    times.Add((int)stopwatch.ElapsedMilliseconds);
                }

                if (url.Contains("youtube", StringComparison.OrdinalIgnoreCase))
                {
                    youtube = success;
                }
                else
                {
                    telegram = success;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Таймаут/обрыв — стратегия не сработала для этого адреса.
            }
        }

        var average = times.Count == 0 ? 0 : (int)times.Average();
        var video = probeVideo && youtube ? await ProbeYoutubeVideoAsync(cancellationToken) : false;
        return new DesyncProbeResult(index, name, ok, ProbeUrls.Length, average, youtube, telegram, video);
    }

    private static readonly string[] VideoIds = { "jNQXAC9IVRw", "aqz-KE-bpKQ" };

    private static readonly Regex VideoUrlRegex = new(
        @"https://[^""\\\s]+googlevideo\.com/videoplayback[^""\\\s]+",
        RegexOptions.Compiled);

    /// <summary>Ищет в HTML страницы YouTube адрес потока videoplayback (googlevideo).</summary>
    internal static string? FindVideoUrl(string page) => FindVideoUrls(page).FirstOrDefault();

    /// <summary>Все адреса видеопотоков со страницы: у разных форматов бывают разные видео-хосты.</summary>
    internal static List<string> FindVideoUrls(string page)
    {
        var decoded = page
            .Replace("\\u0026", "&", StringComparison.Ordinal)
            .Replace("\\u003d", "=", StringComparison.Ordinal)
            .Replace("\\u003f", "?", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u002f", "/", StringComparison.Ordinal)
            .Replace("\\/", "/", StringComparison.Ordinal);
        return VideoUrlRegex.Matches(decoded)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Подробный итог проверки видео: где именно всё остановилось.</summary>
    internal sealed record VideoProbeDetail(
        bool Ok,
        bool PageOk,
        string? Host,
        int Code,
        long Bytes,
        string Detail,
        string? Url = null);

    /// <summary>Проверяет не только страницу, но и реальную загрузку видео с googlevideo.</summary>
    public static async Task<bool> ProbeYoutubeVideoAsync(CancellationToken cancellationToken) =>
        (await ProbeVideoDetailAsync(cancellationToken)).Ok;

    /// <summary>
    /// Полная проверка видео с подробностями для журнала и self-test: открылась ли страница,
    /// нашёлся ли адрес потока, что ответил видео-сервер googlevideo.
    /// </summary>
    internal static async Task<VideoProbeDetail> ProbeVideoDetailAsync(CancellationToken cancellationToken)
    {
        VideoProbeDetail? last = null;
        foreach (var id in VideoIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await CurlTextAsync($"https://www.youtube.com/watch?v={id}", 25, cancellationToken);
            if (string.IsNullOrEmpty(page))
            {
                last = new VideoProbeDetail(false, false, null, 0, 0, "страница YouTube не открылась");
                Trace?.Invoke("видео: страница не получена");
                continue;
            }

            var videoUrl = FindVideoUrl(page);
            if (videoUrl is null)
            {
                var hasPlayer = page.Contains("ytInitialPlayerResponse", StringComparison.Ordinal);
                last = new VideoProbeDetail(false, true, null, 0, 0,
                    $"страница {page.Length} симв., videoplayback не найден" +
                    (hasPlayer ? "" : " (нет ytInitialPlayerResponse)"));
                Trace?.Invoke("видео: " + last.Detail);
                continue;
            }

            var host = new Uri(videoUrl).Host;
            var (code, bytes) = await CurlRangeAsync(videoUrl, 25, cancellationToken);
            var detail = $"страница {page.Length} симв., {host}: HTTP {code}, {bytes} байт";
            last = new VideoProbeDetail(
                code is 200 or 206 && bytes > 1000, true, host, code, bytes, detail, videoUrl);
            Trace?.Invoke("видео: " + detail);
            if (last.Ok)
            {
                return last;
            }
        }

        return last ?? new VideoProbeDetail(false, false, null, 0, 0, "проверка не выполнялась");
    }

    private static string CurlPath =>
        Path.Combine(Environment.SystemDirectory, "curl.exe");

    internal static async Task<string?> CurlTextAsync(string url, int maxTimeSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(CurlPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-4", "-s", "-L", "--compressed", "--max-time", maxTimeSeconds.ToString(),
                         "--noproxy", "*", "--http1.1",
                         "-A", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                               "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
                         "-H", "Accept-Language: ru-RU,ru;q=0.9,en;q=0.8",
                         "-b", "CONSENT=YES+1",
                         url,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var text = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return text;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<(int Code, long Bytes)> CurlRangeAsync(
        string url,
        int maxTimeSeconds,
        CancellationToken cancellationToken) =>
        await CurlRangeResolvedAsync(url, null, maxTimeSeconds, cancellationToken);

    /// <summary>
    /// Скачивает начало видео-потока. <paramref name="resolveIp"/> подменяет адрес
    /// видео-хоста, сохраняя SNI и Host: так можно уйти с заблокированного
    /// локального GGC на другой узел Google.
    /// </summary>
    internal static async Task<(int Code, long Bytes)> CurlRangeResolvedAsync(
        string url,
        string? resolveIp,
        int maxTimeSeconds,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), "vienlink-yt-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var startInfo = new ProcessStartInfo(CurlPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var arguments = new List<string>
            {
                "-4", "-s", "-L", "-o", path, "-w", "%{http_code}",
                "--max-time", maxTimeSeconds.ToString(), "--noproxy", "*",
                "-r", "0-131071",
            };
            if (!string.IsNullOrEmpty(resolveIp))
            {
                var uri = new Uri(url);
                arguments.Add("--resolve");
                arguments.Add($"{uri.Host}:{uri.Port}:{resolveIp}");
            }

            arguments.Add(url);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (0, 0);
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var code = int.TryParse(output.Trim(), out var value) ? value : 0;
            var bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            return (code, bytes);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or OperationCanceledException or IOException)
        {
            return (0, 0);
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------- разбор и сборка пакетов ----------

    private static bool IsExternalTcp(byte[] packet) =>
        packet.Length >= 20 &&
        (packet[0] >> 4) == 4 &&
        packet[9] == 6 &&
        !IsPrivateDestination(packet);

    private static bool IsPrivateDestination(byte[] packet)
    {
        var address = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16, 4));
        var first = (byte)(address >> 24);
        var second = (byte)(address >> 16);
        return first is 0 or 10 or 127 || // 0/8, 10/8, 127/8
               (first == 169 && second == 254) ||
               (first == 172 && second is >= 16 and <= 31) ||
               (first == 192 && second == 168) ||
               (first == 100 && second is >= 64 and <= 127);
    }

    /// <summary>Разбирает IPv4/TCP/TLS и находит SNI. Всё с проверками границ.</summary>
    internal static bool TryParseClientHello(byte[] packet, out TlsClientHelloInfo info)
    {
        info = default;
        if (packet.Length < 40 || (packet[0] >> 4) != 4 || packet[9] != 6)
        {
            return false;
        }

        var ipHeaderLength = (packet[0] & 0x0F) * 4;
        if (ipHeaderLength < 20 || packet.Length < ipHeaderLength + 20)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2)) != 0)
        {
            return false; // фрагментированный IP не собираем
        }

        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        if (totalLength < ipHeaderLength + 20 || totalLength > packet.Length)
        {
            totalLength = (ushort)Math.Min(packet.Length, ushort.MaxValue);
        }

        var tcpOffset = ipHeaderLength;
        var tcpHeaderLength = (packet[tcpOffset + 12] >> 4) * 4;
        if (tcpHeaderLength < 20 || tcpOffset + tcpHeaderLength > totalLength)
        {
            return false;
        }

        var payloadOffset = tcpOffset + tcpHeaderLength;
        var payloadLength = totalLength - payloadOffset;
        if (payloadLength < 11)
        {
            return false;
        }

        var payload = packet.AsSpan(payloadOffset, payloadLength);
        if (!TryParseTlsClientHello(payload, out var sniOffset, out var sniLength, out var extensionsOffset))
        {
            return false;
        }

        info = new TlsClientHelloInfo(
            payloadOffset, payloadLength, payloadOffset + sniOffset, sniLength,
            payloadOffset + extensionsOffset);
        return true;
    }

    /// <summary>
    /// Разбирает ClientHello, который не влез в один TCP-сегмент: проверяет заголовки
    /// и ищет SNI прямо в полезной нагрузке. Поля длин расширений остаются нулевыми,
    /// поэтому фейковый домен нужно брать той же длины, что и настоящий.
    /// </summary>
    internal static bool TryBuildPartialClientHello(
        byte[] packet,
        out TlsClientHelloInfo info,
        out string host)
    {
        info = default;
        host = string.Empty;
        if (packet.Length < 40 || (packet[0] >> 4) != 4 || packet[9] != 6)
        {
            return false;
        }

        var tcpOffset = (packet[0] & 0x0F) * 4;
        if (tcpOffset < 20 || packet.Length < tcpOffset + 20)
        {
            return false;
        }

        var tcpHeaderLength = (packet[tcpOffset + 12] >> 4) * 4;
        var payloadOffset = tcpOffset + tcpHeaderLength;
        if (tcpHeaderLength < 20 || payloadOffset + 11 > packet.Length)
        {
            return false;
        }

        if (packet[payloadOffset] != 0x16 || packet[payloadOffset + 1] != 0x03 ||
            packet[payloadOffset + 5] != 0x01)
        {
            return false;
        }

        var payloadLength = packet.Length - payloadOffset;
        var payloadText = Encoding.ASCII.GetString(packet, payloadOffset, payloadLength);
        if (!TryLocateHost(payloadText, out var sniAt, out var sniLength))
        {
            return false;
        }

        info = new TlsClientHelloInfo(payloadOffset, payloadLength, payloadOffset + sniAt, sniLength, 0);
        host = payloadText.Substring(sniAt, sniLength);
        return true;
    }

    internal static bool TryParseTlsClientHello(
        ReadOnlySpan<byte> payload,
        out int sniOffset,
        out int sniLength)
        => TryParseTlsClientHello(payload, out sniOffset, out sniLength, out _);

    /// <summary>
    /// Ищет SNI в TLS ClientHello. Смещения считаются от начала TLS-потока,
    /// поэтому метод годится и для пакета из WinDivert, и для обычного TCP-потока.
    /// </summary>
    internal static bool TryParseTlsClientHello(
        ReadOnlySpan<byte> payload,
        out int sniOffset,
        out int sniLength,
        out int extensionsLengthOffset)
    {
        sniOffset = 0;
        sniLength = 0;
        extensionsLengthOffset = 0;
        var payloadLength = payload.Length;
        if (payloadLength < 11 ||
            payload[0] != 0x16 || payload[1] != 0x03 || payload[5] != 0x01)
        {
            return false;
        }

        // ClientHello может не помещаться в один TCP-сегмент (большой key share).
        // Не требуем полную запись: SNI обычно находится в начале и в этом сегменте.
        var p = 9; // 5 байт записи + 4 байта заголовка handshake
        if (p + 2 + 32 > payloadLength)
        {
            return false;
        }

        p += 2 + 32; // версия и random
        if (p >= payloadLength)
        {
            return false;
        }

        var sessionLength = payload[p];
        p += 1 + sessionLength;
        if (p + 2 > payloadLength)
        {
            return false;
        }

        var cipherLength = (payload[p] << 8) | payload[p + 1];
        p += 2 + cipherLength;
        if (p + 1 > payloadLength)
        {
            return false;
        }

        var compressionLength = payload[p];
        p += 1 + compressionLength;
        if (p + 2 > payloadLength)
        {
            return false;
        }

        var extensionsLength = (payload[p] << 8) | payload[p + 1];
        extensionsLengthOffset = p;
        p += 2;
        var extensionsEnd = Math.Min(p + extensionsLength, payloadLength);
        while (p + 4 <= extensionsEnd)
        {
            var type = (payload[p] << 8) | payload[p + 1];
            var length = (payload[p + 2] << 8) | payload[p + 3];
            var data = p + 4;
            if (data + length > payloadLength)
            {
                break;
            }

            if (type == 0 && length >= 5)
            {
                var listLength = (payload[data] << 8) | payload[data + 1];
                var q = data + 2;
                var listEnd = Math.Min(q + listLength, data + length);
                while (q + 3 <= listEnd)
                {
                    var nameType = payload[q];
                    var nameLength = (payload[q + 1] << 8) | payload[q + 2];
                    q += 3;
                    if (nameType == 0 && nameLength > 0 && q + nameLength <= listEnd)
                    {
                        sniOffset = q;
                        sniLength = nameLength;
                        return true;
                    }

                    q += nameLength;
                }
            }

            p = data + length;
        }

        return false;
    }

    internal static int[] SplitPoints(DesyncTcpMode mode, TlsClientHelloInfo hello, string? host = null)
    {
        var sni = hello.SniOffset - hello.PayloadOffset;
        var max = hello.PayloadLength - 1;
        if (max < 1)
        {
            return Array.Empty<int>();
        }

        static int Clamp(int value, int max) => Math.Clamp(value, 1, max);

        static int MidSld(int sniStart, string? name, int length)
        {
            if (string.IsNullOrEmpty(name))
            {
                return sniStart + length / 2;
            }

            var lastDot = name.LastIndexOf('.');
            var previousDot = lastDot > 0 ? name.LastIndexOf('.', lastDot - 1) : -1;
            var start = previousDot + 1;
            var sldLength = lastDot > start ? lastDot - start : name.Length;
            return sniStart + start + Math.Max(1, sldLength / 2);
        }

        return mode switch
        {
            DesyncTcpMode.Split1 => new[] { 1 },
            DesyncTcpMode.SplitSni or DesyncTcpMode.DisorderSni or DesyncTcpMode.FakeSplit
                => new[] { Clamp(sni + 1, max) },
            DesyncTcpMode.MultiSplit => new[]
            {
                Clamp(1, max),
                Clamp(sni, max),
                Clamp(sni + hello.SniLength / 2, max),
                Clamp(sni + 1, max),
                Clamp(sni + hello.SniLength, max),
            }.Distinct().OrderBy(value => value).ToArray(),
            DesyncTcpMode.FakeMultiDisorder => new[]
            {
                Clamp(1, max),
                // Разрез перед SNI обязателен: хвост уходит первым сегментом,
                // и DPI не должен увидеть в нём имя домена целиком.
                Clamp(sni, max),
                Clamp(MidSld(sni, host, hello.SniLength), max),
                Clamp(sni + 1, max),
                Clamp(sni + hello.SniLength, max),
            }.Distinct().OrderBy(value => value).ToArray(),
            _ => Array.Empty<int>(),
        };
    }

    /// <summary>Режет ClientHello на TCP-сегменты; seq и длины пересчитываются.</summary>
    internal static List<byte[]> BuildSplitPackets(byte[] packet, TlsClientHelloInfo hello, int[] splitPoints)
    {
        var points = splitPoints
            .Where(point => point > 0 && point < hello.PayloadLength)
            .Distinct()
            .OrderBy(point => point)
            .ToArray();
        if (points.Length == 0)
        {
            return new List<byte[]>();
        }

        var tcpOffset = packet[9] == 6 ? (packet[0] & 0x0F) * 4 : 0;
        if (tcpOffset < 20)
        {
            return new List<byte[]>();
        }

        var sequence = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(tcpOffset + 4, 4));
        var starts = new List<int> { 0 };
        starts.AddRange(points);
        var ends = new List<int>(points);
        ends.Add(hello.PayloadLength);

        var result = new List<byte[]>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index];
            var length = ends[index] - start;
            if (length <= 0)
            {
                continue;
            }

            var piece = new byte[hello.PayloadOffset + length];
            packet.AsSpan(0, hello.PayloadOffset).CopyTo(piece);
            BinaryPrimitives.WriteUInt16BigEndian(piece.AsSpan(2, 2), (ushort)piece.Length);
            BinaryPrimitives.WriteUInt32BigEndian(piece.AsSpan(tcpOffset + 4, 4), sequence + (uint)start);
            if (index != starts.Count - 1)
            {
                piece[tcpOffset + 13] &= 0xF7; // PSH только на последнем сегменте
            }

            piece[10] = 0;
            piece[11] = 0;
            piece[tcpOffset + 16] = 0;
            piece[tcpOffset + 17] = 0;
            packet.AsSpan(hello.PayloadOffset + start, length).CopyTo(piece.AsSpan(hello.PayloadOffset));
            result.Add(piece);
        }

        return result;
    }

    /// <summary>Копия ClientHello с безобидным SNI и TTL 1: до сервера не дойдёт, DPI увидит.</summary>
    internal static byte[] BuildFakeClientHello(
        byte[] packet,
        TlsClientHelloInfo hello,
        byte ttl = 1,
        uint? sequence = null,
        string? benignHost = null,
        bool md5Signature = false)
    {
        var benign = benignHost ?? BenignHost(hello.SniLength);
        var delta = benign.Length - hello.SniLength;
        byte[] fake;
        if (delta == 0)
        {
            fake = (byte[])packet.Clone();
            Encoding.ASCII.GetBytes(benign, fake.AsSpan(hello.SniOffset, hello.SniLength));
        }
        else
        {
            fake = new byte[packet.Length + delta];
            packet.AsSpan(0, hello.SniOffset).CopyTo(fake);
            Encoding.ASCII.GetBytes(benign, fake.AsSpan(hello.SniOffset));
            packet.AsSpan(hello.SniOffset + hello.SniLength)
                .CopyTo(fake.AsSpan(hello.SniOffset + benign.Length));

            // Меняем длину SNI: правим IP total length, TLS record, handshake,
            // длину server_name extension, списка имён и самого имени.
            AdjustUInt16(fake, 2, delta);
            AdjustUInt16(fake, hello.PayloadOffset + 3, delta);
            AdjustUInt24(fake, hello.PayloadOffset + 6, delta);
            AdjustUInt16(fake, hello.ExtensionsLengthOffset, delta);
            AdjustUInt16(fake, hello.SniOffset - 7, delta);
            AdjustUInt16(fake, hello.SniOffset - 5, delta);
            AdjustUInt16(fake, hello.SniOffset - 2, delta);
        }

        fake[8] = ttl;
        if (sequence is { } fakeSequence)
        {
            var tcpOffset = (packet[0] & 0x0F) * 4;
            BinaryPrimitives.WriteUInt32BigEndian(fake.AsSpan(tcpOffset + 4, 4), fakeSequence);
        }

        fake[10] = 0;
        fake[11] = 0;
        var tcp = (packet[0] & 0x0F) * 4;
        fake[tcp + 16] = 0;
        fake[tcp + 17] = 0;

        // Случайный session id и TLS random, как fake-tls-mod=rnd в zapret:
        // DPI не должен связать фейк с настоящим ClientHello.
        var randomOffset = hello.PayloadOffset + 11;
        if (randomOffset + 32 <= fake.Length)
        {
            Random.Shared.NextBytes(fake.AsSpan(randomOffset, 32));
        }

        var sessionLengthOffset = hello.PayloadOffset + 43;
        if (sessionLengthOffset < fake.Length)
        {
            var sessionLength = fake[sessionLengthOffset];
            var sessionOffset = sessionLengthOffset + 1;
            if (sessionLength > 0 && sessionOffset + sessionLength <= fake.Length)
            {
                Random.Shared.NextBytes(fake.AsSpan(sessionOffset, sessionLength));
            }
        }

        if (md5Signature)
        {
            fake = AddTcpMd5Option(fake, (packet[0] & 0x0F) * 4);
        }

        return fake;
    }

    /// <summary>Добавляет TCP MD5-опцию (kind 19): сервер такой сегмент отбросит, DPI — разберёт.</summary>
    private static byte[] AddTcpMd5Option(byte[] fake, int tcpOffset)
    {
        var tcpHeaderLength = (fake[tcpOffset + 12] >> 4) * 4;
        var payloadOffset = tcpOffset + tcpHeaderLength;
        const int optionLength = 20; // kind+len+16 байт MD5 + 2 NOP
        var result = new byte[fake.Length + optionLength];
        fake.AsSpan(0, payloadOffset).CopyTo(result);
        result[payloadOffset] = 19;
        result[payloadOffset + 1] = 18;
        result[payloadOffset + 18] = 1;
        result[payloadOffset + 19] = 1;
        fake.AsSpan(payloadOffset).CopyTo(result.AsSpan(payloadOffset + optionLength));

        // TCP data offset: исходная длина заголовка + MD5-опция.
        var newHeaderLength = tcpHeaderLength + optionLength;
        result[tcpOffset + 12] = (byte)(((newHeaderLength / 4) << 4) | (result[tcpOffset + 12] & 0x0F));
        AdjustUInt16(result, 2, optionLength);
        result[10] = 0;
        result[11] = 0;
        result[tcpOffset + 16] = 0;
        result[tcpOffset + 17] = 0;
        return result;
    }

    private static void AdjustUInt16(byte[] packet, int offset, int delta)
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)(value + delta));
    }

    private static void AdjustUInt24(byte[] packet, int offset, int delta)
    {
        var value = (packet[offset] << 16) | (packet[offset + 1] << 8) | packet[offset + 2];
        value += delta;
        packet[offset] = (byte)(value >> 16);
        packet[offset + 1] = (byte)(value >> 8);
        packet[offset + 2] = (byte)value;
    }

    private static readonly string[] BenignHosts =
    {
        "www.google.com", "www.google.com.",
        "www.google.ru", "www.google.ru.",
        "google.com", "google.com.",
        "google.ru", "google.ru.",
        "mail.ru", "mail.ru.",
        "vk.com", "vk.com.",
        "ya.ru", "ya.ru.",
        "x.com", "x.com.",
        "a.co", "a.co.",
    };

    /// <summary>Домен той же длины, что и настоящий SNI: DPI проверит его вместо цели.</summary>
    internal static string BenignHost(int length)
    {
        const string seed = "www.google.com";
        if (length <= 0)
        {
            return string.Empty;
        }

        var exact = BenignHosts.FirstOrDefault(host => host.Length == length);
        if (exact is not null)
        {
            return exact;
        }

        if (length <= seed.Length)
        {
            var candidate = seed[..length];
            return candidate.EndsWith('.') ? new string('a', length) : candidate;
        }

        var builder = new StringBuilder(seed);
        while (builder.Length < length)
        {
            if (builder.Length + 2 <= length)
            {
                builder.Append(".a");
            }
            else
            {
                builder.Append('a');
            }
        }

        return builder.ToString(0, length);
    }

    /// <summary>Собирает UDP-пакет с шаблоном QUIC Initial от google и заданным TTL.</summary>
    internal static bool TryBuildQuicFake(byte[] packet, byte[] template, byte ttl, out byte[] fake, out string key)
    {
        fake = Array.Empty<byte>();
        key = string.Empty;
        if (packet.Length < 28 || (packet[0] >> 4) != 4 || packet[9] != 17 || IsPrivateDestination(packet))
        {
            return false;
        }

        var ipHeaderLength = (packet[0] & 0x0F) * 4;
        if (ipHeaderLength < 20 || packet.Length < ipHeaderLength + 8 + 800)
        {
            return false;
        }

        var udpOffset = ipHeaderLength;
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udpOffset + 4, 2));
        if (udpLength < 8 || udpOffset + udpLength > packet.Length)
        {
            return false;
        }

        var payload = packet.AsSpan(udpOffset + 8, udpLength - 8);
        if (payload.Length < 800 || (payload[0] & 0x80) == 0)
        {
            return false; // короткий заголовок — это уже 1-RTT, не Initial
        }

        fake = new byte[20 + 8 + template.Length];
        fake[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(fake.AsSpan(2, 2), (ushort)fake.Length);
        fake[8] = ttl;
        fake[9] = 17;
        packet.AsSpan(12, 8).CopyTo(fake.AsSpan(12, 8)); // src/dst IP
        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udpOffset, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udpOffset + 2, 2));
        BinaryPrimitives.WriteUInt16BigEndian(fake.AsSpan(20, 2), srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(fake.AsSpan(22, 2), dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(fake.AsSpan(24, 2), (ushort)(8 + template.Length));
        template.CopyTo(fake.AsSpan(28));
        key = $"{srcPort}:{dstPort}:{packet[12]}.{packet[13]}.{packet[14]}.{packet[15]}-" +
              $"{packet[16]}.{packet[17]}.{packet[18]}.{packet[19]}";
        return true;
    }

    /// <summary>Проверка контрольных сумм через WinDivert без прав администратора (для self-test).</summary>
    internal static bool TryCalculateChecksums(byte[] packet, out byte[] result)
    {
        result = (byte[])packet.Clone();
        if (!IsInstalled)
        {
            return false;
        }

        try
        {
            using var api = WinDivertApi.Load(DllPath);
            var packetBuffer = Marshal.AllocHGlobal(result.Length);
            var addressBuffer = Marshal.AllocHGlobal(AddressSize);
            try
            {
                Marshal.Copy(result, 0, packetBuffer, result.Length);
                Marshal.Copy(new byte[AddressSize], 0, addressBuffer, AddressSize);
                if (!api.CalcChecksums(packetBuffer, (uint)result.Length, addressBuffer, 0))
                {
                    return false;
                }

                Marshal.Copy(packetBuffer, result, 0, result.Length);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(packetBuffer);
                Marshal.FreeHGlobal(addressBuffer);
            }
        }
        catch (Exception exception) when (exception is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Write("desync", "WinDivertHelperCalcChecksums недоступен", exception);
            return false;
        }
    }

    public void Dispose() => Stop();

    // ---------- WinDivert API ----------

    private sealed class WinDivertApi : IDisposable
    {
        private IntPtr _library;

        public required OpenDelegate Open { get; init; }

        public required RecvDelegate Recv { get; init; }

        public required SendDelegate Send { get; init; }

        public required CloseDelegate Close { get; init; }

        public required CalcChecksumsDelegate CalcChecksums { get; init; }

        public static WinDivertApi Load(string dllPath)
        {
            var library = NativeLibrary.Load(dllPath);
            try
            {
                T Get<T>(string name) where T : Delegate =>
                    Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

                return new WinDivertApi
                {
                    Open = Get<OpenDelegate>("WinDivertOpen"),
                    Recv = Get<RecvDelegate>("WinDivertRecv"),
                    Send = Get<SendDelegate>("WinDivertSend"),
                    Close = Get<CloseDelegate>("WinDivertClose"),
                    CalcChecksums = Get<CalcChecksumsDelegate>("WinDivertHelperCalcChecksums"),
                    _library = library,
                };
            }
            catch
            {
                NativeLibrary.Free(library);
                throw;
            }
        }

        public void Dispose()
        {
            if (_library == IntPtr.Zero)
            {
                return;
            }

            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        internal delegate IntPtr OpenDelegate(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filter,
            int layer,
            short priority,
            ulong flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        internal delegate bool RecvDelegate(
            IntPtr handle,
            IntPtr packet,
            uint packetLength,
            out uint receivedLength,
            IntPtr address);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        internal delegate bool SendDelegate(
            IntPtr handle,
            IntPtr packet,
            uint packetLength,
            out uint sentLength,
            IntPtr address);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        internal delegate bool CloseDelegate(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        internal delegate bool CalcChecksumsDelegate(
            IntPtr packet,
            uint packetLength,
            IntPtr address,
            ulong flags);
    }
}
