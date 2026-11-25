using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace VieNLink.SelfTest;

/// <summary>
/// Проверка без NuGet: разбор ссылок, сборка конфига и живой прогон ядра.
/// Запуск: dotnet run --project VieNLink.SelfTest -- liveness [узлов-на-источник] [адрес-проверки]
/// </summary>
internal static class Program
{
    private static int _failed;

    private static readonly (string Name, string Url, SourceKind Kind)[] CandidateSources =
    {
        ("v2rayNG-Config", "https://raw.githubusercontent.com/ALIILAPRO/v2rayNG-Config/main/server.txt", SourceKind.PlainList),
        ("v2rayNG-Config sub", "https://raw.githubusercontent.com/ALIILAPRO/v2rayNG-Config/main/sub.txt", SourceKind.PlainList),
        ("V2RayAggregator", "https://raw.githubusercontent.com/mahdibland/V2RayAggregator/master/sub/sub_merge.txt", SourceKind.PlainList),
        ("ShadowsocksAggregator", "https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/Eternity.txt", SourceKind.PlainList),
        ("get_subscribe", "https://raw.githubusercontent.com/ermaozi/get_subscribe/main/subscribe/v2ray.txt", SourceKind.PlainList),
        ("Free-servers", "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub", SourceKind.Subscription),
        ("freenode", "https://raw.githubusercontent.com/ripaojiedian/freenode/main/sub", SourceKind.Subscription),
        ("barry-far vless", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/vless.txt", SourceKind.PlainList),
        ("MhdiTaheri mix", "https://raw.githubusercontent.com/MhdiTaheri/V2rayCollector/main/sub/mix", SourceKind.PlainList),
        ("Mineral nodes", "https://raw.githubusercontent.com/LalatinaHub/Mineral/master/result/nodes", SourceKind.PlainList),
        ("roosterkid V2RAY", "https://raw.githubusercontent.com/roosterkid/openproxylist/main/V2RAY_RAW.txt", SourceKind.PlainList),
        ("Kwinshadow mix", "https://raw.githubusercontent.com/Kwinshadow/TelegramV2rayCollector/main/sublinks/mix.txt", SourceKind.PlainList),
    };

    private static async Task<int> Main(string[] args)
    {
        AppLog.SuppressFile = true;
        // Чтобы режимы sources/sourcestats/prunesources читали настоящий sources.dat.
        SecretProtector.Current = new DpapiSecretProtector();
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Режимы: parsers | liveness [узлов-на-источник] [адрес-проверки]");
            return 0;
        }

        switch (args[0])
        {
            case "parsers":
                RunParserTests();
                break;
            case "liveness":
                var limit = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 60;
                var url = args.Length > 2 ? args[2] : "https://www.youtube.com/";
                await RunLivenessAsync(limit, url);
                break;
            case "seed":
                await SeedCacheAsync();
                break;
            case "exportseed":
                RunExportSeed(args.Length > 1 ? args[1] : null);
                break;
            case "proxytest":
                await RunProxyTestAsync();
                break;
            case "fetch":
                await RunFetchAsync(args.Length > 1 ? args[1] : "http://127.0.0.1:8123/sub");
                break;
            case "mtproto":
                await RunMtProtoFetchAsync(args.Length > 1 ? args[1] : "https://t.me/s/MTProtoProxies");
                break;
            case "mtprotocheck":
                var mtLimit = args.Length > 1 && int.TryParse(args[1], out var mtParsed) ? mtParsed : 30;
                await RunMtProtoCheckAsync(mtLimit, args.Length > 2 ? args[2] : null);
                break;
            case "mtprotohost":
                if (args.Length < 4 || !int.TryParse(args[2], out var mtPort))
                {
                    Console.WriteLine("Использование: mtprotohost <сервер> <порт> <секрет>");
                    return 2;
                }

                await RunMtProtoHostAsync(args[1], mtPort, args[3]);
                break;
            case "mtprotosites":
                await RunMtProtoSitesAsync();
                break;
            case "probe":
                var probeLimit = args.Length > 1 && int.TryParse(args[1], out var probeParsed) ? probeParsed : 120;
                await RunProbeAsync(probeLimit);
                break;
            case "hiddenprobe":
                var hiddenLimit = args.Length > 1 && int.TryParse(args[1], out var hiddenParsed) ? hiddenParsed : 5;
                await RunHiddenProbeAsync(hiddenLimit);
                break;
            case "cache":
                RunCacheStats();
                break;
            case "sources":
                RunSourcesList();
                break;
            case "sourcestats":
                RunSourceStats();
                break;
            case "prunesources":
                RunPruneSources(args.Length > 1 && args[1] == "--apply");
                break;
            case "sourceprobe":
                if (args.Length < 2)
                {
                    Console.WriteLine("Использование: sourceprobe <url> [limit]");
                    return 2;
                }

                var sourceLimit = args.Length > 2 && int.TryParse(args[2], out var sourceParsed) ? sourceParsed : 60;
                await RunSourceProbeAsync(args[1], sourceLimit);
                break;
            case "linkprobe":
                if (args.Length < 2)
                {
                    Console.WriteLine("Использование: linkprobe <vless://…> [limit]");
                    return 2;
                }

                var linkLimit = args.Length > 2 && int.TryParse(args[2], out var linkParsed) ? linkParsed : 60;
                await RunSourceProbeAsync(args[1], linkLimit, singleLink: true);
                break;
            case "compare":
                var perClass = args.Length > 1 && int.TryParse(args[1], out var compareParsed) ? compareParsed : 80;
                await RunCompareAsync(perClass);
                break;
            case "zapret":
                await RunZapretAsync(args.Length > 1 ? args[1] : "status");
                break;
            case "discover":
                await RunDiscoveryAsync();
                break;
            case "procmatch":
                await RunProcessMatchAsync();
                break;
            case "tun":
                await RunTunAsync();
                break;
            case "checkconfigs":
                await RunConfigChecksAsync();
                break;
            case "export":
                await RunExportAsync();
                break;
            case "statuslogic":
                await RunStatusLogicAsync();
                break;
            case "blacklist":
                RunBlacklistTests();
                break;
            case "connectflow":
                await RunConnectFlowAsync();
                break;
            case "desync":
                RunDesyncTests();
                break;
            case "desyncinstall":
                await RunDesyncInstallAsync();
                break;
            case "desyncstart":
                RunDesyncStartTest();
                break;
            case "forwarder":
                await RunForwarderTestAsync();
                break;
            case "forwarderudp":
                await RunForwarderUdpAsync();
                break;
            case "forwarderudpvia":
                await RunForwarderUdpViaSingBoxAsync();
                break;
            case "providerpresets":
                RunProviderPresetsTest();
                break;
            case "presetsremote":
                await RunPresetsRemoteAsync();
                break;
            case "ranking":
                RunRankingTest();
                break;
            case "forwardertls":
                await RunForwarderTlsAsync();
                break;
            case "forwarder-e2e":
                await RunForwarderE2EAsync();
                break;
            case "video":
                await RunVideoDiagAsync();
                break;
            case "videohost":
                await RunVideoHostAsync(args.Length > 1 ? args[1] : "rr2---sn-hxb54vo-238s.googlevideo.com");
                break;
            case "tunbypassstart":
                await RunTunBypassStartAsync();
                break;
            case "tunbypasslive":
                await RunTunBypassLiveAsync();
                break;
            case "tunbypassautotune":
                await RunTunBypassAutoTuneAsync();
                break;
            case "blocktype":
                await RunBlockTypeAsync();
                break;
            case "desynclive":
                await RunDesyncLiveAsync();
                break;
            case "videolive":
                await RunVideoLiveAsync();
                break;
            case "videofallback":
                await RunVideoFallbackAsync();
                break;
            case "videomulti":
                await RunVideoMultiAsync();
                break;
            case "videoprobe":
                await RunDesyncVideoProbeAsync();
                break;
            case "quicprobe":
                await RunQuicProbeAsync();
                break;
            case "zapretvideo":
                await RunZapretVideoAsync();
                break;
            case "videofront":
                await RunVideoFrontAsync();
                break;
            case "zapretlive":
                await RunZapretLiveAsync();
                break;
            default:
                Console.WriteLine("Неизвестный режим: " + args[0]);
                return 2;
        }

        Console.WriteLine(_failed == 0 ? "ИТОГ: всё в порядке" : $"ИТОГ: провалов {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "  ok   " : "  FAIL ") + name);
        if (!condition)
        {
            _failed++;
        }
    }

    /// <summary>
    /// Собственный обход DPI: разбор TLS ClientHello и сборка пакетов без WinDivert.
    /// Проверяются границы, SNI, seq, длины и фейковые пакеты.
    /// </summary>
    private static void RunDesyncTests()
    {
        Console.WriteLine("Свой обход DPI (разбор пакетов):");
        var host = "www.youtube.com";
        var packet = BuildClientHelloPacket(host);
        Check(DesyncService.TryParseClientHello(packet, out var hello), "ClientHello: разобран");
        Check(hello.SniLength == host.Length, "ClientHello: длина SNI");
        Check(System.Text.Encoding.ASCII.GetString(packet, hello.SniOffset, hello.SniLength) == host,
            "ClientHello: найден домен");

        // ClientHello может прийти частями: SNI в первом сегменте, запись длиннее.
        var cut = packet[..(hello.SniOffset + hello.SniLength)];
        BinaryPrimitives.WriteUInt16BigEndian(cut.AsSpan(2, 2), (ushort)cut.Length);
        Check(DesyncService.TryParseClientHello(cut, out var cutHello) && cutHello.SniLength == host.Length,
            "ClientHello: SNI найден в неполном сегменте");

        // ClientHello больше TCP-сегмента (современные шифрнаборы Chrome/Windows):
        // обычный разбор не срабатывает, но SNI виден в полезной нагрузке и его надо резать.
        var longHost = "rr2---sn-hxb54vo-238s.googlevideo.com";
        var longPayload = new string('q', 120) + "(" + longHost + ")" + new string('z', 300);
        Check(DesyncService.TryLocateHost(longPayload, out var blindAt, out var blindLength),
            "слепой разрез: домен найден в полезной нагрузке");
        Check(longPayload.Substring(blindAt, blindLength) == longHost, "слепой разрез: полное имя хоста");
        var blindPoints = DesyncService.LocateSniSplitPoints(
            longPayload, longPayload.Length, out blindAt, out blindLength, out var blindHost);
        Check(blindHost == longHost, "слепой разрез: имя хоста для журнала");
        Check(blindPoints.Contains(blindAt + 1) && blindPoints.Contains(blindAt + blindLength),
            "слепой разрез: точки на границах SNI");
        Check(!longPayload[(blindAt + blindLength)..].Contains("googlevideo", StringComparison.Ordinal),
            "слепой разрез: хвост, который уходит первым, без имени домена");

        var points = DesyncService.SplitPoints(DesyncTcpMode.SplitSni, hello);
        var sniRelative = hello.SniOffset - hello.PayloadOffset;
        Check(points.Length == 1 && points[0] == sniRelative + 1, "разрез SNI: позиция после первого символа");

        var pieces = DesyncService.BuildSplitPackets(packet, hello, points);
        Check(pieces.Count == 2, "разрез SNI: два TCP-сегмента");
        if (pieces.Count == 2)
        {
            var firstPayload = pieces[0].Length - hello.PayloadOffset;
            var secondPayload = pieces[1].Length - hello.PayloadOffset;
            Check(firstPayload + secondPayload == hello.PayloadLength, "разрез SNI: полезная нагрузка не потеряна");
            var tcpOffset = (packet[0] & 0x0F) * 4;
            var firstSeq = BinaryPrimitives.ReadUInt32BigEndian(pieces[0].AsSpan(tcpOffset + 4, 4));
            var secondSeq = BinaryPrimitives.ReadUInt32BigEndian(pieces[1].AsSpan(tcpOffset + 4, 4));
            Check(secondSeq == firstSeq + (uint)firstPayload, "разрез SNI: seq второго сегмента");
            Check((pieces[0][tcpOffset + 13] & 0x08) == 0, "разрез SNI: PSH снят с первого сегмента");
            Check((pieces[1][tcpOffset + 13] & 0x08) != 0, "разрез SNI: PSH оставлен на последнем");
        }

        var multi = DesyncService.SplitPoints(DesyncTcpMode.MultiSplit, hello);
        Check(multi.Length >= 2 && multi.SequenceEqual(multi.OrderBy(value => value)), "мультиразрез: точки по возрастанию");

        Check(DesyncService.TryBuildIpFragments(packet, out var ipFragments) && ipFragments.Count >= 2,
            "IP-фрагментация: пакет разбит");
        if (ipFragments.Count >= 2)
        {
            var ipHeader = (packet[0] & 0x0F) * 4;
            var total = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
            Check((BinaryPrimitives.ReadUInt16BigEndian(ipFragments[0].AsSpan(6, 2)) & 0x2000) != 0,
                "IP-фрагментация: MF у первого");
            Check((BinaryPrimitives.ReadUInt16BigEndian(ipFragments[^1].AsSpan(6, 2)) & 0x2000) == 0,
                "IP-фрагментация: MF снят у последнего");
            var rebuilt = ipFragments.SelectMany(fragment => fragment.Skip(ipHeader)).ToArray();
            Check(rebuilt.SequenceEqual(packet.AsSpan(ipHeader, total - ipHeader).ToArray()),
                "IP-фрагментация: полезная нагрузка собирается без потерь");
            Check(Checksum(ipFragments[0].AsSpan(0, ipHeader)) == 0,
                "IP-фрагментация: IP-чексумма верна");
        }

        var fake = DesyncService.BuildFakeClientHello(packet, hello);
        Check(fake[8] == 1, "фейк: TTL 1");
        Check(fake[hello.SniOffset..(hello.SniOffset + hello.SniLength)]
                  .SequenceEqual(System.Text.Encoding.ASCII.GetBytes(DesyncService.BenignHost(hello.SniLength))),
            "фейк: SNI заменён доменом той же длины");
        Check(DesyncService.BenignHost(9).Length == 9 && DesyncService.BenignHost(40).Length == 40,
            "фейк: безобидный домен нужной длины");

        var longFake = DesyncService.BuildFakeClientHello(
            packet, hello, ttl: 64, sequence: 0, benignHost: "www.google.com", md5Signature: false);
        Check(DesyncService.TryParseClientHello(longFake, out var longFakeHello),
            "фейк: ClientHello с другим SNI разбирается");
        Check(System.Text.Encoding.ASCII.GetString(longFake, longFakeHello.SniOffset, longFakeHello.SniLength) ==
              "www.google.com", "фейк: SNI www.google.com");
        var md5Fake = DesyncService.BuildFakeClientHello(
            packet, hello, ttl: 64, sequence: 0, benignHost: "www.google.com", md5Signature: true);
        Check(DesyncService.TryParseClientHello(md5Fake, out var md5FakeHello),
            "фейк: ClientHello с TCP MD5 разбирается");
        Check(System.Text.Encoding.ASCII.GetString(md5Fake, md5FakeHello.SniOffset, md5FakeHello.SniLength) ==
              "www.google.com", "фейк: SNI www.google.com с MD5");

        var tlsPayload = packet.AsSpan(hello.PayloadOffset, hello.PayloadLength).ToArray();
        var streamPieces = TlsFragmentForwarder.BuildStreamPieces(tlsPayload, StreamFragmentMode.SplitSni);
        Check(streamPieces.Count == 2 && streamPieces.Sum(piece => piece.Length) == tlsPayload.Length,
            "форвардер: разрез SNI на два куска без потерь");
        Check(streamPieces.Count == 2 && streamPieces[0].Length == sniRelative + 1,
            "форвардер: первый кусок заканчивается после первого символа SNI");
        var streamMulti = TlsFragmentForwarder.BuildStreamPieces(tlsPayload, StreamFragmentMode.MultiSplit);
        Check(streamMulti.Count >= 2 && streamMulti.Sum(piece => piece.Length) == tlsPayload.Length,
            "форвардер: мультиразрез без потерь");
        var streamStagger = TlsFragmentForwarder.BuildStreamPieces(tlsPayload, StreamFragmentMode.SplitStagger);
        Check(streamStagger.Count == 4 && streamStagger.Sum(piece => piece.Length) == tlsPayload.Length,
            "форвардер: ступенчатый разрез на 4 куска без потерь");
        var streamSniEnd = TlsFragmentForwarder.BuildStreamPieces(tlsPayload, StreamFragmentMode.SplitSniEnd);
        var sniEndPoint = Math.Min(sniRelative + hello.SniLength, tlsPayload.Length - 1);
        Check(streamSniEnd.Count == 2 && streamSniEnd[0].Length == sniEndPoint,
            "форвардер: разрез после домена");
        var streamSplitBand = TlsFragmentForwarder.BuildStreamPieces(tlsPayload, StreamFragmentMode.SplitSniBand);
        Check(streamSplitBand.Count == 4 &&
              streamSplitBand.Sum(piece => piece.Length) == tlsPayload.Length,
            "форвардер: разрез полосы SNI — четыре куска");
        var streamDisorder = TlsFragmentForwarder.BuildStreamPieces(tlsPayload, StreamFragmentMode.DisorderSni);
        Check(streamDisorder.Count == 2 && streamDisorder.Sum(piece => piece.Length) == tlsPayload.Length,
            "форвардер: перестановка — два куска без потерь");
        var streamMultiDisorder = TlsFragmentForwarder.BuildStreamPieces(tlsPayload, StreamFragmentMode.MultiDisorder);
        Check(streamMultiDisorder.Count == 4 &&
              streamMultiDisorder.Sum(piece => piece.Length) == tlsPayload.Length,
            "форвардер: мультиперестановка — четыре куска без потерь");
        Check(TlsFragmentForwarder.Strategies.All(strategy => strategy.Description.Length > 0) &&
              TlsFragmentForwarder.Strategies.Any(strategy => strategy.DelayMs > 0) &&
              TlsFragmentForwarder.Strategies.Any(strategy => strategy.Ttl > 0),
            "форвардер: стратегии описаны, есть пауза и TTL");

        var quicPacket = BuildQuicPacket(1200);        var template = new byte[1200];
        template[0] = 0xC0;
        Check(DesyncService.TryBuildQuicFake(quicPacket, template, ttl: 64, out var quicFake, out _),
            "QUIC: фейковый Initial собран");
        Check(quicFake.Length == 20 + 8 + template.Length && quicFake[8] == 64,
            "QUIC: длины и TTL");
        Check(BinaryPrimitives.ReadUInt16BigEndian(quicFake.AsSpan(24, 2)) == 8 + template.Length,
            "QUIC: длина UDP");

        if (DesyncService.IsInstalled)
        {
            Check(DesyncService.TryCalculateChecksums(packet, out var checkedPacket),
                "WinDivert: контрольные суммы рассчитаны");
            Check(Checksum(checkedPacket.AsSpan(0, 20)) == 0, "WinDivert: IP-чексумма верна");
            var tcpStored = BinaryPrimitives.ReadUInt16BigEndian(checkedPacket.AsSpan(36, 2));
            Check(Checksum(TcpPseudoHeader(checkedPacket, 20)) == tcpStored, "WinDivert: TCP-чексумма верна");
        }
        else
        {
            Console.WriteLine("  пропуск: WinDivert не установлен (нужен для проверки чексумм)");
        }
    }

    /// <summary>Контрольная сумма Интернета (RFC 1071) для self-test.</summary>
    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var index = 0; index + 1 < data.Length; index += 2)
        {
            sum += (uint)((data[index] << 8) | data[index + 1]);
        }

        if ((data.Length & 1) != 0)
        {
            sum += (uint)(data[^1] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    private static byte[] TcpPseudoHeader(byte[] packet, int tcpOffset)
    {
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        var tcpLength = totalLength - tcpOffset;
        var buffer = new byte[12 + tcpLength];
        packet.AsSpan(12, 8).CopyTo(buffer); // src/dst IP
        buffer[8] = 0;
        buffer[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10, 2), (ushort)tcpLength);
        packet.AsSpan(tcpOffset, tcpLength).CopyTo(buffer.AsSpan(12));
        buffer[12 + 16] = 0; // checksum внутри TCP-заголовка
        buffer[12 + 17] = 0;
        return buffer;
    }

    private static byte[] BuildClientHelloPacket(string host)
    {
        var hostBytes = System.Text.Encoding.ASCII.GetBytes(host);
        var hello = new List<byte>
        {
            0x03, 0x03,
        };
        hello.AddRange(new byte[32]);
        hello.Add(0); // session id
        hello.AddRange(new byte[] { 0x00, 0x02, 0x13, 0x01 }); // cipher suites
        hello.AddRange(new byte[] { 0x01, 0x00 }); // compression

        var sni = new List<byte>();
        sni.AddRange(new byte[] { 0x00, 0x00 }); // server_name
        var nameLength = 1 + 2 + hostBytes.Length;
        var extensionLength = 2 + nameLength;
        sni.AddRange(new byte[] { (byte)(extensionLength >> 8), (byte)extensionLength });
        sni.AddRange(new byte[] { (byte)(nameLength >> 8), (byte)nameLength, 0x00 });
        sni.AddRange(new byte[] { (byte)(hostBytes.Length >> 8), (byte)hostBytes.Length });
        sni.AddRange(hostBytes);

        hello.AddRange(new byte[] { (byte)(sni.Count >> 8), (byte)sni.Count });
        hello.AddRange(sni);

        var handshake = new List<byte> { 0x01 };
        handshake.AddRange(new[]
        {
            (byte)(hello.Count >> 16), (byte)(hello.Count >> 8), (byte)hello.Count,
        });
        handshake.AddRange(hello);

        var record = new List<byte> { 0x16, 0x03, 0x01 };
        record.AddRange(new[] { (byte)(handshake.Count >> 8), (byte)handshake.Count });
        record.AddRange(handshake);

        var tcp = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(0, 2), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2, 2), 443);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4, 4), 1000);
        tcp[12] = 0x50;
        tcp[13] = 0x18;

        var packet = new byte[20 + tcp.Length + record.Count];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = 6;
        packet[12] = 10;
        packet[15] = 1;
        packet[16] = 142;
        packet[17] = 250;
        packet[19] = 1;
        tcp.CopyTo(packet.AsSpan(20));
        record.CopyTo(packet.AsSpan(40));
        return packet;
    }

    private static async Task RunZapretLiveAsync()
    {
        Console.WriteLine("Zapret live (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        await ZapretService.EnsureAsync(null, CancellationToken.None);
        using var zapret = new ZapretService();
        var urls = new[]
        {
            "https://www.youtube.com/generate_204",
            "https://www.youtube.com/",
            "https://discord.com/",
            "https://cp.cloudflare.com/generate_204",
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await zapret.StartAsync(new Progress<string>(line => Console.WriteLine("  … " + line)),
                timeout.Token, presetIndex: 0, gameMode: false);
            await Task.Delay(1500, timeout.Token);
            foreach (var url in urls)
            {
                var code = await CurlStatusAsync(url, timeout.Token);
                Console.WriteLine($"  {new Uri(url).Host}: {code}");
            }

            var video = await DesyncService.ProbeYoutubeVideoAsync(timeout.Token);
            Console.WriteLine($"  видео: {(video ? "загружается" : "не загружается")}");
        }
        catch (Exception exception)
        {
            Check(false, "zapret live — " + exception.Message);
        }
        finally
        {
            zapret.Stop();
            await Task.Delay(1000, CancellationToken.None);
        }

        var after = await CurlStatusAsync("https://cp.cloudflare.com/generate_204", CancellationToken.None);
        Check(after is >= 200 and < 400, "после остановки zapret интернет работает (HTTP " + after + ")");
    }

    private static async Task RunDesyncLiveAsync()
    {
        Console.WriteLine("Свой обход DPI live (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        DesyncService.Trace = line => Console.WriteLine("    [dpi] " + line);

        var urls = new[]
        {
            "https://www.youtube.com/generate_204",
            "https://www.youtube.com/",
            "https://discord.com/",
            "https://cp.cloudflare.com/generate_204",
        };
        var strategies = DesyncService.Strategies
            .Where(strategy => (strategy.Tcp == DesyncTcpMode.FakeMultiDisorder &&
                                strategy.FakeTtl is 64 or 128) ||
                               strategy.Tcp == DesyncTcpMode.IpFrag)
            .ToList();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        foreach (var strategy in strategies)
        {
            using var service = new DesyncService();
            try
            {
                service.Start(strategy, null, blockQuic: true);
                await Task.Delay(300, timeout.Token);
                var parts = new List<string>();
                foreach (var url in urls)
                {
                    var code = await CurlStatusAsync(url, timeout.Token, maxTime: 6);
                    parts.Add($"{new Uri(url).Host}:{code}");
                }

                var video = await DesyncService.ProbeYoutubeVideoAsync(timeout.Token);
                parts.Add($"video:{(video ? 1 : 0)}");
                Console.WriteLine($"  {strategy.Name}: {string.Join(", ", parts)} " +
                                  $"(fakes {service.FakePackets}, splits {service.SplitPackets})");
            }
            catch (Exception exception)
            {
                Check(false, $"{strategy.Name}: {exception.Message}");
            }
            finally
            {
                service.Stop();
                await Task.Delay(400, CancellationToken.None);
            }
        }

        var after = await CurlStatusAsync("https://cp.cloudflare.com/generate_204", CancellationToken.None);
        Check(after is >= 200 and < 400, "после остановки прямой интернет работает (HTTP " + after + ")");
    }

    /// <summary>
    /// Видео YouTube через свой обход DPI: страница, адрес потока и реальная загрузка
    /// с googlevideo — с блокировкой QUIC и без. Нужны права администратора.
    /// </summary>
    private static async Task RunVideoLiveAsync()
    {
        Console.WriteLine("Видео YouTube через свой обход DPI (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        DesyncService.Trace = line =>
        {
            if (line.Contains("blind", StringComparison.Ordinal) ||
                line.Contains("видео", StringComparison.Ordinal) ||
                line.Contains("ClientHello", StringComparison.Ordinal))
            {
                Console.WriteLine("    [dpi] " + line);
            }
        };
        var strategies = DesyncService.Strategies
            .Where(strategy => strategy.Tcp == DesyncTcpMode.FakeMultiDisorder &&
                               strategy.FakeTtl is 128)
            .ToList();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        foreach (var blockQuic in new[] { false, true })
        {
            foreach (var strategy in strategies)
            {
                using var service = new DesyncService();
                try
                {
                    service.Start(strategy, null, blockQuic);
                    await Task.Delay(300, timeout.Token);
                    var page = await CurlStatusAsync("https://www.youtube.com/", timeout.Token, maxTime: 8);
                    var detail = await DesyncService.ProbeVideoDetailAsync(timeout.Token);
                    var hostRoot = detail.Host is null
                        ? "-"
                        : (await CurlStatusAsync($"https://{detail.Host}/", timeout.Token, maxTime: 10)).ToString();
                    var redirector = (await CurlStatusAsync(
                        "https://redirector.googlevideo.com/", timeout.Token, maxTime: 10)).ToString();
                    var diagnosis = "-";
                    if (detail.Host is not null)
                    {
                        var hostDiag = await DesyncService.DiagnoseAsync(detail.Host, timeout.Token);
                        diagnosis = $"{hostDiag.Kind} ({hostDiag.Detail})";
                    }
                    Console.WriteLine(
                        $"  QUIC {(blockQuic ? "блокирован" : "фейк")} · {strategy.Name}: " +
                        $"YouTube:{page}, видео:{(detail.Ok ? "да" : "нет")} — {detail.Detail}; " +
                        $"корень видео-хоста:{hostRoot}, redirector:{redirector}, TLS-диагноз:{diagnosis} " +
                        $"(фейков {service.FakePackets}, разрезов {service.SplitPackets})");
                }
                catch (Exception exception)
                {
                    Check(false, $"{strategy.Name}: {exception.Message}");
                }
                finally
                {
                    service.Stop();
                    await Task.Delay(400, CancellationToken.None);
                }
            }
        }

        var after = await CurlStatusAsync("https://cp.cloudflare.com/generate_204", CancellationToken.None);
        Check(after is >= 200 and < 400, "после остановки прямой интернет работает (HTTP " + after + ")");
    }

    private static async Task RunBlockTypeAsync()    {
        Console.WriteLine("Тип блокировки (TCP + TLS напрямую):");
        var hosts = new[]
        {
            "www.youtube.com",
            "discord.com",
            "x.com",
            "t.me",
            "www.google.com",
            "cp.cloudflare.com",
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        foreach (var host in hosts)
        {
            var diagnosis = await DesyncService.DiagnoseAsync(host, timeout.Token);
            var label = diagnosis.Kind switch
            {
                BlockKind.Ok => "OK (нет блокировки на уровне TCP/TLS)",
                BlockKind.Ip => "IP-блок (TCP timeout)",
                BlockKind.Tls => "SNI/TLS-блок (TCP есть, TLS обрывается)",
                _ => "DNS",
            };
            Console.WriteLine($"  {host,-20} {diagnosis.Address,-16} {label} — {diagnosis.Detail}");
        }
    }

    private static async Task RunTunBypassLiveAsync()
    {
        Console.WriteLine("Живой TUN-обход (нужны права администратора, через форвардер идёт только curl):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        TlsFragmentForwarder.Trace = line => Console.WriteLine("    [fwd] " + line);
        var urls = new[]
        {
            "https://cp.cloudflare.com/generate_204",
            "https://www.youtube.com/generate_204",
            "https://t.me/",
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        // Контроль: TUN ловит curl, но отправляет напрямую (без форвардера).
        await ProbeLiveAsync("контроль direct", StreamFragmentMode.None, "does-not-exist.exe",
            expectFragment: false, urls, timeout.Token);

        // Контроль: TUN + форвардер, но без фрагментации.
        await ProbeLiveAsync("форвардер без фрагментации", StreamFragmentMode.None, "curl.exe",
            expectFragment: false, urls, timeout.Token);

        var modes = new[]
        {
            StreamFragmentMode.SplitSni,
            StreamFragmentMode.SplitSniStart,
            StreamFragmentMode.SplitSniMid,
            StreamFragmentMode.SplitRecord,
            StreamFragmentMode.Split1,
            StreamFragmentMode.MultiSplit,
            StreamFragmentMode.SplitSniEnd,
            StreamFragmentMode.SplitStagger,
            StreamFragmentMode.SplitSniBand,
            StreamFragmentMode.SplitSlow,
            StreamFragmentMode.DisorderSni,
            StreamFragmentMode.MultiDisorder,
        };
        foreach (var mode in modes)
        {
            await ProbeLiveAsync(mode.ToString(), mode, "curl.exe", expectFragment: true, urls, timeout.Token);
        }

        var after = await CurlStatusAsync("https://cp.cloudflare.com/generate_204", CancellationToken.None);
        Check(after is >= 200 and < 400, "после остановки прямой интернет работает (HTTP " + after + ")");
        TlsFragmentForwarder.Trace = null;
    }

    private static async Task ProbeLiveAsync(
        string label,
        StreamFragmentMode mode,
        string onlyProcess,
        bool expectFragment,
        string[] urls,
        CancellationToken cancellationToken)
    {
        var service = new TunBypassService();
        try
        {
            await service.StartAsync(
                mode,
                new AppSettings { TunBypassBlockQuic = true },
                new Progress<string>(line => Console.WriteLine("  … " + line)),
                cancellationToken,
                onlyProcess);
            await Task.Delay(1500, cancellationToken);

            var ok = 0;
            var parts = new List<string>();
            foreach (var url in urls)
            {
                var code = await CurlStatusAsync(url, cancellationToken);
                parts.Add($"{new Uri(url).Host}:{code}");
                if (code is >= 200 and < 400)
                {
                    ok++;
                }
            }

            Console.WriteLine($"  {label}: {string.Join(", ", parts)}");
            if (ok > 0)
            {
                Console.WriteLine($"  ok   {label}: открылось {ok}/{urls.Length}");
            }
            else
            {
                Console.WriteLine($"  FAIL {label}: открылось 0/{urls.Length}");
                _failed++;
            }

            if (expectFragment)
            {
                Check(service.FragmentedConnections > 0,
                    $"{label}: форвардер разрезал {service.FragmentedConnections} соединений " +
                    $"(принято {service.AcceptedConnections})");
            }
            else
            {
                Console.WriteLine($"  …    {label}: принято {service.AcceptedConnections}, " +
                                  $"разрезано {service.FragmentedConnections}");
            }
        }
        catch (Exception exception)
        {
            Check(false, $"{label}: {exception.Message}");
        }
        finally
        {
            await service.StopAsync();
            await Task.Delay(1500, CancellationToken.None);
        }
    }

    private static async Task<int> CurlStatusAsync(string url, CancellationToken cancellationToken, int maxTime = 12)
    {
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        var startInfo = new ProcessStartInfo(curl)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-4", "-s", "-o", "NUL", "-w", "%{http_code}", "--max-time", maxTime.ToString(),
                     "--noproxy", "*", "--http1.1", url,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 0;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return int.TryParse(output.Trim(), out var code) ? code : 0;
    }

    private static async Task RunTunBypassStartAsync()    {
        Console.WriteLine("TUN-обход (ожидается ошибка без прав администратора):");
        await using var service = new TunBypassService();
        try
        {
            await service.StartAsync(
                StreamFragmentMode.SplitSni,
                new AppSettings { TunBypassBlockQuic = true },
                new Progress<string>(line => Console.WriteLine("  … " + line)),
                CancellationToken.None);
            Check(service.IsRunning, "TUN-обход запустился (есть права администратора)");
            await service.StopAsync();
        }
        catch (Exception exception)
        {
            Console.WriteLine("  ожидаемо: " + exception.Message);
            Check(!service.IsRunning, "TUN-обход без прав завершился без зависания");
        }
    }

    private static async Task RunTunBypassAutoTuneAsync()
    {
        Console.WriteLine("Автоподключение TUN-обхода (нужны права администратора):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var service = new TunBypassService();
        try
        {
            var results = await service.AutoTuneAsync(
                new AppSettings { TunBypassBlockQuic = true },
                new Progress<string>(line => Console.WriteLine("  … " + line)),
                timeout.Token);

            Check(results.Count == TlsFragmentForwarder.Strategies.Count,
                $"автоподбор вернул {results.Count} стратегий из {TlsFragmentForwarder.Strategies.Count}");
            foreach (var result in results)
            {
                Console.WriteLine($"  {(result.Ok > 0 ? "ок" : "— ")} {result.Name}: " +
                                  $"{result.Ok}/{result.Total} (YouTube {(result.YoutubeOk ? "да" : "нет")}, " +
                                  $"Telegram {(result.TelegramOk ? "да" : "нет")}), " +
                                  $"{result.AverageMs} мс");
            }

            var best = results
                .Where(result => result.YoutubeOk || result.TelegramOk)
                .OrderByDescending(result => result.YoutubeOk)
                .ThenByDescending(result => result.TelegramOk)
                .ThenByDescending(result => result.Ok)
                .ThenBy(result => result.AverageMs)
                .FirstOrDefault();
            if (best is not null)
            {
                Console.WriteLine($"  форвардер: выбрана «{best.Name}» ({best.Ok}/{best.Total})");
                Check(true, "форвардер открыл YouTube или Telegram");
                return;
            }

            // Форвардер цели не открыл (ожидаемо на этом провайдере) — как в MainWindow,
            // пробуем сильный WinDivert-движок (fake+multidisorder).
            Console.WriteLine("  форвардер цели не открыл — пробую WinDivert-движок…");
            if (!DesyncService.IsInstalled)
            {
                Check(false, "WinDivert не установлен, а форвардер цели не открыл");
                return;
            }

            await DesyncService.EnsureAsync(
                new Progress<string>(line => Console.WriteLine("  … " + line)), timeout.Token);
            var desync = new DesyncService();
            try
            {
                var desyncResults = await desync.AutoTuneAsync(
                    new Progress<string>(line => Console.WriteLine("  … " + line)),
                    timeout.Token,
                    blockQuic: true);
                var desyncBest = desyncResults
                    .Where(result => result.Ok > 0)
                    .OrderByDescending(result => result.VideoOk)
                    .ThenByDescending(result => result.YoutubeOk)
                    .ThenByDescending(result => result.Ok)
                    .ThenBy(result => result.AverageMs)
                    .FirstOrDefault();
                if (desyncBest is not null && (desyncBest.YoutubeOk || desyncBest.VideoOk || desyncBest.TelegramOk))
                {
                    Console.WriteLine($"  WinDivert: выбрана «{desyncBest.Name}» — " +
                                      $"YouTube {(desyncBest.YoutubeOk ? "да" : "нет")}, " +
                                      $"видео {(desyncBest.VideoOk ? "да" : "нет")}, " +
                                      $"Telegram {(desyncBest.TelegramOk ? "да" : "нет")}");
                    Check(true, "WinDivert-движок открывает YouTube/Telegram (форвардер не умеет)");
                }
                else
                {
                    Check(false, "ни форвардер, ни WinDivert не открыли цели");
                }
            }
            finally
            {
                desync.Stop();
            }
        }
        catch (OperationCanceledException)
        {
            Check(false, "автоподключение: превышен лимит времени (15 мин)");
        }
        catch (Exception exception)
        {
            Check(false, "автоподключение: " + exception.Message);
        }
        finally
        {
            await service.StopAsync();
            await Task.Delay(1000, CancellationToken.None);
        }
    }

    private static async Task RunForwarderE2EAsync()    {
        Console.WriteLine("Форвардер + реальный TLS через SOCKS5 (без TUN и WinDivert):");
        const string url = "https://cp.cloudflare.com/generate_204";
        var successes = 0;
        foreach (var strategy in TlsFragmentForwarder.Strategies)
        {
            var forwarder = new TlsFragmentForwarder();
            try
            {
                await forwarder.StartAsync(strategy.Mode);
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{forwarder.Port}"),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(8),
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    successes++;
                    Console.WriteLine($"  ok   {strategy.Name}: TLS через форвардер ({(int)response.StatusCode})");
                }
                else
                {
                    Console.WriteLine($"  —    {strategy.Name}: HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"  —    {strategy.Name}: {exception.Message}");
            }
            finally
            {
                await forwarder.StopAsync();
            }
        }

        // На конкретном провайдере перестановка/мультиразрез могут не пройти:
        // важно, что сам форвардер умеет установить TLS через SOCKS5.
        Check(successes > 0, "форвардер: TLS через SOCKS5 работает");
    }

    /// <summary>
    /// Видео YouTube без WinDivert и без узла: SOCKS5-форвардер режет TLS ClientHello,
    /// затем проверяются страница и реальный поток videoplayback. Нужен только пользователь,
    /// права администратора не требуются. Помогает понять, где именно рвётся видео:
    /// страница открывается, а googlevideo — нет.
    /// </summary>
    private static async Task RunVideoDiagAsync()
    {
        Console.WriteLine("Видео YouTube через SOCKS5-форвардер (без WinDivert, без админа):");
        const string watchUrl = "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        var modes = TlsFragmentForwarder.Strategies
            .Select(strategy => (strategy.Name, strategy.Mode))
            .Append(("Без фрагментации (контроль)", StreamFragmentMode.None));

        foreach (var (name, mode) in modes)
        {
            Console.WriteLine($"  — {name}:");
            var forwarder = new TlsFragmentForwarder();
            TlsFragmentForwarder.Trace = line => Console.WriteLine("      [fwd] " + line);
            try
            {
                await forwarder.StartAsync(mode);
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{forwarder.Port}"),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    AutomaticDecompression = DecompressionMethods.All,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
                client.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", "CONSENT=YES+1");

                var pageTimer = Stopwatch.StartNew();
                string page;
                try
                {
                    using var response = await client.GetAsync(watchUrl, HttpCompletionOption.ResponseContentRead);
                    page = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(
                        $"      страница: HTTP {(int)response.StatusCode}, {page.Length} симв., " +
                        $"{pageTimer.ElapsedMilliseconds} мс");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    Console.WriteLine($"      страница: ошибка — {Describe(exception)}");
                    continue;
                }

                if (!page.Contains("ytInitialPlayerResponse", StringComparison.Ordinal))
                {
                    Console.WriteLine("      страница без ytInitialPlayerResponse — YouTube отдал заглушку");
                }

                var videoUrl = DesyncService.FindVideoUrl(page);
                if (videoUrl is null)
                {
                    Console.WriteLine("      videoplayback в странице не найден");
                    continue;
                }

                var host = new Uri(videoUrl).Host;
                Console.WriteLine("      видео-хост: " + host);
                var videoTimer = Stopwatch.StartNew();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 262143);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    var buffer = new byte[65536];
                    long total = 0;
                    while (total < 262144)
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                    }

                    Console.WriteLine(
                        $"      видео: HTTP {(int)response.StatusCode}, {total} байт, " +
                        $"{videoTimer.ElapsedMilliseconds} мс");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                      or IOException)
                {
                    Console.WriteLine($"      видео: ошибка — {Describe(exception)}");
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("      форвардер не запустился: " + Describe(exception));
            }
            finally
            {
                TlsFragmentForwarder.Trace = null;
                await forwarder.StopAsync();
            }
        }
    }

    /// <summary>
    /// Domain fronting: подключаемся к тому же IP с другим (незаблокированным) SNI,
    /// а настоящий видео-хост передаём внутри TLS в заголовке Host. Сертификат
    /// *.googlevideo.com подходит и для видео-хоста. Нужны права администратора.
    /// </summary>
    private static async Task RunVideoFrontAsync()
    {
        Console.WriteLine("Domain fronting для видео (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        var strategy = DesyncService.Strategies.First(item =>
            item.Tcp == DesyncTcpMode.FakeMultiDisorder && item.FakeTtl == 128);
        using var service = new DesyncService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            service.Start(strategy, null, blockQuic: true);
            await Task.Delay(300, timeout.Token);
            var page = await DesyncService.CurlTextAsync(
                "https://www.youtube.com/watch?v=jNQXAC9IVRw", 30, timeout.Token);
            var videoUrl = string.IsNullOrEmpty(page) ? null : DesyncService.FindVideoUrl(page);
            if (videoUrl is null)
            {
                Check(false, "адрес видеопотока не найден");
                return;
            }

            var host = new Uri(videoUrl).Host;
            var own = (await Dns.GetHostAddressesAsync(host, timeout.Token))
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
                .Select(item => item.ToString())
                .ToList();
            var foreign = (await Dns.GetHostAddressesAsync("redirector.googlevideo.com", timeout.Token))
                .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
                .Select(item => item.ToString())
                .ToList();
            Console.WriteLine($"  видео-хост {host} → {string.Join(", ", own)}");

            var worked = false;
            foreach (var front in new[] { "redirector.googlevideo.com", "www.google.com" })
            {
                foreach (var ip in own.Concat(foreign).Distinct())
                {
                    var (code, bytes) = await CurlFrontAsync(videoUrl, front, ip, timeout.Token);
                    Console.WriteLine($"  SNI {front,-28} → {ip,-16} HTTP {code}, {bytes} байт");
                    if (code is 200 or 206 && bytes > 1000)
                    {
                        worked = true;
                        break;
                    }
                }

                if (worked)
                {
                    break;
                }
            }

            Check(worked, "fronting отдаёт видео");
        }
        finally
        {
            service.Stop();
        }
    }

    /// <summary>
    /// Запрос видео через чужой SNI: URL и проверка сертификата — для front-хоста,
    /// адрес — заданный, настоящий видео-хост — в заголовке Host.
    /// </summary>
    private static async Task<(int Code, long Bytes)> CurlFrontAsync(
        string videoUrl,
        string frontHost,
        string connectIp,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(videoUrl);
        var frontUrl = $"https://{frontHost}{uri.PathAndQuery}";
        var path = Path.Combine(Path.GetTempPath(), "vienlink-front-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
                     {
                         "-4", "-s", "-L", "-o", path, "-w", "%{http_code}", "--http1.1",
                         "--max-time", "20", "--noproxy", "*", "-r", "0-131071",
                         "--connect-to", $"{frontHost}:443:{connectIp}:443",
                         "-H", $"Host: {uri.Host}",
                         frontUrl,
                     })
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

    /// <summary>
    /// Перебирает ВСЕ режимы zapret (fake, split, ipfrag, hopbyhop, syndata, rstack…)
    /// и проверяет настоящий видео-узел googlevideo, а не только redirector.
    /// Нужны права администратора.
    /// </summary>
    private static async Task RunZapretVideoAsync()
    {
        Console.WriteLine("Все режимы zapret против настоящего видео-узла (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        if (!ZapretService.IsInstalled)
        {
            Check(false, "zapret не установлен");
            return;
        }

        var hostlist = Path.Combine(Path.GetTempPath(), "vienlink-zapret-video.txt");
        File.WriteAllLines(hostlist, new[] { "youtube.com", "googlevideo.com", "ytimg.com" });
        using var zapret = new ZapretService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            // Официальный режим открывает страницу — берём из неё настоящий адрес потока.
            await zapret.StartAsync(null, timeout.Token, 0, gameMode: false, hostlistPath: hostlist);
            await Task.Delay(1500, timeout.Token);
            var page = await DesyncService.CurlTextAsync(
                "https://www.youtube.com/watch?v=jNQXAC9IVRw", 30, timeout.Token);
            zapret.Stop();
            var videoUrl = string.IsNullOrEmpty(page) ? null : DesyncService.FindVideoUrl(page);
            if (videoUrl is null)
            {
                Check(false, "не удалось получить адрес видеопотока");
                return;
            }

            var host = new Uri(videoUrl).Host;
            Console.WriteLine($"  видео-хост: {host}");
            var worked = false;
            for (var index = 0; index < ZapretService.Presets.Count; index++)
            {
                var preset = ZapretService.Presets[index];
                try
                {
                    await zapret.StartAsync(null, timeout.Token, index, gameMode: false, hostlistPath: hostlist);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"  {index,2}. {preset.Name,-28} не запустился — {Describe(exception)}");
                    continue;
                }

                await Task.Delay(1200, timeout.Token);
                var root = await CurlStatusAsync($"https://{host}/", timeout.Token, maxTime: 8);
                var (code, bytes) = await DesyncService.CurlRangeResolvedAsync(
                    videoUrl, null, 20, timeout.Token);
                Console.WriteLine($"  {index,2}. {preset.Name,-28} корень {root,3}  поток HTTP {code}, {bytes} байт");
                zapret.Stop();
                await Task.Delay(300, CancellationToken.None);
                if (code is 200 or 206 && bytes > 1000)
                {
                    worked = true;
                    break;
                }
            }

            Check(worked, "какой-то режим zapret открывает видео");
        }
        finally
        {
            zapret.Stop();
            try
            {
                File.Delete(hostlist);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Перебирает все видео-хосты из страницы YouTube: у разных форматов потока
    /// бывают разные узлы googlevideo, и не все блокируются по IP.
    /// </summary>
    private static async Task RunVideoMultiAsync()
    {
        Console.WriteLine("Все видео-хосты из страницы YouTube (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        DesyncService.Trace = null;
        var strategy = DesyncService.Strategies.First(item =>
            item.Tcp == DesyncTcpMode.FakeMultiDisorder && item.FakeTtl == 128);
        using var service = new DesyncService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            service.Start(strategy, null, blockQuic: true);
            await Task.Delay(300, timeout.Token);
            var page = await DesyncService.CurlTextAsync(
                "https://www.youtube.com/watch?v=jNQXAC9IVRw", 30, timeout.Token);
            if (string.IsNullOrEmpty(page))
            {
                Check(false, "страница YouTube не открылась");
                return;
            }

            var urls = DesyncService.FindVideoUrls(page);
            var groups = urls
                .GroupBy(url => new Uri(url).Host, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Console.WriteLine($"  адресов потоков: {urls.Count}, уникальных хостов: {groups.Count}");
            var worked = 0;
            foreach (var group in groups)
            {
                var root = await CurlStatusAsync($"https://{group.Key}/", timeout.Token, maxTime: 8);
                var (code, bytes) = await DesyncService.CurlRangeResolvedAsync(
                    group.First(), null, 20, timeout.Token);
                Console.WriteLine(
                    $"  {group.Key,-46} форматов {group.Count(),2}  корень {root,3}  поток HTTP {code}, {bytes} байт");
                if (code is 200 or 206 && bytes > 1000)
                {
                    worked++;
                }
            }

            Check(worked > 0, "хотя бы один видео-хост отдаёт поток");
        }
        finally
        {
            service.Stop();
        }
    }

    /// <summary>
    /// Проверяет именно `ProbeVideoDetailAsync` (то, что использует автоподбор):
    /// страница YouTube, несколько хостов googlevideo, порог 64 КБ. Нужен админ.
    /// </summary>
    private static async Task RunDesyncVideoProbeAsync()
    {
        Console.WriteLine("Проба видео автоподбора (ProbeVideoDetailAsync, нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        DesyncService.Trace = line => Console.WriteLine("    [dpi] " + line);
        var strategy = DesyncService.Strategies.First(item =>
            item.Tcp == DesyncTcpMode.FakeMultiDisorder && item.FakeTtl == 128);
        using var service = new DesyncService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            service.Start(strategy, null, blockQuic: true);
            await Task.Delay(300, timeout.Token);
            var detail = await DesyncService.ProbeVideoDetailAsync(timeout.Token);
            Console.WriteLine($"  видео {(detail.Ok ? "идёт" : "не идёт")}: {detail.Detail}");
            Console.WriteLine($"  страница: {(detail.PageOk ? "открылась" : "нет")}, " +
                              $"хост: {detail.Host ?? "—"}, HTTP {detail.Code}, {detail.Bytes} байт");
        }
        catch (Exception exception)
        {
            Check(false, "проба видео — " + Describe(exception));
        }
        finally
        {
            DesyncService.Trace = null;
            service.Stop();
        }
    }

    /// <summary>
    /// Проверяет QUIC-рукопожатие (HTTP/3) напрямую и через стратегию «Только QUIC-фейк»:
    /// именно она нужна для видео YouTube, когда TCP открывается, а поток режется.
    /// Нужен админ (WinDivert).
    /// </summary>
    private static async Task RunQuicProbeAsync()
    {
        Console.WriteLine("QUIC (HTTP/3) через свой обход DPI (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var host in new[] { "www.youtube.com", "www.google.com", "redirector.googlevideo.com" })
        {
            Console.WriteLine($"  напрямую {host}: {(await DesyncService.ProbeQuicAsync(host, timeout.Token) ? "да" : "нет")}");
        }

        using var service = new DesyncService();
        try
        {
            foreach (var ttl in new byte[] { 2, 4, 8, 64, 128 })
            {
                var strategy = new DesyncStrategy(
                    $"Только QUIC-фейк, TTL {ttl}", DesyncTcpMode.None, true, "тест", ttl);
                service.Start(strategy, null, blockQuic: false);
                await Task.Delay(300, timeout.Token);
                var youtube = await DesyncService.ProbeQuicAsync("www.youtube.com", timeout.Token);
                var google = await DesyncService.ProbeQuicAsync("www.google.com", timeout.Token);
                var video = google && await DesyncService.ProbeQuicAsync("redirector.googlevideo.com", timeout.Token);
                Console.WriteLine($"  TTL {ttl,3}: YouTube {(youtube ? "да" : "нет")}, " +
                                  $"Google {(google ? "да" : "нет")}, googlevideo {(video ? "да" : "нет")} " +
                                  $"(фейков {service.FakePackets})");
                service.Stop();
                if (youtube || google)
                {
                    Check(true, "QUIC-фейк открывает HTTP/3");
                    return;
                }
            }

            Check(false, "QUIC-фейк не открыл HTTP/3 ни с одним TTL");
        }
        catch (Exception exception)
        {
            Check(false, "QUIC-фейк — " + Describe(exception));
        }
        finally
        {
            service.Stop();
        }
    }

    /// <summary>
    /// Видео через другой узел Google: локальный GGC провайдера часто режется по IP,
    /// поэтому пробуем тот же поток, но с адресом чужого узла Google (SNI и Host те же).
    /// </summary>
    private static async Task RunVideoFallbackAsync()
    {
        Console.WriteLine("Видео через другой узел Google (нужен админ):");
        var principal = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent());
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            Check(false, "нужны права администратора");
            return;
        }

        DesyncService.Trace = line =>
        {
            if (line.Contains("видео", StringComparison.Ordinal))
            {
                Console.WriteLine("    [dpi] " + line);
            }
        };

        var strategy = DesyncService.Strategies.First(item =>
            item.Tcp == DesyncTcpMode.FakeMultiDisorder && item.FakeTtl == 128);
        using var service = new DesyncService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            service.Start(strategy, null, blockQuic: true);
            await Task.Delay(300, timeout.Token);
            var detail = await DesyncService.ProbeVideoDetailAsync(timeout.Token);
            Console.WriteLine($"  обычный адрес: {(detail.Ok ? "открылся" : "нет")} — {detail.Detail}");
            if (detail.Url is null)
            {
                Check(false, "адрес потока не найден");
                return;
            }

            var candidates = new List<string>();
            foreach (var name in new[] { "redirector.googlevideo.com", "www.youtube.com", "i.ytimg.com" })
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(name, timeout.Token);
                    candidates.AddRange(addresses
                        .Where(item => item.AddressFamily == AddressFamily.InterNetwork)
                        .Select(item => item.ToString()));
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    Console.WriteLine($"  DNS {name}: {Describe(exception)}");
                }
            }

            var own = new HashSet<string>();
            try
            {
                foreach (var address in await Dns.GetHostAddressesAsync(detail.Host!, timeout.Token))
                {
                    own.Add(address.ToString());
                }
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
            }

            var worked = false;
            foreach (var ip in candidates.Distinct().Where(ip => !own.Contains(ip)))
            {
                var (code, bytes) = await DesyncService.CurlRangeResolvedAsync(
                    detail.Url, ip, 20, timeout.Token);
                Console.WriteLine($"  через {ip}: HTTP {code}, {bytes} байт");
                if (code is 200 or 206 && bytes > 1000)
                {
                    worked = true;
                    break;
                }
            }

            Check(worked, "видео открывается через другой узел Google");
        }
        finally
        {
            DesyncService.Trace = null;
            service.Stop();
        }
    }

    /// <summary>
    /// Проверяет конкретный видео-хост (например, локальный GGC googlevideo) через
    /// SOCKS5-форвардер: TCP, затем TLS с разными разрезами ClientHello. Без админа.
    /// Так видно, блокируется ли SNI или весь IP.
    /// </summary>
    private static async Task RunVideoHostAsync(string host)
    {
        Console.WriteLine($"Видео-хост {host} через SOCKS5-форвардер (без админа):");
        var addresses = await Dns.GetHostAddressesAsync(host);
        foreach (var address in addresses.Where(item => item.AddressFamily == AddressFamily.InterNetwork))
        {
            var tcp = new TcpClient();
            var timer = Stopwatch.StartNew();
            var ok = false;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(address, 443, limit.Token);
                ok = true;
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
            }
            finally
            {
                tcp.Close();
            }

            Console.WriteLine($"  TCP {address}: {(ok ? "открыт" : "нет")} ({timer.ElapsedMilliseconds} мс)");
        }

        var modes = TlsFragmentForwarder.Strategies
            .Select(strategy => (strategy.Name, strategy.Mode))
            .Append(("Без фрагментации (контроль)", StreamFragmentMode.None));
        var url = $"https://{host}/";
        foreach (var (name, mode) in modes)
        {
            var forwarder = new TlsFragmentForwarder();
            try
            {
                await forwarder.StartAsync(mode);
                var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "curl.exe"))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var argument in new[]
                         {
                             "-4", "-s", "-o", "NUL", "-w", "%{http_code}", "--max-time", "12",
                             "-x", $"socks5h://127.0.0.1:{forwarder.Port}", url,
                         })
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                var output = process is null ? "0" : await process.StandardOutput.ReadToEndAsync();
                if (process is not null)
                {
                    await process.WaitForExitAsync();
                }

                Console.WriteLine($"  {name,-32} HTTP {output.Trim()} " +
                                  $"(разрезано {forwarder.FragmentedConnections}/{forwarder.AcceptedConnections})");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"  {name,-32} ошибка — {Describe(exception)}");
            }
            finally
            {
                await forwarder.StopAsync();
            }
        }
    }

    private static string Describe(Exception exception)
    {
        var baseException = exception.GetBaseException();
        var message = baseException.Message;
        return baseException is TaskCanceledException or OperationCanceledException
            ? "таймаут"
            : baseException.GetType().Name + ": " + message;
    }

    private static async Task RunForwarderTestAsync()    {
        Console.WriteLine("SOCKS5-форвардер TUN-обхода (без администратора):");
        var host = "www.youtube.com";
        var packet = BuildClientHelloPacket(host);
        DesyncService.TryParseClientHello(packet, out var hello);
        var tlsPayload = packet.AsSpan(hello.PayloadOffset, hello.PayloadLength).ToArray();
        var sniRelative = hello.SniOffset - hello.PayloadOffset;

        var modes = TlsFragmentForwarder.Strategies
            .Select(strategy => strategy.Mode)
            .Append(StreamFragmentMode.None)
            .Distinct()
            .ToArray();

        var greetingChecked = false;
        foreach (var mode in modes)
        {
            var label = mode.ToString();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var destination = new TcpListener(IPAddress.Loopback, 0);
            destination.Start();
            var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;

            var firstRead = -1;
            var serverTask = Task.Run(async () =>
            {
                using var server = await destination.AcceptTcpClientAsync(timeout.Token);
                var stream = server.GetStream();
                var buffer = new byte[65536];
                var total = 0;
                while (total < buffer.Length)
                {
                    int read;
                    try
                    {
                        // 300 мс тишины — запрос закончился (OOB-байт сервер не читает).
                        read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token)
                            .AsTask().WaitAsync(TimeSpan.FromMilliseconds(300));
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    if (firstRead < 0)
                    {
                        firstRead = read;
                    }

                    total += read;
                }

                await stream.WriteAsync("ok"u8.ToArray(), timeout.Token);
                return buffer.AsSpan(0, total).ToArray();
            }, timeout.Token);

            var forwarder = new TlsFragmentForwarder();
            try
            {
                await forwarder.StartAsync(mode);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, forwarder.Port, timeout.Token);
                client.NoDelay = true;
                var stream = client.GetStream();

                await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
                var greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting, timeout.Token);
                if (!greetingChecked)
                {
                    Check(greeting[0] == 5 && greeting[1] == 0, "SOCKS5: приветствие без пароля");
                    greetingChecked = true;
                }

                var request = new byte[10];
                request[0] = 5;
                request[1] = 1;
                request[3] = 1;
                request[4] = 127;
                request[7] = 1;
                request[8] = (byte)(destinationPort >> 8);
                request[9] = (byte)destinationPort;
                await stream.WriteAsync(request, timeout.Token);
                var reply = new byte[10];
                await stream.ReadExactlyAsync(reply, timeout.Token);
                Check(reply[0] == 5 && reply[1] == 0, $"{label}: SOCKS5-соединение установлено");

                await stream.WriteAsync(tlsPayload, timeout.Token);
                var response = new byte[2];
                await stream.ReadExactlyAsync(response, timeout.Token);
                Check(response[0] == (byte)'o' && response[1] == (byte)'k',
                    $"{label}: ответ сервера дошёл");

                var received = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
                var rewritesStream = mode is StreamFragmentMode.Oob
                    or StreamFragmentMode.RecordSplit or StreamFragmentMode.RecordSplitStart
                    or StreamFragmentMode.RecordSplitEnd or StreamFragmentMode.RecordSplitHalf;
                if (!rewritesStream)
                {
                    Check(received.AsSpan().SequenceEqual(tlsPayload),
                        $"{label}: TLS-поток передан без потерь");
                }
                else
                {
                    // RecordSplit добавляет заголовки записей, OOB-байт сервер не читает —
                    // целостность таких стратегий проверяет forwardertls (настоящий TLS).
                    Check(received.Length > 0, $"{label}: сервер получил данные");
                }

                if (mode == StreamFragmentMode.SplitSni)
                {
                    Check(firstRead > 0 && firstRead < tlsPayload.Length,
                        $"{label}: ClientHello разрезан (первый read {firstRead} из {tlsPayload.Length})");
                    Check(firstRead <= sniRelative + 1,
                        $"{label}: первый кусок заканчивается на SNI (read {firstRead}, SNI+1 {sniRelative + 1})");
                }
            }
            catch (Exception exception)
            {
                Check(false, $"{label}: {exception.Message}");
            }
            finally
            {
                await forwarder.StopAsync();
                destination.Stop();
            }
        }
    }

    /// <summary>
    /// UDP ASSOCIATE в форвардере: QUIC-датаграммы проходят через релей, а перед настоящим
    /// Initial к этому назначению уходит серия фейковых Initial с безобидным SNI (TTL 8).
    /// Проверяется без администратора: назначение — локальный UDP-эхо-сервер.
    /// </summary>
    private static async Task RunForwarderUdpAsync()
    {
        Console.WriteLine("UDP-релей форвардера (QUIC-фейк):");
        Check(QuicFake.IsAvailable && QuicFake.Template.Length == 1200,
            "QUIC-фейк: шаблон Initial в сборке (1200 байт)");
        Check(QuicFake.IsInitial(QuicFake.Template.Span), "QUIC-фейк: шаблон — длинный заголовок Initial");
        Check(!QuicFake.IsInitial(new byte[64]), "QUIC-фейк: короткая датаграмма — не Initial");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // Эхо-сервер: считает размеры и первые байты, отвечает «ok» только на настоящий Initial.
        using var echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var echoPort = ((IPEndPoint)echo.LocalEndPoint!).Port;
        var sizes = new System.Collections.Concurrent.ConcurrentBag<int>();
        var echoTask = Task.Run(async () =>
        {
            var buffer = new byte[65535];
            var from = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var result = await echo.ReceiveFromAsync(buffer, SocketFlags.None, from, timeout.Token);
                    sizes.Add(result.ReceivedBytes);
                    if (result.ReceivedBytes == 1300)
                    {
                        await echo.SendToAsync("ok"u8.ToArray(), SocketFlags.None, result.RemoteEndPoint,
                            timeout.Token);
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                                  or ObjectDisposedException)
            {
            }
        }, timeout.Token);

        var forwarder = new TlsFragmentForwarder();
        Socket? client = null;
        try
        {
            await forwarder.StartAsync(StreamFragmentMode.None);
            client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            using var control = new TcpClient();
            await control.ConnectAsync(IPAddress.Loopback, forwarder.Port, timeout.Token);
            var stream = control.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, timeout.Token);

            // UDP ASSOCIATE: адрес клиента нулевой — релей узнает его из датаграммы.
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, timeout.Token);
            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Check(reply[0] == 5 && reply[1] == 0, "SOCKS5: UDP ASSOCIATE принят");
            var relay = new IPEndPoint(new IPAddress(reply.AsSpan(4, 4)), (reply[8] << 8) | reply[9]);
            Check(relay.Address.Equals(IPAddress.Loopback) && relay.Port > 0,
                "SOCKS5: релей вернул адрес 127.0.0.1:" + relay.Port);

            var initial = new byte[1300];
            initial[0] = 0xC3;
            initial[1] = 0x00;
            initial[2] = 0x00;
            initial[3] = 0x00;
            initial[4] = 0x01;

            var request = BuildUdpDatagram(IPAddress.Loopback, echoPort, initial);
            await client.SendToAsync(request, SocketFlags.None, relay, timeout.Token);

            var incoming = new byte[65535];
            var fromRelay = new IPEndPoint(IPAddress.Any, 0);
            var answer = await client.ReceiveFromAsync(incoming, SocketFlags.None, fromRelay, timeout.Token);
            Check(answer.ReceivedBytes >= 10, "UDP-релей: ответ вернулся клиенту");
            var payloadOffset = incoming[3] == 1 ? 10 : 22;
            var answerPort = incoming[3] == 1
                ? (incoming[8] << 8) | incoming[9]
                : (incoming[20] << 8) | incoming[21];
            Check(answerPort == echoPort && incoming[3] == 1, "UDP-релей: в ответе адрес эхо-сервера");
            Check(incoming.AsSpan(payloadOffset, answer.ReceivedBytes - payloadOffset).SequenceEqual("ok"u8),
                "UDP-релей: полезная нагрузка ответа без потерь");

            await WaitForAsync(() => sizes.Contains(1300), timeout.Token);
            Check(sizes.Contains(1300), "UDP-релей: эхо-сервер получил настоящий Initial (1300 байт)");
            Check(forwarder.UdpAssociations == 1, "UDP-релей: ассоциация учтена");

            await WaitForAsync(() => forwarder.FakeQuicPackets >= QuicFake.Repeats, timeout.Token);
            Check(forwarder.FakeQuicPackets == QuicFake.Repeats,
                $"QUIC-фейк: отправлено {forwarder.FakeQuicPackets} копий перед настоящим");
            Check(sizes.Count(size => size == QuicFake.Template.Length) > 0,
                "QUIC-фейк: эхо-сервер видит поддельный Initial (1200 байт)");

            // Повторная датаграмма к тому же назначению фейк не дублирует (окно 10 с),
            // а короткая датаграмма Initial не считается вовсе.
            var seen = forwarder.FakeQuicPackets;
            await client.SendToAsync(BuildUdpDatagram(IPAddress.Loopback, echoPort, initial), SocketFlags.None,
                relay, timeout.Token);
            await Task.Delay(200, timeout.Token);
            Check(forwarder.FakeQuicPackets == seen, "QUIC-фейк: повторное назначение не заливается фейками");

            var shortPayload = new byte[64];
            await client.SendToAsync(BuildUdpDatagram(IPAddress.Loopback, echoPort, shortPayload),
                SocketFlags.None, relay, timeout.Token);
            await Task.Delay(200, timeout.Token);
            Check(forwarder.FakeQuicPackets == seen, "QUIC-фейк: короткая датаграмма пропускается как есть");
        }
        catch (Exception exception)
        {
            Check(false, "UDP-релей: " + exception.Message);
        }
        finally
        {
            client?.Dispose();
            await forwarder.StopAsync();
            echo.Dispose();
            try
            {
                await echoTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException)
            {
            }
        }
    }

    /// <summary>
    /// Правила выбора лучшей стратегии: те же функции использует автоподбор.
    /// </summary>
    private static void RunRankingTest()
    {
        Console.WriteLine("Выбор лучшей стратегии (правила):");
        Check(StrategyRanking.IsBetterPage(3, true, 10, 2, true, 100),
            "страница: больше доменов важнее скорости");
        Check(StrategyRanking.IsBetterPage(2, true, 10, 2, false, 900),
            "страница: превью важнее скорости");
        Check(StrategyRanking.IsBetterPage(2, true, 200, 2, true, 100),
            "страница: при равенстве выигрывает быстрее");
        Check(!StrategyRanking.IsBetterPage(2, true, 50, 2, true, 100),
            "страница: медленнее не выбирается");
        Check(!StrategyRanking.IsBetterPage(1, false, 999, 2, false, 1),
            "страница: меньше доменов не выбирается");

        Check(StrategyRanking.IsBetterVideo(true, false, false, 10, false, true, true, 900),
            "видео: поток важнее звука и SNI");
        Check(!StrategyRanking.IsBetterVideo(false, true, false, 900, true, false, false, 10),
            "видео: звук не заменяет поток");
        Check(StrategyRanking.IsBetterVideo(false, true, false, 50, false, false, true, 0),
            "видео: звук важнее SNI");
        Check(StrategyRanking.IsBetterVideo(true, false, false, 200, true, false, false, 100),
            "видео: быстрее поток выигрывает");
        Check(!StrategyRanking.IsBetterVideo(true, false, false, 50, true, false, false, 100),
            "видео: медленнее не выбирается");
        Check(StrategyRanking.IsBetterVideo(false, false, true, 0, false, false, false, 0),
            "видео: SNI-путь берётся, если больше ничего");
        Check(!StrategyRanking.IsBetterVideo(false, false, false, 0, false, false, true, 0),
            "видео: пустой результат не заменяет SNI");
    }

    /// <summary>
    /// Живая проверка обновляемой базы: скачивает файл из репозитория (при недоступности —
    /// через зеркала) и показывает, что легло в кэш. Запуск: presetsremote
    /// </summary>
    private static async Task RunPresetsRemoteAsync()
    {
        Console.WriteLine($"Обновляемая база операторов: {ProviderPresetStore.RemoteUrl}");
        var path = Path.Combine(Path.GetTempPath(), "vienlink-presets-remote-test.json");
        TryDelete(path);
        TryDelete(Path.ChangeExtension(path, ".remote.json"));
        TryDelete(Path.ChangeExtension(path, ".sync.json"));
        var previous = ProviderPresetStore.PathOverride;
        ProviderPresetStore.PathOverride = path;
        try
        {
            var changed = await ProviderPresetStore.RefreshRemoteAsync(CancellationToken.None, force: true);
            var sync = ProviderPresetStore.SyncStatus();
            Console.WriteLine($"Результат: {(changed ? "база обновлена" : "изменений нет")}, " +
                              $"записей {sync.Count}, успех {sync.LastOkUtc:u}");
            foreach (var preset in ProviderPresetStore.LoadShipped().Presets.Take(5))
            {
                Console.WriteLine($"  (зашитая) {preset.Key}: страница {preset.Page}, видео {preset.Video}");
            }

            Check(sync.LastOkUtc != default, "обновляемая база получена из репозитория");
        }
        finally
        {
            ProviderPresetStore.PathOverride = previous;
            TryDelete(path);
            TryDelete(Path.ChangeExtension(path, ".remote.json"));
            TryDelete(Path.ChangeExtension(path, ".sync.json"));
        }
    }

    /// <summary>
    /// База параметров обхода по операторам: подтверждение, ручной выбор, приоритеты
    /// и экспорт без личных данных (Wi-Fi SSID не попадают).
    /// </summary>
    private static void RunProviderPresetsTest()
    {
        Console.WriteLine("База параметров обхода по операторам:");
        var path = Path.Combine(Path.GetTempPath(), "vienlink-provider-presets-test.json");
        TryDelete(path);
        TryDelete(path + ".bak");
        var previous = ProviderPresetStore.PathOverride;
        ProviderPresetStore.PathOverride = path;
        try
        {
            const string key = "cell:25001:МТС";
            Check(ProviderPresetStore.Find(key) is null, "база пуста");

            ProviderPresetStore.RecordSuccess(key, "Oob", string.Empty, blockQuic: false);
            var preset = ProviderPresetStore.Find(key, out var origin);
            Check(preset is not null && preset.Page == "Oob" && origin == PresetOrigin.Device &&
                  preset.Successes == 1,
                "подтверждение сохранено на устройстве");
            Check(preset is not null && !preset.BlockQuic, "QUIC: режим релея запомнен");

            ProviderPresetStore.RecordSuccess(key, "RecordSplit", "Oob", blockQuic: true);
            preset = ProviderPresetStore.Find(key);
            Check(preset is not null && preset.Page == "RecordSplit" && preset.BlockQuic &&
                  preset.Successes == 2, "автоподбор обновил параметры (2 подтверждения)");

            ProviderPresetStore.RecordChoice(key, "Oob", string.Empty, blockQuic: false);
            preset = ProviderPresetStore.Find(key);
            Check(preset is not null && preset.Source == "manual" && preset.Successes == 2,
                "ручной выбор не считается подтверждением");

            ProviderPresetStore.RecordSuccess(key, "Split1", string.Empty, blockQuic: false);
            preset = ProviderPresetStore.Find(key);
            Check(preset is not null && preset.Page == "Oob" && preset.Successes == 3,
                "автоподбор не перезаписывает ручной выбор");

            ProviderPresetStore.RecordChoice("wifi:Lena11", "Oob", string.Empty, blockQuic: false);
            var export = ProviderPresetStore.ExportJson();
            Check(export.Contains(key, StringComparison.Ordinal) &&
                  !export.Contains("wifi:", StringComparison.OrdinalIgnoreCase),
                "экспорт: только операторы, без SSID");
            Check(ProviderPresetStore.ExportCount() == 1, "экспорт считает только операторов");

            // Видео по QUIC через релей: транспорт отмечается, подтверждения не растут.
            ProviderPresetStore.RecordVideo(key, "Oob", string.Empty, blockQuic: false, "quic");
            preset = ProviderPresetStore.Find(key);
            Check(preset is not null && preset.VideoTransport == "quic" &&
                  preset.VideoSeenUtc != default && preset.Successes == 3,
                "видео по QUIC отмечено без подтверждений");

            // Совместимость ключей: прежние версии хранили имя оператора в ключе.
            ProviderPresetStore.RecordSuccess("cell:25003:Билайн", "Oob", string.Empty, false);
            preset = ProviderPresetStore.Find("cell:25003", out origin);
            Check(preset is not null && preset.Page == "Oob" && origin == PresetOrigin.Device,
                "старый ключ с именем оператора находится по MCCMNC");

            ProviderPresetStore.RecordSuccess("cell:25003", "Split1", string.Empty, false,
                operatorName: "Билайн");
            preset = ProviderPresetStore.Find("cell:25003");
            Check(preset is not null && preset.Key == "cell:25003" && preset.Page == "Split1" &&
                  preset.Successes == 2 && preset.Operator == "Билайн",
                "запись приведена к ключу MCCMNC, имя оператора хранится отдельно");

            var shippedFile = ProviderPresetStore.LoadShipped();
            Check(shippedFile.Version >= 1 && shippedFile.Presets is not null,
                "зашитая база читается из сборки");

            // Обновляемая база из репозитория: тот же файл, но свежее зашитого.
            File.WriteAllText(ProviderPresetStore.RemotePath, """
                {
                  "version": 1,
                  "updatedUtc": "2026-11-25T00:00:00Z",
                  "presets": [
                    { "key": "cell:25002:Мегафон", "page": "Oob", "video": "", "blockQuic": false }
                  ]
                }
                """);
            ProviderPresetStore.PathOverride = path; // сбросить кэш и перечитать файлы
            preset = ProviderPresetStore.Find("cell:25002:Мегафон", out origin);
            Check(preset is not null && preset.Page == "Oob" && origin == PresetOrigin.Remote,
                "обновляемая база применяется к новой сети");

            ProviderPresetStore.RecordSuccess("cell:25002:Мегафон", "Split1", string.Empty, false);
            preset = ProviderPresetStore.Find("cell:25002:Мегафон", out origin);
            Check(preset is not null && preset.Page == "Split1" && origin == PresetOrigin.Device,
                "своё устройство главнее обновляемой базы");

            Check(ProviderPresetStore.TryParseRemote("""
                {
                  "version": 1,
                  "presets": [
                    { "key": "wifi:Lena11", "page": "Oob" },
                    { "key": "cell:25002:Мегафон", "page": "НетТакой" },
                    { "key": "cell:25003:Билайн", "page": "Oob" },
                    { "key": "cell:25003:Билайн", "page": "Split1" }
                  ]
                }
                """, out var remote, out _) && remote.Presets.Count == 1 &&
                  remote.Presets[0].Key == "cell:25003:Билайн",
                "удалённая база: только операторы и известные стратегии");
            Check(!ProviderPresetStore.TryParseRemote("{ \"version\": 1, \"presets\": [", out _, out _),
                "битый файл обновляемой базы отклоняется");

            // Отправка подборок: без согласия не копится, с согласием копится и ждёт приёмник.
            ProviderPresetShare.Enabled = false;
            ProviderPresetShare.Enqueue(new ProviderPreset { Key = "cell:25002:Мегафон", Page = "Oob" });
            Check(ProviderPresetShare.QueueCount == 0, "без согласия подборки в очередь не попадают");

            ProviderPresetShare.Enabled = true;
            Check(!ProviderPresetShare.IsConfigured, "адрес приёмника пока не задан");
            ProviderPresetShare.Enqueue(new ProviderPreset { Key = "cell:25002:Мегафон", Page = "Oob" });
            Check(ProviderPresetShare.QueueCount == 1, "с согласием запись копится и ждёт приёмник");

            File.WriteAllText(ProviderPresetStore.RemotePath, """
                { "version": 1, "uploadUrl": "https://example.invalid/presets", "presets": [] }
                """);
            ProviderPresetStore.PathOverride = path; // сбросить кэш базы
            Check(ProviderPresetShare.IsConfigured &&
                  ProviderPresetShare.Endpoint == "https://example.invalid/presets",
                "адрес приёмника берётся из обновляемой базы");

            Check(ProviderPresetStore.TryParseRemote("""
                { "version": 1, "uploadUrl": "ftp://bad", "presets": [] }
                """, out var noUrl, out _) && noUrl.UploadUrl.Length == 0,
                "адрес приёмника принимается только http(s)");
            ProviderPresetShare.Enabled = false;
        }
        finally
        {
            TryDelete(ProviderPresetStore.RemotePath);
            TryDelete(ProviderPresetStore.SyncPath);
            TryDelete(Path.ChangeExtension(path, ".outbox.json"));
            ProviderPresetStore.PathOverride = previous;
            TryDelete(path);
            TryDelete(path + ".bak");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    /// <summary>Датаграмма в формате SOCKS5 UDP (RSV, FRAG 0, IPv4-адрес назначения).</summary>
    private static byte[] BuildUdpDatagram(IPAddress address, int port, byte[] payload)
    {
        var length = 10 + payload.Length;
        var datagram = new byte[length];
        datagram[3] = 1;
        address.GetAddressBytes().CopyTo(datagram, 4);
        datagram[8] = (byte)(port >> 8);
        datagram[9] = (byte)port;
        payload.CopyTo(datagram, 10);
        return datagram;
    }

    /// <summary>
    /// Сквозная проверка UDP-релея: sing-box (socks inbound → socks outbound) отправляет QUIC
    /// в форвардер, форвардер релеит датаграмму и шлёт фейковый Initial. Без TUN и без админа.
    /// </summary>
    private static async Task RunForwarderUdpViaSingBoxAsync()
    {
        Console.WriteLine("Сквозная проверка UDP: sing-box → форвардер → эхо:");
        if (!SingBoxCore.IsInstalled)
        {
            Console.WriteLine("  пропуск: sing-box.exe не установлен");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var echoPort = ((IPEndPoint)echo.LocalEndPoint!).Port;
        var echoTask = Task.Run(async () =>
        {
            var buffer = new byte[65535];
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var result = await echo.ReceiveFromAsync(
                        buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                    if (result.ReceivedBytes == 1300)
                    {
                        await echo.SendToAsync("ok"u8.ToArray(), SocketFlags.None, result.RemoteEndPoint,
                            timeout.Token);
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                                  or ObjectDisposedException)
            {
            }
        }, timeout.Token);

        var forwarder = new TlsFragmentForwarder();
        var socksPort = FindFreePort(21960);
        var configPath = Path.Combine(AppPaths.RuntimeDirectory, "forwarder-udp-test.json");
        var runner = new SingBoxRunner();
        Socket? client = null;
        try
        {
            await forwarder.StartAsync(StreamFragmentMode.None);
            var config = new JsonObject
            {
                ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
                ["inbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "socks",
                        ["tag"] = "in",
                        ["listen"] = "127.0.0.1",
                        ["listen_port"] = socksPort,
                    },
                },
                ["outbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "socks",
                        ["tag"] = "fragment",
                        ["server"] = "127.0.0.1",
                        ["server_port"] = forwarder.Port,
                    },
                    new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                },
                ["route"] = new JsonObject { ["final"] = "fragment" },
            };
            SingBoxConfigBuilder.WriteToFile(configPath, config.ToJsonString());
            await SingBoxRunner.CheckConfigAsync(configPath, timeout.Token);
            await runner.StartAsync(configPath, socksPort, TimeSpan.FromSeconds(20), timeout.Token);

            // Клиент sing-box: SOCKS5 UDP ASSOCIATE, затем датаграмма-Initial на эхо-сервер.
            client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var control = new TcpClient();
            await control.ConnectAsync(IPAddress.Loopback, socksPort, timeout.Token);
            var stream = control.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, timeout.Token);
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, timeout.Token);
            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Check(reply[0] == 5 && reply[1] == 0, "sing-box: UDP ASSOCIATE принят");
            var relay = new IPEndPoint(new IPAddress(reply.AsSpan(4, 4)), (reply[8] << 8) | reply[9]);

            var initial = new byte[1300];
            initial[0] = 0xC3;
            initial[4] = 0x01;
            await client.SendToAsync(BuildUdpDatagram(IPAddress.Loopback, echoPort, initial), SocketFlags.None,
                relay, timeout.Token);

            var incoming = new byte[65535];
            var answer = await client.ReceiveFromAsync(
                incoming, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            Check(answer.ReceivedBytes >= 12, "sing-box → форвардер: ответ вернулся клиенту");
            var payloadOffset = incoming[3] == 1 ? 10 : 22;
            Check(incoming.AsSpan(payloadOffset, answer.ReceivedBytes - payloadOffset).SequenceEqual("ok"u8),
                "sing-box → форвардер: полезная нагрузка через всю цепочку без потерь");
            Check(forwarder.UdpAssociations >= 1, "sing-box → форвардер: ассоциация открыта");
            await WaitForAsync(() => forwarder.FakeQuicPackets >= QuicFake.Repeats, timeout.Token);
            Check(forwarder.FakeQuicPackets >= QuicFake.Repeats,
                $"sing-box → форвардер: отправлен QUIC-фейк ({forwarder.FakeQuicPackets} копий)");
        }
        catch (Exception exception)
        {
            Check(false, "sing-box → форвардер: " + exception.Message);
        }
        finally
        {
            client?.Dispose();
            await runner.StopAsync();
            await forwarder.StopAsync();
            echo.Dispose();
            try
            {
                await echoTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException)
            {
            }

            try
            {
                File.Delete(configPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Настоящее TLS-рукопожатие через SOCKS5-форвардер для каждой стратегии: сервер
    /// самопроверенный, но ClientHello и записи проходят через тот же код, что и на телефоне.
    /// Ловит стратегии, которые ломают поток (перестановка без raw-сокетов так работать не может).
    /// </summary>
    private static async Task RunForwarderTlsAsync()
    {
        Console.WriteLine("Стратегии форвардера: TLS-рукопожатие через каждую:");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=www.youtube.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("www.youtube.com");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);

        foreach (var strategy in TlsFragmentForwarder.Strategies)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                using var server = await listener.AcceptTcpClientAsync(timeout.Token);
                using var ssl = new SslStream(server.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                }, timeout.Token).ConfigureAwait(false);
                var buffer = new byte[4];
                await ssl.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
                await ssl.WriteAsync(buffer, timeout.Token).ConfigureAwait(false);
            }, timeout.Token);

            var forwarder = new TlsFragmentForwarder();
            try
            {
                await forwarder.StartAsync(strategy.Mode);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, forwarder.Port, timeout.Token);
                client.NoDelay = true;
                var stream = client.GetStream();

                await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
                var greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting, timeout.Token);

                var requestBytes = new byte[10]
                {
                    5, 1, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port,
                };
                await stream.WriteAsync(requestBytes, timeout.Token);
                var reply = new byte[10];
                await stream.ReadExactlyAsync(reply, timeout.Token);
                if (reply[0] != 5 || reply[1] != 0)
                {
                    Check(false, $"{strategy.Mode}: SOCKS5 отказал");
                    continue;
                }

                using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "www.youtube.com",
                }, timeout.Token).ConfigureAwait(false);
                await ssl.WriteAsync(new byte[] { 1, 2, 3, 4 }, timeout.Token).ConfigureAwait(false);
                var response = new byte[4];
                await ssl.ReadExactlyAsync(response, timeout.Token).ConfigureAwait(false);
                Check(response[0] == 1 && response[3] == 4,
                    $"{strategy.Mode}: TLS-рукопожатие и обмен данными");
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Check(false, $"{strategy.Mode}: {Describe(exception)}");
            }
            finally
            {
                await forwarder.StopAsync();
                listener.Stop();
                try
                {
                    await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception)
                {
                    // Рукопожатие не состоялось — это уже отмечено выше.
                }
            }
        }
    }

    private static void RunDesyncStartTest()    {
        Console.WriteLine("Запуск собственного обхода DPI (WinDivert):");
        using var service = new DesyncService();
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        var admin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        try
        {
            service.Start(DesyncService.Strategies[0], null);
            Check(true, "движок запустился");
            service.Stop();
        }
        catch (InvalidOperationException exception) when (!admin)
        {
            Check(exception.Message.Contains("администратор", StringComparison.OrdinalIgnoreCase),
                "без прав администратора — понятная ошибка");
        }
        catch (Exception exception)
        {
            Check(false, "запуск движка — " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static async Task RunDesyncInstallAsync()    {
        Console.WriteLine("Установка WinDivert для своего обхода DPI:");
        var progress = new Progress<string>(line => Console.WriteLine("  … " + line));
        try
        {
            await DesyncService.EnsureAsync(progress, CancellationToken.None);
            Check(DesyncService.IsInstalled, "WinDivert.dll и WinDivert64.sys на месте");
            Check(File.Exists(DesyncService.QuicTemplatePath) &&
                  new FileInfo(DesyncService.QuicTemplatePath).Length > 200,
                "шаблон QUIC-фейка на месте");
            Console.WriteLine("  папка: " + DesyncService.Directory);
        }
        catch (Exception exception)
        {
            Check(false, "установка WinDivert — " + exception.Message);
        }
    }

    private static byte[] BuildQuicPacket(int payloadLength)    {
        var packet = new byte[20 + 8 + payloadLength];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)packet.Length);
        packet[8] = 64;
        packet[9] = 17;
        packet[12] = 10;
        packet[15] = 1;
        packet[16] = 142;
        packet[17] = 250;
        packet[19] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 50000);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 443);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), (ushort)(8 + payloadLength));
        packet[28] = 0xC0;
        return packet;
    }

    private static void RunParserTests()
    {
        Console.WriteLine("Разбор ссылок:");
        var settings = new AppSettings();
        Check(settings.MixedPort > 0, "настройки по умолчанию");

        var vless = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:443?encryption=none&security=tls&sni=cdn.example.com&type=ws&path=%2Fws&host=cdn.example.com#%D0%A2%D0%B5%D1%81%D1%82",
            "test");
        Check(vless is { Protocol: "vless", Port: 443, Server: "example.com" }, "vless: сервер и порт");
        Check(vless?.Name == "Тест", "vless: имя из фрагмента");
        Check(LinkParser.NormalizeName("GB \\U0001F1EC\\U0001F1E7 Test").Contains("🇬🇧", StringComparison.Ordinal),
            "vless: \\U-флаг в имени декодируется");
        Check(NodeDisplay.CleanName("GB 🇬🇧 | @NamiraConfigs | …") == "GB 🇬🇧",
            "имя: Telegram-канал убран из отображения");
        Check(NodeDisplay.Name(new ProxyNode { Name = "@NamiraConfigs", Server = "1.2.3.4", Port = 443 })
              == "1.2.3.4:443",
            "имя: если остался только канал — показываем IP:порт");
        Check(vless?.OutboundJson.Contains("\"ws\"", StringComparison.Ordinal) == true, "vless: websocket");

        var vlessV6 = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@[2001:db8::1]:443?security=tls#V6", "test");
        Check(vlessV6?.Server == "2001:db8::1", "vless: IPv6-адрес без квадратных скобок");
        Check(vlessV6?.OutboundJson.Contains("\"server_name\":\"2001:db8::1\"", StringComparison.Ordinal) == false,
            "vless: IP-адрес не подставляется в SNI");

        var vlessIp = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=tls#IP", "test");
        Check(vlessIp?.OutboundJson.Contains("\"server_name\":\"1.2.3.4\"", StringComparison.Ordinal) == false,
            "vless: IPv4 не подставляется в SNI");

        var vlessDomain = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls#D", "test");
        Check(vlessDomain?.OutboundJson.Contains("\"server_name\":\"example.com\"", StringComparison.Ordinal) == true,
            "vless: домен подставляется в SNI");

        var reality = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=aa&fp=chrome&sni=www.microsoft.com&type=tcp#R",
            "test");
        Check(reality?.OutboundJson.Contains("\"reality\"", StringComparison.Ordinal) == true, "vless: reality");
        Check(LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=reality&type=tcp#R", "test") is null,
            "vless: reality без pbk отбрасывается");

        var trojan = LinkParser.Parse("trojan://secret@example.org:8443?sni=example.org&type=ws&path=%2Ft#T", "test");
        Check(trojan is { Protocol: "trojan", Port: 8443 }, "trojan: разбор");

        var ssBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:password"));
        var ss = LinkParser.Parse($"ss://{ssBase64}@example.net:8388#SS", "test");
        Check(ss is { Protocol: "shadowsocks", Port: 8388 }, "ss: SIP002");
        var ssLegacy = LinkParser.Parse("ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:password@example.net:8388")) + "#S", "test");
        Check(ssLegacy is { Protocol: "shadowsocks", Port: 8388 }, "ss: legacy base64");

        var vmessJson = "{\"v\":\"2\",\"ps\":\"VM\",\"add\":\"example.com\",\"port\":\"443\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"aid\":\"0\",\"scy\":\"auto\",\"net\":\"ws\",\"type\":\"none\",\"host\":\"a.example.com\",\"path\":\"/x\",\"tls\":\"tls\",\"sni\":\"a.example.com\"}";
        var vmess = LinkParser.Parse("vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vmessJson)), "test");
        Check(vmess is { Protocol: "vmess", Port: 443 }, "vmess: разбор");
        Check(vmess?.OutboundJson.Contains("\"ws\"", StringComparison.Ordinal) == true, "vmess: websocket");

        var hy2 = LinkParser.Parse("hysteria2://pass@example.com:443?sni=example.com&insecure=1#H", "test");
        Check(hy2 is { Protocol: "hysteria2" }, "hysteria2: разбор");

        // Оценка маскировки: REALITY+Vision выше обычного TLS и тем более узла без TLS.
        var realityNode = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=ab12&fp=chrome&flow=xtls-rprx-vision&sni=www.youtube.com#R",
            "test");
        var tlsNode = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=ws&path=%2Fws&fp=chrome&sni=example.com#W",
            "test");
        var plainNode = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:80?security=none&type=tcp#P",
            "test");
        Check(realityNode is not null && tlsNode is not null &&
              NodeRanking.IsMasked(realityNode) && NodeRanking.Score(realityNode) > NodeRanking.Score(tlsNode),
            "маскировка: REALITY оценивается выше TLS");
        Check(realityNode is not null && NodeRanking.Label(realityNode).Contains("REALITY", StringComparison.Ordinal),
            "маскировка: пометка REALITY");
        Check(tlsNode is not null && NodeRanking.IsMasked(tlsNode) && NodeRanking.Label(tlsNode).Contains("WS", StringComparison.Ordinal),
            "маскировка: WS·TLS считается маскированным");
        Check(plainNode is not null && !NodeRanking.IsMasked(plainNode) && NodeRanking.Score(plainNode) < 0,
            "маскировка: узел без TLS отсеивается фильтром");
        Check(hy2 is not null && NodeRanking.IsMasked(hy2), "маскировка: Hysteria2 считается маскированным");

        var yandexNode = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=ab12&fp=chrome&sni=www.yandex.ru#Y",
            "test");
        Check(realityNode is not null && NodeRanking.Family(realityNode) == "google" &&
              NodeRanking.CamouflageLabel(realityNode) == "YouTube/Google",
            "камуфляж: YouTube/Google");
        Check(yandexNode is not null && NodeRanking.Family(yandexNode) == "yandex" &&
              NodeRanking.Family(yandexNode) != NodeRanking.Family(realityNode!),
            "камуфляж: Yandex и YouTube — разные семейства для переключения");

        Check(LinkParser.Parse("not a link", "test") is null, "мусор отбрасывается");
        Check(LinkParser.Parse("vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                "{\"add\":\"https://github.com/x\",\"port\":\"443\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"ps\":\"junk\"}")), "test") is null,
            "узел с адресом-ссылкой отбрасывается");

        var extracted = NodeSourceService.ExtractLinks("текст vless://a@b:1#x, и vmess://abc==; лишнее");
        Check(extracted.Count == 2, "извлечение ссылок из текста: " + extracted.Count);

        var config = SingBoxConfigBuilder.Build(new[] { vless! }, vless!.Tag, 20800, 20801, "secret", new AppSettings());
        Check(config.Contains("\"mixed\"", StringComparison.Ordinal), "конфиг: mixed inbound");
        Check(config.Contains("\"clash_api\"", StringComparison.Ordinal), "конфиг: clash api");

        Console.WriteLine("Маршрутизация:");

        var onlySites = new AppSettings { SiteRouteMode = SiteRouteMode.OnlyListed, SiteList = "youtube.com\n*.google.com" };
        var (siteRules, siteFinal) = SingBoxConfigBuilder.BuildRouteRules(onlySites, tunEnabled: false);
        Check(siteFinal == SingBoxConfigBuilder.DirectTag, "«только сайты»: остальное напрямую");
        var siteSuffixes = ((JsonObject)siteRules[1]!)["domain_suffix"]!.AsArray();
        Check(siteSuffixes.Count == 4, "домены добавлены в двух формах (сам домен и поддомены)");
        Check(siteSuffixes.Any(v => v!.GetValue<string>() == "youtube.com") &&
              siteSuffixes.Any(v => v!.GetValue<string>() == ".youtube.com"), "нормализация домена");

        var appsOnly = new AppSettings
        {
            OnlySelectedApps = true,
            AppList = "chrome.exe\nC:\\Program Files\\Telegram\\Telegram.exe",
        };
        var (appRules, appFinal) = SingBoxConfigBuilder.BuildRouteRules(appsOnly, tunEnabled: false);
        Check(appFinal == SingBoxConfigBuilder.DirectTag, "«только приложения»: остальное напрямую");
        var processNames = ((JsonObject)appRules[1]!)["process_name"]!.AsArray();
        Check(processNames.Count == 2 && processNames[1]!.GetValue<string>() == "Telegram.exe", "путь процесса сокращён до имени");

        var (tunRules, _) = SingBoxConfigBuilder.BuildRouteRules(new AppSettings { TunnelMode = TunnelMode.Tun }, tunEnabled: true);
        Check(tunRules[0]!["action"]!.GetValue<string>() == "sniff", "TUN: первое правило — определение домена");
        Check(tunRules[1]!["action"]!.GetValue<string>() == "hijack-dns", "TUN: перехват DNS");

        var tunConfig = SingBoxConfigBuilder.Build(
            new[] { vless! }, vless!.Tag, 20800, 20801, "s",
            new AppSettings { TunnelMode = TunnelMode.Tun, SiteRouteMode = SiteRouteMode.AllExceptListed, SiteList = "example.com" });
        Check(tunConfig.Contains("\"type\": \"tun\"", StringComparison.Ordinal), "TUN: inbound в конфиге");
        Check(tunConfig.Contains("\"auto_detect_interface\": true", StringComparison.Ordinal), "TUN: auto_detect_interface");
        Check(tunConfig.Contains("\"dns\"", StringComparison.Ordinal), "TUN: DNS настроен");

        var duplicateA = LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=tcp#A", "s1");
        var duplicateB = LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=tcp#B", "s2");

        // Точные повторы (одинаковый outbound) — одна строка, даже из разных источников.
        if (duplicateA is not null && duplicateB is not null)
        {
            Check(duplicateA.Id == duplicateB.Id, "одинаковые узлы склеиваются");
            duplicateA.Status = NodeStatus.Working;
            duplicateA.DelayMs = 120;
            duplicateA.CheckedAtUtc = DateTime.UtcNow;
            var exact = NodeDedup.Collapse(new[] { duplicateA, duplicateB });
            Check(exact.Count == 1, "точные повторы склеиваются в одну строку");
            Check(exact[0].Status == NodeStatus.Working && exact[0].DelayMs == 120,
                "проверка проверенного дубля сохраняется");
            Check(exact[0].SourceName.Contains("s1", StringComparison.Ordinal) &&
                  exact[0].SourceName.Contains("s2", StringComparison.Ordinal),
                "источники склеенного узла объединены");
        }

        // Разные аккаунты на одном сервере:порту (общий CDN) — это разные узлы:
        // жёсткое «одна строка на адрес» выбрасывало рабочие варианты.
        var endpointA = LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=ws&path=%2Fa#A", "s1");
        var endpointB = LinkParser.Parse(
            "vless://22222222-3333-4444-5555-666666666666@EXAMPLE.com:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=aa&fp=chrome&sni=www.microsoft.com#B",
            "s2");
        if (endpointA is not null && endpointB is not null)
        {
            endpointA.Status = NodeStatus.Working;
            endpointA.DelayMs = 120;
            endpointA.CheckedAtUtc = DateTime.UtcNow;
            var collapsed = NodeDedup.Collapse(new[] { endpointA, endpointB });
            Check(collapsed.Count == 2, "разные аккаунты на одном сервере:порту сохраняются");
            Check(collapsed[0].Status == NodeStatus.Working && collapsed[0].DelayMs == 120,
                "рабочий вариант остаётся первым");
        }

        // Мягкий лимит: десятки аккаунтов одного CDN-адреса не должны раздувать список.
        var variants = Enumerable.Range(1, NodeLimits.MaxPerEndpoint + 3)
            .Select(index => LinkParser.Parse(
                $"vless://0000000{index}-2222-3333-4444-555555555555@cdn.example:443?security=tls&type=ws&path=%2F{index}#V{index}",
                "s"))
            .OfType<ProxyNode>()
            .ToList();
        Check(variants.Count == NodeLimits.MaxPerEndpoint + 3 &&
              NodeDedup.Collapse(variants).Count == NodeLimits.MaxPerEndpoint,
            "на сервер:порт остаётся не больше " + NodeLimits.MaxPerEndpoint + " вариантов");

        // Лимит не должен приписывать чужие проверки: успех лишнего аккаунта
        // на том же CDN-адресе не переходит на оставшиеся варианты.
        var limitVariants = Enumerable.Range(1, NodeLimits.MaxPerEndpoint + 1)
            .Select(index => LinkParser.Parse(
                $"vless://0000000{index}-2222-3333-4444-555555555555@limit.example:443?security=tls&type=ws&path=%2F{index}#L{index}",
                "s"))
            .OfType<ProxyNode>()
            .ToList();
        var extraWorking = limitVariants[^1];
        extraWorking.Status = NodeStatus.Working;
        extraWorking.DelayMs = 100;
        extraWorking.CheckedAtUtc = DateTime.UtcNow;
        extraWorking.YoutubeOk = true;
        var limited = NodeDedup.Collapse(limitVariants);
        Check(limited.Count == NodeLimits.MaxPerEndpoint &&
              limited.All(node => node.Id == extraWorking.Id || node.YoutubeOk != true),
            "проверка лишнего аккаунта не приписывается чужим узлам");

        // Узел без проверки сертификата помечается и оценивается ниже обычного TLS.
        var insecureNode = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&allowInsecure=1#I",
            "test");
        Check(insecureNode?.AllowInsecure == true, "insecure-узел помечен");
        Check(insecureNode is not null && tlsNode is not null &&
              NodeRanking.Score(insecureNode) < NodeRanking.Score(tlsNode),
            "insecure-узел оценивается ниже узла с проверкой сертификата");

        // MTProto с сайтов: ссылки t.me/proxy, экранированный JSON и структурированный JSON.
        var siteText = "1. https://t.me/proxy?server=example.org&port=443&secret=ee00112233445566778899aabbccddeeff676f6f676c652e636f6d\n";
        var siteProxies = MtProtoService.ExtractAll(siteText, "site");
        Check(siteProxies.Count == 1 && siteProxies[0].Server == "example.org" && siteProxies[0].Port == 443,
            "MTProto: ссылка t.me/proxy с сайта разобрана");

        const string escapedJson = "[{\"scheme\":\"https\",\"host\":\"t.me\",\"path\":\"\\/proxy\",\"query\":{\"server\":\"json.example\",\"port\":\"8443\",\"secret\":\"ee00112233445566778899aabbccddeeff676f6f676c652e636f6d\"},\"link\":\"https:\\/\\/t.me\\/proxy?server=json.example&port=8443&secret=ee00112233445566778899aabbccddeeff676f6f676c652e636f6d\"}]";
        var jsonProxies = MtProtoService.ExtractAll(escapedJson, "json");
        Check(jsonProxies.Count == 1 && jsonProxies[0].Server == "json.example",
            "MTProto: экранированный JSON с сайта разобран");

        const string structuredJson = "[{\"server\":\"structured.example\",\"port\":8443,\"secret\":\"ee00112233445566778899aabbccddeeff676f6f676c652e636f6d\"}]";
        var structuredProxies = MtProtoService.ExtractAll(structuredJson, "json2");
        Check(structuredProxies.Count == 1 && structuredProxies[0].Port == 8443,
            "MTProto: структурированный JSON без ссылки разобран");

        Check(SourceKinds.Classify("https://t.me/s/proxy_mtproto") == SourceKind.Telegram &&
              SourceKinds.Classify("https://raw.githubusercontent.com/Airuop/MTProtoCollector/main/proxy/mtproto.json") == SourceKind.MtProto,
            "типы источников: канал и сайт MTProto различаются");

        Check(SourceKinds.IsServerSource(new SourceDefinition { Url = "https://t.me/s/v2rayng_config", Kind = SourceKind.Telegram }) &&
              !SourceKinds.IsServerSource(new SourceDefinition { Url = "https://raw.githubusercontent.com/Airuop/MTProtoCollector/main/proxy/mtproto.json", Kind = SourceKind.MtProto }) &&
              SourceKinds.IsServerSource(new SourceDefinition { Url = "https://raw.githubusercontent.com/x/y/main/sub", Kind = SourceKind.PlainList }),
            "каналы Telegram дают серверы, сайты MTProto — только прокси");

        // Мусорные и пограничные ссылки не должны проходить разбор.
        Check(LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@example.com:70000?security=tls#P", "t") is null,
            "порт вне диапазона отбрасывается");
        Check(LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@" + new string('a', 300) + ":443?security=tls#P", "t") is null,
            "слишком длинный хост отбрасывается");
        Check(LinkParser.Parse("vmess://%%%", "t") is null, "битый base64 отбрасывается");
        Check(LinkParser.Parse(
                "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=reality&pbk=zz&type=tcp#R", "t") is null,
            "reality с некорректным ключом отбрасывается");

        var tuicNode = LinkParser.Parse(
            "tuic://11111111-2222-3333-4444-555555555555:secret-pass@example.com:443?alpn=h3&sni=example.com&congestion_control=bbr#T", "t");
        Check(tuicNode is not null && tuicNode.Protocol == "tuic" &&
              tuicNode.OutboundJson.Contains("\"type\":\"tuic\"", StringComparison.Ordinal) &&
              tuicNode.OutboundJson.Contains("\"congestion_control\":\"bbr\"", StringComparison.Ordinal),
            "tuic: ссылка разобрана в outbound TUIC");
        Check(LinkParser.Parse("tuic://@example.com:443", "t") is null,
            "tuic: без uuid и пароля отбрасывается");

        // Telegram: в канале лежит история — старые посты в список не попадают.
        var telegramFeedHtml =
            "<div class=\"tgme_widget_message_wrap\"><time datetime=\"2020-01-01T10:00:00+00:00\"></time> " +
            "vless://11111111-2222-3333-4444-555555555555@old.example:443?security=tls#old</div>" +
            "<div class=\"tgme_widget_message_wrap\"><time datetime=\"" +
            DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\"></time> " +
            "vless://11111111-2222-3333-4444-555555555555@new.example:443?security=tls#new</div>";
        var freshTelegram = TelegramText.Extract(telegramFeedHtml, DateTime.UtcNow.AddHours(-TelegramText.FreshHours));
        Check(freshTelegram.Contains("new.example", StringComparison.Ordinal) &&
              !freshTelegram.Contains("old.example", StringComparison.Ordinal),
            "Telegram: старые посты отбрасываются");

        Console.WriteLine("Подписки:");

        var base64Subscription = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "vless://11111111-2222-3333-4444-555555555555@a.example:443?security=tls#A\n" +
            "trojan://pass@b.example:443?sni=b.example#B\n"));
        Check(SubscriptionParser.Parse(base64Subscription, "sub").Count == 2, "base64-список ссылок");

        var plainSubscription = "vless://11111111-2222-3333-4444-555555555555@a.example:443?security=tls#A\nvmess://" +
                                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vmessJson));
        Check(SubscriptionParser.Parse(plainSubscription, "sub").Count == 2, "обычный список ссылок");

        var xrayJson = """
        {"outbounds":[
          {"tag":"proxy","protocol":"vless",
           "settings":{"vnext":[{"address":"1.2.3.4","port":443,"users":[{"id":"11111111-2222-3333-4444-555555555555","flow":"xtls-rprx-vision"}]}]},
           "streamSettings":{"network":"tcp","security":"reality","realitySettings":{"serverName":"a.example","publicKey":"jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0","shortId":"ab","fingerprint":"chrome"}}}
        ]}
        """;
        var xrayNodes = SubscriptionParser.Parse(xrayJson, "xray");
        Check(xrayNodes.Count == 1 && xrayNodes[0].Protocol == "vless", "Xray JSON: узел разобран");
        Check(xrayNodes.Count == 1 && xrayNodes[0].OutboundJson.Contains("\"reality\"", StringComparison.Ordinal), "Xray JSON: reality");

        var singBoxJson = """
        {"outbounds":[{"type":"vless","tag":"n1","server":"1.2.3.4","server_port":443,"uuid":"11111111-2222-3333-4444-555555555555","tls":{"enabled":true}}]}
        """;
        Check(SubscriptionParser.Parse(singBoxJson, "sing-box").Count == 1, "sing-box JSON: узел разобран");
        var singBoxNode = SubscriptionParser.Parse(singBoxJson, "sing-box")[0];
        var singBoxReloaded = SubscriptionParser.Parse(singBoxNode.RawLink, "sing-box")[0];
        Check(singBoxNode.Id == singBoxReloaded.Id, "sing-box JSON: Id стабилен при перезагрузке из кэша");
        Check(LinkParser.Parse(singBoxNode.RawLink, "sing-box") is null, "sing-box JSON не читается LinkParser (нужен fallback)");

        var clashYaml = "proxies:\n" +
                        "  - name: \"NL 1\"\n" +
                        "    type: vless\n" +
                        "    server: 5.6.7.8\n" +
                        "    port: 443\n" +
                        "    uuid: 11111111-2222-3333-4444-555555555555\n" +
                        "    tls: true\n" +
                        "    servername: a.example\n" +
                        "    network: ws\n" +
                        "    ws-opts:\n" +
                        "      path: /ws\n" +
                        "      headers:\n" +
                        "        Host: a.example\n" +
                        "  - name: SS\n" +
                        "    type: ss\n" +
                        "    server: 9.9.9.9\n" +
                        "    port: 8388\n" +
                        "    cipher: aes-256-gcm\n" +
                        "    password: pass\n";
        var clashNodes = SubscriptionParser.Parse(clashYaml, "clash");
        Check(clashNodes.Count == 2, "Clash YAML: два узла");
        Check(clashNodes.Count == 2 && clashNodes[0].OutboundJson.Contains("\"ws\"", StringComparison.Ordinal), "Clash YAML: websocket и Host");
        Check(clashNodes.Count == 2 && clashNodes[1].Protocol == "shadowsocks", "Clash YAML: shadowsocks");

        Check(SubscriptionParser.ParseUserInfo(new[] { "upload=1000000; download=2000000; total=100000000000; expire=1800000000" }) is not null,
            "заголовок subscription-userinfo");

        Console.WriteLine("Секреты:");

        // DPAPI: sources.dat должен читаться только под текущей учётной записью Windows.
        var protectedText = SecretStore.Protect("vless://token@example.com:443");
        Check(SecretStore.Unprotect(protectedText) == "vless://token@example.com:443",
            "DPAPI: секрет шифруется и расшифровывается");
        Check(SecretStore.Unprotect(new byte[] { 1, 2, 3, 4, 5 }) is null,
            "DPAPI: чужой файл не расшифровывается и не роняет приложение");

        Console.WriteLine("Версии данных:");

        var upgraded = new AppSettings();
        Check(SettingsUpgrade.Apply(upgraded) && upgraded.SchemaVersion == SchemaVersions.Settings,
            "настройки: старая схема мигрирует к текущей");
        Check(!SettingsUpgrade.Apply(upgraded), "настройки: повторная миграция не требуется");

        var capped = new AppSettings { SchemaVersion = 3, MaxTotalNodes = 12000 };
        SettingsUpgrade.Apply(capped);
        Check(capped.MaxTotalNodes == NodeLimits.DefaultMaxTotal && capped.SchemaVersion == SchemaVersions.Settings,
            "настройки: список ограничен 5000");

        Console.WriteLine("MTProto:");

        var mtprotoText = "Прокси для Telegram: tg://proxy?server=1.2.3.4&port=443&secret=aabbccddeeff00112233445566778899\n" +
                          "и https://t.me/proxy?server=5.6.7.8&port=80&secret=deadbeefdeadbeefdeadbeefdeadbeef&amp;test=1";
        var mtproto = MtProtoService.Extract(mtprotoText, "тест");
        Check(mtproto.Count == 2, "MTProto: извлечение ссылок tg:// и t.me/proxy");
        Check(mtproto.Count == 2 && mtproto[0].Link == "tg://proxy?server=1.2.3.4&port=443&secret=aabbccddeeff00112233445566778899", "MTProto: нормализация ссылки");
        Check(MtProtoService.Extract("tg://proxy?server=1.2.3.4&port=0&secret=x", "тест").Count == 0, "MTProto: некорректный порт отбрасывается");

        var validFakeTls = MtProtoSecret.TryParse("ee00112233445566778899aabbccddeeff676f6f676c652e636f6d");
        Check(validFakeTls is { Kind: MtProtoSecretKind.FakeTls, Host: "google.com" },
            "MTProto: секрет FakeTLS разбирается (hex)");
        Check(MtProtoSecret.TryParse("7gARIjNEVWZ3iJmqu8zd7v9nb29nbGUuY29t") is not null,
            "MTProto: секрет FakeTLS разбирается (base64url)");
        Check(MtProtoSecret.TryParse("7pJZSUjIp-43RmluNUkNmWNkbnMuZ11vZ2xlLmNvbQ==") is null,
            "MTProto: секрет с испорченным доменом отбрасывается");
        Check(MtProtoService.Extract(
                "tg://proxy?server=1.2.3.4&port=443&secret=7pJZSUjIp-43RmluNUkNmWNkbnMuZ11vZ2xlLmNvbQ==",
                "тест").Count == 0,
            "MTProto: прокси с испорченным доменом не попадает в список");

        var telegramHtml = """
        <div class="tgme_widget_message_text js-message_text">
          <a href="tg://proxy?server=1.2.3.3&port=443&secret=aaaaaaaabbbbbbbbccccccccdddddddd">Прокси 1</a><br/>
          <code>tg://proxy?server=5.6.7.8&amp;port=80&amp;secret=11111111222222223333333344444444</code>
          <a href="https://t.me/proxy?server=9.9.9.9&amp;port=8443&amp;secret=55555555666666667777777788888888">t.me/proxy</a>
        </div>
        """;
        var fromHtml = MtProtoService.Extract(TelegramText.Extract(telegramHtml), "канал");
        Check(fromHtml.Count == 3, "MTProto: разбор HTML канала, найдено " + fromHtml.Count);

        var blockText = "IP: <code>95.217.135.241</code> Port: <code>443</code> " +
                        "Secret: <code>ee00112233445566778899aabbccddeeff676f6f676c652e636f6d</code>";
        var blockProxy = MtProtoService.Extract(blockText, "RSS");
        Check(blockProxy.Count == 1 && blockProxy[0].Server == "95.217.135.241" && blockProxy[0].Port == 443,
            "MTProto: формат IP/Port/Secret (RSS-зеркала)");
        Check(MtProtoService.ChannelName("https://t.me/s/MTProtoProxies") == "MTProtoProxies" &&
              MtProtoService.ChannelName("https://t.me/proxy_mtproto") == "proxy_mtproto",
            "MTProto: имя канала из ссылки");

        var multiLinkSource = new SourceDefinition
        {
            Name = "Мои узлы",
            Kind = SourceKind.SingleLink,
            Url = "vless://11111111-2222-3333-4444-555555555555@a.example:443?security=tls#A\n" +
                  "trojan://pass@b.example:443?sni=b.example#B",
        };
        var multi = NodeSourceService.FetchAsync(multiLinkSource, CancellationToken.None).GetAwaiter().GetResult();
        Check(multi.Nodes.Count == 2, "несколько ссылок на серверы в одном источнике");

        var discovered = SourceDiscoveryService.SelectCandidatePaths(new[]
        {
            ("All_Configs_Sub.txt", 100_000L),
            ("README.md", 5_000L),
            ("sub/sub_merge.txt", 200_000L),
            (".github/workflows/build.yml", 1_000L),
            ("images/logo.png", 3_000L),
            ("tiny.txt", 10L),
        });
        Check(discovered.Count == 2 && discovered.Contains("All_Configs_Sub.txt") && discovered.Contains("sub/sub_merge.txt"),
            "автопоиск: отбор файлов " + string.Join(", ", discovered));

        var discoveredNoSize = SourceDiscoveryService.SelectCandidatePaths(new[]
        {
            "All_Configs_Sub.txt", "README.md", "sub/vless.txt", "images/logo.png",
        });
        Check(discoveredNoSize.Contains("All_Configs_Sub.txt") && discoveredNoSize.Contains("sub/vless.txt") &&
              !discoveredNoSize.Contains("README.md") && !discoveredNoSize.Contains("images/logo.png"),
            "автопоиск: отбор файлов без размера (GitLab) " + string.Join(", ", discoveredNoSize));

        var siteIndex = """
        <a href="/free-node/2026-11-19-vpn-free.htm" title="11月19日">11月19日免费节点 Clash/V2ray</a>
        <a href="/free-node/">免费节点</a>
        <a href="/tag/clash/">Clash</a>
        <a href="/go/Gougou">推荐机场</a>
        <a href="https://example.com/free-node/x.htm">внешняя</a>
        """;
        var articles = SourceDiscoveryService.SelectArticleLinks(siteIndex, new Uri("https://www.freeclashnode.com/free-node/"));
        Check(articles.Count == 1 && articles[0] == "https://www.freeclashnode.com/free-node/2026-11-19-vpn-free.htm",
            "автопоиск сайтов: отбор статей " + string.Join(", ", articles));

        var nodeFreeIndex = """
        <a href="https://nodefree.org/p/2683.html" title="статья">「12月31日」V2ray/Clash订阅链接免费节点</a>
        """;
        var nodeFreeArticles = SourceDiscoveryService.SelectArticleLinks(nodeFreeIndex, new Uri("https://nodefree.org/f/freenode"));
        Check(nodeFreeArticles.Count == 1 && nodeFreeArticles[0] == "https://nodefree.org/p/2683.html",
            "автопоиск сайтов: статья по короткому адресу");

        var articleHtml = """
        <p>https://node.freeclashnode.com/uploads/2026/11/0-20261119.txt</p>
        <p>https://node.freeclashnode.com/uploads/2026/11/1-20261119.yaml</p>
        <a href="https://example.com/sub.json">json</a>
        """;
        var subscriptions = SourceDiscoveryService.ExtractSubscriptionUrls(articleHtml);
        Check(subscriptions.Count == 3, "автопоиск сайтов: ссылки подписок " + string.Join(", ", subscriptions));

        var webDate = SourceDiscoveryService.ExtractPublishedDate("https://node.freeclashnode.com/uploads/2026/11/0-20261119.txt");
        Check(webDate is { } d && d.Year == 2026 && d.Month == 11 && d.Day == 19,
            "автопоиск сайтов: дата из компактного адреса (20261119)");
        var webDatePath = SourceDiscoveryService.ExtractPublishedDate("https://www.freeclashnode.com/free-node/2026-11-19-vpn-free.htm");
        Check(webDatePath is { } dp && dp.Day == 19 && dp.Month == 11,
            "автопоиск сайтов: дата из пути (2026-11-19)");
        Check(SourceDiscoveryService.ExtractPublishedDate("https://nodefree.org/p/2683.html") is null,
            "автопоиск сайтов: без даты в адресе — null");

        Console.WriteLine("zapret:");
        var zapretHostlist = Path.Combine(Path.GetTempPath(), "vienlink-zapret-test-hosts.txt");
        File.WriteAllLines(zapretHostlist, TlsHello.TargetDomains);
        var zapretArgs = ZapretService.BuildArguments(0, gameMode: false, zapretHostlist);
        var hostlistCount = zapretArgs.Count(arg => arg.StartsWith("--hostlist=", StringComparison.Ordinal));
        Check(hostlistCount >= 2, $"zapret: hostlist во всех TCP-правилах ({hostlistCount})");
        Check(zapretArgs.Any(arg => arg.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)) == false,
            "zapret: домены не подставлены в аргументы, только путь к hostlist");
    }

    /// <summary>
    /// Детерминированная проверка системного прокси: ядро с прямым выходом, включение прокси,
    /// запрос к сайту через систему, восстановление прежних настроек реестра.
    /// </summary>
    private static async Task RunProxyTestAsync()
    {
        Console.WriteLine("Проверка системного прокси (без внешнего узла):");
        var before = DescribeSystemProxy();
        var port = FindFreePort(21500);
        var configPath = Path.Combine(AppPaths.RuntimeDirectory, "proxytest-config.json");
        var config = """
        {
          "log": { "level": "warn" },
          "inbounds": [ { "type": "mixed", "tag": "in", "listen": "127.0.0.1", "listen_port": PORT } ],
          "outbounds": [ { "type": "direct", "tag": "direct" } ],
          "route": { "final": "direct" }
        }
        """.Replace("PORT", port.ToString(), StringComparison.Ordinal);

        SingBoxConfigBuilder.WriteToFile(configPath, config);
        await SingBoxRunner.CheckConfigAsync(configPath, CancellationToken.None);

        await using var runner = new SingBoxRunner();
        await runner.StartAsync(configPath, port, TimeSpan.FromSeconds(15), CancellationToken.None);

        var backup = SystemProxy.Capture();
        try
        {
            SystemProxy.Apply(port, backup, "*.deepseek.com;localhost;127.0.0.1");
            var applied = DescribeSystemProxy();
            Console.WriteLine("  применено: " + applied);
            Check(applied.Contains($"127.0.0.1:{port}", StringComparison.Ordinal), "реестр: прокси указывает на локальный порт");

            var code = await CheckSiteAsync(null, "https://www.google.com/generate_204");
            Console.WriteLine($"  google через системный прокси: {(code is null ? "не открылся" : "HTTP " + code)}");
            Check(code is not null, "трафик идёт через прокси");
        }
        finally
        {
            SystemProxy.Restore(backup);
            var after = DescribeSystemProxy();
            Console.WriteLine("  после восстановления: " + after);
            Check(after.Contains("ProxyEnable=0", StringComparison.Ordinal) || after == before, "настройки прокси восстановлены");
        }

        await runner.StopAsync();
    }

    private static int FindFreePort(int start)
    {
        for (var port = start; port < start + 50; port++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        throw new InvalidOperationException("Нет свободного порта");
    }

    /// <summary>Наполняет кэш приложения реальными узлами из источников (без проверки задержек).</summary>
    private static async Task SeedCacheAsync()
    {
        Console.WriteLine("Сбор узлов для кэша приложения…");
        var sources = NodeSourceService.LoadSources();
        var collected = await NodeSourceService.CollectAsync(sources, new Progress<string>(line => Console.WriteLine(line)), CancellationToken.None);
        NodeSourceService.SaveSources(sources);
        NodeSourceService.SaveCache(collected.Nodes);
        if (collected.MtProto.Count > 0)
        {
            MtProtoService.SaveCache(collected.MtProto);
        }

        Console.WriteLine($"Сохранено {collected.Nodes.Count} узлов и {collected.MtProto.Count} MTProto в {AppPaths.NodesPath}");
    }

    /// <summary>
    /// Генерирует seed.txt из рабочих серверов: перед релизом владелец прогоняет этот режим,
    /// получившийся файл кладётся в установщик и APK как стартовый набор. Берём только
    /// свежие рабочие серверы (проверенные не позже двух суток), ссылки без повторов.
    /// </summary>
    private static void RunExportSeed(string? outputPath)
    {
        var working = NodeSourceService.LoadCache(
            out _, out _, DateTime.UtcNow.AddDays(-2), AppPaths.WorkingNodesPath);
        var links = working
            .Where(node => node.Status == NodeStatus.Working)
            .Select(node => node.RawLink)
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .Distinct(StringComparer.Ordinal)
            .Take(300)
            .ToList();

        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(AppPaths.DataDirectory, "seed.txt")
            : outputPath;
        File.WriteAllLines(path, links);
        Console.WriteLine($"Seed: {links.Count} рабочих серверов записано в {path}");
        foreach (var link in links.Take(5))
        {
            Console.WriteLine("  " + ShortenText(link, 90));
        }
    }

    private static async Task RunLivenessAsync(int perSource, string testUrl)
    {
        Console.WriteLine($"Живой прогон: до {perSource} узлов с каждого источника, проверка через {testUrl}\n");

        var settings = new AppSettings
        {
            MaxNodesInConfig = 6000,
            TestUrl = testUrl,
            // В self-test не уводим трафик ассистента через случайный узел.
            ProxyBypass = "*.deepseek.com;*.opencode.ai;*.anthropic.com;*.openai.com;localhost;127.0.0.1;::1",
        };
        await using var manager = new ProxyManager(settings);
        var progress = new Progress<string>(line => Console.WriteLine("    " + line));

        var totals = new List<(string Name, int Total, int Working, int Average)>();
        ProxyNode? best = null;
        var stopwatch = Stopwatch.StartNew();

        foreach (var (name, url, kind) in CandidateSources)
        {
            var source = new SourceDefinition { Name = name, Url = url, Kind = kind };
            try
            {
                var outcome = await NodeSourceService.FetchAsync(source, CancellationToken.None);
                var nodes = outcome.Nodes.Take(perSource).ToList();
                if (nodes.Count == 0)
                {
                    Console.WriteLine($"  {name}: нет узлов ({outcome.Error ?? "пусто"})");
                    continue;
                }

                var testStart = stopwatch.Elapsed.TotalSeconds;
                var summary = await manager.TestNodesAsync(nodes, null, null, CancellationToken.None, timeoutMs: 4000);
                var delays = nodes.Where(n => n.DelayMs is not null).Select(n => n.DelayMs!.Value).ToList();
                var average = delays.Count > 0 ? (int)delays.Average() : 0;
                totals.Add((name, summary.Total, summary.Working, average));
                Console.WriteLine($"  {name}: рабочих {summary.Working}/{summary.Total} ({summary.Working * 100.0 / summary.Total:n1}%), средняя {average} мс, {stopwatch.Elapsed.TotalSeconds - testStart:n1} с");

                var candidate = nodes.Where(n => n.DelayMs is not null).OrderBy(n => n.DelayMs).FirstOrDefault();
                if (candidate is not null && (best is null || candidate.DelayMs < best.DelayMs))
                {
                    best = candidate;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"  {name}: ошибка — {exception.Message}");
            }
        }

        Console.WriteLine();
        var totalNodes = totals.Sum(t => t.Total);
        var totalWorking = totals.Sum(t => t.Working);
        Console.WriteLine($"Всего: рабочих {totalWorking} из {totalNodes} ({(totalNodes > 0 ? totalWorking * 100.0 / totalNodes : 0):n1}%), время {stopwatch.Elapsed.TotalSeconds:n0} с");

        if (best is null)
        {
            Console.WriteLine("Рабочих узлов нет.");
            _failed++;
            return;
        }

        Console.WriteLine($"Лучший узел: {best.Name} ({best.Protocol}, {best.Server}:{best.Port}, {best.DelayMs} мс)");
        await manager.ConnectAsync(best, progress, CancellationToken.None);
        Console.WriteLine($"Mixed-порт: {manager.ActiveMixedPort}");

        Console.WriteLine("Системный прокси: " + DescribeSystemProxy());

        var code = await CheckSiteAsync(manager.ActiveMixedPort, "https://www.youtube.com/");
        Console.WriteLine($"YouTube через локальный прокси: {(code is null ? "не открылся" : "HTTP " + code)}");
        if (code is null)
        {
            _failed++;
        }

        var systemCode = await CheckSiteAsync(null, "https://www.youtube.com/");
        Console.WriteLine($"YouTube через системный прокси: {(systemCode is null ? "не открылся" : "HTTP " + systemCode)}");
        if (systemCode is null)
        {
            _failed++;
        }

        await manager.DisconnectAsync();
        Console.WriteLine("После отключения: " + DescribeSystemProxy());
    }

    /// <summary>Полный путь подключения/отключения через ProxyManager (selector, системный прокси, проверка).</summary>
    private static async Task RunConnectFlowAsync()
    {
        Console.WriteLine("Полный цикл подключения (узел direct, проверка через доступный адрес):");
        var settings = new AppSettings
        {
            TestUrl = "https://www.gstatic.com/generate_204",
            MaxNodesInConfig = 5,
        };

        await using var manager = new ProxyManager(settings);
        var node = new ProxyNode
        {
            Name = "direct",
            Protocol = "direct",
            Server = "direct",
            Port = 1,
            Tag = "direct-flow",
            Identity = "direct-flow",
            OutboundJson = "{\"type\":\"direct\",\"tag\":\"direct-flow\"}",
        };

        await manager.ConnectAsync(node, new Progress<string>(Console.WriteLine), CancellationToken.None);
        Check(manager.IsConnected && manager.CurrentNode?.Tag == "direct-flow", "ConnectAsync подключил и выбрал узел");
        Console.WriteLine("  системный прокси: " + DescribeSystemProxy());

        var verified = await manager.VerifyThroughProxyAsync(CancellationToken.None);
        Check(verified, "VerifyThroughProxyAsync подтвердил доступ через прокси");

        await manager.DisconnectAsync();
        Check(!manager.IsConnected, "DisconnectAsync отключил");
        Console.WriteLine("  после отключения: " + DescribeSystemProxy());
    }

    /// <summary>
    /// Проверка логики статусов без внешних узлов: узел с выходом direct.
    /// Доступные адреса → Working с отметками; заблокированные → Dead (узел удаляется).
    /// </summary>
    private static void RunBlacklistTests()
    {
        Console.WriteLine("Чёрный список источников и негативный кэш серверов:");

        var directory = Path.Combine(Path.GetTempPath(), "vienlink-blacklist-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "sources-blacklist.json");
        var nodePath = Path.Combine(directory, "servers-blacklist.json");
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        SourceBlacklist.ResetForTests(sourcePath, now);
        NodeNegativeCache.ResetForTests(nodePath, now);

        try
        {
            var mirrored = SourceBlacklist.CanonicalUrl(
                "https://ghfast.top/https://raw.githubusercontent.com/owner/repo/main/list.txt");
            Check(mirrored == "https://raw.githubusercontent.com/owner/repo/main/list.txt",
                "источники: зеркало ghfast сводится к исходному адресу");

            Check(SourceBlacklist.RepositoryKey("https://raw.githubusercontent.com/Owner/Repo/main/a.txt") ==
                  "github:owner/repo",
                "источники: ключ репозитория не зависит от файла и регистра");
            Check(SourceBlacklist.RepositoryKey("https://gitlab.com/owner/repo/-/raw/main/list.txt") ==
                  "gitlab:owner/repo",
                "источники: GitLab-репозиторий распознан");

            var family1 = SourceBlacklist.FamilyKey("https://node.freeclashnode.com/1-20261123.txt");
            var family2 = SourceBlacklist.FamilyKey("https://node.freeclashnode.com/1-20261124.txt");
            Check(family1 is not null && family1 == family2,
                "источники: семейство ежедневных ссылок не зависит от даты");

            var barren = new SourceDefinition
            {
                Name = "barren",
                Url = "https://raw.githubusercontent.com/bad/repo/main/list.txt",
                TotalNodeCount = 500,
                TotalWorkingNodeCount = 0,
            };
            var barrenEntry = SourceBlacklist.BlockAuto(barren, "barren", "тест", TimeSpan.FromDays(30));
            Check(barrenEntry.Manual == false && barrenEntry.ExpiresUtc is not null,
                "источники: авто-блокировка мягкая и имеет срок");

            var sameRepoOtherFile = new SourceDefinition
            {
                Url = "https://raw.githubusercontent.com/bad/repo/main/other.txt",
            };
            Check(SourceBlacklist.IsBlocked(sameRepoOtherFile, out var blockedRepo) &&
                  blockedRepo?.Reason == "barren",
                "источники: блокировка репозитория ловит другой файл");

            SourceBlacklist.NowOverride = now.AddDays(31);
            Check(!SourceBlacklist.IsBlocked(sameRepoOtherFile, out _),
                "источники: мягкая блокировка истекает через 30 дней");

            SourceBlacklist.NowOverride = now;
            SourceBlacklist.BlockManual(sameRepoOtherFile, "тест");
            SourceBlacklist.NowOverride = now.AddDays(365);
            Check(SourceBlacklist.IsBlocked(sameRepoOtherFile, out var manualEntry) &&
                  manualEntry is { Manual: true, ExpiresUtc: null },
                "источники: ручная блокировка не истекает");

            SourceBlacklist.NowOverride = now;
            Check(SourceBlacklist.Unblock(new SourceDefinition
                  {
                      Url = "https://raw.githubusercontent.com/bad/repo/main/other.txt",
                  }) > 0,
                "источники: разблокировка снимает запись");
            Check(!SourceBlacklist.IsBlocked(sameRepoOtherFile, out _),
                "источники: после разблокировки источник снова разрешён");

            var first = new ProxyNode
            {
                Name = "first",
                Protocol = "vless",
                Server = "1.2.3.4",
                Port = 443,
                Identity = "uuid-first",
                Tag = "first",
                RawLink = "vless://uuid-first@1.2.3.4:443",
            };
            var sibling = new ProxyNode
            {
                Name = "sibling",
                Protocol = "vless",
                Server = "1.2.3.4",
                Port = 443,
                Identity = "uuid-sibling",
                Tag = "sibling",
                RawLink = "vless://uuid-sibling@1.2.3.4:443",
            };
            NodeNegativeCache.RecordDead(first);
            Check(NodeNegativeCache.IsBlocked(first),
                "кэш: провал узла запоминается");
            Check(!NodeNegativeCache.IsBlocked(sibling),
                "кэш: соседний UUID на том же IP:port не подавляется");

            NodeNegativeCache.NowOverride = now.AddHours(25);
            Check(!NodeNegativeCache.IsBlocked(first),
                "кэш: непроверенный мёртвый узел забывается через 24 часа");

            NodeNegativeCache.NowOverride = now;
            first.DelayMs = 120;
            first.UniversalOk = true;
            NodeNegativeCache.RecordDead(first);
            NodeNegativeCache.NowOverride = now.AddHours(47);
            Check(NodeNegativeCache.IsBlocked(first),
                "кэш: подтверждённо мёртвый рабочий держится 48 часов");
            NodeNegativeCache.NowOverride = now.AddHours(49);
            Check(!NodeNegativeCache.IsBlocked(first),
                "кэш: подтверждённо мёртвый рабочий истекает через 48 часов");

            NodeNegativeCache.NowOverride = now;
            NodeNegativeCache.RecordTcpDead(sibling);
            Check(NodeNegativeCache.IsBlocked(sibling),
                "кэш: TCP-провал запоминается по host:port");
            NodeNegativeCache.NowOverride = now.AddHours(7);
            Check(!NodeNegativeCache.IsBlocked(sibling),
                "кэш: TCP-провал истекает через 6 часов");

            NodeNegativeCache.NowOverride = now;
            NodeNegativeCache.RecordDead(first);
            NodeHealth.RegisterSuccess(first);
            Check(!NodeNegativeCache.IsBlocked(first),
                "кэш: успешная проверка снимает блокировку");

            NodeNegativeCache.RecordDead(first);
            NodeNegativeCache.Flush();
            NodeNegativeCache.ResetForTests(nodePath, now);
            Check(NodeNegativeCache.IsBlocked(first),
                "кэш: записи сохраняются на диск и читаются после перезапуска");

            SourceBlacklist.BlockManual(barren, "тест сохранения");
            SourceBlacklist.ResetForTests(sourcePath, now);
            Check(SourceBlacklist.IsBlocked(barren, out _),
                "источники: чёрный список сохраняется на диск");
        }
        finally
        {
            SourceBlacklist.ResetForTests();
            NodeNegativeCache.ResetForTests();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Временная папка не критична для теста.
            }
        }
    }

    private static async Task RunStatusLogicAsync()
    {
        Console.WriteLine("Логика статусов (узел direct, без внешних серверов):");

        ProxyNode DirectNode(string tag) => new()
        {
            Name = "direct",
            Protocol = "direct",
            Server = "direct",
            Port = 1,
            Tag = tag,
            Identity = tag,
            OutboundJson = "{\"type\":\"direct\",\"tag\":\"" + tag + "\"}",
        };

        var settings = new AppSettings { MaxNodesInConfig = 10 };
        await using var manager = new ProxyManager(settings);

        // Статистика источников: рабочие серверы считаются по каждой отметке источника.
        var sourceNodes = new[]
        {
            new ProxyNode { Status = NodeStatus.Working, SourceName = "A" },
            new ProxyNode { Status = NodeStatus.Working, SourceName = "A, B" },
            new ProxyNode { Status = NodeStatus.Dead, SourceName = "A" },
            new ProxyNode { Status = NodeStatus.Unknown, SourceName = "C" },
        };
        var bySource = NodeSourceStats.WorkingBySource(sourceNodes);
        Check(bySource.TryGetValue("A", out var countA) && countA == 2,
            "источники: рабочие серверы считаются по источнику");
        Check(bySource.TryGetValue("B", out var countB) && countB == 1,
            "источники: сервер из двух источников учтён у обоих");
        Check(!bySource.ContainsKey("C"),
            "источники: непроверенные и мёртвые не считаются рабочими");

        // «Всего зарегистрировано»: уникальные серверы, повторный сбор не раздувает счётчик.
        var totalSource = new SourceDefinition { Name = "T" };
        totalSource.RegisterNodes(new[]
        {
            new ProxyNode { Identity = "total-1" },
            new ProxyNode { Identity = "total-2" },
        });
        totalSource.RegisterNodes(new[] { new ProxyNode { Identity = "total-2" } });
        Check(totalSource.TotalNodeCount == 2,
            "источники: повторный сбор не увеличивает «всего зарегистрировано»");

        var seeded = new SourceDefinition { Name = "S" };
        var seededChanged = NodeSourceStats.SeedTotals(
            new[] { seeded },
            new[] { new ProxyNode { Identity = "seed-1", Status = NodeStatus.Working, SourceName = "S" } });
        Check(seededChanged && seeded.TotalNodeCount == 1,
            "источники: старый кэш заполняет «всего зарегистрировано»");

        // «Рабочих всего»: сервер засчитывается источнику один раз, только если был рабочим.
        var workingSource = new SourceDefinition { Name = "W" };
        var workingNode = new ProxyNode
        {
            Identity = "working-1",
            Status = NodeStatus.Working,
            SourceName = "W",
        };
        Check(NodeSourceStats.RegisterWorking(new[] { workingSource }, workingNode) &&
              workingSource.TotalWorkingNodeCount == 1,
            "источники: рабочий сервер засчитан источнику");
        NodeSourceStats.RegisterWorking(new[] { workingSource }, workingNode);
        Check(workingSource.TotalWorkingNodeCount == 1,
            "источники: повторная проверка не увеличивает «рабочих всего»");
        var deadSource = new SourceDefinition { Name = "D" };
        NodeSourceStats.RegisterWorking(new[] { deadSource },
            new ProxyNode { Identity = "dead-1", Status = NodeStatus.Dead, SourceName = "D" });
        Check(deadSource.TotalWorkingNodeCount == 0,
            "источники: мёртвый сервер не считается рабочим");
        Check(seeded.TotalWorkingNodeCount == 1,
            "источники: рабочий сервер из кэша попадает в «рабочих всего»");

        // Ротация «бесплодных» источников: давний, много серверов, ноль рабочих.
        var barrenSource = new SourceDefinition
        {
            Name = "barren",
            TotalNodeCount = 500,
            TotalWorkingNodeCount = 0,
            FirstSeenUtc = DateTime.UtcNow.AddDays(-10),
        };
        var (rotDisabled, rotRemoved) = NodeSourceStats.RotateBarrenSources(new[] { barrenSource }, DateTime.UtcNow);
        Check(rotDisabled.Count == 1 && ReferenceEquals(rotDisabled[0], barrenSource) &&
              barrenSource.BarrenSinceUtc is not null && rotRemoved.Count == 0,
            "источники: бесплодный источник отключается и помечается");
        var (rotDisabled2, rotRemoved2) = NodeSourceStats.RotateBarrenSources(new[] { barrenSource }, DateTime.UtcNow);
        Check(rotDisabled2.Count == 0 && rotRemoved2.Count == 0,
            "источники: помеченный бесплодный не отключается повторно");
        var (_, rotRemoved3) = NodeSourceStats.RotateBarrenSources(new[] { barrenSource }, DateTime.UtcNow.AddDays(8));
        Check(rotRemoved3.Count == 1,
            "источники: бесплодный удаляется после карантина");

        var goodSource = new SourceDefinition
        {
            Name = "good",
            TotalNodeCount = 500,
            TotalWorkingNodeCount = 1,
            FirstSeenUtc = DateTime.UtcNow.AddDays(-10),
            BarrenSinceUtc = DateTime.UtcNow.AddDays(-2),
        };
        var (rotDisabled4, rotRemoved4) = NodeSourceStats.RotateBarrenSources(new[] { goodSource }, DateTime.UtcNow);
        Check(rotDisabled4.Count == 0 && rotRemoved4.Count == 0 && goodSource.BarrenSinceUtc is null,
            "источники: источник с рабочим сервером не бесплодный");

        var freshSource = new SourceDefinition
        {
            Name = "fresh",
            TotalNodeCount = 1000,
            TotalWorkingNodeCount = 0,
            FirstSeenUtc = DateTime.UtcNow.AddHours(-1),
        };
        var (rotDisabled5, _) = NodeSourceStats.RotateBarrenSources(new[] { freshSource }, DateTime.UtcNow);
        Check(rotDisabled5.Count == 0,
            "источники: свежий источник не отключается сразу");

        // Контент-дедуп: одинаковый текст у разных источников — зеркала.
        var mirrorA = new FetchOutcome
        {
            Source = new SourceDefinition { Name = "A", Url = "https://a/sub", IsAutoDiscovered = true, TotalWorkingNodeCount = 1 },
            ContentHash = "HASH1",
        };
        var mirrorB = new FetchOutcome
        {
            Source = new SourceDefinition { Name = "B", Url = "https://b/sub", IsAutoDiscovered = true, TotalWorkingNodeCount = 0 },
            ContentHash = "HASH1",
        };
        var (dedupRemove, dedupSkip) = NodeSourceStats.DedupMirrors(new[] { mirrorA, mirrorB });
        Check(dedupRemove.Count == 1 && ReferenceEquals(dedupRemove[0], mirrorB.Source) &&
              dedupSkip.Count == 1 && dedupSkip.Contains(mirrorB),
            "источники: автоподобранное зеркало удаляется, лучший остаётся");

        var builtA = new FetchOutcome
        {
            Source = new SourceDefinition { Name = "built-a", Url = "https://a2/sub", TotalWorkingNodeCount = 1 },
            ContentHash = "HASH2",
        };
        var builtB = new FetchOutcome
        {
            Source = new SourceDefinition { Name = "built-b", Url = "https://b2/sub", TotalWorkingNodeCount = 0 },
            ContentHash = "HASH2",
        };
        var (dedupRemove2, dedupSkip2) = NodeSourceStats.DedupMirrors(new[] { builtA, builtB });
        Check(dedupRemove2.Count == 0 && dedupSkip2.Count == 1 && dedupSkip2.Contains(builtB),
            "источники: встроенное зеркало не удаляется, но пропускается");

        var (dedupRemove3, dedupSkip3) = NodeSourceStats.DedupMirrors(new[]
        {
            new FetchOutcome { Source = new SourceDefinition { Name = "x", Url = "https://x" }, ContentHash = "H1" },
            new FetchOutcome { Source = new SourceDefinition { Name = "y", Url = "https://y" }, ContentHash = "H2" },
        });
        Check(dedupRemove3.Count == 0 && dedupSkip3.Count == 0,
            "источники: разное содержимое не зеркала");

        // Карантин источников без даты: удаляются не сразу, а после срока.
        var quarantined = new SourceDefinition { Name = "q", QuarantineSinceUtc = DateTime.UtcNow.AddDays(-8) };
        var quarantineRemoval = NodeSourceStats.RotateQuarantinedSources(new[] { quarantined }, DateTime.UtcNow);
        Check(quarantineRemoval.Count == 1 && ReferenceEquals(quarantineRemoval[0], quarantined),
            "источники: карантинный источник удаляется после срока");
        var freshQuarantine = new SourceDefinition { Name = "q2", QuarantineSinceUtc = DateTime.UtcNow.AddHours(-1) };
        Check(NodeSourceStats.RotateQuarantinedSources(new[] { freshQuarantine }, DateTime.UtcNow).Count == 0,
            "источники: свежий карантинный источник не удаляется");
        var mineQuarantined = new SourceDefinition { Name = "q3", IsMine = true, QuarantineSinceUtc = DateTime.UtcNow.AddDays(-8) };
        Check(NodeSourceStats.RotateQuarantinedSources(new[] { mineQuarantined }, DateTime.UtcNow).Count == 0,
            "источники: личный источник в карантине не удаляется");

        // Бюджет обращений к GitHub API за сбор.
        var apiBudget = new GitHubSourceFreshness.ApiBudget(2);
        Check(apiBudget.TryTake() && apiBudget.TryTake() && !apiBudget.TryTake(),
            "бюджет API: ровно лимит обращений");
        var budgetless = await GitHubSourceFreshness.CheckAsync(
            new SourceDefinition { Name = "b", Url = "https://raw.githubusercontent.com/foo/bar/main/sub.txt" },
            TimeSpan.FromHours(24), CancellationToken.None, new GitHubSourceFreshness.ApiBudget(0));
        Check(!budgetless.Fresh && !budgetless.DateKnown && !budgetless.ApiReached,
            "бюджет API исчерпан: источник собирается без проверки даты");

        // Свежесть не-GitHub источника (автопоиск сайтов) по дате публикации в адресе.
        var webFresh = await GitHubSourceFreshness.CheckAsync(
            new SourceDefinition { Name = "w", Url = "https://nodefree.org/f/123.txt", LastPublishedUtc = DateTime.UtcNow.AddHours(-1) },
            TimeSpan.FromHours(24), CancellationToken.None);
        Check(webFresh.Fresh && webFresh.DateKnown,
            "сбор: не-GitHub источник с датой публикации свежий");
        var webStale = await GitHubSourceFreshness.CheckAsync(
            new SourceDefinition { Name = "w2", Url = "https://nodefree.org/f/123.txt", LastPublishedUtc = DateTime.UtcNow.AddHours(-30) },
            TimeSpan.FromHours(24), CancellationToken.None);
        Check(!webStale.Fresh && webStale.DateKnown,
            "сбор: не-GitHub источник с устаревшей датой пропускается");

        // Доверенный источник: обновлялся не реже раза в 24 часа.
        Check(new SourceDefinition { LastPublishedUtc = DateTime.UtcNow.AddHours(-1) }.IsTrustedNow &&
              !new SourceDefinition { LastPublishedUtc = DateTime.UtcNow.AddHours(-25) }.IsTrustedNow &&
              !new SourceDefinition().IsTrustedNow,
            "источники: доверенный только при публикации за последние 24 ч");

        // Автопоиск источников: GitHub-репозитории старше 24 ч и без даты отсеиваются.
        var freshnessNow = DateTime.UtcNow;
        Check(SourceDiscoveryService.IsRepositoryFresh(
                freshnessNow.AddHours(-23).ToString("O"), freshnessNow),
            "автопоиск: репозиторий возрастом 23 ч принимается");
        Check(!SourceDiscoveryService.IsRepositoryFresh(
                freshnessNow.AddHours(-25).ToString("O"), freshnessNow),
            "автопоиск: репозиторий возрастом 25 ч отсеивается");
        Check(!SourceDiscoveryService.IsRepositoryFresh(null, freshnessNow) &&
              !SourceDiscoveryService.IsRepositoryFresh("не дата", freshnessNow),
            "автопоиск: без даты публикации источник отсеивается");

        // Сбор серверов: GitHub-репозиторий старше 24 ч пропускается.
        Check(GitHubSourceFreshness.TryGetRepository(
                "https://raw.githubusercontent.com/owner/repo/main/sub.txt") == "owner/repo",
            "сбор: ссылка raw.githubusercontent разобрана в owner/repo");
        var fileLocation = GitHubSourceFreshness.TryGetLocation(
            "https://raw.githubusercontent.com/owner/repo/main/sub/file.txt");
        Check(fileLocation is not null &&
              fileLocation.Host == "github" &&
              fileLocation.Reference == "main" &&
              fileLocation.Path == "sub/file.txt",
            "сбор: путь к файлу в источнике разобран");
        var gitlabLocation = GitHubSourceFreshness.TryGetLocation(
            "https://gitlab.com/owner/repo/-/raw/main/sub/file.txt");
        Check(gitlabLocation is not null && gitlabLocation.Host == "gitlab" &&
              gitlabLocation.Repository == "owner/repo" &&
              gitlabLocation.Reference == "main" && gitlabLocation.Path == "sub/file.txt",
            "сбор: ссылка GitLab raw разобрана");
        var giteeLocation = GitHubSourceFreshness.TryGetLocation(
            "https://gitee.com/owner/repo/raw/master/file.txt");
        Check(giteeLocation is not null && giteeLocation.Host == "gitee" &&
              giteeLocation.Repository == "owner/repo" &&
              giteeLocation.Reference == "master" && giteeLocation.Path == "file.txt",
            "сбор: ссылка Gitee raw разобрана");
        Check(GitHubSourceFreshness.TryGetRepository("https://example.com/sub") is null,
            "сбор: не-GitHub ссылка не даёт репозиторий");
        var pushedNow = DateTimeOffset.UtcNow;
        Check(GitHubSourceFreshness.IsPushedAtFresh(pushedNow.AddHours(-23), pushedNow.UtcDateTime) &&
              !GitHubSourceFreshness.IsPushedAtFresh(pushedNow.AddHours(-25), pushedNow.UtcDateTime) &&
              !GitHubSourceFreshness.IsPushedAtFresh(null, pushedNow.UtcDateTime),
            "сбор: свежесть репозитория 24 ч и отсутствие даты отсекается");

        // Пауза после провала: упавший рабочий сервер не должен снова попадать
        // в следующую партию, иначе три провала набираются за минуту.
        var retryNode = new ProxyNode
        {
            Status = NodeStatus.Working,
            FailCount = 1,
            CheckedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            TriedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        };
        Check(!NodeAvailability.RetryAllowed(retryNode, DateTime.UtcNow),
            "перепроверка: после провала рабочий сервер ждёт паузу");
        retryNode.FailCount = 0;
        Check(NodeAvailability.RetryAllowed(retryNode, DateTime.UtcNow),
            "перепроверка: успешная проверка снимает паузу");
        retryNode.FailCount = 1;
        retryNode.TriedAtUtc = DateTime.UtcNow.AddMinutes(-NodeLimits.WorkingRetryMinutes - 1);
        Check(NodeAvailability.RetryAllowed(retryNode, DateTime.UtcNow),
            "перепроверка: после паузы сервер снова проверяется");

        // 1. Оба проверочных адреса доступны → узел рабочий, отметки стоят.
        var working = DirectNode("direct-a");
        var summary = await manager.TestNodesAsync(
            new[] { working },
            null,
            null,
            CancellationToken.None,
            concurrency: 2,
            timeoutMs: 8000,
            youtubeUrl: "https://www.gstatic.com/generate_204",
            telegramUrl: "https://www.google.com/generate_204");
        Check(working.Status == NodeStatus.Working, "доступные адреса → узел рабочий");
        Check(working.YoutubeOk == true && working.TelegramOk == true, "обе отметки YouTube/Telegram");
        Check(summary.YoutubeOk == 1 && summary.TelegramOk == 1, "сводка считает оба сервиса");

        // 2. Узел, который ничего не открывает (block-аутбаунд отбрасывает всё, включая
        // Cloudflare) → помечается нерабочим и удаляется после первой неудачной проверки.
        var dead = new ProxyNode
        {
            Name = "block",
            Protocol = "block",
            Server = "block",
            Port = 1,
            Tag = "block-b",
            Identity = "block-b",
            OutboundJson = "{\"type\":\"block\",\"tag\":\"block-b\"}",
        };
        await manager.TestNodesAsync(
            new[] { dead },
            null,
            null,
            CancellationToken.None,
            concurrency: 2,
            timeoutMs: 3000,
            youtubeUrl: "https://www.youtube.com/",
            telegramUrl: "https://t.me/");
        Check(dead.Status == NodeStatus.Dead && dead.FailCount == 1,
            "непроверенный удаляется после первой неудачной проверки");
        Check(dead.YoutubeOk == false && dead.TelegramOk == false, "обе отметки — отказ");

        // 3. Узел, который уже был рабочим, получает несколько попыток, затем удаляется.
        var retried = DirectNode("direct-c");
        NodeHealth.RegisterSuccess(retried);
        var removedAt = 0;
        for (var attempt = 1; attempt <= NodeLimits.RemoveAfterFailures; attempt++)
        {
            var removed = NodeHealth.RegisterFailure(retried);
            if (removed)
            {
                removedAt = attempt;
            }

            if (attempt < NodeLimits.RemoveAfterFailures)
            {
                Check(retried.Status == NodeStatus.Working,
                    $"провал {attempt}: узел остаётся в «Рабочих» до {NodeLimits.RemoveAfterFailures}-го провала");
            }
        }

        Check(removedAt == NodeLimits.RemoveAfterFailures,
            $"рабочий узел удаляется только на {NodeLimits.RemoveAfterFailures}-м провале");
        Check(retried.Status == NodeStatus.Dead,
            $"после {NodeLimits.RemoveAfterFailures} провалов → нерабочий");
        NodeHealth.RegisterSuccess(retried);
        Check(retried.Status == NodeStatus.Working && retried.FailCount == 0,
            "успешная проверка обнуляет счётчик");

        // «Моргающий» сервер: провалов больше порога, но последний успех был недавно —
        // в мёртвые не переводим, иначе VLESS/Reality уезжает в отсев, хотя ещё жив.
        var flaky = DirectNode("direct-flaky");
        NodeHealth.RegisterSuccess(flaky);
        flaky.CheckedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        for (var attempt = 0; attempt < NodeLimits.RemoveAfterFailures + 2; attempt++)
        {
            NodeHealth.RegisterFailure(flaky);
        }

        Check(flaky.Status == NodeStatus.Working,
            "моргающий сервер не уходит в мёртвые, пока недавно открывал сайт");
        flaky.CheckedAtUtc = DateTime.UtcNow.AddMinutes(-NodeLimits.WorkingDeadAfterMinutes - 5);
        NodeHealth.RegisterFailure(flaky);
        Check(flaky.Status == NodeStatus.Dead,
            "после часа без успеха и повторных провалов сервер удаляется");

        // Проверка через поднятый прокси: Cloudflare первым и сервис, который сервер
        // уже открывал. Только YouTube браковал рабочий сервер на провайдере, где
        // YouTube режется по IP, — и после пяти подключений сервер удалялся.
        var urlsNode = new ProxyNode { UniversalOk = false, YoutubeOk = true };
        var verifyUrls = ProxyManager.VerificationUrls(urlsNode);
        Check(verifyUrls[0] == ProxyManager.UniversalTestUrl,
            "проверка через прокси: универсальный Cloudflare первым");
        Check(verifyUrls.Contains(ProxyManager.YoutubeTestUrl),
            "проверка через прокси: добавлен открытый сервис");
        Check(ProxyManager.VerificationUrls(new ProxyNode { UniversalOk = true }).Count == 1,
            "проверка через прокси: для универсального сервера хватает Cloudflare");

        // 5. Переключение: сначала серверы выбранного сервиса, даже если они медленнее,
        // затем следующие лучшие из других сервисов. Новый универсальный — первым.
        var nowForRank = DateTime.UtcNow;
        ProxyNode RankNode(string tag, int delay, bool universal = false, bool youtube = false,
            bool telegram = false, DateTime? checkedAt = null) => new()
        {
            Tag = tag,
            Name = tag,
            Status = NodeStatus.Working,
            DelayMs = delay,
            UniversalOk = universal,
            YoutubeOk = youtube,
            TelegramOk = telegram,
            CheckedAtUtc = checkedAt ?? nowForRank,
        };

        var universalSlow = RankNode("universal-slow", 900, universal: true);
        var youtubeFast = RankNode("youtube-fast", 100, youtube: true);
        var telegramFast = RankNode("telegram-fast", 50, telegram: true);
        var staleUniversal = RankNode("universal-stale", 10, universal: true,
            checkedAt: nowForRank.AddHours(-2));
        var rankPool = new[] { telegramFast, youtubeFast, universalSlow, staleUniversal };

        var universalRank = NodeFailover.Rank(rankPool, "universal", nowForRank, onlyMine: false);
        Check(universalRank[0].Tag == "universal-slow",
            "переключение: сервер выбранного сервиса важнее скорости");
        Check(universalRank.All(node => node.Tag != "universal-stale"),
            "переключение: устаревшая проверка не участвует");
        Check(universalRank.Count == 3, "переключение: остальные сервисы остаются в резерве");

        var youtubeRank = NodeFailover.Rank(rankPool, "youtube", nowForRank, onlyMine: false);
        Check(youtubeRank[0].Tag == "youtube-fast", "переключение: YouTube первым для YouTube");
        Check(youtubeRank[1].Tag == "telegram-fast", "переключение: затем лучший из других сервисов");

        var newUniversal = RankNode("universal-new", 10, universal: true);
        var withNew = NodeFailover.Rank(rankPool.Append(newUniversal), "universal", nowForRank, onlyMine: false);
        Check(withNew[0].Tag == "universal-new",
            "переключение: новый быстрый универсальный встаёт на 1 место");
        Check(withNew.Take(2).All(node => node.UniversalOk == true),
            "переключение: оба универсальных впереди остальных");

        // 6. TCP-отсев: закрытый порт отбрасывается, живой — остаётся, UDP-протокол
        // (Hysteria2) не трогаем, потому что TCP для него ничего не значит.
        var preListener = new TcpListener(IPAddress.Loopback, 0);
        preListener.Start();
        var livePort = ((IPEndPoint)preListener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var accepted = await preListener.AcceptTcpClientAsync();
                }
            }
            catch (Exception)
            {
                // Слушатель остановлен после проверки.
            }
        });

        try
        {
            ProxyNode PreNode(string tag, string protocol, int port) => new()
            {
                Tag = tag,
                Name = tag,
                Protocol = protocol,
                Server = "127.0.0.1",
                Port = port,
            };

            var prePool = Enumerable.Range(0, 10)
                .SelectMany(_ => new[]
                {
                    PreNode("pre-closed", "vless", 1),
                    PreNode("pre-open", "vless", livePort),
                    PreNode("pre-udp", "hysteria2", 1),
                })
                .ToList();
            var preAlive = await NodePrecheck.FilterAliveAsync(
                prePool, 16, 2000, null, CancellationToken.None);
            Check(preAlive.All(node => node.Tag != "pre-closed"),
                "TCP-отсев: закрытый порт отброшен");
            Check(preAlive.Count(node => node.Tag == "pre-open") == 10,
                "TCP-отсев: живой адрес остаётся");
            Check(preAlive.Count(node => node.Tag == "pre-udp") == 10,
                "TCP-отсев: UDP-протокол не отсеивается по TCP");
        }
        finally
        {
            preListener.Stop();
        }

        // 7. Кэш: возраст источника не должен выбрасывать уже проверенный рабочий сервер.
        var cacheCutoff = DateTime.UtcNow.AddDays(-7);
        Check(!NodeSourceService.IsExpiredBySourceAge(
                NodeStatus.Working, cacheCutoff.AddDays(-1), cacheCutoff),
            "кэш: рабочий сервер не выбрасывается по возрасту источника");
        Check(NodeSourceService.IsExpiredBySourceAge(
                NodeStatus.Unknown, cacheCutoff.AddDays(-1), cacheCutoff),
            "кэш: старый непроверенный сервер отбрасывается");
        Check(!NodeSourceService.IsExpiredBySourceAge(
                NodeStatus.Unknown, cacheCutoff.AddDays(1), cacheCutoff),
            "кэш: свежий непроверенный сервер остаётся");

        // 8. Провал проверки: рабочий сервер не удаляется с первого раза, непроверенный — сразу.
        var failing = new ProxyNode
        {
            Status = NodeStatus.Working,
            DelayMs = 100,
            UniversalOk = true,
            CheckedAtUtc = DateTime.UtcNow,
        };
        NodeHealth.RegisterFailure(failing);
        Check(failing.Status == NodeStatus.Working && failing.FailCount == 1 &&
              failing.DelayMs == 100 && failing.UniversalOk == true,
            "провал: первый провал рабочего сервера не стирает его результат");
        for (var attempt = 1; attempt < NodeLimits.RemoveAfterFailures; attempt++)
        {
            NodeHealth.RegisterFailure(failing);
        }

        Check(failing.Status == NodeStatus.Working &&
              failing.FailCount == NodeLimits.RemoveAfterFailures,
            $"провал: {NodeLimits.RemoveAfterFailures} провалов за час не отправляют " +
            "недавно работавший сервер в мёртвые");
        failing.CheckedAtUtc = DateTime.UtcNow.AddMinutes(-NodeLimits.WorkingDeadAfterMinutes - 5);
        NodeHealth.RegisterFailure(failing);
        Check(failing.Status == NodeStatus.Dead,
            "провал: после часа без успеха сервер помечается мёртвым");
        var unproven = new ProxyNode { Status = NodeStatus.Unknown };
        NodeHealth.RegisterFailure(unproven);
        Check(unproven.Status == NodeStatus.Dead,
            "провал: непроверенный удаляется после первой неудачной проверки");

        // 9. Кэш можно писать и читать по отдельному пути: так рабочие серверы
        // живут в своём маленьком файле, отдельно от кучи непроверенных.
        var cachePath = Path.Combine(Path.GetTempPath(), "vienlink-cache-check.json");
        try
        {
            var sample = new ProxyNode
            {
                RawLink = "vless://00000000-0000-0000-0000-000000000000@example.com:443",
                Protocol = "vless",
                Name = "cache-check",
                Server = "example.com",
                Port = 443,
                Tag = "cache-check",
                Identity = "00000000-0000-0000-0000-000000000000",
                OutboundJson = "{}",
                Status = NodeStatus.Working,
                DelayMs = 42,
                CheckedAtUtc = DateTime.UtcNow,
                UniversalOk = true,
            };
            NodeSourceService.SaveCache(new[] { sample }, allowEmpty: true, cachePath);
            var back = NodeSourceService.LoadCache(out _, out _, null, cachePath);
            Check(back.Count == 1 && back[0].Tag == "cache-check",
                "кэш: запись и чтение по указанному пути");
        }
        finally
        {
            try
            {
                File.Delete(cachePath);
                File.Delete(cachePath + ".bak");
            }
            catch (Exception)
            {
                // Временный файл удалится позже.
            }
        }

        var now = DateTime.UtcNow;

        // 4. Выбор для подключения — только свежая проверка: устаревший кэш
        // не должен считаться доступным для подключения.
        var freshNode = new ProxyNode
        {
            Status = NodeStatus.Working,
            DelayMs = 120,
            CheckedAtUtc = now,
            YoutubeOk = true,
        };
        Check(NodeAvailability.IsUsable(freshNode, NodeLimits.WorkingRecheckMinutes, onlyMine: false, now),
            "свежий рабочий узел считается доступным");
        Check(NodeAvailability.OpenedFor(freshNode, "youtube"), "значок YouTube видит узел");
        Check(!NodeAvailability.OpenedFor(freshNode, "universal"),
            "универсальный доступ не выдумывается из доступа к YouTube");

        var staleNode = new ProxyNode
        {
            Status = NodeStatus.Working,
            DelayMs = 120,
            CheckedAtUtc = now.AddHours(-8),
            YoutubeOk = true,
        };
        Check(!NodeAvailability.IsUsable(staleNode, NodeLimits.WorkingRecheckMinutes, onlyMine: false, now),
            "проверка старше окна свежести — узел не показывается");
        Check(NodeAvailability.IsUsable(staleNode, NodeLimits.WorkingRecheckMinutes, onlyMine: false, staleNode.CheckedAtUtc.Value),
            "на момент своей проверки узел ещё был доступен");

        var unknownWithDelay = new ProxyNode
        {
            Status = NodeStatus.Unknown,
            DelayMs = 120,
            CheckedAtUtc = now,
        };
        Check(!NodeAvailability.IsUsable(unknownWithDelay, NodeLimits.WorkingRecheckMinutes, onlyMine: false, now),
            "непроверенный узел с остатком задержки не считается рабочим");

        // 5. Счётчики в шапке считают мягче выбора: результат не стирается по таймеру
        // 5 минут (иначе значки падают в ноль на каждой перепроверке), но подтверждённый
        // провал и запись старше часа из счётчиков убирают.
        var staleButWorking = new ProxyNode
        {
            Status = NodeStatus.Working,
            DelayMs = 120,
            CheckedAtUtc = now.AddMinutes(-12),
            YoutubeOk = true,
        };
        Check(NodeAvailability.IsCountable(staleButWorking, onlyMine: false, now),
            "счётчик видит рабочего с проверкой 12 минут назад");
        Check(!NodeAvailability.IsCountable(
                new ProxyNode
                {
                    Status = NodeStatus.Working,
                    DelayMs = 120,
                    CheckedAtUtc = now,
                    FailCount = 1,
                },
                onlyMine: false, now),
            "подтверждённый провал убирает узел из счётчиков");
        Check(!NodeAvailability.IsCountable(
                new ProxyNode
                {
                    Status = NodeStatus.Working,
                    DelayMs = 120,
                    CheckedAtUtc = now.AddHours(-2),
                },
                onlyMine: false, now),
            "результат старше часа не показывается в счётчиках");

        // Android меряет задержку только по Cloudflare: узел, открывший лишь YouTube,
        // обязан остаться доступным для кнопки YouTube.
        var youtubeOnly = new ProxyNode
        {
            Status = NodeStatus.Working,
            DelayMs = null,
            CheckedAtUtc = now,
            YoutubeOk = true,
        };
        Check(NodeAvailability.IsUsable(youtubeOnly, NodeLimits.WorkingRecheckMinutes, onlyMine: false, now) &&
              NodeAvailability.OpenedFor(youtubeOnly, "youtube"),
            "узел без задержки Cloudflare, но с YouTube — доступен для YouTube");
        Check(!NodeAvailability.OpenedFor(youtubeOnly, "universal"),
            "узел без Cloudflare не считается универсальным");

        var mineNode = new ProxyNode
        {
            Status = NodeStatus.Working,
            DelayMs = 120,
            CheckedAtUtc = now,
            IsMine = true,
        };
        Check(!NodeAvailability.OpenedFor(mineNode, "universal"),
            "без успешной проверки Cloudflare узла нет в универсальных");
        mineNode.UniversalOk = true;
        Check(NodeAvailability.IsUsable(mineNode, NodeLimits.WorkingRecheckMinutes, onlyMine: true, now) &&
              NodeAvailability.OpenedFor(mineNode, "universal"),
            "свежий личный универсальный узел доступен при «Только мои»");

        // MTProto: проверка — настоящий хендшейк с Telegram. Открытый TCP-порт без MTProto
        // больше не считается «рабочим»: именно из-за этого Telegram отказывался добавлять прокси.
        var silentPort = FindFreePort(23100);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, silentPort);
        listener.Start();
        try
        {
            const string validSecret = "ee00112233445566778899aabbccddeeff676f6f676c652e636f6d";
            var silent = new MtProtoProxy { Server = "127.0.0.1", Port = silentPort, Secret = validSecret };
            var unreachable = new MtProtoProxy
            {
                Server = "127.0.0.1",
                Port = FindFreePort(23200),
                Secret = validSecret,
            };
            var brokenSecret = new MtProtoProxy
            {
                Server = "127.0.0.1",
                Port = silentPort,
                Secret = "7pJZSUjIp-43RmluNUkNmWNkbnMuZ11vZ2xlLmNvbQ==",
            };

            await MtProtoService.TestAsync(
                new[] { silent, unreachable, brokenSecret },
                null,
                null,
                CancellationToken.None,
                concurrency: 3,
                timeoutMs: 1500);

            Check(silent.Status == MtProtoStatus.Dead,
                "MTProto: открытый порт без MTProto — нерабочий");
            Check(unreachable.Status == MtProtoStatus.Dead && unreachable.DeadAtUtc is not null,
                "MTProto: закрытый порт — нерабочий с датой");
            Check(brokenSecret.Status == MtProtoStatus.Dead,
                "MTProto: секрет с испорченным доменом — нерабочий");
        }
        finally
        {
            listener.Stop();
        }

        Check(MtProtoRetention.IsExpired(new MtProtoProxy { Status = MtProtoStatus.Dead, DeadAtUtc = now.AddHours(-25) }, now),
            "нерабочий MTProto старше 24 часов удаляется");
        Check(!MtProtoRetention.IsExpired(new MtProtoProxy { Status = MtProtoStatus.Dead, DeadAtUtc = now.AddHours(-23) }, now),
            "свежий нерабочий MTProto остаётся");
        Check(!MtProtoRetention.IsExpired(new MtProtoProxy { Status = MtProtoStatus.Working, DeadAtUtc = now.AddDays(-30) }, now),
            "рабочий MTProto не удаляется");
        Check(MtProtoSchedule.WorkingRecheckMinutes < MtProtoSchedule.DeadRecheckHours * 60,
            "рабочие MTProto проверяются чаще нерабочих");
        Check(MtProtoRetention.DeadHours == 24, "нерабочий MTProto хранится 24 часа");

        var deadOverflow = Enumerable.Range(0, 4)
            .Select(i => new MtProtoProxy { Status = MtProtoStatus.Dead, DeadAtUtc = now.AddHours(-i) })
            .ToList();
        var trimmedDead = MtProtoRetention.TrimDead(deadOverflow, 2);
        Check(trimmedDead == 2 && deadOverflow.Count == 2, "лимит MTProto: лишние нерабочие удаляются");

        await manager.DisconnectAsync();
    }

    /// <summary>
    /// Экспорт профиля для телефона: ссылка исходного формата и конфиг sing-box.
    /// Конфиг проверяется настоящим `sing-box check` в двух вариантах — с TUN и без.
    /// </summary>
    private static async Task RunExportAsync()
    {
        Console.WriteLine("Экспорт профиля (ссылка + конфиг для Android-клиентов):");
        var nodes = new List<(string Name, ProxyNode? Node)>
        {
            ("vless + reality", LinkParser.Parse(
                "vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=ab&fp=firefox&sni=www.microsoft.com#E", "t")),
            ("trojan + ws", LinkParser.Parse("trojan://pass@example.com:8443?sni=example.com&type=ws&path=%2Ft#E", "t")),
            ("hysteria2", LinkParser.Parse("hysteria2://pass@example.com:443?sni=example.com&insecure=1#E", "t")),
        };

        var parsed = new List<ProxyNode>();
        foreach (var (name, node) in nodes)
        {
            if (node is null)
            {
                Check(false, name + " — ссылка не разобралась");
                continue;
            }

            parsed.Add(node);
            var link = ProfileExport.BuildShareLink(node);
            Check(link.StartsWith(node.Protocol, StringComparison.OrdinalIgnoreCase),
                name + " — ссылка: " + (link.Length <= 60 ? link : link[..60] + "…"));

            var path = Path.Combine(Path.GetTempPath(), "vienlink-export.json");
            foreach (var tunnel in new[] { true, false })
            {
                var json = ProfileExport.BuildConfig(node, tunnel);
                SingBoxConfigBuilder.WriteToFile(path, json);
                try
                {
                    await SingBoxRunner.CheckConfigAsync(path, CancellationToken.None);
                    var expectTun = tunnel ? json.Contains("\"tun\"", StringComparison.Ordinal) : !json.Contains("\"tun\"", StringComparison.Ordinal);
                    Check(expectTun, name + (tunnel ? " — конфиг с TUN" : " — конфиг без TUN") + " проходит sing-box check");
                }
                catch (Exception exception)
                {
                    Check(false, name + (tunnel ? " — TUN" : " — mixed") + ": " + exception.Message);
                }
            }

            Check(ProfileExport.SuggestFileName(node).StartsWith("vienlink-", StringComparison.Ordinal),
                name + " — имя файла: " + ProfileExport.SuggestFileName(node));
        }

        var list = ProfileExport.BuildLinkList(parsed);
        var lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Check(lines == parsed.Count, "список ссылок: " + lines + " строк");
    }

    /// <summary>Проверяет все варианты конфига настоящим `sing-box check` (без прав администратора).</summary>
    private static async Task RunConfigChecksAsync()
    {
        Console.WriteLine("Проверка вариантов конфига через sing-box check:");
        var node = LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&type=ws&path=%2Fws#T", "test");
        if (node is null)
        {
            Check(false, "тестовый узел не разобрался");
            return;
        }

        var variants = new (string Name, AppSettings Settings)[]
        {
            ("системный прокси, всё", new AppSettings()),            ("системный прокси, только сайты", new AppSettings { SiteRouteMode = SiteRouteMode.OnlyListed, SiteList = "youtube.com" }),
            ("системный прокси, только приложения", new AppSettings { OnlySelectedApps = true, AppList = "chrome.exe" }),
            ("системный прокси, видео YouTube через узел", new AppSettings
            {
                SiteRouteMode = SiteRouteMode.OnlyListed,
                SiteList = "youtube.com\nyoutu.be\ngooglevideo.com\nytimg.com\nggpht.com\nyoutube-nocookie.com\n" +
                           "youtubei.googleapis.com\njnn-pa.googleapis.com\nyoutubeembeddedplayer.googleapis.com\n" +
                           "youtube-ui.l.google.com\nwide-youtube.l.google.com\nyt-video-upload.l.google.com\nytimg.l.google.com",
            }),
            ("системный прокси, всё кроме сайтов + приложения", new AppSettings
            {
                SiteRouteMode = SiteRouteMode.AllExceptListed,
                SiteList = "example.com",
                OnlySelectedApps = true,
                AppList = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
            }),
            ("TUN, всё", new AppSettings { TunnelMode = TunnelMode.Tun }),
            ("TUN строгий, кроме сайтов + приложения", new AppSettings
            {
                TunnelMode = TunnelMode.Tun,
                TunStrictRoute = true,
                SiteRouteMode = SiteRouteMode.AllExceptListed,
                SiteList = "example.com",
                OnlySelectedApps = true,
                AppList = "curl.exe",
            }),
            ("TLS-фрагментация (tls.fragment)", new AppSettings { TlsFragment = true }),
            ("TLS-фрагментация + record_fragment", new AppSettings { TlsFragment = true, TlsRecordFragment = true }),
        };

        var path = Path.Combine(AppPaths.RuntimeDirectory, "variant-config.json");
        foreach (var (name, settings) in variants)
        {
            var json = SingBoxConfigBuilder.Build(new[] { node }, node.Tag, 22000, 22001, "s", settings);
            SingBoxConfigBuilder.WriteToFile(path, json);
            try
            {
                await SingBoxRunner.CheckConfigAsync(path, CancellationToken.None);
                Check(true, name);
            }
            catch (Exception exception)
            {
                Check(false, name + " — " + exception.Message);
            }
        }

        var probeDnsConfig = SingBoxConfigBuilder.Build(
            new[] { node }, node.Tag, 22000, 22001, "s", new AppSettings(), explicitDns: true);
        SingBoxConfigBuilder.WriteToFile(path, probeDnsConfig);
        try
        {
            await SingBoxRunner.CheckConfigAsync(path, CancellationToken.None);
            Check(true, "пробное ядро: системный прокси + явный DNS");
        }
        catch (Exception exception)
        {
            Check(false, "пробное ядро: системный прокси + явный DNS — " + exception.Message);
        }

        var fragmentConfig = SingBoxConfigBuilder.Build(
            new[] { node }, node.Tag, 22000, 22001, "s",
            new AppSettings { TlsFragment = true, TlsRecordFragment = true });
        Check(fragmentConfig.Contains("\"fragment\": true", StringComparison.Ordinal),
            "TLS-фрагментация: tls.fragment в конфиге");
        Check(fragmentConfig.Contains("\"record_fragment\": true", StringComparison.Ordinal),
            "TLS-фрагментация: record_fragment в конфиге");

        Console.WriteLine("TUN-обход (без прокси-узла и WinDivert):");
        foreach (var blockQuic in new[] { true, false })
        {
            var bypass = SingBoxConfigBuilder.BuildTunBypass(
                20999, 22002, "s", blockQuic, "VieNLink.exe");
            SingBoxConfigBuilder.WriteToFile(
                Path.Combine(AppPaths.RuntimeDirectory, "tun-bypass-test.json"), bypass);
            try
            {
                await SingBoxRunner.CheckConfigAsync(
                    Path.Combine(AppPaths.RuntimeDirectory, "tun-bypass-test.json"), CancellationToken.None);
                Check(true, "TUN-обход, QUIC " + (blockQuic ? "блокируется" : "напрямую"));
            }
            catch (Exception exception)
            {
                Check(false, "TUN-обход, QUIC " + (blockQuic ? "блокируется" : "напрямую") + " — " + exception.Message);
            }
        }

        Console.WriteLine("Протоколы (конфиг одного узла через sing-box check):");
        var protocolNodes = new List<(string Name, ProxyNode? Node)>
        {
            ("vless + tls + ws", LinkParser.Parse(
                "vless://11111111-2222-3333-4444-555555555555@example.com:443?security=tls&sni=example.com&type=ws&path=%2Fws&host=example.com#A", "t")),
            ("vless + reality + grpc", LinkParser.Parse(
                "vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=ab&fp=chrome&sni=www.microsoft.com&type=grpc&serviceName=grpc#B", "t")),
            ("vmess + ws + tls", LinkParser.Parse("vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                "{\"v\":\"2\",\"ps\":\"VM\",\"add\":\"example.com\",\"port\":\"443\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"aid\":\"0\",\"scy\":\"auto\",\"net\":\"ws\",\"host\":\"example.com\",\"path\":\"/x\",\"tls\":\"tls\",\"sni\":\"example.com\"}")), "t")),
            ("trojan + ws", LinkParser.Parse("trojan://pass@example.com:8443?sni=example.com&type=ws&path=%2Ft#T", "t")),
            ("shadowsocks sip002", LinkParser.Parse("ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:pw")) + "@example.com:8388#S", "t")),
            ("shadowsocks legacy", LinkParser.Parse("ss://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:pw@example.com:8388")) + "#S", "t")),
            ("hysteria2", LinkParser.Parse("hysteria2://pass@example.com:443?sni=example.com&insecure=1#H", "t")),
            ("sing-box JSON", SubscriptionParser.Parse(
                "{\"outbounds\":[{\"type\":\"vless\",\"tag\":\"n\",\"server\":\"1.2.3.4\",\"server_port\":443,\"uuid\":\"11111111-2222-3333-4444-555555555555\",\"tls\":{\"enabled\":true}}]}", "t").FirstOrDefault()),
        };

        foreach (var (name, protocolNode) in protocolNodes)
        {
            if (protocolNode is null)
            {
                Check(false, name + " — ссылка не разобралась");
                continue;
            }

            var json = SingBoxConfigBuilder.Build(new[] { protocolNode }, protocolNode.Tag, 22000, 22001, "s", new AppSettings());
            SingBoxConfigBuilder.WriteToFile(path, json);
            try
            {
                await SingBoxRunner.CheckConfigAsync(path, CancellationToken.None);
                Check(true, name);
            }
            catch (Exception exception)
            {
                Check(false, name + " — " + exception.Message);
            }
        }

        var realityNoSni = LinkParser.Parse(
            "vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#R", "t");
        if (realityNoSni is null)
        {
            Check(false, "reality без sni не разобрался");
        }
        else
        {
            var masked = SingBoxConfigBuilder.Build(
                new[] { realityNoSni }, realityNoSni.Tag, 22000, 22001, "s",
                new AppSettings { DefaultSni = "www.microsoft.com" });
            Check(masked.Contains("\"server_name\": \"www.microsoft.com\"", StringComparison.Ordinal),
                "SNI-маскировка подставлена в Reality без sni");
        }
    }

    /// <summary>
    /// Проверка правил по процессам без прав администратора: правило пропускает только curl.exe,
    /// все остальные процессы получают отказ.
    /// </summary>
    private static async Task RunProcessMatchAsync()
    {
        Console.WriteLine("Правила по процессам (системный прокси, без администратора):");
        var port = FindFreePort(21800);
        var configPath = Path.Combine(AppPaths.RuntimeDirectory, "procmatch-config.json");

        async Task<bool> TryNameAsync(string processName)
        {
            var config = new JsonObject
            {
                ["log"] = new JsonObject { ["level"] = "warn" },
                ["inbounds"] = new JsonArray
                {
                    new JsonObject { ["type"] = "mixed", ["tag"] = "in", ["listen"] = "127.0.0.1", ["listen_port"] = port },
                },
                ["outbounds"] = new JsonArray
                {
                    new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                },
                ["route"] = new JsonObject
                {
                    ["rules"] = new JsonArray
                    {
                        new JsonObject { ["action"] = "sniff" },
                        new JsonObject { ["process_name"] = new JsonArray { processName }, ["action"] = "route", ["outbound"] = "direct" },
                        new JsonObject { ["action"] = "reject" },
                    },
                    ["final"] = "direct",
                },
            };

            SingBoxConfigBuilder.WriteToFile(configPath, config.ToJsonString());
            await SingBoxRunner.CheckConfigAsync(configPath, CancellationToken.None);

            await using var runner = new SingBoxRunner();
            await runner.StartAsync(configPath, port, TimeSpan.FromSeconds(15), CancellationToken.None);
            try
            {
                var curlCode = await CurlAsync($"http://127.0.0.1:{port}", "https://www.gstatic.com/generate_204");
                var selfCode = await CheckSiteAsync(port, "https://www.gstatic.com/generate_204");
                Console.WriteLine($"  process_name={processName}: curl={Describe(curlCode)}, наш процесс={Describe(selfCode)}");
                return curlCode == 204 && selfCode is null;
            }
            finally
            {
                await runner.StopAsync();
            }
        }

        var matched = await TryNameAsync("curl.exe");
        if (!matched)
        {
            matched = await TryNameAsync("curl");
            Console.WriteLine(matched
                ? "  вывод: sing-box сопоставляет имя процесса без .exe — нужно убрать суффикс"
                : "  вывод: правило по процессу не сработало");
        }

        Check(matched, "правило по процессу отличает curl.exe от остальных процессов");
    }

    /// <summary>
    /// Проверка TUN-режима (нужны права администратора): адаптер перехватывает системный трафик,
    /// а правило reject для example.com его блокирует.
    /// </summary>
    private static async Task RunTunAsync()
    {
        Console.WriteLine("TUN-режим:");

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var isAdmin = new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!isAdmin)
        {
            Console.WriteLine("  НЕТ ПРАВ АДМИНИСТРАТОРА — запустите self-test с повышением прав");
            _failed++;
            return;
        }

        await SingBoxCore.EnsureAsync(new Progress<string>(line => Console.WriteLine(line)), CancellationToken.None);
        await SingBoxCore.EnsureWintunAsync(new Progress<string>(line => Console.WriteLine(line)), CancellationToken.None);

        var mixedPort = FindFreePort(21900);
        var apiPort = FindFreePort(mixedPort + 1);
        var configPath = Path.Combine(AppPaths.RuntimeDirectory, "tun-test-config.json");

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "mixed", ["tag"] = "in", ["listen"] = "127.0.0.1", ["listen_port"] = mixedPort },
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = "VieNLinkTest",
                    ["address"] = new JsonArray { "172.19.0.1/30" },
                    ["mtu"] = 1400,
                    ["auto_route"] = true,
                    ["strict_route"] = false,
                    ["dns_mode"] = "hijack",
                    ["stack"] = "gvisor",
                },
            },
            ["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" } },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray { new JsonObject { ["type"] = "local", ["tag"] = "local" } },
                ["final"] = "local",
            },
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                    new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" },
                    new JsonObject
                    {
                        ["process_name"] = new JsonArray { "VieNLink.SelfTest.exe" },
                        ["action"] = "reject",
                    },
                    new JsonObject
                    {
                        ["domain_suffix"] = new JsonArray { "example.com", ".example.com" },
                        ["action"] = "reject",
                    },
                },
                ["final"] = "direct",
                ["auto_detect_interface"] = true,
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = "127.0.0.1:" + apiPort,
                    ["secret"] = "tuntest",
                },
            },
        };

        SingBoxConfigBuilder.WriteToFile(configPath, config.ToJsonString());
        await SingBoxRunner.CheckConfigAsync(configPath, CancellationToken.None);

        await using var runner = new SingBoxRunner();
        try
        {
            await runner.StartAsync(configPath, apiPort, TimeSpan.FromSeconds(25), CancellationToken.None);
            await Task.Delay(2500);

            var adapter = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Contains("VieNLinkTest", StringComparison.OrdinalIgnoreCase) ||
                                     n.Description.Contains("VieNLinkTest", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("  адаптер: " + (adapter?.Description ?? "не найден"));
            Check(adapter is not null, "создан TUN-адаптер");

            var normal = await CurlAsync(proxy: null, "https://www.gstatic.com/generate_204");
            Console.WriteLine("  обычный сайт через TUN: " + Describe(normal));
            Check(normal == 204, "системный трафик проходит через TUN");

            var blocked = await CurlAsync(proxy: null, "https://example.com/");
            Console.WriteLine("  example.com (правило reject): " + Describe(blocked));
            Check(blocked is null, "правило маршрутизации применяется к системному трафику");

            var selfBlocked = await CheckSiteAsync(null, "https://www.gstatic.com/generate_204");
            Console.WriteLine("  наш процесс (правило по процессу): " + Describe(selfBlocked));
            Check(selfBlocked is null, "правило по процессу действует в TUN");
        }
        finally
        {
            await runner.StopAsync();
        }
    }

    private static string Describe(int? code) => code is null ? "отказ" : "HTTP " + code;

    private static async Task<int?> CurlAsync(string? proxy, string url)
    {
        try
        {
            var startInfo = new ProcessStartInfo("curl.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("NUL");
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add("%{http_code}");
            startInfo.ArgumentList.Add("--max-time");
            startInfo.ArgumentList.Add("15");
            if (proxy is not null)
            {
                startInfo.ArgumentList.Add("-x");
                startInfo.ArgumentList.Add(proxy);
            }

            startInfo.ArgumentList.Add(url);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && int.TryParse(output.Trim(), out var code) && code > 0 ? code : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Проверка автопоиска источников на настоящем GitHub (тратит лимит API).</summary>
    private static async Task RunDiscoveryAsync()
    {
        Console.WriteLine("Автопоиск источников на GitHub…");
        var result = await SourceDiscoveryService.DiscoverAsync(
            new List<SourceDefinition>(),
            new Progress<string>(line => Console.WriteLine("  " + line)),
            CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"Репозиториев: {result.RepositoriesSeen}, файлов: {result.FilesChecked}, " +
                          $"источников: {result.Sources.Count}, узлов: {result.Nodes.Count}, ошибка: {result.Error ?? "нет"}");
        foreach (var source in result.Sources)
        {
            Console.WriteLine($"  + {source.Name}: {source.LastNodeCount} узлов");
            Console.WriteLine($"    {source.Url}");
        }

        Check(result.FilesChecked > 0, "файлы кандидатов проверялись");
    }

    /// <summary>Проверка маскировки на реальном кэше: тестирует узлы и подключается к лучшему.</summary>
    /// <summary>
    /// Скрытая проверка: отдельный процесс sing-box на своих портах, без системного прокси.
    /// Берём уже рабочие узлы, чтобы отличить «ядро не работает» от «узлы мертвы».
    /// </summary>
    /// <summary>
    /// Проверка произвольного источника: скачать, разобрать и прогнать выборку
    /// через скрытое ядро. Нужна для оценки новых публичных списков без добавления их в кэш.
    /// </summary>
    private static async Task RunSourceProbeAsync(string url, int limit, bool singleLink = false)
    {
        var source = new SourceDefinition
        {
            Name = "Проверка источника",
            Url = url,
            Kind = singleLink ? SourceKind.SingleLink : SourceKinds.Classify(url),
        };

        Console.WriteLine($"Скачиваю источник: {url}");
        FetchOutcome outcome;
        try
        {
            outcome = await NodeSourceService.FetchAsync(source, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Console.WriteLine("Ошибка загрузки: " + exception.Message);
            _failed++;
            return;
        }

        Console.WriteLine($"Разобрано серверов: {outcome.Nodes.Count}" +
                          (outcome.Error is null ? string.Empty : " · " + outcome.Error));
        if (outcome.Nodes.Count == 0)
        {
            _failed++;
            return;
        }

        var candidates = outcome.Nodes
            .OrderByDescending(NodeRanking.Score)
            .ThenBy(node => node.Server)
            .Take(Math.Clamp(limit, 1, 300))
            .ToList();
        Console.WriteLine($"Проверяю {candidates.Count} из {outcome.Nodes.Count} через скрытое ядро…");

        var settings = new AppSettings
        {
            MaxNodesInConfig = Math.Min(candidates.Count, 400),
            ProxyBypass = "*.deepseek.com;*.opencode.ai;*.anthropic.com;*.openai.com;localhost;127.0.0.1;::1",
        };
        await using var probe = new HiddenProbeService();
        var summary = await probe.TestAsync(
            candidates,
            settings,
            new Progress<string>(line => Console.WriteLine("    " + line)),
            null,
            CancellationToken.None,
            concurrency: 3,
            timeoutMs: 4000);

        var working = candidates.Count(node => node.Status == NodeStatus.Working);
        Console.WriteLine($"Рабочих: {working} из {candidates.Count} (всего в источнике {outcome.Nodes.Count})");
        foreach (var node in candidates
                     .Where(node => node.Status == NodeStatus.Working)
                     .OrderByDescending(node => node.OpenDomains)
                     .ThenBy(node => node.DelayMs)
                     .Take(5))
        {
            Console.WriteLine($"  {node.Name} — {node.DelayMs} мс, доменов {node.OpenDomains}, " +
                              $"YouTube={node.YoutubeOk}, Telegram={node.TelegramOk}, Discord={node.DiscordOk}");
        }

        if (working == 0)
        {
            _failed++;
        }
    }

    private static async Task RunHiddenProbeAsync(int limit)
    {
        var cached = NodeSourceService.LoadCache();
        var candidates = cached
            .Where(node => node.Status == NodeStatus.Working && node.DelayMs is not null)
            .OrderByDescending(node => node.OpenDomains)
            .ThenBy(node => node.DelayMs)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine("В кэше нет рабочих узлов — сначала проверьте узлы в приложении.");
            _failed++;
            return;
        }

        Console.WriteLine($"Скрытая проверка {candidates.Count} рабочих узлов на отдельных портах…");
        var settings = new AppSettings();
        await using var probe = new HiddenProbeService();
        var summary = await probe.TestAsync(
            candidates,
            settings,
            new Progress<string>(line => Console.WriteLine("    " + line)),
            null,
            CancellationToken.None,
            concurrency: 3,
            timeoutMs: 4000);

        Console.WriteLine($"Скрытое ядро: рабочих {summary.Working} из {summary.Total}, " +
                          $"лучший открывает доменов {summary.BestDomains}");
        var best = candidates
            .Where(node => node.DelayMs is not null)
            .OrderByDescending(node => node.OpenDomains)
            .ThenBy(node => node.DelayMs)
            .FirstOrDefault();
        if (best is null || summary.Working == 0)
        {
            Console.WriteLine("Скрытая проверка не открыла ни одного узла.");
            _failed++;
            return;
        }

        Console.WriteLine($"Лучший: {best.Name} — {best.DelayMs} мс, доменов {best.OpenDomains}, " +
                          $"YouTube={best.YoutubeOk}, Telegram={best.TelegramOk}, Discord={best.DiscordOk}");
    }

    private static async Task RunProbeAsync(int limit)
    {
        var cached = NodeSourceService.LoadCache();
        if (cached.Count == 0)
        {
            Console.WriteLine("Кэш узлов пуст — сначала обновите списки в приложении.");
            _failed++;
            return;
        }

        // Сначала уже проверенные, затем самые маскированные и быстрые.
        var candidates = cached
            .OrderByDescending(n => n.Status == NodeStatus.Working)
            .ThenByDescending(NodeRanking.Score)
            .ThenBy(n => n.DelayMs ?? int.MaxValue)
            .Take(limit)
            .ToList();

        var masked = candidates.Count(NodeRanking.IsMasked);
        Console.WriteLine($"Пробую {candidates.Count} узлов из кэша (маскированных {masked})…");

        var settings = new AppSettings
        {
            MaxNodesInConfig = Math.Min(candidates.Count, 400),
            ProxyBypass = "*.deepseek.com;*.opencode.ai;*.anthropic.com;*.openai.com;localhost;127.0.0.1;::1",
        };
        await using var manager = new ProxyManager(settings);
        var summary = await manager.TestNodesAsync(
            candidates,
            new Progress<string>(line => Console.WriteLine("    " + line)),
            null,
            CancellationToken.None,
            timeoutMs: 4000);
        Console.WriteLine($"Рабочих {summary.Working} из {summary.Total}");

        var working = candidates.Where(n => n.DelayMs is not null).ToList();
        var maskedTotal = candidates.Count(NodeRanking.IsMasked);
        var maskedWorking = working.Count(NodeRanking.IsMasked);
        Console.WriteLine($"Маскированные: рабочих {maskedWorking} из {maskedTotal}; " +
                          $"без явной маскировки: {working.Count - maskedWorking} из {candidates.Count - maskedTotal}");

        var best = candidates
            .Where(n => n.DelayMs is not null)
            .OrderByDescending(n => n.TelegramOk == true)
            .ThenByDescending(NodeRanking.Score)
            .ThenBy(n => n.DelayMs)
            .FirstOrDefault();
        if (best is null)
        {
            Console.WriteLine("Рабочих узлов нет.");
            _failed++;
            return;
        }

        Console.WriteLine($"Лучший: {best.Name} — {NodeRanking.Label(best)}, камуфляж {NodeRanking.CamouflageLabel(best)}, " +
                          $"{best.DelayMs} мс, YouTube={best.YoutubeOk}, Telegram={best.TelegramOk}");

        await manager.ConnectAsync(best, null, CancellationToken.None);
        var youtube = await CheckSiteAsync(manager.ActiveMixedPort, "https://www.youtube.com/");
        var telegram = await CheckSiteAsync(manager.ActiveMixedPort, "https://t.me/");
        Console.WriteLine($"Через узел: YouTube {(youtube is null ? "не открылся" : "HTTP " + youtube)}, " +
                          $"t.me {(telegram is null ? "не открылся" : "HTTP " + telegram)}");
        Check(youtube is not null, "узел открывает YouTube");
        Check(telegram is not null, "узел открывает t.me");

        await manager.DisconnectAsync();
    }

    /// <summary>
    /// Сравнение маскированных и обычных узлов: пары «один сервер:порт, два варианта»
    /// и равные случайные выборки. Показывает, где маскировка реально помогла.
    /// </summary>
    private static async Task RunCompareAsync(int samplePerClass)
    {
        var nodes = NodeSourceService.LoadCache();
        if (nodes.Count == 0)
        {
            Console.WriteLine("Кэш пуст — сначала обновите списки в приложении.");
            _failed++;
            return;
        }

        var settings = new AppSettings
        {
            MaxNodesInConfig = 600,
            ProxyBypass = "*.deepseek.com;*.opencode.ai;*.anthropic.com;*.openai.com;localhost;127.0.0.1;::1",
        };
        await using var manager = new ProxyManager(settings);
        var progress = new Progress<string>(line => Console.WriteLine("    " + line));

        // 1. Один и тот же сервер:порт, но маскированный и обычный варианты.
        var pairs = nodes
            .GroupBy(n => n.Server.ToLowerInvariant() + ":" + n.Port, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(NodeRanking.IsMasked) && g.Any(n => !NodeRanking.IsMasked(n)))
            .ToList();

        Console.WriteLine($"Пар «сервер:порт с маскированным и обычным вариантом»: {pairs.Count}");
        var testedPairs = pairs.Take(40).ToList();
        var pairNodes = testedPairs.SelectMany(g => g).GroupBy(n => n.Id).Select(g => g.First()).Take(240).ToList();
        if (pairNodes.Count > 0)
        {
            Console.WriteLine($"Проверяю {pairNodes.Count} узлов из {testedPairs.Count} пар…");
            await manager.TestNodesAsync(pairNodes, progress, null, CancellationToken.None, timeoutMs: 4000);

            var maskedOnly = 0;
            var plainOnly = 0;
            var both = 0;
            var none = 0;
            foreach (var group in testedPairs)
            {
                var maskedOk = group.Where(NodeRanking.IsMasked).Any(n => n.DelayMs is not null);
                var plainOk = group.Where(n => !NodeRanking.IsMasked(n)).Any(n => n.DelayMs is not null);
                if (maskedOk && plainOk)
                {
                    both++;
                }
                else if (maskedOk)
                {
                    maskedOnly++;
                    var node = group.First(n => NodeRanking.IsMasked(n) && n.DelayMs is not null);
                    Console.WriteLine($"  ТОЛЬКО маскированный: {group.Key} · {NodeRanking.Label(node)} · {NodeRanking.CamouflageLabel(node)} · {node.DelayMs} мс");
                }
                else if (plainOk)
                {
                    plainOnly++;
                    var node = group.First(n => !NodeRanking.IsMasked(n) && n.DelayMs is not null);
                    Console.WriteLine($"  ТОЛЬКО обычный: {group.Key} · {NodeRanking.Label(node)} · {node.DelayMs} мс");
                }
                else
                {
                    none++;
                }
            }

            Console.WriteLine($"  Итог по парам: работают оба {both}, только маскированный {maskedOnly}, " +
                              $"только обычный {plainOnly}, оба мертвы {none}");
            if (both == 0 && maskedOnly == 0 && plainOnly == 0)
            {
                Console.WriteLine("  Вывод: ни одна пара не ответила — для оценки маскировки нужны живые узлы.");
            }
            else if (maskedOnly > plainOnly)
            {
                Console.WriteLine($"  Вывод: маскировка помогла на {maskedOnly} парах, обычный вариант — на {plainOnly}.");
            }
            else if (plainOnly > maskedOnly)
            {
                Console.WriteLine($"  Вывод: в этот раз обычный вариант работал чаще ({plainOnly} против {maskedOnly}).");
            }
        }

        // 2. Равные случайные выборки из ещё не проверенных узлов.
        var pool = nodes.ToList();
        var random = new Random(20261119);
        var maskedSample = pool.Where(NodeRanking.IsMasked).OrderBy(_ => random.Next()).Take(samplePerClass).ToList();
        var plainSample = pool.Where(n => !NodeRanking.IsMasked(n)).OrderBy(_ => random.Next()).Take(samplePerClass).ToList();
        var sample = maskedSample.Concat(plainSample).ToList();

        Console.WriteLine();
        Console.WriteLine($"Выборка: маскированных {maskedSample.Count}, обычных {plainSample.Count}");
        if (sample.Count > 0)
        {
            await manager.TestNodesAsync(sample, progress, null, CancellationToken.None, timeoutMs: 4000);
            PrintClassStats("маскированные", maskedSample);
            PrintClassStats("обычные", plainSample);
            var maskedWorking = maskedSample.Count(n => n.DelayMs is not null);
            var plainWorking = plainSample.Count(n => n.DelayMs is not null);
            if (maskedWorking + plainWorking == 0)
            {
                Console.WriteLine("  Вывод: живых узлов нет — сравнить пользу маскировки невозможно.");
            }
            else if (maskedWorking > plainWorking)
            {
                Console.WriteLine($"  Вывод: маскированные работают чаще ({maskedWorking} против {plainWorking}).");
            }
            else if (plainWorking > maskedWorking)
            {
                Console.WriteLine($"  Вывод: обычные работают чаще ({plainWorking} против {maskedWorking}).");
            }
            else
            {
                Console.WriteLine($"  Вывод: результат одинаковый ({maskedWorking} против {plainWorking}).");
            }
        }

        await manager.DisconnectAsync();
    }

    private static void PrintClassStats(string name, List<ProxyNode> nodes)
    {
        var working = nodes.Where(n => n.DelayMs is not null).ToList();
        var youtube = working.Count(n => n.YoutubeOk == true);
        var telegram = working.Count(n => n.TelegramOk == true);
        var average = working.Count > 0 ? (int)working.Average(n => n.DelayMs!.Value) : 0;
        Console.WriteLine($"  {name}: рабочих {working.Count}/{nodes.Count} ({Percent(working.Count, nodes.Count)}), " +
                          $"YouTube {youtube}, Telegram {telegram}, средняя {average} мс");
    }

    /// <summary>Проверка установки zapret: файлы, аргументы, при желании запуск (нужен админ).</summary>
    private static async Task RunZapretAsync(string mode)
    {
        Console.WriteLine("zapret (winws + WinDivert):");
        Console.WriteLine($"  каталог: {ZapretService.Directory}");
        if (mode == "install")
        {
            await ZapretService.EnsureAsync(
                new Progress<string>(line => Console.WriteLine("  " + line)),
                CancellationToken.None);
        }

        foreach (var file in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" })
        {
            var path = Path.Combine(ZapretService.Directory, file);
            Console.WriteLine($"  {file}: {(File.Exists(path) ? new FileInfo(path).Length + " байт" : "нет")}");
        }

        Console.WriteLine($"  установлен: {(ZapretService.IsInstalled ? "да" : "нет")}");
        Console.WriteLine("  аргументы: " + string.Join(" ", ZapretService.BuildArguments()));

        if (mode == "start")
        {
            using var zapret = new ZapretService();
            try
            {
                await zapret.StartAsync(
                    new Progress<string>(line => Console.WriteLine("  " + line)),
                    CancellationToken.None);
                Console.WriteLine("  запущен: да");
            }
            catch (Exception exception)
            {
                Console.WriteLine("  запустить не удалось: " + exception.Message);
            }
        }
    }

    /// <summary>Что лежит в кэше приложения: узлы по статусам, маскировка, MTProto и источники.</summary>
    private static void RunCacheStats()
    {
        var settings = new AppSettings();
        var freshAfter = DateTime.UtcNow.AddDays(-settings.KeepUnseenNodeDays);
        var nodes = NodeSourceService.LoadCache(out var droppedNodes, out var duplicateNodes, freshAfter);
        var sources = NodeSourceService.LoadSources();
        var mtproto = MtProtoService.LoadCache();

        Console.WriteLine("Кэш узлов:");
        Console.WriteLine($"  всего: {nodes.Count} (устаревших выброшено: {droppedNodes}, повторов склеено: {duplicateNodes})");
        foreach (var group in nodes.GroupBy(n => n.Status).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {StatusName(group.Key)}: {group.Count()}");
        }

        var tested = nodes.Where(n => n.DelayMs is not null).ToList();
        var masked = nodes.Where(NodeRanking.IsMasked).ToList();
        Console.WriteLine($"  с задержкой: {tested.Count}, из них маскированных: {tested.Count(NodeRanking.IsMasked)}");
        Console.WriteLine($"  маскированных всего: {masked.Count} ({Percent(masked.Count, nodes.Count)})");
        foreach (var group in masked.GroupBy(NodeRanking.Family).OrderByDescending(g => g.Count()).Take(12))
        {
            Console.WriteLine($"    {group.Key}: {group.Count()}");
        }

        if (masked.Select(NodeRanking.Family).Distinct().Count() > 12)
        {
            Console.WriteLine("    …остальные семейства не показаны");
        }

        Console.WriteLine("  Быстрейшие рабочие:");
        foreach (var node in nodes
                     .Where(n => n.Status == NodeStatus.Working && n.DelayMs is not null)
                     .OrderBy(n => n.DelayMs)
                     .Take(10))
        {
            Console.WriteLine($"    {node.DelayMs,5} мс · {NodeRanking.Label(node)} · {NodeRanking.CamouflageLabel(node)} · {ShortenText(node.Name, 46)}");
        }

        Console.WriteLine("  Источники узлов (топ-10 по количеству):");
        foreach (var group in nodes
                     .GroupBy(n => n.SourceName.Length > 0 ? n.SourceName : "—")
                     .OrderByDescending(g => g.Count())
                     .Take(10))
        {
            Console.WriteLine($"    {group.Count(),6} · {ShortenText(group.Key, 70)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Источники: {sources.Count}, включено {sources.Count(s => s.Enabled)}, " +
                          $"Telegram {sources.Count(s => s.Kind == SourceKind.Telegram)}");

        Console.WriteLine();
        Console.WriteLine($"MTProto: {mtproto.Count}");
        foreach (var group in mtproto.GroupBy(p => p.Status))
        {
            Console.WriteLine($"  {MtProtoStatusName(group.Key)}: {group.Count()}");
        }

        Console.WriteLine($"  с задержкой: {mtproto.Count(p => p.DelayMs is not null)}");

        Console.WriteLine();
        DescribeFile("узлы", AppPaths.NodesPath);
        DescribeFile("MTProto", AppPaths.MtProtoPath);
        DescribeFile("источники (зашифрованы)", AppPaths.SourcesProtectedPath);
        DescribeFile("источники (старый открытый)", AppPaths.SourcesPath);
    }

    /// <summary>Список источников: показывает, что прочиталось из зашифрованного sources.dat.</summary>
    private static void RunSourceStats()
    {
        var sources = NodeSourceService.LoadSources();
        var working = NodeSourceService.LoadCache(
            out _, out _, DateTime.UtcNow.AddDays(-30), AppPaths.WorkingNodesPath);
        var current = NodeSourceStats.WorkingBySource(working);

        Console.WriteLine($"Источников: {sources.Count}, рабочих серверов в кэше: {working.Count}");
        Console.WriteLine("Имя | Всего | Рабочих всего | Рабочих сейчас | Ошибка");
        foreach (var item in sources
                     .Select(source => new
                     {
                         Source = source,
                         Current = current.TryGetValue(source.Name, out var count) ? count : 0,
                     })
                     .OrderByDescending(item => item.Current)
                     .ThenByDescending(item => item.Source.TotalWorkingNodeCount)
                     .ThenByDescending(item => item.Source.TotalNodeCount))
        {
            var source = item.Source;
            Console.WriteLine(
                $"{ShortenText(source.Name, 52)} | {source.TotalNodeCount} | " +
                $"{source.TotalWorkingNodeCount} | {item.Current} | {source.LastError ?? string.Empty}");
        }
    }

    private static void RunSourcesList()
    {
        var sources = NodeSourceService.LoadSources();
        Console.WriteLine($"Источников: {sources.Count} (хранятся в {AppPaths.SourcesProtectedPath})");
        foreach (var source in sources)
        {
            Console.WriteLine($"  {(source.Enabled ? "+" : "-")} {(source.IsMine ? "★ " : string.Empty)}{source.Name}");
            Console.WriteLine("      " + source.Url.ReplaceLineEndings(" "));
        }
    }

    /// <summary>
    /// Чистка списка источников: убирает проверенные, но пустые и бесполезные источники
    /// серверов. MTProto/Telegram-источники не трогаются — у них своя задача.
    /// Без --apply только показывает решение.
    /// </summary>
    private static void RunPruneSources(bool apply)
    {
        var sources = NodeSourceService.LoadSources();
        var pool = NodeSourceService.LoadCache(out _, out _);
        var working = NodeSourceService.LoadCache(
            out _, out _, DateTime.UtcNow.AddDays(-30), AppPaths.WorkingNodesPath);
        var all = pool.Concat(working).ToList();
        var remove = new List<SourceDefinition>();

        Console.WriteLine($"Источников: {sources.Count}, серверов в кэше: {all.Count}");
        Console.WriteLine("Имя | Проверен | Последних | Всего | Рабочих | Решение");
        foreach (var source in sources.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (SourceKinds.IsProxy(source))
            {
                Console.WriteLine($"{ShortenText(source.Name, 40)} | MTProto — не трогаем");
                continue;
            }

            var key = SourceKey(source.Name);
            var total = 0;
            var currentWorking = 0;
            foreach (var node in all)
            {
                if (!NodeFailover.SplitSources(node.SourceName)
                        .Any(name => name.Contains(key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                total++;
                if (node.Status == NodeStatus.Working)
                {
                    currentWorking++;
                }
            }

            var checkedNow = source.LastCheckedUtc is not null;
            var drop = checkedNow && (source.LastNodeCount == 0 || (total < 10 && currentWorking == 0));
            if (drop)
            {
                remove.Add(source);
            }

            var checkedLabel = source.LastCheckedUtc is { } at
                ? at.ToLocalTime().ToString("dd.MM HH:mm")
                : "—";
            Console.WriteLine(
                $"{ShortenText(source.Name, 40)} | {checkedLabel} | {source.LastNodeCount} | " +
                $"{total} | {currentWorking} | {(drop ? "УДАЛИТЬ" : "оставить")}");
        }

        Console.WriteLine($"К удалению: {remove.Count}");
        if (!apply)
        {
            Console.WriteLine("Это предварительный просмотр. Для применения: prunesources --apply");
            return;
        }

        if (remove.Count == 0)
        {
            return;
        }

        foreach (var source in remove)
        {
            sources.RemoveAll(item => string.Equals(item.Id, source.Id, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("Удалён: " + source.Name);
        }

        NodeSourceService.SaveSources(sources);
        Console.WriteLine($"Сохранено, источников осталось: {sources.Count}");
    }

    /// <summary>Ключ для сопоставления с SourceName: первое слово имени источника.</summary>
    private static string SourceKey(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    private static string StatusName(NodeStatus status) => status switch
    {
        NodeStatus.Unknown => "не проверены",
        NodeStatus.Working => "рабочие",
        NodeStatus.Dead => "нерабочие (удаляются)",
        _ => status.ToString(),
    };

    private static string MtProtoStatusName(MtProtoStatus status) => status switch
    {
        MtProtoStatus.Unknown => "не проверены",
        MtProtoStatus.Working => "рабочие",
        MtProtoStatus.Dead => "нерабочие",
        _ => status.ToString(),
    };

    private static string Percent(int part, int total) =>
        total == 0 ? "0%" : (part * 100.0 / total).ToString("n1") + "%";

    private static string ShortenText(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "…";

    private static void DescribeFile(string name, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"{name}: файла нет");
            return;
        }

        var info = new FileInfo(path);
        Console.WriteLine($"{name}: {info.Length / 1024.0:n1} КБ, изменён {info.LastWriteTime:dd.MM HH:mm}");
    }

    /// <summary>Проверка загрузки MTProto: t.me напрямую, затем через RSS-зеркала.</summary>
    private static async Task RunMtProtoFetchAsync(string url)
    {
        Console.WriteLine("Загрузка MTProto: " + url);
        var source = new SourceDefinition { Name = "Тестовый канал", Url = url, Kind = SourceKind.Telegram };
        var proxies = await MtProtoService.FetchAsync(source, CancellationToken.None);
        Console.WriteLine($"  прокси: {proxies.Count}");
        foreach (var proxy in proxies.Take(10))
        {
            Console.WriteLine($"  · {proxy.Server}:{proxy.Port}, секрет {proxy.SecretShort}");
        }

        Check(proxies.Count > 0, "MTProto-прокси получены");
    }

    /// <summary>
    /// Живая проверка MTProto: настоящий FakeTLS-хендшейк и запрос к Telegram, а не TCP-коннект.
    /// Показывает, какие прокси из кэша действительно годятся для Telegram.
    /// </summary>
    private static async Task RunMtProtoCheckAsync(int limit, string? filter)
    {
        var all = MtProtoService.LoadCache();
        var candidates = all
            .Where(proxy => filter is null || proxy.Server.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(proxy => MtProtoSecret.TryParse(proxy.Secret) is not null)
            .OrderByDescending(proxy => proxy.Status == MtProtoStatus.Working)
            .ThenBy(proxy => proxy.DelayMs ?? int.MaxValue)
            .Take(Math.Max(1, limit))
            .ToList();

        Console.WriteLine($"Проверка MTProto настоящим хендшейком: {candidates.Count} из {all.Count} в кэше");
        var working = 0;
        var repaired = 0;
        var lost = 0;
        var consoleLock = new object();
        using var limiter = new SemaphoreSlim(8);
        var tasks = candidates.Select(async proxy =>
        {
            await limiter.WaitAsync().ConfigureAwait(false);
            try
            {
                var probe = await MtProtoProbe.CheckAsync(
                        proxy.Server, proxy.Port, proxy.Secret, MtProtoProbe.DefaultTimeoutMs, CancellationToken.None)
                    .ConfigureAwait(false);
                var delay = probe.DelayMs;
                var secret = MtProtoSecret.TryParse(proxy.Secret);
                lock (consoleLock)
                {
                    if (probe.Alive)
                    {
                        working++;
                    }

                    if (probe.Alive && proxy.Status != MtProtoStatus.Working)
                    {
                        repaired++;
                    }

                    if (!probe.Alive && proxy.Status == MtProtoStatus.Working)
                    {
                        lost++;
                    }

                    var kind = secret?.Kind switch
                    {
                        MtProtoSecretKind.FakeTls => "FakeTLS/" + secret.Host,
                        MtProtoSecretKind.PaddedIntermediate => "dd",
                        _ => "plain",
                    };
                    Console.WriteLine($"  {(probe.Alive ? "ok  " : "НЕТ ")} {proxy.Server}:{proxy.Port} " +
                                      $"{delay?.ToString() ?? "—"} мс (рукопожатие {probe.HandshakeMs} мс), {kind}, " +
                                      $"было: {MtProtoStatusName(proxy.Status)}");
                }
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks);
        Console.WriteLine($"Итого: работают {working} из {candidates.Count}; " +
                          $"ожили {repaired}, оказались мёртвыми {lost}");
    }

    /// <summary>Проверка одного прокси по адресу и секрету.</summary>
    /// <summary>Живой сбор MTProto с сайтов: проверяет, что веб-списки больше не игнорируются.</summary>
    private static async Task RunMtProtoSitesAsync()
    {
        var sources = NodeSourceService.DefaultSources()
            .Where(source => source.Kind == SourceKind.MtProto)
            .ToList();
        Console.WriteLine($"Сайты MTProto: {sources.Count}");
        var total = 0;
        foreach (var source in sources)
        {
            try
            {
                var proxies = await MtProtoService.FetchAsync(source, CancellationToken.None);
                total += proxies.Count;
                Console.WriteLine("  " + source.Name + ": " + proxies.Count +
                                  (proxies.Count > 0 ? " (" + proxies[0].Server + ")" : string.Empty));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.WriteLine("  " + source.Name + ": ошибка — " + exception.Message);
            }
        }

        Console.WriteLine($"ИТОГО с сайтов: {total}");
        Check(total > 0, "хотя бы один сайт MTProto дал прокси");
    }

    private static async Task RunMtProtoHostAsync(string server, int port, string secret)
    {
        var parsed = MtProtoSecret.TryParse(secret);
        Console.WriteLine($"Прокси {server}:{port}");
        Console.WriteLine(parsed is null
            ? "  секрет: не разбирается (нет ключа или испорчен домен)"
            : $"  секрет: {parsed.Kind}" + (parsed.Host.Length > 0 ? $", домен {parsed.Host}" : string.Empty));

        var probe = await MtProtoProbe.CheckAsync(
            server, port, secret, MtProtoProbe.DefaultTimeoutMs, CancellationToken.None,
            message => Console.WriteLine("  · " + message));
        Console.WriteLine(probe.Alive
            ? $"  результат: работает, пинг {probe.DelayMs} мс (рукопожатие {probe.HandshakeMs} мс)"
            : "  результат: Telegram через этот прокси не ответил" +
              (probe.TcpConnected ? " (TCP есть, рукопожатие не прошло)" : string.Empty));
    }

    /// <summary>Проверка загрузки одной подписки: формат, узлы, остаток трафика.</summary>
    private static async Task RunFetchAsync(string url)
    {
        Console.WriteLine("Загрузка подписки: " + url);
        var source = new SourceDefinition { Name = "Тестовая подписка", Url = url };
        var outcome = await NodeSourceService.FetchAsync(source, CancellationToken.None);

        Console.WriteLine($"  узлов: {outcome.Nodes.Count}, трафик: {source.SubscriptionInfo ?? "—"}");
        if (outcome.Nodes.Count == 0)
        {
            Console.WriteLine("  причина: " + (outcome.Error ?? "узлы не найдены"));
            return;
        }

        Check(outcome.Nodes.Count >= 1, $"узлов разобрано: {outcome.Nodes.Count}");
        Check(!string.IsNullOrWhiteSpace(source.SubscriptionInfo), "трафик из заголовка: " + (source.SubscriptionInfo ?? "нет"));

        foreach (var node in outcome.Nodes.Take(5))
        {
            Console.WriteLine($"  · {node.Name} — {node.Protocol}, {node.Server}:{node.Port}");
        }

        var config = SingBoxConfigBuilder.Build(outcome.Nodes, outcome.Nodes.FirstOrDefault()?.Tag, 21700, 21701, "test", new AppSettings());
        Check(config.Contains("\"proxies\"", StringComparison.Ordinal) == false && config.Contains("\"outbounds\"", StringComparison.Ordinal),
            "конфиг из узлов подписки собирается");
    }

    private static string DescribeSystemProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = key?.GetValue("ProxyEnable")?.ToString() ?? "?";
            var server = key?.GetValue("ProxyServer")?.ToString() ?? "—";
            return $"ProxyEnable={enabled}, ProxyServer={server}";
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return "нет доступа к реестру";
        }
    }

    private static async Task<int?> CheckSiteAsync(int? port, string url)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
        };

        if (port is not null)
        {
            handler.Proxy = new WebProxy($"http://127.0.0.1:{port}");
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return (int)response.StatusCode;
        }
        catch (Exception exception)
        {
            Console.WriteLine("  ошибка: " + exception.Message);
            return null;
        }
    }
}


